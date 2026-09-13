using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    public class CaptureRunLockAcquisitionCoordinatorTests
    {
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

            public string Tag { get; set; }

            public int DisposeCount { get; private set; }

            public bool ThrowOnDispose { get; set; }

            public void Dispose()
            {
                DisposeCount++;
                _disposeLog?.Add(LockPath);
                if (ThrowOnDispose)
                {
                    throw new InvalidOperationException("Fake handle dispose failure" + (Tag == null ? string.Empty : ": " + Tag) + ".");
                }
            }
        }

        private sealed class AcquireResult
        {
            public bool Success;
            public ICaptureRunLockHandle Handle;
            public Exception Throw;
        }

        private sealed class FakeBackend : ICaptureRunLockBackend
        {
            public Func<string, AcquireResult> OnAcquire { get; set; }

            public List<string> AttemptedPaths { get; } = new List<string>();

            public bool TryAcquire(string absoluteLockPath, out ICaptureRunLockHandle handle)
            {
                AttemptedPaths.Add(absoluteLockPath);
                AcquireResult result = OnAcquire(absoluteLockPath);
                handle = result.Handle;
                if (result.Throw != null)
                {
                    throw result.Throw;
                }

                return result.Success;
            }
        }

        private static CaptureRunLockPathSet MakePathSet()
        {
            string staging = Path.DirectorySeparatorChar == '\\' ? "C:\\staging" : "/staging";
            string final = Path.DirectorySeparatorChar == '\\' ? "D:\\final" : "/final";
            return new CaptureRunLockPathSet(new CaptureRunRootLayout(staging, 1));
        }

        private static CaptureRunLockAcquisitionCoordinator MakeCoordinator(FakeBackend backend)
        {
            return new CaptureRunLockAcquisitionCoordinator(backend);
        }

        // ---- Type shape ----

        [Test]
        public void InterfacesAndTypeShape()
        {
            Type handleType = typeof(ICaptureRunLockHandle);
            Assert.That(handleType.IsInterface, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(handleType), Is.True);
            Assert.That(handleType.GetProperty("LockPath"), Is.Not.Null);
            Assert.That(handleType.GetProperty("IsCreated"), Is.Not.Null);

            Type backendType = typeof(ICaptureRunLockBackend);
            Assert.That(backendType.IsInterface, Is.True);
            Assert.That(backendType.GetMethod("TryAcquire"), Is.Not.Null);

            Type leaseType = typeof(CaptureRunLockLease);
            Assert.That(leaseType.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(leaseType), Is.True);

            Type coordinatorType = typeof(CaptureRunLockAcquisitionCoordinator);
            Assert.That(coordinatorType.IsSealed, Is.True);
            Assert.That(typeof(IDisposable).IsAssignableFrom(coordinatorType), Is.False);
        }

        [Test]
        public void Coordinator_NullBackend_Rejected()
        {
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => new CaptureRunLockAcquisitionCoordinator(null));
            Assert.That(ex.ParamName, Is.EqualTo("backend"));
        }

        [Test]
        public void Coordinator_HoldsSingleBackendField()
        {
            Type type = typeof(CaptureRunLockAcquisitionCoordinator);
            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(ICaptureRunLockBackend)));
            Assert.That(fields[0].IsInitOnly, Is.True);
        }

        // ---- TryAcquire flow ----

        [Test]
        public void TryAcquire_NullPathSet_LeaseNullAndBackendNotTouched()
        {
            FakeBackend backend = new FakeBackend { OnAcquire = _ => throw new InvalidOperationException("must not be called") };
            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            CaptureRunLockLease lease = null;
            ArgumentNullException ex = Assert.Throws<ArgumentNullException>(() => coordinator.TryAcquire(null, out lease));
            Assert.That(ex.ParamName, Is.EqualTo("pathSet"));
            Assert.That(lease, Is.Null);
            Assert.That(backend.AttemptedPaths, Is.Empty);
        }

        [Test]
        public void TryAcquire_AcquiresTheOneLockOnce()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle handle = new FakeHandle(pathSet.LockPath);

            FakeBackend backend = new FakeBackend();
            backend.OnAcquire = path =>
                path == pathSet.LockPath
                    ? new AcquireResult { Success = true, Handle = handle }
                    : throw new InvalidOperationException("unexpected path: " + path);

            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            bool acquired = coordinator.TryAcquire(pathSet, out CaptureRunLockLease lease);

            Assert.That(acquired, Is.True);
            Assert.That(lease, Is.Not.Null);
            Assert.That(backend.AttemptedPaths, Is.EqualTo(new[] { pathSet.LockPath }));
            Assert.That(handle.DisposeCount, Is.EqualTo(0));
        }

        // ---- Exceptions / rollback ----

        [Test]
        public void TryAcquire_FirstException_NotTransformed()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            IOException expected = new IOException("first backend failure");
            FakeBackend backend = new FakeBackend { OnAcquire = _ => new AcquireResult { Throw = expected } };
            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            IOException ex = Assert.Throws<IOException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(ex, Is.SameAs(expected));
        }

        [Test]
        public void TryAcquire_BackendTrueNullHandle_FailClosed()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeBackend backend = new FakeBackend { OnAcquire = _ => new AcquireResult { Success = true, Handle = null } };
            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.TryAcquire(pathSet, out _));
        }

        [Test]
        public void TryAcquire_BackendFalseNonNullHandle_FailClosedAndCollected()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle leaked = new FakeHandle(pathSet.LockPath);
            FakeBackend backend = new FakeBackend { OnAcquire = _ => new AcquireResult { Success = false, Handle = leaked } };
            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(leaked.DisposeCount, Is.EqualTo(1));
        }

        // ---- Lease construction failures ----

        [Test]
        public void TryAcquire_HandleNotCreated_Rejected()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle badFirst = new FakeHandle(pathSet.LockPath, isCreated: false);

            FakeBackend backend = new FakeBackend();
            backend.OnAcquire = _ => new AcquireResult { Success = true, Handle = badFirst };

            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(ex.ParamName, Is.EqualTo("handle"));
            Assert.That(badFirst.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void TryAcquire_HandlePathMismatch_Rejected()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle badFirst = new FakeHandle("C:\\wrong\\run-1.lock");

            FakeBackend backend = new FakeBackend();
            backend.OnAcquire = _ => new AcquireResult { Success = true, Handle = badFirst };

            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(ex.ParamName, Is.EqualTo("handle"));
            Assert.That(badFirst.DisposeCount, Is.EqualTo(1));
        }

        // ---- Lease disposal ----

        [Test]
        public void Lease_Dispose_IdempotentAfterFullSuccess()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle handle = new FakeHandle(pathSet.LockPath);

            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, handle);
            lease.Dispose();
            lease.Dispose();

            Assert.That(handle.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void Lease_Dispose_SetsIsCreatedFalse()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle handle = new FakeHandle(pathSet.LockPath);

            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, handle);
            Assert.That(lease.IsCreated, Is.True);

            lease.Dispose();
            Assert.That(lease.IsCreated, Is.False);
            Assert.That(lease.PathSet, Is.Not.Null);
        }

        [Test]
        public void Lease_NullAndInvalidHandles_Rejected()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle handle = new FakeHandle(pathSet.LockPath);

            Assert.That(() => new CaptureRunLockLease(null, handle), Throws.ArgumentNullException.With.Property("ParamName").EqualTo("pathSet"));
            Assert.That(() => new CaptureRunLockLease(pathSet, null), Throws.ArgumentNullException.With.Property("ParamName").EqualTo("handle"));
        }

        // ---- Source ----

        [Test]
        public void Production_NoFileDirectoryFileStreamSafeHandlePInvoke()
        {
            foreach (string relative in new[]
            {
                "Assets/Zantetsu/Runtime/Observability/ICaptureRunLockHandle.cs",
                "Assets/Zantetsu/Runtime/Observability/ICaptureRunLockBackend.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunLockLease.cs",
                "Assets/Zantetsu/Runtime/Observability/CaptureRunLockAcquisitionCoordinator.cs"
            })
            {
                string source = File.ReadAllText(LocateSource(relative));
                Assert.That(source, Does.Not.Contain("File."));
                Assert.That(source, Does.Not.Contain("Directory."));
                Assert.That(source, Does.Not.Contain("FileStream"));
                Assert.That(source, Does.Not.Contain("SafeHandle"));
                Assert.That(source, Does.Not.Contain("DllImport"));
                Assert.That(source, Does.Not.Contain("UnityEngine"));
                Assert.That(source, Does.Not.Contain("System.Linq"));
                Assert.That(source, Does.Not.Contain("Random"));
                Assert.That(source, Does.Not.Contain("DateTime"));
                Assert.That(source, Does.Not.Contain("Debug."));
            }
        }

        private static string LocateSource(string relativePath)
        {
            if (File.Exists(relativePath))
            {
                return relativePath;
            }

            string dir = Path.GetDirectoryName(typeof(CaptureRunLockAcquisitionCoordinatorTests).Assembly.Location);
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
