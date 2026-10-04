using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Zantetsu.Core.Animation;
using Zantetsu.Core.Input;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.PhysicsCut;

namespace Zantetsu.Sandbox
{
    // Measurement (the MobPlanSlash unit, Assets/Licensed/MobPlan/MobPlanCity.unity): twenty NPCs moved by their published
    // plans, the player moved by a saved script, and the katana fed by saved grip rows in chunks. With MobPlanArgument and
    // a script the check waits for the crowd, then only feeds the saved input through the product's entrances -- the
    // player's MobPlanPlayerInput.Submit (at the live input's own execution order) and the sandbox recorder's replay into
    // the katana -- and follows what happens: every NPC taken in or retired by the crowd, every hit and cut (with the
    // multi-NPC mode's per-Slash and per-operation following), the plan cycles, and at the end judges the sequence
    // (cut, retirement, re-cut of a child, replacement, a replacement cut). Nothing is cut, hit, stopped, moved or
    // published from here; a Slash that meets nothing is written down as such.
    //
    // The script (one command per line, '#' comments; times in seconds after the replay's start):
    //   move,<from>,<to>,<forward -1..1>,<turn -1..1>    the stick held over that span
    //   slash,<at>,<start row>,<rows>                    a chunk of the saved grip rows, begun at or after <at> once the
    //                                                    previous chunk is fed through and no wave flies
    //   end,<at>                                         nothing more is fed after this; the run ends once every cut has
    //                                                    committed (bounded)
    public static partial class SandboxPropSlashPlayerCheck
    {
        public const string MobPlanArgument = "-zantetsuMobPlan";

        // Names by character for the MobPlan mode ("a<id>" for the first twenty, "r<id>" for a replacement): the multi-NPC
        // following names a character by Model(), which reads this first.
        private static readonly Dictionary<SandboxNpcCharacter, string> s_mobPlanNames = new Dictionary<SandboxNpcCharacter, string>();

        // The scripted stick, fed at the live input's own execution order (MobPlanPlayerInput, -100): before the crowd
        // reads the player (-75) and before the katana places the blade by the rig (0), as a held stick would be.
        [DefaultExecutionOrder(-100)]
        private sealed class MobPlanScriptedStick : MonoBehaviour
        {
            internal Func<(float forward, float turn)?> command;
            internal MobPlanPlayerInput input;

            // The city walk (2026-10-03): the script's spans given exactly instead (each span for the time this frame
            // overlaps it), through the same Submit.
            internal Action drive;

            private void Update()
            {
                if (drive != null)
                {
                    drive();
                    return;
                }

                (float forward, float turn)? c = command?.Invoke();
                if (c.HasValue && input != null)
                {
                    input.Submit(c.Value.forward, c.Value.turn, Time.deltaTime);
                }
            }
        }

        private sealed partial class Walk
        {
            internal string mobPlan;

            private bool MobPlanMode => !string.IsNullOrEmpty(mobPlan);

            // "-zantetsuMobPlan live": a person plays -- no script, no saved input, no head wait, no picture. Only what
            // happens is written, as it happens (mobplan-record.txt and the csv files are flushed line by line), so a
            // session ended by closing the Player keeps every hit.
            private bool MobPlanLive => mobPlan == "live";

            // "-zantetsuMobPlanDetail": the frame-by-frame detail files (hit registration, display tracking, piece lifetime,
            // view) are written, for a diagnosis. Without it -- a performance run -- those rows are neither built nor written;
            // the scenario judgements, the counts, the first failures and the summaries at the end are kept as before. Files
            // are flushed line by line only in the live mode; otherwise the ordinary close (at the end, or at quitting) writes
            // them out.
            private bool MobPlanDetail => Environment.GetCommandLineArgs().Contains("-zantetsuMobPlanDetail");
            private bool _mpDetail;
            private StreamWriter _mpRecord;

            private void MobPlanRecord(string line)
            {
                if (!MobPlanMode) return;
                if (_mpRecord == null)
                {
                    Directory.CreateDirectory(directory);
                    _mpRecord = new StreamWriter(Path.Combine(directory, "mobplan-record.txt")) { AutoFlush = MobPlanLive };
                }

                _mpRecord.WriteLine(line);
            }

            private MobPlanCrowd _crowd;
            // The lineage root of every individual, named when first seen: a slot carries several individuals in turn, so a
            // piece is named by the individual its root fragment was, not by the slot's current one.
            private readonly Dictionary<LogicalFragmentId, string> _mpRootNames = new Dictionary<LogicalFragmentId, string>();

            // What the plan gave an individual in the frame the crowd retired it: the crowd can retire it in the very frame
            // it is hit, before this check reads that hit, and after that the plan has nothing for it.
            private readonly Dictionary<int, (int frame, double source, string clip)> _mpPlanAtRetirement =
                new Dictionary<int, (int frame, double source, string clip)>();
            // Each slot's individuals in turn, and every reuse with what the previous one's cut had reached then.
            private readonly Dictionary<SandboxNpcCharacter, List<string>> _mpSlotIndividuals = new Dictionary<SandboxNpcCharacter, List<string>>();
            private readonly List<string> _mpReuses = new List<string>();
            private readonly List<string> _mpReuseViolations = new List<string>();
            private int _mpDetectorMax, _mpDirectMadeAtBegin = -1;
            private int _mpSlotsAtBegin = -1, _mpFullPreparationsAtBegin = -1, _mpFreeMin = int.MaxValue, _mpReturningMax, _mpPreparingMax;
            private PoseLodDirector _mpLod;
            private int _mpLodExcessMax, _mpLodShortMax, _mpLodMax;
            private MobPlanPlayerInput _mpInput;
            private MobPlanScriptedStick _mpStick;
            private readonly List<(double from, double to, float forward, float turn)> _mpMoves = new List<(double, double, float, float)>();
            private readonly List<(double at, int start, int rows)> _mpSlashes = new List<(double, int, int)>();
            private double _mpEnd = 60.0;
            private int _mpNextSlash;
            private string[] _mpInputLines;
            private StreamWriter _mpFrames, _mpActors, _mpEvents;
            private readonly Dictionary<SandboxNpcCharacter, (int id, bool replacement, int addedFrame)> _mpActorOf =
                new Dictionary<SandboxNpcCharacter, (int, bool, int)>();
            private readonly Dictionary<int, (SandboxNpcCharacter c, int frame)> _mpRetired = new Dictionary<int, (SandboxNpcCharacter, int)>();
            private readonly List<string> _mpViolations = new List<string>();
            private readonly Dictionary<SandboxNpcCharacter, string> _mpRetiredDrawn = new Dictionary<SandboxNpcCharacter, string>();
            private int _mpLiveAtBegin = -1, _mpPublishedAtBegin, _mpStaleAtBegin, _mpReplacementsAtBegin;
            private bool _mpSubscribedLate;
            private double _mpNextActorRow;
            private float _mpTravel, _mpMinDistance = float.PositiveInfinity;
            private string _mpMinDistanceAt;
            private Vector3 _mpPreviousPlayer;
            private bool _replayBeforeReady;
            private int _mpChunk = -1;
            private readonly List<(string name, int frame, int appliedFrame, double applied, double planned, string clip, string plannedClip)> _mpHitPoses =
                new List<(string, int, int, double, double, string, string)>();

            private double MobPlanNow => Time.unscaledTimeAsDouble - _clockStart;

            // The script, the crowd and the scripted stick; the crowd's events are taken before it is ready, so the first
            // twenty are seen being taken in. Then waits (bounded) for the crowd to be ready.
            private System.Collections.IEnumerator MobPlanWaitReady()
            {
                string[] scriptLines = MobPlanLive ? new string[0] : File.ReadAllLines(mobPlan);
                // The city walk's step script (2026-10-03): followed by the player's position, parsed once the player is found.
                bool steps = CityWalkSteps.IsStepScript(scriptLines);
                foreach (string raw in steps ? new string[0] : scriptLines)
                {
                    string line = raw.Split('#')[0].Trim();
                    if (line.Length == 0) continue;
                    string[] c = line.Split(',');
                    switch (c[0].Trim())
                    {
                        case "move":
                            _mpMoves.Add((double.Parse(c[1], Inv), double.Parse(c[2], Inv), float.Parse(c[3], Inv), float.Parse(c[4], Inv)));
                            break;
                        case "slash":
                            _mpSlashes.Add((double.Parse(c[1], Inv), int.Parse(c[2], Inv), int.Parse(c[3], Inv)));
                            break;
                        case "end":
                            _mpEnd = double.Parse(c[1], Inv);
                            break;
                        default:
                            throw new FormatException("mobplan script: " + raw);
                    }
                }

                if (MobPlanLive)
                {
                    _mpEnd = 24.0 * 3600.0;
                    _mpInputLines = new string[0];
                }
                else
                {
                    File.Copy(mobPlan, Path.Combine(directory, "mobplan-script.txt"), true);
                    _mpInputLines = File.ReadAllLines(input);
                }

                _crowd = FindAnyObjectByType<MobPlanCrowd>();
                _mpLod = FindAnyObjectByType<PoseLodDirector>();
                _mpInput = FindAnyObjectByType<MobPlanPlayerInput>();
                if (_crowd == null || _mpInput == null)
                {
                    Log("FAILED: mobplan: the scene has no MobPlanCrowd or no MobPlanPlayerInput");
                    yield break;
                }

                if (steps)
                {
                    Vector3 at = _mpInput.player.transform.position;
                    _mpSteps = CityWalkSteps.Parse(scriptLines, new Vector2(at.x, at.z));
                    _mpEnd = _mpSteps.Budget;
                    if (cityWalk && Has(CityWalkHitShotsArgument)) _mpSteps.HoldBeforeSlash = CityWalkHoldBeforeSlash;
                    Log("mobplan: pictures before and after each visit's Slashes " + (_mpSteps.HoldBeforeSlash != null ? "ON (" + CityWalkHitShotsArgument + ")" : "off"));
                    Log("mobplan: attacks on the NPCs met " + (_mpSteps.Engage == null ? "off (no engage line)" : "ON: range " + _mpSteps.Engage.range.ToString("R", Inv) + " m, half angle "
                        + _mpSteps.Engage.halfAngle.ToString("R", Inv) + " deg, face within " + _mpSteps.Engage.faceTolerance.ToString("R", Inv) + " deg in " + _mpSteps.Engage.turnSeconds.ToString("R", Inv)
                        + " s, cooldown " + _mpSteps.Engage.cooldown.ToString("R", Inv) + " s, at most " + _mpSteps.Engage.maxAttacks + " attacks and " + _mpSteps.Engage.totalSeconds.ToString("R", Inv)
                        + " s, none within " + _mpSteps.Engage.nearStand.ToString("R", Inv) + " m of the plan's next stand, rows " + _mpSteps.Engage.start + "+" + _mpSteps.Engage.rows));
                    Log("mobplan: the step script (followed by the player's position): " + _mpSteps.Steps.Count + " steps, slashes " + _mpSteps.SlashSteps
                        + ", planned " + _mpSteps.PlannedMetres.ToString("F1", Inv) + " m from " + at.ToString("F2") + ", budget " + _mpSteps.Budget.ToString("R", Inv) + " s");
                }

                _mpSubscribedLate = _crowd.IsReady;
                _crowd.ActorAdded += MobPlanAdded;
                _crowd.ActorRetired += MobPlanRetired;
                if (!MobPlanLive)
                {
                    _mpInput.liveInput = false;
                    _mpStick = gameObject.AddComponent<MobPlanScriptedStick>();
                    _mpStick.input = _mpInput;
                    _mpStick.command = MobPlanCommand;
                    if (cityWalk) _mpStick.drive = MobPlanDrive;
                    if (_mpSteps != null) _mpStick.drive = MobPlanStepDrive;
                }

                _mpEvents = new StreamWriter(Path.Combine(directory, "mobplan-events.csv")) { AutoFlush = MobPlanLive };
                _mpEvents.WriteLine("frame,t,event,id,name,detail");
                Log("mobplan: script moves=" + _mpMoves.Count + " slashes=" + _mpSlashes.Count + " end=" + _mpEnd.ToString("R", Inv)
                    + " input=" + input + " (" + _mpInputLines.Length + " lines); live stick off, scripted stick at order -100"
                    + (cityWalk ? " (the city walk: each span given for exactly the time a frame overlaps it)" : "")
                    + (_mpSubscribedLate ? "; WARNING: the crowd was ready before the check subscribed" : ""));
                float until = StageBegin("crowd ready", 120f);
                while (!_crowd.IsReady && Time.realtimeSinceStartup < until)
                {
                    yield return null;
                }

                StageEnd(!_crowd.IsReady, "live " + _crowd.LiveCount);

                Log("mobplan: crowd ready=" + _crowd.IsReady + " live=" + _crowd.LiveCount + " at frame " + Time.frameCount
                    + " player=" + _mpInput.player.transform.position.ToString("F3"));
            }

            // The stick for this frame from the script, only while the replay runs and before the script's end.
            private (float forward, float turn)? MobPlanCommand()
            {
                if (!_replaying || _clockStart <= 0.0)
                {
                    return null;
                }

                double t = MobPlanNow;
                foreach ((double from, double to, float forward, float turn) m in _mpMoves)
                {
                    if (t >= m.from && t < m.to)
                    {
                        return (m.forward, m.turn);
                    }
                }

                return (0f, 0f);
            }

            // The city walk (2026-10-03): a held stick sampled once a frame turns or walks a whole frame more or less than its
            // span (some 1.3 degrees a turn at 90 fps, more in a long frame), which over a walk of many turns leaves the
            // player off the route. Here each span of the script is given for exactly the part of the frame's interval
            // [the previous frame's script time, this one's) that it covers -- the same stick values, through the same
            // Submit, only the seconds told apart -- so the script's turns and distances add up as written.
            private double _mpDrivenTo = double.NaN;

            private void MobPlanDrive()
            {
                if (!_replaying || _clockStart <= 0.0)
                {
                    _mpDrivenTo = double.NaN;
                    return;
                }

                double now = MobPlanNow;
                double since = double.IsNaN(_mpDrivenTo) ? now : _mpDrivenTo;
                _mpDrivenTo = now;
                foreach ((double from, double to, float forward, float turn) m in _mpMoves)
                {
                    double a = Math.Max(since, m.from), b = Math.Min(now, m.to);
                    if (b > a) _mpInput.Submit(m.forward, m.turn, (float)(b - a));
                }
            }

            // The step script (2026-10-03): each frame the current step judged against where the player stands and faces now,
            // the stick it asks for given through the same Submit, and a Slash's chunk begun when its step comes and the
            // katana is idle. Once the steps are done (or halted) the script's end is now.
            private CityWalkSteps _mpSteps;

            // The NPCs the walk may attack (TL, 2026-10-03): each live individual still a hit target, replacements included,
            // within 1.3 x the attack range of the head, with whether a line from the head to its chest (1.2 m up) is clear of
            // anything but the individual itself -- the city's colliders, a piece's -- as the caller's view of who is seen.
            private readonly List<CityWalkSteps.Seen> _cwSeen = new List<CityWalkSteps.Seen>();

            private List<CityWalkSteps.Seen> CityWalkSeen()
            {
                _cwSeen.Clear();
                Transform head = Camera.main != null ? Camera.main.transform : _mpInput.player.transform;
                Vector3 from = head.position;
                float reach = _mpSteps.Engage.range * 1.3f;
                foreach (KeyValuePair<SandboxNpcCharacter, (int id, bool replacement, int addedFrame)> a in _mpActorOf)
                {
                    SandboxNpcCharacter c = a.Key;
                    if (c == null || _mpRetired.ContainsKey(a.Value.id) || !c.IsTarget || c.CharacterRoot == null || !c.CharacterRoot.activeInHierarchy) continue;
                    Vector3 p = c.CharacterRoot.transform.position;
                    var flat = new Vector2(p.x, p.z);
                    if (Vector2.Distance(flat, new Vector2(from.x, from.z)) > reach) continue;
                    bool visible = !Physics.Linecast(from, p + Vector3.up * 1.2f, out RaycastHit hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)
                        || hit.transform.IsChildOf(c.CharacterRoot.transform);
                    _cwSeen.Add(new CityWalkSteps.Seen { id = a.Value.id, position = flat, visible = visible, replacement = a.Value.replacement });
                }

                return _cwSeen;
            }

            // The frames of each attack's chunk and end, for the summary's attribution of hits.
            private void CityWalkEngagementFrames()
            {
                foreach (CityWalkSteps.Engagement e in _mpSteps.Engagements)
                {
                    if (!double.IsNaN(e.chunkAt) && e.chunkFrame < 0) e.chunkFrame = Time.frameCount;
                    if (!double.IsNaN(e.endedAt) && e.endFrame < 0) e.endFrame = Time.frameCount;
                }
            }

            /// <summary>
            /// The attacks on the NPCs met (TL, 2026-10-03), told apart: met (seen in range, in view, unoccluded), attacked
            /// (an attack begun), struck (a chunk fed), hit (an NPC's root or piece accepted between its chunk and its end),
            /// the target itself hit, cut (an NPC root's cut committed), a replacement cut, and missed (fed with no NPC hit).
            /// Each attack to city-walk-attacks.csv; the totals to the log. Nothing is required of them.
            /// </summary>
            private void CityWalkEngagementSummary()
            {
                if (_mpSteps == null || _mpSteps.Engage == null) return;
                int fed = 0, lost = 0, unfaced = 0, other = 0, struckAndHit = 0, targetHit = 0, missed = 0, rootCuts = 0, replacementCuts = 0, pieceHits = 0, propOrBuildingHits = 0;
                try
                {
                    using (var csv = new StreamWriter(Path.Combine(directory, "city-walk-attacks.csv")))
                    {
                        csv.WriteLine("attack,npc,replacement,visit,begunAt,chunkAt,endedAt,chunkFrame,endFrame,distance,angle,outcome,npcRootHits,npcPieceHits,targetHit,npcRootCutsCommitted,replacementCutsCommitted,otherHits");
                        for (int i = 0; i < _mpSteps.Engagements.Count; i++)
                        {
                            CityWalkSteps.Engagement e = _mpSteps.Engagements[i];
                            if (e.outcome == "fed") fed++;
                            else if (e.outcome != null && e.outcome.StartsWith("lost", System.StringComparison.Ordinal)) lost++;
                            else if (e.outcome != null && e.outcome.StartsWith("not faced", System.StringComparison.Ordinal)) unfaced++;
                            else other++;
                            int roots = 0, pieces = 0, cuts = 0, replacementsCut = 0, others = 0;
                            bool target = false;
                            string targetName = "npc-" + (e.replacement ? "r" : "a") + e.id.ToString(Inv);
                            if (e.chunkFrame >= 0)
                            {
                                int last = e.endFrame >= 0 ? e.endFrame : int.MaxValue;
                                foreach (Accepted a in _accepted)
                                {
                                    if (a.acceptedFrame < e.chunkFrame || a.acceptedFrame > last) continue;
                                    string lineage = LineageOf(a.fragment);
                                    if (KindOf(lineage) != "npc") { others++; continue; }
                                    if (a.child) { pieces++; continue; }
                                    roots++;
                                    if (lineage == targetName) target = true;
                                    if (a.committedFrame >= 0)
                                    {
                                        cuts++;
                                        if (lineage.StartsWith("npc-r", System.StringComparison.Ordinal)) replacementsCut++;
                                    }
                                }
                            }

                            if (e.outcome == "fed")
                            {
                                if (roots + pieces > 0) struckAndHit++; else missed++;
                            }

                            if (target) targetHit++;
                            rootCuts += cuts;
                            replacementCuts += replacementsCut;
                            pieceHits += pieces;
                            propOrBuildingHits += others;
                            csv.WriteLine((i + 1).ToString(Inv) + "," + e.id.ToString(Inv) + "," + e.replacement + "," + e.visit + "," + e.begunAt.ToString("F3", Inv) + "," + e.chunkAt.ToString("F3", Inv) + ","
                                + e.endedAt.ToString("F3", Inv) + "," + e.chunkFrame.ToString(Inv) + "," + e.endFrame.ToString(Inv) + "," + e.distance.ToString("F2", Inv) + "," + e.angle.ToString("F1", Inv) + ","
                                + (e.outcome ?? "running").Replace(",", ";") + "," + roots + "," + pieces + "," + target + "," + cuts + "," + replacementsCut + "," + others);
                        }
                    }
                }
                catch (IOException ex)
                {
                    Log("city walk attacks file: " + ex.Message);
                }

                // Whether the walk struck at what it met (TL, 2026-10-03): the frames each NPC was seen eligible and no attack
                // began, by reason; the individuals never attacked, each under its most frequent reason and every reason it met.
                var neverAttacked = _mpSteps.Encountered.Where(id => !_mpSteps.Attacked.Contains(id)).ToList();
                var primary = new SortedDictionary<string, int>(System.StringComparer.Ordinal);
                var anyReason = new SortedDictionary<string, int>(System.StringComparer.Ordinal);
                foreach (int id in neverAttacked)
                {
                    if (!_mpSteps.PassedOverById.TryGetValue(id, out SortedDictionary<string, long> by) || by.Count == 0) { primary["(no frame passed over)"] = primary.TryGetValue("(no frame passed over)", out int z) ? z + 1 : 1; continue; }
                    string top = by.OrderByDescending(kv => kv.Value).First().Key;
                    primary[top] = primary.TryGetValue(top, out int n) ? n + 1 : 1;
                    foreach (string r in by.Keys) anyReason[r] = anyReason.TryGetValue(r, out int m) ? m + 1 : 1;
                }

                long passedFrames = _mpSteps.PassedOverFrames.Values.Sum();
                Log("city walk attacks, met and passed over: met " + _mpSteps.Encountered.Count + " individuals over " + _mpSteps.EncounterFrames + " NPC-frames (an NPC seen eligible on a frame counts once a frame); attacked "
                    + _mpSteps.Attacked.Count + " individuals; never attacked " + neverAttacked.Count + "; NPC-frames passed over " + passedFrames + " by reason: "
                    + string.Join(", ", _mpSteps.PassedOverFrames.Select(kv => kv.Key + " " + kv.Value + " (" + (passedFrames > 0 ? 100.0 * kv.Value / passedFrames : 0).ToString("F1", Inv) + "%)"))
                    + "; the never attacked by their most frequent reason: " + string.Join(", ", primary.Select(kv => kv.Key + " " + kv.Value))
                    + "; by every reason they met: " + string.Join(", ", anyReason.Select(kv => kv.Key + " " + kv.Value)));
                Log("city walk attacks: met " + _mpSteps.Encountered.Count + " NPCs (in range, in view, unoccluded); attacks begun " + _mpSteps.Engagements.Count + " (struck " + fed + ", target lost " + lost
                    + ", not faced in time " + unfaced + ", other " + other + "); struck with an NPC hit " + struckAndHit + " (the target itself hit in " + targetHit + "), missed " + missed
                    + "; NPC root cuts committed from the attacks " + rootCuts + " (replacements " + replacementCuts + "), NPC pieces hit " + pieceHits + ", props or buildings hit " + propOrBuildingHits
                    + "; seconds attacking " + _mpSteps.EngagedSeconds.ToString("F1", Inv) + " of " + _mpSteps.Engage.totalSeconds.ToString("R", Inv) + " (rule: range " + _mpSteps.Engage.range.ToString("R", Inv)
                    + " m, half angle " + _mpSteps.Engage.halfAngle.ToString("R", Inv) + " deg, at most " + _mpSteps.Engage.maxAttacks + " attacks, cooldown " + _mpSteps.Engage.cooldown.ToString("R", Inv) + " s)");
            }

            private void MobPlanStepDrive()
            {
                if (!_replaying || _clockStart <= 0.0 || _mpSteps == null || _mpSteps.Done) return;
                double now = MobPlanNow;
                Transform player = _mpInput.player.transform;
                var at = new Vector2(player.position.x, player.position.z);
                bool idle = !_recorder.IsReplaying && _katana.WaveCount == 0;
                float seconds = Time.unscaledDeltaTime;
                (int start, int rows)? chunk = _mpSteps.Advance(now, at, player.eulerAngles.y, idle, _mpInput.speed, _mpInput.turnDegreesPerSecond, seconds,
                    out (float forward, float turn)? stick, line => { Log("city walk " + line); MobPlanRecord(line); }, _mpSteps.Engage != null ? CityWalkSeen() : null);
                if (_mpSteps.Engage != null) CityWalkEngagementFrames();
                if (stick.HasValue) _mpInput.Submit(stick.Value.forward, stick.Value.turn, seconds);
                if (chunk.HasValue) MobPlanBeginChunk((now, chunk.Value.start, chunk.Value.rows));
                if (_mpSteps.HoldBeforeSlash != null) CityWalkHitShotsAfter(now);
                if (_mpSteps.Done && _mpEnd > now) _mpEnd = now;
            }

            private void MobPlanAdded(int id, SandboxNpcCharacter c, bool replacement)
            {
                string name = (replacement ? "r" : "a") + id.ToString(Inv);
                if (!_mpSlotIndividuals.TryGetValue(c, out List<string> carried)) _mpSlotIndividuals[c] = carried = new List<string>();
                if (carried.Count > 0)
                {
                    // A reused slot: the previous individual's cut must have been published and committed before now.
                    string previous = carried[carried.Count - 1];
                    Accepted root = _accepted.FirstOrDefault(a => !a.child && LineageOf(a.fragment) == "npc-" + previous);
                    string state = root == null ? "no accepted cut" : "op " + root.operation.value + " committed@" + root.committedFrame;
                    _mpReuses.Add(c.CharacterRoot.name + ": " + previous + " -> " + name + " at frame " + Time.frameCount + " (" + state + ")");
                    // Or its cut failed and was aborted before now (counted by the driver): the slot came back from that.
                    bool abortedBefore = root != null && FailedBefore(root.operation, Time.frameCount);
                    if (abortedBefore) _mpReuses[_mpReuses.Count - 1] += " (its cut failed and was aborted)";
                    if (!abortedBefore && (root == null || root.committedFrame < 0 || root.committedFrame >= Time.frameCount))
                    {
                        _mpReuseViolations.Add(c.CharacterRoot.name + " reused for " + name + " at frame " + Time.frameCount + " while " + previous + "'s cut was " + state);
                    }
                }

                MobPlanModelsAdded(name, c, carried.Count > 0);
                MobPlanReuseNoteAdded(name, c, id);
                carried.Add(name);
                s_mobPlanNames[c] = name;
                _mpActorOf[c] = (id, replacement, Time.frameCount);
                if (!_npcs.Contains(c))
                {
                    _npcs.Add(c);
                }

                if (_mpRetired.ContainsKey(id))
                {
                    _mpViolations.Add("id " + id + " taken in again at frame " + Time.frameCount + " after its retirement at frame " + _mpRetired[id].frame);
                }

                Vector3 p = c.CharacterRoot != null ? c.CharacterRoot.transform.position : Vector3.zero;
                MobPlanEvent(replacement ? "replacement" : "added", id, name, "position=" + p.ToString("F2").Replace(",", " ")
                    + " distance=" + (_mpInput != null ? Vector3.Distance(p, _mpInput.player.transform.position).ToString("F2", Inv) : ""));
            }

            private void MobPlanRetired(int id, SandboxNpcCharacter c)
            {
                _mpRetired[id] = (c, Time.frameCount);
                string name = c != null && s_mobPlanNames.TryGetValue(c, out string n) ? n : "?";

                // Its lineage root is named here too: the per-frame naming skips a retired individual, and one retired in
                // the frame it was hit would otherwise never be named -- its slot's next handle has another root.
                if (c != null && c.Handle != null && c.Handle.Source.IsSet && !_mpRootNames.ContainsKey(c.Handle.Source))
                {
                    _mpRootNames[c.Handle.Source] = name;
                }

                if (_crowd.TryEvaluate(id, Time.timeAsDouble, out PoseTable planTable, out double planSource, out _))
                {
                    _mpPlanAtRetirement[id] = (Time.frameCount, planSource, planTable != null ? planTable.ClipName : "none");
                }

                bool withdrawn = c != null && c.Handle != null && c.Handle.IsWithdrawn;
                MobPlanEvent("retired", id, name, "withdrawn=" + withdrawn + " live=" + (_crowd != null ? _crowd.LiveCount : -1));
            }

            private void MobPlanEvent(string what, int id, string name, string detail)
            {
                double t = _clockStart > 0.0 ? MobPlanNow : -1.0;
                _mpEvents?.WriteLine(string.Join(",", Time.frameCount, t.ToString("F4", Inv), what, id, name, detail));
                MobPlanRecord("event " + what + " frame=" + Time.frameCount + " id=" + id + " name=" + name + " " + detail);
                Log("mobplan " + what + ": id=" + id + " name=" + name + " frame=" + Time.frameCount + " t=" + t.ToString("F3", Inv) + " " + detail);
            }

            private string MobPlanLineageOf(LogicalFragmentId root)
            {
                if (_mpRootNames.TryGetValue(root, out string named))
                {
                    return "npc-" + named;
                }

                // A placed cuttable's root, by the names made once at the replay's start (a city holds hundreds of them).
                if (_pcNames.TryGetValue(root, out string placed))
                {
                    return placed;
                }

                foreach (SandboxNpcCharacter c in _npcs)
                {
                    if (c != null && c.Handle != null && c.Handle.Source.IsSet && root == c.Handle.Source)
                    {
                        return "npc-" + Model(c);
                    }
                }

                return PlayableCityRootName(root) ?? (_probe != null && _probe.Body.IsSet && root == _probe.Body ? "box" : "other");
            }

            // At the replay's start: what stands where, and what is under it.
            private void MobPlanBegin()
            {
                _mpLiveAtBegin = _crowd.LiveCount;
                _mpPublishedAtBegin = _crowd.PublishedCycles;
                _mpStaleAtBegin = _crowd.StaleCycles;
                _mpReplacementsAtBegin = _crowd.ReplacementsAdded;
                PlayableCityBegin();
                _mpPreviousPlayer = _mpInput.player.transform.position;
                HitLogOpen("mobPlan");
                FrameLogOpen("mobPlan");   // the per-frame rows (formerly multi.csv), a recording of their own
                _mpFrames = new StreamWriter(Path.Combine(directory, "mobplan-frames.csv"));
                _mpFrames.WriteLine("frame,t,delta,live,busy,published,stale,replacements,playerX,playerZ,playerYaw,waves,replaying,chunk,fed,lodRegistered,slots,free,returning,preparing,waitedForSlot");
                _mpSlotsAtBegin = _crowd.SlotCount;
                _mpFullPreparationsAtBegin = SandboxNpcCharacter.FullPreparations;
                _mpDirectMadeAtBegin = Zantetsu.Rendering.VpDirectSkinInput.CreatedCount;
                Log("mobplan pool: slots=" + _crowd.SlotCount + " free=" + _crowd.FreeSlots + " broken=" + _crowd.BrokenSlots
                    + " prepared in " + _crowd.PoolPrepareSeconds.ToString("F3", Inv) + " s, allocated +" + (_crowd.PoolAllocatedBytes / 1048576.0).ToString("F1", Inv)
                    + " MB, mono +" + (_crowd.PoolMonoBytes / 1048576.0).ToString("F1", Inv) + " MB, full preparations " + SandboxNpcCharacter.FullPreparations
                    + ", shared reads " + _crowd.SharedReads + ", models " + _crowd.SlotShareCount);
                _mpActors = new StreamWriter(Path.Combine(directory, "mobplan-actors.csv"));
                _mpActors.WriteLine("frame,t,name,id,replacement,x,z,yaw,distance,target,withdrawn,drawn");
                MultiStorage("before replay");
                _mpDetail = MobPlanDetail;
                Log("mobplan detail files: " + (_mpDetail ? "written (-zantetsuMobPlanDetail)" : "off (a performance run)")
                    + ", flushed line by line: " + MobPlanLive);
                MobPlanLifetimeBegin();
                MobPlanHitRegistryBegin();
                MobPlanEyeViewBegin();
                MobPlanAllocStagesBegin();
                MobPlanCloseupsBegin();
                MobPlanFailuresBegin();
                Vector3 player = _mpInput.player.transform.position;
                Log("mobplan begin: frame=" + Time.frameCount + " live=" + _mpLiveAtBegin + " published=" + _mpPublishedAtBegin
                    + " player=" + player.ToString("F3") + " yaw=" + _mpInput.player.transform.eulerAngles.y.ToString("F1", Inv)
                    + " floor under player=" + FloorUnder(player));
                foreach (SandboxNpcCharacter c in _npcs)
                {
                    if (c == null || c.CharacterRoot == null) continue;
                    Vector3 p = c.CharacterRoot.transform.position;
                    Log("mobplan npc " + Model(c) + ": target=" + c.IsTarget + " failure=" + (c.Failure ?? "none") + " position=" + p.ToString("F2")
                        + " distance=" + Vector3.Distance(p, player).ToString("F2", Inv) + " floor=" + FloorUnder(p)
                        + " lod=" + (c.Lod != null ? "registered" : "none") + " preparationFrames=" + c.PreparationFrames);
                }
            }

            private static string FloorUnder(Vector3 at)
            {
                return Physics.Raycast(at + Vector3.up, Vector3.down, out RaycastHit under, 5f, ~0, QueryTriggerInteraction.Ignore)
                    ? under.collider.name + " y=" + under.point.y.ToString("F3", Inv) : "none";
            }

            // Every frame of the replay: the next chunk when due, the frame row, the NPCs a few times a second, and what the
            // retired characters show.
            // Diagnosis (re-preparation allocation unit): the parts of the check's own frame, on markers of their own.
            private static readonly Unity.Profiling.ProfilerMarker s_mpFrameMarker = new Unity.Profiling.ProfilerMarker("Zantetsu.Check.MobPlanFrame");
            private static readonly Unity.Profiling.ProfilerMarker s_mpDisplayMarker = new Unity.Profiling.ProfilerMarker("Zantetsu.Check.MobPlanDisplay");
            private static readonly Unity.Profiling.ProfilerMarker s_mpLifetimeMarker = new Unity.Profiling.ProfilerMarker("Zantetsu.Check.MobPlanLifetime");
            private static readonly Unity.Profiling.ProfilerMarker s_mpRegistryMarker = new Unity.Profiling.ProfilerMarker("Zantetsu.Check.MobPlanHitRegistry");

            private void MobPlanFrame(int frame)
            {
                long before = s_allocStages != null ? GC.GetTotalMemory(false) : 0;
                int collections = s_allocStages != null ? GC.CollectionCount(0) : 0;
                using (s_mpFrameMarker.Auto())
                {
                    MobPlanFrameObserved(frame);
                }

                s_allocStages?.NoteMobPlanFrame(GC.GetTotalMemory(false) - before, GC.CollectionCount(0) - collections);
            }

            private void MobPlanFrameObserved(int frame)
            {
                using (s_mpDisplayMarker.Auto()) MobPlanDisplayFrame(frame);
                PlayableCityFrame(frame);
                double t = MobPlanNow;
                if (_mpNextSlash < _mpSlashes.Count && t >= _mpSlashes[_mpNextSlash].at && t < _mpEnd
                    && !_recorder.IsReplaying && _katana.WaveCount == 0)
                {
                    MobPlanBeginChunk(_mpSlashes[_mpNextSlash++]);
                }

                Transform player = _mpInput.player.transform;
                _mpTravel += Vector3.Distance(_mpPreviousPlayer, player.position);
                _mpPreviousPlayer = player.position;
                _mpFrames.WriteLine(string.Join(",", frame, t.ToString("F4", Inv), (Time.unscaledDeltaTime * 1000f).ToString("F3", Inv), _crowd.LiveCount,
                    _crowd.PlannerBusy ? 1 : 0, _crowd.PublishedCycles, _crowd.StaleCycles, _crowd.ReplacementsAdded,
                    player.position.x.ToString("F3", Inv), player.position.z.ToString("F3", Inv), player.eulerAngles.y.ToString("F1", Inv),
                    _katana.WaveCount, _recorder.IsReplaying ? 1 : 0, _mpChunk, _recorder.ReplayIndex, _mpLod != null ? _mpLod.CharacterCount : -1,
                    _crowd.SlotCount, _crowd.FreeSlots, _crowd.ReturningSlots, _crowd.PreparingSlots, _crowd.WaitedForSlot));
                using (s_mpLifetimeMarker.Auto()) MobPlanLifetimeFrame(frame, t);
                _mpFreeMin = Math.Min(_mpFreeMin, _crowd.FreeSlots);
                _mpDetectorMax = Math.Max(_mpDetectorMax, _detector.CharacterCount);
                _mpReturningMax = Math.Max(_mpReturningMax, _crowd.ReturningSlots);
                _mpPreparingMax = Math.Max(_mpPreparingMax, _crowd.PreparingSlots);
                foreach (KeyValuePair<SandboxNpcCharacter, (int id, bool replacement, int addedFrame)> a in _mpActorOf)
                {
                    SandboxNpcCharacter c = a.Key;
                    if (c != null && c.Handle != null && c.Handle.Source.IsSet && !_mpRootNames.ContainsKey(c.Handle.Source)
                        && !_mpRetired.ContainsKey(a.Value.id))
                    {
                        _mpRootNames[c.Handle.Source] = Model(c);
                    }
                }
                if (_mpLod != null)
                {
                    int registered = _mpLod.CharacterCount;
                    _mpLodMax = Math.Max(_mpLodMax, registered);
                    _mpLodExcessMax = Math.Max(_mpLodExcessMax, registered - _crowd.LiveCount);
                    _mpLodShortMax = Math.Max(_mpLodShortMax, _crowd.LiveCount - registered);
                }

                bool rows = t >= _mpNextActorRow;
                if (rows) _mpNextActorRow = t + 0.25;
                foreach (KeyValuePair<SandboxNpcCharacter, (int id, bool replacement, int addedFrame)> a in _mpActorOf)
                {
                    SandboxNpcCharacter c = a.Key;
                    if (c == null || c.CharacterRoot == null) continue;
                    bool retired = _mpRetired.ContainsKey(a.Value.id);
                    bool drawn = c.Renderer != null && c.Renderer.enabled && c.Renderer.gameObject.activeInHierarchy;
                    // A hit target: a candidate of the hit detector whose handle takes hits now. A dormant slot's new handle
                    // is not a candidate until the slot is activated.
                    bool hitTarget = c.Handle != null && !c.Handle.IsDisposed && c.Handle.IsHitTarget && _detector.HasCharacter(c.Handle);
                    if (retired && (drawn || hitTarget) && !_mpRetiredDrawn.ContainsKey(c))
                    {
                        _mpRetiredDrawn[c] = "frame " + frame + " drawn=" + drawn + " hitTarget=" + hitTarget;
                        _mpViolations.Add(Model(c) + " drawn or a hit target after its retirement: " + _mpRetiredDrawn[c]);
                    }

                    Vector3 p = c.CharacterRoot.transform.position;
                    float d = Vector3.Distance(new Vector3(p.x, 0f, p.z), new Vector3(player.position.x, 0f, player.position.z));
                    if (!retired && drawn && d < _mpMinDistance)
                    {
                        _mpMinDistance = d;
                        _mpMinDistanceAt = Model(c) + " frame " + frame;
                    }

                    if (rows)
                    {
                        _mpActors.WriteLine(string.Join(",", frame, t.ToString("F3", Inv), Model(c), a.Value.id, a.Value.replacement ? 1 : 0,
                            p.x.ToString("F3", Inv), p.z.ToString("F3", Inv), c.CharacterRoot.transform.eulerAngles.y.ToString("F1", Inv),
                            d.ToString("F3", Inv), c.IsTarget ? 1 : 0, retired ? 1 : 0, drawn ? 1 : 0));
                    }
                }

                using (s_mpRegistryMarker.Auto()) MobPlanHitRegistryFrame(frame);
                MobPlanModelsFrame(frame);
                MobPlanCloseupsFrame(frame);
                MobPlanLevelEye();
            }

            // One chunk of the saved grip rows, through the recorder's own recording and replay (the katana's entrance).
            private void MobPlanBeginChunk((double at, int start, int rows) chunk)
            {
                _mpChunk++;
                _recorder.BeginRecording();
                _rowTimes.Clear();
                _rowOf.Clear();
                var refused = new List<int>();
                int loaded = 0;
                for (int i = chunk.start + 1; i < _mpInputLines.Length && loaded < Math.Min(chunk.rows, SandboxSlashPoseRecorder.Capacity); i++, loaded++)
                {
                    string[] c = _mpInputLines[i].Split(',');
                    float F(int k) => float.Parse(c[k], Inv);
                    double time = double.Parse(c[2], Inv);
                    var sample = new BladePoseSample(long.Parse(c[1], Inv), time, new Vector3(F(3), F(4), F(5)),
                        new Quaternion(F(6), F(7), F(8), F(9)), (BladeTrackingState)int.Parse(c[10], Inv));
                    if (!_recorder.TryAppendRecordedSample(sample, new Vector3(F(13), F(14), F(15))))
                    {
                        refused.Add(chunk.start + loaded);
                        continue;
                    }

                    _rowTimes.Add(time);
                    _rowOf.Add(chunk.start + loaded);
                }

                bool begun = _recorder.TryBeginReplay(Time.unscaledTimeAsDouble);
                Transform player = _mpInput.player.transform;
                MobPlanEvent("chunk", _mpChunk, "rows " + chunk.start + "+" + loaded, "begun=" + begun + " refused=[" + string.Join(" ", refused) + "] player="
                    + player.position.ToString("F2").Replace(",", " ") + " yaw=" + player.eulerAngles.y.ToString("F1", Inv) + " live=" + _crowd.LiveCount);
            }

            // A hit on an NPC: the pose its current shape was applied with this frame, beside what its plan gives now.
            private void MobPlanOnHit(in SlashHitConfirmed hit, int frame)
            {
                string of = LineageOf(hit.Fragment);
                SandboxNpcCharacter c = _npcs.FirstOrDefault(x => x != null && "npc-" + Model(x) == of);
                if (c == null || !_mpActorOf.TryGetValue(c, out (int id, bool replacement, int addedFrame) a)) return;
                if (!_world.Ledger.TryGetOrigin(hit.Fragment, out _, out _))
                {
                    MobPlanTrackHit(hit, c, frame);
                    MobPlanHitRegistryAtHit(c, frame);
                    MobPlanCloseupsHit(c, frame);
                }
                PoseTablePlayer pose = c.CharacterRoot != null ? c.CharacterRoot.GetComponent<PoseTablePlayer>() : null;
                bool planned = _crowd.TryEvaluate(a.id, Time.timeAsDouble, out PoseTable table, out double source, out Pose root);
                string plannedClip = planned && table != null ? table.ClipName : "none";

                // Retired by the crowd earlier in this same frame: what the plan gave it then, at this same time, is the
                // plan's pose at this hit.
                bool planFromRetirement = !planned && _mpPlanAtRetirement.TryGetValue(a.id, out (int frame, double source, string clip) atRetirement)
                                          && atRetirement.frame == Time.frameCount;
                if (planFromRetirement)
                {
                    (_, source, plannedClip) = _mpPlanAtRetirement[a.id];
                    planned = true;
                }

                // Only a hit on the character itself (its lineage root) meets its current pose; a hit on a piece after its
                // cut meets that piece, and the character's pose is no longer applied.
                bool child = _world.Ledger.TryGetOrigin(hit.Fragment, out _, out _);
                if (pose != null && !child)
                {
                    _mpHitPoses.Add((Model(c), frame, pose.AppliedFrame, pose.AppliedSourceTime, planned ? source : double.NaN,
                        pose.Table != null ? pose.Table.ClipName : "none", plannedClip));
                }

                MobPlanEvent("hit", a.id, Model(c), "slash=" + hit.SlashId + " fragment=" + hit.Fragment.value + " acceptance=" + hit.Acceptance
                    + " child=" + child
                    + (pose != null ? " applied=(frame " + pose.AppliedFrame + " source " + pose.AppliedSourceTime.ToString("F4", Inv) + " " + (pose.Table != null ? pose.Table.ClipName : "none") + ")" : "")
                    + (planFromRetirement
                        ? " plan at its retirement this frame=(source " + source.ToString("F4", Inv) + " " + plannedClip + ")"
                        : " plan now=" + (planned ? "(source " + source.ToString("F4", Inv) + " " + plannedClip + " root "
                            + root.position.ToString("F3").Replace(",", " ") + ")" : "none (retired or out of plan)"))
                    + " root drawn at " + (c.CharacterRoot != null ? c.CharacterRoot.transform.position.ToString("F3").Replace(",", " ") : ""));
            }

            // Whether the scripted run is over: the script's end passed, every chunk fed through, no wave, every cut committed.
            private bool MobPlanFinished()
            {
                return MobPlanNow >= _mpEnd && !_recorder.IsReplaying && _katana.WaveCount == 0 && CutsSettled();
            }

            private void MobPlanClose()
            {
                _mpFrames?.Dispose();
                _mpFrames = null;
                MobPlanLifetimeClose();
                MobPlanHitRegistryClose();
                MobPlanEyeViewClose();
                MobPlanCloseupsClose();
                MobPlanFailuresClose();
                _mpActors?.Dispose();
                _mpActors = null;
                _mpEvents?.Dispose();
                _mpEvents = null;
                _mpDisplayRows?.Dispose();
                _mpDisplayRows = null;
                _mpRecord?.Dispose();
                _mpRecord = null;
                if (_crowd != null)
                {
                    _crowd.ActorAdded -= MobPlanAdded;
                    _crowd.ActorRetired -= MobPlanRetired;
                }

                if (_mpStick != null)
                {
                    _mpStick.command = null;
                    _mpStick.drive = null;
                }
            }

            // The sequence's judgements, the plan cycles, and every Slash with what it met.
            private void MobPlanSummarise()
            {
                using (var cycles = new StreamWriter(Path.Combine(directory, "mobplan-cycles.csv")))
                {
                    cycles.WriteLine("queuedFrame,collectedFrame,queueMs,computeMs,collectMs,outcome,replacements");
                    foreach (MobPlanCrowd.CycleTiming c in _crowd.Timings)
                    {
                        cycles.WriteLine(string.Join(",", c.queuedFrame, c.collectedFrame, c.queue.ToString("F3", Inv), c.compute.ToString("F3", Inv),
                            c.collect.ToString("F3", Inv), c.outcome, c.replacements));
                    }
                }

                Guarded("mobplan display summary", MobPlanDisplaySummary);
                Guarded("playable city summary", PlayableCitySummary);
                Guarded("city walk summary", CityWalkSummary);   // the city walk only: what was cut by name, the re-cuts, the walk, the stages' deadlines
                if (PlayableCity && _world != null && _world.Fusion != null)
                {
                    Guarded("building fusion summary", BuildingFusionSummarise);   // the coexistence run's fusion: its phases, Main, aggregation and hits
                }

                Guarded("capture camera and the game's view", CaptureClose);   // the game's view untouched; an image run's capture camera given back before the world ends
                Guarded("hull required sections", HullRequiredJudge);   // judged whatever the run found (the scenario's own decision)
                if (PlayableCity && _world != null && _world.Hulls != null)
                {
                    Guarded("building hull summary", BuildingHullSummarise);   // the coexistence run's always-kinematic building: bodies, hull updates, display, hits
                    BuildingHullClose();
                }
                var misses = new List<long>();
                foreach (KeyValuePair<long, SlashTally> s in _multiSlashes)
                {
                    Log("mobplan slash " + s.Key + ": latched@" + s.Value.latchFrame + " hits=" + s.Value.hits + " met=[" + string.Join(" ", s.Value.met)
                        + "] rootsAccepted=[" + string.Join(" ", s.Value.roots) + "] childHits=" + s.Value.childHits + " childAccepted=" + s.Value.childAccepted
                        + " published=" + s.Value.published + " pending=" + s.Value.pending + " held=" + s.Value.held + " notAccepted=" + s.Value.notAccepted
                        + " emptySide=" + s.Value.emptySide);
                    if (s.Value.latchFrame >= 0 && s.Value.hits == 0) misses.Add(s.Key);
                }

                foreach ((string name, int frame, int appliedFrame, double applied, double planned, string clip, string plannedClip) h in _mpHitPoses)
                {
                    Log("mobplan hit pose " + h.name + " frame " + h.frame + ": applied at frame " + h.appliedFrame + " source " + h.applied.ToString("F4", Inv) + " " + h.clip
                        + "; plan at the frame's time: source " + h.planned.ToString("F4", Inv) + " " + h.plannedClip);
                }

                bool IsNpc(Accepted a) => a.name.Contains("-npc-");
                string NameOfOp(Accepted a) => LineageOf(a.fragment);
                List<Accepted> npcRoots = _accepted.Where(a => IsNpc(a) && !a.child).ToList();
                List<Accepted> npcChildren = _accepted.Where(a => IsNpc(a) && a.child).ToList();
                var cutCharacters = new List<SandboxNpcCharacter>();
                foreach (Accepted a in npcRoots)
                {
                    string of = NameOfOp(a);
                    SandboxNpcCharacter c = _npcs.FirstOrDefault(x => x != null && "npc-" + Model(x) == of);
                    if (c != null) cutCharacters.Add(c);
                }

                int published = _crowd.PublishedCycles - _mpPublishedAtBegin;
                double seconds = MobPlanNow;
                Log("mobplan summary: seconds=" + seconds.ToString("F2", Inv) + " liveAtBegin=" + _mpLiveAtBegin + " liveAtEnd=" + _crowd.LiveCount
                    + " cyclesPublished=" + published + " stale=" + (_crowd.StaleCycles - _mpStaleAtBegin) + " failed=" + _crowd.FailedCycles
                    + " replacements=" + (_crowd.ReplacementsAdded - _mpReplacementsAtBegin) + " playerTravel=" + _mpTravel.ToString("F2", Inv)
                    + " nearestLiveNpc=" + _mpMinDistance.ToString("F2", Inv) + " (" + _mpMinDistanceAt + ") chunks=" + (_mpChunk + 1)
                    + " slashes=" + _multiSlashes.Count + " misses=[" + string.Join(" ", misses) + "] npcRootCuts=" + npcRoots.Count
                    + " npcChildCuts=" + npcChildren.Count + " replayBeforeReady=" + _replayBeforeReady);

                // 1. Twenty planned NPCs, moving while the player moves.
                Expect(!_mpSubscribedLate && _mpLiveAtBegin == 20, "[scenario] twenty NPCs live at the replay's start (" + _mpLiveAtBegin + ")");
                Expect(published >= Math.Max(5, (int)(seconds / 2.0)), "[scenario] plans kept being published during the run (" + published + " in " + seconds.ToString("F1", Inv) + " s)");
                Expect(_mpTravel > 1f, "[scenario] the player moved by the script (" + _mpTravel.ToString("F2", Inv) + " m)");
                // 2. A real Slash cut an NPC, at the pose its hit met.
                Expect(npcRoots.Any(a => a.committedFrame >= 0), "[scenario] an NPC was cut by a real hit and its geometry committed (" + npcRoots.Count + " root cuts)");
                Expect(_mpHitPoses.Count > 0 && _mpHitPoses.All(h => h.appliedFrame == h.frame && h.applied == h.planned && h.clip == h.plannedClip), "[scenario] every hit on an uncut NPC met the pose applied in its own frame, the plan's pose at that time (" + _mpHitPoses.Count + " hits)");
                // 3. The cut NPC left the display, the hits and the plan, and no later plan brought it back.
                foreach (SandboxNpcCharacter c in cutCharacters.Distinct())
                {
                    bool retired = _mpActorOf.TryGetValue(c, out (int id, bool replacement, int addedFrame) a) && _mpRetired.ContainsKey(a.id);
                    Expect(retired, "[scenario] " + Model(c) + " was retired from the crowd after its cut");
                }

                Expect(_mpViolations.Count == 0, "[scenario] no retired NPC was drawn, a hit target, or taken in again: " + string.Join("; ", _mpViolations));
                // 4. A surviving child, after its Final publication, was cut again by another Slash to its commit.
                Expect(npcChildren.Any(a => a.committedFrame >= 0), "[scenario] a child of an NPC was re-cut by another Slash and its geometry committed (" + npcChildren.Count + ")");
                // 5. A replacement came in by the crowd's own rule and was itself cut.
                Expect(_crowd.ReplacementsAdded - _mpReplacementsAtBegin >= 1, "[scenario] the crowd added a replacement NPC");
                Expect(npcRoots.Any(a => a.committedFrame >= 0 && NameOfOp(a).StartsWith("npc-r", StringComparison.Ordinal)),
                    "[scenario] a replacement NPC was cut by a real hit and its geometry committed");
                // 6. The bone level of detail: a retired NPC leaves it, a replacement joins it.
                if (_mpLod != null)
                {
                    var liveUnregistered = new List<string>();
                    foreach (KeyValuePair<SandboxNpcCharacter, (int id, bool replacement, int addedFrame)> a in _mpActorOf)
                    {
                        if (a.Key != null && !_mpRetired.ContainsKey(a.Value.id) && a.Key.IsTarget && a.Key.Lod == null) liveUnregistered.Add(Model(a.Key));
                    }

                    int retiredStillRegistered = _mpActorOf.Count(a => a.Key != null && _mpRetired.ContainsKey(a.Value.id) && a.Key.Lod != null);
                    Log("mobplan lod: registered at the end=" + _mpLod.CharacterCount + " live=" + _crowd.LiveCount + " max registered=" + _mpLodMax
                        + " max over live=" + _mpLodExcessMax + " max under live=" + _mpLodShortMax + " retired still registered=" + retiredStillRegistered
                        + " live targets not registered=[" + string.Join(" ", liveUnregistered) + "]");
                    Expect(retiredStillRegistered == 0, "[scenario] every retired NPC left the bone level of detail (" + retiredStillRegistered + " still registered)");
                    Expect(liveUnregistered.Count == 0, "[scenario] every live prepared NPC, replacements included, is registered with the bone level of detail");
                    Expect(_mpLodMax <= 20 + 2, "[scenario] the registrations did not grow with the retirements (max " + _mpLodMax + ")");
                }

                // 7. The prepared slots: a fixed set, reused only once released, nothing prepared in full after the start.
                var reusedCut = _accepted.Where(a => !a.child && a.committedFrame >= 0).Select(a => LineageOf(a.fragment))
                    .Where(of => _mpSlotIndividuals.Values.Any(list => list.Count > 1 && list.Skip(1).Any(n => "npc-" + n == of))).Distinct().ToList();
                Log("mobplan pool at the end: slots=" + _crowd.SlotCount + " (at the start " + _mpSlotsAtBegin + ") free=" + _crowd.FreeSlots + " returning=" + _crowd.ReturningSlots
                    + " preparing=" + _crowd.PreparingSlots + " broken=" + _crowd.BrokenSlots + " min free=" + _mpFreeMin + " max returning=" + _mpReturningMax
                    + " max preparing=" + _mpPreparingMax + " reused activations=" + _crowd.ReusedActivations + " waited for a slot=" + _crowd.WaitedForSlot
                    + " full preparations since the start=" + (SandboxNpcCharacter.FullPreparations - _mpFullPreparationsAtBegin)
                    + " reused individuals cut=[" + string.Join(" ", reusedCut) + "]");
                foreach (string reuse in _mpReuses) Log("mobplan reuse: " + reuse);
                MobPlanLifetimeEnd();
                Log("mobplan hit detector: candidates at the end=" + _detector.CharacterCount + " max=" + _mpDetectorMax);
                MobPlanHitRegistryEnd();
                MobPlanModelsEnd();
                MobPlanEyeViewEnd();
                MobPlanAllocStagesEnd();
                Expect(_mpDetectorMax <= _mpSlotsAtBegin, "[scenario] the hit detector's candidates did not grow past the slots (max " + _mpDetectorMax + ")");
                Expect(_crowd.SlotCount == _mpSlotsAtBegin, "[scenario] the slots stayed a fixed set (" + _mpSlotsAtBegin + " -> " + _crowd.SlotCount + ")");
                Expect(SandboxNpcCharacter.FullPreparations == _mpFullPreparationsAtBegin, "[scenario] nothing was prepared in full after the start (every replacement took a prepared slot)");
                Expect(_crowd.BrokenSlots == 0, "[scenario] no slot failed to prepare");
                int slotDirect = _crowd.Slots.Sum(s => s != null ? s.DirectCreations : 0);
                int directSinceBegin = Zantetsu.Rendering.VpDirectSkinInput.CreatedCount - _mpDirectMadeAtBegin;
                Log("mobplan direct skin: made by the slots=" + slotDirect + " (slots " + _crowd.SlotCount + "), made since the start=" + directSinceBegin
                    + ", slots prepared again=" + _crowd.Slots.Sum(s => s != null ? s.Reprepared : 0));
                Expect(slotDirect == _crowd.SlotCount && directSinceBegin == 0,
                    "[scenario] each slot made its direct skin input once, before the start, and none was made again (" + slotDirect + ", " + directSinceBegin + ")");
                // A released slot taken again (TL, 2026-10-03): required of the MobPlan scenarios, not of a city walk, whose cuts may
                // leave never-used slots enough for every replacement; there a reuse is counted, and none is "not exercised". What
                // a reuse must keep (no slot reused before its previous individual's cut was committed, below) is judged everywhere.
                string reuseWhat = "[scenario] a released slot was activated again for a new individual (" + _crowd.ReusedActivations + ")";
                switch (CheckJudgement.SlotReuse(cityWalk, _crowd.ReusedActivations))
                {
                    case CheckJudgement.Kind.Required:
                        Expect(_crowd.ReusedActivations >= 1, reuseWhat);
                        break;
                    case CheckJudgement.Kind.Counted:
                        Log("city walk: " + reuseWhat + " (counted, not required of a city walk)");
                        break;
                    default:
                        NotExercised(reuseWhat + ": a city walk does not require it; no released slot was taken again in this run");
                        break;
                }
                // The script's natural hits on reused individuals, counted as before but not judged (2026-10-01): the script does
                // not guarantee such a hit. The reused-slot check after the script exercises it (its own lines, [reuse check: ...]).
                Log("mobplan reuse (natural, the script's own hits): individuals on a reused slot cut by a real hit and their geometry committed " + reusedCut.Count
                    + " [" + string.Join(" ", reusedCut) + "] (not judged; the reused-slot check exercises it with a synthetic Slash through the ordinary detector)");
                Expect(_mpReuseViolations.Count == 0, "[scenario] no slot was reused before its previous individual's cut was published and committed: " + string.Join("; ", _mpReuseViolations));
                Log("mobplan input gate: connectivity kept=" + _world.CutInputConnectivity.Count + " verified=" + _world.CutInputConnectivity.Kept
                    + " reused=" + _world.CutInputConnectivity.Reused);
                if (_replayBeforeReady)
                {
                    Log("[condition] the VRS replay had begun before the check was ready: not a comparable run");
                }
            }
        }
    }
}
