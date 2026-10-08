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
        }

        private T Track<T>(T disposable) where T : IDisposable
        {
            _disposables.Add(disposable);
            return disposable;
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
    }
}
