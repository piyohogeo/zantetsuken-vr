using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The per-frame placement-only build's repeated checks (TL, 2026-10-04): a registration that comes again with the
    /// values it passed with is not validated again, and a Static placement -- the registration's own matrix, already
    /// validated in the same build -- is not checked a second time. Against checking everything every time (the old pass):
    /// over a sequence of placement-only builds on one snapshot, with registrations unchanged, moved, made invalid in each
    /// of the ways the validation refuses, made valid again, and reordered, the same outcome, reason and shortage at every
    /// step and the same snapshot item by item; the provider asked as often. Then the same binary's cost with nothing
    /// changing, old and new. Written out, not judged.
    /// </summary>
    public class VpMultiCutSnapshotValidateMemoTests
    {
        private sealed class Answers : IVpFragmentPlacement
        {
            public Func<LogicalFragmentId, (VpFragmentPlacementKind kind, Matrix4x4 m)> answer;
            public int asked;

            public VpFragmentPlacementKind TryGetGeometryLocalToWorld(LogicalFragmentId fragment, CutOperationId operation, float side, out Matrix4x4 geometryLocalToWorld)
            {
                asked++;
                (VpFragmentPlacementKind kind, Matrix4x4 m) = answer(fragment);
                geometryLocalToWorld = m;
                return kind;
            }
        }

        private static VpMultiCutSnapshot NewSnapshot() => new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));

        private static void Old(bool old)
        {
            VpMultiCutSnapshot.validateEveryRegistrationForTest = old;
            VpMultiCutSnapshot.placementCheckStaticForTest = old;
            VpMultiCutSnapshot.placementCheckEveryFollowingForTest = old;
        }

        private static VpMultiCutRegistration With(VpMultiCutRegistration r, Bounds? bounds = null, Matrix4x4? placement = null, Matrix4x4? lineage = null) =>
            new VpMultiCutRegistration(r.root, bounds ?? r.localBounds, placement ?? r.geometryLocalToWorld, lineage ?? r.lineageToGeometryLocal, r.reflected, r.vertexEpsilon);

        // The steps of one sequence: what the registrations are at each placement-only build of the same snapshot.
        private static List<(string what, List<VpMultiCutRegistration> registrations)> Steps(List<VpMultiCutRegistration> start)
        {
            var steps = new List<(string, List<VpMultiCutRegistration>)>();
            List<VpMultiCutRegistration> Copy() => new List<VpMultiCutRegistration>(start);
            int a = start.Count / 3, b = start.Count - 1;
            steps.Add(("as registered", Copy()));
            steps.Add(("the same again", Copy()));
            var moved = Copy();
            moved[a] = With(start[a], placement: Matrix4x4.TRS(new Vector3(0.5f, 0.25f, -0.5f), Quaternion.Euler(0f, 30f, 0f), Vector3.one) * start[a].geometryLocalToWorld);
            steps.Add(("one moved", moved));
            steps.Add(("the same again after the move", new List<VpMultiCutRegistration>(moved)));
            var nan = Copy();
            Matrix4x4 m = start[b].geometryLocalToWorld; m.m13 = float.NaN;
            nan[b] = With(start[b], placement: m);
            steps.Add(("the last one's placement not finite", nan));
            steps.Add(("valid again", Copy()));
            var singular = Copy();
            singular[a] = With(start[a], placement: Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1f, 0f, 1f)));
            steps.Add(("one's placement singular", singular));
            var affine = Copy();
            m = start[a].geometryLocalToWorld; m.m30 = 0.1f;
            affine[a] = With(start[a], placement: m);
            steps.Add(("one's placement not affine", affine));
            var lineage = Copy();
            lineage[a] = With(start[a], lineage: Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1.01f, 1f, 1f)) * start[a].lineageToGeometryLocal);
            steps.Add(("one's lineage frame not rigid", lineage));
            var bounds = Copy();
            bounds[b] = With(start[b], bounds: new Bounds(new Vector3(float.NaN, 0f, 0f), start[b].localBounds.size));
            steps.Add(("the last one's box not finite", bounds));
            var both = Copy();
            m = start[b].geometryLocalToWorld; m.m03 = float.PositiveInfinity;
            both[b] = With(start[b], placement: m);
            both[a] = With(start[a], bounds: new Bounds(Vector3.zero, new Vector3(-1f, 1f, 1f) * 2f));
            steps.Add(("two invalid at once (the earlier refusal is the one given)", both));
            var huge = Copy();
            huge[a] = With(start[a], placement: Matrix4x4.TRS(new Vector3(3e37f, 3e37f, 3e37f), Quaternion.identity, new Vector3(3e30f, 3e30f, 3e30f)));
            steps.Add(("one placed where no section can be computed", huge));
            steps.Add(("valid again, after every refusal", Copy()));
            if (start.Count >= 2)
            {
                var swapped = Copy();
                // the same registrations with two of them moved to each other's places (same roots, other values at a slot)
                swapped[a] = With(start[a], placement: start[b].geometryLocalToWorld);
                swapped[b] = With(start[b], placement: start[a].geometryLocalToWorld);
                steps.Add(("two registrations with each other's placements", swapped));
            }

            steps.Add(("as registered, at the end", Copy()));
            return steps;
        }

        [Test]
        public void RememberingWhatPassed_BuildsAndRefusesAsCheckingEverythingEveryTime()
        {
            var inputs = new List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations)>();
            VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 40);
            inputs.Add(("deep 40 (caps), display sets", deep.ledger, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(deep.registrations)));
            (LogicalCutLedger bl, List<VpMultiCutRegistration> br, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(24, 40);
            inputs.Add(("building-like 24 x 40, plain arrays", bl, br));
            int compared = 0, refusals = 0;
            long remembered = 0, placementsRemembered = 0;
            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations) in inputs)
            {
                Matrix4x4 Place(LogicalFragmentId f) => Matrix4x4.TRS(new Vector3(0.01f * f.value, 0f, 0f), Quaternion.Euler(0f, f.value % 7, 0f), Vector3.one);
                // Answers that change from step to step: at rest over some steps, moved at others, and at two steps (whose
                // registrations are valid) one fragment in five answered with what is not a placement -- singular, then not finite.
                int stepNow = 0;
                Matrix4x4 Changing(LogicalFragmentId f)
                {
                    if (stepNow == 3 && f.value % 5 == 0) return Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1f, 1f, 0f));
                    if (stepNow == 13 && f.value % 5 == 0) { Matrix4x4 bad = Place(f); bad.m23 = float.NaN; return bad; }
                    return stepNow == 0 || stepNow == 1 || stepNow == 5 || stepNow == 12 || stepNow == 14
                        ? Place(f)
                        : Matrix4x4.Translate(new Vector3(0f, 0.5f, 0f)) * Place(f);
                }

                var providers = new List<(string name, Func<Answers> make)>
                {
                    ("static", () => new Answers { answer = f => (VpFragmentPlacementKind.Static, default) }),
                    ("following", () => new Answers { answer = f => (VpFragmentPlacementKind.Following, Place(f)) }),
                    ("following, at rest and moved and twice not a placement", () => new Answers { answer = f => (VpFragmentPlacementKind.Following, Changing(f)) }),
                    ("half static, half following", () => new Answers { answer = f => f.value % 2 == 0 ? (VpFragmentPlacementKind.Static, default(Matrix4x4)) : (VpFragmentPlacementKind.Following, Place(f)) }),
                    ("no provider", () => null),
                };
                foreach ((string name, Func<Answers> make) in providers)
                {
                    // One structure, settled over the registrations as registered; then the same sequence of placement-only
                    // builds on one snapshot each way.
                    var outcomes = new List<(VpMultiCutBuildOutcome, VpMultiCutInvalidInput, VpMultiCutShortage, List<string>, int)>[2];
                    for (int way = 0; way < 2; way++)
                    {
                        Old(way == 0);
                        try
                        {
                            var structure = NewSnapshot();
                            Assert.That(structure.TryBuild(ledger, registrations, make()), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                            var s = NewSnapshot();
                            outcomes[way] = new List<(VpMultiCutBuildOutcome, VpMultiCutInvalidInput, VpMultiCutShortage, List<string>, int)>();
                            stepNow = -1;
                            foreach ((string step, List<VpMultiCutRegistration> now) in Steps(registrations))
                            {
                                stepNow++;
                                Answers answers = make();
                                VpMultiCutBuildOutcome o = s.TryBuildPlacementsFrom(structure, ledger, now, answers);
                                outcomes[way].Add((o, s.InvalidInputReason, s.Shortage,
                                    o == VpMultiCutBuildOutcome.Built ? VpMultiCutSnapshotReflectedIndexTests.Contents(s, ledger) : null, answers != null ? answers.asked : 0));
                            }

                            if (way == 1)
                            {
                                remembered += s.ValidationsRemembered;
                                placementsRemembered += s.PlacementChecksRemembered;
                            }
                            else
                            {
                                Assert.That(s.ValidationsRemembered, Is.EqualTo(0), "the old way remembers nothing");
                                Assert.That(s.PlacementChecksRemembered, Is.EqualTo(0), "the old way remembers no placement");
                            }
                        }
                        finally
                        {
                            Old(false);
                        }
                    }

                    List<(string step, List<VpMultiCutRegistration>)> names = Steps(registrations);
                    for (int i = 0; i < names.Count; i++)
                    {
                        string at = what + ", " + name + ", step " + i + " (" + names[i].step + ")";
                        (VpMultiCutBuildOutcome oo, VpMultiCutInvalidInput oi, VpMultiCutShortage os, List<string> oc, int oa) = outcomes[0][i];
                        (VpMultiCutBuildOutcome no, VpMultiCutInvalidInput ni, VpMultiCutShortage ns, List<string> nc, int na) = outcomes[1][i];
                        Assert.That((no, ni, ns), Is.EqualTo((oo, oi, os)), at + ": the same outcome, reason and shortage");
                        Assert.That(na, Is.EqualTo(oa), at + ": the provider asked as often");
                        Assert.That(nc == null, Is.EqualTo(oc == null), at);
                        if (oo != VpMultiCutBuildOutcome.Built) refusals++;
                        compared++;
                        if (oc == null) continue;
                        Assert.That(nc.Count, Is.EqualTo(oc.Count), at + ": as many items");
                        for (int k = 0; k < oc.Count; k++) Assert.That(nc[k], Is.EqualTo(oc[k]), at + ": item " + k);
                    }

                    TestContext.Out.WriteLine(what + ", " + name + ": " + names.Count + " steps the same; outcomes " + string.Join(" ", outcomes[1].ConvertAll(x => x.Item1 == VpMultiCutBuildOutcome.Built ? "built" : x.Item2.ToString())));
                }
            }

            TestContext.Out.WriteLine("steps compared " + compared + ", of them refusals " + refusals + "; registrations passed without being checked again (new way) " + remembered
                + "; Following placements passed without being checked again (new way) " + placementsRemembered);
            Assert.That(refusals, Is.GreaterThan(0), "the sequence holds refusals");
            Assert.That(remembered, Is.GreaterThan(0), "the new way did remember");
            Assert.That(placementsRemembered, Is.GreaterThan(0), "the new way did remember placements");
        }

        private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

        [Test]
        public void Cost_APlacementOnlyBuildWithNothingChanging_OldAndNew()
        {
            // Registrations like a walked city's cut pieces: many, each one piece of its own root cut once, one render
            // fragment each (the saved walk's builds had as many render fragments as registrations, up to 533).
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(0, 532);
            int tick = 0;
            foreach ((string name, Func<Answers> make) in new (string, Func<Answers>)[]
            {
                ("every placement Static", () => new Answers { answer = f => (VpFragmentPlacementKind.Static, default) }),
                ("every placement Following, the same each frame", () => new Answers { answer = f => (VpFragmentPlacementKind.Following, Matrix4x4.TRS(new Vector3(0.01f * f.value, 0f, 0f), Quaternion.identity, Vector3.one)) }),
                ("every placement Following, every one moving each frame", () => new Answers { answer = f => (VpFragmentPlacementKind.Following, Matrix4x4.TRS(new Vector3(0.01f * f.value, 1e-4f * (tick++ % 100000), 0f), Quaternion.identity, Vector3.one)) }),
            })
            {
                var validate = new[] { new List<double>(), new List<double>() };
                var place = new[] { new List<double>(), new List<double>() };
                var whole = new[] { new List<double>(), new List<double>() };
                long checks0 = 0, checks1 = 0, renderFragments = 0;
                for (int round = 0; round < 9; round++)
                {
                    for (int k = 0; k < 2; k++)
                    {
                        int way = (round + k) % 2;   // the order alternates
                        Old(way == 0);
                        try
                        {
                            var structure = NewSnapshot();
                            Assert.That(structure.TryBuild(ledger, registrations, make()), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                            var s = NewSnapshot();
                            Answers answers = make();
                            for (int warm = 0; warm < 3; warm++) Assert.That(s.TryBuildPlacementsFrom(structure, ledger, registrations, answers), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                            const int frames = 60;
                            double v0 = s.ValidateCounts.placementInputSeconds, p0 = s.PlacementOnlyPlaceCounts.seconds;
                            long c0 = s.PlacementOnlyPlaceCounts.placementChecks, r0 = s.PlacementOnlyPlaceCounts.renderFragments;
                            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                            for (int f = 0; f < frames; f++) s.TryBuildPlacementsFrom(structure, ledger, registrations, answers);
                            double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - begin) / (double)System.Diagnostics.Stopwatch.Frequency;
                            validate[way].Add((s.ValidateCounts.placementInputSeconds - v0) * 1e6 / frames);
                            place[way].Add((s.PlacementOnlyPlaceCounts.seconds - p0) * 1e6 / frames);
                            whole[way].Add(seconds * 1e6 / frames);
                            renderFragments = (s.PlacementOnlyPlaceCounts.renderFragments - r0) / frames;
                            if (way == 0) checks0 = (s.PlacementOnlyPlaceCounts.placementChecks - c0) / frames; else checks1 = (s.PlacementOnlyPlaceCounts.placementChecks - c0) / frames;
                        }
                        finally
                        {
                            Old(false);
                        }
                    }
                }

                TestContext.Out.WriteLine(name + " (" + registrations.Count + " registrations, " + renderFragments + " render fragments a build), microseconds a build, medians of 9, old -> new: validation "
                    + Median(validate[0]).ToString("F1") + " -> " + Median(validate[1]).ToString("F1") + "; place " + Median(place[0]).ToString("F1") + " -> " + Median(place[1]).ToString("F1")
                    + "; the whole build " + Median(whole[0]).ToString("F1") + " -> " + Median(whole[1]).ToString("F1") + "; placement checks a build " + checks0 + " -> " + checks1);
            }
        }
    }
}
