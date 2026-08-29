using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Pure, stateless builder that converts one recovery decision into the
    /// ordered, fixed action plan its disposition requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Step order is fixed: observed temporary-marker deletions first, then the
    /// disposition-specific sequence. Canonical markers are never overwritten,
    /// and collision dispositions produce a single non-mutating routing step.
    /// </para>
    /// <para>
    /// The builder holds no fields and performs no filesystem work, no lock
    /// acquisition, no ID issuance, no marker serialization, and no
    /// coordinator invocation.
    /// </para>
    /// </remarks>
    internal static class CaptureRunInitializationRecoveryActionPlanBuilder
    {
        internal static CaptureRunInitializationRecoveryActionPlan Build(
            CaptureRunInitializationRecoveryDecision decision)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            if (!decision.IsValid)
            {
                throw new ArgumentException("Decision must be valid.", nameof(decision));
            }

            return new CaptureRunInitializationRecoveryActionPlan(decision);
        }

        internal static int ComputeStepCount(CaptureRunInitializationRecoveryDecision decision)
        {
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = decision.Snapshot;
            CaptureRunInitializationRootObservation staging = snapshot.Staging;
            CaptureRunInitializationRootObservation final = snapshot.Final;

            int count = HasTemporaryDeletions(decision.Disposition)
                ? TmpDeletionCount(staging, final)
                : 0;

            return count + TailCount(decision, staging, final);
        }

        internal static CaptureRunInitializationRecoveryStep StepAt(
            CaptureRunInitializationRecoveryDecision decision,
            int index)
        {
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = decision.Snapshot;
            CaptureRunInitializationRootObservation staging = snapshot.Staging;
            CaptureRunInitializationRootObservation final = snapshot.Final;

            if (HasTemporaryDeletions(decision.Disposition))
            {
                int tmpCount = TmpDeletionCount(staging, final);
                if (index < tmpCount)
                {
                    return TmpDeletionStepAt(staging, final, index);
                }

                index -= tmpCount;
            }

            return TailStepAt(decision, staging, final, index);
        }

        private static bool HasTemporaryDeletions(CaptureRunInitializationRecoveryDisposition disposition)
        {
            return disposition == CaptureRunInitializationRecoveryDisposition.CleanupTemporaryAndStartFresh
                || disposition == CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization
                || disposition == CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers;
        }

        private static int TmpDeletionCount(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final)
        {
            int count = 0;
            if (staging.HasInitializationTemporary) count++;
            if (staging.HasReadyTemporary) count++;
            if (final.HasInitializationTemporary) count++;
            if (final.HasReadyTemporary) count++;
            return count;
        }

        private static CaptureRunInitializationRecoveryStep TmpDeletionStepAt(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            int index)
        {
            if (staging.HasInitializationTemporary)
            {
                if (index == 0)
                {
                    return new CaptureRunInitializationRecoveryStep(
                        CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, CaptureRunRootRole.Staging, CaptureRunMarkerKind.Initialization);
                }

                index--;
            }

            if (staging.HasReadyTemporary)
            {
                if (index == 0)
                {
                    return new CaptureRunInitializationRecoveryStep(
                        CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, CaptureRunRootRole.Staging, CaptureRunMarkerKind.Ready);
                }

                index--;
            }

            if (final.HasInitializationTemporary)
            {
                if (index == 0)
                {
                    return new CaptureRunInitializationRecoveryStep(
                        CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, CaptureRunRootRole.Final, CaptureRunMarkerKind.Initialization);
                }

                index--;
            }

            return new CaptureRunInitializationRecoveryStep(
                CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary, CaptureRunRootRole.Final, CaptureRunMarkerKind.Ready);
        }

        private static int TailCount(
            CaptureRunInitializationRecoveryDecision decision,
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final)
        {
            switch (decision.Disposition)
            {
                case CaptureRunInitializationRecoveryDisposition.StartFresh:
                case CaptureRunInitializationRecoveryDisposition.AlreadyInitialized:
                case CaptureRunInitializationRecoveryDisposition.RequiresPublicationRecovery:
                case CaptureRunInitializationRecoveryDisposition.RunRootCollision:
                    return 1;

                case CaptureRunInitializationRecoveryDisposition.CleanupTemporaryAndStartFresh:
                    RequireCleanupInvariants(staging, final);
                    return (final.RootExists ? 1 : 0)
                        + (staging.RootExists ? 1 : 0)
                        + 1;

                case CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization:
                {
                    bool stagingHasInit = staging.InitializationStatus == CaptureRunMarkerObservationStatus.Canonical;
                    CaptureRunInitializationRootObservation peer = stagingHasInit ? final : staging;
                    return (peer.RootExists ? 1 : 0) + 4;
                }

                case CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers:
                    return (staging.ReadyStatus == CaptureRunMarkerObservationStatus.Absent ? 1 : 0)
                        + (final.ReadyStatus == CaptureRunMarkerObservationStatus.Absent ? 1 : 0);

                default:
                    throw new InvalidOperationException("Decision disposition must be defined.");
            }
        }

        private static CaptureRunInitializationRecoveryStep TailStepAt(
            CaptureRunInitializationRecoveryDecision decision,
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            int index)
        {
            switch (decision.Disposition)
            {
                case CaptureRunInitializationRecoveryDisposition.StartFresh:
                    return Routing(CaptureRunInitializationRecoveryAction.StartFreshInitialization);

                case CaptureRunInitializationRecoveryDisposition.CleanupTemporaryAndStartFresh:
                    if (final.RootExists)
                    {
                        if (index == 0)
                        {
                            return new CaptureRunInitializationRecoveryStep(
                                CaptureRunInitializationRecoveryAction.RemoveEmptyRoot, CaptureRunRootRole.Final, CaptureRunMarkerKind.None);
                        }

                        index--;
                    }

                    if (staging.RootExists)
                    {
                        if (index == 0)
                        {
                            return new CaptureRunInitializationRecoveryStep(
                                CaptureRunInitializationRecoveryAction.RemoveEmptyRoot, CaptureRunRootRole.Staging, CaptureRunMarkerKind.None);
                        }

                        index--;
                    }

                    return Routing(CaptureRunInitializationRecoveryAction.StartFreshInitialization);

                case CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization:
                {
                    bool stagingHasInit = staging.InitializationStatus == CaptureRunMarkerObservationStatus.Canonical;
                    CaptureRunRootRole peerRole = stagingHasInit ? CaptureRunRootRole.Final : CaptureRunRootRole.Staging;
                    CaptureRunInitializationRootObservation peer = stagingHasInit ? final : staging;

                    if (peer.RootExists)
                    {
                        if (index == 0)
                        {
                            return new CaptureRunInitializationRecoveryStep(
                                CaptureRunInitializationRecoveryAction.RemoveEmptyRoot, peerRole, CaptureRunMarkerKind.None);
                        }

                        index--;
                    }

                    if (index == 0)
                    {
                        return new CaptureRunInitializationRecoveryStep(
                            CaptureRunInitializationRecoveryAction.ProvisionRoot, peerRole, CaptureRunMarkerKind.None);
                    }

                    index--;

                    if (index == 0)
                    {
                        return new CaptureRunInitializationRecoveryStep(
                            CaptureRunInitializationRecoveryAction.WriteMarker, peerRole, CaptureRunMarkerKind.Initialization);
                    }

                    index--;

                    if (index == 0)
                    {
                        return new CaptureRunInitializationRecoveryStep(
                            CaptureRunInitializationRecoveryAction.WriteMarker, CaptureRunRootRole.Staging, CaptureRunMarkerKind.Ready);
                    }

                    return new CaptureRunInitializationRecoveryStep(
                        CaptureRunInitializationRecoveryAction.WriteMarker, CaptureRunRootRole.Final, CaptureRunMarkerKind.Ready);
                }

                case CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers:
                    if (staging.ReadyStatus == CaptureRunMarkerObservationStatus.Absent)
                    {
                        if (index == 0)
                        {
                            return new CaptureRunInitializationRecoveryStep(
                                CaptureRunInitializationRecoveryAction.WriteMarker, CaptureRunRootRole.Staging, CaptureRunMarkerKind.Ready);
                        }

                        index--;
                    }

                    return new CaptureRunInitializationRecoveryStep(
                        CaptureRunInitializationRecoveryAction.WriteMarker, CaptureRunRootRole.Final, CaptureRunMarkerKind.Ready);

                case CaptureRunInitializationRecoveryDisposition.AlreadyInitialized:
                    return Routing(CaptureRunInitializationRecoveryAction.InitializationReady);

                case CaptureRunInitializationRecoveryDisposition.RequiresPublicationRecovery:
                    return Routing(CaptureRunInitializationRecoveryAction.ContinuePublicationRecovery);

                case CaptureRunInitializationRecoveryDisposition.RunRootCollision:
                    return Routing(CaptureRunInitializationRecoveryAction.StopRunRootCollision);

                default:
                    throw new InvalidOperationException("Decision disposition must be defined.");
            }
        }

        private static void RequireCleanupInvariants(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final)
        {
            if (staging.HasNonMarkerEntries || final.HasNonMarkerEntries
                || staging.HasUnknownEntries || final.HasUnknownEntries
                || staging.InitializationStatus != CaptureRunMarkerObservationStatus.Absent
                || final.InitializationStatus != CaptureRunMarkerObservationStatus.Absent
                || staging.ReadyStatus != CaptureRunMarkerObservationStatus.Absent
                || final.ReadyStatus != CaptureRunMarkerObservationStatus.Absent)
            {
                throw new InvalidOperationException("Cleanup disposition must observe no markers, non-marker, or unknown entries.");
            }
        }

        private static CaptureRunInitializationRecoveryStep Routing(CaptureRunInitializationRecoveryAction action)
        {
            return new CaptureRunInitializationRecoveryStep(action, CaptureRunRootRole.None, CaptureRunMarkerKind.None);
        }
    }
}
