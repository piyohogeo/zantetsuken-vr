namespace Zantetsu.Observability
{
    /// <summary>
    /// One-way ownership phase of the single fixed Access Unit region owned by
    /// an <see cref="NvencOwnedAccessUnitBuffer"/>. The region advances
    /// Free → CollectorOwned → SinkOwned → Free and never moves backward.
    /// Poison is not a phase here: it is carried by the injected
    /// <see cref="NvencCaptureProcessState"/>, which rejects every transition
    /// while leaving the current phase unchanged.
    /// </summary>
    internal enum NvencAccessUnitPhase : int
    {
        Free = 0,
        CollectorOwned = 1,
        SinkOwned = 2,
    }
}
