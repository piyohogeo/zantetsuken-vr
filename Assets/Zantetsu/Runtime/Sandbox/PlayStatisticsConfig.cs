using UnityEngine;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// Where the play statistics are sent, and with what (DESIGN 21.18): a local asset under Resources that is NOT in
    /// the repository (.gitignore), made by the Editor tool from environment variables, as the telemetry probe's was. A
    /// build without it sends nothing and says so. The token is the receiver's ingest token: it ships inside the
    /// distributed build, where it can be taken out, so it is no proof of who sends -- the receiver's own size, shape
    /// and rate limits are what stand against misuse. No management key is ever here.
    /// </summary>
    [CreateAssetMenu(fileName = ResourceName, menuName = "Zantetsu/Play statistics config (local, not committed)")]
    public sealed class PlayStatisticsConfig : ScriptableObject
    {
        public const string ResourceName = "ZantetsuPlayStatisticsConfig";

        [Tooltip("The receiver's URL (the Cloudflare Worker's POST endpoint).")]
        public string endpointUrl = "";

        [Tooltip("The ingest token sent as X-Telemetry-Token. Never logged, never committed.")]
        public string ingestToken = "";

        [Tooltip("Seconds before a send is given up as timed out.")]
        public int timeoutSeconds = 5;

        [Tooltip("Off: nothing is sent, whatever the consent (a build for checking the collection alone).")]
        public bool sendEnabled = true;

        [Tooltip("The receiver's tester_role: 'external' for a distributed build, 'developer' for our own.")]
        public string testerRole = "external";

        /// <summary>Whether a send can be made with this configuration.</summary>
        public bool CanSend => sendEnabled && !string.IsNullOrEmpty(endpointUrl) && !string.IsNullOrEmpty(ingestToken);

        /// <summary>The asset under Resources, or null where the build has none.</summary>
        public static PlayStatisticsConfig Load() => Resources.Load<PlayStatisticsConfig>(ResourceName);

        /// <summary>What the configuration is, for the log: never the token itself.</summary>
        public string Describe()
        {
            return "endpoint " + (string.IsNullOrEmpty(endpointUrl) ? "(none)" : endpointUrl) + ", token " + (string.IsNullOrEmpty(ingestToken) ? "absent" : "present")
                + ", timeout " + timeoutSeconds + " s, send " + (sendEnabled ? "on" : "off") + ", role " + testerRole;
        }
    }
}
