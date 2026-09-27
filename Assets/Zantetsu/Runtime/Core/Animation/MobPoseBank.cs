using System;
using System.Collections.Generic;
using Zantetsu.Core.MobPlan;
using UnityEngine;

namespace Zantetsu.Core.Animation
{
    /// <summary>One decoded 66-bone bank per crowd. Yaw variants share all bone sample arrays.</summary>
    public sealed class MobPoseBank
    {
        private readonly PoseTable[] nodes;
        public IReadOnlyList<PoseTable> Tables { get; }
        public MobPoseBank(ClipDataset dataset, byte[][] bytes)
        {
            var byClip = new Dictionary<string, PoseTable>();
            var tables = new List<PoseTable>();
            foreach (var data in bytes)
            {
                if (!PoseTable.TryRead(data, out var table, out var error)) throw new ArgumentException(error);
                if (!table.HasRootTrack) throw new ArgumentException("MobPlan requires a root track");
                if (table.BoneCount != 66) throw new ArgumentException("MobPlan requires the audited 66-bone bank");
                if (tables.Count > 0)
                    for (int i = 0; i < 66; i++) if (table.BonePath(i) != tables[0].BonePath(i)) throw new ArgumentException("Bank rig layout mismatch");
                byClip.Add(table.ClipId, table); tables.Add(table);
            }
            Tables = tables.AsReadOnly();
            nodes = new PoseTable[dataset.Count];
            for (int i = 0; i < nodes.Length; i++)
            {
                var clip = dataset.Clips[i];
                nodes[i] = byClip[clip.ClipId].WithYawRate(clip.YawRateDegps);
                if (clip.RangeFrom + clip.Duration > nodes[i].DurationSeconds + .001) throw new ArgumentException("Node exceeds table duration");
            }
        }
        public PoseTable Table(int node) => nodes[node];
        public bool TryEvaluate(PublishedMobPlan plan, double time, ClipDataset dataset, out PoseTable table, out double source, out Pose root)
        {
            table = null; source = 0; root = default;
            if (!plan.TryResolve(time, dataset, out var segment, out source, out _)) return false;
            table = nodes[segment.Clip];
            source = Math.Min(source, table.DurationSeconds);
            if (!table.TryEvaluateRoot(source, out var at) || !table.TryEvaluateRoot(dataset.Clips[segment.Clip].RangeFrom, out var start)) return false;
            Quaternion placement = Quaternion.Euler(0, segment.WorldStart.YawDeg - start.rotation.eulerAngles.y, 0);
            var startPlanar = new Vector3(start.position.x, 0, start.position.z);
            root = new Pose(new Vector3(segment.WorldStart.X, 0, segment.WorldStart.Z) + placement * (at.position - startPlanar), placement * at.rotation);
            return true;
        }
    }
}
