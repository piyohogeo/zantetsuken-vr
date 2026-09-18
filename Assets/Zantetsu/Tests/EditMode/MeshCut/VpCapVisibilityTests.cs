using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The both-eye Frustum and Facing test of one prepared cap (DESIGN 5.7, T-068).
    /// <para>
    /// The rules themselves are asked first of polygons written out here, against an eye whose clip matrix has whole
    /// numbers in it, so that an eye exactly in a cap's plane or a vertex exactly on a frustum plane is exactly there.
    /// Then real caps, prepared by a <see cref="VpLogicalCutDisplay"/> from a cube cut across its middle, are asked
    /// through the display: at the identity, with the separation in them, and placed somewhere else. Every expected
    /// answer is read off where the cap and the eyes were put, not off the test's own output.
    /// </para>
    /// <para>
    /// Nothing is drawn: the test does not change what the display uploads, and none of these calls is expected to.
    /// </para>
    /// </summary>
    public class VpCapVisibilityTests
    {
        /// <summary>A test value only. The product value is DESIGN O-034's and is not chosen here.</summary>
        private const float FacingEpsilon = 0.01f;

        /// <summary>Half the distance between the two eyes of the eyes built here, a little over a real one.</summary>
        private const float HalfIpd = 0.03f;

        private const int BodyMaterial = 7;
        private const float Separation = 0.25f;

        private readonly List<UnityEngine.Object> _objects = new List<UnityEngine.Object>();

        [TearDown]
        public void DestroyObjects()
        {
            foreach (UnityEngine.Object tracked in _objects)
            {
                if (tracked != null)
                {
                    UnityEngine.Object.DestroyImmediate(tracked);
                }
            }

            _objects.Clear();
        }

        // ----- eyes -----------------------------------------------------------------------------------------------

        /// <summary>
        /// An eye at <paramref name="position"/> looking along +z, with a 90 degree square field, the near plane at 1
        /// and the far plane at 3: clip x = x, y = y, z = 2z - 3 and w = z, all relative to the eye. A point at depth z
        /// is inside when |x| and |y| are at most z and z is from 1 to 3. The numbers are whole so a point meant to be
        /// on a plane is on it.
        /// </summary>
        private static VpCapEye BoxEye(Vector3 position)
        {
            var projection = new Matrix4x4(
                new Vector4(1f, 0f, 0f, 0f),
                new Vector4(0f, 1f, 0f, 0f),
                new Vector4(0f, 0f, 2f, 1f),
                new Vector4(0f, 0f, -3f, 0f));
            return new VpCapEye(position, projection * Matrix4x4.Translate(-position));
        }

        /// <summary>
        /// An ordinary Unity eye: at <paramref name="position"/>, looking along <paramref name="forward"/>, with the
        /// view and projection matrices a camera there would have (90 degrees, square, near 0.1, far 100).
        /// </summary>
        private static VpCapEye CameraEye(Vector3 position, Vector3 forward)
        {
            Matrix4x4 cameraToWorld = Matrix4x4.TRS(position, Quaternion.LookRotation(forward, Vector3.up), Vector3.one);
            Matrix4x4 worldToCamera = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * cameraToWorld.inverse;
            return new VpCapEye(position, Matrix4x4.Perspective(90f, 1f, 0.1f, 100f) * worldToCamera);
        }

        /// <summary>Two eyes looking the same way, <see cref="HalfIpd"/> either side of <paramref name="centre"/> along <paramref name="across"/>.</summary>
        private static (VpCapEye left, VpCapEye right) CameraEyes(Vector3 centre, Vector3 forward, Vector3 across)
        {
            return (CameraEye(centre - (across * HalfIpd), forward), CameraEye(centre + (across * HalfIpd), forward));
        }

        private static VpCapVisibilityVerdict Classify(Vector3[] polygon, Vector3 outwardNormal, VpCapEye left, VpCapEye right)
        {
            return VpCapVisibility.Classify(polygon, outwardNormal, left, right, FacingEpsilon);
        }

        // ----- polygons written out -------------------------------------------------------------------------------

        /// <summary>A square in the plane y = 0, in front of an eye at the origin (depth 1.5 to 2.5), facing +y.</summary>
        private static readonly Vector3[] k_floor =
        {
            new Vector3(-0.5f, 0f, 1.5f), new Vector3(-0.5f, 0f, 2.5f), new Vector3(0.5f, 0f, 2.5f), new Vector3(0.5f, 0f, 1.5f),
        };

        /// <summary>The same square read backwards: the cap of the other side, facing -y.</summary>
        private static readonly Vector3[] k_floorReversed = { k_floor[3], k_floor[2], k_floor[1], k_floor[0] };

        private static (VpCapEye left, VpCapEye right) BoxEyesAtHeight(float leftY, float rightY)
        {
            return (BoxEye(new Vector3(-HalfIpd, leftY, 0f)), BoxEye(new Vector3(HalfIpd, rightY, 0f)));
        }

        /// <summary>Both eyes clearly under a cap that faces up: it is turned away from both, and it goes.</summary>
        [Test]
        public void Facing_BothEyesClearlyBehind_IsExcluded()
        {
            (VpCapEye left, VpCapEye right) = BoxEyesAtHeight(-0.5f, -0.5f);
            VpCapVisibilityVerdict verdict = Classify(k_floor, Vector3.up, left, right);

            Assert.That(verdict.backFacingInBothEyes, Is.True, "half a unit under an upward cap, in both eyes");
            Assert.That(verdict.outsideBothFrustums, Is.False, "and the square is in front of both, so facing alone decides");
            Assert.That(verdict.Keep, Is.False);
        }

        /// <summary>
        /// Anything short of both eyes being past the band keeps the cap: one eye in front, an eye exactly in the
        /// plane, both inside the band, and both exactly at its edge. Just past the edge in both is where it goes.
        /// </summary>
        [Test]
        public void Facing_OneEyeInFront_InThePlane_OrInsideTheBand_IsKept()
        {
            var kept = new (float left, float right, string what)[]
            {
                (-0.5f, 0.5f, "one eye under and one over"),
                (-0.5f, 0f, "one eye under and one exactly in the plane"),
                (0f, 0f, "both exactly in the plane: d = 0"),
                (-0.005f, -0.005f, "both inside the band"),
                (-FacingEpsilon, -FacingEpsilon, "both exactly at the band's edge: d = -epsilon is not past it"),
            };

            foreach ((float leftY, float rightY, string what) in kept)
            {
                (VpCapEye left, VpCapEye right) = BoxEyesAtHeight(leftY, rightY);
                VpCapVisibilityVerdict verdict = Classify(k_floor, Vector3.up, left, right);
                Assert.That(verdict.backFacingInBothEyes, Is.False, what);
                Assert.That(verdict.Keep, Is.True, what);
            }

            (VpCapEye pastLeft, VpCapEye pastRight) = BoxEyesAtHeight(-0.02f, -0.02f);
            Assert.That(
                Classify(k_floor, Vector3.up, pastLeft, pastRight).backFacingInBothEyes, Is.True,
                "twice the band under, in both eyes, is past it");
        }

        /// <summary>
        /// The two caps of one cut are one square in opposite directions, and each is asked with its own normal: what
        /// sends one away keeps the other, and eyes in the plane keep both. Neither is hidden for the other's sake.
        /// </summary>
        [Test]
        public void Facing_JudgesEachSideWithItsOwnNormal()
        {
            (VpCapEye underLeft, VpCapEye underRight) = BoxEyesAtHeight(-0.5f, -0.5f);
            Assert.That(Classify(k_floor, Vector3.up, underLeft, underRight).Keep, Is.False, "from under: the upward cap goes");
            Assert.That(
                Classify(k_floorReversed, Vector3.down, underLeft, underRight).Keep, Is.True, "and the downward one stays");

            (VpCapEye overLeft, VpCapEye overRight) = BoxEyesAtHeight(0.5f, 0.5f);
            Assert.That(Classify(k_floor, Vector3.up, overLeft, overRight).Keep, Is.True, "from over: the upward cap stays");
            Assert.That(
                Classify(k_floorReversed, Vector3.down, overLeft, overRight).Keep, Is.False, "and the downward one goes");

            (VpCapEye inLeft, VpCapEye inRight) = BoxEyesAtHeight(0f, 0f);
            Assert.That(Classify(k_floor, Vector3.up, inLeft, inRight).Keep, Is.True, "in the plane: both stay");
            Assert.That(Classify(k_floorReversed, Vector3.down, inLeft, inRight).Keep, Is.True);
        }

        /// <summary>
        /// A square at depth 1 facing the eyes, well to the right: outside the left eye's field and the right eye's, so
        /// it goes. Moved so that only the right eye has it in view, it stays.
        /// </summary>
        [Test]
        public void Frustum_OutsideBothEyes_IsExcluded_AndInsideOneEyeOnly_IsKept()
        {
            VpCapEye left = BoxEye(new Vector3(-1f, 0f, 0f));
            VpCapEye right = BoxEye(new Vector3(1f, 0f, 0f));

            // x from 5 to 6 at depth 1: 6 to 7 right of the left eye, 4 to 5 right of the right one, both past |x| <= 1.
            Vector3[] farRight = Square(5f, 6f, 1f);
            VpCapVisibilityVerdict gone = Classify(farRight, Vector3.back, left, right);
            Assert.That(gone.outsideBothFrustums, Is.True, "right of both fields");
            Assert.That(gone.backFacingInBothEyes, Is.False, "and facing both eyes, so the frustum alone decides");
            Assert.That(gone.Keep, Is.False);

            // x from 1.5 to 2.5: 2.5 to 3.5 right of the left eye (outside), 0.5 to 1.5 right of the right one (inside).
            Vector3[] oneEye = Square(1.5f, 2.5f, 1f);
            VpCapVisibilityVerdict kept = Classify(oneEye, Vector3.back, left, right);
            Assert.That(kept.outsideBothFrustums, Is.False, "the right eye has it in view");
            Assert.That(kept.Keep, Is.True);
        }

        /// <summary>
        /// Keeping on doubt: a square touching the edge of the field, a floor crossing the near plane and running behind
        /// the eye, a board larger than the field whose every corner is outside some plane, and a square whose centre is
        /// outside while a corner is in — all kept. A square wholly behind the eye, one wholly past the far plane, and a
        /// board that crosses the near plane and runs behind the eye but lies wholly above the field all go: crossing the
        /// near plane is no reason to reject by itself, and no reason to keep either. Both eyes are one here, so each
        /// case is one eye's answer.
        /// </summary>
        [Test]
        public void Frustum_KeepsTheBoundary_TheNearPlaneCrossing_AndAPolygonAroundTheField()
        {
            VpCapEye eye = BoxEye(Vector3.zero);

            var kept = new (Vector3[] polygon, Vector3 normal, string what)[]
            {
                (Square(1f, 2f, 1f), Vector3.back, "the left edge exactly on x = w"),
                (
                    new[]
                    {
                        new Vector3(-0.5f, 0f, -1f), new Vector3(-0.5f, 0f, 2f), new Vector3(0.5f, 0f, 2f), new Vector3(0.5f, 0f, -1f),
                    },
                    Vector3.up,
                    "a floor at eye height from behind the eye (w < 0) through the near plane to depth 2"),
                (Square(-10f, 10f, 2f, -10f, 10f), Vector3.back, "a board around the whole field: every corner outside, none shared"),
                (Square(0.5f, 5f, 1f), Vector3.back, "the centre at x = 2.75 is outside, the corner at 0.5 is in"),
            };

            foreach ((Vector3[] polygon, Vector3 normal, string what) in kept)
            {
                VpCapVisibilityVerdict verdict = Classify(polygon, normal, eye, eye);
                Assert.That(verdict.outsideBothFrustums, Is.False, what);
                Assert.That(verdict.Keep, Is.True, what);
            }

            // One eye exactly on the edge is enough, with the other clearly outside.
            VpCapVisibilityVerdict oneTouching = Classify(Square(1f, 2f, 1f), Vector3.back, eye, BoxEye(new Vector3(-1f, 0f, 0f)));
            Assert.That(oneTouching.Keep, Is.True, "touching in one eye, outside in the other");

            Assert.That(
                Classify(Square(-0.5f, 0.5f, -2f), Vector3.forward, eye, eye).outsideBothFrustums, Is.True,
                "wholly behind the eye, facing it");
            Assert.That(
                Classify(Square(-0.5f, 0.5f, 4f), Vector3.back, eye, eye).outsideBothFrustums, Is.True,
                "wholly past the far plane at 3");

            // At y = 5 every vertex is above y = w: 5 > 2 in front, and 5 > -1 behind the eye where w = -1.
            var aboveAcrossTheNearPlane = new[]
            {
                new Vector3(-0.5f, 5f, -1f), new Vector3(-0.5f, 5f, 2f), new Vector3(0.5f, 5f, 2f), new Vector3(0.5f, 5f, -1f),
            };
            VpCapVisibilityVerdict above = Classify(aboveAcrossTheNearPlane, Vector3.down, eye, eye);
            Assert.That(above.backFacingInBothEyes, Is.False, "it faces down at the eye below it");
            Assert.That(above.outsideBothFrustums, Is.True, "crossing the near plane, but wholly above the field");
        }

        // ----- values that are not finite -------------------------------------------------------------------------

        /// <summary>
        /// A square above the field (y 5 to 6 at depth 1) turned away from eyes at the origin: with finite values both
        /// rules exclude it, so every case below that keeps it is the finiteness check at work, not the geometry.
        /// </summary>
        private static Vector3[] AboveAndTurnedAway()
        {
            return Square(-0.2f, 0.2f, 1f, 5f, 6f);
        }

        private static (VpCapEye left, VpCapEye right) OriginEyes()
        {
            return (BoxEye(new Vector3(-HalfIpd, 0f, 0f)), BoxEye(new Vector3(HalfIpd, 0f, 0f)));
        }

        private static Matrix4x4 WithElement(Matrix4x4 matrix, int index, float value)
        {
            matrix[index] = value;
            return matrix;
        }

        private static Matrix4x4 Scaled(Matrix4x4 matrix, float factor)
        {
            for (int i = 0; i < 16; i++)
            {
                matrix[i] *= factor;
            }

            return matrix;
        }

        /// <summary>
        /// A polygon vertex or an outward normal that is NaN or infinite keeps the cap: neither rule is confirmed. The
        /// NaN in one vertex's x is the case where the other comparisons would still have put it above the field, and
        /// the infinite normal the one where d would have been -infinity.
        /// </summary>
        [Test]
        public void NonFinite_PolygonOrNormal_KeepsTheCap()
        {
            (VpCapEye left, VpCapEye right) = OriginEyes();
            VpCapVisibilityVerdict finite = Classify(AboveAndTurnedAway(), Vector3.forward, left, right);
            Assert.That(finite.backFacingInBothEyes, Is.True, "finite: turned away from both eyes");
            Assert.That(finite.outsideBothFrustums, Is.True, "and above both fields");

            var vertexCases = new (int vertex, int axis, float value)[]
            {
                (0, 0, float.NaN), (1, 0, float.NaN), (2, 1, float.PositiveInfinity), (3, 2, float.NegativeInfinity),
            };
            foreach ((int vertex, int axis, float value) in vertexCases)
            {
                Vector3[] polygon = AboveAndTurnedAway();
                polygon[vertex][axis] = value;
                VpCapVisibilityVerdict verdict = Classify(polygon, Vector3.forward, left, right);
                string what = "vertex " + vertex + " axis " + axis + " = " + value;
                Assert.That(verdict.backFacingInBothEyes, Is.False, what);
                Assert.That(verdict.outsideBothFrustums, Is.False, what);
            }

            foreach (Vector3 normal in new[]
                     {
                         new Vector3(0f, 0f, float.PositiveInfinity), new Vector3(float.NaN, 0f, 1f),
                         new Vector3(0f, float.NegativeInfinity, 1f),
                     })
            {
                VpCapVisibilityVerdict verdict = Classify(AboveAndTurnedAway(), normal, left, right);
                Assert.That(verdict.backFacingInBothEyes, Is.False, "normal " + normal);
                Assert.That(verdict.outsideBothFrustums, Is.False, "normal " + normal);
            }
        }

        /// <summary>
        /// One eye that is not finite — its position or any element of its matrix — confirms neither rule, and the other
        /// eye alone, which would exclude on both, does not.
        /// </summary>
        [Test]
        public void NonFinite_OneEye_ConfirmsNeitherRule()
        {
            (VpCapEye left, VpCapEye right) = OriginEyes();
            var badLeft = new[]
            {
                (new VpCapEye(new Vector3(float.NaN, 0f, 0f), left.worldToClip), "left position NaN"),
                (new VpCapEye(new Vector3(0f, 0f, float.NegativeInfinity), left.worldToClip), "left position -infinity"),
                (new VpCapEye(left.position, WithElement(left.worldToClip, 13, float.NaN)), "left matrix NaN"),
                (new VpCapEye(left.position, WithElement(left.worldToClip, 0, float.PositiveInfinity)), "left matrix +infinity"),
            };

            foreach ((VpCapEye bad, string what) in badLeft)
            {
                VpCapVisibilityVerdict verdict = Classify(AboveAndTurnedAway(), Vector3.forward, bad, right);
                Assert.That(verdict.backFacingInBothEyes, Is.False, what);
                Assert.That(verdict.outsideBothFrustums, Is.False, what);
                Assert.That(verdict.Keep, Is.True, what);

                VpCapVisibilityVerdict swapped = Classify(AboveAndTurnedAway(), Vector3.forward, right, bad);
                Assert.That(swapped.Keep, Is.True, what + ", given as the right eye");
            }
        }

        /// <summary>
        /// Finite input can still overflow. A facing distance that comes out infinite does not confirm facing, and the
        /// frustum still decides on its own; clip coordinates that come out infinite do not confirm the frustum, and
        /// facing still decides on its own. The same matrix scaled less, with no overflow, is the same frustum and still
        /// rejects, so it is the overflow and not the scale that is not trusted.
        /// </summary>
        [Test]
        public void NonFinite_ResultsFromFiniteInput_ConfirmNothing()
        {
            (VpCapEye left, VpCapEye right) = OriginEyes();

            // d = 3e38 * (0 - 5) + 3e38 * (0 - 1), which is past float's range: -infinity.
            var huge = new Vector3(0f, 3e38f, 3e38f);
            VpCapVisibilityVerdict facingOverflow = Classify(AboveAndTurnedAway(), huge, left, right);
            Assert.That(facingOverflow.backFacingInBothEyes, Is.False, "an overflowed distance confirms nothing");
            Assert.That(facingOverflow.outsideBothFrustums, Is.True, "while the frustum is still judged");

            // Every element finite; y = 6 times 1e38 is past float's range.
            var overflowLeft = new VpCapEye(left.position, Scaled(left.worldToClip, 1e38f));
            var overflowRight = new VpCapEye(right.position, Scaled(right.worldToClip, 1e38f));
            VpCapVisibilityVerdict clipOverflow = Classify(AboveAndTurnedAway(), Vector3.forward, overflowLeft, overflowRight);
            Assert.That(clipOverflow.outsideBothFrustums, Is.False, "overflowed clip coordinates confirm nothing");
            Assert.That(clipOverflow.backFacingInBothEyes, Is.True, "while facing is still judged");

            var scaledLeft = new VpCapEye(left.position, Scaled(left.worldToClip, 1e30f));
            var scaledRight = new VpCapEye(right.position, Scaled(right.worldToClip, 1e30f));
            Assert.That(
                Classify(AboveAndTurnedAway(), Vector3.forward, scaledLeft, scaledRight).outsideBothFrustums, Is.True,
                "scaled by 1e30 with nothing overflowing, the same frustum still rejects it");
        }

        /// <summary>A square at depth <paramref name="z"/>, x from <paramref name="x0"/> to <paramref name="x1"/>, y from -0.2 to 0.2 unless given.</summary>
        private static Vector3[] Square(float x0, float x1, float z, float y0 = -0.2f, float y1 = 0.2f)
        {
            return new[] { new Vector3(x0, y0, z), new Vector3(x0, y1, z), new Vector3(x1, y1, z), new Vector3(x1, y0, z) };
        }

        // ----- real caps, from the display ------------------------------------------------------------------------

        private static readonly float3[] k_controlPoints =
        {
            new float3(-1.0f, 0.0f, -1.0f), new float3(1.0f, 0.0f, -1.0f), new float3(1.0f, 0.0f, 1.0f), new float3(-1.0f, 0.0f, 1.0f),
            new float3(-1.0f, 2.0f, -1.0f), new float3(1.0f, 2.0f, -1.0f), new float3(1.0f, 2.0f, 1.0f), new float3(-1.0f, 2.0f, 1.0f),
        };

        private static readonly int[][] k_faces =
        {
            new[] { 0, 4, 5, 1 }, new[] { 1, 5, 6, 2 }, new[] { 2, 6, 7, 3 }, new[] { 3, 7, 4, 0 },
            new[] { 0, 1, 2, 3 }, new[] { 4, 7, 6, 5 },
        };

        /// <summary>
        /// The cube's own plane y = 1. The positive side, above it, is free and drawn <see cref="Separation"/> further
        /// along +y; the negative side holds the anchor and stays. So in the cube's own frame the positive cap is the
        /// square at y = 1.25 facing down, and the negative cap the square at y = 1 facing up.
        /// </summary>
        private static readonly float4 k_plane = new float4(0f, 1f, 0f, -1f);

        private static readonly float3 k_lowAnchor = new float3(0f, 0.2f, 0f);

        private static VpStoredGeometry AppendCube(VpCpuGeometryStorage storage)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            foreach (int[] c in k_faces)
            {
                float3 n = math.normalize(math.cross(
                    k_controlPoints[c[1]] - k_controlPoints[c[0]], k_controlPoints[c[2]] - k_controlPoints[c[0]]));
                uint b = (uint)vertices.Count;
                for (int k = 0; k < 4; k++)
                {
                    vertices.Add(new VpRenderVertex { position = k_controlPoints[c[k]], normal = n, uv0 = new float2(0.5f, 0.5f) });
                    topology.Add(c[k]);
                }

                indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
            }

            Assert.That(
                storage.TryAppendPrepared(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), k_controlPoints.Length,
                    new[] { new VpGeometrySubmesh(0, indices.Count, BodyMaterial) }, out VpStoredGeometry geometry),
                Is.True,
                "append the cube");
            return geometry;
        }

        /// <summary>
        /// A display showing the cube at <paramref name="objectToWorld"/>, cut across its middle and settled, so that it
        /// holds exactly the two caps of that cut.
        /// </summary>
        private VpLogicalCutDisplay CutCubeAt(VpCpuGeometryStorage storage, Matrix4x4 objectToWorld)
        {
            var table = new VpGeometryReferenceTable(storage, 8, 8);
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(4));
            LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_lowAnchor });
            VpStoredGeometry geometry = AppendCube(storage);

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader) { name = "cap visibility body" };
            _objects.Add(material);

            Assert.That(
                VpLogicalCutDisplay.TryCreate(
                    storage, table, ledger, new Dictionary<int, Material> { { BodyMaterial, material } }, null, null, 16, 16,
                    () => 1, out VpLogicalCutDisplay display),
                Is.True,
                "create the display");
            display.Separation = Separation;

            Assert.That(display.TryShow(source, geometry, objectToWorld), Is.True, "show the cube");
            Assert.That(ledger.Admit(source, k_plane, true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(
                ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(display.TryBeginFrame(), Is.True, "settle the split");
            Assert.That(display.CapRecordCount, Is.EqualTo(2), "the two caps of the one cut");
            return display;
        }

        /// <summary>The index of the cap on <paramref name="side"/>, found by the record's own side rather than by position.</summary>
        private static int CapIndexOf(VpLogicalCutDisplay display, float side)
        {
            for (int i = 0; i < display.CapRecordCount; i++)
            {
                Assert.That(display.TryGetCapRecord(i, out LogicalCutCapRecord record), Is.True);
                if (record.side == side)
                {
                    return i;
                }
            }

            Assert.Fail("no cap on side " + side);
            return -1;
        }

        private static VpCapVisibilityVerdict Judge(VpLogicalCutDisplay display, int capIndex, (VpCapEye left, VpCapEye right) eyes)
        {
            Assert.That(
                VpCapVisibility.TryClassify(display, capIndex, eyes.left, eyes.right, FacingEpsilon, out VpCapVisibilityVerdict verdict),
                Is.True,
                "cap " + capIndex + " is there to judge");
            return verdict;
        }

        private static void AssertVector(Vector3 actual, Vector3 expected, string what)
        {
            Assert.That((actual - expected).magnitude, Is.LessThan(1e-4f), what + ": expected " + expected + " but was " + actual);
        }

        /// <summary>
        /// The real caps at the identity, with the free side's separation in them. From between the two caps — above
        /// the face at y = 1 but below the separated cap at 1.25 — both caps face the eyes and both stay, the fixed one
        /// included; had the separation been left out, the positive cap would have been behind them. From above, the
        /// downward cap goes and the upward one stays; from below, the other way round.
        /// </summary>
        [Test]
        public void RealCaps_AreJudgedFromTheirRecord_WithTheSeparationInIt()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            using (VpLogicalCutDisplay display = CutCubeAt(storage, Matrix4x4.identity))
            {
                int positive = CapIndexOf(display, 1f);
                int negative = CapIndexOf(display, -1f);

                // The layout the expectations below are read from.
                Assert.That(display.TryGetCapRecord(positive, out LogicalCutCapRecord up), Is.True);
                Assert.That(display.TryGetCapRecord(negative, out LogicalCutCapRecord down), Is.True);
                AssertVector(up.offset, new Vector3(0f, Separation, 0f), "the free positive side is drawn apart along +y");
                AssertVector(up.outwardNormal, Vector3.down, "and closes downwards");
                Assert.That(down.fixedByAnchors, Is.True, "the negative side holds the anchor");
                AssertVector(down.offset, Vector3.zero, "and stays at y = 1");
                AssertVector(down.outwardNormal, Vector3.up, "closing upwards");

                (VpCapEye, VpCapEye) between = CameraEyes(new Vector3(0f, 1.1f, -5f), Vector3.forward, Vector3.right);
                Assert.That(Judge(display, positive, between).Keep, Is.True, "0.15 under the separated cap, which faces down");
                Assert.That(Judge(display, negative, between).Keep, Is.True, "0.1 over the fixed cap, which faces up");

                (VpCapEye, VpCapEye) above = CameraEyes(new Vector3(0f, 3f, -5f), Vector3.forward, Vector3.right);
                VpCapVisibilityVerdict positiveFromAbove = Judge(display, positive, above);
                Assert.That(positiveFromAbove.backFacingInBothEyes, Is.True, "from above, the downward cap is turned away");
                Assert.That(positiveFromAbove.outsideBothFrustums, Is.False, "though it is in view");
                Assert.That(Judge(display, negative, above).Keep, Is.True, "and the upward cap stays");

                (VpCapEye, VpCapEye) below = CameraEyes(new Vector3(0f, -2f, -5f), Vector3.forward, Vector3.right);
                Assert.That(Judge(display, positive, below).Keep, Is.True, "from below, the downward cap stays");
                Assert.That(Judge(display, negative, below).backFacingInBothEyes, Is.True, "and the upward one is turned away");
            }
        }

        /// <summary>
        /// The cube placed turned a quarter about z and moved to x = 10: its plane y = 1 is now world x = 9, the
        /// positive cap faces +x and the negative one -x. Eyes at x = -10 looking along +x see the negative cap's front
        /// and the positive cap's back — the opposite of what the cube's own frame would say for eyes at its height.
        /// </summary>
        [Test]
        public void APlacedCube_IsJudgedWhereItWasPlaced()
        {
            Matrix4x4 placement = Matrix4x4.TRS(new Vector3(10f, 0f, 0f), Quaternion.Euler(0f, 0f, 90f), Vector3.one);
            using (VpCpuGeometryStorage storage = NewStorage())
            using (VpLogicalCutDisplay display = CutCubeAt(storage, placement))
            {
                int positive = CapIndexOf(display, 1f);
                int negative = CapIndexOf(display, -1f);

                Assert.That(display.TryGetCapRecord(positive, out LogicalCutCapRecord up), Is.True);
                Assert.That(display.TryGetCapRecord(negative, out LogicalCutCapRecord down), Is.True);
                AssertVector(up.outwardNormal, Vector3.right, "the quarter turn takes the positive cap's -y to +x");
                AssertVector(down.outwardNormal, Vector3.left, "and the negative cap's +y to -x");
                Assert.That(display.TryGetCapVertex(negative, 0, out Vector3 onFace), Is.True);
                Assert.That(onFace.x, Is.EqualTo(9f).Within(1e-4f), "the face is at world x = 9");

                (VpCapEye, VpCapEye) fromLeft = CameraEyes(new Vector3(-10f, 0f, 0f), Vector3.right, Vector3.back);
                VpCapVisibilityVerdict positiveVerdict = Judge(display, positive, fromLeft);
                Assert.That(positiveVerdict.backFacingInBothEyes, Is.True, "the +x cap is turned away from eyes at x = -10");
                Assert.That(positiveVerdict.outsideBothFrustums, Is.False, "though both have it in view");
                Assert.That(Judge(display, negative, fromLeft).Keep, Is.True, "and the -x cap faces them");
            }
        }

        /// <summary>
        /// Each call is its own question. The same caps and eyes turned round go out of view, one eye turned back is
        /// enough to keep them, and both turned back give the first answer again. The same eyes on a cube placed
        /// behind them see nothing.
        /// </summary>
        [Test]
        public void ANewCall_WithAnotherViewOrPlacement_IsJudgedAfresh()
        {
            var eyePosition = new Vector3(0f, 1.1f, -5f);
            (VpCapEye left, VpCapEye right) ahead = CameraEyes(eyePosition, Vector3.forward, Vector3.right);
            (VpCapEye left, VpCapEye right) turned = CameraEyes(eyePosition, Vector3.back, Vector3.right);

            using (VpCpuGeometryStorage storage = NewStorage())
            using (VpLogicalCutDisplay display = CutCubeAt(storage, Matrix4x4.identity))
            {
                int positive = CapIndexOf(display, 1f);

                Assert.That(Judge(display, positive, ahead).Keep, Is.True, "looking at the cube");

                VpCapVisibilityVerdict away = Judge(display, positive, turned);
                Assert.That(away.outsideBothFrustums, Is.True, "looking away from it, in both eyes");
                Assert.That(away.backFacingInBothEyes, Is.False, "from the same place, so it still faces them");
                Assert.That(away.Keep, Is.False);

                Assert.That(Judge(display, positive, (turned.left, ahead.right)).Keep, Is.True, "one eye turned back keeps it");
                Assert.That(Judge(display, positive, ahead).Keep, Is.True, "and both turned back give the first answer again");
            }

            // The same eyes, the cube placed 20 behind the first one: its caps are behind both eyes.
            using (VpCpuGeometryStorage storage = NewStorage())
            using (VpLogicalCutDisplay display = CutCubeAt(storage, Matrix4x4.Translate(new Vector3(0f, 0f, -20f))))
            {
                VpCapVisibilityVerdict behind = Judge(display, CapIndexOf(display, 1f), ahead);
                Assert.That(behind.outsideBothFrustums, Is.True, "the cube moved behind the eyes is out of both views");
                Assert.That(behind.Keep, Is.False);
            }
        }

        /// <summary>
        /// Without XR the one camera stands for both eyes: the mono answer is the two-eye answer with that camera as
        /// each. Asked of a cap that is not there, nothing is judged; a facing epsilon that is not a length is refused.
        /// </summary>
        [Test]
        public void Mono_IsTheOneCameraAsBothEyes_AndBadArgumentsAreRefused()
        {
            using (VpCpuGeometryStorage storage = NewStorage())
            using (VpLogicalCutDisplay display = CutCubeAt(storage, Matrix4x4.identity))
            {
                int positive = CapIndexOf(display, 1f);

                VpCapEye above = CameraEye(new Vector3(0f, 3f, -5f), Vector3.forward);
                Assert.That(VpCapVisibility.TryClassifyMono(display, positive, above, FacingEpsilon, out VpCapVisibilityVerdict gone), Is.True);
                Assert.That(gone.backFacingInBothEyes, Is.True, "one camera above the downward cap: turned away");
                Assert.That(gone.Keep, Is.False);

                VpCapEye between = CameraEye(new Vector3(0f, 1.1f, -5f), Vector3.forward);
                Assert.That(VpCapVisibility.TryClassifyMono(display, positive, between, FacingEpsilon, out VpCapVisibilityVerdict kept), Is.True);
                Assert.That(kept.Keep, Is.True, "one camera under it: facing it");

                Assert.That(
                    VpCapVisibility.TryClassifyMono(display, display.CapRecordCount, between, FacingEpsilon, out VpCapVisibilityVerdict none),
                    Is.False,
                    "no such cap");
                Assert.That(none.Keep, Is.True, "and the default verdict excludes nothing");

                foreach (float bad in new[] { -0.01f, float.NaN, float.PositiveInfinity })
                {
                    Assert.Throws<ArgumentOutOfRangeException>(
                        () => VpCapVisibility.TryClassifyMono(display, positive, between, bad, out _), "epsilon " + bad);
                }
            }

            Assert.Throws<ArgumentNullException>(
                () => VpCapVisibility.TryClassifyMono(null, 0, BoxEye(Vector3.zero), FacingEpsilon, out _));
        }

        private static VpCpuGeometryStorage NewStorage()
        {
            return new VpCpuGeometryStorage(2048, 8192, 32, 128, 128, Allocator.Persistent);
        }
    }
}
