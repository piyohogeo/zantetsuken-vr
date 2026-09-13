using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using Zantetsu.MeshCut.Verification;

namespace Zantetsu.MeshCut.ReferenceIntake
{
    // Registry files written by Tools/ReferenceIntake/Register-ReferenceAsset.ps1 (JsonUtility-compatible shapes).
    [Serializable] public class RegistryBlob { public string file; public string sha256; public long bytes; public string blob; }
    [Serializable] public class RegistryReference { public string kind; public string file; public string sha256; public long bytes; public string blob; }
    [Serializable] public class RegistryManifest { public string assetSha; public string name; public string note; public RegistryBlob fbx; public RegistryBlob[] textures; public RegistryReference[] references; public string registeredUtc; }
    [Serializable] public class RegistryRevision { public string revision; public string createdUtc; public string[] assetShas; public string[] assets; }
    [Serializable] public class RegistryDataset { public string name; public string revision; public string[] assetShas; public string[] assets; public RegistryRevision[] revisions; }

    [Serializable] public class RunCut
    {
        public string plane; public float nx, ny, nz, w;
        public string status; public int K, nodes, newVertices, newIndices, capTriangles, capAux, loops, capFanFallbacks;
        public double kernelMicrosecondsMedian; public bool verified; public string verifierNotes; public string failures;
        public string recutStatus; public bool recutVerified; public int recutK, recutFanFallbacks; public string recutFailures;
    }
    [Serializable] public class RunGeometry
    {
        public string model; public int controlPoints, polygons, nonTrianglePolygons, renderVertices, triangles, submeshes;
        public string inputContract; public string importNotes; public bool inputUsable; public RunCut[] cuts;
    }
    [Serializable] public class RunAsset { public string name; public string assetSha; public string fbxSha256; public string fbxBlob; public bool blobVerified; public string[] references; public RunGeometry[] geometries; public string error; }
    [Serializable] public class RunRecord
    {
        public string runId, createdUtc, intakeRoot, dataset, revision, unityVersion, burstSafetyChecks, kernel;
        public int assets, geometries, cutsAttempted, cutsOk, cutsVerified, inputsUnfit;
        public RunAsset[] results;
    }

    /// <summary>
    /// Phase 0.21's first consumer (DESIGN 10.2.3 "参考実行"): cuts the FBX entities of one explicitly named dataset
    /// revision (or one asset SHA) with the Phase 2.9 kernel and the test-side verifier, and records per asset / geometry
    /// / plane what was read, what was cut, how long the kernel took and what the verifier said. Invoked explicitly
    /// (-executeMethod); the standard EditMode suite never calls it, and it never touches the standard suite's
    /// inputs or verdicts. Records go next to the entities in the private repository, never into the public tree.
    ///
    ///   Unity.exe -batchmode -nographics -projectPath ... -executeMethod Zantetsu.MeshCut.ReferenceIntake.ReferenceCutRun.Main
    ///       -intakeRoot <private repository root> (-intakeDataset name[@revision] | -intakeAsset sha) [-intakeRepeat n] [-logFile ...]
    /// </summary>
    public static class ReferenceCutRun
    {
        static string Arg(string name, string fallback = null)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == name) return args[i + 1];
            return fallback;
        }

        public static void Main()
        {
            int code;
            try { code = Run(Arg("-intakeRoot"), Arg("-intakeDataset"), Arg("-intakeAsset"), int.Parse(Arg("-intakeRepeat", "5"), CultureInfo.InvariantCulture)); }
            catch (Exception e) { Debug.LogError("[reference-run] FAILED: " + e); code = 2; }
            EditorApplication.Exit(code);
        }

        public static int Run(string root, string datasetSpec, string assetSha, int repeat)
        {
            if (string.IsNullOrEmpty(root)) throw new ArgumentException("-intakeRoot is required");
            // Reference timings are taken on the real Burst path without safety checks, like the performance test.
            Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks = false;
            string baseDir = Path.Combine(root, "Working", "Phase0.21");
            string blobs = Path.Combine(baseDir, "blobs");
            string manifests = Path.Combine(baseDir, "registry", "manifests");
            var record = new RunRecord
            {
                runId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 6),
                createdUtc = DateTime.UtcNow.ToString("o"), intakeRoot = "<private repository>", unityVersion = Application.unityVersion,
                burstSafetyChecks = Unity.Burst.BurstCompiler.Options.EnableBurstSafetyChecks ? "on" : "off", kernel = "Zantetsu.MeshCut.MeshCutKernel (Phase 2.9)",
            };
            var targets = new List<string>();
            if (!string.IsNullOrEmpty(assetSha)) { targets.Add(assetSha); record.dataset = "(asset)"; record.revision = assetSha; }
            else if (!string.IsNullOrEmpty(datasetSpec))
            {
                string name = datasetSpec, revision = null;
                int at = datasetSpec.IndexOf('@');
                if (at >= 0) { name = datasetSpec.Substring(0, at); revision = datasetSpec.Substring(at + 1); }
                var ds = JsonUtility.FromJson<RegistryDataset>(File.ReadAllText(Path.Combine(baseDir, "registry", "datasets", name + ".json")));
                revision = revision ?? ds.revision;
                RegistryRevision rev = null;
                foreach (var r in ds.revisions) if (r.revision == revision) rev = r;
                if (rev == null) throw new ArgumentException("revision " + revision + " is not retained in dataset " + name);
                targets.AddRange(rev.assetShas);
                record.dataset = name; record.revision = revision;
            }
            else throw new ArgumentException("-intakeDataset or -intakeAsset is required");
            // The input set is fixed here; registry updates during the run are not consulted again.

            var results = new List<RunAsset>();
            foreach (string sha in targets)
            {
                var asset = new RunAsset { assetSha = sha };
                results.Add(asset);
                try
                {
                    var m = JsonUtility.FromJson<RegistryManifest>(File.ReadAllText(Path.Combine(manifests, sha + ".json")));
                    asset.name = m.name; asset.fbxSha256 = m.fbx.sha256; asset.fbxBlob = m.fbx.blob;
                    var refs = new List<string>();
                    foreach (var r in m.references ?? Array.Empty<RegistryReference>()) refs.Add(r.kind + ":" + r.file + ":" + r.blob);
                    asset.references = refs.ToArray();
                    string fbxPath = Path.Combine(blobs, m.fbx.blob);
                    asset.blobVerified = Sha256(fbxPath) == m.fbx.sha256;
                    if (!asset.blobVerified) { asset.error = "FBX blob hash mismatch"; continue; }
                    var import = FbxGeometryImport.Import(fbxPath);
                    var geometries = new List<RunGeometry>();
                    foreach (var g in import.Geometries)
                    {
                        var rg = new RunGeometry
                        {
                            model = g.ModelName, controlPoints = g.ControlPoints, polygons = g.Polygons, nonTrianglePolygons = g.NonTrianglePolygons,
                            renderVertices = g.RenderVertices, triangles = g.Mesh?.TriangleCount ?? 0, submeshes = g.Mesh?.SubmeshIndexCounts.Count ?? 0,
                            importNotes = string.Join("; ", g.Notes), cuts = Array.Empty<RunCut>(),
                        };
                        geometries.Add(rg);
                        record.geometries++;
                        if (!g.Usable) { rg.inputContract = "no triangles"; record.inputsUnfit++; continue; }
                        using (var h = new MeshCutHarness(256 << 20))
                        {
                            var cg = h.Place(g.Mesh, g.ModelName, 4096, 4096);
                            var problems = LogicalTopology.Build(cg).Validate(6);
                            var reference = ReferenceCut.Compute(cg, new float4(0, 1, 0, 0));
                            problems.AddRange(reference.Problems);
                            rg.inputContract = problems.Count == 0 ? "ok" : string.Join("; ", problems);
                            rg.inputUsable = problems.Count == 0;
                            if (!rg.inputUsable) { record.inputsUnfit++; continue; }
                            rg.cuts = CutAllPlanes(h, cg, g, repeat, record);
                        }
                    }
                    asset.geometries = geometries.ToArray();
                    record.assets++;
                }
                catch (Exception e) { asset.error = e.GetType().Name + ": " + e.Message; Debug.LogWarning("[reference-run] " + sha + ": " + e); }
            }
            record.results = results.ToArray();

            string runsDir = Path.Combine(baseDir, "runs");
            Directory.CreateDirectory(runsDir);
            File.WriteAllText(Path.Combine(runsDir, record.runId + ".json"), JsonUtility.ToJson(record, true) + "\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(runsDir, record.runId + ".md"), Summary(record), new UTF8Encoding(false));
            Debug.Log("[reference-run] " + record.runId + ": assets=" + record.assets + " geometries=" + record.geometries + " cuts=" + record.cutsAttempted + " ok=" + record.cutsOk + " verified=" + record.cutsVerified + " unfit=" + record.inputsUnfit);
            return record.cutsAttempted > 0 && record.cutsVerified == record.cutsAttempted ? 0 : 1;
        }

        static RunCut[] CutAllPlanes(MeshCutHarness h, CutGeometry cg, ImportedGeometry g, int repeat, RunRecord record)
        {
            var (mn, mx) = cg.Bounds();
            double3 ext = mx - mn, center = 0.5 * (mn + mx);
            int longest = ext.x >= ext.y && ext.x >= ext.z ? 0 : ext.y >= ext.z ? 1 : 2;
            int shortest = ext.x <= ext.y && ext.x <= ext.z ? 0 : ext.y <= ext.z ? 1 : 2;
            float3 Axis(int a) => a == 0 ? new float3(1, 0, 0) : a == 1 ? new float3(0, 1, 0) : new float3(0, 0, 1);
            // the probe's plane classes: center (+1.37% of the longest extent), dense (shortest axis, +0.71%), grazing (97.1% of the longest), nasty (tilted)
            var planes = new List<(string, float4)>
            {
                ("center", SyntheticGeometry.Plane(Axis(longest), (float3)center + Axis(longest) * (float)(0.0137 * ext[longest]))),
                ("dense", SyntheticGeometry.Plane(Axis(shortest), (float3)center + Axis(shortest) * (float)(0.0071 * ext[shortest]))),
                ("grazing", SyntheticGeometry.Plane(Axis(longest), (float3)mn + Axis(longest) * (float)(0.971 * ext[longest]))),
                ("nasty", SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), (float3)center + new float3((float)(0.0071 * ext.x), (float)(-0.0233 * ext.y), (float)(0.0119 * ext.z)))),
            };
            var cuts = new List<RunCut>();
            foreach (var (label, plane) in planes)
            {
                var cut = new RunCut { plane = label, nx = plane.x, ny = plane.y, nz = plane.z, w = plane.w };
                cuts.Add(cut);
                record.cutsAttempted++;
                CutRun run = null;
                var times = new List<double>();
                for (int i = 0; i < Math.Max(1, repeat); i++)
                {
                    using (var scratchHarness = new MeshCutHarness(256 << 20))
                    {
                        var g2 = scratchHarness.Place(g.Mesh, g.ModelName, 4096, 4096);
                        var r = scratchHarness.Cut(g2, plane, new RunOptions { CheckUnrelatedUnchanged = i == 0 });
                        times.Add(scratchHarness.LastMicroseconds);
                        if (i == 0)
                        {
                            run = r;
                            cut.status = r.Result.status.ToString();
                            cut.K = r.Result.crossingTriangles; cut.nodes = r.Result.nodeCount; cut.newVertices = r.Result.newVertexCount; cut.newIndices = r.Result.newIndexCount;
                            cut.capTriangles = r.Result.capTriangles; cut.capAux = r.Result.capAuxVertices; cut.loops = r.Result.loopCount; cut.capFanFallbacks = r.Result.capFanFallbacks;
                            if (r.Result.status == MeshCutStatus.Ok)
                            {
                                record.cutsOk++;
                                var report = MeshCutVerifier.Verify(r);
                                cut.verified = report.Passed;
                                cut.verifierNotes = string.Join("; ", report.Notes);
                                cut.failures = report.Passed ? "" : string.Join(" | ", report.Failures);
                                if (scratchHarness.LastBadGuards.Count > 0 || scratchHarness.LastUnrelatedChanges.Count > 0) { cut.verified = false; cut.failures += " | memory: " + string.Join(",", scratchHarness.LastBadGuards) + string.Join(",", scratchHarness.LastUnrelatedChanges); }
                                if (cut.verified) record.cutsVerified++;
                                // re-cut of the larger side with the nasty plane through its centre (recut on real data)
                                var larger = r.Positive.TriangleCount >= r.Negative.TriangleCount ? r.Positive : r.Negative;
                                if (larger.TriangleCount > 0 && r.Result.crossingTriangles > 0)
                                {
                                    var (bmn, bmx) = larger.Bounds();
                                    var recutPlane = SyntheticGeometry.Plane(new float3(-0.61f, 0.37f, 0.7f), (float3)(0.5 * (bmn + bmx)) + new float3(0.0031f, 0.0023f, -0.0019f) * (float)math.cmax(bmx - bmn));
                                    var rr = scratchHarness.Cut(larger, recutPlane);
                                    cut.recutStatus = rr.Result.status.ToString();
                                    cut.recutK = rr.Result.crossingTriangles; cut.recutFanFallbacks = rr.Result.capFanFallbacks;
                                    if (rr.Result.status == MeshCutStatus.Ok)
                                    {
                                        var rep2 = MeshCutVerifier.Verify(rr);
                                        cut.recutVerified = rep2.Passed;
                                        cut.recutFailures = rep2.Passed ? "" : string.Join(" | ", rep2.Failures);
                                    }
                                }
                            }
                            else cut.failures = "status " + r.Result.status + " required v/i/s=" + r.Result.requiredVertexCapacity + "/" + r.Result.requiredIndexCapacity + "/" + r.Result.requiredScratchBytes;
                        }
                    }
                }
                times.Sort();
                cut.kernelMicrosecondsMedian = times[times.Count / 2];
            }
            return cuts.ToArray();
        }

        static string Summary(RunRecord r)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Reference cut run " + r.runId);
            sb.AppendLine();
            sb.AppendLine("- dataset: " + r.dataset + " @ " + r.revision);
            sb.AppendLine("- created: " + r.createdUtc + ", Unity " + r.unityVersion + ", Burst safety checks " + r.burstSafetyChecks);
            sb.AppendLine("- assets " + r.assets + ", geometries " + r.geometries + ", cuts attempted " + r.cutsAttempted + ", ok " + r.cutsOk + ", verified " + r.cutsVerified + ", inputs unfit " + r.inputsUnfit);
            sb.AppendLine();
            sb.AppendLine("| asset | model | T | renderV/controlPoints | input | plane | K | newV | newI | caps | aux | fan | kernel us (median of repeats) | verified | recut | notes |");
            sb.AppendLine("| --- | --- | ---: | --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- | --- |");
            foreach (var a in r.results)
            {
                if (a.error != null) { sb.AppendLine("| " + a.name + " | | | | ERROR " + a.error + " | | | | | | | | | | |"); continue; }
                foreach (var g in a.geometries)
                {
                    if (g.cuts == null || g.cuts.Length == 0) { sb.AppendLine("| " + a.name + " | " + g.model + " | " + g.triangles + " | " + g.renderVertices + "/" + g.controlPoints + " | " + g.inputContract + " | | | | | | | | | | " + g.importNotes + " |"); continue; }
                    foreach (var c in g.cuts)
                        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "| {0} | {1} | {2} | {3}/{4} | {5} | {6} | {7} | {8} | {9} | {10} | {11} | {16} | {12:F1} | {13} | {14} | {15} |",
                            a.name, g.model, g.triangles, g.renderVertices, g.controlPoints, g.inputContract, c.plane, c.K, c.newVertices, c.newIndices, c.capTriangles, c.capAux, c.kernelMicrosecondsMedian,
                            c.status == "Ok" ? (c.verified ? "yes" : "NO: " + c.failures) : c.status + " " + c.failures,
                            c.recutStatus == null ? "-" : c.recutStatus + (c.recutStatus == "Ok" ? (c.recutVerified ? " verified" : " NOT verified: " + c.recutFailures) : "") + " K=" + c.recutK,
                            (c.verifierNotes ?? "") + (string.IsNullOrEmpty(g.importNotes) ? "" : " import: " + g.importNotes), c.capFanFallbacks + "/" + c.recutFanFallbacks));
                }
            }
            sb.AppendLine();
            sb.AppendLine("Entities: `blobs/<fbxBlob>` per asset (see the JSON record for blob names and the manifests under `registry/manifests/<assetSha>.json` for textures and reference materials).");
            return sb.ToString();
        }

        static string Sha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(path))
            {
                var hash = sha.ComputeHash(f);
                var sb = new StringBuilder(64);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
