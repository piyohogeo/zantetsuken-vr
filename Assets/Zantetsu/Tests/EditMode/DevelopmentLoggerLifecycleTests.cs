#if DEBUG
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core;

namespace Zantetsu.Core.Tests
{
    public sealed class DevelopmentLoggerLifecycleTests
    {
        [UnityTest]
        public IEnumerator PlayRestartWithoutDomainOrSceneReloadOpensNewFileAndStopsWriter()
        {
            bool previousEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            EnterPlayModeOptions previousOptions = EditorSettings.enterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload | EnterPlayModeOptions.DisableSceneReload;
            try
            {
                yield return new EnterPlayMode();
                yield return null;
                string first = CurrentPath();
                DevelopmentLogger.Instance.write_log("lifecycle-test", "first", 1);
                AssertSamplerCount();
                yield return new ExitPlayMode();
                Assert.That(CurrentSession(), Is.Null);
                using (File.Open(first, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }

                yield return new EnterPlayMode();
                yield return null;
                string second = CurrentPath();
                Assert.That(second, Is.Not.EqualTo(first));
                AssertSamplerCount();
                DevelopmentLogger.Instance.write_log("lifecycle-test", "second", 2);
                yield return new ExitPlayMode();
                Assert.That(CurrentSession(), Is.Null);
                Assert.That(File.ReadAllText(first), Does.Contain("\"tag\":\"first\""));
                Assert.That(File.ReadAllText(first), Does.Not.Contain("\"tag\":\"second\""));
                Assert.That(File.ReadAllText(second), Does.Contain("\"tag\":\"second\""));
            }
            finally
            {
                EditorSettings.enterPlayModeOptionsEnabled = previousEnabled;
                EditorSettings.enterPlayModeOptions = previousOptions;
            }
        }

        private static object CurrentSession() => typeof(DevelopmentLogger)
            .GetField("session", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(DevelopmentLogger.Instance);

        private static string CurrentPath()
        {
            object session = CurrentSession();
            Assert.That(session, Is.Not.Null, "Play startup must open the development log with the repository commit.");
            return (string)session.GetType().GetField("FilePath", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(session);
        }

        private static void AssertSamplerCount() => Assert.That(Resources.FindObjectsOfTypeAll<MonoBehaviour>()
            .Count(component => component != null && component.GetType().Name == "DevelopmentLoggerLifecycle"), Is.EqualTo(1));
    }
}
#endif
