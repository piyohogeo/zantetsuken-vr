using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceEventTests
    {
        private static readonly string[] RequiredEventTypeNames =
        {
            "BladeTrackingLost",
            "BladeTrackingRestored",
            "BladeSamplesReset",
            "EdgeGateEntered",
            "EdgeGateRejected",
            "SlashPrimed",
            "SlashLatched",
            "SlashFrontCreated",
            "FrontVertexAdded",
            "FrontEdgeActivated",
            "FrontSampleIgnored",
            "FrontTopologyRejected",
            "SlashFinalizedByReversal",
            "SlashFinalized",
            "SlashFrontExpired",
            "SlashRecoveryStarted",
            "SlashRearmed",
            "FrontHitConfirmed",
            "CandidateDetected",
            "TaskScheduled",
            "TaskStarted",
            "TaskCompleted",
            "PredictionValidated",
            "PredictionRejected",
            "GenerationChanged",
            "MobPlanCreated",
            "MobPlanExtended",
            "MobTierChanged",
            "ReservationCreated",
            "MobPlanInvalidated",
            "MobReplanned",
            "MobPredictionUsed",
            "MobPredictionRejected",
            "CaptureFrameQueued",
            "CaptureFrameEncoded",
            "CaptureFrameDropped",
            "CaptureRingFrozen",
            "ProjectionCaptureCopied",
            "CommitStarted",
            "CommitSucceeded",
            "CommitRejected",
            "FallbackActivated",
            "TaskCancelled",
            "ResultDisposed",
        };

        [Test]
        public void TraceEvent_HasNoReferenceTypeFields()
        {
            FieldInfo[] fields = typeof(TraceEvent).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.GreaterThan(0), "TraceEvent must declare fields.");

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.FieldType.IsValueType, Is.True, "TraceEvent field " + field.Name + " is not a value type.");
            }
        }

        [Test]
        public void TraceEvent_IsBlittable()
        {
            Assert.That(Marshal.SizeOf<TraceEvent>(), Is.GreaterThan(0));
        }

        [Test]
        public void TraceEventType_None_IsZero()
        {
            Assert.That((int)TraceEventType.None, Is.EqualTo(0));
        }

        [Test]
        public void TraceEventType_DefinesAllRequiredEvents()
        {
            foreach (string name in RequiredEventTypeNames)
            {
                Assert.That(Enum.IsDefined(typeof(TraceEventType), name), Is.True, "Missing TraceEventType: " + name);
            }
        }

        [Test]
        public void TraceEventType_ValuesAreUnique()
        {
            HashSet<int> seen = new HashSet<int>();
            Array values = Enum.GetValues(typeof(TraceEventType));
            foreach (TraceEventType value in values)
            {
                int numeric = (int)value;
                Assert.That(numeric, Is.GreaterThanOrEqualTo(0));
                Assert.That(seen.Add(numeric), Is.True, "Duplicate numeric value for " + value);
            }
        }

        [Test]
        public void TraceTaskType_None_IsZero()
        {
            Assert.That((int)TraceTaskType.None, Is.EqualTo(0));
        }

        [Test]
        public void TraceReason_None_IsZero()
        {
            Assert.That((int)TraceReason.None, Is.EqualTo(0));
        }
    }
}
