using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunReferenceTests
    {
        private const string ValidSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static TraceEvent Event(int tag)
        {
            return new TraceEvent { Timestamp = tag, EventType = TraceEventType.None };
        }

        private static TraceRunContext MakeContext(
            long testRunId = 1,
            string buildId = "build-1",
            string sceneId = "scene-1",
            long randomSeed = 12345)
        {
            return new TraceRunContext(
                testRunId,
                1000,
                buildId,
                "6000.3.22f1",
                ValidSha256,
                sceneId,
                randomSeed,
                0.02,
                3,
                "High",
                1,
                new Vector3(0f, -4.9f, 0f));
        }

        private static TraceRunManifest MakeManifest(
            long testRunId = 1,
            string buildId = "build-1",
            string sceneId = "scene-1",
            long randomSeed = 12345)
        {
            TraceRunContext context = MakeContext(testRunId, buildId, sceneId, randomSeed);

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

        private static CaptureRunReference MakeReference(
            long testRunId = 1,
            long testCaseId = 100,
            int captureProfileId = 5)
        {
            TraceRunManifest manifest = MakeManifest(testRunId: testRunId);
            return new CaptureRunReference(manifest, testCaseId, captureProfileId, TraceRunManifestCodec.ComputeContentSha256(manifest));
        }

        private static string ComputeHash(TraceRunManifest manifest)
        {
            return TraceRunManifestCodec.ComputeContentSha256(manifest);
        }

        private static string FlipFirstHexChar(string hash)
        {
            char first = hash[0];
            char flipped = first == '0' ? '1' : '0';
            return flipped + hash.Substring(1);
        }

        private static void AssertNoFieldOfType(Type type, Type forbiddenFieldType)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(forbiddenFieldType), type.Name + " must not retain a " + forbiddenFieldType.Name);
            }
        }

        private static void AssertNoPublicSetters(Type type)
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                Assert.That(property.GetSetMethod(false), Is.Null, type.Name + "." + property.Name + " must not have a public setter.");
            }
        }

        private static void AssertNoArrayFields(Type type)
        {
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsArray, Is.False, type.Name + "." + field.Name + " must not be an array.");
            }
        }

        [Test]
        public void NullManifest_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new CaptureRunReference(null, 1, 1, ValidSha256));
        }

        [Test]
        public void TestCaseId_ZeroAndNegative_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string hash = ComputeHash(manifest);

            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureRunReference(manifest, 0, 1, hash));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureRunReference(manifest, -1, 1, hash));
        }

        [Test]
        public void CaptureProfileId_ZeroAndNegative_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string hash = ComputeHash(manifest);

            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureRunReference(manifest, 1, 0, hash));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CaptureRunReference(manifest, 1, -1, hash));
        }

        [Test]
        public void Hash_Null_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();

            Assert.Throws<ArgumentNullException>(() => new CaptureRunReference(manifest, 1, 1, null));
        }

        [Test]
        public void Hash_Length63And65_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string hash = ComputeHash(manifest);

            Assert.Throws<ArgumentException>(() => new CaptureRunReference(manifest, 1, 1, hash.Substring(0, 63)));
            Assert.Throws<ArgumentException>(() => new CaptureRunReference(manifest, 1, 1, hash + "0"));
        }

        [Test]
        public void Hash_NonHex_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string hash = ComputeHash(manifest);

            Assert.Throws<ArgumentException>(() => new CaptureRunReference(manifest, 1, 1, 'g' + hash.Substring(1)));
        }

        [Test]
        public void Hash_Mismatch_Rejected()
        {
            TraceRunManifest manifest = MakeManifest();
            string hash = ComputeHash(manifest);

            Assert.Throws<ArgumentException>(() => new CaptureRunReference(manifest, 1, 1, FlipFirstHexChar(hash)));
        }

        [Test]
        public void Hash_Lowercase_Accepted()
        {
            TraceRunManifest manifest = MakeManifest();
            string hash = ComputeHash(manifest);

            CaptureRunReference reference = new CaptureRunReference(manifest, 1, 1, hash);

            Assert.That(reference.RunManifestContentSha256, Is.EqualTo(hash));
        }

        [Test]
        public void Hash_Uppercase_NormalizedToLowercase()
        {
            TraceRunManifest manifest = MakeManifest();
            string hash = ComputeHash(manifest);
            string uppercase = hash.ToUpperInvariant();

            CaptureRunReference reference = new CaptureRunReference(manifest, 1, 1, uppercase);

            Assert.That(reference.RunManifestContentSha256, Is.EqualTo(hash));
            Assert.That(reference.RunManifestContentSha256, Is.EqualTo(uppercase.ToLowerInvariant()));
        }

        [Test]
        public void ManifestValues_Copied()
        {
            TraceRunManifest manifest = MakeManifest(testRunId: 42, buildId: "build-xyz", sceneId: "scene-xyz", randomSeed: -987654321);
            string hash = ComputeHash(manifest);

            CaptureRunReference reference = new CaptureRunReference(manifest, 100, 5, hash);

            Assert.That(reference.TestRunId, Is.EqualTo(42));
            Assert.That(reference.TestCaseId, Is.EqualTo(100));
            Assert.That(reference.BuildId, Is.EqualTo("build-xyz"));
            Assert.That(reference.SceneId, Is.EqualTo("scene-xyz"));
            Assert.That(reference.RandomSeed, Is.EqualTo(-987654321));
            Assert.That(reference.CaptureProfileId, Is.EqualTo(5));
        }

        [Test]
        public void DoesNotRetainManifest_NoArrays_NoPublicSetters()
        {
            AssertNoFieldOfType(typeof(CaptureRunReference), typeof(TraceRunManifest));
            AssertNoArrayFields(typeof(CaptureRunReference));
            AssertNoPublicSetters(typeof(CaptureRunReference));
        }
    }
}
