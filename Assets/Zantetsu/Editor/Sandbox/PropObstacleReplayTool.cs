using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using Zantetsu.Core.MobPlan;

namespace Zantetsu.EditorTools.Sandbox
{
    // PROP-OBSTACLE-1 (evaluation only; not part of any build): the replay of a capture of ordinary play
    // (Zantetsu.Core.MobPlan.PropCapture) with the product's Core, on virtual time -- no scene, no Player.
    //   Unity.exe -batchmode -projectPath . -executeMethod Zantetsu.EditorTools.Sandbox.PropObstacleReplayTool.Run -quit
    //     -zantetsuPropReplaySession <capture folder> -zantetsuPropReplayOut <new folder> -zantetsuPropReplayMode index|run|eye
    //     [-zantetsuPropReplaySections "t0-t1,t0-t1"] [-zantetsuPropReplayVariants "off,on,cell0.1,every5,plane1.2,delay0.5"] [-zantetsuPropReplayPng]
    // index: the record's completeness and a table of 30 s sections with what each holds (sections.csv).
    // run:   every section x variant from the same saved start (results.csv), and pictures of each section (ON and OFF together).
    // eye:   the head heights of the crowd's character models (the plane's first value).
    public static class PropObstacleReplayTool
    {
        public const string AssetsPath = "Assets/Licensed/WalkCity/MobPlanAssetsWalkCity.asset";

        [Serializable]
        private sealed class Identified { public string asset; public int bytes; public string sha256; public string description; public int clips; }

        [Serializable]
        private sealed class Header
        {
            public int formatVersion;
            public string startedUtc, codeVersion, build;
            public PropObstacleSettings settings;
            public float[] mapOffset, mapCentre;
            public float mapWindowMeters;
            public Identified staticMap, rootMotion;
            public PlannerConfig plannerConfig;
            public ScenarioSettings scenario;
            public int simulationStateFormat;
        }

        /// <summary>A capture with the product's data it was made with: checked against the session's hashes.</summary>
        public sealed class Loaded
        {
            public PropCapture.Session Session;
            public ClipDataset Dataset;
            public PolygonWalkable Map;
            public PlannerConfig Config;
            public ScenarioSettings Scenario;
            public PropObstacleSettings Settings;
            public string Identity;
            public PropReplay Replay;
        }

        private static string Sha(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        public static Loaded Load(string folder, double from = double.NegativeInfinity, double to = double.PositiveInfinity)
        {
            var header = JsonUtility.FromJson<Header>(File.ReadAllText(Path.Combine(folder, PropCapture.SessionFile)));
            if (header.formatVersion != PropCapture.Format) throw new InvalidDataException("the capture's format " + header.formatVersion);
            if (header.simulationStateFormat < LocomotionSimulation.OldestStateFormat || header.simulationStateFormat > LocomotionSimulation.StateFormat) throw new InvalidDataException("the simulation state's format " + header.simulationStateFormat);
            var assets = AssetDatabase.LoadAssetAtPath<MobPlanAssets>(AssetsPath);
            if (assets == null) throw new InvalidOperationException("no " + AssetsPath);
            byte[] data = assets.dataset.bytes, polygons = assets.polygons.bytes;
            string dataSha = Sha(data), mapSha = Sha(polygons);
            if (dataSha != header.rootMotion.sha256) throw new InvalidDataException("the root-motion data is not the capture's: " + dataSha + " against " + header.rootMotion.sha256);
            if (mapSha != header.staticMap.sha256) throw new InvalidDataException("the static map is not the capture's: " + mapSha + " against " + header.staticMap.sha256);
            var loaded = new Loaded
            {
                Session = PropCapture.Session.Load(folder, from, to),
                Dataset = ClipDataset.Read(data),
                Map = PolygonWalkable.Read(polygons, header.mapCentre[0], header.mapCentre[1], header.mapWindowMeters, header.mapWindowMeters),
                Config = header.plannerConfig, Scenario = header.scenario, Settings = header.settings,
                Identity = "root motion sha256 " + dataSha + ", static map sha256 " + mapSha + ", code " + header.codeVersion + ", " + header.build + ", started " + header.startedUtc + ", simulation state format " + header.simulationStateFormat,
            };
            loaded.Map.BuildAllTiles();
            loaded.Replay = new PropReplay(loaded.Session, loaded.Dataset, loaded.Map, loaded.Config, loaded.Scenario) { CaptureSquareMeters = header.settings != null ? header.settings.captureSquareMeters : 0f };
            return loaded;
        }

        private static string Argument(string[] args, string name, string otherwise = null)
        {
            int at = Array.IndexOf(args, name);
            return at >= 0 && at + 1 < args.Length ? args[at + 1] : otherwise;
        }

        public static void Run()
        {
            string[] args = Environment.GetCommandLineArgs();
            string outDir = Argument(args, "-zantetsuPropReplayOut") ?? throw new ArgumentException("-zantetsuPropReplayOut");
            if (Directory.Exists(outDir)) throw new IOException("exists: " + outDir);
            Directory.CreateDirectory(outDir);
            string mode = Argument(args, "-zantetsuPropReplayMode", "index");
            try
            {
                if (mode == "eye") { Eye(outDir); return; }
                string folder = Argument(args, "-zantetsuPropReplaySession") ?? throw new ArgumentException("-zantetsuPropReplaySession");
                Loaded loaded = Load(folder);
                File.WriteAllText(Path.Combine(outDir, "identity.txt"), "session " + folder + "\n" + loaded.Identity + "\nsettings: " + loaded.Settings.Describe() + "\n");
                var clipLines = new List<string> { "id,category,name,duration,average_speed,total_displacement,total_yaw,yaw_travel,stationary,wait_capable,low_translation_at_0.15" };
                foreach (ClipDescriptor c in loaded.Dataset.Clips) clipLines.Add(FormattableString.Invariant($"{c.Id},{c.Category},{c.DisplayName.Replace(',', ';')},{c.Duration:F2},{c.AverageSpeed:F3},{c.TotalDisplacement:F3},{c.TotalYaw:F1},{c.YawTravelDeg:F1},{(c.IsStationary ? 1 : 0)},{(c.IsWaitCapable ? 1 : 0)},{(!c.IsStationary && c.AverageSpeed < 0.15f ? 1 : 0)}"));
                File.WriteAllLines(Path.Combine(outDir, "clips.csv"), clipLines);
                if (mode == "index") Index(loaded, outDir);
                else if (mode == "recorded") Recorded(loaded, outDir, Argument(args, "-zantetsuPropReplaySections"), Argument(args, "-zantetsuPropReplayYard"));
                else if (mode == "run") Replay(loaded, outDir, Argument(args, "-zantetsuPropReplaySections"), Argument(args, "-zantetsuPropReplayVariants", "off,on"), Array.IndexOf(args, "-zantetsuPropReplayPng") >= 0, Argument(args, "-zantetsuPropReplayYard"));
                else throw new ArgumentException("the mode: " + mode);
                Debug.Log("PROP REPLAY: " + mode + " done -> " + outDir);
            }
            catch (Exception e)
            {
                File.WriteAllText(Path.Combine(outDir, "FAILED.txt"), e.ToString());
                Debug.LogException(e);
                EditorApplication.Exit(3);
            }
        }

        // ---- index: completeness, and what each 30 s section holds ----

        public sealed class Section
        {
            public double From, To;
            public int Builds, MostCells, MostItems, MovingBuilds, KeysAdded, KeysRemoved, ShapeChanges, WindowMoves, MostLow, MostStaticSkipped, MostSections;
            public int Retirements, Starts, StartsInProps, StartsInView;
            public double RecordedCrossingSeconds;
            public string Tags;

            public static string CsvHeader => "from,to,builds,most_cells,most_items,most_sections,moving_builds,keys_added,keys_removed,shape_changes,window_moves,most_low_fragments,most_static_skipped,retirements,starts,starts_in_props,starts_in_view,recorded_crossing_s,tags";
            public string ToCsv() => FormattableString.Invariant($"{From:F2},{To:F2},{Builds},{MostCells},{MostItems},{MostSections},{MovingBuilds},{KeysAdded},{KeysRemoved},{ShapeChanges},{WindowMoves},{MostLow},{MostStaticSkipped},{Retirements},{Starts},{StartsInProps},{StartsInView},{RecordedCrossingSeconds:F2},{Tags}");
        }

        public static List<Section> Sections(Loaded loaded, double length = 30.0, double stride = 10.0)
        {
            PropCapture.Session s = loaded.Session;
            var sections = new List<Section>();
            if (s.Starts.Count == 0) return sections;
            // per build: what changed against the build before
            int n = s.Builds.Count;
            var moving = new bool[n]; var added = new int[n]; var removed = new int[n]; var shapeChanged = new int[n]; var windowMoved = new bool[n];
            var before = new Dictionary<long, (int shape, int at)>();
            for (int b = 0; b < n; b++)
            {
                PropObstacleInput input = s.Builds[b].Input;
                var now = new Dictionary<long, (int shape, int at)>();
                for (int i = 0; i < input.Count; i++)
                {
                    long key = PropCapture.Key(input.Fragments[i], input.Sides[i]);
                    now[key] = (input.Shapes[i].Id, i);
                    if (!before.TryGetValue(key, out var old)) { if (b > 0) added[b]++; continue; }
                    if (old.shape != input.Shapes[i].Id) shapeChanged[b]++;
                    PropObstacleInput last = s.Builds[b - 1].Input;
                    for (int k = 0; k < 12; k++)
                    {
                        if (Math.Abs(last.Matrices[old.at * 12 + k] - input.Matrices[i * 12 + k]) > 1e-3f) { moving[b] = true; break; }
                    }
                }

                foreach (long key in before.Keys) if (!now.ContainsKey(key)) removed[b]++;
                if (b > 0 && (s.Builds[b - 1].Input.OriginX != input.OriginX || s.Builds[b - 1].Input.OriginZ != input.OriginZ)) windowMoved[b] = true;
                before = now;
            }

            // the recorded play's own crossings: the published plans of each saved cycle, followed for the second to the next one, against the record's map
            var crossing = new List<(double time, double seconds)>();
            var builder = new PropObstacleBuilder(); var map = new PropObstacleMap(); int built = -1;
            for (int c = 0; c < s.Starts.Count; c++)
            {
                PropCapture.CycleStart cycle = s.Starts[c];
                double until = c + 1 < s.Starts.Count ? Math.Min(s.Starts[c + 1].Time, cycle.Time + 2.0) : cycle.Time + 1.0;
                double seconds = 0;
                for (double t = cycle.Time; t < until; t += 0.1)
                {
                    int index = s.BuildIndexAt(t);
                    if (index < 0) continue;
                    if (index != built) { builder.Build(s.Builds[index].Input, null, map); built = index; }
                    if (map.OccupiedCells == 0) continue;
                    foreach (AgentPlan plan in cycle.Baseline)
                    {
                        if (cycle.Waiting.Contains(plan.AgentId) || plan.Segments.Count == 0 || t < plan.StartTime || t > plan.EndTime) continue;
                        Pose2 pose = AgentPlan.PoseAt(loaded.Dataset, plan.Segments, (float)t);
                        if (map.OccupiedAt(pose.X, pose.Z)) seconds += 0.1;
                    }
                }

                crossing.Add((cycle.Time, seconds));
            }

            double first = s.Starts[0].Time, lastTime = s.Starts[s.Starts.Count - 1].Time;
            for (double from = first; from + 5.0 < lastTime; from += stride)
            {
                var section = new Section { From = from, To = Math.Min(from + length, lastTime) };
                for (int b = 0; b < n; b++)
                {
                    double t = s.Builds[b].Input.Time;
                    if (t < section.From || t > section.To) continue;
                    section.Builds++;
                    section.MostCells = Math.Max(section.MostCells, s.Builds[b].Stats.OccupiedCells);
                    section.MostItems = Math.Max(section.MostItems, s.Builds[b].Input.Count);
                    section.MostSections = Math.Max(section.MostSections, s.Builds[b].Stats.Sections);
                    section.MostLow = Math.Max(section.MostLow, s.Builds[b].Stats.ItemsBelowPlane);
                    section.MostStaticSkipped = Math.Max(section.MostStaticSkipped, s.Builds[b].Stats.StaticSkippedCells);
                    if (moving[b]) section.MovingBuilds++;
                    section.KeysAdded += added[b]; section.KeysRemoved += removed[b]; section.ShapeChanges += shapeChanged[b];
                    if (windowMoved[b]) section.WindowMoves++;
                }

                foreach (PropCapture.Event e in s.Events)
                {
                    if (e.Time < section.From || e.Time > section.To) continue;
                    if (e.Kind == PropCapture.RetiredRecord) section.Retirements++;
                    else if (e.Result == PropCapture.StartStarted) section.Starts++;
                    else if (e.Result == PropCapture.StartInProps) section.StartsInProps++;
                    else if (e.Result == PropCapture.StartInView) section.StartsInView++;
                }

                foreach (var c in crossing) if (c.time >= section.From && c.time <= section.To) section.RecordedCrossingSeconds += c.seconds;
                var tags = new List<string>();
                if (section.MostCells == 0) tags.Add("no-section");
                if (section.MovingBuilds > 0 && section.MostCells > 0) tags.Add("moving");
                if (section.MovingBuilds == 0 && section.MostCells > 0) tags.Add("at-rest");
                if (section.KeysAdded > 0) tags.Add("new-pieces");
                if (section.KeysRemoved > 0) tags.Add("pieces-gone");
                if (section.ShapeChanges > 0) tags.Add("shape-replaced");
                if (section.WindowMoves > 0) tags.Add("window-moved");
                if (section.MostLow > 0) tags.Add("low-pieces");
                if (section.MostStaticSkipped > 0) tags.Add("over-static");
                if (section.Starts + section.StartsInProps > 0) tags.Add("starts");
                if (section.StartsInProps > 0) tags.Add("start-refused-in-props");
                if (section.RecordedCrossingSeconds > 0) tags.Add("crossing");
                if (section.Retirements > 0) tags.Add("npc-cut");
                section.Tags = string.Join(" ", tags);
                sections.Add(section);
            }

            return sections;
        }

        private static void Index(Loaded loaded, string outDir)
        {
            PropCapture.Session s = loaded.Session;
            var text = new StringBuilder();
            int published = s.Builds.Count(b => !double.IsNaN(b.PublishTime));
            int missing = s.Builds.Sum(b => b.MissingShapes);
            text.AppendLine(loaded.Identity);
            text.AppendLine("settings: " + loaded.Settings.Describe());
            text.AppendLine(FormattableString.Invariant($"shapes {s.Shapes.Count}; builds {s.Builds.Count} (published {published}; items whose shape or transform is missing {missing}); saved cycles {s.Starts.Count} (with a state {s.Starts.Count(c => c.State != null)}), collections {s.Ends.Count}; events {s.Events.Count}; player samples {s.Player.Count}; records cut short at a file's end {s.TruncatedRecords}"));
            if (s.Builds.Count > 0)
            {
                text.AppendLine(FormattableString.Invariant($"builds from {s.Builds[0].Input.Time:F2} to {s.Builds[s.Builds.Count - 1].Input.Time:F2} s; cycles from {(s.Starts.Count > 0 ? s.Starts[0].Time : 0):F2} to {(s.Starts.Count > 0 ? s.Starts[s.Starts.Count - 1].Time : 0):F2} s"));
                double[] gather = s.Builds.Select(b => b.Timings.GatherMainMilliseconds).OrderBy(v => v).ToArray();
                double[] worker = s.Builds.Select(b => b.Timings.WorkerMilliseconds).OrderBy(v => v).ToArray();
                double[] delay = s.Builds.Where(b => !double.IsNaN(b.PublishTime)).Select(b => (b.PublishTime - b.Input.Time) * 1000.0).OrderBy(v => v).ToArray();
                double[] frames = s.Builds.Select(b => (double)b.Timings.GatherFrames).OrderBy(v => v).ToArray();
                double[] interval = Enumerable.Range(1, s.Builds.Count - 1).Select(i => (s.Builds[i].Input.Time - s.Builds[i - 1].Input.Time) * 1000.0).OrderBy(v => v).ToArray();
                text.AppendLine("recorded in play (Development Player): " + Quantiles("Main gathering ms a build", gather) + "; " + Quantiles("gathering frames", frames) + "; " + Quantiles("worker ms a build", worker) + "; " + Quantiles("input to publication ms", delay) + "; " + Quantiles("interval between inputs ms", interval));
                text.AppendLine(FormattableString.Invariant($"most items {s.Builds.Max(b => b.Input.Count)}, most occupied cells {s.Builds.Max(b => b.Stats.OccupiedCells)}, most sections {s.Builds.Max(b => b.Stats.Sections)}, most fragments wholly below the plane {s.Builds.Max(b => b.Stats.ItemsBelowPlane)}, most shapes below the plane on Main {s.Builds.Max(b => b.Timings.ShapesBelow)}"));
            }

            if (s.Ends.Count > 0)
            {
                text.AppendLine("recorded planning cycles: " + Quantiles("compute ms", s.Ends.Select(e => e.ComputeMilliseconds).OrderBy(v => v).ToArray()) + "; outcomes: "
                    + string.Join(", ", s.Ends.GroupBy(e => e.Outcome.Split(':')[0]).Select(g => g.Key + " " + g.Count())));
                text.AppendLine("recorded planning cycles, the parts: " + Quantiles("queue ms", s.Ends.Select(e => e.QueueMilliseconds).OrderBy(v => v).ToArray()) + "; "
                    + Quantiles("the planner's own wall ms", s.Ends.Select(e => e.WallMilliseconds).OrderBy(v => v).ToArray()) + "; "
                    + Quantiles("candidates ms", s.Ends.Select(e => e.CandidateMilliseconds).OrderBy(v => v).ToArray()));
                var notPublished = s.Ends.Where(e => !e.Outcome.StartsWith("published", StringComparison.Ordinal)).GroupBy(e => e.Outcome).OrderByDescending(g => g.Count()).ToList();
                text.AppendLine("outcomes other than published, in full: " + (notPublished.Count == 0 ? "none" : string.Join(" | ", notPublished.Take(12).Select(g => g.Key + " x " + g.Count())) + (notPublished.Count > 12 ? " | ... " + (notPublished.Count - 12) + " more kinds" : "")));
                var starts = new HashSet<double>(s.Starts.Select(c => c.Time));
                text.AppendLine(FormattableString.Invariant($"collections whose queueing time is a saved cycle's: {s.Ends.Count(e => starts.Contains(e.QueuedTime))} of {s.Ends.Count}"));
            }

            // gaps: cycles are about one a second, player samples every frame
            int cycleGaps = 0;
            for (int i = 1; i < s.Starts.Count; i++) if (s.Starts[i].Time - s.Starts[i - 1].Time > 3.0) cycleGaps++;
            int playerGaps = 0;
            for (int i = 1; i < s.Player.Count; i++) if (s.Player[i].Time - s.Player[i - 1].Time > 0.25) playerGaps++;
            text.AppendLine(FormattableString.Invariant($"gaps: saved cycles more than 3 s apart {cycleGaps}; player samples more than 0.25 s apart {playerGaps}"));
            string summary = Path.Combine(loaded.Session.Folder, PropCapture.SummaryFile);
            text.AppendLine(File.Exists(summary) ? "the writer's summary: " + File.ReadAllText(summary).Replace("\n", " ") : "NO summary.json: the capture did not end by itself (the files may lack their last records)");
            File.WriteAllText(Path.Combine(outDir, "completeness.txt"), text.ToString());
            List<Section> sections = Sections(loaded);
            File.WriteAllLines(Path.Combine(outDir, "sections.csv"), new[] { Section.CsvHeader }.Concat(sections.Select(x => x.ToCsv())));
        }

        // ---- recorded: what the play itself did -- the saved plans followed, counted as a replay counts ----

        private static void Recorded(Loaded loaded, string outDir, string sectionsText, string yardText)
        {
            PropCapture.Session s = loaded.Session;
            if (s.Starts.Count == 0) throw new InvalidDataException("no saved cycle");
            double from = s.Starts[0].Time, to = s.Starts[s.Starts.Count - 1].Time; float length = 60f;
            if (!string.IsNullOrEmpty(sectionsText))
            {
                // "t0-t1": that span; "t0-t1/len": in sections of len seconds
                string[] slash = sectionsText.Split('/');
                string[] ends = slash[0].Split('-');
                from = double.Parse(ends[0], CultureInfo.InvariantCulture); to = double.Parse(ends[1], CultureInfo.InvariantCulture);
                if (slash.Length > 1) length = float.Parse(slash[1], CultureInfo.InvariantCulture);
            }

            float plane = string.IsNullOrEmpty(yardText) ? 0f : float.Parse(yardText, CultureInfo.InvariantCulture);
            PropReplay.RecordedResult result = loaded.Replay.Recorded(from, to, length, plane);
            File.WriteAllLines(Path.Combine(outDir, "recorded.csv"), new[] { PropReplay.RecordedSection.CsvHeader }.Concat(result.Sections.Select(x => x.ToCsv())));
            File.WriteAllLines(Path.Combine(outDir, "recorded-entries.csv"), new[] { PropReplay.RecordedResult.EntryHeader }.Concat(result.EntryRows));
            File.WriteAllLines(Path.Combine(outDir, "recorded-starts-noted.csv"), new[] { PropReplay.RecordedResult.StartHeader }.Concat(result.StartRows));
            File.WriteAllText(Path.Combine(outDir, "recorded.txt"), result.Describe() + "\n");
        }

        private static string Quantiles(string name, double[] sorted)
        {
            if (sorted.Length == 0) return name + ": none";
            double Q(double q) => sorted[Math.Min(sorted.Length - 1, (int)Math.Round(q * (sorted.Length - 1)))];
            return FormattableString.Invariant($"{name}: median {Q(0.5):F3}, p95 {Q(0.95):F3}, most {sorted[sorted.Length - 1]:F3} (n {sorted.Length})");
        }

        // ---- run: sections x variants ----

        public static PropReplayOptions Variant(string name)
        {
            var o = new PropReplayOptions { Name = name };
            int repeat = name.IndexOf('#');   // "on#2": the same variant again (how much two runs of one variant differ)
            foreach (string part in (repeat >= 0 ? name.Substring(0, repeat) : name).Split('+'))
            {
                float Number(string prefix) => float.Parse(part.Substring(prefix.Length), CultureInfo.InvariantCulture);
                if (part == "off") o.Props = false;
                else if (part == "on") o.Props = true;
                else if (part.StartsWith("cell")) o.Cell = Number("cell");
                else if (part.StartsWith("plane")) o.Plane = Number("plane");
                else if (part.StartsWith("every")) o.BuildEvery = (int)Number("every");
                else if (part.StartsWith("delay")) o.PublishDelay = Number("delay");
                else if (part.StartsWith("latency")) o.CycleLatency = Number("latency");
                else if (part == "field") o.GoalField = true;
                else if (part.StartsWith("field")) { o.GoalField = true; o.GoalFieldCell = Number("field"); }   // "field1.0": the goal fields at 1 m cells where the prop map closes cells
                else if (part == "norestore") o.LocalStayNoRestore = true;
                else if (part == "metric") o.MetricReach = true;
                else if (part == "recover") o.Recovery = true;
                else if (part == "extrap") o.Extrapolate = true;
                else if (part == "round") o.ExtraRounding = true;
                else if (part == "work") o.WorkLimits = true;     // no decision reads the clock: the counted limits only
                else if (part == "notrace") o.NoTrace = true;     // this variant without the searches' trace
                else if (part == "norows") o.CycleRows = false;   // and without the per-cycle diagnosis
                else if (part == "reach") o.GoalReach = true;     // PROP-GOAL-REACH
                else if (part == "standoff") { o.GoalReach = true; o.NoReachMode = 1; }   // no point found: a standoff outside the reach
                else if (part == "giveup") { o.GoalReach = true; o.NoReachMode = 2; }     // no point found: the goal is dropped
                else if (part == "repick") { o.GoalReach = true; o.NoReachMode = 3; }     // no point found: another goal of the same kind
                else if (part.StartsWith("followcheck")) { o.FollowMargin = Number("followcheck"); o.FollowUnchecked = true; }   // the placing alone, on a record of windows only
                else if (part.StartsWith("follow")) o.FollowMargin = Number("follow");   // "follow20": the replay places the window, recentre margin 20 m
                else if (part.StartsWith("maskdelay")) o.MaskDelay = Number("maskdelay");   // "maskdelay0.073": a moved window publishes that much later
                else if (part == "recheck") o.PrefixRecheck = 1f;   // PROP-PREFIX-RECHECK at its first value
                else if (part.StartsWith("recheckclear")) { o.PrefixRecheck = Number("recheckclear"); o.PrefixRecheckClearStart = true; }   // and not where the search would start inside the pieces
                else if (part.StartsWith("recheck")) o.PrefixRecheck = Number("recheck");   // "recheck0.5": the earliest end of the freeze, seconds from now
                else if (part == "rescore") o.Rescore = true;     // the traced NPCs' chosen plans costed with and without the estimate
                else if (part.StartsWith("band")) o.PropBand = Number("band");   // "band0.45": the goal fields close 0.45 m about the prop cells
                else if (part.StartsWith("gf")) o.FieldCellAlways = Number("gf");   // "gf1.0": the goal fields at 1 m cells always (static routing too)
                else if (part == "idlefix") o.IdleFix = true;
                else if (part == "stay") o.LocalStay = true;
                else throw new ArgumentException("a variant's part: " + part);
            }

            return o;
        }

        private static void Replay(Loaded loaded, string outDir, string sectionsText, string variantsText, bool png, string yardText)
        {
            // -zantetsuPropReplayYard "0.4,0.8,1.2,1.7": the yardstick is the union of the sections at these heights (the record's plane without it)
            float[] yard = string.IsNullOrEmpty(yardText) ? null : yardText.Split(',').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            File.AppendAllText(Path.Combine(outDir, "identity.txt"), "yardstick planes: " + (yard == null ? "the record's" : yardText) + "; sections " + sectionsText + "; variants " + variantsText + "\n");
            var ranges = new List<(double from, double to)>();
            if (string.IsNullOrEmpty(sectionsText)) throw new ArgumentException("-zantetsuPropReplaySections \"t0-t1,...\"");
            foreach (string part in sectionsText.Split(','))
            {
                string[] ends = part.Split('-');
                ranges.Add((double.Parse(ends[0], CultureInfo.InvariantCulture), double.Parse(ends[1], CultureInfo.InvariantCulture)));
            }

            string[] variants = variantsText.Split(',');
            var settings = new DwellSettings();
            File.WriteAllText(Path.Combine(outDir, "dwell-settings.txt"), settings.Describe() + "\nyaw: the frame-to-frame differences wrapped to +-180 degrees, summed (absolute, and signed: + is clockwise); goal events are seen at the planning cycles (about one a second); progress is in metres nearer the goal by the straight line, summed over the spans of one and the same goal\n");
            var stays = new List<string> { "section,variant," + DwellInterval.CsvHeader };
            var cycleRows = new List<string> { "section,variant," + PropReplayResult.CycleRowHeader };
            var traceRows = new List<string> { "section,variant," + PropReplayResult.TraceRowHeader };
            var guideRows = new List<string> { "section,variant," + PropReplayResult.GuideRowHeader };
            var stateRows = new List<string> { "section,variant,time,id,search_start_time,start_x,start_z,start_yaw," + SingleAgentSearch.StateTraceHeader };
            var goalRows = new List<string> { "section,variant," + PropReplayResult.GoalRowHeader };
            // -zantetsuPropReplayTrace "1152,1162": the NPC ids whose searches keep the beam's record per time bucket
            string traceText = Argument(Environment.GetCommandLineArgs(), "-zantetsuPropReplayTrace");
            HashSet<int> trace = string.IsNullOrEmpty(traceText) ? null : new HashSet<int>(traceText.Split(',').Select(v => int.Parse(v, CultureInfo.InvariantCulture)));
            var hashRows = new List<string> { "section,variant," + PropReplayResult.CycleHashHeader };
            var npcRows = new List<string> { "section,variant," + PropReplayResult.NpcRowHeader };
            var patchRows = new List<string> { "section,variant," + PropReplayResult.PatchRowHeader };
            var rescoreRows = new List<string> { "section,variant," + PropReplayResult.RescoreRowHeader };
            var reachRows = new List<string> { "section,variant," + PropReplayResult.ReachRowHeader };
            var crossingRows = new List<string> { "section,variant," + PropReplayResult.CrossingRowHeader };
            var prefixRows = new List<string> { "section,variant," + PropReplayResult.PrefixRowHeader };
            var selectionRows = new List<string> { "section,variant," + PropReplayResult.SelectionRowHeader };
            var followRows = new List<string> { "section,variant,time,id,searched_by," + SingleAgentSearch.FollowTraceHeader };
            var lines = new List<string> { "section," + PropReplayResult.CsvHeader };
            var totals = new Dictionary<string, PropReplayResult>();
            for (int r = 0; r < ranges.Count; r++)
            {
                PropReplayResult on = null, off = null;
                foreach (string name in variants)
                {
                    PropReplayOptions options = Variant(name);
                    if (options.FollowMargin >= 0f && !options.FollowUnchecked)
                    {
                        float window = loaded.Settings.WindowMeters;
                        float needed = window + 2f * (window * 0.5f - Math.Min(options.FollowMargin, window * 0.4f));
                        if (loaded.Replay.CaptureSquareMeters < needed - 0.01f)
                            throw new InvalidOperationException(FormattableString.Invariant($"{name}: a window following with margin {options.FollowMargin} m needs the shapes of a {needed:F0} m square about the player; this record kept {(loaded.Replay.CaptureSquareMeters > 0f ? loaded.Replay.CaptureSquareMeters.ToString("F0", CultureInfo.InvariantCulture) + " m" : "its windows only")}"));
                    }

                    options.KeepFrames = png && name.IndexOf('#') < 0;
                    options.YardPlanes = yard;
                    options.TraceAgents = options.NoTrace ? null : trace;
                    PropReplayResult result = loaded.Replay.Run(ranges[r].from, ranges[r].to, options);
                    lines.Add(r + "," + result.ToCsv());
                    if (name == "on") on = result; else if (name == "off") off = result;
                    Add(totals, name, result);
                    File.WriteAllLines(Path.Combine(outDir, "results.csv"), lines);   // kept as it goes
                    foreach (string row in result.CycleHashRows) hashRows.Add(r + "," + name + "," + row);
                    File.WriteAllLines(Path.Combine(outDir, "cycle-hash.csv"), hashRows);
                    foreach (string row in result.NpcRows) npcRows.Add(r + "," + name + "," + row);
                    File.WriteAllLines(Path.Combine(outDir, "npc-goals.csv"), npcRows);
                    foreach (string row in result.PatchRows) patchRows.Add(r + "," + name + "," + row);
                    File.WriteAllLines(Path.Combine(outDir, "estimate-patches.csv"), patchRows);
                    foreach (string row in result.RescoreRows) rescoreRows.Add(r + "," + name + "," + row);
                    if (rescoreRows.Count > 1) File.WriteAllLines(Path.Combine(outDir, "rescore.csv"), rescoreRows);
                    foreach (string row in result.ReachRows) reachRows.Add(r + "," + name + "," + row);
                    if (reachRows.Count > 1) File.WriteAllLines(Path.Combine(outDir, "reach.csv"), reachRows);
                    foreach (string row in result.CrossingRows) crossingRows.Add(r + "," + name + "," + row);
                    File.WriteAllLines(Path.Combine(outDir, "crossings.csv"), crossingRows);
                    foreach (string row in result.PrefixRows) prefixRows.Add(r + "," + name + "," + row);
                    File.WriteAllLines(Path.Combine(outDir, "prefix.csv"), prefixRows);
                    foreach (string row in result.SelectionRows) selectionRows.Add(r + "," + name + "," + row);
                    if (selectionRows.Count > 1) File.WriteAllLines(Path.Combine(outDir, "selection.csv"), selectionRows);
                    foreach (string row in result.FollowRows) followRows.Add(r + "," + name + "," + row);
                    if (followRows.Count > 1) File.WriteAllLines(Path.Combine(outDir, "follow.csv"), followRows);
                    foreach (DwellInterval stay in result.Dwells) stays.Add(r + "," + name + "," + stay.ToCsv(result.DwellSettings, loaded.Dataset));
                    foreach (string row in result.CycleRows) cycleRows.Add(r + "," + name + "," + row);
                    File.WriteAllLines(Path.Combine(outDir, "cycles.csv"), cycleRows);
                    foreach (string row in result.TraceRows) traceRows.Add(r + "," + name + "," + row);
                    if (trace != null) File.WriteAllLines(Path.Combine(outDir, "beam-trace.csv"), traceRows);
                    foreach (string row in result.StateRows) stateRows.Add(r + "," + name + "," + row);
                    if (trace != null) File.WriteAllLines(Path.Combine(outDir, "beam-states.csv"), stateRows);
                    foreach (string row in result.GuideRows) guideRows.Add(r + "," + name + "," + row);
                    if (trace != null) File.WriteAllLines(Path.Combine(outDir, "guide.csv"), guideRows);
                    foreach (string row in result.GoalRows) goalRows.Add(r + "," + name + "," + row);
                    File.WriteAllLines(Path.Combine(outDir, "goals.csv"), goalRows);
                    if (options.KeepFrames && trace != null) GuidePictures(loaded, outDir, r, result);
                    File.WriteAllLines(Path.Combine(outDir, "dwells.csv"), stays);
                    if (options.KeepFrames) StayPictures(loaded, outDir, r, result);
                }

                if (png && on != null && off != null) Pictures(loaded, outDir, r, on, off);
            }

            var sum = new List<string> { "section," + PropReplayResult.CsvHeader };
            foreach (var pair in totals)
            {
                pair.Value.DwellAll = DwellTotals.Of(pair.Value.Dwells, settings, false);
                pair.Value.DwellNear = DwellTotals.Of(pair.Value.Dwells, settings, true);
                pair.Value.DwellNearStatic = DwellTotals.Of(pair.Value.Dwells, settings, 2);
                pair.Value.DwellNearAny = DwellTotals.Of(pair.Value.Dwells, settings, 3);
                sum.Add("all," + pair.Value.ToCsv());
            }

            File.WriteAllLines(Path.Combine(outDir, "totals.csv"), sum);
        }

        private static void Add(Dictionary<string, PropReplayResult> totals, string name, PropReplayResult r)
        {
            if (!totals.TryGetValue(name, out PropReplayResult t)) { t = new PropReplayResult { Name = name, From = r.From, To = r.To }; totals[name] = t; }
            t.To = r.To;
            t.AgentSeconds += r.AgentSeconds; t.Cycles += r.Cycles; t.CyclesPublished += r.CyclesPublished; t.CyclesUnpublished += r.CyclesUnpublished; t.CyclesStale += r.CyclesStale; t.CyclesStaleVisible += r.CyclesStaleVisible;
            t.Starts += r.Starts; t.StartsInView += r.StartsInView; t.StartsUnplaced += r.StartsUnplaced; t.StartsRefusedInProps += r.StartsRefusedInProps; t.StartsInsideProps += r.StartsInsideProps; t.StartsOverlappingProps += r.StartsOverlappingProps;
            t.Retired += r.Retired; t.RetirementsNotMatched += r.RetirementsNotMatched; t.Crossings += r.Crossings; t.CrossingSeconds += r.CrossingSeconds; t.OverlapSeconds += r.OverlapSeconds; t.NearSeconds += r.NearSeconds; t.StandingNearSeconds += r.StandingNearSeconds;
            t.GoalsReached += r.GoalsReached; t.StuckGoalEvents += r.StuckGoalEvents; t.TravelMeters += r.TravelMeters; t.PropSpawnRejects += r.PropSpawnRejects; t.PropGoalRejects += r.PropGoalRejects; t.PropPredictedHits += r.PropPredictedHits; t.PropPredictedSeconds += r.PropPredictedSeconds;
            double mapsBefore = t.MapsBuilt, cyclesBefore = t.Cycles - r.Cycles, usedBefore = t.MapsUsedByCycles;
            t.MapsBuilt += r.MapsBuilt; t.MapsUsedByCycles += r.MapsUsedByCycles; t.CyclesWithoutMap += r.CyclesWithoutMap;
            t.MapAgeAtCycleMean = t.MapsUsedByCycles > 0 ? (t.MapAgeAtCycleMean * usedBefore + r.MapAgeAtCycleMean * r.MapsUsedByCycles) / t.MapsUsedByCycles : 0;
            t.MapAgeAtCycleMost = Math.Max(t.MapAgeAtCycleMost, r.MapAgeAtCycleMost);
            t.MapPublishDelayMean = t.MapsBuilt > 0 ? (t.MapPublishDelayMean * mapsBefore + r.MapPublishDelayMean * r.MapsBuilt) / t.MapsBuilt : 0;
            t.MapPublishDelayMost = Math.Max(t.MapPublishDelayMost, r.MapPublishDelayMost);
            t.AgentStepsOutsideWindow += r.AgentStepsOutsideWindow; t.MostOccupiedCells = Math.Max(t.MostOccupiedCells, r.MostOccupiedCells); t.MostItems = Math.Max(t.MostItems, r.MostItems); t.MostSections = Math.Max(t.MostSections, r.MostSections); t.LowFragments += r.LowFragments;
            t.MapBuildMillisecondsInAll += r.MapBuildMillisecondsInAll; t.MapBuildMillisecondsMost = Math.Max(t.MapBuildMillisecondsMost, r.MapBuildMillisecondsMost); t.MapBuildMillisecondsMean = t.MapsBuilt > 0 ? t.MapBuildMillisecondsInAll / t.MapsBuilt : 0;
            t.CycleMillisecondsInAll += r.CycleMillisecondsInAll; t.CycleMillisecondsMost = Math.Max(t.CycleMillisecondsMost, r.CycleMillisecondsMost); t.CycleMillisecondsMean = t.Cycles > 0 ? t.CycleMillisecondsInAll / t.Cycles : 0;
            t.PrepareMillisecondsInAll += r.PrepareMillisecondsInAll; t.ReplayWallSeconds += r.ReplayWallSeconds;
            t.FieldMillisecondsInAll += r.FieldMillisecondsInAll; t.FieldResets += r.FieldResets; t.DwellMeasureMilliseconds += r.DwellMeasureMilliseconds;
            t.Dwells.AddRange(r.Dwells);
            t.ChoseMove += r.ChoseMove; t.ChoseTurn += r.ChoseTurn; t.ChoseWait += r.ChoseWait; t.ChoseTurnWithMoveCandidate += r.ChoseTurnWithMoveCandidate; t.ChoseTurnWithoutMoveCandidate += r.ChoseTurnWithoutMoveCandidate;
            t.TurnOverMoveGapSum += r.TurnOverMoveGapSum; t.ChoseTurnWithWaitCandidate += r.ChoseTurnWithWaitCandidate; t.TurnOverWaitGapSum += r.TurnOverWaitGapSum; t.WalkPostponed += r.WalkPostponed;
            t.SearchedAgentCycles += r.SearchedAgentCycles; t.StartsWithoutFieldPath += r.StartsWithoutFieldPath; t.GoalsInClosedCells += r.GoalsInClosedCells;
            for (int i = 0; i < t.GoalClosed.Length; i++) t.GoalClosed[i] += r.GoalClosed[i];
            for (int i = 0; i < t.GoalClosedInStay.Length; i++) t.GoalClosedInStay[i] += r.GoalClosedInStay[i];
            t.StraightFallbackSeconds += r.StraightFallbackSeconds; t.PatchSeconds += r.PatchSeconds; t.EstimateSeconds += r.EstimateSeconds;
            t.Searches += r.Searches; t.SearchTimeouts += r.SearchTimeouts; t.SearchCaps += r.SearchCaps;
            t.WorkLimits = r.WorkLimits; t.Expansions += r.Expansions; t.RegenerationRounds += r.RegenerationRounds; t.CandidatesTotal += r.CandidatesTotal; t.CycleTimeouts += r.CycleTimeouts; t.SkippedForBudget += r.SkippedForBudget;
            t.PlanHash = (t.PlanHash * 1099511628211UL) ^ r.PlanHash;
            t.LedGoals += r.LedGoals; t.LedGoalsVerified += r.LedGoalsVerified; t.LedGoalsUnverified += r.LedGoalsUnverified; t.NoPointGoals += r.NoPointGoals; t.LedAndNoPointGoals += r.LedAndNoPointGoals; t.StandoffGoals += r.StandoffGoals; t.ReachedNoPointGoals += r.ReachedNoPointGoals;
            t.StandoffSet += r.StandoffSet; t.StandoffNone += r.StandoffNone; t.StandoffArrivals += r.StandoffArrivals; t.ReachedWhileAtStandoff += r.ReachedWhileAtStandoff; t.GivenUpForProps += r.GivenUpForProps; t.PickedAgainForProps += r.PickedAgainForProps;
            t.ReachAgentCycles += r.ReachAgentCycles; t.ReachFound += r.ReachFound; t.ReachNone += r.ReachNone; t.ReachUnverified += r.ReachUnverified; t.ReachArrivals += r.ReachArrivals;
            t.EstimatePatches += r.EstimatePatches; t.EstimateBoundaryPatches += r.EstimateBoundaryPatches; t.EstimateBoundaryStepMost = Math.Max(t.EstimateBoundaryStepMost, r.EstimateBoundaryStepMost);
            t.RecoveryOverlapping += r.RecoveryOverlapping; t.RecoveryOverlappingFound += r.RecoveryOverlappingFound;
            t.RecoveryPatches += r.RecoveryPatches; t.RecoveryFound += r.RecoveryFound; t.RecoveryMilliseconds += r.RecoveryMilliseconds; t.RecoveryReads += r.RecoveryReads;
            t.FieldBuilds += r.FieldBuilds; t.FieldCacheHits += r.FieldCacheHits; t.FieldCellsReached += r.FieldCellsReached; t.FieldBuildMilliseconds += r.FieldBuildMilliseconds;
            t.FieldGridBuilds += r.FieldGridBuilds; t.FieldGridMilliseconds += r.FieldGridMilliseconds; t.LocalStayUnjudgedClips += r.LocalStayUnjudgedClips;
            t.StayRows += r.StayRows; t.StayChoseLeave += r.StayChoseLeave; t.StayChoseLocal += r.StayChoseLocal; t.StayChoseLoop += r.StayChoseLoop;
            t.StayNotLeaveWithLeaveCandidate += r.StayNotLeaveWithLeaveCandidate; t.StayNotLeaveWithoutLeaveCandidate += r.StayNotLeaveWithoutLeaveCandidate; t.StayLeaveGapSum += r.StayLeaveGapSum;
        }

        // ---- pictures ----

        /// <summary>Three moments of a section, ON (green) and OFF (blue) in one picture each: the frame where OFF has most NPCs inside the sections, the section's middle and its end.</summary>
        private static void Pictures(Loaded loaded, string outDir, int section, PropReplayResult on, PropReplayResult off)
        {
            if (on.Frames.Count == 0 || off.Frames.Count == 0) return;
            int worst = 0, most = -1;
            for (int i = 0; i < off.Frames.Count; i++)
            {
                int inside = off.Frames[i].Agents.Count(a => a.Inside);
                if (inside > most) { most = inside; worst = i; }
            }

            var painter = new PropTopDownPainter(1000);
            foreach ((string label, int index) in new[] { ("most-inside-off", worst), ("middle", off.Frames.Count / 2), ("end", off.Frames.Count - 1) })
            {
                int at = Math.Min(index, on.Frames.Count - 1);
                painter.Paint(loaded, on, off, at, 8.0);
                string name = FormattableString.Invariant($"section{section}-{label}-t{off.Frames[at].Time:F1}.png");
                File.WriteAllBytes(Path.Combine(outDir, name), painter.ToPng());
                File.AppendAllText(Path.Combine(outDir, "pictures.txt"), name + ": " + painter.Caption + "\n");
            }
        }

        /// <summary>
        /// Pictures of some stays of a run, 12 m about each: the two with the most absolute yaw among the "rotating-on" ones
        /// (those near the obstacle first), the longest "still" one and the longest "turn-then-wait" one.
        /// </summary>
        private static void StayPictures(Loaded loaded, string outDir, int section, PropReplayResult run)
        {
            if (run.Frames.Count == 0 || run.Dwells.Count == 0) return;
            DwellSettings s = run.DwellSettings;
            var picked = new List<DwellInterval>();
            picked.AddRange(run.Dwells.Where(d => d.Kind(s).StartsWith("rotating-on")).OrderByDescending(d => d.Near).ThenByDescending(d => d.AbsYaw).Take(2));
            DwellInterval still = run.Dwells.Where(d => d.Kind(s) == "still").OrderByDescending(d => d.Seconds).FirstOrDefault();
            DwellInterval turn = run.Dwells.Where(d => d.Kind(s) == "turn-then-wait").OrderByDescending(d => d.Seconds).FirstOrDefault();
            if (still != null) picked.Add(still);
            if (turn != null) picked.Add(turn);
            var painter = new PropTopDownPainter(800);
            foreach (DwellInterval stay in picked)
            {
                painter.PaintStay(loaded, run, stay, 12f, run.Name != "off");
                string name = FormattableString.Invariant($"stay-s{section}-{run.Name.Replace('+', '_')}-id{stay.Id}-t{stay.Start:F1}-{stay.Kind(s).Replace(' ', '_').Replace("(", "").Replace(")", "")}.png");
                File.WriteAllBytes(Path.Combine(outDir, name), painter.ToPng());
                File.AppendAllText(Path.Combine(outDir, "pictures.txt"), name + ": " + painter.Caption + "\n    " + DwellInterval.CsvHeader + "\n    " + stay.ToCsv(s, loaded.Dataset) + "\n");
            }
        }

        /// <summary>
        /// For each traced NPC of a run, three moments (a quarter, a half and three quarters through its guide records): a 20 m view
        /// about the search's start with the line to the goal (cyan), the line to the point the goal field guides to (magenta; none
        /// when the field does not guide) and the chosen candidate's next 5 s (white).
        /// </summary>
        private static void GuidePictures(Loaded loaded, string outDir, int section, PropReplayResult run)
        {
            if (run.Frames.Count == 0) return;
            var painter = new PropTopDownPainter(800);
            double step = run.Frames.Count > 1 ? run.Frames[1].Time - run.Frames[0].Time : 1.0 / 72.0;
            foreach (var group in run.GuideMarks.GroupBy(m => m.Id))
            {
                List<PropReplayResult.GuideMark> marks = group.ToList();
                foreach (int quarter in new[] { 1, 2, 3 })
                {
                    PropReplayResult.GuideMark mark = marks[Math.Min(marks.Count - 1, marks.Count * quarter / 4)];
                    painter.ViewX = mark.StartX; painter.ViewZ = mark.StartZ; painter.ViewSpan = 20f;
                    painter.Extra.Clear();
                    painter.Extra.Add((mark.StartX, mark.StartZ, mark.GoalX, mark.GoalZ, new Color32(60, 220, 255, 255)));
                    if (mark.Detour) painter.Extra.Add((mark.StartX, mark.StartZ, mark.GuideX, mark.GuideZ, new Color32(255, 80, 255, 255)));
                    if (mark.Back != null) for (int i = 0; i + 3 < mark.Back.Length; i += 2) painter.Extra.Add((mark.Back[i], mark.Back[i + 1], mark.Back[i + 2], mark.Back[i + 3], new Color32(255, 235, 60, 255)));   // the way back to the field
                    for (int i = 0; i < 10; i++) painter.Extra.Add((mark.Track[i * 2], mark.Track[i * 2 + 1], mark.Track[i * 2 + 2], mark.Track[i * 2 + 3], new Color32(255, 255, 255, 255)));
                    int index = Math.Max(0, Math.Min(run.Frames.Count - 1, (int)Math.Round((mark.Time - run.Frames[0].Time) / step)));
                    painter.Paint(loaded, run.Name != "off" ? run : null, run.Name != "off" ? null : run, index, 3.0);
                    string name = FormattableString.Invariant($"guide-s{section}-{run.Name.Replace('+', '_')}-id{mark.Id}-t{mark.Time:F1}.png");
                    File.WriteAllBytes(Path.Combine(outDir, name), painter.ToPng());
                    File.AppendAllText(Path.Combine(outDir, "pictures.txt"), name + FormattableString.Invariant($": GUIDE of NPC {mark.Id} in run '{run.Name}' at {mark.Time:F1} s: cyan = to the goal ({mark.GoalX:F1}, {mark.GoalZ:F1}); magenta = to the point the goal field guides to ({(mark.Detour ? "guiding" : "not guiding: the straight line is taken")}); white = the chosen candidate's next 5 s; view 20 m, grid 1 m. ") + painter.Caption + "\n");
                    painter.Extra.Clear(); painter.ViewSpan = 0f;
                }
            }
        }

        // ---- eye: the character models' head heights ----

        private static void Eye(string outDir)
        {
            var lines = new List<string> { "model,top_m,head_bone_m,eye_bone_m" };
            var tops = new List<float>(); var heads = new List<float>();
            foreach (string guid in AssetDatabase.FindAssets("t:GameObject", new[] { "Assets/Licensed/MobPlanModels/characters" }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var model = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (model == null) continue;
                GameObject instance = UnityEngine.Object.Instantiate(model);
                try
                {
                    float floor = instance.transform.position.y;
                    float top = float.NegativeInfinity;
                    foreach (Renderer renderer in instance.GetComponentsInChildren<Renderer>()) top = Mathf.Max(top, renderer.bounds.max.y - floor);
                    float head = float.NaN, eye = float.NaN;
                    foreach (Transform t in instance.GetComponentsInChildren<Transform>())
                    {
                        string n = t.name.ToLowerInvariant();
                        if (n.EndsWith("def-spine.006") || n == "head" || n.EndsWith("def-head")) head = t.position.y - floor;
                        if (n.Contains("eye") && float.IsNaN(eye)) eye = t.position.y - floor;
                    }

                    lines.Add(FormattableString.Invariant($"{Path.GetFileNameWithoutExtension(path)},{top:F3},{head:F3},{eye:F3}"));
                    if (!float.IsInfinity(top)) tops.Add(top);
                    if (!float.IsNaN(head)) heads.Add(head);
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(instance);
                }
            }

            tops.Sort(); heads.Sort();
            string Stats(List<float> v) => v.Count == 0 ? "none" : FormattableString.Invariant($"n {v.Count}, least {v[0]:F3}, median {v[v.Count / 2]:F3}, most {v[v.Count - 1]:F3}");
            lines.Add("# top of the model (renderer bounds, bind pose): " + Stats(tops));
            lines.Add("# head bone: " + Stats(heads));
            File.WriteAllLines(Path.Combine(outDir, "eye-heights.csv"), lines);
            Debug.Log("PROP REPLAY eye: tops " + Stats(tops) + "; head bones " + Stats(heads));
        }
    }

    /// <summary>A top-down picture of the map window: the static map, the sections, the planner's cells, NPCs of two runs, starts, the player.</summary>
    public sealed class PropTopDownPainter
    {
        public readonly int Size;
        public readonly Color32[] Pixels;
        public string Caption = string.Empty;
        private float originX, originZ, scale;
        private readonly PropObstacleBuilder builder = new PropObstacleBuilder();
        private readonly PropObstacleMap grid = new PropObstacleMap();
        private readonly PropObstacleMap yard = new PropObstacleMap();
        private readonly PropSectionList sections = new PropSectionList();
        private Color32[] background;
        private float backgroundX = float.NaN, backgroundZ = float.NaN;

        public PropTopDownPainter(int size) { Size = size; Pixels = new Color32[size * size]; }

        /// <summary>The view: `ViewSpan` metres about (ViewX, ViewZ); 0: the map's whole window.</summary>
        public float ViewX, ViewZ, ViewSpan;
        /// <summary>One stay to show: its radius about where it began, and the NPC's heading every sixth of a second of it (dark at its start, bright at its end).</summary>
        public DwellInterval Highlight;
        public PropReplayResult HighlightRun;
        /// <summary>Lines drawn over everything else (world x, z to x, z).</summary>
        public readonly List<(float x0, float z0, float x1, float z1, Color32 color)> Extra = new List<(float, float, float, float, Color32)>();
        private float backgroundScale;

        /// <summary>A stay of a run: the view about where it began, the frame at its middle, the paths over the stay.</summary>
        public void PaintStay(PropObstacleReplayTool.Loaded loaded, PropReplayResult run, DwellInterval stay, float span, bool asOn)
        {
            ViewX = stay.StartX; ViewZ = stay.StartZ; ViewSpan = span;
            Highlight = stay; HighlightRun = run;
            double step = run.Frames.Count > 1 ? run.Frames[1].Time - run.Frames[0].Time : 1.0 / 72.0;
            int index = Math.Max(0, Math.Min(run.Frames.Count - 1, (int)Math.Round(((stay.Start + stay.End) * 0.5 - run.Frames[0].Time) / step)));
            Paint(loaded, asOn ? run : null, asOn ? null : run, index, stay.Seconds * 0.5 + 1.0);
            Caption = FormattableString.Invariant($"STAY of NPC {stay.Id} in run '{run.Name}': {stay.Start:F1}-{stay.End:F1} s ({stay.Seconds:F1} s, ended: {stay.EndedBy}), {stay.Kind(run.DwellSettings)}, absolute yaw {stay.AbsYaw:F0} deg (signed {stay.SignedYaw:F0}), path {stay.PathMeters:F2} m, farthest {stay.MaxDistance:F2} m, least distance to the sections {(float.IsInfinity(stay.LeastObstacleDistance) ? -1f : stay.LeastObstacleDistance):F2} m; yellow ring: the stay's radius; rays: the heading every 1/6 s (dark to bright); view {span:F0} m, grid 1 m. ") + Caption;
            Highlight = null; HighlightRun = null; ViewSpan = 0f;
        }

        private static readonly Color32 Walkable = new Color32(24, 26, 30, 255), Blocked = new Color32(70, 74, 82, 255), GridLine = new Color32(40, 44, 50, 255);
        public static readonly Color32 SectionColor = new Color32(255, 170, 40, 255), CellColor = new Color32(150, 50, 50, 255), OnColor = new Color32(70, 220, 110, 255), OffColor = new Color32(90, 150, 255, 255),
            InsideColor = new Color32(255, 60, 60, 255), PlayerColor = new Color32(255, 255, 255, 255), StartColor = new Color32(255, 255, 90, 255), RefusedColor = new Color32(255, 110, 220, 255);

        private int Px(float x) => (int)((x - originX) * scale);
        private int Pz(float z) => (int)((z - originZ) * scale);

        private void Set(int x, int y, Color32 c) { if (x >= 0 && y >= 0 && x < Size && y < Size) Pixels[y * Size + x] = c; }

        public void Line(float x0, float z0, float x1, float z1, Color32 c)
        {
            int ax = Px(x0), ay = Pz(z0), bx = Px(x1), by = Pz(z1);
            int dx = Math.Abs(bx - ax), dy = Math.Abs(by - ay), sx = ax < bx ? 1 : -1, sy = ay < by ? 1 : -1, err = dx - dy;
            for (int guard = 0; guard < 4 * Size; guard++)
            {
                Set(ax, ay, c);
                if (ax == bx && ay == by) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; ax += sx; }
                if (e2 < dx) { err += dx; ay += sy; }
            }
        }

        public void Circle(float x, float z, float radius, Color32 c, bool filled)
        {
            int cx = Px(x), cy = Pz(z), r = Math.Max(1, (int)(radius * scale));
            for (int y = -r; y <= r; y++)
            {
                for (int xx = -r; xx <= r; xx++)
                {
                    int d = xx * xx + y * y;
                    if (d > r * r || (!filled && d < (r - 1) * (r - 1))) continue;
                    Set(cx + xx, cy + y, c);
                }
            }
        }

        /// <summary>The frame `index` of two runs of one section (the same virtual times), with each NPC's path over `trail` seconds before and after.</summary>
        public void Paint(PropObstacleReplayTool.Loaded loaded, PropReplayResult on, PropReplayResult off, int index, double trail, bool showOn = true, bool showOff = true)
        {
            PropCapture.Session s = loaded.Session;
            PropReplayResult.Frame frame = (off ?? on).Frames[index];
            int truth = frame.TruthBuild >= 0 ? frame.TruthBuild : Math.Max(0, s.BuildIndexAt(frame.Time));
            PropObstacleInput window = s.Builds[truth].Input;
            float size = window.Cells * window.Cell;
            originX = window.OriginX; originZ = window.OriginZ; scale = Size / size;
            float gridStep = 5f;
            if (ViewSpan > 0f) { originX = ViewX - ViewSpan * 0.5f; originZ = ViewZ - ViewSpan * 0.5f; scale = Size / ViewSpan; if (ViewSpan <= 20f) gridStep = 1f; }
            if (background == null || backgroundX != originX || backgroundZ != originZ || backgroundScale != scale)
            {
                backgroundScale = scale;
                background = new Color32[Size * Size];
                for (int y = 0; y < Size; y++)
                {
                    float z = originZ + (y + 0.5f) / scale;
                    for (int x = 0; x < Size; x++)
                    {
                        float wx = originX + (x + 0.5f) / scale;
                        bool line = (int)Math.Floor(wx / gridStep) != (int)Math.Floor((wx - 1f / scale) / gridStep) || (int)Math.Floor(z / gridStep) != (int)Math.Floor((z - 1f / scale) / gridStep);
                        background[y * Size + x] = !loaded.Map.IsWalkableAt(wx, z) ? Blocked : line ? GridLine : Walkable;
                    }
                }

                backgroundX = originX; backgroundZ = originZ;
            }

            Array.Copy(background, Pixels, Pixels.Length);

            // the planner's cells of the ON run's version at this moment
            PropReplayResult.Frame onFrame = on != null && index < on.Frames.Count ? on.Frames[index] : null;
            int plannerBuild = onFrame != null && !double.IsNaN(onFrame.MapInputTime) ? s.BuildIndexAt(onFrame.MapInputTime + 1e-6) : -1;
            if (showOn && plannerBuild >= 0)
            {
                builder.Build(s.Builds[plannerBuild].Input, null, grid);
                int step = Math.Max(1, (int)(grid.Cell * scale));
                for (int cz = 0; cz < grid.Cells; cz++)
                {
                    for (int cx = 0; cx < grid.Cells; cx++)
                    {
                        if (grid.CellDistance(cx, cz) > 0f) continue;
                        int px = Px(grid.OriginX + cx * grid.Cell), py = Pz(grid.OriginZ + cz * grid.Cell);
                        for (int y = 0; y < step; y++) for (int x = 0; x < step; x++) Set(px + x, py + y, CellColor);
                    }
                }
            }

            // the sections of the newest recorded build
            builder.Build(s.Builds[truth].Input, null, yard, sections);
            for (int p = 0; p < sections.Count; p++)
            {
                int a = sections.Start[p], b = sections.Start[p + 1];
                for (int i = a; i < b; i++)
                {
                    int j = i + 1 == b ? a : i + 1;
                    Line(sections.Points[i * 2], sections.Points[i * 2 + 1], sections.Points[j * 2], sections.Points[j * 2 + 1], SectionColor);
                }
            }

            int insideOn = 0, insideOff = 0;
            for (int pass = 0; pass < 2; pass++)
            {
                PropReplayResult run = pass == 0 ? off : on;
                if (run == null || (pass == 0 ? !showOff : !showOn) || index >= run.Frames.Count) continue;
                Color32 color = pass == 0 ? OffColor : OnColor;
                var dim = new Color32((byte)(color.r / 2), (byte)(color.g / 2), (byte)(color.b / 2), 255);
                // paths: where each NPC was and went in this run (the plans as followed)
                double time = run.Frames[index].Time;
                var last = new Dictionary<int, (float x, float z)>();
                for (int f = 0; f < run.Frames.Count; f += 6)
                {
                    PropReplayResult.Frame other = run.Frames[f];
                    if (other.Time < time - trail || other.Time > time + trail) continue;
                    foreach (PropReplayResult.Agent agent in other.Agents)
                    {
                        if (float.IsNaN(agent.X)) continue;
                        if (last.TryGetValue(agent.Id, out var before) && Math.Abs(before.x - agent.X) + Math.Abs(before.z - agent.Z) < 3f) Line(before.x, before.z, agent.X, agent.Z, other.Time <= time ? dim : color);
                        last[agent.Id] = (agent.X, agent.Z);
                    }
                }

                foreach (PropReplayResult.Agent agent in run.Frames[index].Agents)
                {
                    if (float.IsNaN(agent.X)) continue;
                    Circle(agent.X, agent.Z, loaded.Scenario.npcRadius, color, true);
                    if (agent.Inside) { Circle(agent.X, agent.Z, loaded.Scenario.npcRadius + 0.25f, InsideColor, false); if (pass == 0) insideOff++; else insideOn++; }
                    float yaw = agent.Yaw * Mathf.Deg2Rad;
                    Line(agent.X, agent.Z, agent.X + Mathf.Sin(yaw) * 0.7f, agent.Z + Mathf.Cos(yaw) * 0.7f, PlayerColor);
                }

                foreach (PropReplayResult.StartMark mark in run.StartMarks)
                {
                    if (mark.Time > time || mark.Time < time - trail || mark.Result == PropCapture.StartUnplaced) continue;
                    Color32 c = mark.Result == PropCapture.StartStarted ? (mark.Inside ? InsideColor : StartColor) : mark.Result == PropCapture.StartInProps ? RefusedColor : dim;
                    Line(mark.X - 0.6f, mark.Z - 0.6f, mark.X + 0.6f, mark.Z + 0.6f, c);
                    Line(mark.X - 0.6f, mark.Z + 0.6f, mark.X + 0.6f, mark.Z - 0.6f, c);
                }
            }

            if (Highlight != null && HighlightRun != null)
            {
                Circle(Highlight.StartX, Highlight.StartZ, HighlightRun.DwellSettings != null ? HighlightRun.DwellSettings.RadiusMeters : 0.5f, StartColor, false);
                double span = Math.Max(1e-3, Highlight.End - Highlight.Start);
                for (int f = 0; f < HighlightRun.Frames.Count; f += 12)
                {
                    PropReplayResult.Frame other = HighlightRun.Frames[f];
                    if (other.Time < Highlight.Start || other.Time > Highlight.End) continue;
                    foreach (PropReplayResult.Agent agent in other.Agents)
                    {
                        if (agent.Id != Highlight.Id || float.IsNaN(agent.X)) continue;
                        float u = (float)((other.Time - Highlight.Start) / span);
                        var c = new Color32((byte)(80 + 175 * u), (byte)(40 + 215 * u), (byte)(160 - 120 * u), 255);
                        float a = agent.Yaw * Mathf.Deg2Rad;
                        Line(agent.X, agent.Z, agent.X + Mathf.Sin(a) * 1.1f, agent.Z + Mathf.Cos(a) * 1.1f, c);
                    }
                }
            }

            foreach (var line in Extra) Line(line.x0, line.z0, line.x1, line.z1, line.color);
            PropCapture.PlayerSample player = s.PlayerAt(frame.Time);
            Circle(player.X, player.Z, 1.5f, PlayerColor, false);
            float heading = player.Yaw * Mathf.Deg2Rad;
            Line(player.X, player.Z, player.X + Mathf.Sin(heading) * 3f, player.Z + Mathf.Cos(heading) * 3f, PlayerColor);
            double age = onFrame != null && !double.IsNaN(onFrame.MapInputTime) ? frame.Time - onFrame.MapInputTime : double.NaN;
            Caption = FormattableString.Invariant($"t {frame.Time:F2} s; window {size:F0} m from ({originX:F1}, {originZ:F1}); sections {sections.Count} (orange outlines, build {truth}); planner cells (dark red) of the ON run's version: input {(double.IsNaN(age) ? "none" : (age * 1000).ToString("F0", CultureInfo.InvariantCulture) + " ms old")}; ")
                + FormattableString.Invariant($"NPCs ON green ({(on != null && index < on.Frames.Count ? on.Frames[index].Agents.Length : 0)}, inside {insideOn}), OFF blue ({(off != null ? off.Frames[index].Agents.Length : 0)}, inside {insideOff}; red ring = centre inside a section); paths {trail:F0} s before (dim) and after; crosses: starts (yellow; red inside a section; pink refused in a felled piece); white ring: the player (1.5 m) and heading; grid 5 m");
        }

        public byte[] ToPng()
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(Pixels);
                texture.Apply(false);
                return texture.EncodeToPNG();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
