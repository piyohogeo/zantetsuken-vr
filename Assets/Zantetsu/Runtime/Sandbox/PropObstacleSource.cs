using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.ConvexCut;
using Zantetsu.Core.MobPlan;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// PROP-OBSTACLE-1: makes the planner's map of prop fragments (a felled tree's pieces) -- Zantetsu.Core.MobPlan.PropObstacleMap --
    /// from the cut world, best effort.
    /// <para>
    /// Main does four things only, spread over frames: it tells which current shapes are of a prop's lineage, takes a hold on a
    /// shape whose numbers were not copied yet, copies each one's transform as numbers, and receives the finished version.
    /// The sections, the cells and the distances are made by ONE work at a time on the Background pool; a work reads no Unity
    /// object. Nothing waits in a queue behind it: the next input is gathered only after the work was collected, from the state
    /// of that moment. The finished version is published read-only (PropObstacleMapPool); a planning cycle keeps the version it
    /// took, and while a version is being made the one before stays the current one.
    /// </para>
    /// </summary>
    public sealed unsafe class PropObstacleSource
    {
        /// <summary>
        /// The Player argument that turns the prop fragments' map and the planner's parts taken with it off (D-264: on by
        /// default in every build since 2026-11-17).
        /// </summary>
        public const string OffArgument = "-zantetsuPropObstaclesOff";
        /// <summary>The argument that turned the map on while it was under evaluation: still accepted, without effect.</summary>
        public const string Argument = "-zantetsuPropObstacles";
        /// <summary>With it the crowd also writes the map's end line whole to a file (a device log cuts the line).</summary>
        public const string EndFileArgument = "-zantetsuPropObstacleEndFile";
        /// <summary>Followed by the plane's height above the NPC floor in metres.</summary>
        public const string PlaneArgument = "-zantetsuPropObstaclePlane";
        /// <summary>Followed by the cell's side in metres (the window stays about as wide: the cell count follows).</summary>
        public const string CellArgument = "-zantetsuPropObstacleCell";
        /// <summary>Followed by the aim of the interval between builds in milliseconds.</summary>
        public const string UpdateArgument = "-zantetsuPropObstacleUpdateMs";

        private enum Phase { Idle, Gathering, Submitting, Building }

        private sealed class Entry
        {
            public int id;
            public PropShape copy;
            public int seenBuild;
        }

        private struct Held
        {
            public PhysicsOwnerShape shape;
            public Entry entry;
            public int index;
        }

        private readonly CutWorldRoot world;
        private readonly Vector3 mapOffset;
        private readonly WalkableMap staticMap;
        private readonly PropObstacleSettings settings;
        private readonly PropObstacleBuilder builder = new PropObstacleBuilder();
        private readonly PropObstacleInput[] inputs = { new PropObstacleInput(), new PropObstacleInput() };
        private readonly List<CurrentShape> round = new List<CurrentShape>();
        private readonly Dictionary<PhysicsOwnerShape, Entry> shapes = new Dictionary<PhysicsOwnerShape, Entry>();
        private readonly List<PhysicsOwnerShape> gone = new List<PhysicsOwnerShape>();
        private readonly Stopwatch watch = new Stopwatch();
        private readonly float[] box = new float[6];
        private readonly float[] matrix = new float[12];
        private float windowPlayerX, windowPlayerZ;
        /// <summary>The square a capture keeps shapes of (PropObstacleSettings.captureSquareMeters), and the slack about it.</summary>
        public const float CaptureSquareMeters = 80f, CaptureSlackMeters = 1f;
        /// <summary>Shapes outside the window that the last round kept for the capture.</summary>
        public int LastShapesKeptForCapture { get; private set; }
        private Phase phase;
        private int cursor, flip, buildSerial, nextShapeId, submitTries;
        private double nextStart, gatherStarted, gatherMainSeconds;
        private int gatherFrames;
        private bool hasWindow, maskStale, released;
        private float originX, originZ;
        private byte[] mask = Array.Empty<byte>();
        private Job job;

        public PropObstacleMapPool Pool { get; } = new PropObstacleMapPool();
        /// <summary>The capture the builds are written to (null: none). Set before the first Step.</summary>
        public PropCaptureWriter Capture;
        public PropObstacleSettings Settings => settings;

        // ---- what it did (counts; Main) ----
        public int Builds { get; private set; }
        public int Published { get; private set; }
        public int Failed { get; private set; }
        public int EnqueueRefusals { get; private set; }
        public int GaveUpSubmitting { get; private set; }
        public int WindowMoves { get; private set; }
        public int ShapesCopied { get; private set; }
        public long ShapeBytesCopied { get; private set; }
        public int ShapesKnown => shapes.Count;
        /// <summary>Current shapes looked at that are not of a prop's lineage (the last gathering).</summary>
        public int LastNotProp { get; private set; }
        /// <summary>Prop shapes left out on Main by their whole box: below the plane / above it / outside the window (the last gathering).</summary>
        public int LastShapesBelow { get; private set; }
        public int LastShapesAbove { get; private set; }
        public int LastShapesOutside { get; private set; }
        public long ShapesBelowInAll { get; private set; }
        public int LastGatherFrames { get; private set; }
        public int MostGatherFrames { get; private set; }
        public double LastGatherMainMilliseconds { get; private set; }
        public double GatherMainMillisecondsInAll { get; private set; }
        public double MostGatherMainMillisecondsAFrame { get; private set; }
        public double CollectMainMillisecondsInAll { get; private set; }
        public double MostCollectMainMilliseconds { get; private set; }
        public double WorkerMillisecondsInAll { get; private set; }
        public double MostWorkerMilliseconds { get; private set; }
        public double LastWorkerMilliseconds { get; private set; }
        /// <summary>
        /// The finished builds whose window is another than that of the finished build before (the first build included): they
        /// make the static mask anew. Counted apart so a build without the capture (Release) shows what placing the window costs.
        /// </summary>
        public int WindowBuilds { get; private set; }
        public double WindowBuildWorkerMillisecondsInAll { get; private set; }
        public double MostWindowBuildWorkerMilliseconds { get; private set; }
        private float lastBuiltOriginX = float.NaN, lastBuiltOriginZ = float.NaN;
        /// <summary>From the input's time to the publication (the planner's clock, s): the last and the most.</summary>
        public double LastPublishDelaySeconds { get; private set; }
        public double MostPublishDelaySeconds { get; private set; }
        public double PublishDelaySecondsInAll { get; private set; }
        public PropObstacleStats LastStats { get; private set; }
        public int MostItems { get; private set; }
        public int MostOccupiedCells { get; private set; }

        /// <summary>Observation (Main, at the collection of a published build): the input, the version, the shapes copied for it.</summary>
        public event Action<PropObstacleInput, PropObstacleMap, IReadOnlyList<PropShape>> Built;

        public PropObstacleSource(CutWorldRoot world, Vector3 mapOffset, WalkableMap staticMap, PropObstacleSettings settings)
        {
            this.world = world != null ? world : throw new ArgumentNullException(nameof(world));
            this.mapOffset = mapOffset;
            this.staticMap = staticMap;
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            if (settings.cells < 8 || settings.cellMeters <= 0f) throw new ArgumentException("the window's cells");
        }

        /// <summary>Whether a launch has the map (and the planner's parts taken with it): yes unless it carries <see cref="OffArgument"/>.</summary>
        public static bool On(string[] arguments) => arguments == null || Array.IndexOf(arguments, OffArgument) < 0;

        /// <summary>Whether a launch has the map (<see cref="On"/>), and the settings its arguments give (the adopted values otherwise).</summary>
        public static bool Read(string[] arguments, out PropObstacleSettings settings)
        {
            settings = new PropObstacleSettings();
            if (arguments == null) return true;
            bool on = On(arguments);
            if (TryNumber(arguments, PlaneArgument, out float plane) && plane > 0f && plane < 5f) settings.planeHeightMeters = plane;
            if (TryNumber(arguments, CellArgument, out float cell) && cell >= 0.05f && cell <= 2f)
            {
                float window = settings.WindowMeters;
                settings.cellMeters = cell;
                settings.cells = Mathf.Clamp(Mathf.RoundToInt(window / cell), 8, 1024);
            }

            if (TryNumber(arguments, UpdateArgument, out float update) && update >= 0f && update <= 10000f) settings.updateSeconds = update / 1000f;
            return on;
        }

        private static bool TryNumber(string[] arguments, string name, out float value)
        {
            value = 0f;
            int at = Array.IndexOf(arguments, name);
            return at >= 0 && at + 1 < arguments.Length && float.TryParse(arguments[at + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>Main, every frame of the crowd: one small turn. `now` is the planner's clock; the player is in planner coordinates.</summary>
        public void Step(double now, float playerX, float playerZ)
        {
            if (released) return;
            if (phase == Phase.Idle)
            {
                if (now < nextStart) return;
                BeginGather(now, playerX, playerZ);
            }

            if (phase == Phase.Gathering) Gather(now, playerX, playerZ);
            if (phase == Phase.Submitting) Submit();
        }

        private void BeginGather(double now, float playerX, float playerZ)
        {
            float size = settings.WindowMeters;
            bool hadWindow = hasWindow;
            if (PropObstacleWindow.Follow(size, settings.cellMeters, settings.recentreMarginMeters, ref hasWindow, ref originX, ref originZ, playerX, playerZ))
            {
                if (hadWindow) WindowMoves++;
                maskStale = true;
            }

            // the player this window was decided with: recorded with the build, and the centre of the capture square of this round
            windowPlayerX = playerX; windowPlayerZ = playerZ;

            world.Owners.CollectCurrentShapes(round);
            cursor = 0;
            buildSerial++;
            flip ^= 1;
            PropObstacleInput input = inputs[flip];
            input.Clear();
            input.SetWindow(settings, originX, originZ);
            input.WindowPlayerX = windowPlayerX; input.WindowPlayerZ = windowPlayerZ;
            job = new Job { owner = this, input = input, serial = buildSerial, maskStale = maskStale };
            gatherStarted = now;
            gatherMainSeconds = 0.0;
            gatherFrames = 0;
            LastNotProp = 0; LastShapesBelow = 0; LastShapesAbove = 0; LastShapesOutside = 0; LastShapesKeptForCapture = 0;
            submitTries = 0;
            phase = Phase.Gathering;
        }

        /// <summary>
        /// Looks at the round's shapes while this frame has Main time left (one at least a frame): of a prop's lineage, near the
        /// plane and the window by its whole box -- then its transform as numbers, and a hold when its numbers are not copied yet.
        /// </summary>
        private void Gather(double now, float playerX, float playerZ)
        {
            watch.Restart();
            gatherFrames++;
            PropObstacleInput input = job.input;
            float size = settings.WindowMeters;
            float plane = settings.planeHeightMeters;
            int looked = 0;
            while (cursor < round.Count)
            {
                if (looked > 0 && (watch.Elapsed.TotalSeconds > MaxGatherSecondsAFrame || CutPhysicsStep.FrameRemainingMainSeconds < MainReserveSeconds)) break;
                CurrentShape current = round[cursor++];
                looked++;
                PhysicsOwnerShape shape = current.Shape;
                if (shape == null || shape.IsFreed || current.Owner == null) continue;   // gone since the round was taken
                if (!world.Ledger.TryGetRoot(current.Fragment, out LogicalFragmentId root) || !world.Lifetime.IsPropLineage(root)) { LastNotProp++; continue; }

                // shape local -> owner -> world -> planner coordinates (the crowd's map offset taken off)
                Matrix4x4 m = current.Owner.localToWorldMatrix * (Matrix4x4)shape.LocalToOwner;
                matrix[0] = m.m00; matrix[1] = m.m01; matrix[2] = m.m02; matrix[3] = m.m03 - mapOffset.x;
                matrix[4] = m.m10; matrix[5] = m.m11; matrix[6] = m.m12; matrix[7] = m.m13 - mapOffset.y;
                matrix[8] = m.m20; matrix[9] = m.m21; matrix[10] = m.m22; matrix[11] = m.m23 - mapOffset.z;
                bool known = shapes.TryGetValue(shape, out Entry entry);
                if (known) entry.seenBuild = buildSerial;
                if (shape.TryLocalBounds(out float3 lo, out float3 hi))
                {
                    box[0] = lo.x; box[1] = lo.y; box[2] = lo.z; box[3] = hi.x; box[4] = hi.y; box[5] = hi.z;
                    PropObstacleBuilder.TransformBox(box, 0, matrix, 0, out float minX, out float maxX, out float minY, out float maxY, out float minZ, out float maxZ);
                    // under a capture a shape off the plane is still taken (and counted): the saved input can then be sliced at another height
                    if (maxY < plane) { LastShapesBelow++; ShapesBelowInAll++; if (Capture == null) continue; }
                    else if (minY > plane) { LastShapesAbove++; if (Capture == null) continue; }
                    if (maxX < originX || minX > originX + size || maxZ < originZ || minZ > originZ + size)
                    {
                        LastShapesOutside++;
                        // under a capture the shapes of the capture square are kept too (the builder skips them: they are outside its
                        // window). The square is about the player the window was decided with -- the place a replay places its
                        // windows from -- and a metre more on each side: a window's corner is put on a whole cell, up to one cell
                        // beyond the half window
                        float reach = Capture != null ? settings.captureSquareMeters * 0.5f + CaptureSlackMeters : 0f;
                        if (reach <= CaptureSlackMeters || maxX < windowPlayerX - reach || minX > windowPlayerX + reach || maxZ < windowPlayerZ - reach || minZ > windowPlayerZ + reach) continue;
                        LastShapesKeptForCapture++;
                    }
                }

                if (!known)
                {
                    entry = new Entry { id = ++nextShapeId, seenBuild = buildSerial };
                    shapes.Add(shape, entry);
                }

                int index = input.Add(entry.copy, current.Fragment.value, root.value, current.Side > 0f ? 1 : current.Side < 0f ? -1 : 0);
                Array.Copy(matrix, 0, input.Matrices, index * 12, 12);
                if (entry.copy == null)
                {
                    shape.AcquireForWork();   // the worker copies its numbers from the bank; released at the collection
                    job.held.Add(new Held { shape = shape, entry = entry, index = index });
                }
            }

            double seconds = watch.Elapsed.TotalSeconds;
            gatherMainSeconds += seconds;
            if (seconds * 1000.0 > MostGatherMainMillisecondsAFrame) MostGatherMainMillisecondsAFrame = seconds * 1000.0;
            if (cursor < round.Count) return;
            round.Clear();
            input.Time = now;
            input.PlayerX = playerX; input.PlayerZ = playerZ;
            LastGatherFrames = gatherFrames;
            if (gatherFrames > MostGatherFrames) MostGatherFrames = gatherFrames;
            LastGatherMainMilliseconds = gatherMainSeconds * 1000.0;
            GatherMainMillisecondsInAll += LastGatherMainMilliseconds;
            phase = Phase.Submitting;
        }

        /// <summary>The Main time a gathering turn takes at most in a frame once it looked at one shape, and what it leaves of the frame.</summary>
        internal const double MaxGatherSecondsAFrame = 0.00025;
        internal const double MainReserveSeconds = 0.001;

        private void Submit()
        {
            if (world.Dispatcher.TryEnqueue(WorkPurpose.Maintenance, job, out _))
            {
                Builds++;
                phase = Phase.Building;
                return;
            }

            // no place this frame: the same input is offered again; given up after a while (a newer one is gathered then)
            EnqueueRefusals++;
            if (++submitTries < 30) return;
            GaveUpSubmitting++;
            ReleaseHeld(job, false);
            job = null;
            nextStart = gatherStarted + settings.updateSeconds;
            phase = Phase.Idle;
        }

        private void ReleaseHeld(Job done, bool keepCopies)
        {
            foreach (Held held in done.held)
            {
                if (keepCopies && done.input.Shapes[held.index] != null && held.entry.copy == null)
                {
                    held.entry.copy = done.input.Shapes[held.index];
                    ShapesCopied++;
                    ShapeBytesCopied += held.entry.copy.Bytes;
                }

                held.shape.ReleaseFromWork();
            }

            done.held.Clear();
        }

        /// <summary>Main: the work's end, whatever became of it. The holds go; a finished version becomes the current one.</summary>
        private void Collect(Job done, WorkCompletion completion)
        {
            watch.Restart();
            bool finished = completion.Succeeded && done.built != null && !released;
            ReleaseHeld(done, completion.Succeeded);
            if (finished)
            {
                Pool.Publish(done.built);
                Published++;
                maskStale = false;
                LastStats = done.built.Stats;
                LastWorkerMilliseconds = done.workerSeconds * 1000.0;
                WorkerMillisecondsInAll += LastWorkerMilliseconds;
                if (LastWorkerMilliseconds > MostWorkerMilliseconds) MostWorkerMilliseconds = LastWorkerMilliseconds;
                if (done.input.OriginX != lastBuiltOriginX || done.input.OriginZ != lastBuiltOriginZ)   // NaN before the first: counted
                {
                    WindowBuilds++;
                    WindowBuildWorkerMillisecondsInAll += LastWorkerMilliseconds;
                    if (LastWorkerMilliseconds > MostWindowBuildWorkerMilliseconds) MostWindowBuildWorkerMilliseconds = LastWorkerMilliseconds;
                    lastBuiltOriginX = done.input.OriginX; lastBuiltOriginZ = done.input.OriginZ;
                }

                if (LastStats.Items > MostItems) MostItems = LastStats.Items;
                if (LastStats.OccupiedCells > MostOccupiedCells) MostOccupiedCells = LastStats.OccupiedCells;
                double delay = Now() - done.input.Time;
                LastPublishDelaySeconds = delay;
                PublishDelaySecondsInAll += delay;
                if (delay > MostPublishDelaySeconds) MostPublishDelaySeconds = delay;
                Built?.Invoke(done.input, done.built, done.copied);
            }
            else
            {
                if (done.built != null) Pool.Return(done.built);
                if (!completion.Succeeded && completion.failure != null) { Failed++; UnityEngine.Debug.LogException(completion.failure); }
            }

            // a shape no gathering saw for two builds is gone (retired, or its fragment's shape replaced): its numbers are dropped
            gone.Clear();
            foreach (KeyValuePair<PhysicsOwnerShape, Entry> pair in shapes)
            {
                if (pair.Value.seenBuild < buildSerial - 1) gone.Add(pair.Key);
            }

            foreach (PhysicsOwnerShape shape in gone) shapes.Remove(shape);
            if (job == done) job = null;
            if (phase == Phase.Building)
            {
                nextStart = gatherStarted + settings.updateSeconds;
                phase = Phase.Idle;
            }

            double ms = watch.Elapsed.TotalMilliseconds;
            CollectMainMillisecondsInAll += ms;
            if (ms > MostCollectMainMilliseconds) MostCollectMainMilliseconds = ms;
            if (finished) Capture?.Hand(PropCaptureWriter.Builds, PropCapture.EncodePublished(done.serial, done.built.Version, done.input.Time, Now(), ms));
        }

        /// <summary>The planner's clock, as the crowd gives it (set by the crowd; the frame's time otherwise).</summary>
        public Func<double> Clock;
        private double Now() => Clock != null ? Clock() : Time.timeAsDouble;

        /// <summary>The crowd's end: nothing more is gathered; a work out is collected by the dispatcher as usual and lets its holds go there.</summary>
        public void Release()
        {
            if (released) return;
            released = true;
            if (job != null && phase != Phase.Building) ReleaseHeld(job, false);   // gathered, never submitted
            if (phase != Phase.Building) job = null;
            phase = Phase.Idle;
            round.Clear();
            Pool.Clear();
        }

        public string Describe()
        {
            return FormattableString.Invariant($"prop obstacle map: {settings.Describe()}; builds {Builds}, published {Published}, failed {Failed}, no place at the dispatcher {EnqueueRefusals} (given up {GaveUpSubmitting}), window moved {WindowMoves}; ")
                + FormattableString.Invariant($"shapes copied {ShapesCopied} ({ShapeBytesCopied} B), known now {shapes.Count}; the last gathering: not a prop's {LastNotProp}, below the plane {LastShapesBelow} (in all {ShapesBelowInAll}), above {LastShapesAbove}, outside the window {LastShapesOutside}, frames {LastGatherFrames} (most {MostGatherFrames}); ")
                + FormattableString.Invariant($"Main ms: gathering in all {GatherMainMillisecondsInAll:F3} (the last {LastGatherMainMilliseconds:F3}, most in a frame {MostGatherMainMillisecondsAFrame:F3}), collection in all {CollectMainMillisecondsInAll:F3} (most {MostCollectMainMilliseconds:F3}); ")
                + FormattableString.Invariant($"worker ms in all {WorkerMillisecondsInAll:F3} (most {MostWorkerMilliseconds:F3}), of that in the {WindowBuilds} builds that placed the window (the first included) {WindowBuildWorkerMillisecondsInAll:F3} (most {MostWindowBuildWorkerMilliseconds:F3}); from the input to the publication: mean {(Published > 0 ? PublishDelaySecondsInAll / Published * 1000.0 : 0.0):F1} ms, most {MostPublishDelaySeconds * 1000.0:F1} ms; ")
                + FormattableString.Invariant($"most items {MostItems}, most cells occupied {MostOccupiedCells}; buffers made {Pool.Made}; the last build: {LastStats.Describe()}");
        }

        /// <summary>One build on the Background pool: the numbers of the shapes not copied yet, the window's static cells when it moved, the version.</summary>
        private sealed class Job : IDispatchWork
        {
            public PropObstacleSource owner;
            public PropObstacleInput input;
            public int serial;
            public bool maskStale;
            public readonly List<Held> held = new List<Held>();
            public readonly List<PropShape> copied = new List<PropShape>();
            public PropObstacleMap built;
            public double workerSeconds;

            public void Begin()
            {
                var clock = Stopwatch.StartNew();
                foreach (Held h in held)
                {
                    PropShape copy = Copy(h.shape, h.entry.id);
                    input.Shapes[h.index] = copy;
                    copied.Add(copy);
                }

                // the same shape taken twice in one input (two holds): every index has its copy above
                int cells = input.Cells;
                if (maskStale || owner.mask.Length != cells * cells)
                {
                    if (owner.mask.Length != cells * cells) owner.mask = new byte[cells * cells];
                    PropObstacleBuilder.StaticMask(owner.staticMap, input.OriginX, input.OriginZ, input.Cell, cells, owner.mask);
                }

                PropObstacleMap target = owner.Pool.Take(cells);
                try
                {
                    owner.builder.Build(input, owner.mask, target);
                    built = target;
                    workerSeconds = clock.Elapsed.TotalSeconds;
                    owner.Capture?.HandBuild(input, copied, target.Stats, new PropCapture.BuildTimings
                    {
                        Serial = serial, GatherFrames = owner.LastGatherFrames, GatherMainMilliseconds = owner.LastGatherMainMilliseconds, WorkerMilliseconds = workerSeconds * 1000.0,
                        NotProp = owner.LastNotProp, ShapesBelow = owner.LastShapesBelow, ShapesAbove = owner.LastShapesAbove, ShapesOutside = owner.LastShapesOutside,
                    });
                }
                catch
                {
                    owner.Pool.Return(target);
                    throw;
                }
                finally
                {
                    if (built == null) workerSeconds = clock.Elapsed.TotalSeconds;
                }
            }

            public bool IsComplete => true;

            public void Collect(WorkCompletion completion) => owner.Collect(this, completion);
        }

        /// <summary>The numbers of one shape, read from its banks under a hold (worker): vertices, edges and boxes of every convex.</summary>
        internal static PropShape Copy(PhysicsOwnerShape shape, int id)
        {
            int count = shape.ConvexCount;
            var vertexStart = new int[count + 1];
            var edgeStart = new int[count + 1];
            for (int c = 0; c < count; c++)
            {
                ConvexBrepRange range = shape.Convex(c);
                vertexStart[c + 1] = vertexStart[c] + range.vertexCount;
                edgeStart[c + 1] = edgeStart[c] + range.edgeCount;
            }

            var vertices = new float[vertexStart[count] * 3];
            var edges = new int[edgeStart[count] * 2];
            var bounds = new float[count * 6];
            for (int c = 0; c < count; c++)
            {
                ConvexBrepRange range = shape.Convex(c);
                ConvexBrepBank bank = shape.BankOf(c);
                float3* v = bank.vertices + range.vertexBase;
                int at = vertexStart[c] * 3;
                for (int i = 0; i < range.vertexCount; i++)
                {
                    vertices[at++] = v[i].x; vertices[at++] = v[i].y; vertices[at++] = v[i].z;
                }

                BrepEdge* e = bank.edges + range.edgeBase;
                at = edgeStart[c] * 2;
                for (int i = 0; i < range.edgeCount; i++)
                {
                    edges[at++] = e[i].v0; edges[at++] = e[i].v1;
                }

                shape.ConvexBounds(c, out float3 lo, out float3 hi);
                bounds[c * 6] = lo.x; bounds[c * 6 + 1] = lo.y; bounds[c * 6 + 2] = lo.z;
                bounds[c * 6 + 3] = hi.x; bounds[c * 6 + 4] = hi.y; bounds[c * 6 + 5] = hi.z;
            }

            return new PropShape(id, vertices, vertexStart, edges, edgeStart, bounds);
        }
    }
}
