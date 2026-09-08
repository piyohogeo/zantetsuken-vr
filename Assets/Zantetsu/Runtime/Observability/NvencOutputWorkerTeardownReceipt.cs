using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable success receipt of one Output Worker teardown call. It holds
    /// only the exact teardown implementation that issued it; no token, nonce,
    /// Lease, native handle, or array is retained or exposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsIssuedFor"/> recomputes the exact-reference correlation
    /// without throwing, so a null, foreign, or uninitialized receipt is never
    /// accepted. This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class NvencOutputWorkerTeardownReceipt
    {
        private readonly INvencOutputWorkerTeardown _issuedBy;

        private NvencOutputWorkerTeardownReceipt(INvencOutputWorkerTeardown issuedBy)
        {
            _issuedBy = issuedBy;
        }

        /// <summary>
        /// Atomic issuance factory: null-checks the exact teardown
        /// implementation and holds only that reference.
        /// </summary>
        internal static NvencOutputWorkerTeardownReceipt Issue(INvencOutputWorkerTeardown issuedBy)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            return new NvencOutputWorkerTeardownReceipt(issuedBy);
        }

        /// <summary>
        /// Exact issuance identity: true only for the exact teardown
        /// implementation that issued this receipt. A null, foreign, or
        /// uninitialized receipt returns false without throwing.
        /// </summary>
        internal bool IsIssuedFor(INvencOutputWorkerTeardown issuedBy)
        {
            return issuedBy != null && ReferenceEquals(_issuedBy, issuedBy);
        }
    }
}
