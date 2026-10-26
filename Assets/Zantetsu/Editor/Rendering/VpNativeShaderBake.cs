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
    /// Compiles, with Unity's own shader compiler, the variants of the indirect VP shaders that the Direct3D 12 / Android Vulkan plugins
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
        public const string StencilVolumeShader = "Zantetsu/VP Stencil Volume";
        public const string PluginKeyword = "VP_NATIVE_MULTIDRAW";
        public const string GpuCulledKeyword = "VP_GPU_CULLED";
        public const string NoClipKeyword = "VP_NO_CLIP";
        public const string DirectKeyword = "VP_NATIVE_DIRECT";
        public const string PixelClipKeyword = "VP_PIXEL_CLIP";

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

        private static bool s_vulkan;
        public static void VulkanBatch() { s_vulkan = true; Batch(); }
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
                foreach (Variant variant in GetVariants(s_vulkan))
                    allCompiled &= Bake(variant, folder, report);
                if (s_vulkan)
                {
                    // Stencil uses Unity draws, not native pipeline assets. Still compile its fallback here: forced
                    // early tests would update stencil before discard and mark the part that should have been cut away.
                    allCompiled &= Bake(new Variant("stencil-volume-pixelclip", StencilVolumeShader, PixelClipKeyword), folder, report, false);
                    allCompiled &= Bake(new Variant("stencil-volume-stereo-pixelclip", StencilVolumeShader, PixelClipKeyword, "STEREO_MULTIVIEW_ON"), folder, report, false);
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

        /// <summary>
        /// Preserve the hardware-Clip and ordinary-object assets. Vulkan additionally gets a pixel-Clip version of
        /// each Clip-enabled variant; ordinary objects retain their existing variant, with no Clip work in either stage.
        /// </summary>
        public static Variant[] GetVariants(bool vulkan)
        {
            var variants = new List<Variant>(Variants);
            if (vulkan) variants.Add(new Variant("motion-plugin-stereo", ForwardShader, GpuCulledKeyword, PluginKeyword, "STEREO_MULTIVIEW_ON"));
            // Direct Clip draws are the Vulkan route's. Direct3D 12 draws clipped instances from a clip list instead:
            // the plugin's variant without selection ("-list"), reading the entry's instance transform and clip record.
            foreach (Variant original in variants.ToArray())
            {
                var noClipKeys = new List<string>(original.Keywords) { NoClipKeyword };
                variants.Add(new Variant(original.Name + "-noclip", original.Shader, noClipKeys.ToArray()));
                if (!vulkan && original.Name.Contains("-plugin"))
                {
                    var listKeys = new List<string>(original.Keywords);
                    listKeys.Remove(GpuCulledKeyword);
                    variants.Add(new Variant(original.Name.Replace("-plugin", "-list"), original.Shader, listKeys.ToArray()));
                }
                if (vulkan && original.Name.Contains("-plugin"))
                {
                    var directKeys = new List<string>(original.Keywords);
                    directKeys.Remove(GpuCulledKeyword);
                    directKeys.Remove(PluginKeyword);
                    directKeys.Add(DirectKeyword);
                    variants.Add(new Variant(original.Name.Replace("-plugin", "-direct"), original.Shader, directKeys.ToArray()));
                }
            }
            if (vulkan)
            {
                foreach (Variant original in variants.ToArray())
                {
                    if (Array.IndexOf(original.Keywords, NoClipKeyword) >= 0) continue;
                    var pixelClipKeys = new List<string>(original.Keywords) { PixelClipKeyword };
                    variants.Add(new Variant(original.Name + "-pixelclip", original.Shader, pixelClipKeys.ToArray()));
                }
                for (int v = 0; v < variants.Count; v++)
                {
                    Variant original = variants[v];
                    var keys = (string[])original.Keywords.Clone();
                    for (int i = 0; i < keys.Length; i++) if (keys[i] == StereoKeyword) keys[i] = "STEREO_MULTIVIEW_ON";
                    variants[v] = new Variant(original.Name, original.Shader, keys);
                }
            }
            return variants.ToArray();
        }

        private static bool Bake(Variant variant, string folder, StringBuilder report, bool writeAsset = true)
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

            ShaderData.Pass pass = data.GetSubshader(0).GetPass(variant.Name.StartsWith("motion-") ? 1 : 0);
            report.AppendLine("  pass " + pass.Name + " subshaders " + data.SubshaderCount + " passes " + data.GetSubshader(0).PassCount);
            bool compiled = true;
            var binary = new VpNativeShaderBinary
            {
                Name = variant.Name, Shader = variant.Shader, Keywords = string.Join(" ", variant.Keywords), UnityVersion = Application.unityVersion,
            };
            foreach (ShaderType stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
            {
                string suffix = stage == ShaderType.Vertex ? "vs" : "ps";

                // forExternalTool: D3D returns raw DXBC and Vulkan returns raw SPIR-V. For D3D (checked 2026-10-07), without it
                // Unity's own 38-byte header precedes the same DXBC). Unity strips the reflection chunk either way.
                ShaderData.VariantCompileInfo info = pass.CompileVariant(stage, variant.Keywords, s_vulkan ? ShaderCompilerPlatform.Vulkan : ShaderCompilerPlatform.D3D, s_vulkan ? BuildTarget.Android : BuildTarget.StandaloneWindows64, true);
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

            if (!compiled || !VerifyBindingCapacity(binary, report) || !VerifyClipVariant(variant, binary, report))
            {
                return false;
            }

            if (!writeAsset)
            {
                report.AppendLine("  validation-only shader; no native asset");
                return true;
            }

            // The asset a Player loads (Resources/VpNative/<name>.bytes), with a meta whose GUID is the name's, so a
            // re-bake replaces the asset in place.
            string assets = Path.Combine(folder, "assets", s_vulkan ? "VpNativeVulkan" : VpNativeShaderBinary.ResourceFolder);
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

        // The event ring has separate 16-entry arrays for resource and constant bindings on Vulkan. Count the
        // stage-qualified bindings the managed pipeline creates, including repeated names in VS and PS. A sampler
        // is pipeline-static and VpNativeCommand is the native command constant, so neither uses a draw array slot.
        private static bool VerifyBindingCapacity(VpNativeShaderBinary binary, StringBuilder report)
        {
            int constants = 0, resources = 0;
            foreach (VpNativeShaderBinary.Stage stage in binary.Stages)
            {
                foreach (VpNativeShaderBinary.ConstantBuffer buffer in stage.ConstantBuffers)
                    if (buffer.Name != VpNativePipeline.CommandConstantBuffer) constants++;
                resources += stage.Buffers.Count + stage.Textures.Count;
            }
            int constantCapacity = s_vulkan ? 16 : 8;
            bool valid = constants <= constantCapacity && resources <= 16;
            report.AppendLine("  draw binding capacity gate: " + (valid ? "passed" : "FAILED")
                + "; constants " + constants + "/" + constantCapacity + ", resources " + resources + "/16");
            return valid;
        }

        // Verify the compiled shader rather than relying on a source-level branch. This is the gate that makes an
        // ordinary object's VS free of Clip buffers, work and outputs, and keeps the small direct path free of GPU
        // selection/argument/instance buffers. SPIR-V's ClipDistance capability and built-in cover the output contract.
        private static bool VerifyClipVariant(Variant variant, VpNativeShaderBinary binary, StringBuilder report)
        {
            VpNativeShaderBinary.Stage vertex = binary.FindStage(VpNativeShaderBinary.StageKind.Vertex);
            VpNativeShaderBinary.Stage pixel = binary.FindStage(VpNativeShaderBinary.StageKind.Pixel);
            bool pixelClip = Array.IndexOf(variant.Keywords, PixelClipKeyword) >= 0;
            bool noClip = Array.IndexOf(variant.Keywords, NoClipKeyword) >= 0;
            bool direct = Array.IndexOf(variant.Keywords, DirectKeyword) >= 0;
            bool valid = vertex != null && pixel != null;
            if (noClip)
            {
                valid &= vertex != null && !vertex.Buffers.Exists(b => b.Name == "_VpInstanceClip");
                valid &= pixel != null && !pixel.Buffers.Exists(b => b.Name == "_VpInstanceClip");
                if (s_vulkan && vertex != null && pixel != null)
                    valid &= !HasSpirvClipDistance(vertex.Bytecode) && !HasSpirvClipDistance(pixel.Bytecode) && !HasSpirvDiscard(pixel.Bytecode);
                if (!s_vulkan && vertex != null) valid &= !HasDxbcClipDistanceOutput(vertex.Bytecode, report);
                report.AppendLine("  no-Clip stages gate: " + (valid ? "passed" : "FAILED (Clip buffer/capability/output/discard remains)"));
            }
            else if (pixelClip)
            {
                bool noHardwareClip = s_vulkan && vertex != null && pixel != null
                    && !HasSpirvClipDistance(vertex.Bytecode) && !HasSpirvClipDistance(pixel.Bytecode);
                bool discards = pixel != null && s_vulkan && HasSpirvDiscard(pixel.Bytecode);
                bool stencilOrder = variant.Shader != StencilVolumeShader || (pixel != null && s_vulkan && !HasSpirvEarlyFragmentTests(pixel.Bytecode));
                valid &= noHardwareClip && discards && stencilOrder;
                report.AppendLine("  pixel-Clip stages gate: " + (noHardwareClip && discards && stencilOrder
                    ? "passed (no hardware Clip in VS/PS; PS discards; stencil order safe)"
                    : "FAILED (no hardware Clip " + noHardwareClip + ", PS discards " + discards + ", stencil order safe " + stencilOrder + ")"));
            }
            else if (!s_vulkan && !direct && vertex != null)
            {
                // A Direct3D variant that keeps the clip: it must bind the clip buffer and declare the output.
                bool clip = vertex.Buffers.Exists(b => b.Name == "_VpInstanceClip") && HasDxbcClipDistanceOutput(vertex.Bytecode, report);
                bool list = variant.Name.Contains("-list");
                if (list)
                {
                    string[] absent = { "_VpVisible", "_VpCullCommands", "unity_IndirectDrawArgs" };
                    bool unselected = true;
                    foreach (string name in absent) unselected &= !vertex.Buffers.Exists(b => b.Name == name);
                    bool entries = vertex.Buffers.Exists(b => b.Name == "_VpNativeArguments") && vertex.Buffers.Exists(b => b.Name == "_VpInstanceObjectToWorld");
                    valid &= clip && unselected && entries;
                    report.AppendLine("  clip list VS gate: " + (clip && unselected && entries ? "passed (entries, instance transform and clip record; no selection)" : "FAILED (clip " + clip + ", no selection " + unselected + ", entries " + entries + ")"));
                }
                else
                {
                    report.AppendLine("  Clip VS (kept): clip buffer and SV_ClipDistance output " + (clip ? "present" : "ABSENT"));
                }
            }
            if (direct)
            {
                string[] absent = { "_VpNativeArguments", "unity_IndirectDrawArgs", "_VpVisible", "_VpInstanceObjectToWorld", "_VpInstanceClip", "_VpCullCommands" };
                if (vertex != null)
                {
                    foreach (string name in absent) valid &= !vertex.Buffers.Exists(b => b.Name == name);
                    VpNativeShaderBinary.ConstantBuffer records = vertex.FindConstantBuffer("VpNativeDirect");
                    valid &= records != null && records.Size == 32 * 208;
                    valid &= vertex.Buffers.Count == 1 && vertex.Buffers[0].Name == "_VpVertices";
                    if (s_vulkan) valid &= pixelClip ? !HasSpirvClipDistance(vertex.Bytecode) : HasSpirvClipDistance(vertex.Bytecode);
                }
                valid &= !noClip;
                report.AppendLine("  direct Clip VS gate: " + (valid ? "passed (vertices + 6656-byte record UBO; Clip)" : "FAILED (unexpected buffer/record layout/output)"));
            }
            return valid;
        }

        /// <summary>
        /// Whether a Direct3D shader's output signature declares SV_ClipDistance: read from the compiled bytecode's
        /// own signature chunk (OSGN, or OSG5 / OSG1), which Unity leaves in when it strips the reflection. A vertex
        /// stage that writes no clip distance has no such element; the hardware clips nothing for it.
        /// </summary>
        public static bool HasDxbcClipDistanceOutput(byte[] bytes, StringBuilder report = null)
        {
            if (bytes == null || bytes.Length < 32 || bytes[0] != 'D' || bytes[1] != 'X' || bytes[2] != 'B' || bytes[3] != 'C')
            {
                throw new InvalidDataException("not a DXBC container");
            }

            int chunks = BitConverter.ToInt32(bytes, 28);
            bool found = false, signature = false;
            var names = new List<string>();
            for (int c = 0; c < chunks; c++)
            {
                int at = BitConverter.ToInt32(bytes, 32 + c * 4);
                string tag = Encoding.ASCII.GetString(bytes, at, 4);
                int stride = tag == "OSGN" ? 24 : tag == "OSG5" ? 28 : tag == "OSG1" ? 32 : 0;
                if (stride == 0) continue;
                signature = true;
                int data = at + 8, count = BitConverter.ToInt32(bytes, data), nameField = tag == "OSGN" ? 0 : 4;
                for (int e = 0; e < count; e++)
                {
                    int element = data + 8 + e * stride;
                    int name = data + BitConverter.ToInt32(bytes, element + nameField), end = name;
                    while (end < bytes.Length && bytes[end] != 0) end++;
                    string semantic = Encoding.ASCII.GetString(bytes, name, end - name);
                    int systemValue = BitConverter.ToInt32(bytes, element + nameField + 8);
                    names.Add(semantic + BitConverter.ToInt32(bytes, element + nameField + 4));
                    // D3D_NAME_CLIP_DISTANCE is 2; the name is checked as well, whatever the system value says.
                    found |= systemValue == 2 || semantic.Equals("SV_ClipDistance", StringComparison.OrdinalIgnoreCase);
                }
            }

            if (!signature) throw new InvalidDataException("the DXBC has no output signature chunk");
            report?.AppendLine("    output signature: " + string.Join(" ", names) + "; SV_ClipDistance " + (found ? "present" : "absent"));
            return found;
        }

        /// <summary>Whether SPIR-V requires ClipDistance or actively accesses its built-in output.</summary>
        public static bool HasSpirvClipDistance(byte[] bytes)
        {
            VerifySpirvHeader(bytes);
            // Unity declares a dormant float[1] ClipDistance member in its common gl_PerVertex block even without
            // the capability or a write. A decoration alone therefore cannot establish an active Clip output.
            // Check the capability, standalone built-in, and actual accesses to the decorated output member.
            var pointerTargets = new Dictionary<uint, uint>();
            var outputTypes = new Dictionary<uint, uint>();
            var clipMembers = new Dictionary<uint, uint>();
            var constants = new Dictionary<uint, uint>();
            var accesses = new List<(uint output, uint member)>();
            var wholeStores = new HashSet<uint>();
            for (int at = 20; at < bytes.Length;)
            {
                uint instruction = BitConverter.ToUInt32(bytes, at);
                int length = (int)(instruction >> 16) * 4;
                if (length < 4 || at + length > bytes.Length) throw new InvalidDataException("invalid SPIR-V instruction");
                uint op = instruction & 65535;
                if (op == 17 && length >= 8 && BitConverter.ToUInt32(bytes, at + 4) == 32) return true;
                if (op == 71 && length >= 16 && BitConverter.ToUInt32(bytes, at + 8) == 11 && BitConverter.ToUInt32(bytes, at + 12) == 3) return true;
                if (op == 72 && length >= 20 && BitConverter.ToUInt32(bytes, at + 12) == 11 && BitConverter.ToUInt32(bytes, at + 16) == 3)
                    clipMembers[BitConverter.ToUInt32(bytes, at + 4)] = BitConverter.ToUInt32(bytes, at + 8);
                if (op == 32 && length >= 16)
                    pointerTargets[BitConverter.ToUInt32(bytes, at + 4)] = BitConverter.ToUInt32(bytes, at + 12);
                if (op == 43 && length >= 16)
                    constants[BitConverter.ToUInt32(bytes, at + 8)] = BitConverter.ToUInt32(bytes, at + 12);
                if (op == 59 && length >= 16 && BitConverter.ToUInt32(bytes, at + 12) == 3)
                    outputTypes[BitConverter.ToUInt32(bytes, at + 8)] = BitConverter.ToUInt32(bytes, at + 4);
                if ((op == 65 || op == 66) && length >= 20)
                    accesses.Add((BitConverter.ToUInt32(bytes, at + 12), BitConverter.ToUInt32(bytes, at + 16)));
                if (op == 62 && length >= 12) wholeStores.Add(BitConverter.ToUInt32(bytes, at + 4));
                at += length;
            }
            foreach (var access in accesses)
            {
                if (outputTypes.TryGetValue(access.output, out uint pointer) && pointerTargets.TryGetValue(pointer, out uint structure)
                    && clipMembers.TryGetValue(structure, out uint member) && constants.TryGetValue(access.member, out uint index)
                    && index == member) return true;
            }
            foreach (var output in outputTypes)
                if (pointerTargets.TryGetValue(output.Value, out uint structure) && clipMembers.ContainsKey(structure)
                    && wholeStores.Contains(output.Key)) return true;
            return false;
        }

        /// <summary>Whether compiled SPIR-V contains an instruction that discards a fragment invocation.</summary>
        public static bool HasSpirvDiscard(byte[] bytes)
        {
            VerifySpirvHeader(bytes);
            bool found = false;
            for (int at = 20; at < bytes.Length;)
            {
                uint instruction = BitConverter.ToUInt32(bytes, at);
                int length = (int)(instruction >> 16) * 4;
                if (length < 4 || at + length > bytes.Length) throw new InvalidDataException("invalid SPIR-V instruction");
                uint op = instruction & 65535;
                // OpKill, OpTerminateInvocation, OpDemoteToHelperInvocation (also its EXT alias).
                found |= op == 252 || op == 4416 || op == 5380;
                at += length;
            }
            return found;
        }

        /// <summary>Whether SPIR-V forces depth/stencil tests before fragment discard.</summary>
        public static bool HasSpirvEarlyFragmentTests(byte[] bytes)
        {
            VerifySpirvHeader(bytes);
            bool found = false;
            for (int at = 20; at < bytes.Length;)
            {
                uint instruction = BitConverter.ToUInt32(bytes, at);
                int length = (int)(instruction >> 16) * 4;
                if (length < 4 || at + length > bytes.Length) throw new InvalidDataException("invalid SPIR-V instruction");
                // OpExecutionMode's first operand is the entry point, its second is the execution mode (9).
                if ((instruction & 65535) == 16)
                {
                    if (length < 12) throw new InvalidDataException("invalid SPIR-V execution mode");
                    found |= BitConverter.ToUInt32(bytes, at + 8) == 9;
                }
                at += length;
            }
            return found;
        }

        private static void VerifySpirvHeader(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 20 || bytes.Length % 4 != 0 || BitConverter.ToUInt32(bytes, 0) != 0x07230203)
                throw new InvalidDataException("not SPIR-V");
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

            if (s_vulkan) FilterVulkan(stage);
            return stage;
        }

        // Vulkan compilation reports the whole pass's reflection for both stages. Keep only descriptors actually
        // present in this stage's SPIR-V. The low 24 bits of Unity's index encode set and binding.
        private static void FilterVulkan(VpNativeShaderBinary.Stage stage)
        {
            byte[] bytes = stage.Bytecode;
            if (bytes.Length < 20 || bytes.Length % 4 != 0 || BitConverter.ToUInt32(bytes, 0) != 0x07230203)
                throw new InvalidDataException("Unity did not return SPIR-V");
            var sets = new Dictionary<uint, uint>();
            var bindings = new Dictionary<uint, uint>();
            for (int at = 20; at < bytes.Length;)
            {
                uint instruction = BitConverter.ToUInt32(bytes, at);
                int length = (int)(instruction >> 16) * 4;
                if (length < 4 || at + length > bytes.Length) throw new InvalidDataException("invalid SPIR-V instruction");
                if ((instruction & 65535) == 71 && length >= 16)
                {
                    uint id = BitConverter.ToUInt32(bytes, at + 4), decoration = BitConverter.ToUInt32(bytes, at + 8), value = BitConverter.ToUInt32(bytes, at + 12);
                    if (decoration == 33) bindings[id] = value;
                    if (decoration == 34) sets[id] = value;
                }
                at += length;
            }
            var active = new HashSet<int>();
            foreach (var binding in bindings)
                if (sets.TryGetValue(binding.Key, out uint set)) active.Add((int)((set << 16) | binding.Value));
            bool Missing(int register) => !active.Contains(register & 0xffffff);
            stage.ConstantBuffers.RemoveAll(b => Missing(b.Register));
            stage.Buffers.RemoveAll(b => Missing(b.Register));
            stage.Textures.RemoveAll(b => Missing(b.Register));
            stage.Samplers.RemoveAll(b => Missing(b.Register));
            foreach (var texture in stage.Textures)
                if (texture.SamplerRegister >= 0 && Missing(texture.SamplerRegister)) texture.SamplerRegister = -1;
        }

        private static string GuidOf(string name)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes((s_vulkan ? "Zantetsu.VpNativeVulkanShaderBinary:" : "Zantetsu.VpNativeShaderBinary:") + name));
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
