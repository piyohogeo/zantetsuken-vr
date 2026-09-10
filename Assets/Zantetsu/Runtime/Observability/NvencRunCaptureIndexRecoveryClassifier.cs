using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Side-effect-free classifier that turns one Phase 0.11 NVENC Capture
    /// Index recovery snapshot into a single disposition and, only when a
    /// commit is required, the existing commit mode that describes how the
    /// temporary must be handled.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cascade is fixed. With no final index the temporary decides how the
    /// commit starts: none creates one, the authoritative one is reused, and an
    /// invalid one is replaced. With a final index that already matches the
    /// authoritative plan the Run is past this step and goes to
    /// CaptureComplete. Anything else on either document - canonical bytes
    /// describing another content, or a document too large to have been
    /// observed as one - is a collision that changes nothing, because a
    /// foreign canonical document is evidence of another writer, not of a
    /// half-finished commit.
    /// </para>
    /// <para>
    /// CaptureComplete carries no commit mode: whether a temporary is still
    /// lying around is already in the snapshot, so no cleanup mode or flag is
    /// duplicated into the decision.
    /// </para>
    /// <para>
    /// It touches no file, serializes and compares no canonical bytes,
    /// re-verifies no plan or chunk, deletes no temporary, renames, commits,
    /// and runs no CaptureComplete, releases no lock, and never retries or
    /// falls back. The comparison against the authoritative plan already
    /// happened in the observation it is given.
    /// </para>
    /// </remarks>
    internal static class NvencRunCaptureIndexRecoveryClassifier
    {
        internal static NvencRunCaptureIndexRecoveryDecision Classify(
            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (!snapshot.IsValid)
            {
                throw new ArgumentException("Snapshot must be valid.", nameof(snapshot));
            }

            return new NvencRunCaptureIndexRecoveryDecision(snapshot);
        }

        /// <summary>
        /// Pure computation shared with the decision constructor and its
        /// <c>IsValid</c> recomputation. Assumes the snapshot is valid.
        /// </summary>
        internal static NvencRunCaptureIndexRecoveryDisposition ComputeDisposition(
            NvencRunCaptureIndexRecoveryInspectionSnapshot snapshot,
            out CaptureRunCaptureIndexCommitMode commitMode)
        {
            commitMode = CaptureRunCaptureIndexCommitMode.None;

            NvencRunCaptureIndexObservationStatus temporary = snapshot.TemporaryIndexStatus;

            switch (snapshot.FinalIndexStatus)
            {
                case NvencRunCaptureIndexObservationStatus.Absent:
                    switch (temporary)
                    {
                        case NvencRunCaptureIndexObservationStatus.Absent:
                            commitMode = CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit;
                            return NvencRunCaptureIndexRecoveryDisposition.CommitRequired;

                        case NvencRunCaptureIndexObservationStatus.MatchesAuthoritative:
                            commitMode =
                                CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit;
                            return NvencRunCaptureIndexRecoveryDisposition.CommitRequired;

                        case NvencRunCaptureIndexObservationStatus.Invalid:
                            commitMode =
                                CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit;
                            return NvencRunCaptureIndexRecoveryDisposition.CommitRequired;

                        default:
                            // Canonical bytes of another content, or a document
                            // past the limit: another writer, not this Run.
                            return NvencRunCaptureIndexRecoveryDisposition
                                .PublicationRecoveryCollision;
                    }

                case NvencRunCaptureIndexObservationStatus.MatchesAuthoritative:
                    switch (temporary)
                    {
                        case NvencRunCaptureIndexObservationStatus.Absent:
                        case NvencRunCaptureIndexObservationStatus.MatchesAuthoritative:
                        case NvencRunCaptureIndexObservationStatus.Invalid:
                            return NvencRunCaptureIndexRecoveryDisposition.CaptureCompleteRequired;

                        default:
                            return NvencRunCaptureIndexRecoveryDisposition
                                .PublicationRecoveryCollision;
                    }

                default:
                    // A final index that is not this Run's authoritative one
                    // stops the Run whatever the temporary is.
                    return NvencRunCaptureIndexRecoveryDisposition.PublicationRecoveryCollision;
            }
        }
    }
}
