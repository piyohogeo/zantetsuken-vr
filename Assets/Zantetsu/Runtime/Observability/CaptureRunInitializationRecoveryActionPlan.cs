using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Immutable, ordered recovery action plan produced for one decision. The
    /// step array is allocated once at construction, never exposed, and never
    /// mutated.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The constructor is the sole owner of the step array: it computes the
    /// exact step count from the decision, allocates the array exactly once,
    /// and fills it directly. The array is never exposed, so a plan cannot be
    /// mutated after construction. <see cref="IsValid"/> recomputes the same
    /// sequence from the held values without throwing.
    /// </para>
    /// <para>
    /// This type owns and disposes nothing and performs no filesystem work. It
    /// is not an <see cref="IDisposable"/>, MonoBehaviour, or ScriptableObject.
    /// </para>
    /// </remarks>
    internal sealed class CaptureRunInitializationRecoveryActionPlan
    {
        private readonly CaptureRunInitializationRecoveryDecision _decision;
        private readonly CaptureRunInitializationRecoveryStep[] _steps;

        internal CaptureRunInitializationRecoveryActionPlan(
            CaptureRunInitializationRecoveryDecision decision)
        {
            if (decision == null)
            {
                throw new ArgumentNullException(nameof(decision));
            }

            if (!decision.IsValid)
            {
                throw new ArgumentException("Decision must be valid.", nameof(decision));
            }

            int count = CaptureRunInitializationRecoveryActionPlanBuilder.ComputeStepCount(decision);
            CaptureRunInitializationRecoveryStep[] steps = new CaptureRunInitializationRecoveryStep[count];
            for (int i = 0; i < count; i++)
            {
                steps[i] = CaptureRunInitializationRecoveryActionPlanBuilder.StepAt(decision, i);
            }

            _decision = decision;
            _steps = steps;
        }

        internal CaptureRunInitializationRecoveryDecision Decision => _decision;

        internal int Count => _steps.Length;

        internal CaptureRunMarkerBinding ExpectedBinding => _decision.ExpectedBinding;

        internal CaptureRunRootLayout RootLayout => _decision.RootLayout;

        internal long TestRunId => _decision.TestRunId;

        internal CaptureRunInitializationRecoveryStep GetStep(int index)
        {
            if (index < 0 || index >= _steps.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index, "Step index out of range.");
            }

            return _steps[index];
        }

        internal bool IsValid
        {
            get
            {
                if (_decision == null || !_decision.IsValid || _steps == null)
                {
                    return false;
                }

                int expectedCount = CaptureRunInitializationRecoveryActionPlanBuilder.ComputeStepCount(_decision);
                if (_steps.Length != expectedCount)
                {
                    return false;
                }

                for (int i = 0; i < _steps.Length; i++)
                {
                    CaptureRunInitializationRecoveryStep actual = _steps[i];
                    if (actual == null || !actual.Matches(CaptureRunInitializationRecoveryActionPlanBuilder.StepAt(_decision, i)))
                    {
                        return false;
                    }
                }

                return true;
            }
        }
    }
}
