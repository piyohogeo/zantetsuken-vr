using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 fixed-capacity GPU Conversion Sync
    /// credit pool. No real GPU, fence, query, command buffer, NVENC, or native
    /// resource is used.
    /// </summary>
    public class NvencGpuConversionSyncPoolContractTests
    {
        [Test]
        public void Capacity_IsEight()
        {
            NvencGpuConversionSyncPool pool = new NvencGpuConversionSyncPool(new NvencCaptureProcessState());

            Assert.That(NvencBringUpProfileV1.GpuConversionSyncCapacity, Is.EqualTo(8));
            Assert.That(pool.Capacity, Is.EqualTo(8));
            Assert.That(pool.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void RentEightThenNinthFailsWithDefaultLease()
        {
            NvencGpuConversionSyncPool pool = new NvencGpuConversionSyncPool(new NvencCaptureProcessState());

            for (int i = 0; i < 8; i++)
            {
                Assert.That(pool.TryRent(out NvencGpuConversionSyncLease lease), Is.True);
                Assert.That(lease.IsValid, Is.True);
            }

            Assert.That(pool.OccupiedCount, Is.EqualTo(8));
            Assert.That(pool.TryRent(out NvencGpuConversionSyncLease ninth), Is.False);
            Assert.That(ninth.IsValid, Is.False);
            Assert.That(ninth.SlotIndex, Is.EqualTo(0));
            Assert.That(ninth.Generation, Is.EqualTo(0));
        }

        [Test]
        public void ReturnThenRent_ReusesIndexWithNewGeneration()
        {
            NvencGpuConversionSyncPool pool = new NvencGpuConversionSyncPool(new NvencCaptureProcessState());

            Assert.That(pool.TryRent(out NvencGpuConversionSyncLease first), Is.True);
            int firstIndex = first.SlotIndex;
            long firstGeneration = first.Generation;

            Assert.That(pool.TryReturn(first), Is.True);
            Assert.That(pool.OccupiedCount, Is.EqualTo(0));

            Assert.That(pool.TryRent(out NvencGpuConversionSyncLease second), Is.True);
            Assert.That(second.SlotIndex, Is.EqualTo(firstIndex));
            Assert.That(second.Generation, Is.EqualTo(firstGeneration + 1));
        }

        [Test]
        public void StaleLeaseReturn_DoesNotReleaseCurrentReservation()
        {
            NvencGpuConversionSyncPool pool = new NvencGpuConversionSyncPool(new NvencCaptureProcessState());

            Assert.That(pool.TryRent(out NvencGpuConversionSyncLease first), Is.True);
            Assert.That(pool.TryReturn(first), Is.True);
            Assert.That(pool.TryRent(out NvencGpuConversionSyncLease second), Is.True);
            Assert.That(pool.OccupiedCount, Is.EqualTo(1));

            Assert.That(pool.TryReturn(first), Is.False);
            Assert.That(pool.OccupiedCount, Is.EqualTo(1));
            Assert.That(pool.IsActive(second), Is.True);
            Assert.That(pool.IsActive(first), Is.False);
        }

        [Test]
        public void ForeignPoolLease_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencGpuConversionSyncPool a = new NvencGpuConversionSyncPool(state);
            NvencGpuConversionSyncPool b = new NvencGpuConversionSyncPool(state);

            Assert.That(a.TryRent(out NvencGpuConversionSyncLease lease), Is.True);

            Assert.That(b.TryReturn(lease), Is.False);
            Assert.That(b.IsActive(lease), Is.False);
            Assert.That(a.OccupiedCount, Is.EqualTo(1));
            Assert.That(b.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void DefaultLease_Rejected()
        {
            NvencGpuConversionSyncPool pool = new NvencGpuConversionSyncPool(new NvencCaptureProcessState());
            NvencGpuConversionSyncLease empty = default;

            Assert.That(pool.TryReturn(empty), Is.False);
            Assert.That(pool.IsActive(empty), Is.False);
            Assert.That(pool.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void DoubleReturn_Rejected()
        {
            NvencGpuConversionSyncPool pool = new NvencGpuConversionSyncPool(new NvencCaptureProcessState());

            Assert.That(pool.TryRent(out NvencGpuConversionSyncLease lease), Is.True);
            Assert.That(pool.TryReturn(lease), Is.True);
            Assert.That(pool.TryReturn(lease), Is.False);
            Assert.That(pool.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void Draining_BlocksRent_ButAllowsReturnOfActiveLease()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencGpuConversionSyncPool pool = new NvencGpuConversionSyncPool(state);

            Assert.That(pool.TryRent(out NvencGpuConversionSyncLease lease), Is.True);
            Assert.That(state.TryBeginDrain(), Is.True);

            Assert.That(pool.TryRent(out NvencGpuConversionSyncLease afterDrain), Is.False);
            Assert.That(afterDrain.IsValid, Is.False);

            Assert.That(pool.TryReturn(lease), Is.True);
            Assert.That(pool.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void Poison_BlocksRentAndReturn_KeepsCreditsOccupied()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencGpuConversionSyncPool pool = new NvencGpuConversionSyncPool(state);

            Assert.That(pool.TryRent(out NvencGpuConversionSyncLease lease), Is.True);
            Assert.That(state.TryPoison(), Is.True);

            Assert.That(pool.TryRent(out NvencGpuConversionSyncLease afterPoison), Is.False);
            Assert.That(pool.TryReturn(lease), Is.False);

            Assert.That(pool.OccupiedCount, Is.EqualTo(1));
            Assert.That(pool.IsActive(lease), Is.True);
        }

        [Test]
        public void GenerationOverflow_RefusesReuseWithoutWrap()
        {
            NvencGpuConversionSyncPool pool = new NvencGpuConversionSyncPool(new NvencCaptureProcessState());
            long[] generations = ReadGenerations(pool);

            Assert.That(pool.TryRent(out NvencGpuConversionSyncLease first), Is.True);
            int index = first.SlotIndex;
            Assert.That(pool.TryReturn(first), Is.True);

            generations[index] = long.MaxValue;

            Assert.That(pool.TryRent(out NvencGpuConversionSyncLease atMax), Is.True);
            Assert.That(atMax.SlotIndex, Is.EqualTo(index));
            Assert.That(atMax.Generation, Is.EqualTo(long.MaxValue));

            Assert.That(pool.TryReturn(atMax), Is.True);
            Assert.That(pool.OccupiedCount, Is.EqualTo(0));
            Assert.That(generations[index], Is.EqualTo(long.MaxValue));

            for (int i = 0; i < 7; i++)
            {
                Assert.That(pool.TryRent(out NvencGpuConversionSyncLease lease), Is.True);
                Assert.That(lease.SlotIndex, Is.Not.EqualTo(index));
            }

            Assert.That(pool.TryRent(out NvencGpuConversionSyncLease exhausted), Is.False);
        }

        [Test]
        public void RentReturnPaths_DoNotAllocateArraysOrCollections()
        {
            string source = File.ReadAllText(Path.Combine(RuntimeDirectory(), "NvencGpuConversionSyncPool.cs"));

            string[] bodies =
            {
                ExtractMethodBody(source, "TryRent"),
                ExtractMethodBody(source, "TryReturn"),
            };

            string[] forbidden =
            {
                "new long[", "new bool[", "new int[", "new byte[", "new []",
                "new List", "new Dictionary", "new Stack", "new Queue", "new HashSet",
                "Array.", "ArrayPool", ".ToArray(", ".ToList(", "Enumerable.", "Allocate",
            };

            foreach (string body in bodies)
            {
                foreach (string word in forbidden)
                {
                    Assert.That(body, Does.Not.Contain(word), "Rent/return path must not allocate: " + word);
                }
            }
        }

        [Test]
        public void ProductionSources_NoLockNoWaitNoThreadNoTaskNoIoNoUnityNoNative()
        {
            string directory = RuntimeDirectory();

            string text =
                File.ReadAllText(Path.Combine(directory, "NvencGpuConversionSyncPool.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencGpuConversionSyncLease.cs"));

            Assert.That(text, Does.Not.Contain("lock ("));
            Assert.That(text, Does.Not.Contain("Monitor"));
            Assert.That(text, Does.Not.Contain("ManualResetEvent"));
            Assert.That(text, Does.Not.Contain("AutoResetEvent"));
            Assert.That(text, Does.Not.Contain("WaitHandle"));
            Assert.That(text, Does.Not.Contain("SpinWait"));
            Assert.That(text, Does.Not.Contain("new Thread"));
            Assert.That(text, Does.Not.Contain("ThreadPool"));
            Assert.That(text, Does.Not.Contain("Task"));
            Assert.That(text, Does.Not.Contain("File."));
            Assert.That(text, Does.Not.Contain("Directory."));
            Assert.That(text, Does.Not.Contain("FileStream"));
            Assert.That(text, Does.Not.Contain("DllImport"));
            Assert.That(text, Does.Not.Contain("UnityEngine"));
            Assert.That(text, Does.Not.Contain("Application."));
            Assert.That(text, Does.Not.Contain("SystemInfo"));
            Assert.That(text, Does.Not.Contain("GraphicsDevice"));
            Assert.That(text, Does.Not.Contain("IntPtr"));
            Assert.That(text, Does.Not.Contain("SafeHandle"));
            Assert.That(text, Does.Not.Contain("NvEnc"));
            Assert.That(text, Does.Not.Contain("RenderTexture"));
        }

        [Test]
        public void PoolAndLease_NoSingletonNoStaticMutableState_NoForbiddenApi()
        {
            Type poolType = typeof(NvencGpuConversionSyncPool);
            Type leaseType = typeof(NvencGpuConversionSyncLease);

            string[] forbiddenMethods =
            {
                "Dispose", "Reset", "Clear", "ForceReturn", "ReturnAll", "Resize", "Unpoison",
            };

            Assert.That(poolType.IsSealed, Is.True, poolType.Name + " must be sealed.");
            Assert.That(typeof(IDisposable).IsAssignableFrom(poolType), Is.False, poolType.Name + " must not be disposable.");

            foreach (FieldInfo field in poolType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, poolType.Name + "." + field.Name + " must be static readonly.");
            }

            foreach (MethodInfo method in poolType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.That(forbiddenMethods, Does.Not.Contain(method.Name), poolType.Name + " must not expose " + method.Name + ".");
            }

            Assert.That(leaseType.IsValueType, Is.True, leaseType.Name + " must be a value type.");
            Assert.That(typeof(IDisposable).IsAssignableFrom(leaseType), Is.False, leaseType.Name + " must not be disposable.");

            foreach (FieldInfo field in leaseType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                Assert.That(field.IsInitOnly, Is.True, leaseType.Name + "." + field.Name + " must be readonly.");
            }
        }

        private static long[] ReadGenerations(NvencGpuConversionSyncPool pool)
        {
            FieldInfo field = typeof(NvencGpuConversionSyncPool).GetField("_generations", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "NvencGpuConversionSyncPool must hold a _generations array.");
            return (long[])field.GetValue(pool);
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
    }
}
