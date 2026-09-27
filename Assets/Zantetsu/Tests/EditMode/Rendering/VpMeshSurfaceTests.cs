using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// The product's surface for an ordinary mesh ("Zantetsu/VP Mesh Surface", what an uncut character is drawn with)
    /// renders what the independent test oracle ("Hidden/Zantetsu/Compact16uv Mesh Oracle") renders: the same palette
    /// atlas lookup and the same shading, read back as pixels, with the atlas and with a plain base map. It also carries
    /// the instancing variants Single Pass Instanced stereo needs, which the oracle does not.
    /// <para>
    /// The palette atlas and cut-surface globals are taken at the start and put back at the end.
    /// </para>
    /// </summary>
    public class VpMeshSurfaceTests
    {
        private const string ProductShader = "Zantetsu/VP Mesh Surface";
        private const string OracleShader = "Hidden/Zantetsu/Compact16uv Mesh Oracle";
        private const int Size = 64;

        private readonly List<Object> _objects = new List<Object>();
        private VpCutSurfaceColour.State _colours;
        private Texture2D _oldNormal, _oldDebug;

        [SetUp]
        public void TakeTheGlobals()
        {
            _colours = VpCutSurfaceColour.Capture();
            _oldNormal = VpCutSurfaceAtlas.Normal;
            _oldDebug = VpCutSurfaceAtlas.Debug;
        }

        [TearDown]
        public void PutBackTheGlobals()
        {
            VpCutSurfaceAtlas.Clear();
            VpCutSurfaceColour.Restore(_colours);
            if (_oldNormal != null && _oldDebug != null)
            {
                VpCutSurfaceAtlas.Bind(_oldNormal, _oldDebug);
            }

            foreach (Object tracked in _objects)
            {
                if (tracked != null) Object.DestroyImmediate(tracked);
            }

            _objects.Clear();
        }

        [Test]
        public void WithThePaletteAtlas_TheProductSurfaceDrawsWhatTheOracleDraws()
        {
            VpCutSurfaceColour.SetDebugEnabled(false);
            VpCutSurfaceAtlas.Bind(Gradient(false), Gradient(true));
            Material product = Material(ProductShader);
            product.SetFloat("_VpUsePaletteAtlas", 1f);
            product.SetColor("_BaseColor", Color.white);
            Material oracle = Material(OracleShader);
            oracle.SetFloat("_UseSourcePalette", 0f);

            Color32[] a = Render(product);
            Color32[] b = Render(oracle);
            AssertDrawnAndEqual(a, b);
        }

        [Test]
        public void WithABaseMap_TheProductSurfaceDrawsWhatTheOracleDrawsFromItsSourcePalette()
        {
            VpCutSurfaceAtlas.Clear();
            Texture2D map = Gradient(false);
            Material product = Material(ProductShader);
            product.SetFloat("_VpUsePaletteAtlas", 0f);
            product.SetColor("_BaseColor", Color.white);
            product.SetTexture("_BaseMap", map);
            Material oracle = Material(OracleShader);
            oracle.SetFloat("_UseSourcePalette", 1f);
            oracle.SetTexture("_BaseMap", map);

            AssertDrawnAndEqual(Render(product), Render(oracle));
        }

        [Test]
        public void TheProductSurface_HasTheInstancingVariantsStereoNeeds_AndTheOracleHasNone()
        {
            Shader product = Shader.Find(ProductShader);
            Shader oracle = Shader.Find(OracleShader);
            Assert.That(product, Is.Not.Null);
            Assert.That(product.isSupported, Is.True);
            Assert.That(oracle, Is.Not.Null);
            Assert.That(product.keywordSpace.keywordNames, Does.Contain("INSTANCING_ON"), "multi_compile_instancing");
            Assert.That(oracle.keywordSpace.keywordNames, Does.Not.Contain("INSTANCING_ON"), "the oracle is the one-view reference it was");
        }

        private static void AssertDrawnAndEqual(Color32[] a, Color32[] b)
        {
            int drawn = 0, different = 0;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].r > 8 || a[i].g > 8 || a[i].b > 8) drawn++;
                if (Mathf.Abs(a[i].r - b[i].r) > 1 || Mathf.Abs(a[i].g - b[i].g) > 1 || Mathf.Abs(a[i].b - b[i].b) > 1) different++;
            }

            Assert.That(drawn, Is.GreaterThan(Size * Size / 8), "the triangle covers the view");
            Assert.That(different, Is.Zero, "pixels differing by more than 1 in a channel");
        }

        private Material Material(string name)
        {
            Shader shader = Shader.Find(name);
            Assert.That(shader, Is.Not.Null, name);
            return Track(new Material(shader));
        }

        // 256 x 256, as the atlas pair must be: colour varies with uv, so a wrong lookup shows.
        private Texture2D Gradient(bool debug)
        {
            var texture = Track(new Texture2D(256, 256, TextureFormat.RGBA32, false));
            var pixels = new Color32[256 * 256];
            for (int y = 0; y < 256; y++)
            {
                for (int x = 0; x < 256; x++)
                {
                    pixels[y * 256 + x] = new Color32((byte)x, (byte)y, (byte)(debug ? 255 : 128), 255);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply(false);
            return texture;
        }

        private Color32[] Render(Material material)
        {
            var mesh = Track(new Mesh());
            mesh.SetVertices(new[] { new Vector3(-0.9f, -0.9f, 0f), new Vector3(-0.9f, 0.9f, 0f), new Vector3(0.9f, 0f, 0f) });
            mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back });
            mesh.SetUVs(0, new[] { new Vector2(0.1f, 0.1f), new Vector2(0.1f, 0.4f), new Vector2(0.45f, 0.25f) });
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);

            var target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32));
            Camera camera = Track(new GameObject("VP Mesh Surface Camera")).AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = 1f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            camera.transform.position = new Vector3(0f, 0f, -5f);
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;

            Graphics.RenderMesh(new RenderParams(material) { camera = camera }, mesh, 0, Matrix4x4.identity);
            var request = new RenderPipeline.StandardRequest { destination = target };
            Assert.That(RenderPipeline.SupportsRenderRequest(camera, request), Is.True, "render request");
            RenderPipeline.SubmitRenderRequest(camera, request);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var readback = Track(new Texture2D(Size, Size, TextureFormat.RGBA32, false));
            readback.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            readback.Apply(false);
            RenderTexture.active = previous;
            return readback.GetPixels32();
        }

        private T Track<T>(T tracked) where T : Object
        {
            _objects.Add(tracked);
            return tracked;
        }
    }
}
