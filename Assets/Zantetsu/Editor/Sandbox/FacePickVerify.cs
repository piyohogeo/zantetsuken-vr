using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Zantetsu.PhysicsCut;

namespace Zantetsu.EditorTools.Sandbox
{
    /// <summary>
    /// FACE-PICK-1: reads a face collection's folder back (<see cref="FacePickSet"/>) and writes one report: every pick read whole or not
    /// and why not (the record against its index line's hash, the pictures against theirs), every candidate worked out again from the saved
    /// parts alone, and each pick's own description. A check of what was collected, nothing of the game.
    /// Batch: <c>-executeMethod Zantetsu.EditorTools.Sandbox.FacePickVerify.Run -zantetsuFacePickFolder &lt;folder&gt; -zantetsuFacePickReport &lt;file&gt;</c>.
    /// </summary>
    public static class FacePickVerify
    {
        public const string FolderArgument = "-zantetsuFacePickFolder", ReportArgument = "-zantetsuFacePickReport";

        public static void Run()
        {
            string[] arguments = Environment.GetCommandLineArgs();
            string folder = ValueOf(arguments, FolderArgument), report = ValueOf(arguments, ReportArgument);
            if (folder == null || report == null || !Directory.Exists(folder))
            {
                Debug.LogError("FACE PICK VERIFY: " + FolderArgument + " <an existing folder> and " + ReportArgument + " <a file> are needed");
                EditorApplication.Exit(2);
                return;
            }

            string text;
            int code;
            try
            {
                text = Verify(folder, out code);
            }
            catch (Exception e)
            {
                text = "FACE PICK VERIFY: threw " + e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace;
                code = 2;
            }

            File.WriteAllText(report, text, new UTF8Encoding(false));
            Debug.Log(text);
            EditorApplication.Exit(code);
        }

        /// <summary>The report of <paramref name="folder"/>; 0 where every pick taken at the time reads whole with both pictures and comes out again as saved, 1 otherwise.</summary>
        public static string Verify(string folder, out int code)
        {
            List<FacePickSet.Pick> picks = FacePickSet.Read(folder);
            var text = new StringBuilder();
            text.Append("FACE PICK VERIFY: ").Append(Path.GetFileName(folder)).Append(": ").Append(FacePickSet.Describe(picks)).Append('\n');
            int bad = 0;
            foreach (FacePickSet.Pick pick in picks)
            {
                if (pick.sample == null && pick.failure != null) continue;   // not taken at the time: said in its line
                if (pick.sample == null || pick.problems.Count > 0 || pick.imageBefore == null || pick.imageAfter == null) bad++;
            }

            text.Append(bad == 0 ? "PASS: every pick taken reads whole, with both pictures, and comes out again as saved" : "FAIL: " + bad + " picks are not whole").Append('\n');
            code = bad == 0 ? 0 : 1;
            return text.ToString();
        }

        private static string ValueOf(string[] arguments, string name)
        {
            int at = Array.FindIndex(arguments, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
            return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
        }
    }
}
