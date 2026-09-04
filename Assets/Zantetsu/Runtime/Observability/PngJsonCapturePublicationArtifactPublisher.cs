using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Stateless, synchronous concrete publisher that durably publishes the
    /// staged PngJson artifacts of one execution batch to their final paths
    /// under one execution-wide reservation held by the concrete attempt. It
    /// is a thin adapter over a single <see cref="CaptureArtifactFileStore"/>
    /// and never overwrites an existing destination.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The publisher body holds exactly one immutable store reference and no
    /// active attempt collection, reservation mapping, or mutable execution
    /// state. The concrete attempt holds the exact publisher, execution batch,
    /// action plan token, underlying reservation, and its ended state privately,
    /// and is never registered back onto the publisher.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing, holds no lease directly,
    /// and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject. Calls are synchronous and call-scoped; no concurrency
    /// contract is added.
    /// </para>
    /// </remarks>
    internal sealed class PngJsonCapturePublicationArtifactPublisher : IPngJsonCapturePublicationArtifactPublisher
    {
        private readonly CaptureArtifactFileStore _store;

        internal PngJsonCapturePublicationArtifactPublisher(CaptureArtifactFileStore store)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
        }

        public IPngJsonCapturePublicationArtifactPublishAttempt TryBegin(
            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            if (batch == null)
            {
                throw new ArgumentNullException(nameof(batch));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            // Confirm the exact batch/token correlation and the current batch
            // validity before any side effect. The token is used as-is; it is
            // never re-issued and the whole plan is never re-validated.
            if (!batch.IsValidWithToken(token))
            {
                throw new ArgumentException("Batch must be valid for the supplied token.", nameof(batch));
            }

            CaptureArtifactPublishReservation reservation = _store.TryReservePublish();
            if (reservation == null)
            {
                return null;
            }

            return new Attempt(this, batch, token, reservation);
        }

        public PngJsonCapturePublicationArtifactPublishReceipt PublishReserved(
            IPngJsonCapturePublicationArtifactPublishAttempt attempt,
            PngJsonCapturePublicationArtifactPublishOperation operation,
            PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token)
        {
            if (attempt == null)
            {
                throw new ArgumentNullException(nameof(attempt));
            }

            if (operation == null)
            {
                throw new ArgumentNullException(nameof(operation));
            }

            if (token == null)
            {
                throw new ArgumentNullException(nameof(token));
            }

            Attempt concrete = attempt as Attempt;
            if (concrete == null || !ReferenceEquals(concrete.Publisher, this))
            {
                throw new ArgumentException("Attempt must have been issued by this publisher.", nameof(attempt));
            }

            if (concrete.Ended)
            {
                throw new InvalidOperationException("Attempt is already ended.");
            }

            if (!ReferenceEquals(concrete.Token, token))
            {
                throw new ArgumentException("Token must match the attempt's issuance token.", nameof(token));
            }

            PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch = concrete.Batch;
            if (!ReferenceEquals(batch.ActionPlan, operation.ActionPlan))
            {
                throw new ArgumentException("Operation must belong to the attempt's action plan.", nameof(operation));
            }

            if (!operation.IsValidIndexLocal(token))
            {
                throw new ArgumentException("Operation must be index-locally valid for the supplied token.", nameof(operation));
            }

            CaptureArtifactDescriptor descriptor = BuildDescriptor(operation);

            // Publish exactly once under the attempt's single reservation. A
            // store exception propagates unchanged: no retry, rollback,
            // re-inspection, or additional reservation is attempted.
            CaptureArtifactPublishReceipt genericReceipt = _store.PublishReserved(descriptor, concrete.Reservation);
            if (genericReceipt == null || !genericReceipt.IsIssuedFor(_store, descriptor))
            {
                throw new InvalidOperationException("Store returned an invalid publication receipt.");
            }

            return PngJsonCapturePublicationArtifactPublishReceipt.Create(this, operation, token);
        }

        public void End(IPngJsonCapturePublicationArtifactPublishAttempt attempt)
        {
            if (attempt == null)
            {
                throw new ArgumentNullException(nameof(attempt));
            }

            Attempt concrete = attempt as Attempt;
            if (concrete == null || !ReferenceEquals(concrete.Publisher, this))
            {
                throw new ArgumentException("Attempt must have been issued by this publisher.", nameof(attempt));
            }

            concrete.End();
        }

        private static CaptureArtifactDescriptor BuildDescriptor(
            PngJsonCapturePublicationArtifactPublishOperation operation)
        {
            string id = operation.CaptureFrameId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (operation.ArtifactKind == CaptureRunPublicationArtifactKind.Png)
            {
                return new CaptureArtifactDescriptor(
                    "frame/" + id + "/image",
                    CaptureArtifactKind.FrameImage,
                    "image/png",
                    1,
                    operation.Entry.PngStagingRelativePath,
                    operation.Entry.PngFinalRelativePath,
                    operation.ExpectedByteCount,
                    operation.ExpectedContentSha256);
            }

            if (operation.ArtifactKind == CaptureRunPublicationArtifactKind.Sidecar)
            {
                return new CaptureArtifactDescriptor(
                    "frame/" + id + "/metadata",
                    CaptureArtifactKind.FrameMetadata,
                    "application/vnd.zantetsu.capture-frame+json",
                    2,
                    operation.Entry.SidecarStagingRelativePath,
                    operation.Entry.SidecarFinalRelativePath,
                    operation.ExpectedByteCount,
                    operation.ExpectedContentSha256);
            }

            throw new ArgumentException("Operation artifact kind must be Png or Sidecar.", nameof(operation));
        }

        /// <summary>
        /// Private, call-scoped reservation attempt. It holds only the exact
        /// publisher, execution batch, action plan token, underlying
        /// reservation, and its ended state; no reservation, buffer, lease, or
        /// token is exposed, and nothing is added to the public attempt
        /// interface.
        /// </summary>
        private sealed class Attempt : IPngJsonCapturePublicationArtifactPublishAttempt
        {
            private readonly PngJsonCapturePublicationArtifactPublisher _publisher;
            private readonly PngJsonCapturePublicationArtifactRecoveryExecutionBatch _batch;
            private readonly PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken _token;
            private readonly CaptureArtifactPublishReservation _reservation;
            private bool _ended;

            internal Attempt(
                PngJsonCapturePublicationArtifactPublisher publisher,
                PngJsonCapturePublicationArtifactRecoveryExecutionBatch batch,
                PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken token,
                CaptureArtifactPublishReservation reservation)
            {
                _publisher = publisher;
                _batch = batch;
                _token = token;
                _reservation = reservation;
            }

            internal PngJsonCapturePublicationArtifactPublisher Publisher => _publisher;

            internal PngJsonCapturePublicationArtifactRecoveryExecutionBatch Batch => _batch;

            internal PngJsonCapturePublicationArtifactRecoveryActionPlan.ValidationToken Token => _token;

            internal CaptureArtifactPublishReservation Reservation => _reservation;

            internal bool Ended => _ended;

            internal void End()
            {
                if (_ended)
                {
                    throw new InvalidOperationException("Attempt is already ended.");
                }

                _publisher._store.ReleasePublishReservation(_reservation);
                _ended = true;
            }
        }
    }
}
