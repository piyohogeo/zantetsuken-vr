using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Zantetsu.MeshCut.Verification;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// One frame's update through the product's own entry: take back what the workers finished, let the main thread
    /// carry those results forward, and submit what that made ready -- in the same frame, out of the same budget.
    /// <para>
    /// Nothing here drives anything by repeating a test helper: every case calls
    /// <see cref="SharedWorkFrame.Update"/>, which is what a composition root would call, and **the frame id does not
    /// change** while a case is watching. What is watched is the destination itself: which work it accepted, which it
    /// was told to begin, and in what order. Reaching a stage or joining a queue is not submission.
    /// </para>
    /// <para>
    /// The destination used here runs the work for real -- the work's own <c>Begin</c>, the work's own
    /// <c>IsComplete</c> -- and hands back only what has **really finished** and the test has released. Nothing is
    /// completed by force, nothing unfinished is handed over, and no case waits on wall-clock time for an order.
    /// </para>
    /// </summary>
    public class SharedWorkFrameTests
    {
        private const int DeadlineMilliseconds = 30000;

        // ----- a destination the test can watch and hold ---------------------------------------------------------

        /// <summary>
        /// A destination that keeps what it accepted, in order, and hands a work back only once it has finished of
        /// its own accord **and** the test has let it go. Holding something back delays a collection that would
        /// otherwise happen; it never makes a work finish, and it never hands over one that has not.
        /// </summary>
        private sealed class WatchedExecutor : IWorkExecutor
        {
            private readonly List<IDispatchWork> _held = new List<IDispatchWork>();
            private readonly List<IDispatchWork> _released = new List<IDispatchWork>();
            private readonly Dictionary<IDispatchWork, Exception> _beginFailures = new Dictionary<IDispatchWork, Exception>();
            private bool _closed;

            internal WatchedExecutor(WorkDestination destination, int capacity)
            {
                Destination = destination;
                Capacity = capacity;
            }

            /// <summary>Every work this took, in the order it took them.</summary>
            internal List<IDispatchWork> Accepted { get; } = new List<IDispatchWork>();

            /// <summary>Every work this was told to begin, in the order it began them.</summary>
            internal List<IDispatchWork> Begun { get; } = new List<IDispatchWork>();

            /// <summary>Whether the test is holding everything back until it says otherwise.</summary>
            internal bool HoldEverything { get; set; }

            public WorkDestination Destination { get; }

            public int Capacity { get; }

            public int Held => _held.Count;

            public bool CanAccept => !_closed && _held.Count < Capacity;

            /// <summary>Lets this one be handed back, if and when it has really finished.</summary>
            internal void Release(IDispatchWork work)
            {
                if (!_released.Contains(work))
                {
                    _released.Add(work);
                }
            }

            /// <summary>Lets everything accepted so far, and everything accepted later, be handed back.</summary>
            internal void ReleaseEverything()
            {
                HoldEverything = false;
                for (int i = 0; i < Accepted.Count; i++)
                {
                    Release(Accepted[i]);
                }
            }

            /// <summary>Whether this work has finished running and is waiting only on the test.</summary>
            internal bool FinishedButHeld(IDispatchWork work)
            {
                return _held.Contains(work) && work.IsComplete && !Lets(work);
            }

            private bool Lets(IDispatchWork work)
            {
                return !HoldEverything || _released.Contains(work);
            }

            public bool TryAccept(IDispatchWork work)
            {
                if (work == null)
                {
                    throw new ArgumentNullException(nameof(work));
                }

                if (!CanAccept)
                {
                    return false;
                }

                _held.Add(work);
                Accepted.Add(work);
                return true;
            }

            public void BeginAccepted(IDispatchWork work)
            {
                if (!_held.Contains(work))
                {
                    throw new InvalidOperationException("this destination has not accepted that work");
                }

                Begun.Add(work);
                try
                {
                    work.Begin();
                }
                catch (Exception failure)
                {
                    _beginFailures[work] = failure;
                    throw;
                }
            }

            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion)
            {
                for (int i = 0; i < _held.Count; i++)
                {
                    IDispatchWork candidate = _held[i];

                    // Unfinished work is never handed over, and neither is work the test is holding.
                    if (!candidate.IsComplete || !Lets(candidate))
                    {
                        continue;
                    }

                    _held.RemoveAt(i);
                    work = candidate;
                    completion = _beginFailures.TryGetValue(candidate, out Exception failure)
                        ? WorkCompletion.Failed(failure)
                        : WorkCompletion.Finished;
                    return true;
                }

                work = null;
                completion = default;
                return false;
            }

            public void CloseForNewWork()
            {
                _closed = true;
            }

            public bool StopAndConfirm(int timeoutMilliseconds)
            {
                _closed = true;
                ReleaseEverything();
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < timeoutMilliseconds)
                {
                    bool allDone = true;
                    for (int i = 0; i < _held.Count; i++)
                    {
                        allDone &= _held[i].IsComplete;
                    }

                    if (allDone)
                    {
                        return true;
                    }

                    System.Threading.Thread.Sleep(1);
                }

                return false;
            }
        }

        // ----- one storage, one runner, one frame ------------------------------------------------------------------

        private sealed class Scene : IDisposable
        {
            internal VpCpuGeometryStorage storage;
            internal WatchedExecutor geometry;
            internal UnityJobWorkExecutor job;
            internal WorkerPoolExecutor background;
            internal SharedWorkDispatcher dispatcher;
            internal VpAsyncStorageCut runner;
            internal SharedWorkFrame frame;

            public void Dispose()
            {
                runner?.Dispose();
                geometry?.ReleaseEverything();
                dispatcher?.Shutdown(DeadlineMilliseconds);
                runner?.Pump();
                background?.Dispose();
                storage?.Dispose();
            }
        }

        private static Scene NewScene(
            int vertexCapacity = 4096, int indexCapacity = 16384, int frameBudget = 32, int geometryCapacity = 4)
        {
            var scene = new Scene
            {
                storage = new VpCpuGeometryStorage(vertexCapacity, indexCapacity, 64, 256, 256, Allocator.Persistent),
                geometry = new WatchedExecutor(WorkDestination.GeometryPool, geometryCapacity),
                job = new UnityJobWorkExecutor(4),
                background = WorkerPoolExecutor.BackgroundPool(2),
            };
            scene.dispatcher = new SharedWorkDispatcher(8, 2, frameBudget, scene.job, scene.geometry, scene.background);
            scene.runner = new VpAsyncStorageCut(scene.storage, scene.dispatcher);
            scene.frame = new SharedWorkFrame(scene.dispatcher);
            scene.frame.Add(scene.runner);
            return scene;
        }

        private static SyntheticMesh Box(int n = 3)
        {
            return SyntheticGeometry.Box(n, new float3(1f, 1f, 1f), float3.zero, true)
                .Finish(new LogicalMeshBuilder.AttributeOptions { CreaseAngle = 30, CylindricalUv = true });
        }

        private static VpStoredGeometry Append(VpCpuGeometryStorage storage, SyntheticMesh mesh)
        {
            var submeshes = new VpGeometrySubmesh[mesh.SubmeshIndexCounts.Count];
            int offset = 0;
            for (int s = 0; s < submeshes.Length; s++)
            {
                submeshes[s] = new VpGeometrySubmesh(offset, mesh.SubmeshIndexCounts[s], s);
                offset += mesh.SubmeshIndexCounts[s];
            }

            Assert.That(
                storage.TryAppendCuttable(
                    mesh.Vertices, mesh.Indices, mesh.TopologyOfVertex, mesh.TopologyVertexCount, submeshes,
                    out VpStoredGeometry geometry, out _),
                Is.True,
                "the box is appended as a cut input");
            return geometry;
        }

        private static VpStorageCutInput Acquire(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            Assert.That(VpStorageCutInput.TryAcquire(storage, geometry, out VpStorageCutInput input), Is.True, "acquire");
            return input;
        }

        private static float4 Tilted()
        {
            return SyntheticGeometry.Plane(new float3(0.37f, 0.61f, -0.7f), new float3(0.0071f, -0.0233f, 0.0119f));
        }

        /// <summary>
        /// Updates frame after frame until the condition holds, releasing whatever has finished. For winding a case
        /// down once what it was about has been established -- a case whose frame budget is deliberately small cannot
        /// finish inside one frame, and is not meant to.
        /// </summary>
        private static void RunFramesUntil(Scene scene, int fromFrameId, Func<bool> condition, string what)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int f = 0; clock.ElapsedMilliseconds < DeadlineMilliseconds; f++)
            {
                scene.geometry.ReleaseEverything();
                scene.frame.Update(fromFrameId + f);
                if (condition())
                {
                    return;
                }

                System.Threading.Thread.Sleep(1);
            }

            Assert.Fail(what + ": it had not happened within the deadline");
        }

        /// <summary>Updates the same frame until the condition holds, releasing whatever has finished as it appears.</summary>
        private static void RunSameFrameUntil(Scene scene, int frameId, Func<bool> condition, string what)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < DeadlineMilliseconds)
            {
                scene.geometry.ReleaseEverything();
                scene.frame.Update(frameId);
                if (condition())
                {
                    return;
                }

                System.Threading.Thread.Sleep(1);
            }

            Assert.Fail(what + ": it had not happened within the deadline");
        }

        // ----- the cases -----------------------------------------------------------------------------------------

        /// <summary>
        /// A cut that could not have a reservation is given one, and submitted, in the **same frame** the cut holding
        /// that reservation was collected in. The storage has room for one output at a time here, so the second cut
        /// waits on the first and on nothing else.
        /// <para>
        /// The frame id does not change while this happens: the whole of it is one update.
        /// </para>
        /// </summary>
        [Test]
        public void ACutWaitingForRoom_IsSubmittedInTheFrameTheRoomCameBackIn()
        {
            using (Scene scene = NewScene())
            {
                VpStoredGeometry first = Append(scene.storage, Box());
                VpStoredGeometry second = Append(scene.storage, Box());
                scene.geometry.HoldEverything = true;

                // The first cut asks for nearly all the room the storage has left, so the second has nowhere to go
                // until the first gives it back. The sizes are read from the storage rather than guessed at.
                var nearlyEverything = new VpStorageCutOptions
                {
                    newVertexCapacity = scene.storage.FreeVertexRoom - 64,
                    newIndexCapacity = scene.storage.FreeIndexRoom - 256,
                };
                VpStorageCutRequest a = scene.runner.Submit(Acquire(scene.storage, first), Tilted(), nearlyEverything);
                VpStorageCutRequest b = scene.runner.Submit(Acquire(scene.storage, second), Tilted(), default);

                // One frame, one update: the first takes the only room there is and is submitted; the second cannot
                // even be offered.
                SharedWorkFrameProgress opened = scene.frame.Update(1);
                Assert.That(opened.MadeProgress, Is.True, "the first update moved something");
                Assert.That(scene.geometry.Accepted.Count, Is.EqualTo(1), "one work was taken by the destination");
                Assert.That(scene.geometry.Begun.Count, Is.EqualTo(1), "and begun");
                Assert.That(a.HoldsReservation, Is.True, "the first holds the room");
                Assert.That(b.HoldsReservation, Is.False, "and the second has none");
                Assert.That(b.Stage, Is.EqualTo(VpStorageCutStage.Ready), "it is waiting, not offered");

                // Its work finishes, and the test lets it be collected -- still in frame 1.
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds
                    && !scene.geometry.FinishedButHeld(scene.geometry.Accepted[0]))
                {
                    System.Threading.Thread.Sleep(1);
                }

                Assert.That(
                    scene.geometry.FinishedButHeld(scene.geometry.Accepted[0]), Is.True,
                    "the first work has finished and is waiting only on the test");

                scene.geometry.Release(scene.geometry.Accepted[0]);
                SharedWorkFrameProgress carried = scene.frame.Update(1);

                Assert.That(carried.collected, Is.GreaterThan(0), "the finished work was taken back");
                Assert.That(
                    scene.geometry.Accepted.Count, Is.EqualTo(2),
                    "and the cut that was waiting for the room was submitted in the same frame");
                Assert.That(scene.geometry.Begun.Count, Is.EqualTo(2), "and begun");
                Assert.That(b.HoldsReservation, Is.True, "it holds the room the first gave back");
                Assert.That(b.Stage, Is.EqualTo(VpStorageCutStage.Cutting), "and is with the dispatcher");
                Assert.That(
                    carried.occasions, Is.GreaterThan(1),
                    "and the update it happened in went round more than once: " + carried.occasions
                    + " occasions. Which occasion submitted it is not read from this count.");

                scene.geometry.ReleaseEverything();
                RunSameFrameUntil(scene, 1, () => a.IsOver && b.IsOver, "both cuts end in the same frame");
                Assert.That(a.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "the first cut succeeded");
                Assert.That(b.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "and so did the second");
                TestContext.WriteLine(
                    "frame 1 throughout; works really submitted, in order: " + scene.geometry.Accepted.Count
                    + "; the second was submitted within the update that collected the first, which went round "
                    + carried.occasions + " times");
            }
        }

        /// <summary>
        /// A work whose **collection the test is holding back** is collected at a later update of the same frame, once
        /// the hold is lifted -- not left to the next frame.
        /// <para>
        /// This says nothing about unfinished work. The destination here begins a cut where it accepts it, so the cut's
        /// kernel has run by the time the first update returns; the reason nothing was collected is the hold and
        /// nothing else. What happens when a work really has not finished is
        /// <see cref="AWorkThatHasNotFinished_LeavesTheUpdateWithoutWaiting_AndIsCollectedLaterInTheSameFrame"/>.
        /// </para>
        /// </summary>
        [Test]
        public void AWorkWhoseCollectionIsHeldBack_IsCollectedAtALaterUpdateOfTheSameFrame()
        {
            using (Scene scene = NewScene())
            {
                VpStoredGeometry box = Append(scene.storage, Box());
                scene.geometry.HoldEverything = true;
                VpStorageCutRequest request = scene.runner.Submit(Acquire(scene.storage, box), Tilted(), default);

                SharedWorkFrameProgress first = scene.frame.Update(7);
                Assert.That(scene.geometry.Accepted.Count, Is.EqualTo(1), "it was submitted");
                Assert.That(
                    first.collected, Is.Zero,
                    "and nothing was collected -- because the test is holding it, which is what this case is about");
                Assert.That(request.IsOver, Is.False);

                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds
                    && !scene.geometry.FinishedButHeld(scene.geometry.Accepted[0]))
                {
                    System.Threading.Thread.Sleep(1);
                }

                scene.geometry.Release(scene.geometry.Accepted[0]);

                // The same frame, later: it is taken back here.
                SharedWorkFrameProgress later = scene.frame.Update(7);
                Assert.That(later.collected, Is.GreaterThan(0), "the later update of the same frame collected it");
                Assert.That(request.IsOver, Is.True, "and the cut ended there");
                Assert.That(request.Result.status, Is.EqualTo(VpStorageCutStatus.Ok));
            }
        }

        /// <summary>
        /// A cut that is **ahead of** the one whose return frees the room still takes it, in the same update. The cuts
        /// are advanced in the order they were asked for, so this is the case where the one that has been waiting
        /// longest is the one served -- not merely the last one looked at.
        /// <para>
        /// The waiting cut got there by asking again: its first reservation was far too small for its own output, so it
        /// gave that room back and needs a larger one, which does not fit while the other holds its own. It is first in
        /// the order all the while.
        /// </para>
        /// </summary>
        [Test]
        public void ACutAheadOfTheOneThatFreesTheRoom_IsTheOneServed()
        {
            using (Scene scene = NewScene())
            {
                VpStoredGeometry firstBox = Append(scene.storage, Box());
                VpStoredGeometry secondBox = Append(scene.storage, Box());
                scene.geometry.HoldEverything = true;

                // The first asks for far too little, so it must give its room back and ask again. The second asks for
                // nearly everything that is left, so the first cannot have what it needs until the second returns.
                // Too little **room**; the scratch is left alone, so what it has to ask again for is the storage's.
                var tooLittle = new VpStorageCutOptions { newVertexCapacity = 1, newIndexCapacity = 3 };
                VpStorageCutRequest retrying = scene.runner.Submit(Acquire(scene.storage, firstBox), Tilted(), tooLittle);
                // Nearly everything, with only a few slots to spare -- far less than the first cut's real output needs,
                // so its next attempt cannot fit until this one gives its room back.
                var nearlyEverything = new VpStorageCutOptions
                {
                    newVertexCapacity = scene.storage.FreeVertexRoom - 4,
                    newIndexCapacity = scene.storage.FreeIndexRoom - 8,
                };
                VpStorageCutRequest holding = scene.runner.Submit(Acquire(scene.storage, secondBox), Tilted(), nearlyEverything);

                // Frame 1 throughout. Both are submitted, in the order they were asked for.
                scene.frame.Update(1);
                Assert.That(scene.geometry.Accepted.Count, Is.EqualTo(2), "both were submitted");
                IDispatchWork retryingWork = scene.geometry.Accepted[0];
                IDispatchWork holdingWork = scene.geometry.Accepted[1];

                // Only the short one is let through: its run comes back too small, it gives its room up and asks for
                // more -- which does not fit, because the other still holds nearly all of it.
                WaitUntilFinishedButHeld(scene, retryingWork);
                scene.geometry.Release(retryingWork);
                scene.frame.Update(1);

                Assert.That(retrying.Result.attempts, Is.GreaterThanOrEqualTo(1), "the short attempt was collected");
                Assert.That(retrying.IsOver, Is.False, "and it is asking again rather than failing");
                Assert.That(retrying.HoldsReservation, Is.False, "with no room of its own at the moment");
                Assert.That(holding.HoldsReservation, Is.True, "because the other one still holds it");
                int submittedWhileItWaited = scene.geometry.Accepted.Count;

                // Now the other is let through. It is second in the order the cuts are advanced in, and the one that
                // has been waiting is first -- and that is the one the room goes to, in this same update.
                WaitUntilFinishedButHeld(scene, holdingWork);
                scene.geometry.Release(holdingWork);
                scene.frame.Update(1);

                Assert.That(holding.IsOver, Is.True, "the one that held the room finished");
                Assert.That(holding.Result.status, Is.EqualTo(VpStorageCutStatus.Ok));
                Assert.That(
                    scene.geometry.Accepted.Count, Is.GreaterThan(submittedWhileItWaited),
                    "and the cut ahead of it in the order was given the room and submitted, in this same update");
                Assert.That(
                    ReferenceEquals(scene.geometry.Accepted[scene.geometry.Accepted.Count - 1], retryingWork), Is.True,
                    "which is this request's own work, offered again on its larger reservation");
                Assert.That(retrying.Result.attempts, Is.GreaterThan(1), "having asked for room more than once");

                // Still frame 1: it was submitted above, and here it is let go and taken back.
                scene.geometry.ReleaseEverything();
                RunSameFrameUntil(scene, 1, () => retrying.IsOver, "the cut that waited ends in the same frame");
                Assert.That(retrying.Result.status, Is.EqualTo(VpStorageCutStatus.Ok), "and it succeeded");
                TestContext.WriteLine(
                    "frame 1 throughout; accepted in order: the short attempt, the holder, then the retry of the cut"
                    + " ahead of it in the order -- " + scene.geometry.Accepted.Count + " acceptances in all, the last"
                    + " of them in the same update the holder's room came back in");
            }
        }

        /// <summary>Waits until that work has finished running and is waiting only on the test to let it be collected.</summary>
        private static void WaitUntilFinishedButHeld(Scene scene, IDispatchWork work)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < DeadlineMilliseconds && !scene.geometry.FinishedButHeld(work))
            {
                System.Threading.Thread.Sleep(1);
            }

            Assert.That(scene.geometry.FinishedButHeld(work), Is.True, "the work finished and waits only on the test");
        }

        /// <summary>
        /// A work that really has not finished leaves the update without being waited for, and is collected at a later
        /// occasion of the **same frame** once it finishes. The work here is the test's own, offered to the dispatcher
        /// directly, and it reports itself unfinished until the test says otherwise -- so nothing is being held back
        /// from collection: there is genuinely nothing to collect.
        /// <para>
        /// Nothing of the product waits, sleeps or polls for it, and the test does not make an order out of how long
        /// anything takes: it says when the work is finished.
        /// </para>
        /// </summary>
        [Test]
        public void AWorkThatHasNotFinished_LeavesTheUpdateWithoutWaiting_AndIsCollectedLaterInTheSameFrame()
        {
            using (Scene scene = NewScene())
            {
                var slow = new UnfinishedWork();
                Assert.That(
                    scene.dispatcher.TryEnqueue(WorkPurpose.AdmittedGeometry, slow, out WorkTicket _), Is.True,
                    "the work is offered");

                SharedWorkFrameProgress first = scene.frame.Update(11);
                Assert.That(scene.geometry.Accepted, Does.Contain(slow), "the destination took it");
                Assert.That(slow.begun, Is.True, "and began it");
                Assert.That(slow.IsComplete, Is.False, "it has not finished");
                Assert.That(
                    first.collected, Is.Zero,
                    "so nothing was collected, and the update came back rather than waiting for it");
                Assert.That(slow.collected, Is.False);

                // It finishes. Nothing of the product was waiting on it.
                slow.finished = true;
                SharedWorkFrameProgress later = scene.frame.Update(11);
                Assert.That(
                    later.collected, Is.GreaterThan(0),
                    "a later occasion of the same frame took it back, once it really had finished");
                Assert.That(slow.collected, Is.True, "and it was collected");
            }
        }

        /// <summary>A piece of work that is finished when the test says so, and not before.</summary>
        private sealed class UnfinishedWork : IDispatchWork
        {
            internal bool begun;
            internal bool collected;
            internal bool finished;

            public void Begin()
            {
                begun = true;
            }

            public bool IsComplete => finished;

            public void Collect(WorkCompletion completion)
            {
                collected = true;
            }
        }

        /// <summary>
        /// The budget belongs to the frame. Updating the same frame again does not refill it, so the number of
        /// submissions and collections a frame allows is what it allows however often a caller comes back; a new frame
        /// id is what refills it, and what was left over is taken up then.
        /// <para>
        /// What a spent budget does to the loop is
        /// <see cref="AParticipantStillMoving_BuysNoFurtherOccasion_OnceTheBudgetIsSpent"/>: here nothing of the main
        /// thread is moving, so this case would pass either way and is not evidence about the end conditions.
        /// </para>
        /// </summary>
        [Test]
        public void TheBudgetIsTheFramesNotTheUpdates()
        {
            // One unit: one submission or one collection, for the whole frame.
            using (Scene scene = NewScene(frameBudget: 1))
            {
                VpStoredGeometry first = Append(scene.storage, Box());
                VpStoredGeometry second = Append(scene.storage, Box());
                scene.geometry.HoldEverything = true;
                VpStorageCutRequest a = scene.runner.Submit(Acquire(scene.storage, first), Tilted(), default);
                VpStorageCutRequest b = scene.runner.Submit(Acquire(scene.storage, second), Tilted(), default);

                scene.frame.Update(1);
                Assert.That(scene.geometry.Accepted.Count, Is.EqualTo(1), "one submission is all the budget allows");

                SharedWorkFrameProgress spent = scene.frame.Update(1);
                Assert.That(
                    scene.geometry.Accepted.Count, Is.EqualTo(1),
                    "and updating the same frame again does not buy another");
                Assert.That(scene.dispatcher.RemainingBudget, Is.Zero, "the frame's budget is spent");
                Assert.That(
                    spent.occasions, Is.EqualTo(1),
                    "an update with nothing left takes one occasion and comes back: " + spent.occasions);
                Assert.That(spent.submitted, Is.Zero, "submitting nothing");
                Assert.That(spent.collected, Is.Zero, "and collecting nothing");

                // A new frame refills it, and the one that was left over goes then.
                scene.frame.Update(2);
                Assert.That(
                    scene.geometry.Accepted.Count, Is.EqualTo(2),
                    "the next frame took up what the last one could not");
                Assert.That(a.Stage, Is.EqualTo(VpStorageCutStage.Cutting));
                Assert.That(b.Stage, Is.EqualTo(VpStorageCutStage.Cutting));

                scene.geometry.ReleaseEverything();
                RunFramesUntil(scene, 3, () => a.IsOver && b.IsOver, "both end, over the frames a budget of one allows");
            }
        }

        /// <summary>
        /// A participant that really is still moving buys **no further occasion** once the budget is spent. The
        /// participant here has a finite number of real steps to take, and reports each of them; with the budget gone,
        /// the update pumps it **exactly once** -- that one pump is what carries the last collection forward and gives
        /// back what it holds -- and comes back with its other steps untaken.
        /// <para>
        /// This is the case the budget's place among the end conditions is held by: take
        /// <c>RemainingBudget &lt;= 0</c> out of the loop and the update would go round for this participant's
        /// remaining steps, pumping it three times over three occasions, and the two counts below would fail.
        /// </para>
        /// </summary>
        [Test]
        public void AParticipantStillMoving_BuysNoFurtherOccasion_OnceTheBudgetIsSpent()
        {
            // One unit: one submission or one collection, for the whole frame.
            using (Scene scene = NewScene(frameBudget: 1))
            {
                VpStoredGeometry only = Append(scene.storage, Box());
                scene.geometry.HoldEverything = true;
                VpStorageCutRequest cut = scene.runner.Submit(Acquire(scene.storage, only), Tilted(), default);

                scene.frame.Update(1);
                Assert.That(scene.geometry.Accepted.Count, Is.EqualTo(1), "the one unit went on that submission");
                Assert.That(scene.dispatcher.RemainingBudget, Is.Zero, "and the frame's budget is spent");

                // Added now, so that what it reports is read only against a spent budget.
                var moving = new CountingPump(3);
                scene.frame.Add(moving);

                SharedWorkFrameProgress spent = scene.frame.Update(1);
                Assert.That(moving.calls, Is.EqualTo(1), "it was pumped once: " + moving.calls + " times");
                Assert.That(moving.stepsLeft, Is.EqualTo(2), "with steps of its own left untaken");
                Assert.That(spent.occasions, Is.EqualTo(1), "the update took one occasion: " + spent.occasions);
                Assert.That(spent.mainThreadSteps, Is.GreaterThan(0), "and it did count that step as movement");
                Assert.That(spent.submitted, Is.Zero, "nothing was submitted");
                Assert.That(spent.collected, Is.Zero, "and nothing collected");

                // Once there is budget again, the same participant is pumped as often as the frame goes round.
                scene.frame.Update(2);
                Assert.That(moving.calls, Is.GreaterThan(1), "a frame with budget pumps it again");

                scene.frame.Remove(moving);
                scene.geometry.ReleaseEverything();
                RunFramesUntil(scene, 3, () => cut.IsOver, "the cut ends over the frames a budget of one allows");
            }
        }

        /// <summary>
        /// A participant with a finite number of real steps to take: it reports movement while it has one left, and
        /// nothing after that. Finite, so an update that went round for it would end rather than never return.
        /// </summary>
        private sealed class CountingPump : IMainThreadPump
        {
            internal CountingPump(int steps)
            {
                stepsLeft = steps;
            }

            /// <summary>How many times this was pumped.</summary>
            internal int calls;

            /// <summary>How many real steps it still has to take.</summary>
            internal int stepsLeft;

            public bool Pump()
            {
                calls++;
                if (stepsLeft <= 0)
                {
                    return false;
                }

                stepsLeft--;
                return true;
            }
        }

        /// <summary>
        /// A destination with no room ends the update rather than blocking it: nothing waits, nothing is slept on, and
        /// the work that could not go is submitted at the next occasion once room appears -- in the same frame.
        /// </summary>
        [Test]
        public void ADestinationWithNoRoom_EndsTheUpdateAndIsTakenUpWhenRoomAppears()
        {
            using (Scene scene = NewScene(geometryCapacity: 1))
            {
                VpStoredGeometry first = Append(scene.storage, Box());
                VpStoredGeometry second = Append(scene.storage, Box());
                scene.geometry.HoldEverything = true;
                VpStorageCutRequest a = scene.runner.Submit(Acquire(scene.storage, first), Tilted(), default);
                VpStorageCutRequest b = scene.runner.Submit(Acquire(scene.storage, second), Tilted(), default);

                scene.frame.Update(4);
                Assert.That(scene.geometry.Accepted.Count, Is.EqualTo(1), "the one place is taken");
                Assert.That(b.Stage, Is.EqualTo(VpStorageCutStage.Cutting), "the other is with the dispatcher, waiting");
                Assert.That(
                    scene.dispatcher.RemainingBudget, Is.GreaterThan(0),
                    "and the update ended with budget to spare: it did not spin on a full destination");

                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DeadlineMilliseconds
                    && !scene.geometry.FinishedButHeld(scene.geometry.Accepted[0]))
                {
                    System.Threading.Thread.Sleep(1);
                }

                scene.geometry.Release(scene.geometry.Accepted[0]);
                scene.frame.Update(4);
                Assert.That(
                    scene.geometry.Accepted.Count, Is.EqualTo(2),
                    "once the place came free the other went, in the same frame");

                scene.geometry.ReleaseEverything();
                RunSameFrameUntil(scene, 4, () => a.IsOver && b.IsOver, "both end");
            }
        }

        /// <summary>An update cannot be started from inside one, which is what a collection calling back would do.</summary>
        [Test]
        public void AnUpdateFromInsideAnUpdate_IsRefused()
        {
            using (Scene scene = NewScene())
            {
                var reentrant = new ReentrantPump(scene.frame);
                scene.frame.Add(reentrant);
                Assert.That(
                    () => scene.frame.Update(1), Throws.InstanceOf<InvalidOperationException>(),
                    "re-entering the update is refused");
                Assert.That(reentrant.tried, Is.True, "and it really did try");
            }
        }

        private sealed class ReentrantPump : IMainThreadPump
        {
            private readonly SharedWorkFrame _frame;
            internal bool tried;

            internal ReentrantPump(SharedWorkFrame frame)
            {
                _frame = frame;
            }

            public bool Pump()
            {
                tried = true;
                _frame.Update(1);
                return false;
            }
        }
    }
}
