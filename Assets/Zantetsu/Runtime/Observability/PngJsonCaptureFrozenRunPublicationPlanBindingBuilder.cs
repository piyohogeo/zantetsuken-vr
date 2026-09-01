using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless entry point for the frozen-Run generic-to-legacy publication
    /// plan binding. It delegates the validation and conversion to the
    /// binding's atomic factory, so no legacy plan is injected from outside.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Build"/> holds no state and performs no filesystem, codec,
    /// hashing, or logging work.
    /// </para>
    /// </remarks>
    internal static class PngJsonCaptureFrozenRunPublicationPlanBindingBuilder
    {
        internal static PngJsonCaptureFrozenRunPublicationPlanBinding Build(
            CaptureEvidenceFrozenRunPublicationResult frozenPublicationResult)
        {
            return PngJsonCaptureFrozenRunPublicationPlanBinding.Create(frozenPublicationResult);
        }
    }
}
