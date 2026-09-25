using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.ConvexCut.Tests;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    // Synthetic boxes only. Builder/collider ownership tests do not claim scene publication or solver coverage.
    public unsafe class VpPreparedPhysicsInputTests
    {
        readonly List<IDisposable> owned = new List<IDisposable>();
        T Keep<T>(T item) where T : IDisposable { owned.Add(item); return item; }
        [TearDown] public void Cleanup()
        {
            for (int i = owned.Count - 1; i >= 0; i--) owned[i].Dispose();
            owned.Clear();
        }
        OwnerCutHarness Bank(int count = 1)
        {
            var h = Keep(new OwnerCutHarness());
            h.planeN = new float3(0, 1, 0); h.eps = 1e-5f; h.parentMass = 12;
            for (int i = 0; i < count; i++) h.Add(CaseGenerator.Box());
            h.Build(); return h;
        }
        VpPreparedPhysicsInput Input(OwnerCutHarness h)
        {
            var ranges = new ConvexBrepRange[h.input.convexCount];
            for (int i = 0; i < ranges.Length; i++) ranges[i] = h.input.convexes[i];
            return Keep(new VpPreparedPhysicsInput(h.input.bank, ranges));
        }
        static float4x4 Pose(float x = 2) => new float4x4(quaternion.RotateY(0.37f), new float3(x, 0, -3));

        [TestCase(-1)][TestCase(0)][TestCase(205)]
        public void D5_ColdPreparation_PreservesInputAndHolds_ReleasesObjects_ThenPoses(int fill)
        {
            int saved = PhysicsCutBlocks.Fill; PhysicsCutBlocks.Fill = fill;
            try
            {
                var input=Input(Bank(2));var shape=input.ColdPreparationShape();
                var meshes=new[]{shape.MeshOf(0),shape.MeshOf(1)};
                var points=meshes.Select(m=>m.vertices).ToArray();
                shape.TryLocalBounds(out var lo,out var hi);
                var before=Resources.FindObjectsOfTypeAll<GameObject>().Select(g=>g.GetEntityId()).ToArray();
                var warm=new VpPhysicsColdPreparation(); warm.Prepare(input);
                Assert.That(warm.IsPrepared,Is.True); Assert.That(input.IsPosed,Is.False);
                Assert.That(input.ColdCookCalls,Is.EqualTo(2));
                Assert.That(shape.BankUsers+shape.WorkUsers,Is.Zero);Assert.That(shape.IsFreed||shape.MeshHoldsReleased,Is.False);
                shape.TryLocalBounds(out var afterLo,out var afterHi);Assert.That(afterLo,Is.EqualTo(lo));Assert.That(afterHi,Is.EqualTo(hi));
                for(int c=0;c<2;c++)
                {
                    CollectionAssert.AreEqual(points[c],meshes[c].vertices);
                    var range=shape.Convex(c);
                    for(int v=0;v<range.vertexCount;v++) Assert.That((Vector3)shape.BankOf(c).vertices[range.vertexBase+v],Is.EqualTo(points[c][v]));
                }
                warm.Prepare(null); // Completed preparer does not inspect or retain another per-NPC input.
                CollectionAssert.AreEquivalent(before,Resources.FindObjectsOfTypeAll<GameObject>().Select(g=>g.GetEntityId()).ToArray());
                Assert.That(input.TryPose(new[]{Pose(),Pose(5)},out var posed),Is.True);
                using(var pair=Pair(posed)) Assert.That(pair.Positive.Colliders.Count,Is.EqualTo(2));
            }
            finally {PhysicsCutBlocks.Fill=saved;}
        }

        [TestCase("posed")][TestCase("transferred")][TestCase("failed-pose")][TestCase("disposed")]
        public void D5_ColdPreparation_RefusesNonColdInputWithoutMarkingDone(string state)
        {
            var input=Input(Bank());
            if(state=="posed"||state=="transferred") Posed(input,Pose());
            if(state=="transferred") Keep(input.TakeShape());
            if(state=="failed-pose") Assert.That(input.TryPose(null,out _),Is.False);
            if(state=="disposed") input.Dispose();
            var warm=new VpPhysicsColdPreparation();
            if(state=="disposed") Assert.Throws<ObjectDisposedException>(()=>warm.Prepare(input));
            else Assert.Throws<InvalidOperationException>(()=>warm.Prepare(input));
            Assert.That(warm.IsPrepared,Is.False);
            warm.Prepare(Input(Bank()));Assert.That(warm.IsPrepared,Is.True);
        }

        [Test] public void D5_ColdPreparation_NullRejected_RepresentativeNotRetained()
        {
            var warm=new VpPhysicsColdPreparation();Assert.Throws<ArgumentNullException>(()=>warm.Prepare(null));
            var input=Input(Bank());warm.Prepare(input);input.Dispose();
            warm.Prepare(input);Assert.That(warm.IsPrepared,Is.True);
        }
        PhysicsOwnerShape Posed(VpPreparedPhysicsInput input, params float4x4[] frames)
        {
            Assert.That(input.TryPose(frames, out var shape), Is.True);
            return shape;
        }
        ProvisionalOwnerCandidate Pair(PhysicsOwnerShape shape)
        {
            var sides = new ConvexSide[shape.ConvexCount];
            for (int i = 0; i < sides.Length; i++) sides[i] = ConvexSide.Split;
            var input = new ProvisionalOwnerBuildInput {
                sourceShape = shape, sides = sides, planeLocal = new float4(0, 1, 0, 0),
                placement = PhysicsOwnerPlacement.Identity,
                anchors = new AnchorDistributionResult(AnchorDistributionStatus.Ok, 0, 0),
                parentMass = 12, sourceInertia = new float3(4), sourceInertiaRotation = quaternion.identity,
                cooking = PhysicsCutCook.DefaultCooking, name = "Prepared input test" };
            Assert.That(ProvisionalOwnerBuilder.TryBuild(in input, out var pair, out var outcome), Is.True, outcome.ToString());
            return Keep(pair);
        }
        // Controlled borrowed/produced records, NOT a numerical cut. Produced range is unused by collider preparation.
        PhysicsCutProducts Products(PhysicsOwnerShape shape, bool produced = false)
        {
            var result = new ConvexCutOwnerResult { status = ConvexCutOwnerStatus.Ok,
                positiveMass = 6, negativeMass = 6,
                positiveInertia = new SymmetricMatrix3 { xx = 1, yy = 1, zz = 1 },
                negativeInertia = new SymmetricMatrix3 { xx = 1, yy = 1, zz = 1 } };
            var cap = new ConvexCutOwnerCapacity { vertices = 1, faceOffsets = 1, faceIndices = 1, edges = 1, scratchBytes = 1 };
            var products = Keep(new PhysicsCutProducts(new PhysicsCutArena(in cap, 1),
                new ConvexCutOutcome[shape.ConvexCount], in result, float4x4.identity, PhysicsCutCook.DefaultCooking, 1, 1));
            foreach (bool positive in new[] { true, false })
            {
                Mesh mesh = produced ? UnityEngine.Object.Instantiate(shape.MeshOf(0)) : null;
                if (mesh != null) Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
                products.Add(positive, new PhysicsCutPart(0, !produced, shape.Convex(0), mesh, default));
            }
            return products;
        }
        static void Near(float3 a, float3 b) => Assert.That(math.distance(a, b), Is.LessThan(1e-5f));
        static void ColliderMatches(MeshCollider collider, PhysicsOwnerShape shape, int c)
        {
            Assert.That(collider.sharedMesh, Is.SameAs(shape.MeshOf(c)));
            var vertices = collider.sharedMesh.vertices; var r = shape.Convex(c);
            for (int v = 0; v < vertices.Length; v++)
                Near(collider.transform.TransformPoint(vertices[v]), shape.BankOf(c).vertices[r.vertexBase + v]);
        }
        static int PreparedMeshCount()
        {
            int count = 0;
            foreach (var m in Resources.FindObjectsOfTypeAll<Mesh>()) if (m.name == "Prepared bone-local convex") count++;
            return count;
        }

        [Test] public void ColdBuild_CooksOncePerConvex_NoActors_InstancesDoNotShareMeshes()
        {
            var h = Bank(2); int actors = Resources.FindObjectsOfTypeAll<Rigidbody>().Length;
            int colliders = Resources.FindObjectsOfTypeAll<MeshCollider>().Length;
            var a = Input(h); var b = Input(h);
            Assert.That(a.ColdCookCalls, Is.EqualTo(2)); Assert.That(b.ColdCookCalls, Is.EqualTo(2));
            Assert.That(Resources.FindObjectsOfTypeAll<Rigidbody>().Length, Is.EqualTo(actors));
            Assert.That(Resources.FindObjectsOfTypeAll<MeshCollider>().Length, Is.EqualTo(colliders));
            var sa = Posed(a, Pose(), Pose(5)); var sb = Posed(b, Pose(8), Pose(9));
            Assert.That(sa.MeshOf(0), Is.Not.SameAs(sb.MeshOf(0)));
            Near(sa.MeshFrameOf(0).Position, new float3(2, 0, -3));
            Near(sb.MeshFrameOf(0).Position, new float3(8, 0, -3));
        }
        [Test] public void Pose_WritesIndependentBrepAndFusedBounds_LeavesCookedMeshUnchanged()
        {
            var h = Bank(2); var input = Input(h); var transforms = new[] { Pose(), Pose(6) };
            var shape = Posed(input, transforms); float3 lo = new float3(float.PositiveInfinity), hi = -lo;
            for (int c = 0; c < 2; c++)
            {
                var src = h.input.convexes[c]; var dst = shape.Convex(c);
                var vertices = shape.MeshOf(c).vertices; float3 clo = new float3(float.PositiveInfinity), chi = -clo;
                for (int v = 0; v < dst.vertexCount; v++)
                {
                    float3 bind = h.input.bank.vertices[src.vertexBase + v];
                    Near(vertices[v], bind);
                    float3 expected = math.transform(transforms[c], bind);
                    Near(shape.BankOf(c).vertices[dst.vertexBase + v], expected);
                    Near(math.transform(shape.MeshFrameOf(c).Matrix, bind), expected);
                    clo = math.min(clo, expected); chi = math.max(chi, expected);
                }
                shape.ConvexBounds(c, out var actualLo, out var actualHi); Near(actualLo, clo); Near(actualHi, chi);
                lo = math.min(lo, clo); hi = math.max(hi, chi);
            }
            Assert.That(shape.TryLocalBounds(out var sl, out var sh), Is.True); Near(sl, lo); Near(sh, hi);
            Assert.That(input.ColdCookCalls, Is.EqualTo(2));
            Assert.That(input.TryPose(transforms, out var second), Is.False); Assert.That(second, Is.Null);
        }
        [TestCase("scale")][TestCase("reflection")][TestCase("shear")][TestCase("nan")][TestCase("count")]
        public void InvalidPose_IsOneShot_ExposesNothing_AndCanBeDisposed(string kind)
        {
            var input = Input(Bank(2)); var bad = float4x4.identity;
            if (kind == "scale") bad.c0.x = 0.01f;
            if (kind == "reflection") bad.c0.x = -1;
            if (kind == "shear") bad.c1.x = 0.1f;
            if (kind == "nan") bad.c3.x = float.NaN;
            var frames = kind == "count" ? new[] { Pose() } : new[] { Pose(), bad };
            Assert.That(input.TryPose(frames, out var shape), Is.False); Assert.That(shape, Is.Null);
            Assert.That(input.IsPosed, Is.False);
            Assert.Throws<InvalidOperationException>(() => input.TakeShape());
            Assert.That(input.TryPose(new[] { Pose(), Pose() }, out _), Is.False);
        }
        [Test] public void ColdFailureAfterFirstMesh_ReleasesPartialMeshes()
        {
            var h = Bank(2); int before = PreparedMeshCount();
            h.input.bank.vertices[h.input.convexes[1].vertexBase] = new float3(float.NaN);
            Assert.Throws<ArgumentException>(() => Input(h));
            Assert.That(PreparedMeshCount(), Is.EqualTo(before));
        }
        [Test] public void DisposeBeforePose_ReleasesAllMeshes()
        {
            var h = Bank(2); int before = PreparedMeshCount(); var input = Input(h);
            Assert.That(PreparedMeshCount(), Is.EqualTo(before + 2)); input.Dispose();
            Assert.That(PreparedMeshCount(), Is.EqualTo(before));
            Assert.Throws<ObjectDisposedException>(() => input.TryPose(new[] { Pose(), Pose() }, out _));
        }
        [Test] public void UntransferredPosedShape_DisposalReclaimsBankAndMeshes()
        {
            var input = Input(Bank()); var shape = Posed(input, Pose()); var mesh = shape.MeshOf(0);
            input.Dispose(); Assert.That(shape.IsFreed, Is.True); Assert.That(mesh == null, Is.True);
        }
        [Test] public void Transfer_IsExplicitAndOnce_CallerCleansUpOnRegistrationRefusal()
        {
            var input = Input(Bank()); var borrowed = Posed(input, Pose()); var shape = Keep(input.TakeShape());
            Assert.That(shape, Is.SameAs(borrowed)); Assert.Throws<InvalidOperationException>(() => input.TakeShape());
            var mesh = shape.MeshOf(0); input.Dispose(); Assert.That(mesh != null, Is.True);
            shape.Dispose(); Assert.That(mesh == null, Is.True);
        }
        [Test] public void WorkHold_DefersBankAndMeshRelease()
        {
            var input = Input(Bank()); var shape = Posed(input, Pose()); var mesh = shape.MeshOf(0);
            shape.AcquireForWork(); input.Dispose();
            Assert.That(shape.IsFreed, Is.False); Assert.That(mesh != null, Is.True);
            shape.ReleaseFromWork(); Assert.That(shape.IsFreed, Is.True); Assert.That(mesh == null, Is.True);
        }
        [Test] public void InheritedFinalAndRecutView_PreserveFramesAndMeshLifetime()
        {
            var input = Input(Bank()); var shape = Posed(input, Pose()); var mesh = shape.MeshOf(0);
            var products = Products(shape); var final = Keep(PhysicsOwnerShape.OfSide(shape, products, PhysicsShapeSource.For(products), true));
            var recut = Keep(PhysicsOwnerShape.ProvisionalSide(final, new[] { 0 }));
            Assert.That(final.MeshFrameOf(0).Equals(shape.MeshFrameOf(0)), Is.True);
            Assert.That(recut.MeshFrameOf(0).Equals(shape.MeshFrameOf(0)), Is.True);
            input.Dispose(); final.Dispose(); Assert.That(mesh != null, Is.True);
            Near(recut.BankOf(0).vertices[recut.Convex(0).vertexBase], math.transform(Pose(), (float3)mesh.vertices[0]));
            recut.Dispose(); Assert.That(mesh == null, Is.True);
        }
        [Test] public void Provisional_ChildColliderMatchesPosedBrep_CookedMeshIsReused()
        {
            var input = Input(Bank(2)); var shape = Posed(input, Pose(), Pose(4)); var pair = Pair(shape);
            foreach (var side in new[] { pair.Positive, pair.Negative })
            {
                Assert.That(side.Root.activeSelf, Is.False); Assert.That(side.ShapeFrame.transform.childCount, Is.EqualTo(2));
                for (int c = 0; c < 2; c++) ColliderMatches(side.Colliders[c], shape, c);
            }
            Assert.That(input.ColdCookCalls, Is.EqualTo(2));
        }
        [Test] public void FinalBorrowedCollider_IsReused_WhenFrameMatches()
        {
            var shape = Posed(Input(Bank()), Pose()); var pair = Pair(shape); var products = Products(shape);
            var old = pair.Positive.Colliders[0];
            var prepared = PhysicsOwnerBuilder.PrepareFinalColliders(products, shape.Meshes, pair.Positive,
                pair.PositiveShape, quaternion.identity, float3.zero, shape);
            Assert.That(prepared.KeptCount, Is.EqualTo(1)); Assert.That(prepared.MadeCount, Is.Zero);
            prepared.Adopt(quaternion.identity, float3.zero);
            Assert.That(pair.Positive.Colliders[0], Is.SameAs(old)); ColliderMatches(old, shape, 0);
        }
        [TestCase(false)][TestCase(true)] public void FrameMismatch_Rebuilds_WithdrawOrAdoptCleansOwnedChild(bool adopt)
        {
            var shape = Posed(Input(Bank()), Pose()); var pair = Pair(shape); var products = Products(shape);
            var old = pair.Positive.Colliders[0]; var oldChild = old.gameObject;
            // Same Mesh, different descriptor: the old unframed caller cannot reuse a framed child.
            var prepared = PhysicsOwnerBuilder.PrepareFinalColliders(products, shape.Meshes, pair.Positive,
                pair.PositiveShape, quaternion.identity, float3.zero);
            Assert.That(prepared.KeptCount, Is.Zero); Assert.That(prepared.MadeCount, Is.EqualTo(1));
            if (adopt)
            {
                prepared.Adopt(quaternion.identity, float3.zero);
                Assert.That(oldChild == null, Is.True); Assert.That(pair.Positive.ShapeFrame.transform.childCount, Is.Zero);
                Assert.That(pair.Positive.Colliders[0].gameObject, Is.SameAs(pair.Positive.ShapeFrame));
            }
            else
            {
                prepared.Withdraw(); Assert.That(oldChild != null, Is.True);
                Assert.That(pair.Positive.ShapeFrame.GetComponents<MeshCollider>().Length, Is.Zero);
            }
        }
        [Test] public void WithdrawNewFramedCollider_RemovesItsChild_NotOriginalChild()
        {
            var shape = Posed(Input(Bank()), Pose()); var pair = Pair(shape); var products = Products(shape);
            var side = pair.Positive; var old = side.Colliders[0];
            var prepared = PhysicsOwnerBuilder.PrepareFinalColliders(products, shape.Meshes, side,
                pair.PositiveShape, quaternion.identity, new float3(1, 0, 0), shape);
            Assert.That(prepared.MadeCount, Is.EqualTo(1)); Assert.That(side.ShapeFrame.transform.childCount, Is.EqualTo(2));
            prepared.Withdraw(); Assert.That(side.ShapeFrame.transform.childCount, Is.EqualTo(1));
            Assert.That(side.Colliders[0], Is.SameAs(old));
        }
        [Test] public void ProducedFinalCollider_UsesNumericalFrame_AndReplacesFramedChild()
        {
            var shape = Posed(Input(Bank()), Pose()); var pair = Pair(shape); var products = Products(shape, true);
            var oldChild = pair.Positive.Colliders[0].gameObject;
            var prepared = PhysicsOwnerBuilder.PrepareFinalColliders(products, shape.Meshes, pair.Positive,
                pair.PositiveShape, quaternion.identity, float3.zero, shape);
            Assert.That(prepared.KeptCount, Is.Zero); prepared.Adopt(quaternion.identity, float3.zero);
            Assert.That(oldChild == null, Is.True);
            Assert.That(pair.Positive.Colliders[0].gameObject, Is.SameAs(pair.Positive.ShapeFrame));
            Assert.That(pair.Positive.Colliders[0].sharedMesh, Is.SameAs(products.Part(true, 0).mesh));
        }
        [Test] public void DirectFinalBuilder_UsesInheritedShapeFrames()
        {
            var shape = Posed(Input(Bank()), Pose()); var products = Products(shape);
            var input = new PhysicsOwnerBuildInput { products = products, inheritedShape = shape,
                placement = PhysicsOwnerPlacement.Identity, parentMass = 12,
                anchors = new AnchorDistributionResult(AnchorDistributionStatus.Ok, 0, 0), name = "Prepared final test" };
            Assert.That(PhysicsOwnerBuilder.TryBuild(in input, out var result, out var outcome), Is.True, outcome.ToString());
            Keep(result); ColliderMatches(result.Positive.Colliders[0], shape, 0); ColliderMatches(result.Negative.Colliders[0], shape, 0);
        }
    }
}
