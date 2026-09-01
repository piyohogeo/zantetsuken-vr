using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless entry point for the PNG-compatible Fresh artifact inspection
    /// seed. It delegates validation and construction to the seed's atomic
    /// factory without re-running the full plan validation beforehand.
    /// </summary>
    internal static class PngJsonCaptureFrozenRunArtifactInspectionSeedBuilder
    {
        internal static PngJsonCaptureFrozenRunArtifactInspectionSeed Build(
            PngJsonCaptureFrozenRunPublicationPlanBinding planBinding)
        {
            return PngJsonCaptureFrozenRunArtifactInspectionSeed.Create(planBinding);
        }
    }
}
