using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Pure, stateless factory that converts one WriteMarker step of a recovery
    /// action plan into a concrete <see cref="CaptureRunMarkerWriteOperation"/>
    /// without any side effect.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Validation order is fixed. The factory holds no fields and never touches
    /// the filesystem, the atomic writer, the provisioner, or the cleanup
    /// backend. It serializes only the target marker through the existing
    /// <see cref="CaptureRunInitializationMarkerCodec"/> or
    /// <see cref="CaptureRunReadyMarkerCodec"/> and transfers ownership of the
    /// bytes only after the write operation is constructed successfully.
    /// Codec and constructor exceptions propagate unchanged.
    /// </para>
    /// </remarks>
    internal static class CaptureRunInitializationRecoveryMarkerWriteOperationFactory
    {
        internal static CaptureRunMarkerWriteOperation Create(
            CaptureRunInitializationRecoveryActionPlan actionPlan,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (markerPaths == null)
            {
                throw new ArgumentNullException(nameof(markerPaths));
            }

            if (!actionPlan.IsValid)
            {
                throw new ArgumentException("Action plan must be valid.", nameof(actionPlan));
            }

            if (!ReferenceEquals(markerPaths.RootLayout, actionPlan.RootLayout))
            {
                throw new ArgumentException("Marker path set must share the action plan's root layout.", nameof(markerPaths));
            }

            if (stepIndex < 0 || stepIndex >= actionPlan.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(stepIndex), stepIndex, "Step index out of range.");
            }

            CaptureRunInitializationRecoveryStep step = actionPlan.GetStep(stepIndex);
            if (step == null || !step.IsValid)
            {
                throw new ArgumentException("Step must be valid.", nameof(stepIndex));
            }

            if (step.Action != CaptureRunInitializationRecoveryAction.WriteMarker)
            {
                throw new ArgumentException("Step action must be WriteMarker.", nameof(stepIndex));
            }

            CaptureRunInitializationRecoveryInspectionOperation inspection = actionPlan.Decision.Snapshot.Operation;
            if (inspection == null
                || !inspection.IsValid
                || !ReferenceEquals(inspection.RootLayout, actionPlan.RootLayout))
            {
                throw new ArgumentException("Inspection operation must be valid and share the action plan's root layout.", nameof(actionPlan));
            }

            if (!markerPaths.IsValid)
            {
                throw new ArgumentException("Marker path set paths must match the fixed paths derived from the root layout.", nameof(markerPaths));
            }

            CaptureRunMarkerBinding expectedBinding = actionPlan.Decision.ExpectedBinding;
            if (expectedBinding == null)
            {
                throw new ArgumentException("Expected binding must be present for a write step.", nameof(actionPlan));
            }

            string temporaryPath;
            string finalPath;
            byte[] canonicalBytes;

            if (step.RootRole == CaptureRunRootRole.Staging)
            {
                if (step.MarkerKind == CaptureRunMarkerKind.Initialization)
                {
                    canonicalBytes = CaptureRunInitializationMarkerCodec.SerializeCanonical(expectedBinding.StagingInitialization);
                    temporaryPath = markerPaths.StagingInitializationTemporaryPath;
                    finalPath = markerPaths.StagingInitializationPath;
                }
                else
                {
                    canonicalBytes = CaptureRunReadyMarkerCodec.SerializeCanonical(expectedBinding.StagingReady);
                    temporaryPath = markerPaths.StagingReadyTemporaryPath;
                    finalPath = markerPaths.StagingReadyPath;
                }
            }
            else if (step.MarkerKind == CaptureRunMarkerKind.Initialization)
            {
                canonicalBytes = CaptureRunInitializationMarkerCodec.SerializeCanonical(expectedBinding.FinalInitialization);
                temporaryPath = markerPaths.FinalInitializationTemporaryPath;
                finalPath = markerPaths.FinalInitializationPath;
            }
            else
            {
                canonicalBytes = CaptureRunReadyMarkerCodec.SerializeCanonical(expectedBinding.FinalReady);
                temporaryPath = markerPaths.FinalReadyTemporaryPath;
                finalPath = markerPaths.FinalReadyPath;
            }

            CaptureRunMarkerWriteOperation operation = new CaptureRunMarkerWriteOperation(
                step.RootRole,
                step.MarkerKind,
                temporaryPath,
                finalPath,
                ref canonicalBytes);

            if (canonicalBytes != null)
            {
                throw new InvalidOperationException("Write operation must take ownership of the canonical bytes.");
            }

            return operation;
        }
    }
}
