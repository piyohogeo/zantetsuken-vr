using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of converting every staged draft into a final
    /// <see cref="CaptureFrameRecord"/> after freeze, in capture frame ID
    /// ascending order. Dropped drafts are excluded and only counted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type is the sole allocator and builder of its own record and
    /// staging entry reference arrays: it allocates each array exactly once
    /// inside the constructor and never receives or returns them, so no
    /// external alias to either array can exist. It re-validates every freeze
    /// and integrity precondition before allocating or constructing anything,
    /// so a pre-freeze or inconsistent registry or store cannot produce a
    /// result. The record at an index and the staging entry at the same index
    /// always share the same <c>CaptureFrameId</c>. Only <see cref="GetRecord"/>
    /// and <see cref="GetStagingEntry"/> expose the arrays, with out-of-range
    /// indices rejected by <see cref="ArgumentOutOfRangeException"/>.
    /// </para>
    /// <para>
    /// This type owns, disposes, and registers nothing: the run reference, the
    /// records, and the staging entries are caller-owned and must outlive this
    /// result. It is immutable, main-thread only, and not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureFrameDraftRecordFinalization
    {
        private readonly CaptureRunReference _run;
        private readonly CaptureFrameRecord[] _records;
        private readonly CaptureFramePngStagingEntry[] _stagingEntries;
        private readonly int _droppedCount;

        internal CaptureFrameDraftRecordFinalization(
            CaptureRunReference finalRun,
            CaptureFrameDraftRegistry draftRegistry,
            CaptureFramePngStagingStore stagingStore)
        {
            if (finalRun == null)
            {
                throw new ArgumentNullException(nameof(finalRun));
            }

            if (draftRegistry == null)
            {
                throw new ArgumentNullException(nameof(draftRegistry));
            }

            if (stagingStore == null)
            {
                throw new ArgumentNullException(nameof(stagingStore));
            }

            if (!stagingStore.IsCreated)
            {
                throw new ObjectDisposedException(stagingStore.GetType().Name);
            }

            if (!ReferenceEquals(stagingStore.Run, draftRegistry.Run))
            {
                throw new ArgumentException("Staging store run must match the draft registry run.", nameof(stagingStore));
            }

            if (draftRegistry.ReservationCount != 0)
            {
                throw new InvalidOperationException("Reservation count must be zero before finalization.");
            }

            if (draftRegistry.PendingCount != 0)
            {
                throw new InvalidOperationException("Pending count must be zero before finalization.");
            }

            ForcedDropFrameIdSet forcedDropSet = draftRegistry.IssuedForcedDropFrameIdSet;
            if (forcedDropSet == null
                || !ReferenceEquals(forcedDropSet.IssuedBy, draftRegistry)
                || !forcedDropSet.IsValid
                || forcedDropSet.TestRunId != draftRegistry.Run.TestRunId)
            {
                throw new InvalidOperationException("The canonical forced-drop frame ID set must be issued and valid.");
            }

            CaptureDraftRunContext registryRun = draftRegistry.Run;
            if (finalRun.TestRunId != registryRun.TestRunId
                || finalRun.TestCaseId != registryRun.TestCaseId
                || finalRun.RandomSeed != registryRun.RandomSeed
                || finalRun.CaptureProfileId != registryRun.CaptureProfileId
                || !string.Equals(finalRun.BuildId, registryRun.BuildId, StringComparison.Ordinal)
                || !string.Equals(finalRun.SceneId, registryRun.SceneId, StringComparison.Ordinal))
            {
                throw new ArgumentException("Final run must match the registry run.", nameof(finalRun));
            }

            // Validate every entry and count staged and dropped drafts while
            // cross-checking the staging store entry-by-entry. No array is
            // allocated and no record is constructed in this pass.
            int entryCount = draftRegistry.EntryCount;
            int stagedCount = 0;
            int droppedCount = 0;
            long previousCaptureFrameId = 0;
            long totalByteCount = 0;

            for (int i = 0; i < entryCount; i++)
            {
                CaptureFrameDraft draft = draftRegistry.GetEntryDraft(i);
                CaptureFrameDraftStatus status = draftRegistry.GetEntryStatus(i);
                CaptureFrameDropReason dropReason = draftRegistry.GetEntryDropReason(i);
                DraftDropTraceEmissionState emissionState = draftRegistry.GetEntryEmissionState(i);

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
                if (!draftRegistry.TryGet(draft.Request, out CaptureFrameDraft canonicalDraft, out CaptureFrameDraftStatus canonicalStatus))
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
                        if (dropReason != CaptureFrameDropReason.None)
                        {
                            throw new InvalidOperationException("Staged entry must have drop reason None.");
                        }

                        if (emissionState != DraftDropTraceEmissionState.None)
                        {
                            throw new InvalidOperationException("Staged entry must have emission state None.");
                        }

                        if (!stagingStore.TryGet(captureFrameId, out CaptureFramePngStagingEntry stagingEntry))
                        {
                            throw new InvalidOperationException("A staged draft has no staging entry.");
                        }

                        if (stagingEntry == null || !stagingEntry.IsCreated)
                        {
                            throw new InvalidOperationException("A staged draft's staging entry is missing or disposed.");
                        }

                        if (stagingEntry.TestRunId != registryRun.TestRunId || stagingEntry.CaptureFrameId != captureFrameId)
                        {
                            throw new InvalidOperationException("A staged draft's staging entry IDs do not match.");
                        }

                        totalByteCount = checked(totalByteCount + stagingEntry.ByteCount);
                        stagedCount++;
                        break;

                    case CaptureFrameDraftStatus.Dropped:
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

                        if (stagingStore.TryGet(captureFrameId, out _))
                        {
                            throw new InvalidOperationException("A dropped draft must not have a staging entry.");
                        }

                        droppedCount++;
                        break;

                    default:
                        throw new InvalidOperationException("Entry has an undefined status.");
                }
            }

            if (stagingStore.Count != stagedCount)
            {
                throw new InvalidOperationException("Staging store count must equal the staged draft count.");
            }

            if (totalByteCount != stagingStore.TotalByteCount)
            {
                throw new InvalidOperationException("Staging store total byte count does not match the staged entries.");
            }

            // Sole allocator: allocate each array exactly once and fill it from
            // the validated registry and store. No array is received from or
            // returned to a caller, so no external alias can exist.
            CaptureFrameRecord[] records = new CaptureFrameRecord[stagedCount];
            CaptureFramePngStagingEntry[] stagingEntries = new CaptureFramePngStagingEntry[stagedCount];

            int recordIndex = 0;
            for (int i = 0; i < entryCount; i++)
            {
                if (draftRegistry.GetEntryStatus(i) != CaptureFrameDraftStatus.Staged)
                {
                    continue;
                }

                CaptureFrameDraft draft = draftRegistry.GetEntryDraft(i);

                if (!stagingStore.TryGet(draft.CaptureFrameId, out CaptureFramePngStagingEntry stagingEntry))
                {
                    throw new InvalidOperationException("A staged draft has no staging entry.");
                }

                CaptureFrameRecord record = new CaptureFrameRecord(
                    finalRun,
                    draft.Request,
                    draft.Timing,
                    draft.HeadPose,
                    draft.LeftControllerPose,
                    draft.RightControllerPose,
                    draft.CommitPathId);

                records[recordIndex] = record;
                stagingEntries[recordIndex] = stagingEntry;
                recordIndex++;
            }

            _run = finalRun;
            _records = records;
            _stagingEntries = stagingEntries;
            _droppedCount = droppedCount;
        }

        /// <summary>The final run reference shared by every record.</summary>
        internal CaptureRunReference Run => _run;

        /// <summary>Number of converted staged records.</summary>
        internal int RecordCount => _records.Length;

        /// <summary>Number of dropped drafts excluded from the result.</summary>
        internal int DroppedCount => _droppedCount;

        /// <summary>
        /// Returns the record at the given index in capture frame ID ascending
        /// order. Out-of-range indices throw
        /// <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>
        internal CaptureFrameRecord GetRecord(int index)
        {
            if (index < 0 || index >= _records.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the record count.");
            }

            return _records[index];
        }

        /// <summary>
        /// Returns the non-owning staging entry reference paired with the record
        /// at the given index. The entry stays caller-owned and is never
        /// disposed by this result. Out-of-range indices throw
        /// <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>
        internal CaptureFramePngStagingEntry GetStagingEntry(int index)
        {
            if (index < 0 || index >= _stagingEntries.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the record count.");
            }

            return _stagingEntries[index];
        }
    }
}
