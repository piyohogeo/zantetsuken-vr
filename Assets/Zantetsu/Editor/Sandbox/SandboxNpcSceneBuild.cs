using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zantetsu.Core.Animation;
using Zantetsu.PhysicsCut;
using Zantetsu.Sandbox;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// Makes the private NPC variant of Sandbox.unity (Phase 4.52): the product sandbox scene as it is -- the locomotion,
    /// the katana and its replay, the Prop cut world with its box -- plus one licensed Casual character walking by its
    /// Pose Table and prepared for a current-pose cut, saved under <c>Assets/Licensed</c>, which never enters the public
    /// repository. Sandbox.unity itself is opened and not saved.
    /// <para>
    /// The world draws the character with the licensed palette material on source index 0 (the index the character's
    /// display input uses) and the box's two materials move to 1 and 2; the world holds the licensed palette atlas pair.
    /// The character and the box stand where two replayed waves of the saved run cross each: a choice of this scene for
    /// reproducing the path, not something the hit or the cut knows about.
    /// </para>
    /// </summary>
    public static class SandboxNpcSceneBuild
    {
        public const string ScenePath = "Assets/Licensed/U8Npc/SandboxNpc.unity";
        private const string RepairRoot = "Assets/Licensed/Compact16uvConvexRepair/";
        private const string IntakeRoot = "Assets/Licensed/Compact16uvIntake/";
        private const string TablePath = "Assets/Licensed/U8Npc/AS_Fast_WalkCycle_1.bytes";

        /// <summary>The uncut character's material: the product mesh surface, made by <see cref="CharacterSurface"/>.</summary>
        public const string CharacterSurfacePath = "Assets/Licensed/U8Npc/CharacterMeshSurface.mat";

        public static readonly Vector3 NpcPosition = new Vector3(0.8f, 0f, 2.4f);
        public static readonly Vector3 NpcEuler = new Vector3(0f, 180f, 0f);
        public static readonly Vector3 BoxCentre = new Vector3(-0.2f, 1.2f, 2.6f);

        [MenuItem("Zantetsu/Sandbox/Build Private NPC Scene")]
        public static void Build()
        {
            var intake = AssetDatabase.LoadAssetAtPath<TextAsset>(RepairRoot + "intake.json");
            var hulls = AssetDatabase.LoadAssetAtPath<TextAsset>(RepairRoot + "Resources/CharacterPhysicsMigration/character-casual.json");
            var table = AssetDatabase.LoadAssetAtPath<TextAsset>(TablePath);
            var forward = AssetDatabase.LoadAssetAtPath<Material>(RepairRoot + "SceneIntegration/CharacterForward.mat");
            var normal = AssetDatabase.LoadAssetAtPath<Texture2D>(IntakeRoot + "Resources/PaletteAtlas/Normal.png");
            var debug = AssetDatabase.LoadAssetAtPath<Texture2D>(IntakeRoot + "Resources/PaletteAtlas/Debug.png");
            Material skinMaterial = normal != null ? CharacterSurface(normal) : null;
            if (intake == null || hulls == null || table == null || forward == null || skinMaterial == null || normal == null || debug == null)
            {
                throw new InvalidOperationException("Prepare the private Compact16uv intake, repair, atlas and pose table first.");
            }

            var entry = JsonUtility.FromJson<Intake>(intake.text).assets.Single(e => e.family == "character-casual");
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(entry.assetPath);
            if (model == null)
            {
                throw new InvalidOperationException("The Casual model did not import: " + entry.assetPath);
            }

            Scene scene = EditorSceneManager.OpenScene(SandboxLocomotionSetup.ScenePath, OpenSceneMode.Single);
            GameObject worldObject = Required(scene, SandboxPropCutSetup.WorldName);
            CutWorldRoot world = worldObject.GetComponent<CutWorldRoot>();
            SandboxSlashPropHit hit = worldObject.GetComponent<SandboxSlashPropHit>();
            SandboxCutWorldProbe probe = worldObject.GetComponent<SandboxCutWorldProbe>();

            // The world: the palette atlas pair, the character's material on 0, the box's two on 1 and 2.
            var serialized = new SerializedObject(world);
            serialized.FindProperty("normalPaletteAtlas").objectReferenceValue = normal;
            serialized.FindProperty("debugPaletteAtlas").objectReferenceValue = debug;
            SerializedProperty materials = serialized.FindProperty("materials");
            var side = (Material)materials.GetArrayElementAtIndex(0).FindPropertyRelative("material").objectReferenceValue;
            var end = (Material)materials.GetArrayElementAtIndex(1).FindPropertyRelative("material").objectReferenceValue;
            materials.arraySize = 3;
            SetBinding(materials.GetArrayElementAtIndex(0), 0, forward);
            SetBinding(materials.GetArrayElementAtIndex(1), 1, side);
            SetBinding(materials.GetArrayElementAtIndex(2), 2, end);

            // The display's two shadow casters (DESIGN 5.4): Stable one-sided, immediate two-sided without a cap.
            CutWorldShadowCasters.Assign(serialized);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var probeSerialized = new SerializedObject(probe);
            probeSerialized.FindProperty("sideSourceIndex").intValue = 1;
            probeSerialized.FindProperty("endSourceIndex").intValue = 2;
            probeSerialized.FindProperty("bodyPosition").vector3Value = BoxCentre;
            probeSerialized.ApplyModifiedPropertiesWithoutUndo();

            // No separation impulse in this scene, for both the character and the box (a provisional E2E setting, not
            // a final value): the fixed 1.5 N·s of Sandbox.unity flung a 0.3 g sliver of the box at over 2000 m/s.
            var hitSerialized = new SerializedObject(hit);
            hitSerialized.FindProperty("separationImpulse").floatValue = 0f;
            hitSerialized.ApplyModifiedPropertiesWithoutUndo();

            // The character: its root (what a cut withdraws), the model, a motion body without a collider, the pose player.
            var root = new GameObject("NPC Casual");
            root.transform.SetPositionAndRotation(NpcPosition, Quaternion.Euler(NpcEuler));
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(model, root.transform);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            foreach (Animator animator in instance.GetComponentsInChildren<Animator>(true))
            {
                animator.enabled = false;
            }

            SkinnedMeshRenderer skin = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Single(s => s.sharedMesh != null && s.sharedMesh.name == entry.objectName);

            // Only the character's own skinned renderer draws it; the model's other renderers -- its authored collision
            // hulls among them -- are not what the character looks like, as the character scene probe also has it.
            foreach (Renderer other in instance.GetComponentsInChildren<Renderer>(true))
            {
                Debug.Log("SandboxNpcSceneBuild: renderer " + other.name + " (" + other.GetType().Name + ", was enabled "
                    + other.enabled + ")" + (other == skin ? " kept" : " disabled"));
                other.enabled = other == skin;
            }

            skin.sharedMaterials = Enumerable.Repeat(skinMaterial, skin.sharedMesh.subMeshCount).ToArray();
            skin.updateWhenOffscreen = true;

            var motion = new GameObject("NPC motion body");
            motion.transform.SetParent(root.transform, false);
            Rigidbody body = motion.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = 12f;
            body.centerOfMass = Vector3.up;
            body.inertiaTensor = Vector3.one * 4f;

            PoseTablePlayer player = root.AddComponent<PoseTablePlayer>();
            var playerSerialized = new SerializedObject(player);
            playerSerialized.FindProperty("table").objectReferenceValue = table;
            playerSerialized.FindProperty("modelRoot").objectReferenceValue = instance.transform;
            playerSerialized.FindProperty("requiresBones").boolValue = true;
            playerSerialized.ApplyModifiedPropertiesWithoutUndo();

            // The preparation lives outside the character's root, so a withdrawal never takes it away.
            var setup = new GameObject("NPC Casual Setup");
            SandboxNpcCharacter character = setup.AddComponent<SandboxNpcCharacter>();
            var characterSerialized = new SerializedObject(character);
            characterSerialized.FindProperty("world").objectReferenceValue = world;
            characterSerialized.FindProperty("hit").objectReferenceValue = hit;
            characterSerialized.FindProperty("characterRoot").objectReferenceValue = root;
            characterSerialized.FindProperty("motionBody").objectReferenceValue = body;
            characterSerialized.FindProperty("intake").objectReferenceValue = intake;
            characterSerialized.FindProperty("hulls").objectReferenceValue = hulls;
            characterSerialized.ApplyModifiedPropertiesWithoutUndo();

            // The check's view, turned to where both targets stand.
            GameObject view = Required(scene, SandboxPropCutSetup.ViewName);
            view.transform.SetPositionAndRotation(new Vector3(0.3f, 2.4f, -1.2f), Quaternion.LookRotation(new Vector3(0.3f, 1.1f, 2.5f) - new Vector3(0.3f, 2.4f, -1.2f)));

            Directory.CreateDirectory(Path.GetDirectoryName(ScenePath));
            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                throw new InvalidOperationException("Could not save " + ScenePath + ".");
            }

            AssetDatabase.SaveAssets();
            Debug.Log("SandboxNpcSceneBuild: saved " + ScenePath + " (NPC at " + NpcPosition + ", box at " + BoxCentre + ").");
        }

        /// <summary>
        /// The uncut character's material, made or brought up to date at <see cref="CharacterSurfacePath"/>: the product
        /// mesh surface with the shared palette atlas, white, and the normal atlas as its base map -- what the cut pieces'
        /// material (CharacterForward.mat) is set to -- so a character looks the same before and after its cut, and is
        /// drawn for both eyes under Single Pass Instanced. The test-only mesh oracle is not a scene material.
        /// </summary>
        public static Material CharacterSurface(Texture2D normalAtlas)
        {
            Shader shader = Shader.Find("Zantetsu/VP Mesh Surface");
            if (shader == null || !shader.isSupported)
            {
                throw new InvalidOperationException("The product mesh surface shader is not available.");
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(CharacterSurfacePath);
            if (material == null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CharacterSurfacePath));
                material = new Material(shader) { name = "CharacterMeshSurface" };
                AssetDatabase.CreateAsset(material, CharacterSurfacePath);
            }

            material.shader = shader;
            material.SetFloat("_VpUsePaletteAtlas", 1f);
            material.SetColor("_BaseColor", Color.white);
            material.SetTexture("_BaseMap", normalAtlas);
            EditorUtility.SetDirty(material);
            AssetDatabase.SaveAssets();
            return material;
        }

        /// <summary>The entry for <c>-executeMethod</c>: builds the scene only.</summary>
        public static void BuildFromCommandLine()
        {
            try
            {
                Build();
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorApplication.Exit(1);
            }
        }

        /// <summary>
        /// The entry for <c>-executeMethod</c> that builds the private scene with the sandbox Player build, into the
        /// directory after <c>-zantetsuPlayerOut</c>.
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

        private static void SetBinding(SerializedProperty element, int sourceIndex, Material material)
        {
            element.FindPropertyRelative("sourceIndex").intValue = sourceIndex;
            element.FindPropertyRelative("material").objectReferenceValue = material;
        }

        private static GameObject Required(Scene scene, string name)
        {
            foreach (GameObject rootObject in scene.GetRootGameObjects())
            {
                if (rootObject.name == name)
                {
                    return rootObject;
                }
            }

            throw new InvalidOperationException(scene.path + " has no root object named " + name + ".");
        }

        [Serializable] private sealed class Intake { public Entry[] assets; }

        [Serializable] private sealed class Entry { public string family, assetPath, objectName; }
    }
}
