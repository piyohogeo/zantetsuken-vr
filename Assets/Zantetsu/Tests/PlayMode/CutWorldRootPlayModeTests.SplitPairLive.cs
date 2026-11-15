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
    /// NPC-PAIR-LIVE-1: the protected pairwise combination of the bones and the geometry (the evaluation's pair-or-protect, mode 2)
    /// in the product's own world, behind its switch: an NPC's attempt is the combination's and no other owner's; a placement it makes
    /// is shared out, classified, checked and published by the existing path; each child's part of the rig is what that placement
    /// makes it need -- a bone both need on both, nothing the source had lost back; where it places nothing the method as before goes
    /// on with its own following; a result of an input that changed is not published; and everything it took is given back. The
    /// fixtures are the bone split's (the trunk with a hand, made an NPC's lineage by hand).
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static void AssertClosedChild(CutWorldRoot root, LogicalFragmentId child, string what)
        {
            Assert.That(root.Geometry.TryGetGeometry(child, out VpStoredGeometry geometry), Is.True, what + ": its geometry is settled");
            (int triangles, int boundary, int nonManifold, int badWinding) = TopologySurfaceOf(root, geometry, out int _, out int _);
            Assert.That(triangles > 0 && boundary == 0 && nonManifold == 0 && badWinding == 0, Is.True,
                what + ": closed on its topology (triangles " + triangles + ", boundary " + boundary + ", non-manifold " + nonManifold + ", bad winding " + badWinding + ")");
        }

        /// <summary>
        /// **With the switch, an NPC's piece is placed by the combination and published by the existing path; another owner's piece is
        /// decided as before.** The argument is read as given. The NPC's lower side (the hand overlapping the trunk, the bones apart):
        /// issued as the combination's, placed by it, published once and counted once -- not as the bones' -- into two objects with a
        /// convex each, every triangle and convex of the source in exactly one of them, each closed on its topology. Each child keeps
        /// the bones its side needs (its convex's; the hip, which nothing of either side needs, on neither), and no joint the waist took
        /// off is back. The same body that is no NPC's is not the combination's: tried as before, not split. All of it under the
        /// defaults (eight attempts, the one-component pass-over).
        /// </summary>
        [UnityTest]
        public IEnumerator PairLive_WithTheSwitch_AnNpcsPieceIsPlacedByTheCombinationAndPublished_AndAnotherOwnersIsDecidedAsBefore()
        {
            // NPC-PAIR-DEFAULT-1: the launch's arguments -- on with none, the old argument still on, the off argument off and winning
            Assert.That(CutWorldRoot.ReadNpcPairProtect(new[] { "player.exe", "-force-d3d12" }, out string none), Is.True, "no argument: on");
            Assert.That(none, Does.StartWith("on -- the default since 2026-11-14").And.Contain(CutWorldRoot.NpcPairProtectOffArgument));
            Assert.That(CutWorldRoot.ReadNpcPairProtect(null, out _), Is.True, "no arguments at all: on");
            Assert.That(CutWorldRoot.ReadNpcPairProtect(new[] { "player.exe", CutWorldRoot.NpcPairProtectArgument }, out string old), Is.True, "the old argument: on");
            Assert.That(old, Does.StartWith("on, asked for by -zantetsuNpcPairProtect "));
            Assert.That(CutWorldRoot.ReadNpcPairProtect(new[] { "player.exe", CutWorldRoot.NpcPairProtectOffArgument }, out string off), Is.False, "the off argument: off");
            Assert.That(off, Is.EqualTo("off, asked for by -zantetsuNpcPairProtectOff"));
            Assert.That(CutWorldRoot.ReadNpcPairProtect(new[] { "player.exe", CutWorldRoot.NpcPairProtectArgument, CutWorldRoot.NpcPairProtectOffArgument }, out string both), Is.False, "both: off wins");
            Assert.That(CutWorldRoot.ReadNpcPairProtect(new[] { "player.exe", CutWorldRoot.NpcPairProtectOffArgument, CutWorldRoot.NpcPairProtectArgument }, out _), Is.False, "both, the other way round: off wins");
            Assert.That(both, Does.Contain("off wins"));
            // the reuse's own argument: on with none; off keeps the combination and turns the reuse off
            Assert.That(CutSpatialSplit.ReadPairReuse(new[] { "player.exe" }) && CutSpatialSplit.ReadPairReuse(null), Is.True, "no argument: the reuse on");
            Assert.That(CutSpatialSplit.ReadPairReuse(new[] { "player.exe", CutSpatialSplit.PairReuseOffArgument }), Is.False, "the reuse's off argument");
            Assert.That(CutWorldRoot.ReadNpcPairProtect(new[] { "player.exe", CutSpatialSplit.PairReuseOffArgument }, out _), Is.True, "which leaves the combination on");
            Assert.That(CutSpatialSplit.ReadPairReuse(new[] { "player.exe", CutWorldRoot.NpcPairProtectOffArgument }), Is.True, "and the combination's off argument says nothing of the reuse");
            // the launch line: what was asked, what is in effect, the engine's preparation, the stage
            string line = CutWorldRoot.DescribeNpcPairProtect(none, true, true, "", true, true, true);
            Assert.That(line, Does.StartWith("NPC PAIR PROTECT: asked: on -- the default").And.Contain("in effect: on").And.Contain("mode 2").And.Contain("the graph engine: prepared")
                .And.Contain("reuses the combination's decision").And.Contain("no bent partition after a stage not run"));
            Assert.That(CutWorldRoot.DescribeNpcPairProtect(none, true, true, "", true, true, false), Does.Contain("runs from the start (-zantetsuNpcPairReuseOff)"));
            Assert.That(CutWorldRoot.DescribeNpcPairProtect(off, false, false, "", false, true, true), Does.Contain("in effect: off -- an NPC's piece is decided by its bones").And.Contain("the NPC fallback is on (on)"));
            Assert.That(CutWorldRoot.DescribeNpcPairProtect(none, true, false, "the preparation failed", false, true, true), Does.Contain("OFF though asked for: the preparation failed").And.Contain("the graph engine: NOT prepared"));

            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            // a new world, nothing asked: the combination on and in effect, the engine prepared, the reuse on -- before anything is issued
            Assert.That(split.PairProtect && split.PairEffective && split.PairRefusal == "" && SpatialSplitGraph.IsPrepared, Is.True, "a new world's split has it on: " + split.PairRefusal);
            Assert.That(CutSpatialSplit.PairReuse, Is.True, "and the fallback's graph stage reuses");
            Assert.That(split.Issued == 0 && !split.HasAttempt, Is.True, "settled before the world's additional split began");
            // the combination's own switch: off and on again, the reuse untouched
            split.SetPairProtect(false);
            Assert.That(!split.PairProtect && !split.PairEffective && CutSpatialSplit.PairReuse, Is.True, "off: the combination alone");
            split.SetPairProtect(true);
            Assert.That(split.PairEffective && split.PairRefusal == "" && split.GraphPrepared | SpatialSplitGraph.IsPrepared, Is.True, "on, the graph engine prepared: " + split.PairRefusal);
            Assert.That(split.MaxAttempts == 8 && CutSpatialSplit.SkipOneComponent && VpTopologyComponents.Enabled, Is.True, "the layout: the defaults (eight attempts, the one-component pass-over)");
            LogicalFragmentId npcBody = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 0f), true, handLowX: 0.8f);
            LogicalFragmentId plainBody = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 12f), false, handLowX: 0.8f);
            yield return null;
            var npcSides = new LogicalFragmentId[2];
            var plainSides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, npcBody, npcSides);
            yield return CutAtTheWaist(root, plainBody, plainSides);
            LogicalFragmentId lower = npcSides[1], plainLower = plainSides[1];
            Assert.That(root.NpcBones.TryGet(lower, out NpcBoneFragment sourceRecord), Is.True);
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner), Is.True);
            float lowerMass = (float)lowerOwner.Mass;
            Assert.That(root.Geometry.TryGetGeometry(lower, out VpStoredGeometry lowerGeometry), Is.True);
            int sourceTriangles = TrianglesOf(root, lowerGeometry).Count;
            Assert.That(!sourceRecord.Piece.IsJointKept(0) && !sourceRecord.Piece.IsJointKept(1) && sourceRecord.Piece.IsJointKept(2) && sourceRecord.Piece.IsJointKept(3), Is.True,
                "the layout: the trunk's and the shoulder's joints went with the side above");

            remaining[0] = 1.0;
            yield return Until(() => !split.HasAttempt && split.PairEndedAs(SpatialSplitEnd.Published) + split.PairEndedAs(SpatialSplitEnd.NotFound) + split.PairFellBack >= 1
                                     && split.EndedAs(SpatialSplitEnd.NotFound) >= 1,
                "both lower sides were tried");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(split.DescribePair());
            Assert.That(split.PairIssued, Is.EqualTo(1), "one attempt was an NPC's: the combination's");
            Assert.That(split.BoneIssued, Is.EqualTo(1), "(it is the NPC's attempt)");
            Assert.That(split.PairDecided == 1 && split.PairSeparable == 1 && split.PairFellBack == 0, Is.True, "decided by the combination, apart");
            Assert.That(split.PublishedPair == 1 && split.Published == 1, Is.True, "published once, by the combination; last refusal: " + split.LastRefusal);
            Assert.That(split.PairEndedAs(SpatialSplitEnd.Published), Is.EqualTo(1), "and counted once, by the way it ended");
            Assert.That(split.PublishedBone == 0 && split.BoneSearches == 0 && split.PublishedBoneFallback == 0 && split.PublishedGraph == 0, Is.True, "not as the bones', a fallback's or the graph's");
            Assert.That(split.PairExceptions == 0 && split.PairStale == 0 && split.PairNotRun == 0 && split.PairBonesNotWorkedOut == 0, Is.True);
            Assert.That(split.LastSource, Is.EqualTo(lower));
            Assert.That(split.Examine(plainLower), Is.EqualTo(SpatialSplitPass.Suppressed), "the plain body's lower side: tried as before, nothing found, marked");

            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA), Is.True);
            Assert.That(root.Owners.TryGet(b, out PhysicsFragmentOwner ownerB), Is.True);
            Assert.That(ownerA.Shape.ConvexCount + ownerB.Shape.ConvexCount, Is.EqualTo(2), "every convex of the source in one child, none twice");
            Assert.That(new[] { ownerA.Shape.ConvexBone(0), ownerB.Shape.ConvexBone(0) }, Is.EquivalentTo(new[] { 0, 2 }), "the trunk's and the hand's");
            Assert.That(ownerA.Body.mass + ownerB.Body.mass, Is.EqualTo(lowerMass).Within(1e-3f), "the source's mass shared out");
            Assert.That(root.Geometry.TryGetGeometry(a, out VpStoredGeometry ga), Is.True);
            Assert.That(root.Geometry.TryGetGeometry(b, out VpStoredGeometry gb), Is.True);
            Assert.That(TrianglesOf(root, ga).Count + TrianglesOf(root, gb).Count, Is.EqualTo(sourceTriangles), "every triangle of the source in one child, none twice");
            Assert.That(TrianglesOf(root, ga).Count == split.LastPair.positiveTriangles && TrianglesOf(root, gb).Count == split.LastPair.negativeTriangles, Is.True, "as the combination placed them");
            AssertClosedChild(root, a, "the positive child");
            AssertClosedChild(root, b, "the negative child");

            // The children's parts of the rig: by what each side needs, within what the source still held.
            Assert.That(root.NpcBones.Knows(lower), Is.False, "the split's source record went");
            Assert.That(root.NpcBones.TryGet(a, out NpcBoneFragment ra), Is.True, "to both");
            Assert.That(root.NpcBones.TryGet(b, out NpcBoneFragment rb), Is.True);
            Assert.That(root.NpcBones.SplitsByNeededBones == 1 && root.NpcBones.SplitsWithoutSides == 0 && root.NpcBones.SplitsByConvexes == 0, Is.True, "followed by the bones each side needs");
            bool aIsTrunk = ownerA.Shape.ConvexBone(0) == 0;
            NpcBonePiece trunkPiece = aIsTrunk ? ra.Piece : rb.Piece, handPiece = aIsTrunk ? rb.Piece : ra.Piece;
            Assert.That(handPiece.Present, Is.EqualTo(new[] { false, false, true, false }), "the hand's side keeps the hand");
            Assert.That(!trunkPiece.IsJointKept(0) && !trunkPiece.IsJointKept(1) && !handPiece.IsJointKept(0) && !handPiece.IsJointKept(1), Is.True,
                "the joints the waist took off are on neither child, though the trunk's side needs the trunk's bone");
            Assert.That(!trunkPiece.IsJointKept(2) && !trunkPiece.IsJointKept(3) && !handPiece.IsJointKept(3), Is.True, "the hand's joint is the hand's side's alone; the hip, which neither side needs, is kept by neither");
            AssertPartOf(trunkPiece, sourceRecord.Piece, 4, "the trunk's side");
            AssertPartOf(handPiece, sourceRecord.Piece, 4, "the hand's side");
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// The trunk and a hand clear of it, an NPC's, with a bone both parts are weighted to: bone 0 the trunk's convex (its joint above
        /// the waist), bone 1 the hand's convex (its joint in the hand), bone 2 with no convex of its own (its joint low on the trunk's
        /// axis) -- the hand's boxes weighted to 1 and 2, the trunk's bottom face to 0 and 2, the rest of the trunk to 0. No bone is
        /// another's child. Below the waist the bones connect everything through bone 2, and the geometry stands apart.
        /// </summary>
        private LogicalFragmentId AddNpcWithASharedBone(CutWorldRoot root, Vector3 at, float handLowX = HandLowX)
        {
            float3[] trunk = BoxCornersAt(float3.zero, new float3(1f));
            float3[] hand = BoxCornersAt(new float3(handLowX + 0.25f, -0.5f, 0f), new float3(0.25f));
            var boxes = new List<float3[]> { trunk, hand };
            PhysicsOwnerShape shape = NewBoxesShape(boxes);
            _disposables.Add(shape);
            shape.SetConvexBones(new[] { 0, 1 });
            VpStoredGeometry geometry = AppendBoxesGeometry(root.Storage, boxes);
            var actor = TrackActor(new GameObject("NPC trunk and hand with a shared bone"));
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

            Assert.That(root.TryAddBody(actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, null, false, out LogicalFragmentId fragment), Is.True, "the body was taken into the world");
            _registered.Add(actor);
            var weights = new List<ulong>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                for (int box = 0; box < boxes.Count; box++)
                {
                    for (int face = 0; face < k_faces.Length; face++)
                    {
                        if (k_faces[face].submesh != submesh) continue;
                        ulong entry = box != 0 ? NpcVertexBones.Pack(1, 2, NpcVertexBones.None, NpcVertexBones.None)
                            : face == 4 ? NpcVertexBones.Pack(0, 2, NpcVertexBones.None, NpcVertexBones.None)
                            : NpcVertexBones.Pack(0, NpcVertexBones.None, NpcVertexBones.None, NpcVertexBones.None);
                        for (int k = 0; k < 4; k++) weights.Add(entry);
                    }
                }
            }

            Assert.That(weights.Count, Is.EqualTo(geometry.vertexCount), "the layout: a weight a vertex");
            NpcVertexBones table = NpcVertexBones.FromEntries(weights.ToArray(), 3);
            var lineage = new NpcBoneLineage(3, new[] { -1, -1, -1 }, new[] { true, true, true },
                new[] { new float3(0f, 0.5f, 0f), new float3(handLowX + 0.25f, -0.5f, 0f), new float3(0f, -0.9f, 0f) },
                table, (uint)geometry.vertexStart, geometry.vertexCount, null);
            table.Release();   // the lineage's hold is the one left
            root.NpcBones.RegisterRoot(fragment, lineage);
            return fragment;
        }

        /// <summary>
        /// **A bone both children need is kept by both, and a child is cut and decided again.** The trunk and a hand clear of it, both
        /// weighted to a bone with no convex of its own: the bones connect the lower side whole, the geometry stands apart, and the
        /// combination places the two apart. The trunk's child keeps the trunk's bone and the shared one; the hand's child the hand's
        /// and the shared one -- its joint on both -- and the trunk's joint, which the waist took off, on neither. The trunk's child is
        /// then cut by the ordinary cut: its two sides are the NPC's still, each a part of the child's piece, and each is decided again
        /// by the combination with nothing thrown.
        /// </summary>
        [UnityTest]
        public IEnumerator PairLive_ABoneBothChildrenNeed_IsKeptByBoth_AndAChildIsCutAndDecidedAgain()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            split.SetPairProtect(true);
            Assert.That(split.PairEffective, Is.True, split.PairRefusal);
            LogicalFragmentId body = AddNpcWithASharedBone(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            LogicalFragmentId lower = sides[1];
            Assert.That(root.NpcBones.TryGet(lower, out NpcBoneFragment sourceRecord), Is.True);
            Assert.That(!sourceRecord.Piece.IsJointKept(0) && sourceRecord.Piece.IsJointKept(1) && sourceRecord.Piece.IsJointKept(2), Is.True, "the layout: the trunk's joint went with the side above");

            remaining[0] = 1.0;
            yield return Until(() => !split.HasAttempt && split.PairEndedAs(SpatialSplitEnd.Published) + split.PairEndedAs(SpatialSplitEnd.NotFound) + split.PairFellBack >= 1, "the lower side was decided");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(split.DescribePair());
            Assert.That(split.LastPair.boneAlone, Is.EqualTo(NpcBoneSplitVerdict.NotSeparable), "the layout: the bones alone connect it (" + split.LastPair.boneAloneReason + ")");
            Assert.That(split.PairSeparable >= 1 && split.PublishedPair == 1 && split.PairFellBack == 0, Is.True, "the combination places the two apart and publishes; last refusal: " + split.LastRefusal);
            Assert.That(split.LastSource, Is.EqualTo(lower));
            Assert.That(split.PairBonesNotWorkedOut, Is.Zero, split.LastPairBonesFailure);
            Assert.That(split.PairBonesOnBothSides, Is.EqualTo(1), "one bone both children need");
            Assert.That(split.PairAuxiliaryBones, Is.EqualTo(2), "kept by each with no convex of its own");

            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA), Is.True);
            Assert.That(root.Owners.TryGet(b, out PhysicsFragmentOwner ownerB), Is.True);
            Assert.That(ownerA.Shape.ConvexCount == 1 && ownerB.Shape.ConvexCount == 1, Is.True, "a convex each");
            bool aIsTrunk = ownerA.Shape.ConvexBone(0) == 0;
            LogicalFragmentId trunkChild = aIsTrunk ? a : b, handChild = aIsTrunk ? b : a;
            Assert.That(root.NpcBones.TryGet(trunkChild, out NpcBoneFragment trunkRecord), Is.True);
            Assert.That(root.NpcBones.TryGet(handChild, out NpcBoneFragment handRecord), Is.True);
            Assert.That(trunkRecord.Piece.IsJointKept(2) && handRecord.Piece.IsJointKept(2), Is.True, "the shared bone's joint is kept by both");
            Assert.That(trunkRecord.Piece.Present, Is.EqualTo(new[] { false, false, true }), "the trunk's side: the shared bone (the trunk's own joint went with the waist)");
            Assert.That(handRecord.Piece.Present, Is.EqualTo(new[] { false, true, true }), "the hand's side: the hand's bone and the shared one");
            Assert.That(!trunkRecord.Piece.IsJointKept(0) && !handRecord.Piece.IsJointKept(0), Is.True, "the joint the source had lost is on neither");
            Assert.That(!trunkRecord.Piece.IsJointKept(1), Is.True, "the hand's joint is not the trunk's side's");
            AssertPartOf(trunkRecord.Piece, sourceRecord.Piece, 3, "the trunk's side");
            AssertPartOf(handRecord.Piece, sourceRecord.Piece, 3, "the hand's side");
            AssertClosedChild(root, trunkChild, "the trunk's child");
            AssertClosedChild(root, handChild, "the hand's child");

            // The trunk's child cut again by the ordinary cut (x = 0, through the shared bone's joint): its sides are the NPC's still.
            int decidedBefore = split.PairDecided;
            var pieces = new LogicalFragmentId[2];
            yield return CutInTwo(root, trunkChild, new float4(1f, 0f, 0f, 0f), pieces);
            yield return Until(() => root.Driver.Transactions.Count == 0 && root.Geometry.TryGetGeometry(pieces[0], out _) && root.Geometry.TryGetGeometry(pieces[1], out _), "the re-cut committed");
            Assert.That(root.NpcBones.TryGet(pieces[0], out NpcBoneFragment p0), Is.True, "a side of the re-cut is the NPC's");
            Assert.That(root.NpcBones.TryGet(pieces[1], out NpcBoneFragment p1), Is.True);
            AssertPartOf(p0.Piece, trunkRecord.Piece, 3, "the re-cut's positive side");
            AssertPartOf(p1.Piece, trunkRecord.Piece, 3, "the re-cut's negative side");
            Assert.That(p0.Piece.IsJointKept(2) && p1.Piece.IsJointKept(2), Is.True, "the shared bone's joint lies on the plane: on both sides");
            Assert.That(!p0.Piece.IsJointKept(0) && !p1.Piece.IsJointKept(0) && !p0.Piece.IsJointKept(1) && !p1.Piece.IsJointKept(1), Is.True, "nothing taken off came back");
            Assert.That(p0.Lineage.Chunks.Length, Is.EqualTo(2), "the waist's and the re-cut's new vertices, with their origins");

            // An owner of one convex is looked at only where the convex cut is on: turned on here with a bound no side of these reaches,
            // so that the pieces are decided again and nothing more is published.
            split.Settings = SplitCutOn(100000);
            Assert.That(split.Examine(pieces[0]) == SpatialSplitPass.OneComponent && split.Examine(pieces[1]) == SpatialSplitPass.OneComponent, Is.True,
                "under the defaults each is passed over before an attempt: one part of the mesh (" + split.Examine(pieces[0]) + ", " + split.Examine(pieces[1]) + ")");
            CutSpatialSplit.SkipOneComponent = false;   // (put back by the fixture after the case) so that the combination itself decides them again
            split.Settings = SplitCutOn(100001);   // another search: what was noted under the pass-over is void
            Assert.That(split.Examine(pieces[0]) == SpatialSplitPass.Candidate && split.Examine(pieces[1]) == SpatialSplitPass.Candidate, Is.True,
                "the re-cut's pieces are looked at (" + split.Examine(pieces[0]) + ", " + split.Examine(pieces[1]) + ")");
            int publishedBefore = split.Published;
            remaining[0] = 1.0;
            yield return Until(() => !split.HasAttempt && split.PairDecided >= decidedBefore + 2 && split.Examine(pieces[0]) != SpatialSplitPass.Candidate && split.Examine(pieces[1]) != SpatialSplitPass.Candidate,
                "the re-cut's pieces were decided again");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(split.DescribePair());
            Assert.That(split.PairDecided, Is.GreaterThanOrEqualTo(decidedBefore + 2), "each by the combination");
            Assert.That(split.Published, Is.EqualTo(publishedBefore), "one convex and one part of the mesh each: nothing more to split");
            Assert.That(root.NpcBones.Knows(pieces[0]) && root.NpcBones.Knows(pieces[1]), Is.True, "and they are the NPC's still");
            Assert.That(split.PairExceptions == 0 && split.PairNotRun == 0 && split.PairInvalid == 0, Is.True, "nothing thrown, nothing invalid: " + split.DescribePair());
            Assert.That(split.PairBonesNotWorkedOut, Is.Zero, split.LastPairBonesFailure);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Where the combination places nothing, the method as before goes on, with its own following.** The wrapped NPC's lower side:
        /// one convex round the trunk and the detached hand, so the combination has nothing to put on the other side. The attempt falls
        /// back, the mesh-led plane cuts the convex and publishes -- counted as the combination's fallback, not as its own placement --
        /// and the records follow that plane, each side a part of the source's piece.
        /// </summary>
        [UnityTest]
        public IEnumerator PairLive_WhereTheCombinationPlacesNothing_TheMethodAsBeforeGoesOn_WithItsOwnFollowing()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitCutClosed(root, remaining, 12);
            split.SetPairProtect(true);
            Assert.That(split.PairEffective && split.BoneFallback, Is.True, split.PairRefusal);
            LogicalFragmentId body = AddNpcWrappedBody(root);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, 1);
            LogicalFragmentId lower = sides[1];
            Assert.That(root.NpcBones.TryGet(lower, out NpcBoneFragment sourceRecord), Is.True);

            remaining[0] = 1.0;
            yield return Until(() => split.Published >= 1 || split.EndedAs(SpatialSplitEnd.NotBuildable) + split.EndedAs(SpatialSplitEnd.NotClassified) > 0
                                     || (split.PairDecided >= 2 && split.IsIdle && !split.HasAttempt),
                "the lower side was decided and ended");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(split.DescribePair());
            Assert.That(split.PairFellBack, Is.GreaterThanOrEqualTo(1), "the combination placed nothing, then the method as before");
            Assert.That(split.PairSeparable == 0 && split.PublishedPair == 0, Is.True, "nothing published as the combination's own");
            Assert.That(split.PublishedPairFallback == 1 && split.PublishedFallbackCut == 1 && split.Published == 1, Is.True, "published by the fallback's convex cut; last refusal: " + split.LastRefusal);
            Assert.That(split.PairFallbackEndedAs(SpatialSplitEnd.Published), Is.EqualTo(1), "counted once, as a fallback that published");
            Assert.That(split.PairEndedAs(SpatialSplitEnd.Published), Is.Zero);
            Assert.That(split.LastSource, Is.EqualTo(lower));
            Assert.That(root.NpcBones.SplitsByPlane == 1 && root.NpcBones.SplitsByNeededBones == 0, Is.True, "its records followed by the fallback's own plane");
            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(root.NpcBones.TryGet(a, out NpcBoneFragment ra), Is.True);
            Assert.That(root.NpcBones.TryGet(b, out NpcBoneFragment rb), Is.True);
            AssertPartOf(ra.Piece, sourceRecord.Piece, 4, "the positive side");
            AssertPartOf(rb.Piece, sourceRecord.Piece, 4, "the negative side");
            Assert.That(split.PairExceptions, Is.Zero);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A result of an input that changed is not published, everything it took is given back, and the switch is not thrown while an
        /// attempt is out.** The combination's attempt for the lower side is held back on the worker; the lower side is cut again
        /// meanwhile. The result ends Stale: counted as the combination's stale end, nothing published, nothing marked, nothing held.
        /// The re-cut's pieces are then decided by the combination, each split into its own two; and the world ends in order.
        /// </summary>
        [UnityTest]
        public IEnumerator PairLive_AStaleResultIsNotPublished_EverythingIsGivenBack_AndTheWorldEndsInOrder()
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
            split.SetPairProtect(true);
            LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 0f), true);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            LogicalFragmentId lower = sides[1];

            background.HoldEverything = true;
            remaining[0] = 1.0;
            yield return Until(() => split.HasAttempt && split.AttemptFragment == lower, "the lower side's attempt was issued");
            remaining[0] = 0.0;
            Assert.That(split.PairIssued, Is.EqualTo(1));
            Assert.That(split.AttemptsHeldBytes, Is.GreaterThan(0), "the attempt holds its blocks (the marks' room among them)");
            Assert.Throws<InvalidOperationException>(() => split.SetPairProtect(false), "the switch is not thrown with an attempt out");
            yield return Until(() => background.HoldingCount > 0, "its decision finished and is held back");

            var pieces = new LogicalFragmentId[2];
            yield return CutInTwo(root, lower, new float4(0f, 0f, 1f, 0f), pieces);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the re-cut's record is over");
            background.HoldEverything = false;
            background.ReleaseEverything();
            yield return Until(() => !split.HasAttempt, "the result arrived and was given back");
            Assert.That(split.EndedAs(SpatialSplitEnd.Stale), Is.EqualTo(1), "stale: the lower side is no longer what it was decided for");
            Assert.That(split.PairStale == 1 && split.PairEndedAs(SpatialSplitEnd.Stale) + split.PairFallbackEndedAs(SpatialSplitEnd.Stale) == 1, Is.True, "counted once, as the combination's stale end");
            Assert.That(split.PublishedPair + split.PublishedPairFallback + split.Published, Is.Zero, "nothing published");
            Assert.That(split.SuppressedNow, Is.Zero, "nothing recorded as failed");
            Assert.That(split.AttemptsHeldBytes, Is.Zero, "everything it held was given back");
            Assert.That(root.NpcBones.SplitsByNeededBones, Is.Zero, "and nothing was noted for a split that did not happen");

            int ownersBefore = root.Owners.Count;
            remaining[0] = 1.0;
            yield return Until(() => !split.HasAttempt && split.PublishedPair + split.PairEndedAs(SpatialSplitEnd.NotFound) + split.PairFallbackEndedAs(SpatialSplitEnd.NotFound) >= 2, "both pieces were decided");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine(split.DescribePair());
            Assert.That(split.PublishedPair, Is.EqualTo(2), "each piece split into its own two by the combination");
            Assert.That(root.Owners.Count, Is.EqualTo(ownersBefore + 2));
            Assert.That(split.PairBonesNotWorkedOut == 0 && split.PairExceptions == 0, Is.True, split.LastPairBonesFailure);
            foreach (LogicalFragmentId piece in pieces)
            {
                Assert.That(root.Ledger.TryGetSplitChildren(piece, out LogicalFragmentId first, out LogicalFragmentId second), Is.True, "replaced by its own split");
                AssertClosedChild(root, first, "a child of a re-cut piece");
                AssertClosedChild(root, second, "a child of a re-cut piece");
            }

            Assert.That(split.AttemptsHeldBytes, Is.Zero);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Where the graph engine cannot be prepared, the switch stays off with the reason and an NPC's piece is decided as before.**
        /// </summary>
        [UnityTest]
        public IEnumerator PairLive_WhereTheEngineCannotBePrepared_TheSwitchStaysOffWithTheReason()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            bool was = SpatialSplitGraph.IsPrepared;
            try
            {
                SpatialSplitGraph.ResetPreparedForTests();
                CutSpatialSplit.TestGraphPrepareFailAllocation = 0;
                split.SetPairProtect(true);
                Assert.That(split.PairProtect && !split.PairEffective, Is.True, "asked for, and off");
                StringAssert.Contains("preparation", split.PairRefusal);
                StringAssert.Contains("asked for, off", split.DescribePair());
            }
            finally
            {
                CutSpatialSplit.TestGraphPrepareFailAllocation = -1;
                if (was) Assert.That(SpatialSplitGraph.Prepare(), Is.True, "the engine is prepared again for the cases after this one");
            }

            LogicalFragmentId body = AddNpcBodyWithAHand(root, new Vector3(0f, 0f, 0f), true, handLowX: 0.8f);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            remaining[0] = 1.0;
            yield return Until(() => !split.HasAttempt && split.PublishedBone >= 1, "the lower side was split by the bones, as before");
            remaining[0] = 0.0;
            Assert.That(split.PairIssued == 0 && split.PublishedPair == 0 && split.BoneSearches >= 1, Is.True, "decided as before: " + split.DescribePair());
            split.Settings = default;
            yield return EndWorld(root);
        }
    }
}
