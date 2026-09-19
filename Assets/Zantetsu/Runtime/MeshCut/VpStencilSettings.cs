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
        /// The stencil colour limit N of DESIGN 5.6 / D-185, D-186: at most N - 1 ordinary colours, and the last one
        /// reserved for what they cannot take, drawn the old way. The limit is never a reason to refuse a camera. At least
        /// one -- with one, everything is drawn in the last colour -- and no more than the stencil materials can order
        /// (<c>VpStencilCapMaterials.MaxColors</c>); a larger value is refused, never cut down.
        /// </summary>
        public readonly int maxStencilColors;

        /// <summary>The facing epsilon of <see cref="VpCapVisibility"/>, a world-space length.</summary>
        public readonly float facingEpsilon;

        /// <summary>
        /// The world plane epsilon of the older render-fragment compatibility test (<see cref="VpCapCompatibility"/>).
        /// Kept only for those older classifiers; the display's cap-job preparation (D-183) does not read it -- one volume
        /// stands for two jobs only when their inputs are identical, never within an epsilon.
        /// </summary>
        public readonly float planeEpsilon;

        /// <summary>
        /// The offset epsilon of the older render-fragment compatibility test, a world-space length. Kept only for those
        /// older classifiers; the display's cap-job preparation does not read it.
        /// </summary>
        public readonly float offsetEpsilon;

        /// <summary>
        /// The projection margin, in normalized device coordinates per axis, that the display's cap-job preparation grows
        /// every initial section by before deciding two volume groups may share a colour.
        /// </summary>
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

    /// <summary>How one camera's last preparation ended.</summary>
    public enum VpStencilPreparationOutcome
    {
        /// <summary>Not prepared: never asked, or a later attempt has begun.</summary>
        None = 0,

        /// <summary>Prepared and uploaded: the camera may draw this frame's adopted snapshot.</summary>
        Prepared = 1,

        /// <summary>
        /// Refused for room: the snapshot holds more caps than a preparation has room for. Nothing was uploaded.
        /// </summary>
        CapacityExceeded = 2,

        /// <summary>
        /// **No longer produced** (DESIGN D-185, D-186): the colour limit is never a refusal; what the ordinary colours
        /// cannot take is drawn in the last colour. Kept so that no other value is renumbered.
        /// </summary>
        ColorLimitExceeded = 3,
    }

    /// <summary>
    /// What one camera's last preparation made of the adopted snapshot, in the terms of DESIGN 5.6 / D-183, D-186: cap
    /// records, cap jobs, volume groups, colours, and what went to the last colour -- counted apart, never added into
    /// one another. The counts are those of the last attempt; a refused attempt keeps the counts it
    /// reached before the refusal only where they are settled (the cap records), and zero elsewhere.
    /// </summary>
    public readonly struct VpStencilPreparation
    {
        internal VpStencilPreparation(
            VpStencilPreparationOutcome outcome, int capRecords, int emptyCaps, int hiddenCaps, int jobs, int volumeGroups,
            int colours, int volumeCommands, int capsDrawn, int ordinaryVolumeGroups, int lastColourRenderFragments,
            int lastColourCaps)
        {
            this.ordinaryVolumeGroups = ordinaryVolumeGroups;
            this.lastColourRenderFragments = lastColourRenderFragments;
            this.lastColourCaps = lastColourCaps;
            this.outcome = outcome;
            this.capRecords = capRecords;
            this.emptyCaps = emptyCaps;
            this.hiddenCaps = hiddenCaps;
            this.jobs = jobs;
            this.volumeGroups = volumeGroups;
            this.colours = colours;
            this.volumeCommands = volumeCommands;
            this.capsDrawn = capsDrawn;
        }

        public readonly VpStencilPreparationOutcome outcome;

        /// <summary>Every cap record of the adopted snapshot, seen or not.</summary>
        public readonly int capRecords;

        /// <summary>Cap records whose drawing polygon is empty: never jobs.</summary>
        public readonly int emptyCaps;

        /// <summary>Non-empty cap records the both-eye visibility test left out.</summary>
        public readonly int hiddenCaps;

        /// <summary>Cap jobs: the non-empty caps the visibility test kept, each drawn once.</summary>
        public readonly int jobs;

        /// <summary>Volume groups made, whichever colour they went to.</summary>
        public readonly int volumeGroups;

        /// <summary>Volume groups in ordinary colours: each issued once as an own-face volume, whatever number of jobs shares it.</summary>
        public readonly int ordinaryVolumeGroups;

        /// <summary>
        /// The render fragments the last colour issued a volume for, each once, clipped by every selected face. Zero when
        /// the last colour was not used. Render fragments, not groups.
        /// </summary>
        public readonly int lastColourRenderFragments;

        /// <summary>The caps drawn in the last colour, one per job sent there. Zero when it was not used.</summary>
        public readonly int lastColourCaps;

        /// <summary>The colours uploaded: the ordinary ones used, and the last colour when anything went to it.</summary>
        public readonly int colours;

        /// <summary>
        /// The volume commands uploaded: each ordinary volume group's render fragment's commands and each last-colour
        /// render fragment's commands, one per submesh -- the GPU draws the volume issues stand for, not the CPU issues
        /// (one per colour).
        /// </summary>
        public readonly int volumeCommands;

        /// <summary>The cap polygons fanned for drawing: one per job, the last colour's included.</summary>
        public readonly int capsDrawn;
    }

    /// <summary>What one camera's stencil batch has counted, and whether it is prepared now.</summary>
    public readonly struct VpStencilCameraCounts
    {
        internal VpStencilCameraCounts(
            int uploads, int bufferWrites, int initIssues, int volumeIssues, int capIssues, int volumeGpuDraws, int colours,
            bool singlePassInstanced, bool preparedNow)
        {
            this.uploads = uploads;
            this.bufferWrites = bufferWrites;
            this.initIssues = initIssues;
            this.volumeIssues = volumeIssues;
            this.capIssues = capIssues;
            this.volumeGpuDraws = volumeGpuDraws;
            this.colours = colours;
            this.singlePassInstanced = singlePassInstanced;
            this.preparedNow = preparedNow;
        }

        public readonly int uploads;
        public readonly int bufferWrites;

        /// <summary>Initialisation issues on the CPU: one per colour drawn.</summary>
        public readonly int initIssues;

        /// <summary>Volume issues on the CPU: one per colour with volumes, whatever its command count.</summary>
        public readonly int volumeIssues;

        /// <summary>Cap issues on the CPU: one per colour with caps.</summary>
        public readonly int capIssues;

        /// <summary>The GPU volume draws the volume issues stood for: one per volume command.</summary>
        public readonly int volumeGpuDraws;

        /// <summary>The colours of the arrangement the batch holds.</summary>
        public readonly int colours;

        /// <summary>The stereo condition of that arrangement, which is the body's upload's.</summary>
        public readonly bool singlePassInstanced;

        /// <summary>Whether the camera is prepared for this frame's adopted snapshot.</summary>
        public readonly bool preparedNow;
    }
}