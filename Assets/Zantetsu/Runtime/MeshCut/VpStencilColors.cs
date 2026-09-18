using System;
using System.Collections.Generic;
using UnityEngine;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The stencil colour assignment of DESIGN 5.6 / T-066, on the CPU only: each compatibility group handed in as a
    /// drawing candidate gets one colour out of a fixed limit.
    /// <para>
    /// **Colours.** With a limit of <c>maxStencilColors</c> (at least one), colours <c>0</c> to
    /// <c>maxStencilColors - 2</c> are ordinary and colour <c>maxStencilColors - 1</c> is the last, merged one. A limit
    /// of one has no ordinary colour, so every group goes to the last. No colour past the limit is ever made.
    /// </para>
    /// <para>
    /// **Rule.** Groups are taken in order, and each goes to the first ordinary colour already opened that it may
    /// share, else to a new ordinary colour while one is left, else to the last colour. A group may share an ordinary
    /// colour only when **every** target in it, against **every** target of every group already in that colour, is
    /// not one that <see cref="VpCapProjectionConflict"/> says must be separated — no representative stands for a
    /// group. The last colour asks nothing: incompatible targets are not kept apart there (DESIGN 5.2's quality
    /// exception). Running out of ordinary colours is not a failure; it is the last colour.
    /// </para>
    /// <para>
    /// **Not claimed.** The fewest colours, a particular colour number, and the same result for another input order.
    /// No conflict graph is built or kept, nothing is recoloured, and nothing is kept between calls.
    /// </para>
    /// <para>
    /// **Nothing is dropped.** Every group handed in gets a colour, whatever its caps: a target with no visible cap is
    /// still assigned (leaving out groups with no visible cap is the caller's, at the drawing connection, and not done
    /// here). Geometry sign and display colour are not inputs. The limit, the margin and the epsilons are the caller's;
    /// no product value exists for any of them. Nothing here uploads, orders or issues a draw.
    /// </para>
    /// </summary>
    public static class VpStencilColors
    {
        /// <summary>
        /// Assigns a colour to each of <paramref name="groupCount"/> groups, writing it into
        /// <paramref name="colorOfGroup"/>, and answers how many colours are in use: the ordinary colours opened, or
        /// <paramref name="maxStencilColors"/> when the last colour holds any group.
        /// </summary>
        /// <param name="targets">Every target of every group.</param>
        /// <param name="groupOfTarget">
        /// Each target's group, as <see cref="VpCapCompatibility.Classify"/> writes it: from 0 to
        /// <paramref name="groupCount"/> - 1, every group with at least one target.
        /// </param>
        /// <exception cref="ArgumentNullException">A list is null.</exception>
        /// <exception cref="ArgumentException">
        /// A group index is out of range, a group has no target, or an array is shorter than what it has to hold.
        /// </exception>
        /// <exception cref="ArgumentOutOfRangeException">
        /// The limit is below one, the group count is negative, or the margin or an epsilon is negative or not finite.
        /// </exception>
        public static int Assign(
            IReadOnlyList<VpCapProjectionTarget> targets,
            IReadOnlyList<int> groupOfTarget,
            int groupCount,
            in VpCapEye left,
            in VpCapEye right,
            Vector2 ndcMargin,
            float planeEpsilon,
            float offsetEpsilon,
            int maxStencilColors,
            int[] colorOfGroup)
        {
            if (targets == null)
            {
                throw new ArgumentNullException(nameof(targets));
            }

            if (groupOfTarget == null)
            {
                throw new ArgumentNullException(nameof(groupOfTarget));
            }

            if (colorOfGroup == null)
            {
                throw new ArgumentNullException(nameof(colorOfGroup));
            }

            if (maxStencilColors < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxStencilColors), maxStencilColors, "At least one colour.");
            }

            if (groupCount < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(groupCount), groupCount, "A count of groups.");
            }

            CheckFinite(ndcMargin.x, nameof(ndcMargin));
            CheckFinite(ndcMargin.y, nameof(ndcMargin));
            CheckFinite(planeEpsilon, nameof(planeEpsilon));
            CheckFinite(offsetEpsilon, nameof(offsetEpsilon));

            if (groupOfTarget.Count < targets.Count)
            {
                throw new ArgumentException("Every target has a group.", nameof(groupOfTarget));
            }

            if (colorOfGroup.Length < groupCount)
            {
                throw new ArgumentException("There is a colour slot for every group.", nameof(colorOfGroup));
            }

            // Every group named is in range, and every group in range has a target: nothing is dropped or invented.
            for (int g = 0; g < groupCount; g++)
            {
                colorOfGroup[g] = -1;
            }

            for (int i = 0; i < targets.Count; i++)
            {
                int group = groupOfTarget[i];
                if (group < 0 || group >= groupCount)
                {
                    throw new ArgumentException("Target " + i + " names a group that is not there.", nameof(groupOfTarget));
                }

                colorOfGroup[group] = 0;
            }

            for (int g = 0; g < groupCount; g++)
            {
                if (colorOfGroup[g] != 0)
                {
                    throw new ArgumentException("Group " + g + " has no target.", nameof(groupOfTarget));
                }

                colorOfGroup[g] = -1;
            }

            int ordinary = maxStencilColors - 1;
            int last = maxStencilColors - 1;
            int opened = 0;
            bool lastUsed = false;
            for (int g = 0; g < groupCount; g++)
            {
                int chosen = -1;
                for (int c = 0; c < opened && chosen < 0; c++)
                {
                    if (!ConflictsWithColor(
                            g, c, targets, groupOfTarget, colorOfGroup, left, right, ndcMargin, planeEpsilon, offsetEpsilon))
                    {
                        chosen = c;
                    }
                }

                if (chosen < 0 && opened < ordinary)
                {
                    chosen = opened;
                    opened++;
                }

                if (chosen < 0)
                {
                    chosen = last;
                    lastUsed = true;
                }

                colorOfGroup[g] = chosen;
            }

            return lastUsed ? maxStencilColors : opened;
        }

        /// <summary>
        /// Whether any target of <paramref name="group"/> must be separated from any target of a group already in
        /// ordinary colour <paramref name="color"/>. Only groups already assigned are in a colour.
        /// </summary>
        private static bool ConflictsWithColor(
            int group,
            int color,
            IReadOnlyList<VpCapProjectionTarget> targets,
            IReadOnlyList<int> groupOfTarget,
            int[] colorOfGroup,
            in VpCapEye left,
            in VpCapEye right,
            Vector2 ndcMargin,
            float planeEpsilon,
            float offsetEpsilon)
        {
            for (int i = 0; i < targets.Count; i++)
            {
                if (groupOfTarget[i] != group)
                {
                    continue;
                }

                for (int j = 0; j < targets.Count; j++)
                {
                    int other = groupOfTarget[j];
                    if (other == group || colorOfGroup[other] != color)
                    {
                        continue;
                    }

                    VpCapProjectionVerdict verdict = VpCapProjectionConflict.Judge(
                        targets[i], targets[j], left, right, ndcMargin, planeEpsilon, offsetEpsilon);
                    if (verdict.MustSeparate)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static void CheckFinite(float value, string name)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f)
            {
                throw new ArgumentOutOfRangeException(name, value, "A finite value, zero or more.");
            }
        }
    }
}
