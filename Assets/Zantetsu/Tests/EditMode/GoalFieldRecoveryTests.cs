using System;
using System.Collections.Generic;
using NUnit.Framework;
using Zantetsu.Core.MobPlan;

namespace Zantetsu.Core.Tests
{
    /// <summary>PLANNER-FIELD-RECOVERY: the way back to the goal field from the band the field closes about an obstacle, on a made-up map.</summary>
    public sealed class GoalFieldRecoveryTests
    {
        /// <summary>Two discs of 2 m radius, 4.6 m apart on x: a 0.6 m gap between them at x = 0, z = 0; open everywhere else.</summary>
        private sealed class TwoDiscs : WalkableMap
        {
            public override float ClearanceAt(float x, float z)
            {
                double a = Math.Sqrt((x + 2.3) * (x + 2.3) + z * z) - 2.0, b = Math.Sqrt((x - 2.3) * (x - 2.3) + z * z) - 2.0;
                return (float)Math.Max(0.0, Math.Min(a, b));
            }

            public override bool IsWalkableAt(float x, float z) => ClearanceAt(x, z) > 0f;
        }

        private static GoalField Field(WalkableMap map, float goalX, float goalZ)
        {
            map.OriginX = -20f; map.OriginZ = -20f; map.WorldWidth = 40f; map.WorldHeight = 40f;
            return new GoalField(new FieldGrid(map, 0.77f, 0.5f), goalX, goalZ);
        }

        [Test]
        public void FromInsideTheClosedBand_TheWayBackKeepsTheWalkingClearance_AndItsValueIsTheWalkPlusTheFieldsRest()
        {
            var map = new TwoDiscs();
            GoalField field = Field(map, 0f, 10f);   // the goal beyond the discs, straight through the gap
            // a place just before the gap's mouth: 0.45 m from the discs -- walking may be there, the field (0.77 m) has no cell there
            float x = 0f, z = -1.05f;
            Assert.That(map.ClearanceAt(x, z), Is.InRange(0.4f, 0.6f));
            Assert.That(float.IsPositiveInfinity(field.CellPath(x, z)), Is.True, "the field's own cell there is closed");
            Func<float, float, bool> walkable = (px, pz) => map.ClearanceAt(px, pz) >= 0.37f;
            var patch = new FieldRecovery(field, x, z, 4f, walkable);
            Assert.That(patch.Found, Is.True, "a way back within 4 m");
            Assert.That(patch.Sources, Is.GreaterThan(0));
            Assert.That(patch.ConnectMeters, Is.InRange(0.25f, 9f), "a walk inside the 4 m patch (it may wind: the field is entered where the walk plus the rest is least, not where it is nearest)");
            float remaining = field.CellPath(patch.EntryX, patch.EntryZ);
            Assert.That(float.IsPositiveInfinity(remaining), Is.False, "it ends where the field can be entered");
            Assert.That(patch.CentreValue, Is.EqualTo(patch.ConnectMeters + remaining).Within(0.3f), "the walk + the field's rest");
            List<float> path = patch.CentrePath();
            for (int i = 2; i < path.Count; i += 2)
            {
                Assert.That(map.ClearanceAt(path[i], path[i + 1]), Is.GreaterThanOrEqualTo(0.37f - 1e-3f), "the way never comes nearer an obstacle than walking keeps");
                // a diagonal step has both cells beside it walkable: the way is made of the search's own connections
                bool diagonal = Math.Abs(path[i] - path[i - 2]) > 0.01f && Math.Abs(path[i + 1] - path[i - 1]) > 0.01f;
                if (diagonal && i > 2) Assert.That(walkable(path[i], path[i - 1]) && walkable(path[i - 2], path[i + 1]), Is.True, "no diagonal past a corner at step " + i / 2);
            }
            Assert.That(patch.TryGuide(x, z, 2f, out float gx, out float gz), Is.True);
            Assert.That(map.ClearanceAt(gx, gz), Is.GreaterThanOrEqualTo(0.37f - 1e-3f));
            Assert.That(patch.TryDistance(x, z, out float value) && Math.Abs(value - patch.CentreValue) < 1e-4f, Is.True);
            Assert.That(patch.TryDistance(30f, 30f, out _), Is.False, "outside the patch nothing is said");
        }

        private sealed class OpenMap : WalkableMap
        {
            public override float ClearanceAt(float x, float z) => 10f;
            public override bool IsWalkableAt(float x, float z) => true;
        }

        /// <summary>Open ground, but a 4 m square about the origin where the clearance is 0.5 m: walking may be there (0.37 m), the field (0.77 m) has no cell there.</summary>
        private sealed class Band : WalkableMap
        {
            public override float ClearanceAt(float x, float z) => Math.Abs(x) < 2f && Math.Abs(z) < 2f ? 0.5f : 10f;
            public override bool IsWalkableAt(float x, float z) => true;
        }

        [Test]
        public void TheWayBack_TakesOnlyTheConnectionsTheSearchMade_NeverADiagonalPastACorner()
        {
            // the goal lies along the diagonal; two 0.25 m posts stand at (0.25, 0) and (0, 0.25), so the diagonal step from the
            // origin to (0.25, 0.25) would pass between them: the search refuses it and goes round
            var map = new Band();
            GoalField field = Field(map, 15f, 15f);
            bool Post(float px, float pz) => (Math.Abs(px - 0.25f) < 0.01f && Math.Abs(pz) < 0.01f) || (Math.Abs(px) < 0.01f && Math.Abs(pz - 0.25f) < 0.01f);
            Func<float, float, bool> walkable = (px, pz) => map.ClearanceAt(px, pz) >= 0.37f && !Post(px, pz);
            Assert.That(float.IsPositiveInfinity(field.CellPath(0f, 0f)), Is.True, "the field has no cell at the origin");
            var patch = new FieldRecovery(field, 0f, 0f, 4f, walkable);
            Assert.That(patch.Found, Is.True);
            List<float> path = patch.CentrePath();
            Assert.That(path.Count, Is.GreaterThan(6));
            Assert.That(Math.Abs(path[2] - 0.25f) < 0.01f && Math.Abs(path[3] - 0.25f) < 0.01f, Is.False, "not the diagonal between the two posts");
            for (int i = 2; i < path.Count; i += 2)
            {
                Assert.That(walkable(path[i], path[i + 1]), Is.True);
                bool diagonal = Math.Abs(path[i] - path[i - 2]) > 0.01f && Math.Abs(path[i + 1] - path[i - 1]) > 0.01f;
                if (diagonal) Assert.That(walkable(path[i], path[i - 1]) && walkable(path[i - 2], path[i + 1]), Is.True, "a diagonal step needs both cells beside it (step " + i / 2 + ")");
            }

            Assert.That(patch.TryGuide(0f, 0f, 0.3f, out float gx, out float gz), Is.True);
            Assert.That(Math.Abs(gx - 0.25f) < 0.01f && Math.Abs(gz - 0.25f) < 0.01f, Is.False, "the guide's first step is the search's too");
        }

        [Test]
        public void WhatThePatchWalksOn_KeepsTheWalkingDistance_AndIsRelaxedOnlyAboutAnNpcAlreadyNearer()
        {
            // two felled pieces: a 2 x 2 m box about (10, 10) and another about (14, 10); cells of 0.2 m
            var input = new PropObstacleInput();
            input.SetWindow(new PropObstacleSettings { cells = 150, cellMeters = 0.2f, planeHeightMeters = 1.5f, maxDistanceMeters = 3f }, 0f, 0f);
            foreach (float cx in new[] { 10f, 14f })
            {
                int i = input.Add(Box(-1f, 0f, -1f, 1f, 3f, 1f), (int)cx, (int)cx, 0);
                Array.Clear(input.Matrices, i * 12, 12);
                input.Matrices[i * 12] = 1f; input.Matrices[i * 12 + 5] = 1f; input.Matrices[i * 12 + 10] = 1f; input.Matrices[i * 12 + 3] = cx; input.Matrices[i * 12 + 11] = 10f;
            }

            var props = new PropObstacleMap();
            new PropObstacleBuilder().Build(input, null, props);
            var map = new OpenMap();
            const float keep = 0.45f;

            // an NPC that keeps the distance (0.49 m from the first box): 0.45 m everywhere, nothing relaxed
            // along z = 10 outwards from the first box: the first place 0.49 m from its cells, and one 0.40 m from them
            float nearX = float.NaN, closeX = float.NaN;
            for (float sx = 11f; sx < 12.4f; sx += 0.01f)
            {
                float d = props.DistanceAt(sx, 10f);
                if (float.IsNaN(closeX) && d >= 0.39f && d <= 0.41f) closeX = sx;
                if (float.IsNaN(nearX) && d >= 0.49f) nearX = sx;
            }

            Assert.That(float.IsNaN(nearX) || float.IsNaN(closeX), Is.False, "the two places exist");
            Func<float, float, bool> normal = FieldRecovery.WalkRule(map, 0.37f, props, keep, nearX, 10f, 1f, out bool overlapping);
            Assert.That(overlapping, Is.False, "0.49 m is not nearer than walking keeps");
            Assert.That(normal(nearX, 10f), Is.True);
            Assert.That(normal(closeX, 10f), Is.False, "0.40 m from the cells: under 0.45 m, refused (the first version let 0.39 m pass here)");
            Assert.That(normal(10f, 10f) || normal(14f, 10f), Is.False, "no piece's inside is walkable");

            // an NPC inside the first box: it may get out through its own surroundings, within 1 m of where it stands -- and no farther
            Func<float, float, bool> inside = FieldRecovery.WalkRule(map, 0.37f, props, keep, 10.6f, 10f, 1f, out overlapping);
            Assert.That(overlapping, Is.True);
            Assert.That(inside(10.6f, 10f) && inside(11.0f, 10f) && inside(11.3f, 10f), Is.True, "out through the near face, inside the escape radius");
            Assert.That(inside(9.4f, 10f), Is.False, "the same box's far side, 1.2 m off: not relaxed");
            Assert.That(inside(14f, 10f) || inside(13.2f, 10f), Is.False, "the other piece's inside stays closed (the first version opened the whole patch)");
            Assert.That(inside(12.2f, 10f), Is.True, "between the pieces, 0.9 m from each: plain walking ground");
        }

        private static PropShape Box(float x0, float y0, float z0, float x1, float y1, float z1)
        {
            float[] lohi = { x0, y0, z0, x1, y1, z1 };
            var vertices = new float[24];
            for (int v = 0; v < 8; v++)
            {
                vertices[v * 3] = lohi[(v & 1) != 0 ? 3 : 0]; vertices[v * 3 + 1] = lohi[(v & 2) != 0 ? 4 : 1]; vertices[v * 3 + 2] = lohi[(v & 4) != 0 ? 5 : 2];
            }

            int[] edges = { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };
            return new PropShape(1, vertices, new[] { 0, 8 }, edges, new[] { 0, 12 }, lohi);
        }

        [Test]
        public void TheEstimateForTheCost_IsApartFromTheWay_AndHasNoJumpWhereTheWaysOnBothSidesAreAlike()
        {
            var map = new TwoDiscs();
            GoalField field = Field(map, 0f, 10f);
            Func<float, float, bool> walkable = (px, pz) => map.ClearanceAt(px, pz) >= 0.37f;
            var patch = new FieldRecovery(field, 0f, -1.05f, 4f, walkable);
            Assert.That(patch.Found, Is.True);
            // inside a disc: not walkable -- no way back from there, no guide, no connected value; the estimate alone answers
            float ix = -1.5f, iz = -0.5f;
            Assert.That(walkable(ix, iz), Is.False);
            Assert.That(patch.TryDistance(ix, iz, out _), Is.False, "no connected value where walking cannot be");
            Assert.That(patch.TryGuide(ix, iz, 2f, out _, out _), Is.False, "and no way from there");
            Assert.That(patch.TryEstimate(ix, iz, out float estimate, out float fromX, out float fromZ, out float away), Is.True);
            bool connected = patch.TryDistance(fromX, fromZ, out float at);
            Assert.That(walkable(fromX, fromZ) && connected, Is.True, "the estimate comes from a connected cell");
            Assert.That(estimate, Is.EqualTo(at + away).Within(1e-3f), "that cell's value + the distance to it");
            Assert.That(away, Is.InRange(0.2f, 6f), "some way off, inside the patch (the cell is the one whose value plus distance is least, not the nearest)");
            // a connected point answers its own value
            Assert.That(patch.TryEstimate(0f, -1.05f, out float own, out _, out _, out float none) && Math.Abs(own - patch.CentreValue) < 1e-4f && none == 0f, Is.True);
            // no jump HERE: along a line through the disc, cell to cell, the estimate changes by no more than a diagonal step -- the
            // ways back on the two sides of this disc are alike. It is not a general property: see the long wall below.
            float before = float.NaN;
            for (float sx = -3.5f; sx <= 0.01f; sx += 0.25f)
            {
                Assert.That(patch.TryEstimate(sx, -0.5f, out float v, out _, out _, out _), Is.True, "x " + sx);
                if (!float.IsNaN(before)) Assert.That(Math.Abs(v - before), Is.LessThanOrEqualTo(0.25f * 1.4143f + 1e-3f), "between x " + (sx - 0.25f) + " and " + sx);
                before = v;
            }

            Assert.That(patch.TryEstimate(30f, 30f, out _, out _, out _, out _), Is.False, "outside the patch nothing is estimated");
        }

        /// <summary>
        /// A wall along z = 0 (half a metre thick) from the map's west edge to x = 6, and about it, west of x = 4, a strip 2.5 m to
        /// each side where the clearance is at most 0.5 m: walking may be there (0.37 m) down to 0.62 m from the wall's middle, the
        /// field (0.77 m) has no cell there. The goal is north: from the south the way goes round the wall's end.
        /// </summary>
        private sealed class LongWall : WalkableMap
        {
            public override float ClearanceAt(float x, float z)
            {
                double dz = Math.Max(0.0, Math.Abs(z) - 0.25), dx = Math.Max(0.0, x - 6.0);
                float real = (float)Math.Sqrt(dx * dx + dz * dz);
                return x < 4f && Math.Abs(z) < 2.5f ? Math.Min(real, 0.5f) : real;
            }

            public override bool IsWalkableAt(float x, float z) => ClearanceAt(x, z) > 0f;
        }

        [Test]
        public void WhereTheWaysOnTheTwoSidesOfAnObstacleDiffer_TheDistanceTheCostUses_KeepsAStepOfThatDifference_BesideTheLongSide()
        {
            // The limit of the estimate, read from what the search itself is given (SingleAgentSearch.GoalDistanceFor): the estimate
            // spreads from the lowest connected values and a connected cell keeps its own, so the cells inside the wall take the
            // north side's short way and the south side's connected cells stand the two ways' difference above them.
            var map = new LongWall();
            GoalField field = Field(map, 0f, 10f);
            var config = new PlannerConfig { goalFieldRecovery = true, goalFieldRecoveryExtrapolate = true };
            SearchRequest Request(PlannerConfig c) => new SearchRequest
            {
                AgentId = 1, Radius = 0.35f, GoalX = 0f, GoalZ = 10f, Field = field, StartPose = new Pose2(0f, -1f, 0f), StartTime = 0f, HorizonEnd = 8f,
                Config = c, Recovery = new FieldRecoveryHolder(), Trace = true,
            };
            var search = new SingleAgentSearch(null, map);
            SearchRequest request = Request(config);
            var zs = new List<float>(); var values = new List<float>(); var sources = new List<char>();
            for (int k = -1; k <= 9; k++)   // z = -1.25 .. 1.25 on the patch's own cells (the patch is centred on z = -1)
            {
                float z = -1f + k * 0.25f;
                zs.Add(z); values.Add(search.GoalDistanceFor(request, 0f, z, out char source)); sources.Add(source);
            }

            string seen = new string(sources.ToArray());
            Assert.That(seen, Is.EqualTo("PPPXXXXXPPP"), "south: connected; inside the wall: the estimate; north: connected");
            float south = values[2], firstInside = values[3], north = values[8];
            Assert.That(south - north, Is.GreaterThan(8f), "the case: the way from the south is much longer (round the wall's end)");
            for (int i = 4; i < values.Count; i++)
            {
                Assert.That(Math.Abs(values[i] - values[i - 1]), Is.LessThanOrEqualTo(0.25f + 1e-3f), "from inside the wall on to the north, cell to cell: no step (z " + zs[i - 1] + " to " + zs[i] + ")");
            }

            float step = south - firstInside;
            Assert.That(step, Is.GreaterThan(8f), "from the south's last connected cell into the wall the distance DROPS by the two ways' difference: a step, not one cell's");
            FieldRecovery patch = request.Recovery.Patch;
            Assert.That(patch.EstimateMade, Is.True);
            Assert.That(patch.EstimateBoundaryStepMost, Is.GreaterThanOrEqualTo(step - 0.25f - 1e-3f), "the patch reports the step");
            Assert.That(patch.EstimateBoundarySteps, Is.GreaterThan(0));
            Assert.That(patch.TryGuide(0f, -0.5f, 2f, out _, out _) || patch.TryDistance(0f, -0.5f, out _), Is.False, "the estimate is still no way");

            // without the estimate the same places take the straight line: a step there too (and a larger one against the south)
            var plain = new PlannerConfig { goalFieldRecovery = true };
            SearchRequest before = Request(plain);
            float line = search.GoalDistanceFor(before, 0f, -0.5f, out char s);
            Assert.That(s, Is.EqualTo('S'));
            Assert.That(line, Is.EqualTo(10.5f).Within(1e-3f));
            Assert.That(search.GoalDistanceFor(before, 0f, -0.75f, out s) - line, Is.GreaterThan(8f));
            Assert.That(s, Is.EqualTo('P'));
        }

        [Test]
        public void TheNoPointTime_RunsOnlyWhileNoPointIsFound_IsCarriedOverAFollowedWaypoint_AndEndsWithAPointOrANewStage()
        {
            var agent = new SimAgent { GoalStage = 3, GoalRevision = 10 };
            Assert.That(agent.NoPointSeconds(100f), Is.EqualTo(0f), "nothing runs at first");
            agent.ObserveNoPoint(true, 100f);
            agent.ObserveNoPoint(true, 101f);
            Assert.That(agent.NoPointSeconds(101f), Is.EqualTo(1f).Within(1e-4f));
            // the pass waypoint follows the player: a new revision of the goal, the same stage -- the time is carried on
            agent.GoalRevision = 11;
            agent.ObserveNoPoint(true, 102f);
            agent.GoalRevision = 12;
            agent.ObserveNoPoint(true, 103f);
            Assert.That(agent.NoPointSeconds(103f), Is.EqualTo(3f).Within(1e-4f), "carried over two follow-up moves");
            // a point is found: the time ends, and a later no-point cycle of the SAME goal starts from nothing (the first version
            // kept the first cycle's time per goal revision and gave up at once here)
            agent.ObserveNoPoint(false, 104f);
            Assert.That(agent.ReachNone, Is.False);
            Assert.That(agent.NoPointSeconds(104f), Is.EqualTo(0f));
            agent.ObserveNoPoint(true, 105f);
            Assert.That(agent.NoPointSeconds(105f), Is.EqualTo(0f), "not the 5 s since the first cycle");
            agent.ObserveNoPoint(true, 106.5f);
            Assert.That(agent.NoPointSeconds(106.5f), Is.EqualTo(1.5f).Within(1e-4f));
            // the pass is done or the goal replaced: another stage -- the time does not carry
            agent.GoalStage = 4; agent.GoalRevision = 13;
            Assert.That(agent.NoPointSeconds(107f), Is.EqualTo(0f), "an old stage's time is not read");
            agent.ObserveNoPoint(true, 107f);
            Assert.That(agent.NoPointSeconds(107.5f), Is.EqualTo(0.5f).Within(1e-4f));
        }

        [Test]
        public void APointOfAnEarlierGoalRevision_IsNotUsed_AndAtAStandoffTheGoalsOwnConditionComesFirst()
        {
            var agent = new SimAgent { GoalX = 10f, GoalZ = 0f, GoalRevision = 5, ReachActive = true, ReachStandoff = true, ReachX = 12f, ReachZ = 0f, ReachGoalRevision = 5 };
            Assert.That(agent.ReachValid, Is.True);
            Assert.That(agent.PlanGoalX, Is.EqualTo(12f));
            agent.GoalX = 20f; agent.GoalRevision = 6;   // the goal moved: the point was set for revision 5
            Assert.That(agent.ReachValid, Is.False);
            Assert.That(agent.PlanGoalX, Is.EqualTo(20f), "the planner is given the goal, not the old point");

            // standing 0.55 m from the goal and 0.3 m from a standoff: the goal itself is reached
            Assert.That(SimAgent.StandoffArrivalDistance(0.55f, 0.3f, 0.6f, out bool own), Is.EqualTo(0.55f));
            Assert.That(own, Is.True);
            // 1.4 m from the goal, 0.3 m from the standoff: a standoff arrival
            Assert.That(SimAgent.StandoffArrivalDistance(1.4f, 0.3f, 0.6f, out own), Is.EqualTo(0.3f));
            Assert.That(own, Is.False);
            // 1.4 m from the goal, 2 m from the standoff: not arrived (the standoff's distance is what is tested)
            Assert.That(SimAgent.StandoffArrivalDistance(1.4f, 2f, 0.6f, out own), Is.GreaterThan(0.6f));
        }

        [Test]
        public void WithNoWalkableWayOut_ThereIsNoWayBack()
        {
            var map = new TwoDiscs();
            GoalField field = Field(map, 0f, 10f);
            // walking needs 0.37 m, and a ring from 0.3 to 1 m about the place is refused: the place is shut in
            Func<float, float, bool> shut = (px, pz) =>
            {
                double d = Math.Sqrt(px * px + (pz + 1.05) * (pz + 1.05));
                return map.ClearanceAt(px, pz) >= 0.37f && (d < 0.3 || d > 1.0);
            };
            var patch = new FieldRecovery(field, 0f, -1.05f, 4f, shut);
            Assert.That(patch.Sources, Is.GreaterThan(0), "the field can be entered somewhere in the patch");
            Assert.That(patch.Found, Is.False, "but not from here");
            Assert.That(patch.TryGuide(0f, -1.05f, 2f, out _, out _), Is.False);
            Assert.That(patch.TryDistance(0f, -1.05f, out _), Is.False);
        }
    }
}
