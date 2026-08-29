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
    public class CaptureRunRootProvisionContractTests
    {
        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static string StagingBase() => IsWindows ? "C:\\staging" : "/staging";

        private static string FinalBase() => IsWindows ? "D:\\final" : "/final";

        private static CaptureRunRootLayout MakeLayout(long testRunId = 1)
        {
            return new CaptureRunRootLayout(StagingBase(), FinalBase(), testRunId);
        }

        private static CaptureRunRootProvisionOperation MakeOperation(CaptureRunRootRole role = CaptureRunRootRole.Staging)
        {
            return new CaptureRunRootProvisionOperation(MakeLayout(), role);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        private static string NormalizeDoc(string source)
        {
            string normalized = source.Replace("///", " ");
            return string.Join(" ", normalized.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public int CallCount;

            public CaptureRunRootProvisionOperation LastOperation;

            public Exception ExceptionToThrow;

            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
            {
                CallCount++;
                LastOperation = operation;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        // ---- Operation construction ----

        [Test]
        public void Operation_NullLayout_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunRootProvisionOperation(null, CaptureRunRootRole.Staging));

            Assert.That(ex.ParamName, Is.EqualTo("rootLayout"));
        }

        [Test]
        public void Operation_InvalidRole_RejectedWithParamName()
        {
            CaptureRunRootLayout layout = MakeLayout();

            foreach (CaptureRunRootRole role in new[]
            {
                CaptureRunRootRole.None,
                (CaptureRunRootRole)(-1),
                (CaptureRunRootRole)3,
                (CaptureRunRootRole)int.MaxValue
            })
            {
                ArgumentOutOfRangeException ex = Assert.Throws<ArgumentOutOfRangeException>(
                    () => new CaptureRunRootProvisionOperation(layout, role));

                Assert.That(ex.ParamName, Is.EqualTo("rootRole"));
            }
        }

        [Test]
        public void Operation_StagingForwarding_Exact()
        {
            CaptureRunRootLayout layout = MakeLayout(7);
            CaptureRunRootProvisionOperation operation = new CaptureRunRootProvisionOperation(layout, CaptureRunRootRole.Staging);

            Assert.That(operation.RootRole, Is.EqualTo(CaptureRunRootRole.Staging));
            Assert.That(operation.TrustedBaseRoot, Is.EqualTo(layout.StagingTrustedBaseRoot));
            Assert.That(operation.RunRoot, Is.EqualTo(layout.StagingRunRoot));
            Assert.That(operation.TestRunId, Is.EqualTo(layout.TestRunId));
        }

        [Test]
        public void Operation_FinalForwarding_Exact()
        {
            CaptureRunRootLayout layout = MakeLayout(9);
            CaptureRunRootProvisionOperation operation = new CaptureRunRootProvisionOperation(layout, CaptureRunRootRole.Final);

            Assert.That(operation.RootRole, Is.EqualTo(CaptureRunRootRole.Final));
            Assert.That(operation.TrustedBaseRoot, Is.EqualTo(layout.FinalTrustedBaseRoot));
            Assert.That(operation.RunRoot, Is.EqualTo(layout.FinalRunRoot));
            Assert.That(operation.TestRunId, Is.EqualTo(layout.TestRunId));
        }

        [Test]
        public void Operation_RootLayout_HeldByReference()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunRootProvisionOperation operation = new CaptureRunRootProvisionOperation(layout, CaptureRunRootRole.Staging);

            Assert.That(operation.RootLayout, Is.SameAs(layout));
        }

        [Test]
        public void Operation_Fields_AreExactlyTwoReadonly()
        {
            Type type = typeof(CaptureRunRootProvisionOperation);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(2), "Must hold only the layout and the role; no copied path or ID.");

            int layoutFields = 0;
            int roleFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(CaptureRunRootLayout))
                {
                    layoutFields++;
                }
                else if (field.FieldType == typeof(CaptureRunRootRole))
                {
                    roleFields++;
                }
                else
                {
                    Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
                }
            }

            Assert.That(layoutFields, Is.EqualTo(1));
            Assert.That(roleFields, Is.EqualTo(1));
        }

        [Test]
        public void Operation_BrokenLayout_NullOrEmptyRoot_FailsClosed()
        {
            CaptureRunRootLayout emptyLayout = (CaptureRunRootLayout)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootLayout));

            ArgumentException exAllNull = Assert.Throws<ArgumentException>(
                () => new CaptureRunRootProvisionOperation(emptyLayout, CaptureRunRootRole.Staging));
            Assert.That(exAllNull.ParamName, Is.EqualTo("rootLayout"));

            CaptureRunRootLayout emptyBase = (CaptureRunRootLayout)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootLayout));
            SetField(emptyBase, "_stagingTrustedBaseRoot", string.Empty);
            SetField(emptyBase, "_stagingRunRoot", StagingBase() + (IsWindows ? "\\" : "/") + "runs");
            Assert.Throws<ArgumentException>(
                () => new CaptureRunRootProvisionOperation(emptyBase, CaptureRunRootRole.Staging));

            CaptureRunRootLayout nullRunRoot = (CaptureRunRootLayout)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootLayout));
            SetField(nullRunRoot, "_stagingTrustedBaseRoot", StagingBase());
            Assert.Throws<ArgumentException>(
                () => new CaptureRunRootProvisionOperation(nullRunRoot, CaptureRunRootRole.Staging));
        }

        [Test]
        public void Operation_BrokenLayout_RunRootOutsideTrustedBase_FailsClosed()
        {
            string outside = IsWindows ? "C:\\other\\run" : "/other/run";
            CaptureRunRootLayout outsideLayout = (CaptureRunRootLayout)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootLayout));
            SetField(outsideLayout, "_stagingTrustedBaseRoot", StagingBase());
            SetField(outsideLayout, "_stagingRunRoot", outside);
            Assert.Throws<ArgumentException>(
                () => new CaptureRunRootProvisionOperation(outsideLayout, CaptureRunRootRole.Staging));

            string boundary = IsWindows ? "C:\\staging2\\run" : "/staging2/run";
            CaptureRunRootLayout boundaryLayout = (CaptureRunRootLayout)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootLayout));
            SetField(boundaryLayout, "_stagingTrustedBaseRoot", StagingBase());
            SetField(boundaryLayout, "_stagingRunRoot", boundary);
            Assert.Throws<ArgumentException>(
                () => new CaptureRunRootProvisionOperation(boundaryLayout, CaptureRunRootRole.Staging));
        }

        [Test]
        public void Operation_BrokenLayout_RunRootEqualsTrustedBase_FailsClosed()
        {
            CaptureRunRootLayout layout = (CaptureRunRootLayout)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootLayout));
            SetField(layout, "_stagingTrustedBaseRoot", StagingBase());
            SetField(layout, "_stagingRunRoot", StagingBase());

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => new CaptureRunRootProvisionOperation(layout, CaptureRunRootRole.Staging));

            Assert.That(ex.ParamName, Is.EqualTo("rootLayout"));
        }

        [Test]
        public void Operation_DirectChildAndMultiSegmentDescendant_Accepted()
        {
            string directChild = Path.Combine(StagingBase(), "run");
            CaptureRunRootLayout directLayout = (CaptureRunRootLayout)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootLayout));
            SetField(directLayout, "_stagingTrustedBaseRoot", StagingBase());
            SetField(directLayout, "_stagingRunRoot", directChild);

            CaptureRunRootProvisionOperation directOp = new CaptureRunRootProvisionOperation(
                directLayout, CaptureRunRootRole.Staging);
            Assert.That(directOp.RunRoot, Is.EqualTo(directChild));
            Assert.That(directOp.TrustedBaseRoot, Is.EqualTo(StagingBase()));

            string multiSegment = Path.Combine(StagingBase(), "runs", "run-1");
            CaptureRunRootLayout multiLayout = (CaptureRunRootLayout)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootLayout));
            SetField(multiLayout, "_stagingTrustedBaseRoot", StagingBase());
            SetField(multiLayout, "_stagingRunRoot", multiSegment);

            CaptureRunRootProvisionOperation multiOp = new CaptureRunRootProvisionOperation(
                multiLayout, CaptureRunRootRole.Staging);
            Assert.That(multiOp.RunRoot, Is.EqualTo(multiSegment));
        }

        // ---- Provisioner interface ----

        [Test]
        public void Provisioner_Interface_Internal_ExactlyOneMethod()
        {
            Type type = typeof(ICaptureRunRootProvisioner);

            Assert.That(type.IsInterface, Is.True);
            Assert.That(type.IsPublic, Is.False);

            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.That(methods, Has.Length.EqualTo(1), "Exactly one method.");

            MethodInfo method = type.GetMethod("ProvisionNew");
            Assert.That(method, Is.Not.Null);
            Assert.That(method.ReturnType, Is.EqualTo(typeof(CaptureRunRootProvisionReceipt)));

            ParameterInfo[] parameters = method.GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(CaptureRunRootProvisionOperation)));
        }

        // ---- Receipt ----

        [Test]
        public void Receipt_NullIssuer_Rejected()
        {
            CaptureRunRootProvisionOperation operation = MakeOperation();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunRootProvisionReceipt(null, operation));

            Assert.That(ex.ParamName, Is.EqualTo("issuedBy"));
        }

        [Test]
        public void Receipt_NullOperation_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunRootProvisionReceipt(new FakeProvisioner(), null));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Receipt_HoldsByReference()
        {
            CaptureRunRootProvisionOperation operation = MakeOperation();
            FakeProvisioner provisioner = new FakeProvisioner();

            CaptureRunRootProvisionReceipt receipt = new CaptureRunRootProvisionReceipt(provisioner, operation);

            Assert.That(receipt.IssuedBy, Is.SameAs(provisioner));
            Assert.That(receipt.Operation, Is.SameAs(operation));
        }

        [Test]
        public void Receipt_Forwarding_Exact()
        {
            CaptureRunRootProvisionOperation operation = MakeOperation();
            CaptureRunRootProvisionReceipt receipt = new CaptureRunRootProvisionReceipt(new FakeProvisioner(), operation);

            Assert.That(receipt.RootLayout, Is.SameAs(operation.RootLayout));
            Assert.That(receipt.RootRole, Is.EqualTo(operation.RootRole));
            Assert.That(receipt.TrustedBaseRoot, Is.EqualTo(operation.TrustedBaseRoot));
            Assert.That(receipt.RunRoot, Is.EqualTo(operation.RunRoot));
            Assert.That(receipt.TestRunId, Is.EqualTo(operation.TestRunId));
        }

        [Test]
        public void Receipt_IsValid_True()
        {
            CaptureRunRootProvisionReceipt receipt = new CaptureRunRootProvisionReceipt(new FakeProvisioner(), MakeOperation());

            Assert.That(receipt.IsValid, Is.True);
        }

        [Test]
        public void Receipt_Uninitialized_IsInvalid()
        {
            CaptureRunRootProvisionReceipt receipt = (CaptureRunRootProvisionReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootProvisionReceipt));

            Assert.That(receipt.IsValid, Is.False);
        }

        [Test]
        public void Receipt_Fields_AreExactlyTwoReadonly()
        {
            Type type = typeof(CaptureRunRootProvisionReceipt);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(2));

            int provisionerFields = 0;
            int operationFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(ICaptureRunRootProvisioner))
                {
                    provisionerFields++;
                }
                else if (field.FieldType == typeof(CaptureRunRootProvisionOperation))
                {
                    operationFields++;
                }
                else
                {
                    Assert.Fail(field.Name + " has unexpected type " + field.FieldType.Name + ".");
                }
            }

            Assert.That(provisionerFields, Is.EqualTo(1));
            Assert.That(operationFields, Is.EqualTo(1));
        }

        // ---- Fake provisioner boundary ----

        [Test]
        public void FakeProvisioner_ReturnsReceiptForSameOperation()
        {
            CaptureRunRootProvisionOperation operation = MakeOperation();
            FakeProvisioner provisioner = new FakeProvisioner();

            CaptureRunRootProvisionReceipt receipt = provisioner.ProvisionNew(operation);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.IssuedBy, Is.SameAs(provisioner));
            Assert.That(receipt.Operation, Is.SameAs(operation));
            Assert.That(provisioner.LastOperation, Is.SameAs(operation));
            Assert.That(provisioner.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void FakeProvisioner_Exceptions_NotTransformedOrRetried()
        {
            CaptureRunRootProvisionOperation operation = MakeOperation();
            IOException thrown = new IOException("boom");
            FakeProvisioner provisioner = new FakeProvisioner { ExceptionToThrow = thrown };

            IOException ex = Assert.Throws<IOException>(() => provisioner.ProvisionNew(operation));

            Assert.That(ex, Is.SameAs(thrown));
            Assert.That(provisioner.CallCount, Is.EqualTo(1), "No retry.");
            Assert.That(provisioner.LastOperation, Is.SameAs(operation));
        }

        [Test]
        public void Construction_DoesNotMutateRootLayout()
        {
            CaptureRunRootLayout layout = MakeLayout();
            string stagingRootBefore = layout.StagingRunRoot;
            string finalRootBefore = layout.FinalRunRoot;
            string stagingBaseBefore = layout.StagingTrustedBaseRoot;
            string finalBaseBefore = layout.FinalTrustedBaseRoot;
            long testRunIdBefore = layout.TestRunId;

            CaptureRunRootProvisionOperation operation = new CaptureRunRootProvisionOperation(layout, CaptureRunRootRole.Staging);
            CaptureRunRootProvisionReceipt receipt = new CaptureRunRootProvisionReceipt(new FakeProvisioner(), operation);

            Assert.That(layout.StagingRunRoot, Is.EqualTo(stagingRootBefore));
            Assert.That(layout.FinalRunRoot, Is.EqualTo(finalRootBefore));
            Assert.That(layout.StagingTrustedBaseRoot, Is.EqualTo(stagingBaseBefore));
            Assert.That(layout.FinalTrustedBaseRoot, Is.EqualTo(finalBaseBefore));
            Assert.That(layout.TestRunId, Is.EqualTo(testRunIdBefore));
            Assert.That(receipt.RootLayout, Is.SameAs(layout));
        }

        [Test]
        public void ConsecutiveReceipts_Independent_ShareOnlyOperation()
        {
            CaptureRunRootProvisionOperation operation = MakeOperation();
            FakeProvisioner provisioner = new FakeProvisioner();

            CaptureRunRootProvisionReceipt first = new CaptureRunRootProvisionReceipt(provisioner, operation);
            CaptureRunRootProvisionReceipt second = new CaptureRunRootProvisionReceipt(provisioner, operation);

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.IssuedBy, Is.SameAs(first.IssuedBy));
            Assert.That(second.Operation, Is.SameAs(first.Operation));
            Assert.That(first.IsValid, Is.True);
            Assert.That(second.IsValid, Is.True);
        }

        // ---- Shape ----

        [Test]
        public void NoPublicConstructorOrSetter_Sealed_NotDisposable()
        {
            foreach (Type type in new[] { typeof(CaptureRunRootProvisionOperation), typeof(CaptureRunRootProvisionReceipt) })
            {
                Assert.That(type.IsPublic, Is.False, type.Name);
                Assert.That(type.IsSealed, Is.True, type.Name);
                Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty, type.Name);
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
        public void NoCollectionHandleOrMutableStaticState()
        {
            foreach (Type type in new[] { typeof(CaptureRunRootProvisionOperation), typeof(CaptureRunRootProvisionReceipt) })
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(field.FieldType.IsArray, Is.False, field.Name + " must not be an array.");
                    bool isCollection = typeof(IEnumerable).IsAssignableFrom(field.FieldType) && field.FieldType != typeof(string);
                    Assert.That(isCollection, Is.False, field.Name + " must not be a collection.");
                    Assert.That(typeof(IDisposable).IsAssignableFrom(field.FieldType), Is.False, field.Name + " must not be a handle.");
                }

                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
                }
            }
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoForbiddenDependencies()
        {
            string operationSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunRootProvisionOperation.cs"));
            string provisionerSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/ICaptureRunRootProvisioner.cs"));
            string receiptSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunRootProvisionReceipt.cs"));

            foreach (string source in new[] { operationSource, provisionerSource, receiptSource })
            {
                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("Stream"));
                Assert.That(source, Does.Not.Contain("FileInfo"));
                Assert.That(source, Does.Not.Contain("DirectoryInfo"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("Logger"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("CaptureRunInitializationMarker"));
                Assert.That(source, Does.Not.Contain("CaptureRunReadyMarker"));
                Assert.That(source, Does.Not.Contain("SerializeCanonical"));
                Assert.That(source, Does.Not.Contain("ComputeContentSha256"));
                Assert.That(source, Does.Not.Contain("SHA256"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
            }
        }

        [Test]
        public void Xml_ClarifiesProvisionContract()
        {
            string provisionerSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/ICaptureRunRootProvisioner.cs"));

            string normalized = NormalizeDoc(provisionerSource);

            Assert.That(normalized, Does.Contain("already-existing run root"));
            Assert.That(normalized, Does.Contain("must not return success"));
            Assert.That(normalized, Does.Contain("recovery pass"));
            Assert.That(normalized, Does.Contain("tmp-only root"));
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunRootProvisionContractTests).Assembly.Location);
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
