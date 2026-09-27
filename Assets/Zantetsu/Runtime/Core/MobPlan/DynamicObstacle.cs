using System;
using System.Collections.Generic;

namespace Zantetsu.Core.MobPlan
{
    public enum ObstaclePredictionMode { Hold, ConstantVelocity, Oracle }

    /// <summary>
    /// External dynamic circle (LOCOMOTION_SEARCH_PLAN.md Section 9.4 / S5): a scripted "player" that stops, walks at up to
    /// a maximum speed and turns at random, bouncing off the map. Every random draw is seeded from (seed, event index), so a
    /// state copy reproduces exactly the same future (used by the Oracle prediction). The planner never controls it.
    /// </summary>
    public sealed class DynamicCircle
    {
        public float X;
        public float Z;
        public float HeadingDeg;
        public float Speed;
        public float Radius;
        public float MaxSpeed = 1f;
        public float NextEventTime;
        public int EventIndex;
        public string State = "stop";
        public readonly int Seed;
        // Manual control (window: gamepad left stick / arrow keys): the scripted events are suspended while Manual is on.
        public bool Manual;
        public float CommandSpeed;          // m/s along HeadingDeg (negative = walking backwards)
        public float CommandYawRateDegps;   // + = clockwise (yaw increases)
        private readonly float minEvent;
        private readonly float maxEvent;

        public DynamicCircle(int seed, float x, float z, float radius, float maxSpeed, float minEventSeconds = 1f, float maxEventSeconds = 4f)
        {
            Seed = seed;
            X = x; Z = z; Radius = radius; MaxSpeed = maxSpeed;
            minEvent = minEventSeconds; maxEvent = maxEventSeconds;
            HeadingDeg = (float)(new Random(seed).NextDouble() * 360.0 - 180.0);
        }

        // 0.3 m/s .. MaxSpeed; a MaxSpeed below 0.3 m/s is used as is, so 0 really is a standing player.
        private float WalkSpeed(Random random) => MaxSpeed <= 0.3f ? Math.Max(0f, MaxSpeed) : 0.3f + (float)random.NextDouble() * (MaxSpeed - 0.3f);

        private Random EventRandom() => new Random(unchecked(Seed * 1000003 + EventIndex++));

        /// <summary>Advances the scripted motion: at each event either stop, start walking (random speed), or turn (random angle).</summary>
        public void Tick(float dt, float time, WalkableMap map)
        {
            if (Manual)
            {
                HeadingDeg = Pose2.WrapDeg(HeadingDeg + CommandYawRateDegps * dt);
                Speed = CommandSpeed;
                State = Math.Abs(CommandSpeed) > 0.01f ? "manual" : "manual stop";
                if (Math.Abs(Speed) <= 0.01f) return;
                var mx = (float)Math.Sin(HeadingDeg * Pose2.Deg2Rad) * Speed * dt;
                var mz = (float)Math.Cos(HeadingDeg * Pose2.Deg2Rad) * Speed * dt;
                // Blocked by the map: slide along one axis, else stay.
                if (map == null || map.ClearanceAt(X + mx, Z + mz) >= Radius) { X += mx; Z += mz; }
                else if (map.ClearanceAt(X + mx, Z) >= Radius) X += mx;
                else if (map.ClearanceAt(X, Z + mz) >= Radius) Z += mz;
                return;
            }

            if (time >= NextEventTime)
            {
                var random = EventRandom();
                NextEventTime = time + minEvent + (float)random.NextDouble() * (maxEvent - minEvent);
                var roll = random.NextDouble();
                if (roll < 0.3) { Speed = 0f; State = "stop"; }
                else if (roll < 0.65) { Speed = WalkSpeed(random); State = "walk"; }
                else
                {
                    HeadingDeg = Pose2.WrapDeg(HeadingDeg + (float)(random.NextDouble() * 360.0 - 180.0));
                    if (Speed <= 0f) Speed = WalkSpeed(random);
                    State = "turn";
                }
            }

            if (Speed <= 0f) return;
            var fx = (float)Math.Sin(HeadingDeg * Pose2.Deg2Rad);
            var fz = (float)Math.Cos(HeadingDeg * Pose2.Deg2Rad);
            var nx = X + fx * Speed * dt;
            var nz = Z + fz * Speed * dt;
            if (map != null && map.ClearanceAt(nx, nz) < Radius)
            {
                // Bounce: turn back with a random offset; the bounce is an event too so copies stay in sync.
                var random = EventRandom();
                HeadingDeg = Pose2.WrapDeg(HeadingDeg + 180f + (float)(random.NextDouble() * 120.0 - 60.0));
                NextEventTime = time + minEvent + (float)random.NextDouble() * (maxEvent - minEvent);
                return;
            }

            X = nx; Z = nz;
        }

        public DynamicCircle CloneState() => (DynamicCircle)MemberwiseClone();
    }

    /// <summary>Prediction handed to the planner: timed circle positions from `now` at the collision sample rate (held at the last sample afterwards).</summary>
    public sealed class ObstaclePrediction
    {
        public int Id;
        public float Radius;
        public WorldTrajectory Trajectory;

        public ObstaclePrediction(int id, float radius, WorldTrajectory trajectory) { Id = id; Radius = radius; Trajectory = trajectory; }

        /// <summary>Hold: stays where it is. ConstantVelocity: current velocity for `predictionSeconds`, then holds. Oracle: the scripted future.</summary>
        public static ObstaclePrediction Build(int id, DynamicCircle circle, ObstaclePredictionMode mode, float predictionSeconds, float now, float horizonSeconds, float hz, WalkableMap map)
        {
            var dt = 1f / hz;
            var count = Math.Max(2, (int)Math.Ceiling(horizonSeconds * hz) + 1);
            var trajectory = new WorldTrajectory { StartTime = now, Dt = dt, X = new float[count], Z = new float[count], Yaw = new float[count] };
            switch (mode)
            {
                case ObstaclePredictionMode.Oracle:
                {
                    var copy = circle.CloneState();
                    for (var i = 0; i < count; i++)
                    {
                        trajectory.X[i] = copy.X; trajectory.Z[i] = copy.Z; trajectory.Yaw[i] = copy.HeadingDeg;
                        copy.Tick(dt, now + i * dt, map);
                    }

                    break;
                }
                case ObstaclePredictionMode.ConstantVelocity:
                {
                    var fx = (float)Math.Sin(circle.HeadingDeg * Pose2.Deg2Rad) * circle.Speed;
                    var fz = (float)Math.Cos(circle.HeadingDeg * Pose2.Deg2Rad) * circle.Speed;
                    var lastX = circle.X; var lastZ = circle.Z;
                    for (var i = 0; i < count; i++)
                    {
                        var t = Math.Min(i * dt, predictionSeconds);
                        var x = circle.X + fx * t; var z = circle.Z + fz * t;
                        if (map != null && map.ClearanceAt(x, z) < circle.Radius * 0.5f) { x = lastX; z = lastZ; }
                        trajectory.X[i] = x; trajectory.Z[i] = z; trajectory.Yaw[i] = circle.HeadingDeg;
                        lastX = x; lastZ = z;
                    }

                    break;
                }
                default:
                {
                    for (var i = 0; i < count; i++) { trajectory.X[i] = circle.X; trajectory.Z[i] = circle.Z; trajectory.Yaw[i] = circle.HeadingDeg; }
                    break;
                }
            }

            trajectory.ComputeBounds();
            return new ObstaclePrediction(id, circle.Radius, trajectory);
        }
    }

    /// <summary>Running statistics of the real (not predicted) centre distance between the obstacle and its nearest NPC.</summary>
    [Serializable]
    public sealed class ObstacleStats
    {
        public int samples;
        public float minCenterDistance = float.PositiveInfinity;
        public float minSurfaceGap = float.PositiveInfinity;   // centre distance - (rObstacle + rNpc)
        public float overlapSeconds;                           // some NPC overlaps the obstacle
        public int overlapEvents;                              // transitions into overlap
        public float meanNearest;
        public float p5Nearest;
        public float p50Nearest;
        public float withinOneMeterSeconds;                    // surface gap < 1 m
        public float loiterAgentSeconds;                       // agent-seconds spent stationary with surface gap < 1 m
        private float sumNearest;
        private readonly List<float> nearestPerSample = new List<float>();
        private bool wasOverlapping;

        public void Record(float dt, float nearestCenter, float nearestGap)
        {
            samples++;
            sumNearest += nearestCenter;
            nearestPerSample.Add(nearestCenter);
            if (nearestCenter < minCenterDistance) minCenterDistance = nearestCenter;
            if (nearestGap < minSurfaceGap) minSurfaceGap = nearestGap;
            var overlapping = nearestGap < 0f;
            if (overlapping) overlapSeconds += dt;
            if (nearestGap < 1f) withinOneMeterSeconds += dt;
            if (overlapping && !wasOverlapping) overlapEvents++;
            wasOverlapping = overlapping;
        }

        public void Finish()
        {
            meanNearest = samples > 0 ? sumNearest / samples : 0f;
            p5Nearest = Percentile(0.05);
            p50Nearest = Percentile(0.5);
        }

        public float Percentile(double p)
        {
            if (nearestPerSample.Count == 0) return 0f;
            var sorted = new List<float>(nearestPerSample);
            sorted.Sort();
            var index = (int)Math.Ceiling(p * sorted.Count) - 1;
            return sorted[Math.Max(0, Math.Min(sorted.Count - 1, index))];
        }
    }

    /// <summary>Density around the player: NPCs inside the ring, how many of them walk, passes beside the player and NPC-NPC encounters.</summary>
    public sealed class FlowStats
    {
        public int samples;
        public float ringMeters;
        public float sumWithinRing;
        public float sumMovingWithinRing;
        public int maxWithinRing;
        public float sumWithinHalfRing;
        public int passEvents;                 // NPC enters the pass radius (8 m) of the player
        public int encounters;                 // two NPCs inside the ring come within 1 m surface gap (entry events)
        public float seconds;
        public float MeanWithinRing => samples > 0 ? sumWithinRing / samples : 0f;
        public float MeanMovingWithinRing => samples > 0 ? sumMovingWithinRing / samples : 0f;
        public float MovingFraction => sumWithinRing > 0f ? sumMovingWithinRing / sumWithinRing : 0f;
        public float MeanWithinHalfRing => samples > 0 ? sumWithinHalfRing / samples : 0f;
        public float Density => ringMeters > 0f ? MeanWithinRing / (float)(Math.PI * ringMeters * ringMeters) : 0f;
        public float PassesPerMinute => seconds > 0f ? passEvents / seconds * 60f : 0f;
        public float EncountersPerMinute => seconds > 0f ? encounters / seconds * 60f : 0f;
        private readonly HashSet<long> touching = new HashSet<long>();
        public const float PassRadius = 8f;

        public void Record(float dt, List<SimAgent> agents, ClipDataset dataset, float fx, float fz)
        {
            samples++;
            seconds += dt;
            var within = 0; var moving = 0; var half = 0;
            for (var i = 0; i < agents.Count; i++)
            {
                var a = agents[i];
                var d = a.Pose.DistanceTo(fx, fz);
                var stationary = a.CurrentClip >= 0 && dataset.Clips[a.CurrentClip].IsStationary;
                if (d <= ringMeters) { within++; if (!stationary) moving++; if (d <= ringMeters * 0.5f) half++; }
                var inPass = a.InPassRing ? d < PassRadius + 1f : d < PassRadius;
                if (inPass && !a.InPassRing) passEvents++;
                a.InPassRing = inPass;
                if (d > ringMeters) continue;
                for (var j = i + 1; j < agents.Count; j++)
                {
                    var b = agents[j];
                    var key = ((long)a.Id << 32) | (uint)b.Id;
                    var gap = Pose2.Distance(a.Pose, b.Pose) - a.Radius - b.Radius;
                    var was = touching.Contains(key);
                    var now = was ? gap < 1.3f : gap < 1f;
                    if (now && !was) { encounters++; touching.Add(key); }
                    else if (!now && was) touching.Remove(key);
                }
            }

            sumWithinRing += within; sumMovingWithinRing += moving; sumWithinHalfRing += half;
            if (within > maxWithinRing) maxWithinRing = within;
        }

        public string Describe() => $"ring {ringMeters:0} m: mean {MeanWithinRing:F1} NPC ({Density * 1000f:F1} per 1000 m2, {MovingFraction * 100f:F0}% walking, max {maxWithinRing}), inner {ringMeters * 0.5f:0} m: {MeanWithinHalfRing:F1}, passes < {PassRadius:0} m {PassesPerMinute:F1}/min, NPC-NPC encounters {EncountersPerMinute:F1}/min";
    }
}
