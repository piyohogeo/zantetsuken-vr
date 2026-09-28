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

            private void Update()
            {
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
                foreach (string raw in MobPlanLive ? new string[0] : File.ReadAllLines(mobPlan))
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

                _mpSubscribedLate = _crowd.IsReady;
                _crowd.ActorAdded += MobPlanAdded;
                _crowd.ActorRetired += MobPlanRetired;
                if (!MobPlanLive)
                {
                    _mpInput.liveInput = false;
                    _mpStick = gameObject.AddComponent<MobPlanScriptedStick>();
                    _mpStick.input = _mpInput;
                    _mpStick.command = MobPlanCommand;
                }

                _mpEvents = new StreamWriter(Path.Combine(directory, "mobplan-events.csv")) { AutoFlush = MobPlanLive };
                _mpEvents.WriteLine("frame,t,event,id,name,detail");
                Log("mobplan: script moves=" + _mpMoves.Count + " slashes=" + _mpSlashes.Count + " end=" + _mpEnd.ToString("R", Inv)
                    + " input=" + input + " (" + _mpInputLines.Length + " lines); live stick off, scripted stick at order -100"
                    + (_mpSubscribedLate ? "; WARNING: the crowd was ready before the check subscribed" : ""));
                float until = Time.realtimeSinceStartup + 120f;
                while (!_crowd.IsReady && Time.realtimeSinceStartup < until)
                {
                    yield return null;
                }

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
                    if (root == null || root.committedFrame < 0 || root.committedFrame >= Time.frameCount)
                    {
                        _mpReuseViolations.Add(c.CharacterRoot.name + " reused for " + name + " at frame " + Time.frameCount + " while " + previous + "'s cut was " + state);
                    }
                }

                MobPlanModelsAdded(name, c, carried.Count > 0);
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

                foreach (SandboxNpcCharacter c in _npcs)
                {
                    if (c != null && c.Handle != null && c.Handle.Source.IsSet && root == c.Handle.Source)
                    {
                        return "npc-" + Model(c);
                    }
                }

                return _probe != null && _probe.Body.IsSet && root == _probe.Body ? "box" : "other";
            }

            // At the replay's start: what stands where, and what is under it.
            private void MobPlanBegin()
            {
                _mpLiveAtBegin = _crowd.LiveCount;
                _mpPublishedAtBegin = _crowd.PublishedCycles;
                _mpStaleAtBegin = _crowd.StaleCycles;
                _mpReplacementsAtBegin = _crowd.ReplacementsAdded;
                _mpPreviousPlayer = _mpInput.player.transform.position;
                _multiRows = new StreamWriter(Path.Combine(directory, "multi.csv"));
                _multiHits = new StreamWriter(Path.Combine(directory, "multi-hits.csv")) { AutoFlush = MobPlanLive };
                _multiHits.WriteLine("frame,slashId,of,child,fragment,acceptance,admission,operation");
                _multiRows.WriteLine("frame,real,waves,uncutNpcs,liveFragments,livePieces,liveConvexes,acceptedOps,pendingOps,incompleteOps,acceptedThisFrame,provisionalThisFrame,finalThisFrame,committedThisFrame,unsimulated,stepId");
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
                return MobPlanNow >= _mpEnd && !_recorder.IsReplaying && _katana.WaveCount == 0
                    && _accepted.TrueForAll(a => a.committedFrame >= 0);
            }

            private void MobPlanClose()
            {
                _mpFrames?.Dispose();
                _mpFrames = null;
                MobPlanLifetimeClose();
                MobPlanHitRegistryClose();
                MobPlanEyeViewClose();
                MobPlanCloseupsClose();
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

                MobPlanDisplaySummary();
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
                Expect(_crowd.ReusedActivations >= 1, "[scenario] a released slot was activated again for a new individual (" + _crowd.ReusedActivations + ")");
                Expect(reusedCut.Count >= 1, "[scenario] an individual on a reused slot was cut by a real hit and its geometry committed");
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
