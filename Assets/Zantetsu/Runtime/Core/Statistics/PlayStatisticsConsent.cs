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

        public bool Agreed => Decision == PlayStatisticsDecision.Agreed;

        /// <summary>The player agreed: an install id is made now (a new one each agreement) and both are kept.</summary>
        public void Agree()
        {
            Decision = PlayStatisticsDecision.Agreed;
            InstallId = NewId();
            _store.Set(DecisionKey, "agreed");
            _store.Set(InstallIdKey, InstallId);
            _store.Save();
            Changed?.Invoke(Decision);
        }

        /// <summary>The player declined, or withdrew: kept as declined, the install id forgotten.</summary>
        public void Decline()
        {
            Decision = PlayStatisticsDecision.Declined;
            InstallId = null;
            _store.Set(DecisionKey, "declined");
            _store.Delete(InstallIdKey);
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
