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
    /// external alias to either array can exist. The record at an index and the
    /// staging entry at the same index always share the same
    /// <c>CaptureFrameId</c>; the constructor validates every built element
    /// (created entry and matching IDs) before returning. Only
    /// <see cref="GetRecord"/> and <see cref="GetStagingEntry"/> expose the
    /// arrays, with out-of-range indices rejected by
    /// <see cref="ArgumentOutOfRangeException"/>.
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
            CaptureRunReference run,
            CaptureFrameDraftRegistry draftRegistry,
            CaptureFramePngStagingStore stagingStore,
            int droppedCount)
        {
            if (run == null)
            {
                throw new ArgumentNullException(nameof(run));
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

            if (droppedCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(droppedCount), droppedCount, "Dropped count must not be negative.");
            }

            // Sole allocator: count staged entries first, then allocate each
            // array exactly once and fill it from the registry and store. No
            // array is received from or returned to a caller, so no external
            // alias to these arrays can exist.
            int entryCount = draftRegistry.EntryCount;
            int stagedCount = 0;
            for (int i = 0; i < entryCount; i++)
            {
                if (draftRegistry.GetEntryStatus(i) == CaptureFrameDraftStatus.Staged)
                {
                    stagedCount++;
                }
            }

            CaptureFrameRecord[] records = new CaptureFrameRecord[stagedCount];
            CaptureFramePngStagingEntry[] stagingEntries = new CaptureFramePngStagingEntry[stagedCount];

            int recordIndex = 0;
            for (int i = 0; i < entryCount; i++)
            {
                CaptureFrameDraftStatus status = draftRegistry.GetEntryStatus(i);
                if (status == CaptureFrameDraftStatus.Dropped)
                {
                    continue;
                }

                if (status != CaptureFrameDraftStatus.Staged)
                {
                    throw new InvalidOperationException("Entry has an undefined status.");
                }

                CaptureFrameDraft draft = draftRegistry.GetEntryDraft(i);
                long captureFrameId = draft.CaptureFrameId;

                if (!stagingStore.TryGet(captureFrameId, out CaptureFramePngStagingEntry stagingEntry))
                {
                    throw new InvalidOperationException("A staged draft has no staging entry.");
                }

                if (stagingEntry == null || !stagingEntry.IsCreated)
                {
                    throw new InvalidOperationException("A staged draft's staging entry is missing or disposed.");
                }

                if (stagingEntry.CaptureFrameId != captureFrameId)
                {
                    throw new InvalidOperationException("A staged draft's staging entry capture frame ID does not match.");
                }

                if (stagingEntry.TestRunId != run.TestRunId)
                {
                    throw new InvalidOperationException("A staged draft's staging entry test run ID does not match the run.");
                }

                CaptureFrameRecord record = new CaptureFrameRecord(
                    run,
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

            _run = run;
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
