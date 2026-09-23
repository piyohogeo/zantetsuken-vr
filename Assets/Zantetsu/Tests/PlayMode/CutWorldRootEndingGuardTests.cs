using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The boundaries of <see cref="CutWorldRootPlayModeTests"/>' ending and collection, driven by hand: a case that
    /// made no world, a root made and never built, the order in which the fixture's own resources go, a timeout, a
    /// disposal that throws, the two ways a teardown can be left, and a termination that was meant. Each case asks
    /// about one boundary and reads what the fixture kept, not merely what disappeared in the end.
    /// <para>
    /// **What this does not show.** These are explicit iterator operations against a root that was never activated:
    /// they say nothing about how Unity's test runner interrupts a coroutine, nothing about the deferred destroys of
    /// a real ending, and nothing about a latch that was really fixed. The fixture's own cases cover those on
    /// ordinary frames -- including the one that holds real work and never ends its world.
    /// </para>
    /// </summary>
    public class CutWorldRootEndingGuardTests
    {
        private readonly List<IEnumerator> _iteratorsToDispose = new List<IEnumerator>();
        private readonly List<UnityEngine.Object> _objectsToDestroy = new List<UnityEngine.Object>();

        /// <summary>The test's own clean-up of what it drove and made, kept apart from what was observed.</summary>
        [TearDown]
        public void DisposeWhatThisTestHeld()
        {
            foreach (IEnumerator held in _iteratorsToDispose)
            {
                (held as IDisposable)?.Dispose();
            }

            _iteratorsToDispose.Clear();
            foreach (UnityEngine.Object made in _objectsToDestroy)
            {
                if (made != null)
                {
                    UnityEngine.Object.DestroyImmediate(made);
                }
            }

            _objectsToDestroy.Clear();
        }

        private static CutWorldRootPlayModeTests FreshFixture()
        {
            var fixture = new CutWorldRootPlayModeTests();
            fixture.Fresh();
            return fixture;
        }

        /// <summary>A root whose component exists and was never activated: it never built, and it releases at once.</summary>
        private CutWorldRoot UnbuiltRoot(CutWorldRootPlayModeTests fixture)
        {
            var rootObject = new GameObject("guard: unbuilt cut world");
            _objectsToDestroy.Add(rootObject);
            rootObject.SetActive(false);
            CutWorldRoot root = rootObject.AddComponent<CutWorldRoot>();
            fixture.HoldRootForTest(root);
            fixture.TrackObjectForTest(rootObject);
            return root;
        }

        private GameObject Kept(string name)
        {
            var made = new GameObject(name);
            _objectsToDestroy.Add(made);
            return made;
        }

        /// <summary>A shape stand-in that records when it was disposed, or throws instead.</summary>
        private sealed class WatchedShape : IDisposable
        {
            private readonly Func<int> _clock;
            private readonly bool _throws;

            internal WatchedShape(Func<int> clock, bool throws = false)
            {
                _clock = clock;
                _throws = throws;
            }

            internal int DisposedAt { get; private set; } = -1;

            public void Dispose()
            {
                if (_throws)
                {
                    throw new InvalidOperationException("guard: this shape refuses to be disposed");
                }

                DisposedAt = _clock();
            }
        }

        /// <summary>
        /// Drives an iterator to its end, stepping into the iterators it yields the way a runner would -- minus the
        /// frames: a yielded null is stepped over at once, so nothing deferred is asserted from this.
        /// </summary>
        private static void DriveToEnd(IEnumerator outer, int stepLimit = 10000)
        {
            var stack = new Stack<IEnumerator>();
            stack.Push(outer);
            int steps = 0;
            while (stack.Count > 0)
            {
                Assert.That(++steps, Is.LessThan(stepLimit), "the iterator ended within the step limit");
                IEnumerator top = stack.Peek();
                if (!top.MoveNext())
                {
                    stack.Pop();
                    continue;
                }

                if (top.Current is IEnumerator child)
                {
                    stack.Push(child);
                }
            }
        }

        // ----- 1. a case that made no world, and a root that never built -------------------------------------------------

        [Test]
        public void ACaseThatMadeNoWorld_IsNotAFailure_AndItsOwnObjectsGoBack()
        {
            CutWorldRootPlayModeTests fixture = FreshFixture();
            var tracked = Kept("guard: an object of a case with no world");
            fixture.TrackObjectForTest(tracked);

            DriveToEnd(fixture.Cleanup());

            Assert.That(fixture.EndingIsNoWorld, Is.True, "there was no world to end, which is not a failure");
            Assert.That(fixture.CollectionIsDone, Is.True, "and the case's own objects were given back");
            Assert.That(tracked == null, Is.True);
            Assert.That(fixture.HeldBackCount, Is.Zero);
            Assert.That(fixture.LaterCasesBlocked, Is.False, "so the cases after it are not blocked");
        }

        [Test]
        public void ARootThatWasMadeAndIsNowMissing_IsNotConfirmed_AndNothingIsGivenBack()
        {
            CutWorldRootPlayModeTests fixture = FreshFixture();
            CutWorldRoot root = UnbuiltRoot(fixture);
            var shape = new WatchedShape(() => 0);
            fixture.TrackShapeForTest(shape);

            // The root the case made is gone -- destroyed by something else -- which is not the same as never making one.
            UnityEngine.Object.DestroyImmediate(root.gameObject);

            DriveToEnd(fixture.Cleanup());

            Assert.That(fixture.EndingIsNotConfirmed, Is.True, "a root that was made and is missing is not confirmed");
            Assert.That(fixture.CollectionIsHeldBack, Is.True);
            Assert.That(shape.DisposedAt, Is.EqualTo(-1), "the shape was kept: its use was never confirmed over");
            Assert.That(fixture.KeptShapeCount, Is.EqualTo(1), "and it is still held by the fixture");
            Assert.That(fixture.LaterCasesBlocked, Is.True);
        }

        // ----- 2. the order the fixture's own resources go in ------------------------------------------------------------

        [Test]
        public void TheCollection_DestroysAnUnregisteredActorBeforeTheShapesAndMeshesItPointsAt()
        {
            CutWorldRootPlayModeTests fixture = FreshFixture();
            UnbuiltRoot(fixture);
            GameObject actor = Kept("guard: unregistered actor");
            fixture.TrackActorForTest(actor, registered: false);

            // What the shape sees at the moment it is disposed: the actor gone, and the mesh its colliders pointed
            // at still alive. A collection that destroyed the mesh first would be read here, not merely inferred
            // from what is missing at the end.
            var mesh = new Mesh { name = "guard: collider mesh" };
            _objectsToDestroy.Add(mesh);
            var shape = new WatchedShape(() => (actor == null ? 1 : 0) + (mesh != null ? 2 : 0));
            fixture.TrackShapeForTest(shape);
            fixture.TrackObjectForTest(mesh);

            DriveToEnd(fixture.Cleanup());

            Assert.That(fixture.CollectionIsDone, Is.True);
            Assert.That(
                shape.DisposedAt, Is.EqualTo(3),
                "at the shape's disposal the actor was gone (1) and the mesh was still alive (2)");
            Assert.That(mesh == null, Is.True, "and the mesh went after it");
            Assert.That(fixture.KeptObjectCount, Is.Zero);
            Assert.That(fixture.KeptShapeCount, Is.Zero);
        }

        [Test]
        public void ARegisteredActorStillAlive_IsNotDestroyedByForce_AndTheMeshesStay()
        {
            CutWorldRootPlayModeTests fixture = FreshFixture();
            UnbuiltRoot(fixture);
            GameObject actor = Kept("guard: registered actor the world left behind");
            fixture.TrackActorForTest(actor, registered: true);
            var shape = new WatchedShape(() => 0);
            fixture.TrackShapeForTest(shape);
            var mesh = new Mesh { name = "guard: collider mesh" };
            _objectsToDestroy.Add(mesh);
            fixture.TrackObjectForTest(mesh);

            DriveToEnd(fixture.Cleanup());

            Assert.That(fixture.EndingIsConfirmed, Is.True, "the world's ending was confirmed");
            Assert.That(fixture.CollectionIsHeldBack, Is.True, "but an actor the world took is still alive");
            Assert.That(actor != null, Is.True, "and it was not destroyed by force");
            Assert.That(shape.DisposedAt, Is.EqualTo(-1), "the shape was kept");
            Assert.That(mesh != null, Is.True, "and so was the mesh its colliders point at");
            Assert.That(fixture.KeptActorCount, Is.EqualTo(1));
            Assert.That(fixture.ReportForTest, Has.Some.Contains("still alive after the ending"));
        }

        // ----- 3. a timeout, and a disposal that throws ------------------------------------------------------------------

        [Test]
        public void AnEndingWhoseDeadlineHasPassed_ReleasesNothing_AndRetriesNothing()
        {
            CutWorldRootPlayModeTests fixture = FreshFixture();
            int releaseAsks = 0;
            fixture.UseReleaseProbeForTest(() =>
            {
                releaseAsks++;
                return false;
            });

            // Zero: the deadline is past on the first look, so this case does not depend on how fast it runs.
            fixture.UseEndingDeadlineForTest(0f);
            var shape = new WatchedShape(() => 0);
            fixture.TrackShapeForTest(shape);
            var tracked = Kept("guard: an object of a world that never released");
            fixture.TrackObjectForTest(tracked);

            DriveToEnd(fixture.Cleanup());

            Assert.That(fixture.EndingIsNotConfirmed, Is.True, "the deadline was past, and nothing was released");
            Assert.That(fixture.CollectionIsHeldBack, Is.True);
            Assert.That(shape.DisposedAt, Is.EqualTo(-1), "nothing was disposed");
            Assert.That(tracked != null, Is.True, "and nothing was destroyed");
            Assert.That(fixture.ReportForTest, Has.Some.Contains("was not released within"));
            Assert.That(fixture.LaterCasesBlocked, Is.True);

            // A second call waits for nothing and destroys nothing: neither the ending nor the collection is retried.
            int asksAfterTheTimeout = releaseAsks;
            DriveToEnd(fixture.EndWorldOnceForTest());
            DriveToEnd(fixture.CollectOnceForTest());
            Assert.That(releaseAsks, Is.EqualTo(asksAfterTheTimeout), "the release was not asked about again");
            Assert.That(shape.DisposedAt, Is.EqualTo(-1), "and still nothing was disposed");
            Assert.That(tracked != null, Is.True);
            Assert.That(fixture.HeldBackCount, Is.EqualTo(1), "held back once");
        }

        [Test]
        public void ADisposalThatThrows_KeepsTheRestOfItsListAndEveryListAfterIt()
        {
            CutWorldRootPlayModeTests fixture = FreshFixture();
            UnbuiltRoot(fixture);
            var refuses = new WatchedShape(() => 0, throws: true);
            var after = new WatchedShape(() => 0);
            fixture.TrackShapeForTest(refuses);
            fixture.TrackShapeForTest(after);
            var mesh = new Mesh { name = "guard: a mesh of a later list" };
            _objectsToDestroy.Add(mesh);
            fixture.TrackObjectForTest(mesh);

            DriveToEnd(fixture.Cleanup());

            Assert.That(fixture.CollectionIsHeldBack, Is.True);
            Assert.That(after.DisposedAt, Is.EqualTo(-1), "the shape after the one that threw was not disposed");
            Assert.That(fixture.KeptShapeCount, Is.EqualTo(2), "both are still held, the one that threw included");
            Assert.That(mesh != null, Is.True, "and the objects list after it was not touched at all");
            Assert.That(fixture.KeptObjectCount, Is.GreaterThan(0));
            Assert.That(fixture.ReportForTest, Has.Some.Contains("no later list was touched"));
        }

        // ----- 4. the two ways a teardown can be left -------------------------------------------------------------------

        [Test]
        public void TheTeardownLeftBeforeItsChildEverRan_IsNotStartedAgainByAnotherCall()
        {
            CutWorldRootPlayModeTests fixture = FreshFixture();
            fixture.UseReleaseProbeForTest(() => false);
            var shape = new WatchedShape(() => 0);
            fixture.TrackShapeForTest(shape);

            IEnumerator teardown = fixture.Cleanup();
            _iteratorsToDispose.Add(teardown);
            Assert.That(teardown.MoveNext(), Is.True, "the teardown handed out its child");
            Assert.That(teardown.Current, Is.InstanceOf<IEnumerator>(), "which has not been driven");
            Assert.That(fixture.EndingIsNotStarted, Is.True, "so the ending has not begun");

            // The outer is left before the child ever ran.
            ((IDisposable)teardown).Dispose();
            _iteratorsToDispose.Remove(teardown);

            Assert.That(fixture.WasAbandoned, Is.True, "the teardown was left, and that is final");
            Assert.That(fixture.EndingIsNotConfirmed, Is.True);
            Assert.That(fixture.CollectionIsHeldBack, Is.True);

            // A second call starts nothing: neither the ending nor the collection.
            DriveToEnd(fixture.EndWorldOnceForTest());
            DriveToEnd(fixture.CollectOnceForTest());
            Assert.That(fixture.EndingIsNotConfirmed, Is.True, "the ending was not begun again");
            Assert.That(fixture.CollectionIsHeldBack, Is.True, "and the collection was not either");
            Assert.That(shape.DisposedAt, Is.EqualTo(-1), "nothing was disposed by the second call");
            Assert.That(fixture.HeldBackCount, Is.EqualTo(1), "held back once");
        }

        [Test]
        public void TheChildLeftMidCollection_ChangesNothingWhenItResumes()
        {
            CutWorldRootPlayModeTests fixture = FreshFixture();
            UnbuiltRoot(fixture);
            GameObject actor = Kept("guard: unregistered actor");
            fixture.TrackActorForTest(actor, registered: false);
            var shape = new WatchedShape(() => 0);
            fixture.TrackShapeForTest(shape);

            IEnumerator teardown = fixture.Cleanup();
            _iteratorsToDispose.Add(teardown);

            // The ending, driven through the outer, and then the collection up to its one wait.
            Assert.That(teardown.MoveNext(), Is.True);
            var ending = (IEnumerator)teardown.Current;
            DriveToEnd(ending);
            Assert.That(fixture.EndingIsConfirmed, Is.True);
            Assert.That(teardown.MoveNext(), Is.True, "the teardown handed out the collection");
            var collection = (IEnumerator)teardown.Current;
            _iteratorsToDispose.Add(collection);
            Assert.That(collection.MoveNext(), Is.True, "which reached its wait");
            Assert.That(collection.Current, Is.Null);
            Assert.That(actor == null, Is.True, "the unregistered actor was destroyed before the wait");
            Assert.That(shape.DisposedAt, Is.EqualTo(-1), "and nothing after the wait has run");

            // The outer is left while the collection waits; the child is not touched.
            ((IDisposable)teardown).Dispose();
            _iteratorsToDispose.Remove(teardown);
            Assert.That(fixture.WasAbandoned, Is.True);
            Assert.That(fixture.CollectionIsHeldBack, Is.True);

            // The child is resumed afterwards -- the one thing that used to turn a hold-back into a success.
            bool moved = collection.MoveNext();
            Assert.That(moved, Is.False, "the collection ends where it was left");
            Assert.That(fixture.CollectionIsHeldBack, Is.True, "it is still held back");
            Assert.That(shape.DisposedAt, Is.EqualTo(-1), "nothing was disposed by the resumption");
            Assert.That(fixture.KeptShapeCount, Is.EqualTo(1), "and the shape is still held");
            Assert.That(fixture.KeptProfile, Is.False, "there was no profile in this case to begin with");
            Assert.That(fixture.HeldBackCount, Is.EqualTo(1));
        }

        [Test]
        public void AfterASuccessfulCollection_TheFixtureIsReadyForTheNextCase()
        {
            CutWorldRootPlayModeTests fixture = FreshFixture();
            UnbuiltRoot(fixture);
            GameObject actor = Kept("guard: unregistered actor");
            fixture.TrackActorForTest(actor, registered: false);
            fixture.AddHoldingForTest();
            var shape = new WatchedShape(() => 0);
            fixture.TrackShapeForTest(shape);

            DriveToEnd(fixture.Cleanup());

            Assert.That(fixture.EndingIsConfirmed, Is.True);
            Assert.That(fixture.CollectionIsDone, Is.True);
            Assert.That(fixture.HoldingCount, Is.Zero, "the destinations it held are of no more use, so they go too");
            Assert.That(fixture.KeptObjectCount, Is.Zero);
            Assert.That(fixture.KeptActorCount, Is.Zero);
            Assert.That(fixture.KeptShapeCount, Is.Zero);

            // **The next case of the same fixture starts.** This is what a held reference would have stopped.
            Assert.DoesNotThrow(() => fixture.Fresh(), "the next case's SetUp finds the fixture holding nothing");
            Assert.That(fixture.LaterCasesBlocked, Is.False);
        }

        // ----- 5. a termination this case meant ---------------------------------------------------------------------------

        [Test]
        public void ATerminationThatWasMeantButNeverCame_IsEndedTheOrdinaryWay()
        {
            CutWorldRootPlayModeTests fixture = FreshFixture();
            fixture.ExpectTerminationForTest();
            CutWorldRoot root = UnbuiltRoot(fixture);
            Assert.That(root.TerminationRequested, Is.False, "the latch was never fixed");
            var shape = new WatchedShape(() => 0);
            fixture.TrackShapeForTest(shape);

            DriveToEnd(fixture.Cleanup());

            Assert.That(fixture.EndingIsConfirmed, Is.True, "before the latch, the ordinary ending applies");
            Assert.That(fixture.CollectionIsDone, Is.True);
            Assert.That(shape.DisposedAt, Is.EqualTo(0), "and the case's own resources went back");
            Assert.That(fixture.HeldBackCount, Is.Zero);
        }
    }
}
