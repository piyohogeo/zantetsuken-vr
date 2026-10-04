using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The city walk's script followed by where the player is (TL, 2026-10-03): not a stick held for given seconds but a
    /// sequence of steps, each begun when the one before has ended -- walk to a waypoint, turn to a yaw, begin a Slash -- with
    /// the stick of each frame worked out from the player's position and yaw now (<see cref="Steer"/>) and given through the
    /// ordinary movement input. A waypoint is reached when the player stands within <see cref="ArriveRadius"/> of it, a yaw
    /// when it is within <see cref="FaceTolerance"/>; a Slash begins only once the steps before it have ended (it stands
    /// where the plan put it, facing as it said) and the katana is idle, and ends when its rows are fed through and no wave
    /// flies. A step not ended by its own deadline is unreachable: the walk stops there, said so, and nothing after it runs
    /// -- no Slash from a wrong place. Nothing here moves anything: the caller gives the stick and begins the chunk.
    /// <para>
    /// The script, one step a line ('#' comments; coordinates in the world, metres; yaw in degrees, 0 = +z, 90 = +x):
    /// <c>visit,&lt;label&gt;</c> (a name for the steps after it, for the record), <c>goto,&lt;x&gt;,&lt;z&gt;,&lt;deadline s&gt;</c>,
    /// <c>face,&lt;yaw&gt;,&lt;deadline s&gt;</c>, <c>slash,&lt;start row&gt;,&lt;rows&gt;,&lt;deadline s&gt;</c>, <c>end,&lt;budget s&gt;</c>
    /// (the whole script's bound).
    /// </para>
    /// </summary>
    public sealed class CityWalkSteps
    {
        public enum Kind { Goto, Face, Slash }

        public sealed class Step
        {
            public Kind kind;
            public Vector2 target;      // goto: x, z
            public float yaw;           // face
            public int start, rows;     // slash
            public float deadline;      // seconds from the step's beginning
            public string visit;
            public int line;

            // What happened, for the record.
            public double begunAt = double.NaN, endedAt = double.NaN;
            public string outcome;      // "reached", "faced", "fed", "UNREACHABLE ...", "not run"
            public Vector2 endedAtPosition;
            public float endedAtYaw;
        }

        public const float ArriveRadius = 0.25f;
        public const float FaceTolerance = 1f;
        // A waypoint is walked to only while it lies within this of straight ahead; otherwise the player turns in place first.
        public const float WalkWithin = 20f;

        public readonly List<Step> Steps = new List<Step>();
        public float Budget { get; private set; } = 600f;
        public int Current { get; private set; }
        public bool Done => Current >= Steps.Count || Halted;
        public bool Halted { get; private set; }
        public string HaltReason { get; private set; }
        public int ChunksBegun { get; private set; }

        /// <summary>
        /// Asked before a Slash step begins its chunk, with the katana idle: true holds the Slash back this frame (the player
        /// standing as it arrived and faced) -- for a picture before it (2026-10-03). The step's deadline still runs.
        /// </summary>
        public Func<Step, bool> HoldBeforeSlash;
        public int SlashSteps { get; private set; }
        /// <summary>
        /// The script's own walking distance: from where the walk began to its first waypoint, then waypoint to waypoint.
        /// Where it began is the position given at parse until the first <see cref="Advance"/>, then the position that call
        /// was given -- the player as the walk actually starts (TL, 2026-10-03: the check parsed before the player was placed
        /// at its start, and measured the first leg from the origin).
        /// </summary>
        public float PlannedMetres => (_firstGoto.HasValue ? Vector2.Distance(PlannedFrom, _firstGoto.Value) : 0f) + _legsAfterFirst;

        /// <summary>Where <see cref="PlannedMetres"/> is measured from: the parse's start, then the first Advance's position.</summary>
        public Vector2 PlannedFrom { get; private set; }

        private Vector2? _firstGoto;
        private float _legsAfterFirst;
        private bool _advanced;

        private bool _chunkBegun;

        /// <summary>
        /// Attacking the NPCs met on the way (TL, 2026-10-03): the script's "engage" line --
        /// <c>engage,range,halfAngle,faceTolerance,turnSeconds,cooldown,maxAttacks,totalSeconds,nearStand,start,rows</c>. While a
        /// goto step walks (never during a face or a Slash of the plan), with the katana idle, past the cooldown, within the
        /// attack count and the time they may take, before the script's last minute and not within nearStand metres of where
        /// the plan next stands to Slash: the nearest live NPC the caller sees in range and within halfAngle of the heading,
        /// unoccluded. The player stops, turns toward it by the stick alone (no warp, no position change), and when facing
        /// it within faceTolerance with the katana idle begins the chunk (rows from start) through the katana's normal input;
        /// then waits for the katana to be idle again (the stroke ended, no wave flying) and walks on. A target lost (gone,
        /// out of sight or of range) or not faced within turnSeconds is given up; nothing is guaranteed to hit. The time an
        /// attack takes is not counted against the goto step's deadline.
        /// </summary>
        public sealed class EngageRule
        {
            public float range, halfAngle, faceTolerance, turnSeconds, cooldown, totalSeconds, nearStand;
            public int maxAttacks, start, rows;
        }

        /// <summary>An NPC as the caller sees it this frame.</summary>
        public struct Seen
        {
            public int id;
            public Vector2 position;
            public bool visible;
            public bool replacement;
        }

        /// <summary>One attack: its target, when it began, began its chunk and ended, and how it ended.</summary>
        public sealed class Engagement
        {
            public int id;
            public bool replacement;
            public int step;
            public string visit;
            public double begunAt, chunkAt = double.NaN, endedAt = double.NaN;
            public float distance, angle;
            public int chunk = -1;
            public string outcome;
            public int chunkFrame = -1, endFrame = -1;   // the caller's frames, for its own records
        }

        public EngageRule Engage { get; private set; }
        public readonly List<Engagement> Engagements = new List<Engagement>();
        /// <summary>Every NPC seen in range, within the view's half angle and unoccluded, at any time of the walk (by id).</summary>
        public readonly HashSet<int> Encountered = new HashSet<int>();
        public double EngagedSeconds { get; private set; }
        public Engagement Engaging => _engaging;

        private Engagement _engaging;
        private double _nextEngageAt;
        private Vector2?[] _nextStand;

        // Where the plan next stands to face and Slash, from each step: the target of the last goto before a face or a Slash.
        private void PlanStands()
        {
            _nextStand = new Vector2?[Steps.Count];
            Vector2? stand = null;
            for (int i = Steps.Count - 1; i >= 0; i--)
            {
                if (Steps[i].kind == Kind.Goto && i + 1 < Steps.Count && Steps[i + 1].kind != Kind.Goto) stand = Steps[i].target;
                _nextStand[i] = stand;
            }
        }

        private static float Bearing(Vector2 from, Vector2 to) => Mathf.Atan2(to.x - from.x, to.y - from.y) * Mathf.Rad2Deg;

        /// <summary>Frames x NPCs seen eligible (in range, in view, unoccluded): the per-frame count, not individuals (<see cref="Encountered"/>).</summary>
        public long EncounterFrames { get; private set; }
        /// <summary>Why no attack began on a frame with an NPC seen eligible: frames x NPCs by reason.</summary>
        public readonly SortedDictionary<string, long> PassedOverFrames = new SortedDictionary<string, long>(StringComparer.Ordinal);
        /// <summary>For each NPC seen eligible: its frames passed over, by reason (while it was not the one attacked).</summary>
        public readonly Dictionary<int, SortedDictionary<string, long>> PassedOverById = new Dictionary<int, SortedDictionary<string, long>>();
        /// <summary>The NPCs an attack was begun on.</summary>
        public readonly HashSet<int> Attacked = new HashSet<int>();

        private readonly List<int> _eligibleNow = new List<int>();

        /// <summary>The NPCs seen eligible on the latest frame.</summary>
        public int EligibleNow => _eligibleNow.Count;
        private double _notedAt = double.NaN, _passedAt = double.NaN;

        private void NoteEncounters(Vector2 position, float yaw, IReadOnlyList<Seen> npcs)
        {
            _eligibleNow.Clear();
            for (int i = 0; i < npcs.Count; i++)
            {
                Seen n = npcs[i];
                if (!n.visible || Vector2.Distance(position, n.position) > Engage.range || Mathf.Abs(Mathf.DeltaAngle(yaw, Bearing(position, n.position))) > Engage.halfAngle) continue;
                Encountered.Add(n.id);
                _eligibleNow.Add(n.id);
            }

            EncounterFrames += _eligibleNow.Count;
        }

        // The NPCs seen eligible this frame on whom no attack began, with why.
        private void PassOver(string why, int except = int.MinValue)
        {
            if (_passedAt == _notedAt) return;
            _passedAt = _notedAt;
            foreach (int id in _eligibleNow)
            {
                if (id == except) continue;
                PassedOverFrames[why] = PassedOverFrames.TryGetValue(why, out long n) ? n + 1 : 1;
                if (!PassedOverById.TryGetValue(id, out SortedDictionary<string, long> by)) PassedOverById[id] = by = new SortedDictionary<string, long>(StringComparer.Ordinal);
                by[why] = by.TryGetValue(why, out long m) ? m + 1 : 1;
            }
        }

        /// <summary>
        /// What the plan still needs from here, in seconds (an estimate kept on the safe side): the walk to the current
        /// waypoint and every waypoint after it at the walking speed, each planned Slash's rows at 60 a second and 2 s for
        /// its waves, 1.5 s for each face.
        /// </summary>
        public float RemainingPlanSeconds(Vector2 position, float speed)
        {
            float metres = 0f, seconds = 0f;
            Vector2 at = position;
            for (int i = Current; i < Steps.Count; i++)
            {
                Step st = Steps[i];
                if (st.kind == Kind.Goto) { metres += Vector2.Distance(at, st.target); at = st.target; }
                else if (st.kind == Kind.Face) seconds += 1.5f;
                else seconds += st.rows / 60f + 2f;
            }

            return seconds + (speed > 0f ? metres / speed : 0f);
        }

        // One attack at its longest: the turn allowed, the rows at 60 a second, 2 s for its wave.
        private float AttackAtMostSeconds => Engage.turnSeconds + Engage.rows / 60f + 2f;

        // Whether an attack may begin now, and on whom (the nearest eligible), during goto step s.
        private bool TryChooseTarget(double now, Vector2 position, float yaw, bool katanaIdle, float speed, IReadOnlyList<Seen> npcs, out Seen target, out float distance, out float angle, out string why)
        {
            target = default;
            distance = angle = 0f;
            why = null;
            if (_nextStand == null) PlanStands();
            Vector2? stand = _nextStand[Current];
            // Kept on real time (TL, 2026-10-03): an attack begins only while the script's end still leaves the plan's own
            // remaining time, one more attack at its longest and 15 s to spare; the attacks' time is not taken off it.
            if (!katanaIdle) why = "the katana busy";
            else if (now < _nextEngageAt) why = "the cooldown";
            else if (Engagements.Count >= Engage.maxAttacks) why = "the attack count spent";
            else if (EngagedSeconds >= Engage.totalSeconds) why = "the attack time spent";
            else if (now > Budget - 60f) why = "the script's last 60 s";
            else if (now + RemainingPlanSeconds(position, speed) + AttackAtMostSeconds > Budget - 15f) why = "the plan's remaining time";
            else if (stand.HasValue && Vector2.Distance(position, stand.Value) < Engage.nearStand) why = "within " + Engage.nearStand.ToString("R", CultureInfo.InvariantCulture) + " m of the next planned stand";
            if (why != null) return false;
            bool found = false;
            for (int i = 0; i < npcs.Count; i++)
            {
                Seen n = npcs[i];
                float d = Vector2.Distance(position, n.position);
                float a = Mathf.DeltaAngle(yaw, Bearing(position, n.position));
                if (!n.visible || d > Engage.range || Mathf.Abs(a) > Engage.halfAngle || (found && d >= distance)) continue;
                target = n;
                distance = d;
                angle = a;
                found = true;
            }

            if (!found) why = "none eligible";
            return found;
        }

        private (int start, int rows)? EngageAdvance(double now, Vector2 position, float yaw, bool katanaIdle, float speed, float turnDegreesPerSecond, float seconds,
            IReadOnlyList<Seen> npcs, out (float forward, float turn)? stick, Action<string> record)
        {
            stick = null;
            Engagement e = _engaging;
            double elapsed = now - e.begunAt;
            if (double.IsNaN(e.chunkAt))
            {
                Seen? t = null;
                if (npcs != null) for (int i = 0; i < npcs.Count; i++) if (npcs[i].id == e.id) t = npcs[i];
                if (!t.HasValue || !t.Value.visible || Vector2.Distance(position, t.Value.position) > Engage.range * 1.25f)
                {
                    EndEngagement(now, "lost: " + (!t.HasValue ? "gone" : !t.Value.visible ? "out of sight" : "out of range"), record);
                    return null;
                }

                float bearing = Bearing(position, t.Value.position);
                if (Mathf.Abs(Mathf.DeltaAngle(yaw, bearing)) <= Engage.faceTolerance && katanaIdle)
                {
                    e.chunkAt = now;
                    ChunksBegun++;
                    e.chunk = ChunksBegun;
                    record?.Invoke("attack " + Engagements.Count + " on npc " + e.id + (e.replacement ? " (a replacement)" : "") + ": Slash rows " + Engage.start + "+" + Engage.rows
                        + " begun at " + now.ToString("F2", CultureInfo.InvariantCulture) + " s, " + Vector2.Distance(position, t.Value.position).ToString("F2", CultureInfo.InvariantCulture) + " m away");
                    return (Engage.start, Engage.rows);
                }

                if (elapsed > Engage.turnSeconds)
                {
                    EndEngagement(now, "not faced within " + Engage.turnSeconds.ToString("R", CultureInfo.InvariantCulture) + " s", record);
                    return null;
                }

                stick = Steer(position, yaw, position, speed, turnDegreesPerSecond, seconds, bearing);
                return null;
            }

            if (katanaIdle)
            {
                EndEngagement(now, "fed", record);
                return null;
            }

            if (now - e.chunkAt > Engage.rows / 90f + 10f) EndEngagement(now, "the katana not idle within " + (Engage.rows / 90f + 10f).ToString("F1", CultureInfo.InvariantCulture) + " s of its chunk", record);
            return null;
        }

        private void EndEngagement(double now, string outcome, Action<string> record)
        {
            Engagement e = _engaging;
            e.endedAt = now;
            e.outcome = outcome;
            double took = now - e.begunAt;
            EngagedSeconds += took;
            _nextEngageAt = now + Engage.cooldown;
            if (Current < Steps.Count && !double.IsNaN(Steps[Current].begunAt)) Steps[Current].begunAt += took;   // not against the goto's deadline
            _engaging = null;
            record?.Invoke("attack " + Engagements.Count + " on npc " + e.id + ": " + outcome + " at " + now.ToString("F2", CultureInfo.InvariantCulture) + " s after "
                + took.ToString("F2", CultureInfo.InvariantCulture) + " s");
        }

        public static CityWalkSteps Parse(IEnumerable<string> lines, Vector2 start)
        {
            var steps = new CityWalkSteps { PlannedFrom = start };
            CultureInfo inv = CultureInfo.InvariantCulture;
            string visit = null;
            Vector2 at = start;
            int n = 0;
            foreach (string raw in lines)
            {
                n++;
                string line = raw.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                string[] c = line.Split(',');
                switch (c[0].Trim())
                {
                    case "visit":
                        visit = c[1].Trim();
                        break;
                    case "goto":
                        var target = new Vector2(float.Parse(c[1], inv), float.Parse(c[2], inv));
                        steps.Steps.Add(new Step { kind = Kind.Goto, target = target, deadline = float.Parse(c[3], inv), visit = visit, line = n });
                        if (steps._firstGoto.HasValue) steps._legsAfterFirst += Vector2.Distance(at, target);
                        else steps._firstGoto = target;
                        at = target;
                        break;
                    case "face":
                        steps.Steps.Add(new Step { kind = Kind.Face, yaw = float.Parse(c[1], inv), deadline = float.Parse(c[2], inv), visit = visit, line = n });
                        break;
                    case "slash":
                        steps.Steps.Add(new Step { kind = Kind.Slash, start = int.Parse(c[1], inv), rows = int.Parse(c[2], inv), deadline = float.Parse(c[3], inv), visit = visit, line = n });
                        steps.SlashSteps++;
                        break;
                    case "end":
                        steps.Budget = float.Parse(c[1], inv);
                        break;
                    case "engage":
                        steps.Engage = new EngageRule
                        {
                            range = float.Parse(c[1], inv), halfAngle = float.Parse(c[2], inv), faceTolerance = float.Parse(c[3], inv), turnSeconds = float.Parse(c[4], inv),
                            cooldown = float.Parse(c[5], inv), maxAttacks = int.Parse(c[6], inv), totalSeconds = float.Parse(c[7], inv), nearStand = float.Parse(c[8], inv),
                            start = int.Parse(c[9], inv), rows = int.Parse(c[10], inv),
                        };
                        break;
                    default:
                        throw new FormatException("city walk steps, line " + n + ": " + raw);
                }
            }

            return steps;
        }

        /// <summary>Whether a script holds steps (goto / face) rather than timed stick spans.</summary>
        public static bool IsStepScript(IEnumerable<string> lines)
        {
            foreach (string raw in lines)
            {
                string line = raw.Split('#')[0].Trim();
                if (line.StartsWith("goto,", StringComparison.Ordinal) || line.StartsWith("face,", StringComparison.Ordinal)) return true;
            }

            return false;
        }

        /// <summary>
        /// The stick for one frame towards a waypoint (or, with <paramref name="faceYaw"/>, a yaw): the turn that closes the
        /// heading error this frame without passing it, and -- only while the waypoint lies within <see cref="WalkWithin"/>
        /// of straight ahead -- the forward that closes the distance this frame without passing it.
        /// </summary>
        public static (float forward, float turn) Steer(Vector2 position, float yaw, Vector2 target, float speed, float turnDegreesPerSecond, float seconds, float? faceYaw = null)
        {
            if (!(seconds > 0f)) return (0f, 0f);
            Vector2 to = target - position;
            float distance = to.magnitude;
            float want = faceYaw ?? (distance > 1e-4f ? Mathf.Atan2(to.x, to.y) * Mathf.Rad2Deg : yaw);
            float error = Mathf.DeltaAngle(yaw, want);
            float turn = Mathf.Clamp(error / (turnDegreesPerSecond * seconds), -1f, 1f);
            if (faceYaw.HasValue) return (0f, turn);
            float forward = Mathf.Abs(error) <= WalkWithin ? Mathf.Clamp01(distance / (speed * seconds)) : 0f;
            return (forward, turn);
        }

        /// <summary>
        /// One frame: the current step judged against the player's position and yaw now and the clock (seconds since the
        /// script began), and the stick to give (null: none -- standing, or a Slash). <paramref name="katanaIdle"/>: no chunk
        /// being fed and no wave flying. Returns the chunk to begin this frame, if any.
        /// </summary>
        public (int start, int rows)? Advance(double now, Vector2 position, float yaw, bool katanaIdle, float speed, float turnDegreesPerSecond, float seconds,
            out (float forward, float turn)? stick, Action<string> record = null, IReadOnlyList<Seen> npcs = null)
        {
            stick = null;
            if (!_advanced)
            {
                _advanced = true;
                PlannedFrom = position;
            }

            if (Done) return null;
            if (now > Budget)
            {
                Halt("the script's budget of " + Budget.ToString("R", CultureInfo.InvariantCulture) + " s passed", now, position, yaw, record);
                return null;
            }

            Step s = Steps[Current];
            if (double.IsNaN(s.begunAt))
            {
                s.begunAt = now;
                _chunkBegun = false;
            }

            // counted once a frame (a step ended goes on to the next within the same frame)
            if (Engage != null && npcs != null && now != _notedAt) { NoteEncounters(position, yaw, npcs); _notedAt = now; }
            if (_engaging != null)
            {
                PassOver("during an attack", _engaging.id);
                return EngageAdvance(now, position, yaw, katanaIdle, speed, turnDegreesPerSecond, seconds, npcs, out stick, record);
            }

            if (Engage != null && npcs != null)
            {
                if (s.kind != Kind.Goto)
                {
                    PassOver("a planned face or Slash");
                }
                else if (TryChooseTarget(now, position, yaw, katanaIdle, speed, npcs, out Seen target, out float distance, out float angle, out string why))
                {
                    PassOver("another attacked", target.id);
                    Attacked.Add(target.id);
                    _engaging = new Engagement { id = target.id, replacement = target.replacement, step = Current, visit = s.visit, begunAt = now, distance = distance, angle = angle };
                    Engagements.Add(_engaging);
                    record?.Invoke("attack " + Engagements.Count + " on npc " + target.id + (target.replacement ? " (a replacement)" : "") + " begun at " + now.ToString("F2", CultureInfo.InvariantCulture)
                        + " s during step " + Current + " (" + s.visit + "): " + distance.ToString("F2", CultureInfo.InvariantCulture) + " m, " + angle.ToString("F1", CultureInfo.InvariantCulture) + " deg off the heading");
                    return EngageAdvance(now, position, yaw, katanaIdle, speed, turnDegreesPerSecond, seconds, npcs, out stick, record);
                }
                else
                {
                    PassOver(why);
                }
            }

            float elapsed = (float)(now - s.begunAt);
            switch (s.kind)
            {
                case Kind.Goto:
                    if (Vector2.Distance(position, s.target) <= ArriveRadius)
                    {
                        End(s, "reached", now, position, yaw, record);
                        return Advance(now, position, yaw, katanaIdle, speed, turnDegreesPerSecond, seconds, out stick, record, npcs);
                    }

                    if (elapsed > s.deadline)
                    {
                        Halt("waypoint (" + s.target.x.ToString("F2", CultureInfo.InvariantCulture) + ", " + s.target.y.ToString("F2", CultureInfo.InvariantCulture) + ") not reached within "
                            + s.deadline.ToString("R", CultureInfo.InvariantCulture) + " s: " + Vector2.Distance(position, s.target).ToString("F2", CultureInfo.InvariantCulture) + " m short", now, position, yaw, record);
                        return null;
                    }

                    stick = Steer(position, yaw, s.target, speed, turnDegreesPerSecond, seconds);
                    return null;
                case Kind.Face:
                    if (Mathf.Abs(Mathf.DeltaAngle(yaw, s.yaw)) <= FaceTolerance)
                    {
                        End(s, "faced", now, position, yaw, record);
                        return Advance(now, position, yaw, katanaIdle, speed, turnDegreesPerSecond, seconds, out stick, record, npcs);
                    }

                    if (elapsed > s.deadline)
                    {
                        Halt("yaw " + s.yaw.ToString("F1", CultureInfo.InvariantCulture) + " not faced within " + s.deadline.ToString("R", CultureInfo.InvariantCulture) + " s", now, position, yaw, record);
                        return null;
                    }

                    stick = Steer(position, yaw, position, speed, turnDegreesPerSecond, seconds, s.yaw);
                    return null;
                default:
                    if (!_chunkBegun)
                    {
                        if (katanaIdle && (HoldBeforeSlash == null || !HoldBeforeSlash(s)))
                        {
                            _chunkBegun = true;
                            ChunksBegun++;
                            record?.Invoke("step " + Current + " (" + s.visit + "): Slash rows " + s.start + "+" + s.rows + " begun at " + now.ToString("F2", CultureInfo.InvariantCulture) + " s");
                            return (s.start, s.rows);
                        }
                    }
                    else if (katanaIdle)
                    {
                        End(s, "fed", now, position, yaw, record);
                        return Advance(now, position, yaw, katanaIdle, speed, turnDegreesPerSecond, seconds, out stick, record, npcs);
                    }

                    if (elapsed > s.deadline)
                    {
                        Halt("the Slash (rows " + s.start + "+" + s.rows + ") not " + (_chunkBegun ? "fed through" : "begun") + " within " + s.deadline.ToString("R", CultureInfo.InvariantCulture) + " s", now, position, yaw, record);
                    }

                    return null;
            }
        }

        private void End(Step s, string outcome, double now, Vector2 position, float yaw, Action<string> record)
        {
            s.endedAt = now;
            s.outcome = outcome;
            s.endedAtPosition = position;
            s.endedAtYaw = yaw;
            record?.Invoke("step " + Current + " (" + s.visit + "): " + s.kind + " " + outcome + " at " + now.ToString("F2", CultureInfo.InvariantCulture) + " s after "
                + (now - s.begunAt).ToString("F2", CultureInfo.InvariantCulture) + " s, at (" + position.x.ToString("F2", CultureInfo.InvariantCulture) + ", " + position.y.ToString("F2", CultureInfo.InvariantCulture)
                + ") yaw " + yaw.ToString("F1", CultureInfo.InvariantCulture));
            Current++;
        }

        private void Halt(string why, double now, Vector2 position, float yaw, Action<string> record)
        {
            Halted = true;
            HaltReason = "step " + Current + (Current < Steps.Count ? " (" + Steps[Current].visit + ", script line " + Steps[Current].line + ")" : "") + ": " + why;
            if (Current < Steps.Count)
            {
                Step s = Steps[Current];
                s.endedAt = now;
                s.outcome = "UNREACHABLE: " + why;
                s.endedAtPosition = position;
                s.endedAtYaw = yaw;
            }

            for (int i = Current + 1; i < Steps.Count; i++) Steps[i].outcome = "not run";
            record?.Invoke("city walk steps HALTED: " + HaltReason + "; the " + (Steps.Count - Current - 1) + " steps after it not run");
        }

        /// <summary>Each visit (by its label) and whether every step of it ended as planned.</summary>
        public IEnumerable<(string visit, bool ended, string detail)> Visits()
        {
            var seen = new List<string>();
            foreach (Step s in Steps) if (s.visit != null && !seen.Contains(s.visit)) seen.Add(s.visit);
            foreach (string v in seen)
            {
                List<Step> of = Steps.FindAll(s => s.visit == v);
                bool ended = of.TrueForAll(s => s.outcome == "reached" || s.outcome == "faced" || s.outcome == "fed");
                Step last = of.FindLast(s => !double.IsNaN(s.endedAt));
                yield return (v, ended, ended ? "ended at " + last.endedAt.ToString("F1", CultureInfo.InvariantCulture) + " s"
                    : of.Exists(s => s.outcome != null && s.outcome.StartsWith("UNREACHABLE", StringComparison.Ordinal)) ? of.Find(s => s.outcome != null && s.outcome.StartsWith("UNREACHABLE", StringComparison.Ordinal)).outcome : "not run");
            }
        }
    }
}
