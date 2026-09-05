namespace Zantetsu.Observability
{
    /// <summary>
    /// Color conversion applied on the GPU before NVENC submission.
    /// Append-only; numeric values are fixed.
    /// </summary>
    internal enum NvencColorConversion : int
    {
        None = 0,
        Bt709LimitedRangeNv12 = 1,
    }
}
