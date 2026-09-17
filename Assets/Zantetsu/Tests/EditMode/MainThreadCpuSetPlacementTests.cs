using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using Zantetsu.Core;

namespace Zantetsu.Core.Tests
{
    /// <summary>
    /// The main thread's CPU Set placement of DESIGN 4.3 (D-083, chapter 15), on a stand-in for the operating
    /// system: the classification of a heterogeneous machine, what a machine that is not one leaves alone, saving
    /// and putting back both an empty and a real earlier assignment, and each way it can refuse.
    /// <para>
    /// Nothing here runs on a real topology — that is the player harness's business. What is checked here is the
    /// decision and the ownership: which CPU Sets are chosen and why, what is changed and what is not, and that
    /// nothing is lost when something fails.
    /// </para>
    /// </summary>
    public class MainThreadCpuSetPlacementTests
    {
        private const byte Parked = 0x01;
        private const byte Allocated = 0x02;
        private const byte AllocatedToThisProcess = 0x04;

        /// <summary>
        /// The operating system as the test decides it. Every call it was asked to make is recorded, and it offers
        /// nothing that could change a hard affinity, the process default CPU Sets, a priority or another thread —
        /// because the interface it implements offers nothing of the kind.
        /// </summary>
        private sealed class FakeOs : IMainThreadCpuSetOs
        {
            public int threadId = 7;
            public int groupCount = 1;
            public bool topologyReadable = true;
            public bool affinityReadable = true;
            public bool selectedReadable = true;
            public bool setSucceeds = true;
            public ulong affinityMask = ulong.MaxValue;

            /// <summary>What this thread itself is allowed, which may be narrower than the process.</summary>
            public ulong threadAffinityMask = ulong.MaxValue;
            public int threadAffinityGroup;
            public bool threadAffinityReadable = true;

            /// <summary>What the thread's assignment is, as the fake keeps it. Empty means none.</summary>
            public readonly List<uint> assignment = new List<uint>();

            public readonly List<CpuSetRecord> records = new List<CpuSetRecord>();
            public readonly List<string> calls = new List<string>();
            public readonly List<string> setCalls = new List<string>();

            /// <summary>Makes the readback answer something else than what was set, once it has been set.</summary>
            public bool readbackDisagrees;

            public int CurrentThreadId
            {
                get { return threadId; }
            }

            public int ProcessorGroupCount
            {
                get
                {
                    calls.Add("groups");
                    return groupCount;
                }
            }

            public bool TryReadSystemCpuSets(List<CpuSetRecord> into)
            {
                calls.Add("topology");
                into.Clear();
                if (!topologyReadable)
                {
                    return false;
                }

                into.AddRange(records);
                return into.Count > 0;
            }

            public bool TryReadProcessAffinityMask(out ulong mask)
            {
                calls.Add("affinity");
                mask = affinityMask;
                return affinityReadable;
            }

            public bool TryReadCurrentThreadAffinity(out ulong mask, out int group)
            {
                calls.Add("thread affinity");
                mask = threadAffinityMask;
                group = threadAffinityGroup;
                return threadAffinityReadable;
            }

            public bool TryReadSelectedCpuSets(List<uint> into)
            {
                calls.Add("read");
                into.Clear();
                if (!selectedReadable)
                {
                    return false;
                }

                into.AddRange(assignment);
                if (readbackDisagrees && into.Count > 0)
                {
                    into[0] = into[0] + 1000;
                }

                return true;
            }

            public bool TrySetSelectedCpuSets(IReadOnlyList<uint> ids)
            {
                string named = ids == null || ids.Count == 0 ? "clear" : string.Join(",", Ids(ids));
                calls.Add("set " + named);
                setCalls.Add(named);
                if (!setSucceeds)
                {
                    return false;
                }

                assignment.Clear();
                if (ids != null)
                {
                    for (int i = 0; i < ids.Count; i++)
                    {
                        assignment.Add(ids[i]);
                    }
                }

                return true;
            }

            private static string[] Ids(IReadOnlyList<uint> ids)
            {
                var text = new string[ids.Count];
                for (int i = 0; i < ids.Count; i++)
                {
                    text[i] = ids[i].ToString();
                }

                return text;
            }
        }

        private static CpuSetRecord Record(uint id, byte cpu, byte core, byte efficiencyClass, byte flags = 0)
        {
            return new CpuSetRecord(id, 0, cpu, core, 0, 0, efficiencyClass, flags);
        }

        /// <summary>Eight fast threads on four cores, then four slow ones: a heterogeneous machine in miniature.</summary>
        private static void FillHybrid(FakeOs os)
        {
            os.records.Clear();
            byte cpu = 0;
            for (byte core = 0; core < 4; core++)
            {
                os.records.Add(Record(256u + cpu, cpu, core, 1));
                cpu++;
                os.records.Add(Record(256u + cpu, cpu, core, 1));
                cpu++;
            }

            for (byte core = 4; core < 8; core++)
            {
                os.records.Add(Record(256u + cpu, cpu, core, 0));
                cpu++;
            }
        }

        private static uint[] FastIds()
        {
            return new uint[] { 256, 257, 258, 259, 260, 261, 262, 263 };
        }

        // ----- classification -----------------------------------------------------------------------------------

        /// <summary>
        /// The fastest efficiency class is what gets chosen, from the real records — not from a model name, a
        /// processor number's parity or a fixed mask — and the permission to use a processor is respected.
        /// </summary>
        [Test]
        public void OnAHeterogeneousMachine_TheFastestClassIsChosen_AsFarAsItIsAllowed()
        {
            var os = new FakeOs();
            FillHybrid(os);

            // One fast thread is reserved for another process, one is outside this process's mask, one is parked.
            os.records[7] = Record(263, 7, 3, 1, Allocated);
            os.records[6] = Record(262, 6, 3, 1, Parked);
            os.affinityMask = ~(1UL << 5);

            var placement = new MainThreadCpuSetPlacement(os);
            Assert.That(placement.Apply(), Is.EqualTo(CpuSetPlacementOutcome.Applied), placement.Problem);

            Assert.That(placement.FastestEfficiencyClass, Is.EqualTo(1));
            Assert.That(placement.SlowestEfficiencyClass, Is.EqualTo(0));
            Assert.That(
                placement.Selected, Is.EqualTo(new uint[] { 256, 257, 258, 259, 260, 262 }),
                "the fast sets this process may use, and no slow one");
            Assert.That(placement.ExcludedAsAllocatedElsewhere, Is.EqualTo(1), "reserved for someone else");
            Assert.That(placement.ExcludedByProcessAffinity, Is.EqualTo(1), "outside the process affinity mask");
            Assert.That(placement.ParkedAmongSelected, Is.EqualTo(1), "parked, and kept all the same");
            Assert.That(placement.Changed, Is.True);
            Assert.That(placement.PlacedOnThreadId, Is.EqualTo(os.threadId));
            Assert.That(os.setCalls, Is.EqualTo(new[] { "256,257,258,259,260,262" }), "set once, with exactly that");
        }

        /// <summary>
        /// A set reserved for this process is available: the flag says whose it is, and being reserved is only a
        /// reason to keep away from someone else's.
        /// </summary>
        [Test]
        public void ASetReservedForThisProcess_IsStillAvailable()
        {
            var os = new FakeOs();
            FillHybrid(os);
            os.records[0] = Record(256, 0, 0, 1, (byte)(Allocated | AllocatedToThisProcess));

            var placement = new MainThreadCpuSetPlacement(os);
            Assert.That(placement.Apply(), Is.EqualTo(CpuSetPlacementOutcome.Applied), placement.Problem);
            Assert.That(placement.Selected, Is.EqualTo(FastIds()));
            Assert.That(placement.ExcludedAsAllocatedElsewhere, Is.Zero);
        }

        /// <summary>A machine that is not heterogeneous needs no placement, and gets none: nothing is changed.</summary>
        [Test]
        public void OnAMachineThatIsNotHeterogeneous_NothingIsChanged()
        {
            var os = new FakeOs();
            for (byte cpu = 0; cpu < 8; cpu++)
            {
                os.records.Add(Record(256u + cpu, cpu, (byte)(cpu / 2), 0));
            }

            var placement = new MainThreadCpuSetPlacement(os);
            Assert.That(placement.Apply(), Is.EqualTo(CpuSetPlacementOutcome.NotNeeded));

            Assert.That(placement.Changed, Is.False, "nothing was changed");
            Assert.That(placement.Problem, Is.Null, "and this is not a failure");
            Assert.That(os.setCalls, Is.Empty, "nothing was set");
            Assert.That(placement.Selected, Is.Empty);
            Assert.That(placement.FastestEfficiencyClass, Is.EqualTo(placement.SlowestEfficiencyClass));

            Assert.That(placement.Restore(), Is.EqualTo(CpuSetRestoreOutcome.NotNeeded), "and nothing to put back");
            Assert.That(os.setCalls, Is.Empty);
        }

        // ----- saving and putting back --------------------------------------------------------------------------

        /// <summary>A thread that had no assignment gets none back: the assignment is cleared, not set to everything.</summary>
        [Test]
        public void AThreadThatHadNoAssignment_HasItClearedAgain()
        {
            var os = new FakeOs();
            FillHybrid(os);

            var placement = new MainThreadCpuSetPlacement(os);
            Assert.That(placement.Apply(), Is.EqualTo(CpuSetPlacementOutcome.Applied), placement.Problem);
            Assert.That(placement.OriginalAssignment, Is.Empty, "it had none");
            Assert.That(os.assignment, Is.EqualTo(FastIds()));

            Assert.That(placement.Restore(), Is.EqualTo(CpuSetRestoreOutcome.Restored));
            Assert.That(placement.Changed, Is.False);
            Assert.That(os.assignment, Is.Empty, "and has none again");
            Assert.That(os.setCalls, Is.EqualTo(new[] { "256,257,258,259,260,261,262,263", "clear" }));
        }

        /// <summary>A thread that already had an assignment gets exactly that one back.</summary>
        [Test]
        public void AThreadThatHadAnAssignment_GetsExactlyItBack()
        {
            var os = new FakeOs();
            FillHybrid(os);
            os.assignment.AddRange(new uint[] { 260, 261 });

            var placement = new MainThreadCpuSetPlacement(os);
            Assert.That(placement.Apply(), Is.EqualTo(CpuSetPlacementOutcome.Applied), placement.Problem);
            Assert.That(placement.OriginalAssignment, Is.EqualTo(new uint[] { 260, 261 }), "saved before it changed");
            Assert.That(os.assignment, Is.EqualTo(FastIds()));

            Assert.That(placement.Restore(), Is.EqualTo(CpuSetRestoreOutcome.Restored));
            Assert.That(os.assignment, Is.EqualTo(new uint[] { 260, 261 }), "exactly what it had");
            Assert.That(placement.OriginalAssignment, Is.EqualTo(new uint[] { 260, 261 }), "and it is still recorded");
        }

        /// <summary>Applying twice changes nothing and, above all, does not overwrite what was saved the first time.</summary>
        [Test]
        public void ApplyingTwice_DoesNotOverwriteWhatWasSaved()
        {
            var os = new FakeOs();
            FillHybrid(os);
            os.assignment.AddRange(new uint[] { 300 });

            var placement = new MainThreadCpuSetPlacement(os);
            Assert.That(placement.Apply(), Is.EqualTo(CpuSetPlacementOutcome.Applied), placement.Problem);
            Assert.That(placement.OriginalAssignment, Is.EqualTo(new uint[] { 300 }));
            int callsAfterFirst = os.calls.Count;

            Assert.That(placement.Apply(), Is.EqualTo(CpuSetPlacementOutcome.Applied), "the first outcome, again");
            Assert.That(os.calls.Count, Is.EqualTo(callsAfterFirst), "and not a single call was made");
            Assert.That(
                placement.OriginalAssignment, Is.EqualTo(new uint[] { 300 }),
                "the saved assignment is what the thread had before the first apply, not what it has now");
            Assert.That(os.setCalls.Count, Is.EqualTo(1), "and it was set once");

            Assert.That(placement.Restore(), Is.EqualTo(CpuSetRestoreOutcome.Restored));
            Assert.That(os.assignment, Is.EqualTo(new uint[] { 300 }));
        }

        /// <summary>An assignment belongs to its own thread: no other thread puts it back.</summary>
        [Test]
        public void AnotherThread_CannotPutThePlacementBack()
        {
            var os = new FakeOs();
            FillHybrid(os);
            var placement = new MainThreadCpuSetPlacement(os);
            Assert.That(placement.Apply(), Is.EqualTo(CpuSetPlacementOutcome.Applied), placement.Problem);

            os.threadId = 99;
            Assert.That(placement.Restore(), Is.EqualTo(CpuSetRestoreOutcome.WrongThread));
            Assert.That(placement.Changed, Is.True, "so it is still changed");
            Assert.That(os.assignment, Is.EqualTo(FastIds()), "and nothing was touched");

            os.threadId = 7;
            Assert.That(placement.Restore(), Is.EqualTo(CpuSetRestoreOutcome.Restored), "its own thread can");
            Assert.That(os.assignment, Is.Empty);
        }

        // ----- telling the failures apart -----------------------------------------------------------------------

        [Test]
        public void ATopologyThatCannotBeReadOrJudged_ChangesNothing()
        {
            var unreadable = new FakeOs { topologyReadable = false };
            var first = new MainThreadCpuSetPlacement(unreadable);
            Assert.That(first.Apply(), Is.EqualTo(CpuSetPlacementOutcome.TopologyUnavailable));
            Assert.That(first.Changed, Is.False);
            Assert.That(first.Problem, Is.Not.Null);
            Assert.That(unreadable.setCalls, Is.Empty);

            var empty = new FakeOs();
            Assert.That(
                new MainThreadCpuSetPlacement(empty).Apply(), Is.EqualTo(CpuSetPlacementOutcome.TopologyUnavailable),
                "no records is no topology");

            var groups = new FakeOs { groupCount = 2 };
            FillHybrid(groups);
            var second = new MainThreadCpuSetPlacement(groups);
            Assert.That(second.Apply(), Is.EqualTo(CpuSetPlacementOutcome.TopologyUnavailable));
            Assert.That(second.Problem, Does.Contain("processor groups"));
            Assert.That(groups.setCalls, Is.Empty, "more than one group is not judged here, and nothing is changed");

            var otherGroup = new FakeOs();
            FillHybrid(otherGroup);
            otherGroup.records[3] = new CpuSetRecord(259, 1, 3, 1, 0, 0, 1, 0);
            var third = new MainThreadCpuSetPlacement(otherGroup);
            Assert.That(third.Apply(), Is.EqualTo(CpuSetPlacementOutcome.TopologyUnavailable));
            Assert.That(otherGroup.setCalls, Is.Empty);

            var wide = new FakeOs();
            FillHybrid(wide);
            wide.records[2] = Record(258, 64, 2, 1);
            var fourth = new MainThreadCpuSetPlacement(wide);
            Assert.That(fourth.Apply(), Is.EqualTo(CpuSetPlacementOutcome.TopologyUnavailable));
            Assert.That(wide.setCalls, Is.Empty, "a home processor an affinity mask cannot describe is not judged");

            var affinity = new FakeOs { affinityReadable = false };
            FillHybrid(affinity);
            var fifth = new MainThreadCpuSetPlacement(affinity);
            Assert.That(fifth.Apply(), Is.EqualTo(CpuSetPlacementOutcome.TopologyUnavailable));
            Assert.That(affinity.setCalls, Is.Empty, "what this process may use is part of the topology question");
        }

        [Test]
        public void WhenNothingOfTheFastestClassIsAvailable_NothingIsChanged()
        {
            var os = new FakeOs();
            FillHybrid(os);
            for (int i = 0; i < 8; i++)
            {
                os.records[i] = Record((uint)(256 + i), (byte)i, (byte)(i / 2), 1, Allocated);
            }

            var placement = new MainThreadCpuSetPlacement(os);
            Assert.That(placement.Apply(), Is.EqualTo(CpuSetPlacementOutcome.NoCandidates));
            Assert.That(placement.ExcludedAsAllocatedElsewhere, Is.EqualTo(8));
            Assert.That(placement.Changed, Is.False);
            Assert.That(os.setCalls, Is.Empty);
            Assert.That(placement.Restore(), Is.EqualTo(CpuSetRestoreOutcome.NotNeeded));
        }

        [Test]
        public void WhenWhatTheThreadAlreadyHadCannotBeRead_NothingIsChanged()
        {
            var os = new FakeOs { selectedReadable = false };
            FillHybrid(os);

            var placement = new MainThreadCpuSetPlacement(os);
            Assert.That(placement.Apply(), Is.EqualTo(CpuSetPlacementOutcome.OriginalUnreadable));
            Assert.That(placement.Changed, Is.False, "nothing may be changed before it is known what to put back");
            Assert.That(os.setCalls, Is.Empty);
            Assert.That(placement.Problem, Is.Not.Null);
        }

        [Test]
        public void WhenTheAssignmentCannotBeSet_NothingWasChanged()
        {
            var os = new FakeOs { setSucceeds = false };
            FillHybrid(os);

            var placement = new MainThreadCpuSetPlacement(os);
            Assert.That(placement.Apply(), Is.EqualTo(CpuSetPlacementOutcome.ApplyFailed));
            Assert.That(placement.Changed, Is.False, "the call failed, so nothing was changed");
            Assert.That(os.setCalls.Count, Is.EqualTo(1), "it was tried once");
            Assert.That(os.assignment, Is.Empty);
            Assert.That(placement.Restore(), Is.EqualTo(CpuSetRestoreOutcome.NotNeeded), "and nothing to put back");
        }

        /// <summary>
        /// An assignment that was set but does not read back is **not** the same as one that could not be set: it
        /// changed something, which is reported apart from the outcome, and it can still be put back.
        /// </summary>
        [Test]
        public void WhenTheAssignmentDoesNotReadBack_TheChangeIsReportedSeparately()
        {
            var os = new FakeOs();
            FillHybrid(os);
            os.readbackDisagrees = true;

            var placement = new MainThreadCpuSetPlacement(os);
            Assert.That(placement.Apply(), Is.EqualTo(CpuSetPlacementOutcome.ReadbackMismatch));
            Assert.That(placement.Changed, Is.True, "something was changed even so");
            Assert.That(placement.Problem, Is.Not.Null);
            Assert.That(placement.ReadBack, Is.Not.Empty);

            os.readbackDisagrees = false;
            Assert.That(placement.Restore(), Is.EqualTo(CpuSetRestoreOutcome.Restored), "and it can be put back");
            Assert.That(os.assignment, Is.Empty);
            Assert.That(placement.Changed, Is.False);
        }

        /// <summary>A restore that fails keeps what it saved and can be tried again; it is never called a success.</summary>
        [Test]
        public void ARestoreThatFails_KeepsWhatItSaved_AndCanBeTriedAgain()
        {
            var os = new FakeOs();
            FillHybrid(os);
            os.assignment.AddRange(new uint[] { 311, 312 });

            var placement = new MainThreadCpuSetPlacement(os);
            Assert.That(placement.Apply(), Is.EqualTo(CpuSetPlacementOutcome.Applied), placement.Problem);

            os.setSucceeds = false;
            Assert.That(placement.Restore(), Is.EqualTo(CpuSetRestoreOutcome.RestoreFailed));
            Assert.That(placement.Changed, Is.True, "it is still changed");
            Assert.That(
                placement.OriginalAssignment, Is.EqualTo(new uint[] { 311, 312 }),
                "and what has to be put back is still known");

            // A readback that disagrees is its own answer, and keeps the saved assignment too.
            os.setSucceeds = true;
            os.readbackDisagrees = true;
            Assert.That(placement.Restore(), Is.EqualTo(CpuSetRestoreOutcome.ReadbackMismatch));
            Assert.That(placement.Changed, Is.True);
            Assert.That(placement.OriginalAssignment, Is.EqualTo(new uint[] { 311, 312 }));

            os.readbackDisagrees = false;
            Assert.That(placement.Restore(), Is.EqualTo(CpuSetRestoreOutcome.Restored), "and then it works");
            Assert.That(os.assignment, Is.EqualTo(new uint[] { 311, 312 }));
        }

        [Test]
        public void ThePlacementRefusesNonsense()
        {
            Assert.That(() => new MainThreadCpuSetPlacement(null), Throws.ArgumentNullException);

            var os = new FakeOs();
            var placement = new MainThreadCpuSetPlacement(os);
            Assert.That(placement.Outcome, Is.EqualTo(CpuSetPlacementOutcome.NotAttempted));
            Assert.That(placement.RestoreOutcome, Is.EqualTo(CpuSetRestoreOutcome.NotAttempted));
            Assert.That(placement.Changed, Is.False);
            Assert.That(placement.Restore(), Is.EqualTo(CpuSetRestoreOutcome.NotNeeded), "nothing to put back");
            Assert.That(
                () => new WindowsMainThreadCpuSets().TryReadSystemCpuSets(null), Throws.ArgumentNullException,
                "refused before any platform call is made");
        }

        // ----- what the code is not allowed to call --------------------------------------------------------------

        /// <summary>
        /// Every platform call the Windows side makes either reads something or is the one write it is allowed: the
        /// calling thread's own CPU Set assignment. A new read may be added freely; anything that could move another
        /// thread, the process, a hard affinity or a priority fails here, by shape and by name.
        /// </summary>
        [Test]
        public void TheWindowsSide_WritesNothingButItsOwnThreadsCpuSetAssignment()
        {
            const string theOnlyWrite = "SetThreadSelectedCpuSets";

            var platformCalls = new List<string>();
            MethodInfo[] methods = typeof(WindowsMainThreadCpuSets).GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance
                | BindingFlags.DeclaredOnly);
            foreach (MethodInfo method in methods)
            {
                if ((method.Attributes & MethodAttributes.PinvokeImpl) != 0)
                {
                    platformCalls.Add(method.Name);
                }
            }

            Assert.That(platformCalls, Contains.Item(theOnlyWrite), "the one thing it is here to set");
            foreach (string name in platformCalls)
            {
                Assert.That(
                    name.StartsWith("Get", StringComparison.Ordinal) || name == theOnlyWrite, Is.True,
                    name + " is neither a read nor the one write this is allowed to make");
            }

            // Named outright, so that adding one of them is a failure and not a judgement call.
            string[] forbidden =
            {
                "SetThreadAffinityMask",
                "SetProcessAffinityMask",
                "SetThreadGroupAffinity",
                "SetThreadIdealProcessor",
                "SetThreadIdealProcessorEx",
                "SetProcessDefaultCpuSets",
                "SetProcessDefaultCpuSetMasks",
                "SetThreadSelectedCpuSetMasks",
                "SetThreadPriority",
                "SetThreadPriorityBoost",
                "SetPriorityClass",
                "SetThreadInformation",
                "SetProcessInformation",
                "OpenThread",
                "OpenProcess",
            };
            foreach (string name in forbidden)
            {
                Assert.That(platformCalls, Has.No.Member(name), name + " must not be called from here");
            }

            // And the seam the placement is written against offers only reads and that same one setter.
            var seam = new List<string>();
            foreach (MethodInfo method in typeof(IMainThreadCpuSetOs).GetMethods())
            {
                seam.Add(method.Name);
            }

            Assert.That(
                seam,
                Is.EquivalentTo(new[]
                {
                    "get_CurrentThreadId",
                    "get_ProcessorGroupCount",
                    "TryReadSystemCpuSets",
                    "TryReadProcessAffinityMask",
                    "TryReadCurrentThreadAffinity",
                    "TryReadSelectedCpuSets",
                    "TrySetSelectedCpuSets",
                }),
                "the placement cannot do what its seam does not offer");
        }

        /// <summary>
        /// The calling thread's own affinity narrows the choice as much as the process's does: Windows respects a
        /// restrictive mask above any CPU Set assignment, so a thread already confined to part of the machine cannot
        /// be placed outside it however the assignment reads back.
        /// </summary>
        [Test]
        public void TheCallingThreadsOwnAffinity_NarrowsTheChoice()
        {
            // The process is allowed everywhere; this thread only on two of the fast processors.
            var someFast = new FakeOs { threadAffinityMask = (1UL << 2) | (1UL << 3) };
            FillHybrid(someFast);
            var placement = new MainThreadCpuSetPlacement(someFast);

            Assert.That(placement.Apply(), Is.EqualTo(CpuSetPlacementOutcome.Applied), placement.Problem);
            Assert.That(
                placement.Selected, Is.EqualTo(new uint[] { 258, 259 }),
                "only the fast sets this thread itself is allowed on");
            Assert.That(
                placement.ExcludedByThreadAffinity, Is.EqualTo(6), "the rest are the process's, not this thread's");
            Assert.That(placement.ExcludedByProcessAffinity, Is.Zero, "and the process allowed them all");

            // Confined to the slow processors: there is nothing of the fastest class left for it.
            var onlySlow = new FakeOs { threadAffinityMask = 0xF00 };
            FillHybrid(onlySlow);
            var confined = new MainThreadCpuSetPlacement(onlySlow);

            Assert.That(confined.Apply(), Is.EqualTo(CpuSetPlacementOutcome.NoCandidates));
            Assert.That(confined.Changed, Is.False, "and nothing was set");
            Assert.That(onlySlow.setCalls, Is.Empty);
            Assert.That(confined.ExcludedByThreadAffinity, Is.EqualTo(8));
            Assert.That(confined.Problem, Does.Contain("this thread's own"));

            // Unreadable, or in another group: not judged, and nothing changed.
            var unreadable = new FakeOs { threadAffinityReadable = false };
            FillHybrid(unreadable);
            var unknown = new MainThreadCpuSetPlacement(unreadable);
            Assert.That(unknown.Apply(), Is.EqualTo(CpuSetPlacementOutcome.TopologyUnavailable));
            Assert.That(unknown.Changed, Is.False);
            Assert.That(unreadable.setCalls, Is.Empty);
            Assert.That(unknown.Problem, Does.Contain("this thread's own affinity"));

            var otherGroup = new FakeOs { threadAffinityGroup = 1 };
            FillHybrid(otherGroup);
            var elsewhere = new MainThreadCpuSetPlacement(otherGroup);
            Assert.That(elsewhere.Apply(), Is.EqualTo(CpuSetPlacementOutcome.TopologyUnavailable));
            Assert.That(otherGroup.setCalls, Is.Empty);
            Assert.That(elsewhere.Problem, Does.Contain("processor group 1"));
        }
    }
}
