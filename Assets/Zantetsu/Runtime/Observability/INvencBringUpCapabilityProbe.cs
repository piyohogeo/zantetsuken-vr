namespace Zantetsu.Observability
{
    /// <summary>
    /// Observation boundary that reports the NVENC bring-up capability facts of
    /// the graphics configuration the process is actually using.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One call is one observation attempt against the current active graphics
    /// configuration. A successful attempt returns a snapshot whose
    /// <see cref="NvencBringUpCapabilityV1.IsInitialized"/> is true; a fact
    /// that is not supported is reported inside that snapshot as the false or
    /// zero it was observed to be, never as an exception. If the observation
    /// itself cannot be completed, the exception propagates unchanged.
    /// </para>
    /// <para>
    /// An implementation performs no retry, no caching, no fallback, no adapter
    /// selection, and no graphics API switch. It reports on the same adapter as
    /// the graphics device Unity is currently using - not on "an NVIDIA GPU
    /// exists somewhere in this machine" - and it classifies nothing: whether
    /// those facts admit the fixed profile is
    /// <see cref="NvencBringUpAdmissionValidatorV1"/>'s answer alone.
    /// </para>
    /// <para>
    /// Ownership and lifetime of any session, device, or native handle an
    /// implementation may need are deliberately outside this interface. The
    /// reported maximum encode width and height are two independent observed
    /// values; that a particular combination of them will initialize a session
    /// is not something this boundary can promise.
    /// </para>
    /// </remarks>
    internal interface INvencBringUpCapabilityProbe
    {
        NvencBringUpCapabilityV1 Probe();
    }
}
