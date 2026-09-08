using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning NVENC Run publication plan commit operation: the
    /// exact Run coordinator that issued it, the exact Trace freeze receipt,
    /// the exact chunk finalization result, and the exact publication plan,
    /// fixed together for the future non-owning Publication Service. The
    /// artifact descriptor, frame relation, session issue, root layout, and
    /// run identity are forwarded from the retained graph and are never
    /// duplicated as fields.
    /// </summary>
    /// <remarks>
    /// This type owns no token, nonce, ownership bundle, registry, lease, or
    /// byte array, performs no file, hash, thread, or task operation, and is
    /// not an <see cref="IDisposable"/>. The pre-commit and final basenames
    /// are fixed constants; the actual file write and final-name placement
    /// are performed by the later Publication Service, never by this
    /// operation.
    /// </remarks>
    internal sealed class NvencRunPublicationPlanCommitOperation
    {
        internal const string PreCommitBasename = "publication.plan.nvenc-precommit.tmp";

        internal const string FinalBasename = "publication.plan";

        private readonly NvencCaptureRunCoordinator _coordinator;
        private readonly NvencTraceFreezeReceipt _traceFreezeReceipt;
        private readonly NvencChunkFinalizationResult _finalizationResult;
        private readonly CapturePublicationPlan _plan;

        internal NvencRunPublicationPlanCommitOperation(
            NvencCaptureRunCoordinator coordinator,
            NvencTraceFreezeReceipt traceFreezeReceipt,
            NvencChunkFinalizationResult finalizationResult,
            CapturePublicationPlan plan)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _traceFreezeReceipt = traceFreezeReceipt ?? throw new ArgumentNullException(nameof(traceFreezeReceipt));
            _finalizationResult = finalizationResult ?? throw new ArgumentNullException(nameof(finalizationResult));
            _plan = plan ?? throw new ArgumentNullException(nameof(plan));

            if (!GraphCorrelationsHold())
            {
                throw new ArgumentException(
                    "The publication plan commit operation is not fully correlated.", nameof(coordinator));
            }
        }

        internal CapturePublicationPlan Plan => _plan;

        internal NvencChunkFinalizationResult FinalizationResult => _finalizationResult;

        internal NvencTraceFreezeReceipt TraceFreezeReceipt => _traceFreezeReceipt;

        internal CaptureRunRootLayout RootLayout =>
            _traceFreezeReceipt != null
            && _traceFreezeReceipt.SessionIssue != null
            && _traceFreezeReceipt.SessionIssue.Session != null
                ? _traceFreezeReceipt.SessionIssue.Session.RootLayout
                : null;

        internal long TestRunId => _plan != null ? _plan.TestRunId : 0L;

        internal string RunInitializationId => _plan != null ? _plan.RunInitializationId : null;

        internal string RunManifestContentHash => _plan != null ? _plan.RunManifestContentHash : null;

        internal bool IsValid =>
            GraphCorrelationsHold()
            && _coordinator.IsRetainedPublicationPlanCommitOperation(this);

        internal bool IsIssuedFor(NvencCaptureRunCoordinator coordinator)
        {
            return coordinator != null
                && ReferenceEquals(_coordinator, coordinator)
                && IsValid;
        }

        private bool GraphCorrelationsHold()
        {
            try
            {
                if (_coordinator == null || _traceFreezeReceipt == null
                    || _finalizationResult == null || _plan == null)
                {
                    return false;
                }

                if (!_plan.IsValid || !_finalizationResult.IsValid)
                {
                    return false;
                }

                CaptureArtifactDescriptor descriptor = _finalizationResult.Descriptor;
                if (descriptor == null || !descriptor.IsValid
                    || descriptor.ArtifactKind != CaptureArtifactKind.FrameSequence)
                {
                    return false;
                }

                CaptureArtifactFrameRelation relation = _finalizationResult.FrameRelation;
                if (relation == null || !relation.IsValid
                    || relation.Count < 1 || relation.Count > NvencBringUpProfileV1.CadenceTickCount)
                {
                    return false;
                }

                // The plan must be the exact 1-artifact, full-frame-relation
                // reduction of the finalization result.
                if (_plan.ArtifactCount != 1
                    || !ReferenceEquals(_plan.GetArtifact(0), descriptor))
                {
                    return false;
                }

                if (_plan.CaptureFrameEvidenceCount != relation.Count)
                {
                    return false;
                }

                for (int i = 0; i < relation.Count; i++)
                {
                    CaptureFrameEvidenceEntry entry = _plan.GetCaptureFrameEvidence(i);
                    if (entry == null
                        || entry.CaptureFrameId != relation.GetCaptureFrameId(i)
                        || entry.ArtifactCount != 1
                        || !string.Equals(entry.GetArtifactId(0), descriptor.ArtifactId, StringComparison.Ordinal))
                    {
                        return false;
                    }
                }

                // The Trace freeze receipt must still be valid and exact.
                if (!_traceFreezeReceipt.IsValid
                    || !_traceFreezeReceipt.IsIssuedFor(
                        _traceFreezeReceipt.IssuedBy,
                        _traceFreezeReceipt.Context,
                        _traceFreezeReceipt.SessionIssue))
                {
                    return false;
                }

                // The receipt's Run chunk context must be the exact context the
                // finalization result's sink belongs to, and the session issue /
                // Ownership Lease must still be live.
                if (_traceFreezeReceipt.Context == null
                    || _finalizationResult.Sink == null
                    || !ReferenceEquals(_traceFreezeReceipt.Context.Sink, _finalizationResult.Sink))
                {
                    return false;
                }

                if (_traceFreezeReceipt.SessionIssue == null
                    || !_traceFreezeReceipt.SessionIssue.IsValid)
                {
                    return false;
                }

                // Coordinator-side re-verification: disposition Finalized,
                // context Finalized, slot Registered, and the exact entry
                // result, descriptor, and relation matching this operation.
                return _coordinator.IsPublicationPlanCommitIssued(
                    _traceFreezeReceipt, _finalizationResult, _plan);
            }
            catch (Exception ex) when (ex is ArgumentException
                || ex is ArgumentOutOfRangeException
                || ex is InvalidOperationException)
            {
                return false;
            }
        }
    }
}
