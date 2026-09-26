using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The Slash hit detector's <see cref="TraceEventType.SlashHitConfirmed"/> records (DESIGN 21.16.6) through the
    /// existing trace lane, paged history and file store: every real hit is one record -- accepted or not, with or
    /// without an operation -- saved and read back as it went in, and a lane that cannot take a record changes nothing
    /// of the hit or its acceptance.
    /// </summary>
    public partial class CutWorldRootPlayModeTests
    {
        private const int TraceMaxPayload = 32;

        // One main-thread lane carrying this event only, its history, and its drainer: a Run as the file store expects.
        private sealed class HitTraceRun : IDisposable
        {
            internal readonly TraceLaneSet Lanes;
            internal readonly TracePagedHistory History;
            internal readonly TraceLaneDrainer Drainer;

            internal HitTraceRun(int laneIndexCapacity)
            {
                var profile = new TraceLaneSetProfile(
                    TraceMaxPayload, 64, 256, 8,
                    new[]
                    {
                        new TraceLaneSettings(
                            TraceLaneEventMask.None.With(TraceEventType.SlashHitConfirmed), 4096, laneIndexCapacity),
                    });
                Lanes = new TraceLaneSet(profile);
                History = new TracePagedHistory(profile);
                Drainer = new TraceLaneDrainer(Lanes);
            }

            public void Dispose()
            {
                History.Dispose();
                Lanes.Dispose();
            }
        }

        private sealed unsafe class HitRecords : ITraceRecordDestination
        {
            internal readonly List<TraceEventType> Kinds = new List<TraceEventType>();
            internal readonly List<SlashHitConfirmed> Hits = new List<SlashHitConfirmed>();

            public void Receive(TraceEventType recordKind, byte* payload, int payloadLength)
            {
                Kinds.Add(recordKind);
                Assert.That(
                    SlashHitConfirmedTraceRecord.TryRead(new ReadOnlySpan<byte>(payload, payloadLength), out SlashHitConfirmed hit),
                    Is.True, "a SlashHitConfirmed payload");
                Hits.Add(hit);
            }
        }

        private static string TracePath()
        {
            string directory = Path.Combine(Path.GetTempPath(), "zantetsu-slash-hit-trace-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "run.ztrace");
        }

        private static void RemoveTrace(string path)
        {
            File.Delete(path);
            Directory.Delete(Path.GetDirectoryName(path));
        }

        private static void AssertSameHit(SlashHitConfirmed read, SlashHitConfirmed hit, string what)
        {
            Assert.That(read.SlashId, Is.EqualTo(hit.SlashId), what + ": Slash");
            Assert.That(read.At, Is.EqualTo(hit.At), what + ": update time");
            Assert.That(read.AtLatch, Is.EqualTo(hit.AtLatch), what + ": latch update");
            Assert.That(read.Fragment, Is.EqualTo(hit.Fragment), what + ": fragment");
            Assert.That(read.Side, Is.EqualTo(hit.Side), what + ": side");
            Assert.That(read.Acceptance, Is.EqualTo(hit.Acceptance), what + ": acceptance");
            Assert.That(read.Admission, Is.EqualTo(hit.Admission), what + ": admission");
            Assert.That(read.Operation, Is.EqualTo(hit.Operation), what + ": operation");
        }

        private static SlashSweep At(SlashSweep sweep, double at)
        {
            return new SlashSweep(
                sweep.SlashId, at, sweep.IsLatch, sweep.SourceSlashPlane, sweep.TravelAxis, sweep.SpanAxis,
                sweep.PreviousA, sweep.PreviousB, sweep.CurrentA, sweep.CurrentB);
        }

        /// <summary>
        /// A no-op hit, an accepted one and one passed over for an active source are three records, saved and read back
        /// with their Slash, fragment, update time and what the acceptance said; only the accepted one names an
        /// operation.
        /// </summary>
        [UnityTest]
        public IEnumerator EveryRealHit_IsOneSlashHitConfirmedRecord_SavedAndReadBack()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            LogicalFragmentId body = AddBody(root, Vector3.zero);
            yield return null;
            var detector = new SlashHitDetector(root, in k_hitSettings);
            string path = TracePath();
            var written = new List<SlashHitConfirmed>();
            using (var run = new HitTraceRun(8))
            {
                detector.AttachTrace(run.Lanes.CreateWriter(0));

                Evaluate(detector, At(Level(1, 1f, -3f, 3f), 10.25), 1);
                written.AddRange(Hits(detector));
                Evaluate(detector, At(Level(2, 0.3f, -3f, 3f), 10.5), 1, 2);
                written.AddRange(Hits(detector));
                Evaluate(detector, At(Upright(3, 0.2f), 10.75), 1, 2, 3);
                written.AddRange(Hits(detector));

                Assert.That(written.ConvertAll(h => h.Acceptance), Is.EqualTo(new[]
                {
                    ProvisionalCutAcceptance.EmptySide, ProvisionalCutAcceptance.Published, ProvisionalCutAcceptance.NotAccepted,
                }));
                TracePagedHistoryFileStore.SaveAtomic(path, TracePagedRunResult.Finish(run.Drainer, run.History));
            }

            var read = new HitRecords();
            TracePagedHistoryFileSummary summary = TracePagedHistoryFileStore.Read(path, TraceMaxPayload, read);
            RemoveTrace(path);
            Assert.That(summary.Integrity, Is.EqualTo(TraceIntegrityState.Complete));
            Assert.That(summary.CommittedRecordCount, Is.EqualTo(3L));
            Assert.That(read.Kinds, Is.All.EqualTo(TraceEventType.SlashHitConfirmed));
            for (int i = 0; i < written.Count; i++)
            {
                AssertSameHit(read.Hits[i], written[i], "record " + i);
            }

            Assert.That(read.Hits[0].Fragment, Is.EqualTo(body));
            Assert.That(read.Hits[0].Operation.IsSet, Is.False, "the no-op issued no operation");
            Assert.That(read.Hits[1].Operation.IsSet, Is.True, "the accepted hit names its operation");
            Assert.That(read.Hits[2].Admission, Is.EqualTo(LogicalCutAdmission.SourceActive));
            Assert.That(read.Hits[2].Side, Is.Not.Zero, "on a Provisional side");
            Assert.That(read.Hits[2].Operation.IsSet, Is.False, "passed over: no operation");
            Assert.That(read.Hits[1].At, Is.EqualTo(10.5), "the update time of the sweep that hit");

            yield return Until(() => root.Geometry.StageOf(written[1].Operation) == CutGeometryStage.Committed, "committed");
            yield return EndWorld(root);
        }

        /// <summary>
        /// A lane with room for one record: the second hit's record is dropped and counted, and the hit is still passed
        /// on and accepted -- nothing waits for the trace, and nothing is written again.
        /// </summary>
        [UnityTest]
        public IEnumerator ARecordTheLaneCannotTake_IsDroppedAndCounted_AndTheHitStands()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            AddBody(root, new Vector3(-4f, 0f, 0f));
            AddBody(root, new Vector3(4f, 0f, 0f));
            yield return null;
            var detector = new SlashHitDetector(root, in k_hitSettings);
            string path = TracePath();
            List<SlashHitConfirmed> hits;
            using (var run = new HitTraceRun(1))
            {
                detector.AttachTrace(run.Lanes.CreateWriter(0));
                Evaluate(detector, Level(1, 0.3f, -8f, 8f), 1);
                hits = Hits(detector);
                Assert.That(hits.Count, Is.EqualTo(2));
                Assert.That(hits.TrueForAll(h => h.Acceptance == ProvisionalCutAcceptance.Published), Is.True,
                    "both accepted, whatever the trace could take");
                Assert.That(run.Lanes.DropCountOf(0), Is.EqualTo(1L), "one record had no room");
                yield return null;
                Evaluate(detector, Level(1, 0.3f, -8f, 8f), 1);
                Assert.That(detector.HitCount, Is.Zero, "and the hit is not tried again for the trace's sake");
                TracePagedHistoryFileStore.SaveAtomic(path, TracePagedRunResult.Finish(run.Drainer, run.History));
            }

            var read = new HitRecords();
            TracePagedHistoryFileSummary summary = TracePagedHistoryFileStore.Read(path, TraceMaxPayload, read);
            RemoveTrace(path);
            Assert.That(summary.Integrity, Is.EqualTo(TraceIntegrityState.Incomplete), "a lost record is not hidden");
            Assert.That(summary.LaneDropCount, Is.EqualTo(1L));
            Assert.That(read.Hits.Count, Is.EqualTo(1));
            AssertSameHit(read.Hits[0], hits[0], "the record that had room");

            yield return Until(
                () => root.Geometry.StageOf(hits[0].Operation) == CutGeometryStage.Committed
                      && root.Geometry.StageOf(hits[1].Operation) == CutGeometryStage.Committed,
                "both committed");
            yield return EndWorld(root);
        }
    }
}
