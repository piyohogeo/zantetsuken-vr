using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// Cutting what is on screen once and swapping the display to the sides it produced. The parent stays up until
    /// every child is ready, a refused or unfinished cut changes nothing, and everything this display registers or
    /// creates is given back exactly once.
    /// </summary>
    public class VpCutDisplayTests
    {
        private const int ControlPoints = 8;
        private const int RenderVertices = 24;
        private const int IndexCount = 36;
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

        private static VpCpuGeometryStorage NewStorage(int indexCapacity = 8192)
        {
            return new VpCpuGeometryStorage(2048, indexCapacity, 32, 128, 128, Allocator.Persistent);
        }

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage, Prepared prepared)
        {
            Assert.That(
                storage.TryAppendPrepared(prepared.Vertices, prepared.Indices, prepared.TopologyOfVertex, prepared.TopologyVertexCount, prepared.Submeshes, out VpStoredGeometry geometry),
                Is.True,
                "append prepared");
            return geometry;
        }

        private static float4 CrossingPlane()
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

        /// <summary>A plane the shape lies entirely on one side of: it spans y in [0, 1.3].</summary>
        private static float4 MissingPlane(bool positiveSide)
        {
            float3 n = new float3(0, 1, 0);
            float3 point = new float3(0, positiveSide ? -3f : 3f, 0);
            return new float4(n, -math.dot(n, point));
        }

        private Dictionary<int, Material> Materials()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            return new Dictionary<int, Material>
            {
                { SideMaterial, Track(new Material(shader) { name = "side" }) },
                { EndMaterial, Track(new Material(shader) { name = "end" }) },
            };
        }

        private static VpIndexRangeState IndexState(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            Assert.That(storage.TryGetIndexState(geometry.indexRange, out VpIndexRangeState state, out _, out _), Is.True, "index state");
            return state;
        }

        [Test]
        public void ACrossingCut_SwapsTheParentForTheTwoChildren()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(VpCutDisplay.TryCreate(storage, table, parent, Materials(), null, out VpCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.ShownCount, Is.EqualTo(1), "the parent alone is shown");
                    GameObject parentObject = display.GetShownObject(0);
                    Assert.That(parentObject.activeSelf, Is.True, "and it is active");
                    Assert.That(table.LiveGeometryCount, Is.EqualTo(1));
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(1));

                    Assert.That(display.TryCutOnce(CrossingPlane(), out VpCutDisplayResult result), Is.True, "cut");
                    Assert.That(result.outcome, Is.EqualTo(VpCutDisplayOutcome.Swapped));
                    Assert.That(result.cut.status, Is.EqualTo(VpStorageCutStatus.Ok));
                    Assert.That(result.cut.kernel.crossingTriangles, Is.GreaterThan(0), "the plane really cuts");
                    Assert.That(result.appendedVerticesKept, Is.Zero, "a swap leaves nothing unused");

                    // the display is the two children now, both on screen
                    Assert.That(display.ShownCount, Is.EqualTo(2), "two children");
                    Assert.That(result.shownCount, Is.EqualTo(2));
                    for (int i = 0; i < 2; i++)
                    {
                        Assert.That(display.GetShownObject(i).activeSelf, Is.True, "child " + i + " is shown");
                        Assert.That(display.GetShownObject(i).GetComponent<MeshFilter>().sharedMesh, Is.Not.Null, "child " + i + " has a mesh");
                    }

                    // the parent's display is gone and its geometry retired exactly once
                    Assert.That(parentObject == null, Is.True, "the parent object was destroyed");
                    Assert.That(IndexState(storage, parent), Is.EqualTo(VpIndexRangeState.Free), "the parent's range was retired");
                    Assert.That(table.LiveGeometryCount, Is.EqualTo(2), "only the children are registered");
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(2));
                }
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void ANonCrossingCut_KeepsTheParentAndRegistersNothingNew(bool positiveSide)
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(VpCutDisplay.TryCreate(storage, table, parent, Materials(), null, out VpCutDisplay display), Is.True, "create");
                using (display)
                {
                    GameObject parentObject = display.GetShownObject(0);
                    int vertexCount = storage.VertexCount;

                    Assert.That(display.TryCutOnce(MissingPlane(positiveSide), out VpCutDisplayResult result), Is.True, "cut");
                    Assert.That(result.outcome, Is.EqualTo(VpCutDisplayOutcome.KeptParent), "the input is reused, not replaced");
                    Assert.That(result.cut.kernel.crossingTriangles, Is.Zero, "nothing crosses");
                    Assert.That(
                        positiveSide ? result.cut.positive.IsBorrowed : result.cut.negative.IsBorrowed,
                        Is.True,
                        "the side that keeps it all borrows the input");

                    // no second owner and no second renderer for the same geometry
                    Assert.That(display.ShownCount, Is.EqualTo(1), "still one display");
                    Assert.That(display.GetShownObject(0), Is.SameAs(parentObject), "the same object");
                    Assert.That(display.GetShownGeometry(0).indexRange, Is.EqualTo(parent.indexRange), "the same geometry");
                    Assert.That(table.LiveGeometryCount, Is.EqualTo(1), "nothing new was registered");
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(1));
                    Assert.That(storage.VertexCount, Is.EqualTo(vertexCount), "and nothing was appended");
                    Assert.That(IndexState(storage, parent), Is.EqualTo(VpIndexRangeState.Published), "the parent is untouched");
                }
            }
        }

        [Test]
        public void TheChildren_KeepTheAttributesAndTheSubmeshMaterialMapping()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                Dictionary<int, Material> materials = Materials();
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(VpCutDisplay.TryCreate(storage, table, parent, materials, null, out VpCutDisplay display), Is.True, "create");
                using (display)
                {
                    Assert.That(display.TryCutOnce(CrossingPlane(), out VpCutDisplayResult result), Is.True, "cut");
                    Assert.That(result.outcome, Is.EqualTo(VpCutDisplayOutcome.Swapped));

                    NativeArray<VpRenderVertex>.ReadOnly committed = storage.Vertices;
                    for (int i = 0; i < display.ShownCount; i++)
                    {
                        VpStoredGeometry child = display.GetShownGeometry(i);
                        GameObject go = display.GetShownObject(i);
                        Mesh mesh = go.GetComponent<MeshFilter>().sharedMesh;
                        Material[] shown = go.GetComponent<MeshRenderer>().sharedMaterials;

                        Assert.That(mesh.subMeshCount, Is.EqualTo(2), "child " + i + " keeps both submeshes");
                        Assert.That(shown.Length, Is.EqualTo(2), "child " + i + " has a material per submesh");
                        Assert.That(shown[0], Is.SameAs(materials[SideMaterial]), "child " + i + " submesh 0 material");
                        Assert.That(shown[1], Is.SameAs(materials[EndMaterial]), "child " + i + " submesh 1 material");

                        // the corners are the stored ones, in order, with the attributes untouched
                        Assert.That(storage.TryAcquireIndexReadLease(child.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True);
                        uint[] indices = view.ToArray();
                        Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True);

                        Vector3[] positions = mesh.vertices;
                        var fromMesh = new List<Vector3>();
                        for (int s = 0; s < mesh.subMeshCount; s++)
                        {
                            fromMesh.AddRange(mesh.GetTriangles(s).Select(c => positions[c]));
                        }

                        var fromStorage = indices.Select(v => (Vector3)committed[(int)v].position).ToArray();
                        Assert.That(fromMesh.ToArray(), Is.EqualTo(fromStorage), "child " + i + " corners in order");
                    }
                }
            }
        }

        [Test]
        public void ARefusedCutAndACapacityRetry_LeaveTheDisplayExactlyAsItWas()
        {
            Prepared prepared = BuildPrepared();

            // an index capacity with no room for the cut's reservation
            using (VpCpuGeometryStorage tight = NewStorage(indexCapacity: IndexCount + 8))
            {
                var table = new VpGeometryReferenceTable(tight, 8, 8);
                VpStoredGeometry parent = Append(tight, prepared);
                Assert.That(VpCutDisplay.TryCreate(tight, table, parent, Materials(), null, out VpCutDisplay display), Is.True, "create");
                using (display)
                {
                    GameObject parentObject = display.GetShownObject(0);
                    Assert.That(display.TryCutOnce(CrossingPlane(), out VpCutDisplayResult result), Is.False, "the cut is refused");
                    Assert.That(result.outcome, Is.EqualTo(VpCutDisplayOutcome.CutRefused));
                    Assert.That(result.cut.status, Is.EqualTo(VpStorageCutStatus.StorageCapacity), "storage shortage, not bad input");
                    Assert.That(display.ShownCount, Is.EqualTo(1));
                    Assert.That(display.GetShownObject(0), Is.SameAs(parentObject), "the parent is still shown");
                    Assert.That(IndexState(tight, parent), Is.EqualTo(VpIndexRangeState.Published));
                    Assert.That(table.LiveGeometryCount, Is.EqualTo(1));
                }
            }

            // one attempt with figures too small: unfinished, not failed
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(VpCutDisplay.TryCreate(storage, table, parent, Materials(), null, out VpCutDisplay display), Is.True, "create");
                using (display)
                {
                    GameObject parentObject = display.GetShownObject(0);
                    var options = new VpStorageCutOptions { newVertexCapacity = 1, newIndexCapacity = 3, maxAttempts = 1 };
                    Assert.That(display.TryCutOnce(CrossingPlane(), options, out VpCutDisplayResult result), Is.False, "unfinished");
                    Assert.That(result.outcome, Is.EqualTo(VpCutDisplayOutcome.CapacityRetry));
                    Assert.That(result.cut.status, Is.EqualTo(VpStorageCutStatus.CapacityRetry));
                    Assert.That(result.cut.required.newVertexCapacity, Is.GreaterThan(options.newVertexCapacity), "it says what to reserve");
                    Assert.That(display.ShownCount, Is.EqualTo(1));
                    Assert.That(display.GetShownObject(0), Is.SameAs(parentObject), "the parent is still shown");

                    // and the display carries on: calling again with what it asked for finishes the swap
                    Assert.That(display.TryCutOnce(CrossingPlane(), result.cut.required, out VpCutDisplayResult second), Is.True, "resumed");
                    Assert.That(second.outcome, Is.EqualTo(VpCutDisplayOutcome.Swapped));
                    Assert.That(display.ShownCount, Is.EqualTo(2));
                }
            }
        }

        [Test]
        public void AFailedDisplayPreparation_KeepsTheParentAndTakesBackWhatItBuilt()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                // room for the parent and one child only, so preparing the second child fails
                var table = new VpGeometryReferenceTable(storage, 2, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(VpCutDisplay.TryCreate(storage, table, parent, Materials(), null, out VpCutDisplay display), Is.True, "create");
                using (display)
                {
                    GameObject parentObject = display.GetShownObject(0);
                    int verticesBefore = storage.VertexCount;

                    Assert.That(display.TryCutOnce(CrossingPlane(), out VpCutDisplayResult result), Is.False, "the display cannot be prepared");
                    Assert.That(result.outcome, Is.EqualTo(VpCutDisplayOutcome.DisplayPreparationFailed));
                    Assert.That(result.cut.status, Is.EqualTo(VpStorageCutStatus.Ok), "the cut itself succeeded");

                    // the parent is untouched and still shown
                    Assert.That(display.ShownCount, Is.EqualTo(1));
                    Assert.That(display.GetShownObject(0), Is.SameAs(parentObject));
                    Assert.That(parentObject.activeSelf, Is.True, "the parent is still on screen");
                    Assert.That(IndexState(storage, parent), Is.EqualTo(VpIndexRangeState.Published), "and was not retired");

                    // the half-built child was given back: only the parent's registration remains
                    Assert.That(table.LiveGeometryCount, Is.EqualTo(1), "no child registration is left");
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(1), "no child instance is left");

                    // both sides were given back, the one that was registered and the one that never was
                    Assert.That(IndexState(storage, result.cut.positive.geometry), Is.EqualTo(VpIndexRangeState.Free), "the positive side's range");
                    Assert.That(IndexState(storage, result.cut.negative.geometry), Is.EqualTo(VpIndexRangeState.Free), "the negative side's range");

                    // the display rolled back; the storage's appended vertices did not, and it says so
                    Assert.That(result.appendedVerticesKept, Is.EqualTo(storage.VertexCount - verticesBefore), "the appended vertices are reported");
                    Assert.That(result.appendedVerticesKept, Is.GreaterThan(0), "and they really are still there");
                }
            }
        }

        [Test]
        public void EverythingIsGivenBackOnce_AfterASwapAndOnDispose()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                Assert.That(VpCutDisplay.TryCreate(storage, table, parent, Materials(), null, out VpCutDisplay display), Is.True, "create");

                Assert.That(display.TryCutOnce(CrossingPlane(), out VpCutDisplayResult result), Is.True, "cut");
                Assert.That(result.outcome, Is.EqualTo(VpCutDisplayOutcome.Swapped));
                VpStoredGeometry firstChild = display.GetShownGeometry(0);
                VpStoredGeometry secondChild = display.GetShownGeometry(1);
                Mesh firstMesh = display.GetShownObject(0).GetComponent<MeshFilter>().sharedMesh;
                GameObject firstObject = display.GetShownObject(0);

                // the parent's parts were given back once: retiring them again is refused
                Assert.That(storage.TryRetireIndices(parent.indexRange), Is.False, "the parent's range is already retired");

                display.Dispose();
                Assert.That(display.IsDisposed, Is.True);
                Assert.That(table.LiveGeometryCount, Is.Zero, "every registration is back");
                Assert.That(table.LiveDisplayInstanceCount, Is.Zero, "every instance is back");
                Assert.That(firstObject == null, Is.True, "the child object was destroyed");
                Assert.That(firstMesh == null, Is.True, "and its mesh with it");
                Assert.That(storage.TryRetireIndices(firstChild.indexRange), Is.False, "the first child's range is already retired");
                Assert.That(storage.TryRetireIndices(secondChild.indexRange), Is.False, "and the second child's");

                display.Dispose();
                Assert.That(display.IsDisposed, Is.True, "disposing again does nothing");
                Assert.That(table.LiveGeometryCount, Is.Zero, "and gives nothing back twice");
                Assert.Throws<ObjectDisposedException>(() => display.TryCutOnce(CrossingPlane(), out _), "no cut after disposal");
                Assert.Throws<ObjectDisposedException>(() => display.GetShownGeometry(0), "nor a read of what it showed");
            }
        }

        [Test]
        public void ACreationThatCannotBeShown_RegistersNothing()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);

                // a material the geometry names is missing, so nothing can be shown
                var incomplete = new Dictionary<int, Material> { { SideMaterial, Materials()[SideMaterial] } };
                Assert.That(VpCutDisplay.TryCreate(storage, table, parent, incomplete, null, out VpCutDisplay display), Is.False, "refused");
                Assert.That(display, Is.Null);
                Assert.That(table.LiveGeometryCount, Is.Zero, "no registration was left behind");
                Assert.That(table.LiveDisplayInstanceCount, Is.Zero, "nor an instance");
                Assert.That(IndexState(storage, parent), Is.EqualTo(VpIndexRangeState.Published), "and the geometry is untouched");

                Assert.That(VpCutDisplay.TryCreate(null, table, parent, Materials(), null, out _), Is.False, "a null storage");
                Assert.That(VpCutDisplay.TryCreate(storage, null, parent, Materials(), null, out _), Is.False, "a null table");
                Assert.That(VpCutDisplay.TryCreate(storage, table, parent, null, null, out _), Is.False, "no materials");

                // and after all that a complete request still works
                Assert.That(VpCutDisplay.TryCreate(storage, table, parent, Materials(), null, out VpCutDisplay good), Is.True, "a complete request");
                good.Dispose();
            }
        }

        /// <summary>
        /// Giving a registration back is not an undo — the table retires the geometry's index range with it — so a
        /// display that cannot show what it was handed must never have registered it in the first place.
        /// </summary>
        [Test]
        public void ACreationWithNoRoomForAnInstance_LeavesTheGeometryPublished()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                // room for two registrations but for only one display instance
                var table = new VpGeometryReferenceTable(storage, 4, 1);
                VpStoredGeometry first = Append(storage, prepared);
                VpStoredGeometry second = Append(storage, prepared);
                Assert.That(VpCutDisplay.TryCreate(storage, table, first, Materials(), null, out VpCutDisplay shown), Is.True, "the first");
                using (shown)
                {
                    Assert.That(table.LiveDisplayInstanceCount, Is.EqualTo(table.DisplayInstanceCapacity), "the only instance slot is taken");

                    Assert.That(VpCutDisplay.TryCreate(storage, table, second, Materials(), null, out VpCutDisplay refused), Is.False, "the second");
                    Assert.That(refused, Is.Null);
                    Assert.That(IndexState(storage, second), Is.EqualTo(VpIndexRangeState.Published), "the geometry it could not show is untouched");
                    Assert.That(table.LiveGeometryCount, Is.EqualTo(1), "and it was never registered");
                }
            }
        }

        /// <summary>
        /// The sides take the place of what they were cut from. A display that had been moved, turned and scaled keeps
        /// its placement across the swap, instead of the children appearing back at the identity.
        /// </summary>
        [Test]
        public void TheChildren_TakeTheParentDisplaysPlacement()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                var table = new VpGeometryReferenceTable(storage, 8, 8);
                VpStoredGeometry parent = Append(storage, prepared);
                var root = Track(new GameObject("root"));
                root.transform.position = new Vector3(-4f, 1.5f, 2f);
                root.transform.rotation = Quaternion.Euler(0f, 35f, 0f);
                Assert.That(VpCutDisplay.TryCreate(storage, table, parent, Materials(), root.transform, out VpCutDisplay display), Is.True, "create");
                using (display)
                {
                    Transform shown = display.GetShownObject(0).transform;
                    shown.localPosition = new Vector3(3f, -2f, 5f);
                    shown.localRotation = Quaternion.Euler(20f, 40f, 60f);
                    shown.localScale = new Vector3(2f, 0.5f, 1.5f);
                    Vector3 position = shown.position;
                    Quaternion rotation = shown.rotation;
                    Vector3 scale = shown.lossyScale;

                    Assert.That(display.TryCutOnce(CrossingPlane(), out VpCutDisplayResult result), Is.True, "cut");
                    Assert.That(result.outcome, Is.EqualTo(VpCutDisplayOutcome.Swapped));

                    for (int i = 0; i < display.ShownCount; i++)
                    {
                        Transform child = display.GetShownObject(i).transform;
                        Assert.That(child.parent, Is.SameAs(root.transform), "child " + i + " keeps the parent transform");
                        Assert.That((child.position - position).magnitude, Is.LessThan(1e-4f), "child " + i + " position");
                        Assert.That(Quaternion.Angle(child.rotation, rotation), Is.LessThan(0.01f), "child " + i + " rotation");
                        Assert.That((child.lossyScale - scale).magnitude, Is.LessThan(1e-4f), "child " + i + " scale");
                    }
                }
            }
        }
    }
}
