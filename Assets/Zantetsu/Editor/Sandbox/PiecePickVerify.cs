using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// NPC-PIECE-PICK-1: reads a piece collection's folder back (<see cref="NpcPiecePickSet"/>) and writes one report: every pick whole or
    /// not and why not, and -- for each whole pick -- the bone-aware decision run over the saved parts beside the split's note the pick
    /// carried (the live decision over the same geometry where the note is of it). A check of what was collected, nothing of the game.
    /// Batch: <c>-executeMethod Zantetsu.EditorTools.Sandbox.PiecePickVerify.Run -zantetsuPiecePickFolder &lt;folder&gt; -zantetsuPiecePickReport &lt;file&gt;</c>.
    /// </summary>
    public static class PiecePickVerify
    {
        public const string FolderArgument = "-zantetsuPiecePickFolder", ReportArgument = "-zantetsuPiecePickReport";

        /// <summary>NPC-BONE-SUPPORT-1: a JSON line a pick (the saved decision's counts and placement) beside the report, for the comparison with an independent analysis.</summary>
        public const string JsonArgument = "-zantetsuPiecePickJson";

        /// <summary>
        /// NPC-BONE-TOPOLOGY-1: a folder of <c>pick-&lt;n&gt;.json</c> files ({topologyCount, ids: one topology id a vertex of the pick's
        /// saved lineage}), worked out apart from what a pick holds (a pick holds no topology). With it each pick is decided with its
        /// topology, and the JSON line carries every triangle's side; a pick with no file there is decided as before (each render vertex
        /// its own topology vertex) and its line says so.
        /// </summary>
        public const string TopologyArgument = "-zantetsuPiecePickTopology";

        [Serializable]
        private sealed class PickTopology
        {
            public int pick, topologyCount;
            public bool exact;
            public int[] ids;
        }

        public static void Run()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            string folder = ValueOf(arguments, FolderArgument), report = ValueOf(arguments, ReportArgument), json = ValueOf(arguments, JsonArgument);
            s_topologyFolder = ValueOf(arguments, TopologyArgument);
            if (folder == null || report == null || !Directory.Exists(folder))
            {
                Debug.LogError("PIECE PICK VERIFY: " + FolderArgument + " <an existing folder> and " + ReportArgument + " <a file> are needed");
                EditorApplication.Exit(2);
                return;
            }

            string text;
            int code;
            try
            {
                text = Verify(folder, out code, json);
            }
            catch (Exception e)
            {
                text = "PIECE PICK VERIFY: threw " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace;
                code = 2;
            }

            File.WriteAllText(report, text, new UTF8Encoding(false));
            Debug.Log(text);
            EditorApplication.Exit(code);
        }

        /// <summary>The report of <paramref name="folder"/>; 0 where every pick that did not fail at the time is whole and its saved decision agrees with its note of this geometry, 1 otherwise.</summary>
        public static string Verify(string folder, out int code) => Verify(folder, out code, null);

        private static string s_topologyFolder;

        public static string Verify(string folder, out int code, string jsonPath)
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            List<NpcPiecePickSet.Pick> picks = NpcPiecePickSet.Read(folder);
            var text = new StringBuilder();
            var json = new StringBuilder();
            text.Append("PIECE PICK VERIFY: ").Append(Path.GetFileName(folder)).Append(": ").Append(NpcPiecePickSet.Describe(picks)).Append('\n');
            int disagree = 0, compared = 0, notOfThisGeometry = 0, noNote = 0;
            var verdicts = new Dictionary<string, int>();
            foreach (NpcPiecePickSet.Pick pick in picks)
            {
                if (!pick.IsWhole) continue;
                NpcPiecePickSample r = pick.record;
                PickTopology topology = null;
                if (s_topologyFolder != null)
                {
                    string file = Path.Combine(s_topologyFolder, "pick-" + r.pick.ToString(inv) + ".json");
                    if (File.Exists(file)) topology = JsonUtility.FromJson<PickTopology>(File.ReadAllText(file));
                }

                NpcBoneSplitRecord d = topology != null && topology.ids != null
                    ? NpcPiecePickSet.Decide(pick, topology.ids, topology.topologyCount, out byte[] sides, out byte[] boneSides)
                    : NpcPiecePickSet.Decide(pick, out sides, out boneSides);
                string key = d.verdict + (d.verdict == NpcBoneSplitVerdict.Separable ? "" : " (" + d.reason + ")");
                if (jsonPath != null)
                {
                    // The saved decision as data: its counts, the convexes on the positive side (indices, and their bones), the triangles' and bones' sides.
                    int convexes = pick.bones.convexBones.Length;
                    var positiveConvexes = new List<int>();
                    var positiveBones = new List<int>();
                    for (int i = 0; i < convexes; i++)
                    {
                        if (sides[i] == 1) { positiveConvexes.Add(i); positiveBones.Add(pick.bones.convexBones[i]); }
                    }

                    var bonesPositive = new List<int>();
                    var bonesNegative = new List<int>();
                    for (int k = 0; k < pick.bones.nodeCount; k++)
                    {
                        if (boneSides[k] == 1) bonesPositive.Add(k); else if (boneSides[k] == 0) bonesNegative.Add(k);
                    }

                    json.Append("{\"pick\":").Append(r.pick).Append(",\"fragment\":").Append(r.fragment).Append(",\"model\":").Append(SpatialSplitCaptureSink.Json(r.modelName))
                        .Append(",\"verdict\":").Append(SpatialSplitCaptureSink.Json(d.verdict.ToString())).Append(",\"reason\":").Append(SpatialSplitCaptureSink.Json(d.reason))
                        .Append(",\"separable\":").Append(d.verdict == NpcBoneSplitVerdict.Separable ? "true" : "false")
                        .Append(",\"clusters\":").Append(d.clusters).Append(",\"meshComponents\":").Append(d.meshComponents).Append(",\"groups\":").Append(d.groups)
                        .Append(",\"fullGroups\":").Append(d.fullGroups).Append(",\"unboundGroups\":").Append(d.unboundGroups).Append(",\"pieceBones\":").Append(d.pieceBones)
                        .Append(",\"referencedVertices\":").Append(d.referencedVertices).Append(",\"originSteps\":").Append(d.originSteps).Append(",\"removedWeights\":").Append(d.removedWeights)
                        .Append(",\"connectionsKept\":").Append(d.connectionsKept).Append(",\"connectionsDropped\":").Append(d.connectionsDropped)
                        .Append(",\"positiveConvexes\":").Append(d.positiveConvexes).Append(",\"negativeConvexes\":").Append(d.negativeConvexes)
                        .Append(",\"positiveTriangles\":").Append(d.positiveTriangles).Append(",\"negativeTriangles\":").Append(d.negativeTriangles)
                        .Append(",\"positiveConvexIndices\":[").Append(string.Join(",", positiveConvexes)).Append("],\"positiveConvexBones\":[").Append(string.Join(",", positiveBones))
                        .Append("],\"bonesPositive\":[").Append(string.Join(",", bonesPositive)).Append("],\"bonesNegative\":[").Append(string.Join(",", bonesNegative))
                        .Append("],\"noteVerdict\":").Append(SpatialSplitCaptureSink.Json(r.noteBoneVerdict)).Append(",\"noteGroups\":").Append(r.noteGroups).Append(",\"noteFullGroups\":").Append(r.noteFullGroups)
                        .Append(",\"nativeBytes\":").Append(d.nativeBytes)
                        .Append(",\"topology\":").Append(SpatialSplitCaptureSink.Json(topology == null ? "none (each render vertex its own)" : topology.exact ? "given, exact" : "given, not exact"))
                        .Append(",\"topologyVertices\":").Append(d.topologyVertices).Append(",\"seamVertices\":").Append(d.seamVertices)
                        .Append(",\"triangleSides\":\"");
                    for (int i = convexes; i < sides.Length; i++) json.Append(d.verdict == NpcBoneSplitVerdict.Separable ? (char)('0' + sides[i]) : '-');
                    json.Append("\"}\n");
                }
                verdicts[key] = verdicts.TryGetValue(key, out int n) ? n + 1 : 1;
                string agreement;
                if (!r.hasNote || !r.noteAttempted) { agreement = "no attempt noted"; noNote++; }
                else if (!r.noteIsOfThisGeometry) { agreement = "the note is of an earlier geometry (not compared)"; notOfThisGeometry++; }
                else
                {
                    compared++;
                    bool same = d.verdict.ToString() == r.noteBoneVerdict && d.clusters == r.noteClusters && d.groups == r.noteGroups && d.fullGroups == r.noteFullGroups
                        && d.meshComponents == r.noteMeshComponents && d.pieceBones == r.notePieceBones && d.referencedVertices == r.noteReferencedVertices && d.originSteps == r.noteOriginSteps
                        && d.removedWeights == r.noteRemovedWeights && d.unboundGroups == r.noteUnboundGroups;
                    if (!same) disagree++;
                    agreement = same ? "SAME as the live note" : "DIFFERS from the live note (" + r.noteBoneVerdict + ", clusters " + r.noteClusters + ", groups " + r.noteGroups + "/" + r.noteFullGroups
                        + ", components " + r.noteMeshComponents + ", held " + r.notePieceBones + ", referenced " + r.noteReferencedVertices + ", steps " + r.noteOriginSteps + ", removed " + r.noteRemovedWeights + ")";
                }

                text.Append("pick #").Append(r.pick).Append(": fragment ").Append(r.fragment).Append(" (root ").Append(r.rootFragment).Append(", generation ").Append(r.generation).Append(") of ")
                    .Append(r.modelName).Append("; convexes ").Append(r.convexCount).Append(", triangles ").Append(r.indexCount / 3).Append("; hit in the geometry (")
                    .Append(r.hitGeometry.x.ToString("F3", inv)).Append(", ").Append(r.hitGeometry.y.ToString("F3", inv)).Append(", ").Append(r.hitGeometry.z.ToString("F3", inv))
                    .Append("); saved decision ").Append(d.verdict).Append(" (").Append(d.reason).Append("): held ").Append(d.pieceBones).Append(", clusters ").Append(d.clusters)
                    .Append(", components ").Append(d.meshComponents).Append(", groups ").Append(d.groups).Append(" (").Append(d.fullGroups).Append(" full, ").Append(d.unboundGroups).Append(" mesh alone), removed weights ")
                    .Append(d.removedWeights).Append(", referenced ").Append(d.referencedVertices).Append(" of ").Append(d.lineageVertices).Append("; ").Append(agreement)
                    .Append("; the split's look ").Append(r.examine).Append(r.noteFellBack ? "; fell back, ended " + r.noteEnd : "").Append(r.noteMadeBy.Length > 0 ? "; made by " + r.noteMadeBy : "")
                    .Append("; weights kept ").Append(r.weightsKept ? "yes" : "NO").Append('\n');
            }

            text.Append("saved decisions:");
            foreach (KeyValuePair<string, int> v in verdicts) text.Append(' ').Append(v.Key).Append(' ').Append(v.Value).Append(';');
            text.Append(" compared with the live note ").Append(compared).Append(" (differ ").Append(disagree).Append("), note of an earlier geometry ").Append(notOfThisGeometry)
                .Append(", no attempt noted ").Append(noNote).Append('\n');
            int broken = 0;
            foreach (NpcPiecePickSet.Pick p in picks) if (p.failure == null && !p.IsWhole) broken++;
            if (jsonPath != null) File.WriteAllText(jsonPath, json.ToString(), new UTF8Encoding(false));
            code = broken == 0 && disagree == 0 ? 0 : 1;
            text.Append(code == 0 ? "VERIFY: PASS" : "VERIFY: FAIL (not whole " + broken + ", differ " + disagree + ")").Append('\n');
            return text.ToString();
        }

        private static string ValueOf(string[] arguments, string name)
        {
            int at = Array.FindIndex(arguments, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
        }
    }
}
