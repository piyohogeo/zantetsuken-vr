using Unity.Burst;
using Unity.Mathematics;

namespace Zantetsu.ConvexCut
{
    /// <summary>
    /// Robust-support disposition of one input convex (DESIGN 7.6), decided by the caller from the one
    /// distance epsilon. The kernel only consumes it: Split convexes are clipped at d = 0, the others are
    /// inherited uncut by the named side (no support at all inherits to the positive side).
    /// </summary>
    public enum ConvexSide : byte { Positive = 0, Negative = 1, Split = 2, NearPlaneToPositive = 3 }

    public enum ConvexCutOwnerStatus : int
    {
        Ok = 0,
        InvalidInput = 1,
        CapacityOutput = 2,
        CapacityScratch = 3,
        CutFailed = 4,
        ReductionFailed = 5,
        EmptySide = 6,
        MassPropertiesFailed = 7,
    }

    /// <summary>
    /// Numerical input of one owner cut (DESIGN 7.2 "数値Kernel"): the current compound B-rep, the adopted plane in
    /// the same local frame, the per-vertex signed distance / exact sign and the per-convex 7.6 disposition from the
    /// caller's robust-support scan, the parent mass and the vertex limit L. Everything is caller-owned and is held
    /// immutable from scheduling until completion; the kernel never writes through these pointers.
    /// </summary>
    public unsafe struct ConvexCutOwnerInput
    {
        public ConvexBrepBank bank;
        public ConvexBrepRange* convexes;
        public int convexCount;
        /// <summary><see cref="ConvexSide"/> per convex.</summary>
        public byte* sides;
        /// <summary>Raw signed distance s(x) = dot(plane.xyz, x) + plane.w per vertex, at distanceBases[c] + local index.</summary>
        public float* signedDistance;
        /// <summary>Exact sign of the signed distance per vertex (+1: d &gt; 0, -1: d &lt; 0, 0: d == 0), same indexing.</summary>
        public sbyte* signClass;
        public int* distanceBases;
        public float4 plane;
        public double parentMass;
        /// <summary>Per-convex vertex limit L applied to every output convex (DESIGN 7.2: L = 128).</summary>
        public int vertexLimit;
    }

    /// <summary>
    /// Caller-reserved output of one owner cut: the output B-rep arena (sized by <see cref="ConvexCutOwnerKernel.QueryCapacity"/>),
    /// one <see cref="ConvexCutOutcome"/> per input convex and the scratch block. The kernel writes only inside these ranges.
    /// </summary>
    public unsafe struct ConvexCutOwnerOutput
    {
        public ConvexBrepBank bank;
        public int vertexCapacity, faceOffsetCapacity, faceIndexCapacity, edgeCapacity;
        public ConvexCutOutcome* outcomes;
        public byte* scratch;
        public int scratchBytes;
    }

    /// <summary>
    /// Correspondence of one input convex to the result (valid for this call only, no persistent convex id):
    /// Split convexes name their positive and negative output ranges in the output arena; every other convex is
    /// inherited uncut by <see cref="side"/> (NearPlaneToPositive counts as positive) and has no output range.
    /// </summary>
    public struct ConvexCutOutcome
    {
        public ConvexSide side;
        public ConvexBrepRange positive, negative;
        public int cutStatus, reductionStatus, removedVertices, r1Invoked;
        public bool IsSplit => side == ConvexSide.Split;
        public bool InheritsPositive => side == ConvexSide.Positive || side == ConvexSide.NearPlaneToPositive;
    }

    /// <summary>Worst-case reservation for one owner cut. Owned by the kernel side (DESIGN 7.2 "容量").</summary>
    public struct ConvexCutOwnerCapacity
    {
        public int vertices, faceOffsets, faceIndices, edges, scratchBytes, splitConvexCount;
        /// <summary>Output convex count when every Split convex yields two sides.</summary>
        public int OutputConvexCount => 2 * splitConvexCount;
    }

    public struct ConvexCutOwnerResult
    {
        public ConvexCutOwnerStatus status;
        /// <summary>Input convex index the failure occurred on (-1 when not convex-specific).</summary>
        public int failedConvex;
        public int cutStatus, reductionStatus;
        public int positiveConvexCount, negativeConvexCount, splitConvexCount, reducedConvexCount, removedVertices, r1Invocations;
        public double positiveVolume, negativeVolume;
        public double positiveMass, negativeMass;
        public double3 positiveCenterOfMass, negativeCenterOfMass;
        public SymmetricMatrix3 positiveInertia, negativeInertia;
        /// <summary>Elements actually written (counts) and reserved by the internal plan (never above the output capacities).</summary>
        public int usedVertices, usedFaceOffsets, usedFaceIndices, usedEdges, usedScratchBytes;
        public int reservedVertices, reservedFaceOffsets, reservedFaceIndices, reservedEdges;
        /// <summary>1 when the kernel body ran as managed code instead of Burst (test diagnostic; never set under Burst).</summary>
        public byte executedManaged;
    }

    /// <summary>
    /// Owner-level numerical kernel (DESIGN 7.2): non-intersecting convexes inherit their side, Split convexes are clipped
    /// exactly at d = 0 (<see cref="ConvexClipKernel"/>), outputs above L are reduced in place (<see cref="ConvexReductionKernel"/>),
    /// and mass / center of mass / inertia are integrated over the adopted convex sets (<see cref="MassPropertiesKernel"/>).
    /// Runs entirely inside the caller-provided scratch and output ranges, retains nothing and touches no Unity object.
    /// </summary>
    [BurstCompile(CompileSynchronously = true)]
    public static unsafe class ConvexCutOwnerKernel
    {
        const int k_align = 64;

        struct SplitPlan
        {
            public ClipCapacity pos, neg;
            public int cutCap, hashCap, boundaryCap, cutBytes, reductionBytes;
            public int ScratchBytes => Align(cutBytes) + reductionBytes;
        }

        static int Align(int bytes) => (bytes + k_align - 1) / k_align * k_align;

        /// <summary>Counts the exact-sign classes of one convex from the caller's scan (no clip, no output).</summary>
        static void CountSigns(in ConvexCutOwnerInput input, int c, out int vPos, out int vNeg, out int vOn)
        {
            var r = input.convexes[c];
            sbyte* cls = input.signClass + input.distanceBases[c];
            vPos = 0; vNeg = 0; vOn = 0;
            for (int i = 0; i < r.vertexCount; i++) { sbyte k = cls[i]; if (k > 0) vPos++; else if (k < 0) vNeg++; else vOn++; }
        }

        /// <summary>The per-split reservation: the same formula drives the capacity query and the execution plan.</summary>
        static SplitPlan PlanSplit(in ConvexBrepRange r, int vPos, int vNeg, int vOn)
        {
            var p = new SplitPlan();
            p.pos = ClipCapacity.Snapshot(r.vertexCount, r.edgeCount, r.faceCount, r.faceIndexCount, r.maxFaceLoop, vPos, vOn);
            p.neg = ClipCapacity.Snapshot(r.vertexCount, r.edgeCount, r.faceCount, r.faceIndexCount, r.maxFaceLoop, vNeg, vOn);
            p.cutCap = math.max(p.pos.cutK, p.neg.cutK);
            p.hashCap = math.max(p.pos.hashCap, p.neg.hashCap);
            p.boundaryCap = math.max(p.pos.vOut, p.neg.vOut);
            CutScratch.Layout(null, r.vertexCount, r.edgeCount, r.maxFaceLoop, p.cutCap, p.hashCap, p.boundaryCap, out p.cutBytes);
            int capV = math.max(p.pos.vOut, p.neg.vOut), capE = math.max(p.pos.eOut, p.neg.eOut), capF = math.max(p.pos.fOut, p.neg.fOut), capI = math.max(p.pos.iOut, p.neg.iOut);
            ReductionScratch.Layout(null, capV, capE, capF, capI, out p.reductionBytes);
            return p;
        }

        /// <summary>
        /// Worst-case scratch / output reservation for <paramref name="input"/> from the convex descriptors, the 7.6
        /// dispositions and the exact-sign counts of the scan. Performs no clip and writes no output (DESIGN 7.2 "容量").
        /// </summary>
        [BurstCompile]
        public static void QueryCapacity(in ConvexCutOwnerInput input, ref ConvexCutOwnerCapacity cap)
        {
            cap = default;
            for (int c = 0; c < input.convexCount; c++)
            {
                if ((ConvexSide)input.sides[c] != ConvexSide.Split) continue;
                CountSigns(in input, c, out int vPos, out int vNeg, out int vOn);
                var p = PlanSplit(in input.convexes[c], vPos, vNeg, vOn);
                cap.vertices += p.pos.vOut + p.neg.vOut;
                cap.faceOffsets += p.pos.fOut + 1 + p.neg.fOut + 1;
                cap.faceIndices += p.pos.iOut + p.neg.iOut;
                cap.edges += p.pos.eOut + p.neg.eOut;
                cap.scratchBytes = math.max(cap.scratchBytes, p.ScratchBytes);
                cap.splitConvexCount++;
            }
        }

        [BurstDiscard]
        static void MarkManaged(ref ConvexCutOwnerResult r) { r.executedManaged = 1; }

        /// <summary>
        /// Executes one owner cut. On any status other than Ok the outputs and mass properties are invalid; the
        /// reserved ranges may have been partially written but nothing outside them was touched.
        /// </summary>
        [BurstCompile]
        public static void Execute(in ConvexCutOwnerInput input, in ConvexCutOwnerOutput output, ref ConvexCutOwnerResult result)
        {
            result = default;
            result.failedConvex = -1;
            MarkManaged(ref result);
            if (input.convexCount <= 0 || input.vertexLimit < 4) { result.status = ConvexCutOwnerStatus.InvalidInput; return; }

            var massPos = new MassProperties(); var massNeg = new MassProperties();
            int posCount = 0, negCount = 0;
            int vCur = 0, fCur = 0, iCur = 0, eCur = 0;

            for (int c = 0; c < input.convexCount; c++)
            {
                var side = (ConvexSide)input.sides[c];
                var r = input.convexes[c];
                var outcome = new ConvexCutOutcome { side = side };
                if (side != ConvexSide.Split)
                {
                    if (side != ConvexSide.Positive && side != ConvexSide.Negative && side != ConvexSide.NearPlaneToPositive)
                    { result.status = ConvexCutOwnerStatus.InvalidInput; result.failedConvex = c; output.outcomes[c] = outcome; return; }
                    var view = input.bank.View(in r);
                    var m = new MassProperties();
                    MassPropertiesKernel.ComputeDouble(in view, ref m);
                    if (side == ConvexSide.Negative) { massNeg = massNeg + m; negCount++; } else { massPos = massPos + m; posCount++; }
                    output.outcomes[c] = outcome;
                    continue;
                }

                // ---- Split: plan (same formula as QueryCapacity), reserve inside the arena, clip, reduce, integrate ----
                CountSigns(in input, c, out int vPos, out int vNeg, out int vOn);
                var plan = PlanSplit(in r, vPos, vNeg, vOn);
                int needV = plan.pos.vOut + plan.neg.vOut, needF = plan.pos.fOut + 1 + plan.neg.fOut + 1, needI = plan.pos.iOut + plan.neg.iOut, needE = plan.pos.eOut + plan.neg.eOut;
                if (vCur + needV > output.vertexCapacity || fCur + needF > output.faceOffsetCapacity || iCur + needI > output.faceIndexCapacity || eCur + needE > output.edgeCapacity)
                { result.status = ConvexCutOwnerStatus.CapacityOutput; result.failedConvex = c; output.outcomes[c] = outcome; return; }
                if (plan.ScratchBytes > output.scratchBytes || output.scratch == null)
                { result.status = ConvexCutOwnerStatus.CapacityScratch; result.failedConvex = c; output.outcomes[c] = outcome; return; }
                result.usedScratchBytes = math.max(result.usedScratchBytes, plan.ScratchBytes);

                int posV = vCur, posF = fCur, posI = iCur, posE = eCur;
                int negV = posV + plan.pos.vOut, negF = posF + plan.pos.fOut + 1, negI = posI + plan.pos.iOut, negE = posE + plan.pos.eOut;
                vCur += needV; fCur += needF; iCur += needI; eCur += needE;
                var pos = output.bank.OutputView(posV, plan.pos.vOut, posF, plan.pos.fOut, posI, plan.pos.iOut, posE, plan.pos.eOut);
                var neg = output.bank.OutputView(negV, plan.neg.vOut, negF, plan.neg.fOut, negI, plan.neg.iOut, negE, plan.neg.eOut);

                var inputView = input.bank.View(in r);
                var cutScratch = CutScratch.Layout(output.scratch, r.vertexCount, r.edgeCount, r.maxFaceLoop, plan.cutCap, plan.hashCap, plan.boundaryCap, out int cutBytes);
                var stats = new CutStats();
                int base_ = input.distanceBases[c];
                int cs = ConvexClipKernel.Cut(in inputView, input.signedDistance + base_, input.signClass + base_, in input.plane, ref pos, ref neg, ref cutScratch, ref stats);
                outcome.cutStatus = cs;
                if (cs != (int)CutStatus.Ok)
                { result.status = ConvexCutOwnerStatus.CutFailed; result.cutStatus = cs; result.failedConvex = c; output.outcomes[c] = outcome; return; }

                // inscribed reduction of any side above L (in place, inside the side's reserved range)
                byte* reductionBase = output.scratch + Align(cutBytes);
                for (int sideIdx = 0; sideIdx < 2; sideIdx++)
                {
                    ref BrepBuffer b = ref sideIdx == 0 ? ref pos : ref neg;
                    if (b.V <= input.vertexLimit) continue;
                    var rs = ReductionScratch.Layout(reductionBase, b.vCap, b.eCap, b.fCap, b.iCap, out _);
                    var rstats = new ReductionStats();
                    int rr = ConvexReductionKernel.Reduce(ref b, input.vertexLimit, ref rs, ref rstats);
                    outcome.reductionStatus = rr;
                    outcome.removedVertices += rstats.removedR0 + rstats.removedR1;
                    if (rstats.r1Invoked != 0) { outcome.r1Invoked = 1; result.r1Invocations++; }
                    result.reducedConvexCount++;
                    result.removedVertices += rstats.removedR0 + rstats.removedR1;
                    if (rr != (int)ReductionStatus.Ok)
                    { result.status = ConvexCutOwnerStatus.ReductionFailed; result.reductionStatus = rr; result.failedConvex = c; output.outcomes[c] = outcome; return; }
                }

                outcome.positive = RangeOf(in pos, posV, posF, posI, posE);
                outcome.negative = RangeOf(in neg, negV, negF, negI, negE);
                var mp = new MassProperties(); var mn = new MassProperties();
                MassPropertiesKernel.ComputeDouble(in pos, ref mp);
                MassPropertiesKernel.ComputeDouble(in neg, ref mn);
                massPos = massPos + mp; massNeg = massNeg + mn;
                posCount++; negCount++;
                result.splitConvexCount++;
                result.usedVertices += pos.V + neg.V; result.usedFaceOffsets += pos.F + 1 + neg.F + 1; result.usedFaceIndices += pos.I + neg.I; result.usedEdges += pos.E + neg.E;
                output.outcomes[c] = outcome;
            }

            result.reservedVertices = vCur; result.reservedFaceOffsets = fCur; result.reservedFaceIndices = iCur; result.reservedEdges = eCur;
            result.positiveConvexCount = posCount; result.negativeConvexCount = negCount;
            result.positiveVolume = massPos.volume; result.negativeVolume = massNeg.volume;
            if (posCount == 0 || negCount == 0) { result.status = ConvexCutOwnerStatus.EmptySide; return; }

            // ---- Final mass properties (DESIGN 7.2): parent mass split by raw volume, same density on both sides ----
            double vp = massPos.volume, vn = massNeg.volume, M = input.parentMass;
            if (!(vp > 0 && vn > 0 && math.isfinite(vp) && math.isfinite(vn) && M > 0 && math.isfinite(M))) { result.status = ConvexCutOwnerStatus.MassPropertiesFailed; return; }
            result.positiveMass = M * vp / (vp + vn);
            result.negativeMass = M - result.positiveMass;
            double rho = M / (vp + vn);
            result.positiveCenterOfMass = massPos.CenterOfMass; result.negativeCenterOfMass = massNeg.CenterOfMass;
            result.positiveInertia = massPos.InertiaAboutCom() * rho; result.negativeInertia = massNeg.InertiaAboutCom() * rho;
            bool finite = result.positiveMass > 0 && result.negativeMass > 0 && math.isfinite(result.positiveMass) && math.isfinite(result.negativeMass)
                          && math.all(math.isfinite(result.positiveCenterOfMass)) && math.all(math.isfinite(result.negativeCenterOfMass))
                          && result.positiveInertia.IsFinite && result.negativeInertia.IsFinite;
            result.status = finite ? ConvexCutOwnerStatus.Ok : ConvexCutOwnerStatus.MassPropertiesFailed;
        }

        static ConvexBrepRange RangeOf(in BrepBuffer b, int vertexBase, int faceBase, int faceIndexBase, int edgeBase)
        {
            int lmax = 0;
            for (int f = 0; f < b.F; f++) lmax = math.max(lmax, b.faceOff[f + 1] - b.faceOff[f]);
            return new ConvexBrepRange
            {
                vertexBase = vertexBase, vertexCount = b.V, faceBase = faceBase, faceCount = b.F,
                faceIndexBase = faceIndexBase, faceIndexCount = b.I, edgeBase = edgeBase, edgeCount = b.E, maxFaceLoop = lmax,
            };
        }
    }
}
