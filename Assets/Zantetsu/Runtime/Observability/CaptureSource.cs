namespace Zantetsu.Observability
{
    /// <summary>
    /// Source of a captured frame. Append-only; numeric values are fixed.
    /// </summary>
    public enum CaptureSource : int
    {
        None = 0,
        UnityRenderTexture = 1,
        OpenXRProjection = 2,
    }
}
