using System;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 submission admission linearization
    /// boundary. Uses tiny render target fixtures; no real GPU completion,
    /// NVENC, worker, or native resource is used.
    /// </summary>
    public class NvencSubmissionAdmissionCoordinatorContractTests
    {
        private const int WatchdogTimeoutMs = 5000;
        private const string InitId = "0123456789abcdef0123456789abcdef";

        [Test]
        public void Accept_ValidAndTransfersOwnership()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();
            NvencSubmissionAdmissionCoordinator coordinator = new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool, queue, new RecordingConversionCommandIssuer(), MakeContext(state), owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                CaptureFrameWorkToken token = default;
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(7), surface, out token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Accepted));
                    Assert.That(token.IsValid, Is.True);
                    Assert.That(surface.IsBackendOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(1));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(1));
                    Assert.That(syncPool.OccupiedCount, Is.EqualTo(1));
                    Assert.That(submitToOutputPool.OccupiedCount, Is.EqualTo(1));
                    Assert.That(frameCompletionPool.OccupiedCount, Is.EqualTo(1));

                    Assert.That(coordinator.TryDequeue(out NvencSubmissionRecord record), Is.True);
                    Assert.That(record.WorkToken.IdenticalTo(token), Is.True);
                    Assert.That(ReferenceEquals(record.Surface, surface), Is.True);
                    Assert.That(record.SyncSlot.IsValid, Is.True);
                    Assert.That(record.SubmitToOutputCredit.IsValid, Is.True);
                    Assert.That(record.FrameCompletionCredit.IsValid, Is.True);
                    Assert.That(record.IsValidFor(owner, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool), Is.True);
                }
                finally
                {
                    ReleaseSurface(surface, owner, token);
                }
            }
        }

        [Test]
        public void Accept_RegistersFrameIdIntoContext()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinatorWithContext(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencGpuConversionSyncPool syncPool,
                out NvencSubmitToOutputCreditPool submitToOutputPool,
                out NvencFrameCompletionCreditPool frameCompletionPool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner,
                out NvencRunChunkContext context);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                CaptureFrameWorkToken token = default;
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(7), surface, out token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Accepted));
                    Assert.That(context.AcceptedFrameCount, Is.EqualTo(1));
                    Assert.That(context.TryGetAcceptedFrameId(0, out long acceptedId), Is.True);
                    Assert.That(acceptedId, Is.EqualTo(7));
                }
                finally
                {
                    ReleaseSurface(surface, owner, token);
                }
            }
        }

        [Test]
        public void Accept_Multiple_RegisteredInAcceptedOrder()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinatorWithContext(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencGpuConversionSyncPool syncPool,
                out NvencSubmitToOutputCreditPool submitToOutputPool,
                out NvencFrameCompletionCreditPool frameCompletionPool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner,
                out NvencRunChunkContext context);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(3))
            {
                CaptureSurfaceLease[] surfaces = new CaptureSurfaceLease[3];
                CaptureFrameWorkToken[] tokens = new CaptureFrameWorkToken[3];
                for (int i = 0; i < 3; i++)
                {
                    surfaces[i] = MakeCallerOwnedSurface(renderPool);
                }

                try
                {
                    for (int i = 0; i < 3; i++)
                    {
                        Assert.That(
                            coordinator.TryAccept(MakeFrame(i + 1), surfaces[i], out tokens[i]),
                            Is.EqualTo(CaptureSubmitStatus.Accepted));
                    }

                    Assert.That(context.AcceptedFrameCount, Is.EqualTo(3));
                    for (int i = 0; i < 3; i++)
                    {
                        Assert.That(context.TryGetAcceptedFrameId(i, out long acceptedId), Is.True);
                        Assert.That(acceptedId, Is.EqualTo(i + 1));
                    }
                }
                finally
                {
                    for (int i = 0; i < 3; i++)
                    {
                        ReleaseSurface(surfaces[i], owner, tokens[i]);
                    }
                }
            }
        }

        [Test]
        public void Accept_Backpressured_LeavesContextUnchanged()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinatorWithContext(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencGpuConversionSyncPool syncPool,
                out NvencSubmitToOutputCreditPool submitToOutputPool,
                out NvencFrameCompletionCreditPool frameCompletionPool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner,
                out NvencRunChunkContext context);

            for (int i = 0; i < 8; i++)
            {
                Assert.That(workPool.TryRent(out NvencCaptureWorkSlotLease workLease), Is.True);
            }

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(context.AcceptedFrameCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_NotAccepting_LeavesContextUnchanged()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinatorWithContext(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencGpuConversionSyncPool syncPool,
                out NvencSubmitToOutputCreditPool submitToOutputPool,
                out NvencFrameCompletionCreditPool frameCompletionPool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner,
                out NvencRunChunkContext context);

            Assert.That(state.TryBeginDrain(), Is.True);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.NotAccepting));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(context.AcceptedFrameCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_ForeignTestRunId_ThrowsBeforeSideEffect()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinatorWithContext(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencGpuConversionSyncPool syncPool,
                out NvencSubmitToOutputCreditPool submitToOutputPool,
                out NvencFrameCompletionCreditPool frameCompletionPool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner,
                out NvencRunChunkContext context);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    Assert.Throws<ArgumentException>(
                        () => coordinator.TryAccept(MakeFrame(7, testRunId: 999), surface, out CaptureFrameWorkToken token));

                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(coordinator.TryDequeue(out _), Is.False);
                    Assert.That(context.AcceptedFrameCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_DuplicateFrame_RejectedBeforeSideEffect()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinatorWithContext(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencGpuConversionSyncPool syncPool,
                out NvencSubmitToOutputCreditPool submitToOutputPool,
                out NvencFrameCompletionCreditPool frameCompletionPool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner,
                out NvencRunChunkContext context);

            Assert.That(context.TryRecordAcceptedFrame(5), Is.True);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(5), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.NotAccepting));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(coordinator.TryDequeue(out _), Is.False);
                    Assert.That(context.AcceptedFrameCount, Is.EqualTo(1));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_BackwardFrame_RejectedBeforeSideEffect()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinatorWithContext(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencGpuConversionSyncPool syncPool,
                out NvencSubmitToOutputCreditPool submitToOutputPool,
                out NvencFrameCompletionCreditPool frameCompletionPool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner,
                out NvencRunChunkContext context);

            Assert.That(context.TryRecordAcceptedFrame(5), Is.True);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(3), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.NotAccepting));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(coordinator.TryDequeue(out _), Is.False);
                    Assert.That(context.AcceptedFrameCount, Is.EqualTo(1));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_OverCapacity_RejectedBeforeSideEffect()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinatorWithContext(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencGpuConversionSyncPool syncPool,
                out NvencSubmitToOutputCreditPool submitToOutputPool,
                out NvencFrameCompletionCreditPool frameCompletionPool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner,
                out NvencRunChunkContext context);

            for (long id = 1; id <= 120; id++)
            {
                Assert.That(context.TryRecordAcceptedFrame(id), Is.True);
            }

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(121), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.NotAccepting));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(coordinator.TryDequeue(out _), Is.False);
                    Assert.That(context.AcceptedFrameCount, Is.EqualTo(120));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_EnqueueFailure_DoesNotRegisterFrame()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinatorWithContext(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencGpuConversionSyncPool syncPool,
                out NvencSubmitToOutputCreditPool submitToOutputPool,
                out NvencFrameCompletionCreditPool frameCompletionPool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner,
                out NvencRunChunkContext context);

            FieldInfo writeField = typeof(NvencFixedSpscQueue<NvencSubmissionRecord>).GetField(
                "_writePosition", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(writeField, Is.Not.Null, "Missing field _writePosition.");
            writeField.SetValue(queue, -1L);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    Assert.Throws<IndexOutOfRangeException>(
                        () => coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token));

                    // The conversion was already issued, so nothing is
                    // rolled back and no frame is recorded.
                    Assert.That(context.AcceptedFrameCount, Is.EqualTo(0));
                    Assert.That(state.IsPoisoned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(1));
                    Assert.That(surface.IsBackendOwned, Is.True);
                }
                finally
                {
                    ReleaseSurface(surface, owner, new CaptureFrameWorkToken(owner, 0, 1, 1, 1));
                }
            }
        }

        [Test]
        public void Constructor_NullContext_Throws()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();

            Assert.Throws<ArgumentNullException>(() => new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool, queue, new RecordingConversionCommandIssuer(), null, owner));
        }

        [Test]
        public void Constructor_ContextProcessStateMismatch_Throws()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();

            NvencRunChunkContext foreignContext = MakeContext(new NvencCaptureProcessState());

            Assert.Throws<ArgumentException>(() => new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool, queue, new RecordingConversionCommandIssuer(), foreignContext, owner));
        }

        [Test]
        public void Source_ContextRegistrationPrecedesEndAdmission()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencSubmissionAdmissionCoordinator.cs"));

            int register = source.IndexOf("_context.TryRecordAcceptedFrame(", StringComparison.Ordinal);
            int endAdmission = source.IndexOf("EndAdmission()", StringComparison.Ordinal);

            Assert.That(register, Is.GreaterThanOrEqualTo(0), "Missing context registration call.");
            Assert.That(endAdmission, Is.GreaterThanOrEqualTo(0), "Missing EndAdmission call.");
            Assert.That(register, Is.LessThan(endAdmission),
                "Accepted-frame registration must complete before EndAdmission releases the admission gate.");
        }

        [Test]
        public void Source_PostEnqueueRegistrationFailure_PoisonsWithoutRollback()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencSubmissionAdmissionCoordinator.cs"));

            int register = source.IndexOf("_context.TryRecordAcceptedFrame(", StringComparison.Ordinal);
            Assert.That(register, Is.GreaterThanOrEqualTo(0), "Missing context registration call.");

            int acceptedStatus = source.IndexOf("status = CaptureSubmitStatus.Accepted;", register, StringComparison.Ordinal);
            Assert.That(acceptedStatus, Is.GreaterThanOrEqualTo(0), "Missing accepted status assignment after registration.");

            string registrationBranch = source.Substring(register, acceptedStatus - register);

            Assert.That(registrationBranch, Does.Contain("TryPoison()"));
            Assert.That(registrationBranch, Does.Contain("throw new InvalidOperationException"));
            Assert.That(registrationBranch, Does.Not.Contain("RollbackAfterTransferAndThrow("));
            Assert.That(registrationBranch, Does.Not.Contain("RollbackReservations("));
            Assert.That(registrationBranch, Does.Not.Contain("ReleaseFromBackend"));
        }

        [Test]
        public void Accept_EighthSucceeds_NinthBackpressured()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();
            NvencSubmissionAdmissionCoordinator coordinator = new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool, queue, new RecordingConversionCommandIssuer(), MakeContext(state), owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(9))
            {
                CaptureSurfaceLease[] surfaces = new CaptureSurfaceLease[9];
                CaptureFrameWorkToken[] tokens = new CaptureFrameWorkToken[9];
                for (int i = 0; i < 9; i++)
                {
                    surfaces[i] = MakeCallerOwnedSurface(renderPool);
                }

                try
                {
                    for (int i = 0; i < 8; i++)
                    {
                        CaptureSubmitStatus accepted = coordinator.TryAccept(MakeFrame(i + 1), surfaces[i], out tokens[i]);
                        Assert.That(accepted, Is.EqualTo(CaptureSubmitStatus.Accepted));
                    }

                    Assert.That(workPool.OccupiedCount, Is.EqualTo(8));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(8));
                    Assert.That(syncPool.OccupiedCount, Is.EqualTo(8));
                    Assert.That(submitToOutputPool.OccupiedCount, Is.EqualTo(8));
                    Assert.That(frameCompletionPool.OccupiedCount, Is.EqualTo(8));

                    CaptureSubmitStatus ninth = coordinator.TryAccept(MakeFrame(9), surfaces[8], out CaptureFrameWorkToken ninthToken);

                    Assert.That(ninth, Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(ninthToken.IsValid, Is.False);
                    Assert.That(surfaces[8].IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(8));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(8));
                    Assert.That(syncPool.OccupiedCount, Is.EqualTo(8));
                    Assert.That(submitToOutputPool.OccupiedCount, Is.EqualTo(8));
                    Assert.That(frameCompletionPool.OccupiedCount, Is.EqualTo(8));
                }
                finally
                {
                    for (int i = 0; i < 9; i++)
                    {
                        ReleaseSurface(surfaces[i], owner, tokens[i]);
                    }
                }
            }
        }

        [Test]
        public void Accept_QueueFull_Backpressured()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            for (int i = 0; i < 8; i++)
            {
                Assert.That(queue.TryEnqueue(default), Is.True);
            }

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_WorkPoolFull_Backpressured()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            for (int i = 0; i < 8; i++)
            {
                Assert.That(workPool.TryRent(out NvencCaptureWorkSlotLease workLease), Is.True);
            }

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_SamplePoolFull_RollsBackWorkSlot()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            for (int i = 0; i < 8; i++)
            {
                Assert.That(samplePool.TryRent(out NvencEncodeSampleSlotLease sampleLease), Is.True);
            }

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_SyncPoolFull_RollsBackSampleAndWork()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();
            NvencSubmissionAdmissionCoordinator coordinator = new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool, queue, new RecordingConversionCommandIssuer(), MakeContext(state), owner);

            for (int i = 0; i < 8; i++)
            {
                Assert.That(syncPool.TryRent(out NvencGpuConversionSyncLease syncLease), Is.True);
            }

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(syncPool.OccupiedCount, Is.EqualTo(8));
                    Assert.That(submitToOutputPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(frameCompletionPool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_SubmitToOutputCreditPoolFull_RollsBackAllPrior()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();
            NvencSubmissionAdmissionCoordinator coordinator = new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool, queue, new RecordingConversionCommandIssuer(), MakeContext(state), owner);

            for (int i = 0; i < 8; i++)
            {
                Assert.That(submitToOutputPool.TryRent(out NvencSubmitToOutputCreditLease credit), Is.True);
            }

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(syncPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(submitToOutputPool.OccupiedCount, Is.EqualTo(8));
                    Assert.That(frameCompletionPool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_FrameCompletionCreditPoolFull_RollsBackAllPrior()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();
            NvencSubmissionAdmissionCoordinator coordinator = new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool, queue, new RecordingConversionCommandIssuer(), MakeContext(state), owner);

            for (int i = 0; i < 8; i++)
            {
                Assert.That(frameCompletionPool.TryRent(out NvencFrameCompletionCreditLease credit), Is.True);
            }

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(syncPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(submitToOutputPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(frameCompletionPool.OccupiedCount, Is.EqualTo(8));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_CapacityFailure_RollsBackWhileGateHeld_ThenReleasesGate()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();
            NvencSubmissionAdmissionCoordinator coordinator = new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool, queue, new RecordingConversionCommandIssuer(), MakeContext(state), owner);

            // Pre-fill the sync pool so the admission rents Work and Sample, then
            // fails on Sync and must roll both back before releasing the gate.
            for (int i = 0; i < 8; i++)
            {
                Assert.That(syncPool.TryRent(out NvencGpuConversionSyncLease syncLease), Is.True);
            }

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);

                    // The partial reservations were rolled back, the pre-filled
                    // pool is untouched, and no transition interleaved with the
                    // rollback: the process is still accepting.
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(syncPool.OccupiedCount, Is.EqualTo(8));
                    Assert.That(submitToOutputPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(frameCompletionPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(state.IsAccepting, Is.True);

                    // The gate is released only after the rollback, so it is free
                    // for the next admission.
                    FieldInfo gateField = typeof(NvencCaptureProcessState).GetField(
                        "_admissionGate", BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.That(gateField, Is.Not.Null, "Missing field _admissionGate.");
                    object gate = gateField.GetValue(state);
                    bool entered = Monitor.TryEnter(gate);
                    Assert.That(entered, Is.True, "The admission gate must be released after TryAccept returns.");
                    if (entered)
                    {
                        Monitor.Exit(gate);
                    }
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Source_CapacityRollbackPrecedesEndAdmission()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencSubmissionAdmissionCoordinator.cs"));

            int rollback = source.IndexOf("RollbackReservations(", StringComparison.Ordinal);
            int endAdmission = source.IndexOf("EndAdmission()", StringComparison.Ordinal);
            int finallyKeyword = source.IndexOf("finally", StringComparison.Ordinal);

            Assert.That(rollback, Is.GreaterThanOrEqualTo(0), "Missing capacity rollback call.");
            Assert.That(endAdmission, Is.GreaterThanOrEqualTo(0), "Missing EndAdmission call.");
            Assert.That(finallyKeyword, Is.GreaterThanOrEqualTo(0), "Missing finally block.");

            // The capacity-exhaustion rollback runs inside the try, strictly
            // before the finally block that releases the admission gate via
            // EndAdmission. Combined with AdmissionGuard_SerializesWithTransition
            // (process-state fixture), this guarantees Drain/Poison serialize
            // with the rollback on the same gate and establish only afterwards.
            Assert.That(rollback, Is.LessThan(finallyKeyword),
                "Capacity rollback must precede the finally block.");
            Assert.That(finallyKeyword, Is.LessThan(endAdmission),
                "EndAdmission must run inside the finally block.");
        }

        [Test]
        public void Accept_EnqueueFailure_AfterIssue_HoldsEverythingAndPoisons()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();
            NvencSubmissionAdmissionCoordinator coordinator = new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool, queue, new RecordingConversionCommandIssuer(), MakeContext(state), owner);

            // A negative write position passes the capacity predicate but makes
            // the backing index negative, so the post-transfer enqueue throws.
            FieldInfo writeField = typeof(NvencFixedSpscQueue<NvencSubmissionRecord>).GetField(
                "_writePosition", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(writeField, Is.Not.Null, "Missing field _writePosition.");
            writeField.SetValue(queue, -1L);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    Assert.Throws<IndexOutOfRangeException>(
                        () => coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token));

                    // The conversion is issued by the time the enqueue runs,
                    // so the render callback may be reading the source: nothing
                    // is given back, and the process is poisoned instead.
                    Assert.That(state.IsPoisoned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(1));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(1));
                    Assert.That(syncPool.OccupiedCount, Is.EqualTo(1));
                    Assert.That(submitToOutputPool.OccupiedCount, Is.EqualTo(1));
                    Assert.That(frameCompletionPool.OccupiedCount, Is.EqualTo(1));
                    Assert.That(surface.IsBackendOwned, Is.True);
                }
                finally
                {
                    ReleaseSurface(surface, owner, new CaptureFrameWorkToken(owner, 0, 1, 1, 1));
                }
            }
        }

        [Test]
        public void Accept_IssuerRefusal_CleanupFailureAggregates()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();
            NvencSubmissionAdmissionCoordinator coordinator = new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool, queue,
                new RecordingConversionCommandIssuer(false, null, null), MakeContext(state), owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                FieldInfo poolField = typeof(CaptureSurfaceLease).GetField(
                    "_pool", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(poolField, Is.Not.Null);
                object originalPool = poolField.GetValue(surface);

                try
                {
                    // The issuer refuses, so the rollback runs - and the
                    // surface release inside it now throws, so the refusal is
                    // aggregated with the cleanup failure.
                    poolField.SetValue(surface, null);

                    AggregateException aggregate = Assert.Throws<AggregateException>(
                        () => coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token));

                    Assert.That(aggregate.InnerExceptions.Count, Is.EqualTo(2));
                    Assert.That(aggregate.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());
                    Assert.That(aggregate.InnerExceptions[1], Is.TypeOf<NullReferenceException>());

                    // The reservation rollback completed before the surface
                    // release failed.
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(syncPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(submitToOutputPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(frameCompletionPool.OccupiedCount, Is.EqualTo(0));

                    // Ownership state after the failed cleanup: the surface was
                    // never released, so it remains backend-owned.
                    Assert.That(surface.IsBackendOwned, Is.True);
                    Assert.That(surface.IsCreated, Is.True);
                }
                finally
                {
                    poolField.SetValue(surface, originalPool);
                    ReleaseSurface(surface, owner, new CaptureFrameWorkToken(owner, 0, 1, 1, 1));
                }
            }
        }

        [Test]
        public void Accept_AdmissionGuardUnavailable_RollsBackAllThreeLeases()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();
            NvencSubmissionAdmissionCoordinator coordinator = new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool, queue, new RecordingConversionCommandIssuer(), MakeContext(state), owner);

            bool guardAcquired = false;
            using (ManualResetEventSlim guardHeld = new ManualResetEventSlim(false))
            using (ManualResetEventSlim release = new ManualResetEventSlim(false))
            {
                Thread holder = new Thread(() =>
                {
                    guardAcquired = state.TryBeginAdmission();
                    guardHeld.Set();
                    release.Wait(WatchdogTimeoutMs);
                    if (guardAcquired)
                    {
                        state.EndAdmission();
                    }
                });
                holder.IsBackground = true;
                bool holderJoined = false;
                holder.Start();
                try
                {
                    Assert.That(guardHeld.Wait(WatchdogTimeoutMs), Is.True, "Guard holder did not acquire the gate in time.");
                    Assert.That(guardAcquired, Is.True);
                    using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
                    {
                        CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                        try
                        {
                            CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                            Assert.That(status, Is.EqualTo(CaptureSubmitStatus.NotAccepting));
                            Assert.That(token.IsValid, Is.False);
                            Assert.That(surface.IsCallerOwned, Is.True);
                            Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                            Assert.That(samplePool.OccupiedCount, Is.EqualTo(0));
                            Assert.That(syncPool.OccupiedCount, Is.EqualTo(0));
                        }
                        finally
                        {
                            surface.Dispose();
                        }
                    }
                }
                finally
                {
                    release.Set();
                    holderJoined = holder.Join(WatchdogTimeoutMs);
                }

                Assert.That(holderJoined, Is.True, "Guard holder did not finish in time.");
            }
        }

        [Test]
        public void Accept_InitialDraining_NotAccepting()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();
            Assert.That(state.TryBeginDrain(), Is.True);
            NvencSubmissionAdmissionCoordinator coordinator = new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool, queue, new RecordingConversionCommandIssuer(), MakeContext(state), owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.NotAccepting));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_InitialPoisoned_NotAccepting()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();
            Assert.That(state.TryPoison(), Is.True);
            NvencSubmissionAdmissionCoordinator coordinator = new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool, queue, new RecordingConversionCommandIssuer(), MakeContext(state), owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.NotAccepting));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_RejectsNullFrame()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    Assert.Throws<ArgumentNullException>(() => coordinator.TryAccept(null, surface, out CaptureFrameWorkToken token));
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Accept_RejectsNullSurface()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            Assert.Throws<ArgumentNullException>(() => coordinator.TryAccept(MakeFrame(1), null, out CaptureFrameWorkToken token));
            Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void Accept_RejectsNonCallerOwnedSurface()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                CaptureFrameWorkToken token = new CaptureFrameWorkToken(owner, 0, 1, 3, 7);
                surface.TransferToBackend(owner, token);
                try
                {
                    Assert.Throws<ArgumentException>(() => coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken outToken));
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.ReleaseFromBackend(owner, token);
                }
            }
        }

        [Test]
        public void Dequeue_FifoOrder_NoSorting()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            long[] frameIds = { 1, 2, 3, 4 };

            // The accepted sequence is strictly increasing (the Run chunk
            // context rejects a backward id), so FIFO is verified by asserting
            // the dequeue order equals the accept order.
            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(4))
            {
                CaptureSurfaceLease[] surfaces = new CaptureSurfaceLease[4];
                CaptureFrameWorkToken[] tokens = new CaptureFrameWorkToken[4];
                for (int i = 0; i < 4; i++)
                {
                    surfaces[i] = MakeCallerOwnedSurface(renderPool);
                }

                try
                {
                    for (int i = 0; i < 4; i++)
                    {
                        Assert.That(coordinator.TryAccept(MakeFrame(frameIds[i]), surfaces[i], out tokens[i]), Is.EqualTo(CaptureSubmitStatus.Accepted));
                    }

                    for (int i = 0; i < 4; i++)
                    {
                        Assert.That(coordinator.TryDequeue(out NvencSubmissionRecord record), Is.True);
                        Assert.That(record.WorkToken.CaptureFrameId, Is.EqualTo(frameIds[i]));
                    }
                }
                finally
                {
                    for (int i = 0; i < 4; i++)
                    {
                        ReleaseSurface(surfaces[i], owner, tokens[i]);
                    }
                }
            }
        }

        [Test]
        public void Dequeue_EmptyFalseDefault()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            Assert.That(coordinator.TryDequeue(out NvencSubmissionRecord record), Is.False);
            Assert.That(record.Surface, Is.Null);
            Assert.That(record.WorkToken.IsValid, Is.False);
        }

        [Test]
        public void Accept_QueuePositionLimit_BackpressuredWithoutTransfer()
        {
            NvencSubmissionAdmissionCoordinator coordinator = MakeCoordinator(
                out NvencCaptureProcessState state,
                out NvencCaptureWorkSlotPool workPool,
                out NvencEncodeSampleSlotPool samplePool,
                out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                out Guid owner);

            FieldInfo writeField = typeof(NvencFixedSpscQueue<NvencSubmissionRecord>).GetField("_writePosition", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo readField = typeof(NvencFixedSpscQueue<NvencSubmissionRecord>).GetField("_readPosition", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(writeField, Is.Not.Null);
            Assert.That(readField, Is.Not.Null);
            writeField.SetValue(queue, long.MaxValue);
            readField.SetValue(queue, long.MaxValue - 5);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    CaptureSubmitStatus status = coordinator.TryAccept(MakeFrame(1), surface, out CaptureFrameWorkToken token);

                    Assert.That(status, Is.EqualTo(CaptureSubmitStatus.Backpressured));
                    Assert.That(token.IsValid, Is.False);
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        [Test]
        public void Coordinator_NoWaitNoAllocationNoGuidGeneration()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencSubmissionAdmissionCoordinator.cs"));

            Assert.That(source, Does.Not.Contain("Thread.Sleep"));
            Assert.That(source, Does.Not.Contain("new Thread"));
            Assert.That(source, Does.Not.Contain("ThreadPool"));
            Assert.That(source, Does.Not.Contain("Task"));
            Assert.That(source, Does.Not.Contain("lock ("));
            Assert.That(source, Does.Not.Contain("Monitor"));
            Assert.That(source, Does.Not.Contain("Interlocked"));
            Assert.That(source, Does.Not.Contain("SpinWait"));
            Assert.That(source, Does.Not.Contain("WaitHandle"));
            Assert.That(source, Does.Not.Contain("ManualResetEvent"));
            Assert.That(source, Does.Not.Contain("AutoResetEvent"));
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("DllImport"));
            Assert.That(source, Does.Not.Contain("NvEnc"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("new []"));
            Assert.That(source, Does.Not.Contain("new List"));
            Assert.That(source, Does.Not.Contain("new Dictionary"));
            Assert.That(source, Does.Not.Contain("new byte["));
            Assert.That(source, Does.Not.Contain("Guid.NewGuid"));

            Type type = typeof(NvencSubmissionAdmissionCoordinator);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
        }

        private static NvencSubmissionAdmissionCoordinator MakeCoordinator(
            out NvencCaptureProcessState state,
            out NvencCaptureWorkSlotPool workPool,
            out NvencEncodeSampleSlotPool samplePool,
            out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
            out Guid owner)
        {
            state = new NvencCaptureProcessState();
            workPool = new NvencCaptureWorkSlotPool(state);
            samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            owner = Guid.NewGuid();
            return new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool, queue, new RecordingConversionCommandIssuer(), MakeContext(state), owner);
        }

        private static NvencSubmissionAdmissionCoordinator MakeCoordinatorWithContext(
            out NvencCaptureProcessState state,
            out NvencCaptureWorkSlotPool workPool,
            out NvencEncodeSampleSlotPool samplePool,
            out NvencGpuConversionSyncPool syncPool,
            out NvencSubmitToOutputCreditPool submitToOutputPool,
            out NvencFrameCompletionCreditPool frameCompletionPool,
            out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
            out Guid owner,
            out NvencRunChunkContext context)
        {
            state = new NvencCaptureProcessState();
            workPool = new NvencCaptureWorkSlotPool(state);
            samplePool = new NvencEncodeSampleSlotPool(state);
            syncPool = new NvencGpuConversionSyncPool(state);
            submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            owner = Guid.NewGuid();
            context = MakeContext(state);
            return new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool, queue, new RecordingConversionCommandIssuer(), context, owner);
        }

        private static NvencSubmissionAdmissionCoordinator MakeCoordinatorWithIssuer(
            RecordingConversionCommandIssuer issuer,
            out NvencCaptureProcessState state,
            out NvencCaptureWorkSlotPool workPool,
            out NvencEncodeSampleSlotPool samplePool,
            out NvencGpuConversionSyncPool syncPool,
            out NvencSubmitToOutputCreditPool submitToOutputPool,
            out NvencFrameCompletionCreditPool frameCompletionPool,
            out NvencFixedSpscQueue<NvencSubmissionRecord> queue,
            out Guid owner,
            out NvencRunChunkContext context)
        {
            state = new NvencCaptureProcessState();
            workPool = new NvencCaptureWorkSlotPool(state);
            samplePool = new NvencEncodeSampleSlotPool(state);
            syncPool = new NvencGpuConversionSyncPool(state);
            submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            frameCompletionPool = new NvencFrameCompletionCreditPool(state);
            queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
            owner = Guid.NewGuid();
            context = MakeContext(state);
            return new NvencSubmissionAdmissionCoordinator(
                state, workPool, samplePool, syncPool, submitToOutputPool,
                frameCompletionPool, queue, issuer, context, owner);
        }

        /// <summary>
        /// An accepted submission has its conversion issued exactly once,
        /// against the very slots it reserved, and before the record can be
        /// dequeued by anyone.
        /// </summary>
        [Test]
        public void Accept_IssuesTheConversionForTheReservedSlotsBeforeEnqueue()
        {
            NvencFixedSpscQueue<NvencSubmissionRecord> observedQueue =
                new NvencFixedSpscQueue<NvencSubmissionRecord>();
            RecordingConversionCommandIssuer issuer =
                new RecordingConversionCommandIssuer(true, null, observedQueue);

            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool completionPool =
                new NvencFrameCompletionCreditPool(state);
            Guid owner = Guid.NewGuid();

            NvencSubmissionAdmissionCoordinator coordinator =
                new NvencSubmissionAdmissionCoordinator(
                    state, workPool, samplePool, syncPool, submitPool, completionPool,
                    observedQueue, issuer, MakeContext(state), owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    Assert.That(
                        coordinator.TryAccept(MakeFrame(7), surface, out CaptureFrameWorkToken _),
                        Is.EqualTo(CaptureSubmitStatus.Accepted));

                    Assert.That(issuer.IssueCount, Is.EqualTo(1));

                    // The record the issuer saw is the one that was reserved.
                    Assert.That(
                        observedQueue.TryDequeue(out NvencSubmissionRecord queued), Is.True);
                    Assert.That(issuer.SourceSlotIndex, Is.EqualTo(queued.Surface.SlotIndex));
                    Assert.That(issuer.SampleSlotIndex, Is.EqualTo(queued.SampleSlot.SlotIndex));
                    Assert.That(issuer.SyncSlotIndex, Is.EqualTo(queued.SyncSlot.SlotIndex));
                    Assert.That(issuer.SyncGeneration, Is.EqualTo(queued.SyncSlot.Generation));
                    Assert.That(issuer.WorkTokenFrameId, Is.EqualTo(7L));

                    // And it was asked before anything could be dequeued.
                    Assert.That(issuer.QueueCountWhenIssued, Is.EqualTo(0));
                }
                finally
                {
                    ReleaseSurface(
                        surface, owner, new CaptureFrameWorkToken(owner, 0, 1, 1, 7));
                }
            }
        }

        /// <summary>
        /// A frame that is not admitted never reaches the issuer, and its
        /// surface stays the caller's.
        /// </summary>
        [Test]
        public void Accept_WhenNotAdmitted_NeverIssuesAndLeavesTheSurfaceWithTheCaller()
        {
            RecordingConversionCommandIssuer issuer = new RecordingConversionCommandIssuer();

            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool completionPool =
                new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue =
                new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();

            NvencSubmissionAdmissionCoordinator coordinator =
                new NvencSubmissionAdmissionCoordinator(
                    state, workPool, samplePool, syncPool, submitPool, completionPool,
                    queue, issuer, MakeContext(state), owner);

            // Not accepting at all.
            state.TryBeginDrain();

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    Assert.That(
                        coordinator.TryAccept(MakeFrame(7), surface, out CaptureFrameWorkToken _),
                        Is.EqualTo(CaptureSubmitStatus.NotAccepting));

                    Assert.That(issuer.IssueCount, Is.EqualTo(0));
                    Assert.That(surface.IsCallerOwned, Is.True);
                    Assert.That(queue.Count, Is.EqualTo(0));
                }
                finally
                {
                    surface.Dispose();
                }
            }
        }

        /// <summary>
        /// An issuer that refuses means no command exists, so everything the
        /// admission took goes back - and the process is not poisoned, because
        /// nothing is unknown.
        /// </summary>
        [Test]
        public void Accept_WhenIssuerRefuses_ReturnsEverythingAndDoesNotPoison()
        {
            RecordingConversionCommandIssuer issuer =
                new RecordingConversionCommandIssuer(false, null, null);

            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool completionPool =
                new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue =
                new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();
            NvencRunChunkContext context = MakeContext(state);

            NvencSubmissionAdmissionCoordinator coordinator =
                new NvencSubmissionAdmissionCoordinator(
                    state, workPool, samplePool, syncPool, submitPool, completionPool,
                    queue, issuer, context, owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    Assert.Throws<InvalidOperationException>(
                        () => coordinator.TryAccept(
                            MakeFrame(7), surface, out CaptureFrameWorkToken _));

                    Assert.That(state.IsPoisoned, Is.False);
                    Assert.That(queue.Count, Is.EqualTo(0));
                    Assert.That(context.AcceptedFrameCount, Is.EqualTo(0));

                    // Everything the admission took is back.
                    Assert.That(workPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(samplePool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(syncPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(submitPool.OccupiedCount, Is.EqualTo(0));
                    Assert.That(completionPool.OccupiedCount, Is.EqualTo(0));

                    // The transferred surface was released back to its pool.
                    Assert.That(surface.IsCreated, Is.False);
                }
                finally
                {
                    ReleaseSurface(
                        surface, owner, new CaptureFrameWorkToken(owner, 0, 1, 1, 7));
                }
            }
        }

        /// <summary>
        /// An issuer that throws leaves it unknown whether the GPU is reading
        /// the source, so the process poisons, nothing is returned, and the
        /// same exception comes back out.
        /// </summary>
        [Test]
        public void Accept_WhenIssuerThrows_PoisonsAndHoldsEverything()
        {
            InvalidOperationException sentinel =
                new InvalidOperationException("the conversion command is in an unknown state");
            RecordingConversionCommandIssuer issuer =
                new RecordingConversionCommandIssuer(true, sentinel, null);

            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool completionPool =
                new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue =
                new NvencFixedSpscQueue<NvencSubmissionRecord>();
            Guid owner = Guid.NewGuid();
            NvencRunChunkContext context = MakeContext(state);

            NvencSubmissionAdmissionCoordinator coordinator =
                new NvencSubmissionAdmissionCoordinator(
                    state, workPool, samplePool, syncPool, submitPool, completionPool,
                    queue, issuer, context, owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    InvalidOperationException thrown =
                        Assert.Throws<InvalidOperationException>(
                            () => coordinator.TryAccept(
                                MakeFrame(7), surface, out CaptureFrameWorkToken _));

                    Assert.That(thrown, Is.SameAs(sentinel));
                    Assert.That(state.IsPoisoned, Is.True);
                    Assert.That(queue.Count, Is.EqualTo(0));
                    Assert.That(context.AcceptedFrameCount, Is.EqualTo(0));

                    // Nothing was given back: the surface is still the
                    // backend's and the reservations are still out.
                    Assert.That(surface.IsBackendOwned, Is.True);
                }
                finally
                {
                    ReleaseSurface(
                        surface, owner, new CaptureFrameWorkToken(owner, 0, 1, 1, 7));
                }
            }
        }

        /// <summary>
        /// Once the conversion is issued the render callback may be reading the
        /// source, so an enqueue that fails afterwards poisons and keeps
        /// everything rather than rolling back underneath the GPU.
        /// </summary>
        [Test]
        public void Accept_WhenEnqueueFailsAfterIssue_PoisonsAndRollsBackNothing()
        {
            NvencFixedSpscQueue<NvencSubmissionRecord> queue =
                new NvencFixedSpscQueue<NvencSubmissionRecord>();

            // The issuer fills the queue while it issues, so the enqueue that
            // follows the issue is the one that fails - which is the only way
            // this path is reached.
            RecordingConversionCommandIssuer issuer =
                new RecordingConversionCommandIssuer(true, null, queue, true);

            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool completionPool =
                new NvencFrameCompletionCreditPool(state);
            Guid owner = Guid.NewGuid();
            NvencRunChunkContext context = MakeContext(state);

            NvencSubmissionAdmissionCoordinator coordinator =
                new NvencSubmissionAdmissionCoordinator(
                    state, workPool, samplePool, syncPool, submitPool, completionPool,
                    queue, issuer, context, owner);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeCallerOwnedSurface(renderPool);
                try
                {
                    Assert.Throws<InvalidOperationException>(
                        () => coordinator.TryAccept(
                            MakeFrame(7), surface, out CaptureFrameWorkToken _));

                    Assert.That(issuer.IssueCount, Is.EqualTo(1));
                    Assert.That(state.IsPoisoned, Is.True);
                    Assert.That(context.AcceptedFrameCount, Is.EqualTo(0));

                    // Nothing was rolled back: the surface is still the
                    // backend's, and every reservation is still out.
                    Assert.That(surface.IsBackendOwned, Is.True);
                    Assert.That(workPool.TryRent(out NvencCaptureWorkSlotLease _), Is.False);
                    Assert.That(samplePool.TryRent(out NvencEncodeSampleSlotLease _), Is.False);
                    Assert.That(syncPool.TryRent(out NvencGpuConversionSyncLease _), Is.False);
                }
                finally
                {
                    ReleaseSurface(
                        surface, owner, new CaptureFrameWorkToken(owner, 0, 1, 1, 7));
                }
            }
        }

        [Test]
        public void Constructor_NullIssuer_Throws()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool completionPool =
                new NvencFrameCompletionCreditPool(state);
            NvencFixedSpscQueue<NvencSubmissionRecord> queue =
                new NvencFixedSpscQueue<NvencSubmissionRecord>();

            Assert.Throws<ArgumentNullException>(
                () => new NvencSubmissionAdmissionCoordinator(
                    state, workPool, samplePool, syncPool, submitPool, completionPool,
                    queue, null, MakeContext(state), Guid.NewGuid()));
        }

        /// <summary>
        /// A stand-in for the GPU conversion issuer: it records what it was
        /// asked to issue, and how the queue looked when it was asked.
        /// </summary>
        private sealed class RecordingConversionCommandIssuer
            : INvencGpuConversionCommandIssuer
        {
            private readonly bool _result;
            private readonly Exception _exception;
            private readonly NvencFixedSpscQueue<NvencSubmissionRecord> _queue;

            private readonly bool _fillQueueOnIssue;

            internal RecordingConversionCommandIssuer()
                : this(true, null, null, false)
            {
            }

            internal RecordingConversionCommandIssuer(
                bool result,
                Exception exception,
                NvencFixedSpscQueue<NvencSubmissionRecord> queue)
                : this(result, exception, queue, false)
            {
            }

            internal RecordingConversionCommandIssuer(
                bool result,
                Exception exception,
                NvencFixedSpscQueue<NvencSubmissionRecord> queue,
                bool fillQueueOnIssue)
            {
                _result = result;
                _exception = exception;
                _queue = queue;
                _fillQueueOnIssue = fillQueueOnIssue;
            }

            internal int IssueCount { get; private set; }

            internal int SourceSlotIndex { get; private set; } = -1;

            internal int SampleSlotIndex { get; private set; } = -1;

            internal int SyncSlotIndex { get; private set; } = -1;

            internal long SyncGeneration { get; private set; }

            internal long WorkTokenFrameId { get; private set; }

            internal int QueueCountWhenIssued { get; private set; } = -1;

            public bool TryIssue(in NvencSubmissionRecord record)
            {
                IssueCount++;
                SourceSlotIndex = record.Surface.SlotIndex;
                SampleSlotIndex = record.SampleSlot.SlotIndex;
                SyncSlotIndex = record.SyncSlot.SlotIndex;
                SyncGeneration = record.SyncSlot.Generation;
                WorkTokenFrameId = record.WorkToken.CaptureFrameId;

                if (_queue != null)
                {
                    QueueCountWhenIssued = _queue.Count;

                    if (_fillQueueOnIssue)
                    {
                        while (_queue.CanEnqueue)
                        {
                            _queue.TryEnqueue(default);
                        }
                    }
                }

                if (_exception != null)
                {
                    throw _exception;
                }

                return _result;
            }
        }

        private static NvencRunChunkContext MakeContext(NvencCaptureProcessState state)
        {
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);
            FakeWriter writer = new FakeWriter();
            NvencRunChunkSink sink = new NvencRunChunkSink(state, buffer, writer);
            NvencRunChunkFinalizationCoordinator finalizationCoordinator =
                new NvencRunChunkFinalizationCoordinator(writer);
            return new NvencRunChunkContext(MakeIssue(), sink, finalizationCoordinator, "chunk/0");
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
            return CaptureRunInitializationSession.IssuanceProof.Mint(owner, identity, evidence);
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
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeMarkerWriter());
            return executionCoordinator.Execute(layout, InitId);
        }

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

        private static CaptureFrameEnvelope MakeFrame(long captureFrameId, long testRunId = 1)
        {
            CaptureFrameTraceContext context = new CaptureFrameTraceContext(
                10, 20, 4, 1, captureFrameId, 30, testRunId, 40, 50, 60, 2, 70);
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
                return null;
            }
        }
    }
}
