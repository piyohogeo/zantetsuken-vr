using System;
using System.IO;
using UnityEngine;
using Zantetsu.Trace;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable metadata for a saved trace bundle, combining a frozen snapshot
    /// with the run context that produced it. Holds no event array; the events
    /// themselves live in the snapshot and the binary file.
    /// </summary>
    public sealed class TraceRunManifest
    {
        public const int CurrentSchemaVersion = 1;

        private TraceRunManifest(
            TraceRunContext context,
            int eventCount,
            int triggerHistoryCount,
            int capturedPostRollCount,
            bool wasHistoryOverwrittenAtTrigger)
        {
            TestRunId = context.TestRunId;
            CapturedUtcUnixMilliseconds = context.CapturedUtcUnixMilliseconds;
            BuildId = context.BuildId;
            UnityVersion = context.UnityVersion;
            PackageLockSha256 = context.PackageLockSha256;
            SceneId = context.SceneId;
            RandomSeed = context.RandomSeed;
            FixedDeltaTimeSeconds = context.FixedDeltaTimeSeconds;
            QualityLevel = context.QualityLevel;
            QualityName = context.QualityName;
            WorldPhysicsProfileVersion = context.WorldPhysicsProfileVersion;
            Gravity = context.Gravity;

            EventCount = eventCount;
            TriggerHistoryCount = triggerHistoryCount;
            CapturedPostRollCount = capturedPostRollCount;
            WasHistoryOverwrittenAtTrigger = wasHistoryOverwrittenAtTrigger;
        }

        /// <summary>
        /// Combines a frozen snapshot with a run context into immutable bundle
        /// metadata. The context values and snapshot metadata are copied and
        /// fixed at creation time.
        /// </summary>
        public static TraceRunManifest Create(TraceCaptureSnapshot snapshot, TraceRunContext context)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if ((long)snapshot.EventCount != (long)snapshot.TriggerHistoryCount + (long)snapshot.CapturedPostRollCount)
            {
                throw new InvalidOperationException("Snapshot event counts are inconsistent.");
            }

            return new TraceRunManifest(
                context,
                snapshot.EventCount,
                snapshot.TriggerHistoryCount,
                snapshot.CapturedPostRollCount,
                snapshot.WasHistoryOverwrittenAtTrigger);
        }

        /// <summary>
        /// Rebuilds a manifest from deserialized values, re-validating schema
        /// version, trace format version and event-count consistency. Internal
        /// to the observability assembly; does not construct an event array or
        /// a fake snapshot.
        /// </summary>
        internal static TraceRunManifest Restore(
            TraceRunContext context,
            int schemaVersion,
            ushort traceFormatMajor,
            ushort traceFormatMinor,
            int eventCount,
            int triggerHistoryCount,
            int capturedPostRollCount,
            bool wasHistoryOverwrittenAtTrigger)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (schemaVersion != CurrentSchemaVersion)
            {
                throw new InvalidDataException("Unsupported manifest schema version.");
            }

            if (traceFormatMajor != TraceBinaryFormat.MajorVersion || traceFormatMinor != TraceBinaryFormat.MinorVersion)
            {
                throw new InvalidDataException("Unsupported trace format version.");
            }

            if (eventCount < 0)
            {
                throw new InvalidDataException("Event count must not be negative.");
            }

            if (triggerHistoryCount < 0)
            {
                throw new InvalidDataException("Trigger history count must not be negative.");
            }

            if (capturedPostRollCount < 0)
            {
                throw new InvalidDataException("Captured post-roll count must not be negative.");
            }

            if ((long)eventCount != (long)triggerHistoryCount + (long)capturedPostRollCount)
            {
                throw new InvalidDataException("Event counts are inconsistent.");
            }

            return new TraceRunManifest(
                context,
                eventCount,
                triggerHistoryCount,
                capturedPostRollCount,
                wasHistoryOverwrittenAtTrigger);
        }

        public int SchemaVersion => CurrentSchemaVersion;

        public long TestRunId { get; }

        public long CapturedUtcUnixMilliseconds { get; }

        public string BuildId { get; }

        public string UnityVersion { get; }

        public string PackageLockSha256 { get; }

        public string SceneId { get; }

        public long RandomSeed { get; }

        public double FixedDeltaTimeSeconds { get; }

        public int QualityLevel { get; }

        public string QualityName { get; }

        public int WorldPhysicsProfileVersion { get; }

        public Vector3 Gravity { get; }

        public ushort TraceFormatMajorVersion => TraceBinaryFormat.MajorVersion;

        public ushort TraceFormatMinorVersion => TraceBinaryFormat.MinorVersion;

        public int EventCount { get; }

        public int TriggerHistoryCount { get; }

        public int CapturedPostRollCount { get; }

        public bool WasHistoryOverwrittenAtTrigger { get; }
    }
}
