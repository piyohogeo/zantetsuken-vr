using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEditor;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// Makes the katana's model for the game (<see cref="SandboxKatanaModel"/>) from the sword exported from
    /// <c>Assets/Zantetsu/Sandbox/Katana/Source~/aikuchi_original.blend</c>: the import settings of the model and
    /// its textures, three materials, and the prefab under Resources whose root has the katana root's frame -- +Z
    /// along the blade, -Y the edge, +X the side normal, the blade's base at the origin.
    /// <para>
    /// The frame is found from the meshes themselves, not from what the export's axes are believed to be: the long
    /// axis is the blade's, its sense is from the grip toward the blade, the blade's thin side is the edge, and the
    /// blade's base is where the blade's mesh begins. **The blade is curved, and the katana's blade axis is a
    /// straight line** -- the line a cut is made along: the blade's chord, from the middle of its base to its tip,
    /// is what is laid along +Z, so the blade stays within its own curvature of that line and the grip leans a few
    /// degrees from it. (Laid along the grip instead, the tip would stand several centimetres off the line.) Run
    /// again after the sword is exported again; the assets keep their identities.
    /// </para>
    /// </summary>
    public static class KatanaModelSetup
    {
        public const string Folder = "Assets/Zantetsu/Sandbox/Katana";
        public const string ModelPath = Folder + "/aikuchi_sword.fbx";
        public const string PrefabPath = "Assets/Zantetsu/Runtime/Sandbox/Resources/" + SandboxKatanaModel.ResourceName + ".prefab";

        private const string BladeBaseColour = Folder + "/aikuchi_blade_basecolor.png";
        private const string BladeNormal = Folder + "/aikuchi_blade_normal.png";
        private const string BladeMetallic = Folder + "/aikuchi_blade_metallic_smoothness.png";
        private const string WoodBaseColour = Folder + "/aikuchi_wood_basecolor.png";

        [MenuItem("Zantetsu/Make the katana model prefab")]
        public static void Build()
        {
            foreach (string needed in new[] { ModelPath, BladeBaseColour, BladeNormal, BladeMetallic, WoodBaseColour })
            {
                if (!File.Exists(needed))
                {
                    throw new FileNotFoundException("the katana model's source is not there: " + needed);
                }
            }

            ImportModel();
            ImportTexture(BladeBaseColour, TextureImporterType.Default, true);
            ImportTexture(WoodBaseColour, TextureImporterType.Default, true);
            ImportTexture(BladeNormal, TextureImporterType.NormalMap, false);
            ImportTexture(BladeMetallic, TextureImporterType.Default, false);

            // The materials' numbers are the .blend's: the blade's steel (metallic 0.98, its roughness as the map's
            // smoothness, the brushing's normal at 0.65), the habaki's silver, the grip's wood (roughness 0.63).
            Material blade = MaterialAt(Folder + "/AikuchiBlade.mat");
            blade.SetColor("_BaseColor", Color.white);
            blade.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(BladeBaseColour));
            blade.SetTexture("_MetallicGlossMap", AssetDatabase.LoadAssetAtPath<Texture2D>(BladeMetallic));
            blade.EnableKeyword("_METALLICSPECGLOSSMAP");
            blade.SetFloat("_Metallic", 0.98f);
            blade.SetFloat("_Smoothness", 1f);
            blade.SetFloat("_SmoothnessTextureChannel", 0f);
            blade.SetTexture("_BumpMap", AssetDatabase.LoadAssetAtPath<Texture2D>(BladeNormal));
            blade.SetFloat("_BumpScale", 0.65f);
            blade.EnableKeyword("_NORMALMAP");
            EditorUtility.SetDirty(blade);

            Material habaki = MaterialAt(Folder + "/AikuchiHabaki.mat");
            habaki.SetColor("_BaseColor", new Color(0.30f, 0.34f, 0.38f, 1f).gamma);
            habaki.SetFloat("_Metallic", 0.85f);
            habaki.SetFloat("_Smoothness", 1f - 0.32f);
            EditorUtility.SetDirty(habaki);

            Material wood = MaterialAt(Folder + "/AikuchiWood.mat");
            wood.SetColor("_BaseColor", Color.white);
            wood.SetTexture("_BaseMap", AssetDatabase.LoadAssetAtPath<Texture2D>(WoodBaseColour));
            wood.SetFloat("_Metallic", 0f);
            wood.SetFloat("_Smoothness", 1f - 0.63f);
            EditorUtility.SetDirty(wood);
            AssetDatabase.SaveAssets();

            GameObject source = AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            var root = new GameObject(SandboxKatanaModel.InstanceName);
            try
            {
                var sword = (GameObject)PrefabUtility.InstantiatePrefab(source);
                sword.name = "Aikuchi Sword";
                sword.transform.SetParent(root.transform, false);
                MeshFilter bladeMesh = null, gripMesh = null;
                int triangles = 0;
                foreach (MeshRenderer renderer in sword.GetComponentsInChildren<MeshRenderer>(true))
                {
                    MeshFilter filter = renderer.GetComponent<MeshFilter>();
                    string name = renderer.name;
                    bool isBlade = name.StartsWith("Blade", StringComparison.Ordinal), isGrip = name.StartsWith("Grip", StringComparison.Ordinal);
                    if (!isBlade && !isGrip && !name.StartsWith("Habaki", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("an object of the sword that is not its blade, grip or habaki: " + name);
                    }

                    renderer.sharedMaterials = new[] { isBlade ? blade : isGrip ? wood : habaki };
                    renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                    renderer.receiveShadows = true;
                    triangles += filter.sharedMesh.triangles.Length / 3;
                    if (isBlade) bladeMesh = filter;
                    if (isGrip) gripMesh = filter;
                }

                if (bladeMesh == null || gripMesh == null)
                {
                    throw new InvalidOperationException("the sword has no blade or no grip");
                }

                // The frame, from the meshes: every point in the sword's own space first.
                List<Vector3> bladePoints = Points(bladeMesh, sword.transform), gripPoints = Points(gripMesh, sword.transform);
                Bounds bladeBounds = BoundsOf(bladePoints), gripBounds = BoundsOf(gripPoints);
                int longAxis = Largest(bladeBounds.size), thinAxis = Smallest(bladeBounds.size), wideAxis = 3 - longAxis - thinAxis;
                Vector3 tip = Axis(longAxis) * Mathf.Sign(bladeBounds.center[longAxis] - gripBounds.center[longAxis]);

                // The edge is the blade's thin side: across the middle tenth of its length, the half of the width whose
                // points lie nearer the blade's middle plane.
                float middle = bladeBounds.center[longAxis], slab = 0.05f * bladeBounds.size[longAxis];
                var inSlab = bladePoints.FindAll(p => Mathf.Abs(p[longAxis] - middle) <= slab);
                if (inSlab.Count < 8)
                {
                    throw new InvalidOperationException("too few points of the blade about its middle to find its edge: " + inSlab.Count);
                }

                float widthMiddle = 0.5f * (Min(inSlab, wideAxis) + Max(inSlab, wideAxis));
                double low = 0, high = 0;
                int lowCount = 0, highCount = 0;
                foreach (Vector3 p in inSlab)
                {
                    float thickness = Mathf.Abs(p[thinAxis] - bladeBounds.center[thinAxis]);
                    if (p[wideAxis] < widthMiddle) { low += thickness; lowCount++; }
                    else { high += thickness; highCount++; }
                }

                low /= Math.Max(1, lowCount);
                high /= Math.Max(1, highCount);
                Vector3 edge = Axis(wideAxis) * (low < high ? -1f : 1f);

                // The blade's chord: from the middle of its base (the points within a millimetre of where the blade
                // begins) to its tip (the points within a millimetre of where it ends).
                float begins = float.PositiveInfinity, ends = float.NegativeInfinity;
                foreach (Vector3 p in bladePoints)
                {
                    float along = Vector3.Dot(p, tip);
                    begins = Mathf.Min(begins, along);
                    ends = Mathf.Max(ends, along);
                }

                Vector3 baseMiddle = Vector3.zero, tipPoint = Vector3.zero;
                int baseCount = 0, tipCount = 0;
                foreach (Vector3 p in bladePoints)
                {
                    float along = Vector3.Dot(p, tip);
                    if (along - begins <= 0.001f) { baseMiddle += p; baseCount++; }
                    if (ends - along <= 0.001f) { tipPoint += p; tipCount++; }
                }

                baseMiddle /= baseCount;
                tipPoint /= tipCount;
                Vector3 chord = (tipPoint - baseMiddle).normalized;
                float lean = Vector3.Angle(chord, tip);

                // The chord to +Z and the edge to -Y; then the middle of the blade's base to the origin.
                Quaternion toFrame = Quaternion.Inverse(Quaternion.LookRotation(chord, -edge));
                sword.transform.localRotation = toFrame;
                sword.transform.localPosition = -(toFrame * baseMiddle);

                Bounds placed = default, placedBlade = default;
                bool any = false, anyBlade = false;
                float tipHeight = 0f, tipZ = float.NegativeInfinity;
                foreach (MeshFilter filter in sword.GetComponentsInChildren<MeshFilter>(true))
                {
                    foreach (Vector3 p in Points(filter, root.transform))
                    {
                        if (!any) { placed = new Bounds(p, Vector3.zero); any = true; }
                        else placed.Encapsulate(p);
                        if (filter != bladeMesh) continue;
                        if (!anyBlade) { placedBlade = new Bounds(p, Vector3.zero); anyBlade = true; }
                        else placedBlade.Encapsulate(p);
                        if (p.z > tipZ) { tipZ = p.z; tipHeight = p.y; }
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(PrefabPath));
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool saved);
                if (!saved)
                {
                    throw new IOException("the katana model's prefab could not be saved at " + PrefabPath);
                }

                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "KATANA MODEL: {0} made from {1}: {2} triangles in 3 meshes; in the sword's own space the blade runs along {3}, its edge is toward {4} "
                    + "(mean half thickness about the blade's middle: {5:F5} m on the low side of the width, {6:F5} m on the high side), thickness along {7}; "
                    + "in the katana's frame the sword is from ({8:F4}, {9:F4}, {10:F4}) to ({11:F4}, {12:F4}, {13:F4}); the blade's chord is along +Z, {16:F2} degrees "
                    + "from the grip's own axis; the blade lies from y {17:F4} to y {18:F4} and from z {19:F4} to z {20:F4}; its tip is at z {14:F4}, y {15:F4}",
                    PrefabPath, ModelPath, triangles, tip, edge, low, high, Axis(thinAxis),
                    placed.min.x, placed.min.y, placed.min.z, placed.max.x, placed.max.y, placed.max.z, tipZ, tipHeight,
                    lean, placedBlade.min.y, placedBlade.max.y, placedBlade.min.z, placedBlade.max.z));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        /// <summary>For <c>-executeMethod</c>: makes the prefab and ends the Editor (0 made, 1 not).</summary>
        public static void BuildFromCommandLine()
        {
            bool made = false;
            try
            {
                Build();
                made = File.Exists(PrefabPath);
            }
            catch (Exception e)
            {
                Debug.LogException(e);
            }

            EditorApplication.Exit(made ? 0 : 1);
        }

        private static void ImportModel()
        {
            var importer = (ModelImporter)AssetImporter.GetAtPath(ModelPath);
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.importAnimation = false;
            importer.animationType = ModelImporterAnimationType.None;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importBlendShapes = false;
            importer.importVisibility = false;
            importer.isReadable = false;
            importer.meshCompression = ModelImporterMeshCompression.Off;
            importer.globalScale = 1f;
            importer.useFileScale = true;
            importer.bakeAxisConversion = false;
            importer.importNormals = ModelImporterNormals.Import;
            importer.importTangents = ModelImporterTangents.CalculateMikk;
            importer.weldVertices = true;
            importer.SaveAndReimport();
        }

        private static void ImportTexture(string path, TextureImporterType type, bool colour)
        {
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            importer.textureType = type;
            importer.sRGBTexture = colour;
            importer.alphaIsTransparency = false;
            importer.mipmapEnabled = true;
            importer.wrapMode = TextureWrapMode.Repeat;
            importer.filterMode = FilterMode.Trilinear;
            importer.anisoLevel = 4;
            importer.maxTextureSize = 1024;
            importer.isReadable = false;
            importer.SaveAndReimport();
        }

        private static Material MaterialAt(string path)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Shader lit = Shader.Find("Universal Render Pipeline/Lit");
                if (lit == null)
                {
                    throw new InvalidOperationException("the Universal Render Pipeline's Lit shader is not in this project");
                }

                material = new Material(lit);
                AssetDatabase.CreateAsset(material, path);
            }

            return material;
        }

        // A mesh's vertices in the space of a transform above it. The model is read through the Editor's own copy of
        // the mesh's vertices, so the import does not have to keep the mesh readable.
        private static List<Vector3> Points(MeshFilter filter, Transform space)
        {
            var points = new List<Vector3>();
            Matrix4x4 toSpace = space.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            foreach (Vector3 vertex in filter.sharedMesh.vertices)
            {
                points.Add(toSpace.MultiplyPoint3x4(vertex));
            }

            return points;
        }

        private static Bounds BoundsOf(List<Vector3> points)
        {
            var bounds = new Bounds(points[0], Vector3.zero);
            foreach (Vector3 p in points)
            {
                bounds.Encapsulate(p);
            }

            return bounds;
        }

        private static float Min(List<Vector3> points, int axis)
        {
            float value = float.PositiveInfinity;
            foreach (Vector3 p in points) value = Mathf.Min(value, p[axis]);
            return value;
        }

        private static float Max(List<Vector3> points, int axis)
        {
            float value = float.NegativeInfinity;
            foreach (Vector3 p in points) value = Mathf.Max(value, p[axis]);
            return value;
        }

        private static int Largest(Vector3 v) => v.x >= v.y && v.x >= v.z ? 0 : v.y >= v.z ? 1 : 2;

        private static int Smallest(Vector3 v) => v.x <= v.y && v.x <= v.z ? 0 : v.y <= v.z ? 1 : 2;

        private static Vector3 Axis(int index) => index == 0 ? Vector3.right : index == 1 ? Vector3.up : Vector3.forward;
    }
}
