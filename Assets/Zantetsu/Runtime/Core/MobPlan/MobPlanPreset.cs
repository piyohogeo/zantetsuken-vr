namespace Zantetsu.Core.MobPlan
{
    public static class MobPlanPreset
    {
        public static PlannerConfig Planner()
        {
            var config = new PlannerConfig();
            config.ApplyDatasetDefaults("INERTIAL");
            config.useBackgroundThread = false; // The host dispatches to the dedicated worker.
            return config;
        }
        public static ScenarioSettings Scenario(string polygonsPath) => new ScenarioSettings
        {
            map = MapPreset.Scene, sceneMapPath = polygonsPath,
            sceneCenterX = -220, sceneCenterZ = -170, sceneWindowMeters = 300,
            sceneWindowMarginMeters = 15, sceneClearanceCapMeters = 12, cellSizeMeters = .1f,
            goals = GoalPreset.PlayerFlow, npcCount = 20, seed = 1, npcRadius = .35f,
            flowRingMeters = 8.3f, startRadiusMeters = 0,
            flowPassOffsetMinMeters = 2.5f, flowPassOffsetMaxMeters = 8,
            flowFarGoalMeters = 15.9f, flowFarGoalConeDeg = 60, reassignGoals = true,
            recycleEnabled = true, recycleDistanceMeters = 35, recycleSpawnMinMeters = 15,
            recycleSpawnMaxMeters = 25, recycleViewDeg = 110, recycleViewRangeMeters = 50,
            recycleOcclusionInView = true, recycleCooldownSeconds = 5,
            dynamicObstacle = true, obstacleRadius = 1.5f, obstacleMaxSpeed = 1.49f, obstacleSeed = 7,
            obstaclePrediction = ObstaclePredictionMode.ConstantVelocity, obstaclePredictionSeconds = 2,
        };
    }
}
