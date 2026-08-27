using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Coordinates capture artifact persistence by deriving the deterministic
    /// destination for the FIFO head from its own request and delegating to the
    /// type-safe persistence pump. The caller supplies no path strings or
    /// destinations.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The request used to generate the destination is the artifact queue head's
    /// <c>FrameRecord.Request</c> when that queue is non-empty, otherwise the PNG
    /// queue head's request. The artifact queue always takes priority. The same
    /// request therefore yields the same PNG and sidecar path pair on a retry.
    /// </para>
    /// <para>
    /// Main-thread only and <b>not</b> thread-safe. Under that contract the PNG
    /// queue, the artifact queue, and the registry are not modified externally
    /// while this method runs.
    /// </para>
    /// <para>
    /// Owns and disposes nothing: not the persistence pump, the destination
    /// factory, the PNG queue, the artifact queue, the registry, any request,
    /// artifact, record, receipt, or PNG/sidecar file. It performs no Dispose,
    /// Clear, trace recording, logging, directory creation, or file deletion,
    /// and does not implement <see cref="IDisposable"/>.
    /// </para>
    /// </remarks>
    public sealed class CaptureFramePngArtifactPersistenceCoordinator
    {
        private readonly CaptureFramePngArtifactPersistencePump _persistencePump;
        private readonly CaptureFramePngArtifactDestinationFactory _destinationFactory;

        public CaptureFramePngArtifactPersistenceCoordinator(
            CaptureFramePngArtifactPersistencePump persistencePump,
            CaptureFramePngArtifactDestinationFactory destinationFactory)
        {
            if (persistencePump == null)
            {
                throw new ArgumentNullException(nameof(persistencePump));
            }

            if (destinationFactory == null)
            {
                throw new ArgumentNullException(nameof(destinationFactory));
            }

            _persistencePump = persistencePump;
            _destinationFactory = destinationFactory;
        }

        /// <summary>
        /// Advances persistence by one step, generating the deterministic
        /// destination from the FIFO head request.
        /// </summary>
        public CaptureFramePngArtifactPersistenceStatus TryAdvanceNext(
            CaptureFramePngQueue pngQueue,
            CaptureFramePngArtifactQueue artifactQueue,
            out CaptureFramePngArtifact completedArtifact,
            out CaptureFramePngArtifactSaveReceipt sidecarReceipt)
        {
            completedArtifact = null;
            sidecarReceipt = null;

            if (pngQueue == null)
            {
                throw new ArgumentNullException(nameof(pngQueue));
            }

            if (artifactQueue == null)
            {
                throw new ArgumentNullException(nameof(artifactQueue));
            }

            if (!pngQueue.IsCreated)
            {
                throw new ObjectDisposedException(nameof(CaptureFramePngQueue));
            }

            CaptureFramePngArtifactDestination destination;
            if (artifactQueue.Count > 0)
            {
                if (!artifactQueue.TryPeek(out CaptureFramePngArtifact queued))
                {
                    throw new InvalidOperationException("The artifact queue head could not be peeked.");
                }

                destination = _destinationFactory.Create(queued.FrameRecord.Request);
            }
            else if (pngQueue.Count == 0)
            {
                return _persistencePump.TryAdvanceNext(
                    pngQueue,
                    artifactQueue,
                    null,
                    out completedArtifact,
                    out sidecarReceipt);
            }
            else
            {
                if (!pngQueue.TryPeek(out CaptureFrameRequest peekedRequest, out _))
                {
                    throw new InvalidOperationException("The PNG queue head could not be peeked.");
                }

                destination = _destinationFactory.Create(peekedRequest);
            }

            return _persistencePump.TryAdvanceNext(
                pngQueue,
                artifactQueue,
                destination,
                out completedArtifact,
                out sidecarReceipt);
        }
    }
}
