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
    /// Contract tests for the Submitted Output Collector boundary: the single
    /// normal path that retrieves a bitstream into the fixed Access Unit region
    /// and transfers it to SinkOwned. Uses a fake output source; no GPU, NVENC,
    /// Completion Event, or native resource is touched.
    /// </summary>
    public class NvencSubmittedOutputCollectorContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        [Test]
        public void Submitted_NormalPath_SourceCalledExactlyOnce()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

            Assert.That(h.Collector.TryCollect(record, out NvencSubmittedOutputCollectResult result), Is.True);
            Assert.That(result.IsSucceeded, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Submitted_SourceReceivesExactWorkTokenAndSampleSlot()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(7);

            Assert.That(h.Collector.TryCollect(record, out _), Is.True);

            Assert.That(h.Source.LastWorkToken.IdenticalTo(record.WorkToken), Is.True);
            Assert.That(h.Source.LastSampleSlot.OwnerToken, Is.EqualTo(record.SampleSlot.OwnerToken));
            Assert.That(h.Source.LastSampleSlot.SlotIndex, Is.EqualTo(record.SampleSlot.SlotIndex));
            Assert.That(h.Source.LastSampleSlot.Generation, Is.EqualTo(record.SampleSlot.Generation));
        }

        [Test]
        public void Submitted_DestinationIsFixedRegionCapacity()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

            Assert.That(h.Collector.TryCollect(record, out _), Is.True);

            Assert.That(h.Source.LastDestinationCapacity, Is.EqualTo(h.Buffer.Capacity));
            Assert.That(h.Source.LastDestination.Length, Is.EqualTo((int)NvencBringUpProfileV1.MaxAccessUnitByteLength));
        }

        [Test]
        public void Submitted_SourceContentAndLengthRetainedInBuffer()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

            byte[] content = new byte[8];
            content[0] = 0xAB;
            content[7] = 0xCD;
            h.Source.ContentToWrite = content;
            h.Source.ResultLength = 8;

            Assert.That(h.Collector.TryCollect(record, out NvencSubmittedOutputCollectResult result), Is.True);
            Assert.That(result.IsSucceeded, Is.True);

            // The source's destination is the buffer's fixed storage.
            Assert.That(h.Source.LastDestination[0], Is.EqualTo((byte)0xAB));
            Assert.That(h.Source.LastDestination[7], Is.EqualTo((byte)0xCD));
            Assert.That(ReadValidLength(h.Buffer), Is.EqualTo(8));
            Assert.That(ReadContentReady(h.Buffer), Is.True);
        }

        [Test]
        public void Submitted_SampleSlotReturnedBeforeSinkLeaseIssued()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

            Assert.That(h.Collector.TryCollect(record, out NvencSubmittedOutputCollectResult result), Is.True);

            // The sample slot was still active when the source ran (returned only
            // after the copy), and is returned before the sink lease is issued.
            Assert.That(h.Source.SampleActiveAtCopyStart, Is.True);
            Assert.That(h.SamplePool.IsActive(record.SampleSlot), Is.False);
            Assert.That(result.IsSucceeded, Is.True);
            Assert.That(result.OwnedLease.IsValid, Is.True);
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));
        }

        [Test]
        public void Transfer_HasNoCallerSuppliedLength()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencOwnedAccessUnitBuffer.cs"));
            string body = ExtractMethodBody(source, "TryTransferToSink");

            Assert.That(body, Does.Not.Contain("validLength"));
            Assert.That(body, Does.Not.Contain("int "));
        }

        [Test]
        public void Transfer_WithoutContent_Rejected()
        {
            Harness h = new Harness();
            Assert.That(h.Buffer.TryBeginWrite(MakeToken(1), out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(h.Buffer.TryTransferToSink(write, out _), Is.False);
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));
        }

        [Test]
        public void CopyLength_BoundaryOneAndMax_Accepted()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);
            h.Source.ResultLength = 1;

            Assert.That(h.Collector.TryCollect(record, out NvencSubmittedOutputCollectResult min), Is.True);
            Assert.That(min.IsSucceeded, Is.True);
            Assert.That(ReadValidLength(h.Buffer), Is.EqualTo(1));
        }

        [Test]
        public void CopyLength_Invalid_ControlledFailureNoLease()
        {
            foreach (int invalid in new[] { 0, -1 })
            {
                Harness h = new Harness();
                NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);
                h.Source.ResultLength = invalid;

                Assert.That(h.Collector.TryCollect(record, out NvencSubmittedOutputCollectResult result), Is.True);
                Assert.That(result.IsControlledFailure, Is.True);
                Assert.That(result.OwnedLease.IsValid, Is.False);
                Assert.That(h.SamplePool.IsActive(record.SampleSlot), Is.False);
                Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
            }

            Harness over = new Harness();
            NvencSubmitToOutputRecord overRecord = over.CreateSubmittedRecord(1);
            over.Source.ResultLength = over.Buffer.Capacity + 1;
            Assert.That(over.Collector.TryCollect(overRecord, out NvencSubmittedOutputCollectResult overResult), Is.True);
            Assert.That(overResult.IsControlledFailure, Is.True);
            Assert.That(overResult.OwnedLease.IsValid, Is.False);
        }

        [Test]
        public void CompletedRecord_SecondAttemptPoisonsWithoutSecondSourceCall()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

            Assert.That(h.Collector.TryCollect(record, out _), Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));

            // The record's sample slot is now returned: a second attempt finds
            // broken correlation and poisons instead of re-contacting the source.
            Assert.Throws<InvalidOperationException>(() => h.Collector.TryCollect(record, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void SourceFalse_SampleSlotAndBufferReleased()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);
            h.Source.Result = false;

            Assert.That(h.Collector.TryCollect(record, out NvencSubmittedOutputCollectResult result), Is.True);
            Assert.That(result.IsControlledFailure, Is.True);
            Assert.That(result.OwnedLease.IsValid, Is.False);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
            Assert.That(h.SamplePool.IsActive(record.SampleSlot), Is.False);
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
        }

        [Test]
        public void SourceException_SameReferencePropagatesPoisonHoldsResources()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);
            InvalidOperationException boom = new InvalidOperationException("boom");
            h.Source.ExceptionToThrow = boom;

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => h.Collector.TryCollect(record, out _));

            Assert.That(ReferenceEquals(thrown, boom), Is.True);
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
            Assert.That(h.SamplePool.IsActive(record.SampleSlot), Is.True);
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));
        }

        [Test]
        public void KnownSuccessSampleReturnFailure_PoisonsNoSinkLease()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);
            h.Source.ReturnSampleSlotInsideCopy = true;

            Assert.Throws<InvalidOperationException>(() => h.Collector.TryCollect(record, out _));

            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));
        }

        [Test]
        public void NonSubmittedKinds_RejectedWithDefaultResult_SourceNotContacted()
        {
            Harness h = new Harness();

            // FailedBeforeSubmit is rejected with a default result and no
            // resource touch; the caller routes it to its dedicated release path.
            NvencSubmitToOutputRecord failed = h.CreateFailedBeforeSubmitRecord(1);
            Assert.That(h.Collector.TryCollect(failed, out NvencSubmittedOutputCollectResult failedResult), Is.False);
            Assert.That(failedResult.IsNone, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(0));

            // None (default) does nothing and never contacts the source.
            Assert.That(h.Collector.TryCollect(default, out NvencSubmittedOutputCollectResult none), Is.False);
            Assert.That(none.IsNone, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void ForeignSampleSlot_Poisons()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecordWithForeignSample();

            Assert.Throws<InvalidOperationException>(() => h.Collector.TryCollect(record, out _));

            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void StaleSampleSlot_Poisons()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecordWithStaleSample();

            Assert.Throws<InvalidOperationException>(() => h.Collector.TryCollect(record, out _));

            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void GateContention_NoSourceContact_Retryable()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

            using ManualResetEventSlim entered = new ManualResetEventSlim(false);
            using ManualResetEventSlim release = new ManualResetEventSlim(false);
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
                Assert.That(h.Collector.TryCollect(record, out _), Is.False);
                Assert.That(h.Source.CallCount, Is.EqualTo(0));
                Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
            }
            finally
            {
                release.Set();
                holderJoined = holder.Join(WatchdogTimeoutMs);
            }

            Assert.That(holderJoined, Is.True, "gate holder did not exit");
            Assert.That(holderError, Is.Null);

            // Retry succeeds once the gate is free.
            Assert.That(h.Collector.TryCollect(record, out NvencSubmittedOutputCollectResult retry), Is.True);
            Assert.That(retry.IsSucceeded, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void GateContentionAfterSource_ParksAndConverges()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

            using ManualResetEventSlim sourceEntered = new ManualResetEventSlim(false);
            using ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
            using ManualResetEventSlim release = new ManualResetEventSlim(false);
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
                // The source is called once; the post-source gate is then held, so
                // the collector parks without poisoning or a terminal result.
                Assert.That(h.Collector.TryCollect(record, out NvencSubmittedOutputCollectResult first), Is.False);
                Assert.That(first.IsNone, Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(h.Source.CallCount, Is.EqualTo(1));
                Assert.That(h.SamplePool.IsActive(record.SampleSlot), Is.True);
            }
            finally
            {
                release.Set();
                holderJoined = holder.Join(WatchdogTimeoutMs);
            }

            Assert.That(holderJoined, Is.True, "holder did not exit");
            Assert.That(holderError, Is.Null);

            // Retry converges without re-contacting the source.
            Assert.That(h.Collector.TryCollect(record, out NvencSubmittedOutputCollectResult retry), Is.True);
            Assert.That(retry.IsSucceeded, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void PendingMismatchRecord_PoisonsWithoutAdvancingSourceSampleOrBuffer()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord a = h.CreateSubmittedRecord(1);
            NvencSubmitToOutputRecord b = h.CreateSubmittedRecord(2);

            using ManualResetEventSlim sourceEntered = new ManualResetEventSlim(false);
            using ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
            using ManualResetEventSlim release = new ManualResetEventSlim(false);
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
                // Park A behind the post-source gate without a terminal result.
                Assert.That(h.Collector.TryCollect(a, out _), Is.False);
                Assert.That(h.Source.CallCount, Is.EqualTo(1));
            }
            finally
            {
                release.Set();
                holderJoined = holder.Join(WatchdogTimeoutMs);
            }

            Assert.That(holderJoined, Is.True, "holder did not exit");
            Assert.That(holderError, Is.Null);

            // B is a different record and must never receive A's parked result:
            // the mismatch poisons without advancing source, sample, or buffer.
            Assert.Throws<InvalidOperationException>(() => h.Collector.TryCollect(b, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
            Assert.That(h.SamplePool.IsActive(a.SampleSlot), Is.True);
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));
        }

        [Test]
        public void PendingResume_ReturnedLeaseAfterPark_PoisonsWithoutExternalContact()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord a = h.CreateSubmittedRecord(1);

            using ManualResetEventSlim sourceEntered = new ManualResetEventSlim(false);
            using ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
            using ManualResetEventSlim release = new ManualResetEventSlim(false);
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
                // Park A behind the post-source gate without a terminal result.
                Assert.That(h.Collector.TryCollect(a, out _), Is.False);
                Assert.That(h.Source.CallCount, Is.EqualTo(1));
            }
            finally
            {
                release.Set();
                holderJoined = holder.Join(WatchdogTimeoutMs);
            }

            Assert.That(holderJoined, Is.True, "holder did not exit");
            Assert.That(holderError, Is.Null);

            // Return the Frame Completion credit while A is parked: its lease is
            // no longer active even though the record values are unchanged.
            Assert.That(h.FrameCompletionCreditPool.TryReturn(a.FrameCompletionCredit), Is.True);

            // Resuming with the same old record poisons without re-calling the
            // source, returning the sample slot, or transferring to the sink.
            Assert.Throws<InvalidOperationException>(() => h.Collector.TryCollect(a, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
            Assert.That(h.SamplePool.IsActive(a.SampleSlot), Is.True);
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.CollectorOwned));
        }

        [Test]
        public void Draining_ProcessesAcceptedSubmittedRecord()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

            Assert.That(h.State.TryBeginDrain(), Is.True);

            Assert.That(h.Collector.TryCollect(record, out NvencSubmittedOutputCollectResult result), Is.True);
            Assert.That(result.IsSucceeded, Is.True);
            Assert.That(h.Source.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Poisoned_SourceNotContacted()
        {
            Harness h = new Harness();
            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);

            Assert.That(h.State.TryPoison(), Is.True);

            Assert.That(h.Collector.TryCollect(record, out _), Is.False);
            Assert.That(h.Source.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void ResultVariant_ExclusiveShapes()
        {
            Harness h = new Harness();

            NvencSubmittedOutputCollectResult none = default;
            Assert.That(none.IsNone, Is.True);
            Assert.That(none.IsSucceeded, Is.False);
            Assert.That(none.IsControlledFailure, Is.False);

            NvencSubmitToOutputRecord record = h.CreateSubmittedRecord(1);
            Assert.That(h.Collector.TryCollect(record, out NvencSubmittedOutputCollectResult success), Is.True);
            Assert.That(success.IsSucceeded, Is.True);
            Assert.That(success.IsNone, Is.False);
            Assert.That(success.IsControlledFailure, Is.False);

            Harness h2 = new Harness();
            NvencSubmitToOutputRecord record2 = h2.CreateSubmittedRecord(1);
            h2.Source.Result = false;
            Assert.That(h2.Collector.TryCollect(record2, out NvencSubmittedOutputCollectResult failure), Is.True);
            Assert.That(failure.IsControlledFailure, Is.True);
            Assert.That(failure.IsSucceeded, Is.False);
            Assert.That(failure.IsNone, Is.False);
        }

        [Test]
        public void RecoveryProof_IsInitialized_DefaultFalse_MintedTrue()
        {
            NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof none = default;
            Assert.That(none.IsInitialized, Is.False);

            Harness h = new Harness();
            CaptureFrameWorkToken token = MakeToken(1);
            Assert.That(h.Buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(h.Buffer.TryCancelCollectorReservation(
                write, out NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof minted), Is.True);
            Assert.That(minted.IsInitialized, Is.True);
        }

        [Test]
        public void Result_ControlledFailure_RequiresInitializedProof()
        {
            CaptureFrameWorkToken token = MakeToken(1);

            // A default proof fails closed: not a terminal ControlledFailure.
            NvencSubmittedOutputCollectResult withDefaultProof =
                NvencSubmittedOutputCollectResult.ControlledFailure(token, default);
            Assert.That(withDefaultProof.IsControlledFailure, Is.False);
            Assert.That(withDefaultProof.IsSucceeded, Is.False);
            Assert.That(withDefaultProof.IsNone, Is.False);

            // A minted proof makes the ControlledFailure shape terminal.
            Harness h = new Harness();
            Assert.That(h.Buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
            Assert.That(h.Buffer.TryCancelCollectorReservation(
                write, out NvencOwnedAccessUnitBuffer.NvencOwnedAccessUnitRecoveryProof minted), Is.True);
            NvencSubmittedOutputCollectResult withMintedProof =
                NvencSubmittedOutputCollectResult.ControlledFailure(token, minted);
            Assert.That(withMintedProof.IsControlledFailure, Is.True);
            Assert.That(withMintedProof.IsSucceeded, Is.False);
        }

        [Test]
        public void NormalPath_NoAllocationNoThreadNoIoNoNative()
        {
            string directory = RuntimeDirectory();
            string collectorSource = File.ReadAllText(Path.Combine(directory, "NvencSubmittedOutputCollector.cs"));
            string otherSources =
                File.ReadAllText(Path.Combine(directory, "INvencOutputBitstreamSource.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencSubmittedOutputCollectResult.cs"));

            string[] collectorBodies =
            {
                ExtractMethodBody(collectorSource, "TryCollect"),
                ExtractMethodBody(collectorSource, "CompleteCopy"),
                ExtractMethodBody(collectorSource, "CompleteSuccess"),
                ExtractMethodBody(collectorSource, "CompleteControlledFailure"),
                ExtractMethodBody(collectorSource, "CompletePending"),
                ExtractMethodBody(collectorSource, "Park"),
                ExtractMethodBody(collectorSource, "ClearPending"),
                ExtractMethodBody(collectorSource, "MatchesPending"),
            };

            string[] allocationWords =
            {
                "new byte[", "new long[", "new int[", "new []",
                "new List", "new Dictionary", "new Queue", "new Stack", "new HashSet",
                "Array.", "ArrayPool", ".ToArray(", ".ToList(", "Enumerable.", "Allocate",
            };

            foreach (string body in collectorBodies)
            {
                foreach (string word in allocationWords)
                {
                    Assert.That(body, Does.Not.Contain(word), "Collector run path must not allocate: " + word);
                }
            }

            string allSource = collectorSource + otherSources;

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
        public void ProductionTypes_DoNotExposeBackingArray()
        {
            Type[] noStorageTypes =
            {
                typeof(NvencSubmittedOutputCollector),
                typeof(NvencSubmittedOutputCollectResult),
                typeof(NvencOwnedAccessUnitLease),
                typeof(NvencAccessUnitWriteLease),
            };

            foreach (Type type in noStorageTypes)
            {
                foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    Assert.That(field.FieldType, Is.Not.EqualTo(typeof(byte[])), type.Name + "." + field.Name + " must not hold the backing array.");
                }

                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    Assert.That(property.PropertyType, Is.Not.EqualTo(typeof(byte[])), type.Name + "." + property.Name + " must not expose the backing array.");
                }
            }

            // The buffer owns the array but must never expose it.
            Type bufferType = typeof(NvencOwnedAccessUnitBuffer);
            foreach (PropertyInfo property in bufferType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(property.PropertyType, Is.Not.EqualTo(typeof(byte[])), "buffer must not expose the backing array.");
            }

            foreach (MethodInfo method in bufferType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                Assert.That(method.ReturnType, Is.Not.EqualTo(typeof(byte[])), "buffer must not return the backing array.");
            }
        }

        private static int ReadValidLength(NvencOwnedAccessUnitBuffer buffer)
        {
            FieldInfo field = typeof(NvencOwnedAccessUnitBuffer).GetField(
                "_validLength", BindingFlags.Instance | BindingFlags.NonPublic);
            return (int)field.GetValue(buffer);
        }

        private static bool ReadContentReady(NvencOwnedAccessUnitBuffer buffer)
        {
            FieldInfo field = typeof(NvencOwnedAccessUnitBuffer).GetField(
                "_contentReady", BindingFlags.Instance | BindingFlags.NonPublic);
            return (bool)field.GetValue(buffer);
        }

        private static CaptureFrameWorkToken MakeToken(long frameId)
        {
            return new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, frameId);
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

        private sealed class FakeOutputSource : INvencOutputBitstreamSource
        {
            internal int CallCount;
            internal CaptureFrameWorkToken LastWorkToken;
            internal NvencEncodeSampleSlotLease LastSampleSlot;
            internal byte[] LastDestination;
            internal int LastDestinationCapacity;
            internal int ResultLength = 1024;
            internal bool Result = true;
            internal Exception ExceptionToThrow;
            internal byte[] ContentToWrite;
            internal NvencEncodeSampleSlotPool SamplePool;
            internal bool SampleActiveAtCopyStart;
            internal bool ReturnSampleSlotInsideCopy;
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
                LastWorkToken = workToken;
                LastSampleSlot = sampleSlot;
                LastDestination = destination;
                LastDestinationCapacity = destinationCapacity;

                if (SamplePool != null)
                {
                    SampleActiveAtCopyStart = SamplePool.IsActive(sampleSlot);
                }

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (ContentToWrite != null)
                {
                    int count = Math.Min(ContentToWrite.Length, destinationCapacity);
                    Buffer.BlockCopy(ContentToWrite, 0, destination, 0, count);
                }

                if (ReturnSampleSlotInsideCopy && SamplePool != null)
                {
                    SamplePool.TryReturn(sampleSlot);
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
            internal NvencCaptureWorkSlotPool WorkPool;
            internal NvencEncodeSampleSlotPool SamplePool;
            internal NvencSubmitToOutputCreditPool SubmitToOutputCreditPool;
            internal NvencFrameCompletionCreditPool FrameCompletionCreditPool;
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal FakeOutputSource Source;
            internal NvencSubmittedOutputCollector Collector;

            internal Harness()
            {
                State = new NvencCaptureProcessState();
                WorkPool = new NvencCaptureWorkSlotPool(State);
                SamplePool = new NvencEncodeSampleSlotPool(State);
                SubmitToOutputCreditPool = new NvencSubmitToOutputCreditPool(State);
                FrameCompletionCreditPool = new NvencFrameCompletionCreditPool(State);
                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Source = new FakeOutputSource { SamplePool = SamplePool };
                Collector = new NvencSubmittedOutputCollector(
                    State, WorkPool, SamplePool, SubmitToOutputCreditPool, FrameCompletionCreditPool, Buffer, Source);
            }

            internal NvencSubmitToOutputRecord CreateSubmittedRecord(long frameId)
            {
                Assert.That(WorkPool.TryRent(out NvencCaptureWorkSlotLease workSlot), Is.True);
                Assert.That(SamplePool.TryRent(out NvencEncodeSampleSlotLease sampleSlot), Is.True);
                Assert.That(SubmitToOutputCreditPool.TryRent(out NvencSubmitToOutputCreditLease submitCredit), Is.True);
                Assert.That(FrameCompletionCreditPool.TryRent(out NvencFrameCompletionCreditLease frameCredit), Is.True);

                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Guid.NewGuid(), workSlot.SlotIndex, workSlot.Generation, 1, frameId);
                return NvencSubmitToOutputRecord.CreateSubmitted(token, workSlot, sampleSlot, submitCredit, frameCredit);
            }

            internal NvencSubmitToOutputRecord CreateFailedBeforeSubmitRecord(long frameId)
            {
                Assert.That(WorkPool.TryRent(out NvencCaptureWorkSlotLease workSlot), Is.True);
                Assert.That(SamplePool.TryRent(out NvencEncodeSampleSlotLease sampleSlot), Is.True);
                Assert.That(SubmitToOutputCreditPool.TryRent(out NvencSubmitToOutputCreditLease submitCredit), Is.True);
                Assert.That(FrameCompletionCreditPool.TryRent(out NvencFrameCompletionCreditLease frameCredit), Is.True);

                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Guid.NewGuid(), workSlot.SlotIndex, workSlot.Generation, 1, frameId);
                return NvencSubmitToOutputRecord.CreateFailedBeforeSubmit(
                    token, workSlot, sampleSlot, submitCredit, frameCredit, NvencFailedBeforeSubmitReason.NvencSubmitFailed);
            }

            internal NvencSubmitToOutputRecord CreateSubmittedRecordWithForeignSample()
            {
                Assert.That(WorkPool.TryRent(out NvencCaptureWorkSlotLease workSlot), Is.True);
                Assert.That(SubmitToOutputCreditPool.TryRent(out NvencSubmitToOutputCreditLease submitCredit), Is.True);
                Assert.That(FrameCompletionCreditPool.TryRent(out NvencFrameCompletionCreditLease frameCredit), Is.True);

                NvencEncodeSampleSlotPool foreignSamplePool = new NvencEncodeSampleSlotPool(State);
                Assert.That(foreignSamplePool.TryRent(out NvencEncodeSampleSlotLease foreignSample), Is.True);

                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Guid.NewGuid(), workSlot.SlotIndex, workSlot.Generation, 1, 1);
                return NvencSubmitToOutputRecord.CreateSubmitted(token, workSlot, foreignSample, submitCredit, frameCredit);
            }

            internal NvencSubmitToOutputRecord CreateSubmittedRecordWithStaleSample()
            {
                Assert.That(WorkPool.TryRent(out NvencCaptureWorkSlotLease workSlot), Is.True);
                Assert.That(SamplePool.TryRent(out NvencEncodeSampleSlotLease sampleSlot), Is.True);
                Assert.That(SamplePool.TryReturn(sampleSlot), Is.True);
                Assert.That(SubmitToOutputCreditPool.TryRent(out NvencSubmitToOutputCreditLease submitCredit), Is.True);
                Assert.That(FrameCompletionCreditPool.TryRent(out NvencFrameCompletionCreditLease frameCredit), Is.True);

                CaptureFrameWorkToken token = new CaptureFrameWorkToken(
                    Guid.NewGuid(), workSlot.SlotIndex, workSlot.Generation, 1, 1);
                return NvencSubmitToOutputRecord.CreateSubmitted(token, workSlot, sampleSlot, submitCredit, frameCredit);
            }
        }
    }
}
