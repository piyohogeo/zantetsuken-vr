using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
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
    /// **Nothing of the blade.** Only the waves' sweeps are read: the blade's own pose, its gate and whether it may
    /// fire play no part, so a wave already flying keeps hitting while the gesture cannot fire (T-040).
    /// </para>
    /// </summary>
    public sealed class SlashHitDetector
    {
        private readonly PhysicsOwnerRegistry _registry;
        private readonly LogicalCutLedger _ledger;
        private readonly ProvisionalCutDriver _driver;
        private readonly Func<bool> _open;
        private readonly SlashHitSettings _settings;
        private readonly SlashLineageConsumption _consumption;
        private readonly List<Pending> _pending = new List<Pending>(8);
        private readonly List<SlashHitConfirmed> _hits = new List<SlashHitConfirmed>(8);
        private readonly List<CurrentShape> _shapes = new List<CurrentShape>(16);
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
        }

        /// <summary>A detector over a composed cut world, open while that world is ready and not ending.</summary>
        public SlashHitDetector(CutWorldRoot world, in SlashHitSettings settings)
            : this(world.Owners, world.Ledger, world.Driver, in settings,
                () => world != null && world.IsReady && !world.IsEnding && !world.IsReleased && !world.TerminationRequested)
        {
        }

        /// <summary>The real hits of the last <see cref="Evaluate"/>, in the order they were passed on.</summary>
        public int HitCount => _hits.Count;

        public SlashHitConfirmed HitAt(int index) => _hits[index];

        /// <summary>What each live Slash has consumed.</summary>
        public SlashLineageConsumption Consumption => _consumption;

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
            _hits.Clear();
            _pending.Clear();

            // A Slash that has gone gives its set back first: it can hit nothing any more.
            _consumption.KeepOnly(live);
            if (sweeps.Length == 0 || (_open != null && !_open()))
            {
                return;
            }

            // What the fragments are made of now, read once: every sweep of this update is tested against the same
            // present state, and the acceptances below change the correspondence, not this list.
            _registry.CollectCurrentShapes(_shapes);
            for (int s = 0; s < sweeps.Length; s++)
            {
                Find(in sweeps[s]);
            }

            for (int i = 0; i < _pending.Count; i++)
            {
                Pending hit = _pending[i];
                var ask = new ProvisionalCutAsk
                {
                    source = hit.fragment,
                    plane = hit.plane,
                    positiveSeparationImpulse = _settings.positiveSeparationImpulse,
                    negativeSeparationImpulse = _settings.negativeSeparationImpulse,
                    renderAnchor = hit.renderAnchor,
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
                });
            }
        }
    }
}
