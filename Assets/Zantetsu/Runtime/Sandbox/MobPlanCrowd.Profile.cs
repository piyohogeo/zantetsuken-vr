#if ZANTETSU_MOBPLAN_PROFILE
using System;
using System.Runtime.InteropServices;
using Zantetsu.Core.MobPlan;

namespace Zantetsu.Sandbox
{
    public sealed partial class MobPlanCrowd
    {
        public sealed class ProfileCycle
        {
            public double requested, completed, queue, compute, collect, cpu;
            public bool accepted;
            public CycleMetrics metrics;
        }
        public event Action<ProfileCycle> ProfileCompleted;
        public int ExpiredPlanCount
        {
            get
            {
                int count = 0;
                double now = PlanTime;
                foreach (var actor in actors.Values)
                    if (now < actor.plan.ValidFrom || now > actor.plan.ValidUntil) count++;
                return count;
            }
        }
        private void ReportProfile(Work job, double requested)
        {
            var product = job.result as Product;
            ProfileCompleted?.Invoke(new ProfileCycle
            {
                requested = requested, completed = PlanTime,
                queue = QueueMilliseconds, compute = ComputeMilliseconds, collect = CollectionMilliseconds,
                cpu = job.cpuMilliseconds, accepted = product != null && ReferenceEquals(lastAccepted, product.cycle),
                metrics = product?.cycle?.Metrics
            });
        }

        // Windows thread accounting, independent of Stopwatch wall time. NaN makes failure explicit.
        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);
        private static double ReadThreadCpuSeconds() => GetThreadTimes(GetCurrentThread(), out _, out _, out long kernel, out long user)
            ? (kernel + user) * 1e-7 : double.NaN;
    }
}
#endif
