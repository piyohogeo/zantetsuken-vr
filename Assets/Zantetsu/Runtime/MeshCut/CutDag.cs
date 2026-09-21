using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut
{
    /// <summary>How far one admitted cut's geometry has come in the DAG.</summary>
    public enum CutGeometryStage
    {
        /// <summary>Registered, waiting for the geometry it is to be cut from: its ancestor's committed geometry.</summary>
        WaitingForBasis = 0,

        /// <summary>Its basis is known and the kernel is running, or waiting for its turn to run.</summary>
        Running = 1,

        /// <summary>
        /// Its result is known and nothing outside the storage has changed yet: the two sides the kernel produced, or
        /// the two empty sides a cut of an empty geometry has.
        /// </summary>
        CpuPublished = 2,

        /// <summary>Committed: the two sides are the branch's geometry and the operation's geometry duty is done.</summary>
        Committed = 3,

        /// <summary>
        /// Over without a commit: the branch no longer needs it, the operation ended elsewhere, the kernel failed, or
        /// the DAG was closed. Nothing was published and everything it held has gone back.
        /// </summary>
        Reclaimed = 4,
    }

    /// <summary>
    /// What a geometry commit is asked to publish (DESIGN 4.5.6): one operation's two sides as the kernel produced
    /// them, with the logical fragments they belong to and the plane they came from. Both sides are empty where the
    /// geometry being cut was itself empty, which is an ordinary result and not a failure.
    /// </summary>
    public readonly struct CutGeometryCommit
    {
        public readonly CutOperationId operation;
        public readonly LogicalFragmentId source;
        public readonly LogicalFragmentId positiveFragment;
        public readonly LogicalFragmentId negativeFragment;
        public readonly float4 plane;
        public readonly VpStorageCutSide positive;
        public readonly VpStorageCutSide negative;

        /// <summary>
        /// Cap triangles the kernel really made for this cut. This is the evidence that the cut has a surface
        /// boundary at all: zero means there is none to record (DESIGN 4.5.6, 8).
        /// </summary>
        public readonly int capTriangles;

        internal CutGeometryCommit(
            CutOperationId operation,
            LogicalFragmentId source,
            LogicalFragmentId positiveFragment,
            LogicalFragmentId negativeFragment,
            float4 plane,
            VpStorageCutSide positive,
            VpStorageCutSide negative,
            int capTriangles)
        {
            this.capTriangles = capTriangles;
            this.operation = operation;
            this.source = source;
            this.positiveFragment = positiveFragment;
            this.negativeFragment = negativeFragment;
            this.plane = plane;
            this.positive = positive;
            this.negative = negative;
        }

        /// <summary>Whether neither side has geometry, because what was cut had none.</summary>
        public bool IsEmpty => positive.IsEmpty && negative.IsEmpty;
    }

    /// <summary>
    /// What a geometry commit established: the geometry each side is from now on, which is what a later cut of that
    /// side reads. A side the cut left empty has none, and none is expected.
    /// </summary>
    public struct CutGeometryCommitted
    {
        public VpStoredGeometry positive;
        public VpStoredGeometry negative;
    }

    /// <summary>
    /// The one boundary at which display geometry changes state (DESIGN 4.5.6). Everything a commit really involves —
    /// the transfer to the GPU, the display's new geometry and remaining temporary set, the boundary records — lives
    /// behind this, and none of it is a condition of anything here: a commit either was established, with the geometry
    /// each side now has, or was not, and one that was not is simply asked again at a later opportunity.
    /// </summary>
    public interface ICutGeometryCommit
    {
        /// <summary>
        /// Publishes one operation's geometry as one consistent state, on the main thread. False means it was not
        /// established and nothing changed; the same commit is offered again later, so this must not half-publish.
        /// </summary>
        bool TryCommit(in CutGeometryCommit commit, out CutGeometryCommitted committed);
    }

    /// <summary>One cut's geometry ending in a way that is not an ordinary outcome.</summary>
    public readonly struct CutGeometryFault
    {
        public readonly CutOperationId operation;
        public readonly LogicalFragmentId source;

        /// <summary>What the cut route reported. Never <see cref="VpStorageCutStatus.Ok"/>.</summary>
        public readonly VpStorageCutStatus status;

        /// <summary>What a worker threw, where one did; null otherwise.</summary>
        public readonly Exception failure;

        internal CutGeometryFault(CutOperationId operation, LogicalFragmentId source, VpStorageCutStatus status, Exception failure)
        {
            this.operation = operation;
            this.source = source;
            this.status = status;
            this.failure = failure;
        }

        public override string ToString()
        {
            return operation + " geometry ended as " + status + (failure == null ? string.Empty : ": " + failure.Message);
        }
    }

    /// <summary>
    /// Where a geometry failure is reported (DESIGN 4.5.6, and chapter 4's common termination). The shared geometry of
    /// a cut failing is not an ordinary outcome for the DAG to absorb: it is handed to the caller, once for the cut it
    /// happened to, so that the caller can take it to that termination its own way. It is never read as a physics
    /// failure and never retires the source.
    /// </summary>
    public interface ICutGeometryFault
    {
        /// <summary>Reports one cut's geometry failure, on the main thread, once.</summary>
        void GeometryFailed(in CutGeometryFault fault);
    }

    /// <summary>
    /// The thin piece that runs the cut DAG: it holds what an admitted cut needs — its work, its input and its result
    /// — offers each cut's kernel when what it depends on is there, takes the results back on the main thread, and
    /// checks the conditions the two publications have before anything leaves it (DESIGN 4.2, 4.3, 4.4, 4.5.6, 7.1, 8).
    /// <para>
    /// **What is whose.** The logical parents, children and operation states are the ledger's, and nothing is kept
    /// here that the ledger already knows. Where the work runs is the dispatcher's, and when it runs is the caller's:
    /// the caller owns the dispatcher, opens the frames and calls <see cref="Pump"/>. What a commit really does is the
    /// commit's own, behind <see cref="ICutGeometryCommit"/>, and a geometry failure is the caller's, through
    /// <see cref="ICutGeometryFault"/>. This adds no second ledger, no permanent id of its own and no general node or
    /// event machinery.
    /// </para>
    /// <para>
    /// **The two dependencies, kept apart.** Physics and logical publication never wait for geometry: a cut is
    /// admitted, its anchors are distributed, its final physics succeeds and its two children are published while its
    /// geometry is still being computed, and a published child can be cut again at once. Geometry waits for its
    /// branch: the kernel of a cut of A's child reads A's *committed* geometry, never A's uncommitted output, so it
    /// is offered only after A has committed — and once A has, the cuts that were waiting for it become ready in that
    /// same update. A geometry that finishes first waits: it is not committed before its own operation is published.
    /// Work finishing, the storage's own publication and the geometry commit are three different things, and the
    /// first is never taken for the last.
    /// </para>
    /// <para>
    /// **What the result belongs to.** From the moment the kernel has produced two sides until the commit takes them
    /// over, those sides are this cut's own: if the cut ends without a commit, each *produced* side's index range is
    /// retired here, exactly once, so the space goes back to the storage. That covers a result the runner had already
    /// finished but this had not taken yet. A side that is the input borrowed back is nobody's to retire, and an empty
    /// side has nothing to retire.
    /// </para>
    /// <para>
    /// **The two planes** (DESIGN 5.2). An adopted plane is a value in its source fragment's own logical frame, and
    /// that is what the ledger keeps, what a commit publishes and what a boundary record holds. The kernel cuts in the
    /// coordinates of the committed geometry it reads, which is a different frame, so the same plane is carried into
    /// that frame here — once, when the cut is offered, with the conversion that already exists — and used for the
    /// kernel alone. Neither the ledger's value nor the commit's is overwritten by the converted one. The mapping
    /// comes from the caller with the base geometry, the same mapping the caller gives the display's registration;
    /// nothing here reads a renderer's state to find it, and a geometry whose mapping is not known is not cut with a
    /// made-up identity.
    /// </para>
    /// <para>
    /// **Authority after publication** (DESIGN 8). That an operation was published is not on its own the right to
    /// adopt a result that arrives later. A result is adopted while its branch still has a live fragment to read it
    /// and the basis it was cut from is still the source's current geometry. A cut of a descendant, or another
    /// fragment's change, never invalidates an ancestor's geometry; one branch retiring does not stop the ancestor
    /// work the other branch still needs; and when no branch needs it any more the cut ends wherever it had got to —
    /// waiting for its basis, running, or holding its result — rather than being carried on for the record.
    /// </para>
    /// </summary>
    public sealed class CutDag : IDisposable, IMainThreadPump
    {
        private readonly LogicalCutLedger _ledger;
        private readonly ICutGeometryCommit _commit;
        private readonly ICutGeometryFault _fault;
        private readonly VpCpuGeometryStorage _storage;
        private readonly VpAsyncStorageCut _cuts;
        private readonly List<Node> _nodes = new List<Node>();
        private readonly Dictionary<LogicalFragmentId, VpStoredGeometry> _geometryOf =
            new Dictionary<LogicalFragmentId, VpStoredGeometry>();
        private readonly Dictionary<LogicalFragmentId, Matrix4x4> _frameOf =
            new Dictionary<LogicalFragmentId, Matrix4x4>();
        private readonly HashSet<LogicalFragmentId> _withoutGeometry = new HashSet<LogicalFragmentId>();
        private readonly Dictionary<CutOperationId, VpStorageCutStatus> _faults =
            new Dictionary<CutOperationId, VpStorageCutStatus>();
        private bool _closed;

        /// <summary>
        /// Takes the storage the geometry lives in, the ledger that owns the logical state, the dispatcher the work
        /// runs through, the commit this connects to and where a geometry failure is reported. The dispatcher stays
        /// the caller's; the asynchronous cut runner is this one's own.
        /// </summary>
        public CutDag(
            VpCpuGeometryStorage storage,
            LogicalCutLedger ledger,
            SharedWorkDispatcher dispatcher,
            ICutGeometryCommit commit,
            ICutGeometryFault fault,
            WorkPurpose purpose = WorkPurpose.AdmittedGeometry)
        {
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _commit = commit ?? throw new ArgumentNullException(nameof(commit));
            _fault = fault ?? throw new ArgumentNullException(nameof(fault));
            _cuts = new VpAsyncStorageCut(storage, dispatcher, purpose);
        }

        /// <summary>How many admitted cuts still have geometry work of their own here.</summary>
        public int ActiveCount => _nodes.Count;

        /// <summary>Whether everything this held has gone back, which a closed DAG reaches by being pumped.</summary>
        public bool IsDrained => _nodes.Count == 0 && _cuts.ActiveCount == 0;

        /// <summary>
        /// Says which geometry a fragment is, and in which coordinates, for the first fragment of a branch: the
        /// geometry it was registered with before anything was cut (DESIGN 4.5.6, the registered base geometry of the
        /// first cut). Every later fragment gets both from the commit of the cut that produced it, and is never
        /// registered here.
        /// </summary>
        /// <param name="lineageToGeometryLocal">
        /// The mapping from the lineage's common logical frame — the frame an adopted plane is in — to this geometry's
        /// own coordinates. This is the caller's to give, and is the same mapping the caller registers the display
        /// with, so that the plane a cut is temporarily shown at and the plane its kernel really cuts at are the one
        /// plane. Nothing here reads it from a renderer.
        /// </param>
        public void RegisterBaseGeometry(
            LogicalFragmentId fragment, VpStoredGeometry geometry, Matrix4x4 lineageToGeometryLocal)
        {
            if (!fragment.IsSet)
            {
                throw new ArgumentException("not a fragment", nameof(fragment));
            }

            _geometryOf[fragment] = geometry;
            _frameOf[fragment] = lineageToGeometryLocal;
            _withoutGeometry.Remove(fragment);
        }

        /// <summary>
        /// The same, for a caller whose logical frame **is** the geometry's own coordinates. That is the whole of what
        /// this overload says: it is the identity mapping asserted, not a mapping left out. A caller whose two frames
        /// differ registers the mapping instead — one that is not registered is never guessed at here.
        /// </summary>
        public void RegisterBaseGeometry(LogicalFragmentId fragment, VpStoredGeometry geometry)
        {
            RegisterBaseGeometry(fragment, geometry, Matrix4x4.identity);
        }

        /// <summary>
        /// The frame the geometry a fragment is cut in now is expressed in, if that fragment has one. A side an
        /// earlier cut left empty keeps its frame too: what it is cut from next is still read in these coordinates.
        /// </summary>
        public bool TryGetGeometryFrame(LogicalFragmentId fragment, out Matrix4x4 lineageToGeometryLocal)
        {
            return _frameOf.TryGetValue(fragment, out lineageToGeometryLocal);
        }

        /// <summary>The geometry a fragment is cut from now, if it has one.</summary>
        public bool TryGetGeometry(LogicalFragmentId fragment, out VpStoredGeometry geometry)
        {
            return _geometryOf.TryGetValue(fragment, out geometry);
        }

        /// <summary>
        /// Whether a fragment is known to have no geometry at all: a side an earlier cut left empty. That is an
        /// ordinary state — its physics child is there as usual — and a cut of it produces two empty sides.
        /// </summary>
        public bool HasNoGeometry(LogicalFragmentId fragment)
        {
            return _withoutGeometry.Contains(fragment);
        }

        /// <summary>
        /// Admits one cut through the ledger and, when it is admitted, registers the geometry work that belongs to it.
        /// The admission rules are the ledger's and are not softened here: a cut of a source whose earlier operation
        /// is not published yet is passed over, and nothing of it is kept to be tried again later. The anchors are the
        /// caller's to distribute before publication (DESIGN 7.1).
        /// </summary>
        /// <param name="plane">
        /// The plane in the source fragment's own logical frame (DESIGN 5.2). It is kept as it is: this is the value
        /// the ledger adopts and the value a commit publishes, and the kernel's own is made from it when the cut is
        /// offered, without replacing it.
        /// </param>
        public LogicalCutAdmission TryAdmit(
            LogicalFragmentId source,
            float4 plane,
            bool splitsBothSides,
            out CutOperationId operation)
        {
            if (_closed)
            {
                throw new ObjectDisposedException(nameof(CutDag));
            }

            LogicalCutAdmission admission = _ledger.Admit(source, plane, splitsBothSides, out operation);
            if (admission != LogicalCutAdmission.Admitted)
            {
                return admission;
            }

            _nodes.Add(new Node { operation = operation, source = source, plane = plane });
            return admission;
        }

        /// <summary>
        /// The final physics of an admitted cut succeeded: its operation and its two children are published through
        /// the ledger, on this same main thread update. Geometry takes no part in this and is not waited for, and the
        /// published children can be cut again at once even though neither has geometry yet.
        /// </summary>
        public LogicalCutResultOutcome PublishAfterFinalPhysics(
            CutOperationId operation,
            out LogicalFragmentId positive,
            out LogicalFragmentId negative)
        {
            return _ledger.Publish(operation, out positive, out negative);
        }

        /// <summary>
        /// The final physics of an admitted cut could not be established (DESIGN 7.1.3): the operation is aborted and
        /// its source retired through the ledger. Its geometry work, if any is running, is given up and everything it
        /// holds comes back when it is collected; nothing of it is published.
        /// </summary>
        public LogicalCutResultOutcome AbortAfterFinalPhysics(CutOperationId operation)
        {
            return _ledger.Abort(operation);
        }

        /// <summary>
        /// Moves the DAG as far as it can on the main thread: takes back what the workers finished, commits what may
        /// be committed, and offers what has become ready — including, in this same call, the cuts that were waiting
        /// for a commit that has just happened. Nothing here waits for anything.
        /// <para>
        /// The cuts' own runner is pumped **again** whenever a node moved, because a commit here makes a child's cut
        /// and that child has to be given its reservation and offered to be of any use this frame. Pumped only once at
        /// the top, a child made below it would need a **further call** to this before it was reserved and offered --
        /// which a caller may well make in the same frame, and which it then has to make. Doing it here is what makes
        /// one call enough.
        /// </para>
        /// <para>
        /// Returns whether anything really moved, so that a caller driving a frame knows whether to collect again.
        /// </para>
        /// <para>
        /// After <see cref="Dispose"/> this still works and must still be called: nothing is offered or committed any
        /// more, and what a worker still holds is taken back and given up as it arrives, until <see cref="IsDrained"/>.
        /// </para>
        /// </summary>
        public bool Pump()
        {
            bool any = false;
            try
            {
                // Ancestors first, and again while anything moved: a commit here can make the cuts of its children
                // ready, and DESIGN 4.5.6 allows those to be offered in the same update rather than a frame later.
                bool moved = true;
                while (moved)
                {
                    // The runner first and again each time round: a child's cut made below is reserved and offered
                    // here, in this call, rather than at the next one.
                    moved = _cuts.Pump();
                    for (int i = 0; i < _nodes.Count; i++)
                    {
                        moved |= Advance(_nodes[i]);
                    }

                    any |= moved;
                }
            }
            finally
            {
                // Whatever ended is let go here even when something on the way out threw — a caller's failure
                // notification may — so that nothing that is already over is still counted or looked at again.
                for (int i = _nodes.Count - 1; i >= 0; i--)
                {
                    if (_nodes[i].over)
                    {
                        _nodes.RemoveAt(i);
                        any = true;
                    }
                }
            }

            return any;
        }

        /// <summary>
        /// How far one admitted cut's geometry has come. A cut this no longer holds is read from the ledger instead:
        /// an operation whose geometry duty completed was committed, and any other ended without a commit.
        /// </summary>
        public CutGeometryStage StageOf(CutOperationId operation)
        {
            Node node = Find(operation);
            if (node != null)
            {
                return node.stage;
            }

            return _ledger.TryGetOperation(operation, out LogicalCutOperation record)
                && record.state == LogicalCutOperationState.Completed
                ? CutGeometryStage.Committed
                : CutGeometryStage.Reclaimed;
        }

        /// <summary>
        /// How a cut's geometry failed, where it did, kept after the cut itself is gone.
        /// <see cref="VpStorageCutStatus.Ok"/> means it did not fail: it was committed, or it ended because its branch
        /// no longer needed it. Every failure is also reported to <see cref="ICutGeometryFault"/> as it happens; this
        /// is only the record of it.
        /// </summary>
        public VpStorageCutStatus FailureOf(CutOperationId operation)
        {
            return _faults.TryGetValue(operation, out VpStorageCutStatus status) ? status : VpStorageCutStatus.Ok;
        }

        /// <summary>The geometry one cut's kernel was given to read, for the tests that check the ancestor order.</summary>
        internal VpStoredGeometry BasisOf(CutOperationId operation)
        {
            Node node = Find(operation);
            return node == null ? default : node.basis;
        }

        /// <summary>
        /// The plane one cut's kernel was really given, in the basis geometry's own coordinates, for the tests about
        /// the conversion. Set once the cut has been offered.
        /// </summary>
        internal bool TryGetKernelPlane(CutOperationId operation, out float4 plane)
        {
            Node node = Find(operation);
            if (node == null || node.stage == CutGeometryStage.WaitingForBasis || node.basisIsEmpty)
            {
                // Not offered yet, or a cut of an empty geometry, which has no kernel and so no plane of its own.
                plane = default;
                return false;
            }

            plane = node.kernelPlane;
            return true;
        }

        /// <summary>
        /// The cut one operation's geometry is running, for the tests about what it holds while it runs: its
        /// reservation of the storage's room, and the input it is reading. Null once the cut is over or was given up.
        /// </summary>
        internal VpStorageCutRequest RequestOf(CutOperationId operation)
        {
            Node node = Find(operation);
            return node?.request;
        }

        /// <summary>The result a cut is holding before it is committed or given back, for the tests about ownership.</summary>
        internal bool TryGetResultBeforeCommit(CutOperationId operation, out VpStorageCutResult result)
        {
            Node node = Find(operation);
            if (node == null || node.stage != CutGeometryStage.CpuPublished)
            {
                result = default;
                return false;
            }

            result = node.cut;
            return true;
        }

        /// <summary>
        /// Closes the DAG: no cut is admitted after this, nothing more is offered or committed, and every cut gives
        /// its work up. What a worker is already running is never interrupted, so the caller finishes the ordinary
        /// way — keep dispatching, or stop the dispatcher, and keep calling <see cref="Pump"/> until
        /// <see cref="IsDrained"/>, which is also when the produced geometry of the cuts that did not commit has gone
        /// back to the storage — and only then is the storage safe to dispose. Closing twice does nothing.
        /// </summary>
        public void Dispose()
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            for (int i = 0; i < _nodes.Count; i++)
            {
                Node node = _nodes[i];
                if (node.request != null && !node.request.IsOver)
                {
                    _cuts.Abandon(node.request);
                }
            }

            _cuts.Dispose();
        }

        // ----- one cut, one step -------------------------------------------------------------------------------------

        private bool Advance(Node node)
        {
            if (node.over)
            {
                return false;
            }

            if (!_ledger.TryGetOperation(node.operation, out LogicalCutOperation record))
            {
                return Reclaim(node, terminate: false);
            }

            bool published = record.state == LogicalCutOperationState.Published;
            if (!published && record.state != LogicalCutOperationState.Admitted)
            {
                // Ended by somebody else: aborted before publication, retired with its source, or already finished.
                // There is nothing left to publish and nothing to give the ledger back.
                return Reclaim(node, terminate: false);
            }

            if (_closed)
            {
                return TakeBackWhileClosed(node);
            }

            if (published && !BranchHasAReader(record.positive) && !BranchHasAReader(record.negative))
            {
                // Nothing on either side reads this geometry any more. Wherever the cut had got to — waiting for its
                // basis, running, or holding its two sides — it ends here rather than being carried on for the record.
                return Reclaim(node, terminate: true);
            }

            if (node.terminateWhenPublished)
            {
                // Its geometry is over, and the ledger only takes that notice once the operation is published.
                return published && Reclaim(node, terminate: true);
            }

            bool moved = false;
            if (node.stage == CutGeometryStage.WaitingForBasis)
            {
                moved |= TryStart(node);
            }

            if (node.stage == CutGeometryStage.Running)
            {
                moved |= TryTakeResult(node);
            }

            if (node.stage == CutGeometryStage.CpuPublished)
            {
                moved |= TryCommit(node, published, record);
            }

            return moved;
        }

        /// <summary>
        /// Offers the kernel once the geometry it reads is there. That geometry is the source's **committed** one, so
        /// a cut of a child of A waits here until A has committed, and A's uncommitted output is never borrowed. A
        /// source an earlier commit settled as empty needs no kernel at all: the cut of an empty geometry is two empty
        /// sides, an ordinary result that travels on to the children through the ordinary commit, in the ordinary
        /// order, with no dummy geometry anywhere.
        /// <para>
        /// This is also where the adopted plane becomes the kernel's plane, and the only place it does: the basis and
        /// the frame that basis is in are read together, here, at the moment the cut is really offered — so a cut
        /// admitted long before its ancestor committed takes the frame the commit left, not one from admission time —
        /// and the conversion happens once. A basis whose frame is not known, or a plane that will not convert into
        /// it, is a cut that cannot be run rather than one run at some other plane.
        /// </para>
        /// </summary>
        private bool TryStart(Node node)
        {
            // Declared up front because a source settled as empty never looks one up, and the two paths join below.
            VpStoredGeometry basis = default;
            bool empty = _withoutGeometry.Contains(node.source);
            if (!empty && !_geometryOf.TryGetValue(node.source, out basis))
            {
                return false;
            }

            if (!_frameOf.TryGetValue(node.source, out Matrix4x4 frame))
            {
                // Its geometry is there and the coordinates it is in are not. Nothing is filled in for that.
                return Fail(node, VpStorageCutStatus.InvalidInput, null);
            }

            node.basisFrame = frame;
            if (empty)
            {
                node.basisIsEmpty = true;
                node.cut = new VpStorageCutResult { status = VpStorageCutStatus.Ok };
                node.stage = CutGeometryStage.CpuPublished;
                return true;
            }

            // The adopted plane lives in the lineage's logical frame; the kernel reads the geometry's own. The one
            // conversion, with the existing one, leaving node.plane — what the ledger adopted and what the commit
            // will publish — exactly as it is.
            if (!VpCutPlane.TryGeometryLocalToWorld(node.plane, frame, out float4 kernelPlane))
            {
                return Fail(node, VpStorageCutStatus.InvalidInput, null);
            }

            if (!VpStorageCutInput.TryAcquire(_storage, basis, out VpStorageCutInput input))
            {
                return Fail(node, VpStorageCutStatus.InvalidInput, null);
            }

            node.basis = basis;
            node.kernelPlane = kernelPlane;
            node.request = _cuts.Submit(input, kernelPlane, default);
            node.stage = CutGeometryStage.Running;
            return true;
        }

        private bool TryTakeResult(Node node)
        {
            VpStorageCutRequest request = node.request;
            if (request == null || !request.IsOver)
            {
                return false;
            }

            node.request = null;
            if (request.Stage == VpStorageCutStage.Abandoned)
            {
                return Reclaim(node, terminate: true);
            }

            VpStorageCutResult result = request.Result;
            if (result.status != VpStorageCutStatus.Ok)
            {
                // A geometry failure is a geometry failure (DESIGN 8): the source is not retired for it, no substitute
                // is guessed, and the physics side is left exactly as it is. It does go to the caller, though.
                return Fail(node, result.status, request.Failure);
            }

            node.cut = result;
            node.holdsProduced = true;
            node.stage = CutGeometryStage.CpuPublished;
            return true;
        }

        /// <summary>
        /// Publishes one cut's geometry, when everything that must hold does: its own operation is published, and the
        /// geometry it was cut from is still what the source is. The commit itself may say it was not established, and
        /// then nothing has changed and it is offered again later.
        /// </summary>
        private bool TryCommit(Node node, bool published, LogicalCutOperation record)
        {
            if (!published)
            {
                // Geometry first is ordinary; it simply waits for its own physics and logical publication.
                return false;
            }

            // A basis is the geometry **and** the frame it was read in: a result cut from the right geometry in a
            // frame the source no longer has is as much the wrong result as one cut from a replaced geometry. The
            // matrices are compared value by value, because Matrix4x4's own equality is approximate.
            bool basisStillCurrent = node.basisIsEmpty
                ? _withoutGeometry.Contains(node.source) && CurrentFrameIs(node)
                : _geometryOf.TryGetValue(node.source, out VpStoredGeometry current)
                    && current.Equals(node.basis)
                    && CurrentFrameIs(node);
            if (!basisStillCurrent)
            {
                // What it was cut from is no longer what the source is. Adopting it would put the display back onto
                // geometry that has been replaced.
                return Reclaim(node, terminate: true);
            }

            var commit = new CutGeometryCommit(
                node.operation, node.source, record.positive, record.negative, node.plane, node.cut.positive,
                node.cut.negative, node.cut.kernel.capTriangles);
            if (!_commit.TryCommit(in commit, out CutGeometryCommitted committed))
            {
                return false;
            }

            // The sides are the commit's from here: what it does with them, and what it hands each child, is its own.
            node.holdsProduced = false;
            _geometryOf.Remove(node.source);
            _frameOf.Remove(node.source);
            _withoutGeometry.Remove(node.source);
            Settle(record.positive, node.cut.positive, committed.positive, node.basisFrame);
            Settle(record.negative, node.cut.negative, committed.negative, node.basisFrame);
            node.stage = CutGeometryStage.Committed;
            node.over = true;

            // The one notice the ledger takes, once: this operation's geometry duty is done.
            _ledger.CompleteGeometry(node.operation);
            return true;
        }

        /// <summary>
        /// What one side is from the commit on. The frame travels with it: this commit does not rebuild a child's
        /// coordinates — a produced side is written in the coordinates its basis was read in, and a side that is the
        /// input borrowed back keeps the very geometry — so a later cut of this side is read in the same frame. An
        /// empty side keeps it too, so that the correspondence is not lost where a branch is empty for a while.
        /// </summary>
        private void Settle(
            LogicalFragmentId fragment, VpStorageCutSide side, VpStoredGeometry committed, Matrix4x4 frame)
        {
            if (!fragment.IsSet)
            {
                return;
            }

            _frameOf[fragment] = frame;
            if (side.IsEmpty)
            {
                _geometryOf.Remove(fragment);
                _withoutGeometry.Add(fragment);
                return;
            }

            _geometryOf[fragment] = committed;
            _withoutGeometry.Remove(fragment);
        }

        /// <summary>
        /// Whether anything on this branch still reads the geometry: the fragment itself while it is live, or, once it
        /// has been replaced by a cut of its own, either of that cut's children. A retired branch reads nothing, and
        /// one branch retiring says nothing about the other.
        /// </summary>
        private bool BranchHasAReader(LogicalFragmentId fragment)
        {
            if (!fragment.IsSet || !_ledger.TryGetFragmentState(fragment, out LogicalFragmentState state))
            {
                return false;
            }

            switch (state)
            {
                case LogicalFragmentState.Live:
                    return true;

                case LogicalFragmentState.Replaced:
                    if (!_ledger.TryGetReplacingOperation(fragment, out CutOperationId replacing)
                        || !_ledger.TryGetOperation(replacing, out LogicalCutOperation record))
                    {
                        return false;
                    }

                    return BranchHasAReader(record.positive) || BranchHasAReader(record.negative);

                default:
                    return false;
            }
        }

        /// <summary>After the close: take back what a worker had, give the geometry back, and touch no ledger state.</summary>
        private bool TakeBackWhileClosed(Node node)
        {
            if (node.request != null && !node.request.IsOver)
            {
                // Never interrupted: it is collected first, and only then is what it produced given back.
                return false;
            }

            TakeResultBack(node);
            node.stage = CutGeometryStage.Reclaimed;
            node.over = true;
            return true;
        }

        /// <summary>
        /// Ends one cut whose geometry failed, and then tells the caller, once. The order matters: the failure is
        /// recorded, everything the cut held goes back and its ending is settled with the ledger **first**, and only
        /// then is the caller told. A notification that throws therefore leaves no cut half ended and no work stuck,
        /// and the same failure is never reported a second time — the exception itself is not caught, so it reaches
        /// whoever called <see cref="Pump"/>, which is the point of reporting it. A cut whose operation is not
        /// published yet keeps the ordinary treatment: its notice to the ledger waits for that publication. The
        /// source is untouched either way.
        /// </summary>
        private bool Fail(Node node, VpStorageCutStatus status, Exception failure)
        {
            _faults[node.operation] = status;
            var fault = new CutGeometryFault(node.operation, node.source, status, failure);
            bool moved = Reclaim(node, terminate: true);
            _fault.GeometryFailed(in fault);
            return moved;
        }

        /// <summary>
        /// Ends one cut's geometry without a commit: the work is given up — never interrupted — anything it produced
        /// goes back to the storage, and the ledger is told once, where there is something to tell it. An operation
        /// that is not published yet cannot take that notice, so it is kept until it can, or until the operation ends
        /// some other way.
        /// </summary>
        private bool Reclaim(Node node, bool terminate)
        {
            TakeResultBack(node);
            if (terminate)
            {
                if (_ledger.TryGetOperation(node.operation, out LogicalCutOperation record)
                    && record.state == LogicalCutOperationState.Admitted)
                {
                    // Its physics has not published yet. The notice belongs to the published operation, so it waits.
                    node.terminateWhenPublished = true;
                    node.stage = CutGeometryStage.Reclaimed;
                    return true;
                }

                _ledger.Terminate(node.operation);
            }

            node.terminateWhenPublished = false;
            node.stage = CutGeometryStage.Reclaimed;
            node.over = true;
            return true;
        }

        /// <summary>
        /// Gives back everything one unadopted cut holds: the work, which is given up rather than interrupted, and the
        /// geometry it produced. A result the runner had already finished but this had not taken yet counts — those
        /// two sides exist in the storage and nobody else will retire them. What the plane did not cut is the input
        /// borrowed back and is not this cut's to retire, and an empty side has nothing to retire.
        /// </summary>
        private void TakeResultBack(Node node)
        {
            if (node.request != null)
            {
                if (node.request.IsOver
                    && node.request.Stage == VpStorageCutStage.Finished
                    && node.request.Result.status == VpStorageCutStatus.Ok)
                {
                    node.cut = node.request.Result;
                    node.holdsProduced = true;
                }
                else
                {
                    _cuts.Abandon(node.request);
                }

                node.request = null;
            }

            if (!node.holdsProduced)
            {
                return;
            }

            node.holdsProduced = false;
            Retire(node.cut.positive);
            Retire(node.cut.negative);
            node.cut = default;
        }

        private void Retire(VpStorageCutSide side)
        {
            if (side.IsProduced)
            {
                _storage.TryRetireIndices(side.geometry.indexRange);
            }
        }

        /// <summary>Whether the source is still in the very frame this cut's basis was read in.</summary>
        private bool CurrentFrameIs(Node node)
        {
            return _frameOf.TryGetValue(node.source, out Matrix4x4 current) && SameFrame(current, node.basisFrame);
        }

        /// <summary>
        /// Value by value, with no tolerance: Matrix4x4's own equality is approximate, and a frame that is merely
        /// close to the one a result was cut in is a different frame.
        /// </summary>
        private static bool SameFrame(Matrix4x4 a, Matrix4x4 b)
        {
            for (int i = 0; i < 16; i++)
            {
                if (a[i] != b[i])
                {
                    return false;
                }
            }

            return true;
        }

        private Node Find(CutOperationId operation)
        {
            for (int i = 0; i < _nodes.Count; i++)
            {
                if (_nodes[i].operation == operation)
                {
                    return _nodes[i];
                }
            }

            return null;
        }

        /// <summary>One admitted cut's geometry: what it depends on, what is running for it and what came back.</summary>
        private sealed class Node
        {
            internal CutOperationId operation;
            internal LogicalFragmentId source;
            /// <summary>The adopted plane, in the source fragment's logical frame. Never replaced.</summary>
            internal float4 plane;

            /// <summary>The same plane in the basis geometry's own coordinates: what the kernel was given.</summary>
            internal float4 kernelPlane;

            internal VpStoredGeometry basis;

            /// <summary>The frame the basis was read in, which its two sides go on in.</summary>
            internal Matrix4x4 basisFrame;

            internal bool basisIsEmpty;
            internal VpStorageCutRequest request;
            internal VpStorageCutResult cut;

            /// <summary>Whether the two sides in <see cref="cut"/> are still this cut's to give back.</summary>
            internal bool holdsProduced;

            internal CutGeometryStage stage = CutGeometryStage.WaitingForBasis;
            internal bool terminateWhenPublished;
            internal bool over;
        }
    }
}
