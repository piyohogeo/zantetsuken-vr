namespace Zantetsu.Core.Input
{
    /// <summary>
    /// Result of an edge direction gate evaluation. Reference-free value type.
    /// <see cref="Reason"/> is the single source of truth: a decision is
    /// accepted exactly when <see cref="Reason"/> is
    /// <see cref="BladeEdgeGateReason.None"/>. No independently settable
    /// accepted flag exists.
    /// </summary>
    public readonly struct BladeEdgeGateDecision
    {
        public readonly BladeEdgeGateReason Reason;

        public bool IsAccepted => Reason == BladeEdgeGateReason.None;

        public BladeEdgeGateDecision(BladeEdgeGateReason reason)
        {
            Reason = reason;
        }
    }
}
