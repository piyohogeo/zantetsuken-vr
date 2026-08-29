using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Pure factory that assembles the per-Run path set, marker binding, and
    /// initialization plan for an existing root layout in the fixed order their
    /// contracts require. No initialization ID is issued here and no
    /// filesystem work is performed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Create"/> builds, in order, the path set from the root
    /// layout, the marker binding from the layout's TestRunId, root hashes, and
    /// the caller-supplied initialization ID, and finally the initialization
    /// plan that correlates the two. All validation of the initialization ID,
    /// marker values, marker paths, and Run correlation is delegated to the
    /// existing path set, binding factory, and plan; none of it is
    /// re-implemented here and no exception is transformed or wrapped.
    /// </para>
    /// <para>
    /// This factory owns and disposes nothing, holds no fields and no mutable
    /// static state, performs no retry, fallback, or correction, issues no
    /// replacement initialization ID, and never keeps partially built values
    /// across calls. It performs no marker decode or serialize, no hash
    /// computation, no stream, file, or directory access, no OS locking, no
    /// root creation, no atomic write, flush, or rename, and no recovery or
    /// collision classification.
    /// </para>
    /// </remarks>
    internal static class CaptureRunInitializationPlanFactory
    {
        internal static CaptureRunInitializationPlan Create(
            CaptureRunRootLayout rootLayout,
            string runInitializationId)
        {
            if (rootLayout == null)
            {
                throw new ArgumentNullException(nameof(rootLayout));
            }

            CaptureRunMarkerPathSet markerPaths = new CaptureRunMarkerPathSet(rootLayout);

            CaptureRunMarkerBinding markerBinding = CaptureRunMarkerBindingFactory.Create(
                rootLayout.TestRunId,
                runInitializationId,
                rootLayout.StagingRunRootSha256,
                rootLayout.FinalRunRootSha256);

            return new CaptureRunInitializationPlan(markerPaths, markerBinding);
        }
    }
}
