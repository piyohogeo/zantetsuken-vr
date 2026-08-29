using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable Schema v1 Capture Publication Plan: the expected capture frame
    /// ID set and its PNG/sidecar paths, lengths, and hashes, fixed before any
    /// file I/O so the durable staging and publication steps can consume an
    /// unchanging expectation. No public constructor is provided.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="SchemaVersion"/> is fixed at 1. The entry array is
    /// defensively copied at construction so later mutation of the caller's
    /// array cannot change this plan. Entries are caller-owned and must outlive
    /// this plan. This type owns, disposes, registers, and generates nothing
    /// (no ID, initialization ID, hash, or path is produced here), and is not
    /// an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CapturePublicationPlan
    {
        private readonly long _testRunId;
        private readonly string _runInitializationId;
        private readonly string _runManifestContentSha256;
        private readonly CapturePublicationPlanEntry[] _entries;

        internal CapturePublicationPlan(
            long testRunId,
            string runInitializationId,
            string runManifestContentSha256,
            CapturePublicationPlanEntry[] entries)
        {
            if (testRunId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(testRunId), testRunId, "Test run ID must be greater than zero.");
            }

            if (runInitializationId == null)
            {
                throw new ArgumentNullException(nameof(runInitializationId));
            }

            if (!IsLowercaseHex(runInitializationId, 32))
            {
                throw new ArgumentException("Run initialization ID must be 32 lowercase ASCII hex characters.", nameof(runInitializationId));
            }

            if (runManifestContentSha256 == null)
            {
                throw new ArgumentNullException(nameof(runManifestContentSha256));
            }

            if (!IsLowercaseHex(runManifestContentSha256, 64))
            {
                throw new ArgumentException("Run manifest content SHA-256 must be 64 lowercase ASCII hex characters.", nameof(runManifestContentSha256));
            }

            if (entries == null)
            {
                throw new ArgumentNullException(nameof(entries));
            }

            if (entries.Length > 100000)
            {
                throw new ArgumentOutOfRangeException(nameof(entries), entries.Length, "Entry count must not exceed 100000.");
            }

            long previousCaptureFrameId = 0;
            for (int i = 0; i < entries.Length; i++)
            {
                CapturePublicationPlanEntry entry = entries[i];
                if (entry == null)
                {
                    throw new ArgumentException("Entry array must not contain null elements.", nameof(entries));
                }

                long captureFrameId = entry.CaptureFrameId;
                if (i > 0 && captureFrameId <= previousCaptureFrameId)
                {
                    throw new ArgumentException("Capture frame IDs must be strictly ascending without duplicates.", nameof(entries));
                }

                previousCaptureFrameId = captureFrameId;
            }

            CapturePublicationPlanEntry[] copy = new CapturePublicationPlanEntry[entries.Length];
            Array.Copy(entries, copy, entries.Length);

            _testRunId = testRunId;
            _runInitializationId = runInitializationId;
            _runManifestContentSha256 = runManifestContentSha256;
            _entries = copy;
        }

        internal int SchemaVersion => 1;

        internal long TestRunId => _testRunId;

        internal string RunInitializationId => _runInitializationId;

        internal string RunManifestContentSha256 => _runManifestContentSha256;

        internal int EntryCount => _entries.Length;

        /// <summary>
        /// Returns the entry at the given index in capture frame ID ascending
        /// order. Out-of-range indices throw
        /// <see cref="ArgumentOutOfRangeException"/>.
        /// </summary>
        internal CapturePublicationPlanEntry GetEntry(int index)
        {
            if (index < 0 || index >= _entries.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be within the entry count.");
            }

            return _entries[index];
        }

        private static bool IsLowercaseHex(string value, int length)
        {
            if (value == null || value.Length != length)
            {
                return false;
            }

            for (int i = 0; i < length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
