using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Typed signal that an artifact verification or publication could not
    /// execute because the verification buffer was unavailable. Distinct from
    /// a content mismatch or an ordinary I/O failure, so the recovery
    /// coordinator can converge to a <c>Deferred</c> disposition instead of a
    /// collision, without performing any filesystem change.
    /// </summary>
    internal sealed class CaptureArtifactVerificationDeferredException : Exception
    {
        internal CaptureArtifactVerificationDeferredException(string message)
            : base(message)
        {
        }
    }
}
