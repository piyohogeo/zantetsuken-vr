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
    /// Contract tests for the Phase 0.11 Submitted run-abandoned output recovery
    /// boundary. It safely recovers one Submitted record through the exact
    /// collector without appending, returns the Owned Access Unit to the exact
    /// buffer, and issues the exact recovery result. Uses a fake output source;
    /// no GPU, NVENC, worker, chunk appender, or native resource is used.
    /// </summary>
    public class NvencSubmittedOutputAbandonRecoveryCoordinatorContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        // -------------------------------------------------------------------
        // Recovery paths
        // -------------------------------------------------------------------

        [Test]
        public void Recover_CollectorSuccess_OwnedLeaseReturned_RecoveryResultIssued()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(7);

            Assert.That(h.Coordinator.TryRecover(record, out NvencRunAbandonedRecoveryResult result), Is.True);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Matches(record, h.SampleSlots, h.Buffer), Is.True);

            // Only the Sample Slot and the Owned Access Unit were recovered.
            Assert.That(h.SampleSlots.IsActive(record.SampleSlot), Is.False);
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));

            // Work Slot, Submit credit, and Frame credit stay active.
            Assert.That(h.WorkSlots.IsActive(record.WorkSlot), Is.True);
            Assert.That(h.SubmitToOutputCredits.IsActive(record.SubmitToOutputCredit), Is.True);
            Assert.That(h.FrameCompletionCredits.IsActive(record.FrameCompletionCredit), Is.True);

            // The collector ran the source exactly once and no sink was contacted.
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Recover_CollectorControlledFailure_CancelProof_RecoveryResultIssued()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(7);
            h.Source.Result = false;

            Assert.That(h.Coordinator.TryRecover(record, out NvencRunAbandonedRecoveryResult result), Is.True);

            Assert.That(result.IsValid, Is.True);
            Assert.That(result.Matches(record, h.SampleSlots, h.Buffer), Is.True);

            Assert.That(h.SampleSlots.IsActive(record.SampleSlot), Is.False);
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
            Assert.That(h.WorkSlots.IsActive(record.WorkSlot), Is.True);
            Assert.That(h.SubmitToOutputCredits.IsActive(record.SubmitToOutputCredit), Is.True);
            Assert.That(h.FrameCompletionCredits.IsActive(record.FrameCompletionCredit), Is.True);
        }

        [Test]
        public void Recover_GateContention_NoSourceContact_Retryable()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

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
            bool holderJoined = false;
            holder.Start();
            try
            {
                Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "gate holder did not enter");

                // Gate held: no source contact, no side effect, no overtake.
                Assert.That(h.Coordinator.TryRecover(record, out NvencRunAbandonedRecoveryResult first), Is.False);
                Assert.That(first.IsValid, Is.False);
                Assert.That(h.Source.CallCount, Is.EqualTo(0));
                Assert.That(h.SampleSlots.IsActive(record.SampleSlot), Is.True);
                Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
                Assert.That(h.State.IsPoisoned, Is.False);
            }
            finally
            {
                release.Set();
                holderJoined = holder.Join(WatchdogTimeoutMs);
            }

            Assert.That(holderJoined, Is.True, "gate holder did not exit");
            Assert.That(holderError, Is.Null);

            // Retry succeeds once the gate is free.
            Assert.That(h.Coordinator.TryRecover(record, out NvencRunAbandonedRecoveryResult retry), Is.True);
            Assert.That(retry.IsValid, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Recover_CollectorPendingResume_SourceCalledExactlyOnce()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

            ManualResetEventSlim sourceEntered = new ManualResetEventSlim(false);
            ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);
            Exception holderError = null;

            h.Source.SourceEntered = sourceEntered;
            h.Source.WaitForGateHeld = gateHeld;

            Thread holder = new Thread(() =>
            {
                try
                {
                    if (sourceEntered.Wait(WatchdogTimeoutMs))
                    {
                        if (h.State.TryBeginResourceResolution())
                        {
                            gateHeld.Set();
                            release.Wait(WatchdogTimeoutMs);
                            h.State.EndResourceResolution();
                        }
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
            bool holderJoined = false;
            holder.Start();
            try
            {
                // The source runs once; the post-source commit is deferred behind
                // the held gate, so the collector parks and the coordinator returns
                // false without overtaking.
                Assert.That(h.Coordinator.TryRecover(record, out NvencRunAbandonedRecoveryResult first), Is.False);
                Assert.That(first.IsValid, Is.False);
                Assert.That(h.Source.CallCount, Is.EqualTo(1));
            }
            finally
            {
                release.Set();
                holderJoined = holder.Join(WatchdogTimeoutMs);
            }

            Assert.That(holderJoined, Is.True, "holder did not exit");
            Assert.That(holderError, Is.Null);

            // Resume converges without re-contacting the source.
            Assert.That(h.Coordinator.TryRecover(record, out NvencRunAbandonedRecoveryResult retry), Is.True);
            Assert.That(retry.IsValid, Is.True);
            Assert.That(retry.Matches(record, h.SampleSlots, h.Buffer), Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
            Assert.That(h.SampleSlots.IsActive(record.SampleSlot), Is.False);
        }

        [Test]
        public void Recover_OwnedReturnGateContention_PendingHeldThenResumeConverges()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

            // Drive the collector to Succeeded directly: the owned lease is
            // issued, the sample slot is returned, and the buffer is SinkOwned.
            Assert.That(h.Collector.TryCollect(record, out NvencSubmittedOutputCollectResult collected), Is.True);
            Assert.That(collected.IsSucceeded, Is.True);
            NvencOwnedAccessUnitLease ownedLease = collected.OwnedLease;
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));

            // The coordinator now holds this single owned lease pending its
            // recovery return.
            ParkPending(h.Coordinator, record, ownedLease);

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
            bool holderJoined = false;
            holder.Start();
            try
            {
                Assert.That(entered.Wait(WatchdogTimeoutMs), Is.True, "gate holder did not enter");

                // Gate held: the owned return fails as Busy and the lease stays
                // parked; the collector and source are never re-contacted.
                Assert.That(h.Coordinator.TryRecover(record, out NvencRunAbandonedRecoveryResult first), Is.False);
                Assert.That(first.IsValid, Is.False);
                Assert.That(h.Source.CallCount, Is.EqualTo(1));
                Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));
                Assert.That(h.State.IsPoisoned, Is.False);
            }
            finally
            {
                release.Set();
                holderJoined = holder.Join(WatchdogTimeoutMs);
            }

            Assert.That(holderJoined, Is.True, "holder did not exit");
            Assert.That(holderError, Is.Null);

            // Resume: the same owned lease is returned exactly once and a single
            // recovery result is issued, without re-calling the source.
            Assert.That(h.Coordinator.TryRecover(record, out NvencRunAbandonedRecoveryResult retry), Is.True);
            Assert.That(retry.IsValid, Is.True);
            Assert.That(retry.Matches(record, h.SampleSlots, h.Buffer), Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
        }

        [Test]
        public void Recover_PendingWithDifferentRecord_Poisons()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord a = h.CreateSubmittedRecord(1);
            NvencSubmitToOutputRecord b = h.CreateSubmittedRecord(2);

            Assert.That(h.Collector.TryCollect(a, out NvencSubmittedOutputCollectResult collected), Is.True);
            ParkPending(h.Coordinator, a, collected.OwnedLease);

            Assert.Throws<InvalidOperationException>(() => h.Coordinator.TryRecover(b, out _));
            Assert.That(h.State.IsPoisoned, Is.True);

            // The parked owned lease is not guessed-returned.
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Recover_StaleOwnedLease_PoisonsWithoutGuessReturn()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

            Assert.That(h.Collector.TryCollect(record, out NvencSubmittedOutputCollectResult collected), Is.True);
            NvencOwnedAccessUnitLease ownedLease = collected.OwnedLease;
            ParkPending(h.Coordinator, record, ownedLease);

            // Return the owned lease out-of-band so the parked lease is stale.
            Assert.That(h.Buffer.Return(ownedLease), Is.True);
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));

            Assert.Throws<InvalidOperationException>(() => h.Coordinator.TryRecover(record, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
        }

        [Test]
        public void Recover_ForeignOwnedLease_PoisonsWithoutGuessReturn()
        {
            Harness h = new Harness();
            Harness other = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

            Assert.That(h.Collector.TryCollect(record, out NvencSubmittedOutputCollectResult collected), Is.True);

            // A foreign owned lease minted by another buffer.
            NvencSubmitToOutputRecord otherRecord = other.CreateSubmittedRecord(2);
            Assert.That(other.Collector.TryCollect(otherRecord, out NvencSubmittedOutputCollectResult otherCollected), Is.True);
            NvencOwnedAccessUnitLease foreignLease = otherCollected.OwnedLease;

            ParkPending(h.Coordinator, record, foreignLease);

            Assert.Throws<InvalidOperationException>(() => h.Coordinator.TryRecover(record, out _));
            Assert.That(h.State.IsPoisoned, Is.True);

            // The foreign buffer is untouched by this coordinator.
            Assert.That(other.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));
        }

        [Test]
        public void Recover_NonSubmittedOrDefault_RejectedWithoutSideEffect()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord failed = h.CreateFailedBeforeSubmitRecord(1);

            Assert.That(h.Coordinator.TryRecover(failed, out _), Is.False);
            Assert.That(h.Source.CallCount, Is.EqualTo(0));
            Assert.That(h.State.IsPoisoned, Is.False);
            Assert.That(h.SampleSlots.IsActive(failed.SampleSlot), Is.True);

            Assert.That(h.Coordinator.TryRecover(default, out _), Is.False);
            Assert.That(h.Source.CallCount, Is.EqualTo(0));
            Assert.That(h.State.IsPoisoned, Is.False);
        }

        [Test]
        public void Recover_SourceException_PropagatesPoisonsNoResult()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);
            InvalidOperationException boom = new InvalidOperationException("boom");
            h.Source.ExceptionToThrow = boom;

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => h.Coordinator.TryRecover(record, out _));

            Assert.That(ReferenceEquals(thrown, boom), Is.True);
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
        }

        // -------------------------------------------------------------------
        // Integration with Frame Completion publish
        // -------------------------------------------------------------------

        [Test]
        public void Recover_IssuedResult_UsableForPublishRunAbandoned()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

            Assert.That(h.Coordinator.TryRecover(record, out NvencRunAbandonedRecoveryResult result), Is.True);

            Assert.That(h.Boundary.TryPublishRunAbandoned(record, result, out NvencFrameCompletionRecord published), Is.True);
            Assert.That(published.Status, Is.EqualTo(CaptureFrameCompletionStatus.Cancelled));
            Assert.That(published.Reason, Is.EqualTo(NvencFrameCompletionReason.CancelledAfterRunAbandoned));
        }

        [Test]
        public void Recover_OtherRecordResult_CannotPublishCompletion()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord a = h.CreateSubmittedRecord(1);
            NvencSubmitToOutputRecord b = h.CreateSubmittedRecord(2);

            Assert.That(h.Coordinator.TryRecover(a, out NvencRunAbandonedRecoveryResult resultA), Is.True);
            Assert.That(h.Coordinator.TryRecover(b, out NvencRunAbandonedRecoveryResult resultB), Is.True);

            Assert.Throws<InvalidOperationException>(
                () => h.Boundary.TryPublishRunAbandoned(a, resultB, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
        }

        [Test]
        public void Recover_IssuedResult_InvalidatedByRereservation()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

            Assert.That(h.Coordinator.TryRecover(record, out NvencRunAbandonedRecoveryResult result), Is.True);
            Assert.That(result.Matches(record, h.SampleSlots, h.Buffer), Is.True);

            // Re-reserve the same work token: the recovery nonce rotates.
            Assert.That(h.Buffer.TryBeginWrite(record.WorkToken, out _), Is.True);

            // The old proof can no longer certify recovery for this record.
            Assert.That(result.Matches(record, h.SampleSlots, h.Buffer), Is.False);
        }

        // -------------------------------------------------------------------
        // Shape and source audits
        // -------------------------------------------------------------------

        [Test]
        public void CoordinatorShape_SealedNonDisposable_ExactDependencies_SingleEntry()
        {
            Type type = typeof(NvencSubmittedOutputAbandonRecoveryCoordinator);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(fields.Length, Is.EqualTo(7));

            Type[] expectedTypes =
            {
                typeof(NvencCaptureProcessState),
                typeof(NvencSubmittedOutputCollector),
                typeof(NvencEncodeSampleSlotPool),
                typeof(NvencOwnedAccessUnitBuffer),
                typeof(bool),
                typeof(NvencSubmitToOutputRecord),
                typeof(NvencOwnedAccessUnitLease),
            };

            foreach (FieldInfo field in fields)
            {
                Assert.That(expectedTypes, Does.Contain(field.FieldType), field.Name + " has an unexpected type.");

                bool isDependency = field.FieldType == typeof(NvencCaptureProcessState) ||
                    field.FieldType == typeof(NvencSubmittedOutputCollector) ||
                    field.FieldType == typeof(NvencEncodeSampleSlotPool) ||
                    field.FieldType == typeof(NvencOwnedAccessUnitBuffer);

                if (isDependency)
                {
                    Assert.That(field.IsInitOnly, Is.True, field.Name + " dependency must be readonly.");
                }
            }

            // Single entry: TryRecover(in record, out result) -> bool.
            MethodInfo tryRecover = type.GetMethod(
                "TryRecover", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(tryRecover, Is.Not.Null);
            Assert.That(tryRecover.ReturnType, Is.EqualTo(typeof(bool)));

            ParameterInfo[] parameters = tryRecover.GetParameters();
            Assert.That(parameters.Length, Is.EqualTo(2));
            Assert.That(parameters[0].ParameterType.IsByRef, Is.True);
            Assert.That(parameters[0].ParameterType.GetElementType(), Is.EqualTo(typeof(NvencSubmitToOutputRecord)));
            Assert.That(parameters[0].IsIn, Is.True);
            Assert.That(parameters[1].ParameterType.IsByRef, Is.True);
            Assert.That(parameters[1].ParameterType.GetElementType(), Is.EqualTo(typeof(NvencRunAbandonedRecoveryResult)));
            Assert.That(parameters[1].IsOut, Is.True);

            int exposed = 0;
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!method.IsPrivate && !method.IsSpecialName)
                {
                    exposed++;
                }
            }

            Assert.That(exposed, Is.EqualTo(1), "The coordinator must expose only TryRecover.");
        }

        [Test]
        public void Source_NoAllocationNoThreadNoIoNoNativeNoCompletionNoSink()
        {
            string directory = RuntimeDirectory();
            string source =
                File.ReadAllText(Path.Combine(directory, "NvencSubmittedOutputAbandonRecoveryCoordinator.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencSubmittedOutputCollectResult.cs"));

            string[] forbidden =
            {
                "lock (", "Monitor", "ManualResetEvent", "AutoResetEvent", "WaitHandle", "SpinWait",
                "new Thread", "ThreadPool", "Task", "File.", "Directory.", "FileStream", "DllImport",
                "UnityEngine", "Application.", "SystemInfo", "GraphicsDevice", "IntPtr", "SafeHandle",
                "NvEnc", "RenderTexture", "SHA", "MD5", "Hash", "H.264", "H264", "ArrayPool",
                "Enumerable", ".Select(", ".Where(", ".ToList(", ".ToArray(",
                "new Queue", "new Dictionary", "new List", "new HashSet", "new Stack", "new ArrayList",
                "new byte[", "new long[", "new int[", "new []", "Guid.NewGuid",
                "NvencFrameCompletionBoundary", "NvencFrameCompletionRecord", "NvencRunChunkSink", "INvencRunChunkAppender",
                "NvencCaptureWorkSlotPool", "NvencSubmitToOutputCreditPool", "NvencFrameCompletionCreditPool",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word),
                    "Abandon recovery source must not reference: " + word);
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        private static void ParkPending(
            NvencSubmittedOutputAbandonRecoveryCoordinator coordinator,
            NvencSubmitToOutputRecord record,
            NvencOwnedAccessUnitLease ownedLease)
        {
            SetField(coordinator, "_pending", true);
            SetField(coordinator, "_pendingRecord", record);
            SetField(coordinator, "_pendingOwnedLease", ownedLease);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        private sealed class FakeOutputSource : INvencOutputBitstreamSource
        {
            internal int CallCount;
            internal bool Result = true;
            internal int ResultLength = 1024;
            internal Exception ExceptionToThrow;
            internal ManualResetEventSlim SourceEntered;
            internal ManualResetEventSlim WaitForGateHeld;

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                CallCount++;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (SourceEntered != null)
                {
                    SourceEntered.Set();
                }

                if (WaitForGateHeld != null)
                {
                    WaitForGateHeld.Wait(WatchdogTimeoutMs);
                }

                validLength = ResultLength;
                return Result;
            }
        }

        private sealed class Harness
        {
            internal NvencCaptureProcessState State;
            internal NvencCaptureWorkSlotPool WorkSlots;
            internal NvencEncodeSampleSlotPool SampleSlots;
            internal NvencSubmitToOutputCreditPool SubmitToOutputCredits;
            internal NvencFrameCompletionCreditPool FrameCompletionCredits;
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal FakeOutputSource Source;
            internal NvencSubmittedOutputCollector Collector;
            internal NvencSubmittedOutputAbandonRecoveryCoordinator Coordinator;
            internal NvencFrameCompletionBoundary Boundary;

            internal Harness()
            {
                State = new NvencCaptureProcessState();
                WorkSlots = new NvencCaptureWorkSlotPool(State);
                SampleSlots = new NvencEncodeSampleSlotPool(State);
                SubmitToOutputCredits = new NvencSubmitToOutputCreditPool(State);
                FrameCompletionCredits = new NvencFrameCompletionCreditPool(State);
                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Source = new FakeOutputSource();
                Collector = new NvencSubmittedOutputCollector(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer, Source);
                Coordinator = new NvencSubmittedOutputAbandonRecoveryCoordinator(
                    State, Collector, SampleSlots, Buffer);
                Boundary = new NvencFrameCompletionBoundary(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer);
            }

            internal NvencSubmitToOutputRecord CreateSubmittedRecord(long frameId)
            {
                Assert.That(WorkSlots.TryRent(out NvencCaptureWorkSlotLease workSlot), Is.True);
                Assert.That(SampleSlots.TryRent(out NvencEncodeSampleSlotLease sampleSlot), Is.True);
                Assert.That(SubmitToOutputCredits.TryRent(out NvencSubmitToOutputCreditLease submitCredit), Is.True);
                Assert.That(FrameCompletionCredits.TryRent(out NvencFrameCompletionCreditLease frameCredit), Is.True);

                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Guid.NewGuid(), workSlot.SlotIndex, workSlot.Generation, 1, frameId);
                return NvencSubmitToOutputRecord.CreateSubmitted(
                    token, workSlot, sampleSlot, submitCredit, frameCredit);
            }

            internal NvencSubmitToOutputRecord CreateFailedBeforeSubmitRecord(long frameId)
            {
                Assert.That(WorkSlots.TryRent(out NvencCaptureWorkSlotLease workSlot), Is.True);
                Assert.That(SampleSlots.TryRent(out NvencEncodeSampleSlotLease sampleSlot), Is.True);
                Assert.That(SubmitToOutputCredits.TryRent(out NvencSubmitToOutputCreditLease submitCredit), Is.True);
                Assert.That(FrameCompletionCredits.TryRent(out NvencFrameCompletionCreditLease frameCredit), Is.True);

                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Guid.NewGuid(), workSlot.SlotIndex, workSlot.Generation, 1, frameId);
                return NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                    token, workSlot, sampleSlot, submitCredit, frameCredit,
                    NvencFailedBeforeSubmitReason.NvencSubmitFailed);
            }
        }
    }
}
