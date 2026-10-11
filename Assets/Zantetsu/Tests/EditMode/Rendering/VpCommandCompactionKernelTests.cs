using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// The compaction kernel itself (VpCommandCompact.compute; DESIGN 4.5.10, 4.5.11), run on the device and read
    /// back: for a run of argument entries, every command with instances is among the compacted entries once, whole
    /// and with its own number, none without instances is, the count is theirs, and nothing outside the run or past
    /// the count is written -- whatever the pattern of the visible commands and wherever the run's length falls
    /// against a wave. The count slots are zeroed as this device's route zeroes them (a copy on Direct3D 12).
    /// <para>
    /// The kernel is compiled for Vulkan and Direct3D 12 alone. On another device (the Editor on Direct3D 11) the
    /// cases that run it are ignored, and <see cref="TheKernel_IsThereOnDirect3D12AndVulkan_AndRefusedWithItsReasonElsewhere"/>
    /// says so as a result of its own.
    /// </para>
    /// </summary>
    public class VpCommandCompactionKernelTests
    {
        private const int Words = VpGpuCullSetup.NativeArgumentStride / sizeof(uint);
        private const uint Poison = 0xDEADBEEFu;
        private const uint Stale = 77u;

        private static void RequireKernel()
        {
            if (!VpCommandCompaction.TryLoad(out string failure))
            {
                Assert.Ignore("no compaction kernel on this device (" + SystemInfo.graphicsDeviceType + "): " + failure);
            }
        }

        // One source of entries with its compacted target and count slots, as a selection's column has them.
        private sealed class Harness : IDisposable
        {
            public readonly int Capacity;
            public readonly GraphicsBuffer Source, Target, Counts, Zero;
            public readonly uint[] Entries;
            private readonly CommandBuffer _commands = new CommandBuffer { name = "VP compaction kernel test" };

            public Harness(int capacity)
            {
                Capacity = capacity;
                Entries = new uint[capacity * Words];
                Source = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, capacity, VpGpuCullSetup.NativeArgumentStride) { name = "VP compaction test source" };
                Target = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, capacity, VpGpuCullSetup.NativeArgumentStride) { name = "VP compaction test target" };
                Counts = VpCommandCompaction.NewCounts("VP compaction test counts");
                Zero = VpCommandCompaction.ZeroesByCopy ? VpCommandCompaction.NewZeroSource() : null;
                var poison = new uint[capacity * Words];
                for (int i = 0; i < poison.Length; i++) poison[i] = Poison;
                Target.SetData(poison);
                var stale = new uint[VpCommandCompaction.Slots];
                for (int i = 0; i < stale.Length; i++) stale[i] = Stale;
                Counts.SetData(stale);
            }

            /// <summary>Every entry written again: its own number, five distinct arguments, and instances where <paramref name="visible"/> says.</summary>
            public void Upload(Func<int, bool> visible)
            {
                for (int c = 0; c < Capacity; c++)
                {
                    int at = c * Words;
                    Entries[at] = (uint)c;
                    Entries[at + 1] = 36u + (uint)c;
                    Entries[at + 2] = visible(c) ? 1u + (uint)(c % 5) : 0u;
                    Entries[at + 3] = 1000u + (uint)c;
                    Entries[at + 4] = 7u * (uint)c;
                    Entries[at + 5] = 500u + (uint)c * 4u;
                }

                Source.SetData(Entries);
            }

            /// <summary>One frame's commands: the count slots zeroed (unless <paramref name="zero"/> is false), then each run compacted.</summary>
            public void Frame(bool zero, params (int first, int count, int slot)[] runs)
            {
                _commands.Clear();
                if (zero)
                {
                    if (Zero != null) VpCommandCompaction.ZeroCounts(_commands, Zero, Counts);
                    else VpCommandCompaction.Reset(_commands, Counts);
                }

                foreach ((int first, int count, int slot) in runs)
                {
                    VpCommandCompaction.Compact(_commands, Source, Target, Counts, first, count, slot);
                }

                Graphics.ExecuteCommandBuffer(_commands);
            }

            public void Verify(int first, int count, int slot, int column)
            {
                _commands.Clear();
                VpCommandCompaction.Verify(_commands, Source, Target, Counts, first, count, slot, column);
                Graphics.ExecuteCommandBuffer(_commands);
            }

            public uint[] ReadCounts()
            {
                var back = new uint[VpCommandCompaction.Slots];
                Counts.GetData(back);
                return back;
            }

            public uint[] ReadTarget()
            {
                var back = new uint[Capacity * Words];
                Target.GetData(back);
                return back;
            }

            public void Dispose()
            {
                _commands.Dispose();
                Source.Dispose();
                Target.Dispose();
                Counts.Dispose();
                Zero?.Dispose();
            }
        }

        // The run [first, first + count) of the read-back target against the source: the count is the visible
        // commands'; the entries below the count are those commands, each once and whole; the entries of the run at
        // and past the count are what they were before (`before`, or the poison a new target holds). Returns the count.
        private static int AssertRun(Harness h, uint[] counts, uint[] target, uint[] before, int first, int count, int slot, string what)
        {
            var expected = new List<uint>();
            for (int c = first; c < first + count; c++) if (h.Entries[c * Words + 2] != 0u) expected.Add((uint)c);
            Assert.That(counts[slot], Is.EqualTo((uint)expected.Count), what + ": the count of slot " + slot);
            var got = new List<uint>();
            for (int k = 0; k < expected.Count; k++)
            {
                int at = (first + k) * Words;
                uint id = target[at];
                Assert.That(id, Is.GreaterThanOrEqualTo((uint)first).And.LessThan((uint)(first + count)), what + ": compacted entry " + k + " names a command of the run");
                got.Add(id);
                for (int w = 0; w < Words; w++)
                {
                    Assert.That(target[at + w], Is.EqualTo(h.Entries[(int)id * Words + w]), what + ": compacted entry " + k + " (command " + id + ") word " + w + " is the source's");
                }
            }

            got.Sort();
            Assert.That(got, Is.EqualTo(expected), what + ": every command with instances once, none without");
            for (int k = expected.Count; k < count; k++)
            {
                int at = (first + k) * Words;
                for (int w = 0; w < Words; w++)
                {
                    Assert.That(target[at + w], Is.EqualTo(before != null ? before[at + w] : Poison), what + ": entry " + k + " of the run, past the count, is not written (word " + w + ")");
                }
            }

            return expected.Count;
        }

        private static void AssertOutsideUntouched(Harness h, uint[] target, int from, int to, string what)
        {
            for (int c = from; c < to; c++)
            {
                for (int w = 0; w < Words; w++)
                {
                    Assert.That(target[c * Words + w], Is.EqualTo(Poison), what + ": entry " + c + " outside every run is not written");
                }
            }
        }

        [Test]
        public void TheKernel_IsThereOnDirect3D12AndVulkan_AndRefusedWithItsReasonElsewhere()
        {
            bool loaded = VpCommandCompaction.TryLoad(out string failure);
            GraphicsDeviceType device = SystemInfo.graphicsDeviceType;
            TestContext.Out.WriteLine("device " + device + " (" + SystemInfo.graphicsDeviceName + "): kernel " + (loaded ? "loaded" : "not loaded: " + failure));
            if (device == GraphicsDeviceType.Direct3D12 || device == GraphicsDeviceType.Vulkan)
            {
                Assert.That(loaded, Is.True, failure);
                Assert.That(failure, Is.Null);
            }
            else
            {
                Assert.That(loaded, Is.False, "wave operations are not compiled for " + device);
                Assert.That(failure, Does.Contain("VpCommandCompact"));
            }

            Assert.That(VpCommandCompaction.ZeroesByCopy, Is.EqualTo(device != GraphicsDeviceType.Vulkan), "the zero event is the Vulkan plugin's; elsewhere the slots are zeroed by a copy");
        }

        private static readonly (string name, Func<int, int, int, bool> visible)[] s_patterns =
        {
            ("all", (c, first, count) => true),
            ("none", (c, first, count) => false),
            ("every other", (c, first, count) => ((c - first) & 1) == 0),
            ("every third", (c, first, count) => (c - first) % 3 == 1),
            ("first alone", (c, first, count) => c == first),
            ("last alone", (c, first, count) => c == first + count - 1),
            ("a scattered tenth", (c, first, count) => (((uint)c * 2654435761u) >> 7) % 10u == 3u),
            ("the first half", (c, first, count) => c - first < (count + 1) / 2),
            ("the last of each 32 and the first of the next", (c, first, count) => (c - first) % 32 == 31 || (c - first) % 32 == 0),
            ("all but every 64th", (c, first, count) => (c - first) % 64 != 63),
        };

        // Lengths on both sides of every power of two up to two groups, and the static scene's run: a wave of 4, 8,
        // 16, 32, 64 or 128 lanes has its last lane, and a group of 64 threads its last thread, among them.
        private static readonly int[] s_lengths = { 1, 2, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32, 33, 47, 63, 64, 65, 95, 96, 97, 127, 128, 129, 191, 192, 193, 255, 256, 257, 1602 };

        [Test]
        public void Patterns_AllNoneScatteredAndAtWaveBoundaries_KeepEachCommandWithInstancesOnce_WholeAndInsideItsRun()
        {
            RequireKernel();
            const int first = 5, slot = 3, tail = 9;
            int cases = 0, keptMost = 0;
            foreach (int count in s_lengths)
            {
                foreach ((string name, Func<int, int, int, bool> visible) in s_patterns)
                {
                    using (var h = new Harness(first + count + tail))
                    {
                        // Commands outside the run have instances too: none of them may come into it.
                        h.Upload(c => c < first || c >= first + count || visible(c, first, count));
                        h.Frame(true, (first, count, slot));
                        uint[] counts = h.ReadCounts();
                        uint[] target = h.ReadTarget();
                        string what = name + ", " + count + " commands";
                        keptMost = Math.Max(keptMost, AssertRun(h, counts, target, null, first, count, slot, what));
                        AssertOutsideUntouched(h, target, 0, first, what);
                        AssertOutsideUntouched(h, target, first + count, h.Capacity, what);
                        for (int s = 0; s < VpCommandCompaction.Slots; s++)
                        {
                            if (s != slot) Assert.That(counts[s], Is.Zero, what + ": slot " + s + " was zeroed and is no run's");
                        }

                        cases++;
                    }
                }
            }

            TestContext.Out.WriteLine("device " + SystemInfo.graphicsDeviceType + " (" + SystemInfo.graphicsDeviceName + "): " + cases + " cases of " + s_lengths.Length + " lengths x " + s_patterns.Length
                + " patterns, first command " + first + ", slot " + slot + "; the most kept in one " + keptMost);
            Assert.That(cases, Is.EqualTo(s_lengths.Length * s_patterns.Length));
        }

        [Test]
        public void SeveralRunsOfOneSource_GoIntoTheirOwnRangesAndSlots_InOneFrame()
        {
            RequireKernel();
            // Four draw units of one selection, as four materials' commands lie one after another, the third with no
            // visible command at all; their slots are not in the runs' order and one is the last of the buffer.
            (int first, int count, int slot)[] runs = { (0, 40, 2), (40, 100, 0), (140, 37, VpCommandCompaction.Slots - 1), (177, 300, 1) };
            using (var h = new Harness(480))
            {
                h.Upload(c => c >= 140 && c < 177 ? false : (((uint)c * 2654435761u) >> 5) % 3u != 0u);
                h.Frame(true, runs);
                uint[] counts = h.ReadCounts();
                uint[] target = h.ReadTarget();
                var kept = new List<int>();
                foreach ((int first, int count, int slot) in runs) kept.Add(AssertRun(h, counts, target, null, first, count, slot, "run at " + first));
                AssertOutsideUntouched(h, target, 477, 480, "after the last run");
                TestContext.Out.WriteLine("runs kept " + string.Join(", ", kept) + " of 40, 100, 37, 300");
                Assert.That(kept[2], Is.Zero, "a run of no visible command counts none");
                Assert.That(kept[0], Is.GreaterThan(0));
                Assert.That(kept[1], Is.GreaterThan(0));
                Assert.That(kept[3], Is.GreaterThan(0));
                for (int s = 3; s < VpCommandCompaction.Slots - 1; s++) Assert.That(counts[s], Is.Zero, "slot " + s + " is no run's");
            }
        }

        [Test]
        public void FrameAfterFrame_NoCountAndNoEntryOfAnEarlierFrameIsCounted_AndAfterAFrameOfNoneTheCommandsComeBack()
        {
            RequireKernel();
            const int first = 3, count = 200, slot = 1;
            using (var h = new Harness(first + count + 4))
            {
                // Frame 1: most are visible.
                h.Upload(c => c % 7 != 0);
                h.Frame(true, (first, count, slot));
                uint[] counts = h.ReadCounts();
                uint[] target = h.ReadTarget();
                int kept1 = AssertRun(h, counts, target, null, first, count, slot, "frame 1");

                // Frame 2: none. The count is zero -- not the earlier frame's -- and no entry is written.
                uint[] before = target;
                h.Upload(c => false);
                h.Frame(true, (first, count, slot));
                counts = h.ReadCounts();
                target = h.ReadTarget();
                Assert.That(counts[slot], Is.Zero, "frame 2: no command has instances");
                Assert.That(target, Is.EqualTo(before), "frame 2: nothing is written; the entries left are past the count");

                // Frame 3: a few, others than before. The count is theirs alone.
                h.Upload(c => c % 11 == 2);
                h.Frame(true, (first, count, slot));
                counts = h.ReadCounts();
                uint[] third = h.ReadTarget();
                int kept3 = AssertRun(h, counts, third, target, first, count, slot, "frame 3");

                // Frame 4: all of them again.
                h.Upload(c => true);
                h.Frame(true, (first, count, slot));
                counts = h.ReadCounts();
                uint[] fourth = h.ReadTarget();
                int kept4 = AssertRun(h, counts, fourth, third, first, count, slot, "frame 4");
                TestContext.Out.WriteLine("kept " + kept1 + ", 0, " + kept3 + ", " + kept4 + " of " + count);
                Assert.That(kept3, Is.LessThan(kept1));
                Assert.That(kept4, Is.EqualTo(count));
            }
        }

        [Test]
        public void WithoutTheZero_ASecondCompactionAddsToTheFirstsCount_AndNeverWritesPastItsRun()
        {
            RequireKernel();
            // What the zeroing is for, and what the kernel does when it is missing: the count adds up (so a draw would
            // read entries of the earlier frame), but no entry is written past the run's end -- the next run's
            // entries and every other slot stay as they are.
            const int first = 2, count = 96, slot = 0;
            using (var h = new Harness(first + count + 6))
            {
                h.Upload(c => true);
                h.Frame(true, (first, count, slot));
                h.Frame(false, (first, count, slot));
                uint[] counts = h.ReadCounts();
                uint[] target = h.ReadTarget();
                Assert.That(counts[slot], Is.EqualTo((uint)(2 * count)), "the second compaction's waves added to the first's count");
                AssertOutsideUntouched(h, target, 0, first, "before the run");
                AssertOutsideUntouched(h, target, first + count, h.Capacity, "after the run");
                for (int s = 1; s < VpCommandCompaction.Slots; s++) Assert.That(counts[s], Is.Zero, "slot " + s);
            }
        }

        [Test]
        public void TheCheckKernel_TalliesNoMismatchForACompactedRun_AndTalliesASpoiledEntry()
        {
            RequireKernel();
            const int first = 4, count = 130, slot = 2, column = 1;
            using (var h = new Harness(first + count + 2))
            {
                h.Upload(c => c % 3 != 0);
                h.Frame(true, (first, count, slot));
                var stats = new uint[VpCommandCompaction.VerificationWords];
                var after = new uint[VpCommandCompaction.VerificationWords];
                h.Verify(first, count, slot, column);
                Assert.That(VpCommandCompaction.TryReadVerification(stats), Is.True);

                // The same run checked again, unchanged: one more dispatch, the visible commands tallied, no mismatch.
                h.Verify(first, count, slot, column);
                Assert.That(VpCommandCompaction.TryReadVerification(after), Is.True);
                int at = column * VpCommandCompaction.VerificationWordsPerColumn;
                int visible = 0;
                for (int c = first; c < first + count; c++) if (c % 3 != 0) visible++;
                Assert.That(after[at + 3] - stats[at + 3], Is.EqualTo(1u), "one dispatch");
                Assert.That(after[at + 1] - stats[at + 1], Is.EqualTo((uint)visible), "the commands with instances");
                Assert.That(after[at + 2] - stats[at + 2], Is.EqualTo((uint)visible), "the count read");
                Assert.That(after[at] - stats[at], Is.Zero, "no mismatch");

                // One compacted entry's index range spoiled, and one visible command's entry replaced by another's whole.
                uint[] target = h.ReadTarget();
                target[first * Words + 1] ^= 0x10000u;
                Array.Copy(target, (first + 2) * Words, target, (first + 1) * Words, Words);
                h.Target.SetData(target);
                Array.Copy(after, stats, stats.Length);
                h.Verify(first, count, slot, column);
                Assert.That(VpCommandCompaction.TryReadVerification(after), Is.True);
                TestContext.Out.WriteLine("spoiled: mismatched +" + (after[at] - stats[at]) + " = missing +" + (after[at + 5] - stats[at + 5]) + ", duplicated +" + (after[at + 6] - stats[at + 6])
                    + ", other fields +" + (after[at + 8] - stats[at + 8]));
                Assert.That(after[at + 8] - stats[at + 8], Is.EqualTo(1u), "the entry with another index range");
                Assert.That(after[at + 5] - stats[at + 5], Is.EqualTo(1u), "the command whose entry now names another");
                Assert.That(after[at + 6] - stats[at + 6], Is.EqualTo(1u), "the command named twice");
                Assert.That(after[at] - stats[at], Is.EqualTo(3u), "three commands mismatched");
            }
        }
    }
}
