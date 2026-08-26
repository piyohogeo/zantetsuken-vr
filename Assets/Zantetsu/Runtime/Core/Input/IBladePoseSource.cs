namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Device-independent blade pose input source contract. The interface
    /// itself is device-agnostic; implementations convert device-specific
    /// input (such as OpenXR or Input System pose data) into this contract.
    /// </summary>
    public interface IBladePoseSource
    {
        /// <summary>
        /// Tries to obtain the latest blade pose sample. When this returns
        /// false, <paramref name="sample"/> may be treated as default.
        /// </summary>
        bool TryGetLatestSample(out BladePoseSample sample);
    }
}
