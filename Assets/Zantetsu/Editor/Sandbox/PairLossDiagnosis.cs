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
    /// NPC-PAIR-EVAL-2 review 2: the pairwise combination decided again over the captured attempts of a fixed list, in the Editor,
    /// one JSON line an input (<see cref="SpatialSplitMethodEval.DiagnosePair"/>): for every input the bones' reading, the
    /// combination's verdict and the partition's hash (to be held against the evaluation Player's run); for the inputs named, every
    /// node's attributes and every pair's state. A diagnosis of saved inputs, nothing of the game and no measurement.
    /// Batch: <c>-executeMethod Zantetsu.EditorTools.Sandbox.PairLossDiagnosis.Run -zantetsuPairDiagFolder &lt;session&gt; -zantetsuPairDiagList
    /// &lt;list&gt; -zantetsuPairDiagOut &lt;file&gt; [-zantetsuPairDiagMode 1|2] [-zantetsuPairDiagDetail i,j,k (list indices)]</c>.
    /// </summary>
    public static class PairLossDiagnosis
    {
        [Serializable]
        private sealed class ListLine
        {
            public long world, attempt;
        }

        public static void Run()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            string folder = ValueOf(arguments, "-zantetsuPairDiagFolder"), list = ValueOf(arguments, "-zantetsuPairDiagList"), output = ValueOf(arguments, "-zantetsuPairDiagOut");
            string modeText = ValueOf(arguments, "-zantetsuPairDiagMode"), detailText = ValueOf(arguments, "-zantetsuPairDiagDetail");
            if (folder == null || list == null || output == null || !Directory.Exists(folder) || !File.Exists(list) || File.Exists(output))
            {
                Debug.LogError("PAIR LOSS DIAGNOSIS: an existing session folder, its list and a new output file are needed");
                EditorApplication.Exit(2);
                return;
            }

            int code = 0;
            try
            {
                CultureInfo inv = CultureInfo.InvariantCulture;
                int mode = modeText != null ? int.Parse(modeText, inv) : 2;
                var detail = new HashSet<int>();
                if (detailText != null) foreach (string part in detailText.Split(',')) if (part.Trim().Length > 0) detail.Add(int.Parse(part.Trim(), inv));
                if (!SpatialSplitGraph.IsPrepared) SpatialSplitGraph.Prepare();
                var text = new StringBuilder();
                int index = 0, done = 0;
                foreach (string raw in File.ReadAllLines(list))
                {
                    string t = raw.Trim().TrimStart('﻿');
                    if (t.Length == 0) continue;
                    ListLine l = JsonUtility.FromJson<ListLine>(t);
                    SpatialSplitSample s = SpatialSplitSample.Read(Path.Combine(folder, SpatialSplitCaptureSink.SampleFileName(l.world, l.attempt)));
                    SpatialSplitBoneSample b = SpatialSplitBoneSample.Read(Path.Combine(folder, SpatialSplitBoneSample.FileName(l.world, l.attempt)));
                    NpcPieceTopologySample table = NpcPieceTopologySample.Read(Path.Combine(folder, NpcPieceTopologySample.FileName(l.world, l.attempt)));
                    SpatialSplitPairDiagnosis d = SpatialSplitMethodEval.DiagnosePair(s, b, table, mode, detail.Contains(index));
                    text.Append("{\"index\":").Append(index).Append(",\"attempt\":").Append(l.attempt).Append(",\"mode\":").Append(mode).Append(",\"diagnosis\":").Append(d.Json(detail.Contains(index))).Append("}\n");
                    index++;
                    done++;
                    if (text.Length > (1 << 20)) { File.AppendAllText(output, text.ToString(), new UTF8Encoding(false)); text.Clear(); }
                }

                File.AppendAllText(output, text.ToString(), new UTF8Encoding(false));
                Debug.Log("PAIR LOSS DIAGNOSIS: " + done + " inputs, mode " + mode + ", detail of " + detail.Count + "; live native areas of the engine " + SpatialSplitGraph.LiveNativeAllocations);
            }
            catch (Exception e)
            {
                Debug.LogError("PAIR LOSS DIAGNOSIS: threw " + e);
                code = 2;
            }

            EditorApplication.Exit(code);
        }

        private static string ValueOf(string[] arguments, string name)
        {
            int at = Array.FindIndex(arguments, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
        }
    }
}
