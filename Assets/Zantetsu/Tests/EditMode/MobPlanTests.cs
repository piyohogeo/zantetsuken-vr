using System;
using System.Threading;
using NUnit.Framework;
using Zantetsu.Core.MobPlan;
using Zantetsu.MeshCut;
using UnityEngine;
using ThreadPriority = System.Threading.ThreadPriority;

namespace Zantetsu.Core.Tests
{
    public sealed class MobPlanTests
    {
        private sealed class Map : WalkableMap
        {
            public Func<float, float, bool> Free;
            public override float ClearanceAt(float x, float z) => Free(x, z) ? 10 : 0;
            public override bool IsWalkableAt(float x, float z) => Free(x, z);
        }
        [TestCase(true, true, true, 1, 1)]
        [TestCase(false, true, true, 1, 0)]
        [TestCase(false, false, true, 0, 1)]
        [TestCase(false, false, false, 0, 0)]
        public void CircleUsesProbeAxisOrder(bool full, bool xOnly, bool zOnly, float x, float z)
        {
            var map = new Map { Free = (a, b) => a == 1 && b == 1 ? full : a == 1 ? xOnly : zOnly };
            var result = CircleLocomotion.Move(map, 1.5f, 0, 0, 1, 1);
            Assert.That(result.X, Is.EqualTo(x)); Assert.That(result.Z, Is.EqualTo(z));
        }
        [Test]
        public void PublishedPlanOwnsSegmentsAndResolvesArbitraryOrderWithoutClampingOutside()
        {
            var dataset = new ClipDataset { SampleHz = 1,
                Clips = new[] { new ClipDescriptor { Duration = 1, SampleCount = 2, EndZ = 1, RangeFrom = .25f } },
                T = new[] { 0f, 1f }, X = new[] { 0f, 0f }, Z = new[] { 0f, 1f }, Yaw = new[] { 0f, 0f } };
            var draft = new AgentPlan { AgentId = 7 };
            draft.Segments.Add(new PlanSegment(0, 5, 6, new Pose2(3, 4, 0)));
            var plan = new PublishedMobPlan(draft, 2);
            draft.Segments.Clear();
            foreach (double t in new[] { 5.9, 5.1, 6, 5, 5.9 })
            {
                Assert.That(plan.TryResolve(t, dataset, out _, out double source, out var root), Is.True);
                Assert.That(source, Is.EqualTo(.25 + t - 5).Within(1e-6));
                Assert.That(root.Z, Is.EqualTo(4 + t - 5).Within(1e-5));
            }
            Assert.That(plan.TryResolve(4.99, dataset, out _, out _, out _), Is.False);
            Assert.That(plan.TryResolve(6.01, dataset, out _, out _, out _), Is.False);
            Assert.That(plan.TryResolve(double.NaN, dataset, out _, out _, out _), Is.False);
        }
        private sealed class Work : IDispatchWork
        {
            public Action Run;
            public readonly ManualResetEventSlim Began = new ManualResetEventSlim();
            public ThreadPriority Priority;
            public void Begin() { Priority = Thread.CurrentThread.Priority; Began.Set(); Run?.Invoke(); }
            public bool IsComplete => true;
            public void Collect(WorkCompletion completion) { }
        }
        [Test]
        public void MapPlayerTurnsWhenTranslationIsBlockedAndDoesNotClampTrackedHead()
        {
            var root = new GameObject("player");
            var head = new GameObject("tracked head");
            try
            {
                head.transform.SetParent(root.transform, false);
                head.transform.localPosition = new Vector3(2, 1.7f, 0);
                var player = root.AddComponent<PlayerLocomotion>();
                player.ConfigureMap(new Map { Free = (x, z) => false }, Vector3.zero);
                var tracked = head.transform.localPosition;
                player.TryRequest(new Pose(Vector3.one, Quaternion.Euler(0, 90, 0)));
                Assert.That(root.transform.position, Is.EqualTo(Vector3.zero));
                Assert.That(Mathf.DeltaAngle(root.transform.eulerAngles.y, 90), Is.EqualTo(0).Within(.001));
                Assert.That(head.transform.localPosition, Is.EqualTo(tracked));
            }
            finally { UnityEngine.Object.DestroyImmediate(root); }
        }
        [Test]
        public void SpeculationCannotFillThePlanningWaitingSlot()
        {
            var urgent = new UnityJobWorkExecutor(1);
            using var geometry = new WorkerPoolExecutor(WorkDestination.GeometryPool, 1, ThreadPriority.BelowNormal, 1);
            using var background = new WorkerPoolExecutor(WorkDestination.BackgroundPool, 1, ThreadPriority.Lowest, 1);
            using var planning = WorkerPoolExecutor.PlanningPool(1);
            var dispatcher = new SharedWorkDispatcher(4, 1, 4, urgent, geometry, background, planning);
            Assert.That(dispatcher.TryEnqueue(WorkPurpose.Speculative, new Work(), out _), Is.True);
            Assert.That(dispatcher.TryEnqueue(WorkPurpose.Maintenance, new Work(), out _), Is.True);
            Assert.That(dispatcher.TryEnqueue(WorkPurpose.Maintenance, new Work(), out _), Is.False);
            Assert.That(dispatcher.TryEnqueue(WorkPurpose.MobPlanning, new Work(), out _), Is.True);
            Assert.That(dispatcher.TryEnqueue(WorkPurpose.AdmittedPhysics, new Work(), out _), Is.True);
            dispatcher.Shutdown(10000);
        }
        [Test]
        public void DedicatedPlanningRunsWhileBackgroundCapacityIsHeld()
        {
            using var release = new ManualResetEventSlim();
            using var background = new WorkerPoolExecutor(WorkDestination.BackgroundPool, 1, ThreadPriority.Lowest, 1);
            using var planning = WorkerPoolExecutor.PlanningPool(1);
            var blocked = new Work { Run = () => release.Wait(10000) };
            var plan = new Work();
            try
            {
                Assert.That(background.TryAccept(blocked), Is.True);
                Assert.That(blocked.Began.Wait(10000), Is.True);
                Assert.That(background.CanAccept, Is.False);
                Assert.That(WorkPurposes.DestinationOf(WorkPurpose.MobPlanning), Is.EqualTo(WorkDestination.PlanningPool));
                Assert.That(planning.TryAccept(plan), Is.True);
                Assert.That(plan.Began.Wait(10000), Is.True);
                Assert.That(plan.Priority, Is.EqualTo(ThreadPriority.Normal));
            }
            finally { release.Set(); }
        }
    }
}
