using System;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.EditorTools.Quest;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The Quest build's preload switch (unit 8-C, 2026-10-17): the experiment turns the Vulkan plugin's preload off for
    /// one build and the importer is put back on afterwards whatever the build did -- a failure included -- so that the
    /// tree and every later build keep the preload. The real plugin importer of the tree is used; no build is run.
    /// </summary>
    public sealed class QuestStaticDemoBuildPreloadTests
    {
        private const string NativePath = "Assets/Zantetsu/Runtime/Rendering/Plugins/Android/arm64-v8a/libZantetsuVpVulkan.so";

        private static PluginImporter Native()
        {
            var native = AssetImporter.GetAtPath(NativePath) as PluginImporter;
            Assume.That(native, Is.Not.Null, "the tree's Vulkan plugin importer");
            return native;
        }

        [TearDown]
        public void TearDown()
        {
            // Whatever a test did, the tree keeps the preload.
            PluginImporter native = Native();
            if (!native.isPreloaded) { native.isPreloaded = true; native.SaveAndReimport(); }
        }

        [Test]
        public void TheTree_KeepsThePreload()
        {
            Assert.That(Native().isPreloaded, Is.True, "DESIGN 4.5.10: the plugin is preloaded so that its device intercept runs");
        }

        [Test]
        public void TheExperiment_TurnsThePreloadOffForTheBody_AndPutsItBack()
        {
            PluginImporter native = Native();
            bool seenOff = false;
            LogAssert.Expect(LogType.Log, new Regex("preload was put back \\(isPreloaded=True\\)"));
            QuestStaticDemoBuild.WithPreload(native, false, () => { seenOff = !native.isPreloaded; });
            Assert.That(seenOff, Is.True, "the body ran with the preload off");
            Assert.That(Native().isPreloaded, Is.True, "put back after the body");
        }

        [Test]
        public void TheExperiment_PutsThePreloadBack_WhenTheBodyFails()
        {
            PluginImporter native = Native();
            LogAssert.Expect(LogType.Log, new Regex("preload was put back \\(isPreloaded=True\\)"));
            var thrown = Assert.Throws<InvalidOperationException>(() => QuestStaticDemoBuild.WithPreload(native, false, () => throw new InvalidOperationException("the build failed")));
            Assert.That(thrown.Message, Is.EqualTo("the build failed"), "the failure itself is kept");
            Assert.That(Native().isPreloaded, Is.True, "put back although the body failed");
        }

        [Test]
        public void TheNormalBuild_KeepsThePreloadOn_AndPutsNothingBack()
        {
            PluginImporter native = Native();
            bool seenOn = false;
            QuestStaticDemoBuild.WithPreload(native, true, () => { seenOn = native.isPreloaded; });
            Assert.That(seenOn, Is.True);
            Assert.That(Native().isPreloaded, Is.True);
            LogAssert.NoUnexpectedReceived();
        }
    }
}
