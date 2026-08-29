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

            SelectMarkerAndPaths(step, markerPaths, expectedBinding, out string temporaryPath, out string finalPath, out byte[] canonicalBytes);

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

        internal static bool IsOperationFor(
            CaptureRunInitializationRecoveryActionPlan actionPlan,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex,
            CaptureRunMarkerWriteOperation operation)
        {
            if (actionPlan == null || !actionPlan.IsValid || markerPaths == null || operation == null)
            {
                return false;
            }

            if (!ReferenceEquals(markerPaths.RootLayout, actionPlan.RootLayout) || !markerPaths.IsValid)
            {
                return false;
            }

            if (stepIndex < 0 || stepIndex >= actionPlan.Count)
            {
                return false;
            }

            CaptureRunInitializationRecoveryStep step = actionPlan.GetStep(stepIndex);
            if (step == null || !step.IsValid || step.Action != CaptureRunInitializationRecoveryAction.WriteMarker)
            {
                return false;
            }

            if (operation.RootRole != step.RootRole || operation.MarkerKind != step.MarkerKind)
            {
                return false;
            }

            CaptureRunMarkerBinding expectedBinding = actionPlan.Decision.ExpectedBinding;
            if (expectedBinding == null)
            {
                return false;
            }

            SelectMarkerAndPaths(step, markerPaths, expectedBinding, out string temporaryPath, out string finalPath, out byte[] expectedBytes);

            if (!string.Equals(operation.TemporaryPath, temporaryPath, StringComparison.Ordinal)
                || !string.Equals(operation.FinalPath, finalPath, StringComparison.Ordinal))
            {
                return false;
            }

            if (!operation.IsValid)
            {
                return false;
            }

            return ByteArraysEqual(operation.GetCanonicalBytes(), expectedBytes);
        }

        private static void SelectMarkerAndPaths(
            CaptureRunInitializationRecoveryStep step,
            CaptureRunMarkerPathSet markerPaths,
            CaptureRunMarkerBinding binding,
            out string temporaryPath,
            out string finalPath,
            out byte[] canonicalBytes)
        {
            if (step.RootRole == CaptureRunRootRole.Staging)
            {
                if (step.MarkerKind == CaptureRunMarkerKind.Initialization)
                {
                    canonicalBytes = CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.StagingInitialization);
                    temporaryPath = markerPaths.StagingInitializationTemporaryPath;
                    finalPath = markerPaths.StagingInitializationPath;
                }
                else
                {
                    canonicalBytes = CaptureRunReadyMarkerCodec.SerializeCanonical(binding.StagingReady);
                    temporaryPath = markerPaths.StagingReadyTemporaryPath;
                    finalPath = markerPaths.StagingReadyPath;
                }
            }
            else if (step.MarkerKind == CaptureRunMarkerKind.Initialization)
            {
                canonicalBytes = CaptureRunInitializationMarkerCodec.SerializeCanonical(binding.FinalInitialization);
                temporaryPath = markerPaths.FinalInitializationTemporaryPath;
                finalPath = markerPaths.FinalInitializationPath;
            }
            else
            {
                canonicalBytes = CaptureRunReadyMarkerCodec.SerializeCanonical(binding.FinalReady);
                temporaryPath = markerPaths.FinalReadyTemporaryPath;
                finalPath = markerPaths.FinalReadyPath;
            }
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
