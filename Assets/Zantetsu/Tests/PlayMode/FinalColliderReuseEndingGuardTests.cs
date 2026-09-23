using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The guards of <see cref="FinalColliderReusePlayModeTests"/>' ending and collection, driven by hand: what a
    /// wait that is left before it concluded leaves behind, and what a second call after that does not do. The
    /// iterators are advanced and disposed explicitly here, against a world that never releases and with no compound
    /// body at all.
    /// <para>
    /// **What this does not show.** These are explicit iterator operations. They say nothing about how Unity's test
    /// runner really interrupts a coroutine, and nothing about the deferred destroys of a real ending, which the
    /// existing <c>InheritedConvexes_...</c> case covers on ordinary frames. A dispose count of zero here says the
    /// collection did not run again; it is not a check that a living body was kept.
    /// </para>
    /// </summary>
    public class FinalColliderReuseEndingGuardTests
    {
        /// <summary>A world whose ending is asked for and never finishes.</summary>
        private sealed class NeverReleasedWorld : IWorldEnding
        {
            public int ShutdownCalls { get; private set; }

            public bool IsReleased => false;

            public bool IsDrained()
            {
                return false;
            }

            public void Shutdown()
            {
                ShutdownCalls++;
            }
        }

        private readonly List<IEnumerator> _iteratorsToDispose = new List<IEnumerator>();
        private readonly List<GameObject> _objectsToDestroy = new List<GameObject>();

        [SetUp]
        public void NoLeftoverExecutors()
        {
            CutWorldRoot.nextWorldExecutors = null;
        }

        /// <summary>The test's own clean-up of what it drove and made, kept apart from what was observed.</summary>
        [TearDown]
        public void DisposeWhatThisTestHeld()
        {
            foreach (IEnumerator held in _iteratorsToDispose)
            {
                (held as IDisposable)?.Dispose();
            }

            _iteratorsToDispose.Clear();
            foreach (GameObject made in _objectsToDestroy)
            {
                if (made != null)
                {
                    UnityEngine.Object.DestroyImmediate(made);
                }
            }

            _objectsToDestroy.Clear();
            CutWorldRoot.nextWorldExecutors = null;
        }

        private static FinalColliderReusePlayModeTests FreshFixture()
        {
            var fixture = new FinalColliderReusePlayModeTests();
            fixture.Fresh();
            return fixture;
        }

        [Test]
        public void A_TheEndingsWait_LeftBeforeItConcluded_IsNotConfirmed()
        {
            FinalColliderReusePlayModeTests fixture = FreshFixture();
            var world = new NeverReleasedWorld();
            fixture.UseWorldForTest(world);

            IEnumerator ending = fixture.EndWorldOnceForTest();
            _iteratorsToDispose.Add(ending);

            // Driven to its wait: the ending was requested, nothing is released, and the iterator is at a yield.
            Assert.That(ending.MoveNext(), Is.True, "the ending reached its wait");
            Assert.That(ending.Current, Is.Null, "and is waiting on a frame");
            Assert.That(world.ShutdownCalls, Is.EqualTo(1), "the ending was requested once");
            Assert.That(fixture.EndingIsNotStarted, Is.True, "and nothing has been concluded while it waits");

            // Left there.
            ((IDisposable)ending).Dispose();
            _iteratorsToDispose.Remove(ending);

            Assert.That(fixture.EndingIsNotConfirmed, Is.True, "a wait left before it concluded is not a confirmation");
            Assert.That(fixture.ReportForTest, Has.Some.Contains("left before it concluded"));
        }

        [Test]
        public void B_TheCollectionsWait_LeftBeforeItConcluded_IsNotBegunAgain()
        {
            FinalColliderReusePlayModeTests fixture = FreshFixture();
            var actor = new GameObject("guard test actor");
            _objectsToDestroy.Add(actor);
            fixture.BeginCollectionForTest(actor);

            IEnumerator collection = fixture.CollectOnceForTest();
            _iteratorsToDispose.Add(collection);

            // Driven to its wait: the unregistered actor's destroy was asked for, and the iterator is at the frame it
            // waits for.
            Assert.That(collection.MoveNext(), Is.True, "the collection reached its wait");
            Assert.That(collection.Current, Is.Null, "and is waiting on a frame");
            Assert.That(fixture.CollectionWasEntered, Is.True, "it was entered");
            Assert.That(fixture.CollectionIsNotStarted, Is.True, "and has concluded nothing");

            // Left there.
            ((IDisposable)collection).Dispose();
            _iteratorsToDispose.Remove(collection);

            // A second call does nothing at all: it does not begin the collection again.
            IEnumerator again = fixture.CollectOnceForTest();
            _iteratorsToDispose.Add(again);
            Assert.That(again.MoveNext(), Is.False, "the second call ends at once");
            Assert.That(fixture.CollectionIsNotStarted, Is.True, "nothing was concluded by it");
            Assert.That(fixture.MadeDisposeCalls, Is.Zero, "and the collection's disposal step did not run (there is no body here to keep)");
        }

        [Test]
        public void C_TheTeardown_LeftWhileItsChildWaits_HoldsBackAndRestores()
        {
            FinalColliderReusePlayModeTests fixture = FreshFixture();
            var world = new NeverReleasedWorld();
            fixture.UseWorldForTest(world);
            fixture.AddHoldingForTest();
            CutWorldRoot.nextWorldExecutors = destination => null;

            IEnumerator teardown = fixture.Cleanup();
            _iteratorsToDispose.Add(teardown);

            // The outer iterator's first step hands out its child; the child has not moved yet.
            Assert.That(teardown.MoveNext(), Is.True, "the teardown began");
            var child = teardown.Current as IEnumerator;
            Assert.That(child, Is.Not.Null, "its first step is the ending's own iterator, not yet driven");
            _iteratorsToDispose.Add(child);

            // The child is driven to its wait by hand.
            Assert.That(child.MoveNext(), Is.True, "the ending reached its wait");
            Assert.That(child.Current, Is.Null);
            Assert.That(world.ShutdownCalls, Is.EqualTo(1));
            Assert.That(fixture.EndingIsNotStarted, Is.True, "nothing concluded while it waits");

            // The outer is left, and the child is not touched: what the outer's own finally does, from the states.
            ((IDisposable)teardown).Dispose();
            _iteratorsToDispose.Remove(teardown);

            Assert.That(fixture.EndingIsNotConfirmed, Is.True, "the ending is marked not confirmed");
            Assert.That(fixture.CollectionIsHeldBack, Is.True, "the collection is held back");
            Assert.That(fixture.HeldBackCount, Is.EqualTo(1), "once");
            Assert.That(fixture.HeldBackExecutorCountOfLast, Is.EqualTo(1), "with the executor's reference kept");
            Assert.That(fixture.HoldingCount, Is.Zero, "the fixture's own list was cleared after that");
            Assert.That(CutWorldRoot.nextWorldExecutors, Is.Null, "and the world's executor hook was restored");
            Assert.That(fixture.ReportForTest, Has.Some.Contains("left before the collection concluded"));

            // The test's own clean-up of the child it drove, recorded apart from the observation above: disposing it
            // now runs its finally, which finds the ending already concluded and changes nothing.
            ((IDisposable)child).Dispose();
            _iteratorsToDispose.Remove(child);
            Assert.That(fixture.HeldBackCount, Is.EqualTo(1), "disposing the child afterwards holds nothing back again");
        }
    }
}
