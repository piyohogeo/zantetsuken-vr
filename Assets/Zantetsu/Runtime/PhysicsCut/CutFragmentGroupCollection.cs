#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>What the pieces of a group came from.</summary>
    public enum CutGroupKind
    {
        /// <summary>One NPC individual's cut lineage: ended whole, nothing brought back.</summary>
        Npc = 0,

        /// <summary>One placed prop: ended whole, its own instance brought back as it stood.</summary>
        Prop = 1,

        /// <summary>One building: ended whole with what its pieces share, its own instance brought back.</summary>
        Building = 2,
    }

    /// <summary>
    /// What a group's pieces came from, for <see cref="CutFragmentGroupCollection"/>: the original object that left when
    /// it was cut, what its pieces share besides their own owners, and how it comes back. An NPC's lineage has none.
    /// </summary>
    public interface ICutGroupOrigin
    {
        CutGroupKind Kind { get; }

        /// <summary>Where the original would stand again, in the world; false when it cannot be said (then the group is not chosen).</summary>
        bool TryGetRestoreBounds(out Bounds world);

        /// <summary>What the group's pieces share that has a place of its own (a building's hull), in the world; false when there is none.</summary>
        bool TryGetSharedBounds(out Bounds world);

        /// <summary>Whether nothing of the group's own -- beyond what the ledger and the owners say -- is under way. Reads only.</summary>
        bool MayEndNow(out string why);

        /// <summary>
        /// Called inside the retirement, right after every piece of the group was retired: what the pieces share ends
        /// here (a building's shared body, collider and tracking). Nothing of the original comes back yet.
        /// </summary>
        void EndShared(string why);

        /// <summary>
        /// The original comes back as it stood when it was cut: its renderers, colliders and bodies as they were before
        /// it left -- what was off then stays off. Called once the display has adopted a collection that draws nothing
        /// of the group. False when it cannot (the instance is gone): the group is forgotten then.
        /// </summary>
        bool TryReturn();

        /// <summary>
        /// The original becomes a cut target again, through the entrance an uncut one uses, to be given a new fragment
        /// at its next hit. May cost (a prop's convexes are cooked again): called in a frame that has room. False when
        /// it cannot be made one.
        /// </summary>
        bool TryRearm();
    }

    /// <summary>How <see cref="CutFragmentGroupCollection"/> chooses and paces. Every value is the caller's; the profile holds the provisional ones.</summary>
    public readonly struct CutFragmentGroupSettings
    {
        public CutFragmentGroupSettings(
            bool enabled, int maxLiveCutFragments, int examinePerFrame, double reserveSeconds,
            double seedSecondsPerFragment, double seedSecondsPerGroup)
        {
            this.enabled = enabled;
            this.maxLiveCutFragments = maxLiveCutFragments;
            this.examinePerFrame = examinePerFrame;
            this.reserveSeconds = reserveSeconds;
            this.seedSecondsPerFragment = seedSecondsPerFragment;
            this.seedSecondsPerGroup = seedSecondsPerGroup;
        }

        /// <summary>Off: no group is looked at and no retirement is started. What was started goes on regardless.</summary>
        public readonly bool enabled;

        /// <summary>Groups are looked at only while more live cut fragments than this stand. Where a collection starts, not a limit kept.</summary>
        public readonly int maxLiveCutFragments;

        /// <summary>
        /// Groups looked at per frame, at most. One retirement a frame at most: of the groups a frame looked at and
        /// could end, the farthest.
        /// </summary>
        public readonly int examinePerFrame;

        /// <summary>The Main budget a retirement must leave to the rest of the frame, in seconds, beyond twice its own expected cost.</summary>
        public readonly double reserveSeconds;

        /// <summary>The expected cost of retiring one piece of a group, and of a group besides its pieces, in seconds, before any was measured.</summary>
        public readonly double seedSecondsPerFragment;
        public readonly double seedSecondsPerGroup;

        public bool IsValid =>
            maxLiveCutFragments >= 0
            && examinePerFrame > 0 && reserveSeconds >= 0.0 && !double.IsInfinity(reserveSeconds)
            && seedSecondsPerFragment >= 0.0 && !double.IsInfinity(seedSecondsPerFragment)
            && seedSecondsPerGroup >= 0.0 && !double.IsInfinity(seedSecondsPerGroup);
    }

    /// <summary>Why a turn of <see cref="CutFragmentGroupCollection"/> looked at no group.</summary>
    public enum CutGroupHold
    {
        None = 0,
        HeavyWork,
        GarbageCollection,
        CutInFlight,
        StructureChanged,
        DisplayRoomGrown,
        CompactionRan,

        /// <summary>The collection of empty owners started a retirement in this frame: the two do not share a frame.</summary>
        EmptyOwnerRetired,

        /// <summary>No more live cut fragments than the setting: nothing to start.</summary>
        UnderTheMaximum,

        /// <summary>What is left of the frame's budget is less than the reserve and twice what looking at one group is expected to cost: no group is looked at.</summary>
        BudgetShort,
        Count,
    }

    /// <summary>What a group looked at turned out to be.</summary>
    public enum CutGroupPass
    {
        Candidate = 0,

        /// <summary>No live cut fragment of it stands, or it is on its way back already.</summary>
        NothingToEnd,

        /// <summary>A cut of one of its pieces is accepted, or a Provisional pair of one stands.</summary>
        CutUnderWay,

        /// <summary>A piece's geometry is not committed yet.</summary>
        GeometryOpen,

        /// <summary>A piece has no owner here, or one whose physics is a fused group's.</summary>
        NoOwner,

        /// <summary>What the group shares is under way (a building cutting, dropping, updating its hull), or is of a mode not ended here.</summary>
        SharedBusy,

        /// <summary>Some bounds of it -- a piece's, the shared one's, the original's -- may be in an eye's view.</summary>
        Seen,

        /// <summary>There is no view to judge it against, nothing of it has bounds to judge, or the original's bounds cannot be said.</summary>
        NotJudged,

        /// <summary>What is left of the frame's budget is less than the reserve and twice this group's expected cost.</summary>
        BudgetShort,

        /// <summary>At the application only: the family changed since it was chosen.</summary>
        Changed,
        Count,
    }

    /// <summary>One group chosen, with the family's revision it was chosen at.</summary>
    public readonly struct CutGroupCandidate
    {
        internal CutGroupCandidate(LogicalFragmentId root, long revision, int fragments, float nearestDistance)
        {
            Root = root;
            Revision = revision;
            Fragments = fragments;
            NearestDistance = nearestDistance;
        }

        public LogicalFragmentId Root { get; }

        internal long Revision { get; }

        public int Fragments { get; }

        /// <summary>
        /// How far the group is, in metres: from the viewer to the NEAREST of its bounds -- each piece's, and for a prop
        /// or a building what the pieces share and where the original would stand again. A group with one piece near
        /// is a near group, however far another piece flew. Infinity where it was chosen without a view
        /// (<see cref="CutFragmentGroupCollection.TryChoose"/>).
        /// </summary>
        public float NearestDistance { get; }

        public bool IsSet => Root.IsSet;
    }

    /// <summary>
    /// DESIGN 7.10's lifetime of live logical fragments by the object they came from (Phase 7.1): once more cut
    /// fragments stand than the setting, a whole group -- every piece of one building, one prop or one NPC individual --
    /// that is outside both eyes is ended at one boundary, the farther of those looked at first, and a building or a
    /// prop is given back its own instance, to be cut again as an uncut one.
    /// <para>
    /// **A group** is one registered fragment and everything cut from it: the ledger's family. The live cut fragments
    /// of a family and of the whole ledger are kept by the ledger where a child is published and where a fragment stops
    /// being live; nothing walks a history to find them. A group is known here from the moment its cut is accepted
    /// (<see cref="Register"/>), by whoever knows what it came from.
    /// </para>
    /// <para>
    /// **Which group.** Every piece live, none with a cut accepted or a Provisional pair, each with its geometry
    /// committed and an owner here; what the pieces share quiet (<see cref="ICutGroupOrigin.MayEndNow"/>); and every
    /// bounds of it -- each piece's shape where its owner stands, what is drawn of each piece where it is drawn now
    /// (a building's display piece has no shape of its own and moves apart from its hull), what they share, and where
    /// the original would stand again -- outside both eyes' views. No distance is asked (TL, 2026-10-13: it was 20 m):
    /// a group nothing of which either eye can see is ended however near it lies, and a far one with one piece in one
    /// eye's view stands whole. An original that would come back in view is not chosen for pieces that flew away, nor
    /// a building whose hull is in view. Without a camera, or with nothing of a group to judge, nothing is chosen.
    /// </para>
    /// <para>
    /// **The farther first -- of those looked at.** A turn looks at the next few groups of its walk, as many as the
    /// setting and the budget allow and no more than before, and of those it could end it ends the one that is
    /// farthest: by <see cref="CutGroupCandidate.NearestDistance"/>, the distance to the group's nearest bounds, so
    /// that one piece flown far does not put a group lying at the viewer's feet first. It is the farthest of the
    /// groups THIS TURN LOOKED AT, not of all groups: no turn reads every group, and nothing is sorted. The one
    /// chosen is ended in the same turn, after everything is asked of it again; nothing was simulated, cut or
    /// collected in between. No choice is kept from one turn to another: a group that could have been ended and was
    /// left for a farther one is looked at again, whole and with the view as it then is, when the walk comes round to
    /// it. The walk does not go back to it at once -- the next turn looks at the groups that follow, so that the
    /// near groups a turn left do not fill the next turn's looking.
    /// </para>
    /// <para>
    /// **Looking costs too.** A group is read only when the frame's budget has the reserve and twice what reading and
    /// retiring a group of its size are expected to cost, asked from its count before anything of it is read; the
    /// walk begins and goes on by the same budget.
    /// </para>
    /// <para>
    /// **One boundary.** The pieces are retired one after the other by the existing single retirement -- the ledger,
    /// the owner, the DAG -- inside one call after the frame's collections, and what they share ends in that same
    /// call: nothing is accepted, resolved, simulated or collected in between, so no reader meets a group half ended.
    /// Right before, the family's revision is the one the group was chosen at and everything is asked again. Nothing
    /// is made for it: no operation, no pending cut, no transaction, no generation.
    /// </para>
    /// <para>
    /// **Coming back.** The original returns only once the display has adopted a collection that draws nothing of the
    /// group -- never beside its old pieces -- with what it had switched on when it left. It is made a cut target again
    /// in a later frame that has room, through the uncut entrance: its next hit gives it a new fragment.
    /// </para>
    /// <para>
    /// **When.** One turn a frame BEFORE the frame's collection (in the world's LateUpdate, where the per-piece
    /// lifetime steps when this one is off), by the gates the empty owners' turn uses: a retirement is reflected by the
    /// collection of the very frame the gate chose -- the structural collection it causes is part of what that frame
    /// is asked to bear, not an ungated next frame's (TL, 2026-10-10). An empty owner retired after a collection
    /// changes the ledger, which holds the next turn here. What a retirement is expected to cost is this group's: the
    /// measured cost a piece times its pieces, a group's own, and the structural collection that follows.
    /// </para>
    /// </summary>
    public sealed class CutFragmentGroupCollection
    {

        // The clock reading that begins an observed span: nothing in a Release Player (DESIGN 21.18).
        private static long Began()
        {
#if ZANTETSU_OBSERVE
            return Stopwatch.GetTimestamp();
#else
            return 0;
#endif
        }
        private static readonly ProfilerMarker s_retire = new ProfilerMarker("Zantetsu.FragmentGroups.Retire");
        private static readonly ProfilerMarker s_return = new ProfilerMarker("Zantetsu.FragmentGroups.Return");

        private sealed class Group
        {
            internal LogicalFragmentId root;
            internal ICutGroupOrigin origin;
            internal CutGroupKind kind;
            internal int listIndex;
            internal bool ended;          // its pieces were retired here; the original is on its way back
            internal bool returned;       // the original stands again, not yet a cut target
            internal long adoptedAtEnd;   // the display's adopted collections when the pieces were retired
        }

        private readonly CutWorldRoot _world;
        private readonly Dictionary<LogicalFragmentId, Group> _byRoot = new Dictionary<LogicalFragmentId, Group>();
        private readonly List<Group> _groups = new List<Group>();
        private readonly List<Group> _comingBack = new List<Group>();
        private readonly List<LogicalFragmentId> _pieces = new List<LogicalFragmentId>(64);
        private readonly Plane[] _eyePlanes = new Plane[2 * VpInstanceCulling.EyePlaneCount];
        private readonly SimulateCostHistory _perFragment = new SimulateCostHistory();
        private readonly SimulateCostHistory _examinePerFragment = new SimulateCostHistory();
        private readonly SimulateCostHistory _collectAfter = new SimulateCostHistory();   // the structural collection a retirement causes, in the same frame
        private readonly List<LogicalFragmentId> _drawnFragments = new List<LogicalFragmentId>(32);
        private readonly List<Bounds> _drawnBounds = new List<Bounds>(32);
        private readonly int[] _holds = new int[(int)CutGroupHold.Count];
        private readonly int[] _passes = new int[(int)CutGroupPass.Count];
        private readonly int[] _refusals = new int[(int)CutGroupPass.Count];
        private int _cursor;
        private long _revisionSeen = -1;
        private long _roomSeen = -1;
        private int _collectAfterFrame = int.MinValue;

        internal CutFragmentGroupCollection(CutWorldRoot world, in CutFragmentGroupSettings settings)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            Settings = settings;
            RemainingMainSeconds = () => CutPhysicsStep.FrameRemainingMainSeconds;
        }

        public CutFragmentGroupSettings Settings { get; set; }

        /// <summary>What is left of this frame's Main budget. The product's frame clock; a test gives its own.</summary>
        public Func<double> RemainingMainSeconds { get; set; }

        /// <summary>The camera whose eyes and position the groups are judged against; null means <c>Camera.main</c>.</summary>
        public Camera ViewCamera { get; set; }

        /// <summary>Groups known here now, and of them those whose original is on its way back.</summary>
        public int GroupCount => _groups.Count;
        public int ComingBack => _comingBack.Count;

        public int Turns { get; private set; }
        public int WalkedTurns { get; private set; }
        public int Examined { get; private set; }
        public int Candidates { get; private set; }

        /// <summary>Groups ended, the pieces retired with them, and of the groups those of each kind.</summary>
        public int GroupsRetired { get; private set; }
        public int FragmentsRetired { get; private set; }
        public int NpcsRetired { get; private set; }
        public int PropsRetired { get; private set; }
        public int BuildingsRetired { get; private set; }

        /// <summary>Originals that stand again, those that are cut targets again, and those that could not come back.</summary>
        public int Returned { get; private set; }
        public int Rearmed { get; private set; }
        public int ReturnsFailed { get; private set; }

        /// <summary>Candidates not applied: what stood there at the application was not what was chosen.</summary>
        public int Refused { get; private set; }

        /// <summary>Main time: looking at groups, the retirements (the shared ends in them), the returns and the re-arming, each in all and its longest.</summary>
        public double SelectSeconds { get; private set; }
        public double RetireSeconds { get; private set; }
        public double MaxRetireSeconds { get; private set; }
        public double ReturnSeconds { get; private set; }
        public double MaxReturnSeconds { get; private set; }
        public double RearmSeconds { get; private set; }
        public double MaxRearmSeconds { get; private set; }

        /// <summary>
        /// The collection that reflected a retirement -- the same frame's, after this turn (the display's structural
        /// update for it), as <see cref="CutPhysicsStep.LastCollectSeconds"/> gave it when the next turn read it: how
        /// many were read, their sum and the longest.
        /// </summary>
        public int CollectionsAfterRetirement { get; private set; }
        public double CollectAfterSeconds { get; private set; }
        public double MaxCollectAfterSeconds { get; private set; }

        public int LastRetiredFrame { get; private set; } = int.MinValue;
        public int LastRetiredFragments { get; private set; }

        /// <summary>How far the group last ended was when it was chosen (<see cref="CutGroupCandidate.NearestDistance"/>), in metres; infinity where it was chosen without a view.</summary>
        public float LastRetiredDistance { get; private set; } = float.PositiveInfinity;
        public double LastRetireSeconds { get; private set; }

        /// <summary>The root of the group being retired, while its pieces' owners retire (for a reader of <c>OwnerRetiring</c>); unset otherwise.</summary>
        public LogicalFragmentId RetiringRoot { get; private set; }

        public CutGroupHold LastHold { get; private set; }

        public int HeldFor(CutGroupHold hold) => _holds[(int)hold];

        public int PassedAs(CutGroupPass pass) => _passes[(int)pass];

        public int RefusedAs(CutGroupPass pass) => _refusals[(int)pass];

        /// <summary>
        /// A group from here on: <paramref name="root"/> and everything cut from it, with what it came from --
        /// <paramref name="origin"/> for a building or a prop, null for an NPC individual. Said once, when its first
        /// cut is accepted, by whoever knows; a root already known keeps what it was given.
        /// </summary>
        public void Register(LogicalFragmentId root, ICutGroupOrigin origin)
        {
            if (!root.IsSet || _byRoot.ContainsKey(root))
            {
                return;
            }

            var group = new Group { root = root, origin = origin, kind = origin != null ? origin.Kind : CutGroupKind.Npc, listIndex = _groups.Count };
            _byRoot.Add(root, group);
            _groups.Add(group);
        }

        /// <summary>Forgets a group whose origin is going away (its registrar destroyed). Its pieces live on as they are.</summary>
        public void Forget(LogicalFragmentId root)
        {
            if (_byRoot.TryGetValue(root, out Group group))
            {
                Remove(group);
                _comingBack.Remove(group);
            }
        }

        public bool IsKnown(LogicalFragmentId root) => _byRoot.ContainsKey(root);

        /// <summary>The seed of what the structural collection after a retirement is expected to cost, in seconds, before any was measured (a Quest 3S walk's median was 0.9 ms).</summary>
        public const double SeedCollectAfterSeconds = 0.001;

        /// <summary>
        /// What retiring a group of <paramref name="fragments"/> pieces is expected to cost this frame, in seconds: a
        /// piece's measured cost times the pieces, a group's own, and the structural collection of this frame that the
        /// retirement causes (the median of the last five measured, the seed before any).
        /// </summary>
        public double ExpectedSeconds(int fragments)
        {
            // A piece's cost is the measured time of a retirement over its pieces (what the group shares is in it), the
            // median of the last five; the group's own part stays the seed, as a margin for a group of few pieces.
            double perFragment = _perFragment.Count == 0 ? Settings.seedSecondsPerFragment : _perFragment.ExpectedSeconds;
            return (perFragment * fragments) + Settings.seedSecondsPerGroup + ExpectedCollectAfterSeconds;
        }

        /// <summary>What the structural collection that follows a retirement is expected to cost, in seconds.</summary>
        public double ExpectedCollectAfterSeconds => _collectAfter.Count == 0 ? SeedCollectAfterSeconds : _collectAfter.ExpectedSeconds;

        /// <summary>One frame's turn: see the class notes.</summary>
        public void Step()
        {
            // What a retirement started is carried on whatever the settings and the gates say now: the collection that
            // followed it is read, and an original whose pieces the display has let go stands again.
            FollowRetirement();
            bool worldOpen = _world.IsReady && !_world.IsEnding && !_world.TerminationRequested;
            if (worldOpen)
            {
                ReturnOriginals();
            }

            // Two things apart: whether new collections start (the setting), and whether what a collection started is
            // finished -- an original that stands again is made a cut target again whatever the setting is now.
            CutFragmentGroupSettings settings = Settings;
            bool starting = settings.enabled && settings.IsValid;
            bool finishing = AnyAwaitsRearm();
            if (!worldOpen || (!starting && !finishing))
            {
                return;
            }

            Turns++;
            CutGroupHold hold = HoldNow(in settings, starting, finishing);
            LastHold = hold;
            if (hold != CutGroupHold.None)
            {
                _holds[(int)hold]++;
                Remember();
                return;
            }

            // What this turn saw before its own work: its retirement is the next collection's structural change, and
            // that frame starts nothing beside it.
            Remember();
            WalkedTurns++;

            // A returned original is made a cut target again first, one a turn: it is what was started.
            if (TryRearmOne() || !starting || _world.Ledger.LiveCutFragmentCount <= settings.maxLiveCutFragments)
            {
                return;
            }

            long begin = Stopwatch.GetTimestamp();
            Camera camera = ViewCamera != null ? ViewCamera : Camera.main;
            int eyes = camera != null ? VpInstanceCulling.GetEyePlanes(camera, _eyePlanes) : 0;
            Vector3 viewer = camera != null ? camera.transform.position : default;
            int examined = 0, found = 0;
            CutGroupCandidate best = default;
            while (examined < settings.examinePerFrame && examined < _groups.Count)
            {
                // Looking at a group costs too: the walk begins, and goes on, only while the budget has the reserve and
                // twice what looking at the smallest group is expected to cost -- and, with a group to end in hand,
                // twice what ending it is expected to cost besides. What a group itself needs is asked of it before
                // anything of it is read (Examine).
                double inHand = best.IsSet ? 2.0 * ExpectedSeconds(best.Fragments) : 0.0;
                if (RemainingMainSeconds() < settings.reserveSeconds + (2.0 * ExpectedExamineSeconds(1)) + inHand)
                {
                    if (examined == 0)
                    {
                        LastHold = CutGroupHold.BudgetShort;
                        _holds[(int)CutGroupHold.BudgetShort]++;
                    }

                    break;
                }

                if (_cursor >= _groups.Count)
                {
                    _cursor = 0;
                }

                Group group = _groups[_cursor++];
                examined++;
                Examined++;
                CutGroupPass pass = Examine(group, true, viewer, eyes, in settings, true, inHand, out CutGroupCandidate candidate);
                if (pass != CutGroupPass.Candidate)
                {
                    _passes[(int)pass]++;
                    continue;
                }

                // One that could be ended. The farthest of this turn's is the one ended; the others are left.
                Candidates++;
                found++;
                if (!best.IsSet || candidate.NearestDistance > best.NearestDistance)
                {
                    best = candidate;
                }
            }

#if ZANTETSU_OBSERVE
            double chose = Since(begin);
            SelectSeconds += chose;
            MaxSelectSeconds = Math.Max(MaxSelectSeconds, chose);
#endif
            if (!best.IsSet)
            {
                return;
            }

            // The one chosen is ended now, in this same turn: nothing was simulated, cut or collected since it was
            // looked at, and TryApply asks everything of it again. What ending it needs is asked of what is left of
            // the budget now, after the looking. The others are left where they are: the walk goes on from here, and
            // reads each of them again when it comes round.
            bool retired = false;
            if (RemainingMainSeconds() < settings.reserveSeconds + (2.0 * ExpectedSeconds(best.Fragments)))
            {
                _passes[(int)CutGroupPass.BudgetShort]++;
            }
            else
            {
                retired = TryApply(in best, out _);
            }

            CandidatesLeft += retired ? found - 1 : found;
        }

        /// <summary>
        /// Groups a turn could have ended and did not: it ended a farther one of those it looked at, or what was left
        /// of the budget after the looking did not carry the one chosen. Each is looked at again when the walk comes
        /// round to it.
        /// </summary>
        public int CandidatesLeft { get; private set; }

        /// <summary>The seeds of what looking at a group is expected to cost, in seconds: a piece of it, and the group besides. From no Player measurement.</summary>
        public const double SeedExamineSecondsPerFragment = 0.00002;
        public const double SeedExamineSecondsPerGroup = 0.0001;

        /// <summary>The longest looking at groups in one turn, in seconds: every group the turn looked at, before it ended one.</summary>
        public double MaxSelectSeconds { get; private set; }

        /// <summary>
        /// What looking at a group of <paramref name="fragments"/> pieces is expected to cost, in seconds: a piece's
        /// measured share of a group read whole (the median of the last five), times the pieces, and the group's own.
        /// </summary>
        public double ExpectedExamineSeconds(int fragments)
        {
            double perFragment = _examinePerFragment.Count == 0 ? SeedExamineSecondsPerFragment : _examinePerFragment.ExpectedSeconds;
            return (perFragment * fragments) + SeedExamineSecondsPerGroup;
        }

        /// <summary>Looks at one group as the walk does, without the view and the budget: for a caller that chooses now and applies later.</summary>
        public bool TryChoose(LogicalFragmentId root, out CutGroupCandidate candidate)
        {
            candidate = default;
            return _byRoot.TryGetValue(root, out Group group)
                && Examine(group, false, default, 0, Settings, false, 0.0, out candidate) == CutGroupPass.Candidate;
        }

        /// <summary>
        /// Ends one chosen group whole (see the class notes), after asking again everything it was chosen for and that
        /// its family has not changed since. False, changing nothing, with what it is now, otherwise.
        /// </summary>
        public bool TryApply(in CutGroupCandidate candidate, out CutGroupPass refusal)
        {
            // The asking again is part of what a retirement costs: timed with it (the per-fragment cost history reads it).
            long begin = Stopwatch.GetTimestamp();
            refusal = CutGroupPass.Candidate;
            Group group = null;
            if (!candidate.IsSet || !_world.IsReady || _world.IsEnding || _world.TerminationRequested
                || !_byRoot.TryGetValue(candidate.Root, out group))
            {
                refusal = CutGroupPass.NothingToEnd;
            }
            else if (_world.Ledger.FamilyRevisionOf(candidate.Root) != candidate.Revision)
            {
                refusal = CutGroupPass.Changed;
            }
            else
            {
                refusal = Examine(group, false, default, 0, Settings, false, 0.0, out _);
            }

            if (refusal != CutGroupPass.Candidate)
            {
                Refused++;
                _refusals[(int)refusal]++;
                return false;
            }

            int retired = 0;
            using (s_retire.Auto())
            {
                LogicalCutLedger ledger = _world.Ledger;
                PhysicsOwnerRegistry owners = _world.Owners;
                _pieces.Clear();
                ledger.CopyLiveCutFragmentsOf(group.root, _pieces);
                RetiringRoot = group.root;
                try
                {
                    // Every piece, one after the other, with nothing between them: the ledger first (no target, and
                    // whatever arrives for it later is stale), its owner out of the scene, the DAG's record of it.
                    for (int i = 0; i < _pieces.Count; i++)
                    {
                        LogicalFragmentId piece = _pieces[i];
                        if (!ledger.Retire(piece))
                        {
                            continue;
                        }

                        owners.Retire(piece);
                        _world.Geometry?.Forget(piece);
                        retired++;
                    }

                    // And what they share, in the same call.
                    group.origin?.EndShared("its group was collected");
                }
                finally
                {
                    RetiringRoot = default;
                }
            }

            double spent = Since(begin);   // the retirement's cost: _perFragment below is the budget's expectation (kept in every build)
#if ZANTETSU_OBSERVE
            RetireSeconds += spent;
            MaxRetireSeconds = Math.Max(MaxRetireSeconds, spent);
            LastRetireSeconds = spent;
#endif
            LastRetiredFrame = Time.frameCount;
            LastRetiredFragments = retired;
            LastRetiredDistance = candidate.NearestDistance;
            _collectAfterFrame = Time.frameCount + 1;   // this frame's collection follows this turn; its time is read by the next turn
            GroupsRetired++;
            FragmentsRetired += retired;
            if (retired > 0)
            {
                _perFragment.Add(spent / retired);
            }

            switch (group.kind)
            {
                case CutGroupKind.Npc: NpcsRetired++; break;
                case CutGroupKind.Prop: PropsRetired++; break;
                default: BuildingsRetired++; break;
            }

            // Which, how many and when -- what a look at the run is read against. Kept, and said at the world's end.
            Note(EventKind.Collected, group, true, retired, spent, candidate.NearestDistance);

            if (group.origin == null)
            {
                Remove(group);
            }
            else
            {
                group.ended = true;
                group.adoptedAtEnd = _world.Display != null ? _world.Display.AdoptedCollections : 0;
                _comingBack.Add(group);
            }

            return true;
        }

        /// <summary>The collection's record, in words. Log text only.</summary>
        public string Describe()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var text = new System.Text.StringBuilder(768);
            text.Append("fragment group collection ").Append(Settings.enabled ? "on" : "off")
                .Append(" (more than ").Append(Settings.maxLiveCutFragments).Append(" live cut fragments; outside both eyes at any distance, the farthest of the ")
                .Append(Settings.examinePerFrame).Append(" looked at a turn first): live cut fragments now ").Append(_world.Ledger != null ? _world.Ledger.LiveCutFragmentCount : 0)
                .Append(", groups known ").Append(_groups.Count)
                .Append("; turns ").Append(Turns).Append(" (walked ").Append(WalkedTurns).Append("), groups looked at ").Append(Examined)
                .Append(", candidates ").Append(Candidates).Append(" (left for a farther one or for the budget ").Append(CandidatesLeft)
                .Append("), groups retired ").Append(GroupsRetired).Append(" (NPC ").Append(NpcsRetired)
                .Append(", prop ").Append(PropsRetired).Append(", building ").Append(BuildingsRetired).Append(") with ").Append(FragmentsRetired)
                .Append(" fragments, candidates not applied ").Append(Refused)
                .Append("; originals returned ").Append(Returned).Append(", cut targets again ").Append(Rearmed).Append(", not returned ").Append(ReturnsFailed)
                .Append(", on their way back ").Append(_comingBack.Count);
            text.Append("; held:");
            for (int h = 1; h < (int)CutGroupHold.Count; h++)
            {
                text.Append(' ').Append(((CutGroupHold)h).ToString()).Append(' ').Append(_holds[h]);
            }

            text.Append("; passed over:");
            for (int p = 1; p < (int)CutGroupPass.Count; p++)
            {
                text.Append(' ').Append(((CutGroupPass)p).ToString()).Append(' ').Append(_passes[p]);
                if (_refusals[p] > 0)
                {
                    text.Append(" (+").Append(_refusals[p]).Append(" at the application)");
                }
            }

            text.Append("; time: choosing ").Append((SelectSeconds * 1000.0).ToString("F3", inv)).Append(" ms (longest in a turn ")
                .Append((MaxSelectSeconds * 1000.0).ToString("F3", inv)).Append(", expected a group of one ")
                .Append((ExpectedExamineSeconds(1) * 1000.0).ToString("F3", inv)).Append(", expected collection after ")
                .Append((ExpectedCollectAfterSeconds * 1000.0).ToString("F3", inv)).Append("), retirements ").Append((RetireSeconds * 1000.0).ToString("F3", inv)).Append(" ms (longest ")
                .Append((MaxRetireSeconds * 1000.0).ToString("F3", inv)).Append("), returns ").Append((ReturnSeconds * 1000.0).ToString("F3", inv))
                .Append(" ms (longest ").Append((MaxReturnSeconds * 1000.0).ToString("F3", inv)).Append("), re-arming ")
                .Append((RearmSeconds * 1000.0).ToString("F3", inv)).Append(" ms (longest ").Append((MaxRearmSeconds * 1000.0).ToString("F3", inv))
                .Append("); the collections after a retirement: ").Append(CollectionsAfterRetirement).Append(", ")
                .Append((CollectAfterSeconds * 1000.0).ToString("F3", inv)).Append(" ms (longest ").Append((MaxCollectAfterSeconds * 1000.0).ToString("F3", inv))
                .Append("); the last retirement at frame ").Append(LastRetiredFrame).Append(": ").Append(LastRetiredFragments).Append(" fragments, ")
                .Append((LastRetireSeconds * 1000.0).ToString("F3", inv)).Append(" ms");

            // The room: what a retirement gives back later (vertex groups, index ranges) and what it does not (the
            // display's command slots, taken at the end by every re-cut and never moved down).
            if (_world.Storage != null && _world.Geometry != null && _world.Display != null)
            {
                text.Append("; room: vertex groups released ").Append(_world.Storage.VertexGroupsReleased).Append(" (").Append(_world.Storage.VerticesReleased)
                    .Append(" vertices), waiting ").Append(_world.Geometry.VertexGroupsWaiting).Append(", free vertex room ").Append(_world.Storage.FreeVertexRoom)
                    .Append(", free index room ").Append(_world.Storage.FreeIndexRoom)
                    .Append("; display command slots taken ").Append(_world.Display.DrawCommandEnd).Append(" (drawing ").Append(_world.Display.CommandCount)
                    .Append(") of ").Append(_world.Display.CommandCapacity);
            }

            return text.ToString();
        }

        private CutGroupHold HoldNow(in CutFragmentGroupSettings settings, bool starting, bool finishing)
        {
            switch (_world.FrameEventsHold(out _))
            {
                case CutFrameHold.HeavyWork:
                    return CutGroupHold.HeavyWork;
                case CutFrameHold.GarbageCollection:
                    return CutGroupHold.GarbageCollection;
                case CutFrameHold.CutInFlight:
                    return CutGroupHold.CutInFlight;
            }

            if (_world.Ledger.Revision != _revisionSeen)
            {
                return CutGroupHold.StructureChanged;
            }

            if (_world.DisplayRoomSerial() != _roomSeen)
            {
                return CutGroupHold.DisplayRoomGrown;
            }

            // (Before the frame's collection, this frame's compaction has not run; a retirement here makes this frame's
            // collection structural, which does not compact. Kept for a turn taken after a collection, as a test may.)
            if (_world.Display != null && _world.Display.LastCompactionFrame == Time.frameCount)
            {
                return CutGroupHold.CompactionRan;
            }

            if (_world.EmptyOwners != null && _world.EmptyOwners.LastRetiredFrame == Time.frameCount)
            {
                return CutGroupHold.EmptyOwnerRetired;
            }

            // Nothing to start: no group is looked at. An original made a cut target again is what was started, and
            // goes on in such a frame -- and with the setting off.
            if (!finishing && (!starting || _world.Ledger.LiveCutFragmentCount <= settings.maxLiveCutFragments))
            {
                return CutGroupHold.UnderTheMaximum;
            }

            return CutGroupHold.None;
        }

        private void Remember()
        {
            _revisionSeen = _world.Ledger.Revision;
            _roomSeen = _world.DisplayRoomSerial();
        }

        private CutGroupPass Examine(
            Group group, bool judgeView, Vector3 viewer, int eyes, in CutFragmentGroupSettings settings, bool judgeBudget,
            double inHandSeconds, out CutGroupCandidate candidate)
        {
            candidate = default;
            LogicalCutLedger ledger = _world.Ledger;
            PhysicsOwnerRegistry owners = _world.Owners;
            CutDag dag = _world.Geometry;
            int count = ledger.LiveCutCountOf(group.root);
            if (group.ended || count == 0)
            {
                return CutGroupPass.NothingToEnd;
            }

            if (judgeBudget && ledger.LiveCutFragmentCount <= settings.maxLiveCutFragments)
            {
                return CutGroupPass.NothingToEnd;
            }

            // The budget first, from the count alone and before anything of the group is read: looking at it and
            // retiring it are both this frame's, and a group that does not fit is passed over unread. With a group to
            // end in hand already (inHandSeconds, twice what ending it is expected to cost), one of the two is ended:
            // the budget must carry the looking and the dearer of the two.
            if (judgeBudget
                && RemainingMainSeconds() < settings.reserveSeconds + (2.0 * ExpectedExamineSeconds(count)) + Math.Max(2.0 * ExpectedSeconds(count), inHandSeconds))
            {
                return CutGroupPass.BudgetShort;
            }

            long examineBegan = Stopwatch.GetTimestamp();
            _pieces.Clear();
            ledger.CopyLiveCutFragmentsOf(group.root, _pieces);

            // How far the group is: the nearest of every bounds judged below. And whether anything was judged at all.
            float nearestSquared = float.PositiveInfinity;
            bool judged = false;
            if (judgeView)
            {
                // What the display draws of the group now: every registration of the family, with its geometry's bounds.
                _drawnFragments.Clear();
                _drawnBounds.Clear();
                _world.Display?.CopyRegistrationsOf(group.root, _drawnFragments, _drawnBounds);
            }

            for (int i = 0; i < _pieces.Count; i++)
            {
                LogicalFragmentId piece = _pieces[i];
                if (ledger.TryGetActiveOperation(piece, out _) || owners.TryGetProvisionalOf(piece, out _))
                {
                    return CutGroupPass.CutUnderWay;
                }

                if (dag == null || (!dag.HasNoGeometry(piece) && !dag.TryGetGeometry(piece, out _)))
                {
                    return CutGroupPass.GeometryOpen;
                }

                if (!owners.TryGet(piece, out PhysicsFragmentOwner owner) || owner.IsWithdrawn || owner.IsReleased || owner.IsFused || owner.Root == null)
                {
                    return CutGroupPass.NoOwner;
                }

                if (!judgeView)
                {
                    continue;
                }

                // Its physics shape where its owner stands, when it has one of its own.
                if (owner.Shape != null && !owner.Shape.IsFreed && owner.Shape.TryLocalBounds(out float3 lo, out float3 hi))
                {
                    Bounds world = WorldBounds(owner.Root.transform.localToWorldMatrix * (Matrix4x4)owner.Shape.LocalToOwner, lo, hi);
                    CutGroupPass seen = JudgeBounds(world, viewer, eyes, ref nearestSquared);
                    if (seen != CutGroupPass.Candidate)
                    {
                        return seen;
                    }

                    judged = true;
                }

                // And what is drawn of it, where it is drawn now: the geometry of the registration it is drawn from --
                // its own, or the nearest above it -- at this piece's present placement. A building's display piece has
                // no shape of its own and moves apart from its group's hull (a drop): this is what places it. A piece
                // with geometry whose drawing cannot be placed is not judged, and the group is not chosen.
                if (!dag.HasNoGeometry(piece))
                {
                    if (!TryGetDrawnBounds(ledger, piece, owner, out Bounds drawn))
                    {
                        return CutGroupPass.NotJudged;
                    }

                    CutGroupPass seen = JudgeBounds(drawn, viewer, eyes, ref nearestSquared);
                    if (seen != CutGroupPass.Candidate)
                    {
                        return seen;
                    }

                    judged = true;
                }
            }

            ICutGroupOrigin origin = group.origin;
            if (origin != null)
            {
                if (!origin.MayEndNow(out _))
                {
                    return CutGroupPass.SharedBusy;
                }

                if (judgeView)
                {
                    if (!origin.TryGetRestoreBounds(out Bounds back))
                    {
                        return CutGroupPass.NotJudged;
                    }

                    // Where the original would stand again, and what the pieces share: outside both eyes too, and
                    // part of how far the group is.
                    CutGroupPass seen = JudgeBounds(back, viewer, eyes, ref nearestSquared);
                    if (seen == CutGroupPass.Candidate && origin.TryGetSharedBounds(out Bounds shared))
                    {
                        seen = JudgeBounds(shared, viewer, eyes, ref nearestSquared);
                    }

                    if (seen != CutGroupPass.Candidate)
                    {
                        return seen;
                    }

                    judged = true;
                }
            }

            // Nothing of it to judge against the view (no shape, nothing drawn, no original): not chosen by a walk.
            if (judgeView && !judged)
            {
                return CutGroupPass.NotJudged;
            }

            if (judgeView && _pieces.Count > 0)
            {
                // A group read whole, with the view: a sample of what looking at one costs, a piece.
                _examinePerFragment.Add(Since(examineBegan) / _pieces.Count);
            }

            // What reading it took is gone from the budget: the retirement is asked again with what is left now.
            if (judgeBudget && RemainingMainSeconds() < settings.reserveSeconds + (2.0 * ExpectedSeconds(_pieces.Count)))
            {
                return CutGroupPass.BudgetShort;
            }

            candidate = new CutGroupCandidate(
                group.root, ledger.FamilyRevisionOf(group.root), _pieces.Count, judgeView ? Mathf.Sqrt(nearestSquared) : float.PositiveInfinity);
            return CutGroupPass.Candidate;
        }

        // The world bounds of what is drawn of one piece: the geometry bounds of the registration it is drawn from (the
        // family's registrations were copied by the caller), placed by the piece's own owner as the display places it.
        private bool TryGetDrawnBounds(LogicalCutLedger ledger, LogicalFragmentId piece, PhysicsFragmentOwner owner, out Bounds world)
        {
            world = default;
            if (!owner.TryReadGeometryLocalToWorld(out Matrix4x4 placement))
            {
                return false;
            }

            LogicalFragmentId at = piece;
            for (int step = 0; step <= 256; step++)
            {
                int found = _drawnFragments.IndexOf(at);
                if (found >= 0)
                {
                    Bounds local = _drawnBounds[found];
                    world = WorldBounds(placement, (float3)local.min, (float3)local.max);
                    return true;
                }

                // Up the object's ancestry: the fragment the display still draws may be one a split replaced.
                if (!ledger.TryGetParent(at, out LogicalFragmentId parent))
                {
                    return false;
                }

                at = parent;
            }

            return false;
        }

        // With a view: in an eye's view, or outside both -- and then how far, kept as the nearest so far. No distance
        // is asked of it. Without a view nothing is judged, and a walk chooses nothing.
        private CutGroupPass JudgeBounds(Bounds world, Vector3 viewer, int eyes, ref float nearestSquared)
        {
            if (eyes <= 0)
            {
                return CutGroupPass.NotJudged;
            }

            if (VpInstanceCulling.MayIntersectAnyEye(world, _eyePlanes, eyes))
            {
                return CutGroupPass.Seen;
            }

            nearestSquared = Mathf.Min(nearestSquared, world.SqrDistance(viewer));
            return CutGroupPass.Candidate;
        }

        // The collection that reflected a retirement (the retirement's own frame, after its turn): its time is read
        // here by the next turn, once, and goes into what the next retirement is expected to cost.
        private void FollowRetirement()
        {
            if (_collectAfterFrame == Time.frameCount)
            {
                double seconds = CutPhysicsStep.LastCollectSeconds;
                CollectionsAfterRetirement++;
#if ZANTETSU_OBSERVE
                CollectAfterSeconds += seconds;
                MaxCollectAfterSeconds = Math.Max(MaxCollectAfterSeconds, seconds);
#endif
                _collectAfter.Add(seconds);
                _collectAfterFrame = int.MinValue;
            }
        }

        // An ended group's original stands again once the display has adopted a collection since and holds nothing of
        // the family -- never beside the old pieces. One a turn; no gate holds it back: it was started.
        private void ReturnOriginals()
        {
            VpLogicalCutDisplay display = _world.Display;
            for (int i = 0; i < _comingBack.Count; i++)
            {
                Group group = _comingBack[i];
                if (group.returned)
                {
                    continue;
                }

                if (display != null && (display.IsHalted || display.AdoptedCollections <= group.adoptedAtEnd || display.HoldsAnythingOf(group.root)))
                {
                    continue;
                }

                long begin = Began();
                bool back;
                using (s_return.Auto())
                {
                    back = group.origin.TryReturn();
                }

#if ZANTETSU_OBSERVE
                double spent = Since(begin);
                ReturnSeconds += spent;
                MaxReturnSeconds = Math.Max(MaxReturnSeconds, spent);
                Note(EventKind.Returned, group, back, 0, spent);
#endif
                if (back)
                {
                    group.returned = true;
                    Returned++;
                }
                else
                {
                    ReturnsFailed++;
                    _comingBack.RemoveAt(i);
                    Remove(group);
                }

                return;
            }
        }

        private bool AnyAwaitsRearm()
        {
            for (int i = 0; i < _comingBack.Count; i++)
            {
                if (_comingBack[i].returned)
                {
                    return true;
                }
            }

            return false;
        }

        // A returned original made a cut target again: the uncut entrance's own preparation, in a frame the gates
        // allow and whose budget has the reserve left. The group is forgotten then: its next cut is a new one.
        private bool TryRearmOne()
        {
            for (int i = 0; i < _comingBack.Count; i++)
            {
                Group group = _comingBack[i];
                if (!group.returned)
                {
                    continue;
                }

                if (RemainingMainSeconds() < Settings.reserveSeconds + (2.0 * MaxRearmSeconds))
                {
                    return false;
                }

                long begin = Began();
                bool armed = group.origin.TryRearm();
#if ZANTETSU_OBSERVE
                double spent = Since(begin);
                RearmSeconds += spent;
                MaxRearmSeconds = Math.Max(MaxRearmSeconds, spent);
                Note(EventKind.Rearmed, group, armed, 0, spent);
#endif
                if (armed)
                {
                    Rearmed++;
                }
                else
                {
                    ReturnsFailed++;
                }

                _comingBack.RemoveAt(i);
                Remove(group);
                return true;
            }

            return false;
        }

        // What happened to a group, kept as numbers where it happened and written out by LogEvents: a line written in
        // the frame of a retirement would be part of what that frame cost.
        private enum EventKind : byte { Collected, Returned, Rearmed }

        private struct GroupEvent
        {
            internal EventKind what;
            internal CutGroupKind kind;
            internal bool ok, hasOrigin;
            internal int root, frame, fragments, liveAfter;
            internal double seconds;
            internal float nearest;   // a collection's: how far the group was (its nearest bounds), in metres
        }

        /// <summary>How many events are kept for <see cref="LogEvents"/>; past it they are counted only.</summary>
        public const int EventRoom = 4096;

        private readonly List<GroupEvent> _events = new List<GroupEvent>(1024);

        /// <summary>Events kept for <see cref="LogEvents"/>, and those past <see cref="EventRoom"/> that were not.</summary>
        public int EventsKept => _events.Count;
        public int EventsNotKept { get; private set; }

        private void Note(EventKind what, Group group, bool ok, int fragments, double seconds, float nearest = float.PositiveInfinity)
        {
#if !ZANTETSU_OBSERVE
            // A Release Player keeps no events (DESIGN 21.18): nothing is written at the world's end there.
            return;
#endif
            if (_events.Count >= EventRoom)
            {
                EventsNotKept++;
                return;
            }

            _events.Add(new GroupEvent
            {
                what = what, kind = group.kind, ok = ok, hasOrigin = group.origin != null, root = group.root.value, frame = Time.frameCount,
                fragments = fragments, liveAfter = _world.Ledger.LiveCutFragmentCount, seconds = seconds, nearest = nearest,
            });
        }

        /// <summary>
        /// One log line an event, in the order they happened: each group as it was collected, each original as it
        /// stood again and as it was made a cut target again, with its frame. Said by the world at its end, not in
        /// the frames the events happened in.
        /// </summary>
        public void LogEvents()
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (GroupEvent e in _events)
            {
                string of = "the " + e.kind + " group of fragment " + e.root;
                switch (e.what)
                {
                    case EventKind.Collected:
                        UnityEngine.Debug.Log("FRAGMENT GROUPS: collected " + of + ": " + e.fragments + " fragments at frame " + e.frame + " in " + (e.seconds * 1000.0).ToString("F3", inv)
                            + " ms" + (float.IsInfinity(e.nearest) ? "" : ", its nearest bounds " + e.nearest.ToString("F1", inv) + " m from the view")
                            + "; live cut fragments now " + e.liveAfter + (e.hasOrigin ? "; its original comes back once the display has let its pieces go" : ""));
                        break;
                    case EventKind.Returned:
                        UnityEngine.Debug.Log("FRAGMENT GROUPS: the original of " + of + (e.ok ? " stands again at frame " : " could NOT be brought back at frame ") + e.frame
                            + " (" + (e.seconds * 1000.0).ToString("F3", inv) + " ms)");
                        break;
                    default:
                        UnityEngine.Debug.Log("FRAGMENT GROUPS: the original of " + of + (e.ok ? " is a cut target again at frame " : " could NOT be made a cut target again at frame ") + e.frame
                            + " (" + (e.seconds * 1000.0).ToString("F3", inv) + " ms)");
                        break;
                }
            }

            if (EventsNotKept > 0)
            {
                UnityEngine.Debug.Log("FRAGMENT GROUPS: " + EventsNotKept + " more events were not kept (room for " + EventRoom + ")");
            }
        }

        private void Remove(Group group)
        {
            if (!_byRoot.Remove(group.root))
            {
                return;
            }

            int last = _groups.Count - 1;
            if (group.listIndex != last)
            {
                Group moved = _groups[last];
                _groups[group.listIndex] = moved;
                moved.listIndex = group.listIndex;
            }

            _groups.RemoveAt(last);
        }

        private static Bounds WorldBounds(Matrix4x4 localToWorld, float3 lo, float3 hi)
        {
            Vector3 centre = localToWorld.MultiplyPoint3x4((Vector3)((lo + hi) * 0.5f));
            Vector3 half = (Vector3)((hi - lo) * 0.5f);
            var extents = new Vector3(
                Mathf.Abs(localToWorld.m00) * half.x + Mathf.Abs(localToWorld.m01) * half.y + Mathf.Abs(localToWorld.m02) * half.z,
                Mathf.Abs(localToWorld.m10) * half.x + Mathf.Abs(localToWorld.m11) * half.y + Mathf.Abs(localToWorld.m12) * half.z,
                Mathf.Abs(localToWorld.m20) * half.x + Mathf.Abs(localToWorld.m21) * half.y + Mathf.Abs(localToWorld.m22) * half.z);
            return new Bounds(centre, extents * 2f);
        }

        private static double Since(long begin)
        {
            return (double)(Stopwatch.GetTimestamp() - begin) / Stopwatch.Frequency;
        }
    }
}
