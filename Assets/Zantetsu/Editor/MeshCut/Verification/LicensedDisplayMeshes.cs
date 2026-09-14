using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.MeshCut.Verification
{
    /// <summary>One licensed RenderCut fixture shown in the sandbox's representative display.</summary>
    public sealed class LicensedDisplayMesh
    {
        public LicensedDisplayMesh(string category, string artifactId)
        {
            Category = category;
            ArtifactId = artifactId;
        }

        public string Category { get; }
        public string ArtifactId { get; }
    }

    /// <summary>
    /// Editor-only builder of the Unity Meshes the sandbox shows for licensed Phase 0.2
    /// RenderCut fixtures. It reads the optional sibling private repository and saves each
    /// mesh under Assets/Licensed/, which git ignores: only this code is public, and the
    /// geometry never enters the public repository. Every mesh keeps a GUID fixed by its
    /// fixture, so scene references survive regeneration and resolve to nothing where the
    /// private repository has not been used.
    /// </summary>
    public static class LicensedDisplayMeshes
    {
        public const string PrivateRepositoryName = "zantetsuken-assets-private";
        public const string OutputFolder = "Assets/Licensed/DisplayMeshes";

        // One fixture of at most 10,000 triangles per category.
        public static readonly IReadOnlyList<LicensedDisplayMesh> Selection = new[]
        {
            new LicensedDisplayMesh("Character", "geometry-polyprouniverse-character-b7b71285c0cd8d4e57af-original"),
            new LicensedDisplayMesh("Vehicle", "geometry-polyprouniverse-vehicle-0fb6f980deebd8214e98-original"),
            new LicensedDisplayMesh("Building", "geometry-polyprouniverse-building-1ccdeafc493eb7a2086e-original"),
            new LicensedDisplayMesh("Prop", "geometry-polyprouniverse-props-0637845715156b1d3638-original"),
        };

        public static string PrivateRepositoryRoot
        {
            get
            {
                string repositoryRoot = Directory.GetParent(Application.dataPath).FullName;
                return Path.Combine(Directory.GetParent(repositoryRoot).FullName, PrivateRepositoryName);
            }
        }

        public static bool IsPrivateRepositoryAvailable =>
            File.Exists(Path.Combine(PrivateRepositoryRoot, "Working", "Phase0.2", "Adopted", "dataset-index.json"));

        public static string AssetPathFor(LicensedDisplayMesh entry)
        {
            return OutputFolder + "/" + entry.Category + ".asset";
        }

        /// <summary>The asset GUID a fixture's mesh is always saved with.</summary>
        public static string GuidFor(LicensedDisplayMesh entry)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes("zantetsu.licensed-display-mesh/" + entry.ArtifactId));
                StringBuilder text = new StringBuilder(32);
                foreach (byte value in hash)
                {
                    text.Append(value.ToString("x2"));
                }

                return text.ToString();
            }
        }

        [MenuItem("Tools/Zantetsu/Generate Licensed Display Meshes")]
        private static void GenerateFromMenu()
        {
            Generate();
        }

        /// <summary>
        /// Builds or rebuilds every selected mesh. False, after logging why, when the private
        /// repository is absent, a fixture is missing, or a mesh could not keep its GUID.
        /// </summary>
        public static bool Generate()
        {
            if (!IsPrivateRepositoryAvailable)
            {
                Debug.LogWarning("LicensedDisplayMeshes: the sibling private repository is not available.");
                return false;
            }

            AdoptedFixtureCatalog catalog = AdoptedFixtureCatalog.LoadLicensed(PrivateRepositoryRoot);
            IReadOnlyList<AdoptedFixture> renderCut = catalog.Resolve(AdoptedFixtureUse.RenderCut);
            Directory.CreateDirectory(OutputFolder);

            foreach (LicensedDisplayMesh entry in Selection)
            {
                AdoptedFixture fixture = null;
                foreach (AdoptedFixture candidate in renderCut)
                {
                    if (candidate.ArtifactId == entry.ArtifactId)
                    {
                        fixture = candidate;
                        break;
                    }
                }

                if (fixture == null || fixture.Geometry.Kind != ZcgGeometryKind.TriangleMesh)
                {
                    Debug.LogError("LicensedDisplayMeshes: no licensed RenderCut TriangleMesh for " + entry.Category + ".");
                    return false;
                }

                if (!SaveMesh(entry, fixture.Geometry))
                {
                    return false;
                }
            }

            AssetDatabase.SaveAssets();
            return true;
        }

        private static bool SaveMesh(LicensedDisplayMesh entry, ZcgDocument geometry)
        {
            string path = AssetPathFor(entry);
            string guid = GuidFor(entry);

            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(path);
            if (mesh != null && AssetDatabase.AssetPathToGUID(path) == guid)
            {
                Fill(mesh, entry, geometry);
                EditorUtility.SetDirty(mesh);
                AssetDatabase.SaveAssets();
                return true;
            }

            if (mesh != null)
            {
                AssetDatabase.DeleteAsset(path);
            }

            // CreateAsset picks its own GUID, so the serialized file is re-added from
            // outside with a .meta carrying the fixed one, which the import keeps.
            mesh = new Mesh();
            Fill(mesh, entry, geometry);
            AssetDatabase.CreateAsset(mesh, path);
            AssetDatabase.SaveAssets();
            byte[] serialized = File.ReadAllBytes(path);
            AssetDatabase.DeleteAsset(path);
            File.WriteAllBytes(path, serialized);
            File.WriteAllText(path + ".meta",
                "fileFormatVersion: 2\nguid: " + guid + "\nNativeFormatImporter:\n  externalObjects: {}\n"
                + "  mainObjectFileID: 4300000\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n");
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate);

            if (AssetDatabase.AssetPathToGUID(path) != guid)
            {
                Debug.LogError("LicensedDisplayMeshes: " + path + " did not keep its fixed GUID " + guid + ".");
                return false;
            }

            return true;
        }

        private static void Fill(Mesh mesh, LicensedDisplayMesh entry, ZcgDocument geometry)
        {
            Vector3[] vertices = new Vector3[geometry.Positions.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                ZcgPosition position = geometry.Positions[i];
                vertices[i] = new Vector3(position.X, position.Y, position.Z);
            }

            int[] indices = new int[geometry.Triangles.Length * 3];
            for (int i = 0; i < geometry.Triangles.Length; i++)
            {
                ZcgTriangle triangle = geometry.Triangles[i];
                indices[i * 3] = checked((int)triangle.I0);
                indices[i * 3 + 1] = checked((int)triangle.I1);
                indices[i * 3 + 2] = checked((int)triangle.I2);
            }

            mesh.Clear();
            mesh.name = entry.Category;
            mesh.indexFormat = vertices.Length > ushort.MaxValue ? IndexFormat.UInt32 : IndexFormat.UInt16;
            mesh.SetVertices(vertices);
            mesh.SetTriangles(indices, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }
    }
}
