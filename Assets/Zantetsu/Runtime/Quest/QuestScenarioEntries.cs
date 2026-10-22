using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.Quest
{
    /// <summary>
    /// The Quest's entries of the launch's scenario (DESIGN 3.4), given to <see cref="SandboxScenario"/> before the
    /// first scene: on the device the scripted scenarios and play start here, one of them, from the one choice read
    /// from <c>quest3s-config.json</c>. Off the device nothing is given and the PC's entries run.
    /// </summary>
    internal static class QuestScenarioEntries
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (Zantetsu.Core.PlayerPlatform.IsAndroidFlat) return;
            SandboxScenario.PlatformEntry = Start;
            SandboxScenario.PlatformPlay = () => Start(SandboxScenarioKind.Play);
#endif
        }

        /// <summary>The scenario's demo, made once; a failure in words, or null.</summary>
        internal static string Start(SandboxScenarioKind kind)
        {
            if (kind == SandboxScenarioKind.StaticWait) return QuestStaticDemo.StartForScenario();
            if (kind == SandboxScenarioKind.EmptyXr) return QuestEmptyXrDiagnosis.StartForScenario();   // diagnosis only (unit 22)
            return QuestNpcSlashDemo.StartForScenario(kind);
        }
    }
}
