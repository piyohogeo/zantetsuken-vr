using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.Editor.Sandbox
{
    /// <summary>
    /// Makes the local play statistics configuration (DESIGN 21.18) under Resources from environment variables, so that
    /// the token is never typed into a file that could be committed: <c>ZANTETSU_INGEST_TOKEN</c>, <c>ZANTETSU_STATS_ENDPOINT</c>,
    /// <c>ZANTETSU_STATS_TIMEOUT</c> (seconds), <c>ZANTETSU_STATS_SEND</c> (1/0), <c>ZANTETSU_STATS_ROLE</c>
    /// (external/developer). The asset's path is in .gitignore. Menu "Zantetsu > Play statistics > Make the local config
    /// from the environment", or <c>-executeMethod Zantetsu.Editor.Sandbox.PlayStatisticsConfigSetup.FromEnvironment</c>.
    /// </summary>
    public static class PlayStatisticsConfigSetup
    {
        public const string AssetPath = "Assets/Zantetsu/Runtime/Sandbox/Resources/" + PlayStatisticsConfig.ResourceName + ".asset";

        [MenuItem("Zantetsu/Play statistics/Make the local config from the environment")]
        public static void FromEnvironment()
        {
            string directory = Path.GetDirectoryName(AssetPath);
            if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);
            var config = AssetDatabase.LoadAssetAtPath<PlayStatisticsConfig>(AssetPath);
            bool made = config == null;
            if (made)
            {
                config = ScriptableObject.CreateInstance<PlayStatisticsConfig>();
            }

            config.endpointUrl = Environment.GetEnvironmentVariable("ZANTETSU_STATS_ENDPOINT") ?? config.endpointUrl ?? "";
            config.ingestToken = Environment.GetEnvironmentVariable("ZANTETSU_INGEST_TOKEN") ?? config.ingestToken ?? "";
            string timeout = Environment.GetEnvironmentVariable("ZANTETSU_STATS_TIMEOUT");
            if (int.TryParse(timeout, out int seconds) && seconds > 0) config.timeoutSeconds = seconds;
            string send = Environment.GetEnvironmentVariable("ZANTETSU_STATS_SEND");
            if (send != null) config.sendEnabled = send != "0";
            string role = Environment.GetEnvironmentVariable("ZANTETSU_STATS_ROLE");
            if (!string.IsNullOrEmpty(role)) config.testerRole = role;
            if (made)
            {
                AssetDatabase.CreateAsset(config, AssetPath);
            }
            else
            {
                EditorUtility.SetDirty(config);
            }

            AssetDatabase.SaveAssets();
            Debug.Log("PLAY STATISTICS CONFIG: " + (made ? "made" : "updated") + " " + AssetPath + " -- " + config.Describe());
        }
    }
}
