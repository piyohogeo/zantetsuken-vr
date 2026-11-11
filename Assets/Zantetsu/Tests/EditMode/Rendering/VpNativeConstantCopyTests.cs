using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// VpNativeConstantCopy.compute on the Editor's own device, whichever it is (start the Editor with -force-d3d12
    /// or -force-vulkan for those): the kernel that assembles a batch of constant blocks in one dispatch
    /// (AssembleGlobalsBatch, DESIGN 4.5.8, D-262) writes the bytes the one-block kernel (AssembleGlobals) writes
    /// for the same blocks. Neither the plugin nor a draw is needed: the kernels write a buffer that is read back.
    /// </summary>
    public class VpNativeConstantCopyTests
    {
        private const int Slots = 48;         // VP_NATIVE_SLOT_COUNT
        private const int UsedSlots = 41;     // the slots the kernels fill
        private const int Capacity = 16;      // VP_NATIVE_BATCH
        private const int SlotBytes = 384;    // room for the largest value (five matrices)
        private const int BlockBytes = 16384;
        private const uint Unused = 0xFFFFFFFFu;

        private static Matrix4x4 MatrixOf(int seed)
        {
            var m = new Matrix4x4();
            for (int i = 0; i < 16; i++) m[i] = 0.25f + seed * 1.5f + i * 0.125f;
            return m;
        }

        // The table of one block: which slots it reads and where. Each block lays the slots out its own way.
        private static uint[] OffsetsOf(int block)
        {
            var offsets = new uint[Slots];
            for (int slot = 0; slot < Slots; slot++)
            {
                bool used = slot < UsedSlots && (block == 0 || (block == 1 ? slot % 2 == 0 : slot % 3 != 1));
                int place = block == 2 ? UsedSlots - 1 - slot : slot;
                offsets[slot] = used ? (uint)(place * SlotBytes) : Unused;
            }

            return offsets;
        }

        [TestCase(1)]
        [TestCase(3)]
        [TestCase(Capacity)]
        public void TheBatchKernel_AssemblesTheBytesTheOneBlockKernelAssembles(int blocks)
        {
            if (!SystemInfo.supportsComputeShaders) Assert.Ignore("this device has no compute shaders");
            var shader = Resources.Load<ComputeShader>("VpNativeConstantCopy");
            Assert.That(shader, Is.Not.Null, "the compute shader is a resource");
            int one = shader.FindKernel("AssembleGlobals");
            int batch = shader.FindKernel("AssembleGlobalsBatch");

            // The frame's globals the kernels read, of this test's own making and put back afterwards.
            int lightPositionId = Shader.PropertyToID("_MainLightPosition"), lightColourId = Shader.PropertyToID("_MainLightColor");
            int spheresId = Shader.PropertyToID("_CascadeShadowSplitSpheres0"), worldToShadowId = Shader.PropertyToID("_MainLightWorldToShadow");
            Vector4 lightPositionBefore = Shader.GetGlobalVector(lightPositionId), lightColourBefore = Shader.GetGlobalVector(lightColourId);
            Vector4 spheresBefore = Shader.GetGlobalVector(spheresId);
            Matrix4x4[] worldToShadowBefore = Shader.GetGlobalMatrixArray(worldToShadowId);
            var worldToShadow = new Matrix4x4[5];
            for (int i = 0; i < worldToShadow.Length; i++) worldToShadow[i] = MatrixOf(40 + i);

            // As the route makes its GPU constant buffer on each device.
            GraphicsBuffer.Target target = SystemInfo.graphicsDeviceType == GraphicsDeviceType.Vulkan
                ? GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.Constant : GraphicsBuffer.Target.Raw;
            int words = blocks * BlockBytes / 4;
            var byOne = new GraphicsBuffer(target, words, 4) { name = "constant copy test: one a dispatch" };
            var byBatch = new GraphicsBuffer(target, words, 4) { name = "constant copy test: batch" };
            var tables = new GraphicsBuffer[blocks];
            var commands = new CommandBuffer { name = "constant copy test" };
            try
            {
                Shader.SetGlobalVector(lightPositionId, new Vector4(0.5f, -0.75f, 0.25f, 0f));
                Shader.SetGlobalVector(lightColourId, new Vector4(1.5f, 1.25f, 1f, 1f));
                Shader.SetGlobalVector(spheresId, new Vector4(3f, 4f, 5f, 36f));
                Shader.SetGlobalMatrixArray(worldToShadowId, worldToShadow);
                var zero = new uint[words];
                byOne.SetData(zero);
                byBatch.SetData(zero);

                var stereoVP = new[] { MatrixOf(20), MatrixOf(21) };
                var stereoV = new[] { MatrixOf(22), MatrixOf(23) };
                var stereoP = new[] { MatrixOf(24), MatrixOf(25) };
                var stereoCameraProjection = new[] { MatrixOf(26), MatrixOf(27) };
                var stereoPos = new[] { new Vector4(1f, 2f, 3f, 1f), new Vector4(4f, 5f, 6f, 1f) };
                int stereoVpId = Shader.PropertyToID("_VpNativeStereoMatrixVP"), stereoVId = Shader.PropertyToID("_VpNativeStereoMatrixV");
                int stereoPId = Shader.PropertyToID("_VpNativeStereoMatrixP"), stereoCameraProjectionId = Shader.PropertyToID("_VpNativeStereoCameraProjection");
                int stereoPosId = Shader.PropertyToID("_VpNativeStereoCameraPos"), constantsId = Shader.PropertyToID("_VpNativeConstants");

                var jobOffsets = new int[Capacity * Slots];
                var jobInts = new int[Capacity * 4];
                var jobVP = new Matrix4x4[Capacity];
                var jobV = new Matrix4x4[Capacity];
                var jobP = new Matrix4x4[Capacity];
                var jobBias = new Vector4[Capacity];
                var jobLight = new Vector4[Capacity];

                // One block a dispatch, as the route records it for the one-block kernel.
                for (int block = 0; block < blocks; block++)
                {
                    uint[] offsets = OffsetsOf(block % 3);
                    tables[block] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Slots, sizeof(uint));
                    tables[block].SetData(offsets);
                    Matrix4x4 vp = MatrixOf(block), v = MatrixOf(100 + block), p = MatrixOf(200 + block);
                    int multiplier = 1 + block % 2;
                    var bias = new Vector4(0.01f * (block + 1), 0.02f * (block + 1), 0f, 0f);
                    var light = new Vector3(0.1f + block, 0.2f, -0.3f);
                    float selection = block % 2;
                    commands.SetComputeBufferParam(shader, one, Shader.PropertyToID("_VpNativeOffsets"), tables[block]);
                    commands.SetComputeBufferParam(shader, one, constantsId, byOne);
                    commands.SetComputeIntParam(shader, Shader.PropertyToID("_VpNativeDestination"), block * BlockBytes);
                    commands.SetComputeMatrixParam(shader, Shader.PropertyToID("_VpNativeMatrixVP"), vp);
                    commands.SetComputeMatrixParam(shader, Shader.PropertyToID("_VpNativeMatrixV"), v);
                    commands.SetComputeMatrixParam(shader, Shader.PropertyToID("_VpNativeProjection"), p);
                    commands.SetComputeIntParam(shader, Shader.PropertyToID("_VpNativeInstanceMultiplier"), multiplier);
                    commands.SetComputeVectorParam(shader, Shader.PropertyToID("_VpNativeShadowBias"), bias);
                    commands.SetComputeVectorParam(shader, Shader.PropertyToID("_VpNativeLightDirection"), light);
                    commands.SetComputeFloatParam(shader, Shader.PropertyToID("_VpNativeShadowSliceSelection"), selection);
                    commands.SetComputeMatrixArrayParam(shader, stereoVpId, stereoVP);
                    commands.SetComputeMatrixArrayParam(shader, stereoVId, stereoV);
                    commands.SetComputeMatrixArrayParam(shader, stereoPId, stereoP);
                    commands.SetComputeMatrixArrayParam(shader, stereoCameraProjectionId, stereoCameraProjection);
                    commands.SetComputeVectorArrayParam(shader, stereoPosId, stereoPos);
                    commands.DispatchCompute(shader, one, 1, 1, 1);

                    for (int slot = 0; slot < Slots; slot++) jobOffsets[block * Slots + slot] = unchecked((int)offsets[slot]);
                    jobInts[block * 4] = block * BlockBytes;
                    jobInts[block * 4 + 1] = multiplier;
                    jobVP[block] = vp;
                    jobV[block] = v;
                    jobP[block] = p;
                    jobBias[block] = bias;
                    jobLight[block] = new Vector4(light.x, light.y, light.z, selection);
                }

                // The same blocks by one dispatch, as the route records its batch.
                commands.SetComputeBufferParam(shader, batch, constantsId, byBatch);
                commands.SetComputeIntParams(shader, Shader.PropertyToID("_VpNativeJobOffsets"), jobOffsets);
                commands.SetComputeIntParams(shader, Shader.PropertyToID("_VpNativeJobInts"), jobInts);
                commands.SetComputeMatrixArrayParam(shader, Shader.PropertyToID("_VpNativeJobMatrixVP"), jobVP);
                commands.SetComputeMatrixArrayParam(shader, Shader.PropertyToID("_VpNativeJobMatrixV"), jobV);
                commands.SetComputeMatrixArrayParam(shader, Shader.PropertyToID("_VpNativeJobProjection"), jobP);
                commands.SetComputeVectorArrayParam(shader, Shader.PropertyToID("_VpNativeJobShadowBias"), jobBias);
                commands.SetComputeVectorArrayParam(shader, Shader.PropertyToID("_VpNativeJobLightDirection"), jobLight);
                commands.SetComputeMatrixArrayParam(shader, stereoVpId, stereoVP);
                commands.SetComputeMatrixArrayParam(shader, stereoVId, stereoV);
                commands.SetComputeMatrixArrayParam(shader, stereoPId, stereoP);
                commands.SetComputeMatrixArrayParam(shader, stereoCameraProjectionId, stereoCameraProjection);
                commands.SetComputeVectorArrayParam(shader, stereoPosId, stereoPos);
                commands.DispatchCompute(shader, batch, blocks, 1, 1);
                Graphics.ExecuteCommandBuffer(commands);

                var expected = new uint[words];
                var actual = new uint[words];
                byOne.GetData(expected);
                byBatch.GetData(actual);
                int written = 0, differing = 0, firstDifference = -1;
                for (int i = 0; i < words; i++)
                {
                    if (expected[i] != 0u) written++;
                    if (expected[i] != actual[i])
                    {
                        differing++;
                        if (firstDifference < 0) firstDifference = i;
                    }
                }

                TestContext.Out.WriteLine(SystemInfo.graphicsDeviceType + ": " + blocks + " blocks, " + written + " words written by the one-block kernel, " + differing + " differing");
                Assert.That(written, Is.GreaterThan(100 * blocks), "the one-block kernel wrote the blocks (the comparison is of something)");
                Assert.That(differing, Is.Zero, firstDifference < 0 ? "" : "first difference at byte " + firstDifference * 4 + " (block " + firstDifference * 4 / BlockBytes
                    + ", offset " + firstDifference * 4 % BlockBytes + "): " + expected[firstDifference].ToString("X8") + " by one, " + actual[firstDifference].ToString("X8") + " by the batch");
            }
            finally
            {
                Shader.SetGlobalVector(lightPositionId, lightPositionBefore);
                Shader.SetGlobalVector(lightColourId, lightColourBefore);
                Shader.SetGlobalVector(spheresId, spheresBefore);
                if (worldToShadowBefore != null && worldToShadowBefore.Length > 0) Shader.SetGlobalMatrixArray(worldToShadowId, worldToShadowBefore);
                commands.Dispose();
                byOne.Dispose();
                byBatch.Dispose();
                foreach (GraphicsBuffer table in tables) table?.Dispose();
            }
        }
    }
}
