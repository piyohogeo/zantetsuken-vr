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
    /// The settings of the convex trim (<see cref="CutConvexTrim"/>). The two lengths are provisional values of the first
    /// version (2026-11-20), kept here so that an evaluation changes them in one place.
    /// </summary>
    [Serializable]
    public struct CutConvexTrimSettings
    {
        /// <summary>Off by default: an evaluation's switch (<see cref="CutWorldRoot.ConvexTrimArgument"/>).</summary>
        public bool enabled;

        /// <summary>Metres. A body is looked at when two or more of the three extents of its whole collision box, along axes fixed to the fragment, are above this.</summary>
        public float minSizeMeters;

        /// <summary>Metres. A convex is a candidate in a direction when it reaches this far or further beyond the mesh's support plane there.</summary>
        public float minOverhangMeters;

        /// <summary>Cubic metres, and a part of the convex's volume before its first trim: what is kept of a convex has at least both.</summary>
        public float minKeptVolume;
        public float minKeptVolumeRatio;

        /// <summary>Fragments looked at in a frame at most.</summary>
        public int examinePerFrame;

        /// <summary>Seconds of the frame's Main budget left alone, as the additional split's.</summary>
        public double reserveSeconds;

        /// <summary>Seconds: what each Main stage is taken to cost until it was measured.</summary>
        public double seedExamineSeconds, seedIssueSeconds, seedCollectSeconds, seedMeshSeconds, seedHullCheckSeconds, seedPublishSeconds;

        /// <summary>The most bytes one attempt's working block may take; a fragment that needs more is passed over.</summary>
        public int maxBlockBytes;

        /// <summary>Seconds: a frame in which the trim's whole turn took this or more is recorded with what it held (<see cref="ConvexTrimSlowFrame"/>). A reading only.</summary>
        public double slowFrameSeconds;

        public static CutConvexTrimSettings Default => new CutConvexTrimSettings
        {
            enabled = false,
            minSizeMeters = 0.2f,
            minOverhangMeters = 0.1f,
            minKeptVolume = 1e-6f,
            minKeptVolumeRatio = 0.01f,
            examinePerFrame = 16,
            reserveSeconds = 0.0,
            seedExamineSeconds = 0.00001,
            seedIssueSeconds = 0.0002,
            seedCollectSeconds = 0.0001,
            seedMeshSeconds = 0.0005,
            seedHullCheckSeconds = 0.0002,
            seedPublishSeconds = 0.0015,
            maxBlockBytes = 8 << 20,
            slowFrameSeconds = 0.0005,
        };

        public bool IsValid => minSizeMeters > 0f && minOverhangMeters > 0f && minKeptVolume >= 0f && minKeptVolumeRatio >= 0f && minKeptVolumeRatio < 1f
            && examinePerFrame >= 1 && reserveSeconds >= 0.0 && seedExamineSeconds >= 0.0 && seedIssueSeconds >= 0.0 && seedCollectSeconds >= 0.0 && seedMeshSeconds >= 0.0
            && seedHullCheckSeconds >= 0.0 && seedPublishSeconds >= 0.0 && maxBlockBytes > 0;
    }

    /// <summary>What a look at one fragment said (<see cref="CutConvexTrim.Examine(LogicalFragmentId)"/>).</summary>
    public enum ConvexTrimPass
    {
        /// <summary>A trim may be issued for it.</summary>
        Candidate = 0,
        NotLive,

        /// <summary>A cut of it is under way (accepted, or a Provisional pair stands in for it): looked at again later.</summary>
        CutUnderWay,

        /// <summary>No cut made it: an authored body is not a fragment.</summary>
        NotCutMade,

        /// <summary>No body and colliders of its own (withdrawn, fused, display only).</summary>
        NoOwnPhysics,
        Building,

        /// <summary>Its convexes carry bones or mesh frames of their own (an NPC's piece): not trimmed in this version.</summary>
        NotPlain,

        /// <summary>The shape's or the Root's frame stretches: lengths there are not metres.</summary>
        FrameNotRigid,

        /// <summary>Its display does not follow its owner: the mesh cannot be put beside the convexes.</summary>
        NoGeometryFrame,

        /// <summary>Fewer than two extents above the size: marked for this shape, never looked at again while it stands.</summary>
        SizeShort,

        /// <summary>Its geometry is not committed yet: looked at again when it is.</summary>
        GeometryOpen,

        /// <summary>It has no geometry at all (an empty side).</summary>
        NoGeometry,

        /// <summary>This mesh and this shape were tried already, whatever came of it.</summary>
        Done,
        Count,
    }

    /// <summary>How a trim attempt ended.</summary>
    public enum ConvexTrimEnd
    {
        /// <summary>Convexes were trimmed and the owner has the new shape and colliders.</summary>
        Published = 0,

        /// <summary>No convex reaches beyond a support plane by the threshold: below the threshold everywhere.</summary>
        BelowThreshold,

        /// <summary>There were candidates and every one was left alone (the outcome's reasons).</summary>
        AllRefused,

        /// <summary>The fragment, its shape or its mesh moved on while the attempt was out: thrown away, looked at again.</summary>
        Stale,

        /// <summary>Cancelled, switched off, the world ending, or nothing readable: thrown away.</summary>
        NotRun,

        /// <summary>The worker or a Main stage failed with an exception; the owner stands as it was.</summary>
        Failed,

        /// <summary>PhysX made no convex of a trimmed convex's mesh; the owner stands as it was.</summary>
        CookFailed,

        /// <summary>A collider of a trimmed convex was not found, or the new shape could not be made; the owner stands as it was.</summary>
        NotBuildable,
        Count,
    }

    /// <summary>What holds a Main stage of the trim for a frame.</summary>
    public enum ConvexTrimHold
    {
        None = 0,
        HeavyWork,
        GarbageCollection,
        CutInFlight,
        BudgetShort,

        /// <summary>The additional split comes first: its turn did not end with nothing to do, or it has work waiting for the main thread.</summary>
        SplitFirst,
        Count,
    }

    /// <summary>The trim's times, each kept apart (<see cref="CutConvexTrim.Cost"/>).</summary>
    public enum ConvexTrimCostKind
    {
        /// <summary>Worker: the mesh's support planes and the candidates.</summary>
        WorkerSearch = 0,

        /// <summary>Worker: the clips, tried or adopted.</summary>
        WorkerClip,

        /// <summary>Worker: PhysX cooking the trimmed meshes.</summary>
        WorkerBake,

        /// <summary>Main: one look at a fragment.</summary>
        Examine,

        /// <summary>Main: an issue (the lease, the block, the queue).</summary>
        Issue,

        /// <summary>Main: taking the numbers back and deciding what becomes of them.</summary>
        Collect,

        /// <summary>Main: the Mesh objects of the trimmed convexes.</summary>
        Meshes,

        /// <summary>Main: asking PhysX what it made of each mesh.</summary>
        HullCheck,

        /// <summary>Main: the new shape, the colliders, the owner.</summary>
        Publish,

        /// <summary>Worker: from the work's offer to the queue to its beginning on a worker (the search's and the bake's; the pool is the additional split's too).</summary>
        WorkerWait,

        /// <summary>
        /// Main: the trim's whole turn of a frame in which it did work -- a look, an issue, a step of an attempt -- by the
        /// clock from the turn's beginning to its end (the holds it asked and everything between the measured stages are in
        /// it). A sample a frame that had work; frames without are <see cref="MainIdleFrame"/>.
        /// </summary>
        MainFrame,

        /// <summary>Main: the trim's whole turn of a frame in which it did no work (it was held, or had nothing to look at).</summary>
        MainIdleFrame,
        Count,
    }

    /// <summary>The samples of one of the trim's times: how many, their sum, their median and the longest.</summary>
    public sealed class ConvexTrimCost
    {
        private const int Most = 4096;
        private readonly List<double> _samples = new List<double>(32);
        private double[] _sorted;

        public int Count { get; private set; }
        public double TotalSeconds { get; private set; }
        public double MaxSeconds { get; private set; }

        /// <summary>Of every sample (not only those kept for the median): those of 0.5 ms or more, of 1 ms or more, of 2 ms or more.</summary>
        public int OverHalfMs { get; private set; }
        public int OverOneMs { get; private set; }
        public int OverTwoMs { get; private set; }

        internal void Add(double seconds)
        {
            if (!(seconds >= 0.0) || double.IsInfinity(seconds)) return;
            Count++;
            TotalSeconds += seconds;
            if (seconds > MaxSeconds) MaxSeconds = seconds;
            if (seconds >= 0.0005) OverHalfMs++;
            if (seconds >= 0.001) OverOneMs++;
            if (seconds >= 0.002) OverTwoMs++;
            if (_samples.Count < Most) _samples.Add(seconds);
            _sorted = null;
        }

        /// <summary>The median of the samples kept (the first 4096), 0 with none.</summary>
        public double MedianSeconds
        {
            get
            {
                if (_samples.Count == 0) return 0.0;
                if (_sorted == null)
                {
                    _sorted = _samples.ToArray();
                    Array.Sort(_sorted);
                }

                int n = _sorted.Length;
                return (n & 1) == 1 ? _sorted[n / 2] : 0.5 * (_sorted[(n / 2) - 1] + _sorted[n / 2]);
            }
        }
    }

    /// <summary>The parts of the trim's Main work a slow frame's time is told by.</summary>
    public enum ConvexTrimFramePart
    {
        Look,
        IssueLease,
        IssueSetUp,
        IssueEnqueue,
        Collect,
        Mesh,
        HullCheck,
        PrepareColliders,
        PrepareShape,
        SwitchColliders,
        SwitchShape,
        SwitchRest,

        /// <summary>An attempt's end: its record and marks; what listens to the record (a diagnosis's log); giving back what the attempt held.</summary>
        EndRecord,
        EndListeners,
        EndFree,
        Count,
    }

    /// <summary>
    /// A frame in which the trim's whole turn took <see cref="CutConvexTrimSettings.slowFrameSeconds"/> or more: which
    /// frame, the whole turn by the clock and the sum of its measured stages, which stages and the time of each part,
    /// what was left of the frame's Main budget before and after, what else the frame held (heavy work noted in it, a
    /// garbage collection, cuts in flight, the additional split's attempts) and what the attempt was.
    /// </summary>
    public sealed class ConvexTrimSlowFrame
    {
        public int frame;
        public double wholeSeconds, stagesSeconds;
        public CutConvexTrim.FrameStages stages;
        public double[] partSeconds;
        public double remainingBeforeSeconds, remainingAfterSeconds, laterStepSeconds;

        /// <summary>The frame's hold as the world tells it after the turn ("None", "HeavyWork", "GarbageCollection", "CutInFlight"), and the heavy work it names.</summary>
        public string hold, heavyWork;

        /// <summary>Garbage collections completed in the frame as the world counts them, and generation-0 collections begun during the turn itself.</summary>
        public int gcThisFrame, gcDuringTurn;

        /// <summary>The frame's own length as the engine gives it (the time since the frame before).</summary>
        public float frameDeltaSeconds;
        public int cutsInFlight;
        public bool splitHasAttempt;
        public int splitWaitingMain, splitFound;

        /// <summary>The attempt the turn worked on (0: none, looks alone): its fragment, convexes, trimmed meshes, the mesh's indices, the working block's bytes.</summary>
        public int fragment, convexes, meshes, indexCount, blockBytes;
    }

    /// <summary>One trim attempt as it ended: what it looked at, what it changed, what it cost.</summary>
    public sealed class ConvexTrimRecord
    {
        public long serial;
        public LogicalFragmentId fragment;
        public ConvexTrimEnd end;
        public string note;
        public int issuedFrame, endedFrame;
        public int convexes, indexCount;

        /// <summary>The whole collision box's three extents along the fragment's axes, metres.</summary>
        public float3 extents;

        /// <summary>Per convex, in the shape's convex order: the B-rep's vertices before and after (the bake's input), and the trims adopted.</summary>
        public int[] verticesBefore, verticesAfter, trims;
        public int verticesBeforeInAll, verticesAfterInAll;

        /// <summary>The largest reach beyond a support plane in the 26 directions, metres, before and after.</summary>
        public float overhangBefore, overhangAfter;
        public int candidatesBefore, clipsTried, trimsInAll, trimmedConvexes;

        /// <summary>Candidates left at the end by <see cref="ConvexTrimRefusal"/>.</summary>
        public int[] left = new int[(int)ConvexTrimRefusal.Count];
        public double workerSearchSeconds, workerClipSeconds, workerBakeSeconds;
        public double issueSeconds, collectSeconds, meshSeconds, hullCheckSeconds, publishSeconds;
    }

    /// <summary>
    /// The convex trim (2026-11-20, off by default): where a convex of a large fragment reaches beyond its own drawn mesh,
    /// the part beyond is cut off, so that what a piece rests on is nearer to what is seen of it -- a felled tree's branch
    /// held in the air by its hull.
    /// <para>
    /// **The plane.** The fragment's committed geometry gives, in each of 26 directions fixed to the fragment, the plane
    /// that leaves every vertex its indices name on one side (the support plane). A convex that reaches beyond one by the
    /// threshold or more is clipped there, the furthest reach first, where what is kept is a convex of some volume within
    /// the vertex limit (<see cref="ConvexTrimSearch"/>). The whole of it is worked out on a pool worker, for every convex
    /// and every trim of the attempt; the bake follows on a worker too.
    /// </para>
    /// <para>
    /// **After the additional split.** An attempt is issued only in a frame whose split turn ended with a whole scan and
    /// nothing to issue, no candidate waiting and no attempt out (<see cref="CutSpatialSplit.LeavesRoomForLowerPriority"/>);
    /// its Main stages wait where the split has work for the main thread, and for the frame's heavy work, a collection, a
    /// cut in flight and the budget as the split's stages do. One attempt at a time.
    /// </para>
    /// <para>
    /// **The publication.** The same fragment, the same Root and body: each trimmed convex's collider is given the baked
    /// mesh, and the owner the new shape, in one Main stretch (<see cref="PhysicsOwnerRegistry.ReplaceShape"/>). A hit and
    /// the next cut read the new shape from then on; the NPC planner's prop map takes it at its next gathering (it keeps
    /// its copies by shape); what the additional split decided of the shape before is void. A fragment cut again, retired
    /// or changed while the attempt was out throws the result away, and so does a mesh PhysX makes no convex of: the owner
    /// stands as it was.
    /// </para>
    /// <para>
    /// **The marks.** "Too small" is a mark of a shape (its box); "done" is a mark of a mesh and a shape together. A child
    /// of a cut or of a split is another shape and another mesh, and is looked at for itself; where its parent was too
    /// small in the same frame it is too small without a look (it lies inside its parent's box). A turn that the budget or
    /// the queue refused marks nothing.
    /// </para>
    /// </summary>
    public sealed unsafe class CutConvexTrim
    {
        private static readonly ProfilerMarker s_issue = new ProfilerMarker("Zantetsu.ConvexTrim.Issue");
        private static readonly ProfilerMarker s_meshes = new ProfilerMarker("Zantetsu.ConvexTrim.Meshes");
        private static readonly ProfilerMarker s_hulls = new ProfilerMarker("Zantetsu.ConvexTrim.Hulls");
        private static readonly ProfilerMarker s_publish = new ProfilerMarker("Zantetsu.ConvexTrim.Publish");

        /// <summary>Frames before a fragment passed over for a reason of the moment, or refused by the queue, is looked at again.</summary>
        public const int RetryFrames = 15;

        /// <summary>Records kept (the newest; older ones are dropped).</summary>
        public const int RecordsKept = 2048;

        private readonly CutWorldRoot _world;
        private readonly Dictionary<LogicalFragmentId, Mark> _marks = new Dictionary<LogicalFragmentId, Mark>();
        private readonly Queue<Pending> _pending = new Queue<Pending>();
        private readonly HashSet<LogicalFragmentId> _queued = new HashSet<LogicalFragmentId>();
        private readonly List<LogicalFragmentId> _seedScratch = new List<LogicalFragmentId>();
        private readonly List<MeshCollider> _colliderScratch = new List<MeshCollider>(16);
        private readonly List<ConvexTrimRecord> _records = new List<ConvexTrimRecord>();
        private readonly ConvexTrimCost[] _costs = new ConvexTrimCost[(int)ConvexTrimCostKind.Count];
        private readonly SimulateCostHistory _examineCosts = new SimulateCostHistory();
        private readonly SimulateCostHistory _issueCosts = new SimulateCostHistory();
        private readonly SimulateCostHistory _collectCosts = new SimulateCostHistory();
        private readonly SimulateCostHistory _meshCosts = new SimulateCostHistory();
        private readonly SimulateCostHistory _hullCosts = new SimulateCostHistory();
        private readonly SimulateCostHistory _publishCosts = new SimulateCostHistory();
        private readonly SimulateCostHistory _prepareCosts = new SimulateCostHistory();
        private readonly long[] _passes = new long[(int)ConvexTrimPass.Count];
        private readonly long[] _ends = new long[(int)ConvexTrimEnd.Count];
        private readonly long[] _holds = new long[(int)ConvexTrimHold.Count];
        private readonly long[] _left = new long[(int)ConvexTrimRefusal.Count];
        private Attempt _attempt;
        private long _serial;
        private bool _subscribed, _seeded;

        private enum Verdict : byte { SizeShort, Done }

        private struct Mark
        {
            internal Verdict verdict;
            internal PhysicsOwnerShape shape;
            internal VpIndexRangeHandle range;   // Done only: the mesh
            internal ConvexTrimEnd end;
        }

        private struct Pending
        {
            internal LogicalFragmentId fragment;
            internal int notBefore;
        }

        internal readonly struct Input
        {
            internal Input(PhysicsFragmentOwner owner, PhysicsOwnerShape shape, VpStoredGeometry geometry, float3 extents)
            {
                this.owner = owner;
                this.shape = shape;
                this.geometry = geometry;
                this.extents = extents;
            }

            internal readonly PhysicsFragmentOwner owner;
            internal readonly PhysicsOwnerShape shape;
            internal readonly VpStoredGeometry geometry;
            internal readonly float3 extents;

            internal bool Same(in Input other) =>
                ReferenceEquals(owner, other.owner) && ReferenceEquals(shape, other.shape) && geometry.indexRange.Equals(other.geometry.indexRange);
        }

        public CutConvexTrim(CutWorldRoot world, CutConvexTrimSettings settings)
        {
            _world = world != null ? world : throw new ArgumentNullException(nameof(world));
            Settings = settings;
            for (int i = 0; i < _costs.Length; i++) _costs[i] = new ConvexTrimCost();
            RemainingMainSeconds = () => CutPhysicsStep.FrameRemainingMainSeconds;
            LaterStepSeconds = () => CutPhysicsStep.ExpectedStepSecondsLaterThisFrame;
            SplitLeavesRoom = () => _world.SpatialSplit == null || _world.SpatialSplit.LeavesRoomForLowerPriority;
            SplitWaitsForMain = () => _world.SpatialSplit != null && _world.SpatialSplit.AttemptsWaitingMain > 0;
        }

        public CutConvexTrimSettings Settings { get; set; }

        /// <summary>What is left of the frame's Main budget, and the physics step still to come in it (the product's; a test gives its own).</summary>
        public Func<double> RemainingMainSeconds { get; set; }
        public Func<double> LaterStepSeconds { get; set; }

        /// <summary>Whether the additional split leaves this frame to the trim's issue, and whether it has work waiting for the main thread (the product's; a test gives its own).</summary>
        public Func<bool> SplitLeavesRoom { get; set; }
        public Func<bool> SplitWaitsForMain { get; set; }

        /// <summary>Tests: an issue finds no place in the queue; a bake finds none; the Mesh of this index fails to be made; the publication finds PhysX refused a mesh.</summary>
        internal static bool TestRefuseIssue;
        internal static bool TestRefuseBake;
        internal static int TestFailMeshAt = -1;
        internal static bool TestRefuseHull;

        /// <summary>Whether an attempt is out or waiting for the main thread.</summary>
        public bool HasAttempt => _attempt != null;

        /// <summary>Whether the attempt's work came back and its next step is the main thread's.</summary>
        internal bool AttemptWaitsForMain => _attempt != null && _attempt.collected;

        /// <summary>Fragments waiting to be looked at.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>Nothing to look at and nothing out.</summary>
        public bool IsIdle => _attempt == null && _pending.Count == 0;

        /// <summary>Turns that asked something of the frame, fragments looked at, attempts issued, and issues the queue refused.</summary>
        public long Turns { get; private set; }
        public long Examined { get; private set; }
        public long Issued { get; private set; }
        public long QueueFull { get; private set; }

        /// <summary>Issues whose geometry could not be read, and fragments whose block would have passed the limit.</summary>
        public long NotReadable { get; private set; }
        public long TooLarge { get; private set; }

        /// <summary>Owners whose shape was replaced, convexes trimmed and trims adopted, over every publication.</summary>
        public long Published { get; private set; }
        public long ConvexesTrimmed { get; private set; }
        public long TrimsAdopted { get; private set; }

        /// <summary>Per convex of the published attempts: more vertices than before, as many, fewer; and the largest increase of one convex.</summary>
        public long ConvexesGrew { get; private set; }
        public long ConvexesSame { get; private set; }
        public long ConvexesShrank { get; private set; }
        public int MostVerticesAdded { get; private set; }

        /// <summary>"Too small" taken from the parent without a look, and -- development builds -- those a look of their own did not agree with (never).</summary>
        public long SizeInherited { get; private set; }
        public long SizeInheritedDisagreed { get; private set; }

        /// <summary>
        /// Scans the budget cut short; the most turns in a row one step of an attempt waited for the frame's budget (it
        /// waits however long: nothing is let through a budget that is short); the longest single step of an attempt on
        /// the main thread; bodies woken at a publication.
        /// </summary>
        public long ScansCutShort { get; private set; }
        public int MostBudgetHolds { get; private set; }
        public double MostMainStepSeconds { get; private set; }
        public long BodiesWoken { get; private set; }

        /// <summary>The most of the trim's Main work in one frame, and what that frame held (<see cref="FrameStages"/>).</summary>
        public double MostFrameMainSeconds { get; private set; }
        public FrameStages MostFrameStages { get; private set; }

        /// <summary>The trim's Main work a frame can hold.</summary>
        [Flags]
        public enum FrameStages { None = 0, Look = 1, Issue = 2, Collect = 4, Mesh = 8, HullCheck = 16, Prepare = 32, Switch = 64 }

        private double _frameMainSeconds;
        private FrameStages _frameStages;
        private readonly double[] _frameParts = new double[(int)ConvexTrimFramePart.Count];
        private int _frameFragment, _frameConvexes, _frameMeshes, _frameIndexCount, _frameBlockBytes;
        private readonly List<ConvexTrimSlowFrame> _slowFrames = new List<ConvexTrimSlowFrame>();

        /// <summary>Slow frames kept (the newest), and how many there were in all.</summary>
        public const int SlowFramesKept = 1024;
        public long SlowFrames { get; private set; }
        public IReadOnlyList<ConvexTrimSlowFrame> SlowFrameRecords => _slowFrames;

        /// <summary>Raised on the main thread with each slow frame's record (<see cref="ConvexTrimRecordLog"/> keeps them all).</summary>
        public event Action<ConvexTrimSlowFrame> SlowFrame;

        // The seconds of the end of an attempt inside the step being timed: told as the parts of the end, and so taken out
        // of the part of the step (a step that ends its attempt -- the numbers taken back with nothing to trim, a mesh
        // that fails, a hull refused -- would tell them twice otherwise).
        private double _stepEndSeconds;

        private void NoteMain(FrameStages stage, double seconds, double endSeconds = 0.0)
        {
            _frameMainSeconds += seconds;
            _frameStages |= stage;
            double own = Math.Max(0.0, seconds - endSeconds);
            switch (stage)
            {
                case FrameStages.Look: _frameParts[(int)ConvexTrimFramePart.Look] += own; break;
                case FrameStages.Collect: _frameParts[(int)ConvexTrimFramePart.Collect] += own; break;
                case FrameStages.Mesh: _frameParts[(int)ConvexTrimFramePart.Mesh] += own; break;
                case FrameStages.HullCheck: _frameParts[(int)ConvexTrimFramePart.HullCheck] += own; break;
            }
        }

        private void NotePart(ConvexTrimFramePart part, long from, long to)
        {
            if (to > from) _frameParts[(int)part] += (to - from) / (double)Stopwatch.Frequency;
        }

        private void NoteFrameAttempt(Attempt attempt)
        {
            _frameFragment = attempt.fragment.value;
            _frameConvexes = attempt.convexCount;
            _frameMeshes = attempt.meshCount;
            _frameIndexCount = attempt.indexCount;
            _frameBlockBytes = attempt.block.IsCreated ? attempt.block.Length : 0;
        }

        private void NoteSlowFrame(double whole, double remainingBefore, int gcDuring)
        {
            CutFrameHold hold = _world.FrameEventsHold(out string work);
            CutSpatialSplit split = _world.SpatialSplit;
            var slow = new ConvexTrimSlowFrame
            {
                frame = Time.frameCount, wholeSeconds = whole, stagesSeconds = _frameMainSeconds, stages = _frameStages, partSeconds = (double[])_frameParts.Clone(),
                remainingBeforeSeconds = remainingBefore, remainingAfterSeconds = RemainingMainSeconds(), laterStepSeconds = Later(),
                hold = hold.ToString(), heavyWork = work ?? string.Empty, gcThisFrame = CutPhysicsStep.GcCollectionsThisFrame, gcDuringTurn = gcDuring,
                frameDeltaSeconds = Time.unscaledDeltaTime, cutsInFlight = _world.Driver != null ? _world.Driver.Transactions.Count : 0,
                splitHasAttempt = split != null && split.HasAttempt, splitWaitingMain = split != null ? split.AttemptsWaitingMain : 0, splitFound = split != null ? split.FoundWaiting : 0,
                fragment = _frameFragment, convexes = _frameConvexes, meshes = _frameMeshes, indexCount = _frameIndexCount, blockBytes = _frameBlockBytes,
            };
            SlowFrames++;
            if (_slowFrames.Count >= SlowFramesKept) _slowFrames.RemoveAt(0);
            _slowFrames.Add(slow);
            SlowFrame?.Invoke(slow);
        }

        /// <summary>The newest record of an attempt on the fragment, if one is kept.</summary>
        public bool TryGetLastRecord(LogicalFragmentId fragment, out ConvexTrimRecord record)
        {
            for (int i = _records.Count - 1; i >= 0; i--)
            {
                if (_records[i].fragment != fragment) continue;
                record = _records[i];
                return true;
            }

            record = null;
            return false;
        }

        /// <summary>
        /// Where the fragment stands with the trim now, for a reading from outside (a capture): "InAttempt" (its attempt is
        /// out or waits for the main thread), "Queued" (it waits for a look), or what a look says (a
        /// <see cref="ConvexTrimPass"/>'s name) -- and for "Done" how the attempt that marked it ended. Leaves nothing but
        /// the "too small" mark a look leaves.
        /// </summary>
        public string StateOf(LogicalFragmentId fragment, out string markedEnd)
        {
            markedEnd = string.Empty;
            if (_attempt != null && _attempt.fragment == fragment) return "InAttempt";
            if (_queued.Contains(fragment)) return "Queued";
            ConvexTrimPass pass = Examine(fragment);
            if (pass == ConvexTrimPass.Done && _marks.TryGetValue(fragment, out Mark mark)) markedEnd = mark.end.ToString();
            return pass.ToString();
        }

        /// <summary>Frames a trim work was on a worker while the additional split had attempts out, and frames it was on a worker in all.</summary>
        public long FramesBesideSplit { get; private set; }
        public long FramesOut { get; private set; }

        /// <summary>Frames between an issue and its end, summed and the longest.</summary>
        public long FramesIssueToEnd { get; private set; }
        public int MostFramesIssueToEnd { get; private set; }

        public long PassedAs(ConvexTrimPass pass) => _passes[(int)pass];
        public long EndedAs(ConvexTrimEnd end) => _ends[(int)end];
        public long HeldFor(ConvexTrimHold hold) => _holds[(int)hold];

        /// <summary>Candidates left alone at the end of the attempts, by the reason, over every attempt that ran.</summary>
        public long LeftAs(ConvexTrimRefusal refusal) => _left[(int)refusal];
        public ConvexTrimCost Cost(ConvexTrimCostKind kind) => _costs[(int)kind];

        /// <summary>The attempts as they ended, the oldest first (the newest <see cref="RecordsKept"/>; <see cref="RecordsDropped"/> older ones are kept no more).</summary>
        public IReadOnlyList<ConvexTrimRecord> Records => _records;
        public long RecordsDropped { get; private set; }

        /// <summary>Raised on the main thread with each attempt's record as it ends: a listener that wants every attempt keeps them itself (<see cref="ConvexTrimRecordLog"/>).</summary>
        public event Action<ConvexTrimRecord> Recorded;

        /// <summary>The last attempt's end, and why where there is something to say.</summary>
        public ConvexTrimEnd LastEnd { get; private set; }
        public string LastNote { get; private set; }

        // ---- what is waited for ---------------------------------------------------------------------------------------

        private void Subscribe()
        {
            if (_subscribed || _world.Owners == null || _world.Geometry == null) return;
            _subscribed = true;
            _world.Owners.OwnerAdded += OnOwnerAdded;
            _world.Owners.OwnerRetiring += OnOwnerRetiring;
            _world.Owners.ProvisionalEnding += OnPairEnding;
            _world.Owners.OwnerShapeReplaced += OnShapeReplaced;
            _world.Geometry.GeometryChanged += OnGeometryChanged;
        }

        private void Unsubscribe()
        {
            if (!_subscribed) return;
            _subscribed = false;
            if (_world.Owners != null)
            {
                _world.Owners.OwnerAdded -= OnOwnerAdded;
                _world.Owners.OwnerRetiring -= OnOwnerRetiring;
                _world.Owners.ProvisionalEnding -= OnPairEnding;
                _world.Owners.OwnerShapeReplaced -= OnShapeReplaced;
            }

            if (_world.Geometry != null) _world.Geometry.GeometryChanged -= OnGeometryChanged;
        }

        // A child of a cut or of a split lies inside its parent's box where it has the parent's frame: a parent that was
        // too small makes it too small, with no look. Anything else is looked at for itself.
        private void OnOwnerAdded(LogicalFragmentId fragment, PhysicsFragmentOwner owner)
        {
            if (owner != null && owner.Shape != null && _world.Ledger != null && _world.Ledger.TryGetParent(fragment, out LogicalFragmentId parent)
                && _marks.TryGetValue(parent, out Mark mark) && mark.verdict == Verdict.SizeShort && mark.shape != null && !mark.shape.IsFreed
                && mark.shape.LocalToOwner.Equals(owner.Shape.LocalToOwner))
            {
                _marks[fragment] = new Mark { verdict = Verdict.SizeShort, shape = owner.Shape };
                SizeInherited++;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (owner.Shape.TryLocalBounds(out float3 lo, out float3 hi) && IsLarge(hi - lo, Settings.minSizeMeters)) SizeInheritedDisagreed++;
#endif
                return;
            }

            Enqueue(fragment, 0);
        }

        private void OnOwnerRetiring(LogicalFragmentId fragment, PhysicsFragmentOwner owner)
        {
            _marks.Remove(fragment);
        }

        private void OnPairEnding(ProvisionalOwnerPair pair)
        {
            if (pair != null) Enqueue(pair.Source, Time.frameCount + 1);   // a cut given up: the source is itself again
        }

        private void OnShapeReplaced(LogicalFragmentId fragment, PhysicsFragmentOwner owner, PhysicsOwnerShape had)
        {
            // The trim's own publication marks the new shape itself, right after this; anyone else's replacement would be
            // a shape nothing was decided of.
            Enqueue(fragment, 0);
        }

        private void OnGeometryChanged(LogicalFragmentId fragment)
        {
            if (_world.Owners == null || !_world.Owners.TryGet(fragment, out PhysicsFragmentOwner owner)) return;
            // "Too small" is of the shape alone: a mesh that comes or changes under the same shape asks for no look.
            if (_marks.TryGetValue(fragment, out Mark mark) && mark.verdict == Verdict.SizeShort && ReferenceEquals(mark.shape, owner.Shape)) return;
            Enqueue(fragment, 0);
        }

        private void Enqueue(LogicalFragmentId fragment, int notBefore)
        {
            if (!fragment.IsSet || !_queued.Add(fragment)) return;
            _pending.Enqueue(new Pending { fragment = fragment, notBefore = notBefore });
        }

        // Switched on in a world that has fragments already: every owner is looked at once.
        private void Seed()
        {
            if (_seeded || _world.Owners == null) return;
            _seeded = true;
            _seedScratch.Clear();
            _world.Owners.CopyFragmentsTo(_seedScratch);
            for (int i = 0; i < _seedScratch.Count; i++) Enqueue(_seedScratch[i], 0);
            _seedScratch.Clear();
        }

        // ---- the look -------------------------------------------------------------------------------------------------

        /// <summary>Looks at one fragment as the turn does: reads only, apart from the "too small" mark it leaves.</summary>
        public ConvexTrimPass Examine(LogicalFragmentId fragment)
        {
            return Examine(fragment, Settings, out _);
        }

        private static bool IsLarge(float3 extents, float minSize)
        {
            return (extents.x > minSize ? 1 : 0) + (extents.y > minSize ? 1 : 0) + (extents.z > minSize ? 1 : 0) >= 2;
        }

        private ConvexTrimPass Examine(LogicalFragmentId fragment, in CutConvexTrimSettings settings, out Input input)
        {
            input = default;
            LogicalCutLedger ledger = _world.Ledger;
            PhysicsOwnerRegistry owners = _world.Owners;
            CutDag dag = _world.Geometry;
            if (ledger == null || owners == null || dag == null || !ledger.IsCurrentTarget(fragment)) return ConvexTrimPass.NotLive;
            if (ledger.TryGetActiveOperation(fragment, out _) || owners.TryGetProvisionalOf(fragment, out _)) return ConvexTrimPass.CutUnderWay;
            if (!ledger.MayReplaceBySplit(fragment)) return ConvexTrimPass.NotCutMade;
            if (!owners.TryGet(fragment, out PhysicsFragmentOwner owner) || owner.IsWithdrawn || owner.IsReleased || owner.IsDisplayOnly || owner.IsFused
                || owner.Body == null || owner.Root == null
                || !owners.TryResolveFragment(owner.Body, out LogicalFragmentId resolved, out float side) || resolved != fragment || side != 0f)
            {
                return ConvexTrimPass.NoOwnPhysics;
            }

            if (owner.Building.IsBuildingDerived || owner.BuildingWorldConstraint != null) return ConvexTrimPass.Building;
            PhysicsOwnerShape shape = owner.Shape;
            if (shape == null || shape.IsFreed || shape.ConvexCount == 0) return ConvexTrimPass.NoOwnPhysics;
            if (_marks.TryGetValue(fragment, out Mark mark) && mark.verdict == Verdict.SizeShort && ReferenceEquals(mark.shape, shape)) return ConvexTrimPass.SizeShort;
            if (shape.HasConvexBones || (_world.NpcBones != null && _world.NpcBones.Knows(fragment))) return ConvexTrimPass.NotPlain;
            for (int c = 0; c < shape.ConvexCount; c++)
            {
                if (shape.MeshFrameOf(c).HasFrame) return ConvexTrimPass.NotPlain;
            }

            // Metres: the shape's frame is carried rigidly to the owner's, and nothing stretches the owner (its Root is
            // made unscaled and nothing scales it afterwards; read here all the same, once a look).
            float4x4 shapeToOwner = shape.LocalToOwner;
            Vector3 stretch = owner.RootTransform != null ? owner.RootTransform.lossyScale : Vector3.zero;
            if (!ConvexTrimSearch.IsRigid(in shapeToOwner) || math.any(math.abs((float3)stretch - 1f) > 1e-4f)) return ConvexTrimPass.FrameNotRigid;
            if (owner.GeometryLocalToOwner == null) return ConvexTrimPass.NoGeometryFrame;

            // The size, of the whole collision box along the axes fixed to the fragment (the shape's own frame).
            if (!shape.TryLocalBounds(out float3 lo, out float3 hi) || !IsLarge(hi - lo, settings.minSizeMeters))
            {
                _marks[fragment] = new Mark { verdict = Verdict.SizeShort, shape = shape };
                return ConvexTrimPass.SizeShort;
            }

            if (!dag.TryGetGeometry(fragment, out VpStoredGeometry geometry))
            {
                return dag.HasNoGeometry(fragment) ? ConvexTrimPass.NoGeometry : ConvexTrimPass.GeometryOpen;
            }

            if (_world.Storage == null || _world.Storage.IsDeferredPending(geometry)) return ConvexTrimPass.GeometryOpen;
            if (_marks.TryGetValue(fragment, out mark) && mark.verdict == Verdict.Done && ReferenceEquals(mark.shape, shape) && mark.range.Equals(geometry.indexRange))
            {
                return ConvexTrimPass.Done;
            }

            input = new Input(owner, shape, geometry, hi - lo);
            return ConvexTrimPass.Candidate;
        }

        // ---- the holds ------------------------------------------------------------------------------------------------

        private double Later()
        {
            double later = LaterStepSeconds != null ? LaterStepSeconds() : 0.0;
            return later > 0.0 && !double.IsInfinity(later) ? later : 0.0;
        }

        private ConvexTrimHold Hold(in CutConvexTrimSettings settings, double expectedSeconds)
        {
            switch (_world.FrameEventsHold(out _))
            {
                case CutFrameHold.HeavyWork: return ConvexTrimHold.HeavyWork;
                case CutFrameHold.GarbageCollection: return ConvexTrimHold.GarbageCollection;
                case CutFrameHold.CutInFlight: return ConvexTrimHold.CutInFlight;
            }

            return RemainingMainSeconds() < settings.reserveSeconds + Later() + (2.0 * expectedSeconds) ? ConvexTrimHold.BudgetShort : ConvexTrimHold.None;
        }

        private static double Expected(SimulateCostHistory history, double seed) => history.Count > 0 ? history.ExpectedSeconds : seed;

        public double ExpectedExamineSeconds => Expected(_examineCosts, Settings.seedExamineSeconds);
        public double ExpectedIssueSeconds => Expected(_issueCosts, Settings.seedIssueSeconds);
        public double ExpectedCollectSeconds => Expected(_collectCosts, Settings.seedCollectSeconds);
        public double ExpectedMeshSeconds => Expected(_meshCosts, Settings.seedMeshSeconds);
        public double ExpectedHullCheckSeconds => Expected(_hullCosts, Settings.seedHullCheckSeconds);
        public double ExpectedPrepareSeconds => Expected(_prepareCosts, Settings.seedPublishSeconds);
        public double ExpectedPublishSeconds => Expected(_publishCosts, Settings.seedPublishSeconds);

        private static double Since(long begin) => (Stopwatch.GetTimestamp() - begin) / (double)Stopwatch.Frequency;

        // ---- the turn -------------------------------------------------------------------------------------------------

        /// <summary>One turn, in the world's LateUpdate after the additional split's and before the frame's collection.</summary>
        public void Step()
        {
            long begin = Stopwatch.GetTimestamp();
            _frameMainSeconds = 0.0;
            _frameStages = FrameStages.None;
            Array.Clear(_frameParts, 0, _frameParts.Length);
            _frameFragment = _frameConvexes = _frameMeshes = _frameIndexCount = _frameBlockBytes = 0;
            int gcBefore = GC.CollectionCount(0);
            double remainingBefore = RemainingMainSeconds();
            StepTurn();
            double whole = Since(begin);
            if (_frameStages == FrameStages.None)
            {
                _costs[(int)ConvexTrimCostKind.MainIdleFrame].Add(whole);
                return;
            }

            _costs[(int)ConvexTrimCostKind.MainFrame].Add(whole);
            if (whole > MostFrameMainSeconds)
            {
                MostFrameMainSeconds = whole;
                MostFrameStages = _frameStages;
            }

            if (whole >= Settings.slowFrameSeconds) NoteSlowFrame(whole, remainingBefore, GC.CollectionCount(0) - gcBefore);
        }

        private void StepTurn()
        {
            CutConvexTrimSettings settings = Settings;
            bool on = settings.enabled && settings.IsValid && _world.IsReady && !_world.IsEnding && !_world.TerminationRequested;
            if (_attempt != null)
            {
                StepAttempt(in settings, on);
                return;
            }

            if (!on) return;
            Subscribe();
            Seed();
            if (_pending.Count == 0) return;   // nothing to look at: nothing is asked of the frame

            Turns++;
            // The additional split first: nothing is issued unless its turn of this frame ended with nothing to do.
            if (SplitLeavesRoom != null && !SplitLeavesRoom())
            {
                _holds[(int)ConvexTrimHold.SplitFirst]++;
                return;
            }

            ConvexTrimHold hold = Hold(in settings, ExpectedExamineSeconds + ExpectedIssueSeconds);
            if (hold != ConvexTrimHold.None)
            {
                _holds[(int)hold]++;
                return;
            }

            int frame = Time.frameCount;
            int left = _pending.Count, looked = 0;
            while (left-- > 0 && looked < settings.examinePerFrame)
            {
                if (looked > 0 && RemainingMainSeconds() < settings.reserveSeconds + Later() + (2.0 * (ExpectedExamineSeconds + ExpectedIssueSeconds)))
                {
                    ScansCutShort++;
                    break;
                }

                Pending pending = _pending.Dequeue();
                if (pending.notBefore > frame)
                {
                    _pending.Enqueue(pending);   // not yet: it keeps its place in the set
                    continue;
                }

                _queued.Remove(pending.fragment);
                looked++;
                long begin = Stopwatch.GetTimestamp();
                ConvexTrimPass pass = Examine(pending.fragment, in settings, out Input input);
                double seconds = Since(begin);
                _examineCosts.Add(seconds);
                _costs[(int)ConvexTrimCostKind.Examine].Add(seconds);
                NoteMain(FrameStages.Look, seconds);
                Examined++;
                _passes[(int)pass]++;
                if (pass == ConvexTrimPass.CutUnderWay || pass == ConvexTrimPass.GeometryOpen)
                {
                    Enqueue(pending.fragment, frame + RetryFrames);   // a reason of the moment: no mark
                    continue;
                }

                if (pass != ConvexTrimPass.Candidate) continue;
                if (!TryIssue(pending.fragment, in input, in settings, out bool passedOver) && !passedOver)
                {
                    Enqueue(pending.fragment, frame + RetryFrames);   // not readable now, or no place in the queue: not done
                }

                return;   // one issue a turn (one attempt at a time)
            }
        }

        private void StepAttempt(in CutConvexTrimSettings settings, bool on)
        {
            Attempt attempt = _attempt;
            if (!attempt.collected)
            {
                if (attempt.stage == AttemptStage.Baking && !attempt.offered)
                {
                    if (!on)
                    {
                        End(attempt, ConvexTrimEnd.NotRun, false, false, "switched off or the world ending: its bake was never offered");
                        return;
                    }

                    Turns++;
                    OfferBake(attempt, in settings);
                    return;
                }

                FramesOut++;
                if (_world.SpatialSplit != null && _world.SpatialSplit.HasAttempt) FramesBesideSplit++;
                return;
            }

            if (!on)
            {
                End(attempt, ConvexTrimEnd.NotRun, false, false, "switched off or the world ending");
                return;
            }

            Turns++;
            if (attempt.cancelled)
            {
                End(attempt, ConvexTrimEnd.NotRun, false, true, "cancelled before it ran");
                return;
            }

            if (attempt.failure != null)
            {
                // Not tried again on this input: the same numbers would fail the same way.
                UnityEngine.Debug.LogException(attempt.failure);
                End(attempt, ConvexTrimEnd.Failed, true, false, attempt.failure.GetType().Name + ": " + attempt.failure.Message);
                return;
            }

            // The Main work goes a step at a time -- the numbers taken back, one Mesh, one hull asked, the new shape made,
            // the switch -- and every step waits for the split's own work for the main thread and for the frame, as the
            // split's stages wait for the frame. A step the frame's budget does not hold is left for a later frame,
            // however many frames that takes: nothing is let through a budget that is short.
            while (_attempt == attempt && attempt.collected)
            {
                double expected = attempt.main == MainStage.Numbers ? ExpectedCollectSeconds
                    : attempt.main == MainStage.Meshes ? ExpectedMeshSeconds
                    : attempt.main == MainStage.HullCheck ? ExpectedHullCheckSeconds
                    : attempt.main == MainStage.Prepare ? ExpectedPrepareSeconds
                    : ExpectedPublishSeconds;
                ConvexTrimHold hold = SplitWaitsForMain != null && SplitWaitsForMain() ? ConvexTrimHold.SplitFirst : Hold(in settings, expected);
                if (hold != ConvexTrimHold.None)
                {
                    NoteHold(attempt, hold);
                    return;
                }

                attempt.budgetHolds = 0;
                if (!StillTheInput(attempt, in settings)) return;
                long begin = Stopwatch.GetTimestamp();
                NoteFrameAttempt(attempt);
                _stepEndSeconds = 0.0;
                bool goOn;
                FrameStages stage = attempt.main == MainStage.Numbers ? FrameStages.Collect : attempt.main == MainStage.Meshes ? FrameStages.Mesh
                    : attempt.main == MainStage.HullCheck ? FrameStages.HullCheck : attempt.main == MainStage.Prepare ? FrameStages.Prepare : FrameStages.Switch;
                switch (attempt.main)
                {
                    case MainStage.Numbers:
                        goOn = CollectNumbers(attempt);
                        break;
                    case MainStage.Meshes:
                        goOn = MakeNextMesh(attempt, in settings);
                        break;
                    case MainStage.HullCheck:
                        goOn = CheckNextHull(attempt);
                        break;
                    case MainStage.Prepare:
                        goOn = Prepare(attempt);
                        break;
                    default:
                        Commit(attempt);
                        goOn = false;
                        break;
                }

                double step = Since(begin);
                if (step > MostMainStepSeconds) MostMainStepSeconds = step;
                NoteMain(stage, step, _stepEndSeconds);
                if (!goOn) return;   // (otherwise the next step is asked of what is left of this frame)
            }
        }

        private void NoteHold(Attempt attempt, ConvexTrimHold hold)
        {
            _holds[(int)hold]++;
            if (hold != ConvexTrimHold.BudgetShort) return;
            attempt.budgetHolds++;
            if (attempt.budgetHolds > MostBudgetHolds) MostBudgetHolds = attempt.budgetHolds;
        }

        // False where the fragment, its shape or its mesh moved on: the attempt ended here, to be looked at again.
        private bool StillTheInput(Attempt attempt, in CutConvexTrimSettings settings)
        {
            if (Examine(attempt.fragment, in settings, out Input now) == ConvexTrimPass.Candidate && now.Same(in attempt.input)) return true;
            End(attempt, ConvexTrimEnd.Stale, false, true, "the fragment, its shape or its mesh moved on while the attempt was out");
            return false;
        }

        // ---- issuing --------------------------------------------------------------------------------------------------

        private bool TryIssue(LogicalFragmentId fragment, in Input input, in CutConvexTrimSettings settings, out bool passedOver)
        {
            passedOver = false;
            long begin = Stopwatch.GetTimestamp();
            using (s_issue.Auto())
            {
                VpCpuGeometryStorage storage = _world.Storage;
                CutWorldProfile profile = _world.Profile;
                PhysicsOwnerShape shape = input.shape;
                if (storage == null || profile == null
                    || !storage.TryAcquireIndexReadLease(input.geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly indexView))
                {
                    NotReadable++;
                    return false;
                }

                long leased = Stopwatch.GetTimestamp();
                int convexCount = shape.ConvexCount;
                var ranges = new ConvexBrepRange[convexCount];
                for (int c = 0; c < convexCount; c++) ranges[c] = shape.Convex(c);
                ConvexTrimWorkspace.Layout(null, ranges, profile.VertexLimit, out long bytes);
                if (bytes > settings.maxBlockBytes || indexView.Length < 3)
                {
                    // More room than an attempt may take (or no triangle): passed over for this mesh and this shape.
                    storage.TryReleaseIndexReadLease(lease);
                    TooLarge++;
                    _marks[fragment] = new Mark { verdict = Verdict.Done, shape = shape, range = input.geometry.indexRange, end = ConvexTrimEnd.NotRun };
                    passedOver = true;
                    return false;
                }

                var attempt = new Attempt
                {
                    serial = ++_serial,
                    fragment = fragment,
                    input = input,
                    lease = lease,
                    holdsLease = true,
                    convexCount = convexCount,
                    indexCount = indexView.Length,
                    vertexLimit = profile.VertexLimit,
                    minOverhang = settings.minOverhangMeters,
                    minKeptVolume = settings.minKeptVolume,
                    minKeptVolumeRatio = settings.minKeptVolumeRatio,
                    geometryToOwner = input.owner.GeometryLocalToOwner.Value,
                    shapeToOwner = shape.LocalToOwner,
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
                    attempt.block = PhysicsCutBlocks.Take<byte>(checked((int)bytes));
                    attempt.workspace = ConvexTrimWorkspace.Layout((byte*)attempt.block.GetUnsafePtr(), ranges, profile.VertexLimit, out _);
                    for (int c = 0; c < convexCount; c++) attempt.workspace.banks[c] = shape.BankOf(c);
                    attempt.offeredAt = Stopwatch.GetTimestamp();
                    if (TestRefuseIssue || !_world.Dispatcher.TryEnqueue(WorkPurpose.Maintenance, attempt, out attempt.ticket))
                    {
                        // No place now: nothing was started, and nothing is recorded of the input.
                        QueueFull++;
                        attempt.Free(storage);
                        return false;
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
                NotePart(ConvexTrimFramePart.IssueLease, begin, leased);
                NotePart(ConvexTrimFramePart.IssueSetUp, leased, attempt.offeredAt);
                NotePart(ConvexTrimFramePart.IssueEnqueue, attempt.offeredAt, Stopwatch.GetTimestamp());
                NoteFrameAttempt(attempt);
            }

            double seconds = Since(begin);
            _attempt.issueSeconds = seconds;
            _issueCosts.Add(seconds);
            _costs[(int)ConvexTrimCostKind.Issue].Add(seconds);
            NoteMain(FrameStages.Issue, seconds);
            return true;
        }

        // The bake is a worker's work as the search was, and it is offered as an issue is -- the first time and again after a
        // queue that had no place: only in a frame the additional split leaves to the trim, with nothing of the frame held
        // against it, and of an input that still stands.
        private void OfferBake(Attempt attempt, in CutConvexTrimSettings settings)
        {
            if (SplitLeavesRoom != null && !SplitLeavesRoom())
            {
                NoteHold(attempt, ConvexTrimHold.SplitFirst);
                return;
            }

            ConvexTrimHold hold = Hold(in settings, ExpectedIssueSeconds);
            if (hold != ConvexTrimHold.None)
            {
                NoteHold(attempt, hold);
                return;
            }

            attempt.budgetHolds = 0;
            if (!StillTheInput(attempt, in settings)) return;
            attempt.offeredAt = Stopwatch.GetTimestamp();
            if (TestRefuseBake || !_world.Dispatcher.TryEnqueue(WorkPurpose.Maintenance, attempt, out attempt.ticket))
            {
                QueueFull++;
                return;
            }

            attempt.offered = true;
        }

        // ---- the Main stages ------------------------------------------------------------------------------------------

        // The numbers as the worker left them. False where the attempt ended here.
        private bool CollectNumbers(Attempt attempt)
        {
            long begin = Stopwatch.GetTimestamp();
            _costs[(int)ConvexTrimCostKind.WorkerSearch].Add(attempt.searchSeconds);
            _costs[(int)ConvexTrimCostKind.WorkerClip].Add(attempt.clipSeconds);
            _costs[(int)ConvexTrimCostKind.WorkerWait].Add(attempt.waitSeconds);
            if (!attempt.ran || attempt.supportFailed)
            {
                NoteCollect(attempt, begin);
                End(attempt, ConvexTrimEnd.NotRun, true, false, attempt.supportFailed ? "the mesh's vertices could not be read (an index beyond the storage, or a projection not finite)" : "the work did not run to its end");
                return false;
            }

            for (int r = 1; r < (int)ConvexTrimRefusal.Count; r++) _left[r] += attempt.outcome.left[r];
            if (attempt.outcome.trims == 0)
            {
                NoteCollect(attempt, begin);
                End(attempt, attempt.outcome.candidatesBefore == 0 ? ConvexTrimEnd.BelowThreshold : ConvexTrimEnd.AllRefused, true, false, null);
                return false;
            }

            int meshes = 0;
            for (int c = 0; c < attempt.convexCount; c++)
            {
                if (attempt.workspace.convexes[c].trims > 0) meshes++;
            }

            attempt.meshCount = meshes;
            attempt.main = MainStage.Meshes;
            NoteCollect(attempt, begin);
            return true;
        }

        private void NoteCollect(Attempt attempt, long begin)
        {
            attempt.collectSeconds = Since(begin);
            _collectCosts.Add(attempt.collectSeconds);
            _costs[(int)ConvexTrimCostKind.Collect].Add(attempt.collectSeconds);
        }

        // One Mesh a step, of the next trimmed convex, as the additional split makes a cut convex's: positions alone, no
        // index, the box as the worker measured it. True where another is to be made; false where the attempt ended, or
        // where they are all made and the bake is to be offered.
        private bool MakeNextMesh(Attempt attempt, in CutConvexTrimSettings settings)
        {
            long begin = Stopwatch.GetTimestamp();
            string failure = null;
            using (s_meshes.Auto())
            {
                if (attempt.meshes == null)
                {
                    attempt.cooking = _world.Cook != null ? _world.Cook.Cooking : PhysicsCutCook.DefaultCooking;
                    attempt.meshes = new Mesh[attempt.convexCount];
                    attempt.meshIds = new int[attempt.meshCount];
                    attempt.nextConvex = 0;
                    attempt.unitsDone = 0;
                }

                try
                {
                    int c = attempt.nextConvex;
                    while (c < attempt.convexCount && attempt.workspace.convexes[c].trims == 0) c++;
                    if (c < attempt.convexCount)
                    {
                        if (attempt.unitsDone == TestFailMeshAt) throw new InvalidOperationException("refused for a test at mesh " + attempt.unitsDone);
                        attempt.meshes[c] = MakeMesh(attempt.fragment, c, in attempt.workspace.convexes[c]);
                        attempt.meshIds[attempt.unitsDone++] = attempt.meshes[c].GetEntityId();
                        c++;
                    }

                    attempt.nextConvex = c;
                }
                catch (Exception e)
                {
                    failure = e.GetType().Name + ": " + e.Message;
                }
            }

            double seconds = Since(begin);
            attempt.meshSeconds += seconds;
            if (failure != null)
            {
                _costs[(int)ConvexTrimCostKind.Meshes].Add(attempt.meshSeconds);
                End(attempt, ConvexTrimEnd.NotBuildable, true, false, "a trimmed convex's Mesh could not be made: " + failure);
                return false;
            }

            _meshCosts.Add(seconds);
            if (attempt.unitsDone < attempt.meshCount) return true;
            _costs[(int)ConvexTrimCostKind.Meshes].Add(attempt.meshSeconds);
            attempt.Reset(AttemptStage.Baking);
            attempt.main = MainStage.HullCheck;
            attempt.nextConvex = 0;
            attempt.unitsDone = 0;
            OfferBake(attempt, in settings);
            return false;
        }

        private static Mesh MakeMesh(LogicalFragmentId fragment, int c, in ConvexTrimConvex convex)
            => MakePositionsMesh("Zantetsu Convex Trim " + fragment.value + "." + c, convex.current.v, convex.current.V, convex.lo, convex.hi);

        /// <summary>A convex's Mesh as the bake takes it: positions alone, no index, the box as given. Main thread.</summary>
        internal static Mesh MakePositionsMesh(string name, float3* vertices, int n, float3 lo, float3 hi)
        {
            const UnityEngine.Rendering.MeshUpdateFlags Quiet = UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds
                | UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices | UnityEngine.Rendering.MeshUpdateFlags.DontNotifyMeshUsers;
            var mesh = new Mesh { name = name, hideFlags = HideFlags.HideAndDontSave };
            var positions = new NativeArray<float3>(n, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            try
            {
                UnsafeUtility.MemCpy(positions.GetUnsafePtr(), vertices, (long)n * sizeof(float3));
                var box = new Bounds((Vector3)((lo + hi) * 0.5f), (Vector3)(hi - lo));
                mesh.SetVertexBufferParams(n, new UnityEngine.Rendering.VertexAttributeDescriptor(UnityEngine.Rendering.VertexAttribute.Position, UnityEngine.Rendering.VertexAttributeFormat.Float32, 3));
                mesh.SetVertexBufferData(positions, 0, 0, n, 0, Quiet);
                mesh.SetIndexBufferParams(0, UnityEngine.Rendering.IndexFormat.UInt16);
                mesh.subMeshCount = 1;
                mesh.SetSubMesh(0, new UnityEngine.Rendering.SubMeshDescriptor(0, 0, MeshTopology.Triangles) { bounds = box, firstVertex = 0, vertexCount = n }, Quiet);
                mesh.bounds = box;
            }
            catch
            {
                UnityEngine.Object.Destroy(mesh);
                throw;
            }
            finally
            {
                positions.Dispose();
            }

            return mesh;
        }

        // What PhysX made of the next baked mesh, asked as a cut's check asks. True where another step follows (another
        // hull, or the new shape); false where the attempt ended here.
        private bool CheckNextHull(Attempt attempt)
        {
            long begin = Stopwatch.GetTimestamp();
            if (!attempt.bakeNoted)
            {
                attempt.bakeNoted = true;
                _costs[(int)ConvexTrimCostKind.WorkerBake].Add(attempt.bakeSeconds);
                _costs[(int)ConvexTrimCostKind.WorkerWait].Add(attempt.waitSeconds);
            }

            bool refused = TestRefuseHull;
            using (s_hulls.Auto())
            {
                int c = attempt.nextConvex;
                while (c < attempt.convexCount && attempt.meshes[c] == null) c++;
                if (c < attempt.convexCount && !refused)
                {
                    refused = _world.Cook == null || _world.Cook.ProbeBakedHull(attempt.meshes[c]) != UnityEngine.LowLevelPhysics.GeometryType.ConvexMesh;
                    attempt.unitsDone++;
                    c++;
                }

                attempt.nextConvex = c;
            }

            double seconds = Since(begin);
            attempt.hullCheckSeconds += seconds;
            if (refused)
            {
                _costs[(int)ConvexTrimCostKind.HullCheck].Add(attempt.hullCheckSeconds);
                End(attempt, ConvexTrimEnd.CookFailed, true, false, "PhysX made no convex of a trimmed convex's mesh");
                return false;
            }

            _hullCosts.Add(seconds);
            if (attempt.unitsDone < attempt.meshCount) return true;
            _costs[(int)ConvexTrimCostKind.HullCheck].Add(attempt.hullCheckSeconds);
            attempt.main = MainStage.Prepare;
            return true;
        }

        // Everything of the publication that can refuse and changes nothing: the colliders of the trimmed convexes are
        // found, and the new shape is made (it takes the meshes over). True where the switch follows; false where the
        // attempt ended here.
        private bool Prepare(Attempt attempt)
        {
            long begin = Stopwatch.GetTimestamp();
            long found = begin;
            string note = null;
            using (s_publish.Auto())
            {
                PhysicsFragmentOwner owner = attempt.input.owner;
                PhysicsOwnerShape shape = attempt.input.shape;
                int count = attempt.convexCount;
                var colliders = new MeshCollider[count];
                _colliderScratch.Clear();
                owner.Root.GetComponentsInChildren(true, _colliderScratch);
                for (int c = 0; c < count && note == null; c++)
                {
                    if (attempt.meshes[c] == null) continue;
                    Mesh old = shape.MeshOf(c);
                    for (int i = 0; i < _colliderScratch.Count; i++)
                    {
                        MeshCollider had = _colliderScratch[i];
                        // (a collider switched off is one a handoff left to be destroyed: not this owner's any more)
                        if (had == null || !had.enabled || had.sharedMesh != old || !had.convex) continue;
                        if (colliders[c] != null)
                        {
                            note = "two colliders carry the mesh of convex " + c;
                            break;
                        }

                        colliders[c] = had;
                    }

                    if (note == null && colliders[c] == null) note = "no collider carries the mesh of convex " + c;
                    else if (note == null && colliders[c].cookingOptions != attempt.cooking) note = "the collider of convex " + c + " has another cooking profile than the bake's";
                }

                _colliderScratch.Clear();
                found = Stopwatch.GetTimestamp();
                if (note == null)
                {
                    var trimmed = new BrepBuffer[count];
                    var boxes = new float3x2[count];
                    var owned = new Mesh[attempt.meshCount];
                    int m = 0;
                    for (int c = 0; c < count; c++)
                    {
                        if (attempt.meshes[c] == null) continue;
                        ref ConvexTrimConvex convex = ref attempt.workspace.convexes[c];
                        trimmed[c] = convex.current;
                        boxes[c] = new float3x2(convex.lo, convex.hi);
                        owned[m++] = attempt.meshes[c];
                    }

                    // The meshes are the source's from here: its last user gives them back (the new shape, or -- where the
                    // shape is not made -- the hold let go just below).
                    Mesh[] meshes = attempt.meshes;
                    attempt.meshes = null;
                    PhysicsShapeSource source = PhysicsShapeSource.OwnPreparedMeshes(owned);
                    try
                    {
                        attempt.prepared = PhysicsOwnerShape.Trimmed(shape, trimmed, meshes, boxes, source);
                        attempt.preparedMeshes = meshes;
                        attempt.colliders = colliders;
                    }
                    catch (Exception e)
                    {
                        note = "the trimmed shape could not be made: " + e.GetType().Name + ": " + e.Message;
                    }
                    finally
                    {
                        source.Release();
                    }
                }
            }

            double seconds = Since(begin);
            NotePart(ConvexTrimFramePart.PrepareColliders, begin, found);
            NotePart(ConvexTrimFramePart.PrepareShape, found, Stopwatch.GetTimestamp());
            attempt.publishSeconds += seconds;
            if (note != null)
            {
                _costs[(int)ConvexTrimCostKind.Publish].Add(attempt.publishSeconds);
                End(attempt, ConvexTrimEnd.NotBuildable, true, false, note);
                return false;
            }

            _prepareCosts.Add(seconds);
            attempt.main = MainStage.Publish;
            return true;
        }

        // The switch, in one step: the colliders of the trimmed convexes get the baked meshes, the owner gets the new
        // shape. A failure after the first collider was changed puts the colliders back.
        private void Commit(Attempt attempt)
        {
            long begin = Stopwatch.GetTimestamp();
            long collidersAt = begin, shapeAt = begin;
            ConvexTrimEnd end = ConvexTrimEnd.Published;
            string note = null;
            PhysicsOwnerShape made = attempt.prepared;
            using (s_publish.Auto())
            {
                PhysicsFragmentOwner owner = attempt.input.owner;
                PhysicsOwnerShape shape = attempt.input.shape;
                MeshCollider[] colliders = attempt.colliders;
                Mesh[] meshes = attempt.preparedMeshes;
                int count = attempt.convexCount;
                // (the colliders were found a step ago, perhaps frames ago: each still carries the mesh it is to give up)
                for (int c = 0; c < count && note == null; c++)
                {
                    if (meshes[c] == null) continue;
                    if (colliders[c] == null || !colliders[c].enabled || colliders[c].sharedMesh != shape.MeshOf(c)) note = "the collider of convex " + c + " changed while the switch waited";
                }

                int changed = 0;
                bool switched = false;
                if (note != null)
                {
                    end = ConvexTrimEnd.Stale;
                }
                else
                {
                    try
                    {
                        for (int c = 0; c < count; c++)
                        {
                            if (colliders[c] == null) continue;
                            colliders[c].sharedMesh = meshes[c];
                            changed = c + 1;
                        }

                        collidersAt = Stopwatch.GetTimestamp();
                        switched = _world.Owners.ReplaceShape(attempt.fragment, shape, made);
                        shapeAt = Stopwatch.GetTimestamp();
                        if (!switched)
                        {
                            end = ConvexTrimEnd.Stale;
                            note = "the owner was not made of the attempt's shape any more";
                        }
                    }
                    catch (Exception e)
                    {
                        end = ConvexTrimEnd.Failed;
                        note = "the colliders could not be given the trimmed meshes: " + e.GetType().Name + ": " + e.Message;
                        UnityEngine.Debug.LogException(e);
                    }
                }

                attempt.prepared = null;   // the owner's now, or given up just below
                if (!switched)
                {
                    // Back as it was: the colliders their old meshes, the new shape (and with it the new meshes) given up.
                    for (int c = 0; c < changed; c++)
                    {
                        if (colliders[c] != null) colliders[c].sharedMesh = shape.MeshOf(c);
                    }

                    made.Dispose();
                    made = null;
                }
                else
                {
                    // Another shape under the same fragment: a result of anything admitted before this is stale
                    // (DESIGN 8), and a body that slept on what was cut off is woken to find its rest again.
                    _world.Ledger.NoteOwnershipChanged(attempt.fragment);
                    if (owner.Body != null && !owner.Body.isKinematic)
                    {
                        owner.Body.WakeUp();
                        BodiesWoken++;
                    }
                }
            }

            double seconds = Since(begin);
            NotePart(ConvexTrimFramePart.SwitchColliders, begin, collidersAt);
            NotePart(ConvexTrimFramePart.SwitchShape, collidersAt, shapeAt);
            NotePart(ConvexTrimFramePart.SwitchRest, Math.Max(shapeAt, collidersAt), Stopwatch.GetTimestamp());
            attempt.publishSeconds += seconds;
            _costs[(int)ConvexTrimCostKind.Publish].Add(attempt.publishSeconds);
            if (end == ConvexTrimEnd.Published)
            {
                _publishCosts.Add(seconds);
                attempt.madeShape = made;
            }

            End(attempt, end, end != ConvexTrimEnd.Stale, end == ConvexTrimEnd.Stale, note);
        }

        // ---- the end --------------------------------------------------------------------------------------------------

        private void End(Attempt attempt, ConvexTrimEnd end, bool mark, bool lookAgain, string note)
        {
            if (_attempt == attempt) _attempt = null;
            _ends[(int)end]++;
            LastEnd = end;
            LastNote = note;
            int frames = Math.Max(0, Time.frameCount - attempt.issuedFrame);
            FramesIssueToEnd += frames;
            if (frames > MostFramesIssueToEnd) MostFramesIssueToEnd = frames;
            long endBegan = Stopwatch.GetTimestamp();
            ConvexTrimRecord record = Record(attempt, end, note);
            long recorded = Stopwatch.GetTimestamp();
            Recorded?.Invoke(record);
            long heard = Stopwatch.GetTimestamp();
            NotePart(ConvexTrimFramePart.EndRecord, endBegan, recorded);
            NotePart(ConvexTrimFramePart.EndListeners, recorded, heard);
            if (end == ConvexTrimEnd.Published)
            {
                Published++;
                ConvexesTrimmed += attempt.outcome.trimmedConvexes;
                TrimsAdopted += attempt.outcome.trims;
            }

            if (mark)
            {
                _marks[attempt.fragment] = new Mark
                {
                    verdict = Verdict.Done, shape = end == ConvexTrimEnd.Published ? attempt.madeShape : attempt.input.shape,
                    range = attempt.input.geometry.indexRange, end = end,
                };
                // The publication's own notice (OnShapeReplaced) queued the fragment before the mark stood: the look that
                // comes of it finds the mark.
            }

            if (lookAgain) Enqueue(attempt.fragment, Time.frameCount + RetryFrames);
            long freeing = Stopwatch.GetTimestamp();
            NotePart(ConvexTrimFramePart.EndRecord, heard, freeing);
            attempt.Free(_world.Storage);
            long ended = Stopwatch.GetTimestamp();
            NotePart(ConvexTrimFramePart.EndFree, freeing, ended);
            _stepEndSeconds += (ended - endBegan) / (double)Stopwatch.Frequency;
        }

        private ConvexTrimRecord Record(Attempt attempt, ConvexTrimEnd end, string note)
        {
            var record = new ConvexTrimRecord
            {
                serial = attempt.serial, fragment = attempt.fragment, end = end, note = note,
                issuedFrame = attempt.issuedFrame, endedFrame = Time.frameCount,
                convexes = attempt.convexCount, indexCount = attempt.indexCount, extents = attempt.input.extents,
                workerSearchSeconds = attempt.searchSeconds, workerClipSeconds = attempt.clipSeconds, workerBakeSeconds = attempt.bakeSeconds,
                issueSeconds = attempt.issueSeconds, collectSeconds = attempt.collectSeconds, meshSeconds = attempt.meshSeconds,
                hullCheckSeconds = attempt.hullCheckSeconds, publishSeconds = attempt.publishSeconds,
            };
            if (attempt.ran && !attempt.supportFailed && attempt.block.IsCreated)
            {
                ConvexTrimOutcome outcome = attempt.outcome;
                record.verticesBefore = new int[attempt.convexCount];
                record.verticesAfter = new int[attempt.convexCount];
                record.trims = new int[attempt.convexCount];
                for (int c = 0; c < attempt.convexCount; c++)
                {
                    ref ConvexTrimConvex convex = ref attempt.workspace.convexes[c];
                    record.verticesBefore[c] = convex.verticesBefore;
                    record.verticesAfter[c] = convex.verticesAfter;
                    record.trims[c] = convex.trims;
                    if (end != ConvexTrimEnd.Published) continue;
                    int added = convex.verticesAfter - convex.verticesBefore;
                    if (convex.trims == 0) continue;
                    if (added > 0) ConvexesGrew++; else if (added < 0) ConvexesShrank++; else ConvexesSame++;
                    if (added > MostVerticesAdded) MostVerticesAdded = added;
                }

                record.verticesBeforeInAll = outcome.verticesBefore;
                record.verticesAfterInAll = outcome.verticesAfter;
                record.overhangBefore = outcome.overhangBefore;
                record.overhangAfter = outcome.overhangAfter;
                record.candidatesBefore = outcome.candidatesBefore;
                record.clipsTried = outcome.clipsTried;
                record.trimsInAll = outcome.trims;
                record.trimmedConvexes = outcome.trimmedConvexes;
                for (int r = 0; r < (int)ConvexTrimRefusal.Count; r++) record.left[r] = outcome.left[r];
            }

            if (_records.Count >= RecordsKept)
            {
                _records.RemoveAt(0);
                RecordsDropped++;
            }

            _records.Add(record);
            return record;
        }

        /// <summary>Gives back an outstanding attempt, for the world's end. Work still on a worker is given back when it is collected.</summary>
        internal void Release()
        {
            Unsubscribe();
            Attempt attempt = _attempt;
            _attempt = null;
            if (attempt == null) return;
            if (attempt.collected || !attempt.offered || (_world.Dispatcher != null && _world.Dispatcher.Cancel(attempt.ticket)))
            {
                attempt.Free(_world.Storage);
            }
            else
            {
                attempt.abandoned = true;
            }
        }

        // ---- the readings ---------------------------------------------------------------------------------------------

        private static string Ms(double seconds) => (seconds * 1000.0).ToString("F3", CultureInfo.InvariantCulture);

        private string DescribeCost(string name, ConvexTrimCostKind kind)
        {
            ConvexTrimCost cost = _costs[(int)kind];
            return name + " " + cost.Count + " (median " + Ms(cost.MedianSeconds) + " ms, most " + Ms(cost.MaxSeconds) + " ms)";
        }

        /// <summary>The trim's tallies in one line, for the world's end.</summary>
        public string Describe()
        {
            CutConvexTrimSettings settings = Settings;
            var text = new StringBuilder(1024);
            text.Append("convex trim (").Append(settings.enabled ? "on" : "off")
                .Append("; size ").Append(settings.minSizeMeters.ToString("R", CultureInfo.InvariantCulture))
                .Append(" m, overhang ").Append(settings.minOverhangMeters.ToString("R", CultureInfo.InvariantCulture)).Append(" m): turns ").Append(Turns)
                .Append(", looked at ").Append(Examined).Append(", issued ").Append(Issued).Append(", published ").Append(Published)
                .Append(" (convexes trimmed ").Append(ConvexesTrimmed).Append(", trims ").Append(TrimsAdopted)
                .Append("; vertices of a convex: more ").Append(ConvexesGrew).Append(", as many ").Append(ConvexesSame).Append(", fewer ").Append(ConvexesShrank)
                .Append(", most added ").Append(MostVerticesAdded).Append("); ends:");
            for (int e = 0; e < (int)ConvexTrimEnd.Count; e++) text.Append(' ').Append((ConvexTrimEnd)e).Append(' ').Append(_ends[e]);
            text.Append("; looks:");
            for (int p = 0; p < (int)ConvexTrimPass.Count; p++)
            {
                if (_passes[p] != 0) text.Append(' ').Append((ConvexTrimPass)p).Append(' ').Append(_passes[p]);
            }

            text.Append("; too small from the parent ").Append(SizeInherited).Append(" (disagreed ").Append(SizeInheritedDisagreed)
                .Append("); candidates left alone:");
            for (int r = 1; r < (int)ConvexTrimRefusal.Count; r++) text.Append(' ').Append((ConvexTrimRefusal)r).Append(' ').Append(_left[r]);
            text.Append("; holds:");
            for (int h = 1; h < (int)ConvexTrimHold.Count; h++) text.Append(' ').Append((ConvexTrimHold)h).Append(' ').Append(_holds[h]);
            text.Append("; queue full ").Append(QueueFull).Append(", not readable ").Append(NotReadable).Append(", too large ").Append(TooLarge)
                .Append(", scans cut short ").Append(ScansCutShort).Append(", longest wait for the budget ").Append(MostBudgetHolds).Append(" turns, longest Main step ").Append(Ms(MostMainStepSeconds)).Append(" ms, bodies woken ").Append(BodiesWoken)
                .Append("; frames on a worker ").Append(FramesOut).Append(" (beside split attempts ").Append(FramesBesideSplit)
                .Append("), issue to end ").Append(FramesIssueToEnd).Append(" frames in all (most ").Append(MostFramesIssueToEnd).Append(")")
                .Append("; worker: ").Append(DescribeCost("search", ConvexTrimCostKind.WorkerSearch)).Append(", ").Append(DescribeCost("clip", ConvexTrimCostKind.WorkerClip))
                .Append(", ").Append(DescribeCost("bake", ConvexTrimCostKind.WorkerBake))
                .Append("; Main: ").Append(DescribeCost("look", ConvexTrimCostKind.Examine)).Append(", ").Append(DescribeCost("issue", ConvexTrimCostKind.Issue))
                .Append(", ").Append(DescribeCost("collect", ConvexTrimCostKind.Collect)).Append(", ").Append(DescribeCost("meshes", ConvexTrimCostKind.Meshes))
                .Append(", ").Append(DescribeCost("hull check", ConvexTrimCostKind.HullCheck)).Append(", ").Append(DescribeCost("publish", ConvexTrimCostKind.Publish))
                .Append("; ").Append(DescribeCost("the whole turn of a frame with work", ConvexTrimCostKind.MainFrame)).Append(", the most in a frame of ").Append(MostFrameStages)
                .Append(", of 0.5 ms or more ").Append(_costs[(int)ConvexTrimCostKind.MainFrame].OverHalfMs).Append(", 1 ms or more ").Append(_costs[(int)ConvexTrimCostKind.MainFrame].OverOneMs)
                .Append(", 2 ms or more ").Append(_costs[(int)ConvexTrimCostKind.MainFrame].OverTwoMs).Append(" (medians are of the first 4096 samples; slow frames recorded ").Append(SlowFrames)
                .Append("); ").Append(DescribeCost("the whole turn of a frame without", ConvexTrimCostKind.MainIdleFrame))
                .Append("; ").Append(DescribeCost("a work's wait for a worker", ConvexTrimCostKind.WorkerWait))
                .Append("; waiting ").Append(_pending.Count).Append(", marks ").Append(_marks.Count);
            return text.ToString();
        }

        // ---- the attempt ----------------------------------------------------------------------------------------------

        private enum AttemptStage { Numbers, Baking }

        private enum MainStage { Numbers, Meshes, HullCheck, Prepare, Publish }

        private sealed class Attempt : IDispatchWork
        {
            internal long serial;
            internal LogicalFragmentId fragment;
            internal Input input;
            internal VpIndexReadLease lease;
            internal bool holdsLease, holdsShape;
            internal NativeArray<byte> block;
            internal ConvexTrimWorkspace workspace;
            internal VpRenderVertex* vertices;
            internal int vertexViewLength;
            internal uint* indices;
            internal int indexCount, convexCount, vertexLimit, issuedFrame;
            internal float minOverhang, minKeptVolume, minKeptVolumeRatio;
            internal float4x4 geometryToOwner, shapeToOwner;

            // The work's own state: written by the worker before _done, read by the main thread after it was collected.
            internal ConvexTrimOutcome outcome;
            internal bool ran, supportFailed;
            internal Exception failure;
            internal double searchSeconds, clipSeconds, bakeSeconds;
            internal long offeredAt;        // the main thread's, before the offer
            internal double waitSeconds;    // of the work that ran last: from its offer to its beginning

            // The main thread's.
            internal AttemptStage stage;
            internal MainStage main;
            internal bool offered, collected, cancelled, abandoned;
            internal WorkTicket ticket;
            internal Mesh[] meshes;        // per convex; null where the convex is as it was
            internal int[] meshIds;        // the trimmed ones, in convex order
            internal int meshCount, budgetHolds;
            internal int nextConvex, unitsDone;            // of the stage that goes a mesh a step
            internal bool bakeNoted;
            internal PhysicsOwnerShape prepared;           // made and not yet the owner's
            internal Mesh[] preparedMeshes;                // per convex, the prepared shape's
            internal MeshCollider[] colliders;             // per convex, those of the trimmed
            internal MeshColliderCookingOptions cooking;
            internal PhysicsOwnerShape madeShape;
            internal double issueSeconds, collectSeconds, meshSeconds, hullCheckSeconds, publishSeconds;
            private int _done;

            public bool IsComplete => Volatile.Read(ref _done) != 0;

            public void Begin()
            {
                waitSeconds = Since(offeredAt);
                if (stage == AttemptStage.Baking)
                {
                    Bake();
                    return;
                }

                long begin = Stopwatch.GetTimestamp();
                try
                {
                    float* support = stackalloc float[ConvexTrimSearch.Directions];
                    if (!ConvexTrimSearch.MeshSupport(vertices, vertexViewLength, indices, indexCount, in geometryToOwner, support))
                    {
                        supportFailed = true;
                    }
                    else
                    {
                        ConvexTrimSearch.SetPlanes(ref workspace, in shapeToOwner, support);
                        ConvexTrimSearch.Run(ref workspace, minOverhang, vertexLimit, minKeptVolume, minKeptVolumeRatio, out outcome);
                    }

                    ran = true;
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    clipSeconds = outcome.clipTicks / (double)Stopwatch.Frequency;
                    searchSeconds = Math.Max(0.0, Since(begin) - clipSeconds);
                    Volatile.Write(ref _done, 1);
                }
            }

            // PhysX cooks each mesh the main thread made, with the cooking the colliders ask for. Plain C# on the pool's
            // worker, as the additional split's bake.
            private void Bake()
            {
                long begin = Stopwatch.GetTimestamp();
                try
                {
                    for (int m = 0; m < meshIds.Length; m++) Physics.BakeMesh(meshIds[m], true, cooking);
                }
                catch (Exception e)
                {
                    failure = e;
                }
                finally
                {
                    bakeSeconds = Since(begin);
                    Volatile.Write(ref _done, 1);
                }
            }

            internal void Reset(AttemptStage next)
            {
                stage = next;
                offered = false;
                collected = false;
                cancelled = false;
                ticket = default;
                Volatile.Write(ref _done, 0);
            }

            public void Collect(WorkCompletion completion)
            {
                collected = true;
                cancelled = completion.outcome == WorkOutcome.Cancelled;
                if (completion.outcome == WorkOutcome.Failed && failure == null) failure = completion.failure;
                if (abandoned) Free(null);   // the world let it go while it ran: given back here
            }

            /// <summary>Gives back everything the attempt holds, once. Main thread.</summary>
            internal void Free(VpCpuGeometryStorage storage)
            {
                if (meshes != null)
                {
                    // Made and never handed to a shape: the bake that read them was collected, or never began.
                    for (int c = 0; c < meshes.Length; c++)
                    {
                        if (meshes[c] != null) UnityEngine.Object.Destroy(meshes[c]);
                    }

                    meshes = null;
                }

                if (prepared != null)
                {
                    // Made and never the owner's: given up, and with it the meshes it took over.
                    prepared.Dispose();
                    prepared = null;
                }

                preparedMeshes = null;
                colliders = null;
                if (block.IsCreated) block.Dispose();
                block = default;
                workspace = default;
                vertices = null;
                indices = null;
                if (holdsShape)
                {
                    holdsShape = false;
                    input.shape.ReleaseFromWork();
                }

                // Without a storage (an attempt the world let go while it ran): the lease went with the storage.
                if (holdsLease && storage != null) storage.TryReleaseIndexReadLease(lease);
                holdsLease = false;
                lease = default;
            }
        }
    }
}
