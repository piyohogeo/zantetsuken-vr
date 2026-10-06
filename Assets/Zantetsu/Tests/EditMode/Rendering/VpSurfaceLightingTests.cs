using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// The shared VP lighting (VpCutSurfaceShading.hlsl, DESIGN 5.3) against what it is meant to equal: URP/Lit with the
    /// values of the city's imported <c>Color</c> material -- Base Color (0.8, 0.8, 0.8), Metallic 0, Smoothness 0.5, the
    /// same base map, opaque, no other map. The same shape is drawn from the same view in the same light, once with
    /// "Zantetsu/VP Mesh Surface" and once with URP/Lit, as an ordinary mesh and as a skinned one, and the two images
    /// are compared pixel by pixel.
    /// <para>
    /// **The light is the test's own** (<see cref="VpTestLighting"/>): a directional light with soft shadows, an ambient
    /// light of three different colours and a reflection cubemap whose faces differ, so that each input of the lighting
    /// -- the main light and its shadow, the ambient light by direction, the highlight, the environment reflection --
    /// shows where it is used and would show where it were missing.
    /// </para>
    /// <para>
    /// **Full shadow** is a large caster between the light and the shape. What is left there is the ambient light and
    /// the reflection: it is compared with URP/Lit, with the same view drawn with the light off, and with itself -- the
    /// top of the shape against its underside, which the three ambient colours tell apart.
    /// </para>
    /// </summary>
    public class VpSurfaceLightingTests
    {
        private const string ProductShader = "Zantetsu/VP Mesh Surface";
        private const string LitShader = "Universal Render Pipeline/Lit";
        private const int Size = 128;

        private readonly List<Object> _objects = new List<Object>();
        private VpTestLighting _lighting;
        private Camera _camera;
        private Light _light;
        private RenderTexture _target;
        private GameObject _occluder;

        [SetUp]
        public void BuildTheStage()
        {
            var reflection = Track(new Cubemap(16, TextureFormat.RGBA32, true));
            var faces = new (CubemapFace face, Color colour)[]
            {
                (CubemapFace.PositiveY, new Color(0.55f, 0.75f, 1f)), (CubemapFace.NegativeY, new Color(0.35f, 0.25f, 0.1f)),
                (CubemapFace.PositiveX, new Color(1f, 0.3f, 0.2f)), (CubemapFace.NegativeX, new Color(0.2f, 0.9f, 0.3f)),
                (CubemapFace.PositiveZ, new Color(0.9f, 0.9f, 0.2f)), (CubemapFace.NegativeZ, new Color(0.8f, 0.3f, 0.9f)),
            };
            foreach ((CubemapFace face, Color colour) in faces)
            {
                var pixels = new Color[16 * 16];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = colour;
                reflection.SetPixels(pixels, face);
            }

            reflection.Apply(true);
            _lighting = VpTestLighting.Of(
                new Color(0.35f, 0.55f, 1f), new Color(0.45f, 0.45f, 0.45f), new Color(0.6f, 0.38f, 0.15f), reflection);

            _light = Track(new GameObject("Lighting Test Sun")).AddComponent<Light>();
            _light.type = LightType.Directional;
            _light.shadows = LightShadows.Soft;
            _light.intensity = 1f;
            _light.color = new Color(1f, 0.96f, 0.9f);
            _light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            _target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 });
            _target.Create();
            _camera = Track(new GameObject("Lighting Test Camera")).AddComponent<Camera>();
            _camera.enabled = false;
            _camera.fieldOfView = 40f;
            _camera.clearFlags = CameraClearFlags.SolidColor;
            _camera.backgroundColor = Color.black;
            _camera.nearClipPlane = 0.1f;
            _camera.farClipPlane = 30f;
            _camera.targetTexture = _target;
            _camera.transform.position = new Vector3(0f, 0.6f, -4f);
            _camera.transform.LookAt(Vector3.zero);

            // The caster of the full shadow: between the light and the shape, behind the camera's view, never drawn.
            Material lit = LitWithTheColorMaterialsValues(null);
            _occluder = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
            Object.DestroyImmediate(_occluder.GetComponent<Collider>());
            _occluder.transform.position = -_light.transform.forward * 5f;
            _occluder.transform.rotation = Quaternion.FromToRotation(Vector3.up, -_light.transform.forward);
            _occluder.transform.localScale = new Vector3(9f, 0.05f, 9f);
            MeshRenderer occluderRenderer = _occluder.GetComponent<MeshRenderer>();
            occluderRenderer.sharedMaterial = lit;
            occluderRenderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            _occluder.SetActive(false);
        }

        [TearDown]
        public void TakeTheStageDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
            {
                if (_objects[i] != null)
                {
                    Object.DestroyImmediate(_objects[i] is Component c ? c.gameObject : _objects[i]);
                }
            }

            _objects.Clear();
            _lighting?.Dispose();
            _lighting = null;
        }

        /// <summary>
        /// The common setting is the Color material's, and what the shaders read is that setting: the multiplier as a
        /// colour in the active colour space, Metallic and Smoothness as they are. Drawing writes none of it again.
        /// </summary>
        [Test]
        public void TheCommonSetting_IsTheColorMaterials_AndDrawingWritesNothing()
        {
            Assert.That(VpSurfaceMaterial.ColourScale, Is.EqualTo(new Color(0.8f, 0.8f, 0.8f, 1f)));
            Assert.That(VpSurfaceMaterial.Metallic, Is.EqualTo(0f));
            Assert.That(VpSurfaceMaterial.Smoothness, Is.EqualTo(0.5f));

            VpSurfaceMaterial.EnsureInitialized();
            int writes = VpSurfaceMaterial.Writes;
            Assert.That(writes, Is.GreaterThan(0), "written when the domain began");
            Vector4 scale = Shader.GetGlobalVector("_VpSurfaceColorScale");
            float expected = QualitySettings.activeColorSpace == ColorSpace.Linear ? Mathf.GammaToLinearSpace(0.8f) : 0.8f;
            Assert.That(scale.x, Is.EqualTo(expected).Within(1e-5f), "the multiplier, in the active colour space");
            Assert.That(scale.y, Is.EqualTo(expected).Within(1e-5f));
            Assert.That(scale.z, Is.EqualTo(expected).Within(1e-5f));
            Vector4 material = Shader.GetGlobalVector("_VpSurfaceMaterial");
            Assert.That(material.x, Is.EqualTo(0f), "Metallic");
            Assert.That(material.y, Is.EqualTo(0.5f), "Smoothness");

            // The first drawing lets the pipeline make whatever it makes once; the count is taken after it.
            Material product = Product(null);
            Draw(Sphere(), product, false);
            int materials = Resources.FindObjectsOfTypeAll<Material>().Length;
            Draw(Sphere(), product, false);
            Draw(Sphere(), product, false);
            VpSurfaceMaterial.EnsureInitialized();
            Assert.That(VpSurfaceMaterial.Writes, Is.EqualTo(writes), "drawing and asking again write nothing");
            Assert.That(Resources.FindObjectsOfTypeAll<Material>().Length, Is.EqualTo(materials), "and no material is made for the setting");
        }

        /// <summary>
        /// A mesh in the light: faces of every orientation the view has, the highlight among them. With a plain base
        /// and with a base map whose colour varies across the shape.
        /// </summary>
        [Test]
        public void AMeshInTheLight_IsDrawnAsUrpLitDrawsIt([Values(false, true)] bool baseMap)
        {
            Texture2D map = baseMap ? Gradient() : null;
            Color32[] vp = Draw(Sphere(), Product(map), false);
            Color32[] lit = Draw(Sphere(), LitWithTheColorMaterialsValues(map), false);
            AssertSame(vp, lit, "a mesh in the light" + (baseMap ? ", with a base map" : string.Empty));

            // The view holds lit faces of different orientations: what the main light adds -- this image against the same
            // view with the light off -- is large on the side towards the light and small on the side away from it.
            _light.enabled = false;
            Color32[] noLight = Draw(Sphere(), Product(map), false);
            _light.enabled = true;
            var towards = new Vector3(-0.45f, 0.55f, -0.7f);
            var away = new Vector3(0.75f, -0.5f, -0.4f);
            int towardsGain = Grey(At(vp, towards)) - Grey(At(noLight, towards));
            int awayGain = Grey(At(vp, away)) - Grey(At(noLight, away));
            TestContext.Out.WriteLine(
                "towards the light " + At(vp, towards) + " (light off " + At(noLight, towards) + "), away from it " + At(vp, away)
                + " (light off " + At(noLight, away) + ")");
            Assert.That(towardsGain, Is.GreaterThan(25), "the main light lights the side towards it");
            Assert.That(awayGain, Is.LessThan(towardsGain / 2), "and the side away from it far less");
        }

        /// <summary>
        /// The same mesh fully in shadow. It equals URP/Lit; it equals the view with the light off, so nothing of the
        /// main light is left and what is drawn is the ambient light and the reflection; and that is neither black nor
        /// one value: the top takes the sky's colour and the underside the ground's.
        /// </summary>
        [Test]
        public void AMeshFullyInShadow_IsDrawnAsUrpLitDrawsIt_AndItsAmbientFollowsWhereItFaces()
        {
            Color32[] lighted = Draw(Sphere(), Product(null), false);
            _occluder.SetActive(true);
            Color32[] vp = Draw(Sphere(), Product(null), false);
            Color32[] lit = Draw(Sphere(), LitWithTheColorMaterialsValues(null), false);
            AssertSame(vp, lit, "a mesh fully in shadow");

            _occluder.SetActive(false);
            _light.enabled = false;
            Color32[] noLight = Draw(Sphere(), Product(null), false);
            _light.enabled = true;
            AssertSame(vp, noLight, "fully in shadow against the light off");

            Color32 top = At(vp, new Vector3(0f, 0.8f, -0.6f));
            Color32 underside = At(vp, new Vector3(0f, -0.8f, -0.6f));
            Color32 topLighted = At(lighted, new Vector3(0f, 0.8f, -0.6f));
            Color top4 = _lighting.AmbientTowards(Vector3.up);
            Color under4 = _lighting.AmbientTowards(Vector3.down);
            TestContext.Out.WriteLine(
                "in full shadow: top " + top + ", underside " + underside + "; the same top in the light " + topLighted
                + "; the ambient probe (linear) up " + top4 + ", down " + under4);
            Assert.That(Grey(topLighted) - Grey(top), Is.GreaterThan(30), "the shadow takes the main light away");
            Assert.That(Grey(top), Is.GreaterThan(40), "what is left is not black");
            Assert.That(Grey(underside), Is.GreaterThan(25), "on the underside either");
            Assert.That(top.b - top.r, Is.GreaterThan(25), "the top takes the sky's colour: " + top);
            Assert.That(underside.r - underside.b, Is.GreaterThan(25), "and the underside the ground's: " + underside);
        }

        /// <summary>
        /// The highlight and the environment reflection are in the VP image: URP/Lit with its specular highlights off
        /// differs from it where the highlight is, and URP/Lit with its environment reflections off differs from it
        /// across the shape, while URP/Lit with both on is what it equals (the cases above).
        /// </summary>
        [Test]
        public void TheHighlightAndTheEnvironmentReflection_AreThere()
        {
            Color32[] vp = Draw(Sphere(), Product(null), false);
            Material noHighlight = LitWithTheColorMaterialsValues(null);
            noHighlight.SetFloat("_SpecularHighlights", 0f);
            noHighlight.EnableKeyword("_SPECULARHIGHLIGHTS_OFF");
            Material noReflection = LitWithTheColorMaterialsValues(null);
            noReflection.SetFloat("_EnvironmentReflections", 0f);
            noReflection.EnableKeyword("_ENVIRONMENTREFLECTIONS_OFF");

            Difference highlight = Compare(vp, Draw(Sphere(), noHighlight, false));
            Difference reflection = Compare(vp, Draw(Sphere(), noReflection, false));
            TestContext.Out.WriteLine("against URP/Lit without specular highlights: " + highlight + "; without environment reflections: " + reflection);
            Assert.That(highlight.worst, Is.GreaterThan(12), "the main light's highlight is in the VP image");
            Assert.That(highlight.over, Is.LessThan(highlight.drawn / 2), "and it is a highlight, not the whole shape");
            Assert.That(reflection.worst, Is.GreaterThan(6), "the environment reflection is in the VP image");
        }

        /// <summary>
        /// A skinned mesh, posed by its bone, in the light and fully in shadow: the VP surface on a SkinnedMeshRenderer
        /// against URP/Lit on the same renderer. The pose is in the picture -- it is not the unposed shape's.
        /// </summary>
        [Test]
        public void ASkinnedMesh_IsDrawnAsUrpLitDrawsIt_InTheLightAndFullyInShadow()
        {
            Mesh capsule = Capsule();
            Color32[] vp = Draw(capsule, Product(null), true);
            Color32[] lit = Draw(capsule, LitWithTheColorMaterialsValues(null), true);
            AssertSame(vp, lit, "a skinned mesh in the light");
            Difference posed = Compare(vp, Draw(capsule, Product(null), false));
            Assert.That(posed.over, Is.GreaterThan(200), "the bone's pose is drawn: " + posed);

            _occluder.SetActive(true);
            Color32[] vpShadow = Draw(capsule, Product(null), true);
            Color32[] litShadow = Draw(capsule, LitWithTheColorMaterialsValues(null), true);
            AssertSame(vpShadow, litShadow, "a skinned mesh fully in shadow");
            Assert.That(Compare(vp, vpShadow).worst, Is.GreaterThan(30), "the shadow takes the main light away");
        }

        // ----- helpers -----------------------------------------------------------------------------------------------

        private struct Difference
        {
            internal int drawn, over, worst;

            public override string ToString()
            {
                return "pixels drawn " + drawn + ", differing by more than 2 in a channel " + over + ", the largest difference " + worst;
            }
        }

        private static Difference Compare(Color32[] a, Color32[] b)
        {
            var difference = default(Difference);
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].r > 8 || a[i].g > 8 || a[i].b > 8 || b[i].r > 8 || b[i].g > 8 || b[i].b > 8) difference.drawn++;
                int d = Mathf.Max(Mathf.Abs(a[i].r - b[i].r), Mathf.Max(Mathf.Abs(a[i].g - b[i].g), Mathf.Abs(a[i].b - b[i].b)));
                difference.worst = Mathf.Max(difference.worst, d);
                if (d > 2) difference.over++;
            }

            return difference;
        }

        private static void AssertSame(Color32[] vp, Color32[] reference, string what)
        {
            Difference difference = Compare(vp, reference);
            TestContext.Out.WriteLine(what + ": " + difference);
            Assert.That(difference.drawn, Is.GreaterThan(Size * Size / 10), what + ": the shape covers the view");
            Assert.That(difference.over, Is.Zero, what + ": " + difference);
        }

        private static int Grey(Color32 p)
        {
            return (p.r + p.g + p.b) / 3;
        }

        private Color32 At(Color32[] image, Vector3 world)
        {
            Vector3 viewport = _camera.WorldToViewportPoint(world);
            int x = Mathf.Clamp(Mathf.RoundToInt(viewport.x * Size - 0.5f), 0, Size - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(viewport.y * Size - 0.5f), 0, Size - 1);
            return image[y * Size + x];
        }

        private Material Product(Texture2D map)
        {
            Shader shader = Shader.Find(ProductShader);
            Assert.That(shader, Is.Not.Null, ProductShader);
            var material = Track(new Material(shader));
            material.SetFloat("_VpUsePaletteAtlas", 0f);
            material.SetColor("_BaseColor", Color.white);
            if (map != null) material.SetTexture("_BaseMap", map);
            return material;
        }

        /// <summary>URP/Lit as the city's Color material is imported (read from the assets, 2026-10-06), with the given base map.</summary>
        private Material LitWithTheColorMaterialsValues(Texture2D map)
        {
            Shader shader = Shader.Find(LitShader);
            Assert.That(shader, Is.Not.Null, LitShader);
            var material = Track(new Material(shader));
            material.SetColor("_BaseColor", new Color(0.8f, 0.8f, 0.8f, 1f));
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 0.5f);
            material.SetFloat("_WorkflowMode", 1f);
            material.SetFloat("_Surface", 0f);
            material.SetFloat("_ReceiveShadows", 1f);
            material.SetFloat("_SpecularHighlights", 1f);
            material.SetFloat("_EnvironmentReflections", 1f);
            if (map != null) material.SetTexture("_BaseMap", map);
            Assert.That(material.shaderKeywords, Is.Empty, "the Color material has no keyword");
            return material;
        }

        private Texture2D Gradient()
        {
            var texture = Track(new Texture2D(64, 64, TextureFormat.RGBA32, false));
            var pixels = new Color32[64 * 64];
            for (int y = 0; y < 64; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    pixels[y * 64 + x] = new Color32((byte)(60 + x * 3), (byte)(60 + y * 3), 160, 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        private Mesh Sphere()
        {
            return Primitive(PrimitiveType.Sphere);
        }

        /// <summary>A capsule with one bone that every vertex follows.</summary>
        private Mesh Capsule()
        {
            Mesh mesh = Track(Object.Instantiate(Primitive(PrimitiveType.Capsule)));
            var weights = new BoneWeight[mesh.vertexCount];
            for (int i = 0; i < weights.Length; i++)
            {
                weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            }

            mesh.boneWeights = weights;
            mesh.bindposes = new[] { Matrix4x4.identity };
            return mesh;
        }

        private Mesh Primitive(PrimitiveType type)
        {
            GameObject primitive = GameObject.CreatePrimitive(type);
            Mesh mesh = primitive.GetComponent<MeshFilter>().sharedMesh;
            Object.DestroyImmediate(primitive);
            return mesh;
        }

        /// <summary>
        /// Draws the shape alone on the stage -- twice its size, at the origin -- and reads the image. As a skinned
        /// mesh it is posed by its one bone: turned about the view's axis and about the vertical.
        /// </summary>
        private Color32[] Draw(Mesh mesh, Material material, bool skinned)
        {
            var subject = new GameObject("Lighting Test Subject");
            GameObject bone = null;
            try
            {
                subject.transform.localScale = new Vector3(2f, 2f, 2f);
                if (skinned)
                {
                    bone = new GameObject("Lighting Test Bone");
                    bone.transform.SetParent(subject.transform, false);
                    bone.transform.localRotation = Quaternion.Euler(0f, 35f, 55f);
                    SkinnedMeshRenderer renderer = subject.AddComponent<SkinnedMeshRenderer>();
                    renderer.sharedMesh = mesh;
                    renderer.bones = new[] { bone.transform };
                    renderer.rootBone = bone.transform;
                    renderer.localBounds = new Bounds(Vector3.zero, new Vector3(4f, 4f, 4f));
                    renderer.updateWhenOffscreen = true;
                    renderer.sharedMaterial = material;
                }
                else
                {
                    subject.AddComponent<MeshFilter>().sharedMesh = mesh;
                    subject.AddComponent<MeshRenderer>().sharedMaterial = material;
                }

                var request = new RenderPipeline.StandardRequest { destination = _target };
                Assert.That(RenderPipeline.SupportsRenderRequest(_camera, request), Is.True, "render request");
                RenderPipeline.SubmitRenderRequest(_camera, request);

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = _target;
                var read = Track(new Texture2D(Size, Size, TextureFormat.RGBA32, false));
                read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                read.Apply(false);
                RenderTexture.active = previous;
                return read.GetPixels32();
            }
            finally
            {
                Object.DestroyImmediate(subject);
                if (bone != null) Object.DestroyImmediate(bone);
            }
        }

        private T Track<T>(T tracked) where T : Object
        {
            _objects.Add(tracked);
            return tracked;
        }
    }
}
