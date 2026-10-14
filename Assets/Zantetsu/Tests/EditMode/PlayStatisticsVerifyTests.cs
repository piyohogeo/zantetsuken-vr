using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Statistics;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The statistics' VERIFICATION build (DESIGN 21.18): its fixed tally, and that the build stays apart from the
    /// product -- the symbol is in no project setting, the observation and the service's measured branch are compiled
    /// under the symbol alone, and with it no send configuration is loaded and no transport is made. The Editor never
    /// defines the symbol, so the last two are held by reading the source.
    /// </summary>
    public sealed class PlayStatisticsVerifyTests
    {
        private const string Symbol = "ZANTETSU_STATS_VERIFY";

        [Test]
        public void ATally_KeepsTheCountTheSumsTheEndsAndTheBuckets_AndNothingOfTheSamples()
        {
            var tally = new PlayVerifyTally(new[] { 1.0, 2.0, 4.0 });
            Assert.That(tally.Describe("us"), Is.EqualTo("n 0"));
            Assert.That(tally.UnderEdgeAt(0.5), Is.EqualTo(0.0));

            foreach (double v in new[] { 0.5, 1.0, 1.5, 3.0, 4.0, 10.0 })
            {
                Assert.That(tally.Add(v), Is.True);
            }

            Assert.That(tally.Count, Is.EqualTo(6UL));
            Assert.That(tally[0], Is.EqualTo(1UL), "under 1");
            Assert.That(tally[1], Is.EqualTo(2UL), "1 is not under 1: it is under 2");
            Assert.That(tally[2], Is.EqualTo(1UL));
            Assert.That(tally.Overflow, Is.EqualTo(2UL), "4 and 10 are at or past the last edge");
            Assert.That(tally[0] + tally[1] + tally[2] + tally.Overflow, Is.EqualTo(tally.Count));
            Assert.That(tally.Sum, Is.EqualTo(20.0).Within(1e-12));
            Assert.That(tally.Mean, Is.EqualTo(20.0 / 6.0).Within(1e-12));
            Assert.That(tally.Min, Is.EqualTo(0.5));
            Assert.That(tally.Max, Is.EqualTo(10.0));
            // The sample standard deviation of the six values.
            double mean = 20.0 / 6.0, squares = 0.0;
            foreach (double v in new[] { 0.5, 1.0, 1.5, 3.0, 4.0, 10.0 }) squares += (v - mean) * (v - mean);
            Assert.That(tally.StandardDeviation, Is.EqualTo(Math.Sqrt(squares / 5.0)).Within(1e-9));

            Assert.That(tally.UnderEdgeAt(0.0), Is.EqualTo(1.0), "the first value");
            Assert.That(tally.UnderEdgeAt(0.5), Is.EqualTo(2.0), "the third of six is under 2");
            Assert.That(tally.UnderEdgeAt(0.6), Is.EqualTo(4.0), "the fourth of six");
            Assert.That(tally.UnderEdgeAt(0.95), Is.EqualTo(double.PositiveInfinity), "in the overflow: under no edge");
            Assert.That(tally.DescribeBuckets(), Is.EqualTo("<1: 1, <2: 2, <4: 1, >=4: 2"));
            Assert.That(tally.Describe("us", "F2"), Does.StartWith("n 6, mean 3.33 us, sd ").And.Contain("p50 <2, p95 <inf, p99 <inf, min 0.50, max 10.00, over the last edge 2"));
        }

        [Test]
        public void ATally_RefusesWhatIsNotAMeasurement_AndCountsItApart()
        {
            var tally = new PlayVerifyTally(PlayVerifyTally.MicrosecondEdges);
            Assert.That(tally.Add(double.NaN), Is.False);
            Assert.That(tally.Add(double.PositiveInfinity), Is.False);
            Assert.That(tally.Add(-0.001), Is.False);
            Assert.That(tally.Count, Is.EqualTo(0UL));
            Assert.That(tally.Refused, Is.EqualTo(3UL));
            Assert.That(tally.Sum, Is.EqualTo(0.0));
            Assert.That(tally.Add(0.0), Is.True, "nothing measured between two reads of the clock is a measurement");
            Assert.That(tally[0], Is.EqualTo(1UL));
            Assert.That(tally.StandardDeviation, Is.EqualTo(0.0), "one value has no deviation");
            Assert.Throws<ArgumentException>(() => new PlayVerifyTally(new[] { 1.0, 1.0 }));
            Assert.Throws<ArgumentException>(() => new PlayVerifyTally(new double[0]));
        }

        [Test]
        public void TheEdges_Rise_QuarterMillisecondsToTwenty_AndATwentiethOfAMicrosecondToTenMilliseconds()
        {
            double[] ms = PlayVerifyTally.MillisecondEdges;
            Assert.That(ms.Length, Is.EqualTo(92));
            Assert.That(ms[0], Is.EqualTo(0.25));
            Assert.That(ms[79], Is.EqualTo(20.0));
            Assert.That(ms[ms.Length - 1], Is.EqualTo(100.0));
            for (int i = 1; i < ms.Length; i++) Assert.That(ms[i], Is.GreaterThan(ms[i - 1]));

            double[] us = PlayVerifyTally.MicrosecondEdges;
            Assert.That(us[0], Is.EqualTo(0.05));
            Assert.That(us[us.Length - 1], Is.EqualTo(10000.0));
            for (int i = 1; i < us.Length; i++) Assert.That(us[i], Is.GreaterThan(us[i - 1]));
        }

        private static object s_one, s_two, s_three;

        [Test]
        public void TheAllocationRecord_CountsWhatEachIntervalAllocates_OneIntervalAfterAnother_AndNothingOutsideThem()
        {
            PlayVerifyAllocationRecord.Open();
            Assert.That(PlayVerifyAllocationRecord.Valid, Is.True, "the engine's record exists in the Editor");

            // The intervals as the verification build makes them, one after another in one frame: each End gives its
            // own interval's allocations, not a sum with those before it.
            var seen = new int[8];
            PlayVerifyAllocationRecord.Begin();
            seen[0] = PlayVerifyAllocationRecord.End();
            PlayVerifyAllocationRecord.Begin();
            s_one = new byte[32];
            seen[1] = PlayVerifyAllocationRecord.End();
            PlayVerifyAllocationRecord.Begin();
            s_one = new byte[32];
            s_two = new byte[32];
            s_three = new byte[32];
            seen[2] = PlayVerifyAllocationRecord.End();
            s_one = new byte[4096];   // outside any interval: counted by none
            s_two = new byte[4096];
            PlayVerifyAllocationRecord.Begin();
            seen[3] = PlayVerifyAllocationRecord.End();
            PlayVerifyAllocationRecord.Begin();
            s_three = new byte[32];
            seen[4] = PlayVerifyAllocationRecord.End();

            Assert.That(seen[0], Is.EqualTo(0), "an empty interval");
            Assert.That(seen[1], Is.EqualTo(1), "one allocation");
            Assert.That(seen[2], Is.EqualTo(3), "three, and not four: the interval before is not in it");
            Assert.That(seen[3], Is.EqualTo(0), "what was allocated between two intervals is in neither");
            Assert.That(seen[4], Is.EqualTo(1));
        }

        [Test]
        public void TheVerificationSymbol_IsInNoProjectSetting_AndItsCodeIsUnderItAlone()
        {
            string project = Path.GetDirectoryName(Application.dataPath);
            string settings = File.ReadAllText(Path.Combine(project, "ProjectSettings", "ProjectSettings.asset"));
            Assert.That(settings, Does.Not.Contain(Symbol), "no product build defines the symbol: only the build entry's argument gives it, to one build");

            string sandbox = Path.Combine(Application.dataPath, "Zantetsu", "Runtime", "Sandbox");
            string verify = File.ReadAllText(Path.Combine(sandbox, "PlayStatisticsVerify.cs")).Replace("\r\n", "\n");
            Assert.That(verify, Does.StartWith("#if " + Symbol + "\n"), "the whole observation is under the symbol");
            Assert.That(verify.TrimEnd(), Does.EndWith("#endif"));

            string tally = File.ReadAllText(Path.Combine(Application.dataPath, "Zantetsu", "Runtime", "Core", "Statistics", "PlayVerifyTally.cs")).Replace("\r\n", "\n");
            Assert.That(tally, Does.StartWith("#if " + Symbol + " || UNITY_EDITOR\n"), "the tally: the verification build, and the Editor for this test; no product Player");
            Assert.That(tally.TrimEnd(), Does.EndWith("#endif"));
        }

        [Test]
        public void WithTheSymbol_TheServiceLoadsNoSendConfiguration_MakesNoTransport_AndKeepsTheSavedConsentUntouched()
        {
            string path = Path.Combine(Application.dataPath, "Zantetsu", "Runtime", "Sandbox", "PlayStatisticsService.cs");
            string source = File.ReadAllText(path).Replace("\r\n", "\n");

            // The install: under the symbol the store is the launch's own and the configuration is null; the product's line is the other branch.
            Assert.That(source, Does.Contain(
                "#if " + Symbol + "\n"
                + "            // The verification build (DESIGN 21.18): the consent is the launch's argument, kept in memory -- the saved\n"
                + "            // consent is neither read nor written -- and no send configuration is loaded.\n"
                + "            service.Setup(kind, scene, PlayStatisticsVerify.StoreForThisLaunch(), null);\n"
                + "#else\n"
                + "            service.Setup(kind, scene, new PlayerPrefsStatisticsStore(), PlayStatisticsConfig.Load());\n"
                + "#endif\n"));

            // The transport: none under the symbol, whatever configuration reaches Setup.
            Assert.That(source, Does.Contain(
                "#if " + Symbol + "\n"
                + "            IPlayStatisticsTransport transport = null;   // the verification build sends nothing, whatever it was given\n"
                + "#else\n"
                + "            IPlayStatisticsTransport transport = config != null && config.CanSend ? new PlayStatisticsUnityTransport(this, config) : null;\n"
                + "#endif\n"));

            // The measured Update: a branch of its own after the Editor/Development one; the product's Release branch is the bare call.
            Assert.That(source, Does.Contain("#elif " + Symbol + "\n"));
            Assert.That(source, Does.Contain("#else\n            UpdateCore();\n#endif\n"), "a product Release build calls the Update's body and nothing round it");

            string verify = File.ReadAllText(Path.Combine(Application.dataPath, "Zantetsu", "Runtime", "Sandbox", "PlayStatisticsVerify.cs"));
            Assert.That(verify, Does.Contain("new MemoryPlayStatisticsStore()"), "the launch's consent is kept in memory");
            Assert.That(verify, Does.Not.Contain("PlayerPrefs."), "nothing of the verification reads or writes the saved settings");
            Assert.That(verify, Does.Not.Contain("PlayStatisticsConfig"), "and nothing of it touches a send configuration");
        }
    }
}
