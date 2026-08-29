using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Main-thread converter that promotes every staged draft into a final
    /// <see cref="CaptureFrameRecord"/> after the final
    /// <see cref="CaptureRunReference"/> has been determined, in capture frame
    /// ID ascending order, with no partial publication.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Create"/> delegates to the finalization, which re-validates
    /// the entire draft registry and staging store before allocating a single
    /// array or constructing a single record; on any failure it builds no
    /// record and changes nothing. The registry, store, every draft, and every
    /// staging entry keep their state and ownership exactly as supplied. A
    /// repeated call over the same frozen inputs and final run produces a new
    /// result whose values and ordering are deterministically identical.
    /// </para>
    /// <para>
    /// This type holds only the draft registry and the staging store, transfers
    /// no PNG byte ownership, disposes and rolls back nothing, registers no
    /// record, performs no trace, logging, file I/O, or Unity static API access,
    /// and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFrameDraftRecordFinalizer
    {
        private readonly CaptureFrameDraftRegistry _draftRegistry;
        private readonly CaptureFramePngStagingStore _stagingStore;

        internal CaptureFrameDraftRecordFinalizer(
            CaptureFrameDraftRegistry draftRegistry,
            CaptureFramePngStagingStore stagingStore)
        {
            if (draftRegistry == null)
            {
                throw new ArgumentNullException(nameof(draftRegistry));
            }

            if (stagingStore == null)
            {
                throw new ArgumentNullException(nameof(stagingStore));
            }

            if (!ReferenceEquals(stagingStore.Run, draftRegistry.Run))
            {
                throw new ArgumentException("Staging store run must match the draft registry run.", nameof(stagingStore));
            }

            _draftRegistry = draftRegistry;
            _stagingStore = stagingStore;
        }

        internal CaptureFrameDraftRecordFinalization Create(CaptureRunReference finalRun)
        {
            // The finalization re-validates every freeze and integrity
            // precondition before allocating a single array or constructing a
            // single record, so no pre-freeze or inconsistent input can bypass
            // this finalizer. This finalizer allocates and builds nothing.
            return new CaptureFrameDraftRecordFinalization(finalRun, _draftRegistry, _stagingStore);
        }
    }
}
