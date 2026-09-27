using UnityEngine;

namespace Zantetsu.Core.MobPlan
{
    [CreateAssetMenu(menuName = "Zantetsu/MobPlan assets")]
    public sealed class MobPlanAssets : ScriptableObject
    {
        public TextAsset dataset;
        public TextAsset polygons;
        public TextAsset[] poseTables;
        public TextAsset initialPose;
        public TextAsset provenance;
    }
}
