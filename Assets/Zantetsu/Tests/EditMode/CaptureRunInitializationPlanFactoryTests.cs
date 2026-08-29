using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunInitializationPlanFactoryTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static string Separator => Path.DirectorySeparatorChar.ToString();

        private static string StagingBaseRoot() => IsWindows ? "C:\\staging" : "/staging";

        private static string FinalBaseRoot() => IsWindows ? "D:\\final" : "/final";

        private static CaptureRunRootLayout MakeLayout(long testRunId = 1)
        {
            return new CaptureRunRootLayout(StagingBaseRoot(), FinalBaseRoot(), testRunId);
        }

        private static CaptureRunInitializationPlan Create(CaptureRunRootLayout layout, string initId = InitId)
        {
            return CaptureRunInitializationPlanFactory.Create(layout, initId);
        }

        // ---- Construction ----

        [Test]
        public void NullRootLayout_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => Create(null));
            Assert.That(ex.ParamName, Is.EqualTo("rootLayout"));
        }

        [Test]
        public void ValidInput_ProducesPlan()
        {
            CaptureRunInitializationPlan plan = Create(MakeLayout());

            Assert.That(plan, Is.Not.Null);
        }

        [Test]
        public void PlanToRootLayout_ReferenceEqual()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationPlan plan = Create(layout);

            Assert.That(plan.MarkerPaths, Is.Not.Null);
            Assert.That(plan.MarkerPaths.RootLayout, Is.SameAs(layout));
            Assert.That(plan.MarkerBinding, Is.Not.Null);
        }

        [Test]
        public void ForwardingValues_Exact()
        {
            CaptureRunRootLayout layout = MakeLayout(11);
            CaptureRunInitializationPlan plan = Create(layout);

            Assert.That(plan.TestRunId, Is.EqualTo(layout.TestRunId));
            Assert.That(plan.RunInitializationId, Is.EqualTo(InitId));
            Assert.That(plan.StagingRunRoot, Is.EqualTo(layout.StagingRunRoot));
            Assert.That(plan.FinalRunRoot, Is.EqualTo(layout.FinalRunRoot));
            Assert.That(plan.StagingRunRootSha256, Is.EqualTo(layout.StagingRunRootSha256));
            Assert.That(plan.FinalRunRootSha256, Is.EqualTo(layout.FinalRunRootSha256));
        }

        [Test]
        public void MarkerPaths_FollowPathSetRules()
        {
            CaptureRunRootLayout layout = MakeLayout(7);
            CaptureRunInitializationPlan plan = Create(layout);

            CaptureRunMarkerPathSet paths = plan.MarkerPaths;
            string sep = Separator;
            string staging = layout.StagingRunRoot;
            string final = layout.FinalRunRoot;

            Assert.That(paths.StagingInitializationTemporaryPath, Is.EqualTo(staging + sep + "run.init.tmp"));
            Assert.That(paths.StagingInitializationPath, Is.EqualTo(staging + sep + "run.init"));
            Assert.That(paths.StagingReadyTemporaryPath, Is.EqualTo(staging + sep + "run.ready.tmp"));
            Assert.That(paths.StagingReadyPath, Is.EqualTo(staging + sep + "run.ready"));
            Assert.That(paths.FinalInitializationTemporaryPath, Is.EqualTo(final + sep + "run.init.tmp"));
            Assert.That(paths.FinalInitializationPath, Is.EqualTo(final + sep + "run.init"));
            Assert.That(paths.FinalReadyTemporaryPath, Is.EqualTo(final + sep + "run.ready.tmp"));
            Assert.That(paths.FinalReadyPath, Is.EqualTo(final + sep + "run.ready"));
        }

        [Test]
        public void InitMarkers_HaveRolesAndValues()
        {
            CaptureRunRootLayout layout = MakeLayout(3);
            CaptureRunInitializationPlan plan = Create(layout);

            CaptureRunMarkerBinding binding = plan.MarkerBinding;

            Assert.That(binding.StagingInitialization.RootRole, Is.EqualTo(CaptureRunRootRole.Staging));
            Assert.That(binding.FinalInitialization.RootRole, Is.EqualTo(CaptureRunRootRole.Final));

            Assert.That(binding.StagingInitialization.TestRunId, Is.EqualTo(layout.TestRunId));
            Assert.That(binding.StagingInitialization.RunInitializationId, Is.EqualTo(InitId));
            Assert.That(binding.StagingInitialization.StagingRunRootSha256, Is.EqualTo(layout.StagingRunRootSha256));
            Assert.That(binding.StagingInitialization.FinalRunRootSha256, Is.EqualTo(layout.FinalRunRootSha256));

            Assert.That(binding.FinalInitialization.TestRunId, Is.EqualTo(layout.TestRunId));
            Assert.That(binding.FinalInitialization.RunInitializationId, Is.EqualTo(InitId));
            Assert.That(binding.FinalInitialization.StagingRunRootSha256, Is.EqualTo(layout.StagingRunRootSha256));
            Assert.That(binding.FinalInitialization.FinalRunRootSha256, Is.EqualTo(layout.FinalRunRootSha256));
        }

        [Test]
        public void ReadyHashes_MatchInitContentHashes()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationPlan plan = Create(layout);

            CaptureRunMarkerBinding binding = plan.MarkerBinding;

            string stagingInitHash = CaptureRunInitializationMarkerCodec.ComputeContentSha256(binding.StagingInitialization);
            string finalInitHash = CaptureRunInitializationMarkerCodec.ComputeContentSha256(binding.FinalInitialization);

            Assert.That(binding.StagingReady.StagingInitSha256, Is.EqualTo(stagingInitHash));
            Assert.That(binding.StagingReady.FinalInitSha256, Is.EqualTo(finalInitHash));
            Assert.That(binding.FinalReady.StagingInitSha256, Is.EqualTo(stagingInitHash));
            Assert.That(binding.FinalReady.FinalInitSha256, Is.EqualTo(finalInitHash));
        }

        [Test]
        public void ReadyMarkers_DistinctInstances_CanonicalEqual()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationPlan plan = Create(layout);

            CaptureRunMarkerBinding binding = plan.MarkerBinding;

            Assert.That(binding.FinalReady, Is.Not.SameAs(binding.StagingReady));

            byte[] stagingBytes = CaptureRunReadyMarkerCodec.SerializeCanonical(binding.StagingReady);
            byte[] finalBytes = CaptureRunReadyMarkerCodec.SerializeCanonical(binding.FinalReady);

            Assert.That(finalBytes, Is.EqualTo(stagingBytes));
            Assert.That(binding.StagingReady.TestRunId, Is.EqualTo(binding.FinalReady.TestRunId));
            Assert.That(binding.StagingReady.RunInitializationId, Is.EqualTo(binding.FinalReady.RunInitializationId));
            Assert.That(binding.StagingReady.StagingInitSha256, Is.EqualTo(binding.FinalReady.StagingInitSha256));
            Assert.That(binding.StagingReady.FinalInitSha256, Is.EqualTo(binding.FinalReady.FinalInitSha256));
        }

        // ---- Initialization ID delegation ----

        [Test]
        public void NullInitializationId_RejectedWithParamName()
        {
            CaptureRunRootLayout layout = MakeLayout();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => Create(layout, null));
            Assert.That(ex.ParamName, Is.EqualTo("runInitializationId"));
        }

        [Test]
        public void InvalidInitializationIds_Rejected()
        {
            CaptureRunRootLayout layout = MakeLayout();

            Assert.Throws<ArgumentException>(() => Create(layout, new string('a', 31)));
            Assert.Throws<ArgumentException>(() => Create(layout, new string('a', 33)));
            Assert.Throws<ArgumentException>(() => Create(layout, InitId.ToUpperInvariant()));
            Assert.Throws<ArgumentException>(() => Create(layout, "0123456789abcdef0123456789abcdeg"));
        }

        [Test]
        public void Exceptions_NotTransformedOrAggregated()
        {
            CaptureRunRootLayout layout = MakeLayout();

            ArgumentException ex = Assert.Throws<ArgumentException>(() => Create(layout, new string('a', 31)));

            Assert.That(ex.GetType(), Is.EqualTo(typeof(ArgumentException)));
            Assert.That(ex.ParamName, Is.EqualTo("runInitializationId"));
            Assert.That(ex.InnerException, Is.Null);
        }

        // ---- Ownership / side effects ----

        [Test]
        public void Inputs_NotChanged()
        {
            CaptureRunRootLayout layout = MakeLayout(5);
            string initId = InitId;

            long testRunIdBefore = layout.TestRunId;
            string stagingRootBefore = layout.StagingRunRoot;
            string finalRootBefore = layout.FinalRunRoot;
            string stagingHashBefore = layout.StagingRunRootSha256;
            string finalHashBefore = layout.FinalRunRootSha256;

            CaptureRunInitializationPlan plan = Create(layout, initId);

            Assert.That(layout.TestRunId, Is.EqualTo(testRunIdBefore));
            Assert.That(layout.StagingRunRoot, Is.EqualTo(stagingRootBefore));
            Assert.That(layout.FinalRunRoot, Is.EqualTo(finalRootBefore));
            Assert.That(layout.StagingRunRootSha256, Is.EqualTo(stagingHashBefore));
            Assert.That(layout.FinalRunRootSha256, Is.EqualTo(finalHashBefore));
            Assert.That(initId, Is.EqualTo(InitId));
            Assert.That(plan.RunInitializationId, Is.EqualTo(InitId));
        }

        [Test]
        public void ConsecutiveCalls_ProduceIndependentInstances()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationPlan first = Create(layout);
            CaptureRunInitializationPlan second = Create(layout);

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.MarkerPaths, Is.Not.SameAs(first.MarkerPaths));
            Assert.That(second.MarkerBinding, Is.Not.SameAs(first.MarkerBinding));

            Assert.That(second.MarkerBinding.StagingInitialization, Is.Not.SameAs(first.MarkerBinding.StagingInitialization));
            Assert.That(second.MarkerBinding.FinalInitialization, Is.Not.SameAs(first.MarkerBinding.FinalInitialization));
            Assert.That(second.MarkerBinding.StagingReady, Is.Not.SameAs(first.MarkerBinding.StagingReady));
            Assert.That(second.MarkerBinding.FinalReady, Is.Not.SameAs(first.MarkerBinding.FinalReady));

            Assert.That(second.RunInitializationId, Is.EqualTo(first.RunInitializationId));
            Assert.That(second.TestRunId, Is.EqualTo(first.TestRunId));
        }

        // ---- Shape ----

        [Test]
        public void NoFields_NoPublicApi_NotDisposable()
        {
            Type type = typeof(CaptureRunInitializationPlanFactory);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsAbstract && type.IsSealed, Is.True, "Must be a static class.");
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static), Is.Empty, "No fields.");
            Assert.That(type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Empty, "No public properties.");
            Assert.That(type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Empty, "No public methods.");
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void NoStaticMutableState()
        {
            Type type = typeof(CaptureRunInitializationPlanFactory);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
            }
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoIdGeneratorCodecHashMarkerCtorPathCombine()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationPlanFactory.cs"));

            Assert.That(source, Does.Not.Contain("CaptureRunInitializationIdGenerator"));
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationMarkerCodec"));
            Assert.That(source, Does.Not.Contain("CaptureRunReadyMarkerCodec"));
            Assert.That(source, Does.Not.Contain("ComputeContentSha256"));
            Assert.That(source, Does.Not.Contain("System.Security.Cryptography"));
            Assert.That(source, Does.Not.Contain("SHA256"));
            Assert.That(source, Does.Not.Contain("SHA-256"));
            Assert.That(source, Does.Not.Contain("new CaptureRunInitializationMarker"));
            Assert.That(source, Does.Not.Contain("new CaptureRunReadyMarker"));
            Assert.That(source, Does.Not.Contain("Path.Combine"));
        }

        [Test]
        public void Source_NoFilesystemPInvokeUnityRandomClock()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationPlanFactory.cs"));

            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("Stream"));
            Assert.That(source, Does.Not.Contain("DllImport"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain("Random"));
            Assert.That(source, Does.Not.Contain("DateTime"));
            Assert.That(source, Does.Not.Contain("Debug."));
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationPlanFactoryTests).Assembly.Location);
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
