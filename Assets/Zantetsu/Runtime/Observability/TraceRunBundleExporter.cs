using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Connects the flight recorder to the bundle store: snapshots a frozen
    /// recorder, builds the run manifest and atomically publishes a trace run
    /// bundle. The recorder and its logger are never owned, mutated or disposed.
    /// </summary>
    public static class TraceRunBundleExporter
    {
        /// <summary>
        /// Saves a frozen recorder's capture as a trace run bundle and returns
        /// the manifest that was written. The recorder must already be
        /// <see cref="TraceFlightRecorderState.Frozen"/>; on failure it remains
        /// frozen and can be retried against another path.
        /// </summary>
        public static TraceRunManifest SaveFrozenAtomic(
            string bundleDirectoryPath,
            TraceFlightRecorder recorder,
            TraceRunContext context)
        {
            if (recorder == null)
            {
                throw new ArgumentNullException(nameof(recorder));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (recorder.State != TraceFlightRecorderState.Frozen)
            {
                throw new InvalidOperationException("The flight recorder must be frozen before saving.");
            }

            TraceCaptureSnapshot snapshot = recorder.CreateFrozenSnapshot();
            TraceRunManifest manifest = TraceRunManifest.Create(snapshot, context);
            TraceRunBundleStore.SaveAtomic(bundleDirectoryPath, snapshot, manifest);

            return manifest;
        }
    }
}
