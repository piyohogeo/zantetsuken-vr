using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, filesystem-free Capture Run initialization write batch: the
    /// four marker write operations of a Run's document set, built in a fixed
    /// order for the atomic writer. It owns the operations it built and keeps
    /// the document set it was built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Construction reads the document set's defensive copies in order and
    /// passes each, together with the corresponding marker path set entries, to
    /// a single write operation constructor. The fixed order is staging
    /// initialization, final initialization, staging ready, final ready. The
    /// document set and the four operations are held only after every
    /// operation is built.
    /// </para>
    /// <para>
    /// The document set keeps owning its internal arrays, which are never
    /// mutated. Each getter copy is owned by this batch only until its
    /// operation takes ownership, which the operation signals by clearing the
    /// caller variable. The two ready operations receive separate copies and
    /// never share an array. No dispose contract is introduced for the managed
    /// arrays.
    /// </para>
    /// <para>
    /// This type performs no path construction, no canonical serialization,
    /// no decode, no hash computation, no ID generation, no marker, binding,
    /// or plan factory call, no file, directory, or stream access, no write,
    /// flush, or rename, no directory creation, no OS locking, no retry,
    /// recovery, or collision classification, and no logger, registry, draft,
    /// clock, random, or Unity static API access. It is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationWriteBatch
    {
        private readonly CaptureRunInitializationDocumentSet _documents;
        private readonly CaptureRunMarkerWriteOperation _stagingInitialization;
        private readonly CaptureRunMarkerWriteOperation _finalInitialization;
        private readonly CaptureRunMarkerWriteOperation _stagingReady;
        private readonly CaptureRunMarkerWriteOperation _finalReady;

        internal CaptureRunInitializationWriteBatch(CaptureRunInitializationDocumentSet documents)
        {
            if (documents == null)
            {
                throw new ArgumentNullException(nameof(documents));
            }

            CaptureRunInitializationPlan plan = documents.Plan;
            if (plan == null)
            {
                throw new ArgumentException("Documents must hold an initialization plan.", nameof(documents));
            }

            CaptureRunMarkerPathSet markerPaths = plan.MarkerPaths;
            if (markerPaths == null)
            {
                throw new ArgumentException("Plan must hold a marker path set.", nameof(documents));
            }

            byte[] stagingInitializationBytes = documents.GetStagingInitializationBytes();
            CaptureRunMarkerWriteOperation stagingInitialization = new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Staging,
                CaptureRunMarkerKind.Initialization,
                markerPaths.StagingInitializationTemporaryPath,
                markerPaths.StagingInitializationPath,
                ref stagingInitializationBytes);
            RequireOwnershipTransfer(stagingInitializationBytes, "staging initialization");

            byte[] finalInitializationBytes = documents.GetFinalInitializationBytes();
            CaptureRunMarkerWriteOperation finalInitialization = new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Final,
                CaptureRunMarkerKind.Initialization,
                markerPaths.FinalInitializationTemporaryPath,
                markerPaths.FinalInitializationPath,
                ref finalInitializationBytes);
            RequireOwnershipTransfer(finalInitializationBytes, "final initialization");

            byte[] stagingReadyBytes = documents.GetStagingReadyBytes();
            CaptureRunMarkerWriteOperation stagingReady = new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Staging,
                CaptureRunMarkerKind.Ready,
                markerPaths.StagingReadyTemporaryPath,
                markerPaths.StagingReadyPath,
                ref stagingReadyBytes);
            RequireOwnershipTransfer(stagingReadyBytes, "staging ready");

            byte[] finalReadyBytes = documents.GetFinalReadyBytes();
            CaptureRunMarkerWriteOperation finalReady = new CaptureRunMarkerWriteOperation(
                CaptureRunRootRole.Final,
                CaptureRunMarkerKind.Ready,
                markerPaths.FinalReadyTemporaryPath,
                markerPaths.FinalReadyPath,
                ref finalReadyBytes);
            RequireOwnershipTransfer(finalReadyBytes, "final ready");

            _documents = documents;
            _stagingInitialization = stagingInitialization;
            _finalInitialization = finalInitialization;
            _stagingReady = stagingReady;
            _finalReady = finalReady;
        }

        internal CaptureRunInitializationDocumentSet Documents => _documents;

        internal int Count => 4;

        internal CaptureRunMarkerWriteOperation StagingInitialization => _stagingInitialization;

        internal CaptureRunMarkerWriteOperation FinalInitialization => _finalInitialization;

        internal CaptureRunMarkerWriteOperation StagingReady => _stagingReady;

        internal CaptureRunMarkerWriteOperation FinalReady => _finalReady;

        internal CaptureRunMarkerWriteOperation GetOperation(int index)
        {
            switch (index)
            {
                case 0:
                    return _stagingInitialization;
                case 1:
                    return _finalInitialization;
                case 2:
                    return _stagingReady;
                case 3:
                    return _finalReady;
                default:
                    throw new ArgumentOutOfRangeException(nameof(index), index, "Index must be between 0 and 3.");
            }
        }

        private static void RequireOwnershipTransfer(byte[] canonicalBytes, string operationName)
        {
            if (canonicalBytes != null)
            {
                throw new InvalidOperationException(
                    "The write operation did not take ownership of the canonical bytes: " + operationName + ".");
            }
        }
    }
}
