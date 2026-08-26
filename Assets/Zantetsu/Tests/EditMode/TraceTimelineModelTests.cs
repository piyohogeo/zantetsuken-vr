using System;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Observability;
using Zantetsu.Observability.Editor;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceTimelineModelTests
    {
        private static TraceEvent MakeEvent(long timestamp, long frameId)
        {
            TraceEvent e = default;
            e.Timestamp = timestamp;
            e.FrameId = frameId;
            return e;
        }

        private static TraceTimelineFilter Filter(
            long? slashId = null,
            long? objectId = null,
            uint? objectGeneration = null,
            long? mobId = null,
            uint? planGeneration = null,
            long? taskId = null,
            TraceEventType? eventType = null,
            TraceReason? reason = null)
        {
            return new TraceTimelineFilter(slashId, objectId, objectGeneration, mobId, planGeneration, taskId, eventType, reason);
        }

        [Test]
        public void Assembly_IsEditorOnly_AndNotReferencedByRuntime()
        {
            Assembly editor = typeof(TraceTimelineModel).Assembly;
            Assembly trace = typeof(TraceEvent).Assembly;
            Assembly observability = typeof(TraceLogger).Assembly;

            bool refsTrace = false;
            bool refsObservability = false;
            foreach (AssemblyName name in editor.GetReferencedAssemblies())
            {
                if (name.Name == "Zantetsu.Trace") refsTrace = true;
                if (name.Name == "Zantetsu.Observability") refsObservability = true;
            }

            Assert.That(refsTrace, Is.True);
            Assert.That(refsObservability, Is.True);

            foreach (AssemblyName name in trace.GetReferencedAssemblies())
            {
                Assert.That(name.Name, Is.Not.EqualTo("Zantetsu.Observability.Editor"));
            }

            foreach (AssemblyName name in observability.GetReferencedAssemblies())
            {
                Assert.That(name.Name, Is.Not.EqualTo("Zantetsu.Observability.Editor"));
            }
        }

        [Test]
        public void EmptyModel_HasZeroCountsAndTimestampRange()
        {
            TraceTimelineModel model = new TraceTimelineModel();

            Assert.That(model.Count, Is.EqualTo(0));
            Assert.That(model.VisibleCount, Is.EqualTo(0));
            Assert.That(model.MinimumTimestamp, Is.EqualTo(0));
            Assert.That(model.MaximumTimestamp, Is.EqualTo(0));
        }

        [Test]
        public void Load_RejectsNullArray()
        {
            TraceTimelineModel model = new TraceTimelineModel();
            Assert.Throws<ArgumentNullException>(() => model.Load((TraceEvent[])null));
        }

        [Test]
        public void Load_RejectsNullLogger()
        {
            TraceTimelineModel model = new TraceTimelineModel();
            Assert.Throws<ArgumentNullException>(() => model.Load((TraceLogger)null));
        }

        [Test]
        public void Load_DefensivelyCopiesSource()
        {
            TraceEvent[] source = { MakeEvent(1, 1), MakeEvent(2, 2) };
            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(source);

            source[0].Timestamp = 999;

            Assert.That(model.GetEvent(0).Timestamp, Is.EqualTo(1));
        }

        [Test]
        public void Load_SortsByTimestampAscending()
        {
            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[]
            {
                MakeEvent(30, 1),
                MakeEvent(10, 1),
                MakeEvent(20, 1),
            });

            Assert.That(model.GetEvent(0).Timestamp, Is.EqualTo(10));
            Assert.That(model.GetEvent(1).Timestamp, Is.EqualTo(20));
            Assert.That(model.GetEvent(2).Timestamp, Is.EqualTo(30));
        }

        [Test]
        public void Load_SameTimestamp_SortsByFrameIdAscending()
        {
            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[]
            {
                MakeEvent(10, 3),
                MakeEvent(10, 1),
                MakeEvent(10, 2),
            });

            Assert.That(model.GetEvent(0).FrameId, Is.EqualTo(1));
            Assert.That(model.GetEvent(1).FrameId, Is.EqualTo(2));
            Assert.That(model.GetEvent(2).FrameId, Is.EqualTo(3));
        }

        [Test]
        public void Load_SameTimestampAndFrameId_PreservesInputOrder()
        {
            TraceEvent a = MakeEvent(10, 1);
            a.SlashId = 11;
            TraceEvent b = MakeEvent(10, 1);
            b.SlashId = 22;
            TraceEvent c = MakeEvent(10, 1);
            c.SlashId = 33;

            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[] { a, b, c });

            Assert.That(model.GetEvent(0).SlashId, Is.EqualTo(11));
            Assert.That(model.GetEvent(1).SlashId, Is.EqualTo(22));
            Assert.That(model.GetEvent(2).SlashId, Is.EqualTo(33));
        }

        [Test]
        public void Load_AcceptsEmptyArray()
        {
            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new TraceEvent[0]);

            Assert.That(model.Count, Is.EqualTo(0));
        }

        [Test]
        public void NoFilter_ShowsAllEvents()
        {
            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[] { MakeEvent(10, 1), MakeEvent(20, 2), MakeEvent(30, 3) });

            Assert.That(model.VisibleCount, Is.EqualTo(model.Count));
        }

        [Test]
        public void SlashIdFilter_MatchesOnlySlashId()
        {
            TraceEvent e1 = MakeEvent(10, 1); e1.SlashId = 5;
            TraceEvent e2 = MakeEvent(20, 2); e2.SlashId = 7;
            TraceEvent e3 = MakeEvent(30, 3); e3.SlashId = 5;

            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[] { e1, e2, e3 });
            model.Filter = Filter(slashId: 5);

            Assert.That(model.VisibleCount, Is.EqualTo(2));
            Assert.That(model.GetVisibleEvent(0).SlashId, Is.EqualTo(5));
            Assert.That(model.GetVisibleEvent(1).SlashId, Is.EqualTo(5));
        }

        [Test]
        public void ObjectIdAndGeneration_CompoundFilter()
        {
            TraceEvent e1 = MakeEvent(10, 1); e1.ObjectId = 100; e1.ObjectGeneration = 1;
            TraceEvent e2 = MakeEvent(20, 2); e2.ObjectId = 100; e2.ObjectGeneration = 2;
            TraceEvent e3 = MakeEvent(30, 3); e3.ObjectId = 200; e3.ObjectGeneration = 1;

            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[] { e1, e2, e3 });
            model.Filter = Filter(objectId: 100, objectGeneration: 1);

            Assert.That(model.VisibleCount, Is.EqualTo(1));
            Assert.That(model.GetVisibleEvent(0).ObjectId, Is.EqualTo(100));
        }

        [Test]
        public void MobIdAndPlanGeneration_CompoundFilter()
        {
            TraceEvent e1 = MakeEvent(10, 1); e1.MobId = 50; e1.PlanGeneration = 3;
            TraceEvent e2 = MakeEvent(20, 2); e2.MobId = 50; e2.PlanGeneration = 4;
            TraceEvent e3 = MakeEvent(30, 3); e3.MobId = 60; e3.PlanGeneration = 3;

            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[] { e1, e2, e3 });
            model.Filter = Filter(mobId: 50, planGeneration: 3);

            Assert.That(model.VisibleCount, Is.EqualTo(1));
            Assert.That(model.GetVisibleEvent(0).MobId, Is.EqualTo(50));
        }

        [Test]
        public void TaskIdFilter()
        {
            TraceEvent e1 = MakeEvent(10, 1); e1.TaskId = 9;
            TraceEvent e2 = MakeEvent(20, 2); e2.TaskId = 8;

            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[] { e1, e2 });
            model.Filter = Filter(taskId: 8);

            Assert.That(model.VisibleCount, Is.EqualTo(1));
            Assert.That(model.GetVisibleEvent(0).TaskId, Is.EqualTo(8));
        }

        [Test]
        public void EventTypeFilter()
        {
            TraceEvent e1 = MakeEvent(10, 1); e1.EventType = TraceEventType.SlashPrimed;
            TraceEvent e2 = MakeEvent(20, 2); e2.EventType = TraceEventType.SlashLatched;

            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[] { e1, e2 });
            model.Filter = Filter(eventType: TraceEventType.SlashLatched);

            Assert.That(model.VisibleCount, Is.EqualTo(1));
            Assert.That(model.GetVisibleEvent(0).EventType, Is.EqualTo(TraceEventType.SlashLatched));
        }

        [Test]
        public void ReasonFilter()
        {
            TraceEvent e1 = MakeEvent(10, 1); e1.Reason = TraceReason.None;
            TraceEvent e2 = MakeEvent(20, 2); e2.Reason = (TraceReason)5;

            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[] { e1, e2 });
            model.Filter = Filter(reason: (TraceReason)5);

            Assert.That(model.VisibleCount, Is.EqualTo(1));
            Assert.That(model.GetVisibleEvent(0).Reason, Is.EqualTo((TraceReason)5));
        }

        [Test]
        public void ExplicitZeroId_IsSearchable()
        {
            TraceEvent e1 = MakeEvent(10, 1); e1.SlashId = 0;
            TraceEvent e2 = MakeEvent(20, 2); e2.SlashId = 5;

            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[] { e1, e2 });
            model.Filter = Filter(slashId: 0);

            Assert.That(model.VisibleCount, Is.EqualTo(1));
            Assert.That(model.GetVisibleEvent(0).SlashId, Is.EqualTo(0));
        }

        [Test]
        public void MultipleConditions_ApplyAsAnd()
        {
            TraceEvent e1 = MakeEvent(10, 1); e1.SlashId = 5; e1.EventType = TraceEventType.SlashPrimed;
            TraceEvent e2 = MakeEvent(20, 2); e2.SlashId = 5; e2.EventType = TraceEventType.SlashLatched;
            TraceEvent e3 = MakeEvent(30, 3); e3.SlashId = 6; e3.EventType = TraceEventType.SlashPrimed;

            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[] { e1, e2, e3 });
            model.Filter = Filter(slashId: 5, eventType: TraceEventType.SlashPrimed);

            Assert.That(model.VisibleCount, Is.EqualTo(1));
            Assert.That(model.GetVisibleEvent(0).FrameId, Is.EqualTo(1));
        }

        [Test]
        public void FilterChange_PreservesChronologicalOrder()
        {
            TraceEvent e1 = MakeEvent(10, 1); e1.TaskId = 1;
            TraceEvent e2 = MakeEvent(20, 2); e2.TaskId = 2;
            TraceEvent e3 = MakeEvent(30, 3); e3.TaskId = 1;

            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[] { e1, e2, e3 });
            model.Filter = Filter(taskId: 1);

            Assert.That(model.VisibleCount, Is.EqualTo(2));
            Assert.That(model.GetVisibleEvent(0).Timestamp, Is.EqualTo(10));
            Assert.That(model.GetVisibleEvent(1).Timestamp, Is.EqualTo(30));
        }

        [Test]
        public void FilterChange_DoesNotMutateSource()
        {
            TraceEvent e1 = MakeEvent(10, 1); e1.TaskId = 1;
            TraceEvent e2 = MakeEvent(20, 2); e2.TaskId = 2;
            TraceEvent[] source = { e1, e2 };

            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(source);
            model.Filter = Filter(taskId: 1);

            Assert.That(source[0].TaskId, Is.EqualTo(1));
            Assert.That(source[1].TaskId, Is.EqualTo(2));
            Assert.That(source.Length, Is.EqualTo(2));
        }

        [Test]
        public void LaneKeys_AreCorrectPerLane()
        {
            TraceEvent e = MakeEvent(10, 1);
            e.SlashId = 11; e.ObjectId = 22; e.MobId = 33; e.TaskId = 44; e.ThreadId = 55;

            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[] { e });

            model.Lane = TraceTimelineLane.All;
            Assert.That(model.GetVisibleLaneKey(0), Is.EqualTo(0));
            model.Lane = TraceTimelineLane.Slash;
            Assert.That(model.GetVisibleLaneKey(0), Is.EqualTo(11));
            model.Lane = TraceTimelineLane.Object;
            Assert.That(model.GetVisibleLaneKey(0), Is.EqualTo(22));
            model.Lane = TraceTimelineLane.MobPlan;
            Assert.That(model.GetVisibleLaneKey(0), Is.EqualTo(33));
            model.Lane = TraceTimelineLane.Task;
            Assert.That(model.GetVisibleLaneKey(0), Is.EqualTo(44));
            model.Lane = TraceTimelineLane.Thread;
            Assert.That(model.GetVisibleLaneKey(0), Is.EqualTo(55));
        }

        [Test]
        public void LaneChange_DoesNotChangeVisibleCountOrOrder()
        {
            TraceEvent[] source = { MakeEvent(10, 1), MakeEvent(20, 2), MakeEvent(30, 3) };
            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(source);

            model.Lane = TraceTimelineLane.Slash;
            Assert.That(model.VisibleCount, Is.EqualTo(3));
            Assert.That(model.GetVisibleEvent(0).Timestamp, Is.EqualTo(10));
            Assert.That(model.GetVisibleEvent(2).Timestamp, Is.EqualTo(30));

            model.Lane = TraceTimelineLane.Thread;
            Assert.That(model.VisibleCount, Is.EqualTo(3));
            Assert.That(model.GetVisibleEvent(0).Timestamp, Is.EqualTo(10));
        }

        [Test]
        public void UndefinedLane_IsRejected()
        {
            TraceTimelineModel model = new TraceTimelineModel();
            Assert.Throws<ArgumentOutOfRangeException>(() => model.Lane = (TraceTimelineLane)999);
        }

        [Test]
        public void TimestampRange_WithAndWithoutEvents()
        {
            TraceTimelineModel model = new TraceTimelineModel();
            Assert.That(model.MinimumTimestamp, Is.EqualTo(0));
            Assert.That(model.MaximumTimestamp, Is.EqualTo(0));

            model.Load(new[] { MakeEvent(300, 1), MakeEvent(100, 2), MakeEvent(200, 3) });
            Assert.That(model.MinimumTimestamp, Is.EqualTo(100));
            Assert.That(model.MaximumTimestamp, Is.EqualTo(300));
        }

        [Test]
        public void OutOfRangeIndexes_AreRejected()
        {
            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[] { MakeEvent(10, 1), MakeEvent(20, 2) });

            Assert.Throws<ArgumentOutOfRangeException>(() => model.GetEvent(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => model.GetEvent(2));
            Assert.Throws<ArgumentOutOfRangeException>(() => model.GetVisibleEvent(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => model.GetVisibleEvent(2));
        }

        [Test]
        public void Clear_ThenReload()
        {
            TraceTimelineModel model = new TraceTimelineModel();
            model.Load(new[] { MakeEvent(10, 1) });
            Assert.That(model.Count, Is.EqualTo(1));

            model.Clear();
            Assert.That(model.Count, Is.EqualTo(0));
            Assert.That(model.VisibleCount, Is.EqualTo(0));
            Assert.That(model.MinimumTimestamp, Is.EqualTo(0));
            Assert.That(model.MaximumTimestamp, Is.EqualTo(0));

            model.Load(new[] { MakeEvent(40, 4), MakeEvent(50, 5) });
            Assert.That(model.Count, Is.EqualTo(2));
            Assert.That(model.GetEvent(0).Timestamp, Is.EqualTo(40));
        }

        [Test]
        public void LoadLogger_DoesNotDrain()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                logger.Enqueue(MakeEvent(1, 1));
                logger.Enqueue(MakeEvent(2, 2));

                TraceTimelineModel model = new TraceTimelineModel();
                model.Load(logger);

                Assert.That(model.Count, Is.EqualTo(0)); // history not drained
                Assert.That(logger.HistoryCount, Is.EqualTo(0)); // model did not drain
            }
        }

        [Test]
        public void LoadLogger_SnapshotsOnlyHistory()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                logger.Enqueue(MakeEvent(1, 1));
                logger.Enqueue(MakeEvent(2, 2));
                logger.Drain();

                TraceTimelineModel model = new TraceTimelineModel();
                model.Load(logger);

                Assert.That(model.Count, Is.EqualTo(2));
                Assert.That(model.GetEvent(0).Timestamp, Is.EqualTo(1));
                Assert.That(model.GetEvent(1).Timestamp, Is.EqualTo(2));
            }
        }

        [Test]
        public void LoadThenLoggerChanges_ModelUnchangedUntilReload()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                logger.Enqueue(MakeEvent(1, 1));
                logger.Enqueue(MakeEvent(2, 2));
                logger.Drain();

                TraceTimelineModel model = new TraceTimelineModel();
                model.Load(logger);
                Assert.That(model.Count, Is.EqualTo(2));

                logger.Enqueue(MakeEvent(3, 3));
                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(3));
                Assert.That(model.Count, Is.EqualTo(2)); // unchanged until reload

                model.Load(logger);
                Assert.That(model.Count, Is.EqualTo(3));
            }
        }

        [Test]
        public void Model_DoesNotDisposeLogger()
        {
            using (TraceLogger logger = new TraceLogger(8))
            {
                logger.Enqueue(MakeEvent(1, 1));
                logger.Drain();

                TraceTimelineModel model = new TraceTimelineModel();
                model.Load(logger);

                Assert.That(logger.IsCreated, Is.True);
            }
        }
    }
}
