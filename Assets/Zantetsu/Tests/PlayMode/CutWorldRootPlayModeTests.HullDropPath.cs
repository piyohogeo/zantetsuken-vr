using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The always-kinematic building's drop path (TL, 2026-10-01): x(t) = x0 + (t / T) d + 1/2 g t (t - T), t clamped to
    /// [0, T], g the game's gravity and T fixed at the start, computed from the time alone. Level, diagonal and vertical
    /// cuts, a flipped normal and a turned building: every member on the curve at every frame (the slide's share in the plane
    /// and the world's vertical arc), x0 + d at the end with no arc left, rotations unchanged; a small D(n) keeps the whole
    /// arc; a re-cut stops a drop on its curve and the children start a new curve from there; the last drop after the limit
    /// runs to its end, its hull update refused or not; the world's end with a drop running leaves it cleared. The clock is
    /// advanced once a frame (10 ms) and never within a frame.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const double ArcAtMiddle = 9.81 * 0.25 * 0.25 / 8.0;   // 1/2 g (T/2)(T/2), about 7.66 cm

        private sealed class PathReads
        {
            public int reads, mid;
            public double maxOff, maxRotation, maxArcUp;
            public readonly List<string> lines = new List<string>();
        }

        /// <summary>Every running drop's members against the curve, in the world: start + u d + 1/2 g t (t - T), and their rotations against the start.</summary>
        private static void ReadPaths(BuildingHullFusion h, PathReads p)
        {
            foreach (BuildingHullFusion.DropRecord r in h.DropRecords)
            {
                if (r.end != null) continue;
                double t = r.appliedSeconds, u = r.Phase;
                Vector3 arcWorld = (Vector3)r.gravityWorld * (float)(0.5 * t * (t - r.seconds));
                foreach (BuildingHullFusion.DropMember m in r.members)
                {
                    if (m.root == null) continue;
                    Transform parent = m.root.parent;
                    Vector3 startWorld = parent.TransformPoint(m.from);
                    Vector3 expected = startWorld + (Vector3)r.deltaWorld * (float)u + arcWorld;
                    double off = (m.root.position - expected).magnitude;
                    p.maxOff = System.Math.Max(p.maxOff, off);
                    p.maxRotation = System.Math.Max(p.maxRotation, Quaternion.Angle(m.root.localRotation, m.rotation));
                    p.reads++;
                    if (u > 0.4 && u < 0.6) { p.mid++; p.maxArcUp = System.Math.Max(p.maxArcUp, Vector3.Dot(m.root.position - (startWorld + (Vector3)r.deltaWorld * (float)u), Vector3.up)); }
                }
            }
        }

        private IEnumerator UntilDrops(CutWorldRoot root, System.Func<bool> done, PathReads p, string what)
        {
            for (int i = 0; i < 600 && !done(); i++)
            {
                yield return null;
                ReadPaths(root.Hulls, p);
            }

            Assert.That(done(), Is.True, what + " (" + root.Hulls.DescribeUnsettled() + ")");
        }

        private static void WriteDrops(BuildingHullFusion h, string what)
        {
            TestContext.Out.WriteLine(what + ":");
            foreach (BuildingHullFusion.DropRecord r in h.DropRecords)
                TestContext.Out.WriteLine("  drop " + r.id + " hit " + r.hit + " group " + r.group + ": g " + ((Vector3)r.gravityWorld).ToString("R") + ", T " + r.seconds.ToString("R") + ", d " + ((Vector3)r.deltaWorld).ToString("R") + ", start " + r.start.ToString("R")
                    + ", applied t " + r.appliedSeconds.ToString("R") + " (u " + r.Phase.ToString("R") + ", " + r.placements + " placements), " + (r.end ?? "running") + ", members " + r.members.Count);
        }

        /// <summary>Each member of a completed drop stands at x0 + d (no arc left), unturned.</summary>
        private static void AssertCompletedAtEnd(BuildingHullFusion.DropRecord r, string what)
        {
            Assert.That(r.end, Is.EqualTo("completed"), what + ": completed");
            Assert.That(r.appliedSeconds, Is.EqualTo(r.seconds), what + ": placed at T");
            foreach (BuildingHullFusion.DropMember m in r.members)
            {
                if (m.root == null) continue;
                Vector3 end = m.root.parent.TransformPoint(m.from) + (Vector3)r.deltaWorld;
                Assert.That((m.root.position - end).magnitude, Is.LessThan(1e-5f), what + ": at x0 + d, no arc left");
                Assert.That(Quaternion.Angle(m.root.localRotation, m.rotation), Is.LessThan(1e-3f), what + ": not turned");
            }
        }

        /// <summary>**The formula: x0 at 0, x0 + d/2 lifted by g T^2 / 8 at T/2, x0 + d at T, t clamped outside [0, T].**</summary>
        [Test]
        public void HullDrop_TheFormula_StartMiddleEnd_AndTheClamp()
        {
            Vector3 x0 = new Vector3(1f, 2f, 3f), d = new Vector3(0.15f, 0f, 0f), g = new Vector3(0f, -9.81f, 0f);
            const double T = 0.25;
            Assert.That((BuildingHullFusion.DropPosition(x0, d, g, T, 0.0) - x0).magnitude, Is.LessThan(1e-7f), "x0 at 0");
            Vector3 middle = BuildingHullFusion.DropPosition(x0, d, g, T, T / 2);
            Assert.That((middle - (x0 + d * 0.5f + new Vector3(0f, (float)ArcAtMiddle, 0f))).magnitude, Is.LessThan(1e-6f), "x0 + d/2, lifted by the arc at the middle");
            Assert.That(ArcAtMiddle, Is.EqualTo(0.0766).Within(0.0001), "about 7.7 cm for 9.81 m/s2 and 0.25 s");
            Assert.That(BuildingHullFusion.DropPosition(x0, d, g, T, T), Is.EqualTo(x0 + d), "x0 + d exactly at T");
            Assert.That(BuildingHullFusion.DropPosition(x0, d, g, T, -0.1), Is.EqualTo(x0), "clamped below 0");
            Assert.That(BuildingHullFusion.DropPosition(x0, d, g, T, 1.0), Is.EqualTo(x0 + d), "clamped above T");
            double t = 0.05;
            Vector3 general = x0 + d * (float)(t / T) + g * (float)(0.5 * t * (t - T));
            Assert.That((BuildingHullFusion.DropPosition(x0, d, g, T, t) - general).magnitude, Is.LessThan(1e-6f), "the formula at 0.05 s");
        }

        /// <summary>
        /// **Level, diagonal and vertical cuts, a flipped normal and a turned building: on the curve at every frame, lifted
        /// at the middle by the world's arc, x0 + d at the end, nothing turned.**
        /// </summary>
        [UnityTest]
        public IEnumerator HullDrop_LevelDiagonalVerticalFlippedAndTurned_StandOnTheCurve_AndEndAtX0PlusD()
        {
            CutWorldRoot root = NewKinematicWorld();
            BuildingHullFusion h = root.Hulls;
            float3 anchor = new float3(-0.5f, -0.9f, 0f);
            HullGroup level = AddHullBuilding(root, Vector3.zero, new[] { anchor }, 12.0, out _);
            HullGroup diagonal = AddHullBuilding(root, new Vector3(20f, 0f, 0f), new[] { anchor }, 12.0, out _);
            HullGroup flipped = AddHullBuilding(root, new Vector3(40f, 0f, 0f), new[] { anchor }, 12.0, out _);
            HullGroup turned = AddHullBuildingPosed(root, new Vector3(60f, 0.5f, 5f), Quaternion.Euler(0f, 30f, 10f), new[] { anchor }, 12.0, out _);
            HullGroup vertical = AddHullBuilding(root, new Vector3(80f, 0f, 0f), new[] { anchor }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            FrameClock(h);
            Quaternion bodyTurned = turned.Body.rotation;
            Vector3 n = math.normalize(new float3(1f, 1f, 0f)), along = math.normalize(new float3(1f, -1f, 0f));
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            Evaluate(detector, Wide(2, n, new Vector3(20.2f, 0f, 0f), along, Vector3.forward), 2);
            Evaluate(detector, Wide(3, -n, new Vector3(40.2f, 0f, 0f), along, Vector3.forward), 3);
            Evaluate(detector, Wide(4, n, turned.Members[0].root.transform.position + new Vector3(0.2f, 0f, 0f), along, Vector3.forward), 4);
            Evaluate(detector, Upright(5, 80.3f), 5);
            var p = new PathReads();
            yield return UntilDrops(root, () => h.DropRecords.Count == 5 && h.AnimationsCompleted == 5 && Quiet(h), p, "five drops completed and settled");
            WriteDrops(h, "level, diagonal, flipped, turned, vertical");
            TestContext.Out.WriteLine("read " + p.reads + " times (" + p.mid + " near the middle): most off the curve " + p.maxOff.ToString("R") + " m, the arc up at most " + p.maxArcUp.ToString("R") + " m, rotation at most " + p.maxRotation.ToString("R") + " deg");
            Assert.That(p.reads, Is.GreaterThan(20), "read part way");
            Assert.That(p.maxOff, Is.LessThan(1e-5), "on the curve at every frame");
            Assert.That(p.maxArcUp, Is.EqualTo(ArcAtMiddle).Within(0.005), "lifted by the arc near the middle (u in 0.4..0.6)");
            Assert.That(p.maxRotation, Is.LessThan(1e-3), "nothing turned");
            foreach (BuildingHullFusion.DropRecord r in h.DropRecords)
            {
                AssertCompletedAtEnd(r, "drop " + r.id);
                Assert.That((Vector3)r.gravityWorld, Is.EqualTo(Physics.gravity), "g fixed from the game's gravity");
                Assert.That(r.seconds, Is.EqualTo(KinematicDropSeconds).Within(1e-6), "T");
                Assert.That(math.abs(math.dot(r.deltaWorld, r.normalWorld)), Is.LessThan(1e-5f), "d in the plane");
            }

            // The records come in their publication's order: each found by its building's group.
            BuildingHullFusion.DropRecord Of(HullGroup g) { foreach (BuildingHullFusion.DropRecord r in h.DropRecords) if (r.group == g.Id) return r; return null; }
            Assert.That(math.length(Of(flipped).deltaWorld - Of(diagonal).deltaWorld), Is.LessThan(1e-5f), "the flipped normal: the same d");
            Assert.That(Of(vertical).deltaWorld.y, Is.LessThan(0f), "the vertical cut slides down its plane");
            Assert.That(Of(level).deltaWorld.y, Is.EqualTo(0f).Within(1e-6f), "the level cut slides level (the arc is the only vertical)");
            Assert.That(Quaternion.Angle(turned.Body.rotation, bodyTurned), Is.LessThan(1e-4f), "the turned body not turned");
            Assert.That(h.MaxDropPositionError, Is.LessThan(1e-5), "the trial's own read-back at each end");
            AssertOneEach(root, 5, "at the end");
            yield return EndWorld(root);
        }

        /// <summary>**A small D(n): the whole arc all the same (it is not scaled by D(n)), and x0 + d at the end.**</summary>
        [UnityTest]
        public IEnumerator HullDrop_ASmallDOfN_KeepsTheWholeArc_AndEndsAtX0PlusD()
        {
            CutWorldRoot root = NewLimitWorld(16, 1.0, 0.005);
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            FrameClock(h);
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            var p = new PathReads();
            yield return UntilDrops(root, () => h.DropRecords.Count == 1 && h.DropRecords[0].end != null && Quiet(h), p, "the drop ended");
            WriteDrops(h, "a small D(n)");
            Assert.That(math.length(h.DropRecords[0].deltaWorld), Is.EqualTo(0.005f).Within(1e-6f), "D(1) = D0 = 5 mm");
            Assert.That(p.maxOff, Is.LessThan(1e-5));
            Assert.That(p.maxArcUp, Is.EqualTo(ArcAtMiddle).Within(0.005), "the arc is not scaled by D(n)");
            AssertCompletedAtEnd(h.DropRecords[0], "the small drop");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A re-cut stops the drop on its curve (its height kept), and the children start a new curve there: its own d, g
        /// and T, from its own start, nothing of the old one's time, speed or arc handed on.**
        /// </summary>
        [UnityTest]
        public IEnumerator HullDrop_AReCut_StopsOnTheCurve_AndTheChildrenStartANewCurveThere()
        {
            CutWorldRoot root = NewLimitWorld(16);
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            System.Func<double> clock = FrameClock(h);
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            var p = new PathReads();
            yield return UntilDrops(root, () => h.DropRecords.Count == 1 && h.DropRecords[0].Phase > 0.3 && h.DisplayOperationsOpen == 0, p, "the first drop a third of the way, its display committed");
            BuildingHullFusion.DropRecord first = h.DropRecords[0];
            Evaluate(detector, Upright(2, 0.1f), 2);
            yield return UntilDrops(root, () => h.DropRecords.Count == 2 && h.DropRecords[1].end != null && Quiet(h), p, "the re-cut's drop ended");
            WriteDrops(h, "re-cut on the curve");
            BuildingHullFusion.DropRecord second = h.DropRecords[1];
            Assert.That(first.end, Is.EqualTo("stopped"));
            Assert.That(first.Phase, Is.GreaterThan(0.0).And.LessThan(1.0), "part way");
            Assert.That(p.maxOff, Is.LessThan(1e-5), "both on their curves at every frame");
            // The stop's pose on the first curve, its height above the plane kept.
            foreach (BuildingHullFusion.DropMember m in first.members)
            {
                Assert.That(m.atRead, Is.True);
                Vector3 onCurve = BuildingHullFusion.DropPosition(m.from, first.deltaLocal, first.gravityLocal, first.seconds, first.appliedSeconds);
                Assert.That((m.at - onCurve).magnitude, Is.LessThan(1e-5f), "stopped on its curve");
                Assert.That(m.at.y - m.from.y, Is.GreaterThan(1e-3f), "lifted by the arc, and kept");
            }

            // The second curve starts where the stop left its members (a child where its source stood).
            int matched = 0, others = 0;
            foreach (BuildingHullFusion.DropMember m in second.members)
            {
                Vector3? at = null;
                foreach (BuildingHullFusion.DropMember s in first.members) if (s.fragment == m.fragment) at = s.at;
                foreach (CutOperationId op in h.Hits[1].displayOperations)
                {
                    Assert.That(root.Ledger.TryGetOperation(op, out LogicalCutOperation o), Is.True);
                    if (o.positive != m.fragment && o.negative != m.fragment) continue;
                    foreach (BuildingHullFusion.DropMember s in first.members) if (s.fragment == o.source) at = s.at;
                }

                // A child of the side that did not drop comes from no stopped drop: it starts where its source stood still.
                if (!at.HasValue) { others++; continue; }
                Assert.That((m.from - at.Value).magnitude, Is.LessThan(1e-5f), "member " + m.fragment.value + ": its new curve starts at the stop's pose");
                matched++;
            }

            TestContext.Out.WriteLine("the second drop's members: from the stopped drop " + matched + ", from the side that stood " + others);
            Assert.That(matched, Is.GreaterThan(0), "a member of the stopped drop runs the new curve");
            Assert.That(second.start, Is.GreaterThan(first.start), "its own start");
            Assert.That(second.seconds, Is.EqualTo(first.seconds), "its own T (the same setting)");
            Assert.That(math.abs(math.dot(second.deltaWorld, new float3(1f, 0f, 0f))), Is.LessThan(1e-5f), "its own d, in the upright plane");
            AssertCompletedAtEnd(second, "the new curve");
            yield return EndWorld(root);
        }

        /// <summary>**The last drop after the limit runs to its end at x0 + d, its hull update refused; the old hull stays and the building is cut no more.**</summary>
        [UnityTest]
        public IEnumerator HullDrop_TheLastDropAfterTheLimit_RunsToItsEnd_ItsHullUpdateRefused()
        {
            CutWorldRoot root = NewLimitWorld(2);
            BuildingHullFusion h = root.Hulls;
            HullGroup building = AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            FrameClock(h);
            h.refuseHullForTest = true;
            int generation = building.HullGeneration;
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            var p = new PathReads();
            yield return UntilDrops(root, () => h.DropRecords.Count == 1 && h.DropRecords[0].end != null && Quiet(h), p, "the last drop ended");
            WriteDrops(h, "the last drop");
            Assert.That(h.IsCutStopped(building.Building), Is.True, "n = 2 = N: stopped");
            AssertCompletedAtEnd(h.DropRecords[0], "the last drop");
            Assert.That(p.maxOff, Is.LessThan(1e-5));
            Assert.That(h.HullUpdatesRefused, Is.GreaterThanOrEqualTo(1), "its hull update refused");
            Assert.That(building.HullGeneration, Is.EqualTo(generation), "the old hull stays");
            h.refuseHullForTest = false;
            Evaluate(detector, Upright(2, 0.3f), 2);
            Assert.That(detector.HitCount, Is.Zero, "cut no more");
            yield return EndWorld(root);
        }

        /// <summary>**The world's end with a drop running: the drop is cleared, nothing left running.**</summary>
        [UnityTest]
        public IEnumerator HullDrop_TheWorldsEndWithADropRunning_ClearsIt()
        {
            CutWorldRoot root = NewLimitWorld(16);
            BuildingHullFusion h = root.Hulls;
            AddHullBuilding(root, Vector3.zero, new[] { new float3(-0.5f, -0.9f, 0f) }, 12.0, out _);
            var detector = new SlashHitDetector(root, in k_hitSettings);
            yield return null;
            FrameClock(h);
            Evaluate(detector, Level(1, 0.2f, -3f, 3f), 1);
            var p = new PathReads();
            yield return UntilDrops(root, () => h.DropRecords.Count == 1 && h.DropRecords[0].Phase > 0.2, p, "the drop under way");
            BuildingHullFusion.DropRecord r = h.DropRecords[0];
            Assert.That(r.end, Is.Null, "running");
            yield return EndWorld(root);
            Assert.That(r.end, Is.EqualTo("cleared"), "cleared with the world");
            Assert.That(h.AnimationsRunning, Is.Zero);
        }
    }
}
