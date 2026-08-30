using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable internal token proving one synchronous Capture Index commit
    /// call fully succeeded. It holds no bytes, file handle, hash, or path
    /// copies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The receipt is not a filesystem snapshot; it only records that one
    /// synchronous call succeeded. <see cref="IsValid"/> and
    /// <see cref="IsIssuedFor"/> recompute without throwing, so a receipt whose
    /// operation or lease has been corrupted or released becomes invalid.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunCaptureIndexCommitReceipt
    {
        private readonly ICaptureRunCaptureIndexCommitter _issuedBy;
        private readonly CaptureRunCaptureIndexCommitOperation _operation;

        internal CaptureRunCaptureIndexCommitReceipt(
            ICaptureRunCaptureIndexCommitter issuedBy,
            CaptureRunCaptureIndexCommitOperation operation)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            _issuedBy = issuedBy;
            _operation = operation;
        }

        internal ICaptureRunCaptureIndexCommitter IssuedBy => _issuedBy;

        internal CaptureRunCaptureIndexCommitOperation Operation => _operation;

        internal CaptureRunCaptureIndexCommitMode Mode => _operation.Mode;

        internal string TemporaryPath => _operation.TemporaryPath;

        internal string FinalPath => _operation.FinalPath;

        internal long ByteCount => _operation.ByteCount;

        internal CaptureRunPublicationArtifactRecoveryActionPlan ActionPlan => _operation.ActionPlan;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                return _issuedBy != null
                    && _operation != null
                    && _operation.IsValid;
            }
        }

        internal bool IsIssuedFor(
            ICaptureRunCaptureIndexCommitter committer,
            CaptureRunCaptureIndexCommitOperation operation)
        {
            return IsValid
                && ReferenceEquals(committer, _issuedBy)
                && ReferenceEquals(operation, _operation);
        }
    }
}
