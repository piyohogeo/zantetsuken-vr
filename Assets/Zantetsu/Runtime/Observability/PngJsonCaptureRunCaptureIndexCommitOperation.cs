using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free PngJson Capture Index commit operation: the
    /// exact action plan, its single commit step, the exact publication path
    /// set, the derived commit mode, and the canonical byte sequence that must
    /// become <c>capture.index</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The canonical bytes are exactly the output of
    /// <see cref="PngJsonCapturePublicationPlanCodec"/> for the authoritative
    /// plan, serialized once at construction and held privately.
    /// Only <see cref="GetCanonicalBytes"/> returns a defensive copy; no byte
    /// array, token, or lease is ever exposed.
    /// </para>
    /// <para>
    /// <see cref="Mode"/> is derived uniquely from the authority kind: a Fresh
    /// frozen run always commits a new temporary index, while a Recovery
    /// decision derives the mode from the observed <c>capture.index.tmp</c>
    /// status. <see cref="Create"/> validates the whole plan once and issues a
    /// plan-bound validation token; <see cref="CreateIndexLocal"/> re-verifies
    /// only the single commit step in O(1) and serializes the canonical bytes
    /// exactly once.
    /// </para>
    /// <para>
    /// This type performs no filesystem work, no hash recomputation, and no ID
    /// generation, owns and disposes nothing besides the held byte array, and
    /// is not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCaptureRunCaptureIndexCommitOperation
    {
        private readonly PngJsonCapturePublicationArtifactRecoveryActionPlan _actionPlan;
        private readonly int _stepIndex;
        private readonly CaptureRunPublicationPathSet _publicationPaths;
        private readonly CaptureRunCaptureIndexCommitMode _mode;
        private readonly byte[] _canonicalBytes;

        private PngJsonCaptureRunCaptureIndexCommitOperation(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex,
            CaptureRunPublicationPathSet publicationPaths,
            CaptureRunCaptureIndexCommitMode mode,
            byte[] canonicalBytes)
        {
            _actionPlan = actionPlan;
            _stepIndex = stepIndex;
            _publicationPaths = publicationPaths;
            _mode = mode;
            _canonicalBytes = canonicalBytes;
        }

        /// <summary>
        /// Validated factory: validates the whole plan once through a
        /// non-throwing token issuance and delegates to the token-gated
        /// index-local path with the same token. The full entry scan happens
        /// here, at the action-plan validation boundary, exactly once.
        /// </summary>
        internal static PngJsonCaptureRunCaptureIndexCommitOperation Create(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (!actionPlan.TryAcquireValidationToken(out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token))
            {
                throw new ArgumentException("Action plan must be fully valid.", nameof(actionPlan));
            }

            return CreateIndexLocal(actionPlan, token, stepIndex);
        }

        /// <summary>
        /// O(1) token-gated factory: re-verifies only the single commit step
        /// through the token's index-local commit-input accessor, derives the
        /// commit mode and publication path set, and serializes the canonical
        /// bytes exactly once before issuing the operation. It does not
        /// re-validate the plan, re-issue a token, or re-scan any entry.
        /// </summary>
        internal static PngJsonCaptureRunCaptureIndexCommitOperation CreateIndexLocal(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
            int stepIndex)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            if (stepIndex < 0 || stepIndex >= actionPlan.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(stepIndex), stepIndex, "Step index must be within the step count.");
            }

            if (!token.TryGetIssuedCommitInputs(actionPlan, stepIndex, out _, out PngJsonCapturePublicationArtifactRecoveryDecision decision))
            {
                throw new ArgumentException("Step must be the single commit capture index step bound by the issued token.", nameof(stepIndex));
            }

            if (!TryCorrelate(actionPlan, decision, out CaptureRunPublicationPathSet publicationPaths, out CaptureRunCaptureIndexCommitMode mode))
            {
                throw new ArgumentException("Commit capture index correlation must remain intact.", nameof(actionPlan));
            }

            byte[] canonicalBytes = PngJsonCapturePublicationPlanCodec.SerializeCanonical(actionPlan.AuthoritativePlan);
            if (canonicalBytes == null || canonicalBytes.Length == 0)
            {
                throw new InvalidOperationException("Canonical serialization must produce non-empty bytes.");
            }

            return new PngJsonCaptureRunCaptureIndexCommitOperation(actionPlan, stepIndex, publicationPaths, mode, canonicalBytes);
        }

        internal PngJsonCapturePublicationArtifactRecoveryActionPlan ActionPlan => _actionPlan;

        internal int StepIndex => _stepIndex;

        internal CaptureRunPublicationArtifactRecoveryStep Step => _actionPlan.GetStep(_stepIndex);

        internal PngJsonCapturePublicationArtifactRecoveryDecision Decision => _actionPlan.Decision;

        internal PngJsonCapturePublicationArtifactInspectionAuthority Authority => _actionPlan.Authority;

        internal PngJsonCapturePublicationArtifactInspectionAuthorityKind AuthorityKind => _actionPlan.AuthorityKind;

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _actionPlan.AuthoritativePlan;

        internal CaptureRunCaptureIndexCommitMode Mode => _mode;

        internal CaptureRunPublicationPathSet PublicationPaths => _publicationPaths;

        internal string TemporaryPath => _publicationPaths.CaptureIndexTemporaryPath;

        internal string FinalPath => _publicationPaths.CaptureIndexPath;

        internal long ByteCount => _canonicalBytes.Length;

        internal CaptureRunRootLayout RootLayout => _actionPlan.RootLayout;

        internal long TestRunId => _actionPlan.TestRunId;

        internal string RunInitializationId => _actionPlan.RunInitializationId;

        internal string RunManifestContentSha256 => _actionPlan.RunManifestContentSha256;

        /// <summary>
        /// Returns a fresh defensive copy of the held canonical bytes. The
        /// internal array is never exposed, so later mutation of the copy
        /// cannot change this operation.
        /// </summary>
        internal byte[] GetCanonicalBytes()
        {
            if (_canonicalBytes == null)
            {
                return null;
            }

            byte[] copy = new byte[_canonicalBytes.Length];
            Array.Copy(_canonicalBytes, copy, _canonicalBytes.Length);
            return copy;
        }

        /// <summary>
        /// Derives the commit mode uniquely from an observed
        /// <c>capture.index.tmp</c> state. Limit-exceeded and undefined
        /// statuses are rejected.
        /// </summary>
        internal static CaptureRunCaptureIndexCommitMode DeriveMode(
            CaptureRunPublicationDocumentObservation captureIndexTemporary)
        {
            if (captureIndexTemporary == null)
            {
                throw new ArgumentNullException(nameof(captureIndexTemporary));
            }

            if (!TryDeriveModeFromStatus(captureIndexTemporary.Status, out CaptureRunCaptureIndexCommitMode mode))
            {
                throw new ArgumentException("Capture index temporary status must be absent, canonical, or invalid.", nameof(captureIndexTemporary));
            }

            return mode;
        }

        /// <summary>
        /// Full validity: validates the whole plan once through a non-throwing
        /// token issuance and delegates to <see cref="IsValidWithToken"/> with
        /// the same token. Never throws.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (_actionPlan == null || _canonicalBytes == null)
                {
                    return false;
                }

                if (!_actionPlan.TryAcquireValidationToken(out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token))
                {
                    return false;
                }

                return IsValidWithToken(token);
            }
        }

        /// <summary>
        /// Token-gated full validity: runs the index-local correlation check
        /// and then re-serializes the authoritative plan's canonical bytes once
        /// for a byte-content comparison. It does not re-validate the plan or
        /// re-scan any entry; the caller must supply a live token acquired from
        /// the action plan, which guarantees the plan was fully validated once
        /// upstream. Never throws.
        /// </summary>
        internal bool IsValidWithToken(
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            if (!IsValidIndexLocal(token))
            {
                return false;
            }

            PngJsonCapturePublicationPlan authoritativePlan = AuthoritativePlan;
            if (authoritativePlan == null)
            {
                return false;
            }

            byte[] expected;
            try
            {
                expected = PngJsonCapturePublicationPlanCodec.SerializeCanonical(authoritativePlan);
            }
            catch (Exception)
            {
                return false;
            }

            return BytesEqual(_canonicalBytes, expected);
        }

        /// <summary>
        /// Index-local validity for an already-validated action plan: re-verifies
        /// only the single commit step's structure, the derived mode, the exact
        /// publication path set, and the provenance correlation in O(1), without
        /// re-validating the plan, re-scanning entries, or re-serializing the
        /// canonical bytes. Full byte-content verification remains the
        /// responsibility of <see cref="IsValidWithToken"/>. Never throws.
        /// </summary>
        internal bool IsValidIndexLocal(
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            try
            {
                if (token == null || _actionPlan == null || _publicationPaths == null
                    || _mode == CaptureRunCaptureIndexCommitMode.None
                    || _canonicalBytes == null || _canonicalBytes.Length == 0)
                {
                    return false;
                }

                if (!token.TryGetIssuedCommitInputs(_actionPlan, _stepIndex, out _, out PngJsonCapturePublicationArtifactRecoveryDecision decision))
                {
                    return false;
                }

                if (!TryCorrelate(_actionPlan, decision, out CaptureRunPublicationPathSet publicationPaths, out CaptureRunCaptureIndexCommitMode mode))
                {
                    return false;
                }

                return ReferenceEquals(_publicationPaths, publicationPaths) && _mode == mode;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// O(1), exception-safe correlation shared by construction and
        /// validity: confirms the decision's classification proof (the
        /// <c>CommitCaptureIndex</c> disposition, which already proves every
        /// final PNG and sidecar matches the expected content), the authority's
        /// lock liveness, the exact authoritative plan and its independent ID
        /// and hash value correlation, the trace manifest evidence, the exact
        /// publication path set and root layout, the derived commit mode, and
        /// the distinct temporary and final paths. It never scans a plan entry.
        /// </summary>
        private static bool TryCorrelate(
            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan,
            PngJsonCapturePublicationArtifactRecoveryDecision decision,
            out CaptureRunPublicationPathSet publicationPaths,
            out CaptureRunCaptureIndexCommitMode mode)
        {
            publicationPaths = null;
            mode = CaptureRunCaptureIndexCommitMode.None;

            try
            {
                if (actionPlan == null || decision == null)
                {
                    return false;
                }

                if (decision.Disposition != CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex)
                {
                    return false;
                }

                PngJsonCapturePublicationArtifactInspectionAuthority authority = decision.Authority;
                if (authority == null || !authority.IsLockLivenessIntact)
                {
                    return false;
                }

                PngJsonCapturePublicationPlan authoritativePlan = authority.AuthoritativePlan;
                if (authoritativePlan == null
                    || !ReferenceEquals(authoritativePlan, actionPlan.AuthoritativePlan)
                    || !ReferenceEquals(authoritativePlan, decision.AuthoritativePlan))
                {
                    return false;
                }

                // O(1) independent value correlation: the plan's own ID and
                // hash fields must match the authority's forwarded values, so
                // a forged run ID, initialization ID, or manifest hash is
                // rejected without an entry scan.
                if (authoritativePlan.TestRunId != authority.TestRunId
                    || !string.Equals(authoritativePlan.RunInitializationId, authority.RunInitializationId, StringComparison.Ordinal)
                    || !string.Equals(authoritativePlan.RunManifestContentSha256, authority.RunManifestContentSha256, StringComparison.Ordinal))
                {
                    return false;
                }

                if (decision.Snapshot.TraceManifestStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected)
                {
                    return false;
                }

                publicationPaths = authority.PublicationPaths;
                if (publicationPaths == null || !publicationPaths.IsValid)
                {
                    return false;
                }

                CaptureRunRootLayout rootLayout = authority.RootLayout;
                if (rootLayout == null || !ReferenceEquals(publicationPaths.RootLayout, rootLayout))
                {
                    return false;
                }

                if (!TryDeriveMode(authority, authoritativePlan, out mode))
                {
                    return false;
                }

                if (string.Equals(publicationPaths.CaptureIndexTemporaryPath, publicationPaths.CaptureIndexPath, StringComparison.Ordinal))
                {
                    return false;
                }

                return true;
            }
            catch (Exception)
            {
                publicationPaths = null;
                mode = CaptureRunCaptureIndexCommitMode.None;
                return false;
            }
        }

        /// <summary>
        /// O(1), exception-safe commit-mode derivation: a Fresh frozen run
        /// always creates a new temporary index without consulting any recovery
        /// observation; a Recovery decision derives the mode from the observed
        /// <c>capture.index.tmp</c> status and requires the final
        /// <c>capture.index</c> to be absent, rejecting a canonical temporary
        /// whose plan does not exactly match the authoritative plan.
        /// </summary>
        private static bool TryDeriveMode(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            PngJsonCapturePublicationPlan authoritativePlan,
            out CaptureRunCaptureIndexCommitMode mode)
        {
            mode = CaptureRunCaptureIndexCommitMode.None;

            if (authority.IsFresh)
            {
                mode = CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit;
                return true;
            }

            if (!authority.IsRecovery)
            {
                return false;
            }

            CaptureRunPublicationRecoveryDecision recoveryDecision = authority.RecoveryDecision;
            if (recoveryDecision == null)
            {
                return false;
            }

            CaptureRunPublicationRecoveryInspectionSnapshot publicationSnapshot = recoveryDecision.Snapshot;
            if (publicationSnapshot == null)
            {
                return false;
            }

            CaptureRunPublicationDocumentObservation captureIndex = publicationSnapshot.CaptureIndex;
            if (captureIndex == null || !captureIndex.IsValid
                || captureIndex.Status != CaptureRunPublicationDocumentObservationStatus.Absent)
            {
                return false;
            }

            CaptureRunPublicationDocumentObservation captureIndexTemporary = publicationSnapshot.CaptureIndexTemporary;
            if (captureIndexTemporary == null || !captureIndexTemporary.IsValid)
            {
                return false;
            }

            if (!TryDeriveModeFromStatus(captureIndexTemporary.Status, out mode))
            {
                return false;
            }

            if (mode == CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit
                && !CaptureRunPublicationRecoveryClassifier.PlansEqual(captureIndexTemporary.Plan, authoritativePlan))
            {
                return false;
            }

            return true;
        }

        private static bool TryDeriveModeFromStatus(
            CaptureRunPublicationDocumentObservationStatus status,
            out CaptureRunCaptureIndexCommitMode mode)
        {
            switch (status)
            {
                case CaptureRunPublicationDocumentObservationStatus.Absent:
                    mode = CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit;
                    return true;

                case CaptureRunPublicationDocumentObservationStatus.Canonical:
                    mode = CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit;
                    return true;

                case CaptureRunPublicationDocumentObservationStatus.Invalid:
                    mode = CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit;
                    return true;

                default:
                    mode = CaptureRunCaptureIndexCommitMode.None;
                    return false;
            }
        }

        private static bool BytesEqual(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }
    }
}
