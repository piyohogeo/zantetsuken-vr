using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable single step of a recovery plan: an action and, for root- and
    /// marker-scoped actions, the root role and marker kind it targets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The action determines which root role and marker kind values are legal.
    /// Routing actions carry no root role or marker kind. This type performs no
    /// filesystem work and is not an <see cref="IDisposable"/>, MonoBehaviour,
    /// or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryStep
    {
        private readonly CaptureRunInitializationRecoveryAction _action;
        private readonly CaptureRunRootRole _rootRole;
        private readonly CaptureRunMarkerKind _markerKind;

        internal CaptureRunInitializationRecoveryStep(
            CaptureRunInitializationRecoveryAction action,
            CaptureRunRootRole rootRole,
            CaptureRunMarkerKind markerKind)
        {
            if (!IsDefinedAction(action))
            {
                throw new ArgumentOutOfRangeException(nameof(action), action, "Action must be a defined recovery action.");
            }

            switch (action)
            {
                case CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary:
                case CaptureRunInitializationRecoveryAction.WriteMarker:
                    RequireRootRole(rootRole, nameof(rootRole));
                    RequireMarkerKind(markerKind, nameof(markerKind));
                    break;

                case CaptureRunInitializationRecoveryAction.RemoveEmptyRoot:
                case CaptureRunInitializationRecoveryAction.ProvisionRoot:
                    RequireRootRole(rootRole, nameof(rootRole));
                    RequireNoMarkerKind(markerKind, nameof(markerKind));
                    break;

                default:
                    RequireNoRootRole(rootRole, nameof(rootRole));
                    RequireNoMarkerKind(markerKind, nameof(markerKind));
                    break;
            }

            _action = action;
            _rootRole = rootRole;
            _markerKind = markerKind;
        }

        internal CaptureRunInitializationRecoveryAction Action => _action;

        internal CaptureRunRootRole RootRole => _rootRole;

        internal CaptureRunMarkerKind MarkerKind => _markerKind;

        internal bool IsValid
        {
            get
            {
                if (!IsDefinedAction(_action))
                {
                    return false;
                }

                switch (_action)
                {
                    case CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary:
                    case CaptureRunInitializationRecoveryAction.WriteMarker:
                        return IsRootRole(_rootRole) && IsMarkerKind(_markerKind);

                    case CaptureRunInitializationRecoveryAction.RemoveEmptyRoot:
                    case CaptureRunInitializationRecoveryAction.ProvisionRoot:
                        return IsRootRole(_rootRole) && _markerKind == CaptureRunMarkerKind.None;

                    default:
                        return _rootRole == CaptureRunRootRole.None && _markerKind == CaptureRunMarkerKind.None;
                }
            }
        }

        internal bool Matches(CaptureRunInitializationRecoveryStep other)
        {
            if (other == null)
            {
                return false;
            }

            return _action == other._action
                && _rootRole == other._rootRole
                && _markerKind == other._markerKind;
        }

        private static bool IsDefinedAction(CaptureRunInitializationRecoveryAction action)
        {
            return action >= CaptureRunInitializationRecoveryAction.DeleteMarkerTemporary
                && action <= CaptureRunInitializationRecoveryAction.StopRunRootCollision;
        }

        private static bool IsRootRole(CaptureRunRootRole rootRole)
        {
            return rootRole == CaptureRunRootRole.Staging || rootRole == CaptureRunRootRole.Final;
        }

        private static bool IsMarkerKind(CaptureRunMarkerKind markerKind)
        {
            return markerKind == CaptureRunMarkerKind.Initialization || markerKind == CaptureRunMarkerKind.Ready;
        }

        private static void RequireRootRole(CaptureRunRootRole rootRole, string paramName)
        {
            if (!IsRootRole(rootRole))
            {
                throw new ArgumentException("Root role must be Staging or Final.", paramName);
            }
        }

        private static void RequireNoRootRole(CaptureRunRootRole rootRole, string paramName)
        {
            if (rootRole != CaptureRunRootRole.None)
            {
                throw new ArgumentException("Root role must be None.", paramName);
            }
        }

        private static void RequireMarkerKind(CaptureRunMarkerKind markerKind, string paramName)
        {
            if (!IsMarkerKind(markerKind))
            {
                throw new ArgumentException("Marker kind must be Initialization or Ready.", paramName);
            }
        }

        private static void RequireNoMarkerKind(CaptureRunMarkerKind markerKind, string paramName)
        {
            if (markerKind != CaptureRunMarkerKind.None)
            {
                throw new ArgumentException("Marker kind must be None.", paramName);
            }
        }
    }
}
