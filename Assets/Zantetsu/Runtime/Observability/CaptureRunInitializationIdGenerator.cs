using System;
using System.Security.Cryptography;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Cryptographic 128-bit Capture Run initialization ID generator. Each call
    /// produces a fresh 32-character lowercase ASCII hex identifier.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Callers are responsible for invoking <see cref="Create"/> exactly once
    /// and only after both OS locks have been acquired. The returned value is a
    /// correlation identifier, not a secret, so the entropy buffer is not
    /// erased before returning.
    /// </para>
    /// <para>
    /// This type holds no fields and no mutable static state, mixes in no
    /// counter, clock, GUID, process id, or thread id, and performs no file,
    /// directory, or stream access and no Unity static API access.
    /// </para>
    /// </remarks>
    internal static class CaptureRunInitializationIdGenerator
    {
        internal static string Create()
        {
            byte[] entropy = new byte[16];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(entropy);
            }

            return EncodeEntropy(entropy);
        }

        private static string EncodeEntropy(byte[] entropy)
        {
            if (entropy == null)
            {
                throw new ArgumentNullException(nameof(entropy));
            }

            if (entropy.Length != 16)
            {
                throw new ArgumentException("Entropy must be exactly 16 bytes.", nameof(entropy));
            }

            return CaptureRunInitializationMarkerCodec.ToLowerHex(entropy);
        }
    }
}
