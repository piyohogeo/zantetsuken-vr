using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// The static Stage 3 display of geometries a <see cref="VpCpuGeometryStorage"/> owns: the buffers are made and
    /// written once before any draw, the commands keep the submesh ranges and each is paired with the Material its
    /// stored material index resolves to, and the display releases what it made exactly once. Nothing here says
    /// anything about updating or replacing a buffer while it is being drawn; the display is not for that.
    /// </summary>
    public class VpStoredGeometryDisplayTests
    {
        private const string ForwardShaderName = "Zantetsu/VP Indexed Indirect Unlit";
        private const string ShadowShaderName = "Zantetsu/VP Indexed Indirect Shadow Caster";
        private const int ControlPoints = 8;
        private const int RenderVertices = 24;
        private const int IndexCount = 36;
        private const int SideIndices = 24;
        private const int EndIndices = 12;
        private const int SideMaterial = 7;
        private const int EndMaterial = 2;

        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();

        [TearDown]
        public void DestroyObjects()
        {
            foreach (UnityEngine.Object tracked in _objects)
            {
                if (tracked != null)
                {
                    UnityEngine.Object.DestroyImmediate(tracked);
                }
            }

            _objects.Clear();
        }

        private T Track<T>(T tracked) where T : UnityEngine.Object
        {
            _objects.Add(tracked);
            return tracked;
        }

        private static readonly float3[] k_controlPoints =
        {
            new float3(-1.0f, 0.0f, -1.0f), new float3(1.2f, 0.0f, -1.0f), new float3(1.1f, 0.0f, 0.9f), new float3(-0.8f, 0.0f, 1.0f),
            new float3(-0.5f, 1.3f, -0.4f), new float3(0.7f, 1.3f, -0.6f), new float3(0.6f, 1.3f, 0.5f), new float3(-0.3f, 1.3f, 0.6f),
        };

        private static readonly (int[] cycle, int submesh)[] k_faces =
        {
            (new[] { 0, 4, 5, 1 }, 0), (new[] { 1, 5, 6, 2 }, 0), (new[] { 2, 6, 7, 3 }, 0), (new[] { 3, 7, 4, 0 }, 0),
            (new[] { 0, 1, 2, 3 }, 1), (new[] { 4, 7, 6, 5 }, 1),
        };

        private sealed class Prepared
        {
            public VpRenderVertex[] Vertices;
            public uint[] Indices;
            public int[] TopologyOfVertex;
            public VpGeometrySubmesh[] Submeshes;
            public int TopologyVertexCount;
        }

        private static Prepared BuildPrepared()
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                int start = indices.Count;
                for (int f = 0; f < k_faces.Length; f++)
                {
                    if (k_faces[f].submesh != submesh)
                    {
                        continue;
                    }

                    int[] c = k_faces[f].cycle;
                    float3 n = math.normalize(math.cross(k_controlPoints[c[1]] - k_controlPoints[c[0]], k_controlPoints[c[2]] - k_controlPoints[c[0]]));
                    uint b = (uint)vertices.Count;
                    var uv = new[] { new float2(0.05f, 0.1f), new float2(0.95f, 0.1f), new float2(0.95f, 0.9f), new float2(0.05f, 0.9f) };
                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex
                        {
                            position = k_controlPoints[c[k]],
                            normal = n,
                            uv0 = uv[k] + new float2(f * 0.013f, f * 0.021f),
                        });
                        topology.Add(c[k]);
                    }

                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }

                submeshes.Add(new VpGeometrySubmesh(start, indices.Count - start, submesh == 0 ? SideMaterial : EndMaterial));
            }

            return new Prepared
            {
                Vertices = vertices.ToArray(),
                Indices = indices.ToArray(),
                TopologyOfVertex = topology.ToArray(),
                Submeshes = submeshes.ToArray(),
                TopologyVertexCount = ControlPoints,
            };
        }

        private static VpCpuGeometryStorage NewStorage()
        {
            return new VpCpuGeometryStorage(2048, 8192, 32, 128, 128, Allocator.Persistent);
        }

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage, Prepared prepared)
        {
            Assert.That(
                storage.TryAppendPrepared(prepared.Vertices, prepared.Indices, prepared.TopologyOfVertex, prepared.TopologyVertexCount, prepared.Submeshes, out VpStoredGeometry geometry),
                Is.True,
                "append prepared");
            return geometry;
        }

        /// <summary>For the one subject these display tests cut: appended as a cut input, through the gate.</summary>
        private static VpStoredGeometry AppendCuttable(VpCpuGeometryStorage storage, Prepared prepared)
        {
            Assert.That(
                storage.TryAppendCuttable(prepared.Vertices, prepared.Indices, prepared.TopologyOfVertex, prepared.TopologyVertexCount, prepared.Submeshes, out VpStoredGeometry geometry, out _),
                Is.True,
                "append as a cut input");
            return geometry;
        }

        private static float4 TiltedPlane()
        {
            float3 centre = float3.zero;
            foreach (float3 p in k_controlPoints)
            {
                centre += p;
            }

            centre /= k_controlPoints.Length;
            float3 n = math.normalize(new float3(0.37f, 0.61f, -0.7f));
            return new float4(n, -math.dot(n, centre + new float3(0.0071f, -0.0233f, 0.0119f)));
        }

        private static VpStorageCutResult Cut(VpCpuGeometryStorage storage, VpStoredGeometry geometry, float4 plane)
        {
            Assert.That(VpStorageCutInput.TryAcquire(storage, geometry, out VpStorageCutInput input), Is.True, "acquire input");
            using (input)
            {
                Assert.That(VpStorageCut.TryExecute(storage, input, plane, out VpStorageCutResult result), Is.True, "cut");
                Assert.That(result.status, Is.EqualTo(VpStorageCutStatus.Ok));
                return result;
            }
        }

        private Material NewMaterial(string name)
        {
            Shader shader = Shader.Find(ForwardShaderName);
            Assert.That(shader, Is.Not.Null, ForwardShaderName + " is present");
            return Track(new Material(shader) { name = name });
        }

        [Test]
        public void TheForwardShader_TakesAnOptionalBaseMapAndKeepsTheColourOnlyLook()
        {
            Shader forward = Shader.Find(ForwardShaderName);
            Assert.That(forward, Is.Not.Null, "the forward shader is present");
            Assert.That(forward.isSupported, Is.True, "and supported");
            Assert.That(forward.name, Is.Not.EqualTo("Hidden/InternalErrorShader"));

            var material = Track(new Material(forward));
            Assert.That(material.HasProperty("_BaseMap"), Is.True, "the base map exists");
            Assert.That(material.HasProperty("_BaseColor"), Is.True, "and so does the colour it multiplies into");

            // A caller that sets only a colour is exactly where it was before the texture was added: no texture is
            // assigned, and the shader's own default stands in for one.
            Assert.That(material.GetTexture("_BaseMap"), Is.Null, "nothing is bound until the caller binds it");
            material.SetColor("_BaseColor", Color.green);
            Assert.That(material.GetColor("_BaseColor"), Is.EqualTo(Color.green));

            Shader shadow = Shader.Find(ShadowShaderName);
            Assert.That(shadow, Is.Not.Null, "the shadow caster is untouched and still present");
            Assert.That(shadow.isSupported, Is.True);
        }

        [Test]
        public void ADisplayOfSeveralGeometries_LaysOutIndicesAndCommandsInOrder()
        {
            Prepared prepared = BuildPrepared();
            Material side = NewMaterial("side");
            Material end = NewMaterial("end");
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry first = Append(storage, prepared);
                VpStoredGeometry second = Append(storage, prepared);
                var materials = new Dictionary<int, Material> { { SideMaterial, side }, { EndMaterial, end } };

                Assert.That(
                    VpStoredGeometryDisplay.TryCreate(storage, new[] { first, second }, new[] { Matrix4x4.identity, Matrix4x4.Translate(Vector3.right) }, materials, out VpStoredGeometryDisplay display),
                    Is.True,
                    "create");
                using (display)
                {
                    Assert.That(display.EntryCount, Is.EqualTo(2), "one entry per geometry");
                    Assert.That(display.CommandCount, Is.EqualTo(4), "one command per submesh of each");

                    VpStoredGeometryDisplay.Entry a = display.GetEntry(0);
                    VpStoredGeometryDisplay.Entry b = display.GetEntry(1);
                    Assert.That(a.indexBase, Is.Zero, "the first geometry starts the index buffer");
                    Assert.That(b.indexBase, Is.EqualTo(IndexCount), "the second follows it with no gap");
                    Assert.That(a.commandStart, Is.Zero);
                    Assert.That(a.commandCount, Is.EqualTo(2));
                    Assert.That(b.commandStart, Is.EqualTo(2), "its commands follow the first geometry's");
                    Assert.That(b.commandCount, Is.EqualTo(2));
                    Assert.That(a.localBounds.size, Is.Not.EqualTo(Vector3.zero), "the bounds are measured");

                    // each command carries the Material its own stored material index resolves to, never its ordinal
                    Assert.That(display.GetCommandMaterial(0), Is.SameAs(side), "geometry 0 submesh 0");
                    Assert.That(display.GetCommandMaterial(1), Is.SameAs(end), "geometry 0 submesh 1");
                    Assert.That(display.GetCommandMaterial(2), Is.SameAs(side), "geometry 1 submesh 0");
                    Assert.That(display.GetCommandMaterial(3), Is.SameAs(end), "geometry 1 submesh 1");
                }
            }
        }

        [Test]
        public void TheThreeGeometriesOfACut_AreShownTogether()
        {
            Prepared prepared = BuildPrepared();
            Material side = NewMaterial("side");
            Material end = NewMaterial("end");
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry uncut = AppendCuttable(storage, prepared);

                // the cut is finished before anything is drawn: that is the whole shape of this display
                VpStorageCutResult cut = Cut(storage, uncut, TiltedPlane());
                Assert.That(cut.positive.IsProduced && cut.negative.IsProduced, Is.True, "both sides");

                var materials = new Dictionary<int, Material> { { SideMaterial, side }, { EndMaterial, end } };
                var geometries = new[] { uncut, cut.positive.geometry, cut.negative.geometry };
                var transforms = new[] { Matrix4x4.Translate(Vector3.left * 3f), Matrix4x4.identity, Matrix4x4.Translate(Vector3.right * 3f) };
                Assert.That(VpStoredGeometryDisplay.TryCreate(storage, geometries, transforms, materials, out VpStoredGeometryDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.EntryCount, Is.EqualTo(3));
                    Assert.That(display.CommandCount, Is.EqualTo(6), "two submeshes each");
                    int expectedBase = 0;
                    for (int e = 0; e < display.EntryCount; e++)
                    {
                        VpStoredGeometryDisplay.Entry entry = display.GetEntry(e);
                        Assert.That(entry.indexBase, Is.EqualTo(expectedBase), "entry " + e + " follows the last");
                        Assert.That(storage.TryGetIndexState(entry.geometry.indexRange, out _, out _, out int published), Is.True);
                        expectedBase += published;
                    }

                    // the cut children keep the source material mapping of their parent
                    Assert.That(display.GetCommandMaterial(2), Is.SameAs(side), "positive submesh 0");
                    Assert.That(display.GetCommandMaterial(3), Is.SameAs(end), "positive submesh 1");
                    Assert.That(display.GetCommandMaterial(4), Is.SameAs(side), "negative submesh 0");
                    Assert.That(display.GetCommandMaterial(5), Is.SameAs(end), "negative submesh 1");
                }
            }
        }

        [Test]
        public void ARefusedCreation_YieldsNoDisplayAndLeavesTheStorageAlone()
        {
            Prepared prepared = BuildPrepared();
            Material side = NewMaterial("side");
            using (VpCpuGeometryStorage storage = NewStorage())
            using (VpCpuGeometryStorage other = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                VpStoredGeometry foreign = Append(other, prepared);
                int vertexCount = storage.VertexCount;
                var complete = new Dictionary<int, Material> { { SideMaterial, side }, { EndMaterial, NewMaterial("end") } };

                // a material index nobody answers for: refused outright, with no display and nothing half-built
                var missing = new Dictionary<int, Material> { { SideMaterial, side } };
                Assert.That(VpStoredGeometryDisplay.TryCreate(storage, new[] { subject }, new[] { Matrix4x4.identity }, missing, out VpStoredGeometryDisplay noMaterial), Is.False, "a missing material");
                Assert.That(noMaterial, Is.Null);

                var nullMaterial = new Dictionary<int, Material> { { SideMaterial, side }, { EndMaterial, null } };
                Assert.That(VpStoredGeometryDisplay.TryCreate(storage, new[] { subject }, new[] { Matrix4x4.identity }, nullMaterial, out _), Is.False, "a null material");

                Assert.That(VpStoredGeometryDisplay.TryCreate(storage, new[] { foreign }, new[] { Matrix4x4.identity }, complete, out _), Is.False, "another storage's geometry");
                Assert.That(VpStoredGeometryDisplay.TryCreate(storage, new[] { subject }, new Matrix4x4[0], complete, out _), Is.False, "a transform count that disagrees");
                Assert.That(VpStoredGeometryDisplay.TryCreate(storage, new VpStoredGeometry[0], new Matrix4x4[0], complete, out _), Is.False, "nothing to show");
                Assert.That(VpStoredGeometryDisplay.TryCreate(null, new[] { subject }, new[] { Matrix4x4.identity }, complete, out _), Is.False, "a null storage");
                Assert.That(VpStoredGeometryDisplay.TryCreate(storage, new[] { subject }, new[] { Matrix4x4.identity }, null, out _), Is.False, "no material map");

                Assert.That(storage.VertexCount, Is.EqualTo(vertexCount), "the storage is untouched by the refusals");

                // and after all that, a complete request still succeeds: nothing was left occupying the way
                Assert.That(VpStoredGeometryDisplay.TryCreate(storage, new[] { subject }, new[] { Matrix4x4.identity }, complete, out VpStoredGeometryDisplay display), Is.True, "a complete request");
                display.Dispose();
            }
        }

        [Test]
        public void TheDisplay_ReleasesWhatItMadeOnceAndRefusesUseAfterThat()
        {
            Prepared prepared = BuildPrepared();
            var materials = new Dictionary<int, Material> { { SideMaterial, NewMaterial("side") }, { EndMaterial, NewMaterial("end") } };
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(VpStoredGeometryDisplay.TryCreate(storage, new[] { subject }, new[] { Matrix4x4.identity }, materials, out VpStoredGeometryDisplay display), Is.True, "create");
                Assert.That(display.IsDisposed, Is.False);

                // the owner's ordinary teardown: stop asking for draws, then release once
                display.Dispose();
                Assert.That(display.IsDisposed, Is.True);
                display.Dispose();
                Assert.That(display.IsDisposed, Is.True, "disposing again does nothing");

                Assert.Throws<ObjectDisposedException>(() => display.RenderForward(0), "no draw is issued after release");
                Assert.Throws<ObjectDisposedException>(() => display.GetEntry(0), "nor is its layout readable");
                Assert.Throws<ObjectDisposedException>(() => display.GetCommandMaterial(0), "nor its materials");

                // the storage outlives the display and is unchanged by it
                Assert.That(storage.TryGetSubmeshes(subject, out _), Is.True, "the geometry still reads");
            }
        }

        [Test]
        public void CommandsThatShareAMaterial_AreStillOneCommandEach()
        {
            // Unity applies one Material per call, so the display never relies on a single call picking a material per
            // command: the commands stay one per submesh and the caller's runs decide how many calls there are.
            Prepared prepared = BuildPrepared();
            Material only = NewMaterial("only");
            var materials = new Dictionary<int, Material> { { SideMaterial, only }, { EndMaterial, only } };
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(VpStoredGeometryDisplay.TryCreate(storage, new[] { subject }, new[] { Matrix4x4.identity }, materials, out VpStoredGeometryDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.CommandCount, Is.EqualTo(2), "the submesh ranges are kept even when the material is shared");
                    Assert.That(display.GetCommandMaterial(0), Is.SameAs(only));
                    Assert.That(display.GetCommandMaterial(1), Is.SameAs(only));
                    Assert.That(display.GetEntry(0).commandCount, Is.EqualTo(2));
                }
            }
        }
    }
}
