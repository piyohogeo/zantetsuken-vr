using System;

namespace Zantetsu.Observability
{
    /// <summary>
    /// Pure, stateless builder that converts one recovery action plan into an
    /// execution batch. It holds no fields and performs no filesystem work.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The builder rejects a null or invalid plan and otherwise delegates
    /// directly to the <see cref="CaptureRunInitializationRecoveryExecutionBatch"/>
    /// constructor, which owns the prepared-step array.
    /// </para>
    /// </remarks>
    internal static class CaptureRunInitializationRecoveryExecutionBatchBuilder
    {
        internal static CaptureRunInitializationRecoveryExecutionBatch Build(
            CaptureRunInitializationRecoveryActionPlan actionPlan)
        {
            if (actionPlan == null)
            {
                throw new ArgumentNullException(nameof(actionPlan));
            }

            if (!actionPlan.IsValid)
            {
                throw new ArgumentException("Action plan must be valid.", nameof(actionPlan));
            }

            return new CaptureRunInitializationRecoveryExecutionBatch(actionPlan);
        }
    }
}
