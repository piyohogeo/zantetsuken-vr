using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.Editor.Sandbox;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The reference decider of the additional split (TL 2026-10-21), on small fixtures: elements apart, intersecting,
    /// touching at a point, an edge and a face; a distance under, at and over 2m; a triangle wholly inside a convex;
    /// nested closed meshes that are not taken for volumes; one mesh of parts apart; a chain of intersections in one
    /// component; components that cannot give both sides a convex and a triangle; an input any boundary separates and
    /// the product's h = f(u) surface cannot; a value not finite, an index out of range and a time limit told apart from
    /// "not separable"; and every placement returned re-checked by an independent double computation.
    /// </summary>
    public unsafe class SpatialSplitReferenceDeciderTests
    {
        private const float M = 1f / 4096f;   // the margin m; 2m = 2^-11, exact in binary

        internal static readonly int[][] BoxFaces =
        {
            new[] { 0, 4, 6, 2 }, new[] { 1, 3, 7, 5 }, new[] { 0, 1, 5, 4 }, new[] { 2, 6, 7, 3 }, new[] { 0, 2, 3, 1 }, new[] { 4, 5, 7, 6 },
        };

        // A box's B-rep as the capture saves a convex: corner i at (bit 0 ? hi.x : lo.x, bit 1 ? hi.y : lo.y, bit 2 ? hi.z : lo.z).
        internal static SpatialSplitSample.Convex BoxConvex(float3 lo, float3 hi)
        {
            var vertices = new float3[8];
            for (int i = 0; i < 8; i++) vertices[i] = new float3((i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z);
            var offsets = new List<int>();
            var faceIndices = new List<int>();
            var faceEdges = new List<int>();
            var edges = new List<BrepEdge>();
            var local = new Dictionary<(int, int), int>();
            for (int f = 0; f < 6; f++)
            {
                offsets.Add(4 * f);
                for (int k = 0; k < 4; k++)
                {
                    int a = BoxFaces[f][k], b = BoxFaces[f][(k + 1) % 4];
                    var key = (math.min(a, b), math.max(a, b));
                    if (!local.TryGetValue(key, out int e))
                    {
                        e = edges.Count;
                        local[key] = e;
                        edges.Add(new BrepEdge { v0 = key.Item1, v1 = key.Item2, f0 = -1, f1 = -1 });
                    }

                    BrepEdge edge = edges[e];
                    if (a < b) edge.f0 = f; else edge.f1 = f;
                    edges[e] = edge;
                    faceIndices.Add(a);
                    faceEdges.Add(e);
                }
            }

            offsets.Add(24);
            return new SpatialSplitSample.Convex
            {
                range = new ConvexBrepRange { vertexCount = 8, faceCount = 6, faceIndexCount = 24, edgeCount = 12, maxFaceLoop = 4 },
                vertices = vertices, faceOffsets = offsets.ToArray(), faceIndices = faceIndices.ToArray(), faceEdges = faceEdges.ToArray(), edges = edges.ToArray(),
            };
        }

        /// <summary>A sample as the capture saves one: boxes as the convexes, the positions and index triples as the geometry.</summary>
        internal static SpatialSplitSample Fixture((float3 lo, float3 hi)[] boxes, float3[] positions, int[] indices, float margin = M,
            float4x4? geometryToOwner = null, float4x4? shapeToOwner = null, string name = "fixture")
        {
            int size = UnsafeUtility.SizeOf<VpRenderVertex>();
            var bytes = new byte[positions.Length * size];
            fixed (byte* p = bytes)
            {
                for (int i = 0; i < positions.Length; i++) UnsafeUtility.WriteArrayElement(p, i, new VpRenderVertex { position = positions[i] });
            }

            var ids = new uint[positions.Length];
            for (int i = 0; i < ids.Length; i++) ids[i] = (uint)i;
            var indexArray = new uint[indices.Length];
            for (int i = 0; i < indices.Length; i++) indexArray[i] = (uint)indices[i];
            var convexes = new SpatialSplitSample.Convex[boxes.Length];
            int vertexBase = 0, faceBase = 0, faceIndexBase = 0, edgeBase = 0;
            for (int c = 0; c < boxes.Length; c++)
            {
                convexes[c] = BoxConvex(boxes[c].lo, boxes[c].hi);
                ConvexBrepRange r = convexes[c].range;
                r.vertexBase = vertexBase; r.faceBase = faceBase; r.faceIndexBase = faceIndexBase; r.edgeBase = edgeBase;
                convexes[c].range = r;
                vertexBase += 8; faceBase += 7; faceIndexBase += 24; edgeBase += 12;
            }

            return new SpatialSplitSample
            {
                formatVersion = SpatialSplitCaptureSink.FormatVersion, vertexStructBytes = size, codeVersion = "fixture", sessionId = "fixture", objectName = name, sha256 = "fixture",
                world = 1, attempt = 1, margin = margin, supportEpsilon = margin / 2f, anchorEpsilon = margin / 2f, vertexLimit = 128, minCutSideTriangles = 50,
                maxConvexVertices = 8, vertexViewLength = positions.Length, parentMass = 1.0, limits = new SpatialSplitSearchLimits(64, 8, 16, 131072),
                cutConvexes = false, bentSearch = true, bentLimits = SpatialSplitBentLimits.Default,
                geometryToOwner = geometryToOwner ?? float4x4.identity, shapeToOwner = shapeToOwner ?? float4x4.identity,
                submeshOffsets = new[] { 0 }, submeshCounts = new[] { indices.Length }, materials = new[] { 0 }, anchors = null,
                indices = indexArray, vertexIds = ids, vertexBytes = bytes, convexes = convexes,
            };
        }

        internal static (float3, float3) Box(float x0, float y0, float z0, float x1, float y1, float z1) => (new float3(x0, y0, z0), new float3(x1, y1, z1));

        internal static float3[] Tri(float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz) =>
            new[] { new float3(ax, ay, az), new float3(bx, by, bz), new float3(cx, cy, cz) };

        // Triangles given one by one: three vertices each, every vertex its own.
        internal static (float3[] positions, int[] indices) Mesh(params float3[][] triangles)
        {
            var positions = new List<float3>();
            var indices = new List<int>();
            foreach (float3[] t in triangles)
            {
                foreach (float3 v in t)
                {
                    indices.Add(positions.Count);
                    positions.Add(v);
                }
            }

            return (positions.ToArray(), indices.ToArray());
        }

        internal static float3[][] ClosedBoxMesh(float3 lo, float3 hi)
        {
            var corners = new float3[8];
            for (int i = 0; i < 8; i++) corners[i] = new float3((i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z);
            var triangles = new List<float3[]>();
            foreach (int[] f in BoxFaces)
            {
                triangles.Add(new[] { corners[f[0]], corners[f[1]], corners[f[2]] });
                triangles.Add(new[] { corners[f[0]], corners[f[2]], corners[f[3]] });
            }

            return triangles.ToArray();
        }

        private static ReferenceDecision Decide(SpatialSplitSample sample, double seconds = 60.0)
        {
            ReferenceDecision d = SpatialSplitReferenceDecider.Decide(sample, seconds);
            TestContext.Out.WriteLine(sample.objectName + ": " + d.verdict + " (" + d.reason + "); components " + d.componentCount + " (full " + d.fullComponents
                + ", convex-only " + d.convexOnlyComponents + ", triangle-only " + d.triangleOnlyComponents + "); pairs far " + d.pairsFarByBoxes + ", near " + d.pairsNearByVertices
                + ", planes " + d.pairsFarByAxes + ", inside " + d.pairsNearByInside + ", exact " + d.pairsExact + " (features double " + d.featuresDouble + ", exact " + d.featuresExact
                + "), joined " + d.pairsConnected + "; scale " + d.scale + "; " + (d.seconds * 1000.0).ToString("F2") + " ms");
            if (d.verdict == ReferenceVerdict.Separable) Recheck(sample, d);
            return d;
        }

        internal static readonly (float3, float3) FarBox = Box(20f, 0f, 0f, 21f, 1f, 1f);
        internal static readonly float3[] FarTri = Tri(20.25f, 0.25f, 0.25f, 20.75f, 0.25f, 0.25f, 20.5f, 0.75f, 0.5f);   // inside FarBox

        // The box [0,1]^3 with the triangle given (and another box where one is given), and a far box with its own
        // triangle: whether the triangle (or the other box) joins the box at the origin.
        private static ReferenceDecision WithFar(string name, float3[] triangle, (float3, float3)? otherBox = null)
        {
            (float3[] positions, int[] indices) = Mesh(triangle, FarTri);
            var boxes = new List<(float3, float3)> { Box(0f, 0f, 0f, 1f, 1f, 1f) };
            if (otherBox != null) boxes.Add(otherBox.Value);
            boxes.Add(FarBox);
            return Decide(Fixture(boxes.ToArray(), positions, indices, name: name));
        }

        [Test]
        public void ElementsApart_AreSeparable_EachComponentItsOwn_AndThePlacementIsByOrder()
        {
            (float3[] positions, int[] indices) = Mesh(Tri(1.5f, 0f, 0f, 2f, 0f, 0f, 1.5f, 1f, 0f), FarTri);
            ReferenceDecision d = Decide(Fixture(new[] { Box(0f, 0f, 0f, 1f, 1f, 1f), FarBox }, positions, indices, name: "apart"));
            Assert.That(d.verdict, Is.EqualTo(ReferenceVerdict.Separable));
            Assert.That(d.componentCount, Is.EqualTo(3), "the first triangle is half a metre from its box: three components (the far pair is one)");
            Assert.That(d.fullComponents == 1 && d.convexOnlyComponents == 1 && d.triangleOnlyComponents == 1, Is.True);
            Assert.That(d.convexPositive, Is.EqualTo(new[] { false, true }), "the full component (the far pair) is positive, the rest negative");
            Assert.That(d.trianglePositive, Is.EqualTo(new[] { false, true }));
            Assert.That(d.pairsExact, Is.Zero, "no pair needed the exact judgement");
            Assert.That(d.pairsNearByInside, Is.EqualTo(1), "the far triangle inside its box: joined by a vertex clearly inside");
            Assert.That(d.filters, Is.True);
        }

        [Test]
        public void ATrianglePiercingABox_IsJoinedToIt_ThoughNoVertexIsNear()
        {
            ReferenceDecision d = WithFar("piercing", Tri(0.5f, 0.5f, -2f, 0.5f, 0.5f, 3f, 0.6f, 0.5f, 3f));
            Assert.That(d.verdict, Is.EqualTo(ReferenceVerdict.Separable));
            Assert.That(d.componentCount, Is.EqualTo(2), "the piercing triangle and the box are one component");
            Assert.That(d.pairsExact, Is.GreaterThanOrEqualTo(1), "judged exactly: no vertex of the triangle is near the box");
            Assert.That(d.convexPositive[0], Is.EqualTo(d.trianglePositive[0]));
        }

        [Test]
        public void Touching_AtAPoint_AnEdge_AndAFace_Joins_AndAHairApart_DoesNot()
        {
            Assert.That(WithFar("corner", Tri(1f, 1f, 1f, 2f, 2f, 2f, 2f, 2f, 3f)).componentCount, Is.EqualTo(2), "a vertex on the box's corner");
            Assert.That(WithFar("edge", Tri(1f, 1f, 0.25f, 1f, 1f, 0.75f, 2f, 2f, 0.5f)).componentCount, Is.EqualTo(2), "an edge along the box's edge");
            Assert.That(WithFar("face", Tri(1f, 0.2f, 0.2f, 1f, 0.8f, 0.2f, 1f, 0.5f, 0.8f)).componentCount, Is.EqualTo(2), "a triangle in the box's face");
            Assert.That(WithFar("box face", FarTri, Box(1f, 0f, 0f, 2f, 1f, 1f)).componentCount, Is.EqualTo(2), "two boxes sharing a face are one component (both triangles are in the far box)");
            Assert.That(WithFar("apart", Tri(1f + (3f * M), 0.2f, 0.2f, 1f + (3f * M), 0.8f, 0.2f, 1f + (3f * M), 0.5f, 0.8f)).componentCount, Is.EqualTo(3), "3m away: apart");
        }

        [Test]
        public void ADistance_Under2m_Joins_At2m_DoesNot_Over2m_DoesNot()
        {
            const float hair = 1f / (1 << 20);
            float3[] At(float x) => Tri(x, 0.2f, 0.2f, x, 0.8f, 0.2f, x, 0.5f, 0.8f);
            ReferenceDecision under = WithFar("under 2m", At(1f + (2f * M) - hair));
            Assert.That(under.componentCount, Is.EqualTo(2), "nearer than 2m: joined");
            ReferenceDecision at = WithFar("at 2m", At(1f + (2f * M)));
            Assert.That(at.componentCount, Is.EqualTo(3), "exactly 2m: the margin fits on both sides, not joined");
            Assert.That(at.pairsExact, Is.GreaterThanOrEqualTo(1), "a distance of exactly 2m is judged exactly");
            Assert.That(at.featuresExact, Is.GreaterThanOrEqualTo(1));
            Assert.That(WithFar("over 2m", At(1f + (2f * M) + hair)).componentCount, Is.EqualTo(3));

            // Two boxes the same way (both triangles are in the far box: that pair is one component).
            Assert.That(WithFar("boxes under", FarTri, Box(1f + (2f * M) - hair, 0f, 0f, 2f, 1f, 1f)).componentCount, Is.EqualTo(2), "nearer than 2m: joined");
            Assert.That(WithFar("boxes at", FarTri, Box(1f + (2f * M), 0f, 0f, 2f, 1f, 1f)).componentCount, Is.EqualTo(3), "exactly 2m: apart");
        }

        [Test]
        public void ATriangleWhollyInsideABox_IsJoined_ThoughItMeetsNoFace()
        {
            ReferenceDecision d = WithFar("inside", Tri(0.2f, 0.2f, 0.2f, 0.8f, 0.2f, 0.2f, 0.5f, 0.8f, 0.5f));
            Assert.That(d.componentCount, Is.EqualTo(2));
            Assert.That(d.pairsNearByInside, Is.GreaterThanOrEqualTo(1), "a vertex clearly inside the box");

            // A triangle lying in the box's face z = 0: no vertex clearly inside, no plane apart, so it is judged exactly.
            ReferenceDecision onFace = WithFar("in the face", Tri(0.2f, 0.2f, 0f, 0.8f, 0.2f, 0f, 0.5f, 0.8f, 0f));
            Assert.That(onFace.componentCount, Is.EqualTo(2));
            Assert.That(onFace.pairsExact, Is.GreaterThanOrEqualTo(1), "every vertex on the face z = 0: not clearly inside, judged exactly");
        }

        [Test]
        public void NestedClosedMeshes_AreNotVolumes_TheInnerOneGoesWithWhatItTouches()
        {
            // An inner closed box mesh inside an outer one, apart from it; a convex touching the inner mesh's face from
            // inside; a far box with a triangle. The inner mesh is not inside "the outer volume": it goes with its convex.
            var triangles = new List<float3[]>(ClosedBoxMesh(new float3(0.4f), new float3(0.6f)));
            triangles.AddRange(ClosedBoxMesh(new float3(0f), new float3(1f)));
            triangles.Add(FarTri);
            (float3[] positions, int[] indices) = Mesh(triangles.ToArray());
            ReferenceDecision d = Decide(Fixture(new[] { Box(0.4f, 0.45f, 0.45f, 0.6f, 0.55f, 0.55f), FarBox }, positions, indices, name: "nested"));
            Assert.That(d.verdict, Is.EqualTo(ReferenceVerdict.Separable));
            Assert.That(d.componentCount, Is.EqualTo(3), "inner mesh + its convex, the outer mesh alone, the far pair");
            Assert.That(d.fullComponents, Is.EqualTo(2));
            Assert.That(d.convexPositive[0], Is.True, "the inner convex's component is the first full one: positive");
            for (int t = 0; t < 12; t++) Assert.That(d.trianglePositive[t], Is.True, "inner triangle " + t + " with it");
            for (int t = 12; t < 24; t++) Assert.That(d.trianglePositive[t], Is.False, "outer triangle " + t + " on the other side");
        }

        [Test]
        public void OneMeshOfPartsApart_IsNotOneElement()
        {
            // One index list: a triangle in the box, and one far from everything.
            (float3[] positions, int[] indices) = Mesh(Tri(0.2f, 0.2f, 0.2f, 0.8f, 0.2f, 0.2f, 0.5f, 0.8f, 0.5f), Tri(5f, 5f, 5f, 6f, 5f, 5f, 5f, 6f, 5f), FarTri);
            ReferenceDecision d = Decide(Fixture(new[] { Box(0f, 0f, 0f, 1f, 1f, 1f), FarBox }, positions, indices, name: "parts"));
            Assert.That(d.componentCount, Is.EqualTo(3));
            Assert.That(d.triangleOnlyComponents, Is.EqualTo(1), "the lone triangle is a component of its own");
            Assert.That(d.verdict, Is.EqualTo(ReferenceVerdict.Separable));
        }

        [Test]
        public void AChainOfIntersections_IsOneComponent()
        {
            (float3[] positions, int[] indices) = Mesh(Tri(0.2f, 0.2f, 0.2f, 0.8f, 0.2f, 0.2f, 0.5f, 0.8f, 0.5f), FarTri);
            var boxes = new[] { Box(0f, 0f, 0f, 1f, 1f, 1f), Box(0.9f, 0f, 0f, 1.9f, 1f, 1f), Box(1.8f, 0f, 0f, 2.8f, 1f, 1f), FarBox };
            ReferenceDecision d = Decide(Fixture(boxes, positions, indices, name: "chain"));
            Assert.That(d.componentCount, Is.EqualTo(2), "A meets B, B meets C: one component, though A and C are apart");
            Assert.That(d.convexPositive[0] == d.convexPositive[1] && d.convexPositive[1] == d.convexPositive[2], Is.True);
            Assert.That(d.convexPositive[3], Is.Not.EqualTo(d.convexPositive[0]));
        }

        [Test]
        public void ComponentsThatCannotGiveBothSidesAConvexAndATriangle_AreNotSeparable()
        {
            float3[] inside = Tri(0.2f, 0.2f, 0.2f, 0.8f, 0.2f, 0.2f, 0.5f, 0.8f, 0.5f);
            float3[] lone = Tri(5f, 5f, 5f, 6f, 5f, 5f, 5f, 6f, 5f);
            (float3[] p1, int[] i1) = Mesh(inside, lone);
            ReferenceDecision a = Decide(Fixture(new[] { Box(0f, 0f, 0f, 1f, 1f, 1f) }, p1, i1, name: "one full + a lone triangle"));
            Assert.That(a.verdict, Is.EqualTo(ReferenceVerdict.NotSeparable));
            StringAssert.Contains("no two sides", a.reason);

            (float3[] p2, int[] i2) = Mesh(inside);
            ReferenceDecision b = Decide(Fixture(new[] { Box(0f, 0f, 0f, 1f, 1f, 1f), FarBox }, p2, i2, name: "one full + a lone convex"));
            Assert.That(b.verdict, Is.EqualTo(ReferenceVerdict.NotSeparable));

            (float3[] p3, int[] i3) = Mesh(lone);
            ReferenceDecision c = Decide(Fixture(new[] { Box(0f, 0f, 0f, 1f, 1f, 1f), FarBox }, p3, i3, name: "two lone convexes + one lone triangle"));
            Assert.That(c.verdict, Is.EqualTo(ReferenceVerdict.NotSeparable));
            Assert.That(c.componentCount, Is.EqualTo(3));

            (float3[] p4, int[] i4) = Mesh(lone, Tri(-5f, 5f, 5f, -6f, 5f, 5f, -5f, 6f, 5f));
            ReferenceDecision e = Decide(Fixture(new[] { Box(0f, 0f, 0f, 1f, 1f, 1f), FarBox }, p4, i4, name: "two lone convexes + two lone triangles"));
            Assert.That(e.verdict, Is.EqualTo(ReferenceVerdict.Separable), "a convex and a triangle each side, from the components apart");
            Assert.That(e.positiveConvexes == 1 && e.positiveTriangles == 1 && e.negativeConvexes == 1 && e.negativeTriangles == 1, Is.True);
        }

        [Test]
        public void ABoxInsideAHollowShell_IsSeparableByAnyBoundary_AndNotByTheProductsSurface()
        {
            // The centre box with a triangle inside, and round it a hollow cubic shell of six slabs that overlap at
            // their edges (one connected component), a triangle inside each slab. A closed boundary round the centre
            // separates the two; a plane cannot (the shell is one component and surrounds the centre), and a surface
            // h = f(u) carried along an axis cannot either: the two slabs along that axis stand where the centre does
            // in (u, h), so they fall on its side.
            var boxes = new List<(float3, float3)> { Box(-0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f) };
            var triangles = new List<float3[]> { Tri(-0.25f, -0.25f, 0f, 0.25f, -0.25f, 0f, 0f, 0.25f, 0f) };
            var slabs = new[]
            {
                Box(-2f, -2f, 1.5f, 2f, 2f, 2f), Box(-2f, -2f, -2f, 2f, 2f, -1.5f),      // top, bottom
                Box(1.5f, -2f, -2f, 2f, 2f, 2f), Box(-2f, -2f, -2f, -1.5f, 2f, 2f),      // +x, -x
                Box(-2f, 1.5f, -2f, 2f, 2f, 2f), Box(-2f, -2f, -2f, 2f, -1.5f, 2f),      // +y, -y
            };
            foreach ((float3 lo, float3 hi) in slabs)
            {
                boxes.Add((lo, hi));
                float3 c = (lo + hi) * 0.5f;
                triangles.Add(new[] { c + new float3(-0.125f, -0.125f, 0f), c + new float3(0.125f, -0.125f, 0f), c + new float3(0f, 0.125f, 0f) });
            }

            (float3[] positions, int[] indices) = Mesh(triangles.ToArray());
            SpatialSplitSample sample = Fixture(boxes.ToArray(), positions, indices, name: "shell");
            ReferenceDecision d = Decide(sample);
            Assert.That(d.verdict, Is.EqualTo(ReferenceVerdict.Separable));
            Assert.That(d.componentCount, Is.EqualTo(2), "the centre with its triangle; the shell with its six");
            Assert.That(d.convexPositive, Is.EqualTo(new[] { true, false, false, false, false, false, false }), "the centre alone on the positive side");

            // The product's own numbers on the same input: the plane search and the bent partition find nothing.
            SpatialSplitReplayResult product = SpatialSplitReplay.Run(sample);
            TestContext.Out.WriteLine("the product: " + product.outcome + ", bent tried " + product.search.bent + " frames " + product.search.bentFramesTried + ", error " + product.error);
            Assert.That(product.error, Is.Null);
            Assert.That(product.outcome, Is.EqualTo("NotFound"));
            Assert.That(product.search.bent && product.search.bentFramesTried > 0, Is.True, "the bent partition was looked for");
        }

        [Test]
        public void NotFinite_AnIndexOutOfRange_AndATimeLimit_AreNotNotSeparable()
        {
            (float3[] positions, int[] indices) = Mesh(Tri(0.2f, 0.2f, 0.2f, 0.8f, 0.2f, 0.2f, 0.5f, 0.8f, 0.5f), FarTri);
            var boxes = new[] { Box(0f, 0f, 0f, 1f, 1f, 1f), FarBox };

            var nan = (float3[])positions.Clone();
            nan[0] = new float3(float.NaN, 0f, 0f);
            ReferenceDecision a = Decide(Fixture(boxes, nan, indices, name: "NaN vertex"));
            Assert.That(a.verdict, Is.EqualTo(ReferenceVerdict.InvalidInput));
            StringAssert.Contains("not finite", a.reason);

            var bad = (int[])indices.Clone();
            bad[2] = 99;
            SpatialSplitSample sample = Fixture(boxes, positions, bad, name: "bad index");
            sample.vertexViewLength = 100;   // the storage was bigger: the index was valid there, the sample does not hold it
            ReferenceDecision b = Decide(sample);
            Assert.That(b.verdict, Is.EqualTo(ReferenceVerdict.InvalidInput));
            StringAssert.Contains("does not hold", b.reason);

            ReferenceDecision c = Decide(Fixture(boxes, positions, indices, margin: 0f, name: "no margin"));
            Assert.That(c.verdict, Is.EqualTo(ReferenceVerdict.InvalidInput));

            var flat = Fixture(new[] { Box(0f, 0f, 0f, 1f, 1f, 0f), FarBox }, positions, indices, name: "flat convex");
            ReferenceDecision f = Decide(flat);
            Assert.That(f.verdict, Is.EqualTo(ReferenceVerdict.InvalidInput));
            StringAssert.Contains("convex 0", f.reason);

            // A pair that must be judged exactly, with no time to judge it: undecided, not "not separable".
            (float3[] p2, int[] i2) = Mesh(Tri(1f + (2f * M), 0.2f, 0.2f, 1f + (2f * M), 0.8f, 0.2f, 1f + (2f * M), 0.5f, 0.8f), FarTri);
            ReferenceDecision t = SpatialSplitReferenceDecider.Decide(Fixture(boxes, p2, i2, name: "no time"), 1e-9);
            Assert.That(t.verdict, Is.EqualTo(ReferenceVerdict.Undecided));
            StringAssert.Contains("time limit", t.reason);
        }

        [Test]
        public void ADegenerateTriangle_IsASegmentOrAPoint_NeverDropped()
        {
            ReferenceDecision seg = WithFar("collinear on the face", Tri(1f, 0.5f, 0.5f, 1.5f, 0.5f, 0.5f, 2f, 0.5f, 0.5f));
            Assert.That(seg.degenerateTriangles, Is.EqualTo(1));
            Assert.That(seg.componentCount, Is.EqualTo(2), "its end lies on the box's face: joined");

            ReferenceDecision pt = WithFar("a point far away", Tri(5f, 5f, 5f, 5f, 5f, 5f, 5f, 5f, 5f));
            Assert.That(pt.degenerateTriangles, Is.EqualTo(1));
            Assert.That(pt.componentCount, Is.EqualTo(3), "a component of its own, counted");
            Assert.That(pt.triangleOnlyComponents, Is.EqualTo(1));

            ReferenceDecision ptOn = WithFar("a point on the corner", Tri(1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f));
            Assert.That(ptOn.componentCount, Is.EqualTo(2));
        }

        [Test]
        public void TrianglesSharingAnIndex_OrAPosition_UnderAnotherIndex_AreJoined()
        {
            // Two triangles sharing an edge by index, and two others meeting at a position each names by its own vertex.
            var positions = new[]
            {
                new float3(3f, 0f, 0f), new float3(4f, 0f, 0f), new float3(3f, 1f, 0f), new float3(4f, 1f, 0f),       // shared edge 1-2
                new float3(6f, 0f, 0f), new float3(7f, 0f, 0f), new float3(6f, 1f, 0f), new float3(6f, 1f, 0f), new float3(7f, 1f, 0f), new float3(6f, 2f, 0f),   // 6 and 7 same position
                new float3(20.25f, 0.25f, 0.25f), new float3(20.75f, 0.25f, 0.25f), new float3(20.5f, 0.75f, 0.5f),
            };
            var indices = new[] { 0, 1, 2, 1, 3, 2, 4, 5, 6, 7, 8, 9, 10, 11, 12 };
            ReferenceDecision d = Decide(Fixture(new[] { Box(0f, 0f, 0f, 1f, 1f, 1f), FarBox }, positions, indices, name: "shared"));
            Assert.That(d.componentCount, Is.EqualTo(4), "the box, the shared-edge pair, the same-position pair, the far pair");
            Assert.That(d.trianglePositive[0], Is.EqualTo(d.trianglePositive[1]));
            Assert.That(d.trianglePositive[2], Is.EqualTo(d.trianglePositive[3]));
        }

        [Test]
        public void ATinyTriangleFarFromTheOrigin_TouchedAtAVertexByAPoint_IsJoined_TheTLsCounterexample()
        {
            // TL 2026-10-22: a uniform scale of float(1e-8) and a translation of (1, 1, 1); the triangle (-3, 3, -2),
            // (2, -2, -3), (1, -1, -3) in the geometry's frame, and a point element at its third vertex; margin float(1e-10).
            // They touch, so they are joined. A face normal summed in double from the owner coordinates (Newell over
            // positions near (1, 1, 1)) cancels to garbage here -- (0, 0, -1) against the true (1, 1, 0) direction --
            // and a plane test on it would put the point 1e-8 away: a certificate that is no certificate.
            float s = 1e-8f;
            float4x4 geometry = new float4x4(new float4(s, 0f, 0f, 0f), new float4(0f, s, 0f, 0f), new float4(0f, 0f, s, 0f), new float4(1f, 1f, 1f, 1f));
            var positions = new[] { new float3(-3f, 3f, -2f), new float3(2f, -2f, -3f), new float3(1f, -1f, -3f), new float3(1f, -1f, -3f), new float3(1f, -1f, -3f), new float3(1f, -1f, -3f) };
            var indices = new[] { 0, 1, 2, 3, 4, 5 };
            SpatialSplitSample sample = Fixture(new (float3, float3)[0], positions, indices, margin: 1e-10f, geometryToOwner: geometry, name: "tiny far triangle and a point");
            ReferenceDecision d = SpatialSplitReferenceDecider.Decide(sample, 60.0);
            TestContext.Out.WriteLine(sample.objectName + ": " + d.verdict + " (" + d.reason + "); components " + d.componentCount + "; pairs far by axes " + d.pairsFarByAxes
                + ", exact " + d.pairsExact + ", joined " + d.pairsConnected + "; scale " + d.scale);
            Assert.That(d.verdict, Is.Not.EqualTo(ReferenceVerdict.InvalidInput), d.reason);
            Assert.That(d.pairsConnected, Is.EqualTo(1), "the point touches the triangle at its vertex: joined");
            Assert.That(d.componentCount, Is.EqualTo(1));
        }

        [Test]
        public void TheTransforms_CarryTheElementsToTheOwnersFrame_Exactly()
        {
            // The geometry frame stands 10 m along x from the owner's and turned a quarter about z; the shape frame is
            // turned too. In the owner's frame the triangle lies in the box's face.
            float4x4 geometry = new float4x4(new float4(0f, 1f, 0f, 0f), new float4(-1f, 0f, 0f, 0f), new float4(0f, 0f, 1f, 0f), new float4(10f, 0f, 0f, 1f));
            float4x4 shape = new float4x4(new float4(0f, -1f, 0f, 0f), new float4(1f, 0f, 0f, 0f), new float4(0f, 0f, 1f, 0f), new float4(0f, 0f, 0f, 1f));
            // A geometry point g is the owner point (10 - g.y, g.x, g.z); a shape point s is the owner point (s.y, -s.x, s.z).
            // The box [0,1]^3 of the shape frame is [0,1]x[-1,0]x[0,1] in the owner's, the far box [0,1]x[-21,-20]x[0,1].
            float3 G(float ox, float oy, float oz) => new float3(oy, 10f - ox, oz);   // owner -> geometry
            float3[] onFace = { G(0.2f, 0f, 0.2f), G(0.8f, 0f, 0.2f), G(0.5f, 0f, 0.8f) };   // in the face y = 0 of the first box
            float3[] far = { G(0.25f, -20.75f, 0.25f), G(0.75f, -20.75f, 0.25f), G(0.5f, -20.25f, 0.5f) };   // inside the far box
            (float3[] positions, int[] indices) = Mesh(onFace, far);
            ReferenceDecision d = Decide(Fixture(new[] { Box(0f, 0f, 0f, 1f, 1f, 1f), Box(20f, 0f, 0f, 21f, 1f, 1f) }, positions, indices, geometryToOwner: geometry, shapeToOwner: shape, name: "transformed"));
            Assert.That(d.verdict, Is.EqualTo(ReferenceVerdict.Separable));
            Assert.That(d.componentCount, Is.EqualTo(2), "each triangle with its box, in the owner's frame");
            Assert.That(d.convexPositive[0] == d.trianglePositive[0] && d.convexPositive[1] == d.trianglePositive[1], Is.True);
        }

        // The fixtures of the cross-check: the filtered decision against the exact-only one (no double at all).
        internal static IEnumerable<SpatialSplitSample> CrossCheckFixtures()
        {
            const float hair = 1f / (1 << 20);
            float3[] At(float x) => Tri(x, 0.2f, 0.2f, x, 0.8f, 0.2f, x, 0.5f, 0.8f);
            foreach ((string name, float3[] tri) in new[]
            {
                ("apart", Tri(1.5f, 0f, 0f, 2f, 0f, 0f, 1.5f, 1f, 0f)), ("piercing", Tri(0.5f, 0.5f, -2f, 0.5f, 0.5f, 3f, 0.6f, 0.5f, 3f)),
                ("corner", Tri(1f, 1f, 1f, 2f, 2f, 2f, 2f, 2f, 3f)), ("in the face", Tri(0.2f, 0.2f, 0f, 0.8f, 0.2f, 0f, 0.5f, 0.8f, 0f)),
                ("under 2m", At(1f + (2f * M) - hair)), ("at 2m", At(1f + (2f * M))), ("over 2m", At(1f + (2f * M) + hair)),
                ("sliver at 2m", Tri(1f + (2f * M), 0.5f, 0.1f, 1f + (2f * M), 0.5f + (1f / 1024f), 0.9f, 1f + (2f * M), 0.5f, 0.9f)),
                ("sliver under 2m", Tri(1f + (2f * M) - hair, 0.5f, 0.1f, 1f + (2f * M) - hair, 0.5f + (1f / 1024f), 0.9f, 1f + (2f * M) - hair, 0.5f, 0.9f)),
                ("collinear", Tri(1f, 0.5f, 0.5f, 1.5f, 0.5f, 0.5f, 2f, 0.5f, 0.5f)),
            })
            {
                (float3[] positions, int[] indices) = Mesh(tri, FarTri);
                yield return Fixture(new[] { Box(0f, 0f, 0f, 1f, 1f, 1f), FarBox }, positions, indices, name: name);
            }

            // Boxes under and at 2m; the nested meshes; the shell.
            (float3[] p1, int[] i1) = Mesh(FarTri, FarTri);
            yield return Fixture(new[] { Box(0f, 0f, 0f, 1f, 1f, 1f), Box(1f + (2f * M) - hair, 0f, 0f, 2f, 1f, 1f), FarBox }, p1, i1, name: "boxes under");
            yield return Fixture(new[] { Box(0f, 0f, 0f, 1f, 1f, 1f), Box(1f + (2f * M), 0f, 0f, 2f, 1f, 1f), FarBox }, p1, i1, name: "boxes at");
            var nested = new List<float3[]>(ClosedBoxMesh(new float3(0.4f), new float3(0.6f)));
            nested.AddRange(ClosedBoxMesh(new float3(0f), new float3(1f)));
            nested.Add(FarTri);
            (float3[] p2, int[] i2) = Mesh(nested.ToArray());
            yield return Fixture(new[] { Box(0.4f, 0.45f, 0.45f, 0.6f, 0.55f, 0.55f), FarBox }, p2, i2, name: "nested");
            var boxes = new List<(float3, float3)> { Box(-0.5f, -0.5f, -0.5f, 0.5f, 0.5f, 0.5f) };
            var triangles = new List<float3[]> { Tri(-0.25f, -0.25f, 0f, 0.25f, -0.25f, 0f, 0f, 0.25f, 0f) };
            foreach ((float3 lo, float3 hi) in new[]
            {
                Box(-2f, -2f, 1.5f, 2f, 2f, 2f), Box(-2f, -2f, -2f, 2f, 2f, -1.5f), Box(1.5f, -2f, -2f, 2f, 2f, 2f),
                Box(-2f, -2f, -2f, -1.5f, 2f, 2f), Box(-2f, 1.5f, -2f, 2f, 2f, 2f), Box(-2f, -2f, -2f, 2f, -1.5f, 2f),
            })
            {
                boxes.Add((lo, hi));
                float3 c = (lo + hi) * 0.5f;
                triangles.Add(new[] { c + new float3(-0.125f, -0.125f, 0f), c + new float3(0.125f, -0.125f, 0f), c + new float3(0f, 0.125f, 0f) });
            }

            (float3[] p3, int[] i3) = Mesh(triangles.ToArray());
            yield return Fixture(boxes.ToArray(), p3, i3, name: "shell");

            // The TL's tiny far triangle, and a scene at 2m moved 4096 m away.
            float s = 1e-8f;
            float4x4 geometry = new float4x4(new float4(s, 0f, 0f, 0f), new float4(0f, s, 0f, 0f), new float4(0f, 0f, s, 0f), new float4(1f, 1f, 1f, 1f));
            var tiny = new[] { new float3(-3f, 3f, -2f), new float3(2f, -2f, -3f), new float3(1f, -1f, -3f), new float3(1f, -1f, -3f), new float3(1f, -1f, -3f), new float3(1f, -1f, -3f) };
            yield return Fixture(new (float3, float3)[0], tiny, new[] { 0, 1, 2, 3, 4, 5 }, margin: 1e-10f, geometryToOwner: geometry, name: "tiny far triangle and a point");
            float4x4 moved = new float4x4(new float4(1f, 0f, 0f, 0f), new float4(0f, 1f, 0f, 0f), new float4(0f, 0f, 1f, 0f), new float4(4096f, -4096f, 4096f, 1f));
            (float3[] p4, int[] i4) = Mesh(At(1f + (2f * M)), FarTri);
            yield return Fixture(new[] { Box(0f, 0f, 0f, 1f, 1f, 1f), FarBox }, p4, i4, geometryToOwner: moved, shapeToOwner: moved, name: "at 2m, moved 4096 m");
        }

        [Test]
        public void TheFilteredDecision_AgreesWithTheExactOnlyOne_OnEveryFixture()
        {
            int fixtures = 0;
            foreach (SpatialSplitSample sample in CrossCheckFixtures())
            {
                fixtures++;
                ReferenceDecision filtered = SpatialSplitReferenceDecider.Decide(sample, 120.0);
                ReferenceDecision exact = SpatialSplitReferenceDecider.Decide(sample, 600.0, filters: false);
                TestContext.Out.WriteLine(sample.objectName + ": filtered " + filtered.verdict + " (" + filtered.componentCount + " components, exact pairs " + filtered.pairsExact
                    + ", features exact " + filtered.featuresExact + ", " + (filtered.seconds * 1000.0).ToString("F1") + " ms); exact-only " + exact.verdict + " (" + exact.componentCount
                    + " components, " + (exact.seconds * 1000.0).ToString("F1") + " ms)");
                Assert.That(exact.filters, Is.False);
                Assert.That(exact.pairsFarByBoxes + exact.pairsFarByAxes + exact.pairsNearByVertices + exact.pairsNearByInside + exact.featuresDouble, Is.Zero, sample.objectName + ": nothing by double");
                Assert.That(filtered.verdict, Is.EqualTo(exact.verdict), sample.objectName + ": the verdict");
                Assert.That(filtered.componentCount, Is.EqualTo(exact.componentCount), sample.objectName + ": the components");
                Assert.That(filtered.pairsConnected, Is.EqualTo(exact.pairsConnected), sample.objectName + ": the pairs joined");
                Assert.That(filtered.convexPositive, Is.EqualTo(exact.convexPositive), sample.objectName + ": the convexes' sides");
                Assert.That(filtered.trianglePositive, Is.EqualTo(exact.trianglePositive), sample.objectName + ": the triangles' sides");
            }

            Assert.That(fixtures, Is.GreaterThanOrEqualTo(15));
        }

        [Test]
        public void ASliverTriangle_AndALargeTranslation_KeepTheBoundary_At2m()
        {
            // A sliver (1/1024 wide) in the plane x = 1 + 2m: apart exactly; a hair nearer: joined. And the whole scene
            // moved 4096 m away: the same answers, the exact 2m boundary kept.
            Assert.That(WithFar("sliver at 2m", Tri(1f + (2f * M), 0.5f, 0.1f, 1f + (2f * M), 0.5f + (1f / 1024f), 0.9f, 1f + (2f * M), 0.5f, 0.9f)).componentCount, Is.EqualTo(3));
            const float hair = 1f / (1 << 20);
            Assert.That(WithFar("sliver under 2m", Tri(1f + (2f * M) - hair, 0.5f, 0.1f, 1f + (2f * M) - hair, 0.5f + (1f / 1024f), 0.9f, 1f + (2f * M) - hair, 0.5f, 0.9f)).componentCount, Is.EqualTo(2));
            float4x4 moved = new float4x4(new float4(1f, 0f, 0f, 0f), new float4(0f, 1f, 0f, 0f), new float4(0f, 0f, 1f, 0f), new float4(4096f, -4096f, 4096f, 1f));
            float3[] At(float x) => Tri(x, 0.2f, 0.2f, x, 0.8f, 0.2f, x, 0.5f, 0.8f);
            foreach ((string name, float x, int components) in new[] { ("moved at 2m", 1f + (2f * M), 3), ("moved under 2m", 1f + (2f * M) - hair, 2) })
            {
                (float3[] positions, int[] indices) = Mesh(At(x), FarTri);
                ReferenceDecision d = Decide(Fixture(new[] { Box(0f, 0f, 0f, 1f, 1f, 1f), FarBox }, positions, indices, geometryToOwner: moved, shapeToOwner: moved, name: name));
                Assert.That(d.componentCount, Is.EqualTo(components), name);
            }
        }

        // ---- the independent re-check of a placement --------------------------------------------------------------------------

        // Every element of one side at least 2m from every element of the other, and both sides with a convex and a
        // triangle: computed in double by formulas of their own (axis-aligned boxes and identity frames, which every
        // fixture re-checked here has).
        private static void Recheck(SpatialSplitSample sample, ReferenceDecision d)
        {
            Assert.That(d.positiveConvexes > 0 && d.positiveTriangles > 0 && d.negativeConvexes > 0 && d.negativeTriangles > 0, Is.True, "both sides with both kinds");
            if (!sample.geometryToOwner.Equals(float4x4.identity) || !sample.shapeToOwner.Equals(float4x4.identity)) return;
            int size = UnsafeUtility.SizeOf<VpRenderVertex>();
            var positions = new double3[sample.vertexIds.Length];
            fixed (byte* p = sample.vertexBytes)
            {
                for (int i = 0; i < positions.Length; i++) positions[i] = (float3)UnsafeUtility.ReadArrayElement<VpRenderVertex>(p, i).position;
            }

            int tc = sample.indices.Length / 3;
            var tris = new double3[tc][];
            for (int t = 0; t < tc; t++)
            {
                tris[t] = new double3[3];
                for (int k = 0; k < 3; k++) tris[t][k] = positions[Array.BinarySearch(sample.vertexIds, sample.indices[(3 * t) + k])];
            }

            var lo = new double3[sample.convexes.Length];
            var hi = new double3[sample.convexes.Length];
            for (int c = 0; c < lo.Length; c++)
            {
                lo[c] = new double3(double.PositiveInfinity);
                hi[c] = new double3(double.NegativeInfinity);
                foreach (float3 v in sample.convexes[c].vertices)
                {
                    lo[c] = math.min(lo[c], v);
                    hi[c] = math.max(hi[c], v);
                }
            }

            double least = double.PositiveInfinity;
            for (int a = 0; a < lo.Length; a++)
            {
                for (int b = a + 1; b < lo.Length; b++)
                {
                    if (d.convexPositive[a] != d.convexPositive[b]) least = Math.Min(least, BoxBox(lo[a], hi[a], lo[b], hi[b]));
                }

                for (int t = 0; t < tc; t++)
                {
                    if (d.convexPositive[a] != d.trianglePositive[t]) least = Math.Min(least, TriBox(tris[t], lo[a], hi[a]));
                }
            }

            for (int s = 0; s < tc; s++)
            {
                for (int t = s + 1; t < tc; t++)
                {
                    if (d.trianglePositive[s] != d.trianglePositive[t]) least = Math.Min(least, TriTri(tris[s], tris[t]));
                }
            }

            TestContext.Out.WriteLine("  re-check: least distance across the sides " + least + " (2m = " + (2.0 * sample.margin) + ")");
            Assert.That(least, Is.GreaterThanOrEqualTo((2.0 * sample.margin) - 1e-9), "every pair across the sides keeps 2m");
        }

        private static double BoxBox(double3 lo1, double3 hi1, double3 lo2, double3 hi2) => math.length(math.max(math.max(lo2 - hi1, lo1 - hi2), 0.0));

        private static double PointBox(double3 p, double3 lo, double3 hi) => math.length(math.max(math.max(lo - p, p - hi), 0.0));

        private static bool SegmentHitsBox(double3 a, double3 b, double3 lo, double3 hi)
        {
            double t0 = 0.0, t1 = 1.0;
            double3 e = b - a;
            for (int i = 0; i < 3; i++)
            {
                if (Math.Abs(e[i]) < 1e-300)
                {
                    if (a[i] < lo[i] || a[i] > hi[i]) return false;
                    continue;
                }

                double u = (lo[i] - a[i]) / e[i], v = (hi[i] - a[i]) / e[i];
                t0 = Math.Max(t0, Math.Min(u, v));
                t1 = Math.Min(t1, Math.Max(u, v));
                if (t0 > t1) return false;
            }

            return true;
        }

        private static double PointTri(double3 p, double3 a, double3 b, double3 c)
        {
            // Ericson, closest point on triangle.
            double3 ab = b - a, ac = c - a, ap = p - a;
            double d1 = math.dot(ab, ap), d2 = math.dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return math.distance(p, a);
            double3 bp = p - b;
            double d3 = math.dot(ab, bp), d4 = math.dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return math.distance(p, b);
            double vc = (d1 * d4) - (d3 * d2);
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return math.distance(p, a + (ab * (d1 / (d1 - d3))));
            double3 cp = p - c;
            double d5 = math.dot(ab, cp), d6 = math.dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return math.distance(p, c);
            double vb = (d5 * d2) - (d1 * d6);
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return math.distance(p, a + (ac * (d2 / (d2 - d6))));
            double va = (d3 * d6) - (d5 * d4);
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) return math.distance(p, b + ((c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)))));
            double denom = 1.0 / (va + vb + vc);
            return math.distance(p, a + (ab * (vb * denom)) + (ac * (vc * denom)));
        }

        private static double SegSeg(double3 p1, double3 q1, double3 p2, double3 q2)
        {
            double3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            double a = math.dot(d1, d1), e = math.dot(d2, d2), f = math.dot(d2, r);
            double s, t;
            if (a <= 1e-300 && e <= 1e-300) return math.distance(p1, p2);
            if (a <= 1e-300)
            {
                s = 0.0;
                t = math.clamp(f / e, 0.0, 1.0);
            }
            else
            {
                double c = math.dot(d1, r);
                if (e <= 1e-300)
                {
                    t = 0.0;
                    s = math.clamp(-c / a, 0.0, 1.0);
                }
                else
                {
                    double b = math.dot(d1, d2), denom = (a * e) - (b * b);
                    s = denom != 0.0 ? math.clamp(((b * f) - (c * e)) / denom, 0.0, 1.0) : 0.0;
                    t = ((b * s) + f) / e;
                    if (t < 0.0)
                    {
                        t = 0.0;
                        s = math.clamp(-c / a, 0.0, 1.0);
                    }
                    else if (t > 1.0)
                    {
                        t = 1.0;
                        s = math.clamp((b - c) / a, 0.0, 1.0);
                    }
                }
            }

            return math.distance(p1 + (d1 * s), p2 + (d2 * t));
        }

        private static bool SegmentHitsTri(double3 o, double3 end, double3 a, double3 b, double3 c)
        {
            // Moller-Trumbore, the segment as a ray of t in [0, 1].
            double3 dir = end - o, e1 = b - a, e2 = c - a, h = math.cross(dir, e2);
            double det = math.dot(e1, h);
            if (Math.Abs(det) < 1e-300) return false;
            double3 s = o - a;
            double u = math.dot(s, h) / det;
            if (u < 0 || u > 1) return false;
            double3 q = math.cross(s, e1);
            double v = math.dot(dir, q) / det;
            if (v < 0 || u + v > 1) return false;
            double t = math.dot(e2, q) / det;
            return t >= 0 && t <= 1;
        }

        private static double TriBox(double3[] t, double3 lo, double3 hi)
        {
            foreach (double3 v in t)
            {
                if (math.all(v >= lo) && math.all(v <= hi)) return 0.0;
            }

            for (int k = 0; k < 3; k++)
            {
                if (SegmentHitsBox(t[k], t[(k + 1) % 3], lo, hi)) return 0.0;
            }

            double least = double.PositiveInfinity;
            foreach (double3 v in t) least = Math.Min(least, PointBox(v, lo, hi));
            var corners = new double3[8];
            for (int i = 0; i < 8; i++) corners[i] = new double3((i & 1) != 0 ? hi.x : lo.x, (i & 2) != 0 ? hi.y : lo.y, (i & 4) != 0 ? hi.z : lo.z);
            foreach (double3 c in corners) least = Math.Min(least, PointTri(c, t[0], t[1], t[2]));
            foreach (int[] f in BoxFaces)
            {
                for (int k = 0; k < 4; k++)
                {
                    for (int j = 0; j < 3; j++) least = Math.Min(least, SegSeg(corners[f[k]], corners[f[(k + 1) % 4]], t[j], t[(j + 1) % 3]));
                }
            }

            return least;
        }

        private static double TriTri(double3[] s, double3[] t)
        {
            for (int k = 0; k < 3; k++)
            {
                if (SegmentHitsTri(s[k], s[(k + 1) % 3], t[0], t[1], t[2]) || SegmentHitsTri(t[k], t[(k + 1) % 3], s[0], s[1], s[2])) return 0.0;
            }

            double least = double.PositiveInfinity;
            foreach (double3 v in s) least = Math.Min(least, PointTri(v, t[0], t[1], t[2]));
            foreach (double3 v in t) least = Math.Min(least, PointTri(v, s[0], s[1], s[2]));
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++) least = Math.Min(least, SegSeg(s[i], s[(i + 1) % 3], t[j], t[(j + 1) % 3]));
            }

            return least;
        }
    }
}
