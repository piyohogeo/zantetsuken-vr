using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable completed step of a PngJson capture-complete cleanup
    /// execution: the exact prepared step that was executed, the success
    /// receipt returned by the backend, and the exact action plan validation
    /// token the step was executed with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The type owns exactly three reference fields and has no public
    /// constructor. The single token-gated factory requires the prepared step
    /// to remain index-locally valid for the token and, for each side-effecting
    /// action, requires one receipt issued by the exact backend for the
    /// prepared step's exact operation and token.
    /// <c>CaptureCompleteReady</c> carries no receipt.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> re-validates the full plan exactly once with the
    /// held token and then delegates to
    /// <see cref="IsValidIndexLocal(PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken)"/>,
    /// which first requires the caller's token to be reference-identical to the
    /// held token, so a separately re-issued token for the same plan fails.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep
    {
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep _preparedStep;
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupReceipt _cleanupReceipt;
        private readonly PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken _token;

        private PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep(
            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep preparedStep,
            PngJsonCapturePublicationCaptureCompleteCleanupReceipt cleanupReceipt,
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            _preparedStep = preparedStep;
            _cleanupReceipt = cleanupReceipt;
            _token = token;
        }

        /// <summary>
        /// Single token-gated atomic factory. It first requires the prepared
        /// step to remain index-locally valid for the token — before the step's
        /// action is read — so a structurally corrupted prepared step is
        /// rejected with an <see cref="ArgumentException"/> instead of leaking a
        /// getter exception. For a side-effecting action the backend must be
        /// present and the receipt must be present and issued by that exact
        /// backend for the prepared step's exact operation and token.
        /// <c>CaptureCompleteReady</c> requires no receipt. No plan
        /// re-validation, token re-issuance, entry scan, or filesystem access
        /// happens.
        /// </summary>
        internal static PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep CreateIndexLocal(
            IPngJsonCapturePublicationCaptureCompleteCleanupBackend backend,
            PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep preparedStep,
            PngJsonCapturePublicationCaptureCompleteCleanupReceipt cleanupReceipt,
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            if (preparedStep == null)
            {
                throw new ArgumentNullException(nameof(preparedStep));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (!preparedStep.IsValidIndexLocal(token))
            {
                throw new ArgumentException(
                    "Prepared step must be index-locally valid for the token.",
                    nameof(preparedStep));
            }

            switch (preparedStep.Action)
            {
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact:
                case CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker:
                case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker:
                case CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot:
                    if (backend == null)
                    {
                        throw new ArgumentNullException(nameof(backend));
                    }

                    if (cleanupReceipt == null)
                    {
                        throw new ArgumentNullException(nameof(cleanupReceipt));
                    }

                    if (!cleanupReceipt.IsIssuedFor(backend, preparedStep.CleanupOperation, token))
                    {
                        throw new ArgumentException(
                            "Cleanup receipt must be issued by the backend for the prepared step's exact operation and token.",
                            nameof(cleanupReceipt));
                    }

                    break;

                case CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady:
                    if (cleanupReceipt != null)
                    {
                        throw new ArgumentException(
                            "CaptureCompleteReady must hold no cleanup receipt.",
                            nameof(cleanupReceipt));
                    }

                    break;

                default:
                    throw new ArgumentException(
                        "Prepared step action must be a defined cleanup action.",
                        nameof(preparedStep));
            }

            return new PngJsonCapturePublicationCaptureCompleteCleanupCompletedStep(preparedStep, cleanupReceipt, token);
        }

        internal PngJsonCapturePublicationCaptureCompleteCleanupPreparedStep PreparedStep => _preparedStep;

        internal PngJsonCapturePublicationCaptureCompleteCleanupReceipt CleanupReceipt => _cleanupReceipt;

        internal int StepIndex => _preparedStep.StepIndex;

        internal CaptureRunPublicationCaptureCompleteCleanupAction Action => _preparedStep.Action;

        /// <summary>
        /// Exception-safe full validity: re-validates the held plan exactly
        /// once against the held token and then delegates to the index-local
        /// predicate with that same held token. Never re-issues a token.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (_preparedStep == null || _token == null)
                {
                    return false;
                }

                PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan = _preparedStep.ActionPlan;
                if (actionPlan == null || !actionPlan.IsValidWithToken(_token))
                {
                    return false;
                }

                return IsValidIndexLocal(_token);
            }
        }

        /// <summary>
        /// O(1), exception-safe index-local check: the caller's token must be
        /// reference-identical to the held token first, the prepared step must
        /// remain index-locally valid, and the receipt must match the action —
        /// exactly one receipt re-verified by its own issuer for the prepared
        /// step's exact operation and held token for a side-effecting action,
        /// and none for <c>CaptureCompleteReady</c>. It never re-validates the
        /// whole plan, re-issues a token, scans an entry, or touches a
        /// filesystem.
        /// </summary>
        internal bool IsValidIndexLocal(PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token)
        {
            try
            {
                if (token == null || !ReferenceEquals(token, _token))
                {
                    return false;
                }

                if (_preparedStep == null || !_preparedStep.IsValidIndexLocal(token))
                {
                    return false;
                }

                switch (_preparedStep.Action)
                {
                    case CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary:
                    case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary:
                    case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact:
                    case CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot:
                    case CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan:
                    case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker:
                    case CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker:
                    case CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot:
                        return _cleanupReceipt != null
                            && _cleanupReceipt.IsIssuedFor(
                                _cleanupReceipt.IssuedBy,
                                _preparedStep.CleanupOperation,
                                _token);

                    case CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady:
                        return _cleanupReceipt == null;

                    default:
                        return false;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
