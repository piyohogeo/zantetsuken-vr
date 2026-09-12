namespace Zantetsu.Observability
{
    /// <summary>
    /// Instance-scoped boundary that issues the GPU conversion command one
    /// accepted submission needs, binding exactly the source surface, encode
    /// sample slot, and sync lease that submission reserved.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>true</c> means the command is issued and the render callback may read
    /// the source at any moment. <c>false</c> means no command exists and none
    /// will: the caller may give the reservations back. It is not backpressure,
    /// an unsupported configuration, or a request to try again.
    /// </para>
    /// <para>
    /// An exception means whether a command exists - and therefore whether the
    /// source is still being read - cannot be established. The caller poisons
    /// and keeps everything rather than guessing.
    /// </para>
    /// </remarks>
    internal interface INvencGpuConversionCommandIssuer
    {
        bool TryIssue(in NvencSubmissionRecord record);
    }
}
