using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable completed step of a recovery execution: the prepared step plus
    /// the one receipt produced by executing it, or no receipt for a routing
    /// step.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly one receipt is required for a cleanup, provision, or write
    /// action; a routing step holds none. Each receipt's operation must be the
    /// same reference as the prepared step's operation. <see cref="IsValid"/>
    /// recomputes these checks from the held values without throwing.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryCompletedStep
    {
        private readonly CaptureRunInitializationRecoveryPreparedStep _preparedStep;
        private readonly CaptureRunInitializationRecoveryCleanupReceipt _cleanupReceipt;
        private readonly CaptureRunRootProvisionReceipt _provisionReceipt;
        private readonly CaptureRunMarkerWriteReceipt _markerWriteReceipt;

        internal CaptureRunInitializationRecoveryCompletedStep(
            CaptureRunInitializationRecoveryPreparedStep preparedStep,
            CaptureRunInitializationRecoveryCleanupReceipt cleanupReceipt,
            CaptureRunRootProvisionReceipt provisionReceipt,
            CaptureRunMarkerWriteReceipt markerWriteReceipt)
        {
            if (preparedStep == null)
            {
                throw new ArgumentNullException(nameof(preparedStep));
            }

            if (!preparedStep.IsValid)
            {
                throw new ArgumentException("Prepared step must be valid.", nameof(preparedStep));
            }

            switch (preparedStep.Action)
            {
                case CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary:
                case CaptureRunInitializationRecoveryAction.RemoveEmptyRoot:
                    if (cleanupReceipt == null)
                    {
                        throw new ArgumentException("Cleanup action requires a cleanup receipt.", nameof(cleanupReceipt));
                    }

                    if (!cleanupReceipt.IsValid)
                    {
                        throw new ArgumentException("Cleanup receipt must be valid.", nameof(cleanupReceipt));
                    }

                    if (provisionReceipt != null || markerWriteReceipt != null)
                    {
                        throw new ArgumentException("Cleanup action must not hold a provision or write receipt.", nameof(cleanupReceipt));
                    }

                    if (!ReferenceEquals(cleanupReceipt.Operation, preparedStep.CleanupOperation))
                    {
                        throw new ArgumentException("Cleanup receipt must match the prepared cleanup operation.", nameof(cleanupReceipt));
                    }

                    break;

                case CaptureRunInitializationRecoveryAction.ProvisionRoot:
                    if (provisionReceipt == null)
                    {
                        throw new ArgumentException("Provision action requires a provision receipt.", nameof(provisionReceipt));
                    }

                    if (!provisionReceipt.IsValid)
                    {
                        throw new ArgumentException("Provision receipt must be valid.", nameof(provisionReceipt));
                    }

                    if (cleanupReceipt != null || markerWriteReceipt != null)
                    {
                        throw new ArgumentException("Provision action must not hold a cleanup or write receipt.", nameof(provisionReceipt));
                    }

                    if (!ReferenceEquals(provisionReceipt.Operation, preparedStep.ProvisionOperation))
                    {
                        throw new ArgumentException("Provision receipt must match the prepared provision operation.", nameof(provisionReceipt));
                    }

                    break;

                case CaptureRunInitializationRecoveryAction.WriteMarker:
                    if (markerWriteReceipt == null)
                    {
                        throw new ArgumentException("Write action requires a write receipt.", nameof(markerWriteReceipt));
                    }

                    if (!markerWriteReceipt.IsValid)
                    {
                        throw new ArgumentException("Write receipt must be valid.", nameof(markerWriteReceipt));
                    }

                    if (cleanupReceipt != null || provisionReceipt != null)
                    {
                        throw new ArgumentException("Write action must not hold a cleanup or provision receipt.", nameof(markerWriteReceipt));
                    }

                    if (!ReferenceEquals(markerWriteReceipt.Operation, preparedStep.MarkerWriteOperation))
                    {
                        throw new ArgumentException("Write receipt must match the prepared write operation.", nameof(markerWriteReceipt));
                    }

                    break;

                default:
                    if (cleanupReceipt != null || provisionReceipt != null || markerWriteReceipt != null)
                    {
                        throw new ArgumentException("Routing step must not hold a receipt.", nameof(cleanupReceipt));
                    }

                    break;
            }

            _preparedStep = preparedStep;
            _cleanupReceipt = cleanupReceipt;
            _provisionReceipt = provisionReceipt;
            _markerWriteReceipt = markerWriteReceipt;
        }

        internal CaptureRunInitializationRecoveryPreparedStep PreparedStep => _preparedStep;

        internal CaptureRunInitializationRecoveryCleanupReceipt CleanupReceipt => _cleanupReceipt;

        internal CaptureRunRootProvisionReceipt ProvisionReceipt => _provisionReceipt;

        internal CaptureRunMarkerWriteReceipt MarkerWriteReceipt => _markerWriteReceipt;

        internal bool IsValid
        {
            get
            {
                if (_preparedStep == null || !_preparedStep.IsValid)
                {
                    return false;
                }

                switch (_preparedStep.Action)
                {
                    case CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary:
                    case CaptureRunInitializationRecoveryAction.RemoveEmptyRoot:
                        return _cleanupReceipt != null
                            && _provisionReceipt == null
                            && _markerWriteReceipt == null
                            && _cleanupReceipt.IsValid
                            && ReferenceEquals(_cleanupReceipt.Operation, _preparedStep.CleanupOperation);

                    case CaptureRunInitializationRecoveryAction.ProvisionRoot:
                        return _provisionReceipt != null
                            && _cleanupReceipt == null
                            && _markerWriteReceipt == null
                            && _provisionReceipt.IsValid
                            && ReferenceEquals(_provisionReceipt.Operation, _preparedStep.ProvisionOperation);

                    case CaptureRunInitializationRecoveryAction.WriteMarker:
                        return _markerWriteReceipt != null
                            && _cleanupReceipt == null
                            && _provisionReceipt == null
                            && _markerWriteReceipt.IsValid
                            && ReferenceEquals(_markerWriteReceipt.Operation, _preparedStep.MarkerWriteOperation);

                    default:
                        return _cleanupReceipt == null
                            && _provisionReceipt == null
                            && _markerWriteReceipt == null;
                }
            }
        }
    }
}
