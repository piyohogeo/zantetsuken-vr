using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceRunManifestTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

        private static TraceRunContext MakeContext(
            long testRunId = 1,
            long capturedUtcUnixMilliseconds = 1000,
            string buildId = "build-1",
            string unityVersion = "6000.3.22f1",
            string packageLockSha256 = ValidSha256,
            string sceneId = "scene-1",
            long randomSeed = 12345,
            double fixedDeltaTimeSeconds = 0.02,
            int qualityLevel = 3,
            string qualityName = "High",
            int worldPhysicsProfileVersion = 1,
            Vector3 gravity = default)
        {
            return new TraceRunContext(
                testRunId,
                capturedUtcUnixMilliseconds,
                buildId,
                unityVersion,
                packageLockSha256,
                sceneId,
                randomSeed,
                fixedDeltaTimeSeconds,
                qualityLevel,
                qualityName,
                worldPhysicsProfileVersion,
                gravity);
        }

        private sealed class FrozenFixture : IDisposable
        {
            public TraceLogger Logger;
            public TraceFlightRecorder Recorder;
            public TraceCaptureSnapshot Snapshot;

            public void Dispose()
            {
                if (Logger != null)
                {
                    Logger.Dispose();
                }
            }
        }

        private static FrozenFixture MakeFrozen(int historyCount, int postRollCount, bool wrapped)
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
            return new FrozenFixture { Logger = logger, Recorder = recorder, Snapshot = snapshot };
        }

        // --- Context ---

        [Test]
        public void Context_AllValuesPreserved()
        {
            TraceRunContext context = new TraceRunContext(
                42, 123456789, "build-abc", "6000.3.22f1",
                ValidSha256, "scene-xyz", long.MinValue, 0.02, 3, "High", 1,
                new Vector3(0f, -4.9f, 0f));

            Assert.That(context.TestRunId, Is.EqualTo(42));
            Assert.That(context.CapturedUtcUnixMilliseconds, Is.EqualTo(123456789));
            Assert.That(context.BuildId, Is.EqualTo("build-abc"));
            Assert.That(context.UnityVersion, Is.EqualTo("6000.3.22f1"));
            Assert.That(context.PackageLockSha256, Is.EqualTo(ValidSha256));
            Assert.That(context.SceneId, Is.EqualTo("scene-xyz"));
            Assert.That(context.RandomSeed, Is.EqualTo(long.MinValue));
            Assert.That(context.FixedDeltaTimeSeconds, Is.EqualTo(0.02));
            Assert.That(context.QualityLevel, Is.EqualTo(3));
            Assert.That(context.QualityName, Is.EqualTo("High"));
            Assert.That(context.WorldPhysicsProfileVersion, Is.EqualTo(1));
            Assert.That(context.Gravity.x, Is.EqualTo(0f));
            Assert.That(context.Gravity.y, Is.EqualTo(-4.9f));
            Assert.That(context.Gravity.z, Is.EqualTo(0f));
        }

        [Test]
        public void Context_Sha256Uppercase_NormalizedToLowercase()
        {
            string upper = "ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789";
            string lower = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

            TraceRunContext context = MakeContext(packageLockSha256: upper);

            Assert.That(context.PackageLockSha256, Is.EqualTo(lower));
        }

        [Test]
        public void Context_TestRunId_ZeroOrNegative_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeContext(testRunId: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeContext(testRunId: -1));
        }

        [Test]
        public void Context_UtcMilliseconds_Negative_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeContext(capturedUtcUnixMilliseconds: -1));
        }

        [Test]
        public void Context_RequiredStrings_NullEmptyWhitespace_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => MakeContext(buildId: null));
            Assert.Throws<ArgumentException>(() => MakeContext(buildId: ""));
            Assert.Throws<ArgumentException>(() => MakeContext(buildId: "   "));

            Assert.Throws<ArgumentNullException>(() => MakeContext(unityVersion: null));
            Assert.Throws<ArgumentException>(() => MakeContext(unityVersion: ""));

            Assert.Throws<ArgumentNullException>(() => MakeContext(sceneId: null));
            Assert.Throws<ArgumentException>(() => MakeContext(sceneId: "   "));

            Assert.Throws<ArgumentNullException>(() => MakeContext(qualityName: null));
            Assert.Throws<ArgumentException>(() => MakeContext(qualityName: ""));
        }

        [Test]
        public void Context_Sha256_WrongLength_Rejected()
        {
            string hex63 = new string('a', 63);
            string hex65 = new string('a', 65);

            Assert.Throws<ArgumentException>(() => MakeContext(packageLockSha256: hex63));
            Assert.Throws<ArgumentException>(() => MakeContext(packageLockSha256: hex65));
        }

        [Test]
        public void Context_Sha256_NonHex_Rejected()
        {
            string nonHex = "g" + new string('a', 63);

            Assert.Throws<ArgumentException>(() => MakeContext(packageLockSha256: nonHex));
        }

        [Test]
        public void Context_RandomSeed_MinAndMax_Allowed()
        {
            Assert.That(MakeContext(randomSeed: long.MinValue).RandomSeed, Is.EqualTo(long.MinValue));
            Assert.That(MakeContext(randomSeed: long.MaxValue).RandomSeed, Is.EqualTo(long.MaxValue));
        }

        [Test]
        public void Context_FixedDelta_Invalid_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeContext(fixedDeltaTimeSeconds: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeContext(fixedDeltaTimeSeconds: -0.01));
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeContext(fixedDeltaTimeSeconds: double.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeContext(fixedDeltaTimeSeconds: double.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeContext(fixedDeltaTimeSeconds: double.NegativeInfinity));
        }

        [Test]
        public void Context_NegativeQualityLevel_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeContext(qualityLevel: -1));
        }

        [Test]
        public void Context_ProfileVersion_ZeroOrNegative_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeContext(worldPhysicsProfileVersion: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeContext(worldPhysicsProfileVersion: -1));
        }

        [Test]
        public void Context_Gravity_NonFinite_Rejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeContext(gravity: new Vector3(float.NaN, 0f, 0f)));
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeContext(gravity: new Vector3(0f, float.PositiveInfinity, 0f)));
            Assert.Throws<ArgumentOutOfRangeException>(() => MakeContext(gravity: new Vector3(0f, 0f, float.NegativeInfinity)));
        }

        [Test]
        public void Context_Gravity_NegativeY_Preserved()
        {
            TraceRunContext context = MakeContext(gravity: new Vector3(0f, -4.9f, 0f));

            Assert.That(context.Gravity, Is.EqualTo(new Vector3(0f, -4.9f, 0f)));
        }

        // --- Manifest ---

        [Test]
        public void Manifest_NullSnapshot_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => TraceRunManifest.Create(null, MakeContext()));
        }

        [Test]
        public void Manifest_NullContext_Rejected()
        {
            using (FrozenFixture fixture = MakeFrozen(2, 0, false))
            {
                Assert.Throws<ArgumentNullException>(() => TraceRunManifest.Create(fixture.Snapshot, null));
            }
        }

        [Test]
        public void Manifest_SchemaVersion_IsOne()
        {
            using (FrozenFixture fixture = MakeFrozen(1, 0, false))
            {
                TraceRunManifest manifest = TraceRunManifest.Create(fixture.Snapshot, MakeContext());

                Assert.That(manifest.SchemaVersion, Is.EqualTo(1));
                Assert.That(manifest.SchemaVersion, Is.EqualTo(TraceRunManifest.CurrentSchemaVersion));
            }
        }

        [Test]
        public void Manifest_TraceFormatVersion_MatchesConstants()
        {
            using (FrozenFixture fixture = MakeFrozen(1, 0, false))
            {
                TraceRunManifest manifest = TraceRunManifest.Create(fixture.Snapshot, MakeContext());

                Assert.That(manifest.TraceFormatMajorVersion, Is.EqualTo(TraceBinaryFormat.MajorVersion));
                Assert.That(manifest.TraceFormatMinorVersion, Is.EqualTo(TraceBinaryFormat.MinorVersion));
            }
        }

        [Test]
        public void Manifest_ContextValues_Transferred()
        {
            using (FrozenFixture fixture = MakeFrozen(1, 0, false))
            {
                TraceRunContext context = MakeContext(
                    testRunId: 99,
                    capturedUtcUnixMilliseconds: 555,
                    buildId: "build-x",
                    unityVersion: "6000.3.22f1",
                    packageLockSha256: ValidSha256,
                    sceneId: "scene-y",
                    randomSeed: -777,
                    fixedDeltaTimeSeconds: 0.016,
                    qualityLevel: 5,
                    qualityName: "Ultra",
                    worldPhysicsProfileVersion: 9,
                    gravity: new Vector3(1f, 2f, 3f));

                TraceRunManifest manifest = TraceRunManifest.Create(fixture.Snapshot, context);

                Assert.That(manifest.TestRunId, Is.EqualTo(99));
                Assert.That(manifest.CapturedUtcUnixMilliseconds, Is.EqualTo(555));
                Assert.That(manifest.BuildId, Is.EqualTo("build-x"));
                Assert.That(manifest.UnityVersion, Is.EqualTo("6000.3.22f1"));
                Assert.That(manifest.PackageLockSha256, Is.EqualTo(ValidSha256));
                Assert.That(manifest.SceneId, Is.EqualTo("scene-y"));
                Assert.That(manifest.RandomSeed, Is.EqualTo(-777));
                Assert.That(manifest.FixedDeltaTimeSeconds, Is.EqualTo(0.016));
                Assert.That(manifest.QualityLevel, Is.EqualTo(5));
                Assert.That(manifest.QualityName, Is.EqualTo("Ultra"));
                Assert.That(manifest.WorldPhysicsProfileVersion, Is.EqualTo(9));
                Assert.That(manifest.Gravity, Is.EqualTo(new Vector3(1f, 2f, 3f)));
            }
        }

        [Test]
        public void Manifest_SnapshotMetadata_Transferred()
        {
            using (FrozenFixture fixture = MakeFrozen(3, 2, false))
            {
                TraceRunManifest manifest = TraceRunManifest.Create(fixture.Snapshot, MakeContext());

                Assert.That(manifest.EventCount, Is.EqualTo(5));
                Assert.That(manifest.TriggerHistoryCount, Is.EqualTo(3));
                Assert.That(manifest.CapturedPostRollCount, Is.EqualTo(2));
                Assert.That(manifest.WasHistoryOverwrittenAtTrigger, Is.False);
            }
        }

        [Test]
        public void Manifest_EmptySnapshot_Creates()
        {
            using (FrozenFixture fixture = MakeFrozen(0, 0, false))
            {
                TraceRunManifest manifest = TraceRunManifest.Create(fixture.Snapshot, MakeContext());

                Assert.That(manifest.EventCount, Is.EqualTo(0));
                Assert.That(manifest.TriggerHistoryCount, Is.EqualTo(0));
                Assert.That(manifest.CapturedPostRollCount, Is.EqualTo(0));
            }
        }

        [Test]
        public void Manifest_WrappedHistory_OverwrittenFlagPreserved()
        {
            using (FrozenFixture fixture = MakeFrozen(3, 0, true))
            {
                TraceRunManifest manifest = TraceRunManifest.Create(fixture.Snapshot, MakeContext());

                Assert.That(manifest.WasHistoryOverwrittenAtTrigger, Is.True);
                Assert.That(manifest.EventCount, Is.EqualTo(3));
            }
        }

        [Test]
        public void Manifest_Invariant_EventCountEqualsTriggerPlusPostRoll()
        {
            using (FrozenFixture fixture = MakeFrozen(2, 3, false))
            {
                TraceRunManifest manifest = TraceRunManifest.Create(fixture.Snapshot, MakeContext());

                Assert.That(manifest.EventCount, Is.EqualTo(manifest.TriggerHistoryCount + manifest.CapturedPostRollCount));
            }
        }

        [Test]
        public void Manifest_NoPublicFieldsOrSetters()
        {
            CheckNoMutableApi(typeof(TraceRunContext));
            CheckNoMutableApi(typeof(TraceRunManifest));
        }

        [Test]
        public void Manifest_Creation_DoesNotChangeSnapshotRecorderLogger()
        {
            using (FrozenFixture fixture = MakeFrozen(2, 1, false))
            {
                TraceFlightRecorderState stateBefore = fixture.Recorder.State;
                int triggerBefore = fixture.Recorder.TriggerHistoryCount;
                int postBefore = fixture.Recorder.CapturedPostRollCount;
                int capturedBefore = fixture.Recorder.CapturedCount;
                int historyBefore = fixture.Logger.HistoryCount;
                long totalBefore = fixture.Logger.TotalWritten;

                TraceRunManifest.Create(fixture.Snapshot, MakeContext());

                Assert.That(fixture.Recorder.State, Is.EqualTo(stateBefore));
                Assert.That(fixture.Recorder.TriggerHistoryCount, Is.EqualTo(triggerBefore));
                Assert.That(fixture.Recorder.CapturedPostRollCount, Is.EqualTo(postBefore));
                Assert.That(fixture.Recorder.CapturedCount, Is.EqualTo(capturedBefore));
                Assert.That(fixture.Logger.HistoryCount, Is.EqualTo(historyBefore));
                Assert.That(fixture.Logger.TotalWritten, Is.EqualTo(totalBefore));
            }
        }

        [Test]
        public void Manifest_AfterRecorderReset_ValuesUnchanged()
        {
            using (FrozenFixture fixture = MakeFrozen(2, 0, false))
            {
                TraceRunManifest manifest = TraceRunManifest.Create(fixture.Snapshot, MakeContext());

                fixture.Recorder.Reset();

                Assert.That(manifest.EventCount, Is.EqualTo(2));
                Assert.That(manifest.TriggerHistoryCount, Is.EqualTo(2));
                Assert.That(manifest.CapturedPostRollCount, Is.EqualTo(0));
            }
        }

        private static void CheckNoMutableApi(Type type)
        {
            Assert.That(
                type.GetFields(BindingFlags.Public | BindingFlags.Instance).Length,
                Is.EqualTo(0),
                "Public instance fields exist on " + type.Name);

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(property.CanWrite, Is.False, "Public property has a setter on " + type.Name + ": " + property.Name);
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(method.ReturnType.IsArray, Is.False, "Public method returns an array on " + type.Name + ": " + method.Name);
            }

            Assert.That(type.GetProperty("Item", BindingFlags.Public | BindingFlags.Instance), Is.Null, "Public indexer exists on " + type.Name);
        }
    }
}
