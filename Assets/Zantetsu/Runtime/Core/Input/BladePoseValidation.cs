using UnityEngine;

namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Shared numeric validation for blade pose inputs. Internal to the
    /// Zantetsu.Core assembly; this is not a public validation API.
    /// </summary>
    internal static class BladePoseValidation
    {
        private const float UnitLengthTolerance = 1e-4f;
        private const float OrthogonalityTolerance = 1e-4f;

        internal static bool IsFinite(Vector3 v)
        {
            return !float.IsNaN(v.x) && !float.IsInfinity(v.x)
                && !float.IsNaN(v.y) && !float.IsInfinity(v.y)
                && !float.IsNaN(v.z) && !float.IsInfinity(v.z);
        }

        internal static bool IsUnitAxis(Vector3 axis)
        {
            if (!IsFinite(axis))
            {
                return false;
            }

            float magnitude = axis.magnitude;
            if (!float.IsFinite(magnitude))
            {
                return false;
            }

            return Mathf.Abs(magnitude - 1f) <= UnitLengthTolerance;
        }

        internal static bool HasValidBladeAxes(in EvaluatedBladePose pose)
        {
            return IsUnitAxis(pose.BladeAxis)
                && IsUnitAxis(pose.EdgeDirection)
                && Mathf.Abs(Vector3.Dot(pose.BladeAxis, pose.EdgeDirection)) <= OrthogonalityTolerance;
        }
    }
}
