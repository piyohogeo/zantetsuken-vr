using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Rendering;
using UnityEngine;

namespace Zantetsu.EditorTools.AndroidFlat
{
    /// <summary>Opt-in build diagnostic. Observes surviving VP variants without adding or removing any.</summary>
    public sealed class AndroidFlatShaderVariantAudit : IPreprocessShaders
    {
        private const int MaximumVariants = 1000;
        private static readonly object Gate = new object();
        private static bool s_enabled;
        private static bool s_ioFailed;
        private static bool s_limitReported;
        private static int s_recorded;
        private static long s_observed;
        private static int s_callbacks;
        private static string s_path;

        // AndroidFlatBuild.BuildSettingsScope enables and restores this property; ordinary builds stay untouched.
        public static bool Enabled
        {
            get { lock (Gate) return s_enabled; }
            set
            {
                lock (Gate)
                {
                    if (s_enabled == value) return;
                    s_enabled = value;
                    if (value)
                    {
                        s_recorded = 0;
                        s_observed = 0;
                        s_callbacks = 0;
                        s_ioFailed = false;
                        s_limitReported = false;
                        s_path = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Logs", "android-flat", "shader-variants.txt");
                        Write("VP shader variant audit; utc=" + DateTime.UtcNow.ToString("O") + "; unity=" + Application.unityVersion
                            + "; callbackOrder=" + int.MaxValue + "; maximumVariantRows=" + MaximumVariants + "\n"
                            + "Read-only IPreprocessShaders snapshot after lower-order callbacks. Cached shaders may not invoke this callback.\n", true);
                    }
                    else
                    {
                        Write("END; callbacks=" + s_callbacks + "; observedVariants=" + s_observed + "; recordedVariants=" + s_recorded
                            + "; omittedVariants=" + (s_observed - s_recorded) + "\n");
                    }
                }
            }
        }

        public int callbackOrder => int.MaxValue;

        public void OnProcessShader(Shader shader, ShaderSnippetData snippet, IList<ShaderCompilerData> data)
        {
            lock (Gate)
            {
                if (!s_enabled || shader == null || !IsTarget(shader.name)) return;
                s_callbacks++;
                s_observed += data.Count;
                var text = new StringBuilder();
                string context = "shader=" + shader.name + "; asset=" + AssetDatabase.GetAssetPath(shader)
                    + "; pass=" + snippet.passName + "; passType=" + snippet.passType + "; stage=" + snippet.shaderType;
                // Keep even zero-count snippets: they distinguish complete stripping from a missing callback.
                text.Append("SNIPPET ").Append(context).Append("; survivingVariants=").Append(data.Count).Append('\n');
                for (int i = 0; i < data.Count && s_recorded < MaximumVariants; i++)
                {
                    ShaderCompilerData variant = data[i];
                    var keywords = variant.shaderKeywordSet.GetShaderKeywords();
                    var names = new string[keywords.Length];
                    for (int k = 0; k < names.Length; k++) names[k] = keywords[k].name;
                    Array.Sort(names, StringComparer.Ordinal);
                    text.Append("VARIANT ").Append(++s_recorded).Append("; ").Append(context)
                        .Append("; tier=").Append(variant.graphicsTier)
                        .Append("; compilerPlatform=").Append(variant.shaderCompilerPlatform)
                        .Append("; target=").Append(variant.buildTarget)
                        .Append("; requirements=").Append(variant.shaderRequirements)
                        .Append("; requirementsHex=0x").Append(Convert.ToUInt64(variant.shaderRequirements).ToString("X"))
                        .Append("; keywords=").Append(names.Length == 0 ? "<none>" : string.Join(" ", names)).Append('\n');
                }
                if (s_observed > s_recorded && !s_limitReported)
                {
                    text.Append("LIMIT: variant rows capped at ").Append(MaximumVariants)
                        .Append("; subsequent snippet counts are still recorded.\n");
                    s_limitReported = true;
                }
                Write(text.ToString());
            }
        }

        private static bool IsTarget(string name)
        {
            return name == "Zantetsu/VP Indexed Indirect Unlit"
                || name == "Zantetsu/VP Indexed Indirect Shadow Caster"
                || name == "Zantetsu/VP Stencil Volume"
                || name == "Zantetsu/VP Stencil Cap"
                || name.StartsWith("Hidden/Zantetsu/Android Product Pixel", StringComparison.Ordinal)
                || name == "Hidden/Zantetsu/Android Draw Probe"
                || name == "Zantetsu/VP Mesh Surface";
        }

        private static void Write(string text, bool reset = false)
        {
            if (s_ioFailed) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s_path));
                if (reset) File.WriteAllText(s_path, text, new UTF8Encoding(false));
                else File.AppendAllText(s_path, text, new UTF8Encoding(false));
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                s_ioFailed = true;
                Debug.LogWarning("VP shader variant audit could not write " + s_path + ": " + error.Message);
            }
        }
    }
}
