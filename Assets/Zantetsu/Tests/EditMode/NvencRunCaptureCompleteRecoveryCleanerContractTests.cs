using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the production Phase 0.11 NVENC recovery
    /// CaptureComplete cleaner: admission before any filesystem contact, the
    /// fixed deletion order and its flushes, what may and may not be deleted on
    /// each path, resumption after a partial cleanup, and release on every
    /// path.
    /// </summary>
    /// <remarks>
    /// The filesystem is an in-memory recording fake, so every no-follow status
    /// and residual shape is reachable deterministically on any platform. Two
    /// Windows-only cases run the real backend end to end - one for each
    /// arrival path - and the backend's own NT and P/Invoke details stay with
    /// its own fixture.
    /// </remarks>
    public class NvencRunCaptureCompleteRecoveryCleanerContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

        private const string CaptureIndexName = "capture.index";

        private const string CaptureIndexTemporaryName = "capture.index.tmp";

        private const string PublicationPlanName = "publication.plan";

        private const string ChunksDirectoryName = "chunks";

        private const string RunReadyMarkerName = "run.ready";

        private const string RunInitializationMarkerName = "run.init";

        private const NvencRunCaptureIndexObservationStatus Absent =
            NvencRunCaptureIndexObservationStatus.Absent;

        private const NvencRunCaptureIndexObservationStatus Matches =
            NvencRunCaptureIndexObservationStatus.MatchesAuthoritative;

        private const NvencRunCaptureIndexObservationStatus Invalid =
            NvencRunCaptureIndexObservationStatus.Invalid;

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
                () => new NvencRunCaptureCompleteRecoveryCleaner(null, new FakeFileSystem()));
            Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureCompleteRecoveryCleaner(MakeLayout(), null));
        }

        [Test]
        public void Clean_NullOperation_RejectedWithoutFilesystemContact()
        {
            Harness h = MakeHarness(Matches);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => h.Cleaner.Clean(null));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(h.FileSystem.Calls, Is.Empty);
        }

        [Test]
        public void Clean_OperationWhoseLockWasReleased_RejectedWithoutFilesystemContact()
        {
            Harness h = MakeHarness(Matches);

            ReleaseAllLocks();
            Assert.That(h.Operation.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => h.Cleaner.Clean(h.Operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(h.FileSystem.Calls, Is.Empty);
        }

        [Test]
        public void Clean_ForeignRootLayout_RejectedWithoutFilesystemContact()
        {
            Harness h = MakeHarness(Matches);
            FakeFileSystem fileSystem = new FakeFileSystem();
            NvencRunCaptureCompleteRecoveryCleaner foreign =
                new NvencRunCaptureCompleteRecoveryCleaner(MakeLayout(), fileSystem);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => foreign.Clean(h.Operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(fileSystem.Calls, Is.Empty);
        }

        [Test]
        public void Clean_MissingCapability_RefusesWithoutFilesystemContact()
        {
            Harness withoutNoFollow = MakeHarness(Matches);
            withoutNoFollow.FileSystem.Supported = false;

            Assert.Throws<CaptureArtifactNoFollowUnavailableException>(
                () => withoutNoFollow.Cleaner.Clean(withoutNoFollow.Operation));
            Assert.That(withoutNoFollow.FileSystem.Calls, Is.Empty);

            Harness withoutFlush = MakeHarness(Matches);
            withoutFlush.FileSystem.DirectoryFlushSupported = false;

            Assert.Throws<CaptureArtifactNoFollowUnavailableException>(
                () => withoutFlush.Cleaner.Clean(withoutFlush.Operation));
            Assert.That(withoutFlush.FileSystem.Calls, Is.Empty);
        }

        // ---- The fixed order ----

        [Test]
        public void Clean_AuthoritativeTemporary_DeletesInTheFixedOrderAndFlushesTheRealParents()
        {
            Harness h = MakeHarness(Matches);

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                h.Cleaner.Clean(h.Operation);

            AssertCleaned(result, h);

            // Deletions in the fixed order.
            Assert.That(h.FileSystem.Deletions, Is.EqualTo(new List<string>
            {
                CaptureIndexTemporaryName,
                ChunksDirectoryName,
                PublicationPlanName,
                RunReadyMarkerName,
                RunInitializationMarkerName,
                h.Layout.StagingRunRoot,
            }));

            // Each step ends with a flush of the directory that actually held
            // its entry.
            Assert.That(h.FileSystem.Flushes, Is.EqualTo(new List<string>
            {
                Path.GetFullPath(h.Layout.FinalRunRoot),
                Path.GetFullPath(h.Layout.StagingRunRoot),
                Path.GetFullPath(h.Layout.StagingRunRoot),
                Path.GetFullPath(h.Layout.StagingRunRoot),
                Path.GetFullPath(h.Layout.StagingRunRoot),
                Path.GetFullPath(Path.GetDirectoryName(h.Layout.StagingRunRoot)),
            }));

            AssertPublishedSideUntouched(h);
            AssertEverythingReleased(h);
        }

        // ---- The three healthy temporary shapes ----

        [Test]
        public void Clean_TemporaryAlreadyGone_IsAcceptedAsProcessed()
        {
            Harness h = MakeHarness(Absent);

            AssertCleaned(h.Cleaner.Clean(h.Operation), h);

            Assert.That(h.FileSystem.Deletions, Has.No.Member(CaptureIndexTemporaryName));
            AssertPublishedSideUntouched(h);
        }

        [Test]
        public void Clean_UnusableTemporary_IsDeleted()
        {
            Harness h = MakeHarness(Invalid, temporaryContent: new byte[] { (byte)'{', (byte)'}' });

            AssertCleaned(h.Cleaner.Clean(h.Operation), h);

            Assert.That(h.FileSystem.Deletions[0], Is.EqualTo(CaptureIndexTemporaryName));
            AssertPublishedSideUntouched(h);
        }

        [Test]
        public void Clean_CommittedRunWithoutATemporary_IsCleaned()
        {
            Harness h = MakeHarness(Absent, committed: true);

            Assert.That(h.Operation.HasCommitReceipt, Is.True);
            AssertCleaned(h.Cleaner.Clean(h.Operation), h);
            Assert.That(h.FileSystem.Deletions, Has.No.Member(CaptureIndexTemporaryName));
        }

        // ---- Temporary refusals ----

        [Test]
        public void Clean_TemporaryNoLongerTheAuthoritativeDocument_FailsWithoutDeletingIt()
        {
            Harness h = MakeHarness(Matches, temporaryContent: new byte[] { 9, 9, 9 });

            AssertFailedWithNothingDeleted(h.Cleaner.Clean(h.Operation), h);
        }

        [Test]
        public void Clean_TemporaryPastTheCanonicalLimit_FailsWithoutDeletingIt()
        {
            Harness h = MakeHarness(
                Matches,
                temporaryContent:
                    new byte[CapturePublicationPlanCodec.MaximumCanonicalByteCount + 1]);

            AssertFailedWithNothingDeleted(h.Cleaner.Clean(h.Operation), h);
        }

        [Test]
        public void Clean_TemporaryThatCannotBeObserved_FailsWithoutDeletingIt()
        {
            foreach (CaptureIndexFileOpenStatus status in new[]
            {
                CaptureIndexFileOpenStatus.InvalidFileKind,
                CaptureIndexFileOpenStatus.EscapesRoot,
                CaptureIndexFileOpenStatus.IoFailure,
            })
            {
                Harness h = MakeHarness(Matches);
                h.FileSystem.SetStatus(h.TemporaryPath, status);

                AssertFailedWithNothingDeleted(h.Cleaner.Clean(h.Operation), h, status.ToString());
            }
        }

        [Test]
        public void Clean_TemporaryReadFailure_FailsWithoutDeletingIt()
        {
            Harness h = MakeHarness(Matches);
            h.FileSystem.FailReadsAt(h.TemporaryPath, new IOException("read failed"));

            AssertFailedWithNothingDeleted(h.Cleaner.Clean(h.Operation), h);
        }

        [Test]
        public void Clean_TemporaryThatTheInspectionNeverClassifiedAsRemovable_FailsWithoutDeletingIt()
        {
            // The inspection saw no temporary, yet one stands there now.
            Harness h = MakeHarness(Absent, temporaryContent: null, temporaryPresentAnyway: true);

            AssertFailedWithNothingDeleted(h.Cleaner.Clean(h.Operation), h);
        }

        [Test]
        public void Clean_CommittedRunWhoseTemporaryStillExists_FailsWhateverItHolds()
        {
            // The commit consumed that exact name, so anything there now
            // belongs to another writer.
            Harness authoritative = MakeHarness(
                Absent, committed: true, temporaryPresentAnyway: true);
            AssertFailedWithNothingDeleted(
                authoritative.Cleaner.Clean(authoritative.Operation), authoritative);

            Harness garbage = MakeHarness(
                Absent,
                committed: true,
                temporaryContent: new byte[] { 1 },
                temporaryPresentAnyway: true);
            AssertFailedWithNothingDeleted(garbage.Cleaner.Clean(garbage.Operation), garbage);
        }

        // ---- Staging refusals ----

        [Test]
        public void Clean_NonEmptyChunksDirectory_LeavesItsContentsAndTheLaterTargets()
        {
            Harness h = MakeHarness(Absent);
            string leftover = Path.Combine(h.ChunksPath, "chunk-0.nvenc-idr-chunk-v1.h264");
            h.FileSystem.AddFile(leftover, new byte[] { 4, 5, 6 });

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                h.Cleaner.Clean(h.Operation);

            Assert.That(result.IsFailed, Is.True);
            Assert.That(h.FileSystem.Deletions, Is.Empty);
            Assert.That(h.FileSystem.Exists(leftover), Is.True);
            Assert.That(h.FileSystem.DirectoryExists(h.ChunksPath), Is.True);
            Assert.That(h.FileSystem.Exists(h.PlanPath), Is.True);
            AssertPublishedSideUntouched(h);
            AssertEverythingReleased(h);
        }

        [Test]
        public void Clean_PublicationPlanThatIsNotTheAuthoritativeOne_LeavesItAndTheLaterTargets()
        {
            Harness h = MakeHarness(Absent);
            h.FileSystem.SetFile(h.PlanPath, new byte[] { (byte)'{', (byte)'}' });

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                h.Cleaner.Clean(h.Operation);

            Assert.That(result.IsFailed, Is.True);

            // The chunks directory was already removed by this attempt; the
            // plan and everything after it stays.
            Assert.That(h.FileSystem.Deletions, Is.EqualTo(new List<string> { ChunksDirectoryName }));
            Assert.That(h.FileSystem.Exists(h.PlanPath), Is.True);
            Assert.That(h.FileSystem.Exists(h.ReadyPath), Is.True);
            Assert.That(h.FileSystem.Exists(h.InitPath), Is.True);
            Assert.That(h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot), Is.True);
            AssertPublishedSideUntouched(h);
        }

        [Test]
        public void Clean_ReadyMarkerOfAnotherRun_LeavesTheMarkers()
        {
            Harness h = MakeHarness(Absent);
            CaptureRunRootLayout foreignLayout = MakeLayout();
            CaptureRunInitializationDocumentSet foreign =
                CaptureRunInitializationDocumentSetFactory.Create(foreignLayout, InitId);
            h.FileSystem.SetFile(h.ReadyPath, foreign.GetStagingReadyBytes());

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                h.Cleaner.Clean(h.Operation);

            Assert.That(result.IsFailed, Is.True);
            Assert.That(h.FileSystem.Deletions, Has.No.Member(RunReadyMarkerName));
            Assert.That(h.FileSystem.Exists(h.ReadyPath), Is.True);
            Assert.That(h.FileSystem.Exists(h.InitPath), Is.True);
        }

        [Test]
        public void Clean_ReadyMarkerBoundToAnotherInitialization_LeavesTheMarkers()
        {
            Harness h = MakeHarness(Absent);

            // A ready marker of this Run whose staging init hash belongs to a
            // different initialization document.
            CaptureRunInitializationDocumentSet other =
                CaptureRunInitializationDocumentSetFactory.Create(
                    h.Layout, "fedcba9876543210fedcba9876543210");
            h.FileSystem.SetFile(h.InitPath, other.GetStagingInitializationBytes());

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                h.Cleaner.Clean(h.Operation);

            Assert.That(result.IsFailed, Is.True);
            Assert.That(h.FileSystem.Deletions, Has.No.Member(RunReadyMarkerName));
            Assert.That(h.FileSystem.Deletions, Has.No.Member(RunInitializationMarkerName));
            Assert.That(h.FileSystem.Exists(h.ReadyPath), Is.True);
            Assert.That(h.FileSystem.Exists(h.InitPath), Is.True);
        }

        [Test]
        public void Clean_ReadyMarkerLeftWithoutItsInitializationMarker_Fails()
        {
            // A residual shape that cannot be true: the ready marker binds the
            // initialization marker, so it cannot outlive it.
            Harness h = MakeHarness(Absent);
            h.FileSystem.RemoveFile(h.InitPath);

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                h.Cleaner.Clean(h.Operation);

            Assert.That(result.IsFailed, Is.True);
            Assert.That(h.FileSystem.Exists(h.ReadyPath), Is.True);
            Assert.That(h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot), Is.True);
        }

        [Test]
        public void Clean_UnknownStagingEntry_LeavesItAndTheRunRoot()
        {
            Harness h = MakeHarness(Absent);
            string unknown = Path.Combine(h.Layout.StagingRunRoot, "unexpected.txt");
            h.FileSystem.AddFile(unknown, new byte[] { 7 });

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                h.Cleaner.Clean(h.Operation);

            Assert.That(result.IsFailed, Is.True);
            Assert.That(h.FileSystem.Exists(unknown), Is.True);
            Assert.That(h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot), Is.True);

            // Everything the cleanup is allowed to remove was still removed.
            Assert.That(h.FileSystem.Deletions, Is.EqualTo(new List<string>
            {
                ChunksDirectoryName,
                PublicationPlanName,
                RunReadyMarkerName,
                RunInitializationMarkerName,
            }));
            AssertEverythingReleased(h);
        }

        // ---- A failed flush is retried, not skipped ----

        [Test]
        public void Clean_TemporaryFlushFailure_IsRetriedUntilItSucceeds()
        {
            Harness h = MakeHarness(Matches);
            h.FileSystem.FailFlushesAt(h.Layout.FinalRunRoot, 1);

            // The temporary is deleted, then its directory flush fails.
            NvencRunCaptureCompleteRecoveryCleanupAttemptResult first =
                h.Cleaner.Clean(h.Operation);

            Assert.That(first.IsFailed, Is.True);
            Assert.That(h.FileSystem.Exists(h.TemporaryPath), Is.False);
            Assert.That(h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot), Is.True);

            // The retry finds the temporary already gone and must still flush
            // the directory that held it before calling the step processed.
            h.FileSystem.Flushes.Clear();

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult second =
                h.Cleaner.Clean(h.Operation);

            Assert.That(second.IsCleaned, Is.True);
            Assert.That(h.FileSystem.Flushes[0], Is.EqualTo(Path.GetFullPath(h.Layout.FinalRunRoot)));
            AssertPublishedSideUntouched(h);
            AssertEverythingReleased(h);
        }

        [Test]
        public void Clean_StagingRunRootFlushFailure_IsRetriedUntilItSucceeds()
        {
            Harness h = MakeHarness(Absent);
            string parent = Path.GetDirectoryName(h.Layout.StagingRunRoot);
            h.FileSystem.FailFlushesAt(parent, 1);

            // Everything is removed, then the parent directory flush fails.
            NvencRunCaptureCompleteRecoveryCleanupAttemptResult first =
                h.Cleaner.Clean(h.Operation);

            Assert.That(first.IsFailed, Is.True);
            Assert.That(h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot), Is.False);

            // The retry finds the Run root already gone and must still flush
            // its parent before reporting success.
            h.FileSystem.Flushes.Clear();

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult second =
                h.Cleaner.Clean(h.Operation);

            Assert.That(second.IsCleaned, Is.True);
            Assert.That(h.FileSystem.Flushes, Has.Member(Path.GetFullPath(parent)));
            AssertEverythingReleased(h);
        }

        [Test]
        public void Clean_AllStagingTargetsAlreadyDeleted_FlushesTheStagingRootOncePerStep()
        {
            // A previous attempt removed the chunks directory, the plan, and
            // both markers but may never have flushed, so all four steps flush
            // the staging Run root again before it is removed.
            Harness h = MakeHarness(Absent);
            RemoveEveryStagingTarget(h);

            Assert.That(h.Cleaner.Clean(h.Operation).IsCleaned, Is.True);

            Assert.That(h.FileSystem.Flushes, Is.EqualTo(new List<string>
            {
                Path.GetFullPath(h.Layout.FinalRunRoot),
                Path.GetFullPath(h.Layout.StagingRunRoot),
                Path.GetFullPath(h.Layout.StagingRunRoot),
                Path.GetFullPath(h.Layout.StagingRunRoot),
                Path.GetFullPath(h.Layout.StagingRunRoot),
                Path.GetFullPath(Path.GetDirectoryName(h.Layout.StagingRunRoot)),
            }));
            AssertEverythingReleased(h);
        }

        [Test]
        public void Clean_EachAlreadyDeletedStagingStep_DemandsItsOwnFlush()
        {
            // Failing only the n-th flush of the staging Run root shows that
            // the n-th staging step - chunks, plan, ready, init - is the one
            // asking for it: the attempt stops there, having flushed n-1
            // times, and never reaches the Run root's removal.
            for (int step = 1; step <= 4; step++)
            {
                Harness h = MakeHarness(Absent);
                RemoveEveryStagingTarget(h);
                h.FileSystem.FailFlushOccurrence(h.Layout.StagingRunRoot, step);

                Assert.That(h.Cleaner.Clean(h.Operation).IsFailed, Is.True, "step " + step);

                Assert.That(
                    h.FileSystem.FlushAttempts(h.Layout.StagingRunRoot),
                    Is.EqualTo(step),
                    "step " + step);
                Assert.That(
                    h.FileSystem.SuccessfulFlushes(h.Layout.StagingRunRoot),
                    Is.EqualTo(step - 1),
                    "step " + step);
                Assert.That(
                    h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot),
                    Is.True,
                    "step " + step);
                AssertEverythingReleased(h);
            }
        }

        [Test]
        public void Clean_InitializationMarkerReleaseFailureDuringVerification_StopsBeforeDeleting()
        {
            // The ready marker verification reads the initialization marker
            // and then releases it. A release failure there is the outcome: no
            // marker is deleted and no receipt is issued.
            Harness h = MakeHarness(Absent);
            h.FileSystem.FailReleaseAt(h.InitPath, new IOException("release failed"));

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                h.Cleaner.Clean(h.Operation);

            Assert.That(result.IsFailed, Is.True);
            Assert.That(result.Receipt, Is.Null);
            Assert.That(h.FileSystem.Deletions, Has.No.Member(RunReadyMarkerName));
            Assert.That(h.FileSystem.Deletions, Has.No.Member(RunInitializationMarkerName));
            Assert.That(h.FileSystem.Exists(h.ReadyPath), Is.True);
            Assert.That(h.FileSystem.Exists(h.InitPath), Is.True);
            Assert.That(h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot), Is.True);
        }

        // ---- Resumption ----

        [Test]
        public void Clean_ResumesFromEveryPartialState()
        {
            // Each state is what a previous attempt would have left after
            // failing at the next step.
            for (int completed = 1; completed <= 6; completed++)
            {
                Harness h = MakeHarness(Matches);

                if (completed >= 1)
                {
                    h.FileSystem.RemoveFile(h.TemporaryPath);
                }

                if (completed >= 2)
                {
                    h.FileSystem.RemoveDirectory(h.ChunksPath);
                }

                if (completed >= 3)
                {
                    h.FileSystem.RemoveFile(h.PlanPath);
                }

                if (completed >= 4)
                {
                    h.FileSystem.RemoveFile(h.ReadyPath);
                }

                if (completed >= 5)
                {
                    h.FileSystem.RemoveFile(h.InitPath);
                }

                if (completed >= 6)
                {
                    h.FileSystem.RemoveDirectory(h.Layout.StagingRunRoot);
                }

                NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                    h.Cleaner.Clean(h.Operation);

                Assert.That(result.IsCleaned, Is.True, "resumed after step " + completed);
                Assert.That(
                    h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot),
                    Is.False,
                    "resumed after step " + completed);
                AssertPublishedSideUntouched(h);
                AssertEverythingReleased(h);
            }
        }

        // ---- Result, receipt, and shape ----

        [Test]
        public void Clean_CleanedAndFailed_CarryTheExactCleanerAndOperation()
        {
            Harness cleaned = MakeHarness(Absent);
            NvencRunCaptureCompleteRecoveryCleanupAttemptResult cleanedResult =
                cleaned.Cleaner.Clean(cleaned.Operation);

            Assert.That(cleanedResult.IsCleaned, Is.True);
            Assert.That(cleanedResult.IsIssuedFor(cleaned.Cleaner, cleaned.Operation), Is.True);
            Assert.That(cleanedResult.Receipt, Is.Not.Null);
            Assert.That(
                cleanedResult.Receipt.IsIssuedFor(cleaned.Cleaner, cleaned.Operation), Is.True);
            Assert.That(ReferenceEquals(cleanedResult.Operation, cleaned.Operation), Is.True);
            Assert.That(ReferenceEquals(
                    cleanedResult.AuthoritativePlan, cleaned.Operation.AuthoritativePlan),
                Is.True);

            Harness failed = MakeHarness(Matches, temporaryContent: new byte[] { 3 });
            NvencRunCaptureCompleteRecoveryCleanupAttemptResult failedResult =
                failed.Cleaner.Clean(failed.Operation);

            Assert.That(failedResult.IsFailed, Is.True);
            Assert.That(failedResult.Receipt, Is.Null);
            Assert.That(failedResult.IsIssuedFor(failed.Cleaner, failed.Operation), Is.True);
        }

        [Test]
        public void Cleaner_HoldsOnlyTheRootLayoutAndTheFileSystem()
        {
            Type type = typeof(NvencRunCaptureCompleteRecoveryCleaner);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            Assert.That(fields.Length, Is.EqualTo(2));

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
        public void Backend_ExistingFinalIndexWithTemporary_CleansThroughRealHandles()
        {
            RequireWindows();

            SandboxHarness h = MakeSandboxHarness(Matches, committed: false);

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                h.Cleaner.Clean(h.Operation);

            Assert.That(result.IsCleaned, Is.True);
            Assert.That(File.Exists(h.TemporaryPath), Is.False);
            Assert.That(Directory.Exists(h.Layout.StagingRunRoot), Is.False);
            h.AssertPublishedSideUntouched();
        }

        [Test]
        public void Backend_CommittedRunWithoutTemporary_CleansThroughRealHandles()
        {
            RequireWindows();

            SandboxHarness h = MakeSandboxHarness(Absent, committed: true);

            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result =
                h.Cleaner.Clean(h.Operation);

            Assert.That(result.IsCleaned, Is.True);
            Assert.That(Directory.Exists(h.Layout.StagingRunRoot), Is.False);
            h.AssertPublishedSideUntouched();
        }

        // ---- Fixture helpers ----

        private static void RequireWindows()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore("The cleanup filesystem requires Windows handles.");
            }
        }

        private static void AssertCleaned(
            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result, Harness h)
        {
            Assert.That(result.IsCleaned, Is.True);
            Assert.That(result.Receipt, Is.Not.Null);
            Assert.That(result.IsIssuedFor(h.Cleaner, h.Operation), Is.True);
            Assert.That(h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot), Is.False);
        }

        private static void AssertFailedWithNothingDeleted(
            NvencRunCaptureCompleteRecoveryCleanupAttemptResult result,
            Harness h,
            string message = null)
        {
            Assert.That(result.IsFailed, Is.True, message);
            Assert.That(result.Receipt, Is.Null, message);

            // The first step refused, so nothing at all was removed.
            Assert.That(h.FileSystem.Deletions, Is.Empty, message);
            Assert.That(h.FileSystem.Exists(h.PlanPath), Is.True, message);
            Assert.That(h.FileSystem.DirectoryExists(h.ChunksPath), Is.True, message);
            Assert.That(h.FileSystem.DirectoryExists(h.Layout.StagingRunRoot), Is.True, message);
            AssertPublishedSideUntouched(h);
            AssertEverythingReleased(h);
        }

        private static void AssertPublishedSideUntouched(Harness h)
        {
            Assert.That(h.FileSystem.Exists(h.FinalChunkPath), Is.True);
            Assert.That(h.FileSystem.Read(h.FinalChunkPath), Is.EqualTo(h.FinalChunkBytes));
            Assert.That(h.FileSystem.Exists(h.CaptureIndexPath), Is.True);
            Assert.That(h.FileSystem.Read(h.CaptureIndexPath), Is.EqualTo(h.CaptureIndexBytes));

            // Neither published target is ever even opened.
            Assert.That(h.FileSystem.OpenedPaths, Has.No.Member(h.CaptureIndexPath));
            Assert.That(h.FileSystem.OpenedPaths, Has.No.Member(h.FinalChunkPath));
            Assert.That(h.FileSystem.Deletions, Has.No.Member(CaptureIndexName));
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

        private static void RemoveEveryStagingTarget(Harness h)
        {
            h.FileSystem.RemoveDirectory(h.ChunksPath);
            h.FileSystem.RemoveFile(h.PlanPath);
            h.FileSystem.RemoveFile(h.ReadyPath);
            h.FileSystem.RemoveFile(h.InitPath);
        }

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        /// <summary>
        /// One Run over the in-memory filesystem, seeded as a recovered Run
        /// looks right after its CaptureComplete was accepted.
        /// </summary>
        private Harness MakeHarness(
            NvencRunCaptureIndexObservationStatus temporaryStatus,
            bool committed = false,
            byte[] temporaryContent = null,
            bool temporaryPresentAnyway = false)
        {
            CaptureRunRootLayout layout = MakeLayout();
            NvencRunCaptureCompleteRecoveryCleanupOperation operation = MakeOperation(
                layout, temporaryStatus, committed);
            FakeFileSystem fileSystem = new FakeFileSystem();

            Harness harness = new Harness(
                layout,
                operation,
                fileSystem,
                new NvencRunCaptureCompleteRecoveryCleaner(layout, fileSystem));

            harness.Seed(
                temporaryStatus,
                temporaryContent,
                temporaryPresentAnyway,
                CapturePublicationPlanCodec.SerializeCanonical(operation.AuthoritativePlan));

            return harness;
        }

        private SandboxHarness MakeSandboxHarness(
            NvencRunCaptureIndexObservationStatus temporaryStatus, bool committed)
        {
            string sandbox = Path.Combine(
                Path.GetTempPath(), "zantetsuken-recovery-cleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                Path.Combine(sandbox, "staging"), Path.Combine(sandbox, "final"), 1);
            NvencRunCaptureCompleteRecoveryCleanupOperation operation = MakeOperation(
                layout, temporaryStatus, committed);

            SandboxHarness harness = new SandboxHarness(
                layout,
                operation,
                new NvencRunCaptureCompleteRecoveryCleaner(
                    layout, CaptureIndexCommitFileSystem.Create()));

            harness.Populate(
                temporaryStatus,
                CapturePublicationPlanCodec.SerializeCanonical(operation.AuthoritativePlan));

            return harness;
        }

        private NvencRunCaptureCompleteRecoveryCleanupOperation MakeOperation(
            CaptureRunRootLayout layout,
            NvencRunCaptureIndexObservationStatus temporaryStatus,
            bool committed)
        {
            NvencRunCaptureIndexRecoveryDecision decision = NvencRunCaptureIndexRecoveryClassifier.Classify(
                new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                    new NvencRunCaptureIndexRecoveryInspectionOperation(
                        MakeRecoveryDecision(layout)),
                    committed ? Absent : Matches,
                    temporaryStatus));

            NvencRunCaptureCompleteRecoveryReceipt captureComplete =
                new NvencRunCaptureCompleteRecoveryOrchestrationCoordinator(
                        new NvencRunCaptureIndexRecoveryCommitOrchestrationCoordinator(
                            new NvencRunCaptureIndexRecoveryCommitExecutionCoordinator(
                                new FakeCommitter())),
                        new NvencRunCaptureCompleteRecoveryExecutionCoordinator(
                            new NvencRunCaptureCompleteRecoveryCompleter()))
                    .Execute(decision);

            Assert.That(captureComplete.HasCommitReceipt, Is.EqualTo(committed));

            return NvencRunCaptureCompleteRecoveryCleanupOperation.Create(captureComplete);
        }

        private NvencRunPublicationRecoveryDecision MakeRecoveryDecision(CaptureRunRootLayout layout)
        {
            NvencRunPublicationRecoveryInspectionOperation operation =
                new NvencRunPublicationRecoveryInspectionOperation(
                    MakeRecoveryOutcome(layout), layout);
            CapturePublicationPlan plan = MakePlan(operation);

            return NvencRunPublicationRecoveryClassifier.Classify(
                new NvencRunPublicationRecoveryInspectionSnapshot(
                    operation,
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
                NvencRunChunkArtifactDescriptorFactory.Create(ArtifactId, ChunkByteLength, Hash64),
            };

            CaptureFrameEvidenceEntry[] entries = new CaptureFrameEvidenceEntry[3];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = new CaptureFrameEvidenceEntry(i + 1, new[] { ArtifactId });
            }

            return new CapturePublicationPlan(
                operation.TestRunId, operation.RunInitializationId, Hash64, artifacts, entries);
        }

        /// <summary>
        /// Drives the existing initialization recovery orchestration to a
        /// publication-recovery outcome that still holds its lock, through the
        /// ordinary constructors only.
        /// </summary>
        private CaptureRunInitializationOpenOutcome MakeRecoveryOutcome(CaptureRunRootLayout layout)
        {
            CaptureRunMarkerBinding binding = CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId, InitId, layout.StagingRunRootSha256, layout.FinalRunRootSha256);

            CaptureRunInitializationRootObservation staging = MakeRootObservation(
                CaptureRunRootRole.Staging,
                binding.StagingInitialization,
                binding.StagingReady,
                hasNonMarkerEntry: true);
            CaptureRunInitializationRootObservation final = MakeRootObservation(
                CaptureRunRootRole.Final,
                binding.FinalInitialization,
                binding.FinalReady,
                hasNonMarkerEntry: false);

            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator =
                new CaptureRunInitializationRecoveryOrchestrationCoordinator(
                    new FakeRecoveryInspector(staging, final),
                    new CaptureRunInitializationRecoveryExecutionCoordinator(
                        new FakeCleanupBackend(), new FakeProvisioner(), new FakeMarkerWriter()));

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            CaptureRunLockLease lease = new CaptureRunLockLease(
                pathSet,
                new FakeHandle(pathSet.FirstLockPath),
                new FakeHandle(pathSet.SecondLockPath));
            CaptureRunInitializationSessionOwnershipLease owner =
                CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
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
            string root = Path.DirectorySeparatorChar == '\\' ? "C:\\zantetsuken-fake" : "/zantetsuken-fake";
            _runIds++;

            return new CaptureRunRootLayout(
                Path.Combine(root, "staging-" + _runIds),
                Path.Combine(root, "final-" + _runIds),
                1);
        }

        /// <summary>One Run over the in-memory filesystem.</summary>
        private sealed class Harness
        {
            internal Harness(
                CaptureRunRootLayout layout,
                NvencRunCaptureCompleteRecoveryCleanupOperation operation,
                FakeFileSystem fileSystem,
                NvencRunCaptureCompleteRecoveryCleaner cleaner)
            {
                Layout = layout;
                Operation = operation;
                FileSystem = fileSystem;
                Cleaner = cleaner;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal NvencRunCaptureCompleteRecoveryCleanupOperation Operation { get; }

            internal FakeFileSystem FileSystem { get; }

            internal NvencRunCaptureCompleteRecoveryCleaner Cleaner { get; }

            internal byte[] FinalChunkBytes { get; } = new byte[] { 10, 20, 30, 40 };

            internal byte[] CaptureIndexBytes { get; } = new byte[] { 1, 2, 3, 4, 5 };

            internal string ChunksPath => Path.Combine(Layout.StagingRunRoot, ChunksDirectoryName);

            internal string PlanPath => Path.Combine(Layout.StagingRunRoot, PublicationPlanName);

            internal string ReadyPath => Path.Combine(Layout.StagingRunRoot, RunReadyMarkerName);

            internal string InitPath =>
                Path.Combine(Layout.StagingRunRoot, RunInitializationMarkerName);

            internal string TemporaryPath =>
                Path.Combine(Layout.FinalRunRoot, CaptureIndexTemporaryName);

            internal string CaptureIndexPath => Path.Combine(Layout.FinalRunRoot, CaptureIndexName);

            internal string FinalChunkPath => Path.Combine(
                Layout.FinalRunRoot,
                NvencRunChunkArtifactDescriptorFactory.FinalRelativePath.Replace(
                    '/', Path.DirectorySeparatorChar));

            internal void Seed(
                NvencRunCaptureIndexObservationStatus temporaryStatus,
                byte[] temporaryContent,
                bool temporaryPresentAnyway,
                byte[] canonicalPlan)
            {
                CaptureRunInitializationDocumentSet documents =
                    CaptureRunInitializationDocumentSetFactory.Create(Layout, InitId);

                FileSystem.AddDirectory(Path.GetDirectoryName(Layout.StagingRunRoot));
                FileSystem.AddDirectory(Layout.StagingRunRoot);
                FileSystem.AddDirectory(ChunksPath);
                FileSystem.AddFile(PlanPath, canonicalPlan);
                FileSystem.AddFile(ReadyPath, documents.GetStagingReadyBytes());
                FileSystem.AddFile(InitPath, documents.GetStagingInitializationBytes());

                FileSystem.AddDirectory(Path.GetDirectoryName(Layout.FinalRunRoot));
                FileSystem.AddDirectory(Layout.FinalRunRoot);
                FileSystem.AddDirectory(Path.Combine(Layout.FinalRunRoot, ChunksDirectoryName));
                FileSystem.AddFile(FinalChunkPath, FinalChunkBytes);
                FileSystem.AddFile(CaptureIndexPath, CaptureIndexBytes);

                bool present = temporaryPresentAnyway
                    || temporaryStatus == NvencRunCaptureIndexObservationStatus.MatchesAuthoritative
                    || temporaryStatus == NvencRunCaptureIndexObservationStatus.Invalid;
                if (!present)
                {
                    return;
                }

                byte[] content = temporaryContent
                    ?? (temporaryStatus == NvencRunCaptureIndexObservationStatus.Invalid
                        ? new byte[] { (byte)'{', (byte)'}' }
                        : canonicalPlan);
                FileSystem.AddFile(TemporaryPath, content);
            }
        }

        /// <summary>One Run over a real temporary tree and the real backend.</summary>
        private sealed class SandboxHarness
        {
            private byte[] _finalChunkBytes;
            private byte[] _captureIndexBytes;

            internal SandboxHarness(
                CaptureRunRootLayout layout,
                NvencRunCaptureCompleteRecoveryCleanupOperation operation,
                NvencRunCaptureCompleteRecoveryCleaner cleaner)
            {
                Layout = layout;
                Operation = operation;
                Cleaner = cleaner;
            }

            internal CaptureRunRootLayout Layout { get; }

            internal NvencRunCaptureCompleteRecoveryCleanupOperation Operation { get; }

            internal NvencRunCaptureCompleteRecoveryCleaner Cleaner { get; }

            internal string TemporaryPath =>
                Path.Combine(Layout.FinalRunRoot, CaptureIndexTemporaryName);

            internal string CaptureIndexPath => Path.Combine(Layout.FinalRunRoot, CaptureIndexName);

            internal string FinalChunkPath => Path.Combine(
                Layout.FinalRunRoot,
                NvencRunChunkArtifactDescriptorFactory.FinalRelativePath.Replace(
                    '/', Path.DirectorySeparatorChar));

            internal void Populate(
                NvencRunCaptureIndexObservationStatus temporaryStatus, byte[] canonicalPlan)
            {
                CaptureRunInitializationDocumentSet documents =
                    CaptureRunInitializationDocumentSetFactory.Create(Layout, InitId);

                Directory.CreateDirectory(Path.Combine(Layout.StagingRunRoot, ChunksDirectoryName));
                Directory.CreateDirectory(Path.GetDirectoryName(FinalChunkPath));

                File.WriteAllBytes(
                    Path.Combine(Layout.StagingRunRoot, PublicationPlanName), canonicalPlan);
                File.WriteAllBytes(
                    Path.Combine(Layout.StagingRunRoot, RunReadyMarkerName),
                    documents.GetStagingReadyBytes());
                File.WriteAllBytes(
                    Path.Combine(Layout.StagingRunRoot, RunInitializationMarkerName),
                    documents.GetStagingInitializationBytes());

                _finalChunkBytes = new byte[] { 10, 20, 30, 40 };
                _captureIndexBytes = new byte[] { 1, 2, 3, 4, 5 };
                File.WriteAllBytes(FinalChunkPath, _finalChunkBytes);
                File.WriteAllBytes(CaptureIndexPath, _captureIndexBytes);

                if (temporaryStatus == NvencRunCaptureIndexObservationStatus.MatchesAuthoritative)
                {
                    File.WriteAllBytes(TemporaryPath, canonicalPlan);
                }
            }

            internal void AssertPublishedSideUntouched()
            {
                Assert.That(File.Exists(FinalChunkPath), Is.True);
                Assert.That(File.ReadAllBytes(FinalChunkPath), Is.EqualTo(_finalChunkBytes));
                Assert.That(File.Exists(CaptureIndexPath), Is.True);
                Assert.That(File.ReadAllBytes(CaptureIndexPath), Is.EqualTo(_captureIndexBytes));
                Assert.That(Sha256Hex(File.ReadAllBytes(CaptureIndexPath)),
                    Is.EqualTo(Sha256Hex(_captureIndexBytes)));
            }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes));
            }
        }

        /// <summary>
        /// An in-memory no-follow cleanup filesystem that records the call
        /// order, the deleted entries, and the flushed directories. Absence is
        /// the default, and any status can be forced for one exact path.
        /// </summary>
        private sealed class FakeFileSystem : ICaptureCompleteCleanupFileSystem
        {
            private readonly HashSet<string> _directories =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, byte[]> _files =
                new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, CaptureIndexFileOpenStatus> _statuses =
                new Dictionary<string, CaptureIndexFileOpenStatus>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, Exception> _readFailures =
                new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, Exception> _releaseFailures =
                new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _flushFailures =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _flushOrdinalFailures =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, int> _flushAttempts =
                new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<CaptureIndexCommitDirectory, string> _directoryPaths =
                new Dictionary<CaptureIndexCommitDirectory, string>();
            private readonly Dictionary<CaptureIndexCommitFile, string> _filePaths =
                new Dictionary<CaptureIndexCommitFile, string>();

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

            internal void SetStatus(string path, CaptureIndexFileOpenStatus status)
            {
                _statuses[Norm(path)] = status;
            }

            internal void FailReadsAt(string path, Exception failure)
            {
                _readFailures[Norm(path)] = failure;
            }

            internal void FailReleaseAt(string path, Exception failure)
            {
                _releaseFailures[Norm(path)] = failure;
            }

            /// <summary>Fails the next <paramref name="times"/> flushes of one directory.</summary>
            internal void FailFlushesAt(string path, int times)
            {
                _flushFailures[Norm(path)] = times;
            }

            /// <summary>
            /// Fails only the <paramref name="ordinal"/>-th flush of one
            /// directory, counting from the first flush of this instance.
            /// </summary>
            internal void FailFlushOccurrence(string path, int ordinal)
            {
                _flushOrdinalFailures[Norm(path)] = ordinal;
            }

            internal int FlushAttempts(string path)
            {
                return _flushAttempts.TryGetValue(Norm(path), out int attempts) ? attempts : 0;
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
                if (_readFailures.TryGetValue(path, out Exception failure))
                {
                    stream.ReadFailure = failure;
                }

                if (_releaseFailures.TryGetValue(path, out Exception releaseFailure))
                {
                    stream.DisposeFailure = releaseFailure;
                }

                Streams.Add(stream);
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
                Deletions.Add(
                    string.Equals(Path.GetFileName(path), ChunksDirectoryName, StringComparison.Ordinal)
                        ? ChunksDirectoryName
                        : path);
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

                int attempts = FlushAttempts(path) + 1;
                _flushAttempts[path] = attempts;

                if (_flushFailures.TryGetValue(path, out int remaining) && remaining > 0)
                {
                    _flushFailures[path] = remaining - 1;
                    throw new IOException("Failed to flush " + path + ".");
                }

                if (_flushOrdinalFailures.TryGetValue(path, out int ordinal) && ordinal == attempts)
                {
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
        /// An in-memory stream that records its own release and can fail a
        /// read.
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

            internal Exception ReadFailure { get; set; }

            internal Exception DisposeFailure { get; set; }

            internal bool Disposed { get; private set; }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (ReadFailure != null)
                {
                    throw ReadFailure;
                }

                return base.Read(buffer, offset, count);
            }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);

                if (DisposeFailure != null)
                {
                    throw DisposeFailure;
                }
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
