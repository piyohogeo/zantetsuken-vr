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
    /// Contract tests for the synchronous Run chunk sink boundary: the exact
    /// owned Access Unit lease from the Submitted Output Collector is appended
    /// to a single pre-opened chunk writer in accepted FIFO order, with a
    /// checked accumulated length, and the owned region is returned exactly
    /// once. Uses a fake chunk writer; no filesystem, NVENC, or native
    /// resource is touched.
    /// </summary>
    public class NvencRunChunkSinkContractTests
    {
        private const int WatchdogTimeoutMs = 5000;
        private const byte Seed = 0x40;

        [Test]
        public void Append_SingleFrame_AppendsContentAndReturnsBuffer()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease);

            Assert.That(h.Sink.TryAppend(token, lease, out NvencRunChunkSinkResult result), Is.True);

            Assert.That(result.IsAppended, Is.True);
            Assert.That(result.WorkToken.IdenticalTo(token), Is.True);
            Assert.That(result.AppendedByteLength, Is.EqualTo(64));

            Assert.That(h.Writer.CallCount, Is.EqualTo(1));
            Assert.That(h.Writer.LastOffset, Is.EqualTo(0));
            Assert.That(h.Writer.LastValidLength, Is.EqualTo(64));
            Assert.That(h.Writer.CapturedContent, Is.EqualTo(ExpectedPattern(64, Seed)));

            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(1));
            Assert.That(h.Sink.AccumulatedByteLength, Is.EqualTo(64));
            Assert.That(h.Sink.LastFrameId, Is.EqualTo(1));
        }

        [Test]
        public void Append_MultipleFrames_InputOrderCountersAndFrameIds()
        {
            Harness h = new Harness();
            int[] lengths = { 10, 20, 30 };

            long expected = 0;
            for (int i = 0; i < lengths.Length; i++)
            {
                CaptureFrameWorkToken token = h.ProduceOwnedLease(i + 1, lengths[i], Seed, out NvencOwnedAccessUnitLease lease);
                Assert.That(h.Sink.TryAppend(token, lease, out NvencRunChunkSinkResult result), Is.True);
                Assert.That(result.IsAppended, Is.True);

                expected += lengths[i];
                Assert.That(h.Sink.AppendedCount, Is.EqualTo(i + 1));
                Assert.That(h.Sink.AccumulatedByteLength, Is.EqualTo(expected));
                Assert.That(h.Sink.LastFrameId, Is.EqualTo(i + 1));
            }

            Assert.That(h.Writer.CallCount, Is.EqualTo(3));
        }

        [Test]
        public void Append_ForeignLease_PoisonsWithoutWriterContact()
        {
            Harness h = new Harness();
            Harness other = new Harness();

            CaptureFrameWorkToken token = other.ProduceOwnedLease(1, 32, Seed, out NvencOwnedAccessUnitLease foreignLease);

            Assert.Throws<InvalidOperationException>(() => h.Sink.TryAppend(token, foreignLease, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Append_MismatchedWorkTokenAndLease_PoisonsBeforeWriterContact()
        {
            Harness h = new Harness();
            // The lease is bound to frame 1; the presented work token names a
            // different frame, so the pair is not correlated.
            h.ProduceOwnedLease(1, 32, Seed, out NvencOwnedAccessUnitLease lease);
            CaptureFrameWorkToken foreign = MakeToken(2);

            Assert.Throws<InvalidOperationException>(() => h.Sink.TryAppend(foreign, lease, out _));

            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(0));
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(0));
        }

        [Test]
        public void Append_StaleOrReturnedLease_PoisonsWithoutWriterContact()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 32, Seed, out NvencOwnedAccessUnitLease lease);

            // Return the lease out-of-band so it becomes stale for the sink.
            Assert.That(h.Buffer.Return(lease), Is.True);

            Assert.Throws<InvalidOperationException>(() => h.Sink.TryAppend(token, lease, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(0));
        }

        [Test]
        public void Append_SameLeaseTwice_RejectedBeforeSecondWriterCall()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 48, Seed, out NvencOwnedAccessUnitLease lease);

            Assert.That(h.Sink.TryAppend(token, lease, out NvencRunChunkSinkResult first), Is.True);
            Assert.That(first.IsAppended, Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));

            // The owned lease is already returned: a second append is an
            // ownership break and never contacts the writer again.
            Assert.Throws<InvalidOperationException>(() => h.Sink.TryAppend(token, lease, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Append_ConsumeInFlight_ReturnAndRereserveBlocked_NoCorruption()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 16, Seed, out NvencOwnedAccessUnitLease lease);

            h.Writer.Entered = new ManualResetEventSlim(false);
            h.Writer.WaitFor = new ManualResetEventSlim(false);

            Exception appenderError = null;
            NvencRunChunkSinkResult result = default;
            Thread appender = new Thread(() =>
            {
                try
                {
                    h.Sink.TryAppend(token, lease, out result);
                }
                catch (Exception ex)
                {
                    appenderError = ex;
                }
            })
            {
                IsBackground = true,
            };
            appender.Start();

            Assert.That(h.Writer.Entered.Wait(WatchdogTimeoutMs), Is.True, "writer did not enter");

            // The consume is in flight: Return and re-reservation are refused.
            Assert.That(h.Buffer.Return(lease), Is.False);
            Assert.That(h.Buffer.TryBeginWrite(MakeToken(2), out _), Is.False);

            h.Writer.WaitFor.Set();
            Assert.That(appender.Join(WatchdogTimeoutMs), Is.True, "appender did not exit");
            Assert.That(appenderError, Is.Null);

            Assert.That(result.IsAppended, Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));
            Assert.That(h.Writer.CapturedContent, Is.EqualTo(ExpectedPattern(16, Seed)));
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
        }

        [Test]
        public void Append_ControlledFailure_WriterNotRetried_BufferReturnedExactlyOnce()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 40, Seed, out NvencOwnedAccessUnitLease lease);
            h.Writer.Outcome = NvencRunChunkAppendOutcome.RejectedBeforeWrite;

            Assert.That(h.Sink.TryAppend(token, lease, out NvencRunChunkSinkResult result), Is.True);

            Assert.That(result.IsControlledFailure, Is.True);
            Assert.That(result.WorkToken.IdenticalTo(token), Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(0));
            Assert.That(h.Sink.AccumulatedByteLength, Is.EqualTo(0));
        }

        [Test]
        public void Append_WriterException_PropagatesPoisonsHoldsBuffer()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 40, Seed, out NvencOwnedAccessUnitLease lease);
            InvalidOperationException boom = new InvalidOperationException("boom");
            h.Writer.ExceptionToThrow = boom;

            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => h.Sink.TryAppend(token, lease, out _));

            Assert.That(ReferenceEquals(thrown, boom), Is.True);
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(0));

            // The consume claim stays held after a writer exception, so the
            // region with an unknown outcome can never be returned or reused.
            Assert.That(h.Buffer.Return(lease), Is.False);
        }

        [Test]
        public void Append_IndeterminateOutcome_PoisonsWithoutResultOrCounters()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 40, Seed, out NvencOwnedAccessUnitLease lease);
            h.Writer.Outcome = NvencRunChunkAppendOutcome.Indeterminate;

            Assert.Throws<InvalidOperationException>(() => h.Sink.TryAppend(token, lease, out _));

            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(0));
            Assert.That(h.Sink.AccumulatedByteLength, Is.EqualTo(0));
        }

        [Test]
        public void Append_DefaultOutcome_NonePoisonsWithoutCounters()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 40, Seed, out NvencOwnedAccessUnitLease lease);
            h.Writer.Outcome = default; // NvencRunChunkAppendOutcome.None

            Assert.Throws<InvalidOperationException>(() => h.Sink.TryAppend(token, lease, out _));

            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(0));
            Assert.That(h.Sink.AccumulatedByteLength, Is.EqualTo(0));
        }

        [Test]
        public void Append_PostConsumeGateContention_NoRewriterConvergesOnce()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 56, Seed, out NvencOwnedAccessUnitLease lease);

            ManualResetEventSlim sourceEntered = new ManualResetEventSlim(false);
            ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);

            h.Writer.Entered = sourceEntered;
            h.Writer.WaitFor = gateHeld;

            Exception holderError = null;
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
            holder.Start();

            // The writer runs once and the post-consume gate is held, so the
            // sink parks without a terminal result.
            Assert.That(h.Sink.TryAppend(token, lease, out NvencRunChunkSinkResult first), Is.False);
            Assert.That(first.IsNone, Is.True);
            Assert.That(h.State.IsPoisoned, Is.False);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));

            // The consume claim stays held while parked: Return and
            // re-reservation are refused until the deferred completion runs.
            Assert.That(h.Buffer.Return(lease), Is.False);
            Assert.That(h.Buffer.TryBeginWrite(MakeToken(2), out _), Is.False);

            release.Set();
            Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "holder did not exit");
            Assert.That(holderError, Is.Null);

            // Resuming converges without re-running the writer.
            Assert.That(h.Sink.TryAppend(token, lease, out NvencRunChunkSinkResult retry), Is.True);
            Assert.That(retry.IsAppended, Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.Free));
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(1));
            Assert.That(h.Sink.AccumulatedByteLength, Is.EqualTo(56));
        }

        [Test]
        public void Append_PendingResumeMismatch_PoisonsWithoutWriterContact()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 56, Seed, out NvencOwnedAccessUnitLease lease);

            ManualResetEventSlim sourceEntered = new ManualResetEventSlim(false);
            ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);

            h.Writer.Entered = sourceEntered;
            h.Writer.WaitFor = gateHeld;

            Exception holderError = null;
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
            holder.Start();

            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.False);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));

            release.Set();
            Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "holder did not exit");
            Assert.That(holderError, Is.Null);

            // The consume claim stays held while parked, so the buffer cannot
            // be returned or re-reserved out-of-band.
            Assert.That(h.Buffer.Return(lease), Is.False);

            // Resuming with a different work token breaks the pending record
            // and poisons without contacting the writer again.
            Assert.Throws<InvalidOperationException>(() => h.Sink.TryAppend(MakeToken(999), lease, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));
        }

        [Test]
        public void Append_DeferredConsumeThenPoison_ResumePoisonsWithoutCountersOrReturn()
        {
            Harness h = new Harness();
            CaptureFrameWorkToken token = h.ProduceOwnedLease(1, 56, Seed, out NvencOwnedAccessUnitLease lease);

            ManualResetEventSlim sourceEntered = new ManualResetEventSlim(false);
            ManualResetEventSlim gateHeld = new ManualResetEventSlim(false);
            ManualResetEventSlim release = new ManualResetEventSlim(false);

            h.Writer.Entered = sourceEntered;
            h.Writer.WaitFor = gateHeld;

            Exception holderError = null;
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
            holder.Start();

            // The writer runs once and the post-consume gate is held, so the
            // sink parks with the consume claim still held.
            Assert.That(h.Sink.TryAppend(token, lease, out NvencRunChunkSinkResult first), Is.False);
            Assert.That(first.IsNone, Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));

            release.Set();
            Assert.That(holder.Join(WatchdogTimeoutMs), Is.True, "holder did not exit");
            Assert.That(holderError, Is.Null);

            // Poison after the writer returned but before the deferred
            // completion commits.
            Assert.That(h.State.TryPoison(), Is.True);

            // Resuming finds the process poisoned and never commits the return,
            // the counters, or a terminal result.
            Assert.Throws<InvalidOperationException>(() => h.Sink.TryAppend(token, lease, out _));
            Assert.That(h.State.IsPoisoned, Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));
            Assert.That(h.Buffer.Phase, Is.EqualTo(NvencAccessUnitPhase.SinkOwned));
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(0));
            Assert.That(h.Sink.AccumulatedByteLength, Is.EqualTo(0));
        }

        [Test]
        public void Append_ChunkLimit_BoundaryAndExceeded()
        {
            Harness h = new Harness();

            // Exactly at the limit succeeds.
            long near = NvencBringUpProfileV1.MaxChunkByteLength - 100;
            SetAccumulatedByteLength(h.Sink, near);
            CaptureFrameWorkToken boundaryToken = h.ProduceOwnedLease(1, 100, Seed, out NvencOwnedAccessUnitLease boundaryLease);

            Assert.That(h.Sink.TryAppend(boundaryToken, boundaryLease, out NvencRunChunkSinkResult boundaryResult), Is.True);
            Assert.That(boundaryResult.IsAppended, Is.True);
            Assert.That(h.Sink.AccumulatedByteLength, Is.EqualTo(NvencBringUpProfileV1.MaxChunkByteLength));
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));

            // One byte past the limit is rejected before the writer is contacted.
            CaptureFrameWorkToken overToken = h.ProduceOwnedLease(2, 1, Seed, out NvencOwnedAccessUnitLease overLease);
            Assert.That(h.Sink.TryAppend(overToken, overLease, out NvencRunChunkSinkResult overResult), Is.True);
            Assert.That(overResult.IsControlledFailure, Is.True);
            Assert.That(h.Writer.CallCount, Is.EqualTo(1));
            Assert.That(h.Sink.AppendedCount, Is.EqualTo(1));
        }

        [Test]
        public void Append_120SmallPayloads_SingleLinearPath()
        {
            Harness h = new Harness();

            for (long frameId = 1; frameId <= 120; frameId++)
            {
                CaptureFrameWorkToken token = h.ProduceOwnedLease(frameId, 100, Seed, out NvencOwnedAccessUnitLease lease);
                Assert.That(h.Sink.TryAppend(token, lease, out NvencRunChunkSinkResult result), Is.True);
                Assert.That(result.IsAppended, Is.True);
                Assert.That(result.AppendedByteLength, Is.EqualTo(100));
            }

            Assert.That(h.Sink.AppendedCount, Is.EqualTo(120));
            Assert.That(h.Sink.AccumulatedByteLength, Is.EqualTo(120L * 100L));
            Assert.That(h.Sink.LastFrameId, Is.EqualTo(120));
            Assert.That(h.Writer.CallCount, Is.EqualTo(120));
        }

        [Test]
        public void AppendOutcome_NoneIsZero_AppendedIsNotZero()
        {
            Assert.That((int)NvencRunChunkAppendOutcome.None, Is.EqualTo(0));
            Assert.That((int)NvencRunChunkAppendOutcome.Appended, Is.EqualTo(1));
            Assert.That((int)NvencRunChunkAppendOutcome.RejectedBeforeWrite, Is.EqualTo(2));
            Assert.That((int)NvencRunChunkAppendOutcome.Indeterminate, Is.EqualTo(3));
        }

        [Test]
        public void SinkResult_ExclusiveShapes()
        {
            NvencRunChunkSinkResult none = default;
            Assert.That(none.IsNone, Is.True);
            Assert.That(none.IsAppended, Is.False);
            Assert.That(none.IsControlledFailure, Is.False);

            CaptureFrameWorkToken token = MakeToken(1);
            NvencRunChunkSinkResult appended = NvencRunChunkSinkResult.Appended(token, 128);
            Assert.That(appended.IsAppended, Is.True);
            Assert.That(appended.IsNone, Is.False);
            Assert.That(appended.IsControlledFailure, Is.False);

            NvencRunChunkSinkResult failure = NvencRunChunkSinkResult.ControlledFailure(token);
            Assert.That(failure.IsControlledFailure, Is.True);
            Assert.That(failure.IsAppended, Is.False);
            Assert.That(failure.IsNone, Is.False);
        }

        [Test]
        public void SinkShape_NoQueueWorkerTaskThreadIoOrAllocation()
        {
            string directory = RuntimeDirectory();
            string source =
                File.ReadAllText(Path.Combine(directory, "NvencRunChunkSink.cs")) +
                File.ReadAllText(Path.Combine(directory, "INvencRunChunkAppender.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencRunChunkAppendOutcome.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencRunChunkSinkResult.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencOwnedAccessUnitBoundaryStatus.cs"));

            string[] forbidden =
            {
                "lock (", "Monitor", "ManualResetEvent", "AutoResetEvent", "WaitHandle", "SpinWait",
                "new Thread", "ThreadPool", "Task", "File.", "Directory.", "FileStream", "DllImport",
                "UnityEngine", "Application.", "SystemInfo", "GraphicsDevice", "IntPtr", "SafeHandle",
                "NvEnc", "RenderTexture", "SHA", "MD5", "Hash", "H.264", "H264", "ArrayPool",
                "Enumerable", ".Select(", ".Where(", ".ToList(", "new Queue", "new Dictionary",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "Sink source must not reference: " + word);
            }
        }

        [Test]
        public void SinkRunPath_NoRenameFinalizeDescriptorTruncateRetryOrAllocation()
        {
            string directory = RuntimeDirectory();
            string sinkSource = File.ReadAllText(Path.Combine(directory, "NvencRunChunkSink.cs"));

            string[] methodBodies =
            {
                ExtractMethodBody(sinkSource, "TryAppend"),
                ExtractMethodBody(sinkSource, "CompleteAppend"),
                ExtractMethodBody(sinkSource, "CompleteConsumed"),
                ExtractMethodBody(sinkSource, "CompleteAppended"),
                ExtractMethodBody(sinkSource, "CompleteControlledFailure"),
                ExtractMethodBody(sinkSource, "CompleteCommitted"),
                ExtractMethodBody(sinkSource, "CompleteDeferred"),
                ExtractMethodBody(sinkSource, "CompletePending"),
                ExtractMethodBody(sinkSource, "Park"),
                ExtractMethodBody(sinkSource, "ClearPending"),
                ExtractMethodBody(sinkSource, "MatchesPending"),
                ExtractMethodBody(sinkSource, "PoisonAndThrow"),
            };

            string[] forbidden =
            {
                "rename", "Rename", "finalize", "Finalize", "Descriptor", "descriptor",
                "truncate", "Truncate", "rollback", "Rollback", "retry", "Retry",
                "ArtifactCompletion", "new byte[", "new []", "Array.", "ArrayPool",
                "new List", "new Dictionary", "new Queue", "new Stack", "new HashSet",
            };

            foreach (string body in methodBodies)
            {
                foreach (string word in forbidden)
                {
                    Assert.That(body, Does.Not.Contain(word), "Sink run path must not contain: " + word);
                }
            }
        }

        [Test]
        public void SinkAndResult_DoNotExposeBackingArray()
        {
            Type[] noStorageTypes =
            {
                typeof(NvencRunChunkSink),
                typeof(NvencRunChunkSinkResult),
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

                foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    Assert.That(method.ReturnType, Is.Not.EqualTo(typeof(byte[])), type.Name + " must not return the backing array.");
                }
            }
        }

        private static byte[] ExpectedPattern(int length, byte seed)
        {
            byte[] pattern = new byte[length];
            for (int i = 0; i < length; i++)
            {
                pattern[i] = (byte)(seed + i);
            }
            return pattern;
        }

        private static CaptureFrameWorkToken MakeToken(long frameId)
        {
            return new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, frameId);
        }

        private static void SetAccumulatedByteLength(NvencRunChunkSink sink, long value)
        {
            FieldInfo field = typeof(NvencRunChunkSink).GetField(
                "_accumulatedByteLength", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(sink, value);
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

        private sealed class FakeAppender : INvencRunChunkAppender
        {
            internal int CallCount;
            internal byte[] LastBuffer;
            internal int LastOffset;
            internal int LastValidLength;
            internal byte[] CapturedContent;
            internal NvencRunChunkAppendOutcome Outcome = NvencRunChunkAppendOutcome.Appended;
            internal Exception ExceptionToThrow;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim WaitFor;

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                CallCount++;
                LastBuffer = buffer;
                LastOffset = offset;
                LastValidLength = validLength;

                if (Entered != null)
                {
                    Entered.Set();
                }

                if (WaitFor != null)
                {
                    WaitFor.Wait(WatchdogTimeoutMs);
                }

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (Outcome == NvencRunChunkAppendOutcome.Appended)
                {
                    CapturedContent = new byte[validLength];
                    Array.Copy(buffer, offset, CapturedContent, 0, validLength);
                }

                return Outcome;
            }
        }

        private sealed class Harness
        {
            internal NvencCaptureProcessState State = new NvencCaptureProcessState();
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal FakeAppender Writer = new FakeAppender();
            internal NvencRunChunkSink Sink;

            internal Harness()
            {
                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
            }

            internal CaptureFrameWorkToken ProduceOwnedLease(
                long frameId,
                int length,
                byte seed,
                out NvencOwnedAccessUnitLease lease)
            {
                CaptureFrameWorkToken token = MakeToken(frameId);
                Assert.That(Buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
                Assert.That(Buffer.TryCopyCompletedOutput(write, default, new PatternSource(length, seed), out _),
                    Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
                Assert.That(Buffer.TryTransferToSink(write, out lease), Is.True);
                return token;
            }
        }
    }
}
