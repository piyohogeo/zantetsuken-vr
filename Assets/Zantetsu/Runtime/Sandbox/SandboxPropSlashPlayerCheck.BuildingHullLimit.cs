using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // The always-kinematic building's cut limit in the coexistence check (TL, 2026-10-01): with N given, the re-cut during a
    // drop is exercised before the script (the building below N), and after the script a building that reached N is shown
    // cut no more -- by the script's own sweeps (no hit asked after its stop published) and by one synthetic sweep through
    // its hull given to the ordinary detector (no building hit, no new cut). The limit never reached is a failure (not
    // exercised), not a pass.
    public static partial class SandboxPropSlashPlayerCheck
    {
        private sealed partial class Walk
        {
            private bool HullLimitOn => _world != null && _world.Hulls != null && _world.Hulls.Settings.LimitOn;

            // The required sections (TL, 2026-10-01): decided before the run from the scenario and the world's profile, never
            // from what the run later finds; judged at the summary whatever became of them.
            private HullMidDropSection _hullMidDrop = new HullMidDropSection(false, "not decided");
            private bool _hullLimitChecked;
            private readonly Dictionary<int, int> _bhRegistrationChecked = new Dictionary<int, int>();   // building -> its group id, checked as registered before its first cut

            private void HullRequiredDecide()
            {
                bool profileSays = _world != null && _world.Profile.BuildingHull.enabled && _world.Profile.BuildingHull.LimitOn;
                bool required = MobPlanMode && profileSays;
                string because = "the coexistence scenario (MobPlan) " + MobPlanMode + ", the world's profile: hull on " + (_world != null && _world.Profile.BuildingHull.enabled)
                    + ", always kinematic " + (_world != null && _world.Profile.BuildingHull.kinematicDisplay) + ", cut limit N " + (_world != null ? _world.Profile.BuildingHull.geometryLimit : 0);
                _hullMidDrop = new HullMidDropSection(required, because);
                if (MobPlanMode) Log("hull required sections: the re-cut during a drop before the script and the limit check after it " + (required ? "REQUIRED" : "not required") + " (" + because + ")");
                if (required && HullLimitOn) HullLimitSettingsLog();
                // The coexistence profile's impulse read back in the Player (TL, 2026-10-01): by mass, k 1.0 N s/kg.
                if (MobPlanMode && _world != null && _world.Profile.name == "CutWorldPlayableCityProfile")
                {
                    Expect(_world.Profile.SeparationImpulseByMass && _world.Profile.SeparationImpulsePerKg == 1.0f,
                        "[impulse] the coexistence profile gives each free child J = k x mass with k 1.0 N s/kg (read back: byMass " + _world.Profile.SeparationImpulseByMass + ", k " + _world.Profile.SeparationImpulsePerKg.ToString("R", Inv) + ")");
                }
            }

            /// <summary>
            /// The re-cut during a drop, before the script (required with the cut limit on): the building found and checked as
            /// registered first (before any cut of it), then the section, its frames recorded. With a head wait the external
            /// replay is the head's move or any ordinary Slash wave; without one, any ordinary Slash wave.
            /// </summary>
            private IEnumerator HullMidDropBeforeScript(bool headWait, Camera head, Vector3 readyPosition, Quaternion readyRotation)
            {
                if (!_hullMidDrop.Required || _hullMidDrop.Started) yield break;
                int building = -1;
                foreach (PlayableCityCuttable c in Object.FindObjectsByType<PlayableCityCuttable>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (!c.building || !c.IsRegistered || c.Registration.Group == null || _world.Hulls == null) continue;
                    Log("hull mid-drop section: the building found before the script: " + c.Registration.Description);
                    BuildingHullBegin(c.Registration.Group, c.Registration.Fragment);   // as registered, before its first cut
                    _bhRegistrationChecked[c.Registration.Group.Building] = c.Registration.Group.Id;
                    building = c.Registration.Group.Building;
                    break;
                }

                string phaseFound = _phase, scenarioFound = _bhScenario;
                _phase = "hull-middrop";
                _bhScenario = "middrop";
                Log("hull mid-drop section: " + (headWait ? "in the head wait, before the external replay" : "NO head wait: before the script's start, the external replay judged by its Slash waves only") + "; the frames from here are phase hull-middrop");
                string External()
                {
                    if (_katana != null && _katana.WaveCount > 0) return "an ordinary Slash wave flew (" + _katana.WaveCount + " live)";
                    if (headWait && head != null && (Vector3.Distance(head.transform.position, readyPosition) > startOnHeadMove || Quaternion.Angle(head.transform.rotation, readyRotation) > 0.5f))
                        return "the head moved (the replay began)";
                    return null;
                }

                IEnumerable<VpPreparedCharacterCut> Characters()
                {
                    var list = new List<VpPreparedCharacterCut>();
                    if (_crowd != null) foreach (SandboxNpcCharacter c in _crowd.Slots) if (c != null && c.Handle != null) list.Add(c.Handle);
                    return list;
                }

                RaiseMidDropSectionBegun();
                yield return _hullMidDrop.Run(_world, _detector, building, () => 900000 + (++_bhSyntheticSlashes), External, BuildingHullFrame,
                    () => { if (_detector.HitCount > 0) _lastHitListAt = _detector.HitAt(0).At; }, Characters,
                    op => _mpFailures.TryGet(op, out _) || _world.Geometry.FailureOf(op) != VpStorageCutStatus.Ok, Log,
                    () => Time.realtimeSinceStartupAsDouble);   // the building trial's own clock in the Player (no test clock there)
                _phase = phaseFound;
                _bhScenario = scenarioFound;
                Log("hull mid-drop section: the script goes on from here at frame " + Time.frameCount + (building >= 0 && _world.Hulls != null ? ": building " + building + " n " + _world.Hulls.CountDisplayGeometries(building) + ", stopped " + _world.Hulls.IsCutStopped(building) : ""));
            }

            /// <summary>At the script's start, a building checked as registered before the section: its current state (the same building and group, one of each), and the n the script goes on from.</summary>
            private void BuildingHullCurrent(HullGroup registered)
            {
                BuildingHullFusion h = _world.Hulls;
                int building = registered.Building;
                HullGroup live = null;
                int liveGroups = 0;
                foreach (HullGroup g in h.Groups) if (g.Building == building && g.State != HullGroupState.Gone) { live = g; liveGroups++; }
                bool one = HullMidDropSection.OneEach(h, building, out string why);
                _bhRegistrationChecked.TryGetValue(building, out int groupAtRegistration);
                Log("building hull at the script's start (checked as registered before the section): building " + building + " live groups " + liveGroups + " (group " + (live != null ? live.Id.ToString() : "none") + ", registered as " + groupAtRegistration + "), kinematic " + (live != null && live.Kinematic)
                    + ", followed by the rest " + (live != null && _world.Rest.IsTracked(live.Body)) + ", one each " + one + (why != null ? " (" + why + ")" : "") + "; n " + h.CountDisplayGeometries(building) + " (the script goes on from it), stopped " + h.IsCutStopped(building)
                    + ", hits so far " + h.Hits.Count + ", synthetic Slashes used " + _bhSyntheticSlashes);
                Expect(live != null && liveGroups == 1 && live.Id == groupAtRegistration && live.Kinematic && live.Body != null && live.Body.isKinematic && !_world.Rest.IsTracked(live.Body) && one,
                    "[hull kinematic] at the script's start the building stands as the same one kinematic group with one body, one enabled collider and one hull, not followed by the rest");
            }

            private void HullRequiredJudge()
            {
                _hullMidDrop.Judge(Expect, Log);
                if (_hullMidDrop.Required) Expect(_hullLimitChecked, "[hull limit] the required limit check after the script ran (" + (_hullLimitChecked ? "ran" : "never ran") + ")");
            }

            private void HullLimitSettingsLog()
            {
                BuildingHullSettings s = _world.Hulls.Settings;
                Log("building hull cut limit: N " + s.geometryLimit + " (quality), p " + s.dropExponent.ToString("R", Inv) + " and D0 " + s.dropBaseMetres.ToString("R", Inv) + " m (art); D(n) for n = 1, 2, 4, 8: "
                    + string.Join(", ", new[] { 1, 2, 4, 8 }.Where(n => n < s.geometryLimit).Select(n => BuildingHullFusion.LimitDistance(n, s.geometryLimit, s.dropExponent, s.dropBaseMetres).ToString("F4", Inv))));
            }

            private IEnumerator HullLimitCheck()
            {
                BuildingHullFusion h = _world.Hulls;
                _hullLimitChecked = true;
                string phaseBefore = _phase;
                _phase = "hull-limit";
                const string tag = "[hull limit] ";
                Log("hull limit: after the script, at frame " + Time.frameCount + ": buildings stopped " + h.StoppedBuildings + ", hits refused by the limit " + h.HitsRefusedByLimit + " (held requests ended " + h.HeldEndedByLimit + ")");
                if (h.StoppedBuildings == 0)
                {
                    Expect(false, tag + "a building reached its cut limit during the run (N " + h.Settings.geometryLimit + "): not exercised");
                    _phase = phaseBefore;
                    yield break;
                }

                var groupBuilding = new Dictionary<int, int>();
                foreach (HullGroup g in h.Groups) groupBuilding[g.Id] = g.Building;
                foreach (HullGroup g in h.Groups)
                {
                    if (!h.IsCutStopped(g.Building) || g.State == HullGroupState.Gone || g.Root == null || g.Shape == null) continue;
                    h.TryGetStopTime(g.Building, out double stopAt);
                    int publishedAfter = 0, askedAfter = 0, refusedAfter = 0;
                    foreach (BuildingHullFusion.HullHit hit in h.Hits)
                    {
                        if (!groupBuilding.TryGetValue(hit.group, out int b) || b != g.Building || hit.askedAt <= stopAt) continue;
                        askedAfter++;
                        if (hit.outcome == "Published") publishedAfter++; else refusedAfter++;
                    }

                    Log("hull limit: building " + g.Building + " stopped at real " + stopAt.ToString("F3", Inv) + " (" + (h.StopRecords.TryGetValue(g.Building, out string record) ? record : "-") + "); hits asked after it " + askedAfter + " (published " + publishedAfter + ", refused " + refusedAfter
                        + "); its display geometries now " + h.CountDisplayGeometries(g.Building));
                    Expect(publishedAfter == 0, tag + "after building " + g.Building + " reached N, no sweep of the script cut it again (" + publishedAfter + " published after its stop)");

                    // One synthetic sweep through its hull, given to the ordinary detector.
                    g.Shape.ConvexBounds(0, out float3 glo, out float3 ghi);
                    float3 lo = new float3(float.MaxValue), hi = new float3(float.MinValue);
                    float4x4 toWorld = (float4x4)g.Root.transform.localToWorldMatrix;
                    for (int k = 0; k < 8; k++)
                    {
                        float3 corner = math.transform(toWorld, new float3((k & 1) != 0 ? ghi.x : glo.x, (k & 2) != 0 ? ghi.y : glo.y, (k & 4) != 0 ? ghi.z : glo.z));
                        lo = math.min(lo, corner); hi = math.max(hi, corner);
                    }

                    float3 c = (lo + hi) * 0.5f;
                    int cutsBefore = h.GroupCuts, hitsBefore = h.Hits.Count, displayCutsBefore = h.DisplayCuts;
                    long slash = 900000 + (++_bhSyntheticSlashes);
                    var level = new SlashSweep(slash, Time.unscaledTimeAsDouble, false, new Plane(Vector3.up, -c.y), Vector3.right, Vector3.forward,
                        new Vector3(lo.x - 1f, c.y, lo.z - 1f), new Vector3(lo.x - 1f, c.y, hi.z + 1f), new Vector3(hi.x + 1f, c.y, lo.z - 1f), new Vector3(hi.x + 1f, c.y, hi.z + 1f));
                    _detector.Evaluate(new[] { level }, new[] { slash });
                    int buildingHits = 0;
                    for (int i = 0; i < _detector.HitCount; i++)
                    {
                        SlashHitConfirmed hit = _detector.HitAt(i);
                        foreach (LogicalFragmentId f in g.Fragments) if (f == hit.Fragment) buildingHits++;
                    }

                    if (_detector.HitCount > 0) _lastHitListAt = _detector.HitAt(0).At;
                    for (int f = 0; f < 10; f++) yield return null;
                    Log("hull limit: synthetic Slash " + slash + " (level y " + c.y.ToString("F3", Inv) + " through building " + g.Building + ") given once to the ordinary detector: detector hits " + _detector.HitCount + ", on the building " + buildingHits
                        + "; group cuts " + cutsBefore + " -> " + h.GroupCuts + ", hit records " + hitsBefore + " -> " + h.Hits.Count + ", display cuts " + displayCutsBefore + " -> " + h.DisplayCuts);
                    Expect(buildingHits == 0 && h.GroupCuts == cutsBefore && h.Hits.Count == hitsBefore && h.DisplayCuts == displayCutsBefore,
                        tag + "a sweep through building " + g.Building + " after its stop was no hit and made no cut (detector hits on it " + buildingHits + ", new cuts " + (h.GroupCuts - cutsBefore) + ")");
                }

                _phase = phaseBefore;
            }

            private void HullLimitSummary()
            {
                BuildingHullFusion h = _world != null ? _world.Hulls : null;
                if (h == null || !h.Settings.LimitOn) return;
                HullLimitSettingsLog();
                int maxAfter = 0;
                foreach (BuildingHullFusion.HullHit hit in h.Hits) maxAfter = System.Math.Max(maxAfter, hit.geometriesAfter);
                Log("building hull cut limit: buildings stopped " + h.StoppedBuildings + ", hits refused by the limit " + h.HitsRefusedByLimit + " (held requests ended " + h.HeldEndedByLimit + "), the largest settled count " + maxAfter
                    + " (at most 2 (N - 1) = " + (2 * (h.Settings.geometryLimit - 1)) + " with cuts in two)");
                foreach (string r in h.LimitRecords) Log("building hull cut limit: " + r);
                foreach (KeyValuePair<int, string> s in h.StopRecords) Log("building hull cut limit stop: building " + s.Key + " " + s.Value);
                Expect(maxAfter <= 2 * (h.Settings.geometryLimit - 1), "[hull limit] no settled count past 2 (N - 1) (" + maxAfter + ")");
            }
        }
    }
}
