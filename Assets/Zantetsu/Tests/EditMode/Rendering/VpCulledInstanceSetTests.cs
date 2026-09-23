using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Stage 3 per-cascade instance culling probe: the conservative culling test rejects only bounds entirely outside a
    /// plane beyond the margin, keeps a large rotated instance poking in, and keeps bounds visible to either eye; the
    /// culled instance set writes one forward argument per command with a selected instance (doubled for Single Pass
    /// Instanced, starting at the command's forward visible offset) and per-split shadow arguments at their split offsets,
    /// lists the selected logical instances in the visible buffer, selects everything with no plane, rejects bad uploads
    /// keeping its state, and throws once disposed.
    /// </summary>
    public class VpCulledInstanceSetTests
    {
        private static readonly Bounds UnitCube = new Bounds(Vector3.zero, Vector3.one);

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
        }

        private Camera EyeCamera(Vector3 position)
        {
            Camera camera = new GameObject("VP Culling Test Eye").AddComponent<Camera>();
            _objects.Add(camera.gameObject);
            camera.enabled = false;
            camera.aspect = 1f;
            camera.fieldOfView = 30f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 50f;
            camera.transform.SetPositionAndRotation(position, Quaternion.identity);
            return camera;
        }

        /// <summary>A camera at the origin looking along +Z with a 30 degree square frustum, as one eye of planes.</summary>
        private Plane[] ForwardPlanes()
        {
            var planes = new Plane[2 * VpInstanceCulling.EyePlaneCount];
            Assert.That(VpInstanceCulling.GetEyePlanes(EyeCamera(Vector3.zero), planes), Is.EqualTo(1));
            return planes;
        }

        private static Matrix4x4 At(float x, float y, float z, float scale = 1f)
        {
            return Matrix4x4.TRS(new Vector3(x, y, z), Quaternion.identity, Vector3.one * scale);
        }

        [Test]
        public void MayIntersect_RejectsOnlyBoundsEntirelyOutsideAPlaneBeyondTheMargin()
        {
            var planes = new[] { new Plane(Vector3.right, 0f) };
            Assert.That(VpInstanceCulling.MayIntersect(new Bounds(new Vector3(-2f, 0f, 0f), Vector3.one * 2f), planes, 1), Is.False, "entirely outside");
            Assert.That(VpInstanceCulling.MayIntersect(new Bounds(new Vector3(-0.5f, 0f, 0f), Vector3.one * 2f), planes, 1), Is.True, "straddling");
            Assert.That(VpInstanceCulling.MayIntersect(new Bounds(new Vector3(-1.0005f, 0f, 0f), Vector3.one * 2f), planes, 1), Is.True, "outside within the margin");
            Assert.That(VpInstanceCulling.MayIntersect(new Bounds(new Vector3(-1.01f, 0f, 0f), Vector3.one * 2f), planes, 1), Is.False, "outside beyond the margin");
            Assert.That(VpInstanceCulling.MayIntersect(new Bounds(new Vector3(-50f, 0f, 0f), Vector3.one), planes, 0), Is.True, "no plane");

            // A cube of side 4 turned 45 degrees about Y at x = -2.5 reaches x = -2.5 + 2 * sqrt(2) = 0.33.
            Matrix4x4 rotated = Matrix4x4.TRS(new Vector3(-2.5f, 0f, 0f), Quaternion.Euler(0f, 45f, 0f), Vector3.one * 4f);
            Assert.That(VpInstanceCulling.MayIntersect(VpDirectDraw.WorldBounds(UnitCube, rotated), planes, 1), Is.True, "large rotated instance poking in");
        }

        [Test]
        public void MayIntersectAnyEye_KeepsBoundsVisibleToEitherEye()
        {
            var eyes = new Plane[2 * VpInstanceCulling.EyePlaneCount];
            var single = new Plane[2 * VpInstanceCulling.EyePlaneCount];
            VpInstanceCulling.GetEyePlanes(EyeCamera(new Vector3(-5f, 0f, 0f)), single);
            Array.Copy(single, 0, eyes, 0, VpInstanceCulling.EyePlaneCount);
            VpInstanceCulling.GetEyePlanes(EyeCamera(new Vector3(5f, 0f, 0f)), single);
            Array.Copy(single, 0, eyes, VpInstanceCulling.EyePlaneCount, VpInstanceCulling.EyePlaneCount);

            var leftOnly = new Bounds(new Vector3(-5f, 0f, 10f), Vector3.one);
            var rightOnly = new Bounds(new Vector3(5f, 0f, 10f), Vector3.one);
            var neither = new Bounds(new Vector3(0f, 0f, -10f), Vector3.one);
            Assert.That(VpInstanceCulling.MayIntersectAnyEye(leftOnly, eyes, 2), Is.True, "left eye only");
            Assert.That(VpInstanceCulling.MayIntersectAnyEye(rightOnly, eyes, 2), Is.True, "right eye only");
            Assert.That(VpInstanceCulling.MayIntersectAnyEye(rightOnly, eyes, 1), Is.False, "right eye only, left eye tested");
            Assert.That(VpInstanceCulling.MayIntersectAnyEye(neither, eyes, 2), Is.False, "neither eye");
        }

        /// <summary>
        /// Three commands: A has 2 instances behind the camera, B has 3 with the middle one out of view, C has 2 in view.
        /// Logical instances: A 0-1, B 2-4, C 5-6.
        /// </summary>
        private static (VpIndirectCommand[] commands, Matrix4x4[] transforms) ThreeCommands()
        {
            var a = new VpGeometryRange(0, 8, 0, 36);
            var b = new VpGeometryRange(8, 20, 36, 60);
            var c = new VpGeometryRange(28, 4, 96, 6);
            return (
                new[] { new VpIndirectCommand(a, UnitCube, 2), new VpIndirectCommand(b, UnitCube, 3), new VpIndirectCommand(c, UnitCube, 2) },
                new[]
                {
                    At(0f, 0f, -5f), At(1f, 0f, -8f),
                    At(0f, 0f, 10f), At(30f, 0f, 10f), At(-1f, 0f, 12f),
                    At(0.5f, 0.5f, 20f, 2f), At(0f, 0f, 30f),
                });
        }

        private static GraphicsBuffer.IndirectDrawIndexedArgs[] ReadArguments(GraphicsBuffer buffer, int start, int count)
        {
            var arguments = new GraphicsBuffer.IndirectDrawIndexedArgs[count];
            buffer.GetData(arguments, 0, start, count);
            return arguments;
        }

        private static uint[] ReadVisible(VpCulledInstanceSet set, int start, int count)
        {
            var visible = new uint[count];
            set.VisibleBuffer.GetData(visible, 0, start, count);
            return visible;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SelectForward_WritesOneArgumentPerCommandWithASelectedInstance(bool singlePassInstanced)
        {
            (VpIndirectCommand[] commands, Matrix4x4[] transforms) = ThreeCommands();
            Plane[] planes = ForwardPlanes();
            using (var set = new VpCulledInstanceSet(4, 16))
            {
                Assert.That(set.TryUpload(commands, transforms, singlePassInstanced), Is.True);
                set.SelectForward(planes, 1);
                set.UploadForward();

                Assert.That(set.ForwardDrawCount, Is.EqualTo(2), "commands B and C");
                Assert.That(set.ForwardSelectedInstances, Is.EqualTo(4));
                int forwardBase = VpCulledInstanceSet.MaxSplits * set.InstanceCapacity;
                uint multiplier = singlePassInstanced ? 2u : 1u;
                Assert.That(ReadVisible(set, forwardBase, 4), Is.EqualTo(new uint[] { 2, 4, 5, 6 }), "selected logical instances");
                GraphicsBuffer.IndirectDrawIndexedArgs[] arguments = ReadArguments(set.ForwardArgumentBuffer, 0, 2);
                Assert.That(arguments[0].indexCountPerInstance, Is.EqualTo(60u), "B index count");
                Assert.That(arguments[0].startIndex, Is.EqualTo(36u), "B start index");
                Assert.That(arguments[0].baseVertexIndex, Is.EqualTo(0u), "B base vertex");
                Assert.That(arguments[0].instanceCount, Is.EqualTo(2u * multiplier), "B instances");
                Assert.That(arguments[0].startInstance, Is.EqualTo((uint)forwardBase * multiplier), "B start instance");
                Assert.That(arguments[1].indexCountPerInstance, Is.EqualTo(6u), "C index count");
                Assert.That(arguments[1].startIndex, Is.EqualTo(96u), "C start index");
                Assert.That(arguments[1].instanceCount, Is.EqualTo(2u * multiplier), "C instances");
                Assert.That(arguments[1].startInstance, Is.EqualTo((uint)(forwardBase + 2) * multiplier), "C start instance");
                Assert.That(set.IsForwardSelected(3), Is.False, "B's middle instance");
                Assert.That(set.IsForwardSelected(5), Is.True, "C's first instance");
            }
        }

        [Test]
        public void SelectSplit_WritesShadowArgumentsAtTheSplitOffsets_AndSelectsEverythingWithNoPlane()
        {
            (VpIndirectCommand[] commands, Matrix4x4[] transforms) = ThreeCommands();
            Plane[] planes = ForwardPlanes();
            using (var set = new VpCulledInstanceSet(4, 16))
            {
                Assert.That(set.TryUpload(commands, transforms, true), Is.True);
                set.BeginShadowFrame();
                set.SelectSplit(0, planes, 0);
                set.SelectSplit(2, planes, VpInstanceCulling.EyePlaneCount);
                set.UploadShadowSelections();

                Assert.That(set.SplitCount, Is.EqualTo(3));
                Assert.That(set.ShadowSelectedInstances(0), Is.EqualTo(7), "no plane selects every instance");
                Assert.That(set.ShadowDrawCount(0), Is.EqualTo(3));
                Assert.That(set.ShadowSelectedInstances(1), Is.Zero, "a split not selected");
                Assert.That(set.ShadowDrawCount(1), Is.Zero);
                Assert.That(set.ShadowSelectedInstances(2), Is.EqualTo(4));
                Assert.That(set.ShadowDrawCount(2), Is.EqualTo(2));
                Assert.That(ReadVisible(set, 0, 7), Is.EqualTo(new uint[] { 0, 1, 2, 3, 4, 5, 6 }));
                Assert.That(ReadVisible(set, 2 * set.InstanceCapacity, 4), Is.EqualTo(new uint[] { 2, 4, 5, 6 }));

                set.GetShadowDraw(2, 1, out int argumentsOffset, out int visibleOffset);
                Assert.That(argumentsOffset, Is.EqualTo((2 * set.CommandCapacity + 1) * GraphicsBuffer.IndirectDrawIndexedArgs.size));
                Assert.That(visibleOffset, Is.EqualTo(2 * set.InstanceCapacity + 2));
                GraphicsBuffer.IndirectDrawIndexedArgs[] arguments = ReadArguments(set.ShadowArgumentBuffer, 2 * set.CommandCapacity, 2);
                Assert.That(arguments[0].instanceCount, Is.EqualTo(2u), "B logical instances, not doubled");
                Assert.That(arguments[0].startIndex, Is.EqualTo(36u));
                Assert.That(arguments[0].startInstance, Is.EqualTo(0u));
                Assert.That(arguments[1].instanceCount, Is.EqualTo(2u), "C logical instances");
                Assert.That(arguments[1].indexCountPerInstance, Is.EqualTo(6u));
                Assert.That(arguments[1].startInstance, Is.EqualTo(0u));
                Assert.Throws<ArgumentOutOfRangeException>(() => set.GetShadowDraw(2, 2, out _, out _));
                Assert.Throws<ArgumentOutOfRangeException>(() => set.SelectSplit(VpCulledInstanceSet.MaxSplits, planes, 0));

                set.BeginShadowFrame();
                Assert.That(set.SplitCount, Is.Zero);
                Assert.That(set.ShadowDrawCount(2), Is.Zero, "a new frame forgets the selections");
            }
        }

        [Test]
        public void Uploads_WriteOnlySelectionsTheBuffersDoNotHoldYet()
        {
            (VpIndirectCommand[] commands, Matrix4x4[] transforms) = ThreeCommands();
            Plane[] planes = ForwardPlanes();
            var farther = new Plane[2 * VpInstanceCulling.EyePlaneCount];
            Assert.That(VpInstanceCulling.GetEyePlanes(EyeCamera(new Vector3(0f, 0f, -20f)), farther), Is.EqualTo(1));
            using (var set = new VpCulledInstanceSet(4, 16))
            {
                Assert.That(set.TryUpload(commands, transforms, false), Is.True);
                int forwardBase = VpCulledInstanceSet.MaxSplits * set.InstanceCapacity;
                set.SelectForward(planes, 1);
                set.UploadForward();
                Assert.That(set.ForwardUploadCount, Is.EqualTo(1), "the first selection");
                set.SelectForward(planes, 1);
                set.UploadForward();
                Assert.That(set.ForwardUploadCount, Is.EqualTo(1), "the same selection again uploads nothing");

                set.SelectForward(farther, 1);
                set.SelectForward(farther, 1);
                set.UploadForward();
                Assert.That(set.ForwardUploadCount, Is.EqualTo(2), "a changed selection selected twice before uploading");
                Assert.That(set.ForwardSelectedInstances, Is.EqualTo(6), "every instance but the one at x = 30");
                Assert.That(ReadVisible(set, forwardBase, 6), Is.EqualTo(new uint[] { 0, 1, 2, 4, 5, 6 }), "the farther view's instances");

                set.SelectForward(planes, 1);
                set.UploadForward();
                Assert.That(set.ForwardUploadCount, Is.EqualTo(3), "returning to a smaller selection");
                Assert.That(ReadVisible(set, forwardBase, 4), Is.EqualTo(new uint[] { 2, 4, 5, 6 }));

                Assert.That(set.TryUpload(commands, transforms, true), Is.True);
                set.SelectForward(planes, 1);
                set.UploadForward();
                Assert.That(set.ForwardUploadCount, Is.EqualTo(4), "the same instances doubled for Single Pass Instanced");
                Assert.That(ReadArguments(set.ForwardArgumentBuffer, 0, 1)[0].instanceCount, Is.EqualTo(4u));

                set.BeginShadowFrame();
                set.SelectSplit(1, planes, 0);
                set.UploadShadowSelections();
                Assert.That(set.ShadowUploadCount, Is.EqualTo(1), "the first split selection; the unselected split 0 uploads nothing");
                set.BeginShadowFrame();
                set.SelectSplit(1, planes, 0);
                set.UploadShadowSelections();
                Assert.That(set.ShadowUploadCount, Is.EqualTo(1), "the same split selection in a new frame uploads nothing");
                set.BeginShadowFrame();
                set.SelectSplit(1, planes, VpInstanceCulling.EyePlaneCount);
                set.UploadShadowSelections();
                Assert.That(set.ShadowUploadCount, Is.EqualTo(2), "a changed split selection");
                Assert.That(ReadVisible(set, set.InstanceCapacity, 4), Is.EqualTo(new uint[] { 2, 4, 5, 6 }));
                set.BeginShadowFrame();
                set.SelectSplit(1, planes, 0);
                set.UploadShadowSelections();
                Assert.That(set.ShadowUploadCount, Is.EqualTo(3), "growing back to every instance");
                Assert.That(ReadVisible(set, set.InstanceCapacity, 7), Is.EqualTo(new uint[] { 0, 1, 2, 3, 4, 5, 6 }));
                GraphicsBuffer.IndirectDrawIndexedArgs[] arguments = ReadArguments(set.ShadowArgumentBuffer, set.CommandCapacity, 3);
                Assert.That(arguments.Select(argument => argument.instanceCount), Is.EqualTo(new uint[] { 2, 3, 2 }), "split 1 arguments");
            }
        }

        [Test]
        public void TryUpload_RejectsBadInputKeepingTheState_AndDisposeReleasesOnce()
        {
            (VpIndirectCommand[] commands, Matrix4x4[] transforms) = ThreeCommands();
            var set = new VpCulledInstanceSet(3, 7);
            Assert.That(set.TryUpload(commands, transforms, false), Is.True);
            Assert.That(set.WorldBounds.Contains(new Vector3(30f, 0f, 10f)), Is.True, "world bounds hold every instance");

            Assert.That(set.TryUpload(new VpIndirectCommand[4], new Matrix4x4[0], false), Is.False, "too many commands");
            Assert.That(set.TryUpload(new[] { new VpIndirectCommand(default, UnitCube, 8) }, new Matrix4x4[8], false), Is.False, "too many instances");
            Assert.That(set.TryUpload(commands, new Matrix4x4[6], false), Is.False, "transform count");
            Assert.That(set.TryUpload(new[] { new VpIndirectCommand(default, UnitCube, -1) }, new Matrix4x4[0], false), Is.False, "negative count");
            Assert.That(set.CommandCount, Is.EqualTo(3));
            Assert.That(set.InstanceCount, Is.EqualTo(7));

            set.Dispose();
            set.Dispose();
            Assert.Throws<ObjectDisposedException>(() => set.SelectForward(new Plane[6], 1));
            Assert.Throws<ObjectDisposedException>(() => set.BeginShadowFrame());
            Assert.Throws<ObjectDisposedException>(() => _ = set.VisibleBuffer);
        }
    }
}
