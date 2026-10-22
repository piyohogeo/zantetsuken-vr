using UnityEngine;

namespace Zantetsu.Core.Slash
{
    /// <summary>A screen line and its perspective fan, captured in world space once when a flick is recognized.</summary>
    public readonly struct ScreenSlashFrame
    {
        internal ScreenSlashFrame(
            Vector3 origin, Plane plane, Vector3 travelAxis, Vector3 spanAxis,
            Vector3 initialA, Vector3 initialB, Vector3 startTravelPerMetre, Vector3 endTravelPerMetre, float initialTravelDistance)
        {
            Origin = origin;
            SourceSlashPlane = plane;
            TravelAxis = travelAxis;
            SpanAxis = spanAxis;
            InitialA = initialA;
            InitialB = initialB;
            StartTravelPerMetre = startTravelPerMetre;
            EndTravelPerMetre = endTravelPerMetre;
            InitialTravelDistance = initialTravelDistance;
            IsValid = true;
        }

        public bool IsValid { get; }
        public Vector3 Origin { get; }
        public Plane SourceSlashPlane { get; }
        /// <summary>The unit camera-to-flick ray; flight speed is measured along this direction.</summary>
        public Vector3 TravelAxis { get; }
        public Vector3 SpanAxis { get; }
        public Vector3 InitialA { get; }
        public Vector3 InitialB { get; }
        /// <summary>Endpoint displacements for each metre flown by the ray through the flick position.</summary>
        public Vector3 StartTravelPerMetre { get; }
        public Vector3 EndTravelPerMetre { get; }
        public float InitialTravelDistance { get; }
    }

    public static class ScreenSlashGeometry
    {
        private const float MinimumLengthSquared = 1e-12f;

        /// <summary>
        /// Captures the line through the flick position in its direction, extended to both viewport edges. Stroke
        /// length has no effect. The initial segment is on the near clip plane; later segments expand along the two
        /// captured edge rays. No camera or screen state is retained after this call.
        /// </summary>
        public static bool TryCreate(Camera camera, Vector2 pixelPosition, Vector2 pixelDirection, out ScreenSlashFrame frame)
        {
            frame = default;
            if (camera == null || camera.orthographic)
            {
                return false;
            }

            Rect pixels = camera.pixelRect;
            if (!(pixels.width > 0f) || !(pixels.height > 0f))
            {
                return false;
            }

            Vector2 point = new Vector2((pixelPosition.x - pixels.x) / pixels.width, (pixelPosition.y - pixels.y) / pixels.height);
            Vector2 direction = new Vector2(pixelDirection.x / pixels.width, pixelDirection.y / pixels.height);
            if (!TryClipLine(point, direction, out Vector2 a, out Vector2 b))
            {
                return false;
            }

            return TryBuild(
                camera.transform.position, camera.transform.forward, camera.nearClipPlane,
                camera.ViewportPointToRay(new Vector3(point.x, point.y, 0f)).direction,
                camera.ViewportPointToRay(new Vector3(a.x, a.y, 0f)).direction,
                camera.ViewportPointToRay(new Vector3(b.x, b.y, 0f)).direction, out frame);
        }

        /// <summary>Pure symmetric perspective equivalent, useful to build a frame without a live Camera.</summary>
        public static bool TryCreate(
            in Pose cameraPose, float verticalFieldOfView, float aspect, float nearClipPlane,
            Vector2 viewportPosition, Vector2 viewportDirection, out ScreenSlashFrame frame)
        {
            frame = default;
            if (!float.IsFinite(verticalFieldOfView) || verticalFieldOfView <= 0f || verticalFieldOfView >= 180f
                || !float.IsFinite(aspect) || aspect <= 0f
                || !TryClipLine(viewportPosition, viewportDirection, out Vector2 a, out Vector2 b))
            {
                return false;
            }

            float halfHeight = Mathf.Tan(verticalFieldOfView * Mathf.Deg2Rad * 0.5f);
            float halfWidth = halfHeight * aspect;
            return TryBuild(
                cameraPose.position, cameraPose.rotation * Vector3.forward, nearClipPlane,
                cameraPose.rotation * PerspectiveRay(viewportPosition, halfWidth, halfHeight),
                cameraPose.rotation * PerspectiveRay(a, halfWidth, halfHeight),
                cameraPose.rotation * PerspectiveRay(b, halfWidth, halfHeight), out frame);
        }

        private static Vector3 PerspectiveRay(Vector2 point, float halfWidth, float halfHeight)
        {
            return new Vector3((2f * point.x - 1f) * halfWidth, (2f * point.y - 1f) * halfHeight, 1f).normalized;
        }

        private static bool TryBuild(
            Vector3 origin, Vector3 forward, float nearClipPlane, Vector3 travel, Vector3 rayA, Vector3 rayB,
            out ScreenSlashFrame frame)
        {
            frame = default;
            if (!SlashMath.IsFinite(origin) || !SlashMath.IsFinite(forward) || !SlashMath.IsFinite(travel)
                || !SlashMath.IsFinite(rayA) || !SlashMath.IsFinite(rayB)
                || !float.IsFinite(nearClipPlane) || nearClipPlane <= 0f)
            {
                return false;
            }

            float travelDepth = Vector3.Dot(travel, forward);
            float depthA = Vector3.Dot(rayA, forward);
            float depthB = Vector3.Dot(rayB, forward);
            if (!(travelDepth > 0f) || !(depthA > 0f) || !(depthB > 0f))
            {
                return false;
            }

            Vector3 span = rayB / depthB - rayA / depthA;
            Vector3 normal = Vector3.Cross(rayA, rayB);
            if (!float.IsFinite(span.sqrMagnitude) || !float.IsFinite(normal.sqrMagnitude)
                || !SlashMath.IsFinite(span) || !SlashMath.IsFinite(normal)
                || span.sqrMagnitude <= MinimumLengthSquared || normal.sqrMagnitude <= MinimumLengthSquared)
            {
                return false;
            }

            Vector3 initialA = origin + rayA * (nearClipPlane / depthA);
            Vector3 initialB = origin + rayB * (nearClipPlane / depthB);
            Vector3 startTravel = rayA * (travelDepth / depthA);
            Vector3 endTravel = rayB * (travelDepth / depthB);
            if (!SlashMath.IsFinite(initialA) || !SlashMath.IsFinite(initialB)
                || !SlashMath.IsFinite(startTravel) || !SlashMath.IsFinite(endTravel))
            {
                return false;
            }

            frame = new ScreenSlashFrame(origin, new Plane(normal.normalized, origin), travel.normalized, span.normalized,
                initialA, initialB, startTravel, endTravel, nearClipPlane / travelDepth);
            return true;
        }

        private static bool TryClipLine(Vector2 point, Vector2 direction, out Vector2 a, out Vector2 b)
        {
            a = default;
            b = default;
            if (!IsFinite(point) || !IsFinite(direction) || direction.sqrMagnitude <= MinimumLengthSquared
                || point.x < 0f || point.x > 1f || point.y < 0f || point.y > 1f)
            {
                return false;
            }

            // Normalize before clipping, so multiplying a flick vector never changes its accepted screen fan.
            direction.Normalize();
            float from = float.NegativeInfinity;
            float to = float.PositiveInfinity;
            if (!ClipAxis(point.x, direction.x, ref from, ref to) || !ClipAxis(point.y, direction.y, ref from, ref to)
                || !float.IsFinite(from) || !float.IsFinite(to) || !(to > from))
            {
                return false;
            }

            a = point + direction * from;
            b = point + direction * to;
            return IsFinite(a) && IsFinite(b);
        }

        private static bool ClipAxis(float point, float direction, ref float from, ref float to)
        {
            if (direction == 0f)
            {
                return point >= 0f && point <= 1f;
            }

            float first = -point / direction;
            float second = (1f - point) / direction;
            from = Mathf.Max(from, Mathf.Min(first, second));
            to = Mathf.Min(to, Mathf.Max(first, second));
            return to >= from;
        }

        private static bool IsFinite(Vector2 value) => float.IsFinite(value.x) && float.IsFinite(value.y);
    }
}
