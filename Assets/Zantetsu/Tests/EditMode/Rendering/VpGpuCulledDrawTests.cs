using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// VP Stage 3C at the batch (DESIGN 4.5.7): a <see cref="VpIndexedIndirectDrawBatch"/> made with a
    /// <see cref="VpGpuCullSetup"/> selects its instances in a compute pass and draws with the arguments that pass
    /// wrote. Read back here -- which the product never does -- the lists and the arguments are exactly what the
    /// reference test keeps (<see cref="VpInstanceCulling.KeptForBody"/>, <see cref="VpInstanceCulling.KeptAsCaster"/>):
    /// for boxes touching a plane, far larger than the view, and turned; for either eye alone; with everything kept,
    /// nothing kept, commands of no instance and a batch filled to its capacity; with every command written by every
    /// dispatch and no view written by another's. The image and the shadow are VP Stage 3's, a caster the camera does
    /// not see still casts, and the transfers are the instances' as before with one write for the commands.
    /// </summary>
    public class VpGpuCulledDrawTests
    {
        private const string ShaderName = "Zantetsu/VP Indexed Indirect Unlit";
        private const string ShadowShaderName = "Zantetsu/VP Indexed Indirect Shadow Caster";
        private const int Size = 64;
        private const int ShadowSize = 128;

        // How near the reference's own threshold an instance may be before the two arithmetics are allowed to differ.
        private const float Indecisive = 1e-4f;

        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void DestroyObjects()
        {
            foreach (Object tracked in _objects)
            {
                if (tracked != null)
                {
                    Object.DestroyImmediate(tracked);
                }
            }

            _objects.Clear();
            VpGpuCullSetup.ShadowSliceSelection = true;
        }

        private T Track<T>(T tracked) where T : Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        private static VpGpuCullSetup Setup(int views = 2)
        {
            Assert.That(VpGpuCullSetup.TryCreate(views, out VpGpuCullSetup setup, out string failure), Is.True, failure);
            return setup;
        }

        private Material Material(string shaderName, Color color, bool culled)
        {
            Shader shader = Shader.Find(shaderName);
            Assert.That(shader, Is.Not.Null, shaderName);
            Material material = Track(new Material(shader));
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }

            if (culled)
            {
                Assert.That(shader.keywordSpace.FindKeyword(VpGpuCullSetup.Keyword).isValid, Is.True, shaderName + " has the variant");
                material.EnableKeyword(VpGpuCullSetup.Keyword);
            }

            return material;
        }

        private static void Cull(VpIndexedIndirectDrawBatch batch, int view, VpCullConditions conditions, bool excludeClipped = false)
        {
            var commands = new CommandBuffer { name = "VP GPU Cull Test" };
            try
            {
                batch.IssueCull(commands, view, conditions, excludeClipped);
                Graphics.ExecuteCommandBuffer(commands);
            }
            finally
            {
                commands.Dispose();
            }
        }

        private Camera PerspectiveCamera(Vector3 position, Quaternion rotation, float fieldOfView = 60f, float near = 0.3f, float far = 50f)
        {
            Camera camera = Track(new GameObject("VP GPU Cull Test Eye")).AddComponent<Camera>();
            camera.enabled = false;
            camera.transform.SetPositionAndRotation(position, rotation);
            camera.fieldOfView = fieldOfView;
            camera.aspect = 1f;
            camera.nearClipPlane = near;
            camera.farClipPlane = far;
            return camera;
        }

        private static GraphicsBuffer.IndirectDrawIndexedArgs[] Arguments(GraphicsBuffer buffer, int count)
        {
            var readback = new GraphicsBuffer.IndirectDrawIndexedArgs[count];
            if (count > 0)
            {
                buffer.GetData(readback, 0, 0, count);
            }

            return readback;
        }

        private static uint[] Visible(GraphicsBuffer buffer, int count)
        {
            var readback = new uint[count];
            if (count > 0)
            {
                buffer.GetData(readback, 0, 0, count);
            }

            return readback;
        }

        // The least room, over the planes of the eye or split that keeps it best, between the box and being removed:
        // positive when kept by that much, negative when removed by that much.
        private static float Room(Bounds local, Matrix4x4 m, Vector4[] planes, int first, int count)
        {
            Vector3 centre = m.MultiplyPoint3x4(local.center);
            Vector3 e = local.extents;
            Vector3 ax = new Vector3(m.m00, m.m10, m.m20) * e.x;
            Vector3 ay = new Vector3(m.m01, m.m11, m.m21) * e.y;
            Vector3 az = new Vector3(m.m02, m.m12, m.m22) * e.z;
            float least = float.PositiveInfinity;
            for (int i = first; i < first + count; i++)
            {
                var n = new Vector3(planes[i].x, planes[i].y, planes[i].z);
                float radius = Mathf.Abs(Vector3.Dot(n, ax)) + Mathf.Abs(Vector3.Dot(n, ay)) + Mathf.Abs(Vector3.Dot(n, az));
                least = Mathf.Min(least, Vector3.Dot(n, centre) + planes[i].w + radius + VpInstanceCulling.Margin);
            }

            return least;
        }

        private static bool BodyIsDecisive(Bounds local, Matrix4x4 m, VpCullConditions c)
        {
            for (int eye = 0; eye < c.eyeCount; eye++)
            {
                if (Mathf.Abs(Room(local, m, c.eyePlanes, eye * VpCullConditions.EyePlaneCount, VpCullConditions.EyePlaneCount)) < Indecisive)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool CasterIsDecisive(Bounds local, Matrix4x4 m, VpCullConditions c)
        {
            for (int split = 0; split < c.shadowSplitCount; split++)
            {
                if (Mathf.Abs(Room(local, m, c.shadowPlanes, split * VpCullConditions.SplitPlaneCapacity, (int)c.shadowPlaneCounts[split])) < Indecisive)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Checks one view's lists and arguments against the reference, command by command: the kept instances in
        /// order in the slots of the command's own run, the count, the range and the start. An instance within
        /// <see cref="Indecisive"/> of the reference's threshold may go either way; how many there were is returned.
        /// </summary>
        private static int AssertSelection(
            VpIndexedIndirectDrawBatch batch, int view, VpIndirectCommand[] commands, Matrix4x4[] transforms, VpCullConditions conditions,
            uint multiplier, string label, out int forwardKept, out int shadowKept)
        {
            int total = transforms.Length;
            GraphicsBuffer.IndirectDrawIndexedArgs[] forwardArguments = Arguments(batch.CullForwardArguments(view), commands.Length);
            GraphicsBuffer.IndirectDrawIndexedArgs[] shadowArguments = Arguments(batch.CullShadowArguments(view), commands.Length);
            uint[] forwardVisible = Visible(batch.CullForwardVisible(view), total);
            uint[] shadowVisible = Visible(batch.CullShadowVisible(view), total);
            int undecided = 0;
            forwardKept = 0;
            shadowKept = 0;
            int start = 0;
            for (int c = 0; c < commands.Length; c++)
            {
                VpIndirectCommand command = commands[c];
                string where = label + " command " + c;
                undecided += AssertRun(
                    forwardArguments[c], forwardVisible, command, start, transforms, multiplier, where + " body",
                    (i) => VpInstanceCulling.KeptForBody(command.localBounds, transforms[i], conditions),
                    (i) => BodyIsDecisive(command.localBounds, transforms[i], conditions));
                undecided += AssertRun(
                    shadowArguments[c], shadowVisible, command, start, transforms, 1u, where + " casters",
                    (i) => VpInstanceCulling.KeptAsCaster(command.localBounds, transforms[i], conditions),
                    (i) => CasterIsDecisive(command.localBounds, transforms[i], conditions));
                forwardKept += (int)(forwardArguments[c].instanceCount / multiplier);
                shadowKept += (int)shadowArguments[c].instanceCount;
                start += command.instanceCount;
            }

            return undecided;
        }

        private static int AssertRun(
            GraphicsBuffer.IndirectDrawIndexedArgs arguments, uint[] visible, VpIndirectCommand command, int start, Matrix4x4[] transforms,
            uint multiplier, string where, Func<int, bool> kept, Func<int, bool> decisive)
        {
            Assert.That(arguments.indexCountPerInstance, Is.EqualTo((uint)command.range.indexCount), where + ": index count");
            Assert.That(arguments.startIndex, Is.EqualTo((uint)command.range.indexStart), where + ": start index");
            Assert.That(arguments.baseVertexIndex, Is.Zero, where + ": base vertex");
            Assert.That(arguments.startInstance, Is.EqualTo((uint)start * multiplier), where + ": start instance");
            Assert.That(arguments.instanceCount % multiplier, Is.Zero, where + ": a whole number of kept instances");
            int count = (int)(arguments.instanceCount / multiplier);
            Assert.That(count, Is.LessThanOrEqualTo(command.instanceCount), where + ": no more kept than there are");

            var written = new HashSet<int>();
            for (int k = 0; k < count; k++)
            {
                written.Add((int)visible[start + k]);
            }

            int undecided = 0;
            var expected = new List<uint>();
            for (int i = start; i < start + command.instanceCount; i++)
            {
                bool sure = decisive(i);
                undecided += sure ? 0 : 1;
                if (sure ? kept(i) : written.Contains(i))
                {
                    expected.Add((uint)i);
                }
            }

            var actual = new uint[count];
            Array.Copy(visible, start, actual, 0, count);
            Assert.That(actual, Is.EqualTo(expected.ToArray()), where + ": the kept instances, in order, at the front of the command's own slots");
            return undecided;
        }

        private static Matrix4x4 Place(Vector3 position, Quaternion rotation, Vector3 scale)
        {
            return Matrix4x4.TRS(position, rotation, scale);
        }

        /// <summary>Three splits as boxes of planes around given centres, holding 6, 4 and the full 10 planes.</summary>
        private static void SetBoxSplits(VpCullConditions conditions)
        {
            Vector4 counts = Vector4.zero;
            counts[0] = Box(conditions, 0, new Vector3(0f, 0f, 6f), new Vector3(3f, 3f, 3f), 6);
            counts[1] = Box(conditions, 1, new Vector3(8f, 0f, 14f), new Vector3(4f, 100f, 5f), 4);
            counts[2] = Box(conditions, 2, new Vector3(-10f, 2f, 20f), new Vector3(6f, 4f, 7f), 10);
            conditions.shadowPlaneCounts = counts;
            conditions.shadowSplitCount = 3;
        }

        // Planes of an axis-aligned box (inward normals): x pair, z pair, y pair, then -- up to ten -- four planes far
        // outside it, which remove nothing the box keeps. Returns how many were set.
        private static int Box(VpCullConditions conditions, int split, Vector3 centre, Vector3 half, int planes)
        {
            var all = new[]
            {
                new Plane(Vector3.right, -(centre.x - half.x)), new Plane(Vector3.left, centre.x + half.x),
                new Plane(Vector3.forward, -(centre.z - half.z)), new Plane(Vector3.back, centre.z + half.z),
                new Plane(Vector3.up, -(centre.y - half.y)), new Plane(Vector3.down, centre.y + half.y),
                new Plane(new Vector3(1f, 1f, 0f).normalized, 500f), new Plane(new Vector3(-1f, 1f, 0f).normalized, 500f),
                new Plane(new Vector3(0f, 1f, 1f).normalized, 500f), new Plane(new Vector3(0f, -1f, 1f).normalized, 500f),
            };
            for (int i = 0; i < planes; i++)
            {
                conditions.SetShadowPlane(split, i, all[i]);
            }

            return planes;
        }

        [Test]
        public void TheSelection_IsTheReferences_ForBoxesAtAPlane_LargerThanTheView_AndTurned()
        {
            Camera eye = PerspectiveCamera(Vector3.zero, Quaternion.identity);
            var conditions = new VpCullConditions();
            conditions.SetEye(0, eye.worldToCameraMatrix, eye.projectionMatrix);
            conditions.eyeCount = 1;
            SetBoxSplits(conditions);

            var unit = new Bounds(new Vector3(0.1f, -0.05f, 0.02f), new Vector3(1f, 0.6f, 0.4f));
            var transforms = new List<Matrix4x4>();
            var commands = new List<VpIndirectCommand>();

            // The left plane of a 60 degree, square view: inward normal (cos 30, 0, sin 30) through the origin.
            var leftNormal = new Vector3(Mathf.Cos(Mathf.PI / 6f), 0f, Mathf.Sin(Mathf.PI / 6f));
            var cube = new Bounds(Vector3.zero, Vector3.one * 2f);
            float reach = Mathf.Abs(leftNormal.x) + Mathf.Abs(leftNormal.y) + Mathf.Abs(leftNormal.z);
            Vector3 onPlane = new Vector3(-10f * Mathf.Tan(Mathf.PI / 6f), 0f, 10f);

            // Hand-made, one command each: touching the left plane from outside (kept), outside it by a centimetre
            // (removed), inside by a centimetre (kept); a box holding the whole view (kept); behind the eye (removed);
            // past the far plane (removed); a long thin box turned so that the axis-aligned bounds around it reach
            // into the view while the box itself does not (removed), and the same box turned into the view (kept).
            transforms.Add(Matrix4x4.Translate(onPlane - leftNormal * reach));
            transforms.Add(Matrix4x4.Translate(onPlane - leftNormal * (reach + 0.01f)));
            transforms.Add(Matrix4x4.Translate(onPlane - leftNormal * (reach - 0.01f)));
            transforms.Add(Place(new Vector3(0f, 0f, 20f), Quaternion.identity, Vector3.one * 200f));
            transforms.Add(Matrix4x4.Translate(new Vector3(0f, 0f, -5f)));
            transforms.Add(Matrix4x4.Translate(new Vector3(0f, 0f, 60f)));
            var thin = new Bounds(Vector3.zero, new Vector3(12f, 0.2f, 0.2f));
            for (int i = 0; i < 6; i++)
            {
                commands.Add(new VpIndirectCommand(new VpGeometryRange(i * 3, 3, 100 + i * 6, 6), cube, 1));
            }

            // The thin box lies along the left plane's direction (in the plane's own direction, in xz), one metre outside.
            Vector3 alongPlane = Vector3.Cross(Vector3.up, leftNormal).normalized;
            Quaternion along = Quaternion.FromToRotation(Vector3.right, alongPlane);
            transforms.Add(Place(onPlane - leftNormal * 1f, along, Vector3.one));
            transforms.Add(Place(onPlane + leftNormal * 1f, along, Vector3.one));
            commands.Add(new VpIndirectCommand(new VpGeometryRange(50, 4, 300, 6), thin, 2));

            // Then many commands of 0, 1, 2, 5 and 17 instances scattered around and through the view and the splits,
            // turned and scaled, enough commands to span more than one thread group.
            var random = new System.Random(20261006);
            int[] counts = { 0, 1, 2, 5, 17 };
            for (int c = 0; c < 150; c++)
            {
                int count = counts[c % counts.Length];
                commands.Add(new VpIndirectCommand(new VpGeometryRange(c, 3 + c % 4, 1000 + c * 7, 3 + 3 * (c % 3)), unit, count));
                for (int i = 0; i < count; i++)
                {
                    var position = new Vector3(Next(random, -30f, 30f), Next(random, -20f, 20f), Next(random, -15f, 60f));
                    Quaternion rotation = Quaternion.Euler(Next(random, 0f, 360f), Next(random, 0f, 360f), Next(random, 0f, 360f));
                    float scale = i % 7 == 0 ? Next(random, 5f, 25f) : Next(random, 0.2f, 3f);
                    transforms.Add(Place(position, rotation, new Vector3(scale, scale * Next(random, 0.3f, 2f), scale)));
                }
            }

            VpIndirectCommand[] commandArray = commands.ToArray();
            Matrix4x4[] transformArray = transforms.ToArray();
            using (var batch = new VpIndexedIndirectDrawBatch(commandArray.Length, transformArray.Length, null, Setup()))
            {
                batch.WriteWholeOnce();
                Assert.That(batch.TryUpload(commandArray, transformArray, false), Is.True, "upload");
                Cull(batch, 0, conditions);
                int undecided = AssertSelection(batch, 0, commandArray, transformArray, conditions, 1u, "flat", out int forwardKept, out int shadowKept);
                TestContext.WriteLine("instances " + transformArray.Length + ": body kept " + forwardKept + ", casters kept " + shadowKept + ", undecided " + undecided);
                Assert.That(undecided, Is.LessThan(4), "instances too near the threshold to be a test");
                Assert.That(forwardKept, Is.InRange(50, transformArray.Length - 50), "the data removes and keeps a good part for the body");
                Assert.That(shadowKept, Is.InRange(50, transformArray.Length - 50), "and for the casters");

                // The hand-made ones, by name.
                GraphicsBuffer.IndirectDrawIndexedArgs[] forward = Arguments(batch.CullForwardArguments(0), 7);
                Assert.That(
                    new[] { forward[0].instanceCount, forward[1].instanceCount, forward[2].instanceCount, forward[3].instanceCount, forward[4].instanceCount, forward[5].instanceCount, forward[6].instanceCount },
                    Is.EqualTo(new uint[] { 1, 0, 1, 1, 0, 0, 1 }),
                    "touching, outside, inside, holding the view, behind, past the far plane, the thin pair (one in, one out)");
                Assert.That(Visible(batch.CullForwardVisible(0), 8)[6], Is.EqualTo(7u), "of the thin pair, the one turned into the view");
                Bounds around = VpDirectDraw.WorldBounds(thin, transformArray[6]);
                Assert.That(
                    VpInstanceCulling.MayIntersect(around, new[] { new Plane(leftNormal, 0f) }, 1), Is.True,
                    "the axis-aligned bounds around the removed thin box do reach past the plane: it is the box that is tested");

                // Under Single Pass Instanced only the body's arguments are doubled; the lists are the same.
                Assert.That(batch.TryUpload(commandArray, transformArray, true), Is.True, "upload for stereo");
                Cull(batch, 0, conditions);
                AssertSelection(batch, 0, commandArray, transformArray, conditions, 2u, "doubled", out int doubledForward, out int doubledShadow);
                Assert.That(new[] { doubledForward, doubledShadow }, Is.EqualTo(new[] { forwardKept, shadowKept }), "the same instances kept");
            }
        }

        private static float Next(System.Random random, float from, float to)
        {
            return from + (float)random.NextDouble() * (to - from);
        }

        [Test]
        public void EitherEyeAlone_KeepsAnInstance_AndNeitherRemovesIt()
        {
            // Two eyes turned apart: what lies to the far left is in the left eye only, to the far right in the right only.
            Camera left = PerspectiveCamera(new Vector3(-0.03f, 0f, 0f), Quaternion.Euler(0f, -25f, 0f));
            Camera right = PerspectiveCamera(new Vector3(0.03f, 0f, 0f), Quaternion.Euler(0f, 25f, 0f));
            var both = new VpCullConditions();
            both.SetEye(0, left.worldToCameraMatrix, left.projectionMatrix);
            both.SetEye(1, right.worldToCameraMatrix, right.projectionMatrix);
            both.eyeCount = 2;
            var leftOnly = new VpCullConditions();
            leftOnly.SetEye(0, left.worldToCameraMatrix, left.projectionMatrix);
            leftOnly.eyeCount = 1;

            var cube = new Bounds(Vector3.zero, Vector3.one);
            Matrix4x4[] transforms =
            {
                Matrix4x4.Translate(new Vector3(-9f, 0f, 9f)),   // the left eye's alone
                Matrix4x4.Translate(new Vector3(9f, 0f, 9f)),    // the right eye's alone
                Matrix4x4.Translate(new Vector3(0f, 0f, 10f)),   // both
                Matrix4x4.Translate(new Vector3(0f, 0f, -10f)),  // neither
            };
            VpIndirectCommand[] commands = { new VpIndirectCommand(new VpGeometryRange(0, 3, 0, 3), cube, 4) };
            using (var batch = new VpIndexedIndirectDrawBatch(1, 4, null, Setup()))
            {
                Assert.That(batch.TryUpload(commands, transforms, true), Is.True);
                Cull(batch, 0, both);
                AssertSelection(batch, 0, commands, transforms, both, 2u, "two eyes", out int kept, out _);
                Assert.That(kept, Is.EqualTo(3), "kept by two eyes");
                Assert.That(Visible(batch.CullForwardVisible(0), 3), Is.EqualTo(new uint[] { 0, 1, 2 }), "each eye's own and the shared one");

                Cull(batch, 1, leftOnly);
                AssertSelection(batch, 1, commands, transforms, leftOnly, 2u, "the left eye", out int leftKept, out _);
                Assert.That(leftKept, Is.EqualTo(2), "kept by the left eye alone");
                Assert.That(Visible(batch.CullForwardVisible(1), 2), Is.EqualTo(new uint[] { 0, 2 }));

                // The other view was not written by this one.
                Assert.That(Arguments(batch.CullForwardArguments(0), 1)[0].instanceCount, Is.EqualTo(6u), "view 0 still holds its own selection");
            }
        }

        [Test]
        public void SeparateClipDraws_MixedCommandOmitsOnlyClippedInstances_ForBodyAndCasters_AndOtherViewsKeepThem()
        {
            var box = new Bounds(Vector3.zero, Vector3.one);
            var commands = new[]
            {
                new VpIndirectCommand(new VpGeometryRange(0, 3, 4, 6), box, 5),
                new VpIndirectCommand(new VpGeometryRange(0, 3, 10, 3), box, 2),
            };
            var transforms = new Matrix4x4[7];
            for (int i = 0; i < transforms.Length; i++) transforms[i] = Matrix4x4.identity;
            VpInstanceClip cut = VpInstanceClip.Keep(new Vector4(1f, 0f, 0f, 0f), 1f);
            var clips = new[] { VpInstanceClip.None, cut, VpInstanceClip.None, cut, VpInstanceClip.None, cut, cut };
            var conditions = new VpCullConditions();
            conditions.KeepEverything();
            using (var batch = new VpIndexedIndirectDrawBatch(2, 7, null, Setup()))
            {
                Assert.That(batch.TryUpload(commands, transforms, clips, true), Is.True);
                Cull(batch, 0, conditions, true);
                var body = Arguments(batch.CullForwardArguments(0), 2);
                var casters = Arguments(batch.CullShadowArguments(0), 2);
                Assert.That(body[0].instanceCount, Is.EqualTo(6u), "three ordinary instances, two physical eyes each");
                Assert.That(casters[0].instanceCount, Is.EqualTo(3u));
                Assert.That(Visible(batch.CullForwardVisible(0), 3), Is.EqualTo(new uint[] { 0, 2, 4 }));
                Assert.That(Visible(batch.CullShadowVisible(0), 3), Is.EqualTo(new uint[] { 0, 2, 4 }));
                Assert.That(body[1].instanceCount, Is.Zero, "an entirely clipped command has no ordinary body");
                Assert.That(casters[1].instanceCount, Is.Zero, "nor ordinary casters");
                Assert.That(body[0].startIndex, Is.EqualTo(4u));
                Assert.That(body[1].startInstance, Is.EqualTo(10u), "command layout and stereo addressing are unchanged");

                Cull(batch, 1, conditions);
                Assert.That(Arguments(batch.CullForwardArguments(1), 2)[0].instanceCount, Is.EqualTo(10u), "Unity or another camera's selection keeps its clips");
                Assert.That(Arguments(batch.CullShadowArguments(1), 2)[1].instanceCount, Is.EqualTo(2u));
                Assert.That(Arguments(batch.CullForwardArguments(0), 2)[0].instanceCount, Is.EqualTo(6u), "the separate view was not overwritten");

                Cull(batch, 0, conditions);
                Assert.That(Arguments(batch.CullForwardArguments(0), 2)[1].instanceCount, Is.EqualTo(4u), "the exclusion switch is explicitly reset on every dispatch");
                for (int i = 0; i < clips.Length; i++) clips[i] = VpInstanceClip.None;
                Assert.That(batch.TryUpload(commands, transforms, clips, true), Is.True);
                Cull(batch, 0, conditions, true);
                Assert.That(Arguments(batch.CullForwardArguments(0), 2)[0].instanceCount, Is.EqualTo(10u), "clips removed by geometry commit return to ordinary draws");
                Assert.That(Arguments(batch.CullShadowArguments(0), 2)[1].instanceCount, Is.EqualTo(2u));
            }
        }

        [Test]
        public void EveryDispatch_WritesEveryCommand_KeptOrNot_UpToTheCapacity_AndLeavesTheOtherViewAlone()
        {
            Camera eye = PerspectiveCamera(Vector3.zero, Quaternion.identity);
            Camera away = PerspectiveCamera(Vector3.zero, Quaternion.Euler(0f, 180f, 0f));
            var everything = new VpCullConditions();
            everything.KeepEverything();
            var nothing = new VpCullConditions();
            nothing.SetEye(0, away.worldToCameraMatrix, away.projectionMatrix);
            nothing.eyeCount = 1;
            nothing.shadowSplitCount = 1;
            nothing.shadowPlaneCounts = new Vector4(Box(nothing, 0, new Vector3(0f, 500f, 0f), Vector3.one, 6), 0f, 0f, 0f);
            var seen = new VpCullConditions();
            seen.SetEye(0, eye.worldToCameraMatrix, eye.projectionMatrix);
            seen.eyeCount = 1;

            // Filled to the capacity: eight commands, sixty-four instances, one command of none among them.
            var cube = new Bounds(Vector3.zero, Vector3.one);
            int[] counts = { 9, 0, 1, 20, 3, 7, 16, 8 };
            var commands = new VpIndirectCommand[8];
            var transforms = new Matrix4x4[64];
            for (int c = 0, i = 0; c < 8; c++)
            {
                commands[c] = new VpIndirectCommand(new VpGeometryRange(c * 4, 4, c * 6, 6), cube, counts[c]);
                for (int k = 0; k < counts[c]; k++, i++)
                {
                    transforms[i] = Matrix4x4.Translate(new Vector3((i % 8) - 3.5f, (i / 8) - 3.5f, 12f));
                }
            }

            using (var batch = new VpIndexedIndirectDrawBatch(8, 64, null, Setup()))
            {
                batch.WriteWholeOnce();
                Assert.That(batch.TryUpload(commands, transforms, false), Is.True);
                Assert.That(new[] { batch.CommandCount, batch.InstanceCount }, Is.EqualTo(new[] { 8, 64 }), "at the capacity");

                Cull(batch, 0, everything);
                AssertSelection(batch, 0, commands, transforms, everything, 1u, "everything", out int allForward, out int allShadow);
                Assert.That(new[] { allForward, allShadow }, Is.EqualTo(new[] { 64, 64 }), "everything kept");
                uint[] identity = Visible(batch.CullForwardVisible(0), 64);
                for (int i = 0; i < 64; i++)
                {
                    Assert.That(identity[i], Is.EqualTo((uint)i), "slot " + i + " of a list that keeps everything");
                }

                // View 1 has had no dispatch: it holds the zeros it was made with, which draw nothing.
                foreach (GraphicsBuffer.IndirectDrawIndexedArgs untouched in Arguments(batch.CullForwardArguments(1), 8))
                {
                    Assert.That(untouched.instanceCount, Is.Zero, "a view never selected for draws nothing");
                }

                // Nothing kept: every count is written again, as zero; nothing of the dispatch before is left in force.
                Cull(batch, 0, nothing);
                AssertSelection(batch, 0, commands, transforms, nothing, 1u, "nothing", out int noForward, out int noShadow);
                Assert.That(new[] { noForward, noShadow }, Is.EqualTo(new[] { 0, 0 }), "nothing kept");

                // And kept again, by a real view.
                Cull(batch, 0, seen);
                AssertSelection(batch, 0, commands, transforms, seen, 1u, "seen", out int seenForward, out int seenShadow);
                Assert.That(new[] { seenForward, seenShadow }, Is.EqualTo(new[] { 64, 64 }), "all of it is in front of the eye; the casters have no split and keep everything");

                // An upload refused for room changes nothing, and the selection goes on with what was uploaded before it.
                var tooMany = new VpIndirectCommand[9];
                for (int c = 0; c < tooMany.Length; c++)
                {
                    tooMany[c] = new VpIndirectCommand(new VpGeometryRange(0, 3, 0, 3), cube, 1);
                }

                long sentBefore = batch.ArgumentSetDataCalls;
                Assert.That(batch.TryUpload(tooMany, new Matrix4x4[9], false), Is.False, "more commands than the capacity");
                Assert.That(batch.ArgumentSetDataCalls, Is.EqualTo(sentBefore), "nothing was sent for the refused upload");
                Cull(batch, 0, nothing);
                Cull(batch, 0, seen);
                AssertSelection(batch, 0, commands, transforms, seen, 1u, "after a refused upload", out int afterRefusal, out _);
                Assert.That(afterRefusal, Is.EqualTo(64), "the earlier commands are selected and drawn as before");

                // Fewer commands: the ones uploaded are written; no draw may reach past them.
                var fewer = new[] { commands[2], commands[1], commands[4] };
                var fewerTransforms = new[] { transforms[0], transforms[1], transforms[2], transforms[3] };
                Assert.That(batch.TryUpload(fewer, fewerTransforms, false), Is.True);
                Cull(batch, 0, everything);
                AssertSelection(batch, 0, fewer, fewerTransforms, everything, 1u, "fewer", out int fewerForward, out _);
                Assert.That(fewerForward, Is.EqualTo(4));
                var properties = new MaterialPropertyBlock();
                using (var buffers = new VpGpuIndexedGeometryBuffers(4, 6))
                {
                    Material forward = Material(ShaderName, Color.green, true);
                    Assert.Throws<ArgumentOutOfRangeException>(() => batch.RenderForward(forward, properties, buffers, 0, 0, 4, null, 0), "commands past the uploaded ones");
                    Assert.Throws<ArgumentOutOfRangeException>(() => batch.RenderForward(forward, properties, buffers, 0, 0, 3, null, 2), "a view the batch does not have");
                }

                // No command: nothing is dispatched, and nothing is drawn.
                long dispatches = batch.CullDispatches;
                Assert.That(batch.TryUpload(new VpIndirectCommand[0], new Matrix4x4[0], false), Is.True);
                Cull(batch, 0, everything);
                Assert.That(batch.CullDispatches, Is.EqualTo(dispatches), "no dispatch for no command");
            }
        }

        [Test]
        public void TheSelection_FollowsAnInstanceMoved_ByARangedUpload_WithTheCommandsSentOnce()
        {
            Camera eye = PerspectiveCamera(Vector3.zero, Quaternion.identity);
            var conditions = new VpCullConditions();
            conditions.SetEye(0, eye.worldToCameraMatrix, eye.projectionMatrix);
            conditions.eyeCount = 1;
            var cube = new Bounds(Vector3.zero, Vector3.one);
            var commands = new NativeArray<VpIndirectCommand>(2, Allocator.Temp);
            var transforms = new NativeArray<Matrix4x4>(4, Allocator.Temp);
            var clips = new NativeArray<VpInstanceClip>(4, Allocator.Temp);
            try
            {
                commands[0] = new VpIndirectCommand(new VpGeometryRange(0, 3, 0, 3), cube, 1);
                commands[1] = new VpIndirectCommand(new VpGeometryRange(3, 3, 3, 3), cube, 3);
                for (int i = 0; i < 4; i++)
                {
                    transforms[i] = Matrix4x4.Translate(new Vector3(i - 1.5f, 0f, 10f));
                    clips[i] = VpInstanceClip.None;
                }

                using (var culled = new VpIndexedIndirectDrawBatch(2, 4, null, Setup()))
                using (var plain = new VpIndexedIndirectDrawBatch(2, 4))
                {
                    Assert.That(culled.TryUploadChanged(commands, 2, transforms, clips, false, true, 0, 4), Is.True);
                    Assert.That(plain.TryUploadChanged(commands, 2, transforms, clips, false, true, 0, 4), Is.True);
                    Assert.That(
                        new[] { culled.ArgumentSetDataCalls, culled.ArgumentTransfers, culled.ArgumentElementsTransferred, plain.ArgumentSetDataCalls, plain.ArgumentTransfers, plain.ArgumentElementsTransferred },
                        Is.EqualTo(new long[] { 1, 1, 2, 2, 1, 2 }), "the commands: one write where VP Stage 3 writes its two argument buffers");
                    Cull(culled, 0, conditions);
                    Assert.That(Arguments(culled.CullForwardArguments(0), 2)[1].instanceCount, Is.EqualTo(3u), "all three in view");

                    // Instance 2 is moved behind the eye, by an upload of that one record and no command.
                    transforms[2] = Matrix4x4.Translate(new Vector3(0f, 0f, -10f));
                    Assert.That(culled.TryUploadChanged(commands, 2, transforms, clips, false, false, 2, 3), Is.True);
                    Assert.That(plain.TryUploadChanged(commands, 2, transforms, clips, false, false, 2, 3), Is.True);
                    Assert.That(
                        new[] { culled.ArgumentSetDataCalls, culled.InstanceSetDataCalls, culled.InstanceElementsTransferred },
                        Is.EqualTo(new[] { plain.ArgumentSetDataCalls - 1, plain.InstanceSetDataCalls, plain.InstanceElementsTransferred }),
                        "no command sent again; the instances' transfers are VP Stage 3's");
                    Assert.That(culled.WorldBounds, Is.EqualTo(plain.WorldBounds), "the same draw bounds");
                    Cull(culled, 0, conditions);
                    Assert.That(Arguments(culled.CullForwardArguments(0), 2)[1].instanceCount, Is.EqualTo(2u), "the moved one is removed");
                    Assert.That(Visible(culled.CullForwardVisible(0), 3), Is.EqualTo(new uint[] { 0, 1, 3 }), "and the ones kept close up at the front");

                    // And back into view.
                    transforms[2] = Matrix4x4.Translate(new Vector3(0.5f, 0f, 10f));
                    Assert.That(culled.TryUploadChanged(commands, 2, transforms, clips, false, false, 2, 3), Is.True);
                    Cull(culled, 0, conditions);
                    Assert.That(Visible(culled.CullForwardVisible(0), 4), Is.EqualTo(new uint[] { 0, 1, 2, 3 }), "kept again when it comes back");
                }
            }
            finally
            {
                commands.Dispose();
                transforms.Dispose();
                clips.Dispose();
            }
        }

        [Test]
        public void ABatchThatSelects_DrawsOnlyForAView_AndOneThatDoesNot_HasNoSelection()
        {
            var cube = new Bounds(Vector3.zero, Vector3.one);
            VpIndirectCommand[] commands = { new VpIndirectCommand(new VpGeometryRange(0, 3, 0, 3), cube, 1) };
            Matrix4x4[] transforms = { Matrix4x4.identity };
            var conditions = new VpCullConditions();
            var properties = new MaterialPropertyBlock();
            Material forward = Material(ShaderName, Color.green, true);
            Material shadow = Material(ShadowShaderName, Color.green, true);
            var commandBuffer = new CommandBuffer();
            using (var buffers = new VpGpuIndexedGeometryBuffers(4, 6))
            {
                var culled = new VpIndexedIndirectDrawBatch(1, 1, null, Setup(3));
                Assert.That(new object[] { culled.CullsOnGpu, culled.CullViewCapacity }, Is.EqualTo(new object[] { true, 3 }));
                Assert.That(culled.CullViewBytes, Is.EqualTo(3L * 2L * (20L + 4L)), "three views, each two lists and two argument buffers");
                Assert.That(culled.TryUpload(commands, transforms, false), Is.True);
                Assert.Throws<InvalidOperationException>(() => culled.RenderForward(forward, properties, buffers, 0, 0, 1, null));
                Assert.Throws<InvalidOperationException>(() => culled.RenderShadows(shadow, properties, buffers, 0, 0, 1, null));
                Assert.Throws<InvalidOperationException>(() => { GraphicsBuffer unused = culled.ForwardArgumentBuffer; });
                Assert.Throws<ArgumentOutOfRangeException>(() => culled.IssueCull(commandBuffer, 3, conditions));
                Assert.Throws<ArgumentNullException>(() => culled.IssueCull(null, 0, conditions));
                Assert.Throws<ArgumentNullException>(() => culled.IssueCull(commandBuffer, 0, null));
                culled.Dispose();
                culled.Dispose();
                Assert.Throws<ObjectDisposedException>(() => culled.IssueCull(commandBuffer, 0, conditions));
                Assert.Throws<ObjectDisposedException>(() => culled.RenderForward(forward, properties, buffers, 0, 0, 1, null, 0));

                using (var plain = new VpIndexedIndirectDrawBatch(1, 1))
                {
                    Assert.That(new object[] { plain.CullsOnGpu, plain.CullViewCapacity, plain.CullViewBytes }, Is.EqualTo(new object[] { false, 0, 0L }));
                    Assert.That(plain.TryUpload(commands, transforms, false), Is.True);
                    Assert.Throws<InvalidOperationException>(() => plain.IssueCull(commandBuffer, 0, conditions));
                    Assert.Throws<InvalidOperationException>(() => plain.RenderForward(forward, properties, buffers, 0, 0, 1, null, 0));
                    plain.WriteCullArgumentsZero();
                }
            }

            commandBuffer.Dispose();
            Assert.That(VpGpuCullSetup.TryCreate(0, out _, out string failure), Is.False);
            Assert.That(failure, Does.Contain("no view"));

            // The default is the GPU selection (D-198); VP Stage 3 is asked for, and wins over the former opt-in.
            VpGpuCullSetup.Read(new[] { "player.exe", "-zantetsuCityWalk" }, out bool requested, out bool slices);
            Assert.That(new[] { requested, slices }, Is.EqualTo(new[] { true, true }), "a Player's ordinary arguments: the GPU selection");
            VpGpuCullSetup.Read(new[] { "player.exe" }, out requested, out slices);
            Assert.That(new[] { requested, slices }, Is.EqualTo(new[] { true, true }), "no argument of its own");
            VpGpuCullSetup.Read(null, out requested, out slices);
            Assert.That(new[] { requested, slices }, Is.EqualTo(new[] { true, true }), "no arguments at all");
            VpGpuCullSetup.Read(new[] { "player.exe", VpGpuCullSetup.Vp3Argument }, out requested, out slices);
            Assert.That(new[] { requested, slices }, Is.EqualTo(new[] { false, true }), "VP Stage 3 asked for");
            VpGpuCullSetup.Read(new[] { "player.exe", VpGpuCullSetup.Argument }, out requested, out slices);
            Assert.That(new[] { requested, slices }, Is.EqualTo(new[] { true, true }), "the former opt-in: still accepted, changes nothing");
            VpGpuCullSetup.Read(new[] { "player.exe", VpGpuCullSetup.Argument, VpGpuCullSetup.Vp3Argument }, out requested, out slices);
            Assert.That(new[] { requested, slices }, Is.EqualTo(new[] { false, true }), "both: VP Stage 3");
            VpGpuCullSetup.Read(new[] { "player.exe", VpGpuCullSetup.NoSliceSelectionArgument }, out requested, out slices);
            Assert.That(new[] { requested, slices }, Is.EqualTo(new[] { true, false }), "the default without the slice selection");
            Assert.That(VpGpuCullSetup.Vp3Argument, Is.EqualTo("-zantetsuVp3"), "the argument a launcher writes");
            Assert.That(VpGpuCullSetup.Requested, Is.True, "this process, started with no argument for it, makes its worlds with the GPU selection");
        }

        // ----- images ------------------------------------------------------------------------------------------------

        private Color32[] RenderAndRead(Camera camera, RenderTexture target)
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
            return readback.GetPixels32();
        }

        private static int CountDiffering(Color32[] first, Color32[] second)
        {
            int differing = 0;
            for (int i = 0; i < first.Length; i++)
            {
                if (Math.Abs(first[i].r - second[i].r) > 2 || Math.Abs(first[i].g - second[i].g) > 2 || Math.Abs(first[i].b - second[i].b) > 2)
                {
                    differing++;
                }
            }

            return differing;
        }

        private static int CountGreen(Color32[] pixels)
        {
            int green = 0;
            foreach (Color32 pixel in pixels)
            {
                green += pixel.g > pixel.r + 50 ? 1 : 0;
            }

            return green;
        }

        private static int CountDarker(Color32[] lit, Color32[] with)
        {
            int darker = 0;
            for (int i = 0; i < lit.Length; i++)
            {
                bool marker = with[i].g > with[i].r + 50;
                if (!marker && with[i].r + with[i].g + with[i].b < (lit[i].r + lit[i].g + lit[i].b) * 0.8f)
                {
                    darker++;
                }
            }

            return darker;
        }

        private void InEmptyScene(Action body)
        {
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                body();
            }
            finally
            {
                DestroyObjects();
                if (previousSetup != null && previousSetup.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                }
            }
        }

        private enum Route
        {
            Vp3,
            Culled,
            CulledWithoutSliceSelection,

            // The clipped instances drawn apart (DESIGN 4.5.9): the selection leaves them out and is drawn by the
            // variant with no clip; the clip list is drawn by the variant that reads no selection.
            CulledClipApart,
        }

        private int LastClipEntries { get; set; }
        private long LastClipUploads { get; set; }
        private bool LastClipBuffersMade { get; set; }

        // Of the clip list's own selection in the last picture: the entries kept for the body and as casters, the
        // instance counts read back from the view's entries, and the bytes the selection wrote.
        private int LastClipBodyKept { get; set; }
        private int LastClipCastersKept { get; set; }
        private int[] LastClipBodyCounts { get; set; }
        private int[] LastClipCasterCounts { get; set; }
        private int LastClipSelectionBytes { get; set; }

        // hiddenFrameThenNoSelection: the frame pictured first, in which the selection kept no entry (the sum of the
        // instance counts read back, and the picture), and the bytes put back when the next frame's draws were registered.
        private int LastHiddenCounts { get; set; }
        private Color32[] LastHiddenPicture { get; set; }
        private int LastClipRestoreBytes { get; set; }

        /// <summary>
        /// A lit ground seen from above, with cubes of one command drawn above it by the chosen route; null cubes is
        /// the ground alone. The culled routes select with the camera's own frustum for the body and keep every caster.
        /// </summary>
        private Color32[] LitGround(
            Matrix4x4[] cubes, Route route, bool selectBody = true, VpInstanceClip[] clips = null, Action<Camera> view = null,
            Action<VpCullConditions> casters = null, float groundScale = 3f, bool hiddenFrameThenNoSelection = false)
        {
            Light light = Track(new GameObject("VP GPU Cull Test Sun")).AddComponent<Light>();
            light.transform.rotation = Quaternion.Euler(35f, 0f, 0f);
            light.type = LightType.Directional;
            light.shadows = LightShadows.Hard;
            light.intensity = 1f;

            RenderTexture target = Track(new RenderTexture(ShadowSize, ShadowSize, 24, RenderTextureFormat.ARGB32));
            Camera camera = Track(new GameObject("VP GPU Cull Test Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            camera.transform.SetPositionAndRotation(new Vector3(0f, 10f, 0f), Quaternion.Euler(90f, 0f, 0f));
            camera.orthographicSize = 4f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            view?.Invoke(camera);

            GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Plane));
            Object.DestroyImmediate(ground.GetComponent<Collider>());
            ground.transform.localScale = new Vector3(groundScale, 1f, groundScale);
            MeshRenderer groundRenderer = ground.GetComponent<MeshRenderer>();
            groundRenderer.receiveShadows = true;
            groundRenderer.shadowCastingMode = ShadowCastingMode.Off;
            if (cubes == null)
            {
                return RenderAndRead(camera, target);
            }

            bool culled = route != Route.Vp3;
            bool apart = route == Route.CulledClipApart;
            VpGpuCullSetup.ShadowSliceSelection = route != Route.CulledWithoutSliceSelection;
            Mesh mesh = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            int indexCount = (int)mesh.GetIndexCount(0);
            Material forward = Material(ShaderName, Color.green, culled);
            Material shadow = Material(ShadowShaderName, Color.green, culled);
            var properties = new MaterialPropertyBlock();
            using (var pool = new VpCpuGeometryPool(mesh.vertexCount, indexCount, Allocator.Persistent))
            using (var buffers = new VpGpuIndexedGeometryBuffers(mesh.vertexCount, indexCount))
            using (var batch = new VpIndexedIndirectDrawBatch(1, cubes.Length, null, culled ? Setup() : null))
            {
                Assert.That(pool.TryAppend(mesh, out VpGeometryRange range), Is.True);
                Assert.That(buffers.TryUpload(pool), Is.True);
                Assert.That(batch.TryUpload(new[] { new VpIndirectCommand(range, mesh.bounds, cubes.Length) }, cubes, clips, false), Is.True);
                if (culled)
                {
                    var conditions = new VpCullConditions();
                    conditions.KeepEverything();
                    if (selectBody)
                    {
                        conditions.SetEye(0, camera.worldToCameraMatrix, camera.projectionMatrix);
                        conditions.eyeCount = 1;
                    }

                    casters?.Invoke(conditions);
                    var entries = new List<VpIndexedIndirectDrawBatch.ClipEntry>();
                    if (apart)
                    {
                        // As the display does it: the selection's own draws have no clip at all, every instance with
                        // a plane is left out of the selection and given as an entry of the clip list.
                        forward.EnableKeyword(VpGpuCullSetup.NoClipKeyword);
                        shadow.EnableKeyword(VpGpuCullSetup.NoClipKeyword);
                        for (int i = 0; clips != null && i < clips.Length; i++)
                        {
                            if (clips[i].PlaneCount > 0) entries.Add(new VpIndexedIndirectDrawBatch.ClipEntry(range, i, mesh.bounds));
                        }
                    }

                    Cull(batch, 1, conditions, apart && entries.Count > 0);
                    batch.RenderForward(forward, properties, buffers, 0, 0, 1, camera, 1);
                    batch.RenderShadows(shadow, properties, buffers, 0, 0, 1, camera, 1);
                    if (apart && entries.Count > 0)
                    {
                        // As the display does it: the list's draws are registered for every entry, by the variant
                        // that reads no selection (the casters' with the per-slice judgement on); then the list is
                        // judged by the conditions the selection was issued with, and the entries not kept are
                        // written a count of zero through a command buffer.
                        batch.UploadClipEntries(entries.ToArray(), entries.Count);
                        Bounds listBounds = batch.WorldBounds;
                        Material clipForward = Material(ShaderName, Color.green, false);
                        Material clipShadow = Material(ShadowShaderName, Color.green, false);
                        clipShadow.EnableKeyword(VpGpuCullSetup.ClipListSliceKeyword);
                        batch.RestoreClipEntries(1);
                        batch.RenderClipForward(clipForward, properties, buffers, 0, 0, entries.Count, camera, 1, listBounds);
                        batch.RenderClipShadows(clipShadow, properties, buffers, 0, 0, entries.Count, camera, 1, listBounds);
                        var selection = new VpClipSelection();
                        selection.Begin(entries.Count);
                        if (hiddenFrameThenNoSelection)
                        {
                            // A frame whose selection keeps no entry at all -- an eye that sees none of them, a split
                            // that holds none -- pictured; then the next frame's draws registered with NO selection
                            // issued for them: the entries are put back whole when the draws are registered.
                            var nothing = new VpCullConditions();
                            nothing.SetEye(0, Matrix4x4.Translate(new Vector3(0f, 0f, 900f)), Matrix4x4.Perspective(20f, 1f, 0.1f, 1f));
                            nothing.eyeCount = 1;
                            nothing.shadowPlaneCounts = new Vector4(Box(nothing, 0, new Vector3(0f, 900f, 0f), Vector3.one, 6), 0f, 0f, 0f);
                            nothing.shadowSplitCount = 1;
                            for (int k = 0; k < entries.Count; k++)
                            {
                                selection.Set(k, mesh.bounds, cubes[entries[k].instance], nothing);
                            }
                        }
                        else
                        {
                            for (int k = 0; k < entries.Count; k++)
                            {
                                selection.Set(k, mesh.bounds, cubes[entries[k].instance], conditions);
                            }
                        }

                        selection.End();
                        var write = new CommandBuffer { name = "VP Clip Selection Test" };
                        try
                        {
                            LastClipSelectionBytes = batch.WriteClipSelection(write, 1, selection);
                            Graphics.ExecuteCommandBuffer(write);
                        }
                        finally
                        {
                            write.Dispose();
                        }

                        LastClipBodyKept = selection.BodyKeptCount;
                        LastClipCastersKept = selection.CasterKeptCount;
                        if (hiddenFrameThenNoSelection)
                        {
                            GraphicsBuffer.IndirectDrawIndexedArgs[] hiddenBodies = Arguments(batch.ClipForwardArgumentsForTest(1), entries.Count);
                            GraphicsBuffer.IndirectDrawIndexedArgs[] hiddenCasters = Arguments(batch.ClipShadowArgumentsForTest(1), entries.Count);
                            LastHiddenCounts = 0;
                            for (int k = 0; k < entries.Count; k++)
                            {
                                LastHiddenCounts += (int)hiddenBodies[k].instanceCount + (int)hiddenCasters[k].instanceCount;
                            }

                            LastHiddenPicture = RenderAndRead(camera, target);
                            Cull(batch, 1, conditions, true);
                            batch.RenderForward(forward, properties, buffers, 0, 0, 1, camera, 1);
                            batch.RenderShadows(shadow, properties, buffers, 0, 0, 1, camera, 1);
                            LastClipRestoreBytes = batch.RestoreClipEntries(1);
                            batch.RenderClipForward(clipForward, properties, buffers, 0, 0, entries.Count, camera, 1, listBounds);
                            batch.RenderClipShadows(clipShadow, properties, buffers, 0, 0, entries.Count, camera, 1, listBounds);
                        }
                        GraphicsBuffer.IndirectDrawIndexedArgs[] bodyEntries = Arguments(batch.ClipForwardArgumentsForTest(1), entries.Count);
                        GraphicsBuffer.IndirectDrawIndexedArgs[] casterEntries = Arguments(batch.ClipShadowArgumentsForTest(1), entries.Count);
                        LastClipBodyCounts = new int[entries.Count];
                        LastClipCasterCounts = new int[entries.Count];
                        for (int k = 0; k < entries.Count; k++)
                        {
                            LastClipBodyCounts[k] = (int)bodyEntries[k].instanceCount;
                            LastClipCasterCounts[k] = (int)casterEntries[k].instanceCount;
                        }
                    }

                    LastClipEntries = batch.ClipEntryCount;
                    LastClipUploads = batch.ClipArgumentUploads;
                    LastClipBuffersMade = batch.ClipForwardArgumentsForTest(1) != null;
                    LastBodyKept = (int)Arguments(batch.CullForwardArguments(1), 1)[0].instanceCount;
                    LastCastersKept = (int)Arguments(batch.CullShadowArguments(1), 1)[0].instanceCount;
                    LastBodyList = Visible(batch.CullForwardVisible(1), LastBodyKept);
                    LastCasterList = Visible(batch.CullShadowVisible(1), LastCastersKept);
                }
                else
                {
                    batch.RenderForward(forward, properties, buffers, 0, 0, 1, camera);
                    batch.RenderShadows(shadow, properties, buffers, 0, 0, 1, camera);
                }

                return RenderAndRead(camera, target);
            }
        }

        private int LastBodyKept { get; set; }
        private int LastCastersKept { get; set; }
        private uint[] LastBodyList { get; set; }
        private uint[] LastCasterList { get; set; }

        // Eight half-spaces around a point, in the ground's plane, each keeping the inside: an octagon of the given
        // axis-aligned and diagonal reach. Every one of the eight binds.
        private static VpInstanceClip Octagon(Vector3 centre, float axis, float diagonal)
        {
            var halfSpaces = new VpClipHalfSpace[VpInstanceClip.PlaneCapacity];
            for (int i = 0; i < halfSpaces.Length; i++)
            {
                float angle = i * Mathf.PI / 4f;
                var normal = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle));
                float distance = i % 2 == 0 ? axis : diagonal / Mathf.Sqrt(2f);
                halfSpaces[i] = new VpClipHalfSpace(new Vector4(normal.x, 0f, normal.z, -(distance + Vector3.Dot(normal, centre))), -1f);
            }

            Assert.That(VpInstanceClip.TryKeep(halfSpaces, out VpInstanceClip clip), Is.True, "the plane set is accepted");
            return clip;
        }

        /// <summary>
        /// **The clipped instances drawn apart are the same picture, and each instance is drawn once** (DESIGN 4.5.9).
        /// One command of four slabs -- one with no plane, one with one plane, one with eight, and one clipped far
        /// outside the view -- by VP Stage 3 and by the separation: the selection keeps the one ordinary slab and no
        /// clipped one (body and casters), the clip list holds the three clipped ones, and the two pictures, with
        /// their shadows, are the same. The same with every slab clipped (nothing ordinary) and with none clipped --
        /// where no clip list is made, nothing is sent and nothing more is drawn.
        /// </summary>
        [Test]
        public void ClippedInstancesDrawnApart_AreTheSamePicture_EachInstanceOnce_MixedInOneCommand_AllClipped_AndNoneClipped()
        {
            Matrix4x4 slab = Matrix4x4.Scale(new Vector3(2.2f, 0.4f, 2.2f));
            var centres = new[] { new Vector3(60f, 1.2f, 0f), new Vector3(-2.6f, 1.2f, 1.5f), new Vector3(0f, 1.2f, 1.5f), new Vector3(2.6f, 1.2f, 1.5f) };
            var cubes = new Matrix4x4[centres.Length];
            for (int i = 0; i < centres.Length; i++)
            {
                cubes[i] = Matrix4x4.Translate(centres[i]) * slab;
            }

            VpInstanceClip[] mixed =
            {
                VpInstanceClip.Keep(new Vector4(1f, 0f, 0f, -60f), 1f),
                VpInstanceClip.None,
                Octagon(centres[2], 0.7f, 0.85f),
                VpInstanceClip.Keep(new Vector4(0f, 0f, 1f, -1.5f), 1f),
            };
            VpInstanceClip[] all =
            {
                VpInstanceClip.Keep(new Vector4(1f, 0f, 0f, -60f), 1f),
                VpInstanceClip.Keep(new Vector4(1f, 0f, 0f, 2.6f), 1f),
                Octagon(centres[2], 0.7f, 0.85f),
                VpInstanceClip.Keep(new Vector4(0f, 0f, 1f, -1.5f), 1f),
            };
            VpInstanceClip[] none = { VpInstanceClip.None, VpInstanceClip.None, VpInstanceClip.None, VpInstanceClip.None };
            Action<VpCullConditions> nearCasters = conditions =>
            {
                conditions.shadowPlaneCounts = new Vector4(Box(conditions, 0, new Vector3(0f, 0f, 0f), new Vector3(12f, 50f, 12f), 6), 0f, 0f, 0f);
                conditions.shadowSplitCount = 1;
            };

            InEmptyScene(() =>
            {
                Color32[] unclipped = LitGround(cubes, Route.Vp3, clips: none);
                DestroyObjects();

                // Mixed in one command.
                Color32[] vp3 = LitGround(cubes, Route.Vp3, clips: mixed);
                DestroyObjects();
                Color32[] apart = LitGround(cubes, Route.CulledClipApart, clips: mixed, casters: nearCasters);
                Assert.That(LastBodyList, Is.EqualTo(new uint[] { 1 }), "the selection keeps the one ordinary instance as a body, and no clipped one");
                Assert.That(LastCasterList, Is.EqualTo(new uint[] { 1 }), "and as a caster");
                Assert.That(LastClipEntries, Is.EqualTo(3), "the clip list holds the three clipped ones: with the selection's one, every instance once");
                Assert.That(LastClipUploads, Is.EqualTo(1L), "sent once");
                Assert.That(CountGreen(unclipped) - CountGreen(vp3), Is.GreaterThan(150), "the layout: the planes remove a good part of two slabs");
                Assert.That(CountDiffering(vp3, apart), Is.Zero, "pixels differing between VP Stage 3 and the clipped instances drawn apart");
                DestroyObjects();

                // Every instance clipped: nothing ordinary.
                Color32[] vp3All = LitGround(cubes, Route.Vp3, clips: all);
                DestroyObjects();
                Color32[] apartAll = LitGround(cubes, Route.CulledClipApart, clips: all, casters: nearCasters);
                Assert.That(LastBodyKept, Is.Zero, "nothing is ordinary: the selection keeps no body");
                Assert.That(LastCastersKept, Is.Zero, "and no caster");
                Assert.That(LastClipEntries, Is.EqualTo(4));
                Assert.That(CountDiffering(vp3All, vp3), Is.GreaterThan(100), "the layout: the fourth plane is in the picture");
                Assert.That(CountDiffering(vp3All, apartAll), Is.Zero, "pixels differing with every instance clipped");
                DestroyObjects();

                // None clipped: no clip list at all.
                Color32[] apartNone = LitGround(cubes, Route.CulledClipApart, clips: none, casters: nearCasters);
                Assert.That(LastBodyList, Is.EqualTo(new uint[] { 1, 2, 3 }), "every instance in view is ordinary");
                Assert.That(LastClipEntries, Is.Zero, "no clip list");
                Assert.That(LastClipUploads, Is.Zero, "nothing sent for it");
                Assert.That(LastClipBuffersMade, Is.False, "and no buffer made for it");
                Assert.That(CountDiffering(unclipped, apartNone), Is.Zero, "pixels differing with no instance clipped");
            });
        }

        /// <summary>
        /// **The clip list's entries** (DESIGN 4.5.9): one an instance, the index range of its command, starting at
        /// the instance's own record -- two physical instances from twice the record under Single Pass Instanced for
        /// the body, the one logical instance for the casters -- in Unity's form, and in the plugin's with the entry's
        /// own number first. An empty list sends nothing and keeps the count at zero; a list past the batch's
        /// instances, or naming an instance it does not hold, is refused.
        /// </summary>
        [Test]
        public void TheClipList_HoldsOneEntryAClippedInstance_InUnitysFormAndThePlugins_AndAnEmptyListSendsNothing()
        {
            var box = new Bounds(Vector3.zero, Vector3.one);
            var boxes = new[] { new Bounds(new Vector3(0.5f, 0f, -1f), new Vector3(1f, 2f, 3f)), new Bounds(new Vector3(-2f, 1f, 0f), new Vector3(4f, 1f, 0.5f)) };
            var commands = new[]
            {
                new VpIndirectCommand(new VpGeometryRange(0, 3, 4, 6), box, 5),
                new VpIndirectCommand(new VpGeometryRange(0, 3, 10, 3), box, 2),
            };
            var transforms = new Matrix4x4[7];
            for (int i = 0; i < transforms.Length; i++) transforms[i] = Matrix4x4.identity;
            VpInstanceClip cut = VpInstanceClip.Keep(new Vector4(1f, 0f, 0f, 0f), 1f);
            var clips = new[] { VpInstanceClip.None, cut, VpInstanceClip.None, cut, VpInstanceClip.None, cut, cut };
            var entries = new[]
            {
                new VpIndexedIndirectDrawBatch.ClipEntry(commands[0].range, 1, boxes[0]),
                new VpIndexedIndirectDrawBatch.ClipEntry(commands[0].range, 3, boxes[0]),
                new VpIndexedIndirectDrawBatch.ClipEntry(commands[1].range, 5, boxes[1]),
                new VpIndexedIndirectDrawBatch.ClipEntry(commands[1].range, 6, boxes[1]),
            };

            foreach (bool stereo in new[] { false, true })
            {
                using (var batch = new VpIndexedIndirectDrawBatch(2, 7, null, Setup()))
                {
                    Assert.That(batch.TryUpload(commands, transforms, clips, stereo), Is.True);
                    Assert.That(batch.ClipForwardArgumentsForTest(), Is.Null, "no buffer before a list");
                    batch.UploadClipEntries(null, 0);
                    Assert.That(new long[] { batch.ClipEntryCount, batch.ClipArgumentUploads, batch.ClipArgumentWrites, batch.ClipArgumentBytes }, Is.EqualTo(new long[] { 0, 0, 0, 0 }), "an empty list sends nothing");
                    Assert.That(batch.ClipForwardArgumentsForTest(), Is.Null, "and makes nothing");
                    Assert.That(batch.ClipBoundsForTest, Is.Null);

                    uint generation = batch.ClipArgumentGeneration;
                    batch.UploadClipEntries(entries, 4);
                    Assert.That(new long[] { batch.ClipEntryCount, batch.ClipArgumentUploads }, Is.EqualTo(new long[] { 4, 1 }));
                    Assert.That(batch.ClipArgumentGeneration, Is.EqualTo(generation + 1), "a reader that keeps the native pointer is told of the write");
                    Assert.That(batch.ClipEntriesSinglePassInstanced, Is.EqualTo(stereo));
                    Assert.That(new long[] { batch.ClipArgumentWrites, batch.ClipArgumentBytes }, Is.EqualTo(new long[] { 1, 4 * VpCullCommand.Stride }),
                        "Unity's draw: the list's own write is the entries' bounds; a view's entries are written when it draws");
                    var bounds = new VpCullCommand[4];
                    batch.ClipBoundsForTest.GetData(bounds, 0, 0, 4);
                    Assert.That(new[] { bounds[0].centre, bounds[0].extents, bounds[3].extents }, Is.EqualTo(new[] { boxes[0].center, boxes[0].extents, boxes[1].extents }), "each entry's command bounds, by the entry's number");
                    GraphicsBuffer.IndirectDrawIndexedArgs[] body = Arguments(batch.ClipForwardArgumentsForTest(), 4);
                    GraphicsBuffer.IndirectDrawIndexedArgs[] casters = Arguments(batch.ClipShadowArgumentsForTest(), 4);
                    Assert.That(new long[] { batch.ClipArgumentWrites, batch.ClipArgumentBytes },
                        Is.EqualTo(new long[] { 3, 4 * VpCullCommand.Stride + 2 * 4 * VpGpuCullSetup.UnityArgumentStride }), "the view's two buffers written whole, once");
                    Assert.That(batch.ClipForwardArgumentsForTest(1), Is.Not.SameAs(batch.ClipForwardArgumentsForTest(0)), "a view's entries are its own");
                    uint eyes = stereo ? 2u : 1u;
                    int[] instances = { 1, 3, 5, 6 };
                    for (int k = 0; k < 4; k++)
                    {
                        VpGeometryRange range = k < 2 ? commands[0].range : commands[1].range;
                        Assert.That(new[] { body[k].indexCountPerInstance, body[k].instanceCount, body[k].startIndex, body[k].baseVertexIndex, body[k].startInstance },
                            Is.EqualTo(new[] { (uint)range.indexCount, eyes, (uint)range.indexStart, 0u, (uint)instances[k] * eyes }), "body entry " + k + (stereo ? ", stereo" : ""));
                        Assert.That(new[] { casters[k].indexCountPerInstance, casters[k].instanceCount, casters[k].startIndex, casters[k].baseVertexIndex, casters[k].startInstance },
                            Is.EqualTo(new[] { (uint)range.indexCount, 1u, (uint)range.indexStart, 0u, (uint)instances[k] }), "caster entry " + k);
                    }

                    batch.UploadClipEntries(entries, 0);
                    Assert.That(new long[] { batch.ClipEntryCount, batch.ClipArgumentUploads }, Is.EqualTo(new long[] { 0, 1 }), "emptied: counted as zero, nothing sent");
                    Assert.Throws<ArgumentOutOfRangeException>(() => batch.UploadClipEntries(entries, 5), "more entries than given");
                    Assert.Throws<ArgumentOutOfRangeException>(
                        () => batch.UploadClipEntries(new[] { new VpIndexedIndirectDrawBatch.ClipEntry(commands[0].range, 7) }, 1), "an instance the batch does not hold");
                }
            }

            // The plugin's form: the entry's own number, then the five.
            Assert.That(VpGpuCullSetup.TryCreate(2, true, out VpGpuCullSetup native, out string failure), Is.True, failure);
            using (var batch = new VpIndexedIndirectDrawBatch(2, 7, null, native))
            {
                Assert.That(batch.TryUpload(commands, transforms, clips, true), Is.True);
                batch.UploadClipEntries(entries, 4);
                var words = new uint[4 * 6];
                Assert.That(new long[] { batch.ClipArgumentWrites, batch.ClipArgumentBytes }, Is.EqualTo(new long[] { 2, 2 * 4 * VpGpuCullSetup.NativeArgumentStride }),
                    "the plugin's two argument buffers, written with the list and not again");
                Assert.That(batch.ClipBoundsForTest, Is.Null, "no bounds are sent for the plugin: its runs are chosen on the CPU");
                batch.ClipForwardArgumentsForTest().GetData(words);
                Assert.That(new ArraySegment<uint>(words, 0, 6), Is.EqualTo(new uint[] { 0, 6, 2, 4, 0, 2 }), "entry 0: its number, then indices 6, two eyes, from index 4, base 0, from record 1 twice");
                Assert.That(new ArraySegment<uint>(words, 18, 6), Is.EqualTo(new uint[] { 3, 3, 2, 10, 0, 12 }), "entry 3: its number, then the second command's range, from record 6 twice");
                batch.ClipShadowArgumentsForTest().GetData(words);
                Assert.That(new ArraySegment<uint>(words, 12, 6), Is.EqualTo(new uint[] { 2, 3, 1, 10, 0, 5 }), "caster entry 2: one logical instance, from record 5");
                batch.NativeClipDrawBuffers(out GraphicsBuffer forwardArguments, out GraphicsBuffer shadowArguments, out GraphicsBuffer instanceBuffer, out GraphicsBuffer clipBuffer);
                Assert.That(forwardArguments, Is.SameAs(batch.ClipForwardArgumentsForTest()));
                Assert.That(shadowArguments, Is.SameAs(batch.ClipShadowArgumentsForTest()));
                using (var commandBuffer = new CommandBuffer())
                {
                    var selection = new VpClipSelection();
                    selection.Begin(4);
                    var nothing = new VpCullConditions();
                    nothing.SetEye(0, Matrix4x4.Translate(new Vector3(0f, 0f, 500f)), Matrix4x4.Perspective(30f, 1f, 0.1f, 1f));
                    nothing.eyeCount = 1;
                    for (int k = 0; k < 4; k++) selection.Set(k, boxes[k / 2], Matrix4x4.identity, nothing);
                    selection.End();
                    Assert.That(selection.BodyKeptCount, Is.Zero, "the layout: the eye sees none of them");
                    Assert.That(batch.WriteClipSelection(commandBuffer, 0, selection), Is.Zero, "the plugin's entries are not rewritten by a selection");
                    Assert.That(batch.ClipArgumentWrites, Is.EqualTo(2L));
                }
                Assert.That(instanceBuffer != null && clipBuffer != null, Is.True, "the shared instance and clip buffers: nothing copied for the list");
            }
        }

        /// <summary>
        /// **The clip list's own selection keeps what the GPU selection would, and never less** (DESIGN 4.5.9). Boxes
        /// in and out of the view, turned, at a plane's very edge, seen by one eye of two, outside the view with a
        /// split's volume around them, and in one split's volume, two, or none: the judgement on the CPU keeps every box
        /// the reference of the GPU's test keeps, with <see cref="VpClipSelection.ExtraMargin"/> more on the keeping
        /// side; a slot not valid keeps everything; other conditions (the motion vectors', whose eyes end nearer) are
        /// asked of the same carried boxes.
        /// </summary>
        [Test]
        public void TheClipSelection_KeepsWhatTheSelectionKeeps_AndNeverLess_AtAnEdge_ForOneEyeOfTwo_ForACasterOutOfView_AndBySplit()
        {
            Camera left = PerspectiveCamera(new Vector3(-0.3f, 0f, 0f), Quaternion.Euler(0f, -20f, 0f), 40f, 0.3f, 50f);
            Camera right = PerspectiveCamera(new Vector3(0.3f, 0f, 0f), Quaternion.Euler(0f, 20f, 0f), 40f, 0.3f, 50f);
            var conditions = new VpCullConditions();
            conditions.KeepEverything();
            conditions.SetEye(0, left.worldToCameraMatrix, left.projectionMatrix);
            conditions.SetEye(1, right.worldToCameraMatrix, right.projectionMatrix);
            conditions.eyeCount = 2;
            conditions.shadowPlaneCounts = new Vector4(
                Box(conditions, 0, new Vector3(0f, 0f, 5f), new Vector3(4f, 30f, 4f), 6),
                Box(conditions, 1, new Vector3(0f, 0f, 14f), new Vector3(9f, 30f, 9f), 10), 0f, 0f);
            conditions.shadowSplitCount = 2;

            var local = new Bounds(new Vector3(0.1f, -0.2f, 0.05f), new Vector3(1f, 0.6f, 1.4f));
            var boxes = new List<Matrix4x4>
            {
                Matrix4x4.Translate(new Vector3(0f, 0f, 6f)),                                              // 0: both eyes, split 0
                Matrix4x4.TRS(new Vector3(-6f, 0f, 14f), Quaternion.Euler(20f, 35f, 10f), Vector3.one),     // 1: the left eye only, split 1
                Matrix4x4.TRS(new Vector3(6f, 0f, 14f), Quaternion.Euler(-15f, 50f, 5f), Vector3.one),      // 2: the right eye only, split 1
                Matrix4x4.Translate(new Vector3(0f, 0f, -6f)),                                             // 3: behind both eyes, no split
                Matrix4x4.Translate(new Vector3(0f, 20f, 5f)),                                             // 4: above the view, inside split 0's volume
                Matrix4x4.Translate(new Vector3(0f, 0f, 8.5f)),                                            // 5: in both splits' volumes
                Matrix4x4.Translate(new Vector3(0f, 0f, 60f)),                                             // 6: past the far plane, no split
                Matrix4x4.TRS(new Vector3(0f, 0f, 6f), Quaternion.Euler(0f, 45f, 0f), new Vector3(30f, 0.2f, 30f)), // 7: larger than the view
            };

            // Boxes at the left eye's left plane and at split 0's +x plane: stepped across each in steps finer than
            // the margins, so that some are exactly at the judgement's edge.
            Vector4 edge = conditions.eyePlanes[0];
            var edgeNormal = new Vector3(edge.x, edge.y, edge.z);
            Vector3 onEdge = new Vector3(-0.3f, 0f, 0f) + Quaternion.Euler(0f, -20f, 0f) * Quaternion.Euler(0f, -20f, 0f) * new Vector3(0f, 0f, 10f);
            float reach = Mathf.Abs(edgeNormal.x) * local.extents.x + Mathf.Abs(edgeNormal.y) * local.extents.y + Mathf.Abs(edgeNormal.z) * local.extents.z;
            for (int i = -8; i <= 8; i++)
            {
                Vector3 centre = onEdge - edgeNormal * (reach + i * 0.0004f) - local.center;
                centre -= edgeNormal * (Vector3.Dot(edgeNormal, centre + local.center) + edge.w + reach + i * 0.0004f);
                boxes.Add(Matrix4x4.Translate(centre));
            }

            for (int i = -8; i <= 8; i++)
            {
                boxes.Add(Matrix4x4.Translate(new Vector3(4f + local.extents.x - local.center.x + i * 0.0004f, 0f, 5f - local.center.z)));
            }

            var selection = new VpClipSelection();
            Assert.That(selection.Valid, Is.False);
            Assert.That(new[] { selection.BodyKept(0), selection.CasterKept(3), selection.KeptBy(3, conditions), selection.CasterKeptInSlice(3, Matrix4x4.identity, 0f, true) },
                Is.EqualTo(new[] { true, true, true, true }), "nothing judged: everything is kept");
            selection.Begin(boxes.Count);
            for (int i = 0; i < boxes.Count; i++) selection.Set(i, local, boxes[i], conditions);
            Assert.That(selection.BodyKept(3), Is.True, "not valid until the judgement ends");
            selection.End();
            Assert.That(new[] { selection.Valid, selection.Count == boxes.Count }, Is.EqualTo(new[] { true, true }));

            int body = 0, casters = 0, edgeKeptByMarginAlone = 0;
            for (int i = 0; i < boxes.Count; i++)
            {
                bool referenceBody = VpInstanceCulling.KeptForBody(local, boxes[i], conditions);
                bool referenceCaster = VpInstanceCulling.KeptAsCaster(local, boxes[i], conditions);
                if (referenceBody) Assert.That(selection.BodyKept(i), Is.True, "box " + i + ": the GPU selection's test keeps its body, so the clip list's does");
                if (referenceCaster) Assert.That(selection.CasterKept(i), Is.True, "box " + i + ": the GPU selection's test keeps it as a caster, so the clip list's does");
                if (selection.BodyKept(i)) body++;
                if (selection.CasterKept(i)) casters++;
                if (selection.BodyKept(i) != referenceBody || selection.CasterKept(i) != referenceCaster) edgeKeptByMarginAlone++;
            }

            Assert.That(new[] { selection.BodyKeptCount, selection.CasterKeptCount }, Is.EqualTo(new[] { body, casters }));
            Assert.That(new[] { selection.BodyKept(0), selection.BodyKept(1), selection.BodyKept(2), selection.BodyKept(3), selection.BodyKept(4), selection.BodyKept(6), selection.BodyKept(7) },
                Is.EqualTo(new[] { true, true, true, false, false, false, true }), "in view of both eyes, of the left alone, of the right alone, behind, above, past the far plane, larger than the view");
            Assert.That(VpInstanceCulling.MayIntersect(local, boxes[1], conditions.eyePlanes, VpCullConditions.EyePlaneCount, VpCullConditions.EyePlaneCount), Is.False, "the layout: box 1 is outside the right eye");
            Assert.That(VpInstanceCulling.MayIntersect(local, boxes[2], conditions.eyePlanes, 0, VpCullConditions.EyePlaneCount), Is.False, "the layout: box 2 is outside the left eye");
            Assert.That(new[] { selection.CasterKept(0), selection.CasterKept(1), selection.CasterKept(3), selection.CasterKept(4), selection.CasterKept(5), selection.CasterKept(6) },
                Is.EqualTo(new[] { true, true, false, true, true, false }), "a caster: in split 0, in split 1, in none, out of view inside split 0's volume, in both, in none");
            Assert.That(edgeKeptByMarginAlone, Is.GreaterThan(0).And.LessThan(34), "some boxes at the two edges are kept by the wider margin alone, the others on either side of it are judged as the reference judges them");
            TestContext.Out.WriteLine("boxes " + boxes.Count + ": kept for the body " + body + ", as casters " + casters + "; kept by the wider margin alone " + edgeKeptByMarginAlone);

            // The slices' own judgement: a box projection looking down, 8 wide.
            Matrix4x4 slice = Matrix4x4.Ortho(-4f, 4f, -4f, 4f, 0.1f, 100f) * Matrix4x4.LookAt(new Vector3(0f, 50f, 5f), new Vector3(0f, 0f, 5f), Vector3.forward).inverse;
            Assert.That(selection.CasterKeptInSlice(0, slice, 0f, true), Is.True, "inside the slice");
            Assert.That(selection.CasterKeptInSlice(5, slice, 0f, true), Is.True, "across the slice's edge");
            Assert.That(selection.CasterKeptInSlice(1, slice, 0f, true), Is.False, "wholly outside the slice to its side");
            Assert.That(selection.CasterKeptInSlice(1, slice, 0f, false), Is.True, "with the per-slice judgement off, every caster kept is drawn into every slice");
            Assert.That(selection.CasterKeptInSlice(3, slice, 0f, false), Is.False, "what casts into no split is in no slice");
            Assert.That(selection.CasterKeptInSlice(4, slice, 0f, true), Is.True, "depth is not asked: nearer the light than the slice's near plane still casts");
            Matrix4x4 beside = Matrix4x4.Translate(new Vector3(4f + local.extents.x - local.center.x + 0.3f, 0f, 5f));
            Assert.That(VpInstanceCulling.OutsideSlice(local, beside, slice, 0f), Is.True, "0.3 m past the slice's edge");
            Assert.That(VpInstanceCulling.OutsideSlice(local, beside, slice, 0.35f), Is.False, "but within the reach of the slice's bias");
            for (int i = 0; i < boxes.Count; i++)
            {
                if (VpInstanceCulling.OutsideSlice(local, boxes[i], slice, 0.01f))
                {
                    // What the judgement calls outside rasterises nothing in the slice: every corner is beyond one side.
                    float least = float.MaxValue, most = float.MinValue, leastY = float.MaxValue, mostY = float.MinValue;
                    for (int corner = 0; corner < 8; corner++)
                    {
                        Vector3 p = local.center + Vector3.Scale(local.extents, new Vector3((corner & 1) * 2 - 1, ((corner >> 1) & 1) * 2 - 1, ((corner >> 2) & 1) * 2 - 1));
                        Vector4 clip = slice * (Vector4)(Vector3)boxes[i].MultiplyPoint3x4(p) + slice.GetColumn(3);
                        least = Mathf.Min(least, clip.x); most = Mathf.Max(most, clip.x); leastY = Mathf.Min(leastY, clip.y); mostY = Mathf.Max(mostY, clip.y);
                    }

                    Assert.That(least > 1f || most < -1f || leastY > 1f || mostY < -1f, Is.True, "box " + i + " is called outside the slice and is");
                }
            }

            // The motion vectors' conditions: the same eyes, ending at 10 m.
            var motion = new VpCullConditions();
            motion.KeepEverything();
            for (int eye = 0; eye < 2; eye++)
            {
                Camera camera = eye == 0 ? left : right;
                motion.SetEye(eye, camera.worldToCameraMatrix, camera.projectionMatrix);
                Vector4 row = camera.worldToCameraMatrix.GetRow(2);
                motion.eyePlanes[eye * VpCullConditions.EyePlaneCount + 5] = new Vector4(row.x, row.y, row.z, row.w + 10f);
            }

            motion.eyeCount = 2;
            Assert.That(new[] { selection.KeptBy(0, motion), selection.KeptBy(1, motion), selection.KeptBy(2, motion), selection.KeptBy(3, motion) },
                Is.EqualTo(new[] { true, false, false, false }), "within 10 m of the eyes; 14 m away in view of one; behind");
            selection.Invalidate();
            Assert.That(new[] { selection.BodyKept(3), selection.CasterKept(3), selection.KeptBy(1, motion) }, Is.EqualTo(new[] { true, true, true }), "a new frame's draws: nothing is known of them yet");
        }

        /// <summary>
        /// **Unity's entries of the clip list are a view's own, and a selection's write is that view's alone**
        /// (DESIGN 4.5.9). Two views draw one list. A selection that keeps some bodies and some casters writes a count
        /// of zero into the others of ITS view, and the other view's entries stay whole; the same selection again
        /// writes nothing; entries kept again have their counts back; a list given anew is whole again for both. A
        /// write recorded and not yet executed holds its own data: what is recorded after it does not change it.
        /// </summary>
        [Test]
        public void UnitysClipEntries_AreAViewsOwn_ZeroedAndRestoredByItsSelection_AndARecordedWriteKeepsItsData()
        {
            var box = new Bounds(Vector3.zero, Vector3.one);
            var commands = new[] { new VpIndirectCommand(new VpGeometryRange(0, 3, 0, 6), box, 4) };
            var transforms = new[]
            {
                Matrix4x4.Translate(new Vector3(0f, 0f, 5f)), Matrix4x4.Translate(new Vector3(0f, 0f, -5f)),
                Matrix4x4.Translate(new Vector3(0f, 0f, 9f)), Matrix4x4.Translate(new Vector3(0f, 0f, -9f)),
            };
            VpInstanceClip cut = VpInstanceClip.Keep(new Vector4(1f, 0f, 0f, 50f), 1f);
            var clips = new[] { cut, cut, cut, cut };
            var entries = new VpIndexedIndirectDrawBatch.ClipEntry[4];
            for (int i = 0; i < 4; i++) entries[i] = new VpIndexedIndirectDrawBatch.ClipEntry(commands[0].range, i, box);

            // Forward: the eye at the origin looking along +z sees entries 0 and 2; looking along -z, 1 and 3.
            VpCullConditions Looking(bool ahead, int splitAround)
            {
                var conditions = new VpCullConditions();
                conditions.KeepEverything();
                Camera eye = PerspectiveCamera(Vector3.zero, ahead ? Quaternion.identity : Quaternion.Euler(0f, 180f, 0f));
                conditions.SetEye(0, eye.worldToCameraMatrix, eye.projectionMatrix);
                conditions.eyeCount = 1;
                if (splitAround >= 0)
                {
                    conditions.shadowPlaneCounts = new Vector4(Box(conditions, 0, transforms[splitAround].GetColumn(3), new Vector3(2f, 2f, 2f), 6), 0f, 0f, 0f);
                    conditions.shadowSplitCount = 1;
                }

                return conditions;
            }

            VpClipSelection Judge(VpCullConditions conditions)
            {
                var selection = new VpClipSelection();
                selection.Begin(4);
                for (int i = 0; i < 4; i++) selection.Set(i, box, transforms[i], conditions);
                selection.End();
                return selection;
            }

            int[] Counts(GraphicsBuffer buffer)
            {
                GraphicsBuffer.IndirectDrawIndexedArgs[] read = Arguments(buffer, 4);
                return new[] { (int)read[0].instanceCount, (int)read[1].instanceCount, (int)read[2].instanceCount, (int)read[3].instanceCount };
            }

            int Write(VpIndexedIndirectDrawBatch batch, int view, VpClipSelection selection)
            {
                using (var commandBuffer = new CommandBuffer())
                {
                    int bytes = batch.WriteClipSelection(commandBuffer, view, selection);
                    Graphics.ExecuteCommandBuffer(commandBuffer);
                    return bytes;
                }
            }

            const int entryBytes = 4 * VpGpuCullSetup.UnityArgumentStride;
            foreach (bool stereo in new[] { false, true })
            {
                int eyes = stereo ? 2 : 1;
                using (var batch = new VpIndexedIndirectDrawBatch(1, 4, null, Setup()))
                {
                    Assert.That(batch.TryUpload(commands, transforms, clips, stereo), Is.True);
                    batch.UploadClipEntries(entries, 4);
                    VpClipSelection ahead = Judge(Looking(true, 1)), behind = Judge(Looking(false, -1));
                    Assert.That(new[] { ahead.BodyKept(0), ahead.BodyKept(1), ahead.BodyKept(2), ahead.BodyKept(3) }, Is.EqualTo(new[] { true, false, true, false }), "the layout");
                    Assert.That(new[] { ahead.CasterKept(0), ahead.CasterKept(1), ahead.CasterKept(2), ahead.CasterKept(3) }, Is.EqualTo(new[] { false, true, false, false }), "the layout: the split is around entry 1");

                    Assert.That(Write(batch, 0, ahead), Is.Zero, "no draw of the list was registered for the view: nothing is written");
                    GraphicsBuffer forward0 = batch.ClipForwardArgumentsForTest(0), shadow0 = batch.ClipShadowArgumentsForTest(0);
                    GraphicsBuffer forward1 = batch.ClipForwardArgumentsForTest(1), shadow1 = batch.ClipShadowArgumentsForTest(1);
                    Assert.That(Counts(forward0), Is.EqualTo(new[] { eyes, eyes, eyes, eyes }), "written whole when the view draws the list: every entry drawn");
                    Assert.That(Counts(shadow0), Is.EqualTo(new[] { 1, 1, 1, 1 }));
                    long writes = batch.ClipArgumentWrites, selectionWrites = batch.ClipSelectionWrites;

                    Assert.That(Write(batch, 0, ahead), Is.EqualTo(2 * entryBytes), "the view's two buffers, the list's entries");
                    Assert.That(Counts(forward0), Is.EqualTo(new[] { eyes, 0, eyes, 0 }), "the bodies the view's camera does not see");
                    Assert.That(Counts(shadow0), Is.EqualTo(new[] { 0, 1, 0, 0 }), "the casters no split holds");
                    Assert.That(Counts(forward1), Is.EqualTo(new[] { eyes, eyes, eyes, eyes }), "the other view's entries are not reached");
                    Assert.That(Counts(shadow1), Is.EqualTo(new[] { 1, 1, 1, 1 }));
                    Assert.That(new[] { batch.ClipArgumentWrites - writes, batch.ClipSelectionWrites - selectionWrites }, Is.EqualTo(new long[] { 2, 2 }));
                    Assert.That(Write(batch, 0, ahead), Is.Zero, "the entries hold what is asked: nothing is written");

                    Assert.That(Write(batch, 1, behind), Is.EqualTo(entryBytes), "the other view: its bodies alone change (every caster is kept: no split is given)");
                    Assert.That(Counts(forward1), Is.EqualTo(new[] { 0, eyes, 0, eyes }));
                    Assert.That(Counts(shadow1), Is.EqualTo(new[] { 1, 1, 1, 1 }));
                    Assert.That(Counts(forward0), Is.EqualTo(new[] { eyes, 0, eyes, 0 }), "and the first view's stay as its own selection left them");

                    Assert.That(Write(batch, 0, behind), Is.EqualTo(2 * entryBytes), "the first view's camera turns round");
                    Assert.That(Counts(forward0), Is.EqualTo(new[] { 0, eyes, 0, eyes }), "counts written zero are back where the body is kept again");
                    Assert.That(Counts(shadow0), Is.EqualTo(new[] { 1, 1, 1, 1 }), "and every caster's");

                    // Two writes recorded, then executed in order: the first holds the data it was recorded with.
                    using (var first = new CommandBuffer())
                    using (var second = new CommandBuffer())
                    {
                        Assert.That(batch.WriteClipSelection(first, 0, ahead), Is.EqualTo(2 * entryBytes));
                        Assert.That(batch.WriteClipSelection(second, 0, behind), Is.EqualTo(2 * entryBytes), "recorded after it, before either is executed");
                        Graphics.ExecuteCommandBuffer(first);
                        Assert.That(Counts(forward0), Is.EqualTo(new[] { eyes, 0, eyes, 0 }), "the first write's own data, not what was recorded after it");
                        Assert.That(Counts(shadow0), Is.EqualTo(new[] { 0, 1, 0, 0 }));
                        Graphics.ExecuteCommandBuffer(second);
                        Assert.That(Counts(forward0), Is.EqualTo(new[] { 0, eyes, 0, eyes }));
                        Assert.That(Counts(shadow0), Is.EqualTo(new[] { 1, 1, 1, 1 }));
                    }

                    // A new frame's draws are registered, and no selection has been issued for them (none may be):
                    // every entry written zero is whole again -- the bodies' and the casters' -- and with nothing
                    // left to put back nothing is written.
                    Assert.That(Write(batch, 0, ahead), Is.EqualTo(2 * entryBytes));
                    Assert.That(new[] { Counts(forward0), Counts(shadow0) }, Is.EqualTo(new[] { new[] { eyes, 0, eyes, 0 }, new[] { 0, 1, 0, 0 } }), "the layout: zeros in both of the view's buffers");
                    long restores = batch.ClipRestoreWrites;
                    Assert.That(batch.RestoreClipEntries(0), Is.EqualTo(2 * entryBytes), "both buffers held a zero");
                    Assert.That(new[] { Counts(forward0), Counts(shadow0) }, Is.EqualTo(new[] { new[] { eyes, eyes, eyes, eyes }, new[] { 1, 1, 1, 1 } }), "no selection: every entry is drawn");
                    Assert.That(batch.ClipRestoreWrites - restores, Is.EqualTo(2L));
                    Assert.That(batch.RestoreClipEntries(0), Is.Zero, "nothing holds a zero");
                    Assert.That(batch.RestoreClipEntries(1), Is.EqualTo(entryBytes), "the other view: its bodies alone were written zero");
                    Assert.That(Counts(forward1), Is.EqualTo(new[] { eyes, eyes, eyes, eyes }));
                    Assert.That(Write(batch, 0, ahead), Is.EqualTo(2 * entryBytes), "and a selection that does come writes its zeros after the entries were put back");
                    Assert.That(new[] { Counts(forward0), Counts(shadow0) }, Is.EqualTo(new[] { new[] { eyes, 0, eyes, 0 }, new[] { 0, 1, 0, 0 } }));
                    Assert.That(batch.RestoreClipEntries(0), Is.EqualTo(2 * entryBytes));

                    // A selection of another list (another count) says nothing of these entries.
                    var other = new VpClipSelection();
                    other.Begin(3);
                    other.End();
                    Assert.That(Write(batch, 0, other), Is.Zero);

                    // The list given anew: whole again, for each view when it next draws it.
                    Assert.That(Write(batch, 0, ahead), Is.EqualTo(2 * entryBytes));
                    batch.UploadClipEntries(entries, 4);
                    Assert.That(Write(batch, 0, ahead), Is.Zero, "the view has not drawn the new list: its selection writes nothing");
                    Assert.That(Counts(batch.ClipForwardArgumentsForTest(0)), Is.EqualTo(new[] { eyes, eyes, eyes, eyes }), "the new list, every entry drawn");
                    Assert.That(Write(batch, 0, ahead), Is.EqualTo(2 * entryBytes));
                    Assert.That(Counts(forward0), Is.EqualTo(new[] { eyes, 0, eyes, 0 }));
                }
            }
        }

        /// <summary>
        /// **A clipped instance the camera does not see is not drawn, and still casts the shadow that falls into the
        /// view** (DESIGN 4.5.9), in pictures against VP Stage 3. The four cubes of the case above, every one clipped
        /// (by a plane that removes nothing of it) and so every one in the clip list: two in view; one behind the
        /// view's edge whose shadow the light throws into the view; one far off. The list's selection keeps two
        /// bodies, the entries of the other two are written a count of zero, and the picture -- bodies and shadows --
        /// is VP Stage 3's. With the far cube outside every split's volume its caster entry is zero as well, and the
        /// picture is the same. Then a long row seen along the ground, through every slice of the shadow map.
        /// </summary>
        [Test]
        public void ClippedInstancesOutOfView_AreNotDrawn_AndStillCastTheShadowsThatFallIntoView_AsVpStage3DrawsThem()
        {
            Matrix4x4[] cubes =
            {
                Matrix4x4.Translate(new Vector3(-1.5f, 1.2f, 0f)),
                Matrix4x4.Translate(new Vector3(1.5f, 1.2f, 0f)),
                Matrix4x4.Translate(new Vector3(0f, 2f, -5.2f)),
                Matrix4x4.Translate(new Vector3(-9f, 1f, 9f)),
            };
            VpInstanceClip whole = VpInstanceClip.Keep(new Vector4(1f, 0f, 0f, 500f), 1f);
            VpInstanceClip[] clips = { whole, whole, whole, whole };
            Action<VpCullConditions> near = conditions =>
            {
                conditions.shadowPlaneCounts = new Vector4(Box(conditions, 0, new Vector3(0f, 0f, -1f), new Vector3(6f, 50f, 7f), 6), 0f, 0f, 0f);
                conditions.shadowSplitCount = 1;
            };

            InEmptyScene(() =>
            {
                Color32[] ground = LitGround(null, Route.Vp3);
                DestroyObjects();
                Color32[] vp3 = LitGround(cubes, Route.Vp3, clips: clips);
                DestroyObjects();
                Color32[] apart = LitGround(cubes, Route.CulledClipApart, clips: clips);
                Assert.That(new[] { LastBodyKept, LastCastersKept, LastClipEntries }, Is.EqualTo(new[] { 0, 0, 4 }), "every cube is clipped: the selection keeps none, the list holds the four");
                Assert.That(new[] { LastClipBodyKept, LastClipCastersKept }, Is.EqualTo(new[] { 2, 4 }), "the list's selection: the two in view; every caster (no split is given)");
                Assert.That(LastClipBodyCounts, Is.EqualTo(new[] { 1, 1, 0, 0 }), "the bodies out of view are written a count of zero");
                Assert.That(LastClipCasterCounts, Is.EqualTo(new[] { 1, 1, 1, 1 }));
                Assert.That(LastClipSelectionBytes, Is.EqualTo(4 * VpGpuCullSetup.UnityArgumentStride), "one write: the body entries");
                Assert.That(CountGreen(vp3), Is.GreaterThan(200), "VP Stage 3 draws the cubes in view");
                Assert.That(CountDarker(ground, vp3), Is.GreaterThan(300), "and the shadows");
                Assert.That(CountDiffering(vp3, apart), Is.Zero, "pixels differing between VP Stage 3 and the clip list drawn by its own selection");

                DestroyObjects();
                Color32[] split = LitGround(cubes, Route.CulledClipApart, clips: clips, casters: near);
                Assert.That(new[] { LastClipBodyKept, LastClipCastersKept }, Is.EqualTo(new[] { 2, 3 }), "the far cube is in no split's volume");
                Assert.That(LastClipCasterCounts, Is.EqualTo(new[] { 1, 1, 1, 0 }), "its caster entry is zero; the one behind the view's edge still casts");
                Assert.That(CountDiffering(vp3, split), Is.Zero, "pixels differing with the casters selected by a split's volume");

                // A frame whose selection keeps nothing, then a frame with no selection at all: every entry -- bodies
                // and casters -- was written zero and nothing of the cubes is in the first picture; the entries are
                // whole again when the next frame's draws are registered, and its picture is VP Stage 3's.
                DestroyObjects();
                Color32[] afterHidden = LitGround(cubes, Route.CulledClipApart, clips: clips, hiddenFrameThenNoSelection: true);
                Assert.That(new[] { LastClipBodyKept, LastClipCastersKept, LastHiddenCounts }, Is.EqualTo(new[] { 0, 0, 0 }), "the first frame's selection kept no body and no caster: every entry was written zero");
                Assert.That(CountDiffering(ground, LastHiddenPicture), Is.Zero, "and that frame's picture is the ground alone: no cube, no shadow");
                Assert.That(LastClipRestoreBytes, Is.EqualTo(2 * 4 * VpGpuCullSetup.UnityArgumentStride), "both of the view's buffers are put back when the next frame's draws are registered");
                Assert.That(new[] { LastClipBodyCounts, LastClipCasterCounts }, Is.EqualTo(new[] { new[] { 1, 1, 1, 1 }, new[] { 1, 1, 1, 1 } }), "no selection was issued: every entry is drawn");
                Assert.That(CountDiffering(vp3, afterHidden), Is.Zero, "pixels differing from VP Stage 3 in the frame with no selection: the bodies and the shadows are back");

                // The cube behind the view's edge alone: nothing of it is drawn, and its shadow is in the picture.
                DestroyObjects();
                Color32[] sideAlone = LitGround(new[] { cubes[2] }, Route.CulledClipApart, clips: new[] { whole });
                Assert.That(new[] { LastClipBodyKept, LastClipCastersKept }, Is.EqualTo(new[] { 0, 1 }));
                Assert.That(LastClipBodyCounts, Is.EqualTo(new[] { 0 }));
                Assert.That(CountGreen(sideAlone), Is.Zero, "the body is not drawn");
                Assert.That(CountDarker(ground, sideAlone), Is.GreaterThan(100), "but its shadow falls into the view");
                DestroyObjects();
                Assert.That(CountDiffering(sideAlone, LitGround(new[] { cubes[2] }, Route.Vp3, clips: new[] { whole })), Is.Zero, "as VP Stage 3 draws it");
            });

            // A long row seen along the ground: the shadow map's slices each hold some of the casters.
            var row = new List<Matrix4x4>();
            for (int i = 0; i < 26; i++)
            {
                row.Add(Matrix4x4.Translate(new Vector3(-1.5f, 0.8f, 1f + i * 2.2f)));
                row.Add(Matrix4x4.Translate(new Vector3(2.5f + (i % 3), 1.4f, 2f + i * 2.2f)) * Matrix4x4.Rotate(Quaternion.Euler(0f, 17f * i, 0f)));
            }

            row.Add(Matrix4x4.Translate(new Vector3(-40f, 1f, 10f)));
            row.Add(Matrix4x4.Translate(new Vector3(0.5f, 1f, -30f)));
            Matrix4x4[] rows = row.ToArray();
            var rowClips = new VpInstanceClip[rows.Length];
            for (int i = 0; i < rowClips.Length; i++) rowClips[i] = i % 3 == 0 ? VpInstanceClip.None : whole;
            Action<Camera> along = camera =>
            {
                camera.orthographic = false;
                camera.fieldOfView = 55f;
                camera.transform.position = new Vector3(0.5f, 3f, -4f);
                camera.transform.LookAt(new Vector3(0.5f, 0f, 18f));
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 80f;
            };

            InEmptyScene(() =>
            {
                Color32[] vp3 = LitGround(rows, Route.Vp3, clips: rowClips, view: along, groundScale: 20f);
                DestroyObjects();
                Color32[] apart = LitGround(rows, Route.CulledClipApart, clips: rowClips, view: along, groundScale: 20f);
                int entries = LastClipEntries, bodyKept = LastClipBodyKept;
                DestroyObjects();
                Color32[] together = LitGround(rows, Route.Culled, clips: rowClips, view: along, groundScale: 20f);
                TestContext.Out.WriteLine("row: instances " + rows.Length + ", clip entries " + entries + ", kept for the body " + bodyKept + "; green " + CountGreen(vp3));
                Assert.That(entries, Is.EqualTo(36), "two in three are clipped: mixed with ordinary ones in the one command");
                Assert.That(bodyKept, Is.LessThan(entries).And.GreaterThan(10), "the two set outside the view are not kept; the rows in view are");
                Assert.That(CountGreen(vp3), Is.GreaterThan(500), "VP Stage 3 draws the rows");
                Assert.That(CountDiffering(vp3, apart), Is.Zero, "pixels differing between VP Stage 3 and the separation with its own selection, through every slice");
                Assert.That(CountDiffering(vp3, together), Is.Zero, "and the selection with every instance in it");
            });
        }

        [Test]
        public void TheClipRecord_IsTheKeptInstancesOwn_WithNoPlane_OnePlane_AndEight_InTheBodyAndTheShadow()
        {
            // Large flat slabs, so that what a clip removes is plain from above. The first stands far outside the view
            // and outside the casters' split, so both lists begin one slot ahead of the instances they name: a record
            // read by the slot, not by the instance the slot names, would clip the wrong slab.
            Matrix4x4 slab = Matrix4x4.Scale(new Vector3(2.2f, 0.4f, 2.2f));
            var centres = new[] { new Vector3(60f, 1.2f, 0f), new Vector3(-2.6f, 1.2f, 1.5f), new Vector3(0f, 1.2f, 1.5f), new Vector3(2.6f, 1.2f, 1.5f) };
            var cubes = new Matrix4x4[centres.Length];
            for (int i = 0; i < centres.Length; i++)
            {
                cubes[i] = Matrix4x4.Translate(centres[i]) * slab;
            }

            VpInstanceClip[] clips =
            {
                VpInstanceClip.Keep(new Vector4(1f, 0f, 0f, -60f), 1f),
                VpInstanceClip.None,
                Octagon(centres[2], 0.7f, 0.85f),
                VpInstanceClip.Keep(new Vector4(0f, 0f, 1f, -1.5f), 1f),
            };
            VpInstanceClip[] none = { VpInstanceClip.None, VpInstanceClip.None, VpInstanceClip.None, VpInstanceClip.None };
            Action<VpCullConditions> nearCasters = conditions =>
            {
                conditions.shadowPlaneCounts = new Vector4(Box(conditions, 0, new Vector3(0f, 0f, 0f), new Vector3(12f, 50f, 12f), 6), 0f, 0f, 0f);
                conditions.shadowSplitCount = 1;
            };

            InEmptyScene(() =>
            {
                Color32[] unclipped = LitGround(cubes, Route.Vp3, clips: none);
                DestroyObjects();
                Color32[] vp3 = LitGround(cubes, Route.Vp3, clips: clips);
                DestroyObjects();
                Color32[] culled = LitGround(cubes, Route.Culled, clips: clips, casters: nearCasters);
                Assert.That(LastBodyList, Is.EqualTo(new uint[] { 1, 2, 3 }), "the body's list begins one slot ahead of the instances");
                Assert.That(LastCasterList, Is.EqualTo(new uint[] { 1, 2, 3 }), "and so does the casters'");
                DestroyObjects();
                Color32[] withoutSlices = LitGround(cubes, Route.CulledWithoutSliceSelection, clips: clips, casters: nearCasters);

                Assert.That(CountGreen(unclipped) - CountGreen(vp3), Is.GreaterThan(150), "the eight planes and the one plane remove a good part of two slabs");
                Assert.That(CountDiffering(unclipped, vp3), Is.GreaterThan(250), "and of their shadows");
                Assert.That(CountDiffering(vp3, culled), Is.Zero, "pixels differing between VP Stage 3 and the GPU selection");
                Assert.That(CountDiffering(vp3, withoutSlices), Is.Zero, "pixels differing without the slice selection");
            });
        }

        [Test]
        public void ARowOfCastersAcrossEverySliceOfTheShadowMap_CastsAsVpStage3_WithTheSliceSelectionAndWithout()
        {
            // A row of cubes running away from the camera to past the shadow's range, with a second row beside it,
            // seen along the ground: their shadows fall in every slice of the shadow map and across its boundaries.
            var row = new List<Matrix4x4>();
            for (int i = 0; i < 26; i++)
            {
                row.Add(Matrix4x4.Translate(new Vector3(-1.5f, 0.8f, 1f + i * 2.2f)));
                row.Add(Matrix4x4.Translate(new Vector3(2.5f + (i % 3), 1.4f, 2f + i * 2.2f)) * Matrix4x4.Rotate(Quaternion.Euler(0f, 17f * i, 0f)));
            }

            Matrix4x4[] cubes = row.ToArray();
            Action<Camera> along = camera =>
            {
                camera.orthographic = false;
                camera.fieldOfView = 55f;
                camera.transform.position = new Vector3(0.5f, 3f, -4f);
                camera.transform.LookAt(new Vector3(0.5f, 0f, 18f));
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 80f;
            };

            InEmptyScene(() =>
            {
                Color32[] ground = LitGround(null, Route.Vp3, view: along, groundScale: 20f);
                DestroyObjects();
                Color32[] vp3 = LitGround(cubes, Route.Vp3, view: along, groundScale: 20f);
                DestroyObjects();
                Color32[] culled = LitGround(cubes, Route.Culled, view: along, groundScale: 20f);
                int bodyKept = LastBodyKept;
                DestroyObjects();
                Color32[] withoutSlices = LitGround(cubes, Route.CulledWithoutSliceSelection, view: along, groundScale: 20f);
                TestContext.WriteLine(
                    "cubes " + cubes.Length + ", kept for the body " + bodyKept + "; green pixels " + CountGreen(vp3) + ", shadowed ground pixels "
                    + CountDarker(ground, vp3));
                Assert.That(CountGreen(vp3), Is.GreaterThan(500), "VP Stage 3 draws the rows");
                Assert.That(CountDarker(ground, vp3), Is.GreaterThan(400), "and their shadows along the ground");
                Assert.That(bodyKept, Is.GreaterThan(20), "the selection keeps the rows in view");
                Assert.That(CountDiffering(vp3, culled), Is.Zero, "pixels differing between VP Stage 3 and the GPU selection");
                Assert.That(CountDiffering(vp3, withoutSlices), Is.Zero, "pixels differing without the slice selection");
            });
        }

        [Test]
        public void TheImageAndTheShadow_AreVpStage3s_AndACasterTheCameraDoesNotSee_StillCasts()
        {
            // Two cubes over the ground in view; one behind the view's edge, outside it, whose shadow the light throws
            // into the view; and one far off, of which nothing reaches the view.
            Matrix4x4[] cubes =
            {
                Matrix4x4.Translate(new Vector3(-1.5f, 1.2f, 0f)),
                Matrix4x4.Translate(new Vector3(1.5f, 1.2f, 0f)),
                Matrix4x4.Translate(new Vector3(0f, 2f, -5.2f)),
                Matrix4x4.Translate(new Vector3(-9f, 1f, 9f)),
            };

            InEmptyScene(() =>
            {
                Color32[] ground = LitGround(null, Route.Vp3);
                DestroyObjects();
                Color32[] vp3 = LitGround(cubes, Route.Vp3);
                DestroyObjects();
                Color32[] culled = LitGround(cubes, Route.Culled);
                int bodyKept = LastBodyKept;
                DestroyObjects();
                Color32[] withoutSlices = LitGround(cubes, Route.CulledWithoutSliceSelection);
                DestroyObjects();
                Color32[] everyBody = LitGround(cubes, Route.Culled, false);

                Assert.That(CountGreen(vp3), Is.GreaterThan(200), "VP Stage 3 draws the cubes in view");
                Assert.That(CountDarker(ground, vp3), Is.GreaterThan(300), "and their shadows");
                Assert.That(bodyKept, Is.EqualTo(2), "the selection kept the two cubes in view for the body and removed the two outside it");
                Assert.That(CountDiffering(vp3, culled), Is.Zero, "pixels differing between VP Stage 3 and the GPU selection");
                Assert.That(CountDiffering(vp3, withoutSlices), Is.Zero, "pixels differing without the slice selection");
                Assert.That(CountDiffering(vp3, everyBody), Is.Zero, "pixels differing with every instance kept for the body");

                // The cube behind the view's edge alone: its shadow is in the picture, and only a caster kept although
                // the camera does not see it can have cast it.
                DestroyObjects();
                Color32[] sideAlone = LitGround(new[] { cubes[2] }, Route.Culled);
                Assert.That(LastBodyKept, Is.Zero, "the cube is not in the camera's view");
                Assert.That(CountGreen(sideAlone), Is.Zero, "and is not drawn");
                Assert.That(CountDarker(ground, sideAlone), Is.GreaterThan(100), "but its shadow falls into the view");
                DestroyObjects();
                Assert.That(CountDiffering(sideAlone, LitGround(new[] { cubes[2] }, Route.Vp3)), Is.Zero, "as VP Stage 3 draws it");
            });
        }

        [Test]
        public void TheFlatImage_IsVpStage3s_WithInstancesInAndOutOfTheView()
        {
            RenderTexture target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32));
            Camera camera = Track(new GameObject("VP GPU Cull Test Front Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            camera.transform.position = new Vector3(0f, 0f, -5f);
            camera.orthographicSize = 1f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;

            Vector3[] a = { new Vector3(-0.45f, -0.4f, 0f), new Vector3(-0.25f, 0.4f, 0f), new Vector3(-0.05f, -0.4f, 0f) };
            Vector3[] b = { new Vector3(0.05f, -0.4f, 0f), new Vector3(0.25f, 0.4f, 0f), new Vector3(0.45f, -0.4f, 0f) };
            var cell = new Bounds(Vector3.zero, new Vector3(1f, 1f, 0.1f));

            // A in one cell; B in two cells in view, one cell half out of it, and one far outside.
            Matrix4x4[] transforms =
            {
                Matrix4x4.Translate(new Vector3(-0.5f, 0.5f, 0f)),
                Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0f)),
                Matrix4x4.Translate(new Vector3(40f, 0f, 0f)),
                Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, 0f)),
                Matrix4x4.Translate(new Vector3(1.2f, -0.5f, 0f)),
            };

            Color32[] Draw(bool culled)
            {
                Material forward = Material(ShaderName, Color.green, culled);
                var properties = new MaterialPropertyBlock();
                using (var pool = new VpCpuGeometryPool(6, 6, Allocator.Persistent))
                using (var buffers = new VpGpuIndexedGeometryBuffers(6, 6))
                using (var batch = new VpIndexedIndirectDrawBatch(2, 5, null, culled ? Setup() : null))
                {
                    Assert.That(pool.TryAppend(Triangle(a), out VpGeometryRange rangeA), Is.True);
                    Assert.That(pool.TryAppend(Triangle(b), out VpGeometryRange rangeB), Is.True);
                    Assert.That(buffers.TryUpload(pool), Is.True);
                    VpIndirectCommand[] commands = { new VpIndirectCommand(rangeA, cell, 1), new VpIndirectCommand(rangeB, cell, 4) };
                    Assert.That(batch.TryUpload(commands, transforms, false), Is.True);
                    if (culled)
                    {
                        var conditions = new VpCullConditions();
                        conditions.SetEye(0, camera.worldToCameraMatrix, camera.projectionMatrix);
                        conditions.eyeCount = 1;
                        Cull(batch, 0, conditions);
                        batch.RenderForward(forward, properties, buffers, 0, 0, 2, camera, 0);
                        AssertSelection(batch, 0, commands, transforms, conditions, 1u, "front view", out int kept, out _);
                        Assert.That(kept, Is.EqualTo(4), "the one far outside is removed; the one half out is kept");
                    }
                    else
                    {
                        batch.RenderForward(forward, properties, buffers, 0, 0, 2, camera);
                    }

                    return RenderAndRead(camera, target);
                }
            }

            Color32[] vp3 = Draw(false);
            Color32[] culledImage = Draw(true);
            Assert.That(CountGreen(vp3), Is.GreaterThan(300), "VP Stage 3 draws the triangles");
            Assert.That(CountDiffering(vp3, culledImage), Is.Zero, "pixels differing between VP Stage 3 and the GPU selection");
        }

        /// <summary>
        /// The draw data in slots (DESIGN 5.6), by both routes: commands whose instances begin where each is told --
        /// not where the commands before it end --, a free command slot among them, and instance records between the
        /// regions that no command draws. They draw what the same commands packed draw. A command made to draw nothing
        /// is sent alone and stays removed over later selections, whose input it is; a command taken at the end after
        /// it, with its instance record at the end, is sent as one run of each buffer and draws beside the others,
        /// with nothing of the removed one.
        /// </summary>
        [Test]
        public void CommandSlots_WithOneOfNoInstanceAmongThem_DrawAsTheCommandsPacked_AndACommandRemovedStaysRemoved_WhileOthersAreTakenAtTheEnd()
        {
            RenderTexture target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32));
            Camera camera = Track(new GameObject("VP Draw Slot Test Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            camera.transform.position = new Vector3(0f, 0f, -5f);
            camera.orthographicSize = 1f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;

            Vector3[] a = { new Vector3(-0.45f, -0.4f, 0f), new Vector3(-0.25f, 0.4f, 0f), new Vector3(-0.05f, -0.4f, 0f) };
            Vector3[] b = { new Vector3(0.05f, -0.4f, 0f), new Vector3(0.25f, 0.4f, 0f), new Vector3(0.45f, -0.4f, 0f) };
            var cell = new Bounds(Vector3.zero, new Vector3(1f, 1f, 0.1f));
            Matrix4x4 topLeft = Matrix4x4.Translate(new Vector3(-0.5f, 0.5f, 0f)), topRight = Matrix4x4.Translate(new Vector3(0.5f, 0.5f, 0f));
            Matrix4x4 bottomLeft = Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, 0f)), bottomRight = Matrix4x4.Translate(new Vector3(0.5f, -0.5f, 0f));

            // A record no command draws stands in the middle of the picture: drawn by mistake, it would show.
            Matrix4x4 nobodys = Matrix4x4.identity;

            // The three stages, as slots: one batch throughout, each stage sent as what changed.
            Color32[][] Slots(bool culled)
            {
                var images = new Color32[3][];
                Material forward = Material(ShaderName, Color.green, culled);
                var properties = new MaterialPropertyBlock();
                var commands = new NativeArray<VpIndirectCommand>(5, Allocator.Temp);
                var starts = new NativeArray<int>(5, Allocator.Temp);
                var transforms = new NativeArray<Matrix4x4>(7, Allocator.Temp);
                var clips = new NativeArray<VpInstanceClip>(7, Allocator.Temp);
                var commandRanges = new VpChangedRanges(4, 0);
                int instanceFrom = 0, instanceTo = 0;   // the instance records' one range
                var conditions = new VpCullConditions();
                conditions.SetEye(0, camera.worldToCameraMatrix, camera.projectionMatrix);
                conditions.eyeCount = 1;
                string route = culled ? "the GPU selection" : "VP Stage 3";
                try
                {
                    using (var pool = new VpCpuGeometryPool(6, 6, Allocator.Persistent))
                    using (var buffers = new VpGpuIndexedGeometryBuffers(6, 6))
                    using (var batch = new VpIndexedIndirectDrawBatch(5, 7, null, culled ? Setup() : null))
                    {
                        Assert.That(pool.TryAppend(Triangle(a), out VpGeometryRange rangeA), Is.True);
                        Assert.That(pool.TryAppend(Triangle(b), out VpGeometryRange rangeB), Is.True);
                        Assert.That(buffers.TryUpload(pool), Is.True);
                        for (int i = 0; i < 7; i++)
                        {
                            transforms[i] = nobodys;
                            clips[i] = VpInstanceClip.None;
                        }

                        // Slot 0: A once, its record the fifth. Slot 1: free. Slot 2: B twice, the first two records.
                        // Slot 3: A once, the fourth record. Records 2 and 5 are nobody's.
                        commands[0] = new VpIndirectCommand(rangeA, cell, 1);
                        starts[0] = 4;
                        transforms[4] = topLeft;
                        commands[1] = default;
                        starts[1] = 0;
                        commands[2] = new VpIndirectCommand(rangeB, cell, 2);
                        starts[2] = 0;
                        transforms[0] = topRight;
                        transforms[1] = bottomLeft;
                        commands[3] = new VpIndirectCommand(rangeA, cell, 1);
                        starts[3] = 3;
                        transforms[3] = bottomRight;

                        // The slots and the records in use end at these two; what lies past them is not gone through.
                        int commandEnd = 4, instanceEnd = 6;
                        GraphicsBuffer.IndirectDrawIndexedArgs[] Take(int stage)
                        {
                            Assert.That(
                                batch.TryUploadSlots(commands, starts, commandEnd, transforms, clips, instanceEnd, false, commandRanges, instanceFrom, instanceTo), Is.True,
                                route + ", stage " + stage + ": the slots are taken");
                            commandRanges.Clear();
                            instanceFrom = 0;
                            instanceTo = 0;
                            GraphicsBuffer.IndirectDrawIndexedArgs[] arguments;
                            if (culled)
                            {
                                Cull(batch, 0, conditions);
                                arguments = Arguments(batch.CullForwardArguments(0), commandEnd);
                                batch.RenderForward(forward, properties, buffers, 0, 0, commandEnd, camera, 0);
                            }
                            else
                            {
                                arguments = Arguments(batch.ForwardArgumentBuffer, commandEnd);
                                batch.RenderForward(forward, properties, buffers, 0, 0, commandEnd, camera);
                            }

                            images[stage] = RenderAndRead(camera, target);
                            return arguments;
                        }

                        GraphicsBuffer.IndirectDrawIndexedArgs[] drawn = Take(0);
                        Assert.That(
                            new[] { batch.WholeArgumentElementsTransferred, batch.WholeInstanceElementsTransferred, batch.ArgumentElementsTransferred, batch.InstanceElementsTransferred },
                            Is.EqualTo(new long[] { 4, 6, 0, 0 }), route + ": a batch never sent to takes every slot and every record, counted apart");
                        Assert.That(new[] { batch.CommandCount, batch.InstanceCount }, Is.EqualTo(new[] { 4, 6 }), route + ": it goes through the slots, the one of no instance among them");
                        Assert.That(
                            new[] { drawn[0].instanceCount, drawn[1].instanceCount, drawn[2].instanceCount, drawn[3].instanceCount },
                            Is.EqualTo(new uint[] { 1, 0, 2, 1 }), route + ": the slot of no instance draws none");
                        Assert.That(
                            new[] { drawn[0].startInstance, drawn[2].startInstance, drawn[3].startInstance },
                            Is.EqualTo(new uint[] { 4, 0, 3 }), route + ": each command's instances begin where it was told");
                        if (culled)
                        {
                            uint[] visible = Visible(batch.CullForwardVisible(0), 6);
                            Assert.That(new[] { visible[0], visible[1], visible[3], visible[4] }, Is.EqualTo(new uint[] { 0, 1, 3, 4 }), "what the selection kept stands in each command's own region");
                        }

                        // Slot 2 is made to draw nothing: that one command is sent, and no instance record.
                        commands[2] = default;
                        starts[2] = 0;
                        commandRanges.Add(2, 3);
                        drawn = Take(1);
                        Assert.That(
                            new[] { batch.ArgumentElementsTransferred, batch.InstanceElementsTransferred, batch.WholeArgumentElementsTransferred, batch.WholeInstanceElementsTransferred },
                            Is.EqualTo(new long[] { 1, 0, 4, 6 }), route + ": the removed command alone is sent");
                        Assert.That(
                            new[] { drawn[0].instanceCount, drawn[1].instanceCount, drawn[2].instanceCount, drawn[3].instanceCount },
                            Is.EqualTo(new uint[] { 1, 0, 0, 1 }), route + ": it draws no instance");
                        if (culled)
                        {
                            // The selection reads the command again every time: a later one finds it still removed.
                            Cull(batch, 0, conditions);
                            Cull(batch, 0, conditions);
                            Assert.That(Arguments(batch.CullForwardArguments(0), 4)[2].instanceCount, Is.Zero, "later selections do not bring the removed command back");
                            Assert.That(Arguments(batch.CullShadowArguments(0), 4)[2].instanceCount, Is.Zero, "nor as a caster");
                        }

                        // A command is taken at the end -- the fifth slot, A once, its record the seventh -- while slot 2
                        // goes on drawing nothing and records 0 and 1 still hold where B stood.
                        commandEnd = 5;
                        instanceEnd = 7;
                        commands[4] = new VpIndirectCommand(rangeA, cell, 1);
                        starts[4] = 6;
                        transforms[6] = topRight;
                        commandRanges.Add(4, 5);
                        instanceFrom = 6;
                        instanceTo = 7;
                        drawn = Take(2);
                        Assert.That(
                            new[] { batch.ArgumentElementsTransferred, batch.InstanceElementsTransferred, batch.WholeArgumentElementsTransferred, batch.WholeInstanceElementsTransferred },
                            Is.EqualTo(new long[] { 2, 1, 4, 6 }), route + ": the command taken at the end and its one record are sent, and nothing else");
                        Assert.That(new[] { batch.CommandCount, batch.InstanceCount }, Is.EqualTo(new[] { 5, 7 }), route + ": it goes through one slot and one record more");
                        Assert.That(
                            new[] { drawn[4].instanceCount, drawn[4].startInstance, drawn[4].indexCountPerInstance, drawn[4].startIndex },
                            Is.EqualTo(new uint[] { 1, 6, (uint)rangeA.indexCount, (uint)rangeA.indexStart }), route + ": the slot at the end draws its command");
                        Assert.That(new[] { drawn[1].instanceCount, drawn[2].instanceCount }, Is.EqualTo(new uint[] { 0, 0 }), route + ": the removed command is still removed");
                    }
                }
                finally
                {
                    commands.Dispose();
                    starts.Dispose();
                    transforms.Dispose();
                    clips.Dispose();
                }

                return images;
            }

            // The same three pictures from commands packed one after another, each in a batch of its own.
            Color32[] Packed(bool culled, int stage)
            {
                Material forward = Material(ShaderName, Color.green, culled);
                var properties = new MaterialPropertyBlock();
                using (var pool = new VpCpuGeometryPool(6, 6, Allocator.Persistent))
                using (var buffers = new VpGpuIndexedGeometryBuffers(6, 6))
                using (var batch = new VpIndexedIndirectDrawBatch(3, 4, null, culled ? Setup() : null))
                {
                    Assert.That(pool.TryAppend(Triangle(a), out VpGeometryRange rangeA), Is.True);
                    Assert.That(pool.TryAppend(Triangle(b), out VpGeometryRange rangeB), Is.True);
                    Assert.That(buffers.TryUpload(pool), Is.True);
                    VpIndirectCommand[] commands;
                    Matrix4x4[] transforms;
                    switch (stage)
                    {
                        case 0:
                            commands = new[] { new VpIndirectCommand(rangeA, cell, 1), new VpIndirectCommand(rangeB, cell, 2), new VpIndirectCommand(rangeA, cell, 1) };
                            transforms = new[] { topLeft, topRight, bottomLeft, bottomRight };
                            break;
                        case 1:
                            commands = new[] { new VpIndirectCommand(rangeA, cell, 1), new VpIndirectCommand(rangeA, cell, 1) };
                            transforms = new[] { topLeft, bottomRight };
                            break;
                        default:
                            commands = new[] { new VpIndirectCommand(rangeA, cell, 1), new VpIndirectCommand(rangeA, cell, 1), new VpIndirectCommand(rangeA, cell, 1) };
                            transforms = new[] { topLeft, topRight, bottomRight };
                            break;
                    }

                    Assert.That(batch.TryUpload(commands, transforms, false), Is.True);
                    if (culled)
                    {
                        var conditions = new VpCullConditions();
                        conditions.SetEye(0, camera.worldToCameraMatrix, camera.projectionMatrix);
                        conditions.eyeCount = 1;
                        Cull(batch, 0, conditions);
                        batch.RenderForward(forward, properties, buffers, 0, 0, commands.Length, camera, 0);
                    }
                    else
                    {
                        batch.RenderForward(forward, properties, buffers, 0, 0, commands.Length, camera);
                    }

                    return RenderAndRead(camera, target);
                }
            }

            string[] stages = { "four triangles", "the two of slot 2 removed", "one more taken at the end" };
            Color32[][] vp3 = Slots(false);
            Color32[][] selected = Slots(true);
            var green = new int[3];
            for (int stage = 0; stage < 3; stage++)
            {
                Color32[] packed = Packed(false, stage);
                green[stage] = CountGreen(packed);
                Assert.That(CountDiffering(packed, vp3[stage]), Is.Zero, stages[stage] + ": pixels differing between the packed commands and the slots (VP Stage 3)");
                Assert.That(CountDiffering(packed, selected[stage]), Is.Zero, stages[stage] + ": pixels differing between the packed commands and the slots (the GPU selection)");
                Assert.That(CountDiffering(packed, Packed(true, stage)), Is.Zero, stages[stage] + ": pixels differing between the two routes, packed");
            }

            TestContext.Out.WriteLine("green pixels: " + green[0] + ", " + green[1] + ", " + green[2]);
            Assert.That(green[0], Is.GreaterThan(300), "the four triangles are drawn");
            Assert.That(green[1], Is.LessThan(green[0] * 2 / 3), "two of them are gone");
            Assert.That(green[2], Is.InRange(green[1] + 50, green[0] - 50), "and one is drawn by the command taken at the end");
        }

        private Mesh Triangle(Vector3[] positions)
        {
            Mesh mesh = Track(new Mesh());
            mesh.SetVertices(positions);
            mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back });
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            return mesh;
        }
    }
}
