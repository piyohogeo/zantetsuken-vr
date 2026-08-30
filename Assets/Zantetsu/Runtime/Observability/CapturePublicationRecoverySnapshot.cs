using System;

namespace Zantetsu.Observability
{
    internal sealed class CapturePublicationRecoverySnapshot
    {
        private readonly CapturePublicationPlan _plan;
        private readonly CaptureArtifactRecoveryObservation[] _observations;

        internal CapturePublicationRecoverySnapshot(
            CapturePublicationPlan plan,
            CaptureArtifactRecoveryObservation[] observations)
        {
            if (plan == null || !plan.IsValid) throw new ArgumentException("Plan must be valid.", nameof(plan));
            if (observations == null) throw new ArgumentNullException(nameof(observations));
            if (observations.Length != plan.ArtifactCount) throw new ArgumentException("Observation count must match artifacts.", nameof(observations));
            for (int i = 0; i < observations.Length; i++)
                if (observations[i] == null || !observations[i].IsValid || !ReferenceEquals(observations[i].Descriptor, plan.GetArtifact(i)))
                    throw new ArgumentException("Observations must match plan order.", nameof(observations));
            _plan = plan;
            _observations = new CaptureArtifactRecoveryObservation[observations.Length];
            Array.Copy(observations, _observations, observations.Length);
        }

        internal CapturePublicationPlan Plan => _plan;
        internal int Count => _observations.Length;
        internal CaptureArtifactRecoveryObservation GetObservation(int index)
        {
            if (index < 0 || index >= _observations.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return _observations[index];
        }


        internal bool IsValid
        {
            get
            {
                if (_plan == null || !_plan.IsValid || _observations == null || _observations.Length != _plan.ArtifactCount) return false;
                for (int i = 0; i < _observations.Length; i++)
                {
                    CaptureArtifactRecoveryObservation observation = _observations[i];
                    if (observation == null || !observation.IsValid || !ReferenceEquals(observation.Descriptor, _plan.GetArtifact(i))) return false;
                }
                return true;
            }
        }
    }
}
