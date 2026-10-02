namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Rejection reasons for the edge direction gate, listed in evaluation
    /// priority order.
    /// </summary>
    public enum BladeEdgeGateReason : int
    {
        None = 0,
        InvalidInput = 1,
        WindowTooShort = 2,
        WindowTooLong = 3,
        SpeedBelowMinimum = 4,
        SpeedAboveMaximum = 5,
        DisplacementBelowMinimum = 6,
        NoLateralMotion = 7,
        EdgeLeadBelowThreshold = 8,
    }
}
