using UnityEngine;
using Zantetsu.Core.Statistics;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// Shows <see cref="PlayLoadingView"/> through play's start-up (2026-10-18, TL): from the scene's first frame until
    /// the cut world is ready and every start-up preparation has ended (<see cref="PlayPreparation.Pending"/>: the
    /// crowd's pool and plan, the placed cuttables, the Quest's drawing warm-up) -- the same moment the statistics take
    /// as play's beginning, the head's tracking apart. Once hidden it is gone: a preparation that begins again later is
    /// not a loading. The picture stands before the head, a little below the eyes, and follows the head's turning
    /// smoothly; it animates on the engine's unscaled clock, so it keeps going while the game is paused (the consent
    /// dialog). It takes no input and pauses nothing.
    /// </summary>
    public sealed class PlayLoadingIndicator : MonoBehaviour
    {
        public const string Prefix = "ZANTETSU LOADING: ";

        /// <summary>
        /// Where it stands before the head: metres along the head's horizontal facing, and metres below the eyes -- at
        /// the eyes' own height (TL on the Quest 3S, 2026-10-18: 0.45 m below was "down below", 0.1 m "nearer the eyes
        /// than before"; the ask was "before the eyes").
        /// </summary>
        public const float Distance = 1.5f;
        public const float Below = 0f;

        /// <summary>How fast it follows the head: the fraction of the way it closes a second (exponential).</summary>
        public const float FollowRate = 6f;

        private readonly PlayLoadingView _view = new PlayLoadingView();
        private CutWorldRoot _world;
        private Camera _camera;
        private bool _shown, _done;
        private int _shownFrame = -1;
        private double _shownSeconds;
        private Vector3 _position;
        private Quaternion _rotation;

        public static PlayLoadingIndicator Instance { get; private set; }

        public PlayLoadingView View => _view;
        public bool IsShown => _shown;
        public bool IsDone => _done;

        /// <summary>The player's camera: the main camera unless one is given.</summary>
        public Camera Camera { get; set; }

        /// <summary>The cut world whose readiness is waited for: the scene's, unless a test gives one or says there is none (null, with <see cref="WorldGiven"/>).</summary>
        public CutWorldRoot World { get => _world; set { _world = value; WorldGiven = true; } }
        public bool WorldGiven { get; private set; }

        /// <summary>Makes the indicator for play's start-up, once; it lives across scene loads as the menu does.</summary>
        public static PlayLoadingIndicator Install()
        {
            if (Instance != null) return Instance;
            var host = new GameObject("Zantetsu play loading");
            if (Application.isPlaying) DontDestroyOnLoad(host);
            Instance = host.AddComponent<PlayLoadingIndicator>();
            return Instance;
        }

        /// <summary>Whether the start-up is still going: no cut world yet, one not ready, or a preparation pending.</summary>
        public static bool Loading(bool worldFound, bool worldReady, int pendingPreparations) => !worldFound || !worldReady || pendingPreparations > 0;

        private void Update()
        {
            if (_done) return;
            if (!WorldGiven && _world == null) _world = FindAnyObjectByType<CutWorldRoot>();
            Camera camera = Camera != null ? Camera : _camera != null ? _camera : (_camera = UnityEngine.Camera.main);
            if (camera == null) return;
            Step(_world != null, _world != null && _world.IsReady, PlayPreparation.Pending, camera.transform.position, camera.transform.rotation,
                Time.unscaledDeltaTime, Time.unscaledTime);
        }

        /// <summary>One frame: what <c>Update</c> does, with what it reads given (tests).</summary>
        internal void Step(bool worldFound, bool worldReady, int pendingPreparations, Vector3 headPosition, Quaternion headRotation, float deltaSeconds, float seconds)
        {
            if (_done) return;
            if (!Loading(worldFound, worldReady, pendingPreparations))
            {
                _done = true;
                if (_shown)
                {
                    Debug.Log(Prefix + "hidden (frame " + Time.frameCount + "): the world is ready and the preparations ended, " + (Time.realtimeSinceStartupAsDouble - _shownSeconds).ToString("0.0")
                        + " s after it was shown at frame " + _shownFrame);
                }

                _view.Destroy();
                return;
            }

            Vector3 facing = headRotation * Vector3.forward;
            facing.y = 0f;
            facing = facing.sqrMagnitude < 1e-6f ? Vector3.forward : facing.normalized;
            Vector3 target = headPosition + (facing * Distance) - (Vector3.up * Below);
            Quaternion targetRotation = Quaternion.LookRotation(facing, Vector3.up);
            if (!_shown)
            {
                _view.Build();
                _shown = true;
                _shownFrame = Time.frameCount;
                _shownSeconds = Time.realtimeSinceStartupAsDouble;
                _position = target;
                _rotation = targetRotation;
                Debug.Log(Prefix + "shown (frame " + Time.frameCount + "): " + (!worldFound ? "no cut world yet" : !worldReady ? "the cut world is not ready" : pendingPreparations + " preparation(s) pending")
                    + "; drawn " + (_view.DrawnInFront ? "in front of everything" : "with uGUI's default material, depth tested"));
            }
            else
            {
                float t = 1f - Mathf.Exp(-FollowRate * Mathf.Max(0f, deltaSeconds));
                _position = Vector3.Lerp(_position, target, t);
                _rotation = Quaternion.Slerp(_rotation, targetRotation, t);
            }

            _view.Animate(seconds);
            _view.Place(_position, _rotation);
        }

        /// <summary>Where the picture stands now (tests).</summary>
        internal Vector3 Position => _position;

        private void OnDestroy()
        {
            _view.Destroy();
            if (Instance == this) Instance = null;
        }
    }
}
