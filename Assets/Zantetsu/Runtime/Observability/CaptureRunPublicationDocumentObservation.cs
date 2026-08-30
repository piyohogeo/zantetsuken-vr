using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable snapshot of one publication document's observed state, taken
    /// under the held lock. It records only observed facts; the recovery
    /// classifier, not this type, later compares documents and classifies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A canonical observation holds its decoded plan; absent, invalid, and
    /// limit-exceeded observations hold no plan and no raw bytes or exception.
    /// A canonical plan is kept as a fact even when its run ID,
    /// initialization ID, or manifest hash differ from any expectation, so the
    /// next classifier receives the unmodified observation.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> recomputes the status/probed-byte-count/plan
    /// combination from the held values without throwing. This type holds no
    /// filesystem path, byte array, handle, or stream, owns and disposes
    /// nothing, and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationDocumentObservation
    {
        private readonly CaptureRunPublicationDocumentKind _kind;
        private readonly CaptureRunPublicationDocumentObservationStatus _status;
        private readonly int _probedByteCount;
        private readonly CapturePublicationPlan _plan;

        internal CaptureRunPublicationDocumentObservation(
            CaptureRunPublicationDocumentKind kind,
            CaptureRunPublicationDocumentObservationStatus status,
            int probedByteCount,
            CapturePublicationPlan plan)
        {
            if (!IsDefinedKind(kind))
            {
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Document kind must be a defined publication document kind.");
            }

            if (!IsDefinedStatus(status))
            {
                throw new ArgumentOutOfRangeException(nameof(status), status, "Document observation status must be defined.");
            }

            RequireCombination(status, probedByteCount, plan);

            _kind = kind;
            _status = status;
            _probedByteCount = probedByteCount;
            _plan = plan;
        }

        internal CaptureRunPublicationDocumentKind Kind => _kind;

        internal CaptureRunPublicationDocumentObservationStatus Status => _status;

        internal int ProbedByteCount => _probedByteCount;

        internal CapturePublicationPlan Plan => _plan;

        internal bool IsValid
        {
            get
            {
                if (!IsDefinedKind(_kind) || !IsDefinedStatus(_status))
                {
                    return false;
                }

                switch (_status)
                {
                    case CaptureRunPublicationDocumentObservationStatus.Absent:
                        return _probedByteCount == 0 && _plan == null;

                    case CaptureRunPublicationDocumentObservationStatus.Canonical:
                        return _probedByteCount > 0 && _plan != null && _plan.IsValid;

                    case CaptureRunPublicationDocumentObservationStatus.Invalid:
                        return _probedByteCount >= 0 && _plan == null;

                    case CaptureRunPublicationDocumentObservationStatus.LimitExceeded:
                        return _probedByteCount > 0 && _plan == null;

                    default:
                        return false;
                }
            }
        }

        private static bool IsDefinedKind(CaptureRunPublicationDocumentKind kind)
        {
            return kind == CaptureRunPublicationDocumentKind.PublicationPlanTemporary
                || kind == CaptureRunPublicationDocumentKind.PublicationPlan
                || kind == CaptureRunPublicationDocumentKind.CaptureIndexTemporary
                || kind == CaptureRunPublicationDocumentKind.CaptureIndex;
        }

        private static bool IsDefinedStatus(CaptureRunPublicationDocumentObservationStatus status)
        {
            return status == CaptureRunPublicationDocumentObservationStatus.Absent
                || status == CaptureRunPublicationDocumentObservationStatus.Canonical
                || status == CaptureRunPublicationDocumentObservationStatus.Invalid
                || status == CaptureRunPublicationDocumentObservationStatus.LimitExceeded;
        }

        private static void RequireCombination(
            CaptureRunPublicationDocumentObservationStatus status,
            int probedByteCount,
            CapturePublicationPlan plan)
        {
            switch (status)
            {
                case CaptureRunPublicationDocumentObservationStatus.Absent:
                    if (probedByteCount != 0)
                    {
                        throw new ArgumentException("An absent document observation must have a zero probed byte count.", nameof(probedByteCount));
                    }

                    if (plan != null)
                    {
                        throw new ArgumentException("An absent document observation must not hold a plan.", nameof(plan));
                    }

                    return;

                case CaptureRunPublicationDocumentObservationStatus.Canonical:
                    if (probedByteCount <= 0)
                    {
                        throw new ArgumentException("A canonical document observation must have a positive probed byte count.", nameof(probedByteCount));
                    }

                    if (plan == null)
                    {
                        throw new ArgumentException("A canonical document observation must hold a plan.", nameof(plan));
                    }

                    if (!plan.IsValid)
                    {
                        throw new ArgumentException("A canonical document observation must hold a valid plan.", nameof(plan));
                    }

                    return;

                case CaptureRunPublicationDocumentObservationStatus.Invalid:
                    if (probedByteCount < 0)
                    {
                        throw new ArgumentException("An invalid document observation must have a non-negative probed byte count.", nameof(probedByteCount));
                    }

                    if (plan != null)
                    {
                        throw new ArgumentException("An invalid document observation must not hold a plan.", nameof(plan));
                    }

                    return;

                case CaptureRunPublicationDocumentObservationStatus.LimitExceeded:
                    if (probedByteCount <= 0)
                    {
                        throw new ArgumentException("A limit-exceeded document observation must have a positive probed byte count.", nameof(probedByteCount));
                    }

                    if (plan != null)
                    {
                        throw new ArgumentException("A limit-exceeded document observation must not hold a plan.", nameof(plan));
                    }

                    return;

                default:
                    throw new ArgumentOutOfRangeException(nameof(status), status, "Document observation status must be defined.");
            }
        }
    }
}
