using System.Collections;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The staged stop of a hull cut's sides, without and with the short sibling constraint (TL, 2026-09-30; a limited
    /// prototype): the same initial state, cut, separation and stop in both conditions (the sides' collision with each
    /// other excluded in both, the motion time 0.25 s, the opening 0 .. 5 cm, the new penetration of a candidate 5 mm).
    /// The trial's clock is the physics clock here, so that what happens does not depend on the Editor's speed; the
    /// work off Main still completes when the dispatcher runs it. Each case writes the opening along the normal, the move
    /// along the plane, the relative turn, the fastest speed, the stops, the constraints left, the candidates adopted or
    /// not, the groups, bodies, hulls and colliders, the Main time and the Simulate; the comparison is the record's.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const float StageSeconds = 0.25f, StageOpening = 0.05f;

        private CutWorldRoot NewStageWorld(bool d6, float deadline = 0.9f)
        {
            CutWorldRoot root = NewHullWorld(deadline, profile =>
            {
                SetPrivate(profile, "buildingHullMaxNewPenetrationMetres", 0.005f);
                SetPrivate(profile, "buildingHullStageSeconds", StageSeconds);
                SetPrivate(profile, "buildingHullSiblingD6", d6);
                SetPrivate(profile, "buildingHullSiblingOpeningMetres", StageOpening);
            });
            BuildingHullFusion h = root.Hulls;
            Assert.That(h.Settings.stageSeconds, Is.EqualTo(StageSeconds).Within(1e-6), "the motion time read back");
            Assert.That(h.Settings.siblingD6, Is.EqualTo(d6), "the constraint switch read back");
            Assert.That(h.Settings.siblingOpeningMetres, Is.EqualTo(StageOpening).Within(1e-6));
            Assert.That(h.Settings.maxNewPenetrationMetres, Is.EqualTo(0.005).Within(1e-9));
            h.RealSecondsForTest = () => CutPhysicsStep.Clock.PhysicsSeconds;   // the test clock: the steps taken, not the Editor's speed
            return root;
        }

        /// <summary>Frames until a condition, the Simulate of every step taken meanwhile collected.</summary>
        private static IEnumerator UntilSampled(CutWorldRoot root, System.Func<bool> condition, float seconds, string what, List<double> simulates)
        {
            long last = CutPhysicsStep.Clock.StepId;
            float until = Time.realtimeSinceStartup + seconds;
            while (!condition() && Time.realtimeSinceStartup < until)
            {
                yield return null;
                if (CutPhysicsStep.Clock.StepId != last) { simulates.Add(CutPhysicsStep.LastSimulateSeconds * 1000.0); last = CutPhysicsStep.Clock.StepId; }
            }

            if (!condition())
            {
                WriteHullRecord(root, what + ": timed out after " + seconds + " s; " + root.Hulls.DescribeUnsettled());
                Assert.Fail(what + ": within " + seconds + " s (" + root.Hulls.DescribeUnsettled() + ")");
            }
        }

        private static IEnumerator StepsSampled(int n, List<double> simulates)
        {
            long from = CutPhysicsStep.Clock.StepId, last = from;
            float until = Time.realtimeSinceStartup + 20f;
            while (CutPhysicsStep.Clock.StepId - from < n && Time.realtimeSinceStartup < until)
            {
                yield return null;
                if (CutPhysicsStep.Clock.StepId != last) { simulates.Add(CutPhysicsStep.LastSimulateSeconds * 1000.0); last = CutPhysicsStep.Clock.StepId; }
            }
        }

        private static string Stats(List<double> values)
        {
            if (values.Count == 0) return "n/a";
            var s = new List<double>(values); s.Sort();
            return "p50 " + s[s.Count / 2].ToString("F3") + " p99 " + s[(int)(0.99 * (s.Count - 1))].ToString("F3") + " max " + s[s.Count - 1].ToString("F3") + " (" + s.Count + " steps)";
        }

        /// <summary>The comparison line of one case; the scene's own bodies, hulls and colliders are counted apart from the trial's.</summary>
        private string StageLine(CutWorldRoot root, string label, List<double> simulates)
        {
            BuildingHullFusion h = root.Hulls;
            int bodies = 0, colliders = 0, joints = 0;
            foreach (Rigidbody b in Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None)) bodies++;
            foreach (Collider c in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None)) if (c.gameObject != _hullFloor && c.enabled) colliders++;
            foreach (ConfigurableJoint j in Object.FindObjectsByType<ConfigurableJoint>(FindObjectsSortMode.None)) joints++;
            string line = label + " | d6 " + h.Settings.siblingD6 + " | opening min " + h.MinOpening.ToString("F4") + " max " + h.MaxOpening.ToString("F4") + " m | along the plane max " + h.MaxInPlane.ToString("F4") + " m | relative turn max " + h.MaxRelativeRotationDegrees.ToString("F2") + " deg | fastest " + h.MaxPairSpeed.ToString("F2")
                + " m/s | largest move " + h.MaxFall.ToString("F3") + " m | pairs " + h.PairsMade + " (stops: deadline " + h.StopsByDeadline + ", next Slash " + h.StopsByNextSlash + ", other " + h.StopsOther + "; stop at " + h.MaxStageRealSeconds.ToString("F3") + " s max) | constraints made " + h.ConstraintsMade + " live " + h.ConstraintsLive + " (joints in the scene " + joints
                + ") | candidates " + h.AggregationsBegun + ": adopted " + h.Fusions + ", not achieved " + h.HullsNotAchieved + ", dropped " + h.FusionsStale + " | groups " + h.GroupCount + " (most " + h.MaxGroups + "), bodies " + bodies + ", hulls " + h.LiveHulls + ", colliders " + colliders + " | cuts " + h.GroupCuts
                + " | Main ms: physics " + (h.MainPhysicsSeconds * 1000).ToString("F2") + " (hull exchange " + (h.HullExchangeSeconds * 1000).ToString("F2") + "), display " + (h.MainDisplaySeconds * 1000).ToString("F2") + ", Step max " + (h.MaxStepSeconds * 1000).ToString("F3") + " | Simulate ms " + Stats(simulates);
            TestContext.Out.WriteLine(line);
            foreach (string r in h.StageRecords) TestContext.Out.WriteLine("  " + r);
            foreach (string n in h.NotAchieved) TestContext.Out.WriteLine("  not achieved: " + n);
            return line;
        }

        /// <summary>What every case asks, whatever the condition: nothing left moving, no constraint left (in the trial or in the scene), one hull, one body and one collider a group.</summary>
        private static void AssertStageEnd(CutWorldRoot root, string label)
        {
            BuildingHullFusion h = root.Hulls;
            Assert.That(h.PairsMoving, Is.Zero, label + ": no sides left moving");
            Assert.That(h.ConstraintsLive, Is.Zero, label + ": no constraint left");
            Assert.That(Object.FindObjectsByType<ConfigurableJoint>(FindObjectsSortMode.None).Length, Is.Zero, label + ": no joint left in the scene");
            int bodies = 0;
            foreach (HullGroup g in h.Groups) { if (g.Body != null) bodies++; Assert.That(g.HullCount, Is.EqualTo(1), label + ": group " + g.Id + " holds one hull"); Assert.That(g.Collider != null && g.Collider.attachedRigidbody == g.Body, Is.True, label + ": one collider on its body"); }
            Assert.That(bodies, Is.EqualTo(h.GroupCount), label + ": one body a group");
            Assert.That(h.StopsByDeadline + h.StopsByNextSlash + h.StopsOther, Is.EqualTo(h.PairsMade), label + ": every pair stopped once");
            Assert.That(h.ConstraintsMade, Is.EqualTo(h.Settings.siblingD6 ? h.PairsMade : 0), label + ": a constraint a pair only with the constraint on");
        }

        private static SlashSweep StageCut(long slash, string kind)
        {
            switch (kind)
            {
                case "horizontal": return Level(slash, 0.2f, -3f, 3f);
                case "vertical": return Upright(slash, 0.3f);
                default: return Wide(slash, math.normalize(new float3(1f, 1f, 0f)), new Vector3(0.2f, 0f, 0f), math.normalize(new float3(1f, -1f, 0f)), Vector3.forward);
            }
        }

        /// <summary>One single cut (horizontal, vertical or diagonal; one side anchored or both free) in a world of its own, one condition.</summary>
        private IEnumerator SingleCut(bool d6, string kind, bool anchored)
        {
            string label = kind + ", " + (anchored ? "one side anchored" : "both sides free");
            CutWorldRoot root = NewStageWorld(d6);
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, anchored ? new[] { new float3(-0.5f, -0.9f, 0f) } : new float3[0], 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            var simulates = new List<double>();
            yield return null;
            if (!anchored) yield return UntilSampled(root, () => building.Kinematic, 30f, label + ": the free building at rest first", simulates);
            TestContext.Out.WriteLine(label + ": the building's centre before the cut " + (Vector3)CentreOf(building));
            simulates.Clear();
            Evaluate(detector, StageCut(1, kind), 1);
            Assert.That(detector.HitCount, Is.EqualTo(1), label + ": the cut hits");
            yield return UntilSampled(root, () => h.GroupCuts == 1, 30f, label + ": published", simulates);
            Assert.That(h.PairsMade, Is.EqualTo(1), label + ": the sides move as a pair");
            if (d6) Assert.That(h.ConstraintsLive, Is.EqualTo(1), label + ": held by the constraint");
            yield return UntilSampled(root, () => h.PairsMoving == 0, 30f, label + ": the sides fixed", simulates);
            Assert.That(h.StopsByDeadline, Is.EqualTo(1), label + ": fixed by the motion time");
            Assert.That(h.MaxStageRealSeconds, Is.EqualTo(StageSeconds).Within(0.05), label + ": at the motion time (the test clock is the physics clock: a step's length)");
            foreach (HullGroup g in h.Groups) Assert.That(g.Kinematic && g.Body.isKinematic, Is.True, label + ": group " + g.Id + " fixed");
            yield return UntilSampled(root, () => h.IsSettled, 60f, label + ": the aggregation decided", simulates);
            yield return StepsSampled(20, simulates);
            foreach (HullGroup g in h.Groups) Assert.That(g.Kinematic && g.Body.isKinematic, Is.True, label + ": group " + g.Id + " still fixed after the contacts went on (not released)");
            StageLine(root, label, simulates);
            AssertStageEnd(root, label);
            yield return EndWorld(root);
            Assert.That(Object.FindObjectsByType<ConfigurableJoint>(FindObjectsSortMode.None).Length, Is.Zero, label + ": nothing left after the end");
        }

        [UnityTest] public IEnumerator HullStage_Horizontal_Anchored_WithoutConstraint() { yield return SingleCut(false, "horizontal", true); }
        [UnityTest] public IEnumerator HullStage_Horizontal_Anchored_WithConstraint() { yield return SingleCut(true, "horizontal", true); }
        [UnityTest] public IEnumerator HullStage_Horizontal_Free_WithoutConstraint() { yield return SingleCut(false, "horizontal", false); }
        [UnityTest] public IEnumerator HullStage_Horizontal_Free_WithConstraint() { yield return SingleCut(true, "horizontal", false); }
        [UnityTest] public IEnumerator HullStage_Vertical_Anchored_WithoutConstraint() { yield return SingleCut(false, "vertical", true); }
        [UnityTest] public IEnumerator HullStage_Vertical_Anchored_WithConstraint() { yield return SingleCut(true, "vertical", true); }
        [UnityTest] public IEnumerator HullStage_Vertical_Free_WithoutConstraint() { yield return SingleCut(false, "vertical", false); }
        [UnityTest] public IEnumerator HullStage_Vertical_Free_WithConstraint() { yield return SingleCut(true, "vertical", false); }
        [UnityTest] public IEnumerator HullStage_Diagonal_Anchored_WithoutConstraint() { yield return SingleCut(false, "diagonal", true); }
        [UnityTest] public IEnumerator HullStage_Diagonal_Anchored_WithConstraint() { yield return SingleCut(true, "diagonal", true); }
        [UnityTest] public IEnumerator HullStage_Diagonal_Free_WithoutConstraint() { yield return SingleCut(false, "diagonal", false); }
        [UnityTest] public IEnumerator HullStage_Diagonal_Free_WithConstraint() { yield return SingleCut(true, "diagonal", false); }

        /// <summary>Ten short consecutive cuts (every 6 steps, shorter than the motion time) through the largest group, planes turning; one side anchored.</summary>
        private IEnumerator TenShortCuts(bool d6)
        {
            CutWorldRoot root = NewStageWorld(d6);
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            var simulates = new List<double>();
            yield return null;
            int hits = 0;
            for (int i = 0; i < 10; i++)
            {
                HullGroup big = null;
                foreach (HullGroup g in h.Groups) if (g.State == HullGroupState.Idle && (big == null || g.Mass > big.Mass)) big = g;
                if (big != null)
                {
                    float3 c = CentreOf(big);
                    long slash = 10 + i;
                    SlashSweep sweep = (i % 3) switch
                    {
                        0 => Upright(slash, c.x + 0.1f * ((i % 2) * 2 - 1), c.y),
                        1 => Level(slash, c.y + 0.05f * ((i % 4) - 1.5f), c.x - 3f, c.x + 3f, c.z - 3f, c.z + 3f),
                        _ => Wide(slash, math.normalize(new float3(1f, 1f, 0.3f)), (Vector3)c, math.normalize(new float3(1f, -1f, 0f)), Vector3.forward),
                    };
                    Evaluate(detector, sweep, slash);
                    hits += detector.HitCount;
                }

                yield return StepsSampled(6, simulates);
            }

            yield return UntilSampled(root, () => h.IsSettled, 90f, "after ten short cuts: settled", simulates);
            yield return StepsSampled(20, simulates);
            TestContext.Out.WriteLine("==== ten short cuts (box), d6 " + d6 + ": detector hits " + hits);
            StageLine(root, "ten short cuts (box)", simulates);
            WriteHullRecord(root, "ten short cuts (box), d6 " + d6);
            AssertStageEnd(root, "ten short cuts (box)");
            Assert.That(h.GroupCuts, Is.GreaterThanOrEqualTo(5), "the cuts published");
            yield return EndWorld(root);
        }

        [UnityTest] public IEnumerator HullStage_TenShortCuts_WithoutConstraint() { yield return TenShortCuts(false); }
        [UnityTest] public IEnumerator HullStage_TenShortCuts_WithConstraint() { yield return TenShortCuts(true); }

        /// <summary>The same ten short consecutive cuts on college_001 (one anchor, the floor at its base).</summary>
        private IEnumerator CollegeTenShortCuts(bool d6)
        {
            if (!File.Exists(CollegeInputPath)) Assert.Ignore("the licensed college_001 one-anchor input is not in this checkout: " + CollegeInputPath);
            var data = JsonUtility.FromJson<PlacedCuttableInput>(File.ReadAllText(CollegeInputPath));
            float baseY = float.MaxValue;
            foreach (Vector3 v in data.hulls[0].vertices) baseY = Mathf.Min(baseY, v.y);
            CutWorldRoot root = NewStageWorld(d6);
            BuildingHullFusion h = root.Hulls;
            GameObject instance = TrackActor(new GameObject("college_001 instance"));
            instance.transform.position = new Vector3(0f, -1f - baseY, 0f);
            PlacedCuttableRegistration made = PlacedCuttableRegistration.RegisterHull(root, data, instance.transform, new Renderer[0], new Collider[0], 10000f, SideMaterial);
            _actors.Add(made.Actor);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            var simulates = new List<double>();
            yield return null;
            int hits = 0;
            for (int i = 0; i < 10; i++)
            {
                HullGroup big = null;
                foreach (HullGroup g in h.Groups) if (g.State == HullGroupState.Idle && (big == null || g.Mass > big.Mass)) big = g;
                if (big != null)
                {
                    big.Shape.ConvexBounds(0, out float3 lo, out float3 hi);
                    float3 size = hi - lo;
                    float3 c = CentreOf(big);
                    int kind = i % 3;
                    float off = (i % 5 - 2) * 0.12f;
                    Vector3 normal = kind == 0 ? Vector3.right : kind == 1 ? Vector3.up : (Vector3)math.normalize(new float3(1f, 1f, 0f));
                    Vector3 centre = (Vector3)c + normal * (off * (kind == 0 ? size.x : kind == 1 ? size.y : size.x));
                    Vector3 u = kind == 1 ? Vector3.right : kind == 0 ? Vector3.up : (Vector3)math.normalize(new float3(1f, -1f, 0f)), v = Vector3.forward;
                    long slash = 100 + i;
                    Evaluate(detector, Wide(slash, normal, centre, u, v), slash);
                    hits += detector.HitCount;
                }

                yield return StepsSampled(6, simulates);
            }

            yield return UntilSampled(root, () => h.IsSettled, 120f, "college after ten short cuts: settled", simulates);
            yield return StepsSampled(20, simulates);
            TestContext.Out.WriteLine("==== ten short cuts (college_001), d6 " + d6 + ": detector hits " + hits);
            StageLine(root, "ten short cuts (college_001)", simulates);
            WriteHullRecord(root, "ten short cuts (college_001), d6 " + d6);
            AssertStageEnd(root, "ten short cuts (college_001)");
            yield return EndWorld(root);
        }

        [UnityTest] public IEnumerator HullStage_CollegeTenShortCuts_WithoutConstraint() { yield return CollegeTenShortCuts(false); }
        [UnityTest] public IEnumerator HullStage_CollegeTenShortCuts_WithConstraint() { yield return CollegeTenShortCuts(true); }

        /// <summary>
        /// **The constraint is published at its inward boundary, and removed at the end while the sides move.** The anchor
        /// and the connected anchor stand one half-opening apart along the axis in the pair's rest relation (no error at
        /// the publication); the limit is the half-opening; the pair's collision is excluded. The world ends inside the
        /// motion time: no joint, body or collider is left.
        /// </summary>
        [UnityTest]
        public IEnumerator HullStage_Constraint_PublishedAtItsBoundary_AndRemovedByTheEnd()
        {
            CutWorldRoot root = NewStageWorld(true);
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            h.RealSecondsForTest = () => 0.0;   // the clock held: the pair keeps moving
            Evaluate(detector, StageCut(1, "diagonal"), 1);
            yield return UntilCutsEnd(root, 30f, "the cut");
            ConfigurableJoint[] joints = Object.FindObjectsByType<ConfigurableJoint>(FindObjectsSortMode.None);
            Assert.That(joints.Length, Is.EqualTo(1));
            ConfigurableJoint joint = joints[0];
            Vector3 axisWorld = joint.transform.TransformDirection(joint.axis).normalized;
            Vector3 anchorWorld = joint.transform.TransformPoint(joint.anchor), connectedWorld = joint.connectedBody.transform.TransformPoint(joint.connectedAnchor);
            float along = Vector3.Dot(anchorWorld - connectedWorld, axisWorld);
            TestContext.Out.WriteLine("at the publication: anchor - connected anchor along the axis " + along.ToString("R") + " m, limit " + joint.linearLimit.limit.ToString("R") + " m, connected anchor " + joint.connectedAnchor.ToString("G9"));
            Assert.That(joint.linearLimit.limit, Is.EqualTo(StageOpening * 0.5f).Within(1e-7f), "the limit is half the opening");
            Assert.That(along, Is.EqualTo(-StageOpening * 0.5f).Within(1e-5f), "published at the inward boundary: no constraint error");
            Assert.That(joint.enableCollision, Is.False);
            Assert.That(joint.xMotion == ConfigurableJointMotion.Limited && joint.yMotion == ConfigurableJointMotion.Free && joint.zMotion == ConfigurableJointMotion.Free && joint.angularXMotion == ConfigurableJointMotion.Free && joint.angularYMotion == ConfigurableJointMotion.Locked && joint.angularZMotion == ConfigurableJointMotion.Locked, Is.True, "the Provisional's freedoms");
            yield return Steps(3);
            Assert.That(h.PairsMoving, Is.EqualTo(1), "still moving");
            yield return EndWorld(root);
            Assert.That(Object.FindObjectsByType<ConfigurableJoint>(FindObjectsSortMode.None).Length, Is.Zero, "the constraint removed by the end");
            Assert.That(Object.FindObjectsByType<Rigidbody>(FindObjectsSortMode.None).Length, Is.Zero);
            Assert.That(Object.FindObjectsByType<MeshCollider>(FindObjectsSortMode.None).Length, Is.Zero);
        }

        /// <summary>
        /// **The motion time runs on the real clock: physics steps skipped for their cost do not hold the stop.** The pair is
        /// published with the trial's clock held; the physics is then made to skip (its expected cost past the budget) and the
        /// clock moved past the motion time: the pair is fixed at the next Step with no physics step taken since the
        /// publication (the record says so), the constraint removed. The physics' cost estimate is restored after.
        /// </summary>
        [UnityTest]
        public IEnumerator HullStage_TheMotionTimeRunsWhilePhysicsStepsAreSkipped()
        {
            CutWorldRoot root = NewStageWorld(true);
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            double clock = 0.0;
            h.RealSecondsForTest = () => clock;
            Evaluate(detector, StageCut(1, "diagonal"), 1);
            yield return UntilCutsEnd(root, 30f, "the cut");
            Assert.That(h.PairsMoving == 1 && h.ConstraintsLive == 1, Is.True, "the pair moves, held by its constraint");
            try
            {
                CutPhysicsStep.InjectExpectedCostForTest(1.0);   // every decision skips for the cost (no verification step before 45 skips)
                long steps = CutPhysicsStep.Clock.StepId;
                yield return null;
                yield return null;
                Assert.That(CutPhysicsStep.Clock.StepId, Is.EqualTo(steps), "the physics skips");
                clock = StageSeconds + 0.01;
                yield return null;
                yield return null;
                Assert.That(CutPhysicsStep.Clock.StepId, Is.EqualTo(steps), "still no physics step");
                Assert.That(h.PairsMoving, Is.Zero, "the pair was fixed by the real clock");
                Assert.That(h.StopsByDeadline, Is.EqualTo(1));
                Assert.That(h.ConstraintsLive, Is.Zero, "its constraint removed");
                TestContext.Out.WriteLine(h.StageRecords[0]);
                Assert.That(System.Text.RegularExpressions.Regex.IsMatch(h.StageRecords[0], " [01] steps,"), Is.True, "at most the publication frame's own step before the stop: " + h.StageRecords[0]);
            }
            finally
            {
                CutPhysicsStep.InjectExpectedCostForTest(0.0);
                CutPhysicsStep.RequestCostReevaluation();
            }

            yield return EndWorld(root);
        }

        /// <summary>**A sliver side's mass properties are positive and finite, on the group and on its body**, read back in full digits (the Player's "mass 0.0" was a rounding of such a side).</summary>
        [UnityTest]
        public IEnumerator HullStage_SliverSide_MassAndInertiaPositiveOnGroupAndBody()
        {
            CutWorldRoot root = NewHullWorld();
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            Evaluate(detector, Upright(1, 0.999f), 1);
            yield return UntilCutsEnd(root, 30f, "the sliver cut");
            Assert.That(h.GroupCuts, Is.EqualTo(1), h.Hits[0].outcome);
            foreach (HullGroup g in h.Groups)
            {
                TestContext.Out.WriteLine("group " + g.Id + ": mass " + g.Mass.ToString("R") + " body " + g.Body.mass.ToString("R") + ", inertia " + ((float3)g.Inertia) + " body " + g.Body.inertiaTensor.ToString("G9"));
                Assert.That(g.Mass > 0.0 && !double.IsNaN(g.Mass) && !double.IsInfinity(g.Mass), Is.True, "group " + g.Id + ": a positive finite mass");
                Assert.That(g.Body.mass, Is.EqualTo((float)g.Mass), "group " + g.Id + ": the body carries it");
                Assert.That(math.all(g.Inertia > 0f) && math.all(math.isfinite(g.Inertia)), Is.True, "group " + g.Id + ": a positive finite inertia");
                Assert.That((float3)g.Body.inertiaTensor, Is.EqualTo(g.Inertia), "group " + g.Id + ": the body carries it");
            }

            yield return EndWorld(root);
        }
    }
}
