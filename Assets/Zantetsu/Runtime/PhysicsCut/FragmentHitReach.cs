using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// How far from its owner's origin a fragment's shape can reach (DESIGN 19.1.7, D-193), and whether a sweep is
    /// certainly beyond it: the arithmetic the hit detector's early passing over of far fragments rests on. The
    /// detector reads an owner's position, takes the reach kept for its shape, and passes the shape over -- before its
    /// frame is read as a matrix, composed with the shape's placement and its world box made -- only when the sweep's
    /// box is certainly farther from that position than the reach.
    /// <para>
    /// **The reach is about the owner's origin, in the world's measure.** It is the product of two numbers, each settled
    /// where what it depends on is settled and never while an owner merely moves or turns:
    /// the shape's part (<see cref="TryOwnerReach"/>): the largest distance from the owner frame's origin to a corner of
    /// the shape's local box carried by the shape's own placement in the owner (<see cref="PhysicsOwnerShape.LocalToOwner"/>)
    /// -- so the box's centre, the shape's offset in the owner and any turn, scale or shear of that placement are all in
    /// it, and half the box's diagonal alone would not do; and the owner's part (<see cref="Settle"/>): the owner's own
    /// scale, put into that carrying, and a bound on how much the objects above it can stretch a length -- made only of
    /// local scales, which moving and turning the owner, or anything above it, do not change.
    /// </para>
    /// <para>
    /// **Only ever conservative.** Every rounding is taken upwards, and whatever cannot be vouched for -- a shape with
    /// no usable box, a placement or a matrix that is not finite, a square that overflows -- gives no reach at all
    /// (<see cref="None"/>), and a shape without a reach is never passed over by it: it takes the tests it took before.
    /// </para>
    /// </summary>
    internal static class FragmentHitReach
    {
        /// <summary>No reach can be vouched for: the shape is not passed over early.</summary>
        public const float None = -1f;

        // Upwards, for the rounding of a product or a sum of a few floats.
        private const float Up = 1f + 1e-5f;

        /// <summary>
        /// The shape's part, in the owner's frame: the largest distance from that frame's origin to a corner of the
        /// local box <paramref name="lo"/>..<paramref name="hi"/> carried by <paramref name="localToOwner"/>. The eight
        /// corners are carried as points -- no vertex is read -- and the image of a box under an affine map lies inside
        /// the hull of its corners' images, so no point of the shape is farther. False when the box or the placement is
        /// not finite, the placement is not affine, or the result is not finite.
        /// </summary>
        public static bool TryOwnerReach(float3 lo, float3 hi, in float4x4 localToOwner, out float reach)
        {
            reach = None;
            if (!math.all(math.isfinite(lo) & math.isfinite(hi)) || math.any(hi < lo)
                || !math.all(math.isfinite(localToOwner.c0) & math.isfinite(localToOwner.c1) & math.isfinite(localToOwner.c2) & math.isfinite(localToOwner.c3))
                || localToOwner.c0.w != 0f || localToOwner.c1.w != 0f || localToOwner.c2.w != 0f || localToOwner.c3.w != 1f)
            {
                return false;
            }

            float3 c0 = localToOwner.c0.xyz, c1 = localToOwner.c1.xyz, c2 = localToOwner.c2.xyz, t = localToOwner.c3.xyz;
            float most = 0f;
            for (int corner = 0; corner < 8; corner++)
            {
                float3 p = new float3((corner & 1) == 0 ? lo.x : hi.x, (corner & 2) == 0 ? lo.y : hi.y, (corner & 4) == 0 ? lo.z : hi.z);
                most = math.max(most, math.lengthsq(c0 * p.x + c1 * p.y + c2 * p.z + t));
            }

            // What the carrying itself can be off by: a part in a million of the largest term that went into a corner.
            float3 far = math.max(math.abs(lo), math.abs(hi));
            float terms = math.cmax(math.abs(c0) * far.x + math.abs(c1) * far.y + math.abs(c2) * far.z + math.abs(t));
            reach = math.sqrt(most) * Up + 4e-6f * terms;
            if (float.IsNaN(reach) || float.IsInfinity(reach))
            {
                reach = None;
                return false;
            }

            return true;
        }

        /// <summary>
        /// The reach of <paramref name="shape"/> on the owner whose Transform is <paramref name="owner"/>, in the world's
        /// measure, about that Transform's position; <see cref="None"/> when it cannot be vouched for.
        /// <para>
        /// The owner's part is made only of what moving and turning cannot change -- the owner's, or anything's above
        /// it: the owner's own local scale is put into the carrying of the corners (so the result is the farthest
        /// corner's distance itself for an owner under no scaled parent), and every object above it gives the largest of
        /// its local scale's three numbers, multiplied together (a length carried up through a parent is stretched by
        /// no more than that, however the child is turned under it). A bound read off the world matrix would be that
        /// of one attitude only: under an unevenly scaled parent it changes as the owner turns.
        /// </para>
        /// <para>
        /// Reads the local scales up the owner's parents once: for the moment a shape is given to an owner or the
        /// owner's scale or parent changes, not for an update.
        /// </para>
        /// </summary>
        public static float Settle(PhysicsOwnerShape shape, Transform owner)
        {
            if (shape == null || shape.IsFreed || owner == null)
            {
                return None;
            }

            float inParent;
            float3 own = owner.localScale;
            if (math.all(own == 1f))
            {
                if (!shape.TryOwnerReach(out inParent)) return None;   // the shape's own, kept
            }
            else
            {
                float4x4 placed = math.mul(float4x4.Scale(own), shape.LocalToOwner);
                if (!shape.TryLocalBounds(out float3 lo, out float3 hi) || !TryOwnerReach(lo, hi, in placed, out inParent)) return None;
            }

            float above = 1f;
            for (Transform parent = owner.parent; parent != null; parent = parent.parent)
            {
                above *= math.cmax(math.abs((float3)parent.localScale));
            }

            float reach = inParent * above * Up;
            return float.IsNaN(reach) || float.IsInfinity(reach) || reach < 0f ? None : reach;
        }

        /// <summary>
        /// Whether the box <paramref name="lo"/>..<paramref name="hi"/> (a sweep's four points, or several sweeps'; its
        /// largest coordinate <paramref name="boxReach"/>) is certainly farther from <paramref name="position"/> than
        /// <paramref name="reach"/>: the squared distance from the point to the box against the square of the reach
        /// widened by the margin the world-box test itself uses (a part in ten thousand of the largest coordinate in
        /// play, and a tenth of a millimetre). Touching is not beyond. No square root. **False whenever it is not
        /// certain**: no reach, a position that is not finite, a square that overflows.
        /// </summary>
        public static bool IsBeyond(float3 position, float reach, float3 lo, float3 hi, float boxReach)
        {
            if (!(reach >= 0f) || !math.all(math.isfinite(position)))
            {
                return false;
            }

            float3 gap = math.max(math.max(lo - position, position - hi), 0f);
            float distance2 = math.lengthsq(gap);
            float margin = 1e-4f * math.max(boxReach, math.cmax(math.abs(position)) + reach) + 1e-4f;
            float limit = reach + margin;
            float limit2 = limit * limit;
            // A NaN makes the comparison false; an overflow makes a side infinite, and nothing is certain of that.
            return distance2 > limit2 && !float.IsInfinity(distance2) && !float.IsInfinity(limit2);
        }
    }
}
