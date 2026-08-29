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
    /// The record at an index and the staging entry at the same index always
    /// share the same <c>CaptureFrameId</c>. The constructor validates every
    /// element, the shared run instance, and the strict ascending order, then
    /// defensively copies both arrays so a later mutation of the caller's
    /// arrays cannot change this result. Only <see cref="GetRecord"/> and
    /// <see cref="GetStagingEntry"/> provide access, with out-of-range indices
    /// rejected by <see cref="ArgumentOutOfRangeException"/>.
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
            CaptureFrameRecord[] records,
            CaptureFramePngStagingEntry[] stagingEntries,
            int droppedCount)
        {
            if (run == null)
            {
                throw new ArgumentNullException(nameof(run));
            }

            if (records == null)
            {
                throw new ArgumentNullException(nameof(records));
            }

            if (stagingEntries == null)
            {
                throw new ArgumentNullException(nameof(stagingEntries));
            }

            if (records.Length != stagingEntries.Length)
            {
                throw new ArgumentException("Record and staging entry arrays must have the same length.", nameof(stagingEntries));
            }

            if (droppedCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(droppedCount), droppedCount, "Dropped count must not be negative.");
            }

            long previousCaptureFrameId = 0;
            for (int i = 0; i < records.Length; i++)
            {
                CaptureFrameRecord record = records[i];
                if (record == null)
                {
                    throw new ArgumentException("Record array must not contain null elements.", nameof(records));
                }

                if (!ReferenceEquals(record.Run, run))
                {
                    throw new ArgumentException("Every record must reference the run instance.", nameof(records));
                }

                long captureFrameId = record.CaptureFrameId;
                if (captureFrameId <= 0)
                {
                    throw new ArgumentException("Record capture frame IDs must be positive.", nameof(records));
                }

                if (i > 0 && captureFrameId <= previousCaptureFrameId)
                {
                    throw new ArgumentException("Record capture frame IDs must be strictly ascending.", nameof(records));
                }

                previousCaptureFrameId = captureFrameId;

                CaptureFramePngStagingEntry stagingEntry = stagingEntries[i];
                if (stagingEntry == null)
                {
                    throw new ArgumentException("Staging entry array must not contain null elements.", nameof(stagingEntries));
                }

                if (stagingEntry.CaptureFrameId != captureFrameId)
                {
                    throw new ArgumentException("The record and staging entry at the same index must share the same capture frame ID.", nameof(stagingEntries));
                }

                if (stagingEntry.TestRunId != run.TestRunId)
                {
                    throw new ArgumentException("Staging entry test run ID must match the run.", nameof(stagingEntries));
                }
            }

            // Defensive copy: the result owns its own reference arrays, so a
            // later mutation of the caller's arrays cannot change this result.
            CaptureFrameRecord[] recordCopy = new CaptureFrameRecord[records.Length];
            CaptureFramePngStagingEntry[] entryCopy = new CaptureFramePngStagingEntry[stagingEntries.Length];
            Array.Copy(records, recordCopy, records.Length);
            Array.Copy(stagingEntries, entryCopy, stagingEntries.Length);

            _run = run;
            _records = recordCopy;
            _stagingEntries = entryCopy;
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
