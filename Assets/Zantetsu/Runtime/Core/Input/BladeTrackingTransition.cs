namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Change in usable blade tracking between consecutive samples.
    /// </summary>
    public enum BladeTrackingTransition : int
    {
        None = 0,
        Lost = 1,
        Restored = 2,
    }
}
