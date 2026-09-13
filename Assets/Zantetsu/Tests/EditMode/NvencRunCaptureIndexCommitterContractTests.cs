using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;
using NvencAccessUnitCopyStatus = Zantetsu.Observability.NvencOwnedAccessUnitBuffer.NvencAccessUnitCopyStatus;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 production Fresh NVENC capture index
    /// committer: validation before any filesystem contact, the single
    /// create-write-rename attempt, the canonical plan bytes it writes, and the
    /// Committed and Failed classifications. Uses the committed and published
    /// Run pipeline to mint a valid capture index commit operation with a
    /// deterministic tracking filesystem, plus a bounded real-filesystem check
    /// on Windows; no real GPU, NVENC, sleep, or short negative wait is used.
    /// </summary>
    public class NvencRunCaptureIndexCommitterContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string CaptureIndexTemporaryName = "capture.index.tmp";

        private const string CaptureIndexName = "capture.index";

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
                () => new NvencRunCaptureIndexCommitter(null, new TrackingFileSystem()));
            Assert.That(nullLayout.ParamName, Is.EqualTo("rootLayout"));

            ArgumentNullException nullFileSystem = Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureIndexCommitter(MakeLayout(), null));
            Assert.That(nullFileSystem.ParamName, Is.EqualTo("fileSystem"));

            // The layout's own constructor validates every invariant it
            // guarantees, so an invalid instance cannot be built through it;
            // the committer's IsValid guard stays as defence in depth and is
            // deliberately not exercised by fabricating a corrupted layout.
            Assert.That(MakeLayout().IsValid, Is.True);
        }

        [Test]
        public void Constructor_DoesNotContactTheFilesystem()
        {
            TrackingFileSystem fileSystem = new TrackingFileSystem();
            NvencRunCaptureIndexCommitter committer =
                new NvencRunCaptureIndexCommitter(MakeLayout(), fileSystem);

            Assert.That(committer, Is.Not.Null);
            AssertNoFilesystemContact(fileSystem);
        }

        // ---- Validation before any filesystem contact ----

        [Test]
        public void Commit_NullOperation_ArgumentNullException_NoFilesystemContact()
        {
            TrackingFileSystem fileSystem = new TrackingFileSystem();
            NvencRunCaptureIndexCommitter committer =
                new NvencRunCaptureIndexCommitter(MakeLayout(), fileSystem);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => committer.Commit(null));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            AssertNoFilesystemContact(fileSystem);
        }

        [Test]
        public void Commit_InvalidOperation_ArgumentException_NoFilesystemContact()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PrepareCaptureIndexCommit(h);
                CaptureRunRootLayout layout = operation.RootLayout;

                TrackingFileSystem fileSystem = new TrackingFileSystem();
                NvencRunCaptureIndexCommitter committer =
                    new NvencRunCaptureIndexCommitter(layout, fileSystem);

                // A poisoned process invalidates the operation.
                Assert.That(h.State.TryPoison(), Is.True);
                Assert.That(operation.IsValid, Is.False);

                ArgumentException ex = Assert.Throws<ArgumentException>(() => committer.Commit(operation));
                Assert.That(ex.ParamName, Is.EqualTo("operation"));
                AssertNoFilesystemContact(fileSystem);
            }
        }

        [Test]
        public void Commit_ForeignRootLayout_ArgumentException_NoFilesystemContact()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PrepareCaptureIndexCommit(h);

                // A content-identical but distinct root layout instance is a
                // foreign Run and must be rejected by exact reference.
                TrackingFileSystem fileSystem = new TrackingFileSystem();
                NvencRunCaptureIndexCommitter committer =
                    new NvencRunCaptureIndexCommitter(MakeLayout(), fileSystem);

                ArgumentException ex = Assert.Throws<ArgumentException>(() => committer.Commit(operation));
                Assert.That(ex.ParamName, Is.EqualTo("operation"));
                AssertNoFilesystemContact(fileSystem);
            }
        }

        [Test]
        public void Commit_UnsupportedCapability_Throws_NoFilesystemContact()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PrepareCaptureIndexCommit(h);

                TrackingFileSystem fileSystem = new TrackingFileSystem { Supported = false };
                NvencRunCaptureIndexCommitter committer =
                    new NvencRunCaptureIndexCommitter(operation.RootLayout, fileSystem);

                // Capability insufficiency is a configuration error, never an
                // ordinary Failed commit.
                Assert.Throws<CaptureArtifactNoFollowUnavailableException>(() => committer.Commit(operation));
                AssertNoFilesystemContact(fileSystem);
            }
        }

        // ---- Committed ----

        [Test]
        public void Commit_WritesCanonicalPlanBytesAndRenamesOnce()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PrepareCaptureIndexCommit(h);

                TrackingFileSystem fileSystem = new TrackingFileSystem();
                NvencRunCaptureIndexCommitter committer =
                    new NvencRunCaptureIndexCommitter(operation.RootLayout, fileSystem);

                NvencRunCaptureIndexCommitAttemptResult result = committer.Commit(operation);

                Assert.That(result.IsCommitted, Is.True);
                Assert.That(result.Status, Is.EqualTo(NvencRunCaptureIndexCommitStatus.Committed));
                Assert.That(ReferenceEquals(result.Committer, committer), Is.True);
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);
                Assert.That(result.Receipt, Is.Not.Null);
                Assert.That(result.Receipt.IsIssuedFor(committer, operation), Is.True);

                // Exactly one open of the final Run root, one create-new of the
                // temporary, one write, and one non-overwriting rename.
                Assert.That(fileSystem.OpenedDirectoryPaths,
                    Is.EqualTo(new[] { operation.RootLayout.FinalRunRoot }));
                Assert.That(fileSystem.CreatedNames, Is.EqualTo(new[] { CaptureIndexTemporaryName }));
                Assert.That(fileSystem.RenamedNames, Is.EqualTo(new[] { CaptureIndexName }));
                Assert.That(fileSystem.CreatedStreams, Has.Count.EqualTo(1));
                Assert.That(fileSystem.CreatedStreams[0].WriteCalls, Is.EqualTo(1));

                // The content is the canonical serialization of the same plan
                // the publication plan commit used.
                byte[] expected = CapturePublicationPlanCodec.SerializeCanonical(operation.Plan);
                Assert.That(fileSystem.CreatedStreams[0].ToArray(), Is.EqualTo(expected));

                AssertHandlesReleased(fileSystem);
            }
        }

        [Test]
        public void Commit_TouchesTheFinalRootOnly_LeavesRunStateUnchanged()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PrepareCaptureIndexCommit(h);

                NvencRunEvidenceDisposition disposition = h.RunCoordinator.Disposition;
                NvencRunLocalRegistrySlotState slotState = h.Slot.State;
                bool hasRegisteredEntry = h.Slot.HasRegisteredEntry;
                NvencRunChunkContextState contextState = h.Context.State;
                NvencRunPublicationServiceState serviceState = h.Service.State;
                bool serviceReleased = h.RunCoordinator.PublicationServiceReleased;
                bool leaseValid = h.SessionIssue.IsValid;
                CapturePublicationPlan plan = operation.Plan;

                TrackingFileSystem fileSystem = new TrackingFileSystem();
                NvencRunCaptureIndexCommitter committer =
                    new NvencRunCaptureIndexCommitter(operation.RootLayout, fileSystem);

                Assert.That(committer.Commit(operation).IsCommitted, Is.True);

                // The staging Run root is never opened, and only the two fixed
                // names are ever touched.
                Assert.That(fileSystem.OpenedDirectoryPaths,
                    Has.No.Member(operation.RootLayout.StagingRunRoot));
                Assert.That(fileSystem.CreatedNames, Is.EqualTo(new[] { CaptureIndexTemporaryName }));
                Assert.That(fileSystem.RenamedNames, Is.EqualTo(new[] { CaptureIndexName }));

                Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(disposition));
                Assert.That(h.Slot.State, Is.EqualTo(slotState));
                Assert.That(h.Slot.HasRegisteredEntry, Is.EqualTo(hasRegisteredEntry));
                Assert.That(h.Context.State, Is.EqualTo(contextState));
                Assert.That(h.Service.State, Is.EqualTo(serviceState));
                Assert.That(h.RunCoordinator.PublicationServiceReleased, Is.EqualTo(serviceReleased));
                Assert.That(h.SessionIssue.IsValid, Is.EqualTo(leaseValid));
                Assert.That(ReferenceEquals(operation.Plan, plan), Is.True);
                Assert.That(h.State.IsPoisoned, Is.False);
            }
        }

        // ---- Failed ----

        [Test]
        public void Commit_OpenCreateOrWriteFailure_FailedWithoutReceipt()
        {
            AssertFailedBeforeRename(
                fileSystem => fileSystem.OpenDirectoryFailure = new IOException("open failed"),
                expectCreated: 0);

            AssertFailedBeforeRename(
                fileSystem => fileSystem.CreateNewFailure = new IOException("create failed"),
                expectCreated: 0);

            AssertFailedBeforeRename(
                fileSystem => fileSystem.WriteFailure = new IOException("write failed"),
                expectCreated: 1);
        }

        [Test]
        public void Commit_RenameFailure_FailedWithoutReceipt_NoReReadOrRetry()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PrepareCaptureIndexCommit(h);

                TrackingFileSystem fileSystem = new TrackingFileSystem
                {
                    RenameFailure = new IOException("rename failed"),
                };
                NvencRunCaptureIndexCommitter committer =
                    new NvencRunCaptureIndexCommitter(operation.RootLayout, fileSystem);

                NvencRunCaptureIndexCommitAttemptResult result = committer.Commit(operation);

                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.Status, Is.EqualTo(NvencRunCaptureIndexCommitStatus.Failed));
                Assert.That(result.Receipt, Is.Null);
                Assert.That(ReferenceEquals(result.Committer, committer), Is.True);
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);

                // Exactly one rename attempt, and nothing is re-read,
                // existence-checked, deleted, renamed again, or cleaned up
                // after it raised.
                Assert.That(fileSystem.RenamedNames, Is.EqualTo(new[] { CaptureIndexName }));
                Assert.That(fileSystem.OpenedDirectoryPaths, Has.Count.EqualTo(1));
                Assert.That(fileSystem.CreatedNames, Is.EqualTo(new[] { CaptureIndexTemporaryName }));
                Assert.That(fileSystem.CreatedStreams[0].WriteCalls, Is.EqualTo(1));
                Assert.That(fileSystem.CreatedStreams[0].ReadCalls, Is.EqualTo(0));

                AssertHandlesReleased(fileSystem);
            }
        }

        [Test]
        public void Commit_CommitsAtMostOncePerCall()
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PrepareCaptureIndexCommit(h);

                TrackingFileSystem fileSystem = new TrackingFileSystem();
                NvencRunCaptureIndexCommitter committer =
                    new NvencRunCaptureIndexCommitter(operation.RootLayout, fileSystem);

                Assert.That(committer.Commit(operation).IsCommitted, Is.True);
                Assert.That(fileSystem.CreatedNames, Has.Count.EqualTo(1));
                Assert.That(fileSystem.RenamedNames, Has.Count.EqualTo(1));
                Assert.That(fileSystem.CreatedStreams[0].WriteCalls, Is.EqualTo(1));

                // A second call is the caller's decision, never the committer's
                // retry: each call performs its own single attempt.
                Assert.That(committer.Commit(operation).IsCommitted, Is.True);
                Assert.That(fileSystem.CreatedNames, Has.Count.EqualTo(2));
                Assert.That(fileSystem.RenamedNames, Has.Count.EqualTo(2));
                Assert.That(fileSystem.CreatedStreams[1].WriteCalls, Is.EqualTo(1));
            }
        }

        // ---- Shape ----

        [Test]
        public void Committer_HoldsOnlyTheRootLayoutAndFileSystem_NotDisposable()
        {
            Type type = typeof(NvencRunCaptureIndexCommitter);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(2));
            Assert.That(
                fields.Select(field => field.FieldType),
                Is.EquivalentTo(new[]
                {
                    typeof(CaptureRunRootLayout),
                    typeof(INvencPublicationPlanCommitFileSystem),
                }));

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        // ---- Windows backend integration ----

        [Test]
        public void Backend_Commit_WritesCaptureIndexAndRemovesTemporary()
        {
            RequireWindows();

            CaptureRunRootLayout layout = MakeSandboxLayout();
            Directory.CreateDirectory(layout.FinalRunRoot);

            using (Harness h = Harness.Create(layout))
            {
                NvencRunCaptureIndexCommitOperation operation = PrepareCaptureIndexCommit(h);
                byte[] expected = CapturePublicationPlanCodec.SerializeCanonical(operation.Plan);

                NvencRunCaptureIndexCommitter committer = new NvencRunCaptureIndexCommitter(
                    layout, NvencPublicationPlanCommitFileSystem.Create());

                Assert.That(committer.Commit(operation).IsCommitted, Is.True);

                string finalPath = Path.Combine(layout.FinalRunRoot, CaptureIndexName);
                string temporaryPath = Path.Combine(layout.FinalRunRoot, CaptureIndexTemporaryName);

                Assert.That(File.Exists(temporaryPath), Is.False);
                Assert.That(File.Exists(finalPath), Is.True);
                Assert.That(File.ReadAllBytes(finalPath), Is.EqualTo(expected));

                // Every handle and stream was released.
                using (new FileStream(finalPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }
            }
        }

        [Test]
        public void Backend_ExistingCaptureIndex_NotOverwritten()
        {
            RequireWindows();

            CaptureRunRootLayout layout = MakeSandboxLayout();
            Directory.CreateDirectory(layout.FinalRunRoot);

            string finalPath = Path.Combine(layout.FinalRunRoot, CaptureIndexName);
            byte[] existing = { 9, 9, 9 };
            File.WriteAllBytes(finalPath, existing);

            using (Harness h = Harness.Create(layout))
            {
                NvencRunCaptureIndexCommitOperation operation = PrepareCaptureIndexCommit(h);

                NvencRunCaptureIndexCommitter committer = new NvencRunCaptureIndexCommitter(
                    layout, NvencPublicationPlanCommitFileSystem.Create());

                NvencRunCaptureIndexCommitAttemptResult result = committer.Commit(operation);

                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.Receipt, Is.Null);

                // The existing final is untouched and the temporary is left for
                // Recovery rather than cleaned up.
                Assert.That(File.ReadAllBytes(finalPath), Is.EqualTo(existing));
                Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, CaptureIndexTemporaryName)), Is.True);
            }
        }

        // ---- Fixture helpers ----

        private void AssertFailedBeforeRename(
            Action<TrackingFileSystem> inject,
            int expectCreated)
        {
            using (Harness h = Harness.Create())
            {
                NvencRunCaptureIndexCommitOperation operation = PrepareCaptureIndexCommit(h);

                TrackingFileSystem fileSystem = new TrackingFileSystem();
                inject(fileSystem);
                NvencRunCaptureIndexCommitter committer =
                    new NvencRunCaptureIndexCommitter(operation.RootLayout, fileSystem);

                NvencRunCaptureIndexCommitAttemptResult result = committer.Commit(operation);

                Assert.That(result.IsFailed, Is.True);
                Assert.That(result.Receipt, Is.Null);
                Assert.That(ReferenceEquals(result.Committer, committer), Is.True);
                Assert.That(ReferenceEquals(result.Operation, operation), Is.True);

                // The rename is never reached and nothing is retried or
                // cleaned up.
                Assert.That(fileSystem.RenamedNames, Is.Empty);
                Assert.That(fileSystem.CreatedNames, Has.Count.EqualTo(expectCreated));

                AssertHandlesReleased(fileSystem);
            }
        }

        private static void AssertNoFilesystemContact(TrackingFileSystem fileSystem)
        {
            Assert.That(fileSystem.OpenedDirectoryPaths, Is.Empty);
            Assert.That(fileSystem.CreatedNames, Is.Empty);
            Assert.That(fileSystem.RenamedNames, Is.Empty);
        }

        private static void AssertHandlesReleased(TrackingFileSystem fileSystem)
        {
            foreach (TrackingMemoryStream stream in fileSystem.CreatedStreams)
            {
                Assert.That(stream.Disposed, Is.True, "every created stream must be released.");
            }

            foreach (NvencPublicationPlanCommitFile file in fileSystem.CreatedFiles)
            {
                Assert.That(file.IsDisposed, Is.True, "every created file handle must be released.");
            }

            foreach (NvencPublicationPlanCommitDirectory directory in fileSystem.OpenedDirectories)
            {
                Assert.That(directory.IsDisposed, Is.True, "every opened directory handle must be released.");
            }
        }

        private static void RequireWindows()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore("The capture index committer backend requires Windows file handles.");
            }
        }

        private CaptureRunRootLayout MakeSandboxLayout()
        {
            string root = Path.Combine(
                Path.GetTempPath(), "zantetsuken-captureindex-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _sandboxes.Add(root);
            return new CaptureRunRootLayout(
                Path.Combine(root, "staging"), Path.Combine(root, "final"), 1);
        }

        private sealed class TrackingFileSystem : INvencPublicationPlanCommitFileSystem
        {
            internal bool Supported = true;

            internal readonly List<string> OpenedDirectoryPaths = new List<string>();
            internal readonly List<NvencPublicationPlanCommitDirectory> OpenedDirectories =
                new List<NvencPublicationPlanCommitDirectory>();
            internal readonly List<string> CreatedNames = new List<string>();
            internal readonly List<NvencPublicationPlanCommitFile> CreatedFiles =
                new List<NvencPublicationPlanCommitFile>();
            internal readonly List<TrackingMemoryStream> CreatedStreams = new List<TrackingMemoryStream>();
            internal readonly List<string> RenamedNames = new List<string>();

            internal Exception OpenDirectoryFailure;
            internal Exception CreateNewFailure;
            internal Exception RenameFailure;
            internal Exception WriteFailure;

            public bool IsSupported => Supported;

            public NvencPublicationPlanCommitDirectory OpenDirectory(string absolutePath)
            {
                if (OpenDirectoryFailure != null)
                {
                    throw OpenDirectoryFailure;
                }

                OpenedDirectoryPaths.Add(absolutePath);
                NvencPublicationPlanCommitDirectory directory = new NvencPublicationPlanCommitDirectory(
                    new SafeFileHandle(IntPtr.Zero, true), absolutePath, "\\\\?\\" + absolutePath);
                OpenedDirectories.Add(directory);
                return directory;
            }

            public NvencPublicationPlanCommitFile CreateNew(
                NvencPublicationPlanCommitDirectory directory, string name)
            {
                if (CreateNewFailure != null)
                {
                    throw CreateNewFailure;
                }

                CreatedNames.Add(name);
                TrackingMemoryStream stream = new TrackingMemoryStream { WriteFailure = WriteFailure };
                CreatedStreams.Add(stream);
                NvencPublicationPlanCommitFile file = new NvencPublicationPlanCommitFile(null, stream);
                CreatedFiles.Add(file);
                return file;
            }

            public void Rename(
                NvencPublicationPlanCommitFile file,
                NvencPublicationPlanCommitDirectory directory,
                string newName)
            {
                RenamedNames.Add(newName);
                if (RenameFailure != null)
                {
                    throw RenameFailure;
                }
            }
        }

        private sealed class TrackingMemoryStream : MemoryStream
        {
            internal int WriteCalls;
            internal int ReadCalls;
            internal bool Disposed;
            internal Exception WriteFailure;

            public override void Write(byte[] buffer, int offset, int count)
            {
                WriteCalls++;
                if (WriteFailure != null)
                {
                    throw WriteFailure;
                }

                base.Write(buffer, offset, count);
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                ReadCalls++;
                return base.Read(buffer, offset, count);
            }

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
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
                CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator executionCoordinator =
                new CaptureRunInitializationExecutionCoordinator(new FakeProvisioner(), new FakeMarkerWriter());
            return executionCoordinator.Execute(batch);
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
