using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 NVENC Frame Completion boundary: the
    /// NVENC-specific completion record, the producer-side publish, and the
    /// Main Thread collect that returns the Work Slot and Frame Completion
    /// credit exactly once. No real OS, GPU, NVENC, worker, or native resource
    /// is used.
    /// </summary>
    public class NvencFrameCompletionBoundaryContractTests
    {
        // -------------------------------------------------------------------
        // Record shape
        // -------------------------------------------------------------------

        [Test]
        public void Record_Default_IsInvalid()
        {
            NvencFrameCompletionRecord record = default;

            Assert.That(record.IsValid, Is.False);
            Assert.That(record.Status, Is.EqualTo(CaptureFrameCompletionStatus.None));
            Assert.That(record.Reason, Is.EqualTo(NvencFrameCompletionReason.None));
            Assert.That(record.WorkToken.IsValid, Is.False);
            Assert.That(record.WorkSlot.IsValid, Is.False);
            Assert.That(record.FrameCompletionCredit.IsValid, Is.False);
        }

        [Test]
        public void Record_Succeeded_HasNoneReason()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(1, out NvencCaptureWorkSlotLease workLease, out NvencFrameCompletionCreditLease frameCredit);

            NvencFrameCompletionRecord completion = NvencFrameCompletionRecord.CreateSucceeded(
                record.WorkToken, workLease, frameCredit);

            Assert.That(completion.IsValid, Is.True);
            Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
            Assert.That(completion.Reason, Is.EqualTo(NvencFrameCompletionReason.None));
            Assert.That(completion.WorkToken.IdenticalTo(record.WorkToken), Is.True);
            Assert.That(completion.WorkSlot.SlotIndex, Is.EqualTo(workLease.SlotIndex));
            Assert.That(completion.WorkSlot.Generation, Is.EqualTo(workLease.Generation));
            Assert.That(completion.FrameCompletionCredit.SlotIndex, Is.EqualTo(frameCredit.SlotIndex));
            Assert.That(completion.FrameCompletionCredit.Generation, Is.EqualTo(frameCredit.Generation));
        }

        [Test]
        public void Record_Failed_EachFailureReason()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(1, out NvencCaptureWorkSlotLease workLease, out NvencFrameCompletionCreditLease frameCredit);

            NvencFrameCompletionReason[] reasons =
            {
                NvencFrameCompletionReason.RunChunkControlledFailure,
                NvencFrameCompletionReason.GpuConversionFailed,
                NvencFrameCompletionReason.NvencSubmitFailed,
            };

            foreach (NvencFrameCompletionReason reason in reasons)
            {
                NvencFrameCompletionRecord completion = NvencFrameCompletionRecord.CreateFailed(
                    record.WorkToken, workLease, frameCredit, reason);

                Assert.That(completion.IsValid, Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Failed));
                Assert.That(completion.Reason, Is.EqualTo(reason));
            }
        }

        [Test]
        public void Record_Cancelled_EachCancellationReason()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(1, out NvencCaptureWorkSlotLease workLease, out NvencFrameCompletionCreditLease frameCredit);

            NvencFrameCompletionReason[] reasons =
            {
                NvencFrameCompletionReason.CancelledBeforeSubmit,
                NvencFrameCompletionReason.CancelledAfterRunAbandoned,
                NvencFrameCompletionReason.DrainedBeforeSubmit,
            };

            foreach (NvencFrameCompletionReason reason in reasons)
            {
                NvencFrameCompletionRecord completion = NvencFrameCompletionRecord.CreateCancelled(
                    record.WorkToken, workLease, frameCredit, reason);

                Assert.That(completion.IsValid, Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Cancelled));
                Assert.That(completion.Reason, Is.EqualTo(reason));
            }
        }

        [Test]
        public void Record_Factories_RejectInvalidInputs()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(1, out NvencCaptureWorkSlotLease workLease, out NvencFrameCompletionCreditLease frameCredit);

            Assert.Throws<ArgumentException>(() =>
                NvencFrameCompletionRecord.CreateSucceeded(default, workLease, frameCredit));
            Assert.Throws<ArgumentException>(() =>
                NvencFrameCompletionRecord.CreateSucceeded(record.WorkToken, default, frameCredit));
            Assert.Throws<ArgumentException>(() =>
                NvencFrameCompletionRecord.CreateSucceeded(record.WorkToken, workLease, default));
        }

        [Test]
        public void Record_Factories_RejectMismatchedTokenAndSlot()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(1, out NvencCaptureWorkSlotLease workLease, out NvencFrameCompletionCreditLease frameCredit);

            CaptureFrameWorkToken otherSlot = new CaptureFrameWorkToken(
                Guid.NewGuid(), workLease.SlotIndex + 1, workLease.Generation, 1, 1);
            Assert.Throws<ArgumentException>(() =>
                NvencFrameCompletionRecord.CreateSucceeded(otherSlot, workLease, frameCredit));

            CaptureFrameWorkToken otherGeneration = new CaptureFrameWorkToken(
                Guid.NewGuid(), workLease.SlotIndex, workLease.Generation + 1, 1, 1);
            Assert.Throws<ArgumentException>(() =>
                NvencFrameCompletionRecord.CreateSucceeded(otherGeneration, workLease, frameCredit));
        }

        [Test]
        public void Record_Factories_RejectWrongReasonKind()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(1, out NvencCaptureWorkSlotLease workLease, out NvencFrameCompletionCreditLease frameCredit);

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                NvencFrameCompletionRecord.CreateFailed(
                    record.WorkToken, workLease, frameCredit, NvencFrameCompletionReason.None));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                NvencFrameCompletionRecord.CreateFailed(
                    record.WorkToken, workLease, frameCredit, NvencFrameCompletionReason.CancelledBeforeSubmit));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                NvencFrameCompletionRecord.CreateCancelled(
                    record.WorkToken, workLease, frameCredit, NvencFrameCompletionReason.None));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                NvencFrameCompletionRecord.CreateCancelled(
                    record.WorkToken, workLease, frameCredit, NvencFrameCompletionReason.GpuConversionFailed));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                NvencFrameCompletionRecord.CreateFailed(
                    record.WorkToken, workLease, frameCredit, (NvencFrameCompletionReason)999));
        }

        [Test]
        public void Record_VariantMismatch_IsInvalid()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(1, out NvencCaptureWorkSlotLease workLease, out NvencFrameCompletionCreditLease frameCredit);

            NvencFrameCompletionRecord succeeded = NvencFrameCompletionRecord.CreateSucceeded(
                record.WorkToken, workLease, frameCredit);
            Assert.That(succeeded.IsValid, Is.True);
            Assert.That(WithField(succeeded, "_reason", NvencFrameCompletionReason.GpuConversionFailed).IsValid, Is.False);
            Assert.That(WithField(succeeded, "_status", CaptureFrameCompletionStatus.Failed).IsValid, Is.False);
            Assert.That(WithField(succeeded, "_status", CaptureFrameCompletionStatus.None).IsValid, Is.False);
            Assert.That(WithField(succeeded, "_status", (CaptureFrameCompletionStatus)999).IsValid, Is.False);
            Assert.That(WithField(succeeded, "_workToken", default(CaptureFrameWorkToken)).IsValid, Is.False);
        }

        [Test]
        public void Record_PrivateConstructorOnly_NoArbitraryStatusOrReason()
        {
            foreach (ConstructorInfo constructor in typeof(NvencFrameCompletionRecord).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(constructor.IsPrivate, Is.True, "The record must not expose a public or internal constructor.");
            }
        }

        [Test]
        public void Record_HoldsOnlyFiveValueTypeFields_NoCountOrException()
        {
            Type type = typeof(NvencFrameCompletionRecord);
            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(fields.Length, Is.EqualTo(5));

            Type[] expected =
            {
                typeof(CaptureFrameWorkToken),
                typeof(NvencCaptureWorkSlotLease),
                typeof(NvencFrameCompletionCreditLease),
                typeof(CaptureFrameCompletionStatus),
                typeof(NvencFrameCompletionReason),
            };

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(field.FieldType.IsValueType, Is.True, field.Name + " must be a value type.");
                Assert.That(expected, Does.Contain(field.FieldType));
            }

            Assert.That(type.IsValueType, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            // No produced-output count and no carried exception are expressible.
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(property.Name, Does.Not.Contain("ProducedArtifactCount"));
                Assert.That(property.Name, Does.Not.Contain("Failure"));
            }
        }

        // -------------------------------------------------------------------
        // Producer publish
        // -------------------------------------------------------------------

        [Test]
        public void Publish_SinkAppended_SucceededReasonNone()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(1, out _, out _);

            Assert.That(h.Boundary.TryPublishSubmitted(
                record, NvencRunChunkSinkResult.Appended(record.WorkToken, 16), out NvencFrameCompletionRecord published), Is.True);

            Assert.That(published.IsValid, Is.True);
            Assert.That(published.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
            Assert.That(published.Reason, Is.EqualTo(NvencFrameCompletionReason.None));

            Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord collected), Is.True);
            Assert.That(collected.WorkToken.IdenticalTo(record.WorkToken), Is.True);
            Assert.That(collected.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
            Assert.That(collected.Reason, Is.EqualTo(NvencFrameCompletionReason.None));
        }

        [Test]
        public void Publish_SinkControlledFailure_FailedRunChunkReason()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(1, out _, out _);

            Assert.That(h.Boundary.TryPublishSubmitted(
                record, NvencRunChunkSinkResult.ControlledFailure(record.WorkToken), out NvencFrameCompletionRecord published), Is.True);

            Assert.That(published.Status, Is.EqualTo(CaptureFrameCompletionStatus.Failed));
            Assert.That(published.Reason, Is.EqualTo(NvencFrameCompletionReason.RunChunkControlledFailure));

            Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord collected), Is.True);
            Assert.That(collected.Status, Is.EqualTo(CaptureFrameCompletionStatus.Failed));
            Assert.That(collected.Reason, Is.EqualTo(NvencFrameCompletionReason.RunChunkControlledFailure));
        }

        [Test]
        public void Publish_RunAbandoned_WithRecoveryResult_CancelledAfterRunAbandoned()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmittedRecovered(
                1, out NvencRunAbandonedRecoveryResult recoveryResult);

            Assert.That(h.Boundary.TryPublishRunAbandoned(record, recoveryResult, out NvencFrameCompletionRecord published), Is.True);
            Assert.That(published.Status, Is.EqualTo(CaptureFrameCompletionStatus.Cancelled));
            Assert.That(published.Reason, Is.EqualTo(NvencFrameCompletionReason.CancelledAfterRunAbandoned));
        }

        [Test]
        public void Publish_RunAbandoned_WithoutRecoveryResult_Poisons()
        {
            Harness h = new Harness();
            // The Submitted record's sample slot is still active (abandoned
            // before the collector ran) and no recovery result exists.
            NvencSubmitToOutputRecord record = h.ProduceSubmittedUnresolved(1, out _, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                h.Boundary.TryPublishRunAbandoned(record, default, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(GetQueue(h.Boundary).Count, Is.EqualTo(0));
        }

        [Test]
        public void Publish_RunAbandoned_MismatchedRecoveryResult_Poisons()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmittedRecovered(1, out _);
            h.ProduceSubmittedRecovered(2, out NvencRunAbandonedRecoveryResult otherResult);

            Assert.Throws<InvalidOperationException>(() =>
                h.Boundary.TryPublishRunAbandoned(record, otherResult, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
        }

        [Test]
        public void Publish_FailedBeforeSubmit_MapsFailureAndCancellation()
        {
            Harness h = new Harness();

            NvencFailedBeforeSubmitReason[] reasons =
            {
                NvencFailedBeforeSubmitReason.GpuConversionFailed,
                NvencFailedBeforeSubmitReason.NvencSubmitFailed,
                NvencFailedBeforeSubmitReason.CancelledBeforeSubmit,
                NvencFailedBeforeSubmitReason.CancelledAfterRunAbandoned,
                NvencFailedBeforeSubmitReason.DrainedBeforeSubmit,
            };

            CaptureFrameCompletionStatus[] expectedStatus =
            {
                CaptureFrameCompletionStatus.Failed,
                CaptureFrameCompletionStatus.Failed,
                CaptureFrameCompletionStatus.Cancelled,
                CaptureFrameCompletionStatus.Cancelled,
                CaptureFrameCompletionStatus.Cancelled,
            };

            NvencFrameCompletionReason[] expectedReason =
            {
                NvencFrameCompletionReason.GpuConversionFailed,
                NvencFrameCompletionReason.NvencSubmitFailed,
                NvencFrameCompletionReason.CancelledBeforeSubmit,
                NvencFrameCompletionReason.CancelledAfterRunAbandoned,
                NvencFrameCompletionReason.DrainedBeforeSubmit,
            };

            for (int i = 0; i < reasons.Length; i++)
            {
                NvencSubmitToOutputRecord record = h.ProduceFailedBeforeSubmit(
                    i + 1, reasons[i], out _, out _, out NvencFailedBeforeSubmitReleaseResult releaseResult);

                Assert.That(h.Boundary.TryPublishFailedBeforeSubmit(record, releaseResult, out NvencFrameCompletionRecord published), Is.True);
                Assert.That(published.Status, Is.EqualTo(expectedStatus[i]));
                Assert.That(published.Reason, Is.EqualTo(expectedReason[i]));

                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord collected), Is.True);
                Assert.That(collected.Status, Is.EqualTo(expectedStatus[i]));
                Assert.That(collected.Reason, Is.EqualTo(expectedReason[i]));
            }
        }

        [Test]
        public void Publish_FailedBeforeSubmit_WithoutReleaseResult_Poisons()
        {
            Harness h = new Harness();
            // The FailedBeforeSubmit record's sample slot is still active
            // (the release was not performed) and no release result exists.
            NvencSubmitToOutputRecord record = h.ProduceFailedBeforeSubmitUnreleased(
                1, NvencFailedBeforeSubmitReason.GpuConversionFailed, out _, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                h.Boundary.TryPublishFailedBeforeSubmit(record, default, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(GetQueue(h.Boundary).Count, Is.EqualTo(0));
        }

        [Test]
        public void Publish_FailedBeforeSubmit_MismatchedReleaseResult_Poisons()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceFailedBeforeSubmit(
                1, NvencFailedBeforeSubmitReason.GpuConversionFailed, out _, out _, out _);
            h.ProduceFailedBeforeSubmit(
                2, NvencFailedBeforeSubmitReason.NvencSubmitFailed, out _, out _, out NvencFailedBeforeSubmitReleaseResult otherResult);

            Assert.Throws<InvalidOperationException>(() =>
                h.Boundary.TryPublishFailedBeforeSubmit(record, otherResult, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
        }

        [Test]
        public void Publish_CollectorControlledFailure_ValidEvidence_PublishesFailed()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceCollectorControlledFailure(
                1, out NvencSubmittedOutputCollectResult collectorResult);

            Assert.That(h.Boundary.TryPublishCollectorControlledFailure(
                record, collectorResult, out NvencFrameCompletionRecord published), Is.True);

            Assert.That(published.Status, Is.EqualTo(CaptureFrameCompletionStatus.Failed));
            Assert.That(published.Reason, Is.EqualTo(NvencFrameCompletionReason.OutputCollectControlledFailure));
            Assert.That(h.SubmitToOutputCredits.IsActive(record.SubmitToOutputCredit), Is.False);
        }

        [Test]
        public void Publish_CollectorControlledFailure_WithoutCollector_PoisonsNoCompletionNoCreditReturn()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmittedUnresolved(1, out _, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                h.Boundary.TryPublishCollectorControlledFailure(record, default, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.SubmitToOutputCredits.IsActive(record.SubmitToOutputCredit), Is.True);
            Assert.That(GetQueue(h.Boundary).Count, Is.EqualTo(0));
        }

        [Test]
        public void Publish_CollectorControlledFailure_ForeignProof_PoisonsNoCompletionNoCreditReturn()
        {
            Harness h = new Harness();
            Harness other = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmittedUnresolved(1, out _, out NvencEncodeSampleSlotLease sampleLease, out _);

            // Mint a proof from a foreign buffer for this work token.
            Assert.That(other.Buffer.TryBeginWrite(record.WorkToken, out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(other.Buffer.TryCancelCollectorReservation(
                write, out NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof foreignProof), Is.True);
            Assert.That(h.SampleSlots.TryReturn(sampleLease), Is.True);

            NvencSubmittedOutputCollectResult collectorResult =
                NvencSubmittedOutputCollectResult.ControlledFailure(record.WorkToken, foreignProof);

            Assert.Throws<InvalidOperationException>(() =>
                h.Boundary.TryPublishCollectorControlledFailure(record, collectorResult, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.SubmitToOutputCredits.IsActive(record.SubmitToOutputCredit), Is.True);
            Assert.That(GetQueue(h.Boundary).Count, Is.EqualTo(0));
        }

        [Test]
        public void Publish_CollectorControlledFailure_StaleProofAfterRereserve_Poisons()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmittedUnresolved(1, out _, out NvencEncodeSampleSlotLease sampleLease, out _);

            // Mint a valid proof, then re-reserve the same work token so the
            // recovery nonce rotates and the proof goes stale.
            Assert.That(h.Buffer.TryBeginWrite(record.WorkToken, out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(h.Buffer.TryCancelCollectorReservation(
                write, out NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof staleProof), Is.True);
            Assert.That(h.Buffer.TryBeginWrite(record.WorkToken, out _), Is.True);
            Assert.That(h.SampleSlots.TryReturn(sampleLease), Is.True);

            NvencSubmittedOutputCollectResult collectorResult =
                NvencSubmittedOutputCollectResult.ControlledFailure(record.WorkToken, staleProof);

            Assert.Throws<InvalidOperationException>(() =>
                h.Boundary.TryPublishCollectorControlledFailure(record, collectorResult, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(GetQueue(h.Boundary).Count, Is.EqualTo(0));
        }

        [Test]
        public void Publish_CollectorControlledFailure_WrongWorkToken_Poisons()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmittedUnresolved(1, out _, out NvencEncodeSampleSlotLease sampleLease, out _);
            NvencSubmitToOutputRecord otherRecord = h.ProduceSubmittedUnresolved(2, out _, out _, out _);

            Assert.That(h.Buffer.TryBeginWrite(record.WorkToken, out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(h.Buffer.TryCancelCollectorReservation(
                write, out NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof proof), Is.True);
            Assert.That(h.SampleSlots.TryReturn(sampleLease), Is.True);

            // A collector result naming a different work token cannot publish
            // this record's completion.
            NvencSubmittedOutputCollectResult collectorResult =
                NvencSubmittedOutputCollectResult.ControlledFailure(otherRecord.WorkToken, proof);

            Assert.Throws<InvalidOperationException>(() =>
                h.Boundary.TryPublishCollectorControlledFailure(record, collectorResult, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.SubmitToOutputCredits.IsActive(record.SubmitToOutputCredit), Is.True);
            Assert.That(GetQueue(h.Boundary).Count, Is.EqualTo(0));
        }

        [Test]
        public void ReleaseResult_CreateBeforeReturn_Rejected()
        {
            Harness h = new Harness();
            // The sample slot is still active (the release was not performed).
            NvencSubmitToOutputRecord record = h.ProduceFailedBeforeSubmitUnreleased(
                1, NvencFailedBeforeSubmitReason.GpuConversionFailed, out _, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                NvencFailedBeforeSubmitReleaseResult.Create(record, h.SampleSlots));
        }

        [Test]
        public void ReleaseResult_CreateAfterReturn_Accepted()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceFailedBeforeSubmit(
                1, NvencFailedBeforeSubmitReason.GpuConversionFailed, out _, out _, out NvencFailedBeforeSubmitReleaseResult result);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Matches(record, h.SampleSlots), Is.True);
        }

        [Test]
        public void RecoveryResult_Create_NeverIssued_Accepted()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmittedRecovered(
                1, out NvencRunAbandonedRecoveryResult result);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Matches(record, h.SampleSlots, h.Buffer), Is.True);
        }

        [Test]
        public void RecoveryResult_Create_IssuedThenReturned_Accepted()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmittedWithHeldOwnedUnit(
                1, out NvencOwnedAccessUnitLease ownedLease);

            Assert.That(h.Buffer.TryReturnOwnedAccessUnit(
                ownedLease, out NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof proof),
                Is.EqualTo(NvencOwnedAccessUnitBoundaryStatus.Ready));

            NvencRunAbandonedRecoveryResult result =
                NvencRunAbandonedRecoveryResult.Create(record, h.SampleSlots, h.Buffer, proof);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Matches(record, h.SampleSlots, h.Buffer), Is.True);
        }

        [Test]
        public void RecoveryResult_Create_DefaultProof_Rejected()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmittedUnresolved(
                1, out _, out NvencEncodeSampleSlotLease sampleLease, out _);
            Assert.That(h.SampleSlots.TryReturn(sampleLease), Is.True);

            // A default (never minted) proof cannot certify recovery.
            Assert.Throws<ArgumentException>(() =>
                NvencRunAbandonedRecoveryResult.Create(record, h.SampleSlots, h.Buffer, default));
        }

        [Test]
        public void RecoveryResult_Create_FakeGenerationLease_CannotMintProof()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmittedWithHeldOwnedUnit(
                1, out NvencOwnedAccessUnitLease ownedLease);

            // A lease forged with the same owner token but a wrong generation
            // is not exact and therefore cannot mint a recovery proof.
            NvencOwnedAccessUnitLease fake = new NvencOwnedAccessUnitLease(
                ownedLease.OwnerToken, ownedLease.Generation + 5, ownedLease.WorkToken);

            Assert.That(h.Buffer.TryReturnOwnedAccessUnit(
                fake, out NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof forgedProof),
                Is.EqualTo(NvencOwnedAccessUnitBoundaryStatus.Invalid));
            Assert.That(h.Buffer.VerifyRecoveryProof(forgedProof, record.WorkToken), Is.False);
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));
        }

        [Test]
        public void RecoveryResult_Create_ForgedProof_Rejected()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmittedWithHeldOwnedUnit(
                1, out NvencOwnedAccessUnitLease ownedLease);

            // A proof forged with the correct owner token but a wrong nonce is rejected.
            NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof forged =
                new NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof(
                    ownedLease.OwnerToken, Guid.NewGuid(), record.WorkToken);
            Assert.That(h.Buffer.VerifyRecoveryProof(forged, record.WorkToken), Is.False);

            Assert.Throws<ArgumentException>(() =>
                NvencRunAbandonedRecoveryResult.Create(record, h.SampleSlots, h.Buffer, forged));
        }

        [Test]
        public void RecoveryResult_Create_ForeignBufferProof_Rejected()
        {
            Harness h = new Harness();
            Harness other = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmittedRecovered(1, out _);

            Assert.That(other.Buffer.TryBeginWrite(
                record.WorkToken, out NvencAccessUnitWriteLease foreignWrite), Is.True);
            Assert.That(other.Buffer.TryCancelCollectorReservation(
                foreignWrite, out NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof foreignProof), Is.True);

            Assert.Throws<ArgumentException>(() =>
                NvencRunAbandonedRecoveryResult.Create(record, h.SampleSlots, h.Buffer, foreignProof));
        }

        [Test]
        public void RecoveryResult_StaleProofAfterRereserve_Rejected()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmittedUnresolved(
                1, out _, out NvencEncodeSampleSlotLease sampleLease, out _);

            // 1. Mint a "never issued" proof ahead of time via a cancel.
            Assert.That(h.Buffer.TryBeginWrite(
                record.WorkToken, out NvencAccessUnitWriteLease writeLease), Is.True);
            Assert.That(h.SampleSlots.TryReturn(sampleLease), Is.True);
            Assert.That(h.Buffer.TryCancelCollectorReservation(
                writeLease, out NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof staleProof), Is.True);
            Assert.That(h.Buffer.VerifyRecoveryProof(staleProof, record.WorkToken), Is.True);

            // 2. Re-reserve the same work token and progress to SinkOwned.
            Assert.That(h.Buffer.TryBeginWrite(
                record.WorkToken, out NvencAccessUnitWriteLease write2), Is.True);
            Assert.That(
                h.Buffer.TryCopyCompletedOutput(write2, record.SampleSlot, new PatternSource(16, 0x40), out _),
                Is.EqualTo(NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus.Committed));
            Assert.That(h.Buffer.TryTransferToSink(write2, out _), Is.True);

            // 3. The stale proof can no longer mint a recovery result.
            Assert.That(h.Buffer.VerifyRecoveryProof(staleProof, record.WorkToken), Is.False);
            Assert.Throws<ArgumentException>(() =>
                NvencRunAbandonedRecoveryResult.Create(record, h.SampleSlots, h.Buffer, staleProof));
        }

        [Test]
        public void Publish_RunAbandoned_StaleProofAfterRereserve_Poisons()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmittedRecovered(
                1, out NvencRunAbandonedRecoveryResult result);

            // Re-reserve the same work token and progress to SinkOwned, which
            // rotates the recovery nonce and invalidates the earlier proof.
            Assert.That(h.Buffer.TryBeginWrite(
                record.WorkToken, out NvencAccessUnitWriteLease writeLease), Is.True);
            Assert.That(
                h.Buffer.TryCopyCompletedOutput(writeLease, record.SampleSlot, new PatternSource(16, 0x40), out _),
                Is.EqualTo(NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus.Committed));
            Assert.That(h.Buffer.TryTransferToSink(writeLease, out _), Is.True);

            // The stale recovery result can no longer publish a Cancelled completion.
            Assert.Throws<InvalidOperationException>(() => h.Boundary.TryPublishRunAbandoned(record, result, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
        }

        [Test]
        public void Publish_ForwardsExactTokenSlotAndCredits()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(7, out NvencCaptureWorkSlotLease workLease, out NvencFrameCompletionCreditLease frameCredit);

            Assert.That(h.Boundary.TryPublishSubmitted(
                record, NvencRunChunkSinkResult.Appended(record.WorkToken, 32), out _), Is.True);
            Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord collected), Is.True);

            Assert.That(collected.WorkToken.IdenticalTo(record.WorkToken), Is.True);
            Assert.That(collected.WorkToken.CaptureFrameId, Is.EqualTo(7));
            Assert.That(collected.WorkSlot.SlotIndex, Is.EqualTo(workLease.SlotIndex));
            Assert.That(collected.WorkSlot.Generation, Is.EqualTo(workLease.Generation));
            Assert.That(collected.FrameCompletionCredit.SlotIndex, Is.EqualTo(frameCredit.SlotIndex));
            Assert.That(collected.FrameCompletionCredit.Generation, Is.EqualTo(frameCredit.Generation));
        }

        [Test]
        public void Publish_SinkResultTokenSubstitution_Poisons()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(1, out _, out _);

            // A sink result naming a different frame is an ownership break.
            CaptureFrameWorkToken other = new CaptureFrameWorkToken(
                Guid.NewGuid(), record.WorkSlot.SlotIndex, record.WorkSlot.Generation, 1, 2);

            Assert.Throws<InvalidOperationException>(() =>
                h.Boundary.TryPublishSubmitted(record, NvencRunChunkSinkResult.Appended(other, 16), out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(GetQueue(h.Boundary).Count, Is.EqualTo(0));
        }

        [Test]
        public void Publish_ForeignRecord_PoisonsBeforeEnqueue()
        {
            Harness owner = new Harness();
            Harness foreign = new Harness();

            NvencSubmitToOutputRecord record = owner.ProduceSubmitted(1, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                foreign.Boundary.TryPublishSubmitted(
                    record, NvencRunChunkSinkResult.Appended(record.WorkToken, 16), out _));
            Assert.That(foreign.State.IsPoisoned, Is.True);
            Assert.That(GetQueue(foreign.Boundary).Count, Is.EqualTo(0));
        }

        [Test]
        public void Publish_ReturnedWorkSlotOrCredit_PoisonsBeforeEnqueue()
        {
            // Work slot returned out-of-band.
            Harness h1 = new Harness();
            NvencSubmitToOutputRecord r1 = h1.ProduceSubmitted(1, out NvencCaptureWorkSlotLease w1, out _);
            Assert.That(h1.WorkSlots.TryReturn(w1), Is.True);
            Assert.Throws<InvalidOperationException>(() =>
                h1.Boundary.TryPublishSubmitted(r1, NvencRunChunkSinkResult.Appended(r1.WorkToken, 16), out _));
            Assert.That(h1.State.IsPoisoned, Is.True);
            Assert.That(GetQueue(h1.Boundary).Count, Is.EqualTo(0));

            // Submit-to-Output credit returned out-of-band.
            Harness h2 = new Harness();
            NvencSubmitToOutputRecord r2 = h2.ProduceSubmitted(1, out _, out _);
            Assert.That(h2.SubmitToOutputCredits.TryReturn(r2.SubmitToOutputCredit), Is.True);
            Assert.Throws<InvalidOperationException>(() =>
                h2.Boundary.TryPublishSubmitted(r2, NvencRunChunkSinkResult.Appended(r2.WorkToken, 16), out _));
            Assert.That(h2.State.IsPoisoned, Is.True);

            // Frame Completion credit returned out-of-band.
            Harness h3 = new Harness();
            NvencSubmitToOutputRecord r3 = h3.ProduceSubmitted(1, out _, out NvencFrameCompletionCreditLease f3);
            Assert.That(h3.FrameCompletionCredits.TryReturn(f3), Is.True);
            Assert.Throws<InvalidOperationException>(() =>
                h3.Boundary.TryPublishSubmitted(r3, NvencRunChunkSinkResult.Appended(r3.WorkToken, 16), out _));
            Assert.That(h3.State.IsPoisoned, Is.True);
        }

        [Test]
        public void Publish_DoublePublish_SecondDoesNotEnqueue()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(1, out _, out _);
            NvencRunChunkSinkResult sinkResult = NvencRunChunkSinkResult.Appended(record.WorkToken, 16);

            Assert.That(h.Boundary.TryPublishSubmitted(record, sinkResult, out _), Is.True);
            Assert.That(GetQueue(h.Boundary).Count, Is.EqualTo(1));

            Assert.Throws<InvalidOperationException>(() =>
                h.Boundary.TryPublishSubmitted(record, sinkResult, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(GetQueue(h.Boundary).Count, Is.EqualTo(1));
        }

        [Test]
        public void Publish_ReturnsSubmitToOutputCredit_WorkSlotAndCreditStayActive()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(
                1, out NvencCaptureWorkSlotLease workLease, out NvencFrameCompletionCreditLease frameCredit);

            Assert.That(h.SubmitToOutputCredits.IsActive(record.SubmitToOutputCredit), Is.True);

            Assert.That(h.Boundary.TryPublishSubmitted(
                record, NvencRunChunkSinkResult.Appended(record.WorkToken, 16), out _), Is.True);

            // Submit-to-Output credit returned at publish.
            Assert.That(h.SubmitToOutputCredits.IsActive(record.SubmitToOutputCredit), Is.False);
            // Work Slot and Frame Completion credit still active until collect.
            Assert.That(h.WorkSlots.IsActive(workLease), Is.True);
            Assert.That(h.FrameCompletionCredits.IsActive(frameCredit), Is.True);
        }

        // -------------------------------------------------------------------
        // Main Thread collect
        // -------------------------------------------------------------------

        [Test]
        public void Collect_EmptyQueue_FalseNoSideEffect()
        {
            Harness h = new Harness();

            Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.False);
            Assert.That(completion.IsValid, Is.False);
            Assert.That(h.WorkSlots.OccupiedCount, Is.EqualTo(0));
            Assert.That(h.FrameCompletionCredits.OccupiedCount, Is.EqualTo(0));
            Assert.That(h.SubmitToOutputCredits.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void Collect_ReturnsWorkSlotAndCredit_ThenReusable()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(
                1, out NvencCaptureWorkSlotLease workLease, out NvencFrameCompletionCreditLease frameCredit);

            Assert.That(h.Boundary.TryPublishSubmitted(
                record, NvencRunChunkSinkResult.Appended(record.WorkToken, 16), out _), Is.True);
            Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord collected), Is.True);
            Assert.That(collected.WorkToken.IdenticalTo(record.WorkToken), Is.True);

            Assert.That(h.WorkSlots.IsActive(workLease), Is.False);
            Assert.That(h.FrameCompletionCredits.IsActive(frameCredit), Is.False);
            Assert.That(h.WorkSlots.OccupiedCount, Is.EqualTo(0));
            Assert.That(h.FrameCompletionCredits.OccupiedCount, Is.EqualTo(0));

            // The same slot index is reusable with a new generation.
            Assert.That(h.WorkSlots.TryRent(out NvencCaptureWorkSlotLease rerented), Is.True);
            Assert.That(rerented.SlotIndex, Is.EqualTo(workLease.SlotIndex));
            Assert.That(rerented.Generation, Is.EqualTo(workLease.Generation + 1));
        }

        [Test]
        public void Collect_StaleWorkSlot_PoisonsWithoutReturningCompletion()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(
                1, out NvencCaptureWorkSlotLease workLease, out _);

            Assert.That(h.Boundary.TryPublishSubmitted(
                record, NvencRunChunkSinkResult.Appended(record.WorkToken, 16), out _), Is.True);

            // The queued record's work slot is released out-of-band.
            Assert.That(h.WorkSlots.TryReturn(workLease), Is.True);

            Assert.Throws<InvalidOperationException>(() => h.Boundary.TryCollect(out _));
            Assert.That(h.State.IsPoisoned, Is.True);
        }

        [Test]
        public void Collect_StaleCredit_PoisonsWithoutReturningCompletion()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(
                1, out _, out NvencFrameCompletionCreditLease frameCredit);

            Assert.That(h.Boundary.TryPublishSubmitted(
                record, NvencRunChunkSinkResult.Appended(record.WorkToken, 16), out _), Is.True);

            // The queued record's Frame Completion credit is released out-of-band.
            Assert.That(h.FrameCompletionCredits.TryReturn(frameCredit), Is.True);

            Assert.Throws<InvalidOperationException>(() => h.Boundary.TryCollect(out _));
            Assert.That(h.State.IsPoisoned, Is.True);
        }

        // -------------------------------------------------------------------
        // Queue and process invariants
        // -------------------------------------------------------------------

        [Test]
        public void QueueFull_PoisonsWithoutOverwrite()
        {
            Harness h = new Harness();
            NvencFixedSpscQueue<NvencFrameCompletionRecord> queue = GetQueue(h.Boundary);

            for (int i = 0; i < 8; i++)
            {
                Assert.That(queue.TryEnqueue(default), Is.True);
            }

            NvencSubmitToOutputRecord record = h.ProduceSubmitted(1, out _, out _);

            Assert.Throws<InvalidOperationException>(() =>
                h.Boundary.TryPublishSubmitted(
                    record, NvencRunChunkSinkResult.Appended(record.WorkToken, 16), out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(queue.Count, Is.EqualTo(8));
        }

        [Test]
        public void Poison_PublishCollectAndReturnRefused()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceSubmitted(
                1, out NvencCaptureWorkSlotLease workLease, out NvencFrameCompletionCreditLease frameCredit);

            Assert.That(h.State.TryPoison(), Is.True);

            Assert.That(h.Boundary.TryPublishSubmitted(
                record, NvencRunChunkSinkResult.Appended(record.WorkToken, 16), out _), Is.False);
            Assert.That(h.Boundary.TryCollect(out _), Is.False);
            Assert.That(h.WorkSlots.TryReturn(workLease), Is.False);
            Assert.That(h.FrameCompletionCredits.TryReturn(frameCredit), Is.False);
            Assert.That(h.SubmitToOutputCredits.TryReturn(record.SubmitToOutputCredit), Is.False);
            Assert.That(GetQueue(h.Boundary).Count, Is.EqualTo(0));
        }

        [Test]
        public void Fifo_EnqueueOrderNotCaptureFrameIdOrder()
        {
            Harness h = new Harness();

            NvencSubmitToOutputRecord r3 = h.ProduceSubmitted(3, out _, out _);
            NvencSubmitToOutputRecord r1 = h.ProduceSubmitted(1, out _, out _);
            NvencSubmitToOutputRecord r2 = h.ProduceSubmitted(2, out _, out _);

            Assert.That(h.Boundary.TryPublishSubmitted(r3, NvencRunChunkSinkResult.Appended(r3.WorkToken, 8), out _), Is.True);
            Assert.That(h.Boundary.TryPublishSubmitted(r1, NvencRunChunkSinkResult.Appended(r1.WorkToken, 8), out _), Is.True);
            Assert.That(h.Boundary.TryPublishSubmitted(r2, NvencRunChunkSinkResult.Appended(r2.WorkToken, 8), out _), Is.True);

            long[] expected = { 3, 1, 2 };
            for (int i = 0; i < expected.Length; i++)
            {
                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord collected), Is.True);
                Assert.That(collected.WorkToken.CaptureFrameId, Is.EqualTo(expected[i]));
            }
        }

        [Test]
        public void CapacityEight_NinthNotConstructible()
        {
            Harness h = new Harness();

            for (int i = 0; i < 8; i++)
            {
                Assert.That(h.WorkSlots.TryRent(out _), Is.True);
                Assert.That(h.SubmitToOutputCredits.TryRent(out _), Is.True);
                Assert.That(h.FrameCompletionCredits.TryRent(out _), Is.True);
            }

            Assert.That(h.SubmitToOutputCredits.TryRent(out NvencSubmitToOutputCreditLease ninthCredit), Is.False);
            Assert.That(ninthCredit.IsValid, Is.False);
            Assert.That(h.FrameCompletionCredits.TryRent(out NvencFrameCompletionCreditLease ninthFrameCredit), Is.False);
            Assert.That(ninthFrameCredit.IsValid, Is.False);
        }

        [Test]
        public void Circular_120Items_ExactlyOnce_FinalOccupancyZero()
        {
            Harness h = new Harness();
            bool[] seen = new bool[121];

            int published = 0;
            int collected = 0;

            for (long frameId = 1; frameId <= 120; frameId++)
            {
                NvencSubmitToOutputRecord record = h.ProduceSubmitted(frameId, out _, out _);

                Assert.That(h.Boundary.TryPublishSubmitted(
                    record, NvencRunChunkSinkResult.Appended(record.WorkToken, 100), out NvencFrameCompletionRecord p), Is.True);
                Assert.That(p.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                published++;

                // Keep at most eight in flight so the eight slots circulate.
                if (published - collected >= 8)
                {
                    Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord c), Is.True);
                    long id = c.WorkToken.CaptureFrameId;
                    Assert.That(seen[id], Is.False, "frame " + id + " collected twice");
                    seen[id] = true;
                    collected++;
                }
            }

            while (collected < published)
            {
                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord c), Is.True);
                long id = c.WorkToken.CaptureFrameId;
                Assert.That(seen[id], Is.False, "frame " + id + " collected twice");
                seen[id] = true;
                collected++;
            }

            Assert.That(collected, Is.EqualTo(120));
            for (int i = 1; i <= 120; i++)
            {
                Assert.That(seen[i], Is.True, "frame " + i + " was never collected");
            }

            Assert.That(h.WorkSlots.OccupiedCount, Is.EqualTo(0));
            Assert.That(h.FrameCompletionCredits.OccupiedCount, Is.EqualTo(0));
            Assert.That(h.SubmitToOutputCredits.OccupiedCount, Is.EqualTo(0));
        }

        // -------------------------------------------------------------------
        // Shape / source audits
        // -------------------------------------------------------------------

        [Test]
        public void Boundary_UsesOnlyFixedSpscQueue_NoAdditionalQueuePrimitive()
        {
            Type boundaryType = typeof(NvencFrameCompletionBoundary);
            FieldInfo queueField = boundaryType.GetField("_queue", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(queueField, Is.Not.Null);
            Assert.That(queueField.FieldType.IsGenericType, Is.True);
            Assert.That(queueField.FieldType.GetGenericTypeDefinition(), Is.EqualTo(typeof(NvencFixedSpscQueue<>)));

            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencFrameCompletionBoundary.cs"));
            Assert.That(source, Does.Contain("NvencFixedSpscQueue"));
            Assert.That(source, Does.Not.Contain("new Queue"));
            Assert.That(source, Does.Not.Contain("ConcurrentQueue"));
        }

        [Test]
        public void Source_NoQueueTaskThreadIoHashOutputOrNewException()
        {
            string directory = RuntimeDirectory();
            string source =
                File.ReadAllText(Path.Combine(directory, "NvencFrameCompletionBoundary.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencFrameCompletionRecord.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencFrameCompletionReason.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencFailedBeforeSubmitReleaseResult.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencRunAbandonedRecoveryResult.cs"));

            string[] forbidden =
            {
                "lock (", "Monitor", "ManualResetEvent", "AutoResetEvent", "WaitHandle", "SpinWait",
                "new Thread", "ThreadPool", "Task", "File.", "Directory.", "FileStream", "DllImport",
                "UnityEngine", "Application.", "SystemInfo", "GraphicsDevice", "IntPtr", "SafeHandle",
                "NvEnc", "RenderTexture", "SHA", "MD5", "Hash", "H.264", "H264", "ArrayPool",
                "Enumerable", ".Select(", ".Where(", ".ToList(", ".ToArray(",
                "new Queue", "new Dictionary", "new List", "new HashSet", "new Stack", "new ArrayList",
                "ProducedArtifactCount", "Artifact", ": Exception", "ExceptionDispatchInfo",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "Frame Completion source must not reference: " + word);
            }

            Assert.That(typeof(Exception).IsAssignableFrom(typeof(NvencFrameCompletionBoundary)), Is.False);
            Assert.That(typeof(Exception).IsAssignableFrom(typeof(NvencFrameCompletionRecord)), Is.False);
            Assert.That(typeof(Exception).IsAssignableFrom(typeof(NvencFrameCompletionReason)), Is.False);
            Assert.That(typeof(Exception).IsAssignableFrom(typeof(NvencFailedBeforeSubmitReleaseResult)), Is.False);
            Assert.That(typeof(Exception).IsAssignableFrom(typeof(NvencRunAbandonedRecoveryResult)), Is.False);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static NvencFixedSpscQueue<NvencFrameCompletionRecord> GetQueue(NvencFrameCompletionBoundary boundary)
        {
            FieldInfo field = typeof(NvencFrameCompletionBoundary).GetField(
                "_queue", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return (NvencFixedSpscQueue<NvencFrameCompletionRecord>)field.GetValue(boundary);
        }

        private static NvencFrameCompletionRecord WithField(
            NvencFrameCompletionRecord record, string fieldName, object value)
        {
            object boxed = record;
            FieldInfo field = typeof(NvencFrameCompletionRecord).GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(boxed, value);
            return (NvencFrameCompletionRecord)boxed;
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        private sealed class Harness
        {
            internal NvencCaptureProcessState State = new NvencCaptureProcessState();
            internal NvencCaptureWorkSlotPool WorkSlots;
            internal NvencEncodeSampleSlotPool SampleSlots;
            internal NvencSubmitToOutputCreditPool SubmitToOutputCredits;
            internal NvencFrameCompletionCreditPool FrameCompletionCredits;
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal NvencFrameCompletionBoundary Boundary;

            internal Harness()
            {
                WorkSlots = new NvencCaptureWorkSlotPool(State);
                SampleSlots = new NvencEncodeSampleSlotPool(State);
                SubmitToOutputCredits = new NvencSubmitToOutputCreditPool(State);
                FrameCompletionCredits = new NvencFrameCompletionCreditPool(State);
                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Boundary = new NvencFrameCompletionBoundary(State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer);
            }

            internal NvencSubmitToOutputRecord ProduceSubmitted(
                long frameId,
                out NvencCaptureWorkSlotLease workLease,
                out NvencFrameCompletionCreditLease frameCompletionCredit)
            {
                RentRecord(
                    NvencSubmitToOutputRecordKind.Submitted,
                    frameId,
                    NvencFailedBeforeSubmitReason.None,
                    out NvencSubmitToOutputRecord record,
                    out workLease,
                    out NvencEncodeSampleSlotLease sampleLease,
                    out _,
                    out frameCompletionCredit);

                // The collector returned the Sample Slot before this boundary.
                Assert.That(SampleSlots.TryReturn(sampleLease), Is.True);

                return record;
            }

            internal NvencSubmitToOutputRecord ProduceSubmittedRecovered(
                long frameId,
                out NvencRunAbandonedRecoveryResult recoveryResult)
            {
                RentRecord(
                    NvencSubmitToOutputRecordKind.Submitted,
                    frameId,
                    NvencFailedBeforeSubmitReason.None,
                    out NvencSubmitToOutputRecord record,
                    out _,
                    out NvencEncodeSampleSlotLease sampleLease,
                    out _,
                    out _);

                // The abandon recovery cancelled the collector's write lease
                // before any transfer, so no Owned Access Unit was issued.
                Assert.That(Buffer.TryBeginWrite(
                    record.WorkToken, out NvencAccessUnitWriteLease writeLease), Is.True);
                Assert.That(SampleSlots.TryReturn(sampleLease), Is.True);
                Assert.That(Buffer.TryCancelCollectorReservation(
                    writeLease, out NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof proof), Is.True);
                recoveryResult = NvencRunAbandonedRecoveryResult.Create(record, SampleSlots, Buffer, proof);

                return record;
            }

            internal NvencSubmitToOutputRecord ProduceSubmittedUnresolved(
                long frameId,
                out NvencCaptureWorkSlotLease workLease,
                out NvencEncodeSampleSlotLease sampleLease,
                out NvencFrameCompletionCreditLease frameCompletionCredit)
            {
                RentRecord(
                    NvencSubmitToOutputRecordKind.Submitted,
                    frameId,
                    NvencFailedBeforeSubmitReason.None,
                    out NvencSubmitToOutputRecord record,
                    out workLease,
                    out sampleLease,
                    out _,
                    out frameCompletionCredit);

                // The Sample Slot is intentionally still active (not recovered).
                return record;
            }

            internal NvencSubmitToOutputRecord ProduceFailedBeforeSubmit(
                long frameId,
                NvencFailedBeforeSubmitReason reason,
                out NvencCaptureWorkSlotLease workLease,
                out NvencFrameCompletionCreditLease frameCompletionCredit,
                out NvencFailedBeforeSubmitReleaseResult releaseResult)
            {
                RentRecord(
                    NvencSubmitToOutputRecordKind.FailedBeforeSubmit,
                    frameId,
                    reason,
                    out NvencSubmitToOutputRecord record,
                    out workLease,
                    out NvencEncodeSampleSlotLease sampleLease,
                    out _,
                    out frameCompletionCredit);

                // The failed-before-submit release returned the Sample Slot.
                Assert.That(SampleSlots.TryReturn(sampleLease), Is.True);
                releaseResult = NvencFailedBeforeSubmitReleaseResult.Create(record, SampleSlots);

                return record;
            }

            internal NvencSubmitToOutputRecord ProduceFailedBeforeSubmitUnreleased(
                long frameId,
                NvencFailedBeforeSubmitReason reason,
                out NvencCaptureWorkSlotLease workLease,
                out NvencEncodeSampleSlotLease sampleLease,
                out NvencFrameCompletionCreditLease frameCompletionCredit)
            {
                RentRecord(
                    NvencSubmitToOutputRecordKind.FailedBeforeSubmit,
                    frameId,
                    reason,
                    out NvencSubmitToOutputRecord record,
                    out workLease,
                    out sampleLease,
                    out _,
                    out frameCompletionCredit);

                // The Sample Slot is intentionally still active (not released).
                return record;
            }

            internal NvencSubmitToOutputRecord ProduceCollectorControlledFailure(
                long frameId,
                out NvencSubmittedOutputCollectResult collectorResult)
            {
                RentRecord(
                    NvencSubmitToOutputRecordKind.Submitted,
                    frameId,
                    NvencFailedBeforeSubmitReason.None,
                    out NvencSubmitToOutputRecord record,
                    out _,
                    out NvencEncodeSampleSlotLease sampleLease,
                    out _,
                    out _);

                // Exactly the collector's controlled-failure release: cancel the
                // reservation, return the Sample Slot, and issue a
                // ControlledFailure result carrying the minted recovery proof.
                Assert.That(Buffer.TryBeginWrite(record.WorkToken, out NvencAccessUnitWriteLease writeLease), Is.True);
                Assert.That(SampleSlots.TryReturn(sampleLease), Is.True);
                Assert.That(Buffer.TryCancelCollectorReservation(
                    writeLease, out NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof proof), Is.True);
                collectorResult = NvencSubmittedOutputCollectResult.ControlledFailure(record.WorkToken, proof);

                return record;
            }

            internal NvencSubmitToOutputRecord ProduceSubmittedWithHeldOwnedUnit(
                long frameId,
                out NvencOwnedAccessUnitLease ownedLease)
            {
                RentRecord(
                    NvencSubmitToOutputRecordKind.Submitted,
                    frameId,
                    NvencFailedBeforeSubmitReason.None,
                    out NvencSubmitToOutputRecord record,
                    out _,
                    out NvencEncodeSampleSlotLease sampleLease,
                    out _,
                    out _);

                // Collector: copy the bitstream, return the Sample Slot, and
                // transfer the recorded content to SinkOwned.
                Assert.That(Buffer.TryBeginWrite(record.WorkToken, out NvencAccessUnitWriteLease writeLease), Is.True);
                Assert.That(
                    Buffer.TryCopyCompletedOutput(writeLease, sampleLease, new PatternSource(16, 0x40), out _),
                    Is.EqualTo(NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus.Committed));
                Assert.That(SampleSlots.TryReturn(sampleLease), Is.True);
                Assert.That(Buffer.TryTransferToSink(writeLease, out ownedLease), Is.True);

                return record;
            }

            private void RentRecord(
                NvencSubmitToOutputRecordKind kind,
                long frameId,
                NvencFailedBeforeSubmitReason reason,
                out NvencSubmitToOutputRecord record,
                out NvencCaptureWorkSlotLease workLease,
                out NvencEncodeSampleSlotLease sampleLease,
                out NvencSubmitToOutputCreditLease submitToOutputCredit,
                out NvencFrameCompletionCreditLease frameCompletionCredit)
            {
                Assert.That(WorkSlots.TryRent(out workLease), Is.True);
                Assert.That(SampleSlots.TryRent(out sampleLease), Is.True);
                Assert.That(SubmitToOutputCredits.TryRent(out submitToOutputCredit), Is.True);
                Assert.That(FrameCompletionCredits.TryRent(out frameCompletionCredit), Is.True);

                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Guid.NewGuid(), workLease.SlotIndex, workLease.Generation, 1, frameId);

                if (kind == NvencSubmitToOutputRecordKind.Submitted)
                {
                    record = NvencSubmitToOutputRecord.CreateSubmitted(
                        token, workLease, sampleLease, submitToOutputCredit, frameCompletionCredit);
                }
                else
                {
                    record = NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                        token, workLease, sampleLease, submitToOutputCredit, frameCompletionCredit, reason);
                }
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
                int count = _length < destinationCapacity ? _length : destinationCapacity;
                for (int i = 0; i < count; i++)
                {
                    destination[i] = (byte)(_seed + i);
                }

                validLength = _length;
                return true;
            }
        }
    }
}
