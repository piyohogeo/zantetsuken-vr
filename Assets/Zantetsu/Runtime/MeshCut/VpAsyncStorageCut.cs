using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut
{
    /// <summary>How far one asynchronous cut has come.</summary>
    public enum VpStorageCutStage
    {
        /// <summary>Accepted, and waiting to be offered to the dispatcher. Nothing of the storage is held yet.</summary>
        Waiting = 0,

        /// <summary>
        /// Its sizes are settled and it is waiting for the room to run: the storage's one cut reservation, and a place
        /// in the dispatcher's queue. A cut that waits here is not offered to the dispatcher at all. A cut whose
        /// reservation turned out to be short comes back here with a larger one to ask for.
        /// </summary>
        Ready = 1,

        /// <summary>Its reservation is open and the cut itself is with the dispatcher.</summary>
        Cutting = 2,

        /// <summary>Over, with a result: the two sides on success, the reason on every other status.</summary>
        Finished = 3,

        /// <summary>Given up by the caller, or cancelled with the dispatcher. Everything it held has gone back.</summary>
        Abandoned = 4,
    }

    /// <summary>
    /// One cut asked for through <see cref="VpAsyncStorageCut"/>: the caller's handle on it while it runs and the
    /// result when it is over. Everything the cut needs while it runs — the input adapter and its read lease, the
    /// scratch, the output reservation and the small arrays the kernel writes its ranges into — is held here from the
    /// moment it is accepted until it ends, and given back exactly once at that moment.
    /// <para>
    /// The handle is read on the main thread. <see cref="Result"/> means nothing before <see cref="Stage"/> is
    /// <see cref="VpStorageCutStage.Finished"/>, and a finished cut keeps its result for as long as the caller keeps
    /// the handle: nothing of the runner's touches it again. The geometries a successful cut produced are the
    /// caller's, on exactly the terms of the synchronous entry (<see cref="VpStorageCutSide"/>) — produced, borrowed
    /// or empty.
    /// </para>
    /// </summary>
    public sealed unsafe class VpStorageCutRequest
    {
        internal VpStorageCutInput input;
        internal float4 plane;
        internal VpStorageCutOptions options;
        internal int rangeCount;

        internal NativeArray<MeshCutIndexRange> outputRanges;
        internal VpGeometrySubmesh[] submeshes;
        internal NativeArray<byte> scratch;

        internal MeshCutInput kernelInput;
        internal MeshCutOutput output;
        internal MeshCutResult kernelResult;

        internal int newVertexCapacity, newIndexCapacity, scratchBytes;
        internal VpCutOutputReservation reservation;
        internal WorkTicket ticket;
        internal bool abandoning;

        /// <summary>The thread each part ran on, kept for the tests that check where the work really happened.</summary>
        internal int cutThreadId, collectThreadId;

        internal VpStorageCutRequest(VpStorageCutInput input, float4 plane, in VpStorageCutOptions options)
        {
            this.input = input;
            this.plane = plane;
            this.options = options;
            Parent = input.Geometry;
            rangeCount = input.RangeCount;
            Result = new VpStorageCutResult { status = VpStorageCutStatus.InvalidInput };
        }

        /// <summary>The geometry being cut.</summary>
        public VpStoredGeometry Parent { get; }

        /// <summary>How far it has come.</summary>
        public VpStorageCutStage Stage { get; internal set; } = VpStorageCutStage.Waiting;

        /// <summary>Whether it is over, either way.</summary>
        public bool IsOver => Stage == VpStorageCutStage.Finished || Stage == VpStorageCutStage.Abandoned;

        /// <summary>The result, once <see cref="Stage"/> is <see cref="VpStorageCutStage.Finished"/>.</summary>
        public VpStorageCutResult Result { get; internal set; }

        /// <summary>What a worker threw, when one did; null otherwise.</summary>
        public Exception Failure { get; internal set; }

        /// <summary>How many kernel runs of the cut itself it has taken, re-runs after a short reservation included.</summary>
        public int Attempts { get; internal set; }

        /// <summary>Whether the storage's one cut reservation is held by this cut right now.</summary>
        public bool HoldsReservation => reservation != null && !reservation.IsClosed;

        /// <summary>
        /// The room this cut holds, or null when it holds none. Several cuts of one storage hold room at
        /// once, so which room a cut has is part of what it is: a cut that reserved again is the same cut with
        /// a different one, and a cut beside it keeps the very object it had.
        /// </summary>
        public VpCutOutputReservation Reservation => reservation;
    }

    /// <summary>
    /// Runs the cut kernel over a geometry a <see cref="VpCpuGeometryStorage"/> owns without occupying the main thread
    /// with it: the same kernel, the same storage and the same rules as <see cref="VpStorageCut"/>, taken apart into
    /// what a worker may do and what only the main thread may do (DESIGN 4.3, 4.5.6).
    /// <para>
    /// **The division.** Reading the geometry's shape is work: the capacity query walks every triangle, so it belongs
    /// on a worker with the cut itself. Holding the input's read lease, taking and giving back the output reservation
    /// and turning a finished run into stored geometries belong to the main thread, because the storage is the main
    /// thread's. One cut therefore goes: offered → capacity query on a worker → reservation on the main thread → the
    /// cut on a worker → the two sides published on the main thread. A reservation that turns out to be too small is
    /// given back and a larger one taken, and the cut runs again, by the same rule the synchronous entry uses.
    /// </para>
    /// <para>
    /// **What it does not do.** It publishes nothing beyond the storage's own commit: no ledger, no transfer to the
    /// GPU, no display. The geometries it produces are handed to the caller, who decides what becomes of them. It
    /// owns no dispatcher and drives no frame — the caller brings the dispatcher it already has, opens the frames and
    /// calls <see cref="Pump"/> once each time it is willing to let cuts move, on the main thread.
    /// </para>
    /// <para>
    /// **Several cuts at once.** A storage gives each cut spans of its own, so more than one may be in its
    /// reserved-and-running stretch and their kernels may run together. A cut the storage cannot give room to right
    /// now is not refused and does not wait on a lock: it stays in <see cref="VpStorageCutStage.Ready"/>, unoffered,
    /// and is offered when room comes free. What such a cut waits for is room, not a turn.
    /// </para>
    /// </summary>
    public sealed unsafe class VpAsyncStorageCut : IDisposable
    {
        private readonly VpCpuGeometryStorage _storage;
        private readonly SharedWorkDispatcher _dispatcher;
        private readonly WorkPurpose _purpose;
        private readonly List<VpStorageCutRequest> _requests = new List<VpStorageCutRequest>();
        private readonly Dictionary<VpStorageCutRequest, Work> _work = new Dictionary<VpStorageCutRequest, Work>();
        private bool _closed;

        /// <summary>
        /// Takes the storage whose geometries are cut and the dispatcher the cuts are run through. The purpose decides
        /// the destination: <see cref="WorkPurpose.AdmittedGeometry"/>, the geometry pool, is what an admitted cut is.
        /// </summary>
        public VpAsyncStorageCut(
            VpCpuGeometryStorage storage,
            SharedWorkDispatcher dispatcher,
            WorkPurpose purpose = WorkPurpose.AdmittedGeometry)
        {
            if (storage == null)
            {
                throw new ArgumentNullException(nameof(storage));
            }

            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            if (!WorkPurposes.IsDefined(purpose))
            {
                throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "not a defined work purpose");
            }

            _storage = storage;
            _dispatcher = dispatcher;
            _purpose = purpose;
        }

        /// <summary>How many cuts are neither finished nor abandoned.</summary>
        public int ActiveCount => _requests.Count;

        /// <summary>Whether some cut of this runner's other than <paramref name="request"/> is holding room.</summary>
        private bool HoldsRoomOtherThan(VpStorageCutRequest request)
        {
            for (int i = 0; i < _requests.Count; i++)
            {
                if (_requests[i] != request && _requests[i].HoldsReservation)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>How many of this runner's cuts hold an output reservation right now.</summary>
        public int ReservingCount
        {
            get
            {
                int held = 0;
                for (int i = 0; i < _requests.Count; i++)
                {
                    held += _requests[i].HoldsReservation ? 1 : 0;
                }

                return held;
            }
        }

        /// <summary>
        /// Accepts one cut of <paramref name="input"/>'s geometry by <paramref name="plane"/> and gives back the handle
        /// to follow it by. **The adapter becomes this runner's**: it is held, with its read lease, until the cut is
        /// over or abandoned, and disposed then. Nothing of the storage is taken here, and nothing runs until
        /// <see cref="Pump"/> and the dispatcher have had their turn.
        /// <para>
        /// The options are the synchronous entry's, with one difference: <see cref="VpStorageCutOptions.maxAttempts"/>
        /// is that entry's pacing within one call and is not used here. A reservation that proves too small is simply
        /// taken again, larger, at a later opportunity, so a cut is never given up on for the number of tries — only
        /// for a requirement that does not grow, one too large to express, or a storage that cannot hold it.
        /// </para>
        /// </summary>
        public VpStorageCutRequest Submit(VpStorageCutInput input, float4 plane, in VpStorageCutOptions options)
        {
            if (_closed)
            {
                throw new ObjectDisposedException(nameof(VpAsyncStorageCut));
            }

            if (input == null)
            {
                throw new ArgumentNullException(nameof(input));
            }

            if (input.IsDisposed)
            {
                throw new ArgumentException("the input adapter has been disposed", nameof(input));
            }

            var request = new VpStorageCutRequest(input, plane, options);
            if (request.rangeCount <= 0)
            {
                // Nothing the kernel could read: refused here, and the adapter it was given — the lease with it — goes
                // back at once rather than being held for a cut that will never run.
                input.Dispose();
                request.input = null;
                request.Stage = VpStorageCutStage.Finished;
                return request;
            }

            request.outputRanges = new NativeArray<MeshCutIndexRange>(2 * request.rangeCount, Allocator.Persistent);
            request.submeshes = new VpGeometrySubmesh[2 * request.rangeCount];
            _requests.Add(request);
            _work.Add(request, new Work(request));
            return request;
        }

        /// <summary>
        /// Moves every cut as far as it can go without waiting for anything, on the main thread: offers what is ready
        /// to the dispatcher, takes and gives back reservations, and turns runs the dispatcher has already collected
        /// into results. Called once per frame, alongside <see cref="SharedWorkDispatcher.Dispatch"/>; calling it more
        /// often is harmless and calling it less only makes cuts slower.
        /// <para>
        /// After <see cref="Dispose"/> this still works, and must still be called: nothing new is offered or reserved,
        /// but a cut a worker was running is taken back here once the dispatcher hands it over, and only then does
        /// what it held — its reservation, its scratch and its input lease — go back. Pumping stops being necessary
        /// when <see cref="ActiveCount"/> reaches zero.
        /// </para>
        /// </summary>
        public void Pump()
        {

            // In the order they were asked for, so that the one waiting longest for the storage's reservation is the
            // one that gets it.
            for (int i = 0; i < _requests.Count; i++)
            {
                Advance(_requests[i]);
            }

            for (int i = _requests.Count - 1; i >= 0; i--)
            {
                VpStorageCutRequest request = _requests[i];
                if (request.IsOver)
                {
                    _requests.RemoveAt(i);
                    _work.Remove(request);
                }
            }
        }

        /// <summary>
        /// Gives up on a cut. One that has not been offered, or is still waiting in the dispatcher's queue, ends here
        /// and gives everything back at once. One a worker is already running is never interrupted: it is marked, and
        /// everything it holds goes back when the dispatcher hands it over, with nothing published. Returns whether
        /// this call was what ended it.
        /// </summary>
        public bool Abandon(VpStorageCutRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.IsOver || !_requests.Contains(request))
            {
                return false;
            }

            if (request.Stage == VpStorageCutStage.Waiting
                || request.Stage == VpStorageCutStage.Ready
                || _dispatcher.Cancel(request.ticket))
            {
                End(request, VpStorageCutStage.Abandoned);
                _requests.Remove(request);
                _work.Remove(request);
                return true;
            }

            // Running, or finished and not yet collected: it is not interrupted, and the next pump after its
            // collection gives everything back.
            request.abandoning = true;
            return true;
        }

        /// <summary>
        /// Closes the runner: no cut is accepted after this, and every cut no worker is using — one never offered, and
        /// one still waiting in the dispatcher's queue — ends here as abandoned with everything it held given back.
        /// <para>
        /// A cut a worker is already running is never interrupted, so this is a close and not an end. It is marked,
        /// and the caller finishes the job the ordinary way: keep dispatching, or stop the dispatcher, and keep
        /// calling <see cref="Pump"/> until <see cref="ActiveCount"/> is zero. Each of those cuts is then taken back
        /// on the main thread, publishes nothing, and gives back its reservation, its scratch and its input lease.
        /// Closing twice does nothing.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            for (int i = _requests.Count - 1; i >= 0; i--)
            {
                VpStorageCutRequest request = _requests[i];
                if (request.Stage == VpStorageCutStage.Waiting
                    || request.Stage == VpStorageCutStage.Ready
                    || _dispatcher.Cancel(request.ticket))
                {
                    End(request, VpStorageCutStage.Abandoned);
                    _requests.RemoveAt(i);
                    _work.Remove(request);
                    continue;
                }

                request.abandoning = true;
            }
        }

        private void Advance(VpStorageCutRequest request)
        {
            Work work = _work[request];
            if (_closed && request.Stage != VpStorageCutStage.Cutting)
            {
                // Closed: nothing is offered or reserved any more. What a worker still holds is all that is left to
                // take back, and that is the one stage below.
                return;
            }

            switch (request.Stage)
            {
                case VpStorageCutStage.Waiting:
                    // The views the worker reads are taken here, on the main thread, and only then is anything
                    // decided: a worker asks the storage nothing itself.
                    if (!TryReadInput(request))
                    {
                        return;
                    }

                    // The sizes, from the input's own description. No worker and no pass over the geometry: the
                    // ranges say how many triangles there are, and the rest is arithmetic (DESIGN 6.1).
                    var capacity = new MeshCutCapacity();
                    MeshCutKernel.EstimateCapacity(in request.kernelInput, ref capacity);
                    if (capacity.invalidInput != 0)
                    {
                        Finish(request, VpStorageCutStatus.InvalidInput);
                        return;
                    }

                    if (capacity.capacityOverflow != 0)
                    {
                        // No reservation of any size would serve this input: not a retry, and never a smaller one.
                        Finish(request, VpStorageCutStatus.CapacityOverflow);
                        return;
                    }

                    request.newVertexCapacity = request.options.newVertexCapacity > 0
                        ? request.options.newVertexCapacity
                        : capacity.newVertices;
                    request.newIndexCapacity = request.options.newIndexCapacity > 0
                        ? request.options.newIndexCapacity
                        : capacity.newIndices;
                    request.scratchBytes = math.max(
                        1,
                        request.options.scratchBytes > 0 ? request.options.scratchBytes : capacity.scratchBytes);
                    request.Stage = VpStorageCutStage.Ready;
                    goto case VpStorageCutStage.Ready;

                case VpStorageCutStage.Ready:
                    if (!TryTakeRoom(request))
                    {
                        // Somebody else holds the storage's one reservation: this cut simply waits here, unoffered.
                        return;
                    }

                    Offer(request, work);
                    return;

                case VpStorageCutStage.Cutting:
                    if (!work.ended || !TakeCompletion(request, work))
                    {
                        return;
                    }

                    Settle(request);
                    return;
            }
        }

        /// <summary>
        /// Takes what one attempt needs and points the kernel's output at it: the reservation, the scratch, and the
        /// input as it stands right now. False when it did not take them, which is not always a failure.
        /// <para>
        /// **Room refused while other cuts of this runner hold some is a wait.** It changes nothing, the cut stays in
        /// <see cref="VpStorageCutStage.Ready"/> and asks again at the next pump, by which time a cut that has settled
        /// may have given its room back. Only a refusal with no other cut of this runner holding anything says the
        /// room is not there to be had, and that ends the cut as a capacity failure. A holder outside this runner --
        /// a synchronous cut of the same storage -- is not visible here and such a refusal is read as the second kind.
        /// </para>
        /// <para>
        /// Every attempt reserves. Whether the plane misses the geometry is only known once the run has looked, and
        /// such a reservation goes back whole and unused when the result is settled.
        /// </para>
        /// </summary>
        private bool TryTakeRoom(VpStorageCutRequest request)
        {
            if (!request.HoldsReservation)
            {
                if (!VpStorageCut.TryReserve(
                        _storage,
                        request.Parent,
                        request.rangeCount,
                        request.newVertexCapacity,
                        request.newIndexCapacity,
                        out VpCutOutputReservation reservation))
                {
                    if (HoldsRoomOtherThan(request))
                    {
                        // Somebody else of this runner's is holding what is free: this cut simply waits here.
                        return false;
                    }

                    Finish(request, VpStorageCutStatus.StorageCapacity);
                    return false;
                }

                request.reservation = reservation;
            }

            if (!request.scratch.IsCreated || request.scratch.Length < request.scratchBytes)
            {
                if (request.scratch.IsCreated)
                {
                    request.scratch.Dispose();
                }

                request.scratch = new NativeArray<byte>(request.scratchBytes, Allocator.Persistent);
            }

            if (!TryReadInput(request))
            {
                return false;
            }

            var output = new MeshCutOutput
            {
                outputRanges = (MeshCutIndexRange*)request.outputRanges.GetUnsafePtr(),
                scratch = (byte*)request.scratch.GetUnsafePtr(),
                scratchBytes = request.scratch.Length,
            };
            if (request.options.nodeEdgeKeys.IsCreated && request.options.nodeParams.IsCreated)
            {
                // An optional record for a caller that asked for it, into that caller's own arrays.
                output.nodeEdgeKeys = (long*)request.options.nodeEdgeKeys.GetUnsafePtr();
                output.nodeParams = (float*)request.options.nodeParams.GetUnsafePtr();
                output.nodeCapacity = math.min(request.options.nodeEdgeKeys.Length, request.options.nodeParams.Length);
            }

            VpStorageCut.PointAtReservation(ref output, request.reservation);
            request.output = output;
            return true;
        }

        /// <summary>
        /// Reads the geometry as it stands into the input the worker will use: the storage's own vertex, index and
        /// topology views, taken on the main thread and kept valid by the read lease the adapter holds. Both parts
        /// that run on a worker are handed this, so neither asks the storage anything itself.
        /// </summary>
        private bool TryReadInput(VpStorageCutRequest request)
        {
            if (request.input.TryGetInput(request.plane, out request.kernelInput))
            {
                return true;
            }

            Finish(request, VpStorageCutStatus.InvalidInput);
            return false;
        }

        /// <summary>Turns a collected cut into its two sides, or into the next attempt, or into the reason it ended.</summary>
        private void Settle(VpStorageCutRequest request)
        {
            request.Attempts++;
            MeshCutResult kernel = request.kernelResult;
            VpStorageCutResult result = request.Result;
            result.kernel = kernel;
            result.attempts = request.Attempts;
            if (kernel.status == MeshCutStatus.Ok)
            {
                if (!_storage.TryGetSubmeshes(request.Parent, out NativeArray<VpGeometrySubmesh>.ReadOnly parentSubmeshes)
                    || parentSubmeshes.Length != request.rangeCount)
                {
                    request.Result = result;
                    Finish(request, VpStorageCutStatus.InternalError);
                    return;
                }

                // The one place a side is described and published, shared with the synchronous entry: the reservation
                // is committed, or given back whole when the plane turned out to miss the geometry.
                VpStorageCut.TryFinish(
                    _storage,
                    request.Parent,
                    request.reservation,
                    in kernel,
                    request.outputRanges,
                    request.submeshes,
                    parentSubmeshes,
                    request.rangeCount,
                    ref result);
                request.Result = result;
                Finish(request, result.status);
                return;
            }

            if (!VpStorageCut.TryNextAttempt(
                    in kernel,
                    ref request.newVertexCapacity,
                    ref request.newIndexCapacity,
                    ref request.scratchBytes,
                    ref result))
            {
                request.Result = result;
                Finish(request, result.status);
                return;
            }

            // A short reservation is not a failure: it goes back whole, so another cut may have it, and this one asks
            // for a larger one at a later opportunity.
            request.Result = result;
            ReleaseReservation(request);
            request.Stage = VpStorageCutStage.Ready;
        }

        /// <summary>
        /// Reads what the dispatcher gave back. False when the run did not happen or must not be used: a worker's
        /// exception and a cancellation both end the cut here, and so does the caller having given up on it.
        /// </summary>
        private bool TakeCompletion(VpStorageCutRequest request, Work work)
        {
            work.ended = false;
            WorkCompletion completion = work.completion;
            if (request.abandoning)
            {
                End(request, VpStorageCutStage.Abandoned);
                return false;
            }

            switch (completion.outcome)
            {
                case WorkOutcome.Finished:
                    return true;

                case WorkOutcome.Failed:
                    request.Failure = completion.failure;
                    Finish(request, VpStorageCutStatus.InternalError);
                    return false;

                default:
                    End(request, VpStorageCutStage.Abandoned);
                    return false;
            }
        }

        private void Offer(VpStorageCutRequest request, Work work)
        {
            work.ended = false;
            if (!_dispatcher.TryEnqueue(_purpose, work, out WorkTicket ticket))
            {
                // No place for it right now. It keeps whatever it has taken and is offered again at the next pump.
                return;
            }

            request.ticket = ticket;
            request.Stage = VpStorageCutStage.Cutting;
        }

        private void Finish(VpStorageCutRequest request, VpStorageCutStatus status)
        {
            VpStorageCutResult result = request.Result;
            result.status = status;
            result.attempts = request.Attempts;
            request.Result = result;
            End(request, VpStorageCutStage.Finished);
        }

        /// <summary>Gives back everything one cut held, exactly once, and settles its stage.</summary>
        private void End(VpStorageCutRequest request, VpStorageCutStage stage)
        {
            ReleaseReservation(request);
            if (request.scratch.IsCreated)
            {
                request.scratch.Dispose();
                request.scratch = default;
            }

            if (request.outputRanges.IsCreated)
            {
                request.outputRanges.Dispose();
                request.outputRanges = default;
            }

            if (request.input != null)
            {
                request.input.Dispose();
                request.input = null;
            }

            request.kernelInput = default;
            request.output = default;
            request.Stage = stage;
        }

        private void ReleaseReservation(VpStorageCutRequest request)
        {
            if (request.reservation != null)
            {
                if (!request.reservation.IsClosed)
                {
                    _storage.TryCancelCutOutput(request.reservation);
                }

                request.reservation = null;
            }

        }

        /// <summary>
        /// One cut's worker side: the cut itself, and nothing else is ever offered. It is the kernel and nothing more
        /// — no storage, no Unity object, no allocation — reading views the main thread handed over and writing only
        /// this cut's own scratch, ranges and reservation.
        /// </summary>
        private sealed class Work : IDispatchWork
        {
            private readonly VpStorageCutRequest _request;
            internal bool ended;
            internal WorkCompletion completion;

            internal Work(VpStorageCutRequest request)
            {
                _request = request;
            }

            public void Begin()
            {
                _request.cutThreadId = Thread.CurrentThread.ManagedThreadId;
                MeshCutResult kernel = default;
                MeshCutKernel.Execute(in _request.kernelInput, in _request.output, ref kernel);
                _request.kernelResult = kernel;
            }

            public bool IsComplete => true;

            public void Collect(WorkCompletion completion)
            {
                // Only the news. What it means for the storage is the main thread's own next pump, so that taking a
                // reservation and publishing a side never happen inside a dispatch.
                _request.collectThreadId = Thread.CurrentThread.ManagedThreadId;
                this.completion = completion;
                ended = true;
            }
        }
    }
}
