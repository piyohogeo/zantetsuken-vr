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
    public class CaptureRunMarkerAtomicWriterContractTests
    {
        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static string RunRoot() => IsWindows ? "C:\\staging\\runs\\run-1" : "/staging/runs/run-1";

        private static string InitializationFinalPath() => RunRoot() + (IsWindows ? "\\" : "/") + "run.init";

        private static CaptureRunMarkerWriteOperation MakeOperation()
        {
            string finalPath = InitializationFinalPath();
            string temporaryPath = finalPath + ".tmp";
            byte[] bytes = new byte[8];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = (byte)(i % 256);
            }

            return new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Staging,
                CaptureRunMarkerKind.Initialization,
                temporaryPath,
                finalPath,
                ref bytes);
        }

        private sealed class FakeWriter : ICaptureRunMarkerAtomicWriter
        {
            public int CallCount;

            public CaptureRunMarkerWriteOperation LastOperation;

            public Exception ExceptionToThrow;

            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                CallCount++;
                LastOperation = operation;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }

        // ---- Interface contract ----

        [Test]
        public void Interface_Internal_ExactlyOneMethod()
        {
            Type type = typeof(ICaptureRunMarkerAtomicWriter);

            Assert.That(type.IsInterface, Is.True);
            Assert.That(type.IsPublic, Is.False);

            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            Assert.That(methods, Has.Length.EqualTo(1), "Exactly one method.");

            MethodInfo method = type.GetMethod("WriteAtomic");
            Assert.That(method, Is.Not.Null);
            Assert.That(method.ReturnType, Is.EqualTo(typeof(CaptureRunMarkerWriteReceipt)));

            ParameterInfo[] parameters = method.GetParameters();
            Assert.That(parameters, Has.Length.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(CaptureRunMarkerWriteOperation)));
        }

        // ---- Receipt construction ----

        [Test]
        public void NullWriter_Rejected()
        {
            CaptureRunMarkerWriteOperation operation = MakeOperation();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunMarkerWriteReceipt(null, operation));

            Assert.That(ex.ParamName, Is.EqualTo("issuedBy"));
        }

        [Test]
        public void NullOperation_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => new CaptureRunMarkerWriteReceipt(new FakeWriter(), null));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void ValidReceipt_HoldsByReference()
        {
            CaptureRunMarkerWriteOperation operation = MakeOperation();
            FakeWriter writer = new FakeWriter();

            CaptureRunMarkerWriteReceipt receipt = new CaptureRunMarkerWriteReceipt(writer, operation);

            Assert.That(receipt.IssuedBy, Is.SameAs(writer));
            Assert.That(receipt.Operation, Is.SameAs(operation));
        }

        [Test]
        public void ForwardingValues_MatchOperation()
        {
            CaptureRunMarkerWriteOperation operation = MakeOperation();
            CaptureRunMarkerWriteReceipt receipt = new CaptureRunMarkerWriteReceipt(new FakeWriter(), operation);

            Assert.That(receipt.RootRole, Is.EqualTo(operation.RootRole));
            Assert.That(receipt.MarkerKind, Is.EqualTo(operation.MarkerKind));
            Assert.That(receipt.TemporaryPath, Is.EqualTo(operation.TemporaryPath));
            Assert.That(receipt.FinalPath, Is.EqualTo(operation.FinalPath));
            Assert.That(receipt.ByteCount, Is.EqualTo(operation.ByteCount));
        }

        [Test]
        public void ValidReceipt_IsValid()
        {
            CaptureRunMarkerWriteReceipt receipt = new CaptureRunMarkerWriteReceipt(new FakeWriter(), MakeOperation());

            Assert.That(receipt.IsValid, Is.True);
        }

        [Test]
        public void UninitializedReceipt_IsInvalid()
        {
            CaptureRunMarkerWriteReceipt receipt = (CaptureRunMarkerWriteReceipt)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunMarkerWriteReceipt));

            Assert.That(receipt.IsValid, Is.False);
        }

        // ---- Fake writer boundary ----

        [Test]
        public void FakeWriter_ReturnsReceiptForSameOperation()
        {
            CaptureRunMarkerWriteOperation operation = MakeOperation();
            FakeWriter writer = new FakeWriter();

            CaptureRunMarkerWriteReceipt receipt = writer.WriteAtomic(operation);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.IssuedBy, Is.SameAs(writer));
            Assert.That(receipt.Operation, Is.SameAs(operation));
            Assert.That(writer.LastOperation, Is.SameAs(operation));
            Assert.That(writer.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void FakeWriter_Exceptions_NotTransformedOrRetried()
        {
            CaptureRunMarkerWriteOperation operation = MakeOperation();
            IOException thrown = new IOException("boom");
            FakeWriter writer = new FakeWriter { ExceptionToThrow = thrown };

            IOException ex = Assert.Throws<IOException>(() => writer.WriteAtomic(operation));

            Assert.That(ex, Is.SameAs(thrown));
            Assert.That(writer.CallCount, Is.EqualTo(1), "No retry.");
            Assert.That(writer.LastOperation, Is.SameAs(operation));
        }

        [Test]
        public void ReceiptConstruction_DoesNotMutateOperation()
        {
            CaptureRunMarkerWriteOperation operation = MakeOperation();
            byte[] expectedBytes = operation.GetCanonicalBytes();

            CaptureRunMarkerWriteReceipt receipt = new CaptureRunMarkerWriteReceipt(new FakeWriter(), operation);

            Assert.That(receipt.Operation, Is.SameAs(operation));
            Assert.That(operation.GetCanonicalBytes(), Is.EqualTo(expectedBytes));
            Assert.That(operation.ByteCount, Is.EqualTo(expectedBytes.Length));
        }

        [Test]
        public void ConsecutiveReceipts_Independent_ShareOnlyOperation()
        {
            CaptureRunMarkerWriteOperation operation = MakeOperation();
            FakeWriter writer = new FakeWriter();

            CaptureRunMarkerWriteReceipt first = new CaptureRunMarkerWriteReceipt(writer, operation);
            CaptureRunMarkerWriteReceipt second = new CaptureRunMarkerWriteReceipt(writer, operation);

            Assert.That(second, Is.Not.SameAs(first));
            Assert.That(second.IssuedBy, Is.SameAs(first.IssuedBy));
            Assert.That(second.Operation, Is.SameAs(first.Operation));
            Assert.That(first.IsValid, Is.True);
            Assert.That(second.IsValid, Is.True);
        }

        // ---- Shape ----

        [Test]
        public void NoPublicConstructorOrSetter()
        {
            Type type = typeof(CaptureRunMarkerWriteReceipt);

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
            Type type = typeof(CaptureRunMarkerWriteReceipt);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void Fields_AreExactlyTwoReadonlyReferences()
        {
            Type type = typeof(CaptureRunMarkerWriteReceipt);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

            Assert.That(fields.Length, Is.EqualTo(2), "Must hold the writer and the operation.");

            int writerFields = 0;
            int operationFields = 0;
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                if (field.FieldType == typeof(ICaptureRunMarkerAtomicWriter))
                {
                    writerFields++;
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

            Assert.That(writerFields, Is.EqualTo(1));
            Assert.That(operationFields, Is.EqualTo(1));
        }

        [Test]
        public void NoArrayCollectionHandleOrMutableStaticState()
        {
            Type type = typeof(CaptureRunMarkerWriteReceipt);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.FieldType.IsArray, Is.False, field.Name + " must not be an array.");

                bool isCollection = typeof(IEnumerable).IsAssignableFrom(field.FieldType) && field.FieldType != typeof(string);
                Assert.That(isCollection, Is.False, field.Name + " must not be a collection.");

                Assert.That(typeof(IDisposable).IsAssignableFrom(field.FieldType), Is.False, field.Name + " must not be a handle or disposable.");
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, field.Name + " must be readonly or const.");
            }
        }

        // ---- Source inspection ----

        [Test]
        public void Source_NoFilesystemPInvokeUnityLoggerRegistryDraftClockRandom()
        {
            string interfaceSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/ICaptureRunMarkerAtomicWriter.cs"));
            string receiptSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunMarkerWriteReceipt.cs"));

            foreach (string source in new[] { interfaceSource, receiptSource })
            {
                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("Stream"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("Logger"));
                Assert.That(source, Does.Not.Contain("Registry"));
                Assert.That(source, Does.Not.Contain("Draft"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Random"));
            }
        }

        [Test]
        public void Source_ClarifiesRetentionBoundary()
        {
            string interfaceSource = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/ICaptureRunMarkerAtomicWriter.cs"));

            string normalized = interfaceSource.Replace("///", " ");
            normalized = string.Join(" ", normalized.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries));

            Assert.That(normalized, Does.Contain("must not retain"));
            Assert.That(normalized, Does.Contain("temporary references"));
            Assert.That(normalized, Does.Contain("defensive copies"));
            Assert.That(normalized, Does.Contain("returned receipt"));
            Assert.That(normalized, Does.Contain("canonical byte array"));
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunMarkerAtomicWriterContractTests).Assembly.Location);
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
