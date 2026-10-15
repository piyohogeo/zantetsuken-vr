using NUnit.Framework;
using Zantetsu.Quest;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// A Quest launch's configuration read for its scenario (DESIGN 3.4): a scripted scenario takes the file as it
    /// is; play takes the common settings alone, so that no file and a file left by an experiment start the same play.
    /// </summary>
    public sealed class QuestScenarioConfigTests
    {
        private static string Check(string json, SandboxScenarioKind kind, out string mode, out bool appSw, out bool distance)
        {
            return QuestNpcSlashDemo.CheckLaunch(json, kind, out mode, out appSw, out distance);
        }

        private static QuestNpcSlashDemo.Config ReadPlay(string json) => QuestNpcSlashDemo.ReadLaunch(json, SandboxScenarioKind.Play);

        [Test]
        public void Play_WithNoFile_HasNoDistanceExperiment()
        {
            string failure = Check(null, SandboxScenarioKind.Play, out string mode, out bool appSw, out bool distance);
            Assert.That(failure, Is.Null);
            Assert.That(mode, Is.EqualTo("vp3c-native"), "the product's route");
            Assert.That(appSw, Is.True);
            Assert.That(distance, Is.False, "the experiment is the scripted runs': never play's, whatever the class's default is");

            // The same absence of a file for a scripted run keeps the experiment its default has.
            Assert.That(Check(null, SandboxScenarioKind.CityWalk, out mode, out appSw, out distance), Is.Null);
            Assert.That(distance, Is.True);
        }

        [Test]
        public void Play_WithAnExperimentsFileLeftBehind_StartsTheSamePlay()
        {
            // What a city walk's script wrote: the distance experiment, diagnoses of the compaction, a profile.
            const string left = "{ \"scenario\": \"play\", \"mode\": \"vp3c-native\", \"appSw\": true, \"profiler\": true, \"appSwDistanceExperiment\": true,"
                + " \"appSwFarMeters\": 30, \"appSwFixedView\": \"capture\", \"nativeIndirectCountVerify\": true, \"nativeIndirectCountPoison\": true,"
                + " \"inputStart\": -5, \"inputRows\": 0, \"cityWalkBudgetSeconds\": -1 }";
            string failure = Check(left, SandboxScenarioKind.Play, out string mode, out bool appSw, out bool distance);
            Assert.That(failure, Is.Null, "nothing of a script is asked of play: not its input range, not its budget");
            Assert.That(new object[] { mode, appSw, distance }, Is.EqualTo(new object[] { "vp3c-native", true, false }));

            // The experiment's file with the other route: the experiment would refuse it; play starts.
            const string unityCalls = "{ \"mode\": \"vp3c\", \"appSw\": true, \"appSwDistanceExperiment\": true, \"appSwFarMeters\": 30 }";
            failure = Check(unityCalls, SandboxScenarioKind.Play, out mode, out appSw, out distance);
            Assert.That(failure, Is.Null, "the experiment's condition is not play's");
            Assert.That(new object[] { mode, appSw, distance }, Is.EqualTo(new object[] { "vp3c", true, false }));
            Assert.That(Check(unityCalls, SandboxScenarioKind.CityWalk, out mode, out appSw, out distance), Is.EqualTo("distance experiment requires AppSW and vp3c-native"),
                "for the scripted run the same file is refused as before");
            Assert.That(Check(unityCalls, SandboxScenarioKind.NpcSlash, out mode, out appSw, out distance), Is.EqualTo("distance experiment requires AppSW and vp3c-native"));
        }

        [Test]
        public void Play_TakesTheCommonSettings_TheModeAppSwAndTheApplicationSpace()
        {
            Assert.That(Check("{ \"mode\": \"vp3c\", \"appSw\": false, \"appSpacePoseMode\": \"camera\" }", SandboxScenarioKind.Play, out string mode, out bool appSw, out bool distance), Is.Null);
            Assert.That(new object[] { mode, appSw, distance }, Is.EqualTo(new object[] { "vp3c", false, false }));
            Assert.That(Check("{ \"mode\": \"unity\" }", SandboxScenarioKind.Play, out mode, out appSw, out distance), Is.EqualTo("mode must be vp3c or vp3c-native"));
            Assert.That(Check("{ \"appSpacePoseMode\": \"head\" }", SandboxScenarioKind.Play, out mode, out appSw, out distance), Is.EqualTo("appSpacePoseMode must be camera or tracking-origin"));
        }

        [Test]
        public void Play_CarriesTheBentPartitionsSwitch_AndWithoutItPlayIsAsBefore()
        {
            // The bent partition's verification switch (DESIGN 7.9.2) reaches play (qBent2 of 2026-10-15 lost it);
            // the other switches of a scripted run or an experiment still do not.
            Assert.That(ReadPlay("{ \"spatialSplitBent\": true }").spatialSplitBent, Is.True, "asked: carried into play");
            Assert.That(ReadPlay("{ \"spatialSplitBent\": false }").spatialSplitBent, Is.False);
            Assert.That(ReadPlay("{ \"mode\": \"vp3c-native\" }").spatialSplitBent, Is.False, "not asked: play as before");
            Assert.That(ReadPlay(null).spatialSplitBent, Is.False, "no file: play as before");
            var left = ReadPlay("{ \"spatialSplitBent\": true, \"appSwDistanceExperiment\": true, \"profiler\": true }");
            Assert.That(new object[] { left.spatialSplitBent, left.appSwDistanceExperiment, left.profiler }, Is.EqualTo(new object[] { true, false, false }),
                "the experiment's and the profiler's switches are still left at what play needs");
        }

        [Test]
        public void AScriptedScenario_IsAskedWhatItAlwaysWas()
        {
            Assert.That(Check("{ \"inputRows\": 0 }", SandboxScenarioKind.NpcSlash, out string mode, out bool appSw, out bool distance), Is.EqualTo("invalid input range or start delay"));
            Assert.That(Check("{ \"cityWalkBudgetSeconds\": 0 }", SandboxScenarioKind.CityWalk, out mode, out appSw, out distance), Is.EqualTo("invalid city walk budget"));
            Assert.That(Check("{ \"cityWalkBudgetSeconds\": 0 }", SandboxScenarioKind.NpcSlash, out mode, out appSw, out distance), Is.Null, "the budget is the city walk's alone");
            Assert.That(Check("{ \"mode\": \"vp3c\", \"appSwDistanceExperiment\": false }", SandboxScenarioKind.CityWalk, out mode, out appSw, out distance), Is.Null);
            Assert.That(distance, Is.False);
            Assert.That(Check("{ \"mode\": \"vp3c-native\", \"appSw\": false }", SandboxScenarioKind.CityWalk, out mode, out appSw, out distance), Is.Null);
            Assert.That(distance, Is.False, "no AppSW, no experiment of it");
        }
    }
}
