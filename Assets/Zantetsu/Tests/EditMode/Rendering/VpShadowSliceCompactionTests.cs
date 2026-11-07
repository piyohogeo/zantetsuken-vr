using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace Zantetsu.Rendering.Tests
{
    public class VpShadowSliceCompactionTests
    {
        private const int EntryWords = VpGpuCullSetup.NativeArgumentStride / sizeof(uint);
        private const uint Poison = 0xDEADBEEFu;
        private const int Frame = 100;
        private static readonly (int first, int count, int slot)[] Runs = { (3, 65, 7), (69, 8, 9) };

        [Test]
        public void FourCascades_CompactTheirOwnRuns_WithOriginalIdsAndEveryArgumentPreserved()
        {
            RequireDirect3D12();
            using (var batch = Batch(out VpIndirectCommand[] input, out Matrix4x4[] transforms))
            using (var commands = new CommandBuffer())
            using (GraphicsBuffer zero = VpCommandCompaction.NewZeroSource())
            {
                Assert.That(batch.TryUpload(input, transforms, true), Is.True);
                var all = new VpCullConditions();
                all.KeepEverything();
                batch.IssueCull(commands, 0, all);
                var output = batch.IssueShadowSliceCull(commands, 0, Slices(), new Vector4[4], 4);
                long beforeCompaction = batch.GpuBytes;
                var targets = new HashSet<GraphicsBuffer>();
                var counts = new HashSet<GraphicsBuffer>();
                for (int split = 0; split < 4; split++)
                {
                    var set = batch.ShadowSliceCompactionOf(0, split, Frame);
                    Assert.That(set.Entries(0), Is.Null, "a shadow-only set allocates its caster column");
                    Assert.That(set.Counts(0), Is.Null);
                    Assert.That(targets.Add(set.Entries(1)), Is.True, "every cascade has its own compacted entries");
                    Assert.That(counts.Add(set.Counts(1)), Is.True, "every cascade has its own count slots");
                    Fill(set.Entries(1), Poison);
                    Fill(set.Counts(1), 77u);
                    Compact(batch, commands, zero, output, 0, split, Frame);
                    Assert.That(batch.ShadowSliceCompactionOf(0, split, Frame + 17), Is.SameAs(set), "the D3D12 GPU-only set is reused");
                }

                long perSet = batch.CommandCapacity * (long)VpGpuCullSetup.NativeArgumentStride + VpCommandCompaction.Slots * sizeof(uint);
                Assert.That(batch.GpuBytes, Is.EqualTo(beforeCompaction + 4 * perSet));
                Graphics.ExecuteCommandBuffer(commands);
                for (int split = 0; split < 4; split++)
                {
                    var set = batch.ShadowSliceCompactionOf(0, split, Frame);
                    AssertCompactedRuns(output.Arguments(split), set.Entries(1), set.Counts(1), null);
                }
            }
        }

        [Test]
        public void AZeroSelectionAndReappearance_ReuseTheSet_WithoutChangingAnotherView()
        {
            RequireDirect3D12();
            using (var batch = Batch(out VpIndirectCommand[] input, out Matrix4x4[] transforms))
            using (var commands = new CommandBuffer())
            using (GraphicsBuffer zero = VpCommandCompaction.NewZeroSource())
            {
                Assert.That(batch.TryUpload(input, transforms, false), Is.True);
                var all = new VpCullConditions();
                all.KeepEverything();
                batch.IssueCull(commands, 0, all);
                var first = batch.IssueShadowSliceCull(commands, 0, Slices(), new Vector4[4], 4);
                batch.IssueCull(commands, 1, all);
                var second = batch.IssueShadowSliceCull(commands, 1, Slices(), new Vector4[4], 4);
                for (int split = 0; split < 4; split++)
                {
                    var firstSet = batch.ShadowSliceCompactionOf(0, split, Frame);
                    var secondSet = batch.ShadowSliceCompactionOf(1, split, Frame);
                    Assert.That(firstSet.Entries(1), Is.Not.SameAs(secondSet.Entries(1)));
                    Assert.That(firstSet.Counts(1), Is.Not.SameAs(secondSet.Counts(1)));
                    Fill(firstSet.Entries(1), Poison);
                    Fill(secondSet.Entries(1), Poison);
                    Compact(batch, commands, zero, first, 0, split, Frame);
                    Compact(batch, commands, zero, second, 1, split, Frame);
                }

                Graphics.ExecuteCommandBuffer(commands);
                var firstBefore = new uint[4][];
                var secondBefore = new uint[4][];
                var secondCounts = new uint[4][];
                for (int split = 0; split < 4; split++)
                {
                    var firstSet = batch.ShadowSliceCompactionOf(0, split, Frame);
                    var secondSet = batch.ShadowSliceCompactionOf(1, split, Frame);
                    AssertCompactedRuns(first.Arguments(split), firstSet.Entries(1), firstSet.Counts(1), null);
                    AssertCompactedRuns(second.Arguments(split), secondSet.Entries(1), secondSet.Counts(1), null);
                    firstBefore[split] = Read(firstSet.Entries(1));
                    secondBefore[split] = Read(secondSet.Entries(1));
                    secondCounts[split] = Read(secondSet.Counts(1));
                }

                long allocated = batch.GpuBytes;
                commands.Clear();
                var excluded = new VpCullConditions { shadowSplitCount = 1, shadowPlaneCounts = new Vector4(1f, 0f, 0f, 0f) };
                excluded.SetShadowPlane(0, 0, new Plane(Vector3.right, -10000f));
                batch.IssueCull(commands, 0, excluded);
                first = batch.IssueShadowSliceCull(commands, 0, Slices(), new Vector4[4], 4);
                for (int split = 0; split < 4; split++) Compact(batch, commands, zero, first, 0, split, Frame + 1);
                Graphics.ExecuteCommandBuffer(commands);
                for (int split = 0; split < 4; split++)
                {
                    var set = batch.ShadowSliceCompactionOf(0, split, Frame + 1);
                    AssertCompactedRuns(first.Arguments(split), set.Entries(1), set.Counts(1), firstBefore[split]);
                    foreach (var run in Runs) Assert.That(Read(set.Counts(1))[run.slot], Is.Zero, "an empty frame overwrites the previous count");
                    Assert.That(Read(batch.ShadowSliceCompactionOf(1, split, Frame).Entries(1)), Is.EqualTo(secondBefore[split]));
                    Assert.That(Read(batch.ShadowSliceCompactionOf(1, split, Frame).Counts(1)), Is.EqualTo(secondCounts[split]));
                }

                for (int i = 0; i < transforms.Length; i++) transforms[i] = Matrix4x4.Translate(new Vector3(-transforms[i].m03, 0f, 5f));
                Assert.That(batch.TryUpload(input, transforms, false), Is.True);
                commands.Clear();
                batch.IssueCull(commands, 0, all);
                first = batch.IssueShadowSliceCull(commands, 0, Slices(), new Vector4[4], 4);
                for (int split = 0; split < 4; split++) Compact(batch, commands, zero, first, 0, split, Frame + 2);
                Graphics.ExecuteCommandBuffer(commands);
                for (int split = 0; split < 4; split++)
                {
                    var set = batch.ShadowSliceCompactionOf(0, split, Frame + 2);
                    AssertCompactedRuns(first.Arguments(split), set.Entries(1), set.Counts(1), firstBefore[split]);
                }

                Assert.That(batch.GpuBytes, Is.EqualTo(allocated), "the zero frame and the moved frame allocate no new sets");
            }
        }

        [Test]
        public void RetiringTheBatch_GivesUpEveryCascadeEntriesAndCountExactlyOnce()
        {
            RequireDirect3D12();
            var batch = Batch(out _, out _);
            var buffers = new List<GraphicsBuffer>();
            var sets = new List<VpIndexedIndirectDrawBatch.Compaction>();
            var released = new List<GraphicsBuffer>();
            try
            {
                for (int split = 0; split < 4; split++)
                {
                    var set = batch.ShadowSliceCompactionOf(0, split, Frame);
                    sets.Add(set);
                    buffers.Add(set.Entries(1));
                    buffers.Add(set.Counts(1));
                }

                batch.Dispose(buffer => { released.Add(buffer); buffer.Dispose(); });
                batch.Dispose(buffer => { released.Add(buffer); buffer.Dispose(); });
                foreach (GraphicsBuffer buffer in buffers) Assert.That(released.FindAll(item => ReferenceEquals(item, buffer)).Count, Is.EqualTo(1));
                foreach (var set in sets)
                {
                    Assert.That(set.Entries(1), Is.Null);
                    Assert.That(set.Counts(1), Is.Null);
                }
            }
            finally { batch.Dispose(); }
        }

        private static void RequireDirect3D12()
        {
            if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12) Assert.Ignore("cascade command compaction requires Direct3D12");
            Assert.That(VpCommandCompaction.TryLoad(out string failure), Is.True, failure);
        }

        private static VpIndexedIndirectDrawBatch Batch(out VpIndirectCommand[] input, out Matrix4x4[] transforms)
        {
            Assert.That(VpGpuCullSetup.TryCreate(2, true, out VpGpuCullSetup setup, out string failure), Is.True, failure);
            var batch = new VpIndexedIndirectDrawBatch(80, 160, null, setup);
            batch.WriteWholeOnce();
            input = new VpIndirectCommand[79];
            var placements = new List<Matrix4x4>();
            for (int c = 0; c < input.Length; c++)
            {
                int count = c % 7 == 0 ? 0 : 1 + c % 2;
                input[c] = new VpIndirectCommand(new VpGeometryRange(c * 3, 3, 1000 + c * 3, 3), new Bounds(Vector3.zero, Vector3.one * 0.2f), count);
                for (int i = 0; i < count; i++) placements.Add(Matrix4x4.Translate(new Vector3(-4.5f + 3f * ((c + i) % 4), 0f, 5f)));
            }

            transforms = placements.ToArray();
            return batch;
        }

        private static Matrix4x4[] Slices() => new[]
        {
            Matrix4x4.Ortho(-6f, -3f, -1f, 1f, 0.1f, 10f),
            Matrix4x4.Ortho(-3f, 0f, -1f, 1f, 0.1f, 10f),
            Matrix4x4.Ortho(0f, 3f, -1f, 1f, 0.1f, 10f),
            Matrix4x4.Ortho(3f, 6f, -1f, 1f, 0.1f, 10f),
        };

        private static void Compact(VpIndexedIndirectDrawBatch batch, CommandBuffer commands, GraphicsBuffer zero,
            VpIndexedIndirectDrawBatch.ShadowSliceOutput output, int view, int split, int frame)
        {
            var set = batch.ShadowSliceCompactionOf(view, split, frame);
            VpCommandCompaction.ZeroCounts(commands, zero, set.Counts(1));
            set.MarkZeroReserved(1, frame);
            foreach (var run in Runs) batch.IssueCompaction(commands, set, output.Arguments(split), 1, run.first, run.count, run.slot, frame);
        }

        private static uint[] Read(GraphicsBuffer buffer)
        {
            var result = new uint[buffer.count * buffer.stride / sizeof(uint)];
            buffer.GetData(result);
            return result;
        }

        private static void Fill(GraphicsBuffer buffer, uint value)
        {
            var data = new uint[buffer.count * buffer.stride / sizeof(uint)];
            for (int i = 0; i < data.Length; i++) data[i] = value;
            buffer.SetData(data);
        }

        private static void AssertCompactedRuns(GraphicsBuffer sourceBuffer, GraphicsBuffer targetBuffer, GraphicsBuffer countBuffer, uint[] before)
        {
            uint[] source = Read(sourceBuffer);
            uint[] target = Read(targetBuffer);
            uint[] counts = Read(countBuffer);
            var written = new HashSet<int>();
            foreach (var run in Runs)
            {
                var expected = new List<uint>();
                for (int c = run.first; c < run.first + run.count; c++) if (source[c * EntryWords + 2] != 0) expected.Add((uint)c);
                Assert.That(counts[run.slot], Is.EqualTo((uint)expected.Count));
                var actual = new List<uint>();
                for (int at = 0; at < expected.Count; at++)
                {
                    int entry = run.first + at;
                    written.Add(entry);
                    uint original = target[entry * EntryWords];
                    Assert.That(original, Is.GreaterThanOrEqualTo((uint)run.first).And.LessThan((uint)(run.first + run.count)));
                    actual.Add(original);
                    for (int word = 0; word < EntryWords; word++) Assert.That(target[entry * EntryWords + word], Is.EqualTo(source[(int)original * EntryWords + word]));
                }

                actual.Sort();
                Assert.That(actual, Is.EqualTo(expected), "every retained original command appears once");
            }

            for (int c = 0; c < targetBuffer.count; c++)
            {
                if (written.Contains(c)) continue;
                for (int word = 0; word < EntryWords; word++) Assert.That(target[c * EntryWords + word], Is.EqualTo(before == null ? Poison : before[c * EntryWords + word]), "outside a compacted prefix");
            }
        }
    }
}
