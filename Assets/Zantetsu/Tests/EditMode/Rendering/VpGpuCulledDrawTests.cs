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

        private static void Cull(VpIndexedIndirectDrawBatch batch, int view, VpCullConditions conditions)
        {
            var commands = new CommandBuffer { name = "VP GPU Cull Test" };
            try
            {
                batch.IssueCull(commands, view, conditions);
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
        }

        /// <summary>
        /// A lit ground seen from above, with cubes of one command drawn above it by the chosen route; null cubes is
        /// the ground alone. The culled routes select with the camera's own frustum for the body and keep every caster.
        /// </summary>
        private Color32[] LitGround(
            Matrix4x4[] cubes, Route route, bool selectBody = true, VpInstanceClip[] clips = null, Action<Camera> view = null,
            Action<VpCullConditions> casters = null, float groundScale = 3f)
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
                    Cull(batch, 1, conditions);
                    batch.RenderForward(forward, properties, buffers, 0, 0, 1, camera, 1);
                    batch.RenderShadows(shadow, properties, buffers, 0, 0, 1, camera, 1);
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
