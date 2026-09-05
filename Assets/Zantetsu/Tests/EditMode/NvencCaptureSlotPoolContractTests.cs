using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 fixed-capacity Frame Work Slot and
    /// NVENC Encode Sample Slot reservation pools. No real OS, GPU, NVENC,
    /// Texture, handle, Completion Event, queue, or publication is used.
    /// </summary>
    public class NvencCaptureSlotPoolContractTests
    {
        [Test]
        public void BothPools_CapacityIsEight()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool work = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool sample = new NvencEncodeSampleSlotPool(state);

            Assert.That(NvencBringUpProfileV1.WorkSlotCount, Is.EqualTo(8));
            Assert.That(NvencBringUpProfileV1.EncodeSampleSlotCount, Is.EqualTo(8));
            Assert.That(work.Capacity, Is.EqualTo(8));
            Assert.That(sample.Capacity, Is.EqualTo(8));
            Assert.That(work.OccupiedCount, Is.EqualTo(0));
            Assert.That(sample.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void WorkPool_RentEightThenNinthFailsWithDefaultLease()
        {
            NvencCaptureWorkSlotPool pool = new NvencCaptureWorkSlotPool(new NvencCaptureProcessState());

            for (int i = 0; i < 8; i++)
            {
                Assert.That(pool.TryRent(out NvencCaptureWorkSlotLease lease), Is.True);
                Assert.That(lease.IsValid, Is.True);
            }

            Assert.That(pool.OccupiedCount, Is.EqualTo(8));
            Assert.That(pool.TryRent(out NvencCaptureWorkSlotLease ninth), Is.False);
            Assert.That(ninth.IsValid, Is.False);
            Assert.That(ninth.SlotIndex, Is.EqualTo(0));
            Assert.That(ninth.Generation, Is.EqualTo(0));
        }

        [Test]
        public void SamplePool_RentEightThenNinthFailsWithDefaultLease()
        {
            NvencEncodeSampleSlotPool pool = new NvencEncodeSampleSlotPool(new NvencCaptureProcessState());

            for (int i = 0; i < 8; i++)
            {
                Assert.That(pool.TryRent(out NvencEncodeSampleSlotLease lease), Is.True);
                Assert.That(lease.IsValid, Is.True);
            }

            Assert.That(pool.OccupiedCount, Is.EqualTo(8));
            Assert.That(pool.TryRent(out NvencEncodeSampleSlotLease ninth), Is.False);
            Assert.That(ninth.IsValid, Is.False);
            Assert.That(ninth.SlotIndex, Is.EqualTo(0));
            Assert.That(ninth.Generation, Is.EqualTo(0));
        }

        [Test]
        public void WorkPool_ReturnThenRent_ReusesIndexWithNewGeneration()
        {
            NvencCaptureWorkSlotPool pool = new NvencCaptureWorkSlotPool(new NvencCaptureProcessState());

            Assert.That(pool.TryRent(out NvencCaptureWorkSlotLease first), Is.True);
            int firstIndex = first.SlotIndex;
            long firstGeneration = first.Generation;

            Assert.That(pool.TryReturn(first), Is.True);
            Assert.That(pool.OccupiedCount, Is.EqualTo(0));

            Assert.That(pool.TryRent(out NvencCaptureWorkSlotLease second), Is.True);
            Assert.That(second.SlotIndex, Is.EqualTo(firstIndex));
            Assert.That(second.Generation, Is.EqualTo(firstGeneration + 1));
        }

        [Test]
        public void SamplePool_ReturnThenRent_ReusesIndexWithNewGeneration()
        {
            NvencEncodeSampleSlotPool pool = new NvencEncodeSampleSlotPool(new NvencCaptureProcessState());

            Assert.That(pool.TryRent(out NvencEncodeSampleSlotLease first), Is.True);
            int firstIndex = first.SlotIndex;
            long firstGeneration = first.Generation;

            Assert.That(pool.TryReturn(first), Is.True);
            Assert.That(pool.OccupiedCount, Is.EqualTo(0));

            Assert.That(pool.TryRent(out NvencEncodeSampleSlotLease second), Is.True);
            Assert.That(second.SlotIndex, Is.EqualTo(firstIndex));
            Assert.That(second.Generation, Is.EqualTo(firstGeneration + 1));
        }

        [Test]
        public void WorkPool_StaleLeaseReturn_DoesNotReleaseCurrentReservation()
        {
            NvencCaptureWorkSlotPool pool = new NvencCaptureWorkSlotPool(new NvencCaptureProcessState());

            Assert.That(pool.TryRent(out NvencCaptureWorkSlotLease first), Is.True);
            Assert.That(pool.TryReturn(first), Is.True);
            Assert.That(pool.TryRent(out NvencCaptureWorkSlotLease second), Is.True);
            Assert.That(pool.OccupiedCount, Is.EqualTo(1));

            Assert.That(pool.TryReturn(first), Is.False);
            Assert.That(pool.OccupiedCount, Is.EqualTo(1));
            Assert.That(pool.IsActive(second), Is.True);
            Assert.That(pool.IsActive(first), Is.False);
        }

        [Test]
        public void SamplePool_StaleLeaseReturn_DoesNotReleaseCurrentReservation()
        {
            NvencEncodeSampleSlotPool pool = new NvencEncodeSampleSlotPool(new NvencCaptureProcessState());

            Assert.That(pool.TryRent(out NvencEncodeSampleSlotLease first), Is.True);
            Assert.That(pool.TryReturn(first), Is.True);
            Assert.That(pool.TryRent(out NvencEncodeSampleSlotLease second), Is.True);
            Assert.That(pool.OccupiedCount, Is.EqualTo(1));

            Assert.That(pool.TryReturn(first), Is.False);
            Assert.That(pool.OccupiedCount, Is.EqualTo(1));
            Assert.That(pool.IsActive(second), Is.True);
            Assert.That(pool.IsActive(first), Is.False);
        }

        [Test]
        public void WorkPool_ForeignPoolLease_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool a = new NvencCaptureWorkSlotPool(state);
            NvencCaptureWorkSlotPool b = new NvencCaptureWorkSlotPool(state);

            Assert.That(a.TryRent(out NvencCaptureWorkSlotLease lease), Is.True);

            Assert.That(b.TryReturn(lease), Is.False);
            Assert.That(b.IsActive(lease), Is.False);
            Assert.That(a.OccupiedCount, Is.EqualTo(1));
            Assert.That(b.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void SamplePool_ForeignPoolLease_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencEncodeSampleSlotPool a = new NvencEncodeSampleSlotPool(state);
            NvencEncodeSampleSlotPool b = new NvencEncodeSampleSlotPool(state);

            Assert.That(a.TryRent(out NvencEncodeSampleSlotLease lease), Is.True);

            Assert.That(b.TryReturn(lease), Is.False);
            Assert.That(b.IsActive(lease), Is.False);
            Assert.That(a.OccupiedCount, Is.EqualTo(1));
            Assert.That(b.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void WorkPool_DefaultLease_Rejected()
        {
            NvencCaptureWorkSlotPool pool = new NvencCaptureWorkSlotPool(new NvencCaptureProcessState());
            NvencCaptureWorkSlotLease empty = default;

            Assert.That(pool.TryReturn(empty), Is.False);
            Assert.That(pool.IsActive(empty), Is.False);
            Assert.That(pool.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void SamplePool_DefaultLease_Rejected()
        {
            NvencEncodeSampleSlotPool pool = new NvencEncodeSampleSlotPool(new NvencCaptureProcessState());
            NvencEncodeSampleSlotLease empty = default;

            Assert.That(pool.TryReturn(empty), Is.False);
            Assert.That(pool.IsActive(empty), Is.False);
            Assert.That(pool.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void WorkPool_DoubleReturn_Rejected()
        {
            NvencCaptureWorkSlotPool pool = new NvencCaptureWorkSlotPool(new NvencCaptureProcessState());

            Assert.That(pool.TryRent(out NvencCaptureWorkSlotLease lease), Is.True);
            Assert.That(pool.TryReturn(lease), Is.True);
            Assert.That(pool.TryReturn(lease), Is.False);
            Assert.That(pool.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void SamplePool_DoubleReturn_Rejected()
        {
            NvencEncodeSampleSlotPool pool = new NvencEncodeSampleSlotPool(new NvencCaptureProcessState());

            Assert.That(pool.TryRent(out NvencEncodeSampleSlotLease lease), Is.True);
            Assert.That(pool.TryReturn(lease), Is.True);
            Assert.That(pool.TryReturn(lease), Is.False);
            Assert.That(pool.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void WorkAndSampleLeases_AreMutuallyExclusiveTypes()
        {
            Type workLease = typeof(NvencCaptureWorkSlotLease);
            Type sampleLease = typeof(NvencEncodeSampleSlotLease);

            Assert.That(workLease, Is.Not.EqualTo(sampleLease));
            Assert.That(workLease.IsValueType, Is.True);
            Assert.That(sampleLease.IsValueType, Is.True);
            Assert.That(workLease.IsAssignableFrom(sampleLease), Is.False);
            Assert.That(sampleLease.IsAssignableFrom(workLease), Is.False);

            AssertPoolAcceptsOnly(typeof(NvencCaptureWorkSlotPool), workLease);
            AssertPoolAcceptsOnly(typeof(NvencEncodeSampleSlotPool), sampleLease);

            AssertPoolNeverAccepts(typeof(NvencCaptureWorkSlotPool), sampleLease);
            AssertPoolNeverAccepts(typeof(NvencEncodeSampleSlotPool), workLease);
        }

        [Test]
        public void SampleReturn_DoesNotChangeWorkOccupancy_AndViceVersa()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool work = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool sample = new NvencEncodeSampleSlotPool(state);

            Assert.That(work.TryRent(out NvencCaptureWorkSlotLease workLease), Is.True);
            Assert.That(sample.TryRent(out NvencEncodeSampleSlotLease sampleLease), Is.True);

            Assert.That(sample.TryReturn(sampleLease), Is.True);
            Assert.That(sample.OccupiedCount, Is.EqualTo(0));
            Assert.That(work.OccupiedCount, Is.EqualTo(1));

            Assert.That(sample.TryRent(out sampleLease), Is.True);
            Assert.That(work.TryReturn(workLease), Is.True);
            Assert.That(work.OccupiedCount, Is.EqualTo(0));
            Assert.That(sample.OccupiedCount, Is.EqualTo(1));
        }

        [Test]
        public void Draining_BlocksRent_ButAllowsReturnOfActiveLease()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool work = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool sample = new NvencEncodeSampleSlotPool(state);

            Assert.That(work.TryRent(out NvencCaptureWorkSlotLease workLease), Is.True);
            Assert.That(sample.TryRent(out NvencEncodeSampleSlotLease sampleLease), Is.True);

            Assert.That(state.TryBeginDrain(), Is.True);

            Assert.That(work.TryRent(out NvencCaptureWorkSlotLease workAfterDrain), Is.False);
            Assert.That(sample.TryRent(out NvencEncodeSampleSlotLease sampleAfterDrain), Is.False);
            Assert.That(workAfterDrain.IsValid, Is.False);
            Assert.That(sampleAfterDrain.IsValid, Is.False);

            Assert.That(work.TryReturn(workLease), Is.True);
            Assert.That(sample.TryReturn(sampleLease), Is.True);
            Assert.That(work.OccupiedCount, Is.EqualTo(0));
            Assert.That(sample.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void Poison_BlocksRentAndReturn_KeepsSlotsOccupied()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencCaptureWorkSlotPool work = new NvencCaptureWorkSlotPool(state);
            NvencEncodeSampleSlotPool sample = new NvencEncodeSampleSlotPool(state);

            Assert.That(work.TryRent(out NvencCaptureWorkSlotLease workLease), Is.True);
            Assert.That(sample.TryRent(out NvencEncodeSampleSlotLease sampleLease), Is.True);

            Assert.That(state.TryPoison(), Is.True);

            Assert.That(work.TryRent(out NvencCaptureWorkSlotLease workAfterPoison), Is.False);
            Assert.That(sample.TryRent(out NvencEncodeSampleSlotLease sampleAfterPoison), Is.False);

            Assert.That(work.TryReturn(workLease), Is.False);
            Assert.That(sample.TryReturn(sampleLease), Is.False);

            Assert.That(work.OccupiedCount, Is.EqualTo(1));
            Assert.That(sample.OccupiedCount, Is.EqualTo(1));
            Assert.That(work.IsActive(workLease), Is.True);
            Assert.That(sample.IsActive(sampleLease), Is.True);
        }

        [Test]
        public void State_DoesNotLeakAcrossProcessStateInstances()
        {
            NvencCaptureProcessState poisoned = new NvencCaptureProcessState();
            NvencCaptureProcessState running = new NvencCaptureProcessState();

            NvencCaptureWorkSlotPool poisonedPool = new NvencCaptureWorkSlotPool(poisoned);
            NvencCaptureWorkSlotPool runningPool = new NvencCaptureWorkSlotPool(running);

            Assert.That(poisoned.TryPoison(), Is.True);

            Assert.That(poisonedPool.TryRent(out NvencCaptureWorkSlotLease poisonedLease), Is.False);
            Assert.That(runningPool.TryRent(out NvencCaptureWorkSlotLease runningLease), Is.True);
            Assert.That(runningPool.OccupiedCount, Is.EqualTo(1));
            Assert.That(runningLease.IsValid, Is.True);
        }

        [Test]
        public void WorkPool_GenerationOverflow_RefusesReuseWithoutWrap()
        {
            NvencCaptureWorkSlotPool pool = new NvencCaptureWorkSlotPool(new NvencCaptureProcessState());
            long[] generations = ReadGenerations<NvencCaptureWorkSlotPool>(pool);

            Assert.That(pool.TryRent(out NvencCaptureWorkSlotLease first), Is.True);
            int index = first.SlotIndex;
            Assert.That(pool.TryReturn(first), Is.True);

            generations[index] = long.MaxValue;

            Assert.That(pool.TryRent(out NvencCaptureWorkSlotLease atMax), Is.True);
            Assert.That(atMax.SlotIndex, Is.EqualTo(index));
            Assert.That(atMax.Generation, Is.EqualTo(long.MaxValue));

            Assert.That(pool.TryReturn(atMax), Is.True);
            Assert.That(pool.OccupiedCount, Is.EqualTo(0));
            Assert.That(generations[index], Is.EqualTo(long.MaxValue));

            for (int i = 0; i < 7; i++)
            {
                Assert.That(pool.TryRent(out NvencCaptureWorkSlotLease lease), Is.True);
                Assert.That(lease.SlotIndex, Is.Not.EqualTo(index));
            }

            Assert.That(pool.TryRent(out NvencCaptureWorkSlotLease exhausted), Is.False);
        }

        [Test]
        public void SamplePool_GenerationOverflow_RefusesReuseWithoutWrap()
        {
            NvencEncodeSampleSlotPool pool = new NvencEncodeSampleSlotPool(new NvencCaptureProcessState());
            long[] generations = ReadGenerations<NvencEncodeSampleSlotPool>(pool);

            Assert.That(pool.TryRent(out NvencEncodeSampleSlotLease first), Is.True);
            int index = first.SlotIndex;
            Assert.That(pool.TryReturn(first), Is.True);

            generations[index] = long.MaxValue;

            Assert.That(pool.TryRent(out NvencEncodeSampleSlotLease atMax), Is.True);
            Assert.That(atMax.SlotIndex, Is.EqualTo(index));
            Assert.That(atMax.Generation, Is.EqualTo(long.MaxValue));

            Assert.That(pool.TryReturn(atMax), Is.True);
            Assert.That(pool.OccupiedCount, Is.EqualTo(0));
            Assert.That(generations[index], Is.EqualTo(long.MaxValue));

            for (int i = 0; i < 7; i++)
            {
                Assert.That(pool.TryRent(out NvencEncodeSampleSlotLease lease), Is.True);
                Assert.That(lease.SlotIndex, Is.Not.EqualTo(index));
            }

            Assert.That(pool.TryRent(out NvencEncodeSampleSlotLease exhausted), Is.False);
        }

        [Test]
        public void RentReturnPaths_DoNotAllocateArraysOrCollections()
        {
            string directory = RuntimeDirectory();

            string[] bodies =
            {
                ExtractMethodBody(File.ReadAllText(Path.Combine(directory, "NvencCaptureWorkSlotPool.cs")), "TryRent"),
                ExtractMethodBody(File.ReadAllText(Path.Combine(directory, "NvencCaptureWorkSlotPool.cs")), "TryReturn"),
                ExtractMethodBody(File.ReadAllText(Path.Combine(directory, "NvencEncodeSampleSlotPool.cs")), "TryRent"),
                ExtractMethodBody(File.ReadAllText(Path.Combine(directory, "NvencEncodeSampleSlotPool.cs")), "TryReturn"),
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
                File.ReadAllText(Path.Combine(directory, "NvencCaptureWorkSlotPool.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencCaptureWorkSlotLease.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencEncodeSampleSlotPool.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencEncodeSampleSlotLease.cs"));

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
        }

        [Test]
        public void PoolsAndLeases_NoSingletonNoStaticMutableState_NoForbiddenApi()
        {
            Type[] poolTypes = { typeof(NvencCaptureWorkSlotPool), typeof(NvencEncodeSampleSlotPool) };
            Type[] leaseTypes = { typeof(NvencCaptureWorkSlotLease), typeof(NvencEncodeSampleSlotLease) };

            string[] forbiddenMethods =
            {
                "Dispose", "Reset", "Clear", "ForceReturn", "ReturnAll", "Resize", "Unpoison",
            };

            foreach (Type type in poolTypes)
            {
                Assert.That(type.IsSealed, Is.True, type.Name + " must be sealed.");
                Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False, type.Name + " must not be disposable.");

                foreach (FieldInfo field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, type.Name + "." + field.Name + " must be static readonly.");
                }

                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    Assert.That(property.GetSetMethod(true), Is.Null, type.Name + "." + property.Name + " must not have a static setter.");
                }

                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    Assert.That(forbiddenMethods, Does.Not.Contain(method.Name), type.Name + " must not expose " + method.Name + ".");
                }
            }

            foreach (Type type in leaseTypes)
            {
                Assert.That(type.IsValueType, Is.True, type.Name + " must be a value type.");

                foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(field.IsInitOnly, Is.True, type.Name + "." + field.Name + " must be readonly.");
                }

                foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    Assert.That(property.GetSetMethod(true), Is.Null, type.Name + "." + property.Name + " must not have a setter.");
                }
            }
        }

        private static void AssertPoolAcceptsOnly(Type poolType, Type leaseType)
        {
            foreach (string methodName in new[] { "TryRent", "TryReturn", "IsActive" })
            {
                MethodInfo method = poolType.GetMethod(
                    methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.That(method, Is.Not.Null, poolType.Name + " must declare " + methodName + ".");
                ParameterInfo[] parameters = method.GetParameters();
                Assert.That(parameters.Length, Is.EqualTo(1));
                Assert.That(parameters[0].ParameterType.GetElementType(), Is.EqualTo(leaseType));
            }
        }

        private static void AssertPoolNeverAccepts(Type poolType, Type forbiddenLeaseType)
        {
            foreach (MethodInfo method in poolType.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Type element = parameter.ParameterType.GetElementType() ?? parameter.ParameterType;
                    Assert.That(element, Is.Not.EqualTo(forbiddenLeaseType), poolType.Name + " must not accept " + forbiddenLeaseType.Name + ".");
                }
            }
        }

        private static long[] ReadGenerations<TPool>(TPool pool)
        {
            FieldInfo field = typeof(TPool).GetField("_generations", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, typeof(TPool).Name + " must hold a _generations array.");
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
                char c = source[i];
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source.Substring(open + 1, i - open - 1);
                    }
                }
            }

            Assert.Fail("Unbalanced braces after " + methodName + ".");
            return string.Empty;
        }
    }
}
