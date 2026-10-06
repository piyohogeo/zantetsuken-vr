using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// The one material setting every VP surface is lit with (DESIGN 5.3): the colour multiplier, Metallic and
    /// Smoothness that the shared shading (VpCutSurfaceShading.hlsl) hands to URP's lighting, for an ordinary mesh
    /// or skinned mesh drawn with "Zantetsu/VP Mesh Surface", the body of the cut display by either route, a real cap
    /// committed into the geometry, and a temporary cap the stencil draws.
    /// <para>
    /// **Where the values come from.** They are the imported values of the city's own <c>Color</c> material (the
    /// tiled-seed14 models, read from the imported assets on 2026-10-06, not guessed from its name): shader
    /// <c>Universal Render Pipeline/Lit</c>, metallic workflow, opaque, Base Color (0.8, 0.8, 0.8) with the palette
    /// texture as its Base Map, Metallic 0, Smoothness 0.5, no normal, occlusion or emission map, specular highlights
    /// and environment reflections on, receiving shadows. The models' <c>Glass</c> and <c>Emissive</c> materials hold
    /// the same three values and are drawn as this one opaque material.
    /// </para>
    /// <para>
    /// **One place, written at a boundary.** The values are global shader constants, written when a play session or an
    /// editor domain begins and never per frame, per object or per material: no material is made for them and nothing
    /// is allocated. There is no way to change them while running; a different look is a different value here. The
    /// shaders have no defaults of their own, so a domain that never ran <see cref="EnsureInitialized"/> would light
    /// every VP surface black -- which is why it is written at both starts and asked for again where the cut surface
    /// colours are.
    /// </para>
    /// </summary>
    public static class VpSurfaceMaterial
    {
        /// <summary>The colour every base colour is multiplied by before it is lit, as authored (sRGB): the Color material's Base Color.</summary>
        public static readonly Color ColourScale = new Color(0.8f, 0.8f, 0.8f, 1f);

        /// <summary>The Color material's Metallic.</summary>
        public const float Metallic = 0f;

        /// <summary>The Color material's Smoothness.</summary>
        public const float Smoothness = 0.5f;

        private static readonly int ColourScaleId = Shader.PropertyToID("_VpSurfaceColorScale");
        private static readonly int MaterialId = Shader.PropertyToID("_VpSurfaceMaterial");

        private static bool s_initialized;

        /// <summary>How many times the globals were written in this domain. Observation: it does not grow while playing.</summary>
        public static int Writes { get; private set; }

        /// <summary>Writes the globals unless this domain has written them already.</summary>
        public static void EnsureInitialized()
        {
            if (s_initialized)
            {
                return;
            }

            Write();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        internal static void BeginPlaySession()
        {
            Write();
        }

#if UNITY_EDITOR
        // A freshly loaded editor domain has no globals set at all, so editor drawing and EditMode tests would
        // otherwise start from black.
        [UnityEditor.InitializeOnLoadMethod]
        private static void InitializeInEditorDomain()
        {
            EnsureInitialized();
        }
#endif

        private static void Write()
        {
            // The multiplier is a colour as a material holds it: the shader multiplies in the active colour space, as
            // URP/Lit does with its Base Color. Written as a vector, converted here, so that nothing converts it twice.
            Color scale = QualitySettings.activeColorSpace == ColorSpace.Linear ? ColourScale.linear : ColourScale;
            Shader.SetGlobalVector(ColourScaleId, new Vector4(scale.r, scale.g, scale.b, 1f));
            Shader.SetGlobalVector(MaterialId, new Vector4(Metallic, Smoothness, 0f, 0f));
            s_initialized = true;
            Writes++;
        }
    }
}
