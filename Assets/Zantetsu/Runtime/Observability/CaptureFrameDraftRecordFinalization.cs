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
    /// share the same <c>CaptureFrameId</c>. Both arrays are owned privately
    /// and never exposed; only <see cref="GetRecord"/> and
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
