using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// NPC-ATTEMPT-CAPTURE-1: reads back a capture session of the NPCs' additional-split attempts (the split capture with
    /// <c>-zantetsuSplitCaptureNpcOnly</c>): every attempt the samples' index lists, with its sample, its bones' sidecar, its geometry's
    /// topology table and its live result line -- each file there with the hash its index line lists, all four of one attempt, one
    /// fragment and one epoch, the table covering every vertex the sample names -- and, for an attempt that is whole, the bone-aware
    /// decision run again over what was saved (the pick verification's own path, with the saved table) beside the live line. Writes a
    /// report, one JSON line an attempt, and the fixed list of the whole inputs (the comparison's list format, with the topology's
    /// hash). A check of what was collected, nothing of the game.
    /// Batch: <c>-executeMethod Zantetsu.EditorTools.Sandbox.NpcAttemptCaptureVerify.Run -zantetsuNpcCaptureFolder &lt;session folder&gt;
    /// -zantetsuNpcCaptureReport &lt;file&gt; -zantetsuNpcCaptureJson &lt;file&gt; -zantetsuNpcCaptureList &lt;file&gt;</c>. Exit 0: every
    /// listed attempt whole and every saved decision the live one; 1 otherwise; 2 the arguments or a failure of the tool.
    /// </summary>
    public static class NpcAttemptCaptureVerify
    {
        public const string FolderArgument = "-zantetsuNpcCaptureFolder", ReportArgument = "-zantetsuNpcCaptureReport", JsonArgument = "-zantetsuNpcCaptureJson", ListArgument = "-zantetsuNpcCaptureList";

        [Serializable]
        private sealed class IndexLine
        {
            public long world, attempt;
            public string file, sha256, what, why;
            public int fragment;
        }

        [Serializable]
        private sealed class SummaryLine
        {
            public string description;
            public long issued, accepted, saved, unindexed, lost, inFlight, notCaptured, returned, resultsWritten, resultsNotRecorded;
            public string stopReason, stopDetail;
        }

        public static void Run()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            string folder = ValueOf(arguments, FolderArgument), report = ValueOf(arguments, ReportArgument), json = ValueOf(arguments, JsonArgument), list = ValueOf(arguments, ListArgument);
            if (folder == null || report == null || json == null || list == null || !Directory.Exists(folder))
            {
                Debug.LogError("NPC ATTEMPT CAPTURE VERIFY: " + FolderArgument + " <an existing folder>, " + ReportArgument + ", " + JsonArgument + " and " + ListArgument + " <files> are needed");
                EditorApplication.Exit(2);
                return;
            }

            string text;
            int code;
            try
            {
                text = Verify(folder, json, list, out code);
            }
            catch (Exception e)
            {
                text = "NPC ATTEMPT CAPTURE VERIFY: threw " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace;
                code = 2;
            }

            File.WriteAllText(report, text, new UTF8Encoding(false));
            Debug.Log(text);
            EditorApplication.Exit(code);
        }

        private static List<IndexLine> Lines(string path, List<string> problems)
        {
            var lines = new List<IndexLine>();
            if (!File.Exists(path)) return lines;
            foreach (string raw in File.ReadAllLines(path))
            {
                string t = raw.Trim().TrimStart('﻿');
                if (t.Length == 0) continue;
                try { lines.Add(JsonUtility.FromJson<IndexLine>(t)); }
                catch (Exception e) { problems.Add(Path.GetFileName(path) + ": a line that does not read (" + e.Message + ")"); }
            }

            return lines;
        }

        private static Dictionary<(long, long), IndexLine> ByAttempt(List<IndexLine> lines, string what, List<string> problems, out int twice)
        {
            twice = 0;
            var by = new Dictionary<(long, long), IndexLine>();
            foreach (IndexLine l in lines)
            {
                if (by.ContainsKey((l.world, l.attempt))) { twice++; problems.Add(what + ": attempt " + l.world + "/" + l.attempt + " listed twice"); continue; }
                by[(l.world, l.attempt)] = l;
            }

            return by;
        }

        private static T ReadOne<T>(string folder, IndexLine line, string what, List<string> problems, Func<string, T> read, Func<T, string> hashOf) where T : class
        {
            if (line == null) { problems.Add(what + ": no index line"); return null; }
            if (string.IsNullOrEmpty(line.file)) { problems.Add(what + ": " + (string.IsNullOrEmpty(line.why) ? "no file named" : line.why)); return null; }
            string path = Path.Combine(folder, line.file);
            if (!File.Exists(path)) { problems.Add(what + ": missing " + line.file); return null; }
            T value;
            try { value = read(path); }
            catch (Exception e) { problems.Add(what + ": does not read: " + e.GetType().Name + ": " + e.Message); return null; }
            if (!string.Equals(line.sha256, hashOf(value), StringComparison.Ordinal)) problems.Add(what + ": the hash is not the listed one");
            return value;
        }

        // What the rig and the model's vertex bones are, as one name: attempts of one model share it.
        private static string ModelKey(SpatialSplitBoneSample b)
        {
            using (var sha = SHA256.Create())
            using (var memory = new MemoryStream())
            using (var w = new BinaryWriter(memory))
            {
                w.Write(b.nodeCount);
                foreach (int p in b.parent) w.Write(p);
                w.Write(b.baseCount);
                if (b.baseBones != null) foreach (ulong e in b.baseBones) w.Write(e);
                w.Flush();
                byte[] hash = sha.ComputeHash(memory.ToArray());
                var s = new StringBuilder(16);
                for (int i = 0; i < 8; i++) s.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return s.ToString();
            }
        }

        public static string Verify(string folder, string jsonPath, string listPath, out int code)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            var text = new StringBuilder();
            var general = new List<string>();
            string sessionId = "";
            string sessionFile = Path.Combine(folder, "session.json");
            if (File.Exists(sessionFile)) sessionId = JsonUtility.FromJson<SpatialSplitSessionIdLine>(File.ReadAllText(sessionFile))?.sessionId ?? "";
            else general.Add("no session.json");
            SummaryLine summary = null;
            string summaryFile = Path.Combine(folder, "summary.json");
            if (File.Exists(summaryFile)) summary = JsonUtility.FromJson<SummaryLine>(File.ReadAllText(summaryFile));
            else general.Add("no summary.json (the session was not closed)");

            List<IndexLine> sampleLines = Lines(Path.Combine(folder, "samples.jsonl"), general);
            Dictionary<(long, long), IndexLine> samples = ByAttempt(sampleLines, "samples", general, out int samplesTwice);
            Dictionary<(long, long), IndexLine> bones = ByAttempt(Lines(Path.Combine(folder, "bones.jsonl"), general), "bones", general, out int bonesTwice);
            Dictionary<(long, long), IndexLine> topologies = ByAttempt(Lines(Path.Combine(folder, NpcPieceTopologySample.Index), general), "topologies", general, out int topologiesTwice);
            List<IndexLine> failures = Lines(Path.Combine(folder, "capture-failures.jsonl"), general);
            var live = new Dictionary<(long, long), SpatialSplitBoneResultLine>();
            int liveTwice = 0;
            string liveFile = Path.Combine(folder, "bone-results.jsonl");
            if (File.Exists(liveFile))
            {
                foreach (string raw in File.ReadAllLines(liveFile))
                {
                    string t = raw.Trim().TrimStart('﻿');
                    if (t.Length == 0) continue;
                    SpatialSplitBoneResultLine l = JsonUtility.FromJson<SpatialSplitBoneResultLine>(t);
                    if (live.ContainsKey((l.world, l.attempt))) { liveTwice++; general.Add("bone-results: attempt " + l.world + "/" + l.attempt + " listed twice"); continue; }
                    live[(l.world, l.attempt)] = l;
                }
            }

            // sidecars without a sample
            int orphanBones = 0, orphanTopologies = 0, orphanResults = 0;
            foreach ((long, long) k in bones.Keys) if (!samples.ContainsKey(k)) orphanBones++;
            foreach ((long, long) k in topologies.Keys) if (!samples.ContainsKey(k)) orphanTopologies++;
            foreach ((long, long) k in live.Keys) if (!samples.ContainsKey(k)) orphanResults++;

            text.Append("NPC ATTEMPT CAPTURE VERIFY: ").Append(Path.GetFileName(folder.TrimEnd('\\', '/'))).Append(", session ").Append(sessionId).Append('\n');
            if (summary != null)
            {
                text.Append("the session's own count: issued ").Append(summary.issued).Append(", accepted ").Append(summary.accepted).Append(", saved ").Append(summary.saved)
                    .Append(", committed without an index line ").Append(summary.unindexed).Append(", lost ").Append(summary.lost).Append(", in flight ").Append(summary.inFlight)
                    .Append(", not captured ").Append(summary.notCaptured).Append(", returned ").Append(summary.returned).Append("; results written ").Append(summary.resultsWritten)
                    .Append(", not recorded ").Append(summary.resultsNotRecorded).Append("; stop ").Append(summary.stopReason).Append(' ').Append(summary.stopDetail).Append('\n');
            }

            text.Append("index lines: samples ").Append(samples.Count).Append(" (twice ").Append(samplesTwice).Append("), bones ").Append(bones.Count).Append(" (twice ").Append(bonesTwice)
                .Append(", without a sample ").Append(orphanBones).Append("), topologies ").Append(topologies.Count).Append(" (twice ").Append(topologiesTwice).Append(", without a sample ")
                .Append(orphanTopologies).Append("), live results ").Append(live.Count).Append(" (twice ").Append(liveTwice).Append(", without a sample ").Append(orphanResults)
                .Append("), failures told ").Append(failures.Count).Append('\n');
            foreach (IndexLine f in failures) text.Append("  FAILURE TOLD attempt ").Append(f.world).Append('/').Append(f.attempt).Append(": ").Append(f.what).Append(": ").Append(f.why).Append('\n');

            var json = new StringBuilder();
            var list = new StringBuilder();
            int whole = 0, notWhole = 0, compared = 0, differ = 0, noLive = 0, noBones = 0;
            var firstOfPiece = new Dictionary<string, long>();
            var keys = new List<(long, long)>(samples.Keys);
            keys.Sort();
            foreach ((long world, long attempt) key in keys)
            {
                var problems = new List<string>();
                SpatialSplitSample s = ReadOne(folder, samples[key], "the sample", problems, SpatialSplitSample.Read, v => v.sha256);
                bones.TryGetValue(key, out IndexLine boneLine);
                topologies.TryGetValue(key, out IndexLine topologyLine);
                live.TryGetValue(key, out SpatialSplitBoneResultLine l);
                SpatialSplitBoneSample b = boneLine != null ? ReadOne(folder, boneLine, "the bones", problems, SpatialSplitBoneSample.Read, v => v.sha256) : null;
                NpcPieceTopologySample t = topologyLine != null ? ReadOne(folder, topologyLine, "the topology", problems, NpcPieceTopologySample.Read, v => v.sha256) : null;
                if (boneLine == null) { problems.Add("no bones' sidecar (not a bone-aware attempt, or not written)"); noBones++; }
                if (b != null && topologyLine == null) problems.Add("no topology table");
                if (s != null)
                {
                    if (s.world != key.world || s.attempt != key.attempt || !string.Equals(s.sessionId, sessionId, StringComparison.Ordinal)) problems.Add("the sample is of another attempt or session");
                }

                if (s != null && b != null)
                {
                    if (b.world != s.world || b.attempt != s.attempt || b.fragment != s.fragment || b.epoch != s.epoch || !string.Equals(b.sessionId, s.sessionId, StringComparison.Ordinal)) problems.Add("the bones are of another attempt, fragment, epoch or session");
                    if (b.convexBones.Length != s.convexes.Length) problems.Add("the bones name " + b.convexBones.Length + " convexes, the sample has " + s.convexes.Length);
                    foreach (uint id in s.vertexIds)
                    {
                        if (NpcPiecePickSet.LocalOf(b, id) < 0) { problems.Add("the sample names vertex " + id + ", which is not of the saved lineage"); break; }
                    }
                }

                if (s != null && t != null)
                {
                    string check = t.Check();
                    if (check != null) problems.Add("the topology: " + check);
                    if (t.world != s.world || t.pick != s.attempt || t.fragment != s.fragment || t.epoch != s.epoch || !string.Equals(t.sessionId, s.sessionId, StringComparison.Ordinal)) problems.Add("the topology is of another attempt, fragment, epoch or session");
                    if (t.vertexIds.Length != s.vertexIds.Length) problems.Add("the topology has " + t.vertexIds.Length + " vertices, the sample names " + s.vertexIds.Length);
                    foreach (uint id in s.vertexIds)
                    {
                        if (t.TopologyOf(id) < 0) { problems.Add("the topology has no entry for vertex " + id); break; }
                    }
                }

                if (l != null && s != null && (l.fragment != s.fragment || l.epoch != s.epoch)) problems.Add("the live result is of fragment " + l.fragment + " epoch " + l.epoch + ", the sample of " + s.fragment + " epoch " + s.epoch);
                bool isWhole = problems.Count == 0 && s != null && b != null && t != null;
                if (isWhole) whole++; else notWhole++;

                // the saved decision beside the live line
                string savedVerdict = "", savedHash = "", agreement = "not decided";
                NpcBoneSplitRecord d = default;
                if (isWhole)
                {
                    var pick = new NpcPiecePickSet.Pick { number = (int)key.attempt, sample = s, bones = b, topology = t };
                    d = NpcPiecePickSet.Decide(pick, out byte[] sides, out byte[] _);
                    savedVerdict = d.verdict.ToString();
                    int c = b.convexBones.Length, tri = s.indices.Length / 3;
                    if (d.verdict == NpcBoneSplitVerdict.Separable) savedHash = NpcPiecePickSet.SidesHash(sides, c, tri);

                    if (l == null) { agreement = "no live line"; noLive++; }
                    else
                    {
                        compared++;
                        bool same = string.Equals(l.verdict, savedVerdict, StringComparison.Ordinal) && string.Equals(l.sidesHash ?? "", savedHash, StringComparison.Ordinal)
                            && l.clusters == d.clusters && l.groups == d.groups && l.fullGroups == d.fullGroups && l.meshComponents == d.meshComponents
                            && l.positiveConvexes == d.positiveConvexes && l.negativeConvexes == d.negativeConvexes && l.positiveTriangles == d.positiveTriangles && l.negativeTriangles == d.negativeTriangles
                            && l.connectionsKept == d.connectionsKept && l.connectionsDropped == d.connectionsDropped && l.connectionsProtected == d.connectionsProtected
                            && l.topologyVertices == d.topologyVertices && l.seamVertices == d.seamVertices;
                        agreement = same ? "agrees" : "DIFFERS";
                        if (!same) differ++;
                    }
                }

                string origin = s == null ? "" : s.splitSource != 0 ? "split-child" : s.generation <= 1 ? "first-cut" : "recut";
                string geometry = t != null ? t.geometry.ToString() : "";
                string piece = s == null ? "" : s.world + ":" + s.fragment + ":" + s.epoch + ":" + geometry;
                bool firstOne = false;
                if (isWhole && !firstOfPiece.ContainsKey(piece)) { firstOfPiece[piece] = key.attempt; firstOne = true; }
                string model = b != null ? ModelKey(b) : "";
                json.Append("{\"world\":").Append(key.world).Append(",\"attempt\":").Append(key.attempt).Append(",\"whole\":").Append(isWhole ? "true" : "false")
                    .Append(",\"problems\":").Append(SpatialSplitCaptureSink.Json(string.Join("; ", problems)))
                    .Append(",\"fragment\":").Append(s != null ? s.fragment : -1).Append(",\"epoch\":").Append(s != null ? s.epoch : -1).Append(",\"rootFragment\":").Append(s != null ? s.rootFragment : -1)
                    .Append(",\"splitSource\":").Append(s != null ? s.splitSource : -1).Append(",\"generation\":").Append(s != null ? s.generation : -1).Append(",\"issuedFrame\":").Append(s != null ? s.issuedFrame : -1)
                    .Append(",\"origin\":\"").Append(origin).Append("\",\"geometry\":").Append(SpatialSplitCaptureSink.Json(geometry)).Append(",\"piece\":").Append(SpatialSplitCaptureSink.Json(piece))
                    .Append(",\"firstOfPiece\":").Append(firstOne ? "true" : "false").Append(",\"firstAttemptOfPiece\":").Append(isWhole ? firstOfPiece[piece] : -1)
                    .Append(",\"model\":\"").Append(model).Append("\",\"convexes\":").Append(s != null ? s.convexes.Length : -1).Append(",\"triangles\":").Append(s != null ? s.indices.Length / 3 : -1)
                    .Append(",\"bones\":").Append(b != null ? b.nodeCount : -1)
                    .Append(",\"sha256\":\"").Append(s != null ? s.sha256 : "").Append("\",\"boneSha256\":\"").Append(b != null ? b.sha256 : "").Append("\",\"topologySha256\":\"").Append(t != null ? t.sha256 : "")
                    .Append("\",\"live\":");
                if (l == null) json.Append("null");
                else
                {
                    json.Append("{\"verdict\":\"").Append(l.verdict).Append("\",\"reason\":").Append(SpatialSplitCaptureSink.Json(l.reason ?? "")).Append(",\"end\":\"").Append(l.end).Append("\",\"path\":\"").Append(l.path ?? "")
                        .Append("\",\"fellBack\":").Append(l.fellBack ? "true" : "false").Append(",\"clusters\":").Append(l.clusters).Append(",\"groups\":").Append(l.groups).Append(",\"fullGroups\":").Append(l.fullGroups)
                        .Append(",\"meshComponents\":").Append(l.meshComponents).Append(",\"positiveTriangles\":").Append(l.positiveTriangles).Append(",\"negativeTriangles\":").Append(l.negativeTriangles)
                        .Append(",\"positiveConvexes\":").Append(l.positiveConvexes).Append(",\"negativeConvexes\":").Append(l.negativeConvexes)
                        .Append(",\"connectionsKept\":").Append(l.connectionsKept).Append(",\"connectionsDropped\":").Append(l.connectionsDropped).Append(",\"connectionsProtected\":").Append(l.connectionsProtected)
                        .Append(",\"sidesHash\":\"").Append(l.sidesHash ?? "").Append("\"}");
                }

                json.Append(",\"saved\":");
                if (!isWhole) json.Append("null");
                else
                {
                    json.Append("{\"verdict\":\"").Append(savedVerdict).Append("\",\"reason\":").Append(SpatialSplitCaptureSink.Json(d.reason ?? "")).Append(",\"clusters\":").Append(d.clusters).Append(",\"groups\":").Append(d.groups)
                        .Append(",\"fullGroups\":").Append(d.fullGroups).Append(",\"meshComponents\":").Append(d.meshComponents).Append(",\"positiveTriangles\":").Append(d.positiveTriangles)
                        .Append(",\"negativeTriangles\":").Append(d.negativeTriangles).Append(",\"positiveConvexes\":").Append(d.positiveConvexes).Append(",\"negativeConvexes\":").Append(d.negativeConvexes)
                        .Append(",\"connectionsKept\":").Append(d.connectionsKept).Append(",\"connectionsDropped\":").Append(d.connectionsDropped).Append(",\"connectionsProtected\":").Append(d.connectionsProtected)
                        .Append(",\"topologyVertices\":").Append(d.topologyVertices).Append(",\"seamVertices\":").Append(d.seamVertices).Append(",\"sidesHash\":\"").Append(savedHash).Append("\"}");
                }

                json.Append(",\"agreement\":\"").Append(agreement).Append("\"}\n");
                if (isWhole)
                {
                    list.Append("{\"session\":\"").Append(sessionId).Append("\",\"world\":").Append(key.world).Append(",\"attempt\":").Append(key.attempt)
                        .Append(",\"sha256\":\"").Append(s.sha256).Append("\",\"boneSha256\":\"").Append(b.sha256).Append("\",\"topologySha256\":\"").Append(t.sha256)
                        .Append("\",\"category\":\"").Append(origin).Append("\",\"fragment\":").Append(s.fragment).Append(",\"epoch\":").Append(s.epoch)
                        .Append(",\"piece\":").Append(SpatialSplitCaptureSink.Json(piece)).Append(",\"firstOfPiece\":").Append(firstOne ? "true" : "false").Append(",\"model\":\"").Append(model).Append("\"}\n");
                }
                else
                {
                    text.Append("  NOT WHOLE attempt ").Append(key.world).Append('/').Append(key.attempt).Append(": ").Append(string.Join("; ", problems)).Append('\n');
                }

                if (agreement == "DIFFERS")
                {
                    text.Append("  DIFFERS attempt ").Append(key.world).Append('/').Append(key.attempt).Append(": live ").Append(l.verdict).Append(' ').Append(l.positiveTriangles).Append(':').Append(l.negativeTriangles)
                        .Append(" groups ").Append(l.groups).Append('/').Append(l.fullGroups).Append(" connections ").Append(l.connectionsKept).Append('/').Append(l.connectionsDropped).Append('/').Append(l.connectionsProtected)
                        .Append(", saved ").Append(savedVerdict).Append(' ').Append(d.positiveTriangles).Append(':').Append(d.negativeTriangles).Append(" groups ").Append(d.groups).Append('/').Append(d.fullGroups)
                        .Append(" connections ").Append(d.connectionsKept).Append('/').Append(d.connectionsDropped).Append('/').Append(d.connectionsProtected).Append('\n');
                }
            }

            foreach (string g in general) text.Append("  PROBLEM ").Append(g).Append('\n');
            text.Append("attempts listed ").Append(samples.Count).Append(": whole ").Append(whole).Append(", not whole ").Append(notWhole).Append(" (of them without a bones' sidecar ").Append(noBones)
                .Append("); pieces among the whole (world, fragment, epoch, geometry) ").Append(firstOfPiece.Count).Append('\n');
            text.Append("saved decisions against the live lines: compared ").Append(compared).Append(", differ ").Append(differ).Append(", no live line ").Append(noLive).Append('\n');
            bool counts = summary == null || (summary.saved == samples.Count && summary.lost == 0 && summary.unindexed == 0 && summary.inFlight == 0);
            if (!counts) text.Append("  PROBLEM the session says saved ").Append(summary.saved).Append(", lost ").Append(summary.lost).Append(", without an index line ").Append(summary.unindexed).Append(", in flight ")
                .Append(summary.inFlight).Append("; the samples' index lists ").Append(samples.Count).Append('\n');
            File.WriteAllText(jsonPath, json.ToString(), new UTF8Encoding(false));
            File.WriteAllText(listPath, list.ToString(), new UTF8Encoding(false));
            code = notWhole == 0 && differ == 0 && general.Count == 0 && counts && orphanBones == 0 && orphanTopologies == 0 && orphanResults == 0 ? 0 : 1;
            text.Append(code == 0 ? "VERIFY: PASS" : "VERIFY: NOT ALL (not whole " + notWhole + ", differ " + differ + ", problems " + general.Count + ")").Append('\n');
            return text.ToString();
        }

        private static string ValueOf(string[] arguments, string name)
        {
            int at = Array.FindIndex(arguments, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
        }
    }
}
