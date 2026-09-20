using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>How far one owner's cut and cook has come.</summary>
    public enum PhysicsCutStage
    {
        /// <summary>Accepted. Nothing is reserved yet and nothing has been offered.</summary>
        Waiting = 0,

        /// <summary>Its output is reserved and the numerical work is with the dispatcher: queued, or running.</summary>
        Cutting = 1,

        /// <summary>The numbers are back. Its collider meshes are applied here, and the cook is offered.</summary>
        Applying = 2,

        /// <summary>The meshes are applied and the cook is with the dispatcher.</summary>
        Baking = 3,

        /// <summary>Over, with products or with a reason.</summary>
        Finished = 4,

        /// <summary>Given up, or the runner was closed. Everything it held has gone back.</summary>
        Abandoned = 5,
    }

    /// <summary>How one owner's cut and cook ended.</summary>
    public enum PhysicsCutOutcomeKind
    {
        /// <summary>Not over yet.</summary>
        Pending = 0,

        /// <summary>The two sides were produced and their new convexes were meshed and baked.</summary>
        Ok = 1,

        /// <summary>The input was refused before any work.</summary>
        InvalidInput = 2,

        /// <summary>The numerical kernel did not succeed. Its own status says why.</summary>
        KernelFailed = 3,

        /// <summary>
        /// The run reached its end but is not one that may be handed over: a produced convex got no collider mesh, a
        /// mesh could not be applied, or a work came back failed from the dispatcher. It is not a claim about the
        /// cooked hull, which no boundary here reports on.
        /// </summary>
        CookFailed = 4,

        /// <summary>Given up by the caller, cancelled by the dispatcher, or the runner closed before it ran.</summary>
        Abandoned = 5,
    }

    /// <summary>
    /// One convex of one side of a finished cut. A part is either **borrowed** — an input convex the plane did not
    /// split, which stays the caller's, keeps its existing cooked shape and has no mesh here — or **produced** by
    /// this cut, which lives in the products' own arena and has its own baked mesh.
    /// </summary>
    public readonly struct PhysicsCutPart
    {
        internal PhysicsCutPart(int inputConvex, bool borrowed, ConvexBrepRange range, Mesh mesh, float3x2 bounds)
        {
            this.inputConvex = inputConvex;
            this.borrowed = borrowed;
            this.range = range;
            this.mesh = mesh;
            this.bounds = bounds;
        }

        /// <summary>The input convex it came from.</summary>
        public readonly int inputConvex;

        /// <summary>Whether it is the input convex itself, inherited uncut.</summary>
        public readonly bool borrowed;

        /// <summary>
        /// Where its B-rep is: in the caller's own input bank when borrowed, and in
        /// <see cref="PhysicsCutProducts.Bank"/> when produced.
        /// </summary>
        public readonly ConvexBrepRange range;

        /// <summary>The baked collider mesh of a produced part; null for a borrowed one.</summary>
        public readonly Mesh mesh;

        /// <summary>
        /// The box this produced part lies in, in the numerical local frame: the low corner in <c>c0</c> and the high
        /// one in <c>c1</c>, as the job measured them while it wrote the mesh. It is carried here so that whoever
        /// builds a shape from this part has a box for it without reading a vertex.
        /// <para>
        /// These are the measurements themselves, not the mesh's own <see cref="UnityEngine.Mesh.bounds"/>, which is
        /// the same numbers put through a centre-and-size pair of floats and can come back very slightly inside them.
        /// Meaningless for a borrowed part, which brings no mesh and whose box its parent already has.
        /// </para>
        /// </summary>
        public readonly float3x2 bounds;
    }

    /// <summary>
    /// What one finished cut produced, unpublished: the adopted convexes of each side with their correspondence to the
    /// input, the meshes that were cooked and the profile they were cooked with, the mass properties of each side, and
    /// the transform from the numerical local frame to the owner's, as the caller gave it.
    /// <para>
    /// **What it owns.** The arena the produced convexes live in and the meshes it made. Disposing it gives both back.
    /// It owns nothing of the input: a borrowed part names an input convex, so the caller's own input must outlive
    /// these products for those parts to be read.
    /// </para>
    /// <para>
    /// Nothing here is published. No actor, no collider, no logical publication and no display follows from holding
    /// these; that connection is not made yet.
    /// </para>
    /// </summary>
    public sealed class PhysicsCutProducts : IDisposable
    {
        private readonly PhysicsCutArena _arena;
        private readonly ConvexCutOutcome[] _outcomes;
        private readonly List<PhysicsCutPart> _positive = new List<PhysicsCutPart>(4);
        private readonly List<PhysicsCutPart> _negative = new List<PhysicsCutPart>(4);
        private readonly List<Mesh> _meshes = new List<Mesh>(4);
        private bool _disposed;

        internal PhysicsCutProducts(
            PhysicsCutArena arena,
            ConvexCutOutcome[] outcomes,
            in ConvexCutOwnerResult result,
            float4x4 localToOwner,
            MeshColliderCookingOptions cooking)
        {
            _arena = arena;
            _outcomes = outcomes;
            Result = result;
            LocalToOwner = localToOwner;
            Cooking = cooking;
        }

        /// <summary>The numerical result: mass, centre of mass and inertia of each side, and the kernel's own counts.</summary>
        public ConvexCutOwnerResult Result { get; }

        /// <summary>
        /// From the numerical local frame to the owner's, as the caller gave it with the input. Nothing here converts
        /// anything; this is the correspondence, so that whoever applies the colliders uses the frame the numbers are
        /// in.
        /// </summary>
        public float4x4 LocalToOwner { get; }

        /// <summary>The cooking profile every mesh here was baked with; the collider it is applied to must match it.</summary>
        public MeshColliderCookingOptions Cooking { get; }

        /// <summary>The arena the produced parts' B-reps live in. A borrowed part is not in it.</summary>
        public ConvexBrepBank Bank => _arena.Bank;

        /// <summary>How many input convexes the cut was of.</summary>
        public int InputConvexCount => _outcomes.Length;

        /// <summary>What became of one input convex: split into two, or inherited uncut by one side.</summary>
        public ConvexCutOutcome OutcomeOf(int inputConvex)
        {
            return _outcomes[inputConvex];
        }

        public int PartCount(bool positive)
        {
            return positive ? _positive.Count : _negative.Count;
        }

        public PhysicsCutPart Part(bool positive, int index)
        {
            return positive ? _positive[index] : _negative[index];
        }

        internal void Add(bool positive, in PhysicsCutPart part)
        {
            (positive ? _positive : _negative).Add(part);
            if (part.mesh != null)
            {
                _meshes.Add(part.mesh);
            }
        }

        /// <summary>Gives back the arena and the meshes this made. The caller's own input is untouched.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            for (int i = 0; i < _meshes.Count; i++)
            {
                PhysicsCutCook.DestroyMesh(_meshes[i]);
            }

            _meshes.Clear();
            _arena.Dispose();
        }
    }

    /// <summary>
    /// One owner cut asked for through <see cref="PhysicsCutCook"/>: the caller's handle while it runs and its
    /// products when it is over. The input stays the caller's and must be held unchanged until the cut is over.
    /// </summary>
    public sealed class PhysicsCutRequest
    {
        internal ConvexCutOwnerInput input;
        internal float4x4 localToOwner;
        internal PhysicsCutArena arena;
        internal ConvexCutOwnerCapacity capacity;
        internal Mesh.MeshDataArray meshData;
        internal bool meshDataHeld;
        internal Mesh[] meshes;
        internal NativeArray<int> meshIds;
        internal NativeArray<float3x2> meshBounds;
        internal NativeArray<PhysicsCutJobReport> report;
        internal NativeArray<int> meshVertexCounts;
        internal NativeArray<byte> bakeDone;
        internal PhysicsCutJobReport cut;
        internal bool abandoning;

        /// <summary>Called inside a collection, for the tests that need one to throw there.</summary>
        internal Action collectHook;

        /// <summary>
        /// Leaves the numerical job unscheduled, for the tests about a work that comes back without having run: the
        /// report then stays as nobody wrote it.
        /// </summary>
        internal bool skipCutJob;

        /// <summary>The work of the stage that is with the dispatcher, and its ticket; null between stages.</summary>
        internal object work;
        internal WorkTicket ticket;

        internal PhysicsCutRequest(in ConvexCutOwnerInput input, float4x4 localToOwner)
        {
            this.input = input;
            this.localToOwner = localToOwner;
        }

        /// <summary>How far it has come.</summary>
        public PhysicsCutStage Stage { get; internal set; } = PhysicsCutStage.Waiting;

        /// <summary>Whether it is over, either way.</summary>
        public bool IsOver => Stage == PhysicsCutStage.Finished || Stage == PhysicsCutStage.Abandoned;

        /// <summary>How it ended.</summary>
        public PhysicsCutOutcomeKind Outcome { get; internal set; } = PhysicsCutOutcomeKind.Pending;

        /// <summary>The kernel's own status, for a cut that reached it.</summary>
        public ConvexCutOwnerStatus KernelStatus { get; internal set; }

        /// <summary>What a work threw, when the dispatcher brought one back failed; null otherwise.</summary>
        public Exception Failure { get; internal set; }

        /// <summary>The products of a cut that ended Ok; null otherwise. They become the caller's.</summary>
        public PhysicsCutProducts Products { get; internal set; }

        /// <summary>Whether the output of this cut is reserved right now.</summary>
        public bool HoldsReservation => arena != null;

        /// <summary>How many jobs this cut has really scheduled, for the tests about the dispatcher's control.</summary>
        internal int scheduled;

        /// <summary>The ticket of the work that is with the dispatcher, for the tests.</summary>
        internal WorkTicket Ticket => ticket;
    }

    /// <summary>
    /// Runs one owner's convex cut and the cook of what it produced, without occupying the main thread with either
    /// (DESIGN 4.3, 4.4, 7.2, 7.3): the existing numerical kernel and the bake are two pieces of work offered to the
    /// shared dispatcher, the collider meshes are applied on the main thread between them, and the products are taken
    /// back on the main thread.
    /// <para>
    /// **The dispatcher decides when and where.** Nothing is scheduled here: each stage is offered with
    /// <see cref="SharedWorkDispatcher.TryEnqueue"/>, and the job is scheduled in that work's own
    /// <see cref="IDispatchWork.Begin"/>, which the dispatcher calls when its queue, its frame budget and the
    /// destination's room allow. A cut whose offer is refused waits unoffered and is offered again. Each stage is its
    /// own work: the cut is collected before the bake is offered, one for one.
    /// </para>
    /// <para>
    /// **The division.** Holding the input, asking the kernel what it needs and reserving that, applying a mesh and
    /// handing the products over are the main thread's. The numerical work — the clip, the reduction, the mass
    /// properties and the collider mesh data of what was produced — is one job, not several. The bake is its own job
    /// because it is managed and may not be called from Burst. Nothing is completed early to make progress: a stage
    /// ends when the dispatcher hands it back.
    /// </para>
    /// <para>
    /// **Capacity is physics' own.** The kernel's own capacity query decides what one cut needs, and that is reserved
    /// before it is offered. A cut that cannot be reserved waits unoffered; it is not a failure and there is no
    /// growing retry — the query is a worst case, so a capacity the kernel then refuses is an error and not a step.
    /// </para>
    /// <para>
    /// **What it does not do.** It publishes nothing: no actor, no shape, no constraint, no logical publication and no
    /// display. A failure is handed to the caller as a failure — the source is not retired here, nothing is replaced
    /// by another shape, and the abort of a physics transaction is not connected yet.
    /// </para>
    /// </summary>
    public sealed class PhysicsCutCook : IDisposable
    {
        /// <summary>
        /// The one cooking profile (DESIGN 7.3). It is the configuration the Phase 2.9 cook pass-through check used —
        /// cooking for faster simulation, mesh cleaning, welding colocated vertices and the fast midphase — so that
        /// what is baked here is baked the way that check baked it. Whoever applies a produced mesh to a
        /// <see cref="MeshCollider"/> must set the same options on that collider, which is why the profile is carried
        /// with the products.
        /// </summary>
        public const MeshColliderCookingOptions DefaultCooking =
            MeshColliderCookingOptions.CookForFasterSimulation
            | MeshColliderCookingOptions.EnableMeshCleaning
            | MeshColliderCookingOptions.WeldColocatedVertices
            | MeshColliderCookingOptions.UseFastMidphase;

        private readonly SharedWorkDispatcher _dispatcher;
        private readonly WorkPurpose _purpose;
        private readonly MeshColliderCookingOptions _cooking;
        private readonly int _reservations;
        private readonly List<PhysicsCutRequest> _requests = new List<PhysicsCutRequest>(2);
        private int _reserved;
        private bool _closed;

        /// <summary>
        /// Takes the dispatcher the work runs through, how many cuts may hold their reservation at once, and the
        /// cooking profile. The purpose decides the destination: <see cref="WorkPurpose.AdmittedPhysics"/>, the urgent
        /// Unity job system, is what an admitted cut's physics is.
        /// <para>
        /// <paramref name="concurrentReservations"/> bounds **how many requests may hold a reservation at once**. One
        /// reservation is an arena sized by the kernel's worst case for that input, the small arrays the job reports
        /// through, one writable mesh data of the worst-case mesh count, and that many <see cref="Mesh"/> objects; it
        /// is held from the moment it is taken until the products are handed over or the cut ends, which spans the
        /// numerical work, the main-thread mesh application and the bake. Requests differ in size, so **this is a
        /// count, not a byte limit**: it does not on its own promise any fixed amount of memory (DESIGN 4.4 keeps the
        /// resources finite; this is the count that keeps them so).
        /// </para>
        /// <para>
        /// It is a different limit from the ones that bound execution -- the dispatcher's waiting capacity (how many
        /// works may wait), each destination's capacity (how many accepted works are not yet collected) and the frame
        /// budget (how many submissions and collections a frame allows). Being different does not make it separate in
        /// effect: **a cut that cannot reserve is never offered**, so this count also bounds how many can be in flight.
        /// </para>
        /// <para>
        /// Requests are independent: each has an arena and a working set of its own, and nothing of one cut is read or
        /// written by another. Two owners may therefore hold reservations and be with the dispatcher at the same time.
        /// One owner waiting for its bake does not stop another's numerical work **provided the other has what it
        /// needs**: a free reservation here, a place at the destination, frame budget, and the memory its own
        /// reservation asks for.
        /// </para>
        /// <para>
        /// It has **no default on purpose**. A value belongs to whoever builds this and knows the memory it may hold
        /// at once; one silently inherited from here would be a product setting nobody chose.
        /// </para>
        /// </summary>
        public PhysicsCutCook(
            SharedWorkDispatcher dispatcher,
            int concurrentReservations,
            MeshColliderCookingOptions cooking = DefaultCooking,
            WorkPurpose purpose = WorkPurpose.AdmittedPhysics)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            if (concurrentReservations <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(concurrentReservations), "at least one cut must be able to run");
            }

            if (!WorkPurposes.IsDefined(purpose))
            {
                throw new ArgumentOutOfRangeException(nameof(purpose), purpose, "not a defined work purpose");
            }

            _dispatcher = dispatcher;
            _reservations = concurrentReservations;
            _cooking = cooking;
            _purpose = purpose;
        }

        /// <summary>How many cuts are neither finished nor abandoned.</summary>
        public int ActiveCount => _requests.Count;

        /// <summary>Whether everything this held has gone back, which a closed runner reaches by being pumped.</summary>
        public bool IsDrained => _requests.Count == 0;

        /// <summary>How many cuts hold a reservation right now.</summary>
        public int Reserving => _reserved;

        /// <summary>How many cuts may hold a reservation at once, as this was built with.</summary>
        public int ConcurrentReservations => _reservations;

        /// <summary>The cooking profile this bakes with, and that a collider using its meshes must match.</summary>
        public MeshColliderCookingOptions Cooking => _cooking;

        /// <summary>
        /// Accepts one owner cut. The input — the compound B-rep, the adopted plane, the caller's own distance and
        /// support classification, the parent mass and the vertex limit — stays the caller's and **must be held
        /// unchanged** from here until the cut is over; nothing is classified again here. The transform is from the
        /// numerical local frame to the owner's and comes back with the products. Nothing is reserved and nothing runs
        /// until <see cref="Pump"/> and the dispatcher have had their turn.
        /// </summary>
        public PhysicsCutRequest Submit(in ConvexCutOwnerInput input, float4x4 localToOwner)
        {
            if (_closed)
            {
                throw new ObjectDisposedException(nameof(PhysicsCutCook));
            }

            var request = new PhysicsCutRequest(in input, localToOwner);
            if (input.convexCount <= 0)
            {
                request.Outcome = PhysicsCutOutcomeKind.InvalidInput;
                request.Stage = PhysicsCutStage.Finished;
                return request;
            }

            _requests.Add(request);
            return request;
        }

        /// <summary>
        /// Moves every cut as far as it can on the main thread: reserves and offers what may be, applies the meshes of
        /// a cut whose numbers the dispatcher has handed back, offers the bake, and turns a collected bake into
        /// products.
        /// <para>
        /// After <see cref="Dispose"/> this still works and must still be called: nothing new is offered, and what the
        /// dispatcher still holds is taken back and given up as it arrives, until <see cref="IsDrained"/>.
        /// </para>
        /// </summary>
        public void Pump()
        {
            for (int i = 0; i < _requests.Count; i++)
            {
                Advance(_requests[i]);
            }

            for (int i = _requests.Count - 1; i >= 0; i--)
            {
                if (_requests[i].IsOver)
                {
                    _requests.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Gives up on a cut. One that has not been offered ends here; one still waiting in the dispatcher's queue is
        /// taken out of it and ends here too. One whose work the dispatcher has already submitted is never
        /// interrupted: it is marked, and everything it holds goes back when that work is collected, with no products
        /// handed over.
        /// </summary>
        public bool Abandon(PhysicsCutRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.IsOver || !_requests.Contains(request))
            {
                return false;
            }

            if (request.work == null || _dispatcher.Cancel(request.ticket))
            {
                // Not offered, or taken back out of the queue before it began.
                request.work = null;
                request.ticket = default;
                End(request, PhysicsCutOutcomeKind.Abandoned, PhysicsCutStage.Abandoned);
                _requests.Remove(request);
                return true;
            }

            request.abandoning = true;
            return true;
        }

        /// <summary>
        /// Closes the runner: nothing is accepted after this, every cut the dispatcher has not submitted ends at once,
        /// and one it has is marked. The caller finishes the ordinary way — keep dispatching, or stop the dispatcher,
        /// and keep calling <see cref="Pump"/> until <see cref="IsDrained"/>.
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
                PhysicsCutRequest request = _requests[i];
                if (request.work == null || _dispatcher.Cancel(request.ticket))
                {
                    request.work = null;
                    request.ticket = default;
                    End(request, PhysicsCutOutcomeKind.Abandoned, PhysicsCutStage.Abandoned);
                    _requests.RemoveAt(i);
                    continue;
                }

                request.abandoning = true;
            }
        }

        // ----- one cut, one step -------------------------------------------------------------------------------------

        private void Advance(PhysicsCutRequest request)
        {
            if (request.IsOver)
            {
                return;
            }

            switch (request.Stage)
            {
                case PhysicsCutStage.Waiting:
                    if (_closed)
                    {
                        End(request, PhysicsCutOutcomeKind.Abandoned, PhysicsCutStage.Abandoned);
                        return;
                    }

                    TryOfferCut(request);
                    return;

                case PhysicsCutStage.Cutting:
                    TakeCut(request);
                    return;

                case PhysicsCutStage.Applying:
                    // The meshes are applied; only the offer of the bake is left, and it was refused last time.
                    OfferBake(request);
                    return;

                case PhysicsCutStage.Baking:
                    TakeBake(request);
                    return;
            }
        }

        /// <summary>
        /// Reserves what the kernel says this cut needs and offers the numerical work. A cut that cannot reserve, or
        /// whose offer the dispatcher refuses, waits here: neither is a refusal of the cut and nothing of it is lost.
        /// </summary>
        private void TryOfferCut(PhysicsCutRequest request)
        {
            if (request.arena == null)
            {
                if (_reserved >= _reservations || !TryReserve(request))
                {
                    return;
                }
            }

            var work = new CutWork(request);
            if (!_dispatcher.TryEnqueue(_purpose, work, out WorkTicket ticket))
            {
                // No place for it: nothing is scheduled, the reservation is kept, and it is offered again.
                return;
            }

            request.work = work;
            request.ticket = ticket;
            request.Stage = PhysicsCutStage.Cutting;
        }

        /// <summary>
        /// Takes what one cut needs, all of it or none: the arena, the writable mesh data, the meshes and the small
        /// arrays the job reports through. A failure part way gives back what was already taken.
        /// </summary>
        private bool TryReserve(PhysicsCutRequest request)
        {
            var capacity = new ConvexCutOwnerCapacity();
            ConvexCutOwnerKernel.QueryCapacity(in request.input, ref capacity);
            request.capacity = capacity;
            int meshes = math.max(1, capacity.OutputConvexCount);
            try
            {
                request.arena = new PhysicsCutArena(in capacity, request.input.convexCount);
                request.meshIds = new NativeArray<int>(meshes, Allocator.Persistent);
                request.meshBounds = new NativeArray<float3x2>(meshes, Allocator.Persistent);
                request.meshVertexCounts = new NativeArray<int>(meshes, Allocator.Persistent);
                request.bakeDone = new NativeArray<byte>(meshes, Allocator.Persistent);
                request.report = new NativeArray<PhysicsCutJobReport>(1, Allocator.Persistent);

                // Worst case: every split convex gives two sides, each of which becomes one collider mesh. The meshes
                // that are not used are destroyed when the numbers come back.
                request.meshData = Mesh.AllocateWritableMeshData(meshes);
                request.meshDataHeld = true;
                request.meshes = new Mesh[meshes];
                for (int m = 0; m < meshes; m++)
                {
                    request.meshes[m] = new Mesh { name = "Zantetsu Physics Cut " + m, hideFlags = HideFlags.HideAndDontSave };
                }
            }
            catch
            {
                // Ownership has not been settled, so everything this call took goes back before the failure leaves.
                ReleaseReservation(request, counted: false);
                throw;
            }

            _reserved++;
            return true;
        }

        /// <summary>
        /// Takes the numbers back once the dispatcher has collected them, applies the meshes on this thread and offers
        /// the bake. A work the dispatcher reported as failed or cancelled ends the cut here.
        /// </summary>
        private void TakeCut(PhysicsCutRequest request)
        {
            var work = (CutWork)request.work;
            if (work == null || !work.collected)
            {
                return;
            }

            request.work = null;
            request.ticket = default;
            if (!Accepted(request, work.completion, work.failure, out PhysicsCutOutcomeKind refused))
            {
                End(request, refused, refused == PhysicsCutOutcomeKind.Abandoned ? PhysicsCutStage.Abandoned : PhysicsCutStage.Finished);
                return;
            }

            PhysicsCutJobReport report = request.report[0];
            request.cut = report;
            request.KernelStatus = report.kernel.status;
            if (report.ranToEnd == 0)
            {
                // The work came back without the job having reached its end, so the report is not one to read: an
                // unwritten one is all zeros, which would otherwise look like a kernel that succeeded with nothing to
                // split. Nothing is handed over.
                End(request, PhysicsCutOutcomeKind.CookFailed, PhysicsCutStage.Finished);
                return;
            }

            if (report.kernel.status != ConvexCutOwnerStatus.Ok)
            {
                // A numerical failure is a numerical failure: nothing is published, nothing is replaced by another
                // shape, and the caller is told. Retiring a source, or aborting a transaction, is not connected here.
                End(request, PhysicsCutOutcomeKind.KernelFailed, PhysicsCutStage.Finished);
                return;
            }

            if (report.meshesComplete == 0)
            {
                // Every produced convex must have got its mesh, whatever the count was.
                End(request, PhysicsCutOutcomeKind.CookFailed, PhysicsCutStage.Finished);
                return;
            }

            try
            {
                // Every mesh data of the array is applied together, the unused ones included; those come out empty and
                // are destroyed below rather than being baked.
                Mesh.ApplyAndDisposeWritableMeshData(
                    request.meshData,
                    request.meshes,
                    MeshUpdateFlags.DontRecalculateBounds
                    | MeshUpdateFlags.DontValidateIndices
                    | MeshUpdateFlags.DontNotifyMeshUsers
                    | MeshUpdateFlags.DontResetBoneBounds);
                request.meshDataHeld = false;
            }
            catch
            {
                request.meshDataHeld = false;
                End(request, PhysicsCutOutcomeKind.CookFailed, PhysicsCutStage.Finished);
                throw;
            }

            for (int m = 0; m < request.meshes.Length; m++)
            {
                if (request.meshVertexCounts[m] <= 0)
                {
                    DestroyMesh(request.meshes[m]);
                    request.meshes[m] = null;
                    request.meshIds[m] = 0;
                    continue;
                }

                float3x2 bounds = request.meshBounds[m];
                float3 centre = (bounds.c0 + bounds.c1) * 0.5f;
                request.meshes[m].bounds = new Bounds(centre, bounds.c1 - bounds.c0);
                request.meshIds[m] = request.meshes[m].GetEntityId();
            }

            if (report.meshesWritten == 0)
            {
                // Nothing was produced -- every convex was inherited uncut -- so there is nothing to bake.
                Finish(request);
                return;
            }

            request.Stage = PhysicsCutStage.Applying;
            OfferBake(request);
        }

        private void OfferBake(PhysicsCutRequest request)
        {
            if (_closed)
            {
                End(request, PhysicsCutOutcomeKind.Abandoned, PhysicsCutStage.Abandoned);
                return;
            }

            var work = new BakeWork(request, _cooking);
            if (!_dispatcher.TryEnqueue(_purpose, work, out WorkTicket ticket))
            {
                // No place for it: nothing is scheduled, the meshes stay as they are, and it is offered again.
                return;
            }

            request.work = work;
            request.ticket = ticket;
            request.Stage = PhysicsCutStage.Baking;
        }

        private void TakeBake(PhysicsCutRequest request)
        {
            var work = (BakeWork)request.work;
            if (work == null || !work.collected)
            {
                return;
            }

            request.work = null;
            request.ticket = default;
            if (!Accepted(request, work.completion, work.failure, out PhysicsCutOutcomeKind refused))
            {
                End(request, refused, refused == PhysicsCutOutcomeKind.Abandoned ? PhysicsCutStage.Abandoned : PhysicsCutStage.Finished);
                return;
            }

            for (int m = 0; m < request.bakeDone.Length; m++)
            {
                if (request.bakeDone[m] == 0)
                {
                    // One element of the bake did not come back. What the cook made of the shape is not asked; that
                    // the call was made and returned for every mesh is, and it was not.
                    End(request, PhysicsCutOutcomeKind.CookFailed, PhysicsCutStage.Finished);
                    return;
                }
            }

            Finish(request);
        }

        /// <summary>
        /// Whether a collected work may be used: the dispatcher's own outcome is not ignored, and a cut the caller
        /// gave up on does not go on however the work ended.
        /// </summary>
        private static bool Accepted(
            PhysicsCutRequest request, WorkCompletion completion, Exception collecting, out PhysicsCutOutcomeKind refused)
        {
            if (collecting != null)
            {
                // Taking the work back threw. That is this submission's failure: nothing of it is used, and the cut
                // ends here so that everything it holds goes back.
                request.Failure = collecting;
                refused = PhysicsCutOutcomeKind.CookFailed;
                return false;
            }

            if (request.abandoning || completion.outcome == WorkOutcome.Cancelled)
            {
                refused = PhysicsCutOutcomeKind.Abandoned;
                return false;
            }

            if (completion.outcome == WorkOutcome.Failed)
            {
                request.Failure = completion.failure;
                refused = PhysicsCutOutcomeKind.CookFailed;
                return false;
            }

            refused = PhysicsCutOutcomeKind.Pending;
            return true;
        }

        /// <summary>Builds the products: which convex went where, what is borrowed and what was made and baked.</summary>
        private void Finish(PhysicsCutRequest request)
        {
            var outcomes = new ConvexCutOutcome[request.input.convexCount];
            unsafe
            {
                for (int c = 0; c < outcomes.Length; c++)
                {
                    outcomes[c] = request.arena.Output.outcomes[c];
                }
            }

            var products = new PhysicsCutProducts(
                request.arena, outcomes, request.cut.kernel, request.localToOwner, _cooking);
            int mesh = 0;
            for (int c = 0; c < outcomes.Length; c++)
            {
                ConvexCutOutcome outcome = outcomes[c];
                if (!outcome.IsSplit)
                {
                    // Inherited uncut: the input convex itself, with its existing cooked shape. Nothing is made for it
                    // and nothing of it is this cut's to give back.
                    unsafe
                    {
                        products.Add(
                            outcome.InheritsPositive,
                            new PhysicsCutPart(
                                c, true, request.input.convexes[c], null,
                                new float3x2(new float3(float.NaN), new float3(float.NaN))));
                    }

                    continue;
                }

                products.Add(true, new PhysicsCutPart(c, false, outcome.positive, Take(request, ref mesh, out float3x2 positiveBox), positiveBox));
                products.Add(false, new PhysicsCutPart(c, false, outcome.negative, Take(request, ref mesh, out float3x2 negativeBox), negativeBox));
            }

            // The arena and the meshes are the products' from here; what is left is only this cut's working set.
            request.arena = null;
            request.meshes = null;
            _reserved--;
            ReleaseWorkingSet(request);
            request.Products = products;
            request.Outcome = PhysicsCutOutcomeKind.Ok;
            request.Stage = PhysicsCutStage.Finished;
        }

        /// <summary>
        /// The next mesh the job really filled, in the order the job wrote them, with the box the job measured for it.
        /// </summary>
        private static Mesh Take(PhysicsCutRequest request, ref int mesh, out float3x2 bounds)
        {
            while (mesh < request.meshes.Length && request.meshes[mesh] == null)
            {
                mesh++;
            }

            if (mesh >= request.meshes.Length)
            {
                bounds = new float3x2(new float3(float.NaN), new float3(float.NaN));
                return null;
            }

            bounds = request.meshBounds[mesh];
            return request.meshes[mesh++];
        }

        /// <summary>Ends one cut without products, giving back everything it holds, exactly once.</summary>
        private void End(PhysicsCutRequest request, PhysicsCutOutcomeKind outcome, PhysicsCutStage stage)
        {
            // A job that is still running is never interrupted: it is waited for here, on the main thread, before
            // anything it reads or writes goes back.
            CompleteAnyJob(request);
            ReleaseReservation(request, counted: true);
            request.work = null;
            request.ticket = default;
            request.Outcome = outcome;
            request.Stage = stage;
        }

        private static void CompleteAnyJob(PhysicsCutRequest request)
        {
            switch (request.work)
            {
                case CutWork cut:
                    cut.CompleteIfScheduled();
                    break;
                case BakeWork bake:
                    bake.CompleteIfScheduled();
                    break;
            }
        }

        /// <summary>Gives back everything one cut reserved. Counted only when the reservation was really counted.</summary>
        private void ReleaseReservation(PhysicsCutRequest request, bool counted)
        {
            if (request.meshDataHeld)
            {
                request.meshData.Dispose();
                request.meshDataHeld = false;
            }

            if (request.meshes != null)
            {
                for (int m = 0; m < request.meshes.Length; m++)
                {
                    DestroyMesh(request.meshes[m]);
                    request.meshes[m] = null;
                }

                request.meshes = null;
            }

            if (request.arena != null)
            {
                request.arena.Dispose();
                request.arena = null;
                if (counted)
                {
                    _reserved--;
                }
            }

            ReleaseWorkingSet(request);
        }

        private static void ReleaseWorkingSet(PhysicsCutRequest request)
        {
            if (request.meshIds.IsCreated)
            {
                request.meshIds.Dispose();
            }

            if (request.meshBounds.IsCreated)
            {
                request.meshBounds.Dispose();
            }

            if (request.meshVertexCounts.IsCreated)
            {
                request.meshVertexCounts.Dispose();
            }

            if (request.bakeDone.IsCreated)
            {
                request.bakeDone.Dispose();
            }

            if (request.report.IsCreated)
            {
                request.report.Dispose();
            }
        }

        internal static void DestroyMesh(Mesh mesh)
        {
            if (mesh == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(mesh);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(mesh);
            }
        }

        /// <summary>
        /// The numerical stage as the dispatcher sees it: it is scheduled in <see cref="Begin"/>, which the dispatcher
        /// calls when it has the room and the budget for it, asked whether it has finished without blocking, and
        /// collected once on the main thread. What the collection means for the cut is the next pump's business, so
        /// that no mesh is applied and nothing is published inside a dispatch.
        /// </summary>
        private sealed class CutWork : IDispatchWork
        {
            private readonly PhysicsCutRequest _request;
            private JobHandle _handle;
            private bool _scheduled;
            internal bool collected;
            internal WorkCompletion completion;
            internal Exception failure;

            internal CutWork(PhysicsCutRequest request)
            {
                _request = request;
            }

            public void Begin()
            {
                if (_request.skipCutJob)
                {
                    return;
                }

                var job = new ConvexCutAndMeshJob
                {
                    input = _request.input,
                    output = _request.arena.Output,
                    meshData = _request.meshData,
                    bounds = _request.meshBounds,
                    vertexCounts = _request.meshVertexCounts,
                    report = _request.report,
                };
                _handle = job.Schedule();
                _scheduled = true;
                _request.scheduled++;
            }

            public bool IsComplete => !_scheduled || _handle.IsCompleted;

            public void Collect(WorkCompletion completion)
            {
                // Whatever happens here, the collection is recorded: the dispatcher has already let this work go and
                // will not bring it back, so a throw that left this unrecorded would strand the cut with its
                // reservation held. A throw is this submission's failure, not its success.
                this.completion = completion;
                try
                {
                    // The job's own completion, which is what gives its writes back to this thread.
                    CompleteIfScheduled();
                    _request.collectHook?.Invoke();
                }
                catch (Exception thrown)
                {
                    failure = thrown;
                }
                finally
                {
                    collected = true;
                }
            }

            internal void CompleteIfScheduled()
            {
                if (_scheduled)
                {
                    // Cleared first: a completion that throws must not leave this looking schedulable again.
                    _scheduled = false;
                    _handle.Complete();
                }
            }
        }

        /// <summary>The cook stage, on the same terms: scheduled in Begin, collected once on the main thread.</summary>
        private sealed class BakeWork : IDispatchWork
        {
            private readonly PhysicsCutRequest _request;
            private readonly MeshColliderCookingOptions _cooking;
            private JobHandle _handle;
            private bool _scheduled;
            internal bool collected;
            internal WorkCompletion completion;
            internal Exception failure;

            internal BakeWork(PhysicsCutRequest request, MeshColliderCookingOptions cooking)
            {
                _request = request;
                _cooking = cooking;
            }

            public void Begin()
            {
                var job = new BakeJob { ids = _request.meshIds, done = _request.bakeDone, cooking = _cooking };
                _handle = job.Schedule(_request.meshIds.Length, 1);
                _scheduled = true;
                _request.scheduled++;
            }

            public bool IsComplete => !_scheduled || _handle.IsCompleted;

            public void Collect(WorkCompletion completion)
            {
                this.completion = completion;
                try
                {
                    CompleteIfScheduled();
                    _request.collectHook?.Invoke();
                }
                catch (Exception thrown)
                {
                    failure = thrown;
                }
                finally
                {
                    collected = true;
                }
            }

            internal void CompleteIfScheduled()
            {
                if (_scheduled)
                {
                    _scheduled = false;
                    _handle.Complete();
                }
            }
        }
    }
}
