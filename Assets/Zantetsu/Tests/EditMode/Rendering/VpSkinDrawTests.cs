using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// The skin draw (DESIGN 4.5.12) at its parts and at the batch: the 24-byte vertex and the records are the strides
    /// the shaders read; the weights quantize to integers of 255, deterministically, by the largest remainder; a model
    /// holds the box of every bone's weighted vertices; the display's GPU bounds are the union the CPU makes from the
    /// same palette, its selection keeps by them, its history follows the rules, and a removed individual is gone from
    /// its slot; and the skin draw by Unity's calls is the picture of the CPU bake of the same pose.
    /// </summary>
    public class VpSkinDrawTests
    {
        private const string ShaderName = "Zantetsu/VP Indexed Indirect Unlit";
        private const int Size = 96;

        private readonly List<Object> _objects = new List<Object>();
        private GameObject _root;
        private Mesh _mesh;
        private SkinnedMeshRenderer _renderer;
        private Transform[] _bones;
        private static readonly int[] Topology = { 0, 1, 2, 3 };
        private static readonly int[] Triangles = { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };

        [SetUp]
        public void SetUp()
        {
            _root = Track(new GameObject("skin draw rig"));
            var go = new GameObject("renderer");
            go.transform.SetParent(_root.transform, false);
            _renderer = go.AddComponent<SkinnedMeshRenderer>();
            _renderer.quality = SkinQuality.Bone4;
            _bones = new Transform[4];
            for (int i = 0; i < _bones.Length; i++)
            {
                _bones[i] = new GameObject("bone " + i).transform;
                _bones[i].SetParent(_root.transform, false);
            }

            // A tetrahedron: v0 of bone 0 alone, v1 of bone 1 alone, v2 of bones 2 and 3 by halves, v3 of every bone by quarters.
            _mesh = Track(new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                normals = new[] { new Vector3(-1f, -1f, -1f).normalized, new Vector3(1f, -0.2f, -0.2f).normalized, new Vector3(-0.2f, 1f, -0.2f).normalized, new Vector3(-0.2f, -0.2f, 1f).normalized },
                uv = new[] { new Vector2(.5f / 256, 255.5f / 256), new Vector2(31.5f / 256, 63.5f / 256), new Vector2(128.5f / 256, 127.5f / 256), new Vector2(255.5f / 256, .5f / 256) },
                triangles = Triangles,
                bindposes = Enumerable.Repeat(Matrix4x4.identity, 4).ToArray(),
                boneWeights = new[]
                {
                    new BoneWeight { boneIndex0 = 0, weight0 = 1f },
                    new BoneWeight { boneIndex0 = 1, weight0 = 1f },
                    new BoneWeight { boneIndex0 = 2, weight0 = .5f, boneIndex1 = 3, weight1 = .5f },
                    new BoneWeight { boneIndex0 = 0, weight0 = .25f, boneIndex1 = 1, weight1 = .25f, boneIndex2 = 2, weight2 = .25f, boneIndex3 = 3, weight3 = .25f },
                },
            });
            _renderer.sharedMesh = _mesh;
            _renderer.bones = _bones;
            _renderer.rootBone = _bones[0];
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object tracked in _objects) if (tracked != null) Object.DestroyImmediate(tracked);
            _objects.Clear();
        }

        private T Track<T>(T tracked) where T : Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        private VpSkinModel Model()
        {
            Assert.That(VpSkinModel.TryCreate(_renderer, Topology, 4, null, out VpSkinModel model, out string refusal), Is.True, refusal);
            return model;
        }

        private static void Pose(Transform[] bones, int pose)
        {
            for (int i = 0; i < bones.Length; i++)
            {
                bones[i].localPosition = pose == 0 ? Vector3.zero : new Vector3(.1f * i * pose, -.03f * i, .07f * i * pose);
                bones[i].localRotation = pose == 0 ? Quaternion.identity : Quaternion.Euler(7f * i * pose, -11f * i, 4f * i);
            }
        }

        [Test]
        public void TheStructures_AreTheStridesTheShadersRead()
        {
            Assert.That(Marshal.SizeOf<VpSkinVertex>(), Is.EqualTo(VpSkinVertex.Stride).And.EqualTo(24));
            Assert.That(Marshal.SizeOf<VpSkinRecord>(), Is.EqualTo(VpSkinRecord.Stride).And.EqualTo(32));
            Assert.That(Marshal.SizeOf<VpSkinBoneBox>(), Is.EqualTo(VpSkinBoneBox.Stride).And.EqualTo(32));
            Assert.That(Marshal.SizeOf<VpInstanceBounds>(), Is.EqualTo(VpInstanceBounds.Stride).And.EqualTo(32));
            Assert.That(Marshal.OffsetOf<VpSkinVertex>("position").ToInt32(), Is.Zero);
            Assert.That(Marshal.OffsetOf<VpSkinVertex>("normalX").ToInt32(), Is.EqualTo(12));
            Assert.That(Marshal.OffsetOf<VpSkinVertex>("bone0").ToInt32(), Is.EqualTo(16));
            Assert.That(Marshal.OffsetOf<VpSkinVertex>("weight0").ToInt32(), Is.EqualTo(20));
            Assert.That(VpSkinVertex.MaxBones, Is.EqualTo(256));
            Assert.That(VpSkinVertex.WeightSum, Is.EqualTo(255));
        }

        [Test]
        public void Quantize_SumsTo255_ByTheLargestRemainder_Deterministically_AndRefusesWhatIsNotAWeight()
        {
            Assert.That(VpSkinWeights.TryQuantize(.4f, .3f, .2f, .1f, out byte a, out byte b, out byte c, out byte d), Is.True);
            Assert.That(a + b + c + d, Is.EqualTo(255));
            // 102 | 76.5 | 51 | 25.5: the floors leave one, given to the first of the two equal fractions.
            Assert.That(new[] { a, b, c, d }, Is.EqualTo(new byte[] { 102, 77, 51, 25 }));
            Assert.That(VpSkinWeights.TryQuantize(.4f, .3f, .2f, .1f, out byte a2, out byte b2, out byte c2, out byte d2), Is.True);
            Assert.That(new[] { a2, b2, c2, d2 }, Is.EqualTo(new[] { a, b, c, d }), "the same floats give the same bytes");
            Assert.That(VpSkinWeights.TryQuantize(1f, 0f, 0f, 0f, out a, out b, out c, out d), Is.True);
            Assert.That(new[] { a, b, c, d }, Is.EqualTo(new byte[] { 255, 0, 0, 0 }));
            Assert.That(VpSkinWeights.TryQuantize(.5f, .5f, 0f, 0f, out a, out b, out c, out d), Is.True);
            Assert.That(new[] { a, b, c, d }, Is.EqualTo(new byte[] { 128, 127, 0, 0 }), "127.5 twice: the lower influence takes the unit");
            Assert.That(VpSkinWeights.TryQuantize(.25f, .25f, .25f, .25f, out a, out b, out c, out d), Is.True);
            Assert.That(new[] { a, b, c, d }, Is.EqualTo(new byte[] { 64, 64, 64, 63 }));
            // Every quantized weight is within one unit of its share, and a weight of zero stays zero.
            var random = new System.Random(7);
            for (int trial = 0; trial < 2000; trial++)
            {
                float w0 = (float)random.NextDouble(), w1 = (float)random.NextDouble(), w2 = trial % 3 == 0 ? 0f : (float)random.NextDouble(), w3 = trial % 5 == 0 ? 0f : (float)random.NextDouble();
                float sum = w0 + w1 + w2 + w3;
                w0 /= sum; w1 /= sum; w2 /= sum; w3 /= sum;
                Assert.That(VpSkinWeights.TryQuantize(w0, w1, w2, w3, out a, out b, out c, out d), Is.True, "trial " + trial);
                Assert.That(a + b + c + d, Is.EqualTo(255), "trial " + trial);
                Assert.That(Mathf.Abs(VpSkinWeights.Of(a) - w0), Is.LessThan(1.01f / 255f), "trial " + trial);
                Assert.That(Mathf.Abs(VpSkinWeights.Of(b) - w1), Is.LessThan(1.01f / 255f), "trial " + trial);
                if (w2 == 0f) Assert.That(c, Is.Zero, "trial " + trial);
                if (w3 == 0f) Assert.That(d, Is.Zero, "trial " + trial);
            }

            Assert.That(VpSkinWeights.TryQuantize(.5f, .3f, .1f, .1f, out _, out _, out _, out _), Is.True);
            Assert.That(VpSkinWeights.TryQuantize(.5f, .3f, .1f, .2f, out _, out _, out _, out _), Is.False, "a sum above one");
            Assert.That(VpSkinWeights.TryQuantize(.5f, .3f, .1f, 0f, out _, out _, out _, out _), Is.False, "a sum below one");
            Assert.That(VpSkinWeights.TryQuantize(1.2f, -.2f, 0f, 0f, out _, out _, out _, out _), Is.False, "a negative weight");
            Assert.That(VpSkinWeights.TryQuantize(float.NaN, 1f, 0f, 0f, out _, out _, out _, out _), Is.False, "not a number");
        }

        [Test]
        public void TheModel_HoldsTheBoxOfEveryBonesWeightedVertices_ItsQuantizedWeights_AndRefusesTooManyBones()
        {
            using VpSkinModel model = Model();
            Assert.That(model.VertexCount, Is.EqualTo(4));
            Assert.That(model.IndexCount, Is.EqualTo(12));
            Assert.That(model.BoneCount, Is.EqualTo(4));
            Assert.That(model.UsedBoneCount, Is.EqualTo(4));
            Assert.That(model.Describes(_renderer), Is.True);
            NativeArray<VpSkinVertex>.ReadOnly vertices = model.Vertices;
            Assert.That(vertices[0].WeightTotal, Is.EqualTo(255));
            Assert.That(new[] { vertices[0].bone0, vertices[0].weight0 }, Is.EqualTo(new byte[] { 0, 255 }));
            Assert.That(new[] { vertices[2].bone0, vertices[2].weight0, vertices[2].bone1, vertices[2].weight1 }, Is.EqualTo(new byte[] { 2, 128, 3, 127 }));
            Assert.That(vertices[3].WeightTotal, Is.EqualTo(255));
            Assert.That(vertices[1].position, Is.EqualTo(Vector3.right));
            Assert.That(vertices[1].BindAttributes.uv0, Is.EqualTo(_mesh.uv[1]));
            Assert.That(Vector3.Angle(vertices[1].BindAttributes.normal, _mesh.normals[1]), Is.LessThan(1.4f));
            Assert.That(model.Indices.ToArray(), Is.EqualTo(Triangles.Select(i => (uint)i)));
            Assert.That(model.Topology.ToArray(), Is.EqualTo(Topology));

            // The boxes, with the margin: bone 0 holds v0 and v3, bone 1 v1 and v3, bones 2 and 3 v2 and v3.
            NativeArray<VpSkinBoneBox>.ReadOnly boxes = model.BoneBoxes;
            void AssertBox(int bone, Vector3 min, Vector3 max)
            {
                Assert.That(boxes[bone].IsEmpty, Is.False, "bone " + bone);
                Assert.That(Vector3.Distance(boxes[bone].centre - boxes[bone].extents, min - Vector3.one * VpSkinModel.BoxMargin), Is.LessThan(1e-6f), "bone " + bone + " min");
                Assert.That(Vector3.Distance(boxes[bone].centre + boxes[bone].extents, max + Vector3.one * VpSkinModel.BoxMargin), Is.LessThan(1e-6f), "bone " + bone + " max");
            }

            AssertBox(0, Vector3.zero, Vector3.forward);
            AssertBox(1, Vector3.zero, new Vector3(1f, 0f, 1f));
            AssertBox(2, Vector3.zero, new Vector3(0f, 1f, 1f));
            AssertBox(3, Vector3.zero, new Vector3(0f, 1f, 1f));

            // Every skinned vertex of a pose is inside the union of the carried boxes (the GPU's bounds).
            Pose(_bones, 2);
            var palette = new Matrix4x4[4];
            Assert.That(VpSkinPalette.TryGather(_renderer.transform.worldToLocalMatrix, _renderer.bones, model, palette), Is.True);
            VpInstanceBounds bounds = model.BoundsOf(palette);
            Assert.That(bounds.IsEmpty, Is.False);
            for (int i = 0; i < 4; i++)
            {
                VpSkinVertex v = vertices[i];
                Vector3 skinned = Vector3.zero;
                for (int j = 0; j < 4; j++) skinned += palette[v.Bone(j)].MultiplyPoint3x4(v.position) * VpSkinWeights.Of(v.Weight(j));
                Assert.That(bounds.ToBounds().Contains(skinned), Is.True, "vertex " + i + " at " + skinned + " inside " + bounds.ToBounds());
            }

            // A fifth bone no vertex uses: empty box, no matrix needed. A 257th: refused.
            Pose(_bones, 0);
            _mesh.bindposes = Enumerable.Repeat(Matrix4x4.identity, 5).ToArray();
            _renderer.bones = _bones.Concat(new[] { _root.transform }).ToArray();
            Assert.That(VpSkinModel.TryCreate(_renderer, Topology, 4, null, out VpSkinModel five, out string refusal), Is.True, refusal);
            using (five)
            {
                Assert.That(five.BoneCount, Is.EqualTo(5));
                Assert.That(five.UsedBoneCount, Is.EqualTo(4));
                Assert.That(five.IsBoneUsed(4), Is.False);
                Assert.That(five.BoneBoxes[4].IsEmpty, Is.True);
            }

            _mesh.bindposes = Enumerable.Repeat(Matrix4x4.identity, 257).ToArray();
            _renderer.bones = Enumerable.Repeat(_root.transform, 257).ToArray();
            Assert.That(VpSkinModel.TryCreate(_renderer, Topology, 4, null, out VpSkinModel many, out refusal), Is.False);
            Assert.That(many, Is.Null);
            Assert.That(refusal, Does.Contain("257"));
        }

        private static VpSkinDisplay Display(int views = 2, int individuals = 4)
        {
            Assert.That(VpSkinDisplay.TryCreate(views, false, null, individuals, 2, individuals * 2 * 8, 64, 64, 128, 256, 512, out VpSkinDisplay display, out string failure), Is.True, failure);
            return display;
        }

        // One frame's update: the Editor's frame count stands still, so the display is told to forget the last frame's.
        private static bool Update(VpSkinDisplay display)
        {
            display.ForgetUpdatedFrameForTest();
            return display.UpdateFrame(false);
        }

        private static void Cull(VpSkinDisplay display, Camera camera, VpCullConditions conditions)
        {
            var commands = new CommandBuffer { name = "VP Skin Draw Test" };
            try
            {
                Assert.That(display.NoteCameraForTest(camera), Is.GreaterThanOrEqualTo(0));
                display.ForgetBoundsFrameForTest();
                Assert.That(display.IssueCull(commands, camera, conditions), Is.True);
                Graphics.ExecuteCommandBuffer(commands);
            }
            finally
            {
                commands.Dispose();
            }
        }

        private Camera PerspectiveCamera(Vector3 position, Quaternion rotation)
        {
            Camera camera = Track(new GameObject("VP Skin Draw Test Eye")).AddComponent<Camera>();
            camera.enabled = false;
            camera.transform.SetPositionAndRotation(position, rotation);
            camera.fieldOfView = 60f;
            camera.aspect = 1f;
            camera.nearClipPlane = 0.3f;
            camera.farClipPlane = 50f;
            return camera;
        }

        [Test]
        public void TheDisplay_WritesEachInstancesBoundsFromItsPalette_SelectsByThem_KeepsTheHistoryRules_AndFreesARemovedSlot()
        {
            using VpSkinModel model = Model();
            using VpSkinDisplay display = Display();
            Material material = Track(new Material(Shader.Find(ShaderName)));
            Assert.That(display.TryRegisterModel(model, material, model.BindBounds, 3, out int modelId, out string failure), Is.True, failure);
            Assert.That(display.TryRegisterModel(model, material, model.BindBounds, 1, out _, out failure), Is.False, "registered already");

            // Two individuals: this rig at the origin, and a second rig of the same model far to the right, posed.
            var second = Track(Object.Instantiate(_root));
            second.transform.position = new Vector3(40f, 0f, 0f);
            SkinnedMeshRenderer secondRenderer = second.GetComponentInChildren<SkinnedMeshRenderer>();
            Transform[] secondBones = secondRenderer.bones;
            int versionA = 1, versionB = 1;
            Assert.That(display.TryAdd(modelId, _renderer.transform, _renderer.bones, () => versionA, null, out VpSkinHandle a), Is.True);
            Assert.That(display.TryAdd(modelId, secondRenderer.transform, secondBones, () => versionB, null, out VpSkinHandle b), Is.True);
            Assert.That(display.IndividualCountOf(modelId), Is.EqualTo(2));
            Pose(secondBones, 2);
            Assert.That(Update(display), Is.True);
            Assert.That(display.PalettesGathered, Is.EqualTo(2));
            Assert.That(display.HistoryResets, Is.EqualTo(2), "a registration begins the history");

            // The same frame asked again (another drawing of the same world draws its own camera): nothing moves, the
            // first answer is given, so the previous transform and palette stay the last frame's (review 1, item 6).
            Assert.That(display.UpdateFrame(false), Is.True);
            Assert.That(display.FramesUpdated, Is.EqualTo(1));
            Assert.That(display.PalettesGathered, Is.EqualTo(2));

            Camera camera = PerspectiveCamera(new Vector3(0f, 0.5f, -4f), Quaternion.identity);
            var conditions = new VpCullConditions();
            conditions.SetEye(0, camera.worldToCameraMatrix, camera.projectionMatrix);
            conditions.eyeCount = 1;
            Cull(display, camera, conditions);
            Assert.That(display.BoundsDispatches, Is.EqualTo(1));

            // The GPU's bounds are the CPU's union of the carried boxes, for each individual's own palette.
            VpInstanceBounds[] gpu = display.ReadInstanceBoundsForTest();
            foreach (VpSkinHandle handle in new[] { a, b })
            {
                Assert.That(display.TryGetForTest(handle, out int slot, out VpSkinRecord record, out Matrix4x4[] palette, out _), Is.True);
                Assert.That(record.flags & VpSkinRecord.ActiveFlag, Is.Not.Zero);
                Assert.That(record.previousPaletteStart, Is.EqualTo(record.paletteStart), "a new individual's previous is its current");
                VpInstanceBounds cpu = model.BoundsOf(palette);
                Assert.That(gpu[slot].IsEmpty, Is.False, handle.ToString());
                Assert.That(Vector3.Distance(gpu[slot].centre, cpu.centre), Is.LessThan(1e-5f), handle + " centre");
                Assert.That(Vector3.Distance(gpu[slot].extents, cpu.extents), Is.LessThan(1e-5f), handle + " extents");
            }

            Assert.That(gpu[2].IsEmpty, Is.True, "the model's free slot");

            // The selection by those bounds: the first in view, the far one not; the arguments count one instance.
            var arguments = new GraphicsBuffer.IndirectDrawIndexedArgs[1];
            display.Batch.CullForwardArguments(0).GetData(arguments, 0, 0, 1);
            Assert.That(arguments[0].instanceCount, Is.EqualTo(1u));
            var visible = new uint[1];
            display.Batch.CullForwardVisible(0).GetData(visible, 0, 0, 1);
            Assert.That(display.TryGetForTest(a, out int slotA, out _, out _, out _), Is.True);
            Assert.That(visible[0], Is.EqualTo((uint)slotA));

            // A frame with no pose change: nothing gathered, previous stays current. A pose change: the other palette
            // slot becomes current and the previous is the one before. A reset: previous is current again.
            Assert.That(Update(display), Is.True);
            Assert.That(display.PalettesGathered, Is.EqualTo(2));
            Assert.That(display.TryGetForTest(a, out _, out VpSkinRecord stillA, out _, out _), Is.True);
            Assert.That(stillA.previousPaletteStart, Is.EqualTo(stillA.paletteStart));
            uint before = stillA.paletteStart;
            Pose(_bones, 1);
            versionA++;
            Assert.That(Update(display), Is.True);
            Assert.That(display.PalettesGathered, Is.EqualTo(3));
            Assert.That(display.TryGetForTest(a, out _, out VpSkinRecord movedA, out Matrix4x4[] paletteA, out _), Is.True);
            Assert.That(movedA.paletteStart, Is.Not.EqualTo(before));
            Assert.That(movedA.previousPaletteStart, Is.EqualTo(before));
            var expected = new Matrix4x4[4];
            Assert.That(VpSkinPalette.TryGather(_renderer.transform.worldToLocalMatrix, _renderer.bones, model, expected), Is.True);
            for (int i = 0; i < 4; i++) for (int k = 0; k < 16; k++) Assert.That(paletteA[i][k], Is.EqualTo(expected[i][k]).Within(1e-6f), "bone " + i);
            Assert.That(display.ResetHistory(a), Is.True);
            Assert.That(Update(display), Is.True);
            Assert.That(display.TryGetForTest(a, out _, out VpSkinRecord resetA, out _, out Matrix4x4 previousA), Is.True);
            Assert.That(resetA.previousPaletteStart, Is.EqualTo(resetA.paletteStart));
            Assert.That(previousA, Is.EqualTo(_renderer.transform.localToWorldMatrix));

            // The second moved far: a teleport resets its history too; moved a little: the previous transform is kept.
            second.transform.position = new Vector3(-40f, 0f, 0f);
            Assert.That(Update(display), Is.True);
            Assert.That(display.TryGetForTest(b, out _, out _, out _, out Matrix4x4 previousB), Is.True);
            Assert.That(previousB, Is.EqualTo(secondRenderer.transform.localToWorldMatrix), "teleported: previous is current");
            second.transform.position = new Vector3(-40.5f, 0f, 0f);
            Assert.That(Update(display), Is.True);
            Assert.That(display.TryGetForTest(b, out _, out _, out _, out previousB), Is.True);
            Assert.That(previousB, Is.EqualTo(previousB), "kept");
            Assert.That(((Vector3)previousB.GetColumn(3)).x, Is.EqualTo(-40f).Within(1e-5f), "the previous frame's transform");

            // The second's root turned off (a dormant character): its record is inactive -- drawn in no pass, its bounds
            // empty -- and its history begins again when it comes back, previous = current (review 1, item 5).
            second.SetActive(false);
            Assert.That(Update(display), Is.True);
            Assert.That(display.IndividualsHidden, Is.EqualTo(1));
            Assert.That(display.TryGetForTest(b, out int hiddenSlot, out VpSkinRecord hidden, out _, out _), Is.True);
            Assert.That(hidden.flags & VpSkinRecord.ActiveFlag, Is.Zero);
            Cull(display, camera, conditions);
            Assert.That(display.ReadInstanceBoundsForTest()[hiddenSlot].IsEmpty, Is.True, "hidden: empty bounds");
            Assert.That(Update(display), Is.True);
            Assert.That(display.IndividualsHidden, Is.EqualTo(1), "counted once");
            Pose(secondBones, 1);
            versionB++;
            second.SetActive(true);
            Assert.That(Update(display), Is.True);
            Assert.That(display.TryGetForTest(b, out _, out VpSkinRecord shown, out _, out Matrix4x4 shownPrevious), Is.True);
            Assert.That(shown.flags & VpSkinRecord.ActiveFlag, Is.Not.Zero);
            Assert.That(shown.previousPaletteStart, Is.EqualTo(shown.paletteStart), "back: the history begins again");
            Assert.That(shownPrevious, Is.EqualTo(secondRenderer.transform.localToWorldMatrix));

            // Removed: the last takes the freed slot, the count drops, and the slot behind is empty after the next bounds.
            Assert.That(display.Remove(a), Is.True);
            Assert.That(display.Remove(a), Is.False);
            Assert.That(display.IsLive(a), Is.False);
            Assert.That(display.IndividualCountOf(modelId), Is.EqualTo(1));
            Assert.That(Update(display), Is.True);
            Assert.That(display.TryGetForTest(b, out int slotB, out _, out _, out _), Is.True);
            Assert.That(slotB, Is.EqualTo(0));
            Cull(display, camera, conditions);
            gpu = display.ReadInstanceBoundsForTest();
            Assert.That(gpu[1].IsEmpty, Is.True, "the freed slot");
            Assert.That(gpu[0].IsEmpty, Is.False);
            display.Batch.CullForwardArguments(0).GetData(arguments, 0, 0, 1);
            Assert.That(arguments[0].instanceCount, Is.Zero, "the one left is out of view");
            Assert.That(display.Describe(), Does.Contain("individuals 1"));
        }

        // One frame under the worker gather: the end of the late update schedules, the first camera updates (the
        // Editor's frame count stands still, so both are told to forget the last frame's).
        private static bool Frame(VpSkinDisplay display, out bool scheduled)
        {
            display.ForgetScheduledFrameForTest();
            display.ForgetUpdatedFrameForTest();
            scheduled = display.ScheduleGather();
            return display.UpdateFrame(false);
        }

        private static void AssertPaletteIs(VpSkinDisplay display, VpSkinHandle handle, SkinnedMeshRenderer renderer, VpSkinModel model, string what)
        {
            Assert.That(display.TryGetForTest(handle, out _, out _, out Matrix4x4[] palette, out _), Is.True, what);
            var expected = new Matrix4x4[model.BoneCount];
            Assert.That(VpSkinPalette.TryGather(renderer.transform.worldToLocalMatrix, renderer.bones, model, expected), Is.True, what);
            for (int i = 0; i < expected.Length; i++) for (int k = 0; k < 16; k++) Assert.That(palette[i][k], Is.EqualTo(expected[i][k]).Within(1e-4f), what + ": bone " + i + " [" + k + "]");
        }

        /// <summary>
        /// **The worker gather (SKIN-BENCH-3).** Under <see cref="VpSkinGatherMode.Worker"/> the palettes come from the jobs
        /// and equal the main thread's; a frame without a pose change schedules nothing; a pose written or a root moved after
        /// the schedule is gathered again on the main thread (the current bones, never the stale read); a bone destroyed
        /// fails the individual's job and it keeps its last pose; a removal while a job is in flight completes it first,
        /// and the freed entries serve the next individual; the same frame asked twice is answered once.
        /// </summary>
        [Test]
        public void TheWorkerGather_EqualsTheMainThreads_RegathersWhatChangedAfterTheSchedule_FailsSafely_AndSurvivesRemoval()
        {
            VpSkinGatherMode modeBefore = VpSkinGatherSetup.Mode;
            VpSkinGatherSetup.Mode = VpSkinGatherMode.Worker;
            try
            {
                using VpSkinModel model = Model();
                using VpSkinDisplay display = Display(individuals: 4);
                Assert.That(display.WorkerForTest, Is.True, "the worker gather is in use: " + display.GatherModeActual);
                Assert.That(display.GatherModeActual, Is.EqualTo("worker"));
                Material material = Track(new Material(Shader.Find(ShaderName)));
                Assert.That(display.TryRegisterModel(model, material, model.BindBounds, 3, out int modelId, out string failure), Is.True, failure);
                var second = Track(Object.Instantiate(_root));
                second.transform.position = new Vector3(4f, 0f, 0f);
                SkinnedMeshRenderer secondRenderer = second.GetComponentInChildren<SkinnedMeshRenderer>();
                Transform[] secondBones = secondRenderer.bones;
                int versionA = 1, versionB = 1;
                Assert.That(display.TryAdd(modelId, _renderer.transform, _renderer.bones, () => versionA, null, out VpSkinHandle a), Is.True);
                Assert.That(display.TryAdd(modelId, secondRenderer.transform, secondBones, () => versionB, null, out VpSkinHandle b), Is.True);
                Pose(_bones, 1);
                Pose(secondBones, 2);

                // Frame 1: both new, both gathered by the jobs, equal to the main thread's gather.
                Assert.That(Frame(display, out bool scheduled), Is.True);
                Assert.That(scheduled, Is.True, "two dirty individuals schedule");
                Assert.That(display.JobsScheduled, Is.EqualTo(1));
                Assert.That(display.LateSchedules, Is.Zero);
                Assert.That(display.LastFrameUsedJobs, Is.True);
                Assert.That(display.LastJobIndividuals, Is.EqualTo(2));
                Assert.That(display.PalettesGathered, Is.EqualTo(2));
                Assert.That(display.LateRegathers, Is.Zero);
                Assert.That(display.JobFailures, Is.Zero);
                AssertPaletteIs(display, a, _renderer, model, "a, frame 1");
                AssertPaletteIs(display, b, secondRenderer, model, "b, frame 1");
                for (int i = 0; i < 4; i++) { Assert.That(display.TryGetForTest(a, out _, out _, out Matrix4x4[] p, out _), Is.True); Assert.That(model.IsBoneUsed(i) || p[i] == Matrix4x4.identity, Is.True, "an unused bone is the identity"); }

                // The same frame asked again: answered once (nothing moves).
                Assert.That(display.UpdateFrame(false), Is.True);
                Assert.That(display.FramesUpdated, Is.EqualTo(1));
                Assert.That(display.ScheduleGather(), Is.False, "the frame was updated already");

                // Frame 2: no pose change, nothing scheduled, nothing gathered; the records say previous = current.
                Assert.That(Frame(display, out scheduled), Is.True);
                Assert.That(scheduled, Is.False);
                Assert.That(display.JobsScheduled, Is.EqualTo(1));
                Assert.That(display.LastFrameUsedJobs, Is.False);
                Assert.That(display.PalettesGathered, Is.EqualTo(2));
                Assert.That(display.TryGetForTest(a, out _, out VpSkinRecord stillA, out _, out _), Is.True);
                Assert.That(stillA.previousPaletteStart, Is.EqualTo(stillA.paletteStart));
                uint before = stillA.paletteStart;

                // Frame 3: a's pose changed: gathered by the job into the other half; previous is the one before.
                Pose(_bones, 2); versionA++;
                Assert.That(Frame(display, out scheduled), Is.True);
                Assert.That(scheduled, Is.True);
                Assert.That(display.LastJobIndividuals, Is.EqualTo(1));
                Assert.That(display.PalettesGathered, Is.EqualTo(3));
                Assert.That(display.TryGetForTest(a, out _, out VpSkinRecord movedA, out _, out _), Is.True);
                Assert.That(movedA.paletteStart, Is.Not.EqualTo(before));
                Assert.That(movedA.previousPaletteStart, Is.EqualTo(before));
                AssertPaletteIs(display, a, _renderer, model, "a, frame 3");

                // Frame 4: a's pose written AFTER the schedule (a hit's forced pose): the job's read is stale; the main
                // thread gathers the current bones.
                display.ForgetScheduledFrameForTest(); display.ForgetUpdatedFrameForTest();
                Pose(_bones, 3); versionA++;
                Assert.That(display.ScheduleGather(), Is.True);
                Pose(_bones, 1); versionA++;
                Assert.That(display.UpdateFrame(false), Is.True);
                Assert.That(display.LateRegathers, Is.EqualTo(1), "the pose version moved after the schedule");
                Assert.That(display.PalettesGathered, Is.EqualTo(4));
                AssertPaletteIs(display, a, _renderer, model, "a, frame 4: the current bones");

                // Frame 5: b's root moved after the schedule: the frame's matrix is not the job's; gathered again.
                display.ForgetScheduledFrameForTest(); display.ForgetUpdatedFrameForTest();
                Pose(secondBones, 1); versionB++;
                Assert.That(display.ScheduleGather(), Is.True);
                second.transform.position = new Vector3(4.5f, 0f, 0f);
                Assert.That(display.UpdateFrame(false), Is.True);
                Assert.That(display.LateRegathers, Is.EqualTo(2), "the root moved after the schedule");
                AssertPaletteIs(display, b, secondRenderer, model, "b, frame 5: the current root");
                Assert.That(display.TryGetForTest(b, out _, out _, out _, out Matrix4x4 previousB), Is.True);
                Assert.That(((Vector3)previousB.GetColumn(3)).x, Is.EqualTo(4f).Within(1e-5f), "the previous transform is the frame before's");

                // Frame 6: a used bone of b destroyed: its job fails, it keeps its last pose, the failure is named.
                Assert.That(display.TryGetForTest(b, out _, out VpSkinRecord beforeFailure, out _, out _), Is.True);
                Object.DestroyImmediate(secondBones[1].gameObject);
                versionB++;
                Assert.That(Frame(display, out scheduled), Is.True);
                Assert.That(scheduled, Is.True);
                Assert.That(display.JobFailures, Is.EqualTo(1));
                Assert.That(display.PalettesGathered, Is.EqualTo(5), "nothing gathered for the failed individual");
                Assert.That(display.LastFailure, Does.Contain("could not be gathered"));
                Assert.That(display.TryGetForTest(b, out _, out VpSkinRecord afterFailure, out _, out _), Is.True);
                Assert.That(afterFailure.paletteStart, Is.EqualTo(beforeFailure.paletteStart), "the last pose stands");
                // A failing individual is tried again every frame (its version is never taken), as on the main thread.
                Assert.That(Frame(display, out scheduled), Is.True);
                Assert.That(scheduled, Is.True, "b is still dirty");
                Assert.That(display.JobFailures, Is.EqualTo(2));
                Assert.That(display.Remove(b), Is.True);

                // Frame 7: a removed while its job is in flight: the job is completed first, nothing of a is published,
                // and a third individual takes the freed entries and is gathered by the jobs.
                display.ForgetScheduledFrameForTest(); display.ForgetUpdatedFrameForTest();
                Pose(_bones, 2); versionA++;
                Assert.That(display.ScheduleGather(), Is.True);
                Assert.That(display.Remove(a), Is.True);
                Assert.That(display.CompletedEarly, Is.EqualTo(1));
                var third = Track(Object.Instantiate(_root));
                third.transform.position = new Vector3(-4f, 0f, 0f);
                SkinnedMeshRenderer thirdRenderer = third.GetComponentInChildren<SkinnedMeshRenderer>();
                Transform[] thirdBones = thirdRenderer.bones;
                Pose(thirdBones, 3);
                int versionC = 1;
                Assert.That(display.TryAdd(modelId, thirdRenderer.transform, thirdBones, () => versionC, null, out VpSkinHandle c), Is.True);
                Assert.That(display.UpdateFrame(false), Is.True);
                Assert.That(display.IsLive(a), Is.False);
                Assert.That(display.PalettesGathered, Is.EqualTo(6), "c gathered (on the main thread: added after the schedule)");
                AssertPaletteIs(display, c, thirdRenderer, model, "c, frame 7");
                Assert.That(Frame(display, out scheduled), Is.True);
                Assert.That(scheduled, Is.False, "c is current");
                Pose(thirdBones, 1); versionC++;
                Assert.That(Frame(display, out scheduled), Is.True);
                Assert.That(scheduled, Is.True);
                Assert.That(display.LastJobIndividuals, Is.EqualTo(1), "c by the jobs, through the entries a freed");
                AssertPaletteIs(display, c, thirdRenderer, model, "c, frame 9");

                // A frame scheduled but never updated (no camera drew it): the next frame's schedule completes and drops
                // that job before it writes the inputs again, and the individual -- still dirty -- is gathered anew with
                // the current bones (TL review of SKIN-BENCH-3, P2).
                display.ForgetScheduledFrameForTest(); display.ForgetUpdatedFrameForTest();
                Pose(thirdBones, 2); versionC++;
                Assert.That(display.ScheduleGather(), Is.True, "scheduled, never updated");
                Pose(thirdBones, 3); versionC++;
                Assert.That(Frame(display, out scheduled), Is.True);
                Assert.That(scheduled, Is.True);
                Assert.That(display.UncollectedJobs, Is.EqualTo(1));
                Assert.That(display.LateRegathers, Is.EqualTo(2), "the new job read the current bones: no regather");
                AssertPaletteIs(display, c, thirdRenderer, model, "c after an uncollected frame: the current bones");
                Assert.That(display.Describe(), Does.Contain("uncollected 1"));

                // Hidden: not scheduled, not gathered, its record inactive.
                third.SetActive(false);
                versionC++;
                Assert.That(Frame(display, out scheduled), Is.True);
                Assert.That(scheduled, Is.False, "a hidden individual is not scheduled");
                Assert.That(display.IndividualsHidden, Is.EqualTo(1));
                Assert.That(display.Describe(), Does.Contain("gather worker"));

                // Disposed with a job in flight: completed first (the using disposes below).
                third.SetActive(true);
                display.ForgetScheduledFrameForTest(); display.ForgetUpdatedFrameForTest();
                Assert.That(display.ScheduleGather(), Is.True);
            }
            finally
            {
                VpSkinGatherSetup.Mode = modeBefore;
            }
        }

        /// <summary>
        /// **Who the display draws, its room and its refusals (SKIN-BENCH-3, TL review 3).** An individual is active --
        /// drawn -- only after the first update gathers its palette, and not after its removal; an unknown handle is not.
        /// A model registered twice is refused and counted with its reason; an individual asked of no model is refused and
        /// counted; the description says the room and its use (the walk's record reads it to say whether every model was carried).
        /// </summary>
        [Test]
        public void TheDisplay_SaysWhoItDraws_ItsRoom_AndItsRefusals()
        {
            using VpSkinModel model = Model();
            using VpSkinDisplay display = Display(individuals: 4);
            Assert.That(display.ModelCapacity, Is.EqualTo(2));
            Assert.That(display.BoneBoxCapacity, Is.EqualTo(64));
            Assert.That(display.IsActive(default), Is.False, "no handle");
            Material material = Track(new Material(Shader.Find(ShaderName)));
            Assert.That(display.TryRegisterModel(model, material, model.BindBounds, 2, out int modelId, out string failure), Is.True, failure);
            Assert.That(display.ModelRegistrations, Is.EqualTo(1));
            Assert.That(display.BoneBoxesPlaced, Is.EqualTo(model.BoneCount));
            Assert.That(display.InstancesPlaced, Is.EqualTo(2));
            Assert.That(display.TryRegisterModel(model, material, model.BindBounds, 2, out _, out failure), Is.False);
            Assert.That(failure, Does.Contain("registered already"));
            Assert.That(display.ModelRefusals, Is.EqualTo(1));
            Assert.That(display.LastModelRefusal, Is.EqualTo(failure));
            Assert.That(display.TryAdd(modelId + 7, _renderer.transform, _renderer.bones, () => 1, null, out VpSkinHandle none), Is.False, "no such model");
            Assert.That(none.IsSet, Is.False);
            Assert.That(display.IndividualRefusals, Is.EqualTo(1));
            int version = 1;
            Assert.That(display.TryAdd(modelId, _renderer.transform, _renderer.bones, () => version, null, out VpSkinHandle a), Is.True);
            Assert.That(display.IsActive(a), Is.False, "registered, not yet gathered: not drawn");
            Pose(_bones, 1);
            Assert.That(Frame(display, out _), Is.True);
            Assert.That(display.IsActive(a), Is.True, "gathered: drawn");
            Assert.That(display.Describe(), Does.Contain("room: models 1 of 2 (registrations 1, refusals 1, last: ").And.Contain("instance slots 2 of 4 (individual refusals 1), bone boxes " + model.BoneCount + " of 64"));
            Assert.That(display.Remove(a), Is.True);
            Assert.That(display.IsActive(a), Is.False, "removed: not drawn");
            Assert.That(display.IsActive(new VpSkinHandle(a.id, a.generation + 1)), Is.False, "another generation: not drawn");
        }

        /// <summary>
        /// **The worker's threshold (SKIN-BENCH-3, TL review).** Under the worker mode with a threshold, a frame with fewer
        /// dirty individuals than the threshold schedules nothing and is gathered on the main thread (counted), a frame with
        /// as many goes to the jobs; the palettes are the same either way.
        /// </summary>
        [Test]
        public void TheWorkerThreshold_KeepsSmallFramesOnTheMainThread()
        {
            VpSkinGatherMode modeBefore = VpSkinGatherSetup.Mode;
            int fromBefore = VpSkinGatherSetup.WorkerFrom;
            VpSkinGatherSetup.Mode = VpSkinGatherMode.Worker;
            VpSkinGatherSetup.WorkerFrom = 2;
            try
            {
                using VpSkinModel model = Model();
                using VpSkinDisplay display = Display(individuals: 4);
                Assert.That(display.WorkerForTest, Is.True);
                Assert.That(display.WorkerFrom, Is.EqualTo(2));
                Assert.That(display.GatherModeActual, Is.EqualTo("worker-from-2"));
                Material material = Track(new Material(Shader.Find(ShaderName)));
                Assert.That(display.TryRegisterModel(model, material, model.BindBounds, 3, out int modelId, out string failure), Is.True, failure);
                var second = Track(Object.Instantiate(_root));
                second.transform.position = new Vector3(4f, 0f, 0f);
                SkinnedMeshRenderer secondRenderer = second.GetComponentInChildren<SkinnedMeshRenderer>();
                int versionA = 1, versionB = 1;
                Assert.That(display.TryAdd(modelId, _renderer.transform, _renderer.bones, () => versionA, null, out VpSkinHandle a), Is.True);
                Assert.That(display.TryAdd(modelId, secondRenderer.transform, secondRenderer.bones, () => versionB, null, out VpSkinHandle b), Is.True);
                Pose(_bones, 1); Pose(secondRenderer.bones, 2);
                // Two dirty: as many as the threshold -- the jobs.
                Assert.That(Frame(display, out bool scheduled), Is.True);
                Assert.That(scheduled, Is.True);
                Assert.That(display.LastJobIndividuals, Is.EqualTo(2));
                Assert.That(display.BelowThresholdFrames, Is.Zero);
                // One dirty: below the threshold -- the main thread, counted; the palette is current all the same.
                Pose(_bones, 2); versionA++;
                Assert.That(Frame(display, out scheduled), Is.True);
                Assert.That(scheduled, Is.False, "below the threshold: nothing scheduled");
                Assert.That(display.BelowThresholdFrames, Is.EqualTo(1));
                Assert.That(display.LastFrameUsedJobs, Is.False);
                Assert.That(display.PalettesGathered, Is.EqualTo(3));
                AssertPaletteIs(display, a, _renderer, model, "a below the threshold");
                Assert.That(display.JobsScheduled, Is.EqualTo(1));
            }
            finally
            {
                VpSkinGatherSetup.Mode = modeBefore;
                VpSkinGatherSetup.WorkerFrom = fromBefore;
            }
        }

        /// <summary>
        /// DIAGNOSIS (SKIN-BENCH-2 section 6): under a palette join gap, nearby palette writes go up as one SetData, the
        /// untouched matrices between them sent again; the GPU palette is the same as without the gap (the CPU copy is
        /// what the GPU holds either way), only the write count falls and the extra bytes are counted.
        /// </summary>
        [Test]
        public void ThePaletteJoinGap_JoinsNearbyWrites_AndTheBufferIsTheSame()
        {
            int gapBefore = VpSkinDisplay.PaletteJoinGapMatrices;
            VpSkinDisplay display = null;
            try
            {
                VpSkinDisplay.PaletteJoinGapMatrices = 0;
                VpSkinModel model = Model();
                int bones = model.BoneCount;
                Assert.That(VpSkinDisplay.TryCreate(1, false, null, 4, 2, 4 * 2 * bones + 16, 16, 64, 192, 256, 768, out display, out string failure), Is.True, failure);
                Material material = Track(new Material(Shader.Find(ShaderName)));
                Assert.That(display.TryRegisterModel(model, material, model.BindBounds, 3, out int modelId, out failure), Is.True, failure);
                // Three individuals of one model: each gathered palette alternates between its pair's halves, so three
                // writes a frame stand apart by a bone count.
                var rigs = new List<(Transform frame, Transform[] bones)>();
                int version = 0;
                for (int i = 0; i < 3; i++)
                {
                    SkinnedMeshRenderer r = i == 0 ? _renderer : Track(Object.Instantiate(_root)).GetComponentInChildren<SkinnedMeshRenderer>();
                    rigs.Add((r.transform, r.bones));
                    Assert.That(display.TryAdd(modelId, r.transform, r.bones, () => version, null, out _), Is.True);
                }

                Pose(_bones, 1);
                version = 1;
                Assert.That(display.UpdateFrame(false), Is.True, display.LastFailure);
                long writes0 = display.PaletteWrites, bytes0 = display.PaletteBytesWritten, extra0 = display.PaletteBytesExtra;
                Pose(_bones, 2);
                version = 2;
                display.ForgetUpdatedFrameForTest();
                Assert.That(display.UpdateFrame(false), Is.True, display.LastFailure);
                Assert.That(display.PaletteWrites - writes0, Is.EqualTo(3), "without the gap: one write an individual");
                Assert.That(display.PaletteBytesExtra - extra0, Is.Zero);
                var withoutGap = new Matrix4x4[display.ModelOf(modelId).BoneCount * 6];
                display.PaletteBufferForTest.GetData(withoutGap, 0, display.ModelPaletteStartForTest(modelId), withoutGap.Length);

                VpSkinDisplay.PaletteJoinGapMatrices = 100000;
                Pose(_bones, 3);
                version = 3;
                display.ForgetUpdatedFrameForTest();
                long writes1 = display.PaletteWrites, extra1 = display.PaletteBytesExtra;
                Assert.That(display.UpdateFrame(false), Is.True, display.LastFailure);
                Assert.That(display.PaletteWrites - writes1, Is.EqualTo(1), "with the gap: one write for the three");
                Assert.That(display.PaletteBytesExtra - extra1, Is.EqualTo(2L * bones * 64), "the two untouched halves between them sent again");
                var withGap = new Matrix4x4[withoutGap.Length];
                display.PaletteBufferForTest.GetData(withGap, 0, display.ModelPaletteStartForTest(modelId), withGap.Length);
                // The GPU holds the CPU copy whole: every matrix equals the CPU array's (gathered or untouched alike).
                Matrix4x4[] cpu = display.PaletteCpuForTest;
                int start = display.ModelPaletteStartForTest(modelId);
                for (int i = 0; i < withGap.Length; i++)
                    for (int k = 0; k < 16; k++) Assert.That(withGap[i][k], Is.EqualTo(cpu[start + i][k]), "matrix " + i + " element " + k);
                // And the halves not gathered this frame are what the frame before left there.
                Pose(_bones, 1);
            }
            finally
            {
                VpSkinDisplay.PaletteJoinGapMatrices = gapBefore;
                display?.Dispose();
            }
        }

        /// <summary>
        /// A registration refused -- no instance room, or no geometry room for the placement -- holds nothing of the
        /// model and places nothing: the model's owner frees it alone (review 2, item 2).
        /// </summary>
        [Test]
        public void ARefusedRegistration_HoldsNothing_AndPlacesNothing()
        {
            VpSkinModel first = Model();
            VpSkinModel second = Model();
            // A geometry of one model's room that may not grow (the limit is the capacity).
            Assert.That(VpSkinDisplay.TryCreate(1, false, null, 2, 2, 2 * 2 * 8, 16, 4, 12, 4, 12, out VpSkinDisplay display, out string failure), Is.True, failure);
            Material material = Track(new Material(Shader.Find(ShaderName)));
            Assert.That(display.TryRegisterModel(second, material, second.BindBounds, 3, out _, out failure), Is.False);
            Assert.That(failure, Does.Contain("instance capacity"));
            Assert.That(second.HoldCount, Is.Zero, "refused before anything: not held");
            Assert.That(display.TryRegisterModel(first, material, first.BindBounds, 1, out int firstId, out failure), Is.True, failure);
            Assert.That(display.TryRegisterModel(second, material, second.BindBounds, 1, out _, out failure), Is.False, "no geometry room");
            Assert.That(failure, Does.Contain("limit"));
            Assert.That(second.HoldCount, Is.Zero, "a refused placement holds nothing");
            Assert.That(display.ModelIdOf(second), Is.EqualTo(-1));
            Assert.That(display.Geometry.ModelsPlaced, Is.EqualTo(1), "nothing placed by the refusal");
            Assert.That(display.ModelCount, Is.EqualTo(1));
            Assert.That(display.ModelIdOf(first), Is.EqualTo(firstId));
            Assert.That(first.HoldCount, Is.EqualTo(1));
            second.Dispose();
            Assert.That(second.IsDisposed, Is.True, "its owner's end frees it: nothing holds it");
            display.Dispose();
            Assert.That(first.HoldCount, Is.Zero);
            first.Dispose();
            Assert.That(first.IsDisposed, Is.True);
        }

        /// <summary>
        /// A model is read by the display (written again into a grown geometry) and by a bake past its owner's end: the
        /// owner's Dispose frees nothing while a reader holds it, and the last release frees it. A placement that grows
        /// the geometry gives the draw buffers the new buffers and their generations, so the route takes the pointers
        /// again (review 1, items 1 and 2).
        /// </summary>
        [Test]
        public void TheModel_IsHeldByItsReaders_PastItsOwnersEnd_AndAGrownGeometry_ReachesTheDrawBuffers()
        {
            VpSkinModel first = Model();
            VpSkinModel second = Model();
            // A geometry of one model's room: the second placement grows it, and every placed model is written again.
            Assert.That(VpSkinDisplay.TryCreate(1, false, null, 2, 2, 2 * 2 * 8, 16, 4, 12, 64, 192, out VpSkinDisplay display, out string failure), Is.True, failure);
            Material material = Track(new Material(Shader.Find(ShaderName)));
            Assert.That(display.TryRegisterModel(first, material, first.BindBounds, 1, out int firstId, out failure), Is.True, failure);
            Assert.That(display.ModelIdOf(first), Is.EqualTo(firstId));
            Assert.That(display.ModelIdOf(second), Is.EqualTo(-1));
            Assert.That(first.HoldCount, Is.EqualTo(1), "held by the display");
            uint vertexGeneration = display.DrawBuffers.VertexGeneration, indexGeneration = display.DrawBuffers.IndexGeneration;
            Assert.That(vertexGeneration, Is.EqualTo(display.Geometry.VertexGeneration).And.GreaterThan(0u));
            Assert.That(indexGeneration, Is.EqualTo(display.Geometry.IndexGeneration).And.GreaterThan(0u));

            // The owner is done with the first model; the display still reads it: the growth writes it again.
            first.Dispose();
            Assert.That(first.IsDisposed, Is.False, "held");
            Assert.That(display.TryRegisterModel(second, material, second.BindBounds, 1, out int secondId, out failure), Is.True, failure);
            Assert.That(secondId, Is.Not.EqualTo(firstId));
            Assert.That(display.Geometry.GrowthCount, Is.EqualTo(1));
            Assert.That(display.Geometry.VerticesPlaced, Is.EqualTo(8));
            Assert.That(display.DrawBuffers.VertexBuffer, Is.SameAs(display.Geometry.VertexBuffer));
            Assert.That(display.DrawBuffers.IndexBuffer, Is.SameAs(display.Geometry.IndexBuffer));
            Assert.That(display.DrawBuffers.VertexGeneration, Is.EqualTo(display.Geometry.VertexGeneration).And.GreaterThan(vertexGeneration), "the route takes the new buffer's pointer");
            Assert.That(display.DrawBuffers.IndexGeneration, Is.EqualTo(display.Geometry.IndexGeneration).And.GreaterThan(indexGeneration));

            // A bake borrowing the second model holds it too; the display's end releases both; the last release frees.
            Assert.That(VpDirectSkinInput.TryCreate(_renderer, second, out VpDirectSkinInput input), Is.True);
            Assert.That(second.HoldCount, Is.EqualTo(2));
            display.Dispose();
            Assert.That(first.IsDisposed, Is.True, "freed by the last release, after the owner's end");
            Assert.That(second.IsDisposed, Is.False);
            Assert.That(second.HoldCount, Is.EqualTo(1));
            second.Dispose();
            Assert.That(second.IsDisposed, Is.False, "the bake still reads it");
            input.Dispose();
            Assert.That(second.IsDisposed, Is.True);
        }

        [Test]
        public void TheSetup_ReadsTheLaunch_AndWantsTheSkinDrawOnlyWhereAsked()
        {
            Assert.That(VpSkinDrawSetup.ReadRequest(new[] { "player.exe" }), Is.EqualTo(VpSkinDrawRequest.Default));
            Assert.That(VpSkinDrawSetup.ReadRequest(null), Is.EqualTo(VpSkinDrawRequest.Default));
            Assert.That(VpSkinDrawSetup.ReadRequest(new[] { "player.exe", VpSkinDrawSetup.RenderersArgument }), Is.EqualTo(VpSkinDrawRequest.Renderers));
            Assert.That(VpSkinDrawSetup.ReadRequest(new[] { "player.exe", VpSkinDrawSetup.SkinDrawArgument }), Is.EqualTo(VpSkinDrawRequest.SkinDraw));
            Assert.That(VpSkinDrawSetup.ReadRequest(new[] { VpSkinDrawSetup.SkinDrawArgument, VpSkinDrawSetup.RenderersArgument }), Is.EqualTo(VpSkinDrawRequest.Renderers), "the renderers win");
            // The renderers by default, until the skin draw's device check (review 1 of SKIN24-1): the skin draw where asked for.
            Assert.That(VpSkinDrawSetup.Wanted(VpSkinDrawRequest.Default, "VP3C-NATIVE"), Is.False);
            Assert.That(VpSkinDrawSetup.Wanted(VpSkinDrawRequest.Default, "VP3C"), Is.False);
            Assert.That(VpSkinDrawSetup.Wanted(VpSkinDrawRequest.Default, "VP3"), Is.False);
            Assert.That(VpSkinDrawSetup.Wanted(VpSkinDrawRequest.SkinDraw, "VP3C-NATIVE"), Is.True);
            Assert.That(VpSkinDrawSetup.Wanted(VpSkinDrawRequest.SkinDraw, "VP3C"), Is.True);
            Assert.That(VpSkinDrawSetup.Wanted(VpSkinDrawRequest.SkinDraw, "VP3"), Is.False);
            Assert.That(VpSkinDrawSetup.Wanted(VpSkinDrawRequest.Renderers, "VP3C-NATIVE"), Is.False);
            Assert.That(VpSkinDrawSetup.RenderersArgument, Is.EqualTo("-zantetsuSkinUnity"));

            // The room by arguments (SKIN-BENCH-3, TL review 3): 0 absent; a whole number 1..1000000; anything else is said and 0.
            Assert.That(VpSkinDrawSetup.ModelCapacityArgument, Is.EqualTo("-zantetsuSkinModelCapacity"));
            Assert.That(VpSkinDrawSetup.BoneBoxCapacityArgument, Is.EqualTo("-zantetsuSkinBoneBoxCapacity"));
            Assert.That(VpSkinDrawSetup.ReadCapacity(new[] { "player.exe" }, VpSkinDrawSetup.ModelCapacityArgument, out string roomError), Is.Zero); Assert.That(roomError, Is.Null);
            Assert.That(VpSkinDrawSetup.ReadCapacity(null, VpSkinDrawSetup.ModelCapacityArgument, out roomError), Is.Zero);
            Assert.That(VpSkinDrawSetup.ReadCapacity(new[] { VpSkinDrawSetup.ModelCapacityArgument, "64" }, VpSkinDrawSetup.ModelCapacityArgument, out roomError), Is.EqualTo(64)); Assert.That(roomError, Is.Null);
            Assert.That(VpSkinDrawSetup.ReadCapacity(new[] { VpSkinDrawSetup.ModelCapacityArgument, "64" }, VpSkinDrawSetup.BoneBoxCapacityArgument, out roomError), Is.Zero, "the other argument is not read");
            Assert.That(VpSkinDrawSetup.ReadCapacity(new[] { VpSkinDrawSetup.BoneBoxCapacityArgument, "8192" }, VpSkinDrawSetup.BoneBoxCapacityArgument, out roomError), Is.EqualTo(8192));
            Assert.That(VpSkinDrawSetup.ReadCapacity(new[] { VpSkinDrawSetup.ModelCapacityArgument, "0" }, VpSkinDrawSetup.ModelCapacityArgument, out roomError), Is.Zero); Assert.That(roomError, Is.Not.Null);
            Assert.That(VpSkinDrawSetup.ReadCapacity(new[] { VpSkinDrawSetup.ModelCapacityArgument, "many" }, VpSkinDrawSetup.ModelCapacityArgument, out roomError), Is.Zero); Assert.That(roomError, Is.Not.Null);
            Assert.That(VpSkinDrawSetup.ReadCapacity(new[] { VpSkinDrawSetup.ModelCapacityArgument }, VpSkinDrawSetup.ModelCapacityArgument, out roomError), Is.Zero); Assert.That(roomError, Is.Not.Null);
            Assert.That(VpSkinDrawSetup.ReadCapacity(new[] { VpSkinDrawSetup.ModelCapacityArgument, "1000001" }, VpSkinDrawSetup.ModelCapacityArgument, out roomError), Is.Zero); Assert.That(roomError, Is.Not.Null);

            // The gathering (SKIN-BENCH-3): sync unless the worker is asked for by name; an unknown value is said and ignored.
            Assert.That(VpSkinGatherSetup.ReadMode(null, out string error), Is.EqualTo(VpSkinGatherMode.Sync)); Assert.That(error, Is.Null);
            Assert.That(VpSkinGatherSetup.ReadMode(new[] { "player.exe" }, out error), Is.EqualTo(VpSkinGatherMode.Sync));
            Assert.That(VpSkinGatherSetup.ReadMode(new[] { "player.exe", VpSkinGatherSetup.Argument, "worker" }, out error), Is.EqualTo(VpSkinGatherMode.Worker)); Assert.That(error, Is.Null);
            Assert.That(VpSkinGatherSetup.ReadMode(new[] { VpSkinGatherSetup.Argument, "Sync" }, out error), Is.EqualTo(VpSkinGatherMode.Sync));
            Assert.That(VpSkinGatherSetup.ReadMode(new[] { VpSkinGatherSetup.Argument, "fast" }, out error), Is.EqualTo(VpSkinGatherMode.Sync)); Assert.That(error, Does.Contain("fast"));
            Assert.That(VpSkinGatherSetup.ReadMode(new[] { VpSkinGatherSetup.Argument }, out error), Is.EqualTo(VpSkinGatherMode.Sync)); Assert.That(error, Is.Not.Null);
            Assert.That(VpSkinGatherSetup.Argument, Is.EqualTo("-zantetsuSkinGather"));
            Assert.That(VpSkinGatherSetup.ReadWorkerFrom(new[] { "player.exe" }, out error), Is.Zero);
            Assert.That(VpSkinGatherSetup.ReadWorkerFrom(new[] { VpSkinGatherSetup.WorkerFromArgument, "16" }, out error), Is.EqualTo(16)); Assert.That(error, Is.Null);
            Assert.That(VpSkinGatherSetup.ReadWorkerFrom(new[] { VpSkinGatherSetup.WorkerFromArgument, "-1" }, out error), Is.Zero); Assert.That(error, Is.Not.Null);
            Assert.That(VpSkinGatherSetup.ReadWorkerFrom(new[] { VpSkinGatherSetup.WorkerFromArgument }, out error), Is.Zero); Assert.That(error, Is.Not.Null);

            // The skin casters' slice selection on Vulkan (SKIN-BENCH-3): off unless asked; the ordinary casters' 0 on Vulkan stands.
            Assert.That(VpGpuCullSetup.ReadSkin24SliceSelection(new[] { "player.exe" }), Is.False);
            Assert.That(VpGpuCullSetup.ReadSkin24SliceSelection(new[] { "player.exe", VpGpuCullSetup.Skin24SliceSelectionArgument }), Is.True);
            Assert.That(VpGpuCullSetup.CasterSliceSelectionFor(skinIssue: false, vulkan: false, sliceSelection: true, skin24OnVulkan: false), Is.EqualTo(1f), "Direct3D 12: the global switch");
            Assert.That(VpGpuCullSetup.CasterSliceSelectionFor(skinIssue: true, vulkan: false, sliceSelection: true, skin24OnVulkan: false), Is.EqualTo(1f));
            Assert.That(VpGpuCullSetup.CasterSliceSelectionFor(skinIssue: false, vulkan: true, sliceSelection: true, skin24OnVulkan: true), Is.EqualTo(0f), "Vulkan: the ordinary casters never select");
            Assert.That(VpGpuCullSetup.CasterSliceSelectionFor(skinIssue: true, vulkan: true, sliceSelection: true, skin24OnVulkan: false), Is.EqualTo(0f), "Vulkan: the skin casters only when asked");
            Assert.That(VpGpuCullSetup.CasterSliceSelectionFor(skinIssue: true, vulkan: true, sliceSelection: true, skin24OnVulkan: true), Is.EqualTo(1f));
            Assert.That(VpGpuCullSetup.CasterSliceSelectionFor(skinIssue: true, vulkan: true, sliceSelection: false, skin24OnVulkan: true), Is.EqualTo(0f), "nothing without the global switch");
            Assert.That(VpGpuCullSetup.CasterSliceSelectionFor(skinIssue: false, vulkan: false, sliceSelection: false, skin24OnVulkan: true), Is.EqualTo(0f));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TheOrdinaryDisplaySingleSliceHint_KeepsTheSkinCasterPolicy(bool skinSliceSelection)
        {
            bool before = VpGpuCullSetup.Skin24SliceSelectionOnVulkan;
            try
            {
                VpGpuCullSetup.Skin24SliceSelectionOnVulkan = skinSliceSelection;
                using (VpSkinDisplay display = Display())
                {
                    Camera camera = Track(new GameObject("skin shadow view")).AddComponent<Camera>();
                    IVpGpuCullTarget target = display;
                    target.SetSingleShadowSlice(camera, true);
                    target.SetSingleShadowSlice(camera, false);
                    Assert.That(VpGpuCullSetup.Skin24SliceSelectionOnVulkan, Is.EqualTo(skinSliceSelection));
                    Assert.That(VpGpuCullSetup.CasterSliceSelectionFor(true, true, true, skinSliceSelection),
                        Is.EqualTo(skinSliceSelection ? 1f : 0f));
                }
            }
            finally
            {
                VpGpuCullSetup.Skin24SliceSelectionOnVulkan = before;
            }
        }

        // ----- the picture ---------------------------------------------------------------------------------------------

        private Color32[] RenderAndRead(Camera camera, RenderTexture target, string name = null)
        {
            var request = new RenderPipeline.StandardRequest { destination = target };
            Assert.That(RenderPipeline.SupportsRenderRequest(camera, request), Is.True);
            RenderPipeline.SubmitRenderRequest(camera, request);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            Texture2D readback = Track(new Texture2D(target.width, target.height, TextureFormat.RGBA32, false));
            readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            readback.Apply(false);
            RenderTexture.active = previous;
            // The pictures, for a look at a failure: written where ZANTETSU_SKIN_TEST_DUMP names a folder.
            string dump = Environment.GetEnvironmentVariable("ZANTETSU_SKIN_TEST_DUMP");
            if (name != null && !string.IsNullOrEmpty(dump) && System.IO.Directory.Exists(dump))
            {
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(dump, name + ".png"), readback.EncodeToPNG());
            }

            return readback.GetPixels32();
        }

        // What a picture holds, for a failure's message: how many pixels are not black, and the brightest channel values.
        private static string Describe(Color32[] pixels)
        {
            int lit = pixels.Count(p => p.r > 8 || p.g > 8 || p.b > 8);
            return "not black " + lit + " of " + pixels.Length + ", max r " + pixels.Max(p => p.r) + " g " + pixels.Max(p => p.g) + " b " + pixels.Max(p => p.b);
        }

        private static int CountGreen(Color32[] pixels) => pixels.Count(p => p.g > p.r + 50);

        private static int CountDiffering(Color32[] first, Color32[] second, int level = 2)
        {
            int differing = 0;
            for (int i = 0; i < first.Length; i++)
            {
                if (Math.Abs(first[i].r - second[i].r) > level || Math.Abs(first[i].g - second[i].g) > level || Math.Abs(first[i].b - second[i].b) > level) differing++;
            }

            return differing;
        }

        // Strongly bent: the four bones turned far apart, so that a vertex of several influences blends normals that
        // disagree, and the blend is much shorter than one (review 1, item 4).
        private static void BentPose(Transform[] bones)
        {
            Quaternion[] turns = { Quaternion.identity, Quaternion.Euler(0f, 0f, 110f), Quaternion.Euler(115f, 0f, 0f), Quaternion.Euler(0f, 120f, 0f) };
            Vector3[] moves = { Vector3.zero, new Vector3(.15f, .05f, 0f), new Vector3(0f, .1f, .15f), new Vector3(-.1f, .2f, .05f) };
            for (int i = 0; i < bones.Length; i++)
            {
                bones[i].localPosition = moves[i];
                bones[i].localRotation = turns[i];
            }
        }

        private Material SkinMaterial(Color colour, bool skin)
        {
            Shader shader = Shader.Find(ShaderName);
            Assert.That(shader, Is.Not.Null);
            Material material = Track(new Material(shader));
            material.SetColor("_BaseColor", colour);
            material.EnableKeyword(VpGpuCullSetup.NoClipKeyword);
            if (skin)
            {
                material.EnableKeyword(VpGpuCullSetup.Keyword);
                material.EnableKeyword("VP_SKIN24");
            }

            return material;
        }

        /// <summary>
        /// The skin draw by Unity's calls -- the 24-byte vertices skinned in the vertex stage by the GPU -- draws the
        /// picture of the CPU bake of the same pose (VpDirectSkinInput, the same model, the same quantized weights)
        /// drawn through the ordinary 16-byte batch: the CPU and the GPU skinning agree to the pixel but for the
        /// rounding at the edges. Under ambient light alone, where the picture shows where the skin is; and strongly
        /// bent under one directional light, where it shows the normal's direction and length: the bake's normal is a
        /// unit vector a vertex (its octahedral encoding), and the skin draw's blended normal must be made one too, or
        /// the faces of vertices whose bones disagree are drawn darker (review 1, item 4).
        /// </summary>
        [TestCase(false, TestName = "TheSkinDraw_IsThePictureOfTheCpuBake_OfTheSamePose")]
        [TestCase(true, TestName = "TheSkinDraw_IsThePictureOfTheCpuBake_StronglyBent_UnderOneLight")]
        public void TheSkinDraw_IsThePictureOfTheCpuBake(bool bentAndLit)
        {
            using var lighting = bentAndLit
                ? VpTestLighting.Of(new Color(.08f, .08f, .08f), new Color(.08f, .08f, .08f), new Color(.08f, .08f, .08f), null)
                : VpTestLighting.BrightFromEverySide();
            if (bentAndLit)
            {
                Light sun = Track(new GameObject("VP Skin Draw Test Sun")).AddComponent<Light>();
                sun.transform.rotation = Quaternion.Euler(40f, -35f, 0f);
                sun.type = LightType.Directional;
                sun.shadows = LightShadows.Hard;   // as the other lit pictures of these shaders (nothing casts here)
                sun.intensity = 1f;
            }

            RenderTexture target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32));
            Camera camera = Track(new GameObject("VP Skin Draw Test Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            // The bent tetrahedron is thin and stands about (0.1, 0.25): the camera is closer in on it.
            camera.transform.position = bentAndLit ? new Vector3(0.1f, 0.25f, -5f) : new Vector3(0.5f, 0.5f, -5f);
            camera.orthographicSize = bentAndLit ? 0.75f : 1.2f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            _root.transform.SetPositionAndRotation(new Vector3(0.1f, -0.2f, 0.3f), Quaternion.Euler(0f, 25f, 0f));
            if (bentAndLit) BentPose(_bones); else Pose(_bones, 2);
            // The rig's own renderer is off, as a skin-drawn individual's is: enabled, Unity draws it too, with no
            // material (plain magenta) at the same pose, over both pictures alike (seen 2026-10-29, review 1).
            _renderer.enabled = false;

            using VpSkinModel model = Model();

            // The CPU bake of this pose, drawn by the ordinary batch.
            Color32[] baked;
            {
                Assert.That(VpDirectSkinInput.TryCreate(_renderer, model, out VpDirectSkinInput input), Is.True);
                using (input)
                using (var storage = new VpCpuGeometryStorage(32, 96, 8, 8, 8, Allocator.Persistent))
                {
                    Assert.That(input.TryAppendTo(storage, out VpStoredGeometry geometry), Is.True);
                    Assert.That(storage.TryAcquireIndexReadLease(geometry.indexRange, out var lease, out NativeArray<uint>.ReadOnly view), Is.True);
                    uint[] indices = view.ToArray();
                    Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True);
                    var bakedMesh = Track(new Mesh());
                    NativeArray<VpRenderVertex>.ReadOnly vertices = storage.Vertices;
                    bakedMesh.vertices = Enumerable.Range(0, 4).Select(i => vertices[geometry.vertexStart + i].position).ToArray();
                    bakedMesh.normals = Enumerable.Range(0, 4).Select(i => vertices[geometry.vertexStart + i].normal).ToArray();
                    bakedMesh.uv = Enumerable.Range(0, 4).Select(i => vertices[geometry.vertexStart + i].uv0).ToArray();
                    bakedMesh.triangles = indices.Select(i => (int)(i - (uint)geometry.vertexStart)).ToArray();
                    using (var pool = new VpCpuGeometryPool(4, 12, Allocator.Persistent))
                    using (var buffers = new VpGpuIndexedGeometryBuffers(4, 12))
                    using (var batch = new VpIndexedIndirectDrawBatch(1, 1))
                    {
                        Assert.That(pool.TryAppend(bakedMesh, out VpGeometryRange range), Is.True);
                        Assert.That(buffers.TryUpload(pool), Is.True);
                        VpIndirectCommand[] commands = { new VpIndirectCommand(range, new Bounds(Vector3.one * 0.5f, Vector3.one * 2f), 1) };
                        Assert.That(batch.TryUpload(commands, new[] { _renderer.transform.localToWorldMatrix }, false), Is.True);
                        batch.RenderForward(SkinMaterial(Color.green, false), new MaterialPropertyBlock(), buffers, 0, 0, 1, camera);
                        baked = RenderAndRead(camera, target, (bentAndLit ? "bent-" : "ambient-") + "baked");
                    }
                }
            }

            // The skin draw of the same pose.
            Color32[] drawn;
            using (VpSkinDisplay display = Display(1, 1))
            {
                Material forward = SkinMaterial(Color.green, true);
                Assert.That(display.TryRegisterModel(model, forward, new Bounds(Vector3.one * 0.5f, Vector3.one * 2f), 1, out int modelId, out string failure), Is.True, failure);
                Assert.That(display.TryAdd(modelId, _renderer.transform, _renderer.bones, null, null, out _), Is.True);
                Assert.That(display.UpdateFrame(false), Is.True);
                var conditions = new VpCullConditions();
                conditions.SetEye(0, camera.worldToCameraMatrix, camera.projectionMatrix);
                conditions.eyeCount = 1;
                Cull(display, camera, conditions);
                Assert.That(display.RenderUnity(0, camera, forward, null), Is.True);
                drawn = RenderAndRead(camera, target, (bentAndLit ? "bent-" : "ambient-") + "drawn");
            }

            int green = CountGreen(baked);
            Assert.That(green, Is.GreaterThan(200), "the bake draws the tetrahedron: baked " + Describe(baked) + "; drawn " + Describe(drawn));
            Assert.That(CountGreen(drawn), Is.EqualTo(green).Within(green / 50 + 2), "the skin draw draws it as large");
            if (bentAndLit)
            {
                // The light shades the faces (the picture is not flat), and the shades agree: the oct-encoded normal of the
                // bake and the float normal of the skin draw differ by the encoding's rounding, a few levels at most.
                List<byte> shades = baked.Where(p => p.g > p.r + 50).Select(p => p.g).ToList();
                Assert.That(shades.Max() - shades.Min(), Is.GreaterThan(40), "the light shades the faces");
                Assert.That(CountDiffering(baked, drawn, 6), Is.LessThanOrEqualTo(green / 25 + 4), "pixels differing in shade between the CPU bake and the GPU skinning");
            }
            else
            {
                Assert.That(CountDiffering(baked, drawn), Is.LessThanOrEqualTo(green / 25 + 4), "pixels differing between the CPU bake and the GPU skinning");
            }
        }
    }
}
