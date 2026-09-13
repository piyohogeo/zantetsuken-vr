using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunMarkerBindingTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string StagingHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string FinalHash = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private static CaptureRunMarkerBinding Create(
            long testRunId = 1,
            string initId = InitId,
            string stagingHash = StagingHash,
            string finalHash = FinalHash)
        {
            return new CaptureRunMarkerBinding(testRunId, initId, stagingHash, finalHash);
        }

        private static Exception CreateException(
            long testRunId = 1,
            string initId = InitId,
            string stagingHash = StagingHash,
            string finalHash = FinalHash)
        {
            try
            {
                Create(testRunId, initId, stagingHash, finalHash);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        // ---- Generation ----

        [Test]
        public void Create_ValidInput_ReturnsBinding()
        {
            Assert.That(Create(), Is.Not.Null);
        }

        [Test]
        public void Init_Roles_AreStagingAndFinal()
        {
            CaptureRunMarkerBinding binding = Create();

            Assert.That(binding.StagingInitialization.RootRole, Is.EqualTo(CaptureRunRootRole.Staging));
            Assert.That(binding.FinalInitialization.RootRole, Is.EqualTo(CaptureRunRootRole.Final));
        }

        [Test]
        public void Values_ArePreservedExactly()
        {
            CaptureRunMarkerBinding binding = Create(42, InitId, StagingHash, FinalHash);

            Assert.That(binding.TestRunId, Is.EqualTo(42));
            Assert.That(binding.RunInitializationId, Is.EqualTo(InitId));
            Assert.That(binding.StagingRunRootSha256, Is.EqualTo(StagingHash));
            Assert.That(binding.FinalRunRootSha256, Is.EqualTo(FinalHash));

            Assert.That(binding.StagingInitialization.TestRunId, Is.EqualTo(42));
            Assert.That(binding.FinalInitialization.RunInitializationId, Is.EqualTo(InitId));
            Assert.That(binding.StagingInitialization.StagingRunRootSha256, Is.EqualTo(StagingHash));
            Assert.That(binding.FinalInitialization.FinalRunRootSha256, Is.EqualTo(FinalHash));
        }

        [Test]
        public void ReadyHashes_MatchExistingCodec()
        {
            CaptureRunMarkerBinding binding = Create();

            string expectedStaging = CaptureRunInitializationMarkerCodec.ComputeContentSha256(binding.StagingInitialization);
            string expectedFinal = CaptureRunInitializationMarkerCodec.ComputeContentSha256(binding.FinalInitialization);

            Assert.That(binding.StagingReady.StagingInitSha256, Is.EqualTo(expectedStaging));
            Assert.That(binding.StagingReady.FinalInitSha256, Is.EqualTo(expectedFinal));
        }

        [Test]
        public void Ready_CanonicalBytes_Identical()
        {
            CaptureRunMarkerBinding binding = Create();

            byte[] stagingBytes = CaptureRunReadyMarkerCodec.SerializeCanonical(binding.StagingReady);
            byte[] finalBytes = CaptureRunReadyMarkerCodec.SerializeCanonical(binding.FinalReady);

            Assert.That(finalBytes, Is.EqualTo(stagingBytes));
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
            CaptureRunMarkerBinding first = Create();
            CaptureRunMarkerBinding second = Create();

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.StagingInitialization, Is.Not.SameAs(first.StagingInitialization));
            Assert.That(second.FinalInitialization, Is.Not.SameAs(first.FinalInitialization));
            Assert.That(second.StagingReady, Is.Not.SameAs(first.StagingReady));
        }

        [Test]
        public void DoesNotModifyInputs()
        {
            string initId = InitId;
            string stagingHash = StagingHash;
            string finalHash = FinalHash;

            CaptureRunMarkerBinding binding = Create(1, initId, stagingHash, finalHash);

            Assert.That(initId, Is.EqualTo(InitId));
            Assert.That(stagingHash, Is.EqualTo(StagingHash));
            Assert.That(finalHash, Is.EqualTo(FinalHash));
            Assert.That(binding.RunInitializationId, Is.EqualTo(InitId));
            Assert.That(binding.StagingRunRootSha256, Is.EqualTo(StagingHash));
            Assert.That(binding.FinalRunRootSha256, Is.EqualTo(FinalHash));
        }

        // ---- Shape / responsibilities ----

        [Test]
        public void NoPublicApi_Sealed_NotDisposable_NotUnityObject()
        {
            Type type = typeof(CaptureRunMarkerBinding);

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
        public void Source_DoesNotReimplementHashOrTouchIoUnityRandomClock()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunMarkerBinding.cs"));

            Assert.That(source, Does.Not.Contain("SHA256"));
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain("Random"));
            Assert.That(source, Does.Not.Contain("DateTime"));
            Assert.That(source, Does.Not.Contain("Debug."));
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunMarkerBindingTests).Assembly.Location);
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
