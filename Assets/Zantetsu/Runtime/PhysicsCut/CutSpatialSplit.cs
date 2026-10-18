#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// How <see cref="CutSpatialSplit"/> looks for its one attempt and paces its Main work. Every value is the
    /// caller's; the profile holds the provisional ones (DESIGN 7.9.2, 7.9.5 leave the search's limits, the walk and
    /// the budget to the implementation).
    /// </summary>
    [Serializable]
    public struct CutSpatialSplitSettings
    {
        /// <summary>Off: nothing is walked, nothing is issued and a result that arrives is given back unused.</summary>
        public bool enabled;

        /// <summary>The most fragments one frame's turn looks at while it has no attempt.</summary>
        public int examinePerFrame;

        /// <summary>The part of the frame's Main budget always left alone (the compaction's reserve).</summary>
        public double reserveSeconds;

        /// <summary>What issuing an attempt is expected to cost on Main, until one was measured.</summary>
        public double seedIssueSeconds;

        /// <summary>What building and publishing the two objects is expected to cost on Main, until one was measured.</summary>
        public double seedPublishSeconds;

        /// <summary>The search's finite limits.</summary>
        public SpatialSplitSearchLimits limits;

        /// <summary>
        /// The convex-cut branch: where no plane leaves the convexes whole, a plane that leaves every triangle whole
        /// is looked for and the convexes that cross it are cut. Off: only what one plane separates whole is split.
        /// </summary>
        public bool cutConvexes;

        /// <summary>
        /// A split that cuts a convex is made only when the side with fewer triangles of the committed display
        /// geometry has this many or more. Not asked of a split that cuts nothing.
        /// </summary>
        public int minCutSideTriangles;

        /// <summary>
        /// What the Main stages of a split that cuts convexes are expected to cost until each was measured: making
        /// the collider meshes, reading one cooked hull, and building and publishing the two objects.
        /// </summary>
        public double seedCutApplySeconds, seedHullCheckSeconds, seedCutPublishSeconds;

        /// <summary>
        /// The bent partition (DESIGN 7.9.2): where no plane leaves the convexes whole, a surface of a few plane
        /// pieces that leaves every convex and every triangle whole on a side is looked for
        /// (<see cref="CutSpatialSplitBentSearch"/>), before any convex is cut by the mesh-led plane. Off: the planes
        /// alone. No convex is cut by it.
        /// </summary>
        public bool bentSearch;
        public SpatialSplitBentLimits bentLimits;

        /// <summary>
        /// The triangle minimum of a split that cuts a convex: 50, the TL's choice of 2026-10-11 after city walks at
        /// 200, 100 and 50. The expected costs beside it are provisional, from no performance measurement.
        /// </summary>
        public const int DefaultMinCutSideTriangles = 50;
        public const double DefaultSeedCutApplySeconds = 0.0005, DefaultSeedHullCheckSeconds = 0.0002, DefaultSeedCutPublishSeconds = 0.003;

        /// <summary>Without the convex-cut branch: what one plane separates whole, as the first version.</summary>
        public CutSpatialSplitSettings(
            bool enabled, int examinePerFrame, double reserveSeconds, double seedIssueSeconds, double seedPublishSeconds,
            SpatialSplitSearchLimits limits)
            : this(enabled, examinePerFrame, reserveSeconds, seedIssueSeconds, seedPublishSeconds, limits, false,
                DefaultMinCutSideTriangles, DefaultSeedCutApplySeconds, DefaultSeedHullCheckSeconds, DefaultSeedCutPublishSeconds)
        {
        }

        public CutSpatialSplitSettings(
            bool enabled, int examinePerFrame, double reserveSeconds, double seedIssueSeconds, double seedPublishSeconds,
            SpatialSplitSearchLimits limits, bool cutConvexes, int minCutSideTriangles,
            double seedCutApplySeconds, double seedHullCheckSeconds, double seedCutPublishSeconds)
        {
            this.enabled = enabled;
            this.examinePerFrame = examinePerFrame;
            this.reserveSeconds = reserveSeconds;
            this.seedIssueSeconds = seedIssueSeconds;
            this.seedPublishSeconds = seedPublishSeconds;
            this.limits = limits;
            this.cutConvexes = cutConvexes;
            this.minCutSideTriangles = minCutSideTriangles;
            this.seedCutApplySeconds = seedCutApplySeconds;
            this.seedHullCheckSeconds = seedHullCheckSeconds;
            this.seedCutPublishSeconds = seedCutPublishSeconds;
            bentSearch = false;   // the bent partition is set apart (the profile's reading, a test's): off here
            bentLimits = default;
        }

        public bool IsValid => examinePerFrame >= 1 && reserveSeconds >= 0.0 && !double.IsNaN(reserveSeconds)
            && seedIssueSeconds >= 0.0 && !double.IsNaN(seedIssueSeconds)
            && seedPublishSeconds >= 0.0 && !double.IsNaN(seedPublishSeconds)
            && minCutSideTriangles >= 1
            && seedCutApplySeconds >= 0.0 && !double.IsNaN(seedCutApplySeconds)
            && seedHullCheckSeconds >= 0.0 && !double.IsNaN(seedHullCheckSeconds)
            && seedCutPublishSeconds >= 0.0 && !double.IsNaN(seedCutPublishSeconds)
            && limits.IsValid && (!bentSearch || bentLimits.IsValid);

        /// <summary>Whether two settings look for the same thing: a mark made under one says nothing under another.</summary>
        internal bool SearchesAs(in CutSpatialSplitSettings other)
        {
            return cutConvexes == other.cutConvexes && minCutSideTriangles == other.minCutSideTriangles
                && limits.maxConvexes == other.limits.maxConvexes && limits.maxCentreDirections == other.limits.maxCentreDirections
                && limits.maxPlaneTests == other.limits.maxPlaneTests && limits.maxTriangles == other.limits.maxTriangles
                && limits.maxMeshDirections == other.limits.maxMeshDirections && limits.meshBins == other.limits.meshBins
                && bentSearch == other.bentSearch && (!bentSearch || bentLimits.Equals(in other.bentLimits));
        }
    }

    /// <summary>Why a turn of <see cref="CutSpatialSplit"/> issued nothing and published nothing.</summary>
    public enum SpatialSplitHold
    {
        None = 0,

        /// <summary>Heavy work was noted in this frame (<see cref="CutWorldFrameNotes"/>).</summary>
        HeavyWork,

        /// <summary>A managed garbage collection completed in this frame.</summary>
        GarbageCollection,

        /// <summary>A cut is in flight or held.</summary>
        CutInFlight,

        /// <summary>The ledger changed since the turn before: a cut, a retirement, a collection of a group or of an empty owner.</summary>
        StructureChanged,

        /// <summary>The display's room, its snapshot, a GPU object or the reference table was grown or replaced since the turn before.</summary>
        DisplayRoomGrown,

        /// <summary>What is left of the frame's Main budget is less than the reserve and twice the expected cost.</summary>
        BudgetShort,

        Count,
    }

    /// <summary>Which stage of a turn asked the budget (<see cref="SpatialSplitBudgetDecision"/>).</summary>
    public enum SpatialSplitBudgetStage
    {
        /// <summary>A turn without an attempt: a round's start when due, one look, an issue.</summary>
        Choose,

        /// <summary>A turn with a collected attempt that cuts nothing: building and publishing the two objects.</summary>
        Publish,

        /// <summary>A cut attempt's stages: making its collider meshes, reading one cooked hull, publishing.</summary>
        CutApply,
        HullCheck,
        CutPublish,
    }

    /// <summary>
    /// One turn's budget comparison as it was made (unit 10-B, 2026-10-18): the values the comparison used, kept so
    /// that a device's periodic line can show why a turn was held short of budget -- the remaining time read once
    /// and compared, not read again for the record. The last one only; nothing is written every frame.
    /// </summary>
    public readonly struct SpatialSplitBudgetDecision
    {
        public SpatialSplitBudgetDecision(int frame, SpatialSplitBudgetStage stage, double remaining, double reserve, double laterStep, double expected, bool held)
        {
            Frame = frame;
            Stage = stage;
            Remaining = remaining;
            Reserve = reserve;
            LaterStep = laterStep;
            Expected = expected;
            Held = held;
        }

        /// <summary>The frame of the comparison; <see cref="int.MinValue"/> before any.</summary>
        public int Frame { get; }
        public SpatialSplitBudgetStage Stage { get; }

        /// <summary>What the comparison read as left of the frame's Main budget, in seconds.</summary>
        public double Remaining { get; }

        /// <summary>The settings' reserve, the physics step still expected in this frame, and the stage's own expected cost, in seconds.</summary>
        public double Reserve { get; }
        public double LaterStep { get; }
        public double Expected { get; }

        /// <summary>The whole threshold: reserve + laterStep + 2 x expected.</summary>
        public double Threshold => Reserve + LaterStep + (2.0 * Expected);

        /// <summary>What the comparison had to spare: remaining - threshold. Held exactly when it is below zero (equal goes on).</summary>
        public double Margin => Remaining - Threshold;

        /// <summary>Whether the turn was held for the budget (remaining below the threshold).</summary>
        public bool Held { get; }

        public bool IsSet => Frame != int.MinValue;

        public static SpatialSplitBudgetDecision None => new SpatialSplitBudgetDecision(int.MinValue, SpatialSplitBudgetStage.Choose, 0.0, 0.0, 0.0, 0.0, false);

        public string Describe()
        {
            if (!IsSet) return "budget decision: none yet";
            CultureInfo inv = CultureInfo.InvariantCulture;
            return "budget decision at frame " + Frame.ToString(inv) + " (" + Stage + "): remaining " + (Remaining * 1000.0).ToString("F3", inv)
                + " ms vs threshold " + (Threshold * 1000.0).ToString("F3", inv) + " ms = reserve " + (Reserve * 1000.0).ToString("F3", inv)
                + " + later step " + (LaterStep * 1000.0).ToString("F3", inv) + " + 2 x expected " + (Expected * 1000.0).ToString("F3", inv)
                + " -> " + (Held ? "held (BudgetShort)" : "went on");
        }
    }

    /// <summary>
    /// A run of budget comparisons that held, for one attempt at one stage (unit 10-B, 2026-10-18): how many in a row,
    /// from which frame to which, and the comparison of the run with the largest margin (remaining - threshold), with
    /// the values it used. A new attempt or a new stage begins a new run; a comparison that goes on ends the run.
    /// Holds for another reason (heavy work, a collection, a cut in flight, a structural change, room grown) make
    /// no comparison and neither lengthen nor end it. The attempt is 0 for a turn without one (Choose).
    /// </summary>
    public readonly struct SpatialSplitHoldStreak
    {
        public SpatialSplitHoldStreak(long attempt, SpatialSplitBudgetStage stage, int firstFrame, int lastFrame, int holds, SpatialSplitBudgetDecision best, bool ended, bool endedByGoingOn)
        {
            Attempt = attempt;
            Stage = stage;
            FirstFrame = firstFrame;
            LastFrame = lastFrame;
            Holds = holds;
            Best = best;
            Ended = ended;
            EndedByGoingOn = endedByGoingOn;
        }

        public long Attempt { get; }
        public SpatialSplitBudgetStage Stage { get; }
        public int FirstFrame { get; }
        public int LastFrame { get; }

        /// <summary>Comparisons in a row that held.</summary>
        public int Holds { get; }

        /// <summary>The held comparison of this run with the largest margin (closest to going on), with its values.</summary>
        public SpatialSplitBudgetDecision Best { get; }

        /// <summary>Whether the run is over, and whether a comparison of the same attempt and stage then went on (else the attempt or the stage changed).</summary>
        public bool Ended { get; }
        public bool EndedByGoingOn { get; }

        public bool IsSet => Holds > 0;

        public static SpatialSplitHoldStreak None => default;

        internal SpatialSplitHoldStreak Held(in SpatialSplitBudgetDecision decision)
        {
            SpatialSplitBudgetDecision best = Holds == 0 || decision.Margin > Best.Margin ? decision : Best;
            return new SpatialSplitHoldStreak(Attempt, Stage, Holds == 0 ? decision.Frame : FirstFrame, decision.Frame, Holds + 1, best, false, false);
        }

        internal SpatialSplitHoldStreak EndedAs(bool byGoingOn) => new SpatialSplitHoldStreak(Attempt, Stage, FirstFrame, LastFrame, Holds, Best, true, byGoingOn);

        public string Describe()
        {
            if (!IsSet) return "hold streak: none";
            CultureInfo inv = CultureInfo.InvariantCulture;
            return "hold streak: attempt " + Attempt.ToString(inv) + " at " + Stage + ", " + Holds.ToString(inv) + " held in a row (frames "
                + FirstFrame.ToString(inv) + ".." + LastFrame.ToString(inv) + ")" + (Ended ? (EndedByGoingOn ? ", ended: went on" : ", ended: the attempt or the stage changed") : ", going on")
                + "; largest margin " + (Best.Margin * 1000.0).ToString("F3", inv) + " ms in its " + Best.Describe();
        }
    }

    /// <summary>What a fragment looked at turned out to be.</summary>
    public enum SpatialSplitPass
    {
        /// <summary>A candidate: an attempt may be issued for it.</summary>
        Candidate = 0,

        /// <summary>No longer a live fragment.</summary>
        NotLive,

        /// <summary>A registered fragment no cut made: passed over in this first version.</summary>
        NotCutMade,

        /// <summary>A cut of it is accepted or its Provisional pair stands.</summary>
        CutUnderWay,

        /// <summary>No final physics owner of its own here: none, withdrawn, a member of a group, or display only.</summary>
        NoOwnPhysics,

        /// <summary>Building-derived: passed over in this first version.</summary>
        Building,

        /// <summary>Fewer than two convexes: nothing to put on two sides.</summary>
        FewConvexes,

        /// <summary>Its shared geometry is not committed, or is settled as empty.</summary>
        GeometryOpen,

        /// <summary>It has no registration of its own in the display, or is drawn inside an aggregate.</summary>
        NotShownAlone,

        /// <summary>The search already found nothing for this very input.</summary>
        Suppressed,

        /// <summary>The profile's two epsilons are both zero: there is no margin to keep from the plane.</summary>
        NoTolerance,

        /// <summary>The display has no draw slots or references left for two registrations in its place.</summary>
        NoDisplayRoom,

        Count,
    }

    /// <summary>How an attempt ended.</summary>
    public enum SpatialSplitEnd
    {
        /// <summary>The two objects were published.</summary>
        Published = 0,

        /// <summary>The finite search found no plane for a current input: the input is not tried again.</summary>
        NotFound,

        /// <summary>A position was not finite, or a convex lay within the support epsilon of the plane: the input is not tried again.</summary>
        NotClassified,

        /// <summary>The input is over the search's limits: it is not tried again.</summary>
        OverTheLimits,

        /// <summary>The plane holds but the anchors, the masses or the owners cannot be made for it: the input is not tried again.</summary>
        NotBuildable,

        /// <summary>What the attempt was issued for is no longer what stands there: nothing is applied and nothing is recorded.</summary>
        Stale,

        /// <summary>The work failed or was cancelled, or the feature was switched off: nothing is recorded.</summary>
        NotRun,

        /// <summary>Room was short at the publication (index room, draw slots, the reference table, the GPU copy): nothing is recorded.</summary>
        RoomShort,

        Count,
    }

    /// <summary>
    /// DESIGN 7.9.2's additional split, first version (Phase 5.6: not for a building-derived object, and no convex is
    /// cut): one live fragment whose own final physics owner holds two or more convexes, and whose committed shared
    /// geometry and convexes are both left whole by one plane with something of each on either side, is replaced by
    /// two ordinary objects -- two fragments, two owners, two registrations -- of exactly what it was made of. It is
    /// optional and nothing depends on it: off, late or never, every cut, commit and recut goes on as before, and
    /// whenever it cannot be done the object simply stays as it is.
    /// <para>
    /// **One attempt.** At most one over the whole world, from the moment it is issued until what it made has been
    /// applied or given back. A turn without one walks the owners the registry has, a few a frame, and issues an
    /// attempt for the first fragment that passes the light checks: nothing geometric is decided on the main thread.
    /// The numbers -- the plane, the triangles' sides, the convexes' sides, the anchors' sides, the masses -- are one
    /// work of <see cref="WorkPurpose.Maintenance"/> on the background pool (<see cref="CutSpatialSplitSearch"/>),
    /// which reads the geometry through a lease of its index range and the shape through a hold, and no Unity object.
    /// While it waits and runs, the object goes on as it is and may be cut.
    /// </para>
    /// <para>
    /// **The publication.** Right before it, everything the attempt was issued for is asked again: the fragment
    /// live with no cut of its own, the same owner, the same shape, the same geometry. Moving is no reason to
    /// refuse; the placement and the motion are read at that moment. Then, in one main-thread stretch: the two index
    /// ranges are published over the fragment's own vertices, the two owners are built from the inherited convexes
    /// (their B-reps, cooked meshes and frames as they are: nothing is cooked), the display judges its room -- and
    /// only then the switch, none of which can be refused: the ledger replaces the fragment by two
    /// (<see cref="LogicalCutLedger.ReplaceBySplit"/>: no operation, no pending cut, no boundary), the registry takes
    /// the two owners and retires the old one, the cut DAG hands the geometry on, and the display takes the two
    /// registrations and keeps the old one until its next adoption. No impulse and no offset is added and nothing is
    /// moved: each object starts where the old one stands with the velocity its own centre of mass had.
    /// </para>
    /// <para>
    /// **When.** One turn a frame, in the world's LateUpdate before the frame's collection, so that the collection of
    /// the very frame the gate chose reflects a publication. A turn issues nothing and publishes nothing when heavy
    /// work was noted in the frame, a garbage collection completed in it, a cut is in flight or held, the ledger
    /// changed since the turn before (which also keeps it out of a frame the group collection retired in, and out of
    /// the frame after its own publication), the display grew room or replaced a GPU object, or what is left of the
    /// frame's Main budget is less than the reserve, the simulation this frame's decision is still to take (the
    /// turn runs before it), and twice the expected cost of what the turn would do -- its own measured cost: the
    /// walk and an issue, or a publication with the collection that follows it. Giving back an attempt that will
    /// not be applied is not held.
    /// </para>
    /// <para>
    /// **The walk is paid for too.** Taking the round's list, letting go of marks whose fragments are gone and
    /// looking at a fragment are measured, each learned as the issue and the publication are (a seed, then the
    /// median of the last five), and part of what a turn without an attempt is expected to cost -- so a frame that
    /// finds no candidate is a sample as well. A turn is allowed for one look and an issue; before each further
    /// fragment what is left of the budget is asked again, and the walk stops where it runs out and goes on from
    /// there in a later frame.
    /// </para>
    /// <para>
    /// **And it is not walked for nothing.** A round that reached its end with nothing left to try -- every fragment
    /// passed over, tried and marked, or published -- is not begun again until something an examination reads has
    /// changed since that round began: the ledger's revision (a cut admitted, published, committed or ended, a
    /// retirement, a split), the owner registry (an owner or a Provisional pair come or gone) or the display's room.
    /// Until then a turn does nothing at all. A round that left something wanting -- a publication short of room, a
    /// work that did not run, an issue that found no place -- is followed by another as before. In an examination
    /// the mark is compared before the display is asked anything: a fragment already tried costs a lookup, not a
    /// scan of the display's registrations.
    /// </para>
    /// <para>
    /// **Not again.** When the search ran on an input that is still the fragment's and found no plane -- or found
    /// one that cannot be built -- the fragment is marked with that input and not tried again; the mark is its
    /// owner, shape, geometry and anchor count, so anything that changes one of them is another input. A work that did not run, a
    /// result for an input that has since changed and a publication short of room mark nothing.
    /// </para>
    /// <para>
    /// **Cutting convexes** (<see cref="CutSpatialSplitSettings.cutConvexes"/>, the extension of 2026-10-11). The
    /// search that leaves the convexes whole comes first and its result is taken as before. Where it finds nothing,
    /// the same work looks for a plane by the mesh alone (<see cref="CutSpatialSplitSearch.FindMeshPlane"/>): every
    /// triangle whole on one side, triangles on both. A convex that crosses it is cut by it -- the cut's own
    /// classification, clip, reduction and masses, all on the worker -- provided the side with fewer triangles has
    /// the minimum asked; one convex wrapped round two separate parts of the mesh is a candidate this way. No
    /// triangle is cut, nothing is drawn for the new faces, and no cut operation exists for it.
    /// The Main work of that branch is three stages, each begun only by a turn the gate lets through and each with
    /// its own expected cost: the collider meshes are made from the worker's numbers and the work is offered again,
    /// to bake them on the background pool (never the cut's urgent job path); the cooked hulls are read, a few a
    /// turn while the budget holds; then the publication as above, with the parts that were cut built from their
    /// new meshes. A frame the gate holds -- heavy work noted, a garbage collection, a cut in flight, the ledger
    /// changed, the display's room grown, the budget short -- does nothing at all for such an attempt: it is not
    /// looked at again (that look scans the display), not built and not even given back, and the parent stands
    /// until the switch. Only the world's own ending gives it back at once. A mesh that cannot be made ends the
    /// attempt once, with what was made before it, and marks the input. A mark is of the settings it was made
    /// under: other limits, another minimum or the branch switched on are another search.
    /// </para>
    /// </summary>
    public sealed class CutSpatialSplit
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
        private static readonly ProfilerMarker s_issue = new ProfilerMarker("Zantetsu.SpatialSplit.Issue");
        private static readonly ProfilerMarker s_publish = new ProfilerMarker("Zantetsu.SpatialSplit.Publish");
        private static readonly ProfilerMarker s_apply = new ProfilerMarker("Zantetsu.SpatialSplit.CutMeshes");
        private static readonly ProfilerMarker s_check = new ProfilerMarker("Zantetsu.SpatialSplit.CutHulls");

        private readonly CutWorldRoot _world;
        private readonly List<LogicalFragmentId> _round = new List<LogicalFragmentId>();
        private readonly SimulateCostHistory _issueCosts = new SimulateCostHistory();
        private readonly SimulateCostHistory _publishCosts = new SimulateCostHistory();
        private readonly SimulateCostHistory _collectAfter = new SimulateCostHistory();
        private readonly SimulateCostHistory _examineCosts = new SimulateCostHistory();   // one look at a fragment, a turn's average
        private readonly SimulateCostHistory _roundCosts = new SimulateCostHistory();     // taking a round's list and pruning the marks
        private readonly SimulateCostHistory _cutApplyCosts = new SimulateCostHistory();     // a cutting split: its meshes made
        private readonly SimulateCostHistory _hullCheckCosts = new SimulateCostHistory();    // a cutting split: one cooked hull read
        private readonly SimulateCostHistory _cutPublishCosts = new SimulateCostHistory();   // a cutting split: built and published
        private readonly SimulateCostHistory _cutCollectAfter = new SimulateCostHistory();   // the collection after such a publication
        private readonly int[] _holds = new int[(int)SpatialSplitHold.Count];
        private readonly int[] _passes = new int[(int)SpatialSplitPass.Count];
        private readonly int[] _ends = new int[(int)SpatialSplitEnd.Count];
        private readonly Dictionary<LogicalFragmentId, Input> _suppressed = new Dictionary<LogicalFragmentId, Input>();
        private readonly List<LogicalFragmentId> _suppressedScratch = new List<LogicalFragmentId>();
        private readonly List<float3> _anchorScratch = new List<float3>();
        private readonly List<SplitEvent> _events = new List<SplitEvent>(64);

        // What an examination reads that can change its answer, as three numbers: taken when a round begins, and
        // compared while the walk stands idle after a round that left nothing to try.
        private Signals _roundSignals, _idleSignals;
        private bool _idle, _roundWanting, _subscribed, _passedForDisplayState;
        private long _ownerChanges;
        private CutSpatialSplitSettings _settings;
        private float3 _publishedAt;
        private int _cursor;
        private Attempt _attempt;
        private int _followCollectOf = int.MinValue;
        private bool _followIsCut;
        private int _markEpoch;

        // What the turn before saw of the ledger and of the display's room: a difference is something this frame did.
        private long _revisionSeen = -1;
        private long _roomSeen = -1;

        /// <summary>The collection that follows a publication, until one was measured (the group collection's seed).</summary>
        public const double SeedCollectAfterSeconds = 0.001;

        /// <summary>One look at a fragment, and the start of a round, until each was measured. Provisional, from no Player measurement.</summary>
        public const double SeedExamineSeconds = 0.00001;
        public const double SeedRoundSeconds = 0.0001;

        internal CutSpatialSplit(CutWorldRoot world, in CutSpatialSplitSettings settings)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            Settings = settings;
            Subscribe();
            RemainingMainSeconds = () => CutPhysicsStep.FrameRemainingMainSeconds;
            LaterStepSeconds = () => CutPhysicsStep.ExpectedStepSecondsLaterThisFrame;
        }

        /// <summary>The settings. Giving them again ends an idle wait: what a round found under others says nothing of these.</summary>
        public CutSpatialSplitSettings Settings
        {
            get => _settings;
            set
            {
                if (!value.SearchesAs(in _settings))
                {
                    // Another search: what was marked under the old one is not marked under this.
                    _markEpoch++;
                }

                _settings = value;
                _idle = false;
            }
        }

        /// <summary>Whether the walk stands idle: its last round left nothing to try and nothing it reads has changed since.</summary>
        public bool IsIdle => _idle && _idleSignals.Equals(SignalsNow());

        /// <summary>Turns in which the walk stood idle (not counted among <see cref="Turns"/>), and the times a round's end began such a wait.</summary>
        public long IdleTurns { get; private set; }
        public int IdleWaits { get; private set; }

        /// <summary>What is left of this frame's Main budget. The product's frame clock; a test gives its own.</summary>
        public Func<double> RemainingMainSeconds { get; set; }

        /// <summary>
        /// What this frame's decision is still expected to spend on a simulation: the turn runs before it, so that
        /// much of what is left of the budget is not the turn's. The product's own estimate; a test gives its own.
        /// </summary>
        public Func<double> LaterStepSeconds { get; set; }

        /// <summary>Whether an attempt is outstanding: issued and not yet applied or given back.</summary>
        public bool HasAttempt => _attempt != null;

        /// <summary>Whether the outstanding attempt's work has come back and waits for a turn of the main thread.</summary>
        public bool AttemptIsCollected => _attempt != null && _attempt.collected;

        /// <summary>Whether the outstanding attempt's work came back with convexes to cut (known once it has come back).</summary>
        public bool AttemptCutsConvexes => _attempt != null && _attempt.collected && _attempt.cut;

        /// <summary>The fragment the outstanding attempt is of, unset without one.</summary>
        public LogicalFragmentId AttemptFragment => _attempt != null ? _attempt.fragment : default;

        /// <summary>The outstanding attempt's number in this world (from 1), or 0 with none.</summary>
        public long AttemptSerial => _attempt != null ? _attempt.serial : 0;

        private long _attemptSerial;

        /// <summary>Turns taken while on, those that were held, and rounds of the walk finished.</summary>
        public int Turns { get; private set; }
        public int Rounds { get; private set; }

        /// <summary>Fragments looked at, attempts issued, and attempts the dispatcher had no place for.</summary>
        public int Examined { get; private set; }
        public int Issued { get; private set; }
        public int QueueFull { get; private set; }

        /// <summary>Fragments published as two, and the two-object publications' additions: actors, draw registrations, indices transferred.</summary>
        public int Published { get; private set; }
        public int ActorsAdded { get; private set; }
        public int RegistrationsAdded { get; private set; }
        public long IndicesTransferred { get; private set; }

        /// <summary>Fragments marked as not to be tried again for their current input, now.</summary>
        public int SuppressedNow => _suppressed.Count;

        /// <summary>
        /// The convex-cut branch: attempts whose search went on to the mesh-led one, those that came back with
        /// convexes to cut, publications that cut, convexes cut by them, collider meshes made, cooked hulls read and
        /// refused, and turns the gate held in which such an attempt was left alone (not looked at, not built, not
        /// given back).
        /// </summary>
        public int MeshLedSearches { get; private set; }
        public int CutAttempts { get; private set; }
        public int PublishedWithCut { get; private set; }
        public int ConvexesCut { get; private set; }
        public int CutMeshesMade { get; private set; }
        public int HullsChecked { get; private set; }
        public int HullsRefused { get; private set; }
        public int CutTurnsLeftAlone { get; private set; }

        /// <summary>Attempts ended because a collider mesh of a cut convex could not be made on the main thread.</summary>
        public int CutMeshFailures { get; private set; }

        /// <summary>
        /// The bent partition (DESIGN 7.9.2): attempts whose search went on to it, those it found a surface for,
        /// publications by it, the closures that read the triangles, the elements (convexes and triangles) verified
        /// over their whole extent, and its own time on the worker (the attempt's worker time holds it too).
        /// </summary>
        public int BentSearches { get; private set; }
        public int BentFound { get; private set; }
        public int PublishedBent { get; private set; }
        public long BentCandidates { get; private set; }
        public long BentElementsVerified { get; private set; }
        public double BentSeconds { get; private set; }
        public double MaxBentSeconds { get; private set; }

        /// <summary>For a test: the making of the mesh of this number fails (-1: none does). The product never sets it.</summary>
        internal static int FailMeshAtForTest = -1;

        /// <summary>The convex-cut branch's times: the bakes on the worker, and on Main the meshes made, the hulls read, and the publishing turns.</summary>
        public double BakeSeconds { get; private set; }
        public double MaxBakeSeconds { get; private set; }
        public double CutApplySeconds { get; private set; }
        public double MaxCutApplySeconds { get; private set; }
        public double HullCheckSeconds { get; private set; }
        public double MaxHullCheckTurnSeconds { get; private set; }
        public double CutPublishSeconds { get; private set; }
        public double MaxCutPublishTurnSeconds { get; private set; }

        /// <summary>What the Main stages of a split that cuts convexes are expected to cost: the seeds, then the medians of the last five measured.</summary>
        public double ExpectedCutApplySeconds => _cutApplyCosts.Count == 0 ? Settings.seedCutApplySeconds : _cutApplyCosts.ExpectedSeconds;
        public double ExpectedHullCheckSeconds => _hullCheckCosts.Count == 0 ? Settings.seedHullCheckSeconds : _hullCheckCosts.ExpectedSeconds;
        public double ExpectedCutPublishSeconds =>
            (_cutPublishCosts.Count == 0 ? Settings.seedCutPublishSeconds : _cutPublishCosts.ExpectedSeconds)
            + (_cutCollectAfter.Count == 0 ? SeedCollectAfterSeconds : _cutCollectAfter.ExpectedSeconds);

        /// <summary>The worker's time over every attempt collected, and the longest.</summary>
        public double WorkerSeconds { get; private set; }
        public double MaxWorkerSeconds { get; private set; }

        /// <summary>Main time: issuing (input preparation), building the two owners, and the switch with its transfer; and the longest publishing turn.</summary>
        public double IssueSeconds { get; private set; }
        public double BuildSeconds { get; private set; }
        public double SwitchSeconds { get; private set; }
        public double MaxPublishTurnSeconds { get; private set; }

        /// <summary>The frame of the last publication, and what the last search did; <see cref="int.MinValue"/> before any.</summary>
        public int LastPublishedFrame { get; private set; } = int.MinValue;
        public SpatialSplitSearchResult LastSearch { get; private set; }

        /// <summary>The two fragments of the last publication, and the one they replaced.</summary>
        public LogicalFragmentId LastSource { get; private set; }
        public LogicalFragmentId LastPositive { get; private set; }
        public LogicalFragmentId LastNegative { get; private set; }

        /// <summary>How the last attempt ended, and why the last turn was held.</summary>
        public SpatialSplitEnd LastEnd { get; private set; }
        public SpatialSplitHold LastHold { get; private set; }

        public int HeldFor(SpatialSplitHold hold) => _holds[(int)hold];

        public int PassedAs(SpatialSplitPass pass) => _passes[(int)pass];

        public int EndedAs(SpatialSplitEnd end) => _ends[(int)end];

        /// <summary>What issuing an attempt is expected to cost on Main: the seed, then the median of the last five measured.</summary>
        public double ExpectedIssueSeconds => _issueCosts.Count == 0 ? Settings.seedIssueSeconds : _issueCosts.ExpectedSeconds;

        /// <summary>One look at a fragment: the seed, then the median of the last five walking turns' averages.</summary>
        public double ExpectedExamineSeconds => _examineCosts.Count == 0 ? SeedExamineSeconds : _examineCosts.ExpectedSeconds;

        /// <summary>The start of a round (the list taken, the marks of gone fragments let go): the seed, then the median of the last five.</summary>
        public double ExpectedRoundSeconds => _roundCosts.Count == 0 ? SeedRoundSeconds : _roundCosts.ExpectedSeconds;

        /// <summary>
        /// What a turn without an attempt is expected to cost on Main before it is asked of the budget again: the
        /// start of a round when one is due, one look at a fragment, and an issue. Each further fragment is asked
        /// of the budget on its own, in the walk.
        /// </summary>
        public double ExpectedChooseSeconds =>
            (_cursor >= _round.Count ? ExpectedRoundSeconds : 0.0) + ExpectedExamineSeconds + ExpectedIssueSeconds;

        /// <summary>Main time of the walk -- rounds started and fragments looked at, issues apart -- over the run, the longest turn's, and of that the rounds' starts.</summary>
        public double WalkSeconds { get; private set; }
        public double MaxWalkTurnSeconds { get; private set; }
        public double RoundSeconds { get; private set; }

        /// <summary>Walks stopped before their count because what was left of the budget would not hold another look and an issue.</summary>
        public int WalksCutShort { get; private set; }

        /// <summary>
        /// What a publishing turn is expected to cost on Main, with the collection that follows it in the same
        /// frame: the seeds, then the medians of the last five measured of each.
        /// </summary>
        public double ExpectedPublishSeconds =>
            (_publishCosts.Count == 0 ? Settings.seedPublishSeconds : _publishCosts.ExpectedSeconds)
            + (_collectAfter.Count == 0 ? SeedCollectAfterSeconds : _collectAfter.ExpectedSeconds);

        /// <summary>One frame's turn: see the class notes.</summary>
        public void Step()
        {
            FollowPublication();

            CutSpatialSplitSettings settings = Settings;
            bool on = settings.enabled && settings.IsValid && _world.IsReady && !_world.IsEnding && !_world.TerminationRequested;
            if (_attempt != null && _attempt.collected && !on)
            {
                // Switched off, or the world is ending: what arrived is given back unused. Not held, and nothing marked.
                End(SpatialSplitEnd.NotRun, false);
            }

            if (!on)
            {
                return;
            }

            if (_attempt != null && !_attempt.collected)
            {
                // Waiting or running: the object goes on as it is. Nothing is asked of the frame. (A bake that found
                // no place in the queue is offered again, which is a call.)
                if (_attempt.stage == AttemptStage.Baking && !_attempt.offered)
                {
                    OfferBake(_attempt);
                }

                return;
            }

            if (_attempt != null && _attempt.cut)
            {
                // An attempt that cuts convexes holds meshes and a block of its own, and looking at it again scans
                // the display's registrations: nothing of it -- not that look, not a stage, not even giving it
                // back -- is done before the whole gate has let this frame through, with the cost of the stage it
                // is at. A frame with a spike, a structural change, grown room or a short budget only counts the hold.
                Turns++;
                double stageCost = !_attempt.applied ? ExpectedCutApplySeconds
                    : _attempt.hullsChecked < _attempt.meshCount ? ExpectedHullCheckSeconds
                    : ExpectedCutPublishSeconds;
                double stepLater = LaterStep();
                SpatialSplitBudgetStage cutStage = !_attempt.applied ? SpatialSplitBudgetStage.CutApply
                    : _attempt.hullsChecked < _attempt.meshCount ? SpatialSplitBudgetStage.HullCheck
                    : SpatialSplitBudgetStage.CutPublish;
                SpatialSplitHold held = HoldNow(in settings, stageCost, stepLater, cutStage);
                LastHold = held;
                if (held != SpatialSplitHold.None)
                {
                    _holds[(int)held]++;
                    CutTurnsLeftAlone++;
                    Remember();
                    return;
                }

                Remember();
                if (!ShouldPublish(_attempt, out SpatialSplitEnd over, out bool marked))
                {
                    End(over, marked);
                }
                else if (!_attempt.applied)
                {
                    ApplyCutMeshes();
                }
                else if (_attempt.hullsChecked < _attempt.meshCount)
                {
                    CheckHulls(in settings, stepLater);
                }
                else
                {
                    Publish(in settings);
                }

                return;
            }

            if (_attempt != null && !ShouldPublish(_attempt, out SpatialSplitEnd early, out bool mark))
            {
                // Nothing to publish: given back at once, whatever the frame is.
                End(early, mark);
                return;
            }

            if (_attempt == null && _idle)
            {
                if (_idleSignals.Equals(SignalsNow()))
                {
                    // The last round left nothing to try and nothing it read has changed: nothing is looked at, and
                    // nothing is asked of the frame.
                    IdleTurns++;
                    return;
                }

                _idle = false;
            }

            Turns++;
            double expected = _attempt != null ? ExpectedPublishSeconds : ExpectedChooseSeconds;
            double later = LaterStep();
            SpatialSplitHold hold = HoldNow(in settings, expected, later, _attempt != null ? SpatialSplitBudgetStage.Publish : SpatialSplitBudgetStage.Choose);
            LastHold = hold;
            if (hold != SpatialSplitHold.None)
            {
                _holds[(int)hold]++;
                Remember();
                return;
            }

            // The revision before this turn's own work: a publication makes the next frame a structural frame too.
            Remember();
            if (_attempt != null)
            {
                Publish(in settings);
            }
            else
            {
                Choose(in settings, later);
            }
        }

        // ---- the convex-cut branch's Main stages ----------------------------------------------------------------------

        // After the numbers: one Unity Mesh a produced convex -- positions alone, as a cut's are -- and the work
        // offered again, to bake them on the background pool. Nothing is built and nothing is published here.
        private void ApplyCutMeshes()
        {
            long begin = Stopwatch.GetTimestamp();
            Attempt attempt = _attempt;
            using (s_apply.Auto())
            {
                attempt.cooking = _world.Cook != null ? _world.Cook.Cooking : PhysicsCutCook.DefaultCooking;
                attempt.MakeMeshes();
                CutMeshesMade += attempt.meshesMade;
                if (!attempt.meshesBad)
                {
                    attempt.applied = true;
                    attempt.Reset(AttemptStage.Baking);
                    OfferBake(attempt);
                }
            }

            double spent = Since(begin);
#if ZANTETSU_OBSERVE
            CutApplySeconds += spent;
            MaxCutApplySeconds = Math.Max(MaxCutApplySeconds, spent);
#endif
            if (attempt.meshesBad)
            {
                // A mesh could not be made. What was made before it is still the attempt's and goes back with it,
                // here -- this turn passed the gate -- and once; the input is marked, so that the same meshes are
                // not made and lost again every round.
                CutMeshFailures++;
                LastRefusal = "a collider mesh of a cut convex could not be made: " + attempt.meshFailure;
                End(SpatialSplitEnd.NotBuildable, true);
                return;
            }

            _cutApplyCosts.Add(spent);
        }

        private void OfferBake(Attempt attempt)
        {
            if (_world.Dispatcher.TryEnqueue(WorkPurpose.Maintenance, attempt, out attempt.ticket))
            {
                attempt.offered = true;
            }
            else
            {
                QueueFull++;
            }
        }

        // After the bake: what PhysX made of each mesh, read as a cut's check reads its own. One at least a turn;
        // before each further one what is left of the budget is asked again, and the rest waits for a later turn.
        private void CheckHulls(in CutSpatialSplitSettings settings, double laterStepSeconds)
        {
            long begin = Stopwatch.GetTimestamp();
            Attempt attempt = _attempt;
            int read = 0;
            bool refused = false;
            using (s_check.Auto())
            {
                while (attempt.hullsChecked < attempt.meshCount)
                {
                    if (read > 0 && RemainingMainSeconds() < settings.reserveSeconds + laterStepSeconds + (2.0 * ExpectedHullCheckSeconds))
                    {
                        break;
                    }

                    Mesh mesh = attempt.meshes[attempt.hullsChecked];
                    bool convex = _world.Cook != null && mesh != null
                        && _world.Cook.ProbeBakedHull(mesh) == UnityEngine.LowLevelPhysics.GeometryType.ConvexMesh;
                    attempt.hullsChecked++;
                    read++;
                    HullsChecked++;
                    if (!convex)
                    {
                        refused = true;
                        HullsRefused++;
                        break;
                    }
                }
            }

            double spent = Since(begin);
#if ZANTETSU_OBSERVE
            HullCheckSeconds += spent;
            MaxHullCheckTurnSeconds = Math.Max(MaxHullCheckTurnSeconds, spent);
#endif
            if (read > 0)
            {
                _hullCheckCosts.Add(spent / read);
            }

            if (refused)
            {
                // The plane holds and the numbers are whole, but PhysX makes no convex of a part: this input is not
                // tried again. The parent stands as it was.
                LastRefusal = "PhysX made no convex of a cut convex's mesh";
                End(SpatialSplitEnd.NotBuildable, true);
            }
        }

        // The simulation still to come in this frame, as a time that can be added: never negative, never not a number.
        private double LaterStep()
        {
            double later = LaterStepSeconds != null ? LaterStepSeconds() : 0.0;
            return later > 0.0 && !double.IsInfinity(later) ? later : 0.0;
        }

        /// <summary>Gives back an outstanding attempt, for the world's end. Work still on a worker is given back when it is collected.</summary>
        internal void Release()
        {
            Unsubscribe();
            if (_attempt == null)
            {
                return;
            }

            Attempt attempt = _attempt;
            _attempt = null;
            if (attempt.collected || !attempt.offered || (_world.Dispatcher != null && _world.Dispatcher.Cancel(attempt.ticket)))
            {
                attempt.Free(_world.Storage);
            }
            else
            {
                attempt.abandoned = true;
            }
        }

        // ---- the walk -------------------------------------------------------------------------------------------------

        // The walk: measured whole -- the round's start, each look -- and learned, so that a turn that finds nothing is
        // a sample of what a turn costs too. The turn's gate allowed for a round's start when one was due, one look and
        // an issue; every further fragment is asked of what is left of the budget here, before it is looked at.
        private void Choose(in CutSpatialSplitSettings settings, double laterStepSeconds)
        {
            long begin = Stopwatch.GetTimestamp();
            double roundSpent = 0.0;
            double issueSpent = 0.0;
            int examined = 0;
            bool roundEnded = false;
            while (examined < settings.examinePerFrame)
            {
                if (_cursor >= _round.Count)
                {
                    if (examined > 0)
                    {
                        // The round ended in this turn: the next begins in a later one, under that turn's own gate.
                        break;
                    }

                    long roundBegin = Stopwatch.GetTimestamp();
                    _roundSignals = SignalsNow();   // what this round's answers are of: a change after this begins another
                    _roundWanting = false;
                    _round.Clear();
                    _world.Owners.CopyFragmentsTo(_round);
                    _cursor = 0;
                    ForgetGoneSuppressions();
                    roundSpent = Since(roundBegin);
#if ZANTETSU_OBSERVE
                    RoundSeconds += roundSpent;
#endif
                    _roundCosts.Add(roundSpent);
                    if (_round.Count == 0)
                    {
                        // No owner at all: a round with nothing in it has ended.
                        roundEnded = true;
                        break;
                    }
                }

                if (examined > 0
                    && RemainingMainSeconds() < settings.reserveSeconds + laterStepSeconds + (2.0 * (ExpectedExamineSeconds + ExpectedIssueSeconds)))
                {
                    // What is left would not hold another look and an issue: the walk goes on from here in a later frame.
                    WalksCutShort++;
                    break;
                }

                LogicalFragmentId fragment = _round[_cursor++];
                examined++;
                Examined++;
                if (_cursor >= _round.Count)
                {
                    Rounds++;
                    roundEnded = true;
                }

                SpatialSplitPass pass = Examine(fragment, in settings, out Input input);
                if (pass != SpatialSplitPass.Candidate)
                {
                    _passes[(int)pass]++;

                    // Passed over for what the display shows or has room for now: its adoption of a collection and its
                    // own compaction change that, and none of the three numbers counts either. Such a fragment is
                    // looked at again in the next round.
                    _roundWanting |= _passedForDisplayState;
                    continue;
                }

                int issuedBefore = Issued;
                issueSpent = TryIssue(fragment, in input, in settings);
                _roundWanting |= Issued == issuedBefore;   // not readable now, or no place: wanted again
                break;
            }

            if (roundEnded)
            {
                // The round reached its end. With nothing left wanting, the walk stands idle until something this
                // round read has changed since it began; an attempt issued for its last fragment ends first.
                if (!_roundWanting)
                {
                    _idle = true;
                    _idleSignals = _roundSignals;
                    IdleWaits++;
                }

                _roundWanting = false;
            }

            // The walk's own time: the turn's, less the issue (measured and learned apart).
            double walkSpent = Math.Max(0.0, Since(begin) - issueSpent);
#if ZANTETSU_OBSERVE
            WalkSeconds += walkSpent;
            MaxWalkTurnSeconds = Math.Max(MaxWalkTurnSeconds, walkSpent);
#endif
            if (examined > 0)
            {
                _examineCosts.Add(Math.Max(0.0, walkSpent - roundSpent) / examined);
            }
        }

        /// <summary>
        /// Looks at one fragment as the walk does: whether an attempt may be issued for it, and what it is made of
        /// now. Reads only; nothing geometric is decided here.
        /// </summary>
        public SpatialSplitPass Examine(LogicalFragmentId fragment)
        {
            return Examine(fragment, Settings, out _);
        }

        private SpatialSplitPass Examine(LogicalFragmentId fragment, in CutSpatialSplitSettings settings, out Input input)
        {
            input = default;
            _passedForDisplayState = false;
            LogicalCutLedger ledger = _world.Ledger;
            PhysicsOwnerRegistry owners = _world.Owners;
            CutDag dag = _world.Geometry;
            VpLogicalCutDisplay display = _world.Display;
            if (ledger == null || owners == null || dag == null || display == null || !ledger.IsCurrentTarget(fragment))
            {
                return SpatialSplitPass.NotLive;
            }

            if (ledger.TryGetActiveOperation(fragment, out _) || owners.TryGetProvisionalOf(fragment, out _))
            {
                return SpatialSplitPass.CutUnderWay;
            }

            if (!ledger.MayReplaceBySplit(fragment))
            {
                return SpatialSplitPass.NotCutMade;
            }

            if (!owners.TryGet(fragment, out PhysicsFragmentOwner owner) || owner.IsWithdrawn || owner.IsReleased
                || owner.IsDisplayOnly || owner.IsFused || owner.Body == null || owner.Root == null
                || !owners.TryResolveFragment(owner.Body, out LogicalFragmentId resolved, out float side)
                || resolved != fragment || side != 0f)
            {
                return SpatialSplitPass.NoOwnPhysics;
            }

            if (owner.Building.IsBuildingDerived || owner.BuildingWorldConstraint != null)
            {
                return SpatialSplitPass.Building;
            }

            PhysicsOwnerShape shape = owner.Shape;
            // One convex is nothing to put on two sides -- unless it may be cut, wrapped as it can be round two
            // separate parts of the mesh.
            if (shape == null || shape.IsFreed || shape.ConvexCount < (settings.cutConvexes ? 1 : 2))
            {
                return SpatialSplitPass.FewConvexes;
            }

            if (!dag.TryGetGeometry(fragment, out VpStoredGeometry geometry) || !dag.TryGetGeometryFrame(fragment, out _))
            {
                return SpatialSplitPass.GeometryOpen;
            }

            // The mark, before the display is asked anything: its answers below are scans of its registrations and
            // of its adopted branches, and a fragment already tried for this very input needs none of them.
            ledger.TryGetAnchorCount(fragment, out int anchors);
            input = new Input(owner, shape, geometry, anchors, _markEpoch);
            if (_suppressed.TryGetValue(fragment, out Input tried) && tried.Equals(input))
            {
                return SpatialSplitPass.Suppressed;
            }

            if (!(MarginOf(_world.Profile) > 0f))
            {
                return SpatialSplitPass.NoTolerance;
            }

            // Following its own owner: where the triangles sit in the owner's coordinates is what the one plane is
            // judged in, so a lineage whose display is arranged some other way is passed over. Neither changes
            // while the fragment and the display live.
            if (owner.GeometryLocalToOwner == null || display.IsHalted)
            {
                return SpatialSplitPass.NotShownAlone;
            }

            // What the display shows now and has room for now. These follow its adoption of a collection and its own
            // compaction, which the walk's idle wait does not watch: a fragment passed over here is wanted again.
            if (display.IsDrawnInsideAggregate(fragment))
            {
                _passedForDisplayState = true;
                return SpatialSplitPass.NotShownAlone;
            }

            // Its own registration, and the room the publication will need of the display as far as it can be known
            // before the two index ranges exist -- asked together, in one pass over the registrations. Which of the
            // two was missing is asked only where one was.
            if (!display.HasRoomForSplit(fragment, 0, 2 * geometry.submeshCount, out _))
            {
                _passedForDisplayState = true;
                return display.HasOwnRegistration(fragment) ? SpatialSplitPass.NoDisplayRoom : SpatialSplitPass.NotShownAlone;
            }

            return SpatialSplitPass.Candidate;
        }

        // ---- standing idle --------------------------------------------------------------------------------------------

        /// <summary>What an examination's answers are of, as far as they can change between two rounds.</summary>
        private readonly struct Signals : IEquatable<Signals>
        {
            internal Signals(long ledger, long owners, long room)
            {
                this.ledger = ledger;
                this.owners = owners;
                this.room = room;
            }

            private readonly long ledger, owners, room;

            public bool Equals(Signals other) => ledger == other.ledger && owners == other.owners && room == other.room;
        }

        private Signals SignalsNow()
        {
            return new Signals(_world.Ledger != null ? _world.Ledger.Revision : -1, _ownerChanges, _world.DisplayRoomSerial());
        }

        // The registry says when an owner or a Provisional pair comes or goes; a handoff of a pair to its two final
        // owners changes what answers for a fragment without the ledger counting anything.
        private void Subscribe()
        {
            PhysicsOwnerRegistry owners = _world.Owners;
            if (_subscribed || owners == null)
            {
                return;
            }

            _subscribed = true;
            owners.OwnerAdded += OnOwnerChanged;
            owners.OwnerRetiring += OnOwnerChanged;
            owners.ProvisionalAdded += OnPairChanged;
            owners.ProvisionalEnding += OnPairChanged;
        }

        private void Unsubscribe()
        {
            PhysicsOwnerRegistry owners = _world.Owners;
            if (!_subscribed || owners == null)
            {
                return;
            }

            _subscribed = false;
            owners.OwnerAdded -= OnOwnerChanged;
            owners.OwnerRetiring -= OnOwnerChanged;
            owners.ProvisionalAdded -= OnPairChanged;
            owners.ProvisionalEnding -= OnPairChanged;
        }

        private void OnOwnerChanged(LogicalFragmentId fragment, PhysicsFragmentOwner owner) => _ownerChanges++;

        private void OnPairChanged(ProvisionalOwnerPair pair) => _ownerChanges++;

        // ---- issuing --------------------------------------------------------------------------------------------------

        // Issues an attempt for one candidate, or does not (nothing readable, no place in the queue). The Main time it
        // took, which is the issue's own sample only when an attempt was issued.
        private unsafe double TryIssue(LogicalFragmentId fragment, in Input input, in CutSpatialSplitSettings settings)
        {
            long begin = Stopwatch.GetTimestamp();
            using (s_issue.Auto())
            {
                VpCpuGeometryStorage storage = _world.Storage;
                CutWorldProfile profile = _world.Profile;
                PhysicsOwnerShape shape = input.shape;
                int convexCount = shape.ConvexCount;
                int maxConvexVertices = 0;
                for (int c = 0; c < convexCount; c++)
                {
                    maxConvexVertices = Math.Max(maxConvexVertices, shape.Convex(c).vertexCount);
                }

                int classificationBytes = PhysicsCutClassification.RequiredBlockBytes(shape);
                if (storage == null || profile == null || classificationBytes < 0
                    || !storage.TryGetSubmeshes(input.geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes)
                    || submeshes.Length == 0
                    || !storage.TryAcquireIndexReadLease(input.geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly indexView))
                {
                    // Not readable now: nothing was taken, and nothing is recorded about the input.
                    _passes[(int)SpatialSplitPass.GeometryOpen]++;
                    return Since(begin);
                }

                var attempt = new Attempt
                {
                    serial = ++_attemptSerial,
                    fragment = fragment,
                    input = input,
                    lease = lease,
                    holdsLease = true,
                    parentMass = input.owner.Mass,
                    vertexLimit = profile.VertexLimit,
                    supportEpsilon = profile.SupportEpsilon,
                    anchorEpsilon = profile.AnchorEpsilon,
                    margin = MarginOf(profile),
                    limits = settings.limits,
                    cutConvexes = settings.cutConvexes,
                    minCutSideTriangles = settings.minCutSideTriangles,
                    bentSearch = settings.bentSearch,
                    bentLimits = settings.bentLimits,
                    maxConvexVertices = maxConvexVertices,
                    geometryToOwner = input.owner.GeometryLocalToOwner.Value,
                    shapeToOwner = shape.LocalToOwner,
                    indexCount = indexView.Length,
                    submeshCount = submeshes.Length,
                    convexCount = convexCount,
                    materials = new int[submeshes.Length],
                    issuedFrame = Time.frameCount,
                };
                try
                {
                    shape.AcquireForWork();
                    attempt.holdsShape = true;
                    NativeArray<VpRenderVertex>.ReadOnly vertices = storage.Vertices;
                    attempt.vertices = (VpRenderVertex*)vertices.GetUnsafeReadOnlyPtr();
                    attempt.vertexViewLength = vertices.Length;
                    attempt.indices = (uint*)indexView.GetUnsafeReadOnlyPtr();

                    // One native block for everything the worker writes or reads beside the geometry and the bank:
                    // the output indices, the two sides' submesh tables and boxes, the convex table, the scratch.
                    int s = submeshes.Length;
                    long outputAt = 0;
                    long submeshInAt = outputAt + Align16((long)attempt.indexCount * sizeof(uint));
                    long submeshOutAt = submeshInAt + Align16(2L * s * sizeof(int));
                    long boundsAt = submeshOutAt + Align16(4L * s * sizeof(int));
                    long banksAt = boundsAt + Align16(4L * s * sizeof(float3));
                    long rangesAt = banksAt + Align16((long)convexCount * sizeof(ConvexBrepBank));
                    long scratchAt = rangesAt + Align16((long)convexCount * sizeof(ConvexBrepRange));
                    long bytes = scratchAt + Align16(Math.Max(Math.Max(
                        CutSpatialSplitSearch.ScratchBytes(convexCount),
                        settings.cutConvexes ? CutSpatialSplitSearch.MeshScratchBytes(convexCount, settings.limits) : 0),
                        settings.bentSearch ? CutSpatialSplitBentSearch.ScratchBytes(convexCount, maxConvexVertices, attempt.indexCount / 3) : 0));
                    attempt.block = PhysicsCutBlocks.Take<byte>(checked((int)bytes));
                    byte* block = (byte*)attempt.block.GetUnsafePtr();
                    attempt.output = (uint*)(block + outputAt);
                    attempt.submeshOffsets = (int*)(block + submeshInAt);
                    attempt.submeshCounts = attempt.submeshOffsets + s;
                    attempt.outSubmeshOffsets = (int*)(block + submeshOutAt);
                    attempt.outSubmeshCounts = attempt.outSubmeshOffsets + (2 * s);
                    attempt.outBoundsMin = (float3*)(block + boundsAt);
                    attempt.outBoundsMax = attempt.outBoundsMin + (2 * s);
                    attempt.banks = (ConvexBrepBank*)(block + banksAt);
                    attempt.ranges = (ConvexBrepRange*)(block + rangesAt);
                    attempt.scratch = block + scratchAt;
                    for (int i = 0; i < s; i++)
                    {
                        attempt.submeshOffsets[i] = submeshes[i].indexOffset;
                        attempt.submeshCounts[i] = submeshes[i].indexCount;
                        attempt.materials[i] = submeshes[i].materialIndex;
                    }

                    for (int c = 0; c < convexCount; c++)
                    {
                        attempt.banks[c] = shape.BankOf(c);
                        attempt.ranges[c] = shape.Convex(c);
                    }

                    attempt.classificationBlock = PhysicsCutBlocks.Take<byte>(classificationBytes);
                    attempt.arena = new PhysicsCutArena(default, convexCount);
                    _anchorScratch.Clear();
                    _world.Ledger.TryGetAnchors(fragment, _anchorScratch);
                    attempt.anchors = _anchorScratch.Count == 0 ? null : _anchorScratch.ToArray();

                    if (!_world.Dispatcher.TryEnqueue(WorkPurpose.Maintenance, attempt, out attempt.ticket))
                    {
                        // No place now: nothing was started. The fragment is looked at again in a later round.
                        QueueFull++;
                        attempt.Free(storage);
                        return Since(begin);
                    }
                }
                catch
                {
                    attempt.Free(storage);
                    throw;
                }

                attempt.offered = true;
                _attempt = attempt;
                Issued++;
            }

            double spent = Since(begin);
#if ZANTETSU_OBSERVE
            IssueSeconds += spent;
#endif
            _issueCosts.Add(spent);
            return spent;
        }

        // ---- the publication ------------------------------------------------------------------------------------------

        // Whether what was collected is to be published at all, and -- when it is not -- how the attempt ends and
        // whether the input is marked. Reads only.
        private bool ShouldPublish(Attempt attempt, out SpatialSplitEnd end, out bool mark)
        {
            mark = false;
            if (!attempt.counted)
            {
                // Once an attempt: this is asked again on every frame the publication is held.
                attempt.counted = true;
#if ZANTETSU_OBSERVE
                WorkerSeconds += attempt.workerSeconds;
                MaxWorkerSeconds = Math.Max(MaxWorkerSeconds, attempt.workerSeconds);
#endif
                LastSearch = attempt.search;
                if (attempt.search.meshLed) MeshLedSearches++;
                if (attempt.cut) CutAttempts++;
                if (attempt.search.bent)
                {
                    BentSearches++;
                    BentCandidates += attempt.search.bentCandidates;
                    BentElementsVerified += attempt.search.bentElementsVerified;
                    if (IsBentResult(in attempt.search)) BentFound++;
#if ZANTETSU_OBSERVE
                    BentSeconds += attempt.bentSeconds;
                    MaxBentSeconds = Math.Max(MaxBentSeconds, attempt.bentSeconds);
#endif
                }
            }

            if (attempt.stage == AttemptStage.Baking && !attempt.bakeCounted)
            {
                attempt.bakeCounted = true;
#if ZANTETSU_OBSERVE
                BakeSeconds += attempt.bakeSeconds;
                MaxBakeSeconds = Math.Max(MaxBakeSeconds, attempt.bakeSeconds);
#endif
            }

            if (attempt.cancelled || attempt.failure != null || !attempt.ran)
            {
                end = SpatialSplitEnd.NotRun;
                return false;
            }

            // A result is about the input it was computed from. If that is no longer the fragment's, nothing of it is
            // applied and nothing is recorded: not the success, and not the failure either.
            if (Examine(attempt.fragment, Settings, out Input now) != SpatialSplitPass.Candidate || !now.Equals(attempt.input))
            {
                end = SpatialSplitEnd.Stale;
                return false;
            }

            mark = true;
            switch (attempt.search.outcome)
            {
                case SpatialSplitSearchOutcome.Found:
                    break;
                case SpatialSplitSearchOutcome.NotFound:
                    end = SpatialSplitEnd.NotFound;
                    return false;
                case SpatialSplitSearchOutcome.OverTheLimits:
                    end = SpatialSplitEnd.OverTheLimits;
                    return false;
                default:
                    end = SpatialSplitEnd.NotClassified;
                    return false;
            }

            if (!attempt.distributed)
            {
                end = SpatialSplitEnd.NotFound;
                return false;
            }

            if (!attempt.classified)
            {
                end = SpatialSplitEnd.NotClassified;
                return false;
            }

            if (!attempt.anchorsDistributed || attempt.kernel.status != ConvexCutOwnerStatus.Ok || attempt.meshesBad)
            {
                end = SpatialSplitEnd.NotBuildable;
                return false;
            }

            end = SpatialSplitEnd.Published;
            mark = false;
            return true;
        }

        private unsafe void Publish(in CutSpatialSplitSettings settings)
        {
            long begin = Stopwatch.GetTimestamp();
            Attempt attempt = _attempt;
            LogicalFragmentId source = attempt.fragment;
            PhysicsFragmentOwner sourceOwner = attempt.input.owner;
            PhysicsOwnerShape sourceShape = attempt.input.shape;
            VpCpuGeometryStorage storage = _world.Storage;
            LogicalCutLedger ledger = _world.Ledger;
            PhysicsOwnerRegistry owners = _world.Owners;
            VpLogicalCutDisplay display = _world.Display;
            CutDag dag = _world.Geometry;

            using (s_publish.Auto())
            {
                // 1. The two index ranges over the fragment's own vertices: reserved, written and published here, in
                //    this stretch -- an open reservation is never left across a frame (it holds appends back).
                int s = attempt.submeshCount;
                if (!storage.TryReserveCutOutput(
                        attempt.input.geometry, 0, attempt.indexCount, 2 * s, attempt.input.geometry.blockCount,
                        out VpCutOutputReservation reservation))
                {
                    LastRefusal = "index, submesh or vertex block room in the storage";
                    End(SpatialSplitEnd.RoomShort, false);
                    return;
                }

                // The display's room, asked while the reservation can still go back whole: the two ranges are not
                // published for a split it would refuse for room.
                if (!display.HasRoomForSplit(source, reservation.IndexStart + attempt.indexCount, 2 * s, out string room))
                {
                    storage.TryCancelCutOutput(reservation);
                    LastRefusal = room;
                    End(SpatialSplitEnd.RoomShort, false);
                    return;
                }

                VpStoredGeometry positiveGeometry;
                VpStoredGeometry negativeGeometry;
                try
                {
                    NativeArray<uint> written = reservation.NewIndices;
                    UnsafeUtility.MemCpy(written.GetUnsafePtr(), attempt.output, (long)attempt.indexCount * sizeof(uint));
                    var submeshes = new VpGeometrySubmesh[2 * s];
                    var bounds = new VpGeometryBounds[2 * s];
                    for (int i = 0; i < 2 * s; i++)
                    {
                        submeshes[i] = new VpGeometrySubmesh(attempt.outSubmeshOffsets[i], attempt.outSubmeshCounts[i], attempt.materials[i % s]);
                        bounds[i] = new VpGeometryBounds((Vector3)attempt.outBoundsMin[i], (Vector3)attempt.outBoundsMax[i]);
                    }

                    if (!storage.TryCommitCutOutput(
                            reservation, 0, attempt.input.geometry.topologyVertexCount,
                            attempt.distribution.positiveIndexCount, attempt.distribution.negativeIndexCount,
                            submeshes, s, s, bounds,
                            attempt.distribution.positiveReferencedLo, attempt.distribution.positiveReferencedHi,
                            attempt.distribution.negativeReferencedLo, attempt.distribution.negativeReferencedHi,
                            out positiveGeometry, out negativeGeometry))
                    {
                        storage.TryCancelCutOutput(reservation);
                        End(SpatialSplitEnd.RoomShort, false);
                        return;
                    }
                }
                catch
                {
                    storage.TryCancelCutOutput(reservation);
                    throw;
                }

                // From here the two ranges are published and nobody's: every way out before the switch retires them.
                PhysicsCutProducts products = null;
                PhysicsOwnerCandidate candidate = null;
                PhysicsShapeSource productsSource = null;
                PhysicsOwnerShape positiveShape = null;
                PhysicsOwnerShape negativeShape = null;
                bool switched = false;
                try
                {
                    // 2. The display's judgement of the two registrations, with nothing changed. Its room was asked
                    //    above; what can still refuse here is a side it cannot place or prepare.
                    if (!display.TryPrepareSplit(source, positiveGeometry, negativeGeometry, out string refusal))
                    {
                        LastRefusal = refusal;
                        End(SpatialSplitEnd.RoomShort, false);
                        return;
                    }

                    // 3. The two owners, built and not in the scene: every convex inherited whole, with the masses
                    //    the worker integrated over each side's convexes (the parent's mass shared out between them).
                    long buildBegin = Began();
                    // (An attempt that cut convexes built its meshes with the cooking it baked them by: that one.)
                    products = attempt.TakeProducts(
                        attempt.cut ? attempt.cooking : _world.Cook != null ? _world.Cook.Cooking : PhysicsCutCook.DefaultCooking);
                    PhysicsOwnerPlacement placement = sourceOwner.ReadPlacement();
                    PhysicsOwnerMotion motion = sourceOwner.ReadMotion(placement.position);
                    var build = new PhysicsOwnerBuildInput
                    {
                        products = products,
                        placement = placement,
                        sourceMotion = motion,
                        anchors = attempt.anchorResult,
                        parentMass = attempt.parentMass,
                        inheritedShape = sourceShape,
                        childLineage = sourceOwner.Building.ChildOfSplit(),
                        name = "Split Owner",
                    };
                    if (!placement.IsFinite || !motion.IsFinite)
                    {
                        // Where the object stands now says nothing of what it is made of: not a failure of the input.
#if ZANTETSU_OBSERVE
                        BuildSeconds += Since(buildBegin);
#endif
                        End(SpatialSplitEnd.NotRun, false);
                        return;
                    }

                    if (!PhysicsOwnerBuilder.TryBuild(in build, out candidate, out PhysicsOwnerBuildOutcome built))
                    {
                        LastRefusal = "the two owners could not be built: " + built;
#if ZANTETSU_OBSERVE
                        BuildSeconds += Since(buildBegin);
#endif
                        End(SpatialSplitEnd.NotBuildable, true);
                        return;
                    }

                    productsSource = PhysicsShapeSource.For(products);
                    productsSource.Acquire();
                    positiveShape = PhysicsOwnerShape.OfSide(sourceShape, products, productsSource, true);
                    negativeShape = PhysicsOwnerShape.OfSide(sourceShape, products, productsSource, false);
                    Matrix4x4? displayFrame = sourceOwner.GeometryLocalToOwner;
                    var positiveOwner = new PhysicsFragmentOwner(
                        candidate.Positive.Root, candidate.Positive.Body, positiveShape, candidate.Positive.FixedByAnchors,
                        displayFrame, candidate.ChildLineage, candidate.Positive.BuildingWorld);
                    var negativeOwner = new PhysicsFragmentOwner(
                        candidate.Negative.Root, candidate.Negative.Body, negativeShape, candidate.Negative.FixedByAnchors,
                        displayFrame, candidate.ChildLineage, candidate.Negative.BuildingWorld);
                    owners.Reserve(2);

                    // Where the source is now and how it moves now; no impulse, no offset. Then into the scene.
                    float3 normalWorld = math.mul(placement.rotation, attempt.search.planeOwner.xyz);
                    candidate.Reposition(placement, in motion, normalWorld, 0f, 0f);
                    if (!Establish(candidate))
                    {
                        Withdraw(candidate);
#if ZANTETSU_OBSERVE
                        BuildSeconds += Since(buildBegin);
#endif
                        End(SpatialSplitEnd.NotBuildable, true);
                        return;
                    }

#if ZANTETSU_OBSERVE
                    BuildSeconds += Since(buildBegin);
#endif

                    // 4. The switch. Nothing below is refused: the ledger was asked (MayReplaceBySplit, in the
                    //    examination just made), the registry has its room, the display has judged.
                    long switchBegin = Began();
                    if (!ledger.ReplaceBySplit(source, attempt.positiveAnchors, attempt.negativeAnchors,
                            out LogicalFragmentId positive, out LogicalFragmentId negative))
                    {
                        Withdraw(candidate);
                        End(SpatialSplitEnd.Stale, false);
                        return;
                    }

                    switched = true;
                    productsSource.TakeOwnership();
                    owners.Add(positive, positiveOwner);
                    owners.Add(negative, negativeOwner);
                    candidate.Detach();

                    // The existing OwnerRetiring notification can tell this retirement from any other: the fragment
                    // being replaced is named for exactly as long as its owner retires.
                    ReplacingFragment = source;
                    try
                    {
                        owners.Retire(source);
                    }
                    finally
                    {
                        ReplacingFragment = default;
                    }

                    _publishedAt = placement.position;
                    if (!dag.TrySplitGeometry(source, positive, positiveGeometry, negative, negativeGeometry))
                    {
                        throw new InvalidOperationException("the cut DAG did not hand a split fragment's geometry on although it was read from it");
                    }

                    long transferredBefore = display.SplitIndicesTransferred;
                    display.CommitSplit(source, positive, negative);
#if ZANTETSU_OBSERVE
                    SwitchSeconds += Since(switchBegin);
#endif

                    Published++;
                    if (attempt.cut)
                    {
                        PublishedWithCut++;
                        ConvexesCut += attempt.meshCount / 2;
                    }

                    if (IsBentResult(in attempt.search)) PublishedBent++;

                    ActorsAdded += 1;          // two actors where one stood
                    RegistrationsAdded += 1;   // two registrations where one stood (the old one goes at the next adoption)
                    IndicesTransferred += display.SplitIndicesTransferred - transferredBefore;
                    LastPublishedFrame = Time.frameCount;
                    LastSource = source;
                    LastPositive = positive;
                    LastNegative = negative;
                    _followCollectOf = Time.frameCount + 1;   // this frame's collection follows this turn; its time is read by the next turn
                    _followIsCut = attempt.cut;
                    _suppressed.Remove(source);
                }
                finally
                {
                    if (!switched)
                    {
                        // Nothing was switched: what this stretch made goes back, and the fragment stands as it was.
                        storage.TryRetireIndices(positiveGeometry.indexRange);
                        storage.TryRetireIndices(negativeGeometry.indexRange);
                        positiveShape?.Dispose();
                        negativeShape?.Dispose();
                        candidate?.Dispose();
                    }

                    // This call's own hold on the products, let go once however it ended. Switched, the ownership
                    // moved: where every part is inherited nothing of the two shapes reads them and letting go
                    // gives them back; where convexes were cut the two shapes hold them for their new parts and
                    // they go with the last of those. Not switched, they were still this call's and go back here.
                    productsSource?.Release();
                    if (!switched)
                    {
                        products?.Dispose();
                    }
                }

                End(SpatialSplitEnd.Published, false);
            }

            double spent = Since(begin);
            if (attempt.cut)
            {
#if ZANTETSU_OBSERVE
                CutPublishSeconds += spent;
                MaxCutPublishTurnSeconds = Math.Max(MaxCutPublishTurnSeconds, spent);
#endif
                _cutPublishCosts.Add(spent);
            }
            else
            {
                _publishCosts.Add(spent);
#if ZANTETSU_OBSERVE
                MaxPublishTurnSeconds = Math.Max(MaxPublishTurnSeconds, spent);
#endif
            }

            if (LastEnd == SpatialSplitEnd.Published && LastPublishedFrame == Time.frameCount)
            {
                Note(attempt, spent);
            }
        }

        /// <summary>
        /// The fragment a publication is replacing, set only while its owner retires (the registry's OwnerRetiring
        /// notification): how a reader of retirements knows this one from a cut's, a collection's or a lifetime's.
        /// </summary>
        internal LogicalFragmentId ReplacingFragment { get; private set; }

        // A publication, kept as numbers where it happened and written out by LogEvents: a line written in the
        // frame of a publication would be part of what that frame cost.
        private struct SplitEvent
        {
            internal int frame, source, positive, negative, root, indices, positiveTriangles, negativeTriangles, positiveConvexes, negativeConvexes, convexesCut, bentPieces;
            internal float3 at, normal;
            internal float gap;
            internal double workerSeconds, turnSeconds;
        }

        /// <summary>How many publications are kept for <see cref="LogEvents"/>; past it they are counted only.</summary>
        public const int EventRoom = 4096;

        /// <summary>Publications kept for <see cref="LogEvents"/>, and those past <see cref="EventRoom"/> that were not.</summary>
        public int EventsKept => _events.Count;
        private int _eventsLogged;
        public int EventsNotKept { get; private set; }

        private void Note(Attempt attempt, double turnSeconds)
        {
            if (_events.Count >= EventRoom)
            {
                EventsNotKept++;
                return;
            }

            _world.Ledger.TryGetRoot(LastSource, out LogicalFragmentId root);
            _events.Add(new SplitEvent
            {
                frame = LastPublishedFrame, source = LastSource.value, positive = LastPositive.value, negative = LastNegative.value, root = root.value,
                indices = attempt.indexCount,
                positiveTriangles = attempt.search.positiveTriangles, negativeTriangles = attempt.search.negativeTriangles,
                positiveConvexes = attempt.search.positiveConvexes, negativeConvexes = attempt.search.negativeConvexes,
                convexesCut = attempt.cut ? attempt.meshCount / 2 : 0,
                bentPieces = IsBentResult(in attempt.search) ? attempt.search.bentPieces : 0,
                at = _publishedAt, normal = attempt.search.planeOwner.xyz, gap = attempt.search.gap,
                workerSeconds = attempt.workerSeconds, turnSeconds = turnSeconds,
            });
        }

        /// <summary>
        /// One log line a publication, in the order they happened, with its frame and where the object stood. Said by
        /// the world at its end, not in the frames the publications happened in.
        /// </summary>
        public void LogEvents()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            // Each publication is written once: a run that writes them at its own end (a device run never reaches the
            // world's) and then ends its world writes only the later ones there.
            for (; _eventsLogged < _events.Count; _eventsLogged++)
            {
                SplitEvent e = _events[_eventsLogged];
                UnityEngine.Debug.Log(
                    "SPATIAL SPLIT: fragment " + e.source + " (of the object of fragment " + e.root + ") became " + e.positive + " and " + e.negative + " at frame " + e.frame
                    + ", standing at (" + e.at.x.ToString("F2", inv) + ", " + e.at.y.ToString("F2", inv) + ", " + e.at.z.ToString("F2", inv) + "): triangles "
                    + e.positiveTriangles + " + " + e.negativeTriangles + ", convexes " + e.positiveConvexes + " + " + e.negativeConvexes + ", " + e.indices
                    + " indices transferred; the plane's normal in the owner (" + e.normal.x.ToString("F3", inv) + ", " + e.normal.y.ToString("F3", inv) + ", "
                    + e.normal.z.ToString("F3", inv) + "), the gap " + e.gap.ToString("F4", inv) + " m; worker " + (e.workerSeconds * 1000.0).ToString("F3", inv)
                    + " ms, the publishing turn " + (e.turnSeconds * 1000.0).ToString("F3", inv) + " ms"
                    + (e.convexesCut > 0 ? "; convexes cut by the plane " + e.convexesCut : "")
                    + (e.bentPieces > 0 ? "; a bent partition of " + e.bentPieces + " pieces (the normal is its height axis, the gap its least clearance)" : ""));
            }

            if (EventsNotKept > 0)
            {
                UnityEngine.Debug.Log("SPATIAL SPLIT: " + EventsNotKept + " more publications were counted and not kept (room for " + EventRoom + ")");
            }
        }

        /// <summary>Why the display last refused a split's two registrations, for the record.</summary>
        public string LastRefusal { get; private set; }

        private static bool Establish(PhysicsOwnerCandidate candidate)
        {
            for (int i = 0; i < 2; i++)
            {
                PhysicsOwnerSide side = candidate.Side(i == 0);
                if (side == null || side.Root == null || side.Body == null)
                {
                    return false;
                }

                side.Root.SetActive(true);
                if (!side.TryApplyToBody(out MassPropertiesRefusal _))
                {
                    return false;
                }
            }

            return candidate.Positive.Root.activeInHierarchy && candidate.Negative.Root.activeInHierarchy;
        }

        private static void Withdraw(PhysicsOwnerCandidate candidate)
        {
            for (int i = 0; i < 2; i++)
            {
                PhysicsOwnerSide side = candidate.Side(i == 0);
                if (side?.Root != null)
                {
                    side.Root.SetActive(false);
                }
            }
        }

        // The attempt is over: counted, marked when it is a failure of a current input, and everything it held given back.
        private void End(SpatialSplitEnd end, bool mark)
        {
            Attempt attempt = _attempt;
            if (attempt == null)
            {
                return;
            }

            _attempt = null;
            _ends[(int)end]++;
            LastEnd = end;
            if (mark)
            {
                _suppressed[attempt.fragment] = attempt.input;
            }

            if (end == SpatialSplitEnd.RoomShort || end == SpatialSplitEnd.NotRun || end == SpatialSplitEnd.Stale)
            {
                // Nothing was recorded about the input, and what the walk watches need not change for it to be
                // tried again (a stale result usually comes with such a change, and then this adds nothing): the
                // round it belongs to is followed by another, and a wait already begun ends.
                _roundWanting = true;
                _idle = false;
            }

            attempt.Free(_world.Storage);
        }

        // The collection that followed a publication, read a turn later (it ran after that turn, in the same frame).
        private void FollowPublication()
        {
            if (_followCollectOf != Time.frameCount)
            {
                return;
            }

            _followCollectOf = int.MinValue;
            double seconds = CutPhysicsStep.LastCollectSeconds;
            CollectionsAfterPublication++;
#if ZANTETSU_OBSERVE
            CollectAfterSeconds += seconds;
            MaxCollectAfterSeconds = Math.Max(MaxCollectAfterSeconds, seconds);
#endif
            (_followIsCut ? _cutCollectAfter : _collectAfter).Add(seconds);
        }

        /// <summary>
        /// The collections that reflected a publication (the publication's own frame, after its turn): how many
        /// were read, their time in all and the longest. Apart from the publishing turn's own time.
        /// </summary>
        public int CollectionsAfterPublication { get; private set; }
        public double CollectAfterSeconds { get; private set; }
        public double MaxCollectAfterSeconds { get; private set; }

        // A mark is about a live fragment's input. A fragment that is gone takes its mark with it (once a round).
        private void ForgetGoneSuppressions()
        {
            if (_suppressed.Count == 0)
            {
                return;
            }

            _suppressedScratch.Clear();
            foreach (KeyValuePair<LogicalFragmentId, Input> pair in _suppressed)
            {
                if (!_world.Ledger.IsCurrentTarget(pair.Key))
                {
                    _suppressedScratch.Add(pair.Key);
                }
            }

            for (int i = 0; i < _suppressedScratch.Count; i++)
            {
                _suppressed.Remove(_suppressedScratch[i]);
            }
        }

        // ---- holds ----------------------------------------------------------------------------------------------------

        /// <summary>The last budget comparison a turn made, with the values it used (unit 10-B); none before any.</summary>
        public SpatialSplitBudgetDecision LastBudgetDecision { get; private set; } = SpatialSplitBudgetDecision.None;

        private SpatialSplitHold HoldNow(in CutSpatialSplitSettings settings, double expectedSeconds, double laterStepSeconds, SpatialSplitBudgetStage stage)
        {
            switch (_world.FrameEventsHold(out _))
            {
                case CutFrameHold.HeavyWork: return SpatialSplitHold.HeavyWork;
                case CutFrameHold.GarbageCollection: return SpatialSplitHold.GarbageCollection;
                case CutFrameHold.CutInFlight: return SpatialSplitHold.CutInFlight;
            }

            if (_world.Ledger.Revision != _revisionSeen)
            {
                return SpatialSplitHold.StructureChanged;
            }

            if (_world.DisplayRoomSerial() != _roomSeen)
            {
                return SpatialSplitHold.DisplayRoomGrown;
            }

            // The turn runs before this frame's decision to simulate: what that step is expected to take is not the
            // turn's to spend. Added as it is -- it is the step's own estimate, which its decision compares once.
            // The remaining time is read once; the comparison and the record use that same reading.
            double remaining = RemainingMainSeconds();
            bool held = remaining < settings.reserveSeconds + laterStepSeconds + (2.0 * expectedSeconds);
            var decision = new SpatialSplitBudgetDecision(Time.frameCount, stage, remaining, settings.reserveSeconds, laterStepSeconds, expectedSeconds, held);
            LastBudgetDecision = decision;
            NoteBudgetDecision(_attempt != null ? _attempt.serial : 0, in decision);
            return held ? SpatialSplitHold.BudgetShort : SpatialSplitHold.None;
        }

        /// <summary>The run of held budget comparisons going on now (or the last one, ended), for one attempt at one stage.</summary>
        public SpatialSplitHoldStreak CurrentHoldStreak { get; private set; }

        /// <summary>The run that ended before <see cref="CurrentHoldStreak"/> began, and the longest run of the world so far.</summary>
        public SpatialSplitHoldStreak PreviousHoldStreak { get; private set; }
        public SpatialSplitHoldStreak LongestHoldStreak { get; private set; }

        // A comparison's place in the runs: a new attempt or stage ends the run before and begins another; a held one
        // lengthens the run and keeps the largest margin; one that went on ends the run.
        private void NoteBudgetDecision(long attempt, in SpatialSplitBudgetDecision decision)
        {
            SpatialSplitHoldStreak current = CurrentHoldStreak;
            bool same = current.IsSet && !current.Ended && current.Attempt == attempt && current.Stage == decision.Stage;
            if (current.IsSet && !current.Ended && !same)
            {
                PreviousHoldStreak = current.EndedAs(false);
                current = default;
            }

            if (decision.Held)
            {
                if (!same) current = new SpatialSplitHoldStreak(attempt, decision.Stage, decision.Frame, decision.Frame, 0, SpatialSplitBudgetDecision.None, false, false);
                current = current.Held(in decision);
                CurrentHoldStreak = current;
                if (current.Holds > LongestHoldStreak.Holds) LongestHoldStreak = current;
                return;
            }

            if (same)
            {
                PreviousHoldStreak = current.EndedAs(true);
                CurrentHoldStreak = PreviousHoldStreak;
                if (PreviousHoldStreak.Holds >= LongestHoldStreak.Holds) LongestHoldStreak = PreviousHoldStreak;
            }
        }

        private void Remember()
        {
            _revisionSeen = _world.Ledger.Revision;
            _roomSeen = _world.DisplayRoomSerial();
        }

        // ---- the record -----------------------------------------------------------------------------------------------

        /// <summary>The run's tallies in one line, for the world's end.</summary>
        public string Describe() => Describe(true);

        /// <summary>The run's tallies; without the convex cut's and the bent partition's own parts where they are written on lines of their own.</summary>
        public string Describe(bool withParts)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder(512);
            text.Append("spatial split (").Append(Settings.enabled ? "on" : "off").Append("): turns ").Append(Turns)
                .Append(" (and ").Append(IdleTurns).Append(" idle, in ").Append(IdleWaits).Append(" waits)")
                .Append(", examined ").Append(Examined).Append(" over ").Append(Rounds).Append(" rounds, issued ").Append(Issued)
                .Append(" (no place ").Append(QueueFull).Append("), published ").Append(Published)
                .Append("; ended:");
            for (int e = 0; e < (int)SpatialSplitEnd.Count; e++)
            {
                if (_ends[e] != 0) text.Append(' ').Append((SpatialSplitEnd)e).Append(' ').Append(_ends[e]);
            }

            text.Append("; passed:");
            for (int p = 1; p < (int)SpatialSplitPass.Count; p++)
            {
                if (_passes[p] != 0) text.Append(' ').Append((SpatialSplitPass)p).Append(' ').Append(_passes[p]);
            }

            text.Append("; held:");
            for (int h = 1; h < (int)SpatialSplitHold.Count; h++)
            {
                if (_holds[h] != 0) text.Append(' ').Append((SpatialSplitHold)h).Append(' ').Append(_holds[h]);
            }

            text.Append("; marked not-again now ").Append(SuppressedNow)
                .Append("; worker ms ").Append((WorkerSeconds * 1000.0).ToString("F3", inv)).Append(" (longest ").Append((MaxWorkerSeconds * 1000.0).ToString("F3", inv))
                .Append("); Main ms: walk ").Append((WalkSeconds * 1000.0).ToString("F3", inv))
                .Append(" (longest turn ").Append((MaxWalkTurnSeconds * 1000.0).ToString("F3", inv))
                .Append(", of it round starts ").Append((RoundSeconds * 1000.0).ToString("F3", inv))
                .Append(", cut short for the budget ").Append(WalksCutShort)
                .Append("), issue ").Append((IssueSeconds * 1000.0).ToString("F3", inv))
                .Append(", build ").Append((BuildSeconds * 1000.0).ToString("F3", inv))
                .Append(", switch ").Append((SwitchSeconds * 1000.0).ToString("F3", inv))
                .Append(", longest publishing turn ").Append((MaxPublishTurnSeconds * 1000.0).ToString("F3", inv))
                .Append("; the collections after a publication: ").Append(CollectionsAfterPublication).Append(", ")
                .Append((CollectAfterSeconds * 1000.0).ToString("F3", inv)).Append(" ms (longest ").Append((MaxCollectAfterSeconds * 1000.0).ToString("F3", inv)).Append(")")
                .Append("; indices transferred ").Append(IndicesTransferred).Append(" (").Append(IndicesTransferred * sizeof(uint)).Append(" B)")
                .Append("; actors added ").Append(ActorsAdded).Append(", draw registrations added ").Append(RegistrationsAdded)
                .Append("; last publication at frame ").Append(LastPublishedFrame == int.MinValue ? "-" : LastPublishedFrame.ToString(inv));
            if (withParts)
            {
                text.Append("; ").Append(DescribeConvexCut()).Append("; ").Append(DescribeBent());
            }

            return text.ToString();
        }

        /// <summary>The bent partition's tallies, the last part of <see cref="Describe"/>, as a text of its own (a device's log cuts a long line short).</summary>
        public string DescribeBent()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            SpatialSplitBentLimits l = Settings.bentLimits;
            var text = new StringBuilder(256);
            text.Append("bent partition (").Append(Settings.bentSearch
                    ? "on: frames " + l.maxFrames.ToString(inv) + ", seeds " + l.maxSeeds.ToString(inv) + ", candidates " + l.maxCandidates.ToString(inv)
                      + ", iterations " + l.maxIterations.ToString(inv) + ", pieces " + l.maxPieces.ToString(inv)
                    : "off")
                .Append("): searches ").Append(BentSearches).Append(", found ").Append(BentFound).Append(", published ").Append(PublishedBent)
                .Append(", candidates read with triangles ").Append(BentCandidates).Append(", elements verified ").Append(BentElementsVerified)
                .Append("; worker ms ").Append((BentSeconds * 1000.0).ToString("F3", inv)).Append(" (longest ").Append((MaxBentSeconds * 1000.0).ToString("F3", inv)).Append(')');
            return text.ToString();
        }

        /// <summary>
        /// The convex-cut branch's tallies, the last part of <see cref="Describe"/>, as a text of its own: a log that
        /// cuts a long line short (a device's) loses the end of the whole record.
        /// </summary>
        public string DescribeConvexCut()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder(512);
            text.Append("convex cut (").Append(Settings.cutConvexes ? "on, at " + Settings.minCutSideTriangles.ToString(inv) + " triangles or more a side" : "off")
                .Append("): mesh-led searches ").Append(MeshLedSearches).Append(", attempts with convexes to cut ").Append(CutAttempts)
                .Append(", published ").Append(PublishedWithCut).Append(" (convexes cut ").Append(ConvexesCut)
                .Append("), meshes made ").Append(CutMeshesMade).Append(", hulls read ").Append(HullsChecked).Append(" (refused ").Append(HullsRefused)
                .Append("), turns held with such an attempt left alone ").Append(CutTurnsLeftAlone).Append(", meshes that could not be made ").Append(CutMeshFailures)
                .Append("; bake worker ms ").Append((BakeSeconds * 1000.0).ToString("F3", inv)).Append(" (longest ").Append((MaxBakeSeconds * 1000.0).ToString("F3", inv))
                .Append("); Main ms: meshes ").Append((CutApplySeconds * 1000.0).ToString("F3", inv)).Append(" (longest ").Append((MaxCutApplySeconds * 1000.0).ToString("F3", inv))
                .Append("), hulls ").Append((HullCheckSeconds * 1000.0).ToString("F3", inv)).Append(" (longest turn ").Append((MaxHullCheckTurnSeconds * 1000.0).ToString("F3", inv))
                .Append("), publishing turns ").Append((CutPublishSeconds * 1000.0).ToString("F3", inv)).Append(" (longest ").Append((MaxCutPublishTurnSeconds * 1000.0).ToString("F3", inv)).Append(")");
            return text.ToString();
        }

        /// <summary>
        /// Whether a search's result is the bent partition's: it went through the bent search, found, and the mesh-led
        /// plane (searched after it, when it found nothing) is not what found.
        /// </summary>
        internal static bool IsBentResult(in SpatialSplitSearchResult search)
        {
            return search.bent && search.outcome == SpatialSplitSearchOutcome.Found && !search.meshLed;
        }

        /// <summary>
        /// The margin everything keeps from the plane: twice the larger of the profile's two epsilons, so that the
        /// scan a cut classifies convexes with (beyond the support epsilon) and the anchors' rule (beyond the anchor
        /// epsilon) both find what the search found, with as much again to spare. No tolerance of this path's own.
        /// </summary>
        public static float MarginOf(CutWorldProfile profile)
        {
            return profile == null ? 0f : 2f * math.max(profile.SupportEpsilon, profile.AnchorEpsilon);
        }

        private static double Since(long begin)
        {
            return (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
        }

        private static long Align16(long bytes)
        {
            return (bytes + 15) & ~15L;
        }

        /// <summary>What a fragment is made of, as far as a split reads it: an input is the same while all of it is.</summary>
        private readonly struct Input : IEquatable<Input>
        {
            internal Input(PhysicsFragmentOwner owner, PhysicsOwnerShape shape, VpStoredGeometry geometry, int anchorCount, int epoch)
            {
                this.owner = owner;
                this.shape = shape;
                this.geometry = geometry;
                this.anchorCount = anchorCount;
                this.epoch = epoch;
            }

            // Which search the input is an input of: the settings' as they were (a mark, or a result, of another is not this one's).
            internal readonly int epoch;

            internal readonly PhysicsFragmentOwner owner;
            internal readonly PhysicsOwnerShape shape;
            internal readonly VpStoredGeometry geometry;
            internal readonly int anchorCount;

            // A published geometry has descriptors and blocks of its own, so these name it; compared field by
            // field, because a struct's own Equals would box on every frame an attempt waits.
            public bool Equals(Input other)
            {
                return ReferenceEquals(owner, other.owner) && ReferenceEquals(shape, other.shape) && anchorCount == other.anchorCount
                    && epoch == other.epoch
                    && geometry.submeshStart == other.geometry.submeshStart && geometry.submeshCount == other.geometry.submeshCount
                    && geometry.blockStart == other.geometry.blockStart && geometry.blockCount == other.geometry.blockCount
                    && geometry.vertexStart == other.geometry.vertexStart && geometry.vertexCount == other.geometry.vertexCount
                    && geometry.topologyVertexCount == other.geometry.topologyVertexCount;
            }
        }

        /// <summary>Which of its two runs on a worker an attempt is at: the numbers, or the bake of the meshes made from them.</summary>
        private enum AttemptStage
        {
            Numbers = 0,
            Baking,
        }

        /// <summary>
        /// The one attempt: what it was issued for, what the worker reads and writes, and what it found. It is the
        /// work itself; <see cref="Begin"/> runs on a background worker and touches nothing of Unity's scene. An
        /// attempt that cuts convexes is offered a second time, to bake the meshes the main thread made of its
        /// numbers: one Mesh is in one bake, and neither it nor the shape and blocks the numbers read is given back
        /// before the work that uses it was collected.
        /// </summary>
        private sealed unsafe class Attempt : IDispatchWork
        {
            internal long serial;   // the attempt's number in this world, from 1 (diagnosis: the hold streak's key)
            internal LogicalFragmentId fragment;
            internal Input input;
            internal VpIndexReadLease lease;
            internal bool holdsLease;
            internal bool holdsShape;
            internal WorkTicket ticket;
            internal bool offered, collected, cancelled, abandoned, counted;
            internal Exception failure;
            internal int issuedFrame;

            // Read by the worker.
            internal VpRenderVertex* vertices;
            internal int vertexViewLength;
            internal uint* indices;
            internal int indexCount, submeshCount, convexCount;
            internal int* submeshOffsets, submeshCounts;
            internal int[] materials;
            internal ConvexBrepBank* banks;
            internal ConvexBrepRange* ranges;
            internal float4x4 geometryToOwner, shapeToOwner;
            internal float3[] anchors;
            internal double parentMass;
            internal int vertexLimit;
            internal float supportEpsilon, anchorEpsilon, margin;
            internal SpatialSplitSearchLimits limits;
            internal bool cutConvexes;
            internal int minCutSideTriangles;

            // The bent partition: whether to look for one after the planes, its limits, the most vertices one convex
            // has (the search's scratch), and the search's own time on the worker.
            internal bool bentSearch;
            internal SpatialSplitBentLimits bentLimits;
            internal int maxConvexVertices;
            internal double bentSeconds;

            // The convex-cut branch. Set by the worker's numbers: whether a convex is cut. Then the main thread's:
            // the meshes it made of the produced convexes (positive then negative, a cut convex after another), their
            // ids and boxes, the cooking they are baked by, and how far the reading of the cooked hulls has come.
            internal AttemptStage stage;
            internal bool cut, applied, meshesBad, bakeCounted;
            internal string meshFailure;
            internal Mesh[] meshes;
            internal int[] meshIds;
            internal float3x2[] meshBounds;
            internal int meshCount, meshesMade, hullsChecked;
            internal MeshColliderCookingOptions cooking;
            internal double bakeSeconds;

            // Written by the worker.
            internal NativeArray<byte> block;
            internal NativeArray<byte> classificationBlock;
            internal PhysicsCutArena arena;
            internal byte* scratch;
            internal uint* output;
            internal int* outSubmeshOffsets, outSubmeshCounts;
            internal float3* outBoundsMin, outBoundsMax;
            internal SpatialSplitSearchResult search;
            internal CutSpatialSplitSearch.Distribution distribution;
            internal bool ran, distributed, classified, anchorsDistributed;
            internal PhysicsCutClassification classification;
            internal ConvexCutOwnerResult kernel;
            internal AnchorDistributionResult anchorResult;
            internal List<float3> positiveAnchors, negativeAnchors;
            internal double workerSeconds;
            private int _done;

            public bool IsComplete => Volatile.Read(ref _done) == 1;

            public void Begin()
            {
                if (stage == AttemptStage.Baking)
                {
                    Bake();
                    return;
                }

                long begin = Began();
                try
                {
                    CutSpatialSplitSearch.FindPlane(
                        vertices, vertexViewLength, indices, indexCount, in geometryToOwner,
                        banks, ranges, convexCount, in shapeToOwner, margin, in limits, scratch, ref search);
                    ran = true;
                    if (search.outcome == SpatialSplitSearchOutcome.NotFound && bentSearch && convexCount >= 2)
                    {
                        // No plane leaves the convexes and the triangles whole: a surface of a few plane pieces that
                        // does (DESIGN 7.9.2, the bent partition), before any convex is cut. An input that is invalid,
                        // not finite or over the limits is no reason to look further, and an owner of one convex has
                        // nothing for the other side.
                        long bentBegin = Began();
                        CutSpatialSplitBentSearch.FindBentSurface(
                            vertices, vertexViewLength, indices, indexCount, in geometryToOwner,
                            banks, ranges, convexCount, in shapeToOwner, margin, in limits, in bentLimits, scratch, ref search);
#if ZANTETSU_OBSERVE
                        bentSeconds = Since(bentBegin);
#endif
                    }

                    if (search.outcome == SpatialSplitSearchOutcome.NotFound && cutConvexes)
                    {
                        // Nothing leaves the convexes whole: a plane that leaves the triangles whole is looked for,
                        // and the convexes that cross it are cut. (The bent partition's counts stay in the record.)
                        CutSpatialSplitSearch.FindMeshPlane(
                            vertices, vertexViewLength, indices, indexCount, in geometryToOwner,
                            banks, ranges, convexCount, in shapeToOwner, margin, minCutSideTriangles, in limits, scratch, ref search);
                    }

                    if (search.outcome != SpatialSplitSearchOutcome.Found)
                    {
                        return;
                    }

                    if (IsBentResult(in search))
                    {
                        BeginBent();
                        return;
                    }

                    distributed = CutSpatialSplitSearch.Distribute(
                        vertices, vertexViewLength, indices, indexCount, submeshOffsets, submeshCounts, submeshCount,
                        search.planeGeometry, margin, output, outSubmeshOffsets, outSubmeshCounts, outBoundsMin, outBoundsMax,
                        ref distribution);
                    if (!distributed)
                    {
                        return;
                    }

                    // One plane for the anchors and the convexes, as a cut gives the ledger and the scan the same one:
                    // in the shape's numerical frame, which is the frame the owner's anchors are kept in.
                    float4 planeShape = search.planeShape / math.length(search.planeShape.xyz);

                    // The anchors, by the existing rule.
                    positiveAnchors = new List<float3>();
                    negativeAnchors = new List<float3>();
                    anchorsDistributed = FixedSupportAnchors.TryDistribute(
                        anchors, planeShape, anchorEpsilon, positiveAnchors, negativeAnchors, out anchorResult);
                    if (!anchorsDistributed)
                    {
                        return;
                    }

                    // The convexes once more, by the scan a cut uses and its own epsilon: none may be split and none
                    // may lie near the plane. Its input is the mass kernel's.
                    NativeArray<byte> given = classificationBlock;
                    classificationBlock = default;   // the classification's from here, on every answer
                    if (!PhysicsCutClassification.TryClassifyInto(
                            input.shape, planeShape, supportEpsilon, parentMass, vertexLimit, given, out classification))
                    {
                        return;
                    }

                    int positive = 0;
                    int negative = 0;
                    int split = 0;
                    bool mayCut = cutConvexes && search.meshLed && search.crossingConvexes > 0;
                    IReadOnlyList<ConvexSide> sides = classification.Sides;
                    for (int c = 0; c < sides.Count; c++)
                    {
                        if (sides[c] == ConvexSide.Positive) positive++;
                        else if (sides[c] == ConvexSide.Negative) negative++;
                        else if (!mayCut) return;
                        else if (sides[c] == ConvexSide.Split) split++;
                        else positive++;   // within the cut's own epsilon of the plane: whole, to the positive side, as a cut gives it
                    }

                    ConvexCutOwnerInput kernelInput = classification.Input;
                    if (split > 0)
                    {
                        // Convexes are cut: something of the physics on each side is what the cut's own scan says, and
                        // the room for what the clip and the reduction write is the kernel's own worst case for these
                        // signs -- known only now, so the arena is taken here.
                        if (!classification.SplitsBothSides)
                        {
                            return;
                        }

                        var capacity = default(ConvexCutOwnerCapacity);
                        ConvexCutOwnerKernel.QueryCapacity(in kernelInput, ref capacity);
                        arena.Dispose();
                        arena = new PhysicsCutArena(in capacity, convexCount);
                        cut = true;
                    }
                    else if (positive == 0 || negative == 0)
                    {
                        return;
                    }

                    classified = true;
                    ConvexCutOwnerOutput kernelOutput = arena.Output;
                    ConvexCutOwnerKernel.Execute(in kernelInput, in kernelOutput, ref kernel);
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
#if ZANTETSU_OBSERVE
                    workerSeconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
#endif
                    Volatile.Write(ref _done, 1);
                }
            }

            // The bent partition's sharing out, anchors and classification (DESIGN 7.9.2): every side is what the
            // search verified over the element's whole extent -- the triangles by the same test, the anchors by
            // their signed distance to the surface (a length, as the plane's is), the convexes by the sides the
            // search wrote -- and no plane stands for the surface anywhere. Nothing is cut.
            private void BeginBent()
            {
                distributed = CutSpatialSplitBentSearch.DistributeBent(
                    vertices, vertexViewLength, indices, indexCount, submeshOffsets, submeshCounts, submeshCount,
                    in geometryToOwner, in search.bentSurface, margin, convexCount, maxConvexVertices, scratch,
                    output, outSubmeshOffsets, outSubmeshCounts, outBoundsMin, outBoundsMax, ref distribution);
                if (!distributed)
                {
                    return;
                }

                // The owner's anchors are kept in the shape's numerical frame; the surface is in the owner's.
                positiveAnchors = new List<float3>();
                negativeAnchors = new List<float3>();
                var distances = new List<float>(anchors != null ? anchors.Length : 0);
                if (anchors != null)
                {
                    for (int a = 0; a < anchors.Length; a++)
                    {
                        distances.Add(search.bentSurface.SignedDistance(search.bentSurface.Project(math.transform(shapeToOwner, anchors[a]))));
                    }
                }

                anchorsDistributed = FixedSupportAnchors.TryDistribute(anchors, distances, anchorEpsilon, positiveAnchors, negativeAnchors, out anchorResult);
                if (!anchorsDistributed)
                {
                    return;
                }

                NativeArray<byte> given = classificationBlock;
                classificationBlock = default;   // the classification's from here, on every answer
                if (!PhysicsCutClassification.TryClassifyWithSides(
                        input.shape, CutSpatialSplitBentSearch.SidesOf(scratch), convexCount, parentMass, vertexLimit, given, out classification)
                    || !classification.SplitsBothSides)
                {
                    return;
                }

                classified = true;
                ConvexCutOwnerInput kernelInput = classification.Input;
                ConvexCutOwnerOutput kernelOutput = arena.Output;
                ConvexCutOwnerKernel.Execute(in kernelInput, in kernelOutput, ref kernel);
            }

            // The second run: PhysX cooks each mesh the main thread made, with the cooking the colliders will ask
            // for. Plain C# on the pool's worker, as the design has an optional split's bake; no Unity job.
            private void Bake()
            {
                long begin = Began();
                try
                {
                    for (int m = 0; m < meshCount; m++)
                    {
                        Physics.BakeMesh(meshIds[m], true, cooking);
                    }
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
#if ZANTETSU_OBSERVE
                    bakeSeconds = (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;
#endif
                    Volatile.Write(ref _done, 1);
                }
            }

            /// <summary>Makes the work one that was not offered yet, for its next run. Main thread, after it was collected.</summary>
            internal void Reset(AttemptStage next)
            {
                stage = next;
                offered = false;
                collected = false;
                cancelled = false;
                ticket = default;
                Volatile.Write(ref _done, 0);
            }

            /// <summary>
            /// One Mesh a produced convex, as a cut's job writes its own: positions alone, no index, the box measured
            /// while they are copied. Main thread, after the numbers were collected with convexes cut, and once: a
            /// failure on the way leaves <see cref="meshesBad"/> set with every mesh made so far still held here --
            /// the attempt's to give back with the rest (<see cref="Free"/>) -- and nothing is made a second time.
            /// </summary>
            internal void MakeMeshes()
            {
                if (meshes != null || meshesBad)
                {
                    return;
                }

                ConvexCutOutcome* outcomes = arena.Output.outcomes;
                int count = 0;
                for (int c = 0; c < convexCount; c++)
                {
                    if (outcomes[c].IsSplit) count += 2;
                }

                meshes = new Mesh[count];
                meshIds = new int[count];
                meshBounds = new float3x2[count];
                meshCount = count;
                meshesMade = 0;
                try
                {
                    int m = 0;
                    for (int c = 0; c < convexCount; c++)
                    {
                        if (!outcomes[c].IsSplit) continue;
                        MakeMesh(m++, outcomes[c].positive);
                        MakeMesh(m++, outcomes[c].negative);
                    }
                }
                catch (Exception e)
                {
                    meshesBad = true;
                    meshFailure = e.Message;
                }
            }

            private void MakeMesh(int m, in ConvexBrepRange range)
            {
                int n = range.vertexCount;
                if (n <= 0)
                {
                    throw new InvalidOperationException("the produced convex " + m + " has no vertex");
                }

                if (m == FailMeshAtForTest)
                {
                    throw new InvalidOperationException("refused for a test at mesh " + m);
                }

                const UnityEngine.Rendering.MeshUpdateFlags Quiet = UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds | UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices | UnityEngine.Rendering.MeshUpdateFlags.DontNotifyMeshUsers;
                // Held in the attempt's table before anything is set up on it: whatever fails below, it is given back.
                var mesh = new Mesh { name = "Zantetsu Spatial Split " + fragment.value + "." + m, hideFlags = HideFlags.HideAndDontSave };
                meshes[m] = mesh;
                float3* source = arena.Bank.vertices + range.vertexBase;
                var positions = new NativeArray<float3>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
                try
                {
                    var lower = new float3(float.MaxValue);
                    var upper = new float3(float.MinValue);
                    for (int i = 0; i < n; i++)
                    {
                        float3 p = source[i];
                        positions[i] = p;
                        lower = math.min(lower, p);
                        upper = math.max(upper, p);
                    }

                    var box = new Bounds((lower + upper) * 0.5f, upper - lower);
                    mesh.SetVertexBufferParams(n, new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.Position, UnityEngine.Rendering.VertexAttributeFormat.Float32, 3));
                    mesh.SetVertexBufferData(positions, 0, 0, n, 0, Quiet);
                    mesh.SetIndexBufferParams(0, UnityEngine.Rendering.IndexFormat.UInt16);
                    mesh.subMeshCount = 1;
                    mesh.SetSubMesh(0, new UnityEngine.Rendering.SubMeshDescriptor(0, 0, MeshTopology.Triangles) { bounds = box, firstVertex = 0, vertexCount = n }, Quiet);
                    mesh.bounds = box;
                    meshBounds[m] = new float3x2(lower, upper);
                    meshIds[m] = mesh.GetEntityId();
                }
                finally
                {
                    positions.Dispose();
                }

                meshesMade++;
            }

            public void Collect(WorkCompletion completion)
            {
                collected = true;
                cancelled = completion.outcome == WorkOutcome.Cancelled;
                if (completion.outcome == WorkOutcome.Failed && failure == null)
                {
                    failure = completion.failure;
                }

                if (abandoned)
                {
                    // The world let it go while it ran: given back here, where the main thread collects it.
                    Free(null);
                }
            }

            /// <summary>
            /// The finished split as a cut's products are made: a convex inherited uncut by the side it lies on, and
            /// -- where convexes were cut -- each cut one's two parts with the meshes made and baked for them. The
            /// arena, with its outcomes, and those meshes are the products' from here.
            /// </summary>
            internal PhysicsCutProducts TakeProducts(MeshColliderCookingOptions cooking)
            {
                var outcomes = new ConvexCutOutcome[convexCount];
                int positive = 0;
                int negative = 0;
                for (int c = 0; c < convexCount; c++)
                {
                    outcomes[c] = arena.Output.outcomes[c];
                    if (outcomes[c].IsSplit)
                    {
                        positive++;
                        negative++;
                    }
                    else if (outcomes[c].InheritsPositive) positive++;
                    else negative++;
                }

                var products = new PhysicsCutProducts(arena, outcomes, in kernel, shapeToOwner, cooking, positive, negative);
                arena = null;
                int m = 0;
                for (int c = 0; c < convexCount; c++)
                {
                    if (!outcomes[c].IsSplit)
                    {
                        products.Add(
                            outcomes[c].InheritsPositive,
                            new PhysicsCutPart(c, true, ranges[c], null, new float3x2(new float3(float.NaN), new float3(float.NaN))));
                        continue;
                    }

                    products.Add(true, new PhysicsCutPart(c, false, outcomes[c].positive, meshes[m], meshBounds[m]));
                    m++;
                    products.Add(false, new PhysicsCutPart(c, false, outcomes[c].negative, meshes[m], meshBounds[m]));
                    m++;
                }

                meshes = null;   // the products' from here: destroyed with them
                return products;
            }

            /// <summary>Gives back everything the attempt holds, once. Main thread.</summary>
            internal void Free(VpCpuGeometryStorage storage)
            {
                // Meshes made and not handed to products: the bake that read them was collected, or never began.
                if (meshes != null)
                {
                    for (int m = 0; m < meshes.Length; m++)
                    {
                        if (meshes[m] != null) UnityEngine.Object.Destroy(meshes[m]);
                    }

                    meshes = null;
                }

                classification?.Dispose();
                classification = null;
                if (classificationBlock.IsCreated) classificationBlock.Dispose();
                classificationBlock = default;
                arena?.Dispose();
                arena = null;
                if (block.IsCreated) block.Dispose();
                block = default;
                vertices = null;
                indices = null;
                output = null;
                if (holdsShape)
                {
                    holdsShape = false;
                    input.shape.ReleaseFromWork();
                }

                // Without a storage (an attempt the world let go while it ran, collected after the world's own
                // release): the lease went with the storage it was a lease of.
                if (holdsLease && storage != null)
                {
                    storage.TryReleaseIndexReadLease(lease);
                }

                holdsLease = false;
                lease = default;
            }
        }
    }
}
