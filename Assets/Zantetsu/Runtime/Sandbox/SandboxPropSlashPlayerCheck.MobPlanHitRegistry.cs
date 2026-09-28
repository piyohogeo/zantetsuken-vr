using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // Measurement (the MobPlanSlash unit, hit registration): which individual the hit detector holds, slot by slot, at the
    // end of every frame of the replay (the check's LateUpdate, after the crowd's, the NPCs' and the katana's Update).
    // Each slot is matched on its current individual (id, generation = activations), its current handle (a serial per
    // handle object), the plan, the drawing, the bone level of detail, the detector and the handle's cut: whether a cut was
    // taken on it (held, an operation, withdrawn or ended). A row is written when a slot's state changes
    // (mobplan-hit-registry.csv) and a count per frame (mobplan-hit-registry-frames.csv); at each hit on a character itself,
    // the live individuals the detector does not hold at that moment are named. Only reads.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private StreamWriter _mpHitReg, _mpHitRegFrames;
            private readonly Dictionary<VpPreparedCharacterCut, int> _mpHandleSerial = new Dictionary<VpPreparedCharacterCut, int>();
            private readonly Dictionary<SandboxNpcCharacter, string> _mpHitRegState = new Dictionary<SandboxNpcCharacter, string>();
            private readonly Dictionary<SandboxNpcCharacter, (int since, string name, int serial)> _mpHitOut =
                new Dictionary<SandboxNpcCharacter, (int since, string name, int serial)>();
            private readonly List<(string name, int since, int until, string ended)> _mpHitOutPeriods = new List<(string, int, int, string)>();
            private readonly List<string> _mpHitRegViolations = new List<string>();
            private readonly HashSet<string> _mpHitRegNamed = new HashSet<string>();
            private int _mpHitRegFramesSeen, _mpHitRegUnregisteredFrames, _mpHitRegStaleMax, _mpHitRegInactiveInMax, _mpHitRegTakenInMax;
            private int _mpHitRegCutOutMax, _mpHitRegFramesWithCutOut, _mpHitRegFramesShort;
            private readonly Dictionary<string, int> _mpHitRegAtHit = new Dictionary<string, int>();

            private int HandleSerial(VpPreparedCharacterCut handle)
            {
                if (handle == null) return 0;
                if (!_mpHandleSerial.TryGetValue(handle, out int serial))
                {
                    serial = _mpHandleSerial.Count + 1;
                    _mpHandleSerial[handle] = serial;
                }

                return serial;
            }

            // Whether a cut was taken on the handle: from then on the detector lets the character go (SlashHitDetector
            // removes a character whose cut was requested or held, or whose handle is disposed).
            private static bool CutTaken(VpPreparedCharacterCut handle) =>
                handle == null || handle.IsDisposed || handle.IsHeld || handle.Operation.IsSet || handle.IsWithdrawn;

            private static string CutState(VpPreparedCharacterCut handle) =>
                handle == null ? "none" : handle.IsDisposed ? "disposed" : handle.IsWithdrawn ? "withdrawn" : handle.IsHeld ? "held"
                : handle.Operation.IsSet ? "op" + handle.Operation.value : "ready";

            private void MobPlanHitRegistryBegin()
            {
                _mpHitReg = new StreamWriter(Path.Combine(directory, "mobplan-hit-registry.csv")) { AutoFlush = true };
                _mpHitReg.WriteLine("frame,slot,activations,id,name,handle,state,inDetector,planned,drawn,lod,cut");
                _mpHitRegFrames = new StreamWriter(Path.Combine(directory, "mobplan-hit-registry-frames.csv"));
                _mpHitRegFrames.WriteLine("frame,live,lodRegistered,detector,active,activeInDetector,cutTakenOut,unregistered,inactiveInDetector,takenInDetector,stale");
            }

            // One slot as the check sees it now: its state word and, for a live individual, whether the detector holds it.
            private (string state, bool live, bool inDetector, bool taken, int id, string name) SlotNow(SandboxNpcCharacter c)
            {
                VpPreparedCharacterCut handle = c.Handle;
                bool inDetector = handle != null && _detector.HasCharacter(handle);
                bool hasActor = _mpActorOf.TryGetValue(c, out (int id, bool replacement, int addedFrame) a);
                bool retired = hasActor && _mpRetired.ContainsKey(a.id);
                bool live = hasActor && !retired && c.IsTarget;
                bool taken = CutTaken(handle);
                string name = hasActor && s_mobPlanNames.TryGetValue(c, out string n) ? n : "-";
                string state;
                if (live) state = inDetector ? (taken ? "target-with-taken-cut" : "target") : (taken ? "cut-taken" : "UNREGISTERED");
                else if (c.IsPrepared) state = inDetector ? "dormant-in-detector" : "dormant";
                else if (retired) state = inDetector ? "retired-in-detector" : "retired";
                else state = inDetector ? "other-in-detector" : "other";
                return (state, live, inDetector, taken, hasActor ? a.id : -1, name);
            }

            private void MobPlanHitRegistryFrame(int frame)
            {
                if (_mpHitReg == null) return;
                int active = 0, activeIn = 0, cutOut = 0, unregistered = 0, inactiveIn = 0, takenIn = 0, matched = 0;
                IReadOnlyList<SandboxNpcCharacter> slots = _crowd.Slots;
                for (int s = 0; s < slots.Count; s++)
                {
                    SandboxNpcCharacter c = slots[s];
                    if (c == null) continue;
                    (string state, bool live, bool inDetector, bool taken, int id, string name) now = SlotNow(c);
                    if (now.inDetector) matched++;
                    if (now.live)
                    {
                        active++;
                        if (now.inDetector) { activeIn++; if (now.taken) takenIn++; }
                        else if (now.taken) cutOut++;
                        else unregistered++;
                    }
                    else if (now.inDetector) inactiveIn++;

                    // An out-period of a live individual: opened when it leaves the detector, closed when it is back, retired
                    // or its slot carries another individual.
                    bool out_ = now.live && !now.inDetector;
                    if (_mpHitOut.TryGetValue(c, out (int since, string name, int serial) open)
                        && (!out_ || open.name != now.name || open.serial != HandleSerial(c.Handle)))
                    {
                        _mpHitOutPeriods.Add((open.name, open.since, frame, now.name != open.name || !now.live ? "retired" : now.inDetector ? "back in" : "new handle"));
                        _mpHitOut.Remove(c);
                    }

                    if (out_ && !_mpHitOut.ContainsKey(c)) _mpHitOut[c] = (frame, now.name, HandleSerial(c.Handle));

                    if (now.state == "UNREGISTERED" || now.state.EndsWith("-in-detector") || now.state == "target-with-taken-cut")
                    {
                        string key = now.name + " " + now.state;
                        if (_mpHitRegNamed.Add(key))
                        {
                            _mpHitRegViolations.Add(now.name + " (slot " + s + ", activations " + c.Activations + ", handle " + HandleSerial(c.Handle)
                                                    + ") " + now.state + " first at frame " + frame + " cut=" + CutState(c.Handle));
                        }
                    }

                    bool planned = now.id >= 0 && _crowd.TryGetPlan(now.id, out _);
                    bool drawn = c.Renderer != null && c.Renderer.enabled && c.Renderer.gameObject.activeInHierarchy;
                    string row = string.Join(",", s, c.Activations, now.id, now.name, HandleSerial(c.Handle), now.state, now.inDetector ? 1 : 0,
                        planned ? 1 : 0, drawn ? 1 : 0, c.Lod != null ? 1 : 0, CutState(c.Handle));
                    if (!_mpHitRegState.TryGetValue(c, out string last) || last != row)
                    {
                        _mpHitRegState[c] = row;
                        _mpHitReg.WriteLine(frame + "," + row);
                    }
                }

                int stale = _detector.CharacterCount - matched;
                _mpHitRegFramesSeen++;
                if (unregistered > 0) _mpHitRegUnregisteredFrames++;
                if (cutOut > 0) _mpHitRegFramesWithCutOut++;
                if (_detector.CharacterCount < _crowd.LiveCount) _mpHitRegFramesShort++;
                _mpHitRegCutOutMax = Math.Max(_mpHitRegCutOutMax, cutOut);
                _mpHitRegStaleMax = Math.Max(_mpHitRegStaleMax, stale);
                _mpHitRegInactiveInMax = Math.Max(_mpHitRegInactiveInMax, inactiveIn);
                _mpHitRegTakenInMax = Math.Max(_mpHitRegTakenInMax, takenIn);
                if (stale != 0 && _mpHitRegNamed.Add("stale"))
                {
                    _mpHitRegViolations.Add("the detector held " + stale + " candidate(s) that are no slot's current handle, first at frame " + frame);
                }

                _mpHitRegFrames.WriteLine(string.Join(",", frame, _crowd.LiveCount, _mpLod != null ? _mpLod.CharacterCount : -1, _detector.CharacterCount,
                    active, activeIn, cutOut, unregistered, inactiveIn, takenIn, stale));
            }

            // At a hit on a character itself, inside the katana's Update (the moment the display track reads its counts): the
            // live individuals the detector does not hold, with why.
            private void MobPlanHitRegistryAtHit(SandboxNpcCharacter hitCharacter, int frame)
            {
                var outNow = new List<string>();
                foreach (SandboxNpcCharacter c in _crowd.Slots)
                {
                    if (c == null) continue;
                    (string state, bool live, bool inDetector, bool taken, int id, string name) now = SlotNow(c);
                    if (now.live && !now.inDetector) outNow.Add(now.name + (c == hitCharacter ? "(this hit, " : "(") + CutState(c.Handle) + ")");
                }

                string key = outNow.Count == 0 ? "none" : outNow.Count == 1 && outNow[0].Contains("(this hit") ? "only this hit" : "other";
                _mpHitRegAtHit[key] = (_mpHitRegAtHit.TryGetValue(key, out int n) ? n : 0) + 1;
                MobPlanRecord("hit registry at hit: " + Model(hitCharacter) + " frame=" + frame + " live=" + _crowd.LiveCount
                              + " lod=" + (_mpLod != null ? _mpLod.CharacterCount : -1) + " detector=" + _detector.CharacterCount
                              + " live not in the detector=[" + string.Join(" ", outNow) + "]");
            }

            private void MobPlanHitRegistryEnd()
            {
                if (_mpHitReg == null) return;
                int frame = Time.frameCount;
                foreach (KeyValuePair<SandboxNpcCharacter, (int since, string name, int serial)> open in _mpHitOut)
                {
                    _mpHitOutPeriods.Add((open.Value.name, open.Value.since, frame, "open at the end"));
                }

                var lengths = _mpHitOutPeriods.Select(p => p.until - p.since).ToList();
                string histogram = string.Join(" ", lengths.GroupBy(l => l).OrderBy(g => g.Key).Select(g => g.Key + "f:" + g.Count()));
                string ends = string.Join(" ", _mpHitOutPeriods.GroupBy(p => p.ended).OrderBy(g => g.Key).Select(g => g.Key + ":" + g.Count()));
                string longest = string.Join(" ", _mpHitOutPeriods.OrderByDescending(p => p.until - p.since).Take(3)
                    .Select(p => p.name + "@" + p.since + "+" + (p.until - p.since) + "(" + p.ended + ")"));
                string atHit = string.Join(" ", _mpHitRegAtHit.OrderBy(k => k.Key).Select(k => k.Key + ":" + k.Value));
                string line = "mobplan hit registry: frames=" + _mpHitRegFramesSeen + " handles seen=" + _mpHandleSerial.Count
                              + " frames with the detector below live=" + _mpHitRegFramesShort + " frames with a live individual out for its taken cut="
                              + _mpHitRegFramesWithCutOut + " (max at once " + _mpHitRegCutOutMax + ") frames with a live individual unregistered="
                              + _mpHitRegUnregisteredFrames + " max candidates of no current handle=" + _mpHitRegStaleMax
                              + " max candidates of no live individual=" + _mpHitRegInactiveInMax + " max candidates whose cut was taken=" + _mpHitRegTakenInMax
                              + " out-periods=" + _mpHitOutPeriods.Count + " by length=[" + histogram + "] ended=[" + ends + "] longest=[" + longest
                              + "] at hits=[" + atHit + "]";
                Log(line);
                MobPlanRecord(line);
                foreach (string v in _mpHitRegViolations) Log("mobplan hit registry: " + v);
                Expect(_mpHitRegUnregisteredFrames == 0,
                    "[scenario] every active individual -- neither accepted, held, published nor retiring -- was a hit candidate at the end of every frame (" + _mpHitRegUnregisteredFrames + " frames)");
                Expect(_mpHitRegStaleMax == 0 && _mpHitRegInactiveInMax == 0 && _mpHitRegTakenInMax == 0,
                    "[scenario] the detector held only live individuals' current handles, none accepted or held, each once: "
                    + string.Join("; ", _mpHitRegViolations));
            }

            private void MobPlanHitRegistryClose()
            {
                _mpHitReg?.Dispose();
                _mpHitReg = null;
                _mpHitRegFrames?.Dispose();
                _mpHitRegFrames = null;
            }
        }
    }
}
