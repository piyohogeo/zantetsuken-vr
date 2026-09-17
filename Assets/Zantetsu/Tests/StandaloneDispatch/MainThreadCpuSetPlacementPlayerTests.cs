using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using NUnit.Framework;
using UnityEngine.TestTools;
using Zantetsu.Core;
using Zantetsu.MeshCut;

namespace Zantetsu.MeshCut.StandaloneTests
{
    /// <summary>
    /// The main thread's CPU Set placement (DESIGN 4.3 "Mainのコア配置", D-083, chapter 15) on the real machine, in
    /// an IL2CPP player: the real topology, what is chosen from it, the assignment applied to the player's own main
    /// thread, read back, held across several update opportunities, and put back.
    /// <para>
    /// What this can and cannot say. A CPU Set assignment is a soft preference the scheduler respects, so the
    /// samples of where the main thread ran are **observations of an actual placement**, not proof that it will
    /// never be scheduled on an efficiency core. Nothing here is a timing assertion: no fixed delay, no speed-up and
    /// no OS scheduling order is a pass condition, and nothing asserts a whole matrix of machines. On a machine that
    /// is not heterogeneous the placement is expected to do nothing at all, and this test says so rather than
    /// pretending the heterogeneous path was confirmed.
    /// </para>
    /// <para>
    /// Non-XR: nothing here needs a headset. The placement only ever touches the calling thread, and this checks
    /// that by reading, before and after, the process default CPU Sets, the main thread's own group affinity, and a
    /// Geometry pool worker's OS priority.
    /// </para>
    /// </summary>
    public class MainThreadCpuSetPlacementPlayerTests
    {
        private const int Patience = 20000;
        private const int SampleOpportunities = 12;
        private const int OsBelowNormal = -1;

        [StructLayout(LayoutKind.Sequential)]
        private struct GroupAffinity
        {
            public UIntPtr mask;
            public ushort group;
            public ushort reserved0;
            public ushort reserved1;
            public ushort reserved2;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentProcessorNumber();

        [DllImport("kernel32.dll")]
        private static extern int GetThreadPriority(IntPtr thread);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity groupAffinity);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessDefaultCpuSets(
            IntPtr process, [Out] uint[] cpuSetIds, uint cpuSetIdCount, out uint requiredIdCount);

        /// <summary>The process default CPU Sets, read only: this must be the same before and after.</summary>
        private static string ReadProcessDefaultCpuSets()
        {
            if (!GetProcessDefaultCpuSets(GetCurrentProcess(), null, 0, out uint required))
            {
                if (required == 0)
                {
                    return "unreadable";
                }
            }

            if (required == 0)
            {
                return "none";
            }

            var ids = new uint[required];
            if (!GetProcessDefaultCpuSets(GetCurrentProcess(), ids, required, out uint filled))
            {
                return "unreadable";
            }

            Array.Sort(ids, 0, (int)filled);
            var text = new StringBuilder();
            for (int i = 0; i < filled; i++)
            {
                if (i > 0)
                {
                    text.Append(' ');
                }

                text.Append(ids[i]);
            }

            return text.ToString();
        }

        private static string ReadMainGroupAffinity()
        {
            return GetThreadGroupAffinity(GetCurrentThread(), out GroupAffinity affinity)
                ? "group " + affinity.group + " mask 0x" + ((ulong)affinity.mask).ToString("x")
                : "unreadable";
        }

        private static string Join(IReadOnlyList<uint> ids)
        {
            if (ids == null || ids.Count == 0)
            {
                return "none";
            }

            var text = new StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(' ');
                }

                text.Append(ids[i]);
            }

            return text.ToString();
        }

        /// <summary>
        /// One piece of pool work that reports the OS priority of the worker that ran it. It signals nothing of its
        /// own: a worker publishes the work as ended only after <c>Begin</c> has returned, so the pool handing it
        /// back is the one completion to wait for. Nothing here holds a synchronisation resource that could be
        /// released while a worker is still inside the work.
        /// </summary>
        private sealed class PriorityProbeWork : IDispatchWork
        {
            public int osPriority = int.MinValue;

            public void Begin()
            {
                osPriority = GetThreadPriority(GetCurrentThread());
            }

            public bool IsComplete => true;

            public void Collect(WorkCompletion completion)
            {
                Assert.That(completion.outcome, Is.EqualTo(WorkOutcome.Finished), "the probe work ran to the end");
            }
        }

        /// <summary>
        /// One non-blocking attempt to take the probe back from the pool. False simply means it has not been
        /// published as ended yet, which is ordinary: the caller gives up an update opportunity and asks again.
        /// </summary>
        private static bool TryCollectProbe(WorkerPoolExecutor pool, PriorityProbeWork expected)
        {
            if (!pool.TryTakeFinished(out IDispatchWork taken, out WorkCompletion completion))
            {
                return false;
            }

            Assert.That(ReferenceEquals(taken, expected), Is.True, "the pool gave back the probe work");
            taken.Collect(completion);
            return true;
        }

        /// <summary>
        /// The whole cycle on this machine: read the topology, choose, apply to the player's main thread, read back,
        /// sample where the main thread runs over several update opportunities, check that nothing else moved, and
        /// put the assignment back.
        /// </summary>
        [UnityTest]
        public IEnumerator ThePlacement_ChoosesAppliesHoldsAndRestores_OnThisMachine()
        {
            var os = new WindowsMainThreadCpuSets();
            var placement = new MainThreadCpuSetPlacement(os);

            string defaultsBefore = ReadProcessDefaultCpuSets();
            string mainAffinityBefore = ReadMainGroupAffinity();
            WorkerPoolExecutor pool = WorkerPoolExecutor.GeometryPool(4);
            try
            {
                Assert.That(pool.WaitUntilStartupFinished(Patience), Is.True, "the geometry pool started");
                Assert.That(pool.IsFaulted, Is.False);
                var probeBefore = new PriorityProbeWork();
                Assert.That(pool.TryAccept(probeBefore), Is.True, "the probe work is accepted");
                DateTime probeDeadline = DateTime.UtcNow.AddMilliseconds(Patience);
                while (!TryCollectProbe(pool, probeBefore))
                {
                    Assert.That(
                        DateTime.UtcNow, Is.LessThan(probeDeadline),
                        "the pool did not give the probe work back within the safety timeout");
                    yield return null;
                }

                int poolPriorityBefore = probeBefore.osPriority;

                var records = new List<CpuSetRecord>();
                Assert.That(os.TryReadSystemCpuSets(records), Is.True, "the system CPU sets are readable here");
                Assert.That(os.TryReadProcessAffinityMask(out ulong processMask), Is.True);

                var before = new List<uint>();
                Assert.That(os.TryReadSelectedCpuSets(before), Is.True, "and so is the main thread's own assignment");

                TestContext.WriteLine("processor groups        : " + os.ProcessorGroupCount);
                TestContext.WriteLine("cpu set records         : " + records.Count);
                TestContext.WriteLine("process affinity mask   : 0x" + processMask.ToString("x"));
                TestContext.WriteLine("process default cpu sets: " + defaultsBefore);
                TestContext.WriteLine("main group affinity     : " + mainAffinityBefore);
                TestContext.WriteLine("main original assignment: " + Join(before));
                TestContext.WriteLine("main managed thread id  : " + Thread.CurrentThread.ManagedThreadId);
                for (int i = 0; i < records.Count; i++)
                {
                    TestContext.WriteLine("  " + records[i]);
                }

                CpuSetPlacementOutcome outcome = placement.Apply();
                TestContext.WriteLine("outcome                 : " + outcome);
                TestContext.WriteLine("problem                 : " + (placement.Problem ?? "none"));
                TestContext.WriteLine("fastest/slowest class   : " + placement.FastestEfficiencyClass + " / " + placement.SlowestEfficiencyClass);
                TestContext.WriteLine("selected cpu set ids    : " + Join(placement.Selected));
                TestContext.WriteLine("read back               : " + Join(placement.ReadBack));
                TestContext.WriteLine("excluded, reserved      : " + placement.ExcludedAsAllocatedElsewhere);
                TestContext.WriteLine("excluded, affinity      : " + placement.ExcludedByProcessAffinity);
                TestContext.WriteLine("parked among selected   : " + placement.ParkedAmongSelected);
                TestContext.WriteLine("changed                 : " + placement.Changed);
                TestContext.WriteLine("placed on thread        : " + placement.PlacedOnThreadId);

                if (outcome == CpuSetPlacementOutcome.NotNeeded)
                {
                    // Not a heterogeneous machine. The contract here is that nothing was changed; the heterogeneous
                    // path is simply not confirmed by this run, and is not claimed to be.
                    Assert.That(placement.Changed, Is.False, "nothing is changed on a machine that needs no placement");
                    Assert.That(placement.Selected, Is.Empty);
                    Assert.That(
                        placement.FastestEfficiencyClass, Is.EqualTo(placement.SlowestEfficiencyClass),
                        "one efficiency class is what makes it unnecessary");
                    TestContext.WriteLine(
                        "NOT CONFIRMED HERE: this machine is not heterogeneous, so applying a placement was not exercised.");
                    yield break;
                }

                Assert.That(
                    outcome, Is.EqualTo(CpuSetPlacementOutcome.Applied),
                    "the placement was applied on this machine - " + (placement.Problem ?? "no problem reported"));
                Assert.That(placement.Changed, Is.True);
                Assert.That(placement.Selected, Is.Not.Empty);
                Assert.That(placement.ReadBack, Is.EqualTo(placement.Selected), "and reads back as what was asked");
                Assert.That(
                    placement.PlacedOnThreadId, Is.EqualTo(Thread.CurrentThread.ManagedThreadId),
                    "on the main thread, which is where it is applied and restored");

                // Where the main thread actually runs while the assignment is held. An observation over several real
                // update opportunities, mapped to the efficiency class of the processor it was seen on.
                var classOfCpu = new Dictionary<uint, int>();
                for (int i = 0; i < records.Count; i++)
                {
                    classOfCpu[records[i].logicalProcessorIndex] = records[i].efficiencyClass;
                }

                var seen = new List<string>();
                int onFastest = 0;
                int onSomethingElse = 0;
                for (int i = 0; i < SampleOpportunities; i++)
                {
                    yield return null;
                    uint cpu = GetCurrentProcessorNumber();
                    int efficiencyClass = classOfCpu.TryGetValue(cpu, out int found) ? found : -1;
                    seen.Add("cpu " + cpu + " (class " + efficiencyClass + ")");
                    if (efficiencyClass == placement.FastestEfficiencyClass)
                    {
                        onFastest++;
                    }
                    else
                    {
                        onSomethingElse++;
                    }

                    // The assignment must still be the one that was applied, at every one of these.
                    var held = new List<uint>();
                    Assert.That(os.TryReadSelectedCpuSets(held), Is.True);
                    Assert.That(held, Is.EqualTo(placement.Selected), "the assignment is still held at sample " + i);
                }

                TestContext.WriteLine("main thread samples     : " + string.Join(", ", seen.ToArray()));
                TestContext.WriteLine(
                    "samples on fastest class: " + onFastest + " of " + SampleOpportunities
                    + " (" + onSomethingElse + " elsewhere)");
                Assert.That(
                    onFastest, Is.GreaterThan(0),
                    "the main thread was observed on the class it was placed on - samples: " + string.Join(", ", seen.ToArray()));

                // Nothing of anyone else's was touched while the placement was held.
                Assert.That(
                    ReadProcessDefaultCpuSets(), Is.EqualTo(defaultsBefore), "the process default CPU sets are untouched");
                Assert.That(
                    ReadMainGroupAffinity(), Is.EqualTo(mainAffinityBefore),
                    "and so is the main thread's own hard affinity");
                var probeAfter = new PriorityProbeWork();
                Assert.That(pool.TryAccept(probeAfter), Is.True, "a second probe work is accepted");
                probeDeadline = DateTime.UtcNow.AddMilliseconds(Patience);
                while (!TryCollectProbe(pool, probeAfter))
                {
                    Assert.That(
                        DateTime.UtcNow, Is.LessThan(probeDeadline),
                        "the pool did not give the second probe work back within the safety timeout");
                    yield return null;
                }

                int poolPriorityAfter = probeAfter.osPriority;
                Assert.That(
                    poolPriorityAfter, Is.EqualTo(poolPriorityBefore), "and a pool worker's OS priority is unchanged");
                Assert.That(poolPriorityAfter, Is.EqualTo(OsBelowNormal), "which is the priority that pool was given");
                TestContext.WriteLine("pool worker os priority : " + poolPriorityBefore + " -> " + poolPriorityAfter);

                CpuSetRestoreOutcome restored = placement.Restore();
                TestContext.WriteLine("restore outcome         : " + restored);
                Assert.That(restored, Is.EqualTo(CpuSetRestoreOutcome.Restored), placement.Problem);
                Assert.That(placement.Changed, Is.False);

                var after = new List<uint>();
                Assert.That(os.TryReadSelectedCpuSets(after), Is.True);
                Assert.That(after, Is.EqualTo(before), "the main thread's assignment is exactly what it was");
                TestContext.WriteLine("main assignment after   : " + Join(after));
                Assert.That(
                    ReadProcessDefaultCpuSets(), Is.EqualTo(defaultsBefore), "with the process defaults still untouched");
                Assert.That(ReadMainGroupAffinity(), Is.EqualTo(mainAffinityBefore));
            }
            finally
            {
                // Whatever happened above, the main thread is not left placed.
                if (placement.Changed)
                {
                    TestContext.WriteLine("restore in teardown     : " + placement.Restore());
                }

                pool.StopAndConfirm(Patience);
                pool.Dispose();
            }
        }
    }
}
