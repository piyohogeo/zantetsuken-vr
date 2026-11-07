using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering.Tests
{
    public class VpShadowSliceComputeTests
    {
        [Test]
        public void PerInstanceBounds_KeepTheirOwnSelection_AndRefuseTheCommandBoundsShadowKernel()
        {
            Assert.That(VpGpuCullSetup.TryCreate(1, true, true, out VpGpuCullSetup setup, out string failure), Is.True, failure);
            using (var batch = new VpIndexedIndirectDrawBatch(1, 1, null, setup))
            using (var commands = new CommandBuffer())
            {
                Assert.That(batch.CullsByInstanceBounds, Is.True, "Skin24 selects by each individual's current bounds");
                Assert.That(setup.Shader.HasKernel("CullInstanceBounds"), Is.True, "the skin selection remains available");
                long bytes = batch.GpuBytes;
                Assert.Throws<InvalidOperationException>(() => batch.IssueShadowSliceCull(
                    commands, 0, new[] { Slice(-1f, 1f) }, new Vector4[1], 1));
                Assert.Throws<InvalidOperationException>(() => batch.ShadowSliceCompactionOf(0, 0, 1));
                Assert.That(batch.GpuBytes, Is.EqualTo(bytes), "no ordinary per-cascade outputs are allocated for skin bounds");
                Assert.That(VpGpuCullSetup.CasterSliceSelectionFor(true, false, true, false), Is.EqualTo(1f),
                    "the Direct3D skin caster keeps the vertex-stage judgement");
                Assert.That(VpGpuCullSetup.CasterSliceSelectionFor(true, true, true, true), Is.EqualTo(1f),
                    "the explicit Vulkan skin caster judgement remains available");

                Type route = Type.GetType("Zantetsu.Rendering.Urp.VpNativeDrawRoute, Zantetsu.Rendering.Urp", true);
                Type issueType = route.GetNestedType("Issue", BindingFlags.NonPublic);
                MethodInfo choosesCompute = route.GetMethod("UsesComputeShadowSlices", BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(issueType, Is.Not.Null);
                Assert.That(choosesCompute, Is.Not.Null);
                object issue = Activator.CreateInstance(issueType);
                issueType.GetField("batch").SetValue(issue, batch);
                Assert.That(choosesCompute.Invoke(null, new[] { (object)true, issue }), Is.False,
                    "the route keeps per-instance bounds on the union even when the issue has no Skin24 buffers");

                using (var ordinary = Batch())
                {
                    issueType.GetField("batch").SetValue(issue, ordinary);
                    Assert.That(choosesCompute.Invoke(null, new[] { (object)true, issue }), Is.True);
                    Assert.That(choosesCompute.Invoke(null, new[] { (object)false, issue }), Is.False);
                    issueType.GetField("skin").SetValue(issue, new VpSkinDrawBuffers());
                    Assert.That(choosesCompute.Invoke(null, new[] { (object)true, issue }), Is.False,
                        "Skin24 keeps its existing caster variant");
                    issueType.GetField("skin").SetValue(issue, null);
                    issueType.GetField("clipList").SetValue(issue, true);
                    Assert.That(choosesCompute.Invoke(null, new[] { (object)true, issue }), Is.False,
                        "clip-list casters keep their existing selection");
                }
            }
        }

        [Test]
        public void Request_DefaultsToDirect3D12_AndTheVertexArgumentWins()
        {
            Assert.That(VpGpuCullSetup.ComputeShadowSlicesArgument, Is.EqualTo("-zantetsuVp3cComputeShadowSlices"));
            Assert.That(VpGpuCullSetup.VertexShadowSlicesArgument, Is.EqualTo("-zantetsuVp3cVertexShadowSlices"));
            Assert.That(VpGpuCullSetup.ReadComputeShadowSlices(null, GraphicsDeviceType.Direct3D12), Is.True);
            Assert.That(VpGpuCullSetup.ReadComputeShadowSlices(new string[0], GraphicsDeviceType.Direct3D12), Is.True);
            Assert.That(VpGpuCullSetup.ReadComputeShadowSlices(new[] { "player", VpNativeDrawSetup.Argument }, GraphicsDeviceType.Direct3D12), Is.True);
            Assert.That(VpGpuCullSetup.ReadComputeShadowSlices(null, GraphicsDeviceType.Direct3D11), Is.False);
            Assert.That(VpGpuCullSetup.ReadComputeShadowSlices(null, GraphicsDeviceType.Vulkan), Is.False);
            Assert.That(VpGpuCullSetup.ReadComputeShadowSlices(new[] { VpGpuCullSetup.NoSliceSelectionArgument }, GraphicsDeviceType.Direct3D12), Is.False);
            Assert.That(VpGpuCullSetup.ReadComputeShadowSlices(new[] { VpGpuCullSetup.ComputeShadowSlicesArgument }, GraphicsDeviceType.Direct3D12), Is.True);
            Assert.That(VpGpuCullSetup.ReadComputeShadowSlices(new[] { VpGpuCullSetup.ComputeShadowSlicesArgument, VpGpuCullSetup.NoSliceSelectionArgument }, GraphicsDeviceType.Direct3D12), Is.False);
            Assert.That(VpGpuCullSetup.ReadComputeShadowSlices(new[] { VpGpuCullSetup.ComputeShadowSlicesArgument }, GraphicsDeviceType.Direct3D11), Is.False);
            Assert.That(VpGpuCullSetup.ReadComputeShadowSlices(new[] { VpGpuCullSetup.ComputeShadowSlicesArgument }, GraphicsDeviceType.Vulkan), Is.False);
            Assert.That(VpGpuCullSetup.ReadComputeShadowSlices(new[] { VpGpuCullSetup.VertexShadowSlicesArgument }, GraphicsDeviceType.Direct3D12), Is.False);
            Assert.That(VpGpuCullSetup.ReadComputeShadowSlices(new[] { VpGpuCullSetup.VertexShadowSlicesArgument, VpGpuCullSetup.ComputeShadowSlicesArgument }, GraphicsDeviceType.Direct3D12), Is.False);
            Assert.That(VpGpuCullSetup.ReadComputeShadowSlices(new[] { VpGpuCullSetup.ComputeShadowSlicesArgument, VpGpuCullSetup.VertexShadowSlicesArgument }, GraphicsDeviceType.Direct3D12), Is.False);
        }

        [Test]
        public void FourSlices_SelectEachInstance_AndKeepNumberedNativeEntriesAndRuns()
        {
            var bounds = new Bounds(Vector3.zero, Vector3.one);
            var input = new[]
            {
                new VpIndirectCommand(new VpGeometryRange(0, 3, 100, 3), bounds, 3),
                new VpIndirectCommand(new VpGeometryRange(3, 3, 200, 6), bounds, 0),
                new VpIndirectCommand(new VpGeometryRange(6, 3, 300, 9), bounds, 2),
            };
            var transforms = new[]
            {
                Matrix4x4.Translate(new Vector3(-3f, 0f, 5f)),
                Matrix4x4.Translate(new Vector3(0f, 0f, 5f)),
                Matrix4x4.Translate(new Vector3(3f, 0f, 5f)),
                Matrix4x4.TRS(new Vector3(1.8f, 0f, 5f), Quaternion.Euler(0f, 0f, 90f), new Vector3(0.2f, 3f, 1f)),
                Matrix4x4.TRS(new Vector3(-1.8f, 0f, 5f), Quaternion.identity, new Vector3(3f, 0.2f, 1f)),
            };
            var matrices = new[]
            {
                Slice(-1f, 1f), Slice(-4f, -2f), Slice(2f, 4f), Slice(8f, 10f),
            };

            using (var batch = Batch())
            using (var commands = new CommandBuffer())
            {
                Assert.That(batch.TryUpload(input, transforms, true), Is.True);
                long originalViewBytes = batch.CullViewBytes;
                long originalGpuBytes = batch.GpuBytes;
                long sliceBytes = 4L * (batch.CommandCapacity * 24L + batch.InstanceCapacity * sizeof(uint));
                var conditions = new VpCullConditions();
                conditions.KeepEverything();
                batch.IssueCull(commands, 0, conditions);
                VpIndexedIndirectDrawBatch.ShadowSliceOutput output = batch.IssueShadowSliceCull(commands, 0, matrices, new Vector4[4], 4);
                Assert.That(batch.CullViewBytes, Is.EqualTo(originalViewBytes + sliceBytes));
                Assert.That(batch.GpuBytes, Is.EqualTo(originalGpuBytes + sliceBytes));
                Graphics.ExecuteCommandBuffer(commands);

                var firstCommandCounts = new uint[] { 1, 1, 1, 0 };
                var lastCommandCounts = new uint[] { 2, 1, 1, 0 };
                for (int split = 0; split < 4; split++)
                {
                    uint[] arguments = Words(output.Arguments(split));
                    AssertEntry(arguments, 0, 3, firstCommandCounts[split], 100, 0);
                    AssertEntry(arguments, 1, 6, 0, 200, 3);
                    AssertEntry(arguments, 2, 9, lastCommandCounts[split], 300, 3);
                }

                AssertVisible(output.Visible(0), 0, 1);
                AssertVisible(output.Visible(0), 3, 3, 4);
                AssertVisible(output.Visible(1), 0, 0);
                AssertVisible(output.Visible(1), 3, 4);
                AssertVisible(output.Visible(2), 0, 2);
                AssertVisible(output.Visible(2), 3, 3);
                Assert.That(output.Arguments(0), Is.Not.SameAs(output.Arguments(1)));
                Assert.That(output.Visible(0), Is.Not.SameAs(output.Visible(1)));
            }
        }

        [Test]
        public void Slice_PreservesUnionExclusions_AndUsesBothBiasTermsWithoutAClipZTest()
        {
            var bounds = new Bounds(Vector3.zero, Vector3.one * 0.2f);
            var input = new[] { new VpIndirectCommand(new VpGeometryRange(0, 3, 100, 3), bounds, 4) };
            var transforms = new[]
            {
                Matrix4x4.Translate(new Vector3(0f, 0f, -100f)),
                Matrix4x4.Translate(new Vector3(1.35f, 0f, 0.5f)),
                Matrix4x4.Translate(new Vector3(0f, 0f, 100f)),
                Matrix4x4.Translate(new Vector3(-0.8f, 0f, 0.5f)),
            };
            var conditions = new VpCullConditions
            {
                shadowSplitCount = 1,
                shadowPlaneCounts = new Vector4(1f, 0f, 0f, 0f),
            };
            conditions.SetShadowPlane(0, 0, new Plane(Vector3.right, 0f));
            var matrices = new[] { Slice(-1f, 1f), Slice(-1f, 1f) };
            var biases = new[] { Vector4.zero, new Vector4(-0.1f, -0.2f, 1000f, 1000f) };

            using (var batch = Batch())
            using (var commands = new CommandBuffer())
            {
                Assert.That(batch.TryUpload(input, transforms, false), Is.True);
                batch.IssueCull(commands, 0, conditions);
                VpIndexedIndirectDrawBatch.ShadowSliceOutput output = batch.IssueShadowSliceCull(commands, 0, matrices, biases, 2);
                Graphics.ExecuteCommandBuffer(commands);

                Assert.That(Words(batch.CullShadowArguments(0))[2], Is.EqualTo(3u), "the union removes instance 3 before per-slice selection");
                AssertEntry(Words(output.Arguments(0)), 0, 3, 2, 100, 0);
                AssertVisible(output.Visible(0), 0, 0, 2);
                AssertEntry(Words(output.Arguments(1)), 0, 3, 3, 100, 0);
                AssertVisible(output.Visible(1), 0, 0, 1, 2);
            }
        }

        [Test]
        public void Views_AreIndependent_AndAZeroUnionOverwritesThePreviousSliceCount()
        {
            var bounds = new Bounds(Vector3.zero, Vector3.one * 0.2f);
            var input = new[] { new VpIndirectCommand(new VpGeometryRange(0, 3, 100, 3), bounds, 2) };
            var transforms = new[]
            {
                Matrix4x4.Translate(new Vector3(-3f, 0f, 5f)),
                Matrix4x4.Translate(new Vector3(3f, 0f, 5f)),
            };
            var all = new VpCullConditions();
            all.KeepEverything();
            var left = new[] { Slice(-4f, -2f) };
            var right = new[] { Slice(2f, 4f) };
            var bias = new Vector4[1];

            using (var batch = Batch())
            using (var commands = new CommandBuffer())
            {
                Assert.That(batch.TryUpload(input, transforms, false), Is.True);
                long originalViewBytes = batch.CullViewBytes;
                long originalGpuBytes = batch.GpuBytes;
                long sliceBytes = 4L * (batch.CommandCapacity * 24L + batch.InstanceCapacity * sizeof(uint));
                batch.IssueCull(commands, 0, all);
                VpIndexedIndirectDrawBatch.ShadowSliceOutput first = batch.IssueShadowSliceCull(commands, 0, left, bias, 1);
                Assert.That(batch.CullViewBytes, Is.EqualTo(originalViewBytes + sliceBytes));
                Assert.That(batch.GpuBytes, Is.EqualTo(originalGpuBytes + sliceBytes));
                batch.IssueCull(commands, 1, all);
                VpIndexedIndirectDrawBatch.ShadowSliceOutput second = batch.IssueShadowSliceCull(commands, 1, right, bias, 1);
                Assert.That(batch.CullViewBytes, Is.EqualTo(originalViewBytes + 2L * sliceBytes));
                Assert.That(batch.GpuBytes, Is.EqualTo(originalGpuBytes + 2L * sliceBytes));
                Graphics.ExecuteCommandBuffer(commands);

                Assert.That(first.Arguments(0), Is.Not.SameAs(second.Arguments(0)));
                Assert.That(first.Visible(0), Is.Not.SameAs(second.Visible(0)));
                AssertEntry(Words(first.Arguments(0)), 0, 3, 1, 100, 0);
                AssertVisible(first.Visible(0), 0, 0);
                AssertEntry(Words(second.Arguments(0)), 0, 3, 1, 100, 0);
                AssertVisible(second.Visible(0), 0, 1);

                var excluded = new VpCullConditions
                {
                    shadowSplitCount = 1,
                    shadowPlaneCounts = new Vector4(1f, 0f, 0f, 0f),
                };
                excluded.SetShadowPlane(0, 0, new Plane(Vector3.right, -100f));
                commands.Clear();
                batch.IssueCull(commands, 0, excluded);
                VpIndexedIndirectDrawBatch.ShadowSliceOutput empty = batch.IssueShadowSliceCull(commands, 0, left, bias, 1);
                Assert.That(batch.CullViewBytes, Is.EqualTo(originalViewBytes + 2L * sliceBytes));
                Assert.That(batch.GpuBytes, Is.EqualTo(originalGpuBytes + 2L * sliceBytes));
                Graphics.ExecuteCommandBuffer(commands);
                AssertEntry(Words(empty.Arguments(0)), 0, 3, 0, 100, 0);
                AssertEntry(Words(second.Arguments(0)), 0, 3, 1, 100, 0);

                transforms[0] = Matrix4x4.Translate(new Vector3(3f, 0f, 5f));
                transforms[1] = Matrix4x4.Translate(new Vector3(-3f, 0f, 5f));
                Assert.That(batch.TryUpload(input, transforms, false), Is.True);
                commands.Clear();
                batch.IssueCull(commands, 0, all);
                VpIndexedIndirectDrawBatch.ShadowSliceOutput moved = batch.IssueShadowSliceCull(commands, 0, left, bias, 1);
                Assert.That(batch.CullViewBytes, Is.EqualTo(originalViewBytes + 2L * sliceBytes));
                Assert.That(batch.GpuBytes, Is.EqualTo(originalGpuBytes + 2L * sliceBytes));
                Graphics.ExecuteCommandBuffer(commands);
                AssertEntry(Words(moved.Arguments(0)), 0, 3, 1, 100, 0);
                AssertVisible(moved.Visible(0), 0, 1);
            }
        }

        private static VpIndexedIndirectDrawBatch Batch()
        {
            Assert.That(VpGpuCullSetup.TryCreate(2, true, out VpGpuCullSetup setup, out string failure), Is.True, failure);
            var batch = new VpIndexedIndirectDrawBatch(4, 8, null, setup);
            batch.WriteWholeOnce();
            return batch;
        }

        private static Matrix4x4 Slice(float left, float right)
        {
            return Matrix4x4.Ortho(left, right, -1f, 1f, 0.1f, 10f);
        }

        private static uint[] Words(GraphicsBuffer buffer)
        {
            var words = new uint[buffer.count * buffer.stride / sizeof(uint)];
            buffer.GetData(words);
            return words;
        }

        private static void AssertEntry(uint[] words, int command, uint indexCount, uint instances, uint startIndex, uint startInstance)
        {
            int first = command * 6;
            Assert.That(new[] { words[first], words[first + 1], words[first + 2], words[first + 3], words[first + 4], words[first + 5] },
                Is.EqualTo(new[] { (uint)command, indexCount, instances, startIndex, 0u, startInstance }));
        }

        private static void AssertVisible(GraphicsBuffer buffer, int start, params uint[] expected)
        {
            uint[] visible = Words(buffer);
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(visible[start + i], Is.EqualTo(expected[i]), "visible run at " + (start + i));
            }
        }
    }
}
