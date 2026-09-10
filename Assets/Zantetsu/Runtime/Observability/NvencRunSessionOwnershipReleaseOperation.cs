using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, non-owning NVENC Run Session Ownership Lease release
    /// operation: the exact Run Coordinator, the exact reflected CaptureComplete
    /// cleanup attempt result, and the exact Session Ownership Lease that Run
    /// holds, fixed together for the future release boundary. The cleanup
    /// operation, the root layout, and the run identity are forwarded from the
    /// held result's graph and are never duplicated as fields.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A Failed cleanup does not suppress the lock release: both terminal
    /// cleanup shapes prepare a release, and which of them occurred stays
    /// readable through <see cref="CleanupResult"/>.
    /// </para>
    /// <para>
    /// This type releases nothing. It performs no file, hash, or serialization
    /// operation, owns no thread, wait primitive, filesystem, Registry, or
    /// Service, never advances the registry, the disposition, or the evidence
    /// state, and is not an <see cref="IDisposable"/>. It introduces no proof,
    /// token, nonce, generation, or authority value: every correlation is
    /// <see cref="object.ReferenceEquals"/> against the Run Coordinator's own
    /// retained cleanup graph, so no separate provenance mechanism is needed.
    /// </para>
    /// <para>
    /// The three predicates are deliberately distinct.
    /// <see cref="IsBindingIntact"/> is pure reference correlation and says
    /// nothing about the lease's release state, so a partially released lease
    /// does not break it and a later release attempt stays possible.
    /// <see cref="CanRelease"/> adds the lease's own current releasability.
    /// <see cref="IsValid"/> is admission only: it additionally requires an
    /// unpoisoned process, the disposition the reflected cleanup implies, and a
    /// lease that is still fully retained. A post-release check must therefore
    /// rest on <see cref="IsBindingIntact"/>, never on
    /// <see cref="IsValid"/>.
    /// </para>
    /// <para>
    /// The forwarding surface is intentionally narrow: only what a later
    /// release needs. The plan, the descriptor, the content hashes, and the
    /// resolved paths are reachable through the forwarded graph and are not
    /// duplicated as properties here.
    /// </para>
    /// </remarks>
    internal sealed class NvencRunSessionOwnershipReleaseOperation
    {
        private readonly NvencCaptureRunCoordinator _coordinator;
        private readonly NvencRunCaptureCompleteCleanupAttemptResult _cleanupResult;
        private readonly CaptureRunInitializationSessionOwnershipLease _ownershipLease;

        internal NvencRunSessionOwnershipReleaseOperation(
            NvencCaptureRunCoordinator coordinator,
            NvencRunCaptureCompleteCleanupAttemptResult cleanupResult,
            CaptureRunInitializationSessionOwnershipLease ownershipLease)
        {
            if (coordinator == null)
            {
                throw new ArgumentNullException(nameof(coordinator));
            }

            if (ownershipLease == null)
            {
                throw new ArgumentNullException(nameof(ownershipLease));
            }

            if (cleanupResult.IsNone)
            {
                throw new ArgumentException(
                    "The cleanup attempt result must be a terminal shape.", nameof(cleanupResult));
            }

            _coordinator = coordinator;
            _cleanupResult = cleanupResult;
            _ownershipLease = ownershipLease;
        }

        internal NvencRunCaptureCompleteCleanupAttemptResult CleanupResult => _cleanupResult;

        internal NvencRunCaptureCompleteCleanupOperation CleanupOperation => _cleanupResult.Operation;

        internal CaptureRunInitializationSessionOwnershipLease OwnershipLease => _ownershipLease;

        internal CaptureRunRootLayout RootLayout => _cleanupResult.RootLayout;

        internal long TestRunId => _cleanupResult.TestRunId;

        internal string RunInitializationId => _cleanupResult.RunInitializationId;

        /// <summary>
        /// Pure reference correlation against the Run Coordinator's retained
        /// reflected cleanup result and its exact Session Ownership Lease. It
        /// reads no lease release state and no process state, so neither a
        /// partial release, a completed release, nor a Poison revokes it.
        /// </summary>
        internal bool IsBindingIntact =>
            _coordinator != null
            && _ownershipLease != null
            && _coordinator.IsSessionOwnershipReleaseBindingIntact(_cleanupResult, _ownershipLease);

        /// <summary>
        /// The binding still holds and the exact lease's own disposal has not
        /// completed, so a first attempt or a retry after a partial failure is
        /// still possible.
        /// </summary>
        internal bool CanRelease =>
            IsBindingIntact
            && _ownershipLease.CanRelease;

        /// <summary>
        /// Admission validity, checked before a release is prepared or started:
        /// the binding holds, the process is not poisoned, the disposition is
        /// the one the reflected cleanup implies, the Publication Service is
        /// released and stopped, and the lease is still fully retained and
        /// releasable.
        /// </summary>
        internal bool IsValid =>
            _coordinator != null
            && _ownershipLease != null
            && _coordinator.IsSessionOwnershipReleaseCorrelated(_cleanupResult, _ownershipLease);

        internal bool IsIssuedFor(NvencCaptureRunCoordinator coordinator)
        {
            return coordinator != null
                && ReferenceEquals(_coordinator, coordinator)
                && IsValid;
        }
    }
}
