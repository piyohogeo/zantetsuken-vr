using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// A skinned mesh's bone list shortened to the bones its weights use (TL, 2026-10-05; DESIGN 9, D-195): which bones are
    /// used, that the copy is the source in everything but the naming of its bones, that it deforms as the source
    /// does -- by Unity's bake and by the direct skin input the cut reads -- and that the model's complete bone
    /// correspondence still resolves a bone the renderer no longer lists.
    /// </summary>
    public class VpSkinBoneCompactionTests
    {
        readonly List<Object> owned = new List<Object>();
        static readonly int[] Topology = { 0, 1, 2, 3 };
        static readonly int[] Triangles = { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };

        T Own<T>(T value) where T : Object { owned.Add(value); return value; }

        [TearDown] public void TearDown()
        {
            foreach (Object item in owned) if (item != null) Object.DestroyImmediate(item);
            owned.Clear();
        }

        // A rig of nine bones under one root, the renderer beside them. Bones 1, 3, 6 and 8 will carry the weights.
        (GameObject root, SkinnedMeshRenderer renderer, Transform[] bones) Rig(string name)
        {
            GameObject root = Own(new GameObject(name));
            var go = new GameObject("renderer"); go.transform.SetParent(root.transform, false);
            var renderer = go.AddComponent<SkinnedMeshRenderer>(); renderer.quality = SkinQuality.Bone4;
            var bones = new Transform[9];
            for (int i = 0; i < bones.Length; i++)
            {
                bones[i] = new GameObject("bone " + i).transform;
                // A chain in part: 3 under 1, 8 under 6 under 5 -- so a kept bone has an ancestor that is not kept.
                bones[i].SetParent(i == 3 ? bones[1] : i == 6 ? bones[5] : i == 8 ? bones[6] : root.transform, false);
                bones[i].localPosition = new Vector3(.11f * i, .07f * (i % 3), -.05f * i);
                bones[i].localRotation = Quaternion.Euler(5 * i, -9 * i, 3 * i);
            }

            return (root, renderer, bones);
        }

        static Matrix4x4[] BindPoses(Transform[] bones, Transform renderer) =>
            bones.Select(b => b.worldToLocalMatrix * renderer.localToWorldMatrix).ToArray();

        // Four vertices, each weighted to bones 1, 3, 6, 8 of nine (four weights a vertex: what the direct skin input takes).
        Mesh Tetra(Transform[] bones, Transform renderer)
        {
            var mesh = Own(new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                normals = Enumerable.Repeat(new Vector3(.2f, .3f, -.7f).normalized, 4).ToArray(),
                uv = new[] { new Vector2(.5f / 256, 255.5f / 256), new Vector2(31.5f / 256, 63.5f / 256), new Vector2(128.5f / 256, 127.5f / 256), new Vector2(255.5f / 256, .5f / 256) },
                triangles = Triangles,
                bindposes = BindPoses(bones, renderer),
            });
            mesh.boneWeights = new[]
            {
                new BoneWeight { boneIndex0 = 1, weight0 = .4f, boneIndex1 = 3, weight1 = .3f, boneIndex2 = 6, weight2 = .2f, boneIndex3 = 8, weight3 = .1f },
                new BoneWeight { boneIndex0 = 8, weight0 = .7f, boneIndex1 = 1, weight1 = .3f },
                new BoneWeight { boneIndex0 = 6, weight0 = .5f, boneIndex1 = 3, weight1 = .25f, boneIndex2 = 1, weight2 = .25f },
                new BoneWeight { boneIndex0 = 3, weight0 = 1f },
            };
            return mesh;
        }

        static void Pose(Transform[] bones, Transform renderer, int pose)
        {
            renderer.SetLocalPositionAndRotation(new Vector3(.2f, -.3f, .1f) * pose, Quaternion.Euler(3 * pose, 17 * pose, -9 * pose));
            for (int i = 0; i < bones.Length; i++)
            {
                bones[i].localPosition = new Vector3(.11f * i + .05f * pose, .07f * (i % 3) - .02f * pose * i, -.05f * i + .03f * pose);
                bones[i].localRotation = Quaternion.Euler(5 * i + 13 * pose, -9 * i + 7 * pose * i, 3 * i - 11 * pose);
            }
        }

        [Test]
        public void UsedBones_AreThoseAVertexIsWeightedToAboveZero_HoweverSmallTheWeight()
        {
            var mesh = Own(new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 }, bindposes = Enumerable.Repeat(Matrix4x4.identity, 10).ToArray() });
            // Vertex 0: bones 4 and 7, the second with a weight of 1e-7. Vertex 1: bone 1, and a place of weight zero on bone 9.
            // Vertex 2: bone 4 alone.
            var counts = new NativeArray<byte>(new byte[] { 2, 2, 1 }, Allocator.Temp);
            var weights = new NativeArray<BoneWeight1>(new[]
            {
                new BoneWeight1 { boneIndex = 4, weight = 1f - 1e-7f }, new BoneWeight1 { boneIndex = 7, weight = 1e-7f },
                new BoneWeight1 { boneIndex = 1, weight = 1f }, new BoneWeight1 { boneIndex = 9, weight = 0f },
                new BoneWeight1 { boneIndex = 4, weight = 1f },
            }, Allocator.Temp);
            mesh.SetBoneWeights(counts, weights);
            counts.Dispose(); weights.Dispose();

            int[] used = VpSkinBoneCompaction.UsedBones(mesh);
            // What the mesh holds after Unity took the weights is what is read: the tiny weight is a weight; whether Unity
            // kept it is said here, and the used bones follow from what it kept, never from a threshold of this code's.
            float tiny;
            using (NativeArray<BoneWeight1> held = mesh.GetAllBoneWeights()) tiny = held.Where(w => w.boneIndex == 7).Select(w => w.weight).DefaultIfEmpty(0f).Max();
            TestContext.Out.WriteLine("the mesh holds a weight of " + tiny.ToString("R") + " on bone 7; used bones " + string.Join(" ", used));
            Assert.That(used, Is.EqualTo(tiny > 0f ? new[] { 1, 4, 7 } : new[] { 1, 4 }), "the bones with a weight above zero in the mesh, ascending");
            Assert.That(used.Contains(9), Is.False, "a place of weight zero is not a use");
        }

        [Test]
        public void Compact_KeepsEverythingButTheNamingOfTheBones_WithMoreThanFourWeightsAVertex()
        {
            const int vertices = 12;
            var mesh = Own(new Mesh());
            mesh.vertices = Enumerable.Range(0, vertices).Select(i => new Vector3(i * .1f, (i % 3) * .2f, (i % 4) * -.15f)).ToArray();
            mesh.normals = Enumerable.Range(0, vertices).Select(i => new Vector3(i % 2, 1, i % 3).normalized).ToArray();
            mesh.tangents = Enumerable.Range(0, vertices).Select(i => new Vector4(1, 0, 0, i % 2 == 0 ? 1 : -1)).ToArray();
            mesh.colors = Enumerable.Range(0, vertices).Select(i => new Color(i / 12f, .5f, 1 - i / 12f, 1)).ToArray();
            mesh.uv = Enumerable.Range(0, vertices).Select(i => new Vector2(i / 12f, 1 - i / 12f)).ToArray();
            mesh.SetUVs(3, Enumerable.Range(0, vertices).Select(i => new Vector4(i, -i, i * .5f, 7)).ToList());
            mesh.subMeshCount = 2;
            mesh.SetIndices(new[] { 0, 1, 2, 2, 1, 3, 3, 4, 5 }, MeshTopology.Triangles, 0);
            mesh.SetIndices(new[] { 6, 7, 8, 9, 10, 11 }, MeshTopology.Triangles, 1);
            mesh.bindposes = Enumerable.Range(0, 20).Select(i => Matrix4x4.TRS(new Vector3(i, -i, .5f * i), Quaternion.Euler(i, 2 * i, 3 * i), Vector3.one)).ToArray();
            // One to six weights a vertex, on bones 2, 3, 5, 11, 13, 17 of twenty, the weights descending and summing to one.
            int[] carrying = { 17, 5, 2, 13, 3, 11 };
            var counts = new byte[vertices];
            var weights = new List<BoneWeight1>();
            for (int v = 0; v < vertices; v++)
            {
                int n = 1 + v % 6;
                counts[v] = (byte)n;
                float[] raw = Enumerable.Range(0, n).Select(k => (float)(n - k)).ToArray();
                float sum = raw.Sum();
                for (int k = 0; k < n; k++) weights.Add(new BoneWeight1 { boneIndex = carrying[(v + k) % carrying.Length], weight = raw[k] / sum });
            }

            var nativeCounts = new NativeArray<byte>(counts, Allocator.Temp);
            var nativeWeights = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp);
            mesh.SetBoneWeights(nativeCounts, nativeWeights);
            nativeCounts.Dispose(); nativeWeights.Dispose();
            var delta = Enumerable.Range(0, vertices).Select(i => new Vector3(0, .01f * i, 0)).ToArray();
            mesh.AddBlendShapeFrame("lift", 50f, delta, null, null);
            mesh.AddBlendShapeFrame("lift", 100f, delta.Select(d => d * 2).ToArray(), null, null);
            mesh.RecalculateBounds();

            int[] kept = VpSkinBoneCompaction.UsedBones(mesh);
            Assert.That(kept, Is.EqualTo(new[] { 2, 3, 5, 11, 13, 17 }));
            Mesh compact = Own(VpSkinBoneCompaction.Compact(mesh, kept));
            Assert.That(VpSkinBoneCompaction.Differences(mesh, compact, kept), Is.Empty, "the copy is the source in everything but the naming of its bones");
            Assert.That(compact.bindposes.Length, Is.EqualTo(6), "six bind poses for twenty");
            Assert.That(mesh.bindposes.Length, Is.EqualTo(20), "the source is as it was");
            using (NativeArray<byte> a = mesh.GetBonesPerVertex())
            using (NativeArray<byte> b = compact.GetBonesPerVertex())
            using (NativeArray<BoneWeight1> wa = mesh.GetAllBoneWeights())
            using (NativeArray<BoneWeight1> wb = compact.GetAllBoneWeights())
            {
                Assert.That(b.ToArray(), Is.EqualTo(a.ToArray()), "as many weights a vertex -- up to six, none cut to four");
                Assert.That(b.ToArray().Max(), Is.EqualTo(6));
                Assert.That(wb.Length, Is.EqualTo(wa.Length));
                for (int k = 0; k < wa.Length; k++)
                {
                    Assert.That(wb[k].weight, Is.EqualTo(wa[k].weight), "weight " + k + " has its value");
                    Assert.That(kept[wb[k].boneIndex], Is.EqualTo(wa[k].boneIndex), "weight " + k + " names the bone it named");
                }
            }

            Assert.That(compact.blendShapeCount, Is.EqualTo(1));
            Assert.That(compact.GetBlendShapeFrameCount(0), Is.EqualTo(2));
            Assert.That(compact.subMeshCount, Is.EqualTo(2));

            // What Differences is for: a copy with a weight on another bone, or with a bind pose of another bone, is told.
            Mesh wrong = Own(Object.Instantiate(compact));
            Matrix4x4[] binds = wrong.bindposes; (binds[0], binds[1]) = (binds[1], binds[0]); wrong.bindposes = binds;
            Assert.That(VpSkinBoneCompaction.Differences(mesh, wrong, kept), Is.Not.Empty, "bind poses in another order are told");
            Assert.That(VpSkinBoneCompaction.Differences(mesh, Own(VpSkinBoneCompaction.Compact(mesh, new[] { 1, 2, 3, 5, 11, 13, 17 })), kept), Is.Not.Empty, "a copy made for another list is told");
            Assert.That(() => VpSkinBoneCompaction.Compact(mesh, new[] { 2, 3, 5, 11, 13 }), Throws.ArgumentException, "a list without a used bone is refused");
        }

        [Test]
        public void Compact_LeavesWeightsThatDoNotSumToOne_AsTheyAre_BitForBit()
        {
            (GameObject _, SkinnedMeshRenderer renderer, Transform[] bones) = Rig("unnormalised");
            Mesh mesh = Tetra(bones, renderer.transform);
            // The first weight of vertex 0 written a little larger where it lies in the vertex data, so that the vertex's
            // weights sum to more than one -- as an imported mesh's may (the first model's did: 1.0000001).
            Assert.That(mesh.GetVertexAttributeFormat(UnityEngine.Rendering.VertexAttribute.BlendWeight), Is.EqualTo(UnityEngine.Rendering.VertexAttributeFormat.Float32));
            int stream = mesh.GetVertexAttributeStream(UnityEngine.Rendering.VertexAttribute.BlendWeight);
            int offset = mesh.GetVertexAttributeOffset(UnityEngine.Rendering.VertexAttribute.BlendWeight);
            using (Mesh.MeshDataArray data = Mesh.AcquireReadOnlyMeshData(mesh))
            {
                var bytes = new NativeArray<byte>(data[0].GetVertexData<byte>(stream), Allocator.Temp);
                float first = System.BitConverter.ToSingle(bytes.Slice(offset, 4).ToArray(), 0);
                byte[] larger = System.BitConverter.GetBytes(first + 3e-6f);
                for (int k = 0; k < 4; k++) bytes[offset + k] = larger[k];
                mesh.SetVertexBufferData(bytes, 0, 0, bytes.Length, stream);
                bytes.Dispose();
            }

            // The weights as the vertex data holds them (Mesh.GetAllBoneWeights gives them made to sum to one again,
            // whatever is held, so it cannot show this): four floats a vertex.
            float[] Held(Mesh of)
            {
                using (Mesh.MeshDataArray data = Mesh.AcquireReadOnlyMeshData(of))
                {
                    byte[] raw = data[0].GetVertexData<byte>(stream).ToArray();
                    int stride = of.GetVertexBufferStride(stream), dimension = of.GetVertexAttributeDimension(UnityEngine.Rendering.VertexAttribute.BlendWeight);
                    var values = new float[of.vertexCount * dimension];
                    for (int v = 0; v < of.vertexCount; v++)
                        for (int d = 0; d < dimension; d++) values[v * dimension + d] = System.BitConverter.ToSingle(raw, v * stride + offset + d * 4);
                    return values;
                }
            }

            float[] held = Held(mesh);
            int weightsAVertex = held.Length / mesh.vertexCount;
            float sum = held.Take(weightsAVertex).Sum();
            TestContext.Out.WriteLine("vertex 0 holds weights " + string.Join(" ", held.Take(weightsAVertex).Select(w => w.ToString("R"))) + ", their sum " + sum.ToString("R"));
            Assert.That(sum, Is.GreaterThan(1f), "the vertex's weights, as held, do not sum to one");

            int[] kept = VpSkinBoneCompaction.UsedBones(mesh);
            Mesh compact = Own(VpSkinBoneCompaction.Compact(mesh, kept));
            Assert.That(VpSkinBoneCompaction.Differences(mesh, compact, kept), Is.Empty);
            Assert.That(compact.GetVertexAttributeStream(UnityEngine.Rendering.VertexAttribute.BlendWeight), Is.EqualTo(stream));
            Assert.That(compact.GetVertexAttributeOffset(UnityEngine.Rendering.VertexAttribute.BlendWeight), Is.EqualTo(offset));
            Assert.That(Held(compact), Is.EqualTo(held), "every weight the copy holds is the source's, bit for bit: none was made to sum to one again");
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        public void TheCompactRenderer_DeformsAsTheSourceDoes_ByUnitysBake_AndByTheDirectSkinInput(int pose)
        {
            (GameObject _, SkinnedMeshRenderer full, Transform[] fullBones) = Rig("full");
            (GameObject _, SkinnedMeshRenderer shortened, Transform[] shortBones) = Rig("shortened");
            Mesh mesh = Tetra(fullBones, full.transform);
            full.sharedMesh = mesh; full.bones = fullBones; full.rootBone = fullBones[0];
            int[] kept = VpSkinBoneCompaction.UsedBones(mesh);
            Assert.That(kept, Is.EqualTo(new[] { 1, 3, 6, 8 }));
            Mesh compact = Own(VpSkinBoneCompaction.Compact(mesh, kept));
            Assert.That(VpSkinBoneCompaction.Differences(mesh, compact, kept), Is.Empty);
            shortened.sharedMesh = compact; shortened.bones = kept.Select(i => shortBones[i]).ToArray(); shortened.rootBone = shortBones[0];
            Assert.That(shortened.bones.Length, Is.EqualTo(4));

            // The same pose given to both rigs -- every bone of both, the kept and the others (ancestors of kept ones among them).
            Pose(fullBones, full.transform, pose);
            Pose(shortBones, shortened.transform, pose);

            var bakeFull = Own(new Mesh());
            var bakeShort = Own(new Mesh());
            full.BakeMesh(bakeFull, true);
            shortened.BakeMesh(bakeShort, true);
            Assert.That(bakeShort.vertices, Is.EqualTo(bakeFull.vertices), "Unity's own skinning gives the same positions");
            Assert.That(bakeShort.normals, Is.EqualTo(bakeFull.normals), "and normals");

            Assert.That(VpDirectSkinInput.TryCreate(full, Topology, 4, out VpDirectSkinInput inputFull), Is.True);
            Assert.That(VpDirectSkinInput.TryCreate(shortened, Topology, 4, out VpDirectSkinInput inputShort), Is.True);
            using (inputFull)
            using (inputShort)
            using (var storageFull = new VpCpuGeometryStorage(32, 96, 8, 8, 8, Allocator.Persistent))
            using (var storageShort = new VpCpuGeometryStorage(32, 96, 8, 8, 8, Allocator.Persistent))
            {
                Assert.That(inputFull.TryAppendTo(storageFull, out VpStoredGeometry a), Is.True);
                Assert.That(inputShort.TryAppendTo(storageShort, out VpStoredGeometry b), Is.True);
                Assert.That(a.cutInputAccepted && b.cutInputAccepted, Is.True, "both are cut inputs");
                for (int i = 0; i < 4; i++)
                {
                    VpRenderVertex x = storageFull.Vertices[i], y = storageShort.Vertices[i];
                    Assert.That(y.position, Is.EqualTo(x.position), "the cut's input vertex " + i + " is the same, bit for bit");
                    Assert.That(y.normal, Is.EqualTo(x.normal), "its normal");
                    Assert.That(y.uv0, Is.EqualTo(x.uv0), "its UV");
                    Assert.That(Vector3.Distance(x.position, bakeFull.vertices[i]), Is.LessThan(3e-6f), "and it is the pose Unity bakes");
                }
            }
        }

        [Test]
        public void TheModelsBoneMap_ResolvesABoneTheRendererNoLongerLists_WithItsOwnBindPose_ForEachIndividual()
        {
            (GameObject _, SkinnedMeshRenderer first, Transform[] firstBones) = Rig("first individual");
            (GameObject _, SkinnedMeshRenderer second, Transform[] secondBones) = Rig("second individual");
            Mesh mesh = Tetra(firstBones, first.transform);
            Matrix4x4[] binds = mesh.bindposes;
            int[] kept = VpSkinBoneCompaction.UsedBones(mesh);
            Mesh compact = Own(VpSkinBoneCompaction.Compact(mesh, kept));
            VpSkinBoneMap map = Own(VpSkinBoneMap.Create(firstBones.Select(b => b.name).ToArray(), binds, kept));
            Assert.That(map.Describes(compact, out string why), Is.True, why);
            Assert.That(map.Describes(mesh, out _), Is.False, "the map is of the compact mesh, not of the source");
            Assert.That(map.BoneCount, Is.EqualTo(9));
            Assert.That(map.CompactCount, Is.EqualTo(4));

            // Two individuals of the model: one mesh and one map shared, each with its own Transforms.
            foreach ((SkinnedMeshRenderer renderer, Transform[] bones) in new[] { (first, firstBones), (second, secondBones) })
            {
                renderer.sharedMesh = compact;
                renderer.bones = kept.Select(i => bones[i]).ToArray();
                renderer.rootBone = bones[0];
                renderer.gameObject.AddComponent<VpSkinBones>().Set(map, bones);
            }

            VpSkinBones a = first.GetComponent<VpSkinBones>(), b = second.GetComponent<VpSkinBones>();
            Assert.That(a.IsConsistentWith(first, out why), Is.True, why);
            Assert.That(b.IsConsistentWith(second, out why), Is.True, why);
            Assert.That(a.Map, Is.SameAs(b.Map), "the map is the model's");

            // Bone 5 carries no weight: the renderers do not list it. It is found by name, each individual's own Transform,
            // with the bind pose the model's mesh had for it -- and so is an ancestor of a kept bone.
            Assert.That(first.bones.Contains(firstBones[5]), Is.False);
            int five = a.Find("bone 5");
            Assert.That(five, Is.EqualTo(5));
            Assert.That(map.CompactOf(five), Is.EqualTo(-1), "not in the shortened list");
            Assert.That(a.BoneAt(five), Is.SameAs(firstBones[5]));
            Assert.That(b.BoneAt(b.Find("bone 5")), Is.SameAs(secondBones[5]), "the second individual's own Transform");
            Assert.That(map.BindPose(five), Is.EqualTo(binds[5]));
            Assert.That(firstBones[6].parent, Is.SameAs(firstBones[5]), "the ancestor of a kept bone is still in the hierarchy");
            // A kept bone by name: the same Transform the renderer lists at its shortened place.
            int six = a.Find("bone 6");
            Assert.That(first.bones[map.CompactOf(six)], Is.SameAs(a.BoneAt(six)));
            Assert.That(map.OriginalOf(map.CompactOf(six)), Is.EqualTo(six));
            Assert.That(a.Find("no such bone"), Is.EqualTo(-1));

            // What is told: another individual's Transforms, a renderer still on the full list, a mesh that is not the map's.
            Assert.That(a.IsConsistentWith(second, out why), Is.False, "the second individual's renderer does not list the first's Transforms");
            TestContext.Out.WriteLine("told: " + why);
            first.bones = firstBones;
            Assert.That(a.IsConsistentWith(first, out why), Is.False, "a renderer listing every bone is not the shortened one");
            first.bones = kept.Select(i => firstBones[i]).ToArray();
            first.sharedMesh = mesh;
            Assert.That(a.IsConsistentWith(first, out why), Is.False, "the source mesh is not the map's mesh");
        }

        [Test]
        public void TheFixedScalePreparation_GivesAnUnlistedBonesBindPose_AsItGivesTheListedOnes()
        {
            // A renderer at a fixed scale of 0.01 (as the Professional models are): the prepared mesh's bind poses are
            // the source's times the inverse scale. A bone the compact mesh does not list gets the very same product.
            (GameObject root, SkinnedMeshRenderer renderer, Transform[] bones) = Rig("scaled");
            renderer.transform.localScale = Vector3.one * 0.01f;
            Mesh mesh = Tetra(bones, renderer.transform);
            Matrix4x4[] binds = mesh.bindposes;
            int[] kept = VpSkinBoneCompaction.UsedBones(mesh);
            Mesh compact = Own(VpSkinBoneCompaction.Compact(mesh, kept));
            renderer.sharedMesh = compact; renderer.bones = kept.Select(i => bones[i]).ToArray(); renderer.rootBone = bones[0];
            using var cache = new VpFixedScaleSkinCache();
            using VpFixedScaleSkinInput input = cache.Prepare(renderer, root.transform);
            Assert.That(input.FixedScale, Is.EqualTo(0.01f).Within(1e-9f));
            Matrix4x4[] prepared = input.Renderer.sharedMesh.bindposes;
            Assert.That(prepared.Length, Is.EqualTo(4));
            for (int c = 0; c < kept.Length; c++)
            {
                Assert.That(input.PrepareBindPose(binds[kept[c]]), Is.EqualTo(prepared[c]), "a listed bone's bind pose, prepared here, is the prepared mesh's own");
            }

            Matrix4x4 unlisted = input.PrepareBindPose(binds[5]);
            Assert.That(unlisted, Is.EqualTo(binds[5] * Matrix4x4.Scale(Vector3.one / input.FixedScale)));
            Assert.That(unlisted, Is.Not.EqualTo(binds[5]), "at a scale other than one it is not the source's");
        }
    }
}
