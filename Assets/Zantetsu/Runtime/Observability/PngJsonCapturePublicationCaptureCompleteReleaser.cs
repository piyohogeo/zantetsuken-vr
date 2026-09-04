using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Standard single-attempt boundary that releases the exact ownership
    /// lease of one PngJson capture-complete release operation and returns a
    /// success receipt only after the ownership lease is fully released.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Release"/> is synchronous and performs exactly one attempt.
    /// It requires the operation to be retryable via
    /// <see cref="PngJsonCapturePublicationCaptureCompleteReleaseOperation.CanRelease"/>,
    /// not via the full issuance validity, so a partially released ownership
    /// lease can be retried by the caller passing the same operation again. It
    /// disposes the exact ownership lease once, never a different outcome,
    /// session, or raw lease, and never retries, rolls back, re-inspects,
    /// notifies, touches a registry, or performs filesystem work.
    /// </para>
    /// <para>
    /// A disposal exception is never caught, wrapped, or replaced; it
    /// propagates on the same instance and no receipt is returned. On normal
    /// return the implementation verifies that the ownership lease has fully
    /// completed release via
    /// <see cref="CaptureRunInitializationSessionOwnershipLease.IsReleaseComplete"/>,
    /// then mints one receipt and verifies it is issued for this releaser and
    /// operation.
    /// </para>
    /// <para>
    /// The type holds no instance or mutable static state, keeps no retry
    /// count, completion flag, or last operation or receipt, and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteReleaser : IPngJsonCapturePublicationCaptureCompleteReleaser
    {
        internal PngJsonCapturePublicationCaptureCompleteReleaser()
        {
        }

        public PngJsonCapturePublicationCaptureCompleteReleaseReceipt Release(
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.CanRelease)
            {
                throw new ArgumentException(
                    "Release operation must be retryable with an intact issuance binding.",
                    nameof(operation));
            }

            CaptureRunInitializationSessionOwnershipLease ownershipLease = operation.OwnershipLease;
            if (ownershipLease == null)
            {
                throw new ArgumentException(
                    "Release operation must hold an exact ownership lease.",
                    nameof(operation));
            }

            ownershipLease.Dispose();

            if (!ownershipLease.IsReleaseComplete)
            {
                throw new InvalidOperationException(
                    "Release did not fully release the ownership lease.");
            }

            PngJsonCapturePublicationCaptureCompleteReleaseReceipt receipt =
                new PngJsonCapturePublicationCaptureCompleteReleaseReceipt(this, operation);

            if (receipt == null || !receipt.IsIssuedFor(this, operation))
            {
                throw new InvalidOperationException(
                    "Release receipt is not issued for this operation.");
            }

            return receipt;
        }
    }
}
