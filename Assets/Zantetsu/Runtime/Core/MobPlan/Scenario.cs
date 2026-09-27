using System;
using System.Collections.Generic;

namespace Zantetsu.Core.MobPlan
{
    // Scene: convex obstacle polygons exported from a Unity scene (polygons.bin, see Reports/LocomotionSearch/SCENE_WALKABLE_MAP.md).
    public enum MapPreset { Arena, Corridor, TJunction, Scene }
    // PlayerNear: goals uniformly inside the ring around the player (naive density; NPCs idle near the player).
    // PlayerFlow: pass-through goals: a waypoint beside the player, then a far goal beyond it, so the ring holds walking NPCs only.
    public enum GoalPreset { OpenCrossing, RandomGoals, CorridorFlow, PlayerNear, PlayerFlow }

    /// <summary>Scenario definition (Section 16): map, NPC count, seed, radius, goal assignment. JSON-serialisable.</summary>
    [Serializable]
    public sealed class ScenarioSettings
    {
        public MapPreset map = MapPreset.Arena;
        public float mapSizeMeters = 24f;
        public float cellSizeMeters = 0.1f;
        public int pillarCount = 4;
        public float pillarRadius = 0.5f;
        public float corridorWidthMeters = 2.4f;
        // Scene map: polygons.bin path (absolute, or relative to the project root) and the window the scenario uses
        // (sceneWindowMeters <= 0 = the whole scene). NPC starts and goals are drawn inside the window; obstacles just
        // outside it still count for clearance.
        public string sceneMapPath = "Generated/LocomotionSearch/SceneMaps/tiled-seed2/polygons.bin";
        public float sceneCenterX = -220f;
        public float sceneCenterZ = -170f;
        public float sceneWindowMeters = 60f;
        public float sceneClearanceCapMeters = 12f;
        public float sceneWindowMarginMeters = 15f;    // walkable margin outside the window (NPCs may step out; starts and goals stay inside)
        public GoalPreset goals = GoalPreset.OpenCrossing;
        public int npcCount = 10;
        public int seed = 1;
        public float npcRadius = 0.35f;
        public float crossingRadius = 8f;
        public bool reassignGoals = true;
        // External dynamic circle ("player", Section 9.4): scripted stop / walk / turn, best-effort avoidance only.
        public bool dynamicObstacle = false;
        public float obstacleRadius = 1.5f;
        public float obstacleMaxSpeed = 1f;
        public int obstacleSeed = 7;
        public ObstaclePredictionMode obstaclePrediction = ObstaclePredictionMode.Hold;
        public float obstaclePredictionSeconds = 2f;
        public float goalNearObstacleProbability = 0f;   // test knob: new goals land inside the obstacle circle with this probability
        // Player-centred goal presets (the focus is the obstacle circle, or the map centre without one).
        public float flowRingMeters = 20f;               // ring around the player that the density metrics (and PlayerNear goals) use
        public float startRadiusMeters = 0f;             // PlayerNear / PlayerFlow: starts inside this radius of the player (0 = the player ring); other presets: 0 = the whole window
        public float flowPassOffsetMinMeters = 2.5f;     // pass waypoint: lateral offset from the player, uniform in [min, max], random side
        public float flowPassOffsetMaxMeters = 8f;
        public float flowFarGoalMeters = 27f;            // far goal distance from the player after passing (idle happens there)
        public float flowFarGoalConeDeg = 60f;           // far goal direction within +-cone of the pass direction (no U-turn)
        // Recycling (player presets): an NPC left behind is teleported to an unseen spot near the player and given a new pass goal.
        public bool recycleEnabled = false;
        public float recycleDistanceMeters = 35f;        // farther than this from the player (and unseen) => recycle
        public float recycleSpawnMinMeters = 15f;        // spawn distance band around the player
        public float recycleSpawnMaxMeters = 25f;
        public float recycleViewDeg = 110f;              // player's field of view around HeadingDeg; inside it a spot must be occluded
        public float recycleViewRangeMeters = 50f;       // beyond this the player sees nothing
        public bool recycleOcclusionInView = true;       // allow spawning inside the view when a building hides the spot
        public float recycleCooldownSeconds = 5f;
        // Player attack (window: gamepad A / space): NPCs inside the fan die at once and respawn with the recycle spawn rule.
        public float attackRangeMeters = 10f;
        public float attackAngleDeg = 120f;

        public ScenarioSettings Clone() => (ScenarioSettings)MemberwiseClone();

        public string Describe() => $"{map} {(map == MapPreset.Scene ? $"{System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(sceneMapPath ?? string.Empty))} ({sceneCenterX:0},{sceneCenterZ:0}) {sceneWindowMeters:0}m" : $"{mapSizeMeters:0}m")}/{cellSizeMeters:0.##} {goals} n={npcCount} seed={seed} r={npcRadius}{(dynamicObstacle ? $" obstacle r{obstacleRadius} v{obstacleMaxSpeed} {obstaclePrediction}" : "")}";
    }

    public sealed class SimAgent
    {
        public int Id;
        public float Radius;
        public Pose2 Start;
        public float GoalX;
        public float GoalZ;
        public AgentPlan Plan;
        public Pose2 Pose;
        public int CurrentClip = -1;
        public float ClipLocalTime;
        public int GoalsReached;
        public float NextReplanTime;
        public int GoalRevision;
        public bool GoalBlocked;              // the goal is inside the external circle; the planner is given the standoff point
        public float StandoffX;
        public float StandoffZ;
        public float BlockedWaitSeconds;
        public int GoalsAbandoned;
        public int FlowPhase;                 // PlayerFlow: 0 none, 1 heading to the pass waypoint (GoalX/Z), 2 heading to the far goal
        public float FlowOffset;              // signed lateral offset of the pass waypoint
        public float FlowDirX;                // approach direction when the waypoint was last placed
        public float FlowDirZ;
        public int PassCount;                 // waypoint switches (passes beside the player)
        public bool InPassRing;               // metrics: currently within the pass radius of the player
        public float PlanGoalX => GoalBlocked ? StandoffX : GoalX;
        public float PlanGoalZ => GoalBlocked ? StandoffZ : GoalZ;
        public int PlannedGoalRevision;
        public float BestGoalDistance = float.PositiveInfinity;   // stuck detection: best distance seen for the current goal revision
        public float LastProgressTime;
        public int ProgressRevision = -1;
        public int StuckGoals;
        public float LastRecycleTime = float.NegativeInfinity;
        public int Recycles;
        public int Deaths;
        public int Tier;
        public float TravelDistance;
        public float StationarySeconds;
        public float MovingSeconds;
        public float LastGoalTime;
        public List<float> GoalTimes = new List<float>();
    }

    /// <summary>Builds enclosed maps and NPC scenarios deterministically from a seed.</summary>
    public static class ScenarioBuilder
    {
        /// <summary>Resolves a scenario path against the project root (the working directory's parent of Assets when running in Unity, else the working directory).</summary>
        public static string ResolveProjectPath(string path)
        {
            if (string.IsNullOrEmpty(path) || System.IO.Path.IsPathRooted(path)) return path;
            var root = ProjectRoot ?? System.IO.Directory.GetCurrentDirectory();
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
        }

        /// <summary>Set by the host (Editor / Player runner) when the working directory is not the project root.</summary>
        public static string ProjectRoot;

        public static WalkableMap BuildMap(ScenarioSettings s)
        {
            var cell = Math.Max(0.02f, s.cellSizeMeters);
            if (s.map == MapPreset.Scene)
            {
                var path = ResolveProjectPath(s.sceneMapPath);
                if (!System.IO.File.Exists(path)) throw new System.IO.FileNotFoundException("scene map not found (export it with Tools/SceneWalkableExport first)", path);
                return PolygonWalkable.Load(path, s.sceneCenterX, s.sceneCenterZ, s.sceneWindowMeters, s.sceneWindowMeters, cell, s.sceneClearanceCapMeters, s.sceneWindowMarginMeters);
            }

            WalkableRaster map;
            switch (s.map)
            {
                case MapPreset.Corridor:
                {
                    var length = s.mapSizeMeters;
                    var width = s.corridorWidthMeters + 2f * cell * 2;
                    map = new WalkableRaster((int)Math.Ceiling(length / cell), (int)Math.Ceiling(width / cell), cell, -length * 0.5f, -width * 0.5f);
                    map.BlockBorder(2);
                    map.Description = $"corridor {length:0} x {s.corridorWidthMeters:0.0} m";
                    break;
                }
                case MapPreset.TJunction:
                {
                    var size = s.mapSizeMeters;
                    map = new WalkableRaster((int)Math.Ceiling(size / cell), (int)Math.Ceiling(size / cell), cell, -size * 0.5f, -size * 0.5f);
                    // Block everything, then carve a horizontal bar (top) and a vertical stem.
                    for (var i = 0; i < map.Walkable.Length; i++) map.Walkable[i] = false;
                    var w = s.corridorWidthMeters;
                    Carve(map, -size * 0.5f + 2 * cell, size * 0.25f - w * 0.5f, size * 0.5f - 2 * cell, size * 0.25f + w * 0.5f);
                    Carve(map, -w * 0.5f, -size * 0.5f + 2 * cell, w * 0.5f, size * 0.25f + w * 0.5f);
                    map.Description = $"T-junction {size:0} m, corridor {w:0.0} m";
                    break;
                }
                default:
                {
                    var size = s.mapSizeMeters;
                    map = new WalkableRaster((int)Math.Ceiling(size / cell), (int)Math.Ceiling(size / cell), cell, -size * 0.5f, -size * 0.5f);
                    map.BlockBorder(2);
                    var random = new Random(s.seed * 7919 + 13);
                    for (var i = 0; i < s.pillarCount; i++)
                    {
                        // Pillars on a ring at 0.3 x size so crossing paths through the centre are disturbed but not blocked.
                        var angle = (i + 0.5f) * (float)(2.0 * Math.PI / Math.Max(1, s.pillarCount)) + (float)random.NextDouble() * 0.3f;
                        var radius = size * 0.18f + (float)random.NextDouble() * size * 0.05f;
                        map.BlockCircle((float)Math.Sin(angle) * radius, (float)Math.Cos(angle) * radius, s.pillarRadius);
                    }

                    map.Description = $"arena {size:0} m, {s.pillarCount} pillars r {s.pillarRadius:0.0}";
                    break;
                }
            }

            map.ComputeClearance();
            return map;
        }

        private static void Carve(WalkableRaster map, float x0, float z0, float x1, float z1)
        {
            var cx0 = Math.Max(0, map.CellX(x0));
            var cz0 = Math.Max(0, map.CellZ(z0));
            var cx1 = Math.Min(map.Width - 1, map.CellX(x1));
            var cz1 = Math.Min(map.Height - 1, map.CellZ(z1));
            for (var cz = cz0; cz <= cz1; cz++)
                for (var cx = cx0; cx <= cx1; cx++)
                    map.Walkable[cz * map.Width + cx] = true;
        }

        /// <summary>Start poses and first goals. Starts are pushed to walkable clearance and kept mutually apart.</summary>
        public static List<SimAgent> BuildAgents(ScenarioSettings s, WalkableMap map, PlannerConfig config)
        {
            var agents = new List<SimAgent>();
            var random = new Random(s.seed);
            var required = s.npcRadius + config.staticMarginMeters + 0.1f;
            for (var i = 0; i < s.npcCount; i++)
            {
                var agent = new SimAgent { Id = i, Radius = s.npcRadius };
                float sx, sz, gx, gz;
                if (s.goals == GoalPreset.OpenCrossing && s.map != MapPreset.Corridor)
                {
                    var angle = (float)(2.0 * Math.PI * i / s.npcCount);
                    var r = Math.Min(s.crossingRadius, Math.Min(map.SampleWidth, map.SampleHeight) * 0.5f - 1.5f);
                    sx = map.CenterX + (float)Math.Sin(angle) * r;
                    sz = map.CenterZ + (float)Math.Cos(angle) * r;
                    gx = map.CenterX - (float)Math.Sin(angle) * r;
                    gz = map.CenterZ - (float)Math.Cos(angle) * r;
                }
                else if (s.goals == GoalPreset.CorridorFlow)
                {
                    // Uniform spread along X (density = area / n), alternating direction, mixed lanes; goal = far end of the map in that direction.
                    var margin = 1.5f;
                    var usable = Math.Max(1f, map.WorldWidth - 2f * margin);
                    var spacing = usable / s.npcCount;
                    var side = i % 2 == 0 ? 1f : -1f;
                    var lane = (i / 2) % 2 == 0 ? -1f : 1f;
                    sx = map.OriginX + margin + (i + 0.5f) * spacing;
                    sz = map.CenterZ + lane * Math.Max(0.4f, s.corridorWidthMeters * 0.25f);
                    gx = side > 0f ? map.MaxX - margin : map.OriginX + margin;
                    gz = sz;
                }
                else if (s.goals == GoalPreset.PlayerNear || s.goals == GoalPreset.PlayerFlow)
                {
                    // Starts around the player (the obstacle starts at the map centre): uniform in the annulus [keep-out, start radius],
                    // so a large scene window does not scatter the NPCs hundreds of metres away. The simulation assigns the goals.
                    var startRadius = s.startRadiusMeters > 0f ? s.startRadiusMeters : s.flowRingMeters;
                    var keepOut = s.dynamicObstacle ? s.obstacleRadius + s.npcRadius + 1f : 1f;
                    if (!TryFreePointNear(map, random, required, agents, s.npcRadius, map.CenterX, map.CenterZ, keepOut, startRadius, out sx, out sz)
                        && !TryFreePoint(map, random, required, agents, s.npcRadius, out sx, out sz)) { sx = map.CenterX; sz = map.CenterZ; }
                    gx = sx; gz = sz;
                }
                else if (s.map == MapPreset.Corridor)
                {
                    // Alternate sides: even ids start left going right, odd ids start right going left, staggered along the corridor.
                    var side = i % 2 == 0 ? -1f : 1f;
                    var lane = (i / 2) % 2 == 0 ? -0.5f : 0.5f;
                    var offset = (i / 4) * 1.6f;
                    sx = map.CenterX + side * (map.WorldWidth * 0.5f - 1.5f - offset);
                    sz = map.CenterZ + lane * Math.Max(0.5f, s.corridorWidthMeters * 0.45f);
                    gx = map.CenterX - side * (map.WorldWidth * 0.5f - 1.5f - offset);
                    gz = map.CenterZ - lane * Math.Max(0.5f, s.corridorWidthMeters * 0.45f);
                }
                else
                {
                    sx = map.CenterX; sz = map.CenterZ;
                    var placed = s.startRadiusMeters > 0f && TryFreePointNear(map, random, required, agents, s.npcRadius, map.CenterX, map.CenterZ, 1f, s.startRadiusMeters, out sx, out sz);
                    if (!placed && !TryFreePoint(map, random, required, agents, s.npcRadius, out sx, out sz)) { sx = map.CenterX; sz = map.CenterZ; }
                    PickGoal(map, random, required, config.minGoalDistanceMeters, sx, sz, out gx, out gz);
                }

                // Preset geometry (crossing circle, corridor lanes) ignores obstacles: push both ends to clearance, and when the
                // pushed start lands on another NPC (scene maps: several starts pushed out of the same building) draw a free point.
                var samePoint = gx == sx && gz == sz;
                NudgeToClearance(map, required, ref sx, ref sz);
                if (!map.PointSafe(sx, sz, required) || TooClose(agents, sx, sz, s.npcRadius))
                {
                    if (TryFreePoint(map, random, required, agents, s.npcRadius, out var fx, out var fz)) { sx = fx; sz = fz; }
                }

                if (samePoint) { gx = sx; gz = sz; }
                else NudgeToClearance(map, required + 0.3f, ref gx, ref gz);
                var yaw = gx == sx && gz == sz ? (float)(random.NextDouble() * 360.0 - 180.0) : (float)Math.Atan2(gx - sx, gz - sz) * Pose2.Rad2Deg;
                agent.Start = new Pose2(sx, sz, yaw);
                agent.Pose = agent.Start;
                agent.GoalX = gx; agent.GoalZ = gz;
                agents.Add(agent);
            }

            return agents;
        }

        public static bool TooClose(List<SimAgent> existing, float x, float z, float radius)
        {
            foreach (var other in existing)
            {
                var dx = other.Start.X - x; var dz = other.Start.Z - z;
                if (dx * dx + dz * dz < (radius + other.Radius + 0.6f) * (radius + other.Radius + 0.6f)) return true;
            }

            return false;
        }

        /// <summary>Random free point uniformly distributed in the annulus [minRadius, maxRadius] around (cx, cz), clear of obstacles and other starts.</summary>
        public static bool TryFreePointNear(WalkableMap map, Random random, float required, List<SimAgent> existing, float radius, float cx, float cz, float minRadius, float maxRadius, out float x, out float z)
        {
            maxRadius = Math.Max(maxRadius, minRadius + 0.5f);
            for (var attempt = 0; attempt < 500; attempt++)
            {
                var angle = random.NextDouble() * Math.PI * 2.0;
                var rho = Math.Sqrt(random.NextDouble() * (maxRadius * maxRadius - minRadius * minRadius) + minRadius * minRadius);
                x = cx + (float)(Math.Cos(angle) * rho); z = cz + (float)(Math.Sin(angle) * rho);
                if (map.ClearanceAt(x, z) < required) continue;
                if (!TooClose(existing, x, z, radius)) return true;
            }

            x = 0f; z = 0f;
            return false;
        }

        public static bool TryFreePoint(WalkableMap map, Random random, float required, List<SimAgent> existing, float radius, out float x, out float z)
        {
            for (var attempt = 0; attempt < 500; attempt++)
            {
                if (!map.TryRandomPoint(random, required, out x, out z, 50)) continue;
                if (!TooClose(existing, x, z, radius)) return true;
            }

            x = 0f; z = 0f;
            return false;
        }

        public static void PickGoal(WalkableMap map, Random random, float required, float minDistance, float fromX, float fromZ, out float gx, out float gz)
        {
            var bestX = fromX; var bestZ = fromZ; var bestDistance = -1f;
            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (!map.TryRandomPoint(random, required + 0.3f, out var x, out var z, 50)) continue;
                var dx = x - fromX; var dz = z - fromZ;
                var distance = (float)Math.Sqrt(dx * dx + dz * dz);
                if (distance >= minDistance) { gx = x; gz = z; return; }
                if (distance > bestDistance) { bestDistance = distance; bestX = x; bestZ = z; }
            }

            gx = bestX; gz = bestZ;
        }

        /// <summary>
        /// Moves the point to the nearest position (spiral of rings) with the required clearance: 0.1 m rings up to 3 m,
        /// then 0.5 m rings up to maxRadius (buildings in scene maps are tens of metres wide). Falls back to the best
        /// clearance seen.
        /// </summary>
        public static void NudgeToClearance(WalkableMap map, float required, ref float x, ref float z, float maxRadius = 40f)
        {
            if (map.ClearanceAt(x, z) >= required) return;
            var best = map.ClearanceAt(x, z);
            var bx = x; var bz = z;
            for (var ring = 1; ; ring++)
            {
                var r = ring <= 30 ? ring * 0.1f : 3f + (ring - 30) * 0.5f;
                if (r > maxRadius) break;
                var directions = r <= 3f ? 16 : Math.Min(64, (int)(r * 8f));
                for (var k = 0; k < directions; k++)
                {
                    var a = k * (float)(2.0 * Math.PI / directions);
                    var px = x + (float)Math.Sin(a) * r;
                    var pz = z + (float)Math.Cos(a) * r;
                    var c = map.ClearanceAt(px, pz);
                    if (c > best) { best = c; bx = px; bz = pz; }
                    if (c >= required) { x = px; z = pz; return; }
                }
            }

            x = bx; z = bz;
        }
    }
}
