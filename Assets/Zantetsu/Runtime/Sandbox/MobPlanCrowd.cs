using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Unity.Profiling;
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

        [Tooltip("Prepared slots kept besides the ones on show: a replacement takes one of these, never a new object.")]
        [SerializeField] private int spareSlots = 10;
        public const string SpareSlotsArgument = "-mobPlanSpareSlots";
        private sealed class Actor { public SandboxNpcCharacter character; public PoseTablePlayer pose; public PublishedMobPlan plan; }
        private readonly Dictionary<int, Actor> actors = new Dictionary<int, Actor>();
        public readonly struct CycleTiming
        {
            public readonly double queue, compute, collect;
            // Observation: the frames the cycle was queued and collected in, what became of it, and the replacements it added.
            public readonly int queuedFrame, collectedFrame, replacements;
            public readonly string outcome;
            public CycleTiming(double queue, double compute, double collect) : this(queue, compute, collect, -1, -1, null, 0) { }
            public CycleTiming(double queue, double compute, double collect, int queuedFrame, int collectedFrame, string outcome, int replacements)
            {
                this.queue = queue; this.compute = compute; this.collect = collect;
                this.queuedFrame = queuedFrame; this.collectedFrame = collectedFrame; this.outcome = outcome; this.replacements = replacements;
            }
        }
        private readonly List<CycleTiming> timings = new List<CycleTiming>();
        public IReadOnlyList<CycleTiming> Timings => timings;

        /// <summary>Observation: an actor taken into the crowd (its plan id, its character, whether it is a replacement).</summary>
        public event Action<int, SandboxNpcCharacter, bool> ActorAdded;

        /// <summary>
        /// Observation: an actor retired from the crowd (its plan id and character) after its cut withdrew it. Raised just
        /// before it leaves the plan, so an observer can still read what the plan gives it at this moment
        /// (<see cref="TryEvaluate"/>); what follows -- the plan removal, the level of detail and the slot's return -- is
        /// the same whoever listens.
        /// </summary>
        public event Action<int, SandboxNpcCharacter> ActorRetired;

        public int ReplacementsAdded { get; private set; }

        // ----- the prepared slots --------------------------------------------------------------------------------------
        // Every character this crowd shows is one of a fixed set of slots, all prepared before the scenario begins: the
        // scene's own characters and the spares made from the template. A replacement activates a free prepared slot; a
        // cut character's slot comes back once its cut refers to nothing of it any more, is prepared again by the refill
        // on the frames after that (as its cap and the frame allow), and is free again. With no free slot a replacement
        // waits in the refill -- the crowd is short for a while -- and nothing is made or waited for on the spot.
        private readonly MobPlanSlotPool<SandboxNpcCharacter> pool = new MobPlanSlotPool<SandboxNpcCharacter>();
        // One share per model (SandboxNpcCharacter.SlotShareKey): the parsed intake and hulls and the fixed-scale mesh of
        // one model are shared by that model's slots only.
        private readonly Dictionary<string, SandboxNpcCharacter.SlotShare> slotShares = new Dictionary<string, SandboxNpcCharacter.SlotShare>();
        private bool poolStarted, poolPrepared, loaded;
        private double poolStartSeconds;
        private long poolStartAllocated, poolStartMono;
        public int SlotCount => pool.SlotCount;
        public IReadOnlyList<SandboxNpcCharacter> Slots => pool.Slots;
        public int FreeSlots => pool.FreeCount;
        public int ReturningSlots => pool.ReturningCount;
        public int PreparingSlots => pool.PreparingCount;
        public int BrokenSlots => pool.BrokenCount;
        public bool PoolPrepared => poolPrepared;
        /// <summary>The refill's tries to start a waiting individual that found no free slot (it waits on, once a frame at most).</summary>
        public int WaitedForSlot { get; private set; }
        /// <summary>Activations of a slot that had carried an individual before.</summary>
        public int ReusedActivations { get; private set; }
        public double PoolPrepareSeconds { get; private set; }
        public long PoolAllocatedBytes { get; private set; }
        public long PoolMonoBytes { get; private set; }
        /// <summary>Reads of a model's parsed intake and hulls from its share, over every model; and how many shares (models).</summary>
        public int SharedReads => slotShares.Values.Sum(s => s.SharedReads);
        public int SlotShareCount => slotShares.Count;
        private static readonly ProfilerMarker s_pool = new ProfilerMarker("Zantetsu.MobPlan.Pool");

        // ----- the refill ----------------------------------------------------------------------------------------------
        // A published individual no slot carries yet waits in one queue with the returned slots' re-preparation; each
        // frame runs what fits both the refill's own Main cap and the frame's remaining Main budget over a reserve
        // (MobPlanRefillQueue). A waiting individual stays in the planner's baseline, takes each newer plan, and starts
        // with the plan current then -- dropped instead, taking no slot, when that plan no longer places it or places
        // it in view.
        [Tooltip("The refill's own Main time per frame (display starts and slot re-preparation), in milliseconds.")]
        [SerializeField] private float refillCapMilliseconds = 2f;
        [Tooltip("Main time the refill leaves to the rest of the frame (over the simulation's expected cost), in milliseconds.")]
        [SerializeField] private float refillReserveMilliseconds = 4f;
        public const string RefillCapArgument = "-zantetsuRefillCapMs";
        private readonly MobPlanRefillQueue<PublishedMobPlan> refill = new MobPlanRefillQueue<PublishedMobPlan>(0.0005, 0.0015, 0.0015);
        private readonly List<double> refillStartWaits = new List<double>();
        private readonly List<double> refillStages = new List<double>();
        private readonly double[] refillStageMax = new double[3];
        // A returned slot's waits: from its individual's retirement to its preparation again, and to its being free.
        private readonly Dictionary<SandboxNpcCharacter, double> returnedAt = new Dictionary<SandboxNpcCharacter, double>();
        private readonly Dictionary<SandboxNpcCharacter, double> preparedAt = new Dictionary<SandboxNpcCharacter, double>();
        private readonly List<double> refillPrepareWaits = new List<double>();
        private readonly List<double> refillFreeWaits = new List<double>();
        private int refillMaxReturning, refillMaxPreparing;
        private double refillOverrunMax;
        private int refillStartsDroppedInView, refillStartsDroppedUnplaced, refillIgnoredStale;
        private static readonly ProfilerMarker s_refill = new ProfilerMarker("Zantetsu.MobPlan.Refill");
        private static readonly ProfilerMarker s_refillStart = new ProfilerMarker("Zantetsu.MobPlan.Refill.Start");
        public MobPlanRefillQueue<PublishedMobPlan> Refill => refill;

        private static readonly ProfilerMarker s_update = new ProfilerMarker("Zantetsu.MobPlan.Update");
        private static readonly ProfilerMarker s_queue = new ProfilerMarker("Zantetsu.MobPlan.Queue");
        private static readonly ProfilerMarker s_collect = new ProfilerMarker("Zantetsu.MobPlan.Collect");
        private static readonly ProfilerMarker s_replacement = new ProfilerMarker("Zantetsu.MobPlan.Replacement");
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
            using (s_update.Auto())
            {
                UpdateCrowd();
            }
        }

        private void UpdateCrowd()
        {
            if (stopped || world == null || !world.IsReady) return;
            if (!ready)
            {
                if (!poolStarted) StartPool();
                if (!poolPrepared) CheckPoolPrepared();
                if (!loaded && !loading && !loadingFailed) QueueLoad();
                if (loaded && poolPrepared) Begin();
                return;
            }
            RetireWithdrawn();
            using (s_pool.Auto())
            {
                AdvancePool();
            }

            using (s_refill.Auto())
            {
                RunRefill();
            }
            Vector3 position = player.transform.position;
            playerVelocity = Time.deltaTime > 0 ? (position - previousPlayer) / Time.deltaTime : Vector3.zero;
            previousPlayer = position;
            double now = PlanTime;
            foreach (var actor in actors.Values) ApplyRoot(actor, now);
            if (!busy && now >= nextRequest)
            {
                using (s_queue.Auto())
                {
                    QueueCycle((float)now);
                }
            }
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
                this.loaded = true;
#if ZANTETSU_MOBPLAN_PROFILE
                Debug.Log($"MOBPLAN LOAD queue_ms={work.started * 1000:F3} wall_ms={(work.ended - work.started) * 1000:F3} cpu_ms={work.cpuMilliseconds:F3}");
#endif
            });
            pendingLoad = load;
            loading = world.Dispatcher.TryEnqueue(WorkPurpose.MobPlanning, load, out _);
        }
        private sealed class Loaded { public LocomotionSimulation simulation; public MobPoseBank bank; }

        // The plan is loaded and every slot prepared: the first individuals take the first slots.
        private void Begin()
        {
            epoch = Time.timeAsDouble;
            player.transform.position = new Vector3(simulation.FocusX, 0, simulation.FocusZ) + mapOffset;
            player.ConfigureMap(simulation.Map, mapOffset);
            previousPlayer = player.transform.position;
            PrepareLodPlans();
            for (int i = 0; i < simulation.Agents.Count && pool.TryTake(out SandboxNpcCharacter slot); i++)
                AddActor(simulation.Agents[i].Id, slot, new PublishedMobPlan(simulation.Agents[i].Plan, generation));
            ready = true;
            Debug.Log($"MobPlan ready: {actors.Count} NPC, {simulation.Dataset.Count} nodes, {bank.Tables.Count} tables, 66 bones; slots {pool.SlotCount} ({pool.FreeCount} free)");
        }

        // Before the scenario, with the table bank loaded: every slot's level-of-detail plan, so that an activation only
        // registers it (the plan's range is read over the bank the slot will be driven with).
        private void PrepareLodPlans()
        {
            long began = System.Diagnostics.Stopwatch.GetTimestamp();
            int prepared = 0, refused = 0;
            double largest = 0;
            foreach (SandboxNpcCharacter slot in pool.Slots)
            {
                if (slot == null || slot.Failure != null) continue;
                slot.CharacterRoot.GetComponent<PoseTablePlayer>().TableBank = bank.Tables;
                if (slot.PrepareLodPlan(out _)) { prepared++; largest = Math.Max(largest, slot.LodPlanSeconds); }
                else refused++;
            }
            double seconds = (System.Diagnostics.Stopwatch.GetTimestamp() - began) / (double)System.Diagnostics.Stopwatch.Frequency;
            Debug.Log($"MOBPLAN LOD plans prepared: {prepared} slots, {refused} without, total_ms={seconds * 1000:F2} largest_ms={largest * 1000:F2}");
        }

        // Before the scenario: the scene's characters and the spares, each configured and prepared as a dormant slot.
        private void StartPool()
        {
            poolStarted = true;
            poolStartSeconds = Time.realtimeSinceStartupAsDouble;
            poolStartAllocated = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong();
            poolStartMono = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong();
            int spares = spareSlots;
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, SpareSlotsArgument);
            if (at >= 0 && at + 1 < args.Length && int.TryParse(args[at + 1], out int asked) && asked >= 0) spares = asked;
            foreach (var npc in initialCharacters) PrepareSlot(npc, null);
            for (int i = 0; i < spares && replacementTemplate != null; i++)
            {
                GameObject spare = Instantiate(replacementTemplate);
                spare.name = "MobPlan slot " + (pool.SlotCount + 1);
                PrepareSlot(spare.GetComponentInChildren<SandboxNpcCharacter>(true), spare);
            }
        }

        private void PrepareSlot(SandboxNpcCharacter npc, GameObject holder)
        {
            var pose = npc.CharacterRoot.GetComponent<PoseTablePlayer>();
            pose.Configure(assets.initialPose, pose.ModelRoot, 0, true);
            string key = npc.SlotShareKey;
            if (!slotShares.TryGetValue(key, out SandboxNpcCharacter.SlotShare share)) slotShares[key] = share = new SandboxNpcCharacter.SlotShare();
            npc.PrepareAsSlot(share);
            npc.CharacterRoot.SetActive(true);
            if (holder != null) holder.SetActive(true);
            npc.enabled = true;
            pool.Add(npc);
        }

        private void CheckPoolPrepared()
        {
            if (!poolStarted) return;
            pool.Advance(false);
            if (!pool.AllPrepared) return;
            foreach (SandboxNpcCharacter broken in pool.Broken) Debug.LogError("MobPlan slot not prepared: " + broken.Failure, broken);
            poolPrepared = true;
            PoolPrepareSeconds = Time.realtimeSinceStartupAsDouble - poolStartSeconds;
            PoolAllocatedBytes = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() - poolStartAllocated;
            PoolMonoBytes = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() - poolStartMono;
            Debug.Log($"MOBPLAN POOL prepared: slots={pool.SlotCount} free={pool.FreeCount} broken={pool.BrokenCount} seconds={PoolPrepareSeconds:F3} "
                + $"allocatedDeltaMB={PoolAllocatedBytes / 1048576.0:F1} monoDeltaMB={PoolMonoBytes / 1048576.0:F1} "
                + $"fullPreparations={SandboxNpcCharacter.FullPreparations} sharedReads={SharedReads} models={SlotShareCount}");
        }

        // After the scenario began: a returned slot (its cut refers to nothing of it any more) is prepared again, one a
        // frame; a prepared one is free.
        private void AdvancePool()
        {
            pool.Advance(false);
            refillMaxReturning = Math.Max(refillMaxReturning, pool.ReturningCount);
            refillMaxPreparing = Math.Max(refillMaxPreparing, pool.PreparingCount);
            // A slot prepared again and now free: its whole wait since its individual's retirement.
            if (preparedAt.Count > 0)
            {
                List<SandboxNpcCharacter> free = null;
                foreach (KeyValuePair<SandboxNpcCharacter, double> p in preparedAt)
                    if (p.Key == null || p.Key.IsPrepared || p.Key.Failure != null) (free ??= new List<SandboxNpcCharacter>()).Add(p.Key);
                if (free != null)
                    foreach (SandboxNpcCharacter s in free)
                    {
                        if (s != null && s.IsPrepared) refillFreeWaits.Add(Time.realtimeSinceStartupAsDouble - preparedAt[s]);
                        preparedAt.Remove(s);
                    }
            }
        }

        private double RefillCapSeconds()
        {
            string[] args = Environment.GetCommandLineArgs();
            int at = Array.IndexOf(args, RefillCapArgument);
            if (at >= 0 && at + 1 < args.Length && double.TryParse(args[at + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double ms) && ms > 0)
                return ms / 1000.0;
            return refillCapMilliseconds / 1000.0;
        }

        private double refillCap = double.NaN;
        // Made once: the refill runs every frame and allocates nothing.
        private Func<double> refillRemaining;
        private Func<int, PublishedMobPlan, MobPlanRefillQueue<PublishedMobPlan>.StartResult> refillStart;
        private Func<bool> refillHasPrepare, refillPrepare, refillNeedsBake, refillBake;

        // The frame's refill: waiting starts first, then returned slots prepared again, as far as the cap and the frame allow.
        private void RunRefill()
        {
            if (double.IsNaN(refillCap))
            {
                refillCap = RefillCapSeconds();
                refillRemaining = () => CutPhysicsStep.FrameRemainingMainSeconds;
                refillStart = StartWaiting;
                refillHasPrepare = () => pool.HasReturnReady;
                refillPrepare = () =>
                {
                    bool ok = pool.PrepareOneAgain();
                    SandboxNpcCharacter slot = pool.LastPrepared;
                    if (slot != null && returnedAt.TryGetValue(slot, out double at))
                    {
                        refillPrepareWaits.Add(Time.realtimeSinceStartupAsDouble - at);
                        preparedAt[slot] = at;
                        returnedAt.Remove(slot);
                    }

                    return ok;
                };
                refillNeedsBake = () => pool.NextNeedsBake;
                refillBake = pool.BakeNext;
            }

            int broken = pool.BrokenCount;
            int records = refill.Records.Count;
            refill.Run(Time.frameCount, refillCap, refillRemaining,
                refillReserveMilliseconds / 1000.0 + CutPhysicsStep.ExpectedSimulateSeconds, refillStart, refillHasPrepare, refillPrepare, refillNeedsBake, refillBake);
            if (pool.BrokenCount > broken) Debug.LogError("MobPlan slot not prepared again: " + pool.Broken[pool.BrokenCount - 1].Failure, pool.Broken[pool.BrokenCount - 1]);
            for (int i = records; i < refill.Records.Count; i++)
            {
                MobPlanRefillQueue<PublishedMobPlan>.Record r = refill.Records[i];
                refillStages.Add(r.measured);
                refillStageMax[(int)r.stage] = Math.Max(refillStageMax[(int)r.stage], r.measured);
                refillOverrunMax = Math.Max(refillOverrunMax, r.overrun);
                if (r.stage == MobPlanRefillQueue<PublishedMobPlan>.Stage.Start) refillStartWaits.Add(r.waitSeconds);
            }

            refill.Records.Clear();
        }

        // One waiting individual's start with the plan it has now: dropped, taking no slot, when the plan no longer places
        // it or places it in view; waiting on when no slot is free.
        private MobPlanRefillQueue<PublishedMobPlan>.StartResult StartWaiting(int id, PublishedMobPlan plan)
        {
            using (s_refillStart.Auto())
            {
                if (!bank.TryEvaluate(plan, PlanTime, simulation.Dataset, out _, out _, out var root))
                {
                    refillStartsDroppedUnplaced++;
                    return MobPlanRefillQueue<PublishedMobPlan>.StartResult.Dropped;
                }

                if (Visible(root.position.x, root.position.z))
                {
                    refillStartsDroppedInView++;
                    return MobPlanRefillQueue<PublishedMobPlan>.StartResult.Dropped;
                }

                if (pool.FreeCount == 0)
                {
                    WaitedForSlot++;
                    return MobPlanRefillQueue<PublishedMobPlan>.StartResult.NoSlot;
                }

                pool.TryTake(out SandboxNpcCharacter slot);
                using (s_replacement.Auto())
                {
                    if (!AddActor(id, slot, plan, true)) return MobPlanRefillQueue<PublishedMobPlan>.StartResult.Dropped;
                    ReplacementsAdded++;
                }

                return MobPlanRefillQueue<PublishedMobPlan>.StartResult.Started;
            }
        }

        private bool AddActor(int id, SandboxNpcCharacter character, PublishedMobPlan plan, bool replacement = false)
        {
            var pose = character.CharacterRoot.GetComponent<PoseTablePlayer>();
            pose.TableBank = bank.Tables;
            var actor = new Actor { character = character, pose = pose, plan = plan };
            pose.PlanSource = (double target, out PoseTable table, out double source) =>
                bank.TryEvaluate(actor.plan, target - epoch, simulation.Dataset, out table, out source, out _);
            // Placed where the plan has it first; then posed, drawn, given its level of detail and made a hit target;
            // then its motion is taken again from the plan with its body in the scene; then its plan is registered.
            ApplyRoot(actor, PlanTime);
            bool reused = character.Activations > 0;
            if (!character.Activate())
            {
                Debug.LogError("MobPlan slot could not be activated: " + (character.Failure ?? "not prepared"), character);
                pool.MarkBroken(character);
                return false;
            }
            ApplyRoot(actor, PlanTime);
            if (reused) ReusedActivations++;
            actors.Add(id, actor);
            ActorAdded?.Invoke(id, character, replacement);
            return true;
        }

        private void RetireWithdrawn()
        {
            List<int> gone = null;
            foreach (var pair in actors)
                if (pair.Value.character == null || (pair.Value.character.Handle != null && pair.Value.character.Handle.IsWithdrawn))
                { if (gone == null) gone = new List<int>(); gone.Add(pair.Key); }
            if (gone == null) return;
            foreach (int id in gone)
            {
                SandboxNpcCharacter character = actors[id].character;
                ActorRetired?.Invoke(id, character);
                actors.Remove(id);
                // A cut character's pose is no longer updated: it leaves the bone level of detail with its retirement.
                if (character != null) character.LeaveLevelOfDetail();
                // Its slot waits until the cut refers to nothing of it (AdvancePool).
                if (character != null) { pool.Return(character); returnedAt[character] = Time.realtimeSinceStartupAsDouble; }
            }

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
            public HashSet<int> baseline;
        }
        private void QueueCycle(float now)
        {
            int capturedGeneration = generation;
            int queuedFrame = Time.frameCount;
            var baseline = actors.ToDictionary(p => p.Key, p => p.Value.plan);
            foreach (KeyValuePair<int, PublishedMobPlan> waiting in refill.Waiting) baseline[waiting.Key] = waiting.Value;
            var baselineIds = new HashSet<int>(baseline.Keys);
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
                return new Product { cycle = result, relocated = moved, baseline = baselineIds,
                    plans = result.NewPlans.ToDictionary(pair => pair.Key, pair => new PublishedMobPlan(pair.Value, capturedGeneration)) };
            }, (job, completion) =>
            {
                using (s_collect.Auto())
                {
                    int before = ReplacementsAdded;
                    string outcome = CollectCycle(job, completion, capturedGeneration);
                    timings.Add(new CycleTiming(QueueMilliseconds, ComputeMilliseconds, CollectionMilliseconds, queuedFrame, Time.frameCount, outcome, ReplacementsAdded - before));
                    if (timings.Count > 600) timings.RemoveAt(0);
                }
            });
#if ZANTETSU_MOBPLAN_PROFILE
            work.measured = job => ReportProfile(job, now);
#endif
            if (world.Dispatcher.TryEnqueue(WorkPurpose.MobPlanning, work, out _))
            { busy = true; lastAccepted = null; nextRequest = now + 1; }
        }

        // The collection of one cycle on the main thread: what became of it ("published", or why not).
        private string CollectCycle(Work job, WorkCompletion completion, int capturedGeneration)
        {
            busy = false;
            if (stopped) return "stopped";
            QueueMilliseconds = job.started * 1000;
            ComputeMilliseconds = (job.ended - job.started) * 1000;
            CollectionMilliseconds = (job.watch.Elapsed.TotalSeconds - job.ended) * 1000;
            RetireWithdrawn();
            if (!completion.Succeeded) { Fail(completion.failure); return "failed"; }
            var product = job.result as Product;
            if (product == null || generation != capturedGeneration || (product.cycle.Published && product.cycle.MinCommitUntil <= PlanTime))
            { StaleCycles++; nextRequest = PlanTime; return product == null ? "stale-empty" : generation != capturedGeneration ? "stale-generation" : "stale-expired"; }
            if (!product.cycle.Published) { FailedCycles++; return "unpublished: " + product.cycle.Status; }
            foreach (int id in product.relocated)
            {
                if (!product.plans[id].TryResolve(PlanTime, simulation.Dataset, out _, out _, out var proposed)
                    || Visible(proposed.X, proposed.Z)
                    || (actors.TryGetValue(id, out var old) && Visible(old.character.CharacterRoot.transform.position.x - mapOffset.x, old.character.CharacterRoot.transform.position.z - mapOffset.z)))
                { StaleCycles++; nextRequest = PlanTime; return "stale-visible"; }
            }
            // A waiting start the plan no longer has is cancelled; one it has takes the new plan; a new individual waits for
            // its start (the refill). One the cycle's baseline held that is neither live nor waiting now -- retired, dropped
            // or cancelled since -- is not asked for again.
            List<int> gone = null;
            foreach (KeyValuePair<int, PublishedMobPlan> waiting in refill.Waiting)
                if (!product.plans.ContainsKey(waiting.Key)) (gone ??= new List<int>()).Add(waiting.Key);
            if (gone != null) foreach (int id in gone) refill.Cancel(id);
            foreach (var pair in product.plans)
            {
                if (actors.TryGetValue(pair.Key, out var actor)) actor.plan = pair.Value;
                else if (refill.IsWaiting(pair.Key) || !product.baseline.Contains(pair.Key)) refill.Request(pair.Key, pair.Value, Time.frameCount);
                else refillIgnoredStale++;
            }
            lastAccepted = product.cycle;
            PublishedCycles++;
            return "published";
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
        private void OnDestroy()
        {
            stopped = true; generation++;
            int waitingAtEnd = refill.WaitingStarts;
            refill.CancelAll();
            if (ready)
            {
                Debug.Log($"MOBPLAN END reusedActivations={ReusedActivations} lodPlanFallbacks={SandboxNpcCharacter.LodPlanFallbacks} slotBakes={SandboxNpcCharacter.SlotBakes}");
                Debug.Log($"MOBPLAN REFILL capMs={refillCap * 1000:F2} reserveMs={refillReserveMilliseconds:F2} requests={refill.Requests} replaced={refill.Replaced} "
                    + $"cancelled={refill.Cancelled} (waiting at end {waitingAtEnd}) started={refill.Started} dropped={refill.Dropped} (unplaced {refillStartsDroppedUnplaced}, in view {refillStartsDroppedInView}) "
                    + $"ignoredStale={refillIgnoredStale} prepared={refill.Prepared} deferredByCap={refill.DeferredByCap} deferredByFrame={refill.DeferredByFrame} "
                    + $"overCapRefusals={refill.OverCapRefusals} noSlotFrames={refill.NoSlotFrames} overruns={refill.Overruns} overrunMaxMs={refillOverrunMax * 1000:F3} "
                    + $"startMaxMs={refillStageMax[0] * 1000:F3} prepareMaxMs={refillStageMax[1] * 1000:F3} bakeMaxMs={refillStageMax[2] * 1000:F3} baked={refill.Baked} "
                    + $"expectedMs start={refill.ExpectedSeconds(MobPlanRefillQueue<PublishedMobPlan>.Stage.Start) * 1000:F3} prepare={refill.ExpectedSeconds(MobPlanRefillQueue<PublishedMobPlan>.Stage.Prepare) * 1000:F3} "
                    + $"maxWaitingStarts={refill.MaxWaitingStarts} maxReturning={refillMaxReturning} maxPreparing={refillMaxPreparing} "
                    + $"atEnd returning={pool.ReturningCount} (ready {(pool.HasReturnReady ? "yes" : "no")}) preparing={pool.PreparingCount} free={pool.FreeCount} broken={pool.BrokenCount} "
                    + $"bakeMismatches={SandboxNpcCharacter.BakeMismatches} stageBakes={SandboxNpcCharacter.StageBakes} preparationBakes={SandboxNpcCharacter.PreparationBakes} "
                    + $"prepareWaitMs(retired->prepared) n={refillPrepareWaits.Count} median={Quantile(refillPrepareWaits, .5) * 1000:F1} p90={Quantile(refillPrepareWaits, .9) * 1000:F1} max={Quantile(refillPrepareWaits, 1) * 1000:F1} "
                    + $"freeWaitMs(retired->free) n={refillFreeWaits.Count} median={Quantile(refillFreeWaits, .5) * 1000:F1} max={Quantile(refillFreeWaits, 1) * 1000:F1} "
                    + $"startWaitMs median={Quantile(refillStartWaits, .5) * 1000:F1} p90={Quantile(refillStartWaits, .9) * 1000:F1} max={Quantile(refillStartWaits, 1) * 1000:F1}");
            }
        }

        private static double Quantile(List<double> values, double q)
        {
            if (values.Count == 0) return double.NaN;
            var sorted = new List<double>(values);
            sorted.Sort();
            return sorted[Math.Min(sorted.Count - 1, (int)Math.Round(q * (sorted.Count - 1)))];
        }
        private void OnValidate() { if (spareSlots < 0) spareSlots = 0; }

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
