using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Zantetsu.Quest;

namespace Zantetsu.EditorTools.Quest
{
    /// <summary>
    /// DIAGNOSIS ONLY (unit 22, 2026-10-17): the empty XR scene of the Quest Player -- the sandbox scene's XR rig (XR
    /// Origin / Camera Offset / Main Camera with its tracked pose driver) and its directional light, nothing else: no
    /// city, no world, no katana, no locomotion, no probes. Made from <c>Assets/Scenes/Sandbox.unity</c> as the bunny
    /// demo's scene is, with every other root and every other component on the rig removed, and saved to
    /// <see cref="ScenePath"/>. The scenario "empty-xr" starts in it (<see cref="QuestEmptyXrDiagnosis"/>).
    /// </summary>
    public static class QuestEmptyXrBuild
    {
        public const string SourceScenePath = "Assets/Scenes/Sandbox.unity";
        public const string ScenePath = "Assets/Scenes/" + QuestEmptyXrDiagnosis.SceneName + ".unity";

        [MenuItem("Zantetsu/Quest/Configure empty XR diagnosis scene")]
        public static void Configure()
        {
            var report = new List<string>();
            Configure(report);
            Debug.Log("QUEST EMPTY XR SCENE: " + string.Join("; ", report));
        }

        public static void Configure(List<string> report)
        {
            Scene scene = EditorSceneManager.OpenScene(SourceScenePath, OpenSceneMode.Single);
            string[] keep = { "XR Origin", "Directional Light" };
            var removedRoots = new List<string>();
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                if (keep.Contains(root.name)) continue;
                removedRoots.Add(root.name);
                UnityEngine.Object.DestroyImmediate(root);
            }

            GameObject origin = scene.GetRootGameObjects().FirstOrDefault(r => r.name == "XR Origin");
            if (origin == null) throw new InvalidOperationException("the sandbox scene has no 'XR Origin'");
            Camera camera = Camera.main;
            if (camera == null || !camera.transform.IsChildOf(origin.transform)) throw new InvalidOperationException("the sandbox scene's MainCamera is not under 'XR Origin'");
            Transform offset = camera.transform.parent;
            if (offset == null || offset.parent != origin.transform) throw new InvalidOperationException("the sandbox scene's camera is not 'XR Origin/Camera Offset/Main Camera'");

            // The rig alone: no other child (controllers, hands, locomotion), no other component (locomotion, katana...).
            var removedObjects = new List<string>();
            foreach (Transform child in origin.transform.Cast<Transform>().ToArray())
                if (child != offset) { removedObjects.Add(child.name); UnityEngine.Object.DestroyImmediate(child.gameObject); }
            foreach (Transform child in offset.Cast<Transform>().ToArray())
                if (child != camera.transform) { removedObjects.Add(child.name); UnityEngine.Object.DestroyImmediate(child.gameObject); }
            foreach (Transform child in camera.transform.Cast<Transform>().ToArray())
            { removedObjects.Add(child.name); UnityEngine.Object.DestroyImmediate(child.gameObject); }
            var removedComponents = new List<string>();
            foreach (GameObject on in new[] { origin, offset.gameObject, camera.gameObject })
            {
                foreach (Component component in on.GetComponents<Component>().ToArray())
                {
                    // Kept by type name: the rig's own components (no reference to the input or core-utils assemblies is needed here).
                    string type = component.GetType().FullName;
                    if (component is Transform || component is Camera || component is AudioListener
                        || component is UnityEngine.Rendering.Universal.UniversalAdditionalCameraData
                        || type == "UnityEngine.InputSystem.XR.TrackedPoseDriver" || type == "Unity.XR.CoreUtils.XROrigin")
                        continue;
                    removedComponents.Add(on.name + "/" + component.GetType().Name);
                    UnityEngine.Object.DestroyImmediate(component);
                }
            }

            // The camera as the static scene's build leaves it (QuestStaticDemoBuild.ConfigureScene).
            camera.allowHDR = false;
            camera.allowMSAA = true;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.035f, 0.06f, 0.1f);
            var cameraData = camera.GetUniversalAdditionalCameraData();
            cameraData.renderPostProcessing = false;
            cameraData.SetRenderer(0);

            // No scene content stays bound to the render settings either: no skybox material, no lightmaps, no probes.
            RenderSettings.skybox = null;
            RenderSettings.fog = false;
            Lightmapping.Clear();
            LightmapSettings.lightmaps = Array.Empty<LightmapData>();
            LightmapSettings.lightProbes = null;

            if (!EditorSceneManager.SaveScene(scene, ScenePath)) throw new InvalidOperationException("the empty XR scene could not be saved: " + ScenePath);
            report.Add("scene " + ScenePath + " from " + SourceScenePath);
            report.Add("kept roots: XR Origin (Camera Offset / Main Camera), Directional Light");
            report.Add("removed roots: " + string.Join(", ", removedRoots));
            report.Add("removed rig objects: " + string.Join(", ", removedObjects));
            report.Add("removed rig components: " + string.Join(", ", removedComponents));
            report.Add("camera: hdr off, msaa allowed, solid colour, no post-processing, renderer 0; skybox none, fog off, lightmaps cleared");
        }
    }
}
