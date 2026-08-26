using System;
using System.Globalization;
using System.IO;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class TraceRunManifestCodecTests
    {
        private const string GoldenSha256 = "e53f7aedbe2641671998901523773ff82eabfbc9d487c8a8b00b4fface16e6a6";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private static string BuildGoldenJson()
        {
            string sha64 = new string('a', 64);
            return "{\"schemaVersion\":1,\"testRunId\":1,\"capturedUtcUnixMilliseconds\":0,\"buildId\":\"Zantetsu\",\"unityVersion\":\"6000.3.22f1\",\"packageLockSha256\":\"" + sha64 + "\",\"sceneId\":\"Main\",\"randomSeed\":0,\"fixedDeltaTimeSeconds\":0.02,\"qualityLevel\":0,\"qualityName\":\"High\",\"worldPhysicsProfileVersion\":1,\"gravity\":{\"x\":0,\"y\":-4.9,\"z\":0},\"traceFormat\":{\"major\":1,\"minor\":0},\"trace\":{\"eventCount\":0,\"triggerHistoryCount\":0,\"capturedPostRollCount\":0,\"wasHistoryOverwrittenAtTrigger\":false}}";
        }

        private static byte[] GoldenCanonicalBytes()
        {
            return Utf8NoBom.GetBytes(BuildGoldenJson());
        }

        private static TraceCaptureSnapshot MakeEmptySnapshot()
        {
            TraceLogger logger = new TraceLogger(4);
            TraceFlightRecorder recorder = new TraceFlightRecorder(logger, 0);
            recorder.TryTrigger();
            TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();
            logger.Dispose();
            return snapshot;
        }

        private static TraceRunContext MakeContext(
            long testRunId = 1,
            long capturedUtcUnixMilliseconds = 0,
            string buildId = "Zantetsu",
            string unityVersion = "6000.3.22f1",
            string packageLockSha256 = null,
            string sceneId = "Main",
            long randomSeed = 0,
            double fixedDeltaTimeSeconds = 0.02,
            int qualityLevel = 0,
            string qualityName = "High",
            int worldPhysicsProfileVersion = 1,
            Vector3 gravity = default)
        {
            if (packageLockSha256 == null)
            {
                packageLockSha256 = new string('a', 64);
            }

            return new TraceRunContext(
                testRunId, capturedUtcUnixMilliseconds, buildId, unityVersion,
                packageLockSha256, sceneId, randomSeed, fixedDeltaTimeSeconds,
                qualityLevel, qualityName, worldPhysicsProfileVersion, gravity);
        }

        private static TraceRunManifest MakeManifest(TraceRunContext context)
        {
            return TraceRunManifest.Create(MakeEmptySnapshot(), context);
        }

        private static TraceRunManifest MakeGoldenManifest()
        {
            return MakeManifest(MakeContext(gravity: new Vector3(0f, -4.9f, 0f)));
        }

        // --- Canonical output ---

        [Test]
        public void Serialize_GoldenJsonByteMatch()
        {
            byte[] bytes = TraceRunManifestCodec.SerializeCanonical(MakeGoldenManifest());

            Assert.That(bytes, Is.EqualTo(GoldenCanonicalBytes()));
        }

        [Test]
        public void Serialize_NoBomNewlineOrTrailingWhitespace()
        {
            byte[] bytes = TraceRunManifestCodec.SerializeCanonical(MakeGoldenManifest());

            Assert.That(bytes[0], Is.Not.EqualTo(0xEF)); // no BOM
            for (int i = 0; i < bytes.Length; i++)
            {
                Assert.That(bytes[i], Is.Not.EqualTo((byte)'\n'));
                Assert.That(bytes[i], Is.Not.EqualTo((byte)'\r'));
            }

            Assert.That(bytes[bytes.Length - 1], Is.Not.EqualTo((byte)' '));
            Assert.That(bytes[bytes.Length - 1], Is.Not.EqualTo((byte)'\t'));
        }

        [Test]
        public void Serialize_PropertyOrderFixed()
        {
            string json = Utf8NoBom.GetString(TraceRunManifestCodec.SerializeCanonical(MakeGoldenManifest()));

            int schema = json.IndexOf("\"schemaVersion\"", StringComparison.Ordinal);
            int testRun = json.IndexOf("\"testRunId\"", StringComparison.Ordinal);
            int captured = json.IndexOf("\"capturedUtcUnixMilliseconds\"", StringComparison.Ordinal);
            int build = json.IndexOf("\"buildId\"", StringComparison.Ordinal);
            int unity = json.IndexOf("\"unityVersion\"", StringComparison.Ordinal);
            int package = json.IndexOf("\"packageLockSha256\"", StringComparison.Ordinal);
            int scene = json.IndexOf("\"sceneId\"", StringComparison.Ordinal);
            int seed = json.IndexOf("\"randomSeed\"", StringComparison.Ordinal);
            int delta = json.IndexOf("\"fixedDeltaTimeSeconds\"", StringComparison.Ordinal);
            int level = json.IndexOf("\"qualityLevel\"", StringComparison.Ordinal);
            int name = json.IndexOf("\"qualityName\"", StringComparison.Ordinal);
            int profile = json.IndexOf("\"worldPhysicsProfileVersion\"", StringComparison.Ordinal);
            int gravity = json.IndexOf("\"gravity\"", StringComparison.Ordinal);
            int traceFormat = json.IndexOf("\"traceFormat\"", StringComparison.Ordinal);
            int trace = json.IndexOf("\"trace\"", StringComparison.Ordinal);

            Assert.That(schema, Is.GreaterThanOrEqualTo(0));
            Assert.That(schema < testRun && testRun < captured && captured < build && build < unity && unity < package &&
                package < scene && scene < seed && seed < delta && delta < level && level < name && name < profile &&
                profile < gravity && gravity < traceFormat && traceFormat < trace, Is.True);
        }

        [Test]
        public void Serialize_JapaneseKeptAsUtf8()
        {
            TraceRunManifest manifest = MakeManifest(MakeContext(buildId: "日本語ビルド"));
            byte[] bytes = TraceRunManifestCodec.SerializeCanonical(manifest);

            string json = Utf8NoBom.GetString(bytes);

            Assert.That(json, Does.Contain("日本語ビルド"));
            Assert.That(json, Does.Not.Contain("\\u"));
        }

        [Test]
        public void Serialize_EscapesQuoteBackslashAndControls()
        {
            string buildId = "quote:\" slash:\\ newline:\n tab:\t backspace:\b formfeed:\f carriage:\r";
            TraceRunManifest manifest = MakeManifest(MakeContext(buildId: buildId));
            string json = Utf8NoBom.GetString(TraceRunManifestCodec.SerializeCanonical(manifest));

            Assert.That(json, Does.Contain("\\\""));
            Assert.That(json, Does.Contain("\\\\"));
            Assert.That(json, Does.Contain("\\n"));
            Assert.That(json, Does.Contain("\\t"));
            Assert.That(json, Does.Contain("\\b"));
            Assert.That(json, Does.Contain("\\f"));
            Assert.That(json, Does.Contain("\\r"));
        }

        [Test]
        public void Serialize_ControlCharacter_AsLowercaseHexEscape()
        {
            string buildId = "ctrl\u0001char";
            TraceRunManifest manifest = MakeManifest(MakeContext(buildId: buildId));
            string json = Utf8NoBom.GetString(TraceRunManifestCodec.SerializeCanonical(manifest));

            Assert.That(json, Does.Contain("\\u0001"));
            Assert.That(json, Does.Not.Contain("\\u0001C")); // no stray uppercase
        }

        [Test]
        public void Serialize_ForwardSlash_NotEscaped()
        {
            TraceRunManifest manifest = MakeManifest(MakeContext(sceneId: "a/b/c"));
            string json = Utf8NoBom.GetString(TraceRunManifestCodec.SerializeCanonical(manifest));

            Assert.That(json, Does.Contain("\"a/b/c\""));
            Assert.That(json, Does.Not.Contain("\\/"));
        }

        [Test]
        public void Serialize_ValidSurrogatePair_Roundtrips()
        {
            string buildId = "emoji:\U0001F600";
            TraceRunManifest manifest = MakeManifest(MakeContext(buildId: buildId));

            byte[] bytes = TraceRunManifestCodec.SerializeCanonical(manifest);
            TraceRunManifest result = TraceRunManifestCodec.DeserializeCanonical(bytes);

            Assert.That(result.BuildId, Is.EqualTo(buildId));
        }

        [Test]
        public void Context_LoneHighSurrogate_Rejected()
        {
            Assert.Throws<ArgumentException>(() => MakeContext(buildId: "\uD800"));
        }

        [Test]
        public void Context_LoneLowSurrogate_Rejected()
        {
            Assert.Throws<ArgumentException>(() => MakeContext(buildId: "\uDC00"));
        }

        [Test]
        public void Serialize_NegativeZero_BecomesZero()
        {
            TraceRunManifest manifest = MakeManifest(MakeContext(gravity: new Vector3(-0f, 0f, 0f)));
            string json = Utf8NoBom.GetString(TraceRunManifestCodec.SerializeCanonical(manifest));

            Assert.That(json, Does.Contain("\"x\":0"));
            Assert.That(json, Does.Not.Contain("\"x\":-0"));
        }

        [Test]
        public void Serialize_CultureInvariant()
        {
            CultureInfo original = CultureInfo.CurrentCulture;
            CultureInfo originalUi = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo fr = CultureInfo.GetCultureInfo("fr-FR");
                CultureInfo.CurrentCulture = fr;
                CultureInfo.CurrentUICulture = fr;

                byte[] bytes = TraceRunManifestCodec.SerializeCanonical(MakeGoldenManifest());

                Assert.That(bytes, Is.EqualTo(GoldenCanonicalBytes()));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
                CultureInfo.CurrentUICulture = originalUi;
            }
        }

        [Test]
        public void Serialize_Deterministic()
        {
            TraceRunManifest manifest = MakeGoldenManifest();

            byte[] first = TraceRunManifestCodec.SerializeCanonical(manifest);
            byte[] second = TraceRunManifestCodec.SerializeCanonical(manifest);

            Assert.That(second, Is.EqualTo(first));
        }

        [Test]
        public void Serialize_ExceedsMaximum_Rejected()
        {
            string huge = new string('x', TraceRunManifestCodec.MaximumCanonicalByteCount + 1);
            TraceRunManifest manifest = MakeManifest(MakeContext(buildId: huge));

            Assert.Throws<InvalidOperationException>(() => TraceRunManifestCodec.SerializeCanonical(manifest));
        }

        // --- Deserialize ---

        [Test]
        public void Deserialize_Roundtrip()
        {
            TraceRunManifest manifest = MakeManifest(MakeContext(
                testRunId: 42, capturedUtcUnixMilliseconds: 999, buildId: "b",
                unityVersion: "u", sceneId: "s", randomSeed: -123,
                fixedDeltaTimeSeconds: 0.016, qualityLevel: 5, qualityName: "Ultra",
                worldPhysicsProfileVersion: 9, gravity: new Vector3(1f, -2f, 3f)));

            byte[] bytes = TraceRunManifestCodec.SerializeCanonical(manifest);
            TraceRunManifest result = TraceRunManifestCodec.DeserializeCanonical(bytes);

            Assert.That(result.TestRunId, Is.EqualTo(42));
            Assert.That(result.CapturedUtcUnixMilliseconds, Is.EqualTo(999));
            Assert.That(result.BuildId, Is.EqualTo("b"));
            Assert.That(result.UnityVersion, Is.EqualTo("u"));
            Assert.That(result.SceneId, Is.EqualTo("s"));
            Assert.That(result.RandomSeed, Is.EqualTo(-123));
            Assert.That(result.FixedDeltaTimeSeconds, Is.EqualTo(0.016));
            Assert.That(result.QualityLevel, Is.EqualTo(5));
            Assert.That(result.QualityName, Is.EqualTo("Ultra"));
            Assert.That(result.WorldPhysicsProfileVersion, Is.EqualTo(9));
            Assert.That(result.Gravity, Is.EqualTo(new Vector3(1f, -2f, 3f)));
        }

        [Test]
        public void Deserialize_Null_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => TraceRunManifestCodec.DeserializeCanonical(null));
        }

        [Test]
        public void Deserialize_Empty_Rejected()
        {
            Assert.Throws<InvalidDataException>(() => TraceRunManifestCodec.DeserializeCanonical(new byte[0]));
        }

        [Test]
        public void Deserialize_ExceedsMaximum_Rejected()
        {
            byte[] huge = new byte[TraceRunManifestCodec.MaximumCanonicalByteCount + 1];
            Assert.Throws<InvalidDataException>(() => TraceRunManifestCodec.DeserializeCanonical(huge));
        }

        [Test]
        public void Deserialize_Bom_Rejected()
        {
            byte[] canonical = GoldenCanonicalBytes();
            byte[] withBom = new byte[canonical.Length + 3];
            withBom[0] = 0xEF;
            withBom[1] = 0xBB;
            withBom[2] = 0xBF;
            Array.Copy(canonical, 0, withBom, 3, canonical.Length);

            Assert.Throws<InvalidDataException>(() => TraceRunManifestCodec.DeserializeCanonical(withBom));
        }

        [Test]
        public void Deserialize_InvalidUtf8_Rejected()
        {
            byte[] invalid = { 0x7B, 0xFF, 0xFE, 0x7D };

            Assert.Throws<InvalidDataException>(() => TraceRunManifestCodec.DeserializeCanonical(invalid));
        }

        [Test]
        public void Deserialize_MalformedJson_Rejected()
        {
            Assert.Throws<InvalidDataException>(
                () => TraceRunManifestCodec.DeserializeCanonical(Utf8NoBom.GetBytes("not json")));
        }

        [Test]
        public void Deserialize_SchemaVersionMismatch_Rejected()
        {
            string json = BuildGoldenJson().Replace("\"schemaVersion\":1", "\"schemaVersion\":2");

            Assert.Throws<InvalidDataException>(
                () => TraceRunManifestCodec.DeserializeCanonical(Utf8NoBom.GetBytes(json)));
        }

        [Test]
        public void Deserialize_TraceFormatMajorMismatch_Rejected()
        {
            string json = BuildGoldenJson().Replace("\"major\":1", "\"major\":9");

            Assert.Throws<InvalidDataException>(
                () => TraceRunManifestCodec.DeserializeCanonical(Utf8NoBom.GetBytes(json)));
        }

        [Test]
        public void Deserialize_TraceFormatMinorMismatch_Rejected()
        {
            string json = BuildGoldenJson().Replace("\"minor\":0", "\"minor\":1");

            Assert.Throws<InvalidDataException>(
                () => TraceRunManifestCodec.DeserializeCanonical(Utf8NoBom.GetBytes(json)));
        }

        [Test]
        public void Deserialize_MissingProperty_Rejected()
        {
            string json = BuildGoldenJson().Replace(",\"testRunId\":1", "");

            Assert.Throws<InvalidDataException>(
                () => TraceRunManifestCodec.DeserializeCanonical(Utf8NoBom.GetBytes(json)));
        }

        [Test]
        public void Deserialize_UnknownProperty_Rejected()
        {
            string json = BuildGoldenJson().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"extra\":5");

            Assert.Throws<InvalidDataException>(
                () => TraceRunManifestCodec.DeserializeCanonical(Utf8NoBom.GetBytes(json)));
        }

        [Test]
        public void Deserialize_DuplicateProperty_Rejected()
        {
            string json = BuildGoldenJson().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1");

            Assert.Throws<InvalidDataException>(
                () => TraceRunManifestCodec.DeserializeCanonical(Utf8NoBom.GetBytes(json)));
        }

        [Test]
        public void Deserialize_PropertyOrderChanged_Rejected()
        {
            string json = BuildGoldenJson().Replace("\"testRunId\":1,\"capturedUtcUnixMilliseconds\":0", "\"capturedUtcUnixMilliseconds\":0,\"testRunId\":1");

            Assert.Throws<InvalidDataException>(
                () => TraceRunManifestCodec.DeserializeCanonical(Utf8NoBom.GetBytes(json)));
        }

        [Test]
        public void Deserialize_AddedWhitespace_Rejected()
        {
            string json = BuildGoldenJson().Replace("\"schemaVersion\":", "\"schemaVersion\" : ");

            Assert.Throws<InvalidDataException>(
                () => TraceRunManifestCodec.DeserializeCanonical(Utf8NoBom.GetBytes(json)));
        }

        [Test]
        public void Deserialize_NonCanonicalNumber_Rejected()
        {
            string json = BuildGoldenJson().Replace("\"fixedDeltaTimeSeconds\":0.02", "\"fixedDeltaTimeSeconds\":0.0200");

            Assert.Throws<InvalidDataException>(
                () => TraceRunManifestCodec.DeserializeCanonical(Utf8NoBom.GetBytes(json)));
        }

        [Test]
        public void Deserialize_NonCanonicalEscape_Rejected()
        {
            string json = BuildGoldenJson().Replace("\"Zantetsu\"", "\"Zantets\\u0075\"");

            Assert.Throws<InvalidDataException>(
                () => TraceRunManifestCodec.DeserializeCanonical(Utf8NoBom.GetBytes(json)));
        }

        [Test]
        public void Deserialize_UppercaseSha256_Rejected()
        {
            string upper = new string('A', 64);
            string json = BuildGoldenJson().Replace(new string('a', 64), upper);

            Assert.Throws<InvalidDataException>(
                () => TraceRunManifestCodec.DeserializeCanonical(Utf8NoBom.GetBytes(json)));
        }

        [Test]
        public void Deserialize_NegativeEventCount_Rejected()
        {
            string json = BuildGoldenJson().Replace("\"eventCount\":0", "\"eventCount\":-1");

            Assert.Throws<InvalidDataException>(
                () => TraceRunManifestCodec.DeserializeCanonical(Utf8NoBom.GetBytes(json)));
        }

        [Test]
        public void Deserialize_EventCountInvariantMismatch_Rejected()
        {
            string json = BuildGoldenJson()
                .Replace("\"eventCount\":0", "\"eventCount\":5")
                .Replace("\"triggerHistoryCount\":0", "\"triggerHistoryCount\":2")
                .Replace("\"capturedPostRollCount\":0", "\"capturedPostRollCount\":2");

            Assert.Throws<InvalidDataException>(
                () => TraceRunManifestCodec.DeserializeCanonical(Utf8NoBom.GetBytes(json)));
        }

        [Test]
        public void Deserialize_ExtremeLongs_Roundtrip()
        {
            TraceRunManifest manifest = MakeManifest(MakeContext(
                testRunId: long.MaxValue,
                capturedUtcUnixMilliseconds: long.MaxValue,
                randomSeed: long.MinValue));

            byte[] bytes = TraceRunManifestCodec.SerializeCanonical(manifest);
            TraceRunManifest result = TraceRunManifestCodec.DeserializeCanonical(bytes);

            Assert.That(result.TestRunId, Is.EqualTo(long.MaxValue));
            Assert.That(result.CapturedUtcUnixMilliseconds, Is.EqualTo(long.MaxValue));
            Assert.That(result.RandomSeed, Is.EqualTo(long.MinValue));
        }

        [Test]
        public void Deserialize_ReconstructedOversize_WrapsAsInvalidData()
        {
            string golden = BuildGoldenJson();
            string omittedProperty = ",\"capturedUtcUnixMilliseconds\":0";
            string baseBuildId = "Zantetsu";

            // Enlarge buildId so the input stays within the limit while the
            // reconstructed canonical form (which re-adds the omitted property)
            // exceeds it.
            int max = TraceRunManifestCodec.MaximumCanonicalByteCount;
            int longBuildIdLength = max - (golden.Length - baseBuildId.Length - omittedProperty.Length) - 1;
            string longBuildId = new string('x', longBuildIdLength);

            string inputJson = golden
                .Replace("\"buildId\":\"" + baseBuildId + "\"", "\"buildId\":\"" + longBuildId + "\"")
                .Replace(omittedProperty, "");

            byte[] input = Utf8NoBom.GetBytes(inputJson);
            Assert.That(input.Length, Is.LessThanOrEqualTo(max));

            InvalidDataException ex = Assert.Throws<InvalidDataException>(
                () => TraceRunManifestCodec.DeserializeCanonical(input));

            Assert.That(ex.InnerException, Is.InstanceOf<InvalidOperationException>());
        }

        // --- Hash ---

        [Test]
        public void Hash_GoldenSha256()
        {
            Assert.That(TraceRunManifestCodec.ComputeContentSha256(MakeGoldenManifest()), Is.EqualTo(GoldenSha256));
        }

        [Test]
        public void Hash_Is64LowercaseHex()
        {
            string hash = TraceRunManifestCodec.ComputeContentSha256(MakeGoldenManifest());

            Assert.That(hash.Length, Is.EqualTo(64));
            foreach (char c in hash)
            {
                Assert.That((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'), Is.True, "Unexpected hash character: " + c);
            }
        }

        [Test]
        public void Hash_Deterministic()
        {
            TraceRunManifest manifest = MakeGoldenManifest();

            Assert.That(TraceRunManifestCodec.ComputeContentSha256(manifest),
                Is.EqualTo(TraceRunManifestCodec.ComputeContentSha256(manifest)));
        }

        [Test]
        public void Hash_ChangesWhenFieldChanges()
        {
            string a = TraceRunManifestCodec.ComputeContentSha256(MakeManifest(MakeContext(testRunId: 1)));
            string b = TraceRunManifestCodec.ComputeContentSha256(MakeManifest(MakeContext(testRunId: 2)));

            Assert.That(a, Is.Not.EqualTo(b));
        }

        [Test]
        public void Hash_CultureInvariant()
        {
            TraceRunManifest manifest = MakeGoldenManifest();
            string expected = TraceRunManifestCodec.ComputeContentSha256(manifest);

            CultureInfo original = CultureInfo.CurrentCulture;
            CultureInfo originalUi = CultureInfo.CurrentUICulture;
            try
            {
                CultureInfo fr = CultureInfo.GetCultureInfo("fr-FR");
                CultureInfo.CurrentCulture = fr;
                CultureInfo.CurrentUICulture = fr;

                Assert.That(TraceRunManifestCodec.ComputeContentSha256(manifest), Is.EqualTo(expected));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
                CultureInfo.CurrentUICulture = originalUi;
            }
        }
    }
}
