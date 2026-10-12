using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>Noncommercial demo scene, sharing the product's compound prop cut path.</summary>
    public sealed class BunnyDemo : MonoBehaviour
    {
        public const string SceneName = "StanfordBunnyDemo";
        public const int MinimumCount = 1, MaximumCount = 20;
        private static int s_count = 3;
        public CutWorldRoot world;
        public TextAsset input;
        public TextAsset licenseNotice;
        public GameObject worldTemplate;
        public SandboxRightHandKatana katana;
        private readonly List<PlacedCuttableRegistration> _bodies = new List<PlacedCuttableRegistration>();
        public int RequestedCount { get; set; }
        public int SpawnedCount => _bodies.Count;
        public bool Resetting { get; private set; }
        public IReadOnlyList<PlacedCuttableRegistration> Bodies => _bodies;
        private Camera _camera;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void NewSession() => s_count = 3;

        private void Start()
        {
            RequestedCount = s_count;
            _camera = Camera.main;
            Spawn();
        }

        private void Spawn()
        {
            if (world == null || !world.IsReady || input == null || licenseNotice == null || worldTemplate == null || katana == null)
                throw new InvalidOperationException("Bunny demo requires its world, data and Stanford license notice.");
            var data = JsonUtility.FromJson<PlacedCuttableInput>(input.text);
            if (data.schemaVersion != 1 || data.hulls.Length != 32 || data.isBuilding || data.indices.Length != 69664 * 3)
                throw new InvalidOperationException("Unexpected Bunny export.");
            var placement = new GameObject("Bunny placement");
            try
            {
                for (int i = 0; i < RequestedCount; i++)
                {
                    // A grid in front of the player. Each rabbit is 0.617 m tall, standing until cut.
                    placement.transform.position = new Vector3((i % 5 - (Mathf.Min(RequestedCount, 5)-1)*.5f)*.9f, .75f, 1.5f+(i/5)*1.0f);
                    _bodies.Add(PlacedCuttableRegistration.Register(world, data, placement.transform,
                        Array.Empty<Renderer>(), Array.Empty<Collider>(), 2f, false));
                }
            }
            finally { Destroy(placement); }
            SandboxScenario.KeepOneLocomotionInput();
            SandboxScenario.QuietDevelopmentTools();
            PlayMenu.Install(SandboxScenarioKind.Play);
            Debug.Log("STANFORD BUNNY NONCOMMERCIAL DEMO: " + SpawnedCount + " rabbits; data: Stanford University Computer Graphics Laboratory; https://graphics.stanford.edu/data/3Dscanrep/");
        }

        private void LateUpdate()
        {
            // With no tracked HMD, Floor tracking reports no eye height. Keep the keyboard preview usable.
            if (_camera != null && !UnityEngine.XR.XRSettings.isDeviceActive)
                _camera.transform.localPosition = new Vector3(0,1.6f,0);
        }

        public void ResetScene()
        {
            if (!Resetting) StartCoroutine(ResetAfterDrain());
        }

        private IEnumerator ResetAfterDrain()
        {
            Resetting = true;
            s_count = Mathf.Clamp(RequestedCount, MinimumCount, MaximumCount);
            // Leave the controller/menu callback before destroying its objects or loading another scene.
            yield return null;
            // Close the menu and clear its old camera/input references before the scene is replaced.
            var menu = PlayMenu.Instance;
            if (menu != null) { menu.Release(); Destroy(menu.gameObject); }
            Zantetsu.Core.GamePause.SetManual(true);
            foreach (var katana in FindObjectsByType<SandboxRightHandKatana>(FindObjectsSortMode.None))
            { katana.LiveInputEnabled = false; katana.ResetToKnownState(); }
            // Shutdown carries outstanding work across frames even while the game's progress is paused.
            Debug.Log("BUNNY RESET: ending previous cut world");
            while (!world.Shutdown())
            {
                if (world.TerminationRequested) yield break;
                yield return null;
            }
            DisposeBodies();
            var oldWorld = world.gameObject;
            // Disable its camera callbacks before the new world registers the same camera.
            oldWorld.SetActive(false);
            Destroy(oldWorld);
            yield return null;
            world = Instantiate(worldTemplate).GetComponent<CutWorldRoot>();
            world.name = "Bunny cut world";
            world.gameObject.AddComponent<CutWorldCameraDrawing>().Configure(world, _camera);
            world.gameObject.AddComponent<SandboxSlashPropHit>().Configure(world, katana, .1f);
            RequestedCount = s_count;
            Spawn();
            if (katana != null) katana.LiveInputEnabled = true;
            PlayMenu.Instance.HoldLocomotionUntilRelease();
            Zantetsu.Core.GamePause.SetManual(false);
            Resetting = false;
            Debug.Log("BUNNY RESET: world replaced with " + SpawnedCount + " rabbits");
        }

        private void DisposeBodies()
        {
            foreach (var body in _bodies) body.Dispose();
            _bodies.Clear();
        }

        private void OnDestroy()
        {
            // Authored banks are copied into shapes, but external collider meshes must outlive all workers/pieces.
            if (world != null && !world.IsReleased) world.Shutdown();
            if (world == null || world.IsReleased) DisposeBodies();
        }
    }
}
