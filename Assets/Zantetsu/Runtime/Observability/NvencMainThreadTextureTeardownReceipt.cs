using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable success receipt of one Main Thread NV12 Texture teardown
    /// call. It holds only the exact teardown implementation that issued it
    /// and the exact Run chunk context it is bound to; no token, nonce,
    /// generation, Texture array, Texture reference, or Lease is retained or
    /// exposed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="IsIssuedFor"/> recomputes the exact-reference correlation of
    /// both the issuer and the bound context, and this receipt's own
    /// structure, without throwing, so a null, foreign, or uninitialized
    /// receipt is never accepted. This type owns, mutates, and disposes
    /// nothing and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class NvencMainThreadTextureTeardownReceipt
    {
        private readonly INvencMainThreadTextureTeardown _issuedBy;
        private readonly NvencRunChunkContext _context;

        private NvencMainThreadTextureTeardownReceipt(
            INvencMainThreadTextureTeardown issuedBy,
            NvencRunChunkContext context)
        {
            _issuedBy = issuedBy;
            _context = context;
        }

        /// <summary>
        /// Atomic issuance factory: null-checks the exact teardown
        /// implementation and the exact bound Run chunk context, and requires
        /// the implementation to be actually bound to that context, so a
        /// semantically invalid receipt can never be issued. Holds only those
        /// two references. Issued only after a normal completion.
        /// </summary>
        internal static NvencMainThreadTextureTeardownReceipt Issue(
            INvencMainThreadTextureTeardown issuedBy,
            NvencRunChunkContext context)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (!issuedBy.IsBoundTo(context))
            {
                throw new ArgumentException(
                    "The teardown implementation must be bound to the exact Run chunk context.", nameof(issuedBy));
            }

            return new NvencMainThreadTextureTeardownReceipt(issuedBy, context);
        }

        /// <summary>
        /// Exact issuance identity: true only for the exact teardown
        /// implementation that issued this receipt and the exact Run chunk
        /// context it is bound to, and only while this receipt's own structure
        /// is intact. A null, foreign, or uninitialized receipt returns false
        /// without throwing.
        /// </summary>
        internal bool IsIssuedFor(INvencMainThreadTextureTeardown issuedBy, NvencRunChunkContext context)
        {
            return _issuedBy != null && issuedBy != null
                && _context != null && context != null
                && ReferenceEquals(_issuedBy, issuedBy)
                && ReferenceEquals(_context, context);
        }
    }
}
