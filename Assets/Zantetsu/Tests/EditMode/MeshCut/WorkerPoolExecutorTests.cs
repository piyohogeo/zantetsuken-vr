using System;
using System.Collections.Generic;
using System.Threading;
using NUnit.Framework;
using ThreadPriority = System.Threading.ThreadPriority;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The two always-on pools of DESIGN 4.3, on their real threads: a worker waits when the queue is empty, runs
    /// what it is given without any further push from the main thread, brings a failure back instead of losing it,
    /// holds what has ended until the main thread takes it, and stops in the order a normal stop requires.
    /// <para>
    /// Nothing here is a timing assertion. Where a test waits, it waits for a condition and the timeout is only a
    /// safety net so a broken pool fails instead of hanging; no test asserts how fast anything happened, in what
    /// order two workers got their work, or that a particular worker got it. The priority read back here is the
    /// managed one — that the OS really lowered the thread is the Player harness's business, not this.
    /// </para>
    /// </summary>
    public class WorkerPoolExecutorTests
    {
        /// <summary>A safety net, not a threshold: a correct pool passes these long before it runs out.</summary>
        private const int Patience = 10000;

        private sealed class PoolWork : IDispatchWork
        {
            public PoolWork(string name)
            {
                this.name = name;
            }

            public readonly string name;
            public readonly ManualResetEventSlim begun = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim released = new ManualResetEventSlim(false);

            public int beginCount;
            public int collectCount;
            public int beganOnThread;
            public bool finishedWriting;
            public WorkCompletion lastCompletion;

            /// <summary>Makes Begin wait until the test lets it go, standing for work that is still running.</summary>
            public bool waitToBeReleased;

            /// <summary>Makes Begin wait until nothing is queued at the pool any more.</summary>
            public WorkerPoolExecutor waitForAnEmptyQueueAt;

            public bool throwOnBegin;

            public void Begin()
            {
                Interlocked.Increment(ref beginCount);
                beganOnThread = Thread.CurrentThread.ManagedThreadId;
                begun.Set();

                if (waitToBeReleased)
                {
                    released.Wait(Patience);
                }

                // Used to make a stop deterministic: this work does not finish while the pool still has a queue,
                // so the only thing that can empty it is the stop itself.
                if (waitForAnEmptyQueueAt != null)
                {
                    while (waitForAnEmptyQueueAt.QueuedCount > 0)
                    {
                        Thread.Sleep(1);
                    }
                }

                if (throwOnBegin)
                {
                    throw new InvalidOperationException("begin of " + name + " failed");
                }

                finishedWriting = true;
            }

            public bool IsComplete => true;

            public void Collect(WorkCompletion completion)
            {
                collectCount++;
                lastCompletion = completion;
            }

            public void Dispose()
            {
                begun.Dispose();
                released.Dispose();
            }
        }

        private static bool WaitFor(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(Patience);
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                {
                    return false;
                }

                Thread.Sleep(1);
            }

            return true;
        }

        private static List<KeyValuePair<IDispatchWork, WorkCompletion>> TakeAll(WorkerPoolExecutor pool)
        {
            var taken = new List<KeyValuePair<IDispatchWork, WorkCompletion>>();
            while (pool.TryTakeFinished(out IDispatchWork work, out WorkCompletion completion))
            {
                taken.Add(new KeyValuePair<IDispatchWork, WorkCompletion>(work, completion));
            }

            return taken;
        }

        /// <summary>
        /// Work handed to a pool runs on a worker, one after another, with nothing pushing it from the main thread
        /// after the hand-over. The pool was empty and waiting when the work arrived.
        /// </summary>
        [Test]
        public void WorkHandedToAPool_RunsWithoutAnyFurtherPushFromTheMainThread()
        {
            var pool = new WorkerPoolExecutor(WorkDestination.GeometryPool, 2, ThreadPriority.BelowNormal, 8);
            var works = new List<PoolWork>();
            try
            {
                Assert.That(pool.WaitUntilStartupFinished(Patience), Is.True, "the workers started");
                Assert.That(pool.RunningCount, Is.Zero, "and are waiting, not spinning through an empty queue");

                for (int i = 0; i < 4; i++)
                {
                    var work = new PoolWork("w" + i);
                    works.Add(work);
                    Assert.That(pool.CanAccept, Is.True);
                    Assert.That(pool.TryAccept(work), Is.True, "work is accepted");
                }

                // The main thread does nothing at all from here until every one of them has run.
                Assert.That(WaitFor(() => pool.EndedNotTakenCount == 4), Is.True, "all four ran");

                foreach (PoolWork work in works)
                {
                    Assert.That(work.beginCount, Is.EqualTo(1), work.name + " ran once");
                    Assert.That(work.finishedWriting, Is.True, work.name + " finished its writes");
                    Assert.That(
                        work.beganOnThread, Is.Not.EqualTo(Thread.CurrentThread.ManagedThreadId),
                        work.name + " ran off the main thread");
                }

                List<KeyValuePair<IDispatchWork, WorkCompletion>> taken = TakeAll(pool);
                Assert.That(taken.Count, Is.EqualTo(4));
                foreach (KeyValuePair<IDispatchWork, WorkCompletion> entry in taken)
                {
                    Assert.That(entry.Value.outcome, Is.EqualTo(WorkOutcome.Finished));
                }

                Assert.That(pool.Held, Is.Zero, "and the pool holds nothing once they are taken");
                Assert.That(pool.StopAndConfirm(Patience), Is.True);
            }
            finally
            {
                pool.Dispose();
                foreach (PoolWork work in works)
                {
                    work.Dispose();
                }
            }
        }

        /// <summary>Whatever a first call needs prepared is prepared on the calling thread, before a worker exists.</summary>
        [Test]
        public void ThePoolPreparesWhatItHasTo_OnTheCallingThread_BeforeAnyWorkerExists()
        {
            int preparedOnThread = 0;
            int preparations = 0;
            WorkerPoolExecutor pool = null;
            try
            {
                pool = new WorkerPoolExecutor(
                    WorkDestination.BackgroundPool,
                    2,
                    ThreadPriority.Lowest,
                    4,
                    () =>
                    {
                        preparations++;
                        preparedOnThread = Thread.CurrentThread.ManagedThreadId;
                    });

                Assert.That(preparations, Is.EqualTo(1), "prepared once");
                Assert.That(
                    preparedOnThread, Is.EqualTo(Thread.CurrentThread.ManagedThreadId),
                    "on the thread that built the pool, which is the main thread");
                Assert.That(pool.WaitUntilStartupFinished(Patience), Is.True);
                foreach (WorkerPoolExecutor.WorkerObservation worker in pool.Workers)
                {
                    Assert.That(
                        worker.ManagedThreadId, Is.Not.EqualTo(preparedOnThread),
                        "no worker was the one that prepared it");
                }
            }
            finally
            {
                pool?.Dispose();
            }
        }

        /// <summary>The always-on shapes of DESIGN 4.3: G = 8 at BelowNormal, B = 2 at Lowest, provisionally.</summary>
        [Test]
        public void TheTwoPools_StartTheWorkersTheyWereSpecifiedWith()
        {
            WorkerPoolExecutor geometry = WorkerPoolExecutor.GeometryPool(4);
            WorkerPoolExecutor background = WorkerPoolExecutor.BackgroundPool(4);
            try
            {
                Assert.That(geometry.Destination, Is.EqualTo(WorkDestination.GeometryPool));
                Assert.That(geometry.Workers.Count, Is.EqualTo(WorkerPoolExecutor.DefaultGeometryWorkerCount));
                Assert.That(background.Destination, Is.EqualTo(WorkDestination.BackgroundPool));
                Assert.That(background.Workers.Count, Is.EqualTo(WorkerPoolExecutor.DefaultBackgroundWorkerCount));

                Assert.That(geometry.WaitUntilStartupFinished(Patience), Is.True);
                Assert.That(background.WaitUntilStartupFinished(Patience), Is.True);

                foreach (WorkerPoolExecutor.WorkerObservation worker in geometry.Workers)
                {
                    Assert.That(worker.requestedPriority, Is.EqualTo(ThreadPriority.BelowNormal));
                    Assert.That(worker.ObservedPriority, Is.EqualTo(ThreadPriority.BelowNormal), "read back on the worker");
                    Assert.That(worker.FatalFailure, Is.Null);
                }

                foreach (WorkerPoolExecutor.WorkerObservation worker in background.Workers)
                {
                    Assert.That(worker.requestedPriority, Is.EqualTo(ThreadPriority.Lowest));
                    Assert.That(worker.ObservedPriority, Is.EqualTo(ThreadPriority.Lowest), "read back on the worker");
                    Assert.That(worker.FatalFailure, Is.Null);
                }

                Assert.That(
                    () => new WorkerPoolExecutor(WorkDestination.UnityJob, 1, ThreadPriority.Lowest, 1),
                    Throws.TypeOf<ArgumentOutOfRangeException>(),
                    "the Unity Job destination is not a pool");
                Assert.That(
                    () => new WorkerPoolExecutor(WorkDestination.GeometryPool, 0, ThreadPriority.Lowest, 1),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
                Assert.That(
                    () => new WorkerPoolExecutor(WorkDestination.GeometryPool, 1, ThreadPriority.Lowest, 0),
                    Throws.TypeOf<ArgumentOutOfRangeException>());
            }
            finally
            {
                geometry.Dispose();
                background.Dispose();
            }
        }

        /// <summary>
        /// The capacity is a bound on everything the pool holds, ended work included: a main thread that stops
        /// collecting cannot make it grow, and the pool simply takes nothing more until it does collect.
        /// </summary>
        [Test]
        public void AFullPool_TakesNothingMore_UntilTheMainThreadCollects()
        {
            var pool = new WorkerPoolExecutor(WorkDestination.GeometryPool, 1, ThreadPriority.BelowNormal, 2);
            var first = new PoolWork("first");
            var second = new PoolWork("second");
            var refused = new PoolWork("refused");
            try
            {
                Assert.That(pool.TryAccept(first), Is.True, "first is accepted");
                Assert.That(pool.TryAccept(second), Is.True, "second is accepted");

                Assert.That(pool.Held, Is.EqualTo(2), "two is the bound");
                Assert.That(pool.CanAccept, Is.False, "so nothing more is taken");
                Assert.That(pool.TryAccept(refused), Is.False, "accepting is refused, and nothing happens");
                Assert.That(refused.beginCount, Is.Zero, "and the refused work is untouched");

                Assert.That(WaitFor(() => pool.EndedNotTakenCount == 2), Is.True, "both ran");
                Assert.That(pool.Held, Is.EqualTo(2), "ended work still occupies its room");
                Assert.That(pool.CanAccept, Is.False, "which is what keeps the bound a bound");

                Assert.That(pool.TryTakeFinished(out _, out _), Is.True);
                Assert.That(pool.Held, Is.EqualTo(1));
                Assert.That(pool.CanAccept, Is.True, "collecting is what frees room");
                Assert.That(pool.TryAccept(refused), Is.True, "refused is accepted");
                Assert.That(WaitFor(() => refused.beginCount == 1), Is.True);

                Assert.That(pool.StopAndConfirm(Patience), Is.True);
            }
            finally
            {
                pool.Dispose();
                first.Dispose();
                second.Dispose();
                refused.Dispose();
            }
        }

        /// <summary>A work that throws comes back as a failure, once, and its worker goes on to the next one.</summary>
        [Test]
        public void AWorkThatThrows_ComesBackAsAFailure_AndTheWorkerGoesOn()
        {
            var pool = new WorkerPoolExecutor(WorkDestination.BackgroundPool, 1, ThreadPriority.Lowest, 4);
            var failing = new PoolWork("failing") { throwOnBegin = true };
            var after = new PoolWork("after");
            try
            {
                Assert.That(pool.TryAccept(failing), Is.True, "failing is accepted");
                Assert.That(pool.TryAccept(after), Is.True, "after is accepted");
                Assert.That(WaitFor(() => pool.EndedNotTakenCount == 2), Is.True, "both were handled");

                var outcomes = new Dictionary<IDispatchWork, WorkCompletion>();
                foreach (KeyValuePair<IDispatchWork, WorkCompletion> entry in TakeAll(pool))
                {
                    outcomes.Add(entry.Key, entry.Value);
                }

                Assert.That(outcomes[failing].outcome, Is.EqualTo(WorkOutcome.Failed), "the failure was not lost");
                Assert.That(outcomes[failing].failure, Is.Not.Null);
                Assert.That(outcomes[failing].failure.Message, Does.Contain("failing"));
                Assert.That(failing.beginCount, Is.EqualTo(1), "and nothing re-ran it");

                Assert.That(outcomes[after].outcome, Is.EqualTo(WorkOutcome.Finished), "the worker carried on");
                Assert.That(after.beginCount, Is.EqualTo(1));

                Assert.That(pool.Workers[0].FatalFailure, Is.Null, "the worker itself did not die");
                Assert.That(pool.Workers[0].LastWorkFailure, Is.Not.Null);
                Assert.That(pool.Workers[0].Executed, Is.EqualTo(2));
                Assert.That(pool.StopAndConfirm(Patience), Is.True);
            }
            finally
            {
                pool.Dispose();
                failing.Dispose();
                after.Dispose();
            }
        }

        /// <summary>
        /// A normal stop: new work is refused, what has not begun is cancelled exactly once, work that is running
        /// finishes with what it holds, and every worker is confirmed stopped before the stop returns.
        /// </summary>
        [Test]
        public void ANormalStop_CancelsWhatHasNotBegun_LetsRunningWorkFinish_AndStopsEveryWorker()
        {
            var pool = new WorkerPoolExecutor(WorkDestination.GeometryPool, 1, ThreadPriority.BelowNormal, 8);
            var running = new PoolWork("running");
            var queuedFirst = new PoolWork("queued-first");
            var queuedSecond = new PoolWork("queued-second");
            var afterTheStop = new PoolWork("after-the-stop");
            try
            {
                // The one worker takes this and then holds still until the test lets it go, and even then will not
                // finish while anything is queued. So what the stop finds in the queue is exactly what was put
                // there, and the only thing that can empty it is the stop itself: no race, and no clock.
                running.waitToBeReleased = true;
                running.waitForAnEmptyQueueAt = pool;
                Assert.That(pool.TryAccept(running), Is.True, "running is accepted");
                Assert.That(running.begun.Wait(Patience), Is.True, "the first work is running");

                Assert.That(pool.TryAccept(queuedFirst), Is.True, "queuedFirst is accepted");
                Assert.That(pool.TryAccept(queuedSecond), Is.True, "queuedSecond is accepted");
                Assert.That(pool.QueuedCount, Is.EqualTo(2), "and two are queued behind it");
                running.released.Set();

                pool.CloseForNewWork();
                Assert.That(pool.CanAccept, Is.False, "closed for new work");
                Assert.That(pool.TryAccept(afterTheStop), Is.False, "accepting is refused, and nothing happens");

                Assert.That(pool.StopAndConfirm(Patience), Is.True, "every worker stopped");
                Assert.That(pool.WorkersStopped, Is.True);
                Assert.That(
                    running.finishedWriting, Is.True,
                    "and the work that was running had finished with what it held before that was confirmed");

                var outcomes = new Dictionary<IDispatchWork, WorkCompletion>();
                foreach (KeyValuePair<IDispatchWork, WorkCompletion> entry in TakeAll(pool))
                {
                    outcomes.Add(entry.Key, entry.Value);
                }

                Assert.That(outcomes.Count, Is.EqualTo(3), "all three came back, exactly once each");
                Assert.That(outcomes[running].outcome, Is.EqualTo(WorkOutcome.Finished));
                Assert.That(outcomes[queuedFirst].outcome, Is.EqualTo(WorkOutcome.Cancelled));
                Assert.That(outcomes[queuedSecond].outcome, Is.EqualTo(WorkOutcome.Cancelled));
                Assert.That(queuedFirst.beginCount, Is.Zero, "what was cancelled never began");
                Assert.That(queuedSecond.beginCount, Is.Zero);
                Assert.That(running.beginCount, Is.EqualTo(1), "and what ran, ran once");
                Assert.That(afterTheStop.beginCount, Is.Zero);

                Assert.That(pool.Held, Is.Zero, "nothing is left held");
                Assert.That(pool.TryTakeFinished(out _, out _), Is.False, "and nothing comes back twice");
            }
            finally
            {
                pool.Dispose();
                running.Dispose();
                queuedFirst.Dispose();
                queuedSecond.Dispose();
                afterTheStop.Dispose();
            }
        }

        /// <summary>
        /// A pool that cannot start every worker is never handed out: the workers that did start are stopped and
        /// joined first, and then the failure leaves. Nothing restarts anything.
        /// </summary>
        [Test]
        public void APoolThatCannotStartEveryWorker_StopsTheOnesThatStarted_AndFails()
        {
            var started = new List<Thread>();
            Assert.That(
                () => new WorkerPoolExecutor(
                    WorkDestination.GeometryPool,
                    3,
                    ThreadPriority.BelowNormal,
                    4,
                    null,
                    thread =>
                    {
                        if (started.Count == 2)
                        {
                            throw new InvalidOperationException("no more threads");
                        }

                        started.Add(thread);
                        thread.Start();
                    },
                    null),
                Throws.InvalidOperationException,
                "the construction fails rather than handing out a half-started pool");

            Assert.That(started.Count, Is.EqualTo(2), "two workers had started");
            foreach (Thread thread in started)
            {
                Assert.That(thread.Join(Patience), Is.True, thread.Name + " was stopped and joined");
                Assert.That(thread.IsAlive, Is.False, thread.Name + " is gone");
            }
        }

        /// <summary>
        /// A worker loop that ends the way it never should faults the pool: the main thread can see it, and no new
        /// work is accepted afterwards, so nothing can pile up behind a worker that is gone. Finishing a startup
        /// attempt is reported once per worker and is not the same as running normally.
        /// </summary>
        [Test]
        public void AWorkerThatCannotStart_FaultsThePool_AndItAcceptsNothingMore()
        {
            var pool = new WorkerPoolExecutor(
                WorkDestination.BackgroundPool,
                2,
                ThreadPriority.Lowest,
                4,
                null,
                null,
                () => throw new InvalidOperationException("this worker cannot start"));
            var refused = new PoolWork("refused");
            try
            {
                Assert.That(
                    pool.WaitUntilStartupFinished(Patience), Is.True,
                    "both workers finished their startup attempt");
                Assert.That(pool.StartupFinishedCount, Is.EqualTo(2), "each of them reported that exactly once");
                Assert.That(pool.IsFaulted, Is.True, "but finishing the attempt is not running normally");
                Assert.That(pool.FaultFailure, Is.Not.Null);

                foreach (WorkerPoolExecutor.WorkerObservation worker in pool.Workers)
                {
                    Assert.That(worker.StartupFinished, Is.True, worker.name);
                    Assert.That(worker.StartupSucceeded, Is.False, worker.name + " did not get through it");
                    Assert.That(worker.FatalFailure, Is.Not.Null, worker.name);
                    Assert.That(worker.Executed, Is.Zero, worker.name + " ran nothing");
                }

                Assert.That(pool.CanAccept, Is.False, "a faulted pool takes no new work");
                Assert.That(pool.TryAccept(refused), Is.False, "accepting is refused, and nothing happens");
                Assert.That(refused.beginCount, Is.Zero, "and the refused work is untouched");

                Assert.That(pool.StopAndConfirm(Patience), Is.True, "it still stops, and says so");
                Assert.That(pool.SynchronisationReleased, Is.True);
            }
            finally
            {
                pool.Dispose();
                refused.Dispose();
            }
        }

        /// <summary>
        /// Disposing a pool whose stop was not confirmed keeps the pool's own synchronisation, and says so; a later
        /// confirmed stop is what releases it. Giving the stop more time is not what makes this safe.
        /// </summary>
        [Test]
        public void DisposeWithoutAConfirmedStop_KeepsTheSynchronisation_UntilAConfirmedStopReleasesIt()
        {
            var pool = new WorkerPoolExecutor(WorkDestination.GeometryPool, 1, ThreadPriority.BelowNormal, 4);
            pool.disposeStopTimeoutMilliseconds = 50;
            var stuck = new PoolWork("stuck") { waitToBeReleased = true };
            try
            {
                Assert.That(pool.TryAccept(stuck), Is.True, "stuck is accepted");
                Assert.That(stuck.begun.Wait(Patience), Is.True, "the work is running and will not finish yet");
                Assert.That(pool.WaitUntilStartupFinished(Patience), Is.True);

                pool.Dispose();

                Assert.That(pool.WorkersStopped, Is.False, "the stop could not be confirmed");
                Assert.That(pool.SynchronisationReleased, Is.False, "so nothing of the pool's own was released");
                Assert.That(pool.StartupFinishedCount, Is.EqualTo(1), "and what the worker reported still stands");

                stuck.released.Set();

                Assert.That(pool.StopAndConfirm(Patience), Is.True, "confirming the stop afterwards works");
                Assert.That(pool.WorkersStopped, Is.True);
                Assert.That(pool.SynchronisationReleased, Is.True, "and only then is it released");

                List<KeyValuePair<IDispatchWork, WorkCompletion>> taken = TakeAll(pool);
                Assert.That(taken.Count, Is.EqualTo(1), "the work that ran comes back, once");
                Assert.That(taken[0].Value.outcome, Is.EqualTo(WorkOutcome.Finished));
                Assert.That(stuck.beginCount, Is.EqualTo(1));
            }
            finally
            {
                pool.Dispose();
                stuck.Dispose();
            }
        }

        /// <summary>
        /// The whole dispatch over two real pools and the urgent destination: work goes where its purpose says, runs
        /// on real workers, and every piece of it is collected exactly once by the main thread.
        /// </summary>
        [Test]
        public void TheDispatch_OverRealPools_CollectsEveryWorkExactlyOnce()
        {
            var job = new UnityJobWorkExecutor(4);
            WorkerPoolExecutor geometry = WorkerPoolExecutor.GeometryPool(4);
            WorkerPoolExecutor background = WorkerPoolExecutor.BackgroundPool(4);
            var dispatcher = new SharedWorkDispatcher(8, 2, 64, job, geometry, background);
            var works = new List<PoolWork>();
            try
            {
                dispatcher.BeginFrame(1);
                var purposes = new[]
                {
                    WorkPurpose.AdmittedGeometry,
                    WorkPurpose.AdmittedGeometry,
                    WorkPurpose.Speculative,
                    WorkPurpose.Maintenance,
                };
                foreach (WorkPurpose purpose in purposes)
                {
                    var work = new PoolWork(purpose.ToString());
                    works.Add(work);
                    Assert.That(dispatcher.TryEnqueue(purpose, work, out _), Is.True);
                }

                Assert.That(dispatcher.Dispatch().submitted, Is.EqualTo(4));
                Assert.That(
                    WaitFor(() => geometry.EndedNotTakenCount == 2 && background.EndedNotTakenCount == 2), Is.True,
                    "all four ran at the pool their purpose named");

                dispatcher.BeginFrame(2);
                Assert.That(dispatcher.Dispatch().collected, Is.EqualTo(4));
                foreach (PoolWork work in works)
                {
                    Assert.That(work.beginCount, Is.EqualTo(1), work.name);
                    Assert.That(work.collectCount, Is.EqualTo(1), work.name);
                    Assert.That(work.lastCompletion.outcome, Is.EqualTo(WorkOutcome.Finished), work.name);
                }

                DispatchShutdownResult stop = dispatcher.Shutdown(Patience);
                Assert.That(stop.workersStopped, Is.True);
                Assert.That(stop.cancelled, Is.Zero);
                Assert.That(stop.collected, Is.Zero, "there was nothing left to collect");
                Assert.That(geometry.WorkersStopped, Is.True);
                Assert.That(background.WorkersStopped, Is.True);
            }
            finally
            {
                geometry.Dispose();
                background.Dispose();
                foreach (PoolWork work in works)
                {
                    work.Dispose();
                }
            }
        }
    }
}
