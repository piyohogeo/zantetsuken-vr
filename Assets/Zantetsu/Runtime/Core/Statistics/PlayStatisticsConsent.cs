using System;

namespace Zantetsu.Core.Statistics
{
    /// <summary>Where the consent and the install id are kept between launches (PlayerPrefs in the game; memory in a test).</summary>
    public interface IPlayStatisticsStore
    {
        bool TryGet(string key, out string value);
        void Set(string key, string value);
        void Delete(string key);
        void Save();
    }

    public enum PlayStatisticsDecision
    {
        /// <summary>Never asked, or asked and not answered: nothing is collected or sent.</summary>
        Undecided = 0,
        Agreed = 1,
        Declined = 2,
    }

    /// <summary>
    /// The player's consent to the play statistics (DESIGN 21.18): asked once, kept, changeable in the settings. Nothing
    /// is collected or sent before an agreement; a decline or a withdrawal stops collection and sending and forgets
    /// the install id. The install id is a random GUID made at the agreement -- never a device id.
    /// </summary>
    public sealed class PlayStatisticsConsent
    {
        public const string DecisionKey = "zantetsu.statistics.consent";
        public const string InstallIdKey = "zantetsu.statistics.installId";

        /// <summary>The key of the consent text's version the saved answer was given to.</summary>
        public const string TextVersionKey = "zantetsu.statistics.consentVersion";

        /// <summary>
        /// The consent text's version now. An agreement saved to an older text (or to none: the texts before this key
        /// existed) is not taken as an agreement to this one: the player is asked again, and nothing is collected until
        /// they answer. The saved answer and the game's other settings are left as they are until then. A saved decline
        /// stands whatever its version (nobody is asked again to let something be sent).
        /// Versions: 1 = the texts up to 2026-10-17 (retention "to be decided"); 2 = the ALPHA text (unit 9-B: 90-day
        /// retention, the Quest's send trigger, withdrawal).
        /// </summary>
        public const int CurrentTextVersion = 2;

        private readonly IPlayStatisticsStore _store;

        public PlayStatisticsConsent(IPlayStatisticsStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            Decision = PlayStatisticsDecision.Undecided;
            if (_store.TryGet(DecisionKey, out string decision))
            {
                if (decision == "agreed") Decision = PlayStatisticsDecision.Agreed;
                else if (decision == "declined") Decision = PlayStatisticsDecision.Declined;
            }

            SavedTextVersion = _store.TryGet(TextVersionKey, out string version) && int.TryParse(version, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int v) ? v : 0;
            if (Decision == PlayStatisticsDecision.Agreed && SavedTextVersion != CurrentTextVersion)
            {
                // Agreed to another text: asked again; the saved values stay until the new answer (nothing else is touched).
                ReconsentNeeded = true;
                Decision = PlayStatisticsDecision.Undecided;
            }

            if (Decision == PlayStatisticsDecision.Agreed && _store.TryGet(InstallIdKey, out string id) && Guid.TryParse(id, out Guid parsed))
            {
                InstallId = parsed.ToString();
            }
            else if (Decision == PlayStatisticsDecision.Agreed)
            {
                // Agreed, but no usable id was kept: a new one; the agreement itself stands.
                InstallId = NewId();
                _store.Set(InstallIdKey, InstallId);
                _store.Save();
            }
        }

        public PlayStatisticsDecision Decision { get; private set; }

        /// <summary>The random install id, or null where there is no agreement.</summary>
        public string InstallId { get; private set; }

        /// <summary>The consent text's version the saved answer was given to (0: none saved).</summary>
        public int SavedTextVersion { get; private set; }

        /// <summary>A saved agreement to another version of the text was found: the player is asked again (Decision is Undecided until they answer).</summary>
        public bool ReconsentNeeded { get; private set; }

        public bool Agreed => Decision == PlayStatisticsDecision.Agreed;

        /// <summary>
        /// Whether an agreement now must be given to the current text shown first (TL review 2026-10-18): the saved answer,
        /// whatever it is, was not given to this version (an old or version-less decline, an old agreement asked again,
        /// nothing saved). A decline saved to the current text was given with that text in view, so the setting may agree
        /// at once.
        /// </summary>
        public bool AgreementNeedsCurrentText => !Agreed && SavedTextVersion != CurrentTextVersion;

        /// <summary>The player agreed to the current text: an install id is made now (a new one each agreement) and all three are kept.</summary>
        public void Agree()
        {
            Decision = PlayStatisticsDecision.Agreed;
            InstallId = NewId();
            ReconsentNeeded = false;
            SavedTextVersion = CurrentTextVersion;
            _store.Set(DecisionKey, "agreed");
            _store.Set(InstallIdKey, InstallId);
            _store.Set(TextVersionKey, CurrentTextVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _store.Save();
            Changed?.Invoke(Decision);
        }

        /// <summary>The player declined, or withdrew: kept as declined (to the current text), the install id forgotten.</summary>
        public void Decline()
        {
            Decision = PlayStatisticsDecision.Declined;
            InstallId = null;
            ReconsentNeeded = false;
            SavedTextVersion = CurrentTextVersion;
            _store.Set(DecisionKey, "declined");
            _store.Delete(InstallIdKey);
            _store.Set(TextVersionKey, CurrentTextVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _store.Save();
            Changed?.Invoke(Decision);
        }

        /// <summary>After the decision changed, with the new one.</summary>
        public event Action<PlayStatisticsDecision> Changed;

        private static string NewId() => Guid.NewGuid().ToString();
    }

    /// <summary>A store in memory, for tests and for a game that keeps nothing.</summary>
    public sealed class MemoryPlayStatisticsStore : IPlayStatisticsStore
    {
        private readonly System.Collections.Generic.Dictionary<string, string> _values = new System.Collections.Generic.Dictionary<string, string>();

        public int Saves { get; private set; }

        public bool TryGet(string key, out string value) => _values.TryGetValue(key, out value);
        public void Set(string key, string value) => _values[key] = value;
        public void Delete(string key) => _values.Remove(key);
        public void Save() => Saves++;
    }
}
