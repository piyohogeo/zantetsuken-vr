using NUnit.Framework;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The launch's scenario (DESIGN 3.4): play unless another is named, an unknown value reported, and the arguments
    /// of a scripted entry accepted only with that entry's scenario -- never run as something else.
    /// </summary>
    public sealed class SandboxScenarioTests
    {
        private const string PropSlash = SandboxPropSlashPlayerCheck.Argument;
        private const string CityWalk = SandboxPropSlashPlayerCheck.CityWalkArgument;
        private const string DrawCompare = SandboxDrawComparePlayerCheck.Argument;

        private static SandboxScenarioChoice Read(params string[] arguments)
        {
            var all = new string[arguments.Length + 1];
            all[0] = "Player.exe";
            arguments.CopyTo(all, 1);
            return SandboxScenario.Read(all);
        }

        [Test]
        public void NoArgument_IsPlay()
        {
            SandboxScenarioChoice choice = Read();
            Assert.That(choice.Failure, Is.Null);
            Assert.That(choice.Kind, Is.EqualTo(SandboxScenarioKind.Play));
            Assert.That(choice.ToString(), Is.EqualTo("play (the default: no -zantetsuScenario)"));
            Assert.That(SandboxScenario.Read(null).Kind, Is.EqualTo(SandboxScenarioKind.Play));

            // Arguments that start nothing by themselves leave it play: the draw route, the window, a diagnosis.
            choice = Read("-force-d3d12", "-zantetsuVp3cNative", "-screen-width", "960", "-zantetsuDisableNumericDiagnostics");
            Assert.That(choice.Failure, Is.Null);
            Assert.That(choice.Kind, Is.EqualTo(SandboxScenarioKind.Play));
        }

        [Test]
        public void TheFourValues_AreReadByName_AndOnlyThose()
        {
            Assert.That(SandboxScenario.Argument, Is.EqualTo("-zantetsuScenario"));
            Assert.That(Read("-zantetsuScenario", "play").Kind, Is.EqualTo(SandboxScenarioKind.Play));
            Assert.That(Read("-zantetsuScenario", "play").Failure, Is.Null);
            Assert.That(Read("-zantetsuScenario", "static-wait", DrawCompare, "d").Kind, Is.EqualTo(SandboxScenarioKind.StaticWait));
            Assert.That(Read("-zantetsuScenario", "city-walk", PropSlash, "d", CityWalk).Kind, Is.EqualTo(SandboxScenarioKind.CityWalk));
            Assert.That(Read("-zantetsuScenario", "npc-slash", PropSlash, "d").Kind, Is.EqualTo(SandboxScenarioKind.NpcSlash));
            Assert.That(SandboxScenario.TryParse("empty-xr", out SandboxScenarioKind empty) && empty == SandboxScenarioKind.EmptyXr, Is.True, "the empty XR diagnosis scenario (unit 22)");
            Assert.That(SandboxScenario.Name(SandboxScenarioKind.EmptyXr), Is.EqualTo("empty-xr"));
            Assert.That(SandboxScenario.ScenesFor(SandboxScenarioKind.EmptyXr), Is.EqualTo(new[] { "QuestEmptyXr" }), "its own scene alone: no city scene is a fallback");
            foreach (SandboxScenarioKind kind in new[] { SandboxScenarioKind.Play, SandboxScenarioKind.StaticWait, SandboxScenarioKind.CityWalk, SandboxScenarioKind.NpcSlash })
            {
                Assert.That(SandboxScenario.TryParse(SandboxScenario.Name(kind), out SandboxScenarioKind back), Is.True);
                Assert.That(back, Is.EqualTo(kind));
            }

            foreach (string unknown in new[] { "walk", "Play", "citywalk", "static", "" })
            {
                SandboxScenarioChoice choice = Read("-zantetsuScenario", unknown, PropSlash, "d", CityWalk);
                Assert.That(choice.Failure, Does.Contain("unknown scenario '" + unknown + "'").And.Contain("play, static-wait, city-walk, npc-slash"), unknown);
                Assert.That(choice.ToString(), Does.StartWith("NOT CHOSEN ("), "an unknown value chooses nothing");
            }
        }

        [Test]
        public void AValueMissingOrGivenTwiceDifferently_IsAFailure()
        {
            Assert.That(Read("-zantetsuScenario").Failure, Does.Contain("has no value"));
            Assert.That(Read("-zantetsuScenario", "-force-d3d12").Failure, Does.Contain("has no value"));
            Assert.That(Read("-zantetsuScenario", "play", "-zantetsuScenario", "city-walk").Failure, Does.Contain("given twice"));
            Assert.That(Read("-zantetsuScenario", "play", "-zantetsuScenario", "play").Failure, Is.Null, "the same value twice is one value");
        }

        [Test]
        public void AScriptedEntrysArguments_WithoutItsScenario_AreReported_NotRun()
        {
            // The launches of before this unit: an entry's arguments and no scenario. They are play by default, which
            // runs no script -- so they are a failure, not a walk and not a silent play.
            SandboxScenarioChoice walk = Read(PropSlash, "dir", "-zantetsuMobPlan", "script.txt", CityWalk);
            Assert.That(walk.Failure, Does.Contain("names no scenario").And.Contain(PropSlash).And.Contain("-zantetsuScenario"));
            SandboxScenarioChoice still = Read(DrawCompare, "dir", "-zantetsuDrawCompareMode", "vp3c-native");
            Assert.That(still.Failure, Does.Contain("names no scenario").And.Contain(DrawCompare));
            Assert.That(Read("-zantetsuScenario", "play", PropSlash, "dir").Failure, Is.Not.Null, "named play, the same");
        }

        [Test]
        public void EachScriptedScenario_NeedsItsOwnEntrysArguments_AndRefusesTheOthers()
        {
            Assert.That(Read("-zantetsuScenario", "static-wait").Failure, Does.Contain("needs " + DrawCompare));
            Assert.That(Read("-zantetsuScenario", "static-wait", PropSlash, "d").Failure, Does.Contain("does not run " + PropSlash));
            Assert.That(Read("-zantetsuScenario", "city-walk").Failure, Does.Contain("needs " + PropSlash));
            Assert.That(Read("-zantetsuScenario", "city-walk", PropSlash, "d").Failure, Does.Contain("needs " + CityWalk));
            Assert.That(Read("-zantetsuScenario", "city-walk", DrawCompare, "d").Failure, Does.Contain("does not run " + DrawCompare));
            Assert.That(Read("-zantetsuScenario", "npc-slash").Failure, Does.Contain("needs " + PropSlash));
            Assert.That(Read("-zantetsuScenario", "npc-slash", PropSlash, "d", CityWalk).Failure, Does.Contain("does not run " + CityWalk));
            Assert.That(Read("-zantetsuScenario", "npc-slash", DrawCompare, "d").Failure, Does.Contain("does not run " + DrawCompare));
        }

        [Test]
        public void ADevelopmentCheck_RunsAlone_AndIsRefusedBesideAScenarioAScriptedEntryOrAnotherCheck()
        {
            Assert.That(SandboxScenario.DevelopmentCheckArguments, Is.EqualTo(new[]
            {
                "-zantetsuPlayerCheck", "-zantetsuLocomotionCheck", "-zantetsuSlashReplay", "-mobPlanProfile",
            }));
            foreach (string check in SandboxScenario.DevelopmentCheckArguments)
            {
                // Alone, with the arguments that are its own or nobody's: the one thing the launch runs.
                SandboxScenarioChoice alone = Read(check, "dir", "-zantetsuSlashInput", "input.csv", "-zantetsuSlashStart", "5", "-force-d3d12", "-zantetsuViewDiag");
                Assert.That(alone.Failure, Is.Null, check);
                Assert.That(alone.DevelopmentCheck, Is.EqualTo(check));
                Assert.That(alone.ToString(), Is.EqualTo("none (the development check " + check + " runs alone, started by its own argument)"));

                // Beside every scenario, named or meant by its entry's arguments: refused, whichever way round.
                foreach (string scenario in new[] { "play", "static-wait", "city-walk", "npc-slash" })
                {
                    SandboxScenarioChoice named = Read("-zantetsuScenario", scenario, PropSlash, "d", CityWalk, DrawCompare, "d", check, "dir");
                    Assert.That(named.Failure, Does.Contain("the development check " + check).And.Contain("beside -zantetsuScenario " + scenario), check + " + " + scenario);
                    Assert.That(named.DevelopmentCheck, Is.Null, "a refused launch names no check to start");
                }

                Assert.That(Read("-zantetsuScenario", "city-walk", PropSlash, "d", CityWalk, check, "dir").Failure, Does.Contain("runs alone"));
                Assert.That(Read(check, "dir", "-zantetsuScenario", "npc-slash", PropSlash, "d").Failure, Does.Contain("runs alone"));
                Assert.That(Read(check, "dir", PropSlash, "d").Failure, Does.Contain("beside " + PropSlash).And.Contain("one launch runs one"));
                Assert.That(Read(DrawCompare, "d", check, "dir").Failure, Does.Contain("beside " + DrawCompare));

                foreach (string other in SandboxScenario.DevelopmentCheckArguments)
                {
                    if (other != check)
                    {
                        SandboxScenarioChoice two = Read(check, "dir", other, "dir2");
                        Assert.That(two.Failure, Does.Contain("two development checks").And.Contain(check).And.Contain(other));
                        Assert.That(two.DevelopmentCheck, Is.Null);
                    }
                }
            }

            // A scenario's own launch names no check, and the view diagnosis is none.
            Assert.That(Read("-zantetsuScenario", "city-walk", PropSlash, "d", CityWalk, "-zantetsuViewDiag").DevelopmentCheck, Is.Null);
            Assert.That(Read("-zantetsuScenario", "city-walk", PropSlash, "d", CityWalk, "-zantetsuViewDiag").Failure, Is.Null);
            Assert.That(Read().DevelopmentCheck, Is.Null);
        }

        [Test]
        public void ACheckMayStart_OnlyAsTheLaunchsOneChoice()
        {
            try
            {
                // The launch's one choice is the slash replay check: that check may start, the others may not.
                SandboxScenario.SetForTest(Read(SandboxSlashReplayPlayerCheck.Argument, "dir"));
                Assert.That(SandboxScenario.MayStartDevelopmentCheck(SandboxSlashReplayPlayerCheck.Argument), Is.True);
                Assert.That(SandboxScenario.MayStartDevelopmentCheck(SandboxPlayerCheck.Argument), Is.False);
                Assert.That(SandboxScenario.MayStartDevelopmentCheck(SandboxLocomotionPlayerCheck.Argument), Is.False);

                // The same check named beside a scenario: the launch failed, and the check does not start either --
                // whichever of the two start-up hooks comes first.
                SandboxScenario.SetForTest(Read("-zantetsuScenario", "city-walk", PropSlash, "d", CityWalk, SandboxSlashReplayPlayerCheck.Argument, "dir"));
                Assert.That(SandboxScenario.Current.Failure, Is.Not.Null);
                foreach (string check in SandboxScenario.DevelopmentCheckArguments)
                {
                    Assert.That(SandboxScenario.MayStartDevelopmentCheck(check), Is.False, check);
                }

                // A scenario's own launch, and play: no check starts.
                SandboxScenario.SetForTest(Read("-zantetsuScenario", "city-walk", PropSlash, "d", CityWalk));
                Assert.That(SandboxScenario.MayStartDevelopmentCheck(SandboxSlashReplayPlayerCheck.Argument), Is.False);
                SandboxScenario.SetForTest(Read());
                Assert.That(SandboxScenario.MayStartDevelopmentCheck(SandboxPlayerCheck.Argument), Is.False);
            }
            finally
            {
                SandboxScenario.SetForTest(null);
            }
        }

        [Test]
        public void TheQuestConfiguration_NamesTheScenarioInItsOwnField()
        {
            Assert.That(SandboxScenario.ConfigFile, Is.EqualTo("quest3s-config.json"));
            Assert.That(SandboxScenario.ReadConfig(null).Kind, Is.EqualTo(SandboxScenarioKind.Play), "no file");
            Assert.That(SandboxScenario.ReadConfig(null).Failure, Is.Null);
            Assert.That(SandboxScenario.ReadConfig("{ \"mode\": \"vp3c-native\", \"appSw\": true }").Kind, Is.EqualTo(SandboxScenarioKind.Play), "no field");
            Assert.That(SandboxScenario.ReadConfig("{ \"scenario\": \"\" }").Kind, Is.EqualTo(SandboxScenarioKind.Play));

            // The field beside every other field of the file: the rest is each entry's own and is not read here.
            SandboxScenarioChoice walk = SandboxScenario.ReadConfig(
                "{ \"mode\": \"vp3c-native\", \"scenario\": \"city-walk\", \"cityWalkBudgetSeconds\": 565, \"propChecks\": false }");
            Assert.That(walk.Failure, Is.Null);
            Assert.That(walk.Kind, Is.EqualTo(SandboxScenarioKind.CityWalk));
            Assert.That(walk.ToString(), Is.EqualTo("city-walk (quest3s-config.json scenario city-walk)"));
            Assert.That(SandboxScenario.ReadConfig("{ \"scenario\": \"static-wait\" }").Kind, Is.EqualTo(SandboxScenarioKind.StaticWait));
            Assert.That(SandboxScenario.ReadConfig("{ \"scenario\": \"npc-slash\" }").Kind, Is.EqualTo(SandboxScenarioKind.NpcSlash));
            Assert.That(SandboxScenario.ReadConfig("{ \"scenario\": \"play\" }").Kind, Is.EqualTo(SandboxScenarioKind.Play));

            SandboxScenarioChoice unknown = SandboxScenario.ReadConfig("{ \"scenario\": \"walk\" }");
            Assert.That(unknown.Failure, Does.Contain("unknown scenario 'walk'").And.Contain("quest3s-config.json"));
            Assert.That(SandboxScenario.ReadConfig("{ not json").Failure, Does.Contain("not readable"));
        }

        [Test]
        public void EachScenario_NamesTheScenesItRunsIn()
        {
            Assert.That(SandboxScenario.BootSceneName, Is.EqualTo("ZantetsuBoot"));
            Assert.That(SandboxScenario.ScenesFor(SandboxScenarioKind.Play), Is.EqualTo(new[] { "StanfordBunnyDemo", "WalkCity", "QuestCityWalk" }));
            Assert.That(SandboxScenario.ScenesFor(SandboxScenarioKind.CityWalk), Is.EqualTo(new[] { "WalkCity", "QuestCityWalk" }));
            Assert.That(SandboxScenario.ScenesFor(SandboxScenarioKind.StaticWait), Is.EqualTo(new[] { "WalkCity", "QuestStaticSeed14" }));
            Assert.That(SandboxScenario.ScenesFor(SandboxScenarioKind.NpcSlash), Is.EqualTo(new[] { "MultiNpcCity", "QuestNpcSlash10" }));
        }

        [Test]
        public void TheLaunchRecord_SaysTheScenario()
        {
            Assert.That(SandboxLaunchRecord.Describe(new[] { "player.exe" }), Does.Contain("; scenario: play (the default: no -zantetsuScenario);"));
            Assert.That(
                SandboxLaunchRecord.Describe(new[] { "player.exe", "-zantetsuScenario", "city-walk", PropSlash, "dir", CityWalk }),
                Does.Contain("; scenario: city-walk (-zantetsuScenario city-walk);"));
            Assert.That(SandboxLaunchRecord.Describe(new[] { "player.exe", PropSlash, "dir" }), Does.Contain("; scenario: NOT CHOSEN ("));
        }

        [Test]
        public void TheEditor_StartsNoScenarioByItself()
        {
            // A session's choice is read lazily and can be given by a test; nothing of it runs in the Editor.
            SandboxScenario.SetForTest(new SandboxScenarioChoice(SandboxScenarioKind.CityWalk, "a test", null));
            try
            {
                Assert.That(SandboxScenario.Current.Kind, Is.EqualTo(SandboxScenarioKind.CityWalk));
                Assert.That(PlayMenu.Instance, Is.Null, "no menu was installed by reading it");
            }
            finally
            {
                SandboxScenario.SetForTest(null);
            }

            Assert.That(SandboxScenario.Current.Kind, Is.EqualTo(SandboxScenarioKind.Play), "the Editor's own command line names none");
        }
    }
}
