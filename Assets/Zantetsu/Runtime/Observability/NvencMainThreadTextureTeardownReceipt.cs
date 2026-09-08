using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable success receipt of one Main Thread NV12 Texture teardown
    /// call. It holds only the exact teardown implementation that issued it;
    /// no token, nonce, generation, Texture array, Texture reference, or
    /// Lease is retained or exposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsIssuedFor"/> recomputes the exact-reference correlation
    /// and this receipt's own structure without throwing, so a null, foreign,
    /// or uninitialized receipt is never accepted. This type owns, mutates,
    /// and disposes nothing and is not an <see cref="IDisposable"/>,
    /// MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class NvencMainThreadTextureTeardownReceipt
    {
        private readonly INvencMainThreadTextureTeardown _issuedBy;

        private NvencMainThreadTextureTeardownReceipt(INvencMainThreadTextureTeardown issuedBy)
        {
            _issuedBy = issuedBy;
        }

        /// <summary>
        /// Atomic issuance factory: null-checks the exact teardown
        /// implementation and holds only that reference. Issued only after a
        /// normal completion.
        /// </summary>
        internal static NvencMainThreadTextureTeardownReceipt Issue(INvencMainThreadTextureTeardown issuedBy)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            return new NvencMainThreadTextureTeardownReceipt(issuedBy);
        }

        /// <summary>
        /// Exact issuance identity: true only for the exact teardown
        /// implementation that issued this receipt, and only while this
        /// receipt's own structure is intact. A null, foreign, or
        /// uninitialized receipt returns false without throwing.
        /// </summary>
        internal bool IsIssuedFor(INvencMainThreadTextureTeardown issuedBy)
        {
            return _issuedBy != null && issuedBy != null && ReferenceEquals(_issuedBy, issuedBy);
        }
    }
}
