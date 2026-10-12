using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.TestTools;
using Zantetsu.Core;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The pause menu on the product's drawing (DESIGN 4.6): in a world drawn by the Direct3D 12 plugin's route, the
    /// menu's canvas is drawn by the camera over the world, the world goes on being drawn by the plugin while the game
    /// is paused, and nothing of the picture beside the menu changes.
    /// </summary>
    public sealed partial class CutWorldRootPlayModeTests
    {
        private sealed class MenuTestInput : IPlayMenuInput
        {
            public PlayMenuInputFrame Frame;

            public PlayMenuInputFrame Read(Transform trackingSpace, Camera camera) => Frame;
        }

        /// <summary>
        /// The menu alone before a camera, large enough to read: its picture is saved for the record, and what is
        /// asserted is only that the canvas is drawn with its controls -- the panel, the lettering and the sliders.
        /// </summary>
        [UnityTest]
        public IEnumerator PlayMenu_ItsCanvas_IsDrawnByACamera_WithItsLetteringAndControls()
        {
            float timeScaleBefore = Time.timeScale;
            PlayMenu menu = null;
            try
            {
                var target = Track(new RenderTexture(1024, 800, 24, RenderTextureFormat.ARGB32) { antiAliasing = 1 });
                target.Create();
                Camera camera = TrackActor(new GameObject("Menu Picture Camera")).AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = new Color(0.45f, 0.5f, 0.55f, 1f);
                camera.fieldOfView = 30f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 20f;
                camera.targetTexture = target;
                camera.transform.SetPositionAndRotation(new Vector3(300f, 50f, 300f), Quaternion.Euler(0f, 35f, 0f));

                var input = new MenuTestInput();
                menu = TrackActor(new GameObject("Play menu under test")).AddComponent<PlayMenu>();
                menu.Input = input;
                menu.Camera = camera;
                menu.Setup(SandboxScenarioKind.NpcSlash);
                yield return null;
                input.Frame.menuDown = true;
                yield return null;
                Assert.That(menu.IsOpen, Is.True);

                // The pointer on the resume button: its hover and the pointer's dot are in the picture.
                input.Frame.hasPointer = true;
                input.Frame.pointerOrigin = camera.transform.position;
                RectTransform resume = menu.View.ResumeRect;
                input.Frame.pointerDirection = resume.TransformPoint(resume.rect.center + new Vector2(60f, -10f)) - camera.transform.position;
                yield return null;
                yield return null;
                Color32[] pixels = ReadGpuCullTarget(target);
                int panel = 0, lettering = 0, fill = 0;
                foreach (Color32 p in pixels)
                {
                    if (p.r < 60 && p.g < 60 && p.b < 70) panel++;
                    if (p.r > 200 && p.g > 200 && p.b > 200) lettering++;
                    if (p.b > 180 && p.b > p.r + 60) fill++;
                }

                string folder = System.IO.Path.Combine(Application.temporaryCachePath, "play-menu-pictures");
                System.IO.Directory.CreateDirectory(folder);
                var picture = Track(new Texture2D(target.width, target.height, TextureFormat.RGBA32, false));
                picture.SetPixels32(pixels);
                picture.Apply(false);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "menu.png"), picture.EncodeToPNG());
                TestContext.Out.WriteLine("menu picture: " + target.width + " x " + target.height + ", panel pixels " + panel + ", lettering " + lettering + ", slider fill " + fill
                    + "; on " + SystemInfo.graphicsDeviceType + "; in " + folder);
                Assert.That(panel, Is.GreaterThan(pixels.Length / 4), "the panel");
                Assert.That(lettering, Is.GreaterThan(2000), "the lettering");
                Assert.That(fill, Is.GreaterThan(3000), "the volume slider's fill");
            }
            finally
            {
                if (menu != null)
                {
                    menu.Release();
                }

                GamePause.ResetForTest();
                Time.timeScale = timeScaleBefore;
            }
        }

        [UnityTest]
        public IEnumerator PlayMenu_OnThePluginsRoute_IsDrawnOverTheWorld_WhichGoesOnBeingDrawnWhilePaused()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12)
            {
                Assert.Ignore("the plugin draws on Direct3D 12 only; this Editor runs " + SystemInfo.graphicsDeviceType + " (start it with -force-d3d12 for this case)");
            }

            bool requestedBefore = VpGpuCullSetup.Requested;
            VpBodyRouteRequest nativeBefore = VpNativeDrawSetup.Request;
            float timeScaleBefore = Time.timeScale;
            VpNativeDrawSetup.Request = VpBodyRouteRequest.Native;
            VpGpuCullSetup.Requested = true;
            PlayMenu menu = null;
            try
            {
                Shader casterShader = Shader.Find("Zantetsu/VP Indexed Indirect Shadow Caster");
                Assert.That(casterShader, Is.Not.Null, "the display's shadow caster is in this project");
                Material stable = Track(new Material(casterShader) { name = "stable caster" });
                stable.SetFloat("_Cull", (float)CullMode.Back);
                Material immediate = Track(new Material(casterShader) { name = "immediate caster" });
                immediate.SetFloat("_Cull", (float)CullMode.Off);
                CutWorldRoot root = NewWorld(
                    out Shader _,
                    null,
                    null,
                    SmallDisplayRoom(1024),
                    world =>
                    {
                        SetPrivate(world, "shadowMaterial", stable);
                        SetPrivate(world, "provisionalShadowMaterial", immediate);
                    });
                Assert.That(root.NativeDrawFailure, Is.Null, root.NativeDrawFailure);
                Assert.That(root.Display.NativeArguments, Is.True, "the display's batch writes the plugin's argument entries");

                // A level camera with a wide view, rendering every frame as a player's does.
                ShadowStage stage = NewShadowStage();
                GameObject ground = GameObject.Find("Plane");
                Assert.That(ground, Is.Not.Null, "the stage's ground");
                ground.transform.localScale = new Vector3(14f, 1f, 14f);
                Camera camera = stage.camera;
                camera.orthographic = false;
                camera.fieldOfView = 90f;
                camera.nearClipPlane = 0.1f;
                camera.farClipPlane = 60f;
                camera.transform.SetPositionAndRotation(new Vector3(0f, 2f, -6f), Quaternion.identity);
                camera.enabled = true;
                var drawing = root.gameObject.AddComponent<CutWorldCameraDrawing>();
                SetPrivate(drawing, "world", root);
                SetPrivate(drawing, "cameras", new[] { camera });

                // Bodies at the picture's left and right edges, and one straight ahead whose near face is 1.2 m from
                // the camera (the bodies are 2 m cubes): nearer than the menu's 1.3 m, so the menu stands inside it and a
                // depth-tested menu would be hidden by that face.
                foreach (float x in new[] { 0.07f, 0.5f, 0.93f })
                {
                    LogicalFragmentId id = AddBody(root, camera.ViewportToWorldPoint(new Vector3(x, 0.5f, x == 0.5f ? 2.2f : 6f)));
                    Assert.That(root.Owners.TryGet(id, out PhysicsFragmentOwner owner), Is.True);
                    owner.Body.isKinematic = true;
                }

                yield return null;
                yield return null;
                yield return null;
                Zantetsu.Rendering.Urp.VpNativeDrawRoute route = drawing.NativeRoute;
                Assert.That(route, Is.Not.Null, "the camera is drawn through the plugin's route");
                Assert.That(route.BodiesIssued, Is.GreaterThan(0), "and the plugin drew the bodies");
                Color32[] before = ReadGpuCullTarget(stage.target);
                int width = stage.target.width, height = stage.target.height;

                // The body ahead is in the picture where the menu will be: above the horizon, where nothing else is lit.
                Color32 ahead = before[((height * 56 / 100) * width) + (width / 2)];
                Assert.That(ahead.r > 60 && ahead.g > 60 && ahead.b > 60, Is.True, "the near body's face covers the picture's middle: " + ahead);

                // The menu, opened by its button and the button then held.
                var input = new MenuTestInput();
                menu = TrackActor(new GameObject("Play menu under test")).AddComponent<PlayMenu>();
                menu.Input = input;
                menu.Camera = camera;
                menu.Setup(SandboxScenarioKind.NpcSlash);
                yield return null;
                Assert.That(menu.IsOpen, Is.False);
                input.Frame.menuDown = true;
                yield return null;
                Assert.That(menu.IsOpen && GamePause.Manual, Is.True, "opened and paused in the frame of the press");
                long issuedAtOpening = route.BodiesIssued;
                long stepAtOpening = CutPhysicsStep.Clock.StepId;
                for (int i = 0; i < 5; i++)
                {
                    yield return null;
                }

                Assert.That(menu.IsOpen, Is.True, "held: still open");
                Assert.That(menu.State.Opens, Is.EqualTo(1));
                Assert.That(menu.View.DrawnInFront, Is.True, "the menu's own material is in the project: drawn in front of everything");
                Assert.That(route.BodiesIssued, Is.GreaterThan(issuedAtOpening), "the plugin goes on drawing the bodies while the game is paused");
                Assert.That(CutPhysicsStep.Clock.StepId, Is.EqualTo(stepAtOpening), "and the physics does not step");
                Color32[] with = ReadGpuCullTarget(stage.target);

                // Where the menu stands in the picture: its canvas's corners through the camera. It is level with the
                // camera, so the rows it covers are the same whichever way up the picture was read.
                var corners = new Vector3[4];
                ((RectTransform)menu.View.Root).GetWorldCorners(corners);
                float xMin = 1f, xMax = 0f, yMin = 1f, yMax = 0f;
                foreach (Vector3 corner in corners)
                {
                    Vector3 v = camera.WorldToViewportPoint(corner);
                    Assert.That(v.z, Is.EqualTo(PlayMenuView.Distance).Within(0.01f), "the menu stands 1.3 m before the camera");
                    xMin = Mathf.Min(xMin, v.x);
                    xMax = Mathf.Max(xMax, v.x);
                    yMin = Mathf.Min(yMin, v.y);
                    yMax = Mathf.Max(yMax, v.y);
                }

                Assert.That(yMin + yMax, Is.EqualTo(1f).Within(0.01f), "level with the camera");
                int inside = 0, insideChanged = 0, insideBright = 0, outside = 0, outsideChanged = 0, outsideLit = 0;
                for (int y = 0; y < height; y++)
                {
                    float vy = (y + 0.5f) / height;
                    for (int x = 0; x < width; x++)
                    {
                        float vx = (x + 0.5f) / width;
                        Color32 a = before[(y * width) + x], b = with[(y * width) + x];
                        int difference = Mathf.Max(Mathf.Abs(a.r - b.r), Mathf.Max(Mathf.Abs(a.g - b.g), Mathf.Abs(a.b - b.b)));
                        bool within = vx > xMin + 0.02f && vx < xMax - 0.02f && vy > yMin + 0.02f && vy < yMax - 0.02f;
                        bool beside = vx < xMin - 0.02f || vx > xMax + 0.02f || vy < yMin - 0.02f || vy > yMax + 0.02f;
                        if (within)
                        {
                            inside++;
                            if (difference > 12) insideChanged++;
                            if (b.b > 150 && b.b > b.r + 40) insideBright++;   // the sliders' fill
                        }
                        else if (beside)
                        {
                            outside++;
                            if (difference > 12) outsideChanged++;
                            if (b.r > 24 || b.g > 24 || b.b > 24) outsideLit++;
                        }
                    }
                }

                string folder = System.IO.Path.Combine(Application.temporaryCachePath, "play-menu-pictures");
                System.IO.Directory.CreateDirectory(folder);
                foreach ((string name, Color32[] pixels) in new[] { ("before", before), ("paused", with) })
                {
                    var picture = Track(new Texture2D(width, height, TextureFormat.RGBA32, false));
                    picture.SetPixels32(pixels);
                    picture.Apply(false);
                    System.IO.File.WriteAllBytes(System.IO.Path.Combine(folder, "native-" + name + ".png"), picture.EncodeToPNG());
                }

                TestContext.Out.WriteLine("menu in the picture: x " + xMin.ToString("F3") + ".." + xMax.ToString("F3") + ", y " + yMin.ToString("F3") + ".." + yMax.ToString("F3")
                    + "; inside " + inside + " pixels, changed " + insideChanged + ", slider-fill coloured " + insideBright
                    + "; beside " + outside + " pixels, changed " + outsideChanged + ", lit " + outsideLit + "; pictures in " + folder
                    + "; " + drawing.DescribeNativeRoute(out bool _));
                Assert.That(inside, Is.GreaterThan(width * height / 20), "the menu covers a good part of the picture");
                Assert.That(insideChanged, Is.GreaterThan(inside * 35 / 100), "the menu is drawn where it stands, before the body that is nearer than it");
                Assert.That(insideBright, Is.GreaterThan(20), "with its controls");
                Assert.That(outside, Is.GreaterThan(width * height / 10));
                Assert.That(outsideChanged, Is.LessThanOrEqualTo(outside / 200), "nothing beside the menu changed: the world stands as it was drawn");
                Assert.That(outsideLit, Is.GreaterThan(outside / 20), "and it is drawn there");

                // Closed: the picture is the one before.
                input.Frame.menuDown = false;
                yield return null;
                input.Frame.menuDown = true;
                yield return null;
                Assert.That(menu.IsOpen || GamePause.IsPaused, Is.False);
                yield return null;
                yield return null;
                Color32[] after = ReadGpuCullTarget(stage.target);
                int left = 0;
                for (int i = 0; i < after.Length; i++)
                {
                    if (Mathf.Abs(after[i].r - before[i].r) > 12 || Mathf.Abs(after[i].g - before[i].g) > 12 || Mathf.Abs(after[i].b - before[i].b) > 12) left++;
                }

                Assert.That(left, Is.LessThanOrEqualTo(after.Length / 200), "the menu is gone from the picture");
            }
            finally
            {
                if (menu != null)
                {
                    menu.Release();
                }

                GamePause.ResetForTest();
                Time.timeScale = timeScaleBefore;
                VpNativeDrawSetup.Request = nativeBefore;
                VpGpuCullSetup.Requested = requestedBefore;
            }
        }
    }
}
