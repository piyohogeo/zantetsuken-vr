using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.XR.Management;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Management;
using UnityEngine.Rendering.Universal;
using Zantetsu.Core;
using Zantetsu.Core.Input;
using Zantetsu.Core.Slash;
using Zantetsu.PhysicsCut;
using Zantetsu.Sandbox;
using Object = UnityEngine.Object;

namespace Zantetsu.EditorTools.AndroidFlat
{
    /// <summary>
    /// Batch integration smoke: real player Update, Input System mouse-to-touch recognition, shared cut world,
    /// full Game View captures including overlay UI, orientation changes, pause and scene reset.
    /// Run separately, without -quit: -executeMethod Zantetsu.EditorTools.AndroidFlat.AndroidFlatSmoke.Run -zantetsuAndroidFlat
    /// [-zantetsuAndroidFlatSmokeOut &lt;directory&gt;]. This is Editor verification, not a device performance test.
    /// </summary>
    [InitializeOnLoad]
    public static class AndroidFlatSmoke
    {
        private const string Key = "Zantetsu.AndroidFlatSmoke.";
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static int stage, frame, acceptedBefore, playerId, canvasId, worldId;
        private static double stageAt, deadline, waitDeadline, pausedClock;
        private static string firstError, screenshot;
        private static MonoBehaviour player;
        private static MobPlanPlayerInput locomotion;
        private static Mouse mouse, oldMouse;
        private static bool oldMouseEnabled, resetReentryChecked, tapReleased;
        private static string tapCommand;
        private static int tapNextStage;
        private static Quaternion beforeRotation;
        private static long waveId;
        private static Vector3 waveMidpoint, travel;
        private static float waveWidth;
        private static EditorWindow gameView;
        private static int shutdownFrame = -1, releasedFrame = -1;
        private static double shutdownBegan;

        [Serializable] private sealed class Scenes { public string[] paths; public bool[] enabled; }
        [Serializable] private sealed class SavedAsset { public string path, json, fileBase64; public long localId; }
        [Serializable] private sealed class XrAssets { public SavedAsset[] assets; }
        [Serializable] private sealed class SavedPref { public string key; public bool present, integer; public float number; public int integerValue; }
        [Serializable] private sealed class Prefs { public SavedPref[] entries; }
        private static readonly Vector2 DefaultStickPosition = new Vector2(.15f, .30f);
        private static readonly Vector2 EditedLandscapeStickPosition = new Vector2(.16f, .31f);
        private static readonly Vector2 EditedPortraitStickPosition = new Vector2(.14f, .29f);

        static AndroidFlatSmoke()
        {
            if (!SessionState.GetBool(Key + "running", false)) return;
            EditorApplication.update += Update;
            Application.logMessageReceived += OnLog;
        }

        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) throw new InvalidOperationException("Smoke must start in Edit Mode");
            if (!PlayerPlatform.IsAndroidFlat) throw new InvalidOperationException("Pass -zantetsuAndroidFlat to enable the phone runtime in the Editor");
            if (!File.Exists(AndroidFlatBuild.ScenePath)) throw new FileNotFoundException("Configure the phone scene first", AndroidFlatBuild.ScenePath);
            string[] args = Environment.GetCommandLineArgs(); int index = Array.IndexOf(args, "-zantetsuAndroidFlatSmokeOut");
            string output = index >= 0 && index + 1 < args.Length ? args[index + 1] : "Logs/android-flat-smoke";
            output = Path.GetFullPath(output); Directory.CreateDirectory(output);
            SessionState.SetString(Key + "output", output);
            SessionState.SetString(Key + "scenes", JsonUtility.ToJson(new Scenes {
                paths = EditorBuildSettings.scenes.Select(s => s.path).ToArray(),
                enabled = EditorBuildSettings.scenes.Select(s => s.enabled).ToArray()
            }));
            SessionState.SetBool(Key + "finishing", false);
            SessionState.SetBool(Key + "savedGameView", false);
            SessionState.EraseString(Key + "xrSettings");
            SessionState.EraseInt(Key + "landscape"); SessionState.EraseInt(Key + "portrait");
            SessionState.SetBool(Key + "running", true);
            File.WriteAllText(Path.Combine(output, "smoke.txt"), "Android flat Editor smoke " + DateTime.Now.ToString("o") + "\n");
            try
            {
                SavePlayerPrefsForSmoke();
                EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(AndroidFlatBuild.ScenePath, true) };
                EditorSceneManager.OpenScene(AndroidFlatBuild.ScenePath, OpenSceneMode.Single);
                SetGameSize(1280, 720);
                DisableEditorXr();
                EditorApplication.update -= Update; EditorApplication.update += Update;
                Application.logMessageReceived -= OnLog; Application.logMessageReceived += OnLog;
                EditorApplication.EnterPlaymode();
            }
            catch (Exception error) { Finish(false, error.ToString()); }
        }

        private static string Output => SessionState.GetString(Key + "output", "Logs/android-flat-smoke");
        private static void Record(string message)
        {
            File.AppendAllText(Path.Combine(Output, "smoke.txt"), "frame=" + Time.frameCount + " stage=" + stage + " " + message + "\n");
        }
        private static void OnLog(string message, string trace, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            {
                firstError ??= type + ": " + message; Record(type + ": " + message + "\n" + trace);
                if (SessionState.GetBool(Key + "finishing", false)) SessionState.SetInt(Key + "exit", 1);
            }
        }
        private static void Next(int next)
        { stage = next; frame = Time.frameCount; stageAt = EditorApplication.timeSinceStartup; }
        private static bool Frames(int count) => Time.frameCount - frame >= count;
        private static void Require(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        private static void Update()
        {
            if (!SessionState.GetBool(Key + "running", false)) { EditorApplication.update -= Update; return; }
            if (SessionState.GetBool(Key + "finishing", false))
            {
                if (EditorApplication.isPlaying) { CarryShutdown(); return; }
                if (EditorApplication.isPlayingOrWillChangePlaymode) return;
                RestoreAndExit(); return;
            }
            if (!EditorApplication.isPlaying) return;
            try
            {
                if (deadline == 0) { deadline = EditorApplication.timeSinceStartup + 240; waitDeadline = GamePause.RealtimeSinceStartupAsDouble + 120; }
                Require(EditorApplication.timeSinceStartup <= deadline, "Smoke exceeded the 240-second wall-clock deadline");
                Require(firstError == null, firstError ?? "");
                if (gameView == null) gameView = EditorWindow.GetWindow(typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView"));
                gameView.Repaint();
                if (player != null) Require(Read<string>(player, "failure") == null, "Phone preparation failed: " + Read<string>(player, "failure"));
                switch (stage)
                {
                    case 0:
                        player = Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                            .FirstOrDefault(p => p != null && p.GetType().FullName == "Zantetsu.AndroidFlat.AndroidFlatPlayer");
                        if (player == null || !Read<bool>(player, "ready"))
                        { Require(GamePause.RealtimeSinceStartupAsDouble < waitDeadline, "Player/world/crowd did not become ready in 120 game-clock seconds"); return; }
                        Require(Screen.width == 1280 && Screen.height == 720, "Landscape Game View size was not applied: " + Screen.width + "x" + Screen.height);
                        Require(!GamePause.IsPaused, "Ready player is unexpectedly paused: " + GamePause.Reasons);
                        Require(XRGeneralSettings.Instance == null || XRGeneralSettings.Instance.Manager == null
                            || XRGeneralSettings.Instance.Manager.activeLoader == null, "An XR loader started during the non-VR Editor smoke");
                        locomotion = Object.FindAnyObjectByType<MobPlanPlayerInput>();
                        Require(locomotion != null && locomotion.player != null, "Missing locomotion player");
                        var world = Object.FindAnyObjectByType<CutWorldRoot>();
                        Require(world != null && world.IsReady, "World is not ready");
                        var crowd = Object.FindAnyObjectByType<MobPlanCrowd>();
                        Require(crowd != null && crowd.PreparationEnded, "Crowd is not prepared");
                        Require(Camera.main != null && Camera.main.stereoTargetEye == StereoTargetEyeMask.None
                            && !Camera.main.GetUniversalAdditionalCameraData().allowXRRendering, "The camera is not configured for mono rendering");
                        float landscapeAspect = (float)Screen.width / Screen.height;
                        float expectedLandscapeVerticalFov = 2f * Mathf.Atan(Mathf.Tan(45f * Mathf.Deg2Rad) / landscapeAspect) * Mathf.Rad2Deg;
                        float actualLandscapeHorizontalFov = 2f * Mathf.Atan(Mathf.Tan(Camera.main.fieldOfView * .5f * Mathf.Deg2Rad) * landscapeAspect) * Mathf.Rad2Deg;
                        Require(Mathf.Abs(Camera.main.fieldOfView - expectedLandscapeVerticalFov) < .05f
                            && Mathf.Abs(actualLandscapeHorizontalFov - 90f) < .05f,
                            "Landscape long-side FOV was not applied; expected horizontal 90, vertical " + expectedLandscapeVerticalFov + ", actual vertical " + Camera.main.fieldOfView);
                        Record("PASS landscape long-side FOV: horizontal=" + actualLandscapeHorizontalFov.ToString("F2")
                            + "; vertical=" + Camera.main.fieldOfView.ToString("F4"));
                        var quality = Read<UniversalRenderPipelineAsset>(player, "pipeline");
                        var settings = Read<object>(player, "settings");
                        Require(quality != null && !quality.supportsHDR && quality.msaaSampleCount == 4, "Quest-equivalent HDR/MSAA settings were not applied");
                        bool fsr = Read<bool>(settings, "Fsr");
                        float scale = fsr ? Read<float>(settings, "RenderScale") : 1f;
                        Require(Mathf.Abs(quality.renderScale - scale) < .001f
                            && (!fsr || quality.upscalingFilter == UpscalingFilterSelection.FSR), "FSR1/render-scale runtime settings were not applied");
                        Record("PASS mono camera; HDR off; MSAA4; FSR1=" + fsr + "; renderScale=" + quality.renderScale);
                        playerId = player.GetInstanceID(); worldId = world.GetInstanceID();
                        canvasId = VerifyOverlay().GetInstanceID();
                        oldMouse = Mouse.current; oldMouseEnabled = oldMouse != null && oldMouse.enabled;
                        if (oldMouseEnabled) InputSystem.DisableDevice(oldMouse);
                        mouse = InputSystem.AddDevice<Mouse>("AndroidFlatSmokeMouse"); mouse.MakeCurrent();
                        QueueMouse(Vector2.zero, false);
                        Record("PASS ready; world=" + worldId + "; crowd prepared; overlay=" + canvasId + "; screen=1280x720");
                        Next(1); break;
                    case 1:
                        if (!Frames(2)) return;
                        var input = Read<FlatTouchInput>(player, "input");
                        Require(!input.AwaitingRelease, "Released mouse did not clear touch cancellation");
                        beforeRotation = locomotion.player.transform.rotation;
                        QueueMouse(input.StickCentre, true); Next(2); break;
                    case 2:
                        if (!Frames(2)) return;
                        input = Read<FlatTouchInput>(player, "input");
                        QueueMouse(input.StickCentre + new Vector2(input.StickRadius * .9f, input.StickRadius * .3f), true);
                        Next(3); break;
                    case 3:
                        if (!Frames(8)) return;
                        QueueMouse(Read<FlatTouchInput>(player, "input").StickCentre, false); Next(4); break;
                    case 4:
                        if (!Frames(2)) return;
                        Require(Quaternion.Angle(beforeRotation, locomotion.player.transform.rotation) > .1f, "Joystick did not rotate the real player");
                        Require(Read<FlatTouchInput>(player, "input").Stick == Vector2.zero, "Joystick release did not stop movement");
                        Record("PASS real input joystick rotated player; release cleared stick");
                        acceptedBefore = Read<int>(player, "accepted");
                        QueueMouse(new Vector2(Screen.width * .30f, Screen.height * .58f), true); Next(5); break;
                    case 5:
                        if (!Frames(2)) return;
                        QueueMouse(new Vector2(Screen.width * .72f, Screen.height * .66f), true); Next(6); break;
                    case 6:
                        if (!Frames(2)) return;
                        Require(Read<int>(player, "accepted") == acceptedBefore + 1, "A real fast mouse flick did not produce exactly one Slash");
                        var core = Read<SlashWaveCore>(player, "slash");
                        Require(core.WaveCount > 0, "Accepted flick produced no flying wave");
                        waveId = core.SlashIdAt(core.WaveCount - 1);
                        ReadWave(core, out waveMidpoint, out waveWidth, out travel);
                        QueueMouse(new Vector2(Screen.width * .78f, Screen.height * .70f), true);
                        Next(7); break;
                    case 7:
                        if (!Frames(2)) return;
                        core = Read<SlashWaveCore>(player, "slash");
                        ReadWave(core, out Vector3 midpoint, out float width, out _);
                        Require(Vector3.Dot(midpoint - waveMidpoint, travel) > .001f, "Slash endpoints did not advance along its latched direction");
                        Require(width > waveWidth, "Slash width did not grow with flight distance");
                        Require(Read<int>(player, "accepted") == acceptedBefore + 1, "The same contact fired more than once");
                        Record("PASS real flick latched one Slash; endpoint travel=" + Vector3.Dot(midpoint - waveMidpoint, travel).ToString("F4")
                            + "; width=" + waveWidth.ToString("F4") + "->" + width.ToString("F4"));
                        QueueMouse(Vector2.zero, false); Capture("landscape-play.png", 8); break;
                    case 8:
                        if (!ScreenshotReady(1280, 720)) return;
                        TapControl("Menu", 30); break;
                    case 9:
                        if (Screen.width != 720 || Screen.height != 1280)
                        { Require(EditorApplication.timeSinceStartup - stageAt < 15, "Portrait Game View did not resize"); return; }
                        if (!Frames(3)) return;
                        VerifyOverlay();
                        Require(Camera.main != null && Mathf.Abs(Camera.main.fieldOfView - 90f) < .05f, "Portrait long-side vertical FOV of 90 degrees was not applied");
                        Record("PASS portrait layout and long-side vertical FOV=" + Camera.main.fieldOfView.ToString("F2"));
                        Capture("portrait-play.png", 10); break;
                    case 10:
                        if (!ScreenshotReady(720, 1280)) return;
                        QueueMouse(ControlRect(0).center, true); Next(11); break;
                    case 11:
                        if (!Frames(2)) return;
                        QueueMouse(ControlRect(0).center, false); Next(12); break;
                    case 12:
                        if (!Frames(2)) return;
                        Require(Read<bool>(player, "menu") && GamePause.IsPaused, "Touching Menu did not pause the game");
                        pausedClock = GamePause.UnscaledTimeAsDouble;
                        Capture("portrait-menu.png", 13); break;
                    case 13:
                        if (!ScreenshotReady(720, 1280)) return;
                        Require(Math.Abs(GamePause.UnscaledTimeAsDouble - pausedClock) < .000001, "The game's flight clock advanced while the menu was open");
                        Record("PASS actual Menu touch paused the game and its clock");
                        QueueMouse(ControlRect(9).center, true); Next(14); break;
                    case 14:
                        if (!Frames(2)) return;
                        // A fully drained world can reload within one coroutine turn. Observe the pause transition
                        // synchronously so the lifecycle regression does not depend on an Editor update landing in it.
                        GamePause.PausedChanged -= OnResetPaused;
                        GamePause.PausedChanged += OnResetPaused;
                        QueueMouse(ControlRect(9).center, false);
                        waitDeadline = GamePause.RealtimeSinceStartupAsDouble + 120;
                        Next(15); break;
                    case 15:
                        if (!resetReentryChecked && player != null && Read<bool>(player, "sceneChanging") && GamePause.IsPaused)
                            CheckResetReentry();
                        if (player == null || !Read<bool>(player, "ready"))
                        { Require(GamePause.RealtimeSinceStartupAsDouble < waitDeadline, "Reset scene did not become ready again in 120 game-clock seconds"); return; }
                        if (!Frames(2)) return;
                        Require(player.GetInstanceID() == playerId, "Reset replaced the persistent phone runtime");
                        Require(VerifyOverlay().GetInstanceID() == canvasId, "Reset replaced or lost the persistent touch UI");
                        world = Object.FindAnyObjectByType<CutWorldRoot>();
                        Require(world != null && world.IsReady && world.GetInstanceID() != worldId, "Reset did not replace and prepare the cut world");
                        Require(!GamePause.IsPaused && !Read<bool>(player, "menu"), "Reset left the pause/menu active");
                        Require(resetReentryChecked, "Reset completed without exercising UI reentry during its paused drain");
                        const float expectedPortraitFov = 90f;
                        Require(Camera.main != null && Mathf.Abs(Camera.main.fieldOfView - expectedPortraitFov) < .05f,
                            "Reset camera did not reapply portrait long-side vertical FOV; expected " + expectedPortraitFov + ", actual " + (Camera.main != null ? Camera.main.fieldOfView : 0));
                        Require(Read<SlashWaveCore>(player, "slash").WaveCount == 0, "Reset kept a wave from the old scene");
                        VerifyStickPositions(DefaultStickPosition, EditedLandscapeStickPosition, true);
                        Record("PASS joystick placement survived scene Reset, including the independent saved landscape setting");
                        Record("PASS actual Reset scene touch prepared a new world; one persistent UI/runtime; no stale waves or pause; portraitFov=" + Camera.main.fieldOfView.ToString("F2"));
                        Capture("portrait-after-reset.png", 16); break;
                    case 16:
                        if (!ScreenshotReady(720, 1280)) return;
                        Finish(true, "All integration checks passed (Editor render/input/settings/reset; device performance not measured)"); break;
                    case 30:
                        Require(Read<bool>(player, "menu") && GamePause.IsPaused, "Menu did not pause before joystick-position editing");
                        VerifyStickPositions(DefaultStickPosition, DefaultStickPosition, true);
                        TapControl("StickPosition", 31); break;
                    case 31:
                        VerifyPositionPage();
                        TapControl("StickRight", 32); break;
                    case 32:
                        VerifyStickPositions(DefaultStickPosition, new Vector2(.16f, .30f), true);
                        TapControl("StickUp", 33); break;
                    case 33:
                        VerifyPositionPage();
                        VerifyStickPositions(DefaultStickPosition, EditedLandscapeStickPosition, true);
                        Record("PASS real landscape joystick-position taps changed x/y by 1% and saved only landscape");
                        Capture("landscape-stick-position.png", 34); break;
                    case 34:
                        if (!ScreenshotReady(1280, 720)) return;
                        SetGameSize(720, 1280); Next(35); break;
                    case 35:
                        if (!GameSizeReady(720, 1280)) return;
                        VerifyPositionPage();
                        VerifyStickPositions(DefaultStickPosition, EditedLandscapeStickPosition, true);
                        TapControl("StickLeft", 36); break;
                    case 36:
                        VerifyStickPositions(new Vector2(.14f, .30f), EditedLandscapeStickPosition, true);
                        TapControl("StickDown", 37); break;
                    case 37:
                        VerifyPositionPage();
                        VerifyStickPositions(EditedPortraitStickPosition, EditedLandscapeStickPosition, true);
                        Record("PASS real portrait joystick-position taps changed x/y by 1% independently and persisted both orientations");
                        Capture("portrait-stick-position.png", 38); break;
                    case 38:
                        if (!ScreenshotReady(720, 1280)) return;
                        SetGameSize(1280, 720); Next(39); break;
                    case 39:
                        if (!GameSizeReady(1280, 720)) return;
                        VerifyPositionPage();
                        VerifyStickPositions(EditedPortraitStickPosition, EditedLandscapeStickPosition, true);
                        Record("PASS rotating back restored the edited landscape position while retaining portrait settings");
                        SetGameSize(720, 1280); Next(40); break;
                    case 40:
                        if (!GameSizeReady(720, 1280)) return;
                        VerifyPositionPage();
                        VerifyStickPositions(EditedPortraitStickPosition, EditedLandscapeStickPosition, true);
                        TapControl("StickReset", 41); break;
                    case 41:
                        VerifyStickPositions(DefaultStickPosition, EditedLandscapeStickPosition, true);
                        Record("PASS joystick Reset restored current portrait defaults and preserved the saved landscape position");
                        TapControl("Back", 42); break;
                    case 42:
                        Require(Read<bool>(player, "menu") && GamePause.IsPaused, "Position-page Back closed the paused menu");
                        Require(!Read<bool>(Read<object>(player, "view"), "layoutPage"), "Position-page Back did not return to the main menu");
                        TapControl("Resume", 43); break;
                    case 43:
                        Require(!Read<bool>(player, "menu") && !GamePause.IsPaused, "Resume did not leave joystick-position settings safely");
                        VerifyStickPositions(DefaultStickPosition, EditedLandscapeStickPosition, true);
                        Next(9); break;
                    case 100:
                        if (!Frames(2)) return;
                        if (!tapReleased)
                        {
                            QueueMouse(ControlRect(tapCommand).center, false);
                            tapReleased = true; frame = Time.frameCount;
                        }
                        else Next(tapNextStage);
                        break;
                }
            }
            catch (Exception error) { Finish(false, error.ToString()); }
        }

        private static void OnResetPaused(bool paused)
        {
            if (!paused || resetReentryChecked || player == null || !Read<bool>(player, "sceneChanging")) return;
            GamePause.PausedChanged -= OnResetPaused;
            try { CheckResetReentry(); }
            catch (Exception error)
            {
                // Do not stop the coroutine or destroy the world from inside its pause callback. The next ordinary
                // Editor update turns this assertion into the normal failing result and drains everything safely.
                firstError ??= "Reset UI reentry regression: " + error;
                Record("FAIL " + firstError);
            }
        }

        private static void CheckResetReentry()
        {
            bool wasMenu = Read<bool>(player, "menu"), wasManual = GamePause.Manual;
            Require(wasManual, "Reset drain is not holding its manual pause");
            var command = player.GetType().GetMethod("Command", AnyInstance);
            Type commandType = command.GetParameters()[0].ParameterType;
            foreach (string name in new[] { "Menu", "Resume" })
            {
                command.Invoke(player, new[] { Enum.Parse(commandType, name) });
                Require(Read<bool>(player, "menu") == wasMenu && GamePause.Manual == wasManual,
                    "Command(" + name + ") changed menu/pause during the scene-reset drain");
            }
            player.GetType().GetMethod("ToggleMenu", AnyInstance).Invoke(player, null);
            Require(Read<bool>(player, "menu") == wasMenu && GamePause.Manual == wasManual,
                "ToggleMenu changed menu/pause during the scene-reset drain");
            resetReentryChecked = true;
            Record("PASS Menu/Resume commands and ToggleMenu ignored during paused scene-reset drain");
        }

        private static Canvas VerifyOverlay()
        {
            var canvases = Object.FindObjectsByType<Canvas>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Where(c => c.name == "Android touch controls").ToArray();
            Require(canvases.Length == 1, "Expected exactly one active phone touch canvas; found " + canvases.Length);
            Require(canvases[0].renderMode == RenderMode.ScreenSpaceOverlay, "Phone UI is not output-resolution overlay UI");
            var input = Read<FlatTouchInput>(player, "input");
            Rect safe = Screen.safeArea;
            Require(input.StickCentre.x - input.StickRadius >= safe.xMin - .1f
                && input.StickCentre.x + input.StickRadius <= safe.xMax + .1f
                && input.StickCentre.y - input.StickRadius >= safe.yMin - .1f
                && input.StickCentre.y + input.StickRadius <= safe.yMax + .1f,
                "The full joystick circle is outside the screen safe area: centre=" + input.StickCentre + "; radius=" + input.StickRadius + "; safe=" + safe);
            Require(Screen.safeArea.Contains(ControlRect(0).center), "Menu button is outside the screen safe area");
            return canvases[0];
        }
        private static Rect ControlRect(int index)
        {
            object view = Read<object>(player, "view");
            Array controls = Read<Array>(view, "controls");
            return Read<Rect>(controls.GetValue(index), "rect");
        }
        private static object Control(string command)
        {
            Array controls = Read<Array>(Read<object>(player, "view"), "controls");
            foreach (object control in controls)
                if (control != null && string.Equals(Read<object>(control, "command").ToString(), command, StringComparison.Ordinal)) return control;
            throw new InvalidOperationException("Missing phone control " + command);
        }
        private static Rect ControlRect(string command) => Read<Rect>(Control(command), "rect");
        private static void TapControl(string command, int next)
        {
            Require(Read<Component>(Control(command), "image").gameObject.activeInHierarchy, "Cannot tap hidden phone control " + command);
            tapCommand = command; tapNextStage = next; tapReleased = false;
            QueueMouse(ControlRect(command).center, true); Next(100);
        }
        private static bool GameSizeReady(int width, int height)
        {
            if (Screen.width != width || Screen.height != height)
            { Require(EditorApplication.timeSinceStartup - stageAt < 15, "Game View did not resize to " + width + "x" + height); return false; }
            return Frames(3);
        }
        private static void VerifyPositionPage()
        {
            Require(Read<bool>(player, "menu") && GamePause.IsPaused, "Joystick-position editing did not keep the game paused");
            Require(Read<bool>(Read<object>(player, "view"), "layoutPage"), "Joystick-position page is not open");
            Require(Read<Component>(Control("StickRight"), "image").gameObject.activeInHierarchy, "Joystick-position buttons are not visible");
            Require(Read<int>(player, "accepted") == acceptedBefore + 1, "Joystick-position taps or rotation generated a Slash");
            VerifyOverlay();
        }
        private static Vector2 StickPosition(object settings, bool portrait) =>
            (Vector2)settings.GetType().GetMethod("GetStickPosition", AnyInstance).Invoke(settings, new object[] { portrait });
        private static void VerifyStickPositions(Vector2 portrait, Vector2 landscape, bool verifySaved)
        {
            object settings = Read<object>(player, "settings");
            Require(Vector2.Distance(StickPosition(settings, true), portrait) < .00001f, "Unexpected portrait joystick position");
            Require(Vector2.Distance(StickPosition(settings, false), landscape) < .00001f, "Unexpected landscape joystick position");
            var input = Read<FlatTouchInput>(player, "input");
            Rect safe = Screen.safeArea;
            Vector2 current = Screen.height > Screen.width ? portrait : landscape;
            Vector2 expected = new Vector2(safe.x + safe.width * current.x, safe.y + safe.height * current.y);
            expected.x = Mathf.Clamp(expected.x, safe.xMin + input.StickRadius, safe.xMax - input.StickRadius);
            expected.y = Mathf.Clamp(expected.y, safe.yMin + input.StickRadius, safe.yMax - input.StickRadius);
            Require(Vector2.Distance(input.StickCentre, expected) < .1f,
                "Visible joystick did not use the current orientation's saved position: expected=" + expected + "; actual=" + input.StickCentre);
            VerifyOverlay();
            if (!verifySaved) return;
            object loaded = Activator.CreateInstance(settings.GetType());
            loaded.GetType().GetMethod("Load", AnyInstance).Invoke(loaded, null);
            Require(Vector2.Distance(StickPosition(loaded, true), portrait) < .00001f
                && Vector2.Distance(StickPosition(loaded, false), landscape) < .00001f,
                "A fresh settings Load did not retain independent portrait/landscape positions");
        }

        private static T Read<T>(object target, string name)
        {
            var field = target.GetType().GetField(name, AnyInstance);
            if (field == null) throw new MissingFieldException(target.GetType().FullName, name);
            return (T)field.GetValue(target);
        }
        private static void QueueMouse(Vector2 position, bool down)
        { InputSystem.QueueStateEvent(mouse, new MouseState { position = position }.WithButton(MouseButton.Left, down)); }
        private static void ReadWave(SlashWaveCore core, out Vector3 midpoint, out float width, out Vector3 direction)
        {
            for (int i = 0; i < core.WaveCount; i++)
                if (core.SlashIdAt(i) == waveId && core.TryGetWave(i, out _, out _, out _, out direction, out _, out width, out _, out _, out Vector3 a, out Vector3 b))
                { midpoint = (a + b) * .5f; Require(float.IsFinite(width) && width > 0, "Wave has an invalid width"); return; }
            throw new InvalidOperationException("The accepted wave disappeared before its flight could be observed");
        }
        private static void Capture(string file, int next)
        {
            screenshot = Path.Combine(Output, file);
            if (File.Exists(screenshot)) File.Delete(screenshot);
            ScreenCapture.CaptureScreenshot(screenshot);
            Next(next);
        }
        private static bool ScreenshotReady(int width, int height)
        {
            if (!Frames(3) || !File.Exists(screenshot))
            { Require(EditorApplication.timeSinceStartup - stageAt < 15, "Full Game View screenshot was not written: " + screenshot); return false; }
            byte[] png = File.ReadAllBytes(screenshot);
            if (png.Length < 24) return false;
            int actualWidth = ReadBigEndian(png, 16), actualHeight = ReadBigEndian(png, 20);
            Require(actualWidth == width && actualHeight == height, "Screenshot size is " + actualWidth + "x" + actualHeight + ", expected " + width + "x" + height);
            Record("PASS screenshot " + Path.GetFileName(screenshot) + " " + actualWidth + "x" + actualHeight + "; bytes=" + png.Length);
            return true;
        }
        private static int ReadBigEndian(byte[] data, int at) => data[at] << 24 | data[at + 1] << 16 | data[at + 2] << 8 | data[at + 3];

        private static object GameSizes(out object group)
        {
            Type type = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameViewSizes", true);
            object sizes = type.BaseType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
            object groupType = type.GetProperty("currentGroupType", AnyInstance).GetValue(sizes);
            group = type.GetMethod("GetGroup", AnyInstance).Invoke(sizes, new[] { groupType });
            return sizes;
        }
        private static void SetGameSize(int width, int height)
        {
            gameView = EditorWindow.GetWindow(typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView"));
            GameSizes(out object group);
            Type groupType = group.GetType();
            int builtins = (int)groupType.GetMethod("GetBuiltinCount", AnyInstance).Invoke(group, null);
            int customs = (int)groupType.GetMethod("GetCustomCount", AnyInstance).Invoke(group, null);
            PropertyInfo selected = gameView.GetType().GetProperty("selectedSizeIndex", AnyInstance);
            if (!SessionState.GetBool(Key + "savedGameView", false))
            {
                SessionState.SetInt(Key + "selected", (int)selected.GetValue(gameView));
                SessionState.SetInt(Key + "customCount", customs);
                SessionState.SetBool(Key + "savedGameView", true);
            }
            string sizeKey = Key + (width > height ? "landscape" : "portrait");
            int target = SessionState.GetInt(sizeKey, -1);
            if (target < 0)
            {
                Type sizeType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameViewSize", true);
                Type kind = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameViewSizeType", true);
                object size = Activator.CreateInstance(sizeType, AnyInstance, null,
                    new object[] { Enum.Parse(kind, "FixedResolution"), width, height, "Android phone smoke " + width + "x" + height }, null);
                groupType.GetMethod("AddCustomSize", AnyInstance).Invoke(group, new[] { size });
                target = builtins + customs; SessionState.SetInt(sizeKey, target);
            }
            selected.SetValue(gameView, target);
            gameView.Show(); gameView.Focus(); gameView.Repaint();
        }

        private static void DisableEditorXr()
        {
            // Play Mode uses the Standalone manager even when Android is the selected build target. Snapshot both
            // target groups by asset path/local ID, so restoration survives Enter/Exit Play Mode domain reloads.
            var saved = new List<SavedAsset>();
            var seen = new HashSet<int>();
            void Save(Object asset)
            {
                if (asset == null || !seen.Add(asset.GetInstanceID())) return;
                if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out string guid, out long localId))
                    throw new InvalidOperationException("XR settings are not a persistent asset: " + asset.name);
                string path = AssetDatabase.GUIDToAssetPath(guid);
                saved.Add(new SavedAsset { path = path, localId = localId, json = EditorJsonUtility.ToJson(asset),
                    fileBase64 = File.Exists(path) ? Convert.ToBase64String(File.ReadAllBytes(path)) : null });
                // Persist each snapshot before changing any object; a later failure can restore partial work too.
                SessionState.SetString(Key + "xrSettings", JsonUtility.ToJson(new XrAssets { assets = saved.ToArray() }));
            }
            foreach (string path in new[] { "ProjectSettings/GraphicsSettings.asset", "ProjectSettings/QualitySettings.asset", "ProjectSettings/DynamicsManager.asset" })
            {
                Object asset = AssetDatabase.LoadAllAssetsAtPath(path).FirstOrDefault();
                if (asset == null) throw new InvalidOperationException("Cannot snapshot Editor project settings: " + path);
                saved.Add(new SavedAsset { path = path, localId = 0, json = EditorJsonUtility.ToJson(asset),
                    fileBase64 = Convert.ToBase64String(File.ReadAllBytes(path)) });
                SessionState.SetString(Key + "xrSettings", JsonUtility.ToJson(new XrAssets { assets = saved.ToArray() }));
            }
            foreach (BuildTargetGroup group in new[] { BuildTargetGroup.Standalone, BuildTargetGroup.Android })
            {
                var general = XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(group);
                if (general == null) continue;
                Save(general); general.InitManagerOnStart = false; EditorUtility.SetDirty(general);
                if (general.Manager == null) continue;
                Save(general.Manager);
                general.Manager.automaticLoading = false; general.Manager.automaticRunning = false;
                if (!general.Manager.TrySetLoaders(new List<XRLoader>())) throw new InvalidOperationException("Cannot disable " + group + " XR loaders for Editor smoke");
                EditorUtility.SetDirty(general.Manager);
            }
            AssetDatabase.SaveAssets();
            Record("Editor XR startup disabled for Android and Standalone; settings snapshots=" + saved.Count);
        }
        private static void RestoreEditorXr()
        {
            string text = SessionState.GetString(Key + "xrSettings", "");
            if (string.IsNullOrEmpty(text)) return;
            var saved = JsonUtility.FromJson<XrAssets>(text);
            var failures = new List<Exception>();
            foreach (var asset in saved.assets.Reverse())
            {
                try
                {
                    Object target = asset.localId == 0 ? AssetDatabase.LoadAllAssetsAtPath(asset.path).FirstOrDefault()
                        : AssetDatabase.LoadAllAssetsAtPath(asset.path).FirstOrDefault(candidate =>
                            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(candidate, out string _, out long id) && id == asset.localId);
                    if (target == null) throw new InvalidOperationException("Cannot restore XR settings asset " + asset.path + ":" + asset.localId);
                    EditorJsonUtility.FromJsonOverwrite(asset.json, target); EditorUtility.SetDirty(target);
                }
                catch (Exception error) { failures.Add(error); }
            }
            try { AssetDatabase.SaveAssets(); }
            catch (Exception error) { failures.Add(error); }
            // Native project-setting objects can expose only the active platform's quality levels to EditorJsonUtility.
            // Restore their cached values above, then put back the exact on-disk files after the last SaveAssets.
            foreach (var asset in saved.assets.GroupBy(a => a.path).Select(group => group.First()))
            {
                if (string.IsNullOrEmpty(asset.fileBase64)) continue;
                try { File.WriteAllBytes(asset.path, Convert.FromBase64String(asset.fileBase64)); }
                catch (Exception error) { failures.Add(error); }
            }
            if (failures.Count > 0) throw new AggregateException("Failed to restore temporary Editor XR/settings scope", failures);
            SessionState.EraseString(Key + "xrSettings");
            Record("Restored Android/Standalone XR plus Graphics/Quality/Dynamics settings after Play Mode");
        }

        private static void Finish(bool success, string result)
        {
            GamePause.PausedChanged -= OnResetPaused;
            if (SessionState.GetBool(Key + "finishing", false)) return;
            Record((success ? "PASS " : "FAIL ") + result);
            SessionState.SetBool(Key + "finishing", true); SessionState.SetInt(Key + "exit", success ? 0 : 1);
            if (EditorApplication.isPlaying)
            {
                // Keep actual rendered frames and CutWorldRoot.LateUpdate alive while it drains. Stop only the
                // phone's producer/preparation, and retire its warmup safely before ending the shared world.
                foreach (var host in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                    .Where(p => p != null && p.GetType().FullName == "Zantetsu.AndroidFlat.AndroidFlatPlayer"))
                {
                    host.StopAllCoroutines(); host.enabled = false;
                    object warmup = Read<object>(host, "prewarm");
                    warmup?.GetType().GetMethod("Abandon", AnyInstance).Invoke(warmup, new object[] { "Editor smoke ending" });
                }
                GamePause.SetManual(true);
                shutdownFrame = releasedFrame = -1; shutdownBegan = EditorApplication.timeSinceStartup;
                Record("Draining cut worlds over rendered frames before leaving Play Mode");
            }
            if (mouse != null) { InputSystem.RemoveDevice(mouse); mouse = null; }
            if (oldMouse != null && oldMouseEnabled) InputSystem.EnableDevice(oldMouse);
            if (!EditorApplication.isPlayingOrWillChangePlaymode) RestoreAndExit();
        }

        private static void CarryShutdown()
        {
            if (gameView != null) gameView.Repaint();
            if (shutdownFrame == Time.frameCount) return;
            shutdownFrame = Time.frameCount;
            try
            {
                bool allReleased = true;
                foreach (var world in Object.FindObjectsByType<CutWorldRoot>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (world.IsReleased) continue;
                    if (world.TerminationRequested) throw new InvalidOperationException("A cut world requested fatal termination while smoke was draining");
                    allReleased &= world.Shutdown();
                }
                if (allReleased)
                {
                    if (releasedFrame < 0) { releasedFrame = Time.frameCount; Record("All cut worlds released; allowing warmup retirement frames"); }
                    // A stopped prewarm can own an independent GPU fence/retirement component. Give that component
                    // normal rendered frames before its host is destroyed; never use WaitForEndOfFrame in batch.
                    if (Time.frameCount - releasedFrame >= 4) { Record("Shutdown completed safely"); EditorApplication.ExitPlaymode(); }
                    return;
                }
                if (EditorApplication.timeSinceStartup - shutdownBegan > 60)
                    throw new TimeoutException("Cut worlds did not confirm release during 60 seconds of ordinary shutdown frames");
            }
            catch (Exception error)
            {
                SessionState.SetInt(Key + "exit", 1);
                Record("FAIL fatal shutdown: " + error);
                // A failed stop is never forced to free native memory. The run is a failure; OnDestroy preserves
                // resources still in flight according to CutWorldRoot's own termination contract.
                EditorApplication.ExitPlaymode();
            }
        }
        private static void SavePlayerPrefsForSmoke()
        {
            const string prefix = "zantetsu.androidflat.";
            string[] names = { "move", "turn", "fsr", "scale", "sharpness", "stick.portrait.x", "stick.portrait.y", "stick.landscape.x", "stick.landscape.y" };
            var entries = names.Select(name => new SavedPref {
                key = prefix + name, present = PlayerPrefs.HasKey(prefix + name), integer = name == "fsr",
                number = name != "fsr" ? PlayerPrefs.GetFloat(prefix + name) : 0f,
                integerValue = name == "fsr" ? PlayerPrefs.GetInt(prefix + name) : 0
            }).ToArray();
            SessionState.SetString(Key + "playerPrefs", JsonUtility.ToJson(new Prefs { entries = entries }));
            // Only placement starts from known defaults. All preexisting settings, including absent keys, are restored.
            foreach (SavedPref entry in entries)
                if (entry.key.StartsWith(prefix + "stick.", StringComparison.Ordinal)) PlayerPrefs.DeleteKey(entry.key);
            PlayerPrefs.Save();
            Record("Saved Editor phone PlayerPrefs before Play Mode; seeded portrait/landscape joystick defaults");
        }
        private static void RestorePlayerPrefsAfterSmoke()
        {
            string saved = SessionState.GetString(Key + "playerPrefs", "");
            if (string.IsNullOrEmpty(saved)) return;
            var prefs = JsonUtility.FromJson<Prefs>(saved);
            foreach (SavedPref entry in prefs.entries)
            {
                if (!entry.present) PlayerPrefs.DeleteKey(entry.key);
                else if (entry.integer) PlayerPrefs.SetInt(entry.key, entry.integerValue);
                else PlayerPrefs.SetFloat(entry.key, entry.number);
            }
            PlayerPrefs.Save();
            foreach (SavedPref entry in prefs.entries)
            {
                Require(PlayerPrefs.HasKey(entry.key) == entry.present, "PlayerPrefs key existence was not restored: " + entry.key);
                if (!entry.present) continue;
                Require(entry.integer ? PlayerPrefs.GetInt(entry.key) == entry.integerValue : PlayerPrefs.GetFloat(entry.key).Equals(entry.number),
                    "PlayerPrefs value was not restored: " + entry.key);
            }
            SessionState.EraseString(Key + "playerPrefs");
            Record("PASS restored all nine Editor phone PlayerPrefs keys and their original existence after Play Mode");
        }

        private static void RestoreAndExit()
        {
            int code = SessionState.GetInt(Key + "exit", 1);
            try { RestorePlayerPrefsAfterSmoke(); }
            catch (Exception error) { Record("FAIL restoring smoke PlayerPrefs: " + error); code = 1; }
            try { RestoreEditorXr(); }
            catch (Exception error) { Record("FAIL restoring smoke XR settings: " + error); code = 1; }
            try
            {
                var saved = JsonUtility.FromJson<Scenes>(SessionState.GetString(Key + "scenes", "{}"));
                if (saved.paths != null) EditorBuildSettings.scenes = saved.paths.Select((path, i) => new EditorBuildSettingsScene(path, saved.enabled[i])).ToArray();
            }
            catch (Exception error) { Record("FAIL restoring smoke scene list: " + error); code = 1; }
            try
            {
                if (SessionState.GetBool(Key + "savedGameView", false))
                {
                    GameSizes(out object group);
                    int originalCount = SessionState.GetInt(Key + "customCount", 0);
                    var count = group.GetType().GetMethod("GetCustomCount", AnyInstance);
                    var remove = group.GetType().GetMethod("RemoveCustomSize", AnyInstance);
                    int builtins = (int)group.GetType().GetMethod("GetBuiltinCount", AnyInstance).Invoke(group, null);
                    gameView = EditorWindow.GetWindow(typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView"));
                    var selected = gameView.GetType().GetProperty("selectedSizeIndex", AnyInstance);
                    selected.SetValue(gameView, SessionState.GetInt(Key + "selected", 0));
                    // Unity 6000.3 RemoveCustomSize takes a TOTAL index and subtracts the builtin count internally.
                    // Its invalid-index path logs an error rather than throwing, so verify restoration explicitly too.
                    for (int i = (int)count.Invoke(group, null) - 1; i >= originalCount; i--)
                        remove.Invoke(group, new object[] { builtins + i });
                    Require((int)count.Invoke(group, null) == originalCount, "Game View custom sizes were not restored");
                    selected.SetValue(gameView, SessionState.GetInt(Key + "selected", 0));
                }
            }
            catch (Exception error) { Record("FAIL restoring smoke Editor settings: " + error); code = 1; }
            // Restoration APIs can log failures without throwing. OnLog records those into the persisted exit code.
            if (SessionState.GetInt(Key + "exit", 1) != 0) code = 1;
            Record("RESULT " + (code == 0 ? "PASS" : "FAIL") + "; integration + shutdown + settings restoration");
            SessionState.SetBool(Key + "running", false);
            EditorApplication.update -= Update; Application.logMessageReceived -= OnLog;
            Debug.Log("ANDROID FLAT SMOKE: " + (code == 0 ? "PASS" : "FAIL") + "; report=" + Path.Combine(Output, "smoke.txt"));
            if (Application.isBatchMode) EditorApplication.Exit(code);
        }
    }
}
