using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless entry point for the PngJson capture-complete release
    /// operation. It null-checks the lifecycle evidence once and then delegates
    /// exactly once to the operation's atomic factory, so no operation is ever
    /// built outside the single validated issuance path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Build"/> holds no state, keeps no cache, registry, retry
    /// count, or journal, and is not an <see cref="IDisposable"/>,
    /// MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal static class PngJsonCapturePublicationCaptureCompleteReleaseOperationFactory
    {
        internal static PngJsonCapturePublicationCaptureCompleteReleaseOperation Build(
            PngJsonCapturePublicationCaptureCompleteLifecycleEvidence lifecycleEvidence)
        {
            if (lifecycleEvidence == null)
            {
                throw new ArgumentNullException(nameof(lifecycleEvidence));
            }

            return PngJsonCapturePublicationCaptureCompleteReleaseOperation.Create(lifecycleEvidence);
        }
    }
}
