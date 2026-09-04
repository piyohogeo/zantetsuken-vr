using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Standard single-attempt release boundary for a PngJson capture-complete
    /// release operation: it releases the exact ownership lease once and
    /// returns a success receipt only after the ownership lease is fully
    /// released.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Release"/> is a single synchronous method. It rejects a null
    /// operation with <see cref="ArgumentNullException"/>, rejects a
    /// non-retryable operation with <see cref="ArgumentException"/> before any
    /// side effect, disposes the exact ownership lease exactly once, never
    /// retries, and never rolls back, re-inspects, notifies, touches a
    /// registry, or performs filesystem work.
    /// </para>
    /// <para>
    /// A disposal exception is never caught, wrapped, or replaced; it
    /// propagates on the same instance and no receipt is returned. On normal
    /// return the implementation verifies that the ownership lease has fully
    /// completed release, then issues one receipt and verifies it is issued
    /// for this releaser and operation.
    /// </para>
    /// </remarks>
    internal interface IPngJsonCapturePublicationCaptureCompleteReleaser
    {
        PngJsonCapturePublicationCaptureCompleteReleaseReceipt Release(
            PngJsonCapturePublicationCaptureCompleteReleaseOperation operation);
    }
}
