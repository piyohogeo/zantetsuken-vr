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
    /// Contract tests for the Phase 0.11 single Run chunk local registry slot:
    /// the exact once registration of a finalized chunk result and the
    /// exclusive commit / pre-commit-discard transitions.
    /// </summary>
    public class NvencRunLocalRegistrySlotContractTests
    {
        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Test]
        public void Initial_Empty_NoEntry()
        {
            Harness h = new Harness();

            Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Empty));
            Assert.That(h.Slot.HasRegisteredEntry, Is.False);
            Assert.That(h.Slot.Context, Is.SameAs(h.Context));
            Assert.That(h.Slot.TestRunId, Is.EqualTo(h.Context.TestRunId));
            Assert.That(h.Slot.TryGetEntry(out _, out _, out _), Is.False);
        }

        [Test]
        public void Register_FinalizedExactResult_Success()
        {
            Harness h = new Harness();

            Assert.That(h.Slot.TryRegister(h.Context, h.Result), Is.True);
            Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Registered));
            Assert.That(h.Slot.HasRegisteredEntry, Is.True);

            Assert.That(h.Slot.TryGetEntry(
                out NvencChunkFinalizationResult result,
                out CaptureArtifactDescriptor descriptor,
                out CaptureArtifactFrameRelation relation), Is.True);
            Assert.That(ReferenceEquals(result, h.Result), Is.True);
            Assert.That(ReferenceEquals(descriptor, h.Result.Descriptor), Is.True);
            Assert.That(ReferenceEquals(relation, h.Result.FrameRelation), Is.True);
        }

        [Test]
        public void Register_OpenOrAbandonedContext_Rejected()
        {
            Harness open = new Harness(finalize: false);
            Assert.That(open.Slot.TryRegister(open.Context, null), Is.False);
            Assert.That(open.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Empty));

            Harness abandoned = new Harness(finalize: false);
            abandoned.AcceptAndAppend(1, 64, Seed);
            Assert.That(abandoned.Context.TryFreezeAcceptedFrames(out _), Is.True);
            Assert.That(abandoned.Context.TryAbandon(), Is.True);
            Assert.That(abandoned.Slot.TryRegister(abandoned.Context, null), Is.False);
            Assert.That(abandoned.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Empty));
        }

        [Test]
        public void Register_ForeignContextOrResult_Rejected()
        {
            Harness a = new Harness();
            Harness b = new Harness();

            // Foreign context.
            Assert.That(a.Slot.TryRegister(b.Context, b.Result), Is.False);
            Assert.That(a.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Empty));

            // Foreign / same-value result instance.
            Assert.That(a.Slot.TryRegister(a.Context, b.Result), Is.False);
            Assert.That(a.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Empty));
        }

        [Test]
        public void Register_NullOrInvalidResult_NoPartialRegistration()
        {
            Harness h = new Harness();

            // Null result.
            Assert.That(h.Slot.TryRegister(h.Context, null), Is.False);
            Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Empty));
            Assert.That(h.Slot.HasRegisteredEntry, Is.False);

            // Invalid result: corrupt its receipt descriptor to null.
            SetField(h.Result.Receipt, "_descriptor", null);
            Assert.That(h.Slot.TryRegister(h.Context, h.Result), Is.False);
            Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Empty));
            Assert.That(h.Slot.TryGetEntry(out _, out _, out _), Is.False);
        }

        [Test]
        public void Register_RelationMismatchWithAcceptedList_Rejected()
        {
            Harness h = new Harness(frameCount: 2);

            // Corrupt the context's accepted list so the result's relation no
            // longer matches it in order.
            long[] accepted = (long[])GetField(h.Context, "_acceptedFrameIds");
            accepted[0] = 99;

            Assert.That(h.Slot.TryRegister(h.Context, h.Result), Is.False);
            Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Empty));
        }

        [Test]
        public void Register_ExactlyOnce()
        {
            Harness h = new Harness();

            Assert.That(h.Slot.TryRegister(h.Context, h.Result), Is.True);
            Assert.That(h.Slot.TryRegister(h.Context, h.Result), Is.False);
            Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Registered));
        }

        [Test]
        public void Commit_RegisteredToCommitted_EntryRetained()
        {
            Harness h = new Harness();
            h.Slot.TryRegister(h.Context, h.Result);

            Assert.That(h.Slot.TryCommit(h.Context, h.Result), Is.True);
            Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));

            Assert.That(h.Slot.TryGetEntry(out NvencChunkFinalizationResult result, out _, out _), Is.True);
            Assert.That(ReferenceEquals(result, h.Result), Is.True);
        }

        [Test]
        public void Commit_DoubleForeignCorrupted_Rejected()
        {
            Harness h = new Harness();
            h.Slot.TryRegister(h.Context, h.Result);

            // Double commit.
            Assert.That(h.Slot.TryCommit(h.Context, h.Result), Is.True);
            Assert.That(h.Slot.TryCommit(h.Context, h.Result), Is.False);

            // Foreign context / result from a fresh registered slot.
            Harness f = new Harness();
            f.Slot.TryRegister(f.Context, f.Result);
            Harness other = new Harness("chunk/other");
            Assert.That(f.Slot.TryCommit(other.Context, f.Result), Is.False);
            Assert.That(f.Slot.TryCommit(f.Context, other.Result), Is.False);

            // Corrupted entry: null the held descriptor.
            SetField(f.Slot, "_descriptor", null);
            Assert.That(f.Slot.TryCommit(f.Context, f.Result), Is.False);
        }

        [Test]
        public void Discard_RegisteredToEmpty_EntryCleared()
        {
            Harness h = new Harness();
            h.Slot.TryRegister(h.Context, h.Result);

            Assert.That(h.Slot.TryDiscardRegistered(h.Context, h.Result), Is.True);
            Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Empty));
            Assert.That(h.Slot.HasRegisteredEntry, Is.False);
            Assert.That(h.Slot.TryGetEntry(out _, out _, out _), Is.False);
        }

        [Test]
        public void Discard_ThenReRegisterCommitReDiscard_Rejected()
        {
            Harness h = new Harness();
            h.Slot.TryRegister(h.Context, h.Result);
            Assert.That(h.Slot.TryDiscardRegistered(h.Context, h.Result), Is.True);

            // The latch survives discard: no re-registration, commit, or
            // re-discard.
            Assert.That(h.Slot.TryRegister(h.Context, h.Result), Is.False);
            Assert.That(h.Slot.TryCommit(h.Context, h.Result), Is.False);
            Assert.That(h.Slot.TryDiscardRegistered(h.Context, h.Result), Is.False);
            Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Empty));
        }

        [Test]
        public void Discard_FromCommitted_Rejected()
        {
            Harness h = new Harness();
            h.Slot.TryRegister(h.Context, h.Result);
            h.Slot.TryCommit(h.Context, h.Result);

            Assert.That(h.Slot.TryDiscardRegistered(h.Context, h.Result), Is.False);
            Assert.That(h.Slot.State, Is.EqualTo(NvencRunLocalRegistrySlotState.Committed));
        }

        [Test]
        public void TryGetEntry_AfterTamper_FailsClosed()
        {
            // Nulled held result.
            Harness h = new Harness();
            h.Slot.TryRegister(h.Context, h.Result);
            SetField(h.Slot, "_result", null);
            Assert.That(h.Slot.TryGetEntry(out _, out _, out _), Is.False);

            // Nulled held descriptor.
            Harness h2 = new Harness();
            h2.Slot.TryRegister(h2.Context, h2.Result);
            SetField(h2.Slot, "_descriptor", null);
            Assert.That(h2.Slot.TryGetEntry(out _, out _, out _), Is.False);

            // Corrupted context reference must fail closed, not throw.
            Harness h3 = new Harness();
            h3.Slot.TryRegister(h3.Context, h3.Result);
            SetField(h3.Slot, "_context", null);
            Assert.That(h3.Slot.TryGetEntry(out _, out _, out _), Is.False);
        }

        [Test]
        public void Slot_NotDisposable_NoLeaseThreadQueueFilesystem()
        {
            Type type = typeof(NvencRunLocalRegistrySlot);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(CaptureRunInitializationSessionOwnershipLease)),
                    "slot must not hold the ownership lease.");
            }

            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencRunLocalRegistrySlot.cs"));
            string[] forbidden =
            {
                "File.", "Directory.", "FileStream", "System.IO", "new Thread", "ThreadPool",
                "Task", "new Queue", "Semaphore", "Mutex",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "slot source must not contain: " + word);
            }
        }

        [Test]
        public void Slot_NoTokenProofNonceReceipt()
        {
            Type type = typeof(NvencRunLocalRegistrySlot);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                string typeName = field.FieldType.Name;
                Assert.That(typeName, Does.Not.Contain("Token"), field.Name);
                Assert.That(typeName, Does.Not.Contain("Proof"), field.Name);
                Assert.That(typeName, Does.Not.Contain("Nonce"), field.Name);
                Assert.That(typeName, Does.Not.Contain("Receipt"), field.Name);
                Assert.That(typeName, Does.Not.Contain("Validation"), field.Name);
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

        private static object GetField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            return field.GetValue(target);
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
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

        private sealed class FakeWriter : INvencRunChunkAppender, INvencRunChunkFinalizer
        {
            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
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
            internal FakeWriter Writer = new FakeWriter();
            internal NvencRunChunkSink Sink;
            internal NvencRunChunkFinalizationCoordinator Coordinator;
            internal NvencRunChunkContext Context;
            internal NvencRunLocalRegistrySlot Slot;
            internal NvencChunkFinalizationResult Result;

            internal Harness(string artifactId = "chunk/0", bool finalize = true, int frameCount = 1)
            {
                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
                Coordinator = new NvencRunChunkFinalizationCoordinator(Writer);
                Context = new NvencRunChunkContext(MakeIssue(), Sink, Coordinator, artifactId);
                Slot = new NvencRunLocalRegistrySlot(Context);

                if (finalize)
                {
                    for (long id = 1; id <= frameCount; id++)
                    {
                        AcceptAndAppend(id, 64, Seed);
                    }

                    Assert.That(Context.TryFreezeAcceptedFrames(out _), Is.True);
                    Assert.That(Context.TryFinalize(out Result), Is.True);
                }
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
