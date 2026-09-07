using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Allocation-free terminal outcome of one Run chunk terminal request. A
    /// readonly value type with two mutually exclusive shapes: Finalized (an
    /// exact <see cref="NvencChunkFinalizationResult"/>) and Abandoned (no
    /// result). A default, uninitialized outcome is neither shape.
    /// </summary>
    internal readonly struct NvencRunChunkTerminalOutcome
    {
        private readonly NvencChunkFinalizationResult _result;
        private readonly bool _finalized;

        private NvencRunChunkTerminalOutcome(NvencChunkFinalizationResult result, bool finalized)
        {
            _result = result;
            _finalized = finalized;
        }

        internal bool IsFinalized => _finalized && _result != null && _result.IsValid;

        internal bool IsAbandoned => !_finalized;

        internal NvencChunkFinalizationResult Result => _result;

        internal static NvencRunChunkTerminalOutcome Finalized(NvencChunkFinalizationResult result)
        {
            return new NvencRunChunkTerminalOutcome(result, true);
        }

        internal static NvencRunChunkTerminalOutcome Abandoned()
        {
            return new NvencRunChunkTerminalOutcome(null, false);
        }
    }
}
