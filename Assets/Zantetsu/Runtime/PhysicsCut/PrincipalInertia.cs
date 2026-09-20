using Unity.Mathematics;
using Zantetsu.ConvexCut;

namespace Zantetsu.PhysicsCut
{
    /// <summary>
    /// The principal-axis form of a symmetric inertia, which is the form a Unity <see cref="UnityEngine.Rigidbody"/>
    /// takes: three moments about axes given by a rotation, rather than the full matrix the kernel computes.
    /// <para>
    /// This conversion is written here because there is none to reuse: the existing mass properties
    /// (<see cref="MassProperties.InertiaAboutCom"/>) end at the symmetric matrix, and nothing in the repository
    /// diagonalises one. It is a numerical detail of setting the solver's values, not a new contract — DESIGN 7.2
    /// requires the inertia to be finite and settable on the solver and leaves the method to the implementation.
    /// </para>
    /// <para>
    /// The method is the cyclic Jacobi rotation, in double precision, with a bounded number of sweeps so that it
    /// always ends. A matrix it did not bring far enough down is not accepted: a caller that cannot get its three
    /// moments fails, and no substitute value is invented for it.
    /// </para>
    /// </summary>
    internal static class PrincipalInertia
    {
        /// <summary>At most this many sweeps of the three off-diagonal rotations. Convergence is cubic; three is
        /// normally enough, and the bound is only there so that this cannot run on.</summary>
        private const int MaxSweeps = 24;

        /// <summary>
        /// The three principal moments and the rotation whose columns are their axes, so that
        /// <c>rotation * diag(moments) * transpose(rotation)</c> is the matrix given.
        /// </summary>
        /// <returns>
        /// False when the matrix is not finite, when the sweeps did not bring the off-diagonal part down, or when a
        /// moment is not finite. Whether a moment is usable as a mass property — positive, above the solver's own
        /// limits — is the caller's judgement and not made here.
        /// </returns>
        internal static bool TryDiagonalize(in SymmetricMatrix3 inertia, out double3 moments, out quaternion rotation)
        {
            moments = default;
            rotation = quaternion.identity;
            if (!inertia.IsFinite)
            {
                return false;
            }

            double a00 = inertia.xx, a11 = inertia.yy, a22 = inertia.zz;
            double a01 = inertia.xy, a02 = inertia.xz, a12 = inertia.yz;

            // The accumulated rotation, column by column: v0, v1, v2 are the axes as they stand.
            double3 v0 = new double3(1.0, 0.0, 0.0);
            double3 v1 = new double3(0.0, 1.0, 0.0);
            double3 v2 = new double3(0.0, 0.0, 1.0);

            // What counts as small is relative to the matrix itself, so that this does not depend on the units the
            // masses and lengths happen to be in.
            double scale = math.abs(a00) + math.abs(a11) + math.abs(a22);
            double small = scale * 1e-14;
            bool converged = false;
            for (int sweep = 0; sweep < MaxSweeps; sweep++)
            {
                // The size of what is still off the diagonal, in the same units as the entries themselves.
                double off = math.sqrt((a01 * a01) + (a02 * a02) + (a12 * a12));
                if (off <= small)
                {
                    converged = true;
                    break;
                }

                Rotate(ref a00, ref a11, ref a01, ref a02, ref a12, ref v0, ref v1);   // (0,1)
                Rotate(ref a00, ref a22, ref a02, ref a01, ref a12, ref v0, ref v2);   // (0,2), sharing row 1
                Rotate(ref a11, ref a22, ref a12, ref a01, ref a02, ref v1, ref v2);   // (1,2), sharing row 0
            }

            if (!converged)
            {
                return false;
            }

            // A right-handed basis: the solver is given a rotation, not a reflection.
            if (math.dot(math.cross(v0, v1), v2) < 0.0)
            {
                v0 = -v0;
            }

            moments = new double3(a00, a11, a22);
            if (!math.all(math.isfinite(moments)) || !math.all(math.isfinite(v0)) || !math.all(math.isfinite(v1))
                || !math.all(math.isfinite(v2)))
            {
                return false;
            }

            var basis = new float3x3((float3)math.normalize(v0), (float3)math.normalize(v1), (float3)math.normalize(v2));
            rotation = math.normalize(new quaternion(basis));
            return math.all(math.isfinite(rotation.value));
        }

        /// <summary>
        /// One Jacobi rotation in the (p,q) plane. <paramref name="app"/>, <paramref name="aqq"/> and
        /// <paramref name="apq"/> are that plane's own entries; <paramref name="arp"/> and <paramref name="arq"/> are
        /// the two entries of the remaining row, which the rotation mixes; the two axes are turned with them.
        /// </summary>
        private static void Rotate(
            ref double app, ref double aqq, ref double apq, ref double arp, ref double arq, ref double3 vp, ref double3 vq)
        {
            if (apq == 0.0)
            {
                return;
            }

            double theta = (aqq - app) / (2.0 * apq);
            double t = theta >= 0.0
                ? 1.0 / (theta + math.sqrt((theta * theta) + 1.0))
                : -1.0 / (-theta + math.sqrt((theta * theta) + 1.0));
            double c = 1.0 / math.sqrt((t * t) + 1.0);
            double s = t * c;

            double pp = app, qq = aqq, pq = apq;
            app = (c * c * pp) - (2.0 * s * c * pq) + (s * s * qq);
            aqq = (s * s * pp) + (2.0 * s * c * pq) + (c * c * qq);
            apq = 0.0;

            double rp = arp, rq = arq;
            arp = (c * rp) - (s * rq);
            arq = (s * rp) + (c * rq);

            double3 p = vp, q = vq;
            vp = (c * p) - (s * q);
            vq = (s * p) + (c * q);
        }
    }
}
