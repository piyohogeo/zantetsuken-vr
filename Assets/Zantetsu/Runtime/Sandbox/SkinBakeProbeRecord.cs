using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The bake probe's record (DESIGN 4.5.14): one row a case (a bake compared, an index copy compared, a drawing window),
    /// written as skin-bake-probe.csv, skin-bake-probe.md and skin-bake-probe.json in the probe's directory -- its own
    /// format and place, apart from the benchmark's. Nothing here is a performance sample.
    /// </summary>
    public sealed class SkinBakeProbeRecord
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        public static readonly string[] Columns =
        {
            "subject", "case", "kind", "pass", "frameBaked", "frameVerified", "destination", "start", "count", "guardBefore", "guardAfter", "processed", "invalidGpu", "invalidCpu", "invalidDisagree", "bonesOutsidePalette",
            "positionMaxError", "positionMaxErrorVertex", "positionOverTolerance", "normalExact", "normalBoundary", "normalOther", "normalMaxAngleDeg", "uvExact", "uvBoundary", "uvOther", "indexMismatches", "bakeMs", "notes",
        };

        private readonly List<string[]> _rows = new List<string[]>();
        /// <summary>The bench's frame rows (SKIN-BAKE-PROBE-2), written as skin-bake-bench.csv beside the cases: the sums over a frame's bodies, in milliseconds.</summary>
        public static readonly string[] BenchColumns =
        {
            "condition", "route", "bodies", "repeat", "frame", "kernelMs", "boundsMs", "uploadMs", "indicesMs", "issueMs", "totalMs", "invalid", "burstRan", "writesRefused",
            "frameDtMs", "ftMainMs", "ftRenderMs", "ftGpuMs", "ftPresentWaitMs", "xrAppGpuMs",
        };
        /// <summary>The cut bench's frame rows (SKIN-GPU-FIRST-1), written as skin-bake-cut-frames.csv: the round, the frame, where it stands relative to the requests' frame, how many requests are pending, and the frame's whole times.</summary>
        public static readonly string[] CutFrameColumns =
        {
            "round", "frame", "sinceRequest", "pendingBakes", "pendingStorage", "gpuRegions", "frameDtMs", "ftMainMs", "ftRenderMs", "ftGpuMs", "ftPresentWaitMs", "xrAppGpuMs", "ftFrame", "ftStartOffsetMs",
        };
        private readonly List<string[]> _cutFrames = new List<string[]>();
        public IReadOnlyList<string[]> CutFrameRows => _cutFrames;

        public void CutFrame(params string[] values)
        {
            if (values == null || values.Length != CutFrameColumns.Length) throw new ArgumentException("a cut frame row has " + CutFrameColumns.Length + " values", nameof(values));
            _cutFrames.Add(values);
        }

        /// <summary>The cut bench's request rows (SKIN-GPU-FIRST-1), written as skin-bake-cut-requests.csv: one a request.</summary>
        public static readonly string[] CutRequestColumns =
        {
            "round", "body", "route", "requestFrame", "outcome", "acceptance", "requestMainMs", "acceptedFrame", "physicsFrame", "publishedFrame", "clipFrame", "readyFrame", "committedFrame",
            "framesToAccept", "framesToPhysics", "framesToPublish", "framesToClip", "framesToReady", "framesToCommit", "cutKernelMs", "readyMainMs", "bakeWorkerMs", "fallbackReason", "basisOutcome", "switchCompare",
            "cutPlane", "planeLocal", "planeWorld", "anchor", "budgetWait", "geometryWait",
            // SKIN-GPU-FIRST-EVAL-1: the events themselves (the product's diagnosis hooks; -1 = not reported) beside the observations above, and the clip state at onBeforeRender.
            "physicsObservedFrame", "ledgerEventFrame", "clipBeforeRenderFrame", "readyEventFrame", "readyOutcome", "committedEventFrame", "framesToLedgerEvent", "framesToClipBeforeRender", "framesToReadyEvent", "framesToCommitEvent",
            // EVAL-1 review 1 (P2-3): "valid", or "invalid: ..." when a reset before the round did not return to the initial state (the summary leaves such rounds out).
            "roundValid",
        };
        /// <summary>The cut bench's picture rows (PLANE-1), written as skin-bake-cut-pictures.csv: for every picture and body, the events observed so far (frames; -1 not observed by the bench, which may have missed an event between two observations), the display states and the physics placements (position, rotation as Euler degrees) of the source and the two sides, at the bench's step of the pictured frame.</summary>
        public static readonly string[] CutPictureColumns =
        {
            "round", "k", "frame", "picture", "body", "requestFrame", "acceptance", "acceptedFrame", "physicsFrame", "publishedFrame", "readyFrame", "committedFrame",
            "displaySource", "displayPositive", "displayNegative", "sourcePos", "sourceRot", "positivePos", "positiveRot", "negativePos", "negativeRot", "note", "eyePos", "eyeRot",
            // MOTION-CAPTURE-1 step 6: the display's placements (geometry local -> world, Kind(16 row-major numbers)): the source drawn whole (before the request: Renderer(...) = the uncut renderer's localToWorld), its provisional +/- sides, the two children.
            "placementSource", "placementSourcePositiveSide", "placementSourceNegativeSide", "placementPositive", "placementNegative",
        };
        /// <summary>The motion captures (PLANE-1), written as skin-bake-motion.csv: one row an eye and frame -- the texture, the pixels with a nonzero vector, the NaN pixels, the mean and the largest magnitude (texture units), and the files written.</summary>
        public static readonly string[] MotionColumns = { "frame", "eye", "width", "height", "format", "decoded", "pixels", "nonzero", "nan", "meanMagnitude", "maxMagnitude", "p99Magnitude", "alphaCoverage", "alphaRows", "rawFile", "pictureFile", "failure" };
        private readonly List<string[]> _motion = new List<string[]>();
        public IReadOnlyList<string[]> MotionRows => _motion;

        public void Motion(params string[] values)
        {
            if (values == null || values.Length != MotionColumns.Length) throw new ArgumentException("a motion row has " + MotionColumns.Length + " values", nameof(values));
            _motion.Add(values);
        }
        private readonly List<string[]> _cutPictures = new List<string[]>();
        public IReadOnlyList<string[]> CutPictureRows => _cutPictures;

        public void CutPicture(params string[] values)
        {
            if (values == null || values.Length != CutPictureColumns.Length) throw new ArgumentException("a cut picture row has " + CutPictureColumns.Length + " values", nameof(values));
            _cutPictures.Add(values);
        }
        private readonly List<string[]> _cutRequests = new List<string[]>();
        public IReadOnlyList<string[]> CutRequestRows => _cutRequests;

        public void CutRequest(params string[] values)
        {
            if (values == null || values.Length != CutRequestColumns.Length) throw new ArgumentException("a cut request row has " + CutRequestColumns.Length + " values", nameof(values));
            _cutRequests.Add(values);
        }

        /// <summary>A CSV field: quoted where it holds a comma, a quote or a line break.</summary>
        private static string CsvField(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
        }

        /// <summary>Lines written as a file of their own beside the record (the skin bake stats' CSV).</summary>
        private readonly Dictionary<string, List<string>> _extraFiles = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        public void ExtraFile(string name, IEnumerable<string> lines) { _extraFiles[name] = new List<string>(lines); }

        /// <summary>The bench's statistics, one row a condition and column: n (present values), median, p95 (nearest rank), mean, min, max; written as skin-bake-bench-summary.csv.</summary>
        public static readonly string[] BenchSummaryColumns = { "condition", "route", "bodies", "repeat", "column", "n", "median", "p95", "mean", "min", "max" };
        private readonly List<string[]> _benchSummary = new List<string[]>();

        public void BenchSummary(params string[] values)
        {
            if (values == null || values.Length != BenchSummaryColumns.Length) throw new ArgumentException("a bench summary row has " + BenchSummaryColumns.Length + " values", nameof(values));
            _benchSummary.Add(values);
        }
        private readonly List<string[]> _bench = new List<string[]>();
        public IReadOnlyList<string[]> BenchRows => _bench;

        public void BenchFrame(params string[] values)
        {
            if (values == null || values.Length != BenchColumns.Length) throw new ArgumentException("a bench row has " + BenchColumns.Length + " values", nameof(values));
            _bench.Add(values);
        }
        private readonly List<string> _lines = new List<string>();
        private readonly Dictionary<string, string> _facts = new Dictionary<string, string>(StringComparer.Ordinal);

        public IReadOnlyList<string[]> Rows => _rows;
        public int Passed { get; private set; }
        public int Failed { get; private set; }

        /// <summary>A fact of the run (the device, the layouts, the costs), kept in the json and the markdown.</summary>
        public void Fact(string key, string value) { _facts[key] = value ?? ""; }

        public void Line(string text) { _lines.Add(text); }

        public void Bake(string subject, string caseName, SkinBakeProbeCompare.Result r, string destination, int start, double bakeSeconds, string notes)
        {
            Count(r.Pass);
            _rows.Add(new[]
            {
                subject, caseName, "bake", r.Pass ? "PASS" : "FAIL", r.frameRequested.ToString(Inv), r.frameCompleted.ToString(Inv), destination, start.ToString(Inv), r.count.ToString(Inv),
                "", "", r.processed.ToString(Inv), r.invalidGpu.ToString(Inv), r.invalidCpu.ToString(Inv), r.invalidDisagree.ToString(Inv), r.bonesOutsidePalette.ToString(Inv),
                r.positionMaxError.ToString("E3", Inv), r.positionMaxErrorVertex.ToString(Inv), r.positionOverTolerance.ToString(Inv), r.normalExact.ToString(Inv), r.normalBoundary.ToString(Inv), r.normalOther.ToString(Inv),
                r.normalMaxAngleDegrees.ToString("F4", Inv), r.uvExact.ToString(Inv), r.uvBoundary.ToString(Inv), r.uvOther.ToString(Inv), "", (bakeSeconds * 1000.0).ToString("F4", Inv),
                (r.readbackError ? "readback error; " : "") + (r.guardBeforeIntact ? "" : "GUARD BEFORE WRITTEN; ") + (r.guardAfterIntact ? "" : "GUARD AFTER WRITTEN; ") + (notes ?? ""),
            });
            Line(subject + " / " + caseName + ": " + r.Describe() + (string.IsNullOrEmpty(notes) ? "" : " -- " + notes));
        }

        public void Indices(string subject, string caseName, SkinBakeProbeCompare.IndexResult r, string notes)
        {
            Count(r.Pass);
            var row = new string[Columns.Length];
            for (int i = 0; i < row.Length; i++) row[i] = "";
            row[0] = subject; row[1] = caseName; row[2] = "indices"; row[3] = r.Pass ? "PASS" : "FAIL"; row[4] = r.frameRequested.ToString(Inv); row[5] = r.frameCompleted.ToString(Inv);
            row[8] = r.count.ToString(Inv); row[26] = r.mismatches.ToString(Inv); row[28] = (r.readbackError ? "readback error; " : "") + (notes ?? "");
            _rows.Add(row);
            Line(subject + " / " + caseName + ": " + r.Describe());
        }

        /// <summary>A row of another kind (a drawing window, a refusal, an observation) with its own verdict; pass null for no verdict.</summary>
        public void Other(string subject, string caseName, string kind, bool? pass, int frame, string notes)
        {
            if (pass.HasValue) Count(pass.Value);
            var row = new string[Columns.Length];
            for (int i = 0; i < row.Length; i++) row[i] = "";
            row[0] = subject; row[1] = caseName; row[2] = kind; row[3] = pass.HasValue ? (pass.Value ? "PASS" : "FAIL") : "-"; row[4] = frame.ToString(Inv); row[28] = notes ?? "";
            _rows.Add(row);
            Line(subject + " / " + caseName + " (" + kind + "): " + (pass.HasValue ? (pass.Value ? "PASS" : "FAIL") : "noted") + "; frame " + frame + "; " + notes);
        }

        private void Count(bool pass) { if (pass) Passed++; else Failed++; }

        public void Write(string directory, string title)
        {
            Directory.CreateDirectory(directory);
            var csv = new StringBuilder(string.Join(",", Columns) + "\n");
            foreach (string[] row in _rows) csv.Append(string.Join(",", Array.ConvertAll(row, Cell))).Append('\n');
            File.WriteAllText(Path.Combine(directory, "skin-bake-probe.csv"), csv.ToString());
            if (_bench.Count > 0)
            {
                var bench = new StringBuilder(string.Join(",", BenchColumns) + "\n");
                foreach (string[] row in _bench) bench.Append(string.Join(",", Array.ConvertAll(row, Cell))).Append('\n');
                File.WriteAllText(Path.Combine(directory, "skin-bake-bench.csv"), bench.ToString());
            }

            if (_cutFrames.Count > 0)
            {
                var frames = new StringBuilder(string.Join(",", CutFrameColumns) + "\n");
                foreach (string[] row in _cutFrames) frames.Append(string.Join(",", row)).Append('\n');
                File.WriteAllText(Path.Combine(directory, "skin-bake-cut-frames.csv"), frames.ToString());
            }

            if (_cutRequests.Count > 0)
            {
                var requests = new StringBuilder(string.Join(",", CutRequestColumns) + "\n");
                foreach (string[] row in _cutRequests)
                {
                    for (int c = 0; c < row.Length; c++) { if (c > 0) requests.Append(','); requests.Append(CsvField(row[c])); }
                    requests.Append('\n');
                }
                File.WriteAllText(Path.Combine(directory, "skin-bake-cut-requests.csv"), requests.ToString());
            }

            if (_cutPictures.Count > 0)
            {
                var pictures = new StringBuilder(string.Join(",", CutPictureColumns) + "\n");
                foreach (string[] row in _cutPictures)
                {
                    for (int c = 0; c < row.Length; c++) { if (c > 0) pictures.Append(','); pictures.Append(CsvField(row[c])); }
                    pictures.Append('\n');
                }
                File.WriteAllText(Path.Combine(directory, "skin-bake-cut-pictures.csv"), pictures.ToString());
            }

            if (_motion.Count > 0)
            {
                var motion = new StringBuilder(string.Join(",", MotionColumns) + "\n");
                foreach (string[] row in _motion)
                {
                    for (int c = 0; c < row.Length; c++) { if (c > 0) motion.Append(','); motion.Append(CsvField(row[c])); }
                    motion.Append('\n');
                }
                File.WriteAllText(Path.Combine(directory, "skin-bake-motion.csv"), motion.ToString());
            }

            foreach (var file in _extraFiles)
            {
                File.WriteAllText(Path.Combine(directory, file.Key), string.Join("\n", file.Value) + "\n");
            }

            if (_benchSummary.Count > 0)
            {
                var summary = new StringBuilder(string.Join(",", BenchSummaryColumns) + "\n");
                foreach (string[] row in _benchSummary) summary.Append(string.Join(",", Array.ConvertAll(row, Cell))).Append('\n');
                File.WriteAllText(Path.Combine(directory, "skin-bake-bench-summary.csv"), summary.ToString());
            }

            var md = new StringBuilder("# " + title + "\n\n");
            md.Append("cases passed ").Append(Passed).Append(", failed ").Append(Failed).Append("\n\n## Facts\n\n");
            foreach (KeyValuePair<string, string> f in _facts) md.Append("- ").Append(f.Key).Append(": ").Append(f.Value.Replace("\n", " ")).Append('\n');
            md.Append("\n## Cases\n\n| subject | case | kind | pass | frame | destination | start | count | processed | pos max err | pos over tol | normals exact/boundary/other | max angle | uv exact/boundary/other | invalid gpu/cpu/dis | index mismatches | bake ms | notes |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
            foreach (string[] r in _rows)
            {
                md.Append("| ").Append(r[0]).Append(" | ").Append(r[1]).Append(" | ").Append(r[2]).Append(" | ").Append(r[3]).Append(" | ").Append(r[4]).Append(" | ").Append(r[6]).Append(" | ").Append(r[7]).Append(" | ").Append(r[8])
                    .Append(" | ").Append(r[11]).Append(" | ").Append(r[16]).Append(" | ").Append(r[18]).Append(" | ").Append(r[19]).Append('/').Append(r[20]).Append('/').Append(r[21]).Append(" | ").Append(r[22])
                    .Append(" | ").Append(r[23]).Append('/').Append(r[24]).Append('/').Append(r[25]).Append(" | ").Append(r[12]).Append('/').Append(r[13]).Append('/').Append(r[14]).Append(" | ").Append(r[26]).Append(" | ").Append(r[27])
                    .Append(" | ").Append(r[28].Replace("|", "/")).Append(" |\n");
            }

            md.Append("\n## Lines\n\n");
            foreach (string l in _lines) md.Append("- ").Append(l.Replace("\n", " ")).Append('\n');
            File.WriteAllText(Path.Combine(directory, "skin-bake-probe.md"), md.ToString());

            var json = new StringBuilder("{\n  \"title\": ").Append(Quote(title)).Append(",\n  \"passed\": ").Append(Passed).Append(",\n  \"failed\": ").Append(Failed).Append(",\n  \"facts\": {\n");
            bool first = true;
            foreach (KeyValuePair<string, string> f in _facts) { json.Append(first ? "" : ",\n").Append("    ").Append(Quote(f.Key)).Append(": ").Append(Quote(f.Value)); first = false; }
            json.Append("\n  },\n  \"rows\": [\n");
            for (int i = 0; i < _rows.Count; i++)
            {
                json.Append(i == 0 ? "" : ",\n").Append("    {");
                for (int c = 0; c < Columns.Length; c++) json.Append(c == 0 ? "" : ", ").Append(Quote(Columns[c])).Append(": ").Append(Quote(_rows[i][c]));
                json.Append('}');
            }

            json.Append("\n  ]\n}\n");
            File.WriteAllText(Path.Combine(directory, "skin-bake-probe.json"), json.ToString());
        }

        private static string Cell(string v) => v == null ? "" : (v.IndexOfAny(new[] { ',', '"', '\n' }) >= 0 ? "\"" + v.Replace("\"", "'").Replace("\n", " ") + "\"" : v);
        private static string Quote(string v) => "\"" + (v ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "") + "\"";
    }
}
