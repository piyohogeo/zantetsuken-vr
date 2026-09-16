using System;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Fixed-capacity, main-thread lifetime table of geometry references and the display instances that use them
    /// (DESIGN 4.5.3), over a <see cref="VpCpuGeometryStorage"/> that must outlive the table. The table is the storage's
    /// only geometry reference manager for the storage's whole lifetime: a second table over the same storage cannot be
    /// constructed. The table neither owns nor disposes the storage, and it owns no read leases.
    /// <para>
    /// A geometry is registered from a stored geometry whose vertices are committed and whose index range is Published
    /// in the storage, at most once per index range. Display instances, up to the instance capacity, reference a live
    /// geometry; each instance is retired once. Retiring the last instance does not retire the geometry, which can gain
    /// instances again. Retiring a geometry is a separate, explicit step that succeeds only while no instance references
    /// it: it retires the index range in the storage exactly once and ends the registration, after which no instance can
    /// be added. It waits for neither lease returns nor GPU work, so the index range may stay Retiring in the storage
    /// until its last read lease returns.
    /// </para>
    /// <para>
    /// Any operation given a default, foreign, stale or ended token, or refused by the rule it states, returns false and
    /// changes nothing. Tokens never wrap: table ids come from the same non-wrapping source as index range tables, and a
    /// slot whose generation reached the last value is not used again, which shrinks the usable capacity.
    /// </para>
    /// </summary>
    public sealed class VpGeometryReferenceTable
    {
        private struct GeometrySlot
        {
            public bool live;
            public uint generation;
            public VpStoredGeometry geometry;
            public int liveInstanceCount;
        }

        private struct InstanceSlot
        {
            public bool live;
            public uint generation;
            public int geometrySlot;
        }

        private static int s_lastTableId;

        private readonly VpCpuGeometryStorage _storage;
        private readonly int _tableId;
        private readonly uint _lastGeneration;
        private readonly GeometrySlot[] _geometries;
        private readonly InstanceSlot[] _instances;

        /// <summary>
        /// Creates the storage's reference table. Throws InvalidOperationException when the storage already has one,
        /// leaving that existing claim unchanged. The arguments are checked, the slot arrays allocated and the table id
        /// taken before the storage is claimed, so a failure in any of those steps leaves an unclaimed storage unclaimed;
        /// nothing that can fail follows a successful claim.
        /// </summary>
        public VpGeometryReferenceTable(VpCpuGeometryStorage storage, int geometryCapacity, int displayInstanceCapacity)
            : this(storage, geometryCapacity, displayInstanceCapacity, uint.MaxValue)
        {
        }

        /// <summary>A table whose last generation is lowered, so tests can reach it.</summary>
        internal VpGeometryReferenceTable(VpCpuGeometryStorage storage, int geometryCapacity, int displayInstanceCapacity, uint lastGeneration)
        {
            if (storage == null)
            {
                throw new ArgumentNullException(nameof(storage));
            }

            if (geometryCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(geometryCapacity), geometryCapacity, "Must not be negative.");
            }

            if (displayInstanceCapacity < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(displayInstanceCapacity), displayInstanceCapacity, "Must not be negative.");
            }

            if (lastGeneration < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(lastGeneration), lastGeneration, "Must be at least 1.");
            }

            _storage = storage;
            _lastGeneration = lastGeneration;
            _geometries = new GeometrySlot[geometryCapacity];
            _instances = new InstanceSlot[displayInstanceCapacity];
            _tableId = VpIndexRangeLifecycleTable.NextTableId(ref s_lastTableId);

            // Last, so that nothing after the claim can fail.
            if (!storage.TryClaimReferenceTable())
            {
                throw new InvalidOperationException("The storage already has its geometry reference table.");
            }
        }

        public int GeometryCapacity => _geometries.Length;

        public int DisplayInstanceCapacity => _instances.Length;

        public int LiveGeometryCount { get; private set; }

        public int LiveDisplayInstanceCount { get; private set; }

        /// <summary>
        /// Registers a stored geometry of the storage. Returns false with a default token when the storage does not own
        /// a consistent geometry of that description — its vertices or submesh descriptors are not committed there, or a
        /// geometry without a topology mapping claims topology vertices — when its index range is not Published there or
        /// is already registered in this table, or when no usable geometry slot is free.
        /// </summary>
        public bool TryRegisterGeometry(VpStoredGeometry geometry, out VpGeometryReference reference)
        {
            reference = default;
            if (!_storage.IsGeometryConsistent(geometry)
                || !_storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out _, out _)
                || state != VpIndexRangeState.Published
                || IsRegistered(geometry.indexRange))
            {
                return false;
            }

            for (int s = 0; s < _geometries.Length; s++)
            {
                ref GeometrySlot slot = ref _geometries[s];
                if (slot.live || slot.generation == _lastGeneration)
                {
                    continue;
                }

                slot.live = true;
                slot.generation = checked(slot.generation + 1);
                slot.geometry = geometry;
                slot.liveInstanceCount = 0;
                LiveGeometryCount++;
                reference = new VpGeometryReference(_tableId, s, slot.generation);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Adds a display instance referencing a live geometry. Returns false with a default token for an ended or rejected
        /// geometry token, or when no usable instance slot is free.
        /// </summary>
        public bool TryAddDisplayInstance(VpGeometryReference geometry, out VpDisplayInstanceReference instance)
        {
            instance = default;
            if (!IsLive(geometry))
            {
                return false;
            }

            for (int s = 0; s < _instances.Length; s++)
            {
                ref InstanceSlot slot = ref _instances[s];
                if (slot.live || slot.generation == _lastGeneration)
                {
                    continue;
                }

                slot.live = true;
                slot.generation = checked(slot.generation + 1);
                slot.geometrySlot = geometry.slot;
                _geometries[geometry.slot].liveInstanceCount++;
                LiveDisplayInstanceCount++;
                instance = new VpDisplayInstanceReference(_tableId, s, slot.generation);
                return true;
            }

            return false;
        }

        /// <summary>Ends a live display instance's reference once. Its geometry stays live, even when this was its last instance.</summary>
        public bool TryRetireDisplayInstance(VpDisplayInstanceReference instance)
        {
            if (!IsLive(instance))
            {
                return false;
            }

            ref InstanceSlot slot = ref _instances[instance.slot];
            slot.live = false;
            _geometries[slot.geometrySlot].liveInstanceCount--;
            LiveDisplayInstanceCount--;
            return true;
        }

        /// <summary>
        /// Retires a live geometry that no display instance references: retires its index range in the storage once and
        /// ends the registration. Returns false, changing nothing, while an instance references it, for an ended or
        /// rejected token, or when the storage refuses to retire the index range.
        /// </summary>
        public bool TryRetireGeometry(VpGeometryReference geometry)
        {
            if (!IsLive(geometry)
                || _geometries[geometry.slot].liveInstanceCount != 0
                || !_storage.TryRetireIndices(_geometries[geometry.slot].geometry.indexRange))
            {
                return false;
            }

            _geometries[geometry.slot].live = false;
            LiveGeometryCount--;
            return true;
        }

        /// <summary>The stored geometry and live instance count of a live geometry; false with defaults otherwise.</summary>
        public bool TryGetGeometry(VpGeometryReference geometry, out VpStoredGeometry stored, out int liveDisplayInstanceCount)
        {
            if (!IsLive(geometry))
            {
                stored = default;
                liveDisplayInstanceCount = 0;
                return false;
            }

            stored = _geometries[geometry.slot].geometry;
            liveDisplayInstanceCount = _geometries[geometry.slot].liveInstanceCount;
            return true;
        }

        /// <summary>The geometry a live display instance references; false with a default token otherwise.</summary>
        public bool TryGetDisplayInstanceGeometry(VpDisplayInstanceReference instance, out VpGeometryReference geometry)
        {
            if (!IsLive(instance))
            {
                geometry = default;
                return false;
            }

            int geometrySlot = _instances[instance.slot].geometrySlot;
            geometry = new VpGeometryReference(_tableId, geometrySlot, _geometries[geometrySlot].generation);
            return true;
        }

        private bool IsLive(VpGeometryReference geometry)
        {
            return geometry.tableId == _tableId
                && (uint)geometry.slot < (uint)_geometries.Length
                && _geometries[geometry.slot].live
                && _geometries[geometry.slot].generation == geometry.generation;
        }

        private bool IsLive(VpDisplayInstanceReference instance)
        {
            return instance.tableId == _tableId
                && (uint)instance.slot < (uint)_instances.Length
                && _instances[instance.slot].live
                && _instances[instance.slot].generation == instance.generation;
        }

        private bool IsRegistered(VpIndexRangeHandle indexRange)
        {
            for (int s = 0; s < _geometries.Length; s++)
            {
                VpIndexRangeHandle registered = _geometries[s].geometry.indexRange;
                if (_geometries[s].live
                    && registered.tableId == indexRange.tableId
                    && registered.descriptor == indexRange.descriptor
                    && registered.generation == indexRange.generation)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
