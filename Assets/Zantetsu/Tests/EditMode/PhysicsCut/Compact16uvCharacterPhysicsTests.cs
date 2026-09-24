using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.ConvexCut.Tests;

namespace Zantetsu.PhysicsCut.Tests
{
    public class Compact16uvCharacterPhysicsTests
    {
        const string Root = "Assets/Licensed/Compact16uvIntake/";
        [TestCase("character-casual", 0)] [TestCase("character-casual", 1)]
        [TestCase("character-casual", 2)] [TestCase("character-casual", 3)]
        [TestCase("character-professional", 0)] [TestCase("character-professional", 1)]
        [TestCase("character-professional", 2)] [TestCase("character-professional", 3)]
        public void AuthoredProxies_BindposeBoneLocal_MatchesImportedHierarchy(string family, int pose)
        {
            string path = Root + "Resources/CharacterPhysicsMigration/" + family + ".json";
            if (!File.Exists(path)) Assert.Ignore("Private character physics fixture absent.");
            string expected = family == "character-casual" ? "73ba893fdf0be1001b7dd19cddb9453ce09f7d111e2c1cca9acaa0b9b3bc8695" : "e20f8f25caec423cac6edea771195aaee068e63416c7dc1007a7f1535d30991b";
            Assert.That(Hash(path), Is.EqualTo(expected));
            var fixture = JsonUtility.FromJson<Fixture>(File.ReadAllText(path));
            var entry = JsonUtility.FromJson<Input>(File.ReadAllText(Root + "intake.json")).assets.Single(e => e.family == family);
            Assert.That(fixture.sourceSha256, Is.EqualTo(Hash(entry.assetPath)));
            Assert.That(fixture.coordinateSystem, Is.EqualTo("RendererBindLocal_MirrorBlenderOwnerX"));
            Assert.That(fixture.schemaVersion, Is.EqualTo(1)); Assert.That(fixture.hulls.Length, Is.EqualTo(19));
            var parent = new GameObject("Character physics frame gate");
            try
            {
                var instance = UnityEngine.Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(entry.assetPath), parent.transform);
                foreach (var animator in instance.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
                var skin = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.sharedMesh != null && r.sharedMesh.name == entry.objectName);
                var sourcePositions = skin.sharedMesh.vertices;
                var authored = Vectors(fixture.authoredDisplayVertices);
                Assert.That(authored.Length, Is.EqualTo(entry.topologyCount));
                Assert.That(sourcePositions.Length, Is.EqualTo(entry.topologyMap.Length));
                TestContext.WriteLine("displayBasisMax=" + sourcePositions.Select((v,i) => Vector3.Distance(v,authored[entry.topologyMap[i]])).Max().ToString("R"));
                float importTolerance = 8 * 1.1920929e-7f * Mathf.Max(1, authored.Max(v => v.magnitude));
                for (int i = 0; i < sourcePositions.Length; i++)
                    Assert.That(Vector3.Distance(sourcePositions[i], authored[entry.topologyMap[i]]), Is.LessThanOrEqualTo(importTolerance), "Explicit owner-local basis; float import precision, not a fitted transform.");
                if (pose > 0)
                    for (int b = 0; b < skin.bones.Length; b++) skin.bones[b].localRotation *= Quaternion.Euler(0, (b % 3 - 1) * 4f, (b % 5 - 2) * 3f);
                if (pose >= 2)
                {
                    parent.transform.SetPositionAndRotation(new Vector3(3, -2, 5), Quaternion.Euler(17, 31, -12));
                    parent.transform.localScale = new Vector3(1.3f, .8f, 1.1f);
                }
                if (pose == 3) skin.rootBone.localScale = Vector3.Scale(skin.rootBone.localScale, new Vector3(1.05f, .95f, 1.1f));
                float maxWorldError = 0, maxLocalError = 0, wrongBoneError = 0;
                int accepted = 0, rejected = 0, cuts = 0;
                foreach (var hull in fixture.hulls)
                {
                    int boneIndex = Array.FindIndex(skin.bones, b => b.name == hull.boneName);
                    Assert.That(boneIndex, Is.GreaterThanOrEqualTo(0), hull.boneName);
                    Assert.That(skin.bones.Count(b => b.name == hull.boneName), Is.EqualTo(1));
                    var imported = instance.GetComponentsInChildren<MeshFilter>(true).Single(m => m.name == hull.name);
                    var meshLocal = Vectors(hull.importedMeshLocalVertices);
                    var bindLocal = Vectors(hull.rendererBindVertices);
                    float meshTolerance = 8 * 1.1920929e-7f * Mathf.Max(1, meshLocal.Max(v => v.magnitude));
                    var seen = new HashSet<int>();
                    var boneLocal = bindLocal.Select(v => skin.sharedMesh.bindposes[boneIndex].MultiplyPoint3x4(v)).ToArray();
                    var toRenderer = skin.transform.worldToLocalMatrix * skin.bones[boneIndex].localToWorldMatrix;
                    var posed = boneLocal.Select(v => toRenderer.MultiplyPoint3x4(v)).ToArray();
                    float hullWorldError = 0;
                    foreach (var vertex in imported.sharedMesh.vertices)
                    {
                        // Test-only geometric correspondence for the independent imported-node oracle.
                        // No vertex/face in the exported physics input is welded, moved, or remapped.
                        var matches = Enumerable.Range(0, meshLocal.Length).Where(i => Vector3.Distance(meshLocal[i], vertex) <= meshTolerance).ToArray();
                        Assert.That(matches.Length, Is.EqualTo(1), hull.name + " imported raw mesh differs from explicit mirror-X basis: " + vertex);
                        int id = matches[0];
                        seen.Add(id);
                        Vector3 referenceWorld = imported.transform.TransformPoint(vertex);
                        Vector3 computedWorld = skin.transform.TransformPoint(posed[id]);
                        hullWorldError = Mathf.Max(hullWorldError, Vector3.Distance(referenceWorld, computedWorld));
                        maxLocalError = Mathf.Max(maxLocalError, Vector3.Distance(skin.transform.InverseTransformPoint(referenceWorld), posed[id]));
                        Vector3 wrongWorld = skin.bones[(boneIndex + 1) % skin.bones.Length].TransformPoint(boneLocal[id]);
                        wrongBoneError = Mathf.Max(wrongBoneError, Vector3.Distance(referenceWorld, wrongWorld));
                    }
                    Assert.That(seen.Count, Is.EqualTo(32));
                    maxWorldError = Mathf.Max(maxWorldError, hullWorldError);
                    Assert.That(hullWorldError, Is.LessThan(2e-5f), hull.name + " world frame mismatch");
                    var poly = Poly(bindLocal, hull);
                    bool convex = IsConvex(poly, out double outside, out double tolerance);
                    Assert.That(convex, Is.EqualTo(hull.convexAccepted), hull.name + " convex classification changed");
                    if (!convex)
                    {
                        rejected++;
                        TestContext.WriteLine($"REJECT {hull.name} outside={outside:R} tolerance={tolerance:R}; no cook/cut/owner registration");
                        continue;
                    }
                    accepted++;
                    poly = Poly(posed, hull);
                    Cook(poly);
                    foreach (int axis in new[] { 1, 0 })
                    {
                        float3 normal = default; normal[axis] = 1;
                        double min = poly.V.Min(v => v[axis]), max = poly.V.Max(v => v[axis]);
                        float d = (float)(-(min + (max - min) * .537));
                        using var harness = new OwnerCutHarness();
                        harness.Add(poly).Plane(normal, d, 1e-5f); harness.Build();
                        ulong before = harness.InputHash();
                        var result = harness.ExecuteViaJob();
                        Assert.That(result.status, Is.EqualTo(ConvexCutOwnerStatus.Ok), hull.name);
                        Assert.That(harness.Outcome(0).IsSplit, Is.True, hull.name);
                        Assert.That(harness.InputHash(), Is.EqualTo(before)); Assert.That(harness.CheckGuards(), Is.Empty);
                        Assert.That(result.positiveVolume + result.negativeVolume, Is.EqualTo(Volume(poly)).Within(Volume(poly) * 2e-5));
                        poly = harness.AdoptedSet(true).Single();
                        Cook(poly); Cook(harness.AdoptedSet(false).Single()); cuts++;
                    }
                }
                Assert.That(accepted, Is.EqualTo(family == "character-casual" ? 17 : 18));
                Assert.That(rejected, Is.EqualTo(family == "character-casual" ? 2 : 1));
                Assert.That(wrongBoneError, Is.GreaterThan(.01f), "Wrong bone negative control must be observable.");
                TestContext.WriteLine($"family={family} pose={pose} hulls=19 mappedVertices=608 maxWorldError={maxWorldError:R} maxRendererLocalError={maxLocalError:R} wrongBoneError={wrongBoneError:R} accepted={accepted} rejected={rejected} cuts={cuts}");
            }
            finally { UnityEngine.Object.DestroyImmediate(parent); }
        }
        static bool IsConvex(ConvexPoly p, out double outside, out double tolerance)
        {
            outside = 0;
            double extent = Enumerable.Range(0, 3).Max(a => p.V.Max(v => v[a]) - p.V.Min(v => v[a]));
            tolerance = Math.Max(1e-6, extent * 1e-6);
            foreach (var f in p.F)
            {
                double3 a = p.V[f[0]], normal = math.normalize(math.cross(p.V[f[1]] - a, p.V[f[2]] - a));
                outside = Math.Max(outside, p.V.Max(v => math.dot(normal, v - a)));
            }
            return outside <= tolerance;
        }
        static Vector3[] Vectors(double[] data) => Enumerable.Range(0, data.Length / 3).Select(i => new Vector3((float)data[i*3], (float)data[i*3+1], (float)data[i*3+2])).ToArray();
        static ConvexPoly Poly(Vector3[] positions, Hull hull) => new ConvexPoly(positions.Select(v => new double3(v.x,v.y,v.z)).ToArray(), Enumerable.Range(0,hull.faceOffsets.Length-1).Select(i => hull.faceIndices.Skip(hull.faceOffsets[i]).Take(hull.faceOffsets[i+1]-hull.faceOffsets[i]).ToArray()).ToArray());
        static double Volume(ConvexPoly p) => p.F.Sum(f => Enumerable.Range(1, f.Length-2).Sum(k => math.dot(p.V[f[0]], math.cross(p.V[f[k]], p.V[f[k+1]]))/6));
        static void Cook(ConvexPoly p)
        {
            var mesh = new Mesh();
            try { mesh.vertices = p.V.Select(v => new Vector3((float)v.x,(float)v.y,(float)v.z)).ToArray(); mesh.triangles = p.F.SelectMany(f => Enumerable.Range(1,f.Length-2).SelectMany(k => new[] { f[0], f[k+1], f[k] })).ToArray(); UnityEngine.Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking); }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
        static string Hash(string path) { using var stream = File.OpenRead(path); using var sha = SHA256.Create(); return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant(); }
        [Serializable] sealed class Input { public Entry[] assets; }
        [Serializable] sealed class Entry { public string family, assetPath, objectName; public int[] topologyMap; public int topologyCount; }
        [Serializable] sealed class Fixture { public int schemaVersion; public string sourceSha256, coordinateSystem; public double[] authoredDisplayVertices; public Hull[] hulls; }
        [Serializable] sealed class Hull { public string name, boneName; public bool convexAccepted; public double[] rendererBindVertices, importedMeshLocalVertices; public int[] faceOffsets, faceIndices; }
    }
}
