using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// NPC-BONE-SPLIT-1: the bone-aware split of an NPC's pieces through the product's own world. The fixture is the split's
    /// trunk with a hand beside it, made an NPC's lineage by hand: bone 0 the trunk's, bone 1 the hand's (a child of 0), the
    /// convexes and the drawn boxes weighted to them, the joints at a pose -- the record a prepared character's first cut makes
    /// (its own case is in the prepared character's tests). A waist cut takes the connection of the two bones off where the
    /// joints lie on two sides of it, records where its new vertices came from, and the lower side -- trunk and hand in one
    /// owner -- is decided by the bones alone: split where the hand overlaps the trunk (no plane could), kept where the bones
    /// connect it (though a plane would split it: nothing falls back), not decided before the cut's geometry is committed, not
    /// published when what it was decided for has changed, and decided again on a re-cut's pieces from the vertices that cut made.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        /// <summary>
        /// The trunk and the hand of <see cref="AddBodyWithAHand"/>, an NPC's when <paramref name="npc"/>: the trunk's convex and
        /// boxes of bone 0, the hand's convex of bone 2 and its boxes of <paramref name="handMeshBone"/>. The rig (NPC-BONE-HELD-1):
        /// 0 the trunk (its joint at the centre above the waist, y 0.5), 1 a shoulder over the hand (y <paramref name="shoulderJointY"/>),
        /// 2 the hand (y -0.5), hanging from the shoulder, 3 a hip below the waist (y -0.9): below the waist the trunk is held by its
        /// convex and joined to the hip, and -- with the shoulder above the waist (no joint, no convex below) -- apart from it the hand.
        /// </summary>
        private LogicalFragmentId AddNpcBodyWithAHand(CutWorldRoot root, Vector3 at, bool npc, float handLowX = HandLowX, int handMeshBone = 2, float shoulderJointY = 0.9f, float trunkJointY = 0.5f,
            int trunkBottomBone = 0)
        {
            float3[] trunk = BoxCornersAt(float3.zero, new float3(1f));
            float3[] hand = BoxCornersAt(new float3(handLowX + 0.25f, -0.5f, 0f), new float3(0.25f));
            var boxes = new List<float3[]> { trunk, hand };
            PhysicsOwnerShape shape = NewBoxesShape(boxes);
            _disposables.Add(shape);
            if (npc) shape.SetConvexBones(new[] { 0, 2 });
            VpStoredGeometry geometry = AppendBoxesGeometry(root.Storage, boxes);

            var actor = TrackActor(new GameObject(npc ? "NPC trunk with a hand" : "Trunk with a hand"));
            actor.transform.position = at;
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            for (int c = 0; c < boxes.Count; c++)
            {
                MeshCollider collider = actor.AddComponent<MeshCollider>();
                collider.cookingOptions = PhysicsCutCook.DefaultCooking;
                collider.convex = true;
                collider.sharedMesh = shape.MeshOf(c);
            }

            bool added = root.TryAddBody(actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, null, false, out LogicalFragmentId fragment);
            Assert.That(added, Is.True, "the body was taken into the world");
            _registered.Add(actor);
            if (!npc)
            {
                return fragment;
            }

            // The vertices in the order the geometry was appended (AppendBoxesGeometry): each submesh, each box, each face, four.
            var weights = new List<ulong>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                for (int box = 0; box < boxes.Count; box++)
                {
                    for (int face = 0; face < k_faces.Length; face++)
                    {
                        if (k_faces[face].submesh != submesh) continue;
                        // NPC-BONE-TOPOLOGY-1: the trunk's bottom face (corners 0-3) may be given a bone of its own.
                        ushort bone = (ushort)(box != 0 ? handMeshBone : face == 4 ? trunkBottomBone : 0);
                        for (int k = 0; k < 4; k++) weights.Add(NpcVertexBones.Pack(bone, NpcVertexBones.None, NpcVertexBones.None, NpcVertexBones.None));
                    }
                }
            }

            Assert.That(weights.Count, Is.EqualTo(geometry.vertexCount), "the layout: a weight a vertex");
            NpcVertexBones table = NpcVertexBones.FromEntries(weights.ToArray(), 4);
            var lineage = new NpcBoneLineage(4, new[] { -1, 0, 1, 0 }, new[] { true, true, true, true },
                new[] { new float3(0f, trunkJointY, 0f), new float3(handLowX + 0.25f, shoulderJointY, 0f), new float3(handLowX + 0.25f, -0.5f, 0f), new float3(0f, -0.9f, 0f) },
                table, (uint)geometry.vertexStart, geometry.vertexCount, null);
            table.Release();   // the lineage's hold is the one left
            root.NpcBones.RegisterRoot(fragment, lineage);
            return fragment;
        }


        /// <summary>The surface of a stored geometry by its own topology: triangles, edges used once, edges used more than twice, inner edges whose two faces run the same way.</summary>
        private static (int triangles, int boundary, int nonManifold, int badWinding) TopologySurfaceOf(CutWorldRoot root, VpStoredGeometry geometry, out int blocks, out int seamVertices)
        {
            Assert.That(NpcBoneTopology.TryOfGeometry(root.Storage, geometry, out NpcBoneTopology topology, out string why), Is.True, why);
            Assert.That(root.Storage.TryGetVertexBlocks(geometry, out Unity.Collections.NativeArray<VpGeometryVertexBlock>.ReadOnly vertexBlocks, out _), Is.True);
            blocks = vertexBlocks.Length;
            try
            {
                List<(uint, uint, uint)> triangles = TrianglesOf(root, geometry);
                var uses = new Dictionary<(int, int), int>();
                var directed = new Dictionary<(int, int), int>();
                var renderOfTopology = new Dictionary<int, uint>();
                var seams = new HashSet<uint>();
                int last = -1;
                RenderCutTopologyMap map = topology.Map;
                foreach ((uint, uint, uint) triangle in triangles)
                {
                    uint[] corner = { triangle.Item1, triangle.Item2, triangle.Item3 };
                    var t = new int[3];
                    for (int e = 0; e < 3; e++)
                    {
                        t[e] = RenderCutTopologyMap.TopologyOf(in map, corner[e], ref last);
                        Assert.That(t[e] >= 0 && t[e] < map.topologyVertexCount, Is.True, "render vertex " + corner[e] + " has its topology vertex");
                        if (!renderOfTopology.TryGetValue(t[e], out uint first)) renderOfTopology[t[e]] = corner[e];
                        else if (first != corner[e]) seams.Add(corner[e]);
                    }

                    for (int e = 0; e < 3; e++)
                    {
                        int a = t[e], b = t[(e + 1) % 3];
                        (int, int) key = a < b ? (a, b) : (b, a);
                        uses[key] = uses.TryGetValue(key, out int n) ? n + 1 : 1;
                        directed[(a, b)] = directed.TryGetValue((a, b), out int m) ? m + 1 : 1;
                    }
                }

                int boundary = 0, nonManifold = 0, badWinding = 0;
                foreach (KeyValuePair<(int, int), int> edge in uses)
                {
                    if (edge.Value == 1) boundary++;
                    else if (edge.Value > 2) nonManifold++;
                    else if (!(directed.TryGetValue(edge.Key, out int forward) && forward == 1 && directed.TryGetValue((edge.Key.Item2, edge.Key.Item1), out int back) && back == 1)) badWinding++;
                }

                seamVertices = seams.Count;
                return (triangles.Count, boundary, nonManifold, badWinding);
            }
            finally
            {
                topology.Dispose();
            }
        }

        /// <summary>
        /// **Through the product's own path -- a real cut's cap, the surface's attribute seams, a geometry of several vertex blocks --
        /// a closed piece is split into closed pieces (NPC-BONE-TOPOLOGY-1).** The trunk and the hand, every box face with render
        /// vertices of its own (the seams), the trunk's bottom face weighted to the shoulder alone: a bone the piece below the waist
        /// does not hold. The waist cut -- the cut kernel's: new vertices in a block of their own, a cap sewn to the cut surface by
        /// the kernel's topology -- leaves a closed lower side. Render vertex by render vertex the bottom face was a mesh alone and
        /// went with the hand, opening both children; by the geometry's topology the trunk's lower part is one component with its
        /// cap and its bottom face. The split's two children, read back from the storage with their own topology: every triangle on
        /// exactly one side, every topology edge of each child with its two faces in opposite senses, each with the convex of its bone.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitBone_Topology_AClosedCutPieceWithSeamsAndACap_IsSplitIntoClosedPieces()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.SetPairProtect(false);   // NPC-PAIR-DEFAULT-1: this case is about the bone-aware decision (and its fallback); the protected combination, on by default, is off here
            LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 0f), true, handLowX: 0.8f, trunkBottomBone: 1);
            yield return null;
            Assert.That(root.Geometry.TryGetGeometry(body, out VpStoredGeometry whole), Is.True);
            Assert.That(TopologySurfaceOf(root, whole, out int wholeBlocks, out int wholeSeams), Is.EqualTo((24, 0, 0, 0)), "the layout: two closed boxes");
            Assert.That(wholeBlocks == 1 && wholeSeams == 32, Is.True, "one block; 48 render vertices of 16 topology vertices");
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            LogicalFragmentId lower = sides[1];
            Assert.That(root.Geometry.TryGetGeometry(lower, out VpStoredGeometry lowerGeometry), Is.True);
            (int triangles, int boundary, int nonManifold, int badWinding) parent = TopologySurfaceOf(root, lowerGeometry, out int lowerBlocks, out int lowerSeams);
            TestContext.Out.WriteLine("the lower side: " + parent + ", vertex blocks " + lowerBlocks + ", seam render vertices " + lowerSeams);
            Assert.That(parent.boundary == 0 && parent.nonManifold == 0 && parent.badWinding == 0, Is.True, "the cut's lower side is closed: the kernel's cap is sewn to the cut surface by its topology");
            Assert.That(lowerBlocks, Is.GreaterThanOrEqualTo(2), "the layout: the base's vertices and the cut's, in blocks of their own");
            Assert.That(lowerSeams, Is.GreaterThan(0), "the layout: attribute seams (the faces', the cap's)");
            Assert.That(root.NpcBones.TryGet(lower, out NpcBoneFragment lowerRecord), Is.True);
            Assert.That(!lowerRecord.Piece.IsJointKept(1) && root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner) && lowerOwner.Shape.ConvexCount == 2, Is.True, "the layout: the shoulder not held below; the trunk's and the hand's convexes");

            remaining[0] = 1.0;
            yield return Until(() => !split.HasAttempt && split.BoneSearches >= 1, "the lower side was decided");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.DescribeBone());
            NpcBoneSplitRecord bones = split.LastBone;
            Assert.That(bones.verdict, Is.EqualTo(NpcBoneSplitVerdict.Separable), DescribeBoneRecord(bones));
            Assert.That(split.PublishedBone, Is.EqualTo(1));
            Assert.That(bones.meshComponents == 2 && bones.groups == 2 && bones.fullGroups == 2 && bones.unboundGroups == 0, Is.True,
                "the trunk's lower part with its cap and its bottom face one component, the hand another: " + DescribeBoneRecord(bones) + ", components " + bones.meshComponents + ", mesh alone " + bones.unboundGroups);
            Assert.That(bones.seamVertices > 0 && bones.topologyVertices > 16, Is.True, "seams joined by the topology; the cut's nodes among the topology vertices");
            Assert.That(bones.removedWeights, Is.GreaterThan(0), "the bottom face's weights on the shoulder: left out of the connections, the face kept with its surface");
            Assert.That(bones.positiveTriangles + bones.negativeTriangles, Is.EqualTo(parent.triangles), "no triangle lost or counted twice");
            Assert.That(bones.positiveConvexes == 1 && bones.negativeConvexes == 1, Is.True, "no convex lost or counted twice");

            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(root.Geometry.TryGetGeometry(a, out VpStoredGeometry ga) && root.Geometry.TryGetGeometry(b, out VpStoredGeometry gb), Is.True);
            root.Geometry.TryGetGeometry(a, out ga);
            root.Geometry.TryGetGeometry(b, out gb);
            (int triangles, int boundary, int nonManifold, int badWinding) up = TopologySurfaceOf(root, ga, out _, out _), down = TopologySurfaceOf(root, gb, out _, out _);
            TestContext.Out.WriteLine("the children: positive " + up + ", negative " + down);
            Assert.That(up.triangles == bones.positiveTriangles && down.triangles == bones.negativeTriangles && up.triangles + down.triangles == parent.triangles, Is.True, "every triangle of the parent in exactly one child");
            Assert.That(up.boundary == 0 && up.nonManifold == 0 && up.badWinding == 0, Is.True, "the positive child is closed: " + up);
            Assert.That(down.boundary == 0 && down.nonManifold == 0 && down.badWinding == 0, Is.True, "the negative child is closed: " + down);
            Assert.That(down.triangles, Is.EqualTo(12), "the hand's box, whole");
            Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA) && root.Owners.TryGet(b, out PhysicsFragmentOwner ownerB), Is.True);
            root.Owners.TryGet(a, out ownerA);
            root.Owners.TryGet(b, out ownerB);
            Assert.That(ownerA.Shape.ConvexCount == 1 && ownerB.Shape.ConvexCount == 1 && ownerA.Shape.ConvexBone(0) == 0 && ownerB.Shape.ConvexBone(0) == 2, Is.True, "the trunk's convex with the trunk's surface, the hand's with the hand's");
            Assert.That(root.NpcBones.TryGet(a, out NpcBoneFragment ra) && root.NpcBones.TryGet(b, out NpcBoneFragment rb), Is.True);
            root.NpcBones.TryGet(a, out ra);
            root.NpcBones.TryGet(b, out rb);
            Assert.That(ra.Piece.Present[0] && !ra.Piece.Present[2] && rb.Piece.Present[2] && !rb.Piece.Present[0], Is.True, "each child's bone record is its convex's");
            split.Settings = default;
            yield return EndWorld(root);
        }

        private static string DescribeBoneRecord(in NpcBoneSplitRecord b)
        {
            return b.verdict + " (" + b.reason + "): bones " + b.bones + ", clusters " + b.clusters + ", mesh components " + b.meshComponents + ", groups " + b.groups
                + " (" + b.fullGroups + " full), referenced " + b.referencedVertices + " of " + b.lineageVertices + ", origin steps " + b.originSteps
                + ", convexes +" + b.positiveConvexes + " -" + b.negativeConvexes + ", triangles +" + b.positiveTriangles + " -" + b.negativeTriangles;
        }

        private static bool BoxesOverlap(PhysicsOwnerShape shape, int a, int b)
        {
            shape.ConvexBounds(a, out float3 loA, out float3 hiA);
            shape.ConvexBounds(b, out float3 loB, out float3 hiB);
            return math.all(loA < hiB) && math.all(loB < hiA);
        }

        /// <summary>
        /// **An NPC's hand that overlaps its trunk is split from it by the bones, where no plane separates them.** The waist cut
        /// leaves the trunk's lower part and the hand in one owner, their convexes overlapping; the same body that is not an
        /// NPC's is left by the planes (marked), while the NPC's lower side is decided by its bones alone -- two clusters, one
        /// group each -- and published as two ordinary objects, each with the convex of its own bone, both still the NPC's.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitBone_AnNpcsHandOverlappingItsTrunk_IsSplitByItsBones_WhereNoPlaneSeparatesThem()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.SetPairProtect(false);   // NPC-PAIR-DEFAULT-1: this case is about the bone-aware decision (and its fallback); the protected combination, on by default, is off here
            LogicalFragmentId npcBody = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 0f), true, handLowX: 0.8f);
            LogicalFragmentId plainBody = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 12f), false, handLowX: 0.8f);
            yield return null;
            Assert.That(root.NpcBones.Knows(npcBody) && !root.NpcBones.Knows(plainBody), Is.True, "the layout: one NPC's, one not");
            Assert.That(root.NpcBones.WantsVertexOrigins(npcBody), Is.True);
            var npcSides = new LogicalFragmentId[2];
            var plainSides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, npcBody, npcSides);
            yield return CutAtTheWaist(root, plainBody, plainSides);
            LogicalFragmentId lower = npcSides[1], plainLower = plainSides[1];

            // The commit gave each side its part of the rig: below, the trunk's part towards the hip and, apart, the shoulder's towards the hand.
            Assert.That(root.NpcBones.Knows(npcBody), Is.False, "the cut source's record went to its sides");
            Assert.That(root.NpcBones.TryGet(lower, out NpcBoneFragment lowerRecord) && root.NpcBones.Knows(npcSides[0]), Is.True);
            Assert.That(lowerRecord.Piece.Present, Is.EqualTo(new[] { true, true, true, true }), "every bone has a part below");
            Assert.That(!lowerRecord.Piece.IsJointKept(0) && !lowerRecord.Piece.IsJointKept(1) && lowerRecord.Piece.IsJointKept(2) && lowerRecord.Piece.IsJointKept(3), Is.True,
                "the trunk's and the shoulder's joints above the waist: the two do not meet below");
            Assert.That(lowerRecord.Lineage.Chunks.Length, Is.EqualTo(1), "the waist cut's new vertices, with their origins");
            Assert.That(root.NpcBones.CommitsWithoutOrigins, Is.Zero);
            Assert.That(root.NpcBones.Knows(plainLower), Is.False, "the plain body's pieces are no NPC's");
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner), Is.True);
            PhysicsOwnerShape lowerShape = lowerOwner.Shape;
            float lowerMass = (float)lowerOwner.Mass;
            var lowerBones = new List<int> { lowerShape.ConvexBone(0), lowerShape.ConvexBone(1) };
            Assert.That(lowerBones, Is.EquivalentTo(new[] { 0, 2 }), "the cut's side carries its convexes' bones (the trunk's part a produced one)");
            Assert.That(BoxesOverlap(lowerShape, 0, 1), Is.True, "the layout: the hand's convex overlaps the trunk's");
            Assert.That(root.Owners.TryGet(plainLower, out PhysicsFragmentOwner plainOwner) && !plainOwner.Shape.HasConvexBones, Is.True);

            remaining[0] = 1.0;
            yield return Until(() => !split.HasAttempt && split.PublishedBone + split.BoneNotSeparable + split.BoneInsufficient >= 1
                                     && split.EndedAs(SpatialSplitEnd.NotFound) - split.BoneNotSeparable - split.BoneInsufficient >= 1,
                "both lower sides were tried");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(split.DescribeBone());
            TestContext.Out.WriteLine("bones: " + DescribeBoneRecord(split.LastBone));
            Assert.That(split.BoneIssued, Is.EqualTo(1), "one attempt was an NPC's");
            Assert.That(split.BoneSearches == 1 && split.BoneSeparable == 1, Is.True, "decided by the bones, apart");
            Assert.That(split.PublishedBone, Is.EqualTo(1));
            Assert.That(split.Published, Is.EqualTo(1), "the plain body's lower side was not split: no plane separates overlapping convexes");
            Assert.That(split.Examine(plainLower), Is.EqualTo(SpatialSplitPass.Suppressed), "and it is marked");
            Assert.That(split.LastSource, Is.EqualTo(lower));
            NpcBoneSplitRecord bones = split.LastBone;
            Assert.That(bones.clusters == 2 && bones.groups == 2 && bones.fullGroups == 2, Is.True, DescribeBoneRecord(bones));
            Assert.That(bones.referencedVertices, Is.GreaterThan(0));
            Assert.That(bones.originSteps, Is.GreaterThan(bones.referencedVertices), "the cap's vertices were walked back to the trunk's");

            // Two ordinary objects of the source's convexes, each its own bone's, both the NPC's still.
            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA), Is.True);
            Assert.That(root.Owners.TryGet(b, out PhysicsFragmentOwner ownerB), Is.True);
            Assert.That(ownerA.Shape.ConvexCount == 1 && ownerB.Shape.ConvexCount == 1, Is.True, "a convex each");
            Assert.That(new[] { ownerA.Shape.ConvexBone(0), ownerB.Shape.ConvexBone(0) }, Is.EquivalentTo(new[] { 0, 2 }), "the trunk's and the hand's");
            Assert.That(ownerA.Shape.ConvexBone(0), Is.EqualTo(0), "the trunk's part (more triangles: its faces and the cap) on the positive side");
            Assert.That(ownerA.Body.mass + ownerB.Body.mass, Is.EqualTo(lowerMass).Within(1e-3f), "the source's mass shared out");
            Assert.That(root.NpcBones.Knows(lower), Is.False, "the split's source record went");
            Assert.That(root.NpcBones.TryGet(a, out NpcBoneFragment ra), Is.True, "to both");
            Assert.That(root.NpcBones.TryGet(b, out NpcBoneFragment rb), Is.True);
            Assert.That(ReferenceEquals(ra.Lineage, rb.Lineage), Is.True);
            Assert.That(ra.Piece.Present, Is.EqualTo(new[] { true, false, false, true }), "the trunk's side keeps the trunk and the hip");
            Assert.That(rb.Piece.Present, Is.EqualTo(new[] { false, false, true, false }), "the hand's side keeps the hand (the shoulder was not held: its connection to the hand not used)");
            Assert.That(root.Geometry.TryGetGeometry(a, out VpStoredGeometry ga), Is.True);
            Assert.That(root.Geometry.TryGetGeometry(b, out VpStoredGeometry gb), Is.True);
            Assert.That(TrianglesOf(root, ga).Count, Is.EqualTo(bones.positiveTriangles));
            Assert.That(TrianglesOf(root, gb).Count, Is.EqualTo(bones.negativeTriangles));
            Assert.That(split.Examine(a) == SpatialSplitPass.FewConvexes && split.Examine(b) == SpatialSplitPass.FewConvexes, Is.True, "one convex each: nothing more");
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **An NPC's piece its bones connect is not split, and no other search is tried for it.** The hand stands clear of the
        /// trunk -- a plane separates them, and the same body that is not an NPC's is split by it -- but the trunk's joint and the
        /// shoulder the hand hangs from are below the waist, so the piece below keeps the trunk, the shoulder and the hand together: one
        /// cluster, one group. The NPC's lower side ends as a search that found nothing, marked, and stays one object.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitBone_AnNpcsPieceItsBonesConnect_IsNotSplit_AndNothingFallsBack()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.SetPairProtect(false);   // NPC-PAIR-DEFAULT-1: this case is about the bone-aware decision (and its fallback); the protected combination, on by default, is off here
            Assert.That(split.BoneFallback, Is.True, "the fallback is on by default (NPC-BONE-FALLBACK-DEFAULT-1)");
            split.BoneFallback = false;   // this case is about the bones alone (-zantetsuNpcBoneFallbackOff)
            LogicalFragmentId npcBody = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 0f), true, shoulderJointY: -0.1f, trunkJointY: -0.2f);
            LogicalFragmentId plainBody = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 12f), false);
            yield return null;
            var npcSides = new LogicalFragmentId[2];
            var plainSides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, npcBody, npcSides);
            yield return CutAtTheWaist(root, plainBody, plainSides);
            LogicalFragmentId lower = npcSides[1];
            Assert.That(root.NpcBones.TryGet(lower, out NpcBoneFragment record) && record.Piece.IsJointKept(0) && record.Piece.IsJointKept(1), Is.True,
                "the layout: the trunk's joint and the shoulder below the waist");

            remaining[0] = 1.0;
            yield return Until(() => !split.HasAttempt && split.BoneSearches == 1 && split.Published == 1, "both lower sides were tried");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine("bones: " + DescribeBoneRecord(split.LastBone));
            Assert.That(split.BoneNotSeparable, Is.EqualTo(1));
            Assert.That(split.LastBone.reason, Is.EqualTo(NpcBoneSplitSearch.ReasonOneGroup));
            Assert.That(split.LastBone.clusters, Is.EqualTo(1));
            Assert.That(split.PublishedBone, Is.Zero, "the NPC's piece is not split");
            Assert.That(split.BoneFellBack != 0 || split.PublishedBoneFallback != 0, Is.False, "the fallback off: nothing fell back");
            Assert.That(split.LastSource, Is.EqualTo(plainSides[1]), "the one publication is the plain body's, by a plane");
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Suppressed), "the NPC's piece is marked: no other search for it");
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner owner) && owner.Shape.ConvexCount == 2, Is.True, "and stays one object");
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Nothing is decided before the cut's geometry is committed.** The waist cut's geometry work is held back: its two
        /// sides stand with their owners, but they are no NPC record's yet (the source's stays until the commit) and no attempt
        /// is issued for either. Let go, the commit hands the records on and the lower side is decided by its bones and split.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitBone_NothingIsDecidedBeforeTheCutsGeometryCommits()
        {
            HoldingExecutor geometry = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.GeometryPool
                    ? geometry = Held(new HoldingExecutor(WorkerPoolExecutor.GeometryPool(2)) { HoldEverything = true })
                    : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.SetPairProtect(false);   // NPC-PAIR-DEFAULT-1: this case is about the bone-aware decision (and its fallback); the protected combination, on by default, is off here
            LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 0f), true, handLowX: 0.8f);
            yield return null;
            ProvisionalCutAsk ask = Ask(body, k_planeUp);
            Assert.That(root.TryAsk(in ask), Is.True, "the cut was asked");
            yield return null;
            List<CutOperationId> admitted = AdmittedFor(root, new[] { body });
            Assert.That(admitted.Count, Is.EqualTo(1));
            CutOperationId operation = admitted[0];
            yield return Until(() => geometry.HoldingCount > 0, "the cut's geometry work finished and is held back");

            // Open budget, frames on: nothing of the split for these, and the records still the source's.
            remaining[0] = 1.0;
            for (int i = 0; i < 30; i++)
            {
                yield return null;
                Assert.That(root.Geometry.StageOf(operation), Is.Not.EqualTo(CutGeometryStage.Committed), "the layout: held");
            }

            Assert.That(split.Issued + split.BoneIssued, Is.Zero, "no attempt before the commit");
            LogicalCutOperation made = OperationOf(root, operation);   // read now: the sides are named once the cut is published
            Assert.That(made.positive.IsSet && made.negative.IsSet, Is.True, "the layout: the cut's two sides are named");
            SpatialSplitPass before = split.Examine(made.negative);
            TestContext.Out.WriteLine("the lower side before the commit: " + before);
            Assert.That(before, Is.Not.EqualTo(SpatialSplitPass.Candidate), "not a candidate before its geometry is committed");
            Assert.That(root.NpcBones.Knows(body) && !root.NpcBones.Knows(made.negative) && !root.NpcBones.Knows(made.positive), Is.True,
                "the records follow the geometry: the source's until the commit");

            geometry.HoldEverything = false;
            geometry.ReleaseEverything();
            yield return Until(() => root.Geometry.StageOf(operation) == CutGeometryStage.Committed, "the cut committed");
            Assert.That(root.NpcBones.Knows(made.negative) && root.NpcBones.Knows(made.positive) && !root.NpcBones.Knows(body), Is.True);
            yield return Until(() => !split.HasAttempt && split.PublishedBone + split.BoneNotSeparable + split.BoneInsufficient >= 1, "the lower side was decided");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            Assert.That(split.PublishedBone, Is.EqualTo(1), "split by its bones, after the commit");
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A decision overtaken by a re-cut is not published, and the re-cut's pieces are decided from the vertices it made.**
        /// The lower side's decision is held back while the lower side is cut again, through the trunk and the hand: the result
        /// is given back unused and marks nothing. The re-cut's commit records where its new vertices came from (the second
        /// cut's, walked back through the first's to the base), so each of its two pieces -- each holding a part of the trunk
        /// (bone 0) and a part of the hand (bone 1), the same bones on both -- is decided by itself and split into its own two:
        /// nothing of one piece is mixed with the other's.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitBone_AStaleDecisionIsNotPublished_AndARecutsPiecesAreDecidedFromTheVerticesItMade()
        {
            HoldingExecutor background = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.BackgroundPool
                    ? background = Held(new HoldingExecutor(WorkerPoolExecutor.BackgroundPool(2)))
                    : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            split.SetPairProtect(false);   // NPC-PAIR-DEFAULT-1: this case is about the bone-aware decision (and its fallback); the protected combination, on by default, is off here
            LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 0f), true);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            LogicalFragmentId lower = sides[1];

            background.HoldEverything = true;
            remaining[0] = 1.0;
            yield return Until(() => split.HasAttempt && split.AttemptFragment == lower, "the lower side's attempt was issued");
            remaining[0] = 0.0;
            Assert.That(split.BoneIssued, Is.EqualTo(1));
            yield return Until(() => background.HoldingCount > 0, "its decision finished and is held back");

            // Cut again while the attempt is out: through the trunk's lower part and the hand (z = 0).
            var pieces = new LogicalFragmentId[2];
            yield return CutInTwo(root, lower, new float4(0f, 0f, 1f, 0f), pieces);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the re-cut's record is over");
            background.HoldEverything = false;
            background.ReleaseEverything();
            yield return Until(() => !split.HasAttempt, "the result arrived and was given back");
            Assert.That(split.EndedAs(SpatialSplitEnd.Stale), Is.EqualTo(1), "stale: the lower side is no longer what it was decided for");
            Assert.That(split.PublishedBone + split.Published, Is.Zero);
            Assert.That(split.SuppressedNow, Is.Zero, "nothing recorded as failed");

            // The re-cut's pieces: its new vertices' origins kept, nothing the waist took off back, the same bones on both.
            Assert.That(root.NpcBones.TryGet(pieces[0], out NpcBoneFragment p0), Is.True);
            Assert.That(root.NpcBones.TryGet(pieces[1], out NpcBoneFragment p1), Is.True);
            Assert.That(p0.Lineage.Chunks.Length, Is.EqualTo(2), "the waist's and the re-cut's new vertices, each with their origins");
            Assert.That(!p0.Piece.IsJointKept(1) && !p1.Piece.IsJointKept(1) && !p0.Piece.IsJointKept(0) && !p1.Piece.IsJointKept(0), Is.True,
                "the joints the waist left above stay off (the re-cut's plane takes nothing else: every joint lies on it, on both sides)");
            Assert.That(p0.Piece.Present, Is.EqualTo(new[] { true, true, true, true }));
            Assert.That(p1.Piece.Present, Is.EqualTo(new[] { true, true, true, true }));
            foreach (LogicalFragmentId piece in pieces)
            {
                Assert.That(root.Owners.TryGet(piece, out PhysicsFragmentOwner owner), Is.True);
                var bones = new List<int>();
                for (int c = 0; c < owner.Shape.ConvexCount; c++) bones.Add(owner.Shape.ConvexBone(c));
                Assert.That(bones, Is.EquivalentTo(new[] { 0, 2 }), "each piece: a part of the trunk and a part of the hand");
            }

            int ownersBefore = root.Owners.Count;
            remaining[0] = 1.0;
            yield return Until(() => !split.HasAttempt && split.PublishedBone + split.BoneNotSeparable + split.BoneInsufficient >= 2, "both pieces were decided");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(split.DescribeBone());
            Assert.That(split.BoneInsufficient, Is.Zero, "every vertex of the re-cut was known");
            Assert.That(split.PublishedBone, Is.EqualTo(2), "each piece split into its own two");
            Assert.That(split.LastBone.originSteps, Is.GreaterThan(split.LastBone.referencedVertices), "the walk went through the cuts' vertices");
            Assert.That(root.Owners.Count, Is.EqualTo(ownersBefore + 2));
            var seen = new HashSet<LogicalFragmentId>();
            foreach (LogicalFragmentId piece in pieces)
            {
                Assert.That(root.Ledger.TryGetSplitChildren(piece, out LogicalFragmentId first, out LogicalFragmentId second), Is.True, "replaced by its own split");
                foreach (LogicalFragmentId child in new[] { first, second })
                {
                    Assert.That(seen.Add(child), Is.True, "no child is another piece's");
                    Assert.That(root.Owners.TryGet(child, out PhysicsFragmentOwner owner) && owner.Shape.ConvexCount == 1, Is.True);
                }
            }

            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The piece probe's line** (NPC-BONE-PROBE-1, diagnosis only): after the bones split the NPC's lower side, each of the two says
        /// what made it (the lower side, by the bones), and the lower side -- retired -- how its last attempt ended and what the bones said.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitBone_Probe_APieceSaysWhatMadeIt_AndItsSourcesLastDecision()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.SetPairProtect(false);   // NPC-PAIR-DEFAULT-1: this case is about the bone-aware decision (and its fallback); the protected combination, on by default, is off here
            LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 0f), true, handLowX: 0.8f);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            LogicalFragmentId lower = sides[1];
            Assert.That(split.DescribePiece(lower), Does.Contain("not attempted").Or.Contain("no note"), "before its attempt");
            remaining[0] = 1.0;
            yield return Until(() => !split.HasAttempt && split.PublishedBone == 1, "the lower side was split by the bones");
            remaining[0] = 0.0;
            string child = split.DescribePiece(split.LastNegative), source = split.DescribePiece(lower);
            TestContext.Out.WriteLine(child);
            TestContext.Out.WriteLine(source);
            Assert.That(child, Does.Contain("fragment " + split.LastNegative.value).And.Contain("made at frame").And.Contain("from fragment " + lower.value + " by the bones").And.Contain("convexes 1 (bones"));
            Assert.That(child, Does.Contain("an NPC's: joints kept"));
            Assert.That(source, Does.Contain("no owner now").And.Contain("its last attempt ended Published").And.Contain("the bones: Separable"));
            split.Settings = default;
            yield return EndWorld(root);
        }

        // ----- NPC-BONE-FALLBACK-1: the fallback to the method as before (on by default since 2026-11-04) ---------------------

        /// <summary>
        /// **The NPC fallback is on by default, and the off argument turns it off** (NPC-BONE-FALLBACK-DEFAULT-1): no argument = on; the
        /// argument of the evaluation's days = on (kept); the off argument = off; both = off. A new world's split starts on.
        /// </summary>
        [Test]
        public void SplitBone_Fallback_IsOnByDefault_OffByItsArgument_AndOffWinsOverOn()
        {
            Assert.That(CutWorldRoot.ReadNpcBoneFallback(new[] { "player.exe" }, out string none), Is.True, "no argument: on");
            Assert.That(none, Does.StartWith("on -- the default since 2026-11-04"));
            Assert.That(CutWorldRoot.ReadNpcBoneFallback(new[] { "player.exe", CutWorldRoot.NpcBoneFallbackArgument }, out string on), Is.True, "the old on argument: on");
            Assert.That(on, Does.StartWith("on, asked for by -zantetsuNpcBoneFallback"));
            Assert.That(CutWorldRoot.ReadNpcBoneFallback(new[] { "player.exe", CutWorldRoot.NpcBoneFallbackOffArgument }, out string off), Is.False, "the off argument: off");
            Assert.That(off, Does.StartWith("off, asked for by -zantetsuNpcBoneFallbackOff").And.Contain("does not go on behind an NPC's own decision")
                .And.Contain("the protected combination's (the default since 2026-11-14)").And.Contain("the bones' alone, as before 2026-11-04, with -zantetsuNpcPairProtectOff as well"));
            Assert.That(CutWorldRoot.ReadNpcBoneFallback(new[] { "player.exe", CutWorldRoot.NpcBoneFallbackArgument, CutWorldRoot.NpcBoneFallbackOffArgument }, out string both), Is.False, "both: off");
            Assert.That(both, Does.Contain("off wins"));
            Assert.That(CutWorldRoot.ReadNpcBoneFallback(null, out _), Is.True, "no arguments at all: on");
        }

        /// <summary>A new world's split has the fallback on (the Editor's launch gives no off argument).</summary>
        [UnityTest]
        public IEnumerator SplitBone_Fallback_ANewWorldsSplitHasItOn()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            Assert.That(root.SpatialSplit.BoneFallback, Is.True);
            yield return null;
            yield return EndWorld(root);
        }


        /// <summary>Every joint kept and every segment part of <paramref name="piece"/> is one of <paramref name="of"/>'s: nothing taken off came back.</summary>
        private static void AssertPartOf(NpcBonePiece piece, NpcBonePiece of, int bones, string what)
        {
            for (int k = 0; k < bones; k++)
            {
                if (piece.IsJointKept(k)) Assert.That(of.IsJointKept(k), Is.True, what + ": joint " + k + " came back");
                if (piece.Lo[k] <= piece.Hi[k])
                {
                    Assert.That(of.Lo[k] <= of.Hi[k] && piece.Lo[k] >= of.Lo[k] && piece.Hi[k] <= of.Hi[k], Is.True, what + ": segment " + k + " came back or grew");
                }
            }
        }

        /// <summary>
        /// **With the fallback on, a placement the bones make is theirs, and the method as before does not run.** The hand overlapping
        /// the trunk, the bones apart below the waist: Separable, published by the bones, nothing fell back.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitBone_Fallback_ABonePlacement_IsTheBones_AndTheMethodAsBeforeDoesNotRun()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.SetPairProtect(false);   // NPC-PAIR-DEFAULT-1: this case is about the bone-aware decision (and its fallback); the protected combination, on by default, is off here
            split.BoneFallback = true;
            LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 0f), true, handLowX: 0.8f);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            remaining[0] = 1.0;
            yield return Until(() => !split.HasAttempt && split.BoneSearches >= 1, "the lower side was decided");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.DescribeBone());
            Assert.That(split.BoneSeparable, Is.EqualTo(1));
            Assert.That(split.BoneFellBack, Is.Zero, "the method as before did not run");
            Assert.That(split.PublishedBone == 1 && split.PublishedBoneFallback == 0 && split.Published == 1, Is.True, "published by the bones");
            Assert.That(root.NpcBones.SplitsWithoutSides, Is.Zero, "the bones' sides went to the records");
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **With the fallback on, a piece the bones connect goes on to the method as before, and its publication is that method's.**
        /// The hand clear of the trunk, the bones connected below the waist: NotSeparable, then a plane splits it -- counted as a
        /// fallback by the plane, never as the bones'. Both sides are still the NPC's, each with the convexes of its own bones and its
        /// own part of the rig, the source's cut by that plane (review 1): the trunk's side has the trunk's and the hip's joints and not
        /// the shoulder's or the hand's, the hand's side the other way; a re-cut of the trunk's side brings none of them back.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitBone_Fallback_APieceTheBonesConnect_IsSplitByThePlane_EachSideKeepingItsOwnBones_AndARecutBringsNothingBack()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.SetPairProtect(false);   // NPC-PAIR-DEFAULT-1: this case is about the bone-aware decision (and its fallback); the protected combination, on by default, is off here
            split.BoneFallback = true;
            LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 0f), true, shoulderJointY: -0.1f, trunkJointY: -0.2f);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            LogicalFragmentId lower = sides[1];
            Assert.That(root.NpcBones.TryGet(lower, out NpcBoneFragment sourceRecord), Is.True);
            NpcBonePiece sourcePiece = sourceRecord.Piece;
            remaining[0] = 1.0;
            yield return Until(() => !split.HasAttempt && split.BoneSearches >= 1, "the lower side was decided");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(split.DescribeBone());
            Assert.That(split.BoneNotSeparable, Is.EqualTo(1));
            Assert.That(split.LastBone.reason, Is.EqualTo(NpcBoneSplitSearch.ReasonOneGroup));
            Assert.That(split.BoneFellBack, Is.EqualTo(1), "connected by the bones: on to the method as before");
            Assert.That(split.PublishedBone, Is.Zero, "not counted as the bones'");
            Assert.That(split.PublishedBoneFallback == 1 && split.PublishedFallbackPlane == 1 && split.Published == 1, Is.True, "published by a plane");
            Assert.That(split.FallbackEndedAs(SpatialSplitEnd.Published), Is.EqualTo(1));
            Assert.That(split.LastSource, Is.EqualTo(lower));

            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA) && root.Owners.TryGet(b, out PhysicsFragmentOwner ownerB), Is.True);
            root.Owners.TryGet(b, out ownerB);
            Assert.That(ownerA.Shape.ConvexCount == 1 && ownerB.Shape.ConvexCount == 1, Is.True);
            Assert.That(new[] { ownerA.Shape.ConvexBone(0), ownerB.Shape.ConvexBone(0) }, Is.EquivalentTo(new[] { 0, 2 }), "each side's convex keeps its bone");
            Assert.That(root.NpcBones.Knows(lower), Is.False, "the source's record went");
            Assert.That(root.NpcBones.TryGet(a, out NpcBoneFragment ra) && root.NpcBones.TryGet(b, out NpcBoneFragment rb), Is.True, "both sides are the NPC's");
            root.NpcBones.TryGet(b, out rb);
            Assert.That(root.NpcBones.SplitsByPlane == 1 && root.NpcBones.SplitsWithoutSides == 0 && root.NpcBones.SplitsByConvexes == 0, Is.True, "followed by the plane");
            Assert.That(ReferenceEquals(ra.Lineage, rb.Lineage), Is.True);
            bool aIsTrunk = ownerA.Shape.ConvexBone(0) == 0;
            NpcBonePiece trunkPiece = aIsTrunk ? ra.Piece : rb.Piece, handPiece = aIsTrunk ? rb.Piece : ra.Piece;
            Assert.That(trunkPiece.IsJointKept(0) && trunkPiece.IsJointKept(3), Is.True, "the trunk's side keeps the trunk's and the hip's joints");
            Assert.That(trunkPiece.IsJointKept(1) || trunkPiece.IsJointKept(2), Is.False, "and not the shoulder's or the hand's (they went to the other side)");
            Assert.That(handPiece.IsJointKept(1) && handPiece.IsJointKept(2), Is.True, "the hand's side keeps the shoulder's and the hand's joints");
            Assert.That(handPiece.IsJointKept(0) || handPiece.IsJointKept(3), Is.False, "and not the trunk's or the hip's");
            Assert.That(trunkPiece.Lo[2] > trunkPiece.Hi[2] && handPiece.Lo[3] > handPiece.Hi[3], Is.True, "the shoulder-to-hand segment off the trunk's side, the trunk-to-hip one off the hand's");
            Assert.That(trunkPiece.Lo[1] <= trunkPiece.Hi[1] && handPiece.Lo[1] <= handPiece.Hi[1] && trunkPiece.Hi[1] <= handPiece.Lo[1], Is.True, "the trunk-to-shoulder segment crossed: its part on each side");
            AssertPartOf(trunkPiece, sourcePiece, 4, "the trunk's side");
            AssertPartOf(handPiece, sourcePiece, 4, "the hand's side");

            // a re-cut of the trunk's side (z = 0 through its box): its pieces hold nothing the trunk's side did not
            LogicalFragmentId trunk = aIsTrunk ? a : b;
            var pieces = new LogicalFragmentId[2];
            yield return CutInTwo(root, trunk, new float4(0f, 0f, 1f, 0f), pieces);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the re-cut's record is over");
            foreach (LogicalFragmentId piece in pieces)
            {
                Assert.That(root.NpcBones.TryGet(piece, out NpcBoneFragment p), Is.True, "the re-cut's pieces are the NPC's");
                AssertPartOf(p.Piece, trunkPiece, 4, "a piece of the re-cut");
                Assert.That(p.Piece.IsJointKept(1) || p.Piece.IsJointKept(2), Is.False, "the other side's joints do not come back");
            }

            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// The interleaved combs (<see cref="AddInterleavedCombs"/>) made an NPC's: a bone a convex (comb A's spine 0 with its teeth 1 and
        /// 2, comb B's spine 3 -- a child of 0 -- with its teeth 4 and 5), each convex's boxes weighted to its bone, every joint at its
        /// box's centre below the waist: the bones connect everything there.
        /// </summary>
        private LogicalFragmentId AddNpcCombs(CutWorldRoot root)
        {
            LogicalFragmentId fragment = AddInterleavedCombs(root, new Vector3(0f, 0f, 0f));
            Assert.That(root.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) && root.Geometry.TryGetGeometry(fragment, out VpStoredGeometry _), Is.True);
            root.Geometry.TryGetGeometry(fragment, out VpStoredGeometry geometry);
            const int boxes = 6;
            owner.Shape.SetConvexBones(new[] { 0, 1, 2, 3, 4, 5 });
            var weights = new List<ulong>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                for (int box = 0; box < boxes; box++)
                {
                    for (int face = 0; face < k_faces.Length; face++)
                    {
                        if (k_faces[face].submesh != submesh) continue;
                        for (int k = 0; k < 4; k++) weights.Add(NpcVertexBones.Pack((ushort)box, NpcVertexBones.None, NpcVertexBones.None, NpcVertexBones.None));
                    }
                }
            }

            Assert.That(weights.Count, Is.EqualTo(geometry.vertexCount), "the layout: a weight a vertex");
            NpcVertexBones table = NpcVertexBones.FromEntries(weights.ToArray(), 4);
            var lineage = new NpcBoneLineage(boxes, new[] { -1, 0, 0, 0, 3, 3 }, new[] { true, true, true, true, true, true },
                new[] { new float3(0f, -0.5f, 0f), new float3(1.125f, -0.35f, 0f), new float3(1.125f, -0.95f, 0f), new float3(3f, -0.85f, 0f), new float3(1.875f, -0.65f, 0f), new float3(1.875f, -1.25f, 0f) },
                table, (uint)geometry.vertexStart, geometry.vertexCount, null);
            table.Release();   // the lineage's hold is the one left
            root.NpcBones.RegisterRoot(fragment, lineage);
            return fragment;
        }

        /// <summary>
        /// **A fallback split by no plane keeps on each side the bones with a convex there** (review 1). The NPC's combs below the waist:
        /// connected by the bones, no plane separates them, the graph does. Comb A's side keeps the joints of 0, 1 and 2 and the segments
        /// between them; comb B's side those of 3, 4 and 5; the segment from 0 to 3 (its two bones on two sides) neither.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitBone_Fallback_AGraphSplit_KeepsOnEachSideTheBonesWithAConvexThere()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.SetPairProtect(false);   // NPC-PAIR-DEFAULT-1: this case is about the bone-aware decision (and its fallback); the protected combination, on by default, is off here
            split.Settings = SplitOnWithGraph();
            split.BoneFallback = true;
            Assert.That(split.GraphEffective, Is.True, split.GraphRefusal);
            LogicalFragmentId body = AddNpcCombs(root);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, lowerConvexes: 6);
            LogicalFragmentId lower = sides[1];
            Assert.That(root.NpcBones.TryGet(lower, out NpcBoneFragment sourceRecord), Is.True);
            NpcBonePiece sourcePiece = sourceRecord.Piece;
            // The lower side's own end: replaced by its publication (with several out at once the side above is decided beside it,
            // so "no attempt out and one search" is not this piece's end).
            remaining[0] = 1.0;
            yield return Until(() => !split.HasAttemptFor(lower) && !root.Ledger.IsCurrentTarget(lower), "the lower side was decided and replaced");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(split.DescribeBone());
            Assert.That(split.LastSource, Is.EqualTo(lower), "the publication is the lower side's");
            if (split.MaxAttempts == 1) Assert.That(split.BoneNotSeparable == 1 && split.BoneFellBack == 1, Is.True, "connected by the bones, then the method as before");
            Assert.That(split.BoneNotSeparable >= 1 && split.BoneFellBack >= 1, Is.True, "connected by the bones, then the method as before");
            Assert.That(split.PublishedFallbackGraph == 1 && split.PublishedBone == 0, Is.True, "published by the graph");
            Assert.That(root.NpcBones.SplitsByConvexes == 1 && root.NpcBones.SplitsByPlane == 0 && root.NpcBones.SplitsWithoutSides == 0, Is.True, "followed by the convexes' bones");

            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA), Is.True);
            Assert.That(root.NpcBones.TryGet(a, out NpcBoneFragment ra) && root.NpcBones.TryGet(b, out NpcBoneFragment rb), Is.True, "both sides are the NPC's");
            root.NpcBones.TryGet(b, out rb);
            bool aIsCombA = ownerA.Shape.ConvexBone(0) <= 2;
            NpcBonePiece combA = aIsCombA ? ra.Piece : rb.Piece, combB = aIsCombA ? rb.Piece : ra.Piece;
            for (int k = 0; k < 6; k++)
            {
                Assert.That(combA.IsJointKept(k), Is.EqualTo(k <= 2), "comb A's side, joint " + k);
                Assert.That(combB.IsJointKept(k), Is.EqualTo(k >= 3), "comb B's side, joint " + k);
            }

            Assert.That(combA.Lo[1] <= combA.Hi[1] && combA.Lo[2] <= combA.Hi[2] && combA.Lo[3] > combA.Hi[3] && combA.Lo[4] > combA.Hi[4], Is.True, "comb A's segments only");
            Assert.That(combB.Lo[4] <= combB.Hi[4] && combB.Lo[5] <= combB.Hi[5] && combB.Lo[3] > combB.Hi[3] && combB.Lo[1] > combB.Hi[1], Is.True, "comb B's segments only; 0 to 3 on neither");
            AssertPartOf(combA, sourcePiece, 6, "comb A's side");
            AssertPartOf(combB, sourcePiece, 6, "comb B's side");
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// The wrapped body (<see cref="AddWrappedBody"/>: one convex round the trunk and the detached hand) made an NPC's: the convex
        /// of bone 0, the trunk's boxes weighted to 0 and the hand's to 2, the rig of <see cref="AddNpcBodyWithAHand"/> with every joint
        /// below the waist (the bones connect the lower side whole).
        /// </summary>
        private LogicalFragmentId AddNpcWrappedBody(CutWorldRoot root)
        {
            LogicalFragmentId fragment = AddWrappedBody(root, new Vector3(0f, 0f, 0f));
            Assert.That(root.Owners.TryGet(fragment, out PhysicsFragmentOwner owner) && root.Geometry.TryGetGeometry(fragment, out VpStoredGeometry _), Is.True);
            root.Geometry.TryGetGeometry(fragment, out VpStoredGeometry geometry);
            owner.Shape.SetConvexBones(new[] { 0 });
            var weights = new List<ulong>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                for (int box = 0; box < 2; box++)
                {
                    for (int face = 0; face < k_faces.Length; face++)
                    {
                        if (k_faces[face].submesh != submesh) continue;
                        for (int k = 0; k < 4; k++) weights.Add(NpcVertexBones.Pack((ushort)(box == 0 ? 0 : 2), NpcVertexBones.None, NpcVertexBones.None, NpcVertexBones.None));
                    }
                }
            }

            Assert.That(weights.Count, Is.EqualTo(geometry.vertexCount), "the layout: a weight a vertex");
            NpcVertexBones table = NpcVertexBones.FromEntries(weights.ToArray(), 4);
            var lineage = new NpcBoneLineage(4, new[] { -1, 0, 1, 0 }, new[] { true, true, true, true },
                new[] { new float3(0f, -0.2f, 0f), new float3(HandLowX + 0.25f, -0.1f, 0f), new float3(HandLowX + 0.25f, -0.5f, 0f), new float3(0f, -0.9f, 0f) },
                table, (uint)geometry.vertexStart, geometry.vertexCount, null);
            table.Release();   // the lineage's hold is the one left
            root.NpcBones.RegisterRoot(fragment, lineage);
            return fragment;
        }

        /// <summary>
        /// **An NPC's piece of one convex is looked at only with the fallback, and the method as before may cut it** (review 2). The
        /// wrapped NPC's lower side: one convex, the bones connected. The fallback off: too few convexes, as before. On: the bones say
        /// NotSeparable (one convex), the mesh-led plane cuts the convex -- a fallback by the convex cut, its records by that plane. (The
        /// side above, one convex round the trunk's upper part, is looked at too: one part of the mesh, nothing found.)
        /// </summary>
        [UnityTest]
        public IEnumerator SplitBone_Fallback_APieceOfOneConvex_IsLookedAtWithTheFallback_AndCutByTheMethodAsBefore()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitCutClosed(root, remaining, 12);
            split.SetPairProtect(false);   // NPC-PAIR-DEFAULT-1: this case is about the bone-aware decision (and its fallback); the protected combination, on by default, is off here
            LogicalFragmentId body = AddNpcWrappedBody(root);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, 1);
            LogicalFragmentId lower = sides[1];
            Assert.That(root.NpcBones.TryGet(lower, out NpcBoneFragment sourceRecord), Is.True);
            split.BoneFallback = false;
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.FewConvexes), "the fallback off: an NPC's piece needs two convexes, as before");
            split.BoneFallback = true;
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Candidate), "the fallback on: looked at as any owner of one convex");

            remaining[0] = 1.0;
            yield return Until(() => split.Published >= 1 || split.EndedAs(SpatialSplitEnd.NotBuildable) + split.EndedAs(SpatialSplitEnd.NotClassified) > 0
                                     || (split.BoneSearches >= 2 && split.IsIdle && !split.HasAttempt),
                "the lower side was decided and ended");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(split.DescribeBone());
            Assert.That(split.BoneFellBack, Is.GreaterThanOrEqualTo(1), "one convex: not separable by the bones, then the method as before");
            Assert.That(split.BoneSeparable, Is.Zero);
            Assert.That(split.PublishedFallbackCut == 1 && split.PublishedBone == 0 && split.Published == 1, Is.True, "published by the convex cut; last refusal: " + split.LastRefusal);
            Assert.That(split.LastSource, Is.EqualTo(lower), "the lower side");
            Assert.That(split.ConvexesCut, Is.EqualTo(1));
            Assert.That(root.NpcBones.SplitsByPlane, Is.EqualTo(1), "its records followed by the mesh-led plane");
            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(root.NpcBones.TryGet(a, out NpcBoneFragment ra) && root.NpcBones.TryGet(b, out NpcBoneFragment rb), Is.True, "both sides are the NPC's");
            root.NpcBones.TryGet(b, out rb);
            Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA) && root.Owners.TryGet(b, out PhysicsFragmentOwner ownerB), Is.True);
            root.Owners.TryGet(b, out ownerB);
            Assert.That(ownerA.Shape.ConvexBone(0) == 0 && ownerB.Shape.ConvexBone(0) == 0, Is.True, "each part of the cut convex keeps its bone");
            bool aHasTrunk = ra.Piece.IsJointKept(0);
            NpcBonePiece trunkPiece = aHasTrunk ? ra.Piece : rb.Piece, handPiece = aHasTrunk ? rb.Piece : ra.Piece;
            Assert.That(trunkPiece.IsJointKept(0) && trunkPiece.IsJointKept(3) && !trunkPiece.IsJointKept(1) && !trunkPiece.IsJointKept(2), Is.True, "the trunk's side: the trunk's and the hip's joints");
            Assert.That(handPiece.IsJointKept(1) && handPiece.IsJointKept(2) && !handPiece.IsJointKept(0) && !handPiece.IsJointKept(3), Is.True, "the hand's side: the shoulder's and the hand's");
            AssertPartOf(trunkPiece, sourceRecord.Piece, 4, "the trunk's side");
            AssertPartOf(handPiece, sourceRecord.Piece, 4, "the hand's side");
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A fallback the method as before leaves unfinished stays unfinished** -- not read as a definite failure: no mark, the
        /// fragment deferred. The hand overlapping the trunk (no plane), the bones connected, the graph's allocation failing (injected).
        /// </summary>
        [UnityTest]
        public IEnumerator SplitBone_Fallback_AnUnfinishedMethodAsBefore_StaysUnfinished()
        {
            ResetGraphTestHooks();
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.SetPairProtect(false);   // NPC-PAIR-DEFAULT-1: this case is about the bone-aware decision (and its fallback); the protected combination, on by default, is off here
            split.Settings = SplitOnWithGraph();
            split.BoneFallback = true;
            Assert.That(split.GraphEffective, Is.True, split.GraphRefusal);
            LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 0f), true, handLowX: 0.8f, shoulderJointY: -0.1f, trunkJointY: -0.2f);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            LogicalFragmentId lower = sides[1];
            try
            {
                CutSpatialSplit.TestGraphFailAllocation = 0;   // the engine's first allocation fails on the worker: Unfinished(allocation)
                remaining[0] = 1.0;
                yield return Until(() => !split.HasAttempt && split.BoneSearches >= 1, "the lower side was decided");
                remaining[0] = 0.0;
                TestContext.Out.WriteLine(split.DescribeBone());
                Assert.That(split.BoneNotSeparable == 1 && split.BoneFellBack == 1, Is.True, "connected by the bones, then the method as before");
                Assert.That(split.LastGraph.verdict, Is.EqualTo(SpatialSplitGraphVerdict.Unfinished));
                Assert.That(split.FallbackEndedAs(SpatialSplitEnd.Unfinished) == 1 && split.EndedAs(SpatialSplitEnd.Unfinished) == 1, Is.True, "Unfinished");
                Assert.That(split.EndedAs(SpatialSplitEnd.NotFound), Is.Zero, "not a definite failure");
                Assert.That(split.SuppressedNow, Is.Zero, "nothing is recorded about the input");
                Assert.That(split.DeferredNow, Is.EqualTo(1));
                Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Deferred));
                Assert.That(split.PublishedBone + split.PublishedBoneFallback, Is.Zero);
            }
            finally
            {
                ResetGraphTestHooks();
            }

            split.Settings = default;
            yield return EndWorld(root);
        }
    }
}
