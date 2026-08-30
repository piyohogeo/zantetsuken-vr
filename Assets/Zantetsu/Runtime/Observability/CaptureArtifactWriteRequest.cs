using System;

namespace Zantetsu.Observability
{
    /// <summary>Immutable store request with a private defensive payload copy.</summary>
    internal sealed class CaptureArtifactWriteRequest
    {
        private readonly CaptureArtifactDescriptor _descriptor;
        private readonly byte[] _payload;

        internal CaptureArtifactWriteRequest(CaptureArtifactDescriptor descriptor, byte[] payload)
        {
            if (descriptor == null || !descriptor.IsValid) throw new ArgumentException("Descriptor must be valid.", nameof(descriptor));
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            if (payload.LongLength != descriptor.ByteLength) throw new ArgumentException("Payload length must match descriptor.", nameof(payload));

            _descriptor = descriptor;
            _payload = new byte[payload.Length];
            Array.Copy(payload, _payload, payload.Length);
        }

        internal CaptureArtifactDescriptor Descriptor => _descriptor;
        internal int ByteCount => _payload.Length;

        internal byte[] GetPayload()
        {
            byte[] copy = new byte[_payload.Length];
            Array.Copy(_payload, copy, _payload.Length);
            return copy;
        }
    }
}
