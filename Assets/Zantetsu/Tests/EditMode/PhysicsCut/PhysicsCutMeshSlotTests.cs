using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.PhysicsCut.Tests
{
    public class PhysicsCutMeshSlotTests
    {
        [Test]
        public void ParallelBakePreservesMeshMetadataAndMarksOnlyScheduledElements()
        {
            const int count = 17;
            var slots = new NativeArray<PhysicsCutMeshSlot>(count, Allocator.TempJob);
            var expected = new PhysicsCutMeshSlot[count];
            var meshes = new Mesh[2];
            JobHandle handle = default;
            try
            {
                for (int i = 0; i < meshes.Length; i++)
                {
                    meshes[i] = new Mesh
                    {
                        vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                        triangles = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 },
                    };
                }

                for (int i = 0; i < count; i++)
                {
                    expected[i] = new PhysicsCutMeshSlot
                    {
                        bounds = new float3x2(new float3(i, -i, 0.5f), new float3(i + 1, i + 2, 3)),
                        vertexCount = i + 4,
                        id = i < meshes.Length ? meshes[i].GetEntityId() : 0,
                    };
                    slots[i] = expected[i];
                }

                // The last slot never runs, so its zero must still distinguish it from a returned bake. Other
                // elements include both real meshes (each baked once) and empty slots, with different metadata.
                var job = new BakeJob { meshSlots = slots, cooking = PhysicsCutCook.DefaultCooking };
                handle = job.Schedule(count - 1, 1);
                handle.Complete();
                for (int i = 0; i < count; i++)
                {
                    PhysicsCutMeshSlot actual = slots[i];
                    Assert.That(actual.bounds, Is.EqualTo(expected[i].bounds), "bounds " + i);
                    Assert.That(actual.vertexCount, Is.EqualTo(expected[i].vertexCount), "vertices " + i);
                    Assert.That(actual.id, Is.EqualTo(expected[i].id), "id " + i);
                    Assert.That(actual.bakeDone, Is.EqualTo(i < count - 1 ? 1 : 0), "completion " + i);
                }
            }
            finally
            {
                handle.Complete();
                slots.Dispose();
                foreach (Mesh mesh in meshes)
                {
                    if (mesh != null)
                    {
                        Object.DestroyImmediate(mesh);
                    }
                }
            }
        }
    }
}
