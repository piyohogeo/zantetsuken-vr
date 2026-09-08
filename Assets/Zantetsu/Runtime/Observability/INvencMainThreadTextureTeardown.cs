namespace Zantetsu.Observability
{
    /// <summary>
    /// Minimal synchronous Phase 0.11 Main Thread NV12 Texture teardown
    /// boundary. The single entry is called exactly once, from the Main
    /// Thread, after the Output Worker has completed its normal teardown and
    /// physically stopped, and destroys the eight Unity-managed NV12 Textures
    /// that were created at Run start, each exactly once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A normal return issues a success receipt for this exact implementation.
    /// The implementation performs no internal retry, regeneration, fallback
    /// Texture, second thread, <c>Task</c>, or ThreadPool use, and never
    /// touches the native resources owned by the Output Worker (native
    /// unregister, Encoder Session, Output Buffer, Events), the OS lock, the
    /// Trace, or the Plan. A mid-way exception issues no receipt, performs no
    /// guessed destroy of the remaining Textures, and propagates unchanged.
    /// </para>
    /// <para>
    /// The caller is contractually the Main Thread; no dispatcher or thread
    /// identity mechanism is added here. The real Unity implementation is out
    /// of scope for this unit, so the contract is fixed by fakes inside the
    /// EditMode fixtures.
    /// </para>
    /// </remarks>
    internal interface INvencMainThreadTextureTeardown
    {
        /// <summary>
        /// Destroys the eight Unity-managed NV12 Textures of the exact Run
        /// exactly once, on the Main Thread, and returns the success receipt
        /// for this exact implementation. A mid-way exception propagates
        /// unchanged with no receipt.
        /// </summary>
        NvencMainThreadTextureTeardownReceipt TearDown();
    }
}
