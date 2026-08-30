using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunInitializationDocumentSetFactoryTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const int MaxByteCount = 4 * 1024;

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static string Separator => Path.DirectorySeparatorChar.ToString();

        private static string StagingBaseRoot() => IsWindows ? "C:\\staging" : "/staging";

        private static string FinalBaseRoot() => IsWindows ? "D:\\final" : "/final";

        private static CaptureRunRootLayout MakeLayout(long testRunId = 1)
        {
            return new CaptureRunRootLayout(StagingBaseRoot(), FinalBaseRoot(), testRunId);
        }

        private static CaptureRunInitializationDocumentSet Create(CaptureRunRootLayout layout, string initId = InitId)
        {
            return CaptureRunInitializationDocumentSetFactory.Create(layout, initId);
        }

        private static byte[] GetFieldBytes(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            return (byte[])field.GetValue(target);
        }

        // ---- Construction ----

        [Test]
        public void NullRootLayout_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => Create(null));
            Assert.That(ex.ParamName, Is.EqualTo("rootLayout"));
        }

        [Test]
        public void ValidInput_ProducesDocumentSet()
        {
            CaptureRunInitializationDocumentSet result = Create(MakeLayout());

            Assert.That(result, Is.Not.Null);
        }

        [Test]
        public void RootLayout_ReferenceEqual()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationDocumentSet result = Create(layout);

            Assert.That(result.Plan, Is.Not.Null);
            Assert.That(result.Plan.MarkerPaths, Is.Not.Null);
            Assert.That(result.Plan.MarkerPaths.RootLayout, Is.SameAs(layout));
        }

        [Test]
        public void DocumentSet_HoldsPlanByReference()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationDocumentSet result = Create(layout);

            Assert.That(result.Plan, Is.Not.Null);
            Assert.That(result.Plan.MarkerPaths.RootLayout, Is.SameAs(layout));
            Assert.That(result.Plan.MarkerBinding, Is.Not.Null);
        }

        [Test]
        public void ForwardingValues_Exact()
        {
            CaptureRunRootLayout layout = MakeLayout(13);
            CaptureRunInitializationDocumentSet result = Create(layout);

            Assert.That(result.Plan.TestRunId, Is.EqualTo(layout.TestRunId));
            Assert.That(result.Plan.RunInitializationId, Is.EqualTo(InitId));
            Assert.That(result.Plan.StagingRunRoot, Is.EqualTo(layout.StagingRunRoot));
            Assert.That(result.Plan.FinalRunRoot, Is.EqualTo(layout.FinalRunRoot));
            Assert.That(result.Plan.StagingRunRootSha256, Is.EqualTo(layout.StagingRunRootSha256));
            Assert.That(result.Plan.FinalRunRootSha256, Is.EqualTo(layout.FinalRunRootSha256));
        }

        [Test]
        public void MarkerPaths_FollowPathSetRules()
        {
            CaptureRunRootLayout layout = MakeLayout(7);
            CaptureRunInitializationDocumentSet result = Create(layout);

            CaptureRunMarkerPathSet paths = result.Plan.MarkerPaths;
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
            CaptureRunInitializationDocumentSet result = Create(layout);

            CaptureRunMarkerBinding binding = result.Plan.MarkerBinding;

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
        public void ReadyMarkers_DistinctInstances_AllValuesEqual()
        {
            CaptureRunInitializationDocumentSet result = Create(MakeLayout());

            CaptureRunMarkerBinding binding = result.Plan.MarkerBinding;

            Assert.That(binding.FinalReady, Is.Not.SameAs(binding.StagingReady));
            Assert.That(binding.StagingReady.TestRunId, Is.EqualTo(binding.FinalReady.TestRunId));
            Assert.That(binding.StagingReady.RunInitializationId, Is.EqualTo(binding.FinalReady.RunInitializationId));
            Assert.That(binding.StagingReady.StagingInitSha256, Is.EqualTo(binding.FinalReady.StagingInitSha256));
            Assert.That(binding.StagingReady.FinalInitSha256, Is.EqualTo(binding.FinalReady.FinalInitSha256));
        }

        [Test]
        public void CanonicalBytes_MatchCodecs()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationDocumentSet result = Create(layout);

            byte[] stagingInitExpected = CaptureRunInitializationMarkerCodec.SerializeCanonical(result.Plan.MarkerBinding.StagingInitialization);
            byte[] finalInitExpected = CaptureRunInitializationMarkerCodec.SerializeCanonical(result.Plan.MarkerBinding.FinalInitialization);
            byte[] readyExpected = CaptureRunReadyMarkerCodec.SerializeCanonical(result.Plan.MarkerBinding.StagingReady);

            Assert.That(result.GetStagingInitializationBytes(), Is.EqualTo(stagingInitExpected));
            Assert.That(result.GetFinalInitializationBytes(), Is.EqualTo(finalInitExpected));
            Assert.That(result.GetStagingReadyBytes(), Is.EqualTo(readyExpected));
            Assert.That(result.GetFinalReadyBytes(), Is.EqualTo(readyExpected));
        }

        [Test]
        public void ByteCounts_NonEmptyWithinLimit()
        {
            CaptureRunInitializationDocumentSet result = Create(MakeLayout());

            Assert.That(result.StagingInitializationByteCount, Is.EqualTo(result.GetStagingInitializationBytes().Length));
            Assert.That(result.FinalInitializationByteCount, Is.EqualTo(result.GetFinalInitializationBytes().Length));
            Assert.That(result.ReadyByteCount, Is.EqualTo(result.GetStagingReadyBytes().Length));

            Assert.That(result.StagingInitializationByteCount, Is.GreaterThan(0));
            Assert.That(result.FinalInitializationByteCount, Is.GreaterThan(0));
            Assert.That(result.ReadyByteCount, Is.GreaterThan(0));

            Assert.That(result.StagingInitializationByteCount, Is.LessThanOrEqualTo(MaxByteCount));
            Assert.That(result.FinalInitializationByteCount, Is.LessThanOrEqualTo(MaxByteCount));
            Assert.That(result.ReadyByteCount, Is.LessThanOrEqualTo(MaxByteCount));
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

        // ---- Ownership / independence ----

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

            CaptureRunInitializationDocumentSet result = Create(layout, initId);

            Assert.That(layout.TestRunId, Is.EqualTo(testRunIdBefore));
            Assert.That(layout.StagingRunRoot, Is.EqualTo(stagingRootBefore));
            Assert.That(layout.FinalRunRoot, Is.EqualTo(finalRootBefore));
            Assert.That(layout.StagingRunRootSha256, Is.EqualTo(stagingHashBefore));
            Assert.That(layout.FinalRunRootSha256, Is.EqualTo(finalHashBefore));
            Assert.That(initId, Is.EqualTo(InitId));
            Assert.That(result.Plan.RunInitializationId, Is.EqualTo(InitId));
        }

        [Test]
        public void ConsecutiveCalls_ShareNothingButRootLayout()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationDocumentSet first = Create(layout);
            CaptureRunInitializationDocumentSet second = Create(layout);

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.Plan, Is.Not.SameAs(first.Plan));
            Assert.That(second.Plan.MarkerPaths, Is.Not.SameAs(first.Plan.MarkerPaths));
            Assert.That(second.Plan.MarkerBinding, Is.Not.SameAs(first.Plan.MarkerBinding));

            Assert.That(second.Plan.MarkerBinding.StagingInitialization, Is.Not.SameAs(first.Plan.MarkerBinding.StagingInitialization));
            Assert.That(second.Plan.MarkerBinding.FinalInitialization, Is.Not.SameAs(first.Plan.MarkerBinding.FinalInitialization));
            Assert.That(second.Plan.MarkerBinding.StagingReady, Is.Not.SameAs(first.Plan.MarkerBinding.StagingReady));
            Assert.That(second.Plan.MarkerBinding.FinalReady, Is.Not.SameAs(first.Plan.MarkerBinding.FinalReady));

            Assert.That(GetFieldBytes(second, "_stagingInitializationBytes"), Is.Not.SameAs(GetFieldBytes(first, "_stagingInitializationBytes")));
            Assert.That(GetFieldBytes(second, "_finalInitializationBytes"), Is.Not.SameAs(GetFieldBytes(first, "_finalInitializationBytes")));
            Assert.That(GetFieldBytes(second, "_readyBytes"), Is.Not.SameAs(GetFieldBytes(first, "_readyBytes")));

            Assert.That(second.Plan.MarkerPaths.RootLayout, Is.SameAs(layout));
            Assert.That(first.Plan.MarkerPaths.RootLayout, Is.SameAs(layout));
        }

        // ---- Shape ----

        [Test]
        public void NoFields_NoPublicApi_NotDisposable()
        {
            Type type = typeof(CaptureRunInitializationDocumentSetFactory);

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
            Type type = typeof(CaptureRunInitializationDocumentSetFactory);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
            }
        }

        // ---- Source inspection ----

        [Test]
        public void Source_OnlyPlanFactoryAndDocumentSet_NoOtherConstruction()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationDocumentSetFactory.cs"));

            Assert.That(source, Does.Contain("CaptureRunInitializationPlanFactory"));
            Assert.That(source, Does.Contain("new CaptureRunInitializationDocumentSet"));

            Assert.That(source, Does.Not.Contain("CaptureRunMarkerBindingFactory"));
            Assert.That(source, Does.Not.Contain("CaptureRunMarkerPathSet"));
            Assert.That(source, Does.Not.Contain("CaptureRunMarkerBinding"));
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationMarker"));
            Assert.That(source, Does.Not.Contain("CaptureRunReadyMarker"));
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationMarkerCodec"));
            Assert.That(source, Does.Not.Contain("CaptureRunReadyMarkerCodec"));
            Assert.That(source, Does.Not.Contain("PngJsonCapturePublicationPlanCodec"));
            Assert.That(source, Does.Not.Contain("CaptureFramePngArtifactCodec"));
            Assert.That(source, Does.Not.Contain("TraceRunManifestCodec"));
            Assert.That(source, Does.Not.Contain("TraceBinaryCodec"));
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationIdGenerator"));
            Assert.That(source, Does.Not.Contain("SHA256"));
            Assert.That(source, Does.Not.Contain("SHA-256"));
            Assert.That(source, Does.Not.Contain("System.Security.Cryptography"));
            Assert.That(source, Does.Not.Contain("byte["));
            Assert.That(source, Does.Not.Contain("new byte"));
            Assert.That(source, Does.Not.Contain("Path."));
        }

        [Test]
        public void Source_NoFilesystemPInvokeUnityRandomClock()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationDocumentSetFactory.cs"));

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

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationDocumentSetFactoryTests).Assembly.Location);
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
