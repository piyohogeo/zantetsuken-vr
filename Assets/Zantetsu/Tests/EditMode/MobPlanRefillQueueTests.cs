using System.Collections.Generic;
using NUnit.Framework;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// A crowd's refill queue (<see cref="MobPlanRefillQueue{TPlan}"/>) on a fake clock: starts one at a time, oldest
    /// first, within the frame's cap and remaining budget; a second request only replaces the plan; a cancelled start and
    /// every start after the world's end never run; a stage expected over the cap is refused, not forced; and what a stage
    /// measured is recorded apart from the decision.
    /// </summary>
    public class MobPlanRefillQueueTests
    {
        private const double Ms = 0.001;
        private long _now;
        private readonly List<string> _log = new List<string>();

        private MobPlanRefillQueue<string> Queue(double startSeed = 1 * Ms, double prepareSeed = 1 * Ms) =>
            new MobPlanRefillQueue<string>(startSeed, prepareSeed, 1 * Ms, () => _now, 1000000.0);

        // A start that takes the given time and reports the result.
        private System.Func<int, string, MobPlanRefillQueue<string>.StartResult> Start(double seconds,
            MobPlanRefillQueue<string>.StartResult result = MobPlanRefillQueue<string>.StartResult.Started) =>
            (id, plan) => { _now += (long)(seconds * 1000000.0); _log.Add(id + ":" + plan); return result; };

        private static double Plenty() => 1.0;

        [SetUp] public void Clear() { _now = 0; _log.Clear(); }

        [Test] public void Starts_RunOldestFirst_OnlyAsFarAsTheCapAllows()
        {
            var q = Queue();
            q.Request(1, "a", 0); q.Request(2, "b", 0); q.Request(3, "c", 0);
            q.Run(1, 2.5 * Ms, Plenty, 0, Start(1 * Ms), () => false, () => false);
            CollectionAssert.AreEqual(new[] { "1:a", "2:b" }, _log);
            Assert.That(q.DeferredByCap, Is.EqualTo(1)); Assert.That(q.WaitingStarts, Is.EqualTo(1));
            q.Run(2, 2.5 * Ms, Plenty, 0, Start(1 * Ms), () => false, () => false);
            CollectionAssert.AreEqual(new[] { "1:a", "2:b", "3:c" }, _log);
            Assert.That(q.Started, Is.EqualTo(3)); Assert.That(q.WaitingStarts, Is.Zero);
            Assert.That(q.Records[2].waitFrames, Is.EqualTo(2));
        }

        [Test] public void Request_Again_ReplacesThePlan_KeepsItsPlace()
        {
            var q = Queue();
            Assert.That(q.Request(1, "a", 0), Is.True); Assert.That(q.Request(2, "b", 0), Is.True);
            Assert.That(q.Request(1, "a2", 3), Is.False);
            Assert.That(q.WaitingStarts, Is.EqualTo(2)); Assert.That(q.Replaced, Is.EqualTo(1)); Assert.That(q.Requests, Is.EqualTo(2));
            q.Run(5, 10 * Ms, Plenty, 0, Start(0), () => false, () => false);
            CollectionAssert.AreEqual(new[] { "1:a2", "2:b" }, _log);
            Assert.That(q.Records[0].waitFrames, Is.EqualTo(5), "waiting counts from the first request");
        }

        [Test] public void Cancelled_NeverStarts_AndTheWorldsEndCancelsEverything()
        {
            var q = Queue();
            q.Request(1, "a", 0); q.Request(2, "b", 0); q.Request(3, "c", 0);
            Assert.That(q.Cancel(2), Is.True); Assert.That(q.Cancel(2), Is.False);
            q.Run(1, 1.5 * Ms, Plenty, 0, Start(1 * Ms), () => false, () => false);
            CollectionAssert.AreEqual(new[] { "1:a" }, _log);
            q.CancelAll();
            q.Run(2, 10 * Ms, Plenty, 0, Start(1 * Ms), () => false, () => false);
            CollectionAssert.AreEqual(new[] { "1:a" }, _log);
            Assert.That(q.Cancelled, Is.EqualTo(2)); Assert.That(q.WaitingStarts, Is.Zero);
        }

        [Test] public void NoSlot_KeepsWaiting_Dropped_LeavesTheQueue()
        {
            var q = Queue();
            q.Request(1, "a", 0);
            q.Run(1, 10 * Ms, Plenty, 0, Start(0, MobPlanRefillQueue<string>.StartResult.NoSlot), () => false, () => false);
            Assert.That(q.WaitingStarts, Is.EqualTo(1)); Assert.That(q.NoSlotFrames, Is.EqualTo(1));
            q.Run(2, 10 * Ms, Plenty, 0, Start(0, MobPlanRefillQueue<string>.StartResult.Dropped), () => false, () => false);
            Assert.That(q.WaitingStarts, Is.Zero); Assert.That(q.Dropped, Is.EqualTo(1)); Assert.That(q.Started, Is.Zero);
        }

        [Test] public void Prepare_RunsAfterStarts_OneAtATime_WithinTheCap()
        {
            var q = Queue();
            int ready = 3, prepared = 0;
            q.Request(1, "a", 0);
            q.Run(1, 2.5 * Ms, Plenty, 0, Start(1 * Ms), () => ready > 0, () => { _now += 1000; ready--; prepared++; _log.Add("prepare"); return true; });
            CollectionAssert.AreEqual(new[] { "1:a", "prepare" }, _log);
            Assert.That(prepared, Is.EqualTo(1)); Assert.That(q.DeferredByCap, Is.EqualTo(1));
        }

        [Test] public void AMismatchedBake_RunsAsItsOwnStage_BeforeThatSlotsPreparation()
        {
            var q = Queue();
            bool baked = false; int prepared = 0;
            q.Run(1, 1.5 * Ms, Plenty, 0, Start(0), () => prepared == 0, () => { _now += 1000; prepared++; _log.Add("prepare"); return true; },
                () => !baked, () => { _now += 1000; baked = true; _log.Add("bake"); return true; });
            CollectionAssert.AreEqual(new[] { "bake" }, _log, "the bake is one stage, the preparation waits for the next room");
            Assert.That(q.Baked, Is.EqualTo(1)); Assert.That(q.DeferredByCap, Is.EqualTo(1));
            q.Run(2, 1.5 * Ms, Plenty, 0, Start(0), () => prepared == 0, () => { _now += 1000; prepared++; _log.Add("prepare"); return true; },
                () => !baked, () => { baked = true; return true; });
            CollectionAssert.AreEqual(new[] { "bake", "prepare" }, _log);
            Assert.That(q.Records[1].stage, Is.EqualTo(MobPlanRefillQueue<string>.Stage.Prepare));
        }

        [Test] public void OverCapEstimate_IsRefused_NotForced()
        {
            var q = Queue(prepareSeed: 3 * Ms);
            bool ran = false;
            q.Run(1, 2 * Ms, Plenty, 0, Start(0), () => true, () => ran = true);
            Assert.That(ran, Is.False); Assert.That(q.OverCapRefusals, Is.EqualTo(1));
        }

        [Test] public void TheFramesRemainingBudget_OverTheReserve_Defers()
        {
            var q = Queue();
            q.Request(1, "a", 0);
            q.Run(1, 10 * Ms, () => 1.5 * Ms, 1 * Ms, Start(0), () => false, () => false);
            Assert.That(_log, Is.Empty); Assert.That(q.DeferredByFrame, Is.EqualTo(1));
            q.Run(2, 10 * Ms, () => 2 * Ms, 1 * Ms, Start(0), () => false, () => false);
            CollectionAssert.AreEqual(new[] { "1:a" }, _log);
        }

        [Test] public void AMeasuredOverrun_IsRecordedApartFromTheDecision()
        {
            var q = Queue(startSeed: 0.5 * Ms);
            q.Request(1, "a", 0);
            q.Run(1, 2 * Ms, Plenty, 0, Start(3 * Ms), () => false, () => false);
            Assert.That(q.Started, Is.EqualTo(1)); Assert.That(q.Overruns, Is.EqualTo(1));
            var r = q.Records[0];
            Assert.That(r.expected, Is.EqualTo(0.5 * Ms).Within(1e-12)); Assert.That(r.measured, Is.EqualTo(3 * Ms).Within(1e-9));
            Assert.That(r.overrun, Is.EqualTo(1 * Ms).Within(1e-9));
            // One slow run does not move the estimate (the median of five, starting from the seed)...
            Assert.That(q.ExpectedSeconds(MobPlanRefillQueue<string>.Stage.Start), Is.EqualTo(0.5 * Ms).Within(1e-12));
            // ...a cost that stays over the cap does: the next start is then refused, not forced.
            for (int i = 2; i <= 3; i++) { q.Request(i, "x", i); q.Run(i, 10 * Ms, Plenty, 0, Start(3 * Ms), () => false, () => false); }
            q.Request(9, "b", 4);
            q.Run(4, 2 * Ms, Plenty, 0, Start(0), () => false, () => false);
            Assert.That(q.OverCapRefusals, Is.EqualTo(1)); Assert.That(q.WaitingStarts, Is.EqualTo(1));
        }
    }
}
