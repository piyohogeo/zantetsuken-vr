using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using NvencAccessUnitCopyStatus = Zantetsu.Observability.NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 Run chunk finalization coordinator,
    /// its issuance proof, and the finalization result, including the
    /// pre-finalization validity vs post-issuance binding separation.
    /// </summary>
    public class NvencRunChunkFinalizationCoordinatorContractTests
    {
        private const byte Seed = 0x40;

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ChunkPath = "chunks/chunk-0.nvenc-idr-chunk-v1.h264";

        [Test]
        public void Execute_ExactlyOnce_ForwardsResult()
        {
            Harness h = new Harness();
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");
            FakeFinalizer finalizer = new FakeFinalizer();
            NvencRunChunkFinalizationCoordinator coordinator =
                new NvencRunChunkFinalizationCoordinator(finalizer);

            NvencChunkFinalizationResult result = coordinator.Execute(operation);

            Assert.That(finalizer.CallCount, Is.EqualTo(1));
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Coordinator, Is.SameAs(coordinator));
            Assert.That(result.Finalizer, Is.SameAs(finalizer));
            Assert.That(result.Operation, Is.SameAs(operation));
            Assert.That(result.Receipt.IssuedBy, Is.SameAs(finalizer));
            Assert.That(result.Receipt.Operation, Is.SameAs(operation));
            Assert.That(result.Descriptor, Is.SameAs(result.Receipt.Descriptor));
            Assert.That(result.FrameRelation, Is.SameAs(operation.FrameRelation));
            Assert.That(result.Sink, Is.SameAs(h.Sink));
            Assert.That(result.Evidence, Is.SameAs(operation.Evidence));
            Assert.That(result.ArtifactId, Is.EqualTo("chunk/0"));
            Assert.That(result.ArtifactKind, Is.EqualTo(CaptureArtifactKind.FrameSequence));
            Assert.That(result.FormatId, Is.EqualTo("NvencH264IdrChunk"));
            Assert.That(result.FormatVersion, Is.EqualTo(1));
            Assert.That(result.StagingRelativePath, Is.EqualTo(ChunkPath));
            Assert.That(result.FinalRelativePath, Is.EqualTo(ChunkPath));
            Assert.That(result.ByteLength, Is.EqualTo(64));
            Assert.That(result.ContentHash, Is.EqualTo(Hash64));
            Assert.That(result.AppendedCount, Is.EqualTo(1));
            Assert.That(result.LastFrameId, Is.EqualTo(1));
        }

        [Test]
        public void Execute_NullOrInvalidOperation_FinalizerNotCalled()
        {
            FakeFinalizer finalizer = new FakeFinalizer();
            NvencRunChunkFinalizationCoordinator coordinator =
                new NvencRunChunkFinalizationCoordinator(finalizer);

            ArgumentNullException nullEx = Assert.Throws<ArgumentNullException>(() => coordinator.Execute(null));
            Assert.That(nullEx.ParamName, Is.EqualTo("operation"));
            Assert.That(finalizer.CallCount, Is.EqualTo(0));

            Harness h = new Harness();
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");
            CaptureFrameWorkToken token = h.ProduceOwnedLease(2, 48, Seed, out NvencOwnedAccessUnitLease lease);
            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.True);
            Assert.That(operation.IsValid, Is.False);

            ArgumentException invalidEx = Assert.Throws<ArgumentException>(() => coordinator.Execute(operation));
            Assert.That(invalidEx.ParamName, Is.EqualTo("operation"));
            Assert.That(finalizer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Execute_FinalizerException_PropagatesOnceNoResult()
        {
            Harness h = new Harness();
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");
            FakeFinalizer finalizer = new FakeFinalizer();
            InvalidOperationException boom = new InvalidOperationException("boom");
            finalizer.ExceptionToThrow = boom;
            NvencRunChunkFinalizationCoordinator coordinator =
                new NvencRunChunkFinalizationCoordinator(finalizer);

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(() => coordinator.Execute(operation));
            Assert.That(ReferenceEquals(thrown, boom), Is.True);
            Assert.That(finalizer.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Execute_InvalidReceipt_Rejected()
        {
            Harness h = new Harness();
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");

            // Null receipt.
            FakeFinalizer nullFinalizer = new FakeFinalizer { BuildReceipt = false };
            NvencRunChunkFinalizationCoordinator nullCoordinator =
                new NvencRunChunkFinalizationCoordinator(nullFinalizer);
            Assert.Throws<InvalidOperationException>(() => nullCoordinator.Execute(operation));
            Assert.That(nullFinalizer.CallCount, Is.EqualTo(1));

            // Foreign issuer receipt.
            FakeFinalizer finalizer = new FakeFinalizer();
            NvencRunChunkFinalizationCoordinator coordinator =
                new NvencRunChunkFinalizationCoordinator(finalizer);
            FakeFinalizer otherIssuer = new FakeFinalizer();
            NvencRunChunkFinalizationReceipt foreignReceipt =
                NvencRunChunkFinalizationReceipt.Create(otherIssuer, operation,
                    NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, Hash64));
            finalizer.ReceiptToReturn = foreignReceipt;
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(operation));
            finalizer.ReceiptToReturn = null;

            // Different operation receipt.
            Harness h2 = new Harness();
            NvencRunChunkFinalizationOperation otherOperation = MakeOperation(h2, "chunk/other");
            NvencRunChunkFinalizationReceipt otherReceipt =
                NvencRunChunkFinalizationReceipt.Create(finalizer, otherOperation,
                    NvencRunChunkArtifactDescriptorFactory.Create("chunk/other", 64, Hash64));
            finalizer.ReceiptToReturn = otherReceipt;
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(operation));
            finalizer.ReceiptToReturn = null;

            // Corrupted receipt descriptor.
            NvencRunChunkFinalizationReceipt tampered =
                NvencRunChunkFinalizationReceipt.Create(finalizer, operation,
                    NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, Hash64));
            SetBackingField(tampered.Descriptor, "ContentHash", "zz");
            finalizer.ReceiptToReturn = tampered;
            Assert.Throws<InvalidOperationException>(() => coordinator.Execute(operation));
        }

        [Test]
        public void Result_CannotBeIssuedFromDirectReceiptAlone()
        {
            Harness h = new Harness();
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");
            FakeFinalizer finalizer = new FakeFinalizer();
            NvencRunChunkFinalizationCoordinator coordinator =
                new NvencRunChunkFinalizationCoordinator(finalizer);
            NvencRunChunkFinalizationReceipt receipt =
                NvencRunChunkFinalizationReceipt.Create(finalizer, operation,
                    NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, Hash64));

            // The proof has no public or internal constructor; only the
            // coordinator can mint it, so a direct receipt cannot produce a
            // valid result.
            Type proofType = typeof(NvencRunChunkFinalizationCoordinator.IssuanceProof);
            ConstructorInfo[] constructors = proofType.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(constructors.Length, Is.GreaterThan(0));
            foreach (ConstructorInfo constructor in constructors)
            {
                Assert.That(constructor.IsPrivate, Is.True, "proof constructor must be private.");
            }

            ArgumentNullException proofEx = Assert.Throws<ArgumentNullException>(() =>
                NvencChunkFinalizationResult.Create(coordinator, null, operation, receipt));
            Assert.That(proofEx.ParamName, Is.EqualTo("proof"));

            // Mint rejects a null or foreign gate, so no proof can be minted
            // outside Execute; the finalizer is never called.
            Assert.Throws<ArgumentException>(() =>
                NvencRunChunkFinalizationCoordinator.IssuanceProof.Mint(
                    null, coordinator, finalizer, operation, receipt));
            Assert.Throws<ArgumentException>(() =>
                NvencRunChunkFinalizationCoordinator.IssuanceProof.Mint(
                    new object(), coordinator, finalizer, operation, receipt));
            Assert.That(finalizer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void IssuanceProof_ExactBinding_PrivateGateNotExposed()
        {
            Harness h = new Harness();
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");
            FakeFinalizer finalizer = new FakeFinalizer();
            NvencRunChunkFinalizationCoordinator coordinator =
                new NvencRunChunkFinalizationCoordinator(finalizer);
            NvencChunkFinalizationResult result = coordinator.Execute(operation);

            NvencRunChunkFinalizationCoordinator.IssuanceProof proof = result.Proof;
            NvencRunChunkFinalizationReceipt receipt = result.Receipt;

            Assert.That(proof.IsMintedFor(coordinator, finalizer, operation, receipt), Is.True);

            NvencRunChunkFinalizationCoordinator otherCoordinator =
                new NvencRunChunkFinalizationCoordinator(new FakeFinalizer());
            Assert.That(proof.IsMintedFor(otherCoordinator, finalizer, operation, receipt), Is.False);
            Assert.That(proof.IsMintedFor(coordinator, new FakeFinalizer(), operation, receipt), Is.False);
            Assert.That(proof.IsMintedFor(coordinator, finalizer, MakeOperation(new Harness(), "chunk/x"), receipt), Is.False);

            Harness h3 = new Harness();
            NvencRunChunkFinalizationOperation op3 = MakeOperation(h3, "chunk/3");
            NvencRunChunkFinalizationReceipt otherReceipt =
                NvencRunChunkFinalizationReceipt.Create(finalizer, op3,
                    NvencRunChunkArtifactDescriptorFactory.Create("chunk/3", 64, Hash64));
            Assert.That(proof.IsMintedFor(coordinator, finalizer, operation, otherReceipt), Is.False);

            Type proofType = typeof(NvencRunChunkFinalizationCoordinator.IssuanceProof);
            Assert.That(proofType.GetFields(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(proofType.GetProperties(BindingFlags.Public | BindingFlags.Instance), Is.Empty);
            Assert.That(proofType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), Is.Empty);
        }

        [Test]
        public void Result_AfterPoison_OperationFalseButReceiptResultTrue()
        {
            Harness h = new Harness();
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");
            FakeFinalizer finalizer = new FakeFinalizer();
            NvencRunChunkFinalizationCoordinator coordinator =
                new NvencRunChunkFinalizationCoordinator(finalizer);
            NvencChunkFinalizationResult result = coordinator.Execute(operation);

            Assert.That(operation.IsValid, Is.True);
            Assert.That(result.IsValid, Is.True);

            Assert.That(h.State.TryPoison(), Is.True);

            Assert.That(operation.IsValid, Is.False);
            Assert.That(result.Receipt.IsValid, Is.True);
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Result_AfterLedgerAdvance_ReceiptResultFalse()
        {
            Harness h = new Harness();
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");
            FakeFinalizer finalizer = new FakeFinalizer();
            NvencRunChunkFinalizationCoordinator coordinator =
                new NvencRunChunkFinalizationCoordinator(finalizer);
            NvencChunkFinalizationResult result = coordinator.Execute(operation);
            Assert.That(result.IsValid, Is.True);

            CaptureFrameWorkToken token = h.ProduceOwnedLease(2, 48, Seed, out NvencOwnedAccessUnitLease lease);
            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.True);

            Assert.That(result.Receipt.IsValid, Is.False);
            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void Result_DescriptorOrReceiptFieldSwap_FailClosed()
        {
            Harness h = new Harness();
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");
            FakeFinalizer finalizer = new FakeFinalizer();
            NvencRunChunkFinalizationCoordinator coordinator =
                new NvencRunChunkFinalizationCoordinator(finalizer);
            NvencChunkFinalizationResult result = coordinator.Execute(operation);
            Assert.That(result.IsValid, Is.True);

            SetBackingField(result.Descriptor, "ContentHash", "zz");
            Assert.That(result.IsValid, Is.False);

            Harness h2 = new Harness();
            NvencRunChunkFinalizationOperation other = MakeOperation(h2, "chunk/other");
            SetField(result.Receipt, "_operation", other);
            Assert.That(result.IsValid, Is.False);
        }

        [Test]
        public void Result_SealedFourReadonlyFields_NotDisposable_NoPublicCtor()
        {
            Type type = typeof(NvencChunkFinalizationResult);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(fields.Length, Is.EqualTo(4));

            Type[] expected =
            {
                typeof(NvencRunChunkFinalizationCoordinator),
                typeof(NvencRunChunkFinalizationCoordinator.IssuanceProof),
                typeof(NvencRunChunkFinalizationOperation),
                typeof(NvencRunChunkFinalizationReceipt),
            };

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(Array.IndexOf(expected, field.FieldType), Is.GreaterThanOrEqualTo(0),
                    field.Name + " has an unexpected type.");
            }
        }

        [Test]
        public void Coordinator_SingleFinalizerField_NotDisposable()
        {
            Type type = typeof(NvencRunChunkFinalizationCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(INvencRunChunkFinalizer)));
            Assert.That(fields[0].IsInitOnly, Is.True);
        }

        [Test]
        public void Sources_NoFilesystemHashCloseRenameRetryThreadTask()
        {
            string directory = RuntimeDirectory();
            string source =
                File.ReadAllText(Path.Combine(directory, "NvencRunChunkFinalizationCoordinator.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencChunkFinalizationResult.cs"));

            string[] forbidden =
            {
                "File.", "Directory.", "FileStream", "Stream", "ComputeHash", "HashAlgorithm",
                "IncrementalHash", "SHA256", "SHA384", "SHA512", "MD5", "System.Security.Cryptography",
                "Close(", "Flush(", "Move(", "rename", "Rename", "Retry", "Rollback", "Truncate",
                "Task", "new Thread", "ThreadPool", "lock (", "Monitor", "UnityEngine", "Application.",
                "DllImport", "IntPtr", "SafeHandle",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "coordinator/result source must not contain: " + word);
            }
        }

        private static NvencRunChunkFinalizationOperation MakeOperation(Harness h, string artifactId)
        {
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease);
            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.True);
            Assert.That(h.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);
            return NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, artifactId);
        }

        private static void SetBackingField(object target, string propertyName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                "<" + propertyName + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(target, value);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(target, value);
        }

        private static CaptureFrameWorkToken MakeToken(long frameId)
        {
            return new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, frameId);
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        private sealed class FakeFinalizer : INvencRunChunkFinalizer
        {
            internal int CallCount;
            internal Exception ExceptionToThrow;
            internal NvencRunChunkFinalizationReceipt ReceiptToReturn;
            internal bool BuildReceipt = true;

            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
                CallCount++;
                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (ReceiptToReturn != null)
                {
                    return ReceiptToReturn;
                }

                if (BuildReceipt)
                {
                    CaptureArtifactDescriptor descriptor = NvencRunChunkArtifactDescriptorFactory.Create(
                        operation.ArtifactId, operation.AccumulatedByteLength, Hash64);
                    return NvencRunChunkFinalizationReceipt.Create(this, operation, descriptor);
                }

                return null;
            }
        }

        private sealed class PatternSource : INvencOutputBitstreamSource
        {
            private readonly int _length;
            private readonly byte _seed;

            internal PatternSource(int length, byte seed)
            {
                _length = length;
                _seed = seed;
            }

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                for (int i = 0; i < _length; i++)
                {
                    destination[i] = (byte)(_seed + i);
                }

                validLength = _length;
                return true;
            }
        }

        private sealed class FakeAppender : INvencRunChunkAppender
        {
            internal int CallCount;

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                CallCount++;
                return NvencRunChunkAppendOutcome.Appended;
            }
        }

        private sealed class Harness
        {
            internal NvencCaptureProcessState State = new NvencCaptureProcessState();
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal FakeAppender Writer = new FakeAppender();
            internal NvencRunChunkSink Sink;

            internal Harness()
            {
                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
            }

            internal CaptureFrameWorkToken ProduceOwnedLease(
                long frameId,
                int length,
                byte seed,
                out NvencOwnedAccessUnitLease lease)
            {
                CaptureFrameWorkToken token = MakeToken(frameId);
                Assert.That(Buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
                Assert.That(Buffer.TryCopyCompletedOutput(write, default, new PatternSource(length, seed), out _),
                    Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
                Assert.That(Buffer.TryTransferToSink(write, out lease), Is.True);
                return token;
            }
        }
    }
}
