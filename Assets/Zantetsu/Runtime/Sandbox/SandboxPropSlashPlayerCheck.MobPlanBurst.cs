using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using Zantetsu.Core.Slash;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // LOAD CHECK ONLY (the MobPlan refill unit), not an end-to-end check: "-zantetsuMobPlanRetireBurst <count> <everyFrames>"
    // feeds the detector directly -- no Gesture, no SlashWave -- to retire several individuals at once. Every that many
    // frames, on the first frame no Slash of the script is flying, the <count> live individuals nearest the player are swept
    // at the waist by one Slash (one id, from 900000, one sweep per individual, all in one evaluation), so their cuts,
    // publication, withdrawal and retirement are the ordinary ones and their slots come back together. A burst that throws
    // stops the bursts for the rest of the run, its first failure logged once; it is never tried again frame after frame.
    // Nothing of the product is changed. E2E from the Gesture is the script's own Slash, not this.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            public const string MobPlanRetireBurstArgument = "-zantetsuMobPlanRetireBurst";
            private int _burstCount = -1, _burstEvery, _burstNext = -1, _bursts;
            private string _burstFailure;
            private long _burstSlashId = 900000;

            private void MobPlanBurst(int frame, int update)
            {
                if (_burstCount < 0)
                {
                    _burstCount = 0;
                    string[] args = Environment.GetCommandLineArgs();
                    int at = Array.IndexOf(args, MobPlanRetireBurstArgument);
                    if (at >= 0 && at + 2 < args.Length && int.TryParse(args[at + 1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
                        && int.TryParse(args[at + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int every) && count > 0 && every > 0)
                    {
                        _burstCount = count;
                        _burstEvery = every;
                        Log("mobplan retire burst (load check, detector input, not Gesture E2E): " + count + " individuals every " + every
                            + " frames, one Slash each burst (on a frame no Slash is flying)");
                    }
                }

                if (_burstCount == 0 || _burstFailure != null || _crowd == null || !_crowd.IsReady || _detector == null || _katana == null) return;
                if (_burstNext < 0) _burstNext = frame + _burstEvery;
                if (frame < _burstNext || _katana.WaveCount != 0) return;

                Vector3 player = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
                var live = new List<SandboxNpcCharacter>();
                foreach (SandboxNpcCharacter c in _crowd.Slots)
                    if (c != null && c.IsTarget && c.Handle != null && !c.Handle.IsDisposed && c.Handle.IsHitTarget) live.Add(c);
                live.Sort((a, b) => (a.CharacterRoot.transform.position - player).sqrMagnitude.CompareTo((b.CharacterRoot.transform.position - player).sqrMagnitude));
                int n = Math.Min(_burstCount, live.Count);
                if (n == 0) return;

                var sweeps = new SlashSweep[n];
                long id = _burstSlashId++;
                double time = Time.timeAsDouble;
                var names = new List<string>();
                for (int i = 0; i < n; i++)
                {
                    Transform root = live[i].CharacterRoot.transform;
                    Vector3 waist = root.position + Vector3.up * 1.0f;
                    Vector3 forward = Vector3.ProjectOnPlane(root.forward, Vector3.up).normalized;
                    if (forward.sqrMagnitude < 0.5f) forward = Vector3.forward;
                    Vector3 right = Vector3.Cross(Vector3.up, forward);
                    sweeps[i] = new SlashSweep(id, time, false, new Plane(Vector3.up, waist), forward, right,
                        waist - forward * 0.6f - right * 0.6f, waist - forward * 0.6f + right * 0.6f,
                        waist + forward * 0.6f - right * 0.6f, waist + forward * 0.6f + right * 0.6f);
                    names.Add(live[i].CharacterRoot.name);
                }

                _burstNext = frame + _burstEvery;
                try
                {
                    _detector.Evaluate(new ReadOnlySpan<SlashSweep>(sweeps), new ReadOnlySpan<long>(new[] { id }));
                }
                catch (Exception e)
                {
                    _burstFailure = "frame " + frame + " Slash " + id + " sweeps " + n + ": " + e.GetType().Name + ": " + e.Message;
                    Log("mobplan retire burst STOPPED at its first failure (no further bursts this run): " + _burstFailure);
                    Expect(false, "the load-check burst failed: " + _burstFailure);
                    return;
                }

                _bursts++;
                Log("mobplan retire burst " + _bursts + ": frame=" + frame + " Slash " + id + " sweeps " + n + " (" + string.Join(", ", names) + ") hits " + _detector.HitCount
                    + "; slots free " + _crowd.FreeSlots + " returning " + _crowd.ReturningSlots + " preparing " + _crowd.PreparingSlots);
                if (_detector.HitCount > 0)
                {
                    _lastHitListAt = _detector.HitAt(0).At;
                    for (int i = 0; i < _detector.HitCount; i++)
                    {
                        SlashHitConfirmed hit = _detector.HitAt(i);
                        _seenHits.Add(hit);
                        LogHit(hit, frame, update);
                    }
                }

            }
        }
    }
}
