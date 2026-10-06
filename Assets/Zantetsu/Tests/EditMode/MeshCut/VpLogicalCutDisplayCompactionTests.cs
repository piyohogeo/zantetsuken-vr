using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The compaction of the instance regions (TL, 2026-10-06; DESIGN 5.6, D-202). The regions are taken at the end
    /// and never again, so what is drawn no more leaves holes and the live records spread; a compaction lays every
    /// live region out again in the order of the frame the ordinary processing last wrote a record of each
    /// registration -- the longest unwritten first, the most recently written last -- and the tail comes down to what
    /// is live. A registration's last-written frame advances when a collection is adopted whose ordinary processing
    /// wrote a record of it (a placement update, a structural write), whatever the record held before; not by the
    /// compaction's rewrite, the catch-up between the sides, a range sent around it or a whole write of what was held.
    /// Every case runs the display's own collection and upload beside the same display made to collect everything
    /// every time (the same sides, clips, caps, commands, transforms and bounds after every collection; each one's GPU
    /// buffers read back slot by slot), and the picture before and after a compaction is compared pixel for pixel.
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        // The regions as adopted: these registrations in this order, each exactly what it draws, one after another
        // from the first record, and nothing after them.
        private static void AssertStandInOrder(VpLogicalCutDisplay display, IList<LogicalFragmentId> order, string what)
        {
            int at = 0;
            for (int i = 0; i < order.Count; i++)
            {
                Slots s = SlotsOf(display, order[i], what + ": the " + i + "th");
                Assert.That(s.instanceStart, Is.EqualTo(at), what + ": the " + i + "th of the order begins where the one before ended");
                Assert.That(s.instanceStride, Is.EqualTo(s.renderFragments), what + ": the " + i + "th's region is exactly what it draws");
                at += s.commandCount * s.instanceStride;
            }

            Assert.That(display.DrawInstanceEnd, Is.EqualTo(at), what + ": the tail is the live records");
            Assert.That(display.DrawInstanceCount, Is.EqualTo(at), what + ": and nothing else is drawn");
        }

        // No two registrations' regions share a record, and none reaches past the end.
        private static void AssertRegionsApart(VpLogicalCutDisplay display, IEnumerable<LogicalFragmentId> fragments, string what)
        {
            var regions = new List<(int start, int end)>();
            int drawn = 0;
            foreach (LogicalFragmentId fragment in fragments)
            {
                if (display.TryGetDrawSlots(fragment, out _, out int commandCount, out int instanceStart, out int instanceStride, out int renderFragments))
                {
                    regions.Add((instanceStart, instanceStart + (commandCount * instanceStride)));
                    drawn += commandCount * renderFragments;
                }
            }

            regions.Sort((a, b) => a.start.CompareTo(b.start));
            for (int i = 0; i < regions.Count; i++)
            {
                Assert.That(regions[i].end, Is.LessThanOrEqualTo(display.DrawInstanceEnd), what + ": a region ends within what was taken");
                if (i > 0)
                {
                    Assert.That(regions[i].start, Is.GreaterThanOrEqualTo(regions[i - 1].end), what + ": two regions share no record");
                }
            }

            Assert.That(drawn, Is.EqualTo(display.DrawInstanceCount), what + ": the registrations given are everything drawn");
        }

        private static int LastWritten(VpLogicalCutDisplay display, LogicalFragmentId fragment, string what)
        {
            Assert.That(display.TryGetLastWrittenFrame(fragment, out int frame), Is.True, what + ": it has a history");
            return frame;
        }

        private static int[] LastWrittenOf(VpLogicalCutDisplay display, Run run, string what)
        {
            var frames = new int[run.bodies.Count];
            for (int i = 0; i < frames.Length; i++)
            {
                frames[i] = LastWritten(display, run.bodies[i], what + ", body " + i);
            }

            return frames;
        }

        private static List<LogicalFragmentId> Bodies(Run run, params int[] which)
        {
            var list = new List<LogicalFragmentId>();
            foreach (int i in which) list.Add(run.bodies[i]);
            return list;
        }

        private Color32[] Picture(VpLogicalCutDisplay display) => Draw(display, GridCamera());

        [Test]
        public void Compaction_OrdersTheRegionsByTheFrameTheyWereLastWrittenIn_TheUnwrittenFirst_AndThePictureIsTheSame()
        {
            // Eight bodies, every one following a placement of its own (none is "static" by its kind): 0, 1, 4 and 5
            // of two commands. Bodies 1 and 4 are moved in one frame and body 2 in a later one; the rest stay.
            bool Two(int i) => i % 4 == 0 || i % 4 == 1;
            using (Twin twin = NewSlotTwin(64, 128))
            {
                Both(twin, run => { for (int i = 0; i < 8; i++) AddSlotBody(run, Grid(i), Two(i)); });
                CollectBoth(twin, "the bodies");
                int first = _frame;
                CollectBoth(twin, "settling");
                CollectBoth(twin, "settling again");
                VpLogicalCutDisplay display = twin.kept.Display;
                Assert.That(display.CompactionMinimumInterval, Is.EqualTo(300), "the product's interval (TL, 2026-10-06: 300 adopted collections, provisional)");
                Assert.That(LastWrittenOf(display, twin.kept, "at first"), Is.All.EqualTo(first), "every body was last written by the collection that first drew it");

                Both(twin, run => run.at.Put(run.bodies[1], Grid(1) * Matrix4x4.Translate(new Vector3(0f, 0.5f, 0f)))
                    .Put(run.bodies[4], Grid(4) * Matrix4x4.Translate(new Vector3(0f, 0.5f, 0f))));
                CollectBoth(twin, "bodies 1 and 4 moved");
                int earlier = _frame;
                CollectBoth(twin, "a frame between");
                Both(twin, run => run.at.Put(run.bodies[2], Grid(2) * Matrix4x4.Translate(new Vector3(0f, 0.75f, 0f))));
                CollectBoth(twin, "body 2 moved");
                int later = _frame;
                CollectBoth(twin, "at rest");
                CollectBoth(twin, "at rest again");
                var history = new[] { first, earlier, later, first, earlier, first, first, first };
                Assert.That(LastWrittenOf(display, twin.kept, "after the moves"), Is.EqualTo(history), "the moved ones were written in the frame they moved in; the frames after write nothing");
                Assert.That(display.Compactions + display.CompactionCandidates, Is.Zero, "the interval has not passed: not a candidate");
                var was = new Slots[8];
                for (int i = 0; i < 8; i++) was[i] = SlotsOf(display, twin.kept.bodies[i], "body " + i);
                int tail = display.DrawInstanceEnd;
                Assert.That(tail, Is.EqualTo(display.DrawInstanceCount), "the layout: no hole -- the order alone is what the compaction is for");
                Color32[] before = Picture(display);

                // The compaction: the still ones as they stood, then the two moved earlier, then the one moved later.
                Both(twin, run => run.Display.CompactionMinimumInterval = 1);
                Kept beforeCompaction = Kept.Of(display);
                CollectBoth(twin, "the compaction");
                Kept compaction = Kept.Of(display).Since(beforeCompaction);
                TestContext.Out.WriteLine("the compaction: " + compaction + "; " + display.DescribeCompaction());
                Assert.That(new[] { display.CompactionCandidates, display.Compactions, display.CompactionsSkipped }, Is.EqualTo(new long[] { 1, 1, 0 }), "a candidate, and run");
                AssertStandInOrder(display, Bodies(twin.kept, 0, 3, 5, 6, 7, 1, 4, 2), "after the compaction");
                Assert.That(new[] { display.LastCompactionOldestFrame, display.LastCompactionNewestFrame }, Is.EqualTo(new[] { first, later }));
                Assert.That(display.DrawInstanceEnd, Is.EqualTo(tail), "no hole before, none after");
                for (int i = 0; i < 8; i++)
                {
                    Slots now = SlotsOf(display, twin.kept.bodies[i], "body " + i);
                    Assert.That(new[] { now.commandStart, now.commandCount }, Is.EqualTo(new[] { was[i].commandStart, was[i].commandCount }), "body " + i + "'s commands stay in their slots");
                }

                Assert.That(compaction.written, Is.EqualTo(display.DrawInstanceCount), "every live record written once");
                Assert.That(compaction.instanceElements, Is.EqualTo(display.DrawInstanceCount), "and sent");
                Assert.That(compaction.instanceCalls, Is.EqualTo(2), "as one range: its transforms and its clips");
                Assert.That(compaction.commandsWritten, Is.EqualTo(display.DrawCommandCount), "every live command written, for its new start");
                Assert.That(compaction.wholeArguments + compaction.wholeInstances, Is.EqualTo(0), "nothing sent whole");
                Assert.That(display.LastCompactionSeconds, Is.GreaterThan(0.0));
                Assert.That(display.ExpectedCompactionSeconds, Is.EqualTo(display.LastCompactionSeconds), "the expected cost is the measured one from now on");
                Assert.That(LastWrittenOf(display, twin.kept, "after the compaction"), Is.EqualTo(history), "the compaction's rewrite is no ordinary write");
                Assert.That(Differing(before, Picture(display)), Is.Zero, "the picture is the one before the compaction");

                // The frame after: the other side is given the new layout; no history of that, and nothing more to do.
                beforeCompaction = Kept.Of(display);
                CollectBoth(twin, "the frame after the compaction");
                Assert.That(Kept.Of(display).Since(beforeCompaction).caughtUp, Is.EqualTo(display.DrawInstanceCount), "the other side is given every live record where it now stands");
                Assert.That(LastWrittenOf(display, twin.kept, "after the catch-up"), Is.EqualTo(history), "the catch-up copy is no ordinary write");
                CollectBoth(twin, "two frames after");
                CollectBoth(twin, "three frames after");
                Assert.That(new[] { display.CompactionCandidates, display.Compactions }, Is.EqualTo(new long[] { 1, 1 }), "no ordinary write and no hole since: not a candidate again");
                Assert.That(Differing(before, Picture(display)), Is.Zero, "and the picture stands through the sides' exchanges");

                // A body on the old side moves: updated where it stands, as ever; the next compaction puts it last.
                Slots body0 = SlotsOf(display, twin.kept.bodies[0], "body 0");
                Both(twin, run => run.at.Put(run.bodies[0], Grid(0) * Matrix4x4.Translate(new Vector3(0f, 1f, 0f))));
                CollectBoth(twin, "body 0, of the old side, moved");
                int last = _frame;
                AssertSlotsStand(display, twin.kept.bodies[0], body0, "moved, not compacted");
                Assert.That(Differing(before, Picture(display)), Is.GreaterThan(0), "it moved in the picture");
                Assert.That(LastWritten(display, twin.kept.bodies[0], "body 0"), Is.EqualTo(last));
                CollectBoth(twin, "the second compaction");
                Assert.That(display.Compactions, Is.EqualTo(2), "an ordinary write adopted since the last: a candidate, and its order moves regions");
                AssertStandInOrder(display, Bodies(twin.kept, 3, 5, 6, 7, 1, 4, 2, 0), "after the second compaction");
                CollectBoth(twin, "at rest");
            }
        }

        [Test]
        public void History_AdvancesWithEveryOrdinaryWrite_EvenOfTheSameContent_AndNotByASentRange_TheCatchUp_OrTheCompactionsRewrite()
        {
            // Eight bodies. The last is cut and the cut left published: its two clipped render fragments are placed
            // anew by every collection (a clipped render fragment is never kept as settled), so its records are written
            // every time with what they held. Nothing else is written unless it moves.
            using (Twin twin = NewTwin(8, 64, true))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                CollectBoth(twin, "the bodies");
                int first = _frame;
                CollectBoth(twin, "settling");
                CollectBoth(twin, "settling again");
                var history = new[] { first, first, first, first, first, first, first, first };
                Assert.That(LastWrittenOf(display, twin.kept, "at first"), Is.EqualTo(history), "no ordinary write since they were first drawn");

                var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                Both(twin, run => CutAndPlace(run, 7, plane, Stand(7)));
                CollectBoth(twin, "a cut published");
                history[7] = _frame;
                Assert.That(SlotsOf(display, twin.kept.bodies[7], "the cut body").renderFragments, Is.EqualTo(2), "the layout: drawn as its two sides");
                Assert.That(LastWrittenOf(display, twin.kept, "after the publication"), Is.EqualTo(history), "the structural write of the cut body, and of no other");
                CollectBoth(twin, "the frame after the publication");
                history[7] = _frame;

                // The same content written again, every collection: history each time, and of that body alone.
                for (int f = 0; f < 3; f++)
                {
                    Kept k = Kept.Of(display);
                    Drawn shape = Capture(display);
                    CollectBoth(twin, "the cut body written again " + f);
                    Assert.That(Kept.Of(display).Since(k).written, Is.GreaterThanOrEqualTo(2), "the layout: its two records were written again");
                    AssertSameShape(shape, Capture(display), "with what they held: nothing of the display changed");
                    history[7] = _frame;
                    Assert.That(LastWrittenOf(display, twin.kept, "written again " + f), Is.EqualTo(history), "the same content written again is an ordinary write; the unwritten keep theirs");
                }

                // Bodies 1 and 3 move: one range is sent that holds records nobody wrote. They are no history.
                Kept k1 = Kept.Of(display);
                Both(twin, run => run.at.Put(run.bodies[1], Stand(1, 0.5f)).Put(run.bodies[3], Stand(3, 0.5f)));
                CollectBoth(twin, "bodies 1 and 3 moved");
                Kept sent = Kept.Of(display).Since(k1);
                history[1] = _frame;
                history[3] = _frame;
                history[7] = _frame;
                Assert.That(sent.instanceElements, Is.GreaterThan(sent.written), "the layout: records nobody wrote rode in the range sent (" + sent.instanceElements + " sent, " + sent.written + " written)");
                Assert.That(LastWrittenOf(display, twin.kept, "after the move"), Is.EqualTo(history), "the three written, and not what was sent among them");

                // The next collection gives the other side the moved records: a copy, not an ordinary write.
                k1 = Kept.Of(display);
                CollectBoth(twin, "the frame after: the other side is given them");
                Assert.That(Kept.Of(display).Since(k1).caughtUp, Is.GreaterThanOrEqualTo(2), "the layout: records copied from side to side");
                history[7] = _frame;
                Assert.That(LastWrittenOf(display, twin.kept, "after the catch-up"), Is.EqualTo(history), "bodies 1 and 3 keep the frame they were written in");

                // The compaction: the unwritten as they stood, the two moved, and last the body written every time. It
                // writes every live record; that is history only for the one the ordinary processing wrote in it.
                Color32[] before = Draw(display, Looking(new Vector3(10.5f, 6f, 0f), new Vector3(0f, -1f, 0.2f), 13f));
                Both(twin, run => run.Display.CompactionMinimumInterval = 1);
                CollectBoth(twin, "the compaction");
                Assert.That(display.Compactions, Is.EqualTo(1));
                history[7] = _frame;
                AssertStandInOrder(display, Bodies(twin.kept, 0, 2, 4, 5, 6, 1, 3, 7), "the unwritten, the two that moved, the one written every time");
                Assert.That(LastWrittenOf(display, twin.kept, "after the compaction"), Is.EqualTo(history),
                    "the compaction's rewrite is no ordinary write; the cut body's update in that collection is one all the same");
                Assert.That(Differing(before, Draw(display, Looking(new Vector3(10.5f, 6f, 0f), new Vector3(0f, -1f, 0.2f), 13f))), Is.Zero, "the picture is the one before");

                // After it: the cut body is written on, where the compaction put it; the order stands.
                for (int f = 0; f < 3; f++)
                {
                    CollectBoth(twin, "after the compaction " + f);
                    history[7] = _frame;
                    Assert.That(LastWrittenOf(display, twin.kept, "after the compaction " + f), Is.EqualTo(history), "neither the catch-up nor the candidates since advance the others");
                }

                Assert.That(display.Compactions, Is.EqualTo(1), "candidates since, by the body written every time, with nothing to move");
                Assert.That(display.CompactionsWithoutChange, Is.GreaterThanOrEqualTo(1));
                AssertStandInOrder(display, Bodies(twin.kept, 0, 2, 4, 5, 6, 1, 3, 7), "the order stands");
            }
        }

        [Test]
        public void Compaction_ThenMovesAdditionsRetirementsCutsAndACommit_KeepEveryRegionItsOwn_AndAreDrawnRight()
        {
            // The interval lowered to one adopted collection: the display compacts whenever a change or a hole is
            // pending and its structure stands, all through the case. What is right is what the reference display draws
            // (compared after every collection, with each display's GPU buffers), and that no two regions share a record.
            using (Twin twin = NewTwin(10, 64, true))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                Both(twin, run => run.Display.CompactionMinimumInterval = 1);
                var live = new List<LogicalFragmentId>(twin.kept.bodies);
                void Collect(string what)
                {
                    CollectBoth(twin, what);
                    AssertRegionsApart(display, live, what);
                }

                for (int f = 0; f < 3; f++) Collect("settling " + f);
                Both(twin, run => { Assert.That(run.scene.ledger.Retire(run.bodies[2]), Is.True); Assert.That(run.scene.ledger.Retire(run.bodies[6]), Is.True); });
                live.Remove(twin.kept.bodies[2]);
                live.Remove(twin.kept.bodies[6]);
                Collect("two retired");
                Collect("the retirement's second structural collection");
                Collect("the compaction of the holes");
                Assert.That(display.Compactions, Is.GreaterThanOrEqualTo(1), "the holes were a candidate");
                Assert.That(new[] { display.DrawInstanceEnd, display.DrawInstanceCount }, Is.EqualTo(new[] { 8, 8 }), "eight live records, no hole");
                Color32[] after = Picture(display);

                // A body moved after the compaction: its record where the compaction put it; then compacted to the end.
                Both(twin, run => run.at.Put(run.bodies[3], Stand(3, 1f)));
                Collect("a body moved after the compaction");
                Assert.That(Differing(after, Picture(display)), Is.GreaterThan(0), "it moved in the picture");
                Collect("the compaction that puts it last");
                Assert.That(SlotsOf(display, twin.kept.bodies[3], "the moved body").instanceStart, Is.EqualTo(7), "the most recently changed stands last");

                // A cut published, then committed: the cut body's region of two, then the two sides' own.
                var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) made = default;
                Both(twin, run => made = CutAndPlace(run, 4, plane, Stand(4)));
                Collect("a cut published");
                Assert.That(SlotsOf(display, twin.kept.bodies[4], "the cut body").renderFragments, Is.EqualTo(2));
                Collect("the frame after the cut");
                Collect("two frames after the cut");
                _frame++;
                Both(twin, run => Assert.That(CommitBothSides(run, 4, made.cut, plane, made.positive, made.negative), Is.True, "committed"));
                live.Remove(twin.kept.bodies[4]);
                live.Add(made.positive);
                live.Add(made.negative);
                Collect("the commit");
                Collect("the frame after the commit");
                Collect("two frames after the commit");
                Assert.That(new[] { display.DrawInstanceEnd, display.DrawInstanceCount }, Is.EqualTo(new[] { 9, 9 }), "seven bodies and two sides, no hole");

                // Another body cut and left published; a body taken in; one retired; a side and a body moved.
                (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) second = default;
                Both(twin, run => second = CutAndPlace(run, 7, plane, Stand(7)));
                Collect("a second cut published");
                Collect("the frame after it");
                _frame++;
                Both(twin, run => AddBody(run, Stand(20)));
                live.Add(twin.kept.bodies[10]);
                Collect("a body taken in");
                Collect("the frame after");
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[8]), Is.True));
                live.Remove(twin.kept.bodies[8]);
                Collect("one more retired");
                Collect("its second structural collection");
                Both(twin, run => run.at.Put(made.negative, Stand(4, -1f)).Put(run.bodies[0], Stand(0, 2f)));
                Collect("a side and a body moved");
                Collect("the compaction after them");
                Collect("at rest");
                Assert.That(display.DrawInstanceEnd, Is.EqualTo(display.DrawInstanceCount), "no hole at the end");
                Assert.That(display.Compactions, Is.GreaterThanOrEqualTo(4), "compacted all through: " + display.DescribeCompaction());
                TestContext.Out.WriteLine(display.DescribeCompaction());
            }
        }

        [Test]
        public void Compaction_IsACandidateOnlyAfterTheInterval_AndOnlyWithAChangeOrAHoleSince_AndASkipKeepsItPending()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                Both(twin, run => run.Display.CompactionMinimumInterval = 5);
                void Collect(int times, string what)
                {
                    for (int i = 0; i < times; i++) CollectBoth(twin, what + " " + i);
                }

                // Five adopted collections first: not a candidate in them. In the sixth it is -- the bodies' first
                // drawing is a change -- and its order moves nothing: nothing written, and that state is not asked again.
                Collect(5, "the first five");
                Assert.That(display.CompactionCandidates, Is.Zero, "the interval has not passed");
                Kept k0 = Kept.Of(display);
                Collect(1, "the sixth");
                Assert.That(new[] { display.CompactionCandidates, display.CompactionsWithoutChange, display.Compactions }, Is.EqualTo(new long[] { 1, 1, 0 }), "a candidate with nothing to move");
                Kept none = Kept.Of(display).Since(k0);
                Assert.That(new[] { none.written, none.instanceElements, none.commandsWritten }, Is.EqualTo(new long[] { 0, 0, 0 }), "no copy and no transfer for it");
                Collect(8, "nothing changes");
                Assert.That(display.CompactionCandidateCollections, Is.EqualTo(1), "no change and no hole: not examined again, however long");

                // A change adopted: a candidate once the interval has passed since that baseline, not before.
                Both(twin, run => run.at.Put(run.bodies[3], Stand(3, 1f)));
                Collect(1, "body 3 moved");
                Assert.That(display.CompactionCandidateCollections, Is.EqualTo(1), "the change is adopted by this collection: not known to it yet");
                string refusal = "a test says no";
                double expected = double.NaN;
                int asked = 0;
                Both(twin, run => run.Display.CompactionGate = e => { if (run == twin.kept) { expected = e; asked++; } return refusal; });
                Slots body5 = SlotsOf(display, twin.kept.bodies[5], "body 5");
                Collect(1, "the frame after: a candidate, refused by the gate");
                Collect(1, "refused again");
                Assert.That(asked, Is.EqualTo(2), "asked in each");
                Assert.That(expected, Is.EqualTo(display.DrawInstanceCount * VpLogicalCutDisplay.CompactionSeedSecondsPerRecord), "with the seeded expected cost");
                Assert.That(new[] { display.CompactionCandidates, display.CompactionCandidateCollections, display.CompactionsSkipped, display.Compactions },
                    Is.EqualTo(new long[] { 2, 3, 2, 0 }), "one candidate more, in two collections, both skipped");
                Assert.That(display.LastCompactionSkipReason, Is.EqualTo(refusal));
                AssertSlotsStand(display, twin.kept.bodies[5], body5, "refused");
                TestContext.Out.WriteLine(display.DescribeCompaction());

                // Allowed: run once; what was pending is done.
                Both(twin, run => run.Display.CompactionGate = null);
                Collect(1, "allowed");
                Assert.That(display.Compactions, Is.EqualTo(1), "the skips kept it pending");
                AssertStandInOrder(display, Bodies(twin.kept, 0, 1, 2, 4, 5, 6, 7, 3), "the moved one last");

                // Another change right after: not a candidate until five adopted collections have passed.
                Both(twin, run => run.at.Put(run.bodies[6], Stand(6, 1f)));
                Collect(1, "body 6 moved");
                Collect(4, "within the interval");
                Assert.That(new[] { display.CompactionCandidates, display.Compactions }, Is.EqualTo(new long[] { 2, 1 }), "five adopted collections after the compaction: not yet");
                Collect(1, "the interval has passed");
                Assert.That(new[] { display.CompactionCandidates, display.Compactions }, Is.EqualTo(new long[] { 3, 2 }), "a candidate, and run");
                AssertStandInOrder(display, Bodies(twin.kept, 0, 1, 2, 4, 5, 7, 3, 6), "by the frame each was last written in");

                // A hole, with no change of any record: a candidate too.
                Collect(6, "at rest");
                Assert.That(display.CompactionCandidates, Is.EqualTo(3), "nothing pending");
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[1]), Is.True));
                Collect(1, "one retired");
                Collect(1, "its second structural collection");
                Assert.That(display.Compactions, Is.EqualTo(2), "the structural collections do not compact");
                Assert.That(display.DrawInstanceEnd - display.DrawInstanceCount, Is.EqualTo(1), "one hole");
                Collect(1, "the hole is a candidate");
                Assert.That(new[] { display.CompactionCandidates, display.Compactions }, Is.EqualTo(new long[] { 4, 3 }));
                AssertStandInOrder(display, Bodies(twin.kept, 0, 2, 4, 5, 7, 3, 6), "the hole is gone");
                Collect(2, "at rest");
            }
        }

        [Test]
        public void Compaction_RefusedAfterItsPlan_LeavesTheDisplayAndTheHistoryAsAdopted_AndWhatMovedInItIsHistoryOnceAdopted()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                CollectBoth(twin, "the bodies");
                int first = _frame;
                CollectBoth(twin, "settling");
                Both(twin, run => run.at.Put(run.bodies[2], Stand(2, 1f)));
                CollectBoth(twin, "body 2 moved");
                int moved = _frame;
                CollectBoth(twin, "the frame after");
                var history = new[] { first, first, moved, first, first, first, first, first };
                Assert.That(LastWrittenOf(display, twin.kept, "before"), Is.EqualTo(history));
                var was = new Slots[8];
                for (int i = 0; i < 8; i++) was[i] = SlotsOf(display, twin.kept.bodies[i], "body " + i);
                int tail = display.DrawInstanceEnd;
                Drawn before = Capture(display);
                int uploads = display.CommandUploads;
                long writes = display.HistoryRegistrationWrites;

                // A compaction is planned (body 2 to the end) in a collection in which body 5 moves, and the collection is
                // refused right after the plan: nothing of the plan stands, and body 5's move is no history.
                Both(twin, run => { run.Display.CompactionMinimumInterval = 1; run.at.Put(run.bodies[5], Stand(5, 1f)); });
                display.FailAfterCompactionPlanForTest = true;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.False, "refused");
                Assert.That(display.IsHalted, Is.False, "not a stop");
                Assert.That(display.CommandUploads, Is.EqualTo(uploads), "nothing uploaded");
                Assert.That(new[] { display.CompactionCandidates, display.Compactions }, Is.EqualTo(new long[] { 1, 0 }), "a candidate, not run");
                Assert.That(display.DrawInstanceEnd, Is.EqualTo(tail), "the tail is where it was");
                for (int i = 0; i < 8; i++) AssertSlotsStand(display, twin.kept.bodies[i], was[i], "after the refusal, body " + i);
                AssertSameShape(before, Capture(display), "what is adopted, whole");
                AssertGpuHoldsWhatIsAdopted(display, "after the refused compaction");
                Assert.That(LastWrittenOf(display, twin.kept, "after the refusal"), Is.EqualTo(history), "a write that was not adopted is no history");
                Assert.That(display.HistoryRegistrationWrites, Is.EqualTo(writes));
                display.FailAfterCompactionPlanForTest = false;

                // The next collection plans again and is adopted: body 5's move is history of that frame. The compaction
                // that was pending follows, with body 5 after body 2.
                Assert.That(twin.everything.Display.TryBeginFrame(), Is.True, "the reference collected in that frame");
                CollectBoth(twin, "the collection after the refusal plans again");
                history[5] = _frame;
                Assert.That(display.Compactions, Is.Zero, "and does not compact");
                Assert.That(LastWrittenOf(display, twin.kept, "once adopted"), Is.EqualTo(history), "body 5's move is of the collection that adopted it");
                CollectBoth(twin, "the compaction");
                Assert.That(display.Compactions, Is.EqualTo(1), "the refusal kept it pending");
                AssertStandInOrder(display, Bodies(twin.kept, 0, 1, 3, 4, 6, 7, 2, 5), "the still ones, body 2, body 5");
                Assert.That(LastWrittenOf(display, twin.kept, "after the compaction"), Is.EqualTo(history));
                CollectBoth(twin, "the frame after");
            }
        }

        [Test]
        public void Compaction_WithTheRoomGrownInTheCollection_IsNotRun_AndRunsOnceBothSidesAreWhole()
        {
            // A first room of four render fragments: the fifth body grows the room; the batch is 64 wide and stands.
            using (Twin twin = NewTwin(4, 4, false, true))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                CollectBoth(twin, "the bodies");
                int first = _frame;
                for (int f = 0; f < 2; f++) CollectBoth(twin, "settling " + f);
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[1]), Is.True));
                CollectBoth(twin, "one retired");
                Both(twin, run => run.Display.CompactionMinimumInterval = 1);

                // One hole: the next collection would be a candidate, but five more bodies arrive with it (structural, the
                // room grown): no compaction in it. The one after makes the other side larger and writes it whole: a
                // candidate, skipped. The one after that compacts.
                _frame++;
                Kept k = Kept.Of(display);
                Both(twin, run => { for (int i = 4; i < 9; i++) AddBody(run, Stand(i)); });
                CollectBoth(twin, "five more: the room grown");
                int added = _frame;
                Kept grown = Kept.Of(display).Since(k);
                TestContext.Out.WriteLine("the room grown: " + grown);
                Assert.That(display.RoomGrowths, Is.GreaterThan(0), "the layout: the room grew");
                Assert.That(display.CompactionCandidates, Is.Zero, "not in the structural collection that grew the room");

                // The side was written whole in the larger room -- the bodies held before with what they held: no
                // history of that. The five new ones are of this collection.
                Assert.That(grown.written, Is.GreaterThanOrEqualTo(8), "the layout: every live record was written, the held ones among them");
                var history = new[] { first, int.MinValue, first, first, added, added, added, added, added };
                foreach (int i in new[] { 0, 2, 3, 4, 5, 6, 7, 8 })
                {
                    Assert.That(LastWritten(display, twin.kept.bodies[i], "body " + i), Is.EqualTo(history[i]), "after the growth, body " + i + ": a whole write of what was held is no history");
                }

                // The frame after: the other side is made larger and written whole -- and body 0 moves in that very
                // collection. Its update is an ordinary write although the whole write wrote its record; the rest is not.
                Both(twin, run => run.at.Put(run.bodies[0], Stand(0, 1f)));
                k = Kept.Of(display);
                CollectBoth(twin, "the frame after: the other side is made larger and written whole, and body 0 moves");
                history[0] = _frame;
                Assert.That(Kept.Of(display).Since(k).written, Is.GreaterThanOrEqualTo(8), "the layout: the side was written whole");
                Assert.That(new[] { display.CompactionCandidates, display.CompactionsSkipped, display.Compactions }, Is.EqualTo(new long[] { 1, 1, 0 }), "a candidate, not run");
                Assert.That(display.LastCompactionSkipReason, Is.EqualTo("the side is written whole anyway"));
                foreach (int i in new[] { 0, 2, 3, 4, 5, 6, 7, 8 })
                {
                    Assert.That(LastWritten(display, twin.kept.bodies[i], "body " + i), Is.EqualTo(history[i]), "in the whole write, body " + i + ": only what the ordinary processing updated is history");
                }

                CollectBoth(twin, "two frames after");
                Assert.That(display.Compactions, Is.EqualTo(1), "compacted once the structure stood and both sides were whole");
                AssertStandInOrder(display, Bodies(twin.kept, 2, 3, 4, 5, 6, 7, 8, 0), "the two never written since, the five added, the one that moved");
                CollectBoth(twin, "at rest");
            }
        }

        /// <summary>
        /// A measurement for the record, not a judgement: a compaction of a few hundred live records -- the size a city
        /// walk reaches -- with a history of three waves of moves, on one display: its time from the decision to the
        /// adoption and of that the order, the build and the upload, and what the history cost in comparisons. The
        /// Editor's (Mono) value, not the Player's.
        /// </summary>
        [Test]
        public void Compaction_OfAFewHundredRecords_IsMeasuredForTheRecord()
        {
            const int bodies = 320, retired = 64;
            var run = new Run();
            run.scene = NewReservedScene(1024, 1024, 1024, 64, new VpLogicalCutDisplayLimits(1024, 1024, 1024, 64, 1024, 1024), null, bodies);
            Assert.That(run.scene, Is.Not.Null, "the display was made");
            using (run.scene)
            {
                VpLogicalCutDisplay display = run.Display;
                display.Placement = run.at;
                _frame++;
                Matrix4x4 At(int i) => Matrix4x4.Translate(new Vector3(3f * (i % 20), 0f, 3f * (i / 20)));
                for (int i = 0; i < bodies; i++) AddSlotBody(run, At(i), false);

                void Settle(string what)
                {
                    _frame++;
                    Assert.That(display.TryBeginFrame(), Is.True, what + ": collected");
                }

                Settle("the bodies");
                Settle("the frame after");
                long noted = display.HistoryRegistrationWrites;
                var watch = new System.Diagnostics.Stopwatch();
                int movedRecords = 0;
                for (int wave = 0; wave < 3; wave++)
                {
                    for (int i = wave; i < bodies; i += 9)
                    {
                        run.at.Put(run.bodies[i], At(i) * Matrix4x4.Translate(new Vector3(0f, 0.25f * (wave + 1), 0f)));
                        movedRecords++;
                    }

                    watch.Start();
                    Settle("a wave of moves");
                    watch.Stop();
                    Settle("the frame after the wave");
                }

                long notedByMoves = display.HistoryRegistrationWrites - noted;
                for (int i = 0; i < retired; i++) Assert.That(run.scene.ledger.Retire(run.bodies[(i * 5) % bodies]), Is.True);
                Settle("retired");
                Settle("the retirement's second structural collection");
                Assert.That(display.Compactions, Is.Zero, "the layout: not yet (the interval is the product's)");
                Camera camera = Looking(new Vector3(28.5f, 60f, 22f), Vector3.down, 32f);
                Color32[] before = Draw(display, camera);
                display.CompactionMinimumInterval = 1;
                Kept kept = Kept.Of(display);
                Settle("the compaction");
                kept = Kept.Of(display).Since(kept);
                Assert.That(display.Compactions, Is.EqualTo(1), "compacted");
                int records = display.LastCompactionRecords;
                double us = display.LastCompactionSeconds * 1e6;
                TestContext.Out.WriteLine(
                    "COMPACTION COST (Editor, Mono): " + records + " live records of " + display.LastCompactionRegistrations + " registrations, last written in frames "
                    + display.LastCompactionOldestFrame + ".." + display.LastCompactionNewestFrame + ", laid out in " + us.ToString("F1") + " us from the decision to the adoption = "
                    + (us / records).ToString("F3") + " us a record; of that the order " + (display.LastCompactionOrderSeconds * 1e6).ToString("F1") + " us, the build (stage 5) "
                    + (display.LastCompactionWriteSeconds * 1e6).ToString("F1") + " us, the upload (stage 7) " + (display.LastCompactionUploadSeconds * 1e6).ToString("F1")
                    + " us; the seed is " + (VpLogicalCutDisplay.CompactionSeedSecondsPerRecord * 1e6).ToString("F2") + " us a record; " + kept);
                TestContext.Out.WriteLine(
                    "HISTORY (Editor, Mono): " + movedRecords + " records moved in three collections, " + notedByMoves + " registrations' writes noted for them (a note at the write,"
                    + " no comparison); the three collections took " + (watch.Elapsed.TotalMilliseconds * 1000.0).ToString("F1") + " us whole; "
                    + display.DescribeCompaction());
                AssertGpuHoldsWhatIsAdopted(display, "after the compaction");
                Assert.That(Differing(before, Draw(display, camera)), Is.Zero, "the picture is the one before");
                Settle("the frame after the compaction");
                Assert.That(Differing(before, Draw(display, camera)), Is.Zero, "and through the sides' exchange");

                // Two more, each after another wave of moves: the first compaction of a process pays for things the
                // later ones do not (in the Editor, the compilation of what it calls first), so its time alone is not
                // the work's.
                for (int round = 0; round < 2; round++)
                {
                    for (int i = 1 + round; i < bodies; i += 7)
                    {
                        run.at.Put(run.bodies[i], At(i) * Matrix4x4.Translate(new Vector3(0f, 1.5f + round, 0f)));
                    }

                    Settle("another wave of moves");
                    kept = Kept.Of(display);
                    Settle("the next compaction");
                    kept = Kept.Of(display).Since(kept);
                    Assert.That(display.Compactions, Is.EqualTo(2 + round), "compacted again");
                    us = display.LastCompactionSeconds * 1e6;
                    TestContext.Out.WriteLine(
                        "COMPACTION COST, compaction " + (2 + round) + " (Editor, Mono): " + display.LastCompactionRecords + " live records, " + us.ToString("F1")
                        + " us from the decision to the adoption = " + (us / display.LastCompactionRecords).ToString("F3") + " us a record; of that the order "
                        + (display.LastCompactionOrderSeconds * 1e6).ToString("F1") + " us, the build (stage 5) " + (display.LastCompactionWriteSeconds * 1e6).ToString("F1")
                        + " us, the upload (stage 7) " + (display.LastCompactionUploadSeconds * 1e6).ToString("F1") + " us; records written " + kept.written + ", sent "
                        + kept.instanceElements + " in " + kept.instanceCalls + " SetData calls");
                    long caught = display.InstanceRecordsCaughtUp;
                    var copy = System.Diagnostics.Stopwatch.StartNew();
                    Settle("the frame after: the other side is given the layout");
                    copy.Stop();
                    TestContext.Out.WriteLine(
                        "   the collection after it: " + (display.InstanceRecordsCaughtUp - caught) + " records copied from side to side; that whole collection took "
                        + (copy.Elapsed.TotalMilliseconds * 1000.0).ToString("F1") + " us (the copy is a part of it, not measured apart)");
                }
            }
        }
    }
}
