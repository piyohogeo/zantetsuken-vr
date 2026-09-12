namespace Zantetsu.Observability
{
    /// <summary>
    /// Minimal synchronous Phase 0.11 Main Thread teardown boundary for what
    /// Unity owns in one Run: the conversion <c>CommandBuffer</c> that issued
    /// the render events, and the fixed pool of source RGBA RenderTextures the
    /// conversions read from. The single entry is called exactly once, from
    /// the Main Thread, after the Output Worker has completed its normal
    /// teardown and physically stopped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The implementation is bound to the exact Run chunk whose Unity-managed
    /// resources it owns, and exposes that binding through the O(1)
    /// <see cref="IsBoundTo"/> predicate, so a teardown built for a foreign
    /// Run is rejected before any side effect. A normal return issues a
    /// success receipt for this exact implementation and context. The
    /// implementation performs no internal retry, regeneration, fallback
    /// resource, second thread, <c>Task</c>, or ThreadPool use, and never
    /// touches what the native session owns and the Output Worker releases -
    /// the NV12 input textures, their views and registrations, the Output
    /// Buffers, the Completion Events and the Encoder Session itself - nor the
    /// OS lock, the Trace, or the Plan. A mid-way exception issues no receipt,
    /// destroys nothing further here, and propagates unchanged.
    /// </para>
    /// <para>
    /// The caller is contractually the Main Thread; no dispatcher or thread
    /// identity mechanism is added here.
    /// </para>
    /// </remarks>
    internal interface INvencMainThreadTextureTeardown
    {
        /// <summary>
        /// Destroys the exact Run's Unity-managed resources on the Main
        /// Thread - the conversion <c>CommandBuffer</c> and the source RGBA
        /// texture pool - and returns the success receipt for this exact
        /// implementation and its bound Run chunk context. A mid-way exception
        /// propagates unchanged with no receipt.
        /// </summary>
        NvencMainThreadTextureTeardownReceipt TearDown();

        /// <summary>
        /// O(1) exact-reference binding predicate: true only when this
        /// teardown owns the Unity-managed resources of the exact Run chunk
        /// context passed in. Used by the coordinator to reject a foreign-Run
        /// teardown before any side effect, without exposing the held context.
        /// </summary>
        bool IsBoundTo(NvencRunChunkContext context);
    }
}
