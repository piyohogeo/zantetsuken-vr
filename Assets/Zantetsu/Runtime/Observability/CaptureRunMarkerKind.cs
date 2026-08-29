namespace Zantetsu.Observability
{
    /// <summary>
    /// Identifies which durable Capture Run marker file a write operation
    /// targets. Values are fixed, explicitly numbered, and append-only:
    /// <see cref="None"/> is 0, <see cref="Initialization"/> is 1, and
    /// <see cref="Ready"/> is 2. <see cref="CaptureRunMarkerWriteOperation"/>
    /// rejects <see cref="None"/> and undefined values. Existing values must
    /// never be renumbered or removed.
    /// </summary>
    internal enum CaptureRunMarkerKind : int
    {
        None = 0,
        Initialization = 1,
        Ready = 2
    }
}
