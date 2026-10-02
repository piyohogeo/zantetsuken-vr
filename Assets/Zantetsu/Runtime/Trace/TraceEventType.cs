namespace Zantetsu.Trace
{
    /// <summary>
    /// Identifier of the production trace record. The stored value is fixed.
    /// </summary>
    public enum TraceEventType : int
    {
        None = 0,

        /// <summary>
        /// A SlashWave Segment Sweep met a convex a fragment is made of now (DESIGN 19.1.7, 21.16.6): an observation of
        /// the real hit, recorded with what the cut acceptance said -- including a hit it did not accept, for which no
        /// operation was issued. Not a statement that anything was cut.
        /// </summary>
        SlashHitConfirmed = 47,
    }
}
