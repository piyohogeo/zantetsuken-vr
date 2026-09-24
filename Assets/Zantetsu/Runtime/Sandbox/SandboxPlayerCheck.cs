using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using Unity.Profiling;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// Walks the sandbox scene's own way through one cut in a built Player, so that the path can be seen outside the
    /// editor (DESIGN 3). It presses the same buttons a person would: the probe's own <see
    /// cref="SandboxCutWorldProbe.AskCut"/>, its <see cref="SandboxCutWorldProbe.AskChildCut"/> and the world's own
    /// <see cref="CutWorldRoot.Shutdown"/> -- the three things the Space, C and E keys do.
    /// <para>
    /// **It never drives the product.** No update of the root or the driver is called, no frame is begun, nothing of
    /// the display is prepared or rendered from here. It waits for ordinary frames and reads what the world says.
    /// </para>
    /// <para>
    /// **It does nothing unless it is asked for.** Without <c>-zantetsuPlayerCheck &lt;directory&gt;</c> on the
    /// command line nothing of this exists at run time, so opening the scene and playing it is unchanged. What it
    /// writes -- the pictures and the lines it logs -- goes to that directory, which is outside the project.
    /// </para>
    /// </summary>
    public static class SandboxPlayerCheck
    {
        /// <summary>The argument that turns this on and says where its pictures go.</summary>
        public const string Argument = "-zantetsuPlayerCheck";

        /// <summary>Every line it writes begins with this, so a log can be read for them alone.</summary>
        public const string Prefix = "SANDBOX PLAYER: ";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void StartIfAsked()
        {
            string directory = DirectoryFromCommandLine();
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            var host = new GameObject("Sandbox player check");
            UnityEngine.Object.DontDestroyOnLoad(host);
            host.AddComponent<Walk>().directory = directory;
        }

        private static string DirectoryFromCommandLine()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], Argument, StringComparison.OrdinalIgnoreCase))
                {
                    return arguments[i + 1];
                }
            }

            return null;
        }

        /// <summary>
        /// The walk itself, on ordinary frames: it asks for a cut, waits, asks for the child's cut, waits, and ends
        /// the world, writing the world's state down **at the points it passes** and taking a picture at each of
        /// them.
        /// <para>
        /// **This is a representative path, not a frame-exact reading.** The state is read where the walk's own
        /// coroutine resumes, which is between the updates and the late updates of a frame; a picture is taken at the
        /// end of a frame, after it has been drawn. So a line and the picture near it describe the same part of the
        /// walk, and nothing here claims that a given picture is the picture of the frame a given line was written
        /// in. What a state really was is what the line says, never what a picture suggests.
        /// </para>
        /// </summary>
        [DefaultExecutionOrder(400)]
        private sealed class Walk : MonoBehaviour
        {
            internal string directory;

            private CutWorldRoot _world;
            private SandboxCutWorldProbe _probe;
            private bool _measure;
            private bool _measurementFailed;
            private Camera _captureCamera;
            private RenderTexture _captureTarget;
#if VP_DIAGNOSTIC_SCENE_AB
            private SceneAbRecorder _ab;
            private Vector3 _abOriginalPosition, _abChildPosition;
            private Quaternion _abOriginalRotation;
#endif

            private IEnumerator Start()
            {
                _measure = Environment.GetEnvironmentVariable("VP_COMPACT16UV_SCENE_MEASURE") == "1";
#if VP_DIAGNOSTIC_SCENE_AB
                _measure = true;
#endif
                if (_measure)
                {
                    Application.runInBackground = true; // Diagnostic Player only; project settings stay unchanged.
                    // The dedicated diagnostic build disables XR initialization, not just an already-started loader.
                    if (UnityEngine.XR.XRSettings.isDeviceActive) { Log("FAILED: mono diagnostic requires XR disabled at build time"); _measurementFailed = true; }
                    _captureCamera = Camera.main;
                    if (_captureCamera != null)
                    {
                        _captureTarget = new RenderTexture(Screen.width, Screen.height, 0,
                            UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm)
                        { depthStencilFormat = VpStencilAttachment.EightBitStencilFormat, antiAliasing = 1 };
                        _captureTarget.Create();
                        _captureCamera.targetTexture = _captureTarget;
                    }
                    else _measurementFailed = true;
                }
                Directory.CreateDirectory(directory);
                Log("started. directory=" + directory + " graphics=" + SystemInfo.graphicsDeviceType
                    + " device=" + SystemInfo.graphicsDeviceName + " screen=" + Screen.width + "x" + Screen.height
                    + " batchMode=" + Application.isBatchMode);

                // The scene builds its own world in its own Awake; this only waits for it.
                yield return WaitUntil(() =>
                {
                    _world = UnityEngine.Object.FindFirstObjectByType<CutWorldRoot>();
                    _probe = UnityEngine.Object.FindFirstObjectByType<SandboxCutWorldProbe>();
                    return _world != null && _probe != null && _world.IsReady && _probe.Body.IsSet;
                }, "the scene's world and body");

                if (_world == null || _probe == null || !_world.IsReady || !_probe.Body.IsSet)
                {
                    Log("FAILED: the scene did not build a world with a body.");
                    yield return Finish(2);
                    yield break;
                }

                LogState("the body is registered");
#if VP_DIAGNOSTIC_SCENE_AB
                _ab = gameObject.AddComponent<SceneAbRecorder>(); _ab.Initialize(_world);
                _abOriginalPosition = _probe.Actor.transform.position;
                _abOriginalRotation = _probe.Actor.transform.rotation;
                _ab.Mark("before", _probe.Body);
#endif
                if (Environment.GetEnvironmentVariable("VP_VERTEX_UPLOAD_COMPARE") == "1")
                {
                    // Separate transfer diagnostic, no scene-frame or physics-performance claim.
                    bool comparisonPassed = false;
                    yield return CompactVertexUploadComparison.RunGuarded(directory, passed => comparisonPassed = passed);
                    _world.Shutdown();
                    yield return WaitUntil(() => _world.IsReleased, "comparison world shutdown");
                    yield return Finish(comparisonPassed && _world.IsReleased ? 0 : 9);
                    yield break;
                }
                if (_measure)
                {
                    Log("atlas bound=" + VpCutSurfaceAtlas.IsBound + " vertexStride=" + VpRenderVertex.Stride);
                    yield return MeasureSettledScene("before-cut");
                }
                yield return null;
                yield return Capture("01-before-the-cut");

                // ----- the cut, and the two children it publishes -------------------------------------------------
                LogicalFragmentId body = _probe.Body;
#if VP_DIAGNOSTIC_SCENE_AB
                _ab.Phase = 1;
                bool asked = _probe.AskCut(body, new Vector4(0f, 1f, 0f, -.137f));
#else
                bool asked = _probe.AskCut(body, new Vector4(0f, 1f, 0f, 0f));
#endif
                Log("asked for a cut of the body: " + asked);
                if (!asked)
                {
                    yield return Finish(3);
                    yield break;
                }

                // How many of the frames this waits through had a provisional pair standing. It is counted so that
                // the report can say whether there was ever a window to look at, not to pair a frame with a picture.
                int provisionalFrames = 0;
                CutOperationId operation = default;
                float deadline = Time.realtimeSinceStartup + 60f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    if (_world.Owners.ProvisionalPairCount > 0)
                    {
                        provisionalFrames++;
                    }

                    if (_world.Ledger.TryGetReplacingOperation(body, out operation)
                        && _world.Geometry.StageOf(operation) == CutGeometryStage.Committed)
                    {
                        break;
                    }

                    yield return null;
                }

                Log("frames in which a provisional pair stood: " + provisionalFrames);
                if (!_world.Ledger.TryGetReplacingOperation(body, out operation)
                    || _world.Geometry.StageOf(operation) != CutGeometryStage.Committed)
                {
                    Log("FAILED: the cut did not reach a committed geometry.");
                    LogState("at the deadline");
                    yield return Capture("90-cut-did-not-commit");
                    yield return Finish(4);
                    yield break;
                }

                _world.Ledger.TryGetOperation(operation, out LogicalCutOperation record);
                Log("the cut committed. operation=" + operation
                    + " positive=" + record.positive + " negative=" + record.negative
                    + " stage=" + _world.Geometry.StageOf(operation));
                LogState("after the commit");
#if VP_DIAGNOSTIC_SCENE_AB
                _ab.Phase = 2; _ab.Mark("first-cut", record.positive, record.negative);
#endif
                yield return null;
                yield return Capture("02-after-the-cut");

                // ----- the cut face, with the children carried apart ----------------------------------------------
                if (_world.Owners.TryGet(record.positive, out PhysicsFragmentOwner positive)
                    && _world.Owners.TryGet(record.negative, out PhysicsFragmentOwner negative))
                {
                    Hold(positive.Root);
                    Hold(negative.Root);
#if VP_DIAGNOSTIC_SCENE_AB
                    positive.Root.transform.SetPositionAndRotation(_abOriginalPosition, _abOriginalRotation);
                    negative.Root.transform.SetPositionAndRotation(_abOriginalPosition, _abOriginalRotation);
#endif
                    Vector3 before = positive.Root.transform.position;
                    positive.Root.transform.position += new Vector3(0f, 1.1f, 0f);
#if VP_DIAGNOSTIC_SCENE_AB
                    _abChildPosition = positive.Root.transform.position;
#endif
                    Log("carried the positive child from " + before + " to " + positive.Root.transform.position
                        + "; the negative child is at " + negative.Root.transform.position);
                    yield return null;
                    yield return null;
                    yield return Capture("03-children-apart");
                    if (_measure && VpCutSurfaceAtlas.IsBound)
                    {
                        var colours = VpCutSurfaceColour.Capture();
                        VpCutSurfaceColour.SetDebugEnabled(true);
                        yield return Capture("03b-children-apart-debug");
                        VpCutSurfaceColour.Restore(colours);
                    }
                }

                // ----- cutting one of the published children ------------------------------------------------------
                LogicalFragmentId child = record.positive;
#if VP_DIAGNOSTIC_SCENE_AB
                _ab.Phase = 3;
#endif
                bool askedChild = _probe.AskChildCut();
                Log("asked for a cut of the published child " + child + ": " + askedChild);
                if (!askedChild)
                {
                    yield return Finish(5);
                    yield break;
                }

                CutOperationId second = default;
                deadline = Time.realtimeSinceStartup + 60f;
                while (Time.realtimeSinceStartup < deadline)
                {
                    if (_world.Ledger.TryGetReplacingOperation(child, out second)
                        && _world.Geometry.StageOf(second) == CutGeometryStage.Committed)
                    {
                        break;
                    }

                    yield return null;
                }

                if (!_world.Ledger.TryGetReplacingOperation(child, out second)
                    || _world.Geometry.StageOf(second) != CutGeometryStage.Committed)
                {
                    Log("FAILED: the child's cut did not reach a committed geometry.");
                    LogState("at the deadline");
                    yield return Capture("91-child-cut-did-not-commit");
                    yield return Finish(6);
                    yield break;
                }

                _world.Ledger.TryGetOperation(second, out LogicalCutOperation secondRecord);
#if VP_DIAGNOSTIC_SCENE_AB
                foreach (var id in new[] { secondRecord.positive, secondRecord.negative })
                    if (_world.Owners.TryGet(id, out PhysicsFragmentOwner grandchild))
                    {
                        Hold(grandchild.Root);
                        grandchild.Root.transform.SetPositionAndRotation(_abChildPosition, _abOriginalRotation);
                    }
#endif
                Log("the child's cut committed. operation=" + second
                    + " positive=" + secondRecord.positive + " negative=" + secondRecord.negative);
                LogState("after the child's commit");
#if VP_DIAGNOSTIC_SCENE_AB
                _ab.Phase = 4; _ab.Mark("recut", secondRecord.positive, secondRecord.negative, record.negative);
#endif
                if (_measure) yield return MeasureSettledScene("after-recut");
#if VP_DIAGNOSTIC_SCENE_AB
                _ab.StopAndSave(directory);
#endif
                yield return null;
                yield return Capture("04-after-the-child-cut");

                // ----- the ordinary ending -------------------------------------------------------------------------
                Log("asking the world to end");
                _world.Shutdown();
                Log("accepted after Shutdown: IsReady=" + _world.IsReady);

                int endingFrames = 0;
                deadline = Time.realtimeSinceStartup + 120f;
                while (!_world.IsReleased && Time.realtimeSinceStartup < deadline)
                {
                    endingFrames++;
                    yield return null;
                }

                bool drained = _world.IsDrained();
                Log("the ending: IsReleased=" + _world.IsReleased + " IsDrained=" + drained
                    + " displayDisposed=" + (_world.Display != null && _world.Display.IsDisposed)
                    + " ordinary frames spent=" + endingFrames);
                if (!_world.IsReleased || !drained)
                {
                    Log("FAILED: the world did not give everything back on ordinary frames.");
                    yield return Finish(7);
                    yield break;
                }

                // Frames after the ending, to see that nothing touches what was given up.
                yield return null;
                yield return null;
                LogState("two frames after the ending");
                if (_measure) Log("atlas bound after shutdown=" + VpCutSurfaceAtlas.IsBound);
                yield return Capture("05-after-the-ending");
                yield return Finish(0);
            }

            private void LogState(string what)
            {
                if (_world == null)
                {
                    Log(what + ": there is no world");
                    return;
                }

                VpLogicalCutDisplay display = _world.Display;
                bool live = display != null && !display.IsDisposed;
                Log(what + ": frame=" + Time.frameCount
                    + " ready=" + _world.IsReady
                    + " released=" + _world.IsReleased
                    + " provisionalPairs=" + _world.Owners.ProvisionalPairCount
                    + " geometryFaults=" + _world.GeometryFaults
                    + " displayDisposed=" + (display == null || display.IsDisposed)
                    + " renderFragments=" + (live ? display.RenderFragmentCount : -1)
                    + " drawCommands=" + (live ? display.DrawCommandCount : -1)
                    + " settledCollections=" + (live ? display.SettledCollections : -1)
                    + " broken=" + (live && display.IsBroken)
                    + " halted=" + (live && display.IsHalted));
            }

            /// <summary>
            /// Takes one picture of what the Player is showing, at the end of this frame, and then **steps to the
            /// next ordinary point in the loop**.
            /// <para>
            /// A picture can only be read once the frame has been drawn, so this resumes after that frame's camera
            /// has registered and issued its draws. Asking the world for anything there would be asking it inside a
            /// frame it is already drawing -- ending it there, for one, is what the display refuses by contract. So
            /// the last thing this does is wait for the next frame, and everything after a picture happens at the
            /// ordinary point a key press would happen at.
            /// </para>
            /// </summary>
            private IEnumerator Capture(string name)
            {
#if VP_DIAGNOSTIC_SCENE_AB
                if (_ab != null && !_ab.CapturesEnabled) { yield return null; yield break; }
#endif
                string file = Path.Combine(directory, name + ".png");
                yield return new WaitForEndOfFrame();
                var picture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                var previousTarget = RenderTexture.active;
                try
                {
                    if (_measure) RenderTexture.active = _captureTarget;
                    picture.ReadPixels(new Rect(0f, 0f, Screen.width, Screen.height), 0, 0);
                }
                finally { RenderTexture.active = previousTarget; }
                picture.Apply(false);
                if (_measure)
                {
                    bool nonblack = false;
                    foreach (var pixel in picture.GetPixels32())
                        if (pixel.r + pixel.g + pixel.b > 12) { nonblack = true; break; }
                    if (!nonblack) { Log("FAILED: blank capture " + name); _measurementFailed = true; }
                }
                File.WriteAllBytes(file, picture.EncodeToPNG());
                Destroy(picture);
                Log("picture " + name + " -> " + file);
                yield return null;
            }

            private IEnumerator WaitUntil(Func<bool> condition, string what)
            {
                float deadline = Time.realtimeSinceStartup + 60f;
                while (!condition() && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                Log("waited for " + what + ": " + condition());
            }

            private IEnumerator Finish(int code)
            {
                if (code == 0 && _measurementFailed) code = 8;
                Log("finished with code " + code);
                yield return null;
                Application.Quit(code);
            }

            // Existing explicit Player-check path only. The recorder includes Main-thread waits; it is not
            // a custom CPU-work marker. Memory is a settled process snapshot, not WDDM GPU residency or peak.
            private IEnumerator MeasureSettledScene(string phase)
            {
                using var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread", 1);
                if (!recorder.Valid)
                {
                    Log("FAILED: Main Thread recorder unavailable; no timing result inferred.");
                    _measurementFailed = true;
                    yield break;
                }
                for (int i = 0; i < 60; i++) yield return null;
                var drawing = UnityEngine.Object.FindFirstObjectByType<CutWorldCameraDrawing>();
                int drawnBefore = drawing != null ? drawing.DrawnFrames : 0;
                var values = new double[240];
                for (int i = 0; i < values.Length; i++)
                {
                    yield return null;
                    values[i] = recorder.LastValue * 1e-6;
                }
                Array.Sort(values);
                if (values[0] <= 0) _measurementFailed = true;
                int drawnDelta = drawing != null ? drawing.DrawnFrames - drawnBefore : 0;
                if (drawnDelta <= 0) { Log("FAILED: no product camera draw during sample window"); _measurementFailed = true; }
                long workingSet = WorkingSetBytes();
                if (workingSet < 0) _measurementFailed = true;
                Log("scene sample " + phase + ": frames=240 drawnFrames=" + drawnDelta + " mainThreadMedianMs=" + values[120].ToString("F6")
                    + " mainThreadP95Ms=" + values[228].ToString("F6")
                    + " unityAllocatedBytes=" + UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong()
                    + " unityReservedBytes=" + UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong()
                    + " processWorkingSetBytes=" + workingSet
                    + " storageVertices=" + _world.Storage.VertexCount
                    + " vertexCapacityBytes=" + ((long)_world.Storage.VertexCapacity * VpRenderVertex.Stride)
                    + " vSync=" + QualitySettings.vSyncCount + " targetFrameRate=" + Application.targetFrameRate
                    + "; inclusive Main Thread / settled snapshots, not peak or 32B comparison");
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct ProcessMemoryCounters
            {
                public uint size, pageFaultCount;
                public UIntPtr peakWorkingSet, workingSet, quotaPeakPagedPool, quotaPagedPool;
                public UIntPtr quotaPeakNonPagedPool, quotaNonPagedPool, pageFile, peakPageFile;
            }
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
            [DllImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);
#endif
            private static long WorkingSetBytes()
            {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
                var counters = new ProcessMemoryCounters { size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
                // Current-process pseudo handle is not owned and must not be closed.
                if (GetProcessMemoryInfo(new IntPtr(-1), ref counters, counters.size)) return (long)counters.workingSet.ToUInt64();
#endif
                Log("FAILED: process working-set query unavailable; no memory value inferred.");
                return -1;
            }

            private static void Hold(GameObject actor)
            {
                var actorBody = actor.GetComponent<Rigidbody>();
                if (actorBody != null)
                {
                    actorBody.isKinematic = true;
                }
            }

            private void OnDestroy()
            {
                if (_captureCamera != null && _captureCamera.targetTexture == _captureTarget) _captureCamera.targetTexture = null;
                if (_captureTarget != null) { _captureTarget.Release(); Destroy(_captureTarget); }
            }

            private static void Log(string line)
            {
                Debug.Log(Prefix + line);
            }
        }
    }
}
