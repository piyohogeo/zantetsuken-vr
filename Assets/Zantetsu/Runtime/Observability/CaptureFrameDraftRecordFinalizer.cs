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
    /// <see cref="Create"/> validates the entire draft registry and staging
    /// store before allocating a single array or constructing a single record;
    /// on any failure it builds no record and changes nothing. The registry,
    /// store, every draft, and every staging entry keep their state and
    /// ownership exactly as supplied. A repeated call over the same frozen
    /// inputs and final run produces a new result whose values and ordering are
    /// deterministically identical.
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
            if (finalRun == null)
            {
                throw new ArgumentNullException(nameof(finalRun));
            }

            if (!_stagingStore.IsCreated)
            {
                throw new ObjectDisposedException(_stagingStore.GetType().Name);
            }

            if (_draftRegistry.ReservationCount != 0)
            {
                throw new InvalidOperationException("Reservation count must be zero before finalization.");
            }

            if (_draftRegistry.PendingCount != 0)
            {
                throw new InvalidOperationException("Pending count must be zero before finalization.");
            }

            ForcedDropFrameIdSet forcedDropSet = _draftRegistry.IssuedForcedDropFrameIdSet;
            if (forcedDropSet == null
                || !ReferenceEquals(forcedDropSet.IssuedBy, _draftRegistry)
                || !forcedDropSet.IsValid
                || forcedDropSet.TestRunId != _draftRegistry.Run.TestRunId)
            {
                throw new InvalidOperationException("The canonical forced-drop frame ID set must be issued and valid.");
            }

            CaptureDraftRunContext registryRun = _draftRegistry.Run;
            ValidateFinalRun(finalRun, registryRun);

            // Validate every entry and count staged and dropped drafts while
            // cross-checking the staging store entry-by-entry. No array is
            // allocated and no record is constructed in this pass.
            int entryCount = _draftRegistry.EntryCount;
            int stagedCount = 0;
            int droppedCount = 0;
            long previousCaptureFrameId = 0;
            long totalByteCount = 0;

            for (int i = 0; i < entryCount; i++)
            {
                CaptureFrameDraft draft = _draftRegistry.GetEntryDraft(i);
                CaptureFrameDraftStatus status = _draftRegistry.GetEntryStatus(i);
                CaptureFrameDropReason dropReason = _draftRegistry.GetEntryDropReason(i);
                DraftDropTraceEmissionState emissionState = _draftRegistry.GetEntryEmissionState(i);

                long captureFrameId = draft.CaptureFrameId;
                if (captureFrameId <= 0)
                {
                    throw new InvalidOperationException("Entry capture frame ID must be positive.");
                }

                if (i > 0 && captureFrameId <= previousCaptureFrameId)
                {
                    throw new InvalidOperationException("Capture frame IDs must be strictly ascending without duplicates.");
                }

                previousCaptureFrameId = captureFrameId;

                if (!ReferenceEquals(draft.Run, registryRun))
                {
                    throw new InvalidOperationException("Draft run must be the registry run instance.");
                }

                // Reuse the registry's canonical request-match lookup: a
                // registered draft must be retrievable by its own request with a
                // full request identity match, and must be the same instance the
                // append-order query returned.
                if (!_draftRegistry.TryGet(draft.Request, out CaptureFrameDraft canonicalDraft, out CaptureFrameDraftStatus canonicalStatus))
                {
                    throw new InvalidOperationException("The draft is not registered in the registry.");
                }

                if (!ReferenceEquals(canonicalDraft, draft))
                {
                    throw new InvalidOperationException("The append-order query and the canonical lookup disagree.");
                }

                if (canonicalStatus != status)
                {
                    throw new InvalidOperationException("The canonical status does not match the entry status.");
                }

                switch (status)
                {
                    case CaptureFrameDraftStatus.Staged:
                        ValidateStagedEntry(captureFrameId, dropReason, emissionState, registryRun.TestRunId, ref totalByteCount);
                        stagedCount++;
                        break;

                    case CaptureFrameDraftStatus.Dropped:
                        ValidateDroppedEntry(captureFrameId, dropReason, emissionState);
                        droppedCount++;
                        break;

                    default:
                        throw new InvalidOperationException("Entry has an undefined status.");
                }
            }

            // The store must hold exactly the staged entries: every staged draft
            // found its unique entry above, and the store forbids duplicate
            // capture frame IDs, so an entry-count equality proves there are no
            // extra or missing IDs.
            if (_stagingStore.Count != stagedCount)
            {
                throw new InvalidOperationException("Staging store count must equal the staged draft count.");
            }

            if (totalByteCount != _stagingStore.TotalByteCount)
            {
                throw new InvalidOperationException("Staging store total byte count does not match the staged entries.");
            }

            // The finalization is the sole allocator and builder of its own
            // record and entry arrays; this finalizer allocates nothing and
            // only delegates, so no record is built before every validation
            // above succeeds and no external alias to those arrays can exist.
            return new CaptureFrameDraftRecordFinalization(finalRun, _draftRegistry, _stagingStore, droppedCount);
        }

        private static void ValidateFinalRun(CaptureRunReference finalRun, CaptureDraftRunContext registryRun)
        {
            if (finalRun.TestRunId != registryRun.TestRunId
                || finalRun.TestCaseId != registryRun.TestCaseId
                || finalRun.RandomSeed != registryRun.RandomSeed
                || finalRun.CaptureProfileId != registryRun.CaptureProfileId
                || !string.Equals(finalRun.BuildId, registryRun.BuildId, StringComparison.Ordinal)
                || !string.Equals(finalRun.SceneId, registryRun.SceneId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Final run must match the registry run.", nameof(finalRun));
            }
        }

        private void ValidateStagedEntry(
            long captureFrameId,
            CaptureFrameDropReason dropReason,
            DraftDropTraceEmissionState emissionState,
            long registryTestRunId,
            ref long totalByteCount)
        {
            if (dropReason != CaptureFrameDropReason.None)
            {
                throw new InvalidOperationException("Staged entry must have drop reason None.");
            }

            if (emissionState != DraftDropTraceEmissionState.None)
            {
                throw new InvalidOperationException("Staged entry must have emission state None.");
            }

            if (!_stagingStore.TryGet(captureFrameId, out CaptureFramePngStagingEntry stagingEntry))
            {
                throw new InvalidOperationException("A staged draft has no staging entry.");
            }

            if (stagingEntry == null || !stagingEntry.IsCreated)
            {
                throw new InvalidOperationException("A staged draft's staging entry is missing or disposed.");
            }

            if (stagingEntry.TestRunId != registryTestRunId || stagingEntry.CaptureFrameId != captureFrameId)
            {
                throw new InvalidOperationException("A staged draft's staging entry IDs do not match.");
            }

            totalByteCount = checked(totalByteCount + stagingEntry.ByteCount);
        }

        private void ValidateDroppedEntry(
            long captureFrameId,
            CaptureFrameDropReason dropReason,
            DraftDropTraceEmissionState emissionState)
        {
            if (dropReason != CaptureFrameDropReason.PngEncodeFailed
                && dropReason != CaptureFrameDropReason.PngStagingStoreFull
                && dropReason != CaptureFrameDropReason.CaptureCancelled
                && dropReason != CaptureFrameDropReason.FreezeDrainTimeout)
            {
                throw new InvalidOperationException("Dropped entry must have a normal or freeze drop reason.");
            }

            if (dropReason == CaptureFrameDropReason.FreezeDrainTimeout)
            {
                if (emissionState != DraftDropTraceEmissionState.None)
                {
                    throw new InvalidOperationException("Freeze-drain dropped entry must have emission state None.");
                }
            }
            else if (emissionState != DraftDropTraceEmissionState.Attempted)
            {
                throw new InvalidOperationException("Normal dropped entry must have emission state Attempted.");
            }

            if (_stagingStore.TryGet(captureFrameId, out _))
            {
                throw new InvalidOperationException("A dropped draft must not have a staging entry.");
            }
        }
    }
}
