using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.ConvexCut.Tests;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>Licensed fixture gate, not an ordinary scene or performance measurement.</summary>
    public unsafe class Compact16uvAuthoredPhysicsTests
    {
        const string Root = "Assets/Licensed/Compact16uvIntake/";
        const string FixturePath = Root + "Resources/AuthoredPhysicsMigration/Megacity.json";
        const string PackedPath = Root + "Resources/Static16Migration/Megacity.bytes";
        const string SourcePath = Root + "static-megacity/ITHappy_Megacity_Buildings--bar_001.blend";
        const string FixtureHash = "21cfe6a62ecff7d36426f2b2212a5757764840ddab399601b154e6579c36b2a6";

        [Test]
        public void AuthoredConvex_AndStatic16_ThreeCommonPlanes_CutAndRecut()
        {
            if (!File.Exists(FixturePath)) Assert.Ignore("Private authored Megacity fixture is not installed.");
            Assert.That(Hash(FixturePath), Is.EqualTo(FixtureHash));
            var fixture = JsonUtility.FromJson<Fixture>(File.ReadAllText(FixturePath));
            Assert.That(fixture.schemaVersion, Is.EqualTo(1));
            Assert.That(fixture.coordinateSystem, Is.EqualTo("ImportedMeshLocal_MirrorBlenderOwnerX"));
            Assert.That(Hash(SourcePath), Is.EqualTo(fixture.sourceSha256));
            Assert.That(Hash(PackedPath), Is.EqualTo(fixture.static16Sha256));
            Assert.That(fixture.hulls.Length, Is.EqualTo(1));
            Assert.That(fixture.anchors.Length, Is.EqualTo(21));
            var hull = fixture.hulls[0];
            var poly = new ConvexPoly(
                Enumerable.Range(0, hull.vertices.Length / 3).Select(i => new double3(hull.vertices[3*i], hull.vertices[3*i+1], hull.vertices[3*i+2])).ToArray(),
                Enumerable.Range(0, hull.faceOffsets.Length-1).Select(i => hull.faceIndices.Skip(hull.faceOffsets[i]).Take(hull.faceOffsets[i+1]-hull.faceOffsets[i]).ToArray()).ToArray());
            Assert.That(poly.VertexCount, Is.EqualTo(24));
            Assert.That(poly.FaceCount, Is.EqualTo(44));
            Assert.That(poly.Edges.All(e => e.f0 >= 0 && e.f1 >= 0), Is.True);
            Cook(poly);
            using var storage = new VpCpuGeometryStorage(65536, 262144, 128, 256, 256, Allocator.Persistent);
            using var stream = File.OpenRead(PackedPath);
            Assert.That(VpStatic16File.TryAppendCuttable(stream, storage, out var geometry, out string failure), Is.True, failure);
            Assert.That(VpRenderVertex.Stride, Is.EqualTo(16));
            foreach (int axis in new[] { 2, 0, 1 })
            {
                Assert.That(storage.TryGetPublishedExtent(geometry, out _, out _, out var bounds), Is.True);
                float3 normal = default; normal[axis] = 1;
                float offset = -bounds.center[axis] - .137f*bounds.extents[axis];
                using var harness = new OwnerCutHarness();
                harness.Add(poly).Plane(normal, offset, 1e-5f);
                harness.Build();
                ulong before = harness.InputHash();
                var result = harness.ExecuteViaJob();
                Assert.That(result.status, Is.EqualTo(ConvexCutOwnerStatus.Ok), "physics axis " + axis);
                Assert.That(harness.InputHash(), Is.EqualTo(before));
                Assert.That(harness.CheckGuards(), Is.Empty);
                Assert.That(result.positiveVolume, Is.GreaterThan(0));
                Assert.That(result.negativeVolume, Is.GreaterThan(0));
                double volume = Volume(poly);
                Assert.That(result.positiveVolume + result.negativeVolume, Is.EqualTo(volume).Within(volume * 1e-5));
                Assert.That(harness.Outcome(0).IsSplit, Is.True);
                var positive = harness.AdoptedSet(true).Single();
                Cook(positive);
                Cook(harness.AdoptedSet(false).Single());
                Assert.That(VpStorageCutInput.TryAcquire(storage, geometry, out var input), Is.True);
                VpStorageCutResult cut;
                using (input) Assert.That(VpStorageCut.TryExecute(storage, input, new float4(normal, offset), default, out cut), Is.True);
                Assert.That(cut.status, Is.EqualTo(VpStorageCutStatus.Ok), "display axis " + axis);
                Assert.That(cut.kernel.executedManaged, Is.Zero);
                Assert.That(cut.kernel.openContourCount, Is.Zero);
                TestContext.WriteLine($"axis={axis} offset={offset:R} physicsVolumes={result.positiveVolume:R},{result.negativeVolume:R} displayCommittedVertices={storage.VertexCount}");
                poly = positive;
                geometry = cut.positive.geometry;
            }
        }

        static void Cook(ConvexPoly poly)
        {
            var mesh = new Mesh { name = "Private authored Megacity physics gate" };
            try
            {
                mesh.vertices = poly.V.Select(v => new Vector3((float)v.x, (float)v.y, (float)v.z)).ToArray();
                mesh.triangles = poly.F.SelectMany(f => Enumerable.Range(1, f.Length-2).SelectMany(k => new[] { f[0], f[k+1], f[k] })).ToArray();
                // BakeMesh exposes no cooked-hull geometry here; absence of an error
                // is not a claim that PhysX's cooked topology equals the B-rep.
                UnityEngine.Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
        static double Volume(ConvexPoly poly) => poly.F.Sum(f => Enumerable.Range(1, f.Length-2).Sum(k => math.dot(poly.V[f[0]], math.cross(poly.V[f[k]], poly.V[f[k+1]]))/6));
        static string Hash(string path) { using var s = File.OpenRead(path); using var h = SHA256.Create(); return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
        [Serializable] sealed class Fixture { public int schemaVersion; public string sourceSha256, static16Sha256, coordinateSystem; public Hull[] hulls; public Anchor[] anchors; }
        [Serializable] sealed class Hull { public string name; public float[] vertices; public int[] faceOffsets, faceIndices; }
        [Serializable] sealed class Anchor { public string name; public float[] position; }
    }
}
