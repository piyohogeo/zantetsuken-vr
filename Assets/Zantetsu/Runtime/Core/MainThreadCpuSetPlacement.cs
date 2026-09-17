using System;
using System.Collections.Generic;

namespace Zantetsu.Core
{
    /// <summary>
    /// One CPU Set as the system reports it, which on a normal machine is one logical processor
    /// (<c>SYSTEM_CPU_SET_INFORMATION</c>, the <c>CpuSetInformation</c> record).
    /// </summary>
    public readonly struct CpuSetRecord
    {
        public CpuSetRecord(
            uint id,
            ushort group,
            byte logicalProcessorIndex,
            byte coreIndex,
            byte lastLevelCacheIndex,
            byte numaNodeIndex,
            byte efficiencyClass,
            byte allFlags)
        {
            this.id = id;
            this.group = group;
            this.logicalProcessorIndex = logicalProcessorIndex;
            this.coreIndex = coreIndex;
            this.lastLevelCacheIndex = lastLevelCacheIndex;
            this.numaNodeIndex = numaNodeIndex;
            this.efficiencyClass = efficiencyClass;
            this.allFlags = allFlags;
        }

        /// <summary>The CPU Set ID, which is what a thread assignment names.</summary>
        public readonly uint id;

        /// <summary>The processor group this set belongs to; every other field is relative to it.</summary>
        public readonly ushort group;

        /// <summary>The group-relative index of this set's home processor.</summary>
        public readonly byte logicalProcessorIndex;

        /// <summary>Which core the home processor is on: the same for the threads of one SMT core.</summary>
        public readonly byte coreIndex;

        public readonly byte lastLevelCacheIndex;
        public readonly byte numaNodeIndex;

        /// <summary>
        /// The intrinsic energy efficiency of the home processor. **A higher number is the faster, less
        /// power-efficient processor**, which is what makes the highest class the P cores on a heterogeneous
        /// machine. Every set having the same value is how a machine says it is not heterogeneous.
        /// </summary>
        public readonly byte efficiencyClass;

        /// <summary>The flag byte: Parked, Allocated, AllocatedToTargetProcess, RealTime, then reserved bits.</summary>
        public readonly byte allFlags;

        /// <summary>Parked for power or thermal reasons. A passing state, not a reason to leave a core out.</summary>
        public bool Parked => (allFlags & 0x01) != 0;

        /// <summary>Reserved for some process's exclusive use.</summary>
        public bool Allocated => (allFlags & 0x02) != 0;

        /// <summary>Reserved for **this** process, which is the process the topology was read for.</summary>
        public bool AllocatedToThisProcess => (allFlags & 0x04) != 0;

        public bool RealTime => (allFlags & 0x08) != 0;

        /// <summary>
        /// Reserved for someone else. The system ignores a request that names such a set and schedules the thread
        /// elsewhere, so naming it would quietly widen where the thread runs: it is left out instead.
        /// </summary>
        public bool AllocatedElsewhere => Allocated && !AllocatedToThisProcess;

        public override string ToString()
        {
            return "cpu set " + id + " (group " + group + ", cpu " + logicalProcessorIndex + ", core " + coreIndex
                + ", efficiency class " + efficiencyClass + ", flags 0x" + allFlags.ToString("x2") + ")";
        }
    }

    /// <summary>What an attempt to place the main thread did.</summary>
    public enum CpuSetPlacementOutcome
    {
        /// <summary>Nothing has been attempted yet.</summary>
        NotAttempted = 0,

        /// <summary>The assignment was set and read back as asked.</summary>
        Applied = 1,

        /// <summary>
        /// Not a heterogeneous machine: every CPU Set has the same efficiency class, so there is no faster class to
        /// choose and **nothing was changed**. This is a normal answer, not a failure.
        /// </summary>
        NotNeeded = 2,

        /// <summary>
        /// The topology could not be read, or could not be judged from what was read — several processor groups, no
        /// records, a home processor outside the affinity mask's width. Nothing was changed.
        /// </summary>
        TopologyUnavailable = 3,

        /// <summary>What the main thread already had could not be read, so nothing was changed.</summary>
        OriginalUnreadable = 4,

        /// <summary>
        /// Nothing of the fastest class is available to this process — every one of them is reserved for someone
        /// else or outside this process's affinity mask. Nothing was changed.
        /// </summary>
        NoCandidates = 5,

        /// <summary>The call that sets the assignment failed. Nothing was changed.</summary>
        ApplyFailed = 6,

        /// <summary>
        /// The call reported success but the read back assignment is not what was asked for. Something **was**
        /// changed, so this is not the same as a failure to apply: see <see cref="MainThreadCpuSetPlacement.Changed"/>
        /// and put it back with <see cref="MainThreadCpuSetPlacement.Restore"/>.
        /// </summary>
        ReadbackMismatch = 7,
    }

    /// <summary>What an attempt to put the main thread's assignment back did.</summary>
    public enum CpuSetRestoreOutcome
    {
        NotAttempted = 0,

        /// <summary>Nothing had been changed, so there was nothing to put back.</summary>
        NotNeeded = 1,

        /// <summary>The saved assignment is back, and read back as such.</summary>
        Restored = 2,

        /// <summary>The call failed. What was saved is **kept**, and this may be tried again.</summary>
        RestoreFailed = 3,

        /// <summary>The call reported success but the read back assignment is not the saved one. Kept, and retriable.</summary>
        ReadbackMismatch = 4,

        /// <summary>
        /// This is not the thread the placement was applied on. Nothing was touched: an assignment belongs to its
        /// own thread, and putting it back is that thread's business.
        /// </summary>
        WrongThread = 5,
    }

    /// <summary>
    /// The few operations the placement needs from the operating system. **What is not here is the point**: there is
    /// no way from this interface to change a hard affinity mask, the process default CPU Sets, any thread's
    /// priority, or anything belonging to a thread other than the calling one. The placement therefore cannot do
    /// those things, whatever it is handed.
    /// </summary>
    public interface IMainThreadCpuSetOs
    {
        /// <summary>An id for the calling thread, used only to tell one thread from another.</summary>
        int CurrentThreadId { get; }

        /// <summary>How many processor groups are active. Anything but one cannot be judged here.</summary>
        int ProcessorGroupCount { get; }

        /// <summary>The system's CPU Sets, as seen for this process. False when they could not be read.</summary>
        bool TryReadSystemCpuSets(List<CpuSetRecord> into);

        /// <summary>
        /// The processors this process is allowed on, as a group-relative mask. A restrictive affinity mask is
        /// respected above any CPU Set assignment, so a set outside it is not available.
        /// </summary>
        bool TryReadProcessAffinityMask(out ulong mask);

        /// <summary>
        /// The processors the **calling thread** is allowed on, and the group that mask belongs to. A thread's own
        /// restrictive affinity is respected above a CPU Set assignment just as the process's is, so a thread
        /// already confined to some processors cannot be placed outside them however the assignment reads back.
        /// Read only: nothing here changes a hard affinity.
        /// </summary>
        bool TryReadCurrentThreadAffinity(out ulong mask, out int group);

        /// <summary>
        /// The calling thread's own selected CPU Sets. An empty result means it has no assignment of its own, which
        /// is different from false: that is a failure to read.
        /// </summary>
        bool TryReadSelectedCpuSets(List<uint> into);

        /// <summary>
        /// Sets the calling thread's own selected CPU Sets. A null or empty list **clears** the assignment, which is
        /// how a thread that had none is put back the way it was.
        /// </summary>
        bool TrySetSelectedCpuSets(IReadOnlyList<uint> ids);
    }

    /// <summary>
    /// Puts the Unity main thread on the fastest cores of a heterogeneous Windows machine, and puts it back
    /// (DESIGN 4.3 "Mainのコア配置", D-083, chapter 15).
    /// <para>
    /// **What it does.** It reads the real topology, works out which CPU Sets of the fastest efficiency class this
    /// process may actually use, and gives the **calling thread** — which is meant to be the main thread — a
    /// thread-selected CPU Set assignment naming them. Nothing is inferred from a CPU model name, from whether a
    /// logical processor's number is odd or even, or from a hard-coded mask: the classes come from the system's own
    /// records, and when they cannot be judged this says so instead of guessing.
    /// </para>
    /// <para>
    /// **What it does not touch.** The main thread's hard affinity and OS priority, the process default CPU Sets,
    /// and every other thread — Unity's workers, the Geometry and Background pools, the render thread — are left
    /// exactly as they are. There is no monitoring, no reapplying, no falling back to a hard affinity, and no new
    /// reason for a player to quit. A CPU Set assignment is a soft preference the scheduler respects, so nothing
    /// here promises that the main thread never runs on an efficiency core.
    /// </para>
    /// <para>
    /// **One owner, one thread.** The placement belongs to the thread that applied it: <see cref="Apply"/> records
    /// which thread that was and <see cref="Restore"/> refuses to act on any other. Applying twice changes nothing
    /// and, in particular, never overwrites the assignment saved the first time. Restoring puts back exactly what
    /// was saved — including nothing at all, when the thread had no assignment — and a restore that fails keeps
    /// what it saved so that it can be tried again.
    /// </para>
    /// <para>
    /// **Changed carries the responsibility to restore.** <see cref="Changed"/> is true from the moment an
    /// assignment was successfully set until a restore has been confirmed — that and nothing more. It is not a
    /// claim that the thread's assignment now differs from what it was: setting the same set the thread already had
    /// makes it true just the same, because the responsibility is the same. A refusal to apply leaves it false,
    /// because nothing was set; an assignment that was set but did not read back leaves it **true**, because
    /// something was set even though the outcome is not what was asked for.
    /// </para>
    /// <para>
    /// **No connection of its own.** This is an explicit entry point: something has to call <see cref="Apply"/>
    /// early on the main thread and <see cref="Restore"/> when it is done. Nothing in the project calls either yet —
    /// there is no appropriate product connection point to call them from, so this stays an explicit entry point
    /// rather than inventing one.
    /// </para>
    /// <para>
    /// **It places the thread that calls it.** Every part of this works on the calling thread: which thread that is
    /// belongs to the caller, and calling it on the Unity main thread is the caller's responsibility, not something
    /// this can check.
    /// </para>
    /// </summary>
    public sealed class MainThreadCpuSetPlacement
    {
        private readonly IMainThreadCpuSetOs _os;
        private readonly List<CpuSetRecord> _records = new List<CpuSetRecord>();
        private readonly List<uint> _original = new List<uint>();
        private readonly List<uint> _selected = new List<uint>();
        private readonly List<uint> _readBack = new List<uint>();
        private readonly List<uint> _scratch = new List<uint>();

        public MainThreadCpuSetPlacement(IMainThreadCpuSetOs os)
        {
            _os = os ?? throw new ArgumentNullException(nameof(os));
        }

        /// <summary>What the one attempt to apply did. <see cref="CpuSetPlacementOutcome.NotAttempted"/> until then.</summary>
        public CpuSetPlacementOutcome Outcome { get; private set; }

        /// <summary>What the last attempt to restore did.</summary>
        public CpuSetRestoreOutcome RestoreOutcome { get; private set; }

        /// <summary>
        /// Whether an assignment was set and not yet put back: the responsibility to restore, in other words. True
        /// from a successful set — including one that did not read back, and including one that names exactly what
        /// the thread already had — until <see cref="Restore"/> has confirmed the earlier assignment is back. Not a
        /// statement that the thread's assignment differs from its original.
        /// </summary>
        public bool Changed { get; private set; }

        /// <summary>Why the outcome is what it is, in words, or null when there is nothing to say.</summary>
        public string Problem { get; private set; }

        /// <summary>The thread <see cref="Apply"/> ran on, which is the only thread <see cref="Restore"/> will act on.</summary>
        public int PlacedOnThreadId { get; private set; }

        /// <summary>The assignment the thread had before, saved. Empty means it had none.</summary>
        public IReadOnlyList<uint> OriginalAssignment => _original;

        /// <summary>The CPU Set IDs that were chosen, in order. Empty when nothing was chosen.</summary>
        public IReadOnlyList<uint> Selected => _selected;

        /// <summary>What was read back after the assignment was set.</summary>
        public IReadOnlyList<uint> ReadBack => _readBack;

        /// <summary>The CPU Sets the topology reported, for the record.</summary>
        public IReadOnlyList<CpuSetRecord> Records => _records;

        /// <summary>The highest efficiency class seen, which is the class that was chosen from. -1 until read.</summary>
        public int FastestEfficiencyClass { get; private set; } = -1;

        /// <summary>The lowest efficiency class seen. Equal to the highest on a machine that is not heterogeneous.</summary>
        public int SlowestEfficiencyClass { get; private set; } = -1;

        /// <summary>How many sets of the fastest class were left out because they are reserved for someone else.</summary>
        public int ExcludedAsAllocatedElsewhere { get; private set; }

        /// <summary>How many were left out because they are outside this process's affinity mask.</summary>
        public int ExcludedByProcessAffinity { get; private set; }

        /// <summary>
        /// How many were left out because they are outside the calling thread's **own** affinity mask, although the
        /// process is allowed on them.
        /// </summary>
        public int ExcludedByThreadAffinity { get; private set; }

        /// <summary>How many of the chosen sets were parked when they were chosen. They are kept all the same.</summary>
        public int ParkedAmongSelected { get; private set; }

        /// <summary>
        /// Places the calling thread, once. Call it on the main thread, early, with nothing else placing that
        /// thread. Calling it again returns the first outcome and changes nothing at all — in particular it does not
        /// read or overwrite the saved original assignment, so a second initialisation cannot lose it.
        /// </summary>
        public CpuSetPlacementOutcome Apply()
        {
            if (Outcome != CpuSetPlacementOutcome.NotAttempted)
            {
                return Outcome;
            }

            PlacedOnThreadId = _os.CurrentThreadId;

            _records.Clear();
            if (!_os.TryReadSystemCpuSets(_records) || _records.Count == 0)
            {
                return Fail(CpuSetPlacementOutcome.TopologyUnavailable, "the system CPU sets could not be read");
            }

            int groups = _os.ProcessorGroupCount;
            if (groups != 1)
            {
                return Fail(
                    CpuSetPlacementOutcome.TopologyUnavailable,
                    "this machine has " + groups + " processor groups, and which of them the main thread belongs to is not decided here");
            }

            int fastest = -1;
            int slowest = int.MaxValue;
            for (int i = 0; i < _records.Count; i++)
            {
                CpuSetRecord record = _records[i];
                if (record.group != 0)
                {
                    return Fail(
                        CpuSetPlacementOutcome.TopologyUnavailable,
                        "one processor group was reported but " + record + " is in another");
                }

                if (record.logicalProcessorIndex >= 64)
                {
                    return Fail(
                        CpuSetPlacementOutcome.TopologyUnavailable,
                        record + " is outside the width of an affinity mask, so availability cannot be judged");
                }

                if (record.efficiencyClass > fastest)
                {
                    fastest = record.efficiencyClass;
                }

                if (record.efficiencyClass < slowest)
                {
                    slowest = record.efficiencyClass;
                }
            }

            FastestEfficiencyClass = fastest;
            SlowestEfficiencyClass = slowest;

            if (fastest == slowest)
            {
                // Not heterogeneous: there is no faster class to choose, so nothing is specified and nothing is
                // changed. Told apart from every failure above on purpose.
                Outcome = CpuSetPlacementOutcome.NotNeeded;
                Problem = null;
                return Outcome;
            }

            if (!_os.TryReadProcessAffinityMask(out ulong processAffinity))
            {
                return Fail(CpuSetPlacementOutcome.TopologyUnavailable, "the process affinity mask could not be read");
            }

            // The thread's own affinity counts as much as the process's: Windows respects a restrictive mask above
            // any CPU Set assignment, so a thread already confined somewhere cannot be placed outside it, however
            // the assignment reads back afterwards. Read, never changed.
            if (!_os.TryReadCurrentThreadAffinity(out ulong threadAffinity, out int threadGroup))
            {
                return Fail(
                    CpuSetPlacementOutcome.TopologyUnavailable, "this thread's own affinity mask could not be read");
            }

            if (threadGroup != 0)
            {
                return Fail(
                    CpuSetPlacementOutcome.TopologyUnavailable,
                    "this thread's affinity is in processor group " + threadGroup + ", which is not the one group read here");
            }

            ulong allowed = processAffinity & threadAffinity;

            _selected.Clear();
            ExcludedAsAllocatedElsewhere = 0;
            ExcludedByProcessAffinity = 0;
            ExcludedByThreadAffinity = 0;
            ParkedAmongSelected = 0;
            for (int i = 0; i < _records.Count; i++)
            {
                CpuSetRecord record = _records[i];
                if (record.efficiencyClass != fastest)
                {
                    continue;
                }

                if (record.AllocatedElsewhere)
                {
                    ExcludedAsAllocatedElsewhere++;
                    continue;
                }

                ulong bit = 1UL << record.logicalProcessorIndex;
                if ((processAffinity & bit) == 0)
                {
                    ExcludedByProcessAffinity++;
                    continue;
                }

                if ((allowed & bit) == 0)
                {
                    // Allowed to the process, but not to this thread.
                    ExcludedByThreadAffinity++;
                    continue;
                }

                if (record.Parked)
                {
                    // A passing power state. Leaving it out would narrow the placement for as long as it lasts.
                    ParkedAmongSelected++;
                }

                _selected.Add(record.id);
            }

            if (_selected.Count == 0)
            {
                return Fail(
                    CpuSetPlacementOutcome.NoCandidates,
                    "no CPU set of efficiency class " + fastest + " is available to this thread: "
                    + ExcludedAsAllocatedElsewhere + " reserved elsewhere, "
                    + ExcludedByProcessAffinity + " outside the process affinity mask, "
                    + ExcludedByThreadAffinity + " outside this thread's own");
            }

            _selected.Sort();

            _original.Clear();
            if (!_os.TryReadSelectedCpuSets(_original))
            {
                _original.Clear();
                _selected.Clear();
                return Fail(
                    CpuSetPlacementOutcome.OriginalUnreadable,
                    "what this thread already had could not be read, so nothing was changed");
            }

            if (!_os.TrySetSelectedCpuSets(_selected))
            {
                return Fail(CpuSetPlacementOutcome.ApplyFailed, "setting the thread's selected CPU sets failed");
            }

            // From here something has been changed, whatever the readback says.
            Changed = true;

            _readBack.Clear();
            if (!_os.TryReadSelectedCpuSets(_readBack))
            {
                return Fail(
                    CpuSetPlacementOutcome.ReadbackMismatch,
                    "the assignment was set but could not be read back");
            }

            if (!SameSet(_readBack, _selected))
            {
                return Fail(
                    CpuSetPlacementOutcome.ReadbackMismatch,
                    "the assignment was set to [" + Join(_selected) + "] but reads back as [" + Join(_readBack) + "]");
            }

            Outcome = CpuSetPlacementOutcome.Applied;
            Problem = null;
            return Outcome;
        }

        /// <summary>
        /// Puts back what was saved: the earlier assignment, or no assignment at all when there was none. Does
        /// nothing when nothing was changed, and refuses on any thread but the one that applied it. A failure keeps
        /// everything it saved, and may be tried again.
        /// </summary>
        public CpuSetRestoreOutcome Restore()
        {
            if (!Changed)
            {
                RestoreOutcome = CpuSetRestoreOutcome.NotNeeded;
                return RestoreOutcome;
            }

            if (_os.CurrentThreadId != PlacedOnThreadId)
            {
                RestoreOutcome = CpuSetRestoreOutcome.WrongThread;
                Problem = "this placement belongs to thread " + PlacedOnThreadId
                    + " and cannot be put back from thread " + _os.CurrentThreadId;
                return RestoreOutcome;
            }

            if (!_os.TrySetSelectedCpuSets(_original))
            {
                RestoreOutcome = CpuSetRestoreOutcome.RestoreFailed;
                Problem = "putting the earlier assignment back failed; it is still [" + Join(_original) + "] here";
                return RestoreOutcome;
            }

            _scratch.Clear();
            if (!_os.TryReadSelectedCpuSets(_scratch) || !SameSet(_scratch, _original))
            {
                RestoreOutcome = CpuSetRestoreOutcome.ReadbackMismatch;
                Problem = "the earlier assignment [" + Join(_original) + "] was put back but reads back as ["
                    + Join(_scratch) + "]";
                return RestoreOutcome;
            }

            // Only now is the thread back where it was. What was saved is kept, for the record.
            Changed = false;
            RestoreOutcome = CpuSetRestoreOutcome.Restored;
            Problem = null;
            return RestoreOutcome;
        }

        private CpuSetPlacementOutcome Fail(CpuSetPlacementOutcome outcome, string problem)
        {
            Outcome = outcome;
            Problem = problem;
            return outcome;
        }

        private static bool SameSet(List<uint> a, List<uint> b)
        {
            if (a.Count != b.Count)
            {
                return false;
            }

            for (int i = 0; i < a.Count; i++)
            {
                bool found = false;
                for (int k = 0; k < b.Count; k++)
                {
                    if (a[i] == b[k])
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }
            }

            return true;
        }

        private static string Join(List<uint> ids)
        {
            if (ids.Count == 0)
            {
                return "none";
            }

            var text = new System.Text.StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0)
                {
                    text.Append(' ');
                }

                text.Append(ids[i]);
            }

            return text.ToString();
        }
    }
}
