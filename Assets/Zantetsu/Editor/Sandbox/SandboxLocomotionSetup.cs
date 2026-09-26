using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zantetsu.Core;
using Zantetsu.Sandbox;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// Wires the player's artificial movement into Sandbox.unity once (Phase 4.2 / T-088): a few fixed walls, each a
    /// display cube without a collider and an explicitly authored occupancy volume of the same size; the level that
    /// settles them; the locomotion and its input adapter on the XR Origin; the katana's tracking space; and the
    /// Player layer on the XR Origin and the katana.
    /// </summary>
    public static class SandboxLocomotionSetup
    {
        public const string ScenePath = "Assets/Scenes/Sandbox.unity";
        public const string PlayerLayerName = "Player";
        private const string WallsName = "Locomotion Walls";

        // The walls: name, centre, full size (a box), or a radius and height (a capsule, size.x and size.y).
        private static readonly (string name, PlayerLocomotionOccupancyVolume.Shape shape, Vector3 center, Vector3 size)[] Walls =
        {
            ("Wall Left", PlayerLocomotionOccupancyVolume.Shape.Box, new Vector3(-2.2f, 1.25f, 0f), new Vector3(0.2f, 2.5f, 5f)),
            ("Wall Back", PlayerLocomotionOccupancyVolume.Shape.Box, new Vector3(0f, 1.25f, -2.2f), new Vector3(4.6f, 2.5f, 0.2f)),
            ("Post", PlayerLocomotionOccupancyVolume.Shape.Capsule, new Vector3(1.4f, 1.25f, -1.2f), new Vector3(0.2f, 2.5f, 0f)),
        };

        [MenuItem("Zantetsu/Sandbox/Set Up Player Locomotion")]
        public static void Apply()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (Find(scene, WallsName) != null)
            {
                throw new InvalidOperationException(ScenePath + " already has " + WallsName + "; nothing changed.");
            }

            int playerLayer = LayerMask.NameToLayer(PlayerLayerName);
            if (playerLayer < 0)
            {
                throw new InvalidOperationException("No layer named " + PlayerLayerName + ".");
            }

            GameObject origin = Required(scene, "XR Origin");
            Transform cameraOffset = origin.transform.Find("Camera Offset");
            Transform hmd = cameraOffset != null ? cameraOffset.Find("Main Camera") : null;
            if (hmd == null)
            {
                throw new InvalidOperationException("XR Origin/Camera Offset/Main Camera not found.");
            }

            GameObject katanaRig = Required(scene, "Sandbox Katana Rig");
            GameObject katana = Required(scene, "Katana");
            Material material = Required(scene, "Sandbox Environment").transform.Find("Pillar Front")
                .GetComponent<MeshRenderer>().sharedMaterial;

            var walls = new GameObject(WallsName);
            var volumes = new List<PlayerLocomotionOccupancyVolume>();
            foreach (var wall in Walls)
            {
                bool capsule = wall.shape == PlayerLocomotionOccupancyVolume.Shape.Capsule;
                GameObject shown = GameObject.CreatePrimitive(capsule ? PrimitiveType.Capsule : PrimitiveType.Cube);
                UnityEngine.Object.DestroyImmediate(shown.GetComponent<Collider>());
                shown.name = wall.name;
                shown.transform.SetParent(walls.transform, false);
                shown.transform.localPosition = wall.center;
                shown.transform.localScale = capsule
                    ? new Vector3(wall.size.x * 2f, wall.size.y * 0.5f, wall.size.x * 2f)
                    : wall.size;
                shown.GetComponent<MeshRenderer>().sharedMaterial = material;

                // The occupancy is its own object, so the size is the authored value and not the display's scale.
                var authored = new GameObject(wall.name + " Occupancy");
                authored.transform.SetParent(walls.transform, false);
                authored.transform.localPosition = wall.center;
                PlayerLocomotionOccupancyVolume volume = authored.AddComponent<PlayerLocomotionOccupancyVolume>();
                volume.Set(wall.shape, Vector3.zero, Vector3.zero, capsule ? Vector3.one : wall.size, wall.size.x, wall.size.y);
                volumes.Add(volume);
            }

            PlayerLocomotionLevel level = walls.AddComponent<PlayerLocomotionLevel>();
            level.SetVolumes(volumes);

            PlayerLocomotion locomotion = origin.AddComponent<PlayerLocomotion>();
            locomotion.Configure(
                level,
                hmd,
                new PlayerCapsuleAuthoring(new Vector3(0f, 0.9f, 0f), Vector3.zero, 0.25f, 1.6f),
                new PlayerCapsuleAuthoring(new Vector3(0f, -0.05f, -0.05f), Vector3.zero, 0.15f, 0.4f));
            SandboxLocomotionInput input = origin.AddComponent<SandboxLocomotionInput>();
            input.Configure(locomotion, hmd);

            var katanaFollower = new SerializedObject(katanaRig.GetComponent<SandboxRightHandKatana>());
            katanaFollower.FindProperty("trackingSpace").objectReferenceValue = cameraOffset;
            katanaFollower.ApplyModifiedPropertiesWithoutUndo();

            SetLayer(origin.transform, playerLayer);
            SetLayer(katanaRig.transform, playerLayer);
            SetLayer(katana.transform, playerLayer);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                throw new InvalidOperationException("Could not save " + ScenePath + ".");
            }

            Debug.Log("SandboxLocomotionSetup: wired " + ScenePath + " (" + volumes.Count + " volumes, layer " + playerLayer + ").");
        }

        /// <summary>The entry for <c>-executeMethod</c>.</summary>
        public static void ApplyFromCommandLine()
        {
            try
            {
                Apply();
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// The entry for <c>-executeMethod</c> that builds Sandbox.unity with the cut world sandbox's Player build,
        /// into the directory after <c>-zantetsuPlayerOut</c>.
        /// </summary>
        public static void BuildPlayerFromCommandLine()
        {
            string directory = null;
            string[] arguments = Environment.GetCommandLineArgs();
            for (int i = 0; i < arguments.Length - 1; i++)
            {
                if (string.Equals(arguments[i], "-zantetsuPlayerOut", StringComparison.OrdinalIgnoreCase))
                {
                    directory = arguments[i + 1];
                }
            }

            bool built = !string.IsNullOrEmpty(directory) && CutWorldSandboxPlayerBuild.Build(directory, ScenePath);
            EditorApplication.Exit(built ? 0 : 1);
        }

        private static void SetLayer(Transform root, int layer)
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                t.gameObject.layer = layer;
            }
        }

        private static GameObject Find(Scene scene, string name)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == name)
                {
                    return root;
                }
            }

            return null;
        }

        private static GameObject Required(Scene scene, string name)
        {
            GameObject found = Find(scene, name);
            if (found == null)
            {
                throw new InvalidOperationException(ScenePath + " has no root object named " + name + ".");
            }

            return found;
        }
    }
}
