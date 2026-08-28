using System;
using System.Runtime.ExceptionServices;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Main-thread consumer of the terminal intent queue. It drains at most one
    /// intent per <see cref="ProcessNext"/> call and definitively moves the
    /// matching draft to <see cref="CaptureFrameDraftStatus.Staged"/> or, for a
    /// normal drop, <see cref="CaptureFrameDraftStatus.Dropped"/>, recording the
    /// one-time drop trace for dropped drafts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A successful dequeue transfers logical ownership of the intent and, for
    /// a stage intent, its staging entry to this coordinator. The coordinator
    /// disposes only the private entry of a stage intent that it still owns:
    /// a loser intent, an intent whose draft has an undefined status, an intent
    /// that fails validation before staging, a stage intent that cannot be
    /// registered due to store capacity, or a stage intent whose registration
    /// throws before the store takes ownership. Once
    /// <see cref="CaptureFrameDraftRegistry.TryMarkStaged"/> returns <c>true</c>
    /// the store owns the entry and this coordinator never disposes or rolls it
    /// back, even when a later step throws.
    /// </para>
    /// <para>
    /// This type owns, disposes, and clears none of its four dependencies and
    /// holds no request, draft, intent, entry, lease, readback, PNG queue,
    /// file, or manifest field. It is main-thread only, not thread-safe, and is
    /// not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFrameDraftTerminalCoordinator
    {
        private readonly CaptureFrameDraftTerminalIntentQueue _intentQueue;
        private readonly CaptureFrameDraftRegistry _draftRegistry;
        private readonly CaptureFramePngStagingStore _stagingStore;
        private readonly CaptureFrameTraceObserver _traceObserver;

        internal CaptureFrameDraftTerminalCoordinator(
            CaptureFrameDraftTerminalIntentQueue intentQueue,
            CaptureFrameDraftRegistry draftRegistry,
            CaptureFramePngStagingStore stagingStore,
            CaptureFrameTraceObserver traceObserver)
        {
            if (intentQueue == null)
            {
                throw new ArgumentNullException(nameof(intentQueue));
            }

            if (draftRegistry == null)
            {
                throw new ArgumentNullException(nameof(draftRegistry));
            }

            if (stagingStore == null)
            {
                throw new ArgumentNullException(nameof(stagingStore));
            }

            if (traceObserver == null)
            {
                throw new ArgumentNullException(nameof(traceObserver));
            }

            if (!ReferenceEquals(intentQueue.Registry, draftRegistry))
            {
                throw new ArgumentException("Intent queue registry must match the draft registry.", nameof(draftRegistry));
            }

            if (!ReferenceEquals(stagingStore.Run, draftRegistry.Run))
            {
                throw new ArgumentException("Staging store run must match the draft registry run.", nameof(stagingStore));
            }

            _intentQueue = intentQueue;
            _draftRegistry = draftRegistry;
            _stagingStore = stagingStore;
            _traceObserver = traceObserver;
        }

        /// <summary>
        /// Processes at most one dequeued intent. Returns <see cref="CaptureFrameDraftTerminalProcessingStatus.None"/>
        /// without touching any other dependency when the queue is empty.
        /// </summary>
        internal CaptureFrameDraftTerminalProcessingStatus ProcessNext()
        {
            if (!_intentQueue.TryDequeue(out CaptureFrameDraftTerminalIntent intent))
            {
                return CaptureFrameDraftTerminalProcessingStatus.None;
            }

            // The coordinator now owns the intent and, for a stage intent, its entry.
            CaptureFrameRequest request = intent.Request;

            // Registry lookup with the intent request as the source of truth.
            CaptureFrameDraft draft;
            CaptureFrameDraftStatus status;
            try
            {
                if (!_draftRegistry.TryGet(request, out draft, out status))
                {
                    throw new InvalidOperationException("The draft is not registered in the registry.");
                }

                if (!draft.HasIdenticalRequest(request))
                {
                    throw new InvalidOperationException("The registered draft request does not match the intent request.");
                }
            }
            catch (Exception ex)
            {
                ExceptionDispatchInfo.Capture(BuildRethrowAfterEntryCleanup(intent, ex)).Throw();
                return default; // unreachable
            }

            // Already terminal: this intent is a loser.
            if (status == CaptureFrameDraftStatus.Staged || status == CaptureFrameDraftStatus.Dropped)
            {
                DisposeOwnedStageEntry(intent);
                return CaptureFrameDraftTerminalProcessingStatus.DiscardedAlreadyTerminal;
            }

            // Any status other than Pending here is undefined.
            if (status != CaptureFrameDraftStatus.Pending)
            {
                ExceptionDispatchInfo.Capture(BuildRethrowAfterEntryCleanup(
                    intent,
                    new InvalidOperationException("The draft status is not Pending, Staged, or Dropped."))).Throw();
                return default; // unreachable
            }

            if (!intent.IsStage)
            {
                return CompleteDrop(intent, request, intent.DropReason);
            }

            return CompleteStage(intent, request);
        }

        private CaptureFrameDraftTerminalProcessingStatus CompleteStage(
            CaptureFrameDraftTerminalIntent intent,
            in CaptureFrameRequest request)
        {
            bool staged;
            try
            {
                staged = _draftRegistry.TryMarkStaged(request, _stagingStore, intent.StagingEntry);
            }
            catch (Exception ex)
            {
                // TryMarkStaged threw before the store took ownership: the entry
                // is still coordinator-owned and must be recovered before the
                // original exception propagates.
                ExceptionDispatchInfo.Capture(BuildRethrowAfterEntryCleanup(intent, ex)).Throw();
                return default; // unreachable
            }

            if (staged)
            {
                // The store now owns the entry from this linearization point on;
                // never dispose or roll it back, even if MarkDraftTerminal throws.
                _intentQueue.MarkDraftTerminal(request);
                return CaptureFrameDraftTerminalProcessingStatus.Staged;
            }

            // Store capacity shortage: the entry stays coordinator-owned. Dispose
            // it first; only after a successful disposal does the draft drop.
            DisposeOwnedStageEntry(intent);

            return CompleteDrop(intent, request, CaptureFrameDropReason.PngStagingStoreFull);
        }

        private CaptureFrameDraftTerminalProcessingStatus CompleteDrop(
            CaptureFrameDraftTerminalIntent intent,
            in CaptureFrameRequest request,
            CaptureFrameDropReason reason)
        {
            _draftRegistry.MarkDropped(request, reason);
            _intentQueue.MarkDraftTerminal(request);

            if (!_traceObserver.RecordDraftDropped(_draftRegistry, request.TraceContext.CaptureFrameId))
            {
                throw new InvalidOperationException("The drop trace was not consumable after the draft was dropped.");
            }

            return CaptureFrameDraftTerminalProcessingStatus.Dropped;
        }

        /// <summary>
        /// Disposes the intent's own stage entry, if any. Throws the entry's
        /// disposal exception if it fails; the caller must not retry it.
        /// </summary>
        private static void DisposeOwnedStageEntry(CaptureFrameDraftTerminalIntent intent)
        {
            if (intent.StagingEntry != null)
            {
                intent.StagingEntry.Dispose();
            }
        }

        /// <summary>
        /// Builds the exception to propagate after recovering a coordinator-owned
        /// stage entry. A successful recovery returns the original exception (to
        /// be rethrown with its original stack via
        /// <see cref="ExceptionDispatchInfo"/>); a failed recovery wraps the
        /// original exception first in an <see cref="AggregateException"/>.
        /// </summary>
        private static Exception BuildRethrowAfterEntryCleanup(
            CaptureFrameDraftTerminalIntent intent,
            Exception original)
        {
            if (intent.StagingEntry == null)
            {
                return original;
            }

            try
            {
                intent.StagingEntry.Dispose();
            }
            catch (Exception cleanupEx)
            {
                return new AggregateException(original, cleanupEx);
            }

            return original;
        }
    }
}
