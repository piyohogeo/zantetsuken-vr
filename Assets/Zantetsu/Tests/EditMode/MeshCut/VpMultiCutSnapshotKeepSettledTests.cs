using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The per-frame placement-only build's repeated work on what did not move (TL, 2026-10-05): a render fragment taken
    /// over from the structure with nothing selected, standing bit for bit where the structure placed it, is kept as it
    /// was copied instead of being placed and built again. Against placing and building every one every time (the pass
    /// before): over a chain of placement-only builds made the way a display makes them -- two snapshots, each built from
    /// the one built before -- with answers at rest, moved, moved back, differing only in the sign of a zero, missing,
    /// and not a placement, the same outcome, reason and shortage at every step, the same snapshot item by item and bit
    /// by bit in every placement, and the provider asked as often. Then the same binary's cost, rebuilt and kept.
    /// Written out, not judged.
    /// </summary>
    public class VpMultiCutSnapshotKeepSettledTests
    {
        private sealed class Answers : IVpFragmentPlacement
        {
            public Func<LogicalFragmentId, (VpFragmentPlacementKind kind, Matrix4x4 m)> answer;
            public int asked;

            // What was asked of, and what was answered, in the order asked (one question a render fragment, in their order).
            public readonly List<LogicalFragmentId> askedOf = new List<LogicalFragmentId>();
            public readonly List<Matrix4x4> given = new List<Matrix4x4>();

            public VpFragmentPlacementKind TryGetGeometryLocalToWorld(LogicalFragmentId fragment, CutOperationId operation, float side, out Matrix4x4 geometryLocalToWorld)
            {
                asked++;
                (VpFragmentPlacementKind kind, Matrix4x4 m) = answer(fragment);
                askedOf.Add(fragment);
                given.Add(m);
                geometryLocalToWorld = m;
                return kind;
            }
        }

        private static VpMultiCutSnapshot NewSnapshot() => new VpMultiCutSnapshot(new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256));

        private static int Bits(float f) => BitConverter.ToInt32(BitConverter.GetBytes(f), 0);

        private static List<int> PlacementBits(VpMultiCutSnapshot s)
        {
            var bits = new List<int>(s.RenderFragmentCount * 16);
            for (int r = 0; r < s.RenderFragmentCount; r++)
            {
                Assert.That(s.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                for (int i = 0; i < 16; i++) bits.Add(Bits(rf.geometryLocalToWorld[i]));
            }

            return bits;
        }

        private static Matrix4x4 Place(LogicalFragmentId f) =>
            Matrix4x4.TRS(new Vector3(0.01f * f.value, 0f, 0.25f), Quaternion.Euler(0f, f.value % 7, 0f), Vector3.one);

        private const int MissingStep = 7, NotAPlacementStep = 9, MinusZeroStep = 5, Steps = 13;

        // What a fragment is answered at a step of the chain: at rest over some steps, some moved at others, moved back,
        // every one with a zero's sign turned (the same value, other bits), one in five missing, one in five singular.
        private static (VpFragmentPlacementKind, Matrix4x4) Following(int step, LogicalFragmentId f)
        {
            Matrix4x4 m = Place(f);
            switch (step)
            {
                case 2:
                case 3:
                    if (f.value % 5 == 0) m = Matrix4x4.Translate(new Vector3(0f, 0.5f, 0f)) * m;
                    break;
                case MinusZeroStep:
                    m.m13 = -0f;
                    break;
                case MissingStep:
                    if (f.value % 5 == 0) return (VpFragmentPlacementKind.Missing, default);
                    break;
                case NotAPlacementStep:
                    if (f.value % 5 == 0) m = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1f, 1f, 0f));
                    break;
            }

            return (VpFragmentPlacementKind.Following, m);
        }

        private sealed class Step
        {
            public VpMultiCutBuildOutcome outcome;
            public VpMultiCutInvalidInput reason;
            public VpMultiCutShortage shortage;
            public List<string> contents;
            public List<int> bits;
            public int asked;
            public int renderFragments;
        }

        // The chain a display makes: the candidate is built from the adopted snapshot's structure and, when built, becomes
        // the adopted one; a refused build leaves the adopted one as it was.
        private static List<Step> Chain(
            LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, Func<int, Answers> make, bool rebuild, out long kept, out long placed,
            Func<VpMultiCutSnapshot> newSnapshot = null)
        {
            VpMultiCutSnapshot.placementRebuildUnchangedForTest = rebuild;
            VpMultiCutSnapshot adopted = null, building = null;
            try
            {
                var steps = new List<Step>();
                adopted = newSnapshot != null ? newSnapshot() : NewSnapshot();
                building = newSnapshot != null ? newSnapshot() : NewSnapshot();
                Assert.That(adopted.TryBuild(ledger, registrations, make(0)), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                for (int step = 1; step < Steps; step++)
                {
                    // Step 11: one registration's own placement moved (what a Static placement and no provider draw at).
                    List<VpMultiCutRegistration> now = registrations;
                    if (step == 11)
                    {
                        now = new List<VpMultiCutRegistration>(registrations);
                        VpMultiCutRegistration g = now[now.Count / 3];
                        now[now.Count / 3] = new VpMultiCutRegistration(
                            g.root, g.localBounds, Matrix4x4.Translate(new Vector3(0.5f, 0f, 0f)) * g.geometryLocalToWorld, g.lineageToGeometryLocal, g.reflected, g.vertexEpsilon);
                    }

                    Answers answers = make(step);
                    VpMultiCutBuildOutcome o = building.TryBuildPlacementsFrom(adopted, ledger, now, answers);
                    bool built = o == VpMultiCutBuildOutcome.Built;
                    steps.Add(new Step
                    {
                        outcome = o,
                        reason = building.InvalidInputReason,
                        shortage = building.Shortage,
                        contents = built ? VpMultiCutSnapshotReflectedIndexTests.Contents(building, ledger) : null,
                        bits = built ? PlacementBits(building) : null,
                        asked = answers != null ? answers.asked : 0,
                        renderFragments = built ? building.RenderFragmentCount : 0,
                    });
                    if (built)
                    {
                        (adopted, building) = (building, adopted);
                    }
                }

                kept = adopted.PlacementsKeptAsSettled + building.PlacementsKeptAsSettled;
                placed = adopted.PlacementOnlyPlaceCounts.renderFragments + building.PlacementOnlyPlaceCounts.renderFragments;
                return steps;
            }
            finally
            {
                VpMultiCutSnapshot.placementRebuildUnchangedForTest = false;
                if (adopted != null && adopted.IsOnBacking) adopted.Dispose();
                if (building != null && building.IsOnBacking) building.Dispose();
            }
        }

        [Test]
        public void OnRoomsOfReservedAddressSpace_AsTheProductsSnapshotsStand_KeptAndBuiltAgainAreTheSame()
        {
            // The product's snapshots hold their numbers on reserved address space (VpMultiCutSnapshot.TryCreateOnBacking):
            // the render fragments compared and kept here are read through that room, not through a managed array.
            var capacities = new VpMultiCutCapacities(16384, 131072, 16384, 262144, 256);
            var pages = new CountingPageBacking { GranularityBytes = 65536 };
            int made = 0;
            VpMultiCutSnapshot Native()
            {
                Assert.That(VpMultiCutSnapshot.TryCreateOnBacking(pages, capacities, capacities, out VpMultiCutSnapshot s, out string failure), Is.True, failure);
                Assert.That(s.IsOnBacking, Is.True);
                made++;
                return s;
            }

            var inputs = new List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations)>();
            (LogicalCutLedger bl, List<VpMultiCutRegistration> br, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(24, 40);
            inputs.Add(("building-like 24 x 40", bl, br));
            VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 40);
            inputs.Add(("deep 40 (caps), display sets", deep.ledger, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(deep.registrations)));
            long keptInAll = 0;
            int refusals = 0, compared = 0;
            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations) in inputs)
            {
                // The chain's steps: at rest, one in five moved, the same again, back, a zero's sign turned, back, one in five
                // missing (refused), at rest after the refusal, one in five not a placement (refused), at rest, a
                // registration's own placement moved, back -- the two snapshots used in turn throughout.
                Func<int, Answers> make = step => new Answers { answer = f => Following(step, f) };
                List<Step> rebuilt = Chain(ledger, registrations, make, true, out long keptWhenRebuilding, out _, Native);
                List<Step> kept = Chain(ledger, registrations, make, false, out long keptNow, out long placedNow, Native);
                List<Step> managed = Chain(ledger, registrations, make, false, out long keptManaged, out _);
                Assert.That(keptWhenRebuilding, Is.EqualTo(0));
                Assert.That(keptNow, Is.EqualTo(keptManaged), what + ": as many kept on reserved rooms as on managed ones");
                for (int i = 0; i < rebuilt.Count; i++)
                {
                    string at = what + ", step " + (i + 1);
                    Step o = rebuilt[i], n = kept[i], m = managed[i];
                    Assert.That((n.outcome, n.reason, n.shortage), Is.EqualTo((o.outcome, o.reason, o.shortage)), at + ": the same outcome, reason and shortage");
                    Assert.That((m.outcome, m.reason, m.shortage), Is.EqualTo((o.outcome, o.reason, o.shortage)), at + ": and as on managed rooms");
                    Assert.That(n.asked, Is.EqualTo(o.asked), at + ": the provider asked as often");
                    compared++;
                    if (o.outcome != VpMultiCutBuildOutcome.Built)
                    {
                        refusals++;
                        Assert.That(n.contents, Is.Null);
                        continue;
                    }

                    Assert.That(n.contents.Count, Is.EqualTo(o.contents.Count), at + ": as many items");
                    for (int k = 0; k < o.contents.Count; k++) Assert.That(n.contents[k], Is.EqualTo(o.contents[k]), at + ": item " + k);
                    Assert.That(n.bits, Is.EqualTo(o.bits), at + ": every placement bit for bit");
                    Assert.That(m.contents.Count, Is.EqualTo(o.contents.Count), at + ": as many items as on managed rooms");
                    for (int k = 0; k < o.contents.Count; k++) Assert.That(m.contents[k], Is.EqualTo(o.contents[k]), at + ": item " + k + " as on managed rooms");
                    Assert.That(m.bits, Is.EqualTo(o.bits), at + ": every placement bit for bit as on managed rooms");
                }

                keptInAll += keptNow;
                TestContext.Out.WriteLine("NATIVEKEEP " + what + ": " + rebuilt.Count + " steps the same on reserved rooms, built again and kept, and the same as on managed rooms; render fragments placed "
                    + placedNow + ", kept " + keptNow + "; outcomes " + string.Join(" ", kept.ConvertAll(x => x.outcome == VpMultiCutBuildOutcome.Built ? "built" : x.reason.ToString())));
            }

            Assert.That(refusals, Is.GreaterThan(0), "the chains hold refusals, and go on after them");
            Assert.That(keptInAll, Is.GreaterThan(0), "render fragments were kept on reserved rooms");
            Assert.That(made, Is.EqualTo(8), "two snapshots a chain, two chains an input on reserved rooms");
            Assert.That(pages.Live, Is.EqualTo(0), "every reservation given back");
            Assert.That(pages.ReleasedTwice, Is.EqualTo(0));
            TestContext.Out.WriteLine("NATIVEKEEP steps compared " + compared + ", of them refusals " + refusals + "; kept in all " + keptInAll + "; reservations made " + pages.Reserves + ", given back " + pages.Releases);
        }

        [Test]
        public void ARenderFragmentTakenOverWhereItStood_IsKept_AndEveryBuildIsWhatPlacingAndBuildingAgainGives()
        {
            var inputs = new List<(string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations)>();
            VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(10, 40);
            inputs.Add(("deep 40 (caps), display sets", deep.ledger, VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(deep.registrations)));
            (LogicalCutLedger bl, List<VpMultiCutRegistration> br, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(24, 40);
            inputs.Add(("building-like 24 x 40, plain arrays", bl, br));
            (LogicalCutLedger pl, List<VpMultiCutRegistration> pr, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(0, 60);
            inputs.Add(("pieces 60 (one render fragment each)", pl, pr));
            int compared = 0, refusals = 0;
            long keptInAll = 0;
            foreach ((string what, LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations) in inputs)
            {
                var providers = new List<(string name, Func<int, Answers> make)>
                {
                    ("static", step => new Answers { answer = f => step == MissingStep && f.value % 5 == 0 ? (VpFragmentPlacementKind.Missing, default(Matrix4x4)) : (VpFragmentPlacementKind.Static, default(Matrix4x4)) }),
                    ("following", step => new Answers { answer = f => Following(step, f) }),
                    ("half static, half following", step => new Answers { answer = f => f.value % 2 == 0 ? (VpFragmentPlacementKind.Static, default(Matrix4x4)) : Following(step, f) }),
                    ("no provider", step => null),
                };
                foreach ((string name, Func<int, Answers> make) in providers)
                {
                    List<Step> rebuilt = Chain(ledger, registrations, make, true, out long keptWhenRebuilding, out _);
                    List<Step> kept = Chain(ledger, registrations, make, false, out long keptNow, out long placedNow);
                    Assert.That(keptWhenRebuilding, Is.EqualTo(0), what + ", " + name + ": the pass before keeps nothing");
                    Assert.That(kept.Count, Is.EqualTo(rebuilt.Count));
                    int builtSteps = 0;
                    for (int i = 0; i < rebuilt.Count; i++)
                    {
                        string at = what + ", " + name + ", step " + (i + 1);
                        Step o = rebuilt[i], n = kept[i];
                        Assert.That((n.outcome, n.reason, n.shortage), Is.EqualTo((o.outcome, o.reason, o.shortage)), at + ": the same outcome, reason and shortage");
                        Assert.That(n.asked, Is.EqualTo(o.asked), at + ": the provider asked as often");
                        Assert.That(n.contents == null, Is.EqualTo(o.contents == null), at);
                        compared++;
                        if (o.outcome != VpMultiCutBuildOutcome.Built)
                        {
                            refusals++;
                            continue;
                        }

                        builtSteps++;
                        Assert.That(n.contents.Count, Is.EqualTo(o.contents.Count), at + ": as many items");
                        for (int k = 0; k < o.contents.Count; k++) Assert.That(n.contents[k], Is.EqualTo(o.contents[k]), at + ": item " + k);
                        Assert.That(n.bits, Is.EqualTo(o.bits), at + ": every placement bit for bit");
                    }

                    keptInAll += keptNow;
                    TestContext.Out.WriteLine(what + ", " + name + ": " + rebuilt.Count + " steps the same (" + builtSteps + " built); render fragments placed " + placedNow + ", of them kept as taken over "
                        + keptNow + "; outcomes " + string.Join(" ", kept.ConvertAll(x => x.outcome == VpMultiCutBuildOutcome.Built ? "built" : x.reason.ToString())));
                }
            }

            TestContext.Out.WriteLine("steps compared " + compared + ", of them refusals " + refusals + "; render fragments kept in all " + keptInAll);
            Assert.That(refusals, Is.GreaterThan(0), "the chains hold refusals");
            Assert.That(keptInAll, Is.GreaterThan(0), "render fragments were kept");
        }

        [Test]
        public void WhatIsKept_IsOnlyWhatStandsBitForBitWhereItStood_WithNothingSelected()
        {
            // Pieces with nothing selected, one render fragment each.
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(0, 60);
            VpMultiCutSnapshot adopted = NewSnapshot(), building = NewSnapshot();
            Assert.That(adopted.TryBuild(ledger, registrations, new Answers { answer = f => Following(0, f) }), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            int n = adopted.RenderFragmentCount;

            // At rest: every one kept, every one asked.
            var rest = new Answers { answer = f => Following(1, f) };
            Assert.That(building.TryBuildPlacementsFrom(adopted, ledger, registrations, rest), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(building.PlacementsKeptAsSettled, Is.EqualTo(n), "at rest every render fragment is kept");
            Assert.That(rest.asked, Is.EqualTo(n), "and every one was asked where it stands");
            Assert.That(PlacementBits(building), Is.EqualTo(PlacementBits(adopted)));

            // One in five moved: those are placed and built again, the others kept.
            (adopted, building) = (building, adopted);
            long before = building.PlacementsKeptAsSettled;
            var some = new Answers { answer = f => Following(2, f) };
            Assert.That(building.TryBuildPlacementsFrom(adopted, ledger, registrations, some), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(some.asked, Is.EqualTo(n));
            int moved = some.askedOf.FindAll(f => f.value % 5 == 0).Count;
            Assert.That(moved, Is.GreaterThan(0));
            Assert.That(moved, Is.LessThan(n));
            Assert.That(building.PlacementsKeptAsSettled - before, Is.EqualTo(n - moved), "only what did not move is kept");
            for (int r = 0; r < n; r++)
            {
                Assert.That(building.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                Matrix4x4 expected = some.given[r];
                for (int i = 0; i < 16; i++) Assert.That(Bits(rf.geometryLocalToWorld[i]), Is.EqualTo(Bits(expected[i])), "render fragment " + r + " stands where it was answered");
            }

            // The same value with other bits (a zero's sign): not kept, and what is written is the answer's own bits.
            (adopted, building) = (building, adopted);
            before = building.PlacementsKeptAsSettled;
            Assert.That(building.TryBuildPlacementsFrom(adopted, ledger, registrations, new Answers { answer = f => Following(4, f) }), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            (adopted, building) = (building, adopted);
            before = building.PlacementsKeptAsSettled;
            Assert.That(building.TryBuildPlacementsFrom(adopted, ledger, registrations, new Answers { answer = f => Following(MinusZeroStep, f) }), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(building.PlacementsKeptAsSettled - before, Is.EqualTo(0), "a zero of the other sign is another placement, bit for bit");
            for (int r = 0; r < n; r++)
            {
                Assert.That(building.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                Assert.That(Bits(rf.geometryLocalToWorld.m13), Is.EqualTo(Bits(-0f)), "render fragment " + r + " holds the answer's own zero");
            }

            // The ordinary build (the structure settled again) keeps nothing: there is nothing taken over to keep.
            var again = NewSnapshot();
            Assert.That(again.TryBuild(ledger, registrations, adopted, new Answers { answer = f => Following(MinusZeroStep, f) }), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(again.PlacementsKeptAsSettled, Is.EqualTo(0));

            // With something selected (caps), nothing is kept even at rest: those are built every time, as before.
            VpMultiCutSnapshotReflectedIndexTests.Input deep = VpMultiCutSnapshotReflectedIndexTests.Deep(4, 12);
            List<VpMultiCutRegistration> sets = VpMultiCutSnapshotAncestorWalkTests.WithDisplaySets(deep.registrations);
            VpMultiCutSnapshot a = NewSnapshot(), b = NewSnapshot();
            Assert.That(a.TryBuild(deep.ledger, sets, new Answers { answer = f => Following(0, f) }), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            Assert.That(b.TryBuildPlacementsFrom(a, deep.ledger, sets, new Answers { answer = f => Following(1, f) }), Is.EqualTo(VpMultiCutBuildOutcome.Built));
            int selected = 0, plain = 0;
            for (int r = 0; r < b.RenderFragmentCount; r++)
            {
                Assert.That(b.TryGetRenderFragment(r, out VpMultiCutRenderFragment rf), Is.True);
                if (rf.conditionCount > 0) selected++; else plain++;
            }

            Assert.That(selected, Is.GreaterThan(0), "the input holds render fragments with something selected");
            Assert.That(b.PlacementsKeptAsSettled, Is.EqualTo(plain), "only those with nothing selected are kept");
            TestContext.Out.WriteLine("pieces: " + n + " render fragments, at rest kept " + n + ", one in five moved kept " + (n - moved) + ", a zero's sign turned kept 0; with caps: " + selected
                + " with something selected built again, " + plain + " kept");
        }

        private static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

        [Test]
        public void Cost_PlacingWhatDidNotMove_BuiltAgainAndKept()
        {
            // Registrations like a walked city's cut pieces: many, one render fragment each, nothing selected (the saved
            // walk r1a: 317 render fragments a placement-only pass on average, up to 563; no plane, section or cap in any).
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(0, 562);
            int tick = 0;
            foreach ((string name, Func<Answers> make) in new (string, Func<Answers>)[]
            {
                ("every placement Static", () => new Answers { answer = f => (VpFragmentPlacementKind.Static, default) }),
                ("every placement Following, the same each frame", () => new Answers { answer = f => (VpFragmentPlacementKind.Following, Matrix4x4.TRS(new Vector3(0.01f * f.value, 0f, 0f), Quaternion.identity, Vector3.one)) }),
                ("every placement Following, one in eleven moving each frame", () => new Answers { answer = f => (VpFragmentPlacementKind.Following, Matrix4x4.TRS(new Vector3(0.01f * f.value, f.value % 11 == 0 ? 1e-4f * (tick++ % 100000) : 0f, 0f), Quaternion.identity, Vector3.one)) }),
                ("every placement Following, every one moving each frame", () => new Answers { answer = f => (VpFragmentPlacementKind.Following, Matrix4x4.TRS(new Vector3(0.01f * f.value, 1e-4f * (tick++ % 100000), 0f), Quaternion.identity, Vector3.one)) }),
            })
            {
                var place = new[] { new List<double>(), new List<double>() };
                var whole = new[] { new List<double>(), new List<double>() };
                long keptABuild = 0, renderFragments = 0;
                for (int round = 0; round < 9; round++)
                {
                    for (int k = 0; k < 2; k++)
                    {
                        int way = (round + k) % 2;   // the order alternates; 0 = built again, 1 = kept
                        VpMultiCutSnapshot.placementRebuildUnchangedForTest = way == 0;
                        try
                        {
                            VpMultiCutSnapshot adopted = NewSnapshot(), building = NewSnapshot();
                            Answers answers = make();
                            Assert.That(adopted.TryBuild(ledger, registrations, answers), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                            for (int warm = 0; warm < 4; warm++)
                            {
                                Assert.That(building.TryBuildPlacementsFrom(adopted, ledger, registrations, answers), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                                (adopted, building) = (building, adopted);
                            }

                            const int frames = 60;
                            double p0 = adopted.PlacementOnlyPlaceCounts.seconds + building.PlacementOnlyPlaceCounts.seconds;
                            long r0 = adopted.PlacementOnlyPlaceCounts.renderFragments + building.PlacementOnlyPlaceCounts.renderFragments;
                            long k0 = adopted.PlacementsKeptAsSettled + building.PlacementsKeptAsSettled;
                            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                            for (int f = 0; f < frames; f++)
                            {
                                building.TryBuildPlacementsFrom(adopted, ledger, registrations, answers);
                                (adopted, building) = (building, adopted);
                            }

                            double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - begin) / (double)System.Diagnostics.Stopwatch.Frequency;
                            place[way].Add((adopted.PlacementOnlyPlaceCounts.seconds + building.PlacementOnlyPlaceCounts.seconds - p0) * 1e6 / frames);
                            whole[way].Add(seconds * 1e6 / frames);
                            renderFragments = (adopted.PlacementOnlyPlaceCounts.renderFragments + building.PlacementOnlyPlaceCounts.renderFragments - r0) / frames;
                            if (way == 1) keptABuild = (adopted.PlacementsKeptAsSettled + building.PlacementsKeptAsSettled - k0) / frames;
                        }
                        finally
                        {
                            VpMultiCutSnapshot.placementRebuildUnchangedForTest = false;
                        }
                    }
                }

                TestContext.Out.WriteLine("KEEPCOST " + name + " (" + registrations.Count + " registrations, " + renderFragments + " render fragments a build, kept a build " + keptABuild
                    + "), microseconds a build, medians of 9, built again -> kept: place " + Median(place[0]).ToString("F1") + " -> " + Median(place[1]).ToString("F1")
                    + "; the whole build " + Median(whole[0]).ToString("F1") + " -> " + Median(whole[1]).ToString("F1") + " (Editor)");
            }
        }
    }
}
