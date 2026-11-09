using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;
using Zantetsu.Core.Statistics;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.Quest
{
    /// <summary>The Windows play entry keeps loading and the cut-input hold through the real camera's drawing preparation.</summary>
    public sealed class PcvrPlayPrewarm : MonoBehaviour
    {
        private const string Prefix = "PCVR PLAY PREWARM: ";
        private const double ReadyTimeoutSeconds = 60.0;
        private static PcvrPlayPrewarm s_current;
        private readonly List<XRDisplaySubsystem> _displays = new List<XRDisplaySubsystem>(1);
        private CutWorldRoot _world;
        private Camera _camera;
        private QuestDrawPrewarm _prewarm;
        private Coroutine _routine;
        private bool _preparing, _worldFound, _cameraFound;
        private double _deadline;

        internal bool OtherPreparationsPending => PlayPreparation.Pending > 1;
        internal static bool XrCameraReady(bool displayPresent, bool displayRunning, bool stereoEnabled) =>
            !displayPresent || (displayRunning && stereoEnabled);
        internal bool Finished { get; private set; }
        internal bool Completed => Finished && Failure == null;
        internal string Failure { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForSession() => s_current = null;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            SandboxScenario.PlatformPlay = StartForPlay;
#endif
        }

        private static string StartForPlay()
        {
            Install();
            return null;
        }

        internal static PcvrPlayPrewarm Install()
        {
            if (s_current != null) return s_current;
            var host = new GameObject("PCVR play drawing preparation");
            var preparation = host.AddComponent<PcvrPlayPrewarm>();
            s_current = preparation;
            preparation._preparing = true;
            preparation._deadline = Time.realtimeSinceStartupAsDouble + ReadyTimeoutSeconds + QuestDrawPrewarm.DefaultTimeoutSeconds;
            PlayPreparation.Begin();
            PlayMenu.HoldForPreparation();
            Debug.Log(Prefix + "begun at frame " + Time.frameCount + "; loading and the cut-input hold wait for the drawing");
            return preparation;
        }

        private void Start()
        {
            if (_preparing) _routine = StartCoroutine(Prepare());
        }

        internal IEnumerator Prepare(double readyTimeoutSeconds = ReadyTimeoutSeconds)
        {
            var crowds = FindObjectsByType<MobPlanCrowd>(FindObjectsSortMode.None);
            double readyBy = Time.realtimeSinceStartupAsDouble + readyTimeoutSeconds;
            string waiting;
            while (_preparing)
            {
                if (!_worldFound)
                {
                    _world = FindAnyObjectByType<CutWorldRoot>();
                    _worldFound = _world != null;
                }
                if (!_cameraFound)
                {
                    _camera = Camera.main;
                    _cameraFound = _camera != null;
                }
                if ((_worldFound && (_world == null || _world.IsEnding || _world.TerminationRequested)) || (_cameraFound && _camera == null))
                {
                    Abandon("the world or camera ended during preparation");
                    yield break;
                }
                waiting = QuestNpcSlashDemo.PlayWaitingFor(_world, crowds);
                if (waiting == null && (_camera == null || !_camera.isActiveAndEnabled)) waiting = "the active play camera";
                if (waiting == null)
                {
                    SubsystemManager.GetSubsystems(_displays);
                    bool running = false;
                    foreach (XRDisplaySubsystem display in _displays) running |= display.running;
                    if (!XrCameraReady(_displays.Count != 0, running, _camera.stereoEnabled)) waiting = "the running XR camera";
                }
                if (waiting == null && _world.Display.NativePrewarmView(_camera) < 0) waiting = "the display camera registration";
                if (waiting == null && OtherPreparationsPending) waiting = "the other start-up preparations";
                if (waiting == null) break;
                if (Time.realtimeSinceStartupAsDouble >= readyBy)
                {
                    EndPreparation("still waiting for " + waiting + " at the preparation deadline");
                    yield break;
                }
                yield return null;
            }
            if (!_preparing) yield break;

            bool native = _world.Display.NativeArguments && SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12;
            var drawing = _world.GetComponent<CutWorldCameraDrawing>();
            _prewarm = new QuestDrawPrewarm(_camera, _world, drawing, native, false, false,
                QuestDrawPrewarm.DefaultTimeoutSeconds, FirstPreparedSkinModel, unityBodies: true);
            Debug.Log(Prefix + "warming " + (native ? "native Direct3D 12" : "Unity") + " at frame " + Time.frameCount
                + "; stereo=" + _camera.stereoEnabled + "; API=" + SystemInfo.graphicsDeviceType);
            IEnumerator steps = _prewarm.Run();
            while (_preparing)
            {
                bool more = false;
                string failure = null;
                try { more = steps.MoveNext(); }
                catch (Exception e) { failure = "drawing preparation threw: " + e.Message; }
                if (failure != null)
                {
                    Fail(failure);
                    yield break;
                }
                if (!more) break;
                yield return steps.Current;
            }
            if (!_preparing) yield break;
            foreach (string line in _prewarm.Report) Debug.Log(Prefix + line);
            EndPreparation(_prewarm.Completed ? null : _prewarm.Failure ?? "drawing preparation did not complete");
        }

        private static VpSkinModel FirstPreparedSkinModel()
        {
            foreach (SandboxNpcCharacter npc in FindObjectsByType<SandboxNpcCharacter>(FindObjectsSortMode.None))
                if (npc != null && npc.Handle != null && npc.Handle.SkinModelForWarm != null) return npc.Handle.SkinModelForWarm;
            return null;
        }

        private void Update()
        {
            if (!_preparing) return;
            if ((_worldFound && (_world == null || _world.IsEnding || _world.TerminationRequested)) || (_cameraFound && _camera == null))
                Abandon("the world or camera ended during preparation");
            else if (_prewarm != null && (!_world.IsReady || !_camera.isActiveAndEnabled))
                Abandon("the world or camera stopped during drawing preparation");
            else if (Time.realtimeSinceStartupAsDouble >= _deadline)
                Fail("the drawing preparation exceeded its start-up deadline");
        }

        private void Fail(string failure)
        {
            try { _prewarm?.Abandon(failure); }
            finally { EndPreparation(failure); }
        }

        internal void Abandon(string why)
        {
            if (!_preparing) return;
            if (_routine != null) { StopCoroutine(_routine); _routine = null; }
            try { _prewarm?.Abandon(why); }
            finally { EndPreparation(why, true); }
        }

        private void EndPreparation(string failure, bool abandoned = false)
        {
            if (!_preparing) return;
            _preparing = false;
            Finished = true;
            Failure = failure;
            PlayPreparation.End();
            PlayMenu.ReleasePreparation();
            string message = Prefix + (failure == null ? "completed" : abandoned ? "abandoned: " + failure : "FAILED: " + failure) + " at frame " + Time.frameCount;
            if (failure != null && !abandoned) Debug.LogError(message);
            else Debug.Log(message);
        }

        private void OnDisable() => Abandon("the preparation component was disabled");
        private void OnApplicationQuit() => Abandon("the Player is quitting");
        private void OnDestroy()
        {
            Abandon("the preparation scene was destroyed");
            if (s_current == this) s_current = null;
        }
    }
}
