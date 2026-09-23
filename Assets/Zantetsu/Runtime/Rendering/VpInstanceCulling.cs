using UnityEngine;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// Stage 3 probe: the conservative instance culling test shared by the forward and the per-cascade shadow selections.
    /// An instance is kept unless its world bounds lie entirely on the outer side of one of the planes, by more than
    /// <see cref="Margin"/>. Planes follow <see cref="GeometryUtility.CalculateFrustumPlanes(Camera)"/>: normals point
    /// inward. It never rejects an instance that touches the volume, so it may keep instances that draw nothing.
    /// </summary>
    public static class VpInstanceCulling
    {
        /// <summary>World-space tolerance, in metres, added on the outer side of every plane.</summary>
        public const float Margin = 1e-3f;

        /// <summary>The frustum planes of one eye.</summary>
        public const int EyePlaneCount = 6;

        /// <summary>
        /// Writes the forward view's frustum planes to <paramref name="planes"/> (at least 2 * <see cref="EyePlaneCount"/>
        /// long) and returns the number of eyes: for a stereo camera, the left eye's planes then the right eye's from the
        /// camera's stereo view and projection matrices; otherwise the camera's. An instance is visible to the view when it
        /// may intersect either eye (<see cref="MayIntersectAnyEye"/>). Allocates nothing.
        /// </summary>
        public static int GetEyePlanes(Camera camera, Plane[] planes)
        {
            if (camera.stereoEnabled)
            {
                GeometryUtility.CalculateFrustumPlanes(
                    camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left) * camera.GetStereoViewMatrix(Camera.StereoscopicEye.Left), s_eye);
                System.Array.Copy(s_eye, 0, planes, 0, EyePlaneCount);
                GeometryUtility.CalculateFrustumPlanes(
                    camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right) * camera.GetStereoViewMatrix(Camera.StereoscopicEye.Right), s_eye);
                System.Array.Copy(s_eye, 0, planes, EyePlaneCount, EyePlaneCount);
                return 2;
            }

            GeometryUtility.CalculateFrustumPlanes(camera.projectionMatrix * camera.worldToCameraMatrix, s_eye);
            System.Array.Copy(s_eye, 0, planes, 0, EyePlaneCount);
            return 1;
        }

        /// <summary>Whether the bounds may intersect the frustum of any of <paramref name="eyeCount"/> eyes of 6 planes each.</summary>
        public static bool MayIntersectAnyEye(Bounds bounds, Plane[] eyePlanes, int eyeCount)
        {
            for (int eye = 0; eye < eyeCount; eye++)
            {
                if (MayIntersect(bounds, eyePlanes, eye * EyePlaneCount, EyePlaneCount))
                {
                    return true;
                }
            }

            return false;
        }

        private static readonly Plane[] s_eye = new Plane[EyePlaneCount];

        public static bool MayIntersect(Bounds bounds, Plane[] planes, int planeCount)
        {
            return MayIntersect(bounds, planes, 0, planeCount);
        }

        public static bool MayIntersect(Bounds bounds, Plane[] planes, int firstPlane, int planeCount)
        {
            Vector3 centre = bounds.center;
            Vector3 extents = bounds.extents;
            for (int i = firstPlane; i < firstPlane + planeCount; i++)
            {
                Vector3 normal = planes[i].normal;
                float radius = extents.x * Mathf.Abs(normal.x) + extents.y * Mathf.Abs(normal.y) + extents.z * Mathf.Abs(normal.z);
                if (Vector3.Dot(normal, centre) + planes[i].distance + radius < -Margin)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
