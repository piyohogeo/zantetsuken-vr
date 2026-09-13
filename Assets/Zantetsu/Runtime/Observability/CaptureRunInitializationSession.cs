using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Non-owning value that correlates a Run's ready evidence with its run
    /// identity. It holds no lock and cannot release one; OS lock ownership
    /// lives exclusively in <see cref="CaptureRunInitializationSessionOwnershipLease"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The session holds only ready evidence and forwards run identity
    /// straight from it. Session
    /// validity means only that the ready evidence is valid, not that any lock
    /// is still held; lock liveness is confirmed through the issue's
    /// <see cref="CaptureRunInitializationSessionOwnershipLease"/>.
    /// </para>
    /// <para>
    /// Forwarding properties read straight from the evidence and hold no
    /// copied value. This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationSession
    {
        private readonly CaptureRunInitializationReadyEvidence _readyEvidence;

        internal CaptureRunInitializationSession(CaptureRunInitializationReadyEvidence readyEvidence)
        {
            _readyEvidence = readyEvidence;
        }

        internal CaptureRunInitializationReadyEvidence ReadyEvidence => _readyEvidence;

        internal CaptureRunInitializationExecutionReceipt ExecutionReceipt => _readyEvidence.FreshExecutionReceipt;

        internal CaptureRunRootLayout RootLayout => _readyEvidence.RootLayout;

        internal long TestRunId => _readyEvidence.TestRunId;

        internal string RunInitializationId => _readyEvidence.RunInitializationId;

        internal bool IsValid => _readyEvidence != null && _readyEvidence.IsValid;

    }
}
