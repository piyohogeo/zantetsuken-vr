using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // The reused-slot check (TL, 2026-10-01), a section of its own after the script, apart from its figures (phase
    // "reuse-check"): the script does not guarantee that a natural hit meets an individual on a reused slot, so one such
    // individual -- live, on a slot the crowd's ordinary refill really reused -- is picked, and one small synthetic Slash at
    // its current shape is given once to the ordinary detector's Evaluate (no driver call, no forced retirement, no
    // teleport). The same individual is followed stage by stage: a hit target (drawn, posed, registered), detected, the hit
    // accepted and its cut published, that operation's geometry committed. Its slot, crowd id, generation and prepared cut
    // are recorded; an earlier individual's operation never passes. Each wait is bounded, and a stage not reached is not
    // passed. The script's natural hits on reused individuals are counted apart, as before.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            // Every individual the crowd took in: its slot, its generation on the slot (the slot's activation that carried it),
            // its prepared cut, its crowd id and the frame it came in.
            private readonly Dictionary<string, (SandboxNpcCharacter slot, int generation, VpPreparedCharacterCut handle, int id, int frame)> _mpIndividuals =
                new Dictionary<string, (SandboxNpcCharacter, int, VpPreparedCharacterCut, int, int)>();
            private long _mprSlashId = 950000;

            private void MobPlanReuseNoteAdded(string name, SandboxNpcCharacter c, int id)
            {
                _mpIndividuals[name] = (c, c.Activations, c.Handle, id, Time.frameCount);
            }

            private static string HandleTag(object h) => h == null ? "none" : "h" + RuntimeHelpers.GetHashCode(h).ToString("x8");

            private IEnumerator MobPlanReuseCheck()
            {
                string phaseBefore = _phase;
                _phase = "reuse-check";
                const string tag = "[reuse check: synthetic Slash through the ordinary detector] ";
                Log("reuse check: begins at frame " + Time.frameCount + " (after the script; a synthetic Slash through the ordinary detector, not a natural hit; its frames are phase reuse-check, apart from the script's figures); reused activations so far " + _crowd.ReusedActivations);
                float by = Time.realtimeSinceStartup + 10f;
                while (_katana != null && _katana.WaveCount != 0 && Time.realtimeSinceStartup < by) yield return null;

                // (1) The individual: on a slot reused by the ordinary refill, live, a hit target.
                SandboxNpcCharacter slot = null;
                string name = null;
                (SandboxNpcCharacter slot, int generation, VpPreparedCharacterCut handle, int id, int frame) me = default;
                VpPreparedCharacterCut previous = null;
                string previousName = null;
                var why = new Dictionary<string, string>();
                by = Time.realtimeSinceStartup + 15f;
                while (true)
                {
                    why.Clear();
                    foreach (SandboxNpcCharacter c in _crowd.Slots)
                    {
                        if (c == null || !_mpSlotIndividuals.TryGetValue(c, out List<string> carried) || carried.Count < 2) continue;
                        string n = carried[carried.Count - 1];
                        if (!_mpIndividuals.TryGetValue(n, out var info) || !_mpIndividuals.TryGetValue(carried[carried.Count - 2], out var before)) { why[c.CharacterRoot.name] = "not recorded"; continue; }
                        string reason = MobPlanReuseProbe.WhyNotTarget(c, carried.Count, info.generation, info.handle, before.handle, _mpRetired.ContainsKey(info.id), info.frame, _detector);
                        if (reason != null) { why[c.CharacterRoot.name + " (" + n + ")"] = reason; continue; }
                        slot = c; name = n; me = info; previous = before.handle; previousName = carried[carried.Count - 2];
                        break;
                    }

                    if (slot != null || Time.realtimeSinceStartup >= by || _world.IsEnding) break;
                    yield return null;
                }

                foreach (KeyValuePair<string, string> w in why) Log("reuse check: not picked " + w.Key + ": " + w.Value);
                if (slot == null)
                {
                    Log("reuse check: STOPPED at stage 1: no live individual on a reused slot stood as a hit target within 15 s (frame " + Time.frameCount + ")");
                    Expect(false, tag + "(1) a live individual on a reused slot stood as a hit target");
                    _phase = phaseBefore;
                    yield break;
                }

                List<string> history = _mpSlotIndividuals[slot];
                Log("reuse check: picked " + name + " (crowd id " + me.id + ") on slot " + slot.CharacterRoot.name + " at frame " + Time.frameCount + ": generation " + me.generation + " (slot activations " + slot.Activations + ", prepared again " + slot.Reprepared
                    + "), the slot's individuals in turn [" + string.Join(" ", history) + "], its prepared cut " + HandleTag(me.handle) + " (the previous individual " + previousName + "'s " + HandleTag(previous) + ", its operation " + (previous.Operation.IsSet ? previous.Operation.value.ToString() : "none")
                    + "); activated at frame " + me.frame + "; drawn, posed, a hit target of the detector; Source set before the Slash " + me.handle.Source.IsSet + " (not a condition)");
                Expect(true, tag + "(1) a live individual on a reused slot stood as a hit target (" + name + ", generation " + me.generation + ")");

                // (2) One small Slash at its current shape, given once to the ordinary detector.
                long id = _mprSlashId++;
                if (!MobPlanReuseProbe.TryAim(me.handle, slot.Lod, slot.CharacterRoot.transform.forward, id, Time.timeAsDouble, 0.25f, out SlashSweep sweep, out string aimed))
                {
                    Log("reuse check: STOPPED at stage 2 (aim): " + aimed);
                    Expect(false, tag + "(2) the synthetic Slash met the individual's current shape (aim: " + aimed + ")");
                    _phase = phaseBefore;
                    yield break;
                }

                int opsBefore = _world.Ledger.OperationCount;
                int hullHitsBefore = _world.Hulls != null ? _world.Hulls.Hits.Count : 0;
                int frameGiven = Time.frameCount;
                _detector.Evaluate(new[] { sweep }, new[] { id });
                var hits = new List<SlashHitConfirmed>();
                for (int i = 0; i < _detector.HitCount; i++) hits.Add(_detector.HitAt(i));
                if (hits.Count > 0) _lastHitListAt = hits[0].At;   // read here, not by the script's per-frame reading (kept apart from its records)
                // Everything this one evaluation set going, every hit of it, followed apart from the run's own records.
                var characters = new List<VpPreparedCharacterCut>();
                foreach (SandboxNpcCharacter c in _crowd.Slots) if (c != null && c.Handle != null) characters.Add(c.Handle);
                // A failure is allowed only when a record names it: the driver's failures (whatever frame they came in) or the geometry's own.
                var aftermath = new MobPlanSlashAftermath(_world, id, hits, characters, opsBefore, hullHitsBefore,
                    op => _mpFailures.TryGet(op, out _) || _world.Geometry.FailureOf(op) != VpStorageCutStatus.Ok);
                yield return MobPlanReuseStages(tag, id, aimed, frameGiven, opsBefore, hits, name, me, previous);

                // The rest of what the Slash set going, whatever the stages above found: bounded, 30 s of real time from here, never extended.
                Log("reuse check: waiting for everything the Slash set going (at most 30 s from frame " + Time.frameCount + "): " + aftermath.Describe());
                yield return aftermath.Wait(30f);
                Log("reuse check: " + (aftermath.Passed ? "everything the Slash set going ended acceptably" : "NOT passed (finished " + aftermath.Finished + ", acceptable " + aftermath.Acceptable + ", timed out " + aftermath.TimedOut + ", the world ended " + aftermath.WorldEnded + ")")
                    + " after " + aftermath.WaitedSeconds.ToString("F2", Inv) + " s, at frame " + Time.frameCount + ": " + aftermath.Describe());
                Expect(aftermath.Passed, tag + "(after) every result of the Slash's one evaluation ended within 30 s, each a commit, an ordinary refusal or a failure a record names (hits " + aftermath.HitCount + ", allowed failures " + aftermath.AllowedFailures
                    + ", anomalies " + aftermath.Anomalies.Count + (aftermath.Anomalies.Count > 0 ? ": " + string.Join("; ", aftermath.Anomalies) : "") + ")");
                _phase = phaseBefore;
            }

            // The stages after the Slash: (2) detected, (3) accepted and its own operation published, (4) that operation committed.
            // The first that fails is recorded and the later ones are not judged; what the Slash set going is waited for after.
            private IEnumerator MobPlanReuseStages(string tag, long id, string aimed, int frameGiven, int opsBefore, List<SlashHitConfirmed> hits, string name,
                (SandboxNpcCharacter slot, int generation, VpPreparedCharacterCut handle, int id, int frame) me, VpPreparedCharacterCut previous)
            {
                LogicalFragmentId root = me.handle.Source;
                Log("reuse check: Slash " + id + " given once to the ordinary detector at frame " + frameGiven + " (" + aimed + "): detector hits " + hits.Count + "; the individual's fragment now " + (root.IsSet ? root.value.ToString() : "none"));
                foreach (SlashHitConfirmed h in hits)
                {
                    Log("reuse check: detector hit slash " + h.SlashId + " fragment " + h.Fragment.value + " side " + h.Side + " => " + h.Acceptance + " / " + h.Admission + " operation " + h.Operation.value
                        + (root.IsSet && h.Fragment == root ? " (the picked individual)" : " (another fragment)"));
                }

                bool rootIsEarlier = root.IsSet && _mpRootNames.TryGetValue(root, out string named) && named != name;
                if (root.IsSet && !_mpRootNames.ContainsKey(root)) _mpRootNames[root] = name;
                SlashHitConfirmed mine = hits.FirstOrDefault(h => h.SlashId == id && root.IsSet && h.Fragment == root);
                bool detected = root.IsSet && !rootIsEarlier && hits.Any(h => h.SlashId == id && h.Fragment == root);
                if (!detected)
                {
                    Log("reuse check: STOPPED at stage 2 (detection): the aimed sweep met the current shape by the detector's own query, and the detector reported no hit on " + name + (rootIsEarlier ? " (its fragment is an earlier individual's)" : "")
                        + "; handle " + HandleTag(me.handle) + " hit target " + (!me.handle.IsDisposed && me.handle.IsHitTarget) + ", registered " + _detector.HasCharacter(me.handle));
                    Expect(false, tag + "(2) the ordinary detector detected the hit on the picked individual");
                    yield break;
                }

                Expect(true, tag + "(2) the ordinary detector detected the hit on the picked individual (fragment " + root.value + ", " + mine.Acceptance + ")");

                // (3) Accepted, and its own operation published.
                bool accepted = mine.Acceptance == ProvisionalCutAcceptance.Published || mine.Acceptance == ProvisionalCutAcceptance.Pending || mine.Acceptance == ProvisionalCutAcceptance.Held;
                CutOperationId op = default;
                LogicalCutOperation record = default;
                bool published = false;
                float by = Time.realtimeSinceStartup + 20f;
                while (accepted && Time.realtimeSinceStartup < by)
                {
                    op = me.handle.Operation;
                    if (op.IsSet && _world.Ledger.TryGetOperation(op, out record) && record.state != LogicalCutOperationState.Admitted) { published = true; break; }
                    yield return null;
                }

                bool own = op.IsSet && op.value > opsBefore && (!mine.Operation.IsSet || mine.Operation == op) && record.source == root
                    && !(previous.Operation.IsSet && previous.Operation == op);
                Log("reuse check: acceptance " + mine.Acceptance + "; the individual's operation " + (op.IsSet ? op.value.ToString() : "none") + " (ledger had " + opsBefore + " before the Slash; the hit's " + mine.Operation.value + "; source " + record.source.value
                    + "; the previous individual's " + (previous.Operation.IsSet ? previous.Operation.value.ToString() : "none") + "), " + (published ? "published (" + record.state + ") at frame " + Time.frameCount : "NOT published within 20 s"));
                if (!accepted || !published || !own)
                {
                    Log("reuse check: STOPPED at stage 3: accepted " + accepted + ", published " + published + ", its own operation " + own);
                    Expect(false, tag + "(3) the hit was accepted and the picked individual's own cut published (" + mine.Acceptance + ", published " + published + ", own " + own + ")");
                    yield break;
                }

                Expect(true, tag + "(3) the hit was accepted and the picked individual's own cut published (operation " + op.value + ")");

                // (4) That operation's geometry committed.
                by = Time.realtimeSinceStartup + 30f;
                while (_world.Geometry.StageOf(op) != CutGeometryStage.Committed && Time.realtimeSinceStartup < by) yield return null;
                bool committed = _world.Geometry.StageOf(op) == CutGeometryStage.Committed;
                _world.Ledger.TryGetOperation(op, out record);
                Log("reuse check: operation " + op.value + " geometry " + _world.Geometry.StageOf(op) + " (ledger " + record.state + ") at frame " + Time.frameCount + "; " + name + " retired " + _mpRetired.ContainsKey(me.id)
                    + "; handle withdrawn " + me.handle.IsWithdrawn);
                Expect(committed, tag + "(4) the picked individual's operation " + op.value + " reached its geometry commit");
                if (!committed) Log("reuse check: STOPPED at stage 4: not committed within 30 s");
            }
        }
    }
}
