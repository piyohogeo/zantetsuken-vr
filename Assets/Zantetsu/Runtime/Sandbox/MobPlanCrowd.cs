using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using UnityEngine;
using Zantetsu.Core;
using Zantetsu.Core.Animation;
using Zantetsu.Core.MobPlan;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;
using Debug = UnityEngine.Debug;

namespace Zantetsu.Sandbox
{
    /// <summary>One crowd's publication boundary. Numerical simulation is confined to the dedicated planning worker.</summary>
    [DefaultExecutionOrder(-75)]
    public sealed partial class MobPlanCrowd : MonoBehaviour
    {
        [SerializeField] private MobPlanAssets assets;
        [SerializeField] private CutWorldRoot world;
        [SerializeField] private PlayerLocomotion player;
        [SerializeField] private Transform view;
        [SerializeField] private Vector3 mapOffset = new Vector3(390, .1f, 150);
        [SerializeField] private SandboxNpcCharacter[] initialCharacters;
        [SerializeField] private GameObject replacementTemplate;
        private sealed class Actor { public SandboxNpcCharacter character; public PoseTablePlayer pose; public PublishedMobPlan plan; }
        private readonly Dictionary<int, Actor> actors = new Dictionary<int, Actor>();
        public readonly struct CycleTiming
        {
            public readonly double queue, compute, collect;
            public CycleTiming(double queue, double compute, double collect) { this.queue = queue; this.compute = compute; this.collect = collect; }
        }
        private readonly List<CycleTiming> timings = new List<CycleTiming>();
        public IReadOnlyList<CycleTiming> Timings => timings;
        public bool TryGetPlan(int agentId, out PublishedMobPlan plan)
        { if (actors.TryGetValue(agentId, out var actor)) { plan = actor.plan; return true; } plan = null; return false; }
        public bool TryEvaluate(int agentId, double gameTime, out PoseTable table, out double sourceTime, out Pose root)
        {
            table = null; sourceTime = 0; root = default;
            if (!ready || !actors.TryGetValue(agentId, out var actor) || !bank.TryEvaluate(actor.plan, gameTime - epoch, simulation.Dataset, out table, out sourceTime, out root)) return false;
            root.position += mapOffset;
            return true;
        }
        private LocomotionSimulation simulation;
        private MobPoseBank bank;
        private bool loading, busy, ready, stopped, loadingFailed;
        private Work pendingLoad;
        private int generation;
        private CycleResult lastAccepted;
        private double epoch, nextRequest;
        private Vector3 previousPlayer;
        private Vector3 playerVelocity;
        public int PublishedCycles { get; private set; }
        public int StaleCycles { get; private set; }
        public int FailedCycles { get; private set; }
        public double QueueMilliseconds { get; private set; }
        public double ComputeMilliseconds { get; private set; }
        public double CollectionMilliseconds { get; private set; }
        public bool IsReady => ready;
        public bool PlannerBusy => busy;
        public int LiveCount => actors.Count;
        public WalkableMap Map => ready ? simulation.Map : null;
        public double PlanTime => Time.timeAsDouble - epoch;

        private void Awake()
        {
            foreach (var npc in initialCharacters)
            {
                npc.enabled = false;
                npc.CharacterRoot.SetActive(false);
            }
            if (replacementTemplate != null) replacementTemplate.SetActive(false);
        }

        private void Update()
        {
            if (stopped || world == null || !world.IsReady) return;
            if (!ready)
            {
                if (!loading && !loadingFailed) QueueLoad();
                return;
            }
            RetireWithdrawn();
            Vector3 position = player.transform.position;
            playerVelocity = Time.deltaTime > 0 ? (position - previousPlayer) / Time.deltaTime : Vector3.zero;
            previousPlayer = position;
            double now = PlanTime;
            foreach (var actor in actors.Values) ApplyRoot(actor, now);
            if (!busy && now >= nextRequest) QueueCycle((float)now);
        }

        private void QueueLoad()
        {
            if (pendingLoad != null)
            { loading = world.Dispatcher.TryEnqueue(WorkPurpose.MobPlanning, pendingLoad, out _); return; }
            // Unity asset access only during loading, before handing owned byte arrays to the worker.
            var data = assets.dataset.bytes;
            var polygons = assets.polygons.bytes;
            var tables = assets.poseTables.Select(t => t.bytes).ToArray();
            var load = new Work(() =>
            {
                var dataset = ClipDataset.Read(data);
                var map = PolygonWalkable.Read(polygons, -220, -170, 300, 300);
                map.BuildAllTiles();
                var made = new LocomotionSimulation(dataset, MobPlanPreset.Planner()) { AutoPlan = false, ExternalPlayer = true };
                made.Reset(MobPlanPreset.Scenario(null), map);
                return new Loaded { simulation = made, bank = new MobPoseBank(dataset, tables) };
            }, (work, completion) =>
            {
                loading = false;
                if (stopped) return;
                if (!completion.Succeeded) { loadingFailed = true; Fail(completion.failure); return; }
                pendingLoad = null;
                var loaded = (Loaded)work.result;
                simulation = loaded.simulation; bank = loaded.bank;
                epoch = Time.timeAsDouble;
                player.transform.position = new Vector3(simulation.FocusX, 0, simulation.FocusZ) + mapOffset;
                player.ConfigureMap(simulation.Map, mapOffset);
                previousPlayer = player.transform.position;
                for (int i = 0; i < simulation.Agents.Count; i++)
                    AddActor(simulation.Agents[i].Id, initialCharacters[i], new PublishedMobPlan(simulation.Agents[i].Plan, generation));
                ready = true;
#if ZANTETSU_MOBPLAN_PROFILE
                Debug.Log($"MOBPLAN LOAD queue_ms={work.started * 1000:F3} wall_ms={(work.ended - work.started) * 1000:F3} cpu_ms={work.cpuMilliseconds:F3}");
#endif
                Debug.Log($"MobPlan ready: {actors.Count} NPC, {simulation.Dataset.Count} nodes, {bank.Tables.Count} tables, 66 bones");
            });
            pendingLoad = load;
            loading = world.Dispatcher.TryEnqueue(WorkPurpose.MobPlanning, load, out _);
        }
        private sealed class Loaded { public LocomotionSimulation simulation; public MobPoseBank bank; }

        private void AddActor(int id, SandboxNpcCharacter character, PublishedMobPlan plan)
        {
            var pose = character.CharacterRoot.GetComponent<PoseTablePlayer>();
            pose.Configure(assets.initialPose, pose.ModelRoot, 0, true);
            pose.TableBank = bank.Tables;
            var actor = new Actor { character = character, pose = pose, plan = plan };
            pose.PlanSource = (double target, out PoseTable table, out double source) =>
                bank.TryEvaluate(actor.plan, target - epoch, simulation.Dataset, out table, out source, out _);
            actors.Add(id, actor);
            ApplyRoot(actor, PlanTime);
            character.CharacterRoot.SetActive(true);
            character.enabled = true;
        }

        private void RetireWithdrawn()
        {
            List<int> gone = null;
            foreach (var pair in actors)
                if (pair.Value.character == null || (pair.Value.character.Handle != null && pair.Value.character.Handle.IsWithdrawn))
                { if (gone == null) gone = new List<int>(); gone.Add(pair.Key); }
            if (gone == null) return;
            foreach (int id in gone) actors.Remove(id);
            generation++;
            nextRequest = 0;
        }

        private void ApplyRoot(Actor actor, double now)
        {
            if (!bank.TryEvaluate(actor.plan, now, simulation.Dataset, out _, out _, out var root)) return;
            actor.character.CharacterRoot.transform.SetPositionAndRotation(root.position + mapOffset, root.rotation);
            // Kinematic Rigidbody velocity setters are unsupported. Supply the cut's inherited motion explicitly.
            if (actor.character.Handle != null && bank.TryEvaluate(actor.plan, now + .01, simulation.Dataset, out _, out _, out var future))
            {
                Quaternion delta = future.rotation * Quaternion.Inverse(root.rotation);
                delta.ToAngleAxis(out float angle, out Vector3 axis);
                if (angle > 180) angle -= 360;
                Vector3 angular = float.IsFinite(axis.x) ? axis * (angle * Mathf.Deg2Rad * 100) : Vector3.zero;
                Vector3 comOffset = actor.character.MotionBody.worldCenterOfMass - (root.position + mapOffset);
                actor.character.Handle.SetPlannedMotion((future.position - root.position) * 100 + Vector3.Cross(angular, comOffset), angular);
            }
        }

        private sealed class Product
        {
            public CycleResult cycle;
            public Dictionary<int, PublishedMobPlan> plans;
            public List<int> relocated;
        }
        private void QueueCycle(float now)
        {
            int capturedGeneration = generation;
            var baseline = actors.ToDictionary(p => p.Key, p => p.Value.plan);
            Vector3 p = player.transform.position - mapOffset, velocity = playerVelocity;
            float yaw = view != null ? view.eulerAngles.y : player.transform.eulerAngles.y;
            var accepted = lastAccepted;
            var stopwatch = Stopwatch.StartNew();
            var work = new Work(() =>
            {
                if (stopwatch.Elapsed.TotalSeconds >= 1.9) return null;
                if (accepted != null) simulation.AcknowledgeExternal(accepted);
                var snapshot = simulation.PrepareExternal(now, baseline, p.x, p.z, velocity.x, velocity.z, yaw);
                double remaining = 1.9 - stopwatch.Elapsed.TotalSeconds;
                if (remaining <= 0) return null;
                var config = simulation.Config.Clone();
                config.cycleBudgetMilliseconds = Math.Min(config.cycleBudgetMilliseconds, Math.Max(1, (int)(remaining * 1000)));
                var result = LocomotionPlanner.RunCycle(simulation.Dataset, simulation.Map, config, snapshot);
                if (!result.Published) return new Product { cycle = result };
                var moved = new List<int>();
                foreach (var agent in snapshot.Agents)
                    if (!baseline.TryGetValue(agent.Id, out var old) ||
                        (old.TryResolve(now, simulation.Dataset, out _, out _, out var at) &&
                         Pose2.Distance(at, AgentPlan.PoseAt(simulation.Dataset, agent.Plan.Segments, now)) > .1f)) moved.Add(agent.Id);
                return new Product { cycle = result, relocated = moved,
                    plans = result.NewPlans.ToDictionary(pair => pair.Key, pair => new PublishedMobPlan(pair.Value, capturedGeneration)) };
            }, (job, completion) =>
            {
                busy = false;
                if (stopped) return;
                QueueMilliseconds = job.started * 1000;
                ComputeMilliseconds = (job.ended - job.started) * 1000;
                CollectionMilliseconds = (job.watch.Elapsed.TotalSeconds - job.ended) * 1000;
                timings.Add(new CycleTiming(QueueMilliseconds, ComputeMilliseconds, CollectionMilliseconds));
                if (timings.Count > 600) timings.RemoveAt(0);
                RetireWithdrawn();
                if (!completion.Succeeded) { Fail(completion.failure); return; }
                var product = job.result as Product;
                if (product == null || generation != capturedGeneration || (product.cycle.Published && product.cycle.MinCommitUntil <= PlanTime))
                { StaleCycles++; nextRequest = PlanTime; return; }
                if (!product.cycle.Published) { FailedCycles++; return; }
                foreach (int id in product.relocated)
                {
                    if (!product.plans[id].TryResolve(PlanTime, simulation.Dataset, out _, out _, out var proposed)
                        || Visible(proposed.X, proposed.Z)
                        || (actors.TryGetValue(id, out var old) && Visible(old.character.CharacterRoot.transform.position.x - mapOffset.x, old.character.CharacterRoot.transform.position.z - mapOffset.z)))
                    { StaleCycles++; nextRequest = PlanTime; return; }
                }
                foreach (var pair in product.plans)
                {
                    if (actors.TryGetValue(pair.Key, out var actor)) actor.plan = pair.Value;
                    else if (replacementTemplate != null)
                    {
                        GameObject replacement = Instantiate(replacementTemplate);
                        var npc = replacement.GetComponentInChildren<SandboxNpcCharacter>(true);
                        AddActor(pair.Key, npc, pair.Value);
                        replacement.SetActive(true);
                    }
                }
                lastAccepted = product.cycle;
                PublishedCycles++;
            });
#if ZANTETSU_MOBPLAN_PROFILE
            work.measured = job => ReportProfile(job, now);
#endif
            if (world.Dispatcher.TryEnqueue(WorkPurpose.MobPlanning, work, out _))
            { busy = true; lastAccepted = null; nextRequest = now + 1; }
        }

        private bool Visible(float x, float z)
        {
            Vector3 eye = (view != null ? view.position : player.transform.position) - mapOffset;
            float dx = x - eye.x, dz = z - eye.z;
            if (dx * dx + dz * dz > 50 * 50) return false;
            float heading = view != null ? view.eulerAngles.y : player.transform.eulerAngles.y;
            return Mathf.Abs(Mathf.DeltaAngle(heading, Mathf.Atan2(dx, dz) * Mathf.Rad2Deg)) <= 55
                && !simulation.Map.SegmentBlocked(eye.x, eye.z, x, z);
        }
        private void Fail(Exception failure) { FailedCycles++; if (failure != null) Debug.LogException(failure, this); }
        private void OnDestroy() { stopped = true; generation++; }

        private sealed class Work : IDispatchWork
        {
            private readonly Func<object> run;
            private readonly Action<Work, WorkCompletion> collect;
            public readonly Stopwatch watch = Stopwatch.StartNew();
            public object result;
            public double started, ended;
            public Work(Func<object> run, Action<Work, WorkCompletion> collect) { this.run = run; this.collect = collect; }
            public void Begin()
            {
                started = watch.Elapsed.TotalSeconds;
#if ZANTETSU_MOBPLAN_PROFILE
                double cpuStart = ReadThreadCpuSeconds();
#endif
                try { result = run(); }
                finally
                {
                    ended = watch.Elapsed.TotalSeconds;
#if ZANTETSU_MOBPLAN_PROFILE
                    cpuMilliseconds = (ReadThreadCpuSeconds() - cpuStart) * 1000;
#endif
                }
            }
            public bool IsComplete => true;
            public void Collect(WorkCompletion completion)
            {
                collect(this, completion);
#if ZANTETSU_MOBPLAN_PROFILE
                measured?.Invoke(this);
#endif
            }
#if ZANTETSU_MOBPLAN_PROFILE
            [NonSerialized] public double cpuMilliseconds;
            [NonSerialized] public Action<Work> measured;
#endif
        }
    }
}
