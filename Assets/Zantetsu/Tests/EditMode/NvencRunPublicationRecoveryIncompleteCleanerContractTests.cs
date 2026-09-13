using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the production Phase 0.11 NVENC publication recovery
    /// orphan cleaner: admission before any filesystem contact, the fixed
    /// deletion order and its flushes, what may and may not be deleted,
    /// resumption after a partial cleanup, and release on every path.
    /// </summary>
    /// <remarks>
    /// The filesystem is an in-memory recording fake, so every no-follow status
    /// and residual shape is reachable deterministically on any platform. One
    /// Windows-only case runs the real backend end to end, and the backend's
    /// own NT and P/Invoke details stay with its own fixture.
    /// </remarks>
    public class NvencRunPublicationRecoveryIncompleteCleanerContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string OtherInitId = "fedcba9876543210fedcba9876543210";

        private const string PublicationPlanName = "publication.plan";

        private const string LegacyPlanTemporaryName = "publication.plan.tmp";

        private const string PrecommitTemporaryName = "publication.plan.nvenc-precommit.tmp";

        private const string ChunksDirectoryName = "chunks";

        private const string CaptureIndexName = "capture.index";

        private const string CaptureIndexTemporaryName = "capture.index.tmp";

        private const string RunReadyMarkerName = "run.ready";

        private const string RunInitializationMarkerName = "run.init";

        private const string PartialChunkName = "chunk-0.nvenc-idr-chunk-v1.h264.partial";

        private const string FinalizedChunkName = "chunk-0.nvenc-idr-chunk-v1.h264";

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        private readonly List<string> _sandboxes = new List<string>();

        private int _runIds;

        [TearDown]
        public void TearDown()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }

            _owners.Clear();

            foreach (string sandbox in _sandboxes)
            {
                try
                {
                    if (Directory.Exists(sandbox))
                    {
                        Directory.Delete(sandbox, true);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            _sandboxes.Clear();
        }

        // ---- Admission before any filesystem contact ----

        [Test]
        public void Constructor_NullArguments_Rejected()
        {
            Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryIncompleteCleaner(null, new FakeFileSystem()));
            Assert.Throws<ArgumentNullException>(
                () => new NvencRunPublicationRecoveryIncompleteCleaner(MakeLayout(), null));
        }

        [Test]
        public void Clean_NullOperation_RejectedWithoutFilesystemContact()
        {
            Harness h = MakeHarness();

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => h.Cleaner.Clean(null));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(h.FileSystem.Calls, Is.Empty);
        }

        [Test]
        public void Clean_OperationWhoseLockWasReleased_RejectedWithoutFilesystemContact()
        {
            Harness h = MakeHarness();

            ReleaseAllLocks();
            Assert.That(h.Operation.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => h.Cleaner.Clean(h.Operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(h.FileSystem.Calls, Is.Empty);
        }

        [Test]
        public void Clean_OperationOfAnotherRootLayout_RejectedWithoutFilesystemContact()
        {
            Harness h = MakeHarness();
            Harness other = MakeHarness();

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => h.Cleaner.Clean(other.Operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(h.FileSystem.Calls, Is.Empty);
            Assert.That(other.FileSystem.Calls, Is.Empty);
        }

        [Test]
        public void Clean_WithoutNoFollowCapability_RejectedWithoutFilesystemContact()
        {
            Harness h = MakeHarness();
            h.FileSystem.Supported = false;

            Assert.Throws<CaptureArtifactNoFollowUnavailableException>(
                () => h.Cleaner.Clean(h.Operation));
            Assert.That(h.FileSystem.Calls, Is.Empty);
        }

        [Test]
        public void Clean_WithoutDirectoryFlushCapability_RejectedWithoutFilesystemContact()
        {
            Harness h = MakeHarness();
            h.FileSystem.DirectoryFlushSupported = false;

            Assert.Throws<CaptureArtifactNoFollowUnavailableException>(
                () => h.Cleaner.Clean(h.Operation));
            Assert.That(h.FileSystem.Calls, Is.Empty);
        }

        // ---- The representative normal paths ----

        [Test]
        public void Clean_RunWithItsPrecommitTemporary_DiscardsBothRoots()
        {
            Harness h = MakeHarness(temporaryPresent: true);

            AssertCleaned(h.Cleaner.Clean(h.Operation), h);
            Assert.That(h.FileSystem.Exists(h.PrecommitTemporaryPath), Is.False);
        }

        [Test]
        public void Clean_RunWithoutItsPrecommitTemporary_DiscardsBothRoots()
        {
            Harness h = MakeHarness(temporaryPresent: false);

            AssertCleaned(h.Cleaner.Clean(h.Operation), h);
        }

        [Test]
        public void Clean_DeletesBothThePartialAndTheFinalizedStagingChunk()
        {
            Harness withPartial = MakeHarness(partialChunk: true, finalizedChunk: false);
            AssertCleaned(withPartial.Cleaner.Clean(withPartial.Operation), withPartial);
            Assert.That(withPartial.FileSystem.Deletions, Has.Member(PartialChunkName));

            Harness withFinalized = MakeHarness(partialChunk: false, finalizedChunk: true);
            AssertCleaned(withFinalized.Cleaner.Clean(withFinalized.Operation), withFinalized);
            Assert.That(withFinalized.FileSystem.Deletions, Has.Member(FinalizedChunkName));

            Harness withBoth = MakeHarness(partialChunk: true, finalizedChunk: true);
            AssertCleaned(withBoth.Cleaner.Clean(withBoth.Operation), withBoth);
            Assert.That(withBoth.FileSystem.Deletions, Has.Member(PartialChunkName));
            Assert.That(withBoth.FileSystem.Deletions, Has.Member(FinalizedChunkName));
        }

        [Test]
        public void Clean_BothRunRootsGoAndTheirBaseDirectoriesRemain()
        {
            Harness h = MakeHarness();

            AssertCleaned(h.Cleaner.Clean(h.Operation), h);

            Assert.That(h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot), Is.False);
            Assert.That(h.FileSystem.DirectoryExists(h.Layout.FinalRunRoot), Is.False);
            Assert.That(h.FileSystem.DirectoryExists(h.StagingRunsPath), Is.True);
            Assert.That(h.FileSystem.DirectoryExists(h.FinalRunsPath), Is.True);
        }

        [Test]
        public void Clean_FollowsTheFixedOrderOfOpensDeletesAndFlushes()
        {
            Harness h = MakeHarness(temporaryPresent: true);

            AssertCleaned(h.Cleaner.Clean(h.Operation), h);

            Assert.That(h.FileSystem.Calls, Is.EqualTo(new[]
            {
                // 1. the finished plan must still be absent.
                Open(h.Layout.StagingRunRoot), TryOpen(PublicationPlanName),

                // 2. the NVENC precommit temporary.
                Open(h.Layout.StagingRunRoot), TryOpen(PrecommitTemporaryName),
                Delete(PrecommitTemporaryName), Flush(h.Layout.StagingRunRoot),

                // 3. the two fixed staging chunk entries.
                Open(h.ChunksPath), TryOpen(PartialChunkName), Delete(PartialChunkName),
                Flush(h.ChunksPath),
                Open(h.ChunksPath), TryOpen(FinalizedChunkName), Delete(FinalizedChunkName),
                Flush(h.ChunksPath),

                // 4. the emptied chunks directory.
                Open(h.Layout.StagingRunRoot), Open(h.ChunksPath), Empty(h.ChunksPath),
                DeleteDirectory(h.ChunksPath), Flush(h.Layout.StagingRunRoot),

                // 5. the residual markers of both roots.
                Open(h.Layout.FinalRunRoot), TryOpen(RunReadyMarkerName),
                TryOpen(RunInitializationMarkerName),
                Open(h.Layout.StagingRunRoot), TryOpen(RunReadyMarkerName),
                TryOpen(RunInitializationMarkerName),

                // no committed final artifact may exist before the final
                // markers are deleted.
                Open(h.Layout.FinalRunRoot), TryOpen(CaptureIndexName),
                TryOpen(CaptureIndexTemporaryName), Open(h.FinalChunksPath),

                // 6, 7 and 8. the final side.
                Open(h.Layout.FinalRunRoot), TryOpen(RunReadyMarkerName),
                Delete(RunReadyMarkerName), Flush(h.Layout.FinalRunRoot),
                Open(h.Layout.FinalRunRoot), TryOpen(RunInitializationMarkerName),
                Delete(RunInitializationMarkerName), Flush(h.Layout.FinalRunRoot),
                Open(h.FinalRunsPath), Open(h.Layout.FinalRunRoot),
                Empty(h.Layout.FinalRunRoot), DeleteDirectory(h.Layout.FinalRunRoot),
                Flush(h.FinalRunsPath),

                // 9, 10 and 11. the staging side.
                Open(h.Layout.StagingRunRoot), TryOpen(RunReadyMarkerName),
                Delete(RunReadyMarkerName), Flush(h.Layout.StagingRunRoot),
                Open(h.Layout.StagingRunRoot), TryOpen(RunInitializationMarkerName),
                Delete(RunInitializationMarkerName), Flush(h.Layout.StagingRunRoot),
                Open(h.StagingRunsPath), Open(h.Layout.StagingRunRoot),
                Empty(h.Layout.StagingRunRoot), DeleteDirectory(h.Layout.StagingRunRoot),
                Flush(h.StagingRunsPath),
            }));
        }

        [Test]
        public void Clean_NeverReadsThePrecommitTemporaryOrTheChunks()
        {
            Harness h = MakeHarness(temporaryPresent: true);

            AssertCleaned(h.Cleaner.Clean(h.Operation), h);

            // The markers are read; the discarded artifacts never are.
            Assert.That(h.FileSystem.ReadBytesOf(h.PrecommitTemporaryPath), Is.Zero);
            Assert.That(h.FileSystem.ReadBytesOf(h.PartialChunkPath), Is.Zero);
            Assert.That(h.FileSystem.ReadBytesOf(h.FinalizedChunkPath), Is.Zero);
            Assert.That(h.FileSystem.ReadBytesOf(h.StagingInitPath), Is.GreaterThan(0));
        }

        // ---- What must never be deleted ----

        [Test]
        public void Clean_FinishedPublicationPlanReappeared_FailsWithoutDeletingAnything()
        {
            Harness h = MakeHarness(temporaryPresent: true);
            h.FileSystem.AddFile(h.PlanPath, new byte[] { 1, 2, 3 });

            AssertFailed(h.Cleaner.Clean(h.Operation), h);

            Assert.That(h.FileSystem.Deletions, Is.Empty);
            Assert.That(h.FileSystem.Exists(h.PlanPath), Is.True);
            Assert.That(h.FileSystem.ReadBytesOf(h.PlanPath), Is.Zero, "never read either.");
            Assert.That(h.FileSystem.Exists(h.PrecommitTemporaryPath), Is.True);
            Assert.That(h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot), Is.True);
            Assert.That(h.FileSystem.DirectoryExists(h.Layout.FinalRunRoot), Is.True);
        }

        [Test]
        public void Clean_FinalCaptureIndexPresent_FailsWithoutTouchingTheFinalSide()
        {
            AssertFinalArtifactRefused(h => h.FileSystem.AddFile(h.CaptureIndexPath, h.IndexBytes));
        }

        [Test]
        public void Clean_FinalCaptureIndexTemporaryPresent_FailsWithoutTouchingTheFinalSide()
        {
            AssertFinalArtifactRefused(
                h => h.FileSystem.AddFile(h.CaptureIndexTemporaryPath, h.IndexBytes));
        }

        [Test]
        public void Clean_PublishedFinalChunkPresent_FailsWithoutTouchingTheFinalSide()
        {
            AssertFinalArtifactRefused(h =>
            {
                h.FileSystem.AddDirectory(h.FinalChunksPath);
                h.FileSystem.AddFile(h.PublishedChunkPath, h.ChunkBytes);
            });
        }

        [Test]
        public void Clean_LegacyPlanTemporaryPresent_FailsWithoutDeletingIt()
        {
            Harness h = MakeHarness();
            h.FileSystem.AddFile(h.LegacyPlanTemporaryPath, new byte[] { 9, 9 });

            AssertFailed(h.Cleaner.Clean(h.Operation), h);

            // The staging Run root is the last step, so everything else was
            // discarded and the unknown legacy temporary stayed where it is.
            Assert.That(h.FileSystem.Exists(h.LegacyPlanTemporaryPath), Is.True);
            Assert.That(h.FileSystem.Deletions, Has.No.Member(LegacyPlanTemporaryName));
            Assert.That(h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot), Is.True);
        }

        [Test]
        public void Clean_UnknownEntryInTheStagingRoot_FailsWithoutDeletingIt()
        {
            Harness h = MakeHarness();
            string unknown = Path.Combine(h.Layout.StagingRunRoot, "someone-elses.bin");
            h.FileSystem.AddFile(unknown, new byte[] { 7 });

            AssertFailed(h.Cleaner.Clean(h.Operation), h);

            Assert.That(h.FileSystem.Exists(unknown), Is.True);
            Assert.That(h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot), Is.True);
        }

        [Test]
        public void Clean_NonEmptyChunksDirectory_IsNeverDeletedRecursively()
        {
            Harness h = MakeHarness(temporaryPresent: false);
            string unknown = Path.Combine(h.ChunksPath, "chunk-1.unknown");
            h.FileSystem.AddFile(unknown, new byte[] { 3 });

            AssertFailed(h.Cleaner.Clean(h.Operation), h);

            Assert.That(h.FileSystem.Exists(unknown), Is.True);
            Assert.That(h.FileSystem.DirectoryExists(h.ChunksPath), Is.True);
            Assert.That(h.FileSystem.Deletions, Has.No.Member(ChunksDirectoryName));

            // Only the two fixed chunk entries were ever deleted.
            Assert.That(h.FileSystem.Deletions,
                Is.EqualTo(new[] { PartialChunkName, FinalizedChunkName }));
        }

        // ---- Observation failures are never absence ----

        [Test]
        public void Clean_UnobservableTarget_IsNeverFoldedIntoAbsence()
        {
            CaptureIndexFileOpenStatus[] statuses =
            {
                CaptureIndexFileOpenStatus.InvalidFileKind,
                CaptureIndexFileOpenStatus.EscapesRoot,
                CaptureIndexFileOpenStatus.IoFailure,
            };

            foreach (CaptureIndexFileOpenStatus status in statuses)
            {
                Harness h = MakeHarness(temporaryPresent: true);
                h.FileSystem.SetStatus(h.PrecommitTemporaryPath, status);

                AssertFailed(h.Cleaner.Clean(h.Operation), h);

                Assert.That(h.FileSystem.Deletions, Is.Empty, status.ToString());
                Assert.That(h.FileSystem.Exists(h.PrecommitTemporaryPath), Is.True,
                    status.ToString());
            }
        }

        [Test]
        public void Clean_UnobservableStagingRunRoot_IsNeverFoldedIntoAbsence()
        {
            Harness h = MakeHarness();
            h.FileSystem.SetStatus(h.Layout.StagingRunRoot, CaptureIndexFileOpenStatus.IoFailure);

            AssertFailed(h.Cleaner.Clean(h.Operation), h);
            Assert.That(h.FileSystem.Deletions, Is.Empty);
        }

        // ---- Residual marker shapes ----

        [Test]
        public void Clean_ReadyMarkerWithoutItsInitializationMarker_Fails()
        {
            Harness h = MakeHarness();
            h.FileSystem.RemoveFile(h.StagingInitPath);

            AssertFailed(h.Cleaner.Clean(h.Operation), h);

            Assert.That(h.FileSystem.Exists(h.StagingReadyPath), Is.True);
            Assert.That(h.FileSystem.Deletions, Has.No.Member(RunReadyMarkerName));
        }

        [Test]
        public void Clean_MarkerOfAnotherRun_Fails()
        {
            Harness h = MakeHarness();
            h.FileSystem.SetFile(h.StagingInitPath, h.ForeignStagingInitBytes);

            AssertFailed(h.Cleaner.Clean(h.Operation), h);
            Assert.That(h.FileSystem.Deletions, Has.No.Member(RunInitializationMarkerName));
        }

        [Test]
        public void Clean_ReadyMarkerWithAForeignPeerBinding_Fails()
        {
            // Both peer hashes are checked: a ready marker naming this Run but
            // binding another Run's initialization markers is refused.
            Harness h = MakeHarness();
            h.FileSystem.SetFile(h.FinalReadyPath, h.ForeignReadyBytes);

            AssertFailed(h.Cleaner.Clean(h.Operation), h);
            Assert.That(h.FileSystem.Exists(h.FinalReadyPath), Is.True);
        }

        [Test]
        public void Clean_InitializationMarkerOfTheWrongRoot_Fails()
        {
            // The final root's marker carries the final role; the staging one
            // does not belong there.
            Harness h = MakeHarness();
            h.FileSystem.SetFile(h.FinalInitPath, h.StagingInitBytes);

            AssertFailed(h.Cleaner.Clean(h.Operation), h);
            Assert.That(h.FileSystem.Exists(h.FinalInitPath), Is.True);
        }

        // ---- Resumption ----

        [Test]
        public void Clean_ResumesFromEveryIntermediateStage()
        {
            // Each stage is what the filesystem looks like after a previous
            // attempt failed part-way through the fixed order.
            for (int stage = 0; stage <= 10; stage++)
            {
                Harness h = MakeHarness(temporaryPresent: true);
                ApplyProgress(h, stage);

                NvencRunPublicationRecoveryIncompleteCleanupAttemptResult result =
                    h.Cleaner.Clean(h.Operation);

                Assert.That(result.IsCleaned, Is.True, "stage " + stage);
                Assert.That(h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot), Is.False,
                    "stage " + stage);
                Assert.That(h.FileSystem.DirectoryExists(h.Layout.FinalRunRoot), Is.False,
                    "stage " + stage);
                AssertEverythingReleased(h);
            }
        }

        // ---- Flush is part of every step ----

        [Test]
        public void Clean_EachStepRequiresItsOwnParentFlush()
        {
            // One flush failure per step, including the steps whose entry this
            // attempt found already gone.
            for (int stage = 0; stage <= 10; stage++)
            {
                Harness h = MakeHarness(temporaryPresent: true);
                ApplyProgress(h, stage);

                // The number of successful flushes this attempt needs is fixed;
                // failing each of them in turn must fail the cleanup.
                int flushes = CountFlushes(MakeAndRun(temporaryPresent: true, stage: stage));

                for (int ordinal = 1; ordinal <= flushes; ordinal++)
                {
                    Harness attempt = MakeHarness(temporaryPresent: true);
                    ApplyProgress(attempt, stage);
                    attempt.FileSystem.FailFlushNumber(ordinal);

                    Assert.That(attempt.Cleaner.Clean(attempt.Operation).IsFailed, Is.True,
                        "stage " + stage + ", flush " + ordinal);
                    AssertEverythingReleased(attempt);
                }
            }
        }

        [Test]
        public void Clean_StepsWhoseEntryIsAlreadyGone_StillFlushTheirOwnParent()
        {
            // A resumed attempt: the precommit temporary and both chunk files
            // were deleted by an earlier attempt. Each of those steps must
            // still flush the directory that held its entry before it counts as
            // processed, because that earlier attempt may have died between the
            // deletion and its flush.
            Harness h = MakeHarness(temporaryPresent: true);
            ApplyProgress(h, 3);

            Assert.That(h.Cleaner.Clean(h.Operation).IsCleaned, Is.True);

            Assert.That(h.FileSystem.Calls.GetRange(0, 14), Is.EqualTo(new[]
            {
                Open(h.Layout.StagingRunRoot), TryOpen(PublicationPlanName),
                Open(h.Layout.StagingRunRoot), TryOpen(PrecommitTemporaryName),
                Flush(h.Layout.StagingRunRoot),
                Open(h.ChunksPath), TryOpen(PartialChunkName), Flush(h.ChunksPath),
                Open(h.ChunksPath), TryOpen(FinalizedChunkName), Flush(h.ChunksPath),
                Open(h.Layout.StagingRunRoot), Open(h.ChunksPath), Empty(h.ChunksPath),
            }));

            // Nothing was deleted inside chunks this time, yet both of its
            // steps flushed it.
            Assert.That(h.FileSystem.Deletions, Has.No.Member(PartialChunkName));
            Assert.That(h.FileSystem.Deletions, Has.No.Member(FinalizedChunkName));
            Assert.That(h.FileSystem.SuccessfulFlushes(h.ChunksPath), Is.EqualTo(2));
        }

        [Test]
        public void Clean_AfterADeletionWhoseFlushFailed_CompletesOnTheAbsentPath()
        {
            Harness h = MakeHarness(temporaryPresent: true);

            // The precommit temporary is deleted and the staging Run root's
            // first flush then fails.
            h.FileSystem.FailFlushesAt(h.Layout.StagingRunRoot, 1);

            Assert.That(h.Cleaner.Clean(h.Operation).IsFailed, Is.True);
            Assert.That(h.FileSystem.Exists(h.PrecommitTemporaryPath), Is.False,
                "the deletion itself succeeded.");
            Assert.That(h.FileSystem.SuccessfulFlushes(h.Layout.StagingRunRoot), Is.Zero);

            // The next attempt over the same tree finds the entry absent and
            // must still flush that same directory before treating the step as
            // processed.
            Assert.That(h.Cleaner.Clean(h.Operation).IsCleaned, Is.True);
            Assert.That(h.FileSystem.SuccessfulFlushes(h.Layout.StagingRunRoot),
                Is.GreaterThan(0));
            AssertEverythingReleased(h);
        }

        // ---- Correlation, release and the untouched authority graph ----

        [Test]
        public void Clean_ResultsCorrelateToTheExactCleanerAndOperation()
        {
            Harness cleaned = MakeHarness();
            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult ok =
                cleaned.Cleaner.Clean(cleaned.Operation);

            Assert.That(ok.IsIssuedFor(cleaned.Cleaner, cleaned.Operation), Is.True);
            Assert.That(ok.Receipt.IsIssuedFor(cleaned.Cleaner, cleaned.Operation), Is.True);

            Harness failed = MakeHarness();
            failed.FileSystem.AddFile(failed.PlanPath, new byte[] { 1 });
            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult bad =
                failed.Cleaner.Clean(failed.Operation);

            Assert.That(bad.IsFailed, Is.True);
            Assert.That(bad.Receipt, Is.Null);
            Assert.That(bad.IsIssuedFor(failed.Cleaner, failed.Operation), Is.True);
        }

        [Test]
        public void Clean_ReleasesEveryStreamAndHandleOnBothOutcomes()
        {
            Harness cleaned = MakeHarness(temporaryPresent: true);
            Assert.That(cleaned.Cleaner.Clean(cleaned.Operation).IsCleaned, Is.True);
            AssertEverythingReleased(cleaned);

            Harness failed = MakeHarness(temporaryPresent: true);
            failed.FileSystem.SetFile(failed.StagingInitPath, failed.ForeignStagingInitBytes);
            Assert.That(failed.Cleaner.Clean(failed.Operation).IsFailed, Is.True);
            AssertEverythingReleased(failed);
        }

        [Test]
        public void Clean_LeavesTheOperationAuthorityGraphUnchanged()
        {
            Harness h = MakeHarness(temporaryPresent: true);
            NvencRunPublicationRecoveryDecision decision = h.Operation.Decision;
            NvencRunPublicationRecoveryInspectionSnapshot snapshot = h.Operation.Snapshot;
            CaptureRunInitializationOpenOutcome openOutcome = h.Operation.OpenOutcome;
            CaptureRunLockIdentityEvidence evidence = h.Operation.LockIdentityEvidence;
            CaptureRunInitializationSessionOwnershipLease lease = h.Operation.OwnershipLease;

            Assert.That(h.Cleaner.Clean(h.Operation).IsCleaned, Is.True);

            Assert.That(ReferenceEquals(h.Operation.Decision, decision), Is.True);
            Assert.That(ReferenceEquals(h.Operation.Snapshot, snapshot), Is.True);
            Assert.That(ReferenceEquals(h.Operation.OpenOutcome, openOutcome), Is.True);
            Assert.That(ReferenceEquals(h.Operation.LockIdentityEvidence, evidence), Is.True);
            Assert.That(ReferenceEquals(h.Operation.OwnershipLease, lease), Is.True);

            Assert.That(decision.IsValid, Is.True);
            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunPublicationRecoveryDisposition.Incomplete));
            Assert.That(snapshot.PrecommitTemporaryPresent, Is.True);
            Assert.That(lease.IsCreated, Is.True);
            Assert.That(lease.CanRelease, Is.True);
            Assert.That(lease.IsReleaseComplete, Is.False);
            Assert.That(h.Operation.IsValid, Is.True);
        }

        [Test]
        public void Cleaner_HoldsOnlyItsLayoutAndFilesystem()
        {
            Type type = typeof(NvencRunPublicationRecoveryIncompleteCleaner);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            List<Type> types = new List<Type>();
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, "every held reference must be readonly.");
                types.Add(field.FieldType);
            }

            Assert.That(types, Is.EquivalentTo(new[]
            {
                typeof(CaptureRunRootLayout),
                typeof(ICaptureCompleteCleanupFileSystem),
            }));
        }

        // ---- Windows backend integration ----

        [Test]
        public void Backend_IncompleteRunWithItsTemporary_DiscardsThroughRealHandles()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore("The cleanup filesystem requires Windows handles.");
            }

            SandboxHarness h = MakeSandboxHarness();

            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult result =
                h.Cleaner.Clean(h.Operation);

            Assert.That(result.IsCleaned, Is.True);
            Assert.That(Directory.Exists(h.Layout.StagingRunRoot), Is.False);
            Assert.That(Directory.Exists(h.Layout.FinalRunRoot), Is.False);
            Assert.That(Directory.Exists(Path.GetDirectoryName(h.Layout.StagingRunRoot)), Is.True);
            Assert.That(Directory.Exists(Path.GetDirectoryName(h.Layout.FinalRunRoot)), Is.True);
        }

        // ---- Fixture helpers ----

        private static string Open(string path) => "TryOpenDirectory:" + Path.GetFullPath(path);

        private static string TryOpen(string name) => "TryOpen:" + name;

        private static string Delete(string name) => "Delete:" + name;

        private static string DeleteDirectory(string path) =>
            "DeleteDirectory:" + Path.GetFullPath(path);

        private static string Flush(string path) => "FlushDirectory:" + Path.GetFullPath(path);

        private static string Empty(string path) => "IsDirectoryEmpty:" + Path.GetFullPath(path);

        private static void AssertCleaned(
            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult result, Harness h)
        {
            Assert.That(result.IsCleaned, Is.True);
            Assert.That(result.Receipt, Is.Not.Null);
            Assert.That(result.IsIssuedFor(h.Cleaner, h.Operation), Is.True);
            Assert.That(h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot), Is.False);
            Assert.That(h.FileSystem.DirectoryExists(h.Layout.FinalRunRoot), Is.False);
            AssertEverythingReleased(h);
        }

        private static void AssertFailed(
            NvencRunPublicationRecoveryIncompleteCleanupAttemptResult result, Harness h)
        {
            Assert.That(result.IsFailed, Is.True);
            Assert.That(result.Receipt, Is.Null);
            AssertEverythingReleased(h);
        }

        private static void AssertEverythingReleased(Harness h)
        {
            foreach (RecordingStream stream in h.FileSystem.Streams)
            {
                Assert.That(stream.Disposed, Is.True, "every stream must be released.");
            }

            foreach (SafeFileHandle handle in h.FileSystem.DirectoryHandles)
            {
                Assert.That(handle.IsClosed, Is.True, "every directory handle must be released.");
            }
        }

        /// <summary>
        /// A committed final artifact appears where an incomplete Run can have
        /// none: the final side must be left exactly as it is.
        /// </summary>
        private void AssertFinalArtifactRefused(Action<Harness> place)
        {
            Harness h = MakeHarness();
            place(h);

            AssertFailed(h.Cleaner.Clean(h.Operation), h);

            Assert.That(h.FileSystem.DirectoryExists(h.Layout.FinalRunRoot), Is.True);
            Assert.That(h.FileSystem.Exists(h.FinalReadyPath), Is.True);
            Assert.That(h.FileSystem.Exists(h.FinalInitPath), Is.True);
            Assert.That(h.FileSystem.Deletions, Has.No.Member(RunReadyMarkerName));
            Assert.That(h.FileSystem.Deletions, Has.No.Member(RunInitializationMarkerName));

            if (h.FileSystem.Exists(h.CaptureIndexPath))
            {
                Assert.That(h.FileSystem.Read(h.CaptureIndexPath), Is.EqualTo(h.IndexBytes));
                Assert.That(h.FileSystem.ReadBytesOf(h.CaptureIndexPath), Is.Zero);
            }

            if (h.FileSystem.Exists(h.PublishedChunkPath))
            {
                Assert.That(h.FileSystem.Read(h.PublishedChunkPath), Is.EqualTo(h.ChunkBytes));
                Assert.That(h.FileSystem.OpenedPaths,
                    Has.No.Member(Path.GetFullPath(h.PublishedChunkPath)));
            }
        }

        /// <summary>
        /// Removes the first <paramref name="stage"/> targets of the fixed
        /// order, as a failed earlier attempt would have left them.
        /// </summary>
        private static void ApplyProgress(Harness h, int stage)
        {
            if (stage >= 1)
            {
                h.FileSystem.RemoveFile(h.PrecommitTemporaryPath);
            }

            if (stage >= 2)
            {
                h.FileSystem.RemoveFile(h.PartialChunkPath);
            }

            if (stage >= 3)
            {
                h.FileSystem.RemoveFile(h.FinalizedChunkPath);
            }

            if (stage >= 4)
            {
                h.FileSystem.RemoveDirectory(h.ChunksPath);
            }

            if (stage >= 5)
            {
                h.FileSystem.RemoveFile(h.FinalReadyPath);
            }

            if (stage >= 6)
            {
                h.FileSystem.RemoveFile(h.FinalInitPath);
            }

            if (stage >= 7)
            {
                h.FileSystem.RemoveDirectory(h.Layout.FinalRunRoot);
            }

            if (stage >= 8)
            {
                h.FileSystem.RemoveFile(h.StagingReadyPath);
            }

            if (stage >= 9)
            {
                h.FileSystem.RemoveFile(h.StagingInitPath);
            }

            if (stage >= 10)
            {
                h.FileSystem.RemoveDirectory(h.Layout.StagingRunRoot);
            }
        }

        private Harness MakeAndRun(bool temporaryPresent, int stage)
        {
            Harness h = MakeHarness(temporaryPresent: temporaryPresent);
            ApplyProgress(h, stage);
            Assert.That(h.Cleaner.Clean(h.Operation).IsCleaned, Is.True);
            return h;
        }

        private static int CountFlushes(Harness h)
        {
            return h.FileSystem.Flushes.Count;
        }

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        /// <summary>
        /// One incomplete Run over the in-memory filesystem, seeded as a
        /// process that died before committing its publication plan leaves it.
        /// </summary>
        private Harness MakeHarness(
            bool temporaryPresent = true,
            bool partialChunk = true,
            bool finalizedChunk = true)
        {
            CaptureRunRootLayout layout = MakeLayout();
            FakeFileSystem fileSystem = new FakeFileSystem();

            Harness harness = new Harness(
                layout,
                MakeOperation(layout, temporaryPresent),
                fileSystem,
                new NvencRunPublicationRecoveryIncompleteCleaner(layout, fileSystem));

            harness.Seed(temporaryPresent, partialChunk, finalizedChunk);
            return harness;
        }

        private SandboxHarness MakeSandboxHarness()
        {
            string sandbox = Path.Combine(
                Path.GetTempPath(), "zantetsuken-orphan-cleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                Path.Combine(sandbox, "staging"), Path.Combine(sandbox, "final"), 1);

            SandboxHarness harness = new SandboxHarness(
                layout,
                MakeOperation(layout, temporaryPresent: true),
                new NvencRunPublicationRecoveryIncompleteCleaner(
                    layout, CaptureIndexCommitFileSystem.Create()));

            harness.Populate();
            return harness;
        }

        private NvencRunPublicationRecoveryIncompleteCleanupOperation MakeOperation(
            CaptureRunRootLayout layout, bool temporaryPresent)
        {
            NvencRunPublicationRecoveryInspectionOperation inspection =
                new NvencRunPublicationRecoveryInspectionOperation(
                    MakeRecoveryOutcome(layout, out CaptureRunInitializationSessionOwnershipLease owner),
                    layout);

            NvencRunPublicationRecoveryDecision decision =
                NvencRunPublicationRecoveryClassifier.Classify(
                    new NvencRunPublicationRecoveryInspectionSnapshot(
                        inspection,
                        CaptureRunPublicationDocumentObservationStatus.Absent,
                        null,
                        temporaryPresent,
                        default));

            Assert.That(decision.Disposition,
                Is.EqualTo(NvencRunPublicationRecoveryDisposition.Incomplete));

            return NvencRunPublicationRecoveryIncompleteCleanupOperation.Create(decision, owner);
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

        /// <summary>
        /// A distinct layout per call, so a "foreign" layout is genuinely
        /// another Run rather than the same paths again.
        /// </summary>
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

        /// <summary>One incomplete Run over the in-memory filesystem.</summary>
        private sealed class Harness
        {
            internal Harness(
                CaptureRunRootLayout layout,
                NvencRunPublicationRecoveryIncompleteCleanupOperation operation,
                FakeFileSystem fileSystem,
                NvencRunPublicationRecoveryIncompleteCleaner cleaner)
            {
                Layout = layout;
                Operation = operation;
                FileSystem = fileSystem;
                Cleaner = cleaner;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal NvencRunPublicationRecoveryIncompleteCleanupOperation Operation { get; }

            internal FakeFileSystem FileSystem { get; }

            internal NvencRunPublicationRecoveryIncompleteCleaner Cleaner { get; }

            internal byte[] IndexBytes { get; } = new byte[] { 1, 2, 3, 4, 5 };

            internal byte[] ChunkBytes { get; } = new byte[] { 10, 20, 30, 40 };

            internal byte[] StagingInitBytes { get; private set; }

            internal byte[] ForeignStagingInitBytes { get; private set; }

            internal byte[] ForeignReadyBytes { get; private set; }

            internal string StagingRunsPath => Path.GetDirectoryName(Layout.StagingRunRoot);

            internal string FinalRunsPath => Path.GetDirectoryName(Layout.FinalRunRoot);

            internal string ChunksPath => Path.Combine(Layout.StagingRunRoot, ChunksDirectoryName);

            internal string FinalChunksPath =>
                Path.Combine(Layout.FinalRunRoot, ChunksDirectoryName);

            internal string PlanPath => Path.Combine(Layout.StagingRunRoot, PublicationPlanName);

            internal string LegacyPlanTemporaryPath =>
                Path.Combine(Layout.StagingRunRoot, LegacyPlanTemporaryName);

            internal string PrecommitTemporaryPath =>
                Path.Combine(Layout.StagingRunRoot, PrecommitTemporaryName);

            internal string PartialChunkPath => Path.Combine(ChunksPath, PartialChunkName);

            internal string FinalizedChunkPath => Path.Combine(ChunksPath, FinalizedChunkName);

            internal string StagingReadyPath =>
                Path.Combine(Layout.StagingRunRoot, RunReadyMarkerName);

            internal string StagingInitPath =>
                Path.Combine(Layout.StagingRunRoot, RunInitializationMarkerName);

            internal string FinalReadyPath => Path.Combine(Layout.FinalRunRoot, RunReadyMarkerName);

            internal string FinalInitPath =>
                Path.Combine(Layout.FinalRunRoot, RunInitializationMarkerName);

            internal string CaptureIndexPath => Path.Combine(Layout.FinalRunRoot, CaptureIndexName);

            internal string CaptureIndexTemporaryPath =>
                Path.Combine(Layout.FinalRunRoot, CaptureIndexTemporaryName);

            internal string PublishedChunkPath =>
                Path.Combine(FinalChunksPath, FinalizedChunkName);

            internal void Seed(bool temporaryPresent, bool partialChunk, bool finalizedChunk)
            {
                CaptureRunInitializationDocumentSet documents =
                    new CaptureRunInitializationDocumentSet(Layout, InitId);
                StagingInitBytes = documents.GetStagingInitializationBytes();

                CaptureRunInitializationDocumentSet foreign =
                    new CaptureRunInitializationDocumentSet(Layout, OtherInitId);
                ForeignStagingInitBytes = foreign.GetStagingInitializationBytes();
                ForeignReadyBytes = MakeForeignPeerReadyBytes();

                FileSystem.AddDirectory(StagingRunsPath);
                FileSystem.AddDirectory(Layout.StagingRunRoot);
                FileSystem.AddDirectory(ChunksPath);
                FileSystem.AddFile(StagingReadyPath, documents.GetStagingReadyBytes());
                FileSystem.AddFile(StagingInitPath, StagingInitBytes);

                FileSystem.AddDirectory(FinalRunsPath);
                FileSystem.AddDirectory(Layout.FinalRunRoot);
                FileSystem.AddFile(FinalReadyPath, documents.GetFinalReadyBytes());
                FileSystem.AddFile(FinalInitPath, documents.GetFinalInitializationBytes());

                if (temporaryPresent)
                {
                    FileSystem.AddFile(PrecommitTemporaryPath, new byte[] { 0, 0, 0, 1, 9, 9 });
                }

                if (partialChunk)
                {
                    FileSystem.AddFile(PartialChunkPath, ChunkBytes);
                }

                if (finalizedChunk)
                {
                    FileSystem.AddFile(FinalizedChunkPath, ChunkBytes);
                }
            }

            /// <summary>
            /// A ready marker naming this Run but binding another Run's
            /// initialization markers, so only the peer hashes can reject it.
            /// </summary>
            private byte[] MakeForeignPeerReadyBytes()
            {
                CaptureRunMarkerBinding other = new CaptureRunMarkerBinding(
                    Layout.TestRunId,
                    OtherInitId,
                    Layout.StagingRunRootSha256,
                    Layout.FinalRunRootSha256);

                return CaptureRunReadyMarkerCodec.SerializeCanonical(
                    new CaptureRunReadyMarker(
                        Layout.TestRunId,
                        InitId,
                        other.StagingReady.StagingInitSha256,
                        other.StagingReady.FinalInitSha256));
            }
        }

        /// <summary>One incomplete Run over a real temporary tree.</summary>
        private sealed class SandboxHarness
        {
            internal SandboxHarness(
                CaptureRunRootLayout layout,
                NvencRunPublicationRecoveryIncompleteCleanupOperation operation,
                NvencRunPublicationRecoveryIncompleteCleaner cleaner)
            {
                Layout = layout;
                Operation = operation;
                Cleaner = cleaner;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal NvencRunPublicationRecoveryIncompleteCleanupOperation Operation { get; }

            internal NvencRunPublicationRecoveryIncompleteCleaner Cleaner { get; }

            internal void Populate()
            {
                CaptureRunInitializationDocumentSet documents =
                    new CaptureRunInitializationDocumentSet(Layout, InitId);

                string chunks = Path.Combine(Layout.StagingRunRoot, ChunksDirectoryName);
                Directory.CreateDirectory(chunks);
                Directory.CreateDirectory(Layout.FinalRunRoot);

                File.WriteAllBytes(
                    Path.Combine(Layout.StagingRunRoot, PrecommitTemporaryName),
                    new byte[] { 0, 0, 0, 1, 9, 9 });
                File.WriteAllBytes(
                    Path.Combine(chunks, PartialChunkName), new byte[] { 10, 20, 30, 40 });
                File.WriteAllBytes(
                    Path.Combine(Layout.StagingRunRoot, RunReadyMarkerName),
                    documents.GetStagingReadyBytes());
                File.WriteAllBytes(
                    Path.Combine(Layout.StagingRunRoot, RunInitializationMarkerName),
                    documents.GetStagingInitializationBytes());
                File.WriteAllBytes(
                    Path.Combine(Layout.FinalRunRoot, RunReadyMarkerName),
                    documents.GetFinalReadyBytes());
                File.WriteAllBytes(
                    Path.Combine(Layout.FinalRunRoot, RunInitializationMarkerName),
                    documents.GetFinalInitializationBytes());
            }
        }

        /// <summary>
        /// An in-memory no-follow cleanup filesystem that records the call
        /// order, the deleted entries, the flushed directories, and how much of
        /// each file was actually read. Absence is the default, and any status
        /// can be forced for one exact path.
        /// </summary>
        private sealed class FakeFileSystem : ICaptureCompleteCleanupFileSystem
        {
            private readonly HashSet<string> _directories =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, byte[]> _files =
                new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, CaptureIndexFileOpenStatus> _statuses =
                new Dictionary<string, CaptureIndexFileOpenStatus>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _flushFailures =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, List<RecordingStream>> _streamsByPath =
                new Dictionary<string, List<RecordingStream>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<CaptureIndexCommitDirectory, string> _directoryPaths =
                new Dictionary<CaptureIndexCommitDirectory, string>();
            private readonly Dictionary<CaptureIndexCommitFile, string> _filePaths =
                new Dictionary<CaptureIndexCommitFile, string>();

            private int _flushNumber;
            private int _failFlushNumber;

            internal bool Supported { get; set; } = true;

            internal bool DirectoryFlushSupported { get; set; } = true;

            internal List<string> Calls { get; } = new List<string>();

            internal List<string> OpenedPaths { get; } = new List<string>();

            internal List<string> Deletions { get; } = new List<string>();

            internal List<string> Flushes { get; } = new List<string>();

            internal List<RecordingStream> Streams { get; } = new List<RecordingStream>();

            internal List<SafeFileHandle> DirectoryHandles { get; } = new List<SafeFileHandle>();

            public bool IsSupported => Supported;

            public bool IsDirectoryFlushSupported => DirectoryFlushSupported;

            internal void AddDirectory(string path)
            {
                _directories.Add(Norm(path));
            }

            internal void RemoveDirectory(string path)
            {
                _directories.Remove(Norm(path));
            }

            internal bool DirectoryExists(string path)
            {
                return _directories.Contains(Norm(path));
            }

            internal void AddFile(string path, byte[] content)
            {
                _files[Norm(path)] = content;
            }

            internal void SetFile(string path, byte[] content)
            {
                _files[Norm(path)] = content;
            }

            internal void RemoveFile(string path)
            {
                _files.Remove(Norm(path));
            }

            internal bool Exists(string path)
            {
                return _files.ContainsKey(Norm(path));
            }

            internal byte[] Read(string path)
            {
                return _files[Norm(path)];
            }

            /// <summary>
            /// How many bytes the cleaner itself read from one file, across
            /// every time it opened that path.
            /// </summary>
            internal int ReadBytesOf(string path)
            {
                if (!_streamsByPath.TryGetValue(Norm(path), out List<RecordingStream> streams))
                {
                    return 0;
                }

                int bytes = 0;
                foreach (RecordingStream stream in streams)
                {
                    bytes += stream.BytesRead;
                }

                return bytes;
            }

            internal void SetStatus(string path, CaptureIndexFileOpenStatus status)
            {
                _statuses[Norm(path)] = status;
            }

            /// <summary>Fails the next <paramref name="times"/> flushes of one directory.</summary>
            internal void FailFlushesAt(string path, int times)
            {
                _flushFailures[Norm(path)] = times;
            }

            /// <summary>
            /// Fails the <paramref name="ordinal"/>-th flush of this attempt,
            /// whichever directory it targets.
            /// </summary>
            internal void FailFlushNumber(int ordinal)
            {
                _failFlushNumber = ordinal;
            }

            internal int SuccessfulFlushes(string path)
            {
                string normalized = Norm(path);
                int flushes = 0;
                foreach (string flushed in Flushes)
                {
                    if (string.Equals(flushed, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        flushes++;
                    }
                }

                return flushes;
            }

            public CaptureIndexCommitDirectory OpenDirectory(string absolutePath)
            {
                CaptureIndexDirectoryOpen opened = TryOpenDirectory(absolutePath);
                if (opened.Status != CaptureIndexFileOpenStatus.Opened)
                {
                    throw new IOException("Failed to open " + absolutePath + ".");
                }

                return opened.Directory;
            }

            public CaptureIndexDirectoryOpen TryOpenDirectory(string absolutePath)
            {
                string path = Norm(absolutePath);
                Calls.Add("TryOpenDirectory:" + path);

                if (_statuses.TryGetValue(path, out CaptureIndexFileOpenStatus status))
                {
                    return CaptureIndexDirectoryOpen.Of(status);
                }

                if (!_directories.Contains(path))
                {
                    return CaptureIndexDirectoryOpen.Of(CaptureIndexFileOpenStatus.Absent);
                }

                SafeFileHandle handle = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
                DirectoryHandles.Add(handle);
                OpenedPaths.Add(path);

                CaptureIndexCommitDirectory directory =
                    new CaptureIndexCommitDirectory(handle, path, path);
                _directoryPaths[directory] = path;
                return CaptureIndexDirectoryOpen.Opened(directory);
            }

            public CaptureIndexFileOpen TryOpen(CaptureIndexCommitDirectory directory, string name)
            {
                string path = Norm(Path.Combine(_directoryPaths[directory], name));
                Calls.Add("TryOpen:" + name);

                if (_statuses.TryGetValue(path, out CaptureIndexFileOpenStatus status))
                {
                    return CaptureIndexFileOpen.Of(status);
                }

                if (_directories.Contains(path))
                {
                    return CaptureIndexFileOpen.Of(CaptureIndexFileOpenStatus.InvalidFileKind);
                }

                if (!_files.TryGetValue(path, out byte[] content))
                {
                    return CaptureIndexFileOpen.Of(CaptureIndexFileOpenStatus.Absent);
                }

                RecordingStream stream = new RecordingStream(content);
                Streams.Add(stream);
                if (!_streamsByPath.TryGetValue(path, out List<RecordingStream> opened))
                {
                    opened = new List<RecordingStream>();
                    _streamsByPath[path] = opened;
                }

                opened.Add(stream);
                OpenedPaths.Add(path);

                CaptureIndexCommitFile file = new CaptureIndexCommitFile(null, stream);
                _filePaths[file] = path;
                return CaptureIndexFileOpen.Opened(file);
            }

            public void Delete(CaptureIndexCommitFile file)
            {
                string path = _filePaths[file];
                Calls.Add("Delete:" + Path.GetFileName(path));
                Deletions.Add(Path.GetFileName(path));
                _files.Remove(path);
            }

            public void DeleteDirectory(CaptureIndexCommitDirectory directory)
            {
                string path = _directoryPaths[directory];
                Calls.Add("DeleteDirectory:" + path);
                Deletions.Add(Path.GetFileName(path));
                _directories.Remove(path);
            }

            public bool IsDirectoryEmpty(CaptureIndexCommitDirectory directory)
            {
                string path = _directoryPaths[directory];
                Calls.Add("IsDirectoryEmpty:" + path);

                foreach (string file in _files.Keys)
                {
                    if (string.Equals(
                            Path.GetDirectoryName(file), path, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                foreach (string child in _directories)
                {
                    if (string.Equals(
                            Path.GetDirectoryName(child), path, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return true;
            }

            public void FlushDirectory(CaptureIndexCommitDirectory directory)
            {
                string path = _directoryPaths[directory];
                Calls.Add("FlushDirectory:" + path);

                _flushNumber++;
                if (_failFlushNumber == _flushNumber)
                {
                    throw new IOException("Failed to flush " + path + ".");
                }

                if (_flushFailures.TryGetValue(path, out int remaining) && remaining > 0)
                {
                    _flushFailures[path] = remaining - 1;
                    throw new IOException("Failed to flush " + path + ".");
                }

                Flushes.Add(path);
            }

            private static string Norm(string path)
            {
                return Path.GetFullPath(path);
            }
        }

        /// <summary>
        /// An in-memory stream that records its own release and how much of it
        /// was read.
        /// </summary>
        private sealed class RecordingStream : MemoryStream
        {
            internal RecordingStream(byte[] content)
            {
                if (content.Length > 0)
                {
                    base.Write(content, 0, content.Length);
                }

                base.Position = 0;
            }

            internal int BytesRead { get; private set; }

            internal bool Disposed { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int read = base.Read(buffer, offset, count);
                BytesRead += read;
                return read;
            }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
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
