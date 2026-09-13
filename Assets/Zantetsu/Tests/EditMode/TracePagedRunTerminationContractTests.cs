using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for ending a Run and saving it in one go: what reaches
    /// the file, what happens when either step fails, and what a second
    /// attempt gets.
    /// </summary>
    /// <remarks>
    /// Everything happens in a directory of this fixture's own, outside the
    /// repository. The failures here are made out of ordinary mistakes - a
    /// history released too early, a destination that already exists - rather
    /// than anything injected into the product.
    /// </remarks>
    public unsafe class TracePagedRunTerminationContractTests
    {
        private const TraceEventType KindA = TraceEventType.TaskScheduled;
        private const TraceEventType KindB = TraceEventType.TaskStarted;

        private const int MaxPayloadLength = 16;
        private const int NormalDrainMaxRecordCount = 2;

        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(
                Path.GetTempPath(), "zantetsu-trace-termination-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        [TearDown]
        public void TearDown()
        {
            if (_directory != null && Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }

            _directory = null;
        }

        // -------------------------------------------------------------------
        // Ending a Run and saving it
        // -------------------------------------------------------------------

        [Test]
        public void EverythingLeftInTheLanesEndsUpInTheFile()
        {
            string path = Path.Combine(_directory, "run.ztrace");

            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 4))
            {
                run.Write(0, KindA, 1, 2, 3);
                run.Write(1, KindB, 4);
                run.Write(0, KindA);

                TracePagedRunResult result = run.Coordinator.FinishAndSave(path, out long saved);

                Assert.That(result.Integrity, Is.EqualTo(TraceIntegrityState.Complete));
                Assert.That(result.HistoryCommittedRecordCount, Is.EqualTo(3L));
                Assert.That(
                    run.LanesHold(0), Is.False, "every lane should have been drained");
                Assert.That(run.LanesHold(1), Is.False);

                Assert.That(
                    Directory.GetFileSystemEntries(_directory), Is.EqualTo(new[] { path }),
                    "the finished file should be the only thing there");
                Assert.That(new FileInfo(path).Length, Is.EqualTo(saved));

                // And what is in it is what the Run said it was.
                RecordingDestination destination = new RecordingDestination();
                TracePagedHistoryFileSummary summary =
                    TracePagedHistoryFileStore.Read(path, MaxPayloadLength, destination);

                Assert.That(
                    summary.CommittedRecordCount, Is.EqualTo(result.HistoryCommittedRecordCount));
                Assert.That(summary.Integrity, Is.EqualTo(result.Integrity));
                Assert.That(summary.LaneDropCount, Is.EqualTo(result.FinalDrain.DropCount));
                Assert.That(summary.HistoryDropCount, Is.EqualTo(result.HistoryDropCount));

                Assert.That(destination.Count, Is.EqualTo(3));
                destination.AssertRecord(0, KindA, new byte[] { 1, 2, 3 });
                destination.AssertRecord(1, KindB, new byte[] { 4 });
                destination.AssertRecord(2, KindA, new byte[0]);
            }
        }

        [Test]
        public void TheFileCountsTheWholeRunEvenWhenSomeOfItWasDrainedEarlier()
        {
            string path = Path.Combine(_directory, "run.ztrace");

            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 128, pageCount: 4))
            {
                for (int record = 0; record < 3; record++)
                {
                    run.Write(0, KindA, (byte)record);
                    run.Write(1, KindB, (byte)(100 + record));
                }

                // Two records go to the history during the Run.
                Assert.That(run.DrainOnce(), Is.EqualTo(NormalDrainMaxRecordCount));

                TracePagedRunResult result = run.Coordinator.FinishAndSave(path, out long _);

                Assert.That(
                    result.FinalDrain.RecordCount, Is.EqualTo(4L),
                    "the final drain took what was left");
                Assert.That(result.HistoryCommittedRecordCount, Is.EqualTo(6L));

                RecordingDestination destination = new RecordingDestination();
                TracePagedHistoryFileSummary summary =
                    TracePagedHistoryFileStore.Read(path, MaxPayloadLength, destination);

                Assert.That(
                    summary.CommittedRecordCount, Is.EqualTo(6L),
                    "the file counts the whole Run, not just the final drain");
                Assert.That(destination.Count, Is.EqualTo(6));
                destination.AssertRecord(0, KindA, new byte[] { 0 });
                destination.AssertRecord(1, KindB, new byte[] { 100 });
                destination.AssertRecord(5, KindB, new byte[] { 102 });
            }
        }

        [Test]
        public void WhatWasLostIsStillLostAfterTheRoundTrip()
        {
            string lanePath = Path.Combine(_directory, "lane-drop.ztrace");
            using (Run run = new Run(laneIndexCapacity: 1, pageSize: 128, pageCount: 2))
            {
                run.Write(0, KindA, 1);
                Assert.That(run.TryWrite(0, KindA, 2), Is.False, "the lane is full");

                TracePagedRunResult result = run.Coordinator.FinishAndSave(lanePath, out long _);
                Assert.That(result.Integrity, Is.EqualTo(TraceIntegrityState.Incomplete));

                TracePagedHistoryFileSummary summary = TracePagedHistoryFileStore.Read(
                    lanePath, MaxPayloadLength, new RecordingDestination());
                Assert.That(summary.Integrity, Is.EqualTo(TraceIntegrityState.Incomplete));
                Assert.That(summary.LaneDropCount, Is.EqualTo(1L));
                Assert.That(summary.HistoryDropCount, Is.EqualTo(0L));
            }

            string historyPath = Path.Combine(_directory, "history-drop.ztrace");
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 32, pageCount: 1))
            {
                for (int record = 0; record < 5; record++)
                {
                    run.Write(0, KindA, (byte)record);
                }

                TracePagedRunResult result = run.Coordinator.FinishAndSave(historyPath, out long _);
                Assert.That(result.Integrity, Is.EqualTo(TraceIntegrityState.Incomplete));

                RecordingDestination destination = new RecordingDestination();
                TracePagedHistoryFileSummary summary = TracePagedHistoryFileStore.Read(
                    historyPath, MaxPayloadLength, destination);
                Assert.That(summary.Integrity, Is.EqualTo(TraceIntegrityState.Incomplete));
                Assert.That(summary.LaneDropCount, Is.EqualTo(0L));
                Assert.That(summary.HistoryDropCount, Is.EqualTo(2L));
                Assert.That(destination.Count, Is.EqualTo(3));
            }
        }

        // -------------------------------------------------------------------
        // When one of the two steps fails
        // -------------------------------------------------------------------

        [Test]
        public void WhenFinishingFailsNothingIsSavedAtAll()
        {
            string path = Path.Combine(_directory, "run.ztrace");

            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 4))
            {
                run.Write(0, KindA, 1, 2);

                // Letting go of the history before ending the Run is a mistake
                // in the calling code; finishing cannot get past it.
                run.ReleaseHistory();

                Assert.Throws<ObjectDisposedException>(
                    () => run.Coordinator.FinishAndSave(path, out long _));

                Assert.That(File.Exists(path), Is.False);
                Assert.That(
                    Directory.GetFileSystemEntries(_directory), Is.Empty,
                    "a Run that could not be finished writes nothing, not even part of a file");
            }
        }

        [Test]
        public void WhenSavingFailsNoResultComesBackAndTheFileThatWasThereIsUntouched()
        {
            string path = Path.Combine(_directory, "run.ztrace");
            byte[] existing = { 1, 2, 3, 4 };
            File.WriteAllBytes(path, existing);

            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 4))
            {
                run.Write(0, KindA, 1, 2);

                long saved = -1L;
                Assert.Throws<IOException>(() => run.Coordinator.FinishAndSave(path, out saved));

                Assert.That(saved, Is.EqualTo(-1L), "no byte count is handed back");
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(existing));
                Assert.That(
                    Directory.GetFileSystemEntries(_directory), Is.EqualTo(new[] { path }),
                    "and nothing of its own is left behind");

                // The Run was sealed before the save was tried, and it stays
                // sealed: nothing here undoes that or ends the Run again.
                Assert.Throws<InvalidOperationException>(
                    () => run.Coordinator.FinishAndSave(
                        Path.Combine(_directory, "elsewhere.ztrace"), out long _));
                Assert.That(
                    Directory.GetFileSystemEntries(_directory), Is.EqualTo(new[] { path }),
                    "a sealed Run is not saved somewhere else instead");
            }
        }

        [Test]
        public void EndingTheSameRunTwiceIsRefusedAndWritesNoSecondFile()
        {
            string path = Path.Combine(_directory, "run.ztrace");
            string second = Path.Combine(_directory, "again.ztrace");

            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 4))
            {
                run.Write(0, KindA, 1, 2);
                run.Coordinator.FinishAndSave(path, out long _);

                Assert.Throws<InvalidOperationException>(
                    () => run.Coordinator.FinishAndSave(second, out long _));

                Assert.That(
                    Directory.GetFileSystemEntries(_directory), Is.EqualTo(new[] { path }),
                    "the second attempt leaves no file of its own");
            }
        }

        // -------------------------------------------------------------------
        // What is still the caller's afterwards
        // -------------------------------------------------------------------

        [Test]
        public void TheLanesAndTheHistoryAreStillTheCallersToRelease()
        {
            string path = Path.Combine(_directory, "run.ztrace");

            Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 4);
            try
            {
                run.Write(0, KindA, 1, 2);
                run.Coordinator.FinishAndSave(path, out long _);

                Assert.That(run.HistoryIsLive, Is.True, "ending a Run releases nothing");
                Assert.That(run.LanesAreLive, Is.True);
            }
            finally
            {
                run.Dispose();
            }

            Assert.That(run.HistoryIsLive, Is.False);
            Assert.That(run.LanesAreLive, Is.False);
            Assert.That(File.Exists(path), Is.True, "and the file stays where it was put");
        }

        [Test]
        public void ACoordinatorNeedsAFinalizer()
        {
            Assert.Throws<ArgumentNullException>(
                () => new TracePagedRunTerminationCoordinator(null));
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Two lanes, a history, a drainer and the coordinator over them, put
        /// together the way a Run would be.
        /// </summary>
        private sealed class Run : IDisposable
        {
            private readonly TraceLaneSet _lanes;
            private readonly TracePagedHistory _history;
            private readonly TraceLaneDrainer _drainer;

            internal Run(int laneIndexCapacity, int pageSize, int pageCount)
            {
                TraceLaneSetProfile profile = new TraceLaneSetProfile(
                    MaxPayloadLength,
                    NormalDrainMaxRecordCount,
                    pageSize,
                    pageCount,
                    new[]
                    {
                        new TraceLaneSettings(
                            TraceLaneEventMask.None.With(KindA), 4096, laneIndexCapacity),
                        new TraceLaneSettings(
                            TraceLaneEventMask.None.With(KindB), 4096, laneIndexCapacity),
                    });

                _lanes = new TraceLaneSet(profile);
                _history = new TracePagedHistory(profile);
                _drainer = new TraceLaneDrainer(_lanes);
                Coordinator = new TracePagedRunTerminationCoordinator(
                    new TracePagedRunFinalizer(_drainer, _history));
            }

            internal TracePagedRunTerminationCoordinator Coordinator { get; }

            internal bool HistoryIsLive => _history.AllocatedBytes > 0L;

            internal bool LanesAreLive => _lanes.AllocatedBytes > 0L;

            internal bool LanesHold(int ordinal)
            {
                return _lanes.TryPeek(ordinal, out TraceLaneIndexEntry _, out byte* _);
            }

            internal int DrainOnce()
            {
                return _drainer.Drain(_history);
            }

            internal void Write(int ordinal, TraceEventType kind, params byte[] payload)
            {
                Assert.That(TryWrite(ordinal, kind, payload), Is.True);
            }

            internal bool TryWrite(int ordinal, TraceEventType kind, params byte[] payload)
            {
                TraceLaneWriter writer = _lanes.CreateWriter(ordinal);
                fixed (byte* pinned = payload)
                {
                    return writer.TryWrite(kind, pinned, payload.Length);
                }
            }

            /// <summary>
            /// Lets go of the history early, so finishing fails without
            /// anything being injected into the product.
            /// </summary>
            internal void ReleaseHistory()
            {
                _history.Dispose();
            }

            public void Dispose()
            {
                _history.Dispose();
                _lanes.Dispose();
            }
        }

        private sealed class RecordingDestination : ITraceRecordDestination
        {
            private readonly List<TraceEventType> _kinds = new List<TraceEventType>();
            private readonly List<byte[]> _payloads = new List<byte[]>();

            internal int Count => _kinds.Count;

            public void Receive(TraceEventType recordKind, byte* payload, int payloadLength)
            {
                byte[] copy = new byte[payloadLength];
                for (int i = 0; i < payloadLength; i++)
                {
                    copy[i] = payload[i];
                }

                _kinds.Add(recordKind);
                _payloads.Add(copy);
            }

            internal void AssertRecord(int record, TraceEventType kind, byte[] payload)
            {
                Assert.That(_kinds.Count, Is.GreaterThan(record), "record " + record + " never arrived");
                Assert.That(_kinds[record], Is.EqualTo(kind), "record " + record + " has another kind");
                Assert.That(
                    _payloads[record], Is.EqualTo(payload), "record " + record + " has another payload");
            }
        }
    }
}
