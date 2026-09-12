using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The production <see cref="INvencGpuConversionCommandIssuer"/>: it asks
    /// the native session to issue the conversion one submission reserved,
    /// binding exactly that submission's sync lease, source surface, and encode
    /// sample slot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session is the caller's. This holds one reference to that exact
    /// owner and nothing else - no queue, process state, retry, poll, cache,
    /// thread, or task - and takes ownership of no surface, lease, or command
    /// buffer. The indices and the generation are passed through as the record
    /// states them; nothing is translated or looked up.
    /// </para>
    /// <para>
    /// What the session concluded is passed through unchanged: issued, known
    /// not issued, or an exception when neither can be established.
    /// </para>
    /// </remarks>
    internal sealed class NvencNativeGpuConversionCommandIssuer : INvencGpuConversionCommandIssuer
    {
        private readonly NvencNativeEncoderSessionOwner _session;

        internal NvencNativeGpuConversionCommandIssuer(NvencNativeEncoderSessionOwner session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            _session = session;
        }

        public bool TryIssue(in NvencSubmissionRecord record)
        {
            return _session.TryIssueConversionCommand(
                record.SyncSlot.SlotIndex,
                record.Surface.SlotIndex,
                record.SampleSlot.SlotIndex,
                (ulong)record.SyncSlot.Generation);
        }
    }
}
