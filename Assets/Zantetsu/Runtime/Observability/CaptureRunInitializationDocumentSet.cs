using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free Capture Run initialization document set: the
    /// canonical bytes of the four markers of a Run's initialization plan,
    /// serialized once at construction and exposed only as defensive copies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Construction serializes, in order, the staging and final initialization
    /// markers with <see cref="CaptureRunInitializationMarkerCodec"/>, then the
    /// staging and final ready markers with
    /// <see cref="CaptureRunReadyMarkerCodec"/>, verifies that both ready
    /// markers produce byte-for-byte identical canonical bytes, and verifies
    /// that every byte array is non-empty and within the codec's documented
    /// maximum. The plan reference and the three owned byte arrays are held
    /// only after every check succeeds.
    /// </para>
    /// <para>
    /// Because the two ready markers of a binding always serialize to identical
    /// bytes, only one ready byte array is held; the two ready getters each
    /// return an independent defensive copy of that array. Every getter returns
    /// a fresh copy so callers can never mutate the internal arrays. The caller
    /// owns each returned copy, and mutating it never affects this set.
    /// </para>
    /// <para>
    /// This type owns the arrays returned by the codecs, but never re-computes
    /// or caches any hash, never decodes or re-parses, and treats the existing
    /// binding as the authority for init hashes. It performs no marker
    /// construction, no binding or plan factory call, no initialization ID
    /// generation, no file, directory, or stream access, no tmp write, flush,
    /// or rename, no OS locking, and no recovery or collision classification.
    /// It is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationDocumentSet
    {
        private readonly CaptureRunInitializationPlan _plan;
        private readonly byte[] _stagingInitializationBytes;
        private readonly byte[] _finalInitializationBytes;
        private readonly byte[] _readyBytes;

        internal CaptureRunInitializationDocumentSet(CaptureRunInitializationPlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            CaptureRunMarkerBinding binding = plan.MarkerBinding;
            if (binding == null)
            {
                throw new ArgumentException("Plan must hold a marker binding.", nameof(plan));
            }

            byte[] stagingInitializationBytes = CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.StagingInitialization);
            byte[] finalInitializationBytes = CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.FinalInitialization);
            byte[] stagingReadyBytes = CaptureRunReadyMarkerCodec.SerializeCanonical(binding.StagingReady);
            byte[] finalReadyBytes = CaptureRunReadyMarkerCodec.SerializeCanonical(binding.FinalReady);

            if (!BytesEqual(stagingReadyBytes, finalReadyBytes))
            {
                throw new InvalidOperationException("Staging and final ready markers must serialize to identical canonical bytes.");
            }

            RequireNonEmptyWithinLimit(stagingInitializationBytes, CaptureRunInitializationMarkerCodec.MaximumCanonicalByteCount, "Staging initialization");
            RequireNonEmptyWithinLimit(finalInitializationBytes, CaptureRunInitializationMarkerCodec.MaximumCanonicalByteCount, "Final initialization");
            RequireNonEmptyWithinLimit(stagingReadyBytes, CaptureRunReadyMarkerCodec.MaximumCanonicalByteCount, "Ready");

            _plan = plan;
            _stagingInitializationBytes = stagingInitializationBytes;
            _finalInitializationBytes = finalInitializationBytes;
            _readyBytes = stagingReadyBytes;
        }

        internal CaptureRunInitializationPlan Plan => _plan;

        internal int StagingInitializationByteCount => _stagingInitializationBytes.Length;

        internal int FinalInitializationByteCount => _finalInitializationBytes.Length;

        internal int ReadyByteCount => _readyBytes.Length;

        internal byte[] GetStagingInitializationBytes() => Copy(_stagingInitializationBytes);

        internal byte[] GetFinalInitializationBytes() => Copy(_finalInitializationBytes);

        internal byte[] GetStagingReadyBytes() => Copy(_readyBytes);

        internal byte[] GetFinalReadyBytes() => Copy(_readyBytes);

        private static byte[] Copy(byte[] source)
        {
            byte[] copy = new byte[source.Length];
            Array.Copy(source, copy, source.Length);
            return copy;
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static void RequireNonEmptyWithinLimit(byte[] bytes, int maximumByteCount, string label)
        {
            if (bytes.Length == 0)
            {
                throw new InvalidOperationException(label + " canonical bytes must not be empty.");
            }

            if (bytes.Length > maximumByteCount)
            {
                throw new InvalidOperationException(label + " canonical bytes exceed the maximum allowed byte count.");
            }
        }
    }
}
