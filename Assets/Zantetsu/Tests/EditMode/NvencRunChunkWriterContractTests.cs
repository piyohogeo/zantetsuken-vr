using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using NvencAccessUnitCopyStatus = Zantetsu.Observability.NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 Run chunk writer: the single type that
    /// is both the sink appender and the finalizer, driving a fake file session
    /// deterministically from append through receipt issuance.
    /// </summary>
    public class NvencRunChunkWriterContractTests
    {
        private const byte Seed = 0x40;

        [Test]
        public void Append_ConcatenatedSha256_MatchesReceiptHash()
        {
            FakeFileSession session = new FakeFileSession();
            WriterHarness h = new WriterHarness(session);
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");

            NvencRunChunkFinalizationReceipt receipt = h.Writer.FinalizeChunk(operation);

            Assert.That(receipt.IsValid, Is.True);
            Assert.That(receipt.Descriptor.ContentHash, Is.EqualTo(ComputeSha256Hex(session.Captured.ToArray())));
            Assert.That(h.Writer.State, Is.EqualTo(NvencRunChunkWriterState.Finalized));
        }

        [Test]
        public void Append_OnlyAppendedAdvancesCountLength()
        {
            FakeFileSession session = new FakeFileSession();
            WriterHarness h = new WriterHarness(session);

            CaptureFrameWorkToken token1 = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease1);
            Assert.That(h.Sink.TryAppend(token1, lease1, out _), Is.True);
            Assert.That(h.Writer.AppendCount, Is.EqualTo(1));
            Assert.That(h.Writer.AccumulatedByteLength, Is.EqualTo(64));

            session.Outcome = NvencRunChunkAppendOutcome.RejectedBeforeWrite;
            CaptureFrameWorkToken token2 = h.ProduceOwnedLease(2, 48, Seed, out NvencOwnedAccessUnitLease lease2);
            Assert.That(h.Sink.TryAppend(token2, lease2, out NvencRunChunkSinkResult result), Is.True);
            Assert.That(result.IsControlledFailure, Is.True);

            Assert.That(h.Writer.AppendCount, Is.EqualTo(1));
            Assert.That(h.Writer.AccumulatedByteLength, Is.EqualTo(64));
            Assert.That(h.Writer.State, Is.EqualTo(NvencRunChunkWriterState.Open));
            Assert.That(session.Captured.Count, Is.EqualTo(64));
        }

        [Test]
        public void Append_AccessUnitLimitChunkLimit_SessionNotContacted()
        {
            FakeFileSession session = new FakeFileSession();
            WriterHarness h = new WriterHarness(session);

            Assert.That(
                h.Writer.Append(new byte[1], 0, (int)NvencBringUpProfileV1.MaxAccessUnitByteLength + 1),
                Is.EqualTo(NvencRunChunkAppendOutcome.RejectedBeforeWrite));
            Assert.That(h.Writer.Append(new byte[1], 0, 0),
                Is.EqualTo(NvencRunChunkAppendOutcome.RejectedBeforeWrite));
            Assert.That(h.Writer.Append(null, 0, 1),
                Is.EqualTo(NvencRunChunkAppendOutcome.RejectedBeforeWrite));
            Assert.That(h.Writer.Append(new byte[4], 5, 1),
                Is.EqualTo(NvencRunChunkAppendOutcome.RejectedBeforeWrite));
            Assert.That(session.AppendCallCount, Is.EqualTo(0));
            Assert.That(h.Writer.State, Is.EqualTo(NvencRunChunkWriterState.Open));

            SetField(h.Writer, "_accumulatedByteLength", NvencBringUpProfileV1.MaxChunkByteLength);
            Assert.That(h.Writer.Append(new byte[1], 0, 1),
                Is.EqualTo(NvencRunChunkAppendOutcome.RejectedBeforeWrite));
            Assert.That(session.AppendCallCount, Is.EqualTo(0));
        }

        [Test]
        public void Append_IndeterminateOrSessionException_Faults()
        {
            FakeFileSession indeterminateSession = new FakeFileSession
            {
                Outcome = NvencRunChunkAppendOutcome.Indeterminate,
            };
            WriterHarness hIndeterminate = new WriterHarness(indeterminateSession);
            Assert.That(hIndeterminate.Writer.Append(new byte[8], 0, 8),
                Is.EqualTo(NvencRunChunkAppendOutcome.Indeterminate));
            Assert.That(hIndeterminate.Writer.State, Is.EqualTo(NvencRunChunkWriterState.Faulted));

            int calls = indeterminateSession.AppendCallCount;
            Assert.That(hIndeterminate.Writer.Append(new byte[8], 0, 8),
                Is.EqualTo(NvencRunChunkAppendOutcome.Indeterminate));
            Assert.That(indeterminateSession.AppendCallCount, Is.EqualTo(calls));

            FakeFileSession throwingSession = new FakeFileSession();
            InvalidOperationException boom = new InvalidOperationException("boom");
            throwingSession.AppendException = boom;
            WriterHarness hThrowing = new WriterHarness(throwingSession);
            InvalidOperationException thrown = Assert.Throws<InvalidOperationException>(
                () => hThrowing.Writer.Append(new byte[8], 0, 8));
            Assert.That(ReferenceEquals(thrown, boom), Is.True);
            Assert.That(hThrowing.Writer.State, Is.EqualTo(NvencRunChunkWriterState.Faulted));
        }

        [Test]
        public void Append_HashCommitFailure_FaultsAfterSessionAppended()
        {
            FakeFileSession session = new FakeFileSession();
            WriterHarness h = new WriterHarness(session);
            IncrementalHash hash = GetHashField(h.Writer);
            hash.Dispose();

            Assert.Throws<ObjectDisposedException>(() => h.Writer.Append(new byte[8], 0, 8));

            Assert.That(h.Writer.State, Is.EqualTo(NvencRunChunkWriterState.Faulted));
            Assert.That(session.Captured.Count, Is.EqualTo(8));
            Assert.That(session.AppendCallCount, Is.EqualTo(1));
        }

        [Test]
        public void Finalize_DisposesHashExactlyOnce()
        {
            FakeFileSession session = new FakeFileSession();
            WriterHarness h = new WriterHarness(session);
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");
            IncrementalHash hash = GetHashField(h.Writer);

            NvencRunChunkFinalizationReceipt receipt = h.Writer.FinalizeChunk(operation);
            Assert.That(receipt.IsValid, Is.True);

            Assert.Throws<ObjectDisposedException>(() => hash.GetHashAndReset());
        }

        [Test]
        public void Fault_DisposesHash_IndeterminateOrException()
        {
            FakeFileSession indeterminateSession = new FakeFileSession
            {
                Outcome = NvencRunChunkAppendOutcome.Indeterminate,
            };
            WriterHarness hIndeterminate = new WriterHarness(indeterminateSession);
            IncrementalHash indeterminateHash = GetHashField(hIndeterminate.Writer);
            Assert.That(hIndeterminate.Writer.Append(new byte[8], 0, 8),
                Is.EqualTo(NvencRunChunkAppendOutcome.Indeterminate));
            Assert.That(hIndeterminate.Writer.State, Is.EqualTo(NvencRunChunkWriterState.Faulted));
            Assert.Throws<ObjectDisposedException>(() => indeterminateHash.GetHashAndReset());

            FakeFileSession throwingSession = new FakeFileSession();
            throwingSession.AppendException = new InvalidOperationException("boom");
            WriterHarness hThrowing = new WriterHarness(throwingSession);
            IncrementalHash throwingHash = GetHashField(hThrowing.Writer);
            Assert.Throws<InvalidOperationException>(() => hThrowing.Writer.Append(new byte[8], 0, 8));
            Assert.That(hThrowing.Writer.State, Is.EqualTo(NvencRunChunkWriterState.Faulted));
            Assert.Throws<ObjectDisposedException>(() => throwingHash.GetHashAndReset());
        }

        [Test]
        public void Finalize_ExactWriterOnly()
        {
            FakeFileSession session = new FakeFileSession();
            WriterHarness h = new WriterHarness(session);
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");

            FakeFileSession otherSession = new FakeFileSession();
            NvencRunChunkWriter otherWriter = new NvencRunChunkWriter(otherSession);

            Assert.That(operation.Sink.IsBackedBy(h.Writer), Is.True);
            Assert.That(operation.Sink.IsBackedBy(otherWriter), Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => otherWriter.FinalizeChunk(operation));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(otherSession.CloseCallCount, Is.EqualTo(0));
            Assert.That(otherSession.MoveCallCount, Is.EqualTo(0));
            Assert.That(otherWriter.State, Is.EqualTo(NvencRunChunkWriterState.Open));
        }

        [Test]
        public void Finalize_NullOrInvalidOperation_NoSessionContact()
        {
            FakeFileSession session = new FakeFileSession();
            WriterHarness h = new WriterHarness(session);

            ArgumentNullException nullEx = Assert.Throws<ArgumentNullException>(() => h.Writer.FinalizeChunk(null));
            Assert.That(nullEx.ParamName, Is.EqualTo("operation"));
            Assert.That(session.CloseCallCount, Is.EqualTo(0));
            Assert.That(session.MoveCallCount, Is.EqualTo(0));
            Assert.That(h.Writer.State, Is.EqualTo(NvencRunChunkWriterState.Open));

            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");
            CaptureFrameWorkToken token = h.ProduceOwnedLease(4, 32, Seed, out NvencOwnedAccessUnitLease lease);
            Assert.That(h.Sink.TryAppend(token, lease, out _), Is.True);
            Assert.That(operation.IsValid, Is.False);

            ArgumentException invalidEx = Assert.Throws<ArgumentException>(() => h.Writer.FinalizeChunk(operation));
            Assert.That(invalidEx.ParamName, Is.EqualTo("operation"));
            Assert.That(session.CloseCallCount, Is.EqualTo(0));
            Assert.That(session.MoveCallCount, Is.EqualTo(0));
            Assert.That(h.Writer.State, Is.EqualTo(NvencRunChunkWriterState.Open));
        }

        [Test]
        public void Finalize_CountLengthMismatch_RejectedBeforeSideEffect()
        {
            FakeFileSession session = new FakeFileSession();
            WriterHarness h = new WriterHarness(session);
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");

            SetField(h.Writer, "_appendCount", 1L);
            Assert.That(operation.AppendedCount, Is.EqualTo(2));

            Assert.Throws<ArgumentException>(() => h.Writer.FinalizeChunk(operation));
            Assert.That(session.CloseCallCount, Is.EqualTo(0));
            Assert.That(session.MoveCallCount, Is.EqualTo(0));
            Assert.That(h.Writer.State, Is.EqualTo(NvencRunChunkWriterState.Open));
        }

        [Test]
        public void Finalize_OrderHashCloseMoveDescriptorReceipt()
        {
            FakeFileSession session = new FakeFileSession();
            WriterHarness h = new WriterHarness(session);
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");

            NvencRunChunkFinalizationReceipt receipt = h.Writer.FinalizeChunk(operation);

            Assert.That(receipt.IsValid, Is.True);
            Assert.That(session.Events, Is.EqualTo(new[] { "append", "append", "close", "move" }));
            Assert.That(session.CloseCallCount, Is.EqualTo(1));
            Assert.That(session.MoveCallCount, Is.EqualTo(1));
        }

        [Test]
        public void Finalize_CloseExceptionMoveException_SamePropagationNoReceiptNoRetry()
        {
            FakeFileSession closeSession = new FakeFileSession();
            InvalidOperationException closeBoom = new InvalidOperationException("close boom");
            closeSession.CloseException = closeBoom;
            WriterHarness hClose = new WriterHarness(closeSession);
            NvencRunChunkFinalizationOperation closeOperation = MakeOperation(hClose, "chunk/0");
            InvalidOperationException closeThrown = Assert.Throws<InvalidOperationException>(
                () => hClose.Writer.FinalizeChunk(closeOperation));
            Assert.That(ReferenceEquals(closeThrown, closeBoom), Is.True);
            Assert.That(hClose.Writer.State, Is.EqualTo(NvencRunChunkWriterState.Faulted));
            Assert.That(closeSession.CloseCallCount, Is.EqualTo(1));
            Assert.That(closeSession.MoveCallCount, Is.EqualTo(0));

            FakeFileSession moveSession = new FakeFileSession();
            InvalidOperationException moveBoom = new InvalidOperationException("move boom");
            moveSession.MoveException = moveBoom;
            WriterHarness hMove = new WriterHarness(moveSession);
            NvencRunChunkFinalizationOperation moveOperation = MakeOperation(hMove, "chunk/0");
            InvalidOperationException moveThrown = Assert.Throws<InvalidOperationException>(
                () => hMove.Writer.FinalizeChunk(moveOperation));
            Assert.That(ReferenceEquals(moveThrown, moveBoom), Is.True);
            Assert.That(hMove.Writer.State, Is.EqualTo(NvencRunChunkWriterState.Faulted));
            Assert.That(moveSession.CloseCallCount, Is.EqualTo(1));
            Assert.That(moveSession.MoveCallCount, Is.EqualTo(1));
        }

        [Test]
        public void Finalize_SecondCall_NoCloseMoveReexecution()
        {
            FakeFileSession session = new FakeFileSession();
            WriterHarness h = new WriterHarness(session);
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");

            NvencRunChunkFinalizationReceipt receipt = h.Writer.FinalizeChunk(operation);
            Assert.That(receipt.IsValid, Is.True);
            Assert.That(session.CloseCallCount, Is.EqualTo(1));
            Assert.That(session.MoveCallCount, Is.EqualTo(1));

            Assert.Throws<InvalidOperationException>(() => h.Writer.FinalizeChunk(operation));
            Assert.That(session.CloseCallCount, Is.EqualTo(1));
            Assert.That(session.MoveCallCount, Is.EqualTo(1));
        }

        [Test]
        public void Finalize_PostPoison_ReceiptStillValid()
        {
            FakeFileSession session = new FakeFileSession();
            WriterHarness h = new WriterHarness(session);
            NvencRunChunkFinalizationOperation operation = MakeOperation(h, "chunk/0");
            NvencRunChunkFinalizationReceipt receipt = h.Writer.FinalizeChunk(operation);
            Assert.That(receipt.IsValid, Is.True);

            Assert.That(h.State.TryPoison(), Is.True);
            Assert.That(receipt.IsValid, Is.True);
        }

        [Test]
        public void WriterSource_NoFullReadHashRecomputeRetryTruncateRollback()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencRunChunkWriter.cs"));

            string[] forbidden =
            {
                "File.", "Directory.", "FileStream", "Stream", "ReadAllBytes", "ReadAllText",
                "ReadToEnd", "ComputeHash", "Retry", "Rollback", "Truncate", "retry", "rollback",
                "truncate", "new Thread", "ThreadPool", "Task", "lock (", "Monitor",
                "UnityEngine", "Application.", "DllImport", "IntPtr", "SafeHandle",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "writer source must not contain: " + word);
            }
        }

        [Test]
        public void AppendPath_NoQueueTaskThreadLinqAllocation()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencRunChunkWriter.cs"));
            string appendBody = ExtractMethodBody(source, "Append");

            string[] forbidden =
            {
                "new Queue", "new List", "new Dictionary", "new Stack", "new HashSet",
                "Enumerable", ".Select(", ".Where(", ".ToList(", "Array.", "new byte[",
                "new long[", "new char[", "Task", "Thread", "lock (", "Monitor",
            };

            foreach (string word in forbidden)
            {
                Assert.That(appendBody, Does.Not.Contain(word), "append path must not contain: " + word);
            }
        }

        private static NvencRunChunkFinalizationOperation MakeOperation(WriterHarness h, string artifactId)
        {
            CaptureFrameWorkToken token1 = h.ProduceOwnedLease(1, 64, Seed, out NvencOwnedAccessUnitLease lease1);
            Assert.That(h.Sink.TryAppend(token1, lease1, out _), Is.True);
            CaptureFrameWorkToken token3 = h.ProduceOwnedLease(3, 48, Seed, out NvencOwnedAccessUnitLease lease3);
            Assert.That(h.Sink.TryAppend(token3, lease3, out _), Is.True);
            Assert.That(h.Sink.TryCaptureFinalizationEvidence(2, out NvencRunChunkSinkFinalizationEvidence evidence), Is.True);
            return NvencRunChunkFinalizationOperation.Create(h.Sink, evidence, artifactId);
        }

        private static string ComputeSha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return ToLowerHex(sha.ComputeHash(bytes));
            }
        }

        private static string ToLowerHex(byte[] bytes)
        {
            const string hex = "0123456789abcdef";
            char[] chars = new char[bytes.Length * 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                chars[i * 2] = hex[b >> 4];
                chars[i * 2 + 1] = hex[b & 0x0F];
            }

            return new string(chars);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(target, value);
        }

        private static IncrementalHash GetHashField(NvencRunChunkWriter writer)
        {
            FieldInfo field = typeof(NvencRunChunkWriter).GetField(
                "_hash", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return (IncrementalHash)field.GetValue(writer);
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

        private sealed class FakeFileSession : INvencRunChunkFileSession
        {
            internal readonly List<byte> Captured = new List<byte>();
            internal readonly List<string> Events = new List<string>();
            internal int AppendCallCount;
            internal int CloseCallCount;
            internal int MoveCallCount;
            internal NvencRunChunkAppendOutcome Outcome = NvencRunChunkAppendOutcome.Appended;
            internal Exception AppendException;
            internal Exception CloseException;
            internal Exception MoveException;

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                AppendCallCount++;
                Events.Add("append");
                if (AppendException != null)
                {
                    throw AppendException;
                }

                if (Outcome == NvencRunChunkAppendOutcome.Appended)
                {
                    for (int i = 0; i < validLength; i++)
                    {
                        Captured.Add(buffer[offset + i]);
                    }
                }

                return Outcome;
            }

            public void CloseAppendHandle()
            {
                CloseCallCount++;
                Events.Add("close");
                if (CloseException != null)
                {
                    throw CloseException;
                }
            }

            public void MovePendingToStaging()
            {
                MoveCallCount++;
                Events.Add("move");
                if (MoveException != null)
                {
                    throw MoveException;
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
                for (int i = 0; i < _length; i++)
                {
                    destination[i] = (byte)(_seed + i);
                }

                validLength = _length;
                return true;
            }
        }

        private sealed class WriterHarness
        {
            internal NvencCaptureProcessState State = new NvencCaptureProcessState();
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal NvencRunChunkWriter Writer;
            internal NvencRunChunkSink Sink;

            internal WriterHarness(INvencRunChunkFileSession session)
            {
                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Writer = new NvencRunChunkWriter(session);
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
