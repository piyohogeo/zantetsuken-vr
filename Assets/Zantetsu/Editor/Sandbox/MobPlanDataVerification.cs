using System;
using System.IO;
using System.Linq;
using UnityEngine;
using Zantetsu.Core.Animation;
using Zantetsu.Core.MobPlan;

namespace Zantetsu.Sandbox.Editor
{
    public static class MobPlanDataVerification
    {
        public static void Verify()
        {
            const string path = "Assets/Licensed/MobPlan/";
            var dataset = ClipDataset.Read(File.ReadAllBytes(path + "dataset.bytes"));
            if (dataset.Count != 416 || dataset.EdgeCount != 15909 || dataset.WaitCapableCount != 2)
                throw new InvalidOperationException("Unexpected planning graph");
            var bank = new MobPoseBank(dataset, Directory.GetFiles(path + "Tables", "*.bytes").Select(File.ReadAllBytes).ToArray());
            float maxRootError = 0;
            int samples = 0;
            for (int node = 0; node < dataset.Count; node++)
            {
                var clip = dataset.Clips[node];
                var draft = new AgentPlan { AgentId = node };
                draft.Segments.Add(new PlanSegment(node, 0, clip.Duration, Pose2.Identity));
                var plan = new PublishedMobPlan(draft, 0);
                for (int s = 0; s < clip.SampleCount; s++)
                {
                    float time = dataset.T[clip.SampleOffset + s];
                    if (!bank.TryEvaluate(plan, time, dataset, out _, out _, out var root)) throw new InvalidOperationException("Root query failed");
                    var expected = dataset.LocalPoseAt(node, time);
                    maxRootError = Mathf.Max(maxRootError, Vector2.Distance(new Vector2(root.position.x, root.position.z), new Vector2(expected.X, expected.Z)));
                    samples++;
                }
            }
            if (maxRootError > .005f) throw new InvalidOperationException("Table/planner root mismatch: " + maxRootError);
            Directory.CreateDirectory("Logs/MobPlan");
            string report = $"nodes={dataset.Count} edges={dataset.EdgeCount} tables={bank.Tables.Count} bones=66 rootSamples={samples} maxRootErrorMeters={maxRootError:R}";
            File.WriteAllText("Logs/MobPlan/data-verification.txt", report);
            Debug.Log("MOBPLAN DATA " + report);
        }
    }
}
