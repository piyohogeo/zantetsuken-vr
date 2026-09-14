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
    /// triangles, so the drawn shape follows the index buffer order and the range start, and the draw casts a shadow
    /// onto a Unity mesh. Coverage is counted per region of the image, not compared per pixel.
    /// </summary>
    public class VpDirectDrawTests
    {
        private const string ShaderName = "Zantetsu/VP Unlit";
        private const int Size = 64;
        private const int CoveredPixels = 200;
        private const int ShadowSize = 128;
        private const int ShadowedPixels = 300;

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

        /// <summary>Renders [indexStart, indexStart + indexCount) of the mesh and counts the green pixels in each half.</summary>
        private (int left, int right) Coverage(Mesh mesh, int indexStart, int indexCount)
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
                VpDirectDraw.Render(
                    material,
                    new MaterialPropertyBlock(),
                    buffers,
                    range,
                    Matrix4x4.identity,
                    new Bounds(Vector3.zero, Vector3.one * 4f),
                    0,
                    camera);

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

        /// <summary>
        /// Renders a lit Unity mesh ground from above, with or without a VP cube floating over it under a low
        /// directional light, and returns the luminance of every ground pixel (-1 where the VP cube itself is seen).
        /// </summary>
        private float[] GroundFromAbove(bool drawVpCube)
        {
            GameObject sun = Track(new GameObject("VP Shadow Test Sun"));
            sun.transform.rotation = Quaternion.Euler(35f, 0f, 0f);
            Light light = sun.AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Hard;
            light.intensity = 1f;

            GameObject ground = Track(GameObject.CreatePrimitive(PrimitiveType.Plane));
            Object.DestroyImmediate(ground.GetComponent<Collider>());
            MeshRenderer groundRenderer = ground.GetComponent<MeshRenderer>();
            groundRenderer.receiveShadows = true;
            groundRenderer.shadowCastingMode = ShadowCastingMode.Off;

            RenderTexture target = Track(new RenderTexture(ShadowSize, ShadowSize, 24, RenderTextureFormat.ARGB32));
            Camera camera = TestCamera("VP Shadow Test Camera", target);
            camera.transform.SetPositionAndRotation(new Vector3(0f, 10f, 0f), Quaternion.Euler(90f, 0f, 0f));
            camera.orthographicSize = 4f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;

            Material material = VpMaterial(Color.green);
            Mesh cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
            using (var pool = new VpCpuGeometryPool(cube.vertexCount, (int)cube.GetIndexCount(0), Allocator.Persistent))
            using (var buffers = new VpGpuGeometryBuffers(cube.vertexCount, (int)cube.GetIndexCount(0)))
            {
                Assert.That(pool.TryAppend(cube, out VpGeometryRange range), Is.True);
                Assert.That(buffers.TryUpload(pool), Is.True);
                if (drawVpCube)
                {
                    Matrix4x4 objectToWorld = Matrix4x4.Translate(new Vector3(0f, 1.2f, 0f));
                    VpDirectDraw.Render(
                        material,
                        new MaterialPropertyBlock(),
                        buffers,
                        range,
                        objectToWorld,
                        new Bounds(new Vector3(0f, 1.2f, 0f), Vector3.one),
                        0,
                        camera);
                }

                Color32[] pixels = RenderAndRead(camera, target);
                var luminance = new float[pixels.Length];
                for (int i = 0; i < pixels.Length; i++)
                {
                    Color32 p = pixels[i];
                    luminance[i] = p.g > p.r + 50 ? -1f : (p.r + p.g + p.b) / 3f;
                }

                return luminance;
            }
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
        public void ADirectDraw_CastsAShadowOntoAUnityMesh()
        {
            SceneSetup[] previousSetup = EditorSceneManager.GetSceneManagerSetup();
            try
            {
                EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                float[] withoutCube = GroundFromAbove(false);
                DestroyObjects();
                float[] withCube = GroundFromAbove(true);

                float litGround = 0f;
                foreach (float value in withoutCube)
                {
                    litGround += value;
                }

                litGround /= withoutCube.Length;
                int cubePixels = 0;
                int shadowedWithout = 0;
                int shadowedWith = 0;
                for (int i = 0; i < withCube.Length; i++)
                {
                    if (withoutCube[i] < litGround * 0.7f)
                    {
                        shadowedWithout++;
                    }

                    if (withCube[i] < 0f)
                    {
                        cubePixels++;
                    }
                    else if (withCube[i] < litGround * 0.7f)
                    {
                        shadowedWith++;
                    }
                }

                Assert.That(litGround, Is.GreaterThan(40f), "lit ground brightness");
                Assert.That(shadowedWithout, Is.Zero, "shadowed ground without the VP cube");
                Assert.That(cubePixels, Is.GreaterThan(CoveredPixels), "VP cube seen from above");
                Assert.That(shadowedWith, Is.GreaterThan(ShadowedPixels), "ground shadowed by the VP cube");
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
    }
}
