using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 NVENC recovery terminal result: the
    /// exclusive sum type that carries exactly one already-issued release
    /// receipt.
    /// </summary>
    /// <remarks>
    /// Every receipt here is minted through the ordinary release path it
    /// belongs to, with a cleaner or committer that touches no file. No
    /// readonly field is rewritten, no contradictory graph is forged, and the
    /// receipts' own rules stay with the fixtures that own them.
    /// </remarks>
    public class NvencRunPublicationRecoveryTerminalResultContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        private int _runIds;

        [TearDown]
        public void TearDown()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                try
                {
                    owner.Dispose();
                }
                catch (AggregateException)
                {
                }
                catch (InvalidOperationException)
                {
                }
            }

            _owners.Clear();
        }

        [Test]
        public void Default_IsInvalidAndNamesNoPath()
        {
            NvencRunPublicationRecoveryTerminalResult terminal = default;

            Assert.That(terminal.IsValid, Is.False);
            Assert.That(terminal.IsCaptureCompleted, Is.False);
            Assert.That(terminal.IsIncompleteReleased, Is.False);
            Assert.That(terminal.IsStopped, Is.False);

            Assert.That(terminal.CaptureCompleteRelease, Is.Null);
            Assert.That(terminal.IncompleteRelease, Is.Null);
            Assert.That(terminal.StopRelease, Is.Null);

            Assert.That(terminal.PublicationRecoveryDecision, Is.Null);
            Assert.That(terminal.Snapshot, Is.Null);
            Assert.That(terminal.OpenOutcome, Is.Null);
            Assert.That(terminal.OwnershipLease, Is.Null);
            Assert.That(terminal.RootLayout, Is.Null);
            Assert.That(terminal.TestRunId, Is.Zero);
            Assert.That(terminal.RunInitializationId, Is.Null);
        }

        [Test]
        public void Factories_NullReceipt_Rejected()
        {
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => NvencRunPublicationRecoveryTerminalResult.CaptureCompleted(null))
                    .ParamName,
                Is.EqualTo("receipt"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => NvencRunPublicationRecoveryTerminalResult.IncompleteReleased(null))
                    .ParamName,
                Is.EqualTo("receipt"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => NvencRunPublicationRecoveryTerminalResult.Stopped(null)).ParamName,
                Is.EqualTo("receipt"));
        }

        [Test]
        public void CaptureCompleted_CarriesThatReceiptAlone()
        {
            Run run = MakeRun();
            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt receipt =
                run.ReleaseAfterCaptureComplete(cleanupFailed: false);

            NvencRunPublicationRecoveryTerminalResult terminal =
                NvencRunPublicationRecoveryTerminalResult.CaptureCompleted(receipt);

            Assert.That(terminal.IsValid, Is.True);
            Assert.That(terminal.IsCaptureCompleted, Is.True);
            Assert.That(terminal.IsIncompleteReleased, Is.False);
            Assert.That(terminal.IsStopped, Is.False);

            Assert.That(ReferenceEquals(terminal.CaptureCompleteRelease, receipt), Is.True);
            Assert.That(terminal.IncompleteRelease, Is.Null);
            Assert.That(terminal.StopRelease, Is.Null);

            AssertForwardsSharedGraph(terminal, run, receipt.CleanupOperation.RunInitializationId);
        }

        [Test]
        public void IncompleteReleased_CarriesThatReceiptAlone()
        {
            Run run = MakeRun();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt receipt =
                run.ReleaseAfterOrphanCleanup(cleanupFailed: false);

            NvencRunPublicationRecoveryTerminalResult terminal =
                NvencRunPublicationRecoveryTerminalResult.IncompleteReleased(receipt);

            Assert.That(terminal.IsValid, Is.True);
            Assert.That(terminal.IsIncompleteReleased, Is.True);
            Assert.That(terminal.IsCaptureCompleted, Is.False);
            Assert.That(terminal.IsStopped, Is.False);

            Assert.That(ReferenceEquals(terminal.IncompleteRelease, receipt), Is.True);
            Assert.That(terminal.CaptureCompleteRelease, Is.Null);
            Assert.That(terminal.StopRelease, Is.Null);

            AssertForwardsSharedGraph(terminal, run, receipt.RunInitializationId);
            Assert.That(ReferenceEquals(terminal.PublicationRecoveryDecision, receipt.Decision),
                Is.True);
        }

        [Test]
        public void Stopped_CarriesThatReceiptAlone()
        {
            foreach (bool deferred in new[] { false, true })
            {
                Run run = MakeRun();
                NvencRunPublicationRecoveryStopOwnershipReleaseReceipt receipt =
                    run.ReleaseAfterStopping(deferred);

                NvencRunPublicationRecoveryTerminalResult terminal =
                    NvencRunPublicationRecoveryTerminalResult.Stopped(receipt);

                string because = deferred ? "deferred" : "collision";
                Assert.That(terminal.IsValid, Is.True, because);
                Assert.That(terminal.IsStopped, Is.True, because);
                Assert.That(terminal.IsCaptureCompleted, Is.False, because);
                Assert.That(terminal.IsIncompleteReleased, Is.False, because);

                Assert.That(ReferenceEquals(terminal.StopRelease, receipt), Is.True, because);
                Assert.That(terminal.CaptureCompleteRelease, Is.Null, because);
                Assert.That(terminal.IncompleteRelease, Is.Null, because);

                // The stopping disposition stays where it already is.
                Assert.That(receipt.Disposition, Is.EqualTo(
                    deferred
                        ? NvencRunPublicationRecoveryDisposition.Deferred
                        : NvencRunPublicationRecoveryDisposition.PublicationRecoveryCollision));

                AssertForwardsSharedGraph(terminal, run, receipt.RunInitializationId);
                Assert.That(
                    ReferenceEquals(terminal.PublicationRecoveryDecision, receipt.Decision),
                    Is.True, because);
            }
        }

        [Test]
        public void EveryPath_ForwardsTheSameGraphItsReceiptCarries()
        {
            Run captureComplete = MakeRun();
            NvencRunPublicationRecoveryTerminalResult fromCaptureComplete =
                NvencRunPublicationRecoveryTerminalResult.CaptureCompleted(
                    captureComplete.ReleaseAfterCaptureComplete(cleanupFailed: false));

            Run incomplete = MakeRun();
            NvencRunPublicationRecoveryTerminalResult fromIncomplete =
                NvencRunPublicationRecoveryTerminalResult.IncompleteReleased(
                    incomplete.ReleaseAfterOrphanCleanup(cleanupFailed: false));

            Run stopped = MakeRun();
            NvencRunPublicationRecoveryTerminalResult fromStop =
                NvencRunPublicationRecoveryTerminalResult.Stopped(
                    stopped.ReleaseAfterStopping(deferred: false));

            // Each value's graph is its own Run's, taken from the receipt it
            // carries and not from anywhere else.
            AssertForwardsSharedGraph(fromCaptureComplete, captureComplete, InitId);
            AssertForwardsSharedGraph(fromIncomplete, incomplete, InitId);
            AssertForwardsSharedGraph(fromStop, stopped, InitId);

            Assert.That(
                ReferenceEquals(fromIncomplete.OwnershipLease, fromStop.OwnershipLease),
                Is.False, "three separate Runs.");
            Assert.That(
                ReferenceEquals(fromCaptureComplete.RootLayout, fromIncomplete.RootLayout),
                Is.False, "three separate Runs.");
        }

        [Test]
        public void AfterTheRelease_UpstreamAdmissionIsOverButTheTerminalResultIsValid()
        {
            Run run = MakeRun();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt receipt =
                run.ReleaseAfterOrphanCleanup(cleanupFailed: false);

            NvencRunPublicationRecoveryTerminalResult terminal =
                NvencRunPublicationRecoveryTerminalResult.IncompleteReleased(receipt);

            // The lease is gone, so everything whose validity is first-attempt
            // admission has lapsed.
            Assert.That(run.Owner.IsReleaseComplete, Is.True);
            Assert.That(receipt.Operation.IsValid, Is.False);
            Assert.That(receipt.CleanupResult.IsValid, Is.False);
            Assert.That(receipt.Decision.IsValid, Is.False);

            // The receipt attests the completed release, and the terminal
            // result carries it.
            Assert.That(receipt.IsValid, Is.True);
            Assert.That(terminal.IsValid, Is.True);
            Assert.That(terminal.IsIncompleteReleased, Is.True);

            // A released lease on its own is no terminal result at all.
            Assert.That(default(NvencRunPublicationRecoveryTerminalResult).IsValid, Is.False);
        }

        [Test]
        public void AfterAFailedCleanup_TheReleaseStillMakesAValidTerminalResult()
        {
            Run incomplete = MakeRun();
            NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt incompleteReceipt =
                incomplete.ReleaseAfterOrphanCleanup(cleanupFailed: true);

            NvencRunPublicationRecoveryTerminalResult fromIncomplete =
                NvencRunPublicationRecoveryTerminalResult.IncompleteReleased(incompleteReceipt);

            Assert.That(incompleteReceipt.CleanupStatus,
                Is.EqualTo(NvencRunPublicationRecoveryIncompleteCleanupStatus.Failed));
            Assert.That(fromIncomplete.IsValid, Is.True);
            Assert.That(fromIncomplete.IsIncompleteReleased, Is.True);

            Run captureComplete = MakeRun();
            NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt captureCompleteReceipt =
                captureComplete.ReleaseAfterCaptureComplete(cleanupFailed: true);

            NvencRunPublicationRecoveryTerminalResult fromCaptureComplete =
                NvencRunPublicationRecoveryTerminalResult.CaptureCompleted(
                    captureCompleteReceipt);

            Assert.That(captureCompleteReceipt.CleanupStatus,
                Is.EqualTo(NvencRunCaptureCompleteRecoveryCleanupStatus.Failed));
            Assert.That(fromCaptureComplete.IsValid, Is.True);
            Assert.That(fromCaptureComplete.IsCaptureCompleted, Is.True);
        }

        // ---- Fixture helpers ----

        private static void AssertForwardsSharedGraph(
            NvencRunPublicationRecoveryTerminalResult terminal, Run run, string runInitializationId)
        {
            Assert.That(terminal.PublicationRecoveryDecision, Is.Not.Null);
            Assert.That(
                ReferenceEquals(
                    terminal.Snapshot, terminal.PublicationRecoveryDecision.Snapshot),
                Is.True);
            Assert.That(ReferenceEquals(terminal.OpenOutcome, run.OpenOutcome), Is.True);
            Assert.That(ReferenceEquals(terminal.OwnershipLease, run.Owner), Is.True);
            Assert.That(ReferenceEquals(terminal.RootLayout, run.Layout), Is.True);
            Assert.That(terminal.TestRunId, Is.EqualTo(run.Layout.TestRunId));
            Assert.That(terminal.RunInitializationId, Is.EqualTo(runInitializationId));
        }

        private Run MakeRun()
        {
            CaptureRunRootLayout layout = MakeLayout();

            CaptureRunInitializationOpenOutcome openOutcome = MakeRecoveryOutcome(
                layout, out CaptureRunInitializationSessionOwnershipLease owner);

            return new Run(
                layout,
                openOutcome,
                new NvencRunPublicationRecoveryInspectionOperation(openOutcome, layout),
                owner);
        }

        /// <summary>
        /// Drives the existing initialization recovery orchestration to a
        /// publication-recovery outcome that still holds its lock, through the
        /// ordinary constructors only.
        /// </summary>
        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner)
        {
            CaptureRunMarkerBinding binding = new CaptureRunMarkerBinding(
                layout.TestRunId, InitId, layout.StagingRunRootSha256, layout.FinalRunRootSha256);

            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator =
                new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                    new FakeRecoveryInspector(
                        MakeRootObservation(
                            CaptureRunRootRole.Staging,
                            binding.StagingInitialization,
                            binding.StagingReady,
                            hasNonMarkerEntry: true),
                        MakeRootObservation(
                            CaptureRunRootRole.Final,
                            binding.FinalInitialization,
                            binding.FinalReady,
                            hasNonMarkerEntry: false)),
                    new CaptureRunInitializationRecoveryExecutionCoordinator(
                        new FakeCleanupBackend(), new FakeProvisioner(), new FakeMarkerWriter()));

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            CaptureRunLockLease lease = new CaptureRunLockLease(
                pathSet,
                new FakeHandle(pathSet.FirstLockPath),
                new FakeHandle(pathSet.SecondLockPath));
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);

            CaptureRunLockIdentityEvidence identity =
                CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(
                new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4));

            Assert.That(result.Status,
                Is.EqualTo(CaptureRunInitializationRecoveryExecutionStatus.PublicationRecoveryRequired),
                "the fixture must reach a publication-recovery outcome.");

            return new CaptureRunInitializationOpenOutcome(result, null, identity);
        }

        private static CaptureRunInitializationRootObservation MakeRootObservation(
            CaptureRunRootRole role,
            CaptureRunInitializationMarker init,
            CaptureRunReadyMarker ready,
            bool hasNonMarkerEntry)
        {
            return new CaptureRunInitializationRootObservation(
                role, true, false, CaptureRunMarkerObservationStatus.Canonical, init,
                false, CaptureRunMarkerObservationStatus.Canonical, ready,
                hasNonMarkerEntry, false, false);
        }

        /// <summary>A distinct layout per Run, so the three paths cannot alias.</summary>
        private CaptureRunRootLayout MakeLayout()
        {
            string root = Path.DirectorySeparatorChar == '\\'
                ? "C:\\zantetsuken-fake"
                : "/zantetsuken-fake";
            _runIds++;

            return new CaptureRunRootLayout(
                Path.Combine(root, "staging-" + _runIds),
                Path.Combine(root, "final-" + _runIds),
                1);
        }

        /// <summary>
        /// One Run holding its lease, able to reach any of the three terminal
        /// releases through the ordinary coordinators.
        /// </summary>
        private sealed class Run
        {
            private readonly NvencRunPublicationRecoveryInspectionOperation _inspection;

            internal Run(
                CaptureRunRootLayout layout,
                CaptureRunInitializationOpenOutcome openOutcome,
                NvencRunPublicationRecoveryInspectionOperation inspection,
                CaptureRunInitializationSessionOwnershipLease owner)
            {
                Layout = layout;
                OpenOutcome = openOutcome;
                _inspection = inspection;
                Owner = owner;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal CaptureRunInitializationOpenOutcome OpenOutcome { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            /// <summary>
            /// The recovered path: commit, CaptureComplete, cleanup, release.
            /// </summary>
            internal NvencRunCaptureCompleteRecoveryOwnershipReleaseReceipt
                ReleaseAfterCaptureComplete(bool cleanupFailed)
            {
                NvencRunCaptureCompleteRecoveryReceipt captureComplete =
                    new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                            new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                                new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(
                                    new FakeCommitter())),
                            new NvencRunCaptureCompleteRecoveryExecutionCoordinator(
                                new NvencRunCaptureCompleteRecoveryCompleter()))
                        .Execute(NvencRunCaptureIndexRecoveryClassifier.Classify(
                            new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                                new NvencRunCaptureIndexRecoveryInspectionOperation(Recoverable()),
                                NvencRunCaptureIndexObservationStatus.MatchesAuthoritative,
                                NvencRunCaptureIndexObservationStatus.Absent)));

                NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanup =
                    new NvencRunCaptureCompleteRecoveryCleanupOrchestrationCoordinator(
                            new NvencRunCaptureCompleteRecoveryCleanupExecutionCoordinator(
                                new FakeCaptureCompleteCleaner { Fail = cleanupFailed }))
                        .Execute(captureComplete);

                Assert.That(cleanup.IsValid, Is.True);

                return new NvencRunCaptureCompleteRecoveryOwnershipReleaseCoordinator(
                        cleanup,
                        Owner,
                        new NvencRunCaptureCompleteRecoveryOwnershipReleaseExecutionCoordinator(
                            new NvencRunCaptureCompleteRecoveryOwnershipReleaser()))
                    .Release();
            }

            /// <summary>The orphan cleanup path: cleanup, then release.</summary>
            internal NvencRunPublicationRecoveryIncompleteOwnershipReleaseReceipt
                ReleaseAfterOrphanCleanup(bool cleanupFailed)
            {
                NvencRunPublicationRecoveryIncompleteCleanupAttemptResult cleanup =
                    new NvencRunPublicationRecoveryIncompleteCleanupOrchestrationCoordinator(
                            new NvencRunPublicationRecoveryIncompleteCleanupExecutionCoordinator(
                                new FakeIncompleteCleaner { Fail = cleanupFailed }))
                        .Execute(Incomplete(), Owner);

                Assert.That(cleanup.IsValid, Is.True);

                return new NvencRunPublicationRecoveryIncompleteOwnershipReleaseCoordinator(
                        new NvencRunPublicationRecoveryIncompleteOwnershipReleaseExecutionCoordinator(
                            new NvencRunPublicationRecoveryIncompleteOwnershipReleaser()),
                        cleanup,
                        Owner)
                    .Release();
            }

            /// <summary>The stopping path: release straight from the decision.</summary>
            internal NvencRunPublicationRecoveryStopOwnershipReleaseReceipt ReleaseAfterStopping(
                bool deferred)
            {
                return new NvencRunPublicationRecoveryStopOwnershipReleaseCoordinator(
                        deferred ? Deferred() : Collision(),
                        Owner,
                        new NvencRunPublicationRecoveryStopOwnershipReleaseExecutionCoordinator(
                            new NvencRunPublicationRecoveryStopOwnershipReleaser()))
                    .Release();
            }

            /// <summary>No finished plan, with the precommit temporary present.</summary>
            private NvencRunPublicationRecoveryDecision Incomplete()
            {
                return NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        _inspection,
                        CaptureRunPublicationDocumentObservationStatus.Absent,
                        null,
                        true,
                        default));
            }

            /// <summary>A finished plan next to the precommit temporary.</summary>
            private NvencRunPublicationRecoveryDecision Collision()
            {
                return NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        _inspection,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        MakePlan(_inspection),
                        true,
                        default));
            }

            /// <summary>A canonical plan whose chunk could not be verified.</summary>
            private NvencRunPublicationRecoveryDecision Deferred()
            {
                CapturePublicationPlan plan = MakePlan(_inspection);

                return NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        _inspection,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        plan,
                        false,
                        new CaptureArtifactVerificationResult(
                            plan.GetArtifact(0),
                            CaptureArtifactVerificationExecutionDisposition.Deferred,
                            CaptureArtifactVerificationStatus.None,
                            CaptureArtifactVerificationFailureReason.BufferUnavailable,
                            0)));
            }

            /// <summary>A canonical plan whose chunk verified.</summary>
            private NvencRunPublicationRecoveryDecision Recoverable()
            {
                CapturePublicationPlan plan = MakePlan(_inspection);

                return NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        _inspection,
                        CaptureRunPublicationDocumentObservationStatus.Canonical,
                        plan,
                        false,
                        new CaptureArtifactVerificationResult(
                            plan.GetArtifact(0),
                            CaptureArtifactVerificationExecutionDisposition.Completed,
                            CaptureArtifactVerificationStatus.MatchesExpected,
                            CaptureArtifactVerificationFailureReason.None,
                            ChunkByteLength)));
            }

            private static CapturePublicationPlan MakePlan(
                NvencRunPublicationRecoveryInspectionOperation operation)
            {
                CaptureArtifactDescriptor[] artifacts = new[]
                {
                    NvencRunChunkArtifactDescriptorFactory.Create(
                        ArtifactId, ChunkByteLength, Hash64),
                };

                CaptureFrameEvidenceEntry[] entries = new CaptureFrameEvidenceEntry[3];
                for (int i = 0; i < entries.Length; i++)
                {
                    entries[i] = new CaptureFrameEvidenceEntry(i + 1, new[] { ArtifactId });
                }

                return new CapturePublicationPlan(
                    operation.TestRunId, operation.RunInitializationId, Hash64, artifacts, entries);
            }
        }

        /// <summary>Deletes nothing and reports the asked-for shape.</summary>
        private sealed class FakeCaptureCompleteCleaner : INvencRunCaptureCompleteRecoveryCleaner
        {
            internal bool Fail { get; set; }

            public NvencRunCaptureCompleteRecoveryCleanupAttemptResult Clean(
                NvencRunCaptureCompleteRecoveryCleanupOperation operation)
            {
                return Fail
                    ? NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Failed(this, operation)
                    : NvencRunCaptureCompleteRecoveryCleanupAttemptResult.Cleaned(this, operation);
            }
        }

        /// <summary>Deletes nothing and reports the asked-for shape.</summary>
        private sealed class FakeIncompleteCleaner : INvencRunPublicationRecoveryIncompleteCleaner
        {
            internal bool Fail { get; set; }

            public NvencRunPublicationRecoveryIncompleteCleanupAttemptResult Clean(
                NvencRunPublicationRecoveryIncompleteCleanupOperation operation)
            {
                return Fail
                    ? NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Failed(
                        this, operation)
                    : NvencRunPublicationRecoveryIncompleteCleanupAttemptResult.Cleaned(
                        this, operation);
            }
        }

        /// <summary>Mints the ordinary commit receipt and touches no file.</summary>
        private sealed class FakeCommitter : INvencRunCaptureIndexRecoveryCommitter
        {
            public NvencRunCaptureIndexRecoveryCommitReceipt Commit(
                NvencRunCaptureIndexRecoveryCommitOperation operation)
            {
                return NvencRunCaptureIndexRecoveryCommitReceipt.Committed(this, operation);
            }
        }

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            internal FakeHandle(string lockPath)
            {
                LockPath = lockPath;
            }

            public string LockPath { get; }

            public bool IsCreated => true;

            public void Dispose()
            {
            }
        }

        private sealed class FakeRecoveryInspector : ICaptureRunInitializationRecoveryInspector
        {
            private readonly CaptureRunInitializationRootObservation _staging;
            private readonly CaptureRunInitializationRootObservation _final;

            internal FakeRecoveryInspector(
                CaptureRunInitializationRootObservation staging,
                CaptureRunInitializationRootObservation final)
            {
                _staging = staging;
                _final = final;
            }

            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(
                CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                return new CaptureRunInitializationRecoveryInspectionSnapshot(
                    this, operation, _staging, _final);
            }
        }

        private sealed class FakeCleanupBackend : ICaptureRunInitializationRecoveryCleanupBackend
        {
            public CaptureRunInitializationRecoveryCleanupReceipt Execute(
                CaptureRunInitializationRecoveryCleanupOperation operation)
            {
                return new CaptureRunInitializationRecoveryCleanupReceipt(this, operation);
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(
                CaptureRunRootProvisionOperation operation)
            {
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeMarkerWriter : ICaptureRunMarkerAtomicWriter
        {
            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }
    }
}
