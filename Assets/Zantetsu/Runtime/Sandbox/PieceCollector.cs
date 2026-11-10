#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.XR;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// NPC-PIECE-PICK-1 (development builds and the Editor): the collection, during ordinary play, of the pieces the person wants apart that
    /// the additional split leaves together. The piece probe (<see cref="PieceProbe"/>: the left controller's line, the left X button) with
    /// the press doing more: the probe's own line is logged as before, then the piece the line rests on is saved as it is in that frame
    /// (<see cref="NpcPiecePickCopy"/>: its mesh, convexes and frames in the capture's sample format, its bones in the capture's sidecar, the
    /// pick's own record with where the line pointed and the split's last note on the piece, and -- once a model -- the model's bones and
    /// weights). Each press is one numbered pick, the same piece pointed at from elsewhere another; the pulse of the probe says the press was
    /// taken, a short one that nothing was. The log tells the three apart: <c>#n accepted</c> (copied and handed to the writer), <c>#n saved</c>
    /// (every file of it committed, from the writer thread), <c>#n FAILED</c> (not read, not copied, or not written, with why). The counts are
    /// said again at the end (the quit), with the folder.
    /// <para>
    /// On only with the Player argument <see cref="Argument"/> &lt;an absolute directory&gt;: a new folder of its own is made there. The
    /// model's weights are kept from the launch on (<see cref="NpcVertexBones.KeepWeights"/>); nothing else of the game changes, and the
    /// issued inputs' capture is not turned on (the split's searches run as the product's).
    /// </para>
    /// </summary>
    public sealed class PieceCollector : PieceProbe
    {
        public new const string Prefix = NpcPiecePick.Prefix;

        /// <summary>The Player argument that turns the collection on, followed by an absolute directory; without it nothing is created.</summary>
        public const string Argument = "-zantetsuPieceCollect";

        /// <summary>Why the launch's argument gave no collection (null when it did, or was not given).</summary>
        public static string Refusal { get; private set; }

        public static PieceCollector Collector { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void KeepWeightsIfAsked()
        {
            // Before any model is prepared: the tables made in this session keep their weights.
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

        /// <summary>The collector, when the launch asks for one (and the probe's host with it: the probe's own argument then adds nothing).</summary>
        public static PieceCollector InstallIfAsked()
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
                Refusal = "the piece probe was installed first; the collection needs to be the probe";
                Debug.LogError(Prefix + Refusal);
                return null;
            }

            SpatialSplitCaptureSink sink;
            string folder = SpatialSplitCaptureSession.NewSessionFolder(directory, "piece-pick-");
            string code = SpatialSplitCaptureSession.DescribeBuild(arguments);
            try
            {
                int at = Array.FindIndex(arguments, a => string.Equals(a, Argument, StringComparison.OrdinalIgnoreCase));
                string[] notes = SpatialSplitCaptureSession.LaunchNotes(arguments, at);
                var all = new List<string>(notes) { "piece collection (NPC-PIECE-PICK-1): attempt = pick number; results.jsonl stays empty; picks.jsonl, models.jsonl beside the capture's own indexes" };
                sink = new SpatialSplitCaptureSink(folder, SpatialSplitCaptureLimits.Default, code, all.ToArray());
            }
            catch (Exception e)
            {
                Refusal = "the collection's folder could not be made under " + directory + ": " + e.GetType().Name + ": " + e.Message;
                Debug.LogError(Prefix + Refusal);
                return null;
            }

            var host = new GameObject("Zantetsu piece collector");
            if (Application.isPlaying) DontDestroyOnLoad(host);
            PieceCollector collector = host.AddComponent<PieceCollector>();
            collector._sink = sink;
            collector._code = code;
            collector.Pulse = false;   // the collection's own pulse says whether the pick was taken
            Collector = collector;
            Instance = collector;
            Application.quitting += collector.OnQuitting;
            Debug.Log(Prefix + "on (" + Argument + "): session " + sink.sessionId + " into a new folder " + Path.GetFileName(folder) + "; " + code
                + "; the left X button saves the piece the line rests on (the probe's line is logged too); the models' weights are " + (NpcVertexBones.KeepWeights ? "kept" : "NOT kept (the argument was read too late)"));
            return collector;
        }

        private SpatialSplitCaptureSink _sink;
        private string _code;
        private int _picks, _accepted, _failedToRead;
        private long _saved, _failedToWrite;
        private readonly Dictionary<NpcVertexBones, (string file, string hash)> _models = new Dictionary<NpcVertexBones, (string, string)>();
        private bool _ended;

        /// <summary>Presses (picks numbered), picks copied and handed on, picks not read or copied, picks whose every file was committed, picks a file of which was not written.</summary>
        public int Picks => _picks;
        public int Accepted => _accepted;
        public int FailedToRead => _failedToRead;
        public long Saved => Interlocked.Read(ref _saved);
        public long FailedToWrite => Interlocked.Read(ref _failedToWrite);
        public string Folder => _sink != null ? _sink.directory : null;

        /// <summary>One pick's three items, told by the writer thread (any order): saved when all were written, failed at the first that was not.</summary>
        private sealed class Outcome
        {
            internal PieceCollector owner;
            internal int pick, fragment;
            internal int told;
            internal int failed;

            internal void Done(bool ok, string why)
            {
                if (!ok && Interlocked.Exchange(ref failed, 1) == 0)
                {
                    Interlocked.Increment(ref owner._failedToWrite);
                    Debug.LogError(Prefix + "#" + pick + " FAILED (fragment " + fragment + "): a file was not written: " + why);
                }

                if (Interlocked.Increment(ref told) == NpcPiecePickCopy.ItemsPerPick && Volatile.Read(ref failed) == 0)
                {
                    Interlocked.Increment(ref owner._saved);
                    Debug.Log(Prefix + "#" + pick + " saved (fragment " + fragment + "): its sample, bones and record are committed");
                }
            }
        }

        protected override void Pressed(InputDevice left, bool aimed, Vector3 origin, Vector3 direction, bool found, RaycastHit hit, bool onPiece)
        {
            base.Pressed(left, aimed, origin, direction, found, hit, onPiece);   // the probe's own line, as before
            if (_sink == null || _ended) return;
            int pick = ++_picks;
            CultureInfo inv = CultureInfo.InvariantCulture;
            CutWorldRoot world = World;
            LogicalFragmentId fragment = default;
            string failure = null;
            if (world == null || world.SpatialSplit == null || world.Owners == null) failure = "no cut world";
            else if (!aimed) failure = "the left controller is not tracked";
            else if (!found) failure = "nothing within reach along the line";
            else if (!onPiece || hit.rigidbody == null || !world.Owners.TryResolveFragment(hit.rigidbody, out fragment, out float _)) failure = "the line rests on something that is not a piece of the cut world";

            NpcPiecePickSample record = null;
            if (failure == null)
            {
                var aim = new NpcPiecePickCopy.Aim { origin = origin, direction = direction, hitWorld = hit.point, hitDistance = hit.distance, hitCollider = hit.collider != null ? hit.collider.name : "" };
                var outcome = new Outcome { owner = this, pick = pick, fragment = fragment.value };
                if (!NpcPiecePickCopy.TryPick(world, world.SpatialSplit, _sink, pick, _code, fragment, in aim, _models, outcome.Done, out record, out failure))
                {
                    record = null;
                }
            }

            if (failure != null)
            {
                _failedToRead++;
                Debug.LogWarning(Prefix + "#" + pick + " FAILED (not taken): " + failure + (fragment.IsSet ? "; fragment " + fragment.value : ""));
                NpcPiecePickCopy.HandFailure(_sink, world != null && world.SpatialSplit != null ? world.SpatialSplit.CaptureWorld : 0, pick, failure, fragment.IsSet ? fragment.value : -1, null);
                if (left.isValid) left.SendHapticImpulse(0, 0.2f, 0.05f);
                return;
            }

            _accepted++;
            var text = new StringBuilder(512);
            text.Append(Prefix).Append('#').Append(pick).Append(" accepted: fragment ").Append(record.fragment).Append(" of ").Append(record.objectName).Append(" (root ").Append(record.rootFragment)
                .Append(", generation ").Append(record.generation).Append("), convexes ").Append(record.convexCount).Append(", indices ").Append(record.indexCount)
                .Append(", hit (").Append(record.hitWorld.x.ToString("F2", inv)).Append(", ").Append(record.hitWorld.y.ToString("F2", inv)).Append(", ").Append(record.hitWorld.z.ToString("F2", inv))
                .Append(") in the geometry (").Append(record.hitGeometry.x.ToString("F3", inv)).Append(", ").Append(record.hitGeometry.y.ToString("F3", inv)).Append(", ").Append(record.hitGeometry.z.ToString("F3", inv))
                .Append("); model ").Append(record.modelName).Append(" (").Append(record.modelFile).Append(record.weightsKept ? ", weights kept" : ", NO weights").Append("); the split's look ").Append(record.examine)
                .Append("; the note: ").Append(record.hasNote ? (record.noteAttempted ? "last attempt ended " + record.noteEnd + " at frame " + record.noteEndFrame + (record.noteBoneVerdict.Length > 0 ? ", the bones " + record.noteBoneVerdict + " (" + record.noteBoneReason + ")" : "")
                    + (record.noteFellBack ? ", then the method as before" : "") + (record.noteIsOfThisGeometry ? " -- of this geometry" : " -- of an EARLIER geometry of this fragment") : "not attempted since it was made")
                    + (record.noteMadeBy.Length > 0 ? "; made from fragment " + record.noteMadeFrom + " by " + record.noteMadeBy : "") : "none")
                .Append("; files ").Append(record.sampleFile).Append(' ').Append(record.bonesFile).Append(" + pick record");
            Debug.Log(text.ToString());
            if (left.isValid) left.SendHapticImpulse(0, 0.5f, 0.08f);
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
            string summary = Prefix + "END: picks " + _picks + ", accepted " + _accepted + ", saved " + Saved + ", failed " + (_failedToRead + FailedToWrite) + " (not read or copied " + _failedToRead
                + ", a file not written " + FailedToWrite + "), still being written " + (_accepted - Saved - FailedToWrite) + "; models' tables " + _models.Count + "; folder " + Path.GetFileName(sink.directory)
                + "; " + sink.Describe() + (ended ? "" : "; the writer had not ended after " + waitSeconds.ToString("R", CultureInfo.InvariantCulture) + " s");
            Debug.Log(summary);
            try
            {
                string path = Path.Combine(sink.directory, "picks-end.json");
                string json = "{\"picks\":" + _picks + ",\"accepted\":" + _accepted + ",\"saved\":" + Saved + ",\"failedToRead\":" + _failedToRead + ",\"failedToWrite\":" + FailedToWrite
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
            base.OnDestroy();
            if (ReferenceEquals(Collector, this)) Collector = null;
            if (ReferenceEquals(Instance, this)) Instance = null;
        }
    }
}
#endif
