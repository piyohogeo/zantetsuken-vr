using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The live physics owner of one logical fragment: the body the fragment is, and the convexes and cooked shapes
    /// it is made of.
    /// <para>
    /// This is the correspondence a hit, a query or a following cut goes through. Resolving a hit is not written
    /// here — what is here is the thing a resolution would land on, and the shape a cut of this fragment reads.
    /// </para>
    /// </summary>
    public sealed class PhysicsFragmentOwner
    {
        internal PhysicsFragmentOwner(
            GameObject root,
            Rigidbody body,
            PhysicsOwnerShape shape,
            bool fixedByAnchors,
            Matrix4x4? geometryLocalToOwner = null,
            BuildingLineage building = default,
            ConfigurableJoint buildingWorld = null)
        {
            Root = root != null ? root : throw new ArgumentNullException(nameof(root));
            _rootTransform = Root.transform;
            Body = body != null ? body : throw new ArgumentNullException(nameof(body));
            Shape = shape ?? throw new ArgumentNullException(nameof(shape));
            FixedByAnchors = fixedByAnchors;
            GeometryLocalToOwner = geometryLocalToOwner;
            Building = building;
            BuildingWorldConstraint = buildingWorld;
            SettleHitReach();
        }

        // A member of a fused building group (BuildingFusion, 2026-09-29): no Rigidbody of its own -- its colliders
        // belong to the group's -- but a Root of its own, which the display and the hit shapes follow as before.
        private PhysicsFragmentOwner(
            GameObject root, PhysicsOwnerShape shape, bool fixedByAnchors, Matrix4x4? geometryLocalToOwner,
            BuildingLineage building, FusedGroup group, float mass)
        {
            Root = root != null ? root : throw new ArgumentNullException(nameof(root));
            _rootTransform = Root.transform;
            Shape = shape ?? throw new ArgumentNullException(nameof(shape));
            FixedByAnchors = fixedByAnchors;
            GeometryLocalToOwner = geometryLocalToOwner;
            Building = building;
            Group = group ?? throw new ArgumentNullException(nameof(group));
            _fusedMass = mass;
            SettleHitReach();
        }

        /// <summary>A member owner of a fused group: made body-less, its mass the value the fusion computed for it.</summary>
        internal static PhysicsFragmentOwner Fused(
            GameObject root, PhysicsOwnerShape shape, bool fixedByAnchors, Matrix4x4? geometryLocalToOwner,
            BuildingLineage building, FusedGroup group, float mass)
        {
            return new PhysicsFragmentOwner(root, shape, fixedByAnchors, geometryLocalToOwner, building, group, mass);
        }

        private float _fusedMass;

        // A display member of a building hull group (BuildingHullFusion, 2026-09-30): no body, no shape, no colliders of
        // its own -- only a Root under its group's, which the display follows. Never a hit target (no shape).
        private PhysicsFragmentOwner(GameObject root, Matrix4x4? geometryLocalToOwner, BuildingLineage building)
        {
            Root = root != null ? root : throw new ArgumentNullException(nameof(root));
            _rootTransform = Root.transform;
            GeometryLocalToOwner = geometryLocalToOwner;
            Building = building;
        }

        /// <summary>A display-only owner: a Root for the placement and nothing of the physics (its group's hull carries that).</summary>
        internal static PhysicsFragmentOwner DisplayOnly(GameObject root, Matrix4x4? geometryLocalToOwner, BuildingLineage building)
        {
            return new PhysicsFragmentOwner(root, geometryLocalToOwner, building);
        }

        /// <summary>Whether this owner has no physics of its own at all (a hull group's display member).</summary>
        public bool IsDisplayOnly => Body == null && Shape == null && Group == null;

        /// <summary>The fused building group this owner's colliders belong to (BuildingFusion), or none.</summary>
        public FusedGroup Group { get; private set; }

        /// <summary>Whether this owner's physics is a fused group's: it has no Rigidbody of its own.</summary>
        public bool IsFused => Group != null;

        /// <summary>
        /// Becomes a member of a fused group: the body is gone (the caller destroyed it after moving the root under the
        /// group), the mass it had is kept as this owner's, and the group is named. A member moving to another group
        /// (a split, a merge) only changes the group.
        /// </summary>
        internal void FuseInto(FusedGroup group, float mass)
        {
            bool first = Group == null;
            Group = group ?? throw new ArgumentNullException(nameof(group));
            if (first)
            {
                _fusedMass = mass;
                Body = null;   // destroyed by the caller already (a destroyed component compares equal to null)
            }

            SettleHitReach();   // the Root stands under another parent from here: its stretch is read again
        }

        /// <summary>
        /// How far from the Root's position this owner's shape can reach in the world (DESIGN 19.1.7, D-193;
        /// <see cref="FragmentHitReach"/>), or <see cref="FragmentHitReach.None"/> when that cannot be vouched for.
        /// <para>
        /// **Settled where the shape and its placement are settled, not while the owner moves**: when this owner is made
        /// (its shape and its Root given together -- an owner's shape is never replaced; a different shape is a
        /// different owner), when its Root is put under another parent (<see cref="FuseInto"/>), and whenever
        /// <see cref="RefreshHitReach"/> is called. A shape whose local box was written again since
        /// (<see cref="PhysicsOwnerShape.LocalBoundsVersion"/>) is noticed here and settled again before the value is
        /// given, so a box that changed never leaves an old reach in use. Moving or turning the Root changes nothing of
        /// it, and nothing reads the Root for it between those moments.
        /// </para>
        /// <para>
        /// **The stretch of the Root is the one read at those moments.** Nothing in the system scales an owner's Root or
        /// an object above it after the owner is made; whoever adds processing that does -- a scale, or a parent with
        /// one -- calls <see cref="RefreshHitReach"/> in that same change, before the next evaluation of the Slash hits.
        /// </para>
        /// </summary>
        internal float HitReach
        {
            get
            {
                if (Shape == null)
                {
                    return FragmentHitReach.None;
                }

                if (_hitReachVersion != Shape.LocalBoundsVersion)
                {
                    SettleHitReach();
                }

                return _hitReach;
            }
        }

        /// <summary>Settles <see cref="HitReach"/> again from the shape and from the Root's stretch as it is now.</summary>
        internal void RefreshHitReach()
        {
            SettleHitReach();
        }

        /// <summary>Observation: how many times a reach was settled for any owner or Provisional side since the session began.</summary>
        internal static long HitReachSettles;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetHitReachSettles() => HitReachSettles = 0;

        private float _hitReach = FragmentHitReach.None;
        private int _hitReachVersion = -1;

        private void SettleHitReach()
        {
            HitReachSettles++;
            if (Shape == null)
            {
                _hitReach = FragmentHitReach.None;
                _hitReachVersion = -1;
                return;
            }

            _hitReachVersion = Shape.LocalBoundsVersion;
            _hitReach = FragmentHitReach.Settle(Shape, _rootTransform);
        }

        /// <summary>
        /// Whether this owner comes from a building and its split depth (DESIGN 7.2.2): the owner's own record of it.
        /// Registered explicitly for a first owner, and for a child the value planned before its cut was built.
        /// </summary>
        public BuildingLineage Building { get; }

        /// <summary>
        /// The building World D6 this owner's actor carries (DESIGN 7.2.2), or none. It is the system's own
        /// constraint, known here so that nothing has to look for joints on the actor; it ends with the actor.
        /// </summary>
        public ConfigurableJoint BuildingWorldConstraint { get; }

        public GameObject Root { get; private set; }

        public Rigidbody Body { get; private set; }

        /// <summary>What this owner is made of, and the next cut's input.</summary>
        public PhysicsOwnerShape Shape { get; private set; }

        /// <summary>This owner holds an anchor, so it is fixed (DESIGN 7.1).</summary>
        public bool FixedByAnchors { get; }

        /// <summary>
        /// How the display geometry of this fragment sits in this owner's own coordinates, for an owner whose display
        /// follows it; none for one whose display is arranged some other way, which is a statement and not a gap
        /// (DESIGN 5.6, 7.1.2). It is the caller's to give when the first owner of a lineage is registered, and the
        /// children of a cut inherit it, because a publication does not rebuild a child's owner frame: both sides
        /// start at the very placement the source had. It is **not** the cut DAG's lineage-to-geometry mapping, which
        /// is about the plane a kernel cuts at and has nothing to do with where anything stands.
        /// <para>
        /// It says where the geometry sits on its owner and nothing more. The display draws it there: no side is
        /// displaced for the display, so a separation impulse moves the owner and never this correspondence.
        /// </para>
        /// </summary>
        public Matrix4x4? GeometryLocalToOwner { get; }

        /// <summary>
        /// Where this owner's display geometry stands now: this owner's world transform, read at the moment it is
        /// needed, with the correspondence above. False for an owner that has none, or one that has left the scene —
        /// nothing of where it used to be is handed back.
        /// </summary>
        public bool TryReadGeometryLocalToWorld(out Matrix4x4 geometryLocalToWorld)
        {
            if (IsWithdrawn || Root == null || GeometryLocalToOwner == null)
            {
                geometryLocalToWorld = default;
                return false;
            }

            // The Root's Transform, held since the Root was given (a GameObject's Transform never changes; 2026-10-01): its
            // world matrix is still read now, every time.
            Transform rootTransform = rootTransformReadAgainForTest ? Root.transform : _rootTransform;
            geometryLocalToWorld = rootTransform.localToWorldMatrix * GeometryLocalToOwner.Value;
            return true;
        }

        // The Root's Transform: set with the Root and let go with it (Release), read by the placement read above and by
        // the collection of the hit shapes (2026-10-04).
        private Transform _rootTransform;

        /// <summary>The Root's Transform, held since the Root was given; null once released.</summary>
        internal Transform RootTransform => _rootTransform;

        /// <summary>Tests only: the Root's Transform asked of the Root at every read (the read before 2026-10-01).</summary>
        internal static bool rootTransformReadAgainForTest;

        /// <summary>Tests only: whether the Root's Transform is still held.</summary>
        internal bool HoldsRootTransformForTest => _rootTransform != null;

        /// <summary>Whether it has left the physics scene.</summary>
        public bool IsWithdrawn { get; private set; }

        // Dedicated first-cut bridge. A borrowed character hierarchy or placed instance is stopped, never destroyed by this owner.
        internal IPreparedSourceWithdrawal PreparedSourceWithdrawal;

        // The prepared character's own hierarchy leaving at its first cut's publication (inside that publication).
        private static readonly Unity.Profiling.ProfilerMarker s_withdrawPreparedRoot =
            new Unity.Profiling.ProfilerMarker("Zantetsu.Owner.WithdrawPreparedRoot");

        /// <summary>Whether its objects have been destroyed and its shape given up.</summary>
        public bool IsReleased { get; private set; }

        /// <summary>
        /// The mass the solver has now, which DESIGN 7.2 makes the parent mass of the next cut of this fragment. It is
        /// read from the body rather than remembered, because the body is authoritative once it is published.
        /// </summary>
        public float Mass => Body != null ? Body.mass : _fusedMass;

        /// <summary>Where this owner is now. Read at the moment it is needed, never remembered from earlier.</summary>
        public PhysicsOwnerPlacement ReadPlacement()
        {
            Root.transform.GetPositionAndRotation(out Vector3 position, out Quaternion rotation);
            return new PhysicsOwnerPlacement(position, rotation);
        }

        /// <summary>
        /// How this owner is moving now, in the world, with the render anchor the caller names. Ordinary motion is
        /// what this reads: a source that has moved since a candidate was built is not stale, and the values a split
        /// inherits come from here rather than from anything recorded earlier.
        /// </summary>
        public PhysicsOwnerMotion ReadMotion(float3 renderAnchor)
        {
            if (Body == null)
            {
                // A fused member: its motion is the group's, which a cut of it does not inherit through here.
                return new PhysicsOwnerMotion(Root.transform.position, float3.zero, float3.zero, renderAnchor);
            }

            return new PhysicsOwnerMotion(
                Body.worldCenterOfMass, Body.linearVelocity, Body.angularVelocity, renderAnchor);
        }

        /// <summary>
        /// Takes this owner out of the physics scene, at once: the object is deactivated, so its body and colliders
        /// stop being simulated and stop answering queries from here on. Nothing is destroyed and nothing is given
        /// back yet — <see cref="Release"/> does that. The two are separate because destruction is not immediate
        /// everywhere: in play mode <c>Destroy</c> happens later in the frame, and an owner that has been retired must
        /// not still be found by a query in the meantime.
        /// </summary>
        internal void Withdraw()
        {
            if (IsWithdrawn)
            {
                return;
            }

            IsWithdrawn = true;
            if (PreparedSourceWithdrawal != null)
            {
                using (s_withdrawPreparedRoot.Auto())
                {
                    PreparedSourceWithdrawal.Withdraw();
                }
            }
            if (Root != null)
            {
                Root.SetActive(false);
            }
        }

        /// <summary>
        /// Destroys this owner's objects and gives up its shape. A mesh or a bank something else is still using is not
        /// given back here: another owner has its own hold on the meshes, and work still reading the bank has its own
        /// hold on the shape, so what is still in use goes back when that use ends.
        /// </summary>
        internal void Release()
        {
            if (IsReleased)
            {
                return;
            }

            Withdraw();
            IsReleased = true;
            PhysicsOwnerBuilder.DestroyObject(Root);
            Root = null;
            _rootTransform = null;
            Body = null;
            Shape?.Dispose();
            Shape = null;
        }
    }

    /// <summary>
    /// Which physics owner each live logical fragment is (DESIGN 7.1.2): the correspondence that a publication
    /// switches, and that a following cut reads its input from.
    /// <para>
    /// The key is the fragment the ledger already has; no identity is invented here, and no state of a publication is
    /// kept. A fragment has an owner or it does not.
    /// </para>
    /// <para>
    /// **The published Provisional pairs live here too**, beside that correspondence and without disturbing it
    /// (DESIGN 7.1.1). A Provisional publishes no logical child, so its two actors have no fragment of their own and
    /// the source keeps the one owner record it has -- withdrawn, waiting to be retired. The pair is kept on the cut
    /// that made it, and the source it belongs to can be found from either of its actors, which is what lets a hit on
    /// one of them resolve to the fragment they are both still part of.
    /// </para>
    /// </summary>
    /// <summary>
    /// One convex set a fragment is made of now, and what places it (<see cref="PhysicsOwnerRegistry.CollectCurrentShapes"/>).
    /// <see cref="Side"/> is +1 or -1 for a side of a Provisional pair, whose <see cref="Fragment"/> is the source it
    /// still is, and 0 for a published owner.
    /// </summary>
    public readonly struct CurrentShape
    {
        public CurrentShape(LogicalFragmentId fragment, float side, PhysicsOwnerShape shape, Transform owner, float reach = -1f)
        {
            Fragment = fragment;
            Side = side;
            Shape = shape;
            Owner = owner;
            Reach = reach;
        }

        /// <summary>
        /// How far from <see cref="Owner"/>'s position <see cref="Shape"/> can reach in the world, as its owner (or its
        /// Provisional side) keeps it (DESIGN 19.1.7, D-193); negative when none can be vouched for.
        /// </summary>
        public float Reach { get; }

        public LogicalFragmentId Fragment { get; }

        public float Side { get; }

        public PhysicsOwnerShape Shape { get; }

        public Transform Owner { get; }
    }

    public sealed class PhysicsOwnerRegistry : IDisposable
    {
        private readonly Dictionary<LogicalFragmentId, PhysicsFragmentOwner> _owners =
            new Dictionary<LogicalFragmentId, PhysicsFragmentOwner>();

        private readonly Dictionary<CutOperationId, ProvisionalOwnerPair> _pairsByOperation =
            new Dictionary<CutOperationId, ProvisionalOwnerPair>();

        private readonly Dictionary<LogicalFragmentId, ProvisionalOwnerPair> _pairsBySource =
            new Dictionary<LogicalFragmentId, ProvisionalOwnerPair>();

        private readonly Dictionary<Rigidbody, ProvisionalOwnerPair> _pairOfBody =
            new Dictionary<Rigidbody, ProvisionalOwnerPair>();

        private readonly Dictionary<Rigidbody, LogicalFragmentId> _fragmentOfBody =
            new Dictionary<Rigidbody, LogicalFragmentId>();

        public int Count => _owners.Count;

        /// <summary>
        /// How many system constraints are in the scene's keeping now: each published Provisional pair's sibling
        /// constraint, and every building World D6 on a pair's actor or a published owner's (DESIGN 7.1.1, 7.2.2).
        /// A constraint counts from the moment what carries it is taken in here until what carries it is ended.
        /// </summary>
        public int SystemConstraintCount { get; private set; }

        /// <summary>
        /// Whether a body is held by a constraint the cut system itself made -- a Provisional pair's sibling
        /// constraint, or a building World D6 -- rather than moving on its own. This is the whole of what a later
        /// consumer (DESIGN 4.54's prediction) needs to tell such a body apart; nothing is searched for on the body.
        /// A registered owner carries no other joint (DESIGN 7.2.2), so a body this does not know is not held.
        /// </summary>
        public bool HeldBySystemConstraint(Rigidbody body)
        {
            if (body == null)
            {
                return false;
            }

            // Both actors of a live pair are held by the sibling constraint between them.
            if (_pairOfBody.TryGetValue(body, out ProvisionalOwnerPair pair) && !pair.IsEnded)
            {
                return true;
            }

            return _fragmentOfBody.TryGetValue(body, out LogicalFragmentId fragment)
                && _owners.TryGetValue(fragment, out PhysicsFragmentOwner owner)
                && !owner.IsWithdrawn
                && owner.BuildingWorldConstraint != null;
        }

        /// <summary>How many published Provisional pairs are in the scene now.</summary>
        public int ProvisionalPairCount => _pairsByOperation.Count;

        /// <summary>
        /// Cold absolute high-water capacities for owner maps and simultaneously retained Provisional pair maps.
        /// Includes two body entries per pair. No owners, bodies, IDs or admission slots are created/reserved.
        /// Repeated/lower requests never shrink or add to the requested totals. Not a transaction reservation.
        /// </summary>
        public void PrepareCapacity(int ownerCapacity, int provisionalPairCapacity)
        {
            if (ownerCapacity < 0) throw new ArgumentOutOfRangeException(nameof(ownerCapacity));
            if (provisionalPairCapacity < 0) throw new ArgumentOutOfRangeException(nameof(provisionalPairCapacity));
            int pairBodies = checked(2 * provisionalPairCapacity);
            _owners.EnsureCapacity(ownerCapacity); _fragmentOfBody.EnsureCapacity(ownerCapacity);
            _pairsByOperation.EnsureCapacity(provisionalPairCapacity);
            _pairsBySource.EnsureCapacity(provisionalPairCapacity); _pairOfBody.EnsureCapacity(pairBodies);
        }

        /// <summary>
        /// Makes room for a few more owners before anything is added, so that adding them cannot be what fails. It
        /// changes nothing else.
        /// </summary>
        public void Reserve(int count)
        {
            if (count > 0)
            {
                _owners.EnsureCapacity(_owners.Count + count);
                _fragmentOfBody.EnsureCapacity(_fragmentOfBody.Count + count);
            }
        }

        /// <summary>Appends every fragment that has an owner here to <paramref name="into"/>, in no particular order.</summary>
        public void CopyFragmentsTo(List<LogicalFragmentId> into)
        {
            foreach (LogicalFragmentId fragment in _owners.Keys)
            {
                into.Add(fragment);
            }
        }

        public bool TryGet(LogicalFragmentId fragment, out PhysicsFragmentOwner owner)
        {
            return _owners.TryGetValue(fragment, out owner);
        }

        /// <summary>The published Provisional pair of one accepted cut, if that cut has one in the scene.</summary>
        public bool TryGetProvisional(CutOperationId operation, out ProvisionalOwnerPair pair)
        {
            return _pairsByOperation.TryGetValue(operation, out pair);
        }

        /// <summary>
        /// The published Provisional pair standing in for one fragment, if there is one. A fragment has at most one:
        /// a live fragment has at most one accepted cut (DESIGN 7.1).
        /// </summary>
        public bool TryGetProvisionalOf(LogicalFragmentId source, out ProvisionalOwnerPair pair)
        {
            return _pairsBySource.TryGetValue(source, out pair);
        }

        /// <summary>
        /// Which fragment one body of a published Provisional pair belongs to, and which side of the cut it is.
        /// **Both sides answer with the same source**: the fragment is not split until the Final publication
        /// (DESIGN 7.1.1), so a hit on either actor is a hit on that one fragment.
        /// <para>
        /// This is the correspondence itself and nothing more. Finding which body was hit, and what a hit means, is
        /// not written here.
        /// </para>
        /// </summary>
        /// <summary>
        /// Which live fragment one body is, and which side of a cut it is if it is one of a published Provisional
        /// pair's two actors. **One entrance for both**, because the same actor is both in turn: while a cut is
        /// provisional its two actors answer with the source they are still part of and with the side they are, and
        /// once that cut is published each of them answers with the child it has become, whose side is nothing --
        /// zero -- because a published child is a fragment and not a side of anything.
        /// <para>
        /// A body whose owner has left the physics scene answers with nothing: what was withdrawn is not there to be
        /// resolved to, and neither is one that was retired.
        /// </para>
        /// <para>
        /// This is the correspondence itself and nothing more. Finding which body was hit, and what a hit means, is
        /// not written here.
        /// </para>
        /// </summary>
        public bool TryResolveFragment(Rigidbody body, out LogicalFragmentId fragment, out float side)
        {
            if (TryResolveSource(body, out fragment, out side))
            {
                return true;
            }

            if (body == null || !_fragmentOfBody.TryGetValue(body, out fragment))
            {
                fragment = default;
                return false;
            }

            if (!_owners.TryGetValue(fragment, out PhysicsFragmentOwner owner) || owner.IsWithdrawn)
            {
                fragment = default;
                return false;
            }

            side = 0f;
            return true;
        }

        /// <summary>
        /// Every convex set the fragments are made of now, for a hit (DESIGN 19.1.7), added to <paramref name="into"/>
        /// after it is cleared: each published owner in the scene with its own shape, and each standing side of a
        /// published Provisional pair with that side's shape, answering with the source it still is and its side. Each
        /// comes with the object its shape is placed by, whose world transform with
        /// <see cref="PhysicsOwnerShape.LocalToOwner"/> is where the convexes stand now -- the transform a cut reads, and
        /// the one the next step takes the body from. A withdrawn owner, an ended pair and a shape given up are not in it.
        /// <para>
        /// It reads the correspondence and nothing else, and what it hands out is a list the caller owns: a publication
        /// after it changes the correspondence, not the list.
        /// </para>
        /// </summary>
        public void CollectCurrentShapes(List<CurrentShape> into)
        {
            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }

            into.Clear();
            foreach (KeyValuePair<LogicalFragmentId, PhysicsFragmentOwner> entry in _owners)
            {
                PhysicsFragmentOwner owner = entry.Value;
                if (!owner.IsWithdrawn && owner.Root != null && owner.Shape != null && !owner.Shape.IsFreed)
                {
                    // The Transform held since the Root was given (a GameObject's Transform never changes), not asked of the
                    // Root at every collection (2026-10-04); the Root's being there is asked above, as before.
                    into.Add(new CurrentShape(entry.Key, 0f, owner.Shape,
                        PhysicsFragmentOwner.rootTransformReadAgainForTest ? owner.Root.transform : owner.RootTransform, owner.HitReach));
                }
            }

            foreach (KeyValuePair<CutOperationId, ProvisionalOwnerPair> entry in _pairsByOperation)
            {
                ProvisionalOwnerPair pair = entry.Value;
                for (int s = 0; s < 2; s++)
                {
                    bool positive = s == 0;
                    PhysicsOwnerShape shape = positive ? pair.PositiveShape : pair.NegativeShape;
                    if (pair.IsStanding(positive) && shape != null && !shape.IsFreed)
                    {
                        // The side's own Root and the side's own shape -- and the reach kept for that pair of them.
                        into.Add(new CurrentShape(pair.Source, positive ? 1f : -1f, shape, pair.Side(positive).Root.transform, pair.HitReach(positive)));
                    }
                }
            }
        }

        public bool TryResolveSource(Rigidbody body, out LogicalFragmentId source, out float side)
        {
            source = default;
            side = 0f;
            if (body == null || !_pairOfBody.TryGetValue(body, out ProvisionalOwnerPair pair) || pair.IsEnded)
            {
                return false;
            }

            source = pair.Source;
            side = ReferenceEquals(pair.Positive?.Body, body) ? 1f : -1f;
            return true;
        }

        /// <summary>
        /// Makes room for one more Provisional pair before anything is added, so that adding it cannot be what fails
        /// in the middle of a publication. It changes nothing else.
        /// </summary>
        public void ReserveProvisional()
        {
            _pairsByOperation.EnsureCapacity(_pairsByOperation.Count + 1);
            _pairsBySource.EnsureCapacity(_pairsBySource.Count + 1);
            _pairOfBody.EnsureCapacity(_pairOfBody.Count + 2);
        }

        /// <summary>
        /// Takes a published pair into the correspondence: by its cut, by the source it stands in for, and by each of
        /// its two bodies. The source keeps its own owner record, which the caller has withdrawn.
        /// <para>
        /// The two refusals below are **structural checks by the time this is called**: a caller publishing a pair
        /// settles them before it moves anything, because by here the source has already left the scene and a refusal
        /// would be too late to be anything but an internal error. The room was made by
        /// <see cref="ReserveProvisional"/> for the same reason.
        /// </para>
        /// </summary>
        internal void AddProvisional(ProvisionalOwnerPair pair)
        {
            if (pair == null)
            {
                throw new ArgumentNullException(nameof(pair));
            }

            if (_pairsByOperation.ContainsKey(pair.Operation))
            {
                throw new InvalidOperationException("that cut already has a published Provisional pair");
            }

            if (_pairsBySource.ContainsKey(pair.Source))
            {
                throw new InvalidOperationException("that fragment already has a published Provisional pair");
            }

            _pairsByOperation.Add(pair.Operation, pair);
            _pairsBySource.Add(pair.Source, pair);
            AddBody(pair.Positive, pair);
            AddBody(pair.Negative, pair);
            SystemConstraintCount += ConstraintsOf(pair);
            ProvisionalAdded?.Invoke(pair);
        }

        private static int ConstraintsOf(ProvisionalOwnerPair pair)
        {
            return (pair.Separation != null ? 1 : 0) + pair.BuildingWorldCount;
        }

        private void AddBody(PhysicsOwnerSide side, ProvisionalOwnerPair pair)
        {
            if (side?.Body != null)
            {
                _pairOfBody[side.Body] = pair;
            }
        }

        /// <summary>
        /// Takes one published Provisional pair out of the correspondence and **hands its two actors over** instead of
        /// ending them: the objects stay in the scene exactly as they are, the sibling constraint is destroyed with the
        /// pair, and the shapes those actors stand on go over with them -- no mesh hold is given back here. False when
        /// that cut has no pair here.
        /// <para>
        /// It is for the handoff to a Final publication (DESIGN 7.2), where the same actors are given the final shape
        /// and registered as the children's owners. The caller registers those owners before this, and they are what
        /// holds the shapes from here on.
        /// **<see cref="EndProvisional"/> is the other thing** and destroys them; calling that here would destroy the
        /// actors the children are.
        /// </para>
        /// </summary>
        internal bool TryHandOverProvisional(
            CutOperationId operation, out PhysicsOwnerSide positive, out PhysicsOwnerSide negative)
        {
            positive = null;
            negative = null;
            if (!_pairsByOperation.TryGetValue(operation, out ProvisionalOwnerPair pair))
            {
                return false;
            }

            _pairsByOperation.Remove(operation);
            _pairsBySource.Remove(pair.Source);
            RemoveBody(pair.Positive);
            RemoveBody(pair.Negative);

            // The sibling constraint ends with the pair; the building constraints go on with the actors, and are
            // counted from here by the owners the caller registered for them.
            SystemConstraintCount -= ConstraintsOf(pair);
            pair.HandOver(out positive, out negative);
            return true;
        }

        /// <summary>
        /// Ends one published Provisional pair and forgets it: both actors leave the scene and are destroyed, the
        /// constraint with them, and the two shapes give their mesh holds back. False when that cut has no pair here.
        /// <para>
        /// **The source is not retired here and the ledger is not touched.** The source's own owner record is still
        /// the caller's to retire, and a mesh the source's shape also holds goes back with it and not before. This is
        /// the one way a published pair ends, whether the cut went on to a Final publication or was abandoned.
        /// </para>
        /// </summary>
        public bool EndProvisional(CutOperationId operation)
        {
            if (!_pairsByOperation.TryGetValue(operation, out ProvisionalOwnerPair pair))
            {
                return false;
            }

            // Forgotten first, in all three places, and only then ended: the bodies a pair is found by are the
            // pair's own, and ending it lets go of them.
            _pairsByOperation.Remove(operation);
            _pairsBySource.Remove(pair.Source);
            RemoveBody(pair.Positive);
            RemoveBody(pair.Negative);
            SystemConstraintCount -= ConstraintsOf(pair);
            ProvisionalEnding?.Invoke(pair);
            pair.End();
            return true;
        }

        private void RemoveBody(PhysicsOwnerSide side)
        {
            if (side?.Body != null)
            {
                _pairOfBody.Remove(side.Body);
            }
        }

        /// <summary>
        /// Gives a fragment the owner it already has in the scene — an authored compound, before anything has been
        /// cut. A fragment that already has one is refused rather than replaced.
        /// </summary>
        /// <param name="geometryLocalToOwner">
        /// Where this lineage's display geometry sits in this owner's coordinates, for a display that follows it
        /// (<see cref="PhysicsFragmentOwner.GeometryLocalToOwner"/>). Left out for a lineage whose display is
        /// arranged some other way; every cut of this one inherits what is given here.
        /// </param>
        /// <param name="isBuildingDerived">
        /// Whether this owner is registered as a building (DESIGN 7.2.2): the caller's explicit statement, never
        /// inferred. A building starts at split depth 0; anything else is not building-derived and stays at 0.
        /// </param>
        public PhysicsFragmentOwner RegisterAuthored(
            LogicalFragmentId fragment,
            GameObject root,
            Rigidbody body,
            PhysicsOwnerShape shape,
            bool fixedByAnchors = false,
            Matrix4x4? geometryLocalToOwner = null,
            bool isBuildingDerived = false)
        {
            var owner = new PhysicsFragmentOwner(
                root, body, shape, fixedByAnchors, geometryLocalToOwner,
                isBuildingDerived ? BuildingLineage.RegisteredBuilding : BuildingLineage.NotBuilding);
            Add(fragment, owner);
            return owner;
        }

        /// <summary>An owner has been added (a registered body, or a child at its Final publication).</summary>
        internal event Action<LogicalFragmentId, PhysicsFragmentOwner> OwnerAdded;

        /// <summary>An owner is about to be retired: withdrawn already, not yet released.</summary>
        internal event Action<LogicalFragmentId, PhysicsFragmentOwner> OwnerRetiring;

        /// <summary>A Provisional pair has been published; its source has been withdrawn by the caller.</summary>
        internal event Action<ProvisionalOwnerPair> ProvisionalAdded;

        /// <summary>A Provisional pair is about to be ended (abandoned): its actors are about to be destroyed.</summary>
        internal event Action<ProvisionalOwnerPair> ProvisionalEnding;

        /// <summary>A body that stops being a fragment's (the fragment's owner fused into a group): the way back goes.</summary>
        internal void ForgetBody(Rigidbody body)
        {
            if (body != null)
            {
                _fragmentOfBody.Remove(body);
            }
        }

        internal void Add(LogicalFragmentId fragment, PhysicsFragmentOwner owner)
        {
            if (!fragment.IsSet)
            {
                throw new ArgumentException("not a fragment", nameof(fragment));
            }

            if (_owners.ContainsKey(fragment))
            {
                throw new InvalidOperationException("that fragment already has a physics owner");
            }

            _owners.Add(fragment, owner);
            if (owner.BuildingWorldConstraint != null)
            {
                SystemConstraintCount++;
            }

            if (owner.Body != null)
            {
                // And the way back, so that the actor this fragment is can be resolved to it: a child of a handoff is
                // the very actor a Provisional side was, and it would otherwise stop resolving to anything the moment
                // the pair left the correspondence.
                _fragmentOfBody[owner.Body] = fragment;
            }

            OwnerAdded?.Invoke(fragment, owner);
        }

        /// <summary>
        /// Takes one fragment's owner out of the physics scene without destroying it or giving anything back. The
        /// fragment keeps its owner here, so what was withdrawn can still be looked at; <see cref="Retire"/> is what
        /// ends it.
        /// </summary>
        public bool Withdraw(LogicalFragmentId fragment)
        {
            if (!_owners.TryGetValue(fragment, out PhysicsFragmentOwner owner))
            {
                return false;
            }

            owner.Withdraw();
            return true;
        }

        /// <summary>
        /// Ends one fragment's owner and forgets it: out of the physics scene first, then destroyed and given up. One
        /// side of a cut can be retired without the other — each holds its own cooked shapes, and work still reading a
        /// bank holds that — so this gives back only what nothing else is using.
        /// </summary>
        public bool Retire(LogicalFragmentId fragment)
        {
            if (!_owners.TryGetValue(fragment, out PhysicsFragmentOwner owner))
            {
                return false;
            }

            owner.Withdraw();
            _owners.Remove(fragment);
            if (owner.Body != null)
            {
                _fragmentOfBody.Remove(owner.Body);
            }

            if (owner.BuildingWorldConstraint != null)
            {
                SystemConstraintCount--;
            }

            OwnerRetiring?.Invoke(fragment, owner);
            owner.Release();
            return true;
        }

        /// <summary>Ends every owner and every published pair left. What they were holding goes back with them.</summary>
        public void Dispose()
        {
            foreach (KeyValuePair<CutOperationId, ProvisionalOwnerPair> published in _pairsByOperation)
            {
                published.Value.End();
            }

            _pairsByOperation.Clear();
            _pairsBySource.Clear();
            _pairOfBody.Clear();

            _fragmentOfBody.Clear();
            foreach (KeyValuePair<LogicalFragmentId, PhysicsFragmentOwner> pair in _owners)
            {
                pair.Value.Withdraw();
            }

            foreach (KeyValuePair<LogicalFragmentId, PhysicsFragmentOwner> pair in _owners)
            {
                pair.Value.Release();
            }

            _owners.Clear();
            SystemConstraintCount = 0;
        }
    }
}
