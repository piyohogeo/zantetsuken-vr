using NUnit.Framework;
using Unity.Mathematics;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// A Pending cut's build or publication whose prediction is more than the whole Main budget of a frame (DESIGN 7.1.1):
    /// the ordinary carry-over would never end, as no remainder could fit it and its cost is measured only when it runs.
    /// Such a stage runs past the budget at a later update's opportunity, one stage per world and frame however many
    /// updates the frame has, and its measured cost goes into the stage's history. A stage that merely does not fit this
    /// frame's remainder is carried over as before. The remainder, the whole budget and the predictions are given
    /// deterministically; the frames are counted by the test.
    /// </summary>
    public unsafe partial class ProvisionalCutDriverTests
    {
        private const double k_wholeBudget = 0.010;

        /// <summary>The two stages' predictions, as the only samples of their histories.</summary>
        private static void Predict(World w, double build, double publish)
        {
            w.driver.BuildCosts.Clear();
            w.driver.PublishCosts.Clear();
            w.driver.BuildCosts.Add(build);
            w.driver.PublishCosts.Add(publish);
            w.driver.MainBudgetSeconds = () => k_wholeBudget;
        }

        [Test]
        public void AShortRemainder_IsCarriedOver_AndTheSameRequestIsPublishedLater_WithNoRunPastTheBudget()
        {
            int frame = 200;
            using (World w = NewWorld(frameSource: () => frame))
            {
                Predict(w, build: 0.002, publish: 0.001);
                double remaining = 0.001;
                w.driver.RemainingMainSeconds = () => remaining;
                Assert.That(w.driver.RequestCut(Ask(w), out ProvisionalCutTransaction pending, out _), Is.EqualTo(ProvisionalCutAcceptance.Pending));
                CutOperationId operation = pending.Operation;

                w.driver.Advance(frame);
                w.driver.Advance(++frame);
                Assert.That(pending.Phase, Is.EqualTo(ProvisionalCutPhase.Accepted), "3 ms fits a frame, so it waits for a remainder");
                Assert.That(pending.Candidate, Is.Null);

                remaining = 0.009;
                w.driver.Advance(++frame);
                Assert.That(pending.Phase, Is.EqualTo(ProvisionalCutPhase.Published));
                Assert.That(pending.Operation, Is.EqualTo(operation), "the same request, not another admission");
                Assert.That(pending.PublishedFrame, Is.EqualTo(frame));
                Assert.That(w.driver.BudgetOverrunCount, Is.Zero, "nothing ran past the budget");
            }
        }

        [Test]
        public void AColdPredictionOverTheWholeBudget_IsBuiltPastIt_WithAnyRemainder_ThenPublishedByTheOrdinaryDecision()
        {
            int frame = 300;
            using (World w = NewWorld(frameSource: () => frame))
            {
                Predict(w, build: 0.030, publish: 0.001);
                double remaining = -0.002;
                w.driver.RemainingMainSeconds = () => remaining;
                Assert.That(w.driver.RequestCut(Ask(w), out ProvisionalCutTransaction pending, out _), Is.EqualTo(ProvisionalCutAcceptance.Pending));
                Assert.That(pending.Candidate, Is.Null, "an acceptance itself never runs past the budget");
                Assert.That(w.driver.BudgetOverrunCount, Is.Zero);

                // A later update of the frame: the build runs past the budget, although the remainder is not positive.
                w.driver.Advance(frame);
                ProvisionalOwnerCandidate candidate = pending.Candidate;
                Assert.That(candidate, Is.Not.Null, "built");
                UnityEngine.GameObject positive = candidate.Positive.Root;
                Assert.That(w.driver.BudgetOverrunCount, Is.EqualTo(1));
                ProvisionalBudgetOverrun run = w.driver.LastBudgetOverrun;
                Assert.That(run.Stage, Is.EqualTo(ProvisionalBudgetStage.Build));
                Assert.That(run.Operation, Is.EqualTo(pending.Operation));
                Assert.That(run.Frame, Is.EqualTo(frame));
                Assert.That(run.ExpectedSeconds, Is.EqualTo(0.031).Within(1e-12), "build and publication together");
                Assert.That(run.BudgetSeconds, Is.EqualTo(k_wholeBudget));
                Assert.That(run.RemainingSeconds, Is.EqualTo(-0.002));
                Assert.That(run.MeasuredSeconds, Is.GreaterThan(0.0));
                Assert.That(w.driver.BuildCosts.Count, Is.EqualTo(2), "the measured build went into the history");
                Assert.That(w.driver.BuildCosts.ExpectedSeconds, Is.EqualTo(0.5 * (0.030 + run.MeasuredSeconds)).Within(1e-12));

                // The publication went back to the ordinary decision: no remainder, so it is carried over, built once.
                Assert.That(pending.Phase, Is.EqualTo(ProvisionalCutPhase.Accepted));
                w.driver.Advance(frame);
                Assert.That(pending.Candidate, Is.SameAs(candidate), "not built again");
                Assert.That(pending.Phase, Is.EqualTo(ProvisionalCutPhase.Accepted), "not published in the same frame");

                remaining = 0.005;
                w.driver.Advance(++frame);
                Assert.That(pending.Phase, Is.EqualTo(ProvisionalCutPhase.Published), "published by the ordinary decision");
                Assert.That(pending.Pair.Positive.Root, Is.SameAs(positive), "the pair built past the budget, published once");
                Assert.That(w.driver.BudgetOverrunCount, Is.EqualTo(1));
                Assert.That(w.registry.ProvisionalPairCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void OnlyTheSumOverTheWholeBudget_IsBuiltPastIt()
        {
            int frame = 400;
            using (World w = NewWorld(frameSource: () => frame))
            {
                // 6 ms and 6 ms each fit a 10 ms frame; together they do not.
                Predict(w, build: 0.006, publish: 0.006);
                w.driver.RemainingMainSeconds = () => 0.009;
                Assert.That(w.driver.RequestCut(Ask(w), out ProvisionalCutTransaction pending, out _), Is.EqualTo(ProvisionalCutAcceptance.Pending));

                w.driver.Advance(frame);
                Assert.That(w.driver.BudgetOverrunCount, Is.EqualTo(1));
                Assert.That(w.driver.LastBudgetOverrun.Stage, Is.EqualTo(ProvisionalBudgetStage.Build));
                Assert.That(w.driver.LastBudgetOverrun.ExpectedSeconds, Is.EqualTo(0.012).Within(1e-12));
                Assert.That(pending.Phase, Is.EqualTo(ProvisionalCutPhase.Published), "the 6 ms publication fits the 9 ms remainder");
            }
        }

        [Test]
        public void APublicationPredictedOverTheWholeBudget_RunsPastItInALaterFrame()
        {
            int frame = 500;
            using (World w = NewWorld(frameSource: () => frame))
            {
                Predict(w, build: 0.001, publish: 0.020);
                w.driver.RemainingMainSeconds = () => 0.005;
                Assert.That(w.driver.RequestCut(Ask(w), out ProvisionalCutTransaction pending, out _), Is.EqualTo(ProvisionalCutAcceptance.Pending));

                w.driver.Advance(frame);
                w.driver.Advance(frame);
                Assert.That(w.driver.BudgetOverrunCount, Is.EqualTo(1), "the build, once, in this frame");
                Assert.That(w.driver.LastBudgetOverrun.Stage, Is.EqualTo(ProvisionalBudgetStage.Build));
                Assert.That(pending.Candidate, Is.Not.Null);
                Assert.That(pending.Phase, Is.EqualTo(ProvisionalCutPhase.Accepted), "the publication waits for another frame's allowance");

                w.driver.Advance(++frame);
                Assert.That(w.driver.BudgetOverrunCount, Is.EqualTo(2));
                ProvisionalBudgetOverrun run = w.driver.LastBudgetOverrun;
                Assert.That(run.Stage, Is.EqualTo(ProvisionalBudgetStage.Publication));
                Assert.That(run.Frame, Is.EqualTo(frame));
                Assert.That(run.ExpectedSeconds, Is.EqualTo(0.020).Within(1e-12));
                Assert.That(w.driver.PublishCosts.Count, Is.EqualTo(2), "the measured publication went into the history");
                Assert.That(pending.Phase, Is.EqualTo(ProvisionalCutPhase.Published));
                Assert.That(pending.PublishedFrame, Is.EqualTo(frame));
            }
        }

        [Test]
        public void SeveralPendingCutsAndSeveralUpdates_ShareOneStagePastTheBudgetPerFrame()
        {
            int frame = 600;
            using (World w = NewWorld(frameSource: () => frame))
            {
                // Two live fragments: the two children of a first cut.
                ProvisionalCutTransaction first = Publish(w);
                RunUntil(w, 610, () => first.Phase == ProvisionalCutPhase.HandedOff, "first children published");
                Assert.That(w.ledger.TryGetOperation(first.Operation, out LogicalCutOperation children), Is.True);
                frame = 1000;

                Predict(w, build: 0.030, publish: 0.001);
                w.driver.RemainingMainSeconds = () => 0.005;
                var ask = new ProvisionalCutAsk { source = children.positive, plane = new float4(1, 0, 0, 0) };
                Assert.That(w.driver.RequestCut(ask, out ProvisionalCutTransaction a, out _), Is.EqualTo(ProvisionalCutAcceptance.Pending));
                ask.source = children.negative;
                Assert.That(w.driver.RequestCut(ask, out ProvisionalCutTransaction b, out _), Is.EqualTo(ProvisionalCutAcceptance.Pending));

                // Three updates of one frame -- as its update, late update and after-rendering turn -- take one stage.
                w.driver.Advance(frame);
                w.driver.Advance(frame);
                w.driver.Advance(frame);
                Assert.That(w.driver.BudgetOverrunCount, Is.EqualTo(1), "one stage in the frame");
                bool Built(ProvisionalCutTransaction x) => x.Candidate != null || x.Pair != null;
                Assert.That((Built(a) ? 1 : 0) + (Built(b) ? 1 : 0), Is.EqualTo(1), "one of the two was built");
                ProvisionalCutTransaction built = Built(a) ? a : b;
                ProvisionalCutTransaction waiting = ReferenceEquals(built, a) ? b : a;
                Assert.That(built.Phase, Is.EqualTo(ProvisionalCutPhase.Published), "its publication fits the remainder");
                Assert.That(waiting.Phase, Is.EqualTo(ProvisionalCutPhase.Accepted));
                Assert.That(waiting.Candidate, Is.Null);

                // The next frame's allowance takes the other.
                frame++;
                w.driver.Advance(frame);
                w.driver.Advance(frame);
                Assert.That(w.driver.BudgetOverrunCount, Is.EqualTo(2));
                Assert.That(w.driver.LastBudgetOverrun.Frame, Is.EqualTo(frame));
                Assert.That(w.driver.LastBudgetOverrun.Operation, Is.EqualTo(waiting.Operation));
                Assert.That(waiting.Phase, Is.EqualTo(ProvisionalCutPhase.Published));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void EndingACutCarriedOverAfterItsBuild_LeavesNothing_AndNothingIsPublishedOrBuiltAgain(bool everyCut)
        {
            int frame = 700;
            using (World w = NewWorld(frameSource: () => frame))
            {
                Predict(w, build: 0.001, publish: 0.020);
                w.driver.RemainingMainSeconds = () => 0.005;
                Assert.That(w.driver.RequestCut(Ask(w), out ProvisionalCutTransaction pending, out _), Is.EqualTo(ProvisionalCutAcceptance.Pending));
                w.driver.Advance(frame);
                ProvisionalOwnerCandidate candidate = pending.Candidate;
                Assert.That(candidate, Is.Not.Null, "built past the budget, its publication carried over");
                int builds = w.driver.BuildCosts.Count;

                if (everyCut)
                {
                    w.driver.EndEveryCut();
                }
                else
                {
                    Assert.That(w.driver.EndCut(pending.Operation), Is.True);
                }

                Assert.That(candidate.IsDisposed, Is.True, "the held pair went back");
                Assert.That(pending.HoldsInput, Is.False);
                Assert.That(pending.Classification, Is.Null);
                Assert.That(pending.Phase, Is.EqualTo(ProvisionalCutPhase.Recovered));
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero);
                Assert.That(w.registry.SystemConstraintCount, Is.Zero);

                w.driver.Advance(++frame);
                w.driver.Advance(++frame);
                Assert.That(pending.Phase, Is.EqualTo(ProvisionalCutPhase.Recovered), "not published");
                Assert.That(w.driver.BuildCosts.Count, Is.EqualTo(builds), "not built again");
                Assert.That(w.driver.BudgetOverrunCount, Is.EqualTo(1), "the ended cut took no further allowance");
                Assert.That(w.registry.ProvisionalPairCount, Is.Zero);
            }
        }
    }
}
