using System;

namespace Zantetsu.Core.Statistics
{
    /// <summary>
    /// Which frames count as ordinary play (DESIGN 21.18): with an agreement, a world that is ready, no pause, the
    /// focus held -- and not the first frame after any of those began or after the conditions changed, whose interval
    /// is not a frame's. Pure, so that the rule is tested apart from the game.
    /// </summary>
    public sealed class PlayFrameGate
    {
        private bool _resume = true;

        public ulong Counted { get; private set; }
        public ulong Skipped { get; private set; }

        /// <summary>Something that breaks the run of frames began or ended: the next frame is not counted.</summary>
        public void Interrupt() => _resume = true;

        /// <summary>Whether this frame counts; the first frame after an interruption never does, and clears it.</summary>
        public bool Admit(bool agreed, bool worldReady, bool paused, bool focused)
        {
            if (!agreed || !worldReady || paused || !focused)
            {
                _resume = true;
                Skipped++;
                return false;
            }

            if (_resume)
            {
                _resume = false;
                Skipped++;
                return false;
            }

            Counted++;
            return true;
        }
    }

    /// <summary>
    /// The frame-by-frame collection into <see cref="PlayStatistics"/> (DESIGN 21.18), apart from Unity so that the
    /// connections are tested: the agreement that starts it (and begins the epoch of the first counted frame's
    /// conditions by itself), the conditions read every frame (a change begins its epoch at once), the game's
    /// cumulative counters counted from the agreement on and across a replacement of the world that keeps them, and the
    /// frame times that arrive late and are set aside after every boundary until the lag has passed.
    /// <para>
    /// **Late times.** FrameTimingManager's results come a few frames after their frame. After a boundary -- a frame not
    /// counted (the agreement, a pause, the focus lost, the world not ready) or a change of conditions -- the next
    /// <see cref="AttributionLagFrames"/> counted frames' FrameTimingManager values may belong to frames before it, so they
    /// are set aside: missing in the histogram and counted as <see cref="PlayEpoch.Unattributed"/>. The XR display's GPU time
    /// is "the last frame's" and is set aside for the first counted frame alone. The lag bound is an assumption about the
    /// manager, not a measurement; a lag past it would put a frame under the conditions after its own.
    /// </para>
    /// <para>
    /// **Counters.** A counter the game keeps (accepted cuts, failures, growths) is read as a total; this keeps the value
    /// at the first reading after the agreement as the base, and counts from it. A new generation of the source (the
    /// world replaced, a camera drawing's route remade), or a total that fell, carries what was counted so far and starts
    /// the base at 0: nothing before the agreement is counted, nothing across a replacement is lost or doubled. A failure
    /// kept by several sources (each camera drawing's route) is tracked per source and summed, so that one source remade
    /// neither resets nor doubles the others.
    /// </para>
    /// </summary>
    public sealed class PlayStatisticsCollector
    {
        /// <summary>How many counted frames after a boundary have their FrameTimingManager values set aside (the assumed bound of its lag).</summary>
        public const int AttributionLagFrames = 8;

        private struct Slot
        {
            public bool known;
            public int generation;
            public long baseValue, last, carried;
        }

        /// <summary>How many sources a failure count can come from (the world, a static, the camera drawings' routes), each with its own base and generation.</summary>
        public const int MaxSources = 8;

        private readonly Slot[] _totals = new Slot[(int)PlayTotalKind.Count];
        private readonly Slot[] _failures = new Slot[(int)PlayFailureKind.Count * MaxSources];
        private readonly Slot[] _growths = new Slot[(int)PlayCapacityKind.Count];
        private readonly Slot[] _shortfalls = new Slot[(int)PlayCapacityKind.Count];
        private PlayConditions _conditions;
        private bool _conditionsKnown;
        private long _revisionSeen;
        private bool _revisionKnown;
        private int _lagLeft;

        public PlayStatisticsCollector(PlayStatistics statistics)
        {
            Statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        }

        public PlayStatistics Statistics { get; }
        public PlayFrameGate Gate { get; } = new PlayFrameGate();

        /// <summary>Whether frames and counters are taken: from <see cref="Agree"/> to <see cref="Withdraw"/>.</summary>
        public bool Agreed { get; private set; }

        /// <summary>
        /// Whether the collection was stopped for good (<see cref="Stop"/>: the quit was asked for): the statistics are
        /// final from there, and nothing that happens while the quit waits for its send is a part of them.
        /// </summary>
        public bool Stopped { get; private set; }

        /// <summary>The quit was asked for: no frame, counter or setting is taken from here on. What was counted stays.</summary>
        public void Stop()
        {
            Stopped = true;
        }

        private bool Taking => Agreed && !Stopped;

        /// <summary>The conditions of the current epoch, once a frame was counted (<see cref="ConditionsKnown"/>).</summary>
        public PlayConditions Conditions => _conditions;
        public bool ConditionsKnown => _conditionsKnown;

        /// <summary>Counted frames whose FrameTimingManager values were set aside, in all.</summary>
        public ulong UnattributedFrames { get; private set; }

        /// <summary>
        /// The player agreed: everything counted so far goes, every counter's tracking with it (so that no source read
        /// before the withdrawal is summed again before its own new base is taken), the counters take their base at the
        /// next reading, and the epoch begins at the next counted frame.
        /// </summary>
        public void Agree()
        {
            Agreed = true;
            Statistics.Clear();
            Array.Clear(_totals, 0, _totals.Length);
            Array.Clear(_failures, 0, _failures.Length);
            Array.Clear(_growths, 0, _growths.Length);
            Array.Clear(_shortfalls, 0, _shortfalls.Length);
            _conditionsKnown = false;
            _revisionKnown = false;
            Interrupt();
        }

        /// <summary>The player declined or withdrew: nothing more is taken, and what was taken goes.</summary>
        public void Withdraw()
        {
            Agreed = false;
            Statistics.Clear();
        }

        /// <summary>A boundary from outside the frame's conditions (the application paused, the focus changed): the next frame is not counted and the lag starts over.</summary>
        public void Interrupt()
        {
            Gate.Interrupt();
            _lagLeft = AttributionLagFrames;
        }

        /// <summary>
        /// One frame, as the game stands in it. Returns whether it was counted. The frame of a change of conditions is
        /// not (it is of neither epoch); the first frame after any boundary is not.
        /// </summary>
        public bool Frame(bool worldReady, bool paused, bool focused, in PlayConditions conditions, float intervalMs, FrameTimeSample sample, long structureRevision)
        {
            if (Stopped)
            {
                return false;   // final: the quit's wait is no part of the play
            }

            if (!Gate.Admit(Agreed, worldReady, paused, focused))
            {
                _lagLeft = AttributionLagFrames;
                _revisionKnown = false;   // a structure that differs across the gap is not a change of this frame
                return false;
            }

            if (!_conditionsKnown || !conditions.Equals(_conditions))
            {
                // The frame of the change is of neither epoch; the frames after it are of the new one, their late times set aside.
                _conditions = conditions;
                _conditionsKnown = true;
                Statistics.BeginEpoch(in conditions);
                _lagLeft = AttributionLagFrames;
                return false;
            }

            bool unattributed = false;
            if (_lagLeft > 0)
            {
                if (_lagLeft == AttributionLagFrames) sample.HasGpuXr = false;   // "the last frame's": one frame of lag
                sample.HasMain = sample.HasRender = sample.HasGpu = false;
                unattributed = true;
                _lagLeft--;
                UnattributedFrames++;
            }

            bool changed = _revisionKnown && structureRevision != _revisionSeen;
            _revisionSeen = structureRevision;
            _revisionKnown = true;
            Statistics.NoteFrame(intervalMs, in sample, changed, unattributed);
            return true;
        }

        /// <summary>The live Logical Fragments now.</summary>
        public void Live(int liveFragments)
        {
            if (Taking) Statistics.NoteLiveFragments(liveFragments);
        }

        /// <summary>A total the game keeps, with the generation of what keeps it.</summary>
        public void Total(PlayTotalKind kind, int generation, long total)
        {
            if (Taking) Statistics.NoteTotal(kind, Track(ref _totals[(int)kind], generation, total));
        }

        /// <summary>A failure count the game keeps in one source (source 0), with the generation of what keeps it.</summary>
        public void Failure(PlayFailureKind kind, int generation, long total) => Failure(kind, 0, generation, total);

        /// <summary>
        /// A failure count one of several sources keeps (a camera drawing's route among others): tracked by source --
        /// its own base and generation, so that one source remade does not reset or double the others -- and the
        /// statistic is the sum over the sources. A source past <see cref="MaxSources"/> is not counted.
        /// </summary>
        public void Failure(PlayFailureKind kind, int source, int generation, long total)
        {
            if (!Taking || source < 0 || source >= MaxSources) return;
            int at = (int)kind * MaxSources;
            Track(ref _failures[at + source], generation, total);
            long sum = 0;
            for (int i = 0; i < MaxSources; i++)
            {
                ref Slot s = ref _failures[at + i];
                if (s.known) sum += s.carried + s.last - s.baseValue;
            }

            Statistics.NoteFailures(kind, sum);
        }

        /// <summary>A buffer's figures: the end in use (holes included), the capacity now and the fixed limit (0 for none), its growth and shortfall totals.</summary>
        public void Capacity(PlayCapacityKind kind, int generation, long end, long capacity, long limit, long growthsTotal, long shortfallsTotal)
        {
            if (!Taking) return;
            long growths = Track(ref _growths[(int)kind], generation, growthsTotal);
            long shortfalls = Track(ref _shortfalls[(int)kind], generation, shortfallsTotal);
            Statistics.NoteCapacity(kind, end, capacity, limit, growths, shortfalls);
        }

        public void Capacity(PlayCapacityKind kind, int generation, long end, long capacity, long growthsTotal, long shortfallsTotal)
            => Capacity(kind, generation, end, capacity, 0, growthsTotal, shortfallsTotal);

        public void Settings(in PlayUiSettings settings)
        {
            if (Taking) Statistics.NoteSettings(in settings);
        }

        // The value counted from the agreement: the total less its base, plus what earlier generations came to. An
        // unknown slot (never read, or cleared by the agreement) takes this reading as its base and counts nothing yet.
        private static long Track(ref Slot s, int generation, long total)
        {
            if (!s.known)
            {
                s.known = true;
                s.generation = generation;
                s.baseValue = total;
                s.last = total;
                s.carried = 0;
                return 0;
            }

            if (generation != s.generation || total < s.last)
            {
                s.carried += s.last - s.baseValue;
                s.generation = generation;
                s.baseValue = 0;
            }

            s.last = total;
            return s.carried + total - s.baseValue;
        }
    }
}
