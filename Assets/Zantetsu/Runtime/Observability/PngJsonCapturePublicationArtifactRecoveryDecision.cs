using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable result of a pure shared artifact recovery classification: the
    /// shared inspection snapshot it classified and the fixed disposition.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The disposition is computed from the snapshot at construction, so no
    /// external caller can hand in a contradicting value; no constructor or
    /// factory accepts a disposition. <see cref="IsValid"/> re-validates the
    /// snapshot once, recomputes the disposition with the same token, and
    /// reports success only when the held value matches, so a decision whose
    /// snapshot, entry, authority, operation, or lease was tampered with after
    /// construction becomes invalid.
    /// </para>
    /// <para>
    /// The type owns exactly two fields — the snapshot and the disposition —
    /// holds no token, and is not an <see cref="IDisposable"/>, MonoBehaviour,
    /// or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactRecoveryDecision
    {
        private readonly PngJsonCapturePublicationArtifactInspectionSnapshot _snapshot;
        private readonly CaptureRunPublicationArtifactRecoveryDisposition _disposition;

        private PngJsonCapturePublicationArtifactRecoveryDecision(
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot,
            CaptureRunPublicationArtifactRecoveryDisposition disposition)
        {
            _snapshot = snapshot;
            _disposition = disposition;
        }

        /// <summary>
        /// Trusted construction path: classifies once with the issued token
        /// and assigns the two fields only when the token proves the exact
        /// snapshot and its structure is intact. The token is never retained.
        /// A stale token, structure mismatch, or entry-proof mismatch is
        /// rejected before a decision can be issued. The private constructor
        /// keeps the disposition unfabricable by callers.
        /// </summary>
        internal static PngJsonCapturePublicationArtifactRecoveryDecision Create(
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot,
            PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (!PngJsonCapturePublicationArtifactRecoveryClassifier.TryComputeDisposition(
                snapshot, token, out CaptureRunPublicationArtifactRecoveryDisposition disposition))
            {
                throw new ArgumentException(
                    "Token must be issued for the exact snapshot and its structure must be intact.", nameof(token));
            }

            return new PngJsonCapturePublicationArtifactRecoveryDecision(snapshot, disposition);
        }

        internal PngJsonCapturePublicationArtifactInspectionSnapshot Snapshot => _snapshot;

        internal PngJsonCapturePublicationArtifactInspectionOperation Operation => _snapshot.Operation;

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _snapshot.Authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _snapshot.AuthorityKind;

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _snapshot.Plan;

        internal CaptureRunRootLayout RootLayout => _snapshot.RootLayout;

        internal CaptureRunLockIdentityEvidence LockIdentityEvidence => _snapshot.LockIdentityEvidence;

        internal long TestRunId => _snapshot.TestRunId;

        internal string RunInitializationId => _snapshot.RunInitializationId;

        internal string RunManifestContentSha256 => _snapshot.RunManifestContentSha256;

        internal CaptureRunPublicationArtifactRecoveryDisposition Disposition => _disposition;

        /// <summary>
        /// Exception-safe recomputation: fully validates the snapshot and
        /// issues a token once, recomputes the disposition with the same token
        /// without per-entry full validation or token re-issuance, and reports
        /// success only when the computation succeeds and the held disposition
        /// matches.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (_snapshot == null)
                {
                    return false;
                }

                if (!_snapshot.TryValidate(out PngJsonCapturePublicationArtifactInspectionSnapshot.ValidationToken token))
                {
                    return false;
                }

                if (!PngJsonCapturePublicationArtifactRecoveryClassifier.TryComputeDisposition(
                    _snapshot, token, out CaptureRunPublicationArtifactRecoveryDisposition expected))
                {
                    return false;
                }

                return _disposition == expected;
            }
        }
    }
}
