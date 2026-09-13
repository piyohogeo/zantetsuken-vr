using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free Capture Run initialization document set: the
    /// marker paths and markers of one Run, plus the canonical bytes of the
    /// four markers it must write, serialized once at construction and exposed
    /// only as defensive copies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Construction builds the marker path set and the marker binding from the
    /// same root layout, so they describe one Run by construction and nothing
    /// correlates them afterwards. It then serializes, in order, the staging
    /// and final initialization markers with
    /// <see cref="CaptureRunInitializationMarkerCodec"/> and the ready marker
    /// with <see cref="CaptureRunReadyMarkerCodec"/>, and verifies that every
    /// byte array is non-empty and within the codec's documented maximum. The
    /// two references and the three owned byte arrays are held only after every
    /// check succeeds.
    /// </para>
    /// <para>
    /// Both Run roots receive the same ready content, so only one ready byte
    /// array is held; the two ready getters each return an independent
    /// defensive copy of it. Every getter returns a fresh copy so callers can
    /// never mutate the internal arrays. The caller owns each returned copy,
    /// and mutating it never affects this set.
    /// </para>
    /// <para>
    /// This type owns the arrays returned by the codecs, but never re-computes
    /// or caches any hash, never decodes or re-parses, and treats the binding
    /// as the authority for init hashes. It generates no initialization ID,
    /// performs no file, directory, or stream access, no tmp write, flush, or
    /// rename, no OS locking, and no recovery or collision classification.
    /// It is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationDocumentSet
    {
        private readonly CaptureRunMarkerPathSet _markerPaths;
        private readonly CaptureRunMarkerBinding _markerBinding;
        private readonly byte[] _stagingInitializationBytes;
        private readonly byte[] _finalInitializationBytes;
        private readonly byte[] _readyBytes;

        internal CaptureRunInitializationDocumentSet(
            CaptureRunRootLayout rootLayout,
            string runInitializationId)
        {
            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(rootLayout);

            CaptureRunMarkerBinding binding = new CaptureRunMarkerBinding(
                rootLayout.TestRunId,
                runInitializationId,
                rootLayout.StagingRunRootSha256,
                rootLayout.FinalRunRootSha256);

            byte[] stagingInitializationBytes = CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.StagingInitialization);
            byte[] finalInitializationBytes = CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.FinalInitialization);
            byte[] readyBytes = CaptureRunReadyMarkerCodec.SerializeCanonical(binding.StagingReady);

            RequireNonEmptyWithinLimit(stagingInitializationBytes, CaptureRunInitializationMarkerCodec.MaximumCanonicalByteCount, "Staging initialization");
            RequireNonEmptyWithinLimit(finalInitializationBytes, CaptureRunInitializationMarkerCodec.MaximumCanonicalByteCount, "Final initialization");
            RequireNonEmptyWithinLimit(readyBytes, CaptureRunReadyMarkerCodec.MaximumCanonicalByteCount, "Ready");

            _markerPaths = markerPaths;
            _markerBinding = binding;
            _stagingInitializationBytes = stagingInitializationBytes;
            _finalInitializationBytes = finalInitializationBytes;
            _readyBytes = readyBytes;
        }

        internal CaptureRunMarkerPathSet MarkerPaths => _markerPaths;

        internal CaptureRunMarkerBinding MarkerBinding => _markerBinding;

        internal CaptureRunRootLayout RootLayout => _markerPaths.RootLayout;

        internal long TestRunId => _markerPaths.RootLayout.TestRunId;

        internal string RunInitializationId => _markerBinding.RunInitializationId;

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
