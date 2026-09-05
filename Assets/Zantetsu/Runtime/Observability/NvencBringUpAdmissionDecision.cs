namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless admission decision for the NVENC bring-up boundary.
    /// </summary>
    internal enum NvencBringUpAdmissionDecision : int
    {
        None = 0,
        Supported = 1,
        Unsupported = 2,
    }
}
