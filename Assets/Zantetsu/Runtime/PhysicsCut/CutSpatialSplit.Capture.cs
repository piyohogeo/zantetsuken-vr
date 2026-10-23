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
                if (_attempt != null)
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
            Attempt attempt = _attempt;
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
