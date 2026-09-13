using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, ordered recovery action plan produced for one decision. The
    /// step array is allocated once at construction, never exposed, and never
    /// mutated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor is the sole owner of the step array: it works out the
    /// exact step count from the decision with the rules held below,
    /// allocates the array exactly once, and fills it directly. The array is never exposed, so a plan cannot be
    /// mutated after construction. <see cref="IsValid"/> recomputes the same
    /// sequence from the held values without throwing.
    /// </para>
    /// <para>
    /// This type owns and disposes nothing and performs no filesystem work. It
    /// is not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryActionPlan
    {
        private readonly CaptureRunInitializationRecoveryDecision _decision;
        private readonly CaptureRunInitializationRecoveryStep[] _steps;

        internal CaptureRunInitializationRecoveryActionPlan(
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

            int count = ComputeStepCount(decision);
            CaptureRunInitializationRecoveryStep[] steps = new CaptureRunInitializationRecoveryStep[count];
            for (int i = 0; i < count; i++)
            {
                steps[i] = StepAt(decision, i);
            }

            _decision = decision;
            _steps = steps;
        }

        internal CaptureRunInitializationRecoveryDecision Decision => _decision;

        internal int Count => _steps.Length;

        internal CaptureRunMarkerBinding ExpectedBinding => _decision.ExpectedBinding;

        internal CaptureRunRootLayout RootLayout => _decision.RootLayout;

        internal long TestRunId => _decision.TestRunId;

        internal CaptureRunInitializationRecoveryStep GetStep(int index)
        {
            if (index < 0 || index >= _steps.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Step index out of range.");
            }

            return _steps[index];
        }

        internal bool IsValid
        {
            get
            {
                if (_decision == null || !_decision.IsValid || _steps == null)
                {
                    return false;
                }

                int expectedCount = ComputeStepCount(_decision);
                if (_steps.Length != expectedCount)
                {
                    return false;
                }

                for (int i = 0; i < _steps.Length; i++)
                {
                    CaptureRunInitializationRecoveryStep actual = _steps[i];
                    if (actual == null || !actual.Matches(StepAt(_decision, i)))
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>
        /// How many steps one decision's disposition calls for. Observed
        /// temporary-marker deletions come first, then the sequence the
        /// disposition itself requires.
        /// </summary>
        private static int ComputeStepCount(CaptureRunInitializationRecoveryDecision decision)
        {
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot = decision.Snapshot;
            CaptureRunInitializationRootObservation staging = snapshot.Staging;
            CaptureRunInitializationRootObservation final = snapshot.Final;

            int count = HasTemporaryDeletions(decision.Disposition)
                ? TmpDeletionCount(staging, final)
                : 0;

            return count + TailCount(decision, staging, final);
        }

        private static CaptureRunInitializationRecoveryStep StepAt(
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
