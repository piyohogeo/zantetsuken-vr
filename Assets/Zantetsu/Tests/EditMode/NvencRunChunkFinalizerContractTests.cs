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
    /// Contract tests for the Phase 0.11 Run chunk finalizer interface and
    /// receipt boundary: the single synchronous finalize method and the
    /// immutable success evidence that binds the exact issuer, operation, and
    /// descriptor.
    /// </summary>
    public class NvencRunChunkFinalizerContractTests
    {
        private const byte Seed = 0x40;

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ChunkPath = "chunks/chunk-0.nvenc-idr-chunk-v1.h264";

        [Test]
        public void Interface_SingleSynchronousMethod()
        {
            Type type = typeof(INvencRunChunkFinalizer);

            Assert.That(type.IsInterface, Is.True);

            MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            Assert.That(methods.Length, Is.EqualTo(1));

            MethodInfo method = methods[0];
            Assert.That(method.Name, Is.EqualTo("FinalizeChunk"));
            Assert.That(method.ReturnType, Is.EqualTo(typeof(NvencRunChunkFinalizationReceipt)));
            Assert.That(method.IsAbstract, Is.True);

            ParameterInfo[] parameters = method.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(1));
            Assert.That(parameters[0].ParameterType, Is.EqualTo(typeof(NvencRunChunkFinalizationOperation)));
        }

        [Test]
        public void Receipt_ForwardsAllValuesExactly()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token1 = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease1);
            Assert.That(h.Sink.TryAppend(token1, lease1, out _), Is.True);
            CaptureFrameWorkToken token3 = h.ProduceOwnedLease(3, 48, Seed, out NvencOwnedAccessUnitLease lease3);
            Assert.That(h.Sink.TryAppend(token3, lease3, out _), Is.True);
            Assert.That(h.Sink.TryCaptureFinalizationEvidence(2, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);

            NvencRunChunkFinalizationOperation operation =
                NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, "chunk/0");
            CaptureArtifactDescriptor descriptor =
                NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 112, Hash64);

            FakeFinalizer finalizer = new FakeFinalizer();
            NvencRunChunkFinalizationReceipt receipt =
                NvencRunChunkFinalizationReceipt.Create(finalizer, operation, descriptor);

            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.IssuedBy, Is.SameAs(finalizer));
            Assert.That(receipt.Operation, Is.SameAs(operation));
            Assert.That(receipt.Descriptor, Is.SameAs(descriptor));
            Assert.That(receipt.Sink, Is.SameAs(h.Sink));
            Assert.That(receipt.Evidence, Is.SameAs(evidence));
            Assert.That(receipt.FrameRelation, Is.SameAs(operation.FrameRelation));
            Assert.That(receipt.ArtifactId, Is.EqualTo("chunk/0"));
            Assert.That(receipt.ArtifactKind, Is.EqualTo(CaptureArtifactKind.FrameSequence));
            Assert.That(receipt.FormatId, Is.EqualTo("NvencH264IdrChunk"));
            Assert.That(receipt.FormatVersion, Is.EqualTo(1));
            Assert.That(receipt.StagingRelativePath, Is.EqualTo(ChunkPath));
            Assert.That(receipt.FinalRelativePath, Is.EqualTo(ChunkPath));
            Assert.That(receipt.ByteLength, Is.EqualTo(112));
            Assert.That(receipt.ContentHash, Is.EqualTo(Hash64));
            Assert.That(receipt.AppendedCount, Is.EqualTo(2));
            Assert.That(receipt.LastFrameId, Is.EqualTo(3));
        }

        [Test]
        public void Receipt_NullArgs_RejectedWithParamName()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease);
            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.True);
            Assert.That(h.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);
            NvencRunChunkFinalizationOperation operation =
                NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, "chunk/0");
            CaptureArtifactDescriptor descriptor =
                NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, Hash64);
            FakeFinalizer finalizer = new FakeFinalizer();

            ArgumentNullException issuedByEx = Assert.Throws<ArgumentNullException>(() =>
                NvencRunChunkFinalizationReceipt.Create(null, operation, descriptor));
            Assert.That(issuedByEx.ParamName, Is.EqualTo("issuedBy"));

            ArgumentNullException operationEx = Assert.Throws<ArgumentNullException>(() =>
                NvencRunChunkFinalizationReceipt.Create(finalizer, null, descriptor));
            Assert.That(operationEx.ParamName, Is.EqualTo("operation"));

            ArgumentNullException descriptorEx = Assert.Throws<ArgumentNullException>(() =>
                NvencRunChunkFinalizationReceipt.Create(finalizer, operation, null));
            Assert.That(descriptorEx.ParamName, Is.EqualTo("descriptor"));
        }

        [Test]
        public void Receipt_InvalidOperation_Rejected()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease);
            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.True);
            Assert.That(h.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);
            NvencRunChunkFinalizationOperation operation =
                NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, "chunk/0");
            CaptureArtifactDescriptor descriptor =
                NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, Hash64);

            // Invalidate the operation by appending after it was issued.
            CaptureFrameWorkToken token2 = h.ProduceOwnedLease(2, 48, Seed, out NvencOwnedAccessUnitLease lease2);
            Assert.That(h.Sink.TryAppend(token2, lease2, out _), Is.True);
            Assert.That(operation.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(() =>
                NvencRunChunkFinalizationReceipt.Create(new FakeFinalizer(), operation, descriptor));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
        }

        [Test]
        public void Receipt_IsIssuedForForeignIssuerOrOperation_False()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease);
            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.True);
            Assert.That(h.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);
            NvencRunChunkFinalizationOperation operation =
                NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, "chunk/0");
            CaptureArtifactDescriptor descriptor =
                NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, Hash64);

            FakeFinalizer finalizer = new FakeFinalizer();
            NvencRunChunkFinalizationReceipt receipt =
                NvencRunChunkFinalizationReceipt.Create(finalizer, operation, descriptor);

            Assert.That(receipt.IsIssuedFor(finalizer, operation), Is.True);
            Assert.That(receipt.IsIssuedFor(new FakeFinalizer(), operation), Is.False);

            NvencRunChunkFinalizationOperation otherOperation =
                NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, "chunk/other");
            Assert.That(receipt.IsIssuedFor(finalizer, otherOperation), Is.False);
        }

        [Test]
        public void Receipt_DescriptorMismatch_Rejected()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease);
            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.True);
            Assert.That(h.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);
            NvencRunChunkFinalizationOperation operation =
                NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, "chunk/0");
            FakeFinalizer finalizer = new FakeFinalizer();

            CaptureArtifactDescriptor wrongId =
                MakeChunkDescriptor("chunk/other", CaptureArtifactKind.FrameSequence, "NvencH264IdrChunk", 1, ChunkPath, ChunkPath, 64);
            CaptureArtifactDescriptor wrongKind =
                MakeChunkDescriptor("chunk/0", CaptureArtifactKind.FrameImage, "NvencH264IdrChunk", 1, ChunkPath, ChunkPath, 64);
            CaptureArtifactDescriptor wrongFormat =
                MakeChunkDescriptor("chunk/0", CaptureArtifactKind.FrameSequence, "other/format", 1, ChunkPath, ChunkPath, 64);
            CaptureArtifactDescriptor wrongVersion =
                MakeChunkDescriptor("chunk/0", CaptureArtifactKind.FrameSequence, "NvencH264IdrChunk", 2, ChunkPath, ChunkPath, 64);
            CaptureArtifactDescriptor wrongStaging =
                MakeChunkDescriptor("chunk/0", CaptureArtifactKind.FrameSequence, "NvencH264IdrChunk", 1, "chunks/other.h264", ChunkPath, 64);
            CaptureArtifactDescriptor wrongFinal =
                MakeChunkDescriptor("chunk/0", CaptureArtifactKind.FrameSequence, "NvencH264IdrChunk", 1, ChunkPath, "chunks/other.h264", 64);
            CaptureArtifactDescriptor wrongLength =
                MakeChunkDescriptor("chunk/0", CaptureArtifactKind.FrameSequence, "NvencH264IdrChunk", 1, ChunkPath, ChunkPath, 65);

            foreach (CaptureArtifactDescriptor descriptor in new[]
            {
                wrongId, wrongKind, wrongFormat, wrongVersion, wrongStaging, wrongFinal, wrongLength,
            })
            {
                Assert.Throws<ArgumentException>(() =>
                    NvencRunChunkFinalizationReceipt.Create(finalizer, operation, descriptor));
            }
        }

        [Test]
        public void Receipt_InvalidHashOrPartialDescriptor_Rejected()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease);
            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.True);
            Assert.That(h.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);
            NvencRunChunkFinalizationOperation operation =
                NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, "chunk/0");
            FakeFinalizer finalizer = new FakeFinalizer();

            CaptureArtifactDescriptor badHash =
                NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, Hash64);
            SetBackingField(badHash, "ContentHash", "zz");
            Assert.Throws<ArgumentException>(() =>
                NvencRunChunkFinalizationReceipt.Create(finalizer, operation, badHash));

            CaptureArtifactDescriptor partial =
                MakeChunkDescriptor("chunk/0", CaptureArtifactKind.FrameSequence, "NvencH264IdrChunk", 1,
                    ChunkPath + ".partial", ChunkPath, 64);
            Assert.Throws<ArgumentException>(() =>
                NvencRunChunkFinalizationReceipt.Create(finalizer, operation, partial));
        }

        [Test]
        public void Receipt_InvalidAfterAppendPoisonOrRunAbandoned()
        {
            Harness appended = new Harness();
            CaptureFrameWorkToken appendToken = appended.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease appendLease);
            Assert.That(appended.Sink.TryAppend(appendToken, appendLease, out _), Is.True);
            Assert.That(appended.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence appendEvidence), Is.True);
            NvencRunChunkFinalizationOperation appendOperation =
                NvencRunChunkFinalizationOperation.Create(appended.Sink, appendEvidence, "chunk/0");
            NvencRunChunkFinalizationReceipt appendReceipt =
                NvencRunChunkFinalizationReceipt.Create(new FakeFinalizer(), appendOperation,
                    NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, Hash64));
            Assert.That(appendReceipt.IsValid, Is.True);
            CaptureFrameWorkToken extraToken = appended.ProduceOwnedLease(2, 48, Seed, out NvencOwnedAccessUnitLease extraLease);
            Assert.That(appended.Sink.TryAppend(extraToken, extraLease, out _), Is.True);
            Assert.That(appendReceipt.IsValid, Is.False);

            Harness poisoned = new Harness();
            CaptureFrameWorkToken poisonToken = poisoned.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease poisonLease);
            Assert.That(poisoned.Sink.TryAppend(poisonToken, poisonLease, out _), Is.True);
            Assert.That(poisoned.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence poisonEvidence), Is.True);
            NvencRunChunkFinalizationOperation poisonOperation =
                NvencRunChunkFinalizationOperation.Create(poisoned.Sink, poisonEvidence, "chunk/0");
            NvencRunChunkFinalizationReceipt poisonReceipt =
                NvencRunChunkFinalizationReceipt.Create(new FakeFinalizer(), poisonOperation,
                    NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, Hash64));
            Assert.That(poisonReceipt.IsValid, Is.True);
            Assert.That(poisoned.State.TryPoison(), Is.True);
            // A post-finalization poison invalidates pre-finalization
            // acceptance but the issued ledger binding stays intact, so the
            // receipt remains valid.
            Assert.That(poisonOperation.IsValid, Is.False);
            Assert.That(poisonReceipt.IsValid, Is.True);

            Harness abandoned = new Harness();
            CaptureFrameWorkToken abandonToken = abandoned.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease abandonLease);
            Assert.That(abandoned.Sink.TryAppend(abandonToken, abandonLease, out _), Is.True);
            Assert.That(abandoned.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence abandonEvidence), Is.True);
            NvencRunChunkFinalizationOperation abandonOperation =
                NvencRunChunkFinalizationOperation.Create(abandoned.Sink, abandonEvidence, "chunk/0");
            NvencRunChunkFinalizationReceipt abandonReceipt =
                NvencRunChunkFinalizationReceipt.Create(new FakeFinalizer(), abandonOperation,
                    NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, Hash64));
            Assert.That(abandonReceipt.IsValid, Is.True);
            Assert.That(abandoned.State.TryBeginRunAbandoned(), Is.True);
            // Run abandonment invalidates pre-finalization acceptance but not
            // the issued ledger binding, so the receipt remains valid.
            Assert.That(abandonOperation.IsValid, Is.False);
            Assert.That(abandonReceipt.IsValid, Is.True);
        }

        [Test]
        public void Receipt_DescriptorFieldCorruption_ReturnsFalseWithoutThrowing()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease);
            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.True);
            Assert.That(h.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);
            NvencRunChunkFinalizationOperation operation =
                NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, "chunk/0");
            CaptureArtifactDescriptor descriptor =
                NvencRunChunkArtifactDescriptorFactory.Create("chunk/0", 64, Hash64);
            NvencRunChunkFinalizationReceipt receipt =
                NvencRunChunkFinalizationReceipt.Create(new FakeFinalizer(), operation, descriptor);
            Assert.That(receipt.IsValid, Is.True);

            SetBackingField(descriptor, "ContentHash", "zz");
            Assert.That(receipt.IsValid, Is.False);

            SetBackingField(descriptor, "ByteLength", 0L);
            Assert.That(receipt.IsValid, Is.False);
        }

        [Test]
        public void Receipt_SealedThreeReadonlyFields_NotDisposable_NoPublicCtor()
        {
            Type type = typeof(NvencRunChunkFinalizationReceipt);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(fields.Length, Is.EqualTo(3));

            Type[] expected =
            {
                typeof(INvencRunChunkFinalizer),
                typeof(NvencRunChunkFinalizationOperation),
                typeof(CaptureArtifactDescriptor),
            };

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(Array.IndexOf(expected, field.FieldType), Is.GreaterThanOrEqualTo(0),
                    field.Name + " has an unexpected type.");
            }
        }

        [Test]
        public void Receipt_DoesNotHoldOrExposeForbiddenTypes()
        {
            Type type = typeof(NvencRunChunkFinalizationReceipt);

            Type[] forbidden =
            {
                typeof(NvencOwnedAccessUnitLease),
                typeof(NvencOwnedAccessUnitBuffer),
                typeof(INvencRunChunkAppender),
                typeof(Stream),
                typeof(byte[]),
                typeof(CaptureFrameWorkToken),
            };

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(Array.IndexOf(forbidden, field.FieldType), Is.LessThan(0),
                    field.Name + " must not hold a forbidden type.");
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(Array.IndexOf(forbidden, property.PropertyType), Is.LessThan(0),
                    property.Name + " must not expose a forbidden type.");
            }
        }

        [Test]
        public void ReceiptSource_NoFilesystemHashCloseRename()
        {
            string source = File.ReadAllText(
                Path.Combine(RuntimeDirectory(), "NvencRunChunkFinalizationReceipt.cs"));

            string[] forbidden =
            {
                "File.", "Directory.", "FileStream", "Stream", "ComputeHash", "HashAlgorithm",
                "IncrementalHash", "SHA256", "SHA384", "SHA512", "MD5", "System.Security.Cryptography",
                "Close(", "Flush(", "Move(", "rename", "Rename", "Task", "new Thread", "ThreadPool",
                "UnityEngine", "Application.", "DllImport", "IntPtr", "SafeHandle", "lock (", "Monitor",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "receipt source must not contain: " + word);
            }
        }

        [Test]
        public void InterfaceSource_NoHashRereadOverwriteRenameRetryRollback()
        {
            string source = File.ReadAllText(
                Path.Combine(RuntimeDirectory(), "INvencRunChunkFinalizer.cs"));

            string[] forbidden =
            {
                "File.", "Directory.", "FileStream", "Stream", "Flush(", "File.Move", "File.Replace",
                "ReadAllBytes", "ReadAllText", "Task", "new Thread", "ThreadPool", "Retry",
                "Rollback", "Truncate", "lock (", "Monitor", "UnityEngine", "Application.",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "finalizer contract must not contain: " + word);
            }
        }

        private static CaptureArtifactDescriptor MakeChunkDescriptor(
            string artifactId,
            CaptureArtifactKind kind,
            string formatId,
            int formatVersion,
            string stagingRelativePath,
            string finalRelativePath,
            long byteLength)
        {
            return new CaptureArtifactDescriptor(
                artifactId, kind, formatId, formatVersion,
                stagingRelativePath, finalRelativePath, byteLength, Hash64);
        }

        private static void SetBackingField(object target, string propertyName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                "<" + propertyName + ">k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
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
            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
                throw new NotSupportedException();
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
