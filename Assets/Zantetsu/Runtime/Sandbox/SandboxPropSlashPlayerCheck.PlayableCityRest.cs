using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // Diagnosis (the building rest trial in the playable city, 2026-09-29): what the trial did to the building's pieces
    // over the run and what it left at the end. Only reads; nothing of the product is changed. Nothing is written when
    // the world's profile leaves the trial off.
    //   * playable-rest.csv, one row a frame: the pieces tracked and held, the re-cuts and what they did, the wakes for
    //     lost support, the grounds found lost, the pieces past the timeout without support, the Main times, the
    //     simulate time.
    //   * Each re-cut: its candidates and what became of them; then, within one second after it, the largest speed of
    //     the pieces it returned to dynamic, of the new children, and of the other building pieces, the lowest centre
    //     and the pieces below the floor.
    //   * At the end (or at the Player's quitting, when the world ended early): the counters, every building piece
    //     live classified in the trial's terms, whether any anchored or prop piece was held by the trial, the record.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private sealed class ReleaseWatch
            {
                public int number, frame, candidates, released, slept, woken, below;
                public double at;
                public float maxReleased, maxChildren, maxOthers, minY = float.PositiveInfinity;
                public string speediest = "";
                public readonly HashSet<LogicalFragmentId> releasedPieces = new HashSet<LogicalFragmentId>();
            }

            private StreamWriter _pcRest;
            private BuildingRest _pcRestRef;   // kept so that the summary can be written after the world ended
            private BuildingFusion _pcFusionRef;
            private long _pcLastStepId = -1;
            private int _pcSkipsInARow, _pcMaxSkipsInARow, _pcSkippedFrames, _pcSteppedFrames;
            private double _pcMinRemaining = double.PositiveInfinity;
            private int _pcRestReCuts, _pcRestLastCandidates, _pcRestLastReleased, _pcRestLastSlept, _pcRestLastWoken;
            private double _pcSimulateMax;
            private bool _pcRestSummarised;
            private readonly List<ReleaseWatch> _pcReleaseWatches = new List<ReleaseWatch>();

            private void PlayableRestFrame(int frame, float floorTop)
            {
                BuildingRest rest = _world != null ? _world.Rest : null;
                if (rest == null || !rest.Enabled) return;
                _pcRestRef = rest;
                if (_pcRest == null)
                {
                    _pcRest = DetailWriter("playable-rest.csv");
                    _pcRest.WriteLine("frame,t,trackedDynamic,trackedAnchored,held,asleepByRest,reCuts,reCutCandidates,releasedPieces,sleptOnRelease,wokenOnRelease,wokenBySupportLoss,lostGrounds,autoWakes,unsupportedPastTimeout,heldWithoutSupport,maxReleaseMs,maxSupportMs,maxRestMs,simulateMs,stepped,skipsInARow,remainingMs,expectedMs,groups,fusedPieces,groupCuts,finals,rigidbodies");
                }

                double t = CutPhysicsStep.Clock.PhysicsSeconds;
                double simulate = CutPhysicsStep.LastSimulateSeconds;
                _pcSimulateMax = Math.Max(_pcSimulateMax, simulate);
                // The step decision of the frame before (this runs in LateUpdate before the frame's step): stepped or skipped,
                // and the remaining Main budget against the expected simulate cost it was decided with.
                long stepId = CutPhysicsStep.Clock.StepId;
                bool stepped = _pcLastStepId >= 0 && stepId != _pcLastStepId;
                if (_pcLastStepId >= 0) { if (stepped) { _pcSteppedFrames++; _pcSkipsInARow = 0; } else { _pcSkippedFrames++; _pcSkipsInARow++; _pcMaxSkipsInARow = Math.Max(_pcMaxSkipsInARow, _pcSkipsInARow); } }
                _pcLastStepId = stepId;
                double remaining = CutPhysicsStep.LastDecisionRemainingSeconds, expected = CutPhysicsStep.LastDecisionExpectedSeconds;
                _pcMinRemaining = Math.Min(_pcMinRemaining, remaining);
                BuildingFusion fusion = _world.Fusion;
                if (fusion != null) _pcFusionRef = fusion;
                PlayableBodiesFrame(frame, simulate, _pcSkipsInARow, remaining, expected);
                _pcRest.WriteLine(string.Join(",", frame, t.ToString("F3", Inv), rest.TrackedDynamic, rest.TrackedAnchored, rest.RestedNow, rest.AsleepNow, rest.ReCuts, rest.ReCutCandidates, rest.ReleasedPieces, rest.SleptOnRelease,
                    rest.WokenOnRelease, rest.WokenBySupportLoss, rest.LostGrounds, rest.AutoWakes, rest.UnsupportedPastTimeout, rest.AsleepWithoutSupport, (rest.MaxReleaseSeconds * 1000).ToString("F3", Inv),
                    (rest.MaxSupportSeconds * 1000).ToString("F3", Inv), (rest.MaxSleepSeconds * 1000).ToString("F3", Inv), (simulate * 1000).ToString("F3", Inv),
                    stepped ? 1 : 0, _pcSkipsInARow, (remaining * 1000).ToString("F3", Inv), (expected * 1000).ToString("F3", Inv),
                    fusion != null ? fusion.GroupCount : 0, fusion != null ? fusion.FusedPieces : 0, fusion != null ? fusion.GroupCuts : 0, fusion != null ? fusion.FinalsPublished : 0,
                    frame % 30 == 0 ? UnityEngine.Object.FindObjectsByType<Rigidbody>(FindObjectsInactive.Exclude, FindObjectsSortMode.None).Length : -1));
                if (rest.ReCuts > _pcRestReCuts)
                {
                    // One or more re-cuts this frame (their counters are summed; the last one's released pieces are known).
                    var w = new ReleaseWatch
                    {
                        number = rest.ReCuts, frame = frame, at = t, candidates = rest.ReCutCandidates - _pcRestLastCandidates, released = rest.ReleasedPieces - _pcRestLastReleased,
                        slept = rest.SleptOnRelease - _pcRestLastSlept, woken = rest.WokenOnRelease - _pcRestLastWoken,
                    };
                    foreach (LogicalFragmentId f in rest.LastReleased) w.releasedPieces.Add(f);
                    _pcReleaseWatches.Add(w);
                    Log("playable city rest re-cut " + w.number + " at frame " + frame + " (t " + t.ToString("F2", Inv) + "): candidates " + w.candidates + ", held to dynamic " + w.released + " (asleep " + w.slept + ", woken " + w.woken + "), held outside " + rest.RestedNow
                        + ", tracked " + rest.TrackedDynamic + "; " + (rest.ReCuts - _pcRestReCuts) + " re-cut(s) this frame; longest release so far " + (rest.MaxReleaseSeconds * 1000).ToString("F3", Inv) + " ms for " + rest.MaxReleasePieces + " candidates");
                    _pcRestReCuts = rest.ReCuts; _pcRestLastCandidates = rest.ReCutCandidates; _pcRestLastReleased = rest.ReleasedPieces; _pcRestLastSlept = rest.SleptOnRelease; _pcRestLastWoken = rest.WokenOnRelease;
                }

                if (_pcReleaseWatches.Count == 0) return;
                for (int i = _pcReleaseWatches.Count - 1; i >= 0; i--)
                {
                    ReleaseWatch w = _pcReleaseWatches[i];
                    foreach (KeyValuePair<LogicalFragmentId, PlayablePiece> p in _pcPieces)
                    {
                        if (p.Value.kind != "building" || !_world.Owners.TryGet(p.Key, out PhysicsFragmentOwner owner) || owner.Body == null || owner.IsWithdrawn || owner.Body.isKinematic) continue;
                        float speed = owner.Body.linearVelocity.magnitude;
                        Vector3 c = owner.Body.worldCenterOfMass;
                        if (w.releasedPieces.Contains(p.Key)) w.maxReleased = Mathf.Max(w.maxReleased, speed);
                        else if (p.Value.firstFrame >= w.frame) w.maxChildren = Mathf.Max(w.maxChildren, speed);
                        else w.maxOthers = Mathf.Max(w.maxOthers, speed);
                        if (speed > Mathf.Max(w.maxReleased, Mathf.Max(w.maxChildren, w.maxOthers)) - 1e-6f) w.speediest = "piece " + p.Key.value + (w.releasedPieces.Contains(p.Key) ? " (released)" : p.Value.firstFrame >= w.frame ? " (new)" : " (other)") + " at " + c.ToString("F2");
                        w.minY = Mathf.Min(w.minY, c.y);
                        if (c.y < floorTop - 0.5f) w.below++;
                    }

                    if (t - w.at < 1.0) continue;
                    Log("playable city rest re-cut " + w.number + " at frame " + w.frame + " (t " + w.at.ToString("F2", Inv) + "): within 1 s after it, max |v| of the released " + w.maxReleased.ToString("F2", Inv) + " m/s, of the new children " + w.maxChildren.ToString("F2", Inv)
                        + " m/s, of the other building pieces " + w.maxOthers.ToString("F2", Inv) + " m/s (" + w.speediest + "), lowest centre y " + w.minY.ToString("F2", Inv) + ", piece-frames below the floor " + w.below);
                    _pcReleaseWatches.RemoveAt(i);
                }
            }

            /// <summary>The summary, once: at the check's own end, or at the Player's quitting when the world ended early (then only the counters and the record).</summary>
            private void PlayableRestSummary()
            {
                if (_pcRestSummarised) return;
                _pcRestSummarised = true;
                try { PlayableBodiesSummary(); } catch (Exception e) { Log("playable bodies summary failed: " + e.GetType().Name + ": " + e.Message); }
                _pcRest?.Dispose();
                _pcRest = null;
                BuildingRest rest = _pcRestRef;
                if (rest == null) { Log("playable city rest: off (the world's profile), or never stepped"); return; }
                foreach (ReleaseWatch w in _pcReleaseWatches)
                    Log("playable city rest re-cut " + w.number + " at frame " + w.frame + ": the run ended within 1 s of it; max |v| so far released " + w.maxReleased.ToString("F2", Inv) + " new " + w.maxChildren.ToString("F2", Inv) + " others " + w.maxOthers.ToString("F2", Inv) + " m/s, lowest centre y " + w.minY.ToString("F2", Inv) + ", piece-frames below the floor " + w.below);
                Log("playable city physics steps: frames stepped " + _pcSteppedFrames + ", skipped " + _pcSkippedFrames + ", longest run of skipped frames " + _pcMaxSkipsInARow + ", least remaining Main at a decision " + (_pcMinRemaining * 1000).ToString("F3", Inv) + " ms, simulate max " + (_pcSimulateMax * 1000).ToString("F3", Inv) + " ms");
                BuildingFusion f = _pcFusionRef;
                if (f != null)
                {
                    Log("playable city fusion: groups " + f.GroupCount + " (made " + f.GroupsMade + ", merged " + f.GroupsMerged + "), fused pieces " + f.FusedPieces + ", group cuts " + f.GroupCuts + " (refused " + f.GroupCutsRefused + ", members classified " + f.MembersClassified + ", most in one " + f.MaxMembersInOneCut + ", crossed " + f.MembersSplit + ", members refused " + f.MembersRefused + ", fusions deferred " + f.FusionsDeferred + ", merges deferred " + f.MergesDeferred + ", groups held/released " + f.GroupsHeld + "/" + f.GroupsReleased
                        + "), finals " + f.FinalsPublished + " (failed " + f.FinalsFailed + "), sides dynamic/kinematic " + f.SidesDynamic + "/" + f.SidesKinematic + "; Main ms: fuse total " + (f.FuseSeconds * 1000).ToString("F2", Inv) + " max/turn " + (f.MaxFuseSeconds * 1000).ToString("F3", Inv)
                        + ", publish total " + (f.PublishSeconds * 1000).ToString("F2", Inv) + " max " + (f.MaxPublishSeconds * 1000).ToString("F3", Inv) + " (" + f.MaxCutMembers + " members), final total " + (f.FinalSeconds * 1000).ToString("F2", Inv) + " max " + (f.MaxFinalSeconds * 1000).ToString("F3", Inv)
                        + ", mass total " + (f.MassSeconds * 1000).ToString("F2", Inv) + " max " + (f.MaxMassSeconds * 1000).ToString("F3", Inv) + " (" + f.MaxMassMembers + " members)");
                    foreach (FusedGroup g in f.Groups) Log("playable city fusion group " + g.Key + ": members " + g.MemberCount + ", kinematic " + g.Kinematic + " (body " + (g.Body != null ? g.Body.isKinematic.ToString() : "none") + "), busy " + g.Busy + ", mass " + g.Mass.ToString("F1", Inv));
                    int shown = 0;
                    foreach (string e in f.Events) { if (shown++ >= 400) { Log("playable city fusion event ... " + (f.Events.Count - 400) + " more"); break; } Log("playable city fusion event " + e); }
                }

                Log("playable city rest (" + rest.Settings.mode + ", timeout " + rest.Settings.timeoutSeconds.ToString("F2", Inv) + " s, " + rest.Settings.supportSteps + " support steps" + (rest.Enabled ? "" : ", world ended") + "): tracked dynamic " + rest.TrackedDynamic + " anchored " + rest.TrackedAnchored
                    + "; kinematic rests " + rest.KinematicRests + ", held now " + rest.RestedNow + ", asleep by the rest now " + rest.AsleepNow + "; re-cuts " + rest.ReCuts + " (candidates " + rest.ReCutCandidates + ", most in one " + rest.MaxReCutCandidates + "; held to dynamic " + rest.ReleasedPieces + ", of them asleep " + rest.SleptOnRelease + " woken " + rest.WokenOnRelease
                    + "); woken by support loss " + rest.WokenBySupportLoss + ", grounds lost " + rest.LostGrounds + ", auto wakes " + rest.AutoWakes + "; unsupported past timeout now " + rest.UnsupportedPastTimeout + ", held or asleep without support now " + rest.AsleepWithoutSupport
                    + "; moved until rest mean " + rest.MeanMovedUntilRest.ToString("F3", Inv) + " max " + rest.MaxMovedUntilRest.ToString("F3", Inv) + " m; Main: release max " + (rest.MaxReleaseSeconds * 1000).ToString("F3", Inv) + " ms for " + rest.MaxReleasePieces + " candidates (total " + (rest.ReleaseSeconds * 1000).ToString("F2", Inv)
                    + " ms), support judgement max " + (rest.MaxSupportSeconds * 1000).ToString("F3", Inv) + " ms (total " + (rest.SupportSeconds * 1000).ToString("F1", Inv) + "), rest turn max " + (rest.MaxSleepSeconds * 1000).ToString("F3", Inv) + " ms (total " + (rest.SleepSeconds * 1000).ToString("F1", Inv) + "), contacts " + (rest.ContactSeconds * 1000).ToString("F1", Inv)
                    + " ms inside the simulation, switch " + (rest.SwitchSeconds * 1000).ToString("F2", Inv) + " ms, over " + rest.Steps + " turns; simulate max " + (_pcSimulateMax * 1000).ToString("F3", Inv) + " ms; contact headers " + rest.ContactHeaders + " notes " + rest.ContactNotes);

                // Every building piece live at the end, in the trial's terms (only while the world is still there).
                if (_world == null || _world.Owners == null || !rest.Enabled) { foreach (string e in rest.Events) Log("playable city rest event " + e); return; }
                int anchored = 0, anchoredHeld = 0, held = 0, asleep = 0, grounded = 0, unsupported = 0, notYet = 0, other = 0, propHeld = 0;
                var lines = new List<string>();
                foreach (KeyValuePair<LogicalFragmentId, PlayablePiece> p in _pcPieces)
                {
                    if (!_world.Owners.TryGet(p.Key, out PhysicsFragmentOwner owner) || owner.Body == null || owner.IsWithdrawn) continue;
                    if (p.Value.kind == "prop") { if (rest.IsRested(p.Key)) propHeld++; continue; }
                    if (owner.FixedByAnchors) { anchored++; if (rest.IsRested(p.Key)) anchoredHeld++; continue; }
                    if (rest.IsRested(p.Key)) { held++; continue; }
                    bool described = rest.TryDescribe(p.Key, out string state);
                    string cls = !described ? "not tracked" : state.StartsWith("asleep") ? "asleep by the rest" : state.Contains("timeout not yet") ? "not yet timed out" : state.Contains("grounded True") ? "grounded past timeout" : "unsupported past timeout";
                    if (cls == "grounded past timeout") grounded++; else if (cls == "unsupported past timeout") unsupported++; else if (cls == "not yet timed out") notYet++; else if (cls == "asleep by the rest") asleep++; else other++;
                    if (lines.Count < 32) lines.Add(cls + ": piece " + p.Key.value + " at " + owner.Root.transform.position.ToString("F2") + ": " + (described ? state : "not tracked by the trial") + "; " + Describe(p.Key, owner, p.Value));
                }

                Log("playable city rest building pieces at the end: anchored " + anchored + " (held by the trial " + anchoredHeld + "), held " + held + ", asleep by the rest " + asleep + ", dynamic grounded past timeout " + grounded + ", dynamic unsupported past timeout " + unsupported
                    + ", dynamic not yet timed out " + notYet + ", other " + other + "; prop pieces held by the trial " + propHeld);
                foreach (string l in lines) Log("playable city rest " + l);
                foreach (string e in rest.Events) Log("playable city rest event " + e);
                if (rest.Events.Count >= rest.Settings.eventRecords) Log("playable city rest event record full at " + rest.Settings.eventRecords + " (later events not kept)");
            }
        }
    }
}
