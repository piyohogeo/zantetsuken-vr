using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable record of what one Phase 0.11 NVENC Capture Index recovery
    /// inspection observed: the operation it was issued for, what the final
    /// <c>capture.index</c> was, and what the <c>capture.index.tmp</c>
    /// temporary was.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Those three values - one reference and two observations - are the whole
    /// state. No raw bytes, decoded or copied plan, path, hash, exception, or
    /// handle is held: the comparison against the authoritative plan is already
    /// inside each status, and nothing else about the documents survives the
    /// inspection. The snapshot holds observed facts only - no disposition,
    /// commit mode, or cleanup hint - and no auxiliary field infers one status
    /// from the other.
    /// </para>
    /// <para>
    /// This type performs no filesystem, codec, or hash work, owns and
    /// disposes nothing, and never throws from <see cref="IsValid"/>, which
    /// re-checks the operation and both statuses so a snapshot whose lock has
    /// gone becomes invalid.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunCaptureIndexRecoveryInspectionSnapshot
    {
        private readonly NvencRunCaptureIndexRecoveryInspectionOperation _operation;
        private readonly NvencRunCaptureIndexObservationStatus _finalIndexStatus;
        private readonly NvencRunCaptureIndexObservationStatus _temporaryIndexStatus;

        internal NvencRunCaptureIndexRecoveryInspectionSnapshot(
            NvencRunCaptureIndexRecoveryInspectionOperation operation,
            NvencRunCaptureIndexObservationStatus finalIndexStatus,
            NvencRunCaptureIndexObservationStatus temporaryIndexStatus)
        {
            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Operation must be valid.", nameof(operation));
            }

            if (!IsObservedStatus(finalIndexStatus))
            {
                throw new ArgumentException(
                    "The final Capture Index must carry an observed status.",
                    nameof(finalIndexStatus));
            }

            if (!IsObservedStatus(temporaryIndexStatus))
            {
                throw new ArgumentException(
                    "The temporary Capture Index must carry an observed status.",
                    nameof(temporaryIndexStatus));
            }

            _operation = operation;
            _finalIndexStatus = finalIndexStatus;
            _temporaryIndexStatus = temporaryIndexStatus;
        }

        internal NvencRunCaptureIndexRecoveryInspectionOperation Operation => _operation;

        internal NvencRunCaptureIndexObservationStatus FinalIndexStatus => _finalIndexStatus;

        internal NvencRunCaptureIndexObservationStatus TemporaryIndexStatus =>
            _temporaryIndexStatus;

        internal CapturePublicationPlan AuthoritativePlan => _operation.AuthoritativePlan;

        internal CaptureRunRootLayout RootLayout => _operation.RootLayout;

        internal long TestRunId => _operation.TestRunId;

        internal string RunInitializationId => _operation.RunInitializationId;

        internal bool IsValid
        {
            get
            {
                try
                {
                    return _operation != null
                        && _operation.IsValid
                        && IsObservedStatus(_finalIndexStatus)
                        && IsObservedStatus(_temporaryIndexStatus);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Every document reached one of the five observed outcomes.
        /// <see cref="NvencRunCaptureIndexObservationStatus.None"/> means the
        /// document was never observed, which is not a fact and must never be
        /// classified as one.
        /// </summary>
        private static bool IsObservedStatus(NvencRunCaptureIndexObservationStatus status)
        {
            switch (status)
            {
                case NvencRunCaptureIndexObservationStatus.Absent:
                case NvencRunCaptureIndexObservationStatus.MatchesAuthoritative:
                case NvencRunCaptureIndexObservationStatus.CanonicalMismatch:
                case NvencRunCaptureIndexObservationStatus.Invalid:
                case NvencRunCaptureIndexObservationStatus.LimitExceeded:
                    return true;

                default:
                    return false;
            }
        }
    }
}
