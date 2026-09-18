using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The bounded stencil colour assignment (DESIGN 5.6, T-066) on written-out targets.
    /// <para>
    /// No colour number is the answer. Each test checks what an assignment must be — every group given one colour
    /// within the limit and every target therefore coloured once, the colours in use within the limit, no two targets
    /// that the layout says conflict sharing an ordinary colour, the last colour taking only what did not fit — and,
    /// where the layout leaves nothing to compete, that groups which may share do.
    /// </para>
    /// <para>
    /// The eyes look along +z with clip <c>(x, y, 1.2z - 2.2, z)</c> relative to the eye, as in the projection conflict
    /// tests, so a point at depth z is at <c>(x / z, y / z)</c> on the screen. The groups come from
    /// <see cref="VpCapCompatibility.Classify"/>; conflicts come from <see cref="VpCapProjectionConflict"/> inside the
    /// assignment, and the tests' own expectations come from the layout.
    /// </para>
    /// </summary>
    public class VpStencilColorsTests
    {
        /// <summary>Test values only: no product margin, epsilon or colour limit exists (DESIGN O-034).</summary>
        private const float PlaneEpsilon = 0.01f;
        private const float OffsetEpsilon = 0.01f;
        private static readonly Vector2 k_noMargin = Vector2.zero;

        private static readonly LogicalCutLedger k_ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(4));
        private static readonly Vector4 k_plane = new Vector4(0f, 0f, 1f, -6f);

        private static VpCapEye Eye(Vector3 position)
        {
            var projection = new Matrix4x4(
                new Vector4(1f, 0f, 0f, 0f),
                new Vector4(0f, 1f, 0f, 0f),
                new Vector4(0f, 0f, 1.2f, 1f),
                new Vector4(0f, 0f, -2.2f, 0f));
            return new VpCapEye(position, projection * Matrix4x4.Translate(-position));
        }

        private static VpCapEye LeftNear => Eye(new Vector3(-0.03f, 0f, 0f));
        private static VpCapEye RightNear => Eye(new Vector3(0.03f, 0f, 0f));

        /// <summary>
        /// A target under face <paramref name="operation"/>: a box from <paramref name="min"/> to <paramref name="max"/>
        /// and one cap across it at depth 6. Different operations are incompatible; the same operation is compatible.
        /// </summary>
        private static VpCapProjectionTarget Target(
            int operation, Vector3 min, Vector3 max, bool capsComplete = true, Vector3[] cap = null)
        {
            var box = new Bounds();
            box.SetMinMax(min, max);
            Vector3[] board = cap ?? new[]
            {
                new Vector3(min.x, min.y, 6f), new Vector3(max.x, min.y, 6f), new Vector3(max.x, max.y, 6f), new Vector3(min.x, max.y, 6f),
            };
            var conditions = new VpCapCompatibilityTarget(
                new[] { new VpCapConstraint(new VpCapFace(k_ledger, new CutOperationId(operation)), 1f, k_plane) },
                Vector3.zero);
            return new VpCapProjectionTarget(
                conditions, box, Matrix4x4.identity, capsComplete ? new[] { board } : Array.Empty<Vector3[]>(), capsComplete);
        }

        /// <summary>A one-unit box centred at (x, 0, 6), with its cap.</summary>
        private static VpCapProjectionTarget At(int operation, float x)
        {
            return Target(operation, new Vector3(x - 0.5f, -0.5f, 5.5f), new Vector3(x + 0.5f, 0.5f, 6.5f));
        }

        private sealed class Assignment
        {
            public int[] groupOfTarget;
            public int groupCount;
            public int[] colorOfGroup;
            public int used;

            public int ColorOf(int target) => colorOfGroup[groupOfTarget[target]];
        }

        /// <summary>
        /// Groups the targets, assigns colours, and checks every rule that does not depend on the layout: each group
        /// one colour within the limit, the count in use within the limit, and nothing but full ordinary colours
        /// behind a group in the last colour.
        /// </summary>
        private static Assignment Assign(VpCapProjectionTarget[] targets, int maxColors, VpCapEye left, VpCapEye right, Vector2 margin)
        {
            var conditions = new VpCapCompatibilityTarget[targets.Length];
            for (int i = 0; i < targets.Length; i++)
            {
                conditions[i] = targets[i].conditions;
            }

            var result = new Assignment { groupOfTarget = new int[targets.Length], colorOfGroup = new int[targets.Length] };
            result.groupCount = VpCapCompatibility.Classify(conditions, PlaneEpsilon, OffsetEpsilon, result.groupOfTarget);
            for (int g = 0; g < result.colorOfGroup.Length; g++)
            {
                result.colorOfGroup[g] = 99;
            }

            result.used = VpStencilColors.Assign(
                targets, result.groupOfTarget, result.groupCount, left, right, margin, PlaneEpsilon, OffsetEpsilon,
                maxColors, result.colorOfGroup);

            Assert.That(result.used, Is.InRange(0, maxColors), "colours in use within the limit");
            bool lastUsed = false;
            int highestOrdinary = -1;
            for (int g = 0; g < result.groupCount; g++)
            {
                Assert.That(result.colorOfGroup[g], Is.InRange(0, maxColors - 1), "group " + g + " has a colour within the limit");
                if (result.colorOfGroup[g] == maxColors - 1)
                {
                    lastUsed = true;
                }
                else
                {
                    highestOrdinary = Math.Max(highestOrdinary, result.colorOfGroup[g]);
                }
            }

            if (lastUsed && maxColors > 1)
            {
                Assert.That(highestOrdinary, Is.EqualTo(maxColors - 2), "the last colour is used only once every ordinary one is");
            }

            for (int i = 0; i < targets.Length; i++)
            {
                for (int j = 0; j < i; j++)
                {
                    if (result.groupOfTarget[i] == result.groupOfTarget[j])
                    {
                        Assert.That(result.ColorOf(i), Is.EqualTo(result.ColorOf(j)), "one group, one colour");
                    }
                }
            }

            return result;
        }

        private static bool IsOrdinary(int color, int maxColors) => color < maxColors - 1;

        /// <summary>Two targets the layout says conflict do not share an ordinary colour.</summary>
        private static void AssertApartInOrdinary(Assignment result, int a, int b, int maxColors, string what)
        {
            if (result.ColorOf(a) == result.ColorOf(b))
            {
                Assert.That(IsOrdinary(result.ColorOf(a), maxColors), Is.False, what + ": together only in the last colour");
            }
        }

        /// <summary>Three boxes far apart, under three faces, share one ordinary colour.</summary>
        [Test]
        public void GroupsThatDoNotConflict_ShareAnOrdinaryColour()
        {
            VpCapProjectionTarget[] targets = { At(1, -3f), At(2, 0f), At(3, 3f) };
            Assignment result = Assign(targets, 3, LeftNear, RightNear, k_noMargin);

            Assert.That(result.groupCount, Is.EqualTo(3), "three faces, three groups");
            Assert.That(IsOrdinary(result.ColorOf(0), 3), Is.True);
            Assert.That(result.ColorOf(1), Is.EqualTo(result.ColorOf(0)), "nothing competes, so they share");
            Assert.That(result.ColorOf(2), Is.EqualTo(result.ColorOf(0)));
            Assert.That(result.used, Is.EqualTo(1));
        }

        /// <summary>Two incompatible boxes in one place are kept apart in ordinary colours while there are two.</summary>
        [Test]
        public void OverlappingIncompatibleGroups_AreKeptApart()
        {
            VpCapProjectionTarget[] targets = { At(1, 0f), At(2, 0f) };
            Assignment result = Assign(targets, 3, LeftNear, RightNear, k_noMargin);

            Assert.That(result.ColorOf(1), Is.Not.EqualTo(result.ColorOf(0)));
            Assert.That(IsOrdinary(result.ColorOf(0), 3) && IsOrdinary(result.ColorOf(1), 3), Is.True, "both fit in ordinary colours");
        }

        /// <summary>
        /// Eyes at x = -1 and +1. A small box at depth 2 and another at depth 6 on one line from the left eye conflict
        /// in that eye only, and that is enough to keep them apart.
        /// </summary>
        [Test]
        public void AConflictInOneEyeOnly_KeepsThemApart()
        {
            VpCapProjectionTarget near = Target(1, new Vector3(-0.1f, -0.1f, 1.9f), new Vector3(0.1f, 0.1f, 2.1f),
                cap: new[] { new Vector3(-0.1f, -0.1f, 2f), new Vector3(0.1f, -0.1f, 2f), new Vector3(0.1f, 0.1f, 2f), new Vector3(-0.1f, 0.1f, 2f) });
            VpCapProjectionTarget far = Target(2, new Vector3(1.9f, -0.1f, 5.9f), new Vector3(2.1f, 0.1f, 6.1f));
            VpCapEye left = Eye(new Vector3(-1f, 0f, 0f));
            VpCapEye right = Eye(new Vector3(1f, 0f, 0f));

            VpCapProjectionVerdict layout = VpCapProjectionConflict.Judge(near, far, left, right, k_noMargin, PlaneEpsilon, OffsetEpsilon);
            Assert.That(layout.left == VpCapProjectionOverlap.MayOverlap && layout.right != VpCapProjectionOverlap.MayOverlap, Is.True,
                "the layout: one eye only");

            Assignment result = Assign(new[] { near, far }, 3, left, right, k_noMargin);
            Assert.That(result.ColorOf(1), Is.Not.EqualTo(result.ColorOf(0)), "one eye is enough");
        }

        /// <summary>Overlapping boxes whose caps are apart may share an ordinary colour.</summary>
        [Test]
        public void MeetingBoxesWithCapsApart_ShareAnOrdinaryColour()
        {
            VpCapProjectionTarget a = Target(1, new Vector3(-1.5f, -1f, 5f), new Vector3(0.5f, 1f, 7f),
                cap: new[] { new Vector3(-1.5f, -1f, 6f), new Vector3(-1f, -1f, 6f), new Vector3(-1f, 1f, 6f), new Vector3(-1.5f, 1f, 6f) });
            VpCapProjectionTarget b = Target(2, new Vector3(-0.5f, -1f, 5f), new Vector3(1.5f, 1f, 7f),
                cap: new[] { new Vector3(1f, -1f, 6f), new Vector3(1.5f, -1f, 6f), new Vector3(1.5f, 1f, 6f), new Vector3(1f, 1f, 6f) });

            Assignment result = Assign(new[] { a, b }, 3, LeftNear, RightNear, k_noMargin);
            Assert.That(result.ColorOf(1), Is.EqualTo(result.ColorOf(0)), "boxes meet, caps apart");
            Assert.That(IsOrdinary(result.ColorOf(0), 3), Is.True);
        }

        /// <summary>
        /// One face drawn at two places is one group and one colour. The group's first target is far from a third,
        /// incompatible target; its second is right on it. The two groups do not share an ordinary colour: every target
        /// counts, not the first.
        /// </summary>
        [Test]
        public void EveryTargetOfAGroupCounts_NotARepresentative()
        {
            VpCapProjectionTarget[] targets = { At(1, -3f), At(1, 0f), At(2, 0f) };
            Assignment result = Assign(targets, 3, LeftNear, RightNear, k_noMargin);

            Assert.That(result.groupCount, Is.EqualTo(2), "the layout: one face twice, and another");
            Assert.That(result.groupOfTarget[1], Is.EqualTo(result.groupOfTarget[0]));
            Assert.That(result.ColorOf(1), Is.EqualTo(result.ColorOf(0)), "one group, one colour");

            VpCapProjectionVerdict firsts = VpCapProjectionConflict.Judge(
                targets[0], targets[2], LeftNear, RightNear, k_noMargin, PlaneEpsilon, OffsetEpsilon);
            Assert.That(firsts.MustSeparate, Is.False, "the layout: the first targets of the two groups are apart");

            AssertApartInOrdinary(result, 1, 2, 3, "the second target of the group is on the other");
            Assert.That(result.ColorOf(2), Is.Not.EqualTo(result.ColorOf(0)), "so the groups are in different colours");
        }

        /// <summary>
        /// Three incompatible boxes in one place. With a limit of 3 (two ordinary colours) two are in ordinary colours
        /// and the third in the last; with a limit of 2 (one ordinary) one is ordinary and two are in the last. No colour
        /// is past the limit and every group has one.
        /// </summary>
        [Test]
        public void GroupsThatDoNotFit_GoToTheLastColour_WithinTheLimit()
        {
            VpCapProjectionTarget[] targets = { At(1, 0f), At(2, 0f), At(3, 0f) };

            foreach (int limit in new[] { 3, 2 })
            {
                Assignment result = Assign(targets, limit, LeftNear, RightNear, k_noMargin);
                Assert.That(result.used, Is.EqualTo(limit), "limit " + limit + ": all of it in use");
                int inLast = 0;
                for (int i = 0; i < targets.Length; i++)
                {
                    for (int j = 0; j < i; j++)
                    {
                        AssertApartInOrdinary(result, i, j, limit, "limit " + limit + ", targets " + j + " and " + i);
                    }

                    if (!IsOrdinary(result.ColorOf(i), limit))
                    {
                        inLast++;
                    }
                }

                Assert.That(inLast, Is.EqualTo(targets.Length - (limit - 1)), "limit " + limit + ": what did not fit is in the last colour");
            }
        }

        /// <summary>
        /// A limit of one puts every group in the last colour. No groups is no colour. A target whose cap was left out
        /// is kept, and with the boxes meeting it conflicts even though the cap it was given would be apart; with its
        /// caps complete, the same pair shares.
        /// </summary>
        [Test]
        public void LimitOne_NoGroups_AndAnOmittedCap()
        {
            VpCapProjectionTarget[] crowd = { At(1, 0f), At(2, 0f), At(3, 3f) };
            Assignment one = Assign(crowd, 1, LeftNear, RightNear, k_noMargin);
            for (int i = 0; i < crowd.Length; i++)
            {
                Assert.That(one.ColorOf(i), Is.Zero, "limit 1: the last colour is the only colour");
            }

            Assert.That(one.used, Is.EqualTo(1));

            Assert.That(
                VpStencilColors.Assign(
                    Array.Empty<VpCapProjectionTarget>(), Array.Empty<int>(), 0, LeftNear, RightNear, k_noMargin,
                    PlaneEpsilon, OffsetEpsilon, 3, Array.Empty<int>()),
                Is.Zero,
                "no groups, no colours");

            var boxA = (new Vector3(-1.5f, -1f, 5f), new Vector3(0.5f, 1f, 7f));
            var boxB = (new Vector3(-0.5f, -1f, 5f), new Vector3(1.5f, 1f, 7f));
            Vector3[] capA = { new Vector3(-1.5f, -1f, 6f), new Vector3(-1f, -1f, 6f), new Vector3(-1f, 1f, 6f), new Vector3(-1.5f, 1f, 6f) };

            VpCapProjectionTarget complete = Target(2, boxB.Item1, boxB.Item2,
                cap: new[] { new Vector3(1f, -1f, 6f), new Vector3(1.5f, -1f, 6f), new Vector3(1.5f, 1f, 6f), new Vector3(1f, 1f, 6f) });
            VpCapProjectionTarget omitted = Target(2, boxB.Item1, boxB.Item2, capsComplete: false);
            VpCapProjectionTarget a = Target(1, boxA.Item1, boxA.Item2, cap: capA);

            Assignment shares = Assign(new[] { a, complete }, 3, LeftNear, RightNear, k_noMargin);
            Assert.That(shares.ColorOf(1), Is.EqualTo(shares.ColorOf(0)), "caps complete and apart: they share");

            Assignment kept = Assign(new[] { a, omitted }, 3, LeftNear, RightNear, k_noMargin);
            Assert.That(kept.groupCount, Is.EqualTo(2), "the target with no visible cap is still assigned");
            AssertApartInOrdinary(kept, 0, 1, 3, "a cap left out, boxes meeting");
            Assert.That(kept.ColorOf(1), Is.Not.EqualTo(kept.ColorOf(0)));
        }

        /// <summary>
        /// Each call is its own. The same output array, reused: apart targets share; then one is moved onto the other
        /// and they are kept apart; then the limit drops to one and both are in the last colour; then back as first.
        /// </summary>
        [Test]
        public void TheNextCall_DoesNotCarryTheLastAssignment()
        {
            var groups = new[] { 0, 1 };
            var colors = new int[2];

            VpCapProjectionTarget[] apart = { At(1, -3f), At(2, 3f) };
            VpCapProjectionTarget[] together = { At(1, 0f), At(2, 0f) };

            VpStencilColors.Assign(apart, groups, 2, LeftNear, RightNear, k_noMargin, PlaneEpsilon, OffsetEpsilon, 3, colors);
            Assert.That(colors[1], Is.EqualTo(colors[0]), "first: apart, shared");

            VpStencilColors.Assign(together, groups, 2, LeftNear, RightNear, k_noMargin, PlaneEpsilon, OffsetEpsilon, 3, colors);
            Assert.That(colors[1], Is.Not.EqualTo(colors[0]), "next: moved together, kept apart");

            VpStencilColors.Assign(together, groups, 2, LeftNear, RightNear, k_noMargin, PlaneEpsilon, OffsetEpsilon, 1, colors);
            Assert.That(colors[0] == 0 && colors[1] == 0, Is.True, "limit 1: both in the only colour");

            VpStencilColors.Assign(apart, groups, 2, LeftNear, RightNear, k_noMargin, PlaneEpsilon, OffsetEpsilon, 3, colors);
            Assert.That(colors[1], Is.EqualTo(colors[0]), "back: shared again");
            Assert.That(colors[0], Is.LessThan(2), "in an ordinary colour");
        }

        /// <summary>Malformed input is refused; a shortage of ordinary colours is not malformed.</summary>
        [Test]
        public void MalformedInput_IsRefused()
        {
            VpCapProjectionTarget[] targets = { At(1, 0f), At(2, 0f) };
            var colors = new int[2];

            Assert.Throws<ArgumentOutOfRangeException>(
                () => VpStencilColors.Assign(targets, new[] { 0, 1 }, 2, LeftNear, RightNear, k_noMargin, PlaneEpsilon, OffsetEpsilon, 0, colors),
                "a limit of zero");
            Assert.Throws<ArgumentException>(
                () => VpStencilColors.Assign(targets, new[] { 0, 2 }, 2, LeftNear, RightNear, k_noMargin, PlaneEpsilon, OffsetEpsilon, 3, colors),
                "a group out of range");
            Assert.Throws<ArgumentException>(
                () => VpStencilColors.Assign(targets, new[] { 0, 0 }, 2, LeftNear, RightNear, k_noMargin, PlaneEpsilon, OffsetEpsilon, 3, colors),
                "a group with no target");
            Assert.Throws<ArgumentException>(
                () => VpStencilColors.Assign(targets, new[] { 0, 1 }, 2, LeftNear, RightNear, k_noMargin, PlaneEpsilon, OffsetEpsilon, 3, new int[1]),
                "too few colour slots");
            Assert.Throws<ArgumentException>(
                () => VpStencilColors.Assign(targets, new[] { 0 }, 1, LeftNear, RightNear, k_noMargin, PlaneEpsilon, OffsetEpsilon, 3, colors),
                "a target with no group");
            Assert.Throws<ArgumentOutOfRangeException>(
                () => VpStencilColors.Assign(targets, new[] { 0, 1 }, 2, LeftNear, RightNear, new Vector2(float.NaN, 0f), PlaneEpsilon, OffsetEpsilon, 3, colors),
                "a margin that is not finite");
            Assert.Throws<ArgumentNullException>(
                () => VpStencilColors.Assign(null, new[] { 0, 1 }, 2, LeftNear, RightNear, k_noMargin, PlaneEpsilon, OffsetEpsilon, 3, colors));

            Assert.That(
                VpStencilColors.Assign(targets, new[] { 0, 1 }, 2, LeftNear, RightNear, k_noMargin, PlaneEpsilon, OffsetEpsilon, 2, colors),
                Is.EqualTo(2),
                "no ordinary colour left is the last colour, not a refusal");
        }
    }
}
