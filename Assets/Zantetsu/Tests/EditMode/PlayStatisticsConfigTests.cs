using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Zantetsu.EditorTools.Quest;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The send configuration (unit 9-B): when it can send, that its description never holds the token, and the
    /// distribution build's check of it -- required (present, able to send, external) or none (absent).
    /// </summary>
    public sealed class PlayStatisticsConfigTests
    {
        private static PlayStatisticsConfig Config(string endpoint = "https://example.invalid/v1/telemetry", string token = "not-a-real-token-1234", bool send = true, string role = "external")
        {
            var config = ScriptableObject.CreateInstance<PlayStatisticsConfig>();
            config.endpointUrl = endpoint; config.ingestToken = token; config.sendEnabled = send; config.testerRole = role;
            return config;
        }

        [Test]
        public void CanSend_NeedsEndpointTokenAndSendingOn_AndDescribeHidesTheToken()
        {
            PlayStatisticsConfig config = Config();
            try
            {
                Assert.That(config.CanSend, Is.True);
                Assert.That(config.Describe(), Does.Contain("token present").And.Contain("role external").And.Not.Contain("not-a-real-token-1234"));
                config.ingestToken = "";
                Assert.That(config.CanSend, Is.False);
                Assert.That(config.Describe(), Does.Contain("token absent"));
                config.ingestToken = "x"; config.endpointUrl = "";
                Assert.That(config.CanSend, Is.False);
                config.endpointUrl = "https://example.invalid/v1/telemetry"; config.sendEnabled = false;
                Assert.That(config.CanSend, Is.False);
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        private static readonly string Expected = Zantetsu.Editor.Sandbox.PlayStatisticsConfigSetup.AssetPath;

        // What the build would find with one configuration at the decided path, loaded by the runtime's load.
        private static QuestDistributionBuild.StatisticsConfigFound At(PlayStatisticsConfig config, string path = null)
        {
            path = path ?? Expected;
            return new QuestDistributionBuild.StatisticsConfigFound { typedPaths = new[] { path }, namedPaths = new[] { path }, loaded = config, loadedPath = path };
        }

        [Test]
        public void TheDistributionCheck_RequiresTheOneExternalConfigToTheDecidedEndpoint_OrNoneAtAllWhenNoneWasAsked()
        {
            var required = new QuestDistributionBuild.Request { statistics = QuestDistributionBuild.StatisticsRequired };
            var none = new QuestDistributionBuild.Request { statistics = QuestDistributionBuild.StatisticsNone };
            PlayStatisticsConfig good = Config(endpoint: QuestDistributionBuild.StatisticsEndpoint), developer = Config(endpoint: QuestDistributionBuild.StatisticsEndpoint, role: "developer"),
                silent = Config(endpoint: QuestDistributionBuild.StatisticsEndpoint, send: false), elsewhere = Config(), plain = Config(endpoint: "http://zantetsuken-telemetry-api.hogehogeo.workers.dev/v1/telemetry");
            var nothing = new QuestDistributionBuild.StatisticsConfigFound();
            try
            {
                Assert.That(QuestDistributionBuild.CheckStatisticsConfig(required, At(good), out string state), Is.Null);
                Assert.That(state, Does.Contain("token present").And.Contain(Expected).And.Not.Contain("not-a-real-token-1234"));
                Assert.That(QuestDistributionBuild.CheckStatisticsConfig(required, nothing, out _), Does.Contain("absent").And.Contain(QuestDistributionBuild.StatisticsArgument));
                Assert.That(QuestDistributionBuild.CheckStatisticsConfig(required, At(developer), out _), Does.Contain("developer").And.Contain("external"));
                Assert.That(QuestDistributionBuild.CheckStatisticsConfig(required, At(silent), out _), Does.Contain("cannot send").And.Not.Contain("not-a-real-token-1234"));
                Assert.That(QuestDistributionBuild.CheckStatisticsConfig(required, At(elsewhere), out _), Does.Contain("not the decided endpoint"), "CanSend alone is not enough");
                Assert.That(QuestDistributionBuild.CheckStatisticsConfig(required, At(plain), out _), Does.Contain("not the decided endpoint"), "http is not the decided endpoint");

                // Another Resources folder, a second config, another asset by the name, a load that finds another: refused.
                const string other = "Assets/Somewhere/Resources/ZantetsuPlayStatisticsConfig.asset";
                Assert.That(QuestDistributionBuild.CheckStatisticsConfig(required, At(good, other), out _), Does.Contain("exactly one"));
                var two = At(good); two.typedPaths = new[] { Expected, "Assets/Other/StatsCopy.asset" };
                Assert.That(QuestDistributionBuild.CheckStatisticsConfig(required, two, out _), Does.Contain("exactly one"), "a second config of the type would be carried");
                var named = At(good); named.namedPaths = new[] { Expected, "Assets/Other/Resources/ZantetsuPlayStatisticsConfig.txt" };
                Assert.That(QuestDistributionBuild.CheckStatisticsConfig(required, named, out _), Does.Contain("another asset is named"));
                var notLoaded = At(good); notLoaded.loaded = null; notLoaded.loadedPath = null;
                Assert.That(QuestDistributionBuild.CheckStatisticsConfig(required, notLoaded, out _), Does.Contain("runtime's load"));

                Assert.That(QuestDistributionBuild.CheckStatisticsConfig(none, nothing, out string noneState), Is.Null);
                Assert.That(noneState, Does.Contain("none").And.Contain("null"));
                Assert.That(QuestDistributionBuild.CheckStatisticsConfig(none, At(good), out _), Does.Contain("none was asked").And.Contain("move it aside"), "a no-send build must carry no token");
                var stray = new QuestDistributionBuild.StatisticsConfigFound { typedPaths = new[] { "Assets/Other/StatsCopy.asset" } };
                Assert.That(QuestDistributionBuild.CheckStatisticsConfig(none, stray, out _), Does.Contain("none was asked"), "a config anywhere in the project, loaded or not");
            }
            finally
            {
                Object.DestroyImmediate(good); Object.DestroyImmediate(developer); Object.DestroyImmediate(silent); Object.DestroyImmediate(elsewhere); Object.DestroyImmediate(plain);
            }
        }

        [Test]
        public void TheDistributionCheck_ReadsTheProjectAsTheRuntimeLoadsIt_AConfigInAnotherResourcesFolderIsFoundAndRefused()
        {
            var required = new QuestDistributionBuild.Request { statistics = QuestDistributionBuild.StatisticsRequired };
            var none = new QuestDistributionBuild.Request { statistics = QuestDistributionBuild.StatisticsNone };
            // This tree holds no configuration (no tree keeps one; DESIGN 21.18): "none" passes on the project itself.
            Assume.That(AssetDatabase.LoadMainAssetAtPath(Expected), Is.Null, "a statistics config is in this tree: move it aside before the tests");
            Assert.That(QuestDistributionBuild.CheckStatisticsConfig(none, out string state), Is.Null, state);
            Assert.That(QuestDistributionBuild.CheckStatisticsConfig(required, out _), Does.Contain("absent"));

            // A configuration in another Resources folder (a fake token, this test's own, removed after): the runtime's
            // load finds it, and the check finds it too -- refused for "required" (not the decided path) and for "none".
            const string folder = "Assets/Zantetsu/Tests/EditMode/StatisticsCheckTemporary";
            string path = folder + "/Resources/" + PlayStatisticsConfig.ResourceName + ".asset";
            Assume.That(AssetDatabase.IsValidFolder(folder), Is.False, "left from an earlier run: " + folder);
            AssetDatabase.CreateFolder("Assets/Zantetsu/Tests/EditMode", "StatisticsCheckTemporary");
            AssetDatabase.CreateFolder(folder, "Resources");
            try
            {
                AssetDatabase.CreateAsset(Config(endpoint: QuestDistributionBuild.StatisticsEndpoint, token: "fake-token-of-this-test-only"), path);
                AssetDatabase.SaveAssets();
                Assert.That(PlayStatisticsConfig.Load(), Is.Not.Null, "the runtime's load finds a config in any Resources folder");
                QuestDistributionBuild.StatisticsConfigFound found = QuestDistributionBuild.StatisticsConfigFound.InThisProject();
                Assert.That(found.typedPaths, Is.EqualTo(new[] { path }));
                Assert.That(found.namedPaths, Is.EqualTo(new[] { path }));
                Assert.That(found.loadedPath, Is.EqualTo(path));
                Assert.That(QuestDistributionBuild.CheckStatisticsConfig(required, out string refused), Does.Contain("exactly one").And.Contain(path));
                Assert.That(refused, Does.Not.Contain("fake-token-of-this-test-only"));
                Assert.That(QuestDistributionBuild.CheckStatisticsConfig(none, out _), Does.Contain("none was asked").And.Contain(path));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
                AssetDatabase.SaveAssets();
            }

            Assert.That(AssetDatabase.IsValidFolder(folder), Is.False, "the test's own folder is gone");
            Assert.That(QuestDistributionBuild.CheckStatisticsConfig(none, out _), Is.Null);
        }

        [Test]
        public void TheStatisticsArgument_IsReadAsRequiredOrNone_AndRefusesAnythingElse()
        {
            string[] Args(params string[] more)
            {
                var a = new System.Collections.Generic.List<string> { "Unity.exe", QuestDistributionBuild.VersionNameArgument, "0.2.0", QuestDistributionBuild.VersionCodeArgument, "3",
                    QuestDistributionBuild.KeystoreArgument, "C:/keys/alpha.keystore", QuestDistributionBuild.KeyAliasArgument, "alpha", QuestDistributionBuild.OutputArgument, "C:/out/alpha.apk",
                    QuestDistributionBuild.DevicesArgument, "quest3|quest3s" };
                a.AddRange(more);
                return a.ToArray();
            }

            Assert.That(QuestDistributionBuild.TryRead(Args(), out var r), Is.Null);
            Assert.That(r.statistics, Is.EqualTo(QuestDistributionBuild.StatisticsRequired), "absent means required");
            Assert.That(QuestDistributionBuild.TryRead(Args(QuestDistributionBuild.StatisticsArgument, "none"), out r), Is.Null);
            Assert.That(r.statistics, Is.EqualTo(QuestDistributionBuild.StatisticsNone));
            Assert.That(QuestDistributionBuild.TryRead(Args(QuestDistributionBuild.StatisticsArgument, "maybe"), out _), Does.Contain("required or none"));
            Assert.That(QuestDistributionBuild.TryRead(Args(QuestDistributionBuild.StatisticsArgument), out _), Does.Contain("missing its value"));
        }
    }
}
