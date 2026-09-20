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
            if (!CanRegister(geometry) || !TryFindGeometrySlot(out int slot))
            {
                return false;
            }

            reference = TakeGeometrySlot(slot, geometry);
            return true;
        }

        /// <summary>
        /// Adds a display instance referencing a live geometry. Returns false with a default token for an ended or rejected
        /// geometry token, or when no usable instance slot is free.
        /// </summary>
        public bool TryAddDisplayInstance(VpGeometryReference geometry, out VpDisplayInstanceReference instance)
        {
            instance = default;
            if (!IsLive(geometry) || !TryFindInstanceSlot(out int slot))
            {
                return false;
            }

            instance = TakeInstanceSlot(slot, geometry.slot);
            return true;
        }

        /// <summary>
        /// Registers a stored geometry and adds its first display instance together, or does neither: both slots are
        /// found before either is taken, so a table with room to register the geometry but no room to show it refuses
        /// having registered nothing.
        /// <para>
        /// This is what a caller that cannot undo a registration needs, and giving a registration back is not an undo:
        /// <see cref="TryRetireGeometry"/> retires the geometry's index range in the storage. So on success this table
        /// takes over that responsibility — retiring the geometry here is what retires its range, once — and on a
        /// refusal nothing is registered and nothing is retired: the geometry stays the caller's, still Published in the
        /// storage, and it is the caller who decides what becomes of it.
        /// </para>
        /// Returns false with default tokens under every condition <see cref="TryRegisterGeometry"/> states, and also
        /// when no usable instance slot is free.
        /// </summary>
        public bool TryRegisterGeometryWithDisplayInstance(
            VpStoredGeometry geometry,
            out VpGeometryReference reference,
            out VpDisplayInstanceReference instance)
        {
            reference = default;
            instance = default;
            if (!CanRegister(geometry)
                || !TryFindGeometrySlot(out int geometrySlot)
                || !TryFindInstanceSlot(out int instanceSlot))
            {
                return false;
            }

            // Both slots are in hand; nothing below can fail.
            reference = TakeGeometrySlot(geometrySlot, geometry);
            instance = TakeInstanceSlot(instanceSlot, geometrySlot);
            return true;
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

        /// <summary>What a geometry has to be for this table to register it, short of a slot being free.</summary>
        private bool CanRegister(VpStoredGeometry geometry)
        {
            return _storage.IsGeometryConsistent(geometry)
                && _storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out _, out _)
                && state == VpIndexRangeState.Published
                && !IsRegistered(geometry.indexRange);
        }

        // The one rule for choosing a slot, kept in one place: a slot is usable while it is not live and its generation
        // has not reached the last one. Finding and taking are separate so that a caller needing two slots can find
        // both before taking either.
        private bool TryFindGeometrySlot(out int slot)
        {
            for (int s = 0; s < _geometries.Length; s++)
            {
                if (!_geometries[s].live && _geometries[s].generation != _lastGeneration)
                {
                    slot = s;
                    return true;
                }
            }

            slot = -1;
            return false;
        }

        private bool TryFindInstanceSlot(out int slot)
        {
            for (int s = 0; s < _instances.Length; s++)
            {
                if (!_instances[s].live && _instances[s].generation != _lastGeneration)
                {
                    slot = s;
                    return true;
                }
            }

            slot = -1;
            return false;
        }

        /// <summary>
        /// Whether this table could take <paramref name="count"/> more geometries, each with one display instance,
        /// right now. It asks by the same rule a registration chooses its slots by — a slot that is not live and has
        /// not used its last generation — so a caller that needs two is not told yes and then refused. A slot whose
        /// generations are spent is not room, however free it looks. Nothing is taken or changed here.
        /// </summary>
        public bool HasRoomForGeometriesWithDisplayInstances(int count)
        {
            if (count <= 0)
            {
                return true;
            }

            int geometries = 0;
            for (int s = 0; s < _geometries.Length && geometries < count; s++)
            {
                if (!_geometries[s].live && _geometries[s].generation != _lastGeneration)
                {
                    geometries++;
                }
            }

            int instances = 0;
            for (int s = 0; s < _instances.Length && instances < count; s++)
            {
                if (!_instances[s].live && _instances[s].generation != _lastGeneration)
                {
                    instances++;
                }
            }

            return geometries >= count && instances >= count;
        }

        private VpGeometryReference TakeGeometrySlot(int slot, VpStoredGeometry geometry)
        {
            ref GeometrySlot taken = ref _geometries[slot];
            taken.live = true;
            taken.generation = checked(taken.generation + 1);
            taken.geometry = geometry;
            taken.liveInstanceCount = 0;
            LiveGeometryCount++;
            return new VpGeometryReference(_tableId, slot, taken.generation);
        }

        private VpDisplayInstanceReference TakeInstanceSlot(int slot, int geometrySlot)
        {
            ref InstanceSlot taken = ref _instances[slot];
            taken.live = true;
            taken.generation = checked(taken.generation + 1);
            taken.geometrySlot = geometrySlot;
            _geometries[geometrySlot].liveInstanceCount++;
            LiveDisplayInstanceCount++;
            return new VpDisplayInstanceReference(_tableId, slot, taken.generation);
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
