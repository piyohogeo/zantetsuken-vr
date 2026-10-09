using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.EditorTools.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// The sky fog of the ordinary Unity meshes (DESIGN 5.3.1): "Zantetsu/VP Mesh Surface" goes linearly to the sky's
    /// colour between seven tenths of the camera's far plane and the far plane, by the depth along the view, with the
    /// colour of the skybox in the direction looked along -- from a cubemap baked from the skybox alone.
    /// <para>
    /// The pictures are linear HDR values (a half-float target, no post-processing), so that what is compared is what
    /// the pass writes and what the skybox pass writes, at the stage both are drawn.
    /// </para>
    /// </summary>
    public class VpSkyFogTests
    {
        private const string SurfaceShader = "Zantetsu/VP Mesh Surface";
        private const int Size = 48;
        private const float Far = 100f;

        // The share of the sky in the fogged colour at a depth, for a far plane: the fog's own rule, written here once more.
        private static float ShareAt(float depth, float far = Far)
        {
            return Mathf.Clamp01((depth - VpSkyFog.StartShare * far) / ((1f - VpSkyFog.StartShare) * far));
        }

        private readonly List<Object> _objects = new List<Object>();

        private T Track<T>(T tracked) where T : Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        [TearDown]
        public void PutBack()
        {
            VpSkyFog.Set(null, "a test ended");
            foreach (Object tracked in _objects)
            {
                if (tracked != null) Object.DestroyImmediate(tracked);
            }

            _objects.Clear();
        }

        // A scene with Unity's default skybox and a sun, as the product's scenes have; the fog's cubemap baked from it.
        private void InSkyScene(System.Action<Cubemap, Vector3> body)
        {
            EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            try
            {
                Light sun = Object.FindFirstObjectByType<Light>();
                Assert.That(sun != null && sun.type == LightType.Directional, Is.True, "the scene's sun");
                Assert.That(RenderSettings.skybox, Is.Not.Null, "the scene's skybox");
                VpSurfaceMaterial.EnsureInitialized();
                Cubemap sky = VpSkyFogBake.Bake(VpSkyFog.FaceSize, out string report);
                TestContext.Out.WriteLine(report);
                Assert.That(sky, Is.Not.Null, report);
                Track(sky);
                body(sky, -sun.transform.forward);
            }
            finally
            {
                VpSkyFog.Set(null, "a test ended");
                EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            }
        }

        private Camera Eye(Quaternion rotation, float far = Far)
        {
            var target = Track(new RenderTexture(Size, Size, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear));
            Camera camera = Track(new GameObject("VP Sky Fog Test Eye")).AddComponent<Camera>();
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.Skybox;
            camera.allowHDR = true;
            camera.allowMSAA = false;
            camera.fieldOfView = 60f;
            camera.aspect = 1f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = far;
            camera.targetTexture = target;
            camera.transform.SetPositionAndRotation(new Vector3(3f, 20f, -7f), rotation);
            return camera;
        }

        private Material Surface()
        {
            Shader shader = Shader.Find(SurfaceShader);
            Assert.That(shader, Is.Not.Null, SurfaceShader);
            Material material = Track(new Material(shader));
            material.SetFloat("_VpUsePaletteAtlas", 0f);
            material.SetColor("_BaseColor", new Color(0.9f, 0.35f, 0.2f, 1f));
            return material;
        }

        // A wall across the whole view, square to the view's axis, at that depth along it: every pixel of it is at the
        // one view depth, while its corners are farther from the eye than its centre.
        private Mesh Wall()
        {
            var mesh = Track(new Mesh());
            mesh.SetVertices(new[] { new Vector3(-1f, -1f, 0f), new Vector3(-1f, 1f, 0f), new Vector3(1f, 1f, 0f), new Vector3(1f, -1f, 0f) });
            mesh.SetNormals(new[] { Vector3.back, Vector3.back, Vector3.back, Vector3.back });
            mesh.SetUVs(0, new[] { new Vector2(0f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(1f, 0f) });
            mesh.SetTriangles(new[] { 0, 1, 2, 0, 2, 3 }, 0);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(2f, 2f, 0.01f));
            return mesh;
        }

        private static Matrix4x4 WallAt(Camera camera, float depth)
        {
            float half = depth * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.3f;
            Transform eye = camera.transform;
            return Matrix4x4.TRS(eye.position + eye.forward * depth, eye.rotation, new Vector3(half, half, 1f));
        }

        // The camera's picture, linear HDR; with a wall of the product surface at that depth, or (depth below zero)
        // the sky alone.
        private Color[] Picture(Camera camera, Material material, Mesh wall, float depth)
        {
            if (depth >= 0f)
            {
                Graphics.RenderMesh(new RenderParams(material) { camera = camera, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false }, wall, 0, WallAt(camera, depth));
            }

            RenderTexture target = camera.targetTexture;
            var request = new RenderPipeline.StandardRequest { destination = target };
            Assert.That(RenderPipeline.SupportsRenderRequest(camera, request), Is.True, "render request");
            RenderPipeline.SubmitRenderRequest(camera, request);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var readback = Track(new Texture2D(Size, Size, TextureFormat.RGBAHalf, false, true));
            readback.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
            readback.Apply(false);
            RenderTexture.active = previous;
            return readback.GetPixels();
        }

        private static float Largest(Color[] a, Color[] b, out float mean)
        {
            float largest = 0f;
            double sum = 0;
            for (int i = 0; i < a.Length; i++)
            {
                float d = Mathf.Max(Mathf.Abs(a[i].r - b[i].r), Mathf.Max(Mathf.Abs(a[i].g - b[i].g), Mathf.Abs(a[i].b - b[i].b)));
                largest = Mathf.Max(largest, d);
                sum += d;
            }

            mean = (float)(sum / a.Length);
            return largest;
        }

        // The share of the sky in a fogged colour, read back from the channel in which the lit colour and the sky differ most.
        private static float Share(Color lit, Color fogged, Color sky)
        {
            float r = Mathf.Abs(sky.r - lit.r), g = Mathf.Abs(sky.g - lit.g), b = Mathf.Abs(sky.b - lit.b);
            if (r >= g && r >= b) return (fogged.r - lit.r) / (sky.r - lit.r);
            return g >= b ? (fogged.g - lit.g) / (sky.g - lit.g) : (fogged.b - lit.b) / (sky.b - lit.b);
        }

        private static Color[] Lerp(Color[] a, Color[] b, float t)
        {
            var mixed = new Color[a.Length];
            for (int i = 0; i < a.Length; i++) mixed[i] = Color.LerpUnclamped(a[i], b[i], t);
            return mixed;
        }

        // A view turned away from the sun and raised forty degrees: the sky in it is smooth -- no sun disc, and no
        // horizon, where the skybox steps from the sky's colour to the ground's within less than a cubemap pixel.
        private static Quaternion AwayFromTheSun(Vector3 toSun)
        {
            Vector3 level = new Vector3(-toSun.x, 0f, -toSun.z).normalized;
            return Quaternion.LookRotation(level, Vector3.up) * Quaternion.Euler(-40f, 0f, 0f);
        }

        /// <summary>
        /// **Nothing up to seven tenths of the far plane; then linear to the sky's colour at the far plane, by the depth
        /// along the view.** A wall square to the view, at depths before the start, at it, and at 5, 25, 50, 75 and 90
        /// per cent of the way from it to the far plane: with the fog off the lit wall; with it on the wall before the
        /// start is the same picture to the value, and past it every pixel -- the corners, farther from the eye than
        /// the centre, like the centre -- is the lit colour and the sky's colour mixed by that share.
        /// </summary>
        [Test]
        public void TheFog_IsNoneUpToSevenTenthsOfTheFarPlane_ThenLinearToTheSkyAtTheFarPlane_ByTheDepthAlongTheView()
        {
            InSkyScene((sky, toSun) =>
            {
                Camera camera = Eye(AwayFromTheSun(toSun));
                Material material = Surface();
                Mesh wall = Wall();
                Color[] skyAlone = Picture(camera, material, wall, -1f);
                Assert.That(Largest(skyAlone, new Color[skyAlone.Length], out float skyMean), Is.GreaterThan(0.05f), "the layout: the sky is drawn");

                Assert.That(VpSkyFog.StartShare, Is.EqualTo(0.7f), "the fog begins at seven tenths of the far plane");
                foreach (float depth in new[] { 5f, 40f, 69.9f, 70f })
                {
                    VpSkyFog.Set(null, "the comparison");
                    Color[] off = Picture(camera, material, wall, depth);
                    VpSkyFog.Set(sky);
                    Color[] on = Picture(camera, material, wall, depth);
                    Assert.That(Largest(off, skyAlone, out _), Is.GreaterThan(0.05f), "the layout: the lit wall at " + depth + " m is not the sky's colour");
                    Assert.That(Largest(on, off, out _), Is.LessThan(1e-3f), "at " + depth + " m, before the fog begins, the picture is the one without the fog");
                }

                var shares = new List<string>();
                // Not nearer the far plane than nine tenths of the way from the start: in the far plane's last thousandth the skybox itself is
                // drawn over a mesh (it is drawn at the far plane, within the depth buffer's last steps).
                foreach (float share in new[] { 0.05f, 0.25f, 0.5f, 0.75f, 0.9f })
                {
                    float depth = Far * (VpSkyFog.StartShare + (1f - VpSkyFog.StartShare) * share);
                    VpSkyFog.Set(null, "the comparison");
                    Color[] off = Picture(camera, material, wall, depth);
                    VpSkyFog.Set(sky);
                    Color[] on = Picture(camera, material, wall, depth);
                    float largest = Largest(on, Lerp(off, skyAlone, share), out float mean);
                    int centre = (Size / 2) * Size + Size / 2;
                    shares.Add(depth.ToString("0.0#") + " m: share " + share + ", largest difference from the mix " + largest.ToString("0.0000") + ", mean " + mean.ToString("0.0000")
                        + "; at the centre lit " + off[centre] + ", fogged " + on[centre] + ", sky " + skyAlone[centre] + ", share read back " + Share(off[centre], on[centre], skyAlone[centre]).ToString("0.000"));
                    TestContext.Out.WriteLine(shares[shares.Count - 1]);

                    // The sky's colour in the mix is the cubemap's, which in this view is within 0.005 of the skybox at
                    // every pixel (the case below measures it): that much, by the share, is allowed.
                    Assert.That(largest, Is.LessThan(0.002f + 0.005f * share), "at " + depth + " m every pixel is the lit colour and the sky's mixed by " + share);
                    Assert.That(Share(off[centre], on[centre], skyAlone[centre]), Is.EqualTo(share).Within(0.01f), "the share read back at the centre");
                    Assert.That(Largest(on, off, out _), Is.GreaterThan(0.01f * share), "and is not the picture without the fog");
                }

                TestContext.Out.WriteLine("sky mean " + skyMean.ToString("0.000") + "; " + string.Join("; ", shares));
            });
        }

        /// <summary>
        /// **The fog's colour is the sky's in the direction looked along, and a mesh at the far plane meets the
        /// background.** A wall just inside the far plane (the fog 97 per cent; the lit rest is taken out) in views
        /// down and raised, away from the sun: each picture is the picture of the sky alone from that view, within
        /// what a 128-pixel face holds of a smooth sky. What a 128-pixel face does NOT hold is measured and written,
        /// not hidden: the horizon, where the skybox steps from the sky to the ground within less than one of the
        /// cubemap's pixels (views level with the ground, all round), and the sun's small bright disc.
        /// </summary>
        [Test]
        public void TheFogsColour_IsTheSkysInTheDirectionLookedAlong_AndAMeshAtTheFarPlaneMeetsTheBackground()
        {
            InSkyScene((sky, toSun) =>
            {
                Material material = Surface();
                Mesh wall = Wall();
                VpSkyFog.Set(sky);
                var views = new List<(string name, Quaternion rotation, bool sun)>();
                for (int yaw = 0; yaw < 360; yaw += 45) views.Add(("level, yaw " + yaw, Quaternion.Euler(0f, yaw, 0f), false));
                views.Add(("down", Quaternion.Euler(89f, 20f, 0f), false));
                views.Add(("aslant down", Quaternion.Euler(40f, 250f, 0f), false));
                Quaternion away = Quaternion.LookRotation(new Vector3(-toSun.x, 0f, -toSun.z).normalized, Vector3.up);
                for (int turn = -60; turn <= 60; turn += 60)
                {
                    views.Add(("raised 40, " + turn + " from opposite the sun", away * Quaternion.Euler(0f, turn, 0f) * Quaternion.Euler(-40f, 0f, 0f), false));
                }

                views.Add(("at the sun", Quaternion.LookRotation(toSun, Vector3.up), true));

                var lines = new List<string>();
                Color[] first = null;
                float apart = 0f;
                foreach ((string name, Quaternion rotation, bool sun) view in views)
                {
                    Camera camera = Eye(view.rotation);
                    Color[] skyAlone = Picture(camera, material, wall, -1f);
                    VpSkyFog.Set(null, "the comparison");
                    Color[] lit = Picture(camera, material, wall, Far * 0.99f);
                    VpSkyFog.Set(sky);
                    Color[] fogged = Picture(camera, material, wall, Far * 0.99f);
                    Assert.That(Largest(lit, skyAlone, out float litMean), Is.GreaterThan(0.1f), view.name + ": the layout: the wall is drawn, and lit it is not the sky's colour");
                    Assert.That(litMean, Is.GreaterThan(0.1f), view.name + ": the layout: over the whole view");
                    bool sunInView = view.sun || Vector3.Angle(camera.transform.forward, toSun) < 50f;
                    bool horizonInView = Mathf.Abs(Mathf.Asin(camera.transform.forward.y) * Mathf.Rad2Deg) < 32f;

                    // The wall is mostly the sky's colour: the lit rest is taken out before the two are compared,
                    // so that what is measured is the cubemap against the skybox.
                    float nearFar = ShareAt(Far * 0.99f);
                    for (int i = 0; i < fogged.Length; i++) fogged[i] = (fogged[i] - lit[i] * (1f - nearFar)) / nearFar;
                    float largest = Largest(fogged, skyAlone, out float mean);
                    int over = 0;
                    for (int i = 0; i < fogged.Length; i++)
                    {
                        if (Mathf.Max(Mathf.Abs(fogged[i].r - skyAlone[i].r), Mathf.Max(Mathf.Abs(fogged[i].g - skyAlone[i].g), Mathf.Abs(fogged[i].b - skyAlone[i].b))) > 0.05f) over++;
                    }

                    lines.Add(view.name + ": largest " + largest.ToString("0.0000") + ", mean " + mean.ToString("0.0000") + ", pixels over 0.05 apart " + over + " of " + fogged.Length
                        + (sunInView ? " (the sun is in or near the view)" : "") + (horizonInView ? " (the horizon is in the view)" : ""));
                    if (!sunInView && !horizonInView)
                    {
                        Assert.That(mean, Is.LessThan(0.001f), view.name + ": the fogged wall is the sky behind it, on the whole");
                        Assert.That(largest, Is.LessThan(0.01f), view.name + ": and at every pixel");
                    }
                    else if (!sunInView)
                    {
                        // The horizon: in a band about as high as a few of the cubemap's pixels the fog's colour is the
                        // skybox's fast change smoothed; the rest of the view is the sky behind it.
                        Assert.That(mean, Is.LessThan(0.003f), view.name + ": the fogged wall is the sky behind it but for the band at the horizon");
                        Assert.That(largest, Is.LessThan(0.12f), view.name + ": and in that band it is within a tenth of the sky's colour there (about 1.2)");
                        Assert.That(over, Is.LessThan(fogged.Length / 50), view.name + ": and that band is a small part of the view");
                    }
                    else
                    {
                        Assert.That(mean, Is.LessThan(0.005f), view.name + ": the fogged wall is the sky behind it but for the sun's disc");
                    }

                    if (first == null)
                    {
                        first = fogged;
                    }
                    else
                    {
                        apart = Mathf.Max(apart, Largest(fogged, first, out _));
                    }
                }

                foreach (string line in lines) TestContext.Out.WriteLine(line);
                Assert.That(apart, Is.GreaterThan(0.1f), "the views differ: the fog's colour follows the direction, it is not one colour");
            });
        }

        /// <summary>
        /// For the record, not a judgement: what a face of 128, 256 and 512 pixels leaves between the fogged wall and
        /// the skybox behind it, in a picture of 240 pixels across 60 degrees (a quarter of a degree a pixel) -- level
        /// with the ground, where the skybox steps from the sky to the ground at the horizon, and at the sun.
        /// </summary>
        [Test]
        public void WhatAFaceOf128_256And512Leaves_AtTheHorizonAndTheSun_IsMeasuredForTheRecord()
        {
            InSkyScene((bakedForFog, toSun) =>
            {
                Material material = Surface();
                Mesh wall = Wall();
                var views = new (string name, Quaternion rotation)[]
                {
                    ("level, away from the sun", Quaternion.LookRotation(new Vector3(-toSun.x, 0f, -toSun.z).normalized, Vector3.up)),
                    ("at the sun", Quaternion.LookRotation(toSun, Vector3.up)),
                };
                const int wide = 240;
                foreach (int face in new[] { 128, 256, 512 })
                {
                    Cubemap sky = Track(VpSkyFogBake.Bake(face, out string report));
                    Assert.That(sky, Is.Not.Null, report);
                    foreach ((string name, Quaternion rotation) view in views)
                    {
                        Camera camera = Eye(view.rotation);
                        camera.targetTexture = Track(new RenderTexture(wide, wide, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear));
                        Color[] skyAlone = Wide(camera, material, wall, -1f, wide);
                        VpSkyFog.Set(null, "the comparison");
                        Color[] lit = Wide(camera, material, wall, Far * 0.99f, wide);
                        VpSkyFog.Set(sky);
                        Color[] fogged = Wide(camera, material, wall, Far * 0.99f, wide);
                        float nearFar = ShareAt(Far * 0.99f);
                        for (int i = 0; i < fogged.Length; i++) fogged[i] = (fogged[i] - lit[i] * (1f - nearFar)) / nearFar;
                        float largest = Largest(fogged, skyAlone, out float mean);
                        int over5 = 0, over2 = 0, firstRow = wide, lastRow = -1;
                        for (int i = 0; i < fogged.Length; i++)
                        {
                            float d = Mathf.Max(Mathf.Abs(fogged[i].r - skyAlone[i].r), Mathf.Max(Mathf.Abs(fogged[i].g - skyAlone[i].g), Mathf.Abs(fogged[i].b - skyAlone[i].b)));
                            if (d > 0.02f) over2++;
                            if (d > 0.05f)
                            {
                                over5++;
                                firstRow = Mathf.Min(firstRow, i / wide);
                                lastRow = Mathf.Max(lastRow, i / wide);
                            }
                        }

                        TestContext.Out.WriteLine(
                            "face " + face + ", " + view.name + ": largest difference (linear) " + largest.ToString("0.000") + ", mean " + mean.ToString("0.0000")
                            + "; pixels over 0.02 apart " + over2 + ", over 0.05 apart " + over5 + " of " + fogged.Length
                            + (lastRow >= 0 ? "; rows with a pixel over 0.05: " + (lastRow - firstRow + 1) + " (" + ((lastRow - firstRow + 1) * 60f / wide).ToString("0.0#") + " degrees)" : ""));
                    }
                }
            });
        }

        /// <summary>
        /// For the record, not a judgement: the skybox ITSELF, with no mesh and no fog, in the same world directions
        /// seen by cameras turned differently (level; raised 15 and 30 degrees; lowered 15; turned 25 to the side).
        /// Where the colour of one direction is not the same from every view, no cubemap -- one colour a direction --
        /// can be the background in all of them, whatever its size.
        /// </summary>
        [Test]
        public void TheSkyboxItself_InOneDirection_FromViewsTurnedDifferently_IsMeasuredForTheRecord()
        {
            InSkyScene((sky, toSun) =>
            {
                Material material = Surface();
                Mesh wall = Wall();
                const int wide = 240;
                float yaw = Mathf.Atan2(-toSun.x, -toSun.z) * Mathf.Rad2Deg;
                var views = new (string name, Quaternion rotation)[]
                {
                    ("level", Quaternion.Euler(0f, yaw, 0f)),
                    ("raised 15", Quaternion.Euler(-15f, yaw, 0f)),
                    ("raised 30", Quaternion.Euler(-30f, yaw, 0f)),
                    ("lowered 15", Quaternion.Euler(15f, yaw, 0f)),
                    ("turned 25", Quaternion.Euler(0f, yaw + 25f, 0f)),
                };
                var cameras = new Camera[views.Length];
                var pictures = new Color[views.Length][];
                for (int v = 0; v < views.Length; v++)
                {
                    cameras[v] = Eye(views[v].rotation);
                    cameras[v].targetTexture = Track(new RenderTexture(wide, wide, 24, RenderTextureFormat.ARGBHalf, RenderTextureReadWrite.Linear));
                    pictures[v] = Wide(cameras[v], material, wall, -1f, wide);
                }

                foreach (float elevation in new[] { -8f, -3f, -1f, -0.3f, 0.3f, 1f, 2f, 3f, 5f, 8f, 12f })
                {
                    Vector3 direction = Quaternion.Euler(-elevation, yaw, 0f) * Vector3.forward;
                    var line = new System.Text.StringBuilder("elevation " + elevation.ToString("0.0") + ":");
                    float least = float.MaxValue, most = float.MinValue;
                    for (int v = 0; v < views.Length; v++)
                    {
                        Vector3 point = cameras[v].WorldToViewportPoint(cameras[v].transform.position + direction * 10f);
                        if (point.z <= 0f || point.x < 0.02f || point.x > 0.98f || point.y < 0.02f || point.y > 0.98f)
                        {
                            line.Append(" | " + views[v].name + " (not in view)");
                            continue;
                        }

                        Color colour = pictures[v][Mathf.Clamp((int)(point.y * wide), 0, wide - 1) * wide + Mathf.Clamp((int)(point.x * wide), 0, wide - 1)];
                        least = Mathf.Min(least, colour.g);
                        most = Mathf.Max(most, colour.g);
                        line.Append(" | " + views[v].name + " (" + colour.r.ToString("0.000") + ", " + colour.g.ToString("0.000") + ", " + colour.b.ToString("0.000") + ")");
                    }

                    line.Append(" | green apart by " + (most - least).ToString("0.000"));
                    TestContext.Out.WriteLine(line.ToString());
                }
            });
        }

        private Color[] Wide(Camera camera, Material material, Mesh wall, float depth, int size)
        {
            if (depth >= 0f)
            {
                Graphics.RenderMesh(new RenderParams(material) { camera = camera, shadowCastingMode = ShadowCastingMode.Off, receiveShadows = false }, wall, 0, WallAt(camera, depth));
            }

            RenderTexture target = camera.targetTexture;
            RenderPipeline.SubmitRenderRequest(camera, new RenderPipeline.StandardRequest { destination = target });
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = target;
            var readback = Track(new Texture2D(size, size, TextureFormat.RGBAHalf, false, true));
            readback.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            readback.Apply(false);
            RenderTexture.active = previous;
            return readback.GetPixels();
        }

        /// <summary>
        /// **The far plane is each camera's own, and nothing is written for it.** The same wall, 47.5 m off, is deep in
        /// the fog for a camera whose far plane is 50 m and not in it at all for one whose far plane is 200 m, in the
        /// same frame, with the fog's constants written once. Drawing writes none of them again and makes no material.
        /// </summary>
        [Test]
        public void TheFarPlane_IsEachCamerasOwn_AndDrawingWritesNoConstantAndMakesNoMaterial()
        {
            InSkyScene((sky, toSun) =>
            {
                Material material = Surface();
                Mesh wall = Wall();
                Quaternion rotation = AwayFromTheSun(toSun);
                Camera near = Eye(rotation, 50f), wide = Eye(rotation, 200f);
                VpSkyFog.Set(null, "the comparison");
                Color[] nearOff = Picture(near, material, wall, 47.5f), wideOff = Picture(wide, material, wall, 47.5f);
                Color[] skyAlone = Picture(near, material, wall, -1f);
                VpSkyFog.Set(sky);
                int writes = VpSkyFog.Writes, surfaceWrites = VpSurfaceMaterial.Writes;
                int materials = Resources.FindObjectsOfTypeAll<Material>().Length;
                Color[] nearOn = null, wideOn = null;
                for (int frame = 0; frame < 5; frame++)
                {
                    nearOn = Picture(near, material, wall, 47.5f);
                    wideOn = Picture(wide, material, wall, 47.5f);
                }

                Assert.That(ShareAt(47.5f, 50f), Is.EqualTo(5f / 6f).Within(1e-4f), "the layout: 47.5 m of a far plane of 50 m is five sixths of the way from the start");
                Assert.That(ShareAt(47.5f, 200f), Is.Zero, "the layout: and before the start for a far plane of 200 m");
                Assert.That(Largest(nearOn, Lerp(nearOff, skyAlone, ShareAt(47.5f, 50f)), out _), Is.LessThan(0.006f), "far plane 50 m: the wall at 47.5 m is five sixths the sky's colour");
                Assert.That(Largest(wideOn, wideOff, out _), Is.LessThan(1e-3f), "far plane 200 m: the same wall is not in the fog");
                Assert.That(new[] { VpSkyFog.Writes - writes, VpSurfaceMaterial.Writes - surfaceWrites }, Is.EqualTo(new[] { 0, 0 }), "ten pictures by two cameras wrote no constant");
                Assert.That(Resources.FindObjectsOfTypeAll<Material>().Length, Is.EqualTo(materials), "and made no material");
            });
        }

        /// <summary>
        /// **Off is the picture of before.** With no cubemap set the switch is zero: a wall deep in what would be the
        /// fog is the lit wall, whatever cubemap an earlier test left bound. And the reading of the Player's arguments.
        /// </summary>
        [Test]
        public void WithTheFogOff_AFarWallIsTheLitWall_AndTheArgumentTurnsItOff()
        {
            InSkyScene((sky, toSun) =>
            {
                Camera camera = Eye(AwayFromTheSun(toSun));
                Material material = Surface();
                Mesh wall = Wall();
                VpSkyFog.Set(sky);
                Color[] on = Picture(camera, material, wall, 99f);
                VpSkyFog.Set(null, "the comparison");
                Assert.That(new[] { VpSkyFog.Enabled, VpSkyFog.OffReason == "the comparison" }, Is.EqualTo(new[] { false, true }));
                Color[] off = Picture(camera, material, wall, 99f);
                Color[] near = Picture(camera, material, wall, 30f);
                Assert.That(Largest(off, near, out _), Is.LessThan(0.02f), "the wall at 99 m is lit as the wall at 30 m is: no fog");
                Assert.That(Largest(on, off, out _), Is.GreaterThan(0.05f), "the layout: with the fog on it is not");
            });

            Assert.That(VpSkyFog.ReadEnabled(new[] { "player.exe", "-screen-width", "960" }), Is.True);
            Assert.That(VpSkyFog.ReadEnabled(new[] { "player.exe", VpSkyFog.OffArgument }), Is.False);
            Assert.That(VpSkyFog.ReadEnabled(null), Is.True);
        }

        /// <summary>
        /// **Only the ordinary mesh's forward pass is fogged.** The fog is one include: of every shader and shader
        /// include of the product, "Zantetsu/VP Mesh Surface" alone names it, once, in its forward pass -- not the
        /// shared lighting the cut display's bodies and caps are lit by, not a shadow caster, not a stencil pass.
        /// </summary>
        [Test]
        public void OnlyTheOrdinaryMeshsForwardPass_NamesTheFog_NotTheSharedLighting_NorAnyOtherShader()
        {
            string folder = Path.Combine(Application.dataPath, "Zantetsu", "Runtime");
            var naming = new List<string>();
            foreach (string file in Directory.GetFiles(folder, "*.*", SearchOption.AllDirectories))
            {
                string extension = Path.GetExtension(file);
                if (extension != ".shader" && extension != ".hlsl" && extension != ".cginc" && extension != ".compute") continue;
                string name = Path.GetFileName(file);
                if (name == "VpSkyFog.hlsl") continue;
                string text = File.ReadAllText(file);
                if (text.Contains("VpSkyFog.hlsl") || text.Contains("VpApplySkyFog") || text.Contains("_VpSkyFogCube")) naming.Add(name);
            }

            Assert.That(naming, Is.EqualTo(new[] { "VpMeshSurface.shader" }), "the files that name the fog");
            string surface = File.ReadAllText(Path.Combine(folder, "Rendering", "VpMeshSurface.shader"));
            int forward = surface.IndexOf("Name \"ForwardLit\"", System.StringComparison.Ordinal);
            int shadow = surface.IndexOf("Name \"ShadowCaster\"", System.StringComparison.Ordinal);
            int include = surface.IndexOf("#include \"VpSkyFog.hlsl\"", System.StringComparison.Ordinal);
            Assert.That(forward >= 0 && shadow > forward && include > forward && include < shadow, Is.True, "included in the forward pass");
            Assert.That(surface.IndexOf("VpSkyFog", shadow, System.StringComparison.Ordinal), Is.EqualTo(-1), "and nowhere after it: the shadow and motion passes have no fog");
            string lighting = File.ReadAllText(Path.Combine(folder, "Rendering", "VpCutSurfaceShading.hlsl"));
            Assert.That(lighting.Contains("SkyFog"), Is.False, "the shared lighting has no fog");
        }
    }
}
