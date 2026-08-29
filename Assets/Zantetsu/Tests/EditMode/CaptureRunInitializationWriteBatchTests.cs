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
    public class CaptureRunInitializationWriteBatchTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static string StagingBaseRoot() => IsWindows ? "C:\\staging" : "/staging";

        private static string FinalBaseRoot() => IsWindows ? "D:\\final" : "/final";

        private static CaptureRunRootLayout MakeLayout(long testRunId = 1)
        {
            return new CaptureRunRootLayout(StagingBaseRoot(), FinalBaseRoot(), testRunId);
        }

        private static CaptureRunInitializationDocumentSet MakeDocuments()
        {
            return CaptureRunInitializationDocumentSetFactory.Create(MakeLayout(), InitId);
        }

        private static CaptureRunInitializationWriteBatch MakeBatch()
        {
            return new CaptureRunInitializationWriteBatch(MakeDocuments());
        }

        private static byte[] FieldBytes(object target, string fieldName)
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
        public void NullDocuments_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationWriteBatch(null));

            Assert.That(ex.ParamName, Is.EqualTo("documents"));
        }

        [Test]
        public void ValidDocuments_Constructs()
        {
            CaptureRunInitializationWriteBatch batch = MakeBatch();

            Assert.That(batch, Is.Not.Null);
        }

        [Test]
        public void Documents_HeldByReference()
        {
            CaptureRunInitializationDocumentSet documents = MakeDocuments();
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);

            Assert.That(batch.Documents, Is.SameAs(documents));
        }

        [Test]
        public void Count_IsFour()
        {
            Assert.That(MakeBatch().Count, Is.EqualTo(4));
        }

        [Test]
        public void FourSpecificProperties_NotNull()
        {
            CaptureRunInitializationWriteBatch batch = MakeBatch();

            Assert.That(batch.StagingInitialization, Is.Not.Null);
            Assert.That(batch.FinalInitialization, Is.Not.Null);
            Assert.That(batch.StagingReady, Is.Not.Null);
            Assert.That(batch.FinalReady, Is.Not.Null);
        }

        [Test]
        public void GetOperation_MatchesSpecificProperties()
        {
            CaptureRunInitializationWriteBatch batch = MakeBatch();

            Assert.That(batch.GetOperation(0), Is.SameAs(batch.StagingInitialization));
            Assert.That(batch.GetOperation(1), Is.SameAs(batch.FinalInitialization));
            Assert.That(batch.GetOperation(2), Is.SameAs(batch.StagingReady));
            Assert.That(batch.GetOperation(3), Is.SameAs(batch.FinalReady));
        }

        [Test]
        public void GetOperation_OutOfRange_RejectedWithParamName()
        {
            CaptureRunInitializationWriteBatch batch = MakeBatch();

            foreach (int index in new[] { -1, 4, int.MinValue, int.MaxValue })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => batch.GetOperation(index));

                Assert.That(ex.ParamName, Is.EqualTo("index"));
            }
        }

        // ---- Operation contents ----

        [Test]
        public void OperationRolesAndKinds_Exact()
        {
            CaptureRunInitializationWriteBatch batch = MakeBatch();

            Assert.That(batch.StagingInitialization.RootRole, Is.EqualTo(CaptureRunRootRole.Staging));
            Assert.That(batch.StagingInitialization.MarkerKind, Is.EqualTo(CaptureRunMarkerKind.Initialization));
            Assert.That(batch.FinalInitialization.RootRole, Is.EqualTo(CaptureRunRootRole.Final));
            Assert.That(batch.FinalInitialization.MarkerKind, Is.EqualTo(CaptureRunMarkerKind.Initialization));
            Assert.That(batch.StagingReady.RootRole, Is.EqualTo(CaptureRunRootRole.Staging));
            Assert.That(batch.StagingReady.MarkerKind, Is.EqualTo(CaptureRunMarkerKind.Ready));
            Assert.That(batch.FinalReady.RootRole, Is.EqualTo(CaptureRunRootRole.Final));
            Assert.That(batch.FinalReady.MarkerKind, Is.EqualTo(CaptureRunMarkerKind.Ready));
        }

        [Test]
        public void OperationPaths_MatchMarkerPathSet()
        {
            CaptureRunInitializationDocumentSet documents = MakeDocuments();
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);

            CaptureRunMarkerPathSet paths = documents.Plan.MarkerPaths;

            Assert.That(batch.StagingInitialization.TemporaryPath, Is.EqualTo(paths.StagingInitializationTemporaryPath));
            Assert.That(batch.StagingInitialization.FinalPath, Is.EqualTo(paths.StagingInitializationPath));
            Assert.That(batch.FinalInitialization.TemporaryPath, Is.EqualTo(paths.FinalInitializationTemporaryPath));
            Assert.That(batch.FinalInitialization.FinalPath, Is.EqualTo(paths.FinalInitializationPath));
            Assert.That(batch.StagingReady.TemporaryPath, Is.EqualTo(paths.StagingReadyTemporaryPath));
            Assert.That(batch.StagingReady.FinalPath, Is.EqualTo(paths.StagingReadyPath));
            Assert.That(batch.FinalReady.TemporaryPath, Is.EqualTo(paths.FinalReadyTemporaryPath));
            Assert.That(batch.FinalReady.FinalPath, Is.EqualTo(paths.FinalReadyPath));
        }

        [Test]
        public void OperationBytes_MatchDocumentSetGetters()
        {
            CaptureRunInitializationDocumentSet documents = MakeDocuments();
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);

            Assert.That(batch.StagingInitialization.GetCanonicalBytes(), Is.EqualTo(documents.GetStagingInitializationBytes()));
            Assert.That(batch.FinalInitialization.GetCanonicalBytes(), Is.EqualTo(documents.GetFinalInitializationBytes()));
            Assert.That(batch.StagingReady.GetCanonicalBytes(), Is.EqualTo(documents.GetStagingReadyBytes()));
            Assert.That(batch.FinalReady.GetCanonicalBytes(), Is.EqualTo(documents.GetFinalReadyBytes()));
        }

        [Test]
        public void ByteCounts_Exact()
        {
            CaptureRunInitializationDocumentSet documents = MakeDocuments();
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);

            Assert.That(batch.StagingInitialization.ByteCount, Is.EqualTo(documents.StagingInitializationByteCount));
            Assert.That(batch.FinalInitialization.ByteCount, Is.EqualTo(documents.FinalInitializationByteCount));
            Assert.That(batch.StagingReady.ByteCount, Is.EqualTo(documents.ReadyByteCount));
            Assert.That(batch.FinalReady.ByteCount, Is.EqualTo(documents.ReadyByteCount));
        }

        // ---- Buffer ownership ----

        [Test]
        public void ReadyOperations_DoNotShareInternalArrays()
        {
            CaptureRunInitializationWriteBatch batch = MakeBatch();

            byte[] stagingReadyInternal = FieldBytes(batch.StagingReady, "_canonicalBytes");
            byte[] finalReadyInternal = FieldBytes(batch.FinalReady, "_canonicalBytes");

            Assert.That(finalReadyInternal, Is.Not.SameAs(stagingReadyInternal));
            Assert.That(finalReadyInternal, Is.EqualTo(stagingReadyInternal));
        }

        [Test]
        public void InitOperations_DoNotShareInternalArrays()
        {
            CaptureRunInitializationWriteBatch batch = MakeBatch();

            byte[] stagingInitInternal = FieldBytes(batch.StagingInitialization, "_canonicalBytes");
            byte[] finalInitInternal = FieldBytes(batch.FinalInitialization, "_canonicalBytes");

            Assert.That(finalInitInternal, Is.Not.SameAs(stagingInitInternal));
        }

        [Test]
        public void OperationArrays_DistinctFromDocumentSetArrays()
        {
            CaptureRunInitializationDocumentSet documents = MakeDocuments();
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);

            byte[] docStaging = FieldBytes(documents, "_stagingInitializationBytes");
            byte[] docFinal = FieldBytes(documents, "_finalInitializationBytes");
            byte[] docReady = FieldBytes(documents, "_readyBytes");

            Assert.That(FieldBytes(batch.StagingInitialization, "_canonicalBytes"), Is.Not.SameAs(docStaging));
            Assert.That(FieldBytes(batch.FinalInitialization, "_canonicalBytes"), Is.Not.SameAs(docFinal));
            Assert.That(FieldBytes(batch.StagingReady, "_canonicalBytes"), Is.Not.SameAs(docReady));
            Assert.That(FieldBytes(batch.FinalReady, "_canonicalBytes"), Is.Not.SameAs(docReady));
        }

        [Test]
        public void OperationGetterMutation_DoesNotAffectBatchOrDocuments()
        {
            CaptureRunInitializationDocumentSet documents = MakeDocuments();
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);

            byte[] expected = documents.GetStagingInitializationBytes();

            byte[] copy = batch.StagingInitialization.GetCanonicalBytes();
            for (int i = 0; i < copy.Length; i++)
            {
                copy[i] = 0;
            }

            Assert.That(batch.StagingInitialization.GetCanonicalBytes(), Is.EqualTo(expected));
            Assert.That(documents.GetStagingInitializationBytes(), Is.EqualTo(expected));
        }

        [Test]
        public void Inputs_NotMutated()
        {
            CaptureRunInitializationDocumentSet documents = MakeDocuments();
            CaptureRunInitializationPlan plan = documents.Plan;
            CaptureRunMarkerPathSet paths = plan.MarkerPaths;
            CaptureRunMarkerBinding binding = plan.MarkerBinding;

            string stagingInitPathBefore = paths.StagingInitializationPath;
            string initIdBefore = binding.RunInitializationId;

            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);

            Assert.That(documents.Plan, Is.SameAs(plan));
            Assert.That(plan.MarkerPaths, Is.SameAs(paths));
            Assert.That(plan.MarkerBinding, Is.SameAs(binding));
            Assert.That(paths.StagingInitializationPath, Is.EqualTo(stagingInitPathBefore));
            Assert.That(binding.RunInitializationId, Is.EqualTo(initIdBefore));
        }

        [Test]
        public void ConsecutiveBatches_ShareNothing()
        {
            CaptureRunInitializationDocumentSet documents = MakeDocuments();
            CaptureRunInitializationWriteBatch first = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationWriteBatch second = new CaptureRunInitializationWriteBatch(documents);

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.StagingInitialization, Is.Not.SameAs(first.StagingInitialization));
            Assert.That(second.FinalInitialization, Is.Not.SameAs(first.FinalInitialization));
            Assert.That(second.StagingReady, Is.Not.SameAs(first.StagingReady));
            Assert.That(second.FinalReady, Is.Not.SameAs(first.FinalReady));

            Assert.That(FieldBytes(second.StagingInitialization, "_canonicalBytes"), Is.Not.SameAs(FieldBytes(first.StagingInitialization, "_canonicalBytes")));
            Assert.That(FieldBytes(second.FinalInitialization, "_canonicalBytes"), Is.Not.SameAs(FieldBytes(first.FinalInitialization, "_canonicalBytes")));
            Assert.That(FieldBytes(second.StagingReady, "_canonicalBytes"), Is.Not.SameAs(FieldBytes(first.StagingReady, "_canonicalBytes")));
            Assert.That(FieldBytes(second.FinalReady, "_canonicalBytes"), Is.Not.SameAs(FieldBytes(first.FinalReady, "_canonicalBytes")));
        }

        [Test]
        public void OwnershipTransferFailure_FailsClosed()
        {
            MethodInfo helper = typeof(CaptureRunInitializationWriteBatch).GetMethod(
                "RequireOwnershipTransfer", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(helper, Is.Not.Null, "RequireOwnershipTransfer helper not found.");

            byte[] buffer = new byte[8];

            try
            {
                helper.Invoke(null, new object[] { buffer, "staging initialization" });
                Assert.Fail("Expected the ownership postcondition to fail closed.");
            }
            catch (TargetInvocationException ex)
            {
                Assert.That(ex.InnerException, Is.InstanceOf<InvalidOperationException>());
                Assert.That(ex.InnerException.Message, Does.Contain("did not take ownership"));
            }
        }

        [Test]
        public void OwnershipTransfer_NullBuffer_DoesNotThrow()
        {
            MethodInfo helper = typeof(CaptureRunInitializationWriteBatch).GetMethod(
                "RequireOwnershipTransfer", BindingFlags.NonPublic | BindingFlags.Static);

            Assert.That(helper, Is.Not.Null, "RequireOwnershipTransfer helper not found.");

            byte[] nullBuffer = null;
            Assert.DoesNotThrow(() => helper.Invoke(null, new object[] { nullBuffer, "staging initialization" }));
        }

        // ---- Fail-closed (reflection for unreachable branches) ----

        [Test]
        public void MissingPlan_RejectedAsDocuments()
        {
            CaptureRunInitializationDocumentSet documents = (CaptureRunInitializationDocumentSet)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationDocumentSet));

            ArgumentException ex = Assert.Throws<ArgumentException>(() => new CaptureRunInitializationWriteBatch(documents));
            Assert.That(ex.ParamName, Is.EqualTo("documents"));
        }

        [Test]
        public void MissingMarkerPaths_RejectedAsDocuments()
        {
            CaptureRunInitializationDocumentSet documents = (CaptureRunInitializationDocumentSet)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationDocumentSet));
            CaptureRunInitializationPlan plan = (CaptureRunInitializationPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationPlan));
            SetField(documents, "_plan", plan);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => new CaptureRunInitializationWriteBatch(documents));
            Assert.That(ex.ParamName, Is.EqualTo("documents"));
        }

        [Test]
        public void InvalidMarkerPaths_OperationExceptionLeavesDocumentsUnchanged()
        {
            CaptureRunInitializationDocumentSet documents = MakeDocuments();

            byte[] stagingBefore = FieldBytes(documents, "_stagingInitializationBytes");
            byte[] finalBefore = FieldBytes(documents, "_finalInitializationBytes");
            byte[] readyBefore = FieldBytes(documents, "_readyBytes");

            CaptureRunMarkerPathSet badPaths = (CaptureRunMarkerPathSet)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerPathSet));
            SetField(badPaths, "_stagingInitializationTemporaryPath", "bad.tmp");
            SetField(badPaths, "_stagingInitializationPath", "bad");

            SetField(documents.Plan, "_markerPaths", badPaths);

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationWriteBatch(documents));

            Assert.That(FieldBytes(documents, "_stagingInitializationBytes"), Is.SameAs(stagingBefore));
            Assert.That(FieldBytes(documents, "_finalInitializationBytes"), Is.SameAs(finalBefore));
            Assert.That(FieldBytes(documents, "_readyBytes"), Is.SameAs(readyBefore));
        }

        // ---- Shape ----

        [Test]
        public void NoPublicConstructorOrSetter()
        {
            Type type = typeof(CaptureRunInitializationWriteBatch);

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
            Type type = typeof(CaptureRunInitializationWriteBatch);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void Fields_AreExactlyFiveReadonlyReferences()
        {
            Type type = typeof(CaptureRunInitializationWriteBatch);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(5), "Must hold the documents and four operations.");

            int documentFields = 0;
            int operationFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(CaptureRunInitializationDocumentSet))
                {
                    documentFields++;
                }
                else if (field.FieldType == typeof(CaptureRunMarkerWriteOperation))
                {
                    operationFields++;
                }
                else
                {
                    Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
                }
            }

            Assert.That(documentFields, Is.EqualTo(1));
            Assert.That(operationFields, Is.EqualTo(4));
        }

        [Test]
        public void NoArrayCollectionOrMutableStaticState()
        {
            Type type = typeof(CaptureRunInitializationWriteBatch);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsArray, Is.False, field.Name + " must not be an array.");

                bool isCollection = typeof(IEnumerable).IsAssignableFrom(field.FieldType) && field.FieldType != typeof(string);
                Assert.That(isCollection, Is.False, field.Name + " must not be a collection.");
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
            }
        }

        // ---- Source inspection ----

        [Test]
        public void Source_OnlyDocumentGettersAndOperationConstructor()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationWriteBatch.cs"));

            Assert.That(source, Does.Contain("GetStagingInitializationBytes"));
            Assert.That(source, Does.Contain("GetFinalInitializationBytes"));
            Assert.That(source, Does.Contain("GetStagingReadyBytes"));
            Assert.That(source, Does.Contain("GetFinalReadyBytes"));
            Assert.That(source, Does.Contain("new CaptureRunMarkerWriteOperation"));

            Assert.That(source, Does.Not.Contain("new byte"));
            Assert.That(source, Does.Not.Contain("Array.Copy"));
            Assert.That(source, Does.Not.Contain("Buffer.BlockCopy"));
            Assert.That(source, Does.Not.Contain("Clone()"));
            Assert.That(source, Does.Not.Contain("ToArray"));
            Assert.That(source, Does.Not.Contain("Path."));
            Assert.That(source, Does.Not.Contain("Path.Combine"));
            Assert.That(source, Does.Not.Contain("GetFullPath"));
        }

        [Test]
        public void Source_NoCodecHashGeneratorFactoryFilesystemUnityClockRandom()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunInitializationWriteBatch.cs"));

            Assert.That(source, Does.Not.Contain("CaptureRunInitializationMarkerCodec"));
            Assert.That(source, Does.Not.Contain("CaptureRunReadyMarkerCodec"));
            Assert.That(source, Does.Not.Contain("SerializeCanonical"));
            Assert.That(source, Does.Not.Contain("DeserializeCanonical"));
            Assert.That(source, Does.Not.Contain("ComputeContentSha256"));
            Assert.That(source, Does.Not.Contain("SHA256"));
            Assert.That(source, Does.Not.Contain("SHA-256"));
            Assert.That(source, Does.Not.Contain("System.Security.Cryptography"));
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationIdGenerator"));
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationPlanFactory"));
            Assert.That(source, Does.Not.Contain("CaptureRunInitializationDocumentSetFactory"));
            Assert.That(source, Does.Not.Contain("CaptureRunMarkerBindingFactory"));
            Assert.That(source, Does.Not.Contain("new CaptureRunInitializationMarker"));
            Assert.That(source, Does.Not.Contain("new CaptureRunReadyMarker"));
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

            string dir = Path.GetDirectoryName(typeof(CaptureRunInitializationWriteBatchTests).Assembly.Location);
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
