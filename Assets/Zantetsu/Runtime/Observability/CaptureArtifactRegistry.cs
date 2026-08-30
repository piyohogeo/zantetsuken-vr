using System;

namespace Zantetsu.Observability
{
    /// <summary>Fixed-capacity append-only registry of staged generic artifacts.</summary>
    internal sealed class CaptureArtifactRegistry
    {
        private readonly CaptureArtifactDescriptor[] _descriptors;
        private readonly CaptureFrameWorkToken[] _tokens;
        private readonly CaptureArtifactFrameRelation[] _relations;
        private readonly long[] _reservationTestRunIds;
        private readonly long[] _reservationFrameIds;
        private readonly int[] _reservationRemaining;
        private int _count;
        private int _reservationCount;
        private int _reservedArtifactCount;

        internal CaptureArtifactRegistry(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            _descriptors = new CaptureArtifactDescriptor[capacity];
            _tokens = new CaptureFrameWorkToken[capacity];
            _relations = new CaptureArtifactFrameRelation[capacity];
            _reservationTestRunIds = new long[capacity];
            _reservationFrameIds = new long[capacity];
            _reservationRemaining = new int[capacity];
        }

        internal int Capacity => _descriptors.Length;
        internal int Count => _count;

        internal int ReservedArtifactCount => _reservedArtifactCount;

        internal bool TryReserve(long testRunId, long captureFrameId, int maximumArtifactCount)
        {
            if (testRunId <= 0) throw new ArgumentOutOfRangeException(nameof(testRunId));
            if (captureFrameId <= 0) throw new ArgumentOutOfRangeException(nameof(captureFrameId));
            if (maximumArtifactCount < 0) throw new ArgumentOutOfRangeException(nameof(maximumArtifactCount));
            if (FindReservation(testRunId, captureFrameId) >= 0)
                throw new InvalidOperationException("Frame already has an artifact reservation.");
            if (maximumArtifactCount == 0) return true;
            if (maximumArtifactCount > _descriptors.Length) return false;
            if (_count + _reservedArtifactCount + maximumArtifactCount > _descriptors.Length) return false;
            if (_reservationCount == _reservationFrameIds.Length) return false;
            _reservationTestRunIds[_reservationCount] = testRunId;
            _reservationFrameIds[_reservationCount] = captureFrameId;
            _reservationRemaining[_reservationCount] = maximumArtifactCount;
            _reservationCount++;
            _reservedArtifactCount = checked(_reservedArtifactCount + maximumArtifactCount);
            return true;
        }

        internal void CancelReservation(long testRunId, long captureFrameId)
        {
            int index = FindReservation(testRunId, captureFrameId);
            if (index < 0) return;
            _reservedArtifactCount -= _reservationRemaining[index];
            RemoveReservation(index);
        }

        internal void TrimReservation(in CaptureFrameWorkToken token, int actualArtifactCount)
        {
            if (!token.IsValid) throw new ArgumentException("Token must be valid.", nameof(token));
            int index = FindReservation(token.TestRunId, token.CaptureFrameId);
            if (index < 0 && actualArtifactCount == 0) return;
            if (actualArtifactCount < 0 || index < 0 || actualArtifactCount > _reservationRemaining[index])
                throw new ArgumentOutOfRangeException(nameof(actualArtifactCount));
            int released = _reservationRemaining[index] - actualArtifactCount;
            _reservationRemaining[index] = actualArtifactCount;
            _reservedArtifactCount -= released;
            if (actualArtifactCount == 0) RemoveReservation(index);
        }

        internal bool TryRegister(
            in CaptureFrameWorkToken token,
            CaptureArtifactDescriptor descriptor,
            CaptureArtifactFrameRelation relation)
        {
            if (!token.IsValid) throw new ArgumentException("Token must be valid.", nameof(token));
            if (descriptor == null || !descriptor.IsValid) throw new ArgumentException("Descriptor must be valid.", nameof(descriptor));
            if (relation == null || !relation.IsValid) throw new ArgumentException("Relation must be valid.", nameof(relation));
            int reservation = FindReservation(token.TestRunId, token.CaptureFrameId);
            if (reservation < 0 || _reservationRemaining[reservation] <= 0)
                throw new InvalidOperationException("Artifact has no reserved registry capacity.");
            for (int i = 0; i < _count; i++)
            {
                if (string.Equals(_descriptors[i].ArtifactId, descriptor.ArtifactId, StringComparison.Ordinal)
                    || string.Equals(_descriptors[i].StagingRelativePath, descriptor.StagingRelativePath, StringComparison.Ordinal)
                    || string.Equals(_descriptors[i].FinalRelativePath, descriptor.FinalRelativePath, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Artifact identity or path is already registered.");
                }
            }

            if (_count == _descriptors.Length) throw new InvalidOperationException("Reserved artifact capacity was lost.");
            _tokens[_count] = token;
            _descriptors[_count] = descriptor;
            _relations[_count] = relation;
            _count++;
            ConsumeReservation(reservation);
            return true;
        }

        internal void ReleaseFailedArtifact(in CaptureFrameWorkToken token)
        {
            if (!token.IsValid) throw new ArgumentException("Token must be valid.", nameof(token));
            int reservation = FindReservation(token.TestRunId, token.CaptureFrameId);
            if (reservation < 0 || _reservationRemaining[reservation] <= 0)
                throw new InvalidOperationException("Failed artifact has no reserved registry capacity.");
            ConsumeReservation(reservation);
        }

        internal CaptureArtifactDescriptor GetDescriptor(int index)
        {
            if (index < 0 || index >= _count) throw new ArgumentOutOfRangeException(nameof(index));
            return _descriptors[index];
        }

        internal CaptureFrameWorkToken GetWorkToken(int index)
        {
            if (index < 0 || index >= _count) throw new ArgumentOutOfRangeException(nameof(index));
            return _tokens[index];
        }

        internal CaptureArtifactFrameRelation GetFrameRelation(int index)
        {
            if (index < 0 || index >= _count) throw new ArgumentOutOfRangeException(nameof(index));
            return _relations[index];
        }

        internal int CountForFrame(long captureFrameId)
        {
            if (captureFrameId <= 0) throw new ArgumentOutOfRangeException(nameof(captureFrameId));
            int count = 0;
            for (int i = 0; i < _count; i++) if (_relations[i].Contains(captureFrameId)) count++;
            return count;
        }

        private int FindReservation(long testRunId, long captureFrameId)
        {
            for (int i = 0; i < _reservationCount; i++)
                if (_reservationTestRunIds[i] == testRunId && _reservationFrameIds[i] == captureFrameId) return i;
            return -1;
        }

        private void ConsumeReservation(int index)
        {
            _reservationRemaining[index]--;
            _reservedArtifactCount--;
            if (_reservationRemaining[index] == 0) RemoveReservation(index);
        }

        private void RemoveReservation(int index)
        {
            int last = _reservationCount - 1;
            if (index != last)
            {
                _reservationTestRunIds[index] = _reservationTestRunIds[last];
                _reservationFrameIds[index] = _reservationFrameIds[last];
                _reservationRemaining[index] = _reservationRemaining[last];
            }
            _reservationTestRunIds[last] = 0;
            _reservationFrameIds[last] = 0;
            _reservationRemaining[last] = 0;
            _reservationCount--;
        }
    }
}
