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
            bool enabled, int maxLiveCutFragments, float minDistance, int examinePerFrame, double reserveSeconds,
            double seedSecondsPerFragment, double seedSecondsPerGroup)
        {
            this.enabled = enabled;
            this.maxLiveCutFragments = maxLiveCutFragments;
            this.minDistance = minDistance;
            this.examinePerFrame = examinePerFrame;
            this.reserveSeconds = reserveSeconds;
            this.seedSecondsPerFragment = seedSecondsPerFragment;
            this.seedSecondsPerGroup = seedSecondsPerGroup;
        }

        /// <summary>Off: no group is looked at and no retirement is started. What was started goes on regardless.</summary>
        public readonly bool enabled;

        /// <summary>Groups are looked at only while more live cut fragments than this stand. Where a collection starts, not a limit kept.</summary>
        public readonly int maxLiveCutFragments;

        /// <summary>Every bounds of a chosen group -- its pieces', what they share, the original's -- is at least this far, in metres, from the viewer.</summary>
        public readonly float minDistance;

        /// <summary>Groups looked at per frame, at most. One retirement a frame at most.</summary>
        public readonly int examinePerFrame;

        /// <summary>The Main budget a retirement must leave to the rest of the frame, in seconds, beyond twice its own expected cost.</summary>
        public readonly double reserveSeconds;

        /// <summary>The expected cost of retiring one piece of a group, and of a group besides its pieces, in seconds, before any was measured.</summary>
        public readonly double seedSecondsPerFragment;
        public readonly double seedSecondsPerGroup;

        public bool IsValid =>
            maxLiveCutFragments >= 0 && minDistance >= 0f && !float.IsNaN(minDistance) && !float.IsInfinity(minDistance)
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

        /// <summary>Some bounds of it -- a piece's, the shared one's, the original's -- is nearer than the distance.</summary>
        Near,

        /// <summary>Some bounds of it may be in an eye's view.</summary>
        Seen,

        /// <summary>There is no view to judge it against, or the original's bounds cannot be said.</summary>
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
        internal CutGroupCandidate(LogicalFragmentId root, long revision, int fragments)
        {
            Root = root;
            Revision = revision;
            Fragments = fragments;
        }

        public LogicalFragmentId Root { get; }

        internal long Revision { get; }

        public int Fragments { get; }

        public bool IsSet => Root.IsSet;
    }

    /// <summary>
    /// DESIGN 7.10's lifetime of live logical fragments by the object they came from (Phase 7.1): once more cut
    /// fragments stand than the setting, a whole group -- every piece of one building, one prop or one NPC individual --
    /// that is far from the viewer and outside both eyes is ended at one boundary, and a building or a prop is given
    /// back its own instance, to be cut again as an uncut one.
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
    /// the original would stand again -- at least the distance away and outside the eyes' views. An original that
    /// would come back near or in view is not chosen for pieces that flew away, nor a building for its hull when a
    /// piece of it lies near.
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
    /// **When.** One turn a frame after the collections, after the empty owners' turn, by the gates that turn uses and
    /// never in a frame that turn retired in. What a retirement is expected to cost is this group's: the measured cost
    /// a piece times its pieces, and a group's own.
    /// </para>
    /// </summary>
    public sealed class CutFragmentGroupCollection
    {
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
        /// The frame's collection that followed a retirement (the display's structural update for it), as
        /// <see cref="CutPhysicsStep.LastCollectSeconds"/> gave it: how many were read, their sum and the longest.
        /// </summary>
        public int CollectionsAfterRetirement { get; private set; }
        public double CollectAfterSeconds { get; private set; }
        public double MaxCollectAfterSeconds { get; private set; }

        public int LastRetiredFrame { get; private set; } = int.MinValue;
        public int LastRetiredFragments { get; private set; }
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

        /// <summary>What retiring a group of <paramref name="fragments"/> pieces is expected to cost, in seconds: a piece's measured cost times the pieces, and a group's own.</summary>
        public double ExpectedSeconds(int fragments)
        {
            // A piece's cost is the measured time of a retirement over its pieces (what the group shares is in it), the
            // median of the last five; the group's own part stays the seed, as a margin for a group of few pieces.
            double perFragment = _perFragment.Count == 0 ? Settings.seedSecondsPerFragment : _perFragment.ExpectedSeconds;
            return (perFragment * fragments) + Settings.seedSecondsPerGroup;
        }

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
            int examined = 0;
            bool retired = false;
            while (examined < settings.examinePerFrame && examined < _groups.Count && !retired)
            {
                // Looking at a group costs too: the walk begins, and goes on, only while the budget has the reserve and
                // twice what looking at the smallest group is expected to cost. What a group itself needs is asked of
                // it before anything of it is read (Examine).
                if (RemainingMainSeconds() < settings.reserveSeconds + (2.0 * ExpectedExamineSeconds(1)))
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
                CutGroupPass pass = Examine(group, true, viewer, eyes, in settings, true, out CutGroupCandidate candidate);
                if (pass != CutGroupPass.Candidate)
                {
                    _passes[(int)pass]++;
                    continue;
                }

                Candidates++;
                double chose = Since(begin);
                SelectSeconds += chose;
                MaxSelectSeconds = Math.Max(MaxSelectSeconds, chose);
                retired = TryApply(in candidate, out _);
                begin = Stopwatch.GetTimestamp();
            }

            double rest = Since(begin);
            SelectSeconds += rest;
            MaxSelectSeconds = Math.Max(MaxSelectSeconds, rest);
        }

        /// <summary>The seeds of what looking at a group is expected to cost, in seconds: a piece of it, and the group besides. From no Player measurement.</summary>
        public const double SeedExamineSecondsPerFragment = 0.00002;
        public const double SeedExamineSecondsPerGroup = 0.0001;

        /// <summary>The longest stretch of looking at groups in one turn, in seconds (up to a candidate, or the turn's end).</summary>
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

        /// <summary>Looks at one group as the walk does, without the view, the distance and the budget: for a caller that chooses now and applies later.</summary>
        public bool TryChoose(LogicalFragmentId root, out CutGroupCandidate candidate)
        {
            candidate = default;
            return _byRoot.TryGetValue(root, out Group group)
                && Examine(group, false, default, 0, Settings, false, out candidate) == CutGroupPass.Candidate;
        }

        /// <summary>
        /// Ends one chosen group whole (see the class notes), after asking again everything it was chosen for and that
        /// its family has not changed since. False, changing nothing, with what it is now, otherwise.
        /// </summary>
        public bool TryApply(in CutGroupCandidate candidate, out CutGroupPass refusal)
        {
            // The asking again is part of what a retirement costs: timed with it.
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
                refusal = Examine(group, false, default, 0, Settings, false, out _);
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

            double spent = Since(begin);
            RetireSeconds += spent;
            MaxRetireSeconds = Math.Max(MaxRetireSeconds, spent);
            LastRetireSeconds = spent;
            LastRetiredFrame = Time.frameCount;
            LastRetiredFragments = retired;
            _collectAfterFrame = Time.frameCount + 1;
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
            Note(EventKind.Collected, group, true, retired, spent);

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
                .Append(" (more than ").Append(Settings.maxLiveCutFragments).Append(" live cut fragments, ").Append(Settings.minDistance.ToString("F1", inv))
                .Append(" m): live cut fragments now ").Append(_world.Ledger != null ? _world.Ledger.LiveCutFragmentCount : 0)
                .Append(", groups known ").Append(_groups.Count)
                .Append("; turns ").Append(Turns).Append(" (walked ").Append(WalkedTurns).Append("), groups looked at ").Append(Examined)
                .Append(", candidates ").Append(Candidates).Append(", groups retired ").Append(GroupsRetired).Append(" (NPC ").Append(NpcsRetired)
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

            text.Append("; time: choosing ").Append((SelectSeconds * 1000.0).ToString("F3", inv)).Append(" ms (longest stretch ")
                .Append((MaxSelectSeconds * 1000.0).ToString("F3", inv)).Append(", expected a group of one ")
                .Append((ExpectedExamineSeconds(1) * 1000.0).ToString("F3", inv)).Append("), retirements ").Append((RetireSeconds * 1000.0).ToString("F3", inv)).Append(" ms (longest ")
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
            out CutGroupCandidate candidate)
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
            // retiring it are both this frame's, and a group that does not fit is passed over unread.
            if (judgeBudget
                && RemainingMainSeconds() < settings.reserveSeconds + (2.0 * (ExpectedExamineSeconds(count) + ExpectedSeconds(count))))
            {
                return CutGroupPass.BudgetShort;
            }

            long examineBegan = Stopwatch.GetTimestamp();
            _pieces.Clear();
            ledger.CopyLiveCutFragmentsOf(group.root, _pieces);
            float nearSquared = settings.minDistance * settings.minDistance;
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
                    CutGroupPass seen = JudgeBounds(world, viewer, eyes, nearSquared);
                    if (seen != CutGroupPass.Candidate)
                    {
                        return seen;
                    }
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

                    CutGroupPass seen = JudgeBounds(drawn, viewer, eyes, nearSquared);
                    if (seen != CutGroupPass.Candidate)
                    {
                        return seen;
                    }
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

                    CutGroupPass seen = JudgeBounds(back, viewer, eyes, nearSquared);
                    if (seen == CutGroupPass.Candidate && origin.TryGetSharedBounds(out Bounds shared))
                    {
                        seen = JudgeBounds(shared, viewer, eyes, nearSquared);
                    }

                    if (seen != CutGroupPass.Candidate)
                    {
                        return seen;
                    }
                }
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

            candidate = new CutGroupCandidate(group.root, ledger.FamilyRevisionOf(group.root), _pieces.Count);
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

                if (!ledger.TryGetOrigin(at, out CutOperationId origin, out _) || !ledger.TryGetOperation(origin, out LogicalCutOperation cut))
                {
                    return false;
                }

                at = cut.source;
            }

            return false;
        }

        // With a view: near, seen, or neither. A walk without a view chooses nothing (the caller says so before).
        private CutGroupPass JudgeBounds(Bounds world, Vector3 viewer, int eyes, float nearSquared)
        {
            if (eyes <= 0)
            {
                return CutGroupPass.NotJudged;
            }

            if (world.SqrDistance(viewer) < nearSquared)
            {
                return CutGroupPass.Near;
            }

            return VpInstanceCulling.MayIntersectAnyEye(world, _eyePlanes, eyes) ? CutGroupPass.Seen : CutGroupPass.Candidate;
        }

        // The collection of the frame after a retirement reflected it: its time is read here, once.
        private void FollowRetirement()
        {
            if (_collectAfterFrame == Time.frameCount)
            {
                double seconds = CutPhysicsStep.LastCollectSeconds;
                CollectionsAfterRetirement++;
                CollectAfterSeconds += seconds;
                MaxCollectAfterSeconds = Math.Max(MaxCollectAfterSeconds, seconds);
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

                long begin = Stopwatch.GetTimestamp();
                bool back;
                using (s_return.Auto())
                {
                    back = group.origin.TryReturn();
                }

                double spent = Since(begin);
                ReturnSeconds += spent;
                MaxReturnSeconds = Math.Max(MaxReturnSeconds, spent);
                Note(EventKind.Returned, group, back, 0, spent);
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

                long begin = Stopwatch.GetTimestamp();
                bool armed = group.origin.TryRearm();
                double spent = Since(begin);
                RearmSeconds += spent;
                MaxRearmSeconds = Math.Max(MaxRearmSeconds, spent);
                Note(EventKind.Rearmed, group, armed, 0, spent);
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
        }

        /// <summary>How many events are kept for <see cref="LogEvents"/>; past it they are counted only.</summary>
        public const int EventRoom = 4096;

        private readonly List<GroupEvent> _events = new List<GroupEvent>(1024);

        /// <summary>Events kept for <see cref="LogEvents"/>, and those past <see cref="EventRoom"/> that were not.</summary>
        public int EventsKept => _events.Count;
        public int EventsNotKept { get; private set; }

        private void Note(EventKind what, Group group, bool ok, int fragments, double seconds)
        {
            if (_events.Count >= EventRoom)
            {
                EventsNotKept++;
                return;
            }

            _events.Add(new GroupEvent
            {
                what = what, kind = group.kind, ok = ok, hasOrigin = group.origin != null, root = group.root.value, frame = Time.frameCount,
                fragments = fragments, liveAfter = _world.Ledger.LiveCutFragmentCount, seconds = seconds,
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
                            + " ms; live cut fragments now " + e.liveAfter + (e.hasOrigin ? "; its original comes back once the display has let its pieces go" : ""));
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
