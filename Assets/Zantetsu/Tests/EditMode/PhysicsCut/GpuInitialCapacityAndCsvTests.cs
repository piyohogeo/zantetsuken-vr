using System.Collections.Generic;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The GPU copy's first capacities of 256 MiB each (TL, 2026-10-01): the code's defaults and the shared profile agree
    /// on 16,777,216 vertices of 16 bytes and 67,108,864 indices of 4 bytes, 512 MiB in all, within the CPU reservations
    /// (the profile is usable); and the check's CSV field keeps a row's columns whatever its text holds.
    /// </summary>
    public class GpuInitialCapacityAndCsvTests
    {
        private const int Vertices = 16777216, Indices = 67108864;

        [Test]
        public void TheDefaultsAndTheSharedProfile_Ask256MiBEach_WithinTheReservations()
        {
            var made = ScriptableObject.CreateInstance<CutWorldProfile>();
            var shared = AssetDatabase.LoadAssetAtPath<CutWorldProfile>("Assets/Zantetsu/Settings/CutWorldSandboxProfile.asset");
            try
            {
                Assert.That(shared, Is.Not.Null, "the shared profile");
                foreach ((string what, CutWorldProfile p) in new[] { ("the code's defaults", made), ("the shared profile", shared) })
                {
                    Assert.That(p.GpuVertexInitialCapacity, Is.EqualTo(Vertices), what + ": vertices");
                    Assert.That(p.GpuIndexInitialCapacity, Is.EqualTo(Indices), what + ": indices");
                    Assert.That(p.IsUsable(out string reason), Is.True, what + ": usable (" + reason + ")");
                }

                long vertexBytes = (long)Vertices * VpRenderVertex.Stride, indexBytes = (long)Indices * VpGpuIndexedGeometryBuffers.IndexStride;
                TestContext.Out.WriteLine("vertex stride " + VpRenderVertex.Stride + " B, " + vertexBytes + " B; index stride " + VpGpuIndexedGeometryBuffers.IndexStride + " B, " + indexBytes + " B; in all " + (vertexBytes + indexBytes) + " B");
                Assert.That(VpRenderVertex.Stride, Is.EqualTo(16), "the vertex is 16 bytes in this build");
                Assert.That(vertexBytes, Is.EqualTo(256L << 20));
                Assert.That(indexBytes, Is.EqualTo(256L << 20));
            }
            finally
            {
                Object.DestroyImmediate(made);
            }
        }

        // RFC 4180, for the test only: a line split into its fields.
        private static List<string> Parse(string line)
        {
            var fields = new List<string>();
            var field = new StringBuilder();
            bool quoted = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (quoted)
                {
                    if (c == '"' && i + 1 < line.Length && line[i + 1] == '"') { field.Append('"'); i++; }
                    else if (c == '"') quoted = false;
                    else field.Append(c);
                }
                else if (c == '"') quoted = true;
                else if (c == ',') { fields.Add(field.ToString()); field.Clear(); }
                else field.Append(c);
            }

            fields.Add(field.ToString());
            return fields;
        }

        [Test]
        public void ACsvField_KeepsTheColumns_WithCommasQuotesAndLineBreaks()
        {
            var values = new[] { "committed", "no body shown, both sides empty", "a \"quoted\" word", "two\nlines", "carriage\r\nreturn", "", "plain" };
            string line = string.Join(",", System.Array.ConvertAll(values, SandboxPropSlashPlayerCheck.CsvField));
            List<string> back = Parse(line);
            Assert.That(back, Is.EqualTo(new List<string>(values)), "each field read back whole");
            Assert.That(SandboxPropSlashPlayerCheck.CsvField("committed"), Is.EqualTo("committed"), "a plain field is left as it is");
            Assert.That(SandboxPropSlashPlayerCheck.CsvField(null), Is.EqualTo(""));
        }
    }
}
