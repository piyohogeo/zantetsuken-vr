using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.Rendering.StandaloneTests
{
    public class Compact16uvStoragePlayerTests
    {
        [Test]
        public void ProductStorage_DirectBurstCut_Recut_AndOffsetGpuUploadUseTheSame16Bytes()
        {
            Assert.That(UnsafeUtility.SizeOf<VpRenderVertex>(), Is.EqualTo(16));
            Vector3[] p = { new(-1,-1,-1), new(1,-1,-1), new(1,1,-1), new(-1,1,-1), new(-1,-1,1), new(1,-1,1), new(1,1,1), new(-1,1,1) };
            int[][] faces = { new[]{0,3,2,1}, new[]{4,5,6,7}, new[]{0,1,5,4}, new[]{3,7,6,2}, new[]{0,4,7,3}, new[]{1,2,6,5} };
            var vertices = p.Select(x => new VpRenderVertex { position=x, normal=x.normalized, uv0=new Vector2((x.x > 0 ? 191.5f : 63.5f)/256f, (x.y > 0 ? 191.5f : 63.5f)/256f) }).ToArray();
            uint[] indices = faces.SelectMany(f => new[]{(uint)f[0],(uint)f[1],(uint)f[2],(uint)f[0],(uint)f[2],(uint)f[3]}).ToArray();
            int[] topology = Enumerable.Range(0, 8).ToArray();
            var submeshes = new[]{ new VpGeometrySubmesh(0, indices.Length, 0) };
            using var storage = new VpCpuGeometryStorage(2048, 8192, 32, 128, 128, Allocator.Persistent);
            // Keep a prefix so the cut, lease and GPU upload cannot accidentally rely on a zero vertex base.
            Assert.That(storage.TryAppendCuttable(vertices, indices, topology, 8, submeshes, out _, out _), Is.True);
            Assert.That(storage.TryAppendCuttable(vertices, indices, topology, 8, submeshes, out var geometry, out _), Is.True);
            using var gpu = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2048, 16);
            using var wrongStride = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2048, 32);
            foreach (float4 plane in new[]{ new float4(1,0,0,-.23f), new float4(0,1,0,-.17f), new float4(0,0,1,-.19f) })
            {
                VpRenderVertex[] before = storage.Vertices.ToArray();
                Assert.That(VpStorageCutInput.TryAcquire(storage, geometry, out var input), Is.True);
                VpStorageCutResult result;
                using (input) Assert.That(VpStorageCut.TryExecute(storage, input, plane, default, out result), Is.True);
                Assert.That(result.status, Is.EqualTo(VpStorageCutStatus.Ok));
                Assert.That(result.kernel.executedManaged, Is.Zero, "real product Burst Direct Call");
                Assert.That(result.kernel.newVertexCount, Is.GreaterThan(0));
                Assert.That(result.kernel.openContourCount, Is.Zero);
                var committed = storage.Vertices;
                for (int i=0; i<before.Length; i++) Assert.That(committed[i], Is.EqualTo(before[i]), "old input remains byte-identical");
                Assert.That(VpStoredGeometryTransfer.TryUploadCommittedVertices(storage, wrongStride, 8, committed.Length-8, out int refused), Is.False);
                Assert.That(refused, Is.Zero);
                Assert.That(VpStoredGeometryTransfer.TryUploadCommittedVertices(storage, gpu, 8, committed.Length-8, out int written), Is.True);
                Assert.That(written, Is.EqualTo(committed.Length-8));
                var actual = new VpRenderVertex[written]; gpu.GetData(actual, 0, 8, written);
                for(int i=0; i<written; i++) Assert.That(actual[i], Is.EqualTo(committed[8+i]), "all packed fields round-trip");
                Assert.That(Enumerable.Range(before.Length, committed.Length-before.Length).Any(i=>committed[i].u==247 && committed[i].v==247), Is.True, "new cap slot");
                geometry = result.positive.geometry;
            }
        }
    }
}
