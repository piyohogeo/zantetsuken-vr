using System;
using System.Globalization;
using System.IO;
using UnityEngine;
using UnityEngine.XR;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The collection, during ordinary play, of pieces seen floating (the convex trim's evaluation, 2026-11-20). The piece
    /// probe (<see cref="PieceProbe"/>: the left controller's line, green on a piece; the left X button) with the press
    /// doing more: the piece the line rests on is saved as it stands in that frame -- what is drawn of it, every convex,
    /// its pose and motion, and the colliders within <see cref="SupportReach"/> of it that are not its own
    /// (<see cref="ConvexTrimCapture"/>) -- one JSON file a press, numbered. A pulse says the press was taken, a short one
    /// that nothing was saved. The saved pieces are replayed without the game (ConvexTrimCaptureReplay).
    /// <para>
    /// On only with the Player argument <see cref="Argument"/> &lt;an absolute directory&gt;: a new folder of its own is made
    /// there. Nothing of the game changes; the save is made on the main thread at the press (a hitch of that frame).
    /// </para>
    /// </summary>
    public sealed class FloatCollector : PieceProbe
    {
        public new const string Prefix = "FLOAT COLLECT: ";

        /// <summary>The Player argument that turns the collection on, followed by an absolute directory; without it nothing is created.</summary>
        public new const string Argument = "-zantetsuFloatCollect";

        /// <summary>How far round the piece's world box its supports are looked for, metres.</summary>
        public const float SupportReach = 0.5f;

        public static FloatCollector Collector { get; private set; }

        /// <summary>Why the launch's argument gave no collection (null when it did, or was not given).</summary>
        public static string Refusal { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetForSession()
        {
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

        /// <summary>The collector, when the launch asks for one (it is the probe then: the probe's own argument adds nothing).</summary>
        public static new FloatCollector InstallIfAsked()
        {
            if (!TryReadDirectory(Environment.GetCommandLineArgs(), out string directory, out string refusal))
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
                Refusal = "another probe was installed first; the collection needs to be the probe";
                Debug.LogError(Prefix + Refusal);
                return null;
            }

            string folder;
            try
            {
                folder = Path.Combine(directory, "float-collect-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                Directory.CreateDirectory(folder);
            }
            catch (Exception e)
            {
                Refusal = "the collection's folder could not be made under " + directory + ": " + e.GetType().Name + ": " + e.Message;
                Debug.LogError(Prefix + Refusal);
                return null;
            }

            var host = new GameObject("Zantetsu float collector");
            if (Application.isPlaying) DontDestroyOnLoad(host);
            Collector = host.AddComponent<FloatCollector>();
            Collector._folder = folder;
            Collector.Pulse = false;
            Instance = Collector;
            Debug.Log(Prefix + "on (" + Argument + "): a line from the left controller aims (green on a piece), the left X button saves the piece it rests on -- its drawn mesh, convexes, pose and the colliders within "
                + SupportReach.ToString("R", CultureInfo.InvariantCulture) + " m -- into " + folder);
            return Collector;
        }

        private string _folder;
        private int _saved, _failed;

        /// <summary>Pieces saved and presses that saved nothing; the folder.</summary>
        public int Saved => _saved;
        public int Failed => _failed;
        public string Folder => _folder;

        protected override void Pressed(InputDevice left, bool aimed, Vector3 origin, Vector3 direction, bool found, RaycastHit hit, bool onPiece)
        {
            Log(left, aimed, found, hit, onPiece);   // the probe's own line, as before
            string refusal = null;
            string path = null;
            if (World == null || !found || !onPiece || hit.rigidbody == null || !World.Owners.TryResolveFragment(hit.rigidbody, out LogicalFragmentId fragment, out float _))
            {
                refusal = !aimed ? "the left controller is not tracked" : "the line rests on no piece of the cut world";
            }
            else
            {
                path = Save(World, fragment, "press " + Probes + " at (" + hit.point.x.ToString("F2", CultureInfo.InvariantCulture) + ", " + hit.point.y.ToString("F2", CultureInfo.InvariantCulture) + ", "
                    + hit.point.z.ToString("F2", CultureInfo.InvariantCulture) + ")", out refusal);
            }

            if (path != null)
            {
                left.SendHapticImpulse(0, 0.6f, 0.12f);
            }
            else
            {
                _failed++;
                Debug.LogWarning(Prefix + "#" + Probes + " FAILED: " + refusal);
                left.SendHapticImpulse(0, 0.2f, 0.04f);
            }
        }

        /// <summary>Saves one piece as it stands now (the press's work; a test calls it without a controller). The file's path, or null with why.</summary>
        public string Save(CutWorldRoot world, LogicalFragmentId fragment, string note, out string refusal)
        {
            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
            ConvexTrimCapture capture;
            string path;
            try
            {
                capture = ConvexTrimCapture.Take(world, fragment, note, SupportReach, out refusal);
                if (capture == null) return null;
                path = ConvexTrimCaptureReplay.Write(capture, _folder, _saved + 1);
                FollowTheTrim();
                _log?.WriteTallies();   // the trim's attempts so far and its tallies, beside the pieces
            }
            catch (Exception e)
            {
                refusal = e.GetType().Name + ": " + e.Message;
                return null;
            }

            _saved++;
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - begin) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            int standIns = 0, boxes = 0;
            foreach (ConvexTrimCaptureSupport support in capture.supports)
            {
                if (support.kind == "bounds") boxes++;
                else if (!support.exact) standIns++;
            }

            CultureInfo inv = CultureInfo.InvariantCulture;
            Debug.Log(Prefix + "#" + _saved + " saved: fragment " + capture.fragment + " (root " + capture.root + (capture.propLineage ? ", a prop's" : "") + "; the trim's look: "
                + (string.IsNullOrEmpty(capture.productPass) ? "none" : capture.productPass)
                + (capture.trimEnabled ? "; the trim: " + capture.trimState + (capture.trimHasRecord ? ", its attempt ended " + capture.trimEnd + " at frame " + capture.trimEndedFrame + ", vertices "
                + capture.trimVerticesBefore + " -> " + capture.trimVerticesAfter + ", reach " + capture.trimReachBefore.ToString("F3", inv) + " -> " + capture.trimReachAfter.ToString("F3", inv) + " m" : ", no attempt on it")
                + (capture.trimmedAncestor >= 0 ? ", trimmed ancestor " + capture.trimmedAncestor : "") : "") + "), convexes " + capture.convexes.Length + ", mesh vertices " + capture.meshPositions.Length
                + ", " + (capture.sleeping ? "asleep" : capture.Resting ? "at rest" : "MOVING") + (capture.kinematic ? ", kinematic" : "") + (capture.fixedByAnchors ? ", held by anchors" : "")
                + (capture.joints > 0 ? ", joints " + capture.joints : "") + "; measured " + (capture.measuredClearance >= ConvexTrimCapture.NoSurface * 0.5f ? "no surface under it"
                : capture.measuredClearance.ToString("F3", inv) + " m above " + capture.measuredAgainst + " (layer " + capture.measuredAgainstLayer
                + (capture.measuredAgainstSupport < 0 ? ", NOT saved" : "") + ")") + "; near " + capture.supportsNear + ": saved " + capture.supports.Length + " (stand-ins " + standIns
                + ", boxes " + boxes + "), not colliding " + capture.supportsNotColliding + ", left out " + capture.supportsDropped + (capture.nearOverflow ? ", THE QUERY WAS FULL" : "")
                + "; in " + ms.ToString("F1", inv) + " ms -> " + path);
            return path;
        }

        private ConvexTrimRecordLog _log;

        /// <summary>The log of the trim's attempts (null while the world has no trim that is on).</summary>
        public ConvexTrimRecordLog TrimLog => _log;

        private void OnEnable() => Application.quitting += WriteTheTrimAtTheEnd;

        private void OnDisable() => Application.quitting -= WriteTheTrimAtTheEnd;

        // Every attempt of the world's trim goes to trim-records.txt as it ends (the trim itself keeps the newest ones only):
        // followed from the first frame the world's trim is on, and again where another world takes the first's place.
        private void LateUpdate() => FollowTheTrim();

        private void FollowTheTrim()
        {
            CutConvexTrim trim = World != null ? World.ConvexTrim : null;
            if (trim != null && !trim.Settings.enabled) trim = null;
            if (_log != null && _log.Trim == trim) return;
            if (_log != null)
            {
                _log.WriteTallies();
                _log.Dispose();
                _log = null;
            }

            if (trim == null || _folder == null) return;
            try
            {
                _log = new ConvexTrimRecordLog(trim, _folder, World.SpatialSplit);
                Debug.Log(Prefix + "the trim's attempts go to " + _log.Path + " as they end" + (_log.MissedBefore > 0 ? " (" + _log.MissedBefore + " EARLIER ONES WERE NOT KEPT)" : ""));
            }
            catch (Exception e)
            {
                Debug.LogWarning(Prefix + "the trim's attempts could not be followed: " + e.GetType().Name + ": " + e.Message);
            }
        }

        // As the application quits (before the world is destroyed): what is held is written, and the tallies of the whole session.
        private void WriteTheTrimAtTheEnd()
        {
            if (_log == null) return;
            _log.WriteTallies();
            Debug.Log(Prefix + "the trim's attempts written: " + _log.Lines + (_log.MissedBefore > 0 ? " (" + _log.MissedBefore + " earlier ones missing)" : "")
                + (_log.Failures > 0 ? ", WRITE FAILURES " + _log.Failures : "") + " -> " + _log.Path);
        }

        protected override void OnDestroy()
        {
            base.OnDestroy();
            if (_folder != null) Debug.Log(Prefix + "ended: saved " + _saved + ", presses that saved nothing " + _failed + ", folder " + _folder);
            if (_log != null)
            {
                _log.Dispose();
                _log = null;
            }

            if (Collector == this) Collector = null;
        }
    }
}
