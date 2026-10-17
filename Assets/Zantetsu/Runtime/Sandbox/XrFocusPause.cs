using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;
using Zantetsu.Core;
using Zantetsu.Core.Statistics;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The XR session's input focus as a reason of the game's pause (DESIGN 4.6, D-241; Meta VRC.Quest.Input.4): while
    /// the session is not focused -- the system UI over the game -- the game is paused, the drawing and the head's
    /// tracking go on, and nothing of the game reads the controllers. The decision is <see cref="PlayFocus.PauseByFocus"/>:
    /// the OpenXR session's own focus, never the desktop window's; nothing before XR was first active; XR gone
    /// inactive after that is a pause, not a resumption by the window. Runs before the menu and every game input of
    /// the frame, so that the frame's readers see the pause at once; tells <see cref="GamePause"/> only on a change.
    /// Independent of the play statistics and their consent. Runs before GamePause's own listener (the frame's
    /// first refresh): a focus lost again in the frame after it came back cancels the waiting end instead of being
    /// a resumption and a new pause.
    /// </summary>
    [DefaultExecutionOrder(ExecutionOrder)]
    public sealed class XrFocusPause : MonoBehaviour
    {
        public const string Prefix = "XR FOCUS PAUSE: ";
        public const int ExecutionOrder = GamePause.ListenerExecutionOrder - 500;

        private bool _xrEverActive;
        private bool _lost;
        private bool _loggedStart;

        public static XrFocusPause Instance { get; private set; }

        /// <summary>Whether this listener holds the Focus reason now.</summary>
        public bool Lost => _lost;

        /// <summary>Makes the listener once; it lives across scene loads beside the play menu.</summary>
        public static XrFocusPause Install()
        {
            if (Instance != null) return Instance;
            var host = new GameObject("Zantetsu XR focus pause");
            if (Application.isPlaying) DontDestroyOnLoad(host);
            Instance = host.AddComponent<XrFocusPause>();
            return Instance;
        }

        private void Update()
        {
            bool active = XRSettings.isDeviceActive;
            if (active) _xrEverActive = true;
            string loader = active ? XRSettings.loadedDeviceName ?? "" : "";
            bool openXr = active && loader.IndexOf("OpenXR", System.StringComparison.OrdinalIgnoreCase) >= 0;
            bool focused = openXr && OpenXRUtility.IsSessionFocused;
            Apply(PlayFocus.PauseByFocus(active, _xrEverActive, openXr, focused), active, openXr, focused);
        }

        /// <summary>One frame's decision applied: the reason told to the pause on a change only, with one log line each.</summary>
        internal void Apply(bool lost, bool xrActive, bool openXr, bool focused)
        {
            if (!_loggedStart)
            {
                _loggedStart = true;
                Debug.Log(Prefix + "listening; xrActive=" + xrActive + " openXr=" + openXr + " sessionFocused=" + focused + " (the desktop window's focus is not read)");
            }

            if (lost == _lost) return;
            _lost = lost;
            Debug.Log(Prefix + (lost ? "focus lost" : "focus back") + "; xrActive=" + xrActive + " openXr=" + openXr + " sessionFocused=" + focused + " (frame " + Time.frameCount + ")");
            GamePause.SetFocusLost(lost);
        }

        private void OnDestroy()
        {
            if (_lost)
            {
                // The listener's end is not the focus coming back; but a reason nobody can end is left to no one.
                GamePause.SetFocusLost(false);
                _lost = false;
            }

            if (Instance == this) Instance = null;
        }
    }
}
