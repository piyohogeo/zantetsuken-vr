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
    /// Contract tests for the Phase 0.11 Run chunk finalization operation and
    /// factory boundary: the immutable request that binds an exact sink and its
    /// finalization evidence to a caller-supplied artifact id, and the
    /// stateless factory that delegates exactly once.
    /// </summary>
    public class NvencRunChunkFinalizationOperationContractTests
    {
        private const byte Seed = 0x40;

        [Test]
        public void Create_ForwardsAllValuesExactly()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token1 = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease1);
            Assert.That(h.Sink.TryAppend(token1, lease1, out _), Is.True);
            CaptureFrameWorkToken token2 = h.ProduceOwnedLease(2, 48, Seed, out NvencOwnedAccessUnitLease lease2);
            Assert.That(h.Sink.TryAppend(token2, lease2, out _), Is.True);

            Assert.That(h.Sink.TryCaptureFinalizationEvidence(2, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);

            NvencRunChunkFinalizationOperation operation =
                NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, "chunk/0");

            Assert.That(operation.Sink, Is.SameAs(h.Sink));
            Assert.That(operation.Evidence, Is.SameAs(evidence));
            Assert.That(operation.ArtifactId, Is.EqualTo("chunk/0"));
            Assert.That(operation.AppendedCount, Is.EqualTo(2));
            Assert.That(operation.AccumulatedByteLength, Is.EqualTo(112));
            Assert.That(operation.LastFrameId, Is.EqualTo(2));
            Assert.That(operation.FrameRelation, Is.SameAs(evidence.FrameRelation));
            Assert.That(operation.ArtifactKind, Is.EqualTo(CaptureArtifactKind.FrameSequence));
            Assert.That(operation.FormatId, Is.EqualTo("NvencH264IdrChunk"));
            Assert.That(operation.FormatVersion, Is.EqualTo(1));
            Assert.That(operation.StagingRelativePath, Is.EqualTo("chunks/chunk-0.nvenc-idr-chunk-v1.h264"));
            Assert.That(operation.FinalRelativePath, Is.EqualTo("chunks/chunk-0.nvenc-idr-chunk-v1.h264"));
            Assert.That(operation.PendingRelativePath, Is.EqualTo("chunks/chunk-0.nvenc-idr-chunk-v1.h264.partial"));
            Assert.That(operation.IsValid, Is.True);
        }

        [Test]
        public void Create_NullArgs_RejectedWithParamName()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease);
            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.True);
            Assert.That(h.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);

            ArgumentNullException sinkEx = Assert.Throws<ArgumentNullException>(() =>
                NvencRunChunkFinalizationOperation.Create(null, evidence, "chunk/0"));
            Assert.That(sinkEx.ParamName, Is.EqualTo("sink"));

            ArgumentNullException evidenceEx = Assert.Throws<ArgumentNullException>(() =>
                NvencRunChunkFinalizationOperation.Create(h.Sink, null, "chunk/0"));
            Assert.That(evidenceEx.ParamName, Is.EqualTo("evidence"));

            ArgumentNullException idEx = Assert.Throws<ArgumentNullException>(() =>
                NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, null));
            Assert.That(idEx.ParamName, Is.EqualTo("artifactId"));
        }

        [Test]
        public void Create_ForeignSinkOrForeignEvidence_Rejected()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease);
            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.True);
            Assert.That(h.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);

            Harness other = new Harness();
            Assert.Throws<ArgumentException>(() =>
                NvencRunChunkFinalizationOperation.Create(other.Sink, evidence, "chunk/0"));

            CaptureFrameWorkToken otherToken = other.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease otherLease);
            Assert.That(other.Sink.TryAppend(otherToken, otherLease, out _), Is.True);
            Assert.That(other.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence otherEvidence), Is.True);

            Assert.Throws<ArgumentException>(() =>
                NvencRunChunkFinalizationOperation.Create(h.Sink, otherEvidence, "chunk/0"));
        }

        [Test]
        public void Create_SameValuesDifferentEvidenceReference_AcceptedPerEvidenceContract()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease);
            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.True);

            Assert.That(h.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence first), Is.True);
            Assert.That(h.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence second), Is.True);

            Assert.That(ReferenceEquals(first, second), Is.False);
            Assert.That(first.IsIssuedFor(h.Sink), Is.True);
            Assert.That(second.IsIssuedFor(h.Sink), Is.True);

            NvencRunChunkFinalizationOperation operation =
                NvencRunChunkFinalizationOperation.Create(h.Sink, second, "chunk/0");
            Assert.That(operation.Evidence, Is.SameAs(second));
            Assert.That(operation.IsValid, Is.True);
        }

        [Test]
        public void IsValid_FalseAfterAppend()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token1 = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease1);
            Assert.That(h.Sink.TryAppend(token1, lease1, out _), Is.True);
            Assert.That(h.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);

            NvencRunChunkFinalizationOperation operation =
                NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, "chunk/0");
            Assert.That(operation.IsValid, Is.True);

            CaptureFrameWorkToken token2 = h.ProduceOwnedLease(2, 48, Seed, out NvencOwnedAccessUnitLease lease2);
            Assert.That(h.Sink.TryAppend(token2, lease2, out _), Is.True);

            Assert.That(operation.IsValid, Is.False);
        }

        [Test]
        public void IsValid_FalseAfterPoisonOrRunAbandoned()
        {
            Harness poisoned = new Harness();
            CaptureFrameWorkToken poisonToken = poisoned.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease poisonLease);
            Assert.That(poisoned.Sink.TryAppend(poisonToken, poisonLease, out _), Is.True);
            Assert.That(poisoned.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence poisonEvidence), Is.True);
            NvencRunChunkFinalizationOperation poisonOp =
                NvencRunChunkFinalizationOperation.Create(poisoned.Sink, poisonEvidence, "chunk/0");
            Assert.That(poisonOp.IsValid, Is.True);
            Assert.That(poisoned.State.TryPoison(), Is.True);
            Assert.That(poisonOp.IsValid, Is.False);

            Harness abandoned = new Harness();
            CaptureFrameWorkToken abandonToken = abandoned.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease abandonLease);
            Assert.That(abandoned.Sink.TryAppend(abandonToken, abandonLease, out _), Is.True);
            Assert.That(abandoned.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence abandonEvidence), Is.True);
            NvencRunChunkFinalizationOperation abandonOp =
                NvencRunChunkFinalizationOperation.Create(abandoned.Sink, abandonEvidence, "chunk/0");
            Assert.That(abandonOp.IsValid, Is.True);
            Assert.That(abandoned.State.TryBeginRunAbandoned(), Is.True);
            Assert.That(abandonOp.IsValid, Is.False);
        }

        [Test]
        public void Create_InvalidArtifactId_Rejected()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease);
            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.True);
            Assert.That(h.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);

            Assert.Throws<ArgumentException>(() =>
                NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, ""));
            Assert.Throws<ArgumentException>(() =>
                NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, new string('a', 513)));
        }

        [Test]
        public void FixedValues_MatchDescriptorFactoryConstants()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease);
            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.True);
            Assert.That(h.Sink.TryCaptureFinalizationEvidence(1, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);

            NvencRunChunkFinalizationOperation operation =
                NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, "chunk/0");

            Assert.That(operation.ArtifactKind, Is.EqualTo(CaptureArtifactKind.FrameSequence));
            Assert.That(operation.FormatId, Is.EqualTo(NvencRunChunkArtifactDescriptorFactory.FormatId));
            Assert.That(operation.FormatVersion, Is.EqualTo(NvencRunChunkArtifactDescriptorFactory.FormatVersion));
            Assert.That(operation.StagingRelativePath, Is.EqualTo(NvencRunChunkArtifactDescriptorFactory.StagingRelativePath));
            Assert.That(operation.FinalRelativePath, Is.EqualTo(NvencRunChunkArtifactDescriptorFactory.FinalRelativePath));
            Assert.That(operation.PendingRelativePath, Is.EqualTo(NvencRunChunkArtifactDescriptorFactory.PendingRelativePath));
        }

        [Test]
        public void Operation_SealedThreeReadonlyFields_NotDisposable_NoPublicCtor()
        {
            Type type = typeof(NvencRunChunkFinalizationOperation);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(fields.Length, Is.EqualTo(3));

            Type[] expected =
            {
                typeof(NvencRunChunkSink),
                typeof(NvencRunChunkSinkFinalizationEvidence),
                typeof(string),
            };

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(Array.IndexOf(expected, field.FieldType), Is.GreaterThanOrEqualTo(0),
                    field.Name + " has an unexpected type.");
            }
        }

        [Test]
        public void Operation_DoesNotHoldOrExposeForbiddenTypes()
        {
            Type type = typeof(NvencRunChunkFinalizationOperation);

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
        public void OperationSource_NoFilesystemHashCloseFlushRenameThreadTask()
        {
            string source = File.ReadAllText(
                Path.Combine(RuntimeDirectory(), "NvencRunChunkFinalizationOperation.cs"));

            string[] forbidden =
            {
                "File.", "Directory.", "FileStream", "Stream", "new Thread", "ThreadPool",
                "Task", "ComputeHash", "HashAlgorithm", "IncrementalHash", "SHA256", "SHA384",
                "SHA512", "MD5", "System.Security.Cryptography", "Close(", "Flush(", "Move(",
                "rename", "Rename", "UnityEngine", "Application.", "DllImport", "IntPtr",
                "SafeHandle", "lock (", "Monitor",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "operation source must not contain: " + word);
            }
        }

        private static CaptureFrameWorkToken MakeToken(long frameId)
        {
            return new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, frameId);
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
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
