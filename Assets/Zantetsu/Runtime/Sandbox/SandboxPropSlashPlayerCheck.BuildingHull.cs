using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Core.Slash;
using Zantetsu.ConvexCut;
using Zantetsu.Core.Animation;
using Zantetsu.Core.Input;
using Zantetsu.Observability;
using Zantetsu.Rendering;
using Zantetsu.Trace;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // The building E2E with the hull trial on (2026-09-30): the world's settings read back, the registration as one hull
    // group, the cost of the hull's Step, the request entry and the rest frame by frame with their frame sum (the first
    // frame with hull work apart from the ones after; building-hull.csv), the real bodies, hulls and colliders, the
    // hits' outcomes and their display operations at the ledger's end, and the conditions: cuts published, unions by the
    // deadline and by the next Slash, fusions completed with none given up, every hit answered, one group of one hull at
    // the end (a fixed one and a free one allowed).
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private StreamWriter _bhRows;
            private int _bhLastFrame = -1, _bhFirstWorkFrame = -1, _bhMaxBodies, _bhMaxColliders, _bhStepped, _bhSkipped, _bhSkips, _bhMaxSkips;
            private long _bhLastStepId = -1;
            private double _bhLastStep, _bhLastHit, _bhLastRest, _bhLastReal;
            private readonly List<double> _bhSums = new List<double>(), _bhSumsAfterFirst = new List<double>(), _bhStepMs = new List<double>(), _bhRestMs = new List<double>(), _bhHitMs = new List<double>(), _bhMainWork = new List<double>();
            private double _bhFirstSum, _bhFirstStep, _bhFirstRest, _bhFirstHit;
            private float _bhMaxSpeed;
            private int _bhMaxSpeedFrame = -1;
            private double _bhLastWaveReal = double.NaN;   // the last frame a wave of the ordinary input flew (real seconds)
            private int _bhLastWaveFrame = -1, _bhMaxMembers, _bhMaxPairs, _bhMaxConstraints, _bhNonKinematicFrames, _bhMaxWithGeometry, _bhFramesNotOneEach, _bhFirstNotOneEach = -1;
            private string _bhNotOneEachWhy;
            private readonly FrameTiming[] _bhTimings = new FrameTiming[1];

            private static double RestSeconds(BuildingRest rest) => rest == null ? 0.0 : rest.ContactSeconds + rest.SupportSeconds + rest.SleepSeconds + rest.ReleaseSeconds;

            private readonly List<Collider> _bhColliders = new List<Collider>();
            private readonly HashSet<Collider> _bhOwn = new HashSet<Collider>();
            private bool _bhSettingsLogged;

            /// <summary>
            /// The colliders and bodies that belong to the building's hull groups (under their Roots), and, apart, the enabled
            /// mesh colliders of everything else in the scene (the city, the floor) -- unless <paramref name="withOthers"/> is
            /// false (the city walk's frames: a city's every collider found each frame; -1 then).
            /// </summary>
            private void CountBuildingPhysics(BuildingHullFusion h, out int colliders, out int bodies, out int others, bool withOthers = true)
            {
                colliders = 0; bodies = 0; others = withOthers ? 0 : -1;
                _bhOwn.Clear();
                foreach (HullGroup g in h.Groups)
                {
                    if (g.Root == null) continue;
                    g.Root.GetComponentsInChildren(true, _bhColliders);
                    foreach (Collider c in _bhColliders) { if (c.enabled) colliders++; _bhOwn.Add(c); }
                    if (g.Body != null) bodies++;
                }

                if (!withOthers) return;
                foreach (MeshCollider c in UnityEngine.Object.FindObjectsByType<MeshCollider>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) if (c.enabled && !_bhOwn.Contains(c)) others++;
            }

            // At the replay's start: the settings and the registration as the hull trial made them.
            private void BuildingHullBegin() => BuildingHullBegin(_building.Group, _building.Fragment);

            private void BuildingHullBegin(HullGroup group, LogicalFragmentId fragment)
            {
                BuildingHullFusion h = _world.Hulls;
                CutWorldProfile p = _world.Profile;
                // The settings and the scene around are the world's, the same for every building: written and judged once (a city holds hundreds).
                bool first = !_bhSettingsLogged;
                _bhSettingsLogged = true;
                if (first) Log("building hull settings from the world's profile (" + p.name + "): hull on=" + p.BuildingHull.enabled + " deadline=" + p.BuildingHull.deadlineSeconds.ToString("R", Inv) + " s budget=" + (p.BuildingHull.mainBudgetSeconds * 1000).ToString("R", Inv)
                    + " ms; rest on=" + p.BuildingRest.enabled + " mode=" + p.BuildingRest.mode + " timeout=" + p.BuildingRest.timeoutSeconds.ToString("R", Inv) + " s supportSteps=" + p.BuildingRest.supportSteps
                    + "; fusion on=" + p.BuildingFusion.enabled + "; World D6 on=" + p.BuildingWorld.enabled + "; world: hulls " + (h != null) + " fusion " + (_world.Fusion != null) + " rest " + (_world.Rest != null && _world.Rest.Enabled) + " (" + (_world.Rest != null ? _world.Rest.Settings.mode.ToString() : "none") + ")");
                if (first) Expect(p.BuildingHull.enabled && p.BuildingRest.enabled && p.BuildingRest.mode == BuildingRestMode.Kinematic && !p.BuildingFusion.enabled && !p.BuildingWorld.enabled
                        && h != null && _world.Fusion == null && _world.Rest != null && _world.Rest.Enabled,
                    "[hull] the world runs the hull trial with the kinematic rest, the old fusion off and the World D6 off, as its profile says");
                bool rootOwned = _world.Owners.TryGet(fragment, out PhysicsFragmentOwner root);
                int colliders = group != null && group.Root != null ? group.Root.GetComponentsInChildren<Collider>(true).Length : -1;
                Log("building hull registration: group " + (group != null ? group.Id.ToString() : "none") + " anchored=" + (group != null && group.Anchored) + " kinematic=" + (group != null && group.Kinematic) + " body kinematic=" + (group != null && group.Body != null && group.Body.isKinematic)
                    + " hull vertices=" + (group != null ? group.VertexCount : 0) + " faces=" + (group != null ? group.FaceCount : 0) + " colliders under the root=" + colliders + " members=" + (group != null ? group.MemberCount : 0) + " mass=" + (group != null ? group.Mass.ToString("F1", Inv) : "none")
                    + "; root fragment owner display-only=" + (rootOwned && root.IsDisplayOnly) + "; rest tracked anchored=" + _world.Rest.TrackedAnchored + " dynamic=" + _world.Rest.TrackedDynamic);
                if (h.Settings.kinematicDisplay)
                {
                    Expect(group != null && group.Kinematic && group.Body != null && group.Body.isKinematic && colliders == 1 && group.MemberCount == 1 && rootOwned && root.IsDisplayOnly && !_world.Rest.IsTracked(group.Body),
                        "[hull kinematic] the building is registered as one kinematic hull group with one collider and one display-only member, not followed by the rest (tracked " + (group != null && _world.Rest.IsTracked(group.Body)) + ")");
                }
                else
                {
                    Expect(group != null && group.Anchored && group.Kinematic && group.Body != null && group.Body.isKinematic && colliders == 1 && group.MemberCount == 1 && rootOwned && root.IsDisplayOnly && _world.Rest.TrackedAnchored == 1,
                        "[hull] the building is registered as one anchored hull group with one collider and one display-only member, followed by the rest as ground");
                }
                if (!first) return;
                CountBuildingPhysics(h, out _, out _, out int cityColliders);
                int staticBodies = 0;
                foreach (Rigidbody b in UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)) if (group == null || b != group.Body) staticBodies++;
                Log("building hull configuration: " + (cityWalk ? "the city walk's buildings (" + h.GroupCount + " groups made so far)" : "one building (college_001, one anchor)") + ", the floor, and the scene around it: enabled mesh colliders not the building's " + cityColliders + " (the city's and the floor's, static), other rigidbodies " + staticBodies
                    + "; the trial's settings: max new penetration " + h.Settings.maxNewPenetrationMetres.ToString("F3", Inv) + " m (a diagnostic value), vertex limit " + p.VertexLimit + "; always kinematic " + h.Settings.kinematicDisplay + " (drop " + h.Settings.animationSeconds.ToString("R", Inv) + " s, " + h.Settings.dropHorizontalMetres.ToString("R", Inv) + " m horizontal, " + h.Settings.dropVerticalMetres.ToString("R", Inv) + " m vertical); a candidate hull is judged before any body merges; staged stop " + h.Settings.stageSeconds.ToString("R", Inv) + " s, sibling constraint " + h.Settings.siblingD6 + " opening " + h.Settings.siblingOpeningMetres.ToString("R", Inv) + " m");
            }

            private static string DescribeChildren(BuildingHullFusion.ChildCounts c) =>
                "children empty " + c.empty + " / with a shape " + c.withShape + " / cut again since " + c.cutAgain + " / retired since " + c.retired
                + " / not done yet " + c.pending + " / Completed without geometry " + c.mismatch + " / not in the ledger " + c.unknown;

            private void BuildingHullFrame(int frame)
            {
                if (_world == null || _world.Hulls == null || frame == _bhLastFrame) return;
                _bhLastFrame = frame;
                BuildingHullFusion h = _world.Hulls;
                BuildingRest rest = _world.Rest;
                if (_bhRows == null)
                {
                    _bhRows = DetailWriter("building-hull.csv");
                    _bhRows.WriteLine("frame,scenario,real,t,groups,kinematic,free,anchoredGroups,stagedGroups,restGround,restPinned,pairsMoving,constraintsLive,dropsRunning,hullUpdatesRunning,hullRequestsWaiting,membersCommitted,membersEmpty,membersPending,ledgerFragments,ledgerOperations,liveHulls,bodies,colliders,members,waves,cuts,unions,fusions,givenUp,notAchieved,stale,heldNow,cutsInProgress,fusionsInFlight,displayOpen,maxFreeSpeed,hullStepMs,hitMs,restMs,frameSumMs,simulateMs,stepped,skipsInARow,mainThreadMs,mainWorkMs,frameIntervalMs");
                    _bhLastStep = h.StepSeconds; _bhLastHit = h.HitSeconds; _bhLastRest = RestSeconds(rest);
                    _bhLastReal = Time.realtimeSinceStartupAsDouble;
                }

                double real = Time.realtimeSinceStartupAsDouble;
                double intervalMs = (real - _bhLastReal) * 1000.0;
                _bhLastReal = real;
                double stepMs = (h.StepSeconds - _bhLastStep) * 1000.0, hitMs = (h.HitSeconds - _bhLastHit) * 1000.0, restMs = (RestSeconds(rest) - _bhLastRest) * 1000.0;
                _bhLastStep = h.StepSeconds; _bhLastHit = h.HitSeconds; _bhLastRest = RestSeconds(rest);
                double sum = stepMs + hitMs + restMs;
                double mainThreadMs = double.NaN, mainWorkMs = double.NaN;
                FrameTimingManager.CaptureFrameTimings();
                if (FrameTimingManager.GetLatestTimings(1, _bhTimings) > 0)
                {
                    mainThreadMs = _bhTimings[0].cpuMainThreadFrameTime;
                    mainWorkMs = _bhTimings[0].cpuMainThreadFrameTime - _bhTimings[0].cpuMainThreadPresentWaitTime;
                }

                if (!double.IsNaN(mainWorkMs)) _bhMainWork.Add(mainWorkMs);
                double simulateMs = CutPhysicsStep.LastSimulateSeconds * 1000.0;
                long stepId = CutPhysicsStep.Clock.StepId;
                bool stepped = _bhLastStepId >= 0 && stepId != _bhLastStepId;
                if (_bhLastStepId >= 0) { if (stepped) { _bhStepped++; _bhSkips = 0; } else { _bhSkipped++; _bhSkips++; _bhMaxSkips = Math.Max(_bhMaxSkips, _bhSkips); } }
                _bhLastStepId = stepId;
                CountBuildingPhysics(h, out int colliders, out int bodies, out int _, withOthers: !cityWalk);
                _bhMaxBodies = Math.Max(_bhMaxBodies, bodies);
                _bhMaxColliders = Math.Max(_bhMaxColliders, colliders);
                int kinematic = 0, free = 0, members = 0, anchoredGroups = 0, stagedGroups = 0;
                float speed = 0f;
                foreach (HullGroup g in h.Groups) { members += g.MemberCount; if (g.Anchored) anchoredGroups++; if (g.Staged) stagedGroups++; if (g.Kinematic) kinematic++; else { free++; if (g.Body != null) speed = Mathf.Max(speed, g.Body.linearVelocity.magnitude); } }
                _bhMaxPairs = Math.Max(_bhMaxPairs, h.PairsMoving);
                h.CountDisplay(out int _, out int committed, out int emptyMembers, out int pendingMembers, out int ledgerFragments, out int ledgerOperations);
                _bhMaxWithGeometry = Math.Max(_bhMaxWithGeometry, committed);
                if (h.Settings.kinematicDisplay)
                {
                    HullDropsFrame(h);   // each drop's members read back against its curve (2026-10-01)
                    foreach (HullGroup g in h.Groups) if (g.Body != null && (!g.Body.isKinematic || !g.Kinematic)) { _bhNonKinematicFrames++; break; }
                    // Exactly one of each a building at this frame (not a maximum): its group, its body, its one enabled collider, its one hull.
                    var groupsOf = new Dictionary<int, int>();
                    string why = null;
                    foreach (HullGroup g in h.Groups)
                    {
                        if (g.State == HullGroupState.Gone) continue;
                        groupsOf.TryGetValue(g.Building, out int n); groupsOf[g.Building] = n + 1;
                        int enabled = 0;
                        if (g.Root != null) { g.Root.GetComponentsInChildren(true, _bhColliders); foreach (Collider c in _bhColliders) if (c.enabled) enabled++; }
                        if (g.Body == null || enabled != 1 || g.HullCount != 1 || g.Shape == null) why = "group " + g.Id + ": body " + (g.Body != null) + ", enabled colliders " + enabled + ", hulls " + g.HullCount;
                    }

                    foreach (KeyValuePair<int, int> b in groupsOf) if (b.Value != 1) why = "building " + b.Key + ": " + b.Value + " groups";
                    if (groupsOf.Count != h.GroupsMade) why = "buildings standing " + groupsOf.Count + " of " + h.GroupsMade;
                    if (why != null) { _bhFramesNotOneEach++; if (_bhFirstNotOneEach < 0) { _bhFirstNotOneEach = frame; _bhNotOneEachWhy = why; } }
                }

                // The display's structure builds, validations and placement passes are in frames.csv, stamped with the frame
                // that ran them (a difference of the counters between two reads here fell on the next frame's row).
                _bhMaxConstraints = Math.Max(_bhMaxConstraints, h.ConstraintsLive);
                _bhMaxMembers = Math.Max(_bhMaxMembers, members);
                int waves = _katana != null ? _katana.WaveCount : 0;
                if (waves > 0 && _bhScenario == "slashes") { _bhLastWaveReal = real; _bhLastWaveFrame = frame; }
                if (speed > _bhMaxSpeed) { _bhMaxSpeed = speed; _bhMaxSpeedFrame = frame; }
                // The first frame with hull work (a hit's entry or a Step that did something) is kept apart: JIT and initialisation are suspected in it, not excluded.
                if (_bhFirstWorkFrame < 0 && (hitMs > 0.0 || h.CutsInProgress > 0 || h.GroupCuts > 0)) { _bhFirstWorkFrame = frame; _bhFirstSum = sum; _bhFirstStep = stepMs; _bhFirstRest = restMs; _bhFirstHit = hitMs; }
                else if (_bhFirstWorkFrame >= 0) _bhSumsAfterFirst.Add(sum);
                _bhSums.Add(sum); _bhStepMs.Add(stepMs); _bhRestMs.Add(restMs); _bhHitMs.Add(hitMs);
                _bhRows.WriteLine(string.Join(",", frame, _bhScenario, real.ToString("F3", Inv), CutPhysicsStep.Clock.PhysicsSeconds.ToString("F3", Inv), h.GroupCount, kinematic, free, anchoredGroups, stagedGroups, rest != null ? rest.TrackedAnchored : 0, rest != null ? rest.PinnedGroups : 0, h.PairsMoving, h.ConstraintsLive, h.AnimationsRunning, h.HullUpdatesInFlight, h.WaitingHullRequests, committed, emptyMembers, pendingMembers, ledgerFragments, ledgerOperations, h.LiveHulls, bodies, colliders, members, waves,
                    h.GroupCuts, h.Unions, h.Fusions, h.FusionsGivenUp, h.HullsNotAchieved, h.FusionsStale, h.HeldNow, h.CutsInProgress, h.FusionsInFlight, h.DisplayOperationsOpen, speed.ToString("F2", Inv),
                    stepMs.ToString("F3", Inv), hitMs.ToString("F3", Inv), restMs.ToString("F3", Inv), sum.ToString("F3", Inv), simulateMs.ToString("F3", Inv), stepped ? 1 : 0, _bhSkips,
                    mainThreadMs.ToString("F3", Inv), mainWorkMs.ToString("F3", Inv), intervalMs.ToString("F3", Inv)));
            }

            private void BuildingHullClose()
            {
                _bhRows?.Dispose();
                _bhRows = null;
            }

            private static double Percentile(List<double> values, double q)
            {
                if (values.Count == 0) return 0.0;
                var sorted = new List<double>(values); sorted.Sort();
                return sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(q * (sorted.Count - 1)))];
            }

            private static string Stat(List<double> values) => "p50 " + Percentile(values, 0.5).ToString("F3", Inv) + " p99 " + Percentile(values, 0.99).ToString("F3", Inv) + " max " + (values.Count > 0 ? values.Max() : 0.0).ToString("F3", Inv);

            // ---- the hull scenario (2026-09-30): continuous Slashes replayed, the recovery to one hull, a hold, a re-cut by synthetic Slashes through the current hulls, the recovery again ----

            private string _bhScenario = "slashes";
            private int _bhSyntheticSlashes;

            /// <summary>
            /// The re-cut (TL, 2026-09-30): **one** synthetic Slash, one sweep given once to the ordinary detector (the
            /// acceptance, the driver): a level plane through the hull centre of the heaviest idle group of the building, the
            /// sweep spanning every idle group's bounds by a metre. That it also crosses other groups is ordinary. The groups
            /// it hits are told apart by kind -- a fused hull (an adopted candidate), a group kept apart after its class's
            /// candidate was not adopted, or another -- and each kind hit is judged in three parts, apart: a real hull hit,
            /// a hit accepted, a cut published with its display operations committed. Nothing calls the driver directly.
            /// </summary>
            private IEnumerator HullRecut(BuildingHullFusion h)
            {
                var kindOf = new Dictionary<LogicalFragmentId, string>();
                var groupKind = new Dictionary<int, string>();
                HullGroup through = null;
                float3 lo = new float3(float.MaxValue), hi = new float3(float.MinValue);
                int idle = 0;
                foreach (HullGroup g in h.Groups)
                {
                    if (g.State != HullGroupState.Idle || g.Root == null || g.Shape == null) continue;
                    idle++;
                    string kind = h.IsApartAfterRejection(g) ? "apart after a rejection" : g.HullIsFused ? "fused hull" : "other";
                    groupKind[g.Id] = kind;
                    foreach (LogicalFragmentId f in g.Fragments) kindOf[f] = kind;
                    g.Shape.ConvexBounds(0, out float3 glo, out float3 ghi);
                    float4x4 toWorld = (float4x4)g.Root.transform.localToWorldMatrix;
                    for (int k = 0; k < 8; k++)
                    {
                        float3 corner = math.transform(toWorld, new float3((k & 1) != 0 ? ghi.x : glo.x, (k & 2) != 0 ? ghi.y : glo.y, (k & 4) != 0 ? ghi.z : glo.z));
                        lo = math.min(lo, corner); hi = math.max(hi, corner);
                    }

                    if (through == null || g.Mass > through.Mass) through = g;
                }

                double at = Time.realtimeSinceStartupAsDouble;
                if (through == null)
                {
                    Log("hull re-cut: no idle group at frame " + Time.frameCount + ": not exercised");
                    Expect(false, "[hull re-cut] an idle group stood to be re-cut");
                    yield break;
                }

                through.Shape.ConvexBounds(0, out float3 tlo, out float3 thi);
                float y = math.transform((float4x4)through.Root.transform.localToWorldMatrix, (tlo + thi) * 0.5f).y;
                long slash = 900000 + (++_bhSyntheticSlashes);
                float x0 = lo.x - 1f, x1 = hi.x + 1f, z0 = lo.z - 1f, z1 = hi.z + 1f;
                var sweep = new SlashSweep(slash, Time.unscaledTimeAsDouble, false, new Plane(Vector3.up, -y), Vector3.right, Vector3.forward,
                    new Vector3(x0, y, z0), new Vector3(x0, y, z1), new Vector3(x1, y, z0), new Vector3(x1, y, z1));
                var kindsStanding = new Dictionary<string, int>();
                foreach (string k in groupKind.Values) { kindsStanding.TryGetValue(k, out int c); kindsStanding[k] = c + 1; }
                Log("hull re-cut: one synthetic Slash " + slash + " at frame " + Time.frameCount + " real " + at.ToString("F3", Inv) + " (" + (double.IsNaN(_bhLastWaveReal) ? "no ordinary wave seen" : (at - _bhLastWaveReal).ToString("F2", Inv) + " s after the last ordinary Slash's wave")
                    + "): level y " + y.ToString("F3", Inv) + " through group " + through.Id + " (" + groupKind[through.Id] + ", " + through.VertexCount + " v, mass " + through.Mass.ToString("R", Inv) + "), sweep x [" + x0.ToString("F2", Inv) + ", " + x1.ToString("F2", Inv) + "] z [" + z0.ToString("F2", Inv) + ", " + z1.ToString("F2", Inv) + "] over " + idle + " idle groups ("
                    + string.Join(", ", kindsStanding.Select(p => p.Key + " " + p.Value)) + "); class outcomes now: " + h.DescribeAggregationStates());
                int hitsBefore = h.Hits.Count, cutsBefore = h.GroupCuts;
                var hitReal = new Dictionary<string, bool>(); var accepted = new Dictionary<string, int>(); var refused = new Dictionary<string, int>();
                foreach (string k in new[] { "fused hull", "apart after a rejection", "other" }) { hitReal[k] = false; accepted[k] = 0; refused[k] = 0; }
                _detector.Evaluate(new[] { sweep }, new[] { slash });
                for (int i = 0; i < _detector.HitCount; i++)
                {
                    SlashHitConfirmed hit = _detector.HitAt(i);
                    bool real = kindOf.TryGetValue(hit.Fragment, out string kind);
                    if (real)
                    {
                        hitReal[kind] = true;
                        if (hit.Acceptance == ProvisionalCutAcceptance.Pending || hit.Acceptance == ProvisionalCutAcceptance.Held) accepted[kind]++; else refused[kind]++;
                    }

                    Log("hull re-cut: detector hit " + i + " fragment " + hit.Fragment.value + " (" + (real ? kind : "not a hull member") + ") => " + hit.Acceptance);
                }

                float by = Time.realtimeSinceStartup + 60f;
                bool answered = false;
                while (Time.realtimeSinceStartup < by)
                {
                    yield return null;
                    bool all = true;
                    for (int i = hitsBefore; i < h.Hits.Count; i++) all &= !h.Hits[i].IsPending;
                    if (all && h.CutsInProgress == 0 && h.DisplayOperationsOpen == 0 && CutsSettled()) { answered = true; break; }
                }

                var published = new Dictionary<string, int>(); var committed = new Dictionary<string, int>();
                foreach (string k in hitReal.Keys) { published[k] = 0; committed[k] = 0; }
                for (int i = hitsBefore; i < h.Hits.Count; i++)
                {
                    BuildingHullFusion.HullHit hit = h.Hits[i];
                    string kind = groupKind.TryGetValue(hit.group, out string k) ? k : "other";
                    Log("hull re-cut: request " + hit.id + " group " + hit.group + " g" + hit.generation + " (" + kind + ") => " + (hit.outcome ?? "PENDING") + " (display operations " + hit.displayOperations.Count + ")");
                    if (hit.outcome != "Published") continue;
                    published[kind]++;
                    bool all = true;
                    foreach (CutOperationId op in hit.displayOperations)
                    {
                        all &= _world.Ledger.TryGetOperation(op, out LogicalCutOperation record) && record.state == LogicalCutOperationState.Completed && _world.Geometry.StageOf(op) == CutGeometryStage.Committed;
                    }

                    if (all) committed[kind]++;
                }

                Log("hull re-cut: " + (answered ? "answered and settled" : "NOT settled within 60 s: " + h.DescribeUnsettled()) + " at frame " + Time.frameCount + " real " + Time.realtimeSinceStartupAsDouble.ToString("F3", Inv) + "; hits " + _detector.HitCount + ", requests " + (h.Hits.Count - hitsBefore) + ", cuts " + cutsBefore + " -> " + h.GroupCuts + "; " + h.DescribeCounts());
                foreach (string kind in new[] { "fused hull", "apart after a rejection", "other" })
                {
                    kindsStanding.TryGetValue(kind, out int n);
                    if (n == 0) { Log("hull re-cut (" + kind + "): no such group stood: not exercised"); continue; }
                    Log("hull re-cut (" + kind + "): groups standing " + n + ", real hull hit " + hitReal[kind] + ", accepted " + accepted[kind] + " (refused at once " + refused[kind] + "), published " + published[kind] + ", with every display operation committed " + committed[kind]);
                    if (!hitReal[kind]) { Log("hull re-cut (" + kind + "): the one sweep crossed no such group: not judged"); continue; }
                    Expect(accepted[kind] > 0, "[hull re-cut: " + kind + "] (2) a hit was accepted (Pending or Held) by the ordinary acceptance (" + accepted[kind] + " accepted, " + refused[kind] + " refused at once)");
                    Expect(answered && published[kind] > 0 && committed[kind] == published[kind], "[hull re-cut: " + kind + "] (3) a cut was published and every published hit's display operations reached the ledger's end committed (" + published[kind] + " published, " + committed[kind] + " committed)");
                }

                Expect(hitReal["fused hull"] || hitReal["apart after a rejection"] || hitReal["other"], "[hull re-cut] (1) the one synthetic sweep through the current hull hit a real hull (" + _detector.HitCount + " detector hits)");
            }

            /// <summary>
            /// The re-cut during a drop (TL, 2026-10-01), a short section of its own after the script, apart from its
            /// figures: one synthetic Slash through the building's hull, given once to the ordinary detector; when its cut
            /// is seen published with its drop running, a second Slash (upright, through the hull's centre) given once to
            /// the detector. Judged: the first drop stopped part way by the re-cut (where it stood), both cuts published and
            /// their display operations committed; the one body, collider and hull a frame and the in-plane moves are the
            /// run's own judgements, which cover these frames too.
            /// </summary>
            private IEnumerator HullMidDropRecut()
            {
                _bhMidDropRecutRan = true;
                BuildingHullFusion h = _world.Hulls;
                Log("hull mid-drop re-cut: the script's part ended at frame " + Time.frameCount + ": " + HitTally(h, 0) + "; display cuts " + h.DisplayCuts + ", drops started " + h.AnimationsStarted + " completed " + h.AnimationsCompleted + " stopped by a re-cut " + h.AnimationsStoppedByReCut
                    + ", hull updates exchanged " + h.HullUpdatesAdopted + " refused " + h.HullUpdatesRefused + ", Main ms: classification " + (h.PrepareDisplaySeconds * 1000).ToString("F2", Inv) + " publication " + (h.PublishDisplaySeconds * 1000).ToString("F2", Inv) + " (the figures below include this section)");
                if (_world.Display != null) Log("display GPU copy at the script's end: " + DescribeGpuCopy(_world.Display) + "; commits " + _world.Display.CommitCalls);
                _phase = "hull-middrop";
                _bhScenario = "middrop";
                float by = Time.realtimeSinceStartup + 10f;
                while (!h.IsSettled && Time.realtimeSinceStartup < by) yield return null;
                HullGroup group = null;
                foreach (HullGroup g in h.Groups) if (g.State == HullGroupState.Idle && g.Root != null && g.Shape != null && (group == null || g.Mass > group.Mass)) group = g;
                if (!h.IsSettled || group == null)
                {
                    Log("hull mid-drop re-cut: no settled idle building group at frame " + Time.frameCount + " (" + h.DescribeUnsettled() + "): not exercised");
                    Expect(false, "[hull mid-drop] a settled building stood to be cut twice");
                    yield break;
                }

                group.Shape.ConvexBounds(0, out float3 glo, out float3 ghi);
                float3 lo = new float3(float.MaxValue), hi = new float3(float.MinValue);
                float4x4 toWorld = (float4x4)group.Root.transform.localToWorldMatrix;
                for (int k = 0; k < 8; k++)
                {
                    float3 corner = math.transform(toWorld, new float3((k & 1) != 0 ? ghi.x : glo.x, (k & 2) != 0 ? ghi.y : glo.y, (k & 4) != 0 ? ghi.z : glo.z));
                    lo = math.min(lo, corner); hi = math.max(hi, corner);
                }

                float3 c = (lo + hi) * 0.5f;
                int hitsBefore = h.Hits.Count, cutsBefore = h.GroupCuts, stoppedBefore = h.AnimationsStoppedByReCut;
                long first = 900000 + (++_bhSyntheticSlashes);
                var level = new SlashSweep(first, Time.unscaledTimeAsDouble, false, new Plane(Vector3.up, -c.y), Vector3.right, Vector3.forward,
                    new Vector3(lo.x - 1f, c.y, lo.z - 1f), new Vector3(lo.x - 1f, c.y, hi.z + 1f), new Vector3(hi.x + 1f, c.y, lo.z - 1f), new Vector3(hi.x + 1f, c.y, hi.z + 1f));
                _detector.Evaluate(new[] { level }, new[] { first });
                int firstHits = _detector.HitCount;
                Log("hull mid-drop re-cut: Slash " + first + " (level y " + c.y.ToString("F3", Inv) + " through group " + group.Id + ") given at frame " + Time.frameCount + ": detector hits " + firstHits);

                // Its publication, with its drop running.
                by = Time.realtimeSinceStartup + 10f;
                while (h.GroupCuts == cutsBefore && Time.realtimeSinceStartup < by) yield return null;
                bool published = h.GroupCuts > cutsBefore;
                bool running = h.AnimationsRunning > 0;
                int seenAt = Time.frameCount;
                long second = 900000 + (++_bhSyntheticSlashes);
                int secondHits = 0;
                if (published && running)
                {
                    var upright = new SlashSweep(second, Time.unscaledTimeAsDouble, false, new Plane(Vector3.right, -c.x), Vector3.down, Vector3.forward,
                        new Vector3(c.x, hi.y + 1f, lo.z - 1f), new Vector3(c.x, hi.y + 1f, hi.z + 1f), new Vector3(c.x, lo.y - 1f, lo.z - 1f), new Vector3(c.x, lo.y - 1f, hi.z + 1f));
                    _detector.Evaluate(new[] { upright }, new[] { second });
                    secondHits = _detector.HitCount;
                }

                Log("hull mid-drop re-cut: the first cut " + (published ? "published" : "NOT published within 10 s") + " (seen at frame " + seenAt + ", drops running " + h.AnimationsRunning + ")"
                    + (published && running ? "; Slash " + second + " (upright x " + c.x.ToString("F3", Inv) + ") given at frame " + Time.frameCount + ": detector hits " + secondHits : ": no second Slash given"));
                by = Time.realtimeSinceStartup + 30f;
                bool settled = false;
                while (Time.realtimeSinceStartup < by)
                {
                    yield return null;
                    bool all = true;
                    for (int i = hitsBefore; i < h.Hits.Count; i++) all &= !h.Hits[i].IsPending;
                    if (all && h.IsSettled) { settled = true; break; }
                }

                int publishedHits = 0, committedHits = 0;
                for (int i = hitsBefore; i < h.Hits.Count; i++)
                {
                    BuildingHullFusion.HullHit hit = h.Hits[i];
                    bool all = hit.outcome == "Published";
                    foreach (CutOperationId op in hit.displayOperations)
                    {
                        all &= _world.Ledger.TryGetOperation(op, out LogicalCutOperation record) && record.state == LogicalCutOperationState.Completed && _world.Geometry.StageOf(op) == CutGeometryStage.Committed;
                    }

                    if (hit.outcome == "Published") publishedHits++;
                    if (all && hit.displayOperations.Count > 0) committedHits++;
                    Log("hull mid-drop re-cut: hit " + hit.id + " slash " + hit.slashId + " => " + (hit.outcome ?? "PENDING") + " (display operations " + hit.displayOperations.Count + ", asked to published " + ((hit.publishedAt - hit.askedAt) * 1000).ToString("F1", Inv) + " ms, " + hit.slideRule + ")");
                }

                int stopped = h.AnimationsStoppedByReCut - stoppedBefore;
                Log("hull mid-drop re-cut: " + (settled ? "settled" : "NOT settled within 30 s: " + h.DescribeUnsettled()) + " at frame " + Time.frameCount + "; drops stopped by the re-cut " + stopped + " (the last at " + (h.LastStopFraction * 100).ToString("F1", Inv) + "% of its way); hits " + (h.Hits.Count - hitsBefore) + ", published " + publishedHits + ", with every display operation committed " + committedHits);
                Expect(published && running, "[hull mid-drop] the first Slash's cut was published and seen with its drop running (published " + published + ", drops running " + running + ")");
                Expect(stopped >= 1 && h.LastStopFraction > 0.0 && h.LastStopFraction < 1.0, "[hull mid-drop] the running drop was stopped part way by the re-cut, where it stood (" + stopped + " stopped, the last at " + (h.LastStopFraction * 100).ToString("F1", Inv) + "%)");
                Expect(settled && publishedHits == 2 && committedHits == 2, "[hull mid-drop] both cuts were published and their display operations committed (" + publishedHits + " published, " + committedHits + " committed)");
                _phase = "replay-end";
                _bhScenario = "end";
            }

            private string HitTally(BuildingHullFusion h, int from)
            {
                int published = 0, dropped = 0, notAccepted = 0, noChange = 0, other = 0, pending = 0;
                for (int i = from; i < h.Hits.Count; i++)
                {
                    string o = h.Hits[i].outcome;
                    if (o == null) pending++;
                    else if (o == "Published") published++;
                    else if (o.StartsWith("Dropped")) dropped++;
                    else if (o.StartsWith("NotAccepted")) notAccepted++;
                    else if (o.StartsWith("EmptySide") || o.StartsWith("NoChange")) noChange++;
                    else other++;
                }

                return "hits " + (h.Hits.Count - from) + ": published " + published + ", dropped " + dropped + ", not accepted " + notAccepted + ", empty side or no change " + noChange + ", other refusals " + other + ", pending " + pending;
            }

            private IEnumerator HullScenario()
            {
                BuildingHullFusion h = _world.Hulls;
                double recoverFrom = Time.realtimeSinceStartupAsDouble;
                Log("hull scenario clocks: the last ordinary Slash's wave flew at frame " + _bhLastWaveFrame + " real " + _bhLastWaveReal.ToString("F3", Inv) + "; the recovery wait begins at frame " + Time.frameCount + " real " + recoverFrom.ToString("F3", Inv)
                    + " (" + (recoverFrom - _bhLastWaveReal).ToString("F2", Inv) + " s after the last Slash: the cuts' completion wait, the observation and the hold came first)");
                Log("hull scenario: the continuous Slashes ended at frame " + Time.frameCount + " (" + HitTally(h, 0) + "; " + h.DescribeCounts() + "; most at once: groups " + h.MaxGroups + ", hulls " + h.MaxLiveHulls + ", bodies " + _bhMaxBodies + "; not achieved so far " + h.HullsNotAchieved + " for " + (h.NotAchievedSeconds + h.NotAchievedSecondsNow).ToString("F2", Inv) + " s; class outcomes: " + h.DescribeAggregationStates() + ")");
                _bhScenario = "recover";
                _phase = "hull-recover";
                double t0 = Time.realtimeSinceStartupAsDouble;
                float by = Time.realtimeSinceStartup + 10f;
                while (!(h.IsSettled && h.IsOneHullAchieved) && Time.realtimeSinceStartup < by) yield return null;
                bool recovered = h.IsSettled && h.IsOneHullAchieved;
                double recoverSeconds = Time.realtimeSinceStartupAsDouble - t0;
                double recoveredAt = Time.realtimeSinceStartupAsDouble;
                Log("hull scenario: recovery to one hull " + (recovered ? "in " + recoverSeconds.ToString("F2", Inv) + " s of the recovery wait" : "NOT within the 10 s recovery wait") + " (the wait ended at real " + recoveredAt.ToString("F3", Inv) + ", " + (recoveredAt - _bhLastWaveReal).ToString("F2", Inv) + " s after the last ordinary Slash): " + h.DescribeCounts() + "; " + h.DescribeUnsettled() + "; class outcomes: " + h.DescribeAggregationStates());
                Expect(recovered, "[hull scenario] after the continuous Slashes the building came back to at most one fixed and one free group, one hull each, within the 10 s recovery wait (" + recoverSeconds.ToString("F2", Inv) + " s of the wait; " + (recoveredAt - _bhLastWaveReal).ToString("F2", Inv) + " s after the last ordinary Slash, not within 10 s of it; " + h.DescribeCounts() + ")");
                _bhScenario = "hold";
                _phase = "hull-hold";
                string countsAtHold = h.DescribeCounts();
                int cutsAtHold = h.GroupCuts, hitsAtHold = h.Hits.Count;
                float holdBy = Time.realtimeSinceStartup + 3f;
                while (Time.realtimeSinceStartup < holdBy) yield return null;
                Log("hull scenario: held 3 s: " + h.DescribeCounts() + "; hits meanwhile " + (h.Hits.Count - hitsAtHold) + ", cuts " + (h.GroupCuts - cutsAtHold));
                Expect(h.DescribeCounts() == countsAtHold && h.GroupCuts == cutsAtHold, "[hull scenario] the counts held for 3 s (" + countsAtHold + " -> " + h.DescribeCounts() + ")");
                // The re-cut: synthetic Slashes through the current hulls (the fused one, or the groups standing apart), through the ordinary detection path.
                _bhScenario = "recut";
                _phase = "hull-recut";
                yield return HullRecut(h);
                _bhScenario = "recover2";
                _phase = "hull-recover2";
                t0 = Time.realtimeSinceStartupAsDouble;
                by = Time.realtimeSinceStartup + 10f;
                while (!(h.IsSettled && h.IsOneHullAchieved) && Time.realtimeSinceStartup < by) yield return null;
                bool recovered2 = h.IsSettled && h.IsOneHullAchieved;
                double recover2Seconds = Time.realtimeSinceStartupAsDouble - t0;
                Log("hull scenario: recovery after the re-cut " + (recovered2 ? "in " + recover2Seconds.ToString("F2", Inv) + " s" : "NOT within 10 s") + " (real " + Time.realtimeSinceStartupAsDouble.ToString("F3", Inv) + "): " + h.DescribeCounts() + "; " + h.DescribeUnsettled() + "; class outcomes: " + h.DescribeAggregationStates());
                Expect(recovered2, "[hull scenario] after the re-cut the building came back to one hull within 10 s (" + recover2Seconds.ToString("F2", Inv) + " s; " + h.DescribeCounts() + ")");
                Log("hull scenario: whole run " + HitTally(h, 0) + "; not achieved " + h.HullsNotAchieved + " (classes standing apart for " + (h.NotAchievedSeconds + h.NotAchievedSecondsNow).ToString("F2", Inv) + " s in all, longest " + h.MaxNotAchievedSeconds.ToString("F2", Inv) + " s, a standing one included; every group cuttable meanwhile); most at once: groups " + h.MaxGroups + ", hulls " + h.MaxLiveHulls + ", bodies " + _bhMaxBodies + ", colliders " + _bhMaxColliders);
                _bhScenario = "end";
                _phase = "hull-end";
            }

            private void BuildingHullSummarise()
            {
                BuildingHullFusion h = _world != null ? _world.Hulls : null;
                if (h == null) { Log("building hull: off (the world's profile)"); return; }
                HullLimitSummary();
                _bhRows?.Flush();
                BuildingRest rest = _world.Rest;
                Log("building hull: groups " + h.GroupCount + " (made " + h.GroupsMade + ", most at once " + h.MaxGroups + "), cuts " + h.GroupCuts + " (failed " + h.CutsFailed + ", PhysX refusals " + h.HullRefusals + ", no change " + h.NoChanges + "), cook requests " + h.CookRequests + ", hull checks " + h.HullChecks
                    + ", meshes baked " + h.MeshesBaked + " (fusion bakes " + h.FusionBakes + "), display members scanned " + h.DisplayMembersScanned + " (side reads " + h.SideLookups + ", without geometry " + h.MembersWithoutGeometry + "), display cuts " + h.DisplayCuts + " (refused " + h.DisplayCutsRefused + ", open " + h.DisplayOperationsOpen + ", failed " + h.DisplayOperationsFailed + "), room waits " + h.RoomWaits
                    + "; hits " + h.Hits.Count + ": published " + h.HitsPublished + ", refused/dropped/no change " + h.HitsRefused + ", pending " + h.HitsPending + ", held " + h.HitsHeld + " resumed " + h.HitsResumed + ", duplicates refused at the acceptance " + h.HitsDuplicate + ", aggregation re-evaluations " + h.Reevaluations
                    + "; groups held/released by the rest " + h.GroupsHeld + "/" + h.GroupsReleased + ", unions " + h.Unions + " (deadline " + h.UnionsByDeadline + ", next Slash " + h.UnionsByNextSlash + "), fusions " + h.Fusions + " (begun " + h.FusionsBegun + ", refused " + h.FusionsRefused + ", retried " + h.FusionsRetried + ", given up " + h.FusionsGivenUp + ", stale " + h.FusionsStale + ", PhysX " + h.FusionHullRefusals + ", offers refused " + h.OffersRefused + ", reoffers " + h.Reoffers
                    + "), one hull not achieved " + h.HullsNotAchieved + " (rejected new penetration max " + h.MaxRejectedNewPenetration.ToString("F3", Inv) + " m), candidates dropped (participants changed) " + h.FusionsStale + ", hull exchanges " + h.HullExchanges + ", colliders made " + h.CollidersMade + " live " + h.LiveColliders + ", bodies made/destroyed " + h.BodiesMade + "/" + h.BodiesDestroyed + ", most vertices " + h.MaxVertices + " faces " + h.MaxFaces + ", most hulls at once " + h.MaxLiveHulls
                    + "; the building's real bodies most at once " + _bhMaxBodies + ", its enabled colliders most at once " + _bhMaxColliders);
                Log("building hull not achieved: candidates " + h.HullsNotAchieved + ", classes standing apart for " + (h.NotAchievedSeconds + h.NotAchievedSecondsNow).ToString("F2", Inv) + " s in all (longest " + h.MaxNotAchievedSeconds.ToString("F2", Inv) + " s, a standing one included; standing now " + h.NotAchievedSecondsNow.ToString("F2", Inv) + " s); every group cuttable meanwhile; class outcomes at the end: " + h.DescribeAggregationStates());
                foreach (string n in h.NotAchieved) Log("building hull not achieved: " + n);
                {
                    int held = 0, heldPending = 0, heldPublished = 0, heldDropped = 0, heldOther = 0;
                    var waits = new List<double>();
                    foreach (BuildingHullFusion.HullHit hit in h.Hits)
                    {
                        if (!hit.held) continue;
                        held++;
                        if (hit.IsPending) { heldPending++; continue; }
                        waits.Add((hit.answeredAt - hit.askedAt) * 1000.0);
                        if (hit.outcome == "Published") heldPublished++; else if (hit.outcome.StartsWith("Dropped")) heldDropped++; else heldOther++;
                    }

                    Log("building hull held hits: " + held + " (published " + heldPublished + ", dropped " + heldDropped + ", other ends " + heldOther + ", still pending " + heldPending + "); held to answered ms " + Stat(waits)
                        + "; duplicates refused at the acceptance " + h.HitsDuplicate + "; candidates made for a class already judged apart " + h.CandidatesForJudgedClass + "; candidates dropped (participants changed) " + h.FusionsStale + "; display members most at once " + _bhMaxMembers);
                    Expect(heldPending == 0 && h.CandidatesForJudgedClass == 0, "[hull] every held hit reached its end (" + heldPending + " pending) and no class judged apart was tried again (" + h.CandidatesForJudgedClass + ")");
                }
                foreach (string r in h.PenetrationRecords) Log("building hull exchange: " + r);
                {
                    var stopToFusion = new List<double>();
                    foreach (double v in h.StopToFusionSeconds) stopToFusion.Add(v * 1000.0);
                    int anchoredGroups = 0, stagedGroups = 0;
                    foreach (HullGroup g in h.Groups) { if (g.Anchored) anchoredGroups++; if (g.Staged) stagedGroups++; }
                    Log("building hull staged stop: pairs " + h.PairsMade + " (most moving at once " + _bhMaxPairs + "), constraints made " + h.ConstraintsMade + " (most live at once " + _bhMaxConstraints + ", live at the end " + h.ConstraintsLive + "), stops by the motion time " + h.StopsByDeadline + ", by the next Slash " + h.StopsByNextSlash + ", other " + h.StopsOther
                        + ", groups fixed for show " + h.GroupsStaged + "; at a stop: opening min " + h.MinOpening.ToString("F4", Inv) + " max " + h.MaxOpening.ToString("F4", Inv) + " m, along the plane max " + h.MaxInPlane.ToString("F4", Inv) + " m, relative turn max " + h.MaxRelativeRotationDegrees.ToString("F2", Inv) + " deg, largest move " + h.MaxFall.ToString("F3", Inv) + " m, fastest " + h.MaxPairSpeed.ToString("F2", Inv) + " m/s, stop at " + h.MaxStageRealSeconds.ToString("F3", Inv) + " s real at most"
                        + "; from the last stop to the fusion ms " + Stat(stopToFusion) + " (" + stopToFusion.Count + " fusions after a stop)");
                    Log("building hull anchored counts, apart: the model's anchored groups " + anchoredGroups + ", groups fixed for show now " + stagedGroups + "; the rest's ground-tracked bodies " + rest.TrackedAnchored + " (of which pinned for show over the run " + rest.PinnedGroups + ")");
                    foreach (string r in h.StageRecords) Log("building hull stop: " + r);
                }

                Log("building hull fastest free group: " + h.MaxGroupSpeed.ToString("F2", Inv) + " m/s at " + (h.MaxGroupSpeedAt ?? "none") + "; the check's own per-frame maximum " + _bhMaxSpeed.ToString("F2", Inv) + " m/s at frame " + _bhMaxSpeedFrame);
                Log("building hull Main (ms total): physics side " + (h.MainPhysicsSeconds * 1000).ToString("F2", Inv) + " (hits " + (h.HitSeconds * 1000).ToString("F3", Inv) + ", preparation " + ((h.PrepareSeconds - h.PrepareDisplaySeconds) * 1000).ToString("F2", Inv) + ", publish physics " + (h.PublishPhysicsSeconds * 1000).ToString("F2", Inv)
                    + ", unions " + (h.UniteSeconds * 1000).ToString("F2", Inv) + ", mesh preparation " + (h.MeshPrepareSeconds * 1000).ToString("F2", Inv) + ", hull exchange " + (h.HullExchangeSeconds * 1000).ToString("F2", Inv) + " of which penetration measurement " + (h.PenetrationSeconds * 1000).ToString("F2", Inv) + " (longest " + (h.MaxPenetrationSeconds * 1000).ToString("F3", Inv) + ")), display side " + (h.MainDisplaySeconds * 1000).ToString("F2", Inv)
                    + " (member scans " + (h.PrepareDisplaySeconds * 1000).ToString("F2", Inv) + ", publish display " + (h.PublishDisplaySeconds * 1000).ToString("F2", Inv) + "); worker apart: scan " + (h.FusionWorkerSeconds * 1000).ToString("F2", Inv) + ", bake " + (h.FusionBakeSeconds * 1000).ToString("F2", Inv)
                    + "; publish first " + (h.FirstPublishSeconds * 1000).ToString("F3", Inv) + " max after " + (h.MaxPublishSecondsAfterFirst * 1000).ToString("F3", Inv) + "; Step total " + (h.StepSeconds * 1000).ToString("F2", Inv) + " max " + (h.MaxStepSeconds * 1000).ToString("F3", Inv) + " (over budget " + h.OverrunFrames + ", deferred " + h.Deferred + "), ask to publish max " + (h.MaxAskToPublishSeconds * 1000).ToString("F1", Inv)
                    + "; rest (ms total) contact " + (rest.ContactSeconds * 1000).ToString("F2", Inv) + " support " + (rest.SupportSeconds * 1000).ToString("F2", Inv) + " rest " + (rest.SleepSeconds * 1000).ToString("F2", Inv) + " release " + (rest.ReleaseSeconds * 1000).ToString("F2", Inv) + " over " + rest.Steps + " turns");
                foreach (string o in h.OverrunUnits) Log("building hull overrun: " + o);
                Log("building hull frame costs (ms, per frame: hull Step + request entry + rest): first frame with hull work " + _bhFirstWorkFrame + " sum " + _bhFirstSum.ToString("F3", Inv) + " (step " + _bhFirstStep.ToString("F3", Inv) + ", hit " + _bhFirstHit.ToString("F3", Inv) + ", rest " + _bhFirstRest.ToString("F3", Inv)
                    + "); after it: sum " + Stat(_bhSumsAfterFirst) + "; all frames: sum " + Stat(_bhSums) + ", step " + Stat(_bhStepMs) + ", rest " + Stat(_bhRestMs) + ", hit " + Stat(_bhHitMs)
                    + "; FrameTiming's main thread time minus its present wait (a difference of two counters read at this point, negative in " + _bhMainWork.Count(v => v < 0.0) + " frames: not a Main work time) " + Stat(_bhMainWork)
                    + "; frames stepped " + _bhStepped + " skipped " + _bhSkipped + " longest skipped run " + _bhMaxSkips);
                Log("building hull shape: filled volume " + h.OverhangVolume.ToString("F3", Inv) + " m3 (max " + h.MaxOverhangVolume.ToString("F3", Inv) + "), envelope expansion max " + h.MaxFusionExpansion.ToString("E2", Inv) + " m (shrink max " + h.MaxFusionShrink.ToString("E2", Inv) + "), penetration before an exchange max " + h.MaxPenetrationBeforeExchange.ToString("F4", Inv) + " m after " + h.MaxFusionPenetration.ToString("F4", Inv) + " m added " + h.MaxNewPenetration.ToString("F4", Inv) + " m");
                foreach (HullGroup g in h.Groups) Log("building hull group " + g.Id + " (building " + g.Building + ", generation " + g.Generation + "): " + g.State + ", " + (g.Anchored ? "anchored" : g.Kinematic ? "held" : "free") + ", hulls " + g.HullCount + ", vertices " + g.VertexCount + ", members " + g.MemberCount + ", mass " + g.Mass.ToString("F1", Inv) + (rest.TryDescribeGroup(g.Body, out string state) ? "; rest: " + state : ""));
                foreach (BuildingHullFusion.HullHit hit in h.Hits) Log("building hull hit " + hit.id + ": t " + hit.physicsSeconds.ToString("F3", Inv) + " slash " + hit.slashId + " group " + hit.group + " g" + hit.generation + " => " + (hit.outcome ?? "PENDING") + (hit.displayOperations.Count > 0 ? " (display operations " + hit.displayOperations.Count + ")" : ""));
                foreach (string f in h.Failures) Log("building hull failure: " + f);
                foreach (string e in h.Events) Log("  " + e);

                // The display operations of the published hits: each Completed in the ledger, its live children with a committed geometry frame.
                int operations = 0, completed = 0, notCompleted = 0, notCommitted = 0, publishedHits = 0, chained = 0;
                foreach (BuildingHullFusion.HullHit hit in h.Hits)
                {
                    if (hit.outcome != "Published") continue;
                    publishedHits++;
                    bool all = true;
                    foreach (CutOperationId op in hit.displayOperations)
                    {
                        operations++;
                        bool known = _world.Ledger.TryGetOperation(op, out LogicalCutOperation record);
                        if (!known || record.state != LogicalCutOperationState.Completed) { notCompleted++; all = false; continue; }
                        completed++;
                        foreach (LogicalFragmentId child in new[] { record.positive, record.negative })
                        {
                            if (_world.Ledger.IsCurrentTarget(child) && _world.Geometry != null && !_world.Geometry.TryGetGeometryFrame(child, out _) && !_world.Geometry.HasNoGeometry(child)) { notCommitted++; all = false; }
                        }
                    }

                    if (all && hit.displayOperations.Count > 0) chained++;
                }

                Log("building hull chains: published hits " + publishedHits + " (display operations " + operations + ": Completed " + completed + ", not Completed " + notCompleted + ", a live child without a committed frame " + notCommitted + "; hits whose operations all reached the end " + chained + ")");
                CountBuildingPhysics(h, out int sceneColliders, out int sceneBodies, out int othersAtEnd);
                int hulls = 0;
                foreach (HullGroup g in h.Groups) hulls += g.HullCount;
                Log("building hull physics at the end: groups " + h.GroupCount + ", hulls " + hulls + ", the building's rigidbodies " + sceneBodies + ", its enabled colliders " + sceneColliders + " (the trial's own count " + h.LiveColliders + "); other enabled mesh colliders in the scene " + othersAtEnd + "; all groups at rest " + h.AllGroupsAtRest);

                // The unit's conditions (TL, 2026-09-30). A refusal, a drop or a no-change is not a cut.
                Expect(h.HitsPublished > 0 && h.GroupCuts == h.HitsPublished && h.CutsFailed == 0, "[hull] the building was cut from Slashes through the ordinary path: " + h.HitsPublished + " hits published (" + h.HitsRefused + " refused, dropped or no change: not cuts)");
                if (h.Settings.kinematicDisplay)
                {
                    h.CountDisplay(out int held, out int committed, out int emptyMembers, out int pendingMembers, out int ledgerFragments, out int ledgerOperations);
                    int refusedOrKept = h.HullUpdatesRefused + h.HullUpdatesStale + h.HullUpdatesCancelled;
                    Log("building hull always kinematic: drops started " + h.AnimationsStarted + ", completed " + h.AnimationsCompleted + ", stopped by a re-cut " + h.AnimationsStoppedByReCut + ", running " + h.AnimationsRunning
                        + "; hull updates begun " + h.HullUpdatesBegun + ", exchanged " + h.HullUpdatesAdopted + ", refused " + h.HullUpdatesRefused + ", stale " + h.HullUpdatesStale + ", cancelled " + h.HullUpdatesCancelled + "; requests made " + h.HullRequestsMade + ", skipped " + h.HullRequestsSkipped + ", dropped (refused) " + h.HullRequestsDropped + ", waiting " + h.WaitingHullRequests + " (most running at once " + h.MaxHullUpdatesInFlight + ", most waiting " + h.MaxWaitingHullRequests + ")"
                        + "; hull Main " + (h.HullUpdateMainSeconds * 1000).ToString("F2", Inv) + " ms, worker " + (h.HullUpdateWorkerSeconds * 1000).ToString("F2", Inv) + " ms; frames with a building body not kinematic " + _bhNonKinematicFrames + "; frames not exactly one body, enabled collider and hull a building " + _bhFramesNotOneEach + (_bhFirstNotOneEach >= 0 ? " (first at frame " + _bhFirstNotOneEach + ": " + _bhNotOneEachWhy + ")" : ""));
                    Log("building hull display (a separate problem, not solved by one body and one hull): members held " + held + " (most " + _bhMaxMembers + "): committed " + committed + " (most " + _bhMaxWithGeometry + "), known empty " + emptyMembers + ", not yet committed " + pendingMembers + ";, the ledger's history " + ledgerFragments + " fragments / " + ledgerOperations + " operations, display cuts " + h.DisplayCuts
                        + "; ms total: classification " + (h.PrepareDisplaySeconds * 1000).ToString("F2", Inv) + ", publication " + (h.PublishDisplaySeconds * 1000).ToString("F2", Inv) + ", drops' placement " + (h.AnimationSeconds * 1000).ToString("F2", Inv) + " (the snapshot per frame in frames.csv)");
                    foreach (string r in h.HullUpdateRecords) Log("building hull update: " + r);
                    if (_world.Display != null)
                    {
                        VpLogicalCutDisplay d = _world.Display;
                        Log("display GPU copy at the end: " + DescribeGpuCopy(d));
                        Log("display reflected sets (the whole display, every kind): made " + d.ReflectedSetsMade + " (" + d.ReflectedSetEntries + " boundaries, " + (d.ReflectedSetSeconds * 1000).ToString("F2", Inv) + " ms from Of/With's entry, the array included), held now "
                            + d.ReflectedEntriesHeld + " boundaries; commits " + d.CommitCalls + " (" + (d.CommitSeconds * 1000).ToString("F2", Inv) + " ms in all, longest " + (d.MaxCommitSeconds * 1000).ToString("F3", Inv) + " ms)");
                        // Every commit attempt (commits.csv), the heaviest ones and the heaviest frames by their commits' sum (TL, 2026-10-01).
                        string KindOfSource(int source) { try { return KindOf(LineageOf(new LogicalFragmentId(source))); } catch { return "unknown"; } }
                        string Ms(double seconds) => (seconds * 1000).ToString("F3", Inv);
                        using (var csv = new StreamWriter(Path.Combine(directory, "commits.csv")))
                        {
                            csv.WriteLine("frame,operation,source,kind,shown,outcome,searchMs,prepareMs,roomMs,gpuCapacityMs,transferMs,setsMs,registerMs,totalMs");
                            foreach (VpLogicalCutDisplay.CommitRecord r in d.CommitRecords)
                                csv.WriteLine(string.Join(",", r.frame, r.operation, r.source, CsvField(KindOfSource(r.source)), r.shown, CsvField(r.outcome), Ms(r.search), Ms(r.prepare), Ms(r.room), Ms(r.growth), Ms(r.transfer), Ms(r.sets), Ms(r.register), Ms(r.total)));
                        }

                        foreach (VpLogicalCutDisplay.CommitRecord r in d.CommitRecords.OrderByDescending(r => r.total).Take(5))
                            Log("display commit heavy: frame " + r.frame + " operation " + r.operation + " source " + r.source + " (" + KindOfSource(r.source) + ", " + r.shown + " shown) => " + r.outcome + "; ms " + Ms(r.total) + " = search " + Ms(r.search) + " + prepare " + Ms(r.prepare)
                                + " + room " + Ms(r.room) + " + GPU capacity " + Ms(r.growth) + " + transfer " + Ms(r.transfer) + " + sets " + Ms(r.sets) + " + register " + Ms(r.register));
                        foreach (var frameSum in d.CommitRecords.GroupBy(r => r.frame).Select(g => (frame: g.Key, count: g.Count(), sum: g.Sum(r => r.total), max: g.Max(r => r.total))).OrderByDescending(x => x.sum).Take(5))
                            Log("display commit heavy frame: frame " + frameSum.frame + ": " + frameSum.count + " commits, " + Ms(frameSum.sum) + " ms in all (the longest one " + Ms(frameSum.max) + " ms)");
                    }
                    // Per hit (TL, 2026-09-30): the members crossed, the children left empty, the slide given (computed: its move along the normal).
                    int kHits = 0, kCrossed = 0, kMaxCrossed = 0;
                    var kAll = new BuildingHullFusion.ChildCounts();
                    double kMaxComputedAlong = 0;
                    foreach (BuildingHullFusion.HullHit hit in h.Hits)
                    {
                        if (hit.outcome != "Published") continue;
                        BuildingHullFusion.ChildCounts c = h.CountChildren(hit);
                        double along = Math.Abs(Unity.Mathematics.math.dot(hit.slideWorld, hit.normalWorld));
                        kHits++; kCrossed += hit.displayOperations.Count; kMaxCrossed = Math.Max(kMaxCrossed, hit.displayOperations.Count); kMaxComputedAlong = Math.Max(kMaxComputedAlong, along);
                        kAll.empty += c.empty; kAll.withShape += c.withShape; kAll.cutAgain += c.cutAgain; kAll.retired += c.retired; kAll.pending += c.pending; kAll.mismatch += c.mismatch; kAll.unknown += c.unknown;
                        Log("building hull slide hit " + hit.id + ": t " + hit.physicsSeconds.ToString("F3", Inv) + ", members classified " + hit.membersClassified + " (by the extent alone " + hit.membersByExtent + ", read by their indices " + hit.membersScanned + "), crossed " + hit.displayOperations.Count + ", " + DescribeChildren(c) + ", held after " + hit.membersHeldAfter + "; publication " + hit.publishMs.ToString("F3", Inv) + " ms; asked to published " + ((hit.publishedAt - hit.askedAt) * 1000).ToString("F1", Inv) + " ms, the preparation in " + hit.prepareUnits + " units over frames " + hit.prepareFirstFrame + ".." + hit.prepareLastFrame + "; normal " + ((Vector3)hit.normalWorld).ToString("F3") + ", slide " + ((Vector3)hit.slideWorld).ToString("F4") + " (" + hit.slideRule + "), its dot with the normal " + along.ToString("E2", Inv));
                    }
                    Log("building hull slides and classification: published hits " + kHits + ", members crossed " + kCrossed + " (" + (kHits > 0 ? (kCrossed / (double)kHits).ToString("F2", Inv) : "-") + " a hit, most " + kMaxCrossed + "), their " + DescribeChildren(kAll)
                        + "; members classified " + h.DisplayMembersScanned + " (decided by the extent alone " + h.DisplayMembersByExtent + ", read by their indices " + h.DisplayMembersReadByIndices + ", extent not recorded " + h.DisplayExtentsUnavailable
                        + "), indices actually read " + h.DisplayIndicesRead + ", vertices tested " + h.DisplayVertexTests + ", the index ranges given to the scans " + h.DisplayIndexRangeLengths + " long in all, index leases refused " + h.DisplayIndexLeasesRefused
                        + "; the slides d given: most |dot(d, n)| " + kMaxComputedAlong.ToString("E2", Inv) + " m (d in the plane; the arc of gravity is laid over it, read back below)");
                    foreach (BuildingHullFusion.HullHit hit in h.Hits) if (hit.hullOutcome != null) Log("building hull hit " + hit.id + " hull: " + hit.hullOutcome);
                    // The wait from a hit to its publication, the script's hits apart from the synthetic ones (TL, 2026-10-01).
                    foreach (bool synthetic in new[] { false, true })
                    {
                        var waits = new List<double>(); var units = new List<double>(); var frames = new List<double>();
                        foreach (BuildingHullFusion.HullHit hit in h.Hits)
                        {
                            if (hit.outcome != "Published" || (hit.slashId >= 900000) != synthetic) continue;
                            waits.Add((hit.publishedAt - hit.askedAt) * 1000.0); units.Add(hit.prepareUnits); frames.Add(hit.prepareLastFrame - hit.prepareFirstFrame + 1);
                        }

                        Log("building hull asked to published (" + (synthetic ? "the synthetic Slashes after the script" : "the script's hits") + "): " + waits.Count + " hits, ms " + Stat(waits) + "; preparation units " + Stat(units) + ", frames spanned " + Stat(frames));
                    }

                    Expect(_bhNonKinematicFrames == 0, "[hull kinematic] every building body stayed kinematic at every frame and none was the rest's (" + _bhNonKinematicFrames + " frames with one not)");
                    Expect(_bhFramesNotOneEach == 0, "[hull kinematic] exactly one body, one enabled collider and one hull a building at every frame (" + _bhFramesNotOneEach + " frames otherwise" + (_bhFirstNotOneEach >= 0 ? ", the first at frame " + _bhFirstNotOneEach + ": " + _bhNotOneEachWhy : "") + ")");
                    Expect(h.AnimationsRunning == 0 && h.AnimationsCompleted + h.AnimationsStoppedByReCut == h.AnimationsStarted && h.AnimationsStarted > 0, "[hull kinematic] every drop completed or was stopped by a re-cut (" + h.AnimationsStarted + " started)");
                    Expect(h.GroupCuts >= 2, "[hull kinematic] the building was cut again after a cut (" + h.GroupCuts + " display cuts published)");
                    Log("building hull re-cuts during a drop: " + h.AnimationsStoppedByReCut + " drops stopped where they were by a re-cut (" + (h.AnimationsStoppedByReCut > 0 ? "exercised" : "not exercised in this run") + ")");
                    Expect(kAll.mismatch == 0 && kAll.unknown == 0, "[hull kinematic] no display child of a Completed operation is without its geometry or known empty, and every one is in the ledger (" + DescribeChildren(kAll) + ")");
                    Expect(kHits > 0 && kMaxComputedAlong < 1e-4, "[hull kinematic] each slide d given lay in its cut plane (most |dot(d, n)| " + kMaxComputedAlong.ToString("E2", Inv) + " m)");
                    HullDropsSummary(h);   // the path read back: on the curve part way, x0 + d at completion, the stop's point kept, no rotation
                    Expect(h.WaitingHullRequests == 0 && h.HullUpdatesInFlight == 0 && h.MaxHullUpdatesInFlight <= h.GroupsMade && h.MaxWaitingHullRequests <= h.GroupsMade && h.HullUpdatesAdopted + refusedOrKept >= 1, "[hull kinematic] the hull followed best-effort and nothing is left waiting (exchanged " + h.HullUpdatesAdopted + ", kept the old hull " + refusedOrKept + " times while the display went on)");
                }
                else
                {
                Expect(h.GroupsHeld > 0, "[hull] a free side was held by the rest (" + h.GroupsHeld + " holds, " + h.GroupsReleased + " releases)");
                Expect(h.UnionsByDeadline > 0 && h.UnionsByNextSlash > 0, "[hull] the sides were aggregated both by the deadline (" + h.UnionsByDeadline + ") and by the next Slash (" + h.UnionsByNextSlash + ")");
                Expect(h.AggregationsEnded == h.AggregationsBegun && h.AggregationsBegun > 0 && h.FusionsGivenUp == 0 && h.FusionsInFlight == 0 && !h.HasGivenUpFusions && h.Failures.Count == 0, "[hull] every aggregation ended adopted (fused into one hull), recorded as one hull not achieved, or dropped for its participants changing; none given up (aggregations " + h.AggregationsBegun + ": fused " + h.Fusions + " (" + h.Unions + " merges), not achieved " + h.HullsNotAchieved + ", dropped " + h.FusionsStale + ", given up " + h.FusionsGivenUp + ", failures " + h.Failures.Count + ")");
                }
                Expect(h.HitsPending == 0 && h.Hits.Count == h.HitsPublished + h.HitsRefused && h.HeldNow == 0 && h.CutsInProgress == 0, "[hull] every hit has its one outcome (" + h.Hits.Count + ": published " + h.HitsPublished + ", refused " + h.HitsRefused + ", pending " + h.HitsPending + ")");
                Expect(h.DisplayOperationsOpen == 0 && h.DisplayOperationsFailed == 0 && notCompleted == 0 && notCommitted == 0 && chained == publishedHits, "[hull] every published hit's display operations reached the ledger's end with their geometry committed (" + chained + " of " + publishedHits + ")");
                Expect(h.IsSettledWithoutFailure, "[hull] the trial is settled without failure: " + h.DescribeUnsettled());
                // At most one fixed and one free group a building (2026-10-03: per building, for a city of many; the same for one).
                int kinematicGroups = h.Groups.Count(g => g.Kinematic), freeGroups = h.Groups.Count(g => !g.Kinematic);
                int buildingsPastOne = BuildingsPastOneEach(h.Groups.Select(g => (g.Building, g.Kinematic)));
                Expect(h.IsOneHullAchieved && h.GroupCount >= 1 && buildingsPastOne == 0 && hulls == h.GroupCount && sceneBodies == h.GroupCount && sceneColliders == h.LiveColliders && h.LiveColliders == hulls,
                    "[hull] one hull achieved on the real counts: at the end at most one fixed and one free group a building, each one hull on one real body with one collider (" + h.DescribeCounts() + "; fixed groups " + kinematicGroups + ", free " + freeGroups + ", buildings past one of either " + buildingsPastOne + "; the buildings' bodies " + sceneBodies + ", colliders " + sceneColliders + "; not achieved " + h.HullsNotAchieved + ", given up " + h.FusionsGivenUp + ")");
                Expect(h.MaxGroupSpeed < 20f && _bhMaxSpeed < 20f, "[hull] no group moved faster than 20 m/s after a cut or an exchange (fastest " + h.MaxGroupSpeed.ToString("F2", Inv) + " m/s at " + (h.MaxGroupSpeedAt ?? "none") + ")");
                Expect(_bhMaxSkips < 2 * CutPhysicsStep.VerifyAfterSkippedFrames, "[hull] the physics never stopped: the longest run of skipped frames " + _bhMaxSkips + " is under " + 2 * CutPhysicsStep.VerifyAfterSkippedFrames);
            }
        }
    }
}
