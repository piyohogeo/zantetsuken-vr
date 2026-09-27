using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zantetsu.PhysicsCut;
using Zantetsu.Sandbox;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// Wires a Prop cut into Sandbox.unity once (Phase 4.51): a cut world with its drawing, one box without anchors
    /// standing on a floor collider, the katana's waves joined to that world's hit detector, and a camera of the
    /// Player check's own. The locomotion (U4) and the katana with its replay (U6) are the scene's as they are.
    /// <para>
    /// The box is placed where two waves of the saved excerpt the Player check replays pass -- an earlier one near its
    /// top, a later one lower down -- so that a first cut and a cut of a surviving child both happen from real hits.
    /// That is a choice of this scene for reproducing the path, not something the hit or the cut knows about.
    /// </para>
    /// </summary>
    public static class SandboxPropCutSetup
    {
        public const string WorldName = "Prop Cut World";
        public const string FloorName = "Prop Floor";
        public const string ViewName = "Prop Check View";
        private const string ProfilePath = "Assets/Zantetsu/Settings/CutWorldSandboxProfile.asset";
        private const string SideMaterialPath = "Assets/Zantetsu/Settings/CutWorldSandboxSide.mat";
        private const string EndMaterialPath = "Assets/Zantetsu/Settings/CutWorldSandboxEnd.mat";

        /// <summary>Where the box stands: its centre, resting on the floor, and half its size.</summary>
        public static readonly Vector3 BoxCentre = new Vector3(1.4f, 1.2f, 2.0f);

        public static readonly Vector3 BoxExtents = new Vector3(0.2f, 1.2f, 0.2f);

        [MenuItem("Zantetsu/Sandbox/Set Up Prop Cut")]
        public static void Apply()
        {
            Scene scene = EditorSceneManager.OpenScene(SandboxLocomotionSetup.ScenePath, OpenSceneMode.Single);
            foreach (GameObject existing in scene.GetRootGameObjects())
            {
                if (existing.name == WorldName)
                {
                    throw new InvalidOperationException(
                        SandboxLocomotionSetup.ScenePath + " already has " + WorldName + "; nothing changed.");
                }
            }

            var profile = AssetDatabase.LoadAssetAtPath<CutWorldProfile>(ProfilePath);
            var side = AssetDatabase.LoadAssetAtPath<Material>(SideMaterialPath);
            var end = AssetDatabase.LoadAssetAtPath<Material>(EndMaterialPath);
            if (profile == null || side == null || end == null)
            {
                throw new InvalidOperationException("The cut world sandbox profile or materials are missing.");
            }

            Camera hmd = Required(scene, "XR Origin").transform.Find("Camera Offset/Main Camera")?.GetComponent<Camera>();
            SandboxRightHandKatana katana = Required(scene, "Sandbox Katana Rig").GetComponent<SandboxRightHandKatana>();
            if (hmd == null || katana == null)
            {
                throw new InvalidOperationException("The XR camera or the sandbox katana was not found.");
            }

            // The floor the box and its pieces stand on: a collider the size of the displayed ground, on the default
            // layer, which the Player layer does not collide with.
            var floor = new GameObject(FloorName);
            floor.transform.position = new Vector3(0f, -0.05f, 0f);
            BoxCollider floorCollider = floor.AddComponent<BoxCollider>();
            floorCollider.size = new Vector3(6f, 0.1f, 6f);

            // The Player check's own view, off until the check turns it on with a target of its own.
            var viewObject = new GameObject(ViewName);
            viewObject.transform.SetPositionAndRotation(new Vector3(0.2f, 3.0f, -0.6f), Quaternion.Euler(26f, 25f, 0f));
            Camera view = viewObject.AddComponent<Camera>();
            view.fieldOfView = 60f;
            view.nearClipPlane = 0.05f;
            view.farClipPlane = 60f;
            view.stereoTargetEye = StereoTargetEyeMask.None;
            view.enabled = false;

            var worldObject = new GameObject(WorldName);
            CutWorldRoot root = worldObject.AddComponent<CutWorldRoot>();
            var serialized = new SerializedObject(root);
            serialized.FindProperty("profile").objectReferenceValue = profile;
            SerializedProperty materials = serialized.FindProperty("materials");
            materials.arraySize = 2;
            SetBinding(materials.GetArrayElementAtIndex(0), 0, side);
            SetBinding(materials.GetArrayElementAtIndex(1), 1, end);

            // The display's two shadow casters (DESIGN 5.4): Stable one-sided, immediate two-sided without a cap.
            CutWorldShadowCasters.Assign(serialized);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            CutWorldCameraDrawing drawing = worldObject.AddComponent<CutWorldCameraDrawing>();
            var drawingSerialized = new SerializedObject(drawing);
            drawingSerialized.FindProperty("world").objectReferenceValue = root;
            SerializedProperty cameras = drawingSerialized.FindProperty("cameras");
            cameras.arraySize = 2;
            cameras.GetArrayElementAtIndex(0).objectReferenceValue = hmd;
            cameras.GetArrayElementAtIndex(1).objectReferenceValue = view;
            drawingSerialized.FindProperty("layer").intValue = 0;
            drawingSerialized.ApplyModifiedPropertiesWithoutUndo();

            // The box: no anchors, standing on the floor under gravity, and no key asks for a cut of it.
            SandboxCutWorldProbe probe = worldObject.AddComponent<SandboxCutWorldProbe>();
            var probeSerialized = new SerializedObject(probe);
            probeSerialized.FindProperty("world").objectReferenceValue = root;
            probeSerialized.FindProperty("bodyPosition").vector3Value = BoxCentre;
            probeSerialized.FindProperty("bodyExtents").vector3Value = BoxExtents;
            probeSerialized.FindProperty("bodyUsesGravity").boolValue = true;
            probeSerialized.FindProperty("bodyHasBottomAnchors").boolValue = false;
            probeSerialized.FindProperty("cutKeys").boolValue = false;
            probeSerialized.ApplyModifiedPropertiesWithoutUndo();

            SandboxSlashPropHit hit = worldObject.AddComponent<SandboxSlashPropHit>();
            var hitSerialized = new SerializedObject(hit);
            hitSerialized.FindProperty("world").objectReferenceValue = root;
            hitSerialized.FindProperty("katana").objectReferenceValue = katana;
            hitSerialized.ApplyModifiedPropertiesWithoutUndo();

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                throw new InvalidOperationException("Could not save " + SandboxLocomotionSetup.ScenePath + ".");
            }

            Debug.Log("SandboxPropCutSetup: wired " + SandboxLocomotionSetup.ScenePath + " (box at " + BoxCentre + ").");
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

        private static void SetBinding(SerializedProperty element, int sourceIndex, Material material)
        {
            element.FindPropertyRelative("sourceIndex").intValue = sourceIndex;
            element.FindPropertyRelative("material").objectReferenceValue = material;
        }

        private static GameObject Required(Scene scene, string name)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (root.name == name)
                {
                    return root;
                }
            }

            throw new InvalidOperationException(SandboxLocomotionSetup.ScenePath + " has no root object named " + name + ".");
        }
    }
}
