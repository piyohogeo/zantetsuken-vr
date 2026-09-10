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
    /// Contract tests for the production Phase 0.11 NVENC Capture Index
    /// recovery committer: admission before any filesystem contact, the fixed
    /// observation order, what each commit mode may and may not do to the
    /// temporary, the single non-overwriting rename, and release on every path.
    /// </summary>
    /// <remarks>
    /// The filesystem is a small recording fake, so every no-follow status and
    /// failure point is reachable without junctions or permission tricks. Four
    /// Windows-only cases run the real backend end to end; its NT buffer layout
    /// and P/Invoke constants are already covered by the filesystem's own
    /// fixture and are not re-audited here.
    /// </remarks>
    public class NvencRunCaptureIndexRecoveryCommitterContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string Hash64 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private const string OtherHash64 =
            "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210";

        private const string ArtifactId = "nvenc-chunk-0";

        private const long ChunkByteLength = 4096;

        private const string TemporaryName = "capture.index.tmp";

        private const string FinalName = "capture.index";

        private const NvencRunCaptureIndexObservationStatus Absent =
            NvencRunCaptureIndexObservationStatus.Absent;

        private const NvencRunCaptureIndexObservationStatus Matches =
            NvencRunCaptureIndexObservationStatus.MatchesAuthoritative;

        private const NvencRunCaptureIndexObservationStatus Invalid =
            NvencRunCaptureIndexObservationStatus.Invalid;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        private readonly List<string> _sandboxes = new List<string>();

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
                () => new NvencRunCaptureIndexRecoveryCommitter(null, new FakeFileSystem()));
            Assert.Throws<ArgumentNullException>(
                () => new NvencRunCaptureIndexRecoveryCommitter(MakeLayout(), null));
        }

        [Test]
        public void Commit_NullOperation_RejectedWithoutFilesystemContact()
        {
            Harness h = MakeHarness(Absent, Absent);

            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(
                () => h.Committer.Commit(null));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(h.FileSystem.Calls, Is.Empty);
        }

        [Test]
        public void Commit_OperationWhoseLockWasReleased_RejectedWithoutFilesystemContact()
        {
            Harness h = MakeHarness(Absent, Absent);

            ReleaseAllLocks();
            Assert.That(h.Operation.IsValid, Is.False);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => h.Committer.Commit(h.Operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(h.FileSystem.Calls, Is.Empty);
        }

        [Test]
        public void Commit_ForeignRootLayout_RejectedWithoutFilesystemContact()
        {
            Harness h = MakeHarness(Absent, Absent);
            FakeFileSystem fileSystem = new FakeFileSystem();

            // A content-identical but distinct layout is a foreign Run.
            NvencRunCaptureIndexRecoveryCommitter foreign =
                new NvencRunCaptureIndexRecoveryCommitter(MakeLayout(), fileSystem);

            ArgumentException ex = Assert.Throws<ArgumentException>(
                () => foreign.Commit(h.Operation));

            Assert.That(ex.ParamName, Is.EqualTo("operation"));
            Assert.That(fileSystem.Calls, Is.Empty);
        }

        [Test]
        public void Commit_WithoutNoFollowSupport_RefusesWithoutFilesystemContact()
        {
            Harness h = MakeHarness(Absent, Absent);
            h.FileSystem.Supported = false;

            Assert.Throws<CaptureArtifactNoFollowUnavailableException>(
                () => h.Committer.Commit(h.Operation));
            Assert.That(h.FileSystem.Calls, Is.Empty);
        }

        [Test]
        public void Commit_WithoutDirectoryFlushSupport_StillCommits()
        {
            // Phase 0.11 asks for no durability, so directory flush capability
            // is not an admission condition.
            Harness h = MakeHarness(Absent, Absent);
            h.FileSystem.DirectoryFlushSupported = false;

            Assert.That(h.Committer.Commit(h.Operation), Is.Not.Null);
            Assert.That(h.FileSystem.FlushDirectoryCount, Is.EqualTo(0));
        }

        // ---- Observation order and the existing final ----

        [Test]
        public void Commit_ObservesTheFinalBeforeAnyTemporaryWork()
        {
            Harness h = MakeHarness(Absent, Absent);

            h.Committer.Commit(h.Operation);

            Assert.That(h.FileSystem.Calls, Is.EqualTo(new List<string>
            {
                "OpenDirectory:" + h.Layout.FinalRunRoot,
                "TryOpen:" + FinalName,
                "TryOpen:" + TemporaryName,
                "CreateNew:" + TemporaryName,
                "Write:" + h.CanonicalBytes.Length,
                "Flush",
                "Rename:" + FinalName,
            }));
        }

        [Test]
        public void Commit_ExistingFinalIndex_LeavesTheTemporaryUntouched()
        {
            Harness h = MakeHarness(Absent, Absent);
            h.FileSystem.Serve(FinalName, h.CanonicalBytes);

            Assert.Throws<IOException>(() => h.Committer.Commit(h.Operation));

            Assert.That(h.FileSystem.Calls, Has.No.Member("TryOpen:" + TemporaryName));
            Assert.That(h.FileSystem.CreateNewCount, Is.EqualTo(0));
            Assert.That(h.FileSystem.DeleteCount, Is.EqualTo(0));
            Assert.That(h.FileSystem.RenameCount, Is.EqualTo(0));
            AssertEverythingReleased(h);
        }

        [Test]
        public void Commit_UnusableFinalObservation_IsNotFoldedIntoAbsent()
        {
            foreach (CaptureIndexFileOpenStatus status in new[]
            {
                CaptureIndexFileOpenStatus.InvalidFileKind,
                CaptureIndexFileOpenStatus.EscapesRoot,
                CaptureIndexFileOpenStatus.IoFailure,
            })
            {
                Harness h = MakeHarness(Absent, Absent);
                h.FileSystem.SetStatus(FinalName, status);

                Assert.Throws<IOException>(() => h.Committer.Commit(h.Operation), status.ToString());
                Assert.That(h.FileSystem.CreateNewCount, Is.EqualTo(0));
                Assert.That(h.FileSystem.DeleteCount, Is.EqualTo(0));
                Assert.That(h.FileSystem.RenameCount, Is.EqualTo(0));
                AssertEverythingReleased(h);
            }

            Harness unsupported = MakeHarness(Absent, Absent);
            unsupported.FileSystem.SetStatus(FinalName, CaptureIndexFileOpenStatus.Unsupported);

            Assert.Throws<CaptureArtifactNoFollowUnavailableException>(
                () => unsupported.Committer.Commit(unsupported.Operation));
            Assert.That(unsupported.FileSystem.RenameCount, Is.EqualTo(0));
        }

        // ---- CreateTemporaryAndCommit ----

        [Test]
        public void Create_CreatesWritesAndRenamesOnce()
        {
            Harness h = MakeHarness(Absent, Absent);

            NvencRunCaptureIndexRecoveryCommitReceipt receipt = h.Committer.Commit(h.Operation);

            Assert.That(h.Operation.CommitMode,
                Is.EqualTo(CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit));
            Assert.That(h.FileSystem.CreateNewCount, Is.EqualTo(1));
            Assert.That(h.FileSystem.RenameCount, Is.EqualTo(1));
            Assert.That(h.FileSystem.DeleteCount, Is.EqualTo(0));
            Assert.That(h.FileSystem.WriteCount, Is.EqualTo(1));
            Assert.That(h.FileSystem.CreatedFiles.Count, Is.EqualTo(1));
            Assert.That(h.FileSystem.CreatedFiles[0].WrittenBytes, Is.EqualTo(h.CanonicalBytes));

            // The rename is bound to the handle this attempt created.
            Assert.That(ReferenceEquals(
                    h.FileSystem.RenamedFile, h.FileSystem.CreatedFiles[0].File),
                Is.True);
            Assert.That(h.FileSystem.RenamedToName, Is.EqualTo(FinalName));

            Assert.That(receipt.IsIssuedFor(h.Committer, h.Operation), Is.True);
            AssertNoFlushes(h);
            AssertEverythingReleased(h);
        }

        [Test]
        public void Create_ExistingTemporary_DoesNotCreateOrRename()
        {
            Harness h = MakeHarness(Absent, Absent);
            h.FileSystem.Serve(TemporaryName, new byte[] { 1, 2, 3 });

            Assert.Throws<IOException>(() => h.Committer.Commit(h.Operation));

            Assert.That(h.FileSystem.CreateNewCount, Is.EqualTo(0));
            Assert.That(h.FileSystem.DeleteCount, Is.EqualTo(0));
            Assert.That(h.FileSystem.RenameCount, Is.EqualTo(0));
            AssertEverythingReleased(h);
        }

        // ---- ReuseCanonicalTemporaryAndCommit ----

        [Test]
        public void Reuse_RenamesTheExistingTemporaryWithoutWritingOrDeleting()
        {
            Harness h = MakeHarness(Absent, Matches);
            h.FileSystem.Serve(TemporaryName, h.CanonicalBytes);

            NvencRunCaptureIndexRecoveryCommitReceipt receipt = h.Committer.Commit(h.Operation);

            Assert.That(h.Operation.CommitMode, Is.EqualTo(
                CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit));
            Assert.That(h.FileSystem.CreateNewCount, Is.EqualTo(0));
            Assert.That(h.FileSystem.WriteCount, Is.EqualTo(0));
            Assert.That(h.FileSystem.DeleteCount, Is.EqualTo(0));
            Assert.That(h.FileSystem.RenameCount, Is.EqualTo(1));

            // The rename is bound to the temporary this attempt opened.
            Assert.That(ReferenceEquals(
                    h.FileSystem.RenamedFile, h.FileSystem.OpenedFiles[0].File),
                Is.True);

            Assert.That(receipt.IsValid, Is.True);
            AssertNoFlushes(h);
            AssertEverythingReleased(h);
        }

        [Test]
        public void Reuse_DifferentBytesOrOversizedTemporary_DoesNotRename()
        {
            byte[] shorter = new byte[] { (byte)'{', (byte)'}' };
            byte[] oversized =
                new byte[CapturePublicationPlanCodec.MaximumCanonicalByteCount + 1];

            foreach (byte[] content in new[] { shorter, oversized })
            {
                Harness h = MakeHarness(Absent, Matches);
                h.FileSystem.Serve(TemporaryName, content);

                Assert.Throws<InvalidDataException>(() => h.Committer.Commit(h.Operation));

                Assert.That(h.FileSystem.RenameCount, Is.EqualTo(0));
                Assert.That(h.FileSystem.DeleteCount, Is.EqualTo(0));
                Assert.That(h.FileSystem.CreateNewCount, Is.EqualTo(0));
                Assert.That(h.FileSystem.WriteCount, Is.EqualTo(0));
                AssertEverythingReleased(h);
            }
        }

        [Test]
        public void Reuse_ReadFailure_PropagatesAndDoesNotRename()
        {
            Harness h = MakeHarness(Absent, Matches);
            IOException failure = new IOException("read failed");
            h.FileSystem.Serve(TemporaryName, h.CanonicalBytes);
            h.FileSystem.FailReadsWith(TemporaryName, failure);

            IOException thrown = Assert.Throws<IOException>(() => h.Committer.Commit(h.Operation));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(h.FileSystem.RenameCount, Is.EqualTo(0));
            Assert.That(h.FileSystem.DeleteCount, Is.EqualTo(0));
            AssertEverythingReleased(h);
        }

        // ---- ReplaceInvalidTemporaryAndCommit ----

        [Test]
        public void Replace_DeletesOnlyTheInvalidTemporaryThenCreatesWritesAndRenames()
        {
            Harness h = MakeHarness(Absent, Invalid);
            h.FileSystem.Serve(TemporaryName, new byte[] { (byte)'{', (byte)'}' });

            NvencRunCaptureIndexRecoveryCommitReceipt receipt = h.Committer.Commit(h.Operation);

            Assert.That(h.Operation.CommitMode, Is.EqualTo(
                CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit));
            Assert.That(h.FileSystem.DeleteCount, Is.EqualTo(1));
            Assert.That(h.FileSystem.CreateNewCount, Is.EqualTo(1));
            Assert.That(h.FileSystem.WriteCount, Is.EqualTo(1));
            Assert.That(h.FileSystem.RenameCount, Is.EqualTo(1));

            // Only this attempt's own opened handle was deleted, and the rename
            // is bound to the newly created one.
            Assert.That(ReferenceEquals(
                    h.FileSystem.DeletedFile, h.FileSystem.OpenedFiles[0].File),
                Is.True);
            Assert.That(ReferenceEquals(
                    h.FileSystem.RenamedFile, h.FileSystem.CreatedFiles[0].File),
                Is.True);
            Assert.That(h.FileSystem.CreatedFiles[0].WrittenBytes, Is.EqualTo(h.CanonicalBytes));

            // The delete happens before the replacement is created.
            Assert.That(
                h.FileSystem.Calls.IndexOf("Delete"),
                Is.LessThan(h.FileSystem.Calls.IndexOf("CreateNew:" + TemporaryName)));

            Assert.That(receipt.IsIssuedFor(h.Committer, h.Operation), Is.True);
            AssertNoFlushes(h);
            AssertEverythingReleased(h);
        }

        [Test]
        public void Replace_CanonicalTemporary_IsNeverDeleted()
        {
            // Both this Run's own canonical bytes and another plan's canonical
            // bytes are documents, not garbage.
            Harness authoritative = MakeHarness(Absent, Invalid);
            authoritative.FileSystem.Serve(TemporaryName, authoritative.CanonicalBytes);

            Assert.Throws<InvalidDataException>(
                () => authoritative.Committer.Commit(authoritative.Operation));
            Assert.That(authoritative.FileSystem.DeleteCount, Is.EqualTo(0));
            Assert.That(authoritative.FileSystem.RenameCount, Is.EqualTo(0));
            AssertEverythingReleased(authoritative);

            Harness foreign = MakeHarness(Absent, Invalid);
            foreign.FileSystem.Serve(
                TemporaryName,
                CapturePublicationPlanCodec.SerializeCanonical(foreign.MakeForeignPlan()));

            Assert.Throws<InvalidDataException>(() => foreign.Committer.Commit(foreign.Operation));
            Assert.That(foreign.FileSystem.DeleteCount, Is.EqualTo(0));
            Assert.That(foreign.FileSystem.RenameCount, Is.EqualTo(0));
            AssertEverythingReleased(foreign);
        }

        [Test]
        public void Replace_OversizedTemporary_IsNeverDeleted()
        {
            Harness h = MakeHarness(Absent, Invalid);
            h.FileSystem.Serve(
                TemporaryName,
                new byte[CapturePublicationPlanCodec.MaximumCanonicalByteCount + 1]);

            Assert.Throws<InvalidDataException>(() => h.Committer.Commit(h.Operation));

            Assert.That(h.FileSystem.DeleteCount, Is.EqualTo(0));
            Assert.That(h.FileSystem.CreateNewCount, Is.EqualTo(0));
            Assert.That(h.FileSystem.RenameCount, Is.EqualTo(0));
            AssertEverythingReleased(h);
        }

        [Test]
        public void Replace_ReadFailure_PropagatesAndIsNotTreatedAsInvalidContent()
        {
            Harness h = MakeHarness(Absent, Invalid);
            IOException failure = new IOException("read failed");
            h.FileSystem.Serve(TemporaryName, new byte[] { (byte)'{', (byte)'}' });
            h.FileSystem.FailReadsWith(TemporaryName, failure);

            IOException thrown = Assert.Throws<IOException>(() => h.Committer.Commit(h.Operation));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(h.FileSystem.DeleteCount, Is.EqualTo(0));
            Assert.That(h.FileSystem.CreateNewCount, Is.EqualTo(0));
            Assert.That(h.FileSystem.RenameCount, Is.EqualTo(0));
            AssertEverythingReleased(h);
        }

        [Test]
        public void UnusableTemporaryObservation_IsNeitherAbsentNorInvalidContent()
        {
            foreach (NvencRunCaptureIndexObservationStatus temporary in new[] { Matches, Invalid })
            {
                foreach (CaptureIndexFileOpenStatus status in new[]
                {
                    CaptureIndexFileOpenStatus.Absent,
                    CaptureIndexFileOpenStatus.InvalidFileKind,
                    CaptureIndexFileOpenStatus.EscapesRoot,
                    CaptureIndexFileOpenStatus.IoFailure,
                })
                {
                    Harness h = MakeHarness(Absent, temporary);
                    h.FileSystem.SetStatus(TemporaryName, status);

                    Assert.Throws<IOException>(
                        () => h.Committer.Commit(h.Operation), temporary + "/" + status);
                    Assert.That(h.FileSystem.DeleteCount, Is.EqualTo(0));
                    Assert.That(h.FileSystem.CreateNewCount, Is.EqualTo(0));
                    Assert.That(h.FileSystem.RenameCount, Is.EqualTo(0));
                    AssertEverythingReleased(h);
                }
            }
        }

        // ---- The rename boundary ----

        [Test]
        public void Rename_Exception_PropagatesSameReferenceWithoutRetryOrCleanup()
        {
            Harness h = MakeHarness(Absent, Absent);
            IOException failure = new IOException("rename failed");
            h.FileSystem.RenameException = failure;

            IOException thrown = Assert.Throws<IOException>(() => h.Committer.Commit(h.Operation));

            Assert.That(ReferenceEquals(thrown, failure), Is.True);
            Assert.That(h.FileSystem.RenameCount, Is.EqualTo(1));
            Assert.That(h.FileSystem.DeleteCount, Is.EqualTo(0));
            Assert.That(h.FileSystem.CreateNewCount, Is.EqualTo(1));

            // Nothing is re-observed, re-renamed, or cleaned up afterwards.
            Assert.That(
                h.FileSystem.Calls.IndexOf("Rename:" + FinalName),
                Is.EqualTo(h.FileSystem.Calls.Count - 1));
            AssertEverythingReleased(h);
        }

        // ---- Purity and shape ----

        [Test]
        public void Commit_TouchesOnlyTheTwoFixedNamesAndChangesNothingElse()
        {
            Harness h = MakeHarness(Absent, Absent);
            NvencRunCaptureIndexRecoveryDecision decision =
                h.Operation.CaptureIndexRecoveryDecision;
            CapturePublicationPlan plan = h.Operation.AuthoritativePlan;
            CaptureArtifactDescriptor chunk = plan.GetArtifact(0);
            string chunkHash = chunk.ContentHash;

            h.Committer.Commit(h.Operation);

            foreach (string call in h.FileSystem.Calls)
            {
                bool namesSomethingElse = call.Contains(":")
                    && !call.EndsWith(":" + FinalName, StringComparison.Ordinal)
                    && !call.EndsWith(":" + TemporaryName, StringComparison.Ordinal)
                    && !call.StartsWith("OpenDirectory:", StringComparison.Ordinal)
                    && !call.StartsWith("Write:", StringComparison.Ordinal);
                Assert.That(namesSomethingElse, Is.False, call);
            }

            Assert.That(h.FileSystem.OpenedDirectories,
                Is.EqualTo(new List<string> { h.Layout.FinalRunRoot }));

            // The decision graph and the lock are untouched.
            Assert.That(decision.IsValid, Is.True);
            Assert.That(decision.Snapshot.FinalIndexStatus, Is.EqualTo(Absent));
            Assert.That(decision.Snapshot.TemporaryIndexStatus, Is.EqualTo(Absent));
            Assert.That(ReferenceEquals(plan.GetArtifact(0), chunk), Is.True);
            Assert.That(chunk.ContentHash, Is.EqualTo(chunkHash));
            Assert.That(h.Operation.PublicationRecoveryDecision.IsValid, Is.True);
            Assert.That(h.Owner.IsCreated, Is.True);
            Assert.That(h.Owner.IsReleaseComplete, Is.False);
            Assert.That(h.Operation.IsValid, Is.True);
        }

        [Test]
        public void Committer_HoldsOnlyTheRootLayoutAndTheFileSystem()
        {
            Type type = typeof(NvencRunCaptureIndexRecoveryCommitter);

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
                typeof(ICaptureIndexCommitFileSystem),
            }));
        }

        // ---- Windows backend integration ----

        [Test]
        public void Backend_CreateReuseAndReplace_CommitThroughRealHandles()
        {
            RequireWindows();

            // Create: no temporary, no final.
            SandboxHarness create = MakeSandboxHarness(Absent, Absent);
            create.Committer.Commit(create.Operation);
            Assert.That(File.ReadAllBytes(create.FinalPath), Is.EqualTo(create.CanonicalBytes));
            Assert.That(File.Exists(create.TemporaryPath), Is.False);

            // Reuse: the temporary already holds the authoritative bytes.
            SandboxHarness reuse = MakeSandboxHarness(Absent, Matches);
            File.WriteAllBytes(reuse.TemporaryPath, reuse.CanonicalBytes);
            reuse.Committer.Commit(reuse.Operation);
            Assert.That(File.ReadAllBytes(reuse.FinalPath), Is.EqualTo(reuse.CanonicalBytes));
            Assert.That(File.Exists(reuse.TemporaryPath), Is.False);

            // Replace: the temporary holds an unusable document.
            SandboxHarness replace = MakeSandboxHarness(Absent, Invalid);
            File.WriteAllBytes(replace.TemporaryPath, new byte[] { (byte)'{', (byte)'}' });
            replace.Committer.Commit(replace.Operation);
            Assert.That(File.ReadAllBytes(replace.FinalPath), Is.EqualTo(replace.CanonicalBytes));
            Assert.That(File.Exists(replace.TemporaryPath), Is.False);
        }

        [Test]
        public void Backend_ExistingFinalIndex_IsNeverOverwritten()
        {
            RequireWindows();

            SandboxHarness h = MakeSandboxHarness(Absent, Absent);
            byte[] existing = new byte[] { 7, 7, 7 };
            File.WriteAllBytes(h.FinalPath, existing);

            Assert.Throws<IOException>(() => h.Committer.Commit(h.Operation));

            Assert.That(File.ReadAllBytes(h.FinalPath), Is.EqualTo(existing));
            Assert.That(File.Exists(h.TemporaryPath), Is.False);
        }

        // ---- Fixture helpers ----

        private static void RequireWindows()
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Assert.Ignore("The Capture Index commit filesystem requires Windows handles.");
            }
        }

        private static void AssertNoFlushes(Harness h)
        {
            Assert.That(h.FileSystem.FlushFileDataCount, Is.EqualTo(0));
            Assert.That(h.FileSystem.FlushDirectoryCount, Is.EqualTo(0));
        }

        private static void AssertEverythingReleased(Harness h)
        {
            foreach (FakeFile file in h.FileSystem.AllFiles)
            {
                Assert.That(file.Stream.Disposed, Is.True, "every stream must be released.");
            }

            foreach (SafeFileHandle handle in h.FileSystem.DirectoryHandles)
            {
                Assert.That(handle.IsClosed, Is.True, "every directory handle must be released.");
            }
        }

        private void ReleaseAllLocks()
        {
            foreach (CaptureRunInitializationSessionOwnershipLease owner in _owners)
            {
                owner.Dispose();
            }
        }

        private Harness MakeHarness(
            NvencRunCaptureIndexObservationStatus finalIndex,
            NvencRunCaptureIndexObservationStatus temporaryIndex)
        {
            CaptureRunRootLayout layout = MakeLayout();
            NvencRunCaptureIndexRecoveryCommitOperation operation =
                MakeOperation(layout, finalIndex, temporaryIndex);
            FakeFileSystem fileSystem = new FakeFileSystem();

            return new Harness(
                layout,
                operation,
                fileSystem,
                new NvencRunCaptureIndexRecoveryCommitter(layout, fileSystem),
                _owners[_owners.Count - 1]);
        }

        private SandboxHarness MakeSandboxHarness(
            NvencRunCaptureIndexObservationStatus finalIndex,
            NvencRunCaptureIndexObservationStatus temporaryIndex)
        {
            string sandbox = Path.Combine(
                Path.GetTempPath(), "zantetsuken-index-commit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(sandbox);
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(
                Path.Combine(sandbox, "staging"), Path.Combine(sandbox, "final"), 1);
            Directory.CreateDirectory(layout.FinalRunRoot);

            NvencRunCaptureIndexRecoveryCommitOperation operation =
                MakeOperation(layout, finalIndex, temporaryIndex);

            return new SandboxHarness(
                layout,
                operation,
                new NvencRunCaptureIndexRecoveryCommitter(
                    layout, CaptureIndexCommitFileSystem.Create()));
        }

        private NvencRunCaptureIndexRecoveryCommitOperation MakeOperation(
            CaptureRunRootLayout layout,
            NvencRunCaptureIndexObservationStatus finalIndex,
            NvencRunCaptureIndexObservationStatus temporaryIndex)
        {
            return new NvencRunCaptureIndexRecoveryCommitOperation(
                NvencRunCaptureIndexRecoveryClassifier.Classify(
                    new NvencRunCaptureIndexRecoveryInspectionSnapshot(
                        new NvencRunCaptureIndexRecoveryInspectionOperation(
                            MakeRecoveryDecision(layout)),
                        finalIndex,
                        temporaryIndex)));
        }

        private NvencRunPublicationRecoveryDecision MakeRecoveryDecision(CaptureRunRootLayout layout)
        {
            NvencRunPublicationRecoveryInspectionOperation operation =
                new NvencRunPublicationRecoveryInspectionOperation(
                    MakeRecoveryOutcome(layout), layout);
            CapturePublicationPlan plan = MakePlan(
                operation.TestRunId, operation.RunInitializationId, Hash64);

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
            long testRunId, string runInitializationId, string writerHash)
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
                testRunId, runInitializationId, writerHash, artifacts, entries);
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

        private static CaptureRunRootLayout MakeLayout()
        {
            return new CaptureRunRootLayout(
                Path.DirectorySeparatorChar == '\\' ? "C:\\staging" : "/staging",
                Path.DirectorySeparatorChar == '\\' ? "D:\\final" : "/final",
                1);
        }

        /// <summary>One Run with a recording filesystem and the committer under test.</summary>
        private sealed class Harness
        {
            internal Harness(
                CaptureRunRootLayout layout,
                NvencRunCaptureIndexRecoveryCommitOperation operation,
                FakeFileSystem fileSystem,
                NvencRunCaptureIndexRecoveryCommitter committer,
                CaptureRunInitializationSessionOwnershipLease owner)
            {
                Layout = layout;
                Operation = operation;
                FileSystem = fileSystem;
                Committer = committer;
                Owner = owner;
                CanonicalBytes = CapturePublicationPlanCodec.SerializeCanonical(
                    operation.AuthoritativePlan);
            }

            internal CaptureRunRootLayout Layout { get; }

            internal NvencRunCaptureIndexRecoveryCommitOperation Operation { get; }

            internal FakeFileSystem FileSystem { get; }

            internal NvencRunCaptureIndexRecoveryCommitter Committer { get; }

            internal CaptureRunInitializationSessionOwnershipLease Owner { get; }

            internal byte[] CanonicalBytes { get; }

            internal CapturePublicationPlan MakeForeignPlan()
            {
                return MakePlan(
                    Operation.TestRunId, Operation.RunInitializationId, OtherHash64);
            }
        }

        /// <summary>One Run over a real temporary directory and the real backend.</summary>
        private sealed class SandboxHarness
        {
            internal SandboxHarness(
                CaptureRunRootLayout layout,
                NvencRunCaptureIndexRecoveryCommitOperation operation,
                NvencRunCaptureIndexRecoveryCommitter committer)
            {
                Layout = layout;
                Operation = operation;
                Committer = committer;
                CanonicalBytes = CapturePublicationPlanCodec.SerializeCanonical(
                    operation.AuthoritativePlan);
            }

            internal CaptureRunRootLayout Layout { get; }

            internal NvencRunCaptureIndexRecoveryCommitOperation Operation { get; }

            internal NvencRunCaptureIndexRecoveryCommitter Committer { get; }

            internal byte[] CanonicalBytes { get; }

            internal string TemporaryPath => Path.Combine(Layout.FinalRunRoot, TemporaryName);

            internal string FinalPath => Path.Combine(Layout.FinalRunRoot, FinalName);
        }

        /// <summary>
        /// Records the exact call order, basenames, handle identities, and
        /// read, write, delete, rename, and flush counts. Absent is the default,
        /// so a name the test did not serve is simply not there.
        /// </summary>
        private sealed class FakeFileSystem : ICaptureIndexCommitFileSystem
        {
            private readonly Dictionary<string, byte[]> _contents = new Dictionary<string, byte[]>();
            private readonly Dictionary<string, CaptureIndexFileOpenStatus> _statuses =
                new Dictionary<string, CaptureIndexFileOpenStatus>();
            private readonly Dictionary<string, Exception> _readFailures =
                new Dictionary<string, Exception>();

            internal bool Supported { get; set; } = true;

            internal bool DirectoryFlushSupported { get; set; } = true;

            internal Exception RenameException { get; set; }

            internal List<string> Calls { get; } = new List<string>();

            internal List<string> OpenedDirectories { get; } = new List<string>();

            internal List<SafeFileHandle> DirectoryHandles { get; } = new List<SafeFileHandle>();

            internal List<FakeFile> AllFiles { get; } = new List<FakeFile>();

            internal List<FakeFile> OpenedFiles { get; } = new List<FakeFile>();

            internal List<FakeFile> CreatedFiles { get; } = new List<FakeFile>();

            internal int CreateNewCount { get; private set; }

            internal int WriteCount { get; private set; }

            internal int DeleteCount { get; private set; }

            internal int RenameCount { get; private set; }

            internal int FlushFileDataCount { get; private set; }

            internal int FlushDirectoryCount { get; private set; }

            internal CaptureIndexCommitFile DeletedFile { get; private set; }

            internal CaptureIndexCommitFile RenamedFile { get; private set; }

            internal string RenamedToName { get; private set; }

            public bool IsSupported => Supported;

            public bool IsDirectoryFlushSupported => DirectoryFlushSupported;

            internal void Serve(string name, byte[] content)
            {
                _contents[name] = content;
            }

            internal void SetStatus(string name, CaptureIndexFileOpenStatus status)
            {
                _statuses[name] = status;
            }

            internal void FailReadsWith(string name, Exception failure)
            {
                _readFailures[name] = failure;
            }

            public CaptureIndexCommitDirectory OpenDirectory(string absolutePath)
            {
                Calls.Add("OpenDirectory:" + absolutePath);
                OpenedDirectories.Add(absolutePath);

                SafeFileHandle handle = new SafeFileHandle(IntPtr.Zero, ownsHandle: false);
                DirectoryHandles.Add(handle);
                return new CaptureIndexCommitDirectory(handle, absolutePath, absolutePath);
            }

            public CaptureIndexFileOpen TryOpen(CaptureIndexCommitDirectory directory, string name)
            {
                Calls.Add("TryOpen:" + name);

                if (_statuses.TryGetValue(name, out CaptureIndexFileOpenStatus status))
                {
                    return CaptureIndexFileOpen.Of(status);
                }

                if (!_contents.TryGetValue(name, out byte[] content))
                {
                    return CaptureIndexFileOpen.Of(CaptureIndexFileOpenStatus.Absent);
                }

                RecordingStream stream = new RecordingStream(this, content);
                if (_readFailures.TryGetValue(name, out Exception failure))
                {
                    stream.ReadFailure = failure;
                }

                FakeFile file = new FakeFile(stream);
                AllFiles.Add(file);
                OpenedFiles.Add(file);
                return CaptureIndexFileOpen.Opened(file.File);
            }

            public CaptureIndexCommitFile CreateNew(
                CaptureIndexCommitDirectory directory, string name)
            {
                Calls.Add("CreateNew:" + name);
                CreateNewCount++;

                FakeFile file = new FakeFile(new RecordingStream(this, new byte[0]));
                AllFiles.Add(file);
                CreatedFiles.Add(file);
                return file.File;
            }

            public void FlushFileData(CaptureIndexCommitFile file)
            {
                Calls.Add("FlushFileData");
                FlushFileDataCount++;
            }

            public void Rename(
                CaptureIndexCommitFile file,
                CaptureIndexCommitDirectory directory,
                string newName)
            {
                Calls.Add("Rename:" + newName);
                RenameCount++;
                RenamedFile = file;
                RenamedToName = newName;

                if (RenameException != null)
                {
                    throw RenameException;
                }
            }

            public void Delete(CaptureIndexCommitFile file)
            {
                Calls.Add("Delete");
                DeleteCount++;
                DeletedFile = file;
            }

            public void FlushDirectory(CaptureIndexCommitDirectory directory)
            {
                Calls.Add("FlushDirectory");
                FlushDirectoryCount++;
            }

            internal void RecordWrite(int count)
            {
                Calls.Add("Write:" + count);
                WriteCount++;
            }

            internal void RecordFlush()
            {
                Calls.Add("Flush");
            }
        }

        /// <summary>A recording file: the production handle-bound file plus its stream.</summary>
        private sealed class FakeFile
        {
            internal FakeFile(RecordingStream stream)
            {
                Stream = stream;
                File = new CaptureIndexCommitFile(null, stream);
            }

            internal RecordingStream Stream { get; }

            internal CaptureIndexCommitFile File { get; }

            internal byte[] WrittenBytes => Stream.WrittenBytes;
        }

        /// <summary>
        /// An in-memory stream that records reads, writes, managed flushes, and
        /// its own release, and can fail a read.
        /// </summary>
        private sealed class RecordingStream : MemoryStream
        {
            private readonly FakeFileSystem _fileSystem;
            private readonly List<byte> _written = new List<byte>();

            internal RecordingStream(FakeFileSystem fileSystem, byte[] content)
            {
                // Seeded through the non-virtual base write so the served bytes
                // are readable from position zero without being recorded as
                // this fixture's own writes, and the stream stays expandable.
                if (content.Length > 0)
                {
                    base.Write(content, 0, content.Length);
                }

                base.Position = 0;
                _fileSystem = fileSystem;
            }

            internal Exception ReadFailure { get; set; }

            internal bool Disposed { get; private set; }

            internal byte[] WrittenBytes => _written.ToArray();

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (ReadFailure != null)
                {
                    throw ReadFailure;
                }

                return base.Read(buffer, offset, count);
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                _fileSystem.RecordWrite(count);
                for (int i = 0; i < count; i++)
                {
                    _written.Add(buffer[offset + i]);
                }

                base.Write(buffer, offset, count);
            }

            public override void Flush()
            {
                _fileSystem.RecordFlush();
                base.Flush();
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
