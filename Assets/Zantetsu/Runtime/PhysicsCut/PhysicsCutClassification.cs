using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using Zantetsu.ConvexCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The robust-support scan of one accepted cut (DESIGN 7.6), as the numerical kernel asks for it: the signed
    /// distance and the exact sign of every vertex of the owner's shape against the adopted plane, and from those the
    /// disposition of each convex -- both supports **Split**, one side only that side, neither
    /// **NearPlaneToPositive**.
    /// <para>
    /// **One scan, both uses.** The kernel consumes this scan and never makes it (its input names the arrays), and the
    /// unpublished Provisional pair is allocated by the same dispositions. So it is made once, here, from the shape
    /// the cut was accepted against, and handed to both: <see cref="Input"/> for the cut and cook,
    /// <see cref="Sides"/> for the pair. Two scans could disagree; one cannot.
    /// </para>
    /// <para>
    /// **This is the classification scan, and it is not the mass scan.** It reads every vertex once because the
    /// kernel's input is per-vertex -- that is what a robust-support disposition is made of. The conservative box the
    /// temporary masses come from is a different thing and reads no vertex at all: each convex carries the box it was
    /// measured with (<see cref="PhysicsOwnerShape.ConvexBounds"/>). Nothing here revives that scan.
    /// </para>
    /// <para>
    /// **It owns the arrays the kernel points at**, so it has to outlive every reader of them: the cut request it was
    /// submitted with holds the pointers until that request is over. Disposing it while a request still names it would
    /// leave the kernel pointing at nothing, so whoever submits a request keeps this until the request has ended.
    /// </para>
    /// </summary>
    public sealed unsafe class PhysicsCutClassification : IDisposable
    {
        /// <summary>The one native block the kernel's per-convex and per-vertex inputs live in, each at a 16-byte-aligned offset.</summary>
        private NativeArray<byte> _block;
        private ConvexSide[] _sideValues;
        private ConvexCutOwnerInput _input;
        private static int s_live;

        /// <summary>Classifications holding a block right now (made and not disposed): a leak of one shows here.</summary>
        public static int Live => System.Threading.Volatile.Read(ref s_live);

        private PhysicsCutClassification()
        {
        }

        /// <summary>
        /// Scans <paramref name="shape"/> against <paramref name="planeLocal"/> -- a plane in the same numerical local
        /// frame as the shape's B-rep -- and holds the result.
        /// <para>
        /// False when the input does not hold together: no shape, a shape already given back, a plane that is not
        /// finite or has no direction, an epsilon that is negative or not finite, a mass that is not positive, a
        /// vertex limit that is not positive, or a vertex that is not finite. Nothing is allocated on a refusal. That
        /// is a cut that cannot be built, not one that is waiting.
        /// </para>
        /// </summary>
        /// <param name="supportEpsilon">
        /// The one distance epsilon of DESIGN 7.6: a vertex counts as support on a side only beyond it. **The caller's
        /// value.** It is not decided here and no value of this path's own is written down for it, and it is not the
        /// anchor distribution's epsilon (DESIGN 7.1) -- whether the product uses one number for both is the product's
        /// to say.
        /// </param>
        /// <param name="vertexLimit">The per-convex vertex limit L the kernel applies (DESIGN 7.2: 128). The caller's.</param>
        public static bool TryClassify(
            PhysicsOwnerShape shape,
            float4 planeLocal,
            float supportEpsilon,
            double parentMass,
            int vertexLimit,
            out PhysicsCutClassification classification)
        {
            classification = null;
            int bytes = RequiredBlockBytes(shape);
            if (bytes < 0)
            {
                return false;
            }

            return TryClassifyInto(shape, planeLocal, supportEpsilon, parentMass, vertexLimit, PhysicsCutBlocks.Take<byte>(bytes), out classification);
        }

        /// <summary>
        /// The size of the block one classification of this shape needs, so that it can be allocated on the main thread
        /// and the scan run elsewhere (<see cref="TryClassifyInto"/>). Negative for a shape that cannot be classified
        /// at all: none, given back, without convexes, or with an empty convex.
        /// </summary>
        public static int RequiredBlockBytes(PhysicsOwnerShape shape)
        {
            if (shape == null || shape.IsFreed || shape.ConvexCount <= 0)
            {
                return -1;
            }

            int convexCount = shape.ConvexCount;
            int vertices = 0;
            for (int c = 0; c < convexCount; c++)
            {
                ConvexBrepRange range = shape.Convex(c);
                if (range.vertexCount <= 0)
                {
                    return -1;
                }

                vertices = checked(vertices + range.vertexCount);
            }

            return checked((int)BlockBytes(convexCount, vertices));
        }

        private static long BlockBytes(int convexCount, int vertices)
        {
            long convexesAt = 0;
            long banksAt = convexesAt + Align16((long)convexCount * sizeof(ConvexBrepRange));
            long sidesAt = banksAt + Align16((long)convexCount * sizeof(ConvexBrepBank));
            long signedDistanceAt = sidesAt + Align16(convexCount);
            long signClassAt = signedDistanceAt + Align16((long)vertices * sizeof(float));
            long distanceBasesAt = signClassAt + Align16(vertices);
            return distanceBasesAt + Align16((long)convexCount * sizeof(int));
        }

        /// <summary>
        /// The same scan as <see cref="TryClassify"/> into a block the caller allocated (<see cref="RequiredBlockBytes"/>),
        /// which lets the scan run off the main thread, the shape held for work by the caller. The block is this
        /// classification's on every answer: given back on a refusal, owned on a success.
        /// </summary>
        public static bool TryClassifyInto(
            PhysicsOwnerShape shape,
            float4 planeLocal,
            float supportEpsilon,
            double parentMass,
            int vertexLimit,
            NativeArray<byte> given,
            out PhysicsCutClassification classification)
        {
            classification = null;
            if (shape == null || shape.IsFreed || shape.ConvexCount <= 0
                || !math.all(math.isfinite(planeLocal))
                || math.lengthsq(planeLocal.xyz) <= 0f
                || !(supportEpsilon >= 0f) || !math.isfinite(supportEpsilon)
                || !(parentMass > 0.0) || double.IsNaN(parentMass) || double.IsInfinity(parentMass)
                || vertexLimit <= 0)
            {
                if (given.IsCreated) given.Dispose();
                return false;
            }

            int convexCount = shape.ConvexCount;
            int vertices = 0;
            for (int c = 0; c < convexCount; c++)
            {
                ConvexBrepRange range = shape.Convex(c);
                if (range.vertexCount <= 0)
                {
                    if (given.IsCreated) given.Dispose();
                    return false;
                }

                vertices = checked(vertices + range.vertexCount);
            }

            if (!given.IsCreated || given.Length < BlockBytes(convexCount, vertices))
            {
                if (given.IsCreated) given.Dispose();
                return false;
            }

            var made = new PhysicsCutClassification();
            try
            {
                long convexesAt = 0;
                long banksAt = convexesAt + Align16((long)convexCount * sizeof(ConvexBrepRange));
                long sidesAt = banksAt + Align16((long)convexCount * sizeof(ConvexBrepBank));
                long signedDistanceAt = sidesAt + Align16(convexCount);
                long signClassAt = signedDistanceAt + Align16((long)vertices * sizeof(float));
                long distanceBasesAt = signClassAt + Align16(vertices);
                // Every byte of this block that is read is written by the scan below, over the whole range each
                // array is used across; a refusal part way reads none of it and gives it back.
                made._block = given;
                made._sideValues = new ConvexSide[convexCount];

                byte* block = (byte*)made._block.GetUnsafePtr();
                var convexes = (ConvexBrepRange*)(block + convexesAt);
                var banks = (ConvexBrepBank*)(block + banksAt);
                var sides = block + sidesAt;
                var signedDistance = (float*)(block + signedDistanceAt);
                var signClass = (sbyte*)(block + signClassAt);
                var distanceBases = (int*)(block + distanceBasesAt);

                int at = 0;
                bool setSupportsPositive = false;
                bool setSupportsNegative = false;
                for (int c = 0; c < convexCount; c++)
                {
                    ConvexBrepRange range = shape.Convex(c);
                    ConvexBrepBank bank = shape.BankOf(c);
                    convexes[c] = range;
                    banks[c] = bank;

                    // The distances of one convex are addressed from its own base, which is where this scan wrote
                    // them -- not from where its vertices happen to sit in the shape's bank.
                    distanceBases[c] = at;

                    int supportPositive = 0;
                    int supportNegative = 0;
                    for (int v = 0; v < range.vertexCount; v++)
                    {
                        float3 vertex = bank.vertices[range.vertexBase + v];
                        if (!math.all(math.isfinite(vertex)))
                        {
                            made.Dispose();
                            return false;
                        }

                        // The signed distance as it is, and the sign of it exactly: zero is on the plane and is
                        // neither support (DESIGN 7.6).
                        float s = math.dot(planeLocal.xyz, vertex) + planeLocal.w;
                        if (!math.isfinite(s))
                        {
                            // A finite vertex and a finite plane can still multiply and add to something that is not.
                            // Such a distance says nothing about which side anything is on, and it must not be read as
                            // "near the plane", which is what leaving it to the comparisons below would do.
                            made.Dispose();
                            return false;
                        }

                        signedDistance[at + v] = s;
                        signClass[at + v] = (sbyte)(s > 0f ? 1 : s < 0f ? -1 : 0);

                        if (s > supportEpsilon)
                        {
                            supportPositive++;
                            setSupportsPositive = true;
                        }
                        else if (s < -supportEpsilon)
                        {
                            supportNegative++;
                            setSupportsNegative = true;
                        }
                    }

                    ConvexSide side = supportPositive > 0 && supportNegative > 0 ? ConvexSide.Split
                        : supportNegative > 0 ? ConvexSide.Negative
                        : supportPositive > 0 ? ConvexSide.Positive
                        : ConvexSide.NearPlaneToPositive;
                    sides[c] = (byte)side;
                    made._sideValues[c] = side;
                    at += range.vertexCount;
                }

                made.SupportsPositive = setSupportsPositive;
                made.SupportsNegative = setSupportsNegative;

                made._input = new ConvexCutOwnerInput
                {
                    bank = default,
                    banks = banks,
                    convexes = convexes,
                    convexCount = convexCount,
                    sides = sides,
                    signedDistance = signedDistance,
                    signClass = signClass,
                    distanceBases = distanceBases,
                    plane = planeLocal,
                    parentMass = parentMass,
                    vertexLimit = vertexLimit,
                };
            }
            catch
            {
                made.Dispose();
                throw;
            }

            System.Threading.Interlocked.Increment(ref s_live);
            classification = made;
            return true;
        }

        /// <summary>Whether the arrays this holds have gone back.</summary>
        public bool IsDisposed { get; private set; }

        /// <summary>How many convexes were classified.</summary>
        public int ConvexCount => _sideValues?.Length ?? 0;

        /// <summary>
        /// The kernel's input for this cut: the shape's bank and ranges, this scan's arrays, the plane, the parent
        /// mass and the vertex limit. **It points into what this holds**, and into the shape's own bank, so both have
        /// to outlive every request it is submitted with.
        /// </summary>
        public ConvexCutOwnerInput Input => _input;

        /// <summary>
        /// The same dispositions the kernel will read, for the Provisional pair to be allocated by. It is the one
        /// classification, read a second way, not a second classification.
        /// </summary>
        public IReadOnlyList<ConvexSide> Sides => _sideValues;

        /// <summary>
        /// Whether **any vertex of the whole shape** is support on the positive side -- further from the plane than the
        /// epsilon, on that side (DESIGN 7.6). Kept from the one scan.
        /// </summary>
        public bool SupportsPositive { get; private set; }

        /// <summary>The same for the negative side.</summary>
        public bool SupportsNegative { get; private set; }

        /// <summary>
        /// Whether this plane cuts this shape at all: **the set has support on both sides** (DESIGN 7.6). That, and not
        /// how the convexes were allocated, is what an acceptance is decided by.
        /// <para>
        /// The two are not the same question. A convex with no support at all is allocated to the positive side -- that
        /// is the allocation rule and it is unchanged -- so a shape of one wholly negative convex beside one wholly
        /// near-plane convex would put a convex on each side while having no positive support anywhere. There is
        /// nothing on the positive side of that plane to cut off, and it is a no-op.
        /// </para>
        /// </summary>
        public bool SplitsBothSides => SupportsPositive && SupportsNegative;

        /// <summary>Gives the arrays back. Once; a second call does nothing.</summary>
        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            _input = default;
            _sideValues = null;
            if (_block.IsCreated) System.Threading.Interlocked.Decrement(ref s_live);
            Free(ref _block);
        }

        private static long Align16(long bytes)
        {
            return (bytes + 15) & ~15L;
        }

        private static void Free<T>(ref NativeArray<T> array)
            where T : struct
        {
            if (array.IsCreated)
            {
                array.Dispose();
            }
        }
    }
}
