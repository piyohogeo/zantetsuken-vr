#if DEBUG
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core;

namespace Zantetsu.Core.Tests
{
    public sealed class DevelopmentLoggerTests
    {
        private string directory;
        private const string Commit = "0123456789abcdef0123456789abcdef01234567";

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "zantetsu-logger-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Start();
        }

        [TearDown]
        public void TearDown()
        {
            Call("StopSession");
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }

        [Test]
        public void ValuesAndTimestampKeepDigitsAndStringsUnderNonInvariantCulture()
        {
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                string record = (string)typeof(DevelopmentLogger).GetMethod("FormatRecord", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { 621355968000000000L + 17898765431234567L, 42,
                        "writer\"\\\n日本語", "free.tag/[x]", new object[] { ulong.MaxValue, 1.234567890123456789m, "line\r\n\t😀" } });
                StringAssert.StartsWith("{\"time\":1789876543.1234567,\"frame\":42,", record);
                StringAssert.Contains("18446744073709551615,1.234567890123456789,", record);
                StringAssert.Contains("line\\u000d\\u000a\\u0009\\ud83d\\ude00", record);
                StringAssert.Contains("\"tag\":\"free.tag/[x]\"", record);
                StringAssert.Contains("writer\\\"\\\\\\u000a日本語", record);
                Assert.That(record.Count(c => c == '\n'), Is.Zero);
            }
            finally { CultureInfo.CurrentCulture = previous; }
        }

        [Test]
        public void ParallelRecordsAreCompleteAndReadableWhileWriterRemainsOpen()
        {
            Call("SetFrame", 73);
            string path = CurrentPath();
            StringAssert.Contains(Commit, Path.GetFileName(path));
            Parallel.For(0, 100, i => DevelopmentLogger.Instance.write_log("worker-" + i, "parallel", i));
            WaitForLines(path, 100);
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(input, Encoding.UTF8))
            {
                string content = reader.ReadToEnd();
                Assert.That(content.EndsWith("\n"), Is.True);
                Assert.That(content.Contains("\r"), Is.False);
                string[] lines = content.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                Assert.That(lines.Length, Is.EqualTo(100));
                var values = lines.Select(line => JsonUtility.FromJson<Row>(line)).ToArray();
                Assert.That(values.Select(row => row.value).Distinct().Count(), Is.EqualTo(100));
                foreach (Row row in values)
                {
                    Assert.That(row.frame, Is.EqualTo(73));
                    Assert.That(row.writer_id, Is.EqualTo("worker-" + row.value));
                    Assert.That(row.tag, Is.EqualTo("parallel"));
                }
            }
            byte[] bytes = Encoding.UTF8.GetBytes(ReadText(path));
            Assert.That(bytes[0], Is.EqualTo((byte)'{'), "No UTF-8 BOM");
        }

        [Test]
        public void IteratorsRunSynchronouslyAndFailuresDoNotWritePartialRecords()
        {
            var iterator = new Values();
            DevelopmentLogger.Instance.write_log("test", "iterator", iterator);
            Assert.That(iterator.Disposed, Is.True);
            Assert.That(iterator.Moves, Is.EqualTo(3));
            DevelopmentLogger.Instance.write_log("test", "bad", new object[] { 1, double.NaN });
            DevelopmentLogger.Instance.write_log("test", "throws", new Values { Throw = true });
            DevelopmentLogger.Instance.write_log("test", "still_alive", "ok");
            WaitForLines(CurrentPath(), 2);
            string[] lines = ReadLines(CurrentPath());
            Assert.That(lines.Length, Is.EqualTo(2));
            StringAssert.Contains("\"value\":[1,2]", lines[0]);
            StringAssert.Contains("still_alive", lines[1]);
        }

        [Test]
        public void StopAndRestartCloseOldFileWithoutOverwritingOrReopening()
        {
            DevelopmentLogger.Instance.write_log("test", "first", 1);
            string old = CurrentPath();
            Call("StopSession");
            var ignored = new Values();
            DevelopmentLogger.Instance.write_log("test", "closed", ignored);
            Assert.That(ignored.Moves, Is.Zero);
            using (File.Open(old, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            Start();
            DevelopmentLogger.Instance.write_log("test", "second", 2);
            WaitForLines(CurrentPath(), 1);
            Assert.That(Directory.GetFiles(directory).Length, Is.EqualTo(2));
            Assert.That(ReadLines(old).Length, Is.EqualTo(1));
            // Restart during enumeration must not append an old-session record to the new file.
            Assert.That(DevelopmentLogger.Instance.write_log("test", "stale", RestartingIterator()), Is.EqualTo(DevelopmentLogResult.Unavailable));
            Call("StopSession");
            Assert.That(Directory.GetFiles(directory).Length, Is.EqualTo(3));
            Assert.That(Directory.GetFiles(directory).Sum(p => ReadLines(p).Length), Is.EqualTo(2));
        }

        [Test]
        public void InitializationAndWriteFailureDisableSessionWithoutEscaping()
        {
            string occupied = Path.Combine(directory, "file");
            File.WriteAllText(occupied, "keep");
            Start(occupied);
            WaitForUnavailable();
            Assert.That(File.ReadAllText(occupied), Is.EqualTo("keep"));
            using (var entered = new ManualResetEventSlim())
            {
                Start(directory, 2, () => new FailingWriter(entered));
                Assert.That(DevelopmentLogger.Instance.write_log("test", "io_failure", 1), Is.EqualTo(DevelopmentLogResult.Accepted));
                Assert.That(entered.Wait(5000), Is.True);
                WaitForUnavailable();
                var ignored = new Values();
                Assert.That(DevelopmentLogger.Instance.write_log("test", "no_retry", ignored), Is.EqualTo(DevelopmentLogResult.Unavailable));
                Assert.That(ignored.Moves, Is.Zero);
            }
        }

        [Test]
        public void FullQueueRejectsNewRecordWithoutWaitingForBlockedWriter()
        {
            int caller = Thread.CurrentThread.ManagedThreadId;
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                BlockingWriter output = null;
                int factoryThread = 0;
                Start(directory, 2, () => { factoryThread = Thread.CurrentThread.ManagedThreadId; return output = new BlockingWriter(entered, release); });
                try
                {
                    Assert.That(DevelopmentLogger.Instance.write_log("test", "in_flight", 0), Is.EqualTo(DevelopmentLogResult.Accepted));
                    Assert.That(entered.Wait(5000), Is.True);
                    var admission = Task.Run(() => new[] {
                        DevelopmentLogger.Instance.write_log("test", "queued", 1),
                        DevelopmentLogger.Instance.write_log("test", "queued", 2),
                        DevelopmentLogger.Instance.write_log("test", "full", 3) });
                    Assert.That(admission.Wait(2000), Is.True, "Producers must not wait for file I/O or space.");
                    Assert.That(admission.Result, Is.EqualTo(new[] { DevelopmentLogResult.Accepted, DevelopmentLogResult.Accepted, DevelopmentLogResult.QueueFull }));
                    var ignored = new Values();
                    Assert.That(DevelopmentLogger.Instance.write_log("test", "full_iterator", ignored), Is.EqualTo(DevelopmentLogResult.QueueFull));
                    Assert.That(ignored.Moves, Is.Zero);
                }
                finally { release.Set(); Call("StopSession"); }
                Assert.That(output.Lines.Count, Is.EqualTo(3));
                Assert.That(factoryThread, Is.Not.EqualTo(caller));
                Assert.That(output.WriteThread, Is.EqualTo(factoryThread));
                Assert.That(output.DisposeThread, Is.EqualTo(factoryThread));
                for (int i = 0; i < 3; i++) StringAssert.Contains("\"value\":" + i, output.Lines[i]);
            }
        }

        [Test]
        public void DefaultQueueHolds65536RecordsAndConcurrentAdmissionsStayBounded()
        {
            using (var opening = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                Start(directory, DevelopmentLogger.DefaultQueueCapacity, () => {
                    opening.Set(); release.Wait(); return new StreamWriter(new MemoryStream());
                });
                Assert.That(opening.Wait(5000), Is.True);
                try
                {
                    Assert.That(DevelopmentLogger.DefaultQueueCapacity, Is.EqualTo(65536));
                    int accepted = 0, full = 0;
                    Parallel.For(0, 65550, i => {
                        var result = DevelopmentLogger.Instance.write_log("parallel", "capacity", i);
                        if (result == DevelopmentLogResult.Accepted) Interlocked.Increment(ref accepted);
                        else if (result == DevelopmentLogResult.QueueFull) Interlocked.Increment(ref full);
                    });
                    Assert.That(accepted, Is.EqualTo(65536));
                    Assert.That(full, Is.EqualTo(14));
                }
                finally { release.Set(); Call("StopSession"); }
            }
        }

        private void WaitForUnavailable() => Assert.That(SpinWait.SpinUntil(() =>
            DevelopmentLogger.Instance.write_log("test", "probe", 0) == DevelopmentLogResult.Unavailable, 5000), Is.True);

        [Test]
        public void StalledOldWorkerDoesNotBlockShutdownOrWriteIntoRestartedSession()
        {
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                BlockingWriter output = null;
                Start(directory, 2, () => output = new BlockingWriter(entered, release));
                object old = typeof(DevelopmentLogger).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(DevelopmentLogger.Instance);
                var worker = (Thread)old.GetType().GetField("worker", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(old);
                try
                {
                    DevelopmentLogger.Instance.write_log("test", "old", 1);
                    Assert.That(entered.Wait(5000), Is.True);
                    Task stop = Task.Run(() => Call("StopSession"));
                    Assert.That(stop.Wait(3000), Is.True, "Shutdown must return even while I/O is stalled.");
                    Assert.That(worker.IsAlive, Is.True);
                    Start();
                    Assert.That(DevelopmentLogger.Instance.write_log("test", "new", 2), Is.EqualTo(DevelopmentLogResult.Accepted));
                    WaitForLines(CurrentPath(), 1);
                    StringAssert.DoesNotContain("\"tag\":\"old\"", ReadText(CurrentPath()));
                }
                finally { release.Set(); Assert.That(worker.Join(5000), Is.True); }
                Assert.That(output.Lines.Count, Is.EqualTo(1));
                StringAssert.Contains("\"tag\":\"old\"", output.Lines[0]);
            }
        }

        private sealed class FailingWriter : StreamWriter
        {
            private readonly ManualResetEventSlim entered;
            internal FailingWriter(ManualResetEventSlim entered) : base(new MemoryStream()) { this.entered = entered; }
            public override void WriteLine(string value) { entered.Set(); throw new IOException("Injected storage failure"); }
        }

        private sealed class BlockingWriter : StreamWriter
        {
            private readonly ManualResetEventSlim entered, release;
            internal readonly System.Collections.Generic.List<string> Lines = new System.Collections.Generic.List<string>();
            internal int WriteThread, DisposeThread;
            internal BlockingWriter(ManualResetEventSlim entered, ManualResetEventSlim release) : base(new MemoryStream())
            { this.entered = entered; this.release = release; }
            public override void WriteLine(string value)
            {
                WriteThread = Thread.CurrentThread.ManagedThreadId;
                entered.Set(); release.Wait(); Lines.Add(value);
            }
            protected override void Dispose(bool disposing) { DisposeThread = Thread.CurrentThread.ManagedThreadId; base.Dispose(disposing); }
        }

        private static string CurrentPath()
        {
            object session = typeof(DevelopmentLogger).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(DevelopmentLogger.Instance);
            return (string)session.GetType().GetField("FilePath", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(session);
        }

        private static void WaitForLines(string path, int count) => Assert.That(SpinWait.SpinUntil(() =>
            File.Exists(path) && ReadLines(path).Length >= count, 5000), Is.True, "Worker did not flush the expected records.");

        private static string ReadText(string path)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(input, Encoding.UTF8, false)) return reader.ReadToEnd();
        }
        private static string[] ReadLines(string path)
        {
            string[] parts = ReadText(path).Split('\n');
            return parts.Take(parts.Length - 1).ToArray(); // Only LF-terminated records.
        }

        private IEnumerator RestartingIterator() { Start(); yield return 1; }
        private void Start(string path = null, int capacity = DevelopmentLogger.DefaultQueueCapacity, Func<StreamWriter> factory = null)
            => Call("StartSession", path ?? directory, Commit, capacity, factory);
        private static void Call(string method, params object[] args) => typeof(DevelopmentLogger)
            .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(DevelopmentLogger.Instance, args);

        [Serializable]
        private sealed class Row { public int frame; public string writer_id; public string tag; public int value; }

        private sealed class Values : IEnumerator, IDisposable
        {
            public int Moves;
            public bool Disposed;
            public bool Throw;
            public object Current => Moves;
            public bool MoveNext() { if (Throw) throw new InvalidOperationException(); return ++Moves <= 2; }
            public void Reset() => throw new NotSupportedException();
            public void Dispose() => Disposed = true;
        }
    }
}
#endif
