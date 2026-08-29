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
            return new CaptureRunLockPathSet(new CaptureRunRootLayout(staging, final, 1));
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
        public void TryAcquire_AcquiresInExactOrderOnceEach()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath);

            FakeBackend backend = new FakeBackend();
            backend.OnAcquire = path =>
                path == pathSet.FirstLockPath
                    ? new AcquireResult { Success = true, Handle = first }
                    : path == pathSet.SecondLockPath
                        ? new AcquireResult { Success = true, Handle = second }
                        : throw new InvalidOperationException("unexpected path: " + path);

            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            bool acquired = coordinator.TryAcquire(pathSet, out CaptureRunLockLease lease);

            Assert.That(acquired, Is.True);
            Assert.That(lease, Is.Not.Null);
            Assert.That(backend.AttemptedPaths, Is.EqualTo(new[] { pathSet.FirstLockPath, pathSet.SecondLockPath }));
            Assert.That(first.DisposeCount, Is.EqualTo(0));
            Assert.That(second.DisposeCount, Is.EqualTo(0));
        }

        [Test]
        public void TryAcquire_FirstFalse_SecondNotTouched()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeBackend backend = new FakeBackend { OnAcquire = _ => new AcquireResult { Success = false, Handle = null } };
            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            bool acquired = coordinator.TryAcquire(pathSet, out CaptureRunLockLease lease);

            Assert.That(acquired, Is.False);
            Assert.That(lease, Is.Null);
            Assert.That(backend.AttemptedPaths, Is.EqualTo(new[] { pathSet.FirstLockPath }));
        }

        [Test]
        public void TryAcquire_SecondFalse_FirstDisposedOnce()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath);

            FakeBackend backend = new FakeBackend();
            backend.OnAcquire = path =>
                path == pathSet.FirstLockPath
                    ? new AcquireResult { Success = true, Handle = first }
                    : new AcquireResult { Success = false, Handle = null };

            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            bool acquired = coordinator.TryAcquire(pathSet, out CaptureRunLockLease lease);

            Assert.That(acquired, Is.False);
            Assert.That(lease, Is.Null);
            Assert.That(first.DisposeCount, Is.EqualTo(1));
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
        public void TryAcquire_SecondException_FirstDisposed()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath);

            FakeBackend backend = new FakeBackend();
            backend.OnAcquire = path =>
                path == pathSet.FirstLockPath
                    ? new AcquireResult { Success = true, Handle = first }
                    : new AcquireResult { Throw = new IOException("second backend failure") };

            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            Assert.Throws<IOException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(first.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void TryAcquire_SecondException_CleanupFails_AggregateOrder()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath) { ThrowOnDispose = true, Tag = "first" };
            IOException secondException = new IOException("second backend failure");

            FakeBackend backend = new FakeBackend();
            backend.OnAcquire = path =>
                path == pathSet.FirstLockPath
                    ? new AcquireResult { Success = true, Handle = first }
                    : new AcquireResult { Throw = secondException };

            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            AggregateException agg = Assert.Throws<AggregateException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(agg.InnerExceptions.Count, Is.EqualTo(2));
            Assert.That(agg.InnerExceptions[0], Is.SameAs(secondException));
            Assert.That(agg.InnerExceptions[1].Message, Does.Contain("first"));
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
            FakeHandle leaked = new FakeHandle(pathSet.FirstLockPath);
            FakeBackend backend = new FakeBackend { OnAcquire = _ => new AcquireResult { Success = false, Handle = leaked } };
            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(leaked.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void TryAcquire_FirstFalseNonNull_CleanupFails_Aggregate()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle leaked = new FakeHandle(pathSet.FirstLockPath) { ThrowOnDispose = true, Tag = "first" };
            FakeBackend backend = new FakeBackend { OnAcquire = _ => new AcquireResult { Success = false, Handle = leaked } };
            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            AggregateException agg = Assert.Throws<AggregateException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(agg.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(agg.InnerExceptions[1].Message, Does.Contain("first"));
            Assert.That(leaked.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void TryAcquire_SecondFalseSameReference_DisposedOnce()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle shared = new FakeHandle(pathSet.FirstLockPath);

            FakeBackend backend = new FakeBackend();
            backend.OnAcquire = path =>
                path == pathSet.FirstLockPath
                    ? new AcquireResult { Success = true, Handle = shared }
                    : new AcquireResult { Success = false, Handle = shared };

            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(shared.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void TryAcquire_SecondFalseSameReference_DisposeFails_AggregatedOnce()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle shared = new FakeHandle(pathSet.FirstLockPath) { ThrowOnDispose = true, Tag = "shared" };

            FakeBackend backend = new FakeBackend();
            backend.OnAcquire = path =>
                path == pathSet.FirstLockPath
                    ? new AcquireResult { Success = true, Handle = shared }
                    : new AcquireResult { Success = false, Handle = shared };

            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            AggregateException agg = Assert.Throws<AggregateException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(agg.InnerExceptions.Count, Is.EqualTo(2));
            Assert.That(agg.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(agg.InnerExceptions[1].Message, Does.Contain("shared"));
            Assert.That(shared.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void TryAcquire_SecondTrueNullHandle_FailClosed()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath);

            FakeBackend backend = new FakeBackend();
            backend.OnAcquire = path =>
                path == pathSet.FirstLockPath
                    ? new AcquireResult { Success = true, Handle = first }
                    : new AcquireResult { Success = true, Handle = null };

            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            Assert.Throws<InvalidOperationException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(first.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void TryAcquire_SecondFalseNonNull_CleanupFails_AllHandlesCollected()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath) { ThrowOnDispose = true, Tag = "second" };

            FakeBackend backend = new FakeBackend();
            backend.OnAcquire = path =>
                path == pathSet.FirstLockPath
                    ? new AcquireResult { Success = true, Handle = first }
                    : new AcquireResult { Success = false, Handle = second };

            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            AggregateException agg = Assert.Throws<AggregateException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(agg.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(agg.InnerExceptions[1].Message, Does.Contain("second"));
            Assert.That(first.DisposeCount, Is.EqualTo(1));
            Assert.That(second.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void TryAcquire_SecondTrueNull_CleanupFails_Aggregate()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath) { ThrowOnDispose = true, Tag = "first" };

            FakeBackend backend = new FakeBackend();
            backend.OnAcquire = path =>
                path == pathSet.FirstLockPath
                    ? new AcquireResult { Success = true, Handle = first }
                    : new AcquireResult { Success = true, Handle = null };

            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            AggregateException agg = Assert.Throws<AggregateException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(agg.InnerExceptions[0], Is.TypeOf<InvalidOperationException>());
            Assert.That(agg.InnerExceptions[1].Message, Does.Contain("first"));
            Assert.That(first.DisposeCount, Is.EqualTo(1));
        }

        // ---- Lease construction failures ----

        [Test]
        public void TryAcquire_HandleNotCreated_Rejected()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle badFirst = new FakeHandle(pathSet.FirstLockPath, isCreated: false);
            FakeHandle goodSecond = new FakeHandle(pathSet.SecondLockPath);

            FakeBackend backend = new FakeBackend();
            backend.OnAcquire = path =>
                path == pathSet.FirstLockPath
                    ? new AcquireResult { Success = true, Handle = badFirst }
                    : new AcquireResult { Success = true, Handle = goodSecond };

            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(ex.ParamName, Is.EqualTo("firstHandle"));
            Assert.That(goodSecond.DisposeCount, Is.EqualTo(1));
            Assert.That(badFirst.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void TryAcquire_HandlePathMismatch_Rejected()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle badFirst = new FakeHandle("C:\\wrong\\run-1.lock");
            FakeHandle goodSecond = new FakeHandle(pathSet.SecondLockPath);

            FakeBackend backend = new FakeBackend();
            backend.OnAcquire = path =>
                path == pathSet.FirstLockPath
                    ? new AcquireResult { Success = true, Handle = badFirst }
                    : new AcquireResult { Success = true, Handle = goodSecond };

            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(ex.ParamName, Is.EqualTo("firstHandle"));
            Assert.That(goodSecond.DisposeCount, Is.EqualTo(1));
            Assert.That(badFirst.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void TryAcquire_SameHandleReference_Rejected()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle shared = new FakeHandle(pathSet.FirstLockPath);

            FakeBackend backend = new FakeBackend { OnAcquire = _ => new AcquireResult { Success = true, Handle = shared } };
            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            ArgumentException ex = Assert.Throws<ArgumentException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(ex.ParamName, Is.EqualTo("secondHandle"));
            Assert.That(shared.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void TryAcquire_SameHandleReference_DisposeFails_AggregatedOnce()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle shared = new FakeHandle(pathSet.FirstLockPath) { ThrowOnDispose = true, Tag = "shared" };

            FakeBackend backend = new FakeBackend { OnAcquire = _ => new AcquireResult { Success = true, Handle = shared } };
            CaptureRunLockAcquisitionCoordinator coordinator = MakeCoordinator(backend);

            AggregateException agg = Assert.Throws<AggregateException>(() => coordinator.TryAcquire(pathSet, out _));
            Assert.That(agg.InnerExceptions.Count, Is.EqualTo(2));
            Assert.That(agg.InnerExceptions[0], Is.TypeOf<ArgumentException>());
            Assert.That(agg.InnerExceptions[1].Message, Does.Contain("shared"));
            Assert.That(shared.DisposeCount, Is.EqualTo(1));
        }

        // ---- Lease disposal ----

        [Test]
        public void Lease_Dispose_SecondThenFirst()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            List<string> log = new List<string>();
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath, disposeLog: log);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath, disposeLog: log);

            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);
            lease.Dispose();

            Assert.That(log, Is.EqualTo(new[] { pathSet.SecondLockPath, pathSet.FirstLockPath }));
        }

        [Test]
        public void Lease_Dispose_OneFailureStillTriesOther()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath) { ThrowOnDispose = true, Tag = "first" };
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath);

            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);

            AggregateException agg = Assert.Throws<AggregateException>(() => lease.Dispose());
            Assert.That(agg.InnerExceptions.Count, Is.EqualTo(1));
            Assert.That(second.DisposeCount, Is.EqualTo(1));
            Assert.That(first.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void Lease_Dispose_RetriesOnlyFailed()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath) { ThrowOnDispose = true, Tag = "first" };
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath);

            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);
            Assert.Throws<AggregateException>(() => lease.Dispose());

            first.ThrowOnDispose = false;
            Assert.DoesNotThrow(() => lease.Dispose());

            Assert.That(second.DisposeCount, Is.EqualTo(1));
            Assert.That(first.DisposeCount, Is.EqualTo(2));
        }

        [Test]
        public void Lease_Dispose_IdempotentAfterFullSuccess()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath);

            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);
            lease.Dispose();
            lease.Dispose();

            Assert.That(first.DisposeCount, Is.EqualTo(1));
            Assert.That(second.DisposeCount, Is.EqualTo(1));
        }

        [Test]
        public void Lease_Dispose_MultipleFailures_AggregateOrder()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath) { ThrowOnDispose = true, Tag = "first" };
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath) { ThrowOnDispose = true, Tag = "second" };

            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);

            AggregateException agg = Assert.Throws<AggregateException>(() => lease.Dispose());
            Assert.That(agg.InnerExceptions.Count, Is.EqualTo(2));
            Assert.That(agg.InnerExceptions[0].Message, Does.Contain("second"));
            Assert.That(agg.InnerExceptions[1].Message, Does.Contain("first"));
        }

        [Test]
        public void Lease_Dispose_SetsIsCreatedFalse()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath);

            CaptureRunLockLease lease = new CaptureRunLockLease(pathSet, first, second);
            Assert.That(lease.IsCreated, Is.True);

            lease.Dispose();
            Assert.That(lease.IsCreated, Is.False);
            Assert.That(lease.PathSet, Is.Not.Null);
        }

        [Test]
        public void Lease_NullAndInvalidHandles_Rejected()
        {
            CaptureRunLockPathSet pathSet = MakePathSet();
            FakeHandle first = new FakeHandle(pathSet.FirstLockPath);
            FakeHandle second = new FakeHandle(pathSet.SecondLockPath);

            Assert.That(() => new CaptureRunLockLease(null, first, second), Throws.ArgumentNullException.With.Property("ParamName").EqualTo("pathSet"));
            Assert.That(() => new CaptureRunLockLease(pathSet, null, second), Throws.ArgumentNullException.With.Property("ParamName").EqualTo("firstHandle"));
            Assert.That(() => new CaptureRunLockLease(pathSet, first, null), Throws.ArgumentNullException.With.Property("ParamName").EqualTo("secondHandle"));
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
