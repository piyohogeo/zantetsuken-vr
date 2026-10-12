using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using Zantetsu.EditorTools.Sandbox;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    public sealed class BunnyBuildPolicyTests
    {
        [Test]
        public void MissingPrivateDataDefaultsToDemo_CommercialNeverFallsBack()
        {
            Assert.That(DemoBuildPolicy.SelectDemo(DemoBuildMode.Auto,false,false), Is.True);
            Assert.That(DemoBuildPolicy.SelectDemo(DemoBuildMode.Demo,true,true), Is.True);
            Assert.That(DemoBuildPolicy.SelectDemo(DemoBuildMode.Auto,true,true), Is.False);
            Assert.Throws<BuildFailedException>(()=>DemoBuildPolicy.SelectDemo(DemoBuildMode.Commercial,false,false));
            Assert.Throws<BuildFailedException>(()=>DemoBuildPolicy.SelectDemo(DemoBuildMode.Auto,true,false));
            Assert.That(DemoBuildPolicy.SelectDemo(DemoBuildMode.Commercial,true,true),Is.False);
        }

        [Test]
        public void CommercialRejectsIndirectDemoDependencies_AndScopeIsRestored()
        {
            Assert.Throws<BuildFailedException>(()=>DemoBuildPolicy.CheckDependencies(new[] {BunnyDemoBuild.ScenePath},false));
            using (DemoBuildPolicy.Scope(true,new[] {BunnyDemoBuild.ScenePath})) Assert.That(DemoBuildPolicy.AllowsBunny,Is.True);
            Assert.That(DemoBuildPolicy.AllowsBunny,Is.False);
            Assert.Throws<BuildFailedException>(()=>DemoBuildPolicy.Parse(new[]{DemoBuildPolicy.ModeArgument,"commercial",DemoBuildPolicy.ModeArgument,"demo"}));
        }

        [Test]
        public void ExportHasCorrectTopologyAndCompoundHullLimits()
        {
            var text = AssetDatabase.LoadAssetAtPath<UnityEngine.TextAsset>(BunnyDemoBuild.Root+"/StanfordBunny.json");
            var data = UnityEngine.JsonUtility.FromJson<PlacedCuttableInput>(text.text);
            Assert.That(data.topologyCount,Is.EqualTo(34834));
            Assert.That(data.indices.Length,Is.EqualTo(69664*3));
            Assert.That(data.hulls.Length,Is.EqualTo(32));
            foreach(var h in data.hulls) Assert.That(h.vertices.Length,Is.InRange(4,32));
            Assert.That(data.sha256,Is.EqualTo("b1acc63bece78444aa2e15bdcc72371a201279b98c6f5d4b74c993d02f0566fe"));
        }

        [Test]
        public void CommercialAlsoRejectsAutomaticResourceDependenciesOutsideDemoFolder()
        {
            const string folder="Assets/BunnyPolicyTest/Resources";
            const string prefab=folder+"/Indirect.prefab";
            Directory.CreateDirectory(folder); AssetDatabase.Refresh();
            var go=new UnityEngine.GameObject("indirect test");
            try
            {
                go.AddComponent<BunnyDemo>().input=AssetDatabase.LoadAssetAtPath<UnityEngine.TextAsset>(BunnyDemoBuild.Root+"/StanfordBunny.json");
                PrefabUtility.SaveAsPrefabAsset(go,prefab);
                Assert.Throws<BuildFailedException>(()=>DemoBuildPolicy.CheckAutomaticAssets(false));
            }
            finally { UnityEngine.Object.DestroyImmediate(go); AssetDatabase.DeleteAsset("Assets/BunnyPolicyTest"); }
        }

        [Test]
        public void DemoTemporarilyExcludesPrivateData_ThenRestoresContentsAndMetadata()
        {
            const string folder="Assets/ThirdPartyPrivate";
            const string path=folder+"/BunnyBuildFixture.txt";
            bool existed=Directory.Exists(folder);
            Directory.CreateDirectory(folder);
            try
            {
                File.WriteAllText(path,"private fixture"); AssetDatabase.Refresh();
                string metadata=File.ReadAllText(path+".meta");
                using(DemoBuildPolicy.Scope(true,new[]{BunnyDemoBuild.ScenePath}))
                    Assert.That(File.Exists(path),Is.False,"private Resources/data cannot enter a forced Bunny demo");
                Assert.That(File.ReadAllText(path),Is.EqualTo("private fixture"));
                Assert.That(File.ReadAllText(path+".meta"),Is.EqualTo(metadata));
            }
            finally {AssetDatabase.DeleteAsset(path); if(!existed) AssetDatabase.DeleteAsset(folder);}
        }
    }
}
