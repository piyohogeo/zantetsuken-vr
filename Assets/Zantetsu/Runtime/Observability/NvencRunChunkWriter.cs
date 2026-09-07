using System;
using System.Security.Cryptography;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 Run chunk writer. It is both the
    /// <see cref="INvencRunChunkAppender"/> injected into the
    /// <see cref="NvencRunChunkSink"/> and the
    /// <see cref="INvencRunChunkFinalizer"/>. It appends exact byte ranges to
    /// the fixed <c>.partial</c> session while feeding a single incremental
    /// SHA-256, then finalizes by closing the append handle, moving the pending
    /// file to the fixed staging path, and issuing a receipt.
    /// </summary>
    /// <remarks>
    /// This type holds no queue, list, frame-id array, access-unit copy, or
    /// thread. It never reads the file again, never attempts a step more than
    /// once, and never shortens or discards content.
    /// </remarks>
    internal sealed class NvencRunChunkWriter : INvencRunChunkAppender, INvencRunChunkFinalizer
    {
        private readonly INvencRunChunkFileSession _session;
        private readonly IncrementalHash _hash;
        private NvencRunChunkWriterState _state;
        private long _appendCount;
        private long _accumulatedByteLength;

        internal NvencRunChunkWriter(INvencRunChunkFileSession session)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            _state = NvencRunChunkWriterState.Open;
        }

        internal NvencRunChunkWriterState State => _state;

        internal long AppendCount => _appendCount;

        internal long AccumulatedByteLength => _accumulatedByteLength;

        public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
        {
            if (_state != NvencRunChunkWriterState.Open)
            {
                return NvencRunChunkAppendOutcome.Indeterminate;
            }

            if (!IsValidRange(buffer, offset, validLength))
            {
                return NvencRunChunkAppendOutcome.RejectedBeforeWrite;
            }

            if ((long)validLength + _accumulatedByteLength > NvencBringUpProfileV1.MaxChunkByteLength)
            {
                return NvencRunChunkAppendOutcome.RejectedBeforeWrite;
            }

            NvencRunChunkAppendOutcome outcome;
            try
            {
                outcome = _session.Append(buffer, offset, validLength);
            }
            catch
            {
                _state = NvencRunChunkWriterState.Faulted;
                throw;
            }

            switch (outcome)
            {
                case NvencRunChunkAppendOutcome.Appended:
                    _hash.AppendData(buffer, offset, validLength);
                    _appendCount++;
                    _accumulatedByteLength += validLength;
                    break;

                case NvencRunChunkAppendOutcome.RejectedBeforeWrite:
                    break;

                default:
                    _state = NvencRunChunkWriterState.Faulted;
                    break;
            }

            return outcome;
        }

        public NvencRunChunkFinalizationReceipt FinalizeChunk(
            NvencRunChunkFinalizationOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            if (_state != NvencRunChunkWriterState.Open)
            {
                throw new InvalidOperationException("Writer must be Open to finalize.");
            }

            if (!operation.Sink.IsBackedBy(this))
            {
                throw new ArgumentException(
                    "Operation sink is not backed by this writer.", nameof(operation));
            }

            if (_appendCount != operation.AppendedCount ||
                _accumulatedByteLength != operation.AccumulatedByteLength)
            {
                throw new ArgumentException(
                    "Writer ledger does not match the operation evidence.", nameof(operation));
            }

            _state = NvencRunChunkWriterState.Finalizing;

            try
            {
                byte[] digest = _hash.GetHashAndReset();
                string contentHash = ToLowerHex(digest);

                _session.CloseAppendHandle();
                _session.MovePendingToStaging();

                CaptureArtifactDescriptor descriptor =
                    NvencRunChunkArtifactDescriptorFactory.Create(
                        operation.ArtifactId,
                        operation.AccumulatedByteLength,
                        contentHash);

                NvencRunChunkFinalizationReceipt receipt =
                    NvencRunChunkFinalizationReceipt.Create(this, operation, descriptor);

                _state = NvencRunChunkWriterState.Finalized;
                return receipt;
            }
            catch
            {
                _state = NvencRunChunkWriterState.Faulted;
                throw;
            }
        }

        private static bool IsValidRange(byte[] buffer, int offset, int validLength)
        {
            return buffer != null &&
                offset >= 0 &&
                validLength >= 1 &&
                validLength <= NvencBringUpProfileV1.MaxAccessUnitByteLength &&
                offset <= buffer.Length - validLength;
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
    }
}
