using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceBinaryFileStoreTests
    {
        private static readonly byte[] Magic = { (byte)'Z', (byte)'T', (byte)'R', (byte)'C', (byte)'E', (byte)'V', (byte)'T', (byte)'1' };

        private static string CreateTempDir()
        {
            string dir = Path.Combine(Path.GetTempPath(), "zantetsu-file-store", Guid.NewGuid().ToString("N"));
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
                // best-effort cleanup
            }
        }

        private static TraceEvent MakeEvent(long timestamp)
        {
            return new TraceEvent { Timestamp = timestamp };
        }

        [Test]
        public void SaveLoad_EmptyTrace()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "empty.bin");
                TraceBinaryFileStore.SaveAtomic(path, new TraceEvent[0], 0, 0);

                TraceEvent[] result = TraceBinaryFileStore.Load(path, int.MaxValue);

                Assert.That(result, Is.Empty);
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void SaveLoad_SingleEvent()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "single.bin");
                TraceEvent source = MakeEvent(42);
                TraceBinaryFileStore.SaveAtomic(path, new[] { source }, 0, 1);

                TraceEvent[] result = TraceBinaryFileStore.Load(path, int.MaxValue);

                Assert.That(result.Length, Is.EqualTo(1));
                Assert.That(result[0].Timestamp, Is.EqualTo(42));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void SaveLoad_MultipleEvents()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "multi.bin");
                TraceEvent[] source = { MakeEvent(1), MakeEvent(2), MakeEvent(3) };
                TraceBinaryFileStore.SaveAtomic(path, source, 0, source.Length);

                TraceEvent[] result = TraceBinaryFileStore.Load(path, int.MaxValue);

                Assert.That(result.Length, Is.EqualTo(3));
                for (int i = 0; i < 3; i++)
                {
                    Assert.That(result[i].Timestamp, Is.EqualTo(i + 1));
                }
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_PartialRange()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "partial.bin");
                TraceEvent[] source = { MakeEvent(1), MakeEvent(2), MakeEvent(3), MakeEvent(4) };
                TraceBinaryFileStore.SaveAtomic(path, source, 1, 2);

                TraceEvent[] result = TraceBinaryFileStore.Load(path, int.MaxValue);

                Assert.That(result.Length, Is.EqualTo(2));
                Assert.That(result[0].Timestamp, Is.EqualTo(2));
                Assert.That(result[1].Timestamp, Is.EqualTo(3));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void SaveLoad_All22Fields_SpecialDoubles_UnknownEnums()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "full.bin");

                double nanPayload = BitConverter.Int64BitsToDouble(0x7FF8000000000001L);
                TraceEvent source = new TraceEvent
                {
                    Timestamp = -1,
                    FrameId = -2,
                    FixedStepId = -3,
                    ThreadId = -4,
                    SlashId = -5,
                    SlashGeneration = 6,
                    FrontEdgeId = -7,
                    ObjectId = -8,
                    ObjectGeneration = 9,
                    MobId = -10,
                    PlanGeneration = 11,
                    TaskId = -12,
                    CaptureFrameId = -13,
                    OpenXRFrameId = -14,
                    TestRunId = -15,
                    EventType = (TraceEventType)9999,
                    TaskType = (TraceTaskType)12345,
                    FromState = -16,
                    ToState = -17,
                    Reason = (TraceReason)678,
                    Value0 = -0.0,
                    Value1 = nanPayload,
                };

                TraceBinaryFileStore.SaveAtomic(path, new[] { source }, 0, 1);

                TraceEvent[] result = TraceBinaryFileStore.Load(path, int.MaxValue);
                Assert.That(result.Length, Is.EqualTo(1));

                TraceEvent actual = result[0];
                Assert.That(actual.Timestamp, Is.EqualTo(source.Timestamp));
                Assert.That(actual.FrameId, Is.EqualTo(source.FrameId));
                Assert.That(actual.FixedStepId, Is.EqualTo(source.FixedStepId));
                Assert.That(actual.ThreadId, Is.EqualTo(source.ThreadId));
                Assert.That(actual.SlashId, Is.EqualTo(source.SlashId));
                Assert.That(actual.SlashGeneration, Is.EqualTo(source.SlashGeneration));
                Assert.That(actual.FrontEdgeId, Is.EqualTo(source.FrontEdgeId));
                Assert.That(actual.ObjectId, Is.EqualTo(source.ObjectId));
                Assert.That(actual.ObjectGeneration, Is.EqualTo(source.ObjectGeneration));
                Assert.That(actual.MobId, Is.EqualTo(source.MobId));
                Assert.That(actual.PlanGeneration, Is.EqualTo(source.PlanGeneration));
                Assert.That(actual.TaskId, Is.EqualTo(source.TaskId));
                Assert.That(actual.CaptureFrameId, Is.EqualTo(source.CaptureFrameId));
                Assert.That(actual.OpenXRFrameId, Is.EqualTo(source.OpenXRFrameId));
                Assert.That(actual.TestRunId, Is.EqualTo(source.TestRunId));
                Assert.That((int)actual.EventType, Is.EqualTo(9999));
                Assert.That((int)actual.TaskType, Is.EqualTo(12345));
                Assert.That(actual.FromState, Is.EqualTo(source.FromState));
                Assert.That(actual.ToState, Is.EqualTo(source.ToState));
                Assert.That((int)actual.Reason, Is.EqualTo(678));
                Assert.That(BitConverter.DoubleToInt64Bits(actual.Value0), Is.EqualTo(BitConverter.DoubleToInt64Bits(-0.0)));
                Assert.That(BitConverter.DoubleToInt64Bits(actual.Value1), Is.EqualTo(BitConverter.DoubleToInt64Bits(nanPayload)));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void FinalFile_HasMagicAndCorrectLength()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "check.bin");
                TraceBinaryFileStore.SaveAtomic(path, new[] { MakeEvent(1), MakeEvent(2) }, 0, 2);

                byte[] bytes = File.ReadAllBytes(path);

                Assert.That(bytes.Length, Is.EqualTo(32 + 2 * 140));
                byte[] magicBytes = new byte[8];
                Array.Copy(bytes, 0, magicBytes, 0, 8);
                Assert.That(magicBytes, Is.EqualTo(Magic));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_Success_NoTempFilesLeftBehind()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "clean.bin");
                TraceBinaryFileStore.SaveAtomic(path, new[] { MakeEvent(1) }, 0, 1);

                string[] files = Directory.GetFiles(dir);
                Assert.That(files.Length, Is.EqualTo(1));
                Assert.That(Path.GetFullPath(files[0]), Is.EqualTo(Path.GetFullPath(path)));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_NullPath_Throws()
        {
            Assert.Throws<ArgumentNullException>(
                () => TraceBinaryFileStore.SaveAtomic(null, new TraceEvent[0], 0, 0));
        }

        [Test]
        public void Save_EmptyPath_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => TraceBinaryFileStore.SaveAtomic("", new TraceEvent[0], 0, 0));
        }

        [Test]
        public void Save_WhitespacePath_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => TraceBinaryFileStore.SaveAtomic("   ", new TraceEvent[0], 0, 0));
        }

        [Test]
        public void Save_RelativePath_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => TraceBinaryFileStore.SaveAtomic("relative/file.bin", new TraceEvent[0], 0, 0));
        }

        [Test]
        public void Save_DriveRelativePath_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => TraceBinaryFileStore.SaveAtomic("C:relative.bin", new TraceEvent[0], 0, 0));
        }

        [Test]
        public void Save_CurrentDriveRootedPath_Throws()
        {
            Assert.Throws<ArgumentException>(
                () => TraceBinaryFileStore.SaveAtomic("\\rooted.bin", new TraceEvent[0], 0, 0));
        }

        [Test]
        public void Save_FullyQualifiedWithDotDot_Normalizes()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "sub", "..", "trace.bin");
                TraceBinaryFileStore.SaveAtomic(path, new[] { MakeEvent(1) }, 0, 1);

                string normalized = Path.GetFullPath(path);
                Assert.That(File.Exists(normalized), Is.True);
                Assert.That(TraceBinaryFileStore.Load(normalized, int.MaxValue)[0].Timestamp, Is.EqualTo(1));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void ValidatePath_UncAbsolutePath_IsAccepted()
        {
            MethodInfo method = typeof(TraceBinaryFileStore).GetMethod(
                "ValidatePath", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);

            object[] args = { @"\\server\share\file.bin", null };
            Assert.DoesNotThrow(() => method.Invoke(null, args));
            Assert.That(args[1], Is.EqualTo(@"\\server\share\file.bin"));
        }

        [Test]
        public void Save_NullEvents_Throws()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "null.bin");
                Assert.Throws<ArgumentNullException>(
                    () => TraceBinaryFileStore.SaveAtomic(path, null, 0, 0));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_NegativeSourceIndex_Throws()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "neg.bin");
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => TraceBinaryFileStore.SaveAtomic(path, new[] { MakeEvent(1) }, -1, 1));
                Assert.That(File.Exists(path), Is.False);
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_NegativeCount_Throws()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "negcount.bin");
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => TraceBinaryFileStore.SaveAtomic(path, new[] { MakeEvent(1) }, 0, -1));
                Assert.That(File.Exists(path), Is.False);
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_RangeExceeds_Throws()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "range.bin");
                Assert.Throws<ArgumentException>(
                    () => TraceBinaryFileStore.SaveAtomic(path, new[] { MakeEvent(1), MakeEvent(2) }, 1, 2));
                Assert.That(File.Exists(path), Is.False);
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_MissingParentDirectory_DoesNotCreate()
        {
            string dir = CreateTempDir();
            try
            {
                string missing = Path.Combine(dir, "missing");
                string path = Path.Combine(missing, "trace.bin");

                Assert.Throws<DirectoryNotFoundException>(
                    () => TraceBinaryFileStore.SaveAtomic(path, new[] { MakeEvent(1) }, 0, 1));
                Assert.That(Directory.Exists(missing), Is.False);
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_ExistingDestination_DoesNotOverwrite()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "existing.bin");
                File.WriteAllText(path, "ORIGINAL CONTENT");

                Assert.Throws<IOException>(
                    () => TraceBinaryFileStore.SaveAtomic(path, new[] { MakeEvent(1) }, 0, 1));

                Assert.That(File.ReadAllText(path), Is.EqualTo("ORIGINAL CONTENT"));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_ExistingDestination_BytesUnchanged()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "existing.bin");
                byte[] original = { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x01, 0x02, 0x03 };
                File.WriteAllBytes(path, original);

                Assert.Throws<IOException>(
                    () => TraceBinaryFileStore.SaveAtomic(path, new[] { MakeEvent(1) }, 0, 1));

                byte[] after = File.ReadAllBytes(path);
                Assert.That(after, Is.EqualTo(original));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_DestinationIsDirectory_Throws()
        {
            string dir = CreateTempDir();
            try
            {
                string subDir = Path.Combine(dir, "subdir");
                Directory.CreateDirectory(subDir);

                Assert.Throws<IOException>(
                    () => TraceBinaryFileStore.SaveAtomic(subDir, new[] { MakeEvent(1) }, 0, 1));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_NonexistentFile_ThrowsFileNotFound()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "does-not-exist.bin");

                Assert.Throws<FileNotFoundException>(
                    () => TraceBinaryFileStore.Load(path, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_NegativeMaximumEventCount_ThrowsBeforeOpen()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "does-not-exist.bin");

                Assert.Throws<ArgumentOutOfRangeException>(
                    () => TraceBinaryFileStore.Load(path, -1));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_EventCountExceedsMaximum_ThrowsInvalidData()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "overflow.bin");
                TraceBinaryFileStore.SaveAtomic(path, new[] { MakeEvent(1), MakeEvent(2), MakeEvent(3) }, 0, 3);

                Assert.Throws<InvalidDataException>(
                    () => TraceBinaryFileStore.Load(path, 1));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_MagicCorrupt_ThrowsInvalidData()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "magic.bin");
                TraceBinaryFileStore.SaveAtomic(path, new[] { MakeEvent(1) }, 0, 1);

                byte[] bytes = File.ReadAllBytes(path);
                bytes[0] = (byte)'X';
                File.WriteAllBytes(path, bytes);

                Assert.Throws<InvalidDataException>(
                    () => TraceBinaryFileStore.Load(path, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_MidRecordEof_ThrowsInvalidData()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "eof.bin");
                TraceBinaryFileStore.SaveAtomic(path, new[] { MakeEvent(1) }, 0, 1);

                byte[] bytes = File.ReadAllBytes(path);
                byte[] truncated = new byte[100];
                Array.Copy(bytes, truncated, 100);
                File.WriteAllBytes(path, truncated);

                Assert.Throws<InvalidDataException>(
                    () => TraceBinaryFileStore.Load(path, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_TrailingByte_ThrowsInvalidData()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "trailing.bin");
                TraceBinaryFileStore.SaveAtomic(path, new[] { MakeEvent(1) }, 0, 1);

                byte[] bytes = File.ReadAllBytes(path);
                byte[] withTrailing = new byte[bytes.Length + 1];
                Array.Copy(bytes, withTrailing, bytes.Length);
                withTrailing[bytes.Length] = 0xAA;
                File.WriteAllBytes(path, withTrailing);

                Assert.Throws<InvalidDataException>(
                    () => TraceBinaryFileStore.Load(path, int.MaxValue));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Load_ReleasesFileHandle()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "handle.bin");
                TraceBinaryFileStore.SaveAtomic(path, new[] { MakeEvent(1) }, 0, 1);

                TraceEvent[] result = TraceBinaryFileStore.Load(path, int.MaxValue);
                Assert.That(result.Length, Is.EqualTo(1));

                // Handle must be released: rename and delete both succeed.
                string moved = path + ".moved";
                File.Move(path, moved);
                File.Delete(moved);
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_ReleasesFileHandle()
        {
            string dir = CreateTempDir();
            try
            {
                string path = Path.Combine(dir, "savehandle.bin");
                TraceBinaryFileStore.SaveAtomic(path, new[] { MakeEvent(1) }, 0, 1);

                // Final file must be immediately loadable (no lingering handle).
                TraceEvent[] result = TraceBinaryFileStore.Load(path, int.MaxValue);
                Assert.That(result.Length, Is.EqualTo(1));
                Assert.That(result[0].Timestamp, Is.EqualTo(1));
            }
            finally
            {
                DeleteDir(dir);
            }
        }

        [Test]
        public void Save_ConsecutiveDifferentPaths_NoTempCollision()
        {
            string dir = CreateTempDir();
            try
            {
                string first = Path.Combine(dir, "first.bin");
                string second = Path.Combine(dir, "second.bin");

                TraceBinaryFileStore.SaveAtomic(first, new[] { MakeEvent(1) }, 0, 1);
                TraceBinaryFileStore.SaveAtomic(second, new[] { MakeEvent(2) }, 0, 1);

                string[] files = Directory.GetFiles(dir);
                Assert.That(files.Length, Is.EqualTo(2));

                Assert.That(TraceBinaryFileStore.Load(first, int.MaxValue)[0].Timestamp, Is.EqualTo(1));
                Assert.That(TraceBinaryFileStore.Load(second, int.MaxValue)[0].Timestamp, Is.EqualTo(2));
            }
            finally
            {
                DeleteDir(dir);
            }
        }
    }
}
