using System;
using System.Collections.Generic;
using System.IO;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>
    /// PROP-OBSTACLE-1: the saved numeric inputs of the prop fragments' map and of the planner, from ordinary play -- the
    /// records' encoding (the capture writes them on its own thread) and their reading (the replay and the viewer).
    /// <para>
    /// A session folder holds four streams of length-prefixed records and session.json:
    /// shapes.bin (a shape's numbers, once), builds.bin (every build's window, timings and items: a transform is written when
    /// it changed since the item's last written one, else a mark), cycles.bin (every planning cycle's inputs with the
    /// simulation's state to take the play up from there, its collection, retirements and starts) and player.bin (the player
    /// each frame, fixed records). Times are the planner's clock (s).
    /// </para>
    /// </summary>
    public static class PropCapture
    {
        public const int Format = 1;
        public const string ShapesFile = "shapes.bin", BuildsFile = "builds.bin", CyclesFile = "cycles.bin", PlayerFile = "player.bin", SessionFile = "session.json", SummaryFile = "summary.json";
        public const byte BuildRecord = 1, PublishedRecord = 2;
        public const byte CycleStartRecord = 1, CycleCollectedRecord = 2, RetiredRecord = 3, StartAttemptRecord = 4;
        public const byte StartStarted = 0, StartUnplaced = 1, StartInView = 2, StartInProps = 3, StartNoSlot = 4;

        /// <summary>
        /// A long line of words in parts of at most <paramref name="most"/> characters, cut after a "; " where one is in reach
        /// (at the limit where none is): the Quest's log keeps about 1,000 characters of a line. Joined, the parts are the words.
        /// </summary>
        public static List<string> LogParts(string words, int most)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(words) || most < 1) return parts;
            int from = 0;
            while (words.Length - from > most)
            {
                int end = from + most;
                for (int i = from + most - 2; i > from; i--)
                    if (words[i] == ';' && words[i + 1] == ' ') { end = i + 2; break; }
                parts.Add(words.Substring(from, end - from));
                from = end;
            }
            parts.Add(words.Substring(from));
            return parts;
        }
        public const int PlayerRecordBytes = 36;

        public static byte[] EncodeShape(PropShape shape)
        {
            using (var stream = new MemoryStream(shape.Bytes + 32))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(shape.Id); w.Write(shape.ConvexCount);
                foreach (var v in shape.VertexStart) w.Write(v);
                foreach (var v in shape.EdgeStart) w.Write(v);
                foreach (var v in shape.Vertices) w.Write(v);
                foreach (var v in shape.Edges) w.Write(v);
                foreach (var v in shape.Bounds) w.Write(v);
                return stream.ToArray();
            }
        }

        public static PropShape DecodeShape(BinaryReader r)
        {
            var id = r.ReadInt32(); var count = r.ReadInt32();
            if (count < 0 || count > 1 << 20) throw new InvalidDataException("a shape's convex count " + count);
            var vertexStart = new int[count + 1]; var edgeStart = new int[count + 1];
            for (var i = 0; i <= count; i++) vertexStart[i] = r.ReadInt32();
            for (var i = 0; i <= count; i++) edgeStart[i] = r.ReadInt32();
            var vertices = new float[vertexStart[count] * 3]; var edges = new int[edgeStart[count] * 2]; var bounds = new float[count * 6];
            for (var i = 0; i < vertices.Length; i++) vertices[i] = r.ReadSingle();
            for (var i = 0; i < edges.Length; i++) edges[i] = r.ReadInt32();
            for (var i = 0; i < bounds.Length; i++) bounds[i] = r.ReadSingle();
            return new PropShape(id, vertices, vertexStart, edges, edgeStart, bounds);
        }

        /// <summary>What the gathering on Main and the work measured for one build, beside the input itself.</summary>
        public struct BuildTimings
        {
            public int Serial, GatherFrames, NotProp, ShapesBelow, ShapesAbove, ShapesOutside;
            public double GatherMainMilliseconds, WorkerMilliseconds;
        }

        /// <summary>
        /// One build's record. `last` holds, per item key (fragment and side), the transform written last; an item whose transform
        /// equals it is written as a mark. `forceAll` writes every transform (after a record was not written).
        /// </summary>
        public static byte[] EncodeBuild(PropObstacleInput input, in PropObstacleStats stats, in BuildTimings t, Dictionary<long, float[]> last, bool forceAll)
        {
            using (var stream = new MemoryStream(256 + input.Count * 64))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(BuildRecord);
                w.Write(input.Time);
                w.Write(t.Serial);
                w.Write(input.PlayerX); w.Write(input.PlayerZ); w.Write(input.OriginX); w.Write(input.OriginZ); w.Write(input.Cell); w.Write(input.Cells); w.Write(input.PlaneY); w.Write(input.MaxDistance);
                w.Write(t.GatherFrames); w.Write(t.GatherMainMilliseconds); w.Write(t.WorkerMilliseconds); w.Write(t.NotProp); w.Write(t.ShapesBelow); w.Write(t.ShapesAbove); w.Write(t.ShapesOutside);
                w.Write(stats.Items); w.Write(stats.Convexes); w.Write(stats.OutOfWindow); w.Write(stats.BelowPlane); w.Write(stats.AbovePlane); w.Write(stats.ItemsBelowPlane);
                w.Write(stats.Sections); w.Write(stats.ThinSections); w.Write(stats.OccupiedCells); w.Write(stats.StaticSkippedCells);
                w.Write(stats.SectionMilliseconds); w.Write(stats.RasterMilliseconds); w.Write(stats.DistanceMilliseconds);
                w.Write(input.Count);
                for (var i = 0; i < input.Count; i++)
                {
                    w.Write(input.Fragments[i]); w.Write(input.Roots[i]); w.Write((sbyte)input.Sides[i]); w.Write(input.Shapes[i].Id);
                    var key = Key(input.Fragments[i], input.Sides[i]);
                    var same = !forceAll && last.TryGetValue(key, out var before);
                    if (same)
                    {
                        before = last[key];
                        for (var k = 0; k < 12; k++) if (before[k] != input.Matrices[i * 12 + k]) { same = false; break; }
                    }

                    w.Write(!same);
                    if (same) continue;
                    if (!last.TryGetValue(key, out var kept)) { kept = new float[12]; last[key] = kept; }
                    for (var k = 0; k < 12; k++) { kept[k] = input.Matrices[i * 12 + k]; w.Write(kept[k]); }
                }

                // after the items (a record made before 2026-11-16 ends here): the player the window was decided with
                w.Write(input.WindowPlayerX); w.Write(input.WindowPlayerZ);
                return stream.ToArray();
            }
        }

        public static long Key(int fragment, int side) => ((long)fragment << 2) | (uint)(side + 1);

        public static byte[] EncodePublished(int serial, int version, double inputTime, double publishTime, double collectMilliseconds)
        {
            using (var stream = new MemoryStream(40))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(PublishedRecord); w.Write(publishTime); w.Write(serial); w.Write(version); w.Write(inputTime); w.Write(collectMilliseconds);
                return stream.ToArray();
            }
        }

        /// <summary>One build as read back: its input (ready for PropObstacleBuilder), what was measured, and when it was published (NaN: never).</summary>
        public sealed class Build
        {
            public PropObstacleInput Input;
            public BuildTimings Timings;
            public PropObstacleStats Stats;
            public int Version;
            public double PublishTime = double.NaN, CollectMilliseconds;
            public int MissingShapes;
        }

        public sealed class CycleStart
        {
            public double Time;
            public int Frame, Generation, PropVersion;
            public float PlayerX, PlayerZ, VelocityX, VelocityZ, Yaw;
            public double PropInputTime;
            public byte[] State;
            public List<AgentPlan> Baseline = new List<AgentPlan>();
            public List<int> BaselineGeneration = new List<int>();
            public List<int> Waiting = new List<int>();
        }

        public sealed class CycleEnd
        {
            public double Time, QueuedTime, QueueMilliseconds, ComputeMilliseconds, WallMilliseconds, CandidateMilliseconds;
            public string Outcome;
            public int Replacements, PropHits, DynamicHits;
            public float PropSeconds;
        }

        public struct Event
        {
            public byte Kind, Result;
            public double Time;
            public int Id;
            public float X, Z;
        }

        public struct PlayerSample
        {
            public double Time;
            public float X, Z, Yaw, VelocityX, VelocityZ, EyeX, EyeZ;
        }

        /// <summary>A session read back: everything of builds and shapes (they are small), and the cycles, events and player of a time range.</summary>
        public sealed class Session
        {
            public string Folder;
            public readonly Dictionary<int, PropShape> Shapes = new Dictionary<int, PropShape>();
            public readonly List<Build> Builds = new List<Build>();
            public readonly List<CycleStart> Starts = new List<CycleStart>();
            public readonly List<CycleEnd> Ends = new List<CycleEnd>();
            public readonly List<Event> Events = new List<Event>();
            public readonly List<PlayerSample> Player = new List<PlayerSample>();
            public int TruncatedRecords;

            public static Session Load(string folder, double from = double.NegativeInfinity, double to = double.PositiveInfinity, bool withStates = true)
            {
                var session = new Session { Folder = folder };
                Records(Path.Combine(folder, ShapesFile), session, r =>
                {
                    var shape = DecodeShape(r);
                    session.Shapes[shape.Id] = shape;
                });
                var last = new Dictionary<long, float[]>();
                var bySerial = new Dictionary<int, Build>();
                Records(Path.Combine(folder, BuildsFile), session, r =>
                {
                    var kind = r.ReadByte();
                    if (kind == PublishedRecord)
                    {
                        var publish = r.ReadDouble(); var serial = r.ReadInt32(); var version = r.ReadInt32(); r.ReadDouble(); var collect = r.ReadDouble();
                        if (bySerial.TryGetValue(serial, out var built)) { built.PublishTime = publish; built.Version = version; built.CollectMilliseconds = collect; }
                        return;
                    }

                    if (kind != BuildRecord) throw new InvalidDataException("a build record's kind " + kind);
                    var b = new Build { Input = new PropObstacleInput() };
                    var input = b.Input;
                    input.Time = r.ReadDouble();
                    b.Timings.Serial = r.ReadInt32();
                    input.PlayerX = r.ReadSingle(); input.PlayerZ = r.ReadSingle(); input.OriginX = r.ReadSingle(); input.OriginZ = r.ReadSingle(); input.Cell = r.ReadSingle(); input.Cells = r.ReadInt32(); input.PlaneY = r.ReadSingle(); input.MaxDistance = r.ReadSingle();
                    b.Timings.GatherFrames = r.ReadInt32(); b.Timings.GatherMainMilliseconds = r.ReadDouble(); b.Timings.WorkerMilliseconds = r.ReadDouble();
                    b.Timings.NotProp = r.ReadInt32(); b.Timings.ShapesBelow = r.ReadInt32(); b.Timings.ShapesAbove = r.ReadInt32(); b.Timings.ShapesOutside = r.ReadInt32();
                    b.Stats.Items = r.ReadInt32(); b.Stats.Convexes = r.ReadInt32(); b.Stats.OutOfWindow = r.ReadInt32(); b.Stats.BelowPlane = r.ReadInt32(); b.Stats.AbovePlane = r.ReadInt32(); b.Stats.ItemsBelowPlane = r.ReadInt32();
                    b.Stats.Sections = r.ReadInt32(); b.Stats.ThinSections = r.ReadInt32(); b.Stats.OccupiedCells = r.ReadInt32(); b.Stats.StaticSkippedCells = r.ReadInt32();
                    b.Stats.SectionMilliseconds = r.ReadDouble(); b.Stats.RasterMilliseconds = r.ReadDouble(); b.Stats.DistanceMilliseconds = r.ReadDouble();
                    var count = r.ReadInt32();
                    var matrix = new float[12];
                    for (var i = 0; i < count; i++)
                    {
                        var fragment = r.ReadInt32(); var root = r.ReadInt32(); int side = r.ReadSByte(); var shapeId = r.ReadInt32();
                        var key = Key(fragment, side);
                        if (r.ReadBoolean())
                        {
                            if (!last.TryGetValue(key, out var kept)) { kept = new float[12]; last[key] = kept; }
                            for (var k = 0; k < 12; k++) kept[k] = r.ReadSingle();
                        }

                        if (!session.Shapes.TryGetValue(shapeId, out var shape) || !last.TryGetValue(key, out var m)) { b.MissingShapes++; continue; }
                        var index = input.Add(shape, fragment, root, side);
                        Array.Copy(m, 0, input.Matrices, index * 12, 12);
                    }

                    if (r.BaseStream.Length - r.BaseStream.Position >= 8) { input.WindowPlayerX = r.ReadSingle(); input.WindowPlayerZ = r.ReadSingle(); }
                    bySerial[b.Timings.Serial] = b;
                    session.Builds.Add(b);
                });
                Records(Path.Combine(folder, CyclesFile), session, r =>
                {
                    var kind = r.ReadByte(); var time = r.ReadDouble();
                    if (time < from || time > to) return;
                    switch (kind)
                    {
                        case CycleStartRecord:
                        {
                            var c = new CycleStart { Time = time, Frame = r.ReadInt32(), Generation = r.ReadInt32(), PlayerX = r.ReadSingle(), PlayerZ = r.ReadSingle(), VelocityX = r.ReadSingle(), VelocityZ = r.ReadSingle(), Yaw = r.ReadSingle(), PropVersion = r.ReadInt32(), PropInputTime = r.ReadDouble() };
                            var bytes = r.ReadInt32();
                            if (withStates) c.State = r.ReadBytes(bytes); else r.BaseStream.Seek(bytes, SeekOrigin.Current);
                            var plans = r.ReadInt32();
                            for (var i = 0; i < plans; i++)
                            {
                                var id = r.ReadInt32(); var generation = r.ReadInt32();
                                c.Baseline.Add(new AgentPlan { AgentId = id, Revision = generation, Segments = LocomotionSimulation.ReadSegments(r) });
                                c.BaselineGeneration.Add(generation);
                            }

                            var waiting = r.ReadInt32();
                            for (var i = 0; i < waiting; i++) c.Waiting.Add(r.ReadInt32());
                            session.Starts.Add(c);
                            break;
                        }
                        case CycleCollectedRecord:
                            session.Ends.Add(new CycleEnd { Time = time, QueuedTime = r.ReadDouble(), Outcome = r.ReadString(), QueueMilliseconds = r.ReadDouble(), ComputeMilliseconds = r.ReadDouble(), Replacements = r.ReadInt32(), WallMilliseconds = r.ReadDouble(), CandidateMilliseconds = r.ReadDouble(), PropHits = r.ReadInt32(), PropSeconds = r.ReadSingle(), DynamicHits = r.ReadInt32() });
                            break;
                        case RetiredRecord:
                        case StartAttemptRecord:
                            session.Events.Add(new Event { Kind = kind, Time = time, Id = r.ReadInt32(), X = r.ReadSingle(), Z = r.ReadSingle(), Result = r.ReadByte() });
                            break;
                        default: throw new InvalidDataException("a cycle record's kind " + kind);
                    }
                });
                var path = Path.Combine(folder, PlayerFile);
                if (File.Exists(path))
                {
                    using (var r = new BinaryReader(File.OpenRead(path)))
                    {
                        var n = r.BaseStream.Length / PlayerRecordBytes;
                        for (long i = 0; i < n; i++)
                        {
                            var s = new PlayerSample { Time = r.ReadDouble(), X = r.ReadSingle(), Z = r.ReadSingle(), Yaw = r.ReadSingle(), VelocityX = r.ReadSingle(), VelocityZ = r.ReadSingle(), EyeX = r.ReadSingle(), EyeZ = r.ReadSingle() };
                            if (s.Time >= from - 1.0 && s.Time <= to + 1.0) session.Player.Add(s);
                        }
                    }
                }

                return session;
            }

            /// <summary>Reads a stream of [int length][payload] records; a record cut short at the file's end (the play was killed) is counted, not read.</summary>
            private static void Records(string path, Session session, Action<BinaryReader> read)
            {
                if (!File.Exists(path)) return;
                var all = File.ReadAllBytes(path);
                var at = 0;
                while (at + 4 <= all.Length)
                {
                    var length = BitConverter.ToInt32(all, at);
                    if (length < 0 || at + 4 + length > all.Length) { session.TruncatedRecords++; break; }
                    using (var r = new BinaryReader(new MemoryStream(all, at + 4, length, false))) read(r);
                    at += 4 + length;
                }
            }

            /// <summary>The player at a time, between the two samples about it (the nearest end outside the record).</summary>
            public PlayerSample PlayerAt(double time)
            {
                if (Player.Count == 0) return default;
                int lo = 0, hi = Player.Count - 1;
                if (time <= Player[0].Time) return Player[0];
                if (time >= Player[hi].Time) return Player[hi];
                while (hi - lo > 1) { var mid = (lo + hi) / 2; if (Player[mid].Time <= time) lo = mid; else hi = mid; }
                var a = Player[lo]; var b = Player[hi];
                var t = b.Time > a.Time ? (float)((time - a.Time) / (b.Time - a.Time)) : 0f;
                return new PlayerSample
                {
                    Time = time, X = a.X + (b.X - a.X) * t, Z = a.Z + (b.Z - a.Z) * t, Yaw = a.Yaw + Pose2.WrapDeg(b.Yaw - a.Yaw) * t,
                    VelocityX = a.VelocityX + (b.VelocityX - a.VelocityX) * t, VelocityZ = a.VelocityZ + (b.VelocityZ - a.VelocityZ) * t,
                    EyeX = a.EyeX + (b.EyeX - a.EyeX) * t, EyeZ = a.EyeZ + (b.EyeZ - a.EyeZ) * t,
                };
            }

            /// <summary>The newest build whose input is not later than a time (-1: none).</summary>
            public int BuildIndexAt(double time)
            {
                int lo = 0, hi = Builds.Count - 1, found = -1;
                while (lo <= hi) { var mid = (lo + hi) / 2; if (Builds[mid].Input.Time <= time) { found = mid; lo = mid + 1; } else hi = mid - 1; }
                return found;
            }
        }

        // ---- the cycle stream's records (the crowd writes them) ----

        public static byte[] EncodeCycleStart(double time, int frame, int generation, float px, float pz, float vx, float vz, float yaw, int propVersion, double propInputTime,
            byte[] state, int stateLength, IReadOnlyDictionary<int, PublishedMobPlan> baseline, IEnumerable<int> waiting)
        {
            using (var stream = new MemoryStream(stateLength + 4096))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(CycleStartRecord); w.Write(time); w.Write(frame); w.Write(generation); w.Write(px); w.Write(pz); w.Write(vx); w.Write(vz); w.Write(yaw); w.Write(propVersion); w.Write(propInputTime);
                w.Write(stateLength); w.Write(state, 0, stateLength);
                w.Write(baseline.Count);
                foreach (var pair in baseline)
                {
                    var plan = pair.Value;
                    w.Write(pair.Key); w.Write(plan.Generation); w.Write(plan.SegmentCount);
                    for (var i = 0; i < plan.SegmentCount; i++)
                    {
                        var s = plan.Segment(i);
                        w.Write(s.Clip); w.Write(s.StartTime); w.Write(s.EndTime); LocomotionSimulation.Write(w, s.WorldStart);
                    }
                }

                var ids = new List<int>(waiting);
                w.Write(ids.Count);
                foreach (var id in ids) w.Write(id);
                return stream.ToArray();
            }
        }

        public static byte[] EncodeCycleCollected(double time, double queuedTime, string outcome, double queueMs, double computeMs, int replacements, CycleMetrics metrics)
        {
            using (var stream = new MemoryStream(128))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(CycleCollectedRecord); w.Write(time); w.Write(queuedTime); w.Write(outcome ?? string.Empty); w.Write(queueMs); w.Write(computeMs); w.Write(replacements);
                w.Write(metrics?.wallMilliseconds ?? 0.0); w.Write(metrics?.candidateMilliseconds ?? 0.0); w.Write(metrics?.propPredictedHits ?? 0); w.Write(metrics?.propPredictedSeconds ?? 0f); w.Write(metrics?.dynamicPredictedHits ?? 0);
                return stream.ToArray();
            }
        }

        public static byte[] EncodeEvent(byte kind, double time, int id, float x, float z, byte result)
        {
            using (var stream = new MemoryStream(24))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(kind); w.Write(time); w.Write(id); w.Write(x); w.Write(z); w.Write(result);
                return stream.ToArray();
            }
        }
    }
}
