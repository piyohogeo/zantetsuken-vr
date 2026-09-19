#if DEBUG
using System;
using System.IO;
using UnityEngine;

namespace Zantetsu.Core
{
    [DefaultExecutionOrder(-32000)]
    internal sealed class DevelopmentLoggerLifecycle : MonoBehaviour
    {
        internal const string CommitFileName = "development-logger-commit.txt";
        private static GameObject frameSampler;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void StartSession()
        {
            Application.quitting -= StopSession;
            Application.quitting += StopSession;
            // SubsystemRegistration runs on every Play entry, including without Domain Reload.
            StopSession();
            try
            {
#if UNITY_EDITOR
                string commit = DevelopmentLoggerBuild.ReadCommit();
#else
                string commit = File.ReadAllText(Path.Combine(Application.streamingAssetsPath, CommitFileName)).Trim();
#endif
                // Missing revision metadata is an initialization failure, not a made-up hash.
                if (!IsCommit(commit)) return;
                DevelopmentLogger.Instance.StartSession(@"C:\log\zantetsuken-vr\logger", commit);
            }
            catch (Exception) { /* Optional diagnostics must not prevent Play/Player startup. */ }
        }

        internal static bool IsCommit(string commit)
        {
            if (commit == null || (commit.Length != 40 && commit.Length != 64)) return false;
            foreach (char c in commit)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void InstallFrameSampler()
        {
            try
            {
                DevelopmentLogger.Instance.SetFrame(Time.frameCount);
                frameSampler = new GameObject("Development Logger") { hideFlags = HideFlags.HideAndDontSave };
                DontDestroyOnLoad(frameSampler);
                frameSampler.AddComponent<DevelopmentLoggerLifecycle>();
            }
            catch (Exception) { /* Frame sampling is optional too. */ }
        }

        private void Update() => DevelopmentLogger.Instance.SetFrame(Time.frameCount);
        private static void StopSession()
        {
            DevelopmentLogger.Instance.StopSession();
            // HideAndDontSave objects survive Play exit with Scene Reload disabled.
            // Dispose the sampler explicitly along with its session.
            try
            {
                if (frameSampler != null)
                {
#if UNITY_EDITOR
                    DestroyImmediate(frameSampler);
#else
                    Destroy(frameSampler);
#endif
                }
            }
            catch (Exception) { }
            frameSampler = null;
        }

#if UNITY_EDITOR
        [UnityEditor.InitializeOnLoadMethod]
        private static void RegisterEditorShutdown()
        {
            UnityEditor.EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            UnityEditor.EditorApplication.playModeStateChanged += OnPlayModeChanged;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= StopSession;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += StopSession;
            UnityEditor.EditorApplication.quitting -= StopSession;
            UnityEditor.EditorApplication.quitting += StopSession;
        }

        private static void OnPlayModeChanged(UnityEditor.PlayModeStateChange state)
        {
            if (state == UnityEditor.PlayModeStateChange.ExitingPlayMode) StopSession();
        }
#endif
    }
}
#endif
