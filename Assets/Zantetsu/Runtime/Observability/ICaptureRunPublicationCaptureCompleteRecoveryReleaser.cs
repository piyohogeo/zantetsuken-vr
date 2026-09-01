using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Single-attempt boundary that releases the exact recovery open outcome of
    /// one accepted capture-complete lifecycle evidence and returns a success
    /// receipt. The implementation, thread choice, and retry orchestration are
    /// the responsibility of the caller.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Release"/> is synchronous and performs exactly one attempt:
    /// it calls the operation's exact <c>OpenOutcome.Dispose()</c> once and
    /// returns a non-null receipt on success. It never disposes a different
    /// outcome, a session, or a raw lease, and it never retries, rolls back,
    /// re-inspects, notifies, touches a registry, or performs filesystem work.
    /// </para>
    /// <para>
    /// The method throws <see cref="ArgumentNullException"/> with
    /// <c>ParamName</c> <c>operation</c> for a null operation, and
    /// <see cref="ArgumentException"/> with <c>ParamName</c> <c>operation</c>
    /// for an operation that is not currently releasable (forged, foreign
    /// owner, or already fully released). A disposal exception propagates on
    /// the same instance and no receipt is returned.
    /// </para>
    /// <para>
    /// On success the implementation verifies that both the exact open outcome
    /// and its exact lock lease are no longer created before minting the
    /// receipt.
    /// </para>
    /// </remarks>
    internal interface ICaptureRunPublicationCaptureCompleteRecoveryReleaser
    {
        CaptureRunPublicationCaptureCompleteRecoveryReleaseReceipt Release(
            CaptureRunPublicationCaptureCompleteRecoveryReleaseOperation operation);
    }
}
