using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The instance records' one range (TL, 2026-10-06; DESIGN 5.6). What a collection wrote of the transforms and the
    /// clips is sent as one contiguous range -- from the first record written to past the last, whatever lies between
    /// -- with one buffer write for the transforms and one for the clips, and nothing when nothing was written. The
    /// side being built is given the whole range it lacks of the adopted one first, so every record of the range sent
    /// is current. Every case runs the display's own collection and upload beside the same display made to collect
    /// everything every time, and reads the GPU's buffers back slot by slot after every collection.
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        // What one collection (or a few) did to the instance records: written into the side, given from the other side,
        // sent in the range, and the buffer writes of the transforms and of the clips.
        private struct Sent
        {
            public long written, caughtUp, elements, transformCalls, clipCalls, calls, wholeElements, commandCalls, commandElements;

            public static Sent Of(VpLogicalCutDisplay d) => new Sent
            {
                written = d.InstanceRecordsWritten, caughtUp = d.InstanceRecordsCaughtUp, elements = d.BodyInstanceElementsTransferred,
                transformCalls = d.BodyInstanceTransformSetDataCalls, clipCalls = d.BodyInstanceClipSetDataCalls, calls = d.BodyInstanceSetDataCalls,
                wholeElements = d.BodyWholeInstanceElementsTransferred, commandCalls = d.BodyArgumentSetDataCalls, commandElements = d.BodyArgumentElementsTransferred,
            };

            public Sent Since(Sent b) => new Sent
            {
                written = written - b.written, caughtUp = caughtUp - b.caughtUp, elements = elements - b.elements, transformCalls = transformCalls - b.transformCalls,
                clipCalls = clipCalls - b.clipCalls, calls = calls - b.calls, wholeElements = wholeElements - b.wholeElements, commandCalls = commandCalls - b.commandCalls,
                commandElements = commandElements - b.commandElements,
            };

            // written, sent, the transforms' writes, the clips' writes
            public long[] Transfer => new[] { written, elements, transformCalls, clipCalls };

            public override string ToString() =>
                "records written " + written + ", given from the other side " + caughtUp + ", sent " + elements + " (" + (elements * 196) + " bytes: 64 a transform, 132 a clip) in "
                + transformCalls + " transform and " + clipCalls + " clip buffer writes; sent whole " + wholeElements + "; commands sent " + commandElements + " in " + commandCalls + " writes";
        }

        private Sent CollectAndCount(Twin twin, string what)
        {
            Sent before = Sent.Of(twin.kept.Display);
            CollectBoth(twin, what);
            AssertGpuHoldsWhatIsAdopted(twin.kept.Display, what);
            Sent sent = Sent.Of(twin.kept.Display).Since(before);
            TestContext.Out.WriteLine(what + ": " + sent);
            return sent;
        }

        [Test]
        public void InstanceRange_UpdatesFarApart_AreSentAsOneRange_AWriteABuffer_AndWhatLiesBetweenArrivesCurrent_OverSeveralCollections()
        {
            // Forty bodies of one record each, records 0..39.
            using (Twin twin = NewSlotTwin(64, 128))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                Both(twin, run => { for (int i = 0; i < 40; i++) AddSlotBody(run, Stand(i), false); });
                for (int f = 0; f < 4; f++) CollectBoth(twin, "settling " + f);

                // Nothing changes: nothing is written, copied or sent.
                for (int f = 0; f < 3; f++)
                {
                    Sent none = CollectAndCount(twin, "nothing changes " + f);
                    Assert.That(none.Transfer, Is.EqualTo(new long[] { 0, 0, 0, 0 }), "no write, no transfer");
                    Assert.That(new[] { none.caughtUp, none.wholeElements, none.commandCalls }, Is.EqualTo(new long[] { 0, 0, 0 }), "and nothing copied or sent again");
                }

                // Records 2 and 37, far apart, in one collection: one range from 2 to past 37, one write a buffer.
                Both(twin, run => run.at.Put(run.bodies[2], Stand(2, 0.5f)).Put(run.bodies[37], Stand(37, 0.5f)));
                Sent far = CollectAndCount(twin, "bodies 2 and 37 moved");
                Assert.That(far.Transfer, Is.EqualTo(new long[] { 2, 36, 1, 1 }), "two written; the range [2, 38) sent, once for the transforms and once for the clips");
                Assert.That(new[] { far.calls, far.wholeElements, far.commandCalls }, Is.EqualTo(new long[] { 2, 0, 0 }), "two buffer writes in all, nothing whole, no command");

                // The collection after: the other side is given that whole range, and nothing is sent.
                Sent after = CollectAndCount(twin, "the frame after");
                Assert.That(after.Transfer, Is.EqualTo(new long[] { 0, 0, 0, 0 }), "nothing written, nothing sent");
                Assert.That(after.caughtUp, Is.EqualTo(36), "the side being built is given the whole range it lacks: wider than the two records written");

                // A record between them, alone, written on this side: its own range.
                Both(twin, run => run.at.Put(run.bodies[20], Stand(20, 0.75f)));
                Sent middle = CollectAndCount(twin, "body 20, between them, moved");
                Assert.That(middle.Transfer, Is.EqualTo(new long[] { 1, 1, 1, 1 }), "one record, one range of one");
                Assert.That(middle.caughtUp, Is.Zero, "nothing was owed");

                // The two ends again, on the other side: the range sent holds body 20's record, written a collection ago
                // on the side that is now adopted. It arrives as it is now (the GPU read back, and the reference display).
                Both(twin, run => run.at.Put(run.bodies[2], Stand(2, 1f)).Put(run.bodies[37], Stand(37, 1f)));
                Sent again = CollectAndCount(twin, "bodies 2 and 37 moved again");
                Assert.That(again.Transfer, Is.EqualTo(new long[] { 2, 36, 1, 1 }), "the same range, a write a buffer");
                Assert.That(again.caughtUp, Is.EqualTo(1), "after it was given the record written between");

                // On, over several collections: the ends and the middle in turn and together; every collection is read
                // back from the GPU and set beside the reference.
                for (int round = 0; round < 4; round++)
                {
                    float lift = 1.25f + (0.25f * round);
                    Both(twin, run => run.at.Put(run.bodies[5], Stand(5, lift)).Put(run.bodies[30], Stand(30, lift)));
                    Sent ends = CollectAndCount(twin, "round " + round + ": bodies 5 and 30");
                    Assert.That(new[] { ends.written, ends.elements, ends.transformCalls, ends.clipCalls }, Is.EqualTo(new long[] { 2, 26, 1, 1 }), "round " + round + ": [5, 31)");
                    Both(twin, run => run.at.Put(run.bodies[18], Stand(18, lift)));
                    Sent mid = CollectAndCount(twin, "round " + round + ": body 18");
                    Assert.That(new[] { mid.written, mid.elements, mid.transformCalls, mid.clipCalls }, Is.EqualTo(new long[] { 1, 1, 1, 1 }), "round " + round + ": [18, 19)");
                    Assert.That(mid.caughtUp, Is.EqualTo(26), "round " + round + ": given the range the ends were written in");
                    Both(twin, run => run.at.Put(run.bodies[0], Stand(0, lift)).Put(run.bodies[18], Stand(18, lift + 0.1f)).Put(run.bodies[39], Stand(39, lift)));
                    Sent all = CollectAndCount(twin, "round " + round + ": bodies 0, 18 and 39");
                    Assert.That(new[] { all.written, all.elements, all.transformCalls, all.clipCalls }, Is.EqualTo(new long[] { 3, 40, 1, 1 }), "round " + round + ": [0, 40)");
                    Sent rest = CollectAndCount(twin, "round " + round + ": at rest");
                    Assert.That(new[] { rest.written, rest.elements, rest.transformCalls, rest.clipCalls, rest.caughtUp }, Is.EqualTo(new long[] { 0, 0, 0, 0, 40 }), "round " + round + ": nothing sent, the range given");
                    Sent rest2 = CollectAndCount(twin, "round " + round + ": at rest again");
                    Assert.That(new[] { rest2.elements, rest2.calls, rest2.caughtUp }, Is.EqualTo(new long[] { 0, 0, 0 }), "round " + round + ": and then nothing at all");
                }
            }
        }

        [Test]
        public void InstanceRange_AnUpdateOfWhatStands_AndRecordsTakenAtTheEnd_InOneCollection_AreOneRange_AndTheCommandsKeepTheirRanges()
        {
            using (Twin twin = NewTwin(12))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                for (int f = 0; f < 4; f++) CollectBoth(twin, "settling " + f);

                // Body 1 moves and three bodies are taken in, in one collection: the range reaches from record 1 to
                // past the last record taken at the end. The new commands are sent as their own range, as before.
                _frame++;
                Both(twin, run =>
                {
                    run.at.Put(run.bodies[1], Stand(1, 1f));
                    for (int i = 12; i < 15; i++) AddBody(run, Stand(i));
                });
                Sent grown = CollectAndCount(twin, "body 1 moved and three bodies taken in");
                Assert.That(display.DrawInstanceEnd, Is.EqualTo(15), "the layout: three records taken at the end");
                Assert.That(grown.Transfer, Is.EqualTo(new long[] { 4, 14, 1, 1 }), "four written -- one that stood, three at the end -- sent as [1, 15), a write a buffer");
                Assert.That(new[] { grown.commandElements, grown.commandCalls }, Is.EqualTo(new long[] { 3, display.CullsOnGpu ? 1L : 2L }), "the three new commands, as one range of theirs: the commands' ranges are as they were");
                Sent after = CollectAndCount(twin, "the frame after");
                Assert.That(new[] { after.elements, after.calls }, Is.EqualTo(new long[] { 0, 0 }), "nothing sent");
                Assert.That(after.caughtUp, Is.EqualTo(14), "the other side is given the range");

                // A body retired at the head and one moved at the end: the command zeroed is a command range; the record
                // written is the instance range. Nothing of the retired body's record is sent.
                Both(twin, run =>
                {
                    Assert.That(run.scene.ledger.Retire(run.bodies[0]), Is.True);
                    run.at.Put(run.bodies[14], Stand(14, 1f));
                });
                Sent retired = CollectAndCount(twin, "body 0 retired and body 14 moved");
                Assert.That(new[] { retired.elements, retired.transformCalls, retired.clipCalls }, Is.EqualTo(new long[] { 1, 1, 1 }), "the one record written");
                Assert.That(retired.commandElements, Is.EqualTo(1), "and the one command zeroed");
                CollectAndCount(twin, "the retirement's second structural collection");
                CollectAndCount(twin, "at rest");
            }
        }

        [Test]
        public void InstanceRange_ThroughACompaction_AndTheCollectionAfterIt_TheContentTheStartsAndTheHistoryAreRight()
        {
            using (Twin twin = NewTwin(16))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                CollectBoth(twin, "the bodies");
                int first = _frame;
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                Both(twin, run => { Assert.That(run.scene.ledger.Retire(run.bodies[3]), Is.True); Assert.That(run.scene.ledger.Retire(run.bodies[9]), Is.True); });
                CollectBoth(twin, "two retired");
                CollectBoth(twin, "the retirement's second structural collection");
                Both(twin, run => run.at.Put(run.bodies[1], Stand(1, 1f)).Put(run.bodies[14], Stand(14, 1f)));
                Sent moved = CollectAndCount(twin, "bodies 1 and 14 moved");
                int movedFrame = _frame;
                Assert.That(moved.Transfer, Is.EqualTo(new long[] { 2, 14, 1, 1 }), "[1, 15), the two holes within it");
                Color32[] before = Draw(display, Looking(new Vector3(22.5f, 6f, 0f), new Vector3(0f, -1f, 0.2f), 25f));

                // The compaction writes every live record where it now stands: one range from the first record, a write a
                // buffer. What the side lacked of the collection before is not copied first (it is all written).
                Both(twin, run => run.Display.CompactionMinimumInterval = 1);
                Sent compaction = CollectAndCount(twin, "the compaction");
                Assert.That(display.Compactions, Is.EqualTo(1));
                Assert.That(compaction.Transfer, Is.EqualTo(new long[] { 14, 14, 1, 1 }), "fourteen live records, written and sent as [0, 14)");
                Assert.That(compaction.caughtUp, Is.Zero, "nothing copied from the other side for it");
                var order = new List<LogicalFragmentId>();
                foreach (int i in new[] { 0, 2, 4, 5, 6, 7, 8, 10, 11, 12, 13, 15, 1, 14 }) order.Add(twin.kept.bodies[i]);
                AssertStandInOrder(display, order, "after the compaction");
                Assert.That(Differing(before, Draw(display, Looking(new Vector3(22.5f, 6f, 0f), new Vector3(0f, -1f, 0.2f), 25f))), Is.Zero, "the picture is the one before");

                // The collection after: the other side is given the compacted range, whole; nothing is sent; and neither
                // the compaction nor that copy is an ordinary write.
                Sent after = CollectAndCount(twin, "the frame after the compaction");
                Assert.That(new[] { after.caughtUp, after.elements, after.calls }, Is.EqualTo(new long[] { 14, 0, 0 }), "given [0, 14), nothing sent");
                foreach (int i in new[] { 0, 2, 4, 5, 6, 7, 8, 10, 11, 12, 13, 15 })
                {
                    Assert.That(LastWritten(display, twin.kept.bodies[i], "body " + i), Is.EqualTo(first), "body " + i + ": neither the range sent around it, the compaction nor the copy is its history");
                }

                Assert.That(new[] { LastWritten(display, twin.kept.bodies[1], "body 1"), LastWritten(display, twin.kept.bodies[14], "body 14") }, Is.EqualTo(new[] { movedFrame, movedFrame }));

                // And on: what the compaction put last moves again -- two records side by side now.
                Both(twin, run => run.at.Put(run.bodies[1], Stand(1, 2f)).Put(run.bodies[14], Stand(14, 2f)));
                Sent together = CollectAndCount(twin, "bodies 1 and 14 moved again");
                Assert.That(together.Transfer, Is.EqualTo(new long[] { 2, 2, 1, 1 }), "they stand together at the end now: [12, 14)");
                CollectAndCount(twin, "at rest");
                Assert.That(Differing(before, Draw(display, Looking(new Vector3(22.5f, 6f, 0f), new Vector3(0f, -1f, 0.2f), 25f))), Is.GreaterThan(0), "they moved in the picture");
            }
        }

        [Test]
        public void InstanceRange_ARefusedCompaction_DoesNotForgetWhatTheSideLackedBeyondItsPlan_AndTheNextCollectionsAreRight()
        {
            // Eight bodies, records 0..7; the first two retired, which leaves holes at the head. Body 7 moves: its record,
            // the last, is written on one side, and the other side lacks it. The next collection plans a compaction --
            // six records, [0, 6) -- and is refused right after the plan: the side still lacks record 7, which the plan
            // would not have covered, and is given it by the collection after.
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                Both(twin, run => { Assert.That(run.scene.ledger.Retire(run.bodies[0]), Is.True); Assert.That(run.scene.ledger.Retire(run.bodies[1]), Is.True); });
                CollectBoth(twin, "two retired at the head");
                CollectBoth(twin, "the retirement's second structural collection");
                CollectBoth(twin, "at rest");
                Both(twin, run => run.at.Put(run.bodies[7], Stand(7, 1f)));
                Sent moved = CollectAndCount(twin, "body 7, the last record, moved");
                Assert.That(moved.Transfer, Is.EqualTo(new long[] { 1, 1, 1, 1 }));
                Assert.That(SlotsOf(display, twin.kept.bodies[7], "body 7").instanceStart, Is.EqualTo(7), "the layout: past where a compaction's six records would end");
                Drawn adopted = Capture(display);
                int uploads = display.CommandUploads;

                Both(twin, run => run.Display.CompactionMinimumInterval = 1);
                display.FailAfterCompactionPlanForTest = true;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.False, "refused");
                Assert.That(new[] { display.CompactionCandidates, display.Compactions }, Is.EqualTo(new long[] { 1, 0 }), "the layout: a compaction was planned, and not run");
                Assert.That(display.CommandUploads, Is.EqualTo(uploads), "nothing uploaded");
                AssertSameShape(adopted, Capture(display), "what is adopted, whole");
                AssertGpuHoldsWhatIsAdopted(display, "after the refused compaction");
                display.FailAfterCompactionPlanForTest = false;

                // The collections after: the side that lacked body 7's record is the one built in. It is given that
                // record (and what the refused collection wrote over), and what is adopted and what the GPU holds are
                // body 7 where it moved to -- beside the reference, which was never refused.
                Assert.That(twin.everything.Display.TryBeginFrame(), Is.True, "the reference collected in that frame");
                Sent resumed = CollectAndCount(twin, "the collection after the refusal");
                Assert.That(resumed.caughtUp, Is.EqualTo(1), "given the record it lacked before the refusal (the refused collection wrote nothing: it was refused right after its plan): " + resumed);
                Sent compaction = CollectAndCount(twin, "the compaction");
                Assert.That(display.Compactions, Is.EqualTo(1), "the refusal kept it pending");
                Assert.That(compaction.Transfer, Is.EqualTo(new long[] { 6, 6, 1, 1 }), "six live records, [0, 6)");
                CollectAndCount(twin, "the frame after");
                Both(twin, run => run.at.Put(run.bodies[7], Stand(7, 2f)).Put(run.bodies[2], Stand(2, 1f)));
                CollectAndCount(twin, "bodies 7 and 2 moved");
                CollectAndCount(twin, "at rest");
            }
        }
    }
}
