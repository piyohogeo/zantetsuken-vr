using System.Collections.Generic;
using Unity.Mathematics;

namespace Zantetsu.MeshCut
{
    /// <summary>Why a distribution was refused, or that it was carried out.</summary>
    public enum AnchorDistributionStatus
    {
        Ok,

        /// <summary>One of the two output sets was not given.</summary>
        NoOutput,

        /// <summary>
        /// The same list was given as both an input and an output, or as both outputs. Either would break a promise
        /// this makes — leaving the input set alone, and giving each side an on-plane anchor once.
        /// </summary>
        AliasedSets,

        /// <summary>The epsilon is negative, or is not a finite number.</summary>
        InvalidEpsilon,

        /// <summary>A coefficient of the adopted plane is not finite.</summary>
        NonFinitePlane,

        /// <summary>The plane's normal is all zeros, so it names no plane and no point has a side.</summary>
        DegeneratePlane,

        /// <summary>An anchor's position is not finite.</summary>
        NonFiniteAnchor,

        /// <summary>
        /// An anchor's signed distance came out non-finite from finite inputs (an overflow). It is refused rather than
        /// rounded onto the plane or rescued with a substitute value (DESIGN 7.1: 無効値をOnPlaneへ分類しない).
        /// </summary>
        NonFiniteClassification,
    }

    /// <summary>
    /// What one call to <see cref="FixedSupportAnchors.TryDistribute"/> put on each side, and the fixity that follows
    /// from it. A side that received at least one anchor has its whole child owner fixed; one that received none is
    /// dynamic (DESIGN 7.1: 各子OwnerはAnchorが一つでもあれば全体をStatic／Kinematicで固定し、なければ動的とする).
    /// Deriving that is all this says — setting an actor static or kinematic is Phase 4's, and no physics mode is
    /// introduced here.
    /// <para>
    /// **These are the counts this call added**, not the sizes of the output lists, which the caller may have brought
    /// with contents of their own. <see cref="IsPositiveFixed"/> and <see cref="IsNegativeFixed"/> likewise describe
    /// this distribution alone. A caller accumulating several distributions into one pair of lists decides fixity from
    /// what those lists hold in total — <see cref="FixedSupportAnchors.IsFixed"/> on the list's own count.
    /// </para>
    /// </summary>
    public readonly struct AnchorDistributionResult
    {
        internal AnchorDistributionResult(AnchorDistributionStatus status, int positiveCount, int negativeCount)
        {
            this.status = status;
            this.positiveCount = positiveCount;
            this.negativeCount = negativeCount;
        }

        public readonly AnchorDistributionStatus status;

        /// <summary>How many anchors this call appended to the positive side. Zero unless <see cref="status"/> is Ok.</summary>
        public readonly int positiveCount;

        /// <summary>How many anchors this call appended to the negative side. Zero unless <see cref="status"/> is Ok.</summary>
        public readonly int negativeCount;

        /// <summary>This distribution gave the positive side at least one anchor, so that child owner is fixed by it.</summary>
        public bool IsPositiveFixed => positiveCount > 0;

        /// <summary>This distribution gave the negative side at least one anchor, so that child owner is fixed by it.</summary>
        public bool IsNegativeFixed => negativeCount > 0;
    }

    /// <summary>
    /// The distribution of a source owner's fixed support anchors across an adopted cut plane (DESIGN 7.1), as pure
    /// data. An anchor is a finite logical point belonging to a **physics ownership unit**, expressed in that unit's
    /// Fragment Physics Frame — it is not attached to a convex, a cell, or any shape.
    /// <para>
    /// **What this does.** Given the source owner's current anchor set, the adopted plane and a finite non-negative
    /// <c>anchorEpsilon</c>, each point is classified by <c>s = dot(planeNormal, anchorPosition) + planeDistance</c>:
    /// <c>s &gt; e</c> goes to the positive side, <c>s &lt; -e</c> to the negative side, and <c>-e &lt;= s &lt;= e</c>
    /// to **each** side once. The sides' sets are the output, and a side that received an anchor means that child owner
    /// is fixed.
    /// </para>
    /// <para>
    /// **What this deliberately does not do.** It never checks an anchor against a convex, a cell or a shape: no
    /// containment, no surface agreement, no proximity, no nearest-point search, no projection, no correction, and no
    /// regeneration of anchors from collider vertices or a reduced hull. A point outside every shape is accepted and
    /// distributed on its side alone; the resulting fixity of a distant island, or an owner floating because its
    /// anchors all went the other way, is allowed (DESIGN 7.1: 形状外の点を許容し…離れた部分の固定や浮遊…を許容する).
    /// Anchors are never added to a convex's support, and a re-cut reads only the target owner's current set — never a
    /// sibling's, an ancestor's, or the object's.
    /// </para>
    /// <para>
    /// **Where it sits.** Admission comes first and is decided elsewhere: a request classified as a one-sided no-op, or
    /// skipped, must never reach this, and the source's set is then left exactly as it was. A distribution prepared
    /// here is unpublished until the positive and negative children are published together; this class knows nothing of
    /// ledgers, publication, actors, cook or handoff. Provisional and Final will later use this same distribution
    /// rather than recomputing one.
    /// </para>
    /// <para>
    /// **The frame is the caller's.** The points and the plane must already be expressed in the same Fragment Physics
    /// Frame; nothing here transforms anything, and no frame type is introduced. The plane is <c>(n.xyz, d)</c> with
    /// <c>dot(n, x) + d = 0</c>, the same form the rest of the cut path uses, and its sign is never flipped — whatever
    /// was on the positive side stays positive. Since <c>s</c> and <c>e</c> are compared directly, they must be in the
    /// same scale: a plane whose normal is not unit length scales <c>s</c> with it. The normal must not be all zeros,
    /// which would name no plane; no near-zero threshold is applied and nothing is normalized.
    /// </para>
    /// </summary>
    public static class FixedSupportAnchors
    {
        /// <summary>
        /// Distributes <paramref name="anchors"/> — the source owner's current set — across <paramref name="plane"/>,
        /// **appending** to <paramref name="positive"/> and <paramref name="negative"/>. The input set is never
        /// touched.
        /// <para>
        /// Everything that can be refused is checked before a single point is written, so a refusal — a <c>false</c>
        /// return — leaves both output sets exactly as they were. That is the contract: a refusal changes nothing. An
        /// exception is not part of it: the appends themselves can still throw, since a list may have to grow to take
        /// them, and a caller that sees one discards the output sets it was preparing. Both are safe here because the
        /// sets are unpublished until a cut is published — nothing has been handed to a child owner yet.
        /// </para>
        /// <para>
        /// An empty or null anchor set is normal and gives an Ok result with both sides empty: an owner with no fixed
        /// support is simply dynamic.
        /// </para>
        /// <para>
        /// The three lists must be three **different** lists, checked here by reference. Whether a caller's own
        /// wrapper hides shared storage underneath is not inspected: a caller is expected not to share storage between
        /// the sets, and not to modify the input while this runs.
        /// </para>
        /// </summary>
        /// <param name="anchors">The owner's current anchor positions, in its Fragment Physics Frame. Null is empty.</param>
        /// <param name="plane">The adopted cut plane in that same frame, as <c>(n.xyz, d)</c>, with a non-zero normal.</param>
        /// <param name="epsilon">The existing anchorEpsilon: finite and non-negative. Zero is allowed.</param>
        /// <param name="positive">Receives the positive side's anchors, appended.</param>
        /// <param name="negative">Receives the negative side's anchors, appended.</param>
        /// <param name="result">What this call distributed, and why it was refused if it was.</param>
        public static bool TryDistribute(
            IReadOnlyList<float3> anchors,
            float4 plane,
            float epsilon,
            List<float3> positive,
            List<float3> negative,
            out AnchorDistributionResult result)
        {
            if (positive == null || negative == null)
            {
                result = new AnchorDistributionResult(AnchorDistributionStatus.NoOutput, 0, 0);
                return false;
            }

            // The two promises that aliasing would break: the input is left alone, and an on-plane anchor reaches each
            // side once. Only direct identity is checked.
            if (ReferenceEquals(positive, negative)
                || ReferenceEquals(anchors, positive)
                || ReferenceEquals(anchors, negative))
            {
                result = new AnchorDistributionResult(AnchorDistributionStatus.AliasedSets, 0, 0);
                return false;
            }

            if (!math.isfinite(epsilon) || epsilon < 0f)
            {
                result = new AnchorDistributionResult(AnchorDistributionStatus.InvalidEpsilon, 0, 0);
                return false;
            }

            if (!math.all(math.isfinite(plane)))
            {
                result = new AnchorDistributionResult(AnchorDistributionStatus.NonFinitePlane, 0, 0);
                return false;
            }

            if (math.all(plane.xyz == float3.zero))
            {
                result = new AnchorDistributionResult(AnchorDistributionStatus.DegeneratePlane, 0, 0);
                return false;
            }

            int count = anchors?.Count ?? 0;

            // First pass: refuse anything that cannot be classified, without writing. The signed distances are not
            // kept — there are few anchors and this is a synchronous pass, so the second pass recomputes them rather
            // than allocating somewhere to hold them.
            for (int i = 0; i < count; i++)
            {
                float3 anchor = anchors[i];
                if (!math.all(math.isfinite(anchor)))
                {
                    result = new AnchorDistributionResult(AnchorDistributionStatus.NonFiniteAnchor, 0, 0);
                    return false;
                }

                if (!math.isfinite(SignedDistance(anchor, plane)))
                {
                    result = new AnchorDistributionResult(AnchorDistributionStatus.NonFiniteClassification, 0, 0);
                    return false;
                }
            }

            // Second pass: no refusal can happen from here, so nothing returns false part-way through a distribution.
            // The appends can still throw if a list has to grow, and the caller then discards these unpublished sets.
            int positives = 0;
            int negatives = 0;
            for (int i = 0; i < count; i++)
            {
                float3 anchor = anchors[i];
                float s = SignedDistance(anchor, plane);

                if (s > epsilon)
                {
                    positive.Add(anchor);
                    positives++;
                }
                else if (s < -epsilon)
                {
                    negative.Add(anchor);
                    negatives++;
                }
                else
                {
                    // On the plane, inside the epsilon band: the same value goes to each side exactly once.
                    positive.Add(anchor);
                    negative.Add(anchor);
                    positives++;
                    negatives++;
                }
            }

            result = new AnchorDistributionResult(AnchorDistributionStatus.Ok, positives, negatives);
            return true;
        }

        /// <summary>
        /// Whether an owner holding <paramref name="anchorCount"/> anchors is fixed. One is enough, and nothing else —
        /// no shape, no display geometry's emptiness, no GPU state — takes part in the decision.
        /// </summary>
        public static bool IsFixed(int anchorCount)
        {
            return anchorCount > 0;
        }

        private static float SignedDistance(float3 anchor, float4 plane)
        {
            return math.dot(plane.xyz, anchor) + plane.w;
        }
    }
}
