using Unity.Mathematics;
using UnityEngine;
using Zantetsu.MeshCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The temporary mass properties of a Provisional pair (DESIGN 7.2): an approximation from a conservative box
    /// around the source and this cut's plane, for a short-lived solver. **It is never the final authority**, and it
    /// is not the mass properties of the source's own convexes carried over — those are the exact properties of an
    /// uncut shape and say nothing about the two sides.
    /// <para>
    /// **The box.** Axis-aligned in the source **actor's** frame, enclosing every vertex of every convex of the
    /// source's shape once that shape's own placement (<see cref="PhysicsOwnerShape.LocalToOwner"/>) has been
    /// applied. So it encloses the source's collision shape as the actor carries it, and no more is claimed of it:
    /// it is a conservative range, not a fit. No principal axes are searched for and no minimum-volume box is made.
    /// </para>
    /// <para>
    /// **The division.** The plane is carried into the same actor frame and the box is divided by it. Where the two
    /// volumes are finite and add to something positive, the parent mass is divided in that ratio and the negative
    /// side is the parent mass less the positive one, so the two always add back to the parent. Where they are not,
    /// the parent mass is divided equally. The centre of mass of a side is the centroid of its part of the box.
    /// </para>
    /// <para>
    /// **Both masses have to survive the conversion the body will do**: they reach PhysX as a float, so a division
    /// that is finite and positive as a double but not as a float is refused here rather than handed on. Nothing is
    /// clamped and no floor or ceiling is introduced.
    /// </para>
    /// <para>
    /// **The inertia** is a box inertia, taken over the **whole** source box rather than the side's part of it, which
    /// is the conservative reading of DESIGN 7.2 and needs nothing of the division. The box is axis-aligned in the
    /// actor's frame, so its principal axes are the actor's own and the rotation that goes with it is the identity.
    /// </para>
    /// <para>
    /// **The one inertia fallback** is the case DESIGN 7.2 names, and only that one: where the box inertia is **not
    /// finite**, the source actor's own inertia at this side's share of the mass is used instead — *with the source's
    /// own principal rotation*, because three numbers without the orientation they were taken in name a different
    /// body. A box inertia that is finite but not positive is **not** rescued: it fails the same condition every
    /// other unusable mass fails, and no pair is built.
    /// </para>
    /// </summary>
    internal static unsafe class ProvisionalBoxMass
    {
        internal struct Side
        {
            internal double mass;
            internal float3 centerOfMass;
            internal float3 inertia;

            /// <summary>The orientation <see cref="inertia"/> is expressed in, which Unity keeps beside it.</summary>
            internal quaternion inertiaRotation;
        }

        internal static bool TryDivide(
            PhysicsOwnerShape shape, float4 planeLocal, double parentMass,
            float3 sourceInertia, quaternion sourceInertiaRotation,
            out Side positive, out Side negative, out float3 planeNormalOwner)
        {
            positive = default;
            negative = default;
            planeNormalOwner = default;

            // A shape that has been given back is not one to draw a mass from, whatever it still remembers.
            if (shape == null || shape.IsFreed)
            {
                return false;
            }

            if (!TryBox(shape, out float3 lo, out float3 hi))
            {
                return false;
            }

            // The plane belongs to the shape's numerical frame; the box to the actor's. One conversion, with the
            // existing one, and the caller's plane is left as it is.
            float4x4 localToOwner = shape.LocalToOwner;
            var mapping = new Matrix4x4(
                localToOwner.c0, localToOwner.c1, localToOwner.c2, localToOwner.c3);
            if (!VpCutPlane.TryGeometryLocalToWorld(planeLocal, mapping, out float4 planeOwner))
            {
                return false;
            }

            planeNormalOwner = math.normalize(planeOwner.xyz);
            if (!math.all(math.isfinite(planeNormalOwner)))
            {
                return false;
            }

            ClippedBox(lo, hi, planeOwner, out double volumePositive, out double3 momentPositive,
                out double volumeNegative, out double3 momentNegative);

            double total = volumePositive + volumeNegative;
            double massPositive;
            if (total > 0.0 && !double.IsNaN(total) && !double.IsInfinity(total)
                && volumePositive >= 0.0 && volumeNegative >= 0.0)
            {
                massPositive = parentMass * (volumePositive / total);
            }
            else
            {
                // All zero, not finite, or not computable: halves (DESIGN 7.2).
                massPositive = parentMass * 0.5;
            }

            double massNegative = parentMass - massPositive;
            if (!(massPositive > 0.0) || !(massNegative > 0.0)
                || double.IsNaN(massPositive) || double.IsInfinity(massPositive)
                || double.IsNaN(massNegative) || double.IsInfinity(massNegative))
            {
                return false;
            }

            // A mass is only usable if it is still finite and positive **in the type the body is given it in**: it
            // reaches PhysX through Rigidbody.mass, a float, and a division that is finite as a double but becomes
            // an infinity or a zero on the way there is no mass a solver can take. Nothing is clamped, no floor or
            // ceiling is introduced: it is refused here, where every other unusable mass is refused.
            var floatPositive = (float)massPositive;
            var floatNegative = (float)massNegative;
            if (!math.isfinite(floatPositive) || !math.isfinite(floatNegative)
                || floatPositive <= 0f || floatNegative <= 0f)
            {
                return false;
            }

            float3 centre = (lo + hi) * 0.5f;
            positive.mass = massPositive;
            negative.mass = massNegative;
            positive.centerOfMass = Centroid(volumePositive, momentPositive, centre);
            negative.centerOfMass = Centroid(volumeNegative, momentNegative, centre);
            SideInertia(
                hi - lo, massPositive, sourceInertia, sourceInertiaRotation, parentMass,
                out positive.inertia, out positive.inertiaRotation);
            SideInertia(
                hi - lo, massNegative, sourceInertia, sourceInertiaRotation, parentMass,
                out negative.inertia, out negative.inertiaRotation);

            return math.all(math.isfinite(positive.centerOfMass)) && math.all(math.isfinite(negative.centerOfMass))
                && math.all(math.isfinite(positive.inertia)) && math.all(math.isfinite(negative.inertia))
                && math.all(positive.inertia > 0f) && math.all(negative.inertia > 0f)
                && math.all(math.isfinite(positive.inertiaRotation.value))
                && math.all(math.isfinite(negative.inertiaRotation.value));
        }

        /// <summary>
        /// The shape's own box, carried into the actor's frame. The shape already knows the box its convexes lie in,
        /// in its own local frame, from when it was built; all that is left here is the eight corners of that box
        /// through <see cref="PhysicsOwnerShape.LocalToOwner"/>, wrapped again as an axis-aligned range.
        /// <para>
        /// **No vertex of any convex is read.** Wrapping a box that was turned gives a box no smaller than the one a
        /// scan of the vertices would give, and often larger; that is accepted, because what this box is for is a
        /// division by volume and it must only be sure to contain the shape. Nothing here looks for principal axes or
        /// a smallest box.
        /// </para>
        /// </summary>
        internal static bool TryBox(PhysicsOwnerShape shape, out float3 lo, out float3 hi)
        {
            lo = default;
            hi = default;
            if (!shape.TryLocalBounds(out float3 localLo, out float3 localHi))
            {
                return false;
            }

            float4x4 localToOwner = shape.LocalToOwner;
            lo = new float3(float.PositiveInfinity);
            hi = new float3(float.NegativeInfinity);
            for (int corner = 0; corner < 8; corner++)
            {
                var at = new float3(
                    (corner & 1) == 0 ? localLo.x : localHi.x,
                    (corner & 2) == 0 ? localLo.y : localHi.y,
                    (corner & 4) == 0 ? localLo.z : localHi.z);
                float3 inOwner = math.transform(localToOwner, at);
                if (!math.all(math.isfinite(inOwner)))
                {
                    return false;
                }

                lo = math.min(lo, inOwner);
                hi = math.max(hi, inOwner);
            }

            return math.all(math.isfinite(lo)) && math.all(math.isfinite(hi));
        }

        private static float3 Centroid(double volume, double3 moment, float3 fallback)
        {
            if (volume > 0.0 && !double.IsNaN(volume) && !double.IsInfinity(volume))
            {
                double3 centre = moment / volume;
                var value = new float3((float)centre.x, (float)centre.y, (float)centre.z);
                if (math.all(math.isfinite(value)))
                {
                    return value;
                }
            }

            return fallback;
        }

        /// <summary>
        /// The box inertia of the whole source box at this side's mass, with the identity rotation because that box
        /// is axis-aligned in the actor's own frame.
        /// <para>
        /// Where that box inertia is **not finite** — the one case DESIGN 7.2 provides for — the source actor's own
        /// inertia at this side's share of the parent mass is used, **with the source's own principal rotation**. An
        /// actor whose principal axes are not the actor's axes keeps them here: scaling the three numbers alone and
        /// leaving the rotation at the identity would describe a differently oriented body.
        /// </para>
        /// <para>
        /// A box inertia that is finite but zero or negative is deliberately **not** rescued. It is left as it came
        /// out, and the caller's existing condition — every component finite and positive — refuses it there, in the
        /// one place every unusable mass is refused.
        /// </para>
        /// </summary>
        private static void SideInertia(
            float3 extents, double mass, float3 sourceInertia, quaternion sourceInertiaRotation, double parentMass,
            out float3 inertia, out quaternion inertiaRotation)
        {
            double x = extents.x, y = extents.y, z = extents.z;
            var box = new float3(
                (float)(mass * ((y * y) + (z * z)) / 12.0),
                (float)(mass * ((x * x) + (z * z)) / 12.0),
                (float)(mass * ((x * x) + (y * y)) / 12.0));
            if (math.all(math.isfinite(box)))
            {
                inertia = box;
                inertiaRotation = quaternion.identity;
                return;
            }

            inertia = sourceInertia * (float)(mass / parentMass);
            inertiaRotation = math.normalize(sourceInertiaRotation);
        }

        /// <summary>
        /// The volume and the first moment of each part of an axis-aligned box divided by a plane. The box is split
        /// into six tetrahedra and each of those is divided by the plane, case by case.
        /// <para>
        /// The tests compare each part against a closed form worked out for that part alone. That the two parts
        /// recombine to the whole box is checked as well, but it is a necessary condition, not a proof of this
        /// decomposition: a wrong pair of parts can recombine correctly.
        /// </para>
        /// </summary>
        private static void ClippedBox(
            float3 lo, float3 hi, float4 plane,
            out double volumePositive, out double3 momentPositive,
            out double volumeNegative, out double3 momentNegative)
        {
            volumePositive = 0.0;
            momentPositive = double3.zero;
            volumeNegative = 0.0;
            momentNegative = double3.zero;

            // The eight corners, then the standard six-tetrahedron split of a box around one main diagonal.
            var corner = stackalloc double3[8];
            for (int i = 0; i < 8; i++)
            {
                corner[i] = new double3(
                    (i & 1) == 0 ? lo.x : hi.x,
                    (i & 2) == 0 ? lo.y : hi.y,
                    (i & 4) == 0 ? lo.z : hi.z);
            }

            int* tets = stackalloc int[24]
            {
                0, 1, 3, 7,
                0, 1, 7, 5,
                0, 5, 7, 4,
                0, 3, 2, 7,
                0, 2, 6, 7,
                0, 6, 4, 7,
            };

            var p = new double4(plane.x, plane.y, plane.z, plane.w);
            for (int t = 0; t < 6; t++)
            {
                double3 a = corner[tets[t * 4 + 0]];
                double3 b = corner[tets[t * 4 + 1]];
                double3 c = corner[tets[t * 4 + 2]];
                double3 d = corner[tets[t * 4 + 3]];
                DivideTet(a, b, c, d, p, true, ref volumePositive, ref momentPositive);
                DivideTet(a, b, c, d, p, false, ref volumeNegative, ref momentNegative);
            }
        }

        private static void DivideTet(
            double3 a, double3 b, double3 c, double3 d, double4 plane, bool keepPositive,
            ref double volume, ref double3 moment)
        {
            var v = stackalloc double3[4];
            v[0] = a; v[1] = b; v[2] = c; v[3] = d;
            var s = stackalloc double[4];
            int kept = 0;
            for (int i = 0; i < 4; i++)
            {
                double signed = math.dot(plane.xyz, v[i]) + plane.w;
                s[i] = keepPositive ? signed : -signed;
                if (s[i] > 0.0)
                {
                    kept++;
                }
            }

            if (kept == 0)
            {
                return;
            }

            if (kept == 4)
            {
                Accumulate(v[0], v[1], v[2], v[3], ref volume, ref moment);
                return;
            }

            // The indices of the kept and the dropped corners, in order.
            var keptIndex = stackalloc int[4];
            var dropIndex = stackalloc int[4];
            int keptCount = 0, dropCount = 0;
            for (int i = 0; i < 4; i++)
            {
                if (s[i] > 0.0)
                {
                    keptIndex[keptCount++] = i;
                }
                else
                {
                    dropIndex[dropCount++] = i;
                }
            }

            if (kept == 1)
            {
                int k = keptIndex[0];
                double3 p0 = Cross(v, s, k, dropIndex[0]);
                double3 p1 = Cross(v, s, k, dropIndex[1]);
                double3 p2 = Cross(v, s, k, dropIndex[2]);
                Accumulate(v[k], p0, p1, p2, ref volume, ref moment);
                return;
            }

            if (kept == 3)
            {
                // Everything but the corner that was dropped: the whole tetrahedron less the small one at it.
                int dpt = dropIndex[0];
                double3 q0 = Cross(v, s, dpt, keptIndex[0]);
                double3 q1 = Cross(v, s, dpt, keptIndex[1]);
                double3 q2 = Cross(v, s, dpt, keptIndex[2]);
                double wholeVolume = 0.0;
                double3 wholeMoment = double3.zero;
                Accumulate(v[0], v[1], v[2], v[3], ref wholeVolume, ref wholeMoment);
                double cornerVolume = 0.0;
                double3 cornerMoment = double3.zero;
                Accumulate(v[dpt], q0, q1, q2, ref cornerVolume, ref cornerMoment);
                volume += wholeVolume - cornerVolume;
                moment += wholeMoment - cornerMoment;
                return;
            }

            // Two kept and two dropped: a wedge, as three tetrahedra.
            int a0 = keptIndex[0], a1 = keptIndex[1];
            int b0 = dropIndex[0], b1 = dropIndex[1];
            double3 m00 = Cross(v, s, a0, b0);
            double3 m01 = Cross(v, s, a0, b1);
            double3 m10 = Cross(v, s, a1, b0);
            double3 m11 = Cross(v, s, a1, b1);
            Accumulate(v[a0], v[a1], m00, m01, ref volume, ref moment);
            Accumulate(v[a1], m00, m01, m11, ref volume, ref moment);
            Accumulate(v[a1], m00, m11, m10, ref volume, ref moment);
        }

        /// <summary>Where the edge between two corners meets the plane, from their signed values.</summary>
        private static double3 Cross(double3* v, double* s, int keptCorner, int droppedCorner)
        {
            double denominator = s[keptCorner] - s[droppedCorner];
            double t = denominator != 0.0 ? s[keptCorner] / denominator : 0.0;
            return v[keptCorner] + (v[droppedCorner] - v[keptCorner]) * t;
        }

        private static void Accumulate(double3 a, double3 b, double3 c, double3 d, ref double volume, ref double3 moment)
        {
            double signed = math.dot(b - a, math.cross(c - a, d - a)) / 6.0;
            double piece = math.abs(signed);
            volume += piece;
            moment += piece * ((a + b + c + d) * 0.25);
        }
    }
}
