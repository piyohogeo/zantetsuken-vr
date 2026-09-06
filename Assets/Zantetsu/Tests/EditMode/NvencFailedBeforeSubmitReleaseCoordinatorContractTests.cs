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
    /// Contract tests for the Phase 0.11 FailedBeforeSubmit release boundary:
    /// the coordinator returns only the exact Encode Sample Slot and issues the
    /// exact release result the later Frame Completion publish requires. No
    /// real OS, GPU, NVENC, worker, or native resource is used.
    /// </summary>
    public class NvencFailedBeforeSubmitReleaseCoordinatorContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        // -------------------------------------------------------------------
        // Release path
        // -------------------------------------------------------------------

        [Test]
        public void Release_ReturnsOnlySampleSlot_IssuesExactResult()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceFailedBeforeSubmit(
                7, NvencFailedBeforeSubmitReason.GpuConversionFailed, out NvencCaptureWorkSlotLease workLease, out NvencFrameCompletionCreditLease frameCredit);

            Assert.That(h.Coordinator.TryRelease(record, out NvencFailedBeforeSubmitReleaseResult result), Is.True);

            // One exact, valid result bound to this record's sample slot.
            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Matches(record, h.SampleSlots), Is.True);
            Assert.That(result.Record.WorkToken.IdenticalTo(record.WorkToken), Is.True);
            Assert.That(result.SampleSlot.SlotIndex, Is.EqualTo(record.SampleSlot.SlotIndex));
            Assert.That(result.SampleSlot.Generation, Is.EqualTo(record.SampleSlot.Generation));

            // Only the Sample Slot is returned.
            Assert.That(h.SampleSlots.IsActive(record.SampleSlot), Is.False);
            Assert.That(h.SampleSlots.OccupiedCount, Is.EqualTo(0));
            Assert.That(h.WorkSlots.IsActive(workLease), Is.True);
            Assert.That(h.SubmitToOutputCredits.IsActive(record.SubmitToOutputCredit), Is.True);
            Assert.That(h.FrameCompletionCredits.IsActive(frameCredit), Is.True);
        }

        [Test]
        public void Release_FailureAndCancellationReasons_Representative()
        {
            Harness h = new Harness();

            NvencSubmitToOutputRecord failed = h.ProduceFailedBeforeSubmit(
                1, NvencFailedBeforeSubmitReason.GpuConversionFailed, out _, out _);
            Assert.That(h.Coordinator.TryRelease(failed, out NvencFailedBeforeSubmitReleaseResult failedResult), Is.True);
            Assert.That(failedResult.Record.Reason, Is.EqualTo(NvencFailedBeforeSubmitReason.GpuConversionFailed));
            Assert.That(failedResult.Matches(failed, h.SampleSlots), Is.True);

            NvencSubmitToOutputRecord cancelled = h.ProduceFailedBeforeSubmit(
                2, NvencFailedBeforeSubmitReason.CancelledBeforeSubmit, out _, out _);
            Assert.That(h.Coordinator.TryRelease(cancelled, out NvencFailedBeforeSubmitReleaseResult cancelledResult), Is.True);
            Assert.That(cancelledResult.Record.Reason, Is.EqualTo(NvencFailedBeforeSubmitReason.CancelledBeforeSubmit));
            Assert.That(cancelledResult.Matches(cancelled, h.SampleSlots), Is.True);
        }

        [Test]
        public void Release_GateContention_NoChangeThenRetrySucceeds()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceFailedBeforeSubmit(
                1, NvencFailedBeforeSubmitReason.GpuConversionFailed, out _, out _);

            ManualResetEventSlim entered = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            Exception holderError = null;

            Thread holder = new Thread(() =>
            {
                try
                {
                    if (h.State.TryBeginResourceResolution())
                    {
                        entered.Set();
                        release.Wait(WatchdogTimeoutMs);
                        h.State.EndResourceResolution();
                    }
                }
                catch (Exception ex)
                {
                    holderError = ex;
                }
            })
            {
                IsBackground = true,
            };
            holder.Start();

            Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "gate holder did not enter");

            // Gate held: fail non-waiting with no change to any pool or result.
            Assert.That(h.Coordinator.TryRelease(record, out NvencFailedBeforeSubmitReleaseResult first), Is.False);
            Assert.That(first.IsValid, Is.False);
            Assert.That(h.State.IsPoisoned, Is.False);
            Assert.That(h.SampleSlots.IsActive(record.SampleSlot), Is.True);
            Assert.That(h.WorkSlots.OccupiedCount, Is.EqualTo(1));
            Assert.That(h.SampleSlots.OccupiedCount, Is.EqualTo(1));
            Assert.That(h.SubmitToOutputCredits.OccupiedCount, Is.EqualTo(1));
            Assert.That(h.FrameCompletionCredits.OccupiedCount, Is.EqualTo(1));

            release.Set();
            Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "gate holder did not exit");
            Assert.That(holderError, Is.Null);

            // Retry succeeds once the gate is free.
            Assert.That(h.Coordinator.TryRelease(record, out NvencFailedBeforeSubmitReleaseResult retry), Is.True);
            Assert.That(retry.IsValid, Is.True);
            Assert.That(retry.Matches(record, h.SampleSlots), Is.True);
            Assert.That(h.SampleSlots.IsActive(record.SampleSlot), Is.False);
        }

        [Test]
        public void Release_Poisoned_ReturnsNothing()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceFailedBeforeSubmit(
                1, NvencFailedBeforeSubmitReason.GpuConversionFailed, out NvencCaptureWorkSlotLease workLease, out NvencFrameCompletionCreditLease frameCredit);

            Assert.That(h.State.TryPoison(), Is.True);

            Assert.That(h.Coordinator.TryRelease(record, out NvencFailedBeforeSubmitReleaseResult result), Is.False);
            Assert.That(result.IsValid, Is.False);

            // Nothing returned.
            Assert.That(h.SampleSlots.IsActive(record.SampleSlot), Is.True);
            Assert.That(h.WorkSlots.IsActive(workLease), Is.True);
            Assert.That(h.SubmitToOutputCredits.IsActive(record.SubmitToOutputCredit), Is.True);
            Assert.That(h.FrameCompletionCredits.IsActive(frameCredit), Is.True);
        }

        [Test]
        public void Release_SubmittedOrDefaultRecord_RejectedWithoutPoolContact()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord submitted = h.ProduceSubmitted(1, out _, out _);

            Assert.That(h.Coordinator.TryRelease(submitted, out NvencFailedBeforeSubmitReleaseResult result), Is.False);
            Assert.That(result.IsValid, Is.False);
            Assert.That(h.State.IsPoisoned, Is.False);
            Assert.That(h.SampleSlots.IsActive(submitted.SampleSlot), Is.True);
            Assert.That(h.SampleSlots.OccupiedCount, Is.EqualTo(1));
            Assert.That(h.WorkSlots.OccupiedCount, Is.EqualTo(1));
            Assert.That(h.SubmitToOutputCredits.OccupiedCount, Is.EqualTo(1));
            Assert.That(h.FrameCompletionCredits.OccupiedCount, Is.EqualTo(1));

            // A default (None) record is also rejected without a pool contact.
            Assert.That(h.Coordinator.TryRelease(default, out _), Is.False);
            Assert.That(h.State.IsPoisoned, Is.False);
            Assert.That(h.SampleSlots.OccupiedCount, Is.EqualTo(1));
        }

        [Test]
        public void Release_ForeignSample_PoisonsWithoutGuessReturn()
        {
            Harness h = new Harness();
            Harness other = new Harness();

            Assert.That(h.WorkSlots.TryRent(out NvencCaptureWorkSlotLease hWork), Is.True);
            Assert.That(h.SampleSlots.TryRent(out NvencEncodeSampleSlotLease hSample), Is.True);
            Assert.That(h.SubmitToOutputCredits.TryRent(out NvencSubmitToOutputCreditLease hSubmit), Is.True);
            Assert.That(h.FrameCompletionCredits.TryRent(out NvencFrameCompletionCreditLease hFrame), Is.True);
            Assert.That(other.SampleSlots.TryRent(out NvencEncodeSampleSlotLease foreignSample), Is.True);

            NvencSubmitToOutputRecord record = Harness.BuildFailedRecord(
                1, hWork, foreignSample, hSubmit, hFrame, NvencFailedBeforeSubmitReason.GpuConversionFailed);

            Assert.Throws<InvalidOperationException>(() => h.Coordinator.TryRelease(record, out _));
            Assert.That(h.State.IsPoisoned, Is.True);

            // No guess return: both the host's own sample and the foreign sample stay active.
            Assert.That(h.SampleSlots.IsActive(hSample), Is.True);
            Assert.That(other.SampleSlots.IsActive(foreignSample), Is.True);
            Assert.That(h.SampleSlots.OccupiedCount, Is.EqualTo(1));
            Assert.That(other.SampleSlots.OccupiedCount, Is.EqualTo(1));
        }

        [Test]
        public void Release_ForeignWork_PoisonsWithoutGuessReturn()
        {
            Harness h = new Harness();
            Harness other = new Harness();

            Assert.That(other.WorkSlots.TryRent(out NvencCaptureWorkSlotLease foreignWork), Is.True);
            Assert.That(h.SampleSlots.TryRent(out NvencEncodeSampleSlotLease hSample), Is.True);
            Assert.That(h.SubmitToOutputCredits.TryRent(out NvencSubmitToOutputCreditLease hSubmit), Is.True);
            Assert.That(h.FrameCompletionCredits.TryRent(out NvencFrameCompletionCreditLease hFrame), Is.True);

            NvencSubmitToOutputRecord record = Harness.BuildFailedRecord(
                1, foreignWork, hSample, hSubmit, hFrame, NvencFailedBeforeSubmitReason.GpuConversionFailed);

            Assert.Throws<InvalidOperationException>(() => h.Coordinator.TryRelease(record, out _));
            Assert.That(h.State.IsPoisoned, Is.True);

            Assert.That(other.WorkSlots.IsActive(foreignWork), Is.True);
            Assert.That(h.SampleSlots.IsActive(hSample), Is.True);
            Assert.That(h.SampleSlots.OccupiedCount, Is.EqualTo(1));
        }

        [Test]
        public void Release_ForeignCredit_PoisonsWithoutGuessReturn()
        {
            Harness h = new Harness();
            Harness other = new Harness();

            Assert.That(h.WorkSlots.TryRent(out NvencCaptureWorkSlotLease hWork), Is.True);
            Assert.That(h.SampleSlots.TryRent(out NvencEncodeSampleSlotLease hSample), Is.True);
            Assert.That(other.SubmitToOutputCredits.TryRent(out NvencSubmitToOutputCreditLease foreignSubmit), Is.True);
            Assert.That(h.FrameCompletionCredits.TryRent(out NvencFrameCompletionCreditLease hFrame), Is.True);

            NvencSubmitToOutputRecord record = Harness.BuildFailedRecord(
                1, hWork, hSample, foreignSubmit, hFrame, NvencFailedBeforeSubmitReason.GpuConversionFailed);

            Assert.Throws<InvalidOperationException>(() => h.Coordinator.TryRelease(record, out _));
            Assert.That(h.State.IsPoisoned, Is.True);

            Assert.That(other.SubmitToOutputCredits.IsActive(foreignSubmit), Is.True);
            Assert.That(h.SampleSlots.IsActive(hSample), Is.True);
            Assert.That(h.SampleSlots.OccupiedCount, Is.EqualTo(1));
        }

        [Test]
        public void Release_DoubleRelease_NoResultReissue_Poisons()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceFailedBeforeSubmit(
                1, NvencFailedBeforeSubmitReason.GpuConversionFailed, out _, out _);

            Assert.That(h.Coordinator.TryRelease(record, out NvencFailedBeforeSubmitReleaseResult first), Is.True);
            Assert.That(first.IsValid, Is.True);
            Assert.That(h.SampleSlots.OccupiedCount, Is.EqualTo(0));

            // The same record cannot be released twice: no result reissue.
            NvencFailedBeforeSubmitReleaseResult second = default;
            Assert.Throws<InvalidOperationException>(
                () => h.Coordinator.TryRelease(record, out second));
            Assert.That(second.IsValid, Is.False);
            Assert.That(h.State.IsPoisoned, Is.True);

            // No double return: the sample stays returned, the other three stay active.
            Assert.That(h.SampleSlots.OccupiedCount, Is.EqualTo(0));
            Assert.That(h.WorkSlots.OccupiedCount, Is.EqualTo(1));
            Assert.That(h.SubmitToOutputCredits.OccupiedCount, Is.EqualTo(1));
            Assert.That(h.FrameCompletionCredits.OccupiedCount, Is.EqualTo(1));
        }

        [Test]
        public void Release_Success_WorkSubmitAndFrameStayActive()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceFailedBeforeSubmit(
                1, NvencFailedBeforeSubmitReason.GpuConversionFailed, out NvencCaptureWorkSlotLease workLease, out NvencFrameCompletionCreditLease frameCredit);

            Assert.That(h.Coordinator.TryRelease(record, out _), Is.True);

            Assert.That(h.WorkSlots.IsActive(workLease), Is.True);
            Assert.That(h.SubmitToOutputCredits.IsActive(record.SubmitToOutputCredit), Is.True);
            Assert.That(h.FrameCompletionCredits.IsActive(frameCredit), Is.True);
        }

        // -------------------------------------------------------------------
        // Integration with Frame Completion publish and Main Thread collect
        // -------------------------------------------------------------------

        [Test]
        public void Integration_ReleaseThenPublishThenCollect_AllFourResourcesExactlyOnce()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.ProduceFailedBeforeSubmit(
                7, NvencFailedBeforeSubmitReason.GpuConversionFailed, out NvencCaptureWorkSlotLease workLease, out NvencFrameCompletionCreditLease frameCredit);

            // 1. Coordinator releases the Sample Slot.
            Assert.That(h.Coordinator.TryRelease(record, out NvencFailedBeforeSubmitReleaseResult result), Is.True);
            Assert.That(h.SampleSlots.IsActive(record.SampleSlot), Is.False);
            Assert.That(h.WorkSlots.IsActive(workLease), Is.True);
            Assert.That(h.SubmitToOutputCredits.IsActive(record.SubmitToOutputCredit), Is.True);
            Assert.That(h.FrameCompletionCredits.IsActive(frameCredit), Is.True);

            // 2. Frame Completion publish returns the Submit-to-Output credit.
            Assert.That(h.Boundary.TryPublishFailedBeforeSubmit(record, result, out NvencFrameCompletionRecord published), Is.True);
            Assert.That(published.Status, Is.EqualTo(CaptureFrameCompletionStatus.Failed));
            Assert.That(published.Reason, Is.EqualTo(NvencFrameCompletionReason.GpuConversionFailed));
            Assert.That(h.SubmitToOutputCredits.IsActive(record.SubmitToOutputCredit), Is.False);
            Assert.That(h.WorkSlots.IsActive(workLease), Is.True);
            Assert.That(h.FrameCompletionCredits.IsActive(frameCredit), Is.True);

            // 3. Main Thread collect returns the Work Slot and Frame Completion credit.
            Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord collected), Is.True);
            Assert.That(collected.WorkToken.IdenticalTo(record.WorkToken), Is.True);
            Assert.That(h.WorkSlots.IsActive(workLease), Is.False);
            Assert.That(h.FrameCompletionCredits.IsActive(frameCredit), Is.False);

            // All four resources are collected exactly once.
            Assert.That(h.WorkSlots.OccupiedCount, Is.EqualTo(0));
            Assert.That(h.SampleSlots.OccupiedCount, Is.EqualTo(0));
            Assert.That(h.SubmitToOutputCredits.OccupiedCount, Is.EqualTo(0));
            Assert.That(h.FrameCompletionCredits.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void Integration_OtherRecordResult_CannotPublishCompletion()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord a = h.ProduceFailedBeforeSubmit(
                1, NvencFailedBeforeSubmitReason.GpuConversionFailed, out _, out _);
            NvencSubmitToOutputRecord b = h.ProduceFailedBeforeSubmit(
                2, NvencFailedBeforeSubmitReason.NvencSubmitFailed, out _, out _);

            Assert.That(h.Coordinator.TryRelease(a, out NvencFailedBeforeSubmitReleaseResult resultA), Is.True);
            Assert.That(h.Coordinator.TryRelease(b, out NvencFailedBeforeSubmitReleaseResult resultB), Is.True);

            // B's release result cannot publish a completion for A.
            Assert.Throws<InvalidOperationException>(
                () => h.Boundary.TryPublishFailedBeforeSubmit(a, resultB, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
        }

        // -------------------------------------------------------------------
        // Capacity and shape / source audits
        // -------------------------------------------------------------------

        [Test]
        public void CapacityEight_NinthNotConstructible_NoExtraArraysQueuesOrTokens()
        {
            Harness h = new Harness();

            for (int i = 0; i < 8; i++)
            {
                Assert.That(h.WorkSlots.TryRent(out _), Is.True);
                Assert.That(h.SampleSlots.TryRent(out _), Is.True);
                Assert.That(h.SubmitToOutputCredits.TryRent(out _), Is.True);
                Assert.That(h.FrameCompletionCredits.TryRent(out _), Is.True);
            }

            Assert.That(h.WorkSlots.TryRent(out NvencCaptureWorkSlotLease ninthWork), Is.False);
            Assert.That(ninthWork.IsValid, Is.False);
            Assert.That(h.SampleSlots.TryRent(out NvencEncodeSampleSlotLease ninthSample), Is.False);
            Assert.That(ninthSample.IsValid, Is.False);

            // The coordinator adds no arrays, queues, registries, or tokens.
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencFailedBeforeSubmitReleaseCoordinator.cs"));
            Assert.That(source, Does.Not.Contain("new []"));
            Assert.That(source, Does.Not.Contain("new List"));
            Assert.That(source, Does.Not.Contain("new Dictionary"));
            Assert.That(source, Does.Not.Contain("new Queue"));
            Assert.That(source, Does.Not.Contain("NvencFixedSpscQueue"));
            Assert.That(source, Does.Not.Contain("Guid.NewGuid"));
        }

        [Test]
        public void TypeShape_SealedNonDisposable_ExactDependencies_SingleEntry()
        {
            Type type = typeof(NvencFailedBeforeSubmitReleaseCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(fields.Length, Is.EqualTo(5));

            Type[] expected =
            {
                typeof(NvencCaptureProcessState),
                typeof(NvencCaptureWorkSlotPool),
                typeof(NvencEncodeSampleSlotPool),
                typeof(NvencSubmitToOutputCreditPool),
                typeof(NvencFrameCompletionCreditPool),
            };

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
                Assert.That(expected, Does.Contain(field.FieldType), field.Name + " has an unexpected type.");
            }

            // Single entry: TryRelease(in record, out result) -> bool.
            MethodInfo tryRelease = type.GetMethod(
                "TryRelease", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(tryRelease, Is.Not.Null);
            Assert.That(tryRelease.ReturnType, Is.EqualTo(typeof(bool)));

            ParameterInfo[] parameters = tryRelease.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(2));
            Assert.That(parameters[0].ParameterType.IsByRef, Is.True);
            Assert.That(parameters[0].ParameterType.GetElementType(), Is.EqualTo(typeof(NvencSubmitToOutputRecord)));
            Assert.That(parameters[0].IsIn, Is.True);
            Assert.That(parameters[1].ParameterType.IsByRef, Is.True);
            Assert.That(parameters[1].ParameterType.GetElementType(), Is.EqualTo(typeof(NvencFailedBeforeSubmitReleaseResult)));
            Assert.That(parameters[1].IsOut, Is.True);

            // Only TryRelease is exposed; everything else is private or a constructor.
            int exposed = 0;
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!method.IsPrivate && !method.IsSpecialName)
                {
                    exposed++;
                }
            }

            Assert.That(exposed, Is.EqualTo(1), "The coordinator must expose only TryRelease.");
        }

        [Test]
        public void Source_ForbiddenDependencies_Absent()
        {
            string directory = RuntimeDirectory();
            string source =
                File.ReadAllText(Path.Combine(directory, "NvencFailedBeforeSubmitReleaseCoordinator.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencFailedBeforeSubmitReleaseResult.cs"));

            string[] forbidden =
            {
                "lock (", "Monitor", "ManualResetEvent", "AutoResetEvent", "WaitHandle", "SpinWait",
                "new Thread", "ThreadPool", "Task", "File.", "Directory.", "FileStream", "DllImport",
                "UnityEngine", "Application.", "SystemInfo", "GraphicsDevice", "IntPtr", "SafeHandle",
                "NvEnc", "RenderTexture", "SHA", "MD5", "Hash", "H.264", "H264", "ArrayPool",
                "Enumerable", ".Select(", ".Where(", ".ToList(", ".ToArray(",
                "new Queue", "new Dictionary", "new List", "new HashSet", "new Stack", "new ArrayList",
                "NvencFrameCompletionBoundary", "NvencFrameCompletionRecord", "NvencOwnedAccessUnitBuffer",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word),
                    "FailedBeforeSubmit release source must not reference: " + word);
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

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
            internal NvencFailedBeforeSubmitReleaseCoordinator Coordinator;
            internal NvencFrameCompletionBoundary Boundary;

            internal Harness()
            {
                WorkSlots = new NvencCaptureWorkSlotPool(State);
                SampleSlots = new NvencEncodeSampleSlotPool(State);
                SubmitToOutputCredits = new NvencSubmitToOutputCreditPool(State);
                FrameCompletionCredits = new NvencFrameCompletionCreditPool(State);
                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Coordinator = new NvencFailedBeforeSubmitReleaseCoordinator(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits);
                Boundary = new NvencFrameCompletionBoundary(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer);
            }

            internal NvencSubmitToOutputRecord ProduceFailedBeforeSubmit(
                long frameId,
                NvencFailedBeforeSubmitReason reason,
                out NvencCaptureWorkSlotLease workLease,
                out NvencFrameCompletionCreditLease frameCredit)
            {
                RentAll(
                    out NvencCaptureWorkSlotLease work,
                    out NvencEncodeSampleSlotLease sample,
                    out NvencSubmitToOutputCreditLease submit,
                    out NvencFrameCompletionCreditLease frame);

                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Guid.NewGuid(), work.SlotIndex, work.Generation, 1, frameId);

                workLease = work;
                frameCredit = frame;
                return NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                    token, work, sample, submit, frame, reason);
            }

            internal NvencSubmitToOutputRecord ProduceSubmitted(
                long frameId,
                out NvencCaptureWorkSlotLease workLease,
                out NvencFrameCompletionCreditLease frameCredit)
            {
                RentAll(
                    out NvencCaptureWorkSlotLease work,
                    out NvencEncodeSampleSlotLease sample,
                    out NvencSubmitToOutputCreditLease submit,
                    out NvencFrameCompletionCreditLease frame);

                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Guid.NewGuid(), work.SlotIndex, work.Generation, 1, frameId);

                workLease = work;
                frameCredit = frame;
                return NvencSubmitToOutputRecord.CreateSubmitted(
                    token, work, sample, submit, frame);
            }

            internal static NvencSubmitToOutputRecord BuildFailedRecord(
                long frameId,
                NvencCaptureWorkSlotLease workLease,
                NvencEncodeSampleSlotLease sampleLease,
                NvencSubmitToOutputCreditLease submitCredit,
                NvencFrameCompletionCreditLease frameCredit,
                NvencFailedBeforeSubmitReason reason)
            {
                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Guid.NewGuid(), workLease.SlotIndex, workLease.Generation, 1, frameId);
                return NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                    token, workLease, sampleLease, submitCredit, frameCredit, reason);
            }

            private void RentAll(
                out NvencCaptureWorkSlotLease work,
                out NvencEncodeSampleSlotLease sample,
                out NvencSubmitToOutputCreditLease submit,
                out NvencFrameCompletionCreditLease frame)
            {
                Assert.That(WorkSlots.TryRent(out work), Is.True);
                Assert.That(SampleSlots.TryRent(out sample), Is.True);
                Assert.That(SubmitToOutputCredits.TryRent(out submit), Is.True);
                Assert.That(FrameCompletionCredits.TryRent(out frame), Is.True);
            }
        }
    }
}
