using System;
using System.Collections;
using System.IO;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

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

            private IEnumerator Start()
            {
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
                yield return null;
                yield return Capture("01-before-the-cut");

                // ----- the cut, and the two children it publishes -------------------------------------------------
                LogicalFragmentId body = _probe.Body;
                bool asked = _probe.AskCut(body, new Vector4(0f, 1f, 0f, 0f));
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
                yield return null;
                yield return Capture("02-after-the-cut");

                // ----- the cut face, with the children carried apart ----------------------------------------------
                if (_world.Owners.TryGet(record.positive, out PhysicsFragmentOwner positive)
                    && _world.Owners.TryGet(record.negative, out PhysicsFragmentOwner negative))
                {
                    Hold(positive.Root);
                    Hold(negative.Root);
                    Vector3 before = positive.Root.transform.position;
                    positive.Root.transform.position += new Vector3(0f, 1.1f, 0f);
                    Log("carried the positive child from " + before + " to " + positive.Root.transform.position
                        + "; the negative child is at " + negative.Root.transform.position);
                    yield return null;
                    yield return null;
                    yield return Capture("03-children-apart");
                }

                // ----- cutting one of the published children ------------------------------------------------------
                LogicalFragmentId child = record.positive;
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
                Log("the child's cut committed. operation=" + second
                    + " positive=" + secondRecord.positive + " negative=" + secondRecord.negative);
                LogState("after the child's commit");
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
                string file = Path.Combine(directory, name + ".png");
                yield return new WaitForEndOfFrame();
                var picture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                picture.ReadPixels(new Rect(0f, 0f, Screen.width, Screen.height), 0, 0);
                picture.Apply(false);
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
                Log("finished with code " + code);
                yield return null;
                Application.Quit(code);
            }

            private static void Hold(GameObject actor)
            {
                var actorBody = actor.GetComponent<Rigidbody>();
                if (actorBody != null)
                {
                    actorBody.isKinematic = true;
                }
            }

            private static void Log(string line)
            {
                Debug.Log(Prefix + line);
            }
        }
    }
}
