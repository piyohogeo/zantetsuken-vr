using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>One convex B-rep in a block of its own, for the trim's tests: made from faces, or cut from another by the cut's own clip.</summary>
    internal sealed unsafe class TrimPoly : IDisposable
    {
        private NativeArray<byte> _block;
        internal BrepBuffer B;

        internal ConvexBrepBank Bank => new ConvexBrepBank { vertices = B.v, faceOffsets = B.faceOff, faceIndices = B.faceIdx, faceEdges = B.faceEdge, edges = B.edges };

        internal ConvexBrepRange Range => new ConvexBrepRange
        {
            vertexCount = B.V, faceCount = B.F, faceIndexCount = B.I, edgeCount = B.E, maxFaceLoop = ConvexTrimSearch.MaxFaceLoop(in B),
        };

        internal float3[] Vertices
        {
            get
            {
                var v = new float3[B.V];
                for (int i = 0; i < v.Length; i++) v[i] = B.v[i];
                return v;
            }
        }

        internal double Volume
        {
            get
            {
                var m = default(MassProperties);
                MassPropertiesKernel.ComputeDouble(in B, ref m);
                return m.volume;
            }
        }

        private static TrimPoly Allocate(int v, int f, int i, int e)
        {
            long vAt = 0;
            long fAt = vAt + Align((long)Math.Max(1, v) * sizeof(float3));
            long iAt = fAt + Align((long)(f + 1) * sizeof(int));
            long feAt = iAt + Align((long)Math.Max(1, i) * sizeof(int));
            long eAt = feAt + Align((long)Math.Max(1, i) * sizeof(int));
            long bytes = eAt + Align((long)Math.Max(1, e) * sizeof(BrepEdge));
            var poly = new TrimPoly { _block = new NativeArray<byte>((int)bytes, Allocator.Persistent) };
            byte* at = (byte*)poly._block.GetUnsafePtr();
            poly.B = new BrepBuffer
            {
                v = (float3*)(at + vAt), faceOff = (int*)(at + fAt), faceIdx = (int*)(at + iAt), faceEdge = (int*)(at + feAt), edges = (BrepEdge*)(at + eAt),
                vCap = v, fCap = f, iCap = i, eCap = e,
            };
            return poly;
        }

        private static long Align(long bytes) => (bytes + 15) & ~15L;

        internal static TrimPoly From(IReadOnlyList<float3> vertices, int[] faceOffsets, int[] faceIndices)
        {
            int f = faceOffsets.Length - 1;
            TrimPoly poly = Allocate(vertices.Count, f, faceIndices.Length, faceIndices.Length / 2);
            for (int i = 0; i < vertices.Count; i++) poly.B.v[i] = vertices[i];
            for (int i = 0; i <= f; i++) poly.B.faceOff[i] = faceOffsets[i];
            for (int i = 0; i < faceIndices.Length; i++) poly.B.faceIdx[i] = faceIndices[i];
            poly.B.V = vertices.Count; poly.B.F = f; poly.B.I = faceIndices.Length;
            int hashCap = math.ceilpow2(math.max(16, 2 * faceIndices.Length));
            var keys = new NativeArray<long>(hashCap, Allocator.Temp);
            var values = new NativeArray<int>(hashCap, Allocator.Temp);
            int probes = 0;
            int status = ConvexClipKernel.BuildEdgeTable(ref poly.B, (long*)keys.GetUnsafePtr(), (int*)values.GetUnsafePtr(), hashCap, ref probes);
            keys.Dispose();
            values.Dispose();
            if (status != 0)
            {
                poly.Dispose();
                throw new InvalidOperationException("not a closed two-manifold convex: " + (CutStatus)status);
            }

            return poly;
        }

        /// <summary>The box between two corners, its faces wound the same way round.</summary>
        internal static TrimPoly Box(float3 lo, float3 hi)
        {
            var v = new float3[8];
            for (int i = 0; i < 8; i++) v[i] = new float3((i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z);
            return From(v, new[] { 0, 4, 8, 12, 16, 20, 24 }, new[] { 0, 4, 6, 2, 1, 3, 7, 5, 0, 1, 5, 4, 2, 6, 7, 3, 0, 2, 3, 1, 4, 5, 7, 6 });
        }

        /// <summary>
        /// The part of this convex on one side of a plane (s(x) = dot(n, x) + w; positive: s > 0), by the cut's own clip: this
        /// convex itself where the plane does not split it and it lies on that side (<paramref name="whole"/>), null where it
        /// lies on the other.
        /// </summary>
        internal TrimPoly Keep(float4 plane, bool positive, out bool whole)
        {
            whole = false;
            var sd = new NativeArray<float>(B.V, Allocator.Temp);
            var cls = new NativeArray<sbyte>(B.V, Allocator.Temp);
            bool anyWanted = false, anyOther = false;
            for (int i = 0; i < B.V; i++)
            {
                float s = math.dot(plane.xyz, B.v[i]) + plane.w;
                sd[i] = s;
                cls[i] = (sbyte)(s > 0f ? 1 : s < 0f ? -1 : 0);
                if (s != 0f && (s > 0f) == positive) anyWanted = true;
                else if (s != 0f) anyOther = true;
            }

            try
            {
                if (!anyWanted) return null;
                if (!anyOther)
                {
                    whole = true;
                    return this;
                }

                int loop = ConvexTrimSearch.MaxFaceLoop(in B);
                ClipCapacity cap = ClipCapacity.Static(B.V, B.E, B.F, B.I, loop);
                TrimPoly pos = Allocate(cap.vOut, cap.fOut, cap.iOut, cap.eOut);
                TrimPoly neg = Allocate(cap.vOut, cap.fOut, cap.iOut, cap.eOut);
                CutScratch.Layout(null, B.V, B.E, loop, cap.cutK, cap.hashCap, cap.vOut, out int scratchBytes);
                var scratchBlock = new NativeArray<byte>(scratchBytes, Allocator.Temp);
                CutScratch scratch = CutScratch.Layout((byte*)scratchBlock.GetUnsafePtr(), B.V, B.E, loop, cap.cutK, cap.hashCap, cap.vOut, out _);
                var stats = default(CutStats);
                int status = ConvexClipKernel.Cut(in B, (float*)sd.GetUnsafePtr(), (sbyte*)cls.GetUnsafePtr(), in plane, ref pos.B, ref neg.B, ref scratch, ref stats);
                scratchBlock.Dispose();
                if (status == (int)CutStatus.NotSplit)
                {
                    // At float resolution the plane only touches it: it lies where its vertices are.
                    pos.Dispose();
                    neg.Dispose();
                    whole = true;
                    return this;
                }

                if (status != 0)
                {
                    pos.Dispose();
                    neg.Dispose();
                    throw new InvalidOperationException("the clip failed: " + (CutStatus)status);
                }

                (positive ? neg : pos).Dispose();
                return positive ? pos : neg;
            }
            finally
            {
                sd.Dispose();
                cls.Dispose();
            }
        }

        public void Dispose()
        {
            if (_block.IsCreated) _block.Dispose();
            _block = default;
        }
    }

    /// <summary>A trim run over convexes and a mesh made in a test: the workspace, the planes, the outcome.</summary>
    internal sealed unsafe class TrimRun : IDisposable
    {
        private NativeArray<byte> _block;
        internal ConvexTrimWorkspace W;
        internal ConvexTrimOutcome Outcome;
        internal float[] Support = new float[ConvexTrimSearch.Directions];

        internal static TrimRun Of(IReadOnlyList<TrimPoly> convexes, IReadOnlyList<float3> mesh, float minOverhang = 0.1f, int vertexLimit = 128,
            double minKeptVolume = 1e-6, double minKeptVolumeRatio = 0.01, float4x4? geometryToOwner = null, float4x4? shapeToOwner = null)
        {
            var run = new TrimRun();
            var ranges = convexes.Select(c => c.Range).ToArray();
            ConvexTrimWorkspace.Layout(null, ranges, vertexLimit, out long bytes);
            run._block = new NativeArray<byte>((int)bytes, Allocator.Persistent);
            run.W = ConvexTrimWorkspace.Layout((byte*)run._block.GetUnsafePtr(), ranges, vertexLimit, out _);
            for (int c = 0; c < convexes.Count; c++) run.W.banks[c] = convexes[c].Bank;
            var vertices = mesh.Select(p => new VpRenderVertex { position = p }).ToArray();
            var indices = Enumerable.Range(0, vertices.Length).Select(i => (uint)i).ToArray();
            float4x4 g = geometryToOwner ?? float4x4.identity, s = shapeToOwner ?? float4x4.identity;
            fixed (VpRenderVertex* v = vertices)
            fixed (uint* ix = indices)
            fixed (float* support = run.Support)
            {
                Assert.That(ConvexTrimSearch.MeshSupport(v, vertices.Length, ix, indices.Length, in g, support), Is.True, "the mesh's support planes");
                ConvexTrimSearch.SetPlanes(ref run.W, in s, support);
            }

            ConvexTrimSearch.Run(ref run.W, minOverhang, vertexLimit, minKeptVolume, minKeptVolumeRatio, out run.Outcome);
            return run;
        }

        internal ref ConvexTrimConvex Convex(int c) => ref W.convexes[c];

        internal int Left(ConvexTrimRefusal reason) => Outcome.left[(int)reason];

        public void Dispose()
        {
            if (_block.IsCreated) _block.Dispose();
            _block = default;
        }
    }

    /// <summary>
    /// The numbers of the convex trim (<see cref="ConvexTrimSearch"/>, 2026-11-20), over convexes and meshes made here and
    /// over the walk city's tree_012 cut level at 6 m: the 26 directions, the support planes, the trims adopted and the
    /// candidates left alone with their reasons, and the vertex counts of the preliminary calculation (159 to 168).
    /// </summary>
    public unsafe class ConvexTrimSearchTests
    {
        private static float3[] Corners(float3 lo, float3 hi)
        {
            var v = new float3[8];
            for (int i = 0; i < 8; i++) v[i] = new float3((i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z);
            return v;
        }

        [Test]
        public void TheDirections_AreTheTwentySixOfTheFrame_EachOfUnitLength_EachWithItsOpposite()
        {
            var seen = new HashSet<(int, int, int)>();
            for (int k = 0; k < ConvexTrimSearch.Directions; k++)
            {
                float3 d = ConvexTrimSearch.Direction(k);
                Assert.That(math.length(d), Is.EqualTo(1f).Within(1e-6f), "direction " + k);
                var key = ((int)math.sign(d.x), (int)math.sign(d.y), (int)math.sign(d.z));
                Assert.That(key, Is.Not.EqualTo((0, 0, 0)));
                Assert.That(seen.Add(key), Is.True, "direction " + k + " twice");
                float3 opposite = ConvexTrimSearch.Direction(ConvexTrimSearch.Directions - 1 - k);
                Assert.That(math.dot(d, opposite), Is.EqualTo(-1f).Within(1e-6f), "direction " + k + " and its opposite");
            }

            // the order the preliminary calculation used: x outermost, z innermost
            Assert.That(ConvexTrimSearch.Direction(4), Is.EqualTo(new float3(-1f, 0f, 0f)));
            Assert.That(ConvexTrimSearch.Direction(13), Is.EqualTo(new float3(0f, 0f, 1f)));
            Assert.That(math.distance(ConvexTrimSearch.Direction(22), math.normalize(new float3(1f, 0f, 1f))), Is.LessThan(1e-6f));
        }

        [Test]
        public void AConvexThatReachesBeyondTheMesh_IsCutAtTheMeshsPlane_AndOneThatDoesNotIsLeftAsItWas()
        {
            using (TrimPoly sticking = TrimPoly.Box(new float3(-0.3f, 0f, 0f), new float3(1f, 1f, 1f)))
            using (TrimPoly inside = TrimPoly.Box(new float3(0.2f, 0.2f, 0.2f), new float3(0.8f, 0.8f, 0.8f)))
            using (TrimRun run = TrimRun.Of(new[] { sticking, inside }, Corners(float3.zero, new float3(1f))))
            {
                Assert.That(run.Outcome.trims, Is.EqualTo(1));
                Assert.That(run.Outcome.trimmedConvexes, Is.EqualTo(1));
                Assert.That(run.Outcome.clipsTried, Is.EqualTo(1), "the -x direction reaches furthest, and its clip leaves nothing of the diagonals'");
                Assert.That(run.Outcome.overhangBefore, Is.EqualTo(0.3f).Within(1e-5f));
                Assert.That(run.Outcome.overhangAfter, Is.LessThan(1e-5f));
                Assert.That(run.Outcome.LeftInAll, Is.EqualTo(0));
                Assert.That(run.Convex(0).trims, Is.EqualTo(1));
                Assert.That(run.Convex(0).verticesBefore, Is.EqualTo(8));
                Assert.That(run.Convex(0).verticesAfter, Is.EqualTo(8));
                Assert.That(run.Convex(0).volumeBefore, Is.EqualTo(1.3).Within(1e-5));
                Assert.That(run.Convex(0).volumeAfter, Is.EqualTo(1.0).Within(1e-5));
                Assert.That(run.Convex(0).lo.x, Is.EqualTo(0f).Within(1e-6f), "cut at the mesh's own plane");
                Assert.That(run.Convex(0).hi.x, Is.EqualTo(1f).Within(1e-6f));
                Assert.That(run.Convex(1).trims, Is.EqualTo(0));
                Assert.That(run.Convex(1).copied, Is.EqualTo((byte)0), "a convex with no candidate is not even copied");
                Assert.That(run.Outcome.verticesBefore, Is.EqualTo(16));
                Assert.That(run.Outcome.verticesAfter, Is.EqualTo(16));
            }
        }

        [Test]
        public void TheCandidatesAreCounted_WithTheDiagonals()
        {
            // 0.3 beyond along -x is 0.212 beyond along the four edge diagonals with -x and 0.173 along the four corner ones:
            // nine candidates; the one clip at -x leaves none of them.
            using (TrimPoly sticking = TrimPoly.Box(new float3(-0.3f, 0f, 0f), new float3(1f, 1f, 1f)))
            using (TrimRun run = TrimRun.Of(new[] { sticking }, Corners(float3.zero, new float3(1f))))
            {
                Assert.That(run.Outcome.candidatesBefore, Is.EqualTo(9));
                Assert.That(run.Outcome.trims, Is.EqualTo(1));
                Assert.That(run.Outcome.clipsTried, Is.EqualTo(1));
                Assert.That(run.Outcome.LeftInAll, Is.EqualTo(0));
            }
        }

        [Test]
        public void BelowTheThreshold_NothingIsTried()
        {
            using (TrimPoly sticking = TrimPoly.Box(new float3(-0.09f, 0f, 0f), new float3(1f, 1f, 1f)))
            using (TrimRun run = TrimRun.Of(new[] { sticking }, Corners(float3.zero, new float3(1f))))
            {
                Assert.That(run.Outcome.candidatesBefore, Is.EqualTo(0));
                Assert.That(run.Outcome.clipsTried, Is.EqualTo(0));
                Assert.That(run.Outcome.trims, Is.EqualTo(0));
                Assert.That(run.Outcome.overhangBefore, Is.EqualTo(0.09f).Within(1e-5f));
                Assert.That(run.Outcome.overhangAfter, Is.EqualTo(0.09f).Within(1e-5f));
            }
        }

        [Test]
        public void AConvexWhollyBeyondThePlane_IsLeftAlone_AndTheOthersGoOn()
        {
            // A thin bar wholly beyond the mesh's -x plane, and beyond every diagonal plane it reaches past as well.
            using (TrimPoly beyond = TrimPoly.Box(new float3(-0.8f, 0.02f, 0.02f), new float3(-0.2f, 0.06f, 0.06f)))
            using (TrimPoly sticking = TrimPoly.Box(new float3(0f, 0f, 0f), new float3(1f, 1.5f, 1f)))
            using (TrimRun run = TrimRun.Of(new[] { beyond, sticking }, Corners(float3.zero, new float3(1f))))
            {
                Assert.That(run.Convex(0).trims, Is.EqualTo(0), "nothing of it would be kept");
                Assert.That(run.Left(ConvexTrimRefusal.NothingKept), Is.EqualTo(4), "-x, the two edge diagonals with -y and with -z, and the corner diagonal");
                Assert.That(run.Outcome.LeftInAll, Is.EqualTo(4));
                Assert.That(run.Convex(1).trims, Is.EqualTo(1), "the other convex is trimmed all the same");
                Assert.That(run.Convex(1).hi.y, Is.EqualTo(1f).Within(1e-6f));
                Assert.That(run.Outcome.overhangAfter, Is.EqualTo(0.8f).Within(1e-5f), "the convex left alone still reaches as far");
            }
        }

        [Test]
        public void WhatWouldBeKeptOfTooLittleVolume_IsLeftAlone()
        {
            // 0.95 of 1.0 lies beyond the mesh's -x plane: 5 % would be kept, and the test asks for 10 %. (The mesh is wide
            // in y and z, so that no diagonal plane comes near the convex.)
            using (TrimPoly mostly = TrimPoly.Box(new float3(-0.95f, 0f, 0f), new float3(0.05f, 1f, 1f)))
            using (TrimRun run = TrimRun.Of(new[] { mostly }, Corners(new float3(0f, -5f, -5f), new float3(1f, 5f, 5f)), minKeptVolumeRatio: 0.1))
            {
                Assert.That(run.Outcome.candidatesBefore, Is.EqualTo(1));
                Assert.That(run.Outcome.trims, Is.EqualTo(0));
                Assert.That(run.Left(ConvexTrimRefusal.VolumeShort), Is.EqualTo(1));
                Assert.That(run.Convex(0).verticesAfter, Is.EqualTo(8));
            }
        }

        [Test]
        public void ATrimPastTheVertexLimit_IsLeftAlone_AndNothingIsReduced()
        {
            // A box the mesh's corner diagonal cuts a corner off, half way along the three edges that meet there: the kept
            // part has ten vertices (seven corners and three cut points), and the limit here is eight.
            float3[] mesh =
            {
                new float3(0f, 0f, 0f), new float3(1f, 0f, 0f), new float3(0f, 1f, 0f), new float3(0f, 0f, 1f),
                new float3(1f, 1f, 0f), new float3(1f, 0f, 1f), new float3(0f, 1f, 1f),
                new float3(1f, 1f, 0.5f), new float3(1f, 0.5f, 1f), new float3(0.5f, 1f, 1f),
            };
            using (TrimPoly box = TrimPoly.Box(float3.zero, new float3(1f)))
            using (TrimRun limited = TrimRun.Of(new[] { box }, mesh, vertexLimit: 8))
            using (TrimRun free = TrimRun.Of(new[] { box }, mesh))
            {
                Assert.That(limited.Outcome.trims, Is.EqualTo(0));
                Assert.That(limited.Left(ConvexTrimRefusal.VertexLimit), Is.EqualTo(1));
                Assert.That(limited.Convex(0).verticesAfter, Is.EqualTo(8));
                Assert.That(free.Outcome.trims, Is.EqualTo(1), "within the limit the same corner is cut off");
                Assert.That(free.Convex(0).verticesAfter, Is.EqualTo(10));
                Assert.That(free.Outcome.overhangBefore, Is.EqualTo(0.5f / math.sqrt(3f)).Within(1e-5f));
            }
        }

        [Test]
        public void TheTrimsOfOneConvex_FollowOneAnother_TheFurthestReachFirst()
        {
            using (TrimPoly sticking = TrimPoly.Box(new float3(-0.5f, -0.2f, 0f), new float3(1f, 1f, 1.3f)))
            using (TrimRun run = TrimRun.Of(new[] { sticking }, Corners(float3.zero, new float3(1f))))
            {
                Assert.That(run.Convex(0).trims, Is.GreaterThanOrEqualTo(3), "-x, +z and -y at the least, each cut at the mesh; a diagonal that reaches further goes before them");
                Assert.That(run.Outcome.overhangBefore, Is.EqualTo((0.5f + 0.3f + 0.2f) / math.sqrt(3f)).Within(1e-4f),
                    "the corner diagonal reaches furthest: (0.5 + 0.3 + 0.2) / sqrt 3");
                Assert.That(math.distance(run.Convex(0).lo, float3.zero), Is.LessThan(1e-5f));
                Assert.That(math.distance(run.Convex(0).hi, new float3(1f)), Is.LessThan(1e-5f));
                Assert.That(run.Convex(0).volumeAfter, Is.EqualTo(1.0).Within(1e-4));
                Assert.That(run.Outcome.overhangAfter, Is.LessThan(1e-5f));
            }
        }

        [Test]
        public void TheFramesAreCarried_TheMeshsAndTheShapes()
        {
            // The same scene with the mesh and the convex each in a frame of its own: the owner sees what it saw before.
            float4x4 g = float4x4.TRS(new float3(2f, -1f, 0.5f), quaternion.Euler(0.3f, 1.1f, -0.4f), new float3(1f));
            float4x4 s = float4x4.TRS(new float3(-0.7f, 0.2f, 3f), quaternion.Euler(-0.9f, 0.2f, 0.6f), new float3(1f));
            float4x4 gInverse = math.inverse(g), sInverse = math.inverse(s);
            float3[] mesh = Corners(float3.zero, new float3(1f)).Select(p => math.transform(gInverse, p)).ToArray();
            float3[] corners = Corners(new float3(-0.3f, 0f, 0f), new float3(1f, 1f, 1f)).Select(p => math.transform(sInverse, p)).ToArray();
            using (TrimPoly sticking = TrimPoly.From(corners, new[] { 0, 4, 8, 12, 16, 20, 24 }, new[] { 0, 4, 6, 2, 1, 3, 7, 5, 0, 1, 5, 4, 2, 6, 7, 3, 0, 2, 3, 1, 4, 5, 7, 6 }))
            using (TrimRun run = TrimRun.Of(new[] { sticking }, mesh, geometryToOwner: g, shapeToOwner: s))
            {
                Assert.That(ConvexTrimSearch.IsRigid(in s), Is.True);
                Assert.That(run.Outcome.trims, Is.EqualTo(1));
                Assert.That(run.Outcome.overhangBefore, Is.EqualTo(0.3f).Within(1e-4f));
                float least = float.MaxValue;
                for (int v = 0; v < run.Convex(0).current.V; v++) least = math.min(least, math.transform(s, run.Convex(0).current.v[v]).x);
                Assert.That(least, Is.EqualTo(0f).Within(1e-4f), "cut at the mesh's plane, seen in the owner's frame");
                Assert.That(run.Convex(0).volumeAfter, Is.EqualTo(1.0).Within(1e-3));
            }

            float4x4 stretched = float4x4.TRS(float3.zero, quaternion.identity, new float3(1f, 2f, 1f));
            Assert.That(ConvexTrimSearch.IsRigid(in stretched), Is.False, "a frame that stretches is not one lengths are metres in");
        }

        [Test]
        public void AMeshIndexBeyondTheStorage_GivesNoPlanes()
        {
            var vertices = new[] { new VpRenderVertex { position = Vector3.zero }, new VpRenderVertex { position = Vector3.one } };
            uint[] indices = { 0, 1, 2 };
            float* support = stackalloc float[ConvexTrimSearch.Directions];
            float4x4 identity = float4x4.identity;
            fixed (VpRenderVertex* v = vertices)
            fixed (uint* ix = indices)
            {
                Assert.That(ConvexTrimSearch.MeshSupport(v, vertices.Length, ix, indices.Length, in identity, support), Is.False);
                Assert.That(ConvexTrimSearch.MeshSupport(v, vertices.Length, ix, 2, in identity, support), Is.True);
                Assert.That(support[25], Is.EqualTo(math.sqrt(3f)).Within(1e-6f), "the (1, 1, 1) diagonal reaches the second vertex");
            }
        }

        // ---- the walk city's tree_012, cut level at 6 m (the preliminary calculation's case) ------------------------

        internal const string TreePath = "Assets/Licensed/WalkCity/Inputs/tree_012.json";

        /// <summary>One side of a level cut of a placed input, by the numbers alone: its convexes (the cut's own clip) and the vertices of its mesh (the triangles clipped at the plane, the cap's vertices among them).</summary>
        internal static void LevelCut(PlacedCuttableInput data, float height, bool upper, List<TrimPoly> convexes, List<float3> mesh)
        {
            var plane = new float4(0f, 1f, 0f, -height);
            foreach (PlacedCuttableInput.Hull hull in data.hulls)
            {
                TrimPoly whole = TrimPoly.From(hull.vertices.Select(v => (float3)v).ToArray(), hull.faceOffsets, hull.faceIndices);
                TrimPoly kept = whole.Keep(plane, upper, out bool same);
                if (!same) whole.Dispose();
                if (kept != null) convexes.Add(kept);
            }

            float sign = upper ? 1f : -1f;
            for (int t = 0; t + 2 < data.indices.Length; t += 3)
            {
                for (int k = 0; k < 3; k++)
                {
                    float3 a = data.render[data.indices[t + k]].position, b = data.render[data.indices[t + ((k + 1) % 3)]].position;
                    float sa = sign * (a.y - height), sb = sign * (b.y - height);
                    if (sa >= 0f) mesh.Add(a);
                    if ((sa > 0f && sb < 0f) || (sa < 0f && sb > 0f)) mesh.Add(math.lerp(a, b, sa / (sa - sb)));
                }
            }
        }

        [Test]
        public void Tree012CutLevelAtSixMetres_TheUpperSide_IsTrimmedAsThePreliminaryCalculationHasIt()
        {
            if (!File.Exists(TreePath)) Assert.Ignore("the walk city's inputs are not in this checkout: " + TreePath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(TreePath));
            var convexes = new List<TrimPoly>();
            var mesh = new List<float3>();
            try
            {
                LevelCut(data, 6f, true, convexes, mesh);
                Assert.That(convexes.Count, Is.EqualTo(4), "the convexes that reach above 6 m");
                using (TrimRun run = TrimRun.Of(convexes, mesh))
                {
                    string perConvex = string.Join(", ", Enumerable.Range(0, convexes.Count).Select(c => run.Convex(c).verticesBefore + "->" + run.Convex(c).verticesAfter + " (" + run.Convex(c).trims + ")"));
                    string report = "trims " + run.Outcome.trims + ", vertices " + run.Outcome.verticesBefore + " -> " + run.Outcome.verticesAfter
                        + ", overhang " + run.Outcome.overhangBefore.ToString("F4") + " -> " + run.Outcome.overhangAfter.ToString("F4") + "; per convex: " + perConvex;
                    TestContext.WriteLine("tree_012 upper of y = 6: " + report);
                    Assert.That(run.Outcome.verticesBefore, Is.EqualTo(159), report);
                    Assert.That(run.Outcome.overhangBefore, Is.EqualTo(0.413f).Within(0.0005f), report);
                    Assert.That(run.Outcome.verticesAfter, Is.EqualTo(168), report);
                    Assert.That(run.Outcome.trims, Is.EqualTo(7), report);
                    Assert.That(run.Outcome.overhangAfter, Is.LessThan(0.1f), report);
                    Assert.That(run.Outcome.overhangAfter, Is.EqualTo(0.086f).Within(0.0005f), report);
                    Assert.That(run.Outcome.LeftInAll, Is.EqualTo(0), report);
                    Assert.That(perConvex, Is.EqualTo("40->37 (1), 54->54 (0), 26->30 (2), 39->47 (4)"), report);

                    // What is cut off is beyond the mesh: every mesh vertex is still inside or on every trimmed convex's planes
                    // that were adopted (the support planes), and no trimmed convex reaches beyond one by more than rounding.
                    for (int c = 0; c < convexes.Count; c++)
                    {
                        if (run.Convex(c).trims == 0) continue;
                        Assert.That(run.Convex(c).volumeAfter, Is.LessThan(run.Convex(c).volumeBefore), "convex " + c);
                        Assert.That(run.Convex(c).volumeAfter, Is.GreaterThan(0.9 * run.Convex(c).volumeBefore), "convex " + c + ": what is cut off is a small part");
                    }
                }
            }
            finally
            {
                foreach (TrimPoly p in convexes) p.Dispose();
            }
        }

        [Test]
        public void Tree012CutLevelAtSixMetres_TheLowerSide_IsTrimmedToo()
        {
            if (!File.Exists(TreePath)) Assert.Ignore("the walk city's inputs are not in this checkout: " + TreePath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(TreePath));
            var convexes = new List<TrimPoly>();
            var mesh = new List<float3>();
            try
            {
                LevelCut(data, 6f, false, convexes, mesh);
                Assert.That(convexes.Count, Is.EqualTo(8));
                using (TrimRun run = TrimRun.Of(convexes, mesh))
                {
                    string report = "trims " + run.Outcome.trims + ", vertices " + run.Outcome.verticesBefore + " -> " + run.Outcome.verticesAfter
                        + ", overhang " + run.Outcome.overhangBefore.ToString("F4") + " -> " + run.Outcome.overhangAfter.ToString("F4");
                    TestContext.WriteLine("tree_012 lower of y = 6: " + report);
                    Assert.That(run.Outcome.verticesBefore, Is.EqualTo(339), report);
                    Assert.That(run.Outcome.verticesAfter, Is.EqualTo(347), report);
                    Assert.That(run.Outcome.trims, Is.EqualTo(3), report);
                    Assert.That(run.Outcome.overhangBefore, Is.EqualTo(0.199f).Within(0.0005f), report);
                    Assert.That(run.Outcome.overhangAfter, Is.LessThan(0.1f), report);
                }
            }
            finally
            {
                foreach (TrimPoly p in convexes) p.Dispose();
            }
        }
    }
}
