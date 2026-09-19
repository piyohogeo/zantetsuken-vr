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
        /// The stencil colour limit of DESIGN 5.6 / D-183: every colour up to it is an ordinary one, and there is no
        /// merged last colour -- a camera whose volume groups cannot be given colours within it is refused its
        /// preparation (<see cref="VpStencilPreparationOutcome.ColorLimitExceeded"/>). At least one, and no more than the
        /// stencil materials can order (<c>VpStencilCapMaterials.MaxColors</c>); a larger value is refused, never cut down.
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
        /// Refused because the volume groups could not be given colours within the limit without two that may overlap
        /// on the screen sharing one (D-183). Nothing was uploaded and the camera may not draw; another attempt -- from
        /// another view -- may be made before it draws. Not a shortage of room, and not a stop of the display.
        /// </summary>
        ColorLimitExceeded = 3,
    }

    /// <summary>
    /// What one camera's last preparation made of the adopted snapshot, in the terms of DESIGN 5.6 / D-183: cap records,
    /// cap jobs, volume groups and colours. The counts are those of the last attempt; a refused attempt keeps the counts it
    /// reached before the refusal only where they are settled (the cap records), and zero elsewhere.
    /// </summary>
    public readonly struct VpStencilPreparation
    {
        internal VpStencilPreparation(
            VpStencilPreparationOutcome outcome, int capRecords, int emptyCaps, int hiddenCaps, int jobs, int volumeGroups,
            int colours, int volumeCommands, int capsDrawn)
        {
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

        /// <summary>Volume groups: each issued once, whatever number of jobs shares it.</summary>
        public readonly int volumeGroups;

        /// <summary>The colours uploaded, every one an ordinary one.</summary>
        public readonly int colours;

        /// <summary>
        /// The volume commands uploaded: each volume group's render fragment's commands, one per submesh -- the GPU draws
        /// the volume issues stand for, not the CPU issues (one per colour).
        /// </summary>
        public readonly int volumeCommands;

        /// <summary>The cap polygons fanned for drawing: one per job.</summary>
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