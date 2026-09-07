using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Phase 0.11 Run chunk finalizer. It finalizes exactly one
    /// validated <see cref="NvencRunChunkFinalizationOperation"/> and returns a
    /// <see cref="NvencRunChunkFinalizationReceipt"/> only after the fixed
    /// staging publish is known to have succeeded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before any side effect, a <c>null</c> operation must throw
    /// <see cref="ArgumentNullException"/> for <c>operation</c>, and an
    /// invalid operation must throw <see cref="ArgumentException"/> for
    /// <c>operation</c>. The finalizer must also confirm, before any side
    /// effect, that the operation's sink is backed by the finalizer's exact
    /// append writer via <see cref="NvencRunChunkSink.IsBackedBy"/>. A
    /// different writer instance must be rejected even when it targets the
    /// same location.
    /// </para>
    /// <para>
    /// The finalizer finalizes the single maintained hash without opening the
    /// file again, closes the <c>.partial</c> append handle, moves it to the
    /// fixed staging path without replacing an existing file, and never opens
    /// the closed file afterward. It must not require an explicit
    /// flush-to-storage call. It returns the receipt only after the move is
    /// known to have succeeded; on failure or an unknown move result it returns
    /// no receipt. It does not attempt the move more than once, never shortens
    /// or discards content, and never falls back to another path.
    /// </para>
    /// <para>
    /// The descriptor is built by
    /// <see cref="NvencRunChunkArtifactDescriptorFactory"/>, and the frame
    /// relation is the operation's exact reference. Publishing to the final
    /// root, and registry, plan, and capture-complete duties are out of scope.
    /// </para>
    /// </remarks>
    internal interface INvencRunChunkFinalizer
    {
        NvencRunChunkFinalizationReceipt FinalizeChunk(
            NvencRunChunkFinalizationOperation operation);
    }
}
