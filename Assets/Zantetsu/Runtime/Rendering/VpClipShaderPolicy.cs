using System;
using System.Collections.Generic;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// One clip mode for Unity draws, stencil volumes and native Vulkan pipelines. A Vulkan device whose enabled
    /// features could not be observed uses the safe pixel path. Native drawing and command compaction are independent.
    /// </summary>
    public static class VpClipShaderPolicy
    {
        public const string PixelClipKeyword = "VP_PIXEL_CLIP";
        public const string ForcePixelClipArgument = "-zantetsuVpPixelClip";
        private const string PixelClipSuffix = "-pixelclip";
        private static readonly Dictionary<string, string> PixelVariants = new Dictionary<string, string>(StringComparer.Ordinal);
        private static bool s_configured;
        private static bool s_pixelClip;
#if UNITY_EDITOR
        private static bool? s_override;

        /// <summary>Image tests can exercise the fallback on a graphics device that has hardware clip distances.</summary>
        public static bool? PixelClipOverrideForTests
        {
            get => s_override;
            set { s_override = value; s_configured = false; EnsureConfigured(); }
        }

        public static bool SelectPixelClipForTests(bool vulkan, bool observed, bool enabled, bool forced)
            => SelectPixelClip(vulkan, observed, enabled, forced);
#endif

        public static bool PixelClip
        {
            get { EnsureConfigured(); return s_pixelClip; }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void Reset()
        {
            s_configured = false;
            PixelVariants.Clear();
#if UNITY_EDITOR
            s_override = null;
#endif
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void EnsureConfigured()
        {
            if (s_configured) return;
            bool vulkan = VpNativeDrawPlugin.Vulkan;
            bool supported = false, enabled = false;
            bool observed = vulkan && VpNativeDrawPlugin.TryReadClipDistanceInfo(out supported, out enabled);
            bool forced = Array.Exists(Environment.GetCommandLineArgs(),
                arg => string.Equals(arg, ForcePixelClipArgument, StringComparison.OrdinalIgnoreCase));
            s_pixelClip = SelectPixelClip(vulkan, observed, enabled, forced);
#if UNITY_EDITOR
            if (s_override.HasValue) s_pixelClip = s_override.Value;
#endif
            if (s_pixelClip) Shader.EnableKeyword(PixelClipKeyword);
            else Shader.DisableKeyword(PixelClipKeyword);
            s_configured = true;
            if (Application.isPlaying)
                Debug.Log("VP CLIP MODE: " + (s_pixelClip ? "Pixel" : "Hardware")
                    + "; graphics=" + SystemInfo.graphicsDeviceType + "; observed=" + observed
                    + "; supported=" + supported + "; enabled=" + enabled + "; forced=" + forced);
        }

        private static bool SelectPixelClip(bool vulkan, bool observed, bool enabled, bool forced)
            => vulkan && (forced || !observed || !enabled);

        /// <summary>
        /// Selects the matching baked binary before pipeline lookup/prewarm. Ordinary draws keep their existing
        /// NO_CLIP binary; clip names are cached so repeated draws do not construct strings each frame.
        /// </summary>
        public static string NativeVariant(string variant, bool vulkan)
        {
            if (!vulkan || !PixelClip || variant.EndsWith("-noclip", StringComparison.Ordinal)
                || variant.EndsWith(PixelClipSuffix, StringComparison.Ordinal)) return variant;
            if (!PixelVariants.TryGetValue(variant, out string pixel))
            {
                pixel = variant + PixelClipSuffix;
                PixelVariants.Add(variant, pixel);
            }
            return pixel;
        }
    }
}
