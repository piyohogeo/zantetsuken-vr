using System;
using System.Collections;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunInitializationDocumentSetTests
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
            return new CaptureRunInitializationDocumentSet(layout, initId);
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

            Assert.That(result.MarkerPaths, Is.Not.Null);
            Assert.That(result.MarkerBinding, Is.Not.Null);
            Assert.That(result.MarkerPaths.RootLayout, Is.SameAs(layout));
            Assert.That(result.RootLayout, Is.SameAs(layout));
        }

        [Test]
        public void ForwardingValues_Exact()
        {
            CaptureRunRootLayout layout = MakeLayout(13);
            CaptureRunInitializationDocumentSet result = Create(layout);

            Assert.That(result.TestRunId, Is.EqualTo(layout.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(InitId));
            Assert.That(result.RootLayout.StagingRunRoot, Is.EqualTo(layout.StagingRunRoot));
            Assert.That(result.RootLayout.FinalRunRoot, Is.EqualTo(layout.FinalRunRoot));
            Assert.That(result.RootLayout.StagingRunRootSha256, Is.EqualTo(layout.StagingRunRootSha256));
            Assert.That(result.RootLayout.FinalRunRootSha256, Is.EqualTo(layout.FinalRunRootSha256));
        }

        [Test]
        public void MarkerPaths_FollowPathSetRules()
        {
            CaptureRunRootLayout layout = MakeLayout(7);
            CaptureRunInitializationDocumentSet result = Create(layout);

            CaptureRunMarkerPathSet paths = result.MarkerPaths;
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

            CaptureRunMarkerBinding binding = result.MarkerBinding;

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

        // ---- Canonical bytes ----

        [Test]
        public void StagingInitBytes_MatchCodec()
        {
            CaptureRunInitializationDocumentSet set = Create(MakeLayout());

            byte[] expected = CaptureRunInitializationMarkerCodec.SerializeCanonical(set.MarkerBinding.StagingInitialization);

            Assert.That(set.GetStagingInitializationBytes(), Is.EqualTo(expected));
        }

        [Test]
        public void FinalInitBytes_MatchCodec()
        {
            CaptureRunInitializationDocumentSet set = Create(MakeLayout());

            byte[] expected = CaptureRunInitializationMarkerCodec.SerializeCanonical(set.MarkerBinding.FinalInitialization);

            Assert.That(set.GetFinalInitializationBytes(), Is.EqualTo(expected));
        }

        [Test]
        public void ReadyBytes_MatchCodec()
        {
            CaptureRunInitializationDocumentSet set = Create(MakeLayout());

            byte[] expected = CaptureRunReadyMarkerCodec.SerializeCanonical(set.MarkerBinding.StagingReady);

            Assert.That(set.GetStagingReadyBytes(), Is.EqualTo(expected));
            Assert.That(set.GetFinalReadyBytes(), Is.EqualTo(expected));
        }

        [Test]
        public void ReadyBytes_ValueEqual()
        {
            CaptureRunInitializationDocumentSet set = Create(MakeLayout());

            Assert.That(set.GetFinalReadyBytes(), Is.EqualTo(set.GetStagingReadyBytes()));
        }

        [Test]
        public void ByteCounts_MatchLengths()
        {
            CaptureRunInitializationDocumentSet set = Create(MakeLayout());

            Assert.That(set.StagingInitializationByteCount, Is.EqualTo(set.GetStagingInitializationBytes().Length));
            Assert.That(set.FinalInitializationByteCount, Is.EqualTo(set.GetFinalInitializationBytes().Length));
            Assert.That(set.ReadyByteCount, Is.EqualTo(set.GetStagingReadyBytes().Length));
            Assert.That(set.ReadyByteCount, Is.EqualTo(set.GetFinalReadyBytes().Length));
        }

        [Test]
        public void Bytes_NonEmptyWithinLimit()
        {
            CaptureRunInitializationDocumentSet set = Create(MakeLayout());

            Assert.That(set.GetStagingInitializationBytes().Length, Is.GreaterThan(0));
            Assert.That(set.GetFinalInitializationBytes().Length, Is.GreaterThan(0));
            Assert.That(set.GetStagingReadyBytes().Length, Is.GreaterThan(0));

            Assert.That(set.GetStagingInitializationBytes().Length, Is.LessThanOrEqualTo(MaxByteCount));
            Assert.That(set.GetFinalInitializationBytes().Length, Is.LessThanOrEqualTo(MaxByteCount));
            Assert.That(set.GetStagingReadyBytes().Length, Is.LessThanOrEqualTo(MaxByteCount));
        }

        // ---- Defensive copies ----

        [Test]
        public void Getters_ReturnDistinctCopies()
        {
            CaptureRunInitializationDocumentSet set = Create(MakeLayout());

            Assert.That(set.GetStagingInitializationBytes(), Is.Not.SameAs(set.GetStagingInitializationBytes()));
            Assert.That(set.GetFinalInitializationBytes(), Is.Not.SameAs(set.GetFinalInitializationBytes()));
            Assert.That(set.GetStagingReadyBytes(), Is.Not.SameAs(set.GetStagingReadyBytes()));
            Assert.That(set.GetFinalReadyBytes(), Is.Not.SameAs(set.GetFinalReadyBytes()));
        }

        [Test]
        public void ReadyGetters_ReturnDistinctInstances()
        {
            CaptureRunInitializationDocumentSet set = Create(MakeLayout());

            Assert.That(set.GetFinalReadyBytes(), Is.Not.SameAs(set.GetStagingReadyBytes()));
        }

        [Test]
        public void MutatingReturnedCopy_DoesNotAffectNext()
        {
            CaptureRunInitializationDocumentSet set = Create(MakeLayout());

            byte[] expected = CaptureRunInitializationMarkerCodec.SerializeCanonical(set.MarkerBinding.StagingInitialization);

            byte[] copy = set.GetStagingInitializationBytes();
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = 0;
            }

            Assert.That(set.GetStagingInitializationBytes(), Is.EqualTo(expected));
        }

        [Test]
        public void StagingInitMutation_DoesNotAffectFinalOrReady()
        {
            CaptureRunInitializationDocumentSet set = Create(MakeLayout());

            byte[] finalExpected = CaptureRunInitializationMarkerCodec.SerializeCanonical(set.MarkerBinding.FinalInitialization);
            byte[] readyExpected = CaptureRunReadyMarkerCodec.SerializeCanonical(set.MarkerBinding.StagingReady);

            byte[] staging = set.GetStagingInitializationBytes();
            for (int i = 0; i < staging.Length; i++)
            {
                staging[i] = 0xFF;
            }

            Assert.That(set.GetFinalInitializationBytes(), Is.EqualTo(finalExpected));
            Assert.That(set.GetStagingReadyBytes(), Is.EqualTo(readyExpected));
            Assert.That(set.GetFinalReadyBytes(), Is.EqualTo(readyExpected));
        }

        [Test]
        public void ConsecutiveConstructions_DoNotShareInternalArrays()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationDocumentSet first = Create(layout);
            CaptureRunInitializationDocumentSet second = Create(layout);

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(GetFieldBytes(second, "_stagingInitializationBytes"), Is.Not.SameAs(GetFieldBytes(first, "_stagingInitializationBytes")));
            Assert.That(GetFieldBytes(second, "_finalInitializationBytes"), Is.Not.SameAs(GetFieldBytes(first, "_finalInitializationBytes")));
            Assert.That(GetFieldBytes(second, "_readyBytes"), Is.Not.SameAs(GetFieldBytes(first, "_readyBytes")));
        }

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
            Assert.That(result.RunInitializationId, Is.EqualTo(InitId));
        }

        // ---- Shape ----

        [Test]
        public void NoPublicConstructorOrSetter()
        {
            Type type = typeof(CaptureRunInitializationDocumentSet);

            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty, "No public constructor.");
            Assert.That(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Empty, "No public methods.");

            foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(prop.CanWrite, Is.False, prop.Name + " must be get-only.");
            }
        }

        [Test]
        public void Sealed_NotDisposable_NotUnityObject()
        {
            Type type = typeof(CaptureRunInitializationDocumentSet);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void NoStaticMutableStateOrCollection()
        {
            Type type = typeof(CaptureRunInitializationDocumentSet);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.FieldType == typeof(byte[]))
                {
                    continue;
                }

                Assert.That(field.FieldType.IsArray, Is.False, field.Name + " must not be an array.");

                bool isCollection = typeof(IEnumerable).IsAssignableFrom(field.FieldType) && field.FieldType != typeof(string);
                Assert.That(isCollection, Is.False, field.Name + " must not be a collection.");
            }
        }

        // ---- Source inspection ----

        [Test]
        public void Source_OnlyDesignatedCodecs_NoHashFactoryGeneratorMarkerCtor()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationDocumentSet.cs"));

            Assert.That(source, Does.Contain("CaptureRunInitializationMarkerCodec"));
            Assert.That(source, Does.Contain("CaptureRunReadyMarkerCodec"));
            Assert.That(source, Does.Contain("SerializeCanonical"));

            Assert.That(source, Does.Not.Contain("CaptureFramePngArtifactCodec"));
            Assert.That(source, Does.Not.Contain("PngJsonCapturePublicationPlanCodec"));
            Assert.That(source, Does.Not.Contain("TraceRunManifestCodec"));
            Assert.That(source, Does.Not.Contain("TraceBinaryCodec"));
            Assert.That(source, Does.Not.Contain("DeserializeCanonical"));
            Assert.That(source, Does.Not.Contain("ComputeContentSha256"));
            Assert.That(source, Does.Not.Contain("SHA256"));
            Assert.That(source, Does.Not.Contain("SHA-256"));
            Assert.That(source, Does.Not.Contain("System.Security.Cryptography"));
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationIdGenerator"));
            Assert.That(source, Does.Not.Contain("new CaptureRunInitializationMarker"));
            Assert.That(source, Does.Not.Contain("new CaptureRunReadyMarker"));
        }

        [Test]
        public void Source_NoFilesystemPInvokeUnityRandomClock()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationDocumentSet.cs"));

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

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationDocumentSetTests).Assembly.Location);
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
