using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class PngJsonCaptureRunCaptureIndexCommitterContractTests
    {
        private const string InitId = "0123456789abcdef0123456789abcdef";

        private const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        private static CaptureRunRootRole Staging => CaptureRunRootRole.Staging;

        private static CaptureRunRootRole Final => CaptureRunRootRole.Final;

        private static CaptureRunMarkerObservationStatus Absent => CaptureRunMarkerObservationStatus.Absent;

        private static CaptureRunMarkerObservationStatus Canonical => CaptureRunMarkerObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentKind PublicationPlan => CaptureRunPublicationDocumentKind.PublicationPlan;

        private static CaptureRunPublicationDocumentKind CaptureIndex => CaptureRunPublicationDocumentKind.CaptureIndex;

        private static CaptureRunPublicationDocumentKind CaptureIndexTemporary => CaptureRunPublicationDocumentKind.CaptureIndexTemporary;

        private static CaptureRunPublicationDocumentObservationStatus DocAbsent => CaptureRunPublicationDocumentObservationStatus.Absent;

        private static CaptureRunPublicationDocumentObservationStatus DocCanonical => CaptureRunPublicationDocumentObservationStatus.Canonical;

        private static CaptureRunPublicationDocumentObservationStatus DocInvalid => CaptureRunPublicationDocumentObservationStatus.Invalid;

        private static CaptureRunPublicationEvidenceStatus EvAbsent => CaptureRunPublicationEvidenceStatus.Absent;

        private static CaptureRunPublicationEvidenceStatus EvMatchesExpected => CaptureRunPublicationEvidenceStatus.MatchesExpected;

        private readonly List<CaptureRunInitializationSessionOwnershipLease> _owners =
            new List<CaptureRunInitializationSessionOwnershipLease>();

        private readonly List<string> _sandboxes = new List<string>();

        [TearDown]
        public void TearDown()
        {
            for (int i = _owners.Count - 1; i >= 0; i--)
            {
                _owners[i].Dispose();
            }

            _owners.Clear();

            for (int i = _sandboxes.Count - 1; i >= 0; i--)
            {
                string sandbox = _sandboxes[i];
                if (Directory.Exists(sandbox))
                {
                    Directory.Delete(sandbox, true);
                }
            }

            _sandboxes.Clear();
        }

        // ---- Type shape / source ----

        [Test]
        public void Committer_Type_InternalSealed_ImplementsInterface_NotDisposable()
        {
            Type type = typeof(PngJsonCaptureRunCaptureIndexCommitter);

            Assert.That(type.IsPublic, Is.False);
            Assert.That(type.IsSealed, Is.True);
            Assert.That(type.IsClass, Is.True);
            Assert.That(typeof(IPngJsonCaptureRunCaptureIndexCommitter).IsAssignableFrom(type), Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void Committer_Constructor_NullLayout_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new PngJsonCaptureRunCaptureIndexCommitter(null));
        }

        [Test]
        public void Committer_Constructor_InvalidLayout_Rejected()
        {
            CaptureRunRootLayout invalid = (CaptureRunRootLayout)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunRootLayout));

            Assert.Throws<ArgumentException>(() => new PngJsonCaptureRunCaptureIndexCommitter(invalid));
        }

        [Test]
        public void Committer_Body_NoCanonicalBytesField_NoSerialization()
        {
            Type type = typeof(PngJsonCaptureRunCaptureIndexCommitter);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (FieldInfo field in fields)
            {
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(byte[])));
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(PngJsonCaptureRunCaptureIndexCommitOperation)));
                Assert.That(field.FieldType, Is.Not.EqualTo(typeof(PngJsonCaptureRunCaptureIndexCommitReceipt)));
            }

            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCaptureRunCaptureIndexCommitter.cs");
            Assert.That(source, Does.Not.Contain("SerializeCanonical"));
            Assert.That(source, Does.Not.Contain("new byte[operation"));
        }

        [Test]
        public void Committer_Source_ModeSeparation()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCaptureRunCaptureIndexCommitter.cs");

            int reuse = source.IndexOf("private CaptureIndexCommitFile ReuseCanonicalTemporary", StringComparison.Ordinal);
            int replace = source.IndexOf("private CaptureIndexCommitFile ReplaceInvalidTemporary", StringComparison.Ordinal);
            int commit = source.IndexOf("private void CommitTemporaryToFinal", StringComparison.Ordinal);
            Assert.That(reuse, Is.GreaterThan(0));
            Assert.That(replace, Is.GreaterThan(reuse));
            Assert.That(commit, Is.GreaterThan(replace));

            string reuseBody = source.Substring(reuse, replace - reuse);
            Assert.That(reuseBody, Does.Not.Contain("CreateNew"));
            Assert.That(reuseBody, Does.Not.Contain("Delete"));

            string replaceBody = source.Substring(replace, commit - replace);
            Assert.That(replaceBody, Does.Contain("Delete"));

            string commitBody = source.Substring(commit);
            Assert.That(commitBody, Does.Contain("RequireAbsent(directory, CaptureIndexName)"));
        }

        [Test]
        public void Committer_Source_NoPathBasedWriteDeleteRename()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/PngJsonCaptureRunCaptureIndexCommitter.cs");

            Assert.That(source, Does.Not.Contain("File.Move"));
            Assert.That(source, Does.Not.Contain("File.Delete"));
            Assert.That(source, Does.Not.Contain("new FileStream"));
            Assert.That(source, Does.Not.Contain("FileMode"));
            Assert.That(source, Does.Not.Contain("File.WriteAllBytes"));
            Assert.That(source, Does.Contain("_fileSystem.Rename"));
            Assert.That(source, Does.Contain("_fileSystem.Delete"));
            Assert.That(source, Does.Contain("_fileSystem.CreateNew"));
        }

        [Test]
        public void FileSystem_CreateNew_IsDirectoryHandleRelative()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/CaptureIndexCommitFileSystem.cs");

            int create = source.IndexOf("public CaptureIndexCommitFile CreateNew", StringComparison.Ordinal);
            int flushFile = source.IndexOf("public void FlushFileData", StringComparison.Ordinal);
            Assert.That(create, Is.GreaterThan(0));
            Assert.That(flushFile, Is.GreaterThan(create));

            string createBody = source.Substring(create, flushFile - create);
            // The create must be bound to the verified directory handle, never a
            // path reconstructed from the directory's original path, so a parent
            // directory swapped after RequireAbsent cannot land a file outside
            // the run root.
            Assert.That(createBody, Does.Not.Contain("Path.Combine"));
            Assert.That(createBody, Does.Not.Contain("OriginalPath"));
            Assert.That(createBody, Does.Contain("CreateNewRelativeToDirectory"));
            Assert.That(source, Does.Contain("RootDirectory = directory.Handle.DangerousGetHandle()"));
        }

        [Test]
        public void FileSystem_OpenDirectory_ProbesDirectoryFlushBeforeReturn()
        {
            string source = ReadSource("Assets/Zantetsu/Runtime/Observability/CaptureIndexCommitFileSystem.cs");

            int open = source.IndexOf("public CaptureIndexCommitDirectory OpenDirectory", StringComparison.Ordinal);
            int tryOpen = source.IndexOf("public CaptureIndexFileOpen TryOpen", StringComparison.Ordinal);
            Assert.That(open, Is.GreaterThan(0));
            Assert.That(tryOpen, Is.GreaterThan(open));

            string openBody = source.Substring(open, tryOpen - open);
            int flush = openBody.IndexOf("FlushFileBuffers", StringComparison.Ordinal);
            int returnDirectory = openBody.IndexOf("return new CaptureIndexCommitDirectory", StringComparison.Ordinal);
            // The flush probe must run on the actual directory handle before the
            // directory is returned, so an unsupported filesystem is rejected
            // before temporary creation or rename.
            Assert.That(flush, Is.GreaterThan(0));
            Assert.That(returnDirectory, Is.GreaterThan(flush));
        }

        [Test]
        public void FileSystem_TryOpen_VerificationHandle_DeniesOtherWriteDelete()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);

            string tmpPath = Path.Combine(layout.FinalRunRoot, "capture.index.tmp");
            File.WriteAllBytes(tmpPath, new byte[] { 1, 2, 3, 4 });

            CaptureIndexCommitFileSystem fileSystem = CaptureIndexCommitFileSystem.Create();
            using (CaptureIndexCommitDirectory directory = fileSystem.OpenDirectory(layout.FinalRunRoot))
            {
                CaptureIndexFileOpen opened = fileSystem.TryOpen(directory, "capture.index.tmp");
                Assert.That(opened.Status, Is.EqualTo(CaptureIndexFileOpenStatus.Opened));
                CaptureIndexCommitFile file = opened.File;
                try
                {
                    // While the verification handle is held, other handles must
                    // not be able to open the file for write or delete.
                    Assert.Throws<IOException>(() => File.OpenWrite(tmpPath));
                    Assert.Throws<IOException>(() => File.Delete(tmpPath));

                    // The same handle can still flush, rename, and delete.
                    fileSystem.FlushFileData(file);
                    fileSystem.Rename(file, directory, "capture.index");
                    fileSystem.Delete(file);
                }
                finally
                {
                    file.Dispose();
                }
            }

            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.False);
        }

        // ---- Null / token / layout rejection ----

        [Test]
        public void Commit_NullOperation_Rejected()
        {
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(MakeLayout());

            Assert.Throws<ArgumentNullException>(() => committer.Commit(null, null));
        }

        [Test]
        public void Commit_NullToken_Rejected()
        {
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(MakeLayout());
            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit, MakeLayout(), out _, out _);

            Assert.Throws<ArgumentNullException>(() => committer.Commit(operation, null));
        }

        [Test]
        public void Commit_InvalidOperation_RejectedBeforeIO()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(layout);

            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit, layout, out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            PngJsonCaptureRunCaptureIndexCommitOperation forged = ForgeOperation(operation, new byte[] { 1, 2, 3 });

            Assert.Throws<ArgumentException>(() => committer.Commit(forged, token));
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.False);
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index.tmp")), Is.False);
        }

        [Test]
        public void Commit_ForeignToken_RejectedBeforeIO()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(layout);

            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit, layout, out _, out _);
            PngJsonCaptureRunCaptureIndexCommitOperation other = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit, layout, out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken foreignToken);

            Assert.Throws<ArgumentException>(() => committer.Commit(operation, foreignToken));
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.False);
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index.tmp")), Is.False);
        }

        [Test]
        public void Commit_ForeignRootLayout_RejectedBeforeIO()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(layout);

            // The operation is bound to the forged C:\staging / D:\final layout.
            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit, MakeLayout(), out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            Assert.That(ReferenceEquals(operation.RootLayout, layout), Is.False);
            Assert.Throws<ArgumentException>(() => committer.Commit(operation, token));
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.False);
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index.tmp")), Is.False);
        }

        [Test]
        public void Commit_OwnerReleased_RejectedBeforeIO()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(layout);

            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit,
                layout,
                out CaptureRunInitializationSessionOwnershipLease owner,
                out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            Assert.That(operation.IsValidWithToken(token), Is.True);

            owner.Dispose();
            _owners.Remove(owner);

            Assert.Throws<ArgumentException>(() => committer.Commit(operation, token));
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.False);
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index.tmp")), Is.False);
        }

        // ---- Mode normal paths ----

        [Test]
        public void Commit_CreateTemporary_WritesAndCommits()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(layout);

            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit,
                layout,
                out _,
                out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);
            byte[] canonical = operation.GetCanonicalBytes();

            PngJsonCaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(operation, token);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.IsIssuedFor(committer, operation, token), Is.True);

            string finalPath = Path.Combine(layout.FinalRunRoot, "capture.index");
            string tmpPath = Path.Combine(layout.FinalRunRoot, "capture.index.tmp");
            Assert.That(File.Exists(finalPath), Is.True);
            Assert.That(File.ReadAllBytes(finalPath), Is.EqualTo(canonical));
            Assert.That(File.Exists(tmpPath), Is.False);
        }

        [Test]
        public void Commit_RepeatedRenames_LandExactlyOnCaptureIndex()
        {
            // The commit has produced a corrupted destination name, observed as
            // a sibling like "capture.index<garbage>" rather than a delayed or
            // missing final. A bounded repeat over independent sandboxes on the
            // real filesystem guards the rename destination, and every check
            // below is direct, with no polling, sleeping, or eventual assertion.
            const int iterations = 32;

            for (int iteration = 0; iteration < iterations; iteration++)
            {
                string message = "iteration " + iteration;

                (string sandbox, string staging, string final) = MakeSandbox();
                _sandboxes.Add(sandbox);

                CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
                Directory.CreateDirectory(layout.FinalRunRoot);
                PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(layout);

                PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                    CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit,
                    layout,
                    out _,
                    out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);
                byte[] canonical = operation.GetCanonicalBytes();

                PngJsonCaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(operation, token);

                Assert.That(receipt, Is.Not.Null, message);
                Assert.That(receipt.IsIssuedFor(committer, operation, token), Is.True, message);

                string finalPath = Path.Combine(layout.FinalRunRoot, "capture.index");
                string tmpPath = Path.Combine(layout.FinalRunRoot, "capture.index.tmp");

                Assert.That(File.Exists(tmpPath), Is.False, message);
                Assert.That(File.Exists(finalPath), Is.True, message);

                // Enumerating the Run root catches a corrupted destination name
                // that a plain existence check on the expected path would only
                // report as an absent final.
                string[] matches = Directory.GetFileSystemEntries(layout.FinalRunRoot, "capture.index*");
                Assert.That(matches, Has.Length.EqualTo(1),
                    message + ": " + string.Join(", ", matches));
                Assert.That(Path.GetFileName(matches[0]), Is.EqualTo("capture.index"), message);

                Assert.That(File.ReadAllBytes(finalPath), Is.EqualTo(canonical), message);
            }
        }

        [Test]
        public void Commit_ReuseCanonical_RenamesTmpWithoutRewrite()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(layout);

            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit,
                layout,
                out _,
                out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);
            byte[] canonical = operation.GetCanonicalBytes();

            string tmpPath = Path.Combine(layout.FinalRunRoot, "capture.index.tmp");
            File.WriteAllBytes(tmpPath, canonical);

            PngJsonCaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(operation, token);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.IsIssuedFor(committer, operation, token), Is.True);
            Assert.That(File.ReadAllBytes(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.EqualTo(canonical));
            Assert.That(File.Exists(tmpPath), Is.False);
        }

        [Test]
        public void Commit_ReplaceInvalid_DeletesOnlyInvalidTmpAndCommits()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(layout);

            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit,
                layout,
                out _,
                out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);
            byte[] canonical = operation.GetCanonicalBytes();

            string tmpPath = Path.Combine(layout.FinalRunRoot, "capture.index.tmp");
            File.WriteAllBytes(tmpPath, new byte[] { 123, 125, 123 }); // invalid JSON

            PngJsonCaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(operation, token);

            Assert.That(receipt, Is.Not.Null);
            Assert.That(receipt.IsIssuedFor(committer, operation, token), Is.True);
            Assert.That(File.ReadAllBytes(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.EqualTo(canonical));
            Assert.That(File.Exists(tmpPath), Is.False);
        }

        // ---- Mode-specific hard failures ----

        [Test]
        public void Commit_Create_ExistingTmp_NotModified()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(layout);

            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit, layout, out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            string tmpPath = Path.Combine(layout.FinalRunRoot, "capture.index.tmp");
            byte[] existing = new byte[] { 9, 9, 9 };
            File.WriteAllBytes(tmpPath, existing);

            Assert.Throws<IOException>(() => committer.Commit(operation, token));
            Assert.That(File.ReadAllBytes(tmpPath), Is.EqualTo(existing));
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.False);
        }

        [Test]
        public void Commit_Reuse_MismatchedContent_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(layout);

            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit, layout, out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            string tmpPath = Path.Combine(layout.FinalRunRoot, "capture.index.tmp");
            byte[] wrong = new byte[] { 1, 2, 3, 4 };
            File.WriteAllBytes(tmpPath, wrong);

            Assert.Throws<InvalidDataException>(() => committer.Commit(operation, token));
            Assert.That(File.ReadAllBytes(tmpPath), Is.EqualTo(wrong));
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.False);
        }

        [Test]
        public void Commit_Replace_CanonicalMatchingTmp_HardFailure_NotDeleted()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(layout);

            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit, layout, out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);
            byte[] canonical = operation.GetCanonicalBytes();

            string tmpPath = Path.Combine(layout.FinalRunRoot, "capture.index.tmp");
            File.WriteAllBytes(tmpPath, canonical);

            Assert.Throws<InvalidDataException>(() => committer.Commit(operation, token));
            Assert.That(File.ReadAllBytes(tmpPath), Is.EqualTo(canonical));
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.False);
        }

        [Test]
        public void Commit_Replace_CanonicalForeignTmp_NotDeleted()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(layout);

            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit, layout, out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            // A canonical tmp for a different plan (frame 20 instead of 10).
            PngJsonCapturePublicationPlan otherPlan = MakePlan(1, new[] { MakeEntry(20) });
            byte[] otherCanonical = PngJsonCapturePublicationPlanCodec.SerializeCanonical(otherPlan);

            string tmpPath = Path.Combine(layout.FinalRunRoot, "capture.index.tmp");
            File.WriteAllBytes(tmpPath, otherCanonical);

            Assert.Throws<InvalidDataException>(() => committer.Commit(operation, token));
            Assert.That(File.ReadAllBytes(tmpPath), Is.EqualTo(otherCanonical));
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.False);
        }

        [Test]
        public void Commit_Replace_LimitExceeded_NotDeleted()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(layout);

            // Build with a small inspection byte limit (maximumPlanBytes = 100),
            // so a 101-byte temporary exceeds it.
            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperationWithLimits(
                CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit,
                layout,
                100,
                4,
                64,
                out _,
                out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            string tmpPath = Path.Combine(layout.FinalRunRoot, "capture.index.tmp");
            byte[] oversized = new byte[101];
            for (int i = 0; i < oversized.Length; i++) oversized[i] = (byte)'x';
            File.WriteAllBytes(tmpPath, oversized);

            Assert.Throws<InvalidDataException>(() => committer.Commit(operation, token));
            Assert.That(File.ReadAllBytes(tmpPath), Is.EqualTo(oversized));
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.False);
        }

        [Test]
        public void Commit_Replace_DirectoryTmp_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(layout);

            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit, layout, out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            string tmpPath = Path.Combine(layout.FinalRunRoot, "capture.index.tmp");
            Directory.CreateDirectory(tmpPath);

            Assert.Throws<IOException>(() => committer.Commit(operation, token));
            Assert.That(Directory.Exists(tmpPath), Is.True);
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.False);
        }

        // ---- Common commit ----

        [Test]
        public void Commit_FinalExists_NotOverwritten()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);
            PngJsonCaptureRunCaptureIndexCommitter committer = MakeCommitter(layout);

            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit, layout, out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            string finalPath = Path.Combine(layout.FinalRunRoot, "capture.index");
            byte[] existing = new byte[] { 5, 5, 5 };
            File.WriteAllBytes(finalPath, existing);

            Assert.Throws<IOException>(() => committer.Commit(operation, token));
            Assert.That(File.ReadAllBytes(finalPath), Is.EqualTo(existing));
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index.tmp")), Is.False);
        }

        [Test]
        public void Commit_FinalReappearsBeforeRename_Rejected()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);

            string finalPath = Path.Combine(layout.FinalRunRoot, "capture.index");
            FinalAppearingFileSystem fileSystem = new FinalAppearingFileSystem(finalPath);
            PngJsonCaptureRunCaptureIndexCommitter committer = new PngJsonCaptureRunCaptureIndexCommitter(layout, fileSystem);

            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit, layout, out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            Assert.Throws<IOException>(() => committer.Commit(operation, token));

            // The final appeared before the rename, so the rename never ran:
            // the temporary remains and the appeared final was not overwritten.
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index.tmp")), Is.True);
            Assert.That(File.Exists(finalPath), Is.True);
            Assert.That(File.ReadAllBytes(finalPath), Is.EqualTo(new byte[] { 7, 7, 7 }));
        }

        [Test]
        public void Commit_BackendException_PropagatesUnchanged()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);

            ThrowingFileSystem fileSystem = new ThrowingFileSystem();
            PngJsonCaptureRunCaptureIndexCommitter committer = new PngJsonCaptureRunCaptureIndexCommitter(layout, fileSystem);

            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit, layout, out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => committer.Commit(operation, token));
            Assert.That(ex.Message, Is.EqualTo("backend failure"));
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.False);
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index.tmp")), Is.False);
        }

        [Test]
        public void Commit_PostRenameFlushFailure_FinalNotRolledBack()
        {
            (string sandbox, string staging, string final) = MakeSandbox();
            _sandboxes.Add(sandbox);

            CaptureRunRootLayout layout = new CaptureRunRootLayout(staging, final, 1);
            Directory.CreateDirectory(layout.FinalRunRoot);

            FlushFailingFileSystem fileSystem = new FlushFailingFileSystem();
            PngJsonCaptureRunCaptureIndexCommitter committer = new PngJsonCaptureRunCaptureIndexCommitter(layout, fileSystem);

            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit, layout, out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);
            byte[] canonical = operation.GetCanonicalBytes();

            InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => committer.Commit(operation, token));
            Assert.That(ex.Message, Is.EqualTo("flush failed"));

            // The rename already ran; the final must not be rolled back or deleted.
            Assert.That(File.Exists(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.True);
            Assert.That(File.ReadAllBytes(Path.Combine(layout.FinalRunRoot, "capture.index")), Is.EqualTo(canonical));
        }

        [Test]
        public void Commit_Reuse_SwappedTmp_RenamesVerifiedIdentity()
        {
            CaptureRunRootLayout layout = MakeLayout();
            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit, layout, out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);
            byte[] canonical = operation.GetCanonicalBytes();

            TrackingFileSystem fileSystem = new TrackingFileSystem { TmpExists = true };
            fileSystem.TmpFile = new CaptureIndexCommitFile(null, new MemoryStream(canonical));
            PngJsonCaptureRunCaptureIndexCommitter committer = new PngJsonCaptureRunCaptureIndexCommitter(layout, fileSystem);

            PngJsonCaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(operation, token);
            Assert.That(receipt, Is.Not.Null);

            // The rename must target the exact verified file identity, never a
            // file re-opened by path after a swap.
            Assert.That(fileSystem.RenamedFiles, Has.Count.EqualTo(1));
            Assert.That(fileSystem.RenamedFiles[0], Is.SameAs(fileSystem.TmpFile));
            Assert.That(fileSystem.DeletedFiles, Is.Empty);
            Assert.That(fileSystem.CreatedNames, Is.Empty);
        }

        [Test]
        public void Commit_Replace_SwappedTmp_DeletesVerifiedIdentity()
        {
            CaptureRunRootLayout layout = MakeLayout();
            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit, layout, out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            TrackingFileSystem fileSystem = new TrackingFileSystem { TmpExists = true };
            fileSystem.TmpFile = new CaptureIndexCommitFile(null, new MemoryStream(new byte[] { 123, 125 }));
            PngJsonCaptureRunCaptureIndexCommitter committer = new PngJsonCaptureRunCaptureIndexCommitter(layout, fileSystem);

            PngJsonCaptureRunCaptureIndexCommitReceipt receipt = committer.Commit(operation, token);
            Assert.That(receipt, Is.Not.Null);

            // Only the exact verified temporary is deleted; a replacement file
            // swapped into the path is never deleted.
            Assert.That(fileSystem.DeletedFiles, Has.Count.EqualTo(1));
            Assert.That(fileSystem.DeletedFiles[0], Is.SameAs(fileSystem.TmpFile));
            Assert.That(fileSystem.CreatedNames, Has.Count.EqualTo(1));
            Assert.That(fileSystem.RenamedFiles, Has.Count.EqualTo(1));
        }

        [Test]
        public void Commit_DirectorySwap_EscapesRoot_NoWriteDeleteRename()
        {
            CaptureRunRootLayout layout = MakeLayout();
            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit, layout, out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            TrackingFileSystem fileSystem = new TrackingFileSystem { EscapesRoot = true };
            PngJsonCaptureRunCaptureIndexCommitter committer = new PngJsonCaptureRunCaptureIndexCommitter(layout, fileSystem);

            Assert.Throws<IOException>(() => committer.Commit(operation, token));

            // A parent directory swap must stop the commit before any write,
            // delete, or rename.
            Assert.That(fileSystem.CreatedNames, Is.Empty);
            Assert.That(fileSystem.RenamedFiles, Is.Empty);
            Assert.That(fileSystem.DeletedFiles, Is.Empty);
        }

        [Test]
        public void Commit_DirectoryFlushUnsupported_RejectedBeforeTmpCreation()
        {
            CaptureRunRootLayout layout = MakeLayout();
            PngJsonCaptureRunCaptureIndexCommitOperation operation = BuildCommitOperation(
                CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit, layout, out _, out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token);

            TrackingFileSystem fileSystem = new TrackingFileSystem { DirectoryFlushSupported = false };
            PngJsonCaptureRunCaptureIndexCommitter committer = new PngJsonCaptureRunCaptureIndexCommitter(layout, fileSystem);

            Assert.Throws<CaptureArtifactNoFollowUnavailableException>(() => committer.Commit(operation, token));

            // The preflight must reject before the temporary is created.
            Assert.That(fileSystem.CreatedNames, Is.Empty);
            Assert.That(fileSystem.RenamedFiles, Is.Empty);
            Assert.That(fileSystem.DeletedFiles, Is.Empty);
        }

        // ---- Fakes ----

        private sealed class FinalAppearingFileSystem : ICaptureIndexCommitFileSystem
        {
            private readonly CaptureIndexCommitFileSystem _inner = CaptureIndexCommitFileSystem.Create();
            private readonly string _finalPath;
            private int _captureIndexProbes;

            public FinalAppearingFileSystem(string finalPath)
            {
                _finalPath = finalPath;
            }

            public bool IsSupported => true;

            public bool IsDirectoryFlushSupported => true;

            public CaptureIndexCommitDirectory OpenDirectory(string absolutePath)
            {
                return _inner.OpenDirectory(absolutePath);
            }

            public CaptureIndexFileOpen TryOpen(CaptureIndexCommitDirectory directory, string name)
            {
                if (name == "capture.index")
                {
                    _captureIndexProbes++;
                    if (_captureIndexProbes == 2)
                    {
                        File.WriteAllBytes(_finalPath, new byte[] { 7, 7, 7 });
                    }
                }

                return _inner.TryOpen(directory, name);
            }

            public CaptureIndexCommitFile CreateNew(CaptureIndexCommitDirectory directory, string name)
            {
                return _inner.CreateNew(directory, name);
            }

            public void FlushFileData(CaptureIndexCommitFile file)
            {
                _inner.FlushFileData(file);
            }

            public void Rename(CaptureIndexCommitFile file, CaptureIndexCommitDirectory directory, string newName)
            {
                _inner.Rename(file, directory, newName);
            }

            public void Delete(CaptureIndexCommitFile file)
            {
                _inner.Delete(file);
            }

            public void FlushDirectory(CaptureIndexCommitDirectory directory)
            {
                _inner.FlushDirectory(directory);
            }
        }

        private sealed class ThrowingFileSystem : ICaptureIndexCommitFileSystem
        {
            public bool IsSupported => true;

            public bool IsDirectoryFlushSupported => true;

            public CaptureIndexCommitDirectory OpenDirectory(string absolutePath)
            {
                throw new InvalidOperationException("backend failure");
            }

            public CaptureIndexFileOpen TryOpen(CaptureIndexCommitDirectory directory, string name)
            {
                throw new InvalidOperationException("backend failure");
            }

            public CaptureIndexCommitFile CreateNew(CaptureIndexCommitDirectory directory, string name)
            {
                throw new InvalidOperationException("backend failure");
            }

            public void FlushFileData(CaptureIndexCommitFile file)
            {
                throw new InvalidOperationException("backend failure");
            }

            public void Rename(CaptureIndexCommitFile file, CaptureIndexCommitDirectory directory, string newName)
            {
                throw new InvalidOperationException("backend failure");
            }

            public void Delete(CaptureIndexCommitFile file)
            {
                throw new InvalidOperationException("backend failure");
            }

            public void FlushDirectory(CaptureIndexCommitDirectory directory)
            {
                throw new InvalidOperationException("backend failure");
            }
        }

        private sealed class FlushFailingFileSystem : ICaptureIndexCommitFileSystem
        {
            private readonly CaptureIndexCommitFileSystem _inner = CaptureIndexCommitFileSystem.Create();

            public bool IsSupported => true;

            public bool IsDirectoryFlushSupported => true;

            public CaptureIndexCommitDirectory OpenDirectory(string absolutePath)
            {
                return _inner.OpenDirectory(absolutePath);
            }

            public CaptureIndexFileOpen TryOpen(CaptureIndexCommitDirectory directory, string name)
            {
                return _inner.TryOpen(directory, name);
            }

            public CaptureIndexCommitFile CreateNew(CaptureIndexCommitDirectory directory, string name)
            {
                return _inner.CreateNew(directory, name);
            }

            public void FlushFileData(CaptureIndexCommitFile file)
            {
                _inner.FlushFileData(file);
            }

            public void Rename(CaptureIndexCommitFile file, CaptureIndexCommitDirectory directory, string newName)
            {
                _inner.Rename(file, directory, newName);
            }

            public void Delete(CaptureIndexCommitFile file)
            {
                _inner.Delete(file);
            }

            public void FlushDirectory(CaptureIndexCommitDirectory directory)
            {
                throw new InvalidOperationException("flush failed");
            }
        }

        private sealed class TrackingFileSystem : ICaptureIndexCommitFileSystem
        {
            public bool Supported = true;

            public bool DirectoryFlushSupported = true;

            public bool TmpExists;

            public CaptureIndexCommitFile TmpFile;

            public CaptureIndexFileOpenStatus TmpOpenStatus = CaptureIndexFileOpenStatus.Opened;

            public bool EscapesRoot;

            public readonly List<CaptureIndexCommitFile> OpenedFiles = new List<CaptureIndexCommitFile>();

            public readonly List<CaptureIndexCommitFile> RenamedFiles = new List<CaptureIndexCommitFile>();

            public readonly List<CaptureIndexCommitFile> DeletedFiles = new List<CaptureIndexCommitFile>();

            public readonly List<string> CreatedNames = new List<string>();

            public int FlushFileCalls;

            public bool IsSupported => Supported;

            public bool IsDirectoryFlushSupported => DirectoryFlushSupported;

            public CaptureIndexCommitDirectory OpenDirectory(string absolutePath)
            {
                return new CaptureIndexCommitDirectory(
                    new SafeFileHandle(IntPtr.Zero, true),
                    absolutePath,
                    "\\\\?\\" + absolutePath);
            }

            public CaptureIndexFileOpen TryOpen(CaptureIndexCommitDirectory directory, string name)
            {
                if (EscapesRoot)
                {
                    return CaptureIndexFileOpen.Of(CaptureIndexFileOpenStatus.EscapesRoot);
                }

                if (name != "capture.index.tmp")
                {
                    return CaptureIndexFileOpen.Of(CaptureIndexFileOpenStatus.Absent);
                }

                if (!TmpExists || TmpOpenStatus != CaptureIndexFileOpenStatus.Opened)
                {
                    return CaptureIndexFileOpen.Of(TmpOpenStatus == CaptureIndexFileOpenStatus.Opened
                        ? CaptureIndexFileOpenStatus.Absent
                        : TmpOpenStatus);
                }

                OpenedFiles.Add(TmpFile);
                return CaptureIndexFileOpen.Opened(TmpFile);
            }

            public CaptureIndexCommitFile CreateNew(CaptureIndexCommitDirectory directory, string name)
            {
                CreatedNames.Add(name);
                TmpExists = true;
                return new CaptureIndexCommitFile(null, new MemoryStream());
            }

            public void FlushFileData(CaptureIndexCommitFile file)
            {
                FlushFileCalls++;
            }

            public void Rename(CaptureIndexCommitFile file, CaptureIndexCommitDirectory directory, string newName)
            {
                RenamedFiles.Add(file);
                TmpExists = false;
            }

            public void Delete(CaptureIndexCommitFile file)
            {
                DeletedFiles.Add(file);
                TmpExists = false;
            }

            public void FlushDirectory(CaptureIndexCommitDirectory directory)
            {
            }
        }

        private sealed class FakeHandle : ICaptureRunLockHandle
        {
            private readonly List<string> _disposeLog;

            public FakeHandle(string lockPath, bool isCreated = true, List<string> disposeLog = null)
            {
                LockPath = lockPath;
                IsCreated = isCreated;
                _disposeLog = disposeLog;
            }

            public string LockPath { get; }

            public bool IsCreated { get; }

            public void Dispose()
            {
                _disposeLog?.Add(LockPath);
            }
        }

        private sealed class FakeInspector : ICaptureRunInitializationRecoveryInspector
        {
            private readonly CaptureRunInitializationRootObservation _staging;
            private readonly CaptureRunInitializationRootObservation _final;

            public FakeInspector(CaptureRunInitializationRootObservation staging, CaptureRunInitializationRootObservation final)
            {
                _staging = staging;
                _final = final;
            }

            public CaptureRunInitializationRecoveryInspectionSnapshot Inspect(CaptureRunInitializationRecoveryInspectionOperation operation)
            {
                return new CaptureRunInitializationRecoveryInspectionSnapshot(this, operation, _staging, _final);
            }
        }

        private sealed class FakeCleanupBackend : ICaptureRunInitializationRecoveryCleanupBackend
        {
            public CaptureRunInitializationRecoveryCleanupReceipt Execute(CaptureRunInitializationRecoveryCleanupOperation operation)
            {
                return new CaptureRunInitializationRecoveryCleanupReceipt(this, operation);
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

        private sealed class FakePublicationInspector : ICaptureRunPublicationRecoveryInspector
        {
            public CaptureRunPublicationRecoveryInspectionSnapshot Inspect(CaptureRunPublicationRecoveryInspectionOperation operation)
            {
                throw new InvalidOperationException("Not used.");
            }
        }

        private sealed class FakeArtifactInspector : IPngJsonCapturePublicationArtifactInspector
        {
            public PngJsonCapturePublicationArtifactInspectionSnapshot Inspect(PngJsonCapturePublicationArtifactInspectionOperation operation)
            {
                throw new InvalidOperationException("Not used.");
            }
        }

        // ---- General helpers ----

        private static CaptureRunRootLayout MakeLayout(long testRunId = 1)
        {
            return new CaptureRunRootLayout(
                IsWindows ? "C:\\staging" : "/staging",
                IsWindows ? "D:\\final" : "/final",
                testRunId);
        }

        private static void SetField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            field.SetValue(target, value);
        }

        private static object GetField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(field, Is.Not.Null, fieldName + " field not found.");
            return field.GetValue(target);
        }

        private static string RepositoryRoot()
        {
            return Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        }

        private static string ReadSource(string relativePath)
        {
            return File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath));
        }

        private static long Min(long left, long right)
        {
            return left < right ? left : right;
        }

        private static long Probe(CaptureRunPublicationEvidenceStatus status, long expectedByteLength, long limit)
        {
            switch (status)
            {
                case CaptureRunPublicationEvidenceStatus.Absent:
                    return 0;

                case CaptureRunPublicationEvidenceStatus.MatchesExpected:
                    return expectedByteLength;

                case CaptureRunPublicationEvidenceStatus.Mismatch:
                    return 1;

                case CaptureRunPublicationEvidenceStatus.Invalid:
                    return 0;

                case CaptureRunPublicationEvidenceStatus.LimitExceeded:
                    return checked(limit + 1);

                default:
                    throw new ArgumentOutOfRangeException(nameof(status));
            }
        }

        private static CaptureRunLockLease MakeLease(CaptureRunRootLayout layout, List<string> disposeLog = null)
        {
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(layout);
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, true, disposeLog);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, true, disposeLog);
            return new CaptureRunLockLease(pathSet, first, second);
        }

        private static CaptureRunMarkerBinding MakeMarkerBinding(CaptureRunRootLayout layout)
        {
            return CaptureRunMarkerBindingFactory.Create(
                layout.TestRunId,
                InitId,
                layout.StagingRunRootSha256,
                layout.FinalRunRootSha256);
        }

        private static CaptureRunInitializationRootObservation MakeRootObservation(
            CaptureRunRootRole role,
            bool rootExists,
            CaptureRunMarkerObservationStatus initStatus,
            CaptureRunInitializationMarker initMarker,
            CaptureRunMarkerObservationStatus readyStatus,
            CaptureRunReadyMarker readyMarker,
            bool hasNonMarker = false,
            bool hasUnknown = false,
            bool hasInitTmp = false,
            bool hasReadyTmp = false)
        {
            return new CaptureRunInitializationRootObservation(
                role, rootExists, hasInitTmp, initStatus, initMarker,
                hasReadyTmp, readyStatus, readyMarker, hasNonMarker, hasUnknown, false);
        }

        private static CaptureRunInitializationRootObservation MakeFullyCanonical(CaptureRunRootRole role, CaptureRunMarkerBinding binding)
        {
            CaptureRunInitializationMarker init = role == Staging ? binding.StagingInitialization : binding.FinalInitialization;
            CaptureRunReadyMarker ready = role == Staging ? binding.StagingReady : binding.FinalReady;
            return MakeRootObservation(role, true, Canonical, init, Canonical, ready);
        }

        private static CaptureRunInitializationOpenOutcome ForgeOutcome(
            CaptureRunInitializationRecoveryOrchestrationResult result,
            CaptureRunLockIdentityEvidence lockIdentityEvidence)
        {
            CaptureRunInitializationOpenOutcome outcome = (CaptureRunInitializationOpenOutcome)FormatterServices.GetUninitializedObject(
                typeof(CaptureRunInitializationOpenOutcome));
            SetField(outcome, "_orchestrationResult", result);
            SetField(outcome, "_sessionIssue", null);
            SetField(outcome, "_lockIdentityEvidence", lockIdentityEvidence);
            return outcome;
        }

        private CaptureRunInitializationOpenOutcome MakePublicationRecoveryOutcome(
            List<string> disposeLog,
            out CaptureRunInitializationSessionOwnershipLease owner,
            CaptureRunRootLayout layout = null)
        {
            layout = layout ?? MakeLayout();
            CaptureRunMarkerBinding binding = MakeMarkerBinding(layout);

            CaptureRunInitializationRootObservation staging = MakeRootObservation(
                Staging, true, Canonical, binding.StagingInitialization, Canonical, binding.StagingReady, hasNonMarker: true);
            CaptureRunInitializationRootObservation final = MakeFullyCanonical(Final, binding);

            FakeInspector inspector = new FakeInspector(staging, final);
            CaptureRunInitializationRecoveryExecutionCoordinator execution = new CaptureRunInitializationRecoveryExecutionCoordinator(
                new FakeCleanupBackend(), new FakeProvisioner(), new FakeWriter());
            CaptureRunInitializationRecoveryOrchestrationCoordinator orchestrator = new CaptureRunInitializationRecoveryOrchestrationCoordinator(inspector, execution);

            CaptureRunLockLease lease = MakeLease(layout, disposeLog);
            owner = CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            _owners.Add(owner);
            CaptureRunLockIdentityEvidence identity = CaptureRunLockIdentityEvidence.Create(owner, owner.LockPathSet);

            CaptureRunInitializationRecoveryInspectionOperation inspection = new CaptureRunInitializationRecoveryInspectionOperation(layout, identity, 4);
            CaptureRunInitializationRecoveryOrchestrationResult result = orchestrator.Execute(inspection);

            return ForgeOutcome(result, identity);
        }

        private CaptureRunPublicationRecoveryInspectionOperation MakeRecoveryInspectionOperation(
            int maximumPlanBytes,
            int maximumEntryCount,
            int maximumPathBytes,
            out CaptureRunInitializationSessionOwnershipLease owner,
            CaptureRunRootLayout layout = null)
        {
            return new CaptureRunPublicationRecoveryInspectionOperation(
                MakePublicationRecoveryOutcome(null, out owner, layout),
                maximumPlanBytes,
                maximumEntryCount,
                maximumPathBytes);
        }

        private static CaptureRunPublicationRecoveryInspectionSnapshot MakeRecoverySnapshot(
            ICaptureRunPublicationRecoveryInspector issuedBy,
            CaptureRunPublicationRecoveryInspectionOperation operation,
            CaptureRunPublicationDocumentObservation publicationPlanTemporary = null,
            CaptureRunPublicationDocumentObservation publicationPlan = null,
            CaptureRunPublicationDocumentObservation captureIndexTemporary = null,
            CaptureRunPublicationDocumentObservation captureIndex = null)
        {
            return new CaptureRunPublicationRecoveryInspectionSnapshot(
                issuedBy,
                operation,
                publicationPlanTemporary ?? MakeDoc(CaptureRunPublicationDocumentKind.PublicationPlanTemporary, DocAbsent),
                publicationPlan ?? MakeDoc(PublicationPlan, DocAbsent),
                captureIndexTemporary ?? MakeDoc(CaptureRunPublicationDocumentKind.CaptureIndexTemporary, DocAbsent),
                captureIndex ?? MakeDoc(CaptureIndex, DocAbsent),
                CaptureRunPublicationFramesObservationStatus.Directory,
                CaptureRunPublicationFramesObservationStatus.Directory,
                false, false, false, false);
        }

        private CaptureRunPublicationRecoveryDecision MakeDecision(
            PngJsonCapturePublicationPlan plan,
            bool indexAuthoritative,
            CaptureRunPublicationDocumentObservation captureIndexTemporary,
            out CaptureRunInitializationSessionOwnershipLease owner,
            CaptureRunRootLayout layout = null)
        {
            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation operation = MakeRecoveryInspectionOperation(1000, 4, 64, out owner, layout);
            CaptureRunPublicationRecoveryInspectionSnapshot snapshot = indexAuthoritative
                ? MakeRecoverySnapshot(inspector, operation, captureIndexTemporary: captureIndexTemporary, captureIndex: MakeDoc(CaptureIndex, DocCanonical, 100, plan))
                : MakeRecoverySnapshot(inspector, operation, captureIndexTemporary: captureIndexTemporary, publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            return CaptureRunPublicationRecoveryClassifier.Classify(snapshot);
        }

        private PngJsonCapturePublicationArtifactInspectionAuthority MakeRecoveryAuthority(
            PngJsonCapturePublicationPlan plan,
            bool indexAuthoritative,
            CaptureRunPublicationDocumentObservation captureIndexTemporary,
            out CaptureRunInitializationSessionOwnershipLease owner,
            CaptureRunRootLayout layout = null)
        {
            return PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(
                MakeDecision(plan ?? MakePlan(), indexAuthoritative, captureIndexTemporary, out owner, layout));
        }

        private static PngJsonCapturePublicationPlanEntry MakeEntry(long captureFrameId)
        {
            string id = captureFrameId.ToString(CultureInfo.InvariantCulture);
            return new PngJsonCapturePublicationPlanEntry(
                captureFrameId,
                "frames/" + id + ".png.stage",
                "frames/" + id + ".json.stage",
                "frames/" + id + ".png",
                "frames/" + id + ".json",
                16,
                32,
                HashA,
                HashA);
        }

        private static PngJsonCapturePublicationPlan MakePlan(
            long testRunId = 1,
            PngJsonCapturePublicationPlanEntry[] entries = null)
        {
            return new PngJsonCapturePublicationPlan(
                testRunId,
                InitId,
                HashA,
                entries ?? new[] { MakeEntry(10) });
        }

        private static CaptureRunPublicationDocumentObservation MakeDoc(
            CaptureRunPublicationDocumentKind kind,
            CaptureRunPublicationDocumentObservationStatus status,
            int probedByteCount = 0,
            PngJsonCapturePublicationPlan plan = null)
        {
            return new CaptureRunPublicationDocumentObservation(kind, status, probedByteCount, plan);
        }

        private static PngJsonCapturePublicationArtifactInspectionOperation MakeOperation(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            long maximumPngByteCount = 1000)
        {
            return PngJsonCapturePublicationArtifactInspectionOperation.Create(authority, maximumPngByteCount);
        }

        private static PngJsonCapturePublicationArtifactEntryObservation MakeIndexObservation(
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token,
            PngJsonCapturePublicationArtifactInspectionOperation operation,
            int index,
            CaptureRunPublicationEvidenceStatus stagingPng,
            CaptureRunPublicationEvidenceStatus stagingSidecar,
            CaptureRunPublicationEvidenceStatus finalPng,
            CaptureRunPublicationEvidenceStatus finalSidecar)
        {
            PngJsonCapturePublicationArtifactInspectionPathSet paths = operation.GetArtifactPaths(index);
            PngJsonCapturePublicationPlanEntry entry = paths.Entry;
            long pngLimit = Min(entry.PngByteLength, operation.MaximumPngByteCount);
            long sidecarLimit = Min(entry.SidecarByteLength, operation.MaximumSidecarByteCount);
            return PngJsonCapturePublicationArtifactEntryObservation.CreateIndexLocal(
                token, operation, paths,
                stagingPng, Probe(stagingPng, entry.PngByteLength, pngLimit),
                stagingSidecar, Probe(stagingSidecar, entry.SidecarByteLength, sidecarLimit),
                finalPng, Probe(finalPng, entry.PngByteLength, pngLimit),
                finalSidecar, Probe(finalSidecar, entry.SidecarByteLength, sidecarLimit));
        }

        private static PngJsonCapturePublicationArtifactInspectionSnapshot MakeSnapshotArray(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            CaptureRunPublicationEvidenceStatus traceStatus,
            long traceCount,
            CaptureRunPublicationEvidenceStatus[] stagingPng,
            CaptureRunPublicationEvidenceStatus[] stagingSidecar,
            CaptureRunPublicationEvidenceStatus[] finalPng,
            CaptureRunPublicationEvidenceStatus[] finalSidecar,
            long maximumPngByteCount = 1000)
        {
            PngJsonCapturePublicationArtifactInspectionOperation operation = MakeOperation(authority, maximumPngByteCount);
            PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken token =
                PngJsonCapturePublicationArtifactInspectionOperation.ValidationToken.Acquire(operation);
            Assert.That(operation.EntryCount, Is.EqualTo(stagingPng.Length));

            PngJsonCapturePublicationArtifactEntryObservation[] entries =
                new PngJsonCapturePublicationArtifactEntryObservation[stagingPng.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                entries[i] = MakeIndexObservation(token, operation, i, stagingPng[i], stagingSidecar[i], finalPng[i], finalSidecar[i]);
            }

            return PngJsonCapturePublicationArtifactInspectionSnapshot.Create(
                new FakeArtifactInspector(), operation, traceStatus, traceCount, entries);
        }

        private static PngJsonCapturePublicationArtifactInspectionSnapshot MakeSnapshotSingle(
            PngJsonCapturePublicationArtifactInspectionAuthority authority,
            CaptureRunPublicationEvidenceStatus traceStatus,
            long traceCount,
            CaptureRunPublicationEvidenceStatus stagingPng,
            CaptureRunPublicationEvidenceStatus stagingSidecar,
            CaptureRunPublicationEvidenceStatus finalPng,
            CaptureRunPublicationEvidenceStatus finalSidecar,
            long maximumPngByteCount = 1000)
        {
            return MakeSnapshotArray(
                authority,
                traceStatus,
                traceCount,
                new[] { stagingPng },
                new[] { stagingSidecar },
                new[] { finalPng },
                new[] { finalSidecar },
                maximumPngByteCount);
        }

        private static PngJsonCapturePublicationArtifactRecoveryActionPlan BuildPlan(
            PngJsonCapturePublicationArtifactInspectionSnapshot snapshot)
        {
            return PngJsonCapturePublicationArtifactRecoveryActionPlan.Create(
                PngJsonCapturePublicationArtifactRecoveryClassifier.Classify(snapshot));
        }

        private static PngJsonCapturePublicationArtifactInspectionSnapshot MakeCommitSnapshot(
            PngJsonCapturePublicationArtifactInspectionAuthority authority)
        {
            return MakeSnapshotSingle(authority, EvMatchesExpected, 1, EvAbsent, EvAbsent, EvMatchesExpected, EvMatchesExpected);
        }

        private static PngJsonCapturePublicationArtifactRecoveryActionPlan BuildCommitPlan(
            PngJsonCapturePublicationArtifactInspectionAuthority authority)
        {
            return BuildPlan(MakeCommitSnapshot(authority));
        }

        private PngJsonCaptureRunCaptureIndexCommitOperation BuildCommitOperation(
            CaptureRunCaptureIndexCommitMode mode,
            CaptureRunRootLayout layout,
            out CaptureRunInitializationSessionOwnershipLease owner,
            out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            return BuildCommitOperationWithLimits(mode, layout, 1000, 4, 64, out owner, out token);
        }

        private PngJsonCaptureRunCaptureIndexCommitOperation BuildCommitOperationWithLimits(
            CaptureRunCaptureIndexCommitMode mode,
            CaptureRunRootLayout layout,
            int maximumPlanBytes,
            int maximumEntryCount,
            int maximumPathBytes,
            out CaptureRunInitializationSessionOwnershipLease owner,
            out PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            PngJsonCapturePublicationPlan plan = MakePlan(1, new[] { MakeEntry(10) });

            CaptureRunPublicationDocumentObservation captureIndexTemporary;
            switch (mode)
            {
                case CaptureRunCaptureIndexCommitMode.CreateTemporaryAndCommit:
                    captureIndexTemporary = MakeDoc(CaptureIndexTemporary, DocAbsent);
                    break;

                case CaptureRunCaptureIndexCommitMode.ReuseCanonicalTemporaryAndCommit:
                    captureIndexTemporary = MakeDoc(CaptureIndexTemporary, DocCanonical, 100, plan);
                    break;

                case CaptureRunCaptureIndexCommitMode.ReplaceInvalidTemporaryAndCommit:
                    captureIndexTemporary = MakeDoc(CaptureIndexTemporary, DocInvalid);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(mode));
            }

            FakePublicationInspector inspector = new FakePublicationInspector();
            CaptureRunPublicationRecoveryInspectionOperation inspectionOperation =
                MakeRecoveryInspectionOperation(maximumPlanBytes, maximumEntryCount, maximumPathBytes, out owner, layout);
            CaptureRunPublicationRecoveryInspectionSnapshot recoverySnapshot = MakeRecoverySnapshot(
                inspector,
                inspectionOperation,
                captureIndexTemporary: captureIndexTemporary,
                publicationPlan: MakeDoc(PublicationPlan, DocCanonical, 100, plan));
            CaptureRunPublicationRecoveryDecision decision = CaptureRunPublicationRecoveryClassifier.Classify(recoverySnapshot);
            PngJsonCapturePublicationArtifactInspectionAuthority authority =
                PngJsonCapturePublicationArtifactInspectionAuthority.FromRecovery(decision);

            PngJsonCapturePublicationArtifactRecoveryActionPlan actionPlan = BuildCommitPlan(authority);
            token = actionPlan.AcquireValidationToken();
            return PngJsonCaptureRunCaptureIndexCommitOperation.CreateIndexLocal(actionPlan, token, 0);
        }

        private static PngJsonCaptureRunCaptureIndexCommitOperation ForgeOperation(
            PngJsonCaptureRunCaptureIndexCommitOperation template,
            byte[] canonicalBytes)
        {
            PngJsonCaptureRunCaptureIndexCommitOperation operation = (PngJsonCaptureRunCaptureIndexCommitOperation)FormatterServices.GetUninitializedObject(
                typeof(PngJsonCaptureRunCaptureIndexCommitOperation));
            SetField(operation, "_actionPlan", GetField(template, "_actionPlan"));
            SetField(operation, "_stepIndex", GetField(template, "_stepIndex"));
            SetField(operation, "_publicationPaths", GetField(template, "_publicationPaths"));
            SetField(operation, "_mode", GetField(template, "_mode"));
            SetField(operation, "_canonicalBytes", canonicalBytes);
            return operation;
        }

        private static PngJsonCaptureRunCaptureIndexCommitter MakeCommitter(CaptureRunRootLayout layout)
        {
            return new PngJsonCaptureRunCaptureIndexCommitter(layout);
        }

        private static (string sandbox, string staging, string final) MakeSandbox()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "zantetsuken-committer-" + Guid.NewGuid().ToString("N"));
            string staging = Path.Combine(sandbox, "staging");
            string final = Path.Combine(sandbox, "final");
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(final);
            return (sandbox, staging, final);
        }
    }
}
