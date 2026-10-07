#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The numeric input-contract diagnosis switched off (TL, 2026-10-07; DESIGN 5.6, D-211), at the display. A display
    /// takes the setting when it is made, and its snapshots with it. Through the same moves, cut, commit and
    /// retirement, a display made with the diagnosis off adopts and draws exactly what one made with it on does --
    /// the input is inside the contract, so the diagnosis decided nothing -- while no answer is checked in it. What
    /// the display takes in is no longer refused for its numbers; what it refuses for a fragment's life is refused as
    /// before.
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        private Twin NewTwinDiagnosing(bool diagnosing, int bodies)
        {
            VpNumericDiagnosis.SetForTest(diagnosing);
            try
            {
                return NewTwin(bodies, 64, true);
            }
            finally
            {
                VpNumericDiagnosis.ResetForTest();
            }
        }

        [Test]
        public void NumericDiagnosisOff_ThroughMovesACutItsCommitAndARetirement_TheDisplayDrawsWhatTheDiagnosingOneDraws_AndChecksNoAnswer()
        {
            try
            {
                using (Twin on = NewTwinDiagnosing(true, 6))
                using (Twin off = NewTwinDiagnosing(false, 6))
                {
                    Assert.That(VpNumericDiagnosis.Enabled, Is.True, "the process's own setting is back: the displays keep what they took");
                    int step = 0;
                    void Each(Action<Run> act)
                    {
                        Both(on, act);
                        Both(off, act);
                    }

                    void Collect(string what)
                    {
                        // One frame for the four displays (each twin: the kept way and the way that collects everything).
                        _frame++;
                        foreach (Twin twin in new[] { on, off })
                        {
                            Assert.That(twin.kept.Display.TryBeginFrame(), Is.True, what + ": collected (kept)");
                            Assert.That(twin.everything.Display.TryBeginFrame(), Is.True, what + ": collected (collecting everything)");
                            AssertSameDrawing(twin, what);
                        }

                        AssertSameShape(Capture(on.kept.Display), Capture(off.kept.Display), what + ": the diagnosis off draws what the diagnosing display draws");
                        VpMultiCutSnapshot a = on.kept.Display.AdoptedSnapshot, b = off.kept.Display.AdoptedSnapshot;
                        Assert.That(b.RenderFragmentCount, Is.EqualTo(a.RenderFragmentCount), what + ": the adopted render fragments");
                        for (int r = 0; r < a.RenderFragmentCount; r++)
                        {
                            a.TryGetRenderFragment(r, out VpMultiCutRenderFragment x);
                            b.TryGetRenderFragment(r, out VpMultiCutRenderFragment y);
                            Assert.That(y.geometryLocalToWorld, Is.EqualTo(x.geometryLocalToWorld), what + ": render fragment " + r + " adopted at the same placement");
                            Assert.That(new[] { y.registration, y.conditionCount, y.capCount, y.clip.PlaneCount }, Is.EqualTo(new[] { x.registration, x.conditionCount, x.capCount, x.clip.PlaneCount }),
                                what + ": render fragment " + r + " the same record");
                        }

                        step++;
                    }

                    for (int i = 0; i < 3; i++) Collect("settling " + i);

                    // Bodies move: their answers change and are asked again.
                    Each(run => run.at.Put(run.bodies[1], Stand(1, 0.5f, 20f)).Put(run.bodies[4], Stand(4, -0.25f)));
                    Collect("two bodies moved");
                    Each(run => run.at.Put(run.bodies[1], Stand(1, 0.75f, 40f)));
                    Collect("one moved again");

                    // A cut published, its sides moving apart, then its commit.
                    var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                    (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) made = default;
                    Each(run => made = CutAndPlace(run, 2, plane, Stand(2)));
                    Collect("a cut published");
                    Assert.That(on.kept.Display.CapRecordCount, Is.GreaterThan(0), "the layout: provisional caps drawn");
                    Each(run => run.at.Put(made.positive, Stand(2, 0.3f)).Put(made.negative, Stand(2, -0.3f, 10f)));
                    Collect("its sides apart");
                    _frame++;
                    Each(run => Assert.That(CommitBothSides(run, 2, made.cut, plane, made.positive, made.negative), Is.True, "committed"));
                    Collect("the commit");
                    Collect("after the commit");

                    // A body retired, and the rest going on.
                    Each(run => Assert.That(run.scene.ledger.Retire(run.bodies[5]), Is.True));
                    Collect("one retired");
                    Each(run => run.at.Put(run.bodies[0], Stand(0, 1f)));
                    Collect("another moved");
                    Assert.That(step, Is.EqualTo(11), "every collection of the scenario was compared");

                    VpPlaceCounts onOnly = PlacementOnlyOf(on.kept.Display, out VpPlaceCounts onStructural);
                    VpPlaceCounts offOnly = PlacementOnlyOf(off.kept.Display, out VpPlaceCounts offStructural);
                    TestContext.Out.WriteLine("diagnosing: answers checked " + (onOnly.placementChecks + onStructural.placementChecks) + "; queries " + (onOnly.queries + onStructural.queries)
                                              + "; the diagnosis off: answers checked " + (offOnly.placementChecks + offStructural.placementChecks) + "; queries " + (offOnly.queries + offStructural.queries));
                    Assert.That(onOnly.placementChecks + onStructural.placementChecks, Is.GreaterThan(0), "the diagnosing display checked answers");
                    Assert.That(offOnly.placementChecks + offStructural.placementChecks, Is.Zero, "the other checked none");
                    Assert.That(offOnly.queries + offStructural.queries, Is.EqualTo(onOnly.queries + onStructural.queries), "and asked exactly as many placements");
                    Assert.That(offOnly.passes + offStructural.passes, Is.EqualTo(onOnly.passes + onStructural.passes), "in as many passes");
                }
            }
            finally
            {
                VpNumericDiagnosis.ResetForTest();
            }
        }

        [Test]
        public void NumericDiagnosisOff_WhatTheDisplayTakesIn_IsNotRefusedForItsNumbers_AndIsStillRefusedForAFragmentsLife()
        {
            try
            {
                // No section can be computed at this placement in single precision: the diagnosis's refusal, where it runs.
                Matrix4x4 farOff = Matrix4x4.Translate(new Vector3(1e38f, 0f, 0f));
                for (int way = 0; way < 2; way++)
                {
                    bool diagnosing = way == 0;
                    string what = diagnosing ? "diagnosing" : "the diagnosis off";
                    using (Twin twin = NewTwinDiagnosing(diagnosing, 2))
                    {
                        Run run = twin.kept;
                        VpLogicalCutDisplay display = run.Display;

                        // A fragment's life, refused either way: one already shown, and one that is retired.
                        Assert.That(display.TryShow(run.bodies[0], run.geometries[0], Stand(7)), Is.False, what + ": a fragment already shown is refused");
                        LogicalFragmentId gone = run.scene.ledger.AddFragment();
                        Assert.That(run.scene.ledger.Retire(gone), Is.True);
                        Assert.That(display.TryShow(gone, AppendCuttableCube(run.scene.storage), Stand(8)), Is.False, what + ": a retired fragment is refused");
                        Assert.That(display.TryShow(default, AppendCuttableCube(run.scene.storage), Stand(8)), Is.False, what + ": no fragment is refused");

                        // Its numbers: refused by the diagnosis only.
                        LogicalFragmentId far = run.scene.ledger.AddFragment();
                        Assert.That(display.TryShow(far, AppendCuttableCube(run.scene.storage), farOff), Is.EqualTo(!diagnosing),
                            what + (diagnosing ? ": a placement outside the contract is refused where it is taken in" : ": the same placement is taken in, as a non-Development Player takes it"));
                    }
                }
            }
            finally
            {
                VpNumericDiagnosis.ResetForTest();
            }
        }
    }
}
#endif
