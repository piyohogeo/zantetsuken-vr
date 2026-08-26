using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceRunBundleStoreTests
    {
        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

        private static TraceRunContext MakeContext()
        {
            string sha64 = new string('a', 64);
            return new TraceRunContext(1, 0, "build", "6000.3.22f1", sha64, "scene", 0, 0.02, 0, "High", 1, new Vector3(0f, -9.81f, 0f));
        }

        private sealed class BundleData
        {
            public TraceCaptureSnapshot Snapshot;
            public TraceRunManifest Manifest;
        }

        private static TraceCaptureSnapshot MakeSnapshot(int historyCount, int postRollCount, bool wrapped)
        {
            int capacity = wrapped ? Math.Max(1, historyCount) : historyCount + 1;
            TraceLogger logger = new TraceLogger(capacity);
            TraceFlightRecorder recorder = new TraceFlightRecorder(logger, postRollCount);

            int enqueueCount = wrapped ? historyCount + 1 : historyCount;
            for (int i = 1; i <= enqueueCount; i++)
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

            TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();
            logger.Dispose();
            return snapshot;
        }

        private static BundleData MakeBundle(int historyCount, int postRollCount, bool wrapped)
        {
            TraceCaptureSnapshot snapshot = MakeSnapshot(historyCount, postRollCount, wrapped);
            return new BundleData { Snapshot = snapshot, Manifest = TraceRunManifest.Create(snapshot, MakeContext()) };
        }

        private static TraceEvent FullEvent()
        {
            return new TraceEvent
            {
                Timestamp = 11,
                FrameId = 22,
                FixedStepId = 33,
                ThreadId = 44,
                SlashId = 55,
                SlashGeneration = 66,
                FrontEdgeId = 77,
                ObjectId = 88,
                ObjectGeneration = 99,
                MobId = 111,
                PlanGeneration = 222,
                TaskId = 333,
                CaptureFrameId = 444,
                OpenXRFrameId = 555,
                TestRunId = 666,
                EventType = (TraceEventType)777,
                TaskType = (TraceTaskType)888,
                FromState = 999,
                ToState = 1000,
                Reason = (TraceReason)1111,
                Value0 = -0.0,
                Value1 = BitConverter.Int64BitsToDouble(0x7FF8000000000001L),
            };
        }

        private static BundleData MakeFullFieldBundle()
        {
            TraceLogger logger = new TraceLogger(4);
            TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);
            logger.Enqueue(FullEvent());
            logger.Drain();
            recorder.TryTrigger();
            TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();
            TraceRunManifest manifest = TraceRunManifest.Create(snapshot, MakeContext());
            logger.Dispose();
            return new BundleData { Snapshot = snapshot, Manifest = manifest };
        }

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsu-bundle", Guid.NewGuid().ToString("N"));
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

        private static string SaveBundle(string parentDir, TraceCaptureSnapshot snapshot, TraceRunManifest manifest)
        {
            string bundlePath = Path.Combine(parentDir, "bundle");
            TraceRunBundleStore.SaveAtomic(bundlePath, snapshot, manifest);
            return bundlePath;
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                const string hex = "0123456789abcdef";
                char[] chars = new char[hash.Length * 2];
                for (int i = 0; i < hash.Length; i++)
                {
                    chars[i * 2] = hex[hash[i] >> 4];
                    chars[i * 2 + 1] = hex[hash[i] & 0x0F];
                }

                return new string(chars);
            }
        }

        private static string[] ReadIndexLines(string bundlePath)
        {
            string text = Utf8NoBom.GetString(File.ReadAllBytes(Path.Combine(bundlePath, TraceRunBundleFormat.IndexFileName)));
            return text.Split('\n');
        }

        // --- Save / Load round-trip ---

        [Test]
        public void SaveLoad_EmptyBundle()
        {
            string dir = CreateTempDir();
            try
            {
                BundleData data = MakeBundle(0, 0, false);
                string bundlePath = SaveBundle(dir, data.Snapshot, data.Manifest);

                TraceRunBundle bundle = TraceRunBundleStore.Load(bundlePath, int.MaxValue);

                Assert.That(bundle.Snapshot.EventCount, Is.EqualTo(0));
                Assert.That(bundle.Manifest.EventCount, Is.EqualTo(0));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void SaveLoad_WithPreAndPostRollEvents()
        {
            string dir = CreateTempDir();
            try
            {
                BundleData data = MakeBundle(2, 3, false);
                string bundlePath = SaveBundle(dir, data.Snapshot, data.Manifest);

                TraceRunBundle bundle = TraceRunBundleStore.Load(bundlePath, int.MaxValue);

                Assert.That(bundle.Snapshot.EventCount, Is.EqualTo(5));
                Assert.That(bundle.Snapshot.TriggerHistoryCount, Is.EqualTo(2));
                Assert.That(bundle.Snapshot.CapturedPostRollCount, Is.EqualTo(3));

                long[] timestamps = new long[5];
                for (int i = 0; i < 5; i++)
                {
                    timestamps[i] = bundle.Snapshot.GetEvent(i).Timestamp;
                }

                Assert.That(timestamps, Is.EqualTo(new long[] { 1, 2, 1000, 1001, 1002 }));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void SaveLoad_All22FieldsAndManifest()
        {
            string dir = CreateTempDir();
            try
            {
                BundleData data = MakeFullFieldBundle();
                string bundlePath = SaveBundle(dir, data.Snapshot, data.Manifest);

                TraceRunBundle bundle = TraceRunBundleStore.Load(bundlePath, int.MaxValue);

                Assert.That(bundle.Manifest.TestRunId, Is.EqualTo(data.Manifest.TestRunId));
                Assert.That(bundle.Manifest.BuildId, Is.EqualTo("build"));
                Assert.That(bundle.Snapshot.EventCount, Is.EqualTo(1));

                TraceEvent e = bundle.Snapshot.GetEvent(0);
                Assert.That(e.Timestamp, Is.EqualTo(11));
                Assert.That(e.FrameId, Is.EqualTo(22));
                Assert.That(e.FixedStepId, Is.EqualTo(33));
                Assert.That(e.ThreadId, Is.EqualTo(44));
                Assert.That(e.SlashId, Is.EqualTo(55));
                Assert.That(e.SlashGeneration, Is.EqualTo(66));
                Assert.That(e.FrontEdgeId, Is.EqualTo(77));
                Assert.That(e.ObjectId, Is.EqualTo(88));
                Assert.That(e.ObjectGeneration, Is.EqualTo(99));
                Assert.That(e.MobId, Is.EqualTo(111));
                Assert.That(e.PlanGeneration, Is.EqualTo(222));
                Assert.That(e.TaskId, Is.EqualTo(333));
                Assert.That(e.CaptureFrameId, Is.EqualTo(444));
                Assert.That(e.OpenXRFrameId, Is.EqualTo(555));
                Assert.That(e.TestRunId, Is.EqualTo(666));
                Assert.That((int)e.EventType, Is.EqualTo(777));
                Assert.That((int)e.TaskType, Is.EqualTo(888));
                Assert.That(e.FromState, Is.EqualTo(999));
                Assert.That(e.ToState, Is.EqualTo(1000));
                Assert.That((int)e.Reason, Is.EqualTo(1111));
                Assert.That(BitConverter.DoubleToInt64Bits(e.Value0), Is.EqualTo(BitConverter.DoubleToInt64Bits(-0.0)));
                Assert.That(BitConverter.DoubleToInt64Bits(e.Value1), Is.EqualTo(BitConverter.DoubleToInt64Bits(BitConverter.Int64BitsToDouble(0x7FF8000000000001L))));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        // --- Structure ---

        [Test]
        public void Save_CreatesExactlyThreeFiles()
        {
            string dir = CreateTempDir();
            try
            {
                BundleData data = MakeBundle(0, 0, false);
                string bundlePath = SaveBundle(dir, data.Snapshot, data.Manifest);

                string[] files = Directory.GetFiles(bundlePath);
                Assert.That(files.Length, Is.EqualTo(3));
                Assert.That(Directory.GetDirectories(bundlePath).Length, Is.EqualTo(0));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Index_GoldenAsciiFormat_LfOnly_NoBom()
        {
            string dir = CreateTempDir();
            try
            {
                BundleData data = MakeBundle(0, 0, false);
                string bundlePath = SaveBundle(dir, data.Snapshot, data.Manifest);

                byte[] bytes = File.ReadAllBytes(Path.Combine(bundlePath, TraceRunBundleFormat.IndexFileName));

                Assert.That(bytes[0], Is.Not.EqualTo(0xEF)); // no BOM
                string text = Utf8NoBom.GetString(bytes);
                Assert.That(text, Does.Not.Contain("\r"));

                string[] lines = text.Split('\n');
                Assert.That(lines.Length, Is.EqualTo(4));
                Assert.That(lines[3], Is.Empty); // trailing LF on final line

                Assert.That(lines[0], Is.EqualTo("ZANTETSU_TRACE_BUNDLE 1"));
                Assert.That(lines[1], Does.StartWith("manifest.json "));
                Assert.That(lines[2], Does.StartWith("trace.bin "));

                // Each file line has exactly three space-separated fields.
                Assert.That(lines[1].Split(' ').Length, Is.EqualTo(3));
                Assert.That(lines[2].Split(' ').Length, Is.EqualTo(3));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Index_LengthsMatchActualFiles()
        {
            string dir = CreateTempDir();
            try
            {
                BundleData data = MakeBundle(2, 1, false);
                string bundlePath = SaveBundle(dir, data.Snapshot, data.Manifest);

                string[] lines = ReadIndexLines(bundlePath);
                string[] manifestParts = lines[1].Split(' ');
                string[] traceParts = lines[2].Split(' ');

                long actualManifestLength = new FileInfo(Path.Combine(bundlePath, TraceRunBundleFormat.ManifestFileName)).Length;
                long actualTraceLength = new FileInfo(Path.Combine(bundlePath, TraceRunBundleFormat.TraceFileName)).Length;

                Assert.That(long.Parse(manifestParts[1]), Is.EqualTo(actualManifestLength));
                Assert.That(long.Parse(traceParts[1]), Is.EqualTo(actualTraceLength));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Index_HashesMatchIndependentSha256()
        {
            string dir = CreateTempDir();
            try
            {
                BundleData data = MakeBundle(2, 1, false);
                string bundlePath = SaveBundle(dir, data.Snapshot, data.Manifest);

                string[] lines = ReadIndexLines(bundlePath);
                string[] manifestParts = lines[1].Split(' ');
                string[] traceParts = lines[2].Split(' ');

                string manifestHash = Sha256Hex(File.ReadAllBytes(Path.Combine(bundlePath, TraceRunBundleFormat.ManifestFileName)));
                string traceHash = Sha256Hex(File.ReadAllBytes(Path.Combine(bundlePath, TraceRunBundleFormat.TraceFileName)));

                Assert.That(manifestParts[2], Is.EqualTo(manifestHash));
                Assert.That(traceParts[2], Is.EqualTo(traceHash));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Index_ManifestHashMatchesCodecHash()
        {
            string dir = CreateTempDir();
            try
            {
                BundleData data = MakeBundle(0, 0, false);
                string bundlePath = SaveBundle(dir, data.Snapshot, data.Manifest);

                string[] lines = ReadIndexLines(bundlePath);
                string indexManifestHash = lines[1].Split(' ')[2];

                Assert.That(indexManifestHash, Is.EqualTo(TraceRunManifestCodec.ComputeContentSha256(data.Manifest)));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_NoTempDirectoryLeftBehind()
        {
            string dir = CreateTempDir();
            try
            {
                BundleData data = MakeBundle(0, 0, false);
                SaveBundle(dir, data.Snapshot, data.Manifest);

                Assert.That(Directory.GetDirectories(dir).Length, Is.EqualTo(1)); // only the final bundle
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_ExistingDirectory_NotOverwritten()
        {
            string dir = CreateTempDir();
            try
            {
                BundleData data = MakeBundle(0, 0, false);
                string bundlePath = Path.Combine(dir, "bundle");
                Directory.CreateDirectory(bundlePath);
                File.WriteAllText(Path.Combine(bundlePath, "sentinel"), "original");

                Assert.Throws<IOException>(() => TraceRunBundleStore.SaveAtomic(bundlePath, data.Snapshot, data.Manifest));
                Assert.That(File.ReadAllText(Path.Combine(bundlePath, "sentinel")), Is.EqualTo("original"));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_ExistingFile_NotOverwritten()
        {
            string dir = CreateTempDir();
            try
            {
                BundleData data = MakeBundle(0, 0, false);
                string bundlePath = Path.Combine(dir, "bundle");
                File.WriteAllText(bundlePath, "original");

                Assert.Throws<IOException>(() => TraceRunBundleStore.SaveAtomic(bundlePath, data.Snapshot, data.Manifest));
                Assert.That(File.ReadAllText(bundlePath), Is.EqualTo("original"));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_MetadataMismatch_RejectedBeforeFileCreation()
        {
            string dir = CreateTempDir();
            try
            {
                TraceCaptureSnapshot snapshot = MakeSnapshot(1, 0, false);
                // Manifest built from a DIFFERENT (empty) snapshot.
                TraceRunManifest manifest = TraceRunManifest.Create(MakeSnapshot(0, 0, false), MakeContext());
                string bundlePath = Path.Combine(dir, "bundle");

                Assert.Throws<ArgumentException>(() => TraceRunBundleStore.SaveAtomic(bundlePath, snapshot, manifest));
                Assert.That(Directory.Exists(bundlePath), Is.False);
                Assert.That(Directory.GetDirectories(dir).Length, Is.EqualTo(0));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_Failure_CleansUpTempDirectory()
        {
            string dir = CreateTempDir();
            try
            {
                // Huge buildId passes metadata checks but overflows the
                // canonical size limit during serialization, failing after the
                // temporary directory has been created.
                TraceCaptureSnapshot snapshot = MakeSnapshot(0, 0, false);
                TraceRunContext context = new TraceRunContext(
                    1, 0, new string('x', 70000), "6000.3.22f1", new string('a', 64),
                    "scene", 0, 0.02, 0, "High", 1, new Vector3(0f, -9.81f, 0f));
                TraceRunManifest manifest = TraceRunManifest.Create(snapshot, context);
                string bundlePath = Path.Combine(dir, "bundle");

                Assert.Throws<InvalidOperationException>(() => TraceRunBundleStore.SaveAtomic(bundlePath, snapshot, manifest));
                Assert.That(Directory.Exists(bundlePath), Is.False);
                Assert.That(Directory.GetDirectories(dir).Length, Is.EqualTo(0)); // temp dir cleaned up
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        // --- Load rejections ---

        private static string SaveAndGetPath(string dir, BundleData data)
        {
            return SaveBundle(dir, data.Snapshot, data.Manifest);
        }

        [Test]
        public void Load_MissingFile_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(0, 0, false));
                File.Delete(Path.Combine(bundlePath, TraceRunBundleFormat.ManifestFileName));

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_ExtraFile_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(0, 0, false));
                File.WriteAllText(Path.Combine(bundlePath, "extra.txt"), "x");

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_ExtraDirectory_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(0, 0, false));
                Directory.CreateDirectory(Path.Combine(bundlePath, "extra-dir"));

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_IndexMagicCorrupt_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(0, 0, false));
                RewriteIndex(bundlePath, "NOT_A_TRACE_BUNDLE 1\n");

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_IndexVersionCorrupt_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(0, 0, false));
                RewriteIndex(bundlePath, "ZANTETSU_TRACE_BUNDLE 2\n");

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_IndexLineOrderCorrupt_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(0, 0, false));
                string[] lines = ReadIndexLines(bundlePath);
                string swapped = lines[0] + "\n" + lines[2] + "\n" + lines[1] + "\n";
                RewriteIndex(bundlePath, swapped);

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_IndexExtraWhitespace_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(0, 0, false));
                string[] lines = ReadIndexLines(bundlePath);
                string spaced = lines[0] + "\n" + lines[1].Replace(" ", "  ") + "\n" + lines[2] + "\n";
                RewriteIndex(bundlePath, spaced);

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_IndexCrlf_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(0, 0, false));
                string[] lines = ReadIndexLines(bundlePath);
                string crlf = lines[0] + "\r\n" + lines[1] + "\r\n" + lines[2] + "\r\n";
                File.WriteAllBytes(Path.Combine(bundlePath, TraceRunBundleFormat.IndexFileName), Utf8NoBom.GetBytes(crlf));

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_IndexLengthCorrupt_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(0, 0, false));
                string[] lines = ReadIndexLines(bundlePath);
                string[] parts = lines[1].Split(' ');
                parts[1] = "999999999";
                RewriteIndex(bundlePath, lines[0] + "\n" + string.Join(" ", parts) + "\n" + lines[2] + "\n");

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_IndexHashCorrupt_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(0, 0, false));
                string[] lines = ReadIndexLines(bundlePath);
                string[] parts = lines[1].Split(' ');
                parts[2] = new string('0', 64);
                RewriteIndex(bundlePath, lines[0] + "\n" + string.Join(" ", parts) + "\n" + lines[2] + "\n");

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_ManifestHashCorrupt_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(0, 0, false));
                string manifestPath = Path.Combine(bundlePath, TraceRunBundleFormat.ManifestFileName);
                byte[] bytes = File.ReadAllBytes(manifestPath);
                bytes[0] ^= 0x01;
                File.WriteAllBytes(manifestPath, bytes);

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_ManifestNonCanonical_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                BundleData data = MakeBundle(0, 0, false);
                string bundlePath = SaveBundle(dir, data.Snapshot, data.Manifest);

                // Non-canonical manifest.json with an added space.
                byte[] canonical = TraceRunManifestCodec.SerializeCanonical(data.Manifest);
                string json = Utf8NoBom.GetString(canonical).Replace("{\"schemaVersion\":", "{\"schemaVersion\" : ");
                File.WriteAllBytes(Path.Combine(bundlePath, TraceRunBundleFormat.ManifestFileName), Utf8NoBom.GetBytes(json));

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_TraceLengthCorrupt_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(1, 0, false));
                string tracePath = Path.Combine(bundlePath, TraceRunBundleFormat.TraceFileName);
                byte[] bytes = File.ReadAllBytes(tracePath);
                byte[] truncated = new byte[bytes.Length - 1];
                Array.Copy(bytes, truncated, truncated.Length);
                File.WriteAllBytes(tracePath, truncated);

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_TraceHashCorrupt_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(1, 0, false));
                string tracePath = Path.Combine(bundlePath, TraceRunBundleFormat.TraceFileName);
                byte[] bytes = File.ReadAllBytes(tracePath);
                bytes[32] ^= 0x01; // flip a byte inside the record area
                File.WriteAllBytes(tracePath, bytes);

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_TraceHeaderCorrupt_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(1, 0, false));
                string tracePath = Path.Combine(bundlePath, TraceRunBundleFormat.TraceFileName);
                byte[] bytes = File.ReadAllBytes(tracePath);
                bytes[0] = (byte)'X'; // corrupt the magic byte
                File.WriteAllBytes(tracePath, bytes);

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_MaximumEventCountExceeded_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(2, 0, false));

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, 1));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_ReleasesHandles()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(1, 0, false));
                TraceRunBundle bundle = TraceRunBundleStore.Load(bundlePath, int.MaxValue);
                Assert.That(bundle.Snapshot.EventCount, Is.EqualTo(1));

                // Handles must be released: rename and delete both succeed.
                string moved = bundlePath + ".moved";
                Directory.Move(bundlePath, moved);
                Directory.Delete(moved, true);
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_IndexTooLarge_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(0, 0, false));
                byte[] large = new byte[513]; // MaximumIndexByteCount + 1
                for (int i = 0; i < large.Length; i++)
                {
                    large[i] = (byte)'a';
                }

                File.WriteAllBytes(Path.Combine(bundlePath, TraceRunBundleFormat.IndexFileName), large);

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_ManifestTooLarge_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(0, 0, false));
                string manifestPath = Path.Combine(bundlePath, TraceRunBundleFormat.ManifestFileName);
                int oversized = TraceRunManifestCodec.MaximumCanonicalByteCount + 1;
                File.WriteAllBytes(manifestPath, new byte[oversized]);

                string[] lines = ReadIndexLines(bundlePath);
                string newIndex = lines[0] + "\n" +
                    "manifest.json " + oversized + " " + new string('0', 64) + "\n" +
                    lines[2] + "\n";
                RewriteIndex(bundlePath, newIndex);

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_ManifestLengthMismatch_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(0, 0, false));
                string manifestPath = Path.Combine(bundlePath, TraceRunBundleFormat.ManifestFileName);
                byte[] bytes = File.ReadAllBytes(manifestPath);
                byte[] bigger = new byte[bytes.Length + 1];
                Array.Copy(bytes, bigger, bytes.Length);
                File.WriteAllBytes(manifestPath, bigger);

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_TraceLengthPlusOne_Rejected()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(1, 0, false));
                string tracePath = Path.Combine(bundlePath, TraceRunBundleFormat.TraceFileName);
                byte[] bytes = File.ReadAllBytes(tracePath);
                byte[] bigger = new byte[bytes.Length + 1];
                Array.Copy(bytes, bigger, bytes.Length);
                File.WriteAllBytes(tracePath, bigger);

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_LengthMismatch_CheckedBeforeHashMismatch()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(1, 0, false));
                string tracePath = Path.Combine(bundlePath, TraceRunBundleFormat.TraceFileName);
                byte[] bytes = File.ReadAllBytes(tracePath);
                byte[] truncated = new byte[bytes.Length - 1];
                Array.Copy(bytes, truncated, truncated.Length);
                File.WriteAllBytes(tracePath, truncated);

                InvalidDataException ex = Assert.Throws<InvalidDataException>(
                    () => TraceRunBundleStore.Load(bundlePath, int.MaxValue));

                Assert.That(ex.Message, Does.Contain("length"));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_Failure_ReleasesHandles()
        {
            string dir = CreateTempDir();
            try
            {
                string bundlePath = SaveAndGetPath(dir, MakeBundle(1, 0, false));
                string tracePath = Path.Combine(bundlePath, TraceRunBundleFormat.TraceFileName);
                byte[] bytes = File.ReadAllBytes(tracePath);
                bytes[0] ^= 0x01;
                File.WriteAllBytes(tracePath, bytes);

                Assert.Throws<InvalidDataException>(() => TraceRunBundleStore.Load(bundlePath, int.MaxValue));

                Directory.Delete(bundlePath, true);
                Assert.That(Directory.Exists(bundlePath), Is.False);
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Bundle_NoPublicSettersOrMutableCollections()
        {
            Type type = typeof(TraceRunBundle);

            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.Instance).Length, Is.EqualTo(0));

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(property.CanWrite, Is.False, "Public property has a setter: " + property.Name);
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(method.ReturnType.IsArray, Is.False, "Public method returns an array: " + method.Name);
            }
        }

        private static void RewriteIndex(string bundlePath, string content)
        {
            File.WriteAllBytes(Path.Combine(bundlePath, TraceRunBundleFormat.IndexFileName), Utf8NoBom.GetBytes(content));
        }
    }
}
