namespace Zantetsu.Observability
{
    /// <summary>
    /// Which eye a captured frame corresponds to. Append-only; numeric values
    /// are fixed.
    /// </summary>
    public enum CaptureEye : int
    {
        None = 0,
        Left = 1,
        Right = 2,
    }
}
