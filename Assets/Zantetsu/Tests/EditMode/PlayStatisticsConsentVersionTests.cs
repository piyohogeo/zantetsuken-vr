using NUnit.Framework;
using Zantetsu.Core.Statistics;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The consent text's version (unit 9-B): an agreement saved to an older text is not taken as an agreement to the
    /// current one -- the player is asked again, nothing is collected until they answer, and nothing else saved is
    /// touched; a saved decline stands; a new answer carries the current version.
    /// </summary>
    public sealed class PlayStatisticsConsentVersionTests
    {
        [Test]
        public void AnAgreementSavedToAnOlderText_IsAskedAgain_AndTheSavedValuesStayUntilTheAnswer()
        {
            var store = new MemoryPlayStatisticsStore();
            store.Set(PlayStatisticsConsent.DecisionKey, "agreed");
            store.Set(PlayStatisticsConsent.InstallIdKey, System.Guid.NewGuid().ToString());
            store.Set("zantetsu.settings.volume", "0.55");   // another setting, not the statistics'
            // No version key: the texts before 2026-10-17 (version 1).
            var consent = new PlayStatisticsConsent(store);
            Assert.That(consent.Decision, Is.EqualTo(PlayStatisticsDecision.Undecided), "asked again");
            Assert.That(consent.ReconsentNeeded, Is.True);
            Assert.That(consent.SavedTextVersion, Is.Zero);
            Assert.That(consent.Agreed, Is.False, "nothing is collected until the new answer");
            Assert.That(store.TryGet(PlayStatisticsConsent.DecisionKey, out string saved) && saved == "agreed", Is.True, "the old answer is left as it was");
            Assert.That(store.TryGet("zantetsu.settings.volume", out string volume) && volume == "0.55", Is.True, "nothing else is touched");
            Assert.That(store.Saves, Is.Zero, "nothing was written by the reading");

            store.Set(PlayStatisticsConsent.TextVersionKey, "1");
            consent = new PlayStatisticsConsent(store);
            Assert.That(consent.Decision, Is.EqualTo(PlayStatisticsDecision.Undecided), "version 1 is an older text too");
            Assert.That(consent.SavedTextVersion, Is.EqualTo(1));
        }

        [Test]
        public void TheNewAnswer_CarriesTheCurrentVersion_AndIsKept()
        {
            var store = new MemoryPlayStatisticsStore();
            store.Set(PlayStatisticsConsent.DecisionKey, "agreed");
            var consent = new PlayStatisticsConsent(store);
            Assume.That(consent.ReconsentNeeded, Is.True);
            consent.Agree();
            Assert.That(consent.Agreed, Is.True);
            Assert.That(consent.ReconsentNeeded, Is.False);
            Assert.That(consent.SavedTextVersion, Is.EqualTo(PlayStatisticsConsent.CurrentTextVersion));
            Assert.That(store.TryGet(PlayStatisticsConsent.TextVersionKey, out string version) && version == PlayStatisticsConsent.CurrentTextVersion.ToString(), Is.True);
            var again = new PlayStatisticsConsent(store);
            Assert.That(again.Decision, Is.EqualTo(PlayStatisticsDecision.Agreed), "the agreement to the current text stands at the next launch");
            Assert.That(again.ReconsentNeeded, Is.False);
            Assert.That(again.InstallId, Is.EqualTo(consent.InstallId));
        }

        [Test]
        public void ADeclineOrWithdrawal_Stands_WhateverItsVersion_AndIsWrittenWithTheCurrentOne()
        {
            var store = new MemoryPlayStatisticsStore();
            store.Set(PlayStatisticsConsent.DecisionKey, "declined");   // no version: an old decline
            var consent = new PlayStatisticsConsent(store);
            Assert.That(consent.Decision, Is.EqualTo(PlayStatisticsDecision.Declined), "nobody is asked again to let something be sent");
            Assert.That(consent.ReconsentNeeded, Is.False);

            store = new MemoryPlayStatisticsStore();
            consent = new PlayStatisticsConsent(store);
            consent.Agree();
            consent.Decline();
            Assert.That(store.TryGet(PlayStatisticsConsent.TextVersionKey, out string version) && version == PlayStatisticsConsent.CurrentTextVersion.ToString(), Is.True);
            Assert.That(store.TryGet(PlayStatisticsConsent.InstallIdKey, out _), Is.False, "the id is forgotten");
            Assert.That(new PlayStatisticsConsent(store).Decision, Is.EqualTo(PlayStatisticsDecision.Declined));
        }

        [Test]
        public void NothingSaved_IsUndecided_NotAReconsent()
        {
            var consent = new PlayStatisticsConsent(new MemoryPlayStatisticsStore());
            Assert.That(consent.Decision, Is.EqualTo(PlayStatisticsDecision.Undecided));
            Assert.That(consent.ReconsentNeeded, Is.False, "the first question, not a repeated one");
            Assert.That(consent.SavedTextVersion, Is.Zero);
        }
    }
}
