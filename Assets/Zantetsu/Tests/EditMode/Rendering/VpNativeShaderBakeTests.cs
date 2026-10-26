using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using Zantetsu.EditorTools.Rendering;

namespace Zantetsu.Rendering.Tests
{
    public class VpNativeShaderBakeTests
    {
        [Test]
        public void VulkanVariants_KeepExistingAssets_AndAddPixelClipOnlyForClipDraws()
        {
            VpNativeShaderBake.Variant[] variants = VpNativeShaderBake.GetVariants(true);
            var originals = variants.Where(v => !v.Name.EndsWith("-pixelclip", StringComparison.Ordinal)).ToArray();
            var pixel = variants.Where(v => v.Name.EndsWith("-pixelclip", StringComparison.Ordinal)).ToArray();
            Assert.That(originals.Length, Is.EqualTo(40));
            Assert.That(pixel.Length, Is.EqualTo(26));
            Assert.That(variants.Select(v => v.Name).Distinct().Count(), Is.EqualTo(66));
            Assert.That(originals.Count(v => v.Keywords.Contains(VpNativeShaderBake.NoClipKeyword)), Is.EqualTo(14));
            foreach (VpNativeShaderBake.Variant variant in originals)
            {
                Assert.That(variant.Keywords, Does.Not.Contain(VpNativeShaderBake.PixelClipKeyword), variant.Name);
                if (variant.Keywords.Contains(VpNativeShaderBake.NoClipKeyword))
                    Assert.That(pixel.Any(v => v.Name == variant.Name + "-pixelclip"), Is.False, variant.Name);
                else
                {
                    VpNativeShaderBake.Variant fallback = pixel.Single(v => v.Name == variant.Name + "-pixelclip");
                    Assert.That(fallback.Shader, Is.EqualTo(variant.Shader));
                    Assert.That(fallback.Keywords, Is.EqualTo(variant.Keywords.Concat(new[] { VpNativeShaderBake.PixelClipKeyword }).ToArray()));
                }
            }
            Assert.That(variants.SelectMany(v => v.Keywords), Does.Not.Contain(VpNativeShaderBake.StereoKeyword));
            Assert.That(VpNativeShaderBake.Variants.SelectMany(v => v.Keywords), Does.Contain(VpNativeShaderBake.StereoKeyword), "Vulkan expansion must not mutate the D3D templates");
            VpNativeShaderBake.Variant[] d3d = VpNativeShaderBake.GetVariants(false);
            Assert.That(d3d.Length, Is.EqualTo(37));
            Assert.That(d3d.SelectMany(v => v.Keywords), Does.Not.Contain(VpNativeShaderBake.PixelClipKeyword));
        }

        [Test]
        public void VulkanPixelClipAssets_DoNotRequireHardwareClip_AndDiscardInThePixelStage()
        {
            int checkedCount = 0;
            foreach (VpNativeShaderBake.Variant variant in VpNativeShaderBake.GetVariants(true))
            {
                if (!variant.Keywords.Contains(VpNativeShaderBake.PixelClipKeyword)) continue;
                VpNativeShaderBinary binary = LoadVulkan(variant.Name);
                VpNativeShaderBinary.Stage vertex = binary.FindStage(VpNativeShaderBinary.StageKind.Vertex);
                VpNativeShaderBinary.Stage pixel = binary.FindStage(VpNativeShaderBinary.StageKind.Pixel);
                Assert.That(vertex, Is.Not.Null, variant.Name);
                Assert.That(pixel, Is.Not.Null, variant.Name);
                Assert.That(VpNativeShaderBake.HasSpirvClipDistance(vertex.Bytecode), Is.False, variant.Name + " VS");
                Assert.That(VpNativeShaderBake.HasSpirvClipDistance(pixel.Bytecode), Is.False, variant.Name + " PS");
                Assert.That(VpNativeShaderBake.HasSpirvDiscard(pixel.Bytecode), Is.True, variant.Name + " must discard clipped fragments");
                checkedCount++;
            }
            Assert.That(checkedCount, Is.EqualTo(26));
        }

        [Test]
        public void VulkanNoClipAssets_HaveNoClipBufferCapabilityOrPixelDiscard()
        {
            int checkedCount = 0;
            foreach (VpNativeShaderBake.Variant variant in VpNativeShaderBake.GetVariants(true))
            {
                if (!variant.Keywords.Contains(VpNativeShaderBake.NoClipKeyword)) continue;
                VpNativeShaderBinary binary = LoadVulkan(variant.Name);
                foreach (VpNativeShaderBinary.Stage stage in binary.Stages)
                {
                    Assert.That(stage.Buffers.Any(b => b.Name == "_VpInstanceClip"), Is.False, variant.Name + " " + stage.Kind);
                    Assert.That(VpNativeShaderBake.HasSpirvClipDistance(stage.Bytecode), Is.False, variant.Name + " " + stage.Kind);
                    Assert.That(VpNativeShaderBake.HasSpirvDiscard(stage.Bytecode), Is.False, variant.Name + " " + stage.Kind);
                }
                checkedCount++;
            }
            Assert.That(checkedCount, Is.EqualTo(14));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void VulkanStencilPixelClip_DiscardsBeforeStencilIsUpdated(bool stereo)
        {
            Shader shader = Shader.Find(VpNativeShaderBake.StencilVolumeShader);
            Assert.That(shader, Is.Not.Null);
            ShaderData.Pass pass = ShaderUtil.GetShaderData(shader).GetSubshader(0).GetPass(0);
            string[] keys = stereo
                ? new[] { VpNativeShaderBake.PixelClipKeyword, "STEREO_MULTIVIEW_ON" }
                : new[] { VpNativeShaderBake.PixelClipKeyword };
            foreach (ShaderType stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
            {
                ShaderData.VariantCompileInfo info = pass.CompileVariant(stage, keys, ShaderCompilerPlatform.Vulkan, BuildTarget.Android, true);
                Assert.That(info.Success, Is.True, stage + ": " + string.Join("; ", (info.Messages ?? Array.Empty<ShaderMessage>()).Select(m => m.message)));
                Assert.That(VpNativeShaderBake.HasSpirvClipDistance(info.ShaderData), Is.False, stage.ToString());
                if (stage == ShaderType.Fragment)
                {
                    Assert.That(VpNativeShaderBake.HasSpirvDiscard(info.ShaderData), Is.True, "fallback discards clipped fragments");
                    Assert.That(VpNativeShaderBake.HasSpirvEarlyFragmentTests(info.ShaderData), Is.False,
                        "forced early tests would modify stencil even for discarded fragments");
                }
            }
        }

        private static VpNativeShaderBinary LoadVulkan(string name)
        {
            var asset = Resources.Load<TextAsset>("VpNativeVulkan/" + name);
            Assert.That(asset, Is.Not.Null, name + " must be baked and included in Resources");
            Assert.That(VpNativeShaderBinary.TryRead(asset.bytes, out VpNativeShaderBinary binary, out string failure), Is.True, failure);
            return binary;
        }

        [TestCase(252u)]
        [TestCase(4416u)]
        [TestCase(5380u)]
        public void SpirvDiscard_RecognizesFragmentTerminationInstructions(uint opcode)
        {
            Assert.That(VpNativeShaderBake.HasSpirvDiscard(Spirv(Instruction(opcode))), Is.True);
            Assert.That(VpNativeShaderBake.HasSpirvDiscard(Spirv(Instruction(43, 1, 2, opcode))), Is.False,
                "an operand equal to an opcode is not an instruction");
        }

        [Test]
        public void SpirvEarlyFragmentTests_DetectsExecutionModeButNotMatchingOperandValues()
        {
            Assert.That(VpNativeShaderBake.HasSpirvEarlyFragmentTests(Spirv(Instruction(16, 100, 9))), Is.True);
            Assert.That(VpNativeShaderBake.HasSpirvEarlyFragmentTests(Spirv(Instruction(16, 100, 7))), Is.False,
                "OriginUpperLeft is a different execution mode");
            Assert.That(VpNativeShaderBake.HasSpirvEarlyFragmentTests(Spirv(Instruction(16, 9, 7))), Is.False,
                "an entry-point ID of 9 does not request EarlyFragmentTests");
            Assert.That(VpNativeShaderBake.HasSpirvEarlyFragmentTests(Spirv(Instruction(43, 1, 2, 9))), Is.False,
                "a constant value of 9 is not an execution mode");
            Assert.That(VpNativeShaderBake.HasSpirvEarlyFragmentTests(Spirv(Instruction(16, 100, 7), Instruction(16, 100, 9))), Is.True);
            Assert.Throws<InvalidDataException>(() => VpNativeShaderBake.HasSpirvEarlyFragmentTests(Spirv(Instruction(16, 100))));
        }

        [Test]
        public void SpirvClipDistance_IgnoresDormantPerVertexMember_ButDetectsItsUse()
        {
            var declarations = new List<uint[]>
            {
                Instruction(72, 100, 1, 11, 3), // gl_PerVertex member 1 has BuiltIn ClipDistance.
                Instruction(32, 200, 3, 100),   // pointer to output block.
                Instruction(59, 200, 300, 3),  // output variable.
                Instruction(43, 400, 500, 1),  // index of the ClipDistance member.
                Instruction(43, 400, 501, 0),  // index of an ordinary member.
            };
            Assert.That(VpNativeShaderBake.HasSpirvClipDistance(Spirv(declarations.ToArray())), Is.False);
            declarations.Add(Instruction(65, 200, 600, 300, 501));
            Assert.That(VpNativeShaderBake.HasSpirvClipDistance(Spirv(declarations.ToArray())), Is.False, "an ordinary member access is not Clip");
            declarations.Add(Instruction(65, 200, 601, 300, 500));
            Assert.That(VpNativeShaderBake.HasSpirvClipDistance(Spirv(declarations.ToArray())), Is.True);
            declarations.RemoveAt(declarations.Count - 1);
            declarations.Add(Instruction(62, 300, 700));
            Assert.That(VpNativeShaderBake.HasSpirvClipDistance(Spirv(declarations.ToArray())), Is.True, "a whole output-block store also writes ClipDistance");
            Assert.That(VpNativeShaderBake.HasSpirvClipDistance(Spirv(Instruction(17, 32))), Is.True, "the capability alone requires device support");
            Assert.That(VpNativeShaderBake.HasSpirvClipDistance(Spirv(Instruction(71, 10, 11, 3))), Is.True, "standalone ClipDistance built-in");
        }

        [Test]
        public void SpirvInspection_RejectsNonSpirvAndMalformedInstructions()
        {
            byte[][] invalid = { null, new byte[0], new byte[20], Spirv(new uint[] { 252 }), Spirv(new uint[] { (4u << 16) | 43u, 1u }) };
            foreach (byte[] bytes in invalid)
            {
                Assert.Throws<InvalidDataException>(() => VpNativeShaderBake.HasSpirvDiscard(bytes));
                Assert.Throws<InvalidDataException>(() => VpNativeShaderBake.HasSpirvClipDistance(bytes));
                Assert.Throws<InvalidDataException>(() => VpNativeShaderBake.HasSpirvEarlyFragmentTests(bytes));
            }
        }

        // Minimal instruction streams exercise bytecode inspection; they are not full executable SPIR-V modules.
        private static byte[] Spirv(params uint[][] instructions)
        {
            var words = new List<uint> { 0x07230203, 0x00010000, 0, 1024, 0 };
            foreach (uint[] instruction in instructions) words.AddRange(instruction);
            byte[] bytes = new byte[words.Count * 4];
            Buffer.BlockCopy(words.ToArray(), 0, bytes, 0, bytes.Length);
            return bytes;
        }

        private static uint[] Instruction(uint opcode, params uint[] operands)
        {
            var words = new uint[operands.Length + 1];
            words[0] = ((uint)words.Length << 16) | opcode;
            Array.Copy(operands, 0, words, 1, operands.Length);
            return words;
        }
    }
}
