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


        private static CaptureRunRootLayout MakeLayout(long testRunId = 1)
        {
            return new CaptureRunRootLayout(StagingBase(), testRunId);
        }

        private static CaptureRunMarkerBinding MakeMarkers(CaptureRunRootLayout layout)
        {
            return new CaptureRunMarkerBinding(
                layout.TestRunId,
                InitId,
                layout.RunRootSha256);
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
                _log.Add("Provision");

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
                _log.Add("Write:" + operation.MarkerKind);

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

        private static CaptureRunInitializationExecutionReceipt ExecuteValid(out CaptureRunRootLayout layout)
        {
            layout = MakeLayout();
            List<string> log = new List<string>();
            return MakeCoordinator(new FakeProvisioner(log), new FakeWriter(log)).Execute(layout, InitId);
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
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationExecutionReceipt result = MakeCoordinator(provisioner, writer).Execute(layout, InitId);

            Assert.That(result, Is.Not.Null);
            Assert.That(log, Is.EqualTo(new[]
            {
                "Provision",
                "Write:Initialization",
                "Write:Ready"
            }));
            Assert.That(provisioner.CallCount, Is.EqualTo(1));
            Assert.That(writer.CallCount, Is.EqualTo(2));
        }

        // ---- Receipt validation: staging provision ----

        [Test]
        public void StagingProvision_NullReceipt_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            provisioner.ReceiptFactory = op => null;

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeLayout(), InitId));
            Assert.That(log, Is.EqualTo(new[] { "Provision" }));
        }

        [Test]
        public void StagingProvision_UninitializedReceipt_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            provisioner.ReceiptFactory = op => (CaptureRunRootProvisionReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootProvisionReceipt));

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeLayout(), InitId));
            Assert.That(log, Is.EqualTo(new[] { "Provision" }));
        }

        [Test]
        public void StagingProvision_ForeignIssuer_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            FakeProvisioner foreign = new FakeProvisioner(new List<string>());
            provisioner.ReceiptFactory = op => new CaptureRunRootProvisionReceipt(foreign, op);

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeLayout(), InitId));
            Assert.That(log, Is.EqualTo(new[] { "Provision" }));
        }

        [Test]
        public void StagingProvision_DifferentOperation_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunRootProvisionOperation foreignOperation = new CaptureRunRootProvisionOperation(MakeLayout(2));
            provisioner.ReceiptFactory = op => new CaptureRunRootProvisionReceipt(provisioner, foreignOperation);

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(layout, InitId));
            Assert.That(log, Is.EqualTo(new[] { "Provision" }));
        }

        // ---- Receipt validation: staging initialization write ----

        [Test]
        public void StagingInitWrite_NullReceipt_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            writer.ReceiptFactory = op => null;

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeLayout(), InitId));
            Assert.That(log, Is.EqualTo(new[] { "Provision", "Write:Initialization" }));
        }

        [Test]
        public void StagingInitWrite_UninitializedReceipt_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            writer.ReceiptFactory = op => (CaptureRunMarkerWriteReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerWriteReceipt));

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeLayout(), InitId));
            Assert.That(log, Is.EqualTo(new[] { "Provision", "Write:Initialization" }));
        }

        [Test]
        public void StagingInitWrite_ForeignIssuer_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            FakeWriter foreign = new FakeWriter(new List<string>());
            writer.ReceiptFactory = op => new CaptureRunMarkerWriteReceipt(foreign, op);

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeLayout(), InitId));
            Assert.That(log, Is.EqualTo(new[] { "Provision", "Write:Initialization" }));
        }

        [Test]
        public void StagingInitWrite_DifferentOperation_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            CaptureRunMarkerWriteOperation foreignOperation = new CaptureRunMarkerWriteOperation(
                CaptureRunMarkerKind.Ready,
                markerPaths.ReadyTemporaryPath,
                markerPaths.ReadyPath,
                CaptureRunReadyMarkerCodec.SerializeCanonical(MakeMarkers(layout).Ready));
            writer.ReceiptFactory = op => new CaptureRunMarkerWriteReceipt(writer, foreignOperation);

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(layout, InitId));
            Assert.That(log, Is.EqualTo(new[] { "Provision", "Write:Initialization" }));
        }

        // ---- Receipt validation: final provision and ready writes ----

        [Test]
        public void StagingReadyWrite_NullReceipt_Stops()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            int call = 0;
            writer.ReceiptFactory = op => ++call == 2 ? null : new CaptureRunMarkerWriteReceipt(writer, op);

            Assert.Throws<InvalidOperationException>(() => MakeCoordinator(provisioner, writer).Execute(MakeLayout(), InitId));
            Assert.That(log, Is.EqualTo(new[]
            {
                "Provision",
                "Write:Initialization",
                "Write:Ready"
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

            IOException ex = Assert.Throws<IOException>(() => MakeCoordinator(provisioner, writer).Execute(MakeLayout(), InitId));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(provisioner.CallCount, Is.EqualTo(1));
            Assert.That(writer.CallCount, Is.EqualTo(0));
            Assert.That(log, Is.EqualTo(new[] { "Provision" }));
        }

        [Test]
        public void Exception_WriteStagingInit_Propagates_NoRetry_NoCleanup()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            IOException injected = new IOException("boom");
            writer.ThrowOnCall(1, injected);

            IOException ex = Assert.Throws<IOException>(() => MakeCoordinator(provisioner, writer).Execute(MakeLayout(), InitId));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(provisioner.CallCount, Is.EqualTo(1));
            Assert.That(writer.CallCount, Is.EqualTo(1));
            Assert.That(log, Is.EqualTo(new[] { "Provision", "Write:Initialization" }));
        }

        [Test]
        public void Exception_WriteReady_Propagates_NoRetry_NoCleanup()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            IOException injected = new IOException("boom");
            writer.ThrowOnCall(2, injected);

            IOException ex = Assert.Throws<IOException>(() => MakeCoordinator(provisioner, writer).Execute(MakeLayout(), InitId));

            Assert.That(ex, Is.SameAs(injected));
            Assert.That(writer.CallCount, Is.EqualTo(2));
            Assert.That(log, Is.EqualTo(new[]
            {
                "Provision",
                "Write:Initialization",
                "Write:Ready"
            }));
        }

        // ---- Batch pre-validation (no backend calls) ----

        [Test]
        public void NullRootLayout_Rejected()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => MakeCoordinator(provisioner, writer).Execute(null, InitId));

            Assert.That(ex.ParamName, Is.EqualTo("rootLayout"));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void InvalidInitializationId_NoBackendCalls()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);

            Assert.Throws<ArgumentException>(
                () => MakeCoordinator(provisioner, writer).Execute(MakeLayout(), "not-a-run-initialization-id"));
            Assert.That(provisioner.CallCount, Is.EqualTo(0));
            Assert.That(writer.CallCount, Is.EqualTo(0));
        }

        // ---- Result ----

        [Test]
        public void Result_HoldsReceipts_And_Forwards()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt result = MakeCoordinator(provisioner, writer).Execute(layout, InitId);

            Assert.That(result.MarkerPaths.RootLayout, Is.SameAs(layout));
            Assert.That(result.Provision, Is.Not.Null);
            Assert.That(result.InitializationWrite, Is.Not.Null);
            Assert.That(result.ReadyWrite, Is.Not.Null);

            Assert.That(result.Provision.IssuedBy, Is.SameAs(provisioner));
            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(layout);
            Assert.That(result.InitializationWrite.Operation.FinalPath, Is.EqualTo(markerPaths.InitializationPath));
            Assert.That(result.ReadyWrite.Operation.FinalPath, Is.EqualTo(markerPaths.ReadyPath));

            Assert.That(result.RootLayout, Is.SameAs(markerPaths.RootLayout));
            Assert.That(result.TestRunId, Is.EqualTo(markerPaths.RootLayout.TestRunId));
            Assert.That(result.RunInitializationId, Is.EqualTo(InitId));
        }

        [Test]
        public void Result_IsValid_True()
        {
            List<string> log = new List<string>();
            CaptureRunInitializationExecutionReceipt result = MakeCoordinator(
                new FakeProvisioner(log), new FakeWriter(log)).Execute(MakeLayout(), InitId);

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
        public void Result_DirectConstructor_DifferentOperation_Rejected()
        {
            List<string> log = new List<string>();
            FakeProvisioner provisioner = new FakeProvisioner(log);
            FakeWriter writer = new FakeWriter(log);
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt result = MakeCoordinator(provisioner, writer).Execute(layout, InitId);

            CaptureRunMarkerWriteReceipt wrongWrite = new CaptureRunMarkerWriteReceipt(
                result.InitializationWrite.IssuedBy, result.ReadyWrite.Operation);

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationExecutionReceipt(
                result.MarkerPaths,
                result.RunInitializationId,
                result.Provision,
                wrongWrite,
                result.ReadyWrite));
        }

        [Test]
        public void Result_InputsAndGraph_Unchanged()
        {
            CaptureRunRootLayout layout = MakeLayout();
            string initIdBefore = InitId;
            string stagingPathBefore = new CaptureRunMarkerPathSet(layout).InitializationPath;
            string rootBefore = layout.RunRoot;

            List<string> log = new List<string>();
            CaptureRunInitializationExecutionReceipt result = MakeCoordinator(
                new FakeProvisioner(log), new FakeWriter(log)).Execute(layout, InitId);

            Assert.That(result.RunInitializationId, Is.EqualTo(initIdBefore));
            Assert.That(result.MarkerPaths.InitializationPath, Is.EqualTo(stagingPathBefore));
            Assert.That(layout.RunRoot, Is.EqualTo(rootBefore));
            Assert.That(result.MarkerPaths.RootLayout, Is.SameAs(layout));
        }

        [Test]
        public void Result_DirectConstructor_UninitializedProvisionReceipt_Rejected()
        {
            CaptureRunRootLayout layout;
            CaptureRunInitializationExecutionReceipt result = ExecuteValid(out layout);

            CaptureRunRootProvisionReceipt uninitialized = (CaptureRunRootProvisionReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootProvisionReceipt));

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationExecutionReceipt(
                result.MarkerPaths,
                result.RunInitializationId,
                uninitialized,
                result.InitializationWrite,
                result.ReadyWrite));
        }

        [Test]
        public void Result_DirectConstructor_UninitializedWriteReceipt_Rejected()
        {
            CaptureRunRootLayout layout;
            CaptureRunInitializationExecutionReceipt result = ExecuteValid(out layout);

            CaptureRunMarkerWriteReceipt uninitialized = (CaptureRunMarkerWriteReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerWriteReceipt));

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationExecutionReceipt(
                result.MarkerPaths,
                result.RunInitializationId,
                result.Provision,
                uninitialized,
                result.ReadyWrite));
        }

        [Test]
        public void Result_DirectConstructor_NullIssuerReceipts_Rejected()
        {
            CaptureRunRootLayout layout;
            CaptureRunInitializationExecutionReceipt result = ExecuteValid(out layout);

            CaptureRunRootProvisionReceipt nullIssuerStaging = (CaptureRunRootProvisionReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootProvisionReceipt));
            SetField(nullIssuerStaging, "_operation", result.Provision.Operation);

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationExecutionReceipt(
                result.MarkerPaths,
                result.RunInitializationId,
                nullIssuerStaging,
                result.InitializationWrite,
                result.ReadyWrite));
        }

        [Test]
        public void Result_BrokenOperationBytes_IsValidFalseWithoutThrow_And_DirectConstructorRejected()
        {
            CaptureRunRootLayout layout;
            CaptureRunInitializationExecutionReceipt result = ExecuteValid(out layout);

            string stagingReadyPathBefore = result.ReadyWrite.Operation.FinalPath;

            SetField(result.InitializationWrite.Operation, "_canonicalBytes", null);

            bool valid = result.IsValid;
            Assert.That(valid, Is.False);

            Assert.Throws<ArgumentException>(() => new CaptureRunInitializationExecutionReceipt(
                result.MarkerPaths,
                result.RunInitializationId,
                result.Provision,
                result.InitializationWrite,
                result.ReadyWrite));

            Assert.That(result.ReadyWrite.Operation.FinalPath, Is.EqualTo(stagingReadyPathBefore));
        }
    }
}
