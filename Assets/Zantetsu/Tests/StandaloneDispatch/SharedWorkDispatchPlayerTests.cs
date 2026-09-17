using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using ThreadPriority = System.Threading.ThreadPriority;

namespace Zantetsu.MeshCut.StandaloneTests
{
    /// <summary>
    /// The synthetic numeric kernel the player harness sends to the pools: a Burst direct call, prepared once on the
    /// main thread and then called from a pool worker.
    /// <para>
    /// <c>ranAsBurst</c> is how the harness knows it really was Burst code and not the managed fallback: the marker
    /// is set to 1, and then a <see cref="BurstDiscardAttribute"/> method that would set it back to 0 is called. That
    /// method exists only in managed code — Burst removes it — so a 1 coming out means the Burst version ran.
    /// </para>
    /// </summary>
    [BurstCompile]
    internal static unsafe class SyntheticNumericKernel
    {
        /// <summary>
        /// <paramref name="output"/> becomes the sum of <paramref name="input"/> scaled, computed in floats that are
        /// exact for the harness's inputs so that the result can be compared without a tolerance.
        /// </summary>
        [BurstCompile]
        internal static void Accumulate(float* input, int count, float scale, float* output, int* ranAsBurst)
        {
            *ranAsBurst = 1;
            MarkManaged(ranAsBurst);

            float sum = 0f;
            for (int i = 0; i < count; i++)
            {
                sum += input[i] * scale;
            }

            *output = sum;
        }

        [BurstDiscard]
        private static void MarkManaged(int* ranAsBurst)
        {
            *ranAsBurst = 0;
        }
    }

    /// <summary>
    /// The shared dispatch of DESIGN 4.3 and 4.4 in an IL2CPP player, on the parts a test double cannot stand in
    /// for: real always-on threads at the priority they were configured with, Burst numeric work prepared on the
    /// main thread and called from a worker, one shared immutable input with an independent output per work, work
    /// waiting in a pool queue, ended work held until the main thread takes it, and the normal stop.
    /// <para>
    /// Non-XR: nothing here starts or needs a headset, and nothing here touches a scene object. Nothing here is a
    /// timing assertion either — where it waits it waits for a condition, with a timeout only so that a broken pool
    /// fails instead of hanging, and no OS scheduling order, fixed delay or speed-up is a pass condition. The
    /// synthetic kernel stands in for the real cut and bake work, which this does not touch.
    /// </para>
    /// </summary>
    public unsafe class SharedWorkDispatchPlayerTests
    {
        private const int Patience = 20000;
        private const int SampleCount = 2048;
        private const float Scale = 2f;

        /// <summary>Windows THREAD_PRIORITY_BELOW_NORMAL and THREAD_PRIORITY_LOWEST.</summary>
        private const int OsBelowNormal = -1;
        private const int OsLowest = -2;

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        private static extern int GetThreadPriority(IntPtr thread);

        /// <summary>
        /// The sum the kernel must produce: the samples are 1..SampleCount, so this stays inside the range a float
        /// represents exactly and needs no tolerance.
        /// </summary>
        private static float ExpectedSum => Scale * (SampleCount * (SampleCount + 1L) / 2L);

        private struct Block : IDisposable
        {
            public void* pointer;
            public int bytes;

            public static Block Alloc(int bytes)
            {
                return new Block
                {
                    pointer = UnsafeUtility.Malloc(bytes, 16, Allocator.Persistent),
                    bytes = bytes,
                };
            }

            public void Dispose()
            {
                if (pointer != null)
                {
                    UnsafeUtility.Free(pointer, Allocator.Persistent);
                    pointer = null;
                }
            }
        }

        /// <summary>
        /// One piece of numeric work: it reads the **one shared** input that nothing writes while it runs, and writes
        /// its **own** output and marker, which no other work touches.
        /// </summary>
        private sealed class NumericWork : IDispatchWork, IDisposable
        {
            private readonly float* _sharedInput;
            private readonly int _count;
            private Block _output;
            private Block _marker;

            public NumericWork(string name, float* sharedInput, int count)
            {
                this.name = name;
                _sharedInput = sharedInput;
                _count = count;
                _output = Block.Alloc(sizeof(float));
                _marker = Block.Alloc(sizeof(int));
                *(float*)_output.pointer = float.NaN;
                *(int*)_marker.pointer = -1;
            }

            public readonly string name;
            public readonly ManualResetEventSlim begun = new ManualResetEventSlim(false);

            public int beginCount;
            public int collectCount;
            public int beganOnThread;
            public int osPriority = int.MinValue;
            public bool ranAsBurst;
            public bool finishedWriting;
            public WorkCompletion lastCompletion;

            /// <summary>Makes the work hold on until the test lets it go, so that a queue behind it is certain.</summary>
            public bool waitToBeReleased;

            /// <summary>Then makes it hold on until nothing is queued at the pool, which only a stop achieves.</summary>
            public WorkerPoolExecutor holdUntilTheQueueIsEmptyAt;

            public readonly ManualResetEventSlim released = new ManualResetEventSlim(false);

            /// <summary>The output as the main thread reads it, after the work was collected.</summary>
            public float Result => *(float*)_output.pointer;

            public void Begin()
            {
                Interlocked.Increment(ref beginCount);
                beganOnThread = Thread.CurrentThread.ManagedThreadId;
                osPriority = GetThreadPriority(GetCurrentThread());
                begun.Set();

                if (waitToBeReleased)
                {
                    released.Wait(Patience);
                }

                if (holdUntilTheQueueIsEmptyAt != null)
                {
                    while (holdUntilTheQueueIsEmptyAt.QueuedCount > 0)
                    {
                        Thread.Sleep(1);
                    }
                }

                // The Burst direct call, from a pool worker. Its first call was made on the main thread.
                SyntheticNumericKernel.Accumulate(
                    _sharedInput, _count, Scale, (float*)_output.pointer, (int*)_marker.pointer);

                ranAsBurst = *(int*)_marker.pointer == 1;
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
                _output.Dispose();
                _marker.Dispose();
                begun.Dispose();
                released.Dispose();
            }
        }

        private struct AddOneJob : IJob
        {
            public NativeReference<int> result;

            public void Execute()
            {
                result.Value += 1;
            }
        }

        /// <summary>A real Unity job, for the urgent destination.</summary>
        private sealed class JobWork : IDispatchWork, IDisposable
        {
            private NativeReference<int> _result;
            private JobHandle _handle;
            private bool _begun;

            public JobWork()
            {
                _result = new NativeReference<int>(Allocator.Persistent);
            }

            public int collectCount;
            public int collectedValue;

            public void Begin()
            {
                _handle = new AddOneJob { result = _result }.Schedule();
                _begun = true;
                JobHandle.ScheduleBatchedJobs();
            }

            public bool IsComplete => _begun && _handle.IsCompleted;

            public void Collect(WorkCompletion completion)
            {
                _handle.Complete();
                collectedValue = _result.Value;
                collectCount++;
            }

            public void Dispose()
            {
                if (_begun)
                {
                    _handle.Complete();
                }

                if (_result.IsCreated)
                {
                    _result.Dispose();
                }
            }
        }

        private Block _input;
        private int _warmUps;
        private int _warmUpThread;
        private bool _warmUpRanAsBurst;

        [SetUp]
        public void AllocateTheSharedInput()
        {
            _input = Block.Alloc(SampleCount * sizeof(float));
            float* samples = (float*)_input.pointer;
            for (int i = 0; i < SampleCount; i++)
            {
                samples[i] = i + 1;
            }

            _warmUps = 0;
            _warmUpThread = 0;
            _warmUpRanAsBurst = false;
        }

        [TearDown]
        public void ReleaseTheSharedInput()
        {
            _input.Dispose();
        }

        /// <summary>
        /// The first call of the Burst kernel, made on the main thread, which is what a pool is given as its
        /// preparation. Everything a worker calls later has been called here once already.
        /// </summary>
        private void PrepareOnMain()
        {
            _warmUps++;
            _warmUpThread = Thread.CurrentThread.ManagedThreadId;

            Block output = Block.Alloc(sizeof(float));
            Block marker = Block.Alloc(sizeof(int));
            try
            {
                SyntheticNumericKernel.Accumulate(
                    (float*)_input.pointer, SampleCount, Scale, (float*)output.pointer, (int*)marker.pointer);
                _warmUpRanAsBurst = *(int*)marker.pointer == 1;
                Assert.That(*(float*)output.pointer, Is.EqualTo(ExpectedSum), "the kernel is right on the main thread");
            }
            finally
            {
                output.Dispose();
                marker.Dispose();
            }
        }

        private static bool WaitFor(Func<bool> condition)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Patience);
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

        private void AssertRanOffMainAt(NumericWork work, int expectedOsPriority, string pool)
        {
            Assert.That(work.beginCount, Is.EqualTo(1), work.name + " ran once");
            Assert.That(work.finishedWriting, Is.True, work.name + " finished its writes");
            Assert.That(
                work.beganOnThread, Is.Not.EqualTo(Thread.CurrentThread.ManagedThreadId),
                work.name + " ran off the main thread");
            Assert.That(work.ranAsBurst, Is.True, work.name + " ran as Burst code, not the managed fallback");
            Assert.That(work.Result, Is.EqualTo(ExpectedSum), work.name + " produced its own correct result");
            Assert.That(
                work.osPriority, Is.EqualTo(expectedOsPriority),
                work.name + " ran on a " + pool + " thread at the priority it was configured with, as the OS reports it");
        }

        /// <summary>
        /// Both pools run Burst numeric work on their own always-on threads, at the OS priority they were configured
        /// with, over one shared immutable input, each work writing only its own output.
        /// </summary>
        [Test]
        public void TheTwoPools_RunBurstNumericWork_AtTheirConfiguredPriorities_OnSharedInputWithIndependentOutputs()
        {
            WorkerPoolExecutor geometry = WorkerPoolExecutor.GeometryPool(16, PrepareOnMain);
            WorkerPoolExecutor background = WorkerPoolExecutor.BackgroundPool(16, PrepareOnMain);
            var works = new List<NumericWork>();
            try
            {
                Assert.That(_warmUps, Is.EqualTo(2), "each pool prepared the first call before starting its workers");
                Assert.That(
                    _warmUpThread, Is.EqualTo(Thread.CurrentThread.ManagedThreadId),
                    "and did it on the main thread");
                Assert.That(_warmUpRanAsBurst, Is.True, "the main thread's own first call was Burst code");

                Assert.That(geometry.WaitUntilStartupFinished(Patience), Is.True, "the geometry workers started");
                Assert.That(background.WaitUntilStartupFinished(Patience), Is.True, "the background workers started");
                Assert.That(
                    geometry.Workers.Count, Is.EqualTo(WorkerPoolExecutor.DefaultGeometryWorkerCount),
                    "the always-on geometry pool");
                Assert.That(
                    background.Workers.Count, Is.EqualTo(WorkerPoolExecutor.DefaultBackgroundWorkerCount),
                    "and the always-on background pool");

                var geometryWorks = new List<NumericWork>();
                var backgroundWorks = new List<NumericWork>();
                for (int i = 0; i < 12; i++)
                {
                    var onGeometry = new NumericWork("geometry-" + i, (float*)_input.pointer, SampleCount);
                    var onBackground = new NumericWork("background-" + i, (float*)_input.pointer, SampleCount);
                    works.Add(onGeometry);
                    works.Add(onBackground);
                    geometryWorks.Add(onGeometry);
                    backgroundWorks.Add(onBackground);
                    Assert.That(geometry.TryAccept(onGeometry), Is.True, "onGeometry is accepted");
                    Assert.That(background.TryAccept(onBackground), Is.True, "onBackground is accepted");
                }

                Assert.That(
                    WaitFor(() => geometry.EndedNotTakenCount == 12 && background.EndedNotTakenCount == 12), Is.True,
                    "every work ran at the pool it was given to");

                foreach (NumericWork work in geometryWorks)
                {
                    AssertRanOffMainAt(work, OsBelowNormal, "geometry");
                }

                foreach (NumericWork work in backgroundWorks)
                {
                    AssertRanOffMainAt(work, OsLowest, "background");
                }

                // The one input every one of them read is exactly what it was.
                float* samples = (float*)_input.pointer;
                for (int i = 0; i < SampleCount; i++)
                {
                    Assert.That(samples[i], Is.EqualTo((float)(i + 1)), "the shared input was not written by anyone");
                }

                Assert.That(geometry.StopAndConfirm(Patience), Is.True);
                Assert.That(background.StopAndConfirm(Patience), Is.True);
            }
            finally
            {
                geometry.Dispose();
                background.Dispose();
                foreach (NumericWork work in works)
                {
                    work.Dispose();
                }
            }
        }

        /// <summary>
        /// Work waits in the pool's queue when the workers are busy, and work that has ended is held — output and
        /// all — until the main thread takes it, which is what the capacity bounds.
        /// </summary>
        [Test]
        public void WorkWaitsInThePoolQueue_AndEndedWorkIsHeldUntilTheMainThreadTakesIt()
        {
            var pool = new WorkerPoolExecutor(
                WorkDestination.GeometryPool, 1, ThreadPriority.BelowNormal, 3, PrepareOnMain);
            var first = new NumericWork("first", (float*)_input.pointer, SampleCount);
            var second = new NumericWork("second", (float*)_input.pointer, SampleCount);
            var third = new NumericWork("third", (float*)_input.pointer, SampleCount);
            try
            {
                // The only worker holds this one until the test lets it go, so what is behind it really is waiting
                // in the queue while that is being checked.
                first.waitToBeReleased = true;
                Assert.That(pool.TryAccept(first), Is.True, "first is accepted");
                Assert.That(first.begun.Wait(Patience), Is.True, "the first work is running");

                Assert.That(pool.TryAccept(second), Is.True, "second is accepted");
                Assert.That(pool.TryAccept(third), Is.True, "third is accepted");
                Assert.That(pool.QueuedCount, Is.EqualTo(2), "two works are waiting in the pool's queue");
                Assert.That(pool.Held, Is.EqualTo(3), "and the pool is at its bound");
                Assert.That(pool.CanAccept, Is.False, "so it takes nothing more");
                Assert.That(second.beginCount, Is.Zero, "neither of them has begun");
                Assert.That(third.beginCount, Is.Zero);

                first.released.Set();
                Assert.That(WaitFor(() => pool.EndedNotTakenCount == 3), Is.True, "all three ran, one after another");
                Assert.That(pool.Held, Is.EqualTo(3), "and ended work still occupies the room it was given");
                Assert.That(pool.CanAccept, Is.False, "a main thread that does not collect cannot grow this pool");

                foreach (NumericWork work in new[] { first, second, third })
                {
                    Assert.That(work.ranAsBurst, Is.True, work.name);
                    Assert.That(work.Result, Is.EqualTo(ExpectedSum), work.name + " kept its result while it waited");
                }

                int taken = 0;
                while (pool.TryTakeFinished(out IDispatchWork work, out WorkCompletion completion))
                {
                    taken++;
                    Assert.That(completion.outcome, Is.EqualTo(WorkOutcome.Finished));
                    work.Collect(completion);
                }

                Assert.That(taken, Is.EqualTo(3));
                Assert.That(pool.Held, Is.Zero, "collecting is what frees the room");
                Assert.That(pool.CanAccept, Is.True);
                Assert.That(pool.StopAndConfirm(Patience), Is.True);
            }
            finally
            {
                pool.Dispose();
                first.Dispose();
                second.Dispose();
                third.Dispose();
            }
        }

        /// <summary>
        /// Each purpose reaches its own destination in the player — a real job for the urgent one, the real pools for
        /// the other two — and every work is collected exactly once by the main thread.
        /// </summary>
        [Test]
        public void EachPurpose_ReachesItsOwnDestination_AndIsCollectedOnce()
        {
            var job = new UnityJobWorkExecutor(4);
            WorkerPoolExecutor geometry = WorkerPoolExecutor.GeometryPool(8, PrepareOnMain);
            WorkerPoolExecutor background = WorkerPoolExecutor.BackgroundPool(8, PrepareOnMain);
            var dispatcher = new SharedWorkDispatcher(8, 2, 64, job, geometry, background);
            var jobWork = new JobWork();
            var onGeometry = new NumericWork("geometry", (float*)_input.pointer, SampleCount);
            var onBackground = new NumericWork("background", (float*)_input.pointer, SampleCount);
            try
            {
                dispatcher.BeginFrame(1);
                Assert.That(dispatcher.TryEnqueue(WorkPurpose.AdmittedPhysics, jobWork, out _), Is.True);
                Assert.That(dispatcher.TryEnqueue(WorkPurpose.AdmittedGeometry, onGeometry, out _), Is.True);
                Assert.That(dispatcher.TryEnqueue(WorkPurpose.Speculative, onBackground, out _), Is.True);

                Assert.That(dispatcher.Dispatch().submitted, Is.EqualTo(3), "all three were submitted");
                Assert.That(job.Held, Is.EqualTo(1), "the urgent one to the job system");
                Assert.That(geometry.Held, Is.EqualTo(1), "the admitted geometry to the geometry pool");
                Assert.That(background.Held, Is.EqualTo(1), "and the speculation to the background pool");

                int frame = 1;
                Assert.That(
                    WaitFor(() =>
                    {
                        dispatcher.BeginFrame(++frame);
                        dispatcher.Dispatch();
                        return jobWork.collectCount == 1 && onGeometry.collectCount == 1 && onBackground.collectCount == 1;
                    }),
                    Is.True,
                    "and every one of them was collected");

                Assert.That(jobWork.collectedValue, Is.EqualTo(1), "the job did its work");
                AssertRanOffMainAt(onGeometry, OsBelowNormal, "geometry");
                AssertRanOffMainAt(onBackground, OsLowest, "background");
                Assert.That(dispatcher.SubmittedCount, Is.Zero, "with nothing left out there");

                DispatchShutdownResult stop = dispatcher.Shutdown(Patience);
                Assert.That(stop.workersStopped, Is.True);
                Assert.That(stop.collected, Is.Zero, "there was nothing left to collect");
            }
            finally
            {
                geometry.Dispose();
                background.Dispose();
                jobWork.Dispose();
                onGeometry.Dispose();
                onBackground.Dispose();
            }
        }

        /// <summary>
        /// The normal stop in the player: submission closes, work that never began is cancelled exactly once, the
        /// work that was running finishes with what it holds, and every worker is confirmed stopped.
        /// </summary>
        [Test]
        public void ANormalStop_ClosesSubmission_CancelsWhatNeverBegan_AndStopsEveryWorker()
        {
            var job = new UnityJobWorkExecutor(2);
            var geometry = new WorkerPoolExecutor(
                WorkDestination.GeometryPool, 1, ThreadPriority.BelowNormal, 8, PrepareOnMain);
            WorkerPoolExecutor background = WorkerPoolExecutor.BackgroundPool(8, PrepareOnMain);
            var dispatcher = new SharedWorkDispatcher(8, 2, 64, job, geometry, background);
            var running = new NumericWork("running", (float*)_input.pointer, SampleCount);
            var queued = new NumericWork("queued", (float*)_input.pointer, SampleCount);
            var neverSubmitted = new NumericWork("never-submitted", (float*)_input.pointer, SampleCount);
            try
            {
                dispatcher.BeginFrame(1);

                // The one geometry worker holds the first work until the test lets it go, and then until nothing is
                // queued any more — which only the stop's own draining achieves. So what the stop finds queued is
                // exactly what was put there: no race, and no clock.
                running.waitToBeReleased = true;
                running.holdUntilTheQueueIsEmptyAt = geometry;
                Assert.That(dispatcher.TryEnqueue(WorkPurpose.AdmittedGeometry, running, out _), Is.True);
                dispatcher.Dispatch();
                Assert.That(running.begun.Wait(Patience), Is.True, "the first work is running");

                Assert.That(dispatcher.TryEnqueue(WorkPurpose.AdmittedGeometry, queued, out _), Is.True);
                dispatcher.Dispatch();
                Assert.That(geometry.QueuedCount, Is.EqualTo(1), "the second is queued at the pool");

                // Held here, never submitted: the stop is what reaches it.
                Assert.That(dispatcher.TryEnqueue(WorkPurpose.Speculative, neverSubmitted, out _), Is.True);
                Assert.That(dispatcher.WaitingCount, Is.EqualTo(1));

                running.released.Set();
                DispatchShutdownResult stop = dispatcher.Shutdown(Patience);

                Assert.That(stop.workersStopped, Is.True, "every destination confirmed its workers had stopped");
                Assert.That(geometry.WorkersStopped, Is.True);
                Assert.That(background.WorkersStopped, Is.True);
                Assert.That(dispatcher.IsClosed, Is.True);
                Assert.That(
                    dispatcher.TryEnqueue(WorkPurpose.AdmittedPhysics, new JobWork(), out _), Is.False,
                    "and no new work is taken afterwards");

                Assert.That(running.beginCount, Is.EqualTo(1), "the work that was running ran once");
                Assert.That(running.finishedWriting, Is.True, "and had finished with what it held");
                Assert.That(running.Result, Is.EqualTo(ExpectedSum));
                Assert.That(running.collectCount, Is.EqualTo(1), "and was collected once");
                Assert.That(running.lastCompletion.outcome, Is.EqualTo(WorkOutcome.Finished));

                Assert.That(queued.beginCount, Is.Zero, "the work still in the pool's queue never began");
                Assert.That(queued.collectCount, Is.EqualTo(1), "and got exactly one terminal handling");
                Assert.That(queued.lastCompletion.outcome, Is.EqualTo(WorkOutcome.Cancelled));

                Assert.That(neverSubmitted.beginCount, Is.Zero, "and neither did the one that was never submitted");
                Assert.That(neverSubmitted.collectCount, Is.EqualTo(1));
                Assert.That(neverSubmitted.lastCompletion.outcome, Is.EqualTo(WorkOutcome.Cancelled));

                Assert.That(stop.cancelled, Is.EqualTo(1), "one had not left the dispatch");
                Assert.That(stop.collected, Is.EqualTo(2), "and two were at the pool");
            }
            finally
            {
                geometry.Dispose();
                background.Dispose();
                running.Dispose();
                queued.Dispose();
                neverSubmitted.Dispose();
            }
        }
    }
}
