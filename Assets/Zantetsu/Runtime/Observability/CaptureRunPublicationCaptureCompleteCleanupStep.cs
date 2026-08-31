using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, side-effect-free cleanup step of a Capture Run publication
    /// capture-complete cleanup plan: one action plus its entry index and
    /// artifact kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The value contract is fixed: a <c>DeleteStagingArtifact</c> step uses a
    /// non-negative entry index and <see cref="CaptureRunPublicationArtifactKind.Png"/>
    /// or <see cref="CaptureRunPublicationArtifactKind.Sidecar"/>; every other
    /// defined action uses entry index <c>-1</c> and
    /// <see cref="CaptureRunPublicationArtifactKind.None"/>. The upper entry
    /// bound is enforced by the cleanup plan, which is the only producer of
    /// steps. <see cref="CaptureRunPublicationCaptureCompleteCleanupAction.None"/>,
    /// undefined actions, and contradictory combinations are rejected.
    /// </para>
    /// <para>
    /// This type owns, mutates, and disposes nothing, exposes no public
    /// constructor, and is not an <see cref="IDisposable"/>, MonoBehaviour, or
    /// ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationCaptureCompleteCleanupStep
    {
        private readonly CaptureRunPublicationCaptureCompleteCleanupAction _action;
        private readonly int _entryIndex;
        private readonly CaptureRunPublicationArtifactKind _artifactKind;

        internal CaptureRunPublicationCaptureCompleteCleanupStep(
            CaptureRunPublicationCaptureCompleteCleanupAction action,
            int entryIndex,
            CaptureRunPublicationArtifactKind artifactKind)
        {
            if (!IsDefinedAction(action) || action == CaptureRunPublicationCaptureCompleteCleanupAction.None)
            {
                throw new ArgumentOutOfRangeException(nameof(action), action, "Action must be a defined cleanup action.");
            }

            if (action == CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact)
            {
                if (entryIndex < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(entryIndex), entryIndex, "Staging artifact step entry index must be non-negative.");
                }

                if (artifactKind != CaptureRunPublicationArtifactKind.Png
                    && artifactKind != CaptureRunPublicationArtifactKind.Sidecar)
                {
                    throw new ArgumentOutOfRangeException(nameof(artifactKind), artifactKind, "Staging artifact step artifact kind must be Png or Sidecar.");
                }
            }
            else
            {
                if (entryIndex != -1)
                {
                    throw new ArgumentOutOfRangeException(nameof(entryIndex), entryIndex, "Non-staging-artifact step entry index must be -1.");
                }

                if (artifactKind != CaptureRunPublicationArtifactKind.None)
                {
                    throw new ArgumentOutOfRangeException(nameof(artifactKind), artifactKind, "Non-staging-artifact step artifact kind must be None.");
                }
            }

            _action = action;
            _entryIndex = entryIndex;
            _artifactKind = artifactKind;
        }

        internal CaptureRunPublicationCaptureCompleteCleanupAction Action => _action;

        internal int EntryIndex => _entryIndex;

        internal CaptureRunPublicationArtifactKind ArtifactKind => _artifactKind;

        /// <summary>
        /// Recomputes the held action/entry-index/artifact-kind combination
        /// without throwing.
        /// </summary>
        internal bool IsValid
        {
            get
            {
                if (!IsDefinedAction(_action) || _action == CaptureRunPublicationCaptureCompleteCleanupAction.None)
                {
                    return false;
                }

                if (_action == CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact)
                {
                    return _entryIndex >= 0
                        && (_artifactKind == CaptureRunPublicationArtifactKind.Png
                            || _artifactKind == CaptureRunPublicationArtifactKind.Sidecar);
                }

                return _entryIndex == -1 && _artifactKind == CaptureRunPublicationArtifactKind.None;
            }
        }

        /// <summary>
        /// Full value comparison of the held action, entry index, and artifact
        /// kind against the supplied values.
        /// </summary>
        internal bool Matches(
            CaptureRunPublicationCaptureCompleteCleanupAction action,
            int entryIndex,
            CaptureRunPublicationArtifactKind artifactKind)
        {
            return _action == action
                && _entryIndex == entryIndex
                && _artifactKind == artifactKind;
        }

        private static bool IsDefinedAction(CaptureRunPublicationCaptureCompleteCleanupAction action)
        {
            return action == CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlanTemporary
                || action == CaptureRunPublicationCaptureCompleteCleanupAction.DeleteCaptureIndexTemporary
                || action == CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingArtifact
                || action == CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingFramesRoot
                || action == CaptureRunPublicationCaptureCompleteCleanupAction.DeletePublicationPlan
                || action == CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingReadyMarker
                || action == CaptureRunPublicationCaptureCompleteCleanupAction.DeleteStagingInitializationMarker
                || action == CaptureRunPublicationCaptureCompleteCleanupAction.RemoveStagingRunRoot
                || action == CaptureRunPublicationCaptureCompleteCleanupAction.CaptureCompleteReady;
        }
    }
}
