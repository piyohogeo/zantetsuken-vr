using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Pure, stateless builder that converts one PngJson cleanup orchestration
    /// result into a capture-complete notification operation. It holds no
    /// fields and performs no filesystem work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The builder rejects a null result and otherwise delegates exactly once
    /// to the operation's atomic factory, which owns the single full
    /// validation, token issuance, and notification-specific correlation. The
    /// builder performs no validation, path derivation, or token issuance of
    /// its own.
    /// </para>
    /// </remarks>
    internal static class PngJsonCapturePublicationCaptureCompleteNotificationOperationFactory
    {
        internal static PngJsonCapturePublicationCaptureCompleteNotificationOperation Build(
            PngJsonCapturePublicationCaptureCompleteCleanupOrchestrationResult cleanupResult)
        {
            if (cleanupResult == null)
            {
                throw new ArgumentNullException(nameof(cleanupResult));
            }

            return PngJsonCapturePublicationCaptureCompleteNotificationOperation.Create(cleanupResult);
        }
    }
}
