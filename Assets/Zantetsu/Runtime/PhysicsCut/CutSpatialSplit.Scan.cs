#if UNITY_EDITOR || DEVELOPMENT_BUILD
#define ZANTETSU_OBSERVE
#endif
using System;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// Why a turn of the several-attempts control issued nothing -- or, where it issued, what stopped it issuing more
    /// (SPLIT-CANDIDATES-2, review of 2026-11-13). One value a turn.
    /// </summary>
    public enum SpatialSplitNoIssue
    {
        /// <summary>Nothing stopped the turn: every candidate found was issued (or the turn issued and none was left).</summary>
        None = 0,

        /// <summary>The walk stands idle: the last whole scan left nothing to try and nothing it reads has changed since.</summary>
        Idle,

        /// <summary>A whole scan, and no fragment is a candidate now (the round is done).</summary>
        NoCandidate,

        /// <summary>A switch of the capture or of the graph stage waits for the attempts out: nothing new is issued until it is applied.</summary>
        SettingsPending,

        /// <summary>As many attempts are out as may be.</summary>
        CountLimit,

        /// <summary>As many attempts wait for the main thread as may.</summary>
        WaitingMainLimit,

        /// <summary>The frame's holds, as <see cref="SpatialSplitHold"/> names them.</summary>
        HeavyWork,
        GarbageCollection,
        CutInFlight,
        StructureChanged,
        DisplayRoomGrown,

        /// <summary>What is left of the frame's Main budget would not hold the scan and an issue, or a further issue.</summary>
        BudgetShort,

        /// <summary>The scan was cut short for the budget before it found a candidate: it goes on from there in the next turn.</summary>
        ScanCutShort,

        /// <summary>What the attempts out hold, with what the next one would, is past the limit: it waits for one to end.</summary>
        BytesLimit,

        /// <summary>The only candidates left need, each alone, more than the limit of held bytes: held with that reason until the limit or their input changes.</summary>
        TooLargeAlone,

        /// <summary>No place in the dispatcher's queue.</summary>
        QueueFull,

        /// <summary>The candidate's geometry could not be read now.</summary>
        NotReadable,

        /// <summary>What was found a candidate was no longer one when it came to be issued (its input had moved on).</summary>
        StaleAtUse,

        Count,
    }

    /// <summary>What one scan of the owners' fragments came to (<see cref="CutSpatialSplit.LastScan"/>).</summary>
    public struct SpatialSplitScan
    {
        public int frame, owners;

        /// <summary>Fragments left aside for an attempt of theirs being out.</summary>
        public int attemptOut;

        /// <summary>Fragments that ended at the light look: a valid mark of an input tried and not split, of a search limit reached, of a wait still running.</summary>
        public int lightSuppressed, lightLimitReached, lightDeferred;

        /// <summary>Fragments looked at in detail (owner, geometry, the marks' tables, the display), and what the look said.</summary>
        public int detailed, candidates, suppressed, limitReached, deferred, displayState, tooLarge, otherPassed;

        /// <summary>Whether the display's answers were taken in this scan (false: no fragment came as far as the display, or a current view was there already).</summary>
        public bool viewFilled, viewReused;

        /// <summary>Whether every fragment was looked at; cut short for the budget otherwise.</summary>
        public bool complete;

        /// <summary>Whether something passed over is to be looked at again without any watched number changing (the display's state).</summary>
        public bool wanting;

        /// <summary>Seconds: the whole scan, of it the detailed looks (the view's preparation apart), of it the view's preparation.</summary>
        public double totalSeconds, detailSeconds, viewSeconds;

        public int LightExcluded => lightSuppressed + lightLimitReached + lightDeferred;

        /// <summary>The light part: the list, the attempts out, the marks.</summary>
        public double LightSeconds => Math.Max(0.0, totalSeconds - detailSeconds - viewSeconds);
    }

    // SPLIT-CANDIDATES-2, the review of 2026-11-13: the scan the several-attempts control and the candidate probe share.
    //
    // **The light look first.** A fragment whose input was tried and not split (the mark), whose search reached a fixed limit, or
    // that waits for a retry is, for as long as that input and that search stand, no candidate: nothing of its owner, its geometry
    // or the display need be read to know it. Such a fragment has a *light mark* here -- what the last detailed look said --
    // and a scan asks for that first (one table), going on to the detailed look only for the rest.
    //
    // **What a light mark is valid for.** For the input generation of its fragment and the generation of the search:
    //   - the search's generation is the mark epoch (_markEpoch: the settings' search, the graph stage's effective state); a light
    //     mark carries the epoch it was made under and is void under another;
    //   - the input's generation is kept by removal: a light mark is dropped where anything Input.Equals tells apart can have
    //     moved for its fragment -- the owner (the registry's OwnerAdded / OwnerRetiring; an owner's shape is its own for life, so
    //     the shape goes with the owner), a Provisional pair of the fragment (coming or ending), the geometry (the DAG's
    //     GeometryChanged: registered, committed, handed on by a split, forgotten). The anchors of a live fragment do not change
    //     (they are given at its making and taken at its retirement, which retires the owner). And where the tables the mark was
    //     read from change for the fragment (a mark set or removed, a limit noted, a wait begun, taken or cancelled);
    //   - a wait's light mark also carries the frame the wait ends at, and is void from that frame on.
    // A dropped mark is not a verdict: the next scan looks at the fragment in detail and, if the tables still say the same of the
    // same input, makes the light mark again.
    // What passes for a reason of the moment -- the display's state or room, a cut under way, a geometry still open, the budget, the
    // queue -- gets no light mark: it is looked at again in every scan.
    //
    // **The display's answers when they are first needed.** A detailed look reads the display last. The first one of a scan that
    // comes that far takes the one-pass view (VpLogicalCutDisplay.SplitView); a scan in which none does takes nothing. A view is
    // shared by every look of the same reading stretch -- the probe's scan, the turn's, the look at a candidate before its issue --
    // for as long as it is current (the frame, the registrations, the adoptions, the commits, the tails), and is taken again once
    // it is not. An issue and a publication read the display, the input, the room and the lease themselves at that moment.
    //
    // **The candidates are used in the frame they are found.** The scan leaves its candidates in a list, the oldest first. The
    // turn issues from it while the counts, the bytes and the Main budget allow; what is left stays for the next turn, at the
    // head of the list (the order is kept: a candidate is not overtaken by a later one), and is looked at again in detail before
    // it is used unless nothing its input reads has moved since it was found in this very frame.
    public sealed partial class CutSpatialSplit
    {
        private struct LightMark
        {
            internal SpatialSplitPass pass;
            internal int epoch;     // the search's generation the mark was made under
            internal long until;    // a wait: the frame it ends at (long.MaxValue otherwise)
        }

        private struct Found
        {
            internal LogicalFragmentId fragment;
            internal Input input;
            internal int frame;          // the frame the input was last seen a candidate in
            internal long changes;       // _inputChanges then
            internal long revision;      // the ledger's then
            internal long scan;          // the scan that last confirmed it
        }

        private readonly Dictionary<LogicalFragmentId, LightMark> _light = new Dictionary<LogicalFragmentId, LightMark>();
        private readonly Dictionary<LogicalFragmentId, Input> _tooLarge = new Dictionary<LogicalFragmentId, Input>();
        private readonly List<LogicalFragmentId> _scanList = new List<LogicalFragmentId>();
        private readonly HashSet<LogicalFragmentId> _scanOut = new HashSet<LogicalFragmentId>();
        private readonly List<Found> _found = new List<Found>();
        private readonly List<Found> _scanCandidates = new List<Found>();   // the last scan's candidates (the probe reads them)
        private readonly Dictionary<LogicalFragmentId, int> _foundAt = new Dictionary<LogicalFragmentId, int>();
        private readonly List<LogicalFragmentId> _lightScratch = new List<LogicalFragmentId>();
        private readonly VpLogicalCutDisplay.SplitView _view = new VpLogicalCutDisplay.SplitView();
        private readonly long[] _noIssue = new long[(int)SpatialSplitNoIssue.Count];

        private long _inputChanges, _markChanges, _attemptChanges, _scanSerial, _prunedRevision = -1;
        private long _stampInput = -1, _stampMarks = -1, _stampAttempts = -1, _stampRevision = -1, _stampOwners = -1, _stampRoom = -1;
        private int _stampEpoch = -1, _scanFrom;
        private bool _scanning, _scanViewFilled, _scanViewReused, _geometrySubscribed;
        private double _scanViewSeconds;
        private Signals _scanSignals;
        private SpatialSplitScan _lastScan;
        private SpatialSplitNoIssue _issueRefusal;

        /// <summary>Tests and the comparison of the scans: false looks at every fragment in detail, as before the light marks.</summary>
        public static bool ScanUsesLightMarks = true;

        /// <summary>The last scan (default before any).</summary>
        public SpatialSplitScan LastScan => _lastScan;

        /// <summary>Scans made; of them cut short for the budget; scans the turn did not make because one of the same reading stretch stood.</summary>
        public long Scans { get; private set; }

        /// <summary>Seconds over every scan: the whole, the detailed looks, the view's preparation.</summary>
        public double ScanSeconds { get; private set; }
        public double ScanDetailSeconds { get; private set; }
        public double ScanViewSeconds { get; private set; }
        public long ScansCutShort { get; private set; }
        public long ScansShared { get; private set; }

        /// <summary>Fragments that ended at the light look, and those looked at in detail, over every scan.</summary>
        public long LightExcluded { get; private set; }
        public long DetailedLooks { get; private set; }

        /// <summary>Light marks made, and dropped for a change of the fragment's input or of the tables.</summary>
        public long LightMarksMade { get; private set; }
        public long LightMarksDropped { get; private set; }
        public int LightMarksNow => _light.Count;

        /// <summary>The display's one-pass view: taken, found current and shared, and scans that needed none.</summary>
        public long ViewFills { get; private set; }
        public long ViewShares { get; private set; }
        public long ScansWithoutView { get; private set; }

        /// <summary>Candidates looked at again before their issue (found in an earlier frame, or something moved since), and those no longer candidates then.</summary>
        public long FoundLookedAtAgain { get; private set; }
        public long FoundStaleAtUse { get; private set; }

        /// <summary>The candidates found and not issued yet (the oldest first), and those held for needing more bytes alone than the limit.</summary>
        public int FoundWaiting => _found.Count;
        public int TooLargeNow => _tooLarge.Count;

        /// <summary>The checks of the light marks against the detailed look (tests, the probe): fragments checked, and those the detailed look called a candidate (never).</summary>
        public long LightChecked { get; private set; }
        public long LightDisagreed { get; private set; }

        /// <summary>What the last turn of the several-attempts control issued, why it issued no more, and the turns by that reason.</summary>
        public int LastIssuedInTurn { get; private set; }
        public SpatialSplitNoIssue LastNoIssue { get; private set; }
        public long TurnsBy(SpatialSplitNoIssue reason) => _noIssue[(int)reason];

        /// <summary>Turns in which candidates and a free place stood and nothing was issued, by the reason.</summary>
        public long TurnsWithCandidatesAndNoIssue { get; private set; }

        /// <summary>What the last candidate asked of the bytes (its block, its classification, its sides).</summary>
        public long LastReservationBytes { get; private set; }

        // ---- the generations -------------------------------------------------------------------------------------------

        // Something Input.Equals tells apart may have moved for this fragment: its light mark is void.
        private void InputChanged(LogicalFragmentId fragment)
        {
            _inputChanges++;
            if (_light.Remove(fragment)) LightMarksDropped++;
        }

        // A table a light mark is read from changed for this fragment.
        private void MarkChanged(LogicalFragmentId fragment)
        {
            _markChanges++;
            if (_light.Remove(fragment)) LightMarksDropped++;
        }

        // The tables were emptied, or the search changed: nothing light stands.
        private void AllMarksChanged()
        {
            _markChanges++;
            LightMarksDropped += _light.Count;
            _light.Clear();
            _tooLarge.Clear();
        }

        private void OnGeometryChanged(LogicalFragmentId fragment) => InputChanged(fragment);

        private void SubscribeGeometry()
        {
            if (_geometrySubscribed || _world.Geometry == null) return;
            _geometrySubscribed = true;
            _world.Geometry.GeometryChanged += OnGeometryChanged;
        }

        private void UnsubscribeGeometry()
        {
            if (!_geometrySubscribed) return;
            _geometrySubscribed = false;
            if (_world.Geometry != null) _world.Geometry.GeometryChanged -= OnGeometryChanged;
        }

        private void SetLight(LogicalFragmentId fragment, SpatialSplitPass pass, long until)
        {
            _light[fragment] = new LightMark { pass = pass, epoch = _markEpoch, until = until };
            LightMarksMade++;
        }

        // The marks of fragments that are gone, once after each change of the ledger (a retirement is one).
        private void PruneGoneMarks()
        {
            LogicalCutLedger ledger = _world.Ledger;
            if (ledger == null || ledger.Revision == _prunedRevision) return;
            _prunedRevision = ledger.Revision;
            ForgetGoneSuppressions();
            ForgetGoneDeferrals();
            ForgetGoneLimits();
            if (_tooLarge.Count != 0 || _light.Count != 0)
            {
                _lightScratch.Clear();
                foreach (KeyValuePair<LogicalFragmentId, Input> pair in _tooLarge) if (!ledger.IsCurrentTarget(pair.Key)) _lightScratch.Add(pair.Key);
                for (int i = 0; i < _lightScratch.Count; i++) _tooLarge.Remove(_lightScratch[i]);
                _lightScratch.Clear();
                foreach (KeyValuePair<LogicalFragmentId, LightMark> pair in _light) if (!ledger.IsCurrentTarget(pair.Key)) _lightScratch.Add(pair.Key);
                for (int i = 0; i < _lightScratch.Count; i++) _light.Remove(_lightScratch[i]);
            }
        }

        // ---- the display's view, when a look first needs it ------------------------------------------------------------

        // Examine asks here where it comes to the display: null outside a scan (the walk as before, an issue, a publication ask
        // the display itself), the shared view inside one -- taken now if no current one stands.
        private VpLogicalCutDisplay.SplitView ScanView(VpLogicalCutDisplay display)
        {
            if (!_scanning || !CandidateScanUsesView) return null;
            int frame = Time.frameCount;
            if (!display.SplitViewIsCurrent(_view, frame))
            {
                long begin = Stopwatch.GetTimestamp();
                display.FillSplitView(_view, frame);
                _scanViewSeconds += Since(begin);
                _scanViewFilled = true;
                ViewFills++;
            }
            else if (!_scanViewFilled && !_scanViewReused)
            {
                _scanViewReused = true;
                ViewShares++;
            }

            return _view;
        }

        /// <summary>Tests and the comparison of the two scans: false asks the display itself, a fragment at a time, as SPLIT-CANDIDATES-1 did.</summary>
        public static bool CandidateScanUsesView = true;

        // ---- the scan --------------------------------------------------------------------------------------------------

        // Whether the last scan still tells the world as it stands: the same frame, a whole scan, and nothing it read has moved
        // (the ledger, the owners, the inputs, the marks' tables, the attempts out, the search, the display's room and view).
        private bool ScanIsCurrent()
        {
            return _lastScan.frame == Time.frameCount && _lastScan.complete && Scans != 0
                && _stampInput == _inputChanges && _stampMarks == _markChanges && _stampAttempts == _attemptChanges && _stampEpoch == _markEpoch
                && _world.Ledger != null && _stampRevision == _world.Ledger.Revision && _stampOwners == _ownerChanges && _stampRoom == _world.DisplayRoomSerial()
                && (!(_lastScan.viewFilled || _lastScan.viewReused) || (_world.Display != null && _world.Display.SplitViewIsCurrent(_view, _lastScan.frame)));
        }

        /// <summary>
        /// One look at every owner's fragment: the light look first, the detailed look for the rest, the candidates left in the
        /// list. <paramref name="walk"/>: the scan is the several-attempts control's own (its tallies, its waits' bookkeeping and
        /// its candidates' list are written); false is the probe's look beside the control as before, which changes nothing of the
        /// walk but the light marks. <paramref name="budgeted"/>: before each detailed look after the first, what is left of the
        /// Main budget is asked, and the scan stops there when it would not hold the look and an issue.
        /// </summary>
        private SpatialSplitScan Scan(in CutSpatialSplitSettings settings, bool walk, bool budgeted, double laterStepSeconds)
        {
            long begin = Stopwatch.GetTimestamp();
            var t = new SpatialSplitScan { frame = Time.frameCount };
            SubscribeGeometry();
            PruneGoneMarks();
            Signals signals = SignalsNow();
            _scanList.Clear();
            if (_world.Owners != null) _world.Owners.CopyFragmentsTo(_scanList);
            int n = _scanList.Count;
            t.owners = n;
            _scanOut.Clear();
            if (_attempt != null) _scanOut.Add(_attempt.fragment);
            for (int i = 0; i < _attempts.Count; i++) _scanOut.Add(_attempts[i].fragment);
            long serial = ++_scanSerial;
            _scanCandidates.Clear();
            bool light = ScanUsesLightMarks && _geometrySubscribed;
            bool passedBefore = _passedForDisplayState;
            _scanning = true;
            _scanViewFilled = false;
            _scanViewReused = false;
            _scanViewSeconds = 0.0;
            double detail = 0.0;
            int start = n == 0 ? 0 : _scanFrom % n;
            int at = 0;
            long revision = _world.Ledger != null ? _world.Ledger.Revision : -1;
            try
            {
                for (; at < n; at++)
                {
                    LogicalFragmentId fragment = _scanList[start + at < n ? start + at : start + at - n];
                    if (_scanOut.Count != 0 && _scanOut.Contains(fragment))
                    {
                        t.attemptOut++;
                        continue;
                    }

                    if (light && _light.TryGetValue(fragment, out LightMark mark) && mark.epoch == _markEpoch && t.frame < mark.until)
                    {
                        if (mark.pass == SpatialSplitPass.Suppressed) t.lightSuppressed++;
                        else if (mark.pass == SpatialSplitPass.LimitReached) t.lightLimitReached++;
                        else t.lightDeferred++;
                        continue;
                    }

                    if (budgeted && t.detailed > 0
                        && RemainingMainSeconds() < settings.reserveSeconds + laterStepSeconds + (2.0 * (ExpectedExamineSeconds + ExpectedIssueSeconds)))
                    {
                        break;   // the rest in a later turn, from here
                    }

                    long lookBegin = Stopwatch.GetTimestamp();
                    double viewBefore = _scanViewSeconds;
                    SpatialSplitPass pass = Examine(fragment, in settings, out Input input);
                    if (walk && _deferred.Count != 0) NoteChosen(fragment, in input, pass);
                    t.detailed++;
                    switch (pass)
                    {
                        case SpatialSplitPass.Candidate:
                            if (_tooLarge.Count != 0 && _tooLarge.TryGetValue(fragment, out Input large) && large.Equals(input))
                            {
                                t.tooLarge++;   // held with its reason: more bytes alone than the limit
                                break;
                            }

                            t.candidates++;
                            _scanCandidates.Add(new Found { fragment = fragment, input = input, frame = t.frame, changes = _inputChanges, revision = revision, scan = serial });
                            if (walk) NoteFound(fragment, in input, t.frame, revision, serial);
                            break;
                        case SpatialSplitPass.Suppressed:
                            t.suppressed++;
                            if (light) SetLight(fragment, pass, long.MaxValue);
                            break;
                        case SpatialSplitPass.LimitReached:
                            t.limitReached++;
                            if (light) SetLight(fragment, pass, long.MaxValue);
                            break;
                        case SpatialSplitPass.Deferred:
                            t.deferred++;
                            if (light && _deferred.TryGetValue(fragment, out Deferral wait)) SetLight(fragment, pass, wait.until);
                            break;
                        default:
                            if (_passedForDisplayState)
                            {
                                t.displayState++;
                                t.wanting = true;
                            }
                            else
                            {
                                t.otherPassed++;
                            }

                            break;
                    }

                    if (walk && pass != SpatialSplitPass.Candidate) _passes[(int)pass]++;
                    detail += Since(lookBegin) - (_scanViewSeconds - viewBefore);
                }
            }
            finally
            {
                _scanning = false;
                _passedForDisplayState = passedBefore;
            }

            t.complete = at >= n;
            _scanFrom = t.complete || n == 0 ? 0 : (start + at) % n;
            if (walk && t.complete) DropFoundNotConfirmed(serial);
            t.viewFilled = _scanViewFilled;
            t.viewReused = _scanViewReused;
            t.viewSeconds = _scanViewSeconds;
            t.detailSeconds = detail;
            t.totalSeconds = Since(begin);

            Scans++;
            ScanSeconds += t.totalSeconds;
            ScanDetailSeconds += t.detailSeconds;
            ScanViewSeconds += t.viewSeconds;
            if (!t.complete) ScansCutShort++;
            if (!t.viewFilled && !t.viewReused) ScansWithoutView++;
            LightExcluded += t.LightExcluded;
            DetailedLooks += t.detailed;
            if (walk)
            {
                Examined += t.detailed;
                if (t.complete) Rounds++; else WalksCutShort++;   // the walk went no further for the budget, as the walk before counted it
                if (t.detailed > 0) _examineCosts.Add(t.detailSeconds / t.detailed);
                _roundCosts.Add(t.LightSeconds);
#if ZANTETSU_OBSERVE
                // The walk's Main time, as the walk before counted it: the whole scan, and of it the part that is no detailed
                // look (the list, the attempts out, the marks -- what a round's start was).
                WalkSeconds += t.totalSeconds;
                RoundSeconds += t.LightSeconds;
                MaxWalkTurnSeconds = Math.Max(MaxWalkTurnSeconds, t.totalSeconds);
#endif
            }

            _scanSignals = signals;
            _stampInput = _inputChanges;
            _stampMarks = _markChanges;
            _stampAttempts = _attemptChanges;
            _stampEpoch = _markEpoch;
            _stampRevision = revision;
            _stampOwners = _ownerChanges;
            _stampRoom = _world.DisplayRoomSerial();
            _lastScan = t;
            return t;
        }

        private void NoteFound(LogicalFragmentId fragment, in Input input, int frame, long revision, long serial)
        {
            var found = new Found { fragment = fragment, input = input, frame = frame, changes = _inputChanges, revision = revision, scan = serial };
            if (_foundAt.TryGetValue(fragment, out int index))
            {
                _found[index] = found;   // its place in the order is kept
            }
            else
            {
                _foundAt[fragment] = _found.Count;
                _found.Add(found);
            }
        }

        // After a whole scan: what it did not confirm is no candidate any more.
        private void DropFoundNotConfirmed(long serial)
        {
            int kept = 0;
            for (int i = 0; i < _found.Count; i++)
            {
                if (_found[i].scan == serial) _found[kept++] = _found[i];
            }

            if (kept == _found.Count) return;
            _found.RemoveRange(kept, _found.Count - kept);
            RebuildFoundAt();
        }

        private void RemoveFoundAt(int index)
        {
            _found.RemoveAt(index);
            RebuildFoundAt();
        }

        private void RebuildFoundAt()
        {
            _foundAt.Clear();
            for (int i = 0; i < _found.Count; i++) _foundAt[_found[i].fragment] = i;
        }

        private void ForgetFound()
        {
            _found.Clear();
            _foundAt.Clear();
        }

        /// <summary>
        /// The light marks checked against the detailed look (tests; the probe, now and then): every fragment the light look would
        /// pass over is looked at in detail, and one the detailed look calls a candidate is counted -- and its mark dropped.
        /// Its time is its own, not the scan's.
        /// </summary>
        internal double CheckLightMarks(in CutSpatialSplitSettings settings)
        {
            if (_light.Count == 0) return 0.0;
            long begin = Stopwatch.GetTimestamp();
            int frame = Time.frameCount;
            bool passedBefore = _passedForDisplayState;
            _lightScratch.Clear();
            foreach (KeyValuePair<LogicalFragmentId, LightMark> pair in _light)
            {
                if (pair.Value.epoch == _markEpoch && frame < pair.Value.until) _lightScratch.Add(pair.Key);
            }

            _scanning = true;
            try
            {
                for (int i = 0; i < _lightScratch.Count; i++)
                {
                    LightChecked++;
                    if (Examine(_lightScratch[i], in settings, out _) != SpatialSplitPass.Candidate) continue;
                    LightDisagreed++;
                    if (LightDisagreed == 1) UnityEngine.Debug.LogError("SPATIAL SPLIT SCAN: a light mark stood for fragment " + _lightScratch[i].value + " that the detailed look calls a candidate; the mark is dropped");
                    MarkChanged(_lightScratch[i]);
                }
            }
            finally
            {
                _scanning = false;
                _passedForDisplayState = passedBefore;
            }

            return Since(begin);
        }

        // ---- the issues of a turn ---------------------------------------------------------------------------------------

        private static SpatialSplitNoIssue ReasonOf(SpatialSplitHold hold)
        {
            switch (hold)
            {
                case SpatialSplitHold.HeavyWork: return SpatialSplitNoIssue.HeavyWork;
                case SpatialSplitHold.GarbageCollection: return SpatialSplitNoIssue.GarbageCollection;
                case SpatialSplitHold.CutInFlight: return SpatialSplitNoIssue.CutInFlight;
                case SpatialSplitHold.StructureChanged: return SpatialSplitNoIssue.StructureChanged;
                case SpatialSplitHold.DisplayRoomGrown: return SpatialSplitNoIssue.DisplayRoomGrown;
                case SpatialSplitHold.BudgetShort: return SpatialSplitNoIssue.BudgetShort;
                default: return SpatialSplitNoIssue.None;
            }
        }

        /// <summary>Whether a switch of the capture or of the graph stage waits for the attempts out.</summary>
        public bool SettingsSwitchPending
        {
            get
            {
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                if (_capturePending) return true;
#endif
                return _graphReevaluatePending;
            }
        }

        // The third part of the several-attempts turn: the scan (or the one that stands), then issues from its candidates while
        // the counts, the bytes and the budget allow. The reason the turn issued nothing, or no more.
        private SpatialSplitNoIssue IssueFromScan(in CutSpatialSplitSettings settings, double later, out int issued)
        {
            issued = 0;
            if (SettingsSwitchPending)
            {
                // The switch is applied where the last attempt out ends: nothing new is issued under the settings it replaces.
                IssuesHeldBySettings++;
                return SpatialSplitNoIssue.SettingsPending;
            }

            if (_attempts.Count >= MaxAttempts)
            {
                IssuesHeldByCount++;
                return SpatialSplitNoIssue.CountLimit;
            }

            if (AttemptsWaitingMain >= MaxWaitingMain)
            {
                IssuesHeldByWaitingMain++;
                return SpatialSplitNoIssue.WaitingMainLimit;
            }

            if (_idle)
            {
                if (_idleSignals.Equals(SignalsNow()) && !_deferredDue && Time.frameCount < _deferredWake)
                {
                    return SpatialSplitNoIssue.Idle;
                }

                _idle = false;
            }

            SpatialSplitHold hold = HoldNow(in settings, ExpectedExamineSeconds + ExpectedRoundSeconds + ExpectedIssueSeconds, later, SpatialSplitBudgetStage.Choose);
            LastHold = hold;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _holdEvaluatedFrame = Time.frameCount;
#endif
            if (hold != SpatialSplitHold.None)
            {
                _holds[(int)hold]++;
                return ReasonOf(hold);
            }

            Remember();
            SpatialSplitScan scan;
            if (ScanIsCurrent())
            {
                scan = _lastScan;   // the probe's of this turn, or an earlier one of the same reading stretch
                ScansShared++;
            }
            else
            {
                scan = Scan(in settings, true, true, later);
            }

            SpatialSplitNoIssue reason = SpatialSplitNoIssue.None;
            bool wanting = scan.wanting, anyTooLarge = false, anyStale = false;
            int index = 0;
            _scanning = true;   // a candidate looked at again reads the shared view
            try
            {
                while (index < _found.Count)
                {
                    if (_attempts.Count >= MaxAttempts) { IssuesHeldByCount++; reason = SpatialSplitNoIssue.CountLimit; break; }
                    if (AttemptsWaitingMain >= MaxWaitingMain) { IssuesHeldByWaitingMain++; reason = SpatialSplitNoIssue.WaitingMainLimit; break; }
                    if (issued > 0 && RemainingMainSeconds() < settings.reserveSeconds + later + (2.0 * ExpectedIssueSeconds))
                    {
                        // The budget is used up: the candidates left keep their places for the next turn.
                        IssuesHeldByBudget++;
                        reason = SpatialSplitNoIssue.BudgetShort;
                        break;
                    }

                    Found candidate = _found[index];
                    if (candidate.frame != Time.frameCount || candidate.changes != _inputChanges || candidate.revision != _world.Ledger.Revision || candidate.input.epoch != _markEpoch)
                    {
                        // Its input generation: found in an earlier frame, or something an input reads has moved since. Looked at again.
                        FoundLookedAtAgain++;
                        if (Examine(candidate.fragment, in settings, out Input now) != SpatialSplitPass.Candidate)
                        {
                            FoundStaleAtUse++;
                            anyStale = true;
                            wanting |= _passedForDisplayState;
                            RemoveFoundAt(index);
                            continue;
                        }

                        candidate.input = now;
                    }

                    _issueRefusal = SpatialSplitNoIssue.None;
                    int before = Issued;
                    TryIssue(candidate.fragment, in candidate.input, in settings);
                    if (Issued != before)
                    {
                        if (IsOut(_attempt.fragment)) DuplicateIssues++;   // never: a fragment with an attempt out is left aside by the scan (counted to be seen)
                        _attempts.Add(_attempt);
                        _attempt = null;
                        _attemptChanges++;
                        issued++;
                        RemoveFoundAt(index);
                        continue;
                    }

                    if (_issueRefusal == SpatialSplitNoIssue.TooLargeAlone)
                    {
                        // More alone than the limit: held with that reason, and the others go on.
                        IssuesHeldTooLargeAlone++;
                        _tooLarge[candidate.fragment] = candidate.input;
                        anyTooLarge = true;
                        RemoveFoundAt(index);
                        continue;
                    }

                    if (_issueRefusal == SpatialSplitNoIssue.NotReadable)
                    {
                        wanting = true;   // not readable now: looked at again, and the others go on
                        reason = SpatialSplitNoIssue.NotReadable;
                        index++;
                        continue;
                    }

                    if (_issueRefusal == SpatialSplitNoIssue.BytesLimit) IssuesHeldByBytes++;
                    reason = _issueRefusal == SpatialSplitNoIssue.None ? SpatialSplitNoIssue.QueueFull : _issueRefusal;   // the bytes, or no place: it waits at the head
                    wanting = true;
                    break;
                }
            }
            finally
            {
                _scanning = false;
            }

            if (reason == SpatialSplitNoIssue.None && issued == 0)
            {
                reason = !scan.complete ? SpatialSplitNoIssue.ScanCutShort
                    : anyTooLarge || (scan.tooLarge > 0 && scan.candidates == 0) ? SpatialSplitNoIssue.TooLargeAlone
                    : anyStale ? SpatialSplitNoIssue.StaleAtUse
                    : SpatialSplitNoIssue.NoCandidate;
            }
            else if (reason == SpatialSplitNoIssue.None && !scan.complete)
            {
                reason = SpatialSplitNoIssue.ScanCutShort;
            }

            if (scan.complete && _found.Count == 0 && !wanting)
            {
                // A whole scan that left nothing to try: idle until something it read changes (an attempt out ends this wait
                // where its end leaves its fragment to be tried again; a publication changes the ledger).
                _idle = true;
                _idleSignals = _scanSignals;
                IdleWaits++;
            }

            return reason;
        }

        public long IssuesHeldBySettings { get; private set; }
        public long IssuesHeldByBudget { get; private set; }
        public long IssuesHeldTooLargeAlone { get; private set; }

        /// <summary>The scan's and the issues' tallies (the world's end).</summary>
        public string DescribeScan()
        {
            var text = new System.Text.StringBuilder(512);
            System.Globalization.CultureInfo inv = System.Globalization.CultureInfo.InvariantCulture;
            text.Append("scan: ms ").Append((ScanSeconds * 1000.0).ToString("F3", inv)).Append(" (detailed looks ").Append((ScanDetailSeconds * 1000.0).ToString("F3", inv))
                .Append(", the view ").Append((ScanViewSeconds * 1000.0).ToString("F3", inv)).Append("), scans ").Append(Scans).Append(" (cut short ").Append(ScansCutShort).Append(", shared ").Append(ScansShared).Append(", without the view ").Append(ScansWithoutView)
                .Append("); light excluded ").Append(LightExcluded).Append(", detailed ").Append(DetailedLooks).Append("; light marks made ").Append(LightMarksMade).Append(", dropped ")
                .Append(LightMarksDropped).Append(", now ").Append(_light.Count).Append("; view taken ").Append(ViewFills).Append(", shared ").Append(ViewShares)
                .Append("; candidates looked at again ").Append(FoundLookedAtAgain).Append(", stale then ").Append(FoundStaleAtUse).Append("; waiting now ").Append(_found.Count)
                .Append(", too large alone now ").Append(_tooLarge.Count).Append("; light checks ").Append(LightChecked).Append(", disagreed ").Append(LightDisagreed)
                .Append("; turns with candidates, a place and no issue ").Append(TurnsWithCandidatesAndNoIssue).Append("; turns by what stopped the issues:");
            for (int i = 0; i < _noIssue.Length; i++)
            {
                if (_noIssue[i] != 0) text.Append(' ').Append(((SpatialSplitNoIssue)i).ToString()).Append(' ').Append(_noIssue[i]);
            }

            return text.ToString();
        }
    }
}
