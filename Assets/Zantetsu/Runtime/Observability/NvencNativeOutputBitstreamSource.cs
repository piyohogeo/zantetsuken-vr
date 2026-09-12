using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The production <see cref="INvencOutputBitstreamSource"/>: it waits for
    /// one submitted picture, copies its access unit into the caller's storage,
    /// and gives the lock and the map back.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session is the caller's. This holds one reference to that exact
    /// owner and nothing else - no process state, no buffer of its own, no
    /// timeout, poll, thread, or flush. The call blocks on the picture's
    /// completion event, so it belongs to the output worker alone.
    /// </para>
    /// <para>
    /// False is the controllable rejection: nothing usable came back, but the
    /// native ownership was resolved and the slot is usable again. Anything the
    /// session raises propagates exactly as it came, so the caller poisons
    /// rather than guessing what is still held.
    /// </para>
    /// <para>
    /// The destination array is handed to the native copy for the duration of
    /// the call and is neither stored nor published here. The work token is the
    /// caller's correlation; the native side is addressed by the sample slot
    /// and its generation alone.
    /// </para>
    /// </remarks>
    internal sealed class NvencNativeOutputBitstreamSource : INvencOutputBitstreamSource
    {
        private readonly NvencNativeEncoderSessionOwner _session;

        internal NvencNativeOutputBitstreamSource(NvencNativeEncoderSessionOwner session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            _session = session;
        }

        public bool TryCopyCompletedOutput(
            in CaptureFrameWorkToken workToken,
            in NvencEncodeSampleSlotLease sampleSlot,
            byte[] destination,
            int destinationCapacity,
            out int validLength)
        {
            return _session.TryCopyCompletedOutput(
                sampleSlot.SlotIndex,
                (ulong)sampleSlot.Generation,
                destination,
                destinationCapacity,
                out validLength);
        }
    }
}
