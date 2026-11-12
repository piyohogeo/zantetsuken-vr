#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.XR;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// FACE-PICK-1 (development builds and the Editor): the collection, during ordinary play, of drawn faces the person points at -- a
    /// one-sided polygon left behind, a cut face where none should be. The piece probe (<see cref="PieceProbe"/>: the left controller's
    /// line, the left X button) looking at **what the display draws** instead of the colliders: every frame the line is drawn (to the
    /// first collider, or its whole reach) and coloured by a coarse look (green: the line passes through some drawn instance's box);
    /// the press searches the drawn triangles and the prepared caps along the line and saves the nearest few as one numbered pick
    /// (<see cref="FacePickCopy"/>), with one picture of the mirror window at the end of the press's frame and one a frame later, when
    /// the mark stands: a small cross and the pick's number at the first candidate's point, for a few seconds. A press that meets nothing
    /// is a pick too -- saved with its pictures, said as "nothing met".
    /// <para>
    /// The log tells apart <c>#n accepted</c> (searched, copied, handed to the writer), <c>#n saved</c> (every item of it committed, from
    /// the writer thread: the record, the two pictures, and the piece's own three where that save was made) and <c>#n FAILED</c>. The
    /// pulse says the press was taken: long where something was met, short where nothing was or the pick failed.
    /// </para>
    /// <para>
    /// On only with the Player argument <see cref="Argument"/> &lt;an absolute directory&gt;: a new folder of its own is made there.
    /// Nothing of the game's split or draw settings changes; the models' weights are kept from the launch on, as in the piece
    /// collection, so that an NPC piece saved beside a pick has them.
    /// </para>
    /// </summary>
    public sealed class FaceCollector : PieceProbe
    {
        public new const string Prefix = FacePick.Prefix;

        /// <summary>The Player argument that turns the collection on, followed by an absolute directory; without it nothing is created.</summary>
        public new const string Argument = "-zantetsuFaceCollect";

        /// <summary>How long the mark of a pick stands (seconds, unscaled).</summary>
        private const float MarkSeconds = 4f;

        /// <summary>Why the launch's argument gave no collection (null when it did, or was not given).</summary>
        public static string Refusal { get; private set; }

        public static FaceCollector Collector { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void KeepWeightsIfAsked()
        {
            if (TryReadDirectory(Environment.GetCommandLineArgs(), out _, out _)) NpcVertexBones.KeepWeights = true;
            Collector = null;
            Refusal = null;
        }

        internal static bool TryReadDirectory(string[] arguments, out string directory, out string refusal)
        {
            directory = null;
            refusal = null;
            arguments ??= Array.Empty<string>();
            int at = Array.FindIndex(arguments, a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
            if (at < 0) return false;
            if (at + 1 >= arguments.Length || arguments[at + 1].StartsWith("-", StringComparison.Ordinal) || !Path.IsPathRooted(arguments[at + 1]))
            {
                refusal = Argument + " needs an absolute directory; nothing is collected";
                return false;
            }

            directory = arguments[at + 1];
            return true;
        }

        /// <summary>The collector, when the launch asks for one (it is the probe: the probe's own argument and the piece collection's then add nothing).</summary>
        public static new FaceCollector InstallIfAsked()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            if (!TryReadDirectory(arguments, out string directory, out string refusal))
            {
                if (refusal != null)
                {
                    Refusal = refusal;
                    Debug.LogError(Prefix + refusal);
                }

                return null;
            }

            if (Collector != null) return Collector;
            if (Instance != null)
            {
                Refusal = "the piece probe was installed first; the face collection needs to be the probe";
                Debug.LogError(Prefix + Refusal);
                return null;
            }

            SpatialSplitCaptureSink sink;
            string folder = SpatialSplitCaptureSession.NewSessionFolder(directory, "face-pick-");
            string code = SpatialSplitCaptureSession.DescribeBuild(arguments);
            try
            {
                int at = Array.FindIndex(arguments, a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
                var all = new List<string>(SpatialSplitCaptureSession.LaunchNotes(arguments, at))
                {
                    "face collection (FACE-PICK-1): attempt = pick number; faces.jsonl (the picks' records, .zsf.gz), images.jsonl (two pictures a pick, .png); samples.jsonl, bones.jsonl, picks.jsonl, models.jsonl hold the piece's own save where one was made; results.jsonl stays empty",
                    "graphics " + SystemInfo.graphicsDeviceType + " (" + SystemInfo.graphicsDeviceName + "); XR device " + (XRSettings.loadedDeviceName ?? "") + "; executable " + Path.GetFileName(Environment.GetCommandLineArgs().Length > 0 ? Environment.GetCommandLineArgs()[0] : ""),
                };
                sink = new SpatialSplitCaptureSink(folder, SpatialSplitCaptureLimits.Default, code, all.ToArray());
            }
            catch (Exception e)
            {
                Refusal = "the collection's folder could not be made under " + directory + ": " + e.GetType().Name + ": " + e.Message;
                Debug.LogError(Prefix + Refusal);
                return null;
            }

            var host = new GameObject("Zantetsu face collector");
            if (Application.isPlaying) DontDestroyOnLoad(host);
            FaceCollector collector = host.AddComponent<FaceCollector>();
            collector._sink = sink;
            collector._code = code;
            collector.Pulse = false;
            Collector = collector;
            Instance = collector;
            Application.quitting += collector.OnQuitting;
            Debug.Log(Prefix + "on (" + Argument + "): session " + sink.sessionId + " into a new folder " + Path.GetFileName(folder) + "; " + code
                + "; the left X button saves the drawn faces the line passes through (nearest first), with two pictures of the mirror window; the models' weights are "
                + (NpcVertexBones.KeepWeights ? "kept" : "NOT kept (the argument was read too late)"));
            return collector;
        }

        private SpatialSplitCaptureSink _sink;
        private string _code;
        private int _picks, _accepted, _failedToRead, _nothingMet, _coarse;
        private long _saved, _failedToWrite;
        private readonly Dictionary<NpcVertexBones, (string file, string hash)> _models = new Dictionary<NpcVertexBones, (string, string)>();
        private bool _ended, _bodyRouteSaid;
        private GameObject _mark;
        private Material _markMaterial;
        private float _markUntil;

        /// <summary>Presses (picks numbered), picks searched and handed on, picks not taken at all, picks whose every item was committed, picks an item of which was not written, picks that met nothing (accepted all the same).</summary>
        public int Picks => _picks;
        public int Accepted => _accepted;
        public int FailedToRead => _failedToRead;
        public int NothingMet => _nothingMet;
        public long Saved => Interlocked.Read(ref _saved);
        public long FailedToWrite => Interlocked.Read(ref _failedToWrite);
        public string Folder => _sink != null ? _sink.directory : null;

        /// <summary>One pick's items, told by the writer thread in any order (and by the collector where a picture could not be handed): saved when all were written, failed at the first that was not.</summary>
        private sealed class Outcome
        {
            internal FaceCollector owner;
            internal int pick;
            private readonly object _gate = new object();
            private int _expected = -1, _told;
            private bool _failed, _said;

            internal void Expect(int items)
            {
                lock (_gate)
                {
                    _expected = items;
                    Settle();
                }
            }

            internal void Done(bool ok, string why)
            {
                lock (_gate)
                {
                    _told++;
                    if (!ok && !_failed)
                    {
                        _failed = true;
                        Interlocked.Increment(ref owner._failedToWrite);
                        Debug.LogError(Prefix + "#" + pick + " FAILED: an item was not written: " + why);
                    }

                    Settle();
                }
            }

            private void Settle()
            {
                if (_said || _failed || _expected < 0 || _told < _expected) return;
                _said = true;
                Interlocked.Increment(ref owner._saved);
                Debug.Log(Prefix + "#" + pick + " saved: its " + _expected + " items are committed");
            }
        }

        protected override bool Target(Vector3 origin, Vector3 direction, out RaycastHit hit, out bool onPiece)
        {
            CutWorldRoot world = World;
            // The coarse look, one frame in three (it goes through every drawn instance): the colour may be two frames old.
            if (Time.frameCount % 3 == 0) _coarse = world != null && world.IsReady && world.Display != null ? FacePickCopy.Coarse(world.Display, origin, direction, Reach) : 0;
            onPiece = _coarse > 0;
            if (world != null && world.IsReady && !_bodyRouteSaid)
            {
                _bodyRouteSaid = true;
                Debug.Log(Prefix + "the world's body route is " + world.BodyRoute + " (" + SystemInfo.graphicsDeviceType + ")");
            }

            return Physics.Raycast(origin, direction, out hit, Reach, ~0, QueryTriggerInteraction.Ignore);
        }

        private void LateUpdate()
        {
            if (_mark != null && Time.unscaledTime > _markUntil)
            {
                Destroy(_mark);
                _mark = null;
            }
        }

        protected override void Pressed(InputDevice left, bool aimed, Vector3 origin, Vector3 direction, bool found, RaycastHit hit, bool onPiece)
        {
            if (_sink == null || _ended) return;
            int pick = ++_picks;
            CutWorldRoot world = World;
            Camera eye = Camera.main;
            var aim = new FacePickCopy.Aim { aimed = aimed, origin = origin, direction = direction, reach = Reach, camera = eye };
            if (eye != null)
            {
                Transform t = eye.transform;
                aim.hasCamera = true;
                aim.stereo = eye.stereoEnabled;
                aim.cameraPosition = t.position;
                aim.cameraRotation = new float4(t.rotation.x, t.rotation.y, t.rotation.z, t.rotation.w);
                aim.worldToCamera = eye.worldToCameraMatrix;
                aim.projection = eye.projectionMatrix;
                aim.pixelWidth = Screen.width;
                aim.pixelHeight = Screen.height;
            }

            var outcome = new Outcome { owner = this, pick = pick };
            string failure = "the world is not ready";
            FacePickSample sample = null;
            int items = 0;
            bool taken = world != null && world.IsReady && FacePickCopy.TryPick(world, _sink, pick, _code, in aim, _models, outcome.Done, out sample, out items, out failure);
            if (!taken)
            {
                _failedToRead++;
                Debug.LogWarning(Prefix + "#" + pick + " FAILED (not taken): " + failure);
                FacePickCopy.HandFailure(_sink, world != null && world.SpatialSplit != null ? world.SpatialSplit.CaptureWorld : 0, pick, failure);
                if (left.isValid) left.SendHapticImpulse(0, 0.2f, 0.05f);
                return;
            }

            _accepted++;
            bool met = sample.candidates.Count > 0;
            if (!met) _nothingMet++;
            Debug.Log(Prefix + "#" + pick + " accepted" + (met ? "" : " (NOTHING MET)") + ": " + sample.Describe() + "; record " + FacePickSample.FileName(sample.world, pick) + " + 2 pictures"
                + (sample.pieceSaved ? " + the piece's own save" : ""));
            if (left.isValid) left.SendHapticImpulse(0, met ? 0.5f : 0.2f, met ? 0.08f : 0.05f);
            outcome.Expect(items + 2);
            StartCoroutine(Pictures(pick, sample, outcome, met));
        }

        // One picture at the end of the press's frame (no mark yet), then the mark, and one at the end of the next frame (the mark stands).
        private IEnumerator Pictures(int pick, FacePickSample sample, Outcome outcome, bool met)
        {
            yield return new WaitForEndOfFrame();
            Take(pick, sample.world, "before", outcome);
            if (met) Mark(pick, sample.candidates[0].hitWorld);
            yield return null;
            yield return new WaitForEndOfFrame();
            Take(pick, sample.world, "after", outcome);
        }

        private void Take(int pick, long world, string which, Outcome outcome)
        {
            Texture2D picture = null;
            string how = "ScreenCapture.CaptureScreenshotAsTexture";
            try
            {
                try
                {
                    picture = ScreenCapture.CaptureScreenshotAsTexture();
                }
                catch (Exception)
                {
                    picture = null;
                }

                if (picture == null)
                {
                    how = "ReadPixels of the screen";
                    picture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
                    picture.ReadPixels(new Rect(0f, 0f, Screen.width, Screen.height), 0, 0);
                    picture.Apply(false);
                }

                byte[] png = picture.EncodeToPNG();
                if (_sink == null || _ended || !FacePickCopy.TryHandImage(_sink, world, pick, which, png, picture.width, picture.height, Time.frameCount, how, outcome.Done, out string why))
                {
                    outcome.Done(false, "the picture '" + which + "' was not handed to the writer" + (_ended ? " (the collection ended)" : ""));
                }
            }
            catch (Exception e)
            {
                outcome.Done(false, "the picture '" + which + "' was not taken: " + e.GetType().Name + ": " + e.Message);
            }
            finally
            {
                if (picture != null) Destroy(picture);
            }
        }

        // A small cross at the point and the pick's number beside it, facing the eye; drawn over everything (the menu's shader).
        private void Mark(int pick, Vector3 at)
        {
            if (_mark != null) Destroy(_mark);
            _mark = new GameObject("Zantetsu face pick mark");
            _mark.transform.position = at;
            _markUntil = Time.unscaledTime + MarkSeconds;
            try
            {
                Shader shader = Resources.Load<Shader>(PlayMenuView.ShaderResource);
                if (_markMaterial == null && shader != null && shader.isSupported) _markMaterial = new Material(shader) { name = "Zantetsu face pick mark", hideFlags = HideFlags.HideAndDontSave };
                if (_markMaterial != null)
                {
                    Material material = _markMaterial;
                    var colour = new Color(1f, 0.2f, 0.9f, 1f);
                    for (int axis = 0; axis < 3; axis++)
                    {
                        var arm = new GameObject("arm " + axis);
                        arm.transform.SetParent(_mark.transform, false);
                        LineRenderer line = arm.AddComponent<LineRenderer>();
                        line.sharedMaterial = material;
                        line.positionCount = 2;
                        line.useWorldSpace = true;
                        line.widthMultiplier = 0.004f;
                        line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                        line.receiveShadows = false;
                        line.startColor = colour;
                        line.endColor = colour;
                        Vector3 half = (axis == 0 ? Vector3.right : axis == 1 ? Vector3.up : Vector3.forward) * 0.04f;
                        line.SetPosition(0, at - half);
                        line.SetPosition(1, at + half);
                    }
                }

                Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
                Camera eye = Camera.main;
                if (font != null)
                {
                    var label = new GameObject("number");
                    label.transform.SetParent(_mark.transform, false);
                    float distance = eye != null ? Vector3.Distance(eye.transform.position, at) : 1f;
                    label.transform.position = at + (Vector3.up * (0.05f + (0.01f * distance)));
                    if (eye != null) label.transform.rotation = Quaternion.LookRotation(at - eye.transform.position, Vector3.up);
                    TextMesh text = label.AddComponent<TextMesh>();
                    text.font = font;
                    text.text = "#" + pick.ToString(CultureInfo.InvariantCulture);
                    text.fontSize = 64;
                    text.characterSize = 0.0025f * Mathf.Max(1f, distance);
                    text.anchor = TextAnchor.LowerCenter;
                    text.color = new Color(1f, 0.2f, 0.9f, 1f);
                    label.GetComponent<MeshRenderer>().sharedMaterial = font.material;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning(Prefix + "the mark of #" + pick + " was not made whole: " + e.GetType().Name + ": " + e.Message);
            }
        }

        private void OnQuitting()
        {
            Application.quitting -= OnQuitting;
            End(5.0);
        }

        /// <summary>Ends the collection: the writer is closed (waiting at most this long) and the counts are said; nothing is taken after it.</summary>
        public void End(double waitSeconds)
        {
            if (_ended) return;
            _ended = true;
            SpatialSplitCaptureSink sink = _sink;
            if (sink == null) return;
            bool ended = sink.Close(waitSeconds);
            string summary = Prefix + "END: picks " + _picks + ", accepted " + _accepted + " (nothing met " + _nothingMet + "), saved " + Saved + ", failed " + (_failedToRead + FailedToWrite) + " (not taken " + _failedToRead
                + ", an item not written " + FailedToWrite + "), still being written " + (_accepted - Saved - FailedToWrite) + "; models' tables " + _models.Count + "; folder " + Path.GetFileName(sink.directory)
                + "; " + sink.Describe() + (ended ? "" : "; the writer had not ended after " + waitSeconds.ToString("R", CultureInfo.InvariantCulture) + " s");
            Debug.Log(summary);
            try
            {
                string path = Path.Combine(sink.directory, "faces-end.json");
                string json = "{\"picks\":" + _picks + ",\"accepted\":" + _accepted + ",\"nothingMet\":" + _nothingMet + ",\"saved\":" + Saved + ",\"failedToRead\":" + _failedToRead + ",\"failedToWrite\":" + FailedToWrite
                    + ",\"models\":" + _models.Count + ",\"writerEnded\":" + (ended ? "true" : "false") + ",\"summary\":" + SpatialSplitCaptureSink.Json(summary) + "}";
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
                File.WriteAllText(temporary, json + "\n", new UTF8Encoding(false));
                File.Move(temporary, path);
            }
            catch (Exception e)
            {
                Debug.LogError(Prefix + "the end's file was not written: " + e.GetType().Name + ": " + e.Message);
            }
        }

        protected override void OnDestroy()
        {
            Application.quitting -= OnQuitting;
            if (_mark != null) Destroy(_mark);
            if (_markMaterial != null) Destroy(_markMaterial);
            base.OnDestroy();
            if (ReferenceEquals(Collector, this)) Collector = null;
            if (ReferenceEquals(Instance, this)) Instance = null;
        }
    }
}
#endif
