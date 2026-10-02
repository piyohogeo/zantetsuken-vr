using System;
using System.IO;
using System.Text;
using NUnit.Framework;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// Where a diagnosis writes (2026-10-02): only into a directory its run names explicitly in an environment variable,
    /// apart from its inputs, and never over an existing file. Without the variable the diagnosis does not write
    /// (<see cref="Optional"/>) or is not run (<see cref="Require"/>).
    /// </summary>
    internal static class DiagnosisOutput
    {
        /// <summary>The run's output directory from <paramref name="variable"/>; the case is not run without it.</summary>
        internal static string Require(string variable)
        {
            string dir = Optional(variable);
            if (dir == null) Assert.Ignore(variable + " is not set: name this run's own output directory (inputs are only read)");
            return dir;
        }

        /// <summary>The run's output directory from <paramref name="variable"/>, or null when none is named.</summary>
        internal static string Optional(string variable)
        {
            string dir = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(dir)) return null;
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>A new file of lines (each ended by a newline); an existing one is refused (IOException), never replaced.</summary>
        internal static void WriteNewLines(string path, System.Collections.Generic.IEnumerable<string> lines)
        {
            var b = new StringBuilder();
            foreach (string line in lines) b.Append(line).Append('\n');
            WriteNew(path, b.ToString());
        }

        /// <summary>A new file; an existing one is refused (IOException), never replaced.</summary>
        internal static void WriteNew(string path, string text)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(text);
            }
        }
    }
}
