using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Zantetsu.EditorTools.AndroidFlat
{
    /// <summary>Exports the last build's packed source paths for local distribution review.</summary>
    public static class AndroidFlatDistributionAudit
    {
        [Serializable] private sealed class Inventory
        {
            public string unityVersion, playerOutput, buildStartedAt, buildEndedAt;
            public string[] packedAssets, sceneDependencies;
        }

        public static void ExportLatestBuild()
        {
            var args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, "-zantetsuAndroidFlatAuditOut");
            if (at < 0 || at + 1 >= args.Length) throw new ArgumentException("Specify -zantetsuAndroidFlatAuditOut");
            var report = BuildReport.GetLatestReport();
            if (report == null || report.summary.platform != BuildTarget.Android || report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("The most recent build must be a successful Android build");
            var inventory = new Inventory {
                unityVersion = Application.unityVersion,
                playerOutput = report.summary.outputPath,
                buildStartedAt = report.summary.buildStartedAt.ToUniversalTime().ToString("o"),
                buildEndedAt = report.summary.buildEndedAt.ToUniversalTime().ToString("o"),
                packedAssets = report.packedAssets.SelectMany(p => p.contents).Select(p => p.sourceAssetPath)
                    .Where(p => !string.IsNullOrEmpty(p)).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToArray(),
                sceneDependencies = AssetDatabase.GetDependencies(AndroidFlatBuild.ScenePath, true)
                    .OrderBy(p => p, StringComparer.Ordinal).ToArray()
            };
            if (inventory.packedAssets.Length == 0) throw new InvalidOperationException("The build report contains no packed asset sources");
            string output = Path.GetFullPath(args[at + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllText(output, JsonUtility.ToJson(inventory, true) + "\n");
            Debug.Log("ANDROID FLAT DISTRIBUTION AUDIT: packed=" + inventory.packedAssets.Length
                + "; scene dependencies=" + inventory.sceneDependencies.Length);
        }
    }
}
