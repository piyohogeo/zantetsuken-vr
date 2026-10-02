using UnityEngine;

namespace Zantetsu.Core
{
    /// <summary>
    /// The Player keeps running, its input included, when its desktop window is not in focus (TL 2026-10-02). On Link the
    /// headset is worn while the window on the PC may be behind another one; with the project's runInBackground off, the
    /// Input System's HMD values -- the head pose the camera's TrackedPoseDriver reads -- stopped while the window was out
    /// of focus and came back with it (lv113411), while the legacy XR head went on. Set at every Player start, check or no
    /// check; the Editor's own setting is not touched. Nothing else (background behaviour, tracking state, actions, the
    /// camera's driver) is changed here.
    /// </summary>
    public static class PlayerRunInBackground
    {
        /// <summary>Whether a start in this host sets it: a Player only, never the Editor.</summary>
        public static bool AppliesTo(bool isEditor) => !isEditor;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Apply()
        {
            if (AppliesTo(Application.isEditor))
            {
                Application.runInBackground = true;
            }
        }
    }
}
