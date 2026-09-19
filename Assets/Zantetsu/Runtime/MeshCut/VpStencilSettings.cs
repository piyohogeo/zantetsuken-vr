using System;
using UnityEngine;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The inputs the stencil connection of <see cref="VpLogicalCutDisplay"/> is made with: how many stencil colours,
    /// the epsilons and margin the visibility, compatibility and projection tests take, and how many cameras may hold
    /// stencil work at once.
    /// <para>
    /// **There is no default.** DESIGN O-034 has not decided the colour limit, the facing, plane or offset epsilon or
    /// the margin, so nothing here offers a value for them: whoever makes a display says what it is made with, and a
    /// test's values stay the test's. Deciding the product values is not done by choosing one here.
    /// </para>
    /// </summary>
    public readonly struct VpStencilSettings
    {
        public VpStencilSettings(
            int maxStencilColors,
            float facingEpsilon,
            float planeEpsilon,
            float offsetEpsilon,
            Vector2 ndcMargin,
            int cameraCapacity)
        {
            this.maxStencilColors = maxStencilColors;
            this.facingEpsilon = facingEpsilon;
            this.planeEpsilon = planeEpsilon;
            this.offsetEpsilon = offsetEpsilon;
            this.ndcMargin = ndcMargin;
            this.cameraCapacity = cameraCapacity;
        }

        /// <summary>
        /// The stencil colour limit of DESIGN 5.6: colours 0 to this - 2 are ordinary and this - 1 is the last, merged
        /// one. At least one, and no more than the stencil materials can order (<c>VpStencilCapMaterials.MaxColors</c>);
        /// a larger value is refused, never cut down.
        /// </summary>
        public readonly int maxStencilColors;

        /// <summary>The facing epsilon of <see cref="VpCapVisibility"/>, a world-space length.</summary>
        public readonly float facingEpsilon;

        /// <summary>The world plane epsilon of <see cref="VpCapCompatibility"/>.</summary>
        public readonly float planeEpsilon;

        /// <summary>The offset epsilon of <see cref="VpCapCompatibility"/>, a world-space length.</summary>
        public readonly float offsetEpsilon;

        /// <summary>The projection margin of <see cref="VpCapProjectionConflict"/>, in normalized device coordinates per axis.</summary>
        public readonly Vector2 ndcMargin;

        /// <summary>
        /// How many cameras may be registered for stencil work at once. Each registered camera holds a stencil batch of
        /// its own; this is the fixed number of them, not a cache that grows.
        /// </summary>
        public readonly int cameraCapacity;

        /// <summary>Whether every value is in range; the reason when not.</summary>
        internal bool IsValid(int colourCeiling, out string reason)
        {
            if (maxStencilColors < 1 || maxStencilColors > colourCeiling)
            {
                reason = "the colour limit is from 1 to " + colourCeiling;
                return false;
            }

            if (!IsLength(facingEpsilon) || !IsLength(planeEpsilon) || !IsLength(offsetEpsilon)
                || !IsLength(ndcMargin.x) || !IsLength(ndcMargin.y))
            {
                reason = "an epsilon or the margin is negative or not finite";
                return false;
            }

            if (cameraCapacity < 1)
            {
                reason = "at least one camera";
                return false;
            }

            reason = null;
            return true;
        }

        private static bool IsLength(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0f;
        }
    }

    /// <summary>What one camera's last preparation made of the adopted snapshot.</summary>
    public readonly struct VpStencilPreparation
    {
        internal VpStencilPreparation(
            int targets, int groups, int culledGroups, int colours, int ordinaryColours, int groupsInLastColour,
            int volumeTargets, int capsDrawn)
        {
            this.volumeTargets = volumeTargets;
            this.capsDrawn = capsDrawn;
            this.targets = targets;
            this.groups = groups;
            this.culledGroups = culledGroups;
            this.colours = colours;
            this.ordinaryColours = ordinaryColours;
            this.groupsInLastColour = groupsInLastColour;
        }

        /// <summary>The caps asked about: every prepared cap record, seen or not.</summary>
        public readonly int targets;

        /// <summary>The compatibility groups they formed.</summary>
        public readonly int groups;

        /// <summary>The groups none of whose caps was seen, whose volumes and caps were left out.</summary>
        public readonly int culledGroups;

        /// <summary>The colours uploaded: ordinary ones, and the last one if any group is in it.</summary>
        public readonly int colours;

        /// <summary>The ordinary colours in use.</summary>
        public readonly int ordinaryColours;

        /// <summary>The groups put in the last, merged colour.</summary>
        public readonly int groupsInLastColour;

        /// <summary>The targets whose volumes were issued: every target of every group kept.</summary>
        public readonly int volumeTargets;

        /// <summary>The caps drawn: only those seen, within the groups kept.</summary>
        public readonly int capsDrawn;
    }

    /// <summary>What one camera's stencil batch has counted, and whether it is prepared now.</summary>
    public readonly struct VpStencilCameraCounts
    {
        internal VpStencilCameraCounts(
            int uploads, int bufferWrites, int initIssues, int volumeIssues, int capIssues, int colours,
            bool singlePassInstanced, bool preparedNow)
        {
            this.uploads = uploads;
            this.bufferWrites = bufferWrites;
            this.initIssues = initIssues;
            this.volumeIssues = volumeIssues;
            this.capIssues = capIssues;
            this.colours = colours;
            this.singlePassInstanced = singlePassInstanced;
            this.preparedNow = preparedNow;
        }

        public readonly int uploads;
        public readonly int bufferWrites;
        public readonly int initIssues;
        public readonly int volumeIssues;
        public readonly int capIssues;

        /// <summary>The colours of the arrangement the batch holds.</summary>
        public readonly int colours;

        /// <summary>The stereo condition of that arrangement, which is the body's upload's.</summary>
        public readonly bool singlePassInstanced;

        /// <summary>Whether the camera is prepared for this frame's adopted snapshot.</summary>
        public readonly bool preparedNow;
    }
}
