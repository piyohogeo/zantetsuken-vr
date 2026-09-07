using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Allocation-free terminal outcome of one Run chunk terminal request. A
    /// readonly value type with three mutually exclusive shapes: Finalized (an
    /// exact <see cref="NvencChunkFinalizationResult"/>), Abandoned (no
    /// result), and None (the default, uninitialized). A default outcome is
    /// None, never Abandoned or Finalized.
    /// </summary>
    internal readonly struct NvencRunChunkTerminalOutcome
    {
        private readonly NvencChunkFinalizationResult _result;
        private readonly bool _finalized;
        private readonly bool _initialized;

        private NvencRunChunkTerminalOutcome(NvencChunkFinalizationResult result, bool finalized, bool initialized)
        {
            _result = result;
            _finalized = finalized;
            _initialized = initialized;
        }

        internal bool IsFinalized => _initialized && _finalized && _result != null && _result.IsValid;

        internal bool IsAbandoned => _initialized && !_finalized;

        internal bool IsNone => !_initialized;

        internal NvencChunkFinalizationResult Result => _result;

        internal static NvencRunChunkTerminalOutcome Finalized(NvencChunkFinalizationResult result)
        {
            return new NvencRunChunkTerminalOutcome(result, true, true);
        }

        internal static NvencRunChunkTerminalOutcome Abandoned()
        {
            return new NvencRunChunkTerminalOutcome(null, false, true);
        }
    }
}
