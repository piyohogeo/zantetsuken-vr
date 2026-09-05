using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the two Phase 0.11 fixed-capacity downstream credit
    /// pools: Submit-to-Output and Frame Completion. Neither pool owns a queue,
    /// buffer, surface, handle, or other native resource.
    /// </summary>
    public class NvencCapacityCreditPoolContractTests
    {
        [Test]
        public void Capacity_IsEight_ForBothPools()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencSubmitToOutputCreditPool submitToOutput = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletion = new NvencFrameCompletionCreditPool(state);

            Assert.That(NvencBringUpProfileV1.SubmitToOutputQueueCapacity, Is.EqualTo(8));
            Assert.That(NvencBringUpProfileV1.FrameCompletionQueueCapacity, Is.EqualTo(8));
            Assert.That(submitToOutput.Capacity, Is.EqualTo(8));
            Assert.That(frameCompletion.Capacity, Is.EqualTo(8));
            Assert.That(submitToOutput.OccupiedCount, Is.EqualTo(0));
            Assert.That(frameCompletion.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void RentEightThenNinthFailsWithDefaultLease()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencSubmitToOutputCreditPool submitToOutput = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletion = new NvencFrameCompletionCreditPool(state);

            for (int i = 0; i < 8; i++)
            {
                Assert.That(submitToOutput.TryRent(out NvencSubmitToOutputCreditLease lease), Is.True);
                Assert.That(lease.IsValid, Is.True);
                Assert.That(frameCompletion.TryRent(out NvencFrameCompletionCreditLease fcLease), Is.True);
                Assert.That(fcLease.IsValid, Is.True);
            }

            Assert.That(submitToOutput.OccupiedCount, Is.EqualTo(8));
            Assert.That(frameCompletion.OccupiedCount, Is.EqualTo(8));

            Assert.That(submitToOutput.TryRent(out NvencSubmitToOutputCreditLease ninth), Is.False);
            Assert.That(ninth.IsValid, Is.False);
            Assert.That(ninth.SlotIndex, Is.EqualTo(0));
            Assert.That(ninth.Generation, Is.EqualTo(0));

            Assert.That(frameCompletion.TryRent(out NvencFrameCompletionCreditLease fcNinth), Is.False);
            Assert.That(fcNinth.IsValid, Is.False);
            Assert.That(fcNinth.SlotIndex, Is.EqualTo(0));
            Assert.That(fcNinth.Generation, Is.EqualTo(0));
        }

        [Test]
        public void ReturnThenRent_ReusesIndexWithNewGeneration()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencSubmitToOutputCreditPool submitToOutput = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletion = new NvencFrameCompletionCreditPool(state);

            Assert.That(submitToOutput.TryRent(out NvencSubmitToOutputCreditLease first), Is.True);
            Assert.That(frameCompletion.TryRent(out NvencFrameCompletionCreditLease fcFirst), Is.True);
            int firstIndex = first.SlotIndex;
            long firstGeneration = first.Generation;
            int fcFirstIndex = fcFirst.SlotIndex;
            long fcFirstGeneration = fcFirst.Generation;

            Assert.That(submitToOutput.TryReturn(first), Is.True);
            Assert.That(frameCompletion.TryReturn(fcFirst), Is.True);
            Assert.That(submitToOutput.OccupiedCount, Is.EqualTo(0));
            Assert.That(frameCompletion.OccupiedCount, Is.EqualTo(0));

            Assert.That(submitToOutput.TryRent(out NvencSubmitToOutputCreditLease second), Is.True);
            Assert.That(second.SlotIndex, Is.EqualTo(firstIndex));
            Assert.That(second.Generation, Is.EqualTo(firstGeneration + 1));

            Assert.That(frameCompletion.TryRent(out NvencFrameCompletionCreditLease fcSecond), Is.True);
            Assert.That(fcSecond.SlotIndex, Is.EqualTo(fcFirstIndex));
            Assert.That(fcSecond.Generation, Is.EqualTo(fcFirstGeneration + 1));
        }

        [Test]
        public void StaleLeaseReturn_DoesNotReleaseCurrentReservation()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencSubmitToOutputCreditPool submitToOutput = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletion = new NvencFrameCompletionCreditPool(state);

            Assert.That(submitToOutput.TryRent(out NvencSubmitToOutputCreditLease first), Is.True);
            Assert.That(submitToOutput.TryReturn(first), Is.True);
            Assert.That(submitToOutput.TryRent(out NvencSubmitToOutputCreditLease second), Is.True);
            Assert.That(submitToOutput.OccupiedCount, Is.EqualTo(1));

            Assert.That(submitToOutput.TryReturn(first), Is.False);
            Assert.That(submitToOutput.OccupiedCount, Is.EqualTo(1));
            Assert.That(submitToOutput.IsActive(second), Is.True);
            Assert.That(submitToOutput.IsActive(first), Is.False);

            Assert.That(frameCompletion.TryRent(out NvencFrameCompletionCreditLease fcFirst), Is.True);
            Assert.That(frameCompletion.TryReturn(fcFirst), Is.True);
            Assert.That(frameCompletion.TryRent(out NvencFrameCompletionCreditLease fcSecond), Is.True);
            Assert.That(frameCompletion.OccupiedCount, Is.EqualTo(1));

            Assert.That(frameCompletion.TryReturn(fcFirst), Is.False);
            Assert.That(frameCompletion.OccupiedCount, Is.EqualTo(1));
            Assert.That(frameCompletion.IsActive(fcSecond), Is.True);
            Assert.That(frameCompletion.IsActive(fcFirst), Is.False);
        }

        [Test]
        public void ForeignPoolLease_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencSubmitToOutputCreditPool a = new NvencSubmitToOutputCreditPool(state);
            NvencSubmitToOutputCreditPool b = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool fcA = new NvencFrameCompletionCreditPool(state);
            NvencFrameCompletionCreditPool fcB = new NvencFrameCompletionCreditPool(state);

            Assert.That(a.TryRent(out NvencSubmitToOutputCreditLease lease), Is.True);
            Assert.That(fcA.TryRent(out NvencFrameCompletionCreditLease fcLease), Is.True);

            Assert.That(b.TryReturn(lease), Is.False);
            Assert.That(b.IsActive(lease), Is.False);
            Assert.That(fcB.TryReturn(fcLease), Is.False);
            Assert.That(fcB.IsActive(fcLease), Is.False);

            Assert.That(a.OccupiedCount, Is.EqualTo(1));
            Assert.That(b.OccupiedCount, Is.EqualTo(0));
            Assert.That(fcA.OccupiedCount, Is.EqualTo(1));
            Assert.That(fcB.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void DefaultLease_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencSubmitToOutputCreditPool submitToOutput = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletion = new NvencFrameCompletionCreditPool(state);
            NvencSubmitToOutputCreditLease empty = default;
            NvencFrameCompletionCreditLease fcEmpty = default;

            Assert.That(submitToOutput.TryReturn(empty), Is.False);
            Assert.That(submitToOutput.IsActive(empty), Is.False);
            Assert.That(frameCompletion.TryReturn(fcEmpty), Is.False);
            Assert.That(frameCompletion.IsActive(fcEmpty), Is.False);
            Assert.That(submitToOutput.OccupiedCount, Is.EqualTo(0));
            Assert.That(frameCompletion.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void DoubleReturn_Rejected()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencSubmitToOutputCreditPool submitToOutput = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletion = new NvencFrameCompletionCreditPool(state);

            Assert.That(submitToOutput.TryRent(out NvencSubmitToOutputCreditLease lease), Is.True);
            Assert.That(submitToOutput.TryReturn(lease), Is.True);
            Assert.That(submitToOutput.TryReturn(lease), Is.False);
            Assert.That(submitToOutput.OccupiedCount, Is.EqualTo(0));

            Assert.That(frameCompletion.TryRent(out NvencFrameCompletionCreditLease fcLease), Is.True);
            Assert.That(frameCompletion.TryReturn(fcLease), Is.True);
            Assert.That(frameCompletion.TryReturn(fcLease), Is.False);
            Assert.That(frameCompletion.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void Draining_BlocksRent_ButAllowsReturnOfActiveLease()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencSubmitToOutputCreditPool submitToOutput = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletion = new NvencFrameCompletionCreditPool(state);

            Assert.That(submitToOutput.TryRent(out NvencSubmitToOutputCreditLease lease), Is.True);
            Assert.That(frameCompletion.TryRent(out NvencFrameCompletionCreditLease fcLease), Is.True);
            Assert.That(state.TryBeginDrain(), Is.True);

            Assert.That(submitToOutput.TryRent(out NvencSubmitToOutputCreditLease afterDrain), Is.False);
            Assert.That(afterDrain.IsValid, Is.False);
            Assert.That(frameCompletion.TryRent(out NvencFrameCompletionCreditLease fcAfterDrain), Is.False);
            Assert.That(fcAfterDrain.IsValid, Is.False);

            Assert.That(submitToOutput.TryReturn(lease), Is.True);
            Assert.That(frameCompletion.TryReturn(fcLease), Is.True);
            Assert.That(submitToOutput.OccupiedCount, Is.EqualTo(0));
            Assert.That(frameCompletion.OccupiedCount, Is.EqualTo(0));
        }

        [Test]
        public void Poison_BlocksRentAndReturn_KeepsCreditsOccupied()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencSubmitToOutputCreditPool submitToOutput = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletion = new NvencFrameCompletionCreditPool(state);

            Assert.That(submitToOutput.TryRent(out NvencSubmitToOutputCreditLease lease), Is.True);
            Assert.That(frameCompletion.TryRent(out NvencFrameCompletionCreditLease fcLease), Is.True);
            Assert.That(state.TryPoison(), Is.True);

            Assert.That(submitToOutput.TryRent(out NvencSubmitToOutputCreditLease afterPoison), Is.False);
            Assert.That(submitToOutput.TryReturn(lease), Is.False);
            Assert.That(frameCompletion.TryRent(out NvencFrameCompletionCreditLease fcAfterPoison), Is.False);
            Assert.That(frameCompletion.TryReturn(fcLease), Is.False);

            Assert.That(submitToOutput.OccupiedCount, Is.EqualTo(1));
            Assert.That(submitToOutput.IsActive(lease), Is.True);
            Assert.That(frameCompletion.OccupiedCount, Is.EqualTo(1));
            Assert.That(frameCompletion.IsActive(fcLease), Is.True);
        }

        [Test]
        public void GenerationOverflow_RefusesReuseWithoutWrap()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            NvencSubmitToOutputCreditPool submitToOutput = new NvencSubmitToOutputCreditPool(state);
            NvencFrameCompletionCreditPool frameCompletion = new NvencFrameCompletionCreditPool(state);

            long[] submitGenerations = ReadGenerations(submitToOutput);
            long[] frameCompletionGenerations = ReadGenerations(frameCompletion);

            Assert.That(submitToOutput.TryRent(out NvencSubmitToOutputCreditLease first), Is.True);
            int index = first.SlotIndex;
            Assert.That(submitToOutput.TryReturn(first), Is.True);
            submitGenerations[index] = long.MaxValue;

            Assert.That(submitToOutput.TryRent(out NvencSubmitToOutputCreditLease atMax), Is.True);
            Assert.That(atMax.SlotIndex, Is.EqualTo(index));
            Assert.That(atMax.Generation, Is.EqualTo(long.MaxValue));
            Assert.That(submitToOutput.TryReturn(atMax), Is.True);
            Assert.That(submitToOutput.OccupiedCount, Is.EqualTo(0));
            Assert.That(submitGenerations[index], Is.EqualTo(long.MaxValue));

            for (int i = 0; i < 7; i++)
            {
                Assert.That(submitToOutput.TryRent(out NvencSubmitToOutputCreditLease lease), Is.True);
                Assert.That(lease.SlotIndex, Is.Not.EqualTo(index));
            }

            Assert.That(submitToOutput.TryRent(out NvencSubmitToOutputCreditLease exhausted), Is.False);

            Assert.That(frameCompletion.TryRent(out NvencFrameCompletionCreditLease fcFirst), Is.True);
            int fcIndex = fcFirst.SlotIndex;
            Assert.That(frameCompletion.TryReturn(fcFirst), Is.True);
            frameCompletionGenerations[fcIndex] = long.MaxValue;

            Assert.That(frameCompletion.TryRent(out NvencFrameCompletionCreditLease fcAtMax), Is.True);
            Assert.That(fcAtMax.SlotIndex, Is.EqualTo(fcIndex));
            Assert.That(fcAtMax.Generation, Is.EqualTo(long.MaxValue));
            Assert.That(frameCompletion.TryReturn(fcAtMax), Is.True);
            Assert.That(frameCompletion.OccupiedCount, Is.EqualTo(0));
            Assert.That(frameCompletionGenerations[fcIndex], Is.EqualTo(long.MaxValue));

            for (int i = 0; i < 7; i++)
            {
                Assert.That(frameCompletion.TryRent(out NvencFrameCompletionCreditLease lease), Is.True);
                Assert.That(lease.SlotIndex, Is.Not.EqualTo(fcIndex));
            }

            Assert.That(frameCompletion.TryRent(out NvencFrameCompletionCreditLease fcExhausted), Is.False);
        }

        [Test]
        public void RentReturnPaths_DoNotAllocateArraysOrCollections()
        {
            string directory = RuntimeDirectory();
            string submitSource = File.ReadAllText(Path.Combine(directory, "NvencSubmitToOutputCreditPool.cs"));
            string frameCompletionSource = File.ReadAllText(Path.Combine(directory, "NvencFrameCompletionCreditPool.cs"));

            string[] forbidden =
            {
                "new long[", "new bool[", "new int[", "new byte[", "new []",
                "new List", "new Dictionary", "new Stack", "new Queue", "new HashSet",
                "Array.", "ArrayPool", ".ToArray(", ".ToList(", "Enumerable.", "Allocate",
            };

            string[] bodies =
            {
                ExtractMethodBody(submitSource, "TryRent"),
                ExtractMethodBody(submitSource, "TryReturn"),
                ExtractMethodBody(frameCompletionSource, "TryRent"),
                ExtractMethodBody(frameCompletionSource, "TryReturn"),
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
                File.ReadAllText(Path.Combine(directory, "NvencSubmitToOutputCreditPool.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencSubmitToOutputCreditLease.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencFrameCompletionCreditPool.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencFrameCompletionCreditLease.cs"));

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
            Type[] poolTypes =
            {
                typeof(NvencSubmitToOutputCreditPool),
                typeof(NvencFrameCompletionCreditPool),
            };

            Type[] leaseTypes =
            {
                typeof(NvencSubmitToOutputCreditLease),
                typeof(NvencFrameCompletionCreditLease),
            };

            string[] forbiddenMethods =
            {
                "Dispose", "Reset", "Clear", "ForceReturn", "ReturnAll", "Resize", "Unpoison",
            };

            foreach (Type poolType in poolTypes)
            {
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
            }

            foreach (Type leaseType in leaseTypes)
            {
                Assert.That(leaseType.IsValueType, Is.True, leaseType.Name + " must be a value type.");
                Assert.That(typeof(IDisposable).IsAssignableFrom(leaseType), Is.False, leaseType.Name + " must not be disposable.");

                foreach (FieldInfo field in leaseType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    Assert.That(field.IsInitOnly, Is.True, leaseType.Name + "." + field.Name + " must be readonly.");
                }
            }
        }

        private static long[] ReadGenerations(object pool)
        {
            FieldInfo field = pool.GetType().GetField("_generations", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, pool.GetType().Name + " must hold a _generations array.");
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
