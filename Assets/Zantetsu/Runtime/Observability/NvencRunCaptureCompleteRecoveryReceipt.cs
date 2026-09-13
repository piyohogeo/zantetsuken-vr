using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable evidence that one Phase 0.11 NVENC recovery CaptureComplete
    /// call succeeded: the exact completer that accepted it and the exact
    /// operation it accepted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receipt holds exactly two readonly references, the exact completer
    /// and the exact operation, and they are the whole state. The recovery
    /// decision, its snapshot, the optional commit receipt, the publication
    /// recovery decision, the authoritative plan, the root layout, and the Run
    /// identity are read from <see cref="Operation"/>: the receipt restates
    /// none of it and duplicates none of it as a field of its own. Which of the
    /// two authorities the Run arrived by is still there to read on that
    /// operation rather than flattened away.
    /// </para>
    /// <para>
    /// This is process-local evidence of one successful synchronous
    /// CaptureComplete call and nothing more: not a durable filesystem proof,
    /// not evidence that any cleanup ran, and not evidence that the OS lock was
    /// released. It says only that a call made under the still-held lock
    /// returned successfully.
    /// It is minted only on success, through the success-only factory, which
    /// requires a completer, an operation, and that the operation is still
    /// valid.
    /// </para>
    /// <para>
    /// This type does not own, change, or dispose the operation graph's
    /// lifetime, touches no file, releases
    /// no lock, and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject. <see cref="IsValid"/> never throws, so once the OS
    /// lock is released the operation becomes invalid and this receipt follows
    /// it.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureCompleteRecoveryReceipt
    {
        private readonly INvencRunCaptureCompleteRecoveryCompleter _completer;
        private readonly NvencRunCaptureCompleteRecoveryOperation _operation;

        private NvencRunCaptureCompleteRecoveryReceipt(
            INvencRunCaptureCompleteRecoveryCompleter completer,
            NvencRunCaptureCompleteRecoveryOperation operation)
        {
            _completer = completer;
            _operation = operation;
        }

        /// <summary>
        /// Mints the evidence of one accepted CaptureComplete. There is no
        /// failure or unknown-outcome receipt: a call that did not succeed
        /// leaves by an exception.
        /// </summary>
        internal static NvencRunCaptureCompleteRecoveryReceipt Completed(
            INvencRunCaptureCompleteRecoveryCompleter completer,
            NvencRunCaptureCompleteRecoveryOperation operation)
        {
            if (completer == null)
            {
                throw new ArgumentNullException(nameof(completer));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            return new NvencRunCaptureCompleteRecoveryReceipt(completer, operation);
        }

        internal INvencRunCaptureCompleteRecoveryCompleter Completer => _completer;

        internal NvencRunCaptureCompleteRecoveryOperation Operation => _operation;

        internal bool IsValid
        {
            get
            {
                try
                {
                    return _completer != null && _operation != null && _operation.IsValid;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Whether this receipt is the evidence of that exact completer
        /// accepting that exact operation, and is still valid.
        /// </summary>
        internal bool IsIssuedFor(
            INvencRunCaptureCompleteRecoveryCompleter completer,
            NvencRunCaptureCompleteRecoveryOperation operation)
        {
            try
            {
                return ReferenceEquals(_completer, completer)
                    && ReferenceEquals(_operation, operation)
                    && IsValid;
            }
            catch
            {
                return false;
            }
        }
    }
}
