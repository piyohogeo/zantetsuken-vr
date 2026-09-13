using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Pure factory that produces the complete Capture Run initialization
    /// document set for an existing root layout and a caller-issued
    /// initialization ID.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Create"/> assembles, in order, the marker path set, the
    /// marker binding, the initialization plan that correlates the two, and
    /// the document set over that plan. Only a null root layout is rejected
    /// here; every other validation, including the initialization ID form,
    /// root hashes, Run correlation, ready agreement, and canonical
    /// serialization limits, is delegated to those existing types. No
    /// exception is transformed, wrapped, or aggregated, and no retry,
    /// fallback, ID correction, or replacement ID is performed.
    /// </para>
    /// <para>
    /// This factory owns and disposes nothing, holds no fields and no mutable
    /// static state. It issues no initialization ID, computes no hash,
    /// serializes and decodes nothing, allocates no raw array, performs no
    /// path manipulation, no file, directory, or stream access, no OS
    /// locking, no root creation, no atomic write, flush, or rename, and no
    /// recovery or collision classification.
    /// </para>
    /// </remarks>
    internal static class CaptureRunInitializationDocumentSetFactory
    {
        internal static CaptureRunInitializationDocumentSet Create(
            CaptureRunRootLayout rootLayout,
            string runInitializationId)
        {
            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            CaptureRunMarkerPathSet markerPaths =
                new CaptureRunMarkerPathSet(rootLayout);

            CaptureRunMarkerBinding markerBinding =
                CaptureRunMarkerBindingFactory.Create(
                    rootLayout.TestRunId,
                    runInitializationId,
                    rootLayout.StagingRunRootSha256,
                    rootLayout.FinalRunRootSha256);

            CaptureRunInitializationPlan plan =
                new CaptureRunInitializationPlan(markerPaths, markerBinding);

            return new CaptureRunInitializationDocumentSet(plan);
        }
    }
}
