using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Collections;
using Unity.Profiling;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>How the trial rests a supported building piece: an explicit <c>Rigidbody.Sleep()</c>, or a temporary kinematic hold.</summary>
    public enum BuildingRestMode
    {
        Sleep = 0,
        Kinematic = 1,
    }

    /// <summary>The settings of the building rest trial (<see cref="BuildingRest"/>). Off by default.</summary>
    public readonly struct BuildingRestSettings
    {
        public readonly bool enabled;
        public readonly BuildingRestMode mode;
        public readonly double timeoutSeconds;   // physics seconds after a piece's publication (or its last release) before it may rest
        public readonly int supportSteps;   // consecutive steps its chain to the ground must be confirmed
        public readonly float supportNormalCos;   // |contact normal . up| at least this for a contact to count as support
        public readonly int eventRecords;   // how many event lines are kept

        public BuildingRestSettings(bool enabled, BuildingRestMode mode, double timeoutSeconds, int supportSteps, float supportNormalCos, int eventRecords)
        {
            this.enabled = enabled;
            this.mode = mode;
            this.timeoutSeconds = timeoutSeconds;
            this.supportSteps = supportSteps;
            this.supportNormalCos = supportNormalCos;
            this.eventRecords = eventRecords;
        }

        public BuildingRestSettings(bool enabled, double timeoutSeconds, int supportSteps, float supportNormalCos, int eventRecords)
            : this(enabled, BuildingRestMode.Sleep, timeoutSeconds, supportSteps, supportNormalCos, eventRecords)
        {
        }

        /// <summary>The trial's values: 1 s, 3 steps, |n.y| >= 0.5, 256 event lines.</summary>
        public static BuildingRestSettings Trial(bool enabled) => new BuildingRestSettings(enabled, BuildingRestMode.Sleep, 1.0, 3, 0.5f, 256);

        public static BuildingRestSettings Trial(bool enabled, BuildingRestMode mode) => new BuildingRestSettings(enabled, mode, 1.0, 3, 0.5f, 256);

        public bool IsValid => timeoutSeconds >= 0.0 && supportSteps >= 1 && supportNormalCos > 0f && supportNormalCos <= 1f && eventRecords >= 0;
    }

    /// <summary>
    /// The trial rest of building pieces (2026-09-29): a dynamic piece of a building's lineage that has been published for
    /// the timeout and stands, through a chain of from-below contacts, on the ground (a static collider, a kinematic body
    /// that is not a piece, or an anchored piece) is put to rest -- an explicit sleep (<see cref="BuildingRestMode.Sleep"/>)
    /// or a temporary kinematic hold (<see cref="BuildingRestMode.Kinematic"/>).
    /// <para>
    /// Kinematic mode (the unit of 2026-09-29, local release): a re-cut's physical publication releases only the pieces
    /// the cut piece supported, found upward along the support relations kept before the piece is forgotten. Of them, a
    /// held piece whose support is still confirmed without the cut piece returns to dynamic from rest and is put to
    /// sleep once; a held piece without confirmed support returns to dynamic and is woken; a dynamic piece keeps its
    /// velocity and its clock; held pieces outside the target stay kinematic; anchored pieces stay fixed. Support is
    /// watched for held and sleeping pieces alike by the identity of what grounds them (their contacts are no longer
    /// reported), and a piece whose chain breaks is woken. A piece's clock starts at its creation or at its own return
    /// to dynamic, never at another piece's publication; a piece rests again by its own clock and support, from the
    /// confirmed bottom up, so that a young upper piece does not keep a stable lower one from resting.
    /// </para>
    /// <para>
    /// Mass, inertia, joints, friction, CCD and the sleep threshold are untouched; nothing is retired. In Sleep mode a
    /// piece put to sleep is not put to sleep again after the engine wakes it. Nothing is stepped inside the simulation:
    /// contacts are noted there and judged afterwards.
    /// </para>
    /// </summary>
    public sealed class BuildingRest : IDisposable
    {
        private static readonly ProfilerMarker s_contactsMarker = new ProfilerMarker("Zantetsu.BuildingRest.Contacts");
        private static readonly ProfilerMarker s_supportMarker = new ProfilerMarker("Zantetsu.BuildingRest.Support");
        private static readonly ProfilerMarker s_sleepMarker = new ProfilerMarker("Zantetsu.BuildingRest.Sleep");
        private static readonly ProfilerMarker s_releaseMarker = new ProfilerMarker("Zantetsu.BuildingRest.Release");

        private const int StaleEdgeSteps = 2;   // a reported support not reported for this many steps is gone
        private const float MovedMetres = 0.01f;   // a held ground supporter found this far from where it was noted has moved
        private const float MovedDegrees = 0.5f;

        /// <summary>What grounds a piece: a static collider, a kinematic body that is not a piece, or an anchored piece.</summary>
        private enum GroundKind { None = 0, Static = 1, Kinematic = 2, Anchored = 3 }

        /// <summary>One ground supporter a piece stands on, kept by identity so that it can be checked after the contact notifications stop.</summary>
        private sealed class Ground
        {
            public GroundKind kind;
            public Collider collider;   // the supporting collider (static, or the kinematic body's / anchored piece's)
            public Rigidbody body;   // the kinematic body or the anchored piece's body; null for a static collider
            public int trackedId;   // the anchored piece's tracked id
            public Vector3 position;   // where the supporter was when last noted
            public Quaternion rotation;
            public int lastStep;   // the last step a from-below contact with it was noted
        }

        /// <summary>One tracked body: a dynamic building piece, or an anchored (kinematic) one that only grounds others.</summary>
        private sealed class Tracked
        {
            public Rigidbody body;
            public int id;
            public LogicalFragmentId fragment;
            public int buildingKey;
            public bool anchored;
            public double publishedSeconds;   // the clock: publication, or the piece's own return to dynamic
            public float centreY;
            public int supportedSteps;
            public bool grounded;
            public bool sleptByUs, autoWoken, wokenByUs;
            public bool rested;   // temporarily kinematic by this trial (never an anchored piece)
            public bool isGroup;   // a fused group's body (BuildingFusion): no fragment of its own; its cut state is asked by body
            public double sleptAt = -1.0;
            public Vector3 trackedAt;   // the centre when tracking (or the last release) started: how far it moved before resting
            public int lastChangeStep = int.MinValue;   // a cut, exchange or support change touching it
            public readonly Dictionary<int, int> supporters = new Dictionary<int, int>();   // dynamic supporter body id -> last step noted
            public readonly Dictionary<int, Ground> grounds = new Dictionary<int, Ground>();   // ground supporters by identity
            public readonly HashSet<int> supported = new HashSet<int>();   // bodies this one supports (mirror)
            public readonly HashSet<int> touched = new HashSet<int>();   // partners noted this step (attribution)
            public readonly List<Collider> colliders = new List<Collider>();

            public bool AsleepByUs => sleptByUs && !autoWoken && !wokenByUs;
        }

        private struct Note
        {
            public int body, other;   // tracked body id; the other side's key: a tracked piece's or an untracked dynamic body's id, or a ground supporter's identity
            public bool otherTracked, fromBelow, exit, externalDynamic;
            public GroundKind ground;   // None when the other side is a dynamic body
            public Collider groundCollider;   // the ground supporter's collider (a from-below note only)
            public Rigidbody groundBody;   // the kinematic or anchored ground supporter's body, if any
        }

        private readonly BuildingRestSettings _settings;
        private readonly Dictionary<int, Tracked> _tracked = new Dictionary<int, Tracked>();
        private readonly Dictionary<LogicalFragmentId, Tracked> _byFragment = new Dictionary<LogicalFragmentId, Tracked>();
        private readonly List<Note> _notes = new List<Note>(256);
        private readonly List<string> _events = new List<string>();
        private readonly List<Tracked> _scratch = new List<Tracked>();
        private readonly List<Tracked> _targets = new List<Tracked>();
        private readonly List<int> _stale = new List<int>();
        private readonly Queue<Tracked> _queue = new Queue<Tracked>();
        private readonly HashSet<int> _visited = new HashSet<int>();
        private readonly HashSet<int> _stable = new HashSet<int>();
        private readonly Stopwatch _watch = new Stopwatch();
        private readonly Stopwatch _switchWatch = new Stopwatch();
        private readonly Dictionary<int, int> _reCutOperations = new Dictionary<int, int>();   // operation -> the building key of its source
        private readonly List<LogicalFragmentId> _lastReleased = new List<LogicalFragmentId>();
        private int _step;
        private double _now;
        private bool _subscribed, _disposed;

        public BuildingRest(BuildingRestSettings settings)
        {
            if (!settings.IsValid)
            {
                throw new ArgumentException("the building rest's settings are not valid", nameof(settings));
            }

            _settings = settings;
            if (settings.enabled)
            {
                Physics.ContactEvent += OnContacts;
                _subscribed = true;
            }
        }

        public BuildingRestSettings Settings => _settings;

        public bool Enabled => _settings.enabled && !_disposed;

        private bool Kinematic => _settings.mode == BuildingRestMode.Kinematic;

        // ---- the record ----

        /// <summary>Bodies this trial put to sleep, over its life (Sleep mode's rests, and Kinematic mode's sleeps on release).</summary>
        public int SleepAttempted { get; private set; }

        /// <summary>Of those, how many the engine reported asleep right after the call.</summary>
        public int SleepSucceeded { get; private set; }

        /// <summary>Tracked pieces asleep by this trial right now.</summary>
        public int AsleepNow { get; private set; }

        /// <summary>Pieces the engine woke by itself after this trial's sleep (not put to sleep again).</summary>
        public int AutoWakes { get; private set; }

        /// <summary>Pieces woken by this trial: Sleep mode's group wakes; Kinematic mode's wakes at a re-cut and at a lost support.</summary>
        public int ExplicitWakes { get; private set; }

        /// <summary>Tracked dynamic pieces past the timeout whose support is not confirmed, right now.</summary>
        public int UnsupportedPastTimeout { get; private set; }

        /// <summary>Pieces held or asleep by this trial with no confirmed chain to the ground right now (should stay 0).</summary>
        public int AsleepWithoutSupport { get; private set; }

        /// <summary>Tracked dynamic pieces right now, and the anchored ones.</summary>
        public int TrackedDynamic { get; private set; }

        public int TrackedAnchored { get; private set; }

        /// <summary>Contact pair headers looked at, and the notes taken from them, over the life.</summary>
        public long ContactHeaders { get; private set; }

        public long ContactNotes { get; private set; }

        /// <summary>Contact points of the noted (non-exit) pairs, over the life.</summary>
        public long ContactPoints { get; private set; }

        /// <summary>Collider pairs looked at (each once, whichever sides are tracked), over the life: a pair may give two notes.</summary>
        public long ContactPairs { get; private set; }

        /// <summary>
        /// Diagnosis only: the contact handler takes its notes as usual, and each turn only drops them -- no support
        /// judgement, no rest, no release. Measures the note-taking on its own.
        /// </summary>
        public bool NotesOnly { get; set; }

        /// <summary>Time spent, in seconds: taking the notes (inside the simulation), judging support, resting (and Sleep mode's waking), releasing at re-cuts.</summary>
        public double ContactSeconds { get; private set; }

        public double SupportSeconds { get; private set; }

        public double SleepSeconds { get; private set; }

        public double ReleaseSeconds { get; private set; }

        /// <summary>The longest one support judgement and one rest turn took, in seconds.</summary>
        public double MaxSupportSeconds { get; private set; }

        public double MaxSleepSeconds { get; private set; }

        public int Steps { get; private set; }

        /// <summary>A cut in progress on a fragment (the root gives the ledger's answer): such a piece is not a rest candidate.</summary>
        public Func<LogicalFragmentId, bool> CutInProgress { get; set; }

        /// <summary>A cut in progress on a fused group's body (the fusion answers): such a group is not a rest candidate.</summary>
        public Func<Rigidbody, bool> GroupBusy { get; set; }

        /// <summary>A fused group's body is about to return to dynamic (its support was lost): the fusion applies the group's mass properties and its own state first.</summary>
        public Action<Rigidbody> GroupReleasing { get; set; }

        /// <summary>A fused group's body was just held kinematic by this trial (it rested): the fusion's state follows.</summary>
        public Action<Rigidbody> GroupHeld { get; set; }

        /// <summary>Fused groups this trial returned to dynamic for a lost support.</summary>
        public int GroupsReleased { get; private set; }

        // ---- the kinematic mode's record ----

        /// <summary>Pieces switched to temporary kinematic, over the life; and how many are held so right now.</summary>
        public int KinematicRests { get; private set; }

        public int RestedNow { get; private set; }

        /// <summary>Re-cuts applied (one per operation), the pieces found above their sources (candidates) in all, and the most in one.</summary>
        public int ReCuts { get; private set; }

        public int ReCutCandidates { get; private set; }

        public int MaxReCutCandidates { get; private set; }

        /// <summary>Held pieces returned to dynamic by re-cuts; of them, those put to sleep (support still confirmed) and those woken.</summary>
        public int ReleasedPieces { get; private set; }

        public int SleptOnRelease { get; private set; }

        public int WokenOnRelease { get; private set; }

        /// <summary>Held or sleeping pieces woken because their chain to the ground broke (a supporter retired, moved, was disabled or went).</summary>
        public int WokenBySupportLoss { get; private set; }

        /// <summary>The longest one re-cut's release took on Main, in seconds, and that re-cut's candidate count.</summary>
        public double MaxReleaseSeconds { get; private set; }

        public int MaxReleasePieces { get; private set; }

        /// <summary>Time spent switching isKinematic (rest and release), in seconds.</summary>
        public double SwitchSeconds { get; private set; }

        /// <summary>How far pieces moved from tracking (or their last release) to their rest: the mean and the largest, metres.</summary>
        public float MeanMovedUntilRest { get; private set; }

        public float MaxMovedUntilRest { get; private set; }

        /// <summary>Ground supporters found gone (disabled, destroyed, moved, no longer kinematic, retired) under held or sleeping pieces, over the life.</summary>
        public int LostGrounds { get; private set; }

        /// <summary>The fragments the last re-cut returned from held to dynamic.</summary>
        public IReadOnlyList<LogicalFragmentId> LastReleased => _lastReleased;

        private float _movedSum; private int _movedCount;

        /// <summary>The sleep and wake events, newest last, up to the settings' count.</summary>
        public IReadOnlyList<string> Events => _events;

        // ---- tracking ----

        /// <summary>
        /// Starts following one body. A dynamic building piece is judged for rest from now on (its clock starts at
        /// <paramref name="publishedSeconds"/>); an anchored (kinematic) one is followed only as ground and as the key
        /// its children inherit. The colliders are switched to provide contacts. The same actor again (a handoff registers
        /// the child the pair's actor already is) keeps its support relations: the body is the same, its colliders change.
        /// </summary>
        public void Track(Rigidbody body, LogicalFragmentId fragment, int buildingKey, IReadOnlyList<Collider> colliders, double publishedSeconds, bool anchored)
        {
            if (!Enabled || body == null)
            {
                return;
            }

            int id = body.GetInstanceID();
            if (_tracked.TryGetValue(id, out Tracked already))
            {
                if (already.fragment.IsSet)
                {
                    _byFragment.Remove(already.fragment);
                }

                already.fragment = fragment;
                if (fragment.IsSet)
                {
                    _byFragment[fragment] = already;
                }

                already.lastChangeStep = _step;
                SetColliders(already, colliders);
                return;
            }

            var tracked = new Tracked
            {
                body = body, id = id, fragment = fragment, buildingKey = buildingKey, anchored = anchored,
                publishedSeconds = publishedSeconds, centreY = body.worldCenterOfMass.y, lastChangeStep = _step, trackedAt = body.worldCenterOfMass,
            };
            _tracked[id] = tracked;
            if (fragment.IsSet)
            {
                _byFragment[fragment] = tracked;
            }

            SetColliders(tracked, colliders);
            if (anchored)
            {
                TrackedAnchored++;
            }
            else
            {
                TrackedDynamic++;
            }
        }

        /// <summary>
        /// Starts following a fused group's body (BuildingFusion): judged for rest like a piece, but under no fragment
        /// -- a member's retirement never untracks it -- and with its cut state asked through <see cref="GroupBusy"/>.
        /// An anchored group (a member of it is fixed by anchors) is followed only as ground, like an anchored piece.
        /// </summary>
        public void TrackGroup(Rigidbody body, int buildingKey, IReadOnlyList<Collider> colliders, double publishedSeconds, bool anchored = false)
        {
            if (!Enabled || body == null || _tracked.ContainsKey(body.GetInstanceID()))
            {
                return;
            }

            NewGroup(body, buildingKey, colliders, publishedSeconds, anchored, false);
        }

        private Tracked NewGroup(Rigidbody body, int buildingKey, IReadOnlyList<Collider> colliders, double publishedSeconds, bool anchored, bool held)
        {
            var tracked = new Tracked
            {
                body = body, id = body.GetInstanceID(), buildingKey = buildingKey, isGroup = true, anchored = anchored, rested = held && !anchored,
                publishedSeconds = publishedSeconds, centreY = body.worldCenterOfMass.y, lastChangeStep = _step, trackedAt = body.worldCenterOfMass, sleptAt = publishedSeconds,
            };
            _tracked[tracked.id] = tracked;
            SetColliders(tracked, colliders);
            if (anchored)
            {
                TrackedAnchored++;
            }
            else
            {
                TrackedDynamic++;
                if (tracked.rested) RestedNow++;
            }

            return tracked;
        }

        /// <summary>A tracked group's colliders read again from under its root (members moved in or out). A body not followed changes nothing.</summary>
        public void TrackGroupColliders(Rigidbody body, GameObject root)
        {
            if (!Enabled || body == null || root == null || !_tracked.TryGetValue(body.GetInstanceID(), out Tracked tracked))
            {
                return;
            }

            _colliderScratch.Clear();
            root.GetComponentsInChildren(true, _colliderScratch);
            SetColliders(tracked, _colliderScratch);
        }

        private readonly List<Collider> _colliderScratch = new List<Collider>(64);

        /// <summary>
        /// A tracked group's colliders are about to be exchanged for one (a hull fusion): every ground edge that noted one
        /// of the group's colliders is re-pointed at the collider that stays, so that the exchange alone is never read as
        /// a support lost. The group's own list is set to that one collider. A body not followed changes nothing.
        /// </summary>
        public void RepointGroundColliders(Rigidbody body, Collider now)
        {
            if (!Enabled || body == null || now == null || !_tracked.TryGetValue(body.GetInstanceID(), out Tracked group))
            {
                return;
            }

            int id = group.id;
            foreach (Tracked t in _tracked.Values)
            {
                if (ReferenceEquals(t, group) || !t.grounds.TryGetValue(id, out Ground ground))
                {
                    continue;
                }

                if (ground.collider != now)
                {
                    ground.collider = now;
                    PoseOf(ground, out ground.position, out ground.rotation);
                }
            }

            _colliderScratch.Clear();
            _colliderScratch.Add(now);
            SetColliders(group, _colliderScratch);
        }

        /// <summary>The building key a tracked fragment carries, so that a child can inherit it. False when unknown.</summary>
        public bool TryGetBuildingKey(LogicalFragmentId fragment, out int key)
        {
            if (_byFragment.TryGetValue(fragment, out Tracked tracked))
            {
                key = tracked.buildingKey;
                return true;
            }

            key = 0;
            return false;
        }

        /// <summary>The fragments this trial holds kinematic right now, and the anchored ones it follows as ground (for the fusion's candidates).</summary>
        public void CollectHeldAndAnchored(List<LogicalFragmentId> held, List<LogicalFragmentId> anchored)
        {
            foreach (Tracked t in _tracked.Values)
            {
                if (!t.fragment.IsSet)
                {
                    continue;
                }

                if (t.anchored)
                {
                    anchored?.Add(t.fragment);
                }
                else if (t.rested)
                {
                    held?.Add(t.fragment);
                }
            }
        }

        /// <summary>Whether a body is one this trial follows (a piece, held or not, or an anchored one).</summary>
        public bool IsTracked(Rigidbody body) => body != null && _tracked.ContainsKey(body.GetInstanceID());

        /// <summary>
        /// A tracked body is fused into a group's body that stands where it stood (a fusion, or a held side group
        /// merged into the building's resting group): the group is followed from here as a held group (or as an
        /// anchored one when <paramref name="anchored"/>), and the fused body's support relations with the outside --
        /// the floor, a platform, another group or piece it stood on, and what stood on it -- pass to the group, while
        /// the edges inside the group are dropped. What stands on a held group stands on a tracked supporter, not on
        /// an unconditional ground: when the group's own support goes, the group returns to dynamic and what stood on it
        /// is judged again. Only an anchored group is ground. Nothing is woken here, because nothing lost its support.
        /// A fused body this trial does not follow contributes nothing, and the group is still followed.
        /// </summary>
        public void FuseIntoGroup(Rigidbody old, Rigidbody group, int buildingKey, IReadOnlyList<Collider> groupColliders, bool anchored)
        {
            if (!Enabled || group == null)
            {
                return;
            }

            int groupId = group.GetInstanceID();
            if (!_tracked.TryGetValue(groupId, out Tracked g))
            {
                g = NewGroup(group, buildingKey, groupColliders, _now, anchored, true);
            }
            else
            {
                SetColliders(g, groupColliders);
                if (anchored && !g.anchored)
                {
                    PromoteToAnchored(g);
                }
            }

            if (old == null || !_tracked.TryGetValue(old.GetInstanceID(), out Tracked tracked) || ReferenceEquals(tracked, g))
            {
                return;
            }

            // An edge between the group and the fused body was inside the group from here: dropped on both sides.
            g.supporters.Remove(tracked.id);
            g.grounds.Remove(tracked.id);
            g.supported.Remove(tracked.id);

            // The fused body's own supports, to the group (an edge to the group itself is inside it).
            foreach (KeyValuePair<int, Ground> edge in tracked.grounds)
            {
                if (edge.Key == groupId) continue;
                if (!g.grounds.TryGetValue(edge.Key, out Ground had) || had.lastStep < edge.Value.lastStep) g.grounds[edge.Key] = edge.Value;
            }

            foreach (KeyValuePair<int, int> edge in tracked.supporters)
            {
                if (_tracked.TryGetValue(edge.Key, out Tracked supporter)) supporter.supported.Remove(tracked.id);
                if (edge.Key == groupId) continue;
                if (!g.supporters.TryGetValue(edge.Key, out int step) || step < edge.Value) g.supporters[edge.Key] = edge.Value;
                if (supporter != null) supporter.supported.Add(groupId);
            }

            // What stood on the fused body stands on the group: a supporter edge (a held group is judged, not ground), or
            // a ground edge of the anchored kind for an anchored group.
            foreach (Tracked t in _tracked.Values)
            {
                if (ReferenceEquals(t, tracked) || ReferenceEquals(t, g))
                {
                    continue;
                }

                bool stood = t.supporters.Remove(tracked.id) | t.grounds.Remove(tracked.id);
                if (!stood)
                {
                    continue;
                }

                StandOn(t, g);
            }

            tracked.supported.Clear();
            tracked.supporters.Clear();
            tracked.grounds.Clear();
            RemoveQuietly(tracked);
            g.lastChangeStep = _step;
        }

        /// <summary>A tracked body stands on a group from here: on an anchored group as ground, on a held or dynamic group as a supporter.</summary>
        private void StandOn(Tracked t, Tracked g)
        {
            if (g.anchored)
            {
                var ground = new Ground { kind = GroundKind.Anchored, body = g.body, collider = g.colliders.Count > 0 ? g.colliders[0] : null, trackedId = g.id, lastStep = _step };
                PoseOf(ground, out ground.position, out ground.rotation);
                t.grounds[g.id] = ground;
                t.supporters.Remove(g.id);
            }
            else
            {
                t.supporters[g.id] = _step;
                t.grounds.Remove(g.id);
                g.supported.Add(t.id);
            }
        }

        /// <summary>A held group that took an anchored member is anchored from here: followed as ground only, and what stood on it stands on ground.</summary>
        private void PromoteToAnchored(Tracked g)
        {
            if (g.rested) RestedNow = Math.Max(0, RestedNow - 1);
            if (g.AsleepByUs) AsleepNow = Math.Max(0, AsleepNow - 1);
            TrackedDynamic--;
            TrackedAnchored++;
            g.anchored = true;
            g.rested = false;
            g.sleptByUs = false;
            g.grounds.Clear();
            foreach (KeyValuePair<int, int> edge in g.supporters)
            {
                if (_tracked.TryGetValue(edge.Key, out Tracked supporter)) supporter.supported.Remove(g.id);
            }

            g.supporters.Clear();
            foreach (int id in g.supported)
            {
                if (_tracked.TryGetValue(id, out Tracked above) && above.supporters.Remove(g.id)) StandOn(above, g);
            }

            Record("group anchored t " + _now.ToString("F3") + " #" + g.id + " (" + g.supported.Count + " standing on it)");
        }

        /// <summary>A fused group's state in this trial's own terms, for a diagnosis or a test. False when the body is not tracked.</summary>
        public bool TryDescribeGroup(Rigidbody body, out string state)
        {
            if (body == null || !_tracked.TryGetValue(body.GetInstanceID(), out Tracked t))
            {
                state = null;
                return false;
            }

            state = Describe(t);
            return true;
        }

        /// <summary>
        /// A tracked group body is replaced by two side bodies standing where it stood (a group's cut): what stood on
        /// it is put on the kinematic side as ground of the anchored kind (a kinematic side is one an anchor fixes; the
        /// caller tracks it as an anchored group right after), or on the positive side as a supporter when neither side
        /// is kinematic -- the contacts correct a wrong guess within a few steps -- and the old body is forgotten
        /// without waking anything. The caller tracks the sides afterwards.
        /// </summary>
        public void SplitGroup(Rigidbody old, Rigidbody positive, bool positiveKinematic, Rigidbody negative, bool negativeKinematic, Collider positiveCollider, Collider negativeCollider)
        {
            if (!Enabled || old == null || !_tracked.TryGetValue(old.GetInstanceID(), out Tracked tracked))
            {
                return;
            }

            Rigidbody groundBody = positiveKinematic ? positive : negativeKinematic ? negative : null;
            Collider groundCollider = positiveKinematic ? positiveCollider : negativeCollider;
            foreach (Tracked t in _tracked.Values)
            {
                if (ReferenceEquals(t, tracked))
                {
                    continue;
                }

                bool stood = t.supporters.Remove(tracked.id) | t.grounds.Remove(tracked.id);
                if (!stood)
                {
                    continue;
                }

                if (groundBody != null)
                {
                    var ground = new Ground { kind = GroundKind.Anchored, body = groundBody, collider = groundCollider, trackedId = groundBody.GetInstanceID(), lastStep = _step };
                    PoseOf(ground, out ground.position, out ground.rotation);
                    t.grounds[groundBody.GetInstanceID()] = ground;
                }
                else if (positive != null)
                {
                    t.supporters[positive.GetInstanceID()] = _step;   // the side is tracked by the caller right after; the mirror is filled by the contacts
                }
            }

            tracked.supported.Clear();
            tracked.supporters.Clear();
            tracked.grounds.Clear();
            RemoveQuietly(tracked);
        }

        private void RemoveQuietly(Tracked tracked)
        {
            _tracked.Remove(tracked.id);
            if (tracked.fragment.IsSet)
            {
                _byFragment.Remove(tracked.fragment);
            }

            if (tracked.anchored)
            {
                TrackedAnchored--;
            }
            else
            {
                TrackedDynamic--;
                if (tracked.AsleepByUs) AsleepNow = Math.Max(0, AsleepNow - 1);
                if (tracked.rested) RestedNow = Math.Max(0, RestedNow - 1);
            }
        }

        /// <summary>
        /// A tracked group fixed for show (the hull trial's staged stop, 2026-09-30): the caller made its body kinematic
        /// with no velocity; the rest follows it from here as ground of the anchored kind -- never released by a support
        /// change or the end of a contact -- and what stood on it stands on that ground. Nothing is woken. The group's own
        /// anchors are not the rest's concern and are not touched. A body not followed changes nothing.
        /// </summary>
        public void PinGroup(Rigidbody body, Collider collider)
        {
            if (!Enabled || body == null || !_tracked.TryGetValue(body.GetInstanceID(), out Tracked tracked) || tracked.anchored)
            {
                return;
            }

            foreach (Tracked t in _tracked.Values)
            {
                if (ReferenceEquals(t, tracked) || !t.supporters.Remove(tracked.id)) continue;
                var ground = new Ground { kind = GroundKind.Anchored, body = body, collider = collider, trackedId = tracked.id, lastStep = _step };
                PoseOf(ground, out ground.position, out ground.rotation);
                t.grounds[tracked.id] = ground;
            }

            if (tracked.rested) RestedNow = Math.Max(0, RestedNow - 1);
            if (tracked.AsleepByUs) AsleepNow = Math.Max(0, AsleepNow - 1);
            TrackedDynamic--;
            TrackedAnchored++;
            tracked.anchored = true;
            tracked.rested = false;
            tracked.sleptByUs = false;
            tracked.supported.Clear();
            tracked.supporters.Clear();
            tracked.grounds.Clear();
            tracked.lastChangeStep = _step;
            PinnedGroups++;
        }

        /// <summary>Groups fixed for show and followed as ground from then (<see cref="PinGroup"/>).</summary>
        public int PinnedGroups { get; private set; }

        /// <summary>The building key of a re-cut operation's source (the source is forgotten at the re-cut; its later children inherit through this).</summary>
        public bool TryGetReCutKey(int operation, out int key) => _reCutOperations.TryGetValue(operation, out key);

        /// <summary>
        /// Stops following one body (a retirement, a pair's abort). Its support relations go with it; in Kinematic mode
        /// the pieces that stood on it are judged again at once and those left without a confirmed chain to the ground
        /// are woken -- only they. Sleep mode wakes the support-connected group. Nothing of the body is touched.
        /// </summary>
        public void Untrack(Rigidbody body, string reason)
        {
            if (body == null || !_tracked.TryGetValue(body.GetInstanceID(), out Tracked tracked))
            {
                return;
            }

            if (!Kinematic)
            {
                WakeGroup(tracked, reason);
            }

            Forget(tracked, reason);
            if (Kinematic)
            {
                ComputeGrounded(false);
                WakeUnsupported(reason, false);
            }
        }

        /// <summary>Stops following a fragment's body, by fragment.</summary>
        public void Untrack(LogicalFragmentId fragment, string reason)
        {
            if (_byFragment.TryGetValue(fragment, out Tracked tracked))
            {
                Untrack(tracked.body, reason);
            }
        }

        /// <summary>Removes a tracked piece: the edges of its dependents and supporters, the ground edges it gave as an anchored piece, the counters.</summary>
        private void Forget(Tracked tracked, string reason)
        {
            ForgetContacts(tracked, reason);
            _tracked.Remove(tracked.id);
            if (tracked.fragment.IsSet)
            {
                _byFragment.Remove(tracked.fragment);
            }

            if (tracked.anchored)
            {
                TrackedAnchored--;
                foreach (Tracked t in _tracked.Values)
                {
                    if (t.grounds.Remove(tracked.id))
                    {
                        t.lastChangeStep = _step;
                        LostGrounds++;
                        Record("ground lost t " + _now.ToString("F3") + " piece " + Name(t) + ": anchored piece " + Name(tracked) + " retired (" + reason + ")");
                    }
                }
            }
            else
            {
                TrackedDynamic--;
                if (tracked.AsleepByUs)
                {
                    AsleepNow = Math.Max(0, AsleepNow - 1);
                }

                if (tracked.rested)
                {
                    RestedNow = Math.Max(0, RestedNow - 1);
                }
            }
        }

        /// <summary>
        /// The kinematic mode's local release at a re-cut's physical publication: the pieces the source supports, found
        /// upward along the kept support relations (visited once each), are the candidates; the source is forgotten;
        /// then, without it, a held candidate whose chain to the ground is still confirmed returns to dynamic from rest
        /// and is put to sleep once, a held candidate without one returns to dynamic and is woken, a sleeping candidate
        /// without one is woken, and a dynamic candidate is left as it is. Pieces outside the candidates are not touched.
        /// One operation is applied at most once (Provisional, then Final); a stand-in passes a negative operation.
        /// False when nothing was done (the mode, the operation seen already, or the source not tracked).
        /// </summary>
        public bool ReCut(LogicalFragmentId source, int operation, string reason)
        {
            if (!Enabled || !Kinematic)
            {
                return false;
            }

            if (operation >= 0 && _reCutOperations.ContainsKey(operation))
            {
                return false;   // this cut released already (its Provisional publication came first)
            }

            if (!_byFragment.TryGetValue(source, out Tracked src))
            {
                return false;
            }

            if (operation >= 0)
            {
                _reCutOperations[operation] = src.buildingKey;
            }

            using (s_releaseMarker.Auto())
            {
                _switchWatch.Restart();
                // The candidates: upward from the source along "supports" (visited once; the source itself is not one).
                _visited.Clear();
                _targets.Clear();
                _queue.Clear();
                _queue.Enqueue(src);
                _visited.Add(src.id);
                while (_queue.Count > 0)
                {
                    Tracked t = _queue.Dequeue();
                    foreach (int aboveId in t.supported)
                    {
                        if (_visited.Contains(aboveId) || !_tracked.TryGetValue(aboveId, out Tracked above) || above.anchored)
                        {
                            continue;
                        }

                        _visited.Add(aboveId);
                        _targets.Add(above);
                        _queue.Enqueue(above);
                    }
                }

                bool srcRested = src.rested;
                Forget(src, reason);
                ComputeGrounded(false);

                int released = 0, slept = 0, woken = 0, kept = 0;
                _lastReleased.Clear();
                foreach (Tracked t in _targets)
                {
                    if (t.body == null)
                    {
                        continue;
                    }

                    if (t.rested)
                    {
                        ToDynamic(t);
                        released++;
                        if (t.fragment.IsSet)
                        {
                            _lastReleased.Add(t.fragment);
                        }

                        if (t.grounded)
                        {
                            SleepOnce(t);
                            slept++;
                        }
                        else
                        {
                            Wake(t);
                            woken++;
                        }
                    }
                    else if (t.AsleepByUs && !t.grounded)
                    {
                        Wake(t);
                        woken++;
                    }
                    else
                    {
                        kept++;   // dynamic: its velocity and its clock stay
                    }
                }

                _switchWatch.Stop();
                double seconds = _switchWatch.Elapsed.TotalSeconds;
                ReleaseSeconds += seconds;
                ReCuts++;
                ReCutCandidates += _targets.Count;
                MaxReCutCandidates = Math.Max(MaxReCutCandidates, _targets.Count);
                ReleasedPieces += released;
                SleptOnRelease += slept;
                WokenOnRelease += woken;
                if (seconds > MaxReleaseSeconds) { MaxReleaseSeconds = seconds; MaxReleasePieces = _targets.Count; }
                Record("re-cut t " + _now.ToString("F3") + " of piece " + Name(src) + (srcRested ? " (held)" : "") + " (" + reason + "): " + _targets.Count + " above it [" + Join(_targets) + "]; held to dynamic " + released
                    + " (asleep " + slept + ", woken " + woken + "), dynamic kept " + kept + ", held outside " + RestedNow + ", in " + (seconds * 1000).ToString("F3") + " ms");
                _targets.Clear();
            }

            return true;
        }

        /// <summary>A stand-in for a re-cut in a study: the fragment is forgotten as a cut piece would be, and what it supported is released as at a re-cut.</summary>
        public bool TryReCutStandIn(LogicalFragmentId fragment, string reason) => ReCut(fragment, -1, reason);

        /// <summary>Whether this trial holds a fragment's body temporarily kinematic right now.</summary>
        public bool IsRested(LogicalFragmentId fragment) => _byFragment.TryGetValue(fragment, out Tracked t) && t.rested;

        /// <summary>Whether this trial put a fragment's body to sleep and the engine has not woken it (as far as this trial saw at its last turn).</summary>
        public bool IsAsleepByRest(LogicalFragmentId fragment) => _byFragment.TryGetValue(fragment, out Tracked t) && t.AsleepByUs;

        /// <summary>The fragment's clock: the physics time its publication or its own last return to dynamic started it. False when not tracked.</summary>
        public bool TryGetClock(LogicalFragmentId fragment, out double startedAtSeconds)
        {
            if (_byFragment.TryGetValue(fragment, out Tracked t))
            {
                startedAtSeconds = t.publishedSeconds;
                return true;
            }

            startedAtSeconds = 0.0;
            return false;
        }

        /// <summary>How many tracked pieces stand, directly, on a fragment's body (the size of one upward step of a re-cut's search).</summary>
        public int SupportedCountOf(LogicalFragmentId fragment) => _byFragment.TryGetValue(fragment, out Tracked t) ? t.supported.Count : 0;

        /// <summary>
        /// A tracked fragment's state in this trial's own terms, for a diagnosis: held, asleep or dynamic, its building,
        /// how long since its clock started, whether its chain to the ground is confirmed and for how many steps, what
        /// grounds it, and what keeps a dynamic piece from being held. False when the fragment is not tracked.
        /// </summary>
        public bool TryDescribe(LogicalFragmentId fragment, out string state)
        {
            if (!_byFragment.TryGetValue(fragment, out Tracked t))
            {
                state = null;
                return false;
            }

            state = Describe(t);
            return true;
        }

        private string Describe(Tracked t)
        {
            var text = new System.Text.StringBuilder();
            text.Append(t.anchored ? "anchored" : t.rested ? "held" : t.AsleepByUs ? "asleep by the rest" : "dynamic").Append(", building ").Append(t.buildingKey);
            if (t.isGroup) text.Append(", group");
            if (t.anchored)
            {
                text.Append(", grounds ").Append(t.supported.Count).Append(" piece(s)");
                return text.ToString();
            }

            double age = _now - t.publishedSeconds;
            text.Append(", clock ").Append(age.ToString("F2")).Append(" s (timeout ").Append(age >= _settings.timeoutSeconds ? "passed" : "not yet").Append(')');
            text.Append(", grounded ").Append(t.grounded).Append(" for ").Append(t.supportedSteps).Append(" step(s) via ").Append(t.grounds.Count).Append(" ground(s) and ").Append(t.supporters.Count).Append(" supporter(s), supports ").Append(t.supported.Count);
            foreach (KeyValuePair<int, Ground> g in t.grounds)
            {
                text.Append(" [").Append(g.Value.kind).Append(" #").Append(g.Key).Append(']');
            }

            if (t.rested)
            {
                text.Append(", held since t ").Append(t.sleptAt.ToString("F2")).Append(" after moving ").Append((t.body != null ? (t.body.worldCenterOfMass - t.trackedAt).magnitude : 0f).ToString("F3")).Append(" m");
            }
            else if (t.body != null)
            {
                text.Append(", |v| ").Append(t.body.linearVelocity.magnitude.ToString("F2")).Append(" m/s, ").Append(t.body.IsSleeping() ? "asleep" : "awake");
                text.Append(", last change ").Append(_step - t.lastChangeStep).Append(" step(s) ago");
                if (!t.fragment.IsSet) text.Append(", fragment not set (Final pending)");
                if (t.fragment.IsSet && CutInProgress != null && CutInProgress(t.fragment)) text.Append(", a cut in progress");
                if (t.body.isKinematic) text.Append(", kinematic by another");
                if (t.autoWoken) text.Append(", auto-woken once");
            }

            return text.ToString();
        }

        /// <summary>
        /// Wakes the support-connected group of a fragment's body explicitly (Sleep mode: a re-cut of it is being
        /// published, or its support is changing). The body itself is woken too when it is asleep by this trial.
        /// </summary>
        public void WakeGroupOf(LogicalFragmentId fragment, string reason)
        {
            if (_byFragment.TryGetValue(fragment, out Tracked tracked))
            {
                WakeGroup(tracked, reason);
            }
        }

        private void SetColliders(Tracked tracked, IReadOnlyList<Collider> colliders)
        {
            tracked.colliders.Clear();
            if (colliders == null)
            {
                return;
            }

            for (int i = 0; i < colliders.Count; i++)
            {
                Collider c = colliders[i];
                if (c == null)
                {
                    continue;
                }

                c.providesContacts = true;
                tracked.colliders.Add(c);
            }
        }

        private void ForgetContacts(Tracked tracked, string reason)
        {
            foreach (int supporterId in tracked.supporters.Keys)
            {
                if (_tracked.TryGetValue(supporterId, out Tracked supporter))
                {
                    supporter.supported.Remove(tracked.id);
                }
            }

            tracked.supporters.Clear();
            foreach (int supportedId in tracked.supported)
            {
                if (_tracked.TryGetValue(supportedId, out Tracked above))
                {
                    above.supporters.Remove(tracked.id);
                    above.lastChangeStep = _step;
                }
            }

            tracked.supported.Clear();
            tracked.grounds.Clear();
            tracked.grounded = false;
            tracked.supportedSteps = 0;
            tracked.touched.Clear();
        }

        // ---- the switches ----

        /// <summary>A held piece back to dynamic, from rest: velocities zero, its clock starts now. A group's fusion is told first (its mass properties, its state).</summary>
        private void ToDynamic(Tracked t)
        {
            if (t.isGroup)
            {
                GroupReleasing?.Invoke(t.body);
                GroupsReleased++;
            }

            t.body.isKinematic = false;
            t.body.linearVelocity = Vector3.zero;
            t.body.angularVelocity = Vector3.zero;
            t.rested = false;
            t.publishedSeconds = _now;
            t.supportedSteps = 0;
            t.lastChangeStep = _step;
            t.trackedAt = t.body.worldCenterOfMass;
            RestedNow = Math.Max(0, RestedNow - 1);
        }

        /// <summary>One explicit sleep of a dynamic piece (its support still confirmed); the engine may wake it later, which is observed and not undone.</summary>
        private void SleepOnce(Tracked t)
        {
            t.body.Sleep();
            t.sleptByUs = true;
            t.autoWoken = false;
            t.wokenByUs = false;
            t.sleptAt = _now;
            SleepAttempted++;
            if (t.body.IsSleeping())
            {
                SleepSucceeded++;
                AsleepNow++;
            }
        }

        /// <summary>A dynamic piece woken by this trial (its support is not confirmed).</summary>
        private void Wake(Tracked t)
        {
            if (t.AsleepByUs)
            {
                AsleepNow = Math.Max(0, AsleepNow - 1);
            }

            if (t.body.IsSleeping())
            {
                t.body.WakeUp();
            }

            t.wokenByUs = true;
            t.sleptByUs = false;
            t.lastChangeStep = _step;
            ExplicitWakes++;
        }

        /// <summary>Kinematic mode: every held or sleeping piece whose chain to the ground is not confirmed returns to dynamic (if held) and is woken.</summary>
        private void WakeUnsupported(string reason, bool countLoss)
        {
            _scratch.Clear();
            foreach (Tracked t in _tracked.Values)
            {
                if (!t.anchored && t.body != null && !t.grounded && (t.rested || t.AsleepByUs))
                {
                    _scratch.Add(t);
                }
            }

            foreach (Tracked t in _scratch)
            {
                bool wasHeld = t.rested;
                if (t.rested)
                {
                    ToDynamic(t);
                }

                Wake(t);
                WokenBySupportLoss++;
                Record("support lost t " + _now.ToString("F3") + " piece " + Name(t) + (wasHeld ? " (held)" : " (asleep)") + " woken: " + reason);
            }
        }

        // ---- the contacts, taken as notes inside the simulation ----

        private void OnContacts(PhysicsScene scene, NativeArray<ContactPairHeader>.ReadOnly headers)
        {
            if (_disposed)
            {
                return;
            }

            using (s_contactsMarker.Auto())
            {
                _watch.Restart();
                for (int h = 0; h < headers.Length; h++)
                {
                    ContactPairHeader header = headers[h];
                    ContactHeaders++;
                    int a = header.bodyInstanceID, b = header.otherBodyInstanceID;
                    bool aTracked = _tracked.TryGetValue(a, out Tracked ta), bTracked = _tracked.TryGetValue(b, out Tracked tb);
                    if (!aTracked && !bTracked)
                    {
                        continue;
                    }

                    // Only a dynamic tracked piece is judged; an anchored one grounds the other side.
                    bool aDynamic = aTracked && !ta.anchored, bDynamic = bTracked && !tb.anchored;
                    if (!aDynamic && !bDynamic)
                    {
                        continue;
                    }

                    ContactPairs += header.pairCount;
                    for (int p = 0; p < header.pairCount; p++)
                    {
                        ContactPair pair = header.GetContactPair(p);
                        if (pair.isCollisionExit)
                        {
                            if (aDynamic) Take(Classify(a, b, bTracked, tb, header.otherBody, pair.otherColliderInstanceID, null, false, true));
                            if (bDynamic) Take(Classify(b, a, aTracked, ta, header.body, pair.colliderInstanceID, null, false, true));
                            continue;
                        }

                        bool aBelow = false, bBelow = false;
                        ContactPoints += pair.contactCount;
                        for (int c = 0; c < pair.contactCount; c++)
                        {
                            ContactPairPoint point = pair.GetContactPoint(c);
                            float ny = Mathf.Abs(point.normal.y);
                            if (ny < _settings.supportNormalCos)
                            {
                                continue;   // a side touch, not support
                            }

                            if (aDynamic && point.position.y < ta.centreY - 0.005f) aBelow = true;
                            if (bDynamic && point.position.y < tb.centreY - 0.005f) bBelow = true;
                        }

                        // The supporting collider is resolved only for a from-below note (a lookup by instance id).
                        if (aDynamic) Take(Classify(a, b, bTracked, tb, header.otherBody, pair.otherColliderInstanceID, aBelow ? pair.otherCollider : null, aBelow, false));
                        if (bDynamic) Take(Classify(b, a, aTracked, ta, header.body, pair.colliderInstanceID, bBelow ? pair.collider : null, bBelow, false));
                    }
                }

                _watch.Stop();
                ContactSeconds += _watch.Elapsed.TotalSeconds;
            }
        }

        /// <summary>
        /// A note on what the other side of a pair is for support: a ground supporter kept by identity (a static
        /// collider by its collider id; a kinematic body that is not a piece, or an anchored piece, by its body id), a
        /// tracked dynamic piece, or an untracked dynamic body.
        /// </summary>
        private static Note Classify(int bodyId, int otherId, bool otherTracked, Tracked other, Component otherBody, int otherColliderId, Collider otherCollider, bool fromBelow, bool exit)
        {
            var note = new Note { body = bodyId, otherTracked = otherTracked, fromBelow = fromBelow, exit = exit, groundCollider = otherCollider };
            if (otherId == 0)
            {
                note.other = otherColliderId;   // a static collider: its own identity
                note.ground = GroundKind.Static;
                return note;
            }

            note.other = otherId;
            if (otherTracked)
            {
                if (other.anchored)
                {
                    note.ground = GroundKind.Anchored;
                    note.groundBody = other.body;
                }

                return note;
            }

            // The header's body is a Component: a Rigidbody here, or an articulation body (treated as a kinematic ground).
            if (!(otherBody is Rigidbody rigidbody) || rigidbody.isKinematic)
            {
                note.ground = GroundKind.Kinematic;
                note.groundBody = otherBody as Rigidbody;
                return note;
            }

            note.externalDynamic = true;
            return note;
        }

        private void Take(Note note)
        {
            _notes.Add(note);
            ContactNotes++;
        }

        // ---- the judgement, outside the simulation ----

        /// <summary>
        /// One turn, to be called outside the simulation after the frame's step decision: <paramref name="steps"/> is
        /// how many physics steps happened since the last turn (0 on a frame that skipped its step: the timers stand
        /// still and the notes are kept for the next turn), <paramref name="stepSeconds"/> the step length and
        /// <paramref name="physicsSeconds"/> the physics time now.
        /// </summary>
        public void Step(int steps, double stepSeconds, double physicsSeconds)
        {
            if (!Enabled)
            {
                return;
            }

            _now = physicsSeconds;
            if (NotesOnly)
            {
                _notes.Clear();
                Steps++;
                CacheCentres();
                return;
            }

            if (steps <= 0)
            {
                CacheCentres();
                return;
            }

            _step += steps;
            Steps++;
            using (s_supportMarker.Auto())
            {
                _watch.Restart();
                ApplyNotes();
                JudgeSupport();
                _watch.Stop();
                SupportSeconds += _watch.Elapsed.TotalSeconds;
                MaxSupportSeconds = Math.Max(MaxSupportSeconds, _watch.Elapsed.TotalSeconds);
            }

            using (s_sleepMarker.Auto())
            {
                _watch.Restart();
                ObserveWakes();
                if (Kinematic)
                {
                    RestPieces();
                }
                else
                {
                    RestGroups();
                }

                Count();
                _watch.Stop();
                SleepSeconds += _watch.Elapsed.TotalSeconds;
                MaxSleepSeconds = Math.Max(MaxSleepSeconds, _watch.Elapsed.TotalSeconds);
            }

            CacheCentres();
        }

        private void CacheCentres()
        {
            foreach (Tracked t in _tracked.Values)
            {
                if (t.body != null && !t.anchored)
                {
                    t.centreY = t.body.worldCenterOfMass.y;
                }
            }
        }

        /// <summary>A piece whose pairs are no longer reported by the engine: held kinematic, or asleep.</summary>
        private static bool Quiet(Tracked t) => t.rested || (t.body != null && t.body.IsSleeping());

        private void ApplyNotes()
        {
            foreach (Tracked t in _tracked.Values)
            {
                t.touched.Clear();
            }

            for (int i = 0; i < _notes.Count; i++)
            {
                Note note = _notes[i];
                if (!_tracked.TryGetValue(note.body, out Tracked t))
                {
                    continue;
                }

                t.touched.Add(note.other);
                if (note.exit)
                {
                    // A quiet piece (held, or asleep): the engine stops tracking its pairs with the ground and with other
                    // quiet pieces and reports them as exits -- an artefact of the switch, not a lost support; the held
                    // ground supporters are checked by identity instead (VerifyGrounds). An exit from an awake dynamic
                    // supporter that moved away is honoured.
                    if (Quiet(t) && (note.ground != GroundKind.None || (_tracked.TryGetValue(note.other, out Tracked gone) && Quiet(gone))))
                    {
                        continue;
                    }

                    if (note.ground != GroundKind.None)
                    {
                        t.grounds.Remove(note.other);
                    }
                    else if (t.supporters.Remove(note.other) && _tracked.TryGetValue(note.other, out Tracked s))
                    {
                        s.supported.Remove(t.id);
                    }

                    continue;
                }

                if (!note.fromBelow)
                {
                    continue;
                }

                if (note.ground != GroundKind.None)
                {
                    if (!t.grounds.TryGetValue(note.other, out Ground ground))
                    {
                        ground = new Ground { kind = note.ground, collider = note.groundCollider, body = note.groundBody, trackedId = note.ground == GroundKind.Anchored ? note.other : 0 };
                        t.grounds[note.other] = ground;
                    }

                    ground.lastStep = _step;
                    PoseOf(ground, out ground.position, out ground.rotation);
                }
                else if (note.otherTracked && _tracked.TryGetValue(note.other, out Tracked supporter) && !supporter.anchored)
                {
                    t.supporters[note.other] = _step;
                    supporter.supported.Add(t.id);
                }
            }

            _notes.Clear();
        }

        /// <summary>Where a ground supporter is now: its body's pose, or its collider's transform for a static one.</summary>
        private static bool PoseOf(Ground ground, out Vector3 position, out Quaternion rotation)
        {
            if (ground.body != null)
            {
                position = ground.body.position;
                rotation = ground.body.rotation;
                return true;
            }

            if (ground.collider != null)
            {
                Transform transform = ground.collider.transform;
                position = transform.position;
                rotation = transform.rotation;
                return true;
            }

            position = default;
            rotation = Quaternion.identity;
            return false;
        }

        private void JudgeSupport()
        {
            // Stale edges go where the engine reports the pair: an awake piece's ground edges, and any piece's edge to an
            // awake dynamic supporter (a held or sleeping piece against a kinematic or sleeping one is not reported and
            // keeps what it had; its ground supporters are verified by identity below).
            foreach (Tracked t in _tracked.Values)
            {
                if (t.anchored || t.body == null)
                {
                    continue;
                }

                _stale.Clear();
                if (!Quiet(t))
                {
                    foreach (KeyValuePair<int, Ground> edge in t.grounds)
                    {
                        if (edge.Value.lastStep < _step - StaleEdgeSteps)
                        {
                            _stale.Add(edge.Key);
                        }
                    }

                    foreach (int id in _stale)
                    {
                        t.grounds.Remove(id);
                    }

                    _stale.Clear();
                }

                foreach (KeyValuePair<int, int> edge in t.supporters)
                {
                    if (edge.Value < _step - StaleEdgeSteps && _tracked.TryGetValue(edge.Key, out Tracked s) && !Quiet(s) && !s.body.isKinematic)
                    {
                        _stale.Add(edge.Key);   // the supporter is awake and dynamic: its pairs are reported, so a silent one has moved to the side or away
                    }
                }

                foreach (int id in _stale)
                {
                    t.supporters.Remove(id);
                    if (_tracked.TryGetValue(id, out Tracked s))
                    {
                        s.supported.Remove(t.id);
                    }
                }
            }

            VerifyGrounds();
            ComputeGrounded(true);
            if (Kinematic)
            {
                WakeUnsupported("its chain to the ground broke", true);
            }
        }

        /// <summary>
        /// The ground supporters a quiet (held or sleeping) piece holds, checked by identity now that their contacts are
        /// no longer reported: a disabled or destroyed collider, a kinematic body that moved or turned dynamic, a static
        /// collider that moved, an anchored piece no longer tracked as such -- each is a support gone.
        /// </summary>
        private void VerifyGrounds()
        {
            foreach (Tracked t in _tracked.Values)
            {
                if (t.anchored || t.body == null || !Quiet(t) || t.grounds.Count == 0)
                {
                    continue;
                }

                _stale.Clear();
                foreach (KeyValuePair<int, Ground> edge in t.grounds)
                {
                    string why = GroundLost(edge.Value);
                    if (why == null)
                    {
                        continue;
                    }

                    _stale.Add(edge.Key);
                    Record("ground lost t " + _now.ToString("F3") + " piece " + Name(t) + ": " + edge.Value.kind + " #" + edge.Key + " " + why);
                }

                if (_stale.Count == 0)
                {
                    continue;
                }

                foreach (int key in _stale)
                {
                    t.grounds.Remove(key);
                }

                t.lastChangeStep = _step;
                LostGrounds += _stale.Count;
            }
        }

        /// <summary>Why a held ground supporter no longer supports, or null while it still does.</summary>
        private string GroundLost(Ground ground)
        {
            switch (ground.kind)
            {
                case GroundKind.Anchored:
                    if (!_tracked.TryGetValue(ground.trackedId, out Tracked anchoredPiece) || !anchoredPiece.anchored)
                    {
                        return "no longer an anchored piece";
                    }

                    if (anchoredPiece.body == null)
                    {
                        return "destroyed";
                    }

                    break;
                case GroundKind.Kinematic:
                    if (ground.body != null && !ground.body.isKinematic)
                    {
                        return "no longer kinematic";
                    }

                    break;
            }

            if (ground.body != null && !ground.body.gameObject.activeInHierarchy)
            {
                return "inactive";
            }

            if (ground.collider != null)
            {
                if (!ground.collider.enabled || !ground.collider.gameObject.activeInHierarchy)
                {
                    return "collider disabled";
                }
            }
            else
            {
                return "collider destroyed";   // the supporting collider is gone, whether or not its body remains
            }

            if (!PoseOf(ground, out Vector3 position, out Quaternion rotation))
            {
                return "destroyed";
            }

            if ((position - ground.position).magnitude > MovedMetres || Quaternion.Angle(rotation, ground.rotation) > MovedDegrees)
            {
                return "moved " + (position - ground.position).magnitude.ToString("F3") + " m, " + Quaternion.Angle(rotation, ground.rotation).ToString("F2") + " deg";
            }

            return null;
        }

        /// <summary>Grounded: a from-below chain to a ground supporter, upward from the pieces standing on one. The support step count advances when <paramref name="countStep"/>.</summary>
        private void ComputeGrounded(bool countStep)
        {
            _queue.Clear();
            foreach (Tracked t in _tracked.Values)
            {
                if (t.anchored)
                {
                    continue;
                }

                t.grounded = t.grounds.Count > 0;
                if (t.grounded)
                {
                    _queue.Enqueue(t);
                }
            }

            while (_queue.Count > 0)
            {
                Tracked s = _queue.Dequeue();
                foreach (int aboveId in s.supported)
                {
                    if (_tracked.TryGetValue(aboveId, out Tracked above) && !above.grounded)
                    {
                        above.grounded = true;
                        _queue.Enqueue(above);
                    }
                }
            }

            foreach (Tracked t in _tracked.Values)
            {
                if (!t.anchored)
                {
                    t.supportedSteps = t.grounded ? (countStep ? t.supportedSteps + 1 : t.supportedSteps) : 0;
                }
            }
        }

        private void ObserveWakes()
        {
            foreach (Tracked t in _tracked.Values)
            {
                if (t.anchored || t.body == null || !t.AsleepByUs)
                {
                    continue;
                }

                if (!t.body.IsSleeping())
                {
                    t.autoWoken = true;
                    AutoWakes++;
                    AsleepNow = Math.Max(0, AsleepNow - 1);
                    Record("auto wake t " + _now.ToString("F3") + " piece " + Name(t) + " (asleep " + (_now - t.sleptAt).ToString("F2") + " s) |v| " + t.body.linearVelocity.magnitude.ToString("F3")
                        + " |w| " + t.body.angularVelocity.magnitude.ToString("F2") + "; touched " + Describe(t.touched) + "; change within " + (_step - t.lastChangeStep) + " steps: " + (_step - t.lastChangeStep <= StaleEdgeSteps + 1));
                }
            }
        }

        /// <summary>Whether a dynamic piece meets its own conditions for rest (clock, support steps, recent change, Final done, no cut in progress).</summary>
        private bool OwnConditions(Tracked t)
        {
            if (t.anchored || t.rested || t.body == null || t.body.isKinematic || !t.grounded)
            {
                return false;
            }

            if (t.supportedSteps < _settings.supportSteps || _now - t.publishedSeconds < _settings.timeoutSeconds || _step - t.lastChangeStep <= _settings.supportSteps)
            {
                return false;
            }

            if (t.isGroup)
            {
                return GroupBusy == null || !GroupBusy(t.body);
            }

            return t.fragment.IsSet && (CutInProgress == null || !CutInProgress(t.fragment));
        }

        /// <summary>
        /// Kinematic mode's rest turn, piece by piece from the confirmed bottom: a piece that meets its own conditions is
        /// held when it stands on a ground supporter, or on a piece already held (or held in this turn). A young upper
        /// piece waits for its own clock and does not keep a stable lower one from resting; a piece whose only support
        /// is an awake dynamic piece waits for that one. A sleeping piece is a candidate like an awake one.
        /// </summary>
        private void RestPieces()
        {
            _stable.Clear();
            _scratch.Clear();
            foreach (Tracked t in _tracked.Values)
            {
                if (t.anchored || t.rested)
                {
                    _stable.Add(t.id);
                }
                else if (OwnConditions(t))
                {
                    _scratch.Add(t);
                }
            }

            if (_scratch.Count == 0)
            {
                return;
            }

            _targets.Clear();
            bool progress = true;
            while (progress)
            {
                progress = false;
                for (int i = 0; i < _scratch.Count; i++)
                {
                    Tracked t = _scratch[i];
                    if (t.rested)
                    {
                        continue;
                    }

                    bool confirmed = t.grounds.Count > 0;
                    if (!confirmed)
                    {
                        foreach (int id in t.supporters.Keys)
                        {
                            if (_stable.Contains(id))
                            {
                                confirmed = true;
                                break;
                            }
                        }
                    }

                    if (!confirmed)
                    {
                        continue;
                    }

                    Hold(t);
                    _stable.Add(t.id);
                    _targets.Add(t);
                    progress = true;
                }
            }

            if (_targets.Count > 0)
            {
                KinematicRests += _targets.Count;
                RestedNow += _targets.Count;
                MeanMovedUntilRest = _movedCount > 0 ? _movedSum / _movedCount : 0f;
                Record("kinematic rest t " + _now.ToString("F3") + " " + _targets.Count + " piece(s) [" + Join(_targets) + "]");
                _targets.Clear();
            }
        }

        /// <summary>A dynamic piece to temporary kinematic: zero while dynamic, then held; nothing of the mass properties is touched.</summary>
        private void Hold(Tracked t)
        {
            _switchWatch.Restart();
            if (t.AsleepByUs)
            {
                AsleepNow = Math.Max(0, AsleepNow - 1);
            }

            t.body.linearVelocity = Vector3.zero;
            t.body.angularVelocity = Vector3.zero;
            t.body.isKinematic = true;
            t.rested = true;
            t.sleptByUs = false;
            t.autoWoken = false;
            t.wokenByUs = false;
            t.sleptAt = _now;
            if (t.isGroup)
            {
                GroupHeld?.Invoke(t.body);
            }

            float moved = (t.body.worldCenterOfMass - t.trackedAt).magnitude;
            _movedSum += moved; _movedCount++;
            MaxMovedUntilRest = Mathf.Max(MaxMovedUntilRest, moved);
            _switchWatch.Stop();
            SwitchSeconds += _switchWatch.Elapsed.TotalSeconds;
        }

        /// <summary>Sleep mode's rest turn: the support-connected group is put to sleep together when all its members meet their conditions.</summary>
        private void RestGroups()
        {
            _visited.Clear();
            foreach (Tracked seed in _tracked.Values)
            {
                if (seed.anchored || _visited.Contains(seed.id) || seed.body == null)
                {
                    continue;
                }

                // The support-connected group, both ways along the edges.
                _scratch.Clear();
                _queue.Clear();
                _queue.Enqueue(seed);
                _visited.Add(seed.id);
                while (_queue.Count > 0)
                {
                    Tracked t = _queue.Dequeue();
                    _scratch.Add(t);
                    foreach (int id in t.supporters.Keys) Visit(id);
                    foreach (int id in t.supported) Visit(id);
                }

                bool all = true; int pending = 0;
                for (int i = 0; i < _scratch.Count && all; i++)
                {
                    Tracked t = _scratch[i];
                    pending++;
                    all = t.body != null && !t.body.isKinematic && !t.sleptByUs && !t.autoWoken && t.grounded
                          && t.supportedSteps >= _settings.supportSteps && _now - t.publishedSeconds >= _settings.timeoutSeconds
                          && _step - t.lastChangeStep > _settings.supportSteps;
                }

                if (!all || pending == 0)
                {
                    continue;
                }

                int succeeded = 0;
                for (int i = 0; i < _scratch.Count; i++)
                {
                    Tracked t = _scratch[i];
                    t.body.Sleep();
                    t.sleptByUs = true;
                    t.wokenByUs = false;
                    t.sleptAt = _now;
                    SleepAttempted++;
                    if (t.body.IsSleeping())
                    {
                        succeeded++;
                        SleepSucceeded++;
                        AsleepNow++;
                    }
                }

                Record("sleep t " + _now.ToString("F3") + " group of " + _scratch.Count + " piece(s) [" + Join(_scratch) + "], engine reports asleep " + succeeded);
            }
        }

        private void Visit(int id)
        {
            if (!_visited.Contains(id) && _tracked.TryGetValue(id, out Tracked t) && !t.anchored)
            {
                _visited.Add(id);
                _queue.Enqueue(t);
            }
        }

        /// <summary>Sleep mode: the support-connected group of a piece is woken explicitly and its judgement starts over.</summary>
        private void WakeGroup(Tracked seed, string reason)
        {
            if (!Enabled || seed.anchored && seed.supported.Count == 0)
            {
                return;
            }

            _visited.Clear();
            _scratch.Clear();
            _queue.Clear();
            _queue.Enqueue(seed);
            _visited.Add(seed.id);
            while (_queue.Count > 0)
            {
                Tracked t = _queue.Dequeue();
                _scratch.Add(t);
                foreach (int id in t.supporters.Keys) Visit(id);
                foreach (int id in t.supported) Visit(id);
            }

            int woken = 0;
            for (int i = 0; i < _scratch.Count; i++)
            {
                Tracked t = _scratch[i];
                t.lastChangeStep = _step;
                if (t.anchored || t.body == null || !t.AsleepByUs)
                {
                    continue;
                }

                if (t.body.IsSleeping())
                {
                    t.body.WakeUp();
                }

                t.wokenByUs = true;
                t.sleptByUs = false;
                t.publishedSeconds = _now;
                t.supportedSteps = 0;
                woken++;
                ExplicitWakes++;
                AsleepNow = Math.Max(0, AsleepNow - 1);
            }

            if (woken > 0)
            {
                Record("explicit wake t " + _now.ToString("F3") + " " + reason + ": " + woken + " of the group of " + _scratch.Count + " [" + Join(_scratch) + "]");
            }
        }

        private void Count()
        {
            int unsupported = 0, asleepWithout = 0;
            foreach (Tracked t in _tracked.Values)
            {
                if (t.anchored || t.body == null)
                {
                    continue;
                }

                bool quietByUs = t.rested || (t.AsleepByUs && t.body.IsSleeping());
                if (quietByUs && !t.grounded)
                {
                    asleepWithout++;
                }

                if (!quietByUs && !t.grounded && _now - t.publishedSeconds >= _settings.timeoutSeconds)
                {
                    unsupported++;
                }
            }

            UnsupportedPastTimeout = unsupported;
            AsleepWithoutSupport = asleepWithout;
        }

        private void Record(string line)
        {
            if (_events.Count < _settings.eventRecords)
            {
                _events.Add(line);
            }
        }

        private static string Name(Tracked t) => t.fragment.IsSet ? t.fragment.value.ToString() : "#" + t.id;

        private static string Join(List<Tracked> group)
        {
            var parts = new List<string>(group.Count);
            foreach (Tracked t in group)
            {
                parts.Add(Name(t));
            }

            return string.Join(",", parts);
        }

        private string Describe(HashSet<int> partners)
        {
            if (partners.Count == 0)
            {
                return "nothing";
            }

            var parts = new List<string>(partners.Count);
            foreach (int id in partners)
            {
                parts.Add(_tracked.TryGetValue(id, out Tracked t) && t.fragment.IsSet ? "piece " + t.fragment.value : "other #" + id);
            }

            return string.Join(", ", parts);
        }

        /// <summary>Ends the trial: the contact subscription goes and every tracking record is dropped (the counters and the events stay readable). No body is touched.</summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_subscribed)
            {
                Physics.ContactEvent -= OnContacts;
                _subscribed = false;
            }

            _tracked.Clear();
            _byFragment.Clear();
            _notes.Clear();
            _reCutOperations.Clear();
        }
    }
}
