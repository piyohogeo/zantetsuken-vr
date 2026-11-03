using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.Core.Tests
{
    /// <summary>The GPU bake probe's pieces that need no GPU dispatch (DESIGN 4.5.14): the settings, the known rig, the mesh input's layout reading, the CPU reference.</summary>
    public sealed class SkinBakeProbeTests
    {
        private readonly List<Object> _made = new List<Object>();

        [TearDown]
        public void TearDown()
        {
            foreach (Object o in _made) if (o != null) Object.DestroyImmediate(o);
            _made.Clear();
        }

        private static bool Read(out SkinBakeProbeSettings settings, out string failure, params string[] arguments)
        {
            var all = new List<string> { "Player.exe" };
            all.AddRange(arguments);
            return SkinBakeProbeSettings.TryRead(all.ToArray(), out settings, out failure);
        }

        [Test]
        public void TheSettings_NeedTheDirectory_KeepDefaults_AndRefuseBadValues()
        {
            Assert.That(Read(out SkinBakeProbeSettings s, out string failure, "-zantetsuScenario", "skin-bake-probe"), Is.False);
            Assert.That(s, Is.Null); Assert.That(failure, Does.Contain(SkinBakeProbeSettings.Argument));
            Assert.That(Read(out s, out failure, SkinBakeProbeSettings.Argument, @"C:\out"), Is.True, failure);
            Assert.That(s.directory, Is.EqualTo(@"C:\out")); Assert.That(s.small && s.npc && s.draw && s.picture && !s.keepOpen, Is.True);
            Assert.That(s.drawFrames, Is.EqualTo(SkinBakeProbeSettings.DefaultDrawFrames));
            Assert.That(Read(out s, out failure, SkinBakeProbeSettings.Argument, @"C:\out", SkinBakeProbeSettings.SubjectArgument, "npc", SkinBakeProbeSettings.DrawFramesArgument, "12",
                SkinBakeProbeSettings.NoDrawArgument, SkinBakeProbeSettings.NoPictureArgument, SkinBakeProbeSettings.KeepOpenArgument), Is.True, failure);
            Assert.That(s.small, Is.False); Assert.That(s.npc, Is.True); Assert.That(s.drawFrames, Is.EqualTo(12)); Assert.That(s.draw, Is.False); Assert.That(s.picture, Is.False); Assert.That(s.keepOpen, Is.True);
            Assert.That(Read(out _, out failure, SkinBakeProbeSettings.Argument, @"C:\out", SkinBakeProbeSettings.SubjectArgument, "crowd"), Is.False); Assert.That(failure, Does.Contain("small, npc or both"));
            Assert.That(Read(out _, out failure, SkinBakeProbeSettings.Argument, @"C:\out", SkinBakeProbeSettings.DrawFramesArgument, "1"), Is.False);
            Assert.That(Read(out _, out failure, SkinBakeProbeSettings.Argument, SkinBakeProbeSettings.NoDrawArgument), Is.False); Assert.That(failure, Does.Contain("directory"));
            Assert.That(SkinBakeProbeSettings.TryRead(null, out _, out _), Is.False);
        }

        [Test]
        public void TheRig_HasTheInfluencesAndWeights_A24ByteQuantisationWouldChange()
        {
            var rig = new SkinBakeProbeRig("test rig", null);
            try
            {
                Mesh mesh = rig.Mesh;
                Assert.That(mesh.vertexCount, Is.EqualTo(SkinBakeProbeRig.VertexCount)); Assert.That(SkinBakeProbeRig.VertexCount % SkinBakeProbeGpuBake.GroupSize, Is.Not.Zero, "not a multiple of the group");
                Assert.That(mesh.bindposes.Length, Is.EqualTo(SkinBakeProbeRig.BoneCount)); Assert.That(rig.Renderer.bones.Length, Is.EqualTo(SkinBakeProbeRig.BoneCount));
                using (var counts = mesh.GetBonesPerVertex())
                using (var weights = mesh.GetAllBoneWeights())
                {
                    var seen = new HashSet<int>();
                    int at = 0; bool inexactByte = false;
                    for (int v = 0; v < counts.Length; v++)
                    {
                        seen.Add(counts[v]);
                        float sum = 0f;
                        for (int k = 0; k < counts[v]; k++) { float w = weights[at + k].weight; sum += w; if (Mathf.Abs(w * 255f - Mathf.Round(w * 255f)) > 1e-3f) inexactByte = true; }
                        Assert.That(sum, Is.EqualTo(1f).Within(1e-5f), "vertex " + v + " weights sum to one");
                        at += counts[v];
                    }

                    Assert.That(seen, Is.SupersetOf(new[] { 1, 2, 4 }), "1, 2 and 4 influences occur");
                    Assert.That(inexactByte, Is.True, "a weight a byte cannot hold exactly occurs");
                }

                Vector3[] n = mesh.normals;
                Assert.That(n.Length, Is.EqualTo(mesh.vertexCount));
                for (int v = 0; v < n.Length; v++) Assert.That(n[v].magnitude, Is.EqualTo(1f).Within(1e-4f));
                Assert.That(mesh.GetTopology(0), Is.EqualTo(MeshTopology.Triangles));
                Assert.That(mesh.GetIndexCount(0), Is.EqualTo((SkinBakeProbeRig.Rings - 1) * SkinBakeProbeRig.PerRing * 6));
            }
            finally { rig.Dispose(); }
        }

        [Test]
        public void TheMeshInput_ReadsTheLayoutFromTheMesh_AndAddsRaw()
        {
            var rig = new SkinBakeProbeRig("test rig", null);
            try
            {
                Mesh mesh = rig.Mesh;
                GraphicsBuffer.Target before = mesh.vertexBufferTarget;
                Assert.That(SkinBakeProbeMeshInput.TryPrepare(mesh, out SkinBakeProbeMeshInput input, out string refusal), Is.True, refusal);
                using (input)
                {
                    Assert.That(input.VertexCount, Is.EqualTo(mesh.vertexCount));
                    Assert.That(input.Position.present && input.Normal.present && input.Uv.present, Is.True);
                    Assert.That(input.Position.format, Is.EqualTo(mesh.GetVertexAttributeFormat(VertexAttribute.Position)));
                    Assert.That(input.Position.offset, Is.EqualTo(mesh.GetVertexAttributeOffset(VertexAttribute.Position)));
                    Assert.That(input.Normal.offset, Is.EqualTo(mesh.GetVertexAttributeOffset(VertexAttribute.Normal)));
                    Assert.That(input.Strides[input.Position.stream], Is.EqualTo(mesh.GetVertexBufferStride(input.Position.stream)));
                    Assert.That(input.Streams[input.Position.stream], Is.Not.Null);
                    Assert.That((input.VertexTargetAfter & GraphicsBuffer.Target.Raw) != 0, Is.True, "Raw added to the vertex target (before " + before + ")");
                    Assert.That((input.IndexTargetAfter & GraphicsBuffer.Target.Raw) != 0, Is.True);
                    Assert.That(input.Weights, Is.Not.Null);
                    Assert.That(input.WeightLayout, Is.Not.EqualTo(SkinWeights.None));
                    Assert.That(input.MaxInfluences, Is.EqualTo(4));
                    Assert.That(input.Indices, Is.Not.Null);
                    Assert.That(input.IndexCount, Is.EqualTo((int)mesh.GetIndexCount(0)));
                    Assert.That(input.Describe(), Does.Contain("weights " + input.WeightLayout));
                    input.Hold(); Assert.That(input.Users, Is.EqualTo(1)); input.Release(); Assert.That(input.Users, Is.Zero);
                }

                Assert.That(input.IsDisposed, Is.True);
            }
            finally { rig.Dispose(); }
        }

        [Test]
        public void TheMeshInput_RefusesWhatItDoesNotRead()
        {
            var lines = new Mesh { name = "lines" };
            _made.Add(lines);
            lines.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right };
            lines.normals = new[] { Vector3.up, Vector3.up, Vector3.up };
            lines.SetIndices(new[] { 0, 1, 1, 2 }, MeshTopology.Lines, 0);
            Assert.That(SkinBakeProbeMeshInput.TryPrepare(lines, out _, out string refusal), Is.False);
            Assert.That(refusal, Does.Contain("Lines"));
            var noWeights = new Mesh { name = "no weights" };
            _made.Add(noWeights);
            noWeights.vertices = new[] { Vector3.zero, Vector3.up, Vector3.right };
            noWeights.normals = new[] { Vector3.up, Vector3.up, Vector3.up };
            noWeights.triangles = new[] { 0, 1, 2 };
            Assert.That(SkinBakeProbeMeshInput.TryPrepare(noWeights, out _, out refusal), Is.False);
            Assert.That(refusal, Does.Contain("bone weights"));
            Assert.That(SkinBakeProbeMeshInput.TryPrepare(null, out _, out refusal), Is.False);
        }

        [Test]
        public void TheCpuReference_UnderTheIdentityPalette_IsTheInputEncodedAsTheRenderVertexEncodes()
        {
            var rig = new SkinBakeProbeRig("test rig", null);
            try
            {
                Assert.That(SkinBakeProbeMeshInput.TryPrepare(rig.Mesh, out SkinBakeProbeMeshInput input, out string refusal), Is.True, refusal);
                using (input)
                {
                    SkinBakeProbeCpuBake.Inputs inputs = SkinBakeProbeCpuBake.Read(input);
                    Assert.That(inputs.vertexCount, Is.EqualTo(rig.Mesh.vertexCount)); Assert.That(inputs.maxInfluences, Is.EqualTo(4)); Assert.That(inputs.uv, Is.Not.Null);
                    var palette = new float4x4[SkinBakeProbeRig.BoneCount];
                    for (int b = 0; b < palette.Length; b++) palette[b] = float4x4.identity;
                    var output = new VpRenderVertex[inputs.vertexCount];
                    int invalid = SkinBakeProbeCpuBake.Bake(inputs, palette, output, out float3 min, out float3 max);
                    Assert.That(invalid, Is.Zero);
                    Vector3[] p = rig.Mesh.vertices, n = rig.Mesh.normals; Vector2[] uv = rig.Mesh.uv;
                    for (int v = 0; v < output.Length; v++)
                    {
                        Assert.That(((Vector3)output[v].position - p[v]).magnitude, Is.LessThanOrEqualTo(1e-6f), "position " + v);
                        var expected = new VpRenderVertex { normal = n[v], uv0 = uv[v] };
                        Assert.That(output[v].normalX, Is.EqualTo(expected.normalX), "normal x " + v); Assert.That(output[v].normalY, Is.EqualTo(expected.normalY), "normal y " + v);
                        Assert.That(output[v].u, Is.EqualTo(expected.u), "u " + v); Assert.That(output[v].v, Is.EqualTo(expected.v), "v " + v);
                    }

                    Assert.That(min.y, Is.EqualTo(0f).Within(1e-6f)); Assert.That(max.y, Is.EqualTo(SkinBakeProbeRig.Height).Within(1e-5f));

                    // The palette of the renderer under the bind pose is the identity, under a bend it is not; a one-influence vertex follows its bone exactly.
                    rig.SetPose(0);
                    float4x4[] bind = SkinBakeProbeCpuBake.Palette(rig.Renderer, out int missing);
                    Assert.That(missing, Is.Zero);
                    for (int b = 0; b < bind.Length; b++) for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) Assert.That(bind[b][c][r], Is.EqualTo(float4x4.identity[c][r]).Within(1e-5f));
                    rig.SetPose(2);
                    float4x4[] bent = SkinBakeProbeCpuBake.Palette(rig.Renderer, out _);
                    SkinBakeProbeCpuBake.Bake(inputs, bent, output, out _, out _);
                    int tip = (SkinBakeProbeRig.Rings - 1) * SkinBakeProbeRig.PerRing;   // one influence: bone 3
                    float3 expectedTip = math.mul(bent[SkinBakeProbeRig.BoneCount - 1], new float4(p[tip], 1f)).xyz;
                    Assert.That(math.length((float3)output[tip].position - expectedTip), Is.LessThan(1e-5f));
                    Assert.That(math.length((float3)output[tip].position - (float3)p[tip]), Is.GreaterThan(0.1f), "the strong bend moves the tip");
                }
            }
            finally { rig.Dispose(); }
        }

        [Test]
        public void TheComparison_ClassifiesBoundaryAndOtherDifferences_AndTheGuards()
        {
            var reference = new VpRenderVertex[3];
            reference[0] = new VpRenderVertex { position = new Vector3(1f, 2f, 3f), normal = Vector3.up, uv0 = new Vector2(0.25f, 0.75f) };
            reference[1] = new VpRenderVertex { position = new Vector3(-1f, 0f, 0.5f), normal = new Vector3(0.3f, 0.4f, 0.5f).normalized, uv0 = new Vector2(0.1f, 0.2f) };
            reference[2] = new VpRenderVertex { position = Vector3.zero, normalX = -128, normalY = -128 };
            uint4 Pack(VpRenderVertex v, int dx, int dy, int du, float pdx) => new uint4(math.asuint(v.position.x + pdx), math.asuint(v.position.y), math.asuint(v.position.z),
                ((uint)((sbyte)(v.normalX + dx)) & 0xFF) | (((uint)((sbyte)(v.normalY + dy)) & 0xFF) << 8) | ((uint)(byte)(v.u + du) << 16) | ((uint)v.v << 24));
            uint4 poison = SkinBakeProbeGpuBake.Poison;
            var data = new Unity.Collections.NativeArray<uint4>(1 + 3 + 1, Unity.Collections.Allocator.Temp);
            try
            {
                data[0] = poison;
                data[1] = Pack(reference[0], 0, 0, 0, 0f);          // exact
                data[2] = Pack(reference[1], 1, 0, 1, 5e-5f);       // boundary normal, boundary uv, position within tolerance
                data[3] = new uint4(math.asuint(0f), math.asuint(0f), math.asuint(0f), 0x8080u);   // invalid, as the reference
                data[4] = poison;
                SkinBakeProbeCompare.Result r = SkinBakeProbeCompare.Compare("t", data, 1, 3, 1, reference, 3, 1, 0, poison);
                Assert.That(r.Pass, Is.True, r.Describe());
                Assert.That(r.normalExact, Is.EqualTo(1)); Assert.That(r.normalBoundary, Is.EqualTo(1)); Assert.That(r.normalOther, Is.Zero);
                Assert.That(r.uvExact, Is.EqualTo(1)); Assert.That(r.uvBoundary, Is.EqualTo(1));
                Assert.That(r.invalidCpu, Is.EqualTo(1)); Assert.That(r.invalidGpu, Is.EqualTo(1)); Assert.That(r.invalidDisagree, Is.Zero);
                Assert.That(r.positionMaxError, Is.EqualTo(5e-5f).Within(1e-6f)); Assert.That(r.positionOverTolerance, Is.Zero);

                data[2] = Pack(reference[1], 40, 0, 0, 1e-2f);      // another normal, a position over the tolerance
                data[4] = new uint4(1, 2, 3, 4);                     // the guard written
                r = SkinBakeProbeCompare.Compare("t", data, 1, 3, 1, reference, 2, 1, 0, poison);
                Assert.That(r.Pass, Is.False);
                Assert.That(r.normalOther, Is.EqualTo(1)); Assert.That(r.positionOverTolerance, Is.EqualTo(1)); Assert.That(r.guardAfterIntact, Is.False); Assert.That(r.processedMatches, Is.False);
            }
            finally { data.Dispose(); }
        }

        [Test]
        public void TheWeightReading_FollowsTheBuffersTarget_AndStride()
        {
            // Raw wins whatever else the target has; without it, Structured with the layout's stride; else refused, naming what was found.
            Assert.That(SkinBakeProbeMeshInput.TryChooseWeightReading(GraphicsBuffer.Target.Vertex | GraphicsBuffer.Target.Raw, 32, 32, out SkinBakeProbeMeshInput.WeightReadingKind r, out string why), Is.True, why);
            Assert.That(r, Is.EqualTo(SkinBakeProbeMeshInput.WeightReadingKind.Raw));
            Assert.That(SkinBakeProbeMeshInput.TryChooseWeightReading(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.Structured, 16, 16, out r, out why), Is.True, why);
            Assert.That(r, Is.EqualTo(SkinBakeProbeMeshInput.WeightReadingKind.Raw));
            Assert.That(SkinBakeProbeMeshInput.TryChooseWeightReading(GraphicsBuffer.Target.Vertex | GraphicsBuffer.Target.Structured, 32, 32, out r, out why), Is.True, why);
            Assert.That(r, Is.EqualTo(SkinBakeProbeMeshInput.WeightReadingKind.Structured32));
            Assert.That(SkinBakeProbeMeshInput.TryChooseWeightReading(GraphicsBuffer.Target.Structured, 16, 16, out r, out why), Is.True, why);
            Assert.That(r, Is.EqualTo(SkinBakeProbeMeshInput.WeightReadingKind.Structured16));
            Assert.That(SkinBakeProbeMeshInput.TryChooseWeightReading(GraphicsBuffer.Target.Structured, 8, 8, out r, out why), Is.True, why);
            Assert.That(r, Is.EqualTo(SkinBakeProbeMeshInput.WeightReadingKind.Structured8));
            Assert.That(SkinBakeProbeMeshInput.TryChooseWeightReading(GraphicsBuffer.Target.Vertex, 32, 32, out r, out why), Is.False);
            Assert.That(why, Does.Contain("Raw").And.Contain("Structured").And.Contain("Vertex"));
            Assert.That(SkinBakeProbeMeshInput.TryChooseWeightReading(GraphicsBuffer.Target.Structured, 16, 32, out r, out why), Is.False);
            Assert.That(why, Does.Contain("16").And.Contain("32"));
            Assert.That(SkinBakeProbeMeshInput.TryChooseWeightReading(GraphicsBuffer.Target.Structured, 12, 12, out r, out why), Is.False);
            Assert.That(why, Does.Contain("12"));
        }

        [Test]
        public void TheComputeShader_DeclaresTheWeightReadingVariants()
        {
            var shader = Resources.Load<ComputeShader>(SkinBakeProbeGpuBake.ShaderResource);
            Assert.That(shader, Is.Not.Null);
            string[] names = shader.keywordSpace.keywordNames;
            Assert.That(names, Does.Contain("WEIGHTS_S8").And.Contain("WEIGHTS_S16").And.Contain("WEIGHTS_S32"));
            Assert.That(shader.FindKernel("Bake16"), Is.GreaterThanOrEqualTo(0));
            Assert.That(names, Does.Contain("PROBE_NO_COUNTS"));
        }

        // ---- SKIN-BAKE-PROBE-2

        [Test]
        public void TheStorage_TakesTheProducersBounds_ForAPublishedGeometry()
        {
            using (var storage = new VpCpuGeometryStorage(256, 1024, 16, 16, 16, Allocator.Persistent))
            {
                // A small placeholder: four vertices in a 1 cm speck, two triangles.
                var vertices = new VpRenderVertex[4];
                for (int i = 0; i < 4; i++) vertices[i] = new VpRenderVertex { position = new Vector3(0.001f * i, 0f, 0f), normalX = 0, normalY = 0 };
                uint[] indices = { 0, 1, 2, 0, 2, 3 };
                int[] topology = { 0, 1, 2, 3 };
                var submeshes = new[] { new VpGeometrySubmesh(0, 6, 0) };
                Assert.That(storage.TryAppendPrepared(vertices, indices, topology, 4, submeshes, out VpStoredGeometry geometry), Is.True);
                Assert.That(storage.TryGetPublishedExtent(geometry, out int start, out int count, out Bounds measured), Is.True);
                Assert.That(measured.size.x, Is.LessThan(0.01f), "the storage measured the speck");

                var box = new Bounds(new Vector3(0f, 0.8f, 0f), new Vector3(1f, 2f, 1f));
                Assert.That(storage.TryReplaceExtentBounds(geometry, box), Is.True);
                Assert.That(storage.TryGetPublishedExtent(geometry, out int start2, out int count2, out Bounds replaced), Is.True);
                Assert.That(Vector3.Distance(replaced.min, box.min), Is.LessThan(1e-5f)); Assert.That(Vector3.Distance(replaced.max, box.max), Is.LessThan(1e-5f));   // the extent is re-made from centre and size: float rounding
                Assert.That(start2, Is.EqualTo(start)); Assert.That(count2, Is.EqualTo(count));
                Assert.That(storage.TryGetSubmeshBounds(geometry, out NativeArray<VpGeometryBounds>.ReadOnly sub), Is.True);
                Assert.That(sub.Length, Is.EqualTo(1)); Assert.That(Vector3.Distance(sub[0].min, box.min), Is.LessThan(1e-5f)); Assert.That(Vector3.Distance(sub[0].max, box.max), Is.LessThan(1e-5f));

                // Refused, nothing changed: an inverted box, a NaN box.
                var inverted = new Bounds(); inverted.SetMinMax(new Vector3(1f, 1f, 1f), new Vector3(0f, 0f, 0f));
                Assert.That(storage.TryReplaceExtentBounds(geometry, inverted), Is.False);
                var nan = new Bounds(new Vector3(float.NaN, 0f, 0f), Vector3.one);
                Assert.That(storage.TryReplaceExtentBounds(geometry, nan), Is.False);
                Assert.That(storage.TryGetPublishedExtent(geometry, out _, out _, out Bounds still), Is.True);
                Assert.That(Vector3.Distance(still.min, box.min), Is.LessThan(1e-5f)); Assert.That(Vector3.Distance(still.max, box.max), Is.LessThan(1e-5f));
            }
        }

        [Test]
        public void TheGpuBuffers_WriteARegionThroughTheOwner_AndRaiseTheVertexGeneration()
        {
            using (var buffers = new VpGpuIndexedGeometryBuffers(8, 8))
            {
                var data = new NativeArray<VpRenderVertex>(3, Allocator.Temp);
                for (int i = 0; i < 3; i++) data[i] = new VpRenderVertex { position = new Vector3(i, 0f, 0f), normalX = 1, normalY = 2, u = 3, v = 4 };
                uint before = buffers.VertexGeneration;
                Assert.That(buffers.TryWriteVertices(data, 2, 3), Is.True);
                Assert.That(buffers.VertexGeneration, Is.EqualTo(before + 1), "the write raised the generation");
                Assert.That(buffers.TryWriteVertices(data, 6, 3), Is.False, "outside the capacity");
                Assert.That(buffers.TryWriteVertices(data, 0, 4), Is.False, "more than the data holds");
                Assert.That(buffers.TryWriteVertices(data, 0, 0), Is.True, "nothing to write succeeds");
                Assert.That(buffers.VertexGeneration, Is.EqualTo(before + 1), "a refusal or an empty write raises nothing");
                var read = new VpRenderVertex[8];
                buffers.VertexBuffer.GetData(read);
                Assert.That(read[2].position, Is.EqualTo(new Vector3(0f, 0f, 0f))); Assert.That(read[4].position, Is.EqualTo(new Vector3(2f, 0f, 0f)));
                Assert.That(read[3].u, Is.EqualTo(3)); Assert.That(read[3].normalY, Is.EqualTo(2));
                data.Dispose();
            }
        }

        // ---- SKIN-BAKE-PROBE-3

        [Test]
        public void TheSettings_ReadTheBenchSeries_AndTheLoad()
        {
            Assert.That(SkinBakeProbeSettings.TryParseSeries("none1,product1,burst1,gpu1,gpu4,gpu1x4", out var series, out string why), Is.True, why);
            Assert.That(series.Count, Is.EqualTo(6));
            Assert.That(series[1].route, Is.EqualTo("product")); Assert.That(series[1].bodies, Is.EqualTo(1)); Assert.That(series[1].repeat, Is.EqualTo(1));
            Assert.That(series[4].bodies, Is.EqualTo(4)); Assert.That(series[5].repeat, Is.EqualTo(4)); Assert.That(series[5].Name, Is.EqualTo("gpu1x4"));
            Assert.That(SkinBakeProbeSettings.TryParseSeries("cpu1", out _, out why), Is.False); Assert.That(why, Does.Contain("route"));
            Assert.That(SkinBakeProbeSettings.TryParseSeries("gpu9", out _, out why), Is.False); Assert.That(why, Does.Contain("1..8"));
            Assert.That(SkinBakeProbeSettings.TryParseSeries("burst1x4", out _, out why), Is.False); Assert.That(why, Does.Contain("gpu route"));
            Assert.That(SkinBakeProbeSettings.TryParseSeries("", out _, out why), Is.False);
            Assert.That(Read(out SkinBakeProbeSettings s, out why, "-zantetsuSkinBakeProbe", "C:/out", "-zantetsuSkinBakeProbeBenchSeries", "none1,gpu4", "-zantetsuSkinBakeProbeBenchLoad", "32"), Is.True, why);
            Assert.That(s.IsBench, Is.True); Assert.That(s.SeriesText, Is.EqualTo("none1,gpu4")); Assert.That(s.benchLoad, Is.EqualTo(32)); Assert.That(s.Describe(), Does.Contain("BENCH series none1,gpu4"));
            Assert.That(Read(out s, out why, "-zantetsuSkinBakeProbe", "C:/out", "-zantetsuSkinBakeProbeBench", "burst", "-zantetsuSkinBakeProbeBenchBodies", "4"), Is.True, why);
            Assert.That(s.SeriesText, Is.EqualTo("burst4"), "the one-route bench is a series of one condition");
            Assert.That(Read(out _, out why, "-zantetsuSkinBakeProbe", "C:/out", "-zantetsuSkinBakeProbeBench", "gpu", "-zantetsuSkinBakeProbeBenchSeries", "gpu1"), Is.False); Assert.That(why, Does.Contain("two ways"));
            Assert.That(Read(out _, out why, "-zantetsuSkinBakeProbe", "C:/out", "-zantetsuSkinBakeProbeBenchLoad", "65"), Is.False);
        }

        [Test]
        public void TheRecord_TakesTheBenchRowsOfItsColumns()
        {
            var record = new SkinBakeProbeRecord();
            var row = new string[SkinBakeProbeRecord.BenchColumns.Length];
            for (int i = 0; i < row.Length; i++) row[i] = i.ToString();
            record.BenchFrame(row);
            Assert.That(record.BenchRows.Count, Is.EqualTo(1));
            Assert.That(() => record.BenchFrame("1", "2"), Throws.ArgumentException);
            Assert.That(() => record.BenchSummary("a"), Throws.ArgumentException);
            record.BenchSummary("gpu1", "gpu", "1", "1", "issueMs", "300", "0.02", "0.03", "0.021", "0.01", "0.05");
            Assert.That(SkinBakeProbeRecord.BenchColumns, Does.Contain("ftGpuMs").And.Contain("xrAppGpuMs").And.Contain("indicesMs"));
        }

        [Test]
        public void TheProductEntry_BakesTheCapturedPose_ByTheProductKernel()
        {
            var rig = new SkinBakeProbeRig("product rig", null);
            try
            {
                int n = rig.Mesh.vertexCount;
                var topology = new int[n];
                for (int i = 0; i < n; i++) topology[i] = i;
                rig.SetPose(1);
                if (!VpDirectSkinInput.TryCreate(rig.Renderer, topology, n, out VpDirectSkinInput direct))
                {
                    // The known rig is no product cut input (its weights are not four a vertex): the product's cold gate refuses it, and the
                    // reason is the test's record; the product entry is checked on the NPC in the Player (the bench's product conditions).
                    VpSkinModel.TryCreate(rig.Renderer, topology, n, null, out VpSkinModel model, out string gate);
                    model?.Dispose();
                    Assert.Pass("the product's cold gate refused the known rig: " + gate);
                }

                using (direct)
                using (var output = new NativeArray<VpRenderVertex>(n, Allocator.Temp))
                using (var outputTopology = new NativeArray<int>(n, Allocator.Temp))
                using (var indices = new NativeArray<uint>(direct.IndexCount, Allocator.Temp))
                {
                    Assert.That(direct.TryBakeCaptured(output, outputTopology, out _, out _), Is.False, "no pose captured yet");
                    Assert.That(direct.TryCapturePose(), Is.True);
                    Assert.That(direct.TryBakeCaptured(output, outputTopology, out float3 min, out float3 max), Is.True);
                    Assert.That(math.all(min <= max), Is.True);
                    Assert.That(direct.TryCopyIndices(indices, 7), Is.True);
                    Assert.That(indices[0], Is.EqualTo((uint)rig.Mesh.GetIndices(0)[0] + 7u));
                    Assert.That(direct.TryBakeCaptured(output, outputTopology, out _, out _), Is.True, "the pose stays taken for another call");

                    // Against the float reference of the same pose: the 24-byte input's quantisation (oct normal, uint8 weights) shows as small differences, told apart from a defect.
                    Assert.That(SkinBakeProbeMeshInput.TryPrepare(rig.Mesh, out SkinBakeProbeMeshInput input, out string refusal), Is.True, refusal);
                    using (input)
                    {
                        SkinBakeProbeCpuBake.Inputs inputs = SkinBakeProbeCpuBake.Read(input);
                        var reference = new VpRenderVertex[n];
                        SkinBakeProbeCpuBake.Bake(inputs, SkinBakeProbeCpuBake.Palette(rig.Renderer, out _), reference, out _, out _);
                        SkinBakeProbeCompare.Result r = SkinBakeProbeCompare.Compare("product vs float", output.Reinterpret<uint4>(), 0, n, 0, reference, 0, 0, 0, SkinBakeProbeGpuBake.Poison);
                        Assert.That(r.positionMaxError, Is.LessThan(0.02f), "the uint8 weights move a position by millimetres at most on this rig: " + r.Describe());
                        Assert.That(r.normalOther + r.uvOther, Is.LessThan(n / 4), "most normals and every uv within a quantisation step: " + r.Describe());
                        Assert.That(r.invalidDisagree, Is.Zero);
                    }
                }
            }
            finally { rig.Dispose(); }
        }

        [Test]
        public void TheConservativeBounds_ContainTheBakedShape_UnderEachPose()
        {
            var rig = new SkinBakeProbeRig("bounds rig", null);
            try
            {
                Assert.That(SkinBakeProbeMeshInput.TryPrepare(rig.Mesh, out SkinBakeProbeMeshInput input, out string refusal), Is.True, refusal);
                using (input)
                {
                    SkinBakeProbeCpuBake.Inputs inputs = SkinBakeProbeCpuBake.Read(input);
                    var reference = new VpRenderVertex[input.VertexCount];
                    for (int pose = 0; pose < 3; pose++)
                    {
                        rig.SetPose(pose);
                        float4x4[] palette = SkinBakeProbeCpuBake.Palette(rig.Renderer, out _);
                        SkinBakeProbeCpuBake.Bake(inputs, palette, reference, out float3 min, out float3 max);
                        Bounds box = SkinBakeProbeBounds.Conservative(rig.Mesh.bounds, palette, palette.Length, 0.01f);
                        Assert.That(SkinBakeProbeBounds.Contains(box, reference, out int outside), Is.True, "pose " + pose + ": " + outside + " vertices outside " + box.min + ".." + box.max + " (shape " + (Vector3)min + ".." + (Vector3)max + ")");
                        Assert.That(box.Contains(Vector3.zero), Is.True, "the origin is inside");
                    }

                    // A box that misses the shape is told so.
                    var tiny = new Bounds(Vector3.zero, new Vector3(0.001f, 0.001f, 0.001f));
                    Assert.That(SkinBakeProbeBounds.Contains(tiny, reference, out int missed), Is.False);
                    Assert.That(missed, Is.GreaterThan(0));
                }
            }
            finally { rig.Dispose(); }
        }

        [Test]
        public void TheBurstKernel_GivesTheReferencesVertices_AndRunsThroughBurst()
        {
            var rig = new SkinBakeProbeRig("burst rig", null);
            try
            {
                Assert.That(SkinBakeProbeMeshInput.TryPrepare(rig.Mesh, out SkinBakeProbeMeshInput input, out string refusal), Is.True, refusal);
                using (input)
                {
                    SkinBakeProbeCpuBake.Inputs inputs = SkinBakeProbeCpuBake.Read(input);
                    var reference = new VpRenderVertex[input.VertexCount];
                    using (SkinBakeProbeBurstBake.Prepared prepared = SkinBakeProbeBurstBake.Prepare(inputs, SkinBakeProbeRig.BoneCount))
                    {
                        Assert.That(prepared.VertexCount, Is.EqualTo(input.VertexCount));
                        Assert.That(prepared.BytesAllocated, Is.GreaterThan(0));
                        for (int pose = 0; pose < 3; pose++)
                        {
                            rig.SetPose(pose);
                            float4x4[] palette = SkinBakeProbeCpuBake.Palette(rig.Renderer, out _);
                            int referenceInvalid = SkinBakeProbeCpuBake.Bake(inputs, palette, reference, out _, out _);
                            SkinBakeProbeBurstBake.SetPalette(prepared, palette);
                            int invalid = SkinBakeProbeBurstBake.Run(prepared, out bool burstRan);
                            Assert.That(invalid, Is.EqualTo(referenceInvalid), "pose " + pose + ": the invalid count");
                            Assert.That(burstRan, Is.EqualTo(Unity.Burst.BurstCompiler.IsEnabled), "pose " + pose + ": the Burst entry ran exactly where Burst is enabled");
                            int positionDiffs = 0, encodingDiffs = 0;
                            for (int i = 0; i < input.VertexCount; i++)
                            {
                                VpRenderVertex a = prepared.Output[i], b = reference[i];
                                if (a.position != b.position) positionDiffs++;
                                if (a.normalX != b.normalX || a.normalY != b.normalY || a.u != b.u || a.v != b.v) encodingDiffs++;
                            }

                            Assert.That(positionDiffs, Is.Zero, "pose " + pose + ": positions bitwise equal (Strict)");
                            Assert.That(encodingDiffs, Is.Zero, "pose " + pose + ": normals and uv bytes equal");
                        }
                    }
                }
            }
            finally { rig.Dispose(); }
        }

        [Test]
        public void TheSettings_ReadTheBench_AndRefuseBadValues()
        {
            Assert.That(Read(out SkinBakeProbeSettings s, out string why, "-zantetsuSkinBakeProbe", "C:/out"), Is.True, why);
            Assert.That(s.IsBench, Is.False); Assert.That(s.bench, Is.Null);
            Assert.That(Read(out s, out why, "-zantetsuSkinBakeProbe", "C:/out", "-zantetsuSkinBakeProbeBench", "gpu"), Is.True, why);
            Assert.That(s.IsBench, Is.True); Assert.That(s.bench, Is.EqualTo("gpu")); Assert.That(s.benchFrames, Is.EqualTo(SkinBakeProbeSettings.DefaultBenchFrames)); Assert.That(s.benchWarmup, Is.EqualTo(SkinBakeProbeSettings.DefaultBenchWarmup)); Assert.That(s.benchBodies, Is.EqualTo(1));
            Assert.That(s.Describe(), Does.Contain("BENCH series gpu1"));
            Assert.That(Read(out s, out why, "-zantetsuSkinBakeProbe", "C:/out", "-zantetsuSkinBakeProbeBench", "Burst", "-zantetsuSkinBakeProbeBenchFrames", "120", "-zantetsuSkinBakeProbeBenchWarmup", "0", "-zantetsuSkinBakeProbeBenchBodies", "4"), Is.True, why);
            Assert.That(s.bench, Is.EqualTo("burst")); Assert.That(s.benchFrames, Is.EqualTo(120)); Assert.That(s.benchWarmup, Is.Zero); Assert.That(s.benchBodies, Is.EqualTo(4));
            Assert.That(Read(out _, out why, "-zantetsuSkinBakeProbe", "C:/out", "-zantetsuSkinBakeProbeBench", "cpu"), Is.False); Assert.That(why, Does.Contain("burst or gpu"));
            Assert.That(Read(out _, out why, "-zantetsuSkinBakeProbe", "C:/out", "-zantetsuSkinBakeProbeBenchFrames", "5"), Is.False); Assert.That(why, Does.Contain("10..100000"));
            Assert.That(Read(out _, out why, "-zantetsuSkinBakeProbe", "C:/out", "-zantetsuSkinBakeProbeBenchBodies", "9"), Is.False); Assert.That(why, Does.Contain("1..8"));
            Assert.That(Read(out _, out why, "-zantetsuSkinBakeProbe", "C:/out", "-zantetsuSkinBakeProbeBenchWarmup", "-1"), Is.False);
            Assert.That(s.edgeRight, Is.EqualTo(SkinBakeProbeSettings.DefaultEdgeRight)); Assert.That(s.edgeLeft, Is.EqualTo(SkinBakeProbeSettings.DefaultEdgeLeft));
            Assert.That(Read(out s, out why, "-zantetsuSkinBakeProbe", "C:/out", "-zantetsuSkinBakeProbeEdgeRight", "0.6", "-zantetsuSkinBakeProbeEdgeLeft", "-2.8"), Is.True, why);
            Assert.That(s.edgeRight, Is.EqualTo(0.6f)); Assert.That(s.edgeLeft, Is.EqualTo(-2.8f)); Assert.That(s.Describe(), Does.Contain("edges right 0.60 m, left -2.80 m"));
            Assert.That(Read(out _, out why, "-zantetsuSkinBakeProbe", "C:/out", "-zantetsuSkinBakeProbeEdgeRight", "11"), Is.False); Assert.That(why, Does.Contain("metres"));
            Assert.That(Read(out _, out why, "-zantetsuSkinBakeProbe", "C:/out", "-zantetsuSkinBakeProbeEdgeLeft", "x"), Is.False);
        }

        [Test]
        public void TheMeshInput_RefusesSeveralSubmeshes_AndASubmeshNotAtZero()
        {
            var rig = new SkinBakeProbeRig("submesh rig", null);
            try
            {
                Mesh two = Object.Instantiate(rig.Mesh);
                _made.Add(two);
                int[] all = two.GetIndices(0);
                int half = all.Length / 2 / 3 * 3;
                two.subMeshCount = 2;
                two.SetIndices(all, 0, half, MeshTopology.Triangles, 0, false);
                two.SetIndices(all, half, all.Length - half, MeshTopology.Triangles, 1, false);
                Assert.That(SkinBakeProbeMeshInput.TryPrepare(two, out _, out string refusal), Is.False);
                Assert.That(refusal, Does.Contain("2 submeshes"));

                Mesh based = Object.Instantiate(rig.Mesh);
                _made.Add(based);
                var descriptor = based.GetSubMesh(0);
                descriptor.baseVertex = 1;
                based.SetSubMesh(0, descriptor, UnityEngine.Rendering.MeshUpdateFlags.DontValidateIndices | UnityEngine.Rendering.MeshUpdateFlags.DontRecalculateBounds | UnityEngine.Rendering.MeshUpdateFlags.DontNotifyMeshUsers);
                Assert.That(SkinBakeProbeMeshInput.TryPrepare(based, out _, out refusal), Is.False);
                Assert.That(refusal, Does.Contain("baseVertex 1"));
            }
            finally { rig.Dispose(); }
        }

        // ---- PLANE-1: the cut plane setting, the horizontal plane for a Z-up renderer, the Quest configuration

        [Test]
        public void CutPlane_Settings_LegacyByDefault_HorizontalAsked_OthersRefused()
        {
            Assert.That(Read(out SkinBakeProbeSettings s, out string failure, "-zantetsuSkinBakeProbe", "C:/x"), Is.True, failure);
            Assert.That(s.cutPlane, Is.EqualTo("legacy"));
            Assert.That(s.holdCpuReady, Is.EqualTo(0));
            Assert.That(Read(out s, out failure, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeCutPlane", "horizontal", "-zantetsuSkinBakeProbeHoldCpuReady", "5"), Is.True, failure);
            Assert.That(s.cutPlane, Is.EqualTo("horizontal"));
            Assert.That(s.holdCpuReady, Is.EqualTo(5));
            Assert.That(s.Describe(), Does.Not.Contain("cut plane"), "the cut plane is described with the cut bench only");
            Assert.That(Read(out s, out failure, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeCutBench", "4", "-zantetsuSkinBakeProbeCutPlane", "horizontal", "-zantetsuSkinBakeProbeHoldCpuReady", "5"), Is.True, failure);
            Assert.That(s.Describe(), Does.Contain("cut plane horizontal").And.Contain("HOLD cpu ready 5"));
            Assert.That(Read(out _, out failure, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeCutPlane", "vertical"), Is.False);
            Assert.That(failure, Does.Contain("legacy|horizontal"));
            Assert.That(Read(out _, out failure, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeHoldCpuReady", "61"), Is.False);
            Assert.That(failure, Does.Contain("0..60"));
        }

        [Test]
        public void CutPlane_Horizontal_SeparatesTheWorldsUpFromItsDown_ForAZUpRenderer_LegacyDoesNot()
        {
            // The cut bench's NPC renderer: the character root turned 180 degrees about y, the mesh Z-up under it (the stored
            // geometry's height along local z), standing at (-1.2, 0, 3); the local bounds centre as the listing of SKIN-LINES-1 had it.
            Matrix4x4 placement = Matrix4x4.TRS(new Vector3(-1.2f, 0f, 3f), Quaternion.Euler(270f, 180f, 0f), Vector3.one);
            var localCentre = new Vector3(0f, -0.03f, 0.9f);
            Assert.That(SkinBakeProbeCutPlane.TryMake("horizontal", placement, localCentre, out float4 local, out float4 world, out float3 anchor, out string failure), Is.True, failure);
            Assert.That(world.y, Is.EqualTo(1f), "the world plane's normal is the world's up");
            Assert.That(math.abs(math.dot(world.xyz, anchor) + world.w), Is.LessThan(1e-5f), "the world plane passes through the anchor");
            Assert.That(SkinBakeProbeCutPlane.CheckWorldUpSides(local, placement, anchor, out string detail), Is.True, detail);
            // Decided by the numbers: points 0.5 m above and below the anchor, in the renderer's frame, evaluate with the sides' signs,
            // and the local plane is not the +y plane of the renderer's frame (which is the front/back split).
            Matrix4x4 inverse = placement.inverse;
            float Eval(Vector3 w) { Vector3 l = inverse.MultiplyPoint3x4(w); return math.dot(local.xyz, (float3)l) + local.w; }
            Assert.That(Eval((Vector3)anchor + Vector3.up * 0.5f), Is.GreaterThan(0.4f).And.LessThan(0.6f), "0.5 m above: +0.5 of signed distance (unit normal)");
            Assert.That(Eval((Vector3)anchor - Vector3.up * 0.5f), Is.LessThan(-0.4f).And.GreaterThan(-0.6f));
            Assert.That(math.abs(local.y), Is.LessThan(0.01f), "the horizontal plane is not the renderer frame's +y plane for a Z-up mesh");
            Assert.That(math.abs(local.z), Is.GreaterThan(0.99f), "its normal lies along the renderer frame's z (the mesh's height)");

            Assert.That(SkinBakeProbeCutPlane.TryMake("legacy", placement, localCentre, out float4 legacy, out _, out float3 legacyAnchor, out failure), Is.True, failure);
            Assert.That(legacy, Is.EqualTo(new float4(0f, 1f, 0f, 0.03f)), "legacy is the earlier benches' plane, unchanged");
            Assert.That(legacyAnchor, Is.EqualTo(anchor));
            Assert.That(SkinBakeProbeCutPlane.CheckWorldUpSides(legacy, placement, legacyAnchor, out detail), Is.False, "legacy splits front/back for this renderer: " + detail);

            // A non-uniform scale is still a plane; a placement degenerate in the normal's direction is refused and nothing substituted.
            Matrix4x4 scaled = Matrix4x4.TRS(new Vector3(0.4f, 0f, 3f), Quaternion.Euler(270f, 180f, 0f), new Vector3(0.5f, 0.5f, 2f));
            Assert.That(SkinBakeProbeCutPlane.TryMake("horizontal", scaled, localCentre, out local, out _, out anchor, out failure), Is.True, failure);
            Assert.That(SkinBakeProbeCutPlane.CheckWorldUpSides(local, scaled, anchor, out detail), Is.True, detail);
            // Exactly degenerate in the normal's direction (a rotated placement leaves rounding of ~1e-8 in the normal, which the conversion accepts as a plane).
            Matrix4x4 flat = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(1f, 0f, 1f));
            Assert.That(SkinBakeProbeCutPlane.TryMake("horizontal", flat, localCentre, out local, out _, out _, out failure), Is.False);
            Assert.That(failure, Does.Contain("refused"));
            Assert.That(local, Is.EqualTo(default(float4)), "no substitute plane");
            Assert.That(SkinBakeProbeCutPlane.TryMake("sideways", placement, localCentre, out _, out _, out _, out failure), Is.False);
            Assert.That(failure, Does.Contain("unknown"));
        }

        // ---- MOTION-CAPTURE-1: the check's setting and its view sequence

        [Test]
        public void MotionCheck_Settings_ParseAndRefuse_AndRunAlone()
        {
            Assert.That(Read(out SkinBakeProbeSettings s, out string failure, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeMotionCheck", "-zantetsuSkinBakeProbeMotionCheckStepCm", "1.5", "-zantetsuSkinBakeProbeMotionCheckFrames", "8", "-zantetsuSkinBakeProbeMotionCheckHold", "10"), Is.True, failure);
            Assert.That(s.IsMotionCheck, Is.True);
            Assert.That(s.motionCheckStepCm, Is.EqualTo(1.5f));
            Assert.That(s.motionCheckFrames, Is.EqualTo(8));
            Assert.That(s.motionCheckHold, Is.EqualTo(10));
            Assert.That(s.Describe(), Does.Contain("MOTION CHECK"));
            Assert.That(Read(out s, out failure, "-zantetsuSkinBakeProbe", "C:/x"), Is.True, failure);
            Assert.That(s.IsMotionCheck, Is.False);
            Assert.That(s.motionCheckStepCm, Is.EqualTo(1f)); Assert.That(s.motionCheckFrames, Is.EqualTo(6)); Assert.That(s.motionCheckHold, Is.EqualTo(12));
            Assert.That(Read(out _, out failure, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeMotionCheckStepCm", "0"), Is.False);
            Assert.That(failure, Does.Contain("centimetres"));
            Assert.That(Read(out _, out failure, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeMotionCheck", "-zantetsuSkinBakeProbeCutBench", "4"), Is.False);
            Assert.That(failure, Does.Contain("runs alone"));
            SkinBakeProbeSettings q = Zantetsu.Quest.QuestSkinBakeProbe.SettingsOf("{\"skinBakeProbeMotionCheck\":true,\"skinBakeProbeMotionCheckStepCm\":2.0,\"skinBakeProbeMotionCheckFrames\":5,\"skinBakeProbeMotionCheckHold\":9}", "C:/x", out failure);
            Assert.That(q, Is.Not.Null, failure);
            Assert.That(q.IsMotionCheck, Is.True); Assert.That(q.motionCheckStepCm, Is.EqualTo(2f)); Assert.That(q.motionCheckFrames, Is.EqualTo(5)); Assert.That(q.motionCheckHold, Is.EqualTo(9));
        }

        [Test]
        public void Warm_Settings_TakeEachKind_AndRefuseOthers()
        {
            foreach (string kind in SkinBakeProbeSettings.WarmKinds)
            {
                Assert.That(Read(out SkinBakeProbeSettings s, out string failure, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeWarm", kind), Is.True, failure);
                Assert.That(s.warm, Is.EqualTo(kind));
                Assert.That(s.Describe().Contains("WARM"), Is.EqualTo(kind != "none"));
                SkinBakeProbeSettings q = Zantetsu.Quest.QuestSkinBakeProbe.SettingsOf("{\"skinBakeProbeWarm\":\"" + kind + "\"}", "C:/x", out failure);
                Assert.That(q, Is.Not.Null, failure);
                Assert.That(q.warm, Is.EqualTo(kind));
            }
            Assert.That(Read(out SkinBakeProbeSettings d, out string f2, "-zantetsuSkinBakeProbe", "C:/x"), Is.True, f2);
            Assert.That(d.warm, Is.EqualTo("none"));
            Assert.That(Read(out _, out f2, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeWarm", "everything"), Is.False);
            Assert.That(f2, Does.Contain("none|draw"));
            Assert.That(Zantetsu.Quest.QuestSkinBakeProbe.SettingsOf("{\"skinBakeProbeWarm\":\"everything\"}", "C:/x", out f2), Is.Null);
            Assert.That(f2, Does.Contain("skinBakeProbeWarm"));
        }

        [Test]
        public void MotionView_Settings_BelongToTheCutBenchWithMotionCapture()
        {
            Assert.That(Read(out SkinBakeProbeSettings s, out string failure, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeCutBench", "1", "-zantetsuSkinBakeProbeCutPictureFrames", "24", "-zantetsuSkinBakeProbeMotionCapture", "-zantetsuSkinBakeProbeMotionView", "-zantetsuSkinBakeProbeMotionCheckFrames", "12", "-zantetsuSkinBakeProbeMotionCheckHold", "12"), Is.True, failure);
            Assert.That(s.motionView, Is.True); Assert.That(s.motionCapture, Is.True); Assert.That(s.IsMotionCheck, Is.False); Assert.That(s.IsCutBench, Is.True);
            Assert.That(s.motionCheckFrames, Is.EqualTo(12)); Assert.That(s.motionCheckHold, Is.EqualTo(12));
            Assert.That(s.Describe(), Does.Contain("MOTION VIEW"));
            Assert.That(Read(out s, out failure, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeCutBench", "1", "-zantetsuSkinBakeProbeMotionCapture"), Is.True, failure);
            Assert.That(s.motionView, Is.False);
            Assert.That(Read(out _, out failure, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeMotionCapture", "-zantetsuSkinBakeProbeMotionView"), Is.False);
            Assert.That(failure, Does.Contain("cut bench"));
            Assert.That(Read(out _, out failure, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeCutBench", "1", "-zantetsuSkinBakeProbeCutPictureFrames", "24", "-zantetsuSkinBakeProbeMotionView"), Is.False);
            Assert.That(failure, Does.Contain("MotionCapture"));
            // The view sequence starts in the frame window (TL review P3): no picture frames, a performance run, or no warmup frame is refused.
            Assert.That(Read(out _, out failure, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeCutBench", "1", "-zantetsuSkinBakeProbeMotionCapture", "-zantetsuSkinBakeProbeMotionView"), Is.False);
            Assert.That(failure, Does.Contain("frame window"));
            Assert.That(Read(out _, out failure, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeCutBench", "1", "-zantetsuSkinBakeProbeCutPictureFrames", "24", "-zantetsuSkinBakeProbeCutNoVerify", "-zantetsuSkinBakeProbeMotionCapture", "-zantetsuSkinBakeProbeMotionView"), Is.False);
            Assert.That(failure, Does.Contain("frame window"));
            Assert.That(Read(out _, out failure, "-zantetsuSkinBakeProbe", "C:/x", "-zantetsuSkinBakeProbeCutBench", "1", "-zantetsuSkinBakeProbeCutPictureFrames", "24", "-zantetsuSkinBakeProbeBenchWarmup", "0", "-zantetsuSkinBakeProbeMotionCapture", "-zantetsuSkinBakeProbeMotionView"), Is.False);
            Assert.That(failure, Does.Contain("frame window"));
            SkinBakeProbeSettings q = Zantetsu.Quest.QuestSkinBakeProbe.SettingsOf("{\"skinBakeProbeCutBench\":1,\"skinBakeProbeCutPictureFrames\":24,\"skinBakeProbeMotionCapture\":true,\"skinBakeProbeMotionView\":true,\"skinBakeProbeMotionCheckFrames\":12}", "C:/x", out failure);
            Assert.That(q, Is.Not.Null, failure);
            Assert.That(q.motionView, Is.True); Assert.That(q.motionCapture, Is.True); Assert.That(q.motionCheckFrames, Is.EqualTo(12));
            Assert.That(Zantetsu.Quest.QuestSkinBakeProbe.SettingsOf("{\"skinBakeProbeCutBench\":1,\"skinBakeProbeCutPictureFrames\":24,\"skinBakeProbeMotionView\":true}", "C:/x", out failure), Is.Null);
            Assert.That(failure, Does.Contain("MotionCapture"));
            Assert.That(Zantetsu.Quest.QuestSkinBakeProbe.SettingsOf("{\"skinBakeProbeCutBench\":1,\"skinBakeProbeMotionCapture\":true,\"skinBakeProbeMotionView\":true}", "C:/x", out failure), Is.Null);
            Assert.That(failure, Does.Contain("frame window"));
            Assert.That(Zantetsu.Quest.QuestSkinBakeProbe.SettingsOf("{\"skinBakeProbeCutBench\":1,\"skinBakeProbeCutPictureFrames\":24,\"skinBakeProbeBenchWarmup\":0,\"skinBakeProbeMotionCapture\":true,\"skinBakeProbeMotionView\":true}", "C:/x", out failure), Is.Null);
            Assert.That(failure, Does.Contain("frame window"));
            Assert.That(Zantetsu.Quest.QuestSkinBakeProbe.SettingsOf("{\"skinBakeProbeMotionCapture\":true,\"skinBakeProbeMotionView\":true}", "C:/x", out failure), Is.Null);
            Assert.That(failure, Does.Contain("cut bench"));
        }

        [Test]
        public void MotionCheck_Sequence_IsDecidedByTheIndex_HoldsAreZero_MovesStepOnce_ReturnsToZero()
        {
            const int hold = 12, frames = 6; const float step = 0.01f;
            int length = SkinBakeProbeMotionCheck.Length(hold, frames);
            Assert.That(length, Is.EqualTo(48));
            for (int i = 0; i < hold; i++) { Assert.That(SkinBakeProbeMotionCheck.OffsetX(i, hold, frames, step), Is.EqualTo(0f), "hold1 " + i); Assert.That(SkinBakeProbeMotionCheck.PhaseOf(i, hold, frames), Is.EqualTo("hold1")); }
            for (int k = 1; k <= frames; k++) Assert.That(SkinBakeProbeMotionCheck.OffsetX(hold + k - 1, hold, frames, step), Is.EqualTo(k * step).Within(1e-7f), "right " + k);
            for (int i = hold + frames; i < 2 * hold + frames; i++) Assert.That(SkinBakeProbeMotionCheck.OffsetX(i, hold, frames, step), Is.EqualTo(frames * step).Within(1e-7f), "hold2 " + i);
            for (int k = 1; k <= frames; k++) Assert.That(SkinBakeProbeMotionCheck.OffsetX(2 * hold + frames + k - 1, hold, frames, step), Is.EqualTo((frames - k) * step).Within(1e-7f), "left " + k);
            for (int i = 2 * hold + 2 * frames; i < length + 5; i++) Assert.That(SkinBakeProbeMotionCheck.OffsetX(i, hold, frames, step), Is.EqualTo(0f), "hold3/after " + i);
            // One step a frame while moving, never two: the difference between consecutive indices is 0 or exactly one step.
            for (int i = 1; i < length; i++)
            {
                float d = SkinBakeProbeMotionCheck.OffsetX(i, hold, frames, step) - SkinBakeProbeMotionCheck.OffsetX(i - 1, hold, frames, step);
                Assert.That(Mathf.Abs(d), Is.EqualTo(0f).Within(1e-7f).Or.EqualTo(step).Within(1e-7f), "step at " + i);
            }
            // The same index asked twice (LateUpdate, then onBeforeRender) gives the same offset: the function has no state.
            Assert.That(SkinBakeProbeMotionCheck.OffsetX(hold + 2, hold, frames, step), Is.EqualTo(SkinBakeProbeMotionCheck.OffsetX(hold + 2, hold, frames, step)));
            int wanted = 0; for (int i = 0; i < length; i++) if (SkinBakeProbeMotionCheck.Wanted(i, hold, frames)) wanted++;
            Assert.That(wanted, Is.EqualTo(2 + frames + 3 + frames + 3), "the captured frames: 2 of hold1, the moves, 2 + 1 of hold2, 3 of hold3");
            Assert.That(SkinBakeProbeMotionCheck.Wanted(-1, hold, frames), Is.False);
            Assert.That(SkinBakeProbeMotionCheck.Wanted(length, hold, frames), Is.False);
        }

        [Test]
        public void QuestConfiguration_CutPlane_PictureFrames_Hold_Shadows_ReachTheSettings()
        {
            SkinBakeProbeSettings s = Zantetsu.Quest.QuestSkinBakeProbe.SettingsOf("{\"skinBakeProbeCutBench\":4,\"skinBakeProbeCutPlane\":\"horizontal\",\"skinBakeProbeCutPictureFrames\":8,\"skinBakeProbeHoldCpuReady\":5,\"skinBakeProbeShadows\":\"nocast\"}", "C:/x", out string failure);
            Assert.That(s, Is.Not.Null, failure);
            Assert.That(s.cutBodies, Is.EqualTo(4));
            Assert.That(s.cutPlane, Is.EqualTo("horizontal"));
            Assert.That(s.cutPictureFrames, Is.EqualTo(8));
            Assert.That(s.holdCpuReady, Is.EqualTo(5));
            Assert.That(s.shadows, Is.EqualTo("nocast"));
            s = Zantetsu.Quest.QuestSkinBakeProbe.SettingsOf("{\"skinBakeProbeCutBench\":4}", "C:/x", out failure);
            Assert.That(s, Is.Not.Null, failure);
            Assert.That(s.cutPlane, Is.EqualTo("legacy"));
            Assert.That(s.cutPictureFrames, Is.EqualTo(0));
            Assert.That(s.holdCpuReady, Is.EqualTo(0));
            Assert.That(s.shadows, Is.EqualTo("both"));
            Assert.That(Zantetsu.Quest.QuestSkinBakeProbe.SettingsOf("{\"skinBakeProbeCutPlane\":\"vertical\"}", "C:/x", out failure), Is.Null);
            Assert.That(failure, Does.Contain("skinBakeProbeCutPlane"));
            Assert.That(Zantetsu.Quest.QuestSkinBakeProbe.SettingsOf("{\"skinBakeProbeHoldCpuReady\":99}", "C:/x", out failure), Is.Null);
            Assert.That(failure, Does.Contain("skinBakeProbeHoldCpuReady"));
        }
    }
}
