using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The city walk's step script (TL, 2026-10-03): the player steered by where it stands and faces now, as
    /// MobPlanPlayerInput.Submit moves it (the turn first, then the step along the new heading), reaches each waypoint
    /// without passing it at any frame length, turns in place before walking off, begins a Slash only after its waypoint and
    /// yaw are reached and the katana is idle, and stops at an unreachable waypoint -- said so, nothing after it run.
    /// </summary>
    public sealed class CityWalkStepsTests
    {
        private const float Speed = 1.49f, Turn = 120f;

        // MobPlanPlayerInput.Submit's motion, with an optional blocker (the crowd map refusing the move).
        private static void Move(ref Vector2 position, ref float yaw, (float forward, float turn) stick, float seconds, bool blocked = false)
        {
            yaw = Mathf.Repeat(yaw + Mathf.Clamp(stick.turn, -1f, 1f) * Turn * seconds, 360f);
            if (blocked) return;
            float rad = yaw * Mathf.Deg2Rad;
            position += new Vector2(Mathf.Sin(rad), Mathf.Cos(rad)) * (Mathf.Clamp(stick.forward, -1f, 1f) * Speed * seconds);
        }

        private static CityWalkSteps Run(string[] script, float seconds, double limit, out List<string> records, out List<(int start, int rows, Vector2 at, float yaw)> chunks,
            System.Func<double, bool> idle = null, System.Func<Vector2, bool> blocked = null)
        {
            var steps = CityWalkSteps.Parse(script, Vector2.zero);
            var log = new List<string>();
            var begun = new List<(int, int, Vector2, float)>();
            Vector2 at = Vector2.zero;
            float yaw = 0f;
            double now = 0;
            while (!steps.Done && now < limit)
            {
                (int start, int rows)? chunk = steps.Advance(now, at, yaw, idle == null || idle(now), Speed, Turn, seconds, out (float forward, float turn)? stick, log.Add);
                if (chunk.HasValue) begun.Add((chunk.Value.start, chunk.Value.rows, at, yaw));
                if (stick.HasValue) Move(ref at, ref yaw, stick.Value, seconds, blocked != null && blocked(at));
                now += seconds;
            }

            records = log;
            chunks = begun;
            return steps;
        }

        [TestCase(1f / 90f)]
        [TestCase(1f / 30f)]
        [TestCase(0.25f)]
        public void EveryWaypointIsReachedWithoutPassingIt_AtAnyFrameLength(float seconds)
        {
            string[] script = { "visit,a", "goto,0,10,60", "goto,10,10,60", "goto,10,0,60", "face,270,10", "end,600" };
            var steps = CityWalkSteps.Parse(script, Vector2.zero);
            Vector2 at = Vector2.zero;
            float yaw = 0f, closest = float.MaxValue;
            double now = 0;
            int target = 0, walkedWhileOff = 0;
            while (!steps.Done && now < 120)
            {
                steps.Advance(now, at, yaw, true, Speed, Turn, seconds, out (float forward, float turn)? stick);
                if (stick.HasValue)
                {
                    CityWalkSteps.Step s = steps.Steps[steps.Current];
                    if (s.kind == CityWalkSteps.Kind.Goto)
                    {
                        float off = Mathf.Abs(Mathf.DeltaAngle(yaw, Mathf.Atan2(s.target.x - at.x, s.target.y - at.y) * Mathf.Rad2Deg));
                        if (off > CityWalkSteps.WalkWithin + 1e-3f && stick.Value.forward > 0f) walkedWhileOff++;
                    }

                    Move(ref at, ref yaw, stick.Value, seconds);
                }

                if (steps.Current != target) { target = steps.Current; closest = float.MaxValue; }
                now += seconds;
            }

            Assert.That(steps.Halted, Is.False, steps.HaltReason);
            Assert.That(steps.Done, Is.True, "every step ended within the time");
            Assert.That(steps.Steps.Take(3).All(s => s.outcome == "reached"), Is.True);
            foreach (CityWalkSteps.Step s in steps.Steps.Take(3))
            {
                Assert.That(Vector2.Distance(s.endedAtPosition, s.target), Is.LessThanOrEqualTo(CityWalkSteps.ArriveRadius), "reached within the radius");
            }

            Assert.That(Mathf.Abs(Mathf.DeltaAngle(yaw, 270f)), Is.LessThanOrEqualTo(CityWalkSteps.FaceTolerance), "faced");
            Assert.That(walkedWhileOff, Is.Zero, "never walked while the waypoint lay more than " + CityWalkSteps.WalkWithin + " degrees off");
            Assert.That(Vector2.Distance(at, new Vector2(10f, 0f)), Is.LessThanOrEqualTo(CityWalkSteps.ArriveRadius), "stands at the last waypoint");
        }

        [Test]
        public void ASlashBeginsOnlyWhereThePlanPutsIt_FacingAsItSays_AndTheNextLegWaitsForItsRows()
        {
            string[] script = { "visit,tree", "goto,0,5,30", "face,90,10", "slash,2227,512,30", "goto,5,5,30", "end,600" };
            // The katana busy from 0 to 1 s (an earlier chunk), and for 2 s after each chunk begun.
            var busyUntil = new List<double> { 1.0 };
            double lastChunk = -1;
            System.Func<double, bool> idle = t => t >= busyUntil.Max();
            var steps = CityWalkSteps.Parse(script, Vector2.zero);
            Vector2 at = Vector2.zero;
            float yaw = 0f;
            double now = 0;
            const float dt = 1f / 72f;
            var begunAt = new List<(Vector2 at, float yaw, double t)>();
            while (!steps.Done && now < 60)
            {
                (int start, int rows)? chunk = steps.Advance(now, at, yaw, idle(now), Speed, Turn, dt, out (float forward, float turn)? stick);
                if (chunk.HasValue)
                {
                    begunAt.Add((at, yaw, now));
                    busyUntil.Add(now + 2.0);
                    lastChunk = now;
                }

                if (stick.HasValue) Move(ref at, ref yaw, stick.Value, dt);
                now += dt;
            }

            Assert.That(steps.Halted, Is.False, steps.HaltReason);
            Assert.That(begunAt.Count, Is.EqualTo(1), "one Slash");
            Assert.That(Vector2.Distance(begunAt[0].at, new Vector2(0f, 5f)), Is.LessThanOrEqualTo(CityWalkSteps.ArriveRadius), "begun where the plan put it");
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(begunAt[0].yaw, 90f)), Is.LessThanOrEqualTo(CityWalkSteps.FaceTolerance), "facing as it said");
            Assert.That(steps.Steps[3].begunAt, Is.GreaterThanOrEqualTo(lastChunk + 2.0 - 1e-6), "the next leg only once the rows are fed and no wave flies");
            Assert.That(steps.ChunksBegun, Is.EqualTo(1));
            Assert.That(steps.Visits().Single().ended, Is.True);
        }

        [Test]
        public void AHeldSlash_BeginsOnlyOnceReleased_WhereItArrivedAndFaced_AndItsDeadlineStillRuns()
        {
            string[] script = { "visit,tree", "goto,0,5,30", "face,90,10", "slash,2227,512,30", "end,600" };
            // Held for 0.5 s from the first time it is asked (a picture before the Slash), then released.
            double firstAsked = double.NaN, now = 0;
            int asked = 0;
            var steps = CityWalkSteps.Parse(script, Vector2.zero);
            steps.HoldBeforeSlash = s =>
            {
                asked++;
                if (double.IsNaN(firstAsked)) firstAsked = now;
                return now < firstAsked + 0.5;
            };
            Vector2 at = Vector2.zero;
            float yaw = 0f;
            const float dt = 1f / 72f;
            double begun = double.NaN;
            Vector2 heldAt = Vector2.zero;
            while (!steps.Done && now < 60)
            {
                (int start, int rows)? chunk = steps.Advance(now, at, yaw, double.IsNaN(begun) || now > begun + 1.0, Speed, Turn, dt, out (float forward, float turn)? stick);
                if (!double.IsNaN(firstAsked) && double.IsNaN(begun)) Assert.That(stick.HasValue, Is.False, "standing while held");
                if (chunk.HasValue) { begun = now; heldAt = at; }
                if (stick.HasValue) Move(ref at, ref yaw, stick.Value, dt);
                now += dt;
            }

            Assert.That(steps.Halted, Is.False, steps.HaltReason);
            Assert.That(asked, Is.GreaterThan(1), "asked each frame while it held");
            Assert.That(begun, Is.GreaterThanOrEqualTo(firstAsked + 0.5 - 1e-6), "begun only once released");
            Assert.That(Vector2.Distance(heldAt, new Vector2(0f, 5f)), Is.LessThanOrEqualTo(CityWalkSteps.ArriveRadius));
            Assert.That(steps.ChunksBegun, Is.EqualTo(1));

            // Held for good: the step's own deadline stops the walk, said so.
            var forever = CityWalkSteps.Parse(script, Vector2.zero);
            forever.HoldBeforeSlash = s => true;
            at = Vector2.zero; yaw = 0f; now = 0;
            while (!forever.Done && now < 120)
            {
                forever.Advance(now, at, yaw, true, Speed, Turn, dt, out (float forward, float turn)? stick);
                if (stick.HasValue) Move(ref at, ref yaw, stick.Value, dt);
                now += dt;
            }

            Assert.That(forever.Halted, Is.True);
            Assert.That(forever.HaltReason, Does.Contain("not begun within 30"));
            Assert.That(forever.ChunksBegun, Is.Zero);
        }

        [Test]
        public void ThePlannedDistance_IsMeasuredFromWhereTheWalkBegins_NotFromWhereItWasParsed()
        {
            string[] script = { "visit,a", "goto,96.412,-38.519,30", "goto,100,-40,30", "end,600" };
            var first = new Vector2(96.412f, -38.519f);
            float legs = Vector2.Distance(first, new Vector2(100f, -40f));
            // Parsed before the player is placed (at the origin), as the check parses.
            var steps = CityWalkSteps.Parse(script, Vector2.zero);
            Assert.That(steps.PlannedMetres, Is.EqualTo(first.magnitude + legs).Within(1e-3f), "until the walk begins: from the parse's start");
            var start = new Vector2(99f, -32f);
            steps.Advance(0.0, start, 180f, true, Speed, Turn, 1f / 72f, out _);
            Assert.That(steps.PlannedFrom, Is.EqualTo(start));
            Assert.That(steps.PlannedMetres, Is.EqualTo(Vector2.Distance(start, first) + legs).Within(1e-3f), "from where the walk began");
            steps.Advance(0.1, start + new Vector2(0.5f, 0f), 180f, true, Speed, Turn, 1f / 72f, out _);
            Assert.That(steps.PlannedFrom, Is.EqualTo(start), "only the first advance sets it");
            // Parsed at a start other than the origin, the walk beginning there.
            var elsewhere = new Vector2(-50f, 20f);
            var other = CityWalkSteps.Parse(script, elsewhere);
            Assert.That(other.PlannedMetres, Is.EqualTo(Vector2.Distance(elsewhere, first) + legs).Within(1e-3f));
            other.Advance(0.0, elsewhere, 0f, true, Speed, Turn, 1f / 72f, out _);
            Assert.That(other.PlannedMetres, Is.EqualTo(Vector2.Distance(elsewhere, first) + legs).Within(1e-3f));
        }

        // A walk with NPCs given each frame by a function of the time; the katana busy for "busy" seconds after each chunk.
        private static CityWalkSteps Walk(string[] script, System.Func<double, Vector2, float, List<CityWalkSteps.Seen>> npcs, double limit, double busy,
            out List<string> records, out List<(double t, Vector2 at, float yaw, int start, int rows)> chunks, out List<(double t, float forward, float turn)> sticks)
        {
            var steps = CityWalkSteps.Parse(script, Vector2.zero);
            records = new List<string>();
            chunks = new List<(double, Vector2, float, int, int)>();
            sticks = new List<(double, float, float)>();
            Vector2 at = Vector2.zero;
            float yaw = 0f;
            double now = 0, busyUntil = -1;
            const float dt = 1f / 72f;
            while (!steps.Done && now < limit)
            {
                (int start, int rows)? chunk = steps.Advance(now, at, yaw, now >= busyUntil, Speed, Turn, dt, out (float forward, float turn)? stick, records.Add, npcs(now, at, yaw));
                if (chunk.HasValue) { chunks.Add((now, at, yaw, chunk.Value.start, chunk.Value.rows)); busyUntil = now + busy; }
                if (stick.HasValue) { sticks.Add((now, stick.Value.forward, stick.Value.turn)); Move(ref at, ref yaw, stick.Value, dt); }
                now += dt;
            }

            return steps;
        }

        private const string EngageLine = "engage,8,50,6,1.5,1.5,3,30,12,2275,85";

        [Test]
        public void AnNpcMetOnTheWay_IsTurnedToByTheStickAlone_StruckThroughTheKatana_ThenTheWalkGoesOn()
        {
            // Walking north to (0, 60); an NPC standing 3 m to the right of the path at z = 20.
            string[] script = { "visit,far", "goto,0,60,45", "visit,there", "goto,0,80,30", "face,90,10", "slash,2227,512,30", EngageLine, "end,600" };
            var npc = new Vector2(3f, 20f);
            CityWalkSteps steps = Walk(script, (t, at, yaw) => new List<CityWalkSteps.Seen> { new CityWalkSteps.Seen { id = 7, position = npc, visible = true, replacement = true } },
                200, 2.0, out List<string> records, out var chunks, out var sticks);
            TestContext.WriteLine(string.Join("\n", records));
            Assert.That(steps.Halted, Is.False, steps.HaltReason);
            Assert.That(steps.Engagements.Count, Is.GreaterThanOrEqualTo(1));
            CityWalkSteps.Engagement e = steps.Engagements[0];
            Assert.That(e.id, Is.EqualTo(7));
            Assert.That(e.replacement, Is.True, "a replacement is a target as any other");
            Assert.That(e.outcome, Is.EqualTo("fed"));
            Assert.That(e.visit, Is.EqualTo("far"), "begun while walking, not at a planned stand");
            var attack = chunks.First(c => c.start == 2275);
            Assert.That(attack.rows, Is.EqualTo(85));
            float facing = Mathf.Atan2(npc.x - attack.at.x, npc.y - attack.at.y) * Mathf.Rad2Deg;
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(attack.yaw, facing)), Is.LessThanOrEqualTo(6f), "facing it when the chunk began");
            Assert.That(sticks.Where(k => k.t >= e.begunAt && k.t < e.endedAt).All(k => k.forward == 0f), Is.True, "standing while it turned and struck: no step");
            Assert.That(steps.Steps.Where(st => st.kind == CityWalkSteps.Kind.Slash).All(st => st.outcome == "fed"), Is.True, "the planned Slash still done");
            Assert.That(chunks.Count(c => c.start == 2227), Is.EqualTo(1));
            Assert.That(steps.Encountered.Contains(7), Is.True);
        }

        [Test]
        public void NoAttack_DuringAPlannedFaceOrSlash_NorNearWhereThePlanStands_NorOnAnOccludedNpc()
        {
            // An NPC right by the stand at (0, 20): within 12 m of it no attack; the planned face and Slash undisturbed.
            string[] script = { "visit,there", "goto,0,20,40", "face,90,10", "slash,2227,512,30", "goto,0,21,30", EngageLine, "end,600" };
            CityWalkSteps steps = Walk(script, (t, at, yaw) => new List<CityWalkSteps.Seen> { new CityWalkSteps.Seen { id = 1, position = new Vector2(2f, 21f), visible = true } },
                200, 2.0, out List<string> records, out var chunks, out _);
            TestContext.WriteLine(string.Join("\n", records));
            Assert.That(steps.Halted, Is.False, steps.HaltReason);
            Assert.That(steps.Engagements.Where(x => x.step <= 2), Is.Empty, "none on the way into the stand, nor at its face or Slash");
            // An occluded NPC in the open: never attacked, never counted as met.
            string[] open = { "visit,far", "goto,0,60,45", EngageLine, "end,600" };
            CityWalkSteps hidden = Walk(open, (t, at, yaw) => new List<CityWalkSteps.Seen> { new CityWalkSteps.Seen { id = 2, position = new Vector2(2f, 20f), visible = false } },
                200, 2.0, out _, out _, out _);
            Assert.That(hidden.Engagements, Is.Empty);
            Assert.That(hidden.Encountered, Is.Empty);
        }

        [Test]
        public void TheAttacks_KeepToTheirCount_TheirCooldown_AndTheirTime()
        {
            // NPCs all along the way, always 4 m ahead: attacks as often as allowed -- three at most, 1.5 s apart at least.
            string[] script = { "visit,far", "goto,0,200,200", EngageLine, "end,600" };
            CityWalkSteps steps = Walk(script, (t, at, yaw) => new List<CityWalkSteps.Seen> { new CityWalkSteps.Seen { id = (int)(t / 3), position = at + new Vector2(0.5f, 4f), visible = true } },
                400, 2.0, out List<string> records, out var chunks, out _);
            TestContext.WriteLine(string.Join("\n", records));
            Assert.That(steps.Halted, Is.False, steps.HaltReason);
            Assert.That(steps.Engagements.Count, Is.EqualTo(3), "the count");
            for (int i = 1; i < steps.Engagements.Count; i++) Assert.That(steps.Engagements[i].begunAt, Is.GreaterThanOrEqualTo(steps.Engagements[i - 1].endedAt + 1.5 - 1e-6), "the cooldown");
            Assert.That(steps.Steps[0].outcome, Is.EqualTo("reached"), "the goto's deadline not charged with the attacks");
            // The time: a total of 3 s -- once spent, no attack begins.
            string[] shortly = { "visit,far", "goto,0,200,200", "engage,8,50,6,1.5,1.5,30,3,12,2275,85", "end,600" };
            CityWalkSteps timed = Walk(shortly, (t, at, yaw) => new List<CityWalkSteps.Seen> { new CityWalkSteps.Seen { id = (int)(t / 3), position = at + new Vector2(0.5f, 4f), visible = true } },
                400, 2.0, out _, out _, out _);
            TestContext.WriteLine("timed: " + timed.Engagements.Count + " attacks, " + timed.EngagedSeconds.ToString("F2") + " s");
            Assert.That(timed.Engagements.Count, Is.LessThanOrEqualTo(2), "an attack begins only while the total is not spent");
            double beforeLast = timed.Engagements.Take(timed.Engagements.Count - 1).Sum(x => x.endedAt - x.begunAt);
            Assert.That(beforeLast, Is.LessThan(3.0), "the last one began with time left");
        }

        [Test]
        public void ATargetLostOrNotFaced_IsGivenUp_AndTheWalkGoesOn()
        {
            string[] script = { "visit,far", "goto,0,60,45", EngageLine, "end,600" };
            // Seen for a moment, then gone.
            CityWalkSteps lost = Walk(script, (t, at, yaw) => t < 0.1 ? new List<CityWalkSteps.Seen> { new CityWalkSteps.Seen { id = 4, position = new Vector2(4f, 4f), visible = true } } : new List<CityWalkSteps.Seen>(),
                200, 2.0, out _, out _, out _);
            Assert.That(lost.Halted, Is.False, lost.HaltReason);
            Assert.That(lost.Engagements.Count, Is.EqualTo(1));
            Assert.That(lost.Engagements[0].outcome, Does.StartWith("lost"));
            Assert.That(lost.Steps[0].outcome, Is.EqualTo("reached"), "the walk went on");
            // Always 30 deg to the right of where the player faces, however it turns: never faced within 6 deg, given up after 1.5 s.
            CityWalkSteps unfaced = Walk(script, (t, at, yaw) => new List<CityWalkSteps.Seen> { new CityWalkSteps.Seen { id = 5, position = at + 4f * new Vector2(Mathf.Sin((yaw + 30f) * Mathf.Deg2Rad), Mathf.Cos((yaw + 30f) * Mathf.Deg2Rad)), visible = true } },
                200, 2.0, out _, out _, out _);
            Assert.That(unfaced.Halted, Is.False, unfaced.HaltReason);
            Assert.That(unfaced.Engagements.Count, Is.GreaterThan(0));
            Assert.That(unfaced.Engagements.All(e => e.outcome.StartsWith("not faced")), Is.True, string.Join("; ", unfaced.Engagements.Select(e => e.outcome)));
            Assert.That(unfaced.Engagements.All(e => e.endedAt - e.begunAt <= 1.5 + 0.05), Is.True, "given up at its time");
        }

        [Test]
        public void ThePassedOver_AreCountedByReason_PerFrameAndPerIndividual_AndNoAttackLeavesThePlanShortOfItsTime()
        {
            // 100 m to walk (some 67 s) under a script end of 75 s: an attack (up to 1.5 + 85/60 + 2 s) would leave less than
            // 15 s to spare -- passed over for the plan's remaining time, then for the script's last 60 s; never attacked.
            string[] tight = { "visit,far", "goto,0,100,200", EngageLine, "end,75" };
            CityWalkSteps steps = Walk(tight, (t, at, yaw) => new List<CityWalkSteps.Seen> { new CityWalkSteps.Seen { id = 9, position = at + new Vector2(0.5f, 4f), visible = true } },
                200, 2.0, out List<string> records, out var chunks, out _);
            TestContext.WriteLine(string.Join(", ", steps.PassedOverFrames.Select(kv => kv.Key + " " + kv.Value)));
            Assert.That(steps.Halted, Is.False, steps.HaltReason);
            Assert.That(steps.Engagements, Is.Empty, "no attack the plan could not afford");
            Assert.That(chunks, Is.Empty);
            Assert.That(steps.Encountered.Count, Is.EqualTo(1), "one individual met");
            Assert.That(steps.EncounterFrames, Is.GreaterThan(100), "met on many frames, counted apart from the individuals");
            Assert.That(steps.PassedOverFrames.ContainsKey("the plan's remaining time"), Is.True);
            Assert.That(steps.PassedOverFrames.ContainsKey("the script's last 60 s"), Is.True);
            Assert.That(steps.PassedOverById[9].Keys, Is.EquivalentTo(steps.PassedOverFrames.Keys), "the individual's own reasons");
            Assert.That(steps.PassedOverFrames.Values.Sum(), Is.EqualTo(steps.EncounterFrames), "every frame it was met and not attacked, under one reason");
            Assert.That(steps.Steps[0].outcome, Is.EqualTo("reached"));

            // By the next planned stand: passed over for it.
            string[] near = { "visit,there", "goto,0,20,40", "face,90,10", "slash,2227,512,30", EngageLine, "end,600" };
            CityWalkSteps byStand = Walk(near, (t, at, yaw) => new List<CityWalkSteps.Seen> { new CityWalkSteps.Seen { id = 3, position = new Vector2(2f, 21f), visible = true } },
                200, 2.0, out _, out _, out _);
            Assert.That(byStand.PassedOverFrames.ContainsKey("within 12 m of the next planned stand"), Is.True, string.Join(", ", byStand.PassedOverFrames.Keys));
            Assert.That(byStand.PassedOverFrames.ContainsKey("a planned face or Slash"), Is.True);
            Assert.That(byStand.Engagements, Is.Empty);
        }

        [Test]
        public void AnUnreachableWaypoint_StopsTheWalkThere_SaidSo_NothingAfterItRun()
        {
            string[] script = { "visit,first", "goto,0,5,30", "visit,second", "goto,0,20,8", "face,90,10", "slash,2227,512,30", "visit,third", "goto,5,20,30", "end,600" };
            // A wall at z = 12: the move is refused there, as the crowd map refuses it.
            CityWalkSteps steps = Run(script, 1f / 72f, 120, out List<string> records, out var chunks, blocked: p => p.y >= 12f);
            TestContext.WriteLine(string.Join("\n", records));
            Assert.That(steps.Halted, Is.True);
            Assert.That(steps.HaltReason, Does.Contain("not reached within 8 s").And.Contain("second"));
            Assert.That(chunks, Is.Empty, "no Slash from a wrong place");
            Assert.That(steps.Steps[1].outcome, Does.StartWith("UNREACHABLE"));
            Assert.That(steps.Steps.Skip(2).All(s => s.outcome == "not run"), Is.True, "nothing after it run");
            var visits = steps.Visits().ToList();
            Assert.That(visits[0].ended, Is.True);
            Assert.That(visits[1].ended, Is.False);
            Assert.That(visits[1].detail, Does.StartWith("UNREACHABLE"));
            Assert.That(visits[2].detail, Is.EqualTo("not run"));
            Assert.That(records.Any(r => r.Contains("HALTED")), Is.True, "said so");
        }

        [Test]
        public void TheScriptsBudget_StopsTheWalk_SaidSo()
        {
            string[] script = { "visit,far", "goto,0,100,600", "end,20" };
            CityWalkSteps steps = Run(script, 1f / 72f, 60, out List<string> records, out _);
            Assert.That(steps.Halted, Is.True);
            Assert.That(steps.HaltReason, Does.Contain("budget"));
        }

        [Test]
        public void AStepScriptIsToldFromATimedOne_AndItsLegsAddUp()
        {
            Assert.That(CityWalkSteps.IsStepScript(new[] { "# c", "move,1,2,1,0", "slash,3,2227,512", "end,10" }), Is.False);
            Assert.That(CityWalkSteps.IsStepScript(new[] { "visit,a", "goto,0,3,10", "end,10" }), Is.True);
            var steps = CityWalkSteps.Parse(new[] { "goto,0,3,10", "goto,4,3,10", "end,30" }, Vector2.zero);
            Assert.That(steps.PlannedMetres, Is.EqualTo(7f).Within(1e-4f));
            Assert.That(steps.Budget, Is.EqualTo(30f));
            Assert.Throws<System.FormatException>(() => CityWalkSteps.Parse(new[] { "move,1,2,1,0" }, Vector2.zero));
        }
    }
}
