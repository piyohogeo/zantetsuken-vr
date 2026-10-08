using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The scene draw comparison (TL, 2026-10-07) on a world of the product's own making: placed instances drawn by
    /// their MeshRenderers ("Zantetsu/VP Mesh Surface") are read, and the cut world's display draws the same things in
    /// their place -- one body a renderer, one command a submesh, nothing given to the physics -- and the picture is
    /// the same; what the display cannot draw as the renderer does is refused with its reason and left as it was; and
    /// the run keeps its preparation apart from what it measures.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private sealed class DrawCompareScene
        {
            internal CutWorldRoot root;
            internal Material display, surface;
            internal Texture2D ramp;
            internal List<KeyValuePair<string, Renderer[]>> instances = new List<KeyValuePair<string, Renderer[]>>();
            internal MeshRenderer body, child, turned;
        }

        // The UnifiedLook case's world: a display material of source index 0 with a ramp for its base map, the two
        // casters, and a mesh surface material of the same map and colour for the renderers.
        private DrawCompareScene NewDrawCompareScene(System.Action<CutWorldProfile> more = null)
        {
            var scene = new DrawCompareScene();
            scene.ramp = Track(new Texture2D(256, 256, TextureFormat.RGBA32, false) { filterMode = FilterMode.Bilinear, name = "ramp" });
            var pixels = new Color32[256 * 256];
            for (int v = 0; v < 256; v++) for (int u = 0; u < 256; u++) pixels[v * 256 + u] = new Color32((byte)u, (byte)v, (byte)((u + v) / 2), 255);
            scene.ramp.SetPixels32(pixels);
            scene.ramp.Apply();
            scene.root = NewHullWorld(0.9f, more, 0.3f, r =>
            {
                var field = typeof(CutWorldRoot).GetField("materials", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                var bound = (CutWorldRoot.MaterialBinding[])field.GetValue(r);
                scene.display = Track(new Material(Shader.Find("Zantetsu/VP Indexed Indirect Unlit")) { name = "display" });
                scene.display.SetTexture("_BaseMap", scene.ramp);
                scene.display.SetColor("_BaseColor", Color.white);
                scene.display.SetFloat("_VpUsePaletteAtlas", 0f);
                field.SetValue(r, bound.Concat(new[] { new CutWorldRoot.MaterialBinding { sourceIndex = 0, material = scene.display } }).ToArray());
                Shader caster = Shader.Find("Zantetsu/VP Indexed Indirect Shadow Caster");
                Material stable = Track(new Material(caster) { name = "stable caster" });
                stable.SetFloat("_Cull", (float)CullMode.Back);
                Material immediate = Track(new Material(caster) { name = "immediate caster" });
                immediate.SetFloat("_Cull", (float)CullMode.Off);
                SetPrivate(r, "shadowMaterial", stable);
                SetPrivate(r, "provisionalShadowMaterial", immediate);
            });
            scene.surface = Track(new Material(Shader.Find(SceneDrawInventory.MeshSurfaceShader)) { name = "mesh surface" });
            scene.surface.SetTexture("_BaseMap", scene.ramp);
            scene.surface.SetColor("_BaseColor", scene.display.GetColor("_BaseColor"));
            scene.surface.SetFloat("_VpUsePaletteAtlas", 0f);

            // One instance of a body with a collider and a body of its own and a child renderer at twice the size;
            // another, turned, of the same mesh.
            Mesh box = Track(DrawCompareBox("box", new Vector3(1.2f, 0.8f, 1f)));
            Mesh small = Track(DrawCompareBox("small", new Vector3(0.3f, 0.3f, 0.3f)));
            GameObject first = TrackActor(new GameObject("placed first"));
            first.transform.position = new Vector3(-0.6f, 0.41f, 0f);
            scene.body = DrawCompareRenderer(first, box, scene.surface);
            first.AddComponent<BoxCollider>();
            first.AddComponent<Rigidbody>().isKinematic = true;
            var childObject = new GameObject("part");
            childObject.transform.SetParent(first.transform, false);
            childObject.transform.localPosition = new Vector3(0f, 0.7f, 0f);
            childObject.transform.localScale = new Vector3(2f, 2f, 2f);
            scene.child = DrawCompareRenderer(childObject, small, scene.surface);
            GameObject second = TrackActor(new GameObject("placed second"));
            second.transform.SetPositionAndRotation(new Vector3(0.9f, 0.41f, 0.3f), Quaternion.Euler(0f, 35f, 0f));
            scene.turned = DrawCompareRenderer(second, box, scene.surface);
            second.AddComponent<BoxCollider>();
            scene.instances.Add(new KeyValuePair<string, Renderer[]>("first", first.GetComponentsInChildren<Renderer>(true)));
            scene.instances.Add(new KeyValuePair<string, Renderer[]>("second", second.GetComponentsInChildren<Renderer>(true)));
            return scene;
        }

        private static MeshRenderer DrawCompareRenderer(GameObject on, Mesh mesh, Material surface)
        {
            on.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = on.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = Enumerable.Repeat(surface, mesh.subMeshCount).ToArray();
            return renderer;
        }

        // A box whose top is a submesh of its own: 24 vertices, two submeshes (30 and 6 indices), UVs inside 0..1.
        private static Mesh DrawCompareBox(string name, Vector3 size)
        {
            Vector3 h = size * 0.5f;
            Vector3[] normals = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
            var vertices = new Vector3[24];
            var vertexNormals = new Vector3[24];
            var uvs = new Vector2[24];
            var top = new int[6];
            var sides = new int[30];
            int side = 0;
            for (int f = 0; f < 6; f++)
            {
                Vector3 n = normals[f];
                Vector3 a = Mathf.Abs(n.y) > 0.5f ? Vector3.right : Vector3.up;
                Vector3 b = Vector3.Cross(n, a);
                for (int c = 0; c < 4; c++)
                {
                    float sa = c == 0 || c == 3 ? -1f : 1f, sb = c < 2 ? -1f : 1f;
                    vertices[f * 4 + c] = Vector3.Scale(n + a * sa + b * sb, h);
                    vertexNormals[f * 4 + c] = n;
                    uvs[f * 4 + c] = new Vector2(0.1f + 0.14f * f, 0.2f + 0.15f * c);
                }

                int[] quad = { f * 4, f * 4 + 1, f * 4 + 2, f * 4, f * 4 + 2, f * 4 + 3 };
                if (Vector3.Dot(Vector3.Cross(vertices[quad[1]] - vertices[quad[0]], vertices[quad[2]] - vertices[quad[0]]), n) < 0f)
                {
                    (quad[1], quad[2]) = (quad[2], quad[1]);
                    (quad[4], quad[5]) = (quad[5], quad[4]);
                }

                if (f == 0) quad.CopyTo(top, 0);
                else { quad.CopyTo(sides, side); side += 6; }
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(vertexNormals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(sides, 0);
            mesh.SetTriangles(top, 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        [UnityTest]
        public IEnumerator DrawCompare_TheDisplayDrawsTheTargetsInPlaceOfTheirRenderers_OneCommandASubmesh_NothingOfThePhysicsTouched()
        {
            DrawCompareScene scene = NewDrawCompareScene();
            CutWorldRoot root = scene.root;
            yield return null;

            // What the display cannot draw as the renderer does: each refused with its reason, none left out silently.
            Mesh box = scene.body.GetComponent<MeshFilter>().sharedMesh;
            GameObject lit = TrackActor(new GameObject("another shading"));
            MeshRenderer litRenderer = DrawCompareRenderer(lit, box, Track(new Material(Shader.Find("Universal Render Pipeline/Lit")) { name = "lit" }));
            GameObject noShadow = TrackActor(new GameObject("casts nothing"));
            MeshRenderer noShadowRenderer = DrawCompareRenderer(noShadow, box, scene.surface);
            noShadowRenderer.shadowCastingMode = ShadowCastingMode.Off;
            GameObject oneSlot = TrackActor(new GameObject("one material for two submeshes"));
            MeshRenderer oneSlotRenderer = DrawCompareRenderer(oneSlot, box, scene.surface);
            oneSlotRenderer.sharedMaterials = new[] { scene.surface };
            GameObject mirrored = TrackActor(new GameObject("mirrored"));
            mirrored.transform.localScale = new Vector3(-1f, 1f, 1f);
            MeshRenderer mirroredRenderer = DrawCompareRenderer(mirrored, box, scene.surface);
            GameObject lodded = TrackActor(new GameObject("under a LOD group"));
            lodded.AddComponent<LODGroup>();
            MeshRenderer loddedRenderer = DrawCompareRenderer(lodded, box, scene.surface);
            GameObject off = TrackActor(new GameObject("switched off"));
            MeshRenderer offRenderer = DrawCompareRenderer(off, box, scene.surface);
            offRenderer.enabled = false;
            var refusedOnes = new[] { litRenderer, noShadowRenderer, oneSlotRenderer, mirroredRenderer, loddedRenderer };
            scene.instances.Add(new KeyValuePair<string, Renderer[]>("refused", refusedOnes.Cast<Renderer>().Concat(new Renderer[] { offRenderer, scene.body, null }).ToArray()));

            int bodies = Bodies();
            string collidersBefore = string.Join(",", UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID).Select(c => c.GetInstanceID() + ":" + c.enabled));
            var bank = new SceneDrawMeshBank();
            SceneDrawInventory inventory = SceneDrawInventory.Collect(scene.instances, root.MaterialBindings, bank);
            TestContext.Out.WriteLine(inventory.Describe());
            foreach (SceneDrawRefusal refusal in inventory.Refusals) TestContext.Out.WriteLine("refused: " + refusal);
            Assert.That(inventory.Instances, Is.EqualTo(3));
            Assert.That(inventory.Targets.Count, Is.EqualTo(3), "the body, its part and the turned one");
            Assert.That(inventory.Commands, Is.EqualTo(6), "one a submesh");
            Assert.That(inventory.Vertices, Is.EqualTo(72));
            Assert.That(inventory.Indices, Is.EqualTo(108));
            Assert.That(inventory.DistinctMeshes, Is.EqualTo(2));
            Assert.That(inventory.Scaled, Is.EqualTo(1), "the part at twice its size");
            Assert.That(inventory.NotDrawn, Is.EqualTo(1), "switched off: Unity does not draw it either");
            Assert.That(inventory.ListedAgain, Is.EqualTo(1), "a renderer two instances list is taken once");
            Assert.That(inventory.Missing, Is.EqualTo(1));
            Assert.That(inventory.Refusals.Count, Is.EqualTo(5));
            Assert.That(inventory.Refusals.Select(r => r.reason).ToArray(), Has.Some.Contains("not by 'Zantetsu/VP Mesh Surface'").And.Some.Contains("casts shadows as Off")
                .And.Some.Contains("1 materials for 2 submeshes").And.Some.Contains("mirrored or flat").And.Some.Contains("under a LOD group"));
            Assert.That(inventory.Targets.All(t => t.sourceIndices.All(i => i == 0)), Is.True, "the mesh surface answers to the display's source index 0");
            Assert.That(SceneDrawVpDisplay.CheckRoom(root, inventory, out string room), Is.True, room);
            TestContext.Out.WriteLine("room: " + room);

            // Reading changed nothing.
            Assert.That(scene.body.enabled && scene.child.enabled && scene.turned.enabled, Is.True);
            Assert.That(root.Display.ShownCount, Is.Zero);

            var display = new SceneDrawVpDisplay(root);
            foreach (SceneDrawTarget target in inventory.Targets)
            {
                Assert.That(display.TryShow(target, out string why), Is.True, why);
                Assert.That(target.renderer.enabled, Is.False, "its renderer goes once the display has it");
                Assert.That(display.TryShow(target, out why), Is.False, "and it is not shown twice");
            }

            for (int i = 0; i < 4; i++) yield return null;
            Assert.That(display.ShownCount, Is.EqualTo(3));
            Assert.That(root.Display.ShownCount, Is.EqualTo(3), "one body a renderer");
            Assert.That(root.Display.CommandCount, Is.EqualTo(6), "one command a submesh");
            for (int c = 0; c < root.Display.CommandCount; c++)
            {
                Assert.That(root.Display.TryGetDrawCommand(c, out VpIndirectCommand command), Is.True);
                Assert.That(command.instanceCount, Is.EqualTo(1), "one instance a command: nothing is instanced");
            }

            Assert.That(root.Display.DrawInstanceCount, Is.EqualTo(6));
            foreach (SceneDrawTarget target in inventory.Targets)
            {
                Assert.That(root.Display.TryGetDrawSlots(target.fragment, out int _, out int commands, out int _, out int _, out int _), Is.True, target.path + " is drawn");
                Assert.That(commands, Is.EqualTo(2));
            }

            Assert.That(display.PlacementAnswers, Is.GreaterThan(0), "the display asked where they stand and was answered");

            // Nothing of the physics: no owner, no transaction, no body made, every collider as it was.
            Assert.That(root.Owners.Count, Is.Zero);
            Assert.That(root.Driver.Transactions.Count, Is.Zero);
            Assert.That(Bodies(), Is.EqualTo(bodies));
            Assert.That(string.Join(",", UnityEngine.Object.FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.InstanceID).Select(c => c.GetInstanceID() + ":" + c.enabled)), Is.EqualTo(collidersBefore));
            Assert.That(refusedOnes.All(r => r.enabled), Is.True, "what was refused is still its renderer's");
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);

            // Given back: the renderers draw again, the world's own lookup answers again.
            display.Dispose();
            Assert.That(scene.body.enabled && scene.child.enabled && scene.turned.enabled, Is.True);
            Assert.That(ReferenceEquals(root.Display.Placement, root.Placement), Is.True);
            yield return EndWorld(root);
        }

        // The stage as its camera draws it, the display left out: what the scene's own renderers show.
        private Color32[] DrawCompareRenderersPicture(ShadowStage stage, LightShadows shadows)
        {
            stage.light.shadows = shadows;
            var request = new RenderPipeline.StandardRequest { destination = stage.target };
            Assert.That(RenderPipeline.SupportsRenderRequest(stage.camera, request), Is.True, "render request");
            RenderPipeline.SubmitRenderRequest(stage.camera, request);
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = stage.target;
            var read = Track(new Texture2D(ShadowImageSize, ShadowImageSize, TextureFormat.RGBA32, false));
            read.ReadPixels(new Rect(0, 0, ShadowImageSize, ShadowImageSize), 0, 0);
            read.Apply(false);
            RenderTexture.active = previous;
            return read.GetPixels32();
        }

        [UnityTest]
        public IEnumerator DrawCompare_TheSameView_DrawnByTheRenderersAndByTheDisplay_IsTheSamePicture()
        {
            DrawCompareScene scene = NewDrawCompareScene();
            CutWorldRoot root = scene.root;
            yield return null;
            ShadowStage stage = NewShadowStage();
            stage.camera.orthographicSize = 1.8f;
            stage.camera.transform.SetPositionAndRotation(new Vector3(-2f, 4f, -3f), Quaternion.LookRotation(new Vector3(2f, -3.6f, 3f)));

            Color32[] byRenderers = null, byDisplay = null, byRenderersUnshadowed = null, byDisplayUnshadowed = null;
            // The renderers' pictures are the camera's alone: the display holds nothing and is not asked to draw.
            Assert.That(root.Display.ShownCount, Is.Zero);
            yield return null;
            byRenderers = DrawCompareRenderersPicture(stage, LightShadows.Hard);
            yield return null;
            byRenderersUnshadowed = DrawCompareRenderersPicture(stage, LightShadows.None);

            SceneDrawInventory inventory = SceneDrawInventory.Collect(scene.instances, root.MaterialBindings, new SceneDrawMeshBank());
            Assert.That(inventory.Refusals, Is.Empty);
            var display = new SceneDrawVpDisplay(root);
            foreach (SceneDrawTarget target in inventory.Targets) Assert.That(display.TryShow(target, out string why), Is.True, why);
            yield return null;
            yield return DrawStageAfterTheCollection(root, stage, LightShadows.Hard, d => byDisplay = d);
            yield return DrawStageAfterTheCollection(root, stage, LightShadows.None, d => byDisplayUnshadowed = d);

            Assert.That(inventory.Targets.All(t => !t.renderer.enabled), Is.True, "the second pictures are the display's alone");

            (int compared, int onlyOne, float mean, int p95, int largest) Compare(Color32[] x, Color32[] y)
            {
                int ground = Mode(x), onlyOne = 0;
                var differences = new List<int>();
                for (int i = 0; i < x.Length; i++)
                {
                    bool gx = Mathf.Abs(Grey(x[i]) - ground) <= 6, gy = Mathf.Abs(Grey(y[i]) - ground) <= 6;
                    if (gx != gy) onlyOne++;
                    if (gx || gy) continue;
                    differences.Add(Mathf.Max(Mathf.Abs(x[i].r - y[i].r), Mathf.Max(Mathf.Abs(x[i].g - y[i].g), Mathf.Abs(x[i].b - y[i].b))));
                }

                differences.Sort();
                return (differences.Count, onlyOne, differences.Count > 0 ? (float)differences.Average() : 0f, differences.Count > 0 ? differences[(int)(0.95f * (differences.Count - 1))] : 0, differences.Count > 0 ? differences[differences.Count - 1] : 0);
            }

            var shading = Compare(byRenderersUnshadowed, byDisplayUnshadowed);
            var shadowed = Compare(byRenderers, byDisplay);
            TestContext.Out.WriteLine("without shadows: object pixels " + shading.compared + ", in one picture only " + shading.onlyOne + ", mean channel difference " + shading.mean.ToString("F2") + ", 95th percentile " + shading.p95 + ", largest " + shading.largest);
            TestContext.Out.WriteLine("with the light's shadows: object pixels " + shadowed.compared + ", in one picture only " + shadowed.onlyOne + ", mean channel difference " + shadowed.mean.ToString("F2") + ", 95th percentile " + shadowed.p95 + ", largest " + shadowed.largest);
            Assert.That(shading.compared, Is.GreaterThan(400), "the objects cover the picture");
            Assert.That(shading.onlyOne, Is.LessThanOrEqualTo(shading.compared / 10), "the same shapes at the same places (edges aside): nothing missing, nothing more");
            Assert.That(shading.mean, Is.LessThanOrEqualTo(2f), "the same colour on average (shape, submeshes, material)");
            Assert.That(shading.p95, Is.LessThanOrEqualTo(6), "and nearly everywhere (edges and the normal's and the UV's quantisation aside)");
            Assert.That(shadowed.mean, Is.LessThanOrEqualTo(2f), "the same with the light's shadows, cast and received alike");
            Assert.That(shadowed.p95, Is.LessThanOrEqualTo(6));
            display.Dispose();
            yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator DrawCompare_TheSceneIsQuieted_TheSandboxsSettingsUiIsNotDrawn_AndARunWithItDrawnDoesNotHoldItsConditions()
        {
            bool heldBefore = PlayableCityCuttable.Held;
            int rateBefore = Application.targetFrameRate, vSyncBefore = QualitySettings.vSyncCount;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 120;
            string folder = Path.Combine(Application.temporaryCachePath, "draw-compare-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                DrawCompareScene scene = NewDrawCompareScene();
                CutWorldRoot root = scene.root;
                // The scene's diagnostics UI, as the city's scene carries it: drawn unless told otherwise. (The recorder's controls are hidden by the same call; the recorder itself is not made here: it would begin a capture of its own.)
                GameObject ui = TrackActor(new GameObject("sandbox UI"));
                var overlay = ui.AddComponent<SandboxSlashDiagnosticsOverlay>();
                Assert.That(overlay.Visible, Is.True, "drawn as the scene has them");
                ShadowStage stage = NewShadowStage();
                yield return null;
                yield return null;
                Assert.That(SceneDrawComparePath.TryParse("start,0,2,-6,0\nhold,0.15\nturn,20,0.15\n", out SceneDrawComparePath path, out string failure), Is.True, failure);

                // A run started without the quieting: the UI is drawn, and the run says it did not hold its conditions.
                var drawn = new SandboxDrawComparePlayerCheck.Settings
                {
                    directory = Path.Combine(folder, "ui-drawn"), mode = SandboxDrawComparePlayerCheck.Mode.Unity, path = path, pathFile = "(the test's)", warmupLaps = 0,
                };
                SandboxDrawComparePlayerCheck.Runner withUi = SandboxDrawComparePlayerCheck.Begin(drawn, root, scene.instances, stage.camera, null);
                TrackActor(withUi.gameObject);
                yield return Until(() => withUi.Done, "the run with the UI drawn ends");
                Assert.That(withUi.Completed, Is.True);
                Assert.That(withUi.ConditionsHeld, Is.False, "a run with the settings UI drawn is not a comparison");
                Assert.That(withUi.Code, Is.EqualTo(21));
                StringAssert.Contains("IMGUI", withUi.ConditionsNote);
                StringAssert.Contains("sandbox IMGUI panels drawn 1", File.ReadAllText(Path.Combine(drawn.directory, "draw-compare-result.txt")));

                // The quieting, as the Player does it after the scene's load.
                string said = SandboxDrawComparePlayerCheck.QuietScene();
                TestContext.Out.WriteLine(said);
                Assert.That(overlay.Visible, Is.False, "the diagnostics overlay is not drawn");
                Assert.That(overlay.enabled, Is.True, "only their drawing goes");
                Assert.That(PlayableCityCuttable.Held, Is.True, "no placed cuttable becomes a cut target");
                StringAssert.Contains("hidden: 1", said);

                var hidden = new SandboxDrawComparePlayerCheck.Settings
                {
                    directory = Path.Combine(folder, "ui-hidden"), mode = SandboxDrawComparePlayerCheck.Mode.Unity, path = path, pathFile = "(the test's)", warmupLaps = 0,
                };
                SandboxDrawComparePlayerCheck.Runner quieted = SandboxDrawComparePlayerCheck.Begin(hidden, root, scene.instances, stage.camera, null);
                TrackActor(quieted.gameObject);
                yield return Until(() => quieted.Done, "the quieted run ends");
                Assert.That(quieted.ConditionsHeld, Is.True, quieted.ConditionsNote);
                Assert.That(quieted.Code, Is.Zero);
                StringAssert.Contains("sandbox IMGUI panels drawn 0", File.ReadAllText(Path.Combine(hidden.directory, "draw-compare-result.txt")));
                yield return EndWorld(root);
            }
            finally
            {
                PlayableCityCuttable.Held = heldBefore;
                Application.targetFrameRate = rateBefore;
                QualitySettings.vSyncCount = vSyncBefore;
                try
                {
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                }
                catch (IOException)
                {
                }
            }
        }

        [UnityTest]
        public IEnumerator DrawCompare_TheRun_ByTheDisplay_PreparesBeforeItMeasures_WritesItsRecords_AndEndsWithoutAnyCheck()
        {
            yield return DrawCompareRun(true);
        }

        [UnityTest]
        public IEnumerator DrawCompare_TheRun_ByTheRenderers_GoesTheSameWay_ConvertingAndShowingNothing()
        {
            yield return DrawCompareRun(false);
        }

        private IEnumerator DrawCompareRun(bool vp)
        {
            int rateBefore = Application.targetFrameRate, vSyncBefore = QualitySettings.vSyncCount;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 120;
            string folder = Path.Combine(Application.temporaryCachePath, "draw-compare-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                Assert.That(SceneDrawComparePath.TryParse("start,0,2,-6,0\nhold,0.2\nturn,25,0.2\ngoto,1,-6,0.2\n", out SceneDrawComparePath path, out string failure), Is.True, failure);
                {
                    DrawCompareScene scene = NewDrawCompareScene();
                    CutWorldRoot root = scene.root;
                    yield return null;
                    ShadowStage stage = NewShadowStage();
                    // The stage's own making settles first (what it destroys goes at the frame's end): the run reads the scene as it then stands.
                    yield return null;
                    yield return null;
                    string output = Path.Combine(folder, vp ? "vp3c" : "unity");
                    string capture = Path.Combine(folder, (vp ? "vp3c" : "unity") + "-capture.raw");
                    var settings = new SandboxDrawComparePlayerCheck.Settings
                    {
                        directory = output, mode = vp ? SandboxDrawComparePlayerCheck.Mode.Vp3c : SandboxDrawComparePlayerCheck.Mode.Unity,
                        path = path, pathFile = "(the test's)", warmupLaps = 1, profilerFile = capture, captureTimes = new[] { 0.1 },
                    };
                    bool profilerBefore = Profiler.enabled;
                    SandboxDrawComparePlayerCheck.Runner runner = SandboxDrawComparePlayerCheck.Begin(settings, root, scene.instances, stage.camera, null);
                    TrackActor(runner.gameObject);
                    int code = -1;
                    runner.quit = c => code = c;
                    bool measuringSeen = false;
                    float deadline = Time.realtimeSinceStartup + 60f;
                    while (!runner.Done && Time.realtimeSinceStartup < deadline)
                    {
                        if (runner.Phase == SandboxDrawComparePlayerCheck.Phase.Preparing || runner.Phase == SandboxDrawComparePlayerCheck.Phase.WarmingUp)
                        {
                            Assert.That(runner.RowCount, Is.Zero, "no row while preparing");
                            Assert.That(runner.MeasureStartFrame, Is.EqualTo(-1));
                            Assert.That(File.Exists(Path.Combine(output, "frames.csv")), Is.False);
                        }

                        measuringSeen |= runner.Phase == SandboxDrawComparePlayerCheck.Phase.Measuring;
                        yield return null;
                    }

                    Assert.That(runner.Done, Is.True, "the run ends by itself, with no check to wait for");
                    string result = File.ReadAllText(Path.Combine(output, "draw-compare-result.txt"));
                    string preparation = File.ReadAllText(Path.Combine(output, "draw-compare-preparation.txt"));
                    TestContext.Out.WriteLine((vp ? "vp3c" : "unity") + ":\n" + result + "\n" + preparation);
                    Assert.That(code, Is.EqualTo(runner.Code));
                    Assert.That(runner.Completed, Is.True);
                    Assert.That(runner.ConditionsHeld, Is.True, runner.ConditionsNote);
                    Assert.That(runner.Code, Is.Zero);
                    Assert.That(measuringSeen, Is.True);
                    Assert.That(runner.RowsSeenBeforeMeasurement, Is.Zero, "the frame table began with the measurement");
                    Assert.That(runner.ProfilerSeenBeforeMeasurement, Is.False, "the Profiler's recording began with the measurement");
                    Assert.That(runner.ProfilerStartedHere, Is.True);
                    Assert.That(Profiler.enabled, Is.EqualTo(profilerBefore), "and ended with it");
                    Assert.That(runner.WarmupFrames, Is.GreaterThan(0), "a lap of the path before it, not recorded");
                    Assert.That(runner.RowsPastRoom, Is.Zero);

                    string[] lines = File.ReadAllLines(Path.Combine(output, "frames.csv"));
                    Assert.That(lines.Length - 1, Is.EqualTo(runner.RowCount));
                    Assert.That(runner.RowCount, Is.GreaterThan(5));
                    string[] header = lines[0].Split(',');
                    Assert.That(header, Has.Member("ftGpuMs").And.Member("ftRenderMs").And.Member("Zantetsu.GpuCull.Issue").And.Member("Zantetsu.Snapshot.Place").And.Member("placeQueries").And.Member("camX").And.Member("camYaw"));
                    Assert.That(runner.LargestEyeDistance, Is.LessThan(1e-3), "the camera stood where the path put the eye");
                    Assert.That(runner.LargestEyeHeadingDifference, Is.LessThan(1e-2));
                    StringAssert.Contains("eye against the path: largest distance 0.0000 m", result);
                    StringAssert.Contains("the targets' own: roots 2, colliders 2 (enabled 2), bodies 1 (kinematic 1)", result);
                    StringAssert.Contains("(as loaded: the same)", result);
                    Assert.That(int.Parse(lines[1].Split(',')[0]), Is.EqualTo(runner.MeasureStartFrame), "the first row is the measurement's first frame");
                    Assert.That(int.Parse(lines[lines.Length - 1].Split(',')[0]), Is.EqualTo(runner.MeasureEndFrame));
                    Assert.That(lines.Skip(1).All(l => l.Split(',').Length == header.Length), Is.True);
                    double lastT = double.Parse(lines[lines.Length - 1].Split(',')[1], System.Globalization.CultureInfo.InvariantCulture);
                    Assert.That(lastT, Is.LessThan(path.Seconds), "the rows are the path's time");

                    StringAssert.Contains("run: completed", result);
                    StringAssert.Contains("comparison conditions: held", result);
                    StringAssert.Contains("checks: not run", result);
                    StringAssert.Contains("mode: " + (vp ? "vp3c" : "unity"), result);
                    StringAssert.Contains("GPU time: ", result);
                    // A capture was asked for and no external GPU profiler launched this run: nothing is captured, and that is said.
                    Assert.That(runner.CapturesTaken, Is.Zero);
                    StringAssert.Contains("captures: asked 1; external GPU profiler attached False", result);
                    StringAssert.Contains("NOT captured (no external GPU profiler attached)", result);
                    StringAssert.Contains("commands that draw " + (vp ? "6" : "0"), result);
                    StringAssert.Contains("renderers taken 3", preparation);
                    StringAssert.Contains("none of it is in the frame table", preparation);
                    if (vp)
                    {
                        StringAssert.Contains("registration: shown 3 of 3", preparation);
                        Assert.That(runner.VpDisplay, Is.Not.Null);
                        Assert.That(runner.VpDisplay.ShownCount, Is.EqualTo(3));
                    }
                    else
                    {
                        StringAssert.Contains("registration: none", preparation);
                        Assert.That(runner.VpDisplay, Is.Null, "nothing is converted or shown in Unity's way");
                        Assert.That(root.Display.ShownCount, Is.Zero);
                        Assert.That(root.Storage.VertexCount, Is.Zero);
                    }

                    Assert.That(scene.body.enabled && scene.child.enabled && scene.turned.enabled, Is.True, "after the run the renderers draw as placed");
                    Assert.That(root.Owners.Count, Is.Zero);

                    // A second run into the same folder is refused: nothing is overwritten.
                    SandboxDrawComparePlayerCheck.Runner again = SandboxDrawComparePlayerCheck.Begin(settings, root, scene.instances, stage.camera, null);
                    TrackActor(again.gameObject);
                    yield return Until(() => again.Done, "the second run ends");
                    Assert.That(again.Code, Is.EqualTo(20));
                    Assert.That(again.Completed, Is.False);
                    Assert.That(File.ReadAllLines(Path.Combine(output, "frames.csv")).Length, Is.EqualTo(lines.Length), "the first run's rows are as they were");

                    yield return EndWorld(root);
                }
            }
            finally
            {
                Application.targetFrameRate = rateBefore;
                QualitySettings.vSyncCount = vSyncBefore;
                if (Profiler.enabled && Profiler.enableBinaryLog && Profiler.logFile != null && Profiler.logFile.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                {
                    Profiler.enabled = false;
                    Profiler.enableBinaryLog = false;
                    Profiler.logFile = "";
                }

                try
                {
                    if (Directory.Exists(folder)) Directory.Delete(folder, true);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
