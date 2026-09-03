using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free factory that null-checks its inputs and delegates to
    /// the cleanup operation's construction paths, which are the single
    /// validation paths. It performs no validation, path derivation, or array
    /// copy itself and mutates, owns, or disposes nothing.
    /// </summary>
    internal static class PngJsonCapturePublicationCaptureCompleteCleanupOperationFactory
    {
        internal static PngJsonCapturePublicationCaptureCompleteCleanupOperation Create(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (publicationPaths == null)
            {
                throw new ArgumentNullException(nameof(publicationPaths));
            }

            if (markerPaths == null)
            {
                throw new ArgumentNullException(nameof(markerPaths));
            }

            return PngJsonCapturePublicationCaptureCompleteCleanupOperation.Create(actionPlan, publicationPaths, markerPaths, stepIndex);
        }

        internal static PngJsonCapturePublicationCaptureCompleteCleanupOperation CreateIndexLocal(
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan.ValidationToken token,
            PngJsonCapturePublicationCaptureCompleteCleanupActionPlan actionPlan,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunMarkerPathSet markerPaths,
            int stepIndex)
        {
            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (publicationPaths == null)
            {
                throw new ArgumentNullException(nameof(publicationPaths));
            }

            if (markerPaths == null)
            {
                throw new ArgumentNullException(nameof(markerPaths));
            }

            return PngJsonCapturePublicationCaptureCompleteCleanupOperation.CreateIndexLocal(
                token, actionPlan, publicationPaths, markerPaths, stepIndex);
        }
    }
}
