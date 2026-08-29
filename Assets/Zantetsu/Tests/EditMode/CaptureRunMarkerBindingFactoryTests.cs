using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using Zantetsu.Trace;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunMarkerBindingFactoryTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FinalHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private const string Unspecified = "\u0001";

        private static Type GetTypeFromAssembly(string simpleName)
        {
            Type type = typeof(TraceRunContext).Assembly.GetType("Zantetsu.Observability." + simpleName);
            Assert.That(type, Is.Not.Null, simpleName + " type not found.");
            return type;
        }

        private static Type GetFactoryType() => GetTypeFromAssembly("CaptureRunMarkerBindingFactory");

        private static Type GetInitMarkerType() => GetTypeFromAssembly("CaptureRunInitializationMarker");

        private static Type GetReadyMarkerType() => GetTypeFromAssembly("CaptureRunReadyMarker");

        private static Type GetRoleType() => GetTypeFromAssembly("CaptureRunRootRole");

        private static Type GetInitCodecType() => GetTypeFromAssembly("CaptureRunInitializationMarkerCodec");

        private static Type GetReadyCodecType() => GetTypeFromAssembly("CaptureRunReadyMarkerCodec");

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

        private static object Create(
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

            MethodInfo method = GetFactoryType().GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(null, new object[] { testRunId, initId, stagingHash, finalHash });
        }

        private static Exception CreateException(
            long testRunId = 1,
            string initId = Unspecified,
            string stagingHash = Unspecified,
            string finalHash = Unspecified)
        {
            try
            {
                Create(testRunId, initId, stagingHash, finalHash);
                return null;
            }
            catch (Exception ex)
            {
                return Unwrap(ex);
            }
        }

        private static string ComputeContentSha256(object initMarker)
        {
            MethodInfo method = GetInitCodecType().GetMethod("ComputeContentSha256", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return (string)method.Invoke(null, new object[] { initMarker });
        }

        private static byte[] SerializeReady(object readyMarker)
        {
            MethodInfo method = GetReadyCodecType().GetMethod("SerializeCanonical", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            return (byte[])method.Invoke(null, new object[] { readyMarker });
        }

        // ---- Generation ----

        [Test]
        public void Create_ValidInput_ReturnsBinding()
        {
            object binding = Create();
            Assert.That(binding, Is.Not.Null);
        }

        [Test]
        public void Init_Roles_AreStagingAndFinal()
        {
            object binding = Create();

            object stagingInit = GetProperty(binding, "StagingInitialization");
            object finalInit = GetProperty(binding, "FinalInitialization");

            Assert.That(GetProperty(stagingInit, "RootRole"), Is.EqualTo(Enum.Parse(GetRoleType(), "Staging")));
            Assert.That(GetProperty(finalInit, "RootRole"), Is.EqualTo(Enum.Parse(GetRoleType(), "Final")));
        }

        [Test]
        public void Values_ArePreservedExactly()
        {
            object binding = Create(42, InitId, StagingHash, FinalHash);

            Assert.That((long)GetProperty(binding, "TestRunId"), Is.EqualTo(42));
            Assert.That((string)GetProperty(binding, "RunInitializationId"), Is.EqualTo(InitId));
            Assert.That((string)GetProperty(binding, "StagingRunRootSha256"), Is.EqualTo(StagingHash));
            Assert.That((string)GetProperty(binding, "FinalRunRootSha256"), Is.EqualTo(FinalHash));

            object stagingInit = GetProperty(binding, "StagingInitialization");
            object finalInit = GetProperty(binding, "FinalInitialization");
            Assert.That((long)GetProperty(stagingInit, "TestRunId"), Is.EqualTo(42));
            Assert.That((string)GetProperty(finalInit, "RunInitializationId"), Is.EqualTo(InitId));
            Assert.That((string)GetProperty(stagingInit, "StagingRunRootSha256"), Is.EqualTo(StagingHash));
            Assert.That((string)GetProperty(finalInit, "FinalRunRootSha256"), Is.EqualTo(FinalHash));
        }

        [Test]
        public void ReadyHashes_MatchExistingCodec()
        {
            object binding = Create();

            object stagingInit = GetProperty(binding, "StagingInitialization");
            object finalInit = GetProperty(binding, "FinalInitialization");
            object stagingReady = GetProperty(binding, "StagingReady");

            string expectedStaging = ComputeContentSha256(stagingInit);
            string expectedFinal = ComputeContentSha256(finalInit);

            Assert.That((string)GetProperty(stagingReady, "StagingInitSha256"), Is.EqualTo(expectedStaging));
            Assert.That((string)GetProperty(stagingReady, "FinalInitSha256"), Is.EqualTo(expectedFinal));
        }

        [Test]
        public void Ready_AreDistinctInstances()
        {
            object binding = Create();

            object stagingReady = GetProperty(binding, "StagingReady");
            object finalReady = GetProperty(binding, "FinalReady");

            Assert.That(finalReady, Is.Not.SameAs(stagingReady));
        }

        [Test]
        public void Ready_CanonicalBytes_Identical()
        {
            object binding = Create();

            object stagingReady = GetProperty(binding, "StagingReady");
            object finalReady = GetProperty(binding, "FinalReady");

            byte[] stagingBytes = SerializeReady(stagingReady);
            byte[] finalBytes = SerializeReady(finalReady);

            Assert.That(finalBytes, Is.EqualTo(stagingBytes));
        }

        [Test]
        public void Binding_HoldsMarkers_ByReference()
        {
            object binding = Create();

            object stagingInit = GetProperty(binding, "StagingInitialization");
            Assert.That(GetProperty(binding, "StagingInitialization"), Is.SameAs(stagingInit));

            object finalInit = GetProperty(binding, "FinalInitialization");
            Assert.That(finalInit, Is.Not.SameAs(stagingInit));

            object stagingReady = GetProperty(binding, "StagingReady");
            object finalReady = GetProperty(binding, "FinalReady");
            Assert.That(stagingReady, Is.Not.SameAs(stagingInit));
            Assert.That(finalReady, Is.Not.SameAs(stagingReady));
        }

        // ---- Validation delegation ----

        [Test]
        public void TestRunId_ZeroAndNegative_Rejected()
        {
            Exception zero = CreateException(testRunId: 0);
            Assert.That(zero, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)zero).ParamName, Is.EqualTo("testRunId"));

            Exception negative = CreateException(testRunId: -1);
            Assert.That(negative, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(((ArgumentOutOfRangeException)negative).ParamName, Is.EqualTo("testRunId"));
        }

        [Test]
        public void InitializationId_Invalid_Rejected()
        {
            Exception nullId = CreateException(initId: null);
            Assert.That(nullId, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullId).ParamName, Is.EqualTo("runInitializationId"));

            AssertInvalidArgument(CreateException(initId: new string('0', 31)), "runInitializationId");
            AssertInvalidArgument(CreateException(initId: new string('0', 33)), "runInitializationId");
            AssertInvalidArgument(CreateException(initId: new string('A', 32)), "runInitializationId");
            AssertInvalidArgument(CreateException(initId: new string('g', 32)), "runInitializationId");
        }

        [Test]
        public void StagingRootHash_Invalid_Rejected()
        {
            Exception nullHash = CreateException(stagingHash: null);
            Assert.That(nullHash, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullHash).ParamName, Is.EqualTo("stagingRunRootSha256"));

            AssertInvalidArgument(CreateException(stagingHash: new string('0', 63)), "stagingRunRootSha256");
            AssertInvalidArgument(CreateException(stagingHash: new string('0', 65)), "stagingRunRootSha256");
            AssertInvalidArgument(CreateException(stagingHash: new string('A', 64)), "stagingRunRootSha256");
            AssertInvalidArgument(CreateException(stagingHash: new string('g', 64)), "stagingRunRootSha256");
        }

        [Test]
        public void FinalRootHash_Invalid_Rejected()
        {
            Exception nullHash = CreateException(finalHash: null);
            Assert.That(nullHash, Is.TypeOf<ArgumentNullException>());
            Assert.That(((ArgumentNullException)nullHash).ParamName, Is.EqualTo("finalRunRootSha256"));

            AssertInvalidArgument(CreateException(finalHash: new string('0', 63)), "finalRunRootSha256");
            AssertInvalidArgument(CreateException(finalHash: new string('0', 65)), "finalRunRootSha256");
            AssertInvalidArgument(CreateException(finalHash: new string('A', 64)), "finalRunRootSha256");
            AssertInvalidArgument(CreateException(finalHash: new string('g', 64)), "finalRunRootSha256");
        }

        [Test]
        public void Exceptions_AreNotTransformedOrWrapped()
        {
            Exception ex = CreateException(testRunId: 0);
            Assert.That(ex, Is.TypeOf<ArgumentOutOfRangeException>());
            Assert.That(ex, Is.Not.TypeOf<InvalidOperationException>());
            Assert.That(ex, Is.Not.TypeOf<InvalidDataException>());
        }

        // ---- Independence / purity ----

        [Test]
        public void TwoCreates_AreIndependent()
        {
            object first = Create();
            object second = Create();

            Assert.That(second, Is.Not.SameAs(first));

            Assert.That(GetProperty(second, "StagingInitialization"), Is.Not.SameAs(GetProperty(first, "StagingInitialization")));
            Assert.That(GetProperty(second, "FinalInitialization"), Is.Not.SameAs(GetProperty(first, "FinalInitialization")));
            Assert.That(GetProperty(second, "StagingReady"), Is.Not.SameAs(GetProperty(first, "StagingReady")));
            Assert.That(GetProperty(second, "FinalReady"), Is.Not.SameAs(GetProperty(first, "FinalReady")));
        }

        [Test]
        public void DoesNotModifyInputs()
        {
            string initId = InitId;
            string stagingHash = StagingHash;
            string finalHash = FinalHash;

            object binding = Create(1, initId, stagingHash, finalHash);

            Assert.That(initId, Is.EqualTo(InitId));
            Assert.That(stagingHash, Is.EqualTo(StagingHash));
            Assert.That(finalHash, Is.EqualTo(FinalHash));
            Assert.That((string)GetProperty(binding, "RunInitializationId"), Is.EqualTo(InitId));
            Assert.That((string)GetProperty(binding, "StagingRunRootSha256"), Is.EqualTo(StagingHash));
            Assert.That((string)GetProperty(binding, "FinalRunRootSha256"), Is.EqualTo(FinalHash));
        }

        // ---- Shape / responsibilities ----

        [Test]
        public void Factory_HasNoFieldsAndNoPublicApi()
        {
            Type type = GetFactoryType();

            Assert.That(type.IsAbstract, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance), Is.Empty, "Factory must have no fields.");
            Assert.That(type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly), Is.Empty, "Factory must expose no public API.");
        }

        [Test]
        public void Source_NoIoUnityRandomClock()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunMarkerBindingFactory.cs"));

            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain("Random"));
            Assert.That(source, Does.Not.Contain("DateTime"));
            Assert.That(source, Does.Not.Contain("Debug."));
        }

        [Test]
        public void Source_DelegatesContentHashAndNeverRecomputesRootHash()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunMarkerBindingFactory.cs"));

            // Content hash is delegated to the existing codec.
            Assert.That(source, Does.Contain("CaptureRunInitializationMarkerCodec"));
            Assert.That(source, Does.Contain("ComputeContentSha256"));

            // No SHA-256 is recomputed here (root hashes stay opaque).
            Assert.That(source, Does.Not.Contain("SHA256"));
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunMarkerBindingFactoryTests).Assembly.Location);
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
