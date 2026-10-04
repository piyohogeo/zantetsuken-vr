using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The fragments' world box in Find (TL, 2026-10-04): a fragment's shape passed over by its world box is one the test
    /// in its own frame would not have hit, and the frame kept for an update's sweeps is the one a reading at each sweep
    /// gives. Three cases. In a world being cut by sweeps turned any way, every shape passed over takes the frame test all
    /// the same and none is a hit, and no kept frame differs from the one read again. Over a standing world, sweeps that
    /// touch (a hit the acceptance answers as a no-op, which changes nothing) and sweeps that miss give the same hits in
    /// the same order with the box off and on. And the cost of one update over many shapes, both ways, in this Editor --
    /// written out, not judged.
    /// </summary>
    public partial class CutWorldRootPlayModeTests
    {
        private List<LogicalFragmentId> AddBodyGrid(CutWorldRoot root, int count, int columns, float pitch, bool tilted)
        {
            var bodies = new List<LogicalFragmentId>();
            for (int i = 0; i < count; i++)
            {
                LogicalFragmentId body = AddBody(root, new Vector3(pitch * (i % columns), 0f, pitch * (i / columns)));
                bodies.Add(body);
                Assert.That(root.Owners.TryGet(body, out PhysicsFragmentOwner owner), Is.True);
                // Every third one turned about the vertical (its top stays level); with tilted, every fourth tilted too.
                if (i % 3 == 1) owner.Root.transform.rotation = Quaternion.Euler(0f, 17f * i, 0f);
                if (tilted && i % 4 == 2) owner.Root.transform.rotation = Quaternion.Euler(11f * i, 23f * i, 5f * i);
            }

            return bodies;
        }

        [UnityTest]
        public IEnumerator FragmentBox_PassesOverOnlyWhatTheFrameTestWouldNotHit_InAWorldBeingCut()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;   // the Main budget is not this case's subject
            AddBodyGrid(root, 16, 4, 4f, true);
            yield return null;
            var detector = new SlashHitDetector(root, in k_hitSettings) { fragmentBoxCheckForTest = true };
            var random = new System.Random(20261004);
            int hits = 0, published = 0, sweepsGiven = 0;
            for (int round = 0; round < 36; round++)
            {
                // One to three sweeps an update, of one Slash: quads of many sizes somewhere over the grid, turned any way.
                long slash = round + 1;
                var sweeps = new SlashSweep[1 + round % 3];
                for (int k = 0; k < sweeps.Length; k++)
                {
                    var centre = new Vector3((float)(random.NextDouble() * 16.0 - 2.0), (float)(random.NextDouble() * 3.0 - 1.5), (float)(random.NextDouble() * 16.0 - 2.0));
                    Quaternion turn = Quaternion.Euler((float)(random.NextDouble() * 360.0), (float)(random.NextDouble() * 360.0), (float)(random.NextDouble() * 360.0));
                    sweeps[k] = Quad(slash, centre, turn, 0.5f + (float)(random.NextDouble() * 5.5), 0.2f + (float)(random.NextDouble() * 4.8));
                }

                detector.Evaluate(sweeps, new[] { slash });
                sweepsGiven += sweeps.Length;
                for (int i = 0; i < detector.HitCount; i++)
                {
                    hits++;
                    if (detector.HitAt(i).Acceptance == ProvisionalCutAcceptance.Published) published++;
                }

                Assert.That(detector.FragmentBoxDisagreements, Is.Zero, "round " + round + ": a shape passed over by its box was a hit in its frame");
                Assert.That(detector.FragmentFrameDifferences, Is.Zero, "round " + round + ": a kept frame is not the one read again");
                for (int f = 0; f < 3; f++) yield return null;   // the cuts go on in the ordinary frames
            }

            TestContext.Out.WriteLine("sweeps " + sweepsGiven + ", sweep-by-shape tests passed over by the box " + detector.FragmentsPassedOver
                + " (each tested in its frame all the same: hits among them " + detector.FragmentBoxDisagreements + "), hits passed on " + hits + " (published " + published
                + "), kept frames differing from a reading " + detector.FragmentFrameDifferences);
            Assert.That(detector.FragmentsPassedOver, Is.GreaterThan(0), "the box did pass shapes over");
            Assert.That(published, Is.GreaterThan(4), "the world was cut while it ran");
            for (int f = 0; f < 30; f++) yield return null;
            yield return EndWorld(root);
        }

        private static string HitText(SlashHitConfirmed h) =>
            h.SlashId + " " + h.Fragment.value + " side " + h.Side + " " + h.Acceptance + "/" + h.Admission + " at " + h.At + " latch " + h.AtLatch + " op " + h.Operation.IsSet;

        [UnityTest]
        public IEnumerator FragmentBox_GivesTheSameHitsInTheSameOrder_AsEveryShapeTestedInItsFrame()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            List<LogicalFragmentId> bodies = AddBodyGrid(root, 16, 4, 4f, false);
            yield return null;
            var old = new SlashHitDetector(root, in k_hitSettings) { fragmentBoxReject = false };
            var now = new SlashHitDetector(root, in k_hitSettings);
            var random = new System.Random(7);
            int hits = 0, updates = 0;
            for (int round = 0; round < 60; round++)
            {
                // Level sweeps in the plane of the boxes' tops (a touch: a hit, answered as a no-op, changing nothing), over
                // ranges that hold some boxes, end exactly on a box's face, or hold none; and sweeps above every box.
                long slash = round + 1;
                var sweeps = new SlashSweep[1 + round % 3];
                for (int k = 0; k < sweeps.Length; k++)
                {
                    float x0 = round % 5 == 0 ? -1f + 4f * random.Next(0, 4) : (float)(random.NextDouble() * 18.0 - 3.0);
                    float z0 = round % 7 == 0 ? 1f + 4f * random.Next(0, 4) : (float)(random.NextDouble() * 18.0 - 3.0);
                    float height = (round + k) % 4 == 3 ? 1.5f : 1f;
                    sweeps[k] = Level(slash, height, x0, x0 + (float)(random.NextDouble() * 9.0), z0, z0 + (float)(random.NextDouble() * 9.0));
                }

                old.Evaluate(sweeps, new[] { slash });
                var oldHits = Hits(old).ConvertAll(HitText);
                now.Evaluate(sweeps, new[] { slash });
                var nowHits = Hits(now).ConvertAll(HitText);
                Assert.That(nowHits, Is.EqualTo(oldHits), "round " + round + ": the same hits in the same order");
                foreach (SlashHitConfirmed h in Hits(old)) Assert.That(h.Acceptance, Is.EqualTo(ProvisionalCutAcceptance.EmptySide), "round " + round + ": a touch, answered as a no-op");
                hits += oldHits.Count;
                updates++;
            }

            foreach (LogicalFragmentId body in bodies) Assert.That(root.Ledger.IsCurrentTarget(body), Is.True, "nothing was cut");
            TestContext.Out.WriteLine("updates " + updates + ", hits each way " + hits + "; passed over by the box: off " + old.FragmentsPassedOver + ", on " + now.FragmentsPassedOver);
            Assert.That(hits, Is.GreaterThan(10), "the sweeps did hit");
            Assert.That(old.FragmentsPassedOver, Is.Zero);
            Assert.That(now.FragmentsPassedOver, Is.GreaterThan(0));
            yield return EndWorld(root);
        }

        private static double MedianOf(List<double> v) { v.Sort(); return v[v.Count / 2]; }

        [UnityTest]
        public IEnumerator FragmentBox_Cost_OneUpdateOverManyShapes_BothWays()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            const int shapes = 192;
            AddBodyGrid(root, shapes, 16, 4f, true);
            yield return null;
            var old = new SlashHitDetector(root, in k_hitSettings) { fragmentBoxReject = false };
            var now = new SlashHitDetector(root, in k_hitSettings);
            var random = new System.Random(11);
            const int updates = 400;
            var given = new SlashSweep[updates][];
            for (int u = 0; u < updates; u++)
            {
                // A wave some 3 m wide moving 0.4 m an update above the boxes (it meets none, so nothing changes and every
                // update is the same work both ways); every third update carries two sweeps.
                given[u] = new SlashSweep[u % 3 == 0 ? 2 : 1];
                for (int k = 0; k < given[u].Length; k++)
                {
                    var c = new Vector3((float)(random.NextDouble() * 60.0), 2.5f, (float)(random.NextDouble() * 44.0));
                    given[u][k] = Quad(u + 1, c, Quaternion.Euler(0f, (float)(random.NextDouble() * 360.0), 0f), 3f, 0.4f);
                }
            }

            var live = new long[1];
            var evaluate = new[] { new List<double>(), new List<double>() };
            var collect = new[] { new List<double>(), new List<double>() };
            var list = new List<CurrentShape>(shapes);
            double ticks = 1e6 / System.Diagnostics.Stopwatch.Frequency;
            try
            {
                for (int round = 0; round < 9; round++)
                {
                    for (int k = 0; k < 2; k++)
                    {
                        int way = (round + k) % 2;   // the order alternates
                        PhysicsFragmentOwner.rootTransformReadAgainForTest = way == 0;
                        SlashHitDetector detector = way == 0 ? old : now;
                        long begin = System.Diagnostics.Stopwatch.GetTimestamp();
                        for (int u = 0; u < updates; u++)
                        {
                            live[0] = u + 1;
                            detector.Evaluate(given[u], live);
                            Assert.That(detector.HitCount, Is.Zero);
                        }

                        evaluate[way].Add((System.Diagnostics.Stopwatch.GetTimestamp() - begin) * ticks / updates);
                        begin = System.Diagnostics.Stopwatch.GetTimestamp();
                        for (int u = 0; u < updates; u++) root.Owners.CollectCurrentShapes(list);
                        collect[way].Add((System.Diagnostics.Stopwatch.GetTimestamp() - begin) * ticks / updates);
                        Assert.That(list.Count, Is.EqualTo(shapes));
                    }
                }
            }
            finally
            {
                PhysicsFragmentOwner.rootTransformReadAgainForTest = false;
            }

            TestContext.Out.WriteLine(shapes + " shapes, " + updates + " updates (one in three with two sweeps), sweeps that meet nothing; microseconds an update, medians of 9, old -> new: "
                + "Evaluate (collection, lists and the fragments' test) " + MedianOf(evaluate[0]).ToString("F1") + " -> " + MedianOf(evaluate[1]).ToString("F1")
                + "; of it the collection alone " + MedianOf(collect[0]).ToString("F1") + " -> " + MedianOf(collect[1]).ToString("F1")
                + "; passed over by the box, on: " + now.FragmentsPassedOver);
            Assert.That(old.FragmentsPassedOver, Is.Zero);
            yield return EndWorld(root);
        }
    }
}
