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
    /// Contract tests for the Phase 0.11 Run chunk accepted-frame snapshot and
    /// its StopAccepting freeze boundary: the exact-reference, fail-closed,
    /// immutable snapshot of the accepted Capture Frame Id sequence that gates
    /// the terminal transition.
    /// </summary>
    public class NvencRunAcceptedFrameSnapshotContractTests
    {
        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        [Test]
        public void Snapshot_SealedImmutable_ExactFields()
        {
            Type type = typeof(NvencRunAcceptedFrameSnapshot);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty);

            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(fields.Length, Is.EqualTo(2));

            Type[] expected =
            {
                typeof(NvencRunChunkContext),
                typeof(int),
            };

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(Array.IndexOf(expected, field.FieldType), Is.GreaterThanOrEqualTo(0),
                    field.Name + " has an unexpected type.");
            }

            // The snapshot duplicates no frame-id array and holds no token,
            // lease, queue, writer, buffer, or process state.
            Type[] forbiddenTypes =
            {
                typeof(long[]), typeof(int[]), typeof(CaptureFrameWorkToken),
                typeof(NvencOwnedAccessUnitLease), typeof(NvencRunChunkSink),
                typeof(NvencOwnedAccessUnitBuffer), typeof(NvencCaptureProcessState),
            };

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(Array.IndexOf(forbiddenTypes, property.PropertyType), Is.LessThan(0),
                    property.Name + " must not expose a forbidden type.");
            }
        }

        [Test]
        public void Snapshot_NullContext_FailClosed()
        {
            NvencRunAcceptedFrameSnapshot snapshot = new NvencRunAcceptedFrameSnapshot(null, 0);

            Assert.That(snapshot.Context, Is.Null);
            Assert.That(snapshot.TestRunId, Is.EqualTo(0));
            Assert.That(snapshot.Count, Is.EqualTo(0));
            Assert.That(snapshot.TryGetCaptureFrameId(0, out long id), Is.False);
            Assert.That(id, Is.EqualTo(0));
        }

        [Test]
        public void Snapshot_ForgedEquivalent_Rejected()
        {
            Harness h = new Harness();
            h.Context.TryRecordAcceptedFrame(1);
            NvencRunAcceptedFrameSnapshot real = h.Freeze();

            // A directly constructed snapshot with the same context and count
            // is not the exact frozen snapshot the context holds and must fail
            // closed.
            NvencRunAcceptedFrameSnapshot forged = new NvencRunAcceptedFrameSnapshot(h.Context, real.Count);
            Assert.That(forged.TryGetCaptureFrameId(0, out long id), Is.False);
            Assert.That(id, Is.EqualTo(0));
        }

        [Test]
        public void Snapshot_BoundContext_CarriesTestRunIdAndCount()
        {
            Harness h = new Harness();
            h.Context.TryRecordAcceptedFrame(7);

            NvencRunAcceptedFrameSnapshot snapshot = h.Freeze();
            Assert.That(ReferenceEquals(snapshot.Context, h.Context), Is.True);
            Assert.That(snapshot.TestRunId, Is.EqualTo(h.Context.TestRunId));
            Assert.That(snapshot.Count, Is.EqualTo(1));
            Assert.That(snapshot.TryGetCaptureFrameId(0, out long id), Is.True);
            Assert.That(id, Is.EqualTo(7));
        }

        [Test]
        public void Snapshot_RangeChecks_FailClosed()
        {
            Harness h = new Harness();
            h.Context.TryRecordAcceptedFrame(1);
            h.Context.TryRecordAcceptedFrame(2);
            NvencRunAcceptedFrameSnapshot snapshot = h.Freeze();

            Assert.That(snapshot.TryGetCaptureFrameId(-1, out _), Is.False);
            Assert.That(snapshot.TryGetCaptureFrameId(2, out _), Is.False);
            Assert.That(snapshot.TryGetCaptureFrameId(0, out long first), Is.True);
            Assert.That(first, Is.EqualTo(1));
            Assert.That(snapshot.TryGetCaptureFrameId(1, out long second), Is.True);
            Assert.That(second, Is.EqualTo(2));
        }

        [Test]
        public void Snapshot_ContextDivergence_FailClosed()
        {
            Harness h = new Harness();
            h.Context.TryRecordAcceptedFrame(1);
            NvencRunAcceptedFrameSnapshot snapshot = h.Freeze();

            // Corrupt the context's accepted count so it diverges from the
            // frozen count; reads must fail closed with a zero id.
            SetField(h.Context, "_acceptedCount", 5);
            Assert.That(snapshot.TryGetCaptureFrameId(0, out long id), Is.False);
            Assert.That(id, Is.EqualTo(0));
        }

        [Test]
        public void Freeze_Idempotent_SameReference()
        {
            Harness h = new Harness();
            h.Context.TryRecordAcceptedFrame(1);
            Assert.That(h.State.TryBeginDrain(), Is.True);

            Assert.That(h.Context.TryFreezeAcceptedFrames(out NvencRunAcceptedFrameSnapshot first), Is.True);
            Assert.That(h.Context.TryFreezeAcceptedFrames(out NvencRunAcceptedFrameSnapshot second), Is.True);
            Assert.That(ReferenceEquals(first, second), Is.True);

            Assert.That(h.Context.TryGetAcceptedFrameSnapshot(out NvencRunAcceptedFrameSnapshot held), Is.True);
            Assert.That(ReferenceEquals(held, first), Is.True);
        }

        [Test]
        public void Freeze_RejectsFurtherRegistration()
        {
            Harness h = new Harness();
            h.Context.TryRecordAcceptedFrame(1);
            NvencRunAcceptedFrameSnapshot snapshot = h.Freeze();

            Assert.That(h.Context.TryRecordAcceptedFrame(2), Is.False);
            Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(1));
            Assert.That(snapshot.Count, Is.EqualTo(1));
        }

        [Test]
        public void Freeze_GatesTerminalTransition()
        {
            Harness h = new Harness();
            h.AcceptAndAppend(1, 64, Seed);

            // Before the freeze the terminal is rejected with no side effect.
            Assert.That(h.Context.TryFinalize(out NvencChunkFinalizationResult result), Is.False);
            Assert.That(result, Is.Null);
            Assert.That(h.Context.TryAbandon(), Is.False);
            Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Open));

            // After the freeze the terminal succeeds.
            h.Freeze();
            Assert.That(h.Context.TryFinalize(out result), Is.True);
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void Admission_RegistersThenFreezeGatesTerminal()
        {
            Harness h = new Harness();
            NvencSubmissionAdmissionCoordinator coordinator = new NvencSubmissionAdmissionCoordinator(
                h.State,
                h.WorkSlots,
                h.SampleSlots,
                h.SyncSlots,
                h.SubmitToOutputCredits,
                h.FrameCompletionCredits,
                h.SubmissionQueue,
                new AlwaysIssuingConversionCommandIssuer(),
                h.Context,
                h.Owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(2))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                CaptureSurfaceLease secondSurface = MakeCallerOwnedSurface(renderPool);
                CaptureFrameWorkToken token = default;
                try
                {
                    // The admission boundary registers the accepted frame id
                    // into the context exactly once.
                    Assert.That(coordinator.TryAccept(MakeFrame(7), surface, out token), Is.EqualTo(CaptureSubmitStatus.Accepted));
                    Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(1));

                    // Freezing after admission pins the accepted sequence and
                    // gates further admission with no side effect.
                    NvencRunAcceptedFrameSnapshot snapshot = h.Freeze();
                    Assert.That(snapshot.Count, Is.EqualTo(1));
                    Assert.That(coordinator.TryAccept(MakeFrame(8), secondSurface, out _), Is.EqualTo(CaptureSubmitStatus.NotAccepting));
                    Assert.That(secondSurface.IsCallerOwned, Is.True);
                    Assert.That(h.Context.AcceptedFrameCount, Is.EqualTo(1));
                }
                finally
                {
                    ReleaseSurface(surface, h.Owner, token);
                    secondSurface.Dispose();
                }
            }
        }

        // ---- Helpers ----

        private static CaptureFrameRenderTargetPool MakeRenderPool(int capacity)
        {
            return new CaptureFrameRenderTargetPool(
                capacity, CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(9, new CaptureImageRect(0, 0, 2, 2)));
        }

        private static CaptureSurfaceLease MakeCallerOwnedSurface(CaptureFrameRenderTargetPool pool)
        {
            Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease rtLease), Is.True);
            return new CaptureSurfaceLease(pool, rtLease);
        }

        private static CaptureFrameEnvelope MakeFrame(long captureFrameId)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                10, 20, 4, 1, captureFrameId, 30, 1, 40, 50, 60, 2, 70);
            CaptureFrameRequest request = new CaptureFrameRequest(
                context, CaptureSource.UnityRenderTexture, CaptureEye.Left,
                new CaptureImageRect(0, 0, 2, 2), 0, CapturePixelFormat.Rgba32);
            CaptureFrameTiming timing = new CaptureFrameTiming(1.0, 0.01, true, 2.0, 3.0, 4);
            CapturePoseSample head = new CapturePoseSample(new Vector3(1, 2, 3), Quaternion.identity);
            return new CaptureFrameEnvelope(
                request, timing, head, CapturePoseSample.Unavailable, CapturePoseSample.Unavailable,
                8, 9, CaptureColorSpace.Srgb, 91, "build-a", "scene-a", 123);
        }

        private static void ReleaseSurface(CaptureSurfaceLease surface, Guid owner, CaptureFrameWorkToken token)
        {
            if (surface == null || !surface.IsCreated)
            {
                return;
            }

            if (surface.IsBackendOwned)
            {
                surface.ReleaseFromBackend(owner, token);
            }
            else
            {
                surface.Dispose();
            }
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

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
            internal int CallCount;

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
                CallCount++;
                CaptureArtifactDescriptor descriptor = NvencRunChunkArtifactDescriptorFactory.Create(
                    operation.ArtifactId, operation.AccumulatedByteLength,
                    "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef");
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

            internal NvencCaptureWorkSlotPool WorkSlots;
            internal NvencEncodeSampleSlotPool SampleSlots;
            internal NvencGpuConversionSyncPool SyncSlots;
            internal NvencSubmitToOutputCreditPool SubmitToOutputCredits;
            internal NvencFrameCompletionCreditPool FrameCompletionCredits;
            internal NvencFixedSpscQueue<NvencSubmissionRecord> SubmissionQueue;
            internal Guid Owner;

            internal Harness()
            {
                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
                Coordinator = new NvencRunChunkFinalizationCoordinator(Writer);
                Context = new NvencRunChunkContext(MakeIssue(), Sink, Coordinator, "chunk/0");

                WorkSlots = new NvencCaptureWorkSlotPool(State);
                SampleSlots = new NvencEncodeSampleSlotPool(State);
                SyncSlots = new NvencGpuConversionSyncPool(State);
                SubmitToOutputCredits = new NvencSubmitToOutputCreditPool(State);
                FrameCompletionCredits = new NvencFrameCompletionCreditPool(State);
                SubmissionQueue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
                Owner = Guid.NewGuid();
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

            internal NvencRunAcceptedFrameSnapshot Freeze()
            {
                Assert.That(State.TryBeginDrain(), Is.True);
                Assert.That(Context.TryFreezeAcceptedFrames(out NvencRunAcceptedFrameSnapshot snapshot), Is.True);
                return snapshot;
            }
        }

        /// <summary>
        /// Stands in for the conversion issuer: this fixture is about accepted
        /// frame snapshots, not about what the GPU is asked to do.
        /// </summary>
        private sealed class AlwaysIssuingConversionCommandIssuer
            : INvencGpuConversionCommandIssuer
        {
            public bool TryIssue(in NvencSubmissionRecord record)
            {
                return true;
            }
        }

    }
}
