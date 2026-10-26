using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Plugin = Zantetsu.Rendering.VpNativeDrawPlugin;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// The managed side of the Direct3D 12 native draw (DESIGN 4.5.8), without the plugin: the shader binary the
    /// Editor bakes reads back as written and the baked assets are in the build with the bindings the plugin needs; a
    /// constant block writes each constant at the compiled offset and nowhere else; the event data of an issue is laid
    /// out for the plugin and a block is never reused before the plugin has answered; a batch made for the plugin
    /// writes 24-byte entries numbered with their command; the route is asked for by its argument alone; and the
    /// plugin's availability is answered explicitly, never assumed.
    /// </summary>
    public class VpNativeDrawTests
    {
        private readonly List<IDisposable> _disposables = new List<IDisposable>();

        [TearDown]
        public void DisposeAll()
        {
            foreach (IDisposable disposable in _disposables)
            {
                disposable.Dispose();
            }

            _disposables.Clear();
            VpCommandCompaction.LayoutForTest = null;   // a case that made another route's sets leaves the device's
        }

        private T Track<T>(T disposable) where T : IDisposable
        {
            _disposables.Add(disposable);
            return disposable;
        }

        [Test]
        public unsafe void WarmupData_UsesTheVersionOneResourceFreeHeader_AndWaitsForItsOwnConsumption()
        {
            var ring = Track(new VpNativeDrawData(2, 4, 2, 256));
            Assert.That(ring.TryBeginWarmup(1), Is.True);
            IntPtr block = ring.Finish(out uint serial);
            var header = (Plugin.Draw*)block;
            Assert.That(header->version, Is.EqualTo(Plugin.ContractVersion));
            Assert.That(header->pipeline, Is.EqualTo(1u));
            Assert.That((IntPtr)header->argumentBuffer, Is.EqualTo(IntPtr.Zero));
            Assert.That((IntPtr)header->indexBuffer, Is.EqualTo(IntPtr.Zero));
            Assert.That(header->argumentOffset, Is.Zero);
            Assert.That(header->commandCount, Is.Zero);
            Assert.That(header->resourceCount, Is.Zero);
            Assert.That(header->constantsCount, Is.Zero);
            Assert.That(ring.StateOf(serial), Is.EqualTo(VpNativeDrawData.BlockState.Prepared));
            var recorded = new List<IntPtr>();
            Assert.That(ring.RecordPrepared(recorded.Add), Is.EqualTo(1));
            Assert.That(recorded, Is.EqualTo(new[] { block }));
            ring.SetConsumedSentinel(5);
            Assert.That(ring.InFlight, Is.EqualTo(1), "a recorded warm event still owns its header");
            Assert.That(VpNativeDrawData.ResultOf(block), Is.Null);
            header->result = (uint)Plugin.Result.Recorded;
            header->consumed = 1;
            Assert.That(VpNativeDrawData.ResultOf(block), Is.EqualTo(Plugin.Result.Recorded));
            Assert.That(ring.InFlight, Is.Zero);

            Assert.That(ring.TryBeginWarmup(2, true), Is.True);
            block = ring.Finish(out _);
            Assert.That(((Plugin.Draw*)block)->argumentOffset, Is.EqualTo(1u), "caster and motion request the real pass restart path");
            Assert.That(ring.DropPrepared(), Is.EqualTo(1));
            Assert.Throws<ArgumentException>(() => ring.TryBegin(1, IntPtr.Zero, 0, 0, IntPtr.Zero, 0, 4),
                "resource-free headers are accepted only through the optional warm event API");
        }

        [Test]
        public unsafe void BarrierData_UsesTheResourceFreeHeader_WithoutAPipeline_AndIsRecordedAsItsOwnEvent()
        {
            // 2026-10-10: the compute barrier event between a selection's dispatches and the compaction that reads them.
            var ring = Track(new VpNativeDrawData(2, 4, 2, 256));
            Assert.That(ring.TryBeginBarrier(), Is.True);
            IntPtr block = ring.Finish(out uint serial);
            var header = (Plugin.Draw*)block;
            Assert.That(header->version, Is.EqualTo(Plugin.ContractVersion));
            Assert.That(header->pipeline, Is.Zero);
            Assert.That((IntPtr)header->argumentBuffer, Is.EqualTo(IntPtr.Zero));
            Assert.That((IntPtr)header->indexBuffer, Is.EqualTo(IntPtr.Zero));
            Assert.That(header->argumentOffset, Is.Zero);
            Assert.That(header->commandCount, Is.Zero);
            Assert.That(header->resourceCount, Is.Zero);
            Assert.That(header->constantsCount, Is.Zero);
            Assert.That(ring.InFlightIn(VpNativeDrawData.BlockState.Prepared), Is.EqualTo(1));
            var recorded = new List<IntPtr>();
            Assert.That(ring.RecordPrepared(recorded.Add), Is.EqualTo(1));
            Assert.That(recorded, Is.EqualTo(new[] { block }));
            Assert.That(ring.InFlightIn(VpNativeDrawData.BlockState.Prepared), Is.Zero);
            Assert.That(ring.StateOf(serial), Is.EqualTo(VpNativeDrawData.BlockState.Recorded));
            header->result = (uint)Plugin.Result.Recorded;
            header->consumed = 1;
            Assert.That(VpNativeDrawData.ResultOf(block), Is.EqualTo(Plugin.Result.Recorded));

            // Off a Vulkan device the plugin has no such event, and the selection says so before anything else of it.
            Assume.That(Plugin.Vulkan, Is.False, "this case is the Editor's (Direct3D)");
            Assert.That(Plugin.BarrierEventId, Is.Zero);
        }

        [Test]
        public unsafe void IndirectCountData_UsesTheVersionTwoExtension_WithTheRunAsTheMostAndTheCountRecord()
        {
            var ring = Track(new VpNativeDrawData(2, 4, 2, 256));
            Assert.That(ring.TryBeginIndirectCount(3, new IntPtr(300), 40, 1602, new IntPtr(400), 12, new IntPtr(500), 0, 1024, new IntPtr(123), 4000, 4), Is.True);
            ring.AddViewport(new Rect(0, 0, 32, 32));
            IntPtr block = ring.Finish(out _);
            var header = (Plugin.Draw*)block;
            Assert.That(header->version, Is.EqualTo(Plugin.ExtendedDrawVersion));
            Assert.That(VpNativeDrawData.ModeOf(block), Is.EqualTo(Plugin.DrawMode.IndirectCount));
            Assert.That((IntPtr)header->argumentBuffer, Is.EqualTo(new IntPtr(300)));
            Assert.That(header->argumentOffset, Is.EqualTo(40u * Plugin.ArgumentStride), "the compacted run begins where the fixed run does");
            Assert.That(header->commandCount, Is.EqualTo(1602u), "the run's length is the most the GPU's count may be");
            var ext = (Plugin.DrawExtension*)((byte*)block + sizeof(Plugin.Draw));
            Assert.That(ext->commandsCount, Is.EqualTo(1u));
            Assert.That(ext->reserved, Is.Zero);
            Assert.That(ext->commandsOffset % 8, Is.Zero);
            Assert.That(ext->commandsOffset + sizeof(Plugin.IndirectCountRecord), Is.LessThanOrEqualTo(header->totalBytes));
            var record = (Plugin.IndirectCountRecord*)((byte*)block + ext->commandsOffset);
            Assert.That((IntPtr)record->countBuffer, Is.EqualTo(new IntPtr(400)));
            Assert.That(record->countOffset, Is.EqualTo(12u));
            Assert.That((IntPtr)record->zeroBuffer, Is.EqualTo(new IntPtr(500)));
            Assert.That(record->zeroOffset, Is.Zero);
            Assert.That(record->zeroBytes, Is.EqualTo(1024u));
            Assert.That(record->reserved, Is.Zero);
            Assert.That(sizeof(Plugin.IndirectCountRecord), Is.EqualTo(32), "the C ABI's ZvnIndirectCount");
            Assert.That(ring.DropPrepared(), Is.EqualTo(1));
            Assert.That(ring.TryBeginIndirectCount(3, new IntPtr(300), 40, 1602, new IntPtr(400), 12, IntPtr.Zero, 8, 1024, new IntPtr(123), 4000, 4), Is.True, "no buffer to zero");
            ring.AddViewport(new Rect(0, 0, 32, 32));
            block = ring.Finish(out _);
            record = (Plugin.IndirectCountRecord*)((byte*)block + ((Plugin.DrawExtension*)((byte*)block + sizeof(Plugin.Draw)))->commandsOffset);
            Assert.That(record->zeroBytes, Is.Zero, "no range without a buffer");
            Assert.That(record->zeroOffset, Is.Zero);
            Assert.Throws<ArgumentException>(() => ring.TryBeginIndirectCount(3, new IntPtr(300), 0, 1, IntPtr.Zero, 0, IntPtr.Zero, 0, 0, new IntPtr(123), 4000, 4));

            // The draw from a copy of its count (2026-10-10): the record names the copy's slot and nothing to zero.
            Assert.That(ring.DropPrepared(), Is.EqualTo(1));
            Assert.That(VpCommandCompaction.DrawFromCopy, Is.True, "the way of a route that compacts");
            Assert.That(ring.TryBeginIndirectCount(3, new IntPtr(300), 40, 1602, new IntPtr(400), 12, new IntPtr(600), 12, 0, new IntPtr(123), 4000, 4, true), Is.True);
            ring.AddViewport(new Rect(0, 0, 32, 32));
            block = ring.Finish(out _);
            record = (Plugin.IndirectCountRecord*)((byte*)block + ((Plugin.DrawExtension*)((byte*)block + sizeof(Plugin.Draw)))->commandsOffset);
            Assert.That(record->reserved, Is.EqualTo(Plugin.CountDrawFromCopy));
            Assert.That((IntPtr)record->countBuffer, Is.EqualTo(new IntPtr(400)), "the compute's count: the copy's source");
            Assert.That((IntPtr)record->zeroBuffer, Is.EqualTo(new IntPtr(600)), "the copy's slot: what the draw reads");
            Assert.That(record->zeroOffset, Is.EqualTo(12u));
            Assert.That(record->zeroBytes, Is.Zero);
            Assert.That(ring.DropPrepared(), Is.EqualTo(1));
            Assert.Throws<ArgumentException>(() => ring.TryBeginIndirectCount(3, new IntPtr(300), 40, 1602, new IntPtr(400), 12, IntPtr.Zero, 0, 0, new IntPtr(123), 4000, 4, true), "no slot for the copy");
            Assert.Throws<ArgumentException>(() => ring.TryBeginIndirectCount(3, new IntPtr(300), 40, 1602, new IntPtr(400), 12, new IntPtr(600), 0, 1024, new IntPtr(123), 4000, 4, true), "a range to zero is not a slot");
        }

        [Test]
        public unsafe void DirectClipGroup_CopiesPayloadsAndTracksOnlyItsCompletion_UntilConsumedOrDiscarded()
        {
            var ring = Track(new VpNativeDrawData(4, 4, 2, 20000));
            var draw = new Plugin.DirectCommand { indexCount = 6, instanceCount = 1, firstIndex = 3, firstInstance = 1 };
            var constants = new VpNativeConstantBlock(new VpNativeShaderBinary.ConstantBuffer { Name = "VpNativeDirect", Size = 6656 });
            constants.SetDirectClipRecord(1, Matrix4x4.Translate(new Vector3(2,3,4)), VpInstanceClip.Keep(new Vector4(1,0,0,-5), 1));
            Assert.That(ring.TryBeginDirect(2, new IntPtr(123), 40, 4, new[] { draw }), Is.True);
            ring.AddViewport(new Rect(0,0,32,32)); ring.SetConstants(0, constants);
            IntPtr source = ring.Finish(out _);
            Assert.That(ring.TryBegin(1,new IntPtr(124),0,1,new IntPtr(123),40,4), Is.True);
            ring.AddViewport(new Rect(0,0,32,32)); ring.Finish(out _);
            var groups = new List<IntPtr>();
            Assert.That(ring.GroupPrepared(groups), Is.EqualTo(1));
            Assert.That(groups[0], Is.EqualTo(source), "the group reuses an unrecorded source slot safely");
            Assert.That(ring.InFlight, Is.EqualTo(1));
            var root = (Plugin.Draw*)groups[0];
            Assert.That(VpNativeDrawData.ModeOf(groups[0]), Is.EqualTo(Plugin.DrawMode.Group));
            var ext = (Plugin.DrawExtension*)((byte*)root+sizeof(Plugin.Draw));
            Assert.That(ext->commandsCount, Is.EqualTo(2));
            var offsets = (uint*)((byte*)root+ext->commandsOffset);
            var child = (Plugin.Draw*)((byte*)root+offsets[0]);
            Assert.That(child->totalBytes+offsets[0], Is.LessThanOrEqualTo(root->totalBytes));
            var direct = (Plugin.DrawExtension*)((byte*)child+sizeof(Plugin.Draw));
            var copied = (Plugin.DirectCommand*)((byte*)child+direct->commandsOffset);
            Assert.That(copied->firstIndex, Is.EqualTo(3)); Assert.That(copied->firstInstance, Is.EqualTo(1));
            var cb = (Plugin.Constants*)((byte*)child+child->constantsOffset);
            float* record = (float*)((byte*)child+cb->offset+208);
            Assert.That(record[12], Is.EqualTo(2)); Assert.That(record[19], Is.EqualTo(-5)); Assert.That(record[48], Is.EqualTo(1));
            Assert.That(ring.RecordPrepared(null,IntPtr.Zero,0), Is.EqualTo(1));
            ring.SetConsumedSentinel(100);
            Assert.That(ring.InFlight, Is.EqualTo(1), "a recorded group needs its own completion or submission sentinel");
            root->consumed=1;
            Assert.That(ring.InFlight, Is.Zero);
        }

        [Test]
        public void DirectClipGroup_EmptyListDoesNotTakeOrUploadAnyBlock_AndTooManyDirectRecordsAreRejected()
        {
            var ring=Track(new VpNativeDrawData(2,4,2,1000));
            var groups=new List<IntPtr>();
            Assert.That(ring.GroupPrepared(groups), Is.Zero); Assert.That(ring.Taken, Is.Zero);
            Assert.Throws<ArgumentOutOfRangeException>(()=>ring.TryBeginDirect(1,new IntPtr(1),40,4,new Plugin.DirectCommand[33]));
        }

        private static VpNativeShaderBinary Sample()
        {
            var binary = new VpNativeShaderBinary { Name = "sample", Shader = "Zantetsu/Sample", Keywords = "A B", UnityVersion = "6000.3.22f1" };
            var vertex = new VpNativeShaderBinary.Stage { Kind = VpNativeShaderBinary.StageKind.Vertex, Bytecode = new byte[] { 1, 2, 3, 4 } };
            var globals = new VpNativeShaderBinary.ConstantBuffer { Name = "$Globals", Register = 0, Size = 2208 };
            globals.Constants.Add(new VpNativeShaderBinary.Constant { Name = "unity_MatrixVP", Offset = 1264, Rows = 4, Columns = 4 });
            globals.Constants.Add(new VpNativeShaderBinary.Constant { Name = "_VpInstanceMultiplier", Offset = 2148, Rows = 1, Columns = 1 });
            vertex.ConstantBuffers.Add(globals);
            vertex.ConstantBuffers.Add(new VpNativeShaderBinary.ConstantBuffer { Name = "VpNativeCommand", Register = 1, Size = 16 });
            vertex.Buffers.Add(new VpNativeShaderBinary.BufferBinding { Name = "_VpNativeArguments", Register = 0 });
            vertex.Buffers.Add(new VpNativeShaderBinary.BufferBinding { Name = "_VpVertices", Register = 1 });
            binary.Stages.Add(vertex);
            var pixel = new VpNativeShaderBinary.Stage { Kind = VpNativeShaderBinary.StageKind.Pixel, Bytecode = new byte[] { 9, 8, 7 } };
            pixel.Textures.Add(new VpNativeShaderBinary.TextureBinding { Name = "_BaseMap", Register = 4, SamplerRegister = 4, Dimension = (int)TextureDimension.Tex2D, ArraySize = 1 });
            pixel.Textures.Add(new VpNativeShaderBinary.TextureBinding { Name = "_MainLightShadowmapTexture", Register = 1, SamplerRegister = -1, Dimension = (int)TextureDimension.Tex2D, ArraySize = 1 });
            pixel.Samplers.Add(new VpNativeShaderBinary.SamplerBinding { Name = "", Register = 1 });
            binary.Stages.Add(pixel);
            return binary;
        }

        [Test]
        public void ShaderBinary_ReadsBackAsWritten_AndRefusesAnotherVersion()
        {
            VpNativeShaderBinary sample = Sample();
            byte[] bytes = sample.Write();
            Assert.That(VpNativeShaderBinary.TryRead(bytes, out VpNativeShaderBinary read, out string failure), Is.True, failure);
            Assert.That(read.Describe(), Is.EqualTo(sample.Describe()));
            Assert.That(read.FindStage(VpNativeShaderBinary.StageKind.Vertex).Bytecode, Is.EqualTo(new byte[] { 1, 2, 3, 4 }));
            Assert.That(read.FindStage(VpNativeShaderBinary.StageKind.Pixel).Textures[1].SamplerRegister, Is.EqualTo(-1));
            Assert.That(read.FindStage(VpNativeShaderBinary.StageKind.Vertex).FindConstantBuffer("$Globals").Find("unity_MatrixVP").Offset, Is.EqualTo(1264));

            byte[] otherVersion = (byte[])bytes.Clone();
            otherVersion[4] = 99;
            Assert.That(VpNativeShaderBinary.TryRead(otherVersion, out _, out failure), Is.False);
            Assert.That(failure, Does.Contain("version 99"));
            Assert.That(VpNativeShaderBinary.TryRead(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, out _, out failure), Is.False);
            Assert.That(failure, Does.Contain("magic"));
        }

        [TestCase("forward-plugin-noshadow", "_VpVisible", "_BaseMap")]
        [TestCase("forward-plugin-cascade-soft", "_VpVisible", "_MainLightShadowmapTexture")]
        [TestCase("caster-plugin", "_VpCullCommands", null)]
        [TestCase("forward-plugin-cascade-stereo", "_VpVisible", "_MainLightShadowmapTexture")]
        public void BakedAssets_AreInTheBuild_WithThePluginsBindings(string variant, string vertexBuffer, string pixelTexture)
        {
            var asset = Resources.Load<TextAsset>(VpNativeShaderBinary.ResourceFolder + "/" + variant);
            Assert.That(asset, Is.Not.Null, "Resources/" + VpNativeShaderBinary.ResourceFolder + "/" + variant + ".bytes (VpNativeShaderBake)");
            Assert.That(VpNativeShaderBinary.TryRead(asset.bytes, out VpNativeShaderBinary binary, out string failure), Is.True, failure);
            VpNativeShaderBinary.Stage vertex = binary.FindStage(VpNativeShaderBinary.StageKind.Vertex);
            Assert.That(vertex, Is.Not.Null);
            Assert.That(vertex.Bytecode.Length, Is.GreaterThan(1000));
            Assert.That(BitConverter.ToUInt32(vertex.Bytecode, 0), Is.EqualTo(0x43425844u), "DXBC from the first byte");
            Assert.That(vertex.FindConstantBuffer(VpNativePipeline.CommandConstantBuffer), Is.Not.Null, "the per-command root constant");
            Assert.That(vertex.FindConstantBuffer("$Globals").Find("unity_BaseCommandID"), Is.Null, "Unity's per-draw command id is not read");
            Assert.That(vertex.Buffers.Exists(b => b.Name == "_VpNativeArguments" && b.Register == 0), "the plugin's entries at t0");
            Assert.That(vertex.Buffers.Exists(b => b.Name == vertexBuffer), vertexBuffer);
            Assert.That(vertex.Buffers.Exists(b => b.Name == "unity_IndirectDrawArgs"), Is.False, "Unity's argument buffer is not read");
            if (pixelTexture != null)
            {
                VpNativeShaderBinary.Stage pixel = binary.FindStage(VpNativeShaderBinary.StageKind.Pixel);
                Assert.That(pixel.Textures.Exists(t => t.Name == pixelTexture), pixelTexture);
            }

            if (variant.EndsWith("-stereo"))
            {
                // Unity's own Single Pass Instanced code: the eyes' matrices from the stereo view block (Unity 6 names
                // it UnityStereoViewBuffer), not a matrix of the pass's.
                VpNativeShaderBinary.ConstantBuffer stereo = vertex.FindConstantBuffer("UnityStereoViewBuffer");
                Assert.That(stereo, Is.Not.Null, "the stereo block");
                Assert.That(stereo.Find("unity_StereoMatrixVP"), Is.Not.Null);
                Assert.That(stereo.Find("unity_StereoMatrixVP").ArraySize, Is.EqualTo(2));
                Assert.That(vertex.FindConstantBuffer("$Globals")?.Find("unity_MatrixVP"), Is.Null, "the mono matrix is not read");
                TestContext.WriteLine(binary.Describe());
            }
        }

        [Test]
        public void ConstantBlock_WritesEachConstantAtItsOffset_AndSaysWhichAreUnused()
        {
            var layout = new VpNativeShaderBinary.ConstantBuffer { Name = "$Globals", Size = 160 };
            layout.Constants.Add(new VpNativeShaderBinary.Constant { Name = "_Scale", Offset = 16, Rows = 1, Columns = 4 });
            layout.Constants.Add(new VpNativeShaderBinary.Constant { Name = "_Matrix", Offset = 64, Rows = 4, Columns = 4 });
            layout.Constants.Add(new VpNativeShaderBinary.Constant { Name = "_Count", Offset = 140, Rows = 1, Columns = 1 });
            var block = new VpNativeConstantBlock(layout);
            Assert.That(block.TrySet("_Scale", new Vector4(1f, 2f, 3f, 4f)), Is.True);
            var matrix = Matrix4x4.identity;
            matrix.m01 = 5f; // row 0, column 1: the second column's first value
            Assert.That(block.TrySet("_Matrix", matrix), Is.True);
            Assert.That(block.TrySet("_Count", 7), Is.True);
            Assert.That(block.TrySet("_NotThere", 1f), Is.False, "a constant the stage does not read is not written");
            Assert.That(block.Uses("_NotThere"), Is.False);

            Assert.That(BitConverter.ToSingle(block.Bytes, 16), Is.EqualTo(1f));
            Assert.That(BitConverter.ToSingle(block.Bytes, 28), Is.EqualTo(4f));
            Assert.That(BitConverter.ToSingle(block.Bytes, 64), Is.EqualTo(1f), "column 0 first");
            Assert.That(BitConverter.ToSingle(block.Bytes, 64 + 16), Is.EqualTo(5f), "column 1 begins at the second register: m01 is its first value");
            Assert.That(BitConverter.ToSingle(block.Bytes, 64 + 16 + 4), Is.EqualTo(1f), "m11");
            Assert.That(BitConverter.ToInt32(block.Bytes, 140), Is.EqualTo(7));
            // The non-zero bytes of what was written: 1f, 3f, 4f and 5f hold two each, 2f one, the int one.
            int nonZero = 0;
            foreach (byte b in block.Bytes) nonZero += b != 0 ? 1 : 0;
            Assert.That(nonZero, Is.EqualTo(4 * 2 + 2 + (2 + 1 + 2 + 2) + 1), "nothing else is written");
        }

        [Test]
        public void DrawData_LaysOutAnIssue_AndReusesABlockOnlyOnceAnswered()
        {
            GraphicsBuffer arguments = Track(new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 8, Plugin.ArgumentStride));
            GraphicsBuffer indices = Track(new GraphicsBuffer(GraphicsBuffer.Target.Index, 30, sizeof(uint)));
            GraphicsBuffer vertices = Track(new GraphicsBuffer(GraphicsBuffer.Target.Structured, 10, 16));
            var layout = new VpNativeShaderBinary.ConstantBuffer { Name = "UnityPerMaterial", Size = 48 };
            layout.Constants.Add(new VpNativeShaderBinary.Constant { Name = "_BaseColor", Offset = 16, Rows = 1, Columns = 4 });
            var constants = new VpNativeConstantBlock(layout);
            constants.TrySet("_BaseColor", new Vector4(0.5f, 0.25f, 1f, 1f));

            VpNativeDrawData data = Track(new VpNativeDrawData(2, 4, 2, 256));
            Assert.That(data.TryBegin(3, arguments, 2, 5, indices), Is.True);
            data.AddViewport(new Rect(0f, 0f, 640f, 480f));
            data.SetBuffer(0, arguments, true);
            data.SetBuffer(1, vertices, false);
            data.SetConstants(2, constants);
            IntPtr first = data.Finish(out uint serial);
            Assert.That(serial, Is.EqualTo(1u));
            Assert.That(VpNativeDrawData.ResultOf(first), Is.Null, "not answered until the plugin ran");
            Assert.That(VpNativeDrawData.SerialOf(first), Is.EqualTo(1u));

            // The header as the plugin reads it.
            unsafe
            {
                var draw = (Plugin.Draw*)first;
                Assert.That(draw->version, Is.EqualTo((uint)Plugin.ContractVersion));
                Assert.That(draw->pipeline, Is.EqualTo(3u));
                Assert.That(draw->argumentOffset, Is.EqualTo(2u * Plugin.ArgumentStride));
                Assert.That(draw->commandCount, Is.EqualTo(5u));
                Assert.That(draw->indexBufferBytes, Is.EqualTo(120u));
                Assert.That(draw->indexFormat, Is.EqualTo(Plugin.DxgiR32Uint));
                Assert.That(draw->viewportCount, Is.EqualTo(1u));
                var viewport = (Plugin.Viewport*)draw->viewports;
                Assert.That(viewport->width, Is.EqualTo(640f));
                Assert.That(viewport->scissorBottom, Is.EqualTo(480));
                Assert.That(draw->resourceCount, Is.EqualTo(2u));
                Assert.That(draw->constantsCount, Is.EqualTo(1u));
                var resources = (Plugin.Resource*)((byte*)first + draw->resourcesOffset);
                Assert.That(resources[0].kind, Is.EqualTo((uint)Plugin.ResourceKind.RawBuffer));
                Assert.That(resources[0].elementCount, Is.EqualTo(8u * Plugin.ArgumentStride / 4u), "raw views count words");
                Assert.That(resources[1].kind, Is.EqualTo((uint)Plugin.ResourceKind.StructuredBuffer));
                Assert.That(resources[1].stride, Is.EqualTo(16u));
                Assert.That(resources[1].elementCount, Is.EqualTo(10u));
                var blocks = (Plugin.Constants*)((byte*)first + draw->constantsOffset);
                Assert.That(blocks[0].binding, Is.EqualTo(2u));
                Assert.That(blocks[0].size, Is.EqualTo(48u));
                Assert.That(blocks[0].offset + blocks[0].size, Is.LessThanOrEqualTo(draw->totalBytes));
                float g = *(float*)((byte*)first + blocks[0].offset + 16 + 4);
                Assert.That(g, Is.EqualTo(0.25f), "the constant bytes follow, as written");
            }

            Assert.That(data.TryBegin(3, arguments, 0, 1, indices), Is.True, "the second block");
            data.AddViewport(new Rect(0f, 0f, 1f, 1f));
            IntPtr second = data.Finish(out _);
            Assert.That(second, Is.Not.EqualTo(first));
            Assert.That(data.TryBegin(3, arguments, 0, 1, indices), Is.False, "both blocks are with the plugin: nothing waits, the issue is refused");
            Assert.That(data.RingFull, Is.EqualTo(1));

            // The plugin answers the first: it can be taken again.
            unsafe
            {
                var draw = (Plugin.Draw*)first;
                draw->result = (uint)Plugin.Result.Recorded;
                draw->consumed = 1;
            }

            Assert.That(VpNativeDrawData.ResultOf(first), Is.EqualTo(Plugin.Result.Recorded));
            Assert.That(data.TryBegin(3, arguments, 0, 1, indices), Is.True);
            data.AddViewport(new Rect(0f, 0f, 1f, 1f));
            Assert.That(data.Finish(out uint third), Is.EqualTo(first));
            Assert.That(third, Is.EqualTo(3u));
        }

        [Test]
        public void CommandCompaction_FirstSlotIsZero_AndADiagnosisMayMoveIt_WithinTheSlots()
        {
            // Diagnosis of 2026-10-10: the static scene's one run drawn from count slot 1 (countBufferOffset 4), the
            // condition of the city walk's second run; 0 is the product's, and the run's line says when it is not.
            int was = VpCommandCompaction.FirstSlot;
            try
            {
                Assert.That(was, Is.EqualTo(0));
                Assert.That(VpCommandCompaction.DescribeRun(), Does.Not.Contain("FIRST SLOT"));
                VpCommandCompaction.FirstSlot = 1;
                Assert.That(VpCommandCompaction.FirstSlot, Is.EqualTo(1));
                Assert.That(VpCommandCompaction.DescribeRun(), Does.Contain("FIRST SLOT 1"));
                Assert.Throws<System.ArgumentOutOfRangeException>(() => VpCommandCompaction.FirstSlot = VpCommandCompaction.Slots);
                Assert.Throws<System.ArgumentOutOfRangeException>(() => VpCommandCompaction.FirstSlot = -1);
                Assert.That(VpCommandCompaction.FirstSlot, Is.EqualTo(1));
            }
            finally
            {
                VpCommandCompaction.FirstSlot = was;
            }
        }

        [Test]
        public void CommandCompaction_IsAskedForByDefault_AndTheFixedArgumentIsTheWayBack()
        {
            // DESIGN 4.5.10 (D-217): asked for unless the launch says the fixed draw; asking is not taking.
            Assert.That(Plugin.ReadIndirectCountRequest(null), Is.True);
            Assert.That(Plugin.ReadIndirectCountRequest(new[] { "player", "-zantetsuVp3cNative" }), Is.True);
            Assert.That(Plugin.ReadIndirectCountRequest(new[] { "player", Plugin.CompactCommandsArgument }), Is.True, "accepted, and the default anyway");
            Assert.That(Plugin.ReadIndirectCountRequest(new[] { "player", Plugin.FixedCommandsArgument }), Is.False);
            Assert.That(Plugin.ReadIndirectCountRequest(new[] { "player", Plugin.CompactCommandsArgument, Plugin.FixedCommandsArgument }), Is.False, "the fixed draw wins");
            Assert.That(Plugin.CompactCommandsArgument, Is.EqualTo("-zantetsuVpCompactCommands"));
            Assert.That(Plugin.FixedCommandsArgument, Is.EqualTo("-zantetsuVpFixedCommands"));

            // The Direct3D 12 route (DESIGN 4.5.11, D-219): the other way round -- the fixed draw unless the compaction
            // is asked for, and the fixed argument wins there too.
            Assert.That(Plugin.ReadDirect3D12IndirectCountRequest(null), Is.False);
            Assert.That(Plugin.ReadDirect3D12IndirectCountRequest(new[] { "player", "-zantetsuVp3cNative" }), Is.False, "the fixed draw is that route's default");
            Assert.That(Plugin.ReadDirect3D12IndirectCountRequest(new[] { "player", Plugin.CompactCommandsArgument }), Is.True, "what asks for the compaction there");
            Assert.That(Plugin.ReadDirect3D12IndirectCountRequest(new[] { "player", Plugin.FixedCommandsArgument }), Is.False);
            Assert.That(Plugin.ReadDirect3D12IndirectCountRequest(new[] { "player", Plugin.CompactCommandsArgument, Plugin.FixedCommandsArgument }), Is.False, "the fixed draw wins");
            Assert.That(VpCommandCompaction.VerifyArgument, Is.EqualTo("-zantetsuVpCompactVerify"));

            // What the Vulkan route is asked changes nothing off a Vulkan device; the choice there is the Direct3D 12
            // route's on Direct3D 12, and no route's on any other device -- always with its reason.
            Assume.That(Plugin.Vulkan, Is.False, "this case is the Editor's (Direct3D)");
            bool asked = Plugin.VulkanIndirectCount, asked12 = Plugin.Direct3D12IndirectCount;
            try
            {
                Assert.That(Plugin.IndirectCountAsked, Is.EqualTo(asked12), "off Vulkan the request is the Direct3D 12 route's");
                foreach (bool vulkanAsked in new[] { true, false })
                {
                    Plugin.VulkanIndirectCount = vulkanAsked;
                    Plugin.Direct3D12IndirectCount = false;
                    Assert.That(VpCommandCompaction.TrySelect(out string reason), Is.False);
                    TestContext.Out.WriteLine(SystemInfo.graphicsDeviceType + ", not asked: " + reason);
                    Assert.That(reason, Does.StartWith("fixed:"));
                    if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12)
                    {
                        Assert.That(reason, Does.StartWith("fixed: the default of the Direct3D 12 route").And.Contain(Plugin.CompactCommandsArgument).And.Contain(Plugin.FixedCommandsArgument));
                    }
                    else
                    {
                        Assert.That(reason, Does.Contain("neither a Vulkan nor a Direct3D 12 device"));
                    }
                }

                Plugin.Direct3D12IndirectCount = true;
                bool taken = VpCommandCompaction.TrySelect(out string askedReason);
                TestContext.Out.WriteLine(SystemInfo.graphicsDeviceType + ", asked: " + askedReason);
                if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12)
                {
                    // This Editor's device and library: the plugin draws here, has the count draw, and the device has waves.
                    Assert.That(taken, Is.True, askedReason);
                    Assert.That(askedReason, Does.StartWith("compaction: asked for on the Direct3D 12 route").And.Contain(Plugin.CompactCommandsArgument).And.Contain("lanes").And.Contain("one set a selection"));
                    Assert.That(Plugin.IndirectCountReady, Is.True);
                    Assert.That(Plugin.TryReadIndirectCountInfo(out bool waves, out uint least, out uint most, out _, out _), Is.True);
                    Assert.That(waves, Is.True);
                    Assert.That(least, Is.GreaterThanOrEqualTo(4u).And.LessThanOrEqualTo(most));
                    Assert.That(most, Is.LessThanOrEqualTo(128u));
                }
                else
                {
                    Assert.That(taken, Is.False);
                    Assert.That(askedReason, Does.StartWith("fixed:").And.Contain("neither a Vulkan nor a Direct3D 12 device"));
                }
            }
            finally
            {
                Plugin.VulkanIndirectCount = asked;
                Plugin.Direct3D12IndirectCount = asked12;
            }
        }

        [Test]
        public void TheLaunchRecord_SaysWhatIsAskedOfTheCommandCompaction()
        {
            Assume.That(Plugin.Vulkan, Is.False, "this case is the Editor's (Direct3D)");
            string nothing = Zantetsu.Sandbox.SandboxLaunchRecord.DescribeCommandCompaction(new[] { "player.exe" });
            string compact = Zantetsu.Sandbox.SandboxLaunchRecord.DescribeCommandCompaction(new[] { "player.exe", Plugin.CompactCommandsArgument });
            string both = Zantetsu.Sandbox.SandboxLaunchRecord.DescribeCommandCompaction(new[] { "player.exe", Plugin.CompactCommandsArgument, Plugin.FixedCommandsArgument });
            string check = Zantetsu.Sandbox.SandboxLaunchRecord.DescribeCommandCompaction(new[] { "player.exe", Plugin.CompactCommandsArgument, VpCommandCompaction.VerifyArgument });
            TestContext.Out.WriteLine(nothing + "\n" + compact + "\n" + both + "\n" + check);
            string fixedDraw = Zantetsu.Sandbox.SandboxLaunchRecord.DescribeCommandCompaction(new[] { "player.exe", Plugin.FixedCommandsArgument });
            TestContext.Out.WriteLine(fixedDraw);
            Assert.That(nothing, Does.StartWith("no (").And.Contain("the Direct3D 12 route's default: the fixed draw"));
            Assert.That(compact, Does.StartWith("yes (").And.Contain(Plugin.CompactCommandsArgument).And.Not.Contain("the default anyway"));
            Assert.That(both, Does.StartWith("no (").And.Contain(Plugin.FixedCommandsArgument));
            Assert.That(fixedDraw, Does.StartWith("no (").And.Contain(Plugin.FixedCommandsArgument));
            Assert.That(check, Does.StartWith("yes (").And.Contain("a check run"));
            Assert.That(Zantetsu.Sandbox.SandboxLaunchRecord.Describe(new[] { "player.exe" }), Does.Contain("; command compaction asked: no ("));
        }

        [Test]
        public void Compaction_OnTheDirect3D12Route_ASetIsTwoColumnsWhoseCountsACopyZeroes_AndASelectionKeepsOne()
        {
            // DESIGN 4.5.11: no motion column, no copy of the counts, one set a selection whatever the frame, and count
            // buffers made as a copy's destination; the set is given up through the owner's hand-over like the rest.
            Assume.That(Plugin.Vulkan, Is.False, "this case is the Editor's (Direct3D)");
            Assert.That(VpCommandCompaction.Layout.Columns, Is.EqualTo(2));
            Assert.That(VpCommandCompaction.Layout.Sets, Is.EqualTo(1));
            Assert.That(VpCommandCompaction.Layout.DrawCounts, Is.False);
            Assert.That(VpCommandCompaction.Layout.CountsZeroedByCopy, Is.True);
            Assert.That(VpCommandCompaction.ZeroesByCopy, Is.True);
            Assert.That(VpIndexedIndirectDrawBatch.CompactionSets, Is.EqualTo(1));
            Assert.That(VpIndexedIndirectDrawBatch.Compaction.BytesFor(65536, 24), Is.EqualTo(2L * ((65536L * 24) + (256 * 4))), "two columns of entries and of counts");
            Assert.That(VpGpuCullSetup.TryCreate(2, true, out VpGpuCullSetup setup, out string failure), Is.True, failure);
            var batch = new VpIndexedIndirectDrawBatch(4, 8, null, setup);
            var expected = new HashSet<GraphicsBuffer>();
            var given = new List<GraphicsBuffer>();
            try
            {
                VpIndexedIndirectDrawBatch.Compaction first = batch.CompactionOf(0, 0);
                for (int frame = 0; frame < 6; frame++)
                {
                    VpIndexedIndirectDrawBatch.Compaction set = batch.CompactionOf(0, frame);
                    Assert.That(ReferenceEquals(set, first), Is.True, "one set a view, every frame");
                    for (int column = 0; column < 2; column++)
                    {
                        Assert.That(set.Entries(column), Is.Not.Null);
                        Assert.That(set.Counts(column), Is.Not.Null);
                        Assert.That(set.Counts(column).target & GraphicsBuffer.Target.CopyDestination, Is.EqualTo(GraphicsBuffer.Target.CopyDestination), "the counts are zeroed by a copy");
                        Assert.That(set.Counts(column).count * set.Counts(column).stride, Is.EqualTo(VpCommandCompaction.Slots * sizeof(uint)));
                        Assert.That(set.DrawCounts(column), Is.Null, "the draws read what the compute wrote");
                        expected.Add(set.Entries(column));
                        expected.Add(set.Counts(column));
                    }

                    Assert.That(set.Entries(2), Is.Null, "no motion column");
                    Assert.That(set.Counts(2), Is.Null);
                }

                Assert.That(ReferenceEquals(batch.CompactionOf(1, 0), first), Is.False, "another view's set is its own");
                for (int column = 0; column < 2; column++)
                {
                    expected.Add(batch.CompactionOf(1, 3).Entries(column));
                    expected.Add(batch.CompactionOf(1, 3).Counts(column));
                }

                Assert.That(expected.Count, Is.EqualTo(2 * 4), "two views' sets of two columns of entries and counts");
                using (GraphicsBuffer zeroes = VpCommandCompaction.NewZeroSource())
                {
                    Assert.That(zeroes.target & GraphicsBuffer.Target.CopySource, Is.EqualTo(GraphicsBuffer.Target.CopySource));
                    Assert.That(zeroes.count * zeroes.stride, Is.EqualTo(first.Counts(0).count * first.Counts(0).stride), "a copy of the whole count buffer");
                }

                batch.Dispose(given.Add);
                foreach (GraphicsBuffer buffer in expected)
                {
                    Assert.That(given.Contains(buffer), Is.True, "every compaction buffer is handed over");
                    Assert.That(buffer.IsValid(), Is.True, "and none is disposed by the batch itself");
                }
            }
            finally
            {
                batch.Dispose();
                foreach (GraphicsBuffer buffer in given) buffer.Dispose();
            }
        }

        [Test]
        public void Compaction_SetsAreGivenUpThroughTheOwnersHandOver_AndASelectionsOwnEndWithIt()
        {
            // The compaction's GPU buffers live and end as the selection's own do: the batch's are handed to the
            // owner's give-up (the keeper that waits on issued events), never disposed under an event; a caller-owned
            // selection disposes its sets with itself. And what a set holds at the product's capacity is said.
            // The sets are the Quest 3S Vulkan route's (DESIGN 4.5.10), made here on the Editor's device.
            VpCommandCompaction.LayoutForTest = VpCommandCompaction.SetLayout.Vulkan;
            Assert.That(VpIndexedIndirectDrawBatch.Compaction.BytesFor(65536, 24), Is.EqualTo(3L * ((65536L * 24) + (2 * 256 * 4))), "entries, counts and the counts' copies");
            Assert.That(VpIndexedIndirectDrawBatch.CompactionRing, Is.EqualTo(4));
            Assert.That(VpIndexedIndirectDrawBatch.CompactionSets, Is.EqualTo(4));
            Assert.That(VpCommandCompaction.ZeroesByCopy, Is.False, "the Vulkan route's counts are zeroed by the plugin's event");
            Assert.That(VpGpuCullSetup.TryCreate(2, true, out VpGpuCullSetup setup, out string failure), Is.True, failure);
            var batch = new VpIndexedIndirectDrawBatch(4, 8, null, setup);
            var expected = new HashSet<GraphicsBuffer>();
            var given = new List<GraphicsBuffer>();
            try
            {
                for (int frame = 0; frame < 6; frame++)
                {
                    VpIndexedIndirectDrawBatch.Compaction set = batch.CompactionOf(0, frame);
                    for (int column = 0; column < 3; column++)
                    {
                        Assert.That(set.Entries(column), Is.Not.Null);
                        expected.Add(set.Entries(column));
                        expected.Add(set.Counts(column));
                        Assert.That(set.DrawCounts(column), Is.Not.Null.And.Not.SameAs(set.Counts(column)), "the draws read a copy, not what the compute wrote");
                        expected.Add(set.DrawCounts(column));
                    }
                }

                Assert.That(expected.Count, Is.EqualTo(4 * 9), "four sets of three columns of entries, counts and the counts' copies, however many frames ask");

                VpIndexedIndirectDrawBatch.CullOutput output = batch.CreateCullOutput();
                VpIndexedIndirectDrawBatch.Compaction own = batch.CompactionOf(output, 0);
                GraphicsBuffer ownEntries = own.Entries(2), ownCounts = own.Counts(2);
                Assert.That(expected.Contains(ownEntries), Is.False, "a selection's own sets are not a view's");
                output.Dispose();
                Assert.That(ownEntries.IsValid(), Is.False, "the selection's sets end with it");
                Assert.That(ownCounts.IsValid(), Is.False);

                batch.Dispose(given.Add);
                foreach (GraphicsBuffer buffer in expected)
                {
                    Assert.That(given.Contains(buffer), Is.True, "every compaction buffer is handed over");
                    Assert.That(buffer.IsValid(), Is.True, "and none is disposed by the batch itself");
                }
            }
            finally
            {
                batch.Dispose();
                foreach (GraphicsBuffer buffer in given) buffer.Dispose();
            }
        }

        [Test]
        public void Compaction_RefusesARun_WhoseColumnWasNotReservedForZeroingThisFrame()
        {
            // The experiment's gate (TL review, 2026-10-10): a frame whose zero event could not be taken for a column
            // never compacts into that column -- the batch refuses before any dispatch, whatever the device. The sets
            // are the Vulkan route's ring of four here; the gate is every route's.
            VpCommandCompaction.LayoutForTest = VpCommandCompaction.SetLayout.Vulkan;
            Assert.That(VpGpuCullSetup.TryCreate(1, true, out VpGpuCullSetup setup, out string failure), Is.True, failure);
            var cube = new Bounds(Vector3.zero, Vector3.one);
            var commands = new[] { new VpIndirectCommand(new VpGeometryRange(0, 36, 100, 24), cube, 2), new VpIndirectCommand(new VpGeometryRange(36, 12, 200, 8), cube, 1) };
            var transforms = new[] { Matrix4x4.identity, Matrix4x4.identity, Matrix4x4.identity };
            using (var batch = new VpIndexedIndirectDrawBatch(4, 8, null, setup))
            using (var buffer = new CommandBuffer())
            {
                batch.WriteWholeOnce();
                Assert.That(batch.TryUpload(commands, transforms, false), Is.True, "upload");
                batch.NativeDrawBuffers(0, out GraphicsBuffer forwardArguments, out _, out _, out _, out _, out _, out _);
                VpIndexedIndirectDrawBatch.Compaction set = batch.CompactionOf(0, 10);
                Assert.That(ReferenceEquals(set, batch.CompactionOf(0, 14)), Is.True, "one set every four frames");
                Assert.That(ReferenceEquals(set, batch.CompactionOf(0, 11)), Is.False, "the next frame's set is another");
                Assert.That(set.ZeroReserved(0, 10), Is.False);
                var refused = Assert.Throws<InvalidOperationException>(() => batch.IssueCompaction(buffer, set, forwardArguments, 0, 0, 2, 0, 10));
                Assert.That(refused.Message, Does.Contain("not reserved for zeroing"));
                set.MarkZeroReserved(1, 10);
                Assert.That(set.ZeroReserved(0, 10), Is.False, "another column's reservation does not count");
                Assert.Throws<InvalidOperationException>(() => batch.IssueCompaction(buffer, set, forwardArguments, 0, 0, 2, 0, 10));
                set.MarkZeroReserved(0, 10);
                Assert.That(set.ZeroReserved(0, 10), Is.True);
                Assert.That(set.ZeroReserved(0, 11), Is.False, "a reservation is one frame's");
                // Past the gate: on a device with the wave kernel (Direct3D 12) the compaction is recorded; on one
                // without (Direct3D 11) the compaction itself is what refuses now.
                if (VpCommandCompaction.TryLoad(out string _))
                {
                    Assert.DoesNotThrow(() => batch.IssueCompaction(buffer, set, forwardArguments, 0, 0, 2, 0, 10));
                }
                else
                {
                    var passed = Assert.Throws<InvalidOperationException>(() => batch.IssueCompaction(buffer, set, forwardArguments, 0, 0, 2, 0, 10));
                    Assert.That(passed.Message, Does.Not.Contain("not reserved for zeroing"));
                    Assert.That(passed.Message, Does.Contain(VpCommandCompaction.ShaderResource));
                }
            }
        }

        [Test]
        public void NativeBatch_WritesNumberedEntries_ThatUnitysDrawCannotRead()
        {
            Assert.That(VpGpuCullSetup.TryCreate(1, true, out VpGpuCullSetup setup, out string failure), Is.True, failure);
            Assert.That(setup.NativeArguments, Is.True);
            Assert.That(setup.ArgumentStride, Is.EqualTo(24));
            Assert.That(setup.ArgumentOffset, Is.EqualTo(4));
            Assert.That(VpGpuCullSetup.TryCreate(1, out VpGpuCullSetup unity, out failure), Is.True, failure);
            Assert.That(unity.ArgumentStride, Is.EqualTo(20));

            var cube = new Bounds(Vector3.zero, Vector3.one);
            var commands = new[]
            {
                new VpIndirectCommand(new VpGeometryRange(0, 36, 100, 24), cube, 2),
                new VpIndirectCommand(new VpGeometryRange(36, 12, 200, 8), cube, 0),
                new VpIndirectCommand(new VpGeometryRange(48, 6, 300, 4), cube, 3),
            };
            var transforms = new[]
            {
                Matrix4x4.Translate(new Vector3(0f, 0f, 5f)), Matrix4x4.Translate(new Vector3(0f, 0f, -5f)),
                Matrix4x4.Translate(new Vector3(1f, 0f, 5f)), Matrix4x4.Translate(new Vector3(0f, 1f, 5f)), Matrix4x4.Translate(new Vector3(0f, 0f, 200f)),
            };
            using (var batch = new VpIndexedIndirectDrawBatch(4, 8, null, setup))
            {
                Assert.That(batch.NativeArguments, Is.True);
                batch.WriteWholeOnce();
                Assert.That(batch.TryUpload(commands, transforms, false), Is.True, "upload");
                batch.NativeDrawBuffers(0, out GraphicsBuffer forwardArguments, out GraphicsBuffer forwardVisible, out GraphicsBuffer shadowArguments, out _, out _, out _, out GraphicsBuffer cullCommands);
                Assert.That(forwardArguments.stride, Is.EqualTo(24));
                Assert.That(shadowArguments.stride, Is.EqualTo(24));
                Assert.That(cullCommands.stride, Is.EqualTo(VpCullCommand.Stride));

                var camera = new GameObject("VP Native Test Eye").AddComponent<Camera>();
                try
                {
                    camera.enabled = false;
                    camera.transform.position = Vector3.zero;
                    camera.fieldOfView = 60f;
                    camera.aspect = 1f;
                    camera.nearClipPlane = 0.3f;
                    camera.farClipPlane = 50f;
                    var conditions = new VpCullConditions();
                    conditions.SetEye(0, camera.worldToCameraMatrix, camera.projectionMatrix);
                    conditions.eyeCount = 1;
                    var buffer = new CommandBuffer { name = "VP Native Test Cull" };
                    try
                    {
                        batch.IssueCull(buffer, 0, conditions);
                        Graphics.ExecuteCommandBuffer(buffer);
                    }
                    finally
                    {
                        buffer.Dispose();
                    }
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(camera.gameObject);
                }

                // Every entry: its command's number, then index count, instance count, start index, base vertex, start instance.
                var words = new uint[4 * 6];
                forwardArguments.GetData(words, 0, 0, words.Length);
                Assert.That(new[] { words[0], words[6], words[12] }, Is.EqualTo(new uint[] { 0, 1, 2 }), "the command numbers");
                // A range is (vertex start, vertex count, index start, index count).
                Assert.That(new[] { words[1], words[3], words[4], words[5] }, Is.EqualTo(new uint[] { 24, 100, 0, 0 }), "command 0: index count and start, base vertex 0, start instance 0");
                Assert.That(words[2], Is.EqualTo(1u), "command 0 keeps the instance in front of the eye, not the one behind");
                Assert.That(new[] { words[7], words[8], words[9], words[11] }, Is.EqualTo(new uint[] { 8, 0, 200, 2 }), "command 1: no instance, its run starts at 2");
                Assert.That(new[] { words[13], words[15], words[17] }, Is.EqualTo(new uint[] { 4, 300, 2 }), "command 2: its range and start");
                Assert.That(words[14], Is.EqualTo(2u), "command 2 keeps two of three (one is past the far plane)");
                Assert.That(new[] { words[18], words[19], words[20] }, Is.EqualTo(new uint[] { 0, 0, 0 }), "the entry past the uploaded commands stays zero");
                var visible = new uint[8];
                forwardVisible.GetData(visible, 0, 0, 8);
                Assert.That(visible[0], Is.EqualTo(0u));
                Assert.That(new[] { visible[2], visible[3] }, Is.EqualTo(new uint[] { 2, 3 }));

                batch.ReadCullCountsForDiagnosis(0, out int bodyKept, out int castersKept);
                Assert.That(bodyKept, Is.EqualTo(3), "the diagnosis reads the numbered entries");
                Assert.That(castersKept, Is.EqualTo(5), "no split: every instance kept as a caster");
                Assert.That(() => batch.RenderForward(null, null, null, 0, 0, 1, null, 0), Throws.InstanceOf<Exception>(), "Unity's draw is not issued from these entries");
            }

            using (var unityBatch = new VpIndexedIndirectDrawBatch(4, 8, null, unity))
            {
                Assert.That(unityBatch.NativeArguments, Is.False);
                Assert.That(() => unityBatch.NativeDrawBuffers(0, out _, out _, out _, out _, out _, out _, out _), Throws.InvalidOperationException);
            }
        }

        [Test]
        public void Setup_IsAskedForByItsArgumentAlone()
        {
            Assert.That(VpNativeDrawSetup.Read(new[] { "player.exe", VpNativeDrawSetup.Argument }), Is.True);
            Assert.That(VpNativeDrawSetup.Read(new[] { "player.exe", VpGpuCullSetup.Argument }), Is.False);
            Assert.That(VpNativeDrawSetup.Read(null), Is.False);
        }

        /// <summary>
        /// The body's route by request, device and platform (D-215): with nothing asked, the plugin on a Windows
        /// Direct3D 12 Player and Unity's calls everywhere else -- another device, another platform, the Editor; the
        /// plugin anywhere when asked for (whether it can be had there is the world's matter, and its failure); Unity's
        /// calls anywhere when asked for, which wins over the plugin's argument.
        /// </summary>
        [Test]
        public void Setup_WithNothingAsked_TakesThePluginOnAWindowsDirect3D12Player_AndUnitysCallsElsewhere()
        {
            Assert.That(VpNativeDrawSetup.ReadRequest(new[] { "player.exe" }), Is.EqualTo(VpBodyRouteRequest.Default));
            Assert.That(VpNativeDrawSetup.ReadRequest(null), Is.EqualTo(VpBodyRouteRequest.Default));
            Assert.That(VpNativeDrawSetup.ReadRequest(new[] { "player.exe", VpNativeDrawSetup.Argument }), Is.EqualTo(VpBodyRouteRequest.Native));
            Assert.That(VpNativeDrawSetup.ReadRequest(new[] { "player.exe", VpNativeDrawSetup.UnityApiArgument }), Is.EqualTo(VpBodyRouteRequest.UnityApi));
            Assert.That(
                VpNativeDrawSetup.ReadRequest(new[] { "player.exe", VpNativeDrawSetup.Argument, VpNativeDrawSetup.UnityApiArgument }),
                Is.EqualTo(VpBodyRouteRequest.UnityApi), "the argument for Unity's calls wins");
            Assert.That(VpNativeDrawSetup.UnityApiArgument, Is.EqualTo("-zantetsuVp3cUnityApi"));

            VpNativeDrawSetup.Selection Of(VpBodyRouteRequest request, GraphicsDeviceType device, bool editor, RuntimePlatform platform)
            {
                VpNativeDrawSetup.Selection selection = VpNativeDrawSetup.Select(request, device, editor, platform);
                TestContext.Out.WriteLine(request + ", " + device + ", " + (editor ? "Editor" : platform.ToString()) + ": " + (selection.Native ? "the plugin" : "Unity's calls")
                    + (selection.Asked ? " (asked)" : " (default)") + " -- " + selection.Reason);
                return selection;
            }

            // Nothing asked.
            VpNativeDrawSetup.Selection player12 = Of(VpBodyRouteRequest.Default, GraphicsDeviceType.Direct3D12, false, RuntimePlatform.WindowsPlayer);
            Assert.That(player12.Native, Is.True, "a Windows Direct3D 12 Player draws through the plugin by default");
            Assert.That(player12.Asked, Is.False);
            Assert.That(player12.Reason, Does.Contain("default"));
            VpNativeDrawSetup.Selection player11 = Of(VpBodyRouteRequest.Default, GraphicsDeviceType.Direct3D11, false, RuntimePlatform.WindowsPlayer);
            Assert.That(player11.Native, Is.False, "Direct3D 11 is not forced onto the plugin");
            Assert.That(player11.Asked, Is.False);
            Assert.That(player11.Reason, Does.Contain("Direct3D11").And.Contain("Direct3D 12 only"));
            Assert.That(Of(VpBodyRouteRequest.Default, GraphicsDeviceType.Vulkan, false, RuntimePlatform.WindowsPlayer).Native, Is.False, "nor Vulkan");
            Assert.That(Of(VpBodyRouteRequest.Default, GraphicsDeviceType.Direct3D12, false, RuntimePlatform.LinuxPlayer).Native, Is.False, "nor another platform");
            Assert.That(Of(VpBodyRouteRequest.Default, GraphicsDeviceType.Vulkan, false, RuntimePlatform.Android).Native, Is.False);
            VpNativeDrawSetup.Selection editor12 = Of(VpBodyRouteRequest.Default, GraphicsDeviceType.Direct3D12, true, RuntimePlatform.WindowsEditor);
            Assert.That(editor12.Native, Is.False, "the Editor takes the plugin only when asked");
            Assert.That(editor12.Reason, Does.Contain("Editor"));

            // The plugin asked for: taken as the request anywhere -- that it cannot be had there is the world's failure.
            foreach ((GraphicsDeviceType device, bool editor, RuntimePlatform platform) in new[]
                     {
                         (GraphicsDeviceType.Direct3D12, false, RuntimePlatform.WindowsPlayer), (GraphicsDeviceType.Direct3D11, false, RuntimePlatform.WindowsPlayer),
                         (GraphicsDeviceType.Direct3D12, true, RuntimePlatform.WindowsEditor), (GraphicsDeviceType.Vulkan, false, RuntimePlatform.LinuxPlayer),
                     })
            {
                VpNativeDrawSetup.Selection asked = Of(VpBodyRouteRequest.Native, device, editor, platform);
                Assert.That(asked.Native, Is.True);
                Assert.That(asked.Asked, Is.True);
                Assert.That(asked.Reason, Does.Contain(VpNativeDrawSetup.Argument));
            }

            // Unity's calls asked for: the comparison's route, also where the plugin is the default.
            VpNativeDrawSetup.Selection unity12 = Of(VpBodyRouteRequest.UnityApi, GraphicsDeviceType.Direct3D12, false, RuntimePlatform.WindowsPlayer);
            Assert.That(unity12.Native, Is.False);
            Assert.That(unity12.Asked, Is.True);
            Assert.That(unity12.Reason, Does.Contain(VpNativeDrawSetup.UnityApiArgument));

            // The launch's record says the request and what it selects on this device (this Editor: Unity's calls unless asked).
            string byDefault = Zantetsu.Sandbox.SandboxLaunchRecord.DescribeBodyRoute(new[] { "player.exe" });
            string askedNative = Zantetsu.Sandbox.SandboxLaunchRecord.DescribeBodyRoute(new[] { "player.exe", VpNativeDrawSetup.Argument });
            TestContext.Out.WriteLine("launch record, nothing asked: " + byDefault + "\nlaunch record, the plugin asked: " + askedNative);
            Assert.That(byDefault, Does.StartWith("Default -> Unity's calls").And.Contain("Editor"));
            Assert.That(askedNative, Does.StartWith("Native -> VP3C-NATIVE").And.Contain(VpNativeDrawSetup.Argument));
            Assert.That(Zantetsu.Sandbox.SandboxLaunchRecord.Describe(new[] { "player.exe" }), Does.Contain("body route asked: Default -> ").And.Contain("graphics=" + SystemInfo.graphicsDeviceType));

            // The older switch: true asks for the plugin, false asks for Unity's calls (never the default).
            VpBodyRouteRequest before = VpNativeDrawSetup.Request;
            try
            {
                VpNativeDrawSetup.Requested = true;
                Assert.That(VpNativeDrawSetup.Request, Is.EqualTo(VpBodyRouteRequest.Native));
                VpNativeDrawSetup.Requested = false;
                Assert.That(VpNativeDrawSetup.Request, Is.EqualTo(VpBodyRouteRequest.UnityApi));
                Assert.That(VpNativeDrawSetup.Requested, Is.False);
            }
            finally
            {
                VpNativeDrawSetup.Request = before;
            }
        }

        [Test]
        public void Setup_NativePreferred_AllowsFallback_WhileExplicitNativeRemainsStrict_AndUnityApiRemainsExplicit()
        {
            foreach ((GraphicsDeviceType device, bool editor, RuntimePlatform platform) in new[]
                     {
                         (GraphicsDeviceType.Vulkan, false, RuntimePlatform.Android),
                         (GraphicsDeviceType.Direct3D12, false, RuntimePlatform.WindowsPlayer),
                         (GraphicsDeviceType.Direct3D12, true, RuntimePlatform.WindowsEditor),
                         (GraphicsDeviceType.Direct3D11, true, RuntimePlatform.WindowsEditor),
                     })
            {
                VpNativeDrawSetup.Selection preferred = VpNativeDrawSetup.Select(VpBodyRouteRequest.NativePreferred, device, editor, platform);
                Assert.That(preferred.Native, Is.True, "the plugin is considered before checking its availability");
                Assert.That(preferred.Asked, Is.False, "preference does not impose the strict native-only contract");
                Assert.That(preferred.FallbackExpected, Is.True);
                Assert.That(preferred.Reason, Is.Not.Null.And.Not.Empty);

                VpNativeDrawSetup.Selection strict = VpNativeDrawSetup.Select(VpBodyRouteRequest.Native, device, editor, platform);
                Assert.That(strict.Native, Is.True);
                Assert.That(strict.Asked, Is.True);
                Assert.That(strict.FallbackExpected, Is.False, "the explicit native request still refuses another route");

                VpNativeDrawSetup.Selection unity = VpNativeDrawSetup.Select(VpBodyRouteRequest.UnityApi, device, editor, platform);
                Assert.That(unity.Native, Is.False);
                Assert.That(unity.Asked, Is.True);
                Assert.That(unity.FallbackExpected, Is.False, "an explicit Unity API request is not a native fallback");
            }

            Assert.That(new VpNativeDrawSetup.Selection(true, false, "existing default").FallbackExpected, Is.False,
                "existing three-argument selections retain their recorded-error default failure policy");
            Assert.That(VpNativeDrawSetup.ReadRequest(new[] { "player.exe", VpNativeDrawSetup.Argument }),
                Is.EqualTo(VpBodyRouteRequest.Native), "the native command-line argument remains strict");
            Assert.That(VpNativeDrawSetup.ReadRequest(new[] { "player.exe", VpNativeDrawSetup.Argument, VpNativeDrawSetup.UnityApiArgument }),
                Is.EqualTo(VpBodyRouteRequest.UnityApi), "an explicit Unity API request still wins");
        }

        [Test]
        public void SphericalHarmonics_PackAsUnitysSevenConstants()
        {
            var sh = new SphericalHarmonicsL2();
            sh.AddAmbientLight(new Color(0.5f, 0.25f, 0.125f));
            Vector4[] packed = VpNativeConstants.PackSphericalHarmonics(sh);
            Assert.That(packed.Length, Is.EqualTo(7));
            Assert.That(packed[0].w, Is.EqualTo(sh[0, 0] - sh[0, 6]).Within(1e-6f), "SHAr.w: the constant term less the z² term");
            Assert.That(packed[1].w, Is.EqualTo(sh[1, 0]).Within(1e-6f), "an ambient-only probe has no quadratic term");
            Assert.That(new Vector3(packed[0].x, packed[0].y, packed[0].z), Is.EqualTo(Vector3.zero), "no linear term");
            Assert.That(packed[3], Is.EqualTo(Vector4.zero));
            Assert.That(packed[6].w, Is.EqualTo(1f));
            Assert.That(packed[0].w, Is.GreaterThan(packed[1].w), "red brighter than green, as given");
        }

        [Test]
        public void PointerCache_KeepsABuffersPointer_AndDropsItWithTheObject()
        {
            var cache = new VpNativePointerCache();
            GraphicsBuffer first = Track(new GraphicsBuffer(GraphicsBuffer.Target.Structured, 8, 16));
            IntPtr pointer = cache.Buffer(first);
            Assert.That(pointer, Is.Not.EqualTo(IntPtr.Zero));
            Assert.That(cache.Buffer(first), Is.EqualTo(pointer), "the same buffer: the kept pointer");
            Assert.That(cache.Acquisitions, Is.EqualTo(1));
            Assert.That(cache.Hits, Is.EqualTo(1));

            // Data written into the buffer through a Unity API may change the native buffer (Unity's contract for
            // GetNativeBufferPtr); the owner raises its generation at the write, and the pointer is asked again --
            // and only then: the same generation is kept.
            first.SetData(new Vector4[8]);
            Assert.That(cache.Buffer(first, 1), Is.EqualTo(first.GetNativeBufferPtr()), "the current native buffer after the write");
            Assert.That(cache.Acquisitions, Is.EqualTo(2), "asked again at the new generation");
            Assert.That(cache.Buffer(first, 1), Is.EqualTo(first.GetNativeBufferPtr()));
            Assert.That(cache.Acquisitions, Is.EqualTo(2), "kept at the same generation");
            Assert.That(cache.Buffer(first), Is.EqualTo(first.GetNativeBufferPtr()));
            Assert.That(cache.Acquisitions, Is.EqualTo(3), "generation 0 (never written) is another generation: asked again");

            // A replacement (a capacity grown, a buffer replaced) is a new object: asked again, and the pointer is its own.
            GraphicsBuffer second = Track(new GraphicsBuffer(GraphicsBuffer.Target.Structured, 16, 16));
            IntPtr other = cache.Buffer(second);
            Assert.That(cache.Acquisitions, Is.EqualTo(4));
            Assert.That(other, Is.Not.EqualTo(pointer));

            // A disposed buffer is refused, and dropped by the sweep the route runs every frame.
            var third = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 4);
            cache.Buffer(third);
            third.Dispose();
            Assert.That(() => cache.Buffer(third), Throws.ArgumentException);
            Assert.That(cache.Count, Is.EqualTo(3));
            cache.DropDead();
            Assert.That(cache.Count, Is.EqualTo(2), "the disposed one is gone");
            Assert.That(cache.Buffer(first), Is.EqualTo(first.GetNativeBufferPtr()), "the live one is still kept");
            Assert.That(cache.Acquisitions, Is.EqualTo(5));
            cache.Invalidate(first);
            cache.Buffer(first);
            Assert.That(cache.Acquisitions, Is.EqualTo(6), "an invalidated one is asked again");
        }

        [Test]
        public void PointerCache_KeepsATexture_WhileItsShapeAndUpdateCountStand_AndAskedAgainOnInvalidate()
        {
            var cache = new VpNativePointerCache();
            var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false);
            try
            {
                texture.Apply();
                IntPtr texturePointer = cache.Texture(texture);
                Assert.That(texturePointer, Is.Not.EqualTo(IntPtr.Zero));
                Assert.That(cache.Texture(texture), Is.EqualTo(texturePointer));
                Assert.That(cache.Acquisitions, Is.EqualTo(1));

                // An upload raises the update count: asked again (the native texture is the same, so the answer is too).
                texture.SetPixel(0, 0, Color.red);
                texture.Apply();
                Assert.That(cache.Texture(texture), Is.EqualTo(texturePointer));
                Assert.That(cache.Acquisitions, Is.EqualTo(2), "asked again after an upload");

                // Made again in place (Reinitialize): the native texture changes and Unity raises no count the cache can
                // see, so the owner says so; after that the new pointer is taken.
                texture.Reinitialize(4, 4);
                texture.Apply();
                cache.Invalidate(texture);
                IntPtr afterReinitialize = cache.Texture(texture);
                Assert.That(cache.Acquisitions, Is.EqualTo(3));
                Assert.That(afterReinitialize, Is.EqualTo(texture.GetNativeTexturePtr()), "the current native texture, whatever it is now");

                // A render texture is not taken as a texture pointer at all.
                var renderTexture = new RenderTexture(8, 8, 0, RenderTextureFormat.ARGB32);
                try
                {
                    Assert.That(() => cache.Texture(renderTexture), Throws.ArgumentException);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(renderTexture);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void PointerCache_SeesARenderTextureMadeAgainAtTheSameSize_AtOnce()
        {
            var cache = new VpNativePointerCache();
            var renderTexture = new RenderTexture(8, 8, 16, RenderTextureFormat.ARGB32);
            try
            {
                Assert.That(cache.DepthRenderBuffer(renderTexture), Is.EqualTo(IntPtr.Zero), "not created: nothing to bind");
                renderTexture.Create();
                IntPtr depth = cache.DepthRenderBuffer(renderTexture);
                IntPtr colour = cache.ColourRenderBuffer(renderTexture);
                Assert.That(depth, Is.Not.EqualTo(IntPtr.Zero));
                Assert.That(colour, Is.Not.EqualTo(IntPtr.Zero));
                Assert.That(cache.DepthRenderBuffer(renderTexture), Is.EqualTo(depth));
                Assert.That(cache.ColourRenderBuffer(renderTexture), Is.EqualTo(colour));
                long asked = cache.Acquisitions;
                for (int frame = 0; frame < 200; frame++) cache.DepthRenderBuffer(renderTexture);
                Assert.That(cache.Acquisitions, Is.EqualTo(asked), "a created, unchanged render texture is never asked again by the passing of frames");

                // Released and made again at the same size: Unity raises the update count on creation, so the handles
                // are asked again at once and are the current ones -- the hole the frame interval used to leave.
                uint countBefore = renderTexture.updateCount;
                renderTexture.Release();
                Assert.That(renderTexture.IsCreated(), Is.False);
                Assert.That(cache.DepthRenderBuffer(renderTexture), Is.EqualTo(IntPtr.Zero), "released: nothing to bind, no stale handle");
                renderTexture.Create();
                Assert.That(renderTexture.updateCount, Is.GreaterThan(countBefore), "Unity raises the update count on creation (the fact the cache rests on)");
                IntPtr depthAgain = cache.DepthRenderBuffer(renderTexture);
                Assert.That(cache.Acquisitions, Is.EqualTo(asked + 1), "asked again right after the re-creation");
                Assert.That(depthAgain, Is.EqualTo(renderTexture.depthBuffer.GetNativeRenderBufferPtr()), "the current depth render buffer handle");
                Assert.That(cache.ColourRenderBuffer(renderTexture), Is.EqualTo(renderTexture.colorBuffer.GetNativeRenderBufferPtr()));
            }
            finally
            {
                renderTexture.Release();
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        // A block taken and closed in a ring, as a pass's Execute does; the test's stand-in for the issue.
        private static uint Issue(VpNativeDrawData ring, GraphicsBuffer arguments, GraphicsBuffer indices)
        {
            Assert.That(ring.TryBegin(0, arguments, 0, 1, indices), Is.True, "a block is free");
            ring.AddViewport(new Rect(0, 0, 4, 4));
            ring.Finish(out uint serial);
            return serial;
        }

        private static unsafe void Consume(VpNativeDrawData ring, IntPtr block)
        {
            ((Plugin.Draw*)block)->consumed = 1;
        }

        [Test]
        public void DrawData_MovesABlock_Prepared_Recorded_Submitted_AndReleasesIt_ByItsOwnMark_OrByItsSentinelAlone()
        {
            // A ring of two blocks (so that a third issue must reuse a released slot), no plugin: nothing consumes
            // them but this test.
            var ring = Track(new VpNativeDrawData(2, 4, 2, 256) { Name = "test ring" });
            GraphicsBuffer arguments = Track(new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 4, 24));
            GraphicsBuffer indices = Track(new GraphicsBuffer(GraphicsBuffer.Target.Index, 6, 4));

            // 1. Prepared: closed, in no command buffer. A sentinel settles nothing of it.
            uint a = Issue(ring, arguments, indices);
            Assert.That(ring.StateOf(a), Is.EqualTo(VpNativeDrawData.BlockState.Prepared));
            Assert.That(ring.SubmitRecorded(1), Is.Zero, "a Prepared block is not submitted under a sentinel");
            ring.SetConsumedSentinel(1);
            Assert.That(ring.InFlight, Is.EqualTo(1), "nor released by one");
            Assert.That(ring.HasRecordedUnconsumed, Is.False);

            // 2. Recorded: the pass wrote the event into its command buffer (nowhere here). Still not under a sentinel:
            // the camera's Submit has not happened, so a sentinel issued now could run first.
            Assert.That(ring.RecordPrepared(null, IntPtr.Zero, 0), Is.EqualTo(1));
            Assert.That(ring.StateOf(a), Is.EqualTo(VpNativeDrawData.BlockState.Recorded));
            Assert.That(ring.HasRecordedUnconsumed, Is.True);
            ring.SetConsumedSentinel(2);
            Assert.That(ring.InFlight, Is.EqualTo(1), "a Recorded block is not released by any sentinel");

            // A second block, prepared after the first was recorded: it stays Prepared when the first is submitted.
            uint b = Issue(ring, arguments, indices);

            // 3. Submitted under sentinel 3 (the camera's rendering ended; the sentinel goes behind it on its context).
            Assert.That(ring.SubmitRecorded(3), Is.EqualTo(1), "the Recorded block alone");
            Assert.That(ring.StateOf(a), Is.EqualTo(VpNativeDrawData.BlockState.Submitted));
            Assert.That(ring.StateOf(b), Is.EqualTo(VpNativeDrawData.BlockState.Prepared));
            Assert.That(ring.AllReleasedUpTo(a, 2), Is.False, "sentinel 2 was before it");
            Assert.That(ring.IsDiscardConfirmed(a), Is.False);

            // 4. Sentinel 3 consumed: the rendering thread ran everything submitted before it; this block's event was
            // not among them (no mark) -- its command never ran: discard confirmed, released.
            Assert.That(ring.AllReleasedUpTo(a, 3), Is.True);
            Assert.That(ring.IsDiscardConfirmed(a), Is.True);
            Assert.That(ring.InFlight, Is.EqualTo(1), "the Prepared second block remains");
            Assert.That(ring.StateOf(a), Is.EqualTo(VpNativeDrawData.BlockState.Free));

            // 5. The second block recorded and consumed by its own mark, in whatever state: released by that alone.
            ring.RecordPrepared(null, IntPtr.Zero, 0);
            Assert.That(ring.TryBegin(0, arguments, 0, 1, indices), Is.True, "the first slot is free again");
            ring.AddViewport(new Rect(0, 0, 4, 4));
            IntPtr third = ring.Finish(out uint c);
            Assert.That(ring.DiscardsConfirmed, Is.EqualTo(1), "tallied when the slot was reused");
            Assert.That(ring.InFlight, Is.EqualTo(2));
            Consume(ring, third);
            Assert.That(ring.InFlight, Is.EqualTo(1), "consumed while Prepared: released (the plugin never does this; the mark alone is the rule)");
            Assert.That(ring.StateOf(b), Is.EqualTo(VpNativeDrawData.BlockState.Recorded));

            // 6. A Prepared block the pass never recorded (its Execute ended early): freed by the ring, no event exists.
            uint d = Issue(ring, arguments, indices);
            Assert.That(ring.InFlightIn(VpNativeDrawData.BlockState.Prepared), Is.EqualTo(1));
            Assert.That(ring.DropPrepared(), Is.EqualTo(1));
            Assert.That(ring.StateOf(d), Is.EqualTo(VpNativeDrawData.BlockState.Free));
            Assert.That(ring.NeverRecorded, Is.EqualTo(1));
            Assert.That(ring.InFlight, Is.EqualTo(1), "the Recorded second block alone");

            // An open issue abandoned is dropped the same way.
            Assert.That(ring.TryBegin(0, arguments, 0, 1, indices), Is.True);
            Assert.That(ring.DropPrepared(), Is.EqualTo(1));
            Assert.That(ring.NeverRecorded, Is.EqualTo(2));

            // 7. The device gone: nothing will ever write to a block again; everything is released.
            Assert.That(ring.AllReleased(100), Is.False, "the Recorded block waits (no sentinel can settle it)");
            ring.DeviceGone();
            Assert.That(ring.AllReleased(100), Is.True);
        }

        [Test]
        public void Release_IssuesTheSentinelOnlyAtTheEndOfACamerasRendering_AndNeverOvertakesAnUnsubmittedEvent()
        {
            var ring = Track(new VpNativeDrawData(4, 4, 2, 256) { Name = "seal ring" });
            GraphicsBuffer arguments = Track(new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 4, 24));
            GraphicsBuffer indices = Track(new GraphicsBuffer(GraphicsBuffer.Target.Index, 6, 4));
            var sentinels = new List<IntPtr>();
            bool journalBefore = VpNativeDrawRelease.JournalOn;
            VpNativeDrawRelease.IssueSentinelForTest = sentinel => sentinels.Add(sentinel);
            VpNativeDrawRelease.DeviceAvailableForTest = () => true;
            VpNativeDrawRelease.JournalOn = true;
            VpNativeDrawRelease.ClearJournal();
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 4);
            try
            {
                VpNativeDrawRelease.Register(ring);
                int pendingBefore = VpNativeDrawRelease.Pending;
                long issuedBefore = VpNativeDrawRelease.SentinelsIssued;

                // An event prepared but not recorded when a camera's rendering ends (its pass threw after Finish): the
                // seal frees it -- it was in no command buffer -- and issues no sentinel for it.
                uint never = Issue(ring, arguments, indices);
                VpNativeDrawRelease.SealForTest();
                Assert.That(ring.StateOf(never), Is.EqualTo(VpNativeDrawData.BlockState.Free), "never recorded: freed at the seal");
                Assert.That(ring.NeverRecorded, Is.EqualTo(1));
                Assert.That(VpNativeDrawRelease.SentinelsIssued, Is.EqualTo(issuedBefore), "nothing recorded: no sentinel");

                // An event recorded (in the pass's command buffer) with a buffer retired against it before the camera's
                // rendering ends: the deferral issues NO sentinel (the only place is the end of the rendering), so none
                // can overtake the event's submission.
                uint recorded = Issue(ring, arguments, indices);
                ring.RecordPrepared(null, IntPtr.Zero, 0);
                VpNativeDrawRelease.Defer("retired against a recorded event", new VpNativeDrawRelease.RingView(ring), new IDisposable[] { buffer });
                Assert.That(VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore + 1), "held");
                Assert.That(VpNativeDrawRelease.SentinelsIssued, Is.EqualTo(issuedBefore), "a deferral issues no sentinel");
                Assert.That(buffer.IsValid(), Is.True);

                // The camera's rendering ends: the sentinel goes behind its submission, the Recorded block is Submitted
                // under it. An event prepared in a later pass of the same moment (not recorded yet) is not.
                uint later = Issue(ring, arguments, indices);
                VpNativeDrawRelease.SealForTest();
                Assert.That(VpNativeDrawRelease.SentinelsIssued, Is.EqualTo(issuedBefore + 1), "one sentinel, at the end of the rendering, since something waits");
                Assert.That(sentinels.Count, Is.EqualTo(1));
                Assert.That(ring.StateOf(recorded), Is.EqualTo(VpNativeDrawData.BlockState.Submitted));
                Assert.That(ring.StateOf(later), Is.EqualTo(VpNativeDrawData.BlockState.Free), "a Prepared block at the seal was never recorded: freed");
                Assert.That(VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore + 1), "the sentinel is not consumed: nothing released");
                Assert.That(VpNativeDrawRelease.Tick(), Is.Zero);

                // Another event recorded after the sentinel was issued: not under it. Its consumption would prove
                // nothing of this one, and the ring says so.
                uint after = Issue(ring, arguments, indices);
                ring.RecordPrepared(null, IntPtr.Zero, 0);
                Assert.That(ring.StateOf(after), Is.EqualTo(VpNativeDrawData.BlockState.Recorded));

                // The rendering thread reaches the sentinel: every command submitted before it has run. The recorded
                // block has no mark: its command never ran (discard confirmed). The later one is still Recorded.
                VpNativeDrawRelease.MarkSentinelConsumedForTest(sentinels[0]);
                Assert.That(VpNativeDrawRelease.Tick(), Is.EqualTo(1), "the buffer's view was taken before 'later' and 'after': it waited on 'recorded' alone, now settled");
                Assert.That(VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore));
                Assert.That(buffer.IsValid(), Is.False);
                Assert.That(ring.IsDiscardConfirmed(recorded), Is.True);
                Assert.That(ring.StateOf(after), Is.EqualTo(VpNativeDrawData.BlockState.Recorded), "not under the consumed sentinel: untouched");
                Assert.That(ring.InFlight, Is.EqualTo(1));

                // The journal tells it in order: prepared, recorded, deferred, sentinel issued, submitted, consumed, released.
                string journal = VpNativeDrawRelease.JournalText();
                TestContext.Out.WriteLine(journal);
                int iPrepared = journal.IndexOf("serial " + recorded + ": prepared", StringComparison.Ordinal);
                int iRecorded = journal.IndexOf("serial " + recorded + ": recorded", StringComparison.Ordinal);
                int iDeferred = journal.IndexOf("deferred: 1 resources", StringComparison.Ordinal);
                int iSubmitted = journal.IndexOf("serial " + recorded + ": submitted under sentinel", StringComparison.Ordinal);
                int iIssued = journal.IndexOf("issued at the end of test's rendering", StringComparison.Ordinal);
                int iConsumed = journal.IndexOf("consumed: every command submitted before it has run", StringComparison.Ordinal);
                int iReleased = journal.IndexOf("released (its events are over)", StringComparison.Ordinal);
                Assert.That(iPrepared, Is.GreaterThanOrEqualTo(0).And.LessThan(iRecorded));
                Assert.That(iRecorded, Is.LessThan(iDeferred));
                Assert.That(iDeferred, Is.LessThan(iSubmitted));
                Assert.That(iSubmitted, Is.LessThan(iIssued));
                Assert.That(iIssued, Is.LessThan(iConsumed));
                Assert.That(iConsumed, Is.LessThan(iReleased));
                VpNativeDrawRelease.Unregister(ring);
            }
            finally
            {
                VpNativeDrawRelease.Unregister(ring);
                VpNativeDrawRelease.IssueSentinelForTest = null;
                VpNativeDrawRelease.DeviceAvailableForTest = null;
                VpNativeDrawRelease.JournalOn = journalBefore;
                if (buffer.IsValid()) buffer.Dispose();
            }
        }

        [Test]
        public void Release_KeepsADisposedRoutesRingAndResources_UntilItsSubmittedEventsAreConsumedOrConfirmedDiscarded()
        {
            var ring = new VpNativeDrawData(2, 4, 2, 256) { Name = "disposed route ring" };
            GraphicsBuffer arguments = Track(new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 4, 24));
            GraphicsBuffer indices = Track(new GraphicsBuffer(GraphicsBuffer.Target.Index, 6, 4));
            VpNativeDrawRelease.Register(ring);
            uint one = Issue(ring, arguments, indices);
            ring.RecordPrepared(null, IntPtr.Zero, 0);
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 4);
            var sentinels = new List<IntPtr>();
            VpNativeDrawRelease.IssueSentinelForTest = sentinel => sentinels.Add(sentinel);
            VpNativeDrawRelease.DeviceAvailableForTest = () => true;
            try
            {
                int pendingBefore = VpNativeDrawRelease.Pending;
                long releasedBefore = VpNativeDrawRelease.Released;
                VpNativeDrawRelease.Defer("test route", ring, new IDisposable[] { buffer });
                Assert.That(VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore + 1), "held: the block's event may still run");
                Assert.That(buffer.IsValid(), Is.True, "the GPU resource is not disposed while an event names it");
                Assert.That(VpNativeDrawRelease.Tick(), Is.Zero);

                // The camera's rendering ends: the disposed route's ring is still sealed (it stays registered until freed).
                VpNativeDrawRelease.SealForTest();
                Assert.That(sentinels.Count, Is.EqualTo(1), "a sentinel, since the entry waits");
                Assert.That(ring.StateOf(one), Is.EqualTo(VpNativeDrawData.BlockState.Submitted));
                Assert.That(VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore + 1));

                VpNativeDrawRelease.MarkSentinelConsumedForTest(sentinels[0]);
                Assert.That(VpNativeDrawRelease.Tick(), Is.EqualTo(1), "consumed: released");
                Assert.That(VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore));
                Assert.That(VpNativeDrawRelease.Released, Is.EqualTo(releasedBefore + 1));
                Assert.That(buffer.IsValid(), Is.False, "the GPU resource was disposed with the ring");
                Assert.That(() => ring.TryBegin(0, arguments, 0, 1, indices), Throws.InstanceOf<ObjectDisposedException>(), "the ring was freed");

                // A route that never issued: its resources go at once.
                var other = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 4);
                VpNativeDrawRelease.Defer("test route without a ring", (VpNativeDrawData)null, new IDisposable[] { other });
                Assert.That(VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore));
                Assert.That(other.IsValid(), Is.False);

                // A view of a live ring whose block is consumed by its own mark: released at the next Tick, no sentinel needed.
                var live = Track(new VpNativeDrawData(2, 4, 2, 256) { Name = "live ring" });
                VpNativeDrawRelease.Register(live);
                Assert.That(live.TryBegin(0, arguments, 0, 1, indices), Is.True);
                live.AddViewport(new Rect(0, 0, 4, 4));
                IntPtr liveBlock = live.Finish(out _);
                live.RecordPrepared(null, IntPtr.Zero, 0);
                var retired = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 4);
                VpNativeDrawRelease.Defer("retired buffer", new VpNativeDrawRelease.RingView(live), new IDisposable[] { retired });
                Assert.That(retired.IsValid(), Is.True);
                Consume(live, liveBlock);
                Assert.That(VpNativeDrawRelease.Tick(), Is.EqualTo(1));
                Assert.That(retired.IsValid(), Is.False, "released once its event was consumed");
                Assert.That(() => live.TryBegin(0, arguments, 0, 1, indices), Throws.Nothing, "the ring it waited on lives on");
                VpNativeDrawRelease.Unregister(live);

                // The device gone: the plugin touches no block, so everything waiting is released at once.
                var gone = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 4);
                var goneRing = new VpNativeDrawData(2, 4, 2, 256) { Name = "gone ring" };
                Issue(goneRing, arguments, indices);
                goneRing.RecordPrepared(null, IntPtr.Zero, 0);
                VpNativeDrawRelease.DeviceAvailableForTest = () => false;
                VpNativeDrawRelease.Defer("test route at device loss", goneRing, new IDisposable[] { gone });
                Assert.That(VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore));
                Assert.That(gone.IsValid(), Is.False);
            }
            finally
            {
                VpNativeDrawRelease.Unregister(ring);
                VpNativeDrawRelease.IssueSentinelForTest = null;
                VpNativeDrawRelease.DeviceAvailableForTest = null;
                if (buffer.IsValid()) buffer.Dispose();
            }
        }

        [Test]
        public void Release_ABufferRetiredAcrossRoutes_WaitsOnTheOldRoutesUnconsumedEvents_NotOnTheNewRouteAlone()
        {
            // Two routes' rings, as a drawing component disabled and enabled makes: the old one has an event submitted
            // and unconsumed; the new one's events are all consumed. A display buffer both drew is retired now.
            var old = new VpNativeDrawData(2, 4, 2, 256) { Name = "old route ring" };
            var fresh = Track(new VpNativeDrawData(2, 4, 2, 256) { Name = "new route ring" });
            GraphicsBuffer arguments = Track(new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 4, 24));
            GraphicsBuffer indices = Track(new GraphicsBuffer(GraphicsBuffer.Target.Index, 6, 4));
            var sentinels = new List<IntPtr>();
            VpNativeDrawRelease.IssueSentinelForTest = sentinel => sentinels.Add(sentinel);
            VpNativeDrawRelease.DeviceAvailableForTest = () => true;
            var buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 4);
            try
            {
                VpNativeDrawRelease.Register(old);
                VpNativeDrawRelease.Register(fresh);
                int pendingBefore = VpNativeDrawRelease.Pending;
                long acrossBefore = VpNativeDrawRelease.RetiredAcrossRoutes;
                uint oldEvent = Issue(old, arguments, indices);
                old.RecordPrepared(null, IntPtr.Zero, 0);
                // The old route is disposed (the component disabled) with that event recorded and unconsumed; at the
                // end of the camera's rendering the sentinel goes behind it and the event is Submitted under it.
                VpNativeDrawRelease.Defer("old route", old, Array.Empty<IDisposable>());
                Assert.That(VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore + 1));
                VpNativeDrawRelease.SealForTest();
                Assert.That(sentinels.Count, Is.EqualTo(1));
                Assert.That(old.StateOf(oldEvent), Is.EqualTo(VpNativeDrawData.BlockState.Submitted));

                // The new route draws; its event is recorded and still unconsumed when the buffer is retired.
                Assert.That(fresh.TryBegin(0, arguments, 0, 1, indices), Is.True);
                fresh.AddViewport(new Rect(0, 0, 4, 4));
                IntPtr freshBlock = fresh.Finish(out _);
                fresh.RecordPrepared(null, IntPtr.Zero, 0);

                // The buffer retired against both routes: one entry, waiting on both rings.
                var oldSink = new RingSink(old);
                var freshSink = new RingSink(fresh);
                VpNativeDrawRelease.Retire("display buffer", new IVpNativeDrawSink[] { oldSink, freshSink }, buffer);
                Assert.That(VpNativeDrawRelease.RetiredAcrossRoutes, Is.EqualTo(acrossBefore + 1));
                Assert.That(VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore + 2), "held: both routes' events may still read it");
                Assert.That(buffer.IsValid(), Is.True);
                Assert.That(VpNativeDrawRelease.Tick(), Is.Zero);

                // The new route's event consumed (a rendering thread that caught up with the new route alone): the new
                // route is clean, the old one's event is not -- the buffer stays.
                Consume(fresh, freshBlock);
                Assert.That(VpNativeDrawRelease.Tick(), Is.Zero, "the new route alone being clean releases nothing");
                Assert.That(buffer.IsValid(), Is.True, "the old route's unconsumed event may still read it");
                Assert.That(VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore + 2));

                // The old event's sentinel consumed: the old event never ran; both waits are over; released once.
                VpNativeDrawRelease.MarkSentinelConsumedForTest(sentinels[sentinels.Count - 1]);
                Assert.That(VpNativeDrawRelease.Tick(), Is.EqualTo(2), "the old route's entry and the buffer's");
                Assert.That(VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore));
                Assert.That(buffer.IsValid(), Is.False);
                Assert.That(old.IsDisposed, Is.True, "the old ring was freed with its entry");

                // A buffer retired when no route's events can still read anything goes at once, through either path.
                var clean = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 4);
                VpNativeDrawRelease.Retire("clean buffer", new IVpNativeDrawSink[] { oldSink, freshSink }, clean);
                Assert.That(clean.IsValid(), Is.False);
                Assert.That(VpNativeDrawRelease.Pending, Is.EqualTo(pendingBefore));
                VpNativeDrawRelease.Unregister(fresh);
            }
            finally
            {
                VpNativeDrawRelease.Unregister(old);
                VpNativeDrawRelease.Unregister(fresh);
                VpNativeDrawRelease.IssueSentinelForTest = null;
                VpNativeDrawRelease.DeviceAvailableForTest = null;
                if (buffer.IsValid()) buffer.Dispose();
                if (!old.IsDisposed) old.Dispose();
            }
        }

        // A sink that answers for a ring alone, as a route does for its own (a disposed route keeps answering for the ring it handed over).
        private sealed class RingSink : IVpNativeDrawSink
        {
            private readonly VpNativeDrawData _ring;

            public RingSink(VpNativeDrawData ring)
            {
                _ring = ring;
            }

            public void AddBody(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int startCommand, int commandCount, int view, int layer) => throw new NotSupportedException();

            public void AddCasters(Camera camera, VpIndexedIndirectDrawBatch batch, VpGpuIndexedGeometryBuffers buffers, Material material, int startCommand, int commandCount, int view, int layer) => throw new NotSupportedException();

            public void Retire(GraphicsBuffer buffer) => VpNativeDrawRelease.Defer("ring sink retired buffer", IssuedEventsWait(), new IDisposable[] { buffer });

            public VpNativeDrawRelease.IRingWait IssuedEventsWait() => _ring.IsDisposed || _ring.InFlight == 0 ? null : new VpNativeDrawRelease.RingView(_ring);
        }

        [Test]
        public void Plugin_AnswersItsAvailabilityExplicitly()
        {
            bool loaded = Plugin.TryLoad(out string failure);
            TestContext.WriteLine("device " + SystemInfo.graphicsDeviceType + ": loaded " + loaded + (failure != null ? " (" + failure + ")" : "") + "; state: " + Plugin.DescribeState());
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12)
            {
                Assert.That(loaded, Is.True, failure);
                Assert.That(Plugin.RenderEventFunction, Is.Not.EqualTo(IntPtr.Zero));
            }
            else
            {
                Assert.That(loaded, Is.False, "the plugin draws on Direct3D 12 only");
                Assert.That(failure, Does.Contain("Direct3D 12").Or.Contain("not in this Player"));
            }
        }

        // ----- the detailed observation (the Main P95 investigation, 2026-10-08): off unless asked ------------------

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long value);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long value);

        // GC.Alloc samples on this thread while `action` runs `times` times (the measure of the preparation tests).
        private static int AllocationSamples(Action action, int times)
        {
            UnityEngine.Profiling.Recorder recorder = UnityEngine.Profiling.Recorder.Get("GC.Alloc");
            recorder.enabled = false;
            recorder.FilterToCurrentThread();
            recorder.enabled = true;
            for (int i = 0; i < times; i++) action();
            recorder.enabled = false;
            recorder.CollectFromAllThreads();
            return recorder.sampleBlockCount;
        }

        // The positive control: a recorder that counts nothing must not pass for "no allocation".
        private static byte[] s_known;

        private static void AssertAllocationIsSeen()
        {
            int seen = AllocationSamples(() => s_known = new byte[4096], 1);
            GC.KeepAlive(s_known);
            Assert.That(seen, Is.GreaterThanOrEqualTo(1), "the recorder counts a known allocation on this thread");
        }

        private static readonly Action s_tick = () => VpNativeDrawRelease.Tick();
        private static readonly Action s_seal = () => VpNativeDrawRelease.SealForTest();
        private static readonly Action s_query = () => Plugin.IsAvailableNow();
        private static readonly Action s_stats = () => Plugin.ReadLockStats();

        [Test]
        public void DetailObservation_IsOffUnlessAsked_AndOffTheKeeperKeepsNothing()
        {
            Assert.That(Plugin.DetailTimingArgument, Is.EqualTo("-zantetsuNativeDrawTiming"));
            Assert.That(Array.IndexOf(Environment.GetCommandLineArgs(), Plugin.DetailTimingArgument), Is.LessThan(0), "this Editor was not launched with the option");
            Plugin.ReadDetailTimingArgument();
            Assert.That(Plugin.DetailTiming, Is.False, "off by default: nothing asked for it");

            var ring = Track(new VpNativeDrawData(4, 4, 2, 256) { Name = "observation ring" });
            bool journalBefore = VpNativeDrawRelease.JournalOn;
            VpNativeDrawRelease.JournalOn = false;

            // The device query takes a moment here (a spin), so that a part timed at all shows a time above the clock's step.
            VpNativeDrawRelease.DeviceAvailableForTest = () =>
            {
                System.Threading.Thread.SpinWait(20000);
                return true;
            };
            VpNativeDrawRelease.IssueSentinelForTest = _ => { };
            try
            {
                VpNativeDrawRelease.Register(ring);
                int frame = Time.frameCount;

                // Off: the keeper does its work and keeps nothing of it.
                VpNativeDrawRelease.SealForTest();
                VpNativeDrawRelease.Tick();
                Assert.That(VpNativeDrawRelease.TryGetFrameWork(frame, out VpNativeDrawRelease.FrameWork off), Is.False, "off: no frame's work is handed out");
                Assert.That(off.tickCalls, Is.Zero);
                Assert.That(off.ringsScanned, Is.Zero);

                // On: the same calls are counted and timed -- a seal (with its tick) and a tick.
                Plugin.SetDetailTiming(true);
                Assert.That(Plugin.DetailTiming, Is.True);
                VpNativeDrawRelease.SealForTest();
                VpNativeDrawRelease.Tick();
                Assert.That(VpNativeDrawRelease.TryGetFrameWork(frame, out VpNativeDrawRelease.FrameWork on), Is.True);
                Assert.That(on.frame, Is.EqualTo(frame));
                Assert.That(on.tickCalls, Is.EqualTo(2), "the seal's tick and the tick; none of the calls made while it was off");
                Assert.That(on.ringsScanned, Is.GreaterThanOrEqualTo(3), "the seal's decision and the two ticks each looked at the ring");
                Assert.That(on.blocksScanned, Is.GreaterThanOrEqualTo(ring.Blocks));
                Assert.That(on.entriesPendingAtEnd, Is.EqualTo(VpNativeDrawRelease.Pending));
                Assert.That(on.tickDeviceTicks, Is.GreaterThan(0), "the ticks' device query was timed");
                Assert.That(on.dropTicks, Is.GreaterThanOrEqualTo(0));
                Assert.That(on.wantTicks, Is.GreaterThanOrEqualTo(0));

                // Off again: nothing is handed out, and what runs now is not added to what was kept.
                Plugin.SetDetailTiming(false);
                VpNativeDrawRelease.Tick();
                VpNativeDrawRelease.Tick();
                Assert.That(VpNativeDrawRelease.TryGetFrameWork(frame, out _), Is.False);
                Plugin.SetDetailTiming(true);
                Assert.That(VpNativeDrawRelease.TryGetFrameWork(frame, out VpNativeDrawRelease.FrameWork again), Is.True);
                Assert.That(again.tickCalls, Is.EqualTo(2), "the two ticks made while it was off were not counted");

                // Neither state allocates: the keeper's tick and its seal, a few hundred times each.
                AssertAllocationIsSeen();
                int onTick = AllocationSamples(s_tick, 300), onSeal = AllocationSamples(s_seal, 300);
                Plugin.SetDetailTiming(false);
                int offTick = AllocationSamples(s_tick, 300), offSeal = AllocationSamples(s_seal, 300);
                TestContext.WriteLine("GC.Alloc samples on this thread over 300 calls: tick on " + onTick + ", seal on " + onSeal + ", tick off " + offTick + ", seal off " + offSeal);
                Assert.That(onTick, Is.Zero);
                Assert.That(onSeal, Is.Zero);
                Assert.That(offTick, Is.Zero);
                Assert.That(offSeal, Is.Zero);
            }
            finally
            {
                Plugin.SetDetailTiming(false);
                VpNativeDrawRelease.Unregister(ring);
                VpNativeDrawRelease.IssueSentinelForTest = null;
                VpNativeDrawRelease.DeviceAvailableForTest = null;
                VpNativeDrawRelease.JournalOn = journalBefore;
            }
        }

        [Test]
        public void Plugin_TimesItsMutexOnlyWhenAsked_AndLeavesWhereTheLastQueryWasMade()
        {
            bool loaded = Plugin.TryLoad(out string failure);
            try
            {
                if (!loaded)
                {
                    // No plugin on this device (it draws on Direct3D 12 only): asked for or not, there is nothing to
                    // time, and the query answers no without counting.
                    Assert.That(SystemInfo.graphicsDeviceType, Is.Not.EqualTo(GraphicsDeviceType.Direct3D12), failure);
                    long callsBefore = Plugin.AvailabilityCalls;
                    Plugin.SetDetailTiming(true);
                    Assert.That(Plugin.IsAvailableNow(), Is.False);
                    Assert.That(Plugin.AvailabilityCalls, Is.EqualTo(callsBefore));
                    Plugin.LockStats none = Plugin.ReadLockStats();
                    Assert.That(none.timingOn, Is.False);
                    Assert.That(none.availabilityCalls, Is.Zero);
                    TestContext.WriteLine("device " + SystemInfo.graphicsDeviceType + ": the plugin is not loaded; nothing is timed");
                    return;
                }

                const int Calls = 100;

                // Off (the default): the query is made, and neither side counts or times it.
                Plugin.SetDetailTiming(false);
                Plugin.LockStats s0 = Plugin.ReadLockStats();
                long managed0 = Plugin.AvailabilityCalls, ticks0 = Plugin.AvailabilityTicks;
                Assert.That(s0.timingOn, Is.False, "the plugin's timing is off");
                for (int i = 0; i < Calls; i++) Assert.That(Plugin.IsAvailableNow(), Is.True);
                Plugin.LockStats s1 = Plugin.ReadLockStats();
                Assert.That(s1.availabilityCalls, Is.EqualTo(s0.availabilityCalls), "off: the plugin counted no query");
                Assert.That(s1.availabilityWaitTicks, Is.EqualTo(s0.availabilityWaitTicks));
                Assert.That(s1.availabilityHeldTicks, Is.EqualTo(s0.availabilityHeldTicks));
                Assert.That(s1.availabilityLastQpc, Is.EqualTo(s0.availabilityLastQpc), "off: no acquisition left its place");
                Assert.That(Plugin.AvailabilityCalls, Is.EqualTo(managed0), "off: the managed side counted no query");
                Assert.That(Plugin.AvailabilityTicks, Is.EqualTo(ticks0));

                // On: every query is counted on both sides, and the last one leaves its thread, stamp and processor.
                Plugin.SetDetailTiming(true);
                Plugin.LockStats s2 = Plugin.ReadLockStats();
                Assert.That(s2.timingOn, Is.True);
                QueryPerformanceFrequency(out long frequency);
                Assert.That(s2.qpcFrequency, Is.EqualTo((ulong)frequency), "the plugin's stamps and the rows' (QueryPerformanceCounter, read directly) are of one clock");

                // Written, not held: whether this runtime's Stopwatch is that clock too (it is in the IL2CPP Player, by
                // natW1's rows; it need not be -- which is why the rows' stamps do not go through it).
                QueryPerformanceCounter(out long before);
                long stopwatch = System.Diagnostics.Stopwatch.GetTimestamp();
                TestContext.WriteLine("QueryPerformanceCounter " + before + " at frequency " + frequency + "; Stopwatch.GetTimestamp " + stopwatch + " at frequency " + System.Diagnostics.Stopwatch.Frequency
                    + ": " + (Math.Abs(stopwatch - before) < frequency ? "the same clock in this runtime" : "NOT the same origin in this runtime"));
                for (int i = 0; i < Calls; i++) Assert.That(Plugin.IsAvailableNow(), Is.True);
                QueryPerformanceCounter(out long after);
                Plugin.LockStats s3 = Plugin.ReadLockStats();
                Assert.That(s3.availabilityCalls - s2.availabilityCalls, Is.EqualTo((ulong)Calls));
                Assert.That(Plugin.AvailabilityCalls - managed0, Is.EqualTo(Calls));
                Assert.That(Plugin.AvailabilityTicks, Is.GreaterThan(ticks0));
                Assert.That(s3.availabilityHeldTicks, Is.GreaterThan(s2.availabilityHeldTicks), "the hold was timed");
                Assert.That(s3.availabilityThreadId, Is.EqualTo((ulong)GetCurrentThreadId()), "the thread that asked");
                Assert.That((long)s3.availabilityLastQpc, Is.InRange(before, after), "the last acquisition's stamp, on the clock the rows use (QueryPerformanceCounter)");
                Assert.That(s3.availabilityLastProcessor, Is.LessThan((ulong)Environment.ProcessorCount), "a logical processor's number (where the thread was at that instant, nothing more)");
                TestContext.WriteLine("on: " + Calls + " queries, wait " + s3.Ms(s3.availabilityWaitTicks - s2.availabilityWaitTicks).ToString("F6") + " ms, held "
                    + s3.Ms(s3.availabilityHeldTicks - s2.availabilityHeldTicks).ToString("F6") + " ms; last on thread " + s3.availabilityThreadId + " at qpc " + s3.availabilityLastQpc
                    + " on processor " + s3.availabilityLastProcessor + "; draw events measured so far " + s3.drawEvents);

                // Off again: the counts stand where they were.
                Plugin.SetDetailTiming(false);
                for (int i = 0; i < Calls; i++) Plugin.IsAvailableNow();
                Plugin.LockStats s4 = Plugin.ReadLockStats();
                Assert.That(s4.timingOn, Is.False);
                Assert.That(s4.availabilityCalls, Is.EqualTo(s3.availabilityCalls));
                Assert.That(s4.availabilityLastQpc, Is.EqualTo(s3.availabilityLastQpc));

                // Neither the query nor the reading of the statistics allocates, on or off.
                AssertAllocationIsSeen();
                int offQuery = AllocationSamples(s_query, 300), offStats = AllocationSamples(s_stats, 300);
                Plugin.SetDetailTiming(true);
                int onQuery = AllocationSamples(s_query, 300), onStats = AllocationSamples(s_stats, 300);
                TestContext.WriteLine("GC.Alloc samples on this thread over 300 calls: query off " + offQuery + ", statistics off " + offStats + ", query on " + onQuery + ", statistics on " + onStats);
                Assert.That(offQuery, Is.Zero);
                Assert.That(offStats, Is.Zero);
                Assert.That(onQuery, Is.Zero);
                Assert.That(onStats, Is.Zero);
            }
            finally
            {
                Plugin.SetDetailTiming(false);
            }
        }

        /// <summary>
        /// What the detailed observation costs, measured rather than supposed: the state query, the keeper's tick (no
        /// entry waiting, one ring: the steady state of the draw comparison) and the reading of the statistics, each
        /// timed over blocks of calls with the observation off and on in turn, in this one process. The figures are
        /// written, not judged, but for a wide bound that only a broken switch would pass: they are this Editor's
        /// (Mono, not the Player's IL2CPP), and say what one call adds, not what a frame of a Player loses.
        /// </summary>
        [Test]
        public void DetailObservation_ItsAddedCostIsMeasured_OffAndOnInTurn()
        {
            const int Rounds = 31, Calls = 20000;
            bool loaded = Plugin.TryLoad(out _);
            var ring = Track(new VpNativeDrawData(4, 4, 2, 256) { Name = "cost ring" });
            bool journalBefore = VpNativeDrawRelease.JournalOn;
            VpNativeDrawRelease.JournalOn = false;
            VpNativeDrawRelease.DeviceAvailableForTest = () => true;
            var tickOff = new double[Rounds]; var tickOn = new double[Rounds];
            var queryOff = new double[Rounds]; var queryOn = new double[Rounds];
            var statsOn = new double[Rounds];
            try
            {
                VpNativeDrawRelease.Register(ring);
                for (int round = -1; round < Rounds; round++)   // round -1 warms the paths up and is not kept
                {
                    for (int state = 0; state < 2; state++)
                    {
                        Plugin.SetDetailTiming(state == 1);
                        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
                        for (int i = 0; i < Calls; i++) VpNativeDrawRelease.Tick();
                        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (loaded) for (int i = 0; i < Calls; i++) Plugin.IsAvailableNow();
                        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (loaded && state == 1) for (int i = 0; i < Calls; i++) Plugin.ReadLockStats();
                        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (round < 0) continue;
                        double nsPerTick = 1e9 / System.Diagnostics.Stopwatch.Frequency / Calls;
                        (state == 1 ? tickOn : tickOff)[round] = (t1 - t0) * nsPerTick;
                        (state == 1 ? queryOn : queryOff)[round] = (t2 - t1) * nsPerTick;
                        if (state == 1) statsOn[round] = (t3 - t2) * nsPerTick;
                    }
                }
            }
            finally
            {
                Plugin.SetDetailTiming(false);
                VpNativeDrawRelease.Unregister(ring);
                VpNativeDrawRelease.DeviceAvailableForTest = null;
                VpNativeDrawRelease.JournalOn = journalBefore;
            }

            Array.Sort(tickOff); Array.Sort(tickOn); Array.Sort(queryOff); Array.Sort(queryOn); Array.Sort(statsOn);
            int mid = Rounds / 2, low = Rounds / 10, high = Rounds - 1 - Rounds / 10;
            string Line(string what, double[] off, double[] on) => what + ": off median " + off[mid].ToString("F1") + " ns a call (" + off[low].ToString("F1") + " to " + off[high].ToString("F1")
                + "), on median " + on[mid].ToString("F1") + " (" + on[low].ToString("F1") + " to " + on[high].ToString("F1") + "), difference of the medians " + (on[mid] - off[mid]).ToString("F1") + " ns";
            TestContext.WriteLine("device " + SystemInfo.graphicsDeviceType + ", plugin loaded " + loaded + "; " + Rounds + " rounds of " + Calls + " calls, off and on in turn; ranges are the 10th to the 90th percentile of the rounds");
            TestContext.WriteLine(Line("keeper tick (device query stubbed, one ring, no entry)", tickOff, tickOn));
            if (loaded)
            {
                TestContext.WriteLine(Line("state query (P/Invoke, the plugin's mutex)", queryOff, queryOn));
                TestContext.WriteLine("reading the statistics (on only; once a row in the comparison's runner): median " + statsOn[mid].ToString("F1") + " ns a call (" + statsOn[low].ToString("F1") + " to " + statsOn[high].ToString("F1") + ")");
            }
            else
            {
                TestContext.WriteLine("state query: not measured (the plugin is not loaded on this device)");
            }

            Assert.That(tickOff[mid], Is.GreaterThan(0.0));
            Assert.That(tickOn[mid] - tickOff[mid], Is.LessThan(5000.0), "a tick's added cost is far under 5 microseconds");
            if (loaded) Assert.That(queryOn[mid] - queryOff[mid], Is.LessThan(5000.0), "a query's added cost is far under 5 microseconds");
        }
    }
}
