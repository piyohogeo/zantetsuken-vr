using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.Sandbox
{
    /// <summary>One finite diagnostic write, with no Unity objects or callbacks on its worker.</summary>
    internal sealed class CheckReportWrite
    {
        private readonly Thread _worker;
        private volatile bool _done;
        private Exception _failure;
        internal bool Done => _done;
        internal Exception Failure => _done ? _failure : null;

        internal CheckReportWrite(Action write)
        {
            if (write == null) throw new ArgumentNullException(nameof(write));
            _worker = new Thread(() =>
            {
                try { write(); }
                catch (Exception error) { _failure = error; }
                finally { _done = true; }
            }) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "Check Timeline Writer" };
            _worker.Start();
        }

        internal void Wait() => _worker.Join();
        internal void ThrowIfFailed()
        {
            if (!_done) throw new InvalidOperationException("Timeline writer has not completed");
            if (_failure != null) throw new IOException("Timeline save failed", _failure);
        }
    }

    public static partial class SandboxPropSlashPlayerCheck
    {
        private static IEnumerator ReleaseLogAcrossFrames()
        {
            var held = s_heldLog;
            if (held == null) yield break;
            // Keep holding while draining, so lines added by the live scene retain their order.
            while (s_heldLog == held && s_heldLogReleased < held.Count)
            {
                long began = System.Diagnostics.Stopwatch.GetTimestamp();
                int count = 0;
                do
                {
                    Exception failure = null;
                    try { Debug.Log(Prefix + held[s_heldLogReleased]); }
                    catch (Exception error) { failure = error; }
                    if (failure != null) { s_heldLogReleaseFailure = failure; yield break; }
                    s_heldLogReleased++;
                    count++;
                } while (s_heldLogReleased < held.Count && count < 16
                    && (System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency < 2.0);
                yield return null;
            }
            if (s_heldLog == held) { s_heldLog = null; s_heldLogReleased = 0; }
        }

        private sealed partial class Walk
        {
            private CheckReportWrite _timelineWrite;
            private bool _finalTimelinePrepared;
            private Exception _finalTimelineFailure;

            private bool SaveReportsAcrossFrames
            {
                get
                {
#if UNITY_ANDROID && !UNITY_EDITOR
                    return multiNpc && replayInRealtime && retainSceneAfterMeasurement && !MobPlanMode && !building && !cityWalk;
#else
                    return false;
#endif
                }
            }

            private static bool NumericTimelineDiagnosis
            {
                get
                {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                    return s_numericDiagnosis;
#else
                    return false;
#endif
                }
            }

            private IEnumerator SaveTimelineAcrossFrames(bool final)
            {
                if (_timelineRecorders == null) yield break;
                if (_timelineWrite != null)
                {
                    while (!_timelineWrite.Done) yield return null;
                    _timelineWrite = null;
                }
                CheckReportWrite write = null;
                Exception preparationFailure = null;
                try
                {
                    FrameRow[] rows = _timeline.ToArray();
                    // RecordFrame can still fill the last row's prior-frame markers and display counts in the next LateUpdate.
                    // Earlier rows are never modified again; the one mutable tail is frozen before the worker starts.
                    if (rows.Length > 0) rows[rows.Length - 1] = rows[rows.Length - 1].FrozenCopy();
                    string[] names = _timelineMarkers.Select(marker => marker.name).ToArray();
                    bool diagnosis = NumericTimelineDiagnosis;
                    string path = Path.Combine(directory, final ? "frames.csv" : "frames-at-cuts-complete.csv");
                    write = _timelineWrite = new CheckReportWrite(() => File.WriteAllText(path, FormatTimeline(rows, names, diagnosis)));
                }
                catch (Exception error) { preparationFailure = error; }
                if (preparationFailure != null)
                {
                    if (final) PrepareFinalTimelineResult(preparationFailure);
                    else Guarded("timeline at cuts complete", () => { throw preparationFailure; });
                    yield break;
                }
                while (!write.Done) yield return null;
                _timelineWrite = null;
                if (final) PrepareFinalTimelineResult(write.Failure);
                else Guarded("timeline at cuts complete", write.ThrowIfFailed);
            }

            private void PrepareFinalTimelineResult(Exception failure)
            {
                _finalTimelinePrepared = true;
                _finalTimelineFailure = failure;
                try { CloseTimelineRecorders(failure == null); }
                catch (Exception error)
                {
                    _finalTimelineFailure = failure == null ? error : new AggregateException(failure, error);
                    // A disposal/diagnostic exception must not prevent Complete from counting the closing failure or notifying Done.
                    if (_timelineRecorders != null)
                    {
                        foreach (var recorder in _timelineRecorders)
                        {
                            try { recorder.Dispose(); }
                            catch (Exception) { }
                        }
                        _timelineRecorders = null;
                    }
                }
            }

            private sealed partial class FrameRow
            {
                internal FrameRow FrozenCopy()
                {
                    var copy = (FrameRow)MemberwiseClone();
                    copy.markers = markers == null ? null : (long[])markers.Clone();
                    copy.markerRoom = null;
                    copy.validateRoom = null;
                    copy.placeStructuralRoom = copy.placePlacementOnlyRoom = null;
                    if (validate != null) { copy.validate = new VpValidateCounts(); copy.validate.CopyFrom(validate); }
                    if (placeStructural != null) { copy.placeStructural = new VpPlaceCounts(); copy.placeStructural.CopyFrom(placeStructural); }
                    if (placePlacementOnly != null) { copy.placePlacementOnly = new VpPlaceCounts(); copy.placePlacementOnly.CopyFrom(placePlacementOnly); }
                    return copy;
                }
            }
        }
    }
}
