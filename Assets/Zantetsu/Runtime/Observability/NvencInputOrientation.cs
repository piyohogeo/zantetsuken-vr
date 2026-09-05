namespace Zantetsu.Observability
{
    /// <summary>
    /// Input image orientation for the NVENC bring-up boundary. Append-only;
    /// numeric values are fixed. TopLeft means row 0 is the top of the
    /// displayed image.
    /// </summary>
    internal enum NvencInputOrientation : int
    {
        None = 0,
        TopLeft = 1,
        BottomLeft = 2,
    }
}
