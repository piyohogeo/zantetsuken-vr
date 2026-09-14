using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Stage 1 VP draw (DESIGN 4.5.5): the VP shader compiles with its colour and shadow caster passes, a Direct
    /// non-indexed draw rendered by the pipeline into a small render texture colours the pixels of its index range's
    /// triangles, so the drawn shape follows the index buffer order and the range start, the draw casts a shadow onto a
    /// Unity mesh, and it receives the main light shadow of a Unity mesh. Coverage is counted per region of the image,
    /// not compared per pixel.
    /// </summary>
    public class VpDirectDrawTests
    {
        private const string ShaderName = "Zantetsu/VP Unlit";
        private const int Size = 64;
        private const int CoveredPixels = 200;
        private const int ShadowSize = 128;
        private const int ShadowedPixels = 300;
        // A Unity Lit surface falls to about its ambient term in shadow.
        private const float LitShadowedFraction = 0.7f;

        // The VP shader keeps half of its colour in shadow: 0.5 against about 0.9 linear, roughly 0.77 once sRGB encoded.
        private const float VpShadowedFraction = 0.9f;

        // Two separate triangles facing a camera that looks along +Z: vertices 0-2 left of the centre, 3-5 right of it.
        private static readonly Vector3[] Positions =
        {
            new Vector3(-0.9f, -0.8f, 0f), new Vector3(-0.5f, 0.8f, 0f), new Vector3(-0.1f, -0.8f, 0f),
            new Vector3(0.1f, -0.8f, 0f), new Vector3(0.5f, 0.8f, 0f), new Vector3(0.9f, -0.8f, 0f),
        };

        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void DestroyObjects()
        {
            foreach (Object tracked in _objects)
            {
                if (tracked != null)
                {
                    Object.DestroyImmediate(tracked);
                }
            }

            _objects.Clear();
        }

        private T Track<T>(T tracked) where T : Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        private Material VpMaterial(Color color)
        {
            Shader shader = Shader.Find(ShaderName);
            Assert.That(shader, Is.Not.Null, ShaderName);
            Material material = Track(new Material(shader));
            material.SetColor("_BaseColor", color);
            return material;
        }

        private Camera TestCamera(string name, RenderTexture target)
        {
            GameObject cameraObject = Track(new GameObject(name));
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.targetTexture = target;
            return camera;
        }

        private Color32[] RenderAndRead(Camera camera, RenderTexture target)
        {
            var request = new RenderPipeline.StandardRequest { destination = target };
            Assert.That(RenderPipeline.SupportsRenderRequest(camera, request), Is.True);
            RenderPipeline.SubmitRenderRequest(camera, request);

            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            Texture2D readback = Track(new Texture2D(target.width, target.height, TextureFormat.RGBA32, false));
            readback.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            readback.Apply(false);
            RenderTexture.active = previous;
            return readback.GetPixels32();
        }

        private Mesh TwoTriangles(params int[] triangles)
        {
            Mesh mesh = Track(new Mesh());
            mesh.SetVertices(Positions);
            mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            mesh.SetTriangles(triangles, 0);
            return mesh;
        }

        /// <summary>
        /// Uploads the mesh once, draws [indexStart, indexStart + indexCount) of it at each transform (identity when none
        /// is given) with one property block, and counts the green pixels in each half.
        /// </summary>
        private (int left, int right) Coverage(Mesh mesh, int indexStart, int indexCount, params Matrix4x4[] objectToWorlds)
        {
            Material material = VpMaterial(Color.green);
            RenderTexture target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32));
            Camera camera = TestCamera("VP Draw Test Camera", target);
            camera.transform.position = new Vector3(0f, 0f, -5f);
            camera.orthographicSize = 1f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;

            using (var pool = new VpCpuGeometryPool(16, 16, Allocator.Persistent))
            using (var buffers = new VpGpuGeometryBuffers(16, 16))
            {
                Assert.That(pool.TryAppend(mesh, out VpGeometryRange whole), Is.True);
                Assert.That(buffers.TryUpload(pool), Is.True);
                var range = new VpGeometryRange(whole.vertexStart, whole.vertexCount, indexStart, indexCount);
                var properties = new MaterialPropertyBlock();
                foreach (Matrix4x4 objectToWorld in objectToWorlds.Length > 0 ? objectToWorlds : new[] { Matrix4x4.identity })
                {
                    VpDirectDraw.Render(
                        material,
                        properties,
                        buffers,
                        range,
                        objectToWorld,
                        new Bounds(Vector3.zero, Vector3.one * 4f),
                        0,
                        camera);
                }

                Color32[] pixels = RenderAndRead(camera, target);
                int left = 0;
                int right = 0;
                for (int i = 0; i < pixels.Length; i++)
                {
                    if (pixels[i].g > 64 && pixels[i].r < 64)
                    {
                        if (i % Size < Size / 2)
                        {
                            left++;
                        }
                        else
                        {
                            right++;
                        }
                    }
                }

                return (left, right);
            }
        }

        /// <summary>Runs the body in a new empty scene, so its directional light is the main light, then restores the scenes.</summary>
        private void InEmptyScene(Action body)
        {
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                body();
            }
            finally
            {
                DestroyObjects();
                if (previousSetup != null && previousSetup.Length > 0)
                {
                    EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
                }
                else
                {
                    EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
                }
            }
        }

        /// <summary>A low directional light with hard shadows and a top-down orthographic camera over the origin.</summary>
        private (Camera camera, RenderTexture target) LitTopDownView()
        {
            GameObject sun = Track(new GameObject("VP Shadow Test Sun"));
            sun.transform.rotation = Quaternion.Euler(35f, 0f, 0f);
            Light light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Hard;
            light.intensity = 1f;

            RenderTexture target = Track(new RenderTexture(ShadowSize, ShadowSize, 24, RenderTextureFormat.ARGB32));
            Camera camera = TestCamera("VP Shadow Test Camera", target);
            camera.transform.SetPositionAndRotation(new Vector3(0f, 10f, 0f), Quaternion.Euler(90f, 0f, 0f));
            camera.orthographicSize = 4f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            return (camera, target);
        }

        /// <summary>Queues one VP draw of a built-in mesh at the transform for the camera, keeping its buffers alive for the render.</summary>
        private static void RenderVpMesh(Material material, Mesh mesh, Matrix4x4 objectToWorld, Bounds worldBounds, Camera camera, Action render)
        {
            int indexCount = (int)mesh.GetIndexCount(0);
            using (var pool = new VpCpuGeometryPool(mesh.vertexCount, indexCount, Allocator.Persistent))
            using (var buffers = new VpGpuGeometryBuffers(mesh.vertexCount, indexCount))
            {
                Assert.That(pool.TryAppend(mesh, out VpGeometryRange range), Is.True);
                Assert.That(buffers.TryUpload(pool), Is.True);
                VpDirectDraw.Render(material, new MaterialPropertyBlock(), buffers, range, objectToWorld, worldBounds, 0, camera);
                render();
            }
        }

        /// <summary>Luminance of every pixel, or -1 where the marker colour (the object the shadow comes from or goes to) is seen.</summary>
        private static float[] Luminance(Color32[] pixels, Func<Color32, bool> isMarker)
        {
            var luminance = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                Color32 p = pixels[i];
                luminance[i] = isMarker(p) ? -1f : (p.r + p.g + p.b) / 3f;
            }

            return luminance;
        }

        /// <summary>
        /// Compares a render without the shadowing object against one with it: the lit brightness, the shadowed pixels
        /// without it, the marker pixels with it and the shadowed non-marker pixels with it.
        /// </summary>
        private static void AssertShadowAppears(float[] without, float[] with, float shadowedFraction, string what)
        {
            float lit = 0f;
            foreach (float value in without)
            {
                lit += Mathf.Max(0f, value);
            }

            lit /= without.Length;
            int shadowedWithout = 0;
            int marker = 0;
            int shadowedWith = 0;
            for (int i = 0; i < with.Length; i++)
            {
                if (without[i] >= 0f && without[i] < lit * shadowedFraction)
                {
                    shadowedWithout++;
                }

                if (with[i] < 0f)
                {
                    marker++;
                }
                else if (with[i] < lit * shadowedFraction)
                {
                    shadowedWith++;
                }
            }

            Assert.That(lit, Is.GreaterThan(40f), what + ": lit brightness");
            Assert.That(shadowedWithout, Is.Zero, what + ": shadowed pixels without the shadowing object");
            Assert.That(marker, Is.GreaterThan(CoveredPixels), what + ": marker pixels");
            Assert.That(shadowedWith, Is.GreaterThan(ShadowedPixels), what + ": shadowed pixels");
        }

        [Test]
        public void TheVpShader_CompilesWithAColourAndAShadowCasterPass()
        {
            Shader shader = Shader.Find(ShaderName);

            Assert.That(shader, Is.Not.Null, ShaderName);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            Assert.That(shader.isSupported, Is.True);
            Material material = VpMaterial(Color.white);
            Assert.That(material.FindPass("VpForward"), Is.GreaterThanOrEqualTo(0), "colour pass");
            int shadowPass = material.FindPass("ShadowCaster");
            Assert.That(shadowPass, Is.GreaterThanOrEqualTo(0), "shadow caster pass");
            Assert.That(shader.FindPassTagValue(shadowPass, new ShaderTagId("LightMode")).name, Is.EqualTo("ShadowCaster").IgnoreCase);
        }

        [Test]
        public void ADirectNonIndexedDraw_ColoursTheTrianglesOfItsRangeOnly()
        {
            (int left, int right) = Coverage(TwoTriangles(0, 1, 2, 3, 4, 5), 0, 6);

            Assert.That(left, Is.GreaterThan(CoveredPixels), "left triangle");
            Assert.That(right, Is.GreaterThan(CoveredPixels), "right triangle");
            Assert.That(left + right, Is.LessThan(Size * Size / 2), "background stays uncovered");
        }

        [Test]
        public void TheDrawnShape_FollowsTheIndexBufferOrderAndTheRangeStart()
        {
            Mesh leftFirst = TwoTriangles(0, 1, 2, 3, 4, 5);
            Mesh rightFirst = TwoTriangles(3, 4, 5, 0, 1, 2);

            (int left, int right) firstOfLeftFirst = Coverage(leftFirst, 0, 3);
            (int left, int right) firstOfRightFirst = Coverage(rightFirst, 0, 3);
            (int left, int right) secondOfLeftFirst = Coverage(leftFirst, 3, 3);

            Assert.That(firstOfLeftFirst.left, Is.GreaterThan(CoveredPixels), "left-first, first triangle: left");
            Assert.That(firstOfLeftFirst.right, Is.Zero, "left-first, first triangle: right");
            Assert.That(firstOfRightFirst.left, Is.Zero, "right-first, first triangle: left");
            Assert.That(firstOfRightFirst.right, Is.GreaterThan(CoveredPixels), "right-first, first triangle: right");
            Assert.That(secondOfLeftFirst.left, Is.Zero, "left-first, second triangle: left");
            Assert.That(secondOfLeftFirst.right, Is.GreaterThan(CoveredPixels), "left-first, second triangle: right");
        }

        [Test]
        public void OneUploadedRange_DrawnAtTwoTransforms_AppearsAtBoth()
        {
            // Only the left triangle's range, drawn in place and moved right by one unit, from one upload and one
            // property block.
            (int left, int right) = Coverage(
                TwoTriangles(0, 1, 2, 3, 4, 5),
                0,
                3,
                Matrix4x4.identity,
                Matrix4x4.Translate(new Vector3(1f, 0f, 0f)));

            Assert.That(left, Is.GreaterThan(CoveredPixels), "draw in place");
            Assert.That(right, Is.GreaterThan(CoveredPixels), "draw moved right");
        }

        [Test]
        public void ADirectDraw_CastsAShadowOntoAUnityMesh()
        {
            // A lit Unity mesh ground seen from above, with and without a green VP cube floating over it.
            float[] GroundFromAbove(bool drawVpCube)
            {
                (Camera camera, RenderTexture target) = LitTopDownView();
                GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Plane));
                Object.DestroyImmediate(ground.GetComponent<Collider>());
                MeshRenderer groundRenderer = ground.GetComponent<MeshRenderer>();
                groundRenderer.receiveShadows = true;
                groundRenderer.shadowCastingMode = ShadowCastingMode.Off;

                Color32[] pixels = null;
                if (drawVpCube)
                {
                    RenderVpMesh(
                        VpMaterial(Color.green),
                        Resources.GetBuiltinResource<Mesh>("Cube.fbx"),
                        Matrix4x4.Translate(new Vector3(0f, 1.2f, 0f)),
                        new Bounds(new Vector3(0f, 1.2f, 0f), Vector3.one),
                        camera,
                        () => pixels = RenderAndRead(camera, target));
                }
                else
                {
                    pixels = RenderAndRead(camera, target);
                }

                return Luminance(pixels, p => p.g > p.r + 50);
            }

            InEmptyScene(() =>
            {
                float[] without = GroundFromAbove(false);
                DestroyObjects();
                float[] with = GroundFromAbove(true);
                AssertShadowAppears(without, with, LitShadowedFraction, "Unity mesh ground under a VP cube");
            });
        }

        [Test]
        public void ADirectDraw_ReceivesTheMainLightShadowOfAUnityMesh()
        {
            // A white VP slab (the built-in cube flattened to 10 x 0.1 x 10) seen from above, with and without a red Unity
            // mesh cube floating over it.
            float[] VpGroundFromAbove(bool drawOccluder)
            {
                (Camera camera, RenderTexture target) = LitTopDownView();
                if (drawOccluder)
                {
                    GameObject occluder = Track(GameObject.CreatePrimitive(PrimitiveType.Cube));
                    Object.DestroyImmediate(occluder.GetComponent<Collider>());
                    occluder.transform.position = new Vector3(0f, 1.2f, 0f);
                    Material red = Track(new Material(Shader.Find("Universal Render Pipeline/Lit")));
                    red.SetColor("_BaseColor", Color.red);
                    MeshRenderer occluderRenderer = occluder.GetComponent<MeshRenderer>();
                    occluderRenderer.sharedMaterial = red;
                    occluderRenderer.shadowCastingMode = ShadowCastingMode.On;
                }

                Color32[] pixels = null;
                RenderVpMesh(
                    VpMaterial(Color.white),
                    Resources.GetBuiltinResource<Mesh>("Cube.fbx"),
                    Matrix4x4.Scale(new Vector3(10f, 0.1f, 10f)),
                    new Bounds(Vector3.zero, new Vector3(10f, 0.1f, 10f)),
                    camera,
                    () => pixels = RenderAndRead(camera, target));
                return Luminance(pixels, p => p.r > p.g + 50);
            }

            InEmptyScene(() =>
            {
                float[] without = VpGroundFromAbove(false);
                DestroyObjects();
                float[] with = VpGroundFromAbove(true);
                AssertShadowAppears(without, with, VpShadowedFraction, "VP ground under a Unity mesh cube");
            });
        }
    }
}
