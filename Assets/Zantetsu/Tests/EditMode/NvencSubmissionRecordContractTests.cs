using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 Submission Queue record and its
    /// surface ownership correlation. Uses a tiny render target fixture; no
    /// real GPU completion, NVENC, worker, or native resource is used.
    /// </summary>
    public class NvencSubmissionRecordContractTests
    {
        [Test]
        public void DefaultRecord_IsInvalid()
        {
            NvencSubmissionRecord record = default;

            Assert.That(record.WorkToken.IsValid, Is.False);
            Assert.That(record.SubmitToOutputCredit.IsValid, Is.False);
            Assert.That(record.FrameCompletionCredit.IsValid, Is.False);
            Assert.That(record.Surface, Is.Null);

            using (Harness h = Harness.Create())
            {
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.False);
            }
        }

        [Test]
        public void Create_ValidAndForwardsAll()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                    h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                    h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool);

                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.True);
                Assert.That(record.WorkToken.IdenticalTo(h.Token), Is.True);
                Assert.That(record.WorkToken.CaptureFrameId, Is.EqualTo(7));
                Assert.That(record.WorkSlot.SlotIndex, Is.EqualTo(h.WorkLease.SlotIndex));
                Assert.That(record.WorkSlot.Generation, Is.EqualTo(h.WorkLease.Generation));
                Assert.That(record.SampleSlot.SlotIndex, Is.EqualTo(h.SampleLease.SlotIndex));
                Assert.That(record.SampleSlot.Generation, Is.EqualTo(h.SampleLease.Generation));
                Assert.That(record.SyncSlot.SlotIndex, Is.EqualTo(h.SyncLease.SlotIndex));
                Assert.That(record.SyncSlot.Generation, Is.EqualTo(h.SyncLease.Generation));
                Assert.That(record.SubmitToOutputCredit.SlotIndex, Is.EqualTo(h.SubmitToOutputCredit.SlotIndex));
                Assert.That(record.SubmitToOutputCredit.Generation, Is.EqualTo(h.SubmitToOutputCredit.Generation));
                Assert.That(record.FrameCompletionCredit.SlotIndex, Is.EqualTo(h.FrameCompletionCredit.SlotIndex));
                Assert.That(record.FrameCompletionCredit.Generation, Is.EqualTo(h.FrameCompletionCredit.Generation));
                Assert.That(ReferenceEquals(record.Surface, h.Surface), Is.True);
            }
        }

        [Test]
        public void Create_RejectsNullSurface()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentNullException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, null,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
            }
        }

        [Test]
        public void Create_RejectsNullPools()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentNullException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        null, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
                Assert.Throws<ArgumentNullException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, null, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
                Assert.Throws<ArgumentNullException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, null, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
                Assert.Throws<ArgumentNullException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, null, h.FrameCompletionCreditPool));
                Assert.Throws<ArgumentNullException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, null));
            }
        }

        [Test]
        public void Create_RejectsEmptyOwner()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        Guid.Empty, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
            }
        }

        [Test]
        public void Create_RejectsInvalidTokenAndLeases()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, default, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, default, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, default, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, h.SampleLease, default,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        default, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, default, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
            }
        }

        [Test]
        public void Create_RejectsCallerOwnedSurface()
        {
            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            using (Harness h = Harness.Create())
            {
                Assert.That(renderPool.TryRent(out CaptureFrameRenderTargetLease rtLease), Is.True);
                CaptureSurfaceLease callerOwned = new CaptureSurfaceLease(renderPool, rtLease);

                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, callerOwned,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));

                callerOwned.Dispose();
            }
        }

        [Test]
        public void Create_RejectsForeignOwner()
        {
            using (Harness h = Harness.Create())
            {
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        Guid.NewGuid(), h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
            }
        }

        [Test]
        public void Create_RejectsDifferentTokenAndLookalike()
        {
            using (Harness h = Harness.Create())
            {
                CaptureFrameWorkToken different = new CaptureFrameWorkToken(
                    h.Owner, h.WorkLease.SlotIndex, h.WorkLease.Generation, 1, 99);
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, different, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));

                CaptureFrameWorkToken lookalike = new CaptureFrameWorkToken(
                    h.Owner, h.WorkLease.SlotIndex, h.WorkLease.Generation, 2, 7);
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, lookalike, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
            }
        }

        [Test]
        public void Create_RejectsTokenSlotGenerationMismatch()
        {
            using (Harness h = Harness.Create())
            {
                Assert.That(h.WorkPool.TryRent(out NvencCaptureWorkSlotLease otherWork), Is.True);
                Assert.That(otherWork.SlotIndex, Is.Not.EqualTo(h.WorkLease.SlotIndex));

                // Token for a different work slot.
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, otherWork, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));

                // Token for the same slot but a different generation.
                CaptureFrameWorkToken differentGeneration = new CaptureFrameWorkToken(
                    h.Owner, h.WorkLease.SlotIndex, h.WorkLease.Generation + 1, 1, 7);
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, differentGeneration, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
            }
        }

        [Test]
        public void Create_RejectsForeignWorkPool()
        {
            using (Harness h = Harness.Create())
            {
                NvencCaptureWorkSlotPool foreignWork = new NvencCaptureWorkSlotPool(h.State);
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        foreignWork, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
            }
        }

        [Test]
        public void Create_RejectsForeignSamplePool()
        {
            using (Harness h = Harness.Create())
            {
                NvencEncodeSampleSlotPool foreignSample = new NvencEncodeSampleSlotPool(h.State);
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, foreignSample, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
            }
        }

        [Test]
        public void Create_RejectsForeignSyncPool()
        {
            using (Harness h = Harness.Create())
            {
                NvencGpuConversionSyncPool foreignSync = new NvencGpuConversionSyncPool(h.State);
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, foreignSync, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool));
            }
        }

        [Test]
        public void Create_RejectsForeignSubmitToOutputCreditPool()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputCreditPool foreignSubmitToOutput = new NvencSubmitToOutputCreditPool(h.State);
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, foreignSubmitToOutput, h.FrameCompletionCreditPool));
            }
        }

        [Test]
        public void Create_RejectsForeignFrameCompletionCreditPool()
        {
            using (Harness h = Harness.Create())
            {
                NvencFrameCompletionCreditPool foreignFrameCompletion = new NvencFrameCompletionCreditPool(h.State);
                Assert.Throws<ArgumentException>(() =>
                    NvencSubmissionRecord.Create(
                        h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                        h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                        h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, foreignFrameCompletion));
            }
        }

        [Test]
        public void IsValidFor_FalseAfterWorkLeaseReturned()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                    h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                    h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.True);

                Assert.That(h.WorkPool.TryReturn(h.WorkLease), Is.True);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.False);
            }
        }

        [Test]
        public void IsValidFor_FalseAfterSampleLeaseReturned()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                    h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                    h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.True);

                Assert.That(h.SamplePool.TryReturn(h.SampleLease), Is.True);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.False);
            }
        }

        [Test]
        public void IsValidFor_FalseAfterSyncLeaseReturned()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                    h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                    h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.True);

                Assert.That(h.SyncPool.TryReturn(h.SyncLease), Is.True);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.False);
            }
        }

        [Test]
        public void IsValidFor_FalseAfterSubmitToOutputCreditReturned()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                    h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                    h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.True);

                Assert.That(h.SubmitToOutputCreditPool.TryReturn(h.SubmitToOutputCredit), Is.True);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.False);
            }
        }

        [Test]
        public void IsValidFor_FalseAfterFrameCompletionCreditReturned()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                    h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                    h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.True);

                Assert.That(h.FrameCompletionCreditPool.TryReturn(h.FrameCompletionCredit), Is.True);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.False);
            }
        }

        [Test]
        public void IsValidFor_StaleAfterWorkSlotRerent()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                    h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                    h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.True);

                Assert.That(h.WorkPool.TryReturn(h.WorkLease), Is.True);
                Assert.That(h.WorkPool.TryRent(out NvencCaptureWorkSlotLease rerented), Is.True);
                Assert.That(rerented.SlotIndex, Is.EqualTo(h.WorkLease.SlotIndex));
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.False);
            }
        }

        [Test]
        public void IsValidFor_StaleAfterSyncSlotRerent()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                    h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                    h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.True);

                Assert.That(h.SyncPool.TryReturn(h.SyncLease), Is.True);
                Assert.That(h.SyncPool.TryRent(out NvencGpuConversionSyncLease rerented), Is.True);
                Assert.That(rerented.SlotIndex, Is.EqualTo(h.SyncLease.SlotIndex));
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.False);
            }
        }

        [Test]
        public void IsValidFor_StaleAfterSubmitToOutputCreditRerent()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                    h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                    h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.True);

                Assert.That(h.SubmitToOutputCreditPool.TryReturn(h.SubmitToOutputCredit), Is.True);
                Assert.That(h.SubmitToOutputCreditPool.TryRent(out NvencSubmitToOutputCreditLease rerented), Is.True);
                Assert.That(rerented.SlotIndex, Is.EqualTo(h.SubmitToOutputCredit.SlotIndex));
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.False);
            }
        }

        [Test]
        public void IsValidFor_StaleAfterFrameCompletionCreditRerent()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                    h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                    h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.True);

                Assert.That(h.FrameCompletionCreditPool.TryReturn(h.FrameCompletionCredit), Is.True);
                Assert.That(h.FrameCompletionCreditPool.TryRent(out NvencFrameCompletionCreditLease rerented), Is.True);
                Assert.That(rerented.SlotIndex, Is.EqualTo(h.FrameCompletionCredit.SlotIndex));
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.False);
            }
        }

        [Test]
        public void IsValidFor_FalseAfterSurfaceReleased()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                    h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                    h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.True);

                h.Surface.ReleaseFromBackend(h.Owner, h.Token);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.False);
            }
        }

        [Test]
        public void WorkSampleSyncCreditSlotIndexesMayDiffer()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);

            Assert.That(workPool.TryRent(out NvencCaptureWorkSlotLease workLease0), Is.True);
            Assert.That(workPool.TryRent(out NvencCaptureWorkSlotLease workLease1), Is.True);
            Assert.That(samplePool.TryRent(out NvencEncodeSampleSlotLease sampleLease0), Is.True);
            Assert.That(samplePool.TryRent(out NvencEncodeSampleSlotLease sampleLease1), Is.True);
            Assert.That(samplePool.TryRent(out NvencEncodeSampleSlotLease sampleLease2), Is.True);
            Assert.That(syncPool.TryRent(out NvencGpuConversionSyncLease syncLease), Is.True);
            Assert.That(submitToOutputPool.TryRent(out NvencSubmitToOutputCreditLease submitToOutputCredit), Is.True);
            Assert.That(frameCompletionPool.TryRent(out NvencFrameCompletionCreditLease frameCompletionCredit), Is.True);

            Assert.That(workLease1.SlotIndex, Is.Not.EqualTo(sampleLease2.SlotIndex));
            Assert.That(workLease1.SlotIndex, Is.Not.EqualTo(syncLease.SlotIndex));
            Assert.That(sampleLease2.SlotIndex, Is.Not.EqualTo(syncLease.SlotIndex));
            Assert.That(workLease1.SlotIndex, Is.Not.EqualTo(submitToOutputCredit.SlotIndex));
            Assert.That(workLease1.SlotIndex, Is.Not.EqualTo(frameCompletionCredit.SlotIndex));

            Guid owner = Guid.NewGuid();
            CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                owner, workLease1.SlotIndex, workLease1.Generation, 1, 7);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(1))
            {
                CaptureSurfaceLease surface = MakeTransferredSurface(renderPool, owner, token);
                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    owner, token, workLease1, sampleLease2, syncLease,
                    submitToOutputCredit, frameCompletionCredit, surface,
                    workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool);

                Assert.That(record.IsValidFor(owner, workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool), Is.True);

                surface.ReleaseFromBackend(owner, token);
            }
        }

        [Test]
        public void FactoryAndValidation_DoNotReleaseOrTransferSurface()
        {
            using (Harness h = Harness.Create())
            {
                Assert.That(h.Surface.IsBackendOwned, Is.True);
                Assert.That(h.RenderPool.RentedCount, Is.EqualTo(1));

                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                    h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                    h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.True);
                Assert.That(record.IsValidFor(h.Owner, h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool), Is.True);

                Assert.That(h.Surface.IsBackendOwned, Is.True);
                Assert.That(h.Surface.IsCreated, Is.True);
                Assert.That(h.RenderPool.RentedCount, Is.EqualTo(1));
            }
        }

        [Test]
        public void Queue_FifoOrder()
        {
            const int count = 4;
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool workPool = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samplePool = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool syncPool = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitToOutputPool = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletionPool = new NvencFrameCompletionCreditPool(state);

            using (CaptureFrameRenderTargetPool renderPool = MakeRenderPool(count))
            {
                NvencSubmissionRecord[] records = new NvencSubmissionRecord[count];
                CaptureSurfaceLease[] surfaces = new CaptureSurfaceLease[count];
                Guid owner = Guid.NewGuid();

                for (int i = 0; i < count; i++)
                {
                    Assert.That(workPool.TryRent(out NvencCaptureWorkSlotLease workLease), Is.True);
                    Assert.That(samplePool.TryRent(out NvencEncodeSampleSlotLease sampleLease), Is.True);
                    Assert.That(syncPool.TryRent(out NvencGpuConversionSyncLease syncLease), Is.True);
                    Assert.That(submitToOutputPool.TryRent(out NvencSubmitToOutputCreditLease submitToOutputCredit), Is.True);
                    Assert.That(frameCompletionPool.TryRent(out NvencFrameCompletionCreditLease frameCompletionCredit), Is.True);
                    CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                        owner, workLease.SlotIndex, workLease.Generation, 1, i + 1);
                    surfaces[i] = MakeTransferredSurface(renderPool, owner, token);
                    records[i] = NvencSubmissionRecord.Create(
                        owner, token, workLease, sampleLease, syncLease,
                        submitToOutputCredit, frameCompletionCredit, surfaces[i],
                        workPool, samplePool, syncPool, submitToOutputPool, frameCompletionPool);
                }

                NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
                for (int i = 0; i < count; i++)
                {
                    Assert.That(queue.TryEnqueue(records[i]), Is.True);
                }

                for (int i = 0; i < count; i++)
                {
                    Assert.That(queue.TryDequeue(out NvencSubmissionRecord record), Is.True);
                    Assert.That(record.WorkToken.CaptureFrameId, Is.EqualTo(i + 1));
                    Assert.That(record.WorkToken.IdenticalTo(records[i].WorkToken), Is.True);
                }

                for (int i = 0; i < count; i++)
                {
                    surfaces[i].ReleaseFromBackend(owner, records[i].WorkToken);
                }
            }
        }

        [Test]
        public void Dequeue_RemovesSurfaceReferenceFromBackingSlot()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmissionRecord record = NvencSubmissionRecord.Create(
                    h.Owner, h.Token, h.WorkLease, h.SampleLease, h.SyncLease,
                    h.SubmitToOutputCredit, h.FrameCompletionCredit, h.Surface,
                    h.WorkPool, h.SamplePool, h.SyncPool, h.SubmitToOutputCreditPool, h.FrameCompletionCreditPool);

                NvencFixedSpscQueue<NvencSubmissionRecord> queue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
                Assert.That(queue.TryEnqueue(record), Is.True);
                Assert.That(queue.TryDequeue(out NvencSubmissionRecord dequeued), Is.True);
                Assert.That(ReferenceEquals(dequeued.Surface, h.Surface), Is.True);

                NvencSubmissionRecord[] items = ReadQueueItems(queue);
                Assert.That(items[0].Surface, Is.Null);
                Assert.That(items[0].WorkToken.IsValid, Is.False);
                Assert.That(items[0].WorkSlot.IsValid, Is.False);
                Assert.That(items[0].SampleSlot.IsValid, Is.False);
                Assert.That(items[0].SyncSlot.IsValid, Is.False);
                Assert.That(items[0].SubmitToOutputCredit.IsValid, Is.False);
                Assert.That(items[0].FrameCompletionCredit.IsValid, Is.False);
            }
        }

        [Test]
        public void Record_IsReadonlyValueType_NotDisposable()
        {
            Type type = typeof(NvencSubmissionRecord);

            Assert.That(type.IsValueType, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void Record_HoldsOnlySevenFields_NoCollectionsOrHandles()
        {
            Type type = typeof(NvencSubmissionRecord);
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(fields.Length, Is.EqualTo(7));

            Type[] expected =
            {
                typeof(CaptureFrameWorkToken),
                typeof(NvencCaptureWorkSlotLease),
                typeof(NvencEncodeSampleSlotLease),
                typeof(NvencGpuConversionSyncLease),
                typeof(NvencSubmitToOutputCreditLease),
                typeof(NvencFrameCompletionCreditLease),
                typeof(CaptureSurfaceLease),
            };

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(expected, Does.Contain(field.FieldType));
            }

            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencSubmissionRecord.cs"));
            Assert.That(source, Does.Not.Contain("IntPtr"));
            Assert.That(source, Does.Not.Contain("SafeHandle"));
            Assert.That(source, Does.Not.Contain("DllImport"));
            Assert.That(source, Does.Not.Contain("NvEnc"));
            Assert.That(source, Does.Not.Contain("RenderTexture"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("new Thread"));
            Assert.That(source, Does.Not.Contain("ThreadPool"));
            Assert.That(source, Does.Not.Contain("Thread.Sleep"));
            Assert.That(source, Does.Not.Contain("Task"));
            Assert.That(source, Does.Not.Contain("lock ("));
            Assert.That(source, Does.Not.Contain("Monitor"));
            Assert.That(source, Does.Not.Contain("File."));
            Assert.That(source, Does.Not.Contain("Directory."));
            Assert.That(source, Does.Not.Contain("new []"));
            Assert.That(source, Does.Not.Contain("new List"));
            Assert.That(source, Does.Not.Contain("new Dictionary"));
            Assert.That(source, Does.Not.Contain("new byte["));
        }

        private static CaptureFrameRenderTargetPool MakeRenderPool(int capacity)
        {
            return new CaptureFrameRenderTargetPool(
                capacity, CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(9, new CaptureImageRect(0, 0, 2, 2)));
        }

        private static CaptureSurfaceLease MakeTransferredSurface(
            CaptureFrameRenderTargetPool pool, Guid owner, CaptureFrameWorkToken token)
        {
            Assert.That(pool.TryRent(out CaptureFrameRenderTargetLease rtLease), Is.True);
            CaptureSurfaceLease surface = new CaptureSurfaceLease(pool, rtLease);
            surface.TransferToBackend(owner, token);
            return surface;
        }

        private static NvencSubmissionRecord[] ReadQueueItems(NvencFixedSpscQueue<NvencSubmissionRecord> queue)
        {
            FieldInfo field = typeof(NvencFixedSpscQueue<NvencSubmissionRecord>).GetField(
                "_items", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "Missing field _items.");
            return (NvencSubmissionRecord[])field.GetValue(queue);
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        private sealed class Harness : IDisposable
        {
            internal NvencCaptureProcessState State { get; }
            internal NvencCaptureWorkSlotPool WorkPool { get; }
            internal NvencEncodeSampleSlotPool SamplePool { get; }
            internal NvencGpuConversionSyncPool SyncPool { get; }
            internal NvencSubmitToOutputCreditPool SubmitToOutputCreditPool { get; }
            internal NvencFrameCompletionCreditPool FrameCompletionCreditPool { get; }
            internal NvencCaptureWorkSlotLease WorkLease { get; }
            internal NvencEncodeSampleSlotLease SampleLease { get; }
            internal NvencGpuConversionSyncLease SyncLease { get; }
            internal NvencSubmitToOutputCreditLease SubmitToOutputCredit { get; }
            internal NvencFrameCompletionCreditLease FrameCompletionCredit { get; }
            internal CaptureFrameRenderTargetPool RenderPool { get; }
            internal CaptureSurfaceLease Surface { get; }
            internal Guid Owner { get; }
            internal CaptureFrameWorkToken Token { get; }

            private Harness()
            {
                State = new NvencCaptureProcessState();
                WorkPool = new NvencCaptureWorkSlotPool(State);
                SamplePool = new NvencEncodeSampleSlotPool(State);
                SyncPool = new NvencGpuConversionSyncPool(State);
                SubmitToOutputCreditPool = new NvencSubmitToOutputCreditPool(State);
                FrameCompletionCreditPool = new NvencFrameCompletionCreditPool(State);
                Assert.That(WorkPool.TryRent(out NvencCaptureWorkSlotLease workLease), Is.True);
                Assert.That(SamplePool.TryRent(out NvencEncodeSampleSlotLease sampleLease), Is.True);
                Assert.That(SyncPool.TryRent(out NvencGpuConversionSyncLease syncLease), Is.True);
                Assert.That(SubmitToOutputCreditPool.TryRent(out NvencSubmitToOutputCreditLease submitToOutputCredit), Is.True);
                Assert.That(FrameCompletionCreditPool.TryRent(out NvencFrameCompletionCreditLease frameCompletionCredit), Is.True);
                WorkLease = workLease;
                SampleLease = sampleLease;
                SyncLease = syncLease;
                SubmitToOutputCredit = submitToOutputCredit;
                FrameCompletionCredit = frameCompletionCredit;
                Owner = Guid.NewGuid();
                Token = new CaptureFrameWorkToken(Owner, WorkLease.SlotIndex, WorkLease.Generation, 1, 7);
                RenderPool = MakeRenderPool(1);
                Surface = MakeTransferredSurface(RenderPool, Owner, Token);
            }

            internal static Harness Create()
            {
                return new Harness();
            }

            public void Dispose()
            {
                if (Surface != null && Surface.IsCreated)
                {
                    if (Surface.IsBackendOwned)
                    {
                        Surface.ReleaseFromBackend(Owner, Token);
                    }
                    else
                    {
                        Surface.Dispose();
                    }
                }

                RenderPool?.Dispose();
            }
        }
    }
}
