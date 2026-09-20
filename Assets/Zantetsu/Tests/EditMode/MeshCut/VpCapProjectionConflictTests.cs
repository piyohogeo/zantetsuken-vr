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
    /// The conservative projection conflict test between two stencil targets (DESIGN 5.6, T-066).
    /// <para>
    /// Written-out targets are seen by eyes looking along +z whose clip matrix is <c>(x, y, 1.2z - 2.2, z)</c> relative
    /// to the eye — a square 90 degree field, near 1, far 11 — so a point at depth z is at <c>(x / z, y / z)</c> on the
    /// screen and a layout meant to touch does. Every expectation is read off that layout. Then two real caps from
    /// logical cut displays are asked through the adapter.
    /// </para>
    /// <para>
    /// Compatibility and visibility are the existing tests' subjects; here they are used only where the combination is
    /// the point.
    /// </para>
    /// </summary>
    public class VpCapProjectionConflictTests
    {
        /// <summary>Test values only: no product margin or epsilon exists (DESIGN O-034).</summary>
        private const float PlaneEpsilon = 0.01f;
        private static readonly Vector2 k_noMargin = Vector2.zero;

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
            _ledgers.Clear();
        }

        // ----- eyes and written-out targets -----------------------------------------------------------------------

        private static VpCapEye Eye(Vector3 position)
        {
            var projection = new Matrix4x4(
                new Vector4(1f, 0f, 0f, 0f),
                new Vector4(0f, 1f, 0f, 0f),
                new Vector4(0f, 0f, 1.2f, 1f),
                new Vector4(0f, 0f, -2.2f, 0f));
            return new VpCapEye(position, projection * Matrix4x4.Translate(-position));
        }

        private static readonly LogicalCutLedger k_ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(4));
        private static readonly Vector4 k_plane = new Vector4(0f, 0f, 1f, -6f);

        /// <summary>Conditions under face <paramref name="operation"/>; different operations are incompatible.</summary>
        private static VpCapCompatibilityTarget Conditions(int operation)
        {
            return new VpCapCompatibilityTarget(
                new[] { new VpCapConstraint(new VpCapFace(k_ledger, new CutOperationId(operation)), 1f, k_plane) });
        }

        private static VpCapProjectionTarget Target(
            int operation, Bounds box, Matrix4x4 placement, params Vector3[][] caps)
        {
            return new VpCapProjectionTarget(Conditions(operation), box, placement, caps, true);
        }

        private static VpCapProjectionTarget Target(int operation, Vector3 min, Vector3 max, params Vector3[][] caps)
        {
            var box = new Bounds();
            box.SetMinMax(min, max);
            return Target(operation, box, Matrix4x4.identity, caps);
        }

        /// <summary>A square in the plane z = <paramref name="z"/>.</summary>
        private static Vector3[] Board(float x0, float x1, float y0, float y1, float z)
        {
            return new[] { new Vector3(x0, y0, z), new Vector3(x1, y0, z), new Vector3(x1, y1, z), new Vector3(x0, y1, z) };
        }

        private static Vector3[] Moved(Vector3[] polygon, Vector3 by)
        {
            var moved = new Vector3[polygon.Length];
            for (int i = 0; i < polygon.Length; i++)
            {
                moved[i] = polygon[i] + by;
            }

            return moved;
        }

        private static VpCapProjectionVerdict Judge(
            VpCapProjectionTarget a, VpCapProjectionTarget b, VpCapEye left, VpCapEye right, Vector2 margin)
        {
            VpCapProjectionVerdict verdict = VpCapProjectionConflict.Judge(a, b, left, right, margin, PlaneEpsilon);
            VpCapProjectionVerdict swapped = VpCapProjectionConflict.Judge(b, a, left, right, margin, PlaneEpsilon);
            Assert.That(swapped.left, Is.EqualTo(verdict.left), "the pair's order does not matter (left)");
            Assert.That(swapped.right, Is.EqualTo(verdict.right), "the pair's order does not matter (right)");
            return verdict;
        }

        private static VpCapEye LeftNear => Eye(new Vector3(-0.03f, 0f, 0f));
        private static VpCapEye RightNear => Eye(new Vector3(0.03f, 0f, 0f));

        /// <summary>Two boxes well left and right of the view: apart by their rectangles in both eyes.</summary>
        [Test]
        public void ApartInBothEyes_IsNoConflict()
        {
            VpCapProjectionTarget a = Target(1, new Vector3(-3.5f, -0.5f, 5.5f), new Vector3(-2.5f, 0.5f, 6.5f),
                Board(-3.5f, -2.5f, -0.5f, 0.5f, 6f));
            VpCapProjectionTarget b = Target(2, new Vector3(2.5f, -0.5f, 5.5f), new Vector3(3.5f, 0.5f, 6.5f),
                Board(2.5f, 3.5f, -0.5f, 0.5f, 6f));

            VpCapProjectionVerdict verdict = Judge(a, b, LeftNear, RightNear, k_noMargin);
            Assert.That(verdict.compatible, Is.False);
            Assert.That(verdict.left, Is.EqualTo(VpCapProjectionOverlap.ApartByBounds));
            Assert.That(verdict.right, Is.EqualTo(VpCapProjectionOverlap.ApartByBounds));
            Assert.That(verdict.MustSeparate, Is.False);
        }

        /// <summary>
        /// Eyes at x = -1 and x = +1. A small box at depth 2 and another at depth 6 lie on one line from the left eye,
        /// so they meet there — rectangles and caps — while the right eye sees them apart. One eye is enough.
        /// </summary>
        [Test]
        public void OverlapInOneEyeOnly_IsAConflict()
        {
            VpCapProjectionTarget near = Target(1, new Vector3(-0.1f, -0.1f, 1.9f), new Vector3(0.1f, 0.1f, 2.1f),
                Board(-0.1f, 0.1f, -0.1f, 0.1f, 2f));
            VpCapProjectionTarget far = Target(2, new Vector3(1.9f, -0.1f, 5.9f), new Vector3(2.1f, 0.1f, 6.1f),
                Board(1.9f, 2.1f, -0.1f, 0.1f, 6f));

            VpCapProjectionVerdict verdict = Judge(near, far, Eye(new Vector3(-1f, 0f, 0f)), Eye(new Vector3(1f, 0f, 0f)), k_noMargin);
            Assert.That(verdict.left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap), "in line from the left eye");
            Assert.That(verdict.right, Is.EqualTo(VpCapProjectionOverlap.ApartByBounds), "apart from the right one");
            Assert.That(verdict.MustSeparate, Is.True);
        }

        /// <summary>
        /// Two overlapping boxes whose caps are at their far ends: the rectangles meet, the caps do not, in both eyes —
        /// no conflict. Moved so that the caps meet, the same boxes conflict. Given the same conditions, the meeting
        /// caps need no separation: compatible targets are not asked.
        /// </summary>
        [Test]
        public void MeetingBoxes_AreDecidedByTheirCaps_AndCompatibleTargetsAreNotAsked()
        {
            var minA = new Vector3(-1.5f, -1f, 5f);
            var maxA = new Vector3(0.5f, 1f, 7f);
            var minB = new Vector3(-0.5f, -1f, 5f);
            var maxB = new Vector3(1.5f, 1f, 7f);

            VpCapProjectionVerdict apart = Judge(
                Target(1, minA, maxA, Board(-1.5f, -1f, -1f, 1f, 6f)),
                Target(2, minB, maxB, Board(1f, 1.5f, -1f, 1f, 6f)),
                LeftNear, RightNear, k_noMargin);
            Assert.That(apart.left, Is.EqualTo(VpCapProjectionOverlap.ApartByCaps), "the boxes meet, the caps do not");
            Assert.That(apart.right, Is.EqualTo(VpCapProjectionOverlap.ApartByCaps));
            Assert.That(apart.MustSeparate, Is.False);

            VpCapProjectionTarget capA = Target(1, minA, maxA, Board(-1f, 0.2f, -1f, 1f, 6f));
            VpCapProjectionVerdict meeting = Judge(capA, Target(2, minB, maxB, Board(-0.2f, 1f, -1f, 1f, 6f)), LeftNear, RightNear, k_noMargin);
            Assert.That(meeting.left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap));
            Assert.That(meeting.right, Is.EqualTo(VpCapProjectionOverlap.MayOverlap));
            Assert.That(meeting.MustSeparate, Is.True, "incompatible caps that meet");

            VpCapProjectionVerdict shared = Judge(capA, Target(1, minB, maxB, Board(-0.2f, 1f, -1f, 1f, 6f)), LeftNear, RightNear, k_noMargin);
            Assert.That(shared.compatible, Is.True, "the same conditions");
            Assert.That(shared.left, Is.EqualTo(VpCapProjectionOverlap.NotEvaluated));
            Assert.That(shared.right, Is.EqualTo(VpCapProjectionOverlap.NotEvaluated));
            Assert.That(shared.MustSeparate, Is.False, "compatible targets may share a stencil however they meet");
        }

        /// <summary>
        /// A target made from a list of cap arrays reads that list and those arrays themselves, not copies: the meeting
        /// boxes of the test above, apart by their caps, meet once a vertex of one cap array is moved afterwards, are
        /// apart again when it is moved back, and meet again when a cap is added to the list afterwards. Its conditions,
        /// made from an array, are read the same way.
        /// </summary>
        [Test]
        public void ATargetMadeFromCapArrays_ReadsThemAsTheyAreNow()
        {
            var minA = new Vector3(-1.5f, -1f, 5f);
            var maxA = new Vector3(0.5f, 1f, 7f);
            var minB = new Vector3(-0.5f, -1f, 5f);
            var maxB = new Vector3(1.5f, 1f, 7f);
            var boxA = new Bounds();
            boxA.SetMinMax(minA, maxA);
            var boxB = new Bounds();
            boxB.SetMinMax(minB, maxB);

            Vector3[] capOfA = Board(-1.5f, -1f, -1f, 1f, 6f);
            var capsOfA = new List<Vector3[]> { capOfA };
            VpCapConstraint[] conditionsOfA =
            {
                new VpCapConstraint(new VpCapFace(k_ledger, new CutOperationId(1)), 1f, k_plane),
            };
            var a = new VpCapProjectionTarget(
                new VpCapCompatibilityTarget(conditionsOfA), boxA, Matrix4x4.identity, capsOfA, true);
            VpCapProjectionTarget b = Target(2, minB, maxB, Board(1f, 1.5f, -1f, 1f, 6f));

            Assert.That(Judge(a, b, LeftNear, RightNear, k_noMargin).left, Is.EqualTo(VpCapProjectionOverlap.ApartByCaps), "the layout");

            Vector3 kept = capOfA[1];
            capOfA[1] = new Vector3(1.2f, -1f, 6f);
            capOfA[2] = new Vector3(1.2f, 1f, 6f);
            Assert.That(Judge(a, b, LeftNear, RightNear, k_noMargin).left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap),
                "the cap array was widened afterwards and now meets the other cap");

            capOfA[1] = kept;
            capOfA[2] = new Vector3(-1f, 1f, 6f);
            Assert.That(Judge(a, b, LeftNear, RightNear, k_noMargin).left, Is.EqualTo(VpCapProjectionOverlap.ApartByCaps), "put back");

            capsOfA.Add(Board(0.9f, 1.1f, -0.2f, 0.2f, 6f));
            Assert.That(a.visibleCaps.Count, Is.EqualTo(2), "a cap added to the list afterwards is seen");
            Assert.That(Judge(a, b, LeftNear, RightNear, k_noMargin).left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap),
                "and it meets the other cap");

            capsOfA.RemoveAt(1);
            conditionsOfA[0] = new VpCapConstraint(new VpCapFace(k_ledger, new CutOperationId(2)), 1f, k_plane);
            Assert.That(Judge(a, b, LeftNear, RightNear, k_noMargin).compatible, Is.True,
                "the condition array was changed afterwards to b's own condition: now compatible");
        }

        /// <summary>
        /// A target whose caps are not all given — one was left out by the visibility test — is not shown apart by the
        /// caps step: its volume may still leave stencil under the other's cap. With the boxes meeting, the pair may
        /// overlap; with the boxes apart, the boxes still decide. An empty cap list is no evidence either, even when it
        /// is said to be complete.
        /// </summary>
        [Test]
        public void AnOmittedCap_OrNoCapAtAll_LeavesOnlyTheBoxesToDecide()
        {
            var minA = new Vector3(-1.5f, -1f, 5f);
            var maxA = new Vector3(0.5f, 1f, 7f);
            var boxB = new Bounds();
            boxB.SetMinMax(new Vector3(-0.5f, -1f, 5f), new Vector3(1.5f, 1f, 7f));
            VpCapProjectionTarget a = Target(1, minA, maxA, Board(-1.5f, -1f, -1f, 1f, 6f));

            var omitted = new VpCapProjectionTarget(
                Conditions(2), boxB, Matrix4x4.identity, Array.Empty<Vector3[]>(), false);
            VpCapProjectionVerdict meeting = Judge(a, omitted, LeftNear, RightNear, k_noMargin);
            Assert.That(meeting.left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap), "boxes meet, a cap was left out");
            Assert.That(meeting.right, Is.EqualTo(VpCapProjectionOverlap.MayOverlap));
            Assert.That(meeting.MustSeparate, Is.True);

            var omittedButListed = new VpCapProjectionTarget(
                Conditions(2), boxB, Matrix4x4.identity, new[] { Board(1f, 1.5f, -1f, 1f, 6f) }, false);
            Assert.That(
                Judge(a, omittedButListed, LeftNear, RightNear, k_noMargin).left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap),
                "the caps given would be apart, but another was left out");

            var emptyButComplete = new VpCapProjectionTarget(
                Conditions(2), boxB, Matrix4x4.identity, Array.Empty<Vector3[]>(), true);
            Assert.That(
                Judge(a, emptyButComplete, LeftNear, RightNear, k_noMargin).left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap),
                "no cap at all is not a reason to be apart");

            var farBox = new Bounds();
            farBox.SetMinMax(new Vector3(2.5f, -1f, 5f), new Vector3(3.5f, 1f, 7f));
            var omittedFar = new VpCapProjectionTarget(
                Conditions(2), farBox, Matrix4x4.identity, Array.Empty<Vector3[]>(), false);
            VpCapProjectionVerdict boxesApart = Judge(a, omittedFar, LeftNear, RightNear, k_noMargin);
            Assert.That(boxesApart.left, Is.EqualTo(VpCapProjectionOverlap.ApartByBounds), "the boxes still decide when apart");
            Assert.That(boxesApart.right, Is.EqualTo(VpCapProjectionOverlap.ApartByBounds));
        }

        /// <summary>
        /// One eye at the origin. A box just past the right edge of the field (screen x from 1.024) and one just inside
        /// it (to 0.95): with no margin the outer one is off the screen and they are apart; with a margin of 0.06 on
        /// each side it reaches back onto the screen, the rectangles meet, and the caps (0.099 apart) meet too. Being
        /// outside a side of the field is not decided before the margin is applied.
        /// </summary>
        [Test]
        public void AShapeJustOffTheScreen_IsBroughtBackByTheMargin()
        {
            VpCapEye eye = Eye(Vector3.zero);
            VpCapProjectionTarget inside = Target(1, new Vector3(3.4f, -0.2f, 4f), new Vector3(3.8f, 0.2f, 4.1f),
                Board(3.4f, 3.8f, -0.2f, 0.2f, 4.05f));
            VpCapProjectionTarget outside = Target(2, new Vector3(4.2f, -0.2f, 4f), new Vector3(4.6f, 0.2f, 4.1f),
                Board(4.2f, 4.6f, -0.2f, 0.2f, 4.05f));

            Assert.That(
                Judge(inside, outside, eye, eye, k_noMargin).left, Is.EqualTo(VpCapProjectionOverlap.ApartByBounds),
                "no margin: the outer box is off the screen");

            VpCapProjectionVerdict withMargin = Judge(inside, outside, eye, eye, new Vector2(0.06f, 0f));
            Assert.That(withMargin.left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap), "a margin of 0.06 brings it back");
            Assert.That(withMargin.MustSeparate, Is.True);
        }

        /// <summary>
        /// The separation, a rotation and a translation all reach the projection. The same box and cap as another
        /// target meet it; moved 4 to the side by its separation, they are apart. A plank lying flat below a small box
        /// is apart from it; turned upright, it reaches the box.
        /// </summary>
        [Test]
        public void TheSeparation_ARotation_AndATranslation_ReachTheProjection()
        {
            var box = new Bounds(Vector3.zero, new Vector3(1f, 1f, 1f));
            Matrix4x4 atSix = Matrix4x4.Translate(new Vector3(0f, 0f, 6f));
            Vector3[] cap = Board(-0.5f, 0.5f, -0.5f, 0.5f, 6f);
            VpCapProjectionTarget still = Target(1, box, atSix, cap);

            VpCapProjectionVerdict together = Judge(still, Target(2, box, atSix, cap), LeftNear, RightNear, k_noMargin);
            Assert.That(together.MustSeparate, Is.True, "the same place");

            var apartBy = new Vector3(4f, 0f, 0f);
            Matrix4x4 asideAtSix = Matrix4x4.Translate(new Vector3(apartBy.x, 0f, 6f));
            VpCapProjectionVerdict separated = Judge(
                still, Target(2, box, asideAtSix, Moved(cap, apartBy)), LeftNear, RightNear, k_noMargin);
            Assert.That(separated.left, Is.EqualTo(VpCapProjectionOverlap.ApartByBounds), "put apart by its placement");
            Assert.That(separated.right, Is.EqualTo(VpCapProjectionOverlap.ApartByBounds));

            // A plank 4 long and 0.2 thick at depth 6, and a small box 1.5 above its middle.
            var plank = new Bounds(Vector3.zero, new Vector3(4f, 0.2f, 0.2f));
            VpCapProjectionTarget small = Target(2, new Vector3(-0.1f, 1.4f, 5.9f), new Vector3(0.1f, 1.6f, 6.1f),
                Board(-0.1f, 0.1f, 1.4f, 1.6f, 6f));

            VpCapProjectionVerdict flat = Judge(
                Target(1, plank, atSix, Board(-2f, 2f, -0.1f, 0.1f, 6f)), small, LeftNear, RightNear, k_noMargin);
            Assert.That(flat.left, Is.EqualTo(VpCapProjectionOverlap.ApartByBounds), "lying flat, below the box");
            Assert.That(flat.right, Is.EqualTo(VpCapProjectionOverlap.ApartByBounds));

            Matrix4x4 upright = Matrix4x4.TRS(new Vector3(0f, 0f, 6f), Quaternion.Euler(0f, 0f, 90f), Vector3.one);
            VpCapProjectionVerdict turned = Judge(
                Target(1, plank, upright, Board(-0.1f, 0.1f, -2f, 2f, 6f)), small, LeftNear, RightNear, k_noMargin);
            Assert.That(turned.left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap), "turned upright, through the box");
            Assert.That(turned.MustSeparate, Is.True);
        }

        /// <summary>
        /// One eye at the origin. Boxes touching at x = 0 are not apart, nor are their touching caps. A box whose
        /// nearest screen x is 1/8 against one ending at 0: a margin of 1/16 on each side takes up the gap exactly and
        /// leaves the rectangles meeting, though the caps (1/6 apart) are still apart; half that margin leaves the
        /// rectangles apart; a margin of 0.09 closes the caps' gap too.
        /// </summary>
        [Test]
        public void Touching_IsNotApart_AndTheMarginDecidesItsOwnEdge()
        {
            VpCapEye eye = Eye(Vector3.zero);
            VpCapProjectionTarget left = Target(1, new Vector3(-1f, -0.5f, 4f), new Vector3(0f, 0.5f, 8f),
                Board(-1f, 0f, -0.5f, 0.5f, 6f));

            VpCapProjectionVerdict touching = Judge(
                left, Target(2, new Vector3(0f, -0.5f, 4f), new Vector3(1f, 0.5f, 8f), Board(0f, 1f, -0.5f, 0.5f, 6f)),
                eye, eye, k_noMargin);
            Assert.That(touching.left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap), "touching is not apart");

            VpCapProjectionTarget right = Target(2, new Vector3(1f, -0.5f, 4f), new Vector3(2f, 0.5f, 8f),
                Board(1f, 2f, -0.5f, 0.5f, 6f));
            Assert.That(
                Judge(left, right, eye, eye, new Vector2(0.0625f, 0f)).left, Is.EqualTo(VpCapProjectionOverlap.ApartByCaps),
                "a gap of 1/8 against two margins of 1/16: the rectangles meet, the caps are 1/6 apart");
            Assert.That(
                Judge(left, right, eye, eye, new Vector2(0.03125f, 0f)).left, Is.EqualTo(VpCapProjectionOverlap.ApartByBounds),
                "half that margin leaves the rectangles apart");
            Assert.That(
                Judge(left, right, eye, eye, new Vector2(0.09f, 0f)).left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap),
                "a margin of 0.09 on each closes the caps' 1/6");
        }

        /// <summary>
        /// A box reaching from behind the eye (z = -0.5) to depth 4 is not divided: it may be anywhere. Dividing every
        /// corner would have given it the screen x range [-0.5, 4] and set it apart from a box at [-0.9, -0.68], which
        /// the part of it in front of the eye does reach. A box crossing the near plane with every corner still in
        /// front of the eye is divided, and its projection bounds the part that is drawn.
        /// </summary>
        [Test]
        public void ABoxBehindTheEye_IsNotShrunkByTheDivision_AndANearPlaneCrossingIsBounded()
        {
            VpCapEye eye = Eye(Vector3.zero);
            VpCapProjectionTarget screenLeft = Target(1, new Vector3(-3.6f, -0.2f, 4f), new Vector3(-2.8f, 0.2f, 4.1f),
                Board(-3.6f, -2.8f, -0.2f, 0.2f, 4.05f));

            var floorBehind = new[]
            {
                new Vector3(-2f, 0f, -0.5f), new Vector3(-1.5f, 0f, -0.5f), new Vector3(-1.5f, 0f, 4f), new Vector3(-2f, 0f, 4f),
            };
            VpCapProjectionTarget behind = Target(2, new Vector3(-2f, -0.5f, -0.5f), new Vector3(-1.5f, 0.5f, 4f), floorBehind);
            VpCapProjectionVerdict fromBehind = Judge(screenLeft, behind, eye, eye, k_noMargin);
            Assert.That(fromBehind.left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap), "reaching behind the eye");
            Assert.That(fromBehind.MustSeparate, Is.True);

            var floorNear = new[]
            {
                new Vector3(1f, 0f, 0.5f), new Vector3(1.5f, 0f, 0.5f), new Vector3(1.5f, 0f, 4f), new Vector3(1f, 0f, 4f),
            };
            VpCapProjectionTarget acrossNear = Target(2, new Vector3(1f, -0.5f, 0.5f), new Vector3(1.5f, 0.5f, 4f), floorNear);
            VpCapProjectionTarget screenRight = Target(1, new Vector3(2.4f, -0.2f, 4f), new Vector3(2.8f, 0.2f, 4.1f),
                Board(2.4f, 2.8f, -0.2f, 0.2f, 4.05f));
            Assert.That(
                Judge(screenRight, acrossNear, eye, eye, k_noMargin).left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap),
                "crossing the near plane in front of the eye: screen x from 0.25 to 3, over the other box");
        }

        /// <summary>
        /// A board far larger than the field, every corner outside a different plane, in front of a small box: it is
        /// in view and covers the box, so the two conflict.
        /// </summary>
        [Test]
        public void ABoardWhoseCornersAreOutsideDifferentPlanes_IsNotApart()
        {
            VpCapProjectionTarget small = Target(1, new Vector3(-0.1f, -0.1f, 5.9f), new Vector3(0.1f, 0.1f, 6.1f),
                Board(-0.1f, 0.1f, -0.1f, 0.1f, 6f));
            VpCapProjectionTarget huge = Target(2, new Vector3(-100f, -100f, 4f), new Vector3(100f, 100f, 5f),
                Board(-100f, 100f, -100f, 100f, 4.5f));

            VpCapProjectionVerdict verdict = Judge(small, huge, LeftNear, RightNear, k_noMargin);
            Assert.That(verdict.left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap));
            Assert.That(verdict.right, Is.EqualTo(VpCapProjectionOverlap.MayOverlap));
        }

        /// <summary>
        /// The layout of the apart-by-caps case, broken one value at a time: a cap vertex, the placement, one eye's
        /// matrix, and a finite matrix whose results overflow. None of them can be shown apart,
        /// so each must be separated — in the eye it broke. A margin that is not a finite non-negative value is refused.
        /// </summary>
        [Test]
        public void NotFinite_NeverShowsApart()
        {
            var minA = new Vector3(-1.5f, -1f, 5f);
            var maxA = new Vector3(0.5f, 1f, 7f);
            var minB = new Vector3(-0.5f, -1f, 5f);
            var maxB = new Vector3(1.5f, 1f, 7f);
            VpCapProjectionTarget a = Target(1, minA, maxA, Board(-1.5f, -1f, -1f, 1f, 6f));
            var boxB = new Bounds();
            boxB.SetMinMax(minB, maxB);
            Vector3[] capB = Board(1f, 1.5f, -1f, 1f, 6f);

            Assert.That(
                Judge(a, Target(2, boxB, Matrix4x4.identity, capB), LeftNear, RightNear, k_noMargin).MustSeparate,
                Is.False, "the finite layout is apart");

            Vector3[] nanCap = Board(1f, 1.5f, -1f, 1f, 6f);
            nanCap[2].x = float.NaN;
            Matrix4x4 nanPlacement = Matrix4x4.identity;
            nanPlacement[0, 3] = float.NaN;

            var broken = new (VpCapProjectionTarget b, string what)[]
            {
                (Target(2, boxB, Matrix4x4.identity, nanCap), "a cap vertex NaN"),
                (Target(2, boxB, nanPlacement, capB), "the placement NaN"),
            };
            foreach ((VpCapProjectionTarget b, string what) in broken)
            {
                VpCapProjectionVerdict verdict = Judge(a, b, LeftNear, RightNear, k_noMargin);
                Assert.That(verdict.left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap), what);
                Assert.That(verdict.right, Is.EqualTo(VpCapProjectionOverlap.MayOverlap), what);
                Assert.That(verdict.MustSeparate, Is.True, what);
            }

            VpCapProjectionTarget good = Target(2, boxB, Matrix4x4.identity, capB);
            Matrix4x4 nanMatrix = LeftNear.worldToClip;
            nanMatrix[3, 2] = float.NaN;
            VpCapProjectionVerdict leftBroken = Judge(a, good, new VpCapEye(LeftNear.position, nanMatrix), RightNear, k_noMargin);
            Assert.That(leftBroken.left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap), "the left eye's matrix NaN");
            Assert.That(leftBroken.right, Is.EqualTo(VpCapProjectionOverlap.ApartByCaps), "the right eye still decides for itself");
            Assert.That(leftBroken.MustSeparate, Is.True);

            Matrix4x4 overflowing = LeftNear.worldToClip;
            for (int i = 0; i < 16; i++)
            {
                overflowing[i] *= 1e38f;
            }

            VpCapProjectionVerdict overflow = Judge(a, good, new VpCapEye(LeftNear.position, overflowing), RightNear, k_noMargin);
            Assert.That(overflow.left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap), "finite, but the clip coordinates overflow");
            Assert.That(overflow.MustSeparate, Is.True);

            // Every element finite, and only z overflows: 3e38 times a depth of 5 to 7. The far plane is not decided
            // from an infinite z.
            Matrix4x4 zOverflowing = LeftNear.worldToClip;
            zOverflowing[2, 2] = 3e38f;
            VpCapProjectionVerdict zOverflow = Judge(a, good, new VpCapEye(LeftNear.position, zOverflowing), RightNear, k_noMargin);
            Assert.That(zOverflow.left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap), "only the clip z overflows");
            Assert.That(zOverflow.right, Is.EqualTo(VpCapProjectionOverlap.ApartByCaps));
            Assert.That(zOverflow.MustSeparate, Is.True);

            // Only the boxes overflow, in the left eye: its z scale is 5e37, so a corner at depth 7 gives 3.5e38, past
            // float's range, while the caps at depth 6 give 3e38, finite (and past the far plane). The caps alone would
            // be apart; the box that overflowed still leaves the left eye possibly overlapping. The right eye is normal.
            Matrix4x4 boxesOverflow = LeftNear.worldToClip;
            boxesOverflow[2, 2] = 5e37f;
            var boxesOnly = new VpCapEye(LeftNear.position, boxesOverflow);
            VpCapProjectionVerdict boxOverflow = Judge(a, good, boxesOnly, RightNear, k_noMargin);
            Assert.That(boxOverflow.left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap), "a box overflowed; the caps are finite and apart");
            Assert.That(boxOverflow.right, Is.EqualTo(VpCapProjectionOverlap.ApartByCaps), "the right eye decides as before");
            Assert.That(boxOverflow.MustSeparate, Is.True);

            // The same eye, with the other target wholly behind it: its box projects to nothing, which does not settle
            // the eye before the overflowing box does.
            VpCapProjectionTarget behindEye = Target(1, new Vector3(-0.5f, -0.5f, -3f), new Vector3(0.5f, 0.5f, -2f),
                Board(-0.5f, 0.5f, -0.5f, 0.5f, -2.5f));
            VpCapProjectionVerdict besideNothing = Judge(behindEye, good, boxesOnly, RightNear, k_noMargin);
            Assert.That(besideNothing.left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap), "an overflow next to a box that shows nothing");
            Assert.That(besideNothing.right, Is.EqualTo(VpCapProjectionOverlap.ApartByBounds), "the right eye: behind it, nothing");

            foreach (Vector2 bad in new[] { new Vector2(-0.01f, 0f), new Vector2(0f, float.NaN), new Vector2(float.PositiveInfinity, 0f) })
            {
                Assert.Throws<ArgumentOutOfRangeException>(
                    () => VpCapProjectionConflict.Judge(a, good, LeftNear, RightNear, bad, PlaneEpsilon),
                    "margin " + bad);
            }
        }

        // ----- real caps through the adapter ----------------------------------------------------------------------

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
                Is.True);
            return geometry;
        }

        /// <summary>A cube of its own ledger, cut across y = 1 with the anchor below, settled at <paramref name="placement"/>.</summary>
        private VpLogicalCutDisplay CutCube(VpCpuGeometryStorage storage, Matrix4x4 placement)
        {
            var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(4));
            LogicalFragmentId body = ledger.AddFragment(new List<float3> { new float3(0f, 0.2f, 0f) });
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var material = new Material(shader) { name = "projection body" };
            _objects.Add(material);
            Assert.That(
                VpLogicalCutDisplay.TryCreate(
                    storage, new VpGeometryReferenceTable(storage, 8, 8), ledger,
                    new Dictionary<int, Material> { { BodyMaterial, material } }, null, null, 16, 16,
                    VpDisplayTestCapacities.Branches, VpDisplayTestCapacities.Candidates, VpDisplayTestCapacities.ChainDepth, VpStencilTestSettings.Create(), () => 1,
                    out VpLogicalCutDisplay display),
                Is.True);
            _ledgers[display] = ledger;
            Assert.That(display.TryShow(body, AppendCube(storage), placement), Is.True);
            Assert.That(ledger.Admit(body, new float4(0f, 1f, 0f, -1f), true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(display.TryBeginFrame(), Is.True);
            return display;
        }

        private static int PositiveCap(VpLogicalCutDisplay display)
        {
            for (int i = 0; i < display.CapRecordCount; i++)
            {
                Assert.That(display.TryGetCapRecord(i, out LogicalCutCapRecord record), Is.True);
                if (record.side > 0f)
                {
                    return i;
                }
            }

            Assert.Fail("no positive cap");
            return -1;
        }

        private static VpCapEye CameraEye(Vector3 position)
        {
            Matrix4x4 worldToCamera = Matrix4x4.Scale(new Vector3(1f, 1f, -1f))
                * Matrix4x4.TRS(position, Quaternion.identity, Vector3.one).inverse;
            return new VpCapEye(position, Matrix4x4.Perspective(90f, 1f, 0.1f, 100f) * worldToCamera);
        }

        // Each display's ledger, so a target can name its faces under the ledger that issued them.
        private readonly Dictionary<VpLogicalCutDisplay, LogicalCutLedger> _ledgers = new Dictionary<VpLogicalCutDisplay, LogicalCutLedger>();

        private VpCapProjectionTarget Adapted(VpLogicalCutDisplay display, VpCapEye left, VpCapEye right)
        {
            return VpDisplayRecordTargets.Projection(display, _ledgers[display], PositiveCap(display), left, right, 0.01f);
        }

        /// <summary>
        /// Two cubes of two ledgers — incompatible — seen by eyes in front of them. In the same place their positive
        /// caps meet and they must be separated; with the second cube 3.5 to the side, their rectangles are apart. The
        /// target is made from what the real display published: its render fragment's box and placement, its cap records
        /// and its cap vertices where they are drawn. The positive cap closes the cut at y = 1 and faces down, so the
        /// eyes are just under it. Seen from above, where the visibility test drops both downward positive caps,
        /// neither target's caps are complete, and with their boxes meeting the two may overlap: an omitted cap does not
        /// show the volumes apart.
        /// </summary>
        [Test]
        public void RealCaps_FromTheDisplaysRecords()
        {
            using (VpCpuGeometryStorage first = new VpCpuGeometryStorage(2048, 8192, 32, 128, 128, Allocator.Persistent))
            using (VpCpuGeometryStorage second = new VpCpuGeometryStorage(2048, 8192, 32, 128, 128, Allocator.Persistent))
            using (VpCpuGeometryStorage third = new VpCpuGeometryStorage(2048, 8192, 32, 128, 128, Allocator.Persistent))
            using (VpLogicalCutDisplay here = CutCube(first, Matrix4x4.identity))
            using (VpLogicalCutDisplay alsoHere = CutCube(second, Matrix4x4.identity))
            using (VpLogicalCutDisplay aside = CutCube(third, Matrix4x4.Translate(new Vector3(3.5f, 0f, 0f))))
            {
                VpCapEye left = CameraEye(new Vector3(-0.03f, 0.9f, -5f));
                VpCapEye right = CameraEye(new Vector3(0.03f, 0.9f, -5f));
                VpCapProjectionTarget a = Adapted(here, left, right);
                Assert.That(a.visibleCaps.Count, Is.EqualTo(1), "seen from just under it, the positive cap is kept");
                Assert.That(a.localBounds.min, Is.EqualTo(new Vector3(-1f, 0f, -1f)), "the cube's own box");

                VpCapProjectionVerdict together = VpCapProjectionConflict.Judge(
                    a, Adapted(alsoHere, left, right), left, right, k_noMargin, PlaneEpsilon);
                Assert.That(together.compatible, Is.False, "two ledgers");
                Assert.That(together.MustSeparate, Is.True, "the same place");

                VpCapProjectionVerdict apart = VpCapProjectionConflict.Judge(
                    a, Adapted(aside, left, right), left, right, k_noMargin, PlaneEpsilon);
                Assert.That(apart.left, Is.EqualTo(VpCapProjectionOverlap.ApartByBounds), "3.5 to the side");
                Assert.That(apart.right, Is.EqualTo(VpCapProjectionOverlap.ApartByBounds));

                VpCapEye aboveLeft = CameraEye(new Vector3(-0.03f, 3f, -5f));
                VpCapEye aboveRight = CameraEye(new Vector3(0.03f, 3f, -5f));
                VpCapProjectionTarget seenFromAbove = Adapted(here, aboveLeft, aboveRight);
                Assert.That(seenFromAbove.visibleCaps.Count, Is.Zero, "the downward cap is dropped from above");
                Assert.That(seenFromAbove.capsComplete, Is.False, "and so this target's caps are not complete");
                Assert.That(a.capsComplete, Is.True, "seen from under it, nothing was left out");
                VpCapProjectionVerdict noCaps = VpCapProjectionConflict.Judge(
                    seenFromAbove, Adapted(alsoHere, aboveLeft, aboveRight), aboveLeft, aboveRight, k_noMargin, PlaneEpsilon);
                Assert.That(noCaps.left, Is.EqualTo(VpCapProjectionOverlap.MayOverlap), "boxes meeting, caps left out");
                Assert.That(noCaps.right, Is.EqualTo(VpCapProjectionOverlap.MayOverlap));
                Assert.That(noCaps.MustSeparate, Is.True);
            }
        }
    }
}
