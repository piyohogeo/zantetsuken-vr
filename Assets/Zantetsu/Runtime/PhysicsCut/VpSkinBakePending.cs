using System;
using System.Collections.Generic;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The deferred CPU bakes of the GPU-first route (DESIGN 4.5.15), owned by the world and not by any character handle:
    /// a hit under <c>-zantetsuSkinBake gpu</c> shows its pose by the GPU bake, is accepted, published and withdrawn as any
    /// hit is, and its handle ends with its cut; what goes on living here is the work that writes the CPU vertices the
    /// cuts below that fragment need -- the snapshot (palette, float input, output), the fragment, the reserved geometry.
    /// Its Begin runs the float kernel on the Geometry Pool's thread; its Collect, on the main thread, completes the
    /// storage, gives the display's GPU region back to the storage and registers the base geometry with the cut DAG
    /// (<see cref="CutDag.TryCompletePendingBaseGeometry"/>), which then offers the cuts that were waiting. A bake that
    /// failed fails those cuts through the DAG's own geometry failure; a bake whose pieces all ended before it came back
    /// adopts nothing. Nothing here re-poses, re-accepts or re-registers a physics owner, and nothing waits on the main thread.
    /// </summary>
    /// <summary>How one GPU-first hit's deferred CPU bake ended (SKIN-GPU-FIRST-EVAL-1): reported to <see cref="VpSkinBakePending.CollectedForDiagnosis"/>.</summary>
    public enum VpSkinBakeCollectOutcome
    {
        /// <summary>The storage took the vertices and the DAG's pending basis was published: the CPU Ready of the request.</summary>
        Completed = 0,
        /// <summary>The worker or its output failed; the waiting geometry was failed once.</summary>
        Failed = 1,
        /// <summary>Nothing adopted it: the geometry was retired or refused, or no basis waited.</summary>
        Dropped = 2,
        /// <summary>The world's cuts ended, or the storage was gone, when the work came back.</summary>
        Abandoned = 3,
        /// <summary>Withdrawn before it ran (held, or cancelled in the dispatcher).</summary>
        Withdrawn = 4,
    }

    public sealed class VpSkinBakePending : IMainThreadPump
    {
        private readonly CutWorldRoot _world;
        private readonly VpSkinBakeResources _resources;
        private readonly List<Work> _held = new List<Work>(4);
        private readonly List<Work> _inFlight = new List<Work>(8);
        private readonly List<Work> _failedHeld = new List<Work>(2);
        private bool _ended;

        /// <summary>
        /// Tests only: while this answers true, begun works are kept out of the dispatcher (the CPU bake's completion is
        /// deliberately held, the worker is not), and enqueued when it answers false again. Null in the product.
        /// </summary>
        public static Func<bool> HoldForTest;

        /// <summary>
        /// Diagnosis only (PLANE-1): above 0, a hit's CPU bake stays out of the dispatcher until this many frames after its hit
        /// frame, so that the time the cut's geometry waits for its CPU basis is long enough to picture. Nothing else changes:
        /// the hit's acceptance, the provisional physics, the display and the dispatcher run as before, and the bake then runs
        /// and completes as it would. Off by default; the skin bake probe's -zantetsuSkinBakeProbeHoldCpuReady sets it and
        /// clears it when it ends. A run under it is no performance measurement.
        /// </summary>
        public static int HoldSubmitFramesForDiagnosis;

        /// <summary>
        /// A diagnosis hook (SKIN-GPU-FIRST-EVAL-1, 2026-11-02): called once for each work when its collect ends -- with the hit's
        /// request id and how it ended -- inside the collect, so a probe records the CPU Ready (Completed) apart from a failure, a
        /// drop, an abandonment or a withdrawal. Null in the product; an exception it throws never leaves the collect.
        /// </summary>
        public static Action<long, VpSkinBakeCollectOutcome> CollectedForDiagnosis;

        private static void ReportCollected(long requestId, VpSkinBakeCollectOutcome outcome)
        {
            Action<long, VpSkinBakeCollectOutcome> hook = CollectedForDiagnosis;
            if (hook == null) return;
            try { hook(requestId, outcome); }
            catch (Exception) { }
        }

        internal VpSkinBakePending(CutWorldRoot world, VpSkinBakeResources resources)
        {
            _world = world;
            _resources = resources;
        }

        /// <summary>How many bakes are begun and not collected: held before the dispatcher, waiting in it, or running.</summary>
        public int Count => _held.Count + _inFlight.Count;
        public int HeldCount => _held.Count;
        public int InFlightCount => _inFlight.Count;

        /// <summary>Failed bakes whose snapshot is kept while the display still draws their region.</summary>
        public int FailedHeldCount => _failedHeld.Count;

        public int Begun { get; private set; }
        public int Completed { get; private set; }
        public int Failed { get; private set; }
        public int Dropped { get; private set; }
        public int Abandoned { get; private set; }
        public int Withdrawn { get; private set; }

        /// <summary>
        /// Begins the deferred CPU bake of one hit (before the hit's acceptance): the work is enqueued for the Geometry Pool
        /// (or held, under the test hold). False, with the reason and nothing kept, when the dispatcher has no place; the
        /// snapshot is then still the caller's to return.
        /// </summary>
        internal bool TryBegin(LogicalFragmentId source, VpStoredGeometry geometry, VpSkinBakeSnapshot snapshot, int hitFrame, out Work work, out string refusal)
        {
            refusal = null;
            work = null;
            if (_ended) { refusal = "the world's cuts are ending"; return false; }
            var made = new Work(this, source, geometry, snapshot, hitFrame);
            if ((HoldForTest != null && HoldForTest()) || HoldSubmitFramesForDiagnosis > 0)
            {
                made.held = true;
                _held.Add(made);
            }
            else if (!TryEnqueue(made, out refusal))
            {
                return false;
            }

            Begun++;
            work = made;
            return true;
        }

        private bool TryEnqueue(Work work, out string refusal)
        {
            refusal = null;
            if (_world.Dispatcher == null) { refusal = "no dispatcher"; return false; }
            if (!_world.Dispatcher.TryEnqueue(WorkPurpose.HitPreparation, work, out work.ticket)) { refusal = "dispatcher: no place for the hit preparation"; return false; }
            work.held = false;
            _inFlight.Add(work);
            return true;
        }

        /// <summary>
        /// A begun work given back before its hit was accepted (the show was refused, the hit failed before its acceptance):
        /// a held or waiting work is taken out and its snapshot returned now; a running one adopts nothing at its collect.
        /// </summary>
        internal void Withdraw(Work work)
        {
            if (work == null) return;
            if (work.held)
            {
                _held.Remove(work);
                work.held = false;
                work.ReleaseSnapshot();
                Withdrawn++;
                ReportCollected(work.requestId, VpSkinBakeCollectOutcome.Withdrawn);
                return;
            }

            if (work.ticket.IsSet && _world.Dispatcher != null && _world.Dispatcher.Cancel(work.ticket))
            {
                _inFlight.Remove(work);
                work.ticket = default;
                work.ReleaseSnapshot();
                Withdrawn++;
                ReportCollected(work.requestId, VpSkinBakeCollectOutcome.Withdrawn);
                return;
            }

            work.abandonOnCollect = true;
        }

        /// <summary>Each pump: held works go to the dispatcher once the hold is lifted; failed works give their snapshot back once their region is no longer drawn.</summary>
        public bool Pump()
        {
            bool moved = false;
            if (_held.Count > 0 && !_ended && (HoldForTest == null || !HoldForTest()))
            {
                for (int i = 0; i < _held.Count; i++)
                {
                    Work work = _held[i];
                    if (HoldSubmitFramesForDiagnosis > 0 && Time.frameCount < work.hitFrame + HoldSubmitFramesForDiagnosis) continue;   // the diagnosis hold: not yet
                    if (!TryEnqueue(work, out _)) break;   // no place now: the rest stay held for the next pump
                    _held.RemoveAt(i--);
                    moved = true;
                }
            }

            for (int i = _failedHeld.Count - 1; i >= 0; i--)
            {
                Work work = _failedHeld[i];
                if (_world.Display == null || _world.Display.IsDisposed || !_world.Display.HasGpuRegion(work.geometry))
                {
                    _failedHeld.RemoveAt(i);
                    work.ReleaseSnapshot();
                    moved = true;
                }
            }

            return moved;
        }

        /// <summary>
        /// The world's cuts are ending: held works give their snapshot back, waiting ones are taken out of the dispatcher,
        /// running ones adopt nothing when collected (the dispatcher's own drain collects them), failed ones give theirs back.
        /// </summary>
        internal void EndAll()
        {
            _ended = true;
            for (int i = 0; i < _held.Count; i++) { _held[i].held = false; _held[i].ReleaseSnapshot(); Withdrawn++; }
            _held.Clear();
            for (int i = _inFlight.Count - 1; i >= 0; i--)
            {
                Work work = _inFlight[i];
                if (work.ticket.IsSet && _world.Dispatcher != null && _world.Dispatcher.Cancel(work.ticket))
                {
                    _inFlight.RemoveAt(i);
                    work.ticket = default;
                    work.ReleaseSnapshot();
                    Withdrawn++;
                }
                else
                {
                    work.abandonOnCollect = true;
                }
            }

            for (int i = 0; i < _failedHeld.Count; i++) _failedHeld[i].ReleaseSnapshot();
            _failedHeld.Clear();
        }

        /// <summary>Whether everything begun has been collected (the world's drain asks).</summary>
        public bool IsDrained => _held.Count == 0 && _inFlight.Count == 0;

        public string Describe()
        {
            return "skin bake pending: begun " + Begun + ", completed " + Completed + ", failed " + Failed + ", dropped " + Dropped + ", abandoned " + Abandoned + ", withdrawn " + Withdrawn
                + "; now held " + _held.Count + ", in flight " + _inFlight.Count + ", failed held " + _failedHeld.Count;
        }

        // The collect of one work, on the main thread (inside the dispatcher's Dispatch): what the result becomes.
        private void Collected(Work work, WorkCompletion completion)
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            _inFlight.Remove(work);
            work.ticket = default;
            string outcome;
            VpSkinBakeCollectOutcome ended = VpSkinBakeCollectOutcome.Abandoned;
            try
            {
                if (work.abandonOnCollect || _ended || _world == null || _world.Storage == null || _world.Geometry == null)
                {
                    Abandon(work);
                    outcome = "abandoned"; ended = VpSkinBakeCollectOutcome.Abandoned;
                    return;
                }

                if (!completion.Succeeded)
                {
                    FailBasis(work, completion.failure, "the worker " + completion.outcome + (completion.failure != null ? ": " + completion.failure.Message : ""));
                    outcome = "failed"; ended = VpSkinBakeCollectOutcome.Failed;
                    return;
                }

                if (!work.snapshot.TryMeasuredBounds(out Bounds measured, out string why))
                {
                    FailBasis(work, null, "the output is not usable: " + why);
                    outcome = "failed"; ended = VpSkinBakeCollectOutcome.Failed;
                    return;
                }

                bool completed;
                try
                {
                    completed = _world.Storage.TryCompleteDeferredSkin(work.geometry, work.snapshot.Vertices, work.snapshot.Topology, measured);
                }
                catch (ObjectDisposedException)
                {
                    Abandon(work);
                    outcome = "abandoned"; ended = VpSkinBakeCollectOutcome.Abandoned;
                    return;
                }

                if (!completed)
                {
                    // Not pending any more: the display retired the geometry (its pieces ended) before the bake came back,
                    // or the storage refuses it. Nothing is adopted; the pending flag, where it is still set, goes.
                    Dropped++;
                    try { _world.Storage.TryAbandonDeferredSkin(work.geometry, false); } catch (ObjectDisposedException) { }
                    try { _world.Geometry.DropPendingBaseGeometry(work.source, work.geometry); } catch (ObjectDisposedException) { }
                    ForgetRegion(work);
                    work.ReleaseSnapshot();
                    VpSkinBakeStats.NoteDropped(work.requestId, "the storage would not complete it (retired or refused)");
                    outcome = "dropped"; ended = VpSkinBakeCollectOutcome.Dropped;
                    return;
                }

                if (_world.Display != null && !_world.Display.IsDisposed) _world.Display.CompleteGpuRegion(work.geometry);
                if (_world.Geometry.TryCompletePendingBaseGeometry(work.source, work.geometry))
                {
                    Completed++;
                    outcome = "completed"; ended = VpSkinBakeCollectOutcome.Completed;
                }
                else
                {
                    Dropped++;
                    VpSkinBakeStats.NoteDropped(work.requestId, "no pending basis or no reader for " + work.source);
                    outcome = "dropped"; ended = VpSkinBakeCollectOutcome.Dropped;
                }

                work.ReleaseSnapshot();
            }
            finally
            {
                double collectSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
                VpSkinBakeStats.NoteReady(work.requestId, Time.frameCount, work.workerSeconds, work.kernelSeconds, collectSeconds, work.dispatches);
                ReportCollected(work.requestId, ended);
            }
        }

        private void FailBasis(Work work, Exception failure, string reason)
        {
            Failed++;
            VpSkinBakeStats.NoteBasisFailed(work.requestId, reason);
            bool told = false;
            try
            {
                told = _world.Geometry != null && _world.Geometry.FailPendingBaseGeometry(work.source, work.geometry, VpStorageCutStatus.InvalidInput,
                    failure ?? new InvalidOperationException("the deferred skin bake of " + work.source + " failed: " + reason));
            }
            catch (ObjectDisposedException) { }

            // The region keeps what it draws (the GPU bake of the hit's pose), so its producer stays until the display lets the
            // region go; the storage's vertices never come. Nothing waits where the DAG had no pending basis for it.
            try { _world.Storage?.TryAbandonDeferredSkin(work.geometry, false); } catch (ObjectDisposedException) { }
            if (told && _world.Display != null && !_world.Display.IsDisposed && _world.Display.HasGpuRegion(work.geometry))
            {
                _failedHeld.Add(work);
                return;
            }

            ForgetRegion(work);
            work.ReleaseSnapshot();
        }

        private void Abandon(Work work)
        {
            Abandoned++;
            try { _world?.Storage?.TryAbandonDeferredSkin(work.geometry, false); } catch (ObjectDisposedException) { }
            try { _world?.Geometry?.DropPendingBaseGeometry(work.source, work.geometry); } catch (ObjectDisposedException) { }
            ForgetRegion(work);
            work.ReleaseSnapshot();
            VpSkinBakeStats.NoteDropped(work.requestId, "abandoned (the world's cuts ending, or withdrawn before the acceptance)");
        }

        private void ForgetRegion(Work work)
        {
            try
            {
                if (_world?.Display != null && !_world.Display.IsDisposed) _world.Display.ForgetGpuRegion(work.geometry);
            }
            catch (ObjectDisposedException) { }
        }

        /// <summary>
        /// One deferred CPU bake as a piece of dispatcher work (DESIGN 4.3, 4.4): Begin runs the float kernel over the
        /// snapshot on the Geometry Pool's thread and touches nothing else; Collect hands the result to the owner on the main thread.
        /// </summary>
        public sealed class Work : IDispatchWork
        {
            private readonly VpSkinBakePending _owner;
            internal readonly LogicalFragmentId source;
            internal readonly VpStoredGeometry geometry;
            internal readonly VpSkinBakeSnapshot snapshot;
            internal readonly long requestId;
            internal readonly int hitFrame;
            internal WorkTicket ticket;
            internal bool held, abandonOnCollect;
            internal double workerSeconds, kernelSeconds;
            internal int dispatches;
            private bool _released;

            internal Work(VpSkinBakePending owner, LogicalFragmentId source, VpStoredGeometry geometry, VpSkinBakeSnapshot snapshot, int hitFrame)
            {
                _owner = owner; this.source = source; this.geometry = geometry; this.snapshot = snapshot; this.hitFrame = hitFrame;
                requestId = snapshot.RequestId;
            }

            /// <summary>The snapshot's request id (the stats' key).</summary>
            public long RequestId => requestId;

            /// <summary>The snapshot, while the work holds it (its Dispatches are read at the hit's record).</summary>
            public VpSkinBakeSnapshot Snapshot => _released ? null : snapshot;

            public void Begin()
            {
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                snapshot.RunKernel();
                workerSeconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
                kernelSeconds = snapshot.KernelSeconds;
            }

            public bool IsComplete => true;

            public void Collect(WorkCompletion completion)
            {
                dispatches = snapshot.Dispatches;
                _owner.Collected(this, completion);
            }

            internal void ReleaseSnapshot()
            {
                if (_released) return;
                _released = true;
                _owner._resources.Return(snapshot);
            }
        }
    }
}
