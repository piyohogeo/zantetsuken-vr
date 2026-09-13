using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free Capture Index commit operation: the action
    /// plan, its single commit step, the publication path set, the derived
    /// commit mode, and the canonical byte sequence that must become
    /// <c>capture.index</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The canonical bytes are exactly the output of
    /// <see cref="PngJsonCapturePublicationPlanCodec.SerializeCanonical"/> for the
    /// authoritative plan. The constructor serializes them itself, once, after
    /// every other check has passed, and keeps that single array without
    /// copying it. The array is never handed to a caller: only
    /// <see cref="GetCanonicalBytes"/> returns a defensive copy.
    /// </para>
    /// <para>
    /// <see cref="Mode"/> is derived uniquely from the observed
    /// <c>capture.index.tmp</c> status. <see cref="IsValid"/> recomputes every
    /// correlation without throwing, including re-serializing the plan and
    /// comparing it byte-for-byte with the held bytes.
    /// </para>
    /// <para>
    /// This type performs no filesystem work, no hash recomputation, and no ID
    /// generation, owns and disposes nothing besides the held byte array, and
    /// is not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunCaptureIndexCommitOperation
    {
        private readonly CaptureRunPublicationArtifactRecoveryActionPlan _actionPlan;
        private readonly int _stepIndex;
        private readonly CaptureRunPublicationPathSet _publicationPaths;
        private readonly CaptureRunCaptureIndexCommitMode _mode;
        private readonly byte[] _canonicalBytes;

        internal CaptureRunCaptureIndexCommitOperation(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan,
            int stepIndex)
            : this(actionPlan, ValidateAndIssueToken(actionPlan), stepIndex)
        {
        }

        internal CaptureRunCaptureIndexCommitOperation(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan,
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token,
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

            if (!token.IsIssuedFor(actionPlan))
            {
                throw new ArgumentException("Token must be issued for this action plan.", nameof(token));
            }

            if (stepIndex < 0 || stepIndex >= actionPlan.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(stepIndex), stepIndex, "Step index must be within the step count.");
            }

            CaptureRunPublicationArtifactRecoveryStep step = actionPlan.GetStep(stepIndex);
            if (step == null || !step.IsValid
                || step.Action != CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex
                || step.EntryIndex != -1
                || step.ArtifactKind != CaptureRunPublicationArtifactKind.None)
            {
                throw new ArgumentException("Step must be a valid commit capture index step.", nameof(stepIndex));
            }

            if (actionPlan.Count != 1)
            {
                throw new ArgumentException("Commit capture index plan must hold exactly one step.", nameof(actionPlan));
            }

            CaptureRunPublicationArtifactRecoveryDecision decision = actionPlan.Decision;
            if (decision == null || decision.Disposition != CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex)
            {
                throw new ArgumentException("Action plan must carry the commit capture index disposition.", nameof(actionPlan));
            }

            CaptureRunPublicationArtifactInspectionSnapshot snapshot = decision.Snapshot;
            if (snapshot == null)
            {
                throw new ArgumentException("Action plan decision must hold an artifact inspection snapshot.", nameof(actionPlan));
            }

            CaptureRunPublicationArtifactInspectionOperation operation = snapshot.Operation;
            if (operation == null)
            {
                throw new ArgumentException("Artifact inspection snapshot must hold an operation.", nameof(actionPlan));
            }

            CaptureRunLockIdentityEvidence lockIdentityEvidence = operation.LockIdentityEvidence;
            if (lockIdentityEvidence == null || !lockIdentityEvidence.IsValid)
            {
                throw new ArgumentException("Artifact inspection operation must hold valid lock identity evidence.", nameof(actionPlan));
            }

            CaptureRunRootLayout rootLayout = operation.RootLayout;
            if (rootLayout == null)
            {
                throw new ArgumentException("Artifact inspection operation must hold a root layout.", nameof(actionPlan));
            }

            CaptureRunPublicationRecoveryDecision publicationDecision = decision.PublicationDecision;
            if (publicationDecision == null
                || publicationDecision.Disposition != CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative)
            {
                throw new ArgumentException("Publication decision must be authoritative for the publication plan.", nameof(actionPlan));
            }

            PngJsonCapturePublicationPlan authoritativePlan = publicationDecision.AuthoritativePlan;
            if (authoritativePlan == null || !authoritativePlan.IsValid)
            {
                throw new ArgumentException("Publication decision must hold a valid authoritative plan.", nameof(actionPlan));
            }

            if (snapshot.TraceManifestStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected)
            {
                throw new ArgumentException("Trace manifest evidence must match the expected manifest.", nameof(actionPlan));
            }

            for (int i = 0; i < snapshot.Count; i++)
            {
                CaptureRunPublicationArtifactEntryObservation observation = snapshot.GetEntry(i);
                if (observation == null
                    || observation.FinalPngStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected
                    || observation.FinalSidecarStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected)
                {
                    throw new ArgumentException("Every final artifact must match the expected content.", nameof(actionPlan));
                }
            }

            CaptureRunPublicationRecoveryInspectionSnapshot publicationSnapshot = publicationDecision.Snapshot;
            if (publicationSnapshot == null)
            {
                throw new ArgumentException("Publication decision must hold a recovery inspection snapshot.", nameof(actionPlan));
            }

            CaptureRunPublicationDocumentObservation captureIndex = publicationSnapshot.CaptureIndex;
            if (captureIndex == null || !captureIndex.IsValid
                || captureIndex.Status != CaptureRunPublicationDocumentObservationStatus.Absent)
            {
                throw new ArgumentException("Committed capture index must be absent.", nameof(actionPlan));
            }

            CaptureRunPublicationDocumentObservation captureIndexTemporary = publicationSnapshot.CaptureIndexTemporary;
            if (captureIndexTemporary == null || !captureIndexTemporary.IsValid)
            {
                throw new ArgumentException("Capture index temporary observation must be valid.", nameof(actionPlan));
            }

            CaptureRunCaptureIndexCommitMode mode = DeriveMode(captureIndexTemporary);

            if (mode == CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit
                && (captureIndexTemporary.Status != CaptureRunPublicationDocumentObservationStatus.Canonical
                    || !CaptureRunPublicationRecoveryClassifier.PlansEqual(captureIndexTemporary.Plan, authoritativePlan)))
            {
                throw new ArgumentException("Canonical capture index temporary plan must exactly match the authoritative plan.", nameof(actionPlan));
            }

            CaptureRunPublicationRecoveryInspectionOperation publicationOperation = publicationSnapshot.Operation;
            if (publicationOperation == null)
            {
                throw new ArgumentException("Publication recovery snapshot must hold an operation.", nameof(actionPlan));
            }

            CaptureRunPublicationPathSet publicationPaths = publicationOperation.PublicationPaths;
            if (publicationPaths == null || !publicationPaths.IsValid
                || !ReferenceEquals(publicationPaths.RootLayout, rootLayout)
                || !ReferenceEquals(publicationOperation.RootLayout, rootLayout))
            {
                throw new ArgumentException("Publication path set must be valid and share the root layout.", nameof(actionPlan));
            }

            if (string.Equals(publicationPaths.CaptureIndexTemporaryPath, publicationPaths.CaptureIndexPath, StringComparison.Ordinal))
            {
                throw new ArgumentException("Temporary and final capture index paths must differ.", nameof(actionPlan));
            }

            byte[] canonicalBytes =
                PngJsonCapturePublicationPlanCodec.SerializeCanonical(authoritativePlan);

            _actionPlan = actionPlan;
            _stepIndex = stepIndex;
            _publicationPaths = publicationPaths;
            _mode = mode;
            _canonicalBytes = canonicalBytes;
        }

        private static CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken ValidateAndIssueToken(
            CaptureRunPublicationArtifactRecoveryActionPlan actionPlan)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            try
            {
                return actionPlan.AcquireValidationToken();
            }
            catch (InvalidOperationException ex)
            {
                throw new ArgumentException("Action plan must be valid.", nameof(actionPlan), ex);
            }
        }

        internal CaptureRunPublicationArtifactRecoveryActionPlan ActionPlan => _actionPlan;

        internal int StepIndex => _stepIndex;

        internal CaptureRunPublicationArtifactRecoveryStep Step => _actionPlan.GetStep(_stepIndex);

        internal CaptureRunPublicationArtifactRecoveryDecision Decision => _actionPlan.Decision;

        internal CaptureRunPublicationRecoveryDecision PublicationDecision => _actionPlan.Decision.PublicationDecision;

        internal PngJsonCapturePublicationPlan AuthoritativePlan => _actionPlan.AuthoritativePlan;

        internal CaptureRunCaptureIndexCommitMode Mode => _mode;

        internal string TemporaryPath => _publicationPaths.CaptureIndexTemporaryPath;

        internal string FinalPath => _publicationPaths.CaptureIndexPath;

        internal long ByteCount => _canonicalBytes.Length;

        internal CaptureRunRootLayout RootLayout => _actionPlan.RootLayout;

        internal long TestRunId => _actionPlan.TestRunId;

        internal string RunInitializationId => _actionPlan.RunInitializationId;

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

            if (!TryDeriveMode(captureIndexTemporary.Status, out CaptureRunCaptureIndexCommitMode mode))
            {
                throw new ArgumentException("Capture index temporary status must be absent, canonical, or invalid.", nameof(captureIndexTemporary));
            }

            return mode;
        }

        internal bool IsValid
        {
            get
            {
                if (_actionPlan == null)
                {
                    return false;
                }

                CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token;
                try
                {
                    token = _actionPlan.AcquireValidationToken();
                }
                catch (InvalidOperationException)
                {
                    return false;
                }

                return IsValidWithToken(token);
            }
        }

        /// <summary>
        /// Token-gated full validity: delegates to the index-local correlation
        /// check and then re-serializes the authoritative plan's canonical bytes
        /// for byte-content comparison. It does not re-validate the plan or
        /// re-scan any entry/observation graph; the caller must supply a live
        /// validation token acquired from the action plan, which guarantees the
        /// plan was fully validated exactly once upstream.
        /// </summary>
        internal bool IsValidWithToken(
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token)
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
            catch (InvalidOperationException)
            {
                return false;
            }

            return BytesEqual(_canonicalBytes, expected);
        }

        /// <summary>
        /// Index-local validity for an already-validated action plan: re-verifies
        /// only this step's correlation in O(1) without re-validating the whole
        /// plan, re-scanning entries, or re-serializing the canonical bytes. The
        /// token must be issued for this operation's action plan and its lease
        /// must still be live. Full byte-content verification remains the
        /// responsibility of <see cref="IsValid"/>.
        /// </summary>
        internal bool IsValidIndexLocal(
            CaptureRunPublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            if (_actionPlan == null || token == null)
            {
                return false;
            }

            if (!token.IsIssuedFor(_actionPlan))
            {
                return false;
            }

            if (_stepIndex < 0 || _stepIndex >= _actionPlan.Count)
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryStep step = _actionPlan.GetStep(_stepIndex);
            if (step == null || !step.IsValid
                || step.Action != CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex
                || step.EntryIndex != -1
                || step.ArtifactKind != CaptureRunPublicationArtifactKind.None)
            {
                return false;
            }

            if (_actionPlan.Count != 1)
            {
                return false;
            }

            CaptureRunPublicationArtifactRecoveryDecision decision = _actionPlan.Decision;
            if (decision == null || decision.Disposition != CaptureRunPublicationArtifactRecoveryDisposition.CommitCaptureIndex)
            {
                return false;
            }

            CaptureRunPublicationArtifactInspectionSnapshot snapshot = decision.Snapshot;
            if (snapshot == null)
            {
                return false;
            }

            CaptureRunPublicationArtifactInspectionOperation operation = snapshot.Operation;
            if (operation == null)
            {
                return false;
            }

            CaptureRunLockIdentityEvidence lockIdentityEvidence = operation.LockIdentityEvidence;
            if (lockIdentityEvidence == null || !lockIdentityEvidence.IsValid)
            {
                return false;
            }

            CaptureRunRootLayout rootLayout = operation.RootLayout;
            if (rootLayout == null)
            {
                return false;
            }

            CaptureRunPublicationRecoveryDecision publicationDecision = decision.PublicationDecision;
            if (publicationDecision == null
                || publicationDecision.Disposition != CaptureRunPublicationRecoveryDisposition.PublicationPlanAuthoritative)
            {
                return false;
            }

            PngJsonCapturePublicationPlan authoritativePlan = publicationDecision.AuthoritativePlan;
            if (authoritativePlan == null)
            {
                return false;
            }

            if (snapshot.TraceManifestStatus != CaptureRunPublicationEvidenceStatus.MatchesExpected)
            {
                return false;
            }

            CaptureRunPublicationRecoveryInspectionSnapshot publicationSnapshot = publicationDecision.Snapshot;
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
            if (captureIndexTemporary == null
                || !captureIndexTemporary.IsValid
                || !TryDeriveMode(captureIndexTemporary.Status, out CaptureRunCaptureIndexCommitMode expectedMode)
                || _mode != expectedMode)
            {
                return false;
            }

            CaptureRunPublicationRecoveryInspectionOperation publicationOperation = publicationSnapshot.Operation;
            if (publicationOperation == null)
            {
                return false;
            }

            if (_publicationPaths == null || !_publicationPaths.IsValid
                || !ReferenceEquals(_publicationPaths, publicationOperation.PublicationPaths)
                || !ReferenceEquals(_publicationPaths.RootLayout, rootLayout)
                || !ReferenceEquals(publicationOperation.RootLayout, rootLayout))
            {
                return false;
            }

            if (string.Equals(_publicationPaths.CaptureIndexTemporaryPath, _publicationPaths.CaptureIndexPath, StringComparison.Ordinal))
            {
                return false;
            }

            if (_canonicalBytes == null || _canonicalBytes.Length == 0)
            {
                return false;
            }

            return true;
        }

        private static bool TryDeriveMode(
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
