using System;
using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The sky fog of the ordinary Unity meshes (DESIGN 5.3.1): a mesh drawn with "Zantetsu/VP Mesh Surface" goes
    /// linearly to the sky's colour between seven tenths of the camera's far plane and the far plane itself
    /// (VpSkyFog.hlsl), so that a far building meets the skybox behind it instead of ending at the clip.
    /// <para>
    /// **What is set, and when.** One cubemap -- the product's skybox alone, baked beforehand (the Editor's
    /// VpSkyFogBake; a face of <see cref="FaceSize"/>, linear HDR, no mip chain) -- and one switch, as global shader
    /// constants, when a play session begins. Nothing is written per frame, per camera, per object or per material:
    /// the far plane the fog goes by is the pipeline's own per-camera constant. No material is made and nothing is
    /// allocated while playing. The skybox is taken as fixed: a skybox, sun direction or exposure that changes needs
    /// the cubemap baked again; nothing re-bakes it while running.
    /// </para>
    /// <para>
    /// **Only those meshes.** The cut display's bodies and caps, by any route, do not read these constants: a body is
    /// fogged before it is cut and not after. That difference is this unit's stated limit.
    /// </para>
    /// <para>
    /// A build without the cubemap, and a Player started with <see cref="OffArgument"/>, draw as before the fog: the
    /// switch is zero and the shader samples nothing. The first is told once as an error; it is not taken for fog.
    /// </para>
    /// </summary>
    public static class VpSkyFog
    {
        /// <summary>The cubemap's name under a Resources folder.</summary>
        public const string Resource = "VpSkyFog/VpSkyFog";

        /// <summary>The cubemap's face, in pixels, as baked.</summary>
        public const int FaceSize = 128;

        /// <summary>The share of the far plane at which the fog begins (VP_SKY_FOG_START of VpSkyFog.hlsl).</summary>
        public const float StartShare = 0.7f;

        /// <summary>The Player argument that leaves the fog off, for comparing with and without it in one build.</summary>
        public const string OffArgument = "-zantetsuNoSkyFog";

        private static readonly int CubeId = Shader.PropertyToID("_VpSkyFogCube");
        private static readonly int ParamsId = Shader.PropertyToID("_VpSkyFogParams");

        /// <summary>The cubemap the fog reads now; null when the fog is off.</summary>
        public static Cubemap Sky { get; private set; }

        /// <summary>Whether the fog is on.</summary>
        public static bool Enabled => Sky != null;

        /// <summary>Why the fog is off, when it is; null when it is on.</summary>
        public static string OffReason { get; private set; } = "no play session has begun";

        /// <summary>How many times the globals were written in this domain. Observation: it does not grow while playing.</summary>
        public static int Writes { get; private set; }

        /// <summary>Whether <paramref name="arguments"/> leave the fog on: every case but <see cref="OffArgument"/> given.</summary>
        public static bool ReadEnabled(string[] arguments)
        {
            return arguments == null || Array.IndexOf(arguments, OffArgument) < 0;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        internal static void BeginPlaySession()
        {
            if (!ReadEnabled(Environment.GetCommandLineArgs()))
            {
                Set(null, "asked for by " + OffArgument);
                Debug.Log("VP SKY FOG: off -- " + OffReason);
                return;
            }

            Cubemap sky = Resources.Load<Cubemap>(Resource);
            if (sky == null)
            {
                Set(null, "the baked cubemap '" + Resource + "' is not in the build");
                Debug.LogError("VP SKY FOG: OFF -- " + OffReason + ": far meshes end at the far plane with no fog. Bake it (VpSkyFogBake) and build again.");
                return;
            }

            Set(sky, null);
            Debug.Log(
                "VP SKY FOG: on -- " + sky.name + ", face " + sky.width + ", " + sky.format + ", mips " + sky.mipmapCount
                + "; from " + StartShare.ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture)
                + " of each camera's far plane to the far plane; the Unity meshes of \"Zantetsu/VP Mesh Surface\" only");
        }

        /// <summary>
        /// Sets the cubemap the fog reads, or with null turns the fog off. For the start of a play session and for
        /// tests; not to be called while playing for a skybox that changes -- there is no such mechanism.
        /// </summary>
        public static void Set(Cubemap sky, string offReason = null)
        {
            Sky = sky;
            OffReason = sky != null ? null : offReason ?? "set off";
            if (sky != null)
            {
                Shader.SetGlobalTexture(CubeId, sky);
            }

            Shader.SetGlobalVector(ParamsId, new Vector4(sky != null ? 1f : 0f, 0f, 0f, 0f));
            Writes++;
        }
    }
}
