using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureFrameRecordRegistryTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

        private static TraceRunManifest MakeManifest(long testRunId = 1)
        {
            TraceRunContext context = new TraceRunContext(
                testRunId,
                1000,
                "build-1",
                "6000.3.22f1",
                ValidSha256,
                "scene-1",
                12345,
                0.02,
                3,
                "High",
                1,
                new Vector3(0f, -4.9f, 0f));

            TraceLogger logger = new TraceLogger(1);
            try
            {
                TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);
                logger.Enqueue(Event(1));
                recorder.TryTrigger();
                TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();
                return TraceRunManifest.Create(snapshot, context);
            }
            finally
            {
                logger.Dispose();
            }
        }

        private static CaptureFrameRequest MakeRequest(
            long captureFrameId = 10,
            long unityFrameId = 20,
            long openXRFrameId = 30,
            long testRunId = 1,
            CaptureSource source = CaptureSource.UnityRenderTexture,
            CaptureEye eye = CaptureEye.Left,
            int rectX = 0,
            int rectY = 0,
            int rectWidth = 2,
            int rectHeight = 2,
            int arrayIndex = 0,
            CapturePixelFormat pixelFormat = CapturePixelFormat.Rgba32)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                1,
                unityFrameId,
                3,
                4,
                captureFrameId,
                openXRFrameId,
                testRunId,
                5,
                6,
                7,
                8u,
                9);

            return new CaptureFrameRequest(
                context,
                source,
                eye,
                new CaptureImageRect(rectX, rectY, rectWidth, rectHeight),
                arrayIndex,
                pixelFormat);
        }

        private static CaptureFrameTiming MakeTiming()
        {
            return new CaptureFrameTiming(1.0, 1.0 / 90.0, true, 3.5, 1.25, 7L);
        }

        private static CapturePoseSample MakePose(float x, float y, float z)
        {
            return new CapturePoseSample(new Vector3(x, y, z), Quaternion.identity);
        }

        private static CaptureFrameRecord MakeRecord(CaptureFrameRequest request)
        {
            TraceRunManifest manifest = MakeManifest(request.TraceContext.TestRunId);
            CaptureRunReference run = new CaptureRunReference(
                manifest,
                100,
                5,
                TraceRunManifestCodec.ComputeContentSha256(manifest));

            return new CaptureFrameRecord(
                run,
                request,
                MakeTiming(),
                MakePose(1f, 2f, 3f),
                MakePose(4f, 5f, 6f),
                MakePose(7f, 8f, 9f),
                1);
        }

        [Test]
        public void Constructor_CapacityBoundariesAndInitialProperties()
        {
            CaptureFrameRecordRegistry one = new CaptureFrameRecordRegistry(1);
            Assert.That(one.Capacity, Is.EqualTo(1));
            Assert.That(one.Count, Is.EqualTo(0));
            Assert.That(one.TotalAccepted, Is.EqualTo(0));
            Assert.That(one.TotalRejected, Is.EqualTo(0));

            CaptureFrameRecordRegistry big = new CaptureFrameRecordRegistry(64);
            Assert.That(big.Capacity, Is.EqualTo(64));

            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameRecordRegistry(0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureFrameRecordRegistry(-1));
        }

        [Test]
        public void TryRegister_Success_KeepsSameReference()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFrameRecord record = MakeRecord(MakeRequest(captureFrameId: 10));

            Assert.That(registry.TryRegister(record), Is.True);
            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(registry.TotalAccepted, Is.EqualTo(1));
            Assert.That(registry.TotalRejected, Is.EqualTo(0));

            Assert.That(registry.TryGet(MakeRequest(captureFrameId: 10), out CaptureFrameRecord fetched), Is.True);
            Assert.That(fetched, Is.SameAs(record));
        }

        [Test]
        public void MultipleRecords_GetAndRemoveOutOfOrder()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFrameRequest request10 = MakeRequest(captureFrameId: 10);
            CaptureFrameRequest request20 = MakeRequest(captureFrameId: 20);
            CaptureFrameRequest request30 = MakeRequest(captureFrameId: 30);
            CaptureFrameRecord record10 = MakeRecord(request10);
            CaptureFrameRecord record20 = MakeRecord(request20);
            CaptureFrameRecord record30 = MakeRecord(request30);

            Assert.That(registry.TryRegister(record10), Is.True);
            Assert.That(registry.TryRegister(record20), Is.True);
            Assert.That(registry.TryRegister(record30), Is.True);
            Assert.That(registry.Count, Is.EqualTo(3));

            Assert.That(registry.TryRemove(request30, out CaptureFrameRecord removed30), Is.True);
            Assert.That(removed30, Is.SameAs(record30));

            Assert.That(registry.TryGet(request10, out CaptureFrameRecord fetched10), Is.True);
            Assert.That(fetched10, Is.SameAs(record10));

            Assert.That(registry.TryRemove(request20, out CaptureFrameRecord removed20), Is.True);
            Assert.That(removed20, Is.SameAs(record20));

            Assert.That(registry.TryRemove(request10, out CaptureFrameRecord removed10), Is.True);
            Assert.That(removed10, Is.SameAs(record10));

            Assert.That(registry.Count, Is.EqualTo(0));
            Assert.That(registry.TotalAccepted, Is.EqualTo(3));
            Assert.That(registry.TotalRejected, Is.EqualTo(0));
        }

        [Test]
        public void TryGet_DoesNotMutateStateOrCounters()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFrameRequest request = MakeRequest(captureFrameId: 10);
            CaptureFrameRecord record = MakeRecord(request);
            registry.TryRegister(record);

            Assert.That(registry.TryGet(request, out CaptureFrameRecord first), Is.True);
            Assert.That(registry.TryGet(request, out CaptureFrameRecord second), Is.True);
            Assert.That(first, Is.SameAs(record));
            Assert.That(second, Is.SameAs(record));

            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(registry.TotalAccepted, Is.EqualTo(1));
            Assert.That(registry.TotalRejected, Is.EqualTo(0));
        }

        [Test]
        public void TryRemove_FreesSlotForReRegistration()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);
            CaptureFrameRequest request = MakeRequest(captureFrameId: 10);
            registry.TryRegister(MakeRecord(request));

            Assert.That(registry.TryRemove(request, out CaptureFrameRecord removed), Is.True);
            Assert.That(removed, Is.Not.Null);
            Assert.That(registry.Count, Is.EqualTo(0));

            Assert.That(registry.TryGet(request, out CaptureFrameRecord afterRemoval), Is.False);
            Assert.That(afterRemoval, Is.Null);

            CaptureFrameRecord replacement = MakeRecord(MakeRequest(captureFrameId: 10));
            Assert.That(registry.TryRegister(replacement), Is.True);
            Assert.That(registry.TryGet(request, out CaptureFrameRecord reFetched), Is.True);
            Assert.That(reFetched, Is.SameAs(replacement));
        }

        [Test]
        public void TryRegister_WhenFull_RejectsWithoutMutatingExisting()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(1);
            CaptureFrameRecord first = MakeRecord(MakeRequest(captureFrameId: 10));
            Assert.That(registry.TryRegister(first), Is.True);

            CaptureFrameRecord second = MakeRecord(MakeRequest(captureFrameId: 20));
            Assert.That(registry.TryRegister(second), Is.False);

            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(registry.TotalAccepted, Is.EqualTo(1));
            Assert.That(registry.TotalRejected, Is.EqualTo(1));

            Assert.That(registry.TryGet(MakeRequest(captureFrameId: 10), out CaptureFrameRecord stillThere), Is.True);
            Assert.That(stillThere, Is.SameAs(first));

            Assert.That(registry.TryGet(MakeRequest(captureFrameId: 20), out CaptureFrameRecord rejected), Is.False);
            Assert.That(rejected, Is.Null);
        }

        [Test]
        public void NullRecordAndDefaultRequest_RejectedStateUnchanged()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);

            Assert.Throws<ArgumentNullException>(() => { registry.TryRegister(null); });

            CaptureFrameRequest invalid = default;
            Assert.Throws<ArgumentException>(() => { registry.TryGet(invalid, out _); });
            Assert.Throws<ArgumentException>(() => { registry.TryRemove(invalid, out _); });

            Assert.That(registry.Count, Is.EqualTo(0));
            Assert.That(registry.TotalAccepted, Is.EqualTo(0));
            Assert.That(registry.TotalRejected, Is.EqualTo(0));
        }

        [Test]
        public void DuplicateCaptureFrameId_RejectedStateUnchanged()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            CaptureFrameRequest request = MakeRequest(captureFrameId: 10);
            CaptureFrameRecord record = MakeRecord(request);
            registry.TryRegister(record);

            Assert.Throws<ArgumentException>(() => { registry.TryRegister(record); });

            CaptureFrameRecord other = MakeRecord(MakeRequest(captureFrameId: 10));
            Assert.Throws<ArgumentException>(() => { registry.TryRegister(other); });

            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(registry.TotalAccepted, Is.EqualTo(1));
            Assert.That(registry.TotalRejected, Is.EqualTo(0));

            Assert.That(registry.TryGet(request, out CaptureFrameRecord fetched), Is.True);
            Assert.That(fetched, Is.SameAs(record));
        }

        [Test]
        public void MismatchedTraceContextField_Rejected()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            registry.TryRegister(MakeRecord(MakeRequest(captureFrameId: 10, unityFrameId: 20)));

            CaptureFrameRequest mismatched = MakeRequest(captureFrameId: 10, unityFrameId: 21);
            Assert.Throws<InvalidOperationException>(() => { registry.TryGet(mismatched, out _); });
            Assert.Throws<InvalidOperationException>(() => { registry.TryRemove(mismatched, out _); });

            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(registry.TotalAccepted, Is.EqualTo(1));
            Assert.That(registry.TotalRejected, Is.EqualTo(0));
        }

        [Test]
        public void MismatchedNonContextFields_Rejected()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(8);
            CaptureFrameRequest request = MakeRequest(captureFrameId: 10);
            registry.TryRegister(MakeRecord(request));

            Assert.Throws<InvalidOperationException>(() =>
            {
                registry.TryGet(MakeRequest(captureFrameId: 10, source: CaptureSource.OpenXRProjection), out _);
            });

            Assert.Throws<InvalidOperationException>(() =>
            {
                registry.TryGet(MakeRequest(captureFrameId: 10, eye: CaptureEye.Right), out _);
            });

            Assert.Throws<InvalidOperationException>(() =>
            {
                registry.TryGet(MakeRequest(captureFrameId: 10, rectX: 1, rectY: 1), out _);
            });

            Assert.Throws<InvalidOperationException>(() =>
            {
                registry.TryGet(MakeRequest(captureFrameId: 10, arrayIndex: 1), out _);
            });

            // PixelLayout is fully derived from (pixelFormat, rect width, rect
            // height) inside CaptureFrameRequest and the only defined format is
            // Rgba32, so it cannot differ independently of ImageRect today.
            // IdenticalTo still compares every PixelLayout field.

            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(registry.TotalAccepted, Is.EqualTo(1));
            Assert.That(registry.TotalRejected, Is.EqualTo(0));
        }

        [Test]
        public void UnknownId_ReturnsFalseAndNull()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            registry.TryRegister(MakeRecord(MakeRequest(captureFrameId: 10)));

            Assert.That(registry.TryGet(MakeRequest(captureFrameId: 99), out CaptureFrameRecord fetched), Is.False);
            Assert.That(fetched, Is.Null);

            Assert.That(registry.TryRemove(MakeRequest(captureFrameId: 99), out CaptureFrameRecord removed), Is.False);
            Assert.That(removed, Is.Null);

            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(registry.TotalAccepted, Is.EqualTo(1));
            Assert.That(registry.TotalRejected, Is.EqualTo(0));
        }

        [Test]
        public void Clear_MakesAllRecordsUnavailable()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(4);
            registry.TryRegister(MakeRecord(MakeRequest(captureFrameId: 10)));
            registry.TryRegister(MakeRecord(MakeRequest(captureFrameId: 20)));

            registry.Clear();

            Assert.That(registry.Count, Is.EqualTo(0));
            Assert.That(registry.TryGet(MakeRequest(captureFrameId: 10), out CaptureFrameRecord a), Is.False);
            Assert.That(a, Is.Null);
            Assert.That(registry.TryGet(MakeRequest(captureFrameId: 20), out CaptureFrameRecord b), Is.False);
            Assert.That(b, Is.Null);
        }

        [Test]
        public void Clear_ReusesArrayAndKeepsCounters()
        {
            CaptureFrameRecordRegistry registry = new CaptureFrameRecordRegistry(2);
            registry.TryRegister(MakeRecord(MakeRequest(captureFrameId: 10)));
            registry.TryRegister(MakeRecord(MakeRequest(captureFrameId: 20)));

            registry.Clear();

            Assert.That(registry.Count, Is.EqualTo(0));
            Assert.That(registry.TotalAccepted, Is.EqualTo(2));
            Assert.That(registry.TotalRejected, Is.EqualTo(0));

            Assert.That(registry.TryRegister(MakeRecord(MakeRequest(captureFrameId: 30))), Is.True);
            Assert.That(registry.Count, Is.EqualTo(1));
            Assert.That(registry.TotalAccepted, Is.EqualTo(3));
            Assert.That(registry.TotalRejected, Is.EqualTo(0));

            Assert.That(registry.TryGet(MakeRequest(captureFrameId: 30), out CaptureFrameRecord fetched), Is.True);
            Assert.That(fetched, Is.Not.Null);
        }

        [Test]
        public void OnlyFixedArrayStorage_NoVariableCollections()
        {
            Type type = typeof(CaptureFrameRecordRegistry);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            int arrayCount = 0;
            foreach (FieldInfo field in fields)
            {
                string name = field.FieldType.FullName ?? field.FieldType.Name;

                Assert.That(name.IndexOf("Dictionary", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(name.IndexOf("List", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(name.IndexOf("Queue", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(name.IndexOf("Stack", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(name.IndexOf("HashSet", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(name.IndexOf("LinkedList", StringComparison.Ordinal), Is.LessThan(0));

                if (field.FieldType.IsArray && field.FieldType.GetElementType() == typeof(CaptureFrameRecord))
                {
                    arrayCount++;
                }
            }

            Assert.That(arrayCount, Is.EqualTo(1));
        }

        [Test]
        public void NoForbiddenDependencies()
        {
            Type type = typeof(CaptureFrameRecordRegistry);

            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                string name = field.FieldType.FullName ?? field.FieldType.Name;

                Assert.That(name.IndexOf("FileStore", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(name.IndexOf("TraceLogger", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(name.IndexOf("NativeArray", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(name.IndexOf("Stream", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(name.IndexOf("Writer", StringComparison.Ordinal), Is.LessThan(0));
                Assert.That(typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType), Is.False);
            }
        }
    }
}
