using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.EditorTools.Rendering
{
    /// <summary>
    /// Compiles, with Unity's own shader compiler, the variants of the two indirect VP shaders that the Direct3D 12 plugin
    /// draws (DESIGN 4.5.8; keyword VP_NATIVE_MULTIDRAW), and writes each stage's bytecode with what Unity reports it
    /// binds -- constant buffers and their constants, textures and samplers, buffers. The plugin builds its pipeline
    /// state from these; nothing of Unity's own binding of the ordinary variants is inferred. Beside each plugin variant
    /// the ordinary variant of the same pass is written too, so the two can be compared binding by binding.
    /// <code>
    /// Unity.exe -batchmode -projectPath &lt;project&gt; -executeMethod Zantetsu.EditorTools.Rendering.VpNativeShaderBake.Batch
    ///     -zantetsuNativeBakeOut &lt;new folder&gt; -logFile &lt;log&gt;
    /// </code>
    /// Exit code 0: every variant compiled; 2: some did not (the report says which); 1: it could not be done. Beside the
    /// report and the raw bytecode, the folder gets <c>assets/VpNative/&lt;name&gt;.bytes</c> (+ .meta): the
    /// <see cref="VpNativeShaderBinary"/> files to put under a Resources folder of the project.
    /// </summary>
    public static class VpNativeShaderBake
    {
        public const string ForwardShader = "Zantetsu/VP Indexed Indirect Unlit";
        public const string ShadowCasterShader = "Zantetsu/VP Indexed Indirect Shadow Caster";
        public const string PluginKeyword = "VP_NATIVE_MULTIDRAW";
        public const string GpuCulledKeyword = "VP_GPU_CULLED";

        public readonly struct Variant
        {
            public readonly string Name;
            public readonly string Shader;
            public readonly string[] Keywords;

            public Variant(string name, string shader, params string[] keywords)
            {
                Name = name;
                Shader = shader;
                Keywords = keywords;
            }
        }

        /// <summary>
        /// The variants written. The lighting keywords of the body are those of the comparison case's pipeline (cascaded
        /// main light shadows, soft shadows at the asset's quality); the plugin checks at run time that the pipeline has
        /// these on, and refuses to draw otherwise.
        /// </summary>
        public static readonly Variant[] Variants =
        {
            new Variant("forward-unity-cascade-soft", ForwardShader, GpuCulledKeyword, "_MAIN_LIGHT_SHADOWS_CASCADE", "_SHADOWS_SOFT"),
            new Variant("forward-plugin-noshadow", ForwardShader, GpuCulledKeyword, PluginKeyword),
            new Variant("forward-plugin-shadows", ForwardShader, GpuCulledKeyword, PluginKeyword, "_MAIN_LIGHT_SHADOWS"),
            new Variant("forward-plugin-shadows-soft", ForwardShader, GpuCulledKeyword, PluginKeyword, "_MAIN_LIGHT_SHADOWS", "_SHADOWS_SOFT"),
            new Variant("forward-plugin-cascade", ForwardShader, GpuCulledKeyword, PluginKeyword, "_MAIN_LIGHT_SHADOWS_CASCADE"),
            new Variant("forward-plugin-cascade-soft", ForwardShader, GpuCulledKeyword, PluginKeyword, "_MAIN_LIGHT_SHADOWS_CASCADE", "_SHADOWS_SOFT"),
            new Variant("forward-plugin-noshadow-stereo", ForwardShader, GpuCulledKeyword, PluginKeyword, StereoKeyword),
            new Variant("forward-plugin-shadows-stereo", ForwardShader, GpuCulledKeyword, PluginKeyword, StereoKeyword, "_MAIN_LIGHT_SHADOWS"),
            new Variant("forward-plugin-shadows-soft-stereo", ForwardShader, GpuCulledKeyword, PluginKeyword, StereoKeyword, "_MAIN_LIGHT_SHADOWS", "_SHADOWS_SOFT"),
            new Variant("forward-plugin-cascade-stereo", ForwardShader, GpuCulledKeyword, PluginKeyword, StereoKeyword, "_MAIN_LIGHT_SHADOWS_CASCADE"),
            new Variant("forward-plugin-cascade-soft-stereo", ForwardShader, GpuCulledKeyword, PluginKeyword, StereoKeyword, "_MAIN_LIGHT_SHADOWS_CASCADE", "_SHADOWS_SOFT"),
            new Variant("caster-unity", ShadowCasterShader, GpuCulledKeyword),
            new Variant("caster-plugin", ShadowCasterShader, GpuCulledKeyword, PluginKeyword),
        };

        /// <summary>
        /// Unity's Single Pass Instanced stereo variant: both eyes in one draw, the eye from the instance number, the
        /// slice from SV_RenderTargetArrayIndex, the matrices from UnityStereoGlobals -- Unity's own shader code,
        /// given its constants by the plugin.
        /// </summary>
        public const string StereoKeyword = "STEREO_INSTANCING_ON";

        public static void Batch()
        {
            int code = 1;
            try
            {
                string folder = Arg("-zantetsuNativeBakeOut");
                if (string.IsNullOrEmpty(folder)) throw new ArgumentException("-zantetsuNativeBakeOut <folder> is required");
                if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length > 0) throw new IOException("the output folder is not empty: " + folder);
                Directory.CreateDirectory(folder);
                var report = new StringBuilder();
                report.AppendLine("VpNativeShaderBake " + DateTime.Now.ToString("o") + " Unity " + Application.unityVersion);
                bool allCompiled = true;
                foreach (Variant variant in Variants)
                {
                    allCompiled &= Bake(variant, folder, report);
                }

                File.WriteAllText(Path.Combine(folder, "bake.txt"), report.ToString());
                Debug.Log(report.ToString());
                code = allCompiled ? 0 : 2;
            }
            catch (Exception e)
            {
                Debug.LogException(e);
                code = 1;
            }

            EditorApplication.Exit(code);
        }

        private static bool Bake(Variant variant, string folder, StringBuilder report)
        {
            report.AppendLine();
            report.AppendLine("== " + variant.Name + ": " + variant.Shader + " [" + string.Join(" ", variant.Keywords) + "]");
            Shader shader = Shader.Find(variant.Shader);
            if (shader == null)
            {
                report.AppendLine("  NOT FOUND");
                return false;
            }

            ShaderData data = ShaderUtil.GetShaderData(shader);
            if (data.SubshaderCount < 1 || data.GetSubshader(0).PassCount < 1)
            {
                report.AppendLine("  no pass");
                return false;
            }

            ShaderData.Pass pass = data.GetSubshader(0).GetPass(0);
            report.AppendLine("  pass " + pass.Name + " subshaders " + data.SubshaderCount + " passes " + data.GetSubshader(0).PassCount);
            bool compiled = true;
            var binary = new VpNativeShaderBinary
            {
                Name = variant.Name, Shader = variant.Shader, Keywords = string.Join(" ", variant.Keywords), UnityVersion = Application.unityVersion,
            };
            foreach (ShaderType stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
            {
                string suffix = stage == ShaderType.Vertex ? "vs" : "ps";

                // forExternalTool: the bytes are the DXBC alone, from its first byte (checked 2026-10-07; without it
                // Unity's own 38-byte header precedes the same DXBC). Unity strips the reflection chunk either way.
                ShaderData.VariantCompileInfo info = pass.CompileVariant(stage, variant.Keywords, ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64, true);
                report.AppendLine("  " + suffix + ": success " + info.Success + "; bytes " + (info.ShaderData != null ? info.ShaderData.Length : 0));
                foreach (ShaderMessage message in info.Messages ?? Array.Empty<ShaderMessage>())
                {
                    report.AppendLine("    message " + message.severity + " " + message.message + " (" + message.file + ":" + message.line + ")");
                }

                if (!info.Success)
                {
                    compiled = false;
                    continue;
                }

                File.WriteAllBytes(Path.Combine(folder, variant.Name + "." + suffix + ".bin"), info.ShaderData ?? Array.Empty<byte>());
                Describe(info, report);
                binary.Stages.Add(ToStage(stage == ShaderType.Vertex ? VpNativeShaderBinary.StageKind.Vertex : VpNativeShaderBinary.StageKind.Pixel, info));
            }

            if (!compiled)
            {
                return false;
            }

            // The asset a Player loads (Resources/VpNative/<name>.bytes), with a meta whose GUID is the name's, so a
            // re-bake replaces the asset in place.
            string assets = Path.Combine(folder, "assets", VpNativeShaderBinary.ResourceFolder);
            Directory.CreateDirectory(assets);
            byte[] bytes = binary.Write();
            File.WriteAllBytes(Path.Combine(assets, variant.Name + ".bytes"), bytes);
            File.WriteAllText(Path.Combine(assets, variant.Name + ".bytes.meta"),
                "fileFormatVersion: 2\nguid: " + GuidOf(variant.Name) + "\nTextScriptImporter:\n  externalObjects: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n");
            report.AppendLine("  asset " + variant.Name + ".bytes " + bytes.Length + " bytes, guid " + GuidOf(variant.Name));
            if (!VpNativeShaderBinary.TryRead(bytes, out VpNativeShaderBinary readBack, out string failure) || readBack.Stages.Count != binary.Stages.Count)
            {
                report.AppendLine("  READ-BACK FAILED: " + failure);
                return false;
            }

            return true;
        }

        private static VpNativeShaderBinary.Stage ToStage(VpNativeShaderBinary.StageKind kind, ShaderData.VariantCompileInfo info)
        {
            var stage = new VpNativeShaderBinary.Stage { Kind = kind, Bytecode = info.ShaderData ?? Array.Empty<byte>() };
            var registers = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (ShaderData.ResourceBindingInfo resource in info.ResourceBindings ?? Array.Empty<ShaderData.ResourceBindingInfo>())
            {
                if (resource.Kind == ShaderData.ResourceKind.ConstantBuffer)
                {
                    registers[resource.Name] = resource.Index;
                }
                else if (resource.Kind == ShaderData.ResourceKind.Buffer || resource.Kind == ShaderData.ResourceKind.TypedBuffer)
                {
                    stage.Buffers.Add(new VpNativeShaderBinary.BufferBinding { Name = resource.Name, Register = resource.Index });
                }
                else if (resource.Kind == ShaderData.ResourceKind.Sampler)
                {
                    // A sampler Unity reports without a texture of its own: an inline sampler state of the shader
                    // (in these shaders the shadow map's comparison sampler). Its name is not reported.
                    stage.Samplers.Add(new VpNativeShaderBinary.SamplerBinding { Name = resource.Name ?? "", Register = resource.Index });
                }
            }

            foreach (ShaderData.ConstantBufferInfo buffer in info.ConstantBuffers ?? Array.Empty<ShaderData.ConstantBufferInfo>())
            {
                var binding = new VpNativeShaderBinary.ConstantBuffer
                {
                    Name = buffer.Name, Size = buffer.Size, Register = registers.TryGetValue(buffer.Name, out int register) ? register : -1,
                };
                foreach (ShaderData.ConstantInfo constant in buffer.Fields ?? Array.Empty<ShaderData.ConstantInfo>())
                {
                    binding.Constants.Add(new VpNativeShaderBinary.Constant
                    {
                        Name = constant.Name, Offset = constant.Index, Rows = constant.Rows, Columns = constant.Columns, ArraySize = constant.ArraySize,
                    });
                }

                stage.ConstantBuffers.Add(binding);
            }

            foreach (ShaderData.TextureBindingInfo texture in info.TextureBindings ?? Array.Empty<ShaderData.TextureBindingInfo>())
            {
                stage.Textures.Add(new VpNativeShaderBinary.TextureBinding
                {
                    Name = texture.Name, Register = texture.Index, SamplerRegister = texture.SamplerIndex, Dimension = (int)texture.Dim, ArraySize = texture.ArraySize,
                });
            }

            return stage;
        }

        private static string GuidOf(string name)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes("Zantetsu.VpNativeShaderBinary:" + name));
                var text = new StringBuilder(32);
                foreach (byte b in hash) text.Append(b.ToString("x2"));
                return text.ToString();
            }
        }

        private static void Describe(ShaderData.VariantCompileInfo info, StringBuilder report)
        {
            byte[] bytes = info.ShaderData ?? Array.Empty<byte>();
            report.AppendLine("    DXBC magic at " + IndexOf(bytes, Encoding.ASCII.GetBytes("DXBC")) + "; DXIL magic at " + IndexOf(bytes, Encoding.ASCII.GetBytes("DXIL")));
            report.AppendLine("    attributes: " + string.Join(" ", info.Attributes ?? Array.Empty<UnityEngine.Rendering.VertexAttribute>()));
            foreach (ShaderData.ConstantBufferInfo buffer in info.ConstantBuffers ?? Array.Empty<ShaderData.ConstantBufferInfo>())
            {
                report.AppendLine("    cbuffer " + buffer.Name + " size " + buffer.Size);
                foreach (ShaderData.ConstantInfo constant in buffer.Fields ?? Array.Empty<ShaderData.ConstantInfo>())
                {
                    report.AppendLine(string.Format(CultureInfo.InvariantCulture, "      {0} offset {1} {2} {3} {4}x{5} array {6} struct {7}",
                        constant.Name, constant.Index, constant.ConstantType, constant.DataType, constant.Rows, constant.Columns, constant.ArraySize, constant.StructSize));
                }
            }

            foreach (ShaderData.TextureBindingInfo texture in info.TextureBindings ?? Array.Empty<ShaderData.TextureBindingInfo>())
            {
                report.AppendLine("    texture " + texture.Name + " index " + texture.Index + " sampler " + texture.SamplerIndex + " dim " + texture.Dim
                    + " array " + texture.ArraySize + (texture.Multisampled ? " multisampled" : ""));
            }

            foreach (ShaderData.ResourceBindingInfo resource in info.ResourceBindings ?? Array.Empty<ShaderData.ResourceBindingInfo>())
            {
                report.AppendLine("    resource " + resource.Name + " " + resource.Kind + " index " + resource.Index + " sampler " + resource.SamplerIndex + (resource.Writable ? " writable" : ""));
            }
        }

        private static int IndexOf(byte[] bytes, byte[] pattern)
        {
            for (int i = 0; i + pattern.Length <= bytes.Length; i++)
            {
                int j = 0;
                while (j < pattern.Length && bytes[i + j] == pattern[j]) j++;
                if (j == pattern.Length) return i;
            }

            return -1;
        }

        private static string Arg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (string.Equals(args[i], name, StringComparison.Ordinal)) return args[i + 1];
            }

            return null;
        }
    }
}
