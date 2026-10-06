using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>The settings of the building fusion trial (<see cref="BuildingFusion"/>). Off by default.</summary>
    public readonly struct BuildingFusionSettings
    {
        public readonly bool enabled;
        public readonly int perFrame;   // pieces one frame may fuse or move (the switch is Main work)
        public readonly double deadlineSeconds;   // real playing seconds after a building's last cut before its groups are aggregated (0: only by the next Slash)
        public readonly double mainBudgetSeconds;   // the Main time one Step may begin work within (a unit begun runs to its end)

        public BuildingFusionSettings(bool enabled, int perFrame) : this(enabled, perFrame, 2.0, 0.0015)
        {
        }

        public BuildingFusionSettings(bool enabled, int perFrame, double deadlineSeconds) : this(enabled, perFrame, deadlineSeconds, 0.0015)
        {
        }

        public BuildingFusionSettings(bool enabled, int perFrame, double deadlineSeconds, double mainBudgetSeconds)
        {
            this.enabled = enabled;
            this.perFrame = perFrame;
            this.deadlineSeconds = deadlineSeconds;
            this.mainBudgetSeconds = mainBudgetSeconds;
        }

        public bool IsValid => perFrame >= 1 && deadlineSeconds >= 0.0 && !double.IsNaN(deadlineSeconds) && mainBudgetSeconds > 0.0 && !double.IsNaN(mainBudgetSeconds);
    }

    /// <summary>
    /// One fused building group (BuildingFusion, 2026-09-29): one Rigidbody on a group root, and members -- logical
    /// fragments whose owner Root stands under that root, its colliders belonging to the group's body. Kinematic while
    /// it rests; a side made by a cut is dynamic unless a member of it is anchored.
    /// </summary>
    public sealed class FusedGroup
    {
        internal sealed class Member
        {
            public LogicalFragmentId fragment;
            public PhysicsFragmentOwner owner;
            public float mass;
            public float3 centreLocal;   // the centre of mass in the owner Root's frame
            public float3 inertia;   // the principal moments about that centre
            public quaternion inertiaRotation;   // their axes in the Root's frame
            public bool fixedByAnchors;
            public readonly List<MeshCollider> colliders = new List<MeshCollider>(4);
        }

        internal readonly List<Member> members = new List<Member>();
        internal readonly Dictionary<LogicalFragmentId, Member> byFragment = new Dictionary<LogicalFragmentId, Member>();
        internal readonly Dictionary<Collider, LogicalFragmentId> memberOfCollider = new Dictionary<Collider, LogicalFragmentId>();

        internal FusedGroup(int key, GameObject root, Rigidbody body, bool kinematic)
        {
            Key = key;
            Root = root;
            Body = body;
            Kinematic = kinematic;
        }

        /// <summary>The building this group belongs to (the rest's building key: the lineage root).</summary>
        public int Key { get; }

        public GameObject Root { get; internal set; }

        public Rigidbody Body { get; internal set; }

        /// <summary>Whether the group rests (held kinematic): by the fusion at its making, or by the rest later.</summary>
        public bool Kinematic { get; internal set; }

        /// <summary>Whether a cut of this group is in progress (its members' cooks not all collected).</summary>
        public bool Busy { get; internal set; }

        /// <summary>Whether a member of this group is fixed by anchors: the group is ground, never released by the rest.</summary>
        public bool Anchored => AnchoredMembers > 0;

        /// <summary>How many of its members are fixed by anchors, kept as members come and go (no member is read again for it).</summary>
        public int AnchoredMembers { get; internal set; }

        /// <summary>How many crossed members' cuts in progress have this group as a side (its <see cref="Busy"/> is whether any has).</summary>
        internal int CutsOnIt { get; set; }

        /// <summary>Moves with every member added or removed: what a cut's preparation was made for, and whether it still holds.</summary>
        public int Generation { get; internal set; }

        /// <summary>Cuts of this group being prepared off Main (their hits answered Pending): none is fused into, merged, united or cut meanwhile.</summary>
        public int Preparing { get; internal set; }

        /// <summary>The cut this group is a side of (the Slash, the adopted plane's identity, and its world value at the publication): a hit that is the same request while the group is busy is already answered by it.</summary>
        public float4 LastCutPlaneWorld { get; internal set; }
        public long LastCutSlashId { get; internal set; } = long.MinValue;
        public long LastCutPlaneId { get; internal set; }

        public int MemberCount => members.Count;

        /// <summary>The first member's fragment: the one the rest tracks the group's body under.</summary>
        public LogicalFragmentId Representative => members.Count > 0 ? members[0].fragment : default;

        /// <summary>The whole mass, from the members.</summary>
        public double Mass { get; internal set; }

        public bool Contains(LogicalFragmentId fragment) => byFragment.ContainsKey(fragment);

        /// <summary>The member a collider belongs to. False for a collider that is not a member's.</summary>
        public bool TryGetMemberOf(Collider collider, out LogicalFragmentId fragment) => memberOfCollider.TryGetValue(collider, out fragment);

        public IEnumerable<LogicalFragmentId> Fragments
        {
            get { foreach (Member m in members) yield return m.fragment; }
        }
    }

    /// <summary>
    /// The fusion of a building's held pieces into one kinematic group body, and the cut of a group into two compound
    /// bodies (2026-09-29, trial; off by default). A member keeps its logical fragment, its display geometry and its
    /// owner Root -- the display and the hit shapes follow the Root as before -- and gives up its own Rigidbody: its
    /// colliders belong to the group's. A cut of a member is the group's cut: the plane classifies every member's
    /// convexes, the members it crosses get a logical cut each, the two sides become one compound body each (kinematic
    /// where a member of the side is anchored, dynamic otherwise), and the crossed members are finished from their
    /// cooks into child members. Islands on one side move as one (an accepted approximation of this trial). Nothing is
    /// joined: no display mesh is combined and no convex is rebuilt.
    /// </summary>
    public sealed partial class BuildingFusion : IDisposable
    {
        private static readonly Unity.Profiling.ProfilerMarker s_fuseMarker = new Unity.Profiling.ProfilerMarker("Zantetsu.BuildingFusion.Fuse");
        private static readonly Unity.Profiling.ProfilerMarker s_cutMarker = new Unity.Profiling.ProfilerMarker("Zantetsu.BuildingFusion.Cut");
        private static readonly Unity.Profiling.ProfilerMarker s_finalMarker = new Unity.Profiling.ProfilerMarker("Zantetsu.BuildingFusion.Final");

        /// <summary>One crossed member's own cut, from the group's cut to its Final.</summary>
        private sealed class MemberCut
        {
            public FusedGroup.Member member;
            public CutOperationId operation;
            public PhysicsCutClassification classification;
            public PhysicsCutRequest request;
            public AnchorDistributionResult anchors;
            public FusedGroup positiveGroup, negativeGroup;   // where the two children go
            public GameObject shadow;   // the uncut convexes' copy on the negative side, until the Final
            public readonly List<MeshCollider> shadowColliders = new List<MeshCollider>(4);   // the copy's colliders, as made
            public float4 planeLocal;   // the plane in the member's shape frame (the admitted plane)
            public ProvisionalBoxMass.Side positiveSide, negativeSide;   // each side's mass, centre (in the member Root's frame) and inertia until the Final: the Provisional box rule (ProvisionalBoxMass.TryDivide, DESIGN 7.2)
            public int preparation;   // the preparation whose group cut this is
            public GroupCutExclusion exclusion;   // how its copies are kept from the crossed members (shared by the group cut's member cuts)
            public readonly List<(MeshCollider collider, int layer)> ownLayers = new List<(MeshCollider, int)>(4);   // its crossed member's collider objects and the layers they had, when the exclusion is by a layer pair
            public bool hullRefused;   // its Final failed for PhysX's refusal of a produced convex
            public PhysicsOwnerShape inputShape;   // the shape the cook reads: held for work until the Final has read the products (the owner lets its shape go at its retirement, so the hold is kept here)
        }

        private readonly BuildingFusionSettings _settings;
        private readonly LogicalCutLedger _ledger;
        private readonly PhysicsOwnerRegistry _registry;
        private readonly PhysicsCutCook _cook;
        private readonly CutDag _dag;
        private readonly BuildingRest _rest;
        private readonly float _supportEpsilon, _anchorEpsilon;
        private readonly int _vertexLimit;
        private readonly Func<double> _physicsSeconds;
        private readonly List<FusedGroup> _groups = new List<FusedGroup>();
        private readonly Dictionary<int, FusedGroup> _mainByKey = new Dictionary<int, FusedGroup>();
        private readonly Dictionary<Rigidbody, FusedGroup> _groupOfBody = new Dictionary<Rigidbody, FusedGroup>();
        private readonly List<MemberCut> _cuts = new List<MemberCut>();
        private readonly Dictionary<LogicalFragmentId, MemberCut> _cutOfFragment = new Dictionary<LogicalFragmentId, MemberCut>();   // the cut in progress of a crossed member, by its fragment (one at most)
        private readonly Dictionary<FusedGroup.Member, MemberCut> _cutLookup = new Dictionary<FusedGroup.Member, MemberCut>();   // scratch: the cuts a mass or total is asked with, by their member
        private readonly List<LogicalFragmentId> _held = new List<LogicalFragmentId>(), _anchored = new List<LogicalFragmentId>();
        private readonly List<MeshCollider> _colliders = new List<MeshCollider>(8);
        private readonly List<string> _events = new List<string>();
        private bool _disposed;

        public BuildingFusion(
            BuildingFusionSettings settings, LogicalCutLedger ledger, PhysicsOwnerRegistry registry, PhysicsCutCook cook, CutDag dag,
            BuildingRest rest, float supportEpsilon, float anchorEpsilon, int vertexLimit, Func<double> physicsSeconds, SharedWorkDispatcher dispatcher = null)
        {
            if (!settings.IsValid)
            {
                throw new ArgumentException("the building fusion's settings are not valid", nameof(settings));
            }

            _settings = settings;
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _cook = cook ?? throw new ArgumentNullException(nameof(cook));
            _dag = dag;
            _rest = rest ?? throw new ArgumentNullException(nameof(rest));
            _supportEpsilon = supportEpsilon;
            _anchorEpsilon = anchorEpsilon;
            _vertexLimit = vertexLimit;
            _physicsSeconds = physicsSeconds ?? (() => 0.0);
            _dispatcher = dispatcher;
            if (settings.enabled)
            {
                _registry.OwnerRetiring += OnOwnerRetiring;
                ExclusionLayerPairs.Attach(this);   // the process's layer pairs (the first user configures them)
            }
        }

        public BuildingFusionSettings Settings => _settings;

        public bool Enabled => _settings.enabled && !_disposed;

        /// <summary>The cooking profile the member colliders are made with (the world's).</summary>
        public MeshColliderCookingOptions Cooking { get; set; } = PhysicsCutCook.DefaultCooking;

        /// <summary>The separation impulse's strength for a dynamic side of a group's cut, or none.</summary>
        public SeparationImpulseStrength SeparationStrength { get; set; }

        /// <summary>The impulse a dynamic side gets when there is no strength rule (the ask's own values are per side).</summary>
        public float DefaultSeparationImpulse { get; set; }

        // ---- the record ----

        public int FusedPieces { get; private set; }   // pieces that gave up their body, over the life
        public int GroupsMade { get; private set; }
        public int GroupsMerged { get; private set; }
        public int GroupCount => _groups.Count;
        public int GroupCuts { get; private set; }   // group cuts published
        public int GroupCutsRefused { get; private set; }   // group cuts refused as a whole: a busy group, the ledger's room short of the crossed members, or one crossed member's own refusal
        public int MembersRefused { get; private set; }   // crossed members whose own refusal (not live, under a cut, anchors or mass not divisible by the plane) refused the whole cut; nothing was changed for it
        public int FusionsDeferred { get; private set; }   // pieces not fused because their building's resting group was being cut
        public int MergesDeferred { get; private set; }   // held side groups not merged because the building's resting group was being cut
        public int GroupsReleased { get; private set; }   // resting groups returned to dynamic by the rest for a lost support
        public int GroupsHeld { get; private set; }   // dynamic groups held by the rest

        /// <summary>Tests only: makes a crossed member's cook count as having produced nothing, so the failure path runs.</summary>
        internal Func<LogicalFragmentId, bool> failProductsHook;

        /// <summary>Tests only: while set, the Finals of ended cooks are not collected by <see cref="Step"/>; <see cref="CollectFinals"/> collects them when the test says.</summary>
        internal bool holdFinals;

        /// <summary>Tests only: a piece the hook names is not fused this turn (it stays a rest piece with its own body until the hook lets it go).</summary>
        internal Func<LogicalFragmentId, bool> holdFusionHook;

        /// <summary>Tests only: the Final of a crossed member the hook names is not collected while it says so (the others' are).</summary>
        internal Func<LogicalFragmentId, bool> holdFinalOfHook;

        /// <summary>Tests only: collects the ended cooks now, as <see cref="Step"/> does when nothing holds them.</summary>
        internal void CollectFinals()
        {
            AdvanceHullChecks();
            CollectEndedCuts();
            CompleteMassJobs();
        }

        /// <summary>The crossed members whose Final has not been collected.</summary>
        public int CutsInProgress => _cuts.Count;

        /// <summary>Whether every cut in progress has its cook over (its Final is ready to be collected). True with none in progress.</summary>
        public bool AllCooksOver
        {
            get
            {
                foreach (MemberCut cut in _cuts) if (cut.request != null && !cut.request.IsOver) return false;
                return true;
            }
        }
        public int MembersClassified { get; private set; }
        public int MaxMembersInOneCut { get; private set; }
        public int MembersSplit { get; private set; }   // members the plane crossed (a logical cut each)
        public int FinalsPublished { get; private set; }
        public int FinalsFailed { get; private set; }
        public int SidesDynamic { get; private set; }
        public int SidesKinematic { get; private set; }
        public double FuseSeconds { get; private set; }   // the pieces' fusions and the resting groups' merges, Main (disjoint from the other parts)
        public double MaxFuseSeconds { get; private set; }   // one Step's fusions and merges
        public int MaxCutMembers { get; private set; }   // the members of the group whose publication took the longest
        public double FinalSeconds { get; private set; }
        public double MaxFinalSeconds { get; private set; }
        public double MassSeconds { get; private set; }   // the aggregation of a side's mass properties (Main, pure computation)
        public double MaxMassSeconds { get; private set; }
        public int MaxMassMembers { get; private set; }
        public IReadOnlyList<string> Events => _events;

        /// <summary>The groups, for a diagnosis.</summary>
        public IReadOnlyList<FusedGroup> Groups => _groups;

        /// <summary>Whether a fragment is a fused member whose group is being cut (a rest candidate is not).</summary>
        public bool IsCutting(LogicalFragmentId fragment)
        {
            return _registry.TryGet(fragment, out PhysicsFragmentOwner owner) && owner.IsFused && owner.Group.Busy;
        }

        /// <summary>The group a body belongs to, for a diagnosis. False for a body that is not a group's.</summary>
        public bool TryGetGroupOf(Rigidbody body, out FusedGroup group)
        {
            group = null;
            return body != null && _groupOfBody.TryGetValue(body, out group);
        }

        /// <summary>Whether a body is a fused group's whose cut is in progress (for the rest's candidates).</summary>
        public bool IsGroupBusy(Rigidbody body) => body != null && _groupOfBody.TryGetValue(body, out FusedGroup group) && group.Busy;

        /// <summary>The number of Rigidbodies the groups hold (one each), for the cost comparison.</summary>
        public int GroupBodies => _groups.Count;

        // ---- the fusion ----

        /// <summary>
        /// One turn a frame, on Main after the rest's: up to the settings' count of pieces are fused into their
        /// building's resting group (a held piece, or an anchored one, whose Final and geometry commit are done and
        /// which is under no cut), a dynamic side group the rest holds now becomes a resting group and is merged into
        /// the building's, and the crossed members' cooks that ended are finished.
        /// </summary>
        public void Step()
        {
            if (!Enabled)
            {
                return;
            }

            _stepWatch.Restart();
            _budgetStart = Stopwatch.GetTimestamp();
            _inStep = true;
            try
            {
                AdvanceHullChecks();   // the cooks' hull checks of this fusion's cuts go on even while the Finals are held
                if (!holdFinals)
                {
                    CollectEndedCuts();
                }

                using (s_fuseMarker.Auto())
                {
                    AdvancePreparations();
                    AdvanceHeld();
                    BeginByDeadline();
                    AdvanceAggregations();
                    BeginPart();
                    int budget = _settings.perFrame;
                    budget -= MergeHeldSideGroups(budget);
                    if (budget > 0)
                    {
                        FuseHeldPieces(budget);
                    }

                    double fused = EndPart();
                    FuseSeconds += fused;
                    MaxFuseSeconds = Math.Max(MaxFuseSeconds, fused);
                }
            }
            finally
            {
                // The frame's masses, before its physics step: an indivisible end of the turn.
                CompleteMassJobs();
                _inStep = false;
                _stepWatch.Stop();
                double seconds = _stepWatch.Elapsed.TotalSeconds;
                StepSeconds += seconds;
                MaxStepSeconds = Math.Max(MaxStepSeconds, seconds);
                double over = seconds - _settings.mainBudgetSeconds;
                if (over > 0.0)
                {
                    OverrunFrames++;
                    OverrunSeconds += over;
                    MaxOverrunSeconds = Math.Max(MaxOverrunSeconds, over);
                }
            }
        }

        private void FuseHeldPieces(int budget)
        {
            _held.Clear();
            _anchored.Clear();
            _rest.CollectHeldAndAnchored(_held, _anchored);
            int fused = 0;
            for (int pass = 0; pass < 2 && fused < budget; pass++)
            {
                List<LogicalFragmentId> list = pass == 0 ? _held : _anchored;
                for (int i = 0; i < list.Count && fused < budget; i++)
                {
                    if (!MayStart())
                    {
                        return;
                    }

                    if (TryFuse(list[i]))
                    {
                        fused++;
                    }
                }
            }
        }

        /// <summary>Whether a piece may be fused now: a building piece with its own body, Final done, geometry committed, under no cut, without a joint.</summary>
        private bool Eligible(LogicalFragmentId fragment, out PhysicsFragmentOwner owner, out int key)
        {
            key = 0;
            if (!_registry.TryGet(fragment, out owner) || owner.IsFused || owner.Body == null || owner.IsWithdrawn || owner.Root == null
                || !owner.Building.IsBuildingDerived || owner.BuildingWorldConstraint != null || owner.Shape == null || owner.Shape.IsFreed)
            {
                return false;
            }

            if (!_ledger.IsCurrentTarget(fragment) || _ledger.TryGetActiveOperation(fragment, out _))
            {
                return false;
            }

            if (_dag != null && !_dag.TryGetGeometryFrame(fragment, out _))
            {
                return false;   // the geometry commit of this piece is not done
            }

            if (!_rest.TryGetBuildingKey(fragment, out key))
            {
                return false;
            }

            return owner.Root.GetComponentInChildren<Joint>() == null;
        }

        private bool TryFuse(LogicalFragmentId fragment)
        {
            if (holdFusionHook != null && holdFusionHook(fragment))
            {
                return false;
            }

            if (!Eligible(fragment, out PhysicsFragmentOwner owner, out int key))
            {
                return false;
            }

            Rigidbody body = owner.Body;
            if (!body.isKinematic)
            {
                return false;   // held pieces are kinematic; a piece the rest released meanwhile is not fused
            }

            if (!_mainByKey.TryGetValue(key, out FusedGroup group) || !group.Kinematic)
            {
                // No resting group of this building, or the one there was returned to dynamic (a lost support): a new one.
                group = MakeGroup(key, body.position, body.rotation, true);
                _mainByKey[key] = group;
            }
            else if (group.Busy || group.Preparing > 0)
            {
                FusionsDeferred++;   // a group being cut, or whose cut is being prepared, is not changed under it: the piece waits for a later turn
                return false;
            }

            Fuse(owner, fragment, group);
            return true;
        }

        private FusedGroup MakeGroup(int key, Vector3 position, Quaternion rotation, bool kinematic)
        {
            var root = new GameObject("Fused Group " + key + (kinematic ? " (held)" : ""));
            root.transform.SetPositionAndRotation(position, rotation);
            CutPhysicsStep.NotePlacementInputChanged();   // a root put somewhere outside a physics step (D-204)
            var body = root.AddComponent<Rigidbody>();
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.isKinematic = kinematic;
            body.useGravity = true;
            body.mass = 1f;
            var group = new FusedGroup(key, root, body, kinematic);
            _groups.Add(group);
            _groupOfBody[body] = group;
            GroupsMade++;
            BodiesMade++;
            return group;
        }

        /// <summary>
        /// The switch of one piece into a group, in one Main section: the rest's edges move to the group, the Root goes
        /// under the group root at its world pose, the piece's own body is destroyed at once (its colliders then belong
        /// to the group's body), the correspondence forgets the body, and the owner becomes a body-less member.
        /// </summary>
        private void Fuse(PhysicsFragmentOwner owner, LogicalFragmentId fragment, FusedGroup group)
        {
            Rigidbody body = owner.Body;
            var member = new FusedGroup.Member
            {
                fragment = fragment, owner = owner, mass = body.mass, centreLocal = body.centerOfMass,
                inertia = body.inertiaTensor, inertiaRotation = body.inertiaTensorRotation, fixedByAnchors = owner.FixedByAnchors,
            };
            owner.Root.GetComponentsInChildren(true, member.colliders);
            owner.Root.transform.SetParent(group.Root.transform, true);
            _colliders.Clear();
            group.Root.GetComponentsInChildren(true, _colliders);
            // The rest follows the group (held, or anchored once an anchored member is in it) and carries the piece's
            // outside support relations to it, before the piece's body goes.
            _rest.FuseIntoGroup(body, group.Body, group.Key, _colliders, group.Anchored || member.fixedByAnchors);
            _registry.ForgetBody(body);
            UnityEngine.Object.DestroyImmediate(body);
            owner.FuseInto(group, member.mass);
            AddMember(group, member);
            FusedPieces++;
            Record("fuse t " + _physicsSeconds().ToString("F3") + " piece " + fragment.value + " into group " + group.Key + " (" + group.MemberCount + " members, " + member.colliders.Count + " colliders)");
        }

        private static void AddMember(FusedGroup group, FusedGroup.Member member)
        {
            group.members.Add(member);
            group.byFragment[member.fragment] = member;
            foreach (MeshCollider c in member.colliders)
            {
                if (c != null) group.memberOfCollider[c] = member.fragment;
            }

            group.Mass += member.mass;
            if (member.fixedByAnchors) group.AnchoredMembers++;
            group.Generation++;
        }

        /// <summary>One member leaves: the last one in the list without a search (the moves of a merge or a union take the last); the anchored count kept, not read again from the others.</summary>
        private static void RemoveMember(FusedGroup group, FusedGroup.Member member)
        {
            int last = group.members.Count - 1;
            bool removed;
            if (last >= 0 && ReferenceEquals(group.members[last], member))
            {
                group.members.RemoveAt(last);
                removed = true;
            }
            else
            {
                removed = group.members.Remove(member);
            }

            if (!removed)
            {
                return;
            }

            group.byFragment.Remove(member.fragment);
            foreach (MeshCollider c in member.colliders)
            {
                if (c != null) group.memberOfCollider.Remove(c);
            }

            group.Mass -= member.mass;
            if (member.fixedByAnchors) group.AnchoredMembers--;
            group.Generation++;
        }

        /// <summary>Every member leaves at once (a group cut takes them all to its sides): the tables emptied in one go.</summary>
        private static void RemoveAllMembers(FusedGroup group)
        {
            group.members.Clear();
            group.byFragment.Clear();
            group.memberOfCollider.Clear();
            group.Mass = 0.0;
            group.AnchoredMembers = 0;
            group.Generation++;
        }

        /// <summary>A crossed member's cut begins: in the list, in the table by its fragment, and counted on its two sides.</summary>
        private void AddCut(MemberCut cut)
        {
            _cuts.Add(cut);
            _cutOfFragment[cut.member.fragment] = cut;
            if (cut.positiveGroup != null) cut.positiveGroup.CutsOnIt++;
            if (cut.negativeGroup != null && !ReferenceEquals(cut.negativeGroup, cut.positiveGroup)) cut.negativeGroup.CutsOnIt++;
        }

        /// <summary>A crossed member's cut leaves the cuts in progress (its Final collected, or failed).</summary>
        private void RemoveCutAt(int index)
        {
            MemberCut cut = _cuts[index];
            _cuts.RemoveAt(index);
            if (_cutOfFragment.TryGetValue(cut.member.fragment, out MemberCut mapped) && ReferenceEquals(mapped, cut)) _cutOfFragment.Remove(cut.member.fragment);
            if (cut.positiveGroup != null) cut.positiveGroup.CutsOnIt--;
            if (cut.negativeGroup != null && !ReferenceEquals(cut.negativeGroup, cut.positiveGroup)) cut.negativeGroup.CutsOnIt--;
        }

        /// <summary>
        /// A resting group other than its building's resting group (a side the rest held, or a released group held again)
        /// is merged into the building's, some members a frame; when the building has none resting, it becomes it. The
        /// resting state itself is set by the rest's notices (<see cref="OnGroupHeld"/>, <see cref="OnGroupReleasing"/>).
        /// </summary>
        private int MergeHeldSideGroups(int budget)
        {
            int moved = 0;
            for (int g = _groups.Count - 1; g >= 0 && moved < budget; g--)
            {
                FusedGroup group = _groups[g];
                if (!group.Kinematic || group.Busy || group.Preparing > 0 || group.MemberCount == 0 || group.Body == null)
                {
                    continue;   // dynamic, or being cut or prepared
                }

                if (!_mainByKey.TryGetValue(group.Key, out FusedGroup main) || !main.Kinematic)
                {
                    _mainByKey[group.Key] = group;
                    Record("group " + group.Key + " held: now the building's resting group (" + group.MemberCount + " members)");
                    continue;
                }

                if (ReferenceEquals(main, group))
                {
                    continue;
                }

                if (main.Busy || main.Preparing > 0)
                {
                    MergesDeferred++;   // the building's resting group is being cut: not changed under its cut; this one waits as a resting group of its own
                    continue;
                }

                if (!MayStart())
                {
                    return moved;
                }

                moved += Merge(group, main, budget - moved);
            }

            return moved;
        }

        /// <summary>The rest held a group's body (it rested): the group is a resting group from here.</summary>
        public void OnGroupHeld(Rigidbody body)
        {
            if (body == null || !_groupOfBody.TryGetValue(body, out FusedGroup group) || group.Kinematic)
            {
                return;
            }

            group.Kinematic = true;
            GroupsHeld++;
            AskCostReevaluation();
            Record("group held t " + _physicsSeconds().ToString("F3") + " group " + group.Key + " (" + group.MemberCount + " members)");
        }

        /// <summary>
        /// The rest is returning a resting group's body to dynamic (its support was lost): the group's mass, centre and
        /// inertia are applied to its one body first (a resting group's body carried none), and the group is no resting
        /// group from here -- nothing is fused into it, and a new resting group of the building is made when a piece
        /// rests. The members keep no body of their own.
        /// </summary>
        public void OnGroupReleasing(Rigidbody body)
        {
            if (body == null || !_groupOfBody.TryGetValue(body, out FusedGroup group) || !group.Kinematic)
            {
                return;
            }

            var cuts = new List<MemberCut>();
            bool positiveSide = true;
            foreach (MemberCut c in _cuts)
            {
                if (ReferenceEquals(c.positiveGroup, group)) { cuts.Add(c); positiveSide = true; }
                else if (ReferenceEquals(c.negativeGroup, group)) { cuts.Add(c); positiveSide = false; }
            }

            group.Kinematic = false;
            ApplyMass(group, cuts.Count > 0 ? cuts : null, positiveSide);
            GroupsReleased++;
            Record("group released t " + _physicsSeconds().ToString("F3") + " group " + group.Key + " (" + group.MemberCount + " members, " + group.Mass.ToString("F3") + " kg)");
        }

        /// <summary>
        /// Tests only (2026-10-01): when set, the groups' bodies' lives are written here -- each group cut's publication
        /// (the side kept, both sides, the building's resting group after it), each merge step, and every group dropped with
        /// its reason -- with the frame and the physics step. Nothing is written when null.
        /// </summary>
        internal List<string> bodyTraceForTest;

        internal static string DescribeGroupForTest(FusedGroup g) => g == null ? "none"
            : "group#" + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(g) + " (key " + g.Key + ", body " + (g.Body != null ? g.Body.GetInstanceID().ToString() : "none")
              + ", " + (g.Kinematic ? "resting" : "free") + (g.Anchored ? " anchored" : "") + ", members " + g.MemberCount + ")";

        private void TraceForTest(string what)
        {
            bodyTraceForTest?.Add("frame " + Time.frameCount + " step " + (CutPhysicsStep.Clock != null ? CutPhysicsStep.Clock.StepId : -1) + ": " + what);
        }

        private int Merge(FusedGroup from, FusedGroup into, int budget)
        {
            if (bodyTraceForTest != null) TraceForTest("merge step: " + DescribeGroupForTest(from) + " into " + DescribeGroupForTest(into) + ", budget " + budget);
            int moved = 0;
            while (from.members.Count > 0 && moved < budget)
            {
                if (moved > 0 && !MayStart())
                {
                    break;
                }

                FusedGroup.Member member = from.members[from.members.Count - 1];
                RemoveMember(from, member);
                member.owner.Root.transform.SetParent(into.Root.transform, true);
                member.owner.FuseInto(into, member.mass);
                AddMember(into, member);
                moved++;
            }

            if (from.members.Count == 0)
            {
                _colliders.Clear();
                into.Root.GetComponentsInChildren(true, _colliders);
                _rest.FuseIntoGroup(from.Body, into.Body, into.Key, _colliders, into.Anchored);
                if (bodyTraceForTest != null) TraceForTest("merge complete: " + DescribeGroupForTest(from) + " into " + DescribeGroupForTest(into));
                DropGroup(from, "merged into the building's resting group");
                GroupsMerged++;
                Record("group merged into " + into.Key + " (" + into.MemberCount + " members)");
            }

            return moved;
        }

        private void DropGroup(FusedGroup group, string reason)
        {
            if (bodyTraceForTest != null) TraceForTest("drop (" + reason + "): " + DescribeGroupForTest(group));
            _groups.Remove(group);
            if (_mainByKey.TryGetValue(group.Key, out FusedGroup main) && ReferenceEquals(main, group))
            {
                _mainByKey.Remove(group.Key);
            }

            if (group.Body != null)
            {
                _groupOfBody.Remove(group.Body);
                _rest.Untrack(group.Body, "group dropped");   // what stood on it is judged again by the rest
                BodiesDestroyed++;
            }

            PhysicsOwnerBuilder.DestroyObject(group.Root);
            group.Root = null;
            group.Body = null;
        }

        private void OnOwnerRetiring(LogicalFragmentId fragment, PhysicsFragmentOwner owner)
        {
            if (!owner.IsFused || !owner.Group.byFragment.TryGetValue(fragment, out FusedGroup.Member member))
            {
                return;
            }

            // The owner's Release destroys its Root, and with it the member's colliders: only they leave the group.
            FusedGroup group = owner.Group;
            RemoveMember(group, member);
            if (group.MemberCount == 0 && !group.Busy)
            {
                DropGroup(group, "its last member retired");
            }
            else if (!group.Kinematic)
            {
                if (_collecting)
                {
                    // In a collection of Finals: the group is redone once after them, with its cuts' shares (a Final's
                    // or a failure's side is marked already); a schedule here would be replaced by that one.
                    _massDirty.Add(group);
                }
                else
                {
                    MassSchedulesByRetirement++;
                    ApplyMass(group);
                }
            }
        }

        // ---- the group's cut ----

        /// <summary>
        /// A cut of a fused member, asked through the driver: the group is split by the ask's plane. The plane is in the
        /// hit member's shape frame; it is carried to every member's own frame. The members it crosses get a logical
        /// cut each (the DAG's admission registers their geometry work) and a cook; the rest go uncut to their side.
        /// All-or-none: every crossed member's admission preconditions and the interval's mass rule are judged before
        /// the first admission, and one member's refusal refuses the whole cut with nothing changed -- the group, the
        /// ledger and the physics side stand as they were. A group being cut refuses as well.
        /// </summary>
        public ProvisionalCutAcceptance TryRequestGroupCut(PhysicsFragmentOwner owner, in ProvisionalCutAsk ask, out LogicalCutAdmission admission)
        {
            admission = LogicalCutAdmission.NoOp;
            if (!Enabled || owner == null || !owner.IsFused || owner.Group.Root == null)
            {
                return ProvisionalCutAcceptance.InvalidRequest;
            }

            FusedGroup group = owner.Group;
            if (!group.byFragment.TryGetValue(ask.source, out FusedGroup.Member hit))
            {
                return ProvisionalCutAcceptance.InvalidRequest;
            }

            long begin = Stopwatch.GetTimestamp();
            try
            {
                HitRecord record = NewHit(in ask);   // one record per hit, whatever becomes of it
                // The aggregation's gate: a hit on a building that aggregates -- or that a different Slash reaches while the
                // last cut's groups are apart -- is held and cut after the aggregation, as a Pending acceptance.
                if (HoldOrBegin(group, hit, in ask, record))
                {
                    return ProvisionalCutAcceptance.Pending;
                }

                // Prepared, attached to the group's preparation, answered by the cut in progress, or held: on record, each.
                return RouteHit(group, hit, in ask, record, out admission);
            }
            finally
            {
                double seconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
                RequestSeconds += seconds;
                MaxRequestSeconds = Math.Max(MaxRequestSeconds, seconds);
            }
        }

        private readonly struct Classified
        {
            public readonly FusedGroup.Member member;
            public readonly PhysicsCutClassification classification;   // null when uncut
            public readonly float4 planeLocal;
            public readonly bool split, positive;

            public Classified(FusedGroup.Member member, PhysicsCutClassification classification, float4 planeLocal, bool split, bool positive)
            {
                this.member = member; this.classification = classification; this.planeLocal = planeLocal; this.split = split; this.positive = positive;
            }
        }

        private readonly List<Classified> _classified = new List<Classified>();

        private ProvisionalCutAcceptance Refuse(FusedGroup group, LogicalCutAdmission why, string detail, out LogicalCutAdmission admission)
        {
            GroupCutsRefused++;
            _lastRefusal = detail;
            admission = why;
            Record("group cut refused t " + _physicsSeconds().ToString("F3") + " group " + group.Key + ": " + detail);
            return ProvisionalCutAcceptance.NotAccepted;
        }

        /// <summary>One crossed member's own refusal refuses the whole cut (DESIGN 7.7): nothing was admitted or moved for any member.</summary>
        private ProvisionalCutAcceptance RefuseMember(FusedGroup group, FusedGroup.Member member, LogicalCutAdmission why, string detail, out LogicalCutAdmission admission)
        {
            MembersRefused++;
            return Refuse(group, why, "member " + member.fragment.value + " " + detail + " -- the whole cut is refused, nothing changed", out admission);
        }

        private bool TakenByCut(PhysicsCutClassification cls)
        {
            foreach (MemberCut c in _cuts) if (ReferenceEquals(c.classification, cls)) return true;
            return false;
        }

        /// <summary>
        /// The two sides, in one Main section: two roots with a body each (kinematic where a member of the side is
        /// anchored), every member's Root moved under its side at its world pose (a crossed member goes to the positive
        /// side with its uncut colliders, and a copy of them stands on the negative side until its Final), the old group
        /// root gone, the dynamic sides given their mass properties and the separation impulse, the rest told, and the
        /// crossed members' cooks submitted.
        /// </summary>
        private void PublishSides(FusedGroup group, float4 planeWorld, in ProvisionalCutAsk ask, List<MemberCut> memberCuts, bool positiveFixed, bool negativeFixed, Preparation prepared, GroupCutExclusion exclusion)
        {
            int copyLayer = exclusion != null && exclusion.byLayers ? ExclusionLayerPairs.LayersOf(exclusion.pair).b : -1;
            int layerCalls = 0;
            long mark = Stopwatch.GetTimestamp();
            double moveS = 0, shadowS = 0, ignoreS = 0, restS = 0, submitS = 0, recordS = 0;
            double Lap()
            {
                long now = Stopwatch.GetTimestamp();
                double lap = (now - mark) / (double)Stopwatch.Frequency;
                mark = now;
                return lap;
            }

            int membersAtStart = group.MemberCount;
            Vector3 at = group.Root.transform.position;
            Quaternion rotation = group.Root.transform.rotation;
            // The group's motion, read before it goes: the sides inherit it (a resting group has none).
            Vector3 groupVelocity = group.Body != null && !group.Body.isKinematic ? group.Body.linearVelocity : Vector3.zero;
            Vector3 groupAngular = group.Body != null && !group.Body.isKinematic ? group.Body.angularVelocity : Vector3.zero;
            Vector3 groupCentre = group.Body != null ? group.Body.worldCenterOfMass : at;
            if (_classified.Count != group.members.Count)
            {
                throw new InvalidOperationException("a member of the group was not classified for its cut");   // every member was, or the cut was refused
            }

            // Which side keeps the old group's Root and Body (TL, 2026-09-30: every member moved under two new bodies was
            // 20 ms for 1452 members): the one whose keeping moves fewer colliders. Keeping the positive side moves the
            // negative side's members and the copies made ahead under the old Root; keeping the negative side moves the
            // positive side's members (the crossed originals among them) and leaves the copies where they were made. The
            // meaning of the sides is unchanged: the crossed originals are positive, their copies negative.
            int positiveMembers = 0, negativeMembers = 0, positiveColliders = 0, negativeColliders = 0, preparedCopies = 0;
            for (int i = 0; i < _classified.Count; i++)
            {
                Classified c = _classified[i];
                if (c.positive) { positiveMembers++; positiveColliders += c.member.colliders.Count; }
                else { negativeMembers++; negativeColliders += c.member.colliders.Count; }
            }

            if (prepared != null) foreach (MemberCut cut in memberCuts) if (prepared.shadows.TryGetValue(cut.member, out PreparedShadow ps) && ps.root != null) preparedCopies += ps.colliders.Count;
            int keep = keepSideForTest != 0 ? keepSideForTest : negativeColliders + preparedCopies <= positiveColliders ? 1 : -1;
            bool keepPositive = keep == 1, keepNegative = keep == -1;
            FusedGroup positive = keepPositive ? group : MakeGroup(group.Key, at, rotation, positiveFixed);
            FusedGroup negative = keepNegative ? group : MakeGroup(group.Key, at, rotation, negativeFixed);
            if (keepPositive || keepNegative)
            {
                // The kept body is the side from here: held when an anchor of the side fixes it, free otherwise (woken, its
                // motion given at the Step's end as a new side's is); a union counted of the old group is not of this side.
                bool fixedSide = keepPositive ? positiveFixed : negativeFixed;
                group.Kinematic = fixedSide;
                if (group.Body.isKinematic != fixedSide) group.Body.isKinematic = fixedSide;
                if (!fixedSide) group.Body.WakeUp();
                _unionsBegun.Remove(group);
                if (keepPositive) SidesKeptPositive++; else SidesKeptNegative++;
            }

            positive.Root.name = "Fused Group " + group.Key + " +" + (positiveFixed ? " (anchored)" : "");
            negative.Root.name = "Fused Group " + group.Key + " -" + (negativeFixed ? " (anchored)" : "");
            // The sides stay active while the members move under them (2026-09-30: made inactive and activated after,
            // every collider left the physics scene and came back -- 10 ms more for 1452 members). Nothing steps or
            // queries the physics until this section ends, and the masses are applied before the frame's step.

            int membersMoved = 0, collidersMoved = 0;
            if (!keepPositive && !keepNegative)
            {
                for (int i = 0; i < _classified.Count; i++)
                {
                    Classified c = _classified[i];
                    FusedGroup side = c.positive ? positive : negative;
                    c.member.owner.Root.transform.SetParent(side.Root.transform, true);
                    c.member.owner.FuseInto(side, c.member.mass);
                    AddMember(side, c.member);
                    membersMoved++;
                    collidersMoved += c.member.colliders.Count;
                }

                RemoveAllMembers(group);   // all of them went to the sides: the old group's tables emptied at once
            }
            else
            {
                // The kept side's members stay where they stand (their Transforms untouched); the others move under the new
                // side. The kept group's list is rebuilt once in its order, its tables lose only the moved members.
                _keptMembers.Clear();
                for (int i = 0; i < _classified.Count; i++)
                {
                    Classified c = _classified[i];
                    FusedGroup side = c.positive ? positive : negative;
                    if (ReferenceEquals(side, group))
                    {
                        _keptMembers.Add(c.member);
                        continue;
                    }

                    ForgetMember(group, c.member);
                    c.member.owner.Root.transform.SetParent(side.Root.transform, true);
                    c.member.owner.FuseInto(side, c.member.mass);
                    AddMember(side, c.member);
                    membersMoved++;
                    collidersMoved += c.member.colliders.Count;
                }

                group.members.Clear();
                group.members.AddRange(_keptMembers);
                group.Generation++;
                _keptMembers.Clear();
            }

            // A crossed member's mass is shared between the sides until its Final by the Provisional box rule: the
            // positive side carries the positive side's share through the member itself, the negative side the other
            // share through the shadow (the two shares sum to the member's mass).
            foreach (MemberCut cut in memberCuts)
            {
                positive.Mass -= cut.negativeSide.mass;
                negative.Mass += cut.negativeSide.mass;
            }

            moveS = Lap();
            int shadowsMoved = 0;
            foreach (MemberCut cut in memberCuts)
            {
                cut.positiveGroup = positive;
                cut.negativeGroup = negative;
                cut.preparation = prepared != null ? prepared.id : 0;
                if (prepared != null && prepared.shadows.TryGetValue(cut.member, out PreparedShadow made) && made.root != null)
                {
                    // Made ahead under the old group at the member's place: it goes to the negative side -- or stays, when
                    // the negative side keeps the old Root -- and answers from now (on its cut's copy layer first, when the
                    // cut excludes by a layer pair).
                    if (made.root.transform.parent != negative.Root.transform)
                    {
                        made.root.transform.SetParent(negative.Root.transform, true);
                        shadowsMoved++;
                        collidersMoved += made.colliders.Count;
                    }

                    if (copyLayer >= 0) foreach (MeshCollider copy in made.colliders) { copy.gameObject.layer = copyLayer; layerCalls++; }
                    made.root.SetActive(true);
                    cut.shadow = made.root;
                    cut.shadowColliders.AddRange(made.colliders);
                    prepared.shadows.Remove(cut.member);
                }
                else
                {
                    cut.shadow = MakeShadow(cut.member, negative.Root.transform, cut.shadowColliders, copyLayer >= 0);
                    if (copyLayer >= 0) { foreach (MeshCollider copy in cut.shadowColliders) { copy.gameObject.layer = copyLayer; layerCalls++; } cut.shadow.SetActive(true); }
                }

                cut.exclusion = exclusion;
            }

            shadowS = Lap();

            // The dynamic sides' mass properties are summed off Main and applied at the end of this Step, and with them the
            // group's motion carried to each side at its own centre (the first-split inheritance, DESIGN 7.2) and the
            // separation push (once, along the plane's normal): all before this frame's physics step.
            float3 normal = math.normalize(planeWorld.xyz);
            // A prepared cut's sides are summed from its snapshot (read in the budgeted frames before); otherwise from the members now.
            if (!positiveFixed) InheritLater(prepared != null ? ScheduleSideMass(positive, prepared.members, true) : ApplyMassPending(positive, memberCuts, true), groupVelocity, groupAngular, groupCentre, normal * PushImpulse(positive, ask.positiveSeparationImpulse));
            if (!negativeFixed) InheritLater(prepared != null ? ScheduleSideMass(negative, prepared.members, false) : ApplyMassPending(negative, memberCuts, false), groupVelocity, groupAngular, groupCentre, -normal * PushImpulse(negative, ask.negativeSeparationImpulse));
            // A kept body held by an anchor still carried the whole group's mass properties: its side's are given as well
            // (a new held side's body has none to be wrong about).
            if (keepPositive && positiveFixed) { if (prepared != null) ScheduleSideMass(positive, prepared.members, true); else ApplyMassPending(positive, memberCuts, true); }
            if (keepNegative && negativeFixed) { if (prepared != null) ScheduleSideMass(negative, prepared.members, false); else ApplyMassPending(negative, memberCuts, false); }
            Lap();   // the masses' samples: counted in MassSeconds, not here

            // The copies on the negative side and the crossed members' uncut colliders on the positive side are the same
            // solids twice: none of those pairs collide (the Provisional rule for siblings, DESIGN 7.1.1), so that one
            // side's copy does not carry the other side's original. Everything else collides as it stands. Every copy
            // with every crossed member's colliders: two sides waiting for their Finals move and turn apart, and a copy
            // can meet any crossed member later (TL, 2026-09-30: a reduction of these pairs by where they stand at the
            // publication is not adopted; the pairs and their cost are counted apart).
            // By the cut's layer pair (the copies went to its second layer above; the crossed members' collider objects go
            // to its first here, their layers kept to be put back): the same pairs apart, by 2 x the colliders' layer
            // settings instead of one call a pair (TL, 2026-09-30). Otherwise, one call a pair.
            int pairs = 0, copies = 0, owns = 0;
            foreach (MemberCut a in memberCuts) { foreach (MeshCollider copy in a.shadowColliders) if (copy != null) copies++; foreach (MeshCollider own in a.member.colliders) if (own != null) owns++; }
            pairs = copies * owns;
            if (copyLayer >= 0)
            {
                int ownLayer = ExclusionLayerPairs.LayersOf(exclusion.pair).a;
                foreach (MemberCut a in memberCuts)
                {
                    foreach (MeshCollider own in a.member.colliders)
                    {
                        if (own == null) continue;
                        a.ownLayers.Add((own, own.gameObject.layer));
                        own.gameObject.layer = ownLayer;
                        layerCalls++;
                    }
                }

                ExclusionCalls += layerCalls;
                ExcludedByLayers++;
            }
            else
            {
                foreach (MemberCut a in memberCuts)
                {
                    foreach (MeshCollider copy in a.shadowColliders)
                    {
                        if (copy == null) continue;
                        foreach (MemberCut b in memberCuts)
                        {
                            foreach (MeshCollider own in b.member.colliders)
                            {
                                if (own != null) { Physics.IgnoreCollision(copy, own, true); ExclusionCalls++; }
                            }
                        }
                    }
                }

                ExcludedByPairs++;
            }

            if (exclusion != null) exclusion.remaining = memberCuts.Count;
            ignoreS = Lap();

            if (positiveFixed) SidesKinematic++; else SidesDynamic++;
            if (negativeFixed) SidesKinematic++; else SidesDynamic++;

            // The rest: what stood on the old group stands on a side now; the dynamic sides are pieces it judges.
            Collider positiveCollider = FirstCollider(positive), negativeCollider = FirstCollider(negative);
            _rest.SplitGroup(group.Body, positive.Body, positiveFixed, negative.Body, negativeFixed, positiveCollider, negativeCollider);
            TrackSide(positive);
            TrackSide(negative);

            bool wasMain = _mainByKey.TryGetValue(group.Key, out FusedGroup main) && ReferenceEquals(main, group);
            if (!keepPositive && !keepNegative)
            {
                DropGroup(group, "cut, neither side kept its body");
            }

            if (wasMain)
            {
                if (positiveFixed) _mainByKey[group.Key] = positive;
                else if (negativeFixed) _mainByKey[group.Key] = negative;
                else _mainByKey.Remove(group.Key);   // the kept group is a free side now, no building's resting group
            }

            if (bodyTraceForTest != null)
            {
                TraceForTest("group cut published: kept " + (keepPositive ? "positive" : keepNegative ? "negative" : "neither") + "; positive " + DescribeGroupForTest(positive) + ", negative " + DescribeGroupForTest(negative)
                    + "; the building's resting group now " + (_mainByKey.TryGetValue(group.Key, out FusedGroup mainNow) ? DescribeGroupForTest(mainNow) : "none"));
            }

            restS = Lap();

            // The crossed members' cooks, after the switch: each holds its input until its Final has read the products.
            foreach (MemberCut cut in memberCuts)
            {
                cut.inputShape = cut.member.owner.Shape;
                cut.inputShape.AcquireForWork();
                cut.request = _cook.Submit(cut.classification.Input, cut.inputShape.LocalToOwner);
                cut.request.hullOwnerChecks = true;   // its hull check is this Step's, under the common budget
                AddCut(cut);
                MembersSplit++;
            }

            submitS = Lap();

            positive.Busy = memberCuts.Count > 0;
            negative.Busy = memberCuts.Count > 0;
            positive.LastCutPlaneWorld = planeWorld; positive.LastCutSlashId = ask.slashId; positive.LastCutPlaneId = ask.adoptedPlaneId;
            negative.LastCutPlaneWorld = planeWorld; negative.LastCutSlashId = ask.slashId; negative.LastCutPlaneId = ask.adoptedPlaneId;
            GroupCuts++;
            NoteCut(group.Key, ask.slashId);
            var detail = new System.Text.StringBuilder();
            for (int i = 0; i < _classified.Count; i++)
            {
                Classified c = _classified[i];
                detail.Append(i > 0 ? " " : "").Append(c.member.fragment.value).Append(c.split ? ":split" : c.positive ? ":+" : ":-");
            }

            Record("group cut t " + _physicsSeconds().ToString("F3") + " group " + group.Key + ": " + _classified.Count + " members, crossed " + memberCuts.Count + ", positive " + positive.MemberCount + (positiveFixed ? " (anchored)" : " (dynamic)")
                + ", negative " + negative.MemberCount + (negativeFixed ? " (anchored)" : " (dynamic)") + "; plane world " + planeWorld.ToString() + "; [" + detail + "]");
            recordS = Lap();
            MembersMovedAtPublication += membersMoved;
            CollidersMovedAtPublication += collidersMoved;
            CopiesMovedAtPublication += shadowsMoved;
            MaxMembersMoved = Math.Max(MaxMembersMoved, membersMoved);
            PublishMoveSeconds += moveS; PublishShadowSeconds += shadowS; PublishIgnoreSeconds += ignoreS;
            PublishRestSeconds += restS; PublishSubmitSeconds += submitS + recordS;
            IgnoredPairs += pairs;
            MaxIgnoredPairs = Math.Max(MaxIgnoredPairs, pairs);
            MaxIgnoreSeconds = Math.Max(MaxIgnoreSeconds, ignoreS);
            double ownSeconds = moveS + shadowS + ignoreS + restS + submitS + recordS;
            if (ownSeconds > _maxPublishOwn)
            {
                _maxPublishOwn = ownSeconds;
                MaxPublishBreakdown = membersAtStart + " members, crossed " + memberCuts.Count + ", pairs " + pairs + "; kept " + (keepPositive ? "positive" : keepNegative ? "negative" : "neither")
                    + " (members + " + positiveMembers + " / - " + negativeMembers + ", colliders + " + positiveColliders + " / - " + negativeColliders + ", copies made ahead " + preparedCopies + "), moved members " + membersMoved + ", colliders " + collidersMoved + " (copies " + shadowsMoved + "); ms: move " + (moveS * 1000).ToString("F3") + ", shadows " + (shadowS * 1000).ToString("F3")
                    + ", ignore " + (ignoreS * 1000).ToString("F3") + ", rest+drop " + (restS * 1000).ToString("F3") + ", submit " + (submitS * 1000).ToString("F3") + ", record " + (recordS * 1000).ToString("F3");
            }
        }

        private double _maxPublishOwn;
        private readonly List<FusedGroup.Member> _keptMembers = new List<FusedGroup.Member>();

        /// <summary>Tests only (and the comparison): 1 keeps the positive side on the old Root and Body, -1 the negative, 2 neither (both sides new, as before); 0 chooses by the colliders to move.</summary>
        internal int keepSideForTest;

        /// <summary>Publications that kept the old Root and Body for the positive side and for the negative; members, colliders (copies included) and copies moved under a new side in all; the most members one publication moved.</summary>
        public int SidesKeptPositive { get; private set; }
        public int SidesKeptNegative { get; private set; }
        public long MembersMovedAtPublication { get; private set; }
        public long CollidersMovedAtPublication { get; private set; }
        public long CopiesMovedAtPublication { get; private set; }
        public int MaxMembersMoved { get; private set; }

        /// <summary>A member leaves a group's tables only (its list is rebuilt by the caller): the lookup tables, the mass, the anchored count.</summary>
        private static void ForgetMember(FusedGroup group, FusedGroup.Member member)
        {
            group.byFragment.Remove(member.fragment);
            foreach (MeshCollider c in member.colliders)
            {
                if (c != null) group.memberOfCollider.Remove(c);
            }

            group.Mass -= member.mass;
            if (member.fixedByAnchors) group.AnchoredMembers--;
        }

        /// <summary>A group cut's exclusion of its copies from its crossed members: by a lent layer pair (its crossed members' collider objects and the layers they had), or by pairs of colliders; given back when its last member cut has ended.</summary>
        internal sealed class GroupCutExclusion
        {
            public bool byLayers;
            public int pair = -1;
            public int remaining;
        }

        /// <summary>Tests only (and the comparison): every group cut excludes by pairs of colliders, as it did before the layer pairs.</summary>
        internal bool exclusionByPairsForTest;

        /// <summary>The exclusions: API calls made (layer settings, or IgnoreCollision calls), group cuts excluded by a layer pair and by pairs of colliders, those that fell back to pairs (a crossed member's collider not on the base layer), the Steps a scanned preparation waited for a free pair, and the Main seconds of giving them back.</summary>
        public long ExclusionCalls { get; private set; }
        public int ExcludedByLayers { get; private set; }
        public int ExcludedByPairs { get; private set; }
        /// <summary>Group cuts that fell back to the synchronous exclusion by pairs, by reason: a crossed member's collider not on the base layer, or no layer pair in the process at all. Neither is covered by the layer pairs' performance (TL, 2026-09-30); a performance check expects both at 0.</summary>
        public int ExclusionFallbacksNotBaseLayer { get; private set; }
        public int ExclusionFallbacksNoPairs { get; private set; }
        public int ExclusionFallbacks => ExclusionFallbacksNotBaseLayer + ExclusionFallbacksNoPairs;
        public int LayerPairWaits { get; private set; }
        public double ExclusionReleaseSeconds { get; private set; }

        /// <summary>
        /// How a group cut about to be published will exclude its copies: by a free layer pair when every crossed member's
        /// collider stands on the base layer (the copies are made on it), by pairs of colliders when one does not (or the
        /// test asks); null when a pair is wanted and none is free -- the preparation waits, nothing admitted.
        /// </summary>
        private GroupCutExclusion ChooseExclusion(List<MemberCut> memberCuts)
        {
            if (exclusionByPairsForTest)
            {
                return new GroupCutExclusion { byLayers = false };   // the comparison asked for: not a fallback
            }

            if (ExclusionLayerPairs.PairCount == 0)
            {
                ExclusionFallbacksNoPairs++;
                return new GroupCutExclusion { byLayers = false };
            }

            foreach (MemberCut cut in memberCuts)
            {
                foreach (MeshCollider own in cut.member.colliders)
                {
                    if (own != null && own.gameObject.layer != ExclusionLayerPairs.BaseLayer)
                    {
                        ExclusionFallbacksNotBaseLayer++;
                        return new GroupCutExclusion { byLayers = false };
                    }
                }
            }

            int pair = ExclusionLayerPairs.TryLend(this);
            if (pair < 0)
            {
                LayerPairWaits++;
                return null;
            }

            return new GroupCutExclusion { byLayers = true, pair = pair };
        }

        /// <summary>
        /// A member cut has ended (its Final published or failed, or the ending): its crossed member's colliders, if still
        /// answering (a failure that left the member), go back to the layers they had; the last member cut of the group cut
        /// gives the pair back -- after every crossed member's colliders were removed or put back and every copy removed.
        /// </summary>
        private void ReleaseExclusion(MemberCut cut)
        {
            GroupCutExclusion e = cut.exclusion;
            if (e == null)
            {
                return;
            }

            long begin = Stopwatch.GetTimestamp();
            cut.exclusion = null;
            // Its own crossed member's colliders only (each member cut keeps its own): removed with a published or failed
            // source, or put back to the layers they had when they still answer.
            foreach ((MeshCollider collider, int layer) in cut.ownLayers)
            {
                if (collider != null && collider.enabled) collider.gameObject.layer = layer;
            }

            cut.ownLayers.Clear();
            e.remaining--;
            if (e.remaining <= 0 && e.byLayers && e.pair >= 0)
            {
                ExclusionLayerPairs.Return(e.pair, this);
                e.pair = -1;
            }

            ExclusionReleaseSeconds += (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
        }


        /// <summary>The rest follows a side: a dynamic side is judged for rest, an anchored (kinematic) side is ground.</summary>
        private void TrackSide(FusedGroup side)
        {
            if (side.Body == null)
            {
                return;
            }

            _colliders.Clear();
            side.Root.GetComponentsInChildren(true, _colliders);
            _rest.TrackGroup(side.Body, side.Key, _colliders, _physicsSeconds(), side.Kinematic);
        }

        private static Collider FirstCollider(FusedGroup group)
        {
            foreach (FusedGroup.Member m in group.members) { if (m.colliders.Count > 0 && m.colliders[0] != null) return m.colliders[0]; }
            return null;
        }

        /// <summary>A side's motion, applied with its mass at the end of the Step: its group's at its own centre, and the push (an impulse, so a velocity of push / M) on top.</summary>
        private static void InheritLater(PendingMass pending, Vector3 velocity, Vector3 angular, Vector3 centre, float3 push)
        {
            if (pending == null)
            {
                return;
            }

            pending.inherit = true;
            pending.v0 = (float3)velocity;
            pending.w0 = (float3)angular;
            pending.c0 = (float3)centre;
            pending.push = push;
        }

        private PendingMass ApplyMassPending(FusedGroup group, List<MemberCut> cuts, bool positiveSide) => ScheduleMass(group, cuts, positiveSide);

        /// <summary>The separation impulse of a dynamic side (DESIGN 7.2): the strength rule's, or the ask's, or the default; 0 for no push.</summary>
        private float PushImpulse(FusedGroup side, float askImpulse)
        {
            if (side.Body == null || side.Body.isKinematic)
            {
                return 0f;
            }

            float impulse = SeparationStrength != null ? SeparationStrength(side.Mass) : askImpulse > 0f ? askImpulse : DefaultSeparationImpulse;
            return impulse > 0f && side.Mass > 0.0 && math.isfinite(impulse) ? impulse : 0f;
        }

        /// <summary>
        /// Where a crossed member's side stands until its Final, for the display: the positive side is the member's
        /// Root (on the positive side), the negative side its shadow (moving with the negative side). False for a
        /// fragment that is not a crossed member of a cut in progress, or another operation.
        /// </summary>
        public bool TryGetSidePlacement(LogicalFragmentId fragment, CutOperationId operation, float side, out Matrix4x4 rootToWorld)
        {
            rootToWorld = default;
            if (_cutOfFragment.TryGetValue(fragment, out MemberCut cut) && cut.operation.Equals(operation))
            {
                Transform at = side < 0f ? (cut.shadow != null ? cut.shadow.transform : null) : (cut.member.owner.Root != null ? cut.member.owner.Root.transform : null);
                if (at == null)
                {
                    return false;
                }

                rootToWorld = at.localToWorldMatrix;
                return true;
            }

            return false;
        }

        /// <summary>The uncut colliders of a crossed member, copied onto the negative side at their world poses (the Provisional approximation: both sides carry the uncut convexes until the Final).</summary>
        private GameObject MakeShadow(FusedGroup.Member member, Transform negativeRoot, List<MeshCollider> made, bool inactive)
        {
            var shadow = new GameObject("Shadow of " + member.fragment.value);
            if (inactive) shadow.SetActive(false);   // made ahead: nothing of it enters the physics scene until the switch
            shadow.transform.SetPositionAndRotation(member.owner.Root.transform.position, member.owner.Root.transform.rotation);
            CutPhysicsStep.NotePlacementInputChanged();   // a root put somewhere outside a physics step (D-204)
            shadow.transform.SetParent(negativeRoot, true);
            foreach (MeshCollider c in member.colliders)
            {
                if (c == null || c.sharedMesh == null) continue;
                var copy = new GameObject("Convex mesh frame");
                copy.transform.SetParent(shadow.transform, false);
                copy.transform.SetPositionAndRotation(c.transform.position, c.transform.rotation);
                var collider = copy.AddComponent<MeshCollider>();
                collider.cookingOptions = c.cookingOptions;
                collider.convex = true;
                collider.providesContacts = true;
                collider.sharedMesh = c.sharedMesh;
                made.Add(collider);
                CollidersMade++;
            }

            return shadow;
        }

        // ---- the Finals ----

        private readonly HashSet<FusedGroup> _massDirty = new HashSet<FusedGroup>();

        private bool _collecting;

        private void CollectEndedCuts()
        {
            _massDirty.Clear();
            _collecting = true;
            try
            {
                CollectEndedCutsCore();
            }
            finally
            {
                _collecting = false;
            }

            // The sides' masses once, after every Final of this turn (each Final used to redo them: a group of a hundred
            // members finished eight times a frame was summed eight times; and each Final's retirement of its source
            // summed its side once more), summed off Main, applied at the Step's end.
            if (_massDirty.Count > 0)
            {
                foreach (FusedGroup group in _massDirty)
                {
                    if (group.Root != null) RedoGroupMass(group);
                }

                _massDirty.Clear();
            }
        }

        private void CollectEndedCutsCore()
        {
            for (int i = _cuts.Count - 1; i >= 0; i--)
            {
                MemberCut cut = _cuts[i];
                if (cut.request == null || !cut.request.IsOver)
                {
                    continue;
                }

                if (holdFinalOfHook != null && holdFinalOfHook(cut.member.fragment))
                {
                    continue;
                }

                if (!MayStart())
                {
                    break;   // the rest of the Finals on the next frame; the masses of what was finished follow below
                }

                RemoveCutAt(i);
                using (s_finalMarker.Auto())
                {
                    BeginPart();
                    try
                    {
                        if (cut.request.Products != null && failProductsHook != null && failProductsHook(cut.member.fragment))
                        {
                            cut.request.Products.Dispose();   // a test's stand-in for a cook that produced nothing
                            Fail(cut, "the cook's products were refused (test hook)");
                        }
                        else if (cut.request.Products != null)
                        {
                            Finish(cut, cut.request.Products);
                        }
                        else if (cut.request.HullRejection != null)
                        {
                            // PhysX refused produced convexes (the cook's hull check): the member fails the ordinary way,
                            // nothing of its Final is published, and the failure is this operation's, once.
                            PhysicsCutHullRejection hull = cut.request.HullRejection;
                            int refused = cut.request.HullRejections.Count;
                            _cook.AttributeHullRejection(cut.request, cut.operation, cut.member.fragment, "fusion");
                            FinalsHullRefused++;
                            cut.hullRefused = true;
                            Fail(cut, "PhysX refused the " + (hull.positive ? "positive" : "negative") + " convex of input convex " + hull.convex + " (mesh '" + hull.meshName + "', "
                                + hull.vertexCount + " vertices, geometry " + hull.geometry + ")" + (refused > 1 ? " and " + (refused - 1) + " more convex(es) of the cut" : ""));
                        }
                        else
                        {
                            Fail(cut, "the cook produced nothing (" + cut.request.Outcome + ")");
                        }
                    }
                    finally
                    {
                        cut.request = null;
                        double seconds = EndPart();
                        FinalSeconds += seconds;
                        MaxFinalSeconds = Math.Max(MaxFinalSeconds, seconds);
                        ReleaseExclusion(cut);
                        SettleBusy(cut.positiveGroup);
                        SettleBusy(cut.negativeGroup);
                    }
                }
            }
        }

        /// <summary>A side's mass properties with the shares of the cuts still in progress on it (a kinematic side keeps the number only; a dynamic one gets its body's properties redone).</summary>
        private void RedoGroupMass(FusedGroup group)
        {
            var remaining = new List<MemberCut>();
            bool positiveSide = true;
            foreach (MemberCut c in _cuts)
            {
                if (ReferenceEquals(c.positiveGroup, group)) { remaining.Add(c); positiveSide = true; }
                else if (ReferenceEquals(c.negativeGroup, group)) { remaining.Add(c); positiveSide = false; }
            }

            List<MemberCut> cuts = remaining.Count > 0 ? remaining : null;
            Retotal(group, cuts, positiveSide);
            if (!group.Kinematic && group.Body != null) ApplyMass(group, cuts, positiveSide);
        }

        private void SettleBusy(FusedGroup group)
        {
            if (group == null) return;
            bool busy = group.CutsOnIt > 0;
            group.Busy = busy;
            if (!busy && group.MemberCount == 0 && group.Root != null)
            {
                DropGroup(group, "empty once its cuts ended");
            }
        }

        /// <summary>
        /// A crossed member's Final: the two child shapes from the products, two child member roots with their colliders
        /// (made disabled, at the source member's pose), the ledger's publication, the two body-less child owners, the
        /// source member retired (its Root and uncut colliders go), the shadow gone, the sides' masses redone.
        /// </summary>
        private void Finish(MemberCut cut, PhysicsCutProducts products)
        {
            FusedGroup.Member source = cut.member;
            PhysicsFragmentOwner sourceOwner = source.owner;
            if (sourceOwner == null || sourceOwner.IsReleased || sourceOwner.Root == null || sourceOwner.Shape == null || sourceOwner.Shape.IsFreed)
            {
                products.Dispose();
                Fail(cut, "the source member is gone");
                return;
            }

            LogicalCutResultOutcome prepared = _ledger.PreparePublication(cut.operation);
            if (prepared != LogicalCutResultOutcome.Applied)
            {
                products.Dispose();
                Fail(cut, "the ledger refused the publication (" + prepared + ")", prepared == LogicalCutResultOutcome.Stale);
                return;
            }

            if (!PhysicsOwnerBuilder.TryFinalMassFrame(products, source.mass, out quaternion localRotation, out float3 localOffset, out PhysicsOwnerBuildOutcome _)
                || !PhysicsOwnerBuilder.TryFinalSideMassInFrame(products, source.mass, true, localRotation, localOffset, out double positiveMass, out float3 positiveCentre, out float3 positiveInertia, out quaternion positiveInertiaRotation, out _)
                || !PhysicsOwnerBuilder.TryFinalSideMassInFrame(products, source.mass, false, localRotation, localOffset, out double negativeMass, out float3 negativeCentre, out float3 negativeInertia, out quaternion negativeInertiaRotation, out _)
                || !PhysicsOwnerBuilder.FinalShapesArePresent(products, sourceOwner.Shape.Meshes))
            {
                products.Dispose();
                Fail(cut, "the final set could not be established");
                return;
            }

            PhysicsShapeSource productsSource = PhysicsShapeSource.For(products);
            productsSource.Acquire();
            PhysicsOwnerShape positiveShape = null, negativeShape = null;
            GameObject positiveRoot = null, negativeRoot = null;
            var positiveMember = new FusedGroup.Member();
            var negativeMember = new FusedGroup.Member();
            try
            {
                positiveShape = PhysicsOwnerShape.OfSide(sourceOwner.Shape, products, productsSource, true);
                negativeShape = PhysicsOwnerShape.OfSide(sourceOwner.Shape, products, productsSource, false);
                positiveRoot = MakeChildRoot(sourceOwner.Root.transform, positiveShape, localRotation, localOffset, sourceOwner.Root.name + " +", positiveMember.colliders);
                Transform negativeAt = cut.shadow != null ? cut.shadow.transform : sourceOwner.Root.transform;   // the copy moved with the negative side
                negativeRoot = MakeChildRoot(negativeAt, negativeShape, localRotation, localOffset, sourceOwner.Root.name + " -", negativeMember.colliders);
                _registry.Reserve(2);
            }
            catch (Exception)
            {
                PhysicsOwnerBuilder.DestroyObject(positiveRoot);
                PhysicsOwnerBuilder.DestroyObject(negativeRoot);
                positiveShape?.Dispose();
                negativeShape?.Dispose();
                productsSource.Release();
                products.Dispose();
                Fail(cut, "the child members could not be made");
                throw;
            }

            try
            {
                // ---- one Main section from here ----
                positiveRoot.transform.SetParent(cut.positiveGroup.Root.transform, true);
                negativeRoot.transform.SetParent(cut.negativeGroup.Root.transform, true);
                LogicalCutResultOutcome published = _ledger.Publish(cut.operation, out LogicalFragmentId positive, out LogicalFragmentId negative);
                if (published != LogicalCutResultOutcome.Applied)
                {
                    PhysicsOwnerBuilder.DestroyObject(positiveRoot);
                    PhysicsOwnerBuilder.DestroyObject(negativeRoot);
                    positiveShape.Dispose();
                    negativeShape.Dispose();
                    products.Dispose();
                    Fail(cut, "the ledger refused the publication late (" + published + ")", published == LogicalCutResultOutcome.Stale);
                    return;
                }

                BuildingLineage lineage = sourceOwner.Building.ChildOfSplit();
                PhysicsFragmentOwner positiveOwner = PhysicsFragmentOwner.Fused(positiveRoot, positiveShape, cut.anchors.IsPositiveFixed, sourceOwner.GeometryLocalToOwner, lineage, cut.positiveGroup, (float)positiveMass);
                PhysicsFragmentOwner negativeOwner = PhysicsFragmentOwner.Fused(negativeRoot, negativeShape, cut.anchors.IsNegativeFixed, sourceOwner.GeometryLocalToOwner, lineage, cut.negativeGroup, (float)negativeMass);
                positiveMember.fragment = positive; positiveMember.owner = positiveOwner; positiveMember.mass = (float)positiveMass; positiveMember.centreLocal = positiveCentre; positiveMember.inertia = positiveInertia; positiveMember.inertiaRotation = positiveInertiaRotation; positiveMember.fixedByAnchors = cut.anchors.IsPositiveFixed;
                negativeMember.fragment = negative; negativeMember.owner = negativeOwner; negativeMember.mass = (float)negativeMass; negativeMember.centreLocal = negativeCentre; negativeMember.inertia = negativeInertia; negativeMember.inertiaRotation = negativeInertiaRotation; negativeMember.fixedByAnchors = cut.anchors.IsNegativeFixed;
                _registry.Add(positive, positiveOwner);
                _registry.Add(negative, negativeOwner);
                AddMember(cut.positiveGroup, positiveMember);
                AddMember(cut.negativeGroup, negativeMember);
                foreach (MeshCollider c in positiveMember.colliders) c.enabled = true;
                foreach (MeshCollider c in negativeMember.colliders) c.enabled = true;
                productsSource.TakeOwnership();

                // The source member ends: its colliders stop answering now, its Root goes, and it leaves its group (the
                // retiring event). The shadow on the other side goes with it.
                foreach (MeshCollider c in source.colliders) if (c != null) { c.enabled = false; CollidersDestroyed++; }
                _registry.Retire(source.fragment);
                RetireShadow(cut);
                RedoSideMasses(cut);
                FinalsPublished++;
                _memberOperations.Add(new MemberOperation(cut.operation, source.fragment, positive, negative, "Published", _physicsSeconds(), cut.preparation, false));
                Record("final t " + _physicsSeconds().ToString("F3") + " member " + source.fragment.value + " -> " + positive.value + " (" + positiveMember.colliders.Count + " colliders) and " + negative.value + " (" + negativeMember.colliders.Count + ")");
            }
            finally
            {
                productsSource.Release();
                ReleaseInput(cut);
            }
        }

        /// <summary>The sides of a crossed member's Final or failure are marked: their masses are redone once after the turn's Finals (the collection), or at once when called outside a collection.</summary>
        private void RedoSideMasses(MemberCut done)
        {
            if (done.positiveGroup != null) _massDirty.Add(done.positiveGroup);
            if (done.negativeGroup != null) _massDirty.Add(done.negativeGroup);
        }

        private void Retotal(FusedGroup group, List<MemberCut> cuts, bool positiveSide)
        {
            if (group == null) return;
            FillCutLookup(cuts);
            double total = 0.0;
            foreach (FusedGroup.Member m in group.members)
            {
                double mass = m.mass;
                if (cuts != null && _cutLookup.TryGetValue(m, out MemberCut c)) mass = positiveSide ? c.positiveSide.mass : c.negativeSide.mass;
                total += mass;
            }

            if (cuts != null && !positiveSide)
            {
                foreach (MemberCut c in cuts) if (!IsMemberOf(group, c.member) && ReferenceEquals(c.negativeGroup, group)) total += c.negativeSide.mass;
            }

            group.Mass = total;
        }

        /// <summary>The cuts asked with, by their member (the first of a member's, as a scan in order finds it).</summary>
        private void FillCutLookup(List<MemberCut> cuts)
        {
            _cutLookup.Clear();
            if (cuts == null) return;
            foreach (MemberCut c in cuts) if (!_cutLookup.ContainsKey(c.member)) _cutLookup.Add(c.member, c);
        }

        private static bool IsMemberOf(FusedGroup group, FusedGroup.Member member) => group.byFragment.TryGetValue(member.fragment, out FusedGroup.Member m) && ReferenceEquals(m, member);

        /// <summary>A child member's root at the source member's pose, its shape frame in the products' rigid frame, one collider per convex, all disabled.</summary>
        private GameObject MakeChildRoot(Transform at, PhysicsOwnerShape shape, quaternion localRotation, float3 localOffset, string name, List<MeshCollider> colliders)
        {
            var root = new GameObject(name);
            try
            {
                root.transform.SetPositionAndRotation(at.position, at.rotation);
                CutPhysicsStep.NotePlacementInputChanged();   // a root put somewhere outside a physics step (D-204)
                var shapeFrame = new GameObject("Shape Frame");
                shapeFrame.transform.SetParent(root.transform, false);
                shapeFrame.transform.SetLocalPositionAndRotation(localOffset, localRotation);
                for (int i = 0; i < shape.ConvexCount; i++)
                {
                    MeshCollider collider = PhysicsOwnerBuilder.CreateMeshCollider(shapeFrame, shape.MeshFrameOf(i));
                    collider.enabled = false;
                    collider.cookingOptions = Cooking;
                    collider.convex = true;
                    collider.providesContacts = true;   // the rest judges the side's support through the group's colliders
                    collider.sharedMesh = shape.MeshOf(i);
                    colliders.Add(collider);
                    CollidersMade++;
                }

                return root;
            }
            catch
            {
                PhysicsOwnerBuilder.DestroyObject(root);
                throw;
            }
        }

        /// <summary>Finals failed because PhysX refused a convex of the member's cut (the cook's hull check); counted in <see cref="FinalsFailed"/> too.</summary>
        public int FinalsHullRefused { get; private set; }

        /// <summary>The hull checks of this fusion's cuts, taken in its Step under the common budget: Main seconds (a part of <see cref="StepSeconds"/>), the longest unit, and the units taken (a mesh read, or a cut's check ending in its products or its failure).</summary>
        public double HullSeconds { get; private set; }
        public double MaxHullSeconds { get; private set; }
        public int HullChecksInStep { get; private set; }

        /// <summary>Cuts of this fusion whose hull check is still going (their Finals wait for it).</summary>
        public int CutsChecking { get { int n = 0; foreach (MemberCut c in _cuts) if (c.request != null && c.request.Stage == PhysicsCutStage.Checking) n++; return n; } }

        /// <summary>
        /// The hull checks of this fusion's cuts, a mesh a unit under the common budget, in the order the cuts were
        /// admitted; a check left over goes on in the next Step. Nothing of a cut is collected before its check ends.
        /// </summary>
        private void AdvanceHullChecks()
        {
            for (int i = _cuts.Count - 1; i >= 0; i--)
            {
                MemberCut cut = _cuts[i];
                while (cut.request != null && cut.request.Stage == PhysicsCutStage.Checking)
                {
                    if (_cook.holdHullChecksForTest)
                    {
                        return;
                    }

                    if (!MayStart())
                    {
                        return;
                    }

                    BeginPart();
                    _cook.CheckNextHull(cut.request);
                    double seconds = EndPart();
                    HullSeconds += seconds;
                    MaxHullSeconds = Math.Max(MaxHullSeconds, seconds);
                    HullChecksInStep++;
                }
            }
        }

        private void Fail(MemberCut cut, string why, bool stale = false)
        {
            FinalsFailed++;
            _memberOperations.Add(new MemberOperation(cut.operation, cut.member.fragment, default, default, "Failed: " + why, _physicsSeconds(), cut.preparation, cut.hullRefused));
            Record("final failed member " + cut.member.fragment.value + ": " + why);
            if (!stale)
            {
                // As the ordinary path's abort: the operation ends and the source is retired (its Root and colliders go).
                _ledger.Abort(cut.operation);
                if (_registry.TryGet(cut.member.fragment, out PhysicsFragmentOwner owner) && ReferenceEquals(owner, cut.member.owner))
                {
                    foreach (MeshCollider c in cut.member.colliders) if (c != null) { c.enabled = false; CollidersDestroyed++; }
                    _registry.Retire(cut.member.fragment);
                }
            }

            RetireShadow(cut);
            ReleaseInput(cut);
            RedoSideMasses(cut);
        }

        /// <summary>
        /// The shadow ends in this call: its colliders stop answering now (a destroyed object goes at the frame's end, as
        /// every one does, and until then nothing may collide with a copy that is no longer a side), then the object goes.
        /// </summary>
        private void RetireShadow(MemberCut cut)
        {
            if (cut.shadow == null)
            {
                return;
            }

            foreach (MeshCollider c in cut.shadow.GetComponentsInChildren<MeshCollider>(true)) { c.enabled = false; CollidersDestroyed++; }
            PhysicsOwnerBuilder.DestroyObject(cut.shadow);
            cut.shadow = null;
        }

        private static void ReleaseInput(MemberCut cut)
        {
            cut.classification?.Dispose();
            cut.classification = null;
            if (cut.inputShape != null)
            {
                // The shape's own disposal (at the source's retirement) waits for this hold: a hold never let go would keep
                // the shape's meshes -- and, for a child shape, its holds on its parent's products -- for ever.
                cut.inputShape.ReleaseFromWork();
                cut.inputShape = null;
            }
        }

        // ---- mass properties ----

        /// <summary>
        /// A dynamic group's mass, centre and inertia from its members, about the group root's frame: the members'
        /// principal inertias turned into the world, the parallel-axis terms about the common centre, then diagonalized
        /// in the root's frame. Pure computation (no Unity object is made); its time is recorded.
        /// </summary>
        private void ApplyMass(FusedGroup group) => ApplyMass(group, null, true);

        /// <summary>
        /// What a member contributes to a side: its own mass properties in its Root's frame, or -- for a crossed member
        /// until its Final -- the side's part by the Provisional box rule, in the same frame, placed by the member's Root
        /// (positive side) or by its shadow (negative side), which stands where the Root stood at the cut and moves with
        /// that side.
        /// </summary>
        private bool Contribution(FusedGroup.Member m, bool useCuts, bool positiveSide,
            out double mass, out float3 centreLocal, out float3 inertia, out quaternion inertiaRotation, out Transform at)
        {
            mass = m.mass; centreLocal = m.centreLocal; inertia = m.inertia; inertiaRotation = m.inertiaRotation;
            at = m.owner.Root != null ? m.owner.Root.transform : null;
            if (!useCuts || !_cutLookup.TryGetValue(m, out MemberCut cut)) return at != null;
            ProvisionalBoxMass.Side side = positiveSide ? cut.positiveSide : cut.negativeSide;
            mass = side.mass; centreLocal = side.centerOfMass; inertia = side.inertia; inertiaRotation = side.inertiaRotation;
            if (!positiveSide) at = cut.shadow != null ? cut.shadow.transform : at;
            return at != null;
        }

        /// <summary>A plane (n, d: n.x + d = 0) carried from frame A to frame B by the rigid map A -> B.</summary>
        public static float4 TransformPlane(float4x4 aToB, float4 plane)
        {
            float3 n = plane.xyz;
            float3 point = -plane.w * n / math.dot(n, n);
            float3 nB = math.normalize(math.mul(math.transpose(math.inverse(aToB)), new float4(n, 0f)).xyz);
            float3 pointB = math.mul(aToB, new float4(point, 1f)).xyz;
            return new float4(nB, -math.dot(nB, pointB));
        }

        private void Record(string line)
        {
            if (_events.Count < 4096)
            {
                _events.Add(line);
            }
        }

        /// <summary>Ends the trial: the group roots go (the members' Roots with them, which the owners' own release tolerates), and the held inputs of unfinished cuts are given back.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_settings.enabled)
            {
                _registry.OwnerRetiring -= OnOwnerRetiring;
            }

            foreach (MemberCut cut in _cuts)
            {
                if (cut.request != null && cut.request.IsOver)
                {
                    cut.request.Products?.Dispose();
                    cut.request = null;
                }

                if (cut.request == null)
                {
                    ReleaseInput(cut);
                }

                RetireShadow(cut);
                ReleaseExclusion(cut);
            }

            _cuts.Clear();
            _cutOfFragment.Clear();
            if (_settings.enabled)
            {
                ExclusionLayerPairs.Detach(this);   // whatever is still lent to this fusion goes back; the last user puts the matrix back
            }

            foreach (FusedGroup group in _groups)
            {
                PhysicsOwnerBuilder.DestroyObject(group.Root);
                group.Root = null;
                group.Body = null;
            }

            _groups.Clear();
            _mainByKey.Clear();
            _buildings.Clear();
            AbandonPreparations();
            DisposePendingMass();
        }
    }
}
