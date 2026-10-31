using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Zantetsu.Sandbox
{
    /// <summary>
    /// The benchmark's deterministic plan (DESIGN 3.4, npc-skin-bench): which model family each individual is, where it
    /// stands and faces, and the phase its clip plays at -- the same for every launch of the same count, so that the two
    /// routes draw the same crowd at the same poses. Pure: nothing here reads the scene or the clock.
    /// <para>
    /// **The crowd stands in rows in front of the eye.** The eye is at the origin, looking down +Z; the individuals face
    /// it (yaw 180). Row k stands at <c>firstRowDistance + k * rowGap</c> metres, and is as wide as the horizontal field
    /// of view allows at that distance (spacing <c>spacing</c> metres), so that every individual is inside the view and
    /// a count that grows fills further rows rather than spilling out of the sides.
    /// </para>
    /// <para>
    /// **The pose time is the step's.** An individual's clip time at step <c>s</c> is <c>s * stepSeconds + phase</c>,
    /// the phase spread over the clip by the golden ratio so that no two individuals of the same model are at the same
    /// pose and the spread does not depend on the count's divisors.
    /// </para>
    /// </summary>
    public static class NpcSkinBenchPlan
    {
        public const float DefaultHorizontalFovDegrees = 70f;
        /// <summary>The plan's eye: at the origin, this high, looking down +Z (the view rule and the fixed view use it).</summary>
        public const float EyeHeight = 1.7f;
        public const float DefaultSpacing = 0.9f;
        public const float DefaultFirstRowDistance = 4f;
        public const float DefaultRowGap = 2f;
        /// <summary>The margin inside the field of view the rows keep (degrees), the same the view rule allows.</summary>
        public const float DefaultMarginDegrees = 2f;
        private const double GoldenRatioFraction = 0.6180339887498949;

        public readonly struct Placement
        {
            /// <summary>The individual's number, 0..count-1.</summary>
            public readonly int index;
            /// <summary>The index into the chosen families.</summary>
            public readonly int family;
            public readonly int row;
            public readonly Vector3 position;
            /// <summary>The root's yaw in degrees (180: facing the eye at the origin from +Z).</summary>
            public readonly float yaw;
            /// <summary>The clip time offset, seconds, in [0, clipSeconds).</summary>
            public readonly double phase;

            public Placement(int index, int family, int row, Vector3 position, float yaw, double phase)
            {
                this.index = index; this.family = family; this.row = row; this.position = position; this.yaw = yaw; this.phase = phase;
            }
        }

        /// <summary>The first <paramref name="wanted"/> of the available families in ordinal order (fewer when fewer exist); never duplicates.</summary>
        public static string[] ChooseFamilies(IEnumerable<string> available, int wanted)
        {
            if (available == null) throw new ArgumentNullException(nameof(available));
            return available.Where(f => !string.IsNullOrEmpty(f)).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).Take(Math.Max(0, wanted)).ToArray();
        }

        /// <summary>The individual <paramref name="index"/>'s family among <paramref name="familyCount"/>: round-robin.</summary>
        public static int FamilyOf(int index, int familyCount) => familyCount <= 0 ? -1 : index % familyCount;

        /// <summary>The individual's clip phase: the golden-ratio spread over the clip's length.</summary>
        public static double PhaseOf(int index, double clipSeconds)
        {
            if (!(clipSeconds > 0.0)) return 0.0;
            double fraction = index * GoldenRatioFraction;
            fraction -= Math.Floor(fraction);
            return fraction * clipSeconds;
        }

        /// <summary>The clip time of step <paramref name="step"/> for an individual of <paramref name="phase"/>.</summary>
        public static double PoseTime(int step, double stepSeconds, double phase) => step * stepSeconds + phase;

        /// <summary>How many individuals a row at <paramref name="distance"/> metres holds within the field of view, less the margin on each side.</summary>
        public static int RowCapacity(float distance, float horizontalFovDegrees, float spacing, float marginDegrees = DefaultMarginDegrees)
        {
            if (!(distance > 0f) || !(spacing > 0f)) return 1;
            float halfWidth = distance * Mathf.Tan(Mathf.Max(1f, 0.5f * horizontalFovDegrees - marginDegrees) * Mathf.Deg2Rad);
            return Math.Max(1, (int)Math.Floor(2.0 * halfWidth / spacing) + 1);
        }

        public static Placement[] Place(int count, int familyCount, double clipSeconds,
            float horizontalFovDegrees = DefaultHorizontalFovDegrees, float spacing = DefaultSpacing,
            float firstRowDistance = DefaultFirstRowDistance, float rowGap = DefaultRowGap)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            if (familyCount < 1) throw new ArgumentOutOfRangeException(nameof(familyCount));
            var placements = new Placement[count];
            int placed = 0;
            for (int row = 0; placed < count; row++)
            {
                float distance = firstRowDistance + row * rowGap;
                int capacity = Math.Min(RowCapacity(distance, horizontalFovDegrees, spacing), count - placed);
                // Centred: the row's individuals from -half to +half, one spacing apart.
                float half = 0.5f * (capacity - 1) * spacing;
                for (int i = 0; i < capacity; i++)
                {
                    int index = placed++;
                    placements[index] = new Placement(index, FamilyOf(index, familyCount), row, new Vector3(-half + i * spacing, 0f, distance), 180f, PhaseOf(index, clipSeconds));
                }
            }

            return placements;
        }

        /// <summary>
        /// Whether every placement stands inside a view of the given fields of view from an eye at <paramref name="eyeHeight"/>
        /// looking down +Z: the horizontal angle of its root and the vertical angles of its feet and its head
        /// (<paramref name="characterHeight"/> above the root) within the half fields, less <paramref name="marginDegrees"/>.
        /// </summary>
        public static bool AllInView(IReadOnlyList<Placement> placements, float horizontalFovDegrees, float verticalFovDegrees, float eyeHeight, float characterHeight, float marginDegrees, out string outside)
        {
            outside = null;
            float halfH = 0.5f * horizontalFovDegrees - marginDegrees, halfV = 0.5f * verticalFovDegrees - marginDegrees;
            for (int i = 0; i < placements.Count; i++)
            {
                Vector3 p = placements[i].position;
                if (!(p.z > 0f)) { outside = "individual " + i + " is not in front of the eye"; return false; }
                float horizontal = Mathf.Atan2(Mathf.Abs(p.x), p.z) * Mathf.Rad2Deg;
                float feet = Mathf.Atan2(eyeHeight - p.y, p.z) * Mathf.Rad2Deg;
                float head = Mathf.Atan2(p.y + characterHeight - eyeHeight, p.z) * Mathf.Rad2Deg;
                if (horizontal > halfH) { outside = "individual " + i + " at " + p.ToString("F2") + " is " + horizontal.ToString("F1") + " deg off the axis"; return false; }
                if (feet > halfV || head > halfV) { outside = "individual " + i + " at " + p.ToString("F2") + " leaves the vertical field (feet " + feet.ToString("F1") + ", head " + head.ToString("F1") + " deg)"; return false; }
            }

            return true;
        }

        /// <summary>The farthest row's distance, for the record (the shadow distance must reach it).</summary>
        public static float FarthestDistance(IReadOnlyList<Placement> placements)
        {
            float farthest = 0f;
            for (int i = 0; i < placements.Count; i++) farthest = Mathf.Max(farthest, placements[i].position.z);
            return farthest;
        }
    }
}
