using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable snapshot of one Capture Run root's observed state, taken under
    /// the held lock. It records only observed facts; the recovery layer, not
    /// this type, later classifies collisions and resumes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A canonical observation holds its marker; an absent or invalid
    /// observation holds no marker and no raw bytes or exception for a broken
    /// marker. A missing root holds no content at all. Canonical markers are
    /// kept as facts even when their run ID, role, or hashes differ from
    /// expectations, so the recovery layer can classify the mismatch. Payload,
    /// unknown, and limit-exceeded observations are never normalized away.
    /// </para>
    /// <para>
    /// This type holds no filesystem path, byte array, handle, or stream, and
    /// is not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRootObservation
    {
        private readonly CaptureRunRootRole _rootRole;
        private readonly bool _rootExists;
        private readonly bool _hasInitializationTemporary;
        private readonly CaptureRunMarkerObservationStatus _initializationStatus;
        private readonly CaptureRunInitializationMarker _initializationMarker;
        private readonly bool _hasReadyTemporary;
        private readonly CaptureRunMarkerObservationStatus _readyStatus;
        private readonly CaptureRunReadyMarker _readyMarker;
        private readonly bool _hasNonMarkerEntries;
        private readonly bool _hasUnknownEntries;
        private readonly bool _rootEntryLimitExceeded;

        internal CaptureRunInitializationRootObservation(
            CaptureRunRootRole rootRole,
            bool rootExists,
            bool hasInitializationTemporary,
            CaptureRunMarkerObservationStatus initializationStatus,
            CaptureRunInitializationMarker initializationMarker,
            bool hasReadyTemporary,
            CaptureRunMarkerObservationStatus readyStatus,
            CaptureRunReadyMarker readyMarker,
            bool hasNonMarkerEntries,
            bool hasUnknownEntries,
            bool rootEntryLimitExceeded)
        {
            if (rootRole != CaptureRunRootRole.Staging && rootRole != CaptureRunRootRole.Final)
            {
                throw new ArgumentOutOfRangeException(nameof(rootRole), rootRole, "Root role must be Staging or Final.");
            }

            RequireDefinedStatus(initializationStatus, nameof(initializationStatus));
            RequireDefinedStatus(readyStatus, nameof(readyStatus));

            if (!rootExists)
            {
                if (hasInitializationTemporary)
                {
                    throw new ArgumentException("A missing root must not have an initialization temporary entry.", nameof(hasInitializationTemporary));
                }

                if (hasReadyTemporary)
                {
                    throw new ArgumentException("A missing root must not have a ready temporary entry.", nameof(hasReadyTemporary));
                }

                if (hasNonMarkerEntries)
                {
                    throw new ArgumentException("A missing root must not have non-marker entries.", nameof(hasNonMarkerEntries));
                }

                if (hasUnknownEntries)
                {
                    throw new ArgumentException("A missing root must not have unknown entries.", nameof(hasUnknownEntries));
                }

                if (rootEntryLimitExceeded)
                {
                    throw new ArgumentException("A missing root must not exceed the entry limit.", nameof(rootEntryLimitExceeded));
                }

                if (initializationStatus != CaptureRunMarkerObservationStatus.Absent)
                {
                    throw new ArgumentException("A missing root must have an absent initialization marker.", nameof(initializationStatus));
                }

                if (readyStatus != CaptureRunMarkerObservationStatus.Absent)
                {
                    throw new ArgumentException("A missing root must have an absent ready marker.", nameof(readyStatus));
                }

                if (initializationMarker != null)
                {
                    throw new ArgumentException("A missing root must not hold an initialization marker.", nameof(initializationMarker));
                }

                if (readyMarker != null)
                {
                    throw new ArgumentException("A missing root must not hold a ready marker.", nameof(readyMarker));
                }
            }

            if (initializationStatus == CaptureRunMarkerObservationStatus.Canonical)
            {
                if (initializationMarker == null)
                {
                    throw new ArgumentException("A canonical initialization observation must hold a marker.", nameof(initializationMarker));
                }
            }
            else if (initializationMarker != null)
            {
                throw new ArgumentException("An absent or invalid initialization observation must not hold a marker.", nameof(initializationMarker));
            }

            if (readyStatus == CaptureRunMarkerObservationStatus.Canonical)
            {
                if (readyMarker == null)
                {
                    throw new ArgumentException("A canonical ready observation must hold a marker.", nameof(readyMarker));
                }
            }
            else if (readyMarker != null)
            {
                throw new ArgumentException("An absent or invalid ready observation must not hold a marker.", nameof(readyMarker));
            }

            _rootRole = rootRole;
            _rootExists = rootExists;
            _hasInitializationTemporary = hasInitializationTemporary;
            _initializationStatus = initializationStatus;
            _initializationMarker = initializationMarker;
            _hasReadyTemporary = hasReadyTemporary;
            _readyStatus = readyStatus;
            _readyMarker = readyMarker;
            _hasNonMarkerEntries = hasNonMarkerEntries;
            _hasUnknownEntries = hasUnknownEntries;
            _rootEntryLimitExceeded = rootEntryLimitExceeded;
        }

        internal CaptureRunRootRole RootRole => _rootRole;

        internal bool RootExists => _rootExists;

        internal bool HasInitializationTemporary => _hasInitializationTemporary;

        internal CaptureRunMarkerObservationStatus InitializationStatus => _initializationStatus;

        internal CaptureRunInitializationMarker InitializationMarker => _initializationMarker;

        internal bool HasReadyTemporary => _hasReadyTemporary;

        internal CaptureRunMarkerObservationStatus ReadyStatus => _readyStatus;

        internal CaptureRunReadyMarker ReadyMarker => _readyMarker;

        internal bool HasNonMarkerEntries => _hasNonMarkerEntries;

        internal bool HasUnknownEntries => _hasUnknownEntries;

        internal bool RootEntryLimitExceeded => _rootEntryLimitExceeded;

        internal bool IsValid
        {
            get
            {
                if (_rootRole != CaptureRunRootRole.Staging && _rootRole != CaptureRunRootRole.Final)
                {
                    return false;
                }

                if (!IsDefinedStatus(_initializationStatus) || !IsDefinedStatus(_readyStatus))
                {
                    return false;
                }

                if (!_rootExists)
                {
                    return !_hasInitializationTemporary
                        && !_hasReadyTemporary
                        && !_hasNonMarkerEntries
                        && !_hasUnknownEntries
                        && !_rootEntryLimitExceeded
                        && _initializationStatus == CaptureRunMarkerObservationStatus.Absent
                        && _readyStatus == CaptureRunMarkerObservationStatus.Absent
                        && _initializationMarker == null
                        && _readyMarker == null;
                }

                if (_initializationStatus == CaptureRunMarkerObservationStatus.Canonical)
                {
                    if (_initializationMarker == null)
                    {
                        return false;
                    }
                }
                else if (_initializationMarker != null)
                {
                    return false;
                }

                if (_readyStatus == CaptureRunMarkerObservationStatus.Canonical)
                {
                    if (_readyMarker == null)
                    {
                        return false;
                    }
                }
                else if (_readyMarker != null)
                {
                    return false;
                }

                return true;
            }
        }

        private static bool IsDefinedStatus(CaptureRunMarkerObservationStatus status)
        {
            return status == CaptureRunMarkerObservationStatus.Absent
                || status == CaptureRunMarkerObservationStatus.Canonical
                || status == CaptureRunMarkerObservationStatus.Invalid;
        }

        private static void RequireDefinedStatus(CaptureRunMarkerObservationStatus status, string paramName)
        {
            if (!IsDefinedStatus(status))
            {
                throw new ArgumentOutOfRangeException(paramName, status, "Marker observation status must be Absent, Canonical, or Invalid.");
            }
        }
    }
}
