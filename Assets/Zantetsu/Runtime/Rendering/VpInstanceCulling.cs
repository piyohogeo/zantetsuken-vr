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

        /// <summary>
        /// The same judgement on bounds given in their own frame and carried by <paramref name="objectToWorld"/>, against
        /// planes held as an inward normal in xyz and a distance in w: kept unless the carried box lies wholly on the
        /// outer side of one plane by more than <see cref="Margin"/>. This is the test the GPU selection makes
        /// (VpInstanceCull.compute), written here once more as its reference: the box is tested as it is turned, not by
        /// the axis-aligned bounds around it.
        /// </summary>
        public static bool MayIntersect(Bounds local, Matrix4x4 objectToWorld, Vector4[] planes, int firstPlane, int planeCount)
        {
            Vector3 centre = objectToWorld.MultiplyPoint3x4(local.center);
            Vector3 extents = local.extents;
            Vector3 axisX = new Vector3(objectToWorld.m00, objectToWorld.m10, objectToWorld.m20) * extents.x;
            Vector3 axisY = new Vector3(objectToWorld.m01, objectToWorld.m11, objectToWorld.m21) * extents.y;
            Vector3 axisZ = new Vector3(objectToWorld.m02, objectToWorld.m12, objectToWorld.m22) * extents.z;
            for (int i = firstPlane; i < firstPlane + planeCount; i++)
            {
                Vector3 normal = new Vector3(planes[i].x, planes[i].y, planes[i].z);
                float radius = Mathf.Abs(Vector3.Dot(normal, axisX)) + Mathf.Abs(Vector3.Dot(normal, axisY)) + Mathf.Abs(Vector3.Dot(normal, axisZ));
                if (Vector3.Dot(normal, centre) + planes[i].w + radius < -Margin)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Whether the GPU selection keeps the instance for the body: it may intersect either eye, or no eye is given.</summary>
        public static bool KeptForBody(Bounds local, Matrix4x4 objectToWorld, VpCullConditions conditions)
        {
            if (conditions.eyeCount == 0)
            {
                return true;
            }

            for (int eye = 0; eye < conditions.eyeCount; eye++)
            {
                if (MayIntersect(local, objectToWorld, conditions.eyePlanes, eye * VpCullConditions.EyePlaneCount, VpCullConditions.EyePlaneCount))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Whether the GPU selection keeps the instance as a caster: it may intersect any split's volume, or no split is given.</summary>
        public static bool KeptAsCaster(Bounds local, Matrix4x4 objectToWorld, VpCullConditions conditions)
        {
            if (conditions.shadowSplitCount == 0)
            {
                return true;
            }

            for (int split = 0; split < conditions.shadowSplitCount; split++)
            {
                if (MayIntersect(
                        local, objectToWorld, conditions.shadowPlanes, split * VpCullConditions.SplitPlaneCapacity,
                        (int)conditions.shadowPlaneCounts[split]))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
