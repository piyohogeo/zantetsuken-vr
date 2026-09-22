using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// At the Final handoff of a compound body, the colliders of the convexes a side inherits uncut are the very
    /// colliders its Provisional actor already had -- same mesh, same profile, same frame -- and they keep
    /// answering through the switch; the convexes the cut produced get new colliders; what the side no longer needs
    /// is disabled at the switch and destroyed once the frame is over; and the actor carries exactly its final set.
    /// </summary>
    public class FinalColliderReusePlayModeTests
    {
        private const string ScenePath = "Assets/Scenes/CutWorldSandbox.unity";

        private readonly List<HoldingExecutor> _holding = new List<HoldingExecutor>();

        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            foreach (HoldingExecutor held in _holding)
            {
                held.HoldEverything = false;
            }

            _holding.Clear();
            CutWorldRoot.nextWorldExecutors = null;
            yield return null;
        }

        /// <summary>
        /// The world's executors, each wrapped so that finished work is kept back until released: the Provisional
        /// pair then stands for as long as the test needs to read it, and the handoff follows the release. The
        /// same device the sandbox scene tests use; nothing of the product waits.
        /// </summary>
        private sealed class HoldingExecutor : IWorkExecutor
        {
            private readonly IWorkExecutor _inner;
            private readonly List<(IDispatchWork work, WorkCompletion completion)> _held = new List<(IDispatchWork, WorkCompletion)>();

            internal HoldingExecutor(IWorkExecutor inner)
            {
                _inner = inner;
            }

            internal bool HoldEverything { get; set; } = true;

            public WorkDestination Destination => _inner.Destination;

            public int Capacity => _inner.Capacity;

            public int Held => _inner.Held;

            public bool CanAccept => _inner.CanAccept;

            public bool TryAccept(IDispatchWork work)
            {
                return _inner.TryAccept(work);
            }

            public void BeginAccepted(IDispatchWork work)
            {
                _inner.BeginAccepted(work);
            }

            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
            {
                if (!HoldEverything && _held.Count > 0)
                {
                    // Given back as the inner executor returned it: only the moment of collection is changed here,
                    // never what the work ended as (finished, failed or cancelled).
                    (work, completion) = _held[0];
                    _held.RemoveAt(0);
                    return true;
                }

                if (!_inner.TryTakeFinished(out work, out completion))
                {
                    return false;
                }

                if (!HoldEverything)
                {
                    return true;
                }

                _held.Add((work, completion));
                work = null;
                completion = default;
                return false;
            }

            public void CloseForNewWork()
            {
                _inner.CloseForNewWork();
            }

            public bool StopAndConfirm(int timeoutMilliseconds)
            {
                return _inner.StopAndConfirm(timeoutMilliseconds) && _held.Count == 0;
            }
        }

        [UnityTest]
        public IEnumerator InheritedConvexes_KeepTheirProvisionalColliders_ThroughTheHandoff()
        {
            CutWorldRoot.nextWorldExecutors = destination =>
            {
                IWorkExecutor inner = destination == WorkDestination.UnityJob
                    ? new UnityJobWorkExecutor(4)
                    : destination == WorkDestination.GeometryPool
                        ? WorkerPoolExecutor.GeometryPool(1)
                        : WorkerPoolExecutor.BackgroundPool(1);
                var held = new HoldingExecutor(inner);
                _holding.Add(held);
                return held;
            };
            SceneManager.LoadScene(ScenePath, LoadSceneMode.Single);
            yield return null;
            var world = Object.FindFirstObjectByType<CutWorldRoot>();
            Assert.That(world, Is.Not.Null);
            Assert.That(world.IsReady, Is.True);
            yield return null;

            // Three boxes: one across the plane (cut), two above it (inherited whole by the positive side).
            SandboxCompoundBody made = SandboxCompoundBody.TryBuild(world.Storage, 3, 1, new float3(0.25f, 0.25f, 0.25f), 0);
            Assert.That(made, Is.Not.Null);
            var inheritedMeshes = new List<Mesh> { made.Shape.MeshOf(1), made.Shape.MeshOf(2) };
            Mesh cutMesh = made.Shape.MeshOf(0);
            var actor = new GameObject("Reuse body");
            Rigidbody rigid = actor.AddComponent<Rigidbody>();
            rigid.useGravity = false;
            rigid.isKinematic = true;
            rigid.automaticCenterOfMass = false;
            rigid.automaticInertiaTensor = false;
            rigid.mass = 4f;
            rigid.centerOfMass = Vector3.zero;
            rigid.inertiaTensor = new Vector3(4f, 4f, 4f);
            MeshCollider collider = actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = made.FirstColliderMesh;
            Assert.That(
                world.TryAddBody(actor, made.Shape, made.Geometry, Matrix4x4.identity, Matrix4x4.identity, null, out LogicalFragmentId fragment),
                Is.True);
            made.Taken();
            yield return null;

            Assert.That(
                world.TryAsk(new ProvisionalCutAsk { source = fragment, plane = new float4(0f, 1f, 0f, 0f), renderAnchor = actor.transform.position }),
                Is.True);

            // With every worker result held back, the pair is published and then stands: the handoff needs the
            // cook's result, which cannot be collected until the hold is released below.
            ProvisionalCutTransaction transaction = null;
            float published = Time.realtimeSinceStartup + 30f;
            while (transaction == null || transaction.Phase != ProvisionalCutPhase.Published)
            {
                Assert.That(Time.realtimeSinceStartup, Is.LessThan(published), "the pair is published within the deadline");
                yield return null;
                transaction = null;
                foreach (ProvisionalCutTransaction candidate in world.Driver.Transactions)
                {
                    transaction = candidate;
                }
            }

            Assert.That(transaction.Phase, Is.EqualTo(ProvisionalCutPhase.Published), "the pair stands while the workers' results are held");
            PhysicsOwnerSide positive = transaction.Pair.Positive;
            PhysicsOwnerSide negative = transaction.Pair.Negative;
            var positiveBefore = new List<MeshCollider>(positive.Colliders);
            var negativeBefore = new List<MeshCollider>(negative.Colliders);
            Assert.That(positiveBefore.Count, Is.EqualTo(3), "the positive side stands on the cut convex and the two inherited ones");
            Assert.That(negativeBefore.Count, Is.EqualTo(1), "the negative side on the cut convex only");
            MeshCollider keptA = positiveBefore.Find(c => c.sharedMesh == inheritedMeshes[0]);
            MeshCollider keptB = positiveBefore.Find(c => c.sharedMesh == inheritedMeshes[1]);
            MeshCollider positiveCutOne = positiveBefore.Find(c => c.sharedMesh == cutMesh);
            Assert.That(keptA, Is.Not.Null);
            Assert.That(keptB, Is.Not.Null);
            Assert.That(positiveCutOne, Is.Not.Null);
            Vector3 frameAt = positive.ShapeFrame.transform.localPosition;
            Quaternion frameRotation = positive.ShapeFrame.transform.localRotation;

            // Only now may the results come back: the handoff follows.
            foreach (HoldingExecutor held in _holding)
            {
                held.HoldEverything = false;
            }

            float deadline = Time.realtimeSinceStartup + 30f;
            while (transaction.Phase != ProvisionalCutPhase.HandedOff && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(transaction.Phase, Is.EqualTo(ProvisionalCutPhase.HandedOff), "the handoff happened");

            // Kept: the two inherited convexes' colliders are the same instances, enabled, on the same frame pose.
            Assert.That(positive.Colliders, Has.Member(keptA), "the first inherited convex keeps its collider");
            Assert.That(positive.Colliders, Has.Member(keptB), "and so does the second");
            Assert.That(keptA.enabled && keptB.enabled, Is.True, "still answering");
            Assert.That(positive.ShapeFrame.transform.localPosition, Is.EqualTo(frameAt), "the frame did not move");
            Assert.That(positive.ShapeFrame.transform.localRotation, Is.EqualTo(frameRotation));

            // Replaced: the cut convex's Provisional collider is not among the final ones and is disabled at once.
            Assert.That(positive.Colliders, Has.No.Member(positiveCutOne), "the cut convex's collider is replaced");
            Assert.That(positiveCutOne == null || !positiveCutOne.enabled, Is.True, "and no longer answers");
            Assert.That(negative.Colliders, Has.No.Member(negativeBefore[0]), "the negative side's only collider is replaced");
            Assert.That(negativeBefore[0] == null || !negativeBefore[0].enabled, Is.True);

            // Final: one collider per part, every one enabled with a mesh, the produced ones new.
            Assert.That(positive.Colliders.Count, Is.EqualTo(3), "cut half + two inherited");
            Assert.That(negative.Colliders.Count, Is.EqualTo(1), "cut half");
            foreach (PhysicsOwnerSide side in new[] { positive, negative })
            {
                foreach (MeshCollider final in side.Colliders)
                {
                    Assert.That(final != null && final.enabled && final.sharedMesh != null, Is.True, "a final collider answers with a mesh");
                }
            }

            Assert.That(positive.ProducedColliderCount, Is.EqualTo(1), "one produced convex on the positive side");
            Assert.That(negative.ProducedColliderCount, Is.EqualTo(1));

            yield return null;
            Assert.That(positiveCutOne == null, Is.True, "the replaced collider was destroyed once the frame was over");
            Assert.That(negativeBefore[0] == null, Is.True);
            Assert.That(
                positive.ShapeFrame.GetComponents<MeshCollider>().Length, Is.EqualTo(positive.Colliders.Count),
                "the actor carries exactly its final set");
            Assert.That(negative.ShapeFrame.GetComponents<MeshCollider>().Length, Is.EqualTo(negative.Colliders.Count));

            world.Shutdown();
            float ending = Time.realtimeSinceStartup + 30f;
            while (!world.IsReleased && Time.realtimeSinceStartup < ending)
            {
                yield return null;
            }
        }
    }
}
