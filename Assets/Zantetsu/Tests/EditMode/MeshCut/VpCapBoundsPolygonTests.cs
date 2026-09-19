using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The finite Cap Bounds Polygon of DESIGN 5.2: the cross-section of a body's local bounds box with one cut plane,
    /// in world space, wound about the plane's normal.
    /// <para>
    /// What is asked of it here is what the stencil will rely on: every vertex lies on the plane and inside the box, no
    /// vertex is repeated, the loop is convex and goes round once, and the winding agrees with the normal under a
    /// placement that moves, turns, scales unevenly or mirrors. A plane that misses the box, grazes a corner or runs
    /// along an edge has no cap at all, which is a normal answer and not a failure.
    /// </para>
    /// </summary>
    public class VpCapBoundsPolygonTests
    {
        /// <summary>The same box the display fixture's body occupies: x and z in [-1, 1], y in [0, 2].</summary>
        private static readonly Bounds k_box = new Bounds(new Vector3(0f, 1f, 0f), new Vector3(2f, 2f, 2f));

        private static float Epsilon => VpCapBoundsPolygon.EpsilonFor(k_box);

        private static Vector3[] Buffer()
        {
            return new Vector3[VpCapBoundsPolygon.MaxVertices * 2];
        }

        private static int Build(
            float4 localPlane, Matrix4x4 placement, Vector3[] into, out float4 worldPlane, Bounds? box = null)
        {
            Bounds bounds = box ?? k_box;
            var polygon = new VpCapBoundsPolygon();
            Assert.That(
                polygon.TryBuild(
                    bounds, localPlane, placement, VpCapBoundsPolygon.EpsilonFor(bounds), into, 0, out int count,
                    out worldPlane),
                Is.True,
                "the polygon builds");
            return count;
        }

        /// <summary>
        /// Everything a cap polygon has to be, asked of one result: the vertices lie on the world plane, they lie
        /// inside the placed box, none repeats another, and three consecutive of them wind about the given normal.
        /// The convexity is checked by the turn never changing sign, which also catches an order that doubles back.
        /// </summary>
        private static void AssertPolygon(
            Vector3[] vertices, int count, float4 worldPlane, Bounds localBounds, Matrix4x4 placement, string what)
        {
            Assert.That(count, Is.InRange(3, VpCapBoundsPolygon.MaxVertices), what + ": three to six vertices");

            var normal = new float3(worldPlane.x, worldPlane.y, worldPlane.z);
            Assert.That(math.length(normal), Is.EqualTo(1f).Within(1e-4f), what + ": the world normal is normalized");

            Matrix4x4 toLocal = placement.inverse;
            float scale = math.cmax(math.abs(new float3(
                placement.lossyScale.x, placement.lossyScale.y, placement.lossyScale.z)));
            float tolerance = math.max(1e-4f, 1e-4f * scale);

            for (int i = 0; i < count; i++)
            {
                float distance = math.dot(normal, (float3)vertices[i]) + worldPlane.w;
                Assert.That(
                    math.abs(distance), Is.LessThan(tolerance),
                    what + ": vertex " + i + " is on the plane (" + distance + ")");

                Vector3 local = toLocal.MultiplyPoint3x4(vertices[i]);
                Assert.That(local.x, Is.InRange(localBounds.min.x - 1e-3f, localBounds.max.x + 1e-3f), what + ": inside x");
                Assert.That(local.y, Is.InRange(localBounds.min.y - 1e-3f, localBounds.max.y + 1e-3f), what + ": inside y");
                Assert.That(local.z, Is.InRange(localBounds.min.z - 1e-3f, localBounds.max.z + 1e-3f), what + ": inside z");

                for (int j = i + 1; j < count; j++)
                {
                    Assert.That(
                        Vector3.Distance(vertices[i], vertices[j]), Is.GreaterThan(1e-4f),
                        what + ": vertices " + i + " and " + j + " are not the same point");
                }
            }

            AssertWinding(vertices, count, normal, what);
        }

        /// <summary>
        /// The loop goes round once in the direction the normal asks for: every turn bends the same way, and the
        /// triangle normal of each consecutive triple points along the given direction rather than against it.
        /// </summary>
        private static void AssertWinding(Vector3[] vertices, int count, float3 outward, string what)
        {
            for (int i = 0; i < count; i++)
            {
                float3 a = vertices[i];
                float3 b = vertices[(i + 1) % count];
                float3 c = vertices[(i + 2) % count];
                float turn = math.dot(math.cross(b - a, c - a), outward);
                Assert.That(
                    turn, Is.GreaterThan(0f),
                    what + ": the turn at vertex " + ((i + 1) % count) + " winds about the outward normal (" + turn + ")");
            }
        }

        // ----- the shapes a box and a plane make ------------------------------------------------------------------

        /// <summary>An axis plane through the middle of the box: the square cross-section, four vertices.</summary>
        [Test]
        public void AnAxisPlane_GivesTheSquareCrossSection()
        {
            Vector3[] vertices = Buffer();
            int count = Build(new float4(0f, 1f, 0f, -1f), Matrix4x4.identity, vertices, out float4 worldPlane);

            Assert.That(count, Is.EqualTo(4), "a box cut square across is a quadrilateral");
            AssertPolygon(vertices, count, worldPlane, k_box, Matrix4x4.identity, "axis plane");
            for (int i = 0; i < count; i++)
            {
                Assert.That(vertices[i].y, Is.EqualTo(1f).Within(1e-5f), "the cross-section is at y = 1");
            }

            // It is the box's own square: the four corners of the middle, and nothing invented beyond them.
            Assert.That(
                Mathf.Abs(vertices[0].x), Is.EqualTo(1f).Within(1e-5f), "the cross-section reaches the box's side");
            Assert.That(Mathf.Abs(vertices[0].z), Is.EqualTo(1f).Within(1e-5f));
        }

        /// <summary>A plane across three axes at once, through the box's centre: the hexagonal cross-section.</summary>
        [Test]
        public void ASlantedPlaneThroughTheCentre_GivesSixVertices()
        {
            // dot(n, x) + d = 0 through (0, 1, 0) with n along (1, 1, 1).
            float3 n = math.normalize(new float3(1f, 1f, 1f));
            var plane = new float4(n, -math.dot(n, new float3(0f, 1f, 0f)));

            Vector3[] vertices = Buffer();
            int count = Build(plane, Matrix4x4.identity, vertices, out float4 worldPlane);

            Assert.That(count, Is.EqualTo(6), "a cube cut corner to corner is a hexagon");
            AssertPolygon(vertices, count, worldPlane, k_box, Matrix4x4.identity, "slanted plane");
        }

        /// <summary>A plane that takes one corner off: the smallest cap there is, a triangle.</summary>
        [Test]
        public void APlaneAcrossOneCorner_GivesThreeVertices()
        {
            float3 n = math.normalize(new float3(1f, 1f, 1f));
            var plane = new float4(n, -math.dot(n, new float3(0.6f, 1.6f, 0.6f)));

            Vector3[] vertices = Buffer();
            int count = Build(plane, Matrix4x4.identity, vertices, out float4 worldPlane);

            Assert.That(count, Is.EqualTo(3), "one corner cut off is a triangle");
            AssertPolygon(vertices, count, worldPlane, k_box, Matrix4x4.identity, "corner plane");
        }

        /// <summary>
        /// A plane lying in a face of the box has a cap: that face, with its four corners and no vertex repeated —
        /// each corner is found once as a corner on the plane and never again along the edges that meet there.
        /// </summary>
        [Test]
        public void APlaneInAFaceOfTheBox_GivesThatFaceOnce()
        {
            Vector3[] vertices = Buffer();
            int count = Build(new float4(0f, 1f, 0f, 0f), Matrix4x4.identity, vertices, out float4 worldPlane);

            Assert.That(count, Is.EqualTo(4), "the face itself, with no corner counted twice");
            AssertPolygon(vertices, count, worldPlane, k_box, Matrix4x4.identity, "face plane");
            for (int i = 0; i < count; i++)
            {
                Assert.That(vertices[i].y, Is.EqualTo(0f).Within(1e-5f), "the face at y = 0");
            }
        }

        // ----- and the ones that are no cap at all ----------------------------------------------------------------

        /// <summary>A plane past the box, a plane through one corner, and a plane along one edge: all empty.</summary>
        [Test]
        public void APlaneThatMissesTouchesOrGrazesTheBox_HasNoCap()
        {
            Vector3[] vertices = Buffer();
            var polygon = new VpCapBoundsPolygon();

            Assert.That(
                polygon.TryBuild(
                    k_box, new float4(0f, 1f, 0f, 5f), Matrix4x4.identity, Epsilon, vertices, 0, out int missed, out _),
                Is.True,
                "a plane clear of the box is an ordinary answer");
            Assert.That(missed, Is.EqualTo(0), "and it is empty");

            // Through the corner (1, 2, 1) only: the whole box is on one side of it.
            float3 n = math.normalize(new float3(1f, 1f, 1f));
            var corner = new float4(n, -math.dot(n, new float3(1f, 2f, 1f)));
            Assert.That(
                polygon.TryBuild(k_box, corner, Matrix4x4.identity, Epsilon, vertices, 0, out int touched, out _),
                Is.True);
            Assert.That(touched, Is.EqualTo(0), "one corner is a touch, not a cap");

            // Along the edge x = 1, y = 2: two corners on the plane and nothing else.
            float3 edgeNormal = math.normalize(new float3(1f, 1f, 0f));
            var edge = new float4(edgeNormal, -math.dot(edgeNormal, new float3(1f, 2f, 0f)));
            Assert.That(
                polygon.TryBuild(k_box, edge, Matrix4x4.identity, Epsilon, vertices, 0, out int grazed, out _),
                Is.True);
            Assert.That(grazed, Is.EqualTo(0), "one edge is a line, not a cap");

            for (int i = 0; i < vertices.Length; i++)
            {
                Assert.That(vertices[i], Is.EqualTo(Vector3.zero), "an empty result writes nothing at all");
            }
        }

        /// <summary>Malformed input is refused, and refused without writing anything.</summary>
        /// <summary>
        /// Arithmetic that does not come out finite is refused, never answered as an empty section: an epsilon whose square
        /// passes a float, a corner's distance past a float, and two distances whose difference passes one. A plane that
        /// misses the box, and an epsilon that merges a whole section into one vertex, are still empty answers -- the
        /// second is the epsilon's doing and not an error. Nothing is written by any of them.
        /// </summary>
        [Test]
        public void ArithmeticThatDoesNotComeOutFinite_IsRefused_NeverAnsweredAsEmpty()
        {
            Vector3[] vertices = Buffer();
            var polygon = new VpCapBoundsPolygon();
            var across = new float4(0f, 1f, 0f, -1f);

            Assert.That(
                polygon.TryBuild(k_box, new float4(0f, 1f, 0f, -9f), Matrix4x4.identity, Epsilon, vertices, 0, out int missed, out _),
                Is.True, "a plane that misses the box");
            Assert.That(missed, Is.Zero, "is an empty answer");
            Assert.That(
                polygon.TryBuild(k_box, across, Matrix4x4.identity, 1e19f, vertices, 0, out int merged, out _),
                Is.True, "an epsilon of 1e19, whose square is a float");
            Assert.That(merged, Is.Zero, "merges the section into one vertex: empty, by the epsilon");

            Assert.That(float.IsInfinity(1e20f * 1e20f), Is.True, "the layout: the square passes a float");
            Assert.That(
                polygon.TryBuild(k_box, across, Matrix4x4.identity, 1e20f, vertices, 0, out _, out _),
                Is.False, "an epsilon whose square is not a float");

            var wide = new Bounds { center = Vector3.zero, extents = new Vector3(1e38f, 1f, 1f) };
            Assert.That(
                polygon.TryBuild(wide, new float4(1f, 0f, 0f, -3.3e38f), Matrix4x4.identity, 1e-3f, vertices, 0, out _, out _),
                Is.False, "a corner 4.3e38 from the plane");

            var wider = new Bounds { center = Vector3.zero, extents = new Vector3(2e38f, 1f, 1f) };
            Assert.That(
                polygon.TryBuild(wider, new float4(1f, 0f, 0f, 0f), Matrix4x4.identity, 1e-3f, vertices, 0, out _, out _),
                Is.False, "two corners 2e38 either side, whose distances differ by 4e38");

            for (int i = 0; i < vertices.Length; i++)
            {
                Assert.That(vertices[i], Is.EqualTo(Vector3.zero), "nothing was written by any of them");
            }
        }

        [Test]
        public void MalformedInput_IsRefusedAndWritesNothing()
        {
            Vector3[] vertices = Buffer();
            var polygon = new VpCapBoundsPolygon();
            var plane = new float4(0f, 1f, 0f, -1f);

            Assert.That(
                polygon.TryBuild(
                    k_box, new float4(0f, 0f, 0f, 1f), Matrix4x4.identity, Epsilon, vertices, 0, out _, out _),
                Is.False,
                "a plane with no normal is no plane");

            Assert.That(
                polygon.TryBuild(
                    k_box, new float4(0f, float.NaN, 0f, -1f), Matrix4x4.identity, Epsilon, vertices, 0, out _, out _),
                Is.False,
                "nor is one that is not finite");

            Assert.That(
                polygon.TryBuild(
                    k_box, plane, Matrix4x4.Scale(new Vector3(1f, 0f, 1f)), Epsilon, vertices, 0, out _, out _),
                Is.False,
                "a singular placement has no world plane");

            var projective = Matrix4x4.identity;
            projective.m30 = 0.5f;
            Assert.That(
                polygon.TryBuild(k_box, plane, projective, Epsilon, vertices, 0, out _, out _), Is.False,
                "and a projective one is not what this is for");

            var backwards = new Bounds { min = new Vector3(1f, 1f, 1f), max = new Vector3(1f, 1f, 1f) };
            backwards.SetMinMax(new Vector3(1f, 0f, 0f), new Vector3(-1f, 0f, 0f));
            Assert.That(
                polygon.TryBuild(backwards, plane, Matrix4x4.identity, Epsilon, vertices, 0, out _, out _), Is.False,
                "a bounds whose max is behind its min is not a box");

            Assert.That(
                polygon.TryBuild(k_box, plane, Matrix4x4.identity, -1f, vertices, 0, out _, out _), Is.False,
                "and a negative epsilon is not a distance");

            var tooSmall = new Vector3[VpCapBoundsPolygon.MaxVertices - 1];
            Assert.That(
                polygon.TryBuild(k_box, plane, Matrix4x4.identity, Epsilon, tooSmall, 0, out _, out _), Is.False,
                "there has to be room for the largest polygon a box can give");

            for (int i = 0; i < vertices.Length; i++)
            {
                Assert.That(vertices[i], Is.EqualTo(Vector3.zero), "nothing was written by any of them");
            }
        }

        // ----- placement ------------------------------------------------------------------------------------------

        /// <summary>
        /// Moved, turned, scaled unevenly and mirrored: the face, the vertices and the winding stay consistent. The
        /// ordering is done in world space, which is what lets a mirroring placement come out right rather than
        /// inside out.
        /// </summary>
        [Test]
        public void AMovedTurnedScaledOrMirroredPlacement_KeepsTheFaceAndTheWinding()
        {
            float3 n = math.normalize(new float3(0.3f, 1f, -0.4f));
            var plane = new float4(n, -math.dot(n, new float3(0f, 1f, 0f)));
            Vector3[] vertices = Buffer();

            Matrix4x4 moved = Matrix4x4.TRS(
                new Vector3(3f, -2f, 1.5f), Quaternion.Euler(25f, -70f, 15f), Vector3.one);
            int count = Build(plane, moved, vertices, out float4 movedPlane);
            AssertPolygon(vertices, count, movedPlane, k_box, moved, "moved and turned");

            Matrix4x4 scaled = Matrix4x4.TRS(
                new Vector3(-1f, 4f, 0.5f), Quaternion.Euler(-10f, 35f, 80f), new Vector3(0.4f, 2.5f, 1.75f));
            count = Build(plane, scaled, vertices, out float4 scaledPlane);
            AssertPolygon(vertices, count, scaledPlane, k_box, scaled, "non-uniform scale");

            Matrix4x4 mirrored = Matrix4x4.TRS(
                new Vector3(2f, 1f, -3f), Quaternion.Euler(0f, 45f, 0f), new Vector3(1f, -2f, 1f));
            Assert.That(mirrored.determinant, Is.LessThan(0f), "the placement really does mirror");
            count = Build(plane, mirrored, vertices, out float4 mirroredPlane);
            AssertPolygon(vertices, count, mirroredPlane, k_box, mirrored, "mirrored");
        }

        /// <summary>
        /// The two sides of one cut are one polygon in opposite directions: the same points, and each wound about its
        /// own outward normal.
        /// </summary>
        [Test]
        public void TheReversedPolygon_WindsAboutTheOppositeNormal()
        {
            float3 n = math.normalize(new float3(0.2f, 1f, 0.35f));
            var plane = new float4(n, -math.dot(n, new float3(0f, 1f, 0f)));

            Vector3[] built = Buffer();
            int count = Build(plane, Matrix4x4.identity, built, out float4 worldPlane);
            var normal = new float3(worldPlane.x, worldPlane.y, worldPlane.z);

            var reversed = new Vector3[VpCapBoundsPolygon.MaxVertices * 2];
            for (int i = 0; i < count; i++)
            {
                reversed[i] = built[count - 1 - i];
            }

            AssertWinding(built, count, normal, "as built");
            AssertWinding(reversed, count, -normal, "reversed");

            for (int i = 0; i < count; i++)
            {
                Assert.That(
                    reversed[i], Is.EqualTo(built[count - 1 - i]), "the same points, in the other direction");
            }
        }

        /// <summary>
        /// The epsilon is a length in the body's own units, which is what makes it mean the same thing whatever scale
        /// the caller's plane arrives at: the same plane written with a normal a thousand times longer gives the same
        /// polygon, because the plane is normalized before any distance is measured.
        /// </summary>
        [Test]
        public void AnUnnormalizedPlane_GivesTheSamePolygon()
        {
            float3 n = math.normalize(new float3(0.4f, 1f, -0.2f));
            var plane = new float4(n, -math.dot(n, new float3(0f, 1f, 0f)));

            Vector3[] normalized = Buffer();
            int first = Build(plane, Matrix4x4.identity, normalized, out _);

            Vector3[] scaled = Buffer();
            int second = Build(plane * 1000f, Matrix4x4.identity, scaled, out _);

            Assert.That(second, Is.EqualTo(first), "the same number of vertices");
            for (int i = 0; i < first; i++)
            {
                Assert.That(Vector3.Distance(scaled[i], normalized[i]), Is.LessThan(1e-4f), "and the same vertices");
            }
        }

        /// <summary>
        /// A body with no extent at all has no cap, and the zero epsilon that follows from its size is not a reason to
        /// refuse it: the answer is simply empty.
        /// </summary>
        [Test]
        public void ABodyWithNoExtent_HasNoCap()
        {
            var point = new Bounds(new Vector3(0f, 1f, 0f), Vector3.zero);
            Assert.That(VpCapBoundsPolygon.EpsilonFor(point), Is.EqualTo(0f), "no extent, no epsilon");

            Vector3[] vertices = Buffer();
            int count = Build(new float4(0f, 1f, 0f, -1f), Matrix4x4.identity, vertices, out _, point);
            Assert.That(count, Is.EqualTo(0), "a point is not a cap");
        }

        // ----- the sizes at which the answer is decided ------------------------------------------------------------

        /// <summary>The box of these cases: centred on the origin, two units across, so the merge distance is 2e-6.</summary>
        private static readonly Bounds k_unitBox = new Bounds(Vector3.zero, new Vector3(2f, 2f, 2f));

        /// <summary>Twice the polygon's area, which is positive exactly when the cap is a cap.</summary>
        private static float TwiceArea(Vector3[] vertices, int count)
        {
            var sum = float3.zero;
            for (int i = 1; i + 1 < count; i++)
            {
                sum += math.cross((float3)vertices[i] - (float3)vertices[0], (float3)vertices[i + 1] - (float3)vertices[0]);
            }

            return math.length(sum);
        }

        /// <summary>
        /// Every vertex lies in the face and inside the box — to within the ordinary error of the division, the
        /// interpolation and the matrix multiply that produced it, and of the multiply back that this check itself
        /// does. The slack here is that arithmetic's, and is not a licence for the polygon to leave the box: it is
        /// sized from the coordinates in play, not from the merge distance.
        /// </summary>
        private static void AssertOnFaceAndInBox(
            Vector3[] vertices, int count, float4 worldPlane, Bounds localBounds, Matrix4x4 placement, string what)
        {
            var normal = new float3(worldPlane.x, worldPlane.y, worldPlane.z);
            Matrix4x4 toLocal = placement.inverse;

            float localReach = math.cmax(math.abs(new float3(
                localBounds.max.x - localBounds.min.x,
                localBounds.max.y - localBounds.min.y,
                localBounds.max.z - localBounds.min.z)));
            float worldReach = 0f;
            for (int i = 0; i < count; i++)
            {
                worldReach = math.max(
                    worldReach, math.cmax(math.abs(new float3(vertices[i].x, vertices[i].y, vertices[i].z))));
            }

            float onFace = 1e-6f * math.max(1f, worldReach);
            float inBox = 1e-5f * math.max(1f, localReach);

            for (int i = 0; i < count; i++)
            {
                float distance = math.dot(normal, (float3)vertices[i]) + worldPlane.w;
                Assert.That(
                    math.abs(distance), Is.LessThan(onFace),
                    what + ": vertex " + i + " is in the face (" + distance + ")");

                Vector3 local = toLocal.MultiplyPoint3x4(vertices[i]);
                Assert.That(local.x, Is.InRange(localBounds.min.x - inBox, localBounds.max.x + inBox), what + ": x");
                Assert.That(local.y, Is.InRange(localBounds.min.y - inBox, localBounds.max.y + inBox), what + ": y");
                Assert.That(local.z, Is.InRange(localBounds.min.z - inBox, localBounds.max.z + inBox), what + ": z");
            }
        }

        /// <summary>
        /// A plane a hair inside a face of the box has the cross-section that is a hair inside it; a plane a hair
        /// outside that face is outside the box and has no cap at all. Which side a corner is on is its own distance's
        /// to say, so a near miss is a miss: nothing is moved out to meet a plane the box does not reach.
        /// </summary>
        [Test]
        public void APlaneJustInsideAFace_CutsIt_AndJustOutsideIsEmpty()
        {
            float epsilon = VpCapBoundsPolygon.EpsilonFor(k_unitBox);
            Assert.That(epsilon, Is.EqualTo(2e-6f).Within(1e-9f), "the merge distance of a two-unit box");

            (float d, bool capped, string what)[] cases =
            {
                (1f - 1e-6f, true, "a hair inside the face"),
                (1f + 1e-6f, false, "a hair outside the face"),
                (1f - 1e-4f, true, "well inside"),
                (1f + 1e-4f, false, "well outside"),
            };

            var polygon = new VpCapBoundsPolygon();
            foreach ((float d, bool capped, string what) in cases)
            {
                Vector3[] vertices = Buffer();
                Assert.That(
                    polygon.TryBuild(
                        k_unitBox, new float4(0f, 1f, 0f, -d), Matrix4x4.identity, epsilon, vertices, 0, out int count,
                        out float4 worldPlane),
                    Is.True,
                    what + ": the polygon builds");

                if (!capped)
                {
                    Assert.That(count, Is.Zero, what + ": a plane past the box has no cap");
                    for (int i = 0; i < vertices.Length; i++)
                    {
                        Assert.That(vertices[i], Is.EqualTo(Vector3.zero), what + ": and writes nothing");
                    }

                    continue;
                }

                Assert.That(count, Is.EqualTo(4), what + ": the square cross-section");
                AssertOnFaceAndInBox(vertices, count, worldPlane, k_unitBox, Matrix4x4.identity, what);
                AssertWinding(vertices, count, new float3(worldPlane.x, worldPlane.y, worldPlane.z), what);
                Assert.That(TwiceArea(vertices, count), Is.GreaterThan(1f), what + ": and it has area");

                for (int i = 0; i < count; i++)
                {
                    // Where the edges of the box cross that plane, which is inside the box and not at its face.
                    Assert.That(
                        vertices[i].y, Is.EqualTo(d).Within(1e-6f), what + ": the vertices are where the plane is");
                    Assert.That(vertices[i].y, Is.LessThan(1f), what + ": and inside the box");
                }
            }
        }

        /// <summary>
        /// The same pair of cases under a placement that stretches the plane's own direction a hundredfold, which is
        /// where an answer that had strayed off the face would show. The tolerance is the arithmetic's at those
        /// magnitudes, not a band.
        /// </summary>
        [Test]
        public void APlaneJustInsideAFace_UnderANonUniformScale_IsStillInTheFaceAndTheBox()
        {
            float epsilon = VpCapBoundsPolygon.EpsilonFor(k_unitBox);
            Matrix4x4 placement = Matrix4x4.TRS(
                new Vector3(1f, -2f, 0.5f), Quaternion.Euler(0f, 25f, 0f), new Vector3(0.01f, 50f, 0.01f));
            var polygon = new VpCapBoundsPolygon();

            Vector3[] inside = Buffer();
            Assert.That(
                polygon.TryBuild(
                    k_unitBox, new float4(0f, 1f, 0f, -(1f - 1e-6f)), placement, epsilon, inside, 0, out int count,
                    out float4 worldPlane),
                Is.True);

            Assert.That(count, Is.EqualTo(4), "the square cross-section");
            AssertOnFaceAndInBox(inside, count, worldPlane, k_unitBox, placement, "stretched, a hair inside");
            AssertWinding(inside, count, new float3(worldPlane.x, worldPlane.y, worldPlane.z), "stretched");
            Assert.That(TwiceArea(inside, count), Is.GreaterThan(0f), "and it has area");

            Vector3[] outside = Buffer();
            Assert.That(
                polygon.TryBuild(
                    k_unitBox, new float4(0f, 1f, 0f, -(1f + 1e-6f)), placement, epsilon, outside, 0, out int missed,
                    out _),
                Is.True);
            Assert.That(missed, Is.Zero, "and a hair outside is still outside, however the body is placed");
        }

        /// <summary>
        /// Near a corner: a plane past it has no cap, and a plane a little inside it cuts the smallest cap there is.
        /// Between them lies a cap so small that its three crossings are one point — the merge distance cannot tell it
        /// from a touch, and it is reported as one. That is the merge distance's doing and not a band: the box is not
        /// widened, and a cap this small is the size of the arithmetic that found it.
        /// </summary>
        [Test]
        public void APlaneNearACorner_CutsATriangle_OrIsTooSmallToTellFromATouch()
        {
            float epsilon = VpCapBoundsPolygon.EpsilonFor(k_unitBox);
            float3 n = math.normalize(new float3(1f, 1f, 1f));
            var corner = new float3(1f, 1f, 1f);
            var polygon = new VpCapBoundsPolygon();

            // Past the corner: nothing of the box is on the far side.
            Vector3[] beyond = Buffer();
            Assert.That(
                polygon.TryBuild(
                    k_unitBox, new float4(n, -math.dot(n, corner + (n * 1e-7f))), Matrix4x4.identity, epsilon, beyond,
                    0, out int past, out _),
                Is.True);
            Assert.That(past, Is.Zero, "a plane past the corner has no cap");

            // A hair inside it: three crossings within a hair of the corner, which are one point.
            Vector3[] slivered = Buffer();
            Assert.That(
                polygon.TryBuild(
                    k_unitBox, new float4(n, -math.dot(n, corner - (n * 1e-7f))), Matrix4x4.identity, epsilon,
                    slivered, 0, out int sliver, out _),
                Is.True);
            Assert.That(sliver, Is.Zero, "a cap smaller than the merge distance is not told from a touch");

            // Properly inside: a real triangle, in the plane and inside the box.
            Vector3[] cut = Buffer();
            Assert.That(
                polygon.TryBuild(
                    k_unitBox, new float4(n, -math.dot(n, corner - (n * 0.05f))), Matrix4x4.identity, epsilon, cut, 0,
                    out int count, out float4 worldPlane),
                Is.True);
            Assert.That(count, Is.EqualTo(3), "one corner taken off is a triangle");
            AssertOnFaceAndInBox(cut, count, worldPlane, k_unitBox, Matrix4x4.identity, "inside the corner");
            AssertWinding(cut, count, new float3(worldPlane.x, worldPlane.y, worldPlane.z), "inside the corner");
            Assert.That(TwiceArea(cut, count), Is.GreaterThan(0f), "and it has area");
        }

        /// <summary>
        /// A slanted plane through a body placed with an uneven scale: the hexagon still lies in the face and inside
        /// the box, and still winds one way round.
        /// </summary>
        [Test]
        public void ASlantedPlaneUnderANonUniformScale_StaysInTheFaceAndTheBox()
        {
            float epsilon = VpCapBoundsPolygon.EpsilonFor(k_unitBox);
            float3 n = math.normalize(new float3(1f, 1f, 1f));
            Matrix4x4 placement = Matrix4x4.TRS(
                new Vector3(-3f, 1f, 2f), Quaternion.Euler(35f, -20f, 55f), new Vector3(0.2f, 6f, 1.4f));

            Vector3[] vertices = Buffer();
            var polygon = new VpCapBoundsPolygon();
            Assert.That(
                polygon.TryBuild(
                    k_unitBox, new float4(n, 0f), placement, epsilon, vertices, 0, out int count, out float4 worldPlane),
                Is.True);

            Assert.That(count, Is.EqualTo(6), "a cube cut corner to corner is a hexagon");
            AssertOnFaceAndInBox(vertices, count, worldPlane, k_unitBox, placement, "slanted and stretched");
            AssertWinding(vertices, count, new float3(worldPlane.x, worldPlane.y, worldPlane.z), "slanted and stretched");
            Assert.That(TwiceArea(vertices, count), Is.GreaterThan(0f), "and it has area");
        }

        /// <summary>
        /// A thin body: cut across the thin direction it caps normally, cut along one of its wide faces it caps that
        /// face, and cut the other way it caps a long narrow rectangle. The thinness is far larger than the merge
        /// distance, so the three answers stay apart.
        /// </summary>
        [Test]
        public void AThinBox_CapsAcrossItAlongItsFaceAndEdgeOn()
        {
            var thin = new Bounds(Vector3.zero, new Vector3(2f, 1e-3f, 2f));
            float epsilon = VpCapBoundsPolygon.EpsilonFor(thin);
            Assert.That(epsilon, Is.LessThan(5e-4f), "the merge distance is far under the body's own thickness");

            var polygon = new VpCapBoundsPolygon();

            Vector3[] across = Buffer();
            Assert.That(
                polygon.TryBuild(
                    thin, new float4(0f, 1f, 0f, 0f), Matrix4x4.identity, epsilon, across, 0, out int wide,
                    out float4 widePlane),
                Is.True);
            Assert.That(wide, Is.EqualTo(4), "the wide cross-section");
            AssertOnFaceAndInBox(across, wide, widePlane, thin, Matrix4x4.identity, "across the thin body");
            Assert.That(TwiceArea(across, wide), Is.GreaterThan(1f), "and it has area");

            Vector3[] along = Buffer();
            Assert.That(
                polygon.TryBuild(
                    thin, new float4(0f, 1f, 0f, -5e-4f), Matrix4x4.identity, epsilon, along, 0, out int face,
                    out float4 facePlane),
                Is.True);
            Assert.That(face, Is.EqualTo(4), "the face itself");
            AssertOnFaceAndInBox(along, face, facePlane, thin, Matrix4x4.identity, "along the thin body");
            Assert.That(TwiceArea(along, face), Is.GreaterThan(1f));
            foreach (Vector3 vertex in new[] { along[0], along[1], along[2], along[3] })
            {
                Assert.That(vertex.y, Is.EqualTo(5e-4f).Within(1e-9f), "which is the body's own face, not beyond it");
            }

            Vector3[] edgeOn = Buffer();
            Assert.That(
                polygon.TryBuild(
                    thin, new float4(1f, 0f, 0f, 0f), Matrix4x4.identity, epsilon, edgeOn, 0, out int narrow,
                    out float4 narrowPlane),
                Is.True);
            Assert.That(narrow, Is.EqualTo(4), "a narrow rectangle");
            AssertOnFaceAndInBox(edgeOn, narrow, narrowPlane, thin, Matrix4x4.identity, "edge on");
            Assert.That(TwiceArea(edgeOn, narrow), Is.GreaterThan(0f), "which still has area");

            // And past the thin body there is nothing, as anywhere else.
            Vector3[] beyond = Buffer();
            Assert.That(
                polygon.TryBuild(
                    thin, new float4(0f, 1f, 0f, -1e-3f), Matrix4x4.identity, epsilon, beyond, 0, out int past, out _),
                Is.True);
            Assert.That(past, Is.Zero, "a plane past a thin body misses it too");
        }
    }
}
