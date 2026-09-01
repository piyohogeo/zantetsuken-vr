using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects a validated capture-complete cleanup orchestration result to a
    /// completion notifier exactly once and freezes the accepted receipt into
    /// an immutable notification result.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The coordinator owns exactly one read-only dependency — the completion
    /// notifier — and holds no operation, receipt, result, or cleanup result in
    /// any field. It is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// <para>
    /// <see cref="Execute"/> runs the fixed sequence exactly once per call:
    /// reject a null cleanup result, build the notification operation exactly
    /// once without re-validating the cleanup result beforehand, notify exactly
    /// once, and construct the immutable notification result whose constructor
    /// immediately correlates the receipt. Notifier exceptions propagate
    /// unchanged and unwrapped. The coordinator performs no retry, no
    /// re-cleanup, no re-inspection, no rollback, no disposal, no lease
    /// release, and no draft registry or filesystem contact.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteNotificationCoordinator
    {
        private readonly ICaptureRunPublicationCaptureCompleteNotifier _notifier;

        internal CaptureRunPublicationCaptureCompleteNotificationCoordinator(
            ICaptureRunPublicationCaptureCompleteNotifier notifier)
        {
            if (notifier == null)
            {
                throw new ArgumentNullException(nameof(notifier));
            }

            _notifier = notifier;
        }

        internal ICaptureRunPublicationCaptureCompleteNotifier Notifier => _notifier;

        internal CaptureRunPublicationCaptureCompleteNotificationResult Execute(
            CaptureRunPublicationCaptureCompleteCleanupOrchestrationResult cleanupResult)
        {
            if (cleanupResult == null)
            {
                throw new ArgumentNullException(nameof(cleanupResult));
            }

            CaptureRunPublicationCaptureCompleteNotificationOperation operation =
                new CaptureRunPublicationCaptureCompleteNotificationOperation(cleanupResult);

            CaptureRunPublicationCaptureCompleteNotificationReceipt receipt = _notifier.Notify(operation);

            return new CaptureRunPublicationCaptureCompleteNotificationResult(this, operation, receipt);
        }
    }
}
