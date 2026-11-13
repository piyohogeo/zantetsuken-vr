using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The additional split's launch settings (2026-11-13): with no argument, eight attempts out at once, the topology component
    /// counts taken and a one-component geometry passed over; the arguments that give the control as before, the counts off, and
    /// the counts recorded only; and an argument that cannot be read keeps the default and says so.
    /// </summary>
    public class CutSpatialSplitLaunchTests
    {
        private int _attempts;
        private bool _counts, _skip;

        [SetUp]
        public void SetUp()
        {
            _attempts = CutSpatialSplit.DefaultMaxAttempts;
            _counts = VpTopologyComponents.Enabled;
            _skip = CutSpatialSplit.SkipOneComponent;
        }

        [TearDown]
        public void TearDown()
        {
            CutSpatialSplit.DefaultMaxAttempts = _attempts;
            VpTopologyComponents.Enabled = _counts;
            CutSpatialSplit.SkipOneComponent = _skip;
        }

        private static void AssertSettings(int attempts, bool counts, bool skip, string what)
        {
            Assert.That(CutSpatialSplit.DefaultMaxAttempts, Is.EqualTo(attempts), what + ": attempts out at once");
            Assert.That(VpTopologyComponents.Enabled, Is.EqualTo(counts), what + ": the counts");
            Assert.That(CutSpatialSplit.SkipOneComponent, Is.EqualTo(skip), what + ": the pass-over");
            Assert.That(CutSpatialSplit.BackgroundPoolCapacity(2), Is.EqualTo(attempts > 1 ? Mathf.Max(2, attempts) : 2), what + ": the PC pool's accept capacity (two workers)");
            Assert.That(CutSpatialSplit.BackgroundPoolCapacity(1), Is.EqualTo(attempts > 1 ? attempts : 1), what + ": the Quest pool's accept capacity (one worker)");
        }

        [Test]
        public void WithNoArgument_EightAttempts_TheCounts_AndThePassOver()
        {
            Assert.That(CutSpatialSplitLaunch.Apply(new[] { "player.exe", "-force-d3d12" }), Is.Empty);
            AssertSettings(8, true, true, "no argument");
            StringAssert.Contains("up to 8 attempts out at once", CutSpatialSplitLaunch.Effective);
            StringAssert.Contains("passed over before an attempt", CutSpatialSplitLaunch.Effective);
            Assert.That(CutSpatialSplitLaunch.Apply(null), Is.Empty);
            AssertSettings(8, true, true, "no arguments at all (a device)");
            Assert.That(CutSpatialSplit.EvaluationMaxWaitingMain, Is.EqualTo(4));
            Assert.That(CutSpatialSplit.EvaluationMaxHeldBytes, Is.EqualTo(4L << 20));
        }

        [Test]
        public void TheArguments_GiveTheControlAsBefore_TheCountsOff_AndTheCountsRecordedOnly()
        {
            Assert.That(CutSpatialSplitLaunch.Apply(new[] { "-zantetsuSplitAttempts", "1", "-zantetsuSplitOneComponent", "off" }), Is.Empty);
            AssertSettings(1, false, false, "the launch as it was");
            StringAssert.Contains("the control as before", CutSpatialSplitLaunch.Effective);

            Assert.That(CutSpatialSplitLaunch.Apply(new[] { "-zantetsuSplitOneComponent", "off" }), Is.Empty);
            AssertSettings(8, false, false, "the counts off");

            Assert.That(CutSpatialSplitLaunch.Apply(new[] { "-zantetsuSplitOneComponent", "record" }), Is.Empty);
            AssertSettings(8, true, false, "recorded only");

            Assert.That(CutSpatialSplitLaunch.Apply(new[] { "-zantetsuSplitOneComponent", "skip", "-zantetsuSplitAttempts", "3" }), Is.Empty);
            AssertSettings(3, true, true, "three attempts, passed over");

            Assert.That(CutSpatialSplitLaunch.Apply(new[] { "-zantetsuSplitAttempts", "1" }), Is.Empty);
            AssertSettings(1, true, true, "one attempt at a time, with the pass-over");
        }

        [Test]
        public void AnArgumentThatCannotBeRead_KeepsTheDefault_AndSaysSo()
        {
            LogAssert.Expect(LogType.Error, new Regex("SPLIT SETTINGS: .*takes 1\\.\\.64.*takes off, record or skip"));
            string errors = CutSpatialSplitLaunch.Apply(new[] { "-zantetsuSplitAttempts", "many", "-zantetsuSplitOneComponent", "on" });
            Assert.That(errors, Is.Not.Empty);
            AssertSettings(8, true, true, "unreadable values");
        }
    }
}
