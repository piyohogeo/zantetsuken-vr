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
    /// Contract tests for the single fixed Access Unit region (D-147) and its
    /// one-way Free → CollectorOwned → SinkOwned → Free ownership boundary.
    /// Uses a compact fixture: the 16 MiB storage is allocated once per buffer,
    /// no GPU, NVENC, or native resource is touched.
    /// </summary>
    public class NvencOwnedAccessUnitBufferContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        [Test]
        public void Capacity_EqualsProfileMaxAccessUnitLength()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(NvencBringUpProfileV1.MaxAccessUnitByteLength, Is.EqualTo(16L * 1024L * 1024L));
            Assert.That(buffer.Capacity, Is.EqualTo((int)NvencBringUpProfileV1.MaxAccessUnitByteLength));
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
        }

        [Test]
        public void BeginWrite_OnlySucceedsWhenFree_OneOutstandingAccessUnit()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(write.IsValid, Is.True);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));

            Assert.That(buffer.TryBeginWrite(MakeToken(2), out NvencAccessUnitWriteLease second), Is.False);
            Assert.That(second.IsValid, Is.False);
        }

        [Test]
        public void CopyLength_BoundaryOneAndMax_Accepted()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            FieldInfo validLengthField = typeof(NvencOwnedAccessUnitBuffer).GetField(
                "_validLength", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(validLengthField, Is.Not.Null);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write1), Is.True);
            Assert.That(buffer.TryCopyCompletedOutput(write1, default, new FixedLengthSource(1)), Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
            Assert.That(buffer.TryTransferToSink(write1, out NvencOwnedAccessUnitLease owned1), Is.True);
            Assert.That(validLengthField.GetValue(buffer), Is.EqualTo(1));
            Assert.That(buffer.Return(owned1), Is.True);

            Assert.That(buffer.TryBeginWrite(MakeToken(2), out NvencAccessUnitWriteLease write2), Is.True);
            Assert.That(buffer.TryCopyCompletedOutput(write2, default, new FixedLengthSource(buffer.Capacity)), Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
            Assert.That(buffer.TryTransferToSink(write2, out NvencOwnedAccessUnitLease owned2), Is.True);
            Assert.That(validLengthField.GetValue(buffer), Is.EqualTo(buffer.Capacity));
        }

        [Test]
        public void CopyLength_ZeroNegativeAndOverMax_RejectedWithoutStateChange()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryCopyCompletedOutput(write, default, new FixedLengthSource(0)), Is.EqualTo(NvencAccessUnitCopyStatus.Rejected));
            Assert.That(buffer.TryCopyCompletedOutput(write, default, new FixedLengthSource(-1)), Is.EqualTo(NvencAccessUnitCopyStatus.Rejected));
            Assert.That(buffer.TryCopyCompletedOutput(write, default, new FixedLengthSource(buffer.Capacity + 1)), Is.EqualTo(NvencAccessUnitCopyStatus.Rejected));

            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));

            FieldInfo validLengthField = typeof(NvencOwnedAccessUnitBuffer).GetField(
                "_validLength", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(validLengthField, Is.Not.Null);
            Assert.That(validLengthField.GetValue(buffer), Is.EqualTo(0));

            // No content was recorded: transfer is refused.
            Assert.That(buffer.TryTransferToSink(write, out _), Is.False);
        }

        [Test]
        public void WorkToken_ForwardedExactlyThroughWriteAndOwnedLeases()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            CaptureFrameWorkToken token = MakeToken(7);

            Assert.That(buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(write.WorkToken.IdenticalTo(token), Is.True);

            Assert.That(buffer.TryCopyCompletedOutput(write, default, new FixedLengthSource(1024)), Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
            Assert.That(buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease owned), Is.True);
            Assert.That(owned.WorkToken.IdenticalTo(token), Is.True);
        }

        [Test]
        public void ForeignBufferLease_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer a = new NvencOwnedAccessUnitBuffer(state);
            NvencOwnedAccessUnitBuffer b = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(a.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease writeA), Is.True);

            Assert.That(b.TryTransferToSink(writeA, out _), Is.False);
            Assert.That(b.CancelWrite(writeA), Is.False);

            Assert.That(a.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));
        }

        [Test]
        public void ForeignWorkTokenLease_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);

            // Forge a lease with the same buffer identity and generation but a
            // different exact work token; the buffer must reject it.
            NvencAccessUnitWriteLease forged = new NvencAccessUnitWriteLease(
                write.OwnerToken, write.Generation, MakeToken(2));

            Assert.That(buffer.TryTransferToSink(forged, out _), Is.False);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));

            // The genuine lease still copies and transfers.
            Assert.That(buffer.TryCopyCompletedOutput(write, default, new FixedLengthSource(1024)), Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
            Assert.That(buffer.TryTransferToSink(write, out _), Is.True);
        }

        [Test]
        public void StaleGenerationLease_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.CancelWrite(write), Is.True);

            // The cancelled lease is now stale for every later use.
            Assert.That(buffer.CancelWrite(write), Is.False);
            Assert.That(buffer.TryTransferToSink(write, out _), Is.False);
        }

        [Test]
        public void WriteLease_DoubleTransferAndCancel_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryCopyCompletedOutput(write, default, new FixedLengthSource(1024)), Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
            Assert.That(buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease owned), Is.True);

            Assert.That(buffer.TryTransferToSink(write, out _), Is.False);
            Assert.That(buffer.CancelWrite(write), Is.False);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));

            Assert.That(buffer.Return(owned), Is.True);
        }

        [Test]
        public void OwnedLease_DoubleReturn_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryCopyCompletedOutput(write, default, new FixedLengthSource(1024)), Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
            Assert.That(buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease owned), Is.True);

            Assert.That(buffer.Return(owned), Is.True);
            Assert.That(buffer.Return(owned), Is.False);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
        }

        [Test]
        public void ControlledCancel_ReusableWithNextGeneration()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease first), Is.True);
            long firstGeneration = first.Generation;

            Assert.That(buffer.CancelWrite(first), Is.True);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));

            Assert.That(buffer.TryBeginWrite(MakeToken(2), out NvencAccessUnitWriteLease second), Is.True);
            Assert.That(second.Generation, Is.EqualTo(firstGeneration + 1));
        }

        [Test]
        public void SinkReturn_ReusableWithNextGeneration()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease first), Is.True);
            long firstGeneration = first.Generation;
            Assert.That(buffer.TryCopyCompletedOutput(first, default, new FixedLengthSource(1024)), Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
            Assert.That(buffer.TryTransferToSink(first, out NvencOwnedAccessUnitLease owned), Is.True);

            Assert.That(buffer.Return(owned), Is.True);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));

            Assert.That(buffer.TryBeginWrite(MakeToken(2), out NvencAccessUnitWriteLease second), Is.True);
            Assert.That(second.Generation, Is.EqualTo(firstGeneration + 1));
        }

        [Test]
        public void RunningAndDraining_AllowAcceptedWork()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write1), Is.True);
            Assert.That(buffer.TryCopyCompletedOutput(write1, default, new FixedLengthSource(1024)), Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
            Assert.That(buffer.TryTransferToSink(write1, out NvencOwnedAccessUnitLease owned1), Is.True);

            Assert.That(state.TryBeginDrain(), Is.True);

            Assert.That(buffer.Return(owned1), Is.True);
            Assert.That(buffer.TryBeginWrite(MakeToken(2), out NvencAccessUnitWriteLease write2), Is.True);
            Assert.That(write2.IsValid, Is.True);
        }

        [Test]
        public void Poisoned_BlocksNewAcquire()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(state.TryPoison(), Is.True);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.False);
            Assert.That(write.IsValid, Is.False);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
        }

        [Test]
        public void CollectorOwnedThenPoison_HoldsRegionNoReturnNoReuse()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(state.TryPoison(), Is.True);

            Assert.That(buffer.CancelWrite(write), Is.False);
            Assert.That(buffer.TryTransferToSink(write, out _), Is.False);
            Assert.That(buffer.TryBeginWrite(MakeToken(2), out _), Is.False);

            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));
        }

        [Test]
        public void SinkOwnedThenPoison_HoldsRegionNoReturnNoReuse()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryCopyCompletedOutput(write, default, new FixedLengthSource(1024)), Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
            Assert.That(buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease owned), Is.True);
            Assert.That(state.TryPoison(), Is.True);

            Assert.That(buffer.Return(owned), Is.False);
            Assert.That(buffer.TryBeginWrite(MakeToken(2), out _), Is.False);

            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));
        }

        [Test]
        public void PoisonInsideResourceResolutionGuard_RejectsLaterReleaseAndReuse()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(buffer.TryCopyCompletedOutput(write, default, new FixedLengthSource(1024)), Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
            Assert.That(buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease owned), Is.True);

            // Hold the resource-resolution gate and poison inside it: the poison
            // transition is ordered against the region state transitions.
            Assert.That(state.TryBeginResourceResolution(), Is.True);
            Assert.That(state.TryPoison(), Is.True);
            state.EndResourceResolution();

            Assert.That(buffer.Return(owned), Is.False);
            Assert.That(buffer.TryBeginWrite(MakeToken(2), out _), Is.False);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));
        }

        [Test]
        public void CopyInProgress_SecondCallSameLease_RejectedBeforeSecondSourceCall()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            CaptureFrameWorkToken token = MakeToken(1);
            Assert.That(buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);

            BlockingSource source = new BlockingSource
            {
                Entered = new ManualResetEventSlim(false),
                WaitFor = new ManualResetEventSlim(false),
            };

            Exception copierError = null;
            NvencAccessUnitCopyStatus firstStatus = default;
            Thread copier = new Thread(() =>
            {
                try
                {
                    firstStatus = buffer.TryCopyCompletedOutput(write, default, source);
                }
                catch (Exception ex)
                {
                    copierError = ex;
                }
            })
            {
                IsBackground = true,
            };
            copier.Start();

            Assert.That(source.Entered.Wait(WatchdogTimeoutMs), Is.True, "source did not enter");

            // A concurrent second copy on the same lease is rejected before any
            // side effect: copy-in-progress was claimed exactly once.
            Assert.That(buffer.TryCopyCompletedOutput(write, default, source),
                Is.EqualTo(NvencAccessUnitCopyStatus.NotStarted));
            Assert.That(source.CallCount, Is.EqualTo(1));

            source.WaitFor.Set();
            Assert.That(copier.Join(WatchdogTimeoutMs), Is.True, "copier did not exit");
            Assert.That(copierError, Is.Null);
            Assert.That(firstStatus, Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
            Assert.That(buffer.TryTransferToSink(write, out _), Is.True);
        }

        [Test]
        public void PoisonDuringSourceCall_PreventsContentReadyAndTransfer()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            CaptureFrameWorkToken token = MakeToken(1);
            Assert.That(buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);

            BlockingSource source = new BlockingSource
            {
                Entered = new ManualResetEventSlim(false),
                WaitFor = new ManualResetEventSlim(false),
            };

            Exception copierError = null;
            NvencAccessUnitCopyStatus status = default;
            Thread copier = new Thread(() =>
            {
                try
                {
                    status = buffer.TryCopyCompletedOutput(write, default, source);
                }
                catch (Exception ex)
                {
                    copierError = ex;
                }
            })
            {
                IsBackground = true,
            };
            copier.Start();

            Assert.That(source.Entered.Wait(WatchdogTimeoutMs), Is.True, "source did not enter");

            // Poison wins while the copy is in flight.
            Assert.That(state.TryPoison(), Is.True);

            source.WaitFor.Set();
            Assert.That(copier.Join(WatchdogTimeoutMs), Is.True, "copier did not exit");
            Assert.That(copierError, Is.Null);

            // The copy must not commit: no content-ready and no transfer.
            Assert.That(status, Is.EqualTo(NvencAccessUnitCopyStatus.NotStarted));
            Assert.That(buffer.TryTransferToSink(write, out _), Is.False);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));
        }

        [Test]
        public void CommitGateContention_ParksLengthAndCommitsWithoutSourceRecall()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            CaptureFrameWorkToken token = MakeToken(1);
            Assert.That(buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);

            ManualResetEventSlim sourceEntered = new ManualResetEventSlim(false);
            ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);

            BlockingSource source = new BlockingSource
            {
                Entered = sourceEntered,
                WaitFor = gateHeld,
            };

            Exception holderError = null;
            Thread holder = new Thread(() =>
            {
                try
                {
                    if (sourceEntered.Wait(WatchdogTimeoutMs))
                    {
                        if (state.TryBeginResourceResolution())
                        {
                            gateHeld.Set();
                            release.Wait(WatchdogTimeoutMs);
                            state.EndResourceResolution();
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
            holder.Start();

            Exception copierError = null;
            NvencAccessUnitCopyStatus status = default;
            Thread copier = new Thread(() =>
            {
                try
                {
                    status = buffer.TryCopyCompletedOutput(write, default, source);
                }
                catch (Exception ex)
                {
                    copierError = ex;
                }
            })
            {
                IsBackground = true,
            };
            copier.Start();

            Assert.That(copier.Join(WatchdogTimeoutMs), Is.True, "copier did not exit");
            Assert.That(copierError, Is.Null);
            Assert.That(status, Is.EqualTo(NvencAccessUnitCopyStatus.Pending));
            Assert.That(source.CallCount, Is.EqualTo(1));

            release.Set();
            Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "holder did not exit");
            Assert.That(holderError, Is.Null);

            // The parked length is committed without re-contacting the source.
            Assert.That(buffer.TryCommitPendingCopy(write), Is.True);
            Assert.That(source.CallCount, Is.EqualTo(1));
            Assert.That(buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease owned), Is.True);
            Assert.That(owned.WorkToken.IdenticalTo(token), Is.True);
        }

        [Test]
        public void GenerationOverflow_RetiresWithoutWrap()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencOwnedAccessUnitBuffer buffer = new NvencOwnedAccessUnitBuffer(state);

            FieldInfo generationField = typeof(NvencOwnedAccessUnitBuffer).GetField(
                "_generation", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(generationField, Is.Not.Null);
            generationField.SetValue(buffer, long.MaxValue);

            Assert.That(buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(write.Generation, Is.EqualTo(long.MaxValue));
            Assert.That(buffer.TryCopyCompletedOutput(write, default, new FixedLengthSource(1024)), Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
            Assert.That(buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease owned), Is.True);
            Assert.That(buffer.Return(owned), Is.True);

            // Retired: the single slot must not wrap back to a small generation.
            Assert.That(buffer.TryBeginWrite(MakeToken(2), out _), Is.False);
            Assert.That(buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
        }

        [Test]
        public void LeaseShape_ValueType_NoPublicConstructor_NoDispose_ReadonlyFields()
        {
            Type[] leaseTypes =
            {
                typeof(NvencAccessUnitWriteLease),
                typeof(NvencOwnedAccessUnitLease),
            };

            foreach (Type leaseType in leaseTypes)
            {
                Assert.That(leaseType.IsValueType, Is.True, leaseType.Name + " must be a value type.");
                Assert.That(typeof(IDisposable).IsAssignableFrom(leaseType), Is.False, leaseType.Name + " must not be disposable.");
                Assert.That(leaseType.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty,
                    leaseType.Name + " must not expose a public constructor.");

                foreach (FieldInfo field in leaseType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(field.IsInitOnly, Is.True, leaseType.Name + "." + field.Name + " must be readonly.");
                }
            }
        }

        [Test]
        public void BufferShape_Sealed_NoPublicConstructor_NoDispose()
        {
            Type bufferType = typeof(NvencOwnedAccessUnitBuffer);

            Assert.That(bufferType.IsSealed, Is.True, "The buffer must be sealed.");
            Assert.That(typeof(IDisposable).IsAssignableFrom(bufferType), Is.False, "The buffer must not be disposable.");
            Assert.That(bufferType.GetConstructors(BindingFlags.Public | BindingFlags.Instance), Is.Empty,
                "The buffer must not expose a public constructor.");
        }

        [Test]
        public void RunPath_NoLargeArrayNoResizeNoPoolNoQueueNoThreadNoIoNoHashNoParser()
        {
            string directory = RuntimeDirectory();
            string bufferSource = File.ReadAllText(Path.Combine(directory, "NvencOwnedAccessUnitBuffer.cs"));
            string leaseSource =
                File.ReadAllText(Path.Combine(directory, "NvencAccessUnitWriteLease.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencOwnedAccessUnitLease.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencAccessUnitPhase.cs"));

            // Exactly one fixed backing allocation, in the constructor.
            Assert.That(CountOccurrences(bufferSource, "new byte["), Is.EqualTo(1),
                "The buffer must allocate its storage exactly once.");

            string[] methodBodies =
            {
                ExtractMethodBody(bufferSource, "TryBeginWrite"),
                ExtractMethodBody(bufferSource, "TryCopyCompletedOutput"),
                ExtractMethodBody(bufferSource, "TryCommitPendingCopy"),
                ExtractMethodBody(bufferSource, "TryTransferToSink"),
                ExtractMethodBody(bufferSource, "CancelWrite"),
                ExtractMethodBody(bufferSource, "Return"),
            };

            string[] allocationWords =
            {
                "new byte[", "new long[", "new int[", "new []",
                "new List", "new Dictionary", "new Queue", "new Stack", "new HashSet",
                "Array.", "ArrayPool", ".ToArray(", ".ToList(", "Enumerable.", "Allocate",
            };

            foreach (string body in methodBodies)
            {
                foreach (string word in allocationWords)
                {
                    Assert.That(body, Does.Not.Contain(word), "Run path must not allocate: " + word);
                }
            }

            string allSource = bufferSource + leaseSource;

            string[] forbidden =
            {
                "lock (", "Monitor", "ManualResetEvent", "AutoResetEvent", "WaitHandle", "SpinWait",
                "new Thread", "ThreadPool", "Task", "File.", "Directory.", "FileStream", "DllImport",
                "UnityEngine", "Application.", "SystemInfo", "GraphicsDevice", "IntPtr", "SafeHandle",
                "NvEnc", "RenderTexture", "SHA", "MD5", "Hash", "H.264", "H264", "ArrayPool",
            };

            foreach (string word in forbidden)
            {
                Assert.That(allSource, Does.Not.Contain(word), "Production source must not reference: " + word);
            }
        }

        [Test]
        public void Source_NoRawArrayExposure_NoDirectCopy()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOwnedAccessUnitBuffer.cs"));

            // The buffer never hands out the raw backing array and never copies
            // directly; content flows only through the injected source call.
            Assert.That(source, Does.Not.Contain("TryGetCollectorView"));
            Assert.That(source, Does.Not.Contain("TryGetSinkView"));
            Assert.That(source, Does.Not.Contain("TryCopyCollectorContent"));
            Assert.That(source, Does.Not.Contain("TryConsumeSinkContent"));
            Assert.That(source, Does.Not.Contain("BlockCopy"));
        }

        private sealed class BlockingSource : INvencOutputBitstreamSource
        {
            internal int CallCount;
            internal int Length = 1024;
            internal bool Result = true;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim WaitFor;

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                CallCount++;
                if (Entered != null)
                {
                    Entered.Set();
                }

                if (WaitFor != null)
                {
                    WaitFor.Wait(WatchdogTimeoutMs);
                }

                validLength = Length;
                return Result;
            }
        }

        private sealed class FixedLengthSource : INvencOutputBitstreamSource
        {
            private readonly int _length;

            internal FixedLengthSource(int length)
            {
                _length = length;
            }

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                validLength = _length;
                return true;
            }
        }

        private static CaptureFrameWorkToken MakeToken(long frameId)
        {
            return new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, frameId);
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
    }
}
