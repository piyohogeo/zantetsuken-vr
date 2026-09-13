using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using NvencAccessUnitCopyStatus = Zantetsu.Observability.NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 production Fresh NVENC CaptureComplete
    /// cleaner: validation before any filesystem contact, the fixed staging
    /// removal order with its parent flushes, the Cleaned and Failed
    /// classifications, and the untouched published side. Uses the fully
    /// published, capture-index-committed, and CaptureComplete-collected Run
    /// pipeline over a real Windows sandbox, with a recording decorator around
    /// the real filesystem backend; no real GPU, NVENC, sleep, or short
    /// negative wait is used.
    /// </summary>
    public class NvencRunCaptureCompleteCleanerContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string ForeignInitId = "fedcba9876543210fedcba9876543210";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string PublicationPlanName = "publication.plan";

        private const string ChunksDirectoryName = "chunks";

        private const string RunReadyMarkerName = "run.ready";

        private const string RunInitializationMarkerName = "run.init";

        private const string CaptureIndexName = "capture.index";

        private const string ChunkFileName = "chunk-0.nvenc-idr-chunk-v1.h264";

        private readonly List<string> _sandboxes = new List<string>();

        [TearDown]
        public void TearDown()
        {
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

        // ---- Construction ----

        [Test]
        public void Constructor_NullRootLayoutOrFileSystem_Rejected()
        {
            ArgumentNullException nullLayout = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureCompleteCleaner(null, new RecordingFileSystem(null)));
            Assert.That(nullLayout.ParamName, Is.EqualTo("rootLayout"));

            ArgumentNullException nullFileSystem = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureCompleteCleaner(MakeLayout(), null));
            Assert.That(nullFileSystem.ParamName, Is.EqualTo("fileSystem"));

            // The layout's own constructor validates every invariant it
            // guarantees, so an invalid instance cannot be built through it;
            // the cleaner's IsValid guard stays as defence in depth and is
            // deliberately not exercised by fabricating a corrupted layout.
            Assert.That(MakeLayout().IsValid, Is.True);
        }

        [Test]
        public void Constructor_DoesNotContactTheFilesystem()
        {
            RecordingFileSystem fileSystem = new RecordingFileSystem(null);
            NvencRunCaptureCompleteCleaner cleaner =
                new NvencRunCaptureCompleteCleaner(MakeLayout(), fileSystem);

            Assert.That(cleaner, Is.Not.Null);
            AssertNoFilesystemContact(fileSystem);
        }

        // ---- Validation before any filesystem contact ----

        [Test]
        public void Clean_NullOperation_ArgumentNullException_NoFilesystemContact()
        {
            RecordingFileSystem fileSystem = new RecordingFileSystem(null);
            NvencRunCaptureCompleteCleaner cleaner =
                new NvencRunCaptureCompleteCleaner(MakeLayout(), fileSystem);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => cleaner.Clean(null));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            AssertNoFilesystemContact(fileSystem);
        }

        [Test]
        public void Clean_InvalidOperation_ArgumentException_NoFilesystemContact()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();

            using (Harness h = Harness.Create(sandbox.Layout))
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h, sandbox);

                RecordingFileSystem fileSystem = new RecordingFileSystem(null);
                NvencRunCaptureCompleteCleaner cleaner =
                    new NvencRunCaptureCompleteCleaner(sandbox.Layout, fileSystem);

                // A poisoned process invalidates the operation through the
                // ordinary API.
                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(operation.IsValid, Is.False);

                ArgumentException ex = Assert.Throws<ArgumentException>(() => cleaner.Clean(operation));
                Assert.That(ex.ParamName, Is.EqualTo("operation"));
                AssertNoFilesystemContact(fileSystem);
                sandbox.AssertStagingIntact();
            }
        }

        [Test]
        public void Clean_ForeignRootLayout_ArgumentException_NoFilesystemContact()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();

            using (Harness h = Harness.Create(sandbox.Layout))
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h, sandbox);

                // A distinct root layout instance is a foreign Run and must be
                // rejected by exact reference, before any filesystem contact.
                RecordingFileSystem fileSystem = new RecordingFileSystem(null);
                NvencRunCaptureCompleteCleaner cleaner =
                    new NvencRunCaptureCompleteCleaner(MakeLayout(), fileSystem);

                ArgumentException ex = Assert.Throws<ArgumentException>(() => cleaner.Clean(operation));
                Assert.That(ex.ParamName, Is.EqualTo("operation"));
                AssertNoFilesystemContact(fileSystem);
                sandbox.AssertStagingIntact();
            }
        }

        [Test]
        public void Clean_UnsupportedCapability_Throws_BeforeAnySideEffect()
        {
            RequireWindows();

            // Either capability alone is insufficient: both are preflighted
            // together before the first deletion.
            AssertCapabilityRejected(noFollow: false, directoryFlush: true);
            AssertCapabilityRejected(noFollow: true, directoryFlush: false);
        }

        // ---- Cleaned ----

        [Test]
        public void Clean_Success_RemovesStagingInTheFixedOrder_OnceEach()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();

            using (Harness h = Harness.Create(sandbox.Layout))
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h, sandbox);

                RecordingFileSystem fileSystem = new RecordingFileSystem(CaptureIndexCommitFileSystem.Create());
                NvencRunCaptureCompleteCleaner cleaner =
                    new NvencRunCaptureCompleteCleaner(sandbox.Layout, fileSystem);

                NvencRunCaptureCompleteCleanupAttemptResult result = cleaner.Clean(operation);

                Assert.That(result.IsCleaned, Is.True);

                // The five removals happen once each, in the fixed order, and
                // each is followed by a flush of the directory whose entry it
                // changed.
                Assert.That(fileSystem.Mutations, Is.EqualTo(new[]
                {
                    "delete:" + PublicationPlanName,
                    "flush:" + sandbox.Layout.StagingRunRoot,
                    "deletedir:" + sandbox.StagingChunks,
                    "flush:" + sandbox.Layout.StagingRunRoot,
                    "delete:" + RunReadyMarkerName,
                    "flush:" + sandbox.Layout.StagingRunRoot,
                    "delete:" + RunInitializationMarkerName,
                    "flush:" + sandbox.Layout.StagingRunRoot,
                    "deletedir:" + sandbox.Layout.StagingRunRoot,
                    "flush:" + sandbox.StagingRunRootParent,
                }));

                // The staging side is gone; its parent survives.
                Assert.That(Directory.Exists(sandbox.Layout.StagingRunRoot), Is.False);
                Assert.That(Directory.Exists(sandbox.StagingRunRootParent), Is.True);

                sandbox.AssertFinalUntouched(fileSystem);
                AssertHandlesReleased(fileSystem);
            }
        }

        [Test]
        public void Clean_Success_ResultAndReceiptCorrelateExactly()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();

            using (Harness h = Harness.Create(sandbox.Layout))
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h, sandbox);

                RecordingFileSystem fileSystem = new RecordingFileSystem(CaptureIndexCommitFileSystem.Create());
                NvencRunCaptureCompleteCleaner cleaner =
                    new NvencRunCaptureCompleteCleaner(sandbox.Layout, fileSystem);

                NvencRunCaptureCompleteCleanupAttemptResult result = cleaner.Clean(operation);

                Assert.That(result.IsCleaned, Is.True);
                Assert.That(result.IsFailed, Is.False);
                Assert.That(result.IsNone, Is.False);
                Assert.That(result.Status, Is.EqualTo(NvencRunCaptureCompleteCleanupStatus.Cleaned));
                Assert.That(result.IsValid, Is.True);
                Assert.That(ReferenceEquals(result.Cleaner, cleaner), Is.True);
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
                Assert.That(result.IsIssuedFor(cleaner, operation), Is.True);

                NvencRunCaptureCompleteCleanupReceipt receipt = result.Receipt;
                Assert.That(receipt, Is.Not.Null);
                Assert.That(receipt.IsValid, Is.True);
                Assert.That(receipt.IsIssuedFor(cleaner, operation), Is.True);
                Assert.That(ReferenceEquals(receipt.Cleaner, cleaner), Is.True);
                Assert.That(ReferenceEquals(receipt.Operation, operation), Is.True);
            }
        }

        [Test]
        public void Clean_Success_LeavesRunRegistryDispositionAndServiceUnchanged()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();

            using (Harness h = Harness.Create(sandbox.Layout))
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h, sandbox);

                NvencRunEvidenceDisposition disposition = h.RunCoordinator.Disposition;
                NvencRunLocalRegistrySlotState slotState = h.Slot.State;
                bool hasRegisteredEntry = h.Slot.HasRegisteredEntry;
                NvencRunChunkContextState contextState = h.Context.State;
                NvencRunPublicationServiceState serviceState = h.Service.State;
                bool serviceReleased = h.RunCoordinator.PublicationServiceReleased;
                bool leaseCreated = h.SessionIssue.OwnershipLease.IsCreated;
                bool leaseValid = h.SessionIssue.IsValid;
                int completerCalls = h.RunCompleter.CallCount;
                int indexCommitterCalls = h.IndexCommitter.CallCount;

                RecordingFileSystem fileSystem = new RecordingFileSystem(CaptureIndexCommitFileSystem.Create());
                Assert.That(new NvencRunCaptureCompleteCleaner(sandbox.Layout, fileSystem)
                    .Clean(operation).IsCleaned, Is.True);

                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(disposition));
                Assert.That(h.Slot.State, Is.EqualTo(slotState));
                Assert.That(h.Slot.HasRegisteredEntry, Is.EqualTo(hasRegisteredEntry));
                Assert.That(h.Context.State, Is.EqualTo(contextState));
                Assert.That(h.Service.State, Is.EqualTo(serviceState));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.EqualTo(serviceReleased));
                Assert.That(h.SessionIssue.OwnershipLease.IsCreated, Is.EqualTo(leaseCreated));
                Assert.That(h.SessionIssue.IsValid, Is.EqualTo(leaseValid));
                Assert.That(h.RunCompleter.CallCount, Is.EqualTo(completerCalls));
                Assert.That(h.IndexCommitter.CallCount, Is.EqualTo(indexCommitterCalls));
                Assert.That(h.State.IsPoisoned, Is.False);
                Assert.That(operation.IsValid, Is.True);
            }
        }

        // ---- Failed ----

        [Test]
        public void Clean_PlanContentMismatch_FailedBeforeAnyDeletion()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();

            using (Harness h = Harness.Create(sandbox.Layout))
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h, sandbox);

                // The staging plan no longer carries the operation's canonical
                // plan bytes.
                byte[] foreign = { 1, 2, 3 };
                File.WriteAllBytes(sandbox.StagingPlanPath, foreign);

                RecordingFileSystem fileSystem = new RecordingFileSystem(CaptureIndexCommitFileSystem.Create());
                NvencRunCaptureCompleteCleaner cleaner =
                    new NvencRunCaptureCompleteCleaner(sandbox.Layout, fileSystem);

                NvencRunCaptureCompleteCleanupAttemptResult result = cleaner.Clean(operation);

                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.Status, Is.EqualTo(NvencRunCaptureCompleteCleanupStatus.Failed));
                Assert.That(result.Receipt, Is.Null);
                Assert.That(ReferenceEquals(result.Cleaner, cleaner), Is.True);
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);

                // Nothing was deleted at all, and the mismatching plan is left
                // in place rather than removed anyway.
                Assert.That(fileSystem.Mutations, Is.Empty);
                sandbox.AssertStagingIntact();
                Assert.That(File.ReadAllBytes(sandbox.StagingPlanPath), Is.EqualTo(foreign));
                sandbox.AssertFinalUntouched(fileSystem);
                AssertHandlesReleased(fileSystem);
            }
        }

        [Test]
        public void Clean_MissingPlan_Failed()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();

            using (Harness h = Harness.Create(sandbox.Layout))
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h, sandbox);

                File.Delete(sandbox.StagingPlanPath);

                RecordingFileSystem fileSystem = new RecordingFileSystem(CaptureIndexCommitFileSystem.Create());
                NvencRunCaptureCompleteCleanupAttemptResult result =
                    new NvencRunCaptureCompleteCleaner(sandbox.Layout, fileSystem).Clean(operation);

                // A missing target is a Failed cleanup, never a silent success.
                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.Receipt, Is.Null);
                Assert.That(fileSystem.Mutations, Is.Empty);
                sandbox.AssertFinalUntouched(fileSystem);
                AssertHandlesReleased(fileSystem);
            }
        }

        [Test]
        public void Clean_NonEmptyChunksDirectory_Failed_ChunkNeverDeleted()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();

            using (Harness h = Harness.Create(sandbox.Layout))
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h, sandbox);

                // A leftover staging chunk: the cleaner confirms emptiness and
                // refuses, and never deletes the chunk itself.
                string leftover = Path.Combine(sandbox.StagingChunks, ChunkFileName);
                byte[] leftoverBytes = { 7, 7, 7, 7 };
                File.WriteAllBytes(leftover, leftoverBytes);

                RecordingFileSystem fileSystem = new RecordingFileSystem(CaptureIndexCommitFileSystem.Create());
                NvencRunCaptureCompleteCleanupAttemptResult result =
                    new NvencRunCaptureCompleteCleaner(sandbox.Layout, fileSystem).Clean(operation);

                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.Receipt, Is.Null);

                // Step 1 already happened and is not rolled back; the chunks
                // directory and its content survive untouched.
                Assert.That(fileSystem.Mutations, Is.EqualTo(new[]
                {
                    "delete:" + PublicationPlanName,
                    "flush:" + sandbox.Layout.StagingRunRoot,
                }));
                Assert.That(fileSystem.DeletedFileNames, Has.No.Member(ChunkFileName));
                Assert.That(File.Exists(leftover), Is.True);
                Assert.That(File.ReadAllBytes(leftover), Is.EqualTo(leftoverBytes));
                Assert.That(Directory.Exists(sandbox.StagingChunks), Is.True);

                sandbox.AssertFinalUntouched(fileSystem);
                AssertHandlesReleased(fileSystem);
            }
        }

        [Test]
        public void Clean_ReadyMarkerIdentityMismatch_FailedMidSequence_NoRetryOrRollback()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();

            using (Harness h = Harness.Create(sandbox.Layout))
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h, sandbox);

                // A ready marker minted for a different Run initialization: the
                // first two steps succeed and the third refuses.
                CaptureRunInitializationDocumentSet foreign =
                    new CaptureRunInitializationDocumentSet(sandbox.Layout, ForeignInitId);
                File.WriteAllBytes(sandbox.StagingReadyPath, foreign.GetStagingReadyBytes());

                RecordingFileSystem fileSystem = new RecordingFileSystem(CaptureIndexCommitFileSystem.Create());
                NvencRunCaptureCompleteCleanupAttemptResult result =
                    new NvencRunCaptureCompleteCleaner(sandbox.Layout, fileSystem).Clean(operation);

                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.Receipt, Is.Null);

                // The two completed steps stand: nothing is restored, retried,
                // or attempted a second time, and the result carries no
                // progress position of its own.
                Assert.That(fileSystem.Mutations, Is.EqualTo(new[]
                {
                    "delete:" + PublicationPlanName,
                    "flush:" + sandbox.Layout.StagingRunRoot,
                    "deletedir:" + sandbox.StagingChunks,
                    "flush:" + sandbox.Layout.StagingRunRoot,
                }));
                Assert.That(File.Exists(sandbox.StagingPlanPath), Is.False);
                Assert.That(Directory.Exists(sandbox.StagingChunks), Is.False);
                Assert.That(File.Exists(sandbox.StagingReadyPath), Is.True);
                Assert.That(File.Exists(sandbox.StagingInitPath), Is.True);
                Assert.That(Directory.Exists(sandbox.Layout.StagingRunRoot), Is.True);

                sandbox.AssertFinalUntouched(fileSystem);
                AssertHandlesReleased(fileSystem);
            }
        }

        [Test]
        public void Clean_InitializationMarkerIdentityMismatch_Failed()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();

            using (Harness h = Harness.Create(sandbox.Layout))
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h, sandbox);

                // An initialization marker for a different Run: the ready
                // marker's peer binding no longer holds, so step 3 refuses
                // before the ready marker is deleted.
                CaptureRunInitializationDocumentSet foreign =
                    new CaptureRunInitializationDocumentSet(sandbox.Layout, ForeignInitId);
                File.WriteAllBytes(sandbox.StagingInitPath, foreign.GetStagingInitializationBytes());

                RecordingFileSystem fileSystem = new RecordingFileSystem(CaptureIndexCommitFileSystem.Create());
                NvencRunCaptureCompleteCleanupAttemptResult result =
                    new NvencRunCaptureCompleteCleaner(sandbox.Layout, fileSystem).Clean(operation);

                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.Receipt, Is.Null);
                Assert.That(fileSystem.DeletedFileNames, Is.EqualTo(new[] { PublicationPlanName }));
                Assert.That(File.Exists(sandbox.StagingReadyPath), Is.True);
                Assert.That(File.Exists(sandbox.StagingInitPath), Is.True);

                sandbox.AssertFinalUntouched(fileSystem);
                AssertHandlesReleased(fileSystem);
            }
        }

        [Test]
        public void Clean_ReadyMarkerFinalInitHashMismatch_Failed_NoFinalContact()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();

            using (Harness h = Harness.Create(sandbox.Layout))
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h, sandbox);

                // A canonical ready marker with the right Run identity and the
                // right StagingInitSha256, but a well-formed FinalInitSha256
                // that is not the one this Run's final initialization marker
                // hashes to. The ready marker is the record that binds the two
                // init hashes, so a half-correct binding must not be accepted.
                CaptureRunMarkerBinding genuine = new CaptureRunMarkerBinding(
                    operation.TestRunId,
                    operation.RunInitializationId,
                    sandbox.Layout.StagingRunRootSha256,
                    sandbox.Layout.FinalRunRootSha256);
                Assert.That(genuine.StagingReady.FinalInitSha256, Is.Not.EqualTo(Hash64));

                CaptureRunReadyMarker forged = new CaptureRunReadyMarker(
                    operation.TestRunId,
                    operation.RunInitializationId,
                    genuine.StagingReady.StagingInitSha256,
                    Hash64);
                File.WriteAllBytes(
                    sandbox.StagingReadyPath, CaptureRunReadyMarkerCodec.SerializeCanonical(forged));

                RecordingFileSystem fileSystem = new RecordingFileSystem(CaptureIndexCommitFileSystem.Create());
                NvencRunCaptureCompleteCleanupAttemptResult result =
                    new NvencRunCaptureCompleteCleaner(sandbox.Layout, fileSystem).Clean(operation);

                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.Receipt, Is.Null);

                // The ready marker and its staging peer both survive; the
                // expected final hash was derived in memory, so the published
                // side was never contacted.
                Assert.That(fileSystem.DeletedFileNames, Has.No.Member(RunReadyMarkerName));
                Assert.That(fileSystem.DeletedFileNames, Has.No.Member(RunInitializationMarkerName));
                Assert.That(File.Exists(sandbox.StagingReadyPath), Is.True);
                Assert.That(File.Exists(sandbox.StagingInitPath), Is.True);

                sandbox.AssertFinalUntouched(fileSystem);
                AssertHandlesReleased(fileSystem);
            }
        }

        // ---- Shape ----

        [Test]
        public void Cleaner_HoldsOnlyTheRootLayoutAndFileSystem_NotDisposable()
        {
            Type type = typeof(NvencRunCaptureCompleteCleaner);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(INvencRunCaptureCompleteCleaner).IsAssignableFrom(type), Is.True);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(2));
            Assert.That(
                fields.Select(field => field.FieldType),
                Is.EquivalentTo(new[]
                {
                    typeof(CaptureRunRootLayout),
                    typeof(ICaptureCompleteCleanupFileSystem),
                }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }

            Assert.That(
                type.GetFields(
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
                    .Where(field => !field.IsLiteral && !field.IsInitOnly),
                Is.Empty,
                "the cleaner must hold no mutable static state.");
        }

        // ---- Cleaner fixture helpers ----

        private void AssertCapabilityRejected(bool noFollow, bool directoryFlush)
        {
            Sandbox sandbox = CreateSandbox();

            using (Harness h = Harness.Create(sandbox.Layout))
            {
                NvencRunCaptureCompleteCleanupOperation operation = PrepareCleanup(h, sandbox);

                RecordingFileSystem fileSystem = new RecordingFileSystem(CaptureIndexCommitFileSystem.Create())
                {
                    SupportedOverride = noFollow,
                    DirectoryFlushSupportedOverride = directoryFlush,
                };
                NvencRunCaptureCompleteCleaner cleaner =
                    new NvencRunCaptureCompleteCleaner(sandbox.Layout, fileSystem);

                // Capability insufficiency is a configuration error, never an
                // ordinary Failed cleanup, and is raised before any side effect.
                Assert.Throws<CaptureArtifactNoFollowUnavailableException>(() => cleaner.Clean(operation));
                AssertNoFilesystemContact(fileSystem);
                sandbox.AssertStagingIntact();
            }
        }

        private static void AssertNoFilesystemContact(RecordingFileSystem fileSystem)
        {
            Assert.That(fileSystem.OpenedDirectoryPaths, Is.Empty);
            Assert.That(fileSystem.OpenedFileNames, Is.Empty);
            Assert.That(fileSystem.Mutations, Is.Empty);
        }

        private static void AssertHandlesReleased(RecordingFileSystem fileSystem)
        {
            foreach (CaptureIndexCommitDirectory directory in fileSystem.OpenedDirectories)
            {
                Assert.That(directory.Handle.IsClosed, Is.True,
                    "every opened directory handle must be released.");
            }

            foreach (CaptureIndexCommitFile file in fileSystem.OpenedFiles)
            {
                Assert.That(file.Handle.IsClosed, Is.True, "every opened file handle must be released.");
                Assert.That(file.Stream.CanRead, Is.False, "every opened stream must be released.");
            }
        }

        private static void RequireWindows()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore("The CaptureComplete cleaner requires Windows no-follow file handles.");
            }
        }

        private Sandbox CreateSandbox()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "zantetsuken-cccleanup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _sandboxes.Add(root);
            return new Sandbox(root);
        }

        /// <summary>
        /// Drives the Run through plan commit, artifact publication, capture
        /// index commit, and CaptureComplete, mints the cleanup operation, and
        /// lays down the staging targets plus the published files the cleanup
        /// must never touch.
        /// </summary>
        private static NvencRunCaptureCompleteCleanupOperation PrepareCleanup(Harness h, Sandbox sandbox)
        {
            PrepareCaptureIndexCommit(h);
            h.IndexCommitter.Status = NvencRunCaptureIndexCommitStatus.Committed;
            Assert.That(h.RunCoordinator.TrySubmitCaptureIndexCommit(), Is.True);
            WaitForServiceState(h, NvencRunPublicationServiceState.CaptureIndexCommitCompleted,
                "service did not publish the committed capture index terminal");
            Assert.That(h.RunCoordinator.TryCollectCaptureIndexCommit(out _), Is.True);

            Assert.That(h.RunCoordinator.TryPrepareCaptureComplete(out _), Is.True);
            h.RunCompleter.Status = NvencRunCaptureCompleteStatus.Completed;
            Assert.That(h.RunCoordinator.TrySubmitCaptureComplete(), Is.True);

            // CaptureComplete is the final phase: the Worker always stops.
            WaitForServiceStop(h, "publication worker did not reach the CaptureComplete terminal");
            Assert.That(h.Service.State,
                Is.EqualTo(NvencRunPublicationServiceState.CaptureCompleteCompleted));
            Assert.That(h.RunCoordinator.TryCollectCaptureComplete(out _), Is.True);

            Assert.That(h.RunCoordinator.TryPrepareCaptureCompleteCleanup(
                out NvencRunCaptureCompleteCleanupOperation operation), Is.True);

            sandbox.Populate(operation);
            return operation;
        }

        /// <summary>
        /// Records every filesystem call in order while delegating to the real
        /// backend, so the fixed order, the once-each counts, and the handle
        /// releases are observed without fabricating handles or duplicating the
        /// Win32 backend.
        /// </summary>
        private sealed class RecordingFileSystem : ICaptureCompleteCleanupFileSystem
        {
            private readonly ICaptureCompleteCleanupFileSystem _inner;

            internal bool? SupportedOverride;
            internal bool? DirectoryFlushSupportedOverride;

            internal readonly List<string> OpenedDirectoryPaths = new List<string>();
            internal readonly List<CaptureIndexCommitDirectory> OpenedDirectories =
                new List<CaptureIndexCommitDirectory>();
            internal readonly List<string> OpenedFileNames = new List<string>();
            internal readonly List<CaptureIndexCommitFile> OpenedFiles = new List<CaptureIndexCommitFile>();
            internal readonly List<string> DeletedFileNames = new List<string>();

            /// <summary>
            /// The side effects only, in order: every deletion and every
            /// directory metadata flush.
            /// </summary>
            internal readonly List<string> Mutations = new List<string>();

            private readonly Dictionary<CaptureIndexCommitFile, string> _fileNames =
                new Dictionary<CaptureIndexCommitFile, string>();
            private readonly Dictionary<CaptureIndexCommitDirectory, string> _directoryPaths =
                new Dictionary<CaptureIndexCommitDirectory, string>();

            internal RecordingFileSystem(ICaptureCompleteCleanupFileSystem inner)
            {
                _inner = inner;
            }

            public bool IsSupported => SupportedOverride ?? (_inner != null && _inner.IsSupported);

            public bool IsDirectoryFlushSupported =>
                DirectoryFlushSupportedOverride ?? (_inner != null && _inner.IsDirectoryFlushSupported);

            public CaptureIndexCommitDirectory OpenDirectory(string absolutePath)
            {
                CaptureIndexCommitDirectory directory = _inner.OpenDirectory(absolutePath);
                OpenedDirectoryPaths.Add(absolutePath);
                OpenedDirectories.Add(directory);
                _directoryPaths[directory] = absolutePath;
                return directory;
            }

            public CaptureIndexDirectoryOpen TryOpenDirectory(string absolutePath)
            {
                CaptureIndexDirectoryOpen opened = _inner.TryOpenDirectory(absolutePath);
                if (opened.Status == CaptureIndexFileOpenStatus.Opened)
                {
                    OpenedDirectoryPaths.Add(absolutePath);
                    OpenedDirectories.Add(opened.Directory);
                    _directoryPaths[opened.Directory] = absolutePath;
                }

                return opened;
            }

            public CaptureIndexFileOpen TryOpen(CaptureIndexCommitDirectory directory, string name)
            {
                CaptureIndexFileOpen opened = _inner.TryOpen(directory, name);
                if (opened.Status == CaptureIndexFileOpenStatus.Opened)
                {
                    OpenedFileNames.Add(name);
                    OpenedFiles.Add(opened.File);
                    _fileNames[opened.File] = name;
                }

                return opened;
            }

            public void Delete(CaptureIndexCommitFile file)
            {
                _inner.Delete(file);
                string name = _fileNames.TryGetValue(file, out string known) ? known : "<unknown>";
                DeletedFileNames.Add(name);
                Mutations.Add("delete:" + name);
            }

            public void FlushDirectory(CaptureIndexCommitDirectory directory)
            {
                _inner.FlushDirectory(directory);
                Mutations.Add("flush:" + PathOf(directory));
            }

            public void DeleteDirectory(CaptureIndexCommitDirectory directory)
            {
                _inner.DeleteDirectory(directory);
                Mutations.Add("deletedir:" + PathOf(directory));
            }

            public bool IsDirectoryEmpty(CaptureIndexCommitDirectory directory)
            {
                return _inner.IsDirectoryEmpty(directory);
            }

            private string PathOf(CaptureIndexCommitDirectory directory)
            {
                return _directoryPaths.TryGetValue(directory, out string path) ? path : "<unknown>";
            }
        }

        /// <summary>
        /// A real temporary staging and final tree for one Run, plus the exact
        /// published bytes the cleanup must leave alone.
        /// </summary>
        private sealed class Sandbox
        {
            private byte[] _finalChunkBytes;
            private byte[] _captureIndexBytes;

            internal Sandbox(string root)
            {
                Layout = new CaptureRunRootLayout(
                    Path.Combine(root, "staging"), Path.Combine(root, "final"), 1);

                Directory.CreateDirectory(StagingChunks);
                Directory.CreateDirectory(FinalChunks);
            }

            internal CaptureRunRootLayout Layout { get; }

            internal string StagingRunRootParent => Path.GetDirectoryName(Layout.StagingRunRoot);

            internal string StagingChunks => Path.Combine(Layout.StagingRunRoot, ChunksDirectoryName);

            internal string StagingPlanPath => Path.Combine(Layout.StagingRunRoot, PublicationPlanName);

            internal string StagingReadyPath => Path.Combine(Layout.StagingRunRoot, RunReadyMarkerName);

            internal string StagingInitPath => Path.Combine(Layout.StagingRunRoot, RunInitializationMarkerName);

            internal string FinalChunks => Path.Combine(Layout.FinalRunRoot, ChunksDirectoryName);

            internal string FinalChunkPath => Path.Combine(FinalChunks, ChunkFileName);

            internal string CaptureIndexPath => Path.Combine(Layout.FinalRunRoot, CaptureIndexName);

            internal void Populate(NvencRunCaptureCompleteCleanupOperation operation)
            {
                CaptureRunInitializationDocumentSet documents =
                    new CaptureRunInitializationDocumentSet(Layout, InitId);

                File.WriteAllBytes(StagingInitPath, documents.GetStagingInitializationBytes());
                File.WriteAllBytes(StagingReadyPath, documents.GetStagingReadyBytes());
                File.WriteAllBytes(
                    StagingPlanPath, CapturePublicationPlanCodec.SerializeCanonical(operation.Plan));

                // The published side: the artifact publication already moved the
                // chunk here and the capture index commit wrote the index.
                _finalChunkBytes = new byte[] { 10, 20, 30, 40 };
                _captureIndexBytes = new byte[] { 1, 2, 3, 4, 5 };
                File.WriteAllBytes(FinalChunkPath, _finalChunkBytes);
                File.WriteAllBytes(CaptureIndexPath, _captureIndexBytes);
            }

            internal void AssertStagingIntact()
            {
                Assert.That(File.Exists(StagingPlanPath), Is.True);
                Assert.That(File.Exists(StagingReadyPath), Is.True);
                Assert.That(File.Exists(StagingInitPath), Is.True);
                Assert.That(Directory.Exists(StagingChunks), Is.True);
                Assert.That(Directory.Exists(Layout.StagingRunRoot), Is.True);
            }

            internal void AssertFinalUntouched(RecordingFileSystem fileSystem)
            {
                // Nothing under the final trusted base root is ever opened.
                foreach (string opened in fileSystem.OpenedDirectoryPaths)
                {
                    Assert.That(
                        opened.StartsWith(Layout.FinalTrustedBaseRoot, StringComparison.OrdinalIgnoreCase),
                        Is.False,
                        "the published side must never be opened: " + opened);
                }

                Assert.That(fileSystem.OpenedFileNames, Has.No.Member(CaptureIndexName));
                Assert.That(fileSystem.DeletedFileNames, Has.No.Member(CaptureIndexName));

                Assert.That(File.Exists(FinalChunkPath), Is.True);
                Assert.That(File.ReadAllBytes(FinalChunkPath), Is.EqualTo(_finalChunkBytes));
                Assert.That(File.Exists(CaptureIndexPath), Is.True);
                Assert.That(File.ReadAllBytes(CaptureIndexPath), Is.EqualTo(_captureIndexBytes));
            }
        }

        // ---- Helpers ----

        private static NvencRunCaptureIndexCommitOperation PrepareCaptureIndexCommit(Harness h)
        {
            FinalizeOnly(h);
            Assert.That(h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out _), Is.True);
            h.Committer.Status = NvencRunPublicationPlanCommitStatus.Committed;
            Assert.That(h.RunCoordinator.TrySubmitPublicationPlanCommit(), Is.True);
            WaitForServiceState(h, NvencRunPublicationServiceState.PlanCommitCompleted,
                "service did not publish the committed plan terminal");
            Assert.That(h.RunCoordinator.TryCollectPublicationPlanCommit(out _), Is.True);

            Assert.That(h.RunCoordinator.TryPrepareArtifactPublication(out _), Is.True);
            h.Publisher.Status = NvencRunArtifactPublicationStatus.Published;
            Assert.That(h.RunCoordinator.TrySubmitArtifactPublication(), Is.True);
            WaitForArtifactTerminal(h, "publication worker did not reach the terminal for the artifact publication");
            Assert.That(h.RunCoordinator.TryCollectArtifactPublication(out _), Is.True);

            Assert.That(h.RunCoordinator.TryPrepareCaptureIndexCommit(
                out NvencRunCaptureIndexCommitOperation operation), Is.True);
            return operation;
        }

        /// <summary>
        /// Waits for the artifact terminal. A Published result keeps the same
        /// Worker parked for the Capture Index phase, so only a Failed result
        /// also stops it, and the Service refuses to collect a Failed terminal
        /// until the Worker has physically stopped.
        /// </summary>
        private static void WaitForArtifactTerminal(Harness h, string message)
        {
            SpinWait.SpinUntil(
                () => h.Service.State == NvencRunPublicationServiceState.ArtifactPublicationCompleted,
                WatchdogTimeoutMs);
            Assert.That(h.Service.State,
                Is.EqualTo(NvencRunPublicationServiceState.ArtifactPublicationCompleted), message);

            if (h.Publisher.Status != NvencRunArtifactPublicationStatus.Published)
            {
                WaitForServiceStop(h, message);
            }
        }

        private static void WaitForServiceStop(Harness h, string message)
        {
            FieldInfo field = typeof(NvencRunPublicationService).GetField(
                "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
            Thread worker = (Thread)field?.GetValue(h.Service);
            if (worker != null)
            {
                Assert.That(worker.Join(WatchdogTimeoutMs), Is.True, message);
            }

            Assert.That(h.Service.IsStopped, Is.True, message);
        }

        private static void WaitForServiceState(
            Harness h,
            NvencRunPublicationServiceState expected,
            string message)
        {
            SpinWait.SpinUntil(() => h.Service.State == expected, WatchdogTimeoutMs);
            Assert.That(h.Service.State, Is.EqualTo(expected), message);
        }

        /// <summary>
        /// Collects the requested Run chunk terminal by confirming the real
        /// condition inside a watchdog. A settle observed after the request may
        /// be a raise that was already in flight when the request was accepted,
        /// so it is used only as a wake hint; see TerminalConvergence.
        /// </summary>
        private static NvencRunChunkTerminalOutcome CollectTerminal(Harness h, string message)
        {
            return TerminalConvergence.Collect(
                h.RunCoordinator.TryCollectTerminal, h.Worker, h.SettledEvent, WatchdogTimeoutMs, message);
        }

        private static void WaitSettled(ManualResetEventSlim settled, string message)
        {
            Assert.That(settled.Wait(WatchdogTimeoutMs), Is.True, message);
        }

        private static void FinalizeOnly(Harness h)
        {
            StopFinalizedBackend(h);
            Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
            Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
            Assert.That(h.TraceRecorder.TryTrigger(), Is.True);
            ForcedDropFrameIdSet forced = MakeForcedDropSet(h);
            FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(h);
            Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(forced, checkpoint, out _), Is.True);
        }

        private static void StopFinalizedBackend(Harness h)
        {
            h.AcceptAndAppendChunk(1, 64, Seed);
            Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
            Assert.That(h.RunCoordinator.TryReflectCompletion(
                MakeCompletion(1, CaptureFrameCompletionStatus.Succeeded)), Is.True);
            h.SubmitDrained = true;

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
            CollectTerminal(h, "worker did not converge the finalize request");

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
            WaitSettled(h.SettledEvent, "worker did not complete the teardown");
            h.WaitForPhysicalStop("worker did not physically exit after the teardown");

            Assert.That(h.Worker.TeardownCompleted, Is.True);
            Assert.That(h.Worker.IsStopped, Is.True);

            h.SubmitWorker.Dispose();
        }

        private static ForcedDropFrameIdSet MakeForcedDropSet(Harness h)
        {
            h.DraftQueue.BeginProducerDrain();
            h.DraftQueue.CloseAfterProducerJoin();
            TerminalIntentOwnershipSnapshot snapshot = h.DraftQueue.CreateOwnershipSnapshot(0);
            return h.DraftRegistry.ForceDropPendingForFreeze(h.DraftQueue, snapshot);
        }

        private static FreezeTerminalCheckpoint MakeCheckpoint(Harness h)
        {
            return new FreezeTerminalCheckpoint(
                1000, 1, 1, Thread.CurrentThread.ManagedThreadId, h.Context.TestRunId);
        }

        private static CaptureFrameCompletion MakeCompletion(
            long captureFrameId,
            CaptureFrameCompletionStatus status)
        {
            CaptureFrameWorkToken token = new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, captureFrameId);
            ExceptionDispatchInfo failure = status == CaptureFrameCompletionStatus.Failed
                ? ExceptionDispatchInfo.Capture(new InvalidOperationException("completion failed"))
                : null;
            return new CaptureFrameCompletion(token, captureFrameId, status, true, 0, failure);
        }

        private static CaptureRunInitializationSessionIssue MakeIssue(CaptureRunRootLayout layout)
        {
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true) { Tag = "first" };
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true) { Tag = "second" };
            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);
            CaptureRunInitializationSessionOwnershipLease owner =
                CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);
            return CaptureRunInitializationSession.IssuanceProof.Mint(owner, identity, evidence);
        }

        private static CaptureRunRootLayout MakeLayout()
        {
            return new CaptureRunRootLayout(
                Path.DirectorySeparatorChar == '\\' ? "C:\\staging" : "/staging",
                Path.DirectorySeparatorChar == '\\' ? "D:\\final" : "/final",
                1);
        }

        private static CaptureRunInitializationExecutionReceipt MakeExecutionReceipt(CaptureRunRootLayout layout)
        {
            CaptureRunInitializationDocumentSet documents =
                new CaptureRunInitializationDocumentSet(layout, InitId);
            CaptureRunInitializationExecutionCoordinator executionCoordinator =
                new CaptureRunInitializationExecutionCoordinator(new FakeProvisioner(), new FakeMarkerWriter());
            return executionCoordinator.Execute(documents);
        }

        private static CaptureFrameWorkToken MakeToken(long frameId)
        {
            return new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, 1, frameId);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        // ---- Fakes ----

        private sealed class FakeCaptureIndexCommitter : INvencRunCaptureIndexCommitter
        {
            private int _callCount;
            internal NvencRunCaptureIndexCommitStatus Status = NvencRunCaptureIndexCommitStatus.Committed;
            internal Exception ExceptionToThrow;
            internal bool UseOverride;
            internal NvencRunCaptureIndexCommitAttemptResult OverrideResult;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureIndexCommitAttemptResult Commit(
                NvencRunCaptureIndexCommitOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (UseOverride)
                {
                    return OverrideResult;
                }

                if (Status == NvencRunCaptureIndexCommitStatus.Failed)
                {
                    return NvencRunCaptureIndexCommitAttemptResult.Failed(this, operation);
                }

                return NvencRunCaptureIndexCommitAttemptResult.Committed(this, operation);
            }
        }

        private sealed class FakeCommitter : INvencRunPublicationPlanCommitter
        {
            private int _callCount;
            internal NvencRunPublicationPlanCommitStatus Status = NvencRunPublicationPlanCommitStatus.Committed;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunPublicationPlanCommitAttemptResult Commit(
                NvencRunPublicationPlanCommitOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                if (Status == NvencRunPublicationPlanCommitStatus.FailedBeforeRename)
                {
                    return NvencRunPublicationPlanCommitAttemptResult.FailedBeforeRename(this, operation);
                }

                if (Status == NvencRunPublicationPlanCommitStatus.CommitOutcomeUnknown)
                {
                    return NvencRunPublicationPlanCommitAttemptResult.CommitOutcomeUnknown(this, operation);
                }

                return NvencRunPublicationPlanCommitAttemptResult.Committed(this, operation);
            }
        }

        private sealed class FakePublisher : INvencRunArtifactPublisher
        {
            private int _callCount;
            internal NvencRunArtifactPublicationStatus Status = NvencRunArtifactPublicationStatus.Published;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunArtifactPublicationAttemptResult Publish(
                NvencRunArtifactPublicationOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                if (Status == NvencRunArtifactPublicationStatus.Failed)
                {
                    return NvencRunArtifactPublicationAttemptResult.Failed(this, operation);
                }

                return NvencRunArtifactPublicationAttemptResult.Published(this, operation);
            }
        }

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            public FakeHandle(string lockPath, bool isCreated)
            {
                LockPath = lockPath;
                IsCreated = isCreated;
            }

            public string LockPath { get; }

            public bool IsCreated { get; }

            public string Tag { get; set; }

            public void Dispose()
            {
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
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

        private sealed class FakeWriter : INvencRunChunkAppender, INvencRunChunkFinalizer
        {
            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
                CaptureArtifactDescriptor descriptor = NvencRunChunkArtifactDescriptorFactory.Create(
                    operation.ArtifactId, operation.AccumulatedByteLength, Hash64);
                return NvencRunChunkFinalizationReceipt.Create(this, operation, descriptor);
            }
        }

        private sealed class PatternSource : INvencOutputBitstreamSource
        {
            private readonly int _length;
            private readonly byte _seed;

            internal PatternSource(int length, byte seed)
            {
                _length = length;
                _seed = seed;
            }

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                for (int i = 0; i < _length; i++)
                {
                    destination[i] = (byte)(_seed + i);
                }

                validLength = _length;
                return true;
            }
        }

        private sealed class FakeOutputSource : INvencOutputBitstreamSource
        {
            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                validLength = 1024;
                return true;
            }
        }

        private sealed class FakeSourceReadCompletedSource : INvencSourceReadCompletedSource
        {
            public bool TryGetEvidence(in NvencSubmissionRecord record, out NvencSourceReadCompletedEvidence evidence)
            {
                evidence = default;
                return false;
            }
        }

        private sealed class FakeSubmitter : INvencEncodePictureSubmitter
        {
            public bool TrySubmit(in NvencEncodePictureSubmitOperation operation)
            {
                return true;
            }
        }

        private sealed class FakeTeardown : INvencOutputWorkerTeardown
        {
            public NvencOutputWorkerTeardownReceipt TearDown()
            {
                return NvencOutputWorkerTeardownReceipt.Issue(this);
            }
        }

        private sealed class FakeMainThreadTeardown : INvencMainThreadTextureTeardown
        {
            internal NvencRunChunkContext BoundContext;

            public bool IsBoundTo(NvencRunChunkContext context)
            {
                return BoundContext != null && context != null && ReferenceEquals(BoundContext, context);
            }

            public NvencMainThreadTextureTeardownReceipt TearDown()
            {
                return new NvencMainThreadTextureTeardownReceipt(this, BoundContext);
            }
        }

        private sealed class FakeRunCompleter : INvencRunCaptureCompleter
        {
            private int _callCount;
            internal NvencRunCaptureCompleteStatus Status = NvencRunCaptureCompleteStatus.Completed;
            internal Exception ExceptionToThrow;
            internal bool UseOverride;
            internal NvencRunCaptureCompleteAttemptResult OverrideResult;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim Release;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureCompleteAttemptResult Complete(
                NvencRunCaptureCompleteOperation operation)
            {
                Interlocked.Increment(ref _callCount);

                Entered?.Set();
                Release?.Wait(WatchdogTimeoutMs);

                if (ExceptionToThrow != null)
                {
                    throw ExceptionToThrow;
                }

                if (UseOverride)
                {
                    return OverrideResult;
                }

                if (Status == NvencRunCaptureCompleteStatus.Failed)
                {
                    return NvencRunCaptureCompleteAttemptResult.Failed(this, operation);
                }

                return NvencRunCaptureCompleteAttemptResult.Completed(this, operation);
            }
        }

        /// <summary>
        /// Identity-only CaptureComplete cleaner: this fixture never runs a
        /// cleanup, but the Run Coordinator requires the exact cleanup
        /// Execution Coordinator its results must come from.
        /// </summary>
        private sealed class FakeCleanupCleaner : INvencRunCaptureCompleteCleaner
        {
            public NvencRunCaptureCompleteCleanupAttemptResult Clean(
                NvencRunCaptureCompleteCleanupOperation operation)
            {
                return NvencRunCaptureCompleteCleanupAttemptResult.Cleaned(this, operation);
            }
        }

        /// <summary>
        /// Identity-only Session Ownership Lease releaser: this fixture never
        /// releases a lease, but the Run Coordinator requires the exact release
        /// Execution Coordinator its receipts must come from.
        /// </summary>
        private sealed class FakeSessionOwnershipReleaser : INvencRunSessionOwnershipReleaser
        {
            public NvencRunSessionOwnershipReleaseReceipt Release(
                NvencRunSessionOwnershipReleaseOperation operation)
            {
                if (operation == null)
                {
                    throw new ArgumentNullException(nameof(operation));
                }

                if (!NvencRunSessionOwnershipReleaseAdmission.IsAdmissible(operation))
                {
                    throw new ArgumentException(
                        "The operation cannot start a release attempt.", nameof(operation));
                }

                operation.OwnershipLease.Dispose();
                return NvencRunSessionOwnershipReleaseReceipt.Create(this, operation);
            }
        }

        private sealed class Harness : IDisposable
        {
            internal NvencCaptureProcessState State;
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal FakeWriter Writer;
            internal NvencRunChunkSink Sink;
            internal NvencRunChunkFinalizationCoordinator FinalizationCoordinator;
            internal NvencRunChunkContext Context;
            internal NvencRunLocalRegistrySlot Slot;

            internal NvencCaptureWorkSlotPool WorkSlots;
            internal NvencEncodeSampleSlotPool SampleSlots;
            internal NvencSubmitToOutputCreditPool SubmitToOutputCredits;
            internal NvencFrameCompletionCreditPool FrameCompletionCredits;
            internal FakeOutputSource Source;
            internal NvencSubmittedOutputCollector Collector;
            internal NvencFailedBeforeSubmitReleaseCoordinator ReleaseCoordinator;
            internal NvencSubmittedOutputAbandonRecoveryCoordinator RecoveryCoordinator;
            internal NvencFrameCompletionBoundary Boundary;
            internal NvencFixedSpscQueue<NvencSubmitToOutputRecord> OutputQueue;
            internal NvencOrderedOutputProcessor Processor;
            internal NvencOrderedOutputWorkerService Worker;
            internal FakeTeardown Teardown;
            internal FakeMainThreadTeardown MainThreadTeardown;

            internal NvencGpuConversionSyncPool SubmitSyncSlots;
            internal NvencFixedSpscQueue<NvencSubmissionRecord> SubmitSubmissionQueue;
            internal NvencSourceResourceReleaseCoordinator SubmitReleaseCoordinator;
            internal NvencOrderedSubmitProcessor SubmitProcessor;
            internal NvencOrderedSubmitWorkerService SubmitWorker;

            internal NvencCaptureRunCoordinator RunCoordinator;
            internal NvencCaptureBackendJoinCoordinator BackendJoin;
            internal ManualResetEventSlim SettledEvent;

            internal FakeCommitter Committer;
            internal FakePublisher Publisher;
            internal FakeCaptureIndexCommitter IndexCommitter;
            internal FakeRunCompleter RunCompleter;
            internal NvencRunPublicationService Service;
            internal FakeCleanupCleaner CleanupCleaner;
            internal NvencRunCaptureCompleteCleanupExecutionCoordinator CleanupExecution;
            internal FakeSessionOwnershipReleaser Releaser;
            internal NvencRunSessionOwnershipReleaseExecutionCoordinator ReleaseExecution;

            internal CaptureRunInitializationSessionIssue SessionIssue;
            internal TraceLogger TraceLogger;
            internal TraceFlightRecorder TraceRecorder;
            internal CaptureFrameFreezeTerminalCoordinator FreezeTerminalCoordinator;
            internal CaptureFrameDraftRegistry DraftRegistry;
            internal CaptureFrameDraftTerminalIntentQueue DraftQueue;
            internal NvencTraceFreezeCoordinator TraceFreeze;

            private readonly Action _settledHandler;

            internal bool SubmitDrained
            {
                set => SetField(SubmitWorker, "_drainCompleted", value);
            }

            internal Harness(CaptureRunRootLayout layout)
            {
                State = new NvencCaptureProcessState();

                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Writer = new FakeWriter();
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
                FinalizationCoordinator = new NvencRunChunkFinalizationCoordinator(Writer);
                SessionIssue = MakeIssue(layout);
                Context = new NvencRunChunkContext(SessionIssue, Sink, FinalizationCoordinator, "chunk/0");
                Slot = new NvencRunLocalRegistrySlot(Context);

                WorkSlots = new NvencCaptureWorkSlotPool(State);
                SampleSlots = new NvencEncodeSampleSlotPool(State);
                SubmitToOutputCredits = new NvencSubmitToOutputCreditPool(State);
                FrameCompletionCredits = new NvencFrameCompletionCreditPool(State);
                Source = new FakeOutputSource();
                Collector = new NvencSubmittedOutputCollector(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer, Source);
                ReleaseCoordinator = new NvencFailedBeforeSubmitReleaseCoordinator(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits);
                RecoveryCoordinator = new NvencSubmittedOutputAbandonRecoveryCoordinator(
                    State, Collector, SampleSlots, Buffer);
                Boundary = new NvencFrameCompletionBoundary(
                    State, WorkSlots, SampleSlots, SubmitToOutputCredits, FrameCompletionCredits, Buffer);
                OutputQueue = new NvencFixedSpscQueue<NvencSubmitToOutputRecord>();
                Processor = new NvencOrderedOutputProcessor(
                    State, OutputQueue, Collector, Sink, ReleaseCoordinator, RecoveryCoordinator, Boundary);

                SubmitSyncSlots = new NvencGpuConversionSyncPool(State);
                SubmitSubmissionQueue = new NvencFixedSpscQueue<NvencSubmissionRecord>();
                SubmitReleaseCoordinator = new NvencSourceResourceReleaseCoordinator(
                    State, WorkSlots, SampleSlots, SubmitSyncSlots,
                    SubmitToOutputCredits, FrameCompletionCredits,
                    new FakeSourceReadCompletedSource(), new NvencSourceSurfaceReturnBoundary(), Guid.NewGuid());
                SubmitProcessor = new NvencOrderedSubmitProcessor(
                    State, SubmitSubmissionQueue, OutputQueue,
                    WorkSlots, SampleSlots, SubmitReleaseCoordinator, new FakeSubmitter());
                SubmitWorker = new NvencOrderedSubmitWorkerService(State, SubmitProcessor);

                Teardown = new FakeTeardown();
                Worker = new NvencOrderedOutputWorkerService(State, Processor, Context, SubmitWorker, Teardown);

                MainThreadTeardown = new FakeMainThreadTeardown { BoundContext = Context };
                BackendJoin = new NvencCaptureBackendJoinCoordinator(
                    State, SubmitWorker, Worker, Context,
                    WorkSlots, SampleSlots, SubmitSyncSlots, SubmitToOutputCredits, FrameCompletionCredits,
                    Buffer, Processor, MainThreadTeardown);

                TraceLogger = new TraceLogger(16, Context.TestRunId);
                TraceRecorder = new TraceFlightRecorder(TraceLogger, 16, 2);
                TraceRunContext traceRunContext = new TraceRunContext(
                    Context.TestRunId, 1000, "build-1", "6000.3.22f1", Hash64, "scene-1", 12345, 0.02, 3, "High", 1,
                    new Vector3(0f, -4.9f, 0f));
                CaptureDraftRunContext draftRun = new CaptureDraftRunContext(traceRunContext, 100, 5);
                CaptureTraceProfile traceProfile = new CaptureTraceProfile(5, 4096, 2, 4);
                DraftRegistry = new CaptureFrameDraftRegistry(draftRun, traceProfile);
                DraftQueue = new CaptureFrameDraftTerminalIntentQueue(DraftRegistry, traceProfile);
                FreezeTerminalTraceBufferBuilder freezeBuilder = new FreezeTerminalTraceBufferBuilder(DraftRegistry);
                FreezeTerminalCoordinator = new CaptureFrameFreezeTerminalCoordinator(TraceRecorder, freezeBuilder);
                TraceFreeze = new NvencTraceFreezeCoordinator(
                    TraceLogger, TraceRecorder, FreezeTerminalCoordinator, Context, SessionIssue);

                Committer = new FakeCommitter();
                NvencRunPublicationPlanCommitExecutionCoordinator commitCoordinator =
                    new NvencRunPublicationPlanCommitExecutionCoordinator(Committer);
                Publisher = new FakePublisher();
                NvencRunArtifactPublicationExecutionCoordinator artifactCoordinator =
                    new NvencRunArtifactPublicationExecutionCoordinator(Publisher);
                IndexCommitter = new FakeCaptureIndexCommitter();
                NvencRunCaptureIndexCommitExecutionCoordinator captureIndexCoordinator =
                    new NvencRunCaptureIndexCommitExecutionCoordinator(IndexCommitter);
                RunCompleter = new FakeRunCompleter();
                NvencRunCaptureCompleteExecutionCoordinator captureCompleteCoordinator =
                    new NvencRunCaptureCompleteExecutionCoordinator(RunCompleter);
                Service = new NvencRunPublicationService(
                    State, commitCoordinator, artifactCoordinator, captureIndexCoordinator,
                    captureCompleteCoordinator);

                CleanupCleaner = new FakeCleanupCleaner();
                CleanupExecution =
                    new NvencRunCaptureCompleteCleanupExecutionCoordinator(CleanupCleaner);

                Releaser = new FakeSessionOwnershipReleaser();
                ReleaseExecution =
                    new NvencRunSessionOwnershipReleaseExecutionCoordinator(Releaser);

                RunCoordinator = new NvencCaptureRunCoordinator(
                    State, SubmitWorker, Worker, Context, Slot, MainThreadTeardown, BackendJoin,
                    SessionIssue, TraceFreeze, Service, CleanupExecution, ReleaseExecution);

                SettledEvent = new ManualResetEventSlim(false);
                _settledHandler = () => SettledEvent.Set();
                Worker.Settled += _settledHandler;
            }

            internal static Harness Create(CaptureRunRootLayout layout = null)
            {
                Harness h = new Harness(layout ?? MakeLayout());

                h.SettledEvent.Reset();
                h.Worker.Notify();
                Assert.That(h.SettledEvent.Wait(WatchdogTimeoutMs), Is.True, "worker did not settle initially");

                return h;
            }

            internal void AcceptAndAppendChunk(long frameId, int length, byte seed)
            {
                Assert.That(Context.TryRecordAcceptedFrame(frameId), Is.True);
                CaptureFrameWorkToken token = MakeToken(frameId);
                Assert.That(Buffer.TryBeginWrite(token, out NvencAccessUnitWriteLease write), Is.True);
                Assert.That(Buffer.TryCopyCompletedOutput(write, default, new PatternSource(length, seed), out _),
                    Is.EqualTo(NvencAccessUnitCopyStatus.Committed));
                Assert.That(Buffer.TryTransferToSink(write, out NvencOwnedAccessUnitLease lease), Is.True);
                Assert.That(Sink.TryAppend(token, lease, out _), Is.True);
            }

            internal void WaitForPhysicalStop(string message)
            {
                FieldInfo field = typeof(NvencOrderedOutputWorkerService).GetField(
                    "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
                Thread workerThread = (Thread)field?.GetValue(Worker);
                if (workerThread != null)
                {
                    Assert.That(workerThread.Join(WatchdogTimeoutMs), Is.True, message);
                }
            }

            public void Dispose()
            {
                if (!Worker.IsStopped)
                {
                    State.TryPoison();
                    Worker.Notify();
                }

                WaitForPhysicalStop("worker thread did not physically exit during teardown");

                Worker.Dispose();
                Worker.Settled -= _settledHandler;
                SettledEvent.Dispose();

                if (!Service.IsStopped)
                {
                    if (!Service.TryStopWithoutRequest())
                    {
                        State.TryPoison();
                        Service.Notify();
                    }
                }

                FieldInfo serviceField = typeof(NvencRunPublicationService).GetField(
                    "_workerThread", BindingFlags.Instance | BindingFlags.NonPublic);
                Thread serviceThread = (Thread)serviceField?.GetValue(Service);
                if (serviceThread != null)
                {
                    Assert.That(serviceThread.Join(WatchdogTimeoutMs), Is.True,
                        "publication service worker did not physically exit during teardown");
                }

                Service.Dispose();
            }
        }
    }
}
