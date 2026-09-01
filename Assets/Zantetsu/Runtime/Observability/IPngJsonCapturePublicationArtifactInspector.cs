using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect boundary for one Capture Run publication artifact
    /// inspection, shared by the Recovery and Fresh paths.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The inspection is synchronous, single-attempt, and read-only. For a
    /// null operation the inspector must throw
    /// <see cref="ArgumentNullException"/> with the parameter name
    /// <c>operation</c>, and for an invalid operation it must throw
    /// <see cref="ArgumentException"/> with the parameter name
    /// <c>operation</c>, in both cases before any filesystem contact. The
    /// returned snapshot must never be null, and must satisfy
    /// <c>ReferenceEquals(snapshot.IssuedBy, this)</c>,
    /// <c>ReferenceEquals(snapshot.Operation, operation)</c>, and
    /// <c>snapshot.IsIssuedFor(this, operation)</c>.
    /// </para>
    /// <para>
    /// The inspector must record one entry observation per operation entry in
    /// ascending index order, each corresponding to the exact operation path
    /// set, and must probe the trace manifest and the four artifacts as
    /// no-follow regular files with bounded probes that never read past the
    /// operation's limits. Every stream, hash object, and handle must be
    /// disposed on every path. The inspector must not hold, mutate, or dispose
    /// the operation, its lease, or any path set, must not create, delete,
    /// rename, repair, retry, or fall back on the filesystem, must not acquire
    /// or release locks, and must not touch registries, drafts, trace logging,
    /// Unity APIs, or notification. On any exception it must not return a
    /// snapshot. Thread selection is the responsibility of the caller.
    /// </para>
    /// </remarks>
    internal interface IPngJsonCapturePublicationArtifactInspector
    {
        PngJsonCapturePublicationArtifactInspectionSnapshot Inspect(
            PngJsonCapturePublicationArtifactInspectionOperation operation);
    }
}
