using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for saving a finished Run's trace to a real file and
    /// reading it back: what appears on disk, what does not, and what is
    /// refused.
    /// </summary>
    /// <remarks>
    /// Everything happens in a directory of this fixture's own, outside the
    /// repository, made and removed around each test. What is on disk is
    /// checked by listing that directory - nothing here reaches into the store
    /// for a path or a temporary name, and nothing stands in for the
    /// filesystem.
    /// </remarks>
    public unsafe class TracePagedHistoryFileStoreContractTests
    {
        private const TraceEventType KindA = TraceEventType.TaskScheduled;
        private const TraceEventType KindB = TraceEventType.TaskStarted;

        private const int MaxPayloadLength = 16;
        private const int NormalDrainMaxRecordCount = 64;

        private string _directory;

        [SetUp]
        public void SetUp()
        {
            _directory = Path.Combine(
                Path.GetTempPath(), "zantetsu-trace-store-" + Guid.NewGuid().ToString("N"));
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
        // Saving
        // -------------------------------------------------------------------

        [Test]
        public void ASavedRunLeavesTheFinishedFileAndNothingElse()
        {
            string path = Path.Combine(_directory, "run.ztrace");

            long written;
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 4))
            {
                run.Write(0, KindA, 1, 2, 3);
                run.Write(1, KindB, 4);
                written = TracePagedHistoryFileStore.SaveAtomic(path, run.Finish());
            }

            Assert.That(
                Directory.GetFileSystemEntries(_directory), Is.EqualTo(new[] { path }),
                "the finished file should be the only thing left behind");
            Assert.That(
                new FileInfo(path).Length, Is.EqualTo(written),
                "the length on disk is what the save said it wrote");
        }

        [Test]
        public void ASavedRunReadsBackAsItWentIn()
        {
            string path = Path.Combine(_directory, "run.ztrace");

            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 32, pageCount: 6))
            {
                // 24 bytes each in a 32-byte page, so these span pages.
                run.Write(0, KindA, new byte[16]);
                run.Write(1, KindB, 7, 8);
                run.Write(0, KindA);
                TracePagedHistoryFileStore.SaveAtomic(path, run.Finish());
            }

            RecordingDestination destination = new RecordingDestination();
            TracePagedHistoryFileSummary summary =
                TracePagedHistoryFileStore.Read(path, MaxPayloadLength, destination);

            Assert.That(summary.Integrity, Is.EqualTo(TraceIntegrityState.Complete));
            Assert.That(summary.CommittedRecordCount, Is.EqualTo(3L));
            Assert.That(summary.LaneDropCount, Is.EqualTo(0L));
            Assert.That(summary.HistoryDropCount, Is.EqualTo(0L));

            Assert.That(destination.Count, Is.EqualTo(3));
            destination.AssertRecord(0, KindA, new byte[16]);
            destination.AssertRecord(1, KindB, new byte[] { 7, 8 });
            destination.AssertRecord(2, KindA, new byte[0]);
        }

        [Test]
        public void WhatWasLostSurvivesTheRoundTrip()
        {
            string lanePath = Path.Combine(_directory, "lane-drop.ztrace");
            using (Run run = new Run(laneIndexCapacity: 1, pageSize: 128, pageCount: 2))
            {
                run.Write(0, KindA, 1);
                Assert.That(run.TryWrite(0, KindA, 2), Is.False, "the lane is full");
                TracePagedHistoryFileStore.SaveAtomic(lanePath, run.Finish());
            }

            TracePagedHistoryFileSummary summary = TracePagedHistoryFileStore.Read(
                lanePath, MaxPayloadLength, new RecordingDestination());
            Assert.That(summary.Integrity, Is.EqualTo(TraceIntegrityState.Incomplete));
            Assert.That(summary.LaneDropCount, Is.EqualTo(1L));
            Assert.That(summary.HistoryDropCount, Is.EqualTo(0L));

            string historyPath = Path.Combine(_directory, "history-drop.ztrace");
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 32, pageCount: 1))
            {
                for (int record = 0; record < 5; record++)
                {
                    run.Write(0, KindA, (byte)record);
                }

                TracePagedHistoryFileStore.SaveAtomic(historyPath, run.Finish());
            }

            RecordingDestination destination = new RecordingDestination();
            summary = TracePagedHistoryFileStore.Read(historyPath, MaxPayloadLength, destination);
            Assert.That(summary.Integrity, Is.EqualTo(TraceIntegrityState.Incomplete));
            Assert.That(summary.LaneDropCount, Is.EqualTo(0L));
            Assert.That(summary.HistoryDropCount, Is.EqualTo(2L));
            Assert.That(destination.Count, Is.EqualTo(3));
        }

        // -------------------------------------------------------------------
        // What is refused
        // -------------------------------------------------------------------

        [Test]
        public void AFileThatIsAlreadyThereIsLeftAlone()
        {
            string path = Path.Combine(_directory, "run.ztrace");
            byte[] existing = { 1, 2, 3, 4 };
            File.WriteAllBytes(path, existing);

            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 2))
            {
                run.Write(0, KindA, 1);
                TracePagedRunResult result = run.Finish();

                Assert.Throws<IOException>(() => TracePagedHistoryFileStore.SaveAtomic(path, result));
            }

            Assert.That(File.ReadAllBytes(path), Is.EqualTo(existing), "the file that was there is untouched");
            Assert.That(
                Directory.GetFileSystemEntries(_directory), Is.EqualTo(new[] { path }),
                "a refused save leaves nothing of its own behind");
        }

        [Test]
        public void ADirectoryInTheWayIsLeftAlone()
        {
            string path = Path.Combine(_directory, "run.ztrace");
            Directory.CreateDirectory(path);
            string inside = Path.Combine(path, "something");
            File.WriteAllBytes(inside, new byte[] { 9 });

            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 2))
            {
                run.Write(0, KindA, 1);
                TracePagedRunResult result = run.Finish();

                Assert.Throws<IOException>(() => TracePagedHistoryFileStore.SaveAtomic(path, result));
            }

            Assert.That(Directory.Exists(path), Is.True);
            Assert.That(
                Directory.GetFileSystemEntries(path), Is.EqualTo(new[] { inside }),
                "what was in the directory is still in it");
            Assert.That(Directory.GetFileSystemEntries(_directory), Is.EqualTo(new[] { path }));
        }

        [Test]
        public void AMissingDirectoryIsNotMadeOnTheWay()
        {
            string missing = Path.Combine(_directory, "not-there");
            string path = Path.Combine(missing, "run.ztrace");

            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 2))
            {
                run.Write(0, KindA, 1);
                TracePagedRunResult result = run.Finish();

                Assert.Throws<DirectoryNotFoundException>(
                    () => TracePagedHistoryFileStore.SaveAtomic(path, result));
            }

            Assert.That(Directory.Exists(missing), Is.False, "no directory is made to save into");
            Assert.That(File.Exists(path), Is.False);
            Assert.That(Directory.GetFileSystemEntries(_directory), Is.Empty);
        }

        [Test]
        public void ASaveThatFailsPartWayThroughPublishesNothing()
        {
            string path = Path.Combine(_directory, "run.ztrace");

            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 4))
            {
                run.Write(0, KindA, 1, 2, 3);
                run.Write(1, KindB, 4);
                TracePagedRunResult result = run.Finish();

                // Releasing the history before saving is a mistake in the
                // calling code; what matters here is what it leaves on disk.
                run.ReleaseHistory();

                Assert.Throws<ObjectDisposedException>(
                    () => TracePagedHistoryFileStore.SaveAtomic(path, result));
            }

            Assert.That(File.Exists(path), Is.False, "a save that failed publishes no file");
            Assert.That(
                Directory.GetFileSystemEntries(_directory), Is.Empty,
                "and it leaves none of its own working files behind either");
        }

        // -------------------------------------------------------------------
        // Reading
        // -------------------------------------------------------------------

        [Test]
        public void AMalformedFileIsRefusedAndTheDestinationIsLeftAlone()
        {
            string path = Path.Combine(_directory, "rubbish.ztrace");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });

            RecordingDestination destination = new RecordingDestination();
            Assert.Throws<InvalidDataException>(
                () => TracePagedHistoryFileStore.Read(path, MaxPayloadLength, destination));

            Assert.That(destination.Disposed, Is.False, "the destination is the caller's");
            Assert.That(destination.Count, Is.EqualTo(0));

            // And the file it refused is still there, untouched.
            Assert.That(new FileInfo(path).Length, Is.EqualTo(8));
        }

        [Test]
        public void ReadingAFileThatIsNotThereFails()
        {
            string path = Path.Combine(_directory, "never-written.ztrace");

            Assert.Throws<FileNotFoundException>(
                () => TracePagedHistoryFileStore.Read(path, MaxPayloadLength, new RecordingDestination()));
            Assert.That(Directory.GetFileSystemEntries(_directory), Is.Empty);
        }

        [Test]
        public void TheStoreTakesOverNothingItWasGiven()
        {
            string path = Path.Combine(_directory, "run.ztrace");

            Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 4);
            try
            {
                run.Write(0, KindA, 1, 2);
                TracePagedHistoryFileStore.SaveAtomic(path, run.Finish());
                TracePagedHistoryFileStore.Read(path, MaxPayloadLength, new RecordingDestination());

                // Everything the store was handed is still the caller's, and
                // still alive.
                Assert.That(run.HistoryIsLive, Is.True);
                Assert.That(run.LanesAreLive, Is.True);
            }
            finally
            {
                run.Dispose();
            }

            Assert.That(run.HistoryIsLive, Is.False);
            Assert.That(run.LanesAreLive, Is.False);
        }

        [Test]
        public void AStoreNeedsAnAbsolutePath()
        {
            using (Run run = new Run(laneIndexCapacity: 8, pageSize: 64, pageCount: 2))
            {
                run.Write(0, KindA, 1);
                TracePagedRunResult result = run.Finish();

                Assert.Throws<ArgumentNullException>(
                    () => TracePagedHistoryFileStore.SaveAtomic(null, result));
                Assert.Throws<ArgumentException>(
                    () => TracePagedHistoryFileStore.SaveAtomic("run.ztrace", result));
                Assert.Throws<ArgumentNullException>(
                    () => TracePagedHistoryFileStore.Read(null, MaxPayloadLength, new RecordingDestination()));
                Assert.Throws<ArgumentException>(
                    () => TracePagedHistoryFileStore.Read(
                        "run.ztrace", MaxPayloadLength, new RecordingDestination()));
            }

            Assert.That(Directory.GetFileSystemEntries(_directory), Is.Empty);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        /// <summary>
        /// Two lanes, a history and a drainer, finished once.
        /// </summary>
        private sealed class Run : IDisposable
        {
            private readonly TraceLaneSet _lanes;
            private readonly TracePagedHistory _history;
            private readonly TracePagedRunFinalizer _finalizer;

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
                _finalizer = new TracePagedRunFinalizer(new TraceLaneDrainer(_lanes), _history);
            }

            internal bool HistoryIsLive => _history.AllocatedBytes > 0L;

            internal bool LanesAreLive => _lanes.AllocatedBytes > 0L;

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

            internal TracePagedRunResult Finish()
            {
                return _finalizer.Finish();
            }

            /// <summary>
            /// Lets go of the history early, so a save can be made to fail
            /// without anything being injected into the store.
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

            internal bool Disposed => false;

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
