using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable value describing one fixed artifact recovery step: an action
    /// plus its entry index and artifact kind.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <see cref="CaptureRunPublicationArtifactRecoveryAction.PublishArtifact"/>
    /// step carries a non-negative entry index and a PNG or sidecar kind. Every
    /// other action is a routing step that carries entry index -1 and
    /// <see cref="CaptureRunPublicationArtifactKind.None"/>.
    /// </para>
    /// <para>
    /// <see cref="IsValid"/> and <see cref="Matches"/> recompute the held
    /// combination without throwing, so a forged step becomes invalid.
    /// </para>
    /// <para>
    /// This type performs no filesystem work and is not an
    /// <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunPublicationArtifactRecoveryStep
    {
        private readonly CaptureRunPublicationArtifactRecoveryAction _action;
        private readonly int _entryIndex;
        private readonly CaptureRunPublicationArtifactKind _artifactKind;

        internal CaptureRunPublicationArtifactRecoveryStep(
            CaptureRunPublicationArtifactRecoveryAction action,
            int entryIndex,
            CaptureRunPublicationArtifactKind artifactKind)
        {
            if (!IsDefinedAction(action))
            {
                throw new ArgumentOutOfRangeException(nameof(action), action, "Recovery action must be defined.");
            }

            if (!IsDefinedArtifactKind(artifactKind))
            {
                throw new ArgumentOutOfRangeException(nameof(artifactKind), artifactKind, "Artifact kind must be defined.");
            }

            if (action == CaptureRunPublicationArtifactRecoveryAction.PublishArtifact)
            {
                if (entryIndex < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(entryIndex), entryIndex, "Publish artifact entry index must be non-negative.");
                }

                if (artifactKind != CaptureRunPublicationArtifactKind.Png
                    && artifactKind != CaptureRunPublicationArtifactKind.Sidecar)
                {
                    throw new ArgumentException("Publish artifact must target a PNG or sidecar.", nameof(artifactKind));
                }
            }
            else
            {
                if (entryIndex != -1)
                {
                    throw new ArgumentException("Routing actions must carry entry index -1.", nameof(entryIndex));
                }

                if (artifactKind != CaptureRunPublicationArtifactKind.None)
                {
                    throw new ArgumentException("Routing actions must carry artifact kind None.", nameof(artifactKind));
                }
            }

            _action = action;
            _entryIndex = entryIndex;
            _artifactKind = artifactKind;
        }

        internal CaptureRunPublicationArtifactRecoveryAction Action => _action;

        internal int EntryIndex => _entryIndex;

        internal CaptureRunPublicationArtifactKind ArtifactKind => _artifactKind;

        internal bool IsValid
        {
            get
            {
                if (!IsDefinedAction(_action) || !IsDefinedArtifactKind(_artifactKind))
                {
                    return false;
                }

                if (_action == CaptureRunPublicationArtifactRecoveryAction.PublishArtifact)
                {
                    return _entryIndex >= 0
                        && (_artifactKind == CaptureRunPublicationArtifactKind.Png
                            || _artifactKind == CaptureRunPublicationArtifactKind.Sidecar);
                }

                return _entryIndex == -1 && _artifactKind == CaptureRunPublicationArtifactKind.None;
            }
        }

        internal bool Matches(
            CaptureRunPublicationArtifactRecoveryAction action,
            int entryIndex,
            CaptureRunPublicationArtifactKind artifactKind)
        {
            return IsValid
                && action == _action
                && entryIndex == _entryIndex
                && artifactKind == _artifactKind;
        }

        private static bool IsDefinedAction(CaptureRunPublicationArtifactRecoveryAction action)
        {
            return action == CaptureRunPublicationArtifactRecoveryAction.PublishArtifact
                || action == CaptureRunPublicationArtifactRecoveryAction.ReinspectArtifacts
                || action == CaptureRunPublicationArtifactRecoveryAction.CommitCaptureIndex
                || action == CaptureRunPublicationArtifactRecoveryAction.ContinueCaptureCompleteCleanup
                || action == CaptureRunPublicationArtifactRecoveryAction.StopOrphanedPreTrace
                || action == CaptureRunPublicationArtifactRecoveryAction.StopArtifactSourceMissing
                || action == CaptureRunPublicationArtifactRecoveryAction.StopPublishedArtifactMissing
                || action == CaptureRunPublicationArtifactRecoveryAction.StopRunRootCollision;
        }

        private static bool IsDefinedArtifactKind(CaptureRunPublicationArtifactKind kind)
        {
            return kind == CaptureRunPublicationArtifactKind.None
                || kind == CaptureRunPublicationArtifactKind.Png
                || kind == CaptureRunPublicationArtifactKind.Sidecar;
        }
    }
}
