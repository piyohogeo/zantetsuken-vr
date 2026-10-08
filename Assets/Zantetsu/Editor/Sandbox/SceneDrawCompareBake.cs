using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Zantetsu.PhysicsCut;
using Zantetsu.Sandbox;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// The scene draw comparison (TL, 2026-10-07), before any Player: reads a scene's placed cuttables as the
    /// comparison reads them at run time (<see cref="SceneDrawInventory"/>), converts each distinct mesh once by the
    /// product's own mesh conversion, and writes the geometry a Player will be given
    /// (<see cref="SceneDrawMeshBank"/>: the city's models are imported without Read/Write, so a Player cannot read
    /// them) together with what was found -- the targets, their counts, and every renderer the display could not draw
    /// in the MeshRenderer's place, with its reason. The scene and its assets are not changed.
    /// <code>
    /// Unity.exe -batchmode -projectPath &lt;project&gt; -executeMethod Zantetsu.EditorTools.Sandbox.SceneDrawCompareBake.Batch
    ///     -zantetsuDrawCompareScene &lt;scene asset path&gt; -zantetsuDrawCompareBakeOut &lt;new folder&gt; -logFile &lt;log&gt;
    /// </code>
    /// Exit code 0: every renderer drawn can be taken; 2: some were refused (listed); 1: it could not be done.
    /// </summary>
    public static class SceneDrawCompareBake
    {
        public const string GeometryFile = "scene-geometry.vpdc";

        public static void Batch()
        {
            int code = 1;
            try
            {
                string scene = Arg("-zantetsuDrawCompareScene"), folder = Arg("-zantetsuDrawCompareBakeOut");
                if (string.IsNullOrEmpty(scene) || string.IsNullOrEmpty(folder)) throw new ArgumentException("-zantetsuDrawCompareScene <scene> and -zantetsuDrawCompareBakeOut <folder> are required");
                if (Directory.Exists(folder) && Directory.GetFileSystemEntries(folder).Length > 0) throw new IOException("the output folder is not empty: " + folder);
                Directory.CreateDirectory(folder);
                code = Bake(scene, folder);
            }
            catch (Exception e)
            {
                Debug.LogError("SCENE DRAW BAKE: FAILED: " + e);
            }

            EditorApplication.Exit(code);
        }

        /// <summary>Reads the scene, writes the geometry and the records into <paramref name="folder"/>; 0 or 2 (refusals).</summary>
        public static int Bake(string scenePath, string folder)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            CutWorldRoot[] worlds = UnityEngine.Object.FindObjectsByType<CutWorldRoot>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            if (worlds.Length != 1) throw new InvalidOperationException("the scene has " + worlds.Length + " cut worlds; one is expected");
            CutWorldRoot world = worlds[0];
            PlayableCityCuttable[] cuttables = UnityEngine.Object.FindObjectsByType<PlayableCityCuttable>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .OrderBy(c => c.name, StringComparer.Ordinal).ToArray();
            var bank = new SceneDrawMeshBank();
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            SceneDrawInventory inventory = SceneDrawInventory.OfPlacedCuttables(cuttables, world.MaterialBindings, bank);
            double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;

            string geometryPath = Path.Combine(folder, GeometryFile);
            using (FileStream stream = new FileStream(geometryPath, FileMode.CreateNew, FileAccess.Write))
            {
                bank.Write(stream);
            }

            // Read back as a Player reads it: what was written is what will be given.
            using (FileStream stream = File.OpenRead(geometryPath))
            {
                if (!SceneDrawMeshBank.TryRead(stream, out SceneDrawMeshBank read, out string failure)) throw new IOException("the geometry written cannot be read back: " + failure);
                if (read.Count != bank.Count || read.VertexCount != bank.VertexCount || read.IndexCount != bank.IndexCount) throw new IOException("the geometry read back differs from what was written");
            }

            var uses = new Dictionary<SceneDrawMeshBank.Entry, int>();
            var shadows = new SortedDictionary<string, int>(StringComparer.Ordinal);
            var materials = new SortedDictionary<string, int>(StringComparer.Ordinal);
            int staticBatched = 0, unreadable = 0, largestVertices = 0, largestCommands = 0;
            var targetRows = new List<string> { "owner,path,mesh,vertices,indices,submeshes,scaleX,scaleY,scaleZ,x,y,z,materials,sourceIndices,meshReadableInAPlayer,batchingStatic" };
            foreach (SceneDrawTarget t in inventory.Targets)
            {
                uses[t.geometry] = uses.TryGetValue(t.geometry, out int n) ? n + 1 : 1;
                bool flagged = GameObjectUtility.GetStaticEditorFlags(t.renderer.gameObject).HasFlag(StaticEditorFlags.BatchingStatic);
                if (flagged) staticBatched++;
                bool readable = ReadableInAPlayer(t.mesh);
                if (!readable) unreadable++;
                largestVertices = Math.Max(largestVertices, t.geometry.vertices.Length);
                largestCommands = Math.Max(largestCommands, t.sourceIndices.Length);
                string key = t.renderer.shadowCastingMode + " / receives " + t.renderer.receiveShadows;
                shadows[key] = shadows.TryGetValue(key, out int s) ? s + 1 : 1;
                foreach (Material m in t.renderer.sharedMaterials)
                {
                    string name = AssetDatabase.GetAssetPath(m) + " (" + m.shader.name + ")";
                    materials[name] = materials.TryGetValue(name, out int k) ? k + 1 : 1;
                }

                Vector3 p = t.placement.GetColumn(3);
                targetRows.Add(string.Join(",", Csv(t.owner), Csv(t.path), Csv(t.mesh.name), t.geometry.vertices.Length, t.geometry.indices.Length, t.sourceIndices.Length,
                    t.scale.x.ToString("R", inv), t.scale.y.ToString("R", inv), t.scale.z.ToString("R", inv), p.x.ToString("F3", inv), p.y.ToString("F3", inv), p.z.ToString("F3", inv),
                    Csv(string.Join("|", t.renderer.sharedMaterials.Select(m => m.name))), string.Join("|", t.sourceIndices), readable, flagged));
            }

            File.WriteAllLines(Path.Combine(folder, "targets.csv"), targetRows);
            var meshRows = new List<string> { "mesh,vertices,indices,submeshes,targets,key" };
            foreach (SceneDrawMeshBank.Entry e in bank.Entries)
            {
                meshRows.Add(string.Join(",", Csv(e.name), e.vertices.Length, e.indices.Length, e.submeshIndexCount.Length, uses.TryGetValue(e, out int n) ? n : 0, Csv(e.key)));
            }

            File.WriteAllLines(Path.Combine(folder, "meshes.csv"), meshRows);
            File.WriteAllLines(Path.Combine(folder, "refused.txt"), inventory.Refusals.Select(r => r.ToString()));

            CutWorldProfile profile = world.Profile;
            var text = new StringBuilder();
            text.Append("scene: ").Append(scenePath).Append('\n');
            text.Append("placed cuttables in the scene: ").Append(cuttables.Length).Append('\n');
            text.Append("targets: ").Append(inventory.Describe()).Append('\n');
            text.Append("read and converted in ").Append(seconds.ToString("F2", inv)).Append(" s (in the Editor; a Player reads the file)\n");
            text.Append("geometry file: ").Append(GeometryFile).Append(", ").Append(new FileInfo(geometryPath).Length).Append(" bytes: ").Append(bank.Count).Append(" meshes, ")
                .Append(bank.VertexCount).Append(" vertices, ").Append(bank.IndexCount).Append(" indices (each mesh once; a target is appended as a geometry of its own)\n");
            text.Append("largest target: ").Append(largestVertices).Append(" vertices; most submeshes of one target: ").Append(largestCommands).Append('\n');
            text.Append("targets whose mesh a Player cannot read (imported without Read/Write): ").Append(unreadable).Append(" of ").Append(inventory.Targets.Count).Append('\n');
            text.Append("targets flagged for static batching in the Editor: ").Append(staticBatched).Append(" (a batched renderer is refused at run time: its mesh is the batch's)\n");
            foreach (KeyValuePair<string, int> s in shadows) text.Append("shadow setting of the targets: ").Append(s.Key).Append(": ").Append(s.Value).Append('\n');
            foreach (KeyValuePair<string, int> m in materials) text.Append("material slots of the targets: ").Append(m.Key).Append(": ").Append(m.Value).Append('\n');
            foreach (CutWorldRoot.MaterialBinding b in world.MaterialBindings)
            {
                text.Append("the world's display material, source index ").Append(b.sourceIndex).Append(": ").Append(b.material != null ? AssetDatabase.GetAssetPath(b.material) + " (" + b.material.shader.name + ")" : "(none)").Append('\n');
            }

            if (profile != null)
            {
                text.Append("the world's profile (").Append(AssetDatabase.GetAssetPath(profile)).Append("): CPU vertex reserve ").Append(profile.VertexReserve).Append(" (first commit ").Append(profile.VertexInitialCommit)
                    .Append("), CPU index reserve ").Append(profile.IndexReserve).Append(" (first commit ").Append(profile.IndexInitialCommit).Append("), GPU first capacities ").Append(profile.GpuVertexInitialCapacity)
                    .Append(" vertices and ").Append(profile.GpuIndexInitialCapacity).Append(" indices, submeshes ").Append(profile.SubmeshCapacity).Append(", geometry descriptors ").Append(profile.GeometryDescriptorCapacity)
                    .Append(", draw commands ").Append(profile.DrawCommandCapacity).Append(", draw instances ").Append(profile.DrawInstanceCapacity).Append(", geometry references ").Append(profile.GeometryReferenceCapacity)
                    .Append(" (limit ").Append(profile.GeometryReferenceCapacityLimit).Append("), display instances ").Append(profile.DisplayInstanceCapacity).Append(" (limit ").Append(profile.DisplayInstanceCapacityLimit).Append(")\n");
                text.Append("needed against it: vertices ").Append(inventory.Vertices).Append(", indices ").Append(inventory.Indices).Append(", submeshes and draw commands ").Append(inventory.Commands)
                    .Append(", draw instances ").Append(2L * inventory.Commands).Append(" (two a command are asked for at registration), geometries and display instances ").Append(inventory.Targets.Count).Append('\n');
            }

            text.Append("refused: ").Append(inventory.Refusals.Count).Append('\n');
            foreach (IGrouping<string, SceneDrawRefusal> g in inventory.Refusals.GroupBy(r => r.reason).OrderByDescending(g => g.Count()))
            {
                text.Append("  ").Append(g.Count()).Append(" x ").Append(g.Key).Append('\n');
            }

            File.WriteAllText(Path.Combine(folder, "inventory.txt"), text.ToString());
            Debug.Log("SCENE DRAW BAKE:\n" + text);
            return inventory.Refusals.Count > 0 ? 2 : 0;
        }

        // Whether the mesh's importer keeps it readable in a Player: the Editor itself can always read it.
        private static bool ReadableInAPlayer(Mesh mesh)
        {
            string path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path)) return mesh.isReadable;
            return AssetImporter.GetAtPath(path) is ModelImporter model ? model.isReadable : mesh.isReadable;
        }

        private static string Csv(string value) => value != null && (value.Contains(",") || value.Contains("\"")) ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;

        private static string Arg(string name)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name) return args[i + 1];
            }

            return null;
        }
    }
}
