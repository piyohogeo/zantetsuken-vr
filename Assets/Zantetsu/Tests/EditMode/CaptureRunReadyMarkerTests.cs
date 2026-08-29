using System;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunReadyMarkerTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FinalHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);

        private const string Unspecified = "\u0001";

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetMarkerType() => GetTypeFromAssembly("CaptureRunReadyMarker");

        private static Type GetCodecType() => GetTypeFromAssembly("CaptureRunReadyMarkerCodec");

        private static object GetProperty(object target, string name)
        {
            PropertyInfo prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(prop, Is.Not.Null, target.GetType().Name + "." + name + " property not found.");
            return prop.GetValue(target);
        }

        private static Exception Unwrap(Exception ex)
        {
            if (ex is TargetInvocationException tie && tie.InnerException != null)
            {
                return tie.InnerException;
            }

            return ex;
        }

        private static object MakeMarker(
            long testRunId = 1,
            string initId = Unspecified,
            string stagingHash = Unspecified,
            string finalHash = Unspecified)
        {
            if (initId == Unspecified)
            {
                initId = InitId;
            }

            if (stagingHash == Unspecified)
            {
                stagingHash = StagingHash;
            }

            if (finalHash == Unspecified)
            {
                finalHash = FinalHash;
            }

            ConstructorInfo ctor = GetMarkerType().GetConstructor(
                BindingFlags.NonPublic | BindingFlags.Instance,
                null,
                new[] { typeof(long), typeof(string), typeof(string), typeof(string) },
                null);
            Assert.That(ctor, Is.Not.Null, "Ready marker constructor not found.");
            return ctor.Invoke(new object[] { testRunId, initId, stagingHash, finalHash });
        }

        private static Exception MakeMarkerException(
            long testRunId = 1,
            string initId = Unspecified,
            string stagingHash = Unspecified,
            string finalHash = Unspecified)
        {
            try
            {
                MakeMarker(testRunId, initId, stagingHash, finalHash);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static byte[] Serialize(object marker)
        {
            MethodInfo method = GetCodecType().GetMethod("SerializeCanonical", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return (byte[])method.Invoke(null, new object[] { marker });
        }

        private static string Json(byte[] bytes) => Utf8NoBom.GetString(bytes);

        // ---- Value contract ----

        [Test]
        public void Marker_HoldsAllProperties()
        {
            object marker = MakeMarker(42, InitId, StagingHash, FinalHash);

            Assert.That((int)GetProperty(marker, "SchemaVersion"), Is.EqualTo(1));
            Assert.That((long)GetProperty(marker, "TestRunId"), Is.EqualTo(42));
            Assert.That((string)GetProperty(marker, "RunInitializationId"), Is.EqualTo(InitId));
            Assert.That((string)GetProperty(marker, "StagingInitSha256"), Is.EqualTo(StagingHash));
            Assert.That((string)GetProperty(marker, "FinalInitSha256"), Is.EqualTo(FinalHash));
        }

        [Test]
        public void TestRunId_ZeroAndNegative_Rejected()
        {
            Exception zero = MakeMarkerException(testRunId: 0);
            Assert.That(zero, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)zero).ParamName, Is.EqualTo("testRunId"));

            Exception negative = MakeMarkerException(testRunId: -1);
            Assert.That(negative, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)negative).ParamName, Is.EqualTo("testRunId"));
        }

        [Test]
        public void RunInitializationId_Invalid_Rejected()
        {
            Exception nullId = MakeMarkerException(initId: null);
            Assert.That(nullId, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullId).ParamName, Is.EqualTo("runInitializationId"));

            AssertInvalidArgument(MakeMarkerException(initId: new string('0', 31)), "runInitializationId");
            AssertInvalidArgument(MakeMarkerException(initId: new string('0', 33)), "runInitializationId");
            AssertInvalidArgument(MakeMarkerException(initId: new string('A', 32)), "runInitializationId");
            AssertInvalidArgument(MakeMarkerException(initId: new string('g', 32)), "runInitializationId");
        }

        [Test]
        public void InitHashes_Invalid_Rejected()
        {
            Exception nullStaging = MakeMarkerException(stagingHash: null);
            Assert.That(nullStaging, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullStaging).ParamName, Is.EqualTo("stagingInitSha256"));

            Exception nullFinal = MakeMarkerException(finalHash: null);
            Assert.That(nullFinal, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullFinal).ParamName, Is.EqualTo("finalInitSha256"));

            AssertInvalidArgument(MakeMarkerException(stagingHash: new string('0', 63)), "stagingInitSha256");
            AssertInvalidArgument(MakeMarkerException(stagingHash: new string('0', 65)), "stagingInitSha256");
            AssertInvalidArgument(MakeMarkerException(stagingHash: new string('A', 64)), "stagingInitSha256");
            AssertInvalidArgument(MakeMarkerException(stagingHash: new string('g', 64)), "stagingInitSha256");

            AssertInvalidArgument(MakeMarkerException(finalHash: new string('0', 63)), "finalInitSha256");
            AssertInvalidArgument(MakeMarkerException(finalHash: new string('0', 65)), "finalInitSha256");
            AssertInvalidArgument(MakeMarkerException(finalHash: new string('A', 64)), "finalInitSha256");
            AssertInvalidArgument(MakeMarkerException(finalHash: new string('g', 64)), "finalInitSha256");
        }

        // ---- Canonical serialization ----

        [Test]
        public void Serialize_GoldenJson()
        {
            string golden =
                "{\"SchemaVersion\":1,\"TestRunId\":1,\"RunInitializationId\":\"0123456789abcdef0123456789abcdef\"," +
                "\"StagingInitSha256\":\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"," +
                "\"FinalInitSha256\":\"fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210\"}";

            Assert.That(Json(Serialize(MakeMarker())), Is.EqualTo(golden));
        }

        [Test]
        public void Serialize_NoBomNoTrailingNewline()
        {
            byte[] bytes = Serialize(MakeMarker());

            Assert.That(bytes.Length, Is.GreaterThan(0));
            Assert.That(bytes[0], Is.Not.EqualTo((byte)0xEF));
            Assert.That(bytes[bytes.Length - 1], Is.Not.EqualTo((byte)'\n'));
            Assert.That(bytes[bytes.Length - 1], Is.Not.EqualTo((byte)'\r'));
        }

        [Test]
        public void Serialize_NoWhitespace()
        {
            string json = Json(Serialize(MakeMarker()));

            Assert.That(json, Does.Not.Contain(" "));
            Assert.That(json, Does.Not.Contain("\n"));
            Assert.That(json, Does.Not.Contain("\r"));
            Assert.That(json, Does.Not.Contain("\t"));
        }

        [Test]
        public void Serialize_Deterministic()
        {
            object marker = MakeMarker();
            byte[] first = Serialize(marker);
            byte[] second = Serialize(marker);

            Assert.That(second, Is.EqualTo(first));

            object twin = MakeMarker();
            Assert.That(Serialize(twin), Is.EqualTo(first));
        }

        [Test]
        public void Serialize_LongMaxValue_PlainInvariantDecimal()
        {
            string json = Json(Serialize(MakeMarker(testRunId: long.MaxValue)));

            Assert.That(json, Does.Contain("\"TestRunId\":9223372036854775807"));
            Assert.That(json, Does.Not.Contain("E+"));
            Assert.That(json, Does.Not.Contain("e+"));
        }

        [Test]
        public void Serialize_DoesNotMutateInput()
        {
            object marker = MakeMarker();
            byte[] before = Serialize(marker);

            byte[] after = Serialize(marker);

            Assert.That(after, Is.EqualTo(before));
            Assert.That((string)GetProperty(marker, "RunInitializationId"), Is.EqualTo(InitId));
            Assert.That((string)GetProperty(marker, "StagingInitSha256"), Is.EqualTo(StagingHash));
            Assert.That((string)GetProperty(marker, "FinalInitSha256"), Is.EqualTo(FinalHash));
        }

        [Test]
        public void Codec_4KiBConstant()
        {
            FieldInfo field = GetCodecType().GetField("MaximumCanonicalByteCount", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(field, Is.Not.Null);
            Assert.That((int)field.GetValue(null), Is.EqualTo(4 * 1024));
        }

        [Test]
        public void Codec_DoesNotExposeContentHashApi()
        {
            MethodInfo method = GetCodecType().GetMethod("ComputeContentSha256", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Null, "Ready codec must not compute hashes.");
        }

        // ---- Type shape ----

        [Test]
        public void Marker_NoPublicApiNoDisposableNoUnityObject()
        {
            Type type = GetMarkerType();

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty, "No public constructor.");
            Assert.That(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Empty, "No public properties.");
            Assert.That(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Empty, "No public methods.");
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void MarkerAndCodec_DoNotDependOnIoUnityRandomClock()
        {
            foreach (string relative in new[]
            {
                "Assets/Zantetsu/Runtime/Observability/CaptureRunReadyMarker.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunReadyMarkerCodec.cs"
            })
            {
                string source = File.ReadAllText(LocateSource(relative));
                Assert.That(source, Does.Not.Contain("System.IO"));
                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("System.Linq"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Debug."));
            }
        }

        private static void AssertInvalidArgument(Exception ex, string paramName)
        {
            Assert.That(ex, Is.TypeOf<ArgumentException>());
            Assert.That(((ArgumentException)ex).ParamName, Is.EqualTo(paramName));
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunReadyMarkerTests).Assembly.Location);
            while (dir != null)
            {
                string candidate = Path.Combine(dir, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                DirectoryInfo parent = Directory.GetParent(dir);
                if (parent == null)
                {
                    break;
                }

                dir = parent.FullName;
            }

            Assert.Fail("Source file not found: " + relativePath);
            return null;
        }
    }
}
