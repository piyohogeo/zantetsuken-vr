using System;
using System.Text;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable snapshot of a completed publication recovery inspection:
    /// which inspector produced it, which operation it observed, the four
    /// per-document observations, the two frames observations, and the
    /// per-root entry flags.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The snapshot owns and disposes nothing — neither the outcome, the
    /// lease, the roots, nor the documents. It performs no document
    /// comparison, no test-run-ID, initialization-ID, or manifest-hash
    /// classification, and no collision classification; it records only
    /// observed facts. <see cref="IsValid"/> recomputes every check from the
    /// held values without throwing.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationRecoveryInspectionSnapshot
    {
        private readonly ICaptureRunPublicationRecoveryInspector _issuedBy;
        private readonly CaptureRunPublicationRecoveryInspectionOperation _operation;
        private readonly CaptureRunPublicationDocumentObservation _publicationPlanTemporary;
        private readonly CaptureRunPublicationDocumentObservation _publicationPlan;
        private readonly CaptureRunPublicationDocumentObservation _captureIndexTemporary;
        private readonly CaptureRunPublicationDocumentObservation _captureIndex;
        private readonly CaptureRunPublicationFramesObservationStatus _stagingFramesStatus;
        private readonly CaptureRunPublicationFramesObservationStatus _finalFramesStatus;
        private readonly bool _stagingHasUnexpectedEntries;
        private readonly bool _finalHasUnexpectedEntries;
        private readonly bool _stagingRootEntryLimitExceeded;
        private readonly bool _finalRootEntryLimitExceeded;

        internal CaptureRunPublicationRecoveryInspectionSnapshot(
            ICaptureRunPublicationRecoveryInspector issuedBy,
            CaptureRunPublicationRecoveryInspectionOperation operation,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary,
            CaptureRunPublicationDocumentObservation publicationPlan,
            CaptureRunPublicationDocumentObservation captureIndexTemporary,
            CaptureRunPublicationDocumentObservation captureIndex,
            CaptureRunPublicationFramesObservationStatus stagingFramesStatus,
            CaptureRunPublicationFramesObservationStatus finalFramesStatus,
            bool stagingHasUnexpectedEntries,
            bool finalHasUnexpectedEntries,
            bool stagingRootEntryLimitExceeded,
            bool finalRootEntryLimitExceeded)
        {
            if (issuedBy == null)
            {
                throw new ArgumentNullException(nameof(issuedBy));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (publicationPlanTemporary == null)
            {
                throw new ArgumentNullException(nameof(publicationPlanTemporary));
            }

            if (publicationPlan == null)
            {
                throw new ArgumentNullException(nameof(publicationPlan));
            }

            if (captureIndexTemporary == null)
            {
                throw new ArgumentNullException(nameof(captureIndexTemporary));
            }

            if (captureIndex == null)
            {
                throw new ArgumentNullException(nameof(captureIndex));
            }

            if (!operation.IsValid)
            {
                throw new ArgumentException("Publication inspection operation must be valid.", nameof(operation));
            }

            if (!IsDefinedFramesStatus(stagingFramesStatus))
            {
                throw new ArgumentOutOfRangeException(nameof(stagingFramesStatus), stagingFramesStatus, "Staging frames status must be defined.");
            }

            if (!IsDefinedFramesStatus(finalFramesStatus))
            {
                throw new ArgumentOutOfRangeException(nameof(finalFramesStatus), finalFramesStatus, "Final frames status must be defined.");
            }

            RequireDocument(publicationPlanTemporary, CaptureRunPublicationDocumentKind.PublicationPlanTemporary, nameof(publicationPlanTemporary), operation);
            RequireDocument(publicationPlan, CaptureRunPublicationDocumentKind.PublicationPlan, nameof(publicationPlan), operation);
            RequireDocument(captureIndexTemporary, CaptureRunPublicationDocumentKind.CaptureIndexTemporary, nameof(captureIndexTemporary), operation);
            RequireDocument(captureIndex, CaptureRunPublicationDocumentKind.CaptureIndex, nameof(captureIndex), operation);

            _issuedBy = issuedBy;
            _operation = operation;
            _publicationPlanTemporary = publicationPlanTemporary;
            _publicationPlan = publicationPlan;
            _captureIndexTemporary = captureIndexTemporary;
            _captureIndex = captureIndex;
            _stagingFramesStatus = stagingFramesStatus;
            _finalFramesStatus = finalFramesStatus;
            _stagingHasUnexpectedEntries = stagingHasUnexpectedEntries;
            _finalHasUnexpectedEntries = finalHasUnexpectedEntries;
            _stagingRootEntryLimitExceeded = stagingRootEntryLimitExceeded;
            _finalRootEntryLimitExceeded = finalRootEntryLimitExceeded;
        }

        internal ICaptureRunPublicationRecoveryInspector IssuedBy => _issuedBy;

        internal CaptureRunPublicationRecoveryInspectionOperation Operation => _operation;

        internal CaptureRunPublicationDocumentObservation PublicationPlanTemporary => _publicationPlanTemporary;

        internal CaptureRunPublicationDocumentObservation PublicationPlan => _publicationPlan;

        internal CaptureRunPublicationDocumentObservation CaptureIndexTemporary => _captureIndexTemporary;

        internal CaptureRunPublicationDocumentObservation CaptureIndex => _captureIndex;

        internal CaptureRunPublicationFramesObservationStatus StagingFramesStatus => _stagingFramesStatus;

        internal CaptureRunPublicationFramesObservationStatus FinalFramesStatus => _finalFramesStatus;

        internal bool StagingHasUnexpectedEntries => _stagingHasUnexpectedEntries;

        internal bool FinalHasUnexpectedEntries => _finalHasUnexpectedEntries;

        internal bool StagingRootEntryLimitExceeded => _stagingRootEntryLimitExceeded;

        internal bool FinalRootEntryLimitExceeded => _finalRootEntryLimitExceeded;

        internal bool IsValid
        {
            get
            {
                if (_issuedBy == null || _operation == null || !_operation.IsValid)
                {
                    return false;
                }

                if (_publicationPlanTemporary == null || _publicationPlan == null
                    || _captureIndexTemporary == null || _captureIndex == null)
                {
                    return false;
                }

                if (!_publicationPlanTemporary.IsValid || !_publicationPlan.IsValid
                    || !_captureIndexTemporary.IsValid || !_captureIndex.IsValid)
                {
                    return false;
                }

                if (_publicationPlanTemporary.Kind != CaptureRunPublicationDocumentKind.PublicationPlanTemporary
                    || _publicationPlan.Kind != CaptureRunPublicationDocumentKind.PublicationPlan
                    || _captureIndexTemporary.Kind != CaptureRunPublicationDocumentKind.CaptureIndexTemporary
                    || _captureIndex.Kind != CaptureRunPublicationDocumentKind.CaptureIndex)
                {
                    return false;
                }

                if (!IsDefinedFramesStatus(_stagingFramesStatus) || !IsDefinedFramesStatus(_finalFramesStatus))
                {
                    return false;
                }

                return SatisfiesLimits(_publicationPlanTemporary, _operation)
                    && SatisfiesLimits(_publicationPlan, _operation)
                    && SatisfiesLimits(_captureIndexTemporary, _operation)
                    && SatisfiesLimits(_captureIndex, _operation);
            }
        }

        private static bool IsDefinedFramesStatus(CaptureRunPublicationFramesObservationStatus status)
        {
            return status == CaptureRunPublicationFramesObservationStatus.Absent
                || status == CaptureRunPublicationFramesObservationStatus.Directory
                || status == CaptureRunPublicationFramesObservationStatus.Invalid;
        }

        private static bool SatisfiesLimits(
            CaptureRunPublicationDocumentObservation observation,
            CaptureRunPublicationRecoveryInspectionOperation operation)
        {
            int maximumPlanBytes = operation.MaximumPlanBytes;

            switch (observation.Status)
            {
                case CaptureRunPublicationDocumentObservationStatus.Absent:
                    return true;

                case CaptureRunPublicationDocumentObservationStatus.Canonical:
                    return observation.ProbedByteCount <= maximumPlanBytes
                        && PlanWithinLimits(observation.Plan, operation.MaximumEntryCount, operation.MaximumPathBytes);

                case CaptureRunPublicationDocumentObservationStatus.Invalid:
                    return observation.ProbedByteCount <= maximumPlanBytes;

                case CaptureRunPublicationDocumentObservationStatus.LimitExceeded:
                    return observation.ProbedByteCount == maximumPlanBytes + 1;

                default:
                    return false;
            }
        }

        private static bool PlanWithinLimits(PngJsonCapturePublicationPlan plan, int maximumEntryCount, int maximumPathBytes)
        {
            if (plan == null || !plan.IsValid || plan.EntryCount > maximumEntryCount)
            {
                return false;
            }

            for (int i = 0; i < plan.EntryCount; i++)
            {
                PngJsonCapturePublicationPlanEntry entry = plan.GetEntry(i);
                if (entry == null
                    || Utf8ByteCount(entry.PngStagingRelativePath) > maximumPathBytes
                    || Utf8ByteCount(entry.SidecarStagingRelativePath) > maximumPathBytes
                    || Utf8ByteCount(entry.PngFinalRelativePath) > maximumPathBytes
                    || Utf8ByteCount(entry.SidecarFinalRelativePath) > maximumPathBytes)
                {
                    return false;
                }
            }

            return true;
        }

        private static int Utf8ByteCount(string value)
        {
            return value == null ? int.MaxValue : Encoding.UTF8.GetByteCount(value);
        }

        private static void RequireDocument(
            CaptureRunPublicationDocumentObservation observation,
            CaptureRunPublicationDocumentKind expectedKind,
            string paramName,
            CaptureRunPublicationRecoveryInspectionOperation operation)
        {
            if (!observation.IsValid)
            {
                throw new ArgumentException("Document observation must be internally consistent.", paramName);
            }

            if (observation.Kind != expectedKind)
            {
                throw new ArgumentException("Document observation kind must match its fixed position.", paramName);
            }

            if (!SatisfiesLimits(observation, operation))
            {
                throw new ArgumentException("Document observation must satisfy the operation limits.", paramName);
            }
        }
    }
}
