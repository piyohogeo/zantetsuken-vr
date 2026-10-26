using Zantetsu.Core.Statistics;

namespace Zantetsu.Sandbox
{
    /// <summary>Effective settings and preparation state supplied by a non-XR host after statistics consent.</summary>
    public interface IPlayStatisticsFlatSource
    {
        /// <summary>False during loading, reset and shader prewarming so those frames are outside play statistics.</summary>
        bool StatisticsReady { get; }

        /// <summary>Fills the current screen dimensions and effective rendering settings without allocating.</summary>
        void ReadStatisticsConditions(ref PlayConditions conditions);
    }
}
