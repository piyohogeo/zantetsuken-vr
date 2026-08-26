using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Core.Input;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class BladeInputTraceObserverTests
    {
        private static TraceLogger NewLogger()
        {
            return new TraceLogger(64);
        }

        private static BladeEdgeGateSettings GateSettings()
        {
            return new BladeEdgeGateSettings(0.030, 0.060, 1.5f, 0.15f, 0.15f);
        }

        private static BladeFrame Frame()
        {
            return new BladeFrame(Vector3.right, Vector3.up, Vector3.forward, Vector3.right * 0.7f);
        }

        private static Pose IdentityOffset()
        {
            return new Pose(Vector3.zero, Quaternion.identity);
        }

        private static BladeInputTraceContext Context(long timestamp)
        {
            return new BladeInputTraceContext(timestamp, 0, 0, 0, 0);
        }

        private static BladePoseSample FullyTracked(double timestamp, Vector3 gripPosition)
        {
            return new BladePoseSample(1, timestamp, gripPosition, Quaternion.identity, BladeTrackingState.Position | BladeTrackingState.Rotation);
        }

        private static BladePoseSample FullyTrackedAt(long frameId, double timestamp, Vector3 gripPosition)
        {
            return new BladePoseSample(frameId, timestamp, gripPosition, Quaternion.identity, BladeTrackingState.Position | BladeTrackingState.Rotation);
        }

        private static int ProcessAndRecord(BladeInputProcessor processor, BladeInputTraceObserver observer, BladeInputTraceContext context, BladePoseSample sample)
        {
            BladeInputProcessingResult result = processor.Process(sample, IdentityOffset(), Frame());
            return observer.Record(context, sample, result);
        }

        private static TraceEvent[] Drain(TraceLogger logger)
        {
            logger.Drain();
            TraceEvent[] events = new TraceEvent[logger.HistoryCount];
            for (int i = 0; i < events.Length; i++)
            {
                events[i] = logger.GetHistoryEvent(i);
            }

            return events;
        }

        [Test]
        public void Assembly_DependencyDirection_IsCorrect()
        {
            Assembly observability = typeof(BladeInputTraceObserver).Assembly;
            Assembly core = typeof(BladePoseSample).Assembly;

            bool observabilityReferencesCore = false;
            foreach (AssemblyName name in observability.GetReferencedAssemblies())
            {
                if (name.Name == "Zantetsu.Core")
                {
                    observabilityReferencesCore = true;
                }
            }

            Assert.That(observabilityReferencesCore, Is.True);

            foreach (AssemblyName name in core.GetReferencedAssemblies())
            {
                Assert.That(name.Name, Is.Not.EqualTo("Zantetsu.Observability"));
                Assert.That(name.Name, Is.Not.EqualTo("Zantetsu.Trace"));
            }
        }

        [Test]
        public void Constructor_RejectsNullLogger()
        {
            Assert.Throws<ArgumentNullException>(() => new BladeInputTraceObserver(null));
        }

        [Test]
        public void FirstWindowAccumulating_ZeroEvents()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                int enqueued = ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero));

                Assert.That(enqueued, Is.EqualTo(0));
                Assert.That(Drain(logger).Length, Is.EqualTo(0));
            }
        }

        [Test]
        public void FirstTrackingInsufficient_ZeroEvents()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                BladePoseSample incomplete = new BladePoseSample(1, 0.0, Vector3.zero, Quaternion.identity, BladeTrackingState.Position);
                int enqueued = ProcessAndRecord(processor, observer, Context(0), incomplete);

                Assert.That(enqueued, Is.EqualTo(0));
                Assert.That(Drain(logger).Length, Is.EqualTo(0));
            }
        }

        [Test]
        public void FirstInvalidSample_ZeroEvents()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                BladePoseSample invalid = new BladePoseSample(1, 0.0, new Vector3(float.NaN, 0, 0), Quaternion.identity, BladeTrackingState.Position | BladeTrackingState.Rotation);
                int enqueued = ProcessAndRecord(processor, observer, Context(0), invalid);

                Assert.That(enqueued, Is.EqualTo(0));
                Assert.That(Drain(logger).Length, Is.EqualTo(0));
            }
        }

        [Test]
        public void Lost_RecordsLostAndSamplesReset_InOrder()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(1, 0.0, Vector3.zero));
                BladePoseSample bad = new BladePoseSample(2, 0.010, new Vector3(float.NaN, 0, 0), Quaternion.identity, BladeTrackingState.Position | BladeTrackingState.Rotation);
                BladeInputProcessingResult result = processor.Process(bad, IdentityOffset(), Frame());
                int enqueued = observer.Record(new BladeInputTraceContext(100, 200, 3, 400, 500), bad, result);

                Assert.That(enqueued, Is.EqualTo(2));

                TraceEvent[] events = Drain(logger);
                Assert.That(events.Length, Is.EqualTo(2));
                Assert.That(events[0].EventType, Is.EqualTo(TraceEventType.BladeTrackingLost));
                Assert.That(events[1].EventType, Is.EqualTo(TraceEventType.BladeSamplesReset));

                Assert.That(events[0].Timestamp, Is.EqualTo(100));
                Assert.That(events[0].FrameId, Is.EqualTo(2));
                Assert.That(events[0].FixedStepId, Is.EqualTo(200));
                Assert.That(events[0].ThreadId, Is.EqualTo(3));
                Assert.That(events[0].OpenXRFrameId, Is.EqualTo(400));
                Assert.That(events[0].TestRunId, Is.EqualTo(500));
                Assert.That(events[0].FromState, Is.EqualTo(1));
                Assert.That(events[0].ToState, Is.EqualTo(0));
                Assert.That(events[0].Value0, Is.EqualTo((double)((int)BladeTrackingState.Position | (int)BladeTrackingState.Rotation)));
                Assert.That(events[0].Value1, Is.EqualTo(0.010));

                Assert.That(events[1].Value0, Is.EqualTo((double)(int)BladeInputProcessingStatus.InvalidSample));
                Assert.That(events[1].Value1, Is.EqualTo(0.010));
            }
        }

        [Test]
        public void ConsecutiveTrackingInsufficient_NoAdditionalEvents()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero));
                ProcessAndRecord(processor, observer, Context(0), new BladePoseSample(2, 0.010, Vector3.zero, Quaternion.identity, BladeTrackingState.Position));
                int second = ProcessAndRecord(processor, observer, Context(0), new BladePoseSample(3, 0.020, Vector3.zero, Quaternion.identity, BladeTrackingState.Rotation));

                Assert.That(second, Is.EqualTo(0));
                Assert.That(Drain(logger).Length, Is.EqualTo(2)); // only Lost + SamplesReset
            }
        }

        [Test]
        public void Restored_RecordsRestoredOnce()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero));
                ProcessAndRecord(processor, observer, Context(0), new BladePoseSample(2, 0.010, Vector3.zero, Quaternion.identity, BladeTrackingState.Position));

                int restored = ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(3, 0.030, new Vector3(0, 0.1f, 0)));

                Assert.That(restored, Is.EqualTo(1));

                TraceEvent[] events = Drain(logger);
                Assert.That(events.Length, Is.EqualTo(3)); // Lost, SamplesReset, Restored
                Assert.That(events[2].EventType, Is.EqualTo(TraceEventType.BladeTrackingRestored));
                Assert.That(events[2].FromState, Is.EqualTo(0));
                Assert.That(events[2].ToState, Is.EqualTo(1));
            }
        }

        [Test]
        public void ConsecutiveUsable_NoRepeatedRestored()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero));
                ProcessAndRecord(processor, observer, Context(0), new BladePoseSample(2, 0.010, Vector3.zero, Quaternion.identity, BladeTrackingState.Position));
                int firstRestore = ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(3, 0.030, new Vector3(0, 0.1f, 0)));
                int second = ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(4, 0.060, new Vector3(0, 0.2f, 0)));

                Assert.That(firstRestore, Is.EqualTo(1)); // BladeTrackingRestored
                // second usable sample: no Restored, but may produce gate event (not a Restored).
                TraceEvent[] events = Drain(logger);
                Assert.That(events.CountOf(TraceEventType.BladeTrackingRestored), Is.EqualTo(1));
            }
        }

        [Test]
        public void FirstGateAccepted_RecordsEdgeGateEntered()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero));
                int enqueued = ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(2, 0.050, new Vector3(0, 0.2f, 0)));

                Assert.That(enqueued, Is.EqualTo(1));
                TraceEvent[] events = Drain(logger);
                Assert.That(events.Length, Is.EqualTo(1));
                Assert.That(events[0].EventType, Is.EqualTo(TraceEventType.EdgeGateEntered));
                Assert.That(events[0].FromState, Is.EqualTo(0));
                Assert.That(events[0].ToState, Is.EqualTo(1));
            }
        }

        [Test]
        public void ConsecutiveGateAccepted_NoAdditional()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero));
                ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(2, 0.050, new Vector3(0, 0.2f, 0)));
                int enqueued = ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(3, 0.080, new Vector3(0, 0.4f, 0)));

                Assert.That(enqueued, Is.EqualTo(0));
                Assert.That(Drain(logger).Length, Is.EqualTo(1)); // single EdgeGateEntered
            }
        }

        [Test]
        public void AcceptedThenRejected_RecordsEdgeGateRejected()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero));
                ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(2, 0.050, new Vector3(0, 0.2f, 0))); // accepted
                int enqueued = ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(3, 0.080, new Vector3(0, -0.4f, 0))); // rejected (spine)

                Assert.That(enqueued, Is.EqualTo(1));
                TraceEvent[] events = Drain(logger);
                Assert.That(events[events.Length - 1].EventType, Is.EqualTo(TraceEventType.EdgeGateRejected));
                Assert.That(events[events.Length - 1].FromState, Is.EqualTo(1)); // was accepted before
            }
        }

        [Test]
        public void SameReasonConsecutiveRejected_NoAdditional()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(2, GateSettings());

                ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero));
                ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(2, 0.050, new Vector3(0, -0.2f, 0))); // rejected A
                int enqueued = ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(3, 0.080, new Vector3(0, -0.45f, 0))); // rejected A again

                Assert.That(enqueued, Is.EqualTo(0));
                Assert.That(Drain(logger).CountOf(TraceEventType.EdgeGateRejected), Is.EqualTo(1));
            }
        }

        [Test]
        public void ReasonChange_RecordsEdgeGateRejectedAgain()
        {
            BladeEdgeGateSettings settings = new BladeEdgeGateSettings(0.01, 1.0, 0f, 0f, 0.5f);
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(2, settings);

                ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero));
                ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(2, 0.5, new Vector3(0, -1, 0))); // rejected: edge lead below threshold
                int sameReason = ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(3, 1.0, new Vector3(0, -2, 0))); // same reason
                int changed = ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(4, 1.5, new Vector3(1, -2, 0))); // no lateral motion

                Assert.That(sameReason, Is.EqualTo(0));
                Assert.That(changed, Is.EqualTo(1));

                TraceEvent[] events = Drain(logger);
                Assert.That(events.Length, Is.EqualTo(2));
                Assert.That((int)events[0].Value1, Is.EqualTo((int)BladeEdgeGateReason.EdgeLeadBelowThreshold));
                Assert.That((int)events[1].Value1, Is.EqualTo((int)BladeEdgeGateReason.NoLateralMotion));
            }
        }

        [Test]
        public void RejectedThenAccepted_RecordsEdgeGateEnteredAgain()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero));
                ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(2, 0.050, new Vector3(0, -0.2f, 0))); // rejected
                int enqueued = ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(3, 0.110, new Vector3(0, 0.3f, 0))); // accepted again

                Assert.That(enqueued, Is.EqualTo(1));
                TraceEvent[] events = Drain(logger);
                Assert.That(events.CountOf(TraceEventType.EdgeGateEntered), Is.EqualTo(1));
                Assert.That(events.CountOf(TraceEventType.EdgeGateRejected), Is.EqualTo(1));
            }
        }

        [Test]
        public void GateDecisionGap_ResetsObservation()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(2, GateSettings());

                ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero));
                ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(2, 0.050, new Vector3(0, 0.2f, 0))); // accepted -> EdgeGateEntered
                int gap = ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(3, 0.070, new Vector3(0, 0.4f, 0))); // window too short -> WindowAccumulating
                int reentered = ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(4, 0.100, new Vector3(0, 0.7f, 0))); // accepted -> EdgeGateEntered again

                Assert.That(gap, Is.EqualTo(0));
                Assert.That(reentered, Is.EqualTo(1));
                Assert.That(Drain(logger).CountOf(TraceEventType.EdgeGateEntered), Is.EqualTo(2));
            }
        }

        [Test]
        public void LostResetsGateSuppression()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero));
                ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(2, 0.050, new Vector3(0, 0.2f, 0))); // accepted -> EdgeGateEntered
                ProcessAndRecord(processor, observer, Context(0), new BladePoseSample(3, 0.060, Vector3.zero, Quaternion.identity, BladeTrackingState.Position)); // lost
                ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(4, 0.090, new Vector3(0, 0.4f, 0))); // restored (window accumulating)
                int reentered = ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(5, 0.120, new Vector3(0, 0.6f, 0))); // accepted again

                Assert.That(reentered, Is.EqualTo(1));
                Assert.That(Drain(logger).CountOf(TraceEventType.EdgeGateEntered), Is.EqualTo(2));
            }
        }

        [Test]
        public void Reset_ReobservesNextGate()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero));
                ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(2, 0.050, new Vector3(0, 0.2f, 0))); // accepted -> EdgeGateEntered

                observer.Reset();

                int reentered = ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(3, 0.080, new Vector3(0, 0.4f, 0))); // accepted -> EdgeGateEntered again

                Assert.That(reentered, Is.EqualTo(1));
                Assert.That(Drain(logger).CountOf(TraceEventType.EdgeGateEntered), Is.EqualTo(2));
            }
        }

        [Test]
        public void Record_DoesNotDrain()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero));
                ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(2, 0.050, new Vector3(0, 0.2f, 0))); // enqueues EdgeGateEntered

                Assert.That(logger.HistoryCount, Is.EqualTo(0)); // not drained
                logger.Drain();
                Assert.That(logger.HistoryCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void ChronologicalOrder_AfterDrain()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero)); // accumulating
                ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(2, 0.050, new Vector3(0, 0.2f, 0))); // accepted -> EdgeGateEntered
                ProcessAndRecord(processor, observer, Context(0), new BladePoseSample(3, 0.060, Vector3.zero, Quaternion.identity, BladeTrackingState.Position)); // lost -> Lost + Reset
                ProcessAndRecord(processor, observer, Context(0), new BladePoseSample(4, 0.070, Vector3.zero, Quaternion.identity, BladeTrackingState.Rotation)); // still lost
                ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(5, 0.090, new Vector3(0, 0.4f, 0))); // restored -> Restored
                ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(6, 0.120, new Vector3(0, 0.6f, 0))); // accepted -> EdgeGateEntered

                TraceEvent[] events = Drain(logger);
                Assert.That(events.Length, Is.EqualTo(5));
                Assert.That(events[0].EventType, Is.EqualTo(TraceEventType.EdgeGateEntered));
                Assert.That(events[1].EventType, Is.EqualTo(TraceEventType.BladeTrackingLost));
                Assert.That(events[2].EventType, Is.EqualTo(TraceEventType.BladeSamplesReset));
                Assert.That(events[3].EventType, Is.EqualTo(TraceEventType.BladeTrackingRestored));
                Assert.That(events[4].EventType, Is.EqualTo(TraceEventType.EdgeGateEntered));
            }
        }

        [Test]
        public void UnusedIdFields_AreZero()
        {
            using (TraceLogger logger = NewLogger())
            {
                BladeInputTraceObserver observer = new BladeInputTraceObserver(logger);
                BladeInputProcessor processor = new BladeInputProcessor(3, GateSettings());

                ProcessAndRecord(processor, observer, Context(0), FullyTracked(0.0, Vector3.zero));
                ProcessAndRecord(processor, observer, Context(0), FullyTrackedAt(2, 0.050, new Vector3(0, 0.2f, 0)));

                TraceEvent[] events = Drain(logger);
                Assert.That(events.Length, Is.GreaterThan(0));
                foreach (TraceEvent e in events)
                {
                    Assert.That(e.SlashId, Is.EqualTo(0));
                    Assert.That(e.SlashGeneration, Is.EqualTo(0));
                    Assert.That(e.FrontEdgeId, Is.EqualTo(0));
                    Assert.That(e.ObjectId, Is.EqualTo(0));
                    Assert.That(e.ObjectGeneration, Is.EqualTo(0));
                    Assert.That(e.MobId, Is.EqualTo(0));
                    Assert.That(e.PlanGeneration, Is.EqualTo(0));
                    Assert.That(e.TaskId, Is.EqualTo(0));
                    Assert.That(e.CaptureFrameId, Is.EqualTo(0));
                    Assert.That(e.TaskType, Is.EqualTo(TraceTaskType.None));
                    Assert.That(e.Reason, Is.EqualTo(TraceReason.None));
                }
            }
        }

        [Test]
        public void Context_HasNoReferenceFields()
        {
            FieldInfo[] fields = typeof(BladeInputTraceContext).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.GreaterThan(0));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.FieldType.IsValueType, Is.True, field.Name + " is a reference type");
            }
        }
    }
}

internal static class TraceEventArrayExtensions
{
    public static int CountOf(this TraceEvent[] events, TraceEventType eventType)
    {
        int count = 0;
        foreach (TraceEvent e in events)
        {
            if (e.EventType == eventType)
            {
                count++;
            }
        }

        return count;
    }
}
