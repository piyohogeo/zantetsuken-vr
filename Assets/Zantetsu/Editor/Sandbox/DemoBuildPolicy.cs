using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zantetsu.Sandbox;

namespace Zantetsu.EditorTools.Sandbox
{
    public enum DemoBuildMode { Auto, Demo, Commercial }

    /// <summary>Only an explicit build scope permits packaging Stanford data. All other build entries fail closed.</summary>
    public static class DemoBuildPolicy
    {
        public const string ModeArgument = "-zantetsuBuildMode";
        public static bool AllowsBunny { get; private set; }

        public static DemoBuildMode Parse(string[] args)
        {
            var mode = DemoBuildMode.Auto;
            bool found = false;
            for(int i=0; i<args.Length; i++)
            {
                if (!string.Equals(args[i], ModeArgument, StringComparison.OrdinalIgnoreCase)) continue;
                if (found || ++i == args.Length) throw new BuildFailedException("Specify one " + ModeArgument + " auto|demo|commercial.");
                found = true;
                switch(args[i].ToLowerInvariant())
                {
                    case "auto": mode = DemoBuildMode.Auto; break;
                    case "demo": mode = DemoBuildMode.Demo; break;
                    case "commercial": mode = DemoBuildMode.Commercial; break;
                    default: throw new BuildFailedException("Unknown build mode: " + args[i]);
                }
            }
            return mode;
        }

        public static bool SelectDemo(DemoBuildMode mode, bool hasPrivateData, bool allPrivateScenes)
        {
            if (mode == DemoBuildMode.Demo) return true;
            if (mode == DemoBuildMode.Auto && !hasPrivateData) return true;
            if (!allPrivateScenes) throw new BuildFailedException("Private scenes are missing/incomplete. Commercial mode never falls back to Bunny. Use demo explicitly, or prepare the private scenes.");
            return false;
        }

        public static bool HasPrivateData => new[] { "Assets/Licensed", "Assets/ThirdPartyPrivate" }
            .Any(p => Directory.Exists(p) && Directory.EnumerateFiles(p,"*",SearchOption.AllDirectories).Any(f=>!f.EndsWith(".meta",StringComparison.OrdinalIgnoreCase)));

        public static string[] SelectScenes(string[] privateScenes, out bool demo)
        {
            RestorePrivateInputs(); // Recover a previous interrupted demo build before deciding whether private exists.
            demo = SelectDemo(Parse(Environment.GetCommandLineArgs()), HasPrivateData, privateScenes.All(File.Exists));
            if (demo) return new[] { ScenarioPlayerBuild.EnsureBootScene(), BunnyDemoBuild.EnsureScene() };
            return new[] { ScenarioPlayerBuild.EnsureBootScene() }.Concat(privateScenes).ToArray();
        }

        public static IDisposable Scope(bool demo, string[] scenes)
        {
            if (AllowsBunny) throw new BuildFailedException("Nested demo builds are not supported.");
            CheckDependencies(scenes, demo);
            if (demo) SuspendPrivateInputs();
            AllowsBunny = demo;
            Debug.Log("BUILD CONTENT MODE: " + (demo ? "NONCOMMERCIAL STANFORD BUNNY DEMO" : "COMMERCIAL: Stanford Bunny excluded"));
            return new BuildScope(demo);
        }

        private sealed class BuildScope : IDisposable
        {
            readonly bool demo;
            public BuildScope(bool demo) { this.demo=demo; }
            public void Dispose() { try { if(demo) RestorePrivateInputs(); } finally {AllowsBunny=false;} }
        }

        // Resources and StreamingAssets are packaged even when no scene references them. Keep private raw/baked
        // assets outside Assets for this build, preserving files AND their .meta GUIDs, then put them back in finally.
        // This durable staging directory is outside Unity's disposable Temp directory. No caller-supplied paths.
        const string Staging = ".bunny-demo-content-staging";
        static readonly string[] PrivateInputs = {"Assets/Licensed","Assets/ThirdPartyPrivate","Assets/StreamingAssets/Quest3s"};

        static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath,".."));
        static string StagingRoot => Path.Combine(ProjectRoot,Staging);
        static string Source(int i)
        {
            string path=Path.GetFullPath(Path.Combine(ProjectRoot,PrivateInputs[i]));
            if(!path.StartsWith(Path.GetFullPath(Application.dataPath)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("Private input must stay within this project's Assets.");
            return path;
        }
        static string Backup(int i)
        {
            string path=Path.GetFullPath(Path.Combine(StagingRoot,i.ToString()));
            if(!path.StartsWith(StagingRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
                throw new BuildFailedException("Private backup must stay within this project's staging directory.");
            return path;
        }
        static void SuspendPrivateInputs()
        {
            RestorePrivateInputs();
            Directory.CreateDirectory(StagingRoot);
            try
            {
                for(int i=0;i<PrivateInputs.Length;i++)
                {
                    string input=Source(i);
                    if(Directory.Exists(input)) Directory.Move(input,Backup(i));
                    if(File.Exists(input+".meta")) File.Move(input+".meta",Backup(i)+".meta");
                }
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            }
            catch { RestorePrivateInputs(); throw; }
        }

        public static void RestorePrivateInputs()
        {
            if (!Directory.Exists(StagingRoot)) return;
            for(int i=0;i<PrivateInputs.Length;i++)
            {
                string input=Source(i);
                if(Directory.Exists(Backup(i)))
                {
                    if(Directory.Exists(input)) throw new BuildFailedException("Private restore destination already exists. Preserve both copies and restore manually: " + Backup(i) + " -> " + input);
                    Directory.CreateDirectory(Path.GetDirectoryName(input));
                    Directory.Move(Backup(i),input);
                }
                if(File.Exists(Backup(i)+".meta"))
                {
                    if(File.Exists(input+".meta")) throw new BuildFailedException("Private metadata restore destination already exists: " + input+".meta");
                    File.Move(Backup(i)+".meta",input+".meta");
                }
            }
            // Delete only an empty staging directory, never any saved private contents.
            Directory.Delete(StagingRoot,false);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        }

        public static bool IsBunnyAsset(string path) => path.Replace('\\','/').StartsWith("Assets/NonCommercialDemo/",StringComparison.OrdinalIgnoreCase);

        public static void CheckDependencies(IEnumerable<string> assets, bool demo)
        {
            if (demo) return;
            foreach(string asset in assets)
                foreach(string dependency in AssetDatabase.GetDependencies(asset,true))
                    if (IsBunnyAsset(dependency)) throw new BuildFailedException("Commercial build references noncommercial data: " + asset + " -> " + dependency);
        }

        public static void CheckAutomaticAssets(bool demo)
        {
            var automatic = AssetDatabase.GetAllAssetPaths().Where(p=>
                p.Contains("/Resources/") || p.StartsWith("Assets/StreamingAssets/",StringComparison.OrdinalIgnoreCase));
            CheckDependencies(automatic, demo);
            CheckDependencies(PlayerSettings.GetPreloadedAssets().Where(a=>a!=null).Select(AssetDatabase.GetAssetPath),demo);
            // Demo data is never allowed in automatic packaging locations, even during a demo build.
            if (automatic.Any(IsBunnyAsset)) throw new BuildFailedException("Move Stanford data out of Resources/StreamingAssets.");
        }
    }

    public sealed class NoncommercialBuildGuard : IPreprocessBuildWithReport, IProcessSceneWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => -10000;
        private static string AuditPath(BuildReport report)
        {
            string output=report.summary.outputPath;
            return output.EndsWith(".apk",StringComparison.OrdinalIgnoreCase) || output.EndsWith(".aab",StringComparison.OrdinalIgnoreCase)
                ? output+".content-audit.json" : Path.Combine(Path.GetDirectoryName(output),"zantetsu-content-audit.json");
        }
        public void OnPreprocessBuild(BuildReport report)
        {
            DemoBuildPolicy.CheckAutomaticAssets(DemoBuildPolicy.AllowsBunny);
            string audit=AuditPath(report);
            if (!DemoBuildPolicy.AllowsBunny && File.Exists(audit) && File.ReadAllText(audit).Contains("\"noncommercialBunnyDemo\":true"))
                throw new BuildFailedException("This output contains a previous Bunny demo. Choose a fresh commercial output path to avoid stale demo content: " + audit);
        }
        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null) return; // Editor PlayMode scene processing is not a distributable build.
            DemoBuildPolicy.CheckDependencies(new[] { scene.path }, DemoBuildPolicy.AllowsBunny);
            if (!DemoBuildPolicy.AllowsBunny && scene.GetRootGameObjects().Any(g=>g.GetComponentInChildren<BunnyDemo>(true)!=null))
                throw new BuildFailedException("BunnyDemo component is forbidden in commercial builds.");
        }
        public void OnPostprocessBuild(BuildReport report)
        {
            if (!DemoBuildPolicy.AllowsBunny)
            {
                foreach (var packed in report.packedAssets)
                    foreach (var asset in packed.contents)
                        if (DemoBuildPolicy.IsBunnyAsset(asset.sourceAssetPath))
                            throw new BuildFailedException("Commercial packed content audit found " + asset.sourceAssetPath);
            }
            string folder = Path.GetDirectoryName(report.summary.outputPath);
            string audit = AuditPath(report);
            File.WriteAllText(audit, "{\"noncommercialBunnyDemo\":" + (DemoBuildPolicy.AllowsBunny?"true":"false") + ",\"stanfordAssets\":[" +
                string.Join(",",report.packedAssets.SelectMany(p=>p.contents).Select(p=>p.sourceAssetPath).Where(DemoBuildPolicy.IsBunnyAsset).Distinct().Select(p=>"\""+p+"\"")) + "]}");
            if (DemoBuildPolicy.AllowsBunny)
                File.Copy(BunnyDemoBuild.Root+"/STANFORD-NOTICE.txt",audit+".STANFORD-NOTICE.txt",true);
        }
    }
}
