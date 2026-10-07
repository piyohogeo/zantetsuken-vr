#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The numeric input-contract diagnosis switched off for a Development Player by its launch argument (TL,
    /// 2026-10-07; DESIGN 5.6, D-211). On is the default and is what <see cref="VpNumericContractDiagnosisTests"/>
    /// holds. Here: the argument's reading; with the diagnosis off, that nothing of it runs -- no check, nothing
    /// remembered or compared for one, no room, none of its counts or times; that input inside the contract gives
    /// the same snapshot either way; that a number outside the contract is no longer refused by the diagnosis; and
    /// that what is not the diagnosis -- capacity, the structure, a placement not said, the cut plane's own check --
    /// refuses exactly as it did. A snapshot takes the setting when it is made, so each case makes its snapshots
    /// under the setting it means and puts the process's own back at once.
    /// </summary>
    public class VpNumericDiagnosisSwitchTests
    {
        [TearDown]
        public void TheProcessOwnSettingAgain() => VpNumericDiagnosis.ResetForTest();

        private sealed class Answers : IVpFragmentPlacement
        {
            public Func<LogicalFragmentId, Matrix4x4> answer;
            public VpFragmentPlacementKind kind = VpFragmentPlacementKind.Following;

            public VpFragmentPlacementKind TryGetGeometryLocalToWorld(LogicalFragmentId fragment, CutOperationId operation, float side, out Matrix4x4 geometryLocalToWorld)
            {
                geometryLocalToWorld = kind == VpFragmentPlacementKind.Missing ? default : answer(fragment);
                return kind;
            }
        }

        // A snapshot made while the diagnosis is as said; the process's own setting is back when this returns.
        private static VpMultiCutSnapshot Make(bool diagnosing, Func<VpMultiCutSnapshot> make = null)
        {
            VpNumericDiagnosis.SetForTest(diagnosing);
            try
            {
                return make != null ? make() : new VpMultiCutSnapshot(new VpMultiCutCapacities(256, 2048, 256, 4096, 64));
            }
            finally
            {
                VpNumericDiagnosis.ResetForTest();
            }
        }

        private static VpMultiCutRegistration With(VpMultiCutRegistration r, Bounds? bounds = null, Matrix4x4? placement = null, Matrix4x4? lineage = null) =>
            new VpMultiCutRegistration(r.root, bounds ?? r.localBounds, placement ?? r.geometryLocalToWorld, lineage ?? r.lineageToGeometryLocal, r.reflected, r.vertexEpsilon);

        private static bool ByTheDiagnosis(VpMultiCutBuildOutcome outcome, VpMultiCutSnapshot snapshot) =>
            outcome == VpMultiCutBuildOutcome.InvalidInput
            && (snapshot.InvalidInputReason == VpMultiCutInvalidInput.InputContract || snapshot.InvalidInputReason == VpMultiCutInvalidInput.ConservativeSection
                || snapshot.InvalidInputReason == VpMultiCutInvalidInput.DrawnCapVertex);

        [Test]
        public void TheArgument_TurnsTheDiagnosisOffForADevelopmentPlayer_TheEditorDiagnosesByDefault_AndTheLaunchRecordSaysWhich()
        {
            Assert.That(VpNumericDiagnosis.Enabled, Is.True, "the Editor: on, whatever it was started with");
            Assert.That(VpNumericDiagnosis.DisableArgument, Is.EqualTo("-zantetsuDisableNumericDiagnostics"));
            Assert.That(VpNumericDiagnosis.EnabledFor(new[] { "Player.exe", "-logFile", "x.log" }), Is.True, "a Development Player with no argument: on");
            Assert.That(VpNumericDiagnosis.EnabledFor(new[] { "Player.exe", "-zantetsuDisableNumericDiagnostics" }), Is.False, "with the argument: off");
            Assert.That(VpNumericDiagnosis.EnabledFor(new[] { "Player.exe", "-ZANTETSUdisableNUMERICdiagnostics", "-logFile", "x.log" }), Is.False, "whatever its case");
            Assert.That(VpNumericDiagnosis.EnabledFor(new[] { "Player.exe", "-zantetsuDisableNumericDiagnosticsToo", "zantetsuDisableNumericDiagnostics" }), Is.True, "another word is not it");

            string[] line = { "Player.exe", "-deepprofiling", "-zantetsuPropMarkers", "a;b" };
            string on = SandboxLaunchRecord.Describe(line);
            TestContext.Out.WriteLine(on);
            Assert.That(on, Does.Contain("build=Editor"), "which build");
            Assert.That(on, Does.Contain("numeric diagnosis=on"), "the diagnosis");
            Assert.That(on, Does.Contain("profiler: supported=").And.Contain(", enabled=").And.Contain("binary log=").And.Contain("-deepprofiling=given").And.Contain("-profiler-enable=not given"), "the Profiler's settings");
            Assert.That(on, Does.Contain("buildGuid=").And.Contain("command line: Player.exe -deepprofiling -zantetsuPropMarkers a;b"), "the build's id and the command line");

            VpNumericDiagnosis.SetForTest(false);
            Assert.That(VpNumericDiagnosis.Enabled, Is.False);
            string off = SandboxLaunchRecord.Describe(new[] { "Player.exe", "-zantetsuDisableNumericDiagnostics" });
            TestContext.Out.WriteLine(off);
            Assert.That(off, Does.Contain("numeric diagnosis=off (-zantetsuDisableNumericDiagnostics)"), "off is said, with what set it");
            Assert.That(off, Does.Contain("-deepprofiling=not given"));
            VpNumericDiagnosis.ResetForTest();
            Assert.That(VpNumericDiagnosis.Enabled, Is.True, "the process's own again");
        }

        // What the diagnosis left behind in a snapshot: its counts, its times, what it remembered and its room.
        private static string Trace(VpMultiCutSnapshot s) =>
            "placement-only validations " + s.ValidateCounts.placementOnly + " (registrations " + s.ValidateCounts.placementRegistrations + "), remembered " + s.ValidationsRemembered
            + ", answers checked " + (s.StructuralPlaceCounts.placementChecks + s.PlacementOnlyPlaceCounts.placementChecks) + ", answers remembered " + s.PlacementChecksRemembered
            + ", times s: contract " + s.ValidateCounts.contractSeconds.ToString("R") + ", placement input " + s.ValidateCounts.placementInputSeconds.ToString("R") + ", placement contract "
            + s.ValidateCounts.placementContractSeconds.ToString("R") + ", room " + s.NumericDiagnosisRoomForTest;

        [Test]
        public void Off_NothingOfTheDiagnosisRuns_NoCheckNothingRememberedOrComparedNoRoomNoCountNoTime_AndTheSnapshotIsTheSame()
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(4, 6);
            Matrix4x4 Place(LogicalFragmentId f, int k) => Matrix4x4.TRS(new Vector3(0.01f * f.value, 0.001f * k, 0f), Quaternion.Euler(0f, 3f * k, 0f), Vector3.one);
            var built = new VpMultiCutSnapshot[2];
            for (int way = 0; way < 2; way++)
            {
                bool diagnosing = way == 0;
                string what = diagnosing ? "diagnosing" : "the diagnosis off";
                VpMultiCutSnapshot structure = Make(diagnosing), placed = Make(diagnosing);
                Assert.That(structure.TryBuild(ledger, registrations, new Answers { answer = f => Place(f, 0) }), Is.EqualTo(VpMultiCutBuildOutcome.Built), what + ": the structure");
                for (int k = 0; k < 4; k++)
                {
                    // k = 0 and 1 answer the same placements (a check may pass on what it remembers), then they move.
                    int at = Math.Max(0, k - 1);
                    Assert.That(placed.TryBuildPlacementsFrom(structure, ledger, registrations, new Answers { answer = f => Place(f, at) }), Is.EqualTo(VpMultiCutBuildOutcome.Built), what + ": placement-only build " + k);
                }

                // The counts' own lines say what the process is set to (one setting a process: a Player's never changes).
                VpNumericDiagnosis.SetForTest(diagnosing);
                string validateLine = placed.ValidateCounts.Describe(), placeLine = placed.PlacementOnlyPlaceCounts.Describe();
                VpNumericDiagnosis.ResetForTest();
                TestContext.Out.WriteLine(what + " -- the structure's snapshot: " + Trace(structure));
                TestContext.Out.WriteLine(what + " -- the placed snapshot:      " + Trace(placed));
                TestContext.Out.WriteLine(what + " -- " + validateLine);
                TestContext.Out.WriteLine(what + " -- " + placeLine);
                if (diagnosing)
                {
                    Assert.That(placed.ValidateCounts.placementOnly, Is.EqualTo(4), "a validation at each placement-only build");
                    Assert.That(placed.ValidateCounts.placementRegistrations, Is.EqualTo(4L * registrations.Count));
                    Assert.That(placed.ValidationsRemembered, Is.EqualTo(3L * registrations.Count), "the registrations it passed before, compared and not checked again");
                    Assert.That(placed.StructuralPlaceCounts.placementChecks + placed.PlacementOnlyPlaceCounts.placementChecks, Is.GreaterThan(0), "answers checked");
                    Assert.That(placed.PlacementChecksRemembered, Is.GreaterThan(0), "and an answer that came again passed on what was remembered");
                    Assert.That(placed.NumericDiagnosisRoomForTest.validated, Is.GreaterThanOrEqualTo(registrations.Count), "room for what it remembers");
                    Assert.That(placed.NumericDiagnosisRoomForTest.placed, Is.GreaterThanOrEqualTo(placed.RenderFragmentCount));
                    Assert.That(validateLine, Does.Contain("placement only 4 (registrations").And.Not.Contain("not run"));
                }
                else
                {
                    foreach (VpMultiCutSnapshot s in new[] { structure, placed })
                    {
                        Assert.That(new[] { s.ValidateCounts.placementOnly, s.ValidateCounts.placementRegistrations, s.ValidationsRemembered, s.PlacementChecksRemembered,
                                            s.StructuralPlaceCounts.placementChecks, s.PlacementOnlyPlaceCounts.placementChecks },
                            Is.EqualTo(new long[] { 0, 0, 0, 0, 0, 0 }), "no validation of the placement inputs, nothing compared with what was remembered, no answer checked");
                        Assert.That(new[] { s.ValidateCounts.contractSeconds, s.ValidateCounts.placementInputSeconds, s.ValidateCounts.placementContractSeconds,
                                            s.StructuralPlaceCounts.checkSeconds, s.PlacementOnlyPlaceCounts.checkSeconds },
                            Is.EqualTo(new[] { 0.0, 0.0, 0.0, 0.0, 0.0 }), "none of its times taken");
                        Assert.That(s.NumericDiagnosisRoomForTest, Is.EqualTo((0, 0)), "no room made for what it would remember");
                    }

                    Assert.That(placed.ValidateCounts.structural + structure.ValidateCounts.structural, Is.GreaterThan(0), "the structure's validation still ran and is counted");
                    Assert.That(structure.ValidateCounts.inputSeconds + structure.ValidateCounts.ancestorSeconds + structure.ValidateCounts.operationsSeconds, Is.GreaterThan(0.0), "and timed");
                    Assert.That(placed.PlacementOnlyPlaceCounts.passes, Is.EqualTo(4), "the placement passes are counted");
                    Assert.That(placed.PlacementOnlyPlaceCounts.seconds, Is.GreaterThan(0.0), "and timed");
                    Assert.That(placed.PlacementOnlyPlaceCounts.queries, Is.GreaterThan(0), "and their queries");

                    // A diagnosis that did not run is said so, not written as 0.
                    Assert.That(validateLine, Does.Contain("contract not run").And.Contain("placement only not run (numeric diagnosis off)"));
                    Assert.That(placeLine, Does.Contain("placement checks not run (numeric diagnosis off)").And.Not.Contain("placement checks 0"));
                }

                built[way] = placed;
            }

            // Input inside the contract: the same snapshot either way -- the registrations of the building-like
            // lineage, and one body cut in two with its two caps, placed by a query's answer.
            AssertSameSnapshot(built[0], built[1], "the registrations");
            Assert.That(built[0].RenderFragmentCount, Is.EqualTo(registrations.Count));
            Bounds box = VpMultiCutSnapshotTests.k_box;
            Matrix4x4 stands = Matrix4x4.TRS(new Vector3(3f, -2f, 5f), Quaternion.Euler(10f, 80f, -5f), Vector3.one);
            var cut = new VpMultiCutSnapshot[2];
            for (int way = 0; way < 2; way++)
            {
                LogicalCutLedger one = VpMultiCutSnapshotTests.NewLedger();
                LogicalFragmentId root = one.AddFragment();
                var plane = new float4(0.2f, 1f, 0.1f, -0.1f);
                VpMultiCutSnapshotTests.Cut(one, root, plane / math.length(plane.xyz));
                cut[way] = Make(way == 0, () => VpMultiCutSnapshotTests.NewSnapshot());
                Assert.That(
                    cut[way].TryBuild(one, root, box, Matrix4x4.identity, Matrix4x4.identity, VpMultiCutSnapshotTests.k_none, VpCapBoundsPolygon.EpsilonFor(box), new Answers { answer = _ => stands }),
                    Is.EqualTo(VpMultiCutBuildOutcome.Built), "the cut body, " + (way == 0 ? "diagnosing" : "the diagnosis off"));
            }

            int compared = AssertSameSnapshot(cut[0], cut[1], "the cut body");
            Assert.That(cut[0].CapCount, Is.EqualTo(2), "the layout: the two sides' caps");
            Assert.That(compared, Is.GreaterThanOrEqualTo(6), "their vertices compared");
            TestContext.Out.WriteLine("the same snapshot either way: " + built[0].RenderFragmentCount + " render fragments of the registrations; the cut body's " + cut[0].RenderFragmentCount
                                      + " render fragments, " + cut[0].CapCount + " caps, " + compared + " cap vertices");
        }

        // The same snapshot, record for record and value for value; the number of cap vertices compared.
        private static int AssertSameSnapshot(VpMultiCutSnapshot a, VpMultiCutSnapshot b, string what)
        {
            Assert.That(new[] { b.RegistrationCount, b.BranchCount, b.CandidateCount, b.RenderFragmentCount, b.ConditionCount, b.CapCount, b.CapVertexCount },
                Is.EqualTo(new[] { a.RegistrationCount, a.BranchCount, a.CandidateCount, a.RenderFragmentCount, a.ConditionCount, a.CapCount, a.CapVertexCount }), what + ": the same counts");
            Assert.That(a.RenderFragmentCount, Is.GreaterThan(0), what + ": built");
            for (int r = 0; r < a.RenderFragmentCount; r++)
            {
                Assert.That(a.TryGetRenderFragment(r, out VpMultiCutRenderFragment x), Is.True);
                Assert.That(b.TryGetRenderFragment(r, out VpMultiCutRenderFragment z), Is.True);
                Assert.That(z.geometryLocalToWorld, Is.EqualTo(x.geometryLocalToWorld), what + ", render fragment " + r + ": placed the same");
                Assert.That(new[] { z.registration, z.conditionStart, z.conditionCount, z.capStart, z.capCount, z.clip.PlaneCount },
                    Is.EqualTo(new[] { x.registration, x.conditionStart, x.conditionCount, x.capStart, x.capCount, x.clip.PlaneCount }), what + ", render fragment " + r + ": the same record");
            }

            int vertices = 0;
            for (int c = 0; c < a.CapCount; c++)
            {
                for (int v = 0; a.TryGetCapVertex(c, v, out Vector3 p); v++)
                {
                    Assert.That(b.TryGetCapVertex(c, v, out Vector3 q), Is.True, what + ", cap " + c + " vertex " + v);
                    Assert.That(q, Is.EqualTo(p), what + ", cap " + c + " vertex " + v + ": the same value");
                    vertices++;
                }
            }

            Assert.That(vertices, Is.EqualTo(a.CapVertexCount), what + ": every cap vertex compared");
            return vertices;
        }

        private static IEnumerable<(string what, Func<VpMultiCutRegistration, VpMultiCutRegistration> change, VpMultiCutInvalidInput reason)> OutsideTheContract()
        {
            yield return ("a box whose centre is not finite", r => With(r, bounds: new Bounds(new Vector3(float.NaN, 0f, 0f), r.localBounds.size)), VpMultiCutInvalidInput.InputContract);
            yield return ("a box of infinite size", r => With(r, bounds: new Bounds(r.localBounds.center, new Vector3(float.PositiveInfinity, 1f, 1f))), VpMultiCutInvalidInput.InputContract);
            yield return ("a box with a negative extent", r => With(r, bounds: new Bounds(Vector3.zero, new Vector3(-1f, 1f, 1f) * 2f)), VpMultiCutInvalidInput.InputContract);
            yield return ("a placement that is not finite", r => { Matrix4x4 m = r.geometryLocalToWorld; m.m13 = float.NaN; return With(r, placement: m); }, VpMultiCutInvalidInput.InputContract);
            yield return ("a placement that is singular", r => With(r, placement: Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1f, 0f, 1f))), VpMultiCutInvalidInput.InputContract);
            yield return ("a placement that is not affine", r => { Matrix4x4 m = r.geometryLocalToWorld; m.m30 = 0.1f; return With(r, placement: m); }, VpMultiCutInvalidInput.InputContract);
            yield return ("a lineage frame that is not rigid", r => With(r, lineage: Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1.01f, 1f, 1f)) * r.lineageToGeometryLocal), VpMultiCutInvalidInput.InputContract);
            yield return ("a placement no section can be computed at", r => With(r, placement: Matrix4x4.Translate(new Vector3(1e38f, 0f, 0f))), VpMultiCutInvalidInput.ConservativeSection);
        }

        [Test]
        public void Off_ANumberOutsideTheContract_IsNotRefusedByTheDiagnosis_WhereTheDiagnosingSnapshotRefusesIt()
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(0, 6);
            int target = registrations.Count / 2;
            int cases = 0;
            foreach ((string what, Func<VpMultiCutRegistration, VpMultiCutRegistration> change, VpMultiCutInvalidInput reason) in OutsideTheContract())
            {
                var changed = new List<VpMultiCutRegistration>(registrations);
                changed[target] = change(registrations[target]);
                for (int way = 0; way < 2; way++)
                {
                    bool diagnosing = way == 0;
                    VpMultiCutSnapshot structural = Make(diagnosing), structure = Make(diagnosing), placed = Make(diagnosing);
                    VpMultiCutBuildOutcome whole = structural.TryBuild(ledger, changed, null);
                    Assert.That(structure.TryBuild(ledger, registrations, null), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                    Assert.That(placed.TryBuildPlacementsFrom(structure, ledger, registrations, null), Is.EqualTo(VpMultiCutBuildOutcome.Built), what + ": inside the contract first");
                    VpMultiCutBuildOutcome only = placed.TryBuildPlacementsFrom(structure, ledger, changed, null);
                    if (diagnosing)
                    {
                        Assert.That((whole, structural.InvalidInputReason), Is.EqualTo((VpMultiCutBuildOutcome.InvalidInput, reason)), what + ": refused by the diagnosis in the structure build");
                        Assert.That((only, placed.InvalidInputReason), Is.EqualTo((VpMultiCutBuildOutcome.InvalidInput, reason)), what + ": and in the placement-only build");
                    }
                    else
                    {
                        TestContext.Out.WriteLine("the diagnosis off, " + what + ": the structure build " + whole + (whole == VpMultiCutBuildOutcome.InvalidInput ? " (" + structural.InvalidInputReason + ")" : "")
                                                  + ", the placement-only build " + only + (only == VpMultiCutBuildOutcome.InvalidInput ? " (" + placed.InvalidInputReason + ")" : ""));
                        Assert.That(ByTheDiagnosis(whole, structural), Is.False, what + ": the structure build is not refused by the diagnosis");
                        Assert.That(ByTheDiagnosis(only, placed), Is.False, what + ": nor the placement-only build");
                        Assert.That(placed.ValidateCounts.placementOnly + structural.ValidateCounts.placementOnly, Is.Zero, what + ": it did not run");
                    }
                }

                cases++;
            }

            Assert.That(cases, Is.EqualTo(8));

            // A placement a query answers that is not one, and one that carries the caps' box out of a float.
            (LogicalCutLedger answered, List<VpMultiCutRegistration> answeredRegistrations, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(0, 6);
            Matrix4x4 Place(LogicalFragmentId f) => Matrix4x4.TRS(new Vector3(0.01f * f.value, 0f, 0f), Quaternion.identity, Vector3.one);
            var bad = new (string what, Func<LogicalFragmentId, Matrix4x4> answer)[]
            {
                ("an answer that is singular", f => f.value % 2 == 0 ? Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1f, 1f, 0f)) : Place(f)),
                ("an answer that is not affine", f => { Matrix4x4 m = Place(f); if (f.value % 2 == 0) m.m32 = 0.5f; return m; }),
            };
            foreach ((string what, Func<LogicalFragmentId, Matrix4x4> answer) in bad)
            {
                for (int way = 0; way < 2; way++)
                {
                    bool diagnosing = way == 0;
                    VpMultiCutSnapshot structure = Make(diagnosing), placed = Make(diagnosing);
                    Assert.That(structure.TryBuild(answered, answeredRegistrations, new Answers { answer = Place }), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                    VpMultiCutBuildOutcome outcome = placed.TryBuildPlacementsFrom(structure, answered, answeredRegistrations, new Answers { answer = answer });
                    if (diagnosing)
                    {
                        Assert.That((outcome, placed.InvalidInputReason), Is.EqualTo((VpMultiCutBuildOutcome.InvalidInput, VpMultiCutInvalidInput.InputContract)), what + ": refused by the diagnosis");
                    }
                    else
                    {
                        TestContext.Out.WriteLine("the diagnosis off, " + what + ": " + outcome + (outcome == VpMultiCutBuildOutcome.InvalidInput ? " (" + placed.InvalidInputReason + ")" : ""));
                        Assert.That(ByTheDiagnosis(outcome, placed), Is.False, what + ": not refused by the diagnosis");
                        Assert.That(placed.PlacementOnlyPlaceCounts.placementChecks, Is.Zero, what + ": no answer checked");
                    }
                }
            }

            Bounds box = VpMultiCutSnapshotTests.k_box;
            var across = new float4(0f, 1f, 0f, 0f);
            Matrix4x4 far = Matrix4x4.TRS(new Vector3(3.4e38f, 0f, 0f), Quaternion.identity, new Vector3(1e36f, 1f, 1f));
            var farOff = new float4(1f, 0f, 0f, -3.3e38f);
            for (int way = 0; way < 2; way++)
            {
                bool diagnosing = way == 0;
                LogicalCutLedger one = VpMultiCutSnapshotTests.NewLedger();
                LogicalFragmentId root = one.AddFragment();
                VpMultiCutSnapshotTests.Cut(one, root, across);
                VpMultiCutSnapshot snapshot = Make(diagnosing, () => VpMultiCutSnapshotTests.NewSnapshot());
                VpMultiCutBuildOutcome carried = snapshot.TryBuild(
                    one, root, box, Matrix4x4.identity, Matrix4x4.identity, VpMultiCutSnapshotTests.k_none, VpCapBoundsPolygon.EpsilonFor(box), new Answers { answer = _ => far });
                VpMultiCutInvalidInput carriedWhy = snapshot.InvalidInputReason;

                LogicalCutLedger other = VpMultiCutSnapshotTests.NewLedger();
                LogicalFragmentId body = other.AddFragment();
                VpMultiCutSnapshotTests.Cut(other, body, farOff);
                VpMultiCutSnapshot against = Make(diagnosing, () => VpMultiCutSnapshotTests.NewSnapshot());
                VpMultiCutBuildOutcome within = against.TryBuild(other, body, box, Matrix4x4.identity, Matrix4x4.identity, VpMultiCutSnapshotTests.k_none, VpCapBoundsPolygon.EpsilonFor(box));
                if (diagnosing)
                {
                    Assert.That((carried, carriedWhy), Is.EqualTo((VpMultiCutBuildOutcome.InvalidInput, VpMultiCutInvalidInput.DrawnCapVertex)), "a placement that carries the caps' box out of a float: refused by the diagnosis");
                    Assert.That((within, against.InvalidInputReason), Is.EqualTo((VpMultiCutBuildOutcome.InvalidInput, VpMultiCutInvalidInput.ConservativeSection)), "a plane that does not stay within a float against the box: refused by the diagnosis");
                }
                else
                {
                    TestContext.Out.WriteLine("the diagnosis off: the caps' box carried out of a float " + carried + (carried == VpMultiCutBuildOutcome.InvalidInput ? " (" + carriedWhy + ")" : "")
                                              + "; the plane against the box " + within + (within == VpMultiCutBuildOutcome.InvalidInput ? " (" + against.InvalidInputReason + ")" : ""));
                    Assert.That(carried, Is.EqualTo(VpMultiCutBuildOutcome.Built), "the caps' box carried out of a float: built, as a non-Development Player builds it");
                    Assert.That(snapshot.CapCount, Is.EqualTo(2));
                    Assert.That(ByTheDiagnosis(within, against), Is.False, "the plane against the box: not refused by the diagnosis");
                }
            }
        }

        [Test]
        public void Off_WhatIsNotTheDiagnosis_RefusesAsItDid_CapacityTheStructureAPlacementNotSaidAndThePlanesOwnCheck()
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(4, 6);
            var twice = new List<VpMultiCutRegistration>(registrations) { registrations[1] };
            var unknown = new List<VpMultiCutRegistration>(registrations)
            {
                new VpMultiCutRegistration(new LogicalFragmentId(99999), registrations[0].localBounds, Matrix4x4.identity, Matrix4x4.identity, VpMultiCutSnapshotTests.k_none, registrations[0].vertexEpsilon),
            };
            Bounds box = VpMultiCutSnapshotTests.k_box;
            Matrix4x4 flatAlongX = Matrix4x4.Scale(new Vector3(1e23f, 1f, 1f));
            var results = new List<(VpMultiCutBuildOutcome outcome, VpMultiCutInvalidInput why, VpMultiCutShortage shortage)>[] { new List<(VpMultiCutBuildOutcome, VpMultiCutInvalidInput, VpMultiCutShortage)>(), new List<(VpMultiCutBuildOutcome, VpMultiCutInvalidInput, VpMultiCutShortage)>() };
            string[] names = { "too little room", "a root registered twice", "a root the ledger does not have", "a placement not said (structure build)", "a placement not said (placement-only build)", "a plane the placement cannot carry" };
            for (int way = 0; way < 2; way++)
            {
                bool diagnosing = way == 0;
                void Note(VpMultiCutBuildOutcome outcome, VpMultiCutSnapshot s) => results[way].Add((outcome, s.InvalidInputReason, s.Shortage));

                VpMultiCutSnapshot small = Make(diagnosing, () => new VpMultiCutSnapshot(new VpMultiCutCapacities(2, 8, 2, 8, 4)));
                Note(small.TryBuild(ledger, registrations, null), small);
                VpMultiCutSnapshot s = Make(diagnosing);
                Note(s.TryBuild(ledger, twice, null), s);
                s = Make(diagnosing);
                Note(s.TryBuild(ledger, unknown, null), s);
                s = Make(diagnosing);
                Note(s.TryBuild(ledger, registrations, new Answers { kind = VpFragmentPlacementKind.Missing }), s);
                VpMultiCutSnapshot structure = Make(diagnosing), placed = Make(diagnosing);
                Assert.That(structure.TryBuild(ledger, registrations, null), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                Note(placed.TryBuildPlacementsFrom(structure, ledger, registrations, new Answers { kind = VpFragmentPlacementKind.Missing }), placed);
                LogicalCutLedger one = VpMultiCutSnapshotTests.NewLedger();
                LogicalFragmentId root = one.AddFragment();
                VpMultiCutSnapshotTests.Cut(one, root, new float4(1f, 0f, 0f, 0f));
                s = Make(diagnosing, () => VpMultiCutSnapshotTests.NewSnapshot());
                Note(s.TryBuild(one, root, box, flatAlongX, Matrix4x4.identity, VpMultiCutSnapshotTests.k_none, VpCapBoundsPolygon.EpsilonFor(box)), s);
            }

            for (int i = 0; i < names.Length; i++)
            {
                TestContext.Out.WriteLine(names[i] + ": diagnosing " + results[0][i] + "; the diagnosis off " + results[1][i]);
                Assert.That(results[1][i], Is.EqualTo(results[0][i]), names[i] + ": refused the same way with the diagnosis off");
                Assert.That(results[1][i].outcome, Is.Not.EqualTo(VpMultiCutBuildOutcome.Built), names[i] + ": refused");
            }

            Assert.That(results[1][0].outcome, Is.EqualTo(VpMultiCutBuildOutcome.CapacityExceeded), "too little room is a shortage");
            Assert.That(results[1][0].shortage, Is.Not.EqualTo(VpMultiCutShortage.None));
            Assert.That(results[1][2], Is.EqualTo((VpMultiCutBuildOutcome.InvalidInput, VpMultiCutInvalidInput.Lineage, VpMultiCutShortage.None)), "a root the ledger does not have");
            Assert.That(results[1][3].outcome, Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput), "a placement not said");
            Assert.That(results[1][4].outcome, Is.EqualTo(VpMultiCutBuildOutcome.InvalidInput));
            Assert.That(results[1][5], Is.EqualTo((VpMultiCutBuildOutcome.InvalidInput, VpMultiCutInvalidInput.PlaneNotCarried, VpMultiCutShortage.None)), "the cut plane's own check");
        }

        /// <summary>
        /// The Profiler's sections of the snapshot's stages are entered whether the diagnosis runs or not; the one
        /// section that is the diagnosis itself -- the validation of the placement inputs in a placement-only build --
        /// is not entered where it is off, because nothing runs there.
        /// </summary>
        [Test]
        public void Off_TheStagesProfilerSectionsAreStillEntered_AndTheDiagnosisOwnSectionIsNot()
        {
            (LogicalCutLedger ledger, List<VpMultiCutRegistration> registrations, _) = VpMultiCutSnapshotValidatePartsTests.BuildingLike(4, 6);
            using (ProfilerRecorder validate = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Snapshot.Validate", 4, ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame))
            using (ProfilerRecorder place = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Snapshot.Place", 4, ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame))
            using (ProfilerRecorder collect = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Snapshot.Collect", 4, ProfilerRecorderOptions.Default | ProfilerRecorderOptions.SumAllSamplesInFrame))
            {
                Assert.That(validate.Valid && place.Valid && collect.Valid, Is.True, "the three sections are known to the Profiler");
                VpMultiCutSnapshot structure = Make(false), placed = Make(false);
                long v0 = validate.CurrentValue, p0 = place.CurrentValue, c0 = collect.CurrentValue;
                Assert.That(structure.TryBuild(ledger, registrations, null), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                long v1 = validate.CurrentValue, p1 = place.CurrentValue, c1 = collect.CurrentValue;
                TestContext.Out.WriteLine("the diagnosis off, a structure build: Validate +" + (v1 - v0) + " ns, Collect +" + (c1 - c0) + " ns, Place +" + (p1 - p0) + " ns");
                Assert.That(v1, Is.GreaterThan(v0), "the structure's validation: its section entered");
                Assert.That(c1, Is.GreaterThan(c0), "the collection's");
                Assert.That(p1, Is.GreaterThan(p0), "the placement pass's");

                for (int k = 0; k < 3; k++) Assert.That(placed.TryBuildPlacementsFrom(structure, ledger, registrations, null), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                long v2 = validate.CurrentValue, p2 = place.CurrentValue;
                TestContext.Out.WriteLine("the diagnosis off, three placement-only builds: Validate +" + (v2 - v1) + " ns, Place +" + (p2 - p1) + " ns");
                Assert.That(p2, Is.GreaterThan(p1), "the placement passes: their section entered");
                Assert.That(v2, Is.EqualTo(v1), "the diagnosis's own section: not entered, nothing ran");

                VpMultiCutSnapshot diagnosing = Make(true);
                Assert.That(diagnosing.TryBuildPlacementsFrom(structure, ledger, registrations, null), Is.EqualTo(VpMultiCutBuildOutcome.Built));
                long v3 = validate.CurrentValue;
                TestContext.Out.WriteLine("diagnosing, one placement-only build: Validate +" + (v3 - v2) + " ns");
                Assert.That(v3, Is.GreaterThan(v2), "where the diagnosis runs, its section is entered");
            }
        }
    }
}
#endif
