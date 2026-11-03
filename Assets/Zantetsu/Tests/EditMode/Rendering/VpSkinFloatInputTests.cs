using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// CPU-SKIN-FLOAT-1: the CPU-only float input (<see cref="VpSkinFloatInput"/>) and the synchronous bake that reads it
    /// (<see cref="VpDirectSkinInput"/> with <see cref="VpDirectSkinInput.FloatInputRequested"/>): the bake matches a reference
    /// computed from the mesh's own floats under a fixed palette, bakes the captured pose whatever the bones do afterwards,
    /// shares one input per model, falls back to the 24-byte bake without the input, refuses a mesh that is not the model's,
    /// and keeps the topology, the indices and the bounds of the 24-byte bake.
    /// </summary>
    public class VpSkinFloatInputTests
    {
        readonly System.Collections.Generic.List<Mesh> ownedMeshes = new System.Collections.Generic.List<Mesh>();
        GameObject root;
        Mesh mesh;
        SkinnedMeshRenderer renderer;
        Transform[] bones;
        static readonly int[] Topology = { 0, 1, 2, 3 };
        static readonly int[] Triangles = { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };

        [SetUp] public void SetUp()
        {
            root = new GameObject("CPU float synthetic rig");
            renderer = MakeRenderer("renderer", out bones);
            mesh = Own(new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                normals = new[] { new Vector3(.2f, .3f, -.7f).normalized, new Vector3(-.5f, .1f, .4f).normalized, new Vector3(.9f, -.2f, .1f).normalized, new Vector3(.1f, .95f, .2f).normalized },
                uv = new[] { new Vector2(.5f/256, 255.5f/256), new Vector2(31.5f/256, 63.5f/256), new Vector2(128.5f/256, 127.5f/256), new Vector2(255.5f/256, .5f/256) },
                triangles = Triangles, bindposes = Enumerable.Repeat(Matrix4x4.identity, 4).ToArray(),
                // Weights the 24-byte input cannot hold exactly (not multiples of 1/255): the float input keeps them.
                boneWeights = new[] {
                    new BoneWeight { boneIndex0 = 0, weight0 = .4f, boneIndex1 = 1, weight1 = .3f, boneIndex2 = 2, weight2 = .2f, boneIndex3 = 3, weight3 = .1f },
                    new BoneWeight { boneIndex0 = 1, weight0 = .7f, boneIndex1 = 2, weight1 = .3f },
                    new BoneWeight { boneIndex0 = 2, weight0 = 1f },
                    new BoneWeight { boneIndex0 = 3, weight0 = .33333334f, boneIndex1 = 0, weight1 = .33333334f, boneIndex2 = 1, weight2 = .33333332f } } });
            renderer.sharedMesh = mesh; renderer.bones = bones; renderer.rootBone = bones[0];
        }

        SkinnedMeshRenderer MakeRenderer(string name, out Transform[] rig)
        {
            var go = new GameObject(name); go.transform.SetParent(root.transform, false);
            var r = go.AddComponent<SkinnedMeshRenderer>(); r.quality = SkinQuality.Bone4;
            rig = new Transform[4];
            for (int i = 0; i < rig.Length; i++) { rig[i] = new GameObject(name + " bone " + i).transform; rig[i].SetParent(go.transform, false); }
            return r;
        }

        Mesh Own(Mesh value) { ownedMeshes.Add(value); return value; }

        [TearDown] public void TearDown()
        {
            Object.DestroyImmediate(root);
            foreach (var item in ownedMeshes) Object.DestroyImmediate(item);
            ownedMeshes.Clear();
        }

        static VpCpuGeometryStorage Storage() => new VpCpuGeometryStorage(32, 96, 8, 8, 8, Allocator.Persistent);

        static void Pose(Transform[] rig, int pose)
        {
            for (int i = 0; i < rig.Length; i++)
            {
                rig[i].localPosition = new Vector3(.1f * i * pose, -.03f * i * pose, .07f * i * pose);
                rig[i].localRotation = Quaternion.Euler(7 * i * pose, -11 * i * pose, 4 * i * pose);
            }
        }

        // The reference: the mesh's own float weights, positions and normals skinned by the palette (the bake's arithmetic in managed code).
        static Vector3 ReferencePosition(Mesh m, Matrix4x4[] palette, int i)
        {
            BoneWeight w = m.boneWeights[i]; Vector3 p = m.vertices[i];
            return palette[w.boneIndex0].MultiplyPoint3x4(p) * w.weight0 + palette[w.boneIndex1].MultiplyPoint3x4(p) * w.weight1
                + palette[w.boneIndex2].MultiplyPoint3x4(p) * w.weight2 + palette[w.boneIndex3].MultiplyPoint3x4(p) * w.weight3;
        }

        static Vector3 ReferenceNormal(Mesh m, Matrix4x4[] palette, int i)
        {
            BoneWeight w = m.boneWeights[i]; Vector3 n = m.normals[i];
            return (palette[w.boneIndex0].MultiplyVector(n) * w.weight0 + palette[w.boneIndex1].MultiplyVector(n) * w.weight1
                + palette[w.boneIndex2].MultiplyVector(n) * w.weight2 + palette[w.boneIndex3].MultiplyVector(n) * w.weight3).normalized;
        }

        VpDirectSkinInput InputWithFloat(out VpSkinFloatInput floatInput)
        {
            Assert.That(VpDirectSkinInput.TryCreate(renderer, Topology, 4, out var input), Is.True);
            Assert.That(input.Model.FloatInput, Is.Null, "nothing is made before it is asked for");
            Assert.That(input.Model.TryPrepareFloatInput(out string refusal), Is.True, refusal);
            floatInput = input.Model.FloatInput;
            Assert.That(floatInput, Is.Not.Null);
            return input;
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void AFixedPalette_BakesTheMeshFloatsAsTheReferenceDoes_AndCloserToUnityThanTheQuantizationBound(int pose)
        {
            using var input = InputWithFloat(out VpSkinFloatInput floatInput); using var storage = Storage();
            Pose(bones, pose);
            if (pose == 2) renderer.transform.SetLocalPositionAndRotation(new Vector3(.2f, -.3f, .1f), Quaternion.Euler(3, 17, -9));
            var bake = Own(new Mesh());
            renderer.BakeMesh(bake, true);
            var palette = new Matrix4x4[bones.Length];
            Assert.That(VpSkinPalette.TryGather(renderer.transform.worldToLocalMatrix, renderer.bones, input.Model, palette), Is.True);
            input.FloatInputRequested = true;
            Assert.That(input.TryAppendTo(storage, out var geometry), Is.True);
            Assert.That(input.LastBakeUsedFloatInput, Is.True, "the float input was read");
            Assert.That(input.LastKernelSeconds, Is.GreaterThan(0.0).And.LessThanOrEqualTo(input.LastWriteSeconds));
            Assert.That(storage.TryGetPublishedExtent(geometry, out int start, out int count, out var bounds), Is.True);
            Assert.That(count, Is.EqualTo(4));
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                var v = storage.Vertices[start + i];
                Vector3 reference = ReferencePosition(mesh, palette, i);
                Assert.That(Vector3.Distance(v.position, reference), Is.LessThan(3e-6f), "vertex " + i + ": the bake of the mesh's floats");
                Assert.That(Vector3.Distance(v.position, bake.vertices[i]), Is.LessThan(2e-5f), "vertex " + i + ": Unity's own bake, within float arithmetic (no weight quantization)");
                Assert.That(Vector3.Angle(v.normal, ReferenceNormal(mesh, palette, i)), Is.LessThan(0.8f), "vertex " + i + ": the normal within the output's octahedral encoding");
                Assert.That(v.uv0, Is.EqualTo(mesh.uv[i]), "vertex " + i + ": the UV bytes are the model's");
                min = Vector3.Min(min, v.position); max = Vector3.Max(max, v.position);
            }

            Assert.That(Vector3.Distance(bounds.min, min), Is.LessThan(2e-7f));
            Assert.That(Vector3.Distance(bounds.max, max), Is.LessThan(2e-7f));
            Assert.That(storage.TryGetTopology(geometry, out var topology, out int topologyCount), Is.True);
            Assert.That(topologyCount, Is.EqualTo(4));
            Assert.That(topology.ToArray(), Is.EqualTo(Topology));
            Assert.That(storage.TryAcquireIndexReadLease(geometry.indexRange, out var lease, out var view), Is.True);
            try { Assert.That(view.ToArray(), Is.EqualTo(Triangles.Select(t => (uint)t))); }
            finally { Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True); }
        }

        [Test]
        public void TheCapturedPose_IsBaked_HoweverTheBonesMoveAfterTheCapture()
        {
            using var input = InputWithFloat(out _); using var storage = Storage();
            Pose(bones, 1);
            var palette = new Matrix4x4[bones.Length];
            Assert.That(VpSkinPalette.TryGather(renderer.transform.worldToLocalMatrix, renderer.bones, input.Model, palette), Is.True);
            Assert.That(input.TryCapturePose(), Is.True);
            Pose(bones, 3);
            renderer.transform.position = new Vector3(5f, 0f, 0f);
            input.FloatInputRequested = true;
            Assert.That(input.TryAppendCapturedForDisplay(storage, out var output), Is.True);
            Assert.That(input.LastBakeUsedFloatInput, Is.True);
            Assert.That(storage.TryGetPublishedExtent(output.Geometry, out int start, out int count, out _), Is.True);
            for (int i = 0; i < count; i++)
            {
                Assert.That(Vector3.Distance(storage.Vertices[start + i].position, ReferencePosition(mesh, palette, i)), Is.LessThan(3e-6f), "vertex " + i + ": the pose captured before the bones moved");
            }
        }

        [Test]
        public void TheFloatInput_IsOneAModel_SharedByItsIndividuals_AndOneInputsEndLeavesTheOther()
        {
            Assert.That(VpSkinModel.TryCreate(renderer, Topology, 4, null, out VpSkinModel model, out string why), Is.True, why);
            var second = MakeRenderer("second", out Transform[] bones2);
            second.sharedMesh = mesh; second.bones = bones2; second.rootBone = bones2[0];
            Assert.That(VpDirectSkinInput.TryCreate(renderer, model, out VpDirectSkinInput a), Is.True);
            Assert.That(VpDirectSkinInput.TryCreate(second, model, out VpDirectSkinInput b), Is.True);
            int made = VpSkinFloatInput.CreatedCount;
            Assert.That(model.TryPrepareFloatInput(out string refusal), Is.True, refusal);
            Assert.That(model.TryPrepareFloatInput(out _), Is.True, "asked again: the same input, nothing made");
            Assert.That(VpSkinFloatInput.CreatedCount - made, Is.EqualTo(1), "one float input a model");
            Assert.That(ReferenceEquals(a.Model.FloatInput, b.Model.FloatInput), Is.True, "both individuals read the model's one input");
            Assert.That(model.FloatInputBytes, Is.EqualTo(4L * VpSkinFloatVertex.Stride));
            Assert.That(model.NativeBytes, Is.EqualTo(4L * VpSkinVertex.Stride + 12L * sizeof(uint) + 4L * sizeof(int) + 4L * VpSkinBoneBox.Stride), "the 24-byte input and the model's other data stay; the float input is counted apart");
            using var storage = Storage();
            Pose(bones2, 1);
            a.Dispose();
            Assert.That(model.FloatInput.IsDisposed, Is.False, "the first individual's end frees nothing of the model");
            b.FloatInputRequested = true;
            Assert.That(b.TryAppendTo(storage, out var geometry), Is.True);
            Assert.That(b.LastBakeUsedFloatInput, Is.True);
            var palette = new Matrix4x4[bones2.Length];
            Assert.That(VpSkinPalette.TryGather(second.transform.worldToLocalMatrix, second.bones, model, palette), Is.True);
            Assert.That(storage.TryGetPublishedExtent(geometry, out int start, out int count, out _), Is.True);
            for (int i = 0; i < count; i++) Assert.That(Vector3.Distance(storage.Vertices[start + i].position, ReferencePosition(mesh, palette, i)), Is.LessThan(3e-6f));
            VpSkinFloatInput floatInput = model.FloatInput;
            b.Dispose();
            Assert.That(floatInput.IsDisposed, Is.False, "the model is the owner's: its individuals' ends free nothing");
            model.Dispose();
            Assert.That(floatInput.IsDisposed, Is.True, "freed with the model");
            Assert.That(model.FloatInputBytes, Is.Zero);
        }

        [Test]
        public void WithoutTheFloatInput_TheRequestedBakeReadsThe24ByteInput_AndSaysSo()
        {
            Assert.That(VpDirectSkinInput.TryCreate(renderer, Topology, 4, out var input), Is.True);
            using (input)
            using (var storage = Storage())
            {
                Pose(bones, 1);
                input.FloatInputRequested = true;
                Assert.That(input.TryAppendTo(storage, out var geometry), Is.True);
                Assert.That(input.LastBakeUsedFloatInput, Is.False, "no float input: the 24-byte bake, the request's choice notwithstanding");
                Assert.That(input.Model.FloatInput, Is.Null);
                Assert.That(input.Model.FloatInputBytes, Is.Zero, "nothing allocated for a route that was not prepared");
                Assert.That(geometry.cutInputAccepted, Is.True);
            }
        }

        [Test]
        public void AMeshThatIsNotTheModels_IsRefusedWithAReason_AndTheAnswerIsKept()
        {
            Assert.That(VpDirectSkinInput.TryCreate(renderer, Topology, 4, out var input), Is.True);
            using (input)
            {
                // The mesh changed under the model (a source changed in place must be recreated; the float input will not describe it).
                Vector3[] moved = mesh.vertices; moved[2] = new Vector3(0f, 2f, 0f); mesh.vertices = moved;
                int made = VpSkinFloatInput.CreatedCount;
                Assert.That(input.Model.TryPrepareFloatInput(out string refusal), Is.False);
                Assert.That(refusal, Does.Contain("position").And.Contain("not the model's"));
                Assert.That(input.Model.FloatInput, Is.Null);
                Assert.That(input.Model.FloatInputBytes, Is.Zero);
                Assert.That(VpSkinFloatInput.CreatedCount, Is.EqualTo(made), "nothing was made");
                Assert.That(input.Model.TryPrepareFloatInput(out string again), Is.False, "asked again: the same answer without another try");
                Assert.That(again, Is.EqualTo(refusal));
                Assert.That(input.Model.FloatInputRefusal, Is.EqualTo(refusal));
            }
        }

        [Test]
        public void TheFloatBakeAndThe24ByteBake_ShareTopologyIndicesAndBounds_AndDifferByTheQuantizationOnly()
        {
            using var input = InputWithFloat(out _); using var storage = Storage();
            Pose(bones, 1);
            Assert.That(input.TryCapturePose(), Is.True);
            input.FloatInputRequested = false;
            Assert.That(input.TryAppendCapturedForDisplay(storage, out var packed), Is.True);
            Assert.That(input.LastBakeUsedFloatInput, Is.False);
            Assert.That(input.TryCapturePose(), Is.True);
            input.FloatInputRequested = true;
            Assert.That(input.TryAppendCapturedForDisplay(storage, out var floating), Is.True);
            Assert.That(input.LastBakeUsedFloatInput, Is.True);
            Assert.That(storage.TryGetPublishedExtent(packed.Geometry, out int s0, out int c0, out var b0), Is.True);
            Assert.That(storage.TryGetPublishedExtent(floating.Geometry, out int s1, out int c1, out var b1), Is.True);
            Assert.That(c1, Is.EqualTo(c0));
            Assert.That(storage.TryGetTopology(packed.Geometry, out var t0, out int n0), Is.True);
            Assert.That(storage.TryGetTopology(floating.Geometry, out var t1, out int n1), Is.True);
            Assert.That(t1.ToArray(), Is.EqualTo(t0.ToArray())); Assert.That(n1, Is.EqualTo(n0));
            Assert.That(storage.TryAcquireIndexReadLease(packed.Geometry.indexRange, out var lease0, out var view0), Is.True);
            uint[] indices0;
            try { indices0 = view0.ToArray(); } finally { Assert.That(storage.TryReleaseIndexReadLease(lease0), Is.True); }
            Assert.That(storage.TryAcquireIndexReadLease(floating.Geometry.indexRange, out var lease1, out var view1), Is.True);
            uint[] indices1;
            try { indices1 = view1.ToArray(); } finally { Assert.That(storage.TryReleaseIndexReadLease(lease1), Is.True); }
            Assert.That(indices1.Select(x => x - (uint)s1), Is.EqualTo(indices0.Select(x => x - (uint)s0)), "the same indices, relative to each bake's vertices");
            // The quantization bound of the 24-byte input: each weight within 1/255 of the float, times the carried position's size.
            float bound = 0f;
            for (int i = 0; i < c0; i++)
            {
                Vector3 p = storage.Vertices[s0 + i].position, q = storage.Vertices[s1 + i].position;
                bound = Mathf.Max(bound, Vector3.Distance(p, q));
                Assert.That(Vector3.Distance(p, q), Is.LessThan(4f / 255f * 2f + 1e-5f), "vertex " + i + ": within the weight quantization");
                Assert.That(storage.Vertices[s0 + i].uv0, Is.EqualTo(storage.Vertices[s1 + i].uv0), "the same UV bytes");
                Assert.That(Vector3.Angle(storage.Vertices[s0 + i].normal, storage.Vertices[s1 + i].normal), Is.LessThan(3f), "vertex " + i + ": the normals differ by the input's octahedral quantization at most");
            }

            Assert.That(Vector3.Distance(b0.min, b1.min), Is.LessThanOrEqualTo(bound + 1e-6f));
            Assert.That(Vector3.Distance(b0.max, b1.max), Is.LessThanOrEqualTo(bound + 1e-6f));
        }
    }
}
