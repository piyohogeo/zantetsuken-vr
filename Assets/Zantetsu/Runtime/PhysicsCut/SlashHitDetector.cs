using System;
using System.Collections.Generic;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using Zantetsu.Core.Animation;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// One real hit (DESIGN 19.1.7, 21.16.6 <c>SlashHitConfirmed</c>): a wave's sweep of one update met a convex that
    /// one fragment is made of now, that fragment and no ancestor of it had not been consumed by this Slash, and the
    /// hit was passed to the ordinary cut acceptance once -- with what that acceptance said.
    /// <para>
    /// The fragment is the opaque <see cref="LogicalFragmentId"/> of the world's ledger; no object id is part of it.
    /// A hit on a Provisional side names the source that side still is, with the side.
    /// </para>
    /// </summary>
    public readonly struct SlashHitConfirmed
    {
        public SlashHitConfirmed(
            long slashId, double at, bool atLatch, LogicalFragmentId fragment, float side,
            ProvisionalCutAcceptance acceptance, LogicalCutAdmission admission, CutOperationId operation)
        {
            SlashId = slashId;
            At = at;
            AtLatch = atLatch;
            Fragment = fragment;
            Side = side;
            Acceptance = acceptance;
            Admission = admission;
            Operation = operation;
        }

        public long SlashId { get; }

        /// <summary>The input time of the wave update whose sweep hit (<see cref="SlashSweep.At"/>).</summary>
        public double At { get; }

        /// <summary>Whether it was the latch update's degenerate sweep.</summary>
        public bool AtLatch { get; }

        public LogicalFragmentId Fragment { get; }

        /// <summary>+1 or -1 for a side of a Provisional pair, 0 for a published owner.</summary>
        public float Side { get; }

        public ProvisionalCutAcceptance Acceptance { get; }

        public LogicalCutAdmission Admission { get; }

        /// <summary>The operation the acceptance issued, or unset when it issued none.</summary>
        public CutOperationId Operation { get; }
    }

    /// <summary>
    /// A building hull group as a hit target (BuildingHullFusion, 2026-09-30): one convex in its Root's frame, consumed
    /// per Slash by the group itself (inherited by what a cut or a fusion makes of it), and cut through its own acceptance.
    /// </summary>
    public interface ISlashHullTarget
    {
        bool IsHitTarget { get; }
        Transform Root { get; }
        Zantetsu.ConvexCut.ConvexBrepBank Bank { get; }
        Zantetsu.ConvexCut.ConvexBrepRange Convex { get; }
        void Bounds(out float3 lo, out float3 hi);
        LogicalFragmentId TraceFragment { get; }
        bool IsConsumedBy(long slashId);
        /// <param name="travelWorld">The sweep's travel direction in the world (the always-kinematic mode slides a near-horizontal cut's side along it).</param>
        ProvisionalCutAcceptance TryCut(float4 planeRoot, float4 planeWorld, long slashId, long planeId, double at, float3 travelWorld);
    }

    /// <summary>What a hit's acceptance is asked with that is not the hit's own: the caller's values.</summary>
    public struct SlashHitSettings
    {
        /// <summary>
        /// The two separation impulses every accepted cut is asked with (<see cref="ProvisionalCutAsk"/>). **The
        /// caller's values**: DESIGN 7.2 leaves their formula open and nothing here decides them.
        /// </summary>
        public float positiveSeparationImpulse;

        public float negativeSeparationImpulse;
    }

    /// <summary>
    /// The Slash hit detector (DESIGN 19.1.7, 19.1.9, 4.2): after each core update, every sweep that update gave is
    /// tested against the convexes the fragments are made of now, each real hit consumes its fragment for that Slash,
    /// and a hit that consumed something is passed once to the one cut acceptance (<see cref="ProvisionalCutDriver.RequestCut"/>).
    /// <para>
    /// **Candidates, then the exact test -- from one present state.** Both read the same thing: the convex sets the
    /// fragments are made of now (<see cref="PhysicsOwnerRegistry.CollectCurrentShapes"/>), placed by their owner's
    /// transform as it stands. A candidate is a convex whose own recorded box (<see cref="PhysicsOwnerShape.ConvexBounds"/>,
    /// which holds every vertex of it) meets the box of the sweep's four points, both in that convex's frame; what
    /// decides is <see cref="SlashSweepConvexQuery"/> on the convex itself. No collider and no physics query is asked:
    /// with manual stepping, a collider stands where the last step or synchronisation left it, and a body placed by
    /// its transform since -- the placement the cut reads and the next step starts from -- would be missed by one.
    /// </para>
    /// <para>
    /// **Enumeration, then acceptance.** Every sweep of the update is evaluated first, against the scene as the update
    /// found it; only then are the hits passed on, in the order they were found. An acceptance publishes a Provisional
    /// pair and withdraws its source, which changes what the scene holds -- so nothing is accepted while candidates
    /// are still being read. Each hit keeps the plane it was found with, in the frame of the convexes it was tested
    /// against; nothing simulates between the two, so what is accepted is what was hit.
    /// </para>
    /// <para>
    /// **Consumed before it is passed on, and passed on once.** A fragment whose Slash has consumed it or an ancestor
    /// is not tested again; a new one is consumed and then handed to the acceptance, and whatever that says -- a
    /// no-op, an active source passed over, a full budget -- the same Slash does not try it again. Another Slash can.
    /// </para>
    /// <para>
    /// **Traced as an observation.** Each hit passed on is written, after its acceptance, as one
    /// <see cref="TraceEventType.SlashHitConfirmed"/> record (<see cref="SlashHitConfirmedTraceRecord"/>) into the trace
    /// lane a composition gave (<see cref="AttachTrace"/>) -- whatever the acceptance said, with or without an operation.
    /// A record the lane cannot take is its own drop to count: the hit and its acceptance stand, and nothing is written
    /// again or waited for.
    /// </para>
    /// <para>
    /// **A character before its first cut** (DESIGN 19.1.7, 4.52) is a candidate too, through its prepared cut
    /// (<see cref="AddCharacter"/>): the same query on its bone-local convexes, each placed by its bone's world transform
    /// as the current pose left it -- the transform its cut poses the same convex with. A hit identifies the character as
    /// its fragment (issued then, if this is the first), consumes that fragment like any other, and is passed once to
    /// the character's own acceptance, <see cref="VpPreparedCharacterCut.TryCut"/>, with the plane in its renderer's
    /// frame. Its children descend from that fragment, so the Slash that cut it leaves them alone.
    /// </para>
    /// <para>
    /// **A character whose bones a level of detail may leave behind** (<see cref="AddCharacter(VpPreparedCharacterCut, IPoseOnDemand)"/>):
    /// the update's sweeps are first met with the range it can occupy at all, placed by its root as it stands; one no
    /// sweep can meet is not tested, and one some sweep can meet has its whole pose of the frame put on its bones, once
    /// for all the sweeps, before the exact test above -- which, and the cut after it, then read that pose.
    /// </para>
    /// <para>
    /// **A placed object before its first cut** (DESIGN 4.5.1; <see cref="AddPlaced"/>, <see cref="ISlashPlacedTarget"/>) is a
    /// candidate the same way: its convexes in its own frame as the instance stands, drawn and colliding meanwhile as the
    /// scene placed it. A hit identifies it as its fragment, consumes it, and is passed once to its own acceptance, which
    /// prepares its cut input then; it leaves the candidates when that says it is done.
    /// </para>
    /// <para>
    /// **Nothing of the blade.** Only the waves' sweeps are read: the blade's own pose, its gate and whether it may
    /// fire play no part, so a wave already flying keeps hitting while the gesture cannot fire (T-040).
    /// </para>
    /// </summary>
    public sealed class SlashHitDetector
    {
        private static readonly ProfilerMarker s_evaluate = new ProfilerMarker("Zantetsu.SlashHit.Evaluate");

        // Inside Evaluate, apart from each other: the enumeration, and a character's acceptance.
        private static readonly ProfilerMarker s_find = new ProfilerMarker("Zantetsu.SlashHit.Find");
        private static readonly ProfilerMarker s_acceptCharacter = new ProfilerMarker("Zantetsu.SlashHit.AcceptCharacter");

        private readonly PhysicsOwnerRegistry _registry;
        private readonly LogicalCutLedger _ledger;
        private readonly ProvisionalCutDriver _driver;
        private readonly Func<bool> _open;
        private readonly SlashHitSettings _settings;
        private readonly SlashLineageConsumption _consumption;
        private long _adoptedPlanes;   // one per sweep found for: the identity of its adopted plane
        private readonly List<Pending> _pending = new List<Pending>(8);
        private readonly List<SlashHitConfirmed> _hits = new List<SlashHitConfirmed>(8);
        private readonly List<CurrentShape> _shapes = new List<CurrentShape>(16);
        private readonly List<VpPreparedCharacterCut> _characters = new List<VpPreparedCharacterCut>(4);
        private readonly List<VpPreparedCharacterCut> _characterTargets = new List<VpPreparedCharacterCut>(4);
        private readonly List<IPoseOnDemand> _characterPoses = new List<IPoseOnDemand>(4);
        private readonly List<ISlashHullTarget> _hullTargets = new List<ISlashHullTarget>(4);
        private readonly List<ISlashPlacedTarget> _placed = new List<ISlashPlacedTarget>(8);
        private readonly List<ISlashPlacedTarget> _placedTargets = new List<ISlashPlacedTarget>(8);
        private BuildingHullFusion _hulls;
        private readonly long[] _live = new long[SlashWaveCore.Capacity];
        private readonly SlashSweep[] _sweeps = new SlashSweep[SlashWaveCore.Capacity];
        private float3[] _section = new float3[64];
        private TraceLaneWriter _trace;

        private struct Pending
        {
            public long slashId;
            public double at;
            public bool atLatch;
            public LogicalFragmentId fragment;
            public float side;
            public float4 plane;
            public float3 renderAnchor;
            public VpPreparedCharacterCut character;
            public ISlashHullTarget hull;
            public ISlashPlacedTarget placed;
            public long planeId;   // the sweep's adopted plane: one identity per sweep, shared by the hits it found
            public float4 planeWorld;
            public float3 travelWorld;   // the sweep's travel (hull targets)
        }

        /// <summary>A detector over one cut world's parts, open while <paramref name="open"/> says so.</summary>
        public SlashHitDetector(
            PhysicsOwnerRegistry registry,
            LogicalCutLedger ledger,
            ProvisionalCutDriver driver,
            in SlashHitSettings settings,
            Func<bool> open = null)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
            _driver = driver != null ? driver : throw new ArgumentNullException(nameof(driver));
            if (!float.IsFinite(settings.positiveSeparationImpulse) || !float.IsFinite(settings.negativeSeparationImpulse))
            {
                throw new ArgumentOutOfRangeException(nameof(settings), "the impulses must be finite");
            }

            _settings = settings;
            _open = open;
            _consumption = new SlashLineageConsumption(ledger, SlashWaveCore.Capacity);
            _adoptedPlanes = 0;
        }

        /// <summary>A detector over a composed cut world, open while that world is ready and not ending.</summary>
        public SlashHitDetector(CutWorldRoot world, in SlashHitSettings settings)
            : this(world.Owners, world.Ledger, world.Driver, in settings,
                () => world != null && world.IsReady && !world.IsEnding && !world.IsReleased && !world.TerminationRequested)
        {
            _hulls = world.Hulls;
        }

        /// <summary>The building hull trial whose groups are candidates from now on (their hulls, each in its Root's frame).</summary>
        public void AttachHulls(BuildingHullFusion hulls)
        {
            _hulls = hulls;
        }

        /// <summary>The real hits of the last <see cref="Evaluate"/>, in the order they were passed on.</summary>
        public int HitCount => _hits.Count;

        public SlashHitConfirmed HitAt(int index) => _hits[index];

        /// <summary>What each live Slash has consumed.</summary>
        public SlashLineageConsumption Consumption => _consumption;

        /// <summary>
        /// Makes a prepared character a candidate from now on, until it is cut or taken away. The handle stays its
        /// owner's: this only reads it and calls its cut.
        /// </summary>
        public void AddCharacter(VpPreparedCharacterCut character)
        {
            AddCharacter(character, null);
        }

        /// <summary>
        /// The same, for a character whose bones may stand behind the frame (<paramref name="pose"/>, a level of detail):
        /// in each update its range (<see cref="IPoseOnDemand.RangeBounds"/>, placed by its root as it stands) is met with
        /// every sweep first; a character no sweep can meet is not tested further, and one that some sweep can meet has its
        /// whole current pose put on its bones -- once, however many sweeps or Slashes -- before the exact test, so the test
        /// and the cut read the pose of the frame, not the one the level of detail left.
        /// </summary>
        public void AddCharacter(VpPreparedCharacterCut character, IPoseOnDemand pose)
        {
            if (character != null && !_characters.Contains(character))
            {
                _characters.Add(character);
                _characterPoses.Add(pose);
            }
        }

        /// <summary>Whether a prepared character is a candidate now, and how many are (observation).</summary>
        public bool HasCharacter(VpPreparedCharacterCut character) => character != null && _characters.Contains(character);

        public int CharacterCount => _characters.Count;

        public void RemoveCharacter(VpPreparedCharacterCut character)
        {
            int at = _characters.IndexOf(character);
            if (at >= 0)
            {
                _characters.RemoveAt(at);
                _characterPoses.RemoveAt(at);
            }
        }

        /// <summary>
        /// Makes a placed object before its first cut a candidate from now on (<see cref="ISlashPlacedTarget"/>), until its
        /// own acceptance says it is done or it is taken away. Added once.
        /// </summary>
        public void AddPlaced(ISlashPlacedTarget placed)
        {
            if (placed == null) throw new ArgumentNullException(nameof(placed));
            if (!_placed.Contains(placed)) _placed.Add(placed);
        }

        /// <summary>Whether a placed object is a candidate now, and how many are (observation).</summary>
        public bool HasPlaced(ISlashPlacedTarget placed) => placed != null && _placed.Contains(placed);

        public int PlacedCount => _placed.Count;

        public void RemovePlaced(ISlashPlacedTarget placed)
        {
            _placed.Remove(placed);
        }

        /// <summary>
        /// The main thread's trace lane the hits are written into, from whoever composes the trace of a run. Until one
        /// is given nothing is written: a writer that was never made carries no event.
        /// </summary>
        internal void AttachTrace(TraceLaneWriter writer)
        {
            _trace = writer;
        }

        /// <summary>
        /// Evaluates every sweep <paramref name="core"/>'s last update gave, and passes each new hit on. Called once
        /// after each <see cref="SlashWaveCore.Update"/>, in the update phase, on the main thread.
        /// </summary>
        public void Evaluate(SlashWaveCore core)
        {
            if (core == null)
            {
                _hits.Clear();
                _pending.Clear();
                return;
            }

            int live = 0;
            for (int i = 0; i < core.WaveCount && live < _live.Length; i++)
            {
                _live[live++] = core.SlashIdAt(i);
            }

            int sweeps = 0;
            for (int s = 0; s < core.SweepCount && sweeps < _sweeps.Length; s++)
            {
                _sweeps[sweeps++] = core.SweepAt(s);
            }

            Evaluate(new ReadOnlySpan<SlashSweep>(_sweeps, 0, sweeps), new ReadOnlySpan<long>(_live, 0, live));
        }

        /// <summary>
        /// The same, for sweeps a caller holds itself: <paramref name="sweeps"/> are one update's, and
        /// <paramref name="live"/> is every Slash still flying after it -- a Slash not among them has ended, and its
        /// consumption goes with it.
        /// </summary>
        public void Evaluate(ReadOnlySpan<SlashSweep> sweeps, ReadOnlySpan<long> live)
        {
            // The whole of it: the query, the consumption and each hit's acceptance with its publication.
            using ProfilerMarker.AutoScope scope = s_evaluate.Auto();
            _hits.Clear();
            _pending.Clear();

            // A Slash that has gone gives its set back first: it can hit nothing any more.
            _consumption.KeepOnly(live);
            _hulls?.KeepOnlyLiveSlashes(live);
            if (sweeps.Length == 0 || (_open != null && !_open()))
            {
                return;
            }

            // What the fragments are made of now, read once: every sweep of this update is tested against the same
            // present state, and the acceptances below change the correspondence, not this list.
            using (s_find.Auto())
            {
                _registry.CollectCurrentShapes(_shapes);
                _characterTargets.Clear();
                for (int c = 0; c < _characters.Count; c++)
                {
                    if (_characters[c] == null || !_characters[c].IsHitTarget)
                    {
                        continue;
                    }

                    // A character whose bones may be behind: only if a sweep can meet its range, and then with its
                    // whole current pose on the bones before anything reads them.
                    IPoseOnDemand pose = _characterPoses[c];
                    if (pose != null && pose.IsLive)
                    {
                        if (!AnySweepMeets(pose, sweeps))
                        {
                            continue;
                        }

                        pose.EnsureCurrentFullPose();
                    }

                    _characterTargets.Add(_characters[c]);
                }

                _hullTargets.Clear();
                _hulls?.CollectTargets(_hullTargets);
                _placedTargets.Clear();
                for (int p = 0; p < _placed.Count; p++)
                {
                    if (_placed[p] != null && _placed[p].IsHitTarget) _placedTargets.Add(_placed[p]);
                }

                for (int s = 0; s < sweeps.Length; s++)
                {
                    _adoptedPlanes++;   // this sweep's plane: the identity every hit it finds carries
                    Find(in sweeps[s]);
                    FindCharacters(in sweeps[s]);
                    FindHulls(in sweeps[s]);
                    FindPlaced(in sweeps[s]);
                }
            }

            for (int i = 0; i < _pending.Count; i++)
            {
                Pending hit = _pending[i];
                if (hit.character != null)
                {
                    AcceptCharacter(in hit);
                    continue;
                }

                if (hit.placed != null)
                {
                    AcceptPlaced(in hit);
                    continue;
                }

                if (hit.hull != null)
                {
                    // The group's own acceptance: it consumes the Slash for the group on any answer but a refusal of the request.
                    ProvisionalCutAcceptance hullAcceptance = hit.hull.TryCut(hit.plane, hit.planeWorld, hit.slashId, hit.planeId, hit.at, hit.travelWorld);
                    var hullConfirmed = new SlashHitConfirmed(hit.slashId, hit.at, hit.atLatch, hit.fragment, 0f, hullAcceptance, LogicalCutAdmission.NoOp, default);
                    _hits.Add(hullConfirmed);
                    Trace(in hullConfirmed);
                    continue;
                }

                var ask = new ProvisionalCutAsk
                {
                    source = hit.fragment,
                    plane = hit.plane,
                    positiveSeparationImpulse = _settings.positiveSeparationImpulse,
                    negativeSeparationImpulse = _settings.negativeSeparationImpulse,
                    renderAnchor = hit.renderAnchor,
                    slashId = hit.slashId,
                    adoptedPlaneId = hit.planeId,
                    adoptedPlaneWorld = hit.planeWorld,
                };

                ProvisionalCutAcceptance acceptance = _driver.RequestCut(
                    in ask, out ProvisionalCutTransaction transaction, out LogicalCutAdmission admission);
                var confirmed = new SlashHitConfirmed(
                    hit.slashId, hit.at, hit.atLatch, hit.fragment, hit.side, acceptance, admission,
                    transaction != null ? transaction.Operation : default);
                _hits.Add(confirmed);
                Trace(in confirmed);
            }
        }

        // A placed object's own acceptance: it prepares its cut input only now, and asks through the same driver (or the hull trial).
        private void AcceptPlaced(in Pending hit)
        {
            SlashPlacedCutResult result;
            result = hit.placed.TryCut(new SlashPlacedHit(hit.plane, hit.planeWorld, hit.renderAnchor, hit.slashId, hit.planeId, hit.at,
                hit.travelWorld, _settings.positiveSeparationImpulse, _settings.negativeSeparationImpulse));

            if (result.Done)
            {
                // Its cut went on (it is its fragment's owner from here, or the hull trial's group) or it was refused for good.
                RemovePlaced(hit.placed);
            }

            var confirmed = new SlashHitConfirmed(
                hit.slashId, hit.at, hit.atLatch, hit.fragment, hit.side, result.Acceptance, result.Admission, result.Operation);
            _hits.Add(confirmed);
            Trace(in confirmed);
        }

        // A character's own acceptance: the prepared cut classifies, admits and publishes through the same driver.
        private void AcceptCharacter(in Pending hit)
        {
            VpCharacterCutResult result;
            using (s_acceptCharacter.Auto())
            {
                result = hit.character.TryCut(
                    hit.plane, hit.renderAnchor, _settings.positiveSeparationImpulse, _settings.negativeSeparationImpulse);
            }
            ProvisionalCutAcceptance acceptance;
            LogicalCutAdmission admission;
            switch (result.Outcome)
            {
                case VpCharacterCutOutcome.Requested:
                    acceptance = result.Acceptance;
                    admission = result.Operation.IsSet ? LogicalCutAdmission.Admitted : LogicalCutAdmission.NoOp;
                    break;
                case VpCharacterCutOutcome.EmptySide:
                    acceptance = ProvisionalCutAcceptance.EmptySide;
                    admission = LogicalCutAdmission.NoOp;
                    break;
                case VpCharacterCutOutcome.Full:
                    acceptance = ProvisionalCutAcceptance.NotAccepted;
                    admission = LogicalCutAdmission.Full;
                    break;
                case VpCharacterCutOutcome.Unavailable:
                    acceptance = ProvisionalCutAcceptance.NotAccepted;
                    admission = LogicalCutAdmission.SourceNotLive;
                    break;
                case VpCharacterCutOutcome.Held:
                    // Not admitted: the ledger was not asked. The request is the driver's to take up later.
                    acceptance = ProvisionalCutAcceptance.Held;
                    admission = LogicalCutAdmission.NoOp;
                    break;
                default:
                    acceptance = ProvisionalCutAcceptance.InvalidRequest;
                    admission = LogicalCutAdmission.NoOp;
                    break;
            }

            if (hit.character.IsDisposed || result.Outcome == VpCharacterCutOutcome.Requested
                || result.Outcome == VpCharacterCutOutcome.Held)
            {
                // Its cut is done with it: from here the character is its fragment's owner, or nothing. A held request is
                // the driver's now, and the character is never a hit target again.
                RemoveCharacter(hit.character);
            }

            var confirmed = new SlashHitConfirmed(
                hit.slashId, hit.at, hit.atLatch, hit.fragment, hit.side, acceptance, admission, result.Operation);
            _hits.Add(confirmed);
            Trace(in confirmed);
        }

        // Whether any of this update's sweeps can meet the character's range: the box of each sweep's four points in the
        // root's frame against the range, closed (the convexes' own candidate test, one level up).
        private static bool AnySweepMeets(IPoseOnDemand pose, ReadOnlySpan<SlashSweep> sweeps)
        {
            Transform root = pose.Root;
            if (root == null)
            {
                return false;
            }

            float4x4 worldToRoot = root.worldToLocalMatrix;
            Bounds range = pose.RangeBounds;
            float3 lo = range.min;
            float3 hi = range.max;
            for (int s = 0; s < sweeps.Length; s++)
            {
                float3 a0 = math.transform(worldToRoot, (float3)sweeps[s].PreviousA);
                float3 b0 = math.transform(worldToRoot, (float3)sweeps[s].PreviousB);
                float3 a1 = math.transform(worldToRoot, (float3)sweeps[s].CurrentA);
                float3 b1 = math.transform(worldToRoot, (float3)sweeps[s].CurrentB);
                if (!math.all(math.isfinite(a0) & math.isfinite(b0) & math.isfinite(a1) & math.isfinite(b1)))
                {
                    continue;
                }

                float3 qlo = math.min(math.min(a0, b0), math.min(a1, b1));
                float3 qhi = math.max(math.max(a0, b0), math.max(a1, b1));
                if (!(math.any(qhi < lo) || math.any(hi < qlo)))
                {
                    return true;
                }
            }

            return false;
        }

        // The characters not cut yet: each bone-local convex placed by its bone as it stands now.
        private void FindCharacters(in SlashSweep sweep)
        {
            float3 n = sweep.SourceSlashPlane.normal;
            float3 a0 = sweep.PreviousA;
            float3 b0 = sweep.PreviousB;
            float3 a1 = sweep.CurrentA;
            float3 b1 = sweep.CurrentB;
            if (_characterTargets.Count == 0 || !math.all(math.isfinite(n)) || math.lengthsq(n) <= 0f
                || !math.all(math.isfinite(a0) & math.isfinite(b0) & math.isfinite(a1) & math.isfinite(b1)))
            {
                return;
            }

            var worldPlane = new float4(n, sweep.SourceSlashPlane.distance);
            for (int c = 0; c < _characterTargets.Count; c++)
            {
                VpPreparedCharacterCut character = _characterTargets[c];
                if (character.Source.IsSet && _consumption.IsConsumed(sweep.SlashId, character.Source))
                {
                    continue;
                }

                VpCharacterHitShape shape = character.HitShape;
                bool hit = false;
                for (int k = 0; k < shape.ConvexCount && !hit; k++)
                {
                    float4x4 boneToWorld = (float4x4)character.ConvexBone(k).localToWorldMatrix;
                    float4x4 worldToBone = math.inverse(boneToWorld);
                    float4 plane = math.mul(math.transpose(boneToWorld), worldPlane);
                    plane /= math.length(plane.xyz);
                    float3 la0 = math.transform(worldToBone, a0);
                    float3 lb0 = math.transform(worldToBone, b0);
                    float3 la1 = math.transform(worldToBone, a1);
                    float3 lb1 = math.transform(worldToBone, b1);
                    shape.Bounds(k, out float3 lo, out float3 hi);
                    float3 qlo = math.min(math.min(la0, lb0), math.min(la1, lb1));
                    float3 qhi = math.max(math.max(la0, lb0), math.max(la1, lb1));
                    if (math.any(qhi < lo) || math.any(hi < qlo))
                    {
                        continue;
                    }

                    hit = SlashSweepConvexQuery.Intersects(plane, la0, lb0, la1, lb1, shape.Bank, shape.Convex(k), ref _section);
                }

                if (!hit || !character.TryIdentify(out LogicalFragmentId fragment)
                    || !_consumption.TryConsume(sweep.SlashId, fragment))
                {
                    continue;
                }

                // The plane its cut is asked with: the wave's, in the renderer's frame, as the bones stand now.
                Transform renderer = character.RendererTransform;
                float4 rendererPlane = math.mul(math.transpose((float4x4)renderer.localToWorldMatrix), worldPlane);
                rendererPlane /= math.length(rendererPlane.xyz);
                _pending.Add(new Pending
                {
                    slashId = sweep.SlashId,
                    at = sweep.At,
                    atLatch = sweep.IsLatch,
                    fragment = fragment,
                    side = 0f,
                    plane = rendererPlane,
                    renderAnchor = renderer.position,
                    character = character,
                    planeId = _adoptedPlanes,
                    planeWorld = worldPlane,
                });
            }
        }

        // The placed objects not cut yet: their convexes in their own frame, as the instance stands now.
        private void FindPlaced(in SlashSweep sweep)
        {
            float3 n = sweep.SourceSlashPlane.normal;
            float3 a0 = sweep.PreviousA;
            float3 b0 = sweep.PreviousB;
            float3 a1 = sweep.CurrentA;
            float3 b1 = sweep.CurrentB;
            if (_placedTargets.Count == 0 || !math.all(math.isfinite(n)) || math.lengthsq(n) <= 0f
                || !math.all(math.isfinite(a0) & math.isfinite(b0) & math.isfinite(a1) & math.isfinite(b1)))
            {
                return;
            }

            var worldPlane = new float4(n, sweep.SourceSlashPlane.distance);
            for (int p = 0; p < _placedTargets.Count; p++)
            {
                ISlashPlacedTarget target = _placedTargets[p];
                if (target.Source.IsSet && _consumption.IsConsumed(sweep.SlashId, target.Source))
                {
                    continue;
                }

                float4x4 frameToWorld = target.FrameToWorld;
                float4x4 worldToFrame = math.inverse(frameToWorld);
                float3 la0 = math.transform(worldToFrame, a0);
                float3 lb0 = math.transform(worldToFrame, b0);
                float3 la1 = math.transform(worldToFrame, a1);
                float3 lb1 = math.transform(worldToFrame, b1);
                float3 qlo = math.min(math.min(la0, lb0), math.min(la1, lb1));
                float3 qhi = math.max(math.max(la0, lb0), math.max(la1, lb1));
                float4 plane = math.mul(math.transpose(frameToWorld), worldPlane);
                plane /= math.length(plane.xyz);
                VpCharacterHitShape shape = target.HitShape;
                bool hit = false;
                for (int k = 0; k < shape.ConvexCount && !hit; k++)
                {
                    shape.Bounds(k, out float3 lo, out float3 hi);
                    if (math.any(qhi < lo) || math.any(hi < qlo))
                    {
                        continue;
                    }

                    hit = SlashSweepConvexQuery.Intersects(plane, la0, lb0, la1, lb1, shape.Bank, shape.Convex(k), ref _section);
                }

                if (!hit || !target.TryIdentify(out LogicalFragmentId fragment) || !_consumption.TryConsume(sweep.SlashId, fragment))
                {
                    continue;
                }

                _pending.Add(new Pending
                {
                    slashId = sweep.SlashId, at = sweep.At, atLatch = sweep.IsLatch, fragment = fragment, side = 0f,
                    plane = plane, renderAnchor = frameToWorld.c3.xyz, placed = target, planeId = _adoptedPlanes, planeWorld = worldPlane,
                    travelWorld = (float3)sweep.TravelAxis,
                });
            }
        }

        // The building hull groups: each one convex in its Root's frame, consumed per Slash by the group.
        private void FindHulls(in SlashSweep sweep)
        {
            float3 n = sweep.SourceSlashPlane.normal;
            float3 a0 = sweep.PreviousA;
            float3 b0 = sweep.PreviousB;
            float3 a1 = sweep.CurrentA;
            float3 b1 = sweep.CurrentB;
            if (_hullTargets.Count == 0 || !math.all(math.isfinite(n)) || math.lengthsq(n) <= 0f
                || !math.all(math.isfinite(a0) & math.isfinite(b0) & math.isfinite(a1) & math.isfinite(b1)))
            {
                return;
            }

            var worldPlane = new float4(n, sweep.SourceSlashPlane.distance);
            for (int h = 0; h < _hullTargets.Count; h++)
            {
                ISlashHullTarget target = _hullTargets[h];
                Transform root = target.Root;
                if (!target.IsHitTarget || root == null || target.IsConsumedBy(sweep.SlashId))
                {
                    continue;
                }

                float4x4 rootToWorld = (float4x4)root.localToWorldMatrix;
                float4x4 worldToRoot = math.inverse(rootToWorld);
                float4 plane = math.mul(math.transpose(rootToWorld), worldPlane);
                plane /= math.length(plane.xyz);
                float3 la0 = math.transform(worldToRoot, a0);
                float3 lb0 = math.transform(worldToRoot, b0);
                float3 la1 = math.transform(worldToRoot, a1);
                float3 lb1 = math.transform(worldToRoot, b1);
                target.Bounds(out float3 lo, out float3 hi);
                float3 qlo = math.min(math.min(la0, lb0), math.min(la1, lb1));
                float3 qhi = math.max(math.max(la0, lb0), math.max(la1, lb1));
                if (math.any(qhi < lo) || math.any(hi < qlo))
                {
                    continue;
                }

                Zantetsu.ConvexCut.ConvexBrepBank bank = target.Bank;
                Zantetsu.ConvexCut.ConvexBrepRange range = target.Convex;
                if (!SlashSweepConvexQuery.Intersects(plane, la0, lb0, la1, lb1, in bank, in range, ref _section))
                {
                    continue;
                }

                _pending.Add(new Pending
                {
                    slashId = sweep.SlashId, at = sweep.At, atLatch = sweep.IsLatch, fragment = target.TraceFragment, side = 0f,
                    plane = plane, renderAnchor = root.position, hull = target, planeId = _adoptedPlanes, planeWorld = worldPlane, travelWorld = (float3)sweep.TravelAxis,
                });
            }
        }

        private unsafe void Trace(in SlashHitConfirmed hit)
        {
            if (!_trace.IsEnabled(TraceEventType.SlashHitConfirmed))
            {
                return;
            }

            byte* payload = stackalloc byte[SlashHitConfirmedTraceRecord.Length];
            SlashHitConfirmedTraceRecord.Write(in hit, new Span<byte>(payload, SlashHitConfirmedTraceRecord.Length));

            // Written once. A lane that cannot take it counts the drop itself; the hit is not held back for it.
            _trace.TryWrite(TraceEventType.SlashHitConfirmed, payload, SlashHitConfirmedTraceRecord.Length);
        }

        private void Find(in SlashSweep sweep)
        {
            float3 n = sweep.SourceSlashPlane.normal;
            float3 a0 = sweep.PreviousA;
            float3 b0 = sweep.PreviousB;
            float3 a1 = sweep.CurrentA;
            float3 b1 = sweep.CurrentB;
            if (!math.all(math.isfinite(n)) || math.lengthsq(n) <= 0f
                || !math.all(math.isfinite(a0) & math.isfinite(b0) & math.isfinite(a1) & math.isfinite(b1)))
            {
                return;
            }

            for (int c = 0; c < _shapes.Count; c++)
            {
                CurrentShape current = _shapes[c];
                LogicalFragmentId fragment = current.Fragment;
                PhysicsOwnerShape shape = current.Shape;
                Transform owner = current.Owner;
                if (owner == null || shape.IsFreed || _consumption.IsConsumed(sweep.SlashId, fragment))
                {
                    continue;
                }

                // Into the convexes' own numerical frame: the owner's world transform with the shape's placement on it.
                float4x4 shapeToWorld = math.mul((float4x4)owner.localToWorldMatrix, shape.LocalToOwner);
                float4x4 worldToShape = math.inverse(shapeToWorld);
                var worldPlane = new float4(n, sweep.SourceSlashPlane.distance);
                float4 plane = math.mul(math.transpose(shapeToWorld), worldPlane);
                plane /= math.length(plane.xyz);
                float3 la0 = math.transform(worldToShape, a0);
                float3 lb0 = math.transform(worldToShape, b0);
                float3 la1 = math.transform(worldToShape, a1);
                float3 lb1 = math.transform(worldToShape, b1);

                // The candidate test: the box of the four points against each convex's own recorded box, closed.
                float3 qlo = math.min(math.min(la0, lb0), math.min(la1, lb1));
                float3 qhi = math.max(math.max(la0, lb0), math.max(la1, lb1));
                bool hit = false;
                for (int k = 0; k < shape.ConvexCount && !hit; k++)
                {
                    shape.ConvexBounds(k, out float3 lo, out float3 hi);
                    if (math.any(qhi < lo) || math.any(hi < qlo))
                    {
                        continue;
                    }

                    hit = SlashSweepConvexQuery.Intersects(
                        plane, la0, lb0, la1, lb1, shape.BankOf(k), shape.Convex(k), ref _section);
                }

                if (!hit || !_consumption.TryConsume(sweep.SlashId, fragment))
                {
                    continue;
                }

                _pending.Add(new Pending
                {
                    slashId = sweep.SlashId,
                    at = sweep.At,
                    atLatch = sweep.IsLatch,
                    fragment = fragment,
                    side = current.Side,
                    plane = plane,
                    renderAnchor = owner.position,
                    planeId = _adoptedPlanes,
                    planeWorld = worldPlane,
                });
            }
        }
    }
}
