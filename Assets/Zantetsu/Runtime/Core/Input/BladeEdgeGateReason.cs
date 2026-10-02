namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Rejection reasons for the edge direction gate, listed in evaluation
    /// priority order. Gate-specific; this type does not alter
    /// <c>Zantetsu.Trace.TraceReason</c>.
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
