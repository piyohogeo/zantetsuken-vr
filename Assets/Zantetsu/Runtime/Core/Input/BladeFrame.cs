using System;
using UnityEngine;

namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Blade-local coordinate system of the katana. Reference-free value type.
    /// Axes are validated and normalized on construction; the default value is
    /// an invalid frame.
    /// </summary>
    public readonly struct BladeFrame
    {
        // Fixed implementation tolerances; not exposed as gameplay tuning values.
        private const float OrthogonalityTolerance = 0.0001f;
        private const float MinAxisLengthSquared = 1e-12f;

        /// <summary>Blade long axis, from handle toward the tip.</summary>
        public readonly Vector3 BladeAxis;

        /// <summary>Direction from the spine toward the edge.</summary>
        public readonly Vector3 EdgeDirection;

        /// <summary>Normal of the blade's flat face.</summary>
        public readonly Vector3 SideNormal;

        /// <summary>Velocity sample point in blade-local coordinates.</summary>
        public readonly Vector3 CutSamplePoint;

        /// <summary>
        /// Whether this frame carries finite, non-zero axes and a finite cut
        /// sample point. Always true after a successful construction.
        /// </summary>
        public bool IsValid =>
            IsFinite(BladeAxis) && BladeAxis.sqrMagnitude > MinAxisLengthSquared
            && IsFinite(EdgeDirection) && EdgeDirection.sqrMagnitude > MinAxisLengthSquared
            && IsFinite(SideNormal) && SideNormal.sqrMagnitude > MinAxisLengthSquared
            && IsFinite(CutSamplePoint);

        /// <summary>
        /// Builds a validated blade frame. Axes must be finite, non-zero, and
        /// mutually near-orthogonal; they are normalized and their orientation
        /// is preserved as-is (no handedness reconstruction). The cut sample
        /// point must be finite.
        /// </summary>
        public BladeFrame(Vector3 bladeAxis, Vector3 edgeDirection, Vector3 sideNormal, Vector3 cutSamplePoint)
        {
            ValidateAxis(bladeAxis, nameof(bladeAxis));
            ValidateAxis(edgeDirection, nameof(edgeDirection));
            ValidateAxis(sideNormal, nameof(sideNormal));

            if (!IsFinite(cutSamplePoint))
            {
                throw new ArgumentException(nameof(cutSamplePoint) + " contains NaN or Infinity.", nameof(cutSamplePoint));
            }

            Vector3 b = Normalize(bladeAxis);
            Vector3 e = Normalize(edgeDirection);
            Vector3 s = Normalize(sideNormal);

            RequireOrthogonal(b, e, nameof(bladeAxis), nameof(edgeDirection));
            RequireOrthogonal(e, s, nameof(edgeDirection), nameof(sideNormal));
            RequireOrthogonal(s, b, nameof(sideNormal), nameof(bladeAxis));

            BladeAxis = b;
            EdgeDirection = e;
            SideNormal = s;
            CutSamplePoint = cutSamplePoint;
        }

        private static void ValidateAxis(Vector3 axis, string name)
        {
            if (!IsFinite(axis))
            {
                throw new ArgumentException(name + " contains NaN or Infinity.", name);
            }

            // Vector3.sqrMagnitude may overflow to Infinity for huge finite
            // components; reject those before normalizing.
            float squaredMagnitude = axis.sqrMagnitude;
            if (!float.IsFinite(squaredMagnitude))
            {
                throw new ArgumentException(name + " magnitude overflows.", name);
            }

            if (squaredMagnitude <= MinAxisLengthSquared)
            {
                throw new ArgumentException(name + " must be a non-zero axis.", name);
            }
        }

        // Custom normalization: unlike Vector3.normalized, it does not collapse
        // very short non-zero vectors to zero.
        private static Vector3 Normalize(Vector3 v)
        {
            float length = Mathf.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z);
            return new Vector3(v.x / length, v.y / length, v.z / length);
        }

        private static void RequireOrthogonal(Vector3 a, Vector3 b, string nameA, string nameB)
        {
            if (Mathf.Abs(Vector3.Dot(a, b)) > OrthogonalityTolerance)
            {
                throw new ArgumentException(nameA + " and " + nameB + " are not orthogonal.", nameA);
            }
        }

        private static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }
    }
}
