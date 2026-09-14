using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Stage 1 VP draw (DESIGN 4.5.5): the VP shader compiles, and a Direct non-indexed draw rendered by the pipeline
    /// into a small render texture colours the pixels of its index range's triangles, so the drawn shape follows the
    /// index buffer order and the range start. Coverage is counted per half of the image, not compared per pixel.
    /// </summary>
    public class VpDirectDrawTests
    {
        private const string ShaderName = "Zantetsu/VP Unlit";
        private const int Size = 64;
        private const int CoveredPixels = 200;

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
                Object.DestroyImmediate(tracked);
            }

            _objects.Clear();
        }

        private T Track<T>(T tracked) where T : Object
        {
            _objects.Add(tracked);
            return tracked;
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
            Shader shader = Shader.Find(ShaderName);
            Assert.That(shader, Is.Not.Null, ShaderName);
            Material material = Track(new Material(shader));
            material.SetColor("_BaseColor", Color.green);
            RenderTexture target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGB32));
            GameObject cameraObject = Track(new GameObject("VP Draw Test Camera"));
            cameraObject.transform.position = new Vector3(0f, 0f, -5f);
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.enabled = false;
            camera.orthographic = true;
            camera.orthographicSize = 1f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 20f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;

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

                var request = new RenderPipeline.StandardRequest { destination = target };
                Assert.That(RenderPipeline.SupportsRenderRequest(camera, request), Is.True);
                RenderPipeline.SubmitRenderRequest(camera, request);

                RenderTexture previous = RenderTexture.active;
                RenderTexture.active = target;
                Texture2D readback = Track(new Texture2D(Size, Size, TextureFormat.RGBA32, false));
                readback.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                readback.Apply(false);
                RenderTexture.active = previous;

                Color32[] pixels = readback.GetPixels32();
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

        [Test]
        public void TheVpShader_CompilesWithoutErrors()
        {
            Shader shader = Shader.Find(ShaderName);

            Assert.That(shader, Is.Not.Null, ShaderName);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            Assert.That(shader.isSupported, Is.True);
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
    }
}
