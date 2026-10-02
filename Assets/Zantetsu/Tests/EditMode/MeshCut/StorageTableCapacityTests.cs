using NUnit.Framework;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using Zantetsu.PhysicsCut;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The storage's fixed bookkeeping tables (2026-09-29): the profile's defaults and the shared sandbox profile give room
    /// well past the old 512 descriptors / 2048 submeshes / 2048 vertex blocks, a storage made with them takes geometries
    /// past the old limits, and at its own limit it still refuses (the common end stays).
    /// </summary>
    public class StorageTableCapacityTests
    {
        private const string SharedProfilePath = "Assets/Zantetsu/Settings/CutWorldSandboxProfile.asset";

        [Test]
        public void TheDefaultsAndTheSharedProfile_HoldTheRaisedTables()
        {
            var fresh = ScriptableObject.CreateInstance<CutWorldProfile>();
            try
            {
                Assert.That(fresh.GeometryDescriptorCapacity, Is.EqualTo(8192));
                Assert.That(fresh.SubmeshCapacity, Is.EqualTo(32768));
                Assert.That(fresh.VertexBlockCapacity, Is.EqualTo(65536));
                Assert.That(fresh.SystemConstraintCapacity, Is.EqualTo(8192), "a count compared at acceptance; nothing is allocated for it");
                Assert.That(fresh.ChainDepth, Is.EqualTo(256), "the snapshot's own work arrays; not grown, so sized past play");
                Assert.That(fresh.IsUsable(out string why), Is.True, why);
            }
            finally
            {
                Object.DestroyImmediate(fresh);
            }

            var shared = AssetDatabase.LoadAssetAtPath<CutWorldProfile>(SharedProfilePath);
            Assert.That(shared, Is.Not.Null);
            Assert.That(shared.GeometryDescriptorCapacity, Is.EqualTo(8192));
            Assert.That(shared.SubmeshCapacity, Is.EqualTo(32768));
            Assert.That(shared.VertexBlockCapacity, Is.EqualTo(65536));
            Assert.That(shared.ChainDepth, Is.EqualTo(256));
        }

        // A closed tetrahedron, wound outward: one submesh, one vertex block, one index descriptor per append.
        private static readonly Vector3[] Corners = { new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1) };
        private static readonly uint[] Triangles = { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };

        private static bool Append(VpCpuGeometryStorage storage)
        {
            var vertices = new VpRenderVertex[4];
            for (int i = 0; i < 4; i++) vertices[i] = new VpRenderVertex { position = Corners[i], normal = Vector3.up };
            return storage.TryAppendCuttable(vertices, Triangles, new[] { 0, 1, 2, 3 }, 4, new[] { new VpGeometrySubmesh(0, Triangles.Length, 0) },
                out VpStoredGeometry _, out VpCutInputVerdict _);
        }

        [Test]
        public void AStorageAtTheProfilesCapacities_TakesGeometriesPastTheOldLimits_AndStillRefusesAtItsOwn()
        {
            const int Geometries = 3000;   // past 2048 blocks and submeshes, past 512 descriptors
            var profile = ScriptableObject.CreateInstance<CutWorldProfile>();
            try
            {
                using (var storage = new VpCpuGeometryStorage(4 * (Geometries + 8), 12 * (Geometries + 8), profile.GeometryDescriptorCapacity,
                           profile.SubmeshCapacity, profile.VertexBlockCapacity, Allocator.Persistent))
                {
                    for (int i = 0; i < Geometries; i++) Assert.That(Append(storage), Is.True, "geometry " + i);
                    Assert.That(storage.VertexBlockCount, Is.EqualTo(Geometries));
                    Assert.That(storage.SubmeshCount, Is.EqualTo(Geometries));
                }
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }

            // A table at its own limit still refuses the next geometry: raising the tables did not remove the end.
            using (var small = new VpCpuGeometryStorage(4 * 64, 12 * 64, 64, 64, 8, Allocator.Persistent))
            {
                for (int i = 0; i < 8; i++) Assert.That(Append(small), Is.True);
                Assert.That(Append(small), Is.False, "the ninth block does not fit eight");
            }
        }
    }
}
