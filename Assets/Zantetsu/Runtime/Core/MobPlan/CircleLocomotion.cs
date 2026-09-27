namespace Zantetsu.Core.MobPlan
{
    /// <summary>Probe manual control: full XZ, X only, Z only, then hold. Yaw is independent.</summary>
    public static class CircleLocomotion
    {
        public static Pose2 Move(WalkableMap map, float radius, float x, float z, float nextX, float nextZ)
        {
            if (map.ClearanceAt(nextX, nextZ) >= radius) return new Pose2(nextX, nextZ, 0);
            if (map.ClearanceAt(nextX, z) >= radius) return new Pose2(nextX, z, 0);
            if (map.ClearanceAt(x, nextZ) >= radius) return new Pose2(x, nextZ, 0);
            return new Pose2(x, z, 0);
        }
    }
}
