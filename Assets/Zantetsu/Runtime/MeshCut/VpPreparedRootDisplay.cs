using System;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut
{
    public sealed partial class VpLogicalCutDisplay
    {
        /// <summary>
        /// One cold Shown/command/material allocation for one Direct16 producer and one display.
        /// No Storage, reference slot, GPU room, fragment ID or admission budget is reserved.
        /// Dispose unused slots; display disposal also releases them. After transfer starts a slot is never reusable.
        /// </summary>
        public sealed class PreparedRoot : IDisposable
        {
            internal VpLogicalCutDisplay owner;
            internal VpDirectSkinInput producer;
            internal Shown entry;
            internal PreparedRoot previous, next;
            public bool IsConsumed { get; internal set; }
            public bool IsCommitted { get; internal set; }
            public bool IsDisposed { get; private set; }
            internal PreparedRoot() { }
            internal Shown Take()
            {
                var result = entry;
                owner.UnlinkPreparedRoot(this);
                owner = null; producer = null; entry = null;
                IsConsumed = true;
                return result;
            }
            public void Dispose()
            {
                if (IsDisposed) return;
                IsDisposed = true;
                owner?.UnlinkPreparedRoot(this);
                owner = null; producer = null; entry = null;
            }
        }
        PreparedRoot _preparedRoots;
        int _preparedRootCount;
        void UnlinkPreparedRoot(PreparedRoot slot)
        {
            if (slot.previous != null) slot.previous.next = slot.next; else _preparedRoots = slot.next;
            if (slot.next != null) slot.next.previous = slot.previous;
            slot.previous = slot.next = null;
            _preparedRootCount--;
        }

        /// <summary>Cold only. Material binding (ID 0) is frozen in this slot; recreate it if the binding changes.</summary>
        public bool TryPrepareRoot(VpDirectSkinInput producer, out PreparedRoot slot)
        {
            ThrowIfDisposed(); ThrowIfBroken(); ThrowIfPreparing();
            slot = null;
            if (_halted || producer == null || producer.IsDisposed
                || !_materials.TryGetValue(0, out var material) || material == null) return false;
            var entry = new Shown { reflected = Array.Empty<VpClipBoundary>(), ranges = new VpGeometryRange[1],
                commands = new VpIndirectCommand[1], commandMaterials = new[] { material } };
            _shown.Capacity = Math.Max(_shown.Capacity, checked(_shown.Count + _preparedRootCount + 1));
            slot = new PreparedRoot { owner = this, producer = producer, entry = entry, next = _preparedRoots };
            if (_preparedRoots != null) _preparedRoots.previous = slot;
            _preparedRoots = slot; _preparedRootCount++;
            return true;
        }

        /// <summary>
        /// Explicit, synchronous root registration. No global arm or caller-supplied "verified" flag.
        /// Early refusal leaves the slot ready. Once GPU transfer starts it is consumed, even on refusal/exception.
        /// A refusal does not undo a successful Storage append; the caller still owns the published geometry.
        /// </summary>
        public bool TryShowPreparedRoot(PreparedRoot slot, VpDirectSkinOutput output, LogicalFragmentId fragment,
            Matrix4x4 objectToWorld, Matrix4x4 lineageToGeometryLocal)
        {
            ThrowIfDisposed(); ThrowIfBroken(); ThrowIfPreparing();
            if (slot == null || !ReferenceEquals(slot.owner, this) || !output.IsFrom(slot.producer, _storage)) return false;
            return TryShowCore(fragment, output.Geometry, objectToWorld, lineageToGeometryLocal,
                Array.Empty<VpClipBoundary>(), slot);
        }

        bool TryPrepareRootCommand(PreparedRoot slot, VpStoredGeometry geometry,
            out VpIndirectCommand[] commands, out Material[] materials, out Bounds bounds)
        {
            commands = null; materials = null; bounds = default;
            // Also recheck after the frame callback in TryShowCore; it may have disposed/consumed the slot.
            if (!ReferenceEquals(slot.owner, this) || slot.entry.commandMaterials[0] == null
                || !_storage.TryGetIndexState(geometry.indexRange, out var state, out int start, out _)
                || state != VpIndexRangeState.Published
                || !_storage.TryGetPublishedExtent(geometry, out int first, out int count, out bounds)) return false;
            commands = slot.entry.commands; materials = slot.entry.commandMaterials;
            commands[0] = new VpIndirectCommand(new VpGeometryRange(first, count, start, slot.producer.IndexCount), bounds, 1);
            return true;
        }
    }
}
