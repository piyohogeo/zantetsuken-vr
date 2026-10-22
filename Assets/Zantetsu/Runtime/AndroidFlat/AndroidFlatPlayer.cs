using System.Collections;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.EnhancedTouch;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Zantetsu.Core;
using Zantetsu.Core.Input;
using Zantetsu.Core.Slash;
using Zantetsu.PhysicsCut;
using Zantetsu.Quest;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;
using Touch = UnityEngine.InputSystem.EnhancedTouch.Touch;
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

namespace Zantetsu.AndroidFlat
{
    /// <summary>The phone's input/display boundary. All hits and cuts still use the shared world and slash detector.</summary>
    [DefaultExecutionOrder(-50)]
    public sealed class AndroidFlatPlayer : MonoBehaviour
    {
        private const string Prefix = "ANDROID FLAT: ";
        private readonly FlatTouchInput input = new FlatTouchInput();
        private readonly AndroidFlatSettings settings = new AndroidFlatSettings();
        private readonly AndroidFlatView view = new AndroidFlatView();
        private readonly Transform[] visuals = new Transform[SlashWaveCore.MaximumCapacity];
        private readonly MeshRenderer[] visualRenderers = new MeshRenderer[SlashWaveCore.MaximumCapacity];
        private MaterialPropertyBlock visualProperties;
        private static readonly int ColourId = Shader.PropertyToID("_Color");
        private Camera cameraView;
        private CutWorldRoot world;
        private MobPlanPlayerInput move;
        private SlashHitDetector hits;
        private SlashWaveCore slash;
        private UniversalRenderPipelineAsset pipeline, previousPipeline;
        private QuestDrawPrewarm prewarm;
        private Coroutine preparation;
        private Material slashMaterial;
        private Mesh quad;
        private GameObject visualRoot;
        private Vector2Int size;
        private Rect safe;
        private bool ready, menu, enabledEnhancedTouch, mouseHeld, prepared, layoutDirty = true;
        private bool pausedLastFrame, sceneChanging;
        private int rejected, accepted, fpsFrames;
        private double nextReport, reportStart;
        private string failure;
        private AndroidFlatDrawCapabilities drawCapabilities;
        private VpBodyRouteRequest launchBodyRoute;
        private bool appliedMultiDrawIndirect, appliedDrawIndirectCount;
        private bool RequestedMultiDrawIndirect => drawCapabilities.MultiDrawIndirect && settings.MultiDrawIndirect;
        private bool RequestedDrawIndirectCount => RequestedMultiDrawIndirect && drawCapabilities.DrawIndirectCount && settings.DrawIndirectCount;
        private bool RenderingChangesPending => RequestedMultiDrawIndirect != appliedMultiDrawIndirect
            || RequestedDrawIndirectCount != appliedDrawIndirectCount;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Install()
        {
            if (!PlayerPlatform.IsAndroidFlat) return;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = 60;
            Application.runInBackground = false;
            var host = new GameObject("Android phone player");
            DontDestroyOnLoad(host);
            var player = host.AddComponent<AndroidFlatPlayer>();
            // Both preferences and the route must be settled before any scene's CutWorldRoot.Awake.
            player.InitializeRenderingSettings();
        }

        private void InitializeRenderingSettings()
        {
            settings.Load();
            launchBodyRoute = VpNativeDrawSetup.Request;
            if (!Application.isEditor && launchBodyRoute == VpBodyRouteRequest.Default)
                launchBodyRoute = VpBodyRouteRequest.NativePreferred;
            var deviceCapabilities = AndroidFlatDrawCapabilities.Read();
            // Explicit diagnostic arguments retain their route. Preferences never label a forced-off route as active.
            drawCapabilities = new AndroidFlatDrawCapabilities(
                deviceCapabilities.MultiDrawIndirect && launchBodyRoute != VpBodyRouteRequest.UnityApi,
                deviceCapabilities.DrawIndirectCount && VpNativeDrawPlugin.VulkanIndirectCount);
            ApplyRenderingSettings();
        }

        private void ApplyRenderingSettings()
        {
            appliedMultiDrawIndirect = RequestedMultiDrawIndirect;
            appliedDrawIndirectCount = RequestedDrawIndirectCount;
            VpNativeDrawSetup.UnityMultiDrawIndirect = appliedMultiDrawIndirect;
            // NativePreferred keeps compatibility fallback; an explicit Native retains its strict failure contract.
            VpNativeDrawSetup.Request = settings.MultiDrawIndirect ? launchBodyRoute : VpBodyRouteRequest.UnityApi;
            VpNativeDrawPlugin.VulkanIndirectCount = appliedDrawIndirectCount;
            layoutDirty = true;
            Debug.Log(Prefix + "rendering applied: MultiDrawIndirect=" + appliedMultiDrawIndirect
                + "; DrawIndirectCount=" + appliedDrawIndirectCount + "; routeRequest=" + VpNativeDrawSetup.Request
                + "; availableMulti=" + drawCapabilities.MultiDrawIndirect + "; availableCount=" + drawCapabilities.DrawIndirectCount);
        }

        private void Start()
        {
            if (!EnhancedTouchSupport.enabled) { EnhancedTouchSupport.Enable(); enabledEnhancedTouch = true; }
            visualProperties = new MaterialPropertyBlock();
            view.Build(transform);
            UpdateLoading();
            input.Cancel();
            GamePause.PausedChanged += OnPaused;
            SceneManager.sceneLoaded += OnSceneLoaded;
            preparation = StartCoroutine(PrepareScene());
        }

        private IEnumerator PrepareScene()
        {
            ready = false; failure = null; sceneChanging = false;
            UpdateLoading();
            while ((cameraView = Camera.main) == null || (world = FindAnyObjectByType<CutWorldRoot>()) == null) yield return null;
            foreach (var katana in FindObjectsByType<SandboxRightHandKatana>(FindObjectsInactive.Include, FindObjectsSortMode.None)) katana.enabled = false;
            foreach (var other in FindObjectsByType<SandboxLocomotionInput>(FindObjectsInactive.Include, FindObjectsSortMode.None)) other.enabled = false;
            move = FindAnyObjectByType<MobPlanPlayerInput>();
            if (move == null || move.player == null) { Fail("The scene has no player locomotion"); yield break; }
            move.liveInput = false;
            move.DualStick = false; move.SnapTurn = false;
            cameraView.transform.SetParent(move.player.transform, false);
            cameraView.transform.localPosition = new Vector3(0, 1.65f, 0);
            cameraView.transform.localRotation = Quaternion.identity;
            cameraView.stereoTargetEye = StereoTargetEyeMask.None;
            cameraView.GetUniversalAdditionalCameraData().allowXRRendering = false;
            move.head = cameraView.transform;
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset source)
            {
                previousPipeline = QualitySettings.renderPipeline as UniversalRenderPipelineAsset;
                pipeline = Instantiate(source);
                pipeline.name = "Android phone runtime quality";
                QualitySettings.renderPipeline = pipeline;
                settings.Apply(pipeline);
            }
            slash = new SlashWaveCore(new SlashBlade(Pose.identity, .9f), SlashWaveCore.MaximumCapacity);
            CreateVisuals();
            size = default; safe = default; layoutDirty = true;
            // A replacement camera needs its FOV even if the display orientation did not change.
            // Reuse the detector the scene's props and character slots already register with.
            var binding = FindAnyObjectByType<SandboxSlashPropHit>();
            double deadline = GamePause.RealtimeSinceStartupAsDouble + 120;
            while (!world.IsReady || binding == null || binding.Detector == null)
            {
                if (GamePause.RealtimeSinceStartupAsDouble > deadline) { Fail("The cut world did not finish loading"); yield break; }
                if (binding == null) binding = FindAnyObjectByType<SandboxSlashPropHit>();
                yield return null;
            }
            hits = binding.Detector;
            var crowd = FindAnyObjectByType<MobPlanCrowd>();
            while (crowd != null && !crowd.PreparationEnded)
            {
                if (GamePause.RealtimeSinceStartupAsDouble > deadline) { Fail("The crowd did not finish loading"); yield break; }
                yield return null;
            }
            // Warm the same native/stencil paths as Quest, without requesting its XR motion pass.
            prewarm = new QuestDrawPrewarm(cameraView, world, world.GetComponent<CutWorldCameraDrawing>(),
                world.BodyRoute == "VP3C-NATIVE", false, false);
            yield return prewarm.Run();
            if (!prewarm.Completed) { Fail("Drawing preparation failed: " + prewarm.Failure); yield break; }
            prepared = ready = true;
            UpdateLoading();
            input.Cancel();
            reportStart = Time.realtimeSinceStartupAsDouble; nextReport = reportStart + .5; fpsFrames = 0;
            Debug.Log(Prefix + "ready; device=" + SystemInfo.deviceModel + "; GPU=" + SystemInfo.graphicsDeviceName
                + "; route=" + world.BodyRoute + "; waves=" + slash.WaveCapacity + "; speed=12m/s; lifetime=1.5s; target=60fps");
        }

        private void Fail(string reason)
        {
            failure = reason; ready = false;
            UpdateLoading();
            input.Cancel();
            Debug.LogError(Prefix + reason);
        }

        private void UpdateLoading()
        {
            Rect area = Screen.safeArea;
            if (area.width <= 0 || area.height <= 0) area = new Rect(0, 0, Screen.width, Screen.height);
            view.SetLoading(failure == null && (!ready || sceneChanging), area, Time.unscaledTime);
        }

        private void Update()
        {
            // The overlay also animates while no camera exists, and during paused shutdown/preparation frames.
            UpdateLoading();
            if (cameraView == null) return;
            UpdateLayout();
            if (sceneChanging)
            {
                input.Cancel();
                if (failure != null) view.SetStatus("Unable to start - see Player log");
                return;
            }
            bool paused = GamePause.IsPaused;
            if (paused && !pausedLastFrame) input.Cancel();
            pausedLastFrame = paused;
            if (Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame) ToggleMenu();
            double now = GamePause.UnscaledTimeAsDouble;
            bool ticked = ready && !GamePause.IsPaused;
            if (ticked) slash.Tick(now);
            ReadTouches();
            if (ready && !GamePause.IsPaused && !menu)
            {
                if (!ticked) slash.Tick(GamePause.UnscaledTimeAsDouble);
                move.speed = settings.MoveSpeed; move.turnDegreesPerSecond = settings.TurnSpeed;
                Vector2 stick = input.Stick;
                move.Submit(stick.y, stick.x, Mathf.Min(GamePause.UnscaledDeltaTime, 1f / 30f));
                hits.Evaluate(slash);
                SyncVisuals(now);
            }
            view.SetStick(input.StickCentre, input.Stick, input.StickRadius);
            if (!ready)
                view.SetStatus(failure == null ? "" : "Unable to start - see Player log");
            else if (Time.realtimeSinceStartupAsDouble >= nextReport)
            {
                double elapsed = Time.realtimeSinceStartupAsDouble - reportStart;
                view.SetStatus((fpsFrames / System.Math.Max(.001, elapsed)).ToString("F0") + " fps");
                fpsFrames = 0; reportStart = Time.realtimeSinceStartupAsDouble; nextReport = reportStart + .5;
            }
            fpsFrames++;
        }

        private void ReadTouches()
        {
            bool anyHeld = false;
            var touches = Touch.activeTouches;
            for (int i = 0; i < touches.Count; i++)
            {
                var touch = touches[i];
                bool end = touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled;
                anyHeld |= !end;
                var phase = touch.phase == TouchPhase.Began ? FlatTouchPhase.Began
                    : touch.phase == TouchPhase.Ended ? FlatTouchPhase.Ended
                    : touch.phase == TouchPhase.Canceled ? FlatTouchPhase.Cancelled : FlatTouchPhase.Moved;
                Feed(touch.touchId, phase, touch.screenPosition, touch.time);
            }
            // Editor/desktop smoke testing follows the same ownership and recognition path.
            if (touches.Count == 0 && Mouse.current != null)
            {
                var mouse = Mouse.current;
                bool pressed = mouse.leftButton.isPressed;
                if (pressed || mouseHeld)
                    Feed(-1, pressed ? mouseHeld ? FlatTouchPhase.Moved : FlatTouchPhase.Began : FlatTouchPhase.Ended,
                        mouse.position.ReadValue(), Time.realtimeSinceStartupAsDouble);
                mouseHeld = pressed; anyHeld |= pressed;
            }
            input.EndFrame(anyHeld);
        }

        private void Feed(int id, FlatTouchPhase phase, Vector2 position, double time)
        {
            var result = input.Feed(id, phase, position, time, view.IsUi(position));
            if (result.UiTap) Command(view.CommandAt(result.Position));
            if (result.Flick && ready && !menu && !GamePause.IsPaused
                && ScreenSlashGeometry.TryCreate(cameraView, result.Position, result.Direction, out ScreenSlashFrame frame))
            {
                if (slash.TryLatchScreenSlash(GamePause.UnscaledTimeAsDouble, in frame, out _)) accepted++;
                else { rejected++; Debug.Log(Prefix + "slash capacity reached; rejected=" + rejected); }
            }
        }

        private void UpdateLayout()
        {
            Rect currentSafe = Screen.safeArea;
            var currentSize = new Vector2Int(Screen.width, Screen.height);
            bool changed = currentSize != size || currentSafe != safe;
            if (changed)
            {
                size = currentSize; safe = currentSafe; input.Cancel(); layoutDirty = true;
                float aspect = Mathf.Max(.01f, cameraView.aspect);
                // Keep the long-side FOV at 90 degrees in either orientation; pitch remains zero.
                cameraView.fieldOfView = aspect >= 1f
                    ? Camera.HorizontalToVerticalFieldOfView(90f, aspect) : 90f;
                Debug.Log(Prefix + "screen=" + size.x + "x" + size.y + "; safe=" + safe + "; verticalFov=" + cameraView.fieldOfView);
            }
            if (!layoutDirty) return;
            float shortSide = Mathf.Min(safe.width, safe.height);
            input.StickRadius = shortSide * .13f;
            bool portrait = size.y > size.x;
            Vector2 position = settings.GetStickPosition(portrait);
            float extent = input.StickRadius * 1.04f; // Full outward thumb: .62 travel + .42 knob radius.
            Vector2 inset = new Vector2(extent / Mathf.Max(1f, safe.width), extent / Mathf.Max(1f, safe.height));
            position = new Vector2(Mathf.Clamp(position.x, inset.x, 1f - inset.x), Mathf.Clamp(position.y, inset.y, 1f - inset.y));
            settings.SetStickPosition(portrait, position);
            input.StickCentre = new Vector2(safe.x + safe.width * position.x, safe.y + safe.height * position.y);
            input.MinimumFlickPixels = shortSide * .018f;
            input.MinimumFlickPixelsPerSecond = shortSide * .85f;
            view.Layout(safe, input.StickCentre, input.StickRadius, menu, settings, portrait, drawCapabilities, RenderingChangesPending);
            layoutDirty = false;
        }

        private void ToggleMenu()
        {
            if (sceneChanging || (!ready && failure == null)) return;
            menu = !menu;
            view.ShowStickPosition(false);
            GamePause.SetManual(menu);
            input.Cancel(); layoutDirty = true;
            if (!menu) settings.Save();
        }

        private void Command(FlatCommand command)
        {
            if (sceneChanging) return;
            switch (command)
            {
                case FlatCommand.Menu: ToggleMenu(); return;
                case FlatCommand.Resume: if (menu) ToggleMenu(); return;
                case FlatCommand.StickPosition: view.ShowStickPosition(true); input.Cancel(); layoutDirty = true; return;
                case FlatCommand.Back: view.ShowStickPosition(false); input.Cancel(); layoutDirty = true; return;
                case FlatCommand.StickLeft: ShiftStick(new Vector2(-.01f, 0)); return;
                case FlatCommand.StickRight: ShiftStick(new Vector2(.01f, 0)); return;
                case FlatCommand.StickDown: ShiftStick(new Vector2(0, -.01f)); return;
                case FlatCommand.StickUp: ShiftStick(new Vector2(0, .01f)); return;
                case FlatCommand.StickReset: settings.ResetStickPosition(size.y > size.x); SaveStickPosition(); return;
                case FlatCommand.MoveDown: settings.MoveSpeed = Mathf.Max(.5f, settings.MoveSpeed - .25f); break;
                case FlatCommand.MoveUp: settings.MoveSpeed = Mathf.Min(5f, settings.MoveSpeed + .25f); break;
                case FlatCommand.TurnDown: settings.TurnSpeed = Mathf.Max(30f, settings.TurnSpeed - 15); break;
                case FlatCommand.TurnUp: settings.TurnSpeed = Mathf.Min(240f, settings.TurnSpeed + 15); break;
                case FlatCommand.Fsr: settings.Fsr = !settings.Fsr; break;
                case FlatCommand.Scale: settings.NextScale(); break;
                case FlatCommand.Sharpness: settings.Sharpness = settings.Sharpness >= .99f ? 0f : settings.Sharpness + .25f; break;
                case FlatCommand.MultiDrawIndirect:
                    if (drawCapabilities.MultiDrawIndirect) { settings.MultiDrawIndirect = !settings.MultiDrawIndirect; SaveRenderingPreference(); }
                    return;
                case FlatCommand.DrawIndirectCount:
                    if (drawCapabilities.DrawIndirectCount && settings.MultiDrawIndirect) { settings.DrawIndirectCount = !settings.DrawIndirectCount; SaveRenderingPreference(); }
                    return;
                case FlatCommand.ApplyRendering: if (RenderingChangesPending) ResetScene(); return;
                case FlatCommand.Reset: ResetScene(); return;
                default: return;
            }
            settings.Apply(pipeline); settings.Save(); input.Cancel(); layoutDirty = true;
            Debug.Log(Prefix + "quality: FSR1=" + settings.Fsr + "; scale=" + (settings.Fsr ? settings.RenderScale : 1f)
                + "; sharpness=" + settings.Sharpness + "; screen=" + size.x + "x" + size.y);
        }

        private void SaveRenderingPreference()
        {
            // Buffer layouts and the native route belong to the current world until it has drained.
            settings.Save(); input.Cancel(); layoutDirty = true;
        }

        private void ShiftStick(Vector2 delta)
        {
            bool portrait = size.y > size.x;
            settings.SetStickPosition(portrait, settings.GetStickPosition(portrait) + delta);
            SaveStickPosition();
        }
        private void SaveStickPosition()
        {
            input.Cancel(); layoutDirty = true;
            UpdateLayout(); // Constrain the complete circle to the safe area before displaying and saving its position.
            settings.Save();
        }

        private void CreateVisuals()
        {
            visualRoot = new GameObject("Touch slash effects");
            DontDestroyOnLoad(visualRoot);
            slashMaterial = new Material(Resources.Load<Shader>("AndroidFlatSlash"));
            var meshSource = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad = meshSource.GetComponent<MeshFilter>().sharedMesh;
            Destroy(meshSource);
            for (int i = 0; i < visuals.Length; i++)
            {
                var go = new GameObject("Slash " + i, typeof(MeshFilter), typeof(MeshRenderer));
                go.transform.SetParent(visualRoot.transform, false);
                go.GetComponent<MeshFilter>().sharedMesh = quad;
                var renderer = go.GetComponent<MeshRenderer>();
                renderer.sharedMaterial = slashMaterial;
                renderer.shadowCastingMode = ShadowCastingMode.Off; renderer.receiveShadows = false;
                renderer.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                visuals[i] = go.transform; visualRenderers[i] = renderer;
                go.SetActive(false);
            }
        }

        private void SyncVisuals(double now)
        {
            for (int i = 0; i < visuals.Length; i++)
            {
                if (i >= slash.WaveCount || !slash.TryGetWave(i, out double latchedAt, out Plane plane, out _, out _,
                    out Vector3 span, out float width, out _, out _, out Vector3 a, out Vector3 b))
                { if (visuals[i].gameObject.activeSelf) visuals[i].gameObject.SetActive(false); continue; }
                Vector3 up = Vector3.Cross(plane.normal, span).normalized;
                visuals[i].SetPositionAndRotation((a + b) * .5f,
                    Quaternion.LookRotation(plane.normal, up) * Quaternion.AngleAxis(8, Vector3.right));
                visuals[i].localScale = new Vector3(width, .35f, 1);
                visualProperties.SetColor(ColourId, new Color(.65f, .93f, 1, Mathf.Clamp01(1f - (float)(now - latchedAt) / 1.5f)));
                visualRenderers[i].SetPropertyBlock(visualProperties);
                if (!visuals[i].gameObject.activeSelf) visuals[i].gameObject.SetActive(true);
            }
        }

        private void ResetScene()
        {
            if (sceneChanging) return;
            sceneChanging = true; ready = false; prepared = false; failure = null;
            UpdateLoading();
            settings.Save(); input.Cancel(); menu = false; GamePause.SetManual(false);
            if (preparation != null) { StopCoroutine(preparation); preparation = null; }
            layoutDirty = true;
            StartCoroutine(ReloadAfterDrain());
        }
        private IEnumerator ReloadAfterDrain()
        {
            // Leave the UI/input callback, then finish worker work and GPU draws before destroying the old scene.
            yield return null;
            GamePause.SetManual(true);
            prewarm?.Abandon("phone scene reset");
            while (world != null && !world.Shutdown())
            {
                if (world.TerminationRequested) { Fail("The cut world cannot be reset after termination"); yield break; }
                yield return null;
            }
            ReleaseScene();
            ApplyRenderingSettings();
            GamePause.SetManual(false);
            SceneManager.LoadScene(SceneManager.GetActiveScene().buildIndex);
        }
        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (sceneChanging) preparation = StartCoroutine(PrepareScene());
        }
        private void OnPaused(bool paused) { input.Cancel(); if (paused) settings.Save(); }
        private void OnApplicationFocus(bool focus) { input.Cancel(); if (!Application.isEditor) GamePause.SetFocusLost(!focus); }
        private void OnApplicationPause(bool paused) { input.Cancel(); GamePause.SetSystem(paused); }
        private void ReleaseScene()
        {
            prewarm?.Abandon("phone scene left"); prewarm = null;
            if (pipeline != null) { if (QualitySettings.renderPipeline == pipeline) QualitySettings.renderPipeline = previousPipeline; Destroy(pipeline); pipeline = null; }
            if (visualRoot != null) Destroy(visualRoot);
            if (slashMaterial != null) Destroy(slashMaterial);
            slash = null; hits = null;
        }
        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded; GamePause.PausedChanged -= OnPaused;
            if (enabledEnhancedTouch) EnhancedTouchSupport.Disable();
            ReleaseScene(); view.Destroy();
            Debug.Log(Prefix + "ended; accepted=" + accepted + "; capacityRejected=" + rejected + "; prepared=" + prepared);
        }
    }
}
