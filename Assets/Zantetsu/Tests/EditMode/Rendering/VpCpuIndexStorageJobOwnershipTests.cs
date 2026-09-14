using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using UnityEngine.TestTools;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Synthetic jobs over the CPU VP index storage (DESIGN 4.5.3): two independent jobs read one published input through
    /// their own read leases and write into non-overlapping reserved outputs. Only the main thread collects a completed
    /// job: it publishes what the job wrote, returning the unused tail, or cancels a too small reservation and runs the
    /// job again from the same lease. A range is handed over only once published, each lease is returned after its
    /// job's final run, and collecting an attempt twice changes nothing.
    /// </summary>
    public class VpCpuIndexStorageJobOwnershipTests
    {
        private const double JobTimeoutSeconds = 10.0;

        private static readonly uint[] Input = { 100, 101, 102, 103, 104, 105, 106, 107, 108, 109 };
        private static readonly uint[] EvenPositions = { 100, 102, 104, 106, 108 };
        private static readonly uint[] OddPositions = { 101, 103, 105, 107, 109 };

        private struct SelectionResult
        {
            public int requiredCount;
            public int writtenCount;
            public bool fits;
        }

        /// <summary>
        /// Selects every second input index starting at <see cref="parity"/>. It counts everything the selection needs but
        /// writes only what the output holds, from the output's start.
        /// </summary>
        private struct SelectEverySecondIndexJob : IJob
        {
            [ReadOnly, NativeDisableContainerSafetyRestriction]
            public NativeArray<uint>.ReadOnly input;

            [WriteOnly, NativeDisableContainerSafetyRestriction]
            public NativeArray<uint> output;

            public int parity;
            public NativeReference<SelectionResult> result;

            public void Execute()
            {
                int required = 0;
                for (int i = parity; i < input.Length; i += 2)
                {
                    if (required < output.Length)
                    {
                        output[required] = input[i];
                    }

                    required++;
                }

                result.Value = new SelectionResult
                {
                    requiredCount = required,
                    writtenCount = Math.Min(required, output.Length),
                    fits = required <= output.Length,
                };
            }
        }

        /// <summary>One selection, holding its read lease and input view across all of its runs.</summary>
        private sealed class Selection
        {
            public string name;
            public int parity;
            public VpIndexReadLease lease;
            public NativeArray<uint>.ReadOnly input;
        }

        /// <summary>One run of a selection: its reserved output, job and result.</summary>
        private sealed class Attempt
        {
            public Selection selection;
            public VpIndexRangeHandle output;
            public JobHandle job;
            public NativeReference<SelectionResult> result;
            public bool collected;
            public SelectionResult collectedResult;
        }

        /// <summary>Main-thread scheduling and collection. <see cref="owned"/> receives ranges only once they are published.</summary>
        private sealed class Collector
        {
            public readonly List<VpIndexRangeHandle> owned = new List<VpIndexRangeHandle>();

            private readonly VpCpuIndexStorage _storage;
            private readonly List<Attempt> _attempts = new List<Attempt>();

            public Collector(VpCpuIndexStorage storage)
            {
                _storage = storage;
            }

            public Attempt Reserve(Selection selection, int indexCount)
            {
                Assert.That(_storage.TryReserve(indexCount, out VpIndexRangeHandle output), Is.True, selection.name + " reserve " + indexCount);
                var attempt = new Attempt { selection = selection, output = output };
                _attempts.Add(attempt);
                return attempt;
            }

            public void Schedule(Attempt attempt)
            {
                Assert.That(_storage.TryGetReservedWriteView(attempt.output, out NativeArray<uint> output), Is.True, attempt.selection.name + " write view");
                attempt.result = new NativeReference<SelectionResult>(Allocator.Persistent);
                attempt.job = new SelectEverySecondIndexJob
                {
                    input = attempt.selection.input,
                    output = output,
                    parity = attempt.selection.parity,
                    result = attempt.result,
                }.Schedule();
            }

            /// <summary>
            /// Collects the attempt once its job has completed. A fitting result publishes the written indices, hands the
            /// range over and returns the lease, as that was the selection's final run; a result that did not fit cancels
            /// the reservation and keeps the lease for another run. Returns false, changing nothing, while the job is
            /// still running or when the attempt has already been collected.
            /// </summary>
            public bool TryCollect(Attempt attempt)
            {
                if (attempt.collected || !attempt.job.IsCompleted)
                {
                    return false;
                }

                attempt.job.Complete();
                attempt.collected = true;
                attempt.collectedResult = attempt.result.Value;
                attempt.result.Dispose();

                string name = attempt.selection.name;
                if (!attempt.collectedResult.fits)
                {
                    Assert.That(_storage.TryCancelReservation(attempt.output), Is.True, name + " cancel");
                    return true;
                }

                Assert.That(_storage.TryPublish(attempt.output, attempt.collectedResult.writtenCount), Is.True, name + " publish");
                owned.Add(attempt.output);
                Assert.That(_storage.TryReleaseReadLease(attempt.selection.lease), Is.True, name + " release lease");
                return true;
            }

            /// <summary>Test cleanup only, not a collection path: completes every job and frees the results left.</summary>
            public void CompleteAndDisposeAll()
            {
                foreach (Attempt attempt in _attempts)
                {
                    attempt.job.Complete();
                    if (attempt.result.IsCreated)
                    {
                        attempt.result.Dispose();
                    }
                }
            }
        }

        private static void AssertState(VpCpuIndexStorage storage, VpIndexRangeHandle handle, VpIndexRangeState state, int indexStart, int indexCount, string label)
        {
            Assert.That(storage.TryGetState(handle, out VpIndexRangeState actualState, out int actualStart, out int actualCount), Is.True, label + " state query");
            Assert.That(
                new object[] { actualState, actualStart, actualCount },
                Is.EqualTo(new object[] { state, indexStart, indexCount }),
                label + " state, indexStart, indexCount");
        }

        private static void AssertResult(Attempt attempt, int requiredCount, int writtenCount, bool fits)
        {
            SelectionResult result = attempt.collectedResult;
            Assert.That(
                new object[] { result.requiredCount, result.writtenCount, result.fits },
                Is.EqualTo(new object[] { requiredCount, writtenCount, fits }),
                attempt.selection.name + " required, written, fits");
        }

        private static void AssertReadsThroughNewLease(VpCpuIndexStorage storage, VpIndexRangeHandle handle, uint[] expected, string label)
        {
            Assert.That(storage.TryAcquireReadLease(handle, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True, label + " acquire");
            Assert.That(view.ToArray(), Is.EqualTo(expected), label + " values");
            Assert.That(storage.TryReleaseReadLease(lease), Is.True, label + " release");
        }

        private static Selection Lease(VpCpuIndexStorage storage, VpIndexRangeHandle input, string name, int parity)
        {
            Assert.That(storage.TryAcquireReadLease(input, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True, name + " lease");
            return new Selection { name = name, parity = parity, lease = lease, input = view };
        }

        [UnityTest]
        public IEnumerator TwoJobs_WriteDisjointReservationsFromOneLeasedInput_AndOnlyCollectedPublishesAreHandedOver()
        {
            var storage = new VpCpuIndexStorage(64, 8, Allocator.Persistent);
            var collector = new Collector(storage);
            try
            {
                // 1. The common input is published at [0, 10).
                Assert.That(storage.TryReserve(Input.Length, out VpIndexRangeHandle input), Is.True, "reserve input");
                Assert.That(storage.TryGetReservedWriteView(input, out NativeArray<uint> inputWrite), Is.True, "input write view");
                inputWrite.CopyFrom(Input);
                Assert.That(storage.TryPublish(input), Is.True, "publish input");

                // 2. Each selection takes its own read lease.
                Selection even = Lease(storage, input, "even", 0);
                Selection odd = Lease(storage, input, "odd", 1);

                // 3. Non-overlapping outputs: 7 for the 5 even positions, deliberately only 3 for the 5 odd ones.
                Attempt evenRun = collector.Reserve(even, 7);
                Attempt oddFirstRun = collector.Reserve(odd, 3);
                AssertState(storage, evenRun.output, VpIndexRangeState.Reserved, 10, 7, "even output");
                AssertState(storage, oddFirstRun.output, VpIndexRangeState.Reserved, 17, 3, "odd first output");

                // 4. Both jobs are scheduled without dependencies on each other.
                collector.Schedule(evenRun);
                collector.Schedule(oddFirstRun);
                JobHandle.ScheduleBatchedJobs();

                // 5. The input retires while both leases are held.
                Assert.That(storage.TryRetire(input), Is.True, "retire input");
                AssertState(storage, input, VpIndexRangeState.Retiring, 0, Input.Length, "input");

                // 6. Even after both jobs have finished, nothing is published or handed over before collection.
                Stopwatch clock = Stopwatch.StartNew();
                while (!evenRun.job.IsCompleted || !oddFirstRun.job.IsCompleted)
                {
                    Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(JobTimeoutSeconds), "the first jobs complete");
                    yield return null;
                }

                AssertState(storage, evenRun.output, VpIndexRangeState.Reserved, 10, 7, "even output before collection");
                AssertState(storage, oddFirstRun.output, VpIndexRangeState.Reserved, 17, 3, "odd output before collection");
                Assert.That(collector.owned, Is.Empty, "nothing handed over before collection");

                // 7-8. The even run is collected: its 5 indices are published, the unused 2 returned, and its lease returned.
                Assert.That(collector.TryCollect(evenRun), Is.True, "collect even");
                AssertResult(evenRun, 5, 5, true);
                AssertState(storage, evenRun.output, VpIndexRangeState.Published, 10, 5, "even output");
                Assert.That(collector.owned, Is.EqualTo(new[] { evenRun.output }), "handed over after the even publish");
                Assert.That(storage.TryGetReadView(even.lease, out _), Is.False, "even lease returned");
                AssertState(storage, input, VpIndexRangeState.Retiring, 0, Input.Length, "input while odd holds its lease");

                Assert.That(collector.TryCollect(evenRun), Is.False, "collect even twice");
                AssertState(storage, evenRun.output, VpIndexRangeState.Published, 10, 5, "even output after collecting twice");
                Assert.That(collector.owned, Is.EqualTo(new[] { evenRun.output }), "handed over once");
                AssertState(storage, input, VpIndexRangeState.Retiring, 0, Input.Length, "input after collecting even twice");

                // 9. The odd run did not fit: its reservation is cancelled, nothing is handed over, and its lease is kept.
                Assert.That(collector.TryCollect(oddFirstRun), Is.True, "collect odd first run");
                AssertResult(oddFirstRun, 5, 3, false);
                AssertState(storage, oddFirstRun.output, VpIndexRangeState.Free, 17, 3, "odd first output");
                Assert.That(collector.owned, Is.EqualTo(new[] { evenRun.output }), "the unfit run is not handed over");
                Assert.That(storage.TryGetReadView(odd.lease, out NativeArray<uint>.ReadOnly oddLeaseView), Is.True, "odd lease kept");
                Assert.That(oddLeaseView.ToArray(), Is.EqualTo(Input), "odd lease still reads the retiring input");

                Assert.That(collector.TryCollect(oddFirstRun), Is.False, "collect odd first run twice");
                AssertState(storage, input, VpIndexRangeState.Retiring, 0, Input.Length, "input after collecting odd twice");

                // 10. The odd selection runs again from the same lease with the count it needed. The returned even tail
                // [15, 17) and the cancelled [17, 20) have merged, so the new reservation starts at 15.
                Attempt oddRetry = collector.Reserve(odd, oddFirstRun.collectedResult.requiredCount);
                AssertState(storage, oddRetry.output, VpIndexRangeState.Reserved, 15, 5, "odd retry output");
                collector.Schedule(oddRetry);
                JobHandle.ScheduleBatchedJobs();

                // 11. Until the retry has completed, collecting it changes nothing; once collected it is published whole.
                clock.Restart();
                while (!collector.TryCollect(oddRetry))
                {
                    AssertState(storage, oddRetry.output, VpIndexRangeState.Reserved, 15, 5, "odd retry output before collection");
                    Assert.That(collector.owned, Has.Count.EqualTo(1), "odd retry not handed over before collection");
                    Assert.That(clock.Elapsed.TotalSeconds, Is.LessThan(JobTimeoutSeconds), "the retry completes");
                    yield return null;
                }

                AssertResult(oddRetry, 5, 5, true);
                AssertState(storage, oddRetry.output, VpIndexRangeState.Published, 15, 5, "odd retry output");
                Assert.That(collector.owned, Is.EqualTo(new[] { evenRun.output, oddRetry.output }), "both handed over once");
                Assert.That(collector.TryCollect(oddRetry), Is.False, "collect odd retry twice");
                Assert.That(collector.owned, Has.Count.EqualTo(2), "handed over once after collecting twice");

                // 12-13. The odd lease was the last one: the input is Free and its space is reused.
                Assert.That(storage.TryGetReadView(odd.lease, out _), Is.False, "odd lease returned");
                AssertState(storage, input, VpIndexRangeState.Free, 0, Input.Length, "input after the last lease");
                Assert.That(storage.TryReserve(Input.Length, out VpIndexRangeHandle reuse), Is.True, "reserve the input space again");
                AssertState(storage, reuse, VpIndexRangeState.Reserved, 0, Input.Length, "reused input space");
                Assert.That(storage.TryCancelReservation(reuse), Is.True, "cancel the reuse");

                // 14. The handed-over ranges hold the two halves of the input, read through new leases.
                AssertReadsThroughNewLease(storage, collector.owned[0], EvenPositions, "even");
                AssertReadsThroughNewLease(storage, collector.owned[1], OddPositions, "odd");
            }
            finally
            {
                collector.CompleteAndDisposeAll();
                storage.Dispose();
            }
        }
    }
}
