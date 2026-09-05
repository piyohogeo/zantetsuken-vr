using System;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Observability;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// Contract tests for the Phase 0.11 process-wide fail-stop state. No real
    /// OS, GPU, NVENC, or publication is used.
    /// </summary>
    public class NvencCaptureProcessStateContractTests
    {
        [Test]
        public void NewInstance_IsRunning()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();

            Assert.That(state.State, Is.EqualTo(NvencCaptureProcessStatus.Running));
            Assert.That(state.IsAccepting, Is.True);
            Assert.That(state.IsDraining, Is.False);
            Assert.That(state.IsPoisoned, Is.False);
        }

        [Test]
        public void SeparateInstances_DoNotShareState()
        {
            NvencCaptureProcessState a = new NvencCaptureProcessState();
            NvencCaptureProcessState b = new NvencCaptureProcessState();

            Assert.That(a.TryPoison(), Is.True);

            Assert.That(b.IsAccepting, Is.True);
            Assert.That(b.IsPoisoned, Is.False);
        }

        [Test]
        public void RunningToDraining_TransitionsExactlyOnce()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();

            Assert.That(state.TryBeginDrain(), Is.True);
            Assert.That(state.State, Is.EqualTo(NvencCaptureProcessStatus.Draining));
            Assert.That(state.IsDraining, Is.True);
            Assert.That(state.IsAccepting, Is.False);

            Assert.That(state.TryBeginDrain(), Is.False);
            Assert.That(state.State, Is.EqualTo(NvencCaptureProcessStatus.Draining));
        }

        [Test]
        public void RunningToPoison_DirectlyAllowed()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();

            Assert.That(state.TryPoison(), Is.True);
            Assert.That(state.State, Is.EqualTo(NvencCaptureProcessStatus.PoisonedUntilProcessRestart));
            Assert.That(state.IsPoisoned, Is.True);
        }

        [Test]
        public void DrainingToPoison_Allowed()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            Assert.That(state.TryBeginDrain(), Is.True);

            Assert.That(state.TryPoison(), Is.True);
            Assert.That(state.IsPoisoned, Is.True);
            Assert.That(state.State, Is.EqualTo(NvencCaptureProcessStatus.PoisonedUntilProcessRestart));
        }

        [Test]
        public void PoisonThenDrain_FailsAndStateUnchanged()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            Assert.That(state.TryPoison(), Is.True);

            Assert.That(state.TryBeginDrain(), Is.False);
            Assert.That(state.State, Is.EqualTo(NvencCaptureProcessStatus.PoisonedUntilProcessRestart));
        }

        [Test]
        public void PoisonTwice_SecondIsNoOp()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();

            Assert.That(state.TryPoison(), Is.True);
            Assert.That(state.TryPoison(), Is.False);
            Assert.That(state.State, Is.EqualTo(NvencCaptureProcessStatus.PoisonedUntilProcessRestart));
        }

        [Test]
        public void NoTransitionBackToRunning()
        {
            Type type = typeof(NvencCaptureProcessState);

            foreach (MethodInfo method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName || method.IsConstructor)
                {
                    continue;
                }

                Assert.That(
                    method.Name == "TryBeginDrain" || method.Name == "TryPoison",
                    Is.True,
                    type.Name + "." + method.Name + " must be the only transition method.");
            }
        }

        [Test]
        public void BeginDrainAndPoisonRace_AlwaysPoisoned()
        {
            // Align the two transitions with a barrier, then let the
            // interleaving decide the order. Either order must leave the state
            // poisoned; no timing-based ordering is asserted.
            for (int iteration = 0; iteration < 100; iteration++)
            {
                NvencCaptureProcessState state = new NvencCaptureProcessState();

                using (Barrier barrier = new Barrier(2))
                {
                    Thread drainThread = new Thread(() =>
                    {
                        barrier.SignalAndWait();
                        state.TryBeginDrain();
                    });

                    Thread poisonThread = new Thread(() =>
                    {
                        barrier.SignalAndWait();
                        state.TryPoison();
                    });

                    drainThread.Start();
                    poisonThread.Start();
                    drainThread.Join();
                    poisonThread.Join();
                }

                Assert.That(state.IsPoisoned, Is.True);
                Assert.That(state.State, Is.EqualTo(NvencCaptureProcessStatus.PoisonedUntilProcessRestart));
            }
        }

        [Test]
        public void ConcurrentPoison_MultipleThreads_NoException_FinalPoisoned()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();

            const int threadCount = 8;
            using (Barrier barrier = new Barrier(threadCount))
            {
                Thread[] threads = new Thread[threadCount];
                for (int i = 0; i < threadCount; i++)
                {
                    threads[i] = new Thread(() =>
                    {
                        barrier.SignalAndWait();
                        state.TryPoison();
                    });
                    threads[i].Start();
                }

                for (int i = 0; i < threadCount; i++)
                {
                    threads[i].Join();
                }
            }

            Assert.That(state.IsPoisoned, Is.True);
            Assert.That(state.State, Is.EqualTo(NvencCaptureProcessStatus.PoisonedUntilProcessRestart));
        }

        [Test]
        public void NoMutableStaticFields()
        {
            Type type = typeof(NvencCaptureProcessState);

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                Assert.That(field.IsInitOnly || field.IsLiteral, Is.True, type.Name + "." + field.Name + " must be readonly.");
            }
        }

        [Test]
        public void NotDisposableMonoBehaviourScriptableObject()
        {
            Type type = typeof(NvencCaptureProcessState);

            Assert.That(typeof(IDisposable).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(MonoBehaviour).IsAssignableFrom(type), Is.False);
            Assert.That(typeof(ScriptableObject).IsAssignableFrom(type), Is.False);
        }

        [Test]
        public void HoldsOnlyAtomicInt_NoTokenReceiptGenerationHistory()
        {
            Type type = typeof(NvencCaptureProcessState);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(1));
            Assert.That(fields[0].FieldType, Is.EqualTo(typeof(int)));
        }

        [Test]
        public void ProductionSources_NoLockNoWaitNoThreadNoTaskNoIoNoUnityNoNative()
        {
            string root = Path.Combine(Application.dataPath, "..");
            string directory = Path.Combine(root, "Assets/Zantetsu/Runtime/Observability");
            string text =
                File.ReadAllText(Path.Combine(directory, "NvencCaptureProcessState.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencCaptureProcessStatus.cs"));

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
    }
}
