using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The dispatch record's short log lines and files as the product writes them (GRAPH-CONNECT-17 §3-E4; TL review 8, P2-1: the
    /// walk tools must read the product's own strings). Every line 'DISPATCH SUMMARY &lt;world&gt; k/N: &lt;text&gt;', the last
    /// 'N/N: end: file ... bytes ... sha256 ...; rows ... in ...'. The world's record is written by the real
    /// <see cref="DispatchRecord.WriteWorld"/> from a real dispatcher and pool, its log lines caught as Unity logs them, and the lines
    /// and files left under ZANTETSU_DISPATCH_FIXTURES (else a temporary folder, removed) for walk\check_quest17.py
    /// check-dispatch-fixture to read as it reads a device's.
    /// </summary>
    public class DispatchRecordSummaryTests
    {
        private static readonly Regex Line = new Regex(@"^DISPATCH SUMMARY (.+?) (\d+)/(\d+): (.*)$");
        private static readonly Regex Last = new Regex(@"^end: file (\S+) bytes (\d+) sha256 ([0-9a-f]{64}); rows (\d+) in (\S+)$");

        private sealed class Work : IDispatchWork
        {
            public volatile bool collected, began;
            public ManualResetEventSlim gate;
            public void Begin() { began = true; gate?.Wait(); Thread.Sleep(1); }
            public bool IsComplete => true;
            public void Collect(WorkCompletion completion) { collected = true; }
        }

        private sealed class IdleExecutor : IWorkExecutor
        {
            public IdleExecutor(WorkDestination destination) { Destination = destination; }
            public WorkDestination Destination { get; }
            public int Capacity => 1;
            public int Held => 0;
            public bool CanAccept => false;
            public bool TryAccept(IDispatchWork work) => false;
            public void BeginAccepted(IDispatchWork work) { }
            public bool TryTakeFinished(out IDispatchWork work, out WorkCompletion completion) { work = null; completion = default; return false; }
            public void CloseForNewWork() { }
            public bool StopAndConfirm(int timeoutMilliseconds) => true;
        }

        [Test]
        public void SummaryLines_EveryLine_HasTheOneForm_AndTheLastNamesTheFile()
        {
            var text = new StringBuilder();
            for (int i = 0; i < 40; i++) text.Append("Speculative: works ").Append(i).Append("; ends Collected ").Append(i).Append("; a long enough line to need several parts\n");
            byte[] bytes = Encoding.UTF8.GetBytes(text.ToString());
            List<string> lines = DispatchRecord.SummaryLines("Prop Cut World", text.ToString(), "dispatch-summary-Prop_Cut_World.txt", bytes, 17, "dispatch-works-Prop_Cut_World.csv");
            Assert.That(lines.Count, Is.GreaterThan(2), "several parts and the last");
            var joined = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                Match m = Line.Match(lines[i]);
                Assert.That(m.Success, Is.True, "line " + (i + 1) + " not 'DISPATCH SUMMARY <world> k/N: <text>': " + lines[i]);
                Assert.That(m.Groups[1].Value, Is.EqualTo("Prop Cut World"));
                Assert.That(int.Parse(m.Groups[2].Value), Is.EqualTo(i + 1));
                Assert.That(int.Parse(m.Groups[3].Value), Is.EqualTo(lines.Count));
                Assert.That(lines[i].Length, Is.LessThanOrEqualTo(DispatchRecord.LineRoom));
                if (i < lines.Count - 1) joined.Append(m.Groups[4].Value);
                else
                {
                    Match last = Last.Match(m.Groups[4].Value);
                    Assert.That(last.Success, Is.True, "the last line's text: " + m.Groups[4].Value);
                    Assert.That(last.Groups[1].Value, Is.EqualTo("dispatch-summary-Prop_Cut_World.txt"));
                    Assert.That(int.Parse(last.Groups[2].Value), Is.EqualTo(bytes.Length));
                    Assert.That(last.Groups[4].Value, Is.EqualTo("17"));
                }
            }
            Assert.That(joined.ToString(), Is.EqualTo(text.ToString().Replace('\n', '|')), "the parts joined are the text, '\\n' as '|'");
        }

        [Test]
        public void WriteWorld_WritesTheFilesAndTheLines_TheWalkToolsRead()
        {
            string root = Environment.GetEnvironmentVariable("ZANTETSU_DISPATCH_FIXTURES");
            bool temporary = string.IsNullOrEmpty(root);
            string folder = temporary ? Path.Combine(Path.GetTempPath(), "zantetsu-dispatch-record-" + Guid.NewGuid().ToString("N")) : Path.Combine(root, "world-record");
            Assert.That(Directory.Exists(folder), Is.False, "a new folder: " + folder);
            Directory.CreateDirectory(folder);
            string timelineBefore = CutSpatialSplit.TimelineCsvPath;
            bool logBefore = DispatchObservation.WorkLog;
            var caught = new List<string>();
            void Catch(string message, string stack, LogType type) { if (message.StartsWith("DISPATCH SUMMARY ", StringComparison.Ordinal)) caught.Add(message); }
            WorkerPoolExecutor pool = null;
            SharedWorkDispatcher dispatcher = null;
            var gate = new ManualResetEventSlim(false);   // opened in the finally whatever ended the test
            DispatchRecord.ResetForTests();
            try
            {
                DispatchRecord.Requested = true;
                DispatchRecord.UseFolder(folder);
                DispatchObservation.WorkLog = true;
                pool = WorkerPoolExecutor.BackgroundPoolForReplay(1, 1);
                Assert.That(pool.WaitUntilStartupFinished(5000), Is.True);
                dispatcher = new SharedWorkDispatcher(8, 2, 16, new UnityJobWorkExecutor(1), new IdleExecutor(WorkDestination.GeometryPool), pool);
                var clock = Stopwatch.StartNew();
                int frame = 0;
                void PumpUntil(Func<bool> done, string what)
                {
                    while (!done())
                    {
                        if (clock.ElapsedMilliseconds > 5000) Assert.Fail(what + " not within 5 s");
                        dispatcher.BeginFrame(++frame);
                        dispatcher.Dispatch();
                        Thread.Sleep(1);
                    }
                }
                // TL review 9 (P2-1): a work moved out of Background while it waits -- held behind a gated blocker, then reclassified to
                // the Geometry pool -- so that the product's own MovedOut row reaches the walk tools' checks
                var blocker = new Work { gate = gate };
                Assert.That(dispatcher.TryEnqueue(WorkPurpose.Speculative, blocker, out WorkTicket _), Is.True);
                PumpUntil(() => blocker.began, "the blocker began");
                var leaving = new Work();
                Assert.That(dispatcher.TryEnqueue(WorkPurpose.Speculative, leaving, out WorkTicket leavingTicket), Is.True);
                Assert.That(dispatcher.TryReclassify(leavingTicket, WorkPurpose.AdmittedGeometry), Is.True, "moved to the Geometry pool while waiting");
                gate.Set();
                var works = new List<Work> { blocker };
                for (int i = 0; i < 5; i++)
                {
                    var w = new Work();
                    Assert.That(dispatcher.TryEnqueue(i % 2 == 0 ? WorkPurpose.Maintenance : WorkPurpose.Speculative, w, out WorkTicket _), Is.True);
                    works.Add(w);
                }
                PumpUntil(() => works.TrueForAll(w => w.collected), "the Background works collected");
                Application.logMessageReceived += Catch;
                try { DispatchRecord.WriteWorld("Prop Cut World", dispatcher, null); }
                finally { Application.logMessageReceived -= Catch; }
                Assert.That(caught.Count, Is.GreaterThanOrEqualTo(2), "the summary's parts and its last line were logged");
                File.WriteAllText(Path.Combine(folder, "summary-lines.txt"), string.Join("\n", caught) + "\n", new UTF8Encoding(false));
                string csv = Path.Combine(folder, "dispatch-works-Prop_Cut_World.csv");
                string sum = Path.Combine(folder, "dispatch-summary-Prop_Cut_World.txt");
                Assert.That(File.Exists(csv) && File.Exists(sum), Is.True, "both files written");
                Match last = Last.Match(Line.Match(caught[caught.Count - 1]).Groups[4].Value);
                Assert.That(last.Success, Is.True, "the last line as the walk tools read it: " + caught[caught.Count - 1]);
                Assert.That(int.Parse(last.Groups[4].Value), Is.EqualTo(7), "seven Background works logged: the blocker, the one moved out, five more");
                string[] rows = File.ReadAllLines(csv);
                Assert.That(rows.Length, Is.EqualTo(8), "the header and seven rows");
                string[] head = rows[0].Split(',');
                string movedRow = Array.Find(rows, r => r.Split(',')[Array.IndexOf(head, "end")] == "MovedOut");
                Assert.That(movedRow, Is.Not.Null, "the MovedOut row written");
                string[] cells = movedRow.Split(',');
                Assert.That(cells[Array.IndexOf(head, "destination")], Is.EqualTo("BackgroundPool"), "the destination it was logged for");
                Assert.That(cells[Array.IndexOf(head, "purpose")], Is.EqualTo("Speculative"), "the purpose it had in Background");
                Assert.That(cells[Array.IndexOf(head, "movedTo")], Is.EqualTo("GeometryPool"), "where it went");
            }
            finally
            {
                gate.Set();
                DispatchObservation.WorkLog = logBefore;
                try { dispatcher?.Shutdown(5000); } catch (Exception) { }
                pool?.Dispose();
                DispatchRecord.ResetForTests();
                CutSpatialSplit.TimelineCsvPath = timelineBefore;
                if (temporary) { try { Directory.Delete(folder, true); } catch (IOException) { } }
            }
        }
    }
}
