using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 Windows Fresh NVENC Run chunk
    /// artifact publication filesystem backend: the handle-bound
    /// non-overwriting rename, the staging metadata-only inspection, and the
    /// single post-placement full verification of the final file. Uses real
    /// temporary directories with a deliberately tiny fixed verification
    /// buffer; no real GPU, NVENC, sleep, or unbounded external wait is used.
    /// </summary>
    public class NvencRunArtifactPublicationFileSystemContractTests
    {
        private const int WatchdogTimeoutMs = 5000;

        // Far smaller than any chunk under test, so a length-proportional
        // buffer or a whole-file array would be impossible to hide.
        private const int TinyBufferLength = 7;

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

        // ---- Validation ----

        [Test]
        public void TryPublishFresh_NullArguments_Throw()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();
            CaptureArtifactDescriptor descriptor = MakeDescriptor(64, new byte[64]);
            NvencRunArtifactPublicationFileSystem fileSystem = NvencRunArtifactPublicationFileSystem.Create();

            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => fileSystem.TryPublishFresh(null, descriptor, new byte[TinyBufferLength])).ParamName,
                Is.EqualTo("rootLayout"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => fileSystem.TryPublishFresh(sandbox.Layout, null, new byte[TinyBufferLength])).ParamName,
                Is.EqualTo("descriptor"));
            Assert.That(
                Assert.Throws<ArgumentNullException>(
                    () => fileSystem.TryPublishFresh(sandbox.Layout, descriptor, null)).ParamName,
                Is.EqualTo("verificationBuffer"));
            Assert.That(
                Assert.Throws<ArgumentException>(
                    () => fileSystem.TryPublishFresh(sandbox.Layout, descriptor, new byte[0])).ParamName,
                Is.EqualTo("verificationBuffer"));
        }

        [Test]
        public void IsSupported_ReportsTheWindowsNoFollowCapability()
        {
            NvencRunArtifactPublicationFileSystem fileSystem = NvencRunArtifactPublicationFileSystem.Create();
            Assert.That(fileSystem.IsSupported, Is.EqualTo(RuntimeInformation.IsOSPlatform(OSPlatform.Windows)));
        }

        // ---- Success ----

        [Test]
        public void TryPublishFresh_Placed_StagingGone_FinalMatchesLengthAndHash()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();
            byte[] content = MakeContent(64, 0x40);
            sandbox.WriteStagingChunk(content);
            CaptureArtifactDescriptor descriptor = MakeDescriptor(content.Length, content);

            Assert.That(Publish(sandbox, descriptor), Is.True);

            Assert.That(File.Exists(sandbox.StagingChunkPath), Is.False);
            Assert.That(File.Exists(sandbox.FinalChunkPath), Is.True);
            byte[] published = File.ReadAllBytes(sandbox.FinalChunkPath);
            Assert.That(published, Is.EqualTo(content));
            Assert.That(published.Length, Is.EqualTo((int)descriptor.ByteLength));
            Assert.That(Sha256Hex(published), Is.EqualTo(descriptor.ContentHash));

            AssertExclusivelyOpenable(sandbox.FinalChunkPath);
        }

        [Test]
        public void TryPublishFresh_VerifiesTheWholeFinalThroughTheTinyFixedBuffer()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();

            // Many buffer-sized reads are needed for this chunk, so a passing
            // verification can only come from streaming through the supplied
            // fixed buffer, never from one whole-file array.
            byte[] content = MakeContent(4099, 0x11);
            sandbox.WriteStagingChunk(content);
            CaptureArtifactDescriptor descriptor = MakeDescriptor(content.Length, content);

            NvencRunArtifactPublicationFileSystem fileSystem = NvencRunArtifactPublicationFileSystem.Create();
            Assert.That(fileSystem.TryPublishFresh(sandbox.Layout, descriptor, new byte[1]), Is.True);
            Assert.That(File.ReadAllBytes(sandbox.FinalChunkPath), Is.EqualTo(content));
        }

        [Test]
        public void TryPublishFresh_LeavesTheLegacyAndNvencPlanTemporariesUntouched()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();
            byte[] content = MakeContent(64, 0x40);
            sandbox.WriteStagingChunk(content);

            string legacyTemporary = Path.Combine(sandbox.StagingRunRoot, "publication.plan.tmp");
            string nvencTemporary = Path.Combine(
                sandbox.StagingRunRoot, NvencRunPublicationPlanCommitOperation.PreCommitBasename);
            string plan = Path.Combine(sandbox.StagingRunRoot, NvencRunPublicationPlanCommitOperation.FinalBasename);
            byte[] legacyBytes = { 1, 2, 3 };
            byte[] nvencBytes = { 4, 5, 6, 7 };
            byte[] planBytes = { 8, 9 };
            File.WriteAllBytes(legacyTemporary, legacyBytes);
            File.WriteAllBytes(nvencTemporary, nvencBytes);
            File.WriteAllBytes(plan, planBytes);

            Assert.That(Publish(sandbox, MakeDescriptor(content.Length, content)), Is.True);

            Assert.That(File.ReadAllBytes(legacyTemporary), Is.EqualTo(legacyBytes));
            Assert.That(File.ReadAllBytes(nvencTemporary), Is.EqualTo(nvencBytes));
            Assert.That(File.ReadAllBytes(plan), Is.EqualTo(planBytes));
        }

        // ---- Failure before the rename ----

        [Test]
        public void TryPublishFresh_WrongStagingLength_RefusedBeforeRename_StagingKept()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();
            byte[] content = MakeContent(64, 0x40);
            sandbox.WriteStagingChunk(content);

            // Only the staging metadata length is inspected, so the mismatch is
            // seen before the rename and the chunk is left exactly as it was.
            CaptureArtifactDescriptor descriptor = MakeDescriptor(content.Length + 1, content);

            Assert.That(Publish(sandbox, descriptor), Is.False);

            Assert.That(File.Exists(sandbox.StagingChunkPath), Is.True);
            Assert.That(File.ReadAllBytes(sandbox.StagingChunkPath), Is.EqualTo(content));
            Assert.That(File.Exists(sandbox.FinalChunkPath), Is.False);
            AssertExclusivelyOpenable(sandbox.StagingChunkPath);
        }

        [Test]
        public void TryPublishFresh_ExistingFinal_NotOverwritten_StagingKept()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();
            byte[] content = MakeContent(64, 0x40);
            sandbox.WriteStagingChunk(content);

            byte[] existing = MakeContent(9, 0x99);
            Directory.CreateDirectory(Path.GetDirectoryName(sandbox.FinalChunkPath));
            File.WriteAllBytes(sandbox.FinalChunkPath, existing);

            Assert.That(Publish(sandbox, MakeDescriptor(content.Length, content)), Is.False);

            Assert.That(File.ReadAllBytes(sandbox.FinalChunkPath), Is.EqualTo(existing));
            Assert.That(File.Exists(sandbox.StagingChunkPath), Is.True);
            Assert.That(File.ReadAllBytes(sandbox.StagingChunkPath), Is.EqualTo(content));
            AssertExclusivelyOpenable(sandbox.FinalChunkPath);
            AssertExclusivelyOpenable(sandbox.StagingChunkPath);
        }

        [Test]
        public void TryPublishFresh_AbsentStagingChunk_Refused_NoFinalPlaced()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();
            Directory.CreateDirectory(Path.GetDirectoryName(sandbox.StagingChunkPath));

            Assert.That(Publish(sandbox, MakeDescriptor(64, MakeContent(64, 0x40))), Is.False);
            Assert.That(File.Exists(sandbox.FinalChunkPath), Is.False);
        }

        // ---- Failure after the rename ----

        [Test]
        public void TryPublishFresh_SameLengthWrongContent_RenamedThenRefused_NoRollback()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();
            byte[] actual = MakeContent(64, 0x40);
            byte[] expected = MakeContent(64, 0x7f);
            Assert.That(actual, Is.Not.EqualTo(expected));
            sandbox.WriteStagingChunk(actual);

            // The staging content is never read or re-hashed, so the rename
            // happens first and the mismatch is only found by the single full
            // read of the placed final file.
            CaptureArtifactDescriptor descriptor = MakeDescriptor(actual.Length, expected);

            Assert.That(Publish(sandbox, descriptor), Is.False);

            Assert.That(File.Exists(sandbox.StagingChunkPath), Is.False,
                "the rename must have happened before the content was ever inspected.");
            Assert.That(File.Exists(sandbox.FinalChunkPath), Is.True,
                "a placed but unverified final is left for Recovery, never deleted or rolled back.");
            Assert.That(File.ReadAllBytes(sandbox.FinalChunkPath), Is.EqualTo(actual));
            AssertExclusivelyOpenable(sandbox.FinalChunkPath);
        }

        // ---- Sharing refused for the whole publication ----

        [Test]
        public void TryPublishFresh_ConcurrentWriterOnStagingChunk_RefusedBeforeRename()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();
            byte[] content = MakeContent(64, 0x40);
            sandbox.WriteStagingChunk(content);
            CaptureArtifactDescriptor descriptor = MakeDescriptor(content.Length, content);

            // A writer that could still change the chunk mid-publication would
            // let the same-handle verification pass over bytes the final path
            // no longer holds, so the staging open must refuse write sharing.
            using (new FileStream(
                sandbox.StagingChunkPath,
                FileMode.Open,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete))
            {
                Assert.That(Publish(sandbox, descriptor), Is.False);
            }

            Assert.That(File.Exists(sandbox.StagingChunkPath), Is.True);
            Assert.That(File.ReadAllBytes(sandbox.StagingChunkPath), Is.EqualTo(content));
            Assert.That(File.Exists(sandbox.FinalChunkPath), Is.False);
        }

        [Test]
        public void TryPublishFresh_ConcurrentDeleteSharedStagingChunk_RefusedBeforeRename()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();
            byte[] content = MakeContent(64, 0x40);
            sandbox.WriteStagingChunk(content);
            CaptureArtifactDescriptor descriptor = MakeDescriptor(content.Length, content);

            // A handle that can still delete or rename the chunk is refused for
            // the same reason: the verified identity could be unlinked from the
            // descriptor's final path after the check.
            using (SafeFileHandle held = OpenFileWithDeleteAccess(sandbox.StagingChunkPath))
            {
                Assert.That(held.IsInvalid, Is.False, "the conflicting file handle could not be opened.");
                Assert.That(Publish(sandbox, descriptor), Is.False);
            }

            Assert.That(File.Exists(sandbox.StagingChunkPath), Is.True);
            Assert.That(File.Exists(sandbox.FinalChunkPath), Is.False);

            Assert.That(Publish(sandbox, descriptor), Is.True);
            Assert.That(File.ReadAllBytes(sandbox.FinalChunkPath), Is.EqualTo(content));
        }

        [Test]
        public void TryPublishFresh_RunRootHeldWithDeleteAccess_RefusedBeforeRename()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();
            byte[] content = MakeContent(64, 0x40);
            sandbox.WriteStagingChunk(content);
            CaptureArtifactDescriptor descriptor = MakeDescriptor(content.Length, content);

            // A Run root another handle can still rename or delete could be
            // moved out from under the verified placement, so the root open
            // refuses delete sharing and the conflicting holder makes the whole
            // publication fail closed.
            using (SafeFileHandle held = OpenDirectoryForDelete(sandbox.StagingRunRoot))
            {
                Assert.That(held.IsInvalid, Is.False, "the conflicting directory handle could not be opened.");
                Assert.That(Publish(sandbox, descriptor), Is.False);
            }

            Assert.That(File.Exists(sandbox.StagingChunkPath), Is.True);
            Assert.That(File.Exists(sandbox.FinalChunkPath), Is.False);

            // With the conflicting handle released the same publication
            // succeeds, so the refusal came from the sharing conflict alone.
            Assert.That(Publish(sandbox, descriptor), Is.True);
            Assert.That(File.ReadAllBytes(sandbox.FinalChunkPath), Is.EqualTo(content));
        }

        [Test]
        public void TryPublishFresh_FinalChunksDirectoryHeldWithDeleteAccess_RefusedBeforeRename()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();
            byte[] content = MakeContent(64, 0x40);
            sandbox.WriteStagingChunk(content);
            Directory.CreateDirectory(sandbox.FinalChunksDirectory);
            CaptureArtifactDescriptor descriptor = MakeDescriptor(content.Length, content);

            using (SafeFileHandle held = OpenDirectoryForDelete(sandbox.FinalChunksDirectory))
            {
                Assert.That(held.IsInvalid, Is.False, "the conflicting directory handle could not be opened.");
                Assert.That(Publish(sandbox, descriptor), Is.False);
            }

            Assert.That(File.Exists(sandbox.StagingChunkPath), Is.True);
            Assert.That(File.Exists(sandbox.FinalChunkPath), Is.False);

            Assert.That(Publish(sandbox, descriptor), Is.True);
            Assert.That(File.ReadAllBytes(sandbox.FinalChunkPath), Is.EqualTo(content));
        }

        // ---- No-follow ----

        [Test]
        public void TryPublishFresh_StagingChunkPathIsAReparsePoint_Refused()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();
            byte[] content = MakeContent(64, 0x40);

            string target = Path.Combine(sandbox.Root, "outside-file-target");
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(sandbox.StagingChunksDirectory);
            CreateJunction(sandbox.StagingChunkPath, target);
            _junctions.Add(sandbox.StagingChunkPath);

            Assert.That(Publish(sandbox, MakeDescriptor(content.Length, content)), Is.False);
            Assert.That(File.Exists(sandbox.FinalChunkPath), Is.False);
        }

        [Test]
        public void TryPublishFresh_StagingIntermediateDirectoryIsAReparsePoint_Refused()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();
            byte[] content = MakeContent(64, 0x40);

            // The real chunk lives outside the staging Run root and is reached
            // only through a junction placed at the intermediate directory.
            string target = Path.Combine(sandbox.Root, "outside-staging-chunks");
            Directory.CreateDirectory(target);
            File.WriteAllBytes(Path.Combine(target, Sandbox.ChunkFileName), content);
            CreateJunction(sandbox.StagingChunksDirectory, target);
            _junctions.Add(sandbox.StagingChunksDirectory);

            Assert.That(Publish(sandbox, MakeDescriptor(content.Length, content)), Is.False);

            Assert.That(File.Exists(Path.Combine(target, Sandbox.ChunkFileName)), Is.True);
            Assert.That(File.Exists(sandbox.FinalChunkPath), Is.False);
        }

        [Test]
        public void TryPublishFresh_FinalIntermediateDirectoryIsAReparsePoint_Refused()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox();
            byte[] content = MakeContent(64, 0x40);
            sandbox.WriteStagingChunk(content);

            string target = Path.Combine(sandbox.Root, "outside-final-chunks");
            Directory.CreateDirectory(target);
            CreateJunction(sandbox.FinalChunksDirectory, target);
            _junctions.Add(sandbox.FinalChunksDirectory);

            Assert.That(Publish(sandbox, MakeDescriptor(content.Length, content)), Is.False);

            Assert.That(File.Exists(Path.Combine(target, Sandbox.ChunkFileName)), Is.False,
                "the rename must never land in a junction target outside the final Run root.");
            Assert.That(File.Exists(sandbox.StagingChunkPath), Is.True);
            AssertExclusivelyOpenable(sandbox.StagingChunkPath);
        }

        [Test]
        public void TryPublishFresh_StagingRunRootIsAReparsePoint_Refused()
        {
            RequireWindows();
            Sandbox sandbox = CreateSandbox(createStagingRunRoot: false);

            string target = Path.Combine(sandbox.Root, "outside-staging-run-root");
            Directory.CreateDirectory(Path.Combine(target, Sandbox.ChunksDirectoryName));
            byte[] content = MakeContent(64, 0x40);
            File.WriteAllBytes(
                Path.Combine(Path.Combine(target, Sandbox.ChunksDirectoryName), Sandbox.ChunkFileName), content);

            Directory.CreateDirectory(Path.GetDirectoryName(sandbox.StagingRunRoot));
            CreateJunction(sandbox.StagingRunRoot, target);
            _junctions.Add(sandbox.StagingRunRoot);

            Assert.That(Publish(sandbox, MakeDescriptor(content.Length, content)), Is.False);
            Assert.That(File.Exists(sandbox.FinalChunkPath), Is.False);
        }

        // ---- Volume boundary ----

        [Test]
        public void TryPublishFresh_CrossVolume_RefusedWithoutRename()
        {
            RequireWindows();

            string otherRoot = TryCreateSandboxOnAnotherVolume();
            if (otherRoot == null)
            {
                Assert.Ignore(
                    "No second writable fixed volume is available; the cross-volume refusal is not exercised here.");
            }

            Sandbox sandbox = CreateSandbox();
            byte[] content = MakeContent(64, 0x40);
            sandbox.WriteStagingChunk(content);

            string finalBase = Path.Combine(otherRoot, "final");
            CaptureRunRootLayout crossVolume = new CaptureRunRootLayout(
                sandbox.StagingBase, finalBase, sandbox.TestRunId);
            Directory.CreateDirectory(crossVolume.FinalRunRoot);

            CaptureArtifactDescriptor descriptor = MakeDescriptor(content.Length, content);
            NvencRunArtifactPublicationFileSystem fileSystem = NvencRunArtifactPublicationFileSystem.Create();

            Assert.That(
                fileSystem.TryPublishFresh(crossVolume, descriptor, new byte[TinyBufferLength]), Is.False);

            Assert.That(File.Exists(sandbox.StagingChunkPath), Is.True);
            Assert.That(
                Directory.Exists(Path.Combine(crossVolume.FinalRunRoot, Sandbox.ChunksDirectoryName)), Is.False,
                "the volume mismatch must be refused before any final directory is created.");
        }

        // ---- Helpers ----

        private bool Publish(Sandbox sandbox, CaptureArtifactDescriptor descriptor)
        {
            NvencRunArtifactPublicationFileSystem fileSystem = NvencRunArtifactPublicationFileSystem.Create();
            return fileSystem.TryPublishFresh(sandbox.Layout, descriptor, new byte[TinyBufferLength]);
        }

        private static CaptureArtifactDescriptor MakeDescriptor(long byteLength, byte[] expectedContent)
        {
            return NvencRunChunkArtifactDescriptorFactory.Create(
                "nvenc-chunk-0", byteLength, Sha256Hex(expectedContent));
        }

        private static byte[] MakeContent(int length, byte seed)
        {
            byte[] content = new byte[length];
            for (int i = 0; i < length; i++)
            {
                content[i] = (byte)(seed + i);
            }

            return content;
        }

        private static string Sha256Hex(byte[] bytes)
        {
            byte[] hash;
            using (SHA256 sha = SHA256.Create())
            {
                hash = sha.ComputeHash(bytes);
            }

            const string hex = "0123456789abcdef";
            char[] chars = new char[hash.Length * 2];
            for (int i = 0; i < hash.Length; i++)
            {
                chars[i * 2] = hex[hash[i] >> 4];
                chars[i * 2 + 1] = hex[hash[i] & 15];
            }

            return new string(chars);
        }

        /// <summary>
        /// Opens the file with no sharing at all, which only succeeds when the
        /// backend released every handle and stream it opened over that file.
        /// </summary>
        private static void AssertExclusivelyOpenable(string path)
        {
            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
            }
        }

        /// <summary>
        /// Opens a directory with DELETE access and full sharing: a handle that
        /// could still rename or delete the directory. Managed directory APIs
        /// cannot express this, so the conflicting holder is opened directly.
        /// </summary>
        private static SafeFileHandle OpenDirectoryForDelete(string path)
        {
            return OpenWithDeleteAccess(path, BackupSemantics);
        }

        /// <summary>
        /// Opens a file with DELETE access and full sharing: a handle that could
        /// still delete or rename the chunk during the publication.
        /// </summary>
        private static SafeFileHandle OpenFileWithDeleteAccess(string path)
        {
            return OpenWithDeleteAccess(path, 0u);
        }

        private static SafeFileHandle OpenWithDeleteAccess(string path, uint flags)
        {
            const uint deleteAccess = 0x00010000u;
            const uint fileGenericRead = 0x00120089u;
            const uint shareAll = 0x00000001u | 0x00000002u | 0x00000004u;
            const uint openExisting = 3u;

            return CreateFileW(
                path, fileGenericRead | deleteAccess, shareAll, IntPtr.Zero, openExisting, flags, IntPtr.Zero);
        }

        private const uint BackupSemantics = 0x02000000u;

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        private static void RequireWindows()
        {
            if (!IsWindows)
            {
                Assert.Ignore("The Fresh NVENC artifact publication backend requires Windows file handles.");
            }
        }

        private Sandbox CreateSandbox(bool createStagingRunRoot = true)
        {
            string root = Path.Combine(
                Path.GetTempPath(), "zantetsuken-artifactpublish-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            _sandboxes.Add(root);
            return new Sandbox(root, createStagingRunRoot);
        }

        private string TryCreateSandboxOnAnotherVolume()
        {
            string currentRoot = Path.GetPathRoot(Path.GetTempPath());

            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    if (string.Equals(drive.RootDirectory.FullName, currentRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string candidate = Path.Combine(
                        drive.RootDirectory.FullName,
                        "zantetsuken-artifactpublish-" + Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(candidate);
                    _sandboxes.Add(candidate);
                    return candidate;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            return null;
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

                    throw new InvalidOperationException("mklink /J did not terminate within the watchdog.");
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

        private sealed class Sandbox
        {
            internal const string ChunksDirectoryName = "chunks";

            internal const string ChunkFileName = "chunk-0.nvenc-idr-chunk-v1.h264";

            internal Sandbox(string root, bool createStagingRunRoot)
            {
                Root = root;
                TestRunId = 1;
                StagingBase = Path.Combine(root, "staging");
                FinalBase = Path.Combine(root, "final");
                Layout = new CaptureRunRootLayout(StagingBase, FinalBase, TestRunId);

                if (createStagingRunRoot)
                {
                    Directory.CreateDirectory(Layout.StagingRunRoot);
                }

                Directory.CreateDirectory(Layout.FinalRunRoot);
            }

            internal string Root { get; }

            internal long TestRunId { get; }

            internal string StagingBase { get; }

            internal string FinalBase { get; }

            internal CaptureRunRootLayout Layout { get; }

            internal string StagingRunRoot => Layout.StagingRunRoot;

            internal string StagingChunksDirectory => Path.Combine(StagingRunRoot, ChunksDirectoryName);

            internal string FinalChunksDirectory => Path.Combine(Layout.FinalRunRoot, ChunksDirectoryName);

            internal string StagingChunkPath => Path.Combine(StagingChunksDirectory, ChunkFileName);

            internal string FinalChunkPath => Path.Combine(FinalChunksDirectory, ChunkFileName);

            internal void WriteStagingChunk(byte[] content)
            {
                Directory.CreateDirectory(StagingChunksDirectory);
                File.WriteAllBytes(StagingChunkPath, content);
            }
        }
    }
}
