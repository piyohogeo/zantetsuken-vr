using System;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceRunBundleExporterTests
    {
        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

        private static TraceRunContext MakeContext()
        {
            string sha64 = new string('a', 64);
            return new TraceRunContext(9, 4321, "build-e", "6000.3.22f1", sha64, "scene-e", 77, 0.02, 1, "High", 2, new Vector3(0f, -9.81f, 0f));
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsu-exporter", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void DeleteDir(string dir)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, true);
                }
            }
            catch
            {
                // best-effort
            }
        }

        private static long[] SnapshotTimestamps(TraceCaptureSnapshot snapshot)
        {
            long[] ts = new long[snapshot.EventCount];
            for (int i = 0; i < ts.Length; i++)
            {
                ts[i] = snapshot.GetEvent(i).Timestamp;
            }

            return ts;
        }

        private static void AssertManifestsEqual(TraceRunManifest expected, TraceRunManifest actual)
        {
            Assert.That(actual.SchemaVersion, Is.EqualTo(expected.SchemaVersion));
            Assert.That(actual.TestRunId, Is.EqualTo(expected.TestRunId));
            Assert.That(actual.CapturedUtcUnixMilliseconds, Is.EqualTo(expected.CapturedUtcUnixMilliseconds));
            Assert.That(actual.BuildId, Is.EqualTo(expected.BuildId));
            Assert.That(actual.UnityVersion, Is.EqualTo(expected.UnityVersion));
            Assert.That(actual.PackageLockSha256, Is.EqualTo(expected.PackageLockSha256));
            Assert.That(actual.SceneId, Is.EqualTo(expected.SceneId));
            Assert.That(actual.RandomSeed, Is.EqualTo(expected.RandomSeed));
            Assert.That(actual.FixedDeltaTimeSeconds, Is.EqualTo(expected.FixedDeltaTimeSeconds));
            Assert.That(actual.QualityLevel, Is.EqualTo(expected.QualityLevel));
            Assert.That(actual.QualityName, Is.EqualTo(expected.QualityName));
            Assert.That(actual.WorldPhysicsProfileVersion, Is.EqualTo(expected.WorldPhysicsProfileVersion));
            Assert.That(actual.Gravity, Is.EqualTo(expected.Gravity));
            Assert.That(actual.TraceFormatMajorVersion, Is.EqualTo(expected.TraceFormatMajorVersion));
            Assert.That(actual.TraceFormatMinorVersion, Is.EqualTo(expected.TraceFormatMinorVersion));
            Assert.That(actual.EventCount, Is.EqualTo(expected.EventCount));
            Assert.That(actual.TriggerHistoryCount, Is.EqualTo(expected.TriggerHistoryCount));
            Assert.That(actual.CapturedPostRollCount, Is.EqualTo(expected.CapturedPostRollCount));
            Assert.That(actual.WasHistoryOverwrittenAtTrigger, Is.EqualTo(expected.WasHistoryOverwrittenAtTrigger));
        }

        private static TraceFlightRecorder MakeFrozenRecorder(int historyCount, int postRollCount, out TraceLogger logger)
        {
            logger = new TraceLogger(Math.Max(1, historyCount + 1));
            TraceFlightRecorder recorder = new TraceFlightRecorder(logger, postRollCount);
            for (int i = 1; i <= historyCount; i++)
            {
                logger.Enqueue(Event(i));
            }

            logger.Drain();
            recorder.TryTrigger();
            for (int i = 0; i < postRollCount; i++)
            {
                logger.Enqueue(Event(1000 + i));
            }

            if (postRollCount > 0)
            {
                recorder.Drain();
            }

            return recorder;
        }

        [Test]
        public void SaveFrozen_PostRollZero_Roundtrip()
        {
            TraceFlightRecorder recorder = MakeFrozenRecorder(3, 0, out TraceLogger logger);
            try
            {
                string dir = CreateTempDir();
                try
                {
                    string bundlePath = Path.Combine(dir, "bundle");
                    TraceRunManifest manifest = TraceRunBundleExporter.SaveFrozenAtomic(bundlePath, recorder, MakeContext());

                    TraceRunBundle bundle = TraceRunBundleStore.Load(bundlePath, int.MaxValue);

                    Assert.That(manifest.EventCount, Is.EqualTo(3));
                    Assert.That(bundle.Snapshot.EventCount, Is.EqualTo(3));
                    Assert.That(SnapshotTimestamps(bundle.Snapshot), Is.EqualTo(new long[] { 1, 2, 3 }));
                }
                finally
                {
                    DeleteDir(dir);
                }
            }
            finally
            {
                logger.Dispose();
            }
        }

        [Test]
        public void SaveFrozen_WithPostRoll_PreservesCountsAndOrder()
        {
            TraceFlightRecorder recorder = MakeFrozenRecorder(2, 3, out TraceLogger logger);
            try
            {
                string dir = CreateTempDir();
                try
                {
                    string bundlePath = Path.Combine(dir, "bundle");
                    TraceRunBundleExporter.SaveFrozenAtomic(bundlePath, recorder, MakeContext());

                    TraceRunBundle bundle = TraceRunBundleStore.Load(bundlePath, int.MaxValue);

                    Assert.That(bundle.Manifest.TriggerHistoryCount, Is.EqualTo(2));
                    Assert.That(bundle.Manifest.CapturedPostRollCount, Is.EqualTo(3));
                    Assert.That(bundle.Manifest.EventCount, Is.EqualTo(5));
                    Assert.That(SnapshotTimestamps(bundle.Snapshot), Is.EqualTo(new long[] { 1, 2, 1000, 1001, 1002 }));
                }
                finally
                {
                    DeleteDir(dir);
                }
            }
            finally
            {
                logger.Dispose();
            }
        }

        [Test]
        public void SaveFrozen_Armed_ThrowsAndCreatesNothing()
        {
            TraceLogger logger = new TraceLogger(4);
            TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 2);
            try
            {
                string dir = CreateTempDir();
                try
                {
                    string bundlePath = Path.Combine(dir, "bundle");

                    Assert.Throws<InvalidOperationException>(
                        () => TraceRunBundleExporter.SaveFrozenAtomic(bundlePath, recorder, MakeContext()));
                    Assert.That(Directory.Exists(bundlePath), Is.False);
                }
                finally
                {
                    DeleteDir(dir);
                }
            }
            finally
            {
                logger.Dispose();
            }
        }

        [Test]
        public void SaveFrozen_CapturingPostRoll_ThrowsAndCreatesNothing()
        {
            TraceLogger logger = new TraceLogger(4);
            TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 5);
            logger.Enqueue(Event(1));
            logger.Drain();
            recorder.TryTrigger(); // CapturingPostRoll
            try
            {
                string dir = CreateTempDir();
                try
                {
                    string bundlePath = Path.Combine(dir, "bundle");

                    Assert.Throws<InvalidOperationException>(
                        () => TraceRunBundleExporter.SaveFrozenAtomic(bundlePath, recorder, MakeContext()));
                    Assert.That(Directory.Exists(bundlePath), Is.False);
                }
                finally
                {
                    DeleteDir(dir);
                }
            }
            finally
            {
                logger.Dispose();
            }
        }

        [Test]
        public void SaveFrozen_NullRecorder_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => TraceRunBundleExporter.SaveFrozenAtomic("unused", null, MakeContext()));
        }

        [Test]
        public void SaveFrozen_NullContext_Throws()
        {
            TraceLogger logger = new TraceLogger(4);
            TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);
            try
            {
                Assert.Throws<ArgumentNullException>(
                    () => TraceRunBundleExporter.SaveFrozenAtomic("unused", recorder, null));
            }
            finally
            {
                logger.Dispose();
            }
        }

        [Test]
        public void SaveFrozen_Failure_RecorderUnchanged()
        {
            TraceFlightRecorder recorder = MakeFrozenRecorder(2, 1, out TraceLogger logger);
            try
            {
                string dir = CreateTempDir();
                try
                {
                    string bundlePath = Path.Combine(dir, "bundle");
                    Directory.CreateDirectory(bundlePath); // existing destination

                    TraceFlightRecorderState stateBefore = recorder.State;
                    int triggerBefore = recorder.TriggerHistoryCount;
                    int postBefore = recorder.CapturedPostRollCount;
                    int capturedBefore = recorder.CapturedCount;
                    long[] eventsBefore = SnapshotTimestamps(recorder.CreateFrozenSnapshot());

                    Assert.Throws<IOException>(
                        () => TraceRunBundleExporter.SaveFrozenAtomic(bundlePath, recorder, MakeContext()));

                    Assert.That(recorder.State, Is.EqualTo(stateBefore));
                    Assert.That(recorder.TriggerHistoryCount, Is.EqualTo(triggerBefore));
                    Assert.That(recorder.CapturedPostRollCount, Is.EqualTo(postBefore));
                    Assert.That(recorder.CapturedCount, Is.EqualTo(capturedBefore));
                    Assert.That(SnapshotTimestamps(recorder.CreateFrozenSnapshot()), Is.EqualTo(eventsBefore));
                }
                finally
                {
                    DeleteDir(dir);
                }
            }
            finally
            {
                logger.Dispose();
            }
        }

        [Test]
        public void SaveFrozen_RetryAfterFailure_Succeeds()
        {
            TraceFlightRecorder recorder = MakeFrozenRecorder(1, 0, out TraceLogger logger);
            try
            {
                string dir = CreateTempDir();
                try
                {
                    string existing = Path.Combine(dir, "bundle");
                    Directory.CreateDirectory(existing);

                    Assert.Throws<IOException>(
                        () => TraceRunBundleExporter.SaveFrozenAtomic(existing, recorder, MakeContext()));

                    string retryPath = Path.Combine(dir, "bundle-2");
                    TraceRunManifest manifest = TraceRunBundleExporter.SaveFrozenAtomic(retryPath, recorder, MakeContext());

                    Assert.That(manifest.EventCount, Is.EqualTo(1));
                    Assert.That(TraceRunBundleStore.Load(retryPath, int.MaxValue).Snapshot.EventCount, Is.EqualTo(1));
                }
                finally
                {
                    DeleteDir(dir);
                }
            }
            finally
            {
                logger.Dispose();
            }
        }

        [Test]
        public void SaveFrozen_AfterLoggerDispose_Succeeds()
        {
            TraceLogger logger;
            TraceFlightRecorder recorder = MakeFrozenRecorder(1, 0, out logger);
            logger.Dispose(); // dispose the logger before saving

            string dir = CreateTempDir();
            try
            {
                string bundlePath = Path.Combine(dir, "bundle");
                TraceRunManifest manifest = TraceRunBundleExporter.SaveFrozenAtomic(bundlePath, recorder, MakeContext());

                Assert.That(manifest.EventCount, Is.EqualTo(1));
                Assert.That(TraceRunBundleStore.Load(bundlePath, int.MaxValue).Snapshot.EventCount, Is.EqualTo(1));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void SaveFrozen_ReturnedManifestMatchesReload()
        {
            TraceFlightRecorder recorder = MakeFrozenRecorder(2, 1, out TraceLogger logger);
            try
            {
                string dir = CreateTempDir();
                try
                {
                    string bundlePath = Path.Combine(dir, "bundle");
                    TraceRunManifest manifest = TraceRunBundleExporter.SaveFrozenAtomic(bundlePath, recorder, MakeContext());

                    TraceRunBundle bundle = TraceRunBundleStore.Load(bundlePath, int.MaxValue);

                    AssertManifestsEqual(manifest, bundle.Manifest);
                }
                finally
                {
                    DeleteDir(dir);
                }
            }
            finally
            {
                logger.Dispose();
            }
        }

        [Test]
        public void SaveFrozen_Success_RecorderCanResnapshotUnchanged()
        {
            TraceFlightRecorder recorder = MakeFrozenRecorder(2, 2, out TraceLogger logger);
            try
            {
                long[] before = SnapshotTimestamps(recorder.CreateFrozenSnapshot());

                string dir = CreateTempDir();
                try
                {
                    string bundlePath = Path.Combine(dir, "bundle");
                    TraceRunBundleExporter.SaveFrozenAtomic(bundlePath, recorder, MakeContext());

                    Assert.That(SnapshotTimestamps(recorder.CreateFrozenSnapshot()), Is.EqualTo(before));
                }
                finally
                {
                    DeleteDir(dir);
                }
            }
            finally
            {
                logger.Dispose();
            }
        }
    }
}
