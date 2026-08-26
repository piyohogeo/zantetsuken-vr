namespace Zantetsu.Observability.Editor
{
    /// <summary>
    /// Display grouping key for the trace timeline. Lane selection groups
    /// events; it never hides events. Events whose grouping ID is zero are kept
    /// under the unassigned key 0.
    /// </summary>
    public enum TraceTimelineLane : int
    {
        All = 0,
        Slash = 1,
        Object = 2,
        MobPlan = 3,
        Task = 4,
        Thread = 5,
    }
}
