using System;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using NvencAccessUnitCopyStatus = Zantetsu.Observability.NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 Run chunk terminal request boundary:
    /// the fixed one-slot Finalize/Abandon request that the Output Worker
    /// advances exactly once and the Main Thread collects exactly once without
    /// waiting. Uses fakes and a deterministic park signal; no GPU, NVENC,
    /// native resource, real-time sleep, or short negative wait is used.
    /// </summary>
    public class NvencRunChunkTerminalRequestContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Test]
        public void Finalize_ExecutesOnWorkerThread_NotCallerThread()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                Assert.That(h.Finalizer.CallCount, Is.EqualTo(1));
                Assert.That(h.Finalizer.ExecutingThreadName, Is.EqualTo(NvencOrderedOutputWorkerService.WorkerThreadName));
                Assert.That(h.Worker.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
                Assert.That(outcome.IsFinalized, Is.True);
            }
        }

        [Test]
        public void Abandon_ExecutesOnWorkerThread_NoResult()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestAbandon(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the abandon request");

                Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Abandoned));
                Assert.That(h.Finalizer.CallCount, Is.EqualTo(0));

                Assert.That(h.Worker.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
                Assert.That(outcome.IsAbandoned, Is.True);
                Assert.That(outcome.Result, Is.Null);
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);
            }
        }

        [Test]
        public void ProcessorHoldingWork_DoesNotTouchTerminal()
        {
            using (Harness h = Harness.Create())
            {
                NvencSubmitToOutputRecord record = h.CreateSubmitted(1);
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                // Park the processor mid-record behind the sink writer.
                ManualResetEventSlim writerEntered = new ManualResetEventSlim(false);
                ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
                ManualResetEventSlim release = new ManualResetEventSlim(false);
                h.Writer.Entered = writerEntered;
                h.Writer.WaitForGateHeld = gateHeld;

                Thread holder = new Thread(() =>
                {
                    if (writerEntered.Wait(WatchdogTimeoutMs))
                    {
                        if (h.State.TryBeginResourceResolution())
                        {
                            gateHeld.Set();
                            release.Wait(WatchdogTimeoutMs);
                            h.State.EndResourceResolution();
                        }
                    }
                })
                {
                    IsBackground = true,
                };
                holder.Start();

                h.Enqueue(record);

                // Accepting notifies the worker; the worker dequeues the record
                // and blocks in the sink, so the terminal must not be touched
                // while the processor holds the current record.
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                Assert.That(gateHeld.Wait(WatchdogTimeoutMs), Is.True, "worker did not reach the sink");
                Assert.That(h.Finalizer.CallCount, Is.EqualTo(0));
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);

                release.Set();
                Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "holder did not exit");

                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not converge after release");

                Assert.That(h.Boundary.TryCollect(out NvencFrameCompletionRecord completion), Is.True);
                Assert.That(completion.Status, Is.EqualTo(CaptureFrameCompletionStatus.Succeeded));
                Assert.That(h.Finalizer.CallCount, Is.EqualTo(1));
                Assert.That(h.Worker.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
                Assert.That(outcome.IsFinalized, Is.True);
            }
        }

        [Test]
        public void Finalize_AfterDrain_ExactlyOnce_ExactResultOnce()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                h.AcceptAndAppendChunk(2, 48, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                Assert.That(h.Finalizer.CallCount, Is.EqualTo(1));
                Assert.That(h.Worker.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
                Assert.That(outcome.IsFinalized, Is.True);

                Assert.That(h.Context.TryGetFinalizationResult(out NvencChunkFinalizationResult held), Is.True);
                Assert.That(ReferenceEquals(outcome.Result, held), Is.True);

                // Collected exactly once.
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);
            }
        }

        [Test]
        public void Request_RejectedRunningPoisonedDoubleCross()
        {
            using (Harness h = Harness.Create())
            {
                // Running rejects both kinds.
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);

                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                // Finalize accepts exactly once; double and cross are rejected.
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);
            }

            using (Harness h = Harness.Create())
            {
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                // Abandon accepts exactly once; double and cross are rejected.
                Assert.That(h.Worker.TryRequestAbandon(), Is.True);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
            }

            using (Harness h = Harness.Create())
            {
                // Poisoned rejects both kinds.
                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);
            }
        }

        [Test]
        public void Request_RejectedBeforeSubmitWorkerDrainCompleted()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);

                // Draining but the Submit Worker drain evidence is not yet
                // published: the terminal must not be accepted.
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);

                // Publish the monotonic drain evidence: acceptance now works.
                h.SubmitDrained = true;
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
            }
        }

        [Test]
        public void Request_AfterCompletedOrCollected_Rejected()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                // Completed, not yet collected: no new request accepted.
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);

                Assert.That(h.Worker.TryCollectTerminal(out _), Is.True);

                // Collected: still no new request accepted.
                Assert.That(h.Worker.TryRequestFinalize(), Is.False);
                Assert.That(h.Worker.TryRequestAbandon(), Is.False);
            }
        }

        [Test]
        public void WorkerBoundToExactContext_DoesNotTouchForeignContext()
        {
            using (Harness h = Harness.Create())
            {
                // A separate foreign Run chunk context that no worker is bound
                // to. It must never be finalized or abandoned by worker A.
                NvencOwnedAccessUnitBuffer foreignBuffer = new NvencOwnedAccessUnitBuffer(h.State);
                FakeFinalizer foreignFinalizer = new FakeFinalizer();
                NvencRunChunkSink foreignSink = new NvencRunChunkSink(h.State, foreignBuffer, foreignFinalizer);
                NvencRunChunkFinalizationCoordinator foreignCoordinator = new NvencRunChunkFinalizationCoordinator(foreignFinalizer);
                NvencRunChunkContext foreignContext = new NvencRunChunkContext(MakeIssue(), foreignSink, foreignCoordinator, "chunk/foreign");

                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                Assert.That(h.Worker.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
                Assert.That(outcome.IsFinalized, Is.True);
                Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Finalized));

                // The foreign context is untouched: no finalizer contact and
                // still Open.
                Assert.That(foreignFinalizer.CallCount, Is.EqualTo(0));
                Assert.That(foreignContext.State, Is.EqualTo(NvencRunChunkContextState.Open));
            }
        }

        [Test]
        public void FinalizeFalse_KeepsRequest_RetryOnNextNotifyConverges()
        {
            using (Harness h = Harness.Create())
            {
                // Two accepted frames but only one append: the finalize
                // pre-verification fails before any finalizer contact.
                Assert.That(h.Context.TryRecordAcceptedFrame(1), Is.True);
                Assert.That(h.Context.TryRecordAcceptedFrame(2), Is.True);
                h.AppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not park with the held request");

                // Request held, finalizer never contacted, nothing published.
                Assert.That(h.Finalizer.CallCount, Is.EqualTo(0));
                Assert.That(h.Context.State, Is.EqualTo(NvencRunChunkContextState.Open));
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);

                // Catch the sink up and re-notify: the held request converges.
                h.AppendChunk(2, 48, Seed);
                h.SettledEvent.Reset();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not converge after the sink caught up");

                Assert.That(h.Finalizer.CallCount, Is.EqualTo(1));
                Assert.That(h.Worker.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
                Assert.That(outcome.IsFinalized, Is.True);
            }
        }

        [Test]
        public void FinalizerException_FatalPoisonStopNoTerminal()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                InvalidOperationException boom = new InvalidOperationException("boom");
                h.Finalizer.ExceptionToThrow = boom;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not stop after the finalizer exception");
                h.WaitForPhysicalStop("worker did not physically exit");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Worker.TryGetFailure(out Exception captured), Is.True);
                Assert.That(ReferenceEquals(captured, boom), Is.True);
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);
            }
        }

        [Test]
        public void AbandonRequest_ContextAlreadyTerminal_InvariantFatal()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);

                // Finalize directly on the caller thread: the accepted abandon
                // request can no longer abandon the context.
                Assert.That(h.Context.TryFinalize(out _), Is.True);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestAbandon(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not stop after the abandon invariant failure");
                h.WaitForPhysicalStop("worker did not physically exit");

                Assert.That(h.State.IsPoisoned, Is.True);
                Assert.That(h.Worker.TryGetFailure(out Exception captured), Is.True);
                Assert.That(captured, Is.TypeOf<InvalidOperationException>());
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);
            }
        }

        [Test]
        public void TerminalCompletionAlone_DoesNotStopWorker()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                WaitSettled(h.SettledEvent, "worker did not converge the finalize request");

                Assert.That(h.Worker.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
                Assert.That(outcome.IsFinalized, Is.True);

                // Terminal completion alone never stops the worker: it stays
                // alive waiting for an external teardown request.
                Assert.That(h.Worker.IsStopped, Is.False);
            }
            // Dispose is the teardown request: it poisons and physically stops
            // the worker.
        }

        [Test]
        public void RequestNotify_IsNeverMissed()
        {
            using (Harness h = Harness.Create())
            {
                h.AcceptAndAppendChunk(1, 64, Seed);
                Assert.That(h.State.TryBeginDrain(), Is.True);
                h.SubmitDrained = true;

                // The worker is parked; the acceptance's own notify must wake it
                // even when further notifies coalesce.
                h.SettledEvent.Reset();
                Assert.That(h.Worker.TryRequestFinalize(), Is.True);
                h.Worker.Notify();
                h.Worker.Notify();
                WaitSettled(h.SettledEvent, "worker did not converge the request");

                Assert.That(h.Finalizer.CallCount, Is.EqualTo(1));
                Assert.That(h.Worker.TryCollectTerminal(out NvencRunChunkTerminalOutcome outcome), Is.True);
                Assert.That(outcome.IsFinalized, Is.True);
                Assert.That(h.Worker.TryCollectTerminal(out _), Is.False);
            }
        }

        [Test]
        public void Outcome_DefaultIsNone_NotAbandoned()
        {
            NvencRunChunkTerminalOutcome none = default;

            Assert.That(none.IsNone, Is.True);
            Assert.That(none.IsFinalized, Is.False);
            Assert.That(none.IsAbandoned, Is.False);
            Assert.That(none.Result, Is.Null);
        }

        [Test]
        public void Source_NoExtraQueueTaskThreadPoolTimerSleepSpinTokenProof()
        {
            string request = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencRunChunkTerminalRequest.cs"));

            string[] requestForbidden =
            {
                "new Queue", "new List", "new Dictionary", "Task", "ThreadPool",
                "Timer", "Thread.Sleep", "SpinWait", "Token", "Proof", "Nonce",
                "new Thread", "lock (" + ")", "Monitor",
            };

            foreach (string word in requestForbidden)
            {
                Assert.That(request, Does.Not.Contain(word), "terminal request source must not contain: " + word);
            }

            string service = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOrderedOutputWorkerService.cs"));
            Assert.That(CountOccurrences(service, "new Thread("), Is.EqualTo(1),
                "the service must create exactly one worker thread.");
            Assert.That(CountOccurrences(service, "_signal.Wait()"), Is.EqualTo(1),
                "the worker must have exactly one blocking wait.");

            string[] serviceForbidden =
            {
                "ThreadPool", "Task", "Thread.Sleep", "Timer", "SpinWait",
                "new Queue", "new List", "new Dictionary",
            };

            foreach (string word in serviceForbidden)
            {
                Assert.That(service, Does.Not.Contain(word), "service source must not contain: " + word);
            }
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private static void WaitSettled(ManualResetEventSlim settled, string message)
        {
            Assert.That(settled.Wait(WatchdogTimeoutMs), Is.True, message);
        }

        private static int CountOccurrences(string text, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = text.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
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

        private sealed class FakeFinalizer : INvencRunChunkAppender, INvencRunChunkFinalizer
        {
            private int _callCount;
            internal Exception ExceptionToThrow;
            internal string ExecutingThreadName;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
                Interlocked.Increment(ref _callCount);
                ExecutingThreadName = Thread.CurrentThread.Name;

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

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

        private sealed class FakeOutputSource : INvencOutputBitstreamSource
        {
            private int _callCount;
            internal bool Result = true;
            internal int ResultLength = 1024;
            internal Exception ExceptionToThrow;
            internal ManualResetEventSlim SourceEntered;
            internal ManualResetEventSlim WaitForGateHeld;

            internal int CallCount => Volatile.Read(ref _callCount);

            internal string ExecutingThreadName;

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                Interlocked.Increment(ref _callCount);
                ExecutingThreadName = Thread.CurrentThread.Name;

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

        private sealed class FakeAppender : INvencRunChunkAppender
        {
            private int _appendCount;
            internal NvencRunChunkAppendOutcome Outcome = NvencRunChunkAppendOutcome.Appended;
            internal Exception ExceptionToThrow;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim WaitForGateHeld;

            internal int AppendCount => Volatile.Read(ref _appendCount);

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                Interlocked.Increment(ref _appendCount);

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (Entered != null)
                {
                    Entered.Set();
                }

                if (WaitForGateHeld != null)
                {
                    WaitForGateHeld.Wait(WatchdogTimeoutMs);
                }

                return Outcome;
            }
        }

        private sealed class Harness : IDisposable
        {
            internal NvencCaptureProcessState State;
            internal NvencCaptureWorkSlotPool WorkSlots;
            internal NvencEncodeSampleSlotPool SampleSlots;
            internal NvencSubmitToOutputCreditPool SubmitToOutputCredits;
            internal NvencFrameCompletionCreditPool FrameCompletionCredits;
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal FakeOutputSource Source;
            internal FakeAppender Writer;
            internal NvencSubmittedOutputCollector Collector;
            internal NvencRunChunkSink Sink;
            internal NvencFailedBeforeSubmitReleaseCoordinator ReleaseCoordinator;
            internal NvencSubmittedOutputAbandonRecoveryCoordinator RecoveryCoordinator;
            internal NvencFrameCompletionBoundary Boundary;
            internal NvencFixedSpscQueue<NvencSubmitToOutputRecord> OutputQueue;
            internal NvencOrderedOutputProcessor Processor;
            internal NvencOrderedOutputWorkerService Worker;
            internal ManualResetEventSlim SettledEvent;

            internal NvencOwnedAccessUnitBuffer ChunkBuffer;
            internal FakeFinalizer Finalizer;
            internal NvencRunChunkSink ChunkSink;
            internal NvencRunChunkFinalizationCoordinator Coordinator;
            internal NvencRunChunkContext Context;

            // Monotonic Submit Worker drain evidence injected into the worker;
            // the tests publish it before accepting a terminal request.
            internal bool SubmitDrained;

            private readonly Action _settledHandler;

            internal Harness()
            {
                State = new NvencCaptureProcessState();

                // Build the exact Run chunk context first: the worker is bound
                // to it at construction.
                ChunkBuffer = new NvencOwnedAccessUnitBuffer(State);
                Finalizer = new FakeFinalizer();
                ChunkSink = new NvencRunChunkSink(State, ChunkBuffer, Finalizer);
                Coordinator = new NvencRunChunkFinalizationCoordinator(Finalizer);
                Context = new NvencRunChunkContext(MakeIssue(), ChunkSink, Coordinator, "chunk/0");

                WorkSlots = new NvencCaptureWorkSlotPool(State);
                SampleSlots = new NvencEncodeSampleSlotPool(State);
                SubmitToOutputCredits = new NvencSubmitToOutputCreditPool(State);
                FrameCompletionCredits = new NvencFrameCompletionCreditPool(State);
                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Source = new FakeOutputSource();
                Writer = new FakeAppender();
                Collector = new NvencSubmittedOutputCollector(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer, Source);
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
                ReleaseCoordinator = new NvencFailedBeforeSubmitReleaseCoordinator(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits);
                RecoveryCoordinator = new NvencSubmittedOutputAbandonRecoveryCoordinator(
                    State, Collector, SampleSlots, Buffer);
                Boundary = new NvencFrameCompletionBoundary(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer);
                OutputQueue = new NvencFixedSpscQueue<NvencSubmitToOutputRecord>();
                Processor = new NvencOrderedOutputProcessor(
                    State, OutputQueue, Collector, Sink, ReleaseCoordinator, RecoveryCoordinator, Boundary);
                Worker = new NvencOrderedOutputWorkerService(State, Processor, Context, () => SubmitDrained);

                SettledEvent = new ManualResetEventSlim(false);
                _settledHandler = () => SettledEvent.Set();
                Worker.Settled += _settledHandler;
            }

            internal static Harness Create()
            {
                Harness h = new Harness();

                // Deterministically park the worker once before returning.
                h.SettledEvent.Reset();
                h.Worker.Notify();
                Assert.That(h.SettledEvent.Wait(WatchdogTimeoutMs), Is.True, "worker did not settle initially");

                return h;
            }

            internal void AcceptAndAppendChunk(long frameId, int length, byte seed)
            {
                Assert.That(Context.TryRecordAcceptedFrame(frameId), Is.True);
                AppendChunk(frameId, length, seed);
            }

            internal void AppendChunk(long frameId, int length, byte seed)
            {
                CaptureFrameWorkToken token = MakeToken(frameId);
                Assert.That(ChunkBuffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
                Assert.That(ChunkBuffer.TryCopyCompletedOutput(write, default, new PatternSource(length, seed), out _),
                    Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
                Assert.That(ChunkBuffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease lease), Is.True);
                Assert.That(ChunkSink.TryAppend(token, lease, out _), Is.True);
            }

            internal NvencSubmitToOutputRecord CreateSubmitted(long frameId)
            {
                RentAll(
                    out NvencCaptureWorkSlotLease work,
                    out NvencEncodeSampleSlotLease sample,
                    out NvencSubmitToOutputCreditLease submit,
                    out NvencFrameCompletionCreditLease frame);

                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Guid.NewGuid(), work.SlotIndex, work.Generation, 1, frameId);
                return NvencSubmitToOutputRecord.CreateSubmitted(token, work, sample, submit, frame);
            }

            internal void Enqueue(NvencSubmitToOutputRecord record)
            {
                Assert.That(OutputQueue.TryEnqueue(record), Is.True);
            }

            internal void WaitForPhysicalStop(string message)
            {
                Thread workerThread = GetWorkerThread();
                if (workerThread != null)
                {
                    Assert.That(workerThread.Join(WatchdogTimeoutMs), Is.True, message);
                }
            }

            private Thread GetWorkerThread()
            {
                FieldInfo field = typeof(NvencOrderedOutputWorkerService).GetField(
                    "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
                return (Thread)field?.GetValue(Worker);
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

            public void Dispose()
            {
                if (!Worker.IsStopped)
                {
                    State.TryPoison();
                    Worker.Notify();
                }

                WaitForPhysicalStop("worker thread did not physically exit during teardown");

                Worker.Dispose();
                Worker.Settled -= _settledHandler;
                SettledEvent.Dispose();
            }
        }
    }
}
