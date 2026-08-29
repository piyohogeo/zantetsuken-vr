using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Pure, stateless classifier that maps one observed Capture Run snapshot to
    /// a single recovery disposition. It never touches the filesystem, the
    /// lock, or any marker document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Classification order is fixed: priority collision signals are decided
    /// first, then canonical initialization markers are verified against the
    /// root layout and against the expected binding built through the existing
    /// <see cref="CaptureRunMarkerBindingFactory"/>, and only then is a
    /// disposition selected. A collision disposition never mutates anything;
    /// the classifier never creates, deletes, renames, or repairs a root,
    /// temporary entry, marker, or payload, never acquires or releases a lock,
    /// and never re-issues an initialization ID.
    /// </para>
    /// <para>
    /// The classifier holds no fields and performs no retry, fallback, marker
    /// correction, or backend call. String comparisons are ordinal. It is the
    /// only source of <see cref="CaptureRunInitializationRecoveryDisposition"/>
    /// values.
    /// </para>
    /// </remarks>
    internal static class CaptureRunInitializationRecoveryClassifier
    {
        internal static CaptureRunInitializationRecoveryDecision Classify(
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot)
        {
            if (snapshot == null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (!snapshot.IsValid)
            {
                throw new ArgumentException("Snapshot must be valid.", nameof(snapshot));
            }

            CaptureRunInitializationRecoveryDisposition disposition = Determine(snapshot, out CaptureRunMarkerBinding expectedBinding);
            return new CaptureRunInitializationRecoveryDecision(snapshot, disposition, expectedBinding);
        }

        /// <summary>
        /// Recomputes the single disposition and expected binding implied by a
        /// valid snapshot without constructing a decision. Requires a valid,
        /// non-null snapshot; callers validate first.
        /// </summary>
        internal static CaptureRunInitializationRecoveryDisposition Determine(
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot,
            out CaptureRunMarkerBinding expectedBinding)
        {
            CaptureRunInitializationRootObservation staging = snapshot.Staging;
            CaptureRunInitializationRootObservation final = snapshot.Final;

            if (HasPriorityCollisionSignal(staging) || HasPriorityCollisionSignal(final))
            {
                expectedBinding = null;
                return CaptureRunInitializationRecoveryDisposition.RunRootCollision;
            }

            bool stagingInit = staging.InitializationStatus == CaptureRunMarkerObservationStatus.Canonical;
            bool finalInit = final.InitializationStatus == CaptureRunMarkerObservationStatus.Canonical;
            bool stagingReady = staging.ReadyStatus == CaptureRunMarkerObservationStatus.Canonical;
            bool finalReady = final.ReadyStatus == CaptureRunMarkerObservationStatus.Canonical;

            if (!stagingInit && !finalInit)
            {
                return ClassifyWithoutInitialization(staging, final, stagingReady, finalReady, out expectedBinding);
            }

            CaptureRunRootLayout rootLayout = snapshot.Operation.RootLayout;
            CaptureRunInitializationMarker sourceInit = stagingInit ? staging.InitializationMarker : final.InitializationMarker;
            CaptureRunRootRole sourceRole = stagingInit ? CaptureRunRootRole.Staging : CaptureRunRootRole.Final;

            if (!IsCanonicalInitializationMarker(sourceInit) || !MatchesRootLayout(sourceInit, sourceRole, rootLayout))
            {
                expectedBinding = null;
                return CaptureRunInitializationRecoveryDisposition.RunRootCollision;
            }

            CaptureRunMarkerBinding expected = CaptureRunMarkerBindingFactory.Create(
                rootLayout.TestRunId,
                sourceInit.RunInitializationId,
                rootLayout.StagingRunRootSha256,
                rootLayout.FinalRunRootSha256);

            if (stagingInit && !InitializationMarkersEqual(staging.InitializationMarker, expected.StagingInitialization))
            {
                expectedBinding = null;
                return CaptureRunInitializationRecoveryDisposition.RunRootCollision;
            }

            if (finalInit && !InitializationMarkersEqual(final.InitializationMarker, expected.FinalInitialization))
            {
                expectedBinding = null;
                return CaptureRunInitializationRecoveryDisposition.RunRootCollision;
            }

            if (stagingReady && !ReadyMarkersEqual(staging.ReadyMarker, expected.StagingReady))
            {
                expectedBinding = null;
                return CaptureRunInitializationRecoveryDisposition.RunRootCollision;
            }

            if (finalReady && !ReadyMarkersEqual(final.ReadyMarker, expected.FinalReady))
            {
                expectedBinding = null;
                return CaptureRunInitializationRecoveryDisposition.RunRootCollision;
            }

            if (stagingInit && finalInit)
            {
                return ClassifyBothInitialized(staging, final, stagingReady, finalReady, expected, out expectedBinding);
            }

            return ClassifyOneSidedInitialization(staging, final, stagingInit, stagingReady, finalReady, expected, out expectedBinding);
        }

        /// <summary>
        /// Exception-safe correlation predicate shared by the decision's
        /// constructor and <see cref="CaptureRunInitializationRecoveryDecision.IsValid"/>.
        /// Requires a valid, non-null snapshot; callers validate first.
        /// </summary>
        internal static bool IsCorrelated(
            CaptureRunInitializationRecoveryInspectionSnapshot snapshot,
            CaptureRunInitializationRecoveryDisposition disposition,
            CaptureRunMarkerBinding expectedBinding)
        {
            CaptureRunInitializationRecoveryDisposition computed = Determine(snapshot, out CaptureRunMarkerBinding computedBinding);

            if (disposition != computed)
            {
                return false;
            }

            if (computedBinding == null)
            {
                return expectedBinding == null;
            }

            return expectedBinding != null && BindingMatches(expectedBinding, computedBinding);
        }

        private static CaptureRunInitializationRecoveryDisposition ClassifyWithoutInitialization(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            bool stagingReady,
            bool finalReady,
            out CaptureRunMarkerBinding expectedBinding)
        {
            if (stagingReady || finalReady)
            {
                expectedBinding = null;
                return CaptureRunInitializationRecoveryDisposition.RunRootCollision;
            }

            if (staging.HasNonMarkerEntries || final.HasNonMarkerEntries)
            {
                expectedBinding = null;
                return CaptureRunInitializationRecoveryDisposition.RunRootCollision;
            }

            expectedBinding = null;

            if (!staging.RootExists && !final.RootExists)
            {
                return CaptureRunInitializationRecoveryDisposition.StartFresh;
            }

            return CaptureRunInitializationRecoveryDisposition.CleanupTemporaryAndStartFresh;
        }

        private static CaptureRunInitializationRecoveryDisposition ClassifyOneSidedInitialization(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            bool stagingInit,
            bool stagingReady,
            bool finalReady,
            CaptureRunMarkerBinding expected,
            out CaptureRunMarkerBinding expectedBinding)
        {
            CaptureRunInitializationRootObservation source = stagingInit ? staging : final;
            CaptureRunInitializationRootObservation peer = stagingInit ? final : staging;
            bool sourceReady = stagingInit ? stagingReady : finalReady;

            if (source.HasNonMarkerEntries || sourceReady)
            {
                expectedBinding = null;
                return CaptureRunInitializationRecoveryDisposition.RunRootCollision;
            }

            if (!IsAbsentEmptyOrTmpOnly(peer))
            {
                expectedBinding = null;
                return CaptureRunInitializationRecoveryDisposition.RunRootCollision;
            }

            expectedBinding = expected;
            return CaptureRunInitializationRecoveryDisposition.CompleteMissingPeerInitialization;
        }

        private static CaptureRunInitializationRecoveryDisposition ClassifyBothInitialized(
            CaptureRunInitializationRootObservation staging,
            CaptureRunInitializationRootObservation final,
            bool stagingReady,
            bool finalReady,
            CaptureRunMarkerBinding expected,
            out CaptureRunMarkerBinding expectedBinding)
        {
            int readyCount = (stagingReady ? 1 : 0) + (finalReady ? 1 : 0);
            bool hasNonMarker = staging.HasNonMarkerEntries || final.HasNonMarkerEntries;

            if (readyCount == 2)
            {
                if (!hasNonMarker)
                {
                    expectedBinding = expected;
                    return CaptureRunInitializationRecoveryDisposition.AlreadyInitialized;
                }

                expectedBinding = expected;
                return CaptureRunInitializationRecoveryDisposition.RequiresPublicationRecovery;
            }

            if (!hasNonMarker)
            {
                expectedBinding = expected;
                return CaptureRunInitializationRecoveryDisposition.CompleteReadyMarkers;
            }

            expectedBinding = null;
            return CaptureRunInitializationRecoveryDisposition.RunRootCollision;
        }

        internal static bool BindingMatches(CaptureRunMarkerBinding left, CaptureRunMarkerBinding right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            return InitializationMarkersEqual(left.StagingInitialization, right.StagingInitialization)
                && InitializationMarkersEqual(left.FinalInitialization, right.FinalInitialization)
                && ReadyMarkersEqual(left.StagingReady, right.StagingReady)
                && ReadyMarkersEqual(left.FinalReady, right.FinalReady);
        }

        private static bool HasPriorityCollisionSignal(CaptureRunInitializationRootObservation observation)
        {
            return observation.RootEntryLimitExceeded
                || observation.HasUnknownEntries
                || observation.InitializationStatus == CaptureRunMarkerObservationStatus.Invalid
                || observation.ReadyStatus == CaptureRunMarkerObservationStatus.Invalid;
        }

        private static bool IsAbsentEmptyOrTmpOnly(CaptureRunInitializationRootObservation observation)
        {
            if (!observation.RootExists)
            {
                return true;
            }

            return !observation.HasNonMarkerEntries
                && observation.InitializationStatus == CaptureRunMarkerObservationStatus.Absent
                && observation.ReadyStatus == CaptureRunMarkerObservationStatus.Absent;
        }

        private static bool MatchesRootLayout(
            CaptureRunInitializationMarker marker,
            CaptureRunRootRole expectedRole,
            CaptureRunRootLayout rootLayout)
        {
            return marker.TestRunId == rootLayout.TestRunId
                && marker.RootRole == expectedRole
                && string.Equals(marker.StagingRunRootSha256, rootLayout.StagingRunRootSha256, StringComparison.Ordinal)
                && string.Equals(marker.FinalRunRootSha256, rootLayout.FinalRunRootSha256, StringComparison.Ordinal);
        }

        private static bool InitializationMarkersEqual(
            CaptureRunInitializationMarker observed,
            CaptureRunInitializationMarker expected)
        {
            if (observed == null || expected == null)
            {
                return false;
            }

            if (!HasValidInitializationValues(observed) || !HasValidInitializationValues(expected))
            {
                return false;
            }

            return observed.SchemaVersion == expected.SchemaVersion
                && observed.TestRunId == expected.TestRunId
                && string.Equals(observed.RunInitializationId, expected.RunInitializationId, StringComparison.Ordinal)
                && observed.RootRole == expected.RootRole
                && string.Equals(observed.StagingRunRootSha256, expected.StagingRunRootSha256, StringComparison.Ordinal)
                && string.Equals(observed.FinalRunRootSha256, expected.FinalRunRootSha256, StringComparison.Ordinal);
        }

        private static bool ReadyMarkersEqual(
            CaptureRunReadyMarker observed,
            CaptureRunReadyMarker expected)
        {
            if (observed == null || expected == null)
            {
                return false;
            }

            if (!HasValidReadyValues(observed) || !HasValidReadyValues(expected))
            {
                return false;
            }

            return observed.SchemaVersion == expected.SchemaVersion
                && observed.TestRunId == expected.TestRunId
                && string.Equals(observed.RunInitializationId, expected.RunInitializationId, StringComparison.Ordinal)
                && string.Equals(observed.StagingInitSha256, expected.StagingInitSha256, StringComparison.Ordinal)
                && string.Equals(observed.FinalInitSha256, expected.FinalInitSha256, StringComparison.Ordinal);
        }

        private static bool HasValidInitializationValues(CaptureRunInitializationMarker marker)
        {
            return marker.TestRunId > 0
                && marker.RunInitializationId != null
                && (marker.RootRole == CaptureRunRootRole.Staging || marker.RootRole == CaptureRunRootRole.Final)
                && marker.StagingRunRootSha256 != null
                && marker.FinalRunRootSha256 != null;
        }

        private static bool HasValidReadyValues(CaptureRunReadyMarker marker)
        {
            return marker.TestRunId > 0
                && marker.RunInitializationId != null
                && marker.StagingInitSha256 != null
                && marker.FinalInitSha256 != null;
        }

        private static bool IsCanonicalInitializationMarker(CaptureRunInitializationMarker marker)
        {
            return marker != null
                && marker.TestRunId > 0
                && IsLowercaseHex(marker.RunInitializationId, 32)
                && (marker.RootRole == CaptureRunRootRole.Staging || marker.RootRole == CaptureRunRootRole.Final)
                && IsLowercaseHex(marker.StagingRunRootSha256, 64)
                && IsLowercaseHex(marker.FinalRunRootSha256, 64);
        }

        private static bool IsLowercaseHex(string value, int length)
        {
            if (value == null || value.Length != length)
            {
                return false;
            }

            for (int i = 0; i < length; i++)
            {
                char c = value[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }
    }
}
