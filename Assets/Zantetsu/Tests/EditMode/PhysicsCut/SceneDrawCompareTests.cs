using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The scene draw comparison (TL, 2026-10-07), what holds without a world: the camera path as a function of time,
    /// the run's arguments, and the geometry made beforehand -- converted once by the product's mesh conversion,
    /// written, read back as it was written, and refused when it is not what it says.
    /// </summary>
    public class SceneDrawCompareTests
    {
        private const string Path0 = "# a path\nstart,10,1.6,-5,90\nhold,2\ngoto,14,-5,4\nface,0,3\nturn,360,6\n";

        [Test]
        public void Path_IsAFunctionOfItsOwnTime_StandingMovingAndTurning()
        {
            Assert.That(SceneDrawComparePath.TryParse(Path0, out SceneDrawComparePath path, out string failure), Is.True, failure);
            Assert.That(path.SegmentCount, Is.EqualTo(4));
            Assert.That(path.Seconds, Is.EqualTo(15.0).Within(1e-9));
            Assert.That(path.Metres, Is.EqualTo(4.0).Within(1e-5));
            Assert.That(path.Degrees, Is.EqualTo(450.0).Within(1e-3), "90 by the shorter way, then a whole turn");

            SceneDrawComparePath.Pose start = path.At(0);
            Assert.That(start.eye, Is.EqualTo(new Vector3(10f, 1.6f, -5f)));
            Assert.That(start.yaw, Is.EqualTo(90f));
            Assert.That(path.At(-3).eye, Is.EqualTo(start.eye), "before its time: its start");
            Assert.That(path.At(1.9).eye, Is.EqualTo(start.eye), "standing");
            Assert.That(path.At(1.9).segment, Is.EqualTo(0));

            SceneDrawComparePath.Pose half = path.At(4.0);
            Assert.That(half.segment, Is.EqualTo(1));
            Assert.That(half.eye.x, Is.EqualTo(12f).Within(1e-4f), "half of the way at half of the time");
            Assert.That(half.eye.y, Is.EqualTo(1.6f), "the height is kept");
            Assert.That(half.yaw, Is.EqualTo(90f), "the heading is kept while moving");

            Assert.That(path.At(7.5).yaw, Is.EqualTo(45f).Within(1e-3f), "from 90 to 0 by the shorter way");
            Assert.That(path.At(9.0).yaw, Is.EqualTo(0f).Within(1e-3f));
            Assert.That(path.At(12.0).yaw, Is.EqualTo(180f).Within(1e-3f), "half of the whole turn");
            SceneDrawComparePath.Pose end = path.At(15.0);
            Assert.That(end.eye, Is.EqualTo(new Vector3(14f, 1.6f, -5f)));
            Assert.That(Mathf.DeltaAngle(end.yaw, 0f), Is.EqualTo(0f).Within(1e-3f), "a whole turn ends where it began");
            Assert.That(path.At(99).eye, Is.EqualTo(end.eye), "after its time: its end");

            // The same time, the same pose: nothing but the time enters.
            Assert.That(path.At(4.0).eye, Is.EqualTo(half.eye));
            Assert.That(path.KindOf(0), Is.EqualTo(SceneDrawComparePath.Kind.Hold));
            Assert.That(path.BeginOf(2), Is.EqualTo(6.0).Within(1e-9));
            Assert.That(path.SecondsOf(3), Is.EqualTo(6.0).Within(1e-9));
            StringAssert.Contains("4 segments", path.Describe());
        }

        [TestCase("hold,1\n", "begins with 'start")]
        [TestCase("start,0,0,0,0\n", "no segment")]
        [TestCase("start,0,0,0,0\nstart,1,1,1,1\nhold,1\n", "once")]
        [TestCase("start,0,0,0,0\nhold,0\n", "more than no time")]
        [TestCase("start,0,0,0,0\nwalk,1,2,3\n", "not 'hold")]
        [TestCase("start,0,0,0,0\ngoto,1,x,3\n", "not a number")]
        [TestCase("start,0,0,0,0\nhold,NaN\n", "not a number")]
        [TestCase("", "no start")]
        public void Path_ThatIsNotOne_IsRefusedWithItsLine(string text, string expected)
        {
            Assert.That(SceneDrawComparePath.TryParse(text, out SceneDrawComparePath path, out string failure), Is.False);
            Assert.That(path, Is.Null);
            StringAssert.Contains(expected, failure);
        }

        [Test]
        public void Arguments_NotAskedFor_StartNothing_AndWhatIsAskedForIsReadOnce()
        {
            Assert.That(SandboxDrawComparePlayerCheck.TryRead(new[] { "Player.exe", "-zantetsuPropSlash", "C:/run" }, out var none, out string failure), Is.False);
            Assert.That(none, Is.Null);
            Assert.That(failure, Is.Null, "not asked for is not a failure");

            string[] asked = { "Player.exe", "-zantetsuDrawCompare", "C:/out", "-zantetsuDrawCompareMode", "VP3C", "-zantetsuDrawComparePath", "path.txt", "-zantetsuDrawCompareGeometry", "g.vpdc",
                "-zantetsuDrawCompareProfiler", "capture.raw", "-zantetsuDrawCompareWarmupLaps", "2", "-zantetsuDisableNumericDiagnostics" };
            Assert.That(SandboxDrawComparePlayerCheck.TryRead(asked, out var settings, out failure), Is.True, failure);
            Assert.That(settings.mode, Is.EqualTo(SandboxDrawComparePlayerCheck.Mode.Vp3c));
            Assert.That(settings.directory, Is.EqualTo("C:/out"));
            Assert.That(settings.pathFile, Is.EqualTo("path.txt"));
            Assert.That(settings.geometryFile, Is.EqualTo("g.vpdc"));
            Assert.That(settings.profilerFile, Is.EqualTo("capture.raw"));
            Assert.That(settings.warmupLaps, Is.EqualTo(2));
            Assert.That(settings.allowRefused, Is.False);
            Assert.That(settings.shots, Is.False);

            string[] unity = { "Player.exe", "-zantetsuDrawCompare", "C:/out", "-zantetsuDrawCompareMode", "unity", "-zantetsuDrawComparePath", "path.txt" };
            Assert.That(SandboxDrawComparePlayerCheck.TryRead(unity, out settings, out failure), Is.True, failure);
            Assert.That(settings.mode, Is.EqualTo(SandboxDrawComparePlayerCheck.Mode.Unity));
            Assert.That(settings.warmupLaps, Is.EqualTo(1), "one lap of the path before the measurement, unless said otherwise");
            Assert.That(settings.profilerFile, Is.Null, "no recording unless a file is given");
            Assert.That(settings.captureTimes, Is.Empty, "no capture unless asked for");

            string[] capture = { "Player.exe", "-zantetsuDrawCompare", "C:/out", "-zantetsuDrawCompareMode", "vp3c", "-zantetsuDrawComparePath", "path.txt", "-zantetsuDrawCompareCapture", "1;4.5" };
            Assert.That(SandboxDrawComparePlayerCheck.TryRead(capture, out settings, out failure), Is.True, failure);
            Assert.That(settings.captureTimes, Is.EqualTo(new[] { 1.0, 4.5 }));
        }

        [TestCase(new[] { "Player.exe", "-zantetsuDrawCompare", "C:/out", "-zantetsuDrawComparePath", "p.txt" }, "must be 'unity', 'vp3c' or 'vp3c-native'")]
        [TestCase(new[] { "Player.exe", "-zantetsuDrawCompare", "C:/out", "-zantetsuDrawCompareMode", "both", "-zantetsuDrawComparePath", "p.txt" }, "must be 'unity', 'vp3c' or 'vp3c-native'")]
        [TestCase(new[] { "Player.exe", "-zantetsuDrawCompare", "C:/out", "-zantetsuDrawCompareMode", "vp3c-native", "-zantetsuDrawComparePath", "p.txt" }, "needs the Player launched with -zantetsuVp3cNative")]
        [TestCase(new[] { "Player.exe", "-zantetsuDrawCompare", "C:/out", "-zantetsuDrawCompareMode", "vp3c", "-zantetsuVp3cNative", "-zantetsuDrawComparePath", "p.txt" }, "only mode vp3c-native compares")]
        [TestCase(new[] { "Player.exe", "-zantetsuDrawCompare", "C:/out", "-zantetsuDrawCompareMode", "unity" }, "is required")]
        [TestCase(new[] { "Player.exe", "-zantetsuDrawCompare", "C:/out", "-zantetsuDrawCompareMode", "unity", "-zantetsuDrawComparePath", "p.txt", "-zantetsuPropSlash", "C:/run" }, "does not run beside")]
        [TestCase(new[] { "Player.exe", "-zantetsuDrawCompare", "C:/out", "-zantetsuDrawCompareMode", "unity", "-zantetsuDrawComparePath", "p.txt", "-zantetsuDrawCompareWarmupLaps", "many" }, "must be 0..8")]
        [TestCase(new[] { "Player.exe", "-zantetsuDrawCompare", "C:/out", "-zantetsuDrawCompareMode", "unity", "-zantetsuDrawComparePath", "p.txt", "-zantetsuDrawCompareCapture", "soon" }, "takes path times")]
        public void Arguments_ThatCannotBeRun_AreAFailure_NotADifferentRun(string[] arguments, string expected)
        {
            Assert.That(SandboxDrawComparePlayerCheck.TryRead(arguments, out var settings, out string failure), Is.False);
            Assert.That(settings, Is.Null);
            StringAssert.Contains(expected, failure);
        }

        // A box whose top is a submesh of its own: 24 vertices, two submeshes (30 and 6 indices), UVs inside 0..1.
        internal static Mesh TwoSubmeshBox(string name, Vector3 size, float uvShift = 0f)
        {
            Vector3 h = size * 0.5f;
            Vector3[] normals = { Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back };
            var vertices = new Vector3[24];
            var vertexNormals = new Vector3[24];
            var uvs = new Vector2[24];
            var top = new int[6];
            var sides = new int[30];
            int side = 0;
            for (int f = 0; f < 6; f++)
            {
                Vector3 n = normals[f];
                Vector3 a = Mathf.Abs(n.y) > 0.5f ? Vector3.right : Vector3.up;
                Vector3 b = Vector3.Cross(n, a);
                for (int c = 0; c < 4; c++)
                {
                    float sa = c == 0 || c == 3 ? -1f : 1f, sb = c < 2 ? -1f : 1f;
                    vertices[f * 4 + c] = Vector3.Scale(n + a * sa + b * sb, h);
                    vertexNormals[f * 4 + c] = n;
                    uvs[f * 4 + c] = new Vector2(0.1f + 0.1f * f + uvShift, 0.2f + 0.1f * c);
                }

                int[] quad = { f * 4, f * 4 + 1, f * 4 + 2, f * 4, f * 4 + 2, f * 4 + 3 };
                // The winding that faces outwards along the normal.
                if (Vector3.Dot(Vector3.Cross(vertices[quad[1]] - vertices[quad[0]], vertices[quad[2]] - vertices[quad[0]]), n) < 0f)
                {
                    (quad[1], quad[2]) = (quad[2], quad[1]);
                    (quad[4], quad[5]) = (quad[5], quad[4]);
                }

                if (f == 0) quad.CopyTo(top, 0);
                else { quad.CopyTo(sides, side); side += 6; }
            }

            var mesh = new Mesh { name = name };
            mesh.SetVertices(vertices);
            mesh.SetNormals(vertexNormals);
            mesh.SetUVs(0, uvs);
            mesh.subMeshCount = 2;
            mesh.SetTriangles(sides, 0);
            mesh.SetTriangles(top, 1);
            mesh.RecalculateBounds();
            return mesh;
        }

        [Test]
        public void Geometry_ConvertedOnce_IsReadBackAsItWasWritten_AndFoundByWhatAMeshSaysOfItself()
        {
            Mesh box = TwoSubmeshBox("box", new Vector3(2f, 1f, 3f));
            Mesh other = TwoSubmeshBox("other", new Vector3(1f, 1f, 1f));
            try
            {
                var bank = new SceneDrawMeshBank();
                Assert.That(bank.TryAdd(box, out string refusal), Is.True, refusal);
                Assert.That(bank.TryAdd(other, out refusal), Is.True, refusal);
                Assert.That(bank.TryAdd(box, out refusal), Is.True, "the same geometry again adds nothing: " + refusal);
                Assert.That(bank.Count, Is.EqualTo(2));
                Assert.That(bank.VertexCount, Is.EqualTo(48));
                Assert.That(bank.IndexCount, Is.EqualTo(72));
                Assert.That(bank.TryGet(box, out SceneDrawMeshBank.Entry made), Is.True);
                Assert.That(made.submeshIndexStart, Is.EqualTo(new[] { 0, 30 }));
                Assert.That(made.submeshIndexCount, Is.EqualTo(new[] { 30, 6 }), "the mesh's submeshes in order");
                Assert.That(made.indices.Max(), Is.EqualTo(23u), "the mesh's own vertex numbers");
                Assert.That(made.vertices.All(v => v.HasValidAttributes), Is.True);

                using (var stream = new MemoryStream())
                {
                    bank.Write(stream);
                    byte[] written = stream.ToArray();
                    stream.Position = 0;
                    Assert.That(SceneDrawMeshBank.TryRead(stream, out SceneDrawMeshBank read, out string failure), Is.True, failure);
                    Assert.That(read.Count, Is.EqualTo(2));
                    Assert.That(read.TryGet(box, out SceneDrawMeshBank.Entry back), Is.True, "found by the mesh's name, counts and bounds");
                    Assert.That(back.indices, Is.EqualTo(made.indices));
                    Assert.That(back.submeshIndexCount, Is.EqualTo(made.submeshIndexCount));
                    Assert.That(back.vertices.Length, Is.EqualTo(made.vertices.Length));
                    for (int i = 0; i < back.vertices.Length; i++)
                    {
                        Assert.That(back.vertices[i].position, Is.EqualTo(made.vertices[i].position));
                        Assert.That(back.vertices[i].normal, Is.EqualTo(made.vertices[i].normal));
                        Assert.That(back.vertices[i].uv0, Is.EqualTo(made.vertices[i].uv0));
                    }

                    // Cut short, with something after it, or of another stride: refused, no bank.
                    Assert.That(SceneDrawMeshBank.TryRead(new MemoryStream(written, 0, written.Length - 5), out read, out failure), Is.False);
                    Assert.That(read, Is.Null);
                    Assert.That(SceneDrawMeshBank.TryRead(new MemoryStream(written.Concat(new byte[] { 1 }).ToArray()), out read, out failure), Is.False);
                    StringAssert.Contains("after the last geometry", failure);
                    byte[] stride = (byte[])written.Clone();
                    BitConverter.GetBytes(VpRenderVertex.Stride + 4).CopyTo(stride, 8);
                    Assert.That(SceneDrawMeshBank.TryRead(new MemoryStream(stride), out read, out failure), Is.False);
                    StringAssert.Contains("vertex stride", failure);
                    Assert.That(SceneDrawMeshBank.TryRead(new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }), out read, out failure), Is.False);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(box);
                UnityEngine.Object.DestroyImmediate(other);
            }
        }

        [Test]
        public void Geometry_AnotherMeshUnderTheSameKey_IsRefused_NotTakenForTheFirst()
        {
            // The same name, counts and bounds; another UV inside: what a Player could not tell apart.
            Mesh first = TwoSubmeshBox("twin", new Vector3(1f, 1f, 1f));
            Mesh second = TwoSubmeshBox("twin", new Vector3(1f, 1f, 1f), 0.05f);
            try
            {
                Assert.That(SceneDrawMeshBank.KeyOf(first), Is.EqualTo(SceneDrawMeshBank.KeyOf(second)));
                var bank = new SceneDrawMeshBank();
                Assert.That(bank.TryAdd(first, out string refusal), Is.True, refusal);
                Assert.That(bank.TryAdd(second, out refusal), Is.False);
                StringAssert.Contains("another geometry is kept under the key", refusal);
                Assert.That(bank.Count, Is.EqualTo(1));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(first);
                UnityEngine.Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void Geometry_TheConversionRejects_IsRefusedWithAReason_NothingKept()
        {
            Mesh noNormals = TwoSubmeshBox("no normals", Vector3.one);
            noNormals.SetNormals((Vector3[])null);
            Mesh lines = TwoSubmeshBox("lines", Vector3.one);
            lines.SetIndices(new[] { 0, 1, 1, 2 }, MeshTopology.Lines, 1);
            Mesh tiled = TwoSubmeshBox("tiled", Vector3.one, 2f);
            try
            {
                var bank = new SceneDrawMeshBank();
                Assert.That(bank.TryAdd(noNormals, out string refusal), Is.False);
                StringAssert.Contains("rejected by the VP mesh conversion", refusal);
                Assert.That(bank.TryAdd(lines, out refusal), Is.False);
                StringAssert.Contains("not a triangle list", refusal);
                Assert.That(bank.TryAdd(tiled, out refusal), Is.False, "a UV outside 0..1 is not made into a supported one");
                Assert.That(bank.TryAdd(null, out refusal), Is.False);
                Assert.That(bank.Count, Is.Zero);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(noNormals);
                UnityEngine.Object.DestroyImmediate(lines);
                UnityEngine.Object.DestroyImmediate(tiled);
            }
        }
    }
}
