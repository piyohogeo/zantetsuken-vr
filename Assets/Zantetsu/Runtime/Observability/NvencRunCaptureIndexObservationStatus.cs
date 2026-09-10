namespace Zantetsu.Observability
{
    /// <summary>
    /// What one Phase 0.11 NVENC Capture Index document was observed to be,
    /// already compared against the authoritative plan.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately not the generic publication document observation
    /// status: it carries the comparison as well as the read.
    /// <see cref="MatchesAuthoritative"/> means the canonical bytes were the
    /// authoritative plan's, and <see cref="CanonicalMismatch"/> means they
    /// were canonical but described something else. The comparison belongs to
    /// the inspector that read the bytes, so the classifier never re-serializes
    /// or re-compares a plan; this is an observation, not an authority token.
    /// </para>
    /// <para>
    /// Each value has one condition, and nothing else may be folded into it,
    /// because a later commit is allowed to delete a temporary this status
    /// calls <see cref="Invalid"/>.
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <see cref="Absent"/>: the no-follow observation confirmed there is no
    /// such entry - never an open or read that failed to say so.
    /// </description></item>
    /// <item><description>
    /// <see cref="Invalid"/>: an ordinary file was opened no-follow, read to
    /// its end within the limit, and its bytes did not decode as a canonical
    /// document. It means unusable content, never an unusable observation.
    /// </description></item>
    /// <item><description>
    /// <see cref="LimitExceeded"/>: one byte past the limit was actually
    /// observed.
    /// </description></item>
    /// <item><description>
    /// A reparse point or other non-file kind, a path escaping the Run root,
    /// a platform without no-follow support, and any open or read failure -
    /// access denied included - are none of the above. An inspector that meets
    /// one produces no snapshot at all: it throws, or reports a non-classifying
    /// result. Turning such a failure into <see cref="Invalid"/> would let a
    /// commit delete a file nobody was able to read, and turning it into
    /// <see cref="Absent"/> would claim an entry is gone that may well be
    /// there.
    /// </description></item>
    /// </list>
    /// <para>
    /// Values are explicit and append-only.
    /// </para>
    /// </remarks>
    internal enum NvencRunCaptureIndexObservationStatus : int
    {
        None = 0,
        Absent = 1,
        MatchesAuthoritative = 2,
        CanonicalMismatch = 3,
        Invalid = 4,
        LimitExceeded = 5
    }
}
