using System;
using Unity.Collections;

namespace Zantetsu.Observability
{
    /// <summary>
    /// One-shot ownership of the obligation to release a successful dispatcher
    /// result. It does not own the pool allocation itself.
    /// </summary>
    /// <remarks>
    /// Caller ownership is transferred only by an accepted encode submission.
    /// The synchronous Phase 1 service never copies the raw buffer and the
    /// completion applier releases it on the main thread exactly once.
    /// </remarks>
    internal sealed class CaptureFrameReadbackPayloadLease
    {
        private const int CallerOwned = 0;
        private const int ServiceOwned = 1;
        private const int CompletionOwned = 2;
        private const int ReleaseAttempted = 3;

        private readonly UnityRenderTextureReadbackDispatcher _dispatcher;
        private readonly CaptureFrameReadbackResult _result;
        private int _ownershipState;
        private Guid _serviceOwner;
        private CaptureFrameWorkToken _workToken;
        private bool _releaseSucceeded;

        internal CaptureFrameRequest FrameRequest => _result.FrameRequest;

        internal bool IsCallerOwned => _ownershipState == CallerOwned;

        internal bool IsReleaseAttempted => _ownershipState == ReleaseAttempted;

        internal bool ReleaseSucceeded => _releaseSucceeded;

        internal CaptureFrameReadbackPayloadLease(
            UnityRenderTextureReadbackDispatcher dispatcher,
            in CaptureFrameReadbackResult result)
        {
            if (dispatcher == null)
            {
                throw new ArgumentNullException(nameof(dispatcher));
            }

            if (!result.IsValid)
            {
                throw new ArgumentException("Readback result must be valid.", nameof(result));
            }

            if (result.HasError)
            {
                throw new ArgumentException("Errored readbacks cannot be encoded.", nameof(result));
            }

            _dispatcher = dispatcher;
            _result = result;
            _ownershipState = CallerOwned;
            _serviceOwner = Guid.Empty;
            _workToken = default;
            _releaseSucceeded = false;
        }

        internal void TransferToService(Guid serviceOwner, in CaptureFrameWorkToken workToken)
        {
            if (_ownershipState != CallerOwned)
            {
                throw new InvalidOperationException("The readback payload is not caller-owned.");
            }

            if (serviceOwner == Guid.Empty || !workToken.IsValid || workToken.OwnerToken != serviceOwner)
            {
                throw new ArgumentException("Work token must belong to the accepting service.", nameof(workToken));
            }

            _serviceOwner = serviceOwner;
            _workToken = workToken;
            _ownershipState = ServiceOwned;
        }

        internal NativeArray<byte> GetBufferForService(Guid serviceOwner, in CaptureFrameWorkToken workToken)
        {
            ValidateServiceOwnership(serviceOwner, workToken, ServiceOwned);
            return _dispatcher.GetBuffer(_result);
        }

        internal void TransferToCompletion(Guid serviceOwner, in CaptureFrameWorkToken workToken)
        {
            ValidateServiceOwnership(serviceOwner, workToken, ServiceOwned);
            _ownershipState = CompletionOwned;
        }

        internal void ReleaseFromCompletion(in CaptureFrameWorkToken workToken)
        {
            if (_ownershipState != CompletionOwned || !_workToken.IdenticalTo(workToken))
            {
                throw new InvalidOperationException("The completion does not own this readback payload.");
            }

            ReleaseOnce();
        }

        internal void ReleaseByCaller()
        {
            if (_ownershipState != CallerOwned)
            {
                throw new InvalidOperationException("The caller does not own this readback payload.");
            }

            ReleaseOnce();
        }

        private void ReleaseOnce()
        {
            // Mark before calling the dispatcher. A throwing Release must never
            // be guessed safe to retry.
            _ownershipState = ReleaseAttempted;
            _dispatcher.Release(_result);
            _releaseSucceeded = true;
        }

        private void ValidateServiceOwnership(
            Guid serviceOwner,
            in CaptureFrameWorkToken workToken,
            int expectedState)
        {
            if (_ownershipState != expectedState ||
                _serviceOwner != serviceOwner ||
                !_workToken.IdenticalTo(workToken))
            {
                throw new InvalidOperationException("The encode service does not own this readback payload.");
            }
        }
    }
}
