using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunInitializationExecutionCoordinatorTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static string StagingBase() => IsWindows ? "C:\\staging" : "/staging";

        private static string FinalBase() => IsWindows ? "D:\\final" : "/final";

        private static CaptureRunInitializationWriteBatch MakeBatch(long testRunId = 1)
        {
            CaptureRunRootLayout layout = new CaptureRunRootLayout(StagingBase(), FinalBase(), testRunId);
            CaptureRunInitializationDocumentSet documents = CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            return new CaptureRunInitializationWriteBatch(documents);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            private readonly List<string> _log;
            private readonly Dictionary<int, Exception> _exceptions = new Dictionary<int, Exception>();
            private int _callCount;

            public Func<CaptureRunRootProvisionOperation, CaptureRunRootProvisionReceipt> ReceiptFactory;

            public FakeProvisioner(List<string> log)
            {
                _log = log;
            }

            public int CallCount => _callCount;

            public void ThrowOnCall(int callNumber, Exception exception)
            {
                _exceptions[callNumber] = exception;
            }

            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
            {
                _callCount++;
                _log.Add("Provision:" + operation.RootRole);

                if (_exceptions.TryGetValue(_callCount, out Exception exception))
                {
                    throw exception;
                }

                if (ReceiptFactory != null)
                {
                    return ReceiptFactory(operation);
                }

                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeWriter : ICaptureRunMarkerAtomicWriter
        {
            private readonly List<string> _log;
            private readonly Dictionary<int, Exception> _exceptions = new Dictionary<int, Exception>();
            private int _callCount;

            public Func<CaptureRunMarkerWriteOperation, CaptureRunMarkerWriteReceipt> ReceiptFactory;

            public FakeWriter(List<string> log)
            {
                _log = log;
            }

            public int CallCount => _callCount;

            public void ThrowOnCall(int callNumber, Exception exception)
            {
                _exceptions[callNumber] = exception;
            }

            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                _callCount++;
                _log.Add("Write:" + operation.RootRole + ":" + operation.MarkerKind);

                if (_exceptions.TryGetValue(_callCount, out Exception exception))
                {
                    throw exception;
                }

                if (ReceiptFactory != null)
                {
                    return ReceiptFactory(operation);
                }

                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }

        private static CaptureRunInitializationExecutionCoordinator MakeCoordinator(
            FakeProvisioner provisioner,
            FakeWriter writer)
        {
            return new CaptureRunInitializationExecutionCoordinator(provisioner, writer);
        }

        private static CaptureRunInitializationExecutionReceipt ExecuteValid(out CaptureRunInitializationWriteBatch batch)
        {
            batch = MakeBatch();
            List<string> log = new List<string>();
            return MakeCoordinator(new FakeProvisioner(log), new FakeWriter(log)).Execute(batch);
        }

        // ---- Construction / shape ----

        [Test]
        public void Coordinator_NullProvisioner_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationExecutionCoordinator(null, new FakeWriter(new List<string>())));

            Assert.That(ex.ParamName, Is.EqualTo("rootProvisioner"));
        }

        [Test]
        public void Coordinator_NullWriter_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunInitializationExecutionCoordinator(new FakeProvisioner(new List<string>()), null));

            Assert.That(ex.ParamName, Is.EqualTo("markerWriter"));
        }

        [Test]
        public void Coordinator_Fields_AreTwoReadonlyDependencies()
        {
            Type type = typeof(CaptureRunInitializationExecutionCoordinator);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(2));

            int provisionerFields = 0;
            int writerFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(ICaptureRunRootProvisioner))
                {
                    provisionerFields++;
                }
                else if (field.FieldType == typeof(ICaptureRunMarkerAtomicWriter))
                {
                    writerFields++;
                }
                else
                {
                    Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
                }
            }

            Assert.That(provisionerFields, Is.EqualTo(1));
            Assert.That(writerFields, Is.EqualTo(1));
        }

        [Test]
        public void ExecutionReceipt_Fields_AreSevenReadonlyReferences()
        {
            Type type = typeof(CaptureRunInitializationExecutionReceipt);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(7));

            int batchFields = 0;
            int provisionReceiptFields = 0;
            int writeReceiptFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(CaptureRunInitializationWriteBatch))
                {
                    batchFields++;
                }
                else if (field.FieldType == typeof(CaptureRunRootProvisionReceipt))
                {
                    provisionReceiptFields++;
                }
                else if (field.FieldType == typeof(CaptureRunMarkerWriteReceipt))
                {
                    writeReceiptFields++;
                }
                else
                {
                    Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
                }
            }

            Assert.That(batchFields, Is.EqualTo(1));
            Assert.That(provisionReceiptFields, Is.EqualTo(2));
            Assert.That(writeReceiptFields, Is.EqualTo(4));
        }

        [Test]
        public void NoPublicConstructorOrSetter_Sealed_NotDisposable_NotUnityObject()
        {
            foreach (Type type in new[] { typeof(CaptureRunInitializationExecutionCoordinator), typeof(CaptureRunInitializationExecutionReceipt) })
            {
                Assert.That(type.IsPublic, Is.False, type.Name);
                Assert.That(type.IsSealed, Is.True, type.Name);
                Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty, type.Name);
                Assert.That(type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Empty, type.Name);
                Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False, type.Name);
                Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False, type.Name);
                Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False, type.Name);

                foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(prop.CanWrite, Is.False, prop.Name + " must be get-only.");
                }
            }
        }

        [Test]
        public void NoArrayCollectionOrMutableStaticState()
        {
            foreach (Type type in new[] { typeof(CaptureRunInitializationExecutionCoordinator), typeof(CaptureRunInitializationExecutionReceipt) })
            {
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
        }

        // ---- Normal order ----

        [Test]
        public void Execute_FollowsFixedOrder_EachDependencyOnce()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationWriteBatch batch = MakeBatch();

            CaptureRunInitializationExecutionReceipt result = MakeCoordinator(provisioner, writer).Execute(batch);

            Assert.That(result, Is.Not.Null);
            Assert.That(log, Is.EqualTo(new[]
            {
                "Provision:Staging",
                "Write:Staging:Initialization",
                "Provision:Final",
                "Write:Final:Initialization",
                "Write:Staging:Ready",
                "Write:Final:Ready"
            }));
            Assert.That(provisioner.CallCount, Is.EqualTo(2));
            Assert.That(writer.CallCount, Is.EqualTo(4));
        }

        // ---- Receipt validation: staging provision ----

        [Test]
        public void StagingProvision_NullReceipt_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            provisioner.ReceiptFactory = op => null;

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeBatch()));
            Assert.That(log, Is.EqualTo(new[] { "Provision:Staging" }));
        }

        [Test]
        public void StagingProvision_UninitializedReceipt_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            provisioner.ReceiptFactory = op => (CaptureRunRootProvisionReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootProvisionReceipt));

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeBatch()));
            Assert.That(log, Is.EqualTo(new[] { "Provision:Staging" }));
        }

        [Test]
        public void StagingProvision_ForeignIssuer_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            FakeProvisioner foreign = new FakeProvisioner(new List<string>());
            provisioner.ReceiptFactory = op => new CaptureRunRootProvisionReceipt(foreign, op);

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeBatch()));
            Assert.That(log, Is.EqualTo(new[] { "Provision:Staging" }));
        }

        [Test]
        public void StagingProvision_DifferentOperation_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationWriteBatch batch = MakeBatch();
            CaptureRunRootLayout layout = batch.Documents.Plan.MarkerPaths.RootLayout;
            CaptureRunRootProvisionOperation finalOperation = new CaptureRunRootProvisionOperation(layout, CaptureRunRootRole.Final);
            provisioner.ReceiptFactory = op => new CaptureRunRootProvisionReceipt(provisioner, finalOperation);

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(batch));
            Assert.That(log, Is.EqualTo(new[] { "Provision:Staging" }));
        }

        // ---- Receipt validation: staging initialization write ----

        [Test]
        public void StagingInitWrite_NullReceipt_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            writer.ReceiptFactory = op => null;

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeBatch()));
            Assert.That(log, Is.EqualTo(new[] { "Provision:Staging", "Write:Staging:Initialization" }));
        }

        [Test]
        public void StagingInitWrite_UninitializedReceipt_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            writer.ReceiptFactory = op => (CaptureRunMarkerWriteReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerWriteReceipt));

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeBatch()));
            Assert.That(log, Is.EqualTo(new[] { "Provision:Staging", "Write:Staging:Initialization" }));
        }

        [Test]
        public void StagingInitWrite_ForeignIssuer_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            FakeWriter foreign = new FakeWriter(new List<string>());
            writer.ReceiptFactory = op => new CaptureRunMarkerWriteReceipt(foreign, op);

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeBatch()));
            Assert.That(log, Is.EqualTo(new[] { "Provision:Staging", "Write:Staging:Initialization" }));
        }

        [Test]
        public void StagingInitWrite_DifferentOperation_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationWriteBatch batch = MakeBatch();
            writer.ReceiptFactory = op => new CaptureRunMarkerWriteReceipt(writer, batch.FinalInitialization);

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(batch));
            Assert.That(log, Is.EqualTo(new[] { "Provision:Staging", "Write:Staging:Initialization" }));
        }

        // ---- Receipt validation: final provision and ready writes ----

        [Test]
        public void FinalProvision_NullReceipt_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            int call = 0;
            provisioner.ReceiptFactory = op => ++call == 2 ? null : new CaptureRunRootProvisionReceipt(provisioner, op);

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeBatch()));
            Assert.That(log, Is.EqualTo(new[] { "Provision:Staging", "Write:Staging:Initialization", "Provision:Final" }));
        }

        [Test]
        public void StagingReadyWrite_NullReceipt_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            int call = 0;
            writer.ReceiptFactory = op => ++call == 3 ? null : new CaptureRunMarkerWriteReceipt(writer, op);

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeBatch()));
            Assert.That(log, Is.EqualTo(new[]
            {
                "Provision:Staging",
                "Write:Staging:Initialization",
                "Provision:Final",
                "Write:Final:Initialization",
                "Write:Staging:Ready"
            }));
        }

        [Test]
        public void FinalReadyWrite_NullReceipt_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            int call = 0;
            writer.ReceiptFactory = op => ++call == 4 ? null : new CaptureRunMarkerWriteReceipt(writer, op);

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeBatch()));
            Assert.That(log, Is.EqualTo(new[]
            {
                "Provision:Staging",
                "Write:Staging:Initialization",
                "Provision:Final",
                "Write:Final:Initialization",
                "Write:Staging:Ready",
                "Write:Final:Ready"
            }));
        }

        // ---- Exceptions ----

        [Test]
        public void Exception_ProvisionStaging_Propagates_NoRetry_NoCleanup()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            IOException injected = new IOException("boom");
            provisioner.ThrowOnCall(1, injected);

            IOException ex = Assert.Throws<IOException>(() => MakeCoordinator(provisioner, writer).Execute(MakeBatch()));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(provisioner.CallCount, Is.EqualTo(1));
            Assert.That(writer.CallCount, Is.EqualTo(0));
            Assert.That(log, Is.EqualTo(new[] { "Provision:Staging" }));
        }

        [Test]
        public void Exception_WriteStagingInit_Propagates_NoRetry_NoCleanup()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            IOException injected = new IOException("boom");
            writer.ThrowOnCall(1, injected);

            IOException ex = Assert.Throws<IOException>(() => MakeCoordinator(provisioner, writer).Execute(MakeBatch()));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(provisioner.CallCount, Is.EqualTo(1));
            Assert.That(writer.CallCount, Is.EqualTo(1));
            Assert.That(log, Is.EqualTo(new[] { "Provision:Staging", "Write:Staging:Initialization" }));
        }

        [Test]
        public void Exception_ProvisionFinal_Propagates_NoRetry_NoCleanup()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            IOException injected = new IOException("boom");
            provisioner.ThrowOnCall(2, injected);

            IOException ex = Assert.Throws<IOException>(() => MakeCoordinator(provisioner, writer).Execute(MakeBatch()));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(provisioner.CallCount, Is.EqualTo(2));
            Assert.That(writer.CallCount, Is.EqualTo(1));
            Assert.That(log, Is.EqualTo(new[] { "Provision:Staging", "Write:Staging:Initialization", "Provision:Final" }));
        }

        [Test]
        public void Exception_WriteFinalInit_Propagates_NoRetry_NoCleanup()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            IOException injected = new IOException("boom");
            writer.ThrowOnCall(2, injected);

            IOException ex = Assert.Throws<IOException>(() => MakeCoordinator(provisioner, writer).Execute(MakeBatch()));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(provisioner.CallCount, Is.EqualTo(2));
            Assert.That(writer.CallCount, Is.EqualTo(2));
            Assert.That(log, Is.EqualTo(new[]
            {
                "Provision:Staging",
                "Write:Staging:Initialization",
                "Provision:Final",
                "Write:Final:Initialization"
            }));
        }

        [Test]
        public void Exception_WriteStagingReady_Propagates_NoRetry_NoCleanup()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            IOException injected = new IOException("boom");
            writer.ThrowOnCall(3, injected);

            IOException ex = Assert.Throws<IOException>(() => MakeCoordinator(provisioner, writer).Execute(MakeBatch()));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(writer.CallCount, Is.EqualTo(3));
            Assert.That(log, Is.EqualTo(new[]
            {
                "Provision:Staging",
                "Write:Staging:Initialization",
                "Provision:Final",
                "Write:Final:Initialization",
                "Write:Staging:Ready"
            }));
        }

        [Test]
        public void Exception_WriteFinalReady_Propagates_NoRetry_NoCleanup()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            IOException injected = new IOException("boom");
            writer.ThrowOnCall(4, injected);

            IOException ex = Assert.Throws<IOException>(() => MakeCoordinator(provisioner, writer).Execute(MakeBatch()));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(writer.CallCount, Is.EqualTo(4));
            Assert.That(log, Is.EqualTo(new[]
            {
                "Provision:Staging",
                "Write:Staging:Initialization",
                "Provision:Final",
                "Write:Final:Initialization",
                "Write:Staging:Ready",
                "Write:Final:Ready"
            }));
        }

        // ---- Batch pre-validation (no backend calls) ----

        [Test]
        public void NullBatch_Rejected()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => MakeCoordinator(provisioner, writer).Execute(null));

            Assert.That(ex.ParamName, Is.EqualTo("batch"));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void MissingDocuments_NoBackendCalls()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationWriteBatch batch = (CaptureRunInitializationWriteBatch)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationWriteBatch));

            Assert.Throws<ArgumentException>(() => MakeCoordinator(provisioner, writer).Execute(batch));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void MissingPlan_NoBackendCalls()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationWriteBatch batch = (CaptureRunInitializationWriteBatch)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationWriteBatch));
            CaptureRunInitializationDocumentSet documents = (CaptureRunInitializationDocumentSet)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationDocumentSet));
            SetField(batch, "_documents", documents);

            Assert.Throws<ArgumentException>(() => MakeCoordinator(provisioner, writer).Execute(batch));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void MissingMarkerPaths_NoBackendCalls()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationWriteBatch batch = (CaptureRunInitializationWriteBatch)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationWriteBatch));
            CaptureRunInitializationDocumentSet documents = (CaptureRunInitializationDocumentSet)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationDocumentSet));
            CaptureRunInitializationPlan plan = (CaptureRunInitializationPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationPlan));
            SetField(batch, "_documents", documents);
            SetField(documents, "_plan", plan);

            Assert.Throws<ArgumentException>(() => MakeCoordinator(provisioner, writer).Execute(batch));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void MissingRootLayout_NoBackendCalls()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationWriteBatch batch = (CaptureRunInitializationWriteBatch)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationWriteBatch));
            CaptureRunInitializationDocumentSet documents = (CaptureRunInitializationDocumentSet)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationDocumentSet));
            CaptureRunInitializationPlan plan = (CaptureRunInitializationPlan)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationPlan));
            CaptureRunMarkerPathSet markerPaths = (CaptureRunMarkerPathSet)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerPathSet));
            SetField(batch, "_documents", documents);
            SetField(documents, "_plan", plan);
            SetField(plan, "_markerPaths", markerPaths);

            Assert.Throws<ArgumentException>(() => MakeCoordinator(provisioner, writer).Execute(batch));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void MissingOperation_NoBackendCalls()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationWriteBatch batch = MakeBatch();
            SetField(batch, "_stagingInitialization", null);

            Assert.Throws<ArgumentException>(() => MakeCoordinator(provisioner, writer).Execute(batch));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void RoleKindPathMismatch_NoBackendCalls()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationWriteBatch batch = MakeBatch();
            SetField(batch, "_stagingInitialization", batch.FinalInitialization);

            Assert.Throws<ArgumentException>(() => MakeCoordinator(provisioner, writer).Execute(batch));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
        }

        // ---- Result ----

        [Test]
        public void Result_HoldsSevenReferences_And_Forwards()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationWriteBatch batch = MakeBatch();
            CaptureRunInitializationExecutionReceipt result = MakeCoordinator(provisioner, writer).Execute(batch);

            Assert.That(result.Batch, Is.SameAs(batch));
            Assert.That(result.StagingProvision, Is.Not.Null);
            Assert.That(result.FinalProvision, Is.Not.Null);
            Assert.That(result.StagingInitializationWrite, Is.Not.Null);
            Assert.That(result.FinalInitializationWrite, Is.Not.Null);
            Assert.That(result.StagingReadyWrite, Is.Not.Null);
            Assert.That(result.FinalReadyWrite, Is.Not.Null);

            Assert.That(result.StagingProvision.IssuedBy, Is.SameAs(provisioner));
            Assert.That(result.FinalProvision.IssuedBy, Is.SameAs(provisioner));
            Assert.That(result.StagingInitializationWrite.Operation, Is.SameAs(batch.StagingInitialization));
            Assert.That(result.FinalInitializationWrite.Operation, Is.SameAs(batch.FinalInitialization));
            Assert.That(result.StagingReadyWrite.Operation, Is.SameAs(batch.StagingReady));
            Assert.That(result.FinalReadyWrite.Operation, Is.SameAs(batch.FinalReady));

            Assert.That(result.RootLayout, Is.SameAs(batch.Documents.Plan.MarkerPaths.RootLayout));
            Assert.That(result.TestRunId, Is.EqualTo(batch.Documents.Plan.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(batch.Documents.Plan.RunInitializationId));
        }

        [Test]
        public void Result_IsValid_True()
        {
            List<string> log = new List<string>();
            CaptureRunInitializationExecutionReceipt result = MakeCoordinator(
                new FakeProvisioner(log), new FakeWriter(log)).Execute(MakeBatch());

            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Result_Uninitialized_IsInvalid()
        {
            CaptureRunInitializationExecutionReceipt receipt = (CaptureRunInitializationExecutionReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationExecutionReceipt));

            Assert.That(receipt.IsValid, Is.False);
        }

        [Test]
        public void Result_DirectConstructor_ForeignIssuer_Rejected()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationWriteBatch batch = MakeBatch();
            CaptureRunInitializationExecutionReceipt result = MakeCoordinator(provisioner, writer).Execute(batch);

            FakeProvisioner foreign = new FakeProvisioner(new List<string>());
            CaptureRunRootProvisionReceipt foreignProvision = new CaptureRunRootProvisionReceipt(foreign, result.StagingProvision.Operation);

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationExecutionReceipt(
                result.Batch,
                foreignProvision,
                result.FinalProvision,
                result.StagingInitializationWrite,
                result.FinalInitializationWrite,
                result.StagingReadyWrite,
                result.FinalReadyWrite));
        }

        [Test]
        public void Result_DirectConstructor_DifferentOperation_Rejected()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationWriteBatch batch = MakeBatch();
            CaptureRunInitializationExecutionReceipt result = MakeCoordinator(provisioner, writer).Execute(batch);

            CaptureRunMarkerWriteReceipt wrongWrite = new CaptureRunMarkerWriteReceipt(
                result.StagingInitializationWrite.IssuedBy, batch.FinalInitialization);

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationExecutionReceipt(
                result.Batch,
                result.StagingProvision,
                result.FinalProvision,
                wrongWrite,
                result.FinalInitializationWrite,
                result.StagingReadyWrite,
                result.FinalReadyWrite));
        }

        [Test]
        public void Result_DirectConstructor_OrderMismatch_Rejected()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunInitializationWriteBatch batch = MakeBatch();
            CaptureRunInitializationExecutionReceipt result = MakeCoordinator(provisioner, writer).Execute(batch);

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationExecutionReceipt(
                result.Batch,
                result.StagingProvision,
                result.FinalProvision,
                result.StagingInitializationWrite,
                result.FinalInitializationWrite,
                result.FinalReadyWrite,
                result.StagingReadyWrite));
        }

        [Test]
        public void Result_BatchAndGraph_Unchanged()
        {
            CaptureRunInitializationWriteBatch batch = MakeBatch();
            string initIdBefore = batch.Documents.Plan.RunInitializationId;
            string stagingPathBefore = batch.StagingInitialization.FinalPath;
            string rootBefore = batch.Documents.Plan.StagingRunRoot;

            List<string> log = new List<string>();
            CaptureRunInitializationExecutionReceipt result = MakeCoordinator(
                new FakeProvisioner(log), new FakeWriter(log)).Execute(batch);

            Assert.That(batch.Documents.Plan.RunInitializationId, Is.EqualTo(initIdBefore));
            Assert.That(batch.StagingInitialization.FinalPath, Is.EqualTo(stagingPathBefore));
            Assert.That(batch.Documents.Plan.StagingRunRoot, Is.EqualTo(rootBefore));
            Assert.That(result.Batch, Is.SameAs(batch));
        }

        [Test]
        public void Result_DirectConstructor_UninitializedProvisionReceipt_Rejected()
        {
            CaptureRunInitializationWriteBatch batch;
            CaptureRunInitializationExecutionReceipt result = ExecuteValid(out batch);

            CaptureRunRootProvisionReceipt uninitialized = (CaptureRunRootProvisionReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootProvisionReceipt));

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationExecutionReceipt(
                batch,
                uninitialized,
                result.FinalProvision,
                result.StagingInitializationWrite,
                result.FinalInitializationWrite,
                result.StagingReadyWrite,
                result.FinalReadyWrite));
        }

        [Test]
        public void Result_DirectConstructor_UninitializedWriteReceipt_Rejected()
        {
            CaptureRunInitializationWriteBatch batch;
            CaptureRunInitializationExecutionReceipt result = ExecuteValid(out batch);

            CaptureRunMarkerWriteReceipt uninitialized = (CaptureRunMarkerWriteReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerWriteReceipt));

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationExecutionReceipt(
                batch,
                result.StagingProvision,
                result.FinalProvision,
                uninitialized,
                result.FinalInitializationWrite,
                result.StagingReadyWrite,
                result.FinalReadyWrite));
        }

        [Test]
        public void Result_DirectConstructor_NullIssuerReceipts_Rejected()
        {
            CaptureRunInitializationWriteBatch batch;
            CaptureRunInitializationExecutionReceipt result = ExecuteValid(out batch);

            CaptureRunRootProvisionReceipt nullIssuerStaging = (CaptureRunRootProvisionReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootProvisionReceipt));
            SetField(nullIssuerStaging, "_operation", result.StagingProvision.Operation);

            CaptureRunRootProvisionReceipt nullIssuerFinal = (CaptureRunRootProvisionReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootProvisionReceipt));
            SetField(nullIssuerFinal, "_operation", result.FinalProvision.Operation);

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationExecutionReceipt(
                batch,
                nullIssuerStaging,
                nullIssuerFinal,
                result.StagingInitializationWrite,
                result.FinalInitializationWrite,
                result.StagingReadyWrite,
                result.FinalReadyWrite));
        }

        [Test]
        public void Result_DirectConstructor_NullBatchOperation_Rejected()
        {
            CaptureRunInitializationWriteBatch batch;
            CaptureRunInitializationExecutionReceipt result = ExecuteValid(out batch);

            SetField(batch, "_stagingInitialization", null);

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationExecutionReceipt(
                batch,
                result.StagingProvision,
                result.FinalProvision,
                result.StagingInitializationWrite,
                result.FinalInitializationWrite,
                result.StagingReadyWrite,
                result.FinalReadyWrite));
        }

        [Test]
        public void Result_DirectConstructor_RoleKindPathBrokenBatch_Rejected()
        {
            CaptureRunInitializationWriteBatch batch;
            CaptureRunInitializationExecutionReceipt result = ExecuteValid(out batch);

            SetField(batch, "_stagingInitialization", batch.FinalInitialization);

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationExecutionReceipt(
                batch,
                result.StagingProvision,
                result.FinalProvision,
                result.StagingInitializationWrite,
                result.FinalInitializationWrite,
                result.StagingReadyWrite,
                result.FinalReadyWrite));
        }

        [Test]
        public void Result_BrokenOperationBytes_IsValidFalseWithoutThrow_And_DirectConstructorRejected()
        {
            CaptureRunInitializationWriteBatch batch;
            CaptureRunInitializationExecutionReceipt result = ExecuteValid(out batch);

            byte[] finalInitBytesBefore = batch.FinalInitialization.GetCanonicalBytes();
            string stagingReadyPathBefore = batch.StagingReady.FinalPath;
            string finalReadyPathBefore = batch.FinalReady.FinalPath;

            SetField(batch.StagingInitialization, "_canonicalBytes", null);

            bool valid = result.IsValid;
            Assert.That(valid, Is.False);

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationExecutionReceipt(
                batch,
                result.StagingProvision,
                result.FinalProvision,
                result.StagingInitializationWrite,
                result.FinalInitializationWrite,
                result.StagingReadyWrite,
                result.FinalReadyWrite));

            Assert.That(batch.FinalInitialization.GetCanonicalBytes(), Is.EqualTo(finalInitBytesBefore));
            Assert.That(batch.StagingReady.FinalPath, Is.EqualTo(stagingReadyPathBefore));
            Assert.That(batch.FinalReady.FinalPath, Is.EqualTo(finalReadyPathBefore));
        }
    }
}
