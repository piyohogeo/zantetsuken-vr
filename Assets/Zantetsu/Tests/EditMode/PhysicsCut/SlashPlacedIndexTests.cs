using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;

namespace Zantetsu.PhysicsCut.Tests
{
    /// <summary>
    /// The tree of the placed targets' boxes by itself (2026-10-05; DESIGN 19.1.7, D-192): a search finds exactly the
    /// entries that testing every entry's box would find -- each once, none left out -- whatever was added, given
    /// another box or taken away since the tree was built; and a box that is not finite or is inside out is not taken.
    /// The boxes are of every kind the split has to cope with: many on one line (equal centres on an axis), points,
    /// long ones, some as large as the whole field.
    /// </summary>
    public sealed class SlashPlacedIndexTests
    {
        private sealed class Entry
        {
            public int id, slot;
            public float3 lo, hi;
        }

        // What the index says it keeps: the entry's box widened by the pad, against the asked box, with the margin.
        private static bool Kept(Entry e, float3 lo, float3 hi, float reach)
        {
            float3 elo = e.lo, ehi = e.hi;
            float pad = 1e-3f * SlashPlacedIndex<Entry>.Reach(elo, ehi) + 1e-3f;
            elo -= pad;
            ehi += pad;
            float m = SlashPlacedIndex<Entry>.Margin(reach, SlashPlacedIndex<Entry>.Reach(elo, ehi));
            return !(math.any(hi < elo - m) || math.any(ehi + m < lo));
        }

        // Plainly touching or overlapping, with no pad and no margin: what must never be left out.
        private static bool Overlaps(Entry e, float3 lo, float3 hi) => !(math.any(hi < e.lo) || math.any(e.hi < lo));

        [Test]
        public void ASearch_FindsExactlyWhatTestingEveryBoxFinds_ThroughAddsChangesAndRemovals()
        {
            var random = new System.Random(20261005);
            var index = new SlashPlacedIndex<Entry>();
            var entries = new List<Entry>();
            Entry[] found = null;
            int nextId = 0, searches = 0, foundInAll = 0, mustFind = 0;
            float Next(float span) => (float)(random.NextDouble() * span);

            (float3 lo, float3 hi) Box()
            {
                // on a 4 m grid (many equal centres on an axis), or anywhere; a point, a large one, or a prop's size
                float3 c = random.Next(3) == 0
                    ? new float3(Next(400f) - 200f, Next(40f) - 20f, Next(400f) - 200f)
                    : new float3(4f * random.Next(60) - 120f, 0f, 4f * random.Next(60) - 120f);
                int kind = random.Next(10);
                float3 half = kind == 0 ? new float3(Next(80f), Next(30f), Next(80f))
                    : kind == 1 ? float3.zero
                    : new float3(0.2f + Next(1.5f), 0.2f + Next(1f), 0.2f + Next(1.5f));
                return (c - half, c + half);
            }

            void Add()
            {
                (float3 lo, float3 hi) = Box();
                var e = new Entry { id = nextId++, lo = lo, hi = hi };
                Assert.That(index.TryAdd(e, lo, hi, out e.slot), Is.True);
                entries.Add(e);
            }

            void Remove(Entry e)
            {
                Entry moved = index.RemoveAt(e.slot, out int movedTo);
                if (moved != null) moved.slot = movedTo;
                entries.Remove(e);
            }

            void Check(int queries)
            {
                for (int q = 0; q < queries; q++)
                {
                    float3 c = new float3(Next(440f) - 220f, Next(50f) - 25f, Next(440f) - 220f);
                    int kind = random.Next(8);
                    float3 half = kind == 0 ? new float3(500f) : kind == 1 ? float3.zero : kind == 2 ? new float3(Next(60f), Next(20f), Next(60f)) : new float3(Next(3f), Next(1f), Next(3f));
                    if (kind == 3 && entries.Count > 0)
                    {
                        // touching an entry's box exactly, at its upper corner
                        Entry e = entries[random.Next(entries.Count)];
                        c = e.hi + half;
                    }

                    float3 lo = c - half, hi = c + half;
                    float reach = SlashPlacedIndex<Entry>.Reach(lo, hi);
                    int n = index.Search(lo, hi, reach, ref found);
                    int[] got = found.Take(n).Select(e => e.id).OrderBy(i => i).ToArray();
                    int[] expected = entries.Where(e => Kept(e, lo, hi, reach)).Select(e => e.id).OrderBy(i => i).ToArray();
                    if (!got.SequenceEqual(expected))
                    {
                        Assert.Fail("search " + searches + " over " + entries.Count + " entries, box " + lo + " .. " + hi + ": testing every box finds [" + string.Join(" ", expected) + "], the tree found [" + string.Join(" ", got) + "]");
                    }

                    foreach (Entry e in entries)
                    {
                        if (!Overlaps(e, lo, hi)) continue;
                        mustFind++;
                        Assert.That(System.Array.BinarySearch(got, e.id), Is.GreaterThanOrEqualTo(0), "an entry whose box the asked box touches is found");
                    }

                    searches++;
                    foundInAll += n;
                }
            }

            Assert.That(index.Search(new float3(-1f), new float3(1f), 1f, ref found), Is.EqualTo(0), "nothing held: nothing found");
            Add();
            Check(20);
            for (int i = 0; i < 4; i++) Add();   // one leaf's worth, then past it
            Check(20);
            Add();
            Check(20);
            while (entries.Count < 700) Add();
            Check(300);
            long builds = index.Builds;
            Check(50);
            Assert.That(index.Builds, Is.EqualTo(builds), "nothing added or changed: the tree is not built again");

            for (int op = 0; op < 600; op++)
            {
                int what = random.Next(4);
                if (what == 0 && entries.Count > 0)
                {
                    Entry e = entries[random.Next(entries.Count)];
                    (float3 lo, float3 hi) = Box();
                    Assert.That(index.TryChange(e.slot, lo, hi), Is.True);
                    e.lo = lo;
                    e.hi = hi;
                }
                else if (what == 1 && entries.Count > 0)
                {
                    Remove(entries[random.Next(entries.Count)]);
                }
                else if (what == 2 && entries.Count > 0)
                {
                    // several taken away with the tree as it is: cleared in place, searched all the same
                    index.Search(new float3(-1f), new float3(1f), 1f, ref found);
                    for (int k = 0; k < 3 && entries.Count > 0; k++) Remove(entries[random.Next(entries.Count)]);
                }
                else
                {
                    Add();
                }

                Assert.That(index.Count, Is.EqualTo(entries.Count));
                Check(4);
            }

            // taken away one by one down to none, the tree searched all the way
            while (entries.Count > 0)
            {
                Remove(entries[random.Next(entries.Count)]);
                Check(entries.Count % 16 == 0 ? 6 : 1);
            }

            Assert.That(index.Count, Is.EqualTo(0));
            Assert.That(index.Search(new float3(-500f), new float3(500f), 500f, ref found), Is.EqualTo(0), "none left: nothing found");
            for (int i = 0; i < 60; i++) Add();
            Check(60);
            TestContext.Out.WriteLine("searches " + searches + ", entries found in all " + foundInAll + ", of them plainly touching the asked box " + mustFind + "; builds " + index.Builds);
            Assert.That(mustFind, Is.GreaterThan(1000), "the searches met entries");
        }

        [Test]
        public void ABoxThatIsNotFiniteOrIsInsideOut_IsNotTaken()
        {
            var index = new SlashPlacedIndex<Entry>();
            var e = new Entry();
            Assert.That(index.TryAdd(e, new float3(float.NaN, 0f, 0f), new float3(1f), out int slot), Is.False, "NaN");
            Assert.That(slot, Is.EqualTo(-1));
            Assert.That(index.TryAdd(e, new float3(0f), new float3(float.PositiveInfinity, 1f, 1f), out _), Is.False, "infinite");
            Assert.That(index.TryAdd(e, new float3(1f, 0f, 0f), new float3(0f, 1f, 1f), out _), Is.False, "inside out");
            Assert.That(index.TryAdd(e, new float3(-float.MaxValue), new float3(float.MaxValue), out _), Is.False, "too large to be widened");
            Assert.That(index.TryAdd(null, new float3(0f), new float3(1f), out _), Is.False, "nothing to hold");
            Assert.That(index.Count, Is.EqualTo(0));
            Assert.That(index.TryAdd(e, new float3(0f), new float3(1f), out slot), Is.True);
            Assert.That(index.TryChange(slot, new float3(float.NaN), new float3(1f)), Is.False, "not given a box that is not finite");
            Entry[] found = null;
            Assert.That(index.Search(new float3(0.4f), new float3(0.6f), 1f, ref found), Is.EqualTo(1), "it keeps the box it had");
            Assert.That(index.TryChange(slot + 1, new float3(0f), new float3(1f)), Is.False, "no such entry");
        }
    }
}
