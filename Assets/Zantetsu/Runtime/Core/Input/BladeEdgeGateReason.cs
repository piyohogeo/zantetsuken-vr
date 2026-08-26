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
        DisplacementBelowMinimum = 5,
        NoLateralMotion = 6,
        EdgeLeadBelowThreshold = 7,
    }
}
