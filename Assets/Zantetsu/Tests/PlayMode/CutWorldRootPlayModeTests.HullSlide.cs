using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The always-kinematic mode's slide along the cut plane and its display classification by the vertices a geometry
    /// uses (TL, 2026-09-30). The slide: a diagonal cut, its normal flipped, and a moved and turned building each move
    /// their upper child in the plane (the move's dot with the world normal zero), by the distance interpolated from 2 cm
    /// (vertical) to 15 cm (horizontal); the lower child and the kinematic body do not move. The classification: a child
    /// of a box cut at x = 0 lies wholly on x >= 0, yet names its parent's vertex block -- by the blocks it is "crossed"
    /// by x = -0.5, by the vertices its indices use it is not; a cut there makes no operation on it and no empty child,
    /// and a plane that does cross it still makes both children's shapes. The shapes are read independently here, from
    /// the storage's indices and the placement, never through the classification itself.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private HullGroup AddHullBuildingPosed(CutWorldRoot root, Vector3 at, Quaternion rotation, float3[] anchors, double mass, out LogicalFragmentId fragment)
        {
            PhysicsOwnerShape shape = NewBoxShape(out Mesh _);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendBoxGeometry(root.Storage, default);
            var actor = TrackActor(new GameObject("Hull Building (posed)"));
            actor.transform.SetPositionAndRotation(at, rotation);
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = true;
            Assert.That(root.TryAddBuildingHull(actor, shape, geometry, Matrix4x4.identity, anchors, mass, out fragment, out HullGroup group), Is.True, "the building was taken into the hull trial");
            _registered.Add(actor);
            return group;
        }

        private static float3 ExpectedSlide(float3 normal)
        {
            float3 n = math.normalize(normal);
            float3 t = math.normalize(new float3(0f, -1f, 0f) - math.dot(new float3(0f, -1f, 0f), n) * n);
            return t * (DropVertical + (DropHorizontal - DropVertical) * math.abs(n.y));
        }

        /// <summary>
        /// **The upper side slides in the cut plane, whatever the normal's sign or the building's pose; the lower side and
        /// the body stay.** Three buildings: a diagonal cut (normal (1,1,0)); the same cut with the normal flipped; the same
        /// world plane through a building moved and turned (yaw 30 deg, roll 10 deg). Each upper child moves down the plane
        /// by 2 cm + 13 cm x |n.y|; its move's dot with the normal is zero; the flipped normal moves the same child the same
        /// way; the lower children and the bodies (position and rotation) do not move.
        /// </summary>
        [UnityTest]
        public IEnumerator HullKinematic_SlidesInThePlane_DiagonalFlippedAndPosed_TheLowerSideAndTheBodyStay()
        {
            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            float3 anchor = new float3(-0.5f, -0.9f, 0f);
            HullGroup a = AddHullBuilding(root, Vector3.zero, new[] { anchor }, 12.0, out _);
            HullGroup b = AddHullBuilding(root, new Vector3(20f, 0f, 0f), new[] { anchor }, 12.0, out _);
            HullGroup c = AddHullBuildingPosed(root, new Vector3(40f, 0.5f, 5f), Quaternion.Euler(0f, 30f, 10f), new[] { anchor }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            var snapshots = new List<double>();
            using var snapshot = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Display.Collect.2Snapshot");
            yield return null;
            Vector3 oa = a.Members[0].root.transform.position, ob = b.Members[0].root.transform.position, oc = c.Members[0].root.transform.position;
            Vector3 bodyA = a.Body.position, bodyC = c.Body.position;
            Quaternion rotA = a.Body.rotation, rotC = c.Body.rotation;
            Vector3 n = math.normalize(new float3(1f, 1f, 0f)), along = math.normalize(new float3(1f, -1f, 0f));
            Evaluate(detector, Wide(1, n, new Vector3(0.2f, 0f, 0f), along, Vector3.forward), 1);
            Evaluate(detector, Wide(2, -n, new Vector3(20.2f, 0f, 0f), along, Vector3.forward), 2);
            Evaluate(detector, Wide(3, n, oc + new Vector3(0.2f, 0f, 0f), along, Vector3.forward), 3);
            yield return UntilKinematic(root, 3, () => h.GroupCuts == 3 && h.IsSettled, 30f, "three diagonal cuts slid and settled", snapshots, snapshot);
            Vector3 expected = (Vector3)ExpectedSlide(n);
            TestContext.Out.WriteLine("expected slide " + expected.ToString("F5") + " (" + expected.magnitude.ToString("F5") + " m); rules: " + h.Hits[0].slideRule + " / " + h.Hits[1].slideRule + " / " + h.Hits[2].slideRule);
            AssertOneMoved(a, oa, expected, n, "diagonal");
            AssertOneMoved(b, ob, expected, -n, "diagonal, the normal flipped (the same move)");
            AssertOneMoved(c, oc, expected, n, "diagonal through a moved and turned building");
            AssertMovedIsUpper(root, a, oa, h.Hits[0], true, "diagonal");
            AssertMovedIsUpper(root, b, ob, h.Hits[1], false, "diagonal, the normal flipped");
            Assert.That((a.Body.position - bodyA).magnitude + (c.Body.position - bodyC).magnitude, Is.LessThan(1e-6f), "the bodies did not move");
            Assert.That(Quaternion.Angle(a.Body.rotation, rotA) + Quaternion.Angle(c.Body.rotation, rotC), Is.LessThan(1e-4f), "nor turn");
            Assert.That(a.HullGeneration == 2 && b.HullGeneration == 2 && c.HullGeneration == 2, Is.True, "each hull exchanged with the same move");
            KinematicLine(root, "slides in the plane", snapshots);
            AssertOneEach(root, 3, "at the end");
            yield return EndWorld(root);
        }

        /// <summary>A plane in the world given in a member's frame (the detector's transform of a plane).</summary>
        private static float4 PlaneInMember(HullGroup.DisplayMember m, float4 planeWorld)
        {
            float4 p = math.mul(math.transpose((float4x4)m.root.transform.localToWorldMatrix), planeWorld);
            return p / math.length(p.xyz);
        }

        /// <summary>The signed distances to a world plane of a fragment's display in the world, read independently: by the vertices its published indices use, and by every vertex of its blocks.</summary>
        private static void IndependentSides(CutWorldRoot root, LogicalFragmentId fragment, float4 planeWorld, out float minUsed, out float maxUsed, out float minBlocks, out float maxBlocks)
        {
            minUsed = minBlocks = float.MaxValue; maxUsed = maxBlocks = float.MinValue;
            Assert.That(root.Geometry.TryGetGeometry(fragment, out VpStoredGeometry geometry), Is.True, "fragment " + fragment.value + " has a committed geometry");
            Assert.That(root.Placement.TryGetGeometryLocalToWorld(fragment, default, 0f, out Matrix4x4 toWorld), Is.EqualTo(VpFragmentPlacementKind.Following), "placed by following its member Root");
            NativeArray<VpRenderVertex>.ReadOnly vertices = root.Storage.Vertices;
            Assert.That(root.Storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly indices), Is.True, "its indices readable");
            try
            {
                for (int i = 0; i < indices.Length; i++)
                {
                    float3 w = toWorld.MultiplyPoint3x4(vertices[(int)indices[i]].position);
                    float d = math.dot(planeWorld.xyz, w) + planeWorld.w;
                    minUsed = math.min(minUsed, d); maxUsed = math.max(maxUsed, d);
                }
            }
            finally
            {
                root.Storage.TryReleaseIndexReadLease(lease);
            }

            Assert.That(root.Storage.TryGetVertexBlocks(geometry, out NativeArray<VpGeometryVertexBlock>.ReadOnly blocks, out _), Is.True);
            foreach (VpGeometryVertexBlock block in blocks)
            {
                for (int v = block.vertexStart; v < block.vertexStart + block.vertexCount; v++)
                {
                    float3 w = toWorld.MultiplyPoint3x4(vertices[v].position);
                    float d = math.dot(planeWorld.xyz, w) + planeWorld.w;
                    minBlocks = math.min(minBlocks, d); maxBlocks = math.max(maxBlocks, d);
                }
            }
        }

        /// <summary>
        /// **The classification reads the vertices a child uses, not its ancestors' blocks.** A box x in [-1, 1] is cut at
        /// x = 0; the positive child (x >= 0) is committed. Read independently: its indices' vertices all stand at x >= 0,
        /// its blocks reach x = -1. Against x = -0.5: by the blocks the child is "crossed" (the former reading's false
        /// crossing), by its indices it is wholly positive; the negative child is crossed both ways. A cut at x = -0.5 then
        /// makes one display operation (on the negative child), none on the positive one, and no empty child; a cut at
        /// x = 0.5, which does cross the positive child, makes both of its children's shapes (no empty child either).
        /// </summary>
        [UnityTest]
        public IEnumerator HullKinematic_TheClassificationReadsTheVerticesAChildUses_NotItsAncestorsBlocks()
        {
            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            var snapshots = new List<double>();
            using var snapshot = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Zantetsu.Display.Collect.2Snapshot");
            yield return null;
            Evaluate(detector, Upright(1, 0f), 1);
            yield return UntilKinematic(root, 1, () => h.GroupCuts == 1 && h.IsSettled, 30f, "the cut at x = 0 committed and settled", snapshots, snapshot);
            Assert.That(root.Ledger.TryGetOperation(h.Hits[0].displayOperations[0], out LogicalCutOperation first), Is.True);
            HullGroup.DisplayMember positive = null, negative = null;
            foreach (HullGroup.DisplayMember m in building.Members) { if (m.fragment == first.positive) positive = m; if (m.fragment == first.negative) negative = m; }
            Assert.That(positive != null && negative != null, Is.True, "both children held");
            var planeWorld = new float4(1f, 0f, 0f, 0.5f);   // x = -0.5
            IndependentSides(root, positive.fragment, planeWorld, out float minUsed, out float maxUsed, out float minBlocks, out float maxBlocks);
            TestContext.Out.WriteLine("the positive child against x = -0.5, read independently: by its indices [" + minUsed.ToString("F4") + ", " + maxUsed.ToString("F4") + "], by its blocks [" + minBlocks.ToString("F4") + ", " + maxBlocks.ToString("F4") + "]");
            Assert.That(minUsed, Is.GreaterThan(0.4f), "the vertices it uses stand at x >= 0: wholly on the positive side");
            Assert.That(minBlocks, Is.LessThan(-0.4f), "its blocks reach the parent's x = -1 (unused by it)");

            // The classification of both children against x = -0.5, by the former reading and by the indices: the same shapes, the same plane.
            Assert.That(h.ClassifyForTest(positive.fragment, PlaneInMember(positive, planeWorld), true, out bool bp, out bool bn), Is.True);
            Assert.That(h.ClassifyForTest(positive.fragment, PlaneInMember(positive, planeWorld), false, out bool ip, out bool inn), Is.True);
            Assert.That(h.ClassifyForTest(negative.fragment, PlaneInMember(negative, planeWorld), true, out bool nbp, out bool nbn), Is.True);
            Assert.That(h.ClassifyForTest(negative.fragment, PlaneInMember(negative, planeWorld), false, out bool nip, out bool nin), Is.True);
            int crossedByBlocks = (bp && bn ? 1 : 0) + (nbp && nbn ? 1 : 0), crossedByIndices = (ip && inn ? 1 : 0) + (nip && nin ? 1 : 0);
            TestContext.Out.WriteLine("members crossed by x = -0.5: by the blocks " + crossedByBlocks + ", by the indices " + crossedByIndices + " (the positive child: blocks " + bp + "/" + bn + ", indices " + ip + "/" + inn + ")");
            Assert.That(bp && bn, Is.True, "by the blocks the positive child is falsely crossed");
            Assert.That(ip && !inn, Is.True, "by its indices it is wholly positive");
            Assert.That(nip && nin, Is.True, "the negative child is crossed");

            // The cut at x = -0.5: one operation (the negative child), none on the positive one, no empty child.
            int operations = root.Ledger.OperationCount;
            h.CountDisplay(out int held0, out _, out int empty0, out _, out _, out _);
            Evaluate(detector, Upright(2, -0.5f), 2);
            yield return UntilKinematic(root, 1, () => h.GroupCuts == 2 && h.IsSettled, 30f, "the cut at x = -0.5 settled", snapshots, snapshot);
            h.CountDisplay(out int held1, out int committed1, out int empty1, out int pending1, out _, out _);
            TestContext.Out.WriteLine("after x = -0.5: operations " + operations + " -> " + root.Ledger.OperationCount + ", members " + held0 + " -> " + held1 + " (committed " + committed1 + ", empty " + empty0 + " -> " + empty1 + ", not yet " + pending1 + ")");
            Assert.That(root.Ledger.OperationCount - operations, Is.EqualTo(1), "one display operation");
            Assert.That(h.Hits[1].displayOperations.Count, Is.EqualTo(1));
            Assert.That(empty1, Is.EqualTo(empty0), "no empty child");
            Assert.That(building.Members.Exists(m => m.fragment == positive.fragment), Is.True, "the positive child kept, not cut");

            // The cut at x = 0.5 crosses the positive child: both of its children have shapes.
            Evaluate(detector, Upright(3, 0.5f), 3);
            yield return UntilKinematic(root, 1, () => h.GroupCuts == 3 && h.IsSettled, 30f, "the cut at x = 0.5 settled", snapshots, snapshot);
            h.CountDisplay(out int held2, out int committed2, out int empty2, out int pending2, out _, out _);
            TestContext.Out.WriteLine("after x = 0.5: members " + held2 + " (committed " + committed2 + ", empty " + empty2 + ", not yet " + pending2 + ")");
            Assert.That(h.Hits[2].displayOperations.Count, Is.GreaterThanOrEqualTo(1), "the positive child cut");
            Assert.That(root.Ledger.TryGetOperation(h.Hits[2].displayOperations[0], out LogicalCutOperation third), Is.True);
            Assert.That(root.Geometry.HasNoGeometry(third.positive) || root.Geometry.HasNoGeometry(third.negative), Is.False, "both children have shapes");
            Assert.That(empty2, Is.EqualTo(empty0), "still no empty child");
            KinematicLine(root, "classification by the vertices used", snapshots);
            AssertOneEach(root, 1, "at the end");
            yield return EndWorld(root);
        }
    }
}
