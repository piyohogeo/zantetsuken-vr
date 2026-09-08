namespace Zantetsu.Observability
{
    /// <summary>
    /// Minimal synchronous Phase 0.11 Output Worker teardown boundary. The
    /// single <see cref="TearDown"/> entry is called exactly once from the
    /// Output Worker thread, after the terminal request has been collected,
    /// and completes the safe reverse-order teardown of the Output
    /// Worker-owned resources — the Owned Access Unit region and the native
    /// registrations, Output Buffer, Events, and Session — without touching any
    /// Unity-managed Texture.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A normal return issues a receipt that proves only that this exact
    /// teardown implementation completed its own resource teardown. The
    /// implementation performs no internal retry, rollback, second thread,
    /// <c>Task</c>, or ThreadPool use; an exception means the ownership became
    /// indeterminate and propagates unchanged as a fatal failure.
    /// </para>
    /// <para>
    /// The real native implementation is out of scope for this unit; the
    /// contract is fixed by fakes inside the EditMode fixtures.
    /// </para>
    /// </remarks>
    internal interface INvencOutputWorkerTeardown
    {
        /// <summary>
        /// Completes the safe reverse-order teardown of the Output
        /// Worker-owned resources exactly once, on the Output Worker thread.
        /// A normal return issues the success receipt for this exact
        /// implementation; an exception propagates unchanged.
        /// </summary>
        NvencOutputWorkerTeardownReceipt TearDown();
    }
}
