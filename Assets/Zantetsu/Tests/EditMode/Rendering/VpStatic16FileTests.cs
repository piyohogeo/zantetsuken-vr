using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using Zantetsu.MeshCut.ReferenceIntake;

namespace Zantetsu.Rendering.Tests
{
    public class VpStatic16FileTests
    {
        private static Mesh Tetrahedron()
        {
            return new Mesh { vertices = new[]{ Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                normals = Enumerable.Repeat(Vector3.up, 4).ToArray(),
                uv = Enumerable.Repeat(new Vector2(63.5f/256, 31.5f/256), 4).ToArray(),
                triangles = new[]{0,2,1,0,1,3,0,3,2,1,2,3} };
        }
        private static byte[] Pack(Mesh mesh)
        {
            using var stream = new MemoryStream();
            VpStatic16Writer.Write(stream, mesh, new[]{0,1,2,3}, 4, new[]{7});
            return stream.ToArray();
        }
        [Test]
        public void RoundTrip_Uses16BytePayload_NonzeroBase_AndCallerOwnedStream()
        {
            var mesh = Tetrahedron();
            try
            {
                byte[] data = Pack(mesh);
                Assert.That(data.Length, Is.EqualTo(28 + 4*16 + 12*4 + 4*4 + 12));
                using var storage = new VpCpuGeometryStorage(32, 96, 8, 8, 8, Allocator.Persistent);
                using var stream = new MemoryStream(data);
                Assert.That(VpStatic16File.TryAppendCuttable(stream, storage, out _, out var failure), Is.True, failure);
                stream.Position = 0;
                Assert.That(VpStatic16File.TryAppendCuttable(stream, storage, out var second, out failure), Is.True, failure);
                Assert.That(second.cutInputAccepted, Is.True);
                Assert.That(storage.VertexCount, Is.EqualTo(8));
                for (int i=0; i<4; i++) Assert.That(storage.Vertices[4+i], Is.EqualTo(storage.Vertices[i]));
                Assert.That(storage.TryGetTopology(second, out var ids, out int count), Is.True);
                Assert.That(count, Is.EqualTo(4)); Assert.That(ids.ToArray(), Is.EqualTo(new[]{0,1,2,3}));
                Assert.That(storage.TryGetSubmeshes(second,out var submeshes),Is.True);
                Assert.That(submeshes[0].materialIndex,Is.EqualTo(7));
                Assert.That(storage.TryAcquireIndexReadLease(second.indexRange,out var lease,out _),Is.True);
                try
                {
                    Assert.That(storage.TryGetLeasedIndexSpan(lease,out var indices,out _,out int indexCount),Is.True);
                    Assert.That(indexCount,Is.EqualTo(12));
                    Assert.That(indices.ToArray(),Is.EqualTo(mesh.triangles.Select(i=>(uint)(i+4)).ToArray()));
                }
                finally { Assert.That(storage.TryReleaseIndexReadLease(lease),Is.True); }
                Assert.That(stream.CanRead, Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
        [TestCase("magic")][TestCase("stride")][TestCase("short")][TestCase("extra")]
        [TestCase("index")][TestCase("normal")][TestCase("topology")][TestCase("submesh")][TestCase("capacity")]
        public void InvalidFile_IsRejectedWithoutTakingStorageRoom(string kind)
        {
            var mesh = Tetrahedron();
            try
            {
                byte[] data = Pack(mesh);
                if (kind == "magic") data[0] = 0;
                if (kind == "stride") data[8] = 32;
                if (kind == "short") Array.Resize(ref data, data.Length-1);
                if (kind == "extra") Array.Resize(ref data, data.Length+1);
                if (kind == "index") data[28+64] = 255;
                if (kind == "normal") data[28+12] = 128;
                if (kind == "topology") data[28+64+48] = 255;
                if (kind == "submesh") data[data.Length-12] = 1;
                using var storage = new VpCpuGeometryStorage(kind=="capacity" ? 3 : 16, 96, 8, 8, 8, Allocator.Persistent);
                int free = storage.FreeVertexRoom;
                using var stream = new MemoryStream(data);
                Assert.That(VpStatic16File.TryAppendCuttable(stream, storage, out _, out var failure), Is.False);
                Assert.That(failure, Is.Not.Empty);
                Assert.That(storage.FreeVertexRoom, Is.EqualTo(free)); Assert.That(storage.VertexCount, Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
        [TestCase("uv")][TestCase("topology")][TestCase("skin")]
        public void UnsupportedAuthoringInput_WritesNoBytes(string kind)
        {
            var mesh = Tetrahedron();
            try
            {
                if (kind=="uv") mesh.uv = Enumerable.Repeat(new Vector2(.5f,.5f),4).ToArray();
                if (kind=="skin") mesh.bindposes = new[]{Matrix4x4.identity};
                using var stream = new MemoryStream();
                Assert.Throws<ArgumentException>(() => VpStatic16Writer.Write(stream, mesh, kind=="topology" ? new[]{0,0,0,0} : new[]{0,1,2,3},4,new[]{0}));
                Assert.That(stream.Length, Is.Zero);
            }
            finally { UnityEngine.Object.DestroyImmediate(mesh); }
        }
    }
}
