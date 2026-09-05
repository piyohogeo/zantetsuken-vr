using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunLockOsBackendFilesystemTests
    {
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
                Assert.Ignore("Capture Run OS lock backend tests require Windows file handles.");
            }
        }

        private (string sandbox, string staging, string final) MakeSandbox()
        {
            string sandbox = Path.Combine(Path.GetTempPath(), "zantetsuken-lock-" + Guid.NewGuid().ToString("N"));
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

        [Test]
        public void Acquire_Success_ReturnsCreatedHandleWithExactPath()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(MakeLayout(staging, final));
            CaptureRunLockOsBackend backend = CaptureRunLockOsBackend.Create();

            bool acquired = backend.TryAcquire(pathSet.FirstLockPath, out ICaptureRunLockHandle handle);

            Assert.That(acquired, Is.True);
            Assert.That(handle, Is.Not.Null);
            Assert.That(handle.IsCreated, Is.True);
            Assert.That(handle.LockPath, Is.EqualTo(pathSet.FirstLockPath));

            handle.Dispose();
            Assert.That(handle.IsCreated, Is.False);
        }

        [Test]
        public void Acquire_SameLockHeldByAnotherBackend_ReturnsFalseNull()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(MakeLayout(staging, final));

            CaptureRunLockOsBackend firstBackend = CaptureRunLockOsBackend.Create();
            CaptureRunLockOsBackend secondBackend = CaptureRunLockOsBackend.Create();

            bool first = firstBackend.TryAcquire(pathSet.FirstLockPath, out ICaptureRunLockHandle firstHandle);
            Assert.That(first, Is.True);

            bool second = secondBackend.TryAcquire(pathSet.FirstLockPath, out ICaptureRunLockHandle secondHandle);
            Assert.That(second, Is.False);
            Assert.That(secondHandle, Is.Null);
            Assert.That(firstHandle.IsCreated, Is.True);

            firstHandle.Dispose();
        }

        [Test]
        public void Acquire_AfterHandleDispose_SucceedsAgain()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(MakeLayout(staging, final));
            CaptureRunLockOsBackend backend = CaptureRunLockOsBackend.Create();

            bool first = backend.TryAcquire(pathSet.FirstLockPath, out ICaptureRunLockHandle firstHandle);
            Assert.That(first, Is.True);
            firstHandle.Dispose();

            bool second = backend.TryAcquire(pathSet.FirstLockPath, out ICaptureRunLockHandle secondHandle);
            Assert.That(second, Is.True);
            Assert.That(secondHandle.IsCreated, Is.True);
            secondHandle.Dispose();
        }

        [Test]
        public void Acquire_AfterRelease_LockFilePersists_AndReacquirable()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(MakeLayout(staging, final));
            CaptureRunLockOsBackend backend = CaptureRunLockOsBackend.Create();

            bool first = backend.TryAcquire(pathSet.FirstLockPath, out ICaptureRunLockHandle firstHandle);
            Assert.That(first, Is.True);
            firstHandle.Dispose();

            // The lock file is not ownership evidence and must persist.
            Assert.That(File.Exists(pathSet.FirstLockPath), Is.True);

            bool second = backend.TryAcquire(pathSet.FirstLockPath, out ICaptureRunLockHandle secondHandle);
            Assert.That(second, Is.True);
            secondHandle.Dispose();
        }

        [Test]
        public void Coordinator_TwoLocksAcquired_OtherCoordinatorSamePathSetFails()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(MakeLayout(staging, final));

            CaptureRunLockAcquisitionCoordinator firstCoordinator =
                new CaptureRunLockAcquisitionCoordinator(CaptureRunLockOsBackend.Create());
            CaptureRunLockAcquisitionCoordinator secondCoordinator =
                new CaptureRunLockAcquisitionCoordinator(CaptureRunLockOsBackend.Create());

            bool first = firstCoordinator.TryAcquire(pathSet, out CaptureRunLockLease firstLease);
            Assert.That(first, Is.True);
            Assert.That(firstLease.IsCreated, Is.True);

            bool second = secondCoordinator.TryAcquire(pathSet, out CaptureRunLockLease secondLease);
            Assert.That(second, Is.False);
            Assert.That(secondLease, Is.Null);

            firstLease.Dispose();
        }

        [Test]
        public void OwnershipLease_ContentionUntilFullRelease_ThenReacquirable()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(MakeLayout(staging, final));

            CaptureRunLockAcquisitionCoordinator coordinator =
                new CaptureRunLockAcquisitionCoordinator(CaptureRunLockOsBackend.Create());
            bool acquired = coordinator.TryAcquire(pathSet, out CaptureRunLockLease lease);
            Assert.That(acquired, Is.True);

            CaptureRunInitializationSessionOwnershipLease owner =
                CaptureRunInitializationSessionOwnershipLease.Create(ref lease);
            Assert.That(owner.IsCreated, Is.True);

            CaptureRunLockOsBackend contender = CaptureRunLockOsBackend.Create();
            bool contended = contender.TryAcquire(pathSet.FirstLockPath, out ICaptureRunLockHandle contendedHandle);
            Assert.That(contended, Is.False);
            Assert.That(contendedHandle, Is.Null);

            owner.Dispose();
            Assert.That(owner.IsReleaseComplete, Is.True);

            bool reacquired = contender.TryAcquire(pathSet.FirstLockPath, out ICaptureRunLockHandle reacquiredHandle);
            Assert.That(reacquired, Is.True);
            reacquiredHandle.Dispose();
        }

        [Test]
        public void Coordinator_SecondLockPreHeld_FailsAndRollsBackFirst_FirstReacquirable()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(MakeLayout(staging, final));

            CaptureRunLockOsBackend preholdBackend = CaptureRunLockOsBackend.Create();
            bool preheld = preholdBackend.TryAcquire(pathSet.SecondLockPath, out ICaptureRunLockHandle secondPreheld);
            Assert.That(preheld, Is.True);

            CaptureRunLockAcquisitionCoordinator coordinator =
                new CaptureRunLockAcquisitionCoordinator(CaptureRunLockOsBackend.Create());
            bool acquired = coordinator.TryAcquire(pathSet, out CaptureRunLockLease lease);
            Assert.That(acquired, Is.False);
            Assert.That(lease, Is.Null);

            // Rollback released the first handle, so it must be re-acquirable.
            CaptureRunLockOsBackend verifyBackend = CaptureRunLockOsBackend.Create();
            bool reacquired = verifyBackend.TryAcquire(pathSet.FirstLockPath, out ICaptureRunLockHandle firstAgain);
            Assert.That(reacquired, Is.True);
            firstAgain.Dispose();

            secondPreheld.Dispose();
        }

        [Test]
        public void Acquire_LocksDirectoryJunction_RejectsWithoutOutsideCreation()
        {
            RequireWindows();
            (string sandbox, string staging, string final) = MakeSandbox();
            string outside = Path.Combine(sandbox, "outside");
            Directory.CreateDirectory(outside);

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(MakeLayout(staging, final));
            string locksPath = Path.Combine(staging, ".locks");
            CreateJunction(locksPath, outside);
            _junctions.Add(locksPath);

            CaptureRunLockOsBackend backend = CaptureRunLockOsBackend.Create();

            Assert.Throws<IOException>(() => backend.TryAcquire(pathSet.StagingLockPath, out _));
            Assert.That(Directory.GetFiles(outside), Is.Empty);
        }

        [Test]
        public void Acquire_BaseDirectoryJunction_RejectsWithoutOutsideCreation()
        {
            RequireWindows();
            (string sandbox, string staging, string final) = MakeSandbox();
            string outside = Path.Combine(sandbox, "outside");
            Directory.CreateDirectory(outside);

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(MakeLayout(staging, final));

            Directory.Delete(staging, true);
            CreateJunction(staging, outside);
            _junctions.Add(staging);

            CaptureRunLockOsBackend backend = CaptureRunLockOsBackend.Create();

            Assert.Throws<IOException>(() => backend.TryAcquire(pathSet.StagingLockPath, out _));
            Assert.That(Directory.GetFiles(outside), Is.Empty);
        }

        [Test]
        public void FailurePath_AfterRollback_SandboxDeletable_NoHandleLeak()
        {
            RequireWindows();
            (string sandbox, string staging, string final) = MakeSandbox();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(MakeLayout(staging, final));

            CaptureRunLockOsBackend preholdBackend = CaptureRunLockOsBackend.Create();
            bool preheld = preholdBackend.TryAcquire(pathSet.SecondLockPath, out ICaptureRunLockHandle secondPreheld);
            Assert.That(preheld, Is.True);

            CaptureRunLockAcquisitionCoordinator coordinator =
                new CaptureRunLockAcquisitionCoordinator(CaptureRunLockOsBackend.Create());
            bool acquired = coordinator.TryAcquire(pathSet, out CaptureRunLockLease lease);
            Assert.That(acquired, Is.False);
            Assert.That(lease, Is.Null);

            secondPreheld.Dispose();

            // If any base, .locks, or lock file handle leaked, the recursive
            // delete fails with a sharing violation.
            Directory.Delete(sandbox, true);
            Assert.That(Directory.Exists(sandbox), Is.False);
        }

        [Test]
        public void Handle_DoubleDispose_Safe()
        {
            RequireWindows();
            (_, string staging, string final) = MakeSandbox();
            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(MakeLayout(staging, final));
            CaptureRunLockOsBackend backend = CaptureRunLockOsBackend.Create();

            bool acquired = backend.TryAcquire(pathSet.FirstLockPath, out ICaptureRunLockHandle handle);
            Assert.That(acquired, Is.True);

            handle.Dispose();
            Assert.That(handle.IsCreated, Is.False);
            Assert.DoesNotThrow(() => handle.Dispose());
            Assert.That(handle.IsCreated, Is.False);
        }
    }
}
