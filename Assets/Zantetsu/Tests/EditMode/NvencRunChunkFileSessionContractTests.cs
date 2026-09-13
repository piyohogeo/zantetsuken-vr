using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Real-filesystem contract tests for the Phase 0.11 Run chunk file
    /// session: the exact <c>.partial</c> file pinned to a no-follow-verified
    /// staging Run root handle, from create-new through append, close, and the
    /// single non-overwriting staging rename.
    /// </summary>
    public class NvencRunChunkFileSessionContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";
        private const string PendingFileName = "chunk-0.nvenc-idr-chunk-v1.h264.partial";
        private const string StagingFileName = "chunk-0.nvenc-idr-chunk-v1.h264";

        private readonly List<string> _sandboxes = new List<string>();
        private readonly List<string> _junctions = new List<string>();

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

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static void RequireWindows()
        {
            if (!IsWindows)
            {
                Assert.Ignore("Run chunk file session tests require Windows file handles.");
            }
        }

        private (string sandbox, string staging, string final) MakeSandbox()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "zantetsuken-chunk-" + Guid.NewGuid().ToString("N"));
            string staging = Path.Combine(sandbox, "staging");
            string final = Path.Combine(sandbox, "final");
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(final);
            _sandboxes.Add(sandbox);
            return (sandbox, staging, final);
        }

        private static CaptureRunRootLayout MakeLayout(string staging, string final)
        {
            return new CaptureRunRootLayout(staging, final, 1);
        }

        private static string ChunksDir(CaptureRunRootLayout layout)
        {
            return Path.Combine(layout.StagingRunRoot, "chunks");
        }

        private static string PendingPath(CaptureRunRootLayout layout)
        {
            return Path.Combine(ChunksDir(layout), PendingFileName);
        }

        private static string StagingPath(CaptureRunRootLayout layout)
        {
            return Path.Combine(ChunksDir(layout), StagingFileName);
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
                process.WaitForExit();
                if (process.ExitCode != 0)
                {
                    throw new InvalidOperationException("mklink /J failed: " + process.StandardError.ReadToEnd());
                }
            }
        }

        private static CaptureRunInitializationExecutionReceipt MakeExecutionReceipt(CaptureRunRootLayout layout)
        {
            CaptureRunInitializationDocumentSet documents = CaptureRunInitializationDocumentSetFactory.Create(layout, InitId);
            CaptureRunInitializationWriteBatch batch = new CaptureRunInitializationWriteBatch(documents);
            CaptureRunInitializationExecutionCoordinator executionCoordinator = new CaptureRunInitializationExecutionCoordinator(
                new FakeProvisioner(), new FakeWriter());
            return executionCoordinator.Execute(batch);
        }

        private static CaptureRunInitializationSessionOwnershipLease MakeOwnershipLease(
            CaptureRunRootLayout layout, List<string> disposeLog)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true, disposeLog) { Tag = "first" };
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true, disposeLog) { Tag = "second" };
            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);
            return CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
        }

        private static CaptureRunLockIdentityEvidence MakeIdentityEvidence(
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            return CaptureRunLockIdentityEvidence.Create(ownershipLease, ownershipLease.LockPathSet);
        }

        private static CaptureRunInitializationSessionIssue MakeIssue(
            CaptureRunRootLayout layout, List<string> disposeLog)
        {
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, disposeLog);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(MakeExecutionReceipt(layout));
            return CaptureRunInitializationSession.IssuanceProof.Mint(owner, identity, evidence);
        }

        private static string RuntimeDirectory()
        {
            return Path.Combine(Path.Combine(Application.dataPath, ".."), "Assets/Zantetsu/Runtime/Observability");
        }

        private static string ExtractMethodBody(string source, string methodName)
        {
            int signature = source.IndexOf(methodName + "(", StringComparison.Ordinal);
            Assert.That(signature, Is.GreaterThanOrEqualTo(0), "Method signature not found: " + methodName);

            int open = source.IndexOf('{', signature);
            Assert.That(open, Is.GreaterThanOrEqualTo(0), "Method body not found: " + methodName);

            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{')
                {
                    depth++;
                }
                else if (source[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(open, i - open + 1);
                    }
                }
            }

            Assert.Fail("Method body not terminated: " + methodName);
            return null;
        }

        // ---- File allocation ----

        [Test]
        public void Create_PendingFileCreatedAtExactPath_NoStagingFile()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            using (NvencRunChunkFileSession session = NvencRunChunkFileSession.Create(MakeIssue(layout, null)))
            {
                Assert.That(session, Is.Not.Null);
                Assert.That(File.Exists(PendingPath(layout)), Is.True);
                Assert.That(File.Exists(StagingPath(layout)), Is.False);
                Assert.That(Directory.Exists(ChunksDir(layout)), Is.True);
            }
        }

        [Test]
        public void Create_StagingAlreadyExists_RejectedWithoutOverwrite()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(ChunksDir(layout));

            byte[] existing = new byte[] { 9, 8, 7, 6 };
            File.WriteAllBytes(StagingPath(layout), existing);

            Assert.Throws<IOException>(() => NvencRunChunkFileSession.Create(MakeIssue(layout, null)));

            Assert.That(File.ReadAllBytes(StagingPath(layout)), Is.EqualTo(existing));
            Assert.That(File.Exists(PendingPath(layout)), Is.False);
        }

        [Test]
        public void Create_PendingAlreadyExists_RejectedWithoutTruncate()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(ChunksDir(layout));

            byte[] existing = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
            File.WriteAllBytes(PendingPath(layout), existing);

            Assert.Throws<IOException>(() => NvencRunChunkFileSession.Create(MakeIssue(layout, null)));

            Assert.That(File.ReadAllBytes(PendingPath(layout)), Is.EqualTo(existing));
            Assert.That(File.Exists(StagingPath(layout)), Is.False);
        }

        [Test]
        public void Create_ChunksDirectoryJunction_Rejected()
        {
            RequireWindows();
            (string sandbox, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            string outside = Path.Combine(sandbox, "outside");
            Directory.CreateDirectory(outside);
            CreateJunction(ChunksDir(layout), outside);
            _junctions.Add(ChunksDir(layout));

            Assert.Throws<IOException>(() => NvencRunChunkFileSession.Create(MakeIssue(layout, null)));

            Assert.That(Directory.GetFileSystemEntries(outside), Is.Empty);
        }

        [Test]
        public void Create_RunRootJunction_Rejected()
        {
            RequireWindows();
            (string sandbox, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);

            string outside = Path.Combine(sandbox, "outside");
            Directory.CreateDirectory(outside);
            Directory.CreateDirectory(Path.GetDirectoryName(layout.StagingRunRoot));
            CreateJunction(layout.StagingRunRoot, outside);
            _junctions.Add(layout.StagingRunRoot);

            Assert.Throws<IOException>(() => NvencRunChunkFileSession.Create(MakeIssue(layout, null)));

            Assert.That(Directory.GetFileSystemEntries(outside), Is.Empty);
        }

        [Test]
        public void Create_ChunksIsFile_Rejected()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);
            File.WriteAllBytes(ChunksDir(layout), new byte[] { 1 });

            Assert.Throws<IOException>(() => NvencRunChunkFileSession.Create(MakeIssue(layout, null)));

            Assert.That(File.ReadAllBytes(ChunksDir(layout)), Is.EqualTo(new byte[] { 1 }));
        }

        // ---- Append ----

        [Test]
        public void Append_MultipleAppends_ConcatenatedInOrder()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            using (NvencRunChunkFileSession session = NvencRunChunkFileSession.Create(MakeIssue(layout, null)))
            {
                Assert.That(session.Append(new byte[] { 1, 2, 3 }, 0, 3), Is.EqualTo(NvencRunChunkAppendOutcome.Appended));
                Assert.That(session.Append(new byte[] { 0, 4, 5, 0 }, 1, 2), Is.EqualTo(NvencRunChunkAppendOutcome.Appended));
                Assert.That(session.Append(new byte[] { 6 }, 0, 1), Is.EqualTo(NvencRunChunkAppendOutcome.Appended));

                session.CloseAppendHandle();
                session.MovePendingToStaging();
            }

            Assert.That(File.ReadAllBytes(StagingPath(layout)), Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6 }));
            Assert.That(File.Exists(PendingPath(layout)), Is.False);
        }

        [Test]
        public void Append_InvalidRange_RejectedBeforeWrite()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            using (NvencRunChunkFileSession session = NvencRunChunkFileSession.Create(MakeIssue(layout, null)))
            {
                Assert.That(session.Append(null, 0, 1), Is.EqualTo(NvencRunChunkAppendOutcome.RejectedBeforeWrite));
                Assert.That(session.Append(new byte[4], 0, 0), Is.EqualTo(NvencRunChunkAppendOutcome.RejectedBeforeWrite));
                Assert.That(session.Append(new byte[4], -1, 1), Is.EqualTo(NvencRunChunkAppendOutcome.RejectedBeforeWrite));
                Assert.That(session.Append(new byte[4], 5, 1), Is.EqualTo(NvencRunChunkAppendOutcome.RejectedBeforeWrite));

                session.CloseAppendHandle();
                session.MovePendingToStaging();
            }

            Assert.That(File.ReadAllBytes(StagingPath(layout)), Is.Empty);
        }

        [Test]
        public void Append_AfterClose_RejectedBeforeWrite()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            using (NvencRunChunkFileSession session = NvencRunChunkFileSession.Create(MakeIssue(layout, null)))
            {
                Assert.That(session.Append(new byte[] { 1, 2, 3 }, 0, 3), Is.EqualTo(NvencRunChunkAppendOutcome.Appended));
                session.CloseAppendHandle();

                Assert.That(session.Append(new byte[] { 9, 9 }, 0, 2), Is.EqualTo(NvencRunChunkAppendOutcome.RejectedBeforeWrite));

                session.MovePendingToStaging();
            }

            Assert.That(File.ReadAllBytes(StagingPath(layout)), Is.EqualTo(new byte[] { 1, 2, 3 }));
        }

        // ---- Close and rename ----

        [Test]
        public void Move_BeforeClose_Rejected()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            using (NvencRunChunkFileSession session = NvencRunChunkFileSession.Create(MakeIssue(layout, null)))
            {
                session.Append(new byte[] { 1, 2, 3 }, 0, 3);

                Assert.Throws<InvalidOperationException>(() => session.MovePendingToStaging());
            }

            Assert.That(File.Exists(PendingPath(layout)), Is.True);
            Assert.That(File.Exists(StagingPath(layout)), Is.False);
        }

        [Test]
        public void Move_StagingReappearsAtMoveTime_FailsWithoutOverwrite()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            using (NvencRunChunkFileSession session = NvencRunChunkFileSession.Create(MakeIssue(layout, null)))
            {
                session.Append(new byte[] { 1, 2, 3 }, 0, 3);
                session.CloseAppendHandle();

                byte[] reappeared = new byte[] { 7, 7, 7, 7 };
                File.WriteAllBytes(StagingPath(layout), reappeared);

                Assert.Throws<IOException>(() => session.MovePendingToStaging());
            }

            Assert.That(File.ReadAllBytes(StagingPath(layout)), Is.EqualTo(new byte[] { 7, 7, 7, 7 }));
            Assert.That(File.Exists(PendingPath(layout)), Is.True);
        }

        [Test]
        public void DoubleClose_DoubleMove_DoubleDispose_NoSideEffectReexecution()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            NvencRunChunkFileSession session = NvencRunChunkFileSession.Create(MakeIssue(layout, null));
            try
            {
                session.Append(new byte[] { 5, 6, 7 }, 0, 3);

                session.CloseAppendHandle();
                session.CloseAppendHandle();

                session.MovePendingToStaging();
                Assert.Throws<InvalidOperationException>(() => session.MovePendingToStaging());

                session.Dispose();
                session.Dispose();
            }
            finally
            {
                session.Dispose();
            }

            Assert.That(File.ReadAllBytes(StagingPath(layout)), Is.EqualTo(new byte[] { 5, 6, 7 }));
            Assert.That(File.Exists(PendingPath(layout)), Is.False);
        }

        [Test]
        public void Move_FailedFirstAttempt_CannotRetryAfterConflictCleared()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            using (NvencRunChunkFileSession session = NvencRunChunkFileSession.Create(MakeIssue(layout, null)))
            {
                session.Append(new byte[] { 1, 2, 3 }, 0, 3);
                session.CloseAppendHandle();

                // The first attempt fails on a staging-name conflict.
                File.WriteAllBytes(StagingPath(layout), new byte[] { 7, 7, 7 });
                Assert.Throws<IOException>(() => session.MovePendingToStaging());

                // Clearing the conflict must not make the same session
                // retryable: the attempt was latched before the native call.
                File.Delete(StagingPath(layout));
                Assert.Throws<InvalidOperationException>(() => session.MovePendingToStaging());
            }

            Assert.That(File.Exists(PendingPath(layout)), Is.True);
            Assert.That(File.Exists(StagingPath(layout)), Is.False);
        }

        [Test]
        public void Partial_ExclusiveNonSharedHandle_RejectsExternalAccess()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            byte[] data = new byte[] { 1, 2, 3, 4, 5 };
            using (NvencRunChunkFileSession session = NvencRunChunkFileSession.Create(MakeIssue(layout, null)))
            {
                session.Append(data, 0, data.Length);

                // The pending handle is non-shared: read, write, and delete
                // opens are all rejected while the session holds it.
                Assert.Throws<IOException>(() => File.OpenRead(PendingPath(layout)));
                Assert.Throws<IOException>(() => File.OpenWrite(PendingPath(layout)));
                Assert.Throws<IOException>(() => File.Delete(PendingPath(layout)));

                session.CloseAppendHandle();

                // The retained identity handle keeps exclusivity after close.
                Assert.Throws<IOException>(() => File.OpenRead(PendingPath(layout)));
                Assert.Throws<IOException>(() => File.Delete(PendingPath(layout)));

                session.MovePendingToStaging();

                // Exclusivity follows the retained identity handle to the
                // staged name until the session is disposed.
                Assert.Throws<IOException>(() => File.OpenRead(StagingPath(layout)));
                Assert.Throws<IOException>(() => File.Delete(StagingPath(layout)));
            }

            // After Dispose every handle is released and the staged file is
            // readable again.
            Assert.That(File.ReadAllBytes(StagingPath(layout)), Is.EqualTo(data));
        }

        // ---- Ownership and disposal ----

        [Test]
        public void Dispose_DoesNotDeletePendingPartial()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            NvencRunChunkFileSession session = NvencRunChunkFileSession.Create(MakeIssue(layout, null));
            session.Append(new byte[] { 1, 2, 3, 4 }, 0, 4);
            session.Dispose();

            Assert.That(File.Exists(PendingPath(layout)), Is.True);
        }

        [Test]
        public void Dispose_DoesNotReleaseOwnershipLease()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            List<string> disposeLog = new List<string>();
            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, disposeLog);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(MakeExecutionReceipt(layout));
            CaptureRunInitializationSessionIssue issue = CaptureRunInitializationSession.IssuanceProof.Mint(owner, identity, evidence);

            using (NvencRunChunkFileSession session = NvencRunChunkFileSession.Create(issue))
            {
                Assert.That(session, Is.Not.Null);
            }

            Assert.That(owner.IsCreated, Is.True);
            Assert.That(disposeLog, Is.Empty);
        }

        [Test]
        public void Dispose_ReleasesFileHandles_AllowingRecursiveDelete()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            NvencRunChunkFileSession session = NvencRunChunkFileSession.Create(MakeIssue(layout, null));
            session.Append(new byte[] { 1, 2, 3 }, 0, 3);
            session.Dispose();

            // The pending file is opened without delete sharing, so a leaked
            // handle would block the recursive delete below.
            Assert.DoesNotThrow(() => Directory.Delete(layout.StagingRunRoot, true));
            Assert.That(Directory.Exists(layout.StagingRunRoot), Is.False);
        }

        [Test]
        public void Create_OwnerReleased_Rejected()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            CaptureRunInitializationSessionOwnershipLease owner = MakeOwnershipLease(layout, null);
            CaptureRunLockIdentityEvidence identity = MakeIdentityEvidence(owner);
            CaptureRunInitializationReadyEvidence evidence = CaptureRunInitializationReadyEvidence.FromFresh(MakeExecutionReceipt(layout));
            CaptureRunInitializationSessionIssue issue = CaptureRunInitializationSession.IssuanceProof.Mint(owner, identity, evidence);

            owner.Dispose();

            Assert.Throws<ArgumentException>(() => NvencRunChunkFileSession.Create(issue));
            Assert.That(File.Exists(PendingPath(layout)), Is.False);
        }

        [Test]
        public void Create_ForeignSessionIssue_Rejected()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            CaptureRunInitializationSessionIssue foreign =
                (CaptureRunInitializationSessionIssue)FormatterServices.GetUninitializedObject(
                    typeof(CaptureRunInitializationSessionIssue));

            Assert.Throws<ArgumentException>(() => NvencRunChunkFileSession.Create(foreign));
            Assert.That(File.Exists(PendingPath(layout)), Is.False);
        }

        // ---- Path swap after acceptance ----

        [Test]
        public void Move_PendingHandlePinsNamespace_AndRenamesToExactStaging()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunRootLayout layout = MakeLayout(staging, final);
            Directory.CreateDirectory(layout.StagingRunRoot);

            using (NvencRunChunkFileSession session = NvencRunChunkFileSession.Create(MakeIssue(layout, null)))
            {
                session.Append(new byte[] { 4, 2 }, 0, 2);
                session.CloseAppendHandle();

                // The retained pending file handle pins the chunks directory,
                // so a parent-directory swap cannot remove or redirect it; the
                // rename below must therefore land at the exact fixed staging
                // path inside the retained Run root.
                string moved = ChunksDir(layout) + "-moved";
                Assert.Throws<IOException>(() => Directory.Move(ChunksDir(layout), moved));

                session.MovePendingToStaging();
            }

            Assert.That(File.ReadAllBytes(StagingPath(layout)), Is.EqualTo(new byte[] { 4, 2 }));
            Assert.That(File.Exists(PendingPath(layout)), Is.False);
        }

        [Test]
        public void Rename_IsHandleRelative_NotPathResolved()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencRunChunkFileSession.cs"));
            string renameBody = ExtractMethodBody(source, "void RenameNonOverwriting");

            // The rename must pin the destination to the retained directory
            // handle with a relative name, never a re-resolved string path.
            Assert.That(renameBody, Does.Contain("RootDirectory"));
            Assert.That(renameBody, Does.Contain("NtSetInformationFile"));
            Assert.That(renameBody, Does.Not.Contain("GetFinalPathNameByHandle"));
            Assert.That(renameBody, Does.Not.Contain("Path.Combine"));
        }

        // ---- Source contract ----

        [Test]
        public void Source_NoFullReadHashRetryTruncateCopyFlushTrue()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencRunChunkFileSession.cs"));

            string[] forbidden =
            {
                "ReadAllBytes", "ReadAllText", "ReadToEnd", "ComputeHash", "IncrementalHash",
                "Retry", "Rollback", "Truncate", "File.Copy", "File.Move", "File.Delete",
                "Path.Combine", "Flush(true)", "FlushFileBuffers", "new Thread", "ThreadPool",
                "Task", "lock (", "Monitor",
            };

            foreach (string word in forbidden)
            {
                Assert.That(source, Does.Not.Contain(word), "file session source must not contain: " + word);
            }
        }

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            private readonly List<string> _disposeLog;

            public FakeHandle(string lockPath, bool isCreated, List<string> disposeLog)
            {
                LockPath = lockPath;
                IsCreated = isCreated;
                _disposeLog = disposeLog;
            }

            public string LockPath { get; }

            public bool IsCreated { get; }

            public string Tag { get; set; }

            public void Dispose()
            {
                _disposeLog?.Add(LockPath);
            }
        }

        private sealed class FakeProvisioner : ICaptureRunRootProvisioner
        {
            public CaptureRunRootProvisionReceipt ProvisionNew(CaptureRunRootProvisionOperation operation)
            {
                return new CaptureRunRootProvisionReceipt(this, operation);
            }
        }

        private sealed class FakeWriter : ICaptureRunMarkerAtomicWriter
        {
            public CaptureRunMarkerWriteReceipt WriteAtomic(CaptureRunMarkerWriteOperation operation)
            {
                return new CaptureRunMarkerWriteReceipt(this, operation);
            }
        }
    }
}
