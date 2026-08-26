namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Outcome of a blade input processing step.
    /// </summary>
    public enum BladeInputProcessingStatus : int
    {
        None = 0,
        WaitingForTracking = 1,
        WindowAccumulating = 2,
        GateAccepted = 3,
        GateRejected = 4,
        InvalidSample = 5,
    }
}
