using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Object = UnityEngine.Object;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The cut display's lighting on the product's own path (DESIGN 5.3) against what it is meant to equal: URP/Lit with
    /// the values of the city's imported <c>Color</c> material. A world made by <see cref="CutWorldRoot"/> and drawn by
    /// <see cref="CutWorldCameraDrawing"/> goes through three stages -- the whole body; the cut while its geometry is
    /// held back, when the sides are clipped and the cut faces are the temporary caps the stencil draws; the committed
    /// children, whose cut faces are real caps in their geometry -- once selecting on the GPU (VP Stage 3C) and once as
    /// VP Stage 3.
    /// <para>
    /// **The reference stands in the same place.** Beside the world there are ordinary MeshRenderers with URP/Lit, of
    /// the same shapes at the same places -- the box, then its two halves with their cut faces -- on a layer of their
    /// own, and a second camera with the first one's pose that sees only them. The world's display is drawn for the
    /// first camera alone. Both cameras render every frame in the same light, so each pair of images is of one frame.
    /// </para>
    /// <para>
    /// **Each stage is taken four ways**: from above, where the lower half's cut face looks up at the view, and from
    /// below, where the upper half's looks down at it; in the light, and fully in shadow under a large caster. The
    /// light is the test's own: a directional light with soft shadows, an ambient light of three colours and a
    /// reflection cubemap whose faces differ, put on the scene for the case and taken off again.
    /// </para>
    /// <para>
    /// The images are compared whole and at the middle of the two cut faces. The whole comparison allows the pixels of
    /// the edges -- a cap the stencil fills and a clipped side are not rasterised as a closed mesh is, and the clipped
    /// sides cast as open shells -- and the bound on them is loose on purpose; the middles of the faces are exact.
    /// </para>
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const int LightingImageSize = 192;
        private const int LightingStandInLayer = 30;
        private static readonly string[] s_lightingStages = { "whole", "immediate (temporary caps)", "committed (real caps)" };
        private static readonly string[] s_lightingViews = { "from above, in the light", "from below, in the light", "from above, fully in shadow", "from below, fully in shadow" };

        [UnityTest]
        public IEnumerator SurfaceLighting_TheGpuSelectionsDisplay_IsLitAsUrpLit_BeforeTheCutWhileImmediateAndCommitted()
        {
            yield return SurfaceLightingScenes(true);
        }

        [UnityTest]
        public IEnumerator SurfaceLighting_VpStage3sDisplay_IsLitAsUrpLit_BeforeTheCutWhileImmediateAndCommitted()
        {
            yield return SurfaceLightingScenes(false);
        }

        private sealed class LightingStage
        {
            internal Camera camera, reference;
            internal RenderTexture target, referenceTarget;
            internal GameObject occluder;
            internal GameObject whole, lower, upper;
        }

        private struct LightingDifference
        {
            internal int drawn, over, worst;

            public override string ToString()
            {
                return "pixels drawn " + drawn + ", differing by more than 2 in a channel " + over + ", the largest difference " + worst;
            }
        }

        private IEnumerator SurfaceLightingScenes(bool gpu)
        {
            bool requestedBefore = VpGpuCullSetup.Requested;
            VpCutSurfaceColour.State coloursBefore = VpCutSurfaceColour.Capture();
            AmbientMode modeBefore = RenderSettings.ambientMode;
            Color skyBefore = RenderSettings.ambientSkyColor, equatorBefore = RenderSettings.ambientEquatorColor, groundBefore = RenderSettings.ambientGroundColor;
            float ambientIntensityBefore = RenderSettings.ambientIntensity, reflectionIntensityBefore = RenderSettings.reflectionIntensity;
            DefaultReflectionMode reflectionModeBefore = RenderSettings.defaultReflectionMode;
            Texture reflectionBefore = RenderSettings.customReflectionTexture;
            VpGpuCullSetup.Requested = gpu;
            try
            {
                VpCutSurfaceColour.SetDebugEnabled(false);
                SurfaceLightingEnvironment();

                Shader casterShader = Shader.Find("Zantetsu/VP Indexed Indirect Shadow Caster");
                Assert.That(casterShader, Is.Not.Null, "the display's shadow caster is in this project");
                Material stable = Track(new Material(casterShader) { name = "stable caster" });
                stable.SetFloat("_Cull", (float)CullMode.Back);
                Material immediate = Track(new Material(casterShader) { name = "immediate caster" });
                immediate.SetFloat("_Cull", (float)CullMode.Off);

                HoldingExecutor geometryPool = null;
                CutWorldRoot root = NewWorld(
                    out Shader _,
                    destination => destination == WorkDestination.GeometryPool
                        ? geometryPool = Held(new HoldingExecutor(WorkerPoolExecutor.GeometryPool(2)))
                        : null,
                    null,
                    null,
                    world =>
                    {
                        SetPrivate(world, "shadowMaterial", stable);
                        SetPrivate(world, "provisionalShadowMaterial", immediate);
                    });
                Assert.That(geometryPool, Is.Not.Null);
                geometryPool.HoldEverything = true;
                root.Driver.RemainingMainSeconds = () => 1.0;
                Assert.That(root.GpuCullFallback, Is.Null, "the world did not fall back");
                Assert.That(root.Display.CullsOnGpu, Is.EqualTo(gpu), "the world draws by the route this case asked for");

                LightingStage stage = NewLightingStage();
                var drawing = root.gameObject.AddComponent<CutWorldCameraDrawing>();
                SetPrivate(drawing, "world", root);
                SetPrivate(drawing, "cameras", new[] { stage.camera });

                var centre = new Vector3(0f, 4f, 0f);
                var carried = new Vector3(3f, 4f, 0f);
                LogicalFragmentId body = AddBody(root, centre);
                Assert.That(root.Owners.TryGet(body, out PhysicsFragmentOwner wholeOwner), Is.True);
                wholeOwner.Body.isKinematic = true;
                yield return null;
                yield return null;

                var lines = new List<string>();
                var failures = new List<string>();
                var images = new List<Color32[]>();
                stage.whole.SetActive(true);
                yield return SurfaceLightingTakeStage(stage, 0, lines, failures, images, centre, carried);

                // The cut, with its geometry held back: the sides are clipped and the cut faces are temporary caps.
                Assert.That(root.TryAsk(Ask(body, new float4(0f, 1f, 0f, 0f))), Is.True);
                CutOperationId operation = default;
                yield return Until(
                    () =>
                    {
                        foreach (ProvisionalCutTransaction candidate in root.Driver.Transactions)
                        {
                            operation = candidate.Operation;
                        }

                        return operation.IsSet && geometryPool.HoldingCount > 0
                            && root.Ledger.TryGetOperation(operation, out LogicalCutOperation published)
                            && published.positive.IsSet;
                    },
                    "the children are published while the cut's geometry is held back");
                Assert.That(root.Ledger.TryGetOperation(operation, out LogicalCutOperation record), Is.True);
                Assert.That(root.Owners.TryGet(record.positive, out PhysicsFragmentOwner upper), Is.True);
                Assert.That(root.Owners.TryGet(record.negative, out PhysicsFragmentOwner lower), Is.True);
                upper.Body.isKinematic = true;
                lower.Body.isKinematic = true;
                lower.Root.transform.SetPositionAndRotation(centre, Quaternion.identity);
                upper.Root.transform.SetPositionAndRotation(carried, Quaternion.identity);
                stage.whole.SetActive(false);
                stage.lower.SetActive(true);
                stage.upper.SetActive(true);
                yield return null;
                yield return null;
                yield return SurfaceLightingTakeStage(stage, 1, lines, failures, images, centre, carried);
                Assert.That(
                    root.Ledger.TryGetOperation(operation, out LogicalCutOperation stillOut)
                    && stillOut.state != LogicalCutOperationState.Completed, Is.True, "still immediate: not committed");
                Assert.That(root.Display.CapRecordCount, Is.GreaterThan(0), "the cut faces are caps the stencil work draws");

                // The geometry commits: the cut faces are real caps of the children's own geometry.
                geometryPool.HoldEverything = false;
                geometryPool.ReleaseEverything();
                yield return Until(
                    () => root.Ledger.TryGetOperation(operation, out LogicalCutOperation now)
                        && now.state == LogicalCutOperationState.Completed,
                    "the cut's geometry committed");
                yield return null;
                yield return null;
                yield return SurfaceLightingTakeStage(stage, 2, lines, failures, images, centre, carried);
                Assert.That(root.Display.CapRecordCount, Is.Zero, "after the commit no cap is the stencil work's");

                // Nothing of the lighting changes when the geometry commits: the immediate images against the committed ones.
                for (int view = 0; view < s_lightingViews.Length; view++)
                {
                    LightingDifference commit = LightingCompare(images[s_lightingViews.Length + view], images[2 * s_lightingViews.Length + view]);
                    lines.Add("immediate against committed, " + s_lightingViews[view] + ": " + commit);
                    if (commit.over >= commit.drawn / 25) failures.Add("the commit changes the image, " + s_lightingViews[view] + ": " + commit);
                }

                TestContext.Out.WriteLine((gpu ? "GPU selection (VP Stage 3C)" : "VP Stage 3") + ":\n" + string.Join("\n", lines));
                Assert.That(root.Display.IsHalted, Is.False);
                Assert.That(root.TerminationRequested, Is.False);
                string failed = string.Join("; ", failures);

                stage.camera.enabled = false;
                stage.reference.enabled = false;
                yield return null;
                Object.Destroy(drawing);
                yield return null;
                yield return EndWorld(root);
                Assert.That(failures, Is.Empty, failed);
            }
            finally
            {
                VpGpuCullSetup.Requested = requestedBefore;
                VpCutSurfaceColour.Restore(coloursBefore);
                RenderSettings.ambientMode = modeBefore;
                RenderSettings.ambientSkyColor = skyBefore;
                RenderSettings.ambientEquatorColor = equatorBefore;
                RenderSettings.ambientGroundColor = groundBefore;
                RenderSettings.ambientIntensity = ambientIntensityBefore;
                RenderSettings.reflectionIntensity = reflectionIntensityBefore;
                RenderSettings.defaultReflectionMode = reflectionModeBefore;
                RenderSettings.customReflectionTexture = reflectionBefore;
                DynamicGI.UpdateEnvironment();
            }
        }

        /// <summary>The case's own ambient light and default reflection: three colours, and a cubemap whose faces differ.</summary>
        private void SurfaceLightingEnvironment()
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
            RenderSettings.ambientMode = AmbientMode.Trilight;
            RenderSettings.ambientSkyColor = new Color(0.35f, 0.55f, 1f);
            RenderSettings.ambientEquatorColor = new Color(0.45f, 0.45f, 0.45f);
            RenderSettings.ambientGroundColor = new Color(0.6f, 0.38f, 0.15f);
            RenderSettings.ambientIntensity = 1f;
            RenderSettings.defaultReflectionMode = DefaultReflectionMode.Custom;
            RenderSettings.customReflectionTexture = reflection;
            RenderSettings.reflectionIntensity = 1f;
            DynamicGI.UpdateEnvironment();
        }

        private LightingStage NewLightingStage()
        {
            Light light = TrackActor(new GameObject("Lighting Stage Sun")).AddComponent<Light>();
            light.type = LightType.Directional;
            light.shadows = LightShadows.Soft;
            light.intensity = 1f;
            light.color = new Color(1f, 0.96f, 0.9f);
            light.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var stage = new LightingStage();
            stage.target = Track(new RenderTexture(LightingImageSize, LightingImageSize, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 });
            stage.target.Create();
            stage.referenceTarget = Track(new RenderTexture(LightingImageSize, LightingImageSize, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 });
            stage.referenceTarget.Create();
            stage.camera = LightingCamera("Lighting Stage Camera", stage.target, ~(1 << LightingStandInLayer));
            stage.reference = LightingCamera("Lighting Stage Reference Camera", stage.referenceTarget, (1 << LightingStandInLayer) | 1);

            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            Assert.That(lit, Is.Not.Null);
            Assert.That(QualitySettings.activeColorSpace, Is.EqualTo(ColorSpace.Linear), "the project renders in linear space");
            Material surface = LitWithTheColorMaterialsValues(lit, "stand-in surface", Color.white);

            // The cut surface colour reaches the display's shaders as the numbers it was named with (a global constant is
            // not converted, and the temporary cap's material undoes its conversion to match: VpStencilCapBatch), so
            // those numbers are the base colour as the shader has it.
            Material cap = LitWithTheColorMaterialsValues(lit, "stand-in cut face", VpCutSurfaceColour.Current);

            // The caster of the full shadow: between the light and the bodies, seen by both cameras' shadows, never drawn.
            stage.occluder = TrackActor(GameObject.CreatePrimitive(PrimitiveType.Cube));
            Object.Destroy(stage.occluder.GetComponent<Collider>());
            stage.occluder.transform.position = new Vector3(1.5f, 4f, 0f) - light.transform.forward * 9f;
            stage.occluder.transform.rotation = Quaternion.FromToRotation(Vector3.up, -light.transform.forward);
            stage.occluder.transform.localScale = new Vector3(16f, 0.05f, 16f);
            MeshRenderer occluderRenderer = stage.occluder.GetComponent<MeshRenderer>();
            occluderRenderer.sharedMaterial = surface;
            occluderRenderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            stage.occluder.SetActive(false);

            // The body is the fixture's box, 2 m a side about its own origin, cut through that origin by y = 0.
            stage.whole = LightingStandIn("Stand-in whole", new Vector3(-1f, -1f, -1f), new Vector3(1f, 1f, 1f), 0, surface, cap);
            stage.lower = LightingStandIn("Stand-in lower half", new Vector3(-1f, -1f, -1f), new Vector3(1f, 0f, 1f), 1, surface, cap);
            stage.upper = LightingStandIn("Stand-in upper half", new Vector3(-1f, 0f, -1f), new Vector3(1f, 1f, 1f), -1, surface, cap);
            return stage;
        }

        private Camera LightingCamera(string name, RenderTexture target, int mask)
        {
            Camera camera = TrackActor(new GameObject(name)).AddComponent<Camera>();
            camera.fieldOfView = 50f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 60f;
            camera.cullingMask = mask;
            camera.targetTexture = target;
            camera.enabled = true;
            return camera;
        }

        /// <summary>
        /// URP/Lit as the city's Color material is imported, its Base Color (0.8, 0.8, 0.8) multiplied into the given
        /// base colour, which is given as a shader has it (linear).
        /// </summary>
        private Material LitWithTheColorMaterialsValues(Shader lit, string name, Color baseColour)
        {
            Material material = Track(new Material(lit) { name = name });
            Color product = baseColour * new Color(0.8f, 0.8f, 0.8f, 1f).linear;
            product.a = 1f;
            material.SetColor("_BaseColor", product.gamma);
            material.SetFloat("_Metallic", 0f);
            material.SetFloat("_Smoothness", 0.5f);
            material.SetFloat("_WorkflowMode", 1f);
            material.SetFloat("_Surface", 0f);
            material.SetFloat("_ReceiveShadows", 1f);
            material.SetFloat("_SpecularHighlights", 1f);
            material.SetFloat("_EnvironmentReflections", 1f);
            return material;
        }

        /// <summary>
        /// A closed box between two corners, each face with its own normal, as an ordinary renderer on the stand-ins'
        /// layer. <paramref name="capSide"/> names the face that is the cut's: +1 the top, -1 the bottom, 0 none.
        /// </summary>
        private GameObject LightingStandIn(string name, Vector3 min, Vector3 max, int capSide, Material surface, Material cap)
        {
            var corners = new[]
            {
                new Vector3(min.x, min.y, min.z), new Vector3(max.x, min.y, min.z), new Vector3(max.x, min.y, max.z), new Vector3(min.x, min.y, max.z),
                new Vector3(min.x, max.y, min.z), new Vector3(max.x, max.y, min.z), new Vector3(max.x, max.y, max.z), new Vector3(min.x, max.y, max.z),
            };
            var vertices = new List<Vector3>();
            var normals = new List<Vector3>();
            var surfaceIndices = new List<int>();
            var capIndices = new List<int>();
            for (int face = 0; face < k_faces.Length; face++)
            {
                int[] c = k_faces[face].cycle;
                Vector3 normal = Vector3.Cross(corners[c[1]] - corners[c[0]], corners[c[2]] - corners[c[0]]).normalized;
                bool isCap = (capSide > 0 && normal.y > 0.5f) || (capSide < 0 && normal.y < -0.5f);
                int b = vertices.Count;
                for (int k = 0; k < 4; k++)
                {
                    vertices.Add(corners[c[k]]);
                    normals.Add(normal);
                }

                (isCap ? capIndices : surfaceIndices).AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }

            var mesh = Track(new Mesh { name = name });
            mesh.SetVertices(vertices);
            mesh.SetNormals(normals);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(surfaceIndices, 0);
            mesh.SetTriangles(capIndices, 1);
            mesh.RecalculateBounds();

            GameObject standIn = TrackActor(new GameObject(name));
            standIn.layer = LightingStandInLayer;
            standIn.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer renderer = standIn.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new[] { surface, cap };
            renderer.shadowCastingMode = ShadowCastingMode.On;
            renderer.receiveShadows = true;
            standIn.SetActive(false);
            return standIn;
        }

        private static LightingDifference LightingCompare(Color32[] a, Color32[] b)
        {
            var difference = default(LightingDifference);
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i].r > 8 || a[i].g > 8 || a[i].b > 8 || b[i].r > 8 || b[i].g > 8 || b[i].b > 8) difference.drawn++;
                int d = Math.Max(Math.Abs(a[i].r - b[i].r), Math.Max(Math.Abs(a[i].g - b[i].g), Math.Abs(a[i].b - b[i].b)));
                difference.worst = Math.Max(difference.worst, d);
                if (d > 2) difference.over++;
            }

            return difference;
        }

        private static Color32 LightingAt(Camera camera, Color32[] image, Vector3 world)
        {
            Vector3 viewport = camera.WorldToViewportPoint(world);
            int x = Mathf.Clamp(Mathf.RoundToInt(viewport.x * LightingImageSize - 0.5f), 0, LightingImageSize - 1);
            int y = Mathf.Clamp(Mathf.RoundToInt(viewport.y * LightingImageSize - 0.5f), 0, LightingImageSize - 1);
            return image[y * LightingImageSize + x];
        }

        private static int LightingChannelDifference(Color32 a, Color32 b)
        {
            return Math.Max(Math.Abs(a.r - b.r), Math.Max(Math.Abs(a.g - b.g), Math.Abs(a.b - b.b)));
        }

        // One stage, four ways. The world's image is kept for the comparison across the commit.
        private IEnumerator SurfaceLightingTakeStage(
            LightingStage stage, int stageIndex, List<string> lines, List<string> failures, List<Color32[]> images, Vector3 centre, Vector3 carried)
        {
            stage.whole.transform.position = centre;
            stage.lower.transform.position = centre;
            stage.upper.transform.position = carried;
            for (int view = 0; view < s_lightingViews.Length; view++)
            {
                bool above = view % 2 == 0;
                var eye = new Vector3(1.5f, above ? 8.5f : -0.5f, -7.5f);
                var target = new Vector3(1.5f, 4f, 0f);
                stage.camera.transform.position = eye;
                stage.camera.transform.LookAt(target);
                stage.reference.transform.SetPositionAndRotation(stage.camera.transform.position, stage.camera.transform.rotation);
                stage.occluder.SetActive(view >= 2);
                yield return null;
                yield return null;
                yield return null;
                Color32[] vp = ReadGpuCullTarget(stage.target);
                Color32[] lit = ReadGpuCullTarget(stage.referenceTarget);
                images.Add(vp);
                string label = s_lightingStages[stageIndex] + ", " + s_lightingViews[view];
                LightingDifference difference = LightingCompare(vp, lit);
                string line = label + ": " + difference;
                Assert.That(difference.drawn, Is.GreaterThan(1500), label + ": the view shows the bodies");

                // Face middles, a little inside each face: the top of the lower body (the cut face once it is cut), its
                // front, and the underside of the upper body (the other cut face) or of the whole one.
                Vector3 top = centre + new Vector3(0.1f, stageIndex == 0 ? 1f : 0f, -0.1f);
                Vector3 front = centre + new Vector3(0.1f, stageIndex == 0 ? 0.1f : -0.5f, -1f);
                Vector3 under = stageIndex == 0 ? centre + new Vector3(0.1f, -1f, -0.1f) : carried + new Vector3(0.1f, 0f, -0.1f);
                Vector3 facing = above ? top : under;
                Color32 vpFace = LightingAt(stage.camera, vp, facing), litFace = LightingAt(stage.camera, lit, facing);
                Color32 vpFront = LightingAt(stage.camera, vp, front), litFront = LightingAt(stage.camera, lit, front);
                string faceName = stageIndex == 0 ? (above ? "top" : "underside") : (above ? "cut face looking up" : "cut face looking down");
                line += "; " + faceName + " VP " + vpFace + " URP/Lit " + litFace + "; front VP " + vpFront + " URP/Lit " + litFront;
                lines.Add(line);

                // Every view of every stage is taken before anything is judged, so that one difference does not hide the rest.
                if (Math.Max(vpFace.r, Math.Max(vpFace.g, vpFace.b)) <= 20) failures.Add(label + ": the " + faceName + " is black: " + vpFace);
                if (LightingChannelDifference(vpFace, litFace) > 2) failures.Add(label + ": the " + faceName + " differs: VP " + vpFace + " URP/Lit " + litFace);
                if (LightingChannelDifference(vpFront, litFront) > 2) failures.Add(label + ": the front differs: VP " + vpFront + " URP/Lit " + litFront);
                if (difference.over >= difference.drawn / 25) failures.Add(label + ": " + difference);
            }

            stage.occluder.SetActive(false);
        }
    }
}
