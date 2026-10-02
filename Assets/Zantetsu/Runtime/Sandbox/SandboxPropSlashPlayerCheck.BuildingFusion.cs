using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // The building E2E with the fusion on (2026-09-30): the cost of the run split by phase -- right after a cut, while
    // a building aggregates, and after -- frame by frame (building-fusion.csv) and summarised: the real Rigidbody and
    // hull counts, the contact collection, the simulation, the display snapshot, the whole Main frame and the held
    // hits' wait; and whether the aggregation completed by both the deadline and the next Slash, the held hits were
    // processed, the groups did not keep growing, and the physics kept stepping through the cuts and the re-fusions.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private const int CutPhaseFrames = 30;   // a frame within this many of a group cut's publication is "cut"

            private sealed class PhaseStat
            {
                public int frames, stepped;
                public readonly List<double> simulate = new List<double>(), contact = new List<double>(), main = new List<double>(), mainWork = new List<double>(), snapshot = new List<double>(), fusion = new List<double>(), fusionOverrun = new List<double>();
                public int minBodies = int.MaxValue, maxBodies, minHulls = int.MaxValue, maxHulls, maxSkips, deferred;
            }

            private readonly FrameTiming[] _bfTimings = new FrameTiming[1];
            private double _bfLastClassify, _bfLastPublish, _bfLastFinal, _bfLastUnion, _bfLastMass, _bfLastFuse, _bfLastRequest, _bfLastWorker, _bfLastOverrun, _bfLastHull, _bfLastCookHull;
            private int _bfLastHullChecks;
            private double _bfLastMove, _bfLastShadow, _bfLastIgnore, _bfLastSample, _bfLastApply, _bfLastShadowPrep;
            private long _bfLastPairs;
            private int _bfLastDeferred;

            private StreamWriter _bfRows;
            private int _bfLastFrame = -1, _bfLastCuts, _bfLastCutFrame = int.MinValue, _bfMaxGroups, _bfSkips, _bfMaxSkips, _bfStepped, _bfSkipped;
            private long _bfLastStepId = -1;
            private double _bfLastContact, _bfLastSupport, _bfLastFusion, _bfLastReal;
            private readonly Dictionary<string, PhaseStat> _bfPhases = new Dictionary<string, PhaseStat>();
            private readonly List<string> _bfPhaseChanges = new List<string>();
            private string _bfPhase = "idle";
            private BuildingFusion _bfFusion;

            private void BuildingFusionFrame(int frame)
            {
                if (_world == null || _world.Fusion == null || frame == _bfLastFrame) return;
                _bfLastFrame = frame;
                BuildingFusion f = _world.Fusion;
                _bfFusion = f;
                BuildingRest rest = _world.Rest;
                if (_bfRows == null)
                {
                    _bfRows = new StreamWriter(Path.Combine(directory, "building-fusion.csv"));
                    _bfRows.WriteLine("frame,real,t,phase,groups,resting,free,busy,aggregating,held,fusedPieces,groupCuts,finals,rigidbodies,hulls,contactMs,supportMs,simulateMs,stepped,skipsInARow,remainingMs,expectedMs,verifications,droppedOwedMs,snapshotMs,frameIntervalMs,mainThreadMs,mainWorkMs,fusionMs,stepMs,requestMs,classifyMs,publishMs,finalMs,unionMs,massMs,fuseMs,workerMs,overrunMs,deferred,preparations,heldWaitMs,unionMembers,aggregations,heldProcessed,hullMs,cookHullMs,hullChecks,pubMoveMs,pubShadowMs,pubIgnoreMs,ignorePairs,massSampleMs,massApplyMs,shadowPrepMs");
                    _bfLastReal = Time.realtimeSinceStartupAsDouble;
                    _bfLastContact = rest != null ? rest.ContactSeconds : 0.0;
                    _bfLastSupport = rest != null ? rest.SupportSeconds : 0.0;
                    _bfLastFusion = FusionMainSeconds(f);
                }

                int resting = 0, free = 0, busy = 0;
                foreach (FusedGroup g in f.Groups) { if (g.Busy) busy++; if (g.Kinematic) resting++; else free++; }
                if (f.GroupCuts > _bfLastCuts) { _bfLastCuts = f.GroupCuts; _bfLastCutFrame = frame; }
                _bfMaxGroups = Math.Max(_bfMaxGroups, f.GroupCount);
                string phase = f.BuildingsAggregating > 0 ? "aggregating" : busy > 0 || frame - _bfLastCutFrame <= CutPhaseFrames ? "cut" : f.GroupCuts > 0 ? "after" : "idle";
                if (phase != _bfPhase) { _bfPhaseChanges.Add(frame + ":" + phase); _bfPhase = phase; }

                double real = Time.realtimeSinceStartupAsDouble;
                double mainMs = (real - _bfLastReal) * 1000.0;
                _bfLastReal = real;
                double contactMs = rest != null ? (rest.ContactSeconds - _bfLastContact) * 1000.0 : 0.0;
                double supportMs = rest != null ? (rest.SupportSeconds - _bfLastSupport) * 1000.0 : 0.0;
                if (rest != null) { _bfLastContact = rest.ContactSeconds; _bfLastSupport = rest.SupportSeconds; }
                double fusionNow = FusionMainSeconds(f);
                double fusionMs = (fusionNow - _bfLastFusion) * 1000.0;
                _bfLastFusion = fusionNow;
                double stepMs = fusionMs - (f.RequestSeconds - _bfLastRequest) * 1000.0, requestMs = (f.RequestSeconds - _bfLastRequest) * 1000.0;
                double classifyMs = (f.ClassifySeconds - _bfLastClassify) * 1000.0, publishMs = (f.PublishSeconds - _bfLastPublish) * 1000.0, finalMs = (f.FinalSeconds - _bfLastFinal) * 1000.0;
                double unionMs = (f.UnionSeconds - _bfLastUnion) * 1000.0, massMs = (f.MassSeconds - _bfLastMass) * 1000.0, fuseMs = (f.FuseSeconds - _bfLastFuse) * 1000.0, workerMs = (f.WorkerSeconds - _bfLastWorker) * 1000.0;
                double overrunMs = (f.OverrunSeconds - _bfLastOverrun) * 1000.0;
                // The hull checks: the fusion's own (in its Step, under the common budget) and the cook's in all (the pump's included).
                double hullMs = (f.HullSeconds - _bfLastHull) * 1000.0;
                double cookHullMs = _world.Cook != null ? (_world.Cook.HullCheckSeconds - _bfLastCookHull) * 1000.0 : 0.0;
                int hullChecks = _world.Cook != null ? _world.Cook.HullChecks - _bfLastHullChecks : 0;
                _bfLastHull = f.HullSeconds;
                if (_world.Cook != null) { _bfLastCookHull = _world.Cook.HullCheckSeconds; _bfLastHullChecks = _world.Cook.HullChecks; }
                int deferred = f.DeferredForBudget - _bfLastDeferred;
                // The publication's and the masses' parts (2026-09-30): the members' moves, the copies at the switch, the exclusions (and their pairs), the samples, the applies, and the copies made ahead.
                double pubMoveMs = (f.PublishMoveSeconds - _bfLastMove) * 1000.0, pubShadowMs = (f.PublishShadowSeconds - _bfLastShadow) * 1000.0, pubIgnoreMs = (f.PublishIgnoreSeconds - _bfLastIgnore) * 1000.0;
                long ignorePairs = f.IgnoredPairs - _bfLastPairs;
                double massSampleMs = (f.MassSampleSeconds - _bfLastSample) * 1000.0, massApplyMs = (f.MassApplySeconds - _bfLastApply) * 1000.0, shadowPrepMs = (f.ShadowPrepareSeconds - _bfLastShadowPrep) * 1000.0;
                _bfLastMove = f.PublishMoveSeconds; _bfLastShadow = f.PublishShadowSeconds; _bfLastIgnore = f.PublishIgnoreSeconds; _bfLastPairs = f.IgnoredPairs;
                _bfLastSample = f.MassSampleSeconds; _bfLastApply = f.MassApplySeconds; _bfLastShadowPrep = f.ShadowPrepareSeconds;
                _bfLastRequest = f.RequestSeconds; _bfLastClassify = f.ClassifySeconds; _bfLastPublish = f.PublishSeconds; _bfLastFinal = f.FinalSeconds; _bfLastUnion = f.UnionSeconds; _bfLastMass = f.MassSeconds; _bfLastFuse = f.FuseSeconds; _bfLastWorker = f.WorkerSeconds; _bfLastOverrun = f.OverrunSeconds; _bfLastDeferred = f.DeferredForBudget;
                // The Main thread's own time this frame (FrameTimingManager; a few frames late, medians unaffected): the frame and the frame less the present wait.
                double mainThreadMs = double.NaN, mainWorkMs = double.NaN;
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, _bfTimings) > 0)
                {
                    mainThreadMs = _bfTimings[0].cpuMainThreadFrameTime;
                    mainWorkMs = _bfTimings[0].cpuMainThreadFrameTime - _bfTimings[0].cpuMainThreadPresentWaitTime;
                }
                double simulateMs = CutPhysicsStep.LastSimulateSeconds * 1000.0;
                long stepId = CutPhysicsStep.Clock.StepId;
                bool stepped = _bfLastStepId >= 0 && stepId != _bfLastStepId;
                if (_bfLastStepId >= 0) { if (stepped) { _bfStepped++; _bfSkips = 0; } else { _bfSkipped++; _bfSkips++; _bfMaxSkips = Math.Max(_bfMaxSkips, _bfSkips); } }
                _bfLastStepId = stepId;
                int bodies = UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length;
                int hulls = 0;
                foreach (MeshCollider c in UnityEngine.Object.FindObjectsByType<MeshCollider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) if (c.enabled) hulls++;
                double heldWaitMs = 0.0;
                if (f.BuildingsAggregating > 0)
                {
                    foreach (FusedGroup g in f.Groups) if (f.IsAggregating(g.Key, out double since, out _)) heldWaitMs = Math.Max(heldWaitMs, (real - since) * 1000.0);
                }

                if (!_bfPhases.TryGetValue(phase, out PhaseStat stat)) _bfPhases[phase] = stat = new PhaseStat();
                stat.frames++;
                if (stepped) { stat.stepped++; stat.simulate.Add(simulateMs); }
                stat.contact.Add(contactMs);
                stat.main.Add(mainMs);
                if (!double.IsNaN(mainWorkMs)) stat.mainWork.Add(mainWorkMs);
                stat.snapshot.Add(CutPhysicsStep.LastCollectSeconds * 1000.0);
                stat.fusion.Add(fusionMs);
                stat.fusionOverrun.Add(overrunMs);
                stat.deferred += deferred;
                stat.minBodies = Math.Min(stat.minBodies, bodies); stat.maxBodies = Math.Max(stat.maxBodies, bodies);
                stat.minHulls = Math.Min(stat.minHulls, hulls); stat.maxHulls = Math.Max(stat.maxHulls, hulls);
                stat.maxSkips = Math.Max(stat.maxSkips, _bfSkips);
                _bfRows.WriteLine(string.Join(",", frame, real.ToString("F3", Inv), CutPhysicsStep.Clock.PhysicsSeconds.ToString("F3", Inv), phase, f.GroupCount, resting, free, busy, f.BuildingsAggregating,
                    f.HeldRequests - f.HeldProcessed, f.FusedPieces, f.GroupCuts, f.FinalsPublished, bodies, hulls, contactMs.ToString("F3", Inv), supportMs.ToString("F3", Inv), simulateMs.ToString("F3", Inv),
                    stepped ? 1 : 0, _bfSkips, (CutPhysicsStep.LastDecisionRemainingSeconds * 1000).ToString("F3", Inv), (CutPhysicsStep.LastDecisionExpectedSeconds * 1000).ToString("F3", Inv),
                    CutPhysicsStep.VerificationSteps, (CutPhysicsStep.Clock.DroppedSeconds * 1000).ToString("F1", Inv), (CutPhysicsStep.LastCollectSeconds * 1000).ToString("F3", Inv), mainMs.ToString("F3", Inv),
                    mainThreadMs.ToString("F3", Inv), mainWorkMs.ToString("F3", Inv),
                    fusionMs.ToString("F3", Inv), stepMs.ToString("F3", Inv), requestMs.ToString("F3", Inv), classifyMs.ToString("F3", Inv), publishMs.ToString("F3", Inv), finalMs.ToString("F3", Inv), unionMs.ToString("F3", Inv), massMs.ToString("F3", Inv), fuseMs.ToString("F3", Inv), workerMs.ToString("F3", Inv),
                    overrunMs.ToString("F3", Inv), deferred, f.PreparationsInFlight, heldWaitMs.ToString("F1", Inv), f.UnionMembersMoved, f.AggregationsCompleted, f.HeldProcessed,
                    hullMs.ToString("F3", Inv), cookHullMs.ToString("F3", Inv), hullChecks,
                    pubMoveMs.ToString("F3", Inv), pubShadowMs.ToString("F3", Inv), pubIgnoreMs.ToString("F3", Inv), ignorePairs, massSampleMs.ToString("F3", Inv), massApplyMs.ToString("F3", Inv), shadowPrepMs.ToString("F3", Inv)));
            }

            // The fusion's Main time: every Step (the Finals, the preparations, the aggregations, the merges, the fusions, the masses) and every request outside one; disjoint.
            private static double FusionMainSeconds(BuildingFusion f) => f.StepSeconds + f.RequestSeconds;

            private int RealBodiesUnder(FusedGroup g)
            {
                var bodies = new HashSet<Rigidbody>();
                foreach (LogicalFragmentId fragment in g.Fragments)
                {
                    if (!_world.Owners.TryGet(fragment, out PhysicsFragmentOwner o) || o.Root == null) continue;
                    foreach (MeshCollider c in o.Root.GetComponentsInChildren<MeshCollider>()) if (c.enabled && c.attachedRigidbody != null) bodies.Add(c.attachedRigidbody);
                }

                return bodies.Count;
            }

            private static double Median(List<double> values)
            {
                if (values.Count == 0) return 0.0;
                var sorted = new List<double>(values); sorted.Sort();
                int m = sorted.Count / 2;
                return (sorted.Count & 1) == 1 ? sorted[m] : 0.5 * (sorted[m - 1] + sorted[m]);
            }

            private static string Ms(List<double> values) => Median(values).ToString("F3", Inv) + "/" + (values.Count > 0 ? values.Max() : 0.0).ToString("F3", Inv);

            private void BuildingFusionClose()
            {
                _bfRows?.Dispose();
                _bfRows = null;
            }

            /// <summary>A member operation PhysX refused: attributed to it by the cook, aborted in the ledger, its source no longer live and without an owner.</summary>
            private bool HullRefusalRecovered(BuildingFusion.MemberOperation op, out string how)
            {
                bool attributed = false;
                if (_world.Cook != null) foreach (PhysicsCutHullRejection r in _world.Cook.HullRejections) attributed |= r.IsAttributed && r.operation.Equals(op.operation);
                bool aborted = _world.Ledger.TryGetOperation(op.operation, out LogicalCutOperation record) && record.state == LogicalCutOperationState.Aborted;
                bool sourceGone = !_world.Ledger.IsCurrentTarget(op.source) && !_world.Owners.TryGet(op.source, out _);
                how = "attributed " + attributed + ", aborted " + aborted + ", source retired " + sourceGone;
                return attributed && aborted && sourceGone;
            }

            /// <summary>
            /// The fused building's successes (TL, 2026-09-30): from each hit on a fused member through its preparation's
            /// group cut to the crossed members' operations, each at the ledger's end (Completed with the fusion's children)
            /// with its live children's geometry committed. A hit is a success when its preparation published and every one
            /// of its operations is so, save those PhysX refused and were recovered (counted apart); a published cut that
            /// crossed no member made no logical cut and is counted apart. A hit still pending, or abandoned by the world's
            /// ending, is never a success.
            /// </summary>
            private void BuildingFusionChains(int acceptedOnFused)
            {
                BuildingFusion f = _world.Fusion;
                var byPreparation = new Dictionary<int, List<BuildingFusion.MemberOperation>>();
                foreach (BuildingFusion.MemberOperation op in f.MemberOperations)
                {
                    if (!byPreparation.TryGetValue(op.preparation, out List<BuildingFusion.MemberOperation> list)) byPreparation[op.preparation] = list = new List<BuildingFusion.MemberOperation>();
                    list.Add(op);
                }

                int published = 0, committed = 0, withRefusals = 0, noCrossed = 0, broken = 0, pending = 0, abandoned = 0, included = 0, refused = 0;
                bool rootCommitted = false;
                var countedPreparations = new HashSet<int>();
                foreach (BuildingFusion.HitRecord h in f.Hits)
                {
                    if (h.IsPending) { pending++; continue; }
                    if (h.outcome.StartsWith("Abandoned")) { abandoned++; continue; }
                    if (h.outcome.StartsWith("Included")) { included++; continue; }
                    if (!h.published || h.preparation <= 0) { refused++; continue; }
                    published++;
                    if (!byPreparation.TryGetValue(h.preparation, out List<BuildingFusion.MemberOperation> ops) || ops.Count == 0) { noCrossed++; continue; }
                    int good = 0, refusedOk = 0, bad = 0;
                    foreach (BuildingFusion.MemberOperation op in ops)
                    {
                        if (!op.Published) { if (op.hullRefused && HullRefusalRecovered(op, out _)) refusedOk++; else bad++; continue; }
                        bool completed = _world.Ledger.TryGetOperation(op.operation, out LogicalCutOperation record) && record.state == LogicalCutOperationState.Completed
                            && record.positive.Equals(op.positive) && record.negative.Equals(op.negative);
                        bool framed = _world.Geometry == null
                            || ((!_world.Ledger.IsCurrentTarget(op.positive) || _world.Geometry.TryGetGeometryFrame(op.positive, out _))
                                && (!_world.Ledger.IsCurrentTarget(op.negative) || _world.Geometry.TryGetGeometryFrame(op.negative, out _)));
                        if (completed && framed) good++; else bad++;
                    }

                    if (bad == 0 && good > 0)
                    {
                        committed++;
                        if (refusedOk > 0) withRefusals++;
                        foreach (PlayableCityCuttable c in _pcCuttables) if (c.building && c.IsRegistered && c.Registration.Fragment.value == h.fragment) rootCommitted = true;
                    }
                    else if (bad > 0)
                    {
                        broken++;
                        if (countedPreparations.Add(h.preparation)) Log("playable city building chain broken: hit " + h.id + " slash " + h.slashId + " preparation " + h.preparation + ": operations " + ops.Count + " (at the end with geometry " + good + ", refused and recovered " + refusedOk + ", not at the end " + bad + ")");
                    }
                }

                Log("playable city building (fused): hits accepted on fused members " + acceptedOnFused + ", the fusion's hit records " + f.Hits.Count + ": published " + published + " (whose operations all reached the ledger's end with their geometry committed "
                    + committed + ", of them with a PhysX refusal recovered " + withRefusals + "; published with no member crossed " + noCrossed + "; with an operation not at its end " + broken + "), included in a cut in progress " + included
                    + ", refused or dropped " + refused + ", abandoned at the ending " + abandoned + " and pending " + pending + " (neither counted as a success); the building's own first hit committed " + rootCommitted);
                Expect(committed > 0 && broken == 0, "[scenario] a building was cut from a Slash: a fused member's hit published a group cut whose member operations reached the ledger's end with their geometry committed ("
                    + committed + " such hits, " + broken + " with an operation not at its end; " + pending + " pending and " + abandoned + " abandoned are not successes)");
            }

            private void BuildingFusionSummarise()
            {
                BuildingFusion f = _bfFusion ?? (_world != null ? _world.Fusion : null);
                if (f == null) { Log("building fusion: off (the world's profile)"); return; }
                _bfRows?.Flush();
                Log("building fusion phases (frames stepped/all; ms median/max: simulate, contact, snapshot, frame interval, Main thread (FrameTiming), Main work (less present wait), fusion Main (Step + request), fusion overrun; deferred units; rigidbodies min..max; hulls min..max; longest skipped run):");
                foreach (string phase in new[] { "idle", "cut", "aggregating", "after" })
                {
                    if (!_bfPhases.TryGetValue(phase, out PhaseStat s)) continue;
                    Log("building fusion phase " + phase + ": frames " + s.stepped + "/" + s.frames + "; simulate " + Ms(s.simulate) + " contact " + Ms(s.contact) + " snapshot " + Ms(s.snapshot) + " frame " + Ms(s.main) + " mainThread " + Ms(s.mainWork) + " fusion " + Ms(s.fusion) + " overrun " + Ms(s.fusionOverrun)
                        + "; deferred " + s.deferred + "; rigidbodies " + s.minBodies + ".." + s.maxBodies + "; hulls " + s.minHulls + ".." + s.maxHulls + "; skipped run " + s.maxSkips);
                }

                Log("building fusion Main breakdown (ms total, max per unit): step " + (f.StepSeconds * 1000).ToString("F2", Inv) + " (max " + (f.MaxStepSeconds * 1000).ToString("F3", Inv) + "), request " + (f.RequestSeconds * 1000).ToString("F2", Inv) + " (max " + (f.MaxRequestSeconds * 1000).ToString("F3", Inv)
                    + "); classify+admit " + (f.ClassifySeconds * 1000).ToString("F2", Inv) + " (max " + (f.MaxClassifySeconds * 1000).ToString("F3", Inv) + "), publish " + (f.PublishSeconds * 1000).ToString("F2", Inv) + " (max " + (f.MaxPublishSeconds * 1000).ToString("F3", Inv) + ", " + f.MaxCutMembers + " members), final "
                    + (f.FinalSeconds * 1000).ToString("F2", Inv) + " (max " + (f.MaxFinalSeconds * 1000).ToString("F3", Inv) + "), union " + (f.UnionSeconds * 1000).ToString("F2", Inv) + " (max " + (f.MaxUnionSeconds * 1000).ToString("F3", Inv) + "), mass " + (f.MassSeconds * 1000).ToString("F2", Inv) + " (max " + (f.MaxMassSeconds * 1000).ToString("F3", Inv)
                    + "; wait for the jobs " + (f.MassWaitSeconds * 1000).ToString("F2", Inv) + ", max " + (f.MaxMassWaitSeconds * 1000).ToString("F3", Inv) + "), hull checks in the Step " + (f.HullSeconds * 1000).ToString("F2", Inv) + " (max " + (f.MaxHullSeconds * 1000).ToString("F3", Inv) + ", " + f.HullChecksInStep + " checks), fuse+merge " + (f.FuseSeconds * 1000).ToString("F2", Inv) + " (max " + (f.MaxFuseSeconds * 1000).ToString("F3", Inv) + "); worker (not Main) " + (f.WorkerSeconds * 1000).ToString("F2", Inv) + " (max " + (f.MaxWorkerSeconds * 1000).ToString("F3", Inv)
                    + "); budget " + (f.Settings.mainBudgetSeconds * 1000).ToString("F2", Inv) + " ms: units deferred " + f.DeferredForBudget + ", steps over " + f.OverrunFrames + " (total " + (f.OverrunSeconds * 1000).ToString("F2", Inv) + " ms, max " + (f.MaxOverrunSeconds * 1000).ToString("F3", Inv) + "); preparations made " + f.PreparationsMade
                    + " published " + f.PreparationsPublished + " refused " + f.PreparationsRefused + " stale " + f.PreparationsStale + " abandoned " + f.PreparationsAbandoned + " re-offered " + f.PreparationsReoffered + " in flight " + f.PreparationsInFlight + ", max wait " + (f.MaxPreparationSeconds * 1000).ToString("F1", Inv) + " ms"
                    + "; mass reserve now " + (f.MassReserveSeconds * 1000).ToString("F3", Inv) + " ms, reschedule waits " + (f.MassRescheduleWaitSeconds * 1000).ToString("F3", Inv) + " ms");

                Log("building fusion publication parts (ms total): sides kept on the old body positive " + f.SidesKeptPositive + " / negative " + f.SidesKeptNegative + ", moved at the publications: members " + f.MembersMovedAtPublication
                    + " (most in one " + f.MaxMembersMoved + "), colliders " + f.CollidersMovedAtPublication + " (copies " + f.CopiesMovedAtPublication + "); members moved " + (f.PublishMoveSeconds * 1000).ToString("F2", Inv) + ", copies at the switch " + (f.PublishShadowSeconds * 1000).ToString("F2", Inv)
                    + ", exclusions " + (f.PublishIgnoreSeconds * 1000).ToString("F2", Inv) + " (" + f.IgnoredPairs + " pairs kept apart, most in one cut " + f.MaxIgnoredPairs + ", max " + (f.MaxIgnoreSeconds * 1000).ToString("F3", Inv) + " ms; API calls " + f.ExclusionCalls
                    + ", group cuts by a layer pair " + f.ExcludedByLayers + " and by pairs " + f.ExcludedByPairs + " (fallbacks to pairs, outside the layer pairs' performance: not on the base layer " + f.ExclusionFallbacksNotBaseLayer + ", no layer pair " + f.ExclusionFallbacksNoPairs + "), Steps waiting for a free pair " + f.LayerPairWaits + ", given back in " + (f.ExclusionReleaseSeconds * 1000).ToString("F2", Inv)
                    + " ms; pool " + ExclusionLayerPairs.PairCount + " pairs, most lent " + ExclusionLayerPairs.MaxLent + ", refusals " + ExclusionLayerPairs.Refusals + "), rest and drop " + (f.PublishRestSeconds * 1000).ToString("F2", Inv)
                    + ", submit and record " + (f.PublishSubmitSeconds * 1000).ToString("F2", Inv) + "; heaviest publication: " + f.MaxPublishBreakdown
                    + "; copies made ahead " + f.ShadowsPrepared + " (dropped " + f.ShadowsPreparedDropped + ", " + (f.ShadowPrepareSeconds * 1000).ToString("F2", Inv) + " ms, max " + (f.MaxShadowPrepareSeconds * 1000).ToString("F3", Inv) + ")"
                    + "; masses: samples " + (f.MassSampleSeconds * 1000).ToString("F2", Inv) + " (max " + (f.MaxMassSampleSeconds * 1000).ToString("F3", Inv) + "), applies at the Step's end " + (f.MassApplySeconds * 1000).ToString("F2", Inv) + " (max " + (f.MaxMassApplySeconds * 1000).ToString("F3", Inv)
                    + "), schedules " + f.MassSchedules + " (" + f.MassSamplesGathered + " samples; by a retirement outside a collection " + f.MassSchedulesByRetirement + ", reschedules " + f.MassReschedules + ")"
                    + "; the applies by part (ms total): " + string.Join(", ", BuildingFusion.MassApplyPartNames.Select((n, i) => n + " " + (f.MassApplyPartSeconds[i] * 1000).ToString("F2", Inv)))
                    + " (Step ends " + f.MassApplyEnds + ", groups applied " + f.MassGroupsApplied + ", members " + f.MassMembersApplied + ", member colliders " + f.MassHullsApplied + "); heaviest Step end: " + f.MaxMassApplyBreakdown);
                Log("building fusion phase changes: " + string.Join(" ", _bfPhaseChanges));
                Log("building fusion physics steps: frames stepped " + _bfStepped + ", skipped " + _bfSkipped + ", longest run of skipped frames " + _bfMaxSkips + ", verification steps " + CutPhysicsStep.VerificationSteps
                    + " (last " + (CutPhysicsStep.LastVerificationSeconds * 1000).ToString("F3", Inv) + " ms, overrun total " + (CutPhysicsStep.VerificationOverrunSeconds * 1000).ToString("F3", Inv) + " ms), owed time dropped "
                    + (CutPhysicsStep.Clock.DroppedSeconds * 1000).ToString("F1", Inv) + " ms, expected now " + (CutPhysicsStep.ExpectedSimulateSeconds * 1000).ToString("F3", Inv) + " ms");
                Log("building fusion aggregation: begun " + f.AggregationsBegun + " (deadline " + f.AggregationsByDeadline + ", next slash " + f.AggregationsBySlash + "), completed " + f.AggregationsCompleted + ", max " + (f.MaxAggregationSeconds * 1000).ToString("F1", Inv)
                    + " ms, total " + (f.AggregationSeconds * 1000).ToString("F1", Inv) + " ms, frames waiting for Finals " + f.AggregationWaitFrames + "; held hits " + f.HeldRequests + " (holds ended " + f.HeldProcessed + ": routed into a cut path " + f.HeldRouted + ", refused at the routing " + f.HeldRefused + "; the hits' results are in the hits summary"
                    + ", max wait " + (f.MaxHeldWaitSeconds * 1000).ToString("F1", Inv) + " ms); free unions " + f.FreeUnions + " (" + f.UnionMembersMoved + " members; " + (f.UnionSeconds * 1000).ToString("F3", Inv) + " ms, max " + (f.MaxUnionSeconds * 1000).ToString("F3", Inv)
                    + "); bodies made/destroyed/reused " + f.BodiesMade + "/" + f.BodiesDestroyed + "/" + f.BodiesReused + ", colliders made/destroyed " + f.CollidersMade + "/" + f.CollidersDestroyed + "; groups now " + f.GroupCount + ", most at once " + _bfMaxGroups
                    + "; group cuts " + f.GroupCuts + " (refused " + f.GroupCutsRefused + "), finals " + f.FinalsPublished + " (failed " + f.FinalsFailed + "), fused pieces " + f.FusedPieces + ", groups held/released " + f.GroupsHeld + "/" + f.GroupsReleased
                    + "; groups held/released " + f.GroupsHeld + "/" + f.GroupsReleased);
                foreach (FusedGroup g in f.Groups) Log("building fusion group " + g.Key + " " + g.Root.name + ": members " + g.MemberCount + ", kinematic " + g.Kinematic + " (body " + (g.Body != null ? g.Body.isKinematic.ToString() : "none") + "), anchored " + g.Anchored + ", busy " + g.Busy + ", mass " + g.Mass.ToString("F1", Inv) + ", real bodies under its members " + RealBodiesUnder(g));
                foreach (BuildingFusion.HeldOutcome h in f.HeldOutcomes) Log("building fusion held hit: slash " + h.slashId + " member " + h.fragment + " waited " + (h.waitSeconds * 1000).ToString("F1", Inv) + " ms -> " + h.outcome);
                Log("building fusion hits: " + f.Hits.Count + " on fused members, by outcome: published " + f.HitsPublished + " (own preparation, attached, or included), refused " + f.HitsRefused + ", pending " + f.HitsPending
                    + "; events: prepared " + f.HitsPrepared + ", attached (the same request as a preparation) " + f.HitsAttached + ", included (the same request as the cut in progress) " + f.HitsIncluded + ", held " + f.HitsHeld + " (held again " + f.HitsHeldAgain + "); group cuts refused " + f.GroupCutsRefused);
                foreach (BuildingFusion.HitRecord h in f.Hits) Log("building fusion hit " + h.id + ": t " + h.physicsSeconds.ToString("F3", Inv) + " slash " + h.slashId + " plane " + h.planeId + " member " + h.fragment + (h.preparation > 0 ? " preparation " + h.preparation : "") + " => " + (h.outcome ?? "PENDING") + " [" + h.Events + "]");
                // The crossed members' operations the fusion issued, against the ledger and the geometry: each published one is
                // Completed in the ledger with the fusion's two children, and both children have their geometry committed.
                int published = 0, failed = 0, notCompleted = 0, childrenMismatch = 0, notCommitted = 0, refusedRecovered = 0, refusedNotRecovered = 0;
                foreach (BuildingFusion.MemberOperation op in f.MemberOperations)
                {
                    if (!op.Published)
                    {
                        // A Final PhysX refused (the cook's hull check) is a known failure: allowed when it was attributed to
                        // this operation, aborted in the ledger, and its source retired with nothing of it left -- counted
                        // apart from the successes. Any other failure is a failure.
                        if (op.hullRefused)
                        {
                            bool recovered = HullRefusalRecovered(op, out string how);
                            if (recovered) refusedRecovered++; else refusedNotRecovered++;
                            Log("building fusion member operation " + op.operation.value + " of member " + op.source.value + ": refused by PhysX, " + how + ": " + op.outcome);
                            continue;
                        }

                        failed++;
                        Log("building fusion member operation " + op.operation.value + " of member " + op.source.value + ": " + op.outcome);
                        continue;
                    }

                    published++;
                    bool known = _world.Ledger.TryGetOperation(op.operation, out LogicalCutOperation record);
                    if (!known || record.state != LogicalCutOperationState.Completed) { notCompleted++; Log("building fusion member operation " + op.operation.value + ": ledger " + (known ? record.state.ToString() : "unknown")); }
                    if (known && (!record.positive.Equals(op.positive) || !record.negative.Equals(op.negative))) childrenMismatch++;
                    if (_world.Geometry != null && (!_world.Geometry.TryGetGeometryFrame(op.positive, out _) || !_world.Geometry.TryGetGeometryFrame(op.negative, out _)))
                    {
                        // A child replaced by a later cut has no frame of its own any more: only a child still live counts as not committed.
                        bool positiveLive = _world.Ledger.IsCurrentTarget(op.positive), negativeLive = _world.Ledger.IsCurrentTarget(op.negative);
                        if ((positiveLive && !_world.Geometry.TryGetGeometryFrame(op.positive, out _)) || (negativeLive && !_world.Geometry.TryGetGeometryFrame(op.negative, out _))) { notCommitted++; Log("building fusion member operation " + op.operation.value + ": a live child without a committed geometry frame"); }
                    }
                }

                Log("building fusion member operations: " + f.MemberOperations.Count + " (published " + published + ", refused by PhysX and recovered " + refusedRecovered + ", refused and not recovered " + refusedNotRecovered + ", other failures " + failed
                    + "); ledger not Completed " + notCompleted + ", children mismatch " + childrenMismatch + ", live child not committed " + notCommitted);
                foreach (string e in f.Events) Log("  " + e);

                // The unit's conditions (TL, 2026-09-30).
                Expect(f.AggregationsByDeadline > 0 && f.AggregationsBySlash > 0, "[fusion] the aggregation was begun both by the deadline and by a next Slash");
                Expect(f.AggregationsCompleted == f.AggregationsBegun && f.BuildingsAggregating == 0, "[fusion] every aggregation begun completed");
                Expect(f.HeldProcessed == f.HeldRequests && f.HeldRequests > 0, "[fusion] every held hit's hold ended (" + f.HeldRouted + " routed into a cut path: prepared, attached or included; " + f.HeldRefused + " refused at the routing: dropped or not accepted); their results are the hits' outcomes");
                Expect(f.Hits.Count == f.HitsPublished + f.HitsRefused && f.HitsPending == 0, "[fusion] every hit on a fused member has its one outcome (" + f.Hits.Count + " hits: published " + f.HitsPublished + ", refused " + f.HitsRefused + ", pending " + f.HitsPending + ")");
                int badCompletions = f.Completions.Count(c => c.resting > 1 || c.free > 1);
                foreach (BuildingFusion.Completion c in f.Completions) Log("building fusion completion: building " + c.building + " resting " + c.resting + " free " + c.free + " held " + c.heldHits + " after " + (c.seconds * 1000).ToString("F1", Inv) + " ms (" + c.trigger + ")");
                Expect(f.Completions.Count > 0 && badCompletions == 0 && f.GroupCount <= 2, "[fusion] every aggregation completed with at most one resting and one free group (" + f.Completions.Count + " completions, " + badCompletions
                    + " beyond that), and the groups did not keep growing through the cuts (at the end " + f.GroupCount + "; most at once " + _bfMaxGroups + ", within one Slash's hits, brought back by the next aggregation)");
                Expect(f.FreeUnions > 0 && f.UnionMembersMoved > 0, "[fusion] free groups were united (unions " + f.FreeUnions + ", members moved " + f.UnionMembersMoved + "): the aggregation reduced real bodies, not only counted");
                Expect(f.MemberOperations.Count == f.FinalsPublished + f.FinalsFailed && notCompleted == 0 && childrenMismatch == 0 && notCommitted == 0 && failed == 0 && refusedNotRecovered == 0,
                    "[fusion] every crossed member's operation the fusion issued is Completed in the ledger with its two children, whose geometry is committed, or was refused by PhysX and recovered ("
                    + f.MemberOperations.Count + " operations: completed " + published + ", refused and recovered " + refusedRecovered + " (apart from the successes), refused and not recovered " + refusedNotRecovered + ", other failures " + failed + ")");
                int busy = f.Groups.Count(g => g.Busy);
                Expect(busy == 0 && f.CutsInProgress == 0 && f.PreparationsInFlight == 0, "[fusion] no cut left in progress or in preparation at the end");
                Expect(_bfMaxSkips < 2 * CutPhysicsStep.VerifyAfterSkippedFrames, "[fusion] the physics never stopped: the longest run of skipped frames " + _bfMaxSkips + " is under " + 2 * CutPhysicsStep.VerifyAfterSkippedFrames);
                bool steppedInEveryPhase = true;
                foreach (string phase in new[] { "cut", "aggregating" }) if (_bfPhases.TryGetValue(phase, out PhaseStat s) && s.frames > 10 && s.stepped == 0) steppedInEveryPhase = false;
                Expect(steppedInEveryPhase, "[fusion] the physics stepped during the cuts and the aggregations, not only after");
            }
        }
    }
}
