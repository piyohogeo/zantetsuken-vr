using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// What became, under the additional split, of one fragment whose shape was replaced (the convex trim's publication,
    /// 2026-11-20): what the split held of the fragment just before -- how many attempts it had issued on it, how the
    /// last of them ended, which of its marks stood -- when the shape was replaced, when the split next issued an attempt
    /// on it and how that attempt ended. So that the attempts a replaced shape brings back can be counted in one session,
    /// with their waits, and set against what had been decided of the shape before.
    /// </summary>
    public sealed class SpatialSplitShapeRetry
    {
        public int fragment;

        /// <summary>Attempts the split had issued on the fragment when its shape was replaced: above 0, the next issue is one made again.</summary>
        public int issuedBefore;

        /// <summary>How the last attempt on the fragment before the replacement ended ("" where there was none, or it had not ended).</summary>
        public string endBefore = "";

        /// <summary>
        /// What stood of the fragment just before the replacement, as letters: S a mark not to try the input again (a
        /// failure), L a limit of the search reached on it, W a wait before it is tried again, G a light mark (passed
        /// over without a search), T held as too large alone, F waiting as a found candidate, O an attempt out. "" none.
        /// </summary>
        public string stateBefore = "";

        /// <summary>Times the shape was replaced before the next issue (1 as a rule).</summary>
        public int replacements;
        public int replacedFrame, issuedFrame, endedFrame;
        public bool issued, ended;

        /// <summary>The fragment had no owner any more before the split issued on it again (it was cut, or retired).</summary>
        public bool gone;
        public string end = "";

        /// <summary>From the (last) replacement to the issue, and from the issue to the attempt's end.</summary>
        public double waitToIssueSeconds, issueToEndSeconds;
        internal long replacedAt, issuedAt;
    }

    /// <summary>The waits of the split's candidates from the scan that first found them to their issue, of one kind of candidate.</summary>
    public sealed class SpatialSplitCandidateWaits
    {
        public long issued;
        public long frames, framesMost;
        public double seconds, secondsMost;

        /// <summary>Issued in the frame they were found; after 1 frame; 2 to 4; 5 to 9; 10 or more.</summary>
        public long sameFrame, oneFrame, twoToFour, fiveToNine, tenOrMore;

        internal void Add(int waitedFrames, double waitedSeconds)
        {
            issued++;
            frames += waitedFrames;
            if (waitedFrames > framesMost) framesMost = waitedFrames;
            seconds += waitedSeconds;
            if (waitedSeconds > secondsMost) secondsMost = waitedSeconds;
            if (waitedFrames <= 0) sameFrame++;
            else if (waitedFrames == 1) oneFrame++;
            else if (waitedFrames < 5) twoToFour++;
            else if (waitedFrames < 10) fiveToNine++;
            else tenOrMore++;
        }

        public string Describe()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            long n = Math.Max(1, issued);
            return issued + " (waited: mean " + (frames / (double)n).ToString("F2", inv) + " frames, most " + framesMost + "; mean " + (seconds * 1000.0 / n).ToString("F3", inv) + " ms, most "
                + (secondsMost * 1000.0).ToString("F3", inv) + " ms; in the frame found " + sameFrame + ", after 1 frame " + oneFrame + ", 2-4 " + twoToFour + ", 5-9 " + fiveToNine + ", 10 or more " + tenOrMore + ")";
        }
    }

    public sealed partial class CutSpatialSplit
    {
        /// <summary>Records of replaced shapes kept (the newest).</summary>
        public const int ShapeRetriesKept = 8192;

        // A diagnosis's tables are looked through for fragments without an owner once in this many notes, so that what is
        // kept of a fragment goes with the fragment (a session cuts thousands).
        private const int PruneEvery = 256;

        private struct IssuedNote
        {
            internal int issued;
            internal int lastEnd;   // a SpatialSplitEnd, or -1: none ended yet
        }

        private struct FirstFound
        {
            internal int frame;
            internal long at;
            internal long retryTurn;   // _retryTurns then
        }

        private readonly Dictionary<LogicalFragmentId, IssuedNote> _issuedOf = new Dictionary<LogicalFragmentId, IssuedNote>();
        private readonly Dictionary<LogicalFragmentId, SpatialSplitShapeRetry> _retryWaiting = new Dictionary<LogicalFragmentId, SpatialSplitShapeRetry>();
        private readonly Dictionary<Attempt, SpatialSplitShapeRetry> _retryOut = new Dictionary<Attempt, SpatialSplitShapeRetry>();
        private readonly Dictionary<LogicalFragmentId, FirstFound> _firstFound = new Dictionary<LogicalFragmentId, FirstFound>();
        private readonly List<SpatialSplitShapeRetry> _retries = new List<SpatialSplitShapeRetry>();
        private readonly List<LogicalFragmentId> _pruneScratch = new List<LogicalFragmentId>();
        private readonly int[] _retryEnds = new int[(int)SpatialSplitEnd.Count];
        private readonly int[] _firstEnds = new int[(int)SpatialSplitEnd.Count];
        private int _notesSincePrune, _retriesOutNow;
        private long _retryTurns;

        /// <summary>
        /// Of the fragments whose shape was replaced: issues that followed on a fragment the split had issued on before
        /// (attempts made again) and on one it had not (its first attempt, which the replacement did not cause); those of
        /// both that ended; those whose fragment was gone before another issue; records dropped past <see cref="ShapeRetriesKept"/>.
        /// </summary>
        public long ShapeRetriesIssued { get; private set; }
        public long ShapeFirstsIssued { get; private set; }
        public long ShapeRetriesEnded { get; private set; }
        public long ShapeRetriesGone { get; private set; }
        public long ShapeRetriesDropped { get; private set; }
        public double ShapeRetryWaitSeconds { get; private set; }
        public double ShapeRetryWaitSecondsMost { get; private set; }
        public long ShapeRetryWaitFrames { get; private set; }
        public int ShapeRetryWaitFramesMost { get; private set; }
        public double ShapeRetryRunSeconds { get; private set; }
        public double ShapeRetryRunSecondsMost { get; private set; }

        /// <summary>
        /// The waits from the scan that first found a candidate to its issue: of the candidates an attempt was made again
        /// for after a replaced shape; of the others while an attempt made again was out in some turn of their wait; of
        /// the others otherwise. And the turns in which candidates were held at the limit of attempts out, or of attempts
        /// waiting for the main thread, while an attempt made again was among those out.
        /// </summary>
        public SpatialSplitCandidateWaits RetryCandidateWaits { get; } = new SpatialSplitCandidateWaits();
        public SpatialSplitCandidateWaits OrdinaryWaitsBesideRetries { get; } = new SpatialSplitCandidateWaits();
        public SpatialSplitCandidateWaits OrdinaryWaitsWithout { get; } = new SpatialSplitCandidateWaits();
        public long LimitHoldsBesideRetries { get; private set; }
        public long LimitHoldsWithout { get; private set; }

        /// <summary>What the diagnosis keeps of fragments now (bounded by the fragments with an owner and <see cref="PruneEvery"/>).</summary>
        public int ShapeRetryFragmentsKept => _issuedOf.Count + _retryWaiting.Count + _firstFound.Count;

        /// <summary>The records, the oldest first (the newest <see cref="ShapeRetriesKept"/>).</summary>
        public IReadOnlyList<SpatialSplitShapeRetry> ShapeRetries => _retries;

        /// <summary>Attempts made again that ended as <paramref name="end"/>.</summary>
        public int ShapeRetriesEndedAs(SpatialSplitEnd end) => _retryEnds[(int)end];

        // What stands of the fragment now, as letters (SpatialSplitShapeRetry.stateBefore). Reads only.
        private string StateLetters(LogicalFragmentId fragment)
        {
            string s = string.Empty;
            if (_suppressed.ContainsKey(fragment)) s += "S";
            if (_limitReached.ContainsKey(fragment)) s += "L";
            if (_deferred.ContainsKey(fragment)) s += "W";
            if (_light.ContainsKey(fragment)) s += "G";
            if (_tooLarge.ContainsKey(fragment)) s += "T";
            if (_foundAt.ContainsKey(fragment)) s += "F";
            if (IsOut(fragment) || (_attempt != null && _attempt.fragment == fragment)) s += "O";
            return s;
        }

        // (from OnShapeReplaced, before the fragment's marks are voided)
        private void NoteShapeReplacedForRetry(LogicalFragmentId fragment)
        {
            if (!_retryWaiting.TryGetValue(fragment, out SpatialSplitShapeRetry retry))
            {
                _issuedOf.TryGetValue(fragment, out IssuedNote before);
                retry = new SpatialSplitShapeRetry
                {
                    fragment = fragment.value, issuedBefore = before.issued,
                    endBefore = before.issued > 0 && before.lastEnd >= 0 ? ((SpatialSplitEnd)before.lastEnd).ToString() : string.Empty,
                    stateBefore = StateLetters(fragment),
                };
                _retryWaiting.Add(fragment, retry);
                if (_retries.Count >= ShapeRetriesKept)
                {
                    _retries.RemoveAt(0);
                    ShapeRetriesDropped++;
                }

                _retries.Add(retry);
            }

            retry.replacements++;
            retry.replacedFrame = Time.frameCount;
            retry.replacedAt = Stopwatch.GetTimestamp();
            PruneSometimes();
        }

        // (from the issue, once the queue took the attempt)
        private void NoteIssueForRetry(Attempt attempt)
        {
            _issuedOf.TryGetValue(attempt.fragment, out IssuedNote note);
            if (note.issued == 0) note.lastEnd = -1;
            note.issued++;
            _issuedOf[attempt.fragment] = note;
            PruneSometimes();
            if (!_retryWaiting.TryGetValue(attempt.fragment, out SpatialSplitShapeRetry retry)) return;
            _retryWaiting.Remove(attempt.fragment);
            retry.issued = true;
            retry.issuedFrame = Time.frameCount;
            retry.issuedAt = Stopwatch.GetTimestamp();
            retry.waitToIssueSeconds = (retry.issuedAt - retry.replacedAt) / (double)Stopwatch.Frequency;
            _retryOut[attempt] = retry;
            if (retry.issuedBefore == 0)
            {
                ShapeFirstsIssued++;
                return;
            }

            _retriesOutNow++;
            ShapeRetriesIssued++;
            ShapeRetryWaitSeconds += retry.waitToIssueSeconds;
            if (retry.waitToIssueSeconds > ShapeRetryWaitSecondsMost) ShapeRetryWaitSecondsMost = retry.waitToIssueSeconds;
            int frames = retry.issuedFrame - retry.replacedFrame;
            ShapeRetryWaitFrames += frames;
            if (frames > ShapeRetryWaitFramesMost) ShapeRetryWaitFramesMost = frames;
        }

        // (from the attempt's end)
        private void NoteEndForRetry(Attempt attempt, SpatialSplitEnd end)
        {
            if (_issuedOf.TryGetValue(attempt.fragment, out IssuedNote note))
            {
                note.lastEnd = (int)end;
                _issuedOf[attempt.fragment] = note;
            }

            if (_retryOut.Count == 0 || !_retryOut.TryGetValue(attempt, out SpatialSplitShapeRetry retry)) return;
            _retryOut.Remove(attempt);
            retry.ended = true;
            retry.endedFrame = Time.frameCount;
            retry.end = end.ToString();
            retry.issueToEndSeconds = (Stopwatch.GetTimestamp() - retry.issuedAt) / (double)Stopwatch.Frequency;
            if (retry.issuedBefore == 0)
            {
                _firstEnds[(int)end]++;
                return;
            }

            _retriesOutNow = Math.Max(0, _retriesOutNow - 1);
            ShapeRetriesEnded++;
            _retryEnds[(int)end]++;
            ShapeRetryRunSeconds += retry.issueToEndSeconds;
            if (retry.issueToEndSeconds > ShapeRetryRunSecondsMost) ShapeRetryRunSecondsMost = retry.issueToEndSeconds;
        }

        // (from the scan, where a candidate is noted as found: the first time it is, until it is issued or is one no more)
        private void NoteFirstFound(LogicalFragmentId fragment)
        {
            if (_firstFound.ContainsKey(fragment)) return;
            _firstFound.Add(fragment, new FirstFound { frame = Time.frameCount, at = Stopwatch.GetTimestamp(), retryTurn = _retryTurns });
        }

        // (from the scan, where a candidate found is one no more without an issue)
        private void ForgetFirstFound(LogicalFragmentId fragment) => _firstFound.Remove(fragment);

        // (from the several attempts' turn, before it issues: a turn with an attempt made again out is counted)
        private void NoteTurnForCandidateWaits()
        {
            if (_retriesOutNow > 0) _retryTurns++;
        }

        // (from the several attempts' turn, where candidates are held at a limit)
        private void NoteLimitHold()
        {
            if (_retriesOutNow > 0) LimitHoldsBesideRetries++;
            else LimitHoldsWithout++;
        }

        // (from the several attempts' turn, once a found candidate was issued; after NoteIssueForRetry)
        private void NoteCandidateIssued(LogicalFragmentId fragment, Attempt attempt)
        {
            if (!_firstFound.TryGetValue(fragment, out FirstFound first)) return;
            _firstFound.Remove(fragment);
            int frames = Time.frameCount - first.frame;
            double seconds = (Stopwatch.GetTimestamp() - first.at) / (double)Stopwatch.Frequency;
            if (_retryOut.TryGetValue(attempt, out SpatialSplitShapeRetry retry) && retry.issuedBefore > 0) RetryCandidateWaits.Add(frames, seconds);
            else if (first.retryTurn != _retryTurns) OrdinaryWaitsBesideRetries.Add(frames, seconds);
            else OrdinaryWaitsWithout.Add(frames, seconds);
        }

        private void PruneSometimes()
        {
            if (++_notesSincePrune < PruneEvery) return;
            _notesSincePrune = 0;
            PruneShapeRetries();
        }

        /// <summary>Drops what the diagnosis keeps of fragments that have no owner any more. Main thread.</summary>
        internal void PruneShapeRetries()
        {
            PhysicsOwnerRegistry owners = _world.Owners;
            if (owners == null) return;
            _pruneScratch.Clear();
            foreach (KeyValuePair<LogicalFragmentId, IssuedNote> e in _issuedOf)
            {
                if (!owners.TryGet(e.Key, out _)) _pruneScratch.Add(e.Key);
            }

            foreach (LogicalFragmentId fragment in _pruneScratch) _issuedOf.Remove(fragment);
            _pruneScratch.Clear();
            foreach (KeyValuePair<LogicalFragmentId, SpatialSplitShapeRetry> e in _retryWaiting)
            {
                if (!owners.TryGet(e.Key, out _)) _pruneScratch.Add(e.Key);
            }

            foreach (LogicalFragmentId fragment in _pruneScratch)
            {
                _retryWaiting[fragment].gone = true;
                _retryWaiting.Remove(fragment);
                ShapeRetriesGone++;
            }

            _pruneScratch.Clear();
            foreach (KeyValuePair<LogicalFragmentId, FirstFound> e in _firstFound)
            {
                if (!owners.TryGet(e.Key, out _)) _pruneScratch.Add(e.Key);
            }

            foreach (LogicalFragmentId fragment in _pruneScratch) _firstFound.Remove(fragment);
            _pruneScratch.Clear();
        }

        /// <summary>The replaced shapes and what the split made of them, in one line.</summary>
        public string DescribeShapeRetries()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            string Ms(double seconds) => (seconds * 1000.0).ToString("F3", inv);
            var text = new StringBuilder(512);
            text.Append("spatial split after a replaced shape: shapes replaced ").Append(ShapesReplaced).Append("; attempts made again (the split had issued on the fragment before) ")
                .Append(ShapeRetriesIssued).Append(", ended ").Append(ShapeRetriesEnded).Append(':');
            for (int e = 0; e < _retryEnds.Length; e++)
            {
                if (_retryEnds[e] != 0) text.Append(' ').Append((SpatialSplitEnd)e).Append(' ').Append(_retryEnds[e]);
            }

            long issued = Math.Max(1, ShapeRetriesIssued), ended = Math.Max(1, ShapeRetriesEnded);
            text.Append("; from the replacement to the issue: mean ").Append((ShapeRetryWaitFrames / (double)issued).ToString("F1", inv)).Append(" frames (most ").Append(ShapeRetryWaitFramesMost)
                .Append("), mean ").Append(Ms(ShapeRetryWaitSeconds / issued)).Append(" ms (most ").Append(Ms(ShapeRetryWaitSecondsMost)).Append("); from the issue to the end: mean ")
                .Append(Ms(ShapeRetryRunSeconds / ended)).Append(" ms (most ").Append(Ms(ShapeRetryRunSecondsMost)).Append("); first attempts after a replacement (not made again) ")
                .Append(ShapeFirstsIssued).Append(':');
            for (int e = 0; e < _firstEnds.Length; e++)
            {
                if (_firstEnds[e] != 0) text.Append(' ').Append((SpatialSplitEnd)e).Append(' ').Append(_firstEnds[e]);
            }

            text.Append("; replaced and not issued on since ").Append(_retryWaiting.Count).Append(", gone before another issue ").Append(ShapeRetriesGone).Append("; attempts in all ").Append(Issued)
                .Append("; kept of fragments ").Append(ShapeRetryFragmentsKept);
            if (ShapeRetriesDropped > 0) text.Append("; records dropped ").Append(ShapeRetriesDropped);
            return text.ToString();
        }

        /// <summary>The waits of the candidates from the scan that found them to their issue, in one line (whether or not a shape was replaced).</summary>
        public string DescribeCandidateWaits()
        {
            return "spatial split candidates, from first found to issue: made again after a replaced shape " + RetryCandidateWaits.Describe() + "; the others, an attempt made again out in a turn of their wait "
                + OrdinaryWaitsBesideRetries.Describe() + "; the others otherwise " + OrdinaryWaitsWithout.Describe() + "; turns candidates were held at a limit of attempts: beside attempts made again "
                + LimitHoldsBesideRetries + ", without " + LimitHoldsWithout;
        }

        /// <summary>The heads of a record's line (<see cref="ShapeRetryLine"/>).</summary>
        public const string ShapeRetryHeads = "fragment\tissuedBefore\tendBefore\tstateBefore\treplacements\treplacedFrame\tissued\tissuedFrame\twaitToIssueMs\tended\tendedFrame\tend\tissueToEndMs\tgone";

        /// <summary>One record as a tab-separated line (no line end).</summary>
        public static string ShapeRetryLine(SpatialSplitShapeRetry r)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            return r.fragment.ToString(inv) + "\t" + r.issuedBefore.ToString(inv) + "\t" + r.endBefore + "\t" + r.stateBefore + "\t" + r.replacements.ToString(inv) + "\t" + r.replacedFrame.ToString(inv)
                + "\t" + (r.issued ? "1" : "0") + "\t" + r.issuedFrame.ToString(inv) + "\t" + (r.waitToIssueSeconds * 1000.0).ToString("F3", inv) + "\t" + (r.ended ? "1" : "0")
                + "\t" + r.endedFrame.ToString(inv) + "\t" + r.end + "\t" + (r.issueToEndSeconds * 1000.0).ToString("F3", inv) + "\t" + (r.gone ? "1" : "0");
        }
    }
}
