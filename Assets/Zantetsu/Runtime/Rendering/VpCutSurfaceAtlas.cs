using System;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>One caller-owned palette pair for explicitly opted-in shared materials. No texture/material clones
    /// or geometry writes. Surface sampling stays on normalAtlas (including its mips); only the cap binding switches.</summary>
    public static class VpCutSurfaceAtlas
    {
        private static readonly int EnabledId=Shader.PropertyToID("_VpPaletteAtlasEnabled");
        private static readonly int SurfaceId=Shader.PropertyToID("_VpPaletteSurface");
        private static readonly int CapsId=Shader.PropertyToID("_VpPaletteCaps");
        private static Texture2D s_normal, s_debug;
        public static Texture2D Normal => s_normal;
        public static Texture2D Debug => s_debug;
        public static bool IsBound => s_normal!=null && s_debug!=null;

        public static void Bind(Texture2D normalAtlas, Texture2D debugAtlas)
        {
            if(normalAtlas==null || debugAtlas==null || normalAtlas.width!=256 || normalAtlas.height!=256
                || debugAtlas.width!=256 || debugAtlas.height!=256) throw new ArgumentException("A prepared 256x256 normal/debug atlas pair is required.");
            VpCutSurfaceColour.EnsureInitialized();
            s_normal=normalAtlas;s_debug=debugAtlas;
            Shader.SetGlobalTexture(SurfaceId,s_normal);
            SelectDebug(VpCutSurfaceColour.DebugEnabled);
            Shader.SetGlobalFloat(EnabledId,1);
        }

        public static void Clear()
        {
            Shader.SetGlobalFloat(EnabledId,0);
            Shader.SetGlobalTexture(SurfaceId,null);Shader.SetGlobalTexture(CapsId,null);
            s_normal=null;s_debug=null; // Ownership stays with the caller.
        }

        internal static void SelectDebug(bool enabled)
        {
            if(IsBound) Shader.SetGlobalTexture(CapsId,enabled ? s_debug : s_normal);
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void BeginPlaySession() => Clear();
#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void BeginEditorDomain() => Clear();
#endif
    }
}
