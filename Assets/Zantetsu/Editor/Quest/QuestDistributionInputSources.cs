using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.EditorTools.Quest
{
    /// <summary>
    /// The placed cuttables' inputs as a distribution carries them (unit 11, TL 2026-10-18): the licensed inputs under
    /// Assets/Licensed hold, in their <c>source</c> field, the absolute path of the file they were made from, which names
    /// the machine's folders. For the distribution build alone, Tools/Quest3s/Build-Distribution.ps1 writes that one value
    /// empty in the build tree's copies (the originals moved aside, and put back after the build whatever happened), and
    /// this checks what Unity holds: each input imported again from disk (a synchronous forced import), its TextAsset's
    /// bytes hashing to the file the wrapper wrote; with the blank files, also the input as the runtime reads it
    /// (<see cref="JsonUtility"/> into <see cref="PlacedCuttableInput"/>) equal to the original's but for the source, which
    /// is empty. After the originals are back the same check, asked for "original", shows the import is the original's
    /// again. <c>source</c> is carried and copied at run time but read by nothing (PlayableCityCuttable.Scaled copies it).
    ///
    /// The manifest the wrapper writes: one line an input, tab-separated -- the asset path, the SHA-256 of the blank
    /// file, the SHA-256 of the original, and the original's copy relative to the aside folder. No content of an input.
    /// </summary>
    public static class QuestDistributionInputSources
    {
        public const string ManifestArgument = "-zantetsuDistInputSources";
        public const string AsideArgument = "-zantetsuDistInputSourcesAside";
        public const string ExpectArgument = "-zantetsuDistInputSourcesExpect";
        public const string ExpectBlank = "blank", ExpectOriginal = "original";
        /// <summary>The exit code of a failed check run from the command line.</summary>
        public const int FailedExitCode = 3;

        public sealed class Entry
        {
            public string assetPath, blankSha256, originalSha256, originalCopy;
        }

        /// <summary>The manifest's entries, or the failure (null: read).</summary>
        public static string ReadManifest(string path, out List<Entry> entries)
        {
            entries = new List<Entry>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "the input-source manifest is not there: " + path;
            foreach (string line in File.ReadAllLines(path))
            {
                if (line.Length == 0) continue;
                string[] f = line.Split('\t');
                if (f.Length != 4 || !f[0].StartsWith("Assets/Licensed/", StringComparison.Ordinal) || !f[0].EndsWith(".json", StringComparison.Ordinal)
                    || f[1].Length != 64 || f[2].Length != 64 || f[3].Length == 0)
                {
                    return "a malformed line in the input-source manifest (" + (entries.Count + 1) + ")";
                }

                entries.Add(new Entry { assetPath = f[0], blankSha256 = f[1], originalSha256 = f[2], originalCopy = f[3] });
            }

            return entries.Count == 0 ? "the input-source manifest names no input" : null;
        }

        /// <summary>
        /// Imports every input from disk and checks Unity's result against <paramref name="expect"/> ("blank" or
        /// "original"). The failure (null: every input as expected); <paramref name="state"/> for the log.
        /// </summary>
        public static string Check(List<Entry> entries, string expect, string aside, out string state)
        {
            state = "input sources: " + entries.Count + " inputs, expected " + expect;
            if (expect != ExpectBlank && expect != ExpectOriginal) return "the expected state must be " + ExpectBlank + " or " + ExpectOriginal + ", not '" + expect + "'";
            if (expect == ExpectBlank && (string.IsNullOrEmpty(aside) || !Directory.Exists(aside))) return "the originals' folder is not there";
            int compared = 0;
            foreach (Entry e in entries)
            {
                AssetDatabase.ImportAsset(e.assetPath, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
                var asset = AssetDatabase.LoadAssetAtPath<TextAsset>(e.assetPath);
                if (asset == null) return "Unity holds no TextAsset at " + e.assetPath;
                string held = Sha256(asset.bytes);
                string wanted = expect == ExpectBlank ? e.blankSha256 : e.originalSha256;
                if (!string.Equals(held, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    return "Unity's import of " + e.assetPath + " is not the " + expect + " file (its bytes hash to " + held.Substring(0, 12) + "..., not " + wanted.Substring(0, 12) + "...)";
                }

                if (expect == ExpectBlank)
                {
                    string originalPath = Path.Combine(aside, e.originalCopy);
                    if (!File.Exists(originalPath)) return "the original of " + e.assetPath + " is not in the originals' folder";
                    PlacedCuttableInput blank = JsonUtility.FromJson<PlacedCuttableInput>(asset.text);
                    PlacedCuttableInput original = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(originalPath));
                    if (blank == null || original == null) return "the input " + e.assetPath + " could not be read as the runtime reads it";
                    if (blank.source != "") return "the input " + e.assetPath + " as read still has a source";
                    if (string.IsNullOrEmpty(original.source)) return "the original of " + e.assetPath + " has no source to blank";
                    original.source = "";
                    if (JsonUtility.ToJson(original) != JsonUtility.ToJson(blank)) return "the input " + e.assetPath + " as read differs from its original in more than its source";
                    compared++;
                }
            }

            state += expect == ExpectBlank ? "; every import is the blank file, read as the original but for an empty source (" + compared + ")" : "; every import is the original file again";
            return null;
        }

        /// <summary>The Unity entry of the check after the originals are put back (and of a check of the blank state).</summary>
        public static void CheckFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();
            string manifest = Value(args, ManifestArgument), expect = Value(args, ExpectArgument), aside = Value(args, AsideArgument);
            string failure = ReadManifest(manifest, out List<Entry> entries);
            string state = null;
            if (failure == null) failure = Check(entries, expect, aside, out state);
            if (failure != null)
            {
                Debug.LogError("QUEST DISTRIBUTION INPUT SOURCES check failed: " + failure);
                if (Application.isBatchMode) EditorApplication.Exit(FailedExitCode);
                return;
            }

            Debug.Log("QUEST DISTRIBUTION INPUT SOURCES: " + state);
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>The value after <paramref name="name"/>, or null.</summary>
        public static string Value(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        public static string Sha256(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}
