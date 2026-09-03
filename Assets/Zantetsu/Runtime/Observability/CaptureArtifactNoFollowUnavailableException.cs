using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Typed signal that the filesystem no-follow capability required for
    /// artifact verification is unavailable on this platform. It is raised
    /// before any Run root, Plan, or chunk is created, so capability
    /// insufficiency can never be mistaken for a content mismatch or a
    /// run-root collision.
    /// </summary>
    internal sealed class CaptureArtifactNoFollowUnavailableException : Exception
    {
        internal CaptureArtifactNoFollowUnavailableException(string message)
            : base(message)
        {
        }
    }
}
