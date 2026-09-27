using System;

namespace Zantetsu.Core.MobPlan
{
    /// <summary>
    /// Planar rigid transform (XZ + yaw in degrees, Unity convention: forward = (sin yaw, cos yaw), positive yaw turns right).
    /// Pure struct with no UnityEngine dependency so the planner thread can use it. Matches PlanarPose.Compose of the probe.
    /// </summary>
    [Serializable]
    public struct Pose2
    {
        public float X;
        public float Z;
        public float YawDeg;

        public const float Deg2Rad = (float)(Math.PI / 180.0);
        public const float Rad2Deg = (float)(180.0 / Math.PI);

        public Pose2(float x, float z, float yawDeg)
        {
            X = x; Z = z; YawDeg = yawDeg;
        }

        public static readonly Pose2 Identity = new Pose2(0f, 0f, 0f);

        public static float WrapDeg(float degrees)
        {
            degrees %= 360f;
            if (degrees > 180f) degrees -= 360f;
            else if (degrees <= -180f) degrees += 360f;
            return degrees;
        }

        public float ForwardX => (float)Math.Sin(YawDeg * Deg2Rad);
        public float ForwardZ => (float)Math.Cos(YawDeg * Deg2Rad);

        /// <summary>Rotates a local vector by this yaw (same as Quaternion.Euler(0, yaw, 0) * v on XZ).</summary>
        public void Rotate(float vx, float vz, out float ox, out float oz)
        {
            var r = YawDeg * Deg2Rad;
            var c = (float)Math.Cos(r);
            var s = (float)Math.Sin(r);
            ox = vx * c + vz * s;
            oz = -vx * s + vz * c;
        }

        public void TransformPoint(float px, float pz, out float ox, out float oz)
        {
            Rotate(px, pz, out ox, out oz);
            ox += X;
            oz += Z;
        }

        /// <summary>this × b (apply b in this frame).</summary>
        public Pose2 Compose(Pose2 b)
        {
            Rotate(b.X, b.Z, out var rx, out var rz);
            return new Pose2(X + rx, Z + rz, WrapDeg(YawDeg + b.YawDeg));
        }

        public Pose2 Inverse()
        {
            var inverse = new Pose2(0f, 0f, -YawDeg);
            inverse.Rotate(-X, -Z, out var rx, out var rz);
            return new Pose2(rx, rz, -YawDeg);
        }

        public static float Distance(Pose2 a, Pose2 b)
        {
            var dx = a.X - b.X;
            var dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        public float DistanceTo(float x, float z)
        {
            var dx = X - x;
            var dz = Z - z;
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Unsigned angle (deg) between this pose's forward and the direction to (x, z); 0 when the target is at the pose.</summary>
        public float HeadingErrorTo(float x, float z)
        {
            var dx = x - X;
            var dz = z - Z;
            var length = Math.Sqrt(dx * dx + dz * dz);
            if (length < 1e-6) return 0f;
            var target = (float)Math.Atan2(dx, dz) * Rad2Deg;
            return Math.Abs(WrapDeg(target - YawDeg));
        }

        public override string ToString() => $"({X:F2}, {Z:F2}) yaw {YawDeg:F1}";
    }
}
