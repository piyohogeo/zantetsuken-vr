namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, allocation-free handoff payload that carries one completed
    /// source surface release request and its exact completion evidence from
    /// the Submit Worker to the Main Thread. It transfers no ownership, holds
    /// no native resource, and is not disposable.
    /// </summary>
    internal readonly struct NvencSourceSurfaceReleaseHandoff
    {
        private readonly NvencSubmissionRecord _record;
        private readonly NvencSourceReadCompletedEvidence _evidence;

        internal NvencSourceSurfaceReleaseHandoff(
            NvencSubmissionRecord record,
            NvencSourceReadCompletedEvidence evidence)
        {
            _record = record;
            _evidence = evidence;
        }

        internal NvencSubmissionRecord Record => _record;

        internal NvencSourceReadCompletedEvidence Evidence => _evidence;
    }
}
