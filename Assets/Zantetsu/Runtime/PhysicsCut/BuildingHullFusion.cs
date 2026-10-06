using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>The settings of the building hull trial (<see cref="BuildingHullFusion"/>). Off by default.</summary>
    public readonly struct BuildingHullSettings
    {
        public readonly bool enabled;
        public readonly double deadlineSeconds;   // real seconds after a cut before its two sides are fused (0: only by the next Slash); also the wait before a refused fusion is tried again
        public readonly double mainBudgetSeconds;   // the Main time one Step may begin units within
        public readonly double maxNewPenetrationMetres;   // a candidate hull that would penetrate something outside its participants deeper than this beyond what their hulls did is not adopted (a diagnostic value)
        public readonly double stageSeconds;   // the sides' motion time after a cut (real seconds; 0: off): then, or at the next Slash's accepted hit on the building, the moving sides are fixed for show
        public readonly bool siblingD6;   // the two sides held by a short sibling constraint while they move (only with a motion time)
        public readonly double siblingOpeningMetres;   // the constraint's opening along the normal (0 .. this)
        public readonly bool kinematicDisplay;   // the always-kinematic mode: one kinematic body, hull and collider a building for ever; the display cut and animated on its own; the hull exchanged best-effort
        public readonly double animationSeconds;   // the display's drop time (real seconds)
        public readonly double dropHorizontalMetres, dropVerticalMetres;   // how far the upper side drops for a horizontal plane / a vertical one (interpolated in between by |n.up|)

        // The always-kinematic building's cut limit (2026-10-01; 0: off): a quality setting, N display geometries -- a building
        // whose live, non-empty, committed display fragments reach N is cut no more. With it, the drop is D(n) = D0 (1 -
        // log2 n / log2 N)^p from the count n before the cut (art settings p and D0), in place of the horizontal / vertical one.
        public readonly int geometryLimit;
        public readonly double dropExponent, dropBaseMetres;

        public BuildingHullSettings(bool enabled, double deadlineSeconds, double mainBudgetSeconds, double maxNewPenetrationMetres = 0.1, double stageSeconds = 0.0, bool siblingD6 = false, double siblingOpeningMetres = 0.05,
            bool kinematicDisplay = false, double animationSeconds = 0.25, double dropHorizontalMetres = 0.15, double dropVerticalMetres = 0.02,
            int geometryLimit = 0, double dropExponent = 1.0, double dropBaseMetres = 0.5)
        {
            this.enabled = enabled; this.deadlineSeconds = deadlineSeconds; this.mainBudgetSeconds = mainBudgetSeconds; this.maxNewPenetrationMetres = maxNewPenetrationMetres;
            this.stageSeconds = stageSeconds; this.siblingD6 = siblingD6; this.siblingOpeningMetres = siblingOpeningMetres;
            this.kinematicDisplay = kinematicDisplay; this.animationSeconds = animationSeconds; this.dropHorizontalMetres = dropHorizontalMetres; this.dropVerticalMetres = dropVerticalMetres;
            this.geometryLimit = geometryLimit; this.dropExponent = dropExponent; this.dropBaseMetres = dropBaseMetres;
        }

        /// <summary>The cut limit is on: the always-kinematic mode with N given.</summary>
        public bool LimitOn => kinematicDisplay && geometryLimit > 0;

        public bool IsValid => deadlineSeconds >= 0.0 && mainBudgetSeconds > 0.0 && maxNewPenetrationMetres >= 0.0 && stageSeconds >= 0.0 && siblingOpeningMetres > 0.0 && (!siblingD6 || stageSeconds > 0.0)
            && animationSeconds > 0.0 && dropHorizontalMetres >= 0.0 && dropVerticalMetres >= 0.0 && (!kinematicDisplay || (stageSeconds == 0.0 && !siblingD6))
            && (geometryLimit == 0 || (kinematicDisplay && geometryLimit >= 2 && dropExponent > 0.0 && !double.IsNaN(dropExponent) && !double.IsInfinity(dropExponent)
                && dropBaseMetres > 0.0 && !double.IsNaN(dropBaseMetres) && !double.IsInfinity(dropBaseMetres)));
    }

    public enum HullGroupState { Idle = 0, Cutting = 1, Fusing = 2, Gone = 3 }

    /// <summary>
    /// One building group of the hull trial (2026-09-30): one Root, one Rigidbody, **one convex hull** in the group's frame
    /// -- the input of every hit, cut and candidate -- with its cooked mesh on one collider, the anchors as points of that
    /// frame, the mass properties (never recomputed from the members), the display members (fragments with a Root under
    /// the group's, no physics of their own), the Slashes consumed, and a shape generation that moves with every hull.
    /// A group never holds more than its one hull: an aggregation merges bodies only when its candidate hull is adopted.
    /// </summary>
    public sealed class HullGroup : ISlashHullTarget
    {
        internal sealed class DisplayMember
        {
            public LogicalFragmentId fragment;
            public GameObject root;
        }

        internal HullGroup(int id, int building) { Id = id; Building = building; }

        public int Id { get; }
        public int Building { get; }
        public int Generation { get; internal set; }
        public GameObject Root { get; internal set; }
        public Rigidbody Body { get; internal set; }
        public MeshCollider Collider { get; internal set; }
        public PhysicsOwnerShape Shape { get; internal set; }
        internal HullBrep OwnedBrep;
        internal Mesh OwnedMesh;
        internal readonly List<float3> Anchors = new List<float3>();
        internal readonly List<DisplayMember> Members = new List<DisplayMember>();

        /// <summary>
        /// The Slashes this group has taken (by a request accepted -- Pending or Held -- or inherited from the group it was
        /// cut or united from): one reservation a Slash, the request's own while it is open, kept as the lineage's
        /// consumption after it. A Slash reserved here is not taken again by this group or what it becomes; the same Slash
        /// may still cut another group.
        /// </summary>
        internal readonly Dictionary<long, SlashReservation> Reserved = new Dictionary<long, SlashReservation>();
        public double Mass { get; internal set; }
        public float3 Centre { get; internal set; }
        public float3 Inertia { get; internal set; }
        public quaternion InertiaRotation { get; internal set; } = quaternion.identity;
        /// <summary>Whether the body is kinematic now: fixed by an anchor (<see cref="Anchored"/>) or held by the building rest (<see cref="Held"/>).</summary>
        public bool Kinematic { get; internal set; }
        public HullGroupState State { get; internal set; }
        internal double lastCutAt = double.NegativeInfinity;

        /// <summary>Fixed for show by the staged stop (velocity zero, kinematic, followed by the rest as ground): not held by its support, not anchored. Cleared by the group's next cut.</summary>
        public bool Staged { get; internal set; }

        /// <summary>The always-kinematic mode: the generation of the hull (moved by every exchange adopted); a candidate made on another generation is stale.</summary>
        public int HullGeneration { get; internal set; } = 1;

        /// <summary>Whether the group's hull now is an adopted candidate (a fused hull), not a registration's or a cut's side: a record.</summary>
        public bool HullIsFused { get; internal set; }

        public bool Anchored => Anchors.Count > 0;
        /// <summary>Held kinematic by the building rest (a temporary hold, released when its support goes), as opposed to fixed by an anchor.</summary>
        public bool Held => Kinematic && !Anchored;
        public int AnchorCount => Anchors.Count;
        public int MemberCount => Members.Count;
        /// <summary>One, always: a group is its one hull.</summary>
        public int HullCount => 1;
        public int VertexCount => Shape != null && !Shape.IsFreed ? Shape.Convex(0).vertexCount : 0;
        public int FaceCount => Shape != null && !Shape.IsFreed ? Shape.Convex(0).faceCount : 0;
        public int ConsumedCount => Reserved.Count;
        public bool IsConsumedBy(long slashId) => Reserved.ContainsKey(slashId);
        public IEnumerable<LogicalFragmentId> Fragments { get { foreach (DisplayMember m in Members) yield return m.fragment; } }

        internal BuildingHullFusion owner;

        /// <summary>
        /// A building drawn by its own renderers until its first cut is published (TL, 2026-10-03; DESIGN 4.5.1): its
        /// display is registered only at that publication, just before the display cut that takes the drawing over, and
        /// the building's own drawing leaves right after it; a first cut that ends without a publication takes the group
        /// back out (<see cref="BuildingHullFusion.TakeBackUncut"/>). Null for a group shown at its registration, and
        /// once handed over or taken back.
        /// </summary>
        internal FirstCutHandover Handover;

        /// <summary>Whether its first cut has still to take the drawing over (see <see cref="Handover"/>).</summary>
        public bool AwaitsFirstCut => Handover != null;

        internal sealed class FirstCutHandover
        {
            /// <summary>The display registration, at the first publication; false (or a throw) refuses that cut.</summary>
            internal Func<bool> show;

            /// <summary>Once the display has the cut: the building's own drawing and colliders leave.</summary>
            internal Action handedOver;

            /// <summary>The first cut ended without a publication: the world takes what it registered back (the group's own objects go here).</summary>
            internal Action<string> takenBack;
        }

        // ---- the hit target ----
        bool ISlashHullTarget.IsHitTarget => State != HullGroupState.Gone && Root != null && Shape != null && !Shape.IsFreed && !owner.IsCutStopped(Building);
        Transform ISlashHullTarget.Root => Root != null ? Root.transform : null;
        ConvexBrepBank ISlashHullTarget.Bank => Shape.BankOf(0);
        ConvexBrepRange ISlashHullTarget.Convex => Shape.Convex(0);
        void ISlashHullTarget.Bounds(out float3 lo, out float3 hi) => Shape.ConvexBounds(0, out lo, out hi);
        LogicalFragmentId ISlashHullTarget.TraceFragment => Members.Count > 0 ? Members[0].fragment : default;
        bool ISlashHullTarget.IsConsumedBy(long slashId) => Reserved.ContainsKey(slashId);
        ProvisionalCutAcceptance ISlashHullTarget.TryCut(float4 planeRoot, float4 planeWorld, long slashId, long planeId, double at, float3 travelWorld) => owner.TryCut(this, planeRoot, planeWorld, slashId, planeId, travelWorld);
    }

    /// <summary>
    /// One Slash's reservation on a group lineage (2026-09-30): made when a hit is accepted (Pending) or held, carrying
    /// the hit; open until the hit is published, refused, dropped or abandoned, then kept as the lineage's consumption
    /// until the Slash ends. A held hit resumes only where its own reservation stands (the group it was held on, or what
    /// that group became); a second sweep of the same Slash, in the same frame or a later one, finds the group reserved.
    /// </summary>
    public sealed class SlashReservation
    {
        public long slashId;
        public int hitId;
        public bool open;
        public override string ToString() => "slash " + slashId + " hit " + hitId + (open ? " (open)" : " (consumed)");
    }

    /// <summary>
    /// The building hull trial (TL, 2026-09-30; off by default, buildings only, no World D6): a building is one group with
    /// one convex hull; a hit on the hull cuts the hull (the cook, off Main) into two, publishes two groups in one Main
    /// section before the physics step (the positive side keeps the old Root and Body), cuts the display members the
    /// plane crosses through the DAG (no physics of theirs), and the resting sides are aggregated back into one hull -- the
    /// convex hull of all, gap-filling -- by the deadline or the next Slash: the candidate is built and judged first
    /// (off Main, then against everything outside its participants), and only an adopted candidate merges the bodies. A
    /// candidate not adopted leaves the groups apart, each its own hull, each cuttable. Nothing is ever rebuilt from the members.
    /// </summary>
    public sealed partial class BuildingHullFusion : IDisposable
    {
        private readonly BuildingHullSettings _settings;
        private readonly LogicalCutLedger _ledger;
        private readonly PhysicsOwnerRegistry _registry;
        private readonly PhysicsCutCook _cook;
        private readonly CutDag _dag;
        private readonly VpCpuGeometryStorage _storage;
        private readonly SharedWorkDispatcher _dispatcher;
        private readonly BuildingRest _rest;
        private readonly float _supportEpsilon, _anchorEpsilon;
        private readonly int _vertexLimit;
        private readonly Func<double> _physicsSeconds;
        private Func<double> _realSeconds;

        /// <summary>Tests only: the trial's real clock replaced (a test clock that does not depend on the Editor's speed).</summary>
        internal Func<double> RealSecondsForTest { set => _realSeconds = value ?? (() => Time.realtimeSinceStartupAsDouble); }
        private readonly List<HullGroup> _groups = new List<HullGroup>();
        private readonly Dictionary<int, HullGroup> _byId = new Dictionary<int, HullGroup>();
        private int _lastGroupId, _lastHitId, _lastBuilding;
        private bool _disposed;

        public BuildingHullFusion(BuildingHullSettings settings, LogicalCutLedger ledger, PhysicsOwnerRegistry registry, PhysicsCutCook cook, CutDag dag, VpCpuGeometryStorage storage, SharedWorkDispatcher dispatcher,
            BuildingRest rest, float supportEpsilon, float anchorEpsilon, int vertexLimit, Func<double> physicsSeconds, Func<double> realSeconds)
        {
            if (!settings.IsValid) throw new ArgumentException("the building hull settings are not valid", nameof(settings));
            _settings = settings;
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _cook = cook ?? throw new ArgumentNullException(nameof(cook));
            _dag = dag ?? throw new ArgumentNullException(nameof(dag));
            _storage = storage ?? throw new ArgumentNullException(nameof(storage));
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher), "the hull trial's fusions run on the dispatcher");
            _rest = rest ?? throw new ArgumentNullException(nameof(rest), "the hull trial's groups rest by the building rest");
            if (!rest.Enabled || rest.Settings.mode != BuildingRestMode.Kinematic) throw new ArgumentException("the hull trial needs the kinematic building rest", nameof(rest));
            _supportEpsilon = supportEpsilon;
            _anchorEpsilon = anchorEpsilon;
            _vertexLimit = vertexLimit;
            _physicsSeconds = physicsSeconds ?? (() => 0.0);
            _realSeconds = realSeconds ?? (() => Time.realtimeSinceStartupAsDouble);
        }

        public BuildingHullSettings Settings => _settings;
        public bool Enabled => _settings.enabled && !_disposed;
        public MeshColliderCookingOptions Cooking { get; set; } = PhysicsCutCook.DefaultCooking;
        public float DefaultSeparationImpulse { get; set; }
        public IReadOnlyList<HullGroup> Groups => _groups;
        public bool TryGetGroup(int id, out HullGroup group) => _byId.TryGetValue(id, out group) && group.State != HullGroupState.Gone;

        /// <summary>The hit targets now: every group standing (for the detector).</summary>
        public void CollectTargets(List<ISlashHullTarget> into)
        {
            foreach (HullGroup g in _groups) if (g.State != HullGroupState.Gone && g.Root != null && !IsCutStopped(g.Building)) into.Add(g);   // a building past its cut limit is no target
        }

        /// <summary>A Slash that ended gives its consumption back: no group keeps it (the detector says which are live) -- except a request still open (Pending or Held), which outlives its Slash until it is answered.</summary>
        public void KeepOnlyLiveSlashes(ReadOnlySpan<long> live)
        {
            _liveScratch.Clear();
            foreach (long id in live) _liveScratch.Add(id);
            foreach (HullGroup g in _groups)
            {
                _reservationScratch.Clear();
                foreach (KeyValuePair<long, SlashReservation> r in g.Reserved) if (!r.Value.open && !_liveScratch.Contains(r.Key)) _reservationScratch.Add(r.Key);
                foreach (long id in _reservationScratch) g.Reserved.Remove(id);
            }
        }

        private readonly HashSet<long> _liveScratch = new HashSet<long>();
        private readonly List<long> _reservationScratch = new List<long>();

        /// <summary>Hits refused at the acceptance because the group was already reserved for the Slash (a second sweep of the same frame, or a later frame's): no record of their own.</summary>
        public int HitsDuplicate { get; private set; }

        // ---- registration ----

        /// <summary>
        /// A building enters the trial: its group hull is the convex hull of every vertex of the shape given (in the actor's
        /// frame, through the shape's placement), made on its collider; the actor's body carries the mass given with the
        /// hull's own centre and inertia; the anchors (actor frame) hold it kinematic; the display fragment is a member
        /// with a Root of its own under the actor. The shape given is only read here.
        /// </summary>
        public HullGroup Register(GameObject actor, Rigidbody body, PhysicsOwnerShape shape, LogicalFragmentId displayFragment, IReadOnlyList<float3> anchors, double mass, out string refusal)
        {
            refusal = null;
            var points = new List<float3>();
            for (int c = 0; c < shape.ConvexCount; c++) HullBrep.CopyVertices(shape.BankOf(c), shape.Convex(c), points, shape.LocalToOwner);
            HullBrep brep = HullBrep.OfPoints(points, out refusal);

            if (brep == null) return null;
            if (brep.VertexCount > _vertexLimit) { brep.Dispose(); refusal = "the hull has " + brep.VertexCount + " vertices, past the limit " + _vertexLimit; return null; }
            HullGroup group = null;
            try
            {
                group = new HullGroup(++_lastGroupId, ++_lastBuilding) { owner = this, Root = actor, Body = body, Generation = 1 };
                return RegisterMade(group, brep, actor, body, displayFragment, anchors, mass);
            }
            catch
            {
                // Nothing of a registration that threw stays: what it had made goes, and the error is passed on (the
                // actor is the caller's).
                if (group != null) Unregister(group, "an exception while it was registered");
                else brep.Dispose();
                throw;
            }
        }

        /// <summary>Tests only: called in Register once the group's hull, mesh and collider are made, before it is listed.</summary>
        internal static Action registerHookForTest;

        private HullGroup RegisterMade(HullGroup group, HullBrep brep, GameObject actor, Rigidbody body, LogicalFragmentId displayFragment, IReadOnlyList<float3> anchors, double mass)
        {
            group.OwnedBrep = brep;
            group.OwnedMesh = brep.MakeColliderMesh("Building hull " + group.Id, Cooking);
            MeshesBaked++;
            group.Shape = PhysicsOwnerShape.Authored(brep.Bank, new[] { brep.Range }, new List<Mesh> { group.OwnedMesh }, PhysicsShapeSource.External(), float4x4.identity);
            group.Collider = MakeCollider(actor, group.Shape);
            if (anchors != null) group.Anchors.AddRange(anchors);
            group.Kinematic = group.Anchored || _settings.kinematicDisplay;   // the always-kinematic mode: kinematic whatever its anchors, for good
            body.isKinematic = group.Kinematic;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            SetMassFromHull(group, brep, mass);
            group.Members.Add(new HullGroup.DisplayMember { fragment = displayFragment, root = NewMemberRoot(actor.transform, actor.transform, "Display member " + displayFragment.value) });
            registerHookForTest?.Invoke();
            _groups.Add(group);
            _byId[group.Id] = group;
            GroupsMade++;
            NoteHullSize(group);
            NoteLiveHulls();
            // The rest follows the group's body from here: an anchored one as ground, a free one for its rest by the rest's clock and support.
            if (!_settings.kinematicDisplay) _rest.TrackGroup(body, group.Building, new[] { group.Collider }, _physicsSeconds(), group.Anchored);   // the always-kinematic mode is not the rest's: no hold, no release, no wake
            Record("registered group " + group.Id + " building " + group.Building + ": hull " + brep.VertexCount + " vertices " + brep.FaceCount + " faces, anchors " + group.AnchorCount + ", mass " + mass.ToString("F3") + (group.Anchored ? " (anchored)" : " (free)"));
            return group;
        }

        /// <summary>
        /// A group whose registration did not complete (2026-10-03): an exception in <see cref="Register"/>, or one the
        /// world met after it. Everything the registration made goes -- its listing, the rest's tracking of its body, its
        /// collider and display member objects, its hull, mesh and shape -- once; the actor stays the caller's. Nothing of
        /// the display's is touched here.
        /// </summary>
        internal void Unregister(HullGroup group, string why)
        {
            if (group == null || group.State == HullGroupState.Gone) return;
            group.State = HullGroupState.Gone;
            bool listed = _groups.Remove(group);
            _byId.Remove(group.Id);
            if (group.Body != null) _rest.Untrack(group.Body, why);
            if (group.Collider != null && group.Root != null) PhysicsOwnerBuilder.DestroyComponent(group.Collider, group.Root);
            foreach (HullGroup.DisplayMember m in group.Members) PhysicsOwnerBuilder.DestroyObject(m.root);
            group.Members.Clear();
            group.Shape?.Dispose();
            group.OwnedBrep?.Dispose();
            if (group.OwnedMesh != null) PhysicsCutCook.DestroyMesh(group.OwnedMesh);
            group.Root = null; group.Body = null; group.Collider = null;
            group.Shape = null; group.OwnedBrep = null; group.OwnedMesh = null;
            GroupsUnregistered++;
            if (listed) NoteLiveHulls();
            Record("group " + group.Id + " unregistered (" + why + ")");
        }

        /// <summary>Groups whose registration did not complete and were taken out again (<see cref="Unregister"/>).</summary>
        public int GroupsUnregistered { get; private set; }

        // ---- the building rest's notices: the groups' holds and releases are the rest's, by its clock and support ----

        private bool TryGroupOf(Rigidbody body, out HullGroup group)
        {
            group = null;
            if (body == null) return false;
            foreach (HullGroup g in _groups) if (g.State != HullGroupState.Gone && g.Body == body) { group = g; return true; }
            return false;
        }

        /// <summary>The rest asks whether a group is a rest candidate: not while it is cut or a participant of a candidate.</summary>
        public bool IsGroupBusy(Rigidbody body) => TryGroupOf(body, out HullGroup g) && g.State != HullGroupState.Idle;

        /// <summary>The rest held a group's body kinematic (its clock ran out on confirmed support): the group's state follows, and the building's aggregation is looked at again (the fixed and free classes changed).</summary>
        public void OnGroupHeld(Rigidbody body)
        {
            if (!TryGroupOf(body, out HullGroup g)) return;
            g.Kinematic = true;
            GroupsHeld++;
            Record("group " + g.Id + " held by the rest t " + _physicsSeconds().ToString("F3"));
            Reevaluate(g.Building, "held");
        }

        /// <summary>The rest is about to return a held group to dynamic (its support went): the group's mass properties are applied first, its state follows, and the building's aggregation is looked at again.</summary>
        public void OnGroupReleasing(Rigidbody body)
        {
            if (!TryGroupOf(body, out HullGroup g)) return;
            g.Kinematic = false;
            ApplyMass(g);
            GroupsReleased++;
            Record("group " + g.Id + " released by the rest t " + _physicsSeconds().ToString("F3") + " (support lost)");
            Reevaluate(g.Building, "released");
        }

        private static GameObject NewMemberRoot(Transform under, Transform at, string name)
        {
            var root = new GameObject(name);
            root.transform.SetPositionAndRotation(at.position, at.rotation);
            CutPhysicsStep.NoteUnpublishedPlacementChange();   // a root nothing is asked through yet, placed (D-204; no held placement is concerned, D-207)
            root.transform.SetParent(under, true);
            return root;
        }

        private MeshCollider MakeCollider(GameObject root, PhysicsOwnerShape shape)
        {
            MeshCollider collider = PhysicsOwnerBuilder.CreateMeshCollider(root, shape.MeshFrameOf(0));
            collider.cookingOptions = Cooking;
            collider.convex = true;
            collider.providesContacts = true;
            collider.sharedMesh = shape.MeshOf(0);
            CollidersMade++;
            return collider;
        }

        private static void SetMassFromHull(HullGroup group, HullBrep brep, double mass)
        {
            MassProperties m = brep.Mass();
            double3 centre = m.CenterOfMass;
            SymmetricMatrix3 inertia = m.InertiaAboutCom() * (mass / m.volume);
            group.Mass = mass;
            group.Centre = (float3)centre;
            if (PrincipalInertia.TryDiagonalize(in inertia, out double3 moments, out quaternion rotation) && math.all(moments > 0.0)) { group.Inertia = (float3)moments; group.InertiaRotation = rotation; }
            else { group.Inertia = new float3(1f, 1f, 1f) * (float)mass; group.InertiaRotation = quaternion.identity; }
            ApplyMass(group);
        }

        private static void ApplyMass(HullGroup group)
        {
            Rigidbody body = group.Body;
            if (body == null) return;
            body.mass = (float)group.Mass;
            body.centerOfMass = group.Centre;
            body.inertiaTensor = group.Inertia;
            body.inertiaTensorRotation = group.InertiaRotation;
        }

        private static MassProperties MassOf(PhysicsOwnerShape shape)
        {
            var m = new MassProperties();
            BrepBuffer view = shape.BankOf(0).View(shape.Convex(0));
            MassPropertiesKernel.ComputeDouble(in view, ref m);
            return m;
        }

        // ---- the hits: one record each ----

        public sealed class HullHit
        {
            public int id;
            public long slashId, planeId;
            public int group, generation;
            public double physicsSeconds, askedAt, publishedAt = double.NaN, answeredAt = double.NaN;
            public bool held;   // held at its acceptance (behind a cut, a candidate or a due class): a record
            public string hullOutcome;   // the always-kinematic mode: what the hull's update came to, apart from the hit's outcome (the display's)
            public float3 travelWorld;   // the sweep's travel in the world (kept for a held hit's resumption)
            public string slideRule;   // the always-kinematic mode: how the slide's direction was chosen
            public float3 slideWorld, normalWorld;   // the always-kinematic mode: the move given to the display and the hull (world), the plane's unit normal
            public int membersClassified = -1, membersHeldAfter = -1;   // the always-kinematic mode: the members classified for this hit, held after its publication
            public double publishMs = double.NaN;
            public int prepareUnits, prepareFirstFrame = -1, prepareLastFrame = -1;
            public int membersByExtent, membersScanned;   // the members classified by the committed geometry's recorded extent alone, and by reading its indices   // the preparation's units under the budget and the frames they fell in (observation)
            public string state;   // "prepared", "held", "refused"
            public string outcome;   // null while pending
            public int cookSerial = -1;
            public string failedMesh;
            public readonly List<CutOperationId> displayOperations = new List<CutOperationId>();
            public int positiveGroup = -1, negativeGroup = -1;
            public int geometriesBefore = -1, geometriesAfter = -1;   // the cut limit: the building's display geometries the cut went on from, and once settled after it (-1: not counted)
            public double limitDistance = double.NaN;   // the cut limit: the drop D(n) the cut took
            internal SlashReservation reservation;   // open while the request is Pending or Held; consumed after
            public bool IsPending => outcome == null;
        }

        private readonly List<HullHit> _hits = new List<HullHit>();
        public IReadOnlyList<HullHit> Hits => _hits;
        public int HitsPending { get { int n = 0; foreach (HullHit h in _hits) if (h.IsPending) n++; return n; } }

        private enum CutStage { Preparing = 0, Cooking = 1, WaitingForRoom = 2, Display = 3 }

        /// <summary>A display member's sides against the cut's plane, read from its display vertices: +1 (all positive), -1 (all negative), 0 (both: crossed), 2 (no display: an empty side of an earlier cut; stays with the positive side).</summary>
        private sealed class MemberSide
        {
            public HullGroup.DisplayMember member;
            public int side;
            public float4 planeMember;
        }

        private sealed class PendingCut
        {
            public HullGroup group;
            public int generation;
            public HullHit hit;
            public CutStage stage;
            public PhysicsCutClassification classification;
            public PhysicsCutRequest request;
            public float4 planeGroup, planeWorld;
            public int cursor;   // the members scanned so far (a preparation spread over Steps)
            public readonly List<MemberSide> sides = new List<MemberSide>();
            public int roomWaits;
        }

        private sealed class HeldHit
        {
            public HullGroup group;
            public HullHit hit;
            public float4 planeWorld;
            public long slashId, planeId;
        }

        private readonly List<PendingCut> _cuts = new List<PendingCut>();
        private readonly List<HeldHit> _held = new List<HeldHit>();
        public int CutsInProgress => _cuts.Count;
        public int HeldNow => _held.Count;

        /// <summary>Tests only: a reason makes every real hit refused at once (NotAccepted), as a refusal at the acceptance would; the group stays reserved for the Slash all the same.</summary>
        internal Func<HullGroup, string> refuseHitForTest;

        /// <summary>
        /// A hit on a group's hull (the detector's, the plane in the group's frame): recorded at once, and the group
        /// reserved for the Slash from this record on -- a refusal at once leaves the reservation closed (consumed), so
        /// that no later sweep of the Slash takes the group again while the Slash flies; the same Slash may still cut
        /// another group. A group cutting or a participant of a candidate, or a building whose resting class is due to be
        /// aggregated, holds it (routed to what results); an idle group has its cut prepared under the Step's budget --
        /// Pending from here.
        /// </summary>
        internal ProvisionalCutAcceptance TryCut(HullGroup group, float4 planeGroup, float4 planeWorld, long slashId, long planeId, float3 travelWorld = default)
        {
            long begin = Stopwatch.GetTimestamp();
            try
            {
                if (!Enabled || group.State == HullGroupState.Gone || group.Root == null) return ProvisionalCutAcceptance.InvalidRequest;
                // The candidates were collected before this frame's first acceptance: a second sweep of the same Slash
                // finds the group reserved here (a later frame's is kept out by the detector).
                if (group.Reserved.ContainsKey(slashId)) { HitsDuplicate++; return ProvisionalCutAcceptance.NotAccepted; }
                var hit = new HullHit { id = ++_lastHitId, slashId = slashId, planeId = planeId, group = group.Id, generation = group.Generation, physicsSeconds = _physicsSeconds(), askedAt = _realSeconds(), travelWorld = travelWorld };
                _hits.Add(hit);
                hit.reservation = new SlashReservation { slashId = slashId, hitId = hit.id, open = false };
                group.Reserved[slashId] = hit.reservation;   // consumed from the record on, whatever the answer; open only while the request is Held or Pending
                string refusal = refuseHitForTest?.Invoke(group);
                if (refusal != null)
                {
                    Finish(hit, "NotAccepted: " + refusal, false);
                    Record("hit " + hit.id + " slash " + slashId + " on group " + group.Id + " refused at once: " + refusal + " (the group stays reserved for the Slash)");
                    return ProvisionalCutAcceptance.NotAccepted;
                }

                // The cut limit (2026-10-01): a building cut no more refuses here, before anything of it is touched.
                if (LimitOn && IsCutStopped(group.Building))
                {
                    RefuseByLimit(hit, "NotAccepted: the building's cut limit was reached (N = " + _settings.geometryLimit + ")");
                    return ProvisionalCutAcceptance.NotAccepted;
                }

                // A next Slash on a building whose resting class waits: the class is aggregated first, and the hit is held for what results.
                bool nextSlash = false;
                BuildingWait w = WaitOf(group.Building);
                if (w != null && !w.nextSlash) { w.nextSlash = true; nextSlash = HasDueUnion(w); }
                else if (w != null) nextSlash = HasDueUnion(w);
                StopPairsOf(group.Building, "the next Slash's hit " + hit.id);   // accepted from here (held or pending): the moving sides of the building are fixed first
                if (group.State != HullGroupState.Idle || nextSlash || BuildingBusy(group.Building))
                {
                    hit.state = "held";
                    hit.held = true;
                    hit.reservation.open = true;   // held: resumed where this reservation stands
                    _held.Add(new HeldHit { group = group, hit = hit, planeWorld = planeWorld, slashId = slashId, planeId = planeId });
                    HitsHeld++;
                    Record("hit " + hit.id + " held: group " + group.Id + " is " + group.State + (nextSlash ? " (a next Slash: the building's resting class is aggregated first)" : ""));
                    return ProvisionalCutAcceptance.Held;
                }

                // The cut limit at the cut's start: n counted settled (the building is not busy here), the drop computed once.
                if (!PassesLimit(group, hit, out string limitRefusal))
                {
                    RefuseByLimit(hit, limitRefusal);
                    return ProvisionalCutAcceptance.NotAccepted;
                }

                hit.reservation.open = true;
                hit.state = "prepared";
                group.State = HullGroupState.Cutting;
                _cuts.Add(new PendingCut { group = group, generation = group.Generation, hit = hit, planeGroup = planeGroup, planeWorld = planeWorld, stage = CutStage.Preparing });
                Record("hit " + hit.id + " slash " + slashId + " on group " + group.Id + " (generation " + group.Generation + "): pending, prepared under the budget");
                return ProvisionalCutAcceptance.Pending;
            }
            finally
            {
                HitSeconds += (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
            }
        }

        /// <summary>Whether something of a building waits that a held hit must not overtake: a group cutting or a participant of a candidate, a resting class due to be aggregated, a candidate in flight. A class judged apart, or a refused scan awaiting its retry, is not: its groups are cuttable.</summary>
        private bool BuildingBusy(int building)
        {
            foreach (HullGroup g in _groups) if (g.Building == building && g.State != HullGroupState.Gone && g.State != HullGroupState.Idle) return true;
            BuildingWait wait = WaitOf(building);
            if (wait != null && HasDueUnion(wait)) return true;
            if (LimitOn && DisplayOpen(building)) return true;   // the cut limit: the next cut waits for the last one's display operations, its n then settled
            return HasCandidateInFlight(building);
        }

        /// <summary>
        /// One unit of a cut's preparation under the budget: the hull classified (once), then the display members' sides
        /// read from their display vertices, some members a Step. True when the preparation ended (submitted, or the hit
        /// answered without a cut).
        /// </summary>
        private bool Prepare(PendingCut cut)
        {
            HullGroup group = cut.group;
            cut.hit.prepareUnits++;
            if (cut.hit.prepareFirstFrame < 0) cut.hit.prepareFirstFrame = Time.frameCount;
            cut.hit.prepareLastFrame = Time.frameCount;
            if (_settings.kinematicDisplay && cut.cursor == 0 && cut.sides.Count == 0) StopAnimationsOf(group, "a re-cut", cut.hit.id);   // cut from the pose the display has now
            if (cut.classification == null && !_settings.kinematicDisplay)
            {
                if (!PhysicsCutClassification.TryClassify(group.Shape, cut.planeGroup, _supportEpsilon, group.Mass, _vertexLimit, out cut.classification))
                {
                    EndWithoutCut(cut, "NotAccepted: the hull could not be classified");
                    return true;
                }

                if (!cut.classification.SplitsBothSides)
                {
                    EndWithoutCut(cut, "EmptySide: the plane does not cross the hull");
                    return true;
                }
            }

            long scanBegin = Stopwatch.GetTimestamp();
            try
            {
            while (cut.cursor < group.Members.Count)
            {
                if (cut.cursor > 0 && !MayStart()) return false;
                HullGroup.DisplayMember m = group.Members[cut.cursor];
                float4x4 memberToGroup = math.mul(math.inverse((float4x4)group.Root.transform.localToWorldMatrix), (float4x4)m.root.transform.localToWorldMatrix);
                float4 planeMember = BuildingFusion.TransformPlane(math.inverse(memberToGroup), cut.planeGroup);
                planeMember /= math.length(planeMember.xyz);
                if (!DisplaySides(m.fragment, planeMember, out bool positive, out bool negative, out bool byExtent))
                {
                    return false;   // its geometry is not committed yet (a child of the last cut): the preparation waits here
                }

                if (byExtent) cut.hit.membersByExtent++; else cut.hit.membersScanned++;
                cut.sides.Add(new MemberSide { member = m, side = positive && negative ? 0 : positive ? 1 : negative ? -1 : 2, planeMember = planeMember });
                cut.cursor++;
                DisplayMembersScanned++;
            }
            }
            finally
            {
                PrepareDisplaySeconds += (Stopwatch.GetTimestamp() - scanBegin) / (double)Stopwatch.Frequency;
            }

            int positives = 0, negatives = 0, crossed = 0;
            foreach (MemberSide s in cut.sides) { if (s.side == 1) positives++; else if (s.side == -1) negatives++; else if (s.side == 0) crossed++; }   // 2: no display, on no side
            if (crossed == 0 && (positives == 0 || negatives == 0))
            {
                NoChanges++;
                EndWithoutCut(cut, "NoChange: the display stands on one side only (" + positives + " positive, " + negatives + " negative)");
                return true;
            }

            if (_settings.kinematicDisplay)
            {
                cut.stage = CutStage.Display;   // published by the display alone at the next unit; no cook
                return false;
            }

            group.Shape.AcquireForWork();
            ConvexCutOwnerInput input = cut.classification.Input;
            cut.request = _cook.Submit(in input, float4x4.identity);
            cut.request.hullOwnerChecks = true;
            cut.stage = CutStage.Cooking;
            CookRequests++;
            Record("hit " + cut.hit.id + ": cut submitted on group " + group.Id + "; display " + positives + "+/" + negatives + "-/" + crossed + " crossed");
            return true;
        }

        /// <summary>
        /// A display member's presence on each side of a plane (the member's frame), read from its display vertices --
        /// the actual geometry, not its range. False while the member's geometry is not committed.
        /// </summary>
        private bool DisplaySides(LogicalFragmentId fragment, float4 planeMember, out bool positive, out bool negative) => DisplaySides(fragment, planeMember, out positive, out negative, out _);

        // The float guard beside the side tolerance, per unit of the plane's magnitude over the box (2026-10-01): the
        // extent's box comes back as a centre and a size, and the plane's value at a corner and at a vertex are each
        // rounded; a box closer than the tolerance and this guard to the plane is read by its indices instead.
        private const float ExtentGuard = 1e-5f;

        private bool DisplaySides(LogicalFragmentId fragment, float4 planeMember, out bool positive, out bool negative, out bool byExtent)
        {
            positive = negative = false;
            byExtent = false;
            if (_dag.HasNoGeometry(fragment) || !_ledger.IsCurrentTarget(fragment)) return true;   // a side an earlier cut left empty, or a member gone: on no side, never waited for
            if (!_dag.TryGetGeometry(fragment, out VpStoredGeometry geometry) || !_dag.TryGetGeometryFrame(fragment, out Matrix4x4 lineageToGeometryLocal))
            {
                // Its geometry is still being made -- or never will be: the cut that made it ended without a commit.
                if (_ledger.TryGetOrigin(fragment, out CutOperationId origin, out float _) && _dag.StageOf(origin) == CutGeometryStage.Reclaimed) { MembersWithoutGeometry++; return true; }
                return false;
            }
            float4 plane = BuildingFusion.TransformPlane((float4x4)lineageToGeometryLocal, planeMember);
            NativeArray<VpRenderVertex>.ReadOnly vertices = _storage.Vertices;
            bool above = false, below = false;
            if (!classifyByBlocksForTest)
            {
                // The vertices the geometry's published indices use -- its body and its cap, every submesh -- and none
                // other: a child names its ancestors' blocks, and the vertices its indices do not use are not its shape
                // (2026-09-30: scanning the blocks made a child on one side of a plane "crossed" by an ancestor's vertices).
                if (refuseIndexLeaseForTest || !_storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly indices))
                {
                    DisplayIndexLeasesRefused++;
                    return false;   // not readable now: the preparation waits (a committed geometry's range is published)
                }

                try
                {
                    // Certainly on one side (2026-10-01): the committed geometry's own recorded extent -- the bounds of the
                    // vertices its indices name, in the same coordinates, not its ancestors' blocks -- wholly beyond the
                    // tolerance and a float guard on one side. A box across the plane, near it, or not recorded is read
                    // by its indices as before; a box across the plane is not taken as crossed.
                    if (!classifyExtentOffForTest && _storage.TryGetPublishedExtent(geometry, out _, out int referenced, out Bounds box) && referenced > 0)
                    {
                        float3 lo = box.min, hi = box.max, n = plane.xyz;
                        float least = math.dot(n, math.select(hi, lo, n >= 0f)) + plane.w;
                        float most = math.dot(n, math.select(lo, hi, n >= 0f)) + plane.w;
                        float guard = ExtentGuard * (math.csum(math.abs(n) * math.max(math.abs(lo), math.abs(hi))) + math.abs(plane.w));
                        if (least > _supportEpsilon + guard) { positive = true; byExtent = true; }
                        else if (most < -_supportEpsilon - guard) { negative = true; byExtent = true; }
                        if (byExtent) { DisplayMembersByExtent++; return true; }
                    }
                    else
                    {
                        DisplayExtentsUnavailable++;
                    }

                    DisplayMembersReadByIndices++;
                    DisplayIndexRangeLengths += indices.Length;
                    int read = 0;
                    for (int i = 0; i < indices.Length; i++)
                    {
                        read++;
                        int v = (int)indices[i];
                        if ((uint)v >= (uint)vertices.Length) continue;
                        DisplayVertexTests++;
                        float sd = math.dot(plane.xyz, (float3)vertices[v].position) + plane.w;
                        if (sd > _supportEpsilon) above = true; else if (sd < -_supportEpsilon) below = true;
                        if (above && below) break;
                    }

                    DisplayIndicesRead += read;
                }
                finally
                {
                    _storage.TryReleaseIndexReadLease(lease);
                }

                positive = above; negative = below;
                return true;
            }

            if (_storage.TryGetVertexBlocks(geometry, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out _))
            {
                foreach (VpGeometryVertexBlock block in blocks) { ScanSides(vertices, block.vertexStart, block.vertexCount, plane, _supportEpsilon, ref above, ref below); if (above && below) break; }
            }
            else
            {
                ScanSides(vertices, geometry.vertexStart, geometry.vertexCount, plane, _supportEpsilon, ref above, ref below);
            }

            positive = above; negative = below;
            return true;
        }

        /// <summary>Tests only: classify a member's display by its vertex blocks (the former reading), to show its false crossings beside the indices' reading.</summary>
        internal bool classifyByBlocksForTest;

        /// <summary>Tests only: every member read by its indices, the extent not asked (the reference the extent's decisions are compared with).</summary>
        internal bool classifyExtentOffForTest;

        /// <summary>Tests only: the index lease refused, as when the range cannot be read now.</summary>
        internal bool refuseIndexLeaseForTest;

        /// <summary>
        /// The classification's reads (2026-10-01): the members decided by their extent alone, the members read by their
        /// indices, the indices actually read (a scan stops once both sides are seen), the vertices tested, the total
        /// length of the index ranges the scans were given (the figure called "indices read" before 2026-10-01), the
        /// members whose extent was not recorded, and the leases refused (the preparation waited).
        /// </summary>
        public int DisplayMembersByExtent { get; private set; }
        public int DisplayMembersReadByIndices { get; private set; }
        public long DisplayIndicesRead { get; private set; }
        public long DisplayVertexTests { get; private set; }
        public long DisplayIndexRangeLengths { get; private set; }
        public int DisplayExtentsUnavailable { get; private set; }
        public int DisplayIndexLeasesRefused { get; private set; }

        /// <summary>Tests only: a member's display classified against a plane in its frame, by its indices or (<paramref name="byBlocks"/>) by its vertex blocks. False while its geometry is not committed.</summary>
        internal bool ClassifyForTest(LogicalFragmentId fragment, float4 planeMember, bool byBlocks, out bool positive, out bool negative)
            => ClassifyForTest(fragment, planeMember, byBlocks, false, out positive, out negative, out _);

        /// <summary>Tests only: the same, with the extent's decision left out when <paramref name="extentOff"/> (the indices read for every member), and whether the extent decided.</summary>
        internal bool ClassifyForTest(LogicalFragmentId fragment, float4 planeMember, bool byBlocks, bool extentOff, out bool positive, out bool negative, out bool byExtent)
        {
            bool was = classifyByBlocksForTest, wasOff = classifyExtentOffForTest;
            classifyByBlocksForTest = byBlocks;
            classifyExtentOffForTest = extentOff;
            try { return DisplaySides(fragment, planeMember, out positive, out negative, out byExtent); }
            finally { classifyByBlocksForTest = was; classifyExtentOffForTest = wasOff; }
        }

        private static void ScanSides(NativeArray<VpRenderVertex>.ReadOnly vertices, int start, int count, float4 plane, float epsilon, ref bool positive, ref bool negative)
        {
            for (int i = start; i < start + count && i < vertices.Length; i++)
            {
                float s = math.dot(plane.xyz, (float3)vertices[i].position) + plane.w;
                if (s > epsilon) positive = true; else if (s < -epsilon) negative = true;
                if (positive && negative) return;
            }
        }

        private void EndWithoutCut(PendingCut cut, string outcome)
        {
            cut.classification?.Dispose();
            cut.classification = null;
            cut.group.State = HullGroupState.Idle;
            Finish(cut.hit, outcome, false);
        }

        private void Finish(HullHit hit, string outcome, bool published)
        {
            if (!published && _byId.TryGetValue(hit.group, out HullGroup awaiting) && awaiting.Handover != null && !_takeBack.Exists(t => ReferenceEquals(t.group, awaiting)))
            {
                _takeBack.Add((awaiting, outcome));   // taken back at the Step's end (or by the caller of an answer given at once)
            }

            hit.outcome = outcome;
            hit.answeredAt = _realSeconds();
            if (hit.reservation != null) hit.reservation.open = false;   // collected: the lineage's consumption from here, until the Slash ends
            if (published) { HitsPublished++; hit.publishedAt = _realSeconds(); double wait = hit.publishedAt - hit.askedAt; MaxAskToPublishSeconds = Math.Max(MaxAskToPublishSeconds, wait); AskToPublishSeconds += wait; }
            else HitsRefused++;
        }

        // ---- the Step ----

        private readonly Stopwatch _stepWatch = new Stopwatch();
        private long _budgetStart;
        private bool _inStep;
        private long _lastStepId = -1;

        private bool MayStart()
        {
            if (!_inStep || (Stopwatch.GetTimestamp() - _budgetStart) / (double)Stopwatch.Frequency < _settings.mainBudgetSeconds) return true;
            Deferred++;
            return false;
        }

        // The largest indivisible unit of the current Step, so that an overrun names what it could not split.
        private string _stepUnit;
        private double _stepUnitSeconds;

        private void Unit(string name, long begin)
        {
            double s = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
            if (s > _stepUnitSeconds) { _stepUnitSeconds = s; _stepUnit = name; }
        }

        /// <summary>The Steps over the budget: each's length and its largest unit, newest last (at most 64).</summary>
        public IReadOnlyList<string> OverrunUnits => _overrunUnits;

        private readonly List<string> _overrunUnits = new List<string>();

        /// <summary>One turn a frame on Main, before the physics step: the cuts' preparations, checks and publications, the fusions' stages, the held hits. The groups' rest is the building rest's turn.</summary>
        // ---- a first cut that did not take the drawing over: the group taken back out ----

        private readonly List<(HullGroup group, string why)> _takeBack = new List<(HullGroup, string)>();

        /// <summary>Groups taken back out after a first cut ended without a publication (see <see cref="HullGroup.Handover"/>).</summary>
        public int UncutTakenBack { get; private set; }

        /// <summary>Groups whose first cut took the drawing over (see <see cref="HullGroup.Handover"/>).</summary>
        public int HandedOver { get; private set; }

        private void TakeBackQueued()
        {
            for (int i = 0; i < _takeBack.Count; i++) TakeBackUncut(_takeBack[i].group, _takeBack[i].why);
            _takeBack.Clear();
        }

        /// <summary>
        /// A building whose first cut ended without a publication, taken back out once (TL, 2026-10-03): its hits held, and
        /// any hit of it still unanswered, are abandoned (each its one outcome), the world's part given back by the hand-over's own take-back (its owner, fragment, geometry), then
        /// the group's objects -- its Root (the actor), collider, hull mesh and shape. Nothing of it was ever shown, so its
        /// own drawing never left. A group already handed over, taken back or gone is left as it is.
        /// </summary>
        internal void TakeBackUncut(HullGroup group, string why)
        {
            if (group == null || group.State == HullGroupState.Gone || group.Handover == null) return;
            HullGroup.FirstCutHandover handover = group.Handover;
            group.Handover = null;
            for (int i = _held.Count - 1; i >= 0; i--)
            {
                if (!ReferenceEquals(_held[i].group, group)) continue;
                Finish(_held[i].hit, "Abandoned: the building was taken back out before its first cut", false);
                _held.RemoveAt(i);
            }

            // A hit recorded for the group but never answered (its acceptance threw after the record) gets its one outcome too.
            foreach (HullHit h in _hits)
            {
                if (h.group == group.Id && h.IsPending) Finish(h, "Abandoned: the building was taken back out before its first cut", false);
            }

            _cuts.RemoveAll(c => ReferenceEquals(c.group, group));
            _waitingRequest.Remove(group);
            try
            {
                handover.takenBack?.Invoke(why);
            }
            finally
            {
                group.State = HullGroupState.Gone;
                _byId.Remove(group.Id);
                _groups.Remove(group);
                PhysicsOwnerBuilder.DestroyObject(group.Root);
                group.Root = null; group.Body = null; group.Collider = null;
                group.Shape?.Dispose();
                group.OwnedBrep?.Dispose();
                if (group.OwnedMesh != null) PhysicsCutCook.DestroyMesh(group.OwnedMesh);
                group.Shape = null; group.OwnedBrep = null; group.OwnedMesh = null;
                UncutTakenBack++;
                NoteLiveHulls();
                Record("group " + group.Id + " taken back out before its first cut (" + why + "): nothing of it was shown, its building drawn as placed");
            }
        }

        public void Step()
        {
            if (!Enabled) return;
            _stepWatch.Restart();
            _budgetStart = Stopwatch.GetTimestamp();
            _inStep = true;
            _stepUnit = null;
            _stepUnitSeconds = 0.0;
            try
            {
                StepPairs();
                AdvanceCuts();   // the drops are placed with the physics steps (StepDropsAfterPhysicsStep), not here
                StepHullUpdates();
                StepFusions();
                RouteHeld();
                SettleDisplayOperations();
                SettleLimits();
                NoteSpeeds();
                TakeBackQueued();
            }
            finally
            {
                _inStep = false;
                _stepWatch.Stop();
                double s = _stepWatch.Elapsed.TotalSeconds;
                StepSeconds += s;
                MaxStepSeconds = Math.Max(MaxStepSeconds, s);
                if (s > _settings.mainBudgetSeconds)
                {
                    OverrunFrames++;
                    OverrunSeconds += s - _settings.mainBudgetSeconds;
                    if (_overrunUnits.Count < 64) _overrunUnits.Add("step " + (s * 1000).ToString("F3") + " ms, largest unit " + (_stepUnit ?? "(none)") + " " + (_stepUnitSeconds * 1000).ToString("F3") + " ms");
                }
            }
        }

        private void AdvanceCuts()
        {
            for (int i = _cuts.Count - 1; i >= 0; i--)
            {
                PendingCut cut = _cuts[i];
                if (cut.stage == CutStage.Display)
                {
                    if (!MayStart()) return;
                    long displayBegin = Stopwatch.GetTimestamp();
                    PublishOutcome shown = PublishDisplayOnly(cut);
                    Unit("display publication", displayBegin);
                    if (shown == PublishOutcome.WaitingForRoom) { cut.roomWaits++; RoomWaits++; continue; }
                    _cuts.RemoveAt(i);
                    continue;
                }

                if (cut.stage == CutStage.Preparing)
                {
                    if (!MayStart()) return;
                    long prepareBegin = Stopwatch.GetTimestamp();
                    bool ended = Prepare(cut);
                    PrepareSeconds += (Stopwatch.GetTimestamp() - prepareBegin) / (double)Stopwatch.Frequency;
                    Unit("preparation", prepareBegin);
                    if (ended && cut.request == null) _cuts.RemoveAt(i);
                    continue;
                }

                if (cut.stage == CutStage.Cooking)
                {
                    long begin = Stopwatch.GetTimestamp();
                    if (cut.hit.cookSerial < 0) cut.hit.cookSerial = cut.request.Serial;   // given at the cook's reservation, after the submission
                    while (cut.request.Stage == PhysicsCutStage.Checking)
                    {
                        if (_cook.holdHullChecksForTest || !MayStart()) { PrepareSeconds += (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency; return; }
                        long checkBegin = Stopwatch.GetTimestamp();
                        _cook.CheckNextHull(cut.request);
                        HullChecks++;
                        Unit("hull check", checkBegin);
                    }

                    PrepareSeconds += (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
                    if (!cut.request.IsOver) continue;
                }

                if (!MayStart()) return;
                if (cut.request.Products != null)
                {
                    long publishBegin = Stopwatch.GetTimestamp();
                    PublishOutcome outcome = Publish(cut);
                    Unit("publication", publishBegin);
                    if (outcome == PublishOutcome.WaitingForRoom) { cut.stage = CutStage.WaitingForRoom; cut.roomWaits++; RoomWaits++; continue; }
                    _cuts.RemoveAt(i);
                }
                else
                {
                    _cuts.RemoveAt(i);
                    if (cut.request.HullRejection != null)
                    {
                        PhysicsCutHullRejection r = cut.request.HullRejection;
                        _cook.AttributeHullRejection(cut.request, default, cut.group.Members.Count > 0 ? cut.group.Members[0].fragment : default, "hull");
                        cut.hit.failedMesh = r.meshName;
                        HullRefusals++;
                        Fail(cut, "Refused: PhysX refused the " + (r.positive ? "positive" : "negative") + " child hull (mesh '" + r.meshName + "', " + r.vertexCount + " vertices)");
                    }
                    else Fail(cut, "Failed: the cook produced nothing (" + cut.request.Outcome + ", kernel " + cut.request.KernelStatus + " clip " + (CutStatus)cut.request.cut.kernel.cutStatus + "; hull " + cut.group.VertexCount + " vertices " + cut.group.FaceCount + " faces)");
                }
            }
        }

        /// <summary>The hull's B-rep as text (a kernel failure's record: what the kernel was given).</summary>
        private static unsafe string DescribeHull(PhysicsOwnerShape shape)
        {
            if (shape == null || shape.IsFreed) return "(no shape)";
            ConvexBrepBank bank = shape.BankOf(0);
            ConvexBrepRange r = shape.Convex(0);
            var text = new System.Text.StringBuilder();
            text.Append("vertices ").Append(r.vertexCount).Append(':');
            for (int i = 0; i < r.vertexCount; i++) { float3 v = bank.vertices[r.vertexBase + i]; text.Append(' ').Append(v.x.ToString("R")).Append(',').Append(v.y.ToString("R")).Append(',').Append(v.z.ToString("R")); }
            text.Append(" faces ").Append(r.faceCount).Append(':');
            for (int f = 0; f < r.faceCount; f++)
            {
                int from = bank.faceOffsets[r.faceBase + f], to = bank.faceOffsets[r.faceBase + f + 1];
                text.Append(" [");
                for (int k = from; k < to; k++) text.Append(k > from ? "," : "").Append(bank.faceIndices[r.faceIndexBase + k]);
                text.Append(']');
            }

            return text.ToString();
        }

        /// <summary>A cut that ends before the ledger took anything: the group stays as it was (its hull, collider, body, members, generation); the record names the group, the cook's serial and the mesh.</summary>
        private void Fail(PendingCut cut, string why)
        {
            if (cut.request != null && cut.request.Outcome == PhysicsCutOutcomeKind.KernelFailed) why += "; " + DescribeHull(cut.group.Shape);
            cut.group.Shape.ReleaseFromWork();
            cut.classification?.Dispose();
            cut.classification = null;
            cut.request?.Products?.Dispose();
            cut.group.State = HullGroupState.Idle;
            CutsFailed++;
            _failures.Add("group " + cut.group.Id + " generation " + cut.generation + " cook " + (cut.request != null ? cut.request.Serial : -1) + ": " + why);
            Finish(cut.hit, why, false);
            Record("hit " + cut.hit.id + " " + why + " (group " + cut.group.Id + " unchanged)");
        }
    }
}
