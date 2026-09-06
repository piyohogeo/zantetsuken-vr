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
        private const int WatchdogTimeoutMs = 5000;

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
        public void TryBeginAdmission_RunningSucceeds_AndMustEnd()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();

            Assert.That(state.TryBeginAdmission(), Is.True);
            Assert.That(state.State, Is.EqualTo(NvencCaptureProcessStatus.Running));
            Assert.That(state.IsAccepting, Is.True);

            state.EndAdmission();
        }

        [Test]
        public void TryBeginAdmission_FailsAfterDrain()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            Assert.That(state.TryBeginDrain(), Is.True);

            Assert.That(state.TryBeginAdmission(), Is.False);
            Assert.That(state.State, Is.EqualTo(NvencCaptureProcessStatus.Draining));
        }

        [Test]
        public void TryBeginAdmission_FailsAfterPoison()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            Assert.That(state.TryPoison(), Is.True);

            Assert.That(state.TryBeginAdmission(), Is.False);
            Assert.That(state.State, Is.EqualTo(NvencCaptureProcessStatus.PoisonedUntilProcessRestart));
        }

        [Test]
        public void AdmissionGuard_SerializesWithTransition()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();

            // Hold the gate as if an admission were mid-enqueue.
            Assert.That(state.TryBeginAdmission(), Is.True);

            int drainCompleted = 0;
            using (ManualResetEventSlim transitionStarted = new ManualResetEventSlim(false))
            {
                Thread drainThread = new Thread(() =>
                {
                    transitionStarted.Set();
                    state.TryBeginDrain();
                    Volatile.Write(ref drainCompleted, 1);
                });
                drainThread.IsBackground = true;
                drainThread.Start();

                Assert.That(transitionStarted.Wait(WatchdogTimeoutMs), Is.True, "Transition thread did not start within the watchdog timeout.");

                // The transition has reached the gate but the guard is still
                // held, so it cannot complete.
                Assert.That(Volatile.Read(ref drainCompleted), Is.EqualTo(0));

                state.EndAdmission();

                Assert.That(drainThread.Join(WatchdogTimeoutMs), Is.True, "Drain did not finish within the watchdog timeout.");
            }

            Assert.That(Volatile.Read(ref drainCompleted), Is.EqualTo(1));
            Assert.That(state.IsDraining, Is.True);

            // After the transition, admission fails.
            Assert.That(state.TryBeginAdmission(), Is.False);
        }

        [Test]
        public void ResourceResolution_RunningSucceeds_AndMustEnd()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();

            Assert.That(state.TryBeginResourceResolution(), Is.True);
            Assert.That(state.State, Is.EqualTo(NvencCaptureProcessStatus.Running));

            state.EndResourceResolution();
        }

        [Test]
        public void ResourceResolution_DrainingSucceeds()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            Assert.That(state.TryBeginDrain(), Is.True);

            Assert.That(state.TryBeginResourceResolution(), Is.True);
            Assert.That(state.IsDraining, Is.True);

            state.EndResourceResolution();
        }

        [Test]
        public void ResourceResolution_PoisonedFails()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            Assert.That(state.TryPoison(), Is.True);

            Assert.That(state.TryBeginResourceResolution(), Is.False);
            Assert.That(state.IsPoisoned, Is.True);
        }

        [Test]
        public void SubmitStep_RunningSucceeds_AndMustEnd()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();

            Assert.That(state.TryBeginSubmitStep(), Is.True);
            Assert.That(state.State, Is.EqualTo(NvencCaptureProcessStatus.Running));

            state.EndSubmitStep();
        }

        [Test]
        public void SubmitStep_DrainingSucceeds()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            Assert.That(state.TryBeginDrain(), Is.True);

            Assert.That(state.TryBeginSubmitStep(), Is.True);
            Assert.That(state.IsDraining, Is.True);

            state.EndSubmitStep();
        }

        [Test]
        public void SubmitStep_PoisonedFails()
        {
            NvencCaptureProcessState state = new NvencCaptureProcessState();
            Assert.That(state.TryPoison(), Is.True);

            Assert.That(state.TryBeginSubmitStep(), Is.False);
            Assert.That(state.IsPoisoned, Is.True);
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
                    method.Name == "TryBeginDrain" || method.Name == "TryPoison" ||
                    method.Name == "TryBeginRunAbandoned" ||
                    method.Name == "TryBeginAdmission" || method.Name == "EndAdmission" ||
                    method.Name == "TryBeginResourceResolution" || method.Name == "EndResourceResolution" ||
                    method.Name == "TryBeginSubmitStep" || method.Name == "EndSubmitStep",
                    Is.True,
                    type.Name + "." + method.Name + " must be a transition, admission, resource-resolution, or submit-step method.");
            }
        }

        [Test]
        public void BeginDrainAndPoisonRace_AlwaysPoisoned()
        {
            // Sequential tests already verify both orders, so only a few aligned
            // races are needed to exercise the poison-wins interleaving. Every
            // wait below is bounded; a timeout becomes an explicit assertion
            // failure instead of hanging the fixture.
            for (int iteration = 0; iteration < 3; iteration++)
            {
                NvencCaptureProcessState state = new NvencCaptureProcessState();

                using (Barrier barrier = new Barrier(2))
                {
                    bool drainReached = false;
                    bool poisonReached = false;

                    Thread drainThread = new Thread(() =>
                    {
                        drainReached = barrier.SignalAndWait(WatchdogTimeoutMs);
                        state.TryBeginDrain();
                    });
                    drainThread.IsBackground = true;

                    Thread poisonThread = new Thread(() =>
                    {
                        poisonReached = barrier.SignalAndWait(WatchdogTimeoutMs);
                        state.TryPoison();
                    });
                    poisonThread.IsBackground = true;

                    drainThread.Start();
                    poisonThread.Start();

                    Assert.That(drainThread.Join(WatchdogTimeoutMs), Is.True, "Drain thread did not finish within the watchdog timeout.");
                    Assert.That(poisonThread.Join(WatchdogTimeoutMs), Is.True, "Poison thread did not finish within the watchdog timeout.");
                    Assert.That(drainReached, Is.True, "Drain thread did not reach the barrier within the watchdog timeout.");
                    Assert.That(poisonReached, Is.True, "Poison thread did not reach the barrier within the watchdog timeout.");
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
                bool[] reachedBarrier = new bool[threadCount];

                for (int i = 0; i < threadCount; i++)
                {
                    int index = i;
                    threads[index] = new Thread(() =>
                    {
                        reachedBarrier[index] = barrier.SignalAndWait(WatchdogTimeoutMs);
                        state.TryPoison();
                    });
                    threads[index].IsBackground = true;
                    threads[index].Start();
                }

                for (int i = 0; i < threadCount; i++)
                {
                    Assert.That(threads[i].Join(WatchdogTimeoutMs), Is.True, "Thread " + i + " did not finish within the watchdog timeout.");
                }

                for (int i = 0; i < threadCount; i++)
                {
                    Assert.That(reachedBarrier[i], Is.True, "Thread " + i + " did not reach the barrier within the watchdog timeout.");
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
        public void HoldsOnlyStateAndGate_NoTokenReceiptGenerationHistory()
        {
            Type type = typeof(NvencCaptureProcessState);

            FieldInfo[] fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.That(fields.Length, Is.EqualTo(3));

            Type[] expected = { typeof(int), typeof(object) };
            foreach (FieldInfo field in fields)
            {
                Assert.That(expected, Does.Contain(field.FieldType), field.Name + " must be an int or object.");
            }
        }

        [Test]
        public void ProductionSources_NoSpinNoSleepNoThreadNoTaskNoIoNoUnityNoNative()
        {
            string root = Path.Combine(Application.dataPath, "..");
            string directory = Path.Combine(root, "Assets/Zantetsu/Runtime/Observability");
            string text =
                File.ReadAllText(Path.Combine(directory, "NvencCaptureProcessState.cs")) +
                File.ReadAllText(Path.Combine(directory, "NvencCaptureProcessStatus.cs"));

            Assert.That(text, Does.Not.Contain("lock ("));
            Assert.That(text, Does.Not.Contain("while ("));
            Assert.That(text, Does.Not.Contain("SpinWait"));
            Assert.That(text, Does.Not.Contain("Thread.Sleep"));
            Assert.That(text, Does.Not.Contain("ManualResetEvent"));
            Assert.That(text, Does.Not.Contain("AutoResetEvent"));
            Assert.That(text, Does.Not.Contain("WaitHandle"));
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
