using NUnit.Framework;
using Zantetsu.Core.Statistics;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>The first-run consent's check path (DESIGN 21.18, unit 7-A): separate keys, a reset of those alone, nothing at a normal launch.</summary>
    public sealed class PlayStatisticsConsentCheckTests
    {
        private static readonly string[] Normal = { "-zantetsuScenario", "play" };
        private static readonly string[] Check = { "-zantetsuScenario", "play", PlayStatisticsConsentCheck.Argument };
        private static readonly string[] CheckAndReset = { PlayStatisticsConsentCheck.Argument, PlayStatisticsConsentCheck.ResetArgument };
        private static readonly string[] ResetAlone = { PlayStatisticsConsentCheck.ResetArgument };

        [Test]
        public void TheArguments_ChooseTheMode_AndTheResetAlone_ChangesNoMode()
        {
            Assert.That(PlayStatisticsConsentCheck.Select(Normal), Is.EqualTo(PlayStatisticsConsentCheck.Mode.Normal));
            Assert.That(PlayStatisticsConsentCheck.Select(null), Is.EqualTo(PlayStatisticsConsentCheck.Mode.Normal));
            Assert.That(PlayStatisticsConsentCheck.Select(Check), Is.EqualTo(PlayStatisticsConsentCheck.Mode.Check));
            Assert.That(PlayStatisticsConsentCheck.Select(ResetAlone), Is.EqualTo(PlayStatisticsConsentCheck.Mode.Normal));
            Assert.That(PlayStatisticsConsentCheck.ResetAskedFor(ResetAlone), Is.True);
            Assert.That(PlayStatisticsConsentCheck.ResetAskedFor(Check), Is.False);
        }

        [Test]
        public void TheCheckStore_KeepsTheConsentAndId_UnderItsOwnKeys_AndLeavesTheNormalOnes()
        {
            var backing = new MemoryPlayStatisticsStore();
            backing.Set(PlayStatisticsConsent.DecisionKey, "agreed");
            backing.Set(PlayStatisticsConsent.InstallIdKey, "11111111-1111-1111-1111-111111111111");
            backing.Set("zantetsu.volume", "0.55");

            var consent = new PlayStatisticsConsent(new PlayStatisticsConsentCheck.CheckStore(backing));
            Assert.That(consent.Decision, Is.EqualTo(PlayStatisticsDecision.Undecided), "the normal agreement is not read through the check store");
            consent.Decline();
            consent.Agree();

            Assert.That(backing.TryGet(PlayStatisticsConsent.DecisionKey, out string normal) && normal == "agreed", Is.True, "the normal consent is as it was");
            Assert.That(backing.TryGet(PlayStatisticsConsent.InstallIdKey, out string id) && id == "11111111-1111-1111-1111-111111111111", Is.True, "the normal id is as it was");
            Assert.That(backing.TryGet("zantetsu.volume", out string volume) && volume == "0.55", Is.True, "a game setting is as it was");
            Assert.That(backing.TryGet(PlayStatisticsConsentCheck.CheckKey(PlayStatisticsConsent.DecisionKey), out string check) && check == "agreed", Is.True);
            Assert.That(backing.TryGet(PlayStatisticsConsentCheck.CheckKey(PlayStatisticsConsent.InstallIdKey), out string checkId) && checkId != id, Is.True, "its own id");
        }

        [Test]
        public void TheReset_DeletesTheTwoCheckKeysAlone_AndSaves()
        {
            var backing = new MemoryPlayStatisticsStore();
            backing.Set(PlayStatisticsConsent.DecisionKey, "declined");
            backing.Set(PlayStatisticsConsentCheck.CheckKey(PlayStatisticsConsent.DecisionKey), "agreed");
            backing.Set(PlayStatisticsConsentCheck.CheckKey(PlayStatisticsConsent.InstallIdKey), "22222222-2222-2222-2222-222222222222");
            backing.Set("zantetsu.volume", "0.55");
            int saves = backing.Saves;

            Assert.That(PlayStatisticsConsentCheck.Reset(backing), Is.EqualTo(2));
            Assert.That(backing.TryGet(PlayStatisticsConsentCheck.CheckKey(PlayStatisticsConsent.DecisionKey), out _), Is.False);
            Assert.That(backing.TryGet(PlayStatisticsConsentCheck.CheckKey(PlayStatisticsConsent.InstallIdKey), out _), Is.False);
            Assert.That(backing.TryGet(PlayStatisticsConsent.DecisionKey, out string normal) && normal == "declined", Is.True, "the normal consent stays");
            Assert.That(backing.TryGet("zantetsu.volume", out _), Is.True, "a game setting stays");
            Assert.That(backing.Saves, Is.EqualTo(saves + 1));
            Assert.That(PlayStatisticsConsentCheck.Reset(backing), Is.Zero, "nothing left to delete");
        }

        [Test]
        public void TheStoreForALaunch_IsTheBackingItself_WithoutTheArgument_AndTheCheckStore_WithIt()
        {
            var backing = new MemoryPlayStatisticsStore();
            backing.Set(PlayStatisticsConsentCheck.CheckKey(PlayStatisticsConsent.DecisionKey), "agreed");
            backing.Set(PlayStatisticsConsentCheck.CheckKey(PlayStatisticsConsent.TextVersionKey), PlayStatisticsConsent.CurrentTextVersion.ToString());

            Assert.That(PlayStatisticsConsentCheck.StoreForThisLaunch(backing, Normal), Is.SameAs(backing), "a normal launch: nothing in between, nothing deleted");
            Assert.That(backing.TryGet(PlayStatisticsConsentCheck.CheckKey(PlayStatisticsConsent.DecisionKey), out _), Is.True, "the check keys are not deleted at a normal launch");

            IPlayStatisticsStore check = PlayStatisticsConsentCheck.StoreForThisLaunch(backing, Check);
            Assert.That(check, Is.InstanceOf<PlayStatisticsConsentCheck.CheckStore>());
            Assert.That(new PlayStatisticsConsent(check).Decision, Is.EqualTo(PlayStatisticsDecision.Agreed), "the check consent kept from the earlier check launch");

            IPlayStatisticsStore reset = PlayStatisticsConsentCheck.StoreForThisLaunch(backing, CheckAndReset);
            Assert.That(new PlayStatisticsConsent(reset).Decision, Is.EqualTo(PlayStatisticsDecision.Undecided), "reset first: the dialog's starting state");
        }
    }
}
