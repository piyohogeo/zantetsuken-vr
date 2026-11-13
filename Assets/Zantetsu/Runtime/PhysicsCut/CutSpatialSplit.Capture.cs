#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Diagnostics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The capture of the issued inputs (DESIGN 7.9.8; development and the Editor only): observation, nothing more.
    /// The search, its order, the conditions, the limits, the publication and the physics are as they are without it.
    /// What it adds to Main -- the copy at the issue and the copy of the outputs where they are first read -- is measured
    /// apart and kept out of what the issue is expected to cost; the frame pays it all the same.
    /// </summary>
    public sealed partial class CutSpatialSplit
    {
        /// <summary>
        /// The capture this split hands its issued inputs to: the launch's (null without its argument, set where the
        /// split is made), or a test's. The capture knows its splits, for the application's quitting.
        /// </summary>
        internal SpatialSplitCaptureSink Capture
        {
            get => _capture;
            set
            {
                if (_attempt != null || _attempts.Count != 0)
                {
                    // An attempt is out whose input went to the sink of its issue: its result goes to the same sink. The switch
                    // waits for that attempt's collection and is applied at its end (GRAPH-CONNECT-5 review 1).
                    _pendingCapture = value;
                    _capturePending = true;
                    return;
                }

                if (ReferenceEquals(_capture, value)) return;
                _capture?.Detach(this);
                _capture = value;
                _capture?.Attach(this);
                ReevaluateGraph();   // the graph stage does not run with the capture on (GRAPH-CONNECT-4 §3-11)
            }
        }

        private SpatialSplitCaptureSink _capture, _pendingCapture;
        private bool _capturePending;

        /// <summary>A capture switch asked for while an attempt was out: applied when that attempt is collected.</summary>
        internal bool CapturePending => _capturePending;

        /// <summary>This split's world, as the capture numbers worlds.</summary>
        internal long CaptureWorld { get; } = SpatialSplitCaptureSession.NextWorld();

        /// <summary>Main time and bytes of the capture's copies at the issue, and of the copies of the outputs.</summary>
        public double CaptureCopySeconds { get; private set; }
        public double MaxCaptureCopySeconds { get; private set; }
        public long CaptureCopyBytes { get; private set; }
        public long MaxCaptureCopyBytes { get; private set; }
        public double CaptureResultSeconds { get; private set; }
        public double MaxCaptureResultSeconds { get; private set; }

        // NPC-BONE-LINK-1: the existing NPC identification -- the fragment's ledger family root is one the prepared character's cut
        // marked for the piece lifetime. Not the bones' records (a fragment of an NPC whose bones are not known is still an NPC's).
        private bool IsNpcLineage(LogicalFragmentId fragment)
        {
            return _world.Lifetime != null && _world.Ledger.TryGetRoot(fragment, out LogicalFragmentId root) && _world.Lifetime.IsMarkedLineage(root);
        }

        /// <summary>
        /// NPC-ATTEMPT-CAPTURE-1: the topology tables handed to the capture's writer, and the sidecars that were not (the bones', the
        /// topology's, a result line), with the last reason. A failure is also a line of capture-failures.jsonl where the queue has room.
        /// </summary>
        public long CaptureTopologies { get; private set; }
        public long CaptureBoneFailures { get; private set; }
        public long CaptureTopologyFailures { get; private set; }
        public string LastCaptureFailure { get; private set; } = "";

        /// <summary>Tests only: the topology table's copy is refused.</summary>
        internal static bool TestFailCaptureTopology;

        // At the issue, after the bones' sidecar: the geometry's own render vertex to topology vertex table for every vertex the
        // attempt's indices name (every vertex block: the base's, each cut's, a cap's auxiliary vertices), through the map the decision
        // reads, while the attempt holds the geometry's index lease -- the collection of picked pieces' copy (NpcPieceTopologySample),
        // named by the attempt's serial as its sample and its bones are.
        private unsafe void CaptureTopology(Attempt attempt)
        {
            long begin = Stopwatch.GetTimestamp();
            string why = null;
            try
            {
                NpcPieceTopologySample table = TestFailCaptureTopology ? null : NpcPieceTopologySample.Take(_world.Storage, attempt.input.geometry, attempt.indices, attempt.indexCount, out why);
                if (TestFailCaptureTopology) why = "refused for a test";
                if (table != null)
                {
                    table.sessionId = Capture.sessionId;
                    table.world = CaptureWorld;
                    table.pick = checked((int)attempt.serial);
                    table.fragment = attempt.fragment.value;
                    table.epoch = attempt.input.epoch;
                    SpatialSplitBoneItem item = NpcPiecePickCopy.MakeTopologyItem(table, null, out string _);
                    if (Capture.TryReserve(item.bytes, false))
                    {
                        Capture.HandBone(item);
                        CaptureTopologies++;
                        attempt.topologyCaptured = true;
                    }
                    else
                    {
                        why = "no room in the queue (" + Capture.LastRefusal + ")";
                    }
                }
            }
            catch (Exception e)
            {
                why = e.GetType().Name + ": " + e.Message;
            }

            if (!attempt.topologyCaptured) NoteCaptureFailure(attempt, "topology", why ?? "not known");
            double spent = Since(begin);
            attempt.captureCopySeconds += spent;
            CaptureCopySeconds += spent;
        }

        private void NoteCaptureFailure(Attempt attempt, string what, string why)
        {
            if (what == "bones") CaptureBoneFailures++; else CaptureTopologyFailures++;
            LastCaptureFailure = what + " of attempt " + attempt.serial + ": " + why;
            UnityEngine.Debug.LogError("SPLIT CAPTURE NPC: " + LastCaptureFailure);
            SpatialSplitCaptureSink sink = Capture;
            if (sink == null) return;
            var item = new SpatialSplitBoneItem
            {
                index = "capture-failures.jsonl",
                line = "{\"world\":" + CaptureWorld + ",\"attempt\":" + attempt.serial + ",\"fragment\":" + attempt.fragment.value + ",\"epoch\":" + attempt.input.epoch
                    + ",\"what\":\"" + what + "\",\"why\":" + SpatialSplitCaptureSink.Json(why ?? "") + "}",
                bytes = SpatialSplitCaptureResult.Overhead,
            };
            if (sink.TryReserve(item.bytes, true)) sink.HandBone(item);
        }

        // At the issue, with the input complete and leased and the work not yet offered: the copy, or none.
        private SpatialSplitCaptureSample CaptureInput(Attempt attempt)
        {
            SpatialSplitCaptureSink sink = Capture;
            if (sink == null)
            {
                return null;
            }

            long begin = Stopwatch.GetTimestamp();
            SpatialSplitCaptureSample sample = null;
            if (sink.IsCapturing)
            {
                var identity = new SpatialSplitCaptureIdentity
                {
                    world = CaptureWorld,
                    attempt = attempt.serial,
                    issuedFrame = attempt.issuedFrame,
                    fragment = attempt.fragment.value,
                    epoch = attempt.input.epoch,
                    objectName = attempt.input.owner.Root != null ? attempt.input.owner.Root.name : "",
                };
                LogicalCutLedger ledger = _world.Ledger;
                if (ledger.TryGetRoot(attempt.fragment, out LogicalFragmentId root)) identity.rootFragment = root.value;
                if (ledger.TryGetSplitSource(attempt.fragment, out LogicalFragmentId source)) identity.splitSource = source.value;
                LogicalFragmentId at = attempt.fragment;
                for (int depth = 0; depth < 4096 && ledger.TryGetParent(at, out LogicalFragmentId parent); depth++)
                {
                    identity.generation++;
                    at = parent;
                }

                sample = SpatialSplitCaptureCopy.TryTake(sink, attempt, in identity);
            }
            else
            {
                sink.NoteRefused(sink.IsClosed ? SpatialSplitCaptureRefusal.Closed : SpatialSplitCaptureRefusal.Stopped);
            }

            double spent = Since(begin);
            if (sample != null) sample.copySeconds = spent;
            attempt.captureCopySeconds = spent;
            attempt.captureCopyBytes = sample != null ? sample.bytes : 0;
            CaptureCopySeconds += spent;
            MaxCaptureCopySeconds = Math.Max(MaxCaptureCopySeconds, spent);
            CaptureCopyBytes += attempt.captureCopyBytes;
            MaxCaptureCopyBytes = Math.Max(MaxCaptureCopyBytes, attempt.captureCopyBytes);
            return sample;
        }

        // Where the outputs are first read (Main, the work collected): a copy of them, for an attempt whose input was captured.
        private void TakeCaptureResult(Attempt attempt)
        {
            if (!attempt.captured || attempt.captureResult != null)
            {
                return;
            }

            if (!attempt.ran || attempt.cancelled || attempt.failure != null)
            {
                // The numbers did not run to their end: there is no answer of the search to keep (the result says why).
                return;
            }

            if (attempt.captureOutputsNotKept != null || Capture == null)
            {
                return;   // already refused once: the result goes without them
            }

            long begin = Stopwatch.GetTimestamp();
            try
            {
                attempt.captureResult = SpatialSplitCaptureCopy.TakeResult(Capture, attempt, out string refused);
                attempt.captureOutputsNotKept = refused;
            }
            catch (Exception e)
            {
                // The capture's own failure: the attempt goes on as it would without it; its result is written without the
                // outputs, saying why (and so counted 結果未記録 by the replay, never taken for a search's answer).
                attempt.captureResult = null;
                attempt.captureOutputsNotKept = "the outputs' copy failed: " + e.GetType().Name + ": " + e.Message;
                Capture?.Stop(SpatialSplitCaptureRefusal.CopyFailed, attempt.captureOutputsNotKept);
            }

            double spent = Since(begin);
            if (attempt.captureResult != null) attempt.captureResult.resultSeconds = spent;
            CaptureResultSeconds += spent;
            MaxCaptureResultSeconds = Math.Max(MaxCaptureResultSeconds, spent);
        }

        /// <summary>
        /// The application's quitting (SpatialSplitCaptureSink.Quit, before the worlds go): the outstanding attempt's
        /// result is handed now, as it stands, and the attempt hands nothing later. Main.
        /// </summary>
        internal void EndCaptureForQuit()
        {
            if (_attempt != null) EndCaptureForQuit(_attempt);
            for (int i = 0; i < _attempts.Count; i++) EndCaptureForQuit(_attempts[i]);   // SPLIT-CANDIDATES-2: every attempt out
        }

        private void EndCaptureForQuit(Attempt attempt)
        {
            if (attempt == null || !attempt.captured)
            {
                return;
            }

            EndCapture(attempt, "AppQuit",
                attempt.collected ? (attempt.cut && attempt.applied ? "collected, its meshes made: not yet published" : "collected, not yet applied")
                : attempt.offered ? (attempt.stage == AttemptStage.Baking ? "its meshes out to be baked" : "out on a worker: its outputs were never read")
                : "waiting for a place in the queue");
        }

        // The attempt is over, however (an end of SpatialSplitEnd, the world's end, abandoned while it ran, the application
        // quitting): its result to the capture -- the numbers' outputs where they were read, or read now where they came
        // back and were never looked at; the later stages as they stand now where no work of it is out -- and nothing of
        // it kept here.
        private void EndCapture(Attempt attempt, string end, string why)
        {
            if (!attempt.captured)
            {
                return;
            }

            if (attempt.captureResult == null && attempt.collected && attempt.stage == AttemptStage.Numbers && !attempt.abandoned)
            {
                TakeCaptureResult(attempt);
            }

            attempt.captured = false;
            SpatialSplitCaptureResult result = attempt.captureResult ?? new SpatialSplitCaptureResult { observed = false };
            attempt.captureResult = null;
            bool noWorkOut = (attempt.collected || !attempt.offered) && !attempt.abandoned;
            if (!result.observed && !attempt.abandoned && ((attempt.collected && attempt.stage == AttemptStage.Numbers) || attempt.stage == AttemptStage.Baking))
            {
                // The numbers came back (a bake out reads none of what is noted here).
                SpatialSplitCaptureCopy.NoteNumbers(result, attempt);
            }

            if (noWorkOut)
            {
                SpatialSplitCaptureCopy.NoteStages(result, attempt);
            }

            // The numbers ran and their outputs could not be kept: said, and counted apart (never as a search that found nothing).
            result.outputsNotKept = !result.observed && attempt.ran && !attempt.cancelled && attempt.failure == null ? attempt.captureOutputsNotKept : null;
            result.world = CaptureWorld;
            result.attempt = attempt.serial;
            result.issuedFrame = attempt.issuedFrame;
            result.endFrame = Time.frameCount;
            result.end = end;
            result.copySeconds = attempt.captureCopySeconds;
            result.copyBytes = attempt.captureCopyBytes;
            result.detail = why ?? (attempt.cancelled ? "cancelled"
                : attempt.failure != null ? "failed: " + attempt.failure.GetType().Name
                : attempt.collected && !attempt.ran && attempt.stage == AttemptStage.Numbers ? "did not run"
                : end == nameof(SpatialSplitEnd.RoomShort) || end == nameof(SpatialSplitEnd.NotBuildable) ? LastRefusal
                : null);

            // To the sink its room was reserved in (the outputs' copy), else to this split's.
            SpatialSplitCaptureSink sink = result.sink ?? Capture;
            if (sink == null)
            {
                result.Discard();
                return;
            }

            sink.HandResult(result);
        }
    }
}
#endif
