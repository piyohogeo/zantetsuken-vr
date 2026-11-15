using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    // SPLIT-CANDIDATES-2 (2026-11-13): several attempts out at once -- the default control since that day (MaxAttempts 8),
    // beside the control as before (one attempt at a time), which MaxAttempts 1 still is.
    //
    // The attempts are independent: each has its own input, block, lease, shape hold, ticket, result and stage, as the one
    // attempt always had. What is new is the list that holds them and the turn that gives each of them its chance:
    //   1. offers and light ends, for every attempt, whatever the frame holds: a bake waiting for a place is offered; an attempt
    //      that came back and is not to be published (not found, stale, not run, failed) is ended there and then -- its
    //      resources go back, and it never waits behind another attempt's publication. (Not an attempt that cuts convexes:
    //      as under the control as before, even the look at it waits for the gate, in the second part);
    //   2. the heavy Main stages (a publication, the cut meshes, the hull checks, a cut's publication), one attempt after
    //      another in a rotating order, each under the turn's gate as it stands at that moment: the frame's holds (heavy work,
    //      a collection, a cut in flight, a structure change, the display's room) and the Main budget read from the clock, so
    //      that what the turn already spent counts. The first one held ends the stage for the frame. An attempt's own input is
    //      looked at again right before its publication; a publication of this turn is not a structure change to hold the
    //      next one for (each is checked against its own fragment);
    //   3. issues, from the scan's candidates (CutSpatialSplit.Scan.cs: the light look first, the candidates used in the frame
    //      they are found), while the counts allow: fewer attempts out than MaxAttempts, fewer waiting for Main than
    //      MaxWaitingMain, what is held with what the next would hold no more than MaxHeldBytes -- and the same gate as before
    //      (the frame's holds, the Main budget, asked again before every further issue). No issue while a switch of the capture
    //      or of the graph stage waits for the attempts out. A fragment with an attempt out is left aside by the scan; the
    //      children of a publication are met by the next scan.
    // The existing code of each stage runs as it is, with the attempt in hand set to the one whose turn it is.
    public sealed partial class CutSpatialSplit
    {
        /// <summary>The Background workers the product's pool has (the pool's own; not changed here).</summary>
        public static int BackgroundWorkers => WorkerPoolExecutor.DefaultBackgroundWorkerCount;

        /// <summary>
        /// The most attempts out at once in a world made from here on -- waiting for a place, on a worker, done and not collected,
        /// collected and waiting for Main, all counted. 8 by default since 2026-11-13 (the PC and the Quest alike); 1 is the
        /// control as before. Set by the launch (<see cref="CutSpatialSplitLaunch"/>) before any world's pools are made.
        /// </summary>
        public static int DefaultMaxAttempts = CutSpatialSplitLaunch.DefaultAttempts;

        /// <summary>
        /// The first values of the several-attempts control (SPLIT-CANDIDATES-1.md, the addition of 2026-11-13, says where they are from):
        /// 8 attempts out (two workers at a mean 1.6 ms an attempt finish about that many within a frame; their issues cost Main about
        /// 0.2 ms), 4 waiting for Main (a publication with its collection is about 1.1 ms of some 9 ms left), 4 MiB held (an attempt's
        /// blocks: median 29 KiB, most 157 KiB over the 3640 saved inputs).
        /// </summary>
        public const int EvaluationMaxAttempts = 8, EvaluationMaxWaitingMain = 4;
        public const long EvaluationMaxHeldBytes = 4L << 20;

        /// <summary>This world's limits (see <see cref="DefaultMaxAttempts"/>).</summary>
        public int MaxAttempts { get => _maxAttempts ?? DefaultMaxAttempts; set => _maxAttempts = value; }
        private int? _maxAttempts;
        public int MaxWaitingMain { get; set; } = EvaluationMaxWaitingMain;

        /// <summary>
        /// The most the attempts out may hold of the main thread's blocks (<see cref="AttemptsHeldBytes"/> says what is counted).
        /// An issue is made only if what is held, with what the new attempt will hold, stays within it. A change of it lets what
        /// was held for needing more alone than the limit be looked at again.
        /// </summary>
        public long MaxHeldBytes
        {
            get => _maxHeldBytes;
            set
            {
                if (value == _maxHeldBytes) return;
                _maxHeldBytes = value;
                _tooLarge.Clear();
                _idle = false;
            }
        }

        private long _maxHeldBytes = EvaluationMaxHeldBytes;

        /// <summary>
        /// The accept capacity of the Background pool a world makes: the profile's (2), or -- with the several-attempts control --
        /// the most attempts out, so that an attempt issued waits in the pool for a worker and not for a later frame's dispatch.
        /// The dispatcher's waiting capacity and its reserve for urgent work are the profile's, unchanged.
        /// </summary>
        public static int BackgroundPoolCapacity(int profileCapacity) => Math.Max(profileCapacity, DefaultMaxAttempts > 1 ? DefaultMaxAttempts : 0);

        private readonly List<Attempt> _attempts = new List<Attempt>();
        private readonly List<Attempt> _attemptTurn = new List<Attempt>();
        private long _rotation;

        /// <summary>Attempts out now; the most at once; fragments passed over for having one out; ends made in the light stage; publications in one turn at most.</summary>
        public int AttemptsOut => _attempts.Count + (_attempt != null && !_attempts.Contains(_attempt) ? 1 : 0);
        public int MostAttemptsOut { get; private set; }
        public long AttemptOutPasses { get; private set; }
        public long LightEnds { get; private set; }
        public int MostPublishedInATurn { get; private set; }
        public int MostIssuedInATurn { get; private set; }
        public long IssuesHeldByCount { get; private set; }
        public long IssuesHeldByWaitingMain { get; private set; }
        public long IssuesHeldByBytes { get; private set; }
        public long MostHeldBytes { get; private set; }
        public long DuplicateIssues { get; private set; }

        // The attempt in hand, or (several out) the one issued first: what the single attempt's readouts tell.
        private Attempt FirstOut => _attempt ?? (_attempts.Count != 0 ? _attempts[0] : null);

        /// <summary>Whether an attempt of this fragment is out (the one in hand, or one of the several).</summary>
        internal bool HasAttemptFor(LogicalFragmentId fragment) => (_attempt != null && _attempt.fragment.Equals(fragment)) || IsOut(fragment);

        private bool IsOut(LogicalFragmentId fragment)
        {
            for (int i = 0; i < _attempts.Count; i++)
            {
                if (_attempts[i].fragment.Equals(fragment)) return true;
            }

            return false;
        }

        /// <summary>The attempts whose work is done or collected and that wait for a turn of the main thread.</summary>
        public int AttemptsWaitingMain
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _attempts.Count; i++) if (_attempts[i].collected || _attempts[i].IsComplete) n++;
                return n;
            }
        }

        /// <summary>
        /// What the attempts out hold of the blocks taken on the main thread for them, bytes. **Counted**: each attempt's work
        /// block (the output indices, the submesh tables and boxes, the convex table, the search's scratch), its classification's
        /// block, and its sides (the graph stage's, or the bones'). **Not counted**: the geometry and the shape themselves (read
        /// in place under a lease and a hold, not copied), the meshes a convex-cutting attempt makes for its bake (Unity objects),
        /// the bones' own small tables, and a development build's capture copies.
        /// </summary>
        public long AttemptsHeldBytes
        {
            get
            {
                long bytes = 0;
                for (int i = 0; i < _attempts.Count; i++) bytes += _attempts[i].HeldBytes;
                return bytes;
            }
        }

        // The world switched off or ending with attempts out: those that came back are ended, and so is a bake that found no
        // place in the queue and waits to be offered again (nothing of it is on a worker). What is on a worker keeps everything
        // it reads -- its blocks, its lease, its shape's hold, its meshes -- until it is collected, and is ended here in the turn
        // after that; the candidates found and not issued are forgotten.
        private void EndCollectedWhenOff()
        {
            ForgetFound();
            _attemptTurn.Clear();
            _attemptTurn.AddRange(_attempts);
            for (int i = 0; i < _attemptTurn.Count; i++)
            {
                Attempt a = _attemptTurn[i];
                bool neverOffered = !a.collected && a.stage == AttemptStage.Baking && !a.offered;
                if (!a.collected && !neverOffered) continue;
                _attempt = a;
                End(SpatialSplitEnd.NotRun, false, neverOffered ? "switched off or the world ending: its bake was never offered" : "switched off or the world ending");
                _attempt = null;
            }
        }

        private void StepSeveral(in CutSpatialSplitSettings settings)
        {
            if (_attempts.Count == 0 && _idle && _idleSignals.Equals(SignalsNow()) && !_deferredDue && Time.frameCount < _deferredWake)
            {
                IdleTurns++;   // as before: an idle turn is not a turn taken, and asks nothing of the frame
                LastNoIssue = SpatialSplitNoIssue.Idle;
                LastIssuedInTurn = 0;
                _noIssue[(int)SpatialSplitNoIssue.Idle]++;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                _issueEvaluatedFrame = Time.frameCount;
#endif
                return;
            }

            Turns++;
            double later = LaterStep();
            int issuedBefore = Issued, publishedBefore = Published;
            if (_attempts.Count > MostAttemptsOut) MostAttemptsOut = _attempts.Count;
            long held = AttemptsHeldBytes;
            if (held > MostHeldBytes) MostHeldBytes = held;

            // 1. Offers and light ends: every attempt, whatever the frame holds.
            _attemptTurn.Clear();
            _attemptTurn.AddRange(_attempts);
            int count = _attemptTurn.Count;
            int start = count == 0 ? 0 : (int)(_rotation++ % count);
            for (int k = 0; k < count; k++)
            {
                Attempt a = _attemptTurn[(start + k) % count];
                if (!a.collected)
                {
                    if (a.stage == AttemptStage.Baking && !a.offered) OfferBake(a);
                    continue;
                }

                // An attempt that cuts convexes holds meshes, and looking at it again scans the display's registrations: as under
                // the control as before, nothing of it -- not that look, not giving it back -- is done before the whole gate has
                // let this frame through. It is looked at in the second part, under the gate.
                if (a.cut) continue;
                _attempt = a;
                if (!ShouldPublish(a, out SpatialSplitEnd over, out bool marked))
                {
                    End(over, marked);
                    LightEnds++;
                }

                _attempt = null;
            }

            // 2. The heavy Main stages, one attempt after another, under the gate as it stands at each.
            _attemptTurn.Clear();
            _attemptTurn.AddRange(_attempts);
            count = _attemptTurn.Count;
            for (int k = 0; k < count; k++)
            {
                Attempt a = _attemptTurn[count == 0 ? 0 : (start + k) % count];
                if (!a.collected) continue;
                _attempt = a;
                SpatialSplitBudgetStage stage = !a.cut ? SpatialSplitBudgetStage.Publish
                    : !a.applied ? SpatialSplitBudgetStage.CutApply
                    : a.hullsChecked < a.meshCount ? SpatialSplitBudgetStage.HullCheck
                    : SpatialSplitBudgetStage.CutPublish;
                double cost = !a.cut ? ExpectedPublishSeconds
                    : !a.applied ? ExpectedCutApplySeconds
                    : a.hullsChecked < a.meshCount ? ExpectedHullCheckSeconds
                    : ExpectedCutPublishSeconds;
                SpatialSplitHold hold = HoldNow(in settings, cost, later, stage);
                LastHold = hold;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
                _holdEvaluatedFrame = Time.frameCount;
#endif
                if (hold == SpatialSplitHold.BudgetShort && NoteStageHold(a, stage)) hold = SpatialSplitHold.None;
                if (hold != SpatialSplitHold.None)
                {
                    _holds[(int)hold]++;
                    if (a.cut) CutTurnsLeftAlone++;
                    _attempt = null;
                    break;
                }

                Remember();
                if (!ShouldPublish(a, out SpatialSplitEnd over, out bool marked))
                {
                    End(over, marked);   // its own input, looked at again right before
                }
                else if (a.cut && !a.applied)
                {
                    ApplyCutMeshes();
                }
                else if (a.cut && a.hullsChecked < a.meshCount)
                {
                    CheckHulls(in settings, later);
                }
                else
                {
                    Publish(in settings);
                    Remember();   // this turn's own publication is not a structure change to hold the next attempt for
                }

                _attempt = null;
            }

            // 3. Issues, from the scan's candidates, while the counts, the bytes and the gate allow.
            SpatialSplitNoIssue reason = IssueFromScan(in settings, later, out int issuedNow);
            LastNoIssue = reason;
            LastIssuedInTurn = issuedNow;
#if UNITY_EDITOR || DEVELOPMENT_BUILD
            _issueEvaluatedFrame = Time.frameCount;
#endif
            _noIssue[(int)reason]++;
            if (issuedNow == 0 && _found.Count != 0 && _attempts.Count < MaxAttempts) TurnsWithCandidatesAndNoIssue++;

            Remember();
            MostIssuedInATurn = Math.Max(MostIssuedInATurn, Issued - issuedBefore);
            MostPublishedInATurn = Math.Max(MostPublishedInATurn, Published - publishedBefore);
            if (_attempts.Count > MostAttemptsOut) MostAttemptsOut = _attempts.Count;
        }

        /// <summary>The several-attempts control's own tallies (the world's end).</summary>
        public string DescribeSeveral()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            return "several attempts (" + (MaxAttempts > 1 ? "on" : "off: one attempt at a time, the control as before") + "): Background workers " + BackgroundWorkers
                + ", pool accept capacity " + (_world.Dispatcher?.Executor(WorkDestination.BackgroundPool)?.Capacity ?? 0).ToString(inv)
                + ", dispatcher waiting capacity " + (_world.Dispatcher != null ? _world.Dispatcher.WaitingCapacity : 0).ToString(inv)
                + " (reserved for urgent work " + (_world.Dispatcher != null ? _world.Dispatcher.ReservedForUrgent : 0).ToString(inv) + "), most attempts out " + MaxAttempts
                + ", most waiting for Main " + MaxWaitingMain + ", most held bytes " + MaxHeldBytes
                + "; seen: most out at once " + MostAttemptsOut + ", most issued in a turn " + MostIssuedInATurn + ", most published in a turn " + MostPublishedInATurn
                + ", most bytes held " + MostHeldBytes + "; ends in the light stage " + LightEnds + "; fragments passed over for an attempt out " + AttemptOutPasses
                + "; issues held: by the count " + IssuesHeldByCount + ", by those waiting for Main " + IssuesHeldByWaitingMain + ", by the bytes " + IssuesHeldByBytes
                + ", too large alone " + IssuesHeldTooLargeAlone + ", by the budget after an issue " + IssuesHeldByBudget + ", by a settings switch waiting " + IssuesHeldBySettings
                + "; " + DescribeScan();
        }

        internal sealed unsafe partial class Attempt
        {
            /// <summary>What this attempt holds of the main thread's blocks now.</summary>
            internal long HeldBytes => (block.IsCreated ? block.Length : 0) + (classificationBlock.IsCreated ? classificationBlock.Length : 0) + (graphSides.IsCreated ? graphSides.Length : 0)
                + (pairMarks.IsCreated ? pairMarks.Length : 0) + pairKeptBytes;
        }
    }
}
