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
    /// Contract tests for the Phase 0.11 Run chunk context: the fixed-capacity
    /// accepted frame-id sequence and the exclusive, exactly-once terminal
    /// transition that binds the sink, finalization coordinator, and finalized
    /// result.
    /// </summary>
    public class NvencRunChunkContextContractTests
    {
        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        // ---- Initial state ----

        [Test]
        public void Initial_Open_NoResult()
        {
            Harness h = new Harness();

            Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Open));
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(0));
            Assert.That(h.Context.TryGetAcceptedFrameId(0, out _), Is.False);
            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult result), Is.False);
            Assert.That(result, Is.Null);
        }

        // ---- Accepted registration ----

        [Test]
        public void Accepted_ZeroOneOneHundredTwenty()
        {
            Harness h = new Harness();
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(0));

            Assert.That(h.Context.TryRecordAcceptedFrame(1), Is.True);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(1));
            Assert.That(h.Context.TryGetAcceptedFrameId(0, out long first), Is.True);
            Assert.That(first, Is.EqualTo(1));

            for (long id = 2; id <= NvencBringUpProfileV1.CadenceTickCount; id++)
            {
                Assert.That(h.Context.TryRecordAcceptedFrame(id), Is.True);
            }

            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(NvencBringUpProfileV1.CadenceTickCount));
            for (int i = 0; i < NvencBringUpProfileV1.CadenceTickCount; i++)
            {
                Assert.That(h.Context.TryGetAcceptedFrameId(i, out long id), Is.True);
                Assert.That(id, Is.EqualTo(i + 1));
            }
        }

        [Test]
        public void Accepted_OneHundredTwentyFirstDuplicateBackwardInvalid_AtomicallyRejected()
        {
            Harness h = new Harness();
            for (long id = 1; id <= NvencBringUpProfileV1.CadenceTickCount; id++)
            {
                Assert.That(h.Context.TryRecordAcceptedFrame(id), Is.True);
            }

            int count = h.Context.AcceptedFrameCount;

            // 121st entry.
            Assert.That(h.Context.TryRecordAcceptedFrame(NvencBringUpProfileV1.CadenceTickCount + 1), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(count));

            // Duplicate of the last entry.
            Assert.That(h.Context.TryRecordAcceptedFrame(NvencBringUpProfileV1.CadenceTickCount), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(count));

            // Backward entry.
            Assert.That(h.Context.TryRecordAcceptedFrame(50), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(count));

            // Non-positive entries.
            Assert.That(h.Context.TryRecordAcceptedFrame(0), Is.False);
            Assert.That(h.Context.TryRecordAcceptedFrame(-5), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(count));
        }

        [Test]
        public void Accepted_AfterTerminal_Rejected()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);
            h.Context.TryAbandon();

            Assert.That(h.Context.TryRecordAcceptedFrame(2), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(1));
        }

        // ---- Finalize ----

        [Test]
        public void Finalize_AcceptedMatchesSinkRelation_Success()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);
            h.AcceptAndAppend(2, 48, Seed);

            Assert.That(h.Context.TryFinalize(out NvencChunkFinalizationResult result), Is.True);
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Sink, Is.SameAs(h.Sink));
            Assert.That(result.ArtifactId, Is.EqualTo("chunk/0"));
            Assert.That(result.AppendedCount, Is.EqualTo(2));
            Assert.That(result.LastFrameId, Is.EqualTo(2));
            Assert.That(h.Finalizer.CallCount, Is.EqualTo(1));
            Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Finalized));

            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult held), Is.True);
            Assert.That(ReferenceEquals(held, result), Is.True);
        }

        [Test]
        public void Finalize_ZeroAccepted_FinalizerNotContacted()
        {
            Harness h = new Harness();

            Assert.That(h.Context.TryFinalize(out NvencChunkFinalizationResult result), Is.False);
            Assert.That(result, Is.Null);
            Assert.That(h.Finalizer.CallCount, Is.EqualTo(0));
            Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Open));
        }

        [Test]
        public void Finalize_CountInsufficientExtraOrderMismatch_FinalizerNotContacted()
        {
            // Insufficient: the sink has fewer appends than accepted.
            Harness insufficient = new Harness();
            insufficient.Context.TryRecordAcceptedFrame(1);
            insufficient.Context.TryRecordAcceptedFrame(2);
            insufficient.Append(1, 64, Seed);
            Assert.That(insufficient.Context.TryFinalize(out _), Is.False);
            Assert.That(insufficient.Finalizer.CallCount, Is.EqualTo(0));

            // Extra: the sink has more appends than accepted.
            Harness extra = new Harness();
            extra.Context.TryRecordAcceptedFrame(1);
            extra.Append(1, 64, Seed);
            extra.Append(2, 48, Seed);
            Assert.That(extra.Context.TryFinalize(out _), Is.False);
            Assert.That(extra.Finalizer.CallCount, Is.EqualTo(0));

            // Order mismatch: same count, different frame id sequence.
            Harness order = new Harness();
            order.Context.TryRecordAcceptedFrame(1);
            order.Context.TryRecordAcceptedFrame(2);
            order.Append(1, 64, Seed);
            order.Append(3, 48, Seed);
            Assert.That(order.Context.TryFinalize(out _), Is.False);
            Assert.That(order.Finalizer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Finalize_ExactlyOnce_ResultSameReference()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);

            Assert.That(h.Context.TryFinalize(out NvencChunkFinalizationResult result), Is.True);
            Assert.That(h.Finalizer.CallCount, Is.EqualTo(1));

            Assert.That(h.Context.TryFinalize(out NvencChunkFinalizationResult second), Is.False);
            Assert.That(second, Is.Null);
            Assert.That(h.Finalizer.CallCount, Is.EqualTo(1));

            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult held), Is.True);
            Assert.That(ReferenceEquals(held, result), Is.True);
        }

        [Test]
        public void Finalized_ThenAcceptedAddReFinalizeAbandonRejected()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);
            h.Context.TryFinalize(out _);

            Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Finalized));
            Assert.That(h.Context.TryRecordAcceptedFrame(2), Is.False);
            Assert.That(h.Context.TryFinalize(out _), Is.False);
            Assert.That(h.Context.TryAbandon(), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(1));
        }

        // ---- Abandon ----

        [Test]
        public void Abandon_OpenToAbandoned_ExactlyOnce_NoResult()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);

            Assert.That(h.Context.TryAbandon(), Is.True);
            Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Abandoned));
            Assert.That(h.Context.TryAbandon(), Is.False);
            Assert.That(h.Finalizer.CallCount, Is.EqualTo(0));

            Assert.That(h.Context.TryRecordAcceptedFrame(2), Is.False);
            Assert.That(h.Context.TryFinalize(out _), Is.False);
            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult result), Is.False);
            Assert.That(result, Is.Null);
        }

        // ---- Result publication ----

        [Test]
        public void FinalizedResult_SwapOrNull_FailClosed()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);
            Assert.That(h.Context.TryFinalize(out NvencChunkFinalizationResult result), Is.True);
            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult held), Is.True);
            Assert.That(ReferenceEquals(held, result), Is.True);

            // Nulled result fails closed.
            SetField(h.Context, "_finalizationResult", null);
            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult afterNull), Is.False);
            Assert.That(afterNull, Is.Null);

            // Swapped foreign result fails closed.
            Harness other = new Harness("chunk/other");
            other.AcceptAndAppend(1, 64, Seed);
            Assert.That(other.Context.TryFinalize(out NvencChunkFinalizationResult foreign), Is.True);
            SetField(h.Context, "_finalizationResult", foreign);
            Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult afterSwap), Is.False);
            Assert.That(afterSwap, Is.Null);
        }

        // ---- Constructor binding ----

        [Test]
        public void Constructor_NullOrInvalidInputs_Rejected()
        {
            Harness h = new Harness();

            Assert.Throws<ArgumentNullException>(() =>
                new NvencRunChunkContext(null, h.Sink, h.Coordinator, "chunk/0"));
            Assert.Throws<ArgumentNullException>(() =>
                new NvencRunChunkContext(MakeIssue(), null, h.Coordinator, "chunk/0"));
            Assert.Throws<ArgumentNullException>(() =>
                new NvencRunChunkContext(MakeIssue(), h.Sink, null, "chunk/0"));
            Assert.Throws<ArgumentNullException>(() =>
                new NvencRunChunkContext(MakeIssue(), h.Sink, h.Coordinator, null));
            Assert.Throws<ArgumentException>(() =>
                new NvencRunChunkContext(MakeIssue(), h.Sink, h.Coordinator, string.Empty));
        }

        // ---- Ownership and allocation ----

        [Test]
        public void Context_NotDisposable_NoLeaseOwnership_NoFilesystem()
        {
            Type type = typeof(NvencRunChunkContext);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            // The context never holds or disposes the ownership lease.
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureRunInitializationSessionOwnershipLease)),
                    "context must not hold the ownership lease.");
            }

            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencRunChunkContext.cs"));
            string[] forbidden =
            {
                "File.", "Directory.", "FileStream", "System.IO", "new Thread", "ThreadPool",
                "Task", "lock (", "Monitor",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "context source must not contain: " + word);
            }
        }

        [Test]
        public void AcceptedRegistration_AllocationFree()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencRunChunkContext.cs"));
            string body = ExtractMethodBody(source, "TryRecordAcceptedFrame");

            string[] forbidden =
            {
                "new ", "Array.", "List", "Queue", "Dictionary", "Enumerable",
                ".Select(", ".Where(", ".ToList(", ".ToArray(",
            };

            foreach (string word in forbidden)
            {
                Assert.That(body, Does.Not.Contain(word), "accepted registration must not allocate: " + word);
            }
        }

        // ---- Helpers ----

        private static CaptureRunInitializationSessionIssue MakeIssue()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true) { Tag = "first" };
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true) { Tag = "second" };
            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);
            CaptureRunInitializationSessionOwnershipLease owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);
            return CaptureRunInitializationSessionFactory.Create(owner, identity, evidence);
        }

        private static CaptureRunRootLayout MakeLayout()
        {
            return new CaptureRunRootLayout(
                Path.DirectorySeparatorChar == '\\' ? "C:\\staging" : "/staging",
                Path.DirectorySeparatorChar == '\\' ? "D:\\final" : "/final",
                1);
        }

        private static CaptureRunInitializationExecutionReceipt MakeExecutionReceipt(CaptureRunRootLayout layout)
        {
            CaptureRunInitializationDocumentSet documents = CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeMarkerWriter());
            return executionCoordinator.Execute(batch);
        }

        private static CaptureFrameWorkToken MakeToken(long frameId)
        {
            return new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, frameId);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = source.IndexOf(methodName + "(", StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), "Method signature not found: " + methodName);

            int open = source.IndexOf('{', signature);
            Assert.That(open, Is.GreaterThanOrEqualTo(0), "Method body not found: " + methodName);

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                {
                    depth++;
                }
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(open, i - open + 1);
                    }
                }
            }

            Assert.Fail("Method body not terminated: " + methodName);
            return null;
        }

        // ---- Fakes ----

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            public FakeHandle(string lockPath, bool isCreated)
            {
                LockPath = lockPath;
                IsCreated = isCreated;
            }

            public string LockPath { get; }

            public bool IsCreated { get; }

            public string Tag { get; set; }

            public void Dispose()
            {
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
            {
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeMarkerWriter : ICaptureRunMarkerAtomicWriter
        {
            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }

        private sealed class FakeAppender : INvencRunChunkAppender
        {
            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                return NvencRunChunkAppendOutcome.Appended;
            }
        }

        private sealed class FakeFinalizer : INvencRunChunkFinalizer
        {
            internal int CallCount;

            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
                CallCount++;
                CaptureArtifactDescriptor descriptor = NvencRunChunkArtifactDescriptorFactory.Create(
                    operation.ArtifactId, operation.AccumulatedByteLength, Hash64);
                return NvencRunChunkFinalizationReceipt.Create(this, operation, descriptor);
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

        private sealed class Harness
        {
            internal NvencCaptureProcessState State = new NvencCaptureProcessState();
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal FakeAppender Writer = new FakeAppender();
            internal NvencRunChunkSink Sink;
            internal FakeFinalizer Finalizer = new FakeFinalizer();
            internal NvencRunChunkFinalizationCoordinator Coordinator;
            internal NvencRunChunkContext Context;

            internal Harness(string artifactId = "chunk/0")
            {
                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
                Coordinator = new NvencRunChunkFinalizationCoordinator(Finalizer);
                Context = new NvencRunChunkContext(MakeIssue(), Sink, Coordinator, artifactId);
            }

            internal void AcceptAndAppend(long frameId, int length, byte seed)
            {
                Assert.That(Context.TryRecordAcceptedFrame(frameId), Is.True);
                Append(frameId, length, seed);
            }

            internal void Append(long frameId, int length, byte seed)
            {
                CaptureFrameWorkToken token = MakeToken(frameId);
                Assert.That(Buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
                Assert.That(Buffer.TryCopyCompletedOutput(write, default, new PatternSource(length, seed), out _),
                    Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
                Assert.That(Buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease lease), Is.True);
                Assert.That(Sink.TryAppend(token, lease, out _), Is.True);
            }
        }
    }
}
