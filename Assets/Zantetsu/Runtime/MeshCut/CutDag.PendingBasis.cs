using System;
using System.Collections.Generic;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut
{
    public sealed partial class CutDag
    {
        // A base geometry whose CPU vertices are still to come (DESIGN 4.5.15, the GPU-first route): the fragment's cut is
        // admitted, published and shown as any other -- the display draws the GPU bake's region -- while the kernel of
        // every cut below it waits here, in WaitingForBasis, exactly as a cut of a child waits for its parent's commit.
        // This is the thin connection and nothing more: no future, no event, no second ledger. The pending geometry is
        // never a kernel's input and never "an empty geometry"; it becomes the fragment's geometry through
        // RegisterBaseGeometry once the storage holds its vertices, or fails every cut that was waiting on it.
        private sealed class PendingBasis
        {
            internal VpStoredGeometry geometry;
            internal Matrix4x4 frame;
            internal bool failed;
            internal VpStorageCutStatus status;
            internal Exception failure;
        }

        private readonly Dictionary<LogicalFragmentId, PendingBasis> _pendingBasisOf = new Dictionary<LogicalFragmentId, PendingBasis>();
        private readonly Dictionary<CutOperationId, double> _kernelSecondsOf = new Dictionary<CutOperationId, double>();

        /// <summary>How many fragments wait for their base geometry's CPU vertices now.</summary>
        public int PendingBasisCount => _pendingBasisOf.Count;

        /// <summary>How many pending bases were registered, completed into a base geometry, failed, and dropped (no reader was left when they completed).</summary>
        public int PendingBasesRegistered { get; private set; }
        public int PendingBasesCompleted { get; private set; }
        public int PendingBasesFailed { get; private set; }
        public int PendingBasesDropped { get; private set; }

        /// <summary>
        /// Says which geometry a fragment **will** be, once its deferred CPU bake has written the vertices: the storage
        /// must still hold the geometry as pending (<see cref="VpCpuGeometryStorage.IsDeferredPending"/>). From here every
        /// cut of this fragment, and of its descendants, waits for its basis; nothing of their admission, physics or
        /// publication waits. The vertex room of the lineage is kept as a registered base geometry's is.
        /// </summary>
        public void RegisterPendingBaseGeometry(LogicalFragmentId fragment, VpStoredGeometry geometry, Matrix4x4 lineageToGeometryLocal)
        {
            if (_closed) throw new ObjectDisposedException(nameof(CutDag));
            if (!fragment.IsSet) throw new ArgumentException("not a fragment", nameof(fragment));
            if (!_storage.IsDeferredPending(geometry))
            {
                throw new InvalidOperationException("the geometry is not a pending deferred one: a completed geometry is registered by RegisterBaseGeometry");
            }

            if (_geometryOf.ContainsKey(fragment) || _withoutGeometry.Contains(fragment) || _pendingBasisOf.ContainsKey(fragment))
            {
                throw new InvalidOperationException("the fragment " + fragment + " has a geometry, or a pending one, already");
            }

            _pendingBasisOf[fragment] = new PendingBasis { geometry = geometry, frame = lineageToGeometryLocal };
            PendingBasesRegistered++;
            if (_storage.TryGetVertexGroup(geometry, out int group) && !_rootOfVertexGroup.ContainsKey(group))
            {
                _rootOfVertexGroup[group] = fragment;
            }
        }

        /// <summary>Whether the fragment's base geometry is still to come (failed or not).</summary>
        public bool IsBasisPending(LogicalFragmentId fragment) => _pendingBasisOf.ContainsKey(fragment);

        /// <summary>The pending base geometry of a fragment and whether its bake failed; false for a fragment with none.</summary>
        public bool TryGetPendingBasis(LogicalFragmentId fragment, out VpStoredGeometry geometry, out bool failed)
        {
            if (_pendingBasisOf.TryGetValue(fragment, out PendingBasis pending))
            {
                geometry = pending.geometry;
                failed = pending.failed;
                return true;
            }

            geometry = default;
            failed = false;
            return false;
        }

        /// <summary>
        /// The pending base geometry's CPU vertices are in the storage (the caller completed it there first, so the storage
        /// no longer says pending): the fragment's geometry is registered as a base geometry and the cuts waiting on it are
        /// offered at the next pump. False, with the pending entry gone, when nothing on the fragment's branch reads geometry
        /// any more (the pieces all ended before the bake came back) -- the caller then gives its resources back -- and
        /// false, with nothing changed, when the DAG is closed, the fragment has no pending basis, the geometry is another, the
        /// basis failed, or the storage still says pending. No physics, owner or publication is touched here.
        /// </summary>
        public bool TryCompletePendingBaseGeometry(LogicalFragmentId fragment, VpStoredGeometry geometry)
        {
            if (_closed || !_pendingBasisOf.TryGetValue(fragment, out PendingBasis pending)) return false;
            if (pending.failed || !pending.geometry.Equals(geometry) || _storage.IsDeferredPending(geometry)) return false;
            _pendingBasisOf.Remove(fragment);
            if (!BranchHasAReader(fragment))
            {
                PendingBasesDropped++;
                return false;
            }

            RegisterBaseGeometry(fragment, geometry, pending.frame);
            PendingBasesCompleted++;
            return true;
        }

        /// <summary>
        /// The pending base geometry will never come (its bake adopted nothing: the pieces ended, or the storage would not
        /// complete it): the entry goes, so that nothing waits on it. False when the fragment has no such pending basis.
        /// </summary>
        public bool DropPendingBaseGeometry(LogicalFragmentId fragment, VpStoredGeometry geometry)
        {
            if (!_pendingBasisOf.TryGetValue(fragment, out PendingBasis pending) || !pending.geometry.Equals(geometry)) return false;
            _pendingBasisOf.Remove(fragment);
            PendingBasesDropped++;
            return true;
        }

        /// <summary>
        /// The pending base geometry's bake failed: every cut waiting on it fails at the next pump, through the ordinary
        /// geometry failure (<see cref="ICutGeometryFault"/>, once each) -- the source is not retired, the physics is left
        /// as it is, nothing is re-accepted. False when the fragment has no such pending basis (nothing waits).
        /// </summary>
        public bool FailPendingBaseGeometry(LogicalFragmentId fragment, VpStoredGeometry geometry, VpStorageCutStatus status, Exception failure)
        {
            if (!_pendingBasisOf.TryGetValue(fragment, out PendingBasis pending) || pending.failed || !pending.geometry.Equals(geometry)) return false;
            pending.failed = true;
            pending.status = status == VpStorageCutStatus.Ok ? VpStorageCutStatus.InvalidInput : status;
            pending.failure = failure;
            PendingBasesFailed++;
            return true;
        }

        /// <summary>
        /// The worker seconds one cut's kernel took, recorded when its result was taken back (-1 for a cut whose kernel has
        /// not come back, a cut of an empty geometry, or one this never ran). Kept after the cut is over, for the benches.
        /// </summary>
        public double KernelSecondsOf(CutOperationId operation)
        {
            return _kernelSecondsOf.TryGetValue(operation, out double seconds) ? seconds : -1;
        }

        // In TryStart: a source whose basis is pending waits (nothing is offered, no worker is held), or fails as its
        // basis did. True when the node was handled here (waited or failed).
        private bool WaitsForPendingBasis(Node node, out bool moved)
        {
            moved = false;
            if (!_pendingBasisOf.TryGetValue(node.source, out PendingBasis pending)) return false;
            if (pending.failed) moved = Fail(node, pending.status, pending.failure);
            return true;
        }
    }
}
