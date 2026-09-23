using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using NUnit.Framework.Interfaces;
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
    /// <summary>
    /// What the fixture's ending asks of a world, and nothing more: the ending request, whether everything has been
    /// given back, and whether nothing is still with a worker. The real world is <see cref="CutWorldRoot"/>; a
    /// synthetic test may stand in one that never releases, to drive the ending's waits by hand.
    /// </summary>
    internal interface IWorldEnding
    {
        bool IsReleased { get; }

        bool IsDrained();

        void Shutdown();
    }

    public class FinalColliderReusePlayModeTests
    {
        /// <summary>The scene's world, as the ending sees it.</summary>
        private sealed class WorldEndingOf : IWorldEnding
        {
            private readonly CutWorldRoot _root;

            internal WorldEndingOf(CutWorldRoot root)
            {
                _root = root;
            }

            public bool IsReleased => _root.IsReleased;

            public bool IsDrained()
            {
                return _root.IsDrained();
            }

            public void Shutdown()
            {
                _root.Shutdown();
            }
        }

        private const string ScenePath = "Assets/Scenes/CutWorldSandbox.unity";

        private const float EndingDeadlineSeconds = 30f;

        private readonly List<HoldingExecutor> _holding = new List<HoldingExecutor>();

        // What the test made, kept here so that the fixture can end the world and collect however the test ends.
        // The world is the scene's; the compound body and the actor are the test's until the world takes them.
        private CutWorldRoot _world;
        private IWorldEnding _endingWorld;
        private SandboxCompoundBody _made;
        private GameObject _actor;

        /// <summary>
        /// Whether <c>TryAddBody</c> succeeded and <c>Taken()</c> was called: from then on the actor and the shape are
        /// the world's, and the world's own ending destroys the actor. An actor that is still there after a confirmed
        /// ending is never treated as unregistered and destroyed by force.
        /// </summary>
        private bool _actorRegistered;

        /// <summary>The world's ending, as far as this fixture saw it: <c>IsReleased</c> within the deadline, or not.</summary>
        private enum WorldEnding
        {
            NotStarted = 0,
            Confirmed = 1,
            NotConfirmed = 2,
        }

        /// <summary>The fixture's own collection of the actor and the compound body, which is a separate thing.</summary>
        private enum Collection
        {
            NotStarted = 0,
            Done = 1,
            HeldBack = 2,
        }

        private WorldEnding _ending;
        private bool _endingEntered;
        private Collection _collection;
        private bool _collectionEntered;
        private bool _actorDestroyed;
        private int _madeDisposeCalls;
        private readonly List<string> _report = new List<string>();

        /// <summary>
        /// What a test could not collect, kept for the fixture's life and never touched again: the world, the body,
        /// the actor, whether it was registered, the executors, and why.
        /// </summary>
        private sealed class HeldBack
        {
            public HeldBack(
                string test, CutWorldRoot world, SandboxCompoundBody made, GameObject actor, bool actorRegistered,
                IReadOnlyList<HoldingExecutor> executors, IReadOnlyList<string> reasons)
            {
                Test = test;
                World = world;
                Made = made;
                Actor = actor;
                ActorRegistered = actorRegistered;
                Executors = executors;
                Reasons = reasons;
            }

            public string Test { get; }

            public CutWorldRoot World { get; }

            public SandboxCompoundBody Made { get; }

            public GameObject Actor { get; }

            public bool ActorRegistered { get; }

            public IReadOnlyList<HoldingExecutor> Executors { get; }

            public IReadOnlyList<string> Reasons { get; }
        }

        private readonly List<HeldBack> _heldBack = new List<HeldBack>();

        [SetUp]
        public void Fresh()
        {
            _world = null;
            _endingWorld = null;
            _made = null;
            _actor = null;
            _actorRegistered = false;
            _ending = WorldEnding.NotStarted;
            _endingEntered = false;
            _collection = Collection.NotStarted;
            _collectionEntered = false;
            _actorDestroyed = false;
            _madeDisposeCalls = 0;
            _report.Clear();
        }

        /// <summary>
        /// The ending of the world, asked for once and carried by the ordinary frames: the holds are let go, the
        /// ending is requested, and <c>IsReleased</c> is waited for until the deadline. It runs once; the test body
        /// calls it and asserts the outcome, and the teardown calls it in case the body did not get there. Nothing is
        /// collected here.
        /// </summary>
        private IEnumerator EndWorldOnce()
        {
            if (_endingEntered)
            {
                yield break;
            }

            _endingEntered = true;

            // From here every completion passes through. This says nothing about the holds being empty already:
            // what was held is handed back at the collections that follow, on the frames the wait below spends.
            foreach (HoldingExecutor held in _holding)
            {
                held.HoldEverything = false;
            }

            if (_endingWorld == null)
            {
                _ending = WorldEnding.NotConfirmed;
                _report.Add("ending: there was no world to end");
                yield break;
            }

            if (!TryRequestEnding())
            {
                _ending = WorldEnding.NotConfirmed;
                yield break;
            }

            try
            {
                float deadline = Time.realtimeSinceStartup + EndingDeadlineSeconds;
                while (!_endingWorld.IsReleased && Time.realtimeSinceStartup < deadline)
                {
                    yield return null;
                }

                if (_endingWorld.IsReleased)
                {
                    _ending = WorldEnding.Confirmed;
                }
                else
                {
                    _ending = WorldEnding.NotConfirmed;
                    _report.Add("ending: the world was not released within " + EndingDeadlineSeconds
                                + " s (IsReleased=false, IsDrained=" + _endingWorld.IsDrained() + ")");
                }
            }
            finally
            {
                // Left before it concluded -- whoever was driving this wait stopped -- and that is not a
                // confirmation of anything.
                if (_ending == WorldEnding.NotStarted)
                {
                    _ending = WorldEnding.NotConfirmed;
                    _report.Add("ending: the wait for the world's release was left before it concluded");
                }
            }
        }

        private bool TryRequestEnding()
        {
            try
            {
                _endingWorld.Shutdown();
                return true;
            }
            catch (Exception failure)
            {
                _report.Add("ending: Shutdown threw: " + failure);
                return false;
            }
        }

        /// <summary>
        /// The fixture's collection, once, and only after the world's ending was confirmed: an actor the world never
        /// took is destroyed first -- its collider points at the body's meshes -- then a frame passes for the destroys
        /// (the world's are deferred), the actor is confirmed gone whether it was registered or not, and only then is
        /// the compound body disposed: its meshes, which the world's shapes borrowed until the ending, and its native
        /// arrays. Anything that does not hold leaves everything held back, with the reasons; nothing is retried.
        /// </summary>
        private IEnumerator CollectOnce()
        {
            // Entered once, whatever it then concludes: a second call while the first is still at its wait -- or
            // after it was left there -- does not begin the collection again.
            if (_collectionEntered || _collection != Collection.NotStarted)
            {
                yield break;
            }

            _collectionEntered = true;
            if (_ending != WorldEnding.Confirmed)
            {
                HoldBack("collection: the world's ending was not confirmed, so nothing of the body was destroyed");
                yield break;
            }

            if (_actor != null && !_actorRegistered && !TryDestroyActor())
            {
                HoldBack("collection: destroying the unregistered actor threw, so the meshes it points at were kept");
                yield break;
            }

            // The frame the deferred destroys take effect in: the world's (a registered actor) and this one's.
            yield return null;

            if (_actor != null)
            {
                HoldBack("collection: the actor is still alive after the ending (registered=" + _actorRegistered
                         + "), so the meshes its colliders point at were kept");
                yield break;
            }

            _actorDestroyed = true;
            if (_made != null)
            {
                _madeDisposeCalls++;
                if (!TryDisposeMade())
                {
                    HoldBack("collection: made.Dispose() threw; the remaining references are kept as they are");
                    yield break;
                }

                _made = null;
            }

            _collection = Collection.Done;
        }

        private bool TryDestroyActor()
        {
            try
            {
                UnityEngine.Object.Destroy(_actor);
                return true;
            }
            catch (Exception failure)
            {
                _report.Add("collection: Destroy(actor) threw: " + failure);
                return false;
            }
        }

        private bool TryDisposeMade()
        {
            try
            {
                _made.Dispose();
                return true;
            }
            catch (Exception failure)
            {
                _report.Add("collection: made.Dispose() threw: " + failure);
                return false;
            }
        }

        // ----- for the synthetic guard tests only: the operations and readings they need, and no more ---------------

        /// <summary>Stands in a world for the ending to ask; nothing of the scene is involved.</summary>
        internal void UseWorldForTest(IWorldEnding world)
        {
            _endingWorld = world;
        }

        /// <summary>
        /// One held destination with no inner executor, so that the held-back record has a reference to keep. The
        /// ending only sets its <c>HoldEverything</c>; nothing here runs work.
        /// </summary>
        internal void AddHoldingForTest()
        {
            _holding.Add(new HoldingExecutor(null));
        }

        /// <summary>
        /// Puts the fixture where a collection may begin -- the ending confirmed, one actor the world never took --
        /// with no compound body at all: what is checked is the collection's guard, not the body.
        /// </summary>
        internal void BeginCollectionForTest(GameObject unregisteredActor)
        {
            _ending = WorldEnding.Confirmed;
            _actor = unregisteredActor;
            _actorRegistered = false;
        }

        internal IEnumerator EndWorldOnceForTest()
        {
            return EndWorldOnce();
        }

        internal IEnumerator CollectOnceForTest()
        {
            return CollectOnce();
        }

        internal bool EndingIsNotStarted => _ending == WorldEnding.NotStarted;

        internal bool EndingIsNotConfirmed => _ending == WorldEnding.NotConfirmed;

        internal bool CollectionWasEntered => _collectionEntered;

        internal bool CollectionIsNotStarted => _collection == Collection.NotStarted;

        internal bool CollectionIsHeldBack => _collection == Collection.HeldBack;

        internal int MadeDisposeCalls => _madeDisposeCalls;

        internal int HoldingCount => _holding.Count;

        internal int HeldBackCount => _heldBack.Count;

        internal int HeldBackExecutorCountOfLast => _heldBack.Count == 0 ? -1 : _heldBack[_heldBack.Count - 1].Executors.Count;

        internal IReadOnlyList<string> ReportForTest => _report;

        private void HoldBack(string reason)
        {
            _report.Add(reason);
            _collection = Collection.HeldBack;
            _heldBack.Add(new HeldBack(
                TestContext.CurrentContext.Test.FullName, _world, _made, _actor, _actorRegistered,
                new List<HoldingExecutor>(_holding), new List<string>(_report)));
        }

        /// <summary>
        /// The teardown: the ending if the body did not get to it, then the collection, then what is restored no
        /// matter what, then the report -- which fails a test that had passed and is written beside the failure of
        /// one that had not.
        /// </summary>
        [UnityTearDown]
        public IEnumerator Cleanup()
        {
            try
            {
                yield return EndWorldOnce();
                yield return CollectOnce();
            }
            finally
            {
                // Left before the collection concluded -- a wait that was interrupted, or an ending never
                // confirmed -- and nothing held back yet: held back now, once, before the executors' references go.
                // This reads the states alone; whether the inner iterators' own finally blocks ran is not assumed.
                if (_ending == WorldEnding.NotStarted)
                {
                    _ending = WorldEnding.NotConfirmed;
                    _report.Add("cleanup: the ending had not concluded when the teardown was left");
                }

                if (_collection == Collection.NotStarted)
                {
                    HoldBack("cleanup: the teardown was left before the collection concluded; nothing of the body "
                             + "was destroyed");
                }

                _holding.Clear();
                CutWorldRoot.nextWorldExecutors = null;
                WriteClosingLine();
            }

            // Reached only by a teardown that ran to its end. An interrupted one leaves its states and reasons in
            // the finally above and in the closing line; this report is not claimed for it, and it never replaces
            // the exception the test itself is carrying.
            Report();
            yield return null;
        }

        private void WriteClosingLine()
        {
            string line;
            try
            {
                line = "FinalColliderReuse ending: world=" + _ending + " actorRegistered=" + _actorRegistered
                       + " actorDestroyed=" + _actorDestroyed + " madeDisposeCalls=" + _madeDisposeCalls
                       + " collection=" + _collection + " report=[" + string.Join(" | ", _report) + "]";
            }
            catch (Exception describing)
            {
                line = "FinalColliderReuse ending: (describing it threw: " + describing.GetType().Name + ")";
            }

            try
            {
                Debug.Log(line);
            }
            catch (Exception)
            {
                // Not written, and not recorded: this is the end of it.
            }
        }

        private void Report()
        {
            if (_ending == WorldEnding.Confirmed && _collection == Collection.Done)
            {
                return;
            }

            string text = string.Join("\n", _report);
            if (TestContext.CurrentContext.Result.Outcome.Status == TestStatus.Passed)
            {
                Assert.Fail("the fixture could not confirm the ending or collect what the test made:\n" + text);
            }

            TestContext.WriteLine("fixture ending (beside the test's own failure): " + text);
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
            var world = UnityEngine.Object.FindFirstObjectByType<CutWorldRoot>();
            _world = world;
            _endingWorld = world != null ? new WorldEndingOf(world) : null;
            Assert.That(world, Is.Not.Null);
            Assert.That(world.IsReady, Is.True);
            yield return null;

            // Three boxes: one across the plane (cut), two above it (inherited whole by the positive side).
            SandboxCompoundBody made = SandboxCompoundBody.TryBuild(world.Storage, 3, 1, new float3(0.25f, 0.25f, 0.25f), 0);
            _made = made;
            Assert.That(made, Is.Not.Null);
            var inheritedMeshes = new List<Mesh> { made.Shape.MeshOf(1), made.Shape.MeshOf(2) };
            Mesh cutMesh = made.Shape.MeshOf(0);
            var actor = new GameObject("Reuse body");
            _actor = actor;
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
            // Taken by the world, or not: the shape and the actor are the world's from the moment it takes them, and
            // the fixture is told so before anything else can happen -- the assertion comes after.
            bool added = world.TryAddBody(
                actor, made.Shape, made.Geometry, Matrix4x4.identity, Matrix4x4.identity, null, out LogicalFragmentId fragment);
            if (added)
            {
                made.Taken();
                _actorRegistered = true;
            }

            Assert.That(added, Is.True);
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

            // The world's ending, once, on the ordinary frames -- and it must have finished: a deadline that passes
            // is this test's failure, not something the teardown quietly absorbs. The collection of what this test
            // made is the teardown's.
            yield return EndWorldOnce();
            Assert.That(_ending, Is.EqualTo(WorldEnding.Confirmed), "the world's ending finished on the ordinary frames within the deadline");
        }
    }
}
