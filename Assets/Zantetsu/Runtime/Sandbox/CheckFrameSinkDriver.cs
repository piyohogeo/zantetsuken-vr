using UnityEngine;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The sinks' frame: a hidden object kept across scenes while any sink lives, pumping them in its LateUpdate (after the
    /// game's own updates; a readback asked at a render is collected on a later frame). Made by the first sink, removed by
    /// the last one to settle; without capture it is never made.
    /// </summary>
    [DefaultExecutionOrder(32000)]
    public sealed class CheckFrameSinkDriver : MonoBehaviour
    {
        private static CheckFrameSinkDriver s_driver;

        public static bool Exists => s_driver != null;

        internal static void Ensure()
        {
            if (s_driver != null) return;
            var go = new GameObject("Check Frame Sink Driver") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            s_driver = go.AddComponent<CheckFrameSinkDriver>();
        }

        internal static void Remove()
        {
            if (s_driver == null) return;
            Destroy(s_driver.gameObject);
            s_driver = null;
        }

        private void LateUpdate() => CheckFrameSink.PumpAll();

        private void OnDestroy()
        {
            if (s_driver == this) s_driver = null;
        }
    }
}
