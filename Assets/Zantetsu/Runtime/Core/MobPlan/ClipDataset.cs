using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>One planning node: a clip (or yaw-rate variant) with its normalised root trajectory and graph successors.</summary>
    [Serializable]
    public sealed class ClipDescriptor
    {
        public int Id;
        public string NodeId = string.Empty;       // probe node id ("path#clip", "...~yaw+10")
        public string ClipId = string.Empty;       // probe clip id without the yaw suffix
        public string DisplayName = string.Empty;  // short label
        public string Category = string.Empty;     // catalog category (LocomotionV2, Turn, Idle, ...)
        public float Duration;
        /// <summary>Clip-local time where this node starts (sub-clip segments from the subdivision plan); 0 for whole clips.</summary>
        public float RangeFrom;
        public bool IsLoop;
        public bool IsStationary;                  // small total displacement and yaw
        public bool IsWaitCapable;                 // stationary + self edge (or manifest)
        public bool CanReachWait;                  // a stop path within the limit exists
        public float StopPathSeconds = float.PositiveInfinity;
        public int[] StopPath = Array.Empty<int>();    // successor ids that lead to a wait-capable node (excluding this node)
        public int StationaryContinuation = -1;        // the one stationary successor exposed to the search (Section 11.4)
        public int SampleOffset;
        public int SampleCount;
        public float EndX;
        public float EndZ;
        public float EndYaw;
        public float TotalDisplacement;
        public float TotalYaw;
        public float AverageSpeed;
        public float YawRateDegps;                 // variant rate, 0 for the original clip
        public int[] Outgoing = Array.Empty<int>();    // planning-set successors in ascending id order
        public int RawOutgoingCount;               // successors in the full probe graph (before planning-set filtering)
        public bool HasSelfEdge;
        /// <summary>Max root distance from the node start over all samples (derived, not stored): a clip placed at P stays inside the circle (P, MaxRadius).</summary>
        public float MaxRadius;
        /// <summary>Group index of the base clip (+range): a clip and its yaw-rate variants share one family (derived, not stored).</summary>
        public int FamilyId;
        /// <summary>Speed-weighted mean angle (deg) between the root velocity and the root facing: 0 walking forward, 90 strafing, 180 backing up (derived).</summary>
        public float FacingErrorDeg;
        /// <summary>Sum of |yaw change| over the samples (deg): a wiggle that returns to the start heading counts here, not in TotalYaw (derived).</summary>
        public float YawTravelDeg;
        /// <summary>Moves forward with little turning: displacement >= 0.3 m, facing error < 25 deg, |TotalYaw| < 20 deg (derived).</summary>
        public bool IsStraight;

        public Pose2 EndPose => new Pose2(EndX, EndZ, EndYaw);
    }

    /// <summary>
    /// Root-motion-only planning dataset (LOCOMOTION_SEARCH_PLAN.md Section 7). Trajectories are normalised so that the
    /// clip start root is the identity: Ĥ(t) = H(0)^-1 H(t). World root at time t of a clip placed at W is W × Ĥ(t),
    /// and the next clip starts at W × Ĥ(T). Samples are uniform at SampleHz plus an exact end sample.
    /// </summary>
    public sealed class ClipDataset
    {
        public const string FormatMagic = "ZLSDS001";

        public string CharacterId = string.Empty;
        public string SourceSetName = string.Empty;
        public string ToleranceDescription = string.Empty;
        public string BuildDescription = string.Empty;
        public float SampleHz = 30f;
        public ClipDescriptor[] Clips = Array.Empty<ClipDescriptor>();
        public float[] T = Array.Empty<float>();
        public float[] X = Array.Empty<float>();
        public float[] Z = Array.Empty<float>();
        public float[] Yaw = Array.Empty<float>();
        public int EdgeCount;
        public int WaitCapableCount;
        public int CanReachWaitCount;
        public int SourceNodeCount;
        public int SourceEdgeCount;

        public int Count => Clips.Length;

        private Dictionary<string, int> indexByNodeId;

        public int IndexOf(string nodeId)
        {
            if (indexByNodeId == null)
            {
                indexByNodeId = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < Clips.Length; i++) indexByNodeId[Clips[i].NodeId] = i;
            }

            return indexByNodeId.TryGetValue(nodeId, out var index) ? index : -1;
        }

        public bool HasEdge(int from, int to)
        {
            var outgoing = Clips[from].Outgoing;
            return Array.BinarySearch(outgoing, to) >= 0;
        }

        /// <summary>Normalised root pose at a clip-local time (clamped). XZ linear, yaw shortest arc.</summary>
        public Pose2 LocalPoseAt(int clip, float localTime)
        {
            var c = Clips[clip];
            if (c.SampleCount == 0) return Pose2.Identity;
            if (localTime <= 0f) return new Pose2(X[c.SampleOffset], Z[c.SampleOffset], Yaw[c.SampleOffset]);
            if (localTime >= c.Duration) return c.EndPose;
            var first = (int)Math.Floor(localTime * SampleHz);
            if (first >= c.SampleCount - 1) return c.EndPose;
            var i0 = c.SampleOffset + first;
            var i1 = i0 + 1;
            var span = T[i1] - T[i0];
            var w = span > 0f ? (localTime - T[i0]) / span : 0f;
            if (w < 0f) w = 0f; else if (w > 1f) w = 1f;
            return new Pose2(X[i0] + (X[i1] - X[i0]) * w, Z[i0] + (Z[i1] - Z[i0]) * w, Pose2.WrapDeg(Yaw[i0] + Pose2.WrapDeg(Yaw[i1] - Yaw[i0]) * w));
        }

        public void Save(string absolutePath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath) ?? ".");
            using var stream = new FileStream(absolutePath, FileMode.Create, FileAccess.Write);
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(Encoding.ASCII.GetBytes(FormatMagic));
            writer.Write(CharacterId); writer.Write(SourceSetName); writer.Write(ToleranceDescription); writer.Write(BuildDescription);
            writer.Write(SampleHz);
            writer.Write(EdgeCount); writer.Write(WaitCapableCount); writer.Write(CanReachWaitCount); writer.Write(SourceNodeCount); writer.Write(SourceEdgeCount);
            writer.Write(Clips.Length);
            foreach (var c in Clips)
            {
                writer.Write(c.Id); writer.Write(c.NodeId); writer.Write(c.ClipId); writer.Write(c.DisplayName); writer.Write(c.Category);
                writer.Write(c.Duration); writer.Write(c.RangeFrom); writer.Write(c.IsLoop); writer.Write(c.IsStationary); writer.Write(c.IsWaitCapable); writer.Write(c.CanReachWait);
                writer.Write(c.StopPathSeconds); WriteInts(writer, c.StopPath); writer.Write(c.StationaryContinuation);
                writer.Write(c.SampleOffset); writer.Write(c.SampleCount); writer.Write(c.EndX); writer.Write(c.EndZ); writer.Write(c.EndYaw);
                writer.Write(c.TotalDisplacement); writer.Write(c.TotalYaw); writer.Write(c.AverageSpeed); writer.Write(c.YawRateDegps);
                WriteInts(writer, c.Outgoing); writer.Write(c.RawOutgoingCount); writer.Write(c.HasSelfEdge);
            }

            WriteFloats(writer, T); WriteFloats(writer, X); WriteFloats(writer, Z); WriteFloats(writer, Yaw);
        }

        public static ClipDataset Load(string absolutePath)
        {
            return Read(File.ReadAllBytes(absolutePath));
        }
        public static ClipDataset Read(byte[] bytes)
        {
            using var reader = new BinaryReader(new MemoryStream(bytes, false), Encoding.UTF8);
            if (Encoding.ASCII.GetString(reader.ReadBytes(FormatMagic.Length)) != FormatMagic) throw new InvalidDataException("not a locomotion dataset");
            var dataset = new ClipDataset
            {
                CharacterId = reader.ReadString(), SourceSetName = reader.ReadString(), ToleranceDescription = reader.ReadString(), BuildDescription = reader.ReadString(),
                SampleHz = reader.ReadSingle(),
                EdgeCount = reader.ReadInt32(), WaitCapableCount = reader.ReadInt32(), CanReachWaitCount = reader.ReadInt32(), SourceNodeCount = reader.ReadInt32(), SourceEdgeCount = reader.ReadInt32()
            };
            dataset.Clips = new ClipDescriptor[reader.ReadInt32()];
            for (var i = 0; i < dataset.Clips.Length; i++)
            {
                var c = new ClipDescriptor
                {
                    Id = reader.ReadInt32(), NodeId = reader.ReadString(), ClipId = reader.ReadString(), DisplayName = reader.ReadString(), Category = reader.ReadString(),
                    Duration = reader.ReadSingle(), RangeFrom = reader.ReadSingle(), IsLoop = reader.ReadBoolean(), IsStationary = reader.ReadBoolean(), IsWaitCapable = reader.ReadBoolean(), CanReachWait = reader.ReadBoolean(),
                    StopPathSeconds = reader.ReadSingle()
                };
                c.StopPath = ReadInts(reader); c.StationaryContinuation = reader.ReadInt32();
                c.SampleOffset = reader.ReadInt32(); c.SampleCount = reader.ReadInt32(); c.EndX = reader.ReadSingle(); c.EndZ = reader.ReadSingle(); c.EndYaw = reader.ReadSingle();
                c.TotalDisplacement = reader.ReadSingle(); c.TotalYaw = reader.ReadSingle(); c.AverageSpeed = reader.ReadSingle(); c.YawRateDegps = reader.ReadSingle();
                c.Outgoing = ReadInts(reader); c.RawOutgoingCount = reader.ReadInt32(); c.HasSelfEdge = reader.ReadBoolean();
                dataset.Clips[i] = c;
            }

            dataset.T = ReadFloats(reader); dataset.X = ReadFloats(reader); dataset.Z = ReadFloats(reader); dataset.Yaw = ReadFloats(reader);
            dataset.ComputeDerived();
            return dataset;
        }

        private static void WriteInts(BinaryWriter writer, int[] values)
        {
            writer.Write(values.Length);
            foreach (var v in values) writer.Write(v);
        }

        private static void WriteFloats(BinaryWriter writer, float[] values)
        {
            writer.Write(values.Length);
            foreach (var v in values) writer.Write(v);
        }

        private static int[] ReadInts(BinaryReader reader)
        {
            var values = new int[reader.ReadInt32()];
            for (var i = 0; i < values.Length; i++) values[i] = reader.ReadInt32();
            return values;
        }

        private static float[] ReadFloats(BinaryReader reader)
        {
            var values = new float[reader.ReadInt32()];
            for (var i = 0; i < values.Length; i++) values[i] = reader.ReadSingle();
            return values;
        }

        // ---- graph preprocessing (Section 7.3, 11.4) ----

        /// <summary>
        /// Fills StationaryContinuation, StopPath / StopPathSeconds / CanReachWait from Outgoing and the wait-capable flags.
        /// Stop paths are duration-shortest paths (Dijkstra from every wait-capable node over reversed edges).
        /// </summary>
        /// <summary>Derived per-clip values that are not stored: bounding radius and yaw-variant family ids.</summary>
        public void ComputeDerived()
        {
            var families = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var c in Clips)
            {
                var maxSq = 0f;
                for (var i = 0; i < c.SampleCount; i++)
                {
                    var index = c.SampleOffset + i;
                    var d = X[index] * X[index] + Z[index] * Z[index];
                    if (d > maxSq) maxSq = d;
                }

                c.MaxRadius = (float)Math.Sqrt(maxSq);
                // Facing error and yaw travel from consecutive samples (velocity below 0.15 m/s is ignored: standing, foot shuffles).
                var weighted = 0.0; var weight = 0.0; var yawTravel = 0.0;
                for (var i = 1; i < c.SampleCount; i++)
                {
                    var index = c.SampleOffset + i;
                    var dt = T[index] - T[index - 1];
                    if (dt <= 0f) continue;
                    var vx = (X[index] - X[index - 1]) / dt; var vz = (Z[index] - Z[index - 1]) / dt;
                    var speed = Math.Sqrt(vx * vx + vz * vz);
                    yawTravel += Math.Abs(Pose2.WrapDeg(Yaw[index] - Yaw[index - 1]));
                    if (speed < 0.15) continue;
                    var yaw = (Yaw[index] + Yaw[index - 1]) * 0.5f * Pose2.Deg2Rad;
                    var fx = Math.Sin(yaw); var fz = Math.Cos(yaw);
                    var cosine = Math.Max(-1.0, Math.Min(1.0, (vx * fx + vz * fz) / speed));
                    weighted += Math.Acos(cosine) * Pose2.Rad2Deg * speed * dt;
                    weight += speed * dt;
                }

                c.FacingErrorDeg = weight > 0.0 ? (float)(weighted / weight) : 0f;
                c.YawTravelDeg = (float)yawTravel;
                c.IsStraight = c.TotalDisplacement >= 0.3f && c.FacingErrorDeg < 25f && Math.Abs(c.TotalYaw) < 20f;
                var tilde = c.NodeId.LastIndexOf("~yaw", StringComparison.Ordinal);
                var family = tilde >= 0 ? c.NodeId.Substring(0, tilde) : c.NodeId;
                if (!families.TryGetValue(family, out var id)) { id = families.Count; families[family] = id; }
                c.FamilyId = id;
            }
        }

        public void Preprocess(float maxStopPathSeconds)
        {
            ComputeDerived();
            var n = Count;
            // Stationary continuation (deterministic): for a sub-clip segment the natural next segment of the same clip
            // (wrapping to the first), so a full idle cycle returns to its start and drift does not accumulate; else the
            // self edge; else the stationary successor with the least root drift.
            for (var i = 0; i < n; i++)
            {
                var c = Clips[i];
                c.StationaryContinuation = -1;
                if (!c.IsStationary) continue;
                if (c.NodeId.Contains("@"))
                {
                    var natural = -1;
                    var wrap = -1;
                    foreach (var to in c.Outgoing)
                    {
                        var s = Clips[to];
                        if (s.ClipId != c.ClipId || s.YawRateDegps != c.YawRateDegps || !s.IsStationary) continue;
                        if (Math.Abs(s.RangeFrom - (c.RangeFrom + c.Duration)) < 2e-3f) natural = to;
                        if (s.RangeFrom <= 1e-6f) wrap = to;
                    }

                    if (natural >= 0) { c.StationaryContinuation = natural; continue; }
                    if (wrap >= 0 && c.HasSelfEdge == false) { c.StationaryContinuation = wrap; continue; }
                    if (wrap >= 0) { c.StationaryContinuation = wrap; continue; }
                }

                if (c.HasSelfEdge) { c.StationaryContinuation = i; continue; }
                var best = -1;
                var bestDrift = float.MaxValue;
                foreach (var to in c.Outgoing)
                {
                    var s = Clips[to];
                    if (!s.IsStationary) continue;
                    var drift = s.TotalDisplacement + Math.Abs(s.TotalYaw) * 0.01f;
                    if (drift < bestDrift) { bestDrift = drift; best = to; }
                }

                c.StationaryContinuation = best;
            }

            // A wait-capable node must be able to idle forever: its continuation chain has to enter a cycle (Section 11.4).
            var continuable = new bool[n];
            for (var i = 0; i < n; i++)
            {
                var cursor = i;
                var seen = new HashSet<int>();
                while (cursor >= 0 && seen.Add(cursor)) cursor = Clips[cursor].StationaryContinuation;
                continuable[i] = cursor >= 0;   // stopped on a repeat => cycle reached
            }

            for (var i = 0; i < n; i++)
            {
                if (Clips[i].IsWaitCapable && !continuable[i]) { Clips[i].IsWaitCapable = false; Clips[i].DisplayName += " (no idle cycle)"; }
            }

            // Reverse adjacency.
            var inLists = new List<int>[n];
            for (var i = 0; i < n; i++) inLists[i] = new List<int>();
            for (var i = 0; i < n; i++) foreach (var to in Clips[i].Outgoing) inLists[to].Add(i);

            // Multi-source Dijkstra over reversed edges: dist[v] = shortest total duration of successors after v until a wait-capable node is reached.
            var dist = new float[n];
            var next = new int[n];
            for (var i = 0; i < n; i++) { dist[i] = float.PositiveInfinity; next[i] = -1; }
            var heap = new SortedSet<(float d, int node)>();
            for (var i = 0; i < n; i++)
            {
                if (Clips[i].IsWaitCapable) { dist[i] = 0f; heap.Add((0f, i)); }
            }

            while (heap.Count > 0)
            {
                var top = heap.Min;
                heap.Remove(top);
                var v = top.node;
                if (top.d > dist[v]) continue;
                foreach (var u in inLists[v])
                {
                    // u -> v: after u ends, play v (duration of v) then v's remaining path.
                    var candidate = dist[v] + Clips[v].Duration;
                    if (candidate < dist[u] - 1e-6f || (Math.Abs(candidate - dist[u]) <= 1e-6f && next[u] >= 0 && v < next[u]))
                    {
                        if (!float.IsPositiveInfinity(dist[u])) heap.Remove((dist[u], u));
                        dist[u] = candidate;
                        next[u] = v;
                        heap.Add((candidate, u));
                    }
                }
            }

            WaitCapableCount = 0;
            CanReachWaitCount = 0;
            for (var i = 0; i < n; i++)
            {
                var c = Clips[i];
                c.StopPathSeconds = dist[i];
                c.CanReachWait = dist[i] <= maxStopPathSeconds;
                var path = new List<int>();
                if (c.CanReachWait && !c.IsWaitCapable)
                {
                    var cursor = next[i];
                    while (cursor >= 0 && path.Count < 64)
                    {
                        path.Add(cursor);
                        if (Clips[cursor].IsWaitCapable) break;
                        cursor = next[cursor];
                    }
                }

                c.StopPath = path.ToArray();
                if (c.IsWaitCapable) WaitCapableCount++;
                if (c.CanReachWait) CanReachWaitCount++;
            }
        }
    }
}
