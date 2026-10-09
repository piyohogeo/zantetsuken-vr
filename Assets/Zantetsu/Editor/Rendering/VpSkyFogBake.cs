using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Zantetsu.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.EditorTools.Rendering
{
    /// <summary>
    /// Bakes the cubemap the sky fog reads (DESIGN 5.3.1, <see cref="VpSkyFog"/>): the open scene's skybox ALONE, as
    /// a camera of that scene shows it behind everything -- the skybox material as it is (its rotation and exposure
    /// are its own properties), lit by the scene's sun as the engine hands it to the skybox -- and nothing else: no
    /// building, no character, no post-processing, no tone mapping.
    /// <para>
    /// **How.** A camera made for the bake, two kilometres above everything the scene draws, with a far plane one
    /// kilometre away: every light of the scene reaches it and no geometry does -- which the bake proves, by drawing
    /// the same six faces once more over a plain colour and finding nothing but that colour. (Not farther up and not
    /// with a nearer far plane: the skybox is drawn as a mesh around the camera, the size of its far plane, and a
    /// small mesh very far from the origin falls on the steps of single precision -- the sky comes out bent, which
    /// shows at the horizon.) It clears to the skybox, renders in HDR with no anti-aliasing and
    /// no post-processing, into the six faces of a half-float cube render texture of <see cref="VpSkyFog.FaceSize"/>
    /// pixels, which are read back face by face. The cubemap is kept as those linear half-float values with no mip
    /// chain: what the fog samples is what the camera drew, not a convolution of it and not an encoding of it. (The
    /// camera's own way into a Cubemap asset is not used: it writes display-range values -- nothing above 1 -- and a
    /// sky brighter than 1 would come out of the fog darker than the background it is to meet.)
    /// </para>
    /// <para>
    /// **When to bake again.** The skybox is taken as fixed. Change the scene's skybox material or any of its
    /// properties, the sun (its direction, colour or intensity -- the default skybox draws its sun from it), or the
    /// ambient settings a custom skybox reads, and the fog's colour no longer is the background's until this is run
    /// again: the menu Zantetsu/Rendering/Bake Sky Fog Cubemap with the product's scene open, or in batch mode
    /// <c>-executeMethod Zantetsu.EditorTools.Rendering.VpSkyFogBake.Batch -zantetsuSkyFogScene &lt;scene&gt;
    /// -zantetsuSkyFogRecord &lt;folder&gt;</c>. The record of a bake says what it was baked from.
    /// </para>
    /// </summary>
    public static class VpSkyFogBake
    {
        /// <summary>Where the baked cubemap is kept: the folder <see cref="VpSkyFog.Resource"/> names, under the runtime's Resources.</summary>
        public const string AssetPath = "Assets/Zantetsu/Runtime/Rendering/Resources/VpSkyFog/VpSkyFog.asset";

        [MenuItem("Zantetsu/Rendering/Bake Sky Fog Cubemap")]
        private static void BakeFromMenu()
        {
            if (TryBakeAsset(out string report))
            {
                Debug.Log(report);
            }
            else
            {
                Debug.LogError(report);
            }
        }

        /// <summary>
        /// Batch entry: opens <c>-zantetsuSkyFogScene</c>, bakes, writes the asset, and writes the bake's record into
        /// <c>-zantetsuSkyFogRecord</c> (a folder that must be empty or absent). Exits with 0 only when all of it stood.
        /// </summary>
        public static void Batch()
        {
            int code = 1;
            try
            {
                string scene = Arg("-zantetsuSkyFogScene");
                string folder = Arg("-zantetsuSkyFogRecord");
                if (string.IsNullOrEmpty(scene) || string.IsNullOrEmpty(folder))
                {
                    throw new ArgumentException("-zantetsuSkyFogScene <scene asset path> and -zantetsuSkyFogRecord <folder> are required");
                }

                if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length > 0)
                {
                    throw new IOException("the record folder is not empty: " + folder);
                }

                Directory.CreateDirectory(folder);
                EditorSceneManager.OpenScene(scene, OpenSceneMode.Single);
                bool baked = TryBakeAsset(out string report);
                File.WriteAllText(Path.Combine(folder, "sky-fog-bake.txt"), report);
                Debug.Log(report);
                if (baked)
                {
                    File.Copy(AssetPath, Path.Combine(folder, Path.GetFileName(AssetPath)));
                    File.Copy(AssetPath + ".meta", Path.Combine(folder, Path.GetFileName(AssetPath) + ".meta"));
                    code = 0;
                }
            }
            catch (Exception exception)
            {
                Debug.LogError("VP SKY FOG BAKE: FAILED: " + exception);
            }

            EditorApplication.Exit(code);
        }

        /// <summary>Bakes the open scene's skybox and writes it as the asset the fog reads. False, with the reason, when it could not.</summary>
        public static bool TryBakeAsset(out string report)
        {
            Cubemap baked = Bake(VpSkyFog.FaceSize, out report);
            if (baked == null)
            {
                return false;
            }

            // Only sampled at run time: no copy of its pixels is kept on the CPU.
            baked.name = Path.GetFileNameWithoutExtension(AssetPath);
            baked.Apply(false, true);
            Directory.CreateDirectory(Path.GetDirectoryName(AssetPath));
            Cubemap existing = AssetDatabase.LoadAssetAtPath<Cubemap>(AssetPath);
            if (existing != null)
            {
                // Written into the asset there is, so that its identity (its GUID) stays.
                EditorUtility.CopySerialized(baked, existing);
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(baked);
            }
            else
            {
                AssetDatabase.CreateAsset(baked, AssetPath);
            }

            AssetDatabase.SaveAssets();
            report += "written: " + AssetPath + "\n";
            return true;
        }

        /// <summary>
        /// The open scene's skybox alone as a cubemap of <paramref name="faceSize"/> pixels a face: linear half-float,
        /// no mip chain. Null, with the reason in <paramref name="report"/>, when the scene has no skybox or the
        /// camera could not render the faces. The caller owns the cubemap.
        /// </summary>
        public static Cubemap Bake(int faceSize, out string report)
        {
            var text = new StringBuilder();
            text.AppendLine("VP SKY FOG BAKE " + DateTime.Now.ToString("o") + " Unity " + Application.unityVersion + ", " + SystemInfo.graphicsDeviceType
                + ", colour space " + QualitySettings.activeColorSpace);
            Material skybox = RenderSettings.skybox;
            UnityEngine.SceneManagement.Scene scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            text.AppendLine("scene: " + (string.IsNullOrEmpty(scene.path) ? "(unsaved) " + scene.name : scene.path));
            if (skybox == null)
            {
                text.AppendLine("FAILED: the scene has no skybox material (RenderSettings.skybox)");
                report = text.ToString();
                return null;
            }

            text.AppendLine("skybox: material '" + skybox.name + "', shader '" + (skybox.shader != null ? skybox.shader.name : "none") + "'" + Describe(skybox));
            Light sun = RenderSettings.sun;
            text.AppendLine("sun (RenderSettings.sun): " + (sun != null ? Describe(sun) : "none set: the engine gives the skybox the brightest directional light"));
            foreach (Light light in Object.FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (light.type == LightType.Directional && light.isActiveAndEnabled)
                {
                    text.AppendLine("directional light: " + Describe(light));
                }
            }

            text.AppendLine("ambient: mode " + RenderSettings.ambientMode + ", intensity " + RenderSettings.ambientIntensity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));

            var holder = new GameObject("VP Sky Fog Bake Camera") { hideFlags = HideFlags.HideAndDontSave };
            var faces = new Cubemap(faceSize, TextureFormat.RGBAHalf, false);
            var rendered = new RenderTexture(faceSize, faceSize, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear)
            {
                dimension = UnityEngine.Rendering.TextureDimension.Cube,
                useMipMap = false,
                autoGenerateMips = false,
                antiAliasing = 1,
                hideFlags = HideFlags.HideAndDontSave,
            };
            var readback = new Texture2D(faceSize, faceSize, TextureFormat.RGBAHalf, false, true) { hideFlags = HideFlags.HideAndDontSave };
            RenderTexture active = RenderTexture.active;
            try
            {
                // Two kilometres above everything the scene draws, with a far plane one kilometre off: lights reach
                // the camera, geometry does not (proved below).
                float top = 0f;
                int renderers = 0;
                foreach (Renderer renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
                {
                    top = Mathf.Max(top, renderer.bounds.max.y);
                    renderers++;
                }

                holder.transform.SetPositionAndRotation(new Vector3(0f, top + 2000f, 0f), Quaternion.identity);
                Camera camera = holder.AddComponent<Camera>();
                camera.enabled = false;
                camera.cullingMask = ~0;
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = 1000f;
                camera.allowHDR = true;
                camera.allowMSAA = false;
                camera.useOcclusionCulling = false;

                // The proof first: the same faces over a plain colour, with no skybox. Anything else in them is geometry.
                var plain = new Color(0.25f, 0.5f, 0.75f, 1f);
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = plain;
                if (!rendered.Create() || !camera.RenderToCubemap(rendered, 63))
                {
                    text.AppendLine("FAILED: the camera could not render into the cube render texture's faces");
                    Object.DestroyImmediate(faces);
                    report = text.ToString();
                    return null;
                }

                Color expected = QualitySettings.activeColorSpace == ColorSpace.Linear ? plain.linear : plain;
                int foreign = 0;
                for (int face = 0; face < 6; face++)
                {
                    Graphics.SetRenderTarget(rendered, 0, (CubemapFace)face);
                    readback.ReadPixels(new Rect(0, 0, faceSize, faceSize), 0, 0, false);
                    foreach (Color pixel in readback.GetPixels())
                    {
                        if (Mathf.Abs(pixel.r - expected.r) > 0.01f || Mathf.Abs(pixel.g - expected.g) > 0.01f || Mathf.Abs(pixel.b - expected.b) > 0.01f) foreign++;
                    }
                }

                text.AppendLine("geometry in the bake camera's faces: " + foreign + " pixels of " + (6 * faceSize * faceSize) + " (the scene's " + renderers
                    + " renderers reach up to y " + top.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "; the camera stands at y "
                    + holder.transform.position.y.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + ")");
                if (foreign > 0)
                {
                    text.AppendLine("FAILED: something of the scene is drawn into the faces: the cubemap would not be the skybox alone");
                    Object.DestroyImmediate(faces);
                    report = text.ToString();
                    return null;
                }

                camera.clearFlags = CameraClearFlags.Skybox;
                if (!camera.RenderToCubemap(rendered, 63))
                {
                    text.AppendLine("FAILED: the camera could not render the skybox into the cube render texture's faces");
                    Object.DestroyImmediate(faces);
                    report = text.ToString();
                    return null;
                }

                // Face by face, as rendered: the values are copied, not converted, and the rows are kept in the order
                // they are read back in (VpSkyFogTests holds the result against the skybox in views all round, up and
                // down: rows turned over put the ground's colour where the sky is).
                for (int face = 0; face < 6; face++)
                {
                    Graphics.SetRenderTarget(rendered, 0, (CubemapFace)face);
                    readback.ReadPixels(new Rect(0, 0, faceSize, faceSize), 0, 0, false);
                    faces.SetPixels(readback.GetPixels(), (CubemapFace)face);
                }

                faces.wrapMode = TextureWrapMode.Clamp;
                faces.filterMode = FilterMode.Bilinear;
                faces.Apply(false, false);
                text.AppendLine("camera: clear to the skybox, HDR, no MSAA, no post-processing (a camera with no additional data), near "
                    + camera.nearClipPlane + " far " + camera.farClipPlane + " at " + holder.transform.position + ": no geometry is within it");
                text.AppendLine("cubemap: face " + faces.width + ", " + faces.format + ", mips " + faces.mipmapCount + ", linear half-float values as rendered (not convolved, not encoded)");
                float largest = 0f;
                for (int face = 0; face < 6; face++)
                {
                    Color[] pixels = faces.GetPixels((CubemapFace)face);
                    double r = 0, g = 0, b = 0;
                    float most = 0f;
                    foreach (Color pixel in pixels)
                    {
                        r += pixel.r;
                        g += pixel.g;
                        b += pixel.b;
                        most = Mathf.Max(most, pixel.maxColorComponent);
                    }

                    largest = Mathf.Max(largest, most);
                    text.AppendLine("  face " + (CubemapFace)face + ": mean linear rgb ("
                        + (r / pixels.Length).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + ", "
                        + (g / pixels.Length).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + ", "
                        + (b / pixels.Length).ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) + "), largest component "
                        + most.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture));
                }

                text.AppendLine("largest component of all: " + largest.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture)
                    + (largest > 1f ? " (above 1: the values are not clamped to the display's range)" : " (nothing above 1)"));
                report = text.ToString();
                return faces;
            }
            finally
            {
                RenderTexture.active = active;
                rendered.Release();
                Object.DestroyImmediate(rendered);
                Object.DestroyImmediate(readback);
                Object.DestroyImmediate(holder);
            }
        }

        private static string Describe(Light light)
        {
            Vector3 forward = light.transform.forward;
            return "'" + light.name + "' euler " + light.transform.eulerAngles.ToString("F2") + ", shines along " + forward.ToString("F4")
                   + ", colour " + light.color + ", intensity " + light.intensity.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        }

        // The skybox material's own numbers and colours, as far as the shader names them: its exposure, rotation, tint
        // and the like, whatever the skybox shader is.
        private static string Describe(Material skybox)
        {
            var text = new StringBuilder();
            Shader shader = skybox.shader;
            for (int i = 0; shader != null && i < shader.GetPropertyCount(); i++)
            {
                string name = shader.GetPropertyName(i);
                switch (shader.GetPropertyType(i))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        text.Append("; ").Append(name).Append(' ').Append(skybox.GetFloat(name).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        text.Append("; ").Append(name).Append(' ').Append(skybox.GetColor(name));
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        Texture texture = skybox.GetTexture(name);
                        text.Append("; ").Append(name).Append(' ').Append(texture != null ? "'" + texture.name + "'" : "none");
                        break;
                }
            }

            return text.ToString();
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
