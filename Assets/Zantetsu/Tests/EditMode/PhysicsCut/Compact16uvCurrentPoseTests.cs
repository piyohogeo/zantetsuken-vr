using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>Private input component gate. Not a character scene, physics rig, or performance test.</summary>
    public class Compact16uvCurrentPoseTests
    {
        const string Manifest = "Assets/Licensed/Compact16uvIntake/intake.json";

        [TestCase("character-casual", 0)]
        [TestCase("character-casual", 1)]
        [TestCase("character-casual", 2)]
        [TestCase("character-casual", 3)]
        [TestCase("character-professional", 0)]
        [TestCase("character-professional", 1)]
        [TestCase("character-professional", 2)]
        [TestCase("character-professional", 3)]
        public void CurrentPose_BakeTrue_RendererLocal_CompactStorage_CutAndRecut(string family, int pose)
        {
            if (!File.Exists(Manifest)) Assert.Ignore("Private character intake is not installed.");
            var entry = JsonUtility.FromJson<Input>(File.ReadAllText(Manifest)).assets.Single(e => e.family == family);
            Assert.That(FileHash(entry.assetPath), Is.EqualTo(entry.sourceSha256));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(entry.assetPath);
            Assert.That(prefab, Is.Not.Null);
            var source = prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.sharedMesh != null && r.sharedMesh.name == entry.objectName);
            Mesh mesh = source.sharedMesh;
            string path = ObjectPath(source.transform, prefab.transform);
            Assert.That(Hash(w => { w.Write(path); foreach (var p in mesh.vertices) { w.Write(p.x); w.Write(p.y); w.Write(p.z); } }), Is.EqualTo(entry.currentPositionHash));
            Assert.That(Hash(w => { w.Write(path); foreach (var n in mesh.normals) { w.Write(n.x); w.Write(n.y); w.Write(n.z); } }), Is.EqualTo(entry.currentNormalHash));
            Assert.That(Hash(w => { w.Write(path); foreach (var uv in mesh.uv) { w.Write(uv.x); w.Write(uv.y); } }), Is.EqualTo(entry.currentUvHash));
            Assert.That(Hash(w => { w.Write(path); w.Write(mesh.subMeshCount); for (int s = 0; s < mesh.subMeshCount; s++) { w.Write((int)mesh.GetTopology(s)); foreach (int i in mesh.GetIndices(s, true)) w.Write(i); } }), Is.EqualTo(entry.currentIndexTopologyHash));
            Assert.That(Hash(w => { w.Write(path); using var c = mesh.GetBonesPerVertex(); using var b = mesh.GetAllBoneWeights(); w.Write(c.Length); foreach (byte n in c) w.Write(n); w.Write(b.Length); foreach (var x in b) { w.Write(x.boneIndex); w.Write(x.weight); } foreach (var m in mesh.bindposes) for (int i = 0; i < 16; i++) w.Write(m[i]); }), Is.EqualTo(entry.currentWeightHash));
            Assert.That(mesh.blendShapeCount, Is.Zero, "This gate does not cover blendshapes.");
            Assert.That(entry.topologyMap.Length, Is.EqualTo(mesh.vertexCount));
            var parent = new GameObject("Current pose gate parent");
            var instance = UnityEngine.Object.Instantiate(prefab, parent.transform);
            var baked = new Mesh();
            var initial = new Mesh();
            var uncompensated = new Mesh();
            try
            {
                foreach (var animator in instance.GetComponentsInChildren<Animator>(true)) animator.enabled = false;
                var renderer = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true).Single(r => r.sharedMesh == mesh);
                renderer.BakeMesh(initial, true);
                if (pose > 0)
                    for (int i = 0; i < renderer.bones.Length; i++)
                        renderer.bones[i].localRotation *= Quaternion.Euler(0, (i % 3 - 1) * 4f, (i % 5 - 2) * 3f);
                if (pose >= 2)
                {
                    parent.transform.SetPositionAndRotation(new Vector3(3, -2, 5), Quaternion.Euler(17, 31, -12));
                    parent.transform.localScale = new Vector3(1.3f, .8f, 1.1f);
                }
                if (pose == 3)
                    renderer.rootBone.localScale = Vector3.Scale(renderer.rootBone.localScale, new Vector3(1.05f, .95f, 1.1f));
                renderer.BakeMesh(baked, true);
                var positions = baked.vertices;
                var oracle = SkinPositions(renderer);
                float maxError = positions.Zip(oracle, Vector3.Distance).Max();
                TestContext.WriteLine($"frame family={family} pose={pose} rendererScale={renderer.transform.lossyScale} rootBoneScale={renderer.rootBone.lossyScale} maxPositionError={maxError:R}");
                using (var counts = mesh.GetBonesPerVertex()) TestContext.WriteLine("maxInfluences=" + counts.ToArray().Max());
                var scale = renderer.transform.lossyScale;
                renderer.BakeMesh(uncompensated, false);
                float falseError = uncompensated.vertices.Zip(oracle, Vector3.Distance).Max();
                float scaledError = uncompensated.vertices.Zip(oracle, (a, b) => Vector3.Distance(a, Vector3.Scale(b, scale))).Max();
                TestContext.WriteLine($"scaleProbe bakeFalseLocalError={falseError:R} bakeFalseScaledOracleError={scaledError:R}");
                float extent = Mathf.Max(1f, baked.bounds.size.magnitude);
                Assert.That(scaledError, Is.LessThan(2e-5f * extent));
                if (pose >= 2 || family == "character-professional")
                    Assert.That(falseError, Is.GreaterThan(2e-5f * extent), "Negative control: false must expose the scale-contract mismatch.");
                Assert.That(maxError, Is.LessThan(2e-5f * extent), "Renderer-local all-weight matrix oracle; object scale must not be baked twice.");
                float worldError = positions.Zip(oracle, (a, b) => Vector3.Distance(renderer.transform.TransformPoint(a), renderer.transform.TransformPoint(b))).Max();
                TestContext.WriteLine($"worldPositionError={worldError:R}");
                float deformation = positions.Zip(initial.vertices, Vector3.Distance).Max();
                if (pose > 0) Assert.That(deformation, Is.GreaterThan(.001f), "The pose must actually change.");
                CollectionAssert.AreEqual(mesh.uv, baked.uv);
                for (int s = 0; s < mesh.subMeshCount; s++) CollectionAssert.AreEqual(mesh.GetIndices(s, true), baked.GetIndices(s, true));
                int indexCount = Enumerable.Range(0, mesh.subMeshCount).Sum(s => (int)mesh.GetIndexCount(s));
                using var packed = new NativeArray<VpRenderVertex>(mesh.vertexCount, Allocator.Temp);
                using var indices = new NativeArray<uint>(indexCount, Allocator.Temp);
                Assert.That(VpMeshConverter.TryConvert(baked, packed, indices, out int vc, out int ic), Is.True);
                Assert.That(vc, Is.EqualTo(mesh.vertexCount)); Assert.That(ic, Is.EqualTo(indexCount));
                Assert.That(VpRenderVertex.Stride, Is.EqualTo(16));
                var normals = baked.normals;
                var uvs = mesh.uv;
                float maxAngle = 0;
                for (int i = 0; i < vc; i++)
                {
                    Assert.That(packed[i].position, Is.EqualTo(positions[i]));
                    Assert.That(packed[i].uv0, Is.EqualTo(uvs[i]));
                    maxAngle = Mathf.Max(maxAngle, Vector3.Angle(normals[i], packed[i].normal));
                }
                Assert.That(maxAngle, Is.LessThan(1.1f));
                int offset = 0;
                var submeshes = Enumerable.Range(0, mesh.subMeshCount).Select(s => { int n = (int)mesh.GetIndexCount(s); var sub = new VpGeometrySubmesh(offset, n, s); offset += n; return sub; }).ToArray();
                using var storage = new VpCpuGeometryStorage(65536, 262144, 128, 256, 256, Allocator.Persistent);
                Assert.That(storage.TryAppendCuttable(packed.ToArray(), indices.ToArray(), entry.topologyMap, entry.topologyCount, submeshes, out var geometry, out var verdict), Is.True, verdict.ToString());
                foreach (int axis in new[] { 1, 0 })
                {
                    int previousCount = storage.VertexCount;
                    Assert.That(storage.TryGetPublishedExtent(geometry, out _, out _, out var bounds), Is.True);
                    float3 normal = default; normal[axis] = 1;
                    float d = -bounds.center[axis] - .137f * bounds.extents[axis];
                    Assert.That(VpStorageCutInput.TryAcquire(storage, geometry, out var input), Is.True);
                    VpStorageCutResult result;
                    using (input) Assert.That(VpStorageCut.TryExecute(storage, input, new float4(normal, d), default, out result), Is.True);
                    Assert.That(result.status, Is.EqualTo(VpStorageCutStatus.Ok));
                    Assert.That(result.kernel.executedManaged, Is.Zero);
                    Assert.That(result.kernel.openContourCount, Is.Zero);
                    Assert.That(storage.VertexCount, Is.GreaterThan(previousCount), "The plane must generate new cut vertices.");
                    geometry = result.positive.geometry;
                    TestContext.WriteLine($"cut axis={axis} offset={d:R} committedVertices={storage.VertexCount}");
                }
                for (int i = 0; i < vc; i++) Assert.That(storage.Vertices[i], Is.EqualTo(packed[i]), "Published source must stay immutable.");
                TestContext.WriteLine($"family={family} pose={pose} vertices={vc} indices={ic} maxPositionError={maxError:R} maxNormalAngle={maxAngle:R} deformation={deformation:R} payloadBytes={vc * 16} quality={renderer.quality} globalSkinWeights={QualitySettings.skinWeights}");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(initial);
                UnityEngine.Object.DestroyImmediate(uncompensated);
                UnityEngine.Object.DestroyImmediate(baked);
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }

        static Vector3[] SkinPositions(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer.sharedMesh;
            var matrices = renderer.bones.Select((b, i) => renderer.transform.worldToLocalMatrix * b.localToWorldMatrix * mesh.bindposes[i]).ToArray();
            using var counts = mesh.GetBonesPerVertex();
            using var weights = mesh.GetAllBoneWeights();
            var positions = mesh.vertices;
            int cursor = 0;
            for (int v = 0; v < positions.Length; v++)
            {
                Vector3 sum = default;
                for (int b = 0; b < counts[v]; b++) { var w = weights[cursor++]; sum += w.weight * matrices[w.boneIndex].MultiplyPoint3x4(positions[v]); }
                positions[v] = sum;
            }
            return positions;
        }
        static string ObjectPath(Transform t, Transform root) => t == root ? t.name : ObjectPath(t.parent, root) + "/" + t.name;
        static string FileHash(string path) { using var s = File.OpenRead(path); using var h = SHA256.Create(); return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant(); }
        static string Hash(Action<BinaryWriter> write) { using var s = new MemoryStream(); using (var w = new BinaryWriter(s, System.Text.Encoding.UTF8, true)) write(w); using var h = SHA256.Create(); return BitConverter.ToString(h.ComputeHash(s.ToArray())).Replace("-", "").ToLowerInvariant(); }
        [Serializable] sealed class Input { public Entry[] assets; }
        [Serializable] sealed class Entry { public string family, assetPath, sourceSha256, currentPositionHash, currentNormalHash, currentUvHash, currentIndexTopologyHash, currentWeightHash, objectName; public int topologyCount; public int[] topologyMap; }
    }
}
