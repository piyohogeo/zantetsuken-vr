namespace Zantetsu.Observability
{
    /// <summary>
    /// Result of a no-follow directory open: either an opened, identity-pinned
    /// directory handle or a terminal status.
    /// </summary>
    /// <remarks>
    /// It reuses <see cref="CaptureIndexFileOpenStatus"/> rather than
    /// introducing a second status vocabulary. Its reason for existing is that
    /// a cleanup which may resume after a partial failure has to tell "this
    /// directory is confirmed gone" from "this directory could not be
    /// observed" - and an exception, an error message, or a path-based
    /// existence probe cannot make that distinction safely.
    /// </remarks>
    internal sealed class CaptureIndexDirectoryOpen
    {
        private CaptureIndexDirectoryOpen(
            CaptureIndexFileOpenStatus status,
            CaptureIndexCommitDirectory directory)
        {
            Status = status;
            Directory = directory;
        }

        internal CaptureIndexFileOpenStatus Status { get; }

        internal CaptureIndexCommitDirectory Directory { get; }

        internal static CaptureIndexDirectoryOpen Of(CaptureIndexFileOpenStatus status)
        {
            return new CaptureIndexDirectoryOpen(status, null);
        }

        internal static CaptureIndexDirectoryOpen Opened(CaptureIndexCommitDirectory directory)
        {
            return new CaptureIndexDirectoryOpen(CaptureIndexFileOpenStatus.Opened, directory);
        }
    }
}
