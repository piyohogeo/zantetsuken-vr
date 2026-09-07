namespace Zantetsu.Observability
{
    /// <summary>
    /// Synchronous Run chunk file session: the exact <c>.partial</c> file
    /// opened before the run begins. It appends exact byte ranges, closes the
    /// append handle, and moves the pending file to the fixed staging path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Append"/> appends the given range exactly once. After
    /// <see cref="CloseAppendHandle"/> no further append is accepted.
    /// <see cref="MovePendingToStaging"/> moves the closed file identity to the
    /// fixed staging path without replacing an existing file, and only a known
    /// success returns normally. The session never re-opens the file, never
    /// reads the whole file, never attempts a step more than once, never
    /// shortens or discards content, and never falls back to another path.
    /// </para>
    /// </remarks>
    internal interface INvencRunChunkFileSession
    {
        NvencRunChunkAppendOutcome Append(byte[] buffer, int offset, int validLength);

        void CloseAppendHandle();

        void MovePendingToStaging();
    }
}
