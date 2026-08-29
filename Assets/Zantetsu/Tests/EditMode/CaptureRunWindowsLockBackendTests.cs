using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunWindowsLockBackendTests
    {
        private string _tempRoot;
        private string _stagingBase;
        private string _finalBase;

        private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        [SetUp]
        public void SetUp()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "zantetsuken-lock-tests-" + Guid.NewGuid().ToString("N"));
            _stagingBase = Path.Combine(_tempRoot, "staging");
            _finalBase = Path.Combine(_tempRoot, "final");
            Directory.CreateDirectory(_stagingBase);
            Directory.CreateDirectory(_finalBase);
        }

        [TearDown]
        public void TearDown()
        {
            List<Exception> failures = new List<Exception>();
            try
            {
                TryDeleteDirectory(_tempRoot);
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }

            if (failures.Count > 0)
            {
                throw new AggregateException("Test cleanup failed.", failures);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            DirectoryInfo info = new DirectoryInfo(path);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                // junction/symlink: リンク自体を削除し、ターゲットには触れない。
                Directory.Delete(path, recursive: false);
                return;
            }

            foreach (string child in Directory.GetDirectories(path))
            {
                TryDeleteDirectory(child);
            }

            foreach (string file in Directory.GetFiles(path))
            {
                File.Delete(file);
            }

            Directory.Delete(path, recursive: false);
        }

        private static string LockPath(string baseDirectory, long testRunId)
        {
            return Path.Combine(baseDirectory, ".locks", "run-" + testRunId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".lock");
        }

        // ---- Path validation ----

        [Test]
        public void TryAcquire_NullEmptyRelative_Rejected()
        {
            CaptureRunWindowsLockBackend backend = new CaptureRunWindowsLockBackend();
            ICaptureRunLockHandle handle;

            Assert.Throws<ArgumentNullException>(() => backend.TryAcquire(null, out handle));
            Assert.Throws<ArgumentException>(() => backend.TryAcquire(string.Empty, out handle));
            Assert.Throws<ArgumentException>(() => backend.TryAcquire("   ", out handle));
            Assert.Throws<ArgumentException>(() => backend.TryAcquire("relative.lock", out handle));
        }

        [Test]
        public void TryAcquire_UncAndDevice_Rejected()
        {
            if (!IsWindows)
            {
                Assert.Ignore("Windows-specific path forms.");
                return;
            }

            CaptureRunWindowsLockBackend backend = new CaptureRunWindowsLockBackend();
            ICaptureRunLockHandle handle;

            Assert.Throws<ArgumentException>(() => backend.TryAcquire("\\\\server\\share\\run-1.lock", out handle));
            Assert.Throws<ArgumentException>(() => backend.TryAcquire("\\\\?\\C:\\device\\run-1.lock", out handle));
        }

        [Test]
        public void TryAcquire_NotFixedLockPath_Rejected()
        {
            CaptureRunWindowsLockBackend backend = new CaptureRunWindowsLockBackend();
            ICaptureRunLockHandle handle;

            // 正の TestRunId 形式でない、または .locks 直下でない。
            Assert.Throws<ArgumentException>(() => backend.TryAcquire(Path.Combine(_stagingBase, "run-1.lock"), out handle));
            Assert.Throws<ArgumentException>(() => backend.TryAcquire(Path.Combine(_stagingBase, ".locks", "run-0.lock"), out handle));
            Assert.Throws<ArgumentException>(() => backend.TryAcquire(Path.Combine(_stagingBase, ".locks", "run-01.lock"), out handle));
            Assert.Throws<ArgumentException>(() => backend.TryAcquire(Path.Combine(_stagingBase, ".locks", "other.lock"), out handle));
        }

        // ---- Acquisition ----

        [Test]
        public void TryAcquire_CreatesLockFileAndSucceeds()
        {
            CaptureRunWindowsLockBackend backend = new CaptureRunWindowsLockBackend();
            string path = LockPath(_stagingBase, 1);

            bool acquired = backend.TryAcquire(path, out ICaptureRunLockHandle handle);

            Assert.That(acquired, Is.True);
            Assert.That(handle, Is.Not.Null);
            Assert.That(File.Exists(path), Is.True);
            handle.Dispose();
        }

        [Test]
        public void TryAcquire_SamePathWhileHeld_ReturnsFalseNull()
        {
            CaptureRunWindowsLockBackend backend = new CaptureRunWindowsLockBackend();
            string path = LockPath(_stagingBase, 1);

            Assert.That(backend.TryAcquire(path, out ICaptureRunLockHandle first), Is.True);

            bool second = backend.TryAcquire(path, out ICaptureRunLockHandle secondHandle);
            Assert.That(second, Is.False);
            Assert.That(secondHandle, Is.Null);

            first.Dispose();
        }

        [Test]
        public void TryAcquire_AfterDispose_ReacquireSameFile()
        {
            CaptureRunWindowsLockBackend backend = new CaptureRunWindowsLockBackend();
            string path = LockPath(_stagingBase, 1);

            Assert.That(backend.TryAcquire(path, out ICaptureRunLockHandle first), Is.True);
            first.Dispose();

            // lock file は残るが、未保持なら再取得可能。
            Assert.That(File.Exists(path), Is.True);
            Assert.That(backend.TryAcquire(path, out ICaptureRunLockHandle second), Is.True);
            second.Dispose();
        }

        [Test]
        public void Handle_LockPathIsCreatedAndIdempotentDispose()
        {
            CaptureRunWindowsLockBackend backend = new CaptureRunWindowsLockBackend();
            string path = LockPath(_stagingBase, 1);

            Assert.That(backend.TryAcquire(path, out ICaptureRunLockHandle handle), Is.True);
            Assert.That(handle.LockPath, Is.EqualTo(Path.GetFullPath(path)));
            Assert.That(handle.IsCreated, Is.True);

            handle.Dispose();
            Assert.That(handle.IsCreated, Is.False);

            Assert.DoesNotThrow(() => handle.Dispose());
        }

        [Test]
        public void Handle_ConstructorValidation()
        {
            using (SafeFileHandle valid = new SafeFileHandle(new IntPtr(1), true))
            {
                Assert.Throws<ArgumentNullException>(() => new CaptureRunWindowsLockHandle(null, valid));
            }

            Assert.Throws<ArgumentNullException>(() => new CaptureRunWindowsLockHandle("C:\\x", null));

            using (SafeFileHandle invalid = new SafeFileHandle(new IntPtr(-1), false))
            {
                ArgumentException invalidEx = Assert.Throws<ArgumentException>(() => new CaptureRunWindowsLockHandle("C:\\x", invalid));
                Assert.That(invalidEx.ParamName, Is.EqualTo("handle"));
            }

            SafeFileHandle closed = new SafeFileHandle(new IntPtr(1), true);
            closed.Dispose();
            ArgumentException closedEx = Assert.Throws<ArgumentException>(() => new CaptureRunWindowsLockHandle("C:\\x", closed));
            Assert.That(closedEx.ParamName, Is.EqualTo("handle"));
        }

        [Test]
        public void TwoCoordinators_MutualExclusion()
        {
            CaptureRunWindowsLockBackend backend1 = new CaptureRunWindowsLockBackend();
            CaptureRunWindowsLockBackend backend2 = new CaptureRunWindowsLockBackend();
            CaptureRunLockAcquisitionCoordinator coordinator1 = new CaptureRunLockAcquisitionCoordinator(backend1);
            CaptureRunLockAcquisitionCoordinator coordinator2 = new CaptureRunLockAcquisitionCoordinator(backend2);

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(new CaptureRunRootLayout(_stagingBase, _finalBase, 1));

            Assert.That(coordinator1.TryAcquire(pathSet, out CaptureRunLockLease lease1), Is.True);
            Assert.That(coordinator2.TryAcquire(pathSet, out CaptureRunLockLease lease2), Is.False);
            Assert.That(lease2, Is.Null);

            lease1.Dispose();
            Assert.That(coordinator2.TryAcquire(pathSet, out CaptureRunLockLease lease3), Is.True);
            lease3.Dispose();
        }

        [Test]
        public void SecondLockContention_RollsBackFirst()
        {
            CaptureRunWindowsLockBackend backend = new CaptureRunWindowsLockBackend();
            CaptureRunWindowsLockBackend blocker = new CaptureRunWindowsLockBackend();
            CaptureRunLockAcquisitionCoordinator coordinator = new CaptureRunLockAcquisitionCoordinator(backend);

            CaptureRunLockPathSet pathSet = new CaptureRunLockPathSet(new CaptureRunRootLayout(_stagingBase, _finalBase, 1));

            Assert.That(blocker.TryAcquire(pathSet.SecondLockPath, out ICaptureRunLockHandle blockingHandle), Is.True);

            Assert.That(coordinator.TryAcquire(pathSet, out CaptureRunLockLease lease), Is.False);
            Assert.That(lease, Is.Null);

            // first は rollback 済みなので、直後に再取得可能。
            Assert.That(backend.TryAcquire(pathSet.FirstLockPath, out ICaptureRunLockHandle firstHandle), Is.True);
            firstHandle.Dispose();
            blockingHandle.Dispose();
        }

        // ---- Error contracts ----

        [Test]
        public void TryAcquire_MissingBase_ThrowsNotFalse()
        {
            CaptureRunWindowsLockBackend backend = new CaptureRunWindowsLockBackend();
            string missing = Path.Combine(_tempRoot, "missing");
            string path = LockPath(missing, 1);

            ICaptureRunLockHandle handle = null;
            Assert.Throws<IOException>(() => backend.TryAcquire(path, out handle));
            Assert.That(handle, Is.Null);
        }

        [Test]
        public void TryAcquire_LocksNotDirectory_Rejected()
        {
            CaptureRunWindowsLockBackend backend = new CaptureRunWindowsLockBackend();
            string locksPath = Path.Combine(_stagingBase, ".locks");
            File.WriteAllText(locksPath, "not a directory");
            string path = LockPath(_stagingBase, 1);

            ICaptureRunLockHandle handle = null;
            Assert.Throws<IOException>(() => backend.TryAcquire(path, out handle));
            Assert.That(handle, Is.Null);
        }

        // ---- Reparse points (environment-dependent) ----

        [Test]
        public void TryAcquire_BaseIsReparsePoint_Rejected()
        {
            if (!IsWindows)
            {
                Assert.Ignore("Windows-specific reparse point test.");
                return;
            }

            string target = Path.Combine(_tempRoot, "base-target");
            Directory.CreateDirectory(target);
            string junction = Path.Combine(_tempRoot, "base-junction");
            if (!TryCreateJunction(junction, target))
            {
                Assert.Ignore("Unable to create a junction in this environment.");
                return;
            }

            CaptureRunWindowsLockBackend backend = new CaptureRunWindowsLockBackend();
            ICaptureRunLockHandle handle;
            Assert.Throws<IOException>(() => backend.TryAcquire(LockPath(junction, 1), out handle));
        }

        [Test]
        public void TryAcquire_LocksIsReparsePoint_Rejected()
        {
            if (!IsWindows)
            {
                Assert.Ignore("Windows-specific reparse point test.");
                return;
            }

            string target = Path.Combine(_tempRoot, "locks-target");
            Directory.CreateDirectory(target);
            string junction = Path.Combine(_stagingBase, ".locks");
            if (!TryCreateJunction(junction, target))
            {
                Assert.Ignore("Unable to create a junction in this environment.");
                return;
            }

            CaptureRunWindowsLockBackend backend = new CaptureRunWindowsLockBackend();
            ICaptureRunLockHandle handle;
            Assert.Throws<IOException>(() => backend.TryAcquire(LockPath(_stagingBase, 1), out handle));
        }

        [Test]
        public void TryAcquire_LockFileIsReparsePoint_Rejected()
        {
            if (!IsWindows)
            {
                Assert.Ignore("Windows-specific reparse point test.");
                return;
            }

            string target = Path.Combine(_tempRoot, "lock-target");
            File.WriteAllText(target, "content");
            string link = LockPath(_stagingBase, 1);
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            if (!TryCreateFileSymlink(link, target))
            {
                Assert.Ignore("Unable to create a file symlink in this environment.");
                return;
            }

            CaptureRunWindowsLockBackend backend = new CaptureRunWindowsLockBackend();
            ICaptureRunLockHandle handle;
            Assert.Throws<IOException>(() => backend.TryAcquire(link, out handle));
        }

        // ---- Shape / P/Invoke ----

        [Test]
        public void Backend_HasNoFieldsNotDisposableNoMutableStatic()
        {
            Type type = typeof(CaptureRunWindowsLockBackend);

            Assert.That(type.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance), Is.Empty, "Backend must have no instance fields.");

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsLiteral, Is.True, field.Name + " must be const.");
            }
        }

        [Test]
        public void PInvoke_Attributes()
        {
            Type type = typeof(CaptureRunWindowsLockBackend);

            MethodInfo createFile = type.GetMethod("CreateFileW", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(createFile, Is.Not.Null);
            DllImportAttribute createAttr = createFile.GetCustomAttribute<DllImportAttribute>();
            Assert.That(createAttr.Value, Is.EqualTo("kernel32.dll"));
            Assert.That(createAttr.SetLastError, Is.True);
            Assert.That(createAttr.CharSet, Is.EqualTo(CharSet.Unicode));

            MethodInfo getInfo = type.GetMethod("GetFileInformationByHandle", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(getInfo, Is.Not.Null);
            Assert.That(getInfo.GetCustomAttribute<DllImportAttribute>().SetLastError, Is.True);

            MethodInfo getFinal = type.GetMethod("GetFinalPathNameByHandleW", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(getFinal, Is.Not.Null);
            DllImportAttribute finalAttr = getFinal.GetCustomAttribute<DllImportAttribute>();
            Assert.That(finalAttr.Value, Is.EqualTo("kernel32.dll"));
            Assert.That(finalAttr.CharSet, Is.EqualTo(CharSet.Unicode));
            Assert.That(finalAttr.SetLastError, Is.True);
        }

        [Test]
        public void Backend_DoesNotTouchRunRootOrFilesystemApis()
        {
            string source = File.ReadAllText(LocateSource("Assets/Zantetsu/Runtime/Observability/CaptureRunWindowsLockBackend.cs"));

            Assert.That(source, Does.Not.Contain("runs"));
            Assert.That(source, Does.Not.Contain("FileStream"));
            Assert.That(source, Does.Not.Contain("UnityEngine"));
            Assert.That(source, Does.Not.Contain("System.Linq"));
            Assert.That(source, Does.Not.Contain("Random"));
            Assert.That(source, Does.Not.Contain("DateTime"));
            Assert.That(source, Does.Not.Contain("Debug."));
            Assert.That(source, Does.Not.Contain("Thread."));
        }

        private static bool TryCreateJunction(string junctionPath, string targetPath)
        {
            return RunMklink("/c mklink /J \"" + junctionPath + "\" \"" + targetPath + "\"", junctionPath);
        }

        private static bool TryCreateFileSymlink(string linkPath, string targetPath)
        {
            return RunMklink("/c mklink \"" + linkPath + "\" \"" + targetPath + "\"", linkPath);
        }

        private static bool RunMklink(string arguments, string expectedPath)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process process = Process.Start(psi))
                {
                    process.WaitForExit();
                    return process.ExitCode == 0 && (File.Exists(expectedPath) || Directory.Exists(expectedPath));
                }
            }
            catch
            {
                return false;
            }
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunWindowsLockBackendTests).Assembly.Location);
            while (dir != null)
            {
                string candidate = Path.Combine(dir, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                DirectoryInfo parent = Directory.GetParent(dir);
                if (parent == null)
                {
                    break;
                }

                dir = parent.FullName;
            }

            Assert.Fail("Source file not found: " + relativePath);
            return null;
        }
    }
}
