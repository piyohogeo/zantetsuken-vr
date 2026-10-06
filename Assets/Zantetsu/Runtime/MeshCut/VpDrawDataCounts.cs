namespace Zantetsu.MeshCut
{
    /// <summary>
    /// Observation only: what the collections of one frame did to the body's draw data -- the commands and the instance
    /// records (a transform and a clip) of <see cref="VpLogicalCutDisplay"/> -- and where that data stood afterwards.
    /// It is counted where the work is done, never by comparing anything, and is part of
    /// <see cref="VpLogicalCutDisplay.FrameCounts"/>, so a frame's markers and these counts belong to the same frame.
    /// </summary>
    public struct VpDrawDataCounts
    {
        /// <summary>Whether a collection of that frame filled this in.</summary>
        public bool known;

        /// <summary>
        /// CPU: commands and instance records written into the side being built; and commands and instance records
        /// that side was first given from the adopted one, because the adopted side had been written there and it had not.
        /// </summary>
        public long commandsWritten, commandsCaughtUp, instancesWritten, instancesCaughtUp;

        /// <summary>
        /// Registrations whose commands were written (again), and instance regions taken at the end -- a
        /// registration first drawn, or one whose instances no longer fitted the region it had. Of those, the regions
        /// taken for a registration that had one, and the instance records that stood in the one it left: what a
        /// structural change moved. A command slot never moves.
        /// </summary>
        public long registrationsWritten, regionsTaken, regionsMoved, instancesMoved;

        /// <summary>
        /// GPU, of what changed: commands sent (arguments, or the selection's commands) with their buffer writes, and
        /// instance records sent with theirs.
        /// </summary>
        public long argumentElements, argumentCalls, instanceElements, instanceCalls;

        /// <summary>Of <see cref="instanceCalls"/>: the buffer writes of the instance records' one range, the transforms' and the clips' (at most one each a collection).</summary>
        public long transformCalls, clipCalls;

        /// <summary>
        /// GPU, of everything valid, apart from the above: a batch's first upload, a batch that took a smaller one's
        /// place, another stereo condition. Commands, instance records, and the buffer writes of both together.
        /// </summary>
        public long wholeArgumentElements, wholeInstanceElements, wholeCalls;

        /// <summary>
        /// The compaction of the instance regions (DESIGN 5.6, D-202): compactions run in this frame's collections (0 or
        /// 1), the instance records they laid out again, the time from the decision to the adoption in ms, and the
        /// collections that found a compaction necessary and did not run it (the gate, or the collection's own reasons).
        /// </summary>
        public int compactions, compactionsSkipped, compactionCandidates;

        /// <summary>
        /// Of the compactions' time: the order, the candidate's build (stage 5) and the upload (stage 7), in ms. And
        /// the history: registrations whose last-written frame this frame's adopted collections advanced.
        /// </summary>
        public double compactionOrderMilliseconds, compactionWriteMilliseconds, compactionUploadMilliseconds;
        public long historyWrites;
        public long compactionRecordsMoved;
        public double compactionMilliseconds;

        /// <summary>
        /// After the frame's last collection: the commands that are processed -- every command slot ever taken, those
        /// of what is drawn no more included -- how many of those draw something, and the room; the same of the
        /// instance records. The command end only grows; compaction may lower the instance end.
        /// </summary>
        public int commandEnd, commandsLive, commandCapacity, instanceEnd, instancesLive, instanceCapacity;
    }
}
