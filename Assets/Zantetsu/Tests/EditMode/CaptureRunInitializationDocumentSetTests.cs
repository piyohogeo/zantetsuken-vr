using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
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

        private static string StagingBaseRoot() => IsWindows ? "C:\\staging" : "/staging";

        private static string FinalBaseRoot() => IsWindows ? "D:\\final" : "/final";

        private static CaptureRunInitializationPlan MakePlan(long testRunId = 1)
        {
            CaptureRunRootLayout layout = new CaptureRunRootLayout(StagingBaseRoot(), FinalBaseRoot(), testRunId);
            return CaptureRunInitializationPlanFactory.Create(layout, InitId);
        }

        private static byte[] GetFieldBytes(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            return (byte[])field.GetValue(target);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        // ---- Construction ----

        [Test]
        public void NullPlan_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationDocumentSet(null));

            Assert.That(ex.ParamName, Is.EqualTo("plan"));
        }

        [Test]
        public void ValidPlan_Constructs()
        {
            CaptureRunInitializationDocumentSet set = new CaptureRunInitializationDocumentSet(MakePlan());

            Assert.That(set, Is.Not.Null);
        }

        [Test]
        public void Plan_HeldByReference()
        {
            CaptureRunInitializationPlan plan = MakePlan();
            CaptureRunInitializationDocumentSet set = new CaptureRunInitializationDocumentSet(plan);

            Assert.That(set.Plan, Is.SameAs(plan));
        }

        [Test]
        public void ReadyBytesMismatch_FailsClosed()
        {
            CaptureRunInitializationPlan plan = MakePlan();
            CaptureRunMarkerBinding goodBinding = plan.MarkerBinding;

            CaptureRunMarkerBinding badBinding = (CaptureRunMarkerBinding)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerBinding));
            SetField(badBinding, "_stagingInitialization", goodBinding.StagingInitialization);
            SetField(badBinding, "_finalInitialization", goodBinding.FinalInitialization);
            SetField(badBinding, "_stagingReady", goodBinding.StagingReady);
            SetField(badBinding, "_finalReady", new CaptureRunReadyMarker(
                goodBinding.FinalReady.TestRunId,
                goodBinding.FinalReady.RunInitializationId,
                "0000000000000000000000000000000000000000000000000000000000000000",
                goodBinding.FinalReady.FinalInitSha256));

            CaptureRunInitializationPlan badPlan = (CaptureRunInitializationPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationPlan));
            SetField(badPlan, "_markerBinding", badBinding);

            Assert.Throws<InvalidOperationException>(() => new CaptureRunInitializationDocumentSet(badPlan));
        }

        // ---- Canonical bytes ----

        [Test]
        public void StagingInitBytes_MatchCodec()
        {
            CaptureRunInitializationPlan plan = MakePlan();
            CaptureRunInitializationDocumentSet set = new CaptureRunInitializationDocumentSet(plan);

            byte[] expected = CaptureRunInitializationMarkerCodec.SerializeCanonical(plan.MarkerBinding.StagingInitialization);

            Assert.That(set.GetStagingInitializationBytes(), Is.EqualTo(expected));
        }

        [Test]
        public void FinalInitBytes_MatchCodec()
        {
            CaptureRunInitializationPlan plan = MakePlan();
            CaptureRunInitializationDocumentSet set = new CaptureRunInitializationDocumentSet(plan);

            byte[] expected = CaptureRunInitializationMarkerCodec.SerializeCanonical(plan.MarkerBinding.FinalInitialization);

            Assert.That(set.GetFinalInitializationBytes(), Is.EqualTo(expected));
        }

        [Test]
        public void ReadyBytes_MatchCodec()
        {
            CaptureRunInitializationPlan plan = MakePlan();
            CaptureRunInitializationDocumentSet set = new CaptureRunInitializationDocumentSet(plan);

            byte[] expected = CaptureRunReadyMarkerCodec.SerializeCanonical(plan.MarkerBinding.StagingReady);

            Assert.That(set.GetStagingReadyBytes(), Is.EqualTo(expected));
            Assert.That(set.GetFinalReadyBytes(), Is.EqualTo(expected));
        }

        [Test]
        public void ReadyBytes_ValueEqual()
        {
            CaptureRunInitializationDocumentSet set = new CaptureRunInitializationDocumentSet(MakePlan());

            Assert.That(set.GetFinalReadyBytes(), Is.EqualTo(set.GetStagingReadyBytes()));
        }

        [Test]
        public void ByteCounts_MatchLengths()
        {
            CaptureRunInitializationDocumentSet set = new CaptureRunInitializationDocumentSet(MakePlan());

            Assert.That(set.StagingInitializationByteCount, Is.EqualTo(set.GetStagingInitializationBytes().Length));
            Assert.That(set.FinalInitializationByteCount, Is.EqualTo(set.GetFinalInitializationBytes().Length));
            Assert.That(set.ReadyByteCount, Is.EqualTo(set.GetStagingReadyBytes().Length));
            Assert.That(set.ReadyByteCount, Is.EqualTo(set.GetFinalReadyBytes().Length));
        }

        [Test]
        public void Bytes_NonEmptyWithinLimit()
        {
            CaptureRunInitializationDocumentSet set = new CaptureRunInitializationDocumentSet(MakePlan());

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
            CaptureRunInitializationDocumentSet set = new CaptureRunInitializationDocumentSet(MakePlan());

            Assert.That(set.GetStagingInitializationBytes(), Is.Not.SameAs(set.GetStagingInitializationBytes()));
            Assert.That(set.GetFinalInitializationBytes(), Is.Not.SameAs(set.GetFinalInitializationBytes()));
            Assert.That(set.GetStagingReadyBytes(), Is.Not.SameAs(set.GetStagingReadyBytes()));
            Assert.That(set.GetFinalReadyBytes(), Is.Not.SameAs(set.GetFinalReadyBytes()));
        }

        [Test]
        public void ReadyGetters_ReturnDistinctInstances()
        {
            CaptureRunInitializationDocumentSet set = new CaptureRunInitializationDocumentSet(MakePlan());

            Assert.That(set.GetFinalReadyBytes(), Is.Not.SameAs(set.GetStagingReadyBytes()));
        }

        [Test]
        public void MutatingReturnedCopy_DoesNotAffectNext()
        {
            CaptureRunInitializationPlan plan = MakePlan();
            CaptureRunInitializationDocumentSet set = new CaptureRunInitializationDocumentSet(plan);

            byte[] expected = CaptureRunInitializationMarkerCodec.SerializeCanonical(plan.MarkerBinding.StagingInitialization);

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
            CaptureRunInitializationPlan plan = MakePlan();
            CaptureRunInitializationDocumentSet set = new CaptureRunInitializationDocumentSet(plan);

            byte[] finalExpected = CaptureRunInitializationMarkerCodec.SerializeCanonical(plan.MarkerBinding.FinalInitialization);
            byte[] readyExpected = CaptureRunReadyMarkerCodec.SerializeCanonical(plan.MarkerBinding.StagingReady);

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
            CaptureRunInitializationPlan plan = MakePlan();
            CaptureRunInitializationDocumentSet first = new CaptureRunInitializationDocumentSet(plan);
            CaptureRunInitializationDocumentSet second = new CaptureRunInitializationDocumentSet(plan);

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(GetFieldBytes(second, "_stagingInitializationBytes"), Is.Not.SameAs(GetFieldBytes(first, "_stagingInitializationBytes")));
            Assert.That(GetFieldBytes(second, "_finalInitializationBytes"), Is.Not.SameAs(GetFieldBytes(first, "_finalInitializationBytes")));
            Assert.That(GetFieldBytes(second, "_readyBytes"), Is.Not.SameAs(GetFieldBytes(first, "_readyBytes")));
        }

        [Test]
        public void Inputs_NotMutated()
        {
            CaptureRunInitializationPlan plan = MakePlan();
            CaptureRunMarkerBinding binding = plan.MarkerBinding;

            CaptureRunInitializationMarker stagingInit = binding.StagingInitialization;
            CaptureRunInitializationMarker finalInit = binding.FinalInitialization;
            CaptureRunReadyMarker stagingReady = binding.StagingReady;
            CaptureRunReadyMarker finalReady = binding.FinalReady;

            string initIdBefore = stagingInit.RunInitializationId;
            string stagingHashBefore = stagingInit.StagingRunRootSha256;
            string finalHashBefore = finalInit.FinalRunRootSha256;
            string readyStagingHashBefore = stagingReady.StagingInitSha256;

            CaptureRunInitializationDocumentSet set = new CaptureRunInitializationDocumentSet(plan);

            Assert.That(plan.MarkerBinding, Is.SameAs(binding));
            Assert.That(binding.StagingInitialization, Is.SameAs(stagingInit));
            Assert.That(binding.FinalInitialization, Is.SameAs(finalInit));
            Assert.That(binding.StagingReady, Is.SameAs(stagingReady));
            Assert.That(binding.FinalReady, Is.SameAs(finalReady));

            Assert.That(stagingInit.RunInitializationId, Is.EqualTo(initIdBefore));
            Assert.That(stagingInit.StagingRunRootSha256, Is.EqualTo(stagingHashBefore));
            Assert.That(finalInit.FinalRunRootSha256, Is.EqualTo(finalHashBefore));
            Assert.That(stagingReady.StagingInitSha256, Is.EqualTo(readyStagingHashBefore));
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
        public void Fields_ArePlanAndThreeReadonlyByteArrays()
        {
            Type type = typeof(CaptureRunInitializationDocumentSet);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(4), "Must hold the plan and three byte arrays.");

            int planFields = 0;
            int byteArrayFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(CaptureRunInitializationPlan))
                {
                    planFields++;
                }
                else if (field.FieldType == typeof(byte[]))
                {
                    byteArrayFields++;
                }
                else
                {
                    Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
                }
            }

            Assert.That(planFields, Is.EqualTo(1));
            Assert.That(byteArrayFields, Is.EqualTo(3));
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
            Assert.That(source, Does.Not.Contain("CapturePublicationPlanCodec"));
            Assert.That(source, Does.Not.Contain("TraceRunManifestCodec"));
            Assert.That(source, Does.Not.Contain("TraceBinaryCodec"));
            Assert.That(source, Does.Not.Contain("DeserializeCanonical"));
            Assert.That(source, Does.Not.Contain("ComputeContentSha256"));
            Assert.That(source, Does.Not.Contain("SHA256"));
            Assert.That(source, Does.Not.Contain("SHA-256"));
            Assert.That(source, Does.Not.Contain("System.Security.Cryptography"));
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationIdGenerator"));
            Assert.That(source, Does.Not.Contain("CaptureRunMarkerBindingFactory"));
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationPlanFactory"));
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
