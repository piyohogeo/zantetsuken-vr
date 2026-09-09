using System;
using System.Diagnostics;
using System.IO;
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
    /// Contract tests for the Phase 0.11 production NVENC publication plan
    /// committer: the single synchronous concrete filesystem committer and its
    /// dedicated no-follow filesystem surface. Uses the finalized-and-frozen
    /// Run pipeline with a deterministic fake filesystem; no real GPU, NVENC,
    /// wall-clock race, sleep, or short negative wait is used.
    /// </summary>
    public class NvencRunPublicationPlanCommitterContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        private const byte Seed = 0x40;

        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private readonly System.Collections.Generic.List<string> _sandboxes =
            new System.Collections.Generic.List<string>();
        private readonly System.Collections.Generic.List<string> _junctions =
            new System.Collections.Generic.List<string>();

        [TearDown]
        public void TearDown()
        {
            foreach (string junction in _junctions)
            {
                try
                {
                    if (Directory.Exists(junction))
                    {
                        Directory.Delete(junction, false);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            _junctions.Clear();

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

        // ---- Validation before filesystem contact ----

        [Test]
        public void Commit_Null_ArgumentNullException_NoFilesystemContact()
        {
            CaptureRunRootLayout layout = MakeLayout();
            TrackingFileSystem fileSystem = new TrackingFileSystem();
            NvencRunPublicationPlanCommitter committer = new NvencRunPublicationPlanCommitter(layout, fileSystem);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => committer.Commit(null));
            Assert.That(ex.ParamName, Is.EqualTo("operation"));

            Assert.That(fileSystem.OpenedDirectoryPaths, Is.Empty);
            Assert.That(fileSystem.CreatedNames, Is.Empty);
            Assert.That(fileSystem.RenamedNames, Is.Empty);
        }

        [Test]
        public void Commit_InvalidOperation_ArgumentException_NoFilesystemContact()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                CaptureRunRootLayout layout = operation.RootLayout;
                h.SessionIssue.OwnershipLease.Dispose();
                Assert.That(operation.IsValid, Is.False);

                TrackingFileSystem fileSystem = new TrackingFileSystem();
                NvencRunPublicationPlanCommitter committer = new NvencRunPublicationPlanCommitter(layout, fileSystem);

                Assert.Throws<ArgumentException>(() => committer.Commit(operation));

                Assert.That(fileSystem.OpenedDirectoryPaths, Is.Empty);
                Assert.That(fileSystem.CreatedNames, Is.Empty);
                Assert.That(fileSystem.RenamedNames, Is.Empty);
            }
        }

        [Test]
        public void Commit_ForeignRootLayout_ArgumentException_NoFilesystemContact()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                // A content-identical but distinct root layout instance is a
                // foreign Run and must be rejected by exact reference.
                TrackingFileSystem fileSystem = new TrackingFileSystem();
                NvencRunPublicationPlanCommitter committer = new NvencRunPublicationPlanCommitter(MakeLayout(), fileSystem);

                Assert.Throws<ArgumentException>(() => committer.Commit(operation));

                Assert.That(fileSystem.OpenedDirectoryPaths, Is.Empty);
                Assert.That(fileSystem.CreatedNames, Is.Empty);
                Assert.That(fileSystem.RenamedNames, Is.Empty);
            }
        }

        [Test]
        public void Commit_UnsupportedFileSystem_ThrowsBeforeContact()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                TrackingFileSystem fileSystem = new TrackingFileSystem { Supported = false };
                NvencRunPublicationPlanCommitter committer = new NvencRunPublicationPlanCommitter(operation.RootLayout, fileSystem);

                Assert.Throws<CaptureArtifactNoFollowUnavailableException>(() => committer.Commit(operation));

                Assert.That(fileSystem.OpenedDirectoryPaths, Is.Empty);
                Assert.That(fileSystem.CreatedNames, Is.Empty);
                Assert.That(fileSystem.RenamedNames, Is.Empty);
            }
        }

        // ---- Success path ----

        [Test]
        public void Commit_Success_WritesCanonicalOnce_NonOverwritingRename_CommittedReceipt()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                TrackingFileSystem fileSystem = new TrackingFileSystem();
                NvencRunPublicationPlanCommitter committer = new NvencRunPublicationPlanCommitter(operation.RootLayout, fileSystem);

                NvencRunPublicationPlanCommitAttemptResult result = committer.Commit(operation);

                Assert.That(result.IsCommitted, Is.True);
                Assert.That(result.IsFailedBeforeRename, Is.False);
                Assert.That(result.IsCommitOutcomeUnknown, Is.False);
                Assert.That(result.IsValid, Is.True);
                Assert.That(result.Receipt, Is.Not.Null);
                Assert.That(result.Receipt.IsIssuedFor(committer, operation), Is.True);

                // The staging Run root is exact.
                Assert.That(fileSystem.OpenedDirectoryPaths, Has.Count.EqualTo(1));
                Assert.That(fileSystem.OpenedDirectoryPaths[0], Is.EqualTo(operation.RootLayout.StagingRunRoot));

                // The dedicated tmp and final basenames are exact.
                Assert.That(fileSystem.CreatedNames, Is.EqualTo(
                    new[] { NvencRunPublicationPlanCommitOperation.PreCommitBasename }));
                Assert.That(fileSystem.RenamedNames, Is.EqualTo(
                    new[] { NvencRunPublicationPlanCommitOperation.FinalBasename }));

                // Canonical bytes are written exactly once to the dedicated tmp.
                Assert.That(fileSystem.CreatedStreams, Has.Count.EqualTo(1));
                Assert.That(fileSystem.CreatedStreams[0].WriteCalls, Is.EqualTo(1));
                byte[] expected = CapturePublicationPlanCodec.SerializeCanonical(operation.Plan);
                Assert.That(fileSystem.CreatedStreams[0].ToArray(), Is.EqualTo(expected));

                // One create and one rename per call.
                Assert.That(fileSystem.CreatedNames, Has.Count.EqualTo(1));
                Assert.That(fileSystem.RenamedNames, Has.Count.EqualTo(1));

                // Handles and streams are released on the success path.
                Assert.That(fileSystem.CreatedFiles[0].IsDisposed, Is.True);
                Assert.That(fileSystem.CreatedStreams[0].Disposed, Is.True);
                Assert.That(fileSystem.OpenedDirectories[0].IsDisposed, Is.True);
            }
        }

        [Test]
        public void Commit_DoesNotContactLegacyPublicationPlanTemporary()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                TrackingFileSystem fileSystem = new TrackingFileSystem();
                NvencRunPublicationPlanCommitter committer = new NvencRunPublicationPlanCommitter(operation.RootLayout, fileSystem);

                Assert.That(committer.Commit(operation).IsCommitted, Is.True);

                Assert.That(fileSystem.CreatedNames, Does.Not.Contain("publication.plan.tmp"));
                Assert.That(fileSystem.RenamedNames, Does.Not.Contain("publication.plan.tmp"));
            }
        }

        // ---- Pre-rename failure classification ----

        [Test]
        public void Commit_OpenDirectoryFailure_FailedBeforeRename_NoTemporaryDeletion()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                TrackingFileSystem fileSystem = new TrackingFileSystem { OpenDirectoryFailure = new IOException("open failed") };
                NvencRunPublicationPlanCommitter committer = new NvencRunPublicationPlanCommitter(operation.RootLayout, fileSystem);

                NvencRunPublicationPlanCommitAttemptResult result = committer.Commit(operation);

                Assert.That(result.IsFailedBeforeRename, Is.True);
                Assert.That(result.Receipt, Is.Null);
                Assert.That(fileSystem.CreatedNames, Is.Empty);
                Assert.That(fileSystem.RenamedNames, Is.Empty);
            }
        }

        [Test]
        public void Commit_CreateNewFailure_FailedBeforeRename_NoTemporaryDeletion()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                TrackingFileSystem fileSystem = new TrackingFileSystem { CreateNewFailure = new IOException("create failed") };
                NvencRunPublicationPlanCommitter committer = new NvencRunPublicationPlanCommitter(operation.RootLayout, fileSystem);

                NvencRunPublicationPlanCommitAttemptResult result = committer.Commit(operation);

                Assert.That(result.IsFailedBeforeRename, Is.True);
                Assert.That(result.Receipt, Is.Null);
                Assert.That(fileSystem.OpenedDirectoryPaths, Has.Count.EqualTo(1));
                Assert.That(fileSystem.CreatedNames, Is.Empty);
                Assert.That(fileSystem.RenamedNames, Is.Empty);
                Assert.That(fileSystem.OpenedDirectories[0].IsDisposed, Is.True);
            }
        }

        [Test]
        public void Commit_WriteFailure_FailedBeforeRename_NoRenameNoTemporaryDeletion()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                TrackingFileSystem fileSystem = new TrackingFileSystem { WriteFailure = new IOException("write failed") };
                NvencRunPublicationPlanCommitter committer = new NvencRunPublicationPlanCommitter(operation.RootLayout, fileSystem);

                NvencRunPublicationPlanCommitAttemptResult result = committer.Commit(operation);

                Assert.That(result.IsFailedBeforeRename, Is.True);
                Assert.That(result.Receipt, Is.Null);
                Assert.That(fileSystem.CreatedNames, Has.Count.EqualTo(1));
                Assert.That(fileSystem.RenamedNames, Is.Empty);

                // The dedicated tmp is never deleted and the stream is released.
                Assert.That(fileSystem.CreatedStreams[0].Disposed, Is.True);
                Assert.That(fileSystem.OpenedDirectories[0].IsDisposed, Is.True);
            }
        }

        // ---- Rename-time classification ----

        [Test]
        public void Commit_RenameFailure_CommitOutcomeUnknown_NoReReadNoDeleteNoRetry()
        {
            using (Harness h = Harness.Create())
            {
                FinalizeAndFreeze(h, 1);
                Assert.That(
                    h.RunCoordinator.TryPreparePublicationPlanCommit(Hash64, out NvencRunPublicationPlanCommitOperation operation),
                    Is.True);

                TrackingFileSystem fileSystem = new TrackingFileSystem { RenameFailure = new IOException("rename failed") };
                NvencRunPublicationPlanCommitter committer = new NvencRunPublicationPlanCommitter(operation.RootLayout, fileSystem);

                NvencRunPublicationPlanCommitAttemptResult result = committer.Commit(operation);

                Assert.That(result.IsCommitOutcomeUnknown, Is.True);
                Assert.That(result.IsCommitted, Is.False);
                Assert.That(result.IsFailedBeforeRename, Is.False);
                Assert.That(result.Receipt, Is.Null);

                // Exactly one create, one write, and one rename; no second
                // rename, no retry, no re-read, and no deletion.
                Assert.That(fileSystem.CreatedNames, Has.Count.EqualTo(1));
                Assert.That(fileSystem.CreatedStreams[0].WriteCalls, Is.EqualTo(1));
                Assert.That(fileSystem.RenamedNames, Has.Count.EqualTo(1));

                // Handles and streams are released even on the unknown path.
                Assert.That(fileSystem.CreatedFiles[0].IsDisposed, Is.True);
                Assert.That(fileSystem.CreatedStreams[0].Disposed, Is.True);
                Assert.That(fileSystem.OpenedDirectories[0].IsDisposed, Is.True);
            }
        }

        // ---- Shape and source audits ----

        [Test]
        public void Committer_SealedInternalBound_NoDisposable()
        {
            Type type = typeof(NvencRunPublicationPlanCommitter);

            Assert.That(type.IsClass, Is.True);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(typeof(INvencRunPublicationPlanCommitter).IsAssignableFrom(type), Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);

            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            Assert.That(fields, Has.Length.EqualTo(2));
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(CaptureRunRootLayout)));
            Assert.That(fields[1].FieldType, Is.EqualTo(typeof(INvencPublicationPlanCommitFileSystem)));
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.IsInitOnly, Is.True, field.Name + " must be readonly.");
            }
        }

        [Test]
        public void FileSystemInterface_SingleCapabilityAndThreeMethods_NoDeleteNoRead()
        {
            Type type = typeof(INvencPublicationPlanCommitFileSystem);

            Assert.That(type.IsInterface, Is.True);
            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.GetInterfaces(), Is.Empty);

            PropertyInfo[] properties = type.GetProperties();
            Assert.That(properties, Has.Length.EqualTo(1));
            Assert.That(properties[0].Name, Is.EqualTo("IsSupported"));

            // Exactly three non-accessor methods: open-directory, create-new,
            // and rename. No delete, re-read, flush, or re-open surface exists.
            System.Collections.Generic.List<string> names = new System.Collections.Generic.List<string>();
            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (!method.IsSpecialName)
                {
                    names.Add(method.Name);
                }
            }

            names.Sort();
            Assert.That(names, Is.EqualTo(new[] { "CreateNew", "OpenDirectory", "Rename" }));
        }

        [Test]
        public void CommitterSource_Audit_NoForbiddenApiOrLegacyBasename()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencRunPublicationPlanCommitter.cs"));

            string[] forbidden =
            {
                "WritePlan", "publication.plan.tmp", "Flush(true)", "FlushFileBuffers",
                "File.", "Directory.", "Path.", "FileStream", "ReadAllBytes", ".Read(",
                "File.Move", "File.Delete", "Directory.Delete",
                "new Thread", "Task", "Thread.Sleep", "SpinWait",
                "Disposition", "Registry", "OwnershipLease",
                "DllImport", "IntPtr", "SafeHandle", "JsonUtility",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "committer source must not contain: " + word);
            }

            Assert.That(source, Does.Contain("CapturePublicationPlanCodec.SerializeCanonical"));
            Assert.That(source, Does.Contain("NvencRunPublicationPlanCommitOperation.PreCommitBasename"));
            Assert.That(source, Does.Contain("NvencRunPublicationPlanCommitOperation.FinalBasename"));
            Assert.That(source, Does.Contain("StagingRunRoot"));
        }

        [Test]
        public void FileSystemBackendSource_Audit_NoDurabilityNoDeleteNoLegacyBasename()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencPublicationPlanCommitFileSystem.cs"));

            string[] forbidden =
            {
                "FlushFileBuffers", "publication.plan.tmp",
                "File.Move", "File.Delete", "Directory.Delete", "WritePlan",
                "DeleteByHandle", "FileDispositionInfo", "DeleteFile",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "backend source must not contain: " + word);
            }

            // The rename destination is handle-relative and the directory open
            // is a true no-follow open.
            Assert.That(source, Does.Contain("NtSetInformationFile"));
            Assert.That(source, Does.Contain("WriteIntPtr"));
            Assert.That(source, Does.Contain("DangerousGetHandle"));
            Assert.That(source, Does.Contain("FileFlagOpenReparsePoint"));
            Assert.That(source, Does.Contain("ReplaceIfExists"));
            Assert.That(source, Does.Contain("NtCreateFile"));
        }

        // ---- Windows backend integration ----

        [Test]
        public void Backend_Rename_SucceedsAndPublishesFinal()
        {
            RequireWindows();
            string root = MakeSandbox();

            byte[] bytes = { 1, 2, 3, 4, 5 };
            NvencPublicationPlanCommitFileSystem fileSystem = NvencPublicationPlanCommitFileSystem.Create();
            using (NvencPublicationPlanCommitDirectory directory = fileSystem.OpenDirectory(root))
            using (NvencPublicationPlanCommitFile file = fileSystem.CreateNew(
                directory, NvencRunPublicationPlanCommitOperation.PreCommitBasename))
            {
                file.Stream.Write(bytes, 0, bytes.Length);
                file.Stream.Flush();
                fileSystem.Rename(file, directory, NvencRunPublicationPlanCommitOperation.FinalBasename);
            }

            Assert.That(
                File.ReadAllBytes(Path.Combine(root, NvencRunPublicationPlanCommitOperation.FinalBasename)),
                Is.EqualTo(bytes));
            Assert.That(
                File.Exists(Path.Combine(root, NvencRunPublicationPlanCommitOperation.PreCommitBasename)),
                Is.False);
        }

        [Test]
        public void Backend_ExistingFinal_NonOverwritingRenameFails()
        {
            RequireWindows();
            string root = MakeSandbox();

            byte[] existing = { 9, 9, 9 };
            File.WriteAllBytes(Path.Combine(root, NvencRunPublicationPlanCommitOperation.FinalBasename), existing);

            NvencPublicationPlanCommitFileSystem fileSystem = NvencPublicationPlanCommitFileSystem.Create();
            using (NvencPublicationPlanCommitDirectory directory = fileSystem.OpenDirectory(root))
            using (NvencPublicationPlanCommitFile file = fileSystem.CreateNew(
                directory, NvencRunPublicationPlanCommitOperation.PreCommitBasename))
            {
                byte[] bytes = { 1, 2, 3 };
                file.Stream.Write(bytes, 0, bytes.Length);
                file.Stream.Flush();
                Assert.Throws<IOException>(() => fileSystem.Rename(
                    file, directory, NvencRunPublicationPlanCommitOperation.FinalBasename));
            }

            // The existing final is untouched and the temporary remains.
            Assert.That(
                File.ReadAllBytes(Path.Combine(root, NvencRunPublicationPlanCommitOperation.FinalBasename)),
                Is.EqualTo(existing));
            Assert.That(
                File.Exists(Path.Combine(root, NvencRunPublicationPlanCommitOperation.PreCommitBasename)),
                Is.True);
        }

        [Test]
        public void Backend_LeafReparseDirectory_Rejected()
        {
            RequireWindows();
            string sandbox = MakeSandbox();
            string target = Path.Combine(sandbox, "target");
            string link = Path.Combine(sandbox, "link");
            Directory.CreateDirectory(target);
            CreateJunction(link, target);
            _junctions.Add(link);

            NvencPublicationPlanCommitFileSystem fileSystem = NvencPublicationPlanCommitFileSystem.Create();
            Assert.Throws<IOException>(() => fileSystem.OpenDirectory(link));
        }

        // ---- Helpers ----

        private static void WaitSettled(ManualResetEventSlim settled, string message)
        {
            Assert.That(settled.Wait(WatchdogTimeoutMs), Is.True, message);
        }

        private static void StopFinalizedBackend(Harness h, int frameCount)
        {
            for (long id = 1; id <= frameCount; id++)
            {
                h.AcceptAndAppendChunk(id, 64, Seed);
            }

            Assert.That(h.RunCoordinator.TryBeginDrain(out _), Is.True);
            for (long id = 1; id <= frameCount; id++)
            {
                Assert.That(h.RunCoordinator.TryReflectCompletion(
                    MakeCompletion(id, CaptureFrameCompletionStatus.Succeeded)), Is.True);
            }
            h.SubmitDrained = true;

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTerminal(), Is.True);
            WaitSettled(h.SettledEvent, "worker did not converge the finalize request");
            Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True);

            h.SettledEvent.Reset();
            Assert.That(h.RunCoordinator.TryRequestTeardown(), Is.True);
            WaitSettled(h.SettledEvent, "worker did not complete the teardown");
            h.WaitForPhysicalStop("worker did not physically exit after the teardown");

            Assert.That(h.Worker.TeardownCompleted, Is.True);
            Assert.That(h.Worker.IsStopped, Is.True);

            h.SubmitWorker.Dispose();
        }

        private static void CompleteTraceFreeze(Harness h)
        {
            Assert.That(h.RunCoordinator.TryCompleteMainThreadTextureTeardown(), Is.True);
            Assert.That(h.RunCoordinator.TryCompleteBackendJoin(), Is.True);
            Assert.That(h.TraceRecorder.TryTrigger(), Is.True);

            ForcedDropFrameIdSet forced = MakeForcedDropSet(h);
            FreezeTerminalCheckpoint checkpoint = MakeCheckpoint(h);
            Assert.That(h.RunCoordinator.TryCompleteTraceFreeze(forced, checkpoint, out _), Is.True);
        }

        private static void FinalizeAndFreeze(Harness h, int frameCount)
        {
            StopFinalizedBackend(h, frameCount);
            CompleteTraceFreeze(h);

            Assert.That(h.RunCoordinator.Disposition, Is.EqualTo(NvencRunEvidenceDisposition.Finalized));
            Assert.That(h.Context.TryGetFinalizationResult(out _), Is.True);
        }

        private static CaptureFrameCompletion MakeCompletion(
            long captureFrameId,
            CaptureFrameCompletionStatus status,
            int producedArtifactCount = 0,
            long testRunId = 1)
        {
            CaptureFrameWorkToken token = new CaptureFrameWorkToken(Guid.NewGuid(), 0, 1, testRunId, captureFrameId);
            ExceptionDispatchInfo failure = status == CaptureFrameCompletionStatus.Failed
                ? ExceptionDispatchInfo.Capture(new InvalidOperationException("completion failed"))
                : null;
            return new CaptureFrameCompletion(token, captureFrameId, status, true, producedArtifactCount, failure);
        }

        private static CaptureRunInitializationSessionIssue MakeIssue()
        {
            CaptureRunRootLayout layout = MakeLayout();
            CaptureRunInitializationExecutionReceipt receipt = MakeExecutionReceipt(layout);
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true) { Tag = "first" };
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true) { Tag = "second" };
            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);
            CaptureRunInitializationSessionOwnershipLease owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(receipt);
            return CaptureRunInitializationSessionFactory.Create(owner, identity, evidence);
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
            CaptureRunInitializationDocumentSet documents = CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeMarkerWriter());
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

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static void RequireWindows()
        {
            if (!IsWindows)
            {
                Assert.Ignore("The NVENC publication plan committer filesystem backend requires Windows file handles.");
            }
        }

        private string MakeSandbox()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "zantetsuken-plancommitter-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            _sandboxes.Add(sandbox);
            return sandbox;
        }

        private static void CreateJunction(string linkPath, string targetPath)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo(
                "cmd.exe", "/c mklink /J \"" + linkPath + "\" \"" + targetPath + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (Process process = Process.Start(startInfo))
            {
                // Bounded wait: a hung cmd.exe must never stall the suite.
                if (!process.WaitForExit(WatchdogTimeoutMs))
                {
                    try
                    {
                        process.Kill();
                        process.WaitForExit(WatchdogTimeoutMs);
                    }
                    catch (Exception killFailure)
                    {
                        throw new InvalidOperationException(
                            "mklink /J did not terminate within the watchdog and could not be killed.", killFailure);
                    }

                    throw new InvalidOperationException(
                        "mklink /J did not terminate within the watchdog.");
                }

                string output;
                try
                {
                    output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                }
                catch (Exception readFailure)
                {
                    output = "(output unavailable: " + readFailure.Message + ")";
                }

                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("mklink /J failed: " + output);
                }
            }
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

        private static NvencOrderedSubmitWorkerService BuildSubmitWorker(NvencCaptureProcessState state)
        {
            NvencCaptureWorkSlotPool work = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool samples = new NvencEncodeSampleSlotPool(state);
            NvencGpuConversionSyncPool sync = new NvencGpuConversionSyncPool(state);
            NvencSubmitToOutputCreditPool submitCredits = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCredits = new NvencFrameCompletionCreditPool(state);
            NvencSourceResourceReleaseCoordinator release = new NvencSourceResourceReleaseCoordinator(
                state, work, samples, sync, submitCredits, frameCredits,
                new FakeSourceReadCompletedSource(), new NvencSourceSurfaceReturnBoundary(), Guid.NewGuid());
            NvencOrderedSubmitProcessor processor = new NvencOrderedSubmitProcessor(
                state,
                new NvencFixedSpscQueue<NvencSubmissionRecord>(),
                new NvencFixedSpscQueue<NvencSubmitToOutputRecord>(),
                work, samples, release, new FakeSubmitter());
            return new NvencOrderedSubmitWorkerService(state, processor);
        }

        // ---- Fakes ----

        private sealed class TrackingFileSystem : INvencPublicationPlanCommitFileSystem
        {
            internal bool Supported = true;
            internal readonly System.Collections.Generic.List<string> OpenedDirectoryPaths = new System.Collections.Generic.List<string>();
            internal readonly System.Collections.Generic.List<NvencPublicationPlanCommitDirectory> OpenedDirectories = new System.Collections.Generic.List<NvencPublicationPlanCommitDirectory>();
            internal readonly System.Collections.Generic.List<string> CreatedNames = new System.Collections.Generic.List<string>();
            internal readonly System.Collections.Generic.List<NvencPublicationPlanCommitFile> CreatedFiles = new System.Collections.Generic.List<NvencPublicationPlanCommitFile>();
            internal readonly System.Collections.Generic.List<TrackingMemoryStream> CreatedStreams = new System.Collections.Generic.List<TrackingMemoryStream>();
            internal readonly System.Collections.Generic.List<string> RenamedNames = new System.Collections.Generic.List<string>();

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

            public NvencPublicationPlanCommitFile CreateNew(NvencPublicationPlanCommitDirectory directory, string name)
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

            public void Rename(NvencPublicationPlanCommitFile file, NvencPublicationPlanCommitDirectory directory, string newName)
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

            protected override void Dispose(bool disposing)
            {
                Disposed = true;
                base.Dispose(disposing);
            }
        }

        private sealed class FakeCommitter : INvencRunPublicationPlanCommitter
        {
            internal NvencRunPublicationPlanCommitStatus Status = NvencRunPublicationPlanCommitStatus.Committed;

            public NvencRunPublicationPlanCommitAttemptResult Commit(
                NvencRunPublicationPlanCommitOperation operation)
            {
                if (operation == null)
                {
                    throw new ArgumentNullException(nameof(operation));
                }

                if (!operation.IsValid)
                {
                    throw new ArgumentException("Operation is not valid.", nameof(operation));
                }

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
            internal Exception ExceptionToThrow;
            internal bool UseOverride;
            internal NvencRunArtifactPublicationAttemptResult OverrideResult;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunArtifactPublicationAttemptResult Publish(
                NvencRunArtifactPublicationOperation operation)
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
            private int _finalizeCount;

            public NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength)
            {
                return NvencRunChunkAppendOutcome.Appended;
            }

            public NvencRunChunkFinalizationReceipt FinalizeChunk(NvencRunChunkFinalizationOperation operation)
            {
                Interlocked.Increment(ref _finalizeCount);

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
            internal bool Result = true;
            internal int ResultLength = 1024;

            public bool TryCopyCompletedOutput(
                in CaptureFrameWorkToken workToken,
                in NvencEncodeSampleSlotLease sampleSlot,
                byte[] destination,
                int destinationCapacity,
                out int validLength)
            {
                validLength = ResultLength;
                return Result;
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
            private int _callCount;

            public NvencOutputWorkerTeardownReceipt TearDown()
            {
                Interlocked.Increment(ref _callCount);
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

        private sealed class FakeIndexCommitter : INvencRunCaptureIndexCommitter
        {
            private int _callCount;
            internal NvencRunCaptureIndexCommitStatus Status = NvencRunCaptureIndexCommitStatus.Committed;
            internal Exception ExceptionToThrow;
            internal bool UseOverride;
            internal NvencRunCaptureIndexCommitAttemptResult OverrideResult;
            internal ManualResetEventSlim Entered;
            internal ManualResetEventSlim Release;

            internal int CallCount => Volatile.Read(ref _callCount);

            public NvencRunCaptureIndexCommitAttemptResult Commit(
                NvencRunCaptureIndexCommitOperation operation)
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

                if (Status == NvencRunCaptureIndexCommitStatus.Failed)
                {
                    return NvencRunCaptureIndexCommitAttemptResult.Failed(this, operation);
                }

                return NvencRunCaptureIndexCommitAttemptResult.Committed(this, operation);
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

        private sealed class Harness : IDisposable
        {
            internal NvencCaptureProcessState State;
            internal NvencOwnedAccessUnitBuffer Buffer;
            internal FakeWriter Writer = new FakeWriter();
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

            internal CaptureRunInitializationSessionIssue SessionIssue;
            internal TraceLogger TraceLogger;
            internal TraceFlightRecorder TraceRecorder;
            internal CaptureFrameFreezeTerminalCoordinator FreezeTerminalCoordinator;
            internal CaptureFrameDraftRegistry DraftRegistry;
            internal CaptureFrameDraftTerminalIntentQueue DraftQueue;
            internal NvencTraceFreezeCoordinator TraceFreeze;

            internal FakeCommitter Committer;
            internal FakePublisher Publisher;
            internal FakeIndexCommitter IndexCommitter;
            internal FakeRunCompleter RunCompleter;
            internal NvencRunPublicationService Service;
            internal FakeCleanupCleaner CleanupCleaner;
            internal NvencRunCaptureCompleteCleanupExecutionCoordinator CleanupExecution;

            private readonly Action _settledHandler;

            internal bool SubmitDrained
            {
                set => SetField(SubmitWorker, "_drainCompleted", value);
            }

            internal Harness()
            {
                State = new NvencCaptureProcessState();

                Buffer = new NvencOwnedAccessUnitBuffer(State);
                Writer = new FakeWriter();
                Sink = new NvencRunChunkSink(State, Buffer, Writer);
                FinalizationCoordinator = new NvencRunChunkFinalizationCoordinator(Writer);
                SessionIssue = MakeIssue();
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
                IndexCommitter = new FakeIndexCommitter();
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

                RunCoordinator = new NvencCaptureRunCoordinator(
                    State, SubmitWorker, Worker, Context, Slot, MainThreadTeardown, BackendJoin, SessionIssue, TraceFreeze, Service, CleanupExecution);

                SettledEvent = new ManualResetEventSlim(false);
                _settledHandler = () => SettledEvent.Set();
                Worker.Settled += _settledHandler;
            }

            internal static Harness Create()
            {
                Harness h = new Harness();

                h.SettledEvent.Reset();
                h.Worker.Notify();
                Assert.That(h.SettledEvent.Wait(WatchdogTimeoutMs), Is.True, "worker did not settle initially");

                return h;
            }

            internal void AcceptAndAppendChunk(long frameId, int length, byte seed)
            {
                Assert.That(Context.TryRecordAcceptedFrame(frameId), Is.True);
                AppendChunk(frameId, length, seed);
            }

            internal void AppendChunk(long frameId, int length, byte seed)
            {
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
