using System;
using NUnit.Framework;
using Zantetsu.Core.MobPlan;

namespace Zantetsu.Core.Tests
{
    /// <summary>PROP-OBSTACLE-1: the prop fragments' map -- sections, cells, distances, versions -- on numbers alone.</summary>
    public sealed class PropObstacleMapTests
    {
        private static readonly int[] BoxEdges = { 0, 1, 1, 3, 3, 2, 2, 0, 4, 5, 5, 7, 7, 6, 6, 4, 0, 4, 1, 5, 2, 6, 3, 7 };

        /// <summary>A shape of boxes (lo, hi triples), each one convex of eight vertices and twelve edges.</summary>
        private static PropShape Boxes(int id, params float[] loHi)
        {
            var count = loHi.Length / 6;
            var vertices = new float[count * 24]; var edges = new int[count * 24]; var bounds = new float[count * 6];
            var vertexStart = new int[count + 1]; var edgeStart = new int[count + 1];
            for (var c = 0; c < count; c++)
            {
                for (var v = 0; v < 8; v++)
                {
                    vertices[(c * 8 + v) * 3] = loHi[c * 6 + ((v & 1) != 0 ? 3 : 0)];
                    vertices[(c * 8 + v) * 3 + 1] = loHi[c * 6 + ((v & 2) != 0 ? 4 : 1)];
                    vertices[(c * 8 + v) * 3 + 2] = loHi[c * 6 + ((v & 4) != 0 ? 5 : 2)];
                }

                Array.Copy(BoxEdges, 0, edges, c * 24, 24);
                Array.Copy(loHi, c * 6, bounds, c * 6, 6);
                vertexStart[c + 1] = (c + 1) * 8; edgeStart[c + 1] = (c + 1) * 12;
            }

            return new PropShape(id, vertices, vertexStart, edges, edgeStart, bounds);
        }

        private static void Identity(float[] m, int o, float x = 0f, float y = 0f, float z = 0f)
        {
            Array.Clear(m, o, 12);
            m[o] = 1f; m[o + 5] = 1f; m[o + 10] = 1f; m[o + 3] = x; m[o + 7] = y; m[o + 11] = z;
        }

        /// <summary>A rotation about the z axis by `degrees` (a trunk tipping over in the x, y plane), then a translation.</summary>
        private static void TiltZ(float[] m, int o, float degrees, float x, float y, float z)
        {
            var c = (float)Math.Cos(degrees * Math.PI / 180.0); var s = (float)Math.Sin(degrees * Math.PI / 180.0);
            Array.Clear(m, o, 12);
            m[o] = c; m[o + 1] = -s; m[o + 3] = x;
            m[o + 4] = s; m[o + 5] = c; m[o + 7] = y;
            m[o + 10] = 1f; m[o + 11] = z;
        }

        private static PropObstacleInput Input(float originX = 0f, float originZ = 0f, int cells = 100, float cell = 0.2f, float plane = 1.5f)
        {
            var input = new PropObstacleInput();
            input.SetWindow(new PropObstacleSettings { cells = cells, cellMeters = cell, planeHeightMeters = plane, maxDistanceMeters = 3f }, originX, originZ);
            return input;
        }

        private static float Area(float[] polygon, int n)
        {
            var area = 0f;
            for (var i = 0; i < n; i++)
            {
                var j = (i + 1) % n;
                area += polygon[i * 2] * polygon[j * 2 + 1] - polygon[j * 2] * polygon[i * 2 + 1];
            }

            return area * 0.5f;
        }

        [Test]
        public void Slice_AnUprightBox_IsItsFootprint_CounterClockwise()
        {
            var shape = Boxes(1, -0.5f, 0f, -0.25f, 0.5f, 3f, 0.25f);
            var m = new float[12]; Identity(m, 0, 10f, 0f, 10f);
            var builder = new PropObstacleBuilder();
            var polygon = new float[16];
            var n = builder.Slice(shape, 0, m, 0, 1.5f, ref polygon);
            Assert.That(n, Is.EqualTo(4), "four edges cross the plane");
            Assert.That(Area(polygon, n), Is.EqualTo(0.5f).Within(1e-4f), "1 x 0.5 m, positive: counter-clockwise");
            for (var i = 0; i < n; i++)
            {
                Assert.That(Math.Abs(polygon[i * 2] - 10f), Is.EqualTo(0.5f).Within(1e-5f));
                Assert.That(Math.Abs(polygon[i * 2 + 1] - 10f), Is.EqualTo(0.25f).Within(1e-5f));
            }
        }

        [Test]
        public void Slice_ATiltedTrunk_IsLongerAlongTheTilt_AndAPlaneThatMissesGivesNothing()
        {
            // a 0.4 m square trunk 6 m long, tipped 60 degrees from upright: the section is 0.4 / cos(60) = 0.8 m along x
            var shape = Boxes(1, -0.2f, 0f, -0.2f, 0.2f, 6f, 0.2f);
            var m = new float[12]; TiltZ(m, 0, 60f, 0f, 0f, 0f);
            var builder = new PropObstacleBuilder();
            var polygon = new float[16];
            var n = builder.Slice(shape, 0, m, 0, 1.5f, ref polygon);
            Assert.That(n, Is.EqualTo(4));
            Assert.That(Area(polygon, n), Is.EqualTo(0.8f * 0.4f).Within(1e-3f));
            Assert.That(builder.Slice(shape, 0, m, 0, 4f, ref polygon), Is.EqualTo(0), "the trunk's top is at 6 cos 60 + 0.2 sin 60 = 3.17 m: a plane at 4 m misses it");
            // lying flat (90 degrees): everything is below 1.5 m
            TiltZ(m, 0, 90f, 0f, 0f, 0f);
            Assert.That(builder.Slice(shape, 0, m, 0, 1.5f, ref polygon), Is.EqualTo(0));
        }

        [Test]
        public void Build_MarksTheSectionsCells_AndGivesDistances_NothingOutsideTheWindow()
        {
            var input = Input();
            var i = input.Add(Boxes(1, -0.5f, 0f, -0.5f, 0.5f, 3f, 0.5f), 7, 3, 0);
            Identity(input.Matrices, i * 12, 10.1f, 0f, 10.1f);   // x, z in [9.6, 10.6]: five cells a side by their centres
            var map = new PropObstacleMap();
            var sections = new PropSectionList();
            new PropObstacleBuilder().Build(input, null, map, sections);
            Assert.That(sections.Count, Is.EqualTo(1));
            Assert.That(map.Stats.Sections, Is.EqualTo(1));
            Assert.That(map.OccupiedCells, Is.InRange(25, 49), "the footprint's cells and at most the ring half a cell around it");
            Assert.That(map.OccupiedAt(10.1f, 10.1f) && map.DistanceAt(10.1f, 10.1f) == 0f, Is.True, "inside: 0");
            Assert.That(map.DistanceAt(12.1f, 10.1f), Is.EqualTo(1.5f).Within(0.21f), "1.5 m from the face, to a cell");
            Assert.That(map.Overlaps(10.9f, 10.1f, 0.45f), Is.True, "a 0.45 m circle 0.3 m from the face");
            Assert.That(map.Overlaps(11.6f, 10.1f, 0.45f), Is.False, "the same circle 1 m from the face");
            Assert.That(map.DistanceAt(19f, 19f), Is.EqualTo(3f).Within(1e-4f), "kept up to the cap");
            Assert.That(float.IsPositiveInfinity(map.DistanceAt(-1f, 10f)) && float.IsPositiveInfinity(map.DistanceAt(10f, 20.5f)), Is.True, "outside the window the map says nothing");
            Assert.That(map.Overlaps(-1f, 10f, 5f), Is.False);
            Assert.That(map.BoxNear(9f, 9.2f, 9f, 9.2f, 0.5f) && !map.BoxNear(2f, 3f, 2f, 3f, 0.5f), Is.True);
        }

        [Test]
        public void Build_ALowPiece_HasNoSection_AndIsCounted()
        {
            var input = Input();
            var low = input.Add(Boxes(1, -1f, 0f, -1f, 1f, 0.6f, 1f), 1, 1, 0);
            Identity(input.Matrices, low * 12, 5f, 0f, 5f);
            var high = input.Add(Boxes(2, -1f, 2f, -1f, 1f, 3f, 1f), 2, 2, 0);
            Identity(input.Matrices, high * 12, 12f, 0f, 12f);
            var far = input.Add(Boxes(3, -1f, 0f, -1f, 1f, 3f, 1f), 3, 3, 0);
            Identity(input.Matrices, far * 12, 80f, 0f, 80f);
            var map = new PropObstacleMap();
            new PropObstacleBuilder().Build(input, null, map);
            Assert.That(map.OccupiedCells, Is.EqualTo(0));
            Assert.That(map.Stats.BelowPlane, Is.EqualTo(1));
            Assert.That(map.Stats.ItemsBelowPlane, Is.EqualTo(1), "the low piece: wholly below the plane");
            Assert.That(map.Stats.AbovePlane, Is.EqualTo(1));
            Assert.That(map.Stats.OutOfWindow, Is.EqualTo(1));
            Assert.That(float.IsPositiveInfinity(map.DistanceAt(5f, 5f)), Is.True, "nothing occupied: no obstacle anywhere");
        }

        [Test]
        public void Build_PiecesThatOverlap_AreOneOccupiedArea_AndAThinSectionIsNotLost()
        {
            var one = Input();
            var a = one.Add(Boxes(1, 0f, 0f, 0f, 2f, 3f, 1f), 1, 1, 0);
            Identity(one.Matrices, a * 12, 5f, 0f, 5f);
            var map1 = new PropObstacleMap();
            var builder = new PropObstacleBuilder();
            builder.Build(one, null, map1);

            var two = Input();
            a = two.Add(Boxes(1, 0f, 0f, 0f, 2f, 3f, 1f), 1, 1, 0);
            Identity(two.Matrices, a * 12, 5f, 0f, 5f);
            var b = two.Add(Boxes(2, 0f, 0f, 0f, 2f, 3f, 1f), 2, 1, 0);   // the same place again: a piece inside another
            Identity(two.Matrices, b * 12, 5f, 0f, 5f);
            var map2 = new PropObstacleMap();
            builder.Build(two, null, map2);
            Assert.That(map2.Stats.Sections, Is.EqualTo(2));
            Assert.That(map2.OccupiedCells, Is.EqualTo(map1.OccupiedCells), "two sections over the same cells: the same cells");

            // a branch 4 cm wide and 3 m long along x: thinner than a cell
            var thin = Input();
            var t = thin.Add(Boxes(3, 0f, 0f, 0f, 3f, 3f, 0.04f), 3, 3, 0);
            Identity(thin.Matrices, t * 12, 5.03f, 0f, 5.07f);
            var map3 = new PropObstacleMap();
            builder.Build(thin, null, map3);
            Assert.That(map3.OccupiedCells, Is.InRange(15, 34), "the row of cells along it (two rows where it runs near a cell border)");
            Assert.That(map3.Overlaps(6.5f, 5.09f, 0.1f), Is.True);
        }

        private sealed class HalfBlocked : WalkableMap
        {
            // everything with x < 10 is a static obstacle
            public override float ClearanceAt(float x, float z) => Math.Max(0f, x - 10f);
            public override bool IsWalkableAt(float x, float z) => x >= 10f;
        }

        [Test]
        public void Build_InsideTheStaticMap_IsLeftOut_AndWhatSticksOutStays()
        {
            var input = Input();
            var i = input.Add(Boxes(1, 0f, 0f, 0f, 4f, 3f, 1f), 1, 1, 0);
            Identity(input.Matrices, i * 12, 8f, 0f, 5f);   // x in [8, 12]: half in the static obstacle
            var mask = new byte[input.Cells * input.Cells];
            PropObstacleBuilder.StaticMask(new HalfBlocked(), input.OriginX, input.OriginZ, input.Cell, input.Cells, mask);
            var map = new PropObstacleMap();
            new PropObstacleBuilder().Build(input, mask, map);
            Assert.That(map.Stats.StaticSkippedCells, Is.GreaterThan(40), "the half inside the static obstacle was not written");
            Assert.That(map.OccupiedAt(9f, 5.5f), Is.False, "inside the static obstacle: the static map's");
            Assert.That(map.OccupiedAt(11f, 5.5f), Is.True, "what sticks out stays an obstacle");
            Assert.That(map.MinX, Is.GreaterThanOrEqualTo(10f - 1e-4f));
        }

        private static PropCapture.Build BuildAt(float beginX, float beginZ, float endX, float endZ, bool decidedRecorded = true)
        {
            var input = new PropObstacleInput { Cell = 0.2f, Cells = 250, PlaneY = 1.7f, MaxDistance = 3f, PlayerX = endX, PlayerZ = endZ };
            if (decidedRecorded) { input.WindowPlayerX = beginX; input.WindowPlayerZ = beginZ; }
            return new PropCapture.Build { Input = input };
        }

        /// <summary>
        /// The product decides a build's window with the player at the START of the gathering and records the player at its END
        /// too. A replay must place its windows with the first: where the player crosses the recentre margin during a gathering,
        /// the product's window stays for that build and moves at the next.
        /// </summary>
        [Test]
        public void AReplaysWindows_AreDecidedWithThePlayerTheProductDecidedWith_AlsoWhereAGatheringCrossesTheMargin()
        {
            // a 50 m window about (0, 0): corner (-25, -25); with a margin of 10 m it is placed anew once the player is beyond 15 m
            var session = new PropCapture.Session();
            session.Builds.Add(BuildAt(0f, 0f, 0.05f, 0f));
            session.Builds.Add(BuildAt(14.96f, 0f, 15.04f, 0f));    // the gathering crosses the margin: decided inside it
            session.Builds.Add(BuildAt(15.04f, 0f, 15.10f, 0f));    // decided beyond it: placed anew
            session.Builds.Add(BuildAt(15.10f, 0f, 15.20f, 0f));
            var replay = new PropReplay(session, null, null, null, null);
            float[] corners = replay.Windows(10f);

            // the product's rule on the places it decides with
            bool has = false; float ox = 0f, oz = 0f;
            var moved = new bool[4];
            float[] beginX = { 0f, 14.96f, 15.04f, 15.10f };
            for (int i = 0; i < 4; i++)
            {
                moved[i] = PropObstacleWindow.Follow(50f, 0.2f, 10f, ref has, ref ox, ref oz, beginX[i], 0f);
                Assert.That(corners[i * 2], Is.EqualTo(ox), "build " + i + ", x");
                Assert.That(corners[i * 2 + 1], Is.EqualTo(oz), "build " + i + ", z");
            }

            Assert.That(moved, Is.EqualTo(new[] { true, false, true, false }));
            Assert.That(corners[0], Is.EqualTo(-25f).Within(1e-4f));
            Assert.That(corners[2], Is.EqualTo(-25f).Within(1e-4f), "the build whose gathering crossed the margin keeps the window");
            Assert.That(corners[4], Is.EqualTo(-10f).Within(1e-3f), "placed anew about 15.04: the corner on a whole cell at or below -9.96");
            Assert.That(corners[6], Is.EqualTo(corners[4]));

            // the case: decided with the player at the gathering's end (all a record before 2026-11-16 has), the window moves one build early
            var old = new PropCapture.Session();
            old.Builds.Add(BuildAt(0f, 0f, 0.05f, 0f, false));
            old.Builds.Add(BuildAt(14.96f, 0f, 15.04f, 0f, false));
            float[] early = new PropReplay(old, null, null, null, null).Windows(10f);
            Assert.That(early[2], Is.Not.EqualTo(-25f).Within(1e-3f), "the case");

            // the margin is at most 0.4 of the window: 24 m acts as 20 m
            bool has2 = false; float x2 = 0f, z2 = 0f;
            PropObstacleWindow.Follow(50f, 0.2f, 24f, ref has2, ref x2, ref z2, 0f, 0f);
            Assert.That(PropObstacleWindow.Follow(50f, 0.2f, 24f, ref has2, ref x2, ref z2, 4.9f, 0f), Is.False);
            Assert.That(PropObstacleWindow.Follow(50f, 0.2f, 24f, ref has2, ref x2, ref z2, 5.1f, 0f), Is.True);
        }

        /// <summary>A build's record carries the player its window was decided with; a record that ends before it (made earlier) reads as "not recorded".</summary>
        [Test]
        public void ALaunch_HasTheMapByDefault_AndOnlyTheOffArgumentTurnsItOff()
        {
            var adopted = new PropObstacleSettings();
            Assert.That(adopted.recentreMarginMeters, Is.EqualTo(20f), "the adopted margin");

            Assert.That(Zantetsu.Sandbox.PropObstacleSource.Read(new string[0], out PropObstacleSettings none), Is.True, "no argument");
            Assert.That(none.Describe(), Is.EqualTo(adopted.Describe()));
            Assert.That(Zantetsu.Sandbox.PropObstacleSource.Read(null, out _), Is.True);
            Assert.That(Zantetsu.Sandbox.PropObstacleSource.Read(new[] { "player", "-zantetsuPropObstacles" }, out _), Is.True, "the evaluation's argument: accepted, no effect");
            Assert.That(Zantetsu.Sandbox.PropObstacleSource.Read(new[] { "player", "-zantetsuPropObstaclesOff" }, out _), Is.False);
            Assert.That(Zantetsu.Sandbox.PropObstacleSource.Read(new[] { "-zantetsuPropObstacles", "-zantetsuPropObstaclesOff" }, out _), Is.False, "off wins");
            Assert.That(Zantetsu.Sandbox.PropObstacleSource.On(new[] { "-zantetsuPropObstaclesOffX" }), Is.True, "another argument");
            Assert.That(Zantetsu.Sandbox.PropObstacleSource.Read(new[] { "-zantetsuPropObstacleUpdateMs", "500" }, out PropObstacleSettings slower), Is.True);
            Assert.That(slower.updateSeconds, Is.EqualTo(0.5f).Within(1e-6f));
        }

        [Test]
        public void ALongLinesParts_AreWithinTheLimit_CutAfterASeparator_AndJoinToTheLine()
        {
            var words = new System.Text.StringBuilder("MOBPLAN PROPS (end): ");
            for (int i = 0; i < 60; i++) words.Append("counter ").Append(i).Append(' ').Append(i * 1234.5).Append(i % 7 == 0 ? ", and " : "; ");
            words.Append(new string('x', 130));   // a run without a separator, longer than a part
            string line = words.ToString();
            foreach (int most in new[] { 1, 2, 50, 100, 800, line.Length - 1 })
            {
                var parts = PropCapture.LogParts(line, most);
                Assert.That(string.Concat(parts), Is.EqualTo(line), "most " + most);
                Assert.That(parts.Count, Is.GreaterThan(1));
                for (int i = 0; i < parts.Count; i++)
                {
                    Assert.That(parts[i].Length, Is.InRange(1, most), "most " + most + ", part " + i);
                    // a part short of the limit ends after a separator (the last one ends the line)
                    if (i < parts.Count - 1 && parts[i].Length < most) Assert.That(parts[i].EndsWith("; "), Is.True, "most " + most + ", part " + i + ": " + parts[i]);
                }
            }
            Assert.That(PropCapture.LogParts(line, line.Length), Is.EqualTo(new[] { line }));
            Assert.That(PropCapture.LogParts(line, 5000), Is.EqualTo(new[] { line }));
            Assert.That(PropCapture.LogParts("", 100), Is.Empty);
            Assert.That(PropCapture.LogParts(null, 100), Is.Empty);
            Assert.That(PropCapture.LogParts(line, 0), Is.Empty);
        }

        [Test]
        public void ABuildsRecord_CarriesThePlayerItsWindowWasDecidedWith_AndAnEarlierRecordReadsAsNone()
        {
            var input = new PropObstacleInput { Time = 12.5, Cell = 0.2f, Cells = 250, PlaneY = 1.7f, MaxDistance = 3f, PlayerX = 15.04f, PlayerZ = -3f, OriginX = -25f, OriginZ = -28f, WindowPlayerX = 14.96f, WindowPlayerZ = -3.01f };
            byte[] record = PropCapture.EncodeBuild(input, default, new PropCapture.BuildTimings { Serial = 1 }, new System.Collections.Generic.Dictionary<long, float[]>(), true);
            string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zantetsu-prop-capture-test-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(folder);
            try
            {
                foreach (int cut in new[] { 0, 8 })
                {
                    using (var stream = new System.IO.FileStream(System.IO.Path.Combine(folder, PropCapture.BuildsFile), System.IO.FileMode.Create))
                    using (var w = new System.IO.BinaryWriter(stream))
                    {
                        w.Write(record.Length - cut);
                        w.Write(record, 0, record.Length - cut);
                    }

                    PropCapture.Session session = PropCapture.Session.Load(folder);
                    Assert.That(session.Builds.Count, Is.EqualTo(1));
                    PropObstacleInput read = session.Builds[0].Input;
                    Assert.That(read.PlayerX, Is.EqualTo(15.04f));
                    Assert.That(read.OriginZ, Is.EqualTo(-28f));
                    PropReplay.WindowPlayer(read, out float px, out float pz);
                    if (cut == 0)
                    {
                        Assert.That(read.WindowPlayerX, Is.EqualTo(14.96f));
                        Assert.That(read.WindowPlayerZ, Is.EqualTo(-3.01f));
                        Assert.That(px, Is.EqualTo(14.96f));
                    }
                    else
                    {
                        Assert.That(float.IsNaN(read.WindowPlayerX) && float.IsNaN(read.WindowPlayerZ), Is.True, "a record without it");
                        Assert.That(px, Is.EqualTo(15.04f), "falls back on the player at the gathering's end");
                        Assert.That(pz, Is.EqualTo(-3f));
                    }
                }
            }
            finally
            {
                System.IO.Directory.Delete(folder, true);
            }
        }

        [Test]
        public void Pool_NeverHandsOutTheCurrentVersion_OrOneAReaderHolds()
        {
            var pool = new PropObstacleMapPool();
            Assert.That(pool.RetainCurrent(), Is.Null, "nothing before the first publication");
            var first = pool.Take(10);
            Assert.That(pool.Take(10), Is.Not.SameAs(first), "a buffer being written is not taken twice");
            pool.Publish(first);
            Assert.That(first.Version, Is.EqualTo(1));
            var held = pool.RetainCurrent();
            Assert.That(held, Is.SameAs(first));
            var second = pool.Take(10);
            Assert.That(second, Is.Not.SameAs(first), "the current one is not written");
            pool.Publish(second);
            var third = pool.Take(10);
            Assert.That(third, Is.Not.SameAs(first).And.Not.SameAs(second), "the first is still held by a reader; the second is current");
            pool.Return(third);
            held.Release();
            var fourth = pool.Take(10);
            Assert.That(fourth == first || fourth == third, Is.True, "released, or given back: free again");
            Assert.That(fourth, Is.Not.SameAs(second));
            Assert.That(() => held.Release(), Throws.InvalidOperationException, "a release too many is an error");
        }

        [Test]
        public void PlannerRandom_TakenUpFromASavedState_DrawsTheSameAgain()
        {
            var random = new PlannerRandom(38);
            for (var i = 0; i < 100; i++) random.NextDouble();
            var state = random.State;
            var expected = new double[50];
            for (var i = 0; i < expected.Length; i++) expected[i] = i % 3 == 0 ? random.Next(1000) : random.NextDouble();
            var again = new PlannerRandom(1) { State = state };
            for (var i = 0; i < expected.Length; i++) Assert.That(i % 3 == 0 ? again.Next(1000) : again.NextDouble(), Is.EqualTo(expected[i]), "draw " + i);
            Random asBase = new PlannerRandom(5);
            for (var i = 0; i < 1000; i++)
            {
                var d = asBase.NextDouble(); var n = asBase.Next(7); var r = asBase.Next(-3, 4);
                Assert.That(d >= 0.0 && d < 1.0 && n >= 0 && n < 7 && r >= -3 && r < 4 && asBase.Next() >= 0, Is.True);
            }
        }
    }
}
