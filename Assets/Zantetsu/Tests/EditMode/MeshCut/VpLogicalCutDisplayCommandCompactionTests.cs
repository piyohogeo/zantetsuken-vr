using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The command slots brought down by the compaction (TL, 2026-10-10; DESIGN 5.6, D-216). A registration drawn no
    /// more left its command slots holding commands of no instance, which every selection and every issued draw still
    /// went through, for as long as the display lived. The compaction of the instance regions (D-202) now lays the
    /// live registrations' runs of command slots out again as well, one after another from the first slot in the
    /// order they stand in, and the end comes down to the commands that draw. Every case runs the display's own
    /// collection beside the same display made to collect everything every time, with each one's GPU buffers read
    /// back slot by slot, and the picture before and after is compared pixel for pixel.
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        private Camera StandCamera() => Looking(new Vector3(10.5f, 6f, 0f), new Vector3(0f, -1f, 0.2f), 13f);

        // The live registrations' runs of command slots: these, in this order, one after another from the first slot,
        // nothing between them and nothing after -- every slot below the end is somebody's.
        private static void AssertRunsStandTogether(VpLogicalCutDisplay display, IList<LogicalFragmentId> order, string what)
        {
            int at = 0;
            for (int i = 0; i < order.Count; i++)
            {
                Slots s = SlotsOf(display, order[i], what + ": the " + i + "th");
                Assert.That(s.commandStart, Is.EqualTo(at), what + ": the " + i + "th run begins where the one before ended");
                at += s.commandCount;
            }

            Assert.That(display.DrawCommandEnd, Is.EqualTo(at), what + ": the end is the commands that draw");
            Assert.That(display.DrawCommandCount, Is.EqualTo(at), what + ": and every one of them draws");
            for (int slot = 0; slot < at; slot++)
            {
                Assert.That(display.TryGetCommandSlot(slot, out VpIndirectCommand command, out _, out bool free), Is.True, what + ": command slot " + slot);
                Assert.That(free, Is.False, what + ": command slot " + slot + " is somebody's");
                Assert.That(command.instanceCount, Is.GreaterThan(0), what + ": command slot " + slot + " draws");
            }

            Assert.That(display.TryGetCommandSlot(at, out _, out _, out _), Is.False, what + ": nothing is gone through past the end");
        }

        // The registrations given, by where their runs of command slots stand now.
        private static List<LogicalFragmentId> ByCommandStart(VpLogicalCutDisplay display, IEnumerable<LogicalFragmentId> fragments, string what)
        {
            var list = new List<LogicalFragmentId>(fragments);
            list.Sort((a, b) => SlotsOf(display, a, what).commandStart.CompareTo(SlotsOf(display, b, what).commandStart));
            return list;
        }

        /// <summary>
        /// **The command slots of what is drawn no more are given back, and the picture is the same.** Ten bodies,
        /// one of them cut and left published (clipped, with caps drawn through the tables by the snapshot's
        /// numbering); two are retired, and their slots hold commands of no instance below the end. The compaction
        /// lays the live runs out from the first slot in the order they stood in: the end is the commands that draw,
        /// every slot below it is somebody's, the GPU holds exactly that, and the picture is the one before. A body
        /// taken in after takes the slots at the new end; and the last run retired brings the end down with no run moved.
        /// </summary>
        [Test]
        public void CommandSlots_OfWhatIsDrawnNoMore_AreGivenBackByTheCompaction_TheLiveRunsInTheOrderTheyStood_AndThePictureIsTheSame()
        {
            using (Twin twin = NewTwin(10, 64, true))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                Both(twin, run => CutAndPlace(run, 7, plane, Stand(7)));
                CollectBoth(twin, "a cut published");
                CollectBoth(twin, "the frame after the cut");
                Both(twin, run => { Assert.That(run.scene.ledger.Retire(run.bodies[2]), Is.True); Assert.That(run.scene.ledger.Retire(run.bodies[6]), Is.True); });
                var live = new List<LogicalFragmentId>(twin.kept.bodies);
                live.Remove(twin.kept.bodies[2]);
                live.Remove(twin.kept.bodies[6]);
                CollectBoth(twin, "two retired");
                CollectBoth(twin, "the retirement's second structural collection");

                int endBefore = display.DrawCommandEnd, drawing = display.DrawCommandCount;
                Assert.That(endBefore, Is.GreaterThan(drawing), "the layout: slots of what is drawn no more below the end");
                Assert.That(display.Compactions, Is.Zero, "the layout: the interval has not passed");
                List<LogicalFragmentId> order = ByCommandStart(display, live, "before");
                Camera camera = StandCamera();   // one camera for every picture: the display holds a few cameras' preparations only
                Color32[] before = Draw(display, camera);

                Both(twin, run => run.Display.CompactionMinimumInterval = 1);
                CollectBoth(twin, "the compaction");
                TestContext.Out.WriteLine(display.DescribeCompaction());
                Assert.That(display.Compactions, Is.EqualTo(1), "the holes were a candidate, and it ran");
                AssertRunsStandTogether(display, order, "after the compaction");
                Assert.That(display.DrawCommandEnd, Is.EqualTo(drawing), "the end came down to the commands that draw");
                Assert.That(new[] { display.LastCompactionCommandEndBefore, display.LastCompactionCommandEnd }, Is.EqualTo(new[] { endBefore, drawing }));
                Assert.That(display.CompactionCommandSlotsGivenBack, Is.EqualTo(endBefore - drawing));
                Assert.That(display.CompactionCommandRunsMoved, Is.GreaterThan(0), "runs after the holes were put elsewhere");
                Assert.That(display.DrawInstanceEnd, Is.EqualTo(display.DrawInstanceCount), "and the instance regions as before (D-202)");
                AssertGpuHoldsWhatIsAdopted(display, "after the compaction");
                Assert.That(Differing(before, Draw(display, camera)), Is.Zero, "the picture is the one before the compaction");

                // The frames after: the other side is given the new layout and its tables are made again.
                CollectBoth(twin, "the frame after the compaction");
                AssertGpuHoldsWhatIsAdopted(display, "the frame after");
                Assert.That(Differing(before, Draw(display, camera)), Is.Zero, "and stands through the sides' exchange");
                CollectBoth(twin, "two frames after");
                AssertRunsStandTogether(display, order, "two frames after");
                Assert.That(Differing(before, Draw(display, camera)), Is.Zero);
                Assert.That(display.Compactions, Is.EqualTo(1), "nothing more to move");

                // A body taken in takes the slots at the new end, not past the old one.
                _frame++;   // a body is taken in in a frame of its own, as the other cases do
                Both(twin, run => AddBody(run, Stand(20)));
                CollectBoth(twin, "a body taken in");
                LogicalFragmentId added = twin.kept.bodies[10];
                Assert.That(SlotsOf(display, added, "the body taken in").commandStart, Is.EqualTo(drawing), "at the end the compaction left");
                CollectBoth(twin, "the frame after");
                order.Add(added);
                AssertRunsStandTogether(display, order, "with the body taken in");
                AssertGpuHoldsWhatIsAdopted(display, "with the body taken in");

                // The last run retired: no run is put elsewhere, and the end comes down all the same.
                long runsMoved = display.CompactionCommandRunsMoved;
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[10]), Is.True));
                order.Remove(added);
                CollectBoth(twin, "the last run retired");
                CollectBoth(twin, "its second structural collection");
                Assert.That(display.DrawCommandEnd, Is.GreaterThan(display.DrawCommandCount), "the layout: its slots below the end");
                CollectBoth(twin, "the compaction of the end");
                CollectBoth(twin, "at rest");
                AssertRunsStandTogether(display, order, "after the end came down");
                Assert.That(display.CompactionCommandRunsMoved, Is.EqualTo(runsMoved), "no run moved for it");
                AssertGpuHoldsWhatIsAdopted(display, "after the end came down");
                Assert.That(Differing(before, Draw(display, camera)), Is.Zero, "the picture is still the one before");
                TestContext.Out.WriteLine(display.DescribeCompaction());
            }
        }

        /// <summary>
        /// **A compaction that is not adopted puts every run back.** With slots of what is drawn no more below the
        /// end, the collection that planned the compaction is refused right after its plan: the end, every run and
        /// what the GPU holds are as adopted. The next collections plan again and bring the slots down.
        /// </summary>
        [Test]
        public void CommandSlots_ACompactionRefusedAfterItsPlan_LeavesEveryRunAndTheEndAsAdopted_AndTheNextOneGivesTheSlotsBack()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                CollectBoth(twin, "the bodies");
                CollectBoth(twin, "settling");
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[1]), Is.True));
                var live = new List<LogicalFragmentId>(twin.kept.bodies);
                live.Remove(twin.kept.bodies[1]);
                CollectBoth(twin, "one retired");
                CollectBoth(twin, "its second structural collection");
                int end = display.DrawCommandEnd;
                Assert.That(end, Is.GreaterThan(display.DrawCommandCount), "the layout: its slots below the end");
                var was = new List<Slots>();
                foreach (LogicalFragmentId body in live) was.Add(SlotsOf(display, body, "before"));
                Drawn before = Capture(display);
                int uploads = display.CommandUploads;

                Both(twin, run => run.Display.CompactionMinimumInterval = 1);
                display.FailAfterCompactionPlanForTest = true;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.False, "refused");
                Assert.That(display.IsHalted, Is.False, "not a stop");
                Assert.That(display.CommandUploads, Is.EqualTo(uploads), "nothing uploaded");
                Assert.That(display.Compactions, Is.Zero, "not run");
                Assert.That(display.DrawCommandEnd, Is.EqualTo(end), "the end is where it was");
                for (int i = 0; i < live.Count; i++) AssertSlotsStand(display, live[i], was[i], "after the refusal, live body " + i);
                AssertSameShape(before, Capture(display), "what is adopted, whole");
                AssertGpuHoldsWhatIsAdopted(display, "after the refused compaction");
                Assert.That(display.CompactionCommandSlotsGivenBack, Is.Zero, "no slot is counted as given back");
                display.FailAfterCompactionPlanForTest = false;

                Assert.That(twin.everything.Display.TryBeginFrame(), Is.True, "the reference collected in that frame");
                CollectBoth(twin, "the collection after the refusal plans again");
                CollectBoth(twin, "the compaction");
                CollectBoth(twin, "the frame after");
                Assert.That(display.Compactions, Is.GreaterThanOrEqualTo(1), "the refusal kept it pending");
                AssertRunsStandTogether(display, ByCommandStart(display, live, "after"), "after the compaction that was adopted");
                AssertGpuHoldsWhatIsAdopted(display, "after the compaction that was adopted");
            }
        }

        /// <summary>
        /// **With nothing left drawn the end comes down to none.** Every body is retired: the display draws nothing,
        /// and its slots would be gone through for as long as it lived. The compaction brings the end to the first
        /// slot, and the next body taken in starts from it.
        /// </summary>
        [Test]
        public void CommandSlots_WithNothingLeftDrawn_TheEndComesDownToNone_AndTheNextBodyStartsFromTheFirstSlot()
        {
            using (Twin twin = NewTwin(3))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                CollectBoth(twin, "the bodies");
                CollectBoth(twin, "settling");
                Both(twin, run => { for (int i = 0; i < 3; i++) Assert.That(run.scene.ledger.Retire(run.bodies[i]), Is.True); });
                CollectBoth(twin, "all retired");
                CollectBoth(twin, "the second structural collection");
                Assert.That(display.DrawCommandCount, Is.Zero, "the layout: nothing draws");
                int end = display.DrawCommandEnd;
                Assert.That(end, Is.GreaterThan(0), "the layout: and every slot ever taken is still gone through");

                Both(twin, run => run.Display.CompactionMinimumInterval = 1);
                CollectBoth(twin, "the compaction of nothing");
                CollectBoth(twin, "the frame after");
                TestContext.Out.WriteLine(display.DescribeCompaction());
                Assert.That(display.Compactions, Is.EqualTo(1), "it ran");
                Assert.That(new[] { display.DrawCommandEnd, display.DrawInstanceEnd }, Is.EqualTo(new[] { 0, 0 }), "nothing is gone through any more");
                Assert.That(display.CompactionCommandSlotsGivenBack, Is.EqualTo(end));
                AssertGpuHoldsWhatIsAdopted(display, "with nothing drawn");

                _frame++;   // a body is taken in in a frame of its own, as the other cases do
                Both(twin, run => AddBody(run, Stand(1)));
                CollectBoth(twin, "a body taken in");
                CollectBoth(twin, "the frame after");
                Slots s = SlotsOf(display, twin.kept.bodies[3], "the body taken in");
                Assert.That(new[] { s.commandStart, s.instanceStart }, Is.EqualTo(new[] { 0, 0 }), "from the first slot and the first record");
                Assert.That(display.DrawCommandEnd, Is.EqualTo(s.commandCount));
                AssertGpuHoldsWhatIsAdopted(display, "with the body taken in");
            }
        }
    }
}
