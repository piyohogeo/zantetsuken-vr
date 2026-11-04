using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// NPC-BONE-SPLIT-1 through a prepared character: its cold preparation makes the bone rig from its renderer, its model's
    /// weights and its convexes' bones (the only read of the rig's Transforms for it); its hit records the lineage at the pose
    /// it took; the cut's commit hands the record to both sides with the connection the plane took off and its new vertices'
    /// origins, the convexes' bones carried to the sides; and the side that holds both bones is split by them.
    /// </summary>
    public unsafe partial class ProvisionalMassFlagActivationPlayModeTests
    {
        /// <summary>
        /// Two bones, two tetrahedra: the renderer's own bone at the origin with tetrahedron A (x 0..1) weighted to it, a child
        /// bone at x 3 with tetrahedron B (x 3..4) weighted to it, at the bind pose.
        /// </summary>
        SkinnedMeshRenderer TwoBoneRig(out Transform child)
        {
            var go = ColdTrack(new GameObject("Bone split rig"));
            var r = go.AddComponent<SkinnedMeshRenderer>();
            r.quality = SkinQuality.Bone4;
            var second = new GameObject("Bone split rig: child bone");
            second.transform.SetParent(go.transform, false);
            second.transform.localPosition = new Vector3(3f, 0f, 0f);
            child = second.transform;
            var a = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward };
            var vertices = a.Concat(a.Select(v => v + new Vector3(3f, 0f, 0f))).ToArray();
            var weights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = 1 }, 4)
                .Concat(Enumerable.Repeat(new BoneWeight { boneIndex0 = 1, weight0 = 1 }, 4)).ToArray();
            r.sharedMesh = ColdTrack(new Mesh
            {
                vertices = vertices,
                normals = Enumerable.Repeat(Vector3.up, 8).ToArray(),
                uv = Enumerable.Repeat(new Vector2(.5f, .5f), 8).ToArray(),
                triangles = new[] { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3, 4, 6, 5, 4, 5, 7, 4, 7, 6, 5, 6, 7 },
                bindposes = new[] { Matrix4x4.identity, Matrix4x4.Translate(new Vector3(-3f, 0f, 0f)) },
                boneWeights = weights,
            });
            r.bones = new[] { go.transform, child };
            return r;
        }

        /// <summary>
        /// Two box convexes in one bank, each in the bank's own convention (face offsets relative to the convex's face indices,
        /// face indices and edges local to it), with their edge tables and cooked collider meshes: one at x 0 and one at x 3 of
        /// their bones' frames, each 2 m a side.
        /// </summary>
        PhysicsOwnerShape TwoBoxShape()
        {
            int[] local = BoxFaceIndices();
            var offsets = new[] { 0, 4, 8, 12, 16, 20, 24 };
            var found = new Dictionary<long, int>(); var edges = new List<BrepEdge>(); var faceEdges = new int[24];
            for (int f = 0; f < 6; f++)
            {
                for (int k = offsets[f]; k < offsets[f + 1]; k++)
                {
                    int a = local[k], b = local[k + 1 == offsets[f + 1] ? offsets[f] : k + 1];
                    int lo = math.min(a, b), hi = math.max(a, b); long key = ((long)lo << 32) | (uint)hi;
                    if (!found.TryGetValue(key, out int at)) { at = edges.Count; found.Add(key, at); edges.Add(new BrepEdge { v0 = lo, v1 = hi, f0 = -1, f1 = -1 }); }
                    var edge = edges[at]; if (a == lo) edge.f0 = f; else edge.f1 = f; edges[at] = edge;
                    faceEdges[k] = at;
                }
            }

            Assert.That(edges.Count, Is.EqualTo(12));
            var corners = new List<float3>(); var allOffsets = new List<int>(); var allIndices = new List<int>(); var allFaceEdges = new List<int>(); var allEdges = new List<BrepEdge>();
            var ranges = new ConvexBrepRange[2]; var meshes = new List<Mesh>();
            for (int c = 0; c < 2; c++)
            {
                ranges[c] = new ConvexBrepRange
                {
                    vertexBase = corners.Count, vertexCount = 8, faceBase = allOffsets.Count, faceCount = 6, faceIndexBase = allIndices.Count, faceIndexCount = 24,
                    edgeBase = allEdges.Count, edgeCount = 12, maxFaceLoop = 4,
                };
                corners.AddRange(BoxCorners(3f * c)); allOffsets.AddRange(offsets); allIndices.AddRange(local); allFaceEdges.AddRange(faceEdges); allEdges.AddRange(edges);
                meshes.Add(NewBoxMesh(3f * c));
            }

            var v = new NativeArray<float3>(corners.ToArray(), Allocator.Persistent);
            var o = new NativeArray<int>(allOffsets.ToArray(), Allocator.Persistent);
            var i2 = new NativeArray<int>(allIndices.ToArray(), Allocator.Persistent);
            var fe = new NativeArray<int>(allFaceEdges.ToArray(), Allocator.Persistent);
            var e = new NativeArray<BrepEdge>(allEdges.ToArray(), Allocator.Persistent);
            try
            {
                var bank = new ConvexBrepBank
                {
                    vertices = (float3*)v.GetUnsafePtr(), faceOffsets = (int*)o.GetUnsafePtr(), faceIndices = (int*)i2.GetUnsafePtr(),
                    faceEdges = (int*)fe.GetUnsafePtr(), edges = (BrepEdge*)e.GetUnsafePtr(),
                };
                PhysicsOwnerShape shape = PhysicsOwnerShape.Authored(bank, ranges, meshes, PhysicsShapeSource.External(), float4x4.identity);   // copies the bank
                _disposables.Add(shape);
                return shape;
            }
            finally { v.Dispose(); o.Dispose(); i2.Dispose(); fe.Dispose(); e.Dispose(); }
        }

        /// <summary>
        /// **A prepared character's cut records its bones, and each side keeps its part of them.** The rig is made in the cold
        /// preparation (the convexes' bones, the parent, the model's weights); the hit through tetrahedron A and the convex of bone 0
        /// -- the plane between the joints -- registers the lineage at the hit's pose; the commit hands each side its part of the rig
        /// (NPC-BONE-ACTIVE-2: bone 0's segment cut through, its part on each side; bone 1 wholly positive) and keeps the new
        /// vertices' origins, each side's convexes with their bones (bone 0 on both: the cut convex's two parts). The positive side --
        /// a part of A with a part of bone 0's convex, and B with bone 1's -- holds bone 0's part reaching bone 1's joint: the two
        /// meet there, so its bones decide it is one group and it is not split.
        /// </summary>
        [UnityTest]
        public IEnumerator BoneSplit_APreparedCharactersCut_RecordsItsBones_AndEachSideKeepsItsPartOfThem()
        {
            ColdWorld();
            coldWarm = new VpPhysicsColdPreparation();
            SkinnedMeshRenderer rig = TwoBoneRig(out Transform child);
            var motion = rig.gameObject.AddComponent<Rigidbody>();
            motion.useGravity = false;
            motion.mass = 12;
            motion.automaticCenterOfMass = motion.automaticInertiaTensor = false;
            motion.inertiaTensor = Vector3.one * 4;
            var source = TwoBoxShape();
            Assert.That(coldWorld.TryPrepareCharacterCut(rig, new[] { 0, 1, 2, 3, 4, 5, 6, 7 }, 8, source.BankOf(0), new[] { source.Convex(0), source.Convex(1) },
                new[] { rig.transform, child }, coldWarm, rig.gameObject, VpCharacterMassProperties.FromBody(motion, rig.transform), out var handle), Is.True);
            coldHandles.Add(handle);
            Assert.That(handle.BoneRigRefusal, Is.Null, "the rig was made");
            NpcBoneRig made = handle.BoneRig;
            Assert.That(made.NodeCount, Is.EqualTo(2));
            Assert.That(made.ConvexNode, Is.EqualTo(new[] { 0, 1 }));
            Assert.That(made.Parent, Is.EqualTo(new[] { -1, 0 }), "the child bone's parent is the renderer's bone");
            Assert.That(made.Vertices.VertexCount, Is.EqualTo(8));
            Assert.That(made.Vertices.Bone(0, 0) == 0 && made.Vertices.Bone(4, 0) == 1 && made.Vertices.Bone(4, 1) == NpcVertexBones.None, Is.True, "the weights by vertex");
            yield return null;
            Assert.That(handle.TryFinishPreparation(), Is.True);

            CutSpatialSplit split = coldWorld.SpatialSplit;
            Assert.That(split.Settings.enabled, Is.True, "the layout: the split is on by the profile");
            split.RemainingMainSeconds = () => 0.0;   // closed until the cut is over
            coldWorld.Driver.RemainingMainSeconds = () => 1.0;   // the cut's own budget open: its acceptance (Published) is not what this case is about
            var result = handle.TryCut(new float4(1, 0, 0, -0.5f), float3.zero);
            Assert.That(result.Outcome, Is.EqualTo(VpCharacterCutOutcome.Requested));
            Assert.That(result.Acceptance, Is.EqualTo(ProvisionalCutAcceptance.Published));
            LogicalFragmentId character = result.Source;
            Assert.That(coldWorld.NpcBones.TryGet(character, out NpcBoneFragment root), Is.True, "the hit registered the lineage");
            Assert.That(root.Lineage.Insufficient, Is.Null);
            Assert.That(root.Lineage.BaseCount, Is.EqualTo(8));
            Assert.That(math.length(root.Lineage.Joints[0]) < 1e-5f && math.length(root.Lineage.Joints[1] - new float3(3, 0, 0)) < 1e-5f, Is.True,
                "the joints at the hit's pose, in the renderer's frame");

            for (int i = 0; i < 240 && coldWorld.Geometry.StageOf(result.Operation) != CutGeometryStage.Committed; i++) yield return null;
            Assert.That(coldWorld.Geometry.StageOf(result.Operation), Is.EqualTo(CutGeometryStage.Committed));
            Assert.That(coldWorld.Ledger.TryGetOperation(result.Operation, out LogicalCutOperation operation), Is.True);
            Assert.That(coldWorld.NpcBones.TryGet(operation.positive, out NpcBoneFragment positive), Is.True, "both sides are the NPC's");
            Assert.That(coldWorld.NpcBones.TryGet(operation.negative, out NpcBoneFragment negativeRecord), Is.True);
            // NPC-BONE-ACTIVE-2: the plane x = 0.5 crosses bone 0's segment (x 0 to 3): its part on each side; bone 1 (x 3) wholly positive.
            Assert.That(positive.Piece.Present, Is.EqualTo(new[] { true, true }), "the positive side: bone 0's part towards bone 1, and bone 1");
            Assert.That(negativeRecord.Piece.Present, Is.EqualTo(new[] { true, false }), "the negative side: bone 0's joint and part; bone 1 wholly on the other side");
            Assert.That(positive.Piece.IsJointKept(1) && !positive.Piece.IsJointKept(0) && negativeRecord.Piece.IsJointKept(0), Is.True);
            Assert.That(positive.Piece.Lo[1] > 0f && positive.Piece.Hi[1] == 1f && negativeRecord.Piece.Lo[1] == 0f && negativeRecord.Piece.Hi[1] < 1f, Is.True,
                "bone 0's segment shared at the plane");
            Assert.That(positive.Lineage.Chunks.Length == 1 && positive.Lineage.Chunks[0].count > 0, Is.True, "the cut's new vertices with their origins");
            for (int i = 0; i < 240 && !(coldWorld.Owners.TryGet(operation.positive, out _) && coldWorld.Driver.Transactions.Count == 0); i++) yield return null;
            Assert.That(coldWorld.Owners.TryGet(operation.positive, out PhysicsFragmentOwner up), Is.True);
            Assert.That(coldWorld.Owners.TryGet(operation.negative, out PhysicsFragmentOwner downOwner), Is.True);
            var upBones = new List<int>();
            for (int c = 0; c < up.Shape.ConvexCount; c++) upBones.Add(up.Shape.ConvexBone(c));
            Assert.That(upBones, Is.EquivalentTo(new[] { 0, 1 }), "the positive side: a part of bone 0's convex and bone 1's");
            Assert.That(downOwner.Shape.ConvexCount == 1 && downOwner.Shape.ConvexBone(0) == 0, Is.True, "the negative side: the other part of bone 0's");

            split.BoneFallback = false;   // this case is about the bones' own decision (the fallback is on by default since 2026-11-04)
            split.RemainingMainSeconds = () => 1.0;
            for (int i = 0; i < 600 && !(split.PublishedBone >= 1 || split.BoneNotSeparable + split.BoneInsufficient > 0); i++) yield return null;
            split.RemainingMainSeconds = () => 0.0;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(split.DescribeBone());
            // The positive side holds bone 0's part reaching bone 1's joint: the two meet there, one cluster -- decided, not split, nothing else tried.
            Assert.That(split.BoneInsufficient + split.PublishedBone, Is.Zero, "decided: " + split.LastBone.verdict + " (" + split.LastBone.reason + ")");
            Assert.That(split.BoneNotSeparable, Is.EqualTo(1), "the positive side's two bones are connected within it");
            Assert.That(split.LastBone.clusters, Is.EqualTo(1));
            Assert.That(split.LastBone.reason, Is.EqualTo(NpcBoneSplitSearch.ReasonOneGroup));
            Assert.That(coldWorld.Owners.TryGet(operation.positive, out PhysicsFragmentOwner kept) && kept.Shape.ConvexCount == 2, Is.True, "and stays one object");
        }
    }
}
