using System;
using UnityEngine;
using Zantetsu.Core.Statistics;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The first-run consent checked on a device that has answered already (DESIGN 21.18, unit 7-A): with the Player
    /// argument <see cref="Argument"/> the statistics service keeps its consent and install id under a separate pair
    /// of keys (<see cref="CheckStore"/>), loads no send configuration and so sends nothing; the game's own settings
    /// and the normal consent and id are neither read nor written. With <see cref="ResetArgument"/> those two check
    /// keys alone are deleted, once, at that launch, so that the next launch with <see cref="Argument"/> starts
    /// undecided and the existing Setup -> AskConsent path shows the dialog. Nothing of this acts at a normal launch
    /// (no argument): the normal store, the normal configuration. The verification build (ZANTETSU_STATS_VERIFY)
    /// fixes the answer and is not used for this.
    /// </summary>
    public static class PlayStatisticsConsentCheck
    {
        public const string Prefix = "PLAY STATISTICS CONSENT CHECK: ";

        /// <summary>The consent and install id live under the check keys; no send configuration is loaded.</summary>
        public const string Argument = "-zantetsuConsentCheck";

        /// <summary>The two check keys are deleted at this launch (and nothing else).</summary>
        public const string ResetArgument = "-zantetsuConsentCheckReset";

        /// <summary>What is put before a key in the check store: "zantetsu.statistics.consent" becomes "zantetsu.check.zantetsu.statistics.consent".</summary>
        public const string KeyPrefix = "zantetsu.check.";

        public enum Mode { Normal, Check }

        public static bool AskedFor(string[] arguments) => Has(arguments, Argument);

        public static bool ResetAskedFor(string[] arguments) => Has(arguments, ResetArgument);

        private static bool Has(string[] arguments, string argument)
        {
            if (arguments == null) return false;
            foreach (string a in arguments) if (string.Equals(a, argument, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>The mode the arguments ask for: Check with <see cref="Argument"/>, Normal otherwise (the reset argument alone changes no mode).</summary>
        public static Mode Select(string[] arguments) => AskedFor(arguments) ? Mode.Check : Mode.Normal;

        /// <summary>The keys the check store writes for the consent's two keys.</summary>
        public static string CheckKey(string key) => KeyPrefix + key;

        /// <summary>
        /// Deletes the two check keys from the backing store (and saves), nothing else. Returns how many of the two
        /// existed. The normal consent, the normal install id and the game's settings are untouched by construction:
        /// only the two mapped names are deleted.
        /// </summary>
        public static int Reset(IPlayStatisticsStore backing)
        {
            int existed = 0;
            foreach (string key in new[] { PlayStatisticsConsent.DecisionKey, PlayStatisticsConsent.InstallIdKey })
            {
                string mapped = CheckKey(key);
                if (backing.TryGet(mapped, out _)) existed++;
                backing.Delete(mapped);
            }

            backing.Save();
            return existed;
        }

        /// <summary>
        /// The store the service uses at this launch (the Player's arguments): the check store over the given backing
        /// with the reset applied first when asked, or the backing itself. The log says which, once.
        /// </summary>
        public static IPlayStatisticsStore StoreForThisLaunch(IPlayStatisticsStore backing, string[] arguments)
        {
            if (ResetAskedFor(arguments))
            {
                int existed = Reset(backing);
                Debug.Log(Prefix + "the two check keys were deleted (" + existed + " existed); the normal consent, id and settings were not touched (" + ResetArgument + ")");
            }

            if (!AskedFor(arguments)) return backing;
            Debug.Log(Prefix + "the consent and install id are kept under the check keys (" + KeyPrefix + "...), no send configuration is loaded, nothing is sent (" + Argument + ")");
            return new CheckStore(backing);
        }

        /// <summary>A store that puts every key under <see cref="KeyPrefix"/> in the backing store: the normal keys are never read or written through it.</summary>
        public sealed class CheckStore : IPlayStatisticsStore
        {
            private readonly IPlayStatisticsStore _backing;

            public CheckStore(IPlayStatisticsStore backing) => _backing = backing ?? throw new ArgumentNullException(nameof(backing));

            public bool TryGet(string key, out string value) => _backing.TryGet(CheckKey(key), out value);
            public void Set(string key, string value) => _backing.Set(CheckKey(key), value);
            public void Delete(string key) => _backing.Delete(CheckKey(key));
            public void Save() => _backing.Save();
        }
    }
}
