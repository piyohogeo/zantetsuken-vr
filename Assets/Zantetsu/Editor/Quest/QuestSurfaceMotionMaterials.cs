using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Zantetsu.EditorTools.Quest
{
    /// <summary>How a Quest copy of a VP Mesh Surface material writes the AppSW motion (its XRMotionVectors pass).</summary>
    public enum QuestSurfaceMotion
    {
        /// <summary>The camera's motion alone (VP_CAMERA_ONLY_ASW): an ordinary Unity Mesh.</summary>
        CameraOnly,

        /// <summary>
        /// URP's standard object motion (ObjectMotionVectors.hlsl): this and the previous frame's object matrix and
        /// skinned vertices. A SkinnedMeshRenderer (2026-10-19: the close NPCs' judder under AppSW).
        /// </summary>
        Object,
    }

    /// <summary>
    /// The Quest builds' copies of the VP Mesh Surface materials, one per source material AND motion (2026-10-19): a
    /// source shared by a Mesh and a SkinnedMeshRenderer gets two copies, so that the skin's object motion never
    /// reaches the Mesh. Only the motion keyword differs from the source; colour, shadow and depth passes are the same.
    /// </summary>
    public sealed class QuestSurfaceMotionMaterials
    {
        public const string SurfaceShaderName = "Zantetsu/VP Mesh Surface";
        public const string CameraOnlyKeyword = "VP_CAMERA_ONLY_ASW";

        private readonly string _root, _cameraPrefix, _objectPrefix;
        private readonly Dictionary<(Material, QuestSurfaceMotion), Material> _copies = new Dictionary<(Material, QuestSurfaceMotion), Material>();
        private int _cameraCount, _objectCount;

        public QuestSurfaceMotionMaterials(string root, string cameraPrefix, string objectPrefix)
        {
            _root = root; _cameraPrefix = cameraPrefix; _objectPrefix = objectPrefix;
        }

        public int CameraOnlyCount => _cameraCount;
        public int ObjectCount => _objectCount;

        /// <summary>The motion a renderer's VP Mesh Surface slots take: object motion for a skin, the camera's for anything else.</summary>
        public static QuestSurfaceMotion MotionOf(Renderer renderer) => renderer is SkinnedMeshRenderer ? QuestSurfaceMotion.Object : QuestSurfaceMotion.CameraOnly;

        /// <summary>The copy of <paramref name="source"/> for <paramref name="motion"/>, made (or reused) once per pair.</summary>
        public Material CopyOf(Material source, QuestSurfaceMotion motion)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (_copies.TryGetValue((source, motion), out Material copy)) return copy;
            string path = _root + "/" + (motion == QuestSurfaceMotion.Object ? _objectPrefix + _objectCount++ : _cameraPrefix + _cameraCount++) + ".mat";
            copy = string.IsNullOrEmpty(_root) ? null : AssetDatabase.LoadAssetAtPath<Material>(path);
            if (copy == null)
            {
                copy = new Material(source);
                if (!string.IsNullOrEmpty(_root)) AssetDatabase.CreateAsset(copy, path);
            }
            else
            {
                copy.shader = source.shader;
                copy.CopyPropertiesFromMaterial(source);
                copy.shaderKeywords = source.shaderKeywords;
            }

            Apply(copy, motion);
            if (!string.IsNullOrEmpty(_root)) EditorUtility.SetDirty(copy);
            _copies.Add((source, motion), copy);
            return copy;
        }

        /// <summary>The motion keyword of a copy: on for the camera's motion, off for the object's. Nothing else.</summary>
        public static void Apply(Material copy, QuestSurfaceMotion motion)
        {
            if (motion == QuestSurfaceMotion.CameraOnly) copy.EnableKeyword(CameraOnlyKeyword);
            else copy.DisableKeyword(CameraOnlyKeyword);
        }

        /// <summary>
        /// A skin's motion history for the AppSW object motion: Object motion and skinned motion vectors (the previous
        /// skinned vertices kept by Unity). What it had before is counted into <paramref name="before"/>.
        /// </summary>
        public static void EnableSkinHistory(SkinnedMeshRenderer skin, IDictionary<string, int> before)
        {
            string key = "mode=" + skin.motionVectorGenerationMode + "/skinnedMotionVectors=" + skin.skinnedMotionVectors;
            before[key] = before.TryGetValue(key, out int n) ? n + 1 : 1;
            skin.motionVectorGenerationMode = MotionVectorGenerationMode.Object;
            skin.skinnedMotionVectors = true;
        }

        /// <summary>
        /// Every NPC character of the open scene (inactive ones and the crowd's template included) settles its drawn
        /// skin's history each frame (<see cref="Zantetsu.Rendering.VpSkinMotionHistory"/>): no stale bone motion on its
        /// first frame back. How many were set.
        /// </summary>
        public static int EnableCharacterSkinHistory()
        {
            int set = 0;
            foreach (var npc in UnityEngine.Object.FindObjectsByType<Zantetsu.Sandbox.SandboxNpcCharacter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var options = new SerializedObject(npc);
                options.FindProperty("skinMotionHistory").boolValue = true;
                options.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(npc);
                set++;
            }

            return set;
        }

        /// <summary>The count text of a tally (sorted), for the build's report.</summary>
        public static string Describe(IDictionary<string, int> tally) =>
            tally.Count == 0 ? "none" : string.Join(", ", tally.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value + " " + p.Key));
    }
}
