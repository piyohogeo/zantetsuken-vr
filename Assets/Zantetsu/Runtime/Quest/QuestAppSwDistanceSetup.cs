using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Rendering.Urp;

namespace Zantetsu.Quest
{
    /// <summary>Explicit NPC experiment only. Fixed view is for numeric capture; off retains live head tracking.</summary>
    public sealed class QuestAppSwDistanceSetup : IDisposable
    {
        [Serializable] private sealed class Pose { public Vector3 position; public Quaternion rotation; }
        private readonly Camera _camera;
        private readonly string _fixedMode, _poseFile;
        private readonly VpAppSwDistanceExperiment _distance;
        private Pose _pose;
        private bool _locked, _disposed;
        public string Describe => "AppSW distance=" + _distance.FarMeters + "m (0=full); colour near/far=" + _camera.nearClipPlane + "/" + _camera.farClipPlane
            + "; object bounds culling only; fixed view=" + _fixedMode + "; Unity extra culls=" + _distance.CulledFrames
            + (_pose != null ? "; fixed pose=" + JsonUtility.ToJson(_pose) : "");

        public QuestAppSwDistanceSetup(Camera camera, float farMeters, string fixedMode, string persistentDirectory)
        {
            if (fixedMode != "off" && fixedMode != "capture" && fixedMode != "reuse") throw new ArgumentException("fixed view must be off/capture/reuse");
            _camera = camera; _fixedMode = fixedMode;
            _poseFile = Path.Combine(persistentDirectory, "appsw-distance-benchmark-pose.json");
            _distance = new VpAppSwDistanceExperiment(camera, farMeters);
            Application.onBeforeRender += LockView;
        }

        public void BeginPoseLock()
        {
            if (_fixedMode == "off") return;
            if (_camera.transform.parent == null || Vector3.Distance(_camera.transform.parent.lossyScale, Vector3.one) > 0.0001f)
                throw new InvalidOperationException("Fixed benchmark view requires the unit-scale tracked camera parent.");
            if (_fixedMode == "capture")
            {
                _pose = new Pose { position = _camera.transform.position, rotation = _camera.transform.rotation };
                File.WriteAllText(_poseFile, JsonUtility.ToJson(_pose, true));
            }
            else
            {
                _pose = JsonUtility.FromJson<Pose>(File.ReadAllText(_poseFile));
                if (_pose == null || !float.IsFinite(_pose.position.sqrMagnitude) || !float.IsFinite(_pose.rotation.x)
                    || !float.IsFinite(_pose.rotation.y) || !float.IsFinite(_pose.rotation.z) || !float.IsFinite(_pose.rotation.w)
                    || Mathf.Abs(Quaternion.Dot(_pose.rotation, _pose.rotation) - 1f) > 0.001f)
                    throw new InvalidDataException("Saved benchmark pose is invalid.");
            }
            _locked = true; LockView();
            Debug.Log("QUEST APPSW DISTANCE: " + Describe);
        }

        [BeforeRenderOrder(1000)]
        private void LockView()
        {
            if (!_locked || _disposed) return;
            Transform camera = _camera.transform, parent = camera.parent;
            Quaternion rotation = _pose.rotation * Quaternion.Inverse(camera.localRotation);
            parent.SetPositionAndRotation(_pose.position - rotation * camera.localPosition, rotation);
        }

        public void BeforeCamera(ScriptableRenderContext context) => _distance.Enqueue(context);
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true; Application.onBeforeRender -= LockView;
            _distance?.Dispose();
        }
    }
}
