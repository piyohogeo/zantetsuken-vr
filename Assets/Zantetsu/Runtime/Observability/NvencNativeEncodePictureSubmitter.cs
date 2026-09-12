using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// The production <see cref="INvencEncodePictureSubmitter"/>: it maps one
    /// encode sample slot's input and submits exactly one picture for it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session is the caller's. This holds one reference to that exact
    /// owner and nothing else - no process state, no queue, no retry, no
    /// fallback, and no completion wait - and names only the slot and its
    /// generation, which is all the native side needs: the work token and the
    /// work slot stay on this side of the boundary.
    /// </para>
    /// <para>
    /// False is the narrow controllable failure the caller reports as a failed
    /// submit. Anything the session raises propagates exactly as it came, so
    /// the caller poisons rather than fabricating a record for work whose
    /// ownership is unknown.
    /// </para>
    /// </remarks>
    internal sealed class NvencNativeEncodePictureSubmitter : INvencEncodePictureSubmitter
    {
        private readonly NvencNativeEncoderSessionOwner _session;

        internal NvencNativeEncodePictureSubmitter(NvencNativeEncoderSessionOwner session)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            _session = session;
        }

        public bool TrySubmit(in NvencEncodePictureSubmitOperation operation)
        {
            return _session.TrySubmitEncodePicture(
                operation.SampleSlot.SlotIndex,
                (ulong)operation.SampleSlot.Generation);
        }
    }
}
