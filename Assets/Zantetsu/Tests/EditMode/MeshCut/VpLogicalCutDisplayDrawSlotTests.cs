using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The draw slots (TL, 2026-10-06; DESIGN 5.6): every registration that is drawn holds a run of command slots and
    /// an instance region, taken at the end of everything taken so far and never taken again by another registration.
    /// A change of structure -- a cut, a publication, a geometry commit, a retirement, a new body -- writes and sends
    /// the registrations it concerns and nothing of the others, whose slots stand where they were; what is drawn no
    /// more leaves commands of no instance behind. Every case here runs the display's own collection and upload,
    /// beside the same display made to collect everything every time; after every collection each one's GPU buffers,
    /// read back slot by slot, hold what it adopted (<see cref="AssertGpuHoldsWhatIsAdopted"/>). Where bodies are
    /// retired and others taken in, the picture is compared, pixel for pixel, with a display that was only ever shown
    /// what is left.
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        private struct Slots
        {
            public int commandStart, commandCount, instanceStart, instanceStride, renderFragments;

            public override string ToString() =>
                "commands " + commandCount + " from " + commandStart + ", instance records from " + instanceStart + " (" + instanceStride
                + " a command), drawn as " + renderFragments;
        }

        private static Slots SlotsOf(VpLogicalCutDisplay display, LogicalFragmentId fragment, string what)
        {
            Assert.That(
                display.TryGetDrawSlots(fragment, out int commandStart, out int commandCount, out int instanceStart, out int instanceStride, out int renderFragments),
                Is.True, what + ": it holds draw slots");
            return new Slots
            {
                commandStart = commandStart, commandCount = commandCount, instanceStart = instanceStart, instanceStride = instanceStride,
                renderFragments = renderFragments,
            };
        }

        private static void AssertSlotsStand(VpLogicalCutDisplay display, LogicalFragmentId fragment, Slots was, string what)
        {
            Assert.That(SlotsOf(display, fragment, what).ToString(), Is.EqualTo(was.ToString()), what + ": its draw slots stand where they were");
        }

        private static void AssertHoldsNoSlots(VpLogicalCutDisplay display, LogicalFragmentId fragment, string what)
        {
            Assert.That(display.TryGetDrawSlots(fragment, out _, out _, out _, out _, out _), Is.False, what + ": it holds no draw slots");
        }

        // A command slot of something drawn no more: it is gone through, holds a command of no instance, and is not
        // another registration's.
        private static void AssertDrawsNothing(VpLogicalCutDisplay display, int slot, string what)
        {
            Assert.That(display.TryGetCommandSlot(slot, out VpIndirectCommand command, out _, out bool free), Is.True, what + ": command slot " + slot + " is below the end");
            Assert.That(free, Is.True, what + ": command slot " + slot + " is nobody's");
            Assert.That(command.instanceCount, Is.Zero, what + ": command slot " + slot + " holds a command of no instance");
        }

        // What the collections did to the slots themselves, beside what Kept counts.
        private struct SlotWork
        {
            public long registrations, regionsTaken, regionsMoved, recordsMoved, commandsCaughtUp;

            public static SlotWork Of(VpLogicalCutDisplay d) => new SlotWork
            {
                registrations = d.RegistrationsWritten, regionsTaken = d.RegionsTaken, regionsMoved = d.RegionsMoved,
                recordsMoved = d.InstanceRecordsMoved, commandsCaughtUp = d.CommandRecordsCaughtUp,
            };

            public SlotWork Since(SlotWork before) => new SlotWork
            {
                registrations = registrations - before.registrations, regionsTaken = regionsTaken - before.regionsTaken,
                regionsMoved = regionsMoved - before.regionsMoved, recordsMoved = recordsMoved - before.recordsMoved,
                commandsCaughtUp = commandsCaughtUp - before.commandsCaughtUp,
            };

            public override string ToString() =>
                "registrations written " + registrations + ", instance regions taken " + regionsTaken + " (for a registration that had one " + regionsMoved + ", "
                + recordsMoved + " records in what it left), commands given from the other side " + commandsCaughtUp;
        }

        // Two displays with room, and limits, for that many commands and instance records.
        private Twin NewSlotTwin(int commands, int instances)
        {
            var twin = new Twin { kept = new Run(), everything = new Run() };
            foreach (Run run in new[] { twin.kept, twin.everything })
            {
                run.scene = NewReservedScene(commands, instances, 64, 64, new VpLogicalCutDisplayLimits(commands, instances, 64, 64), null, 64);
                Assert.That(run.scene, Is.Not.Null, "the display was made");
                run.Display.Placement = run.at;
                _frame++;
            }

            twin.everything.Display.collectEverythingForTest = true;
            return twin;
        }

        // A cube of one submesh (one command) or of two (two commands, two materials).
        private void AddSlotBody(Run run, Matrix4x4 at, bool twoSubmeshes)
        {
            LogicalFragmentId fragment = run.scene.ledger.AddFragment();
            VpStoredGeometry geometry = AppendCube(run.scene.storage, twoSubmeshes);
            Assert.That(run.Display.TryShow(fragment, geometry, at), Is.True, "body " + run.bodies.Count);
            run.at.Put(fragment, at);
            run.bodies.Add(fragment);
            run.geometries.Add(geometry);
        }

        private static Matrix4x4 Grid(int i) => Matrix4x4.Translate(new Vector3(3f * (i % 6), 0f, 3f * (i / 6)));

        // Three rows of the grid, from above and in front.
        private Camera GridCamera() => Looking(new Vector3(7.5f, 5f, 0f), new Vector3(0f, -1f, 0.6f), 9f);

        private static int Differing(Color32[] a, Color32[] b)
        {
            Assert.That(a.Length, Is.EqualTo(b.Length));
            int differing = 0;
            for (int p = 0; p < a.Length; p++)
            {
                if (a[p].r != b[p].r || a[p].g != b[p].g || a[p].b != b[p].b || a[p].a != b[p].a) differing++;
            }

            return differing;
        }

        [Test]
        public void DrawSlots_ACutInOneBody_WritesAndSendsThatBodyAlone_AndEveryOtherBodysSlotsStand()
        {
            const int bodies = 24, cutBody = bodies - 1;
            using (Twin twin = NewSlotTwin(32, 64))
            {
                Both(twin, run => { for (int i = 0; i < bodies; i++) AddSlotBody(run, Stand(i), false); });
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                VpLogicalCutDisplay display = twin.kept.Display;
                var was = new Slots[bodies];
                for (int i = 0; i < bodies; i++)
                {
                    was[i] = SlotsOf(display, twin.kept.bodies[i], "body " + i);
                    Assert.That(new[] { was[i].commandStart, was[i].commandCount, was[i].instanceStart, was[i].renderFragments }, Is.EqualTo(new[] { i, 1, i, 1 }), "the layout: body " + i);
                }

                // A cut admitted and prepared on one body: it is drawn as two clipped render fragments. Its command stays
                // where it is; its region has room for one record, so its two records are taken at the end -- its own
                // region alone.
                Kept before = Kept.Of(display);
                SlotWork workBefore = SlotWork.Of(display);
                var plane = Normalized(new float4(0f, 1f, 0f, 0f));
                CutOperationId cut = default;
                Both(twin, run => cut = Admit(run.scene.ledger, run.bodies[cutBody], plane));
                CollectBoth(twin, "a cut admitted on one body");
                Kept kept = Kept.Of(display).Since(before);
                SlotWork work = SlotWork.Of(display).Since(workBefore);
                TestContext.Out.WriteLine("one of " + bodies + " bodies cut: " + kept + "; " + work);
                for (int i = 0; i < bodies; i++)
                {
                    if (i != cutBody) AssertSlotsStand(display, twin.kept.bodies[i], was[i], "a cut on one body, body " + i);
                }

                Slots cutSlots = SlotsOf(display, twin.kept.bodies[cutBody], "the cut body");
                Assert.That(cutSlots.commandStart, Is.EqualTo(cutBody), "its command stays in its slot");
                Assert.That(cutSlots.renderFragments, Is.EqualTo(2), "drawn as two");
                Assert.That(cutSlots.instanceStart, Is.EqualTo(bodies), "its records taken at the end: no record of another body moved for it");
                Assert.That(kept.commandsWritten, Is.EqualTo(1), "one command written: its own");
                Assert.That(kept.written, Is.EqualTo(2), "two instance records written: its own");
                Assert.That(work.registrations, Is.EqualTo(1), "one registration written");
                Assert.That(new[] { work.regionsTaken, work.regionsMoved, work.recordsMoved }, Is.EqualTo(new long[] { 1, 1, 1 }), "one region taken, its own, for the one record that stood in what it left");
                Assert.That(kept.argumentElements, Is.EqualTo(1), "one command sent");
                Assert.That(kept.instanceElements, Is.EqualTo(2), "two instance records sent");
                Assert.That(kept.instanceCalls, Is.EqualTo(2), "in one range: its transforms and its clips");
                Assert.That(kept.wholeArguments + kept.wholeInstances, Is.EqualTo(0), "nothing sent whole");
                Assert.That(display.DrawCommandEnd, Is.EqualTo(bodies), "as many command slots as before");
                Assert.That(display.DrawInstanceEnd, Is.EqualTo(bodies + 2), "the instance records end after its new region");
                Assert.That(display.SideCount, Is.EqualTo(bodies + 1), "one more instance is drawn; the record it had is drawn by nothing");

                // The frame after: the other side is given what it lacks -- that command, those two records -- and no
                // command is written or sent.
                before = Kept.Of(display);
                workBefore = SlotWork.Of(display);
                CollectBoth(twin, "the frame after the cut");
                kept = Kept.Of(display).Since(before);
                work = SlotWork.Of(display).Since(workBefore);
                TestContext.Out.WriteLine("the frame after: " + kept + "; " + work);
                Assert.That(work.commandsCaughtUp, Is.EqualTo(1), "the other side is given the one command");
                Assert.That(kept.caughtUp, Is.EqualTo(2), "and the two records");
                Assert.That(kept.commandsWritten + kept.argumentElements + kept.wholeArguments, Is.EqualTo(0), "no command is written or sent");
                Assert.That(kept.written, Is.LessThanOrEqualTo(2), "no record but the clipped pair's own is written");
                Assert.That(kept.instanceElements, Is.LessThanOrEqualTo(2), "nor sent");
                Assert.That(work.registrations + work.regionsTaken, Is.EqualTo(0), "no registration is written again");

                // The cut published while a body far from its records moves: the cut body's registration is written again
                // where it stands -- no region taken -- and of the others only the moved one's record.
                before = Kept.Of(display);
                workBefore = SlotWork.Of(display);
                Both(twin, run =>
                {
                    Assert.That(run.scene.ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
                    run.at.Put(positive, Stand(cutBody)).Put(negative, Stand(cutBody)).Put(run.bodies[2], Stand(2, 2f));
                });
                CollectBoth(twin, "the cut published, body 2 moved");
                kept = Kept.Of(display).Since(before);
                work = SlotWork.Of(display).Since(workBefore);
                TestContext.Out.WriteLine("published, a far body moved: " + kept + "; " + work);
                for (int i = 0; i < bodies; i++)
                {
                    if (i != cutBody) AssertSlotsStand(display, twin.kept.bodies[i], was[i], "the publication, body " + i);
                }

                AssertSlotsStand(display, twin.kept.bodies[cutBody], cutSlots, "the publication, the cut body");
                Assert.That(kept.commandsWritten, Is.EqualTo(1), "the cut body's command written again");
                Assert.That(work.registrations, Is.EqualTo(1), "its registration alone");
                Assert.That(work.regionsTaken, Is.EqualTo(0), "no region taken");
                Assert.That(kept.written, Is.EqualTo(3), "its two records and the moved body's one");
                Assert.That(kept.instanceElements, Is.EqualTo(bodies), "sent as one range, from the moved body's record to past the cut body's at the end, with what lies between");
                Assert.That(kept.instanceCalls, Is.EqualTo(2), "the transforms and the clips of the one range");
                Assert.That(display.DrawInstanceEnd, Is.EqualTo(bodies + 2), "the end stands");
                CollectBoth(twin, "at rest");
                CollectBoth(twin, "at rest again");
            }
        }

        [Test]
        public void DrawSlots_BodiesRetiredAtTheHeadTheMiddleAndTheEnd_DrawNothingFromThenOn_AndNewBodiesAreTakenAtTheEnd_NotInTheirSlots()
        {
            // Twelve bodies in two rows: the even ones of two submeshes (two commands, a material each), the odd of one.
            bool Two(int i) => i % 2 == 0;
            int[] retired = { 0, 5, 11 };
            using (Twin twin = NewSlotTwin(32, 64))
            {
                Both(twin, run => { for (int i = 0; i < 12; i++) AddSlotBody(run, Grid(i), Two(i)); });
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                VpLogicalCutDisplay display = twin.kept.Display;
                Camera camera = GridCamera();
                var was = new Slots[12];
                int commands = 0;
                for (int i = 0; i < 12; i++)
                {
                    was[i] = SlotsOf(display, twin.kept.bodies[i], "body " + i);
                    Assert.That(new[] { was[i].commandStart, was[i].commandCount, was[i].instanceStart }, Is.EqualTo(new[] { commands, Two(i) ? 2 : 1, commands }), "the layout: body " + i);
                    commands += was[i].commandCount;
                }

                Assert.That(display.DrawCommandEnd, Is.EqualTo(18), "the layout: eighteen commands");
                int[] left = { 0, 1, was[5].commandStart, was[11].commandStart };
                Color32[] whole = Draw(display, camera);

                // The first body (two commands), one in the middle and the last are retired.
                Kept before = Kept.Of(display);
                Both(twin, run => { foreach (int i in retired) Assert.That(run.scene.ledger.Retire(run.bodies[i]), Is.True); });
                CollectBoth(twin, "three bodies retired");
                Kept kept = Kept.Of(display).Since(before);
                TestContext.Out.WriteLine("three of twelve retired, the collection of the retirement: " + kept);
                CollectBoth(twin, "the frame after");
                kept = Kept.Of(display).Since(before);
                TestContext.Out.WriteLine("three of twelve retired, with the frame after: " + kept);
                Assert.That(kept.commandsWritten, Is.EqualTo(4), "their four commands are made to draw nothing, and no other command is written");
                Assert.That(kept.written, Is.EqualTo(0), "no instance record is written: the others stand");
                Assert.That(kept.instanceElements + kept.wholeArguments + kept.wholeInstances, Is.EqualTo(0), "no instance record is sent, and nothing whole");
                for (int i = 0; i < 12; i++)
                {
                    if (Array.IndexOf(retired, i) >= 0) AssertHoldsNoSlots(display, twin.kept.bodies[i], "retired body " + i);
                    else AssertSlotsStand(display, twin.kept.bodies[i], was[i], "after the retirements, body " + i);
                }

                foreach (int slot in left) AssertDrawsNothing(display, slot, "after the retirements");
                Assert.That(display.DrawCommandEnd, Is.EqualTo(18), "the end stands: the slots of what is drawn no more are still gone through");
                Assert.That(display.DrawInstanceEnd, Is.EqualTo(18), "and so does the instance records'");
                Assert.That(display.DrawCommandCount, Is.EqualTo(14), "fourteen commands draw");
                Assert.That(display.SideCount, Is.EqualTo(14), "fourteen instances");
                Color32[] holes = Draw(display, camera);
                Assert.That(Differing(whole, holes), Is.GreaterThan(50), "the retired bodies were in the picture and are not now");

                // A display that was only ever shown the nine that are left draws the same picture.
                using (Twin nine = NewSlotTwin(32, 64))
                {
                    Both(nine, run =>
                    {
                        for (int i = 0; i < 12; i++)
                        {
                            if (Array.IndexOf(retired, i) < 0) AddSlotBody(run, Grid(i), Two(i));
                        }
                    });
                    CollectBoth(nine, "the nine alone");
                    CollectBoth(nine, "the nine alone, the frame after");
                    Assert.That(nine.kept.Display.DrawCommandEnd, Is.EqualTo(14), "the layout: no command of no instance in the fresh display");
                    Assert.That(Differing(holes, Draw(nine.kept.Display, GridCamera())), Is.Zero, "commands of no instance among the others draw nothing: the picture of the nine alone");
                }

                // Two new bodies, in the third row: both are taken at the end, one after the other -- one run of each
                // buffer -- and not in the slots the retired bodies left, which go on drawing nothing.
                _frame++;
                Both(twin, run =>
                {
                    AddSlotBody(run, Grid(12), false);
                    AddSlotBody(run, Grid(13), true);
                });
                before = Kept.Of(display);
                SlotWork workBefore = SlotWork.Of(display);
                CollectBoth(twin, "two bodies taken in");
                kept = Kept.Of(display).Since(before);
                SlotWork work = SlotWork.Of(display).Since(workBefore);
                TestContext.Out.WriteLine("two bodies taken in: " + kept + "; " + work);
                Slots one = SlotsOf(display, twin.kept.bodies[12], "the new one-command body");
                Slots two = SlotsOf(display, twin.kept.bodies[13], "the new two-command body");
                Assert.That(new[] { one.commandStart, one.instanceStart }, Is.EqualTo(new[] { 18, 18 }), "at the end of the commands and of the records, not in a slot a retired body left");
                Assert.That(new[] { two.commandStart, two.instanceStart }, Is.EqualTo(new[] { 19, 19 }), "and the next right after it");
                foreach (int slot in left) AssertDrawsNothing(display, slot, "after the new bodies");
                Assert.That(kept.commandsWritten, Is.EqualTo(3), "their three commands written, no other");
                Assert.That(kept.written, Is.EqualTo(3), "their three instance records, no other");
                Assert.That(work.registrations, Is.EqualTo(2), "two registrations written");
                Assert.That(work.regionsMoved, Is.EqualTo(0), "no region left for another");
                Assert.That(new[] { kept.argumentElements, kept.argumentCalls }, Is.EqualTo(new long[] { 3, 2 }), "the three commands sent as one run (its two argument buffers)");
                Assert.That(new[] { kept.instanceElements, kept.instanceCalls }, Is.EqualTo(new long[] { 3, 2 }), "the three records sent as one run (its transforms and its clips)");
                Assert.That(kept.wholeArguments + kept.wholeInstances, Is.EqualTo(0), "nothing sent whole");
                for (int i = 0; i < 12; i++)
                {
                    if (Array.IndexOf(retired, i) < 0) AssertSlotsStand(display, twin.kept.bodies[i], was[i], "after the new bodies, body " + i);
                }

                Assert.That(new[] { display.DrawCommandEnd, display.DrawInstanceEnd }, Is.EqualTo(new[] { 21, 21 }), "the ends are everything ever taken");
                Assert.That(new[] { display.DrawCommandCount, display.SideCount }, Is.EqualTo(new[] { 17, 17 }), "of which seventeen draw");
                CollectBoth(twin, "the frame after the new bodies");
                Color32[] taken = Draw(display, camera);

                // And the picture is that of a display shown the nine and the two: nothing of a retired body is drawn
                // where it stood, in this collection or the ones after it.
                using (Twin fresh = NewSlotTwin(32, 64))
                {
                    Both(fresh, run =>
                    {
                        for (int i = 0; i < 12; i++)
                        {
                            if (Array.IndexOf(retired, i) < 0) AddSlotBody(run, Grid(i), Two(i));
                        }

                        AddSlotBody(run, Grid(12), false);
                        AddSlotBody(run, Grid(13), true);
                    });
                    CollectBoth(fresh, "the eleven alone");
                    CollectBoth(fresh, "the eleven alone, the frame after");
                    Color32[] eleven = Draw(fresh.kept.Display, GridCamera());
                    Assert.That(Differing(taken, eleven), Is.Zero, "the picture of the eleven");

                    // Later collections -- a body moved and put back, so that something is written and sent -- do not
                    // bring a retired body back.
                    Both(twin, run => run.at.Put(run.bodies[3], Stand(3, 1f)));
                    CollectBoth(twin, "a body moved");
                    Both(twin, run => run.at.Put(run.bodies[3], Grid(3)));
                    CollectBoth(twin, "and put back");
                    CollectBoth(twin, "at rest");
                    foreach (int slot in left) AssertDrawsNothing(display, slot, "collections later");
                    Assert.That(Differing(Draw(display, camera), eleven), Is.Zero, "collections later: still the picture of the eleven");
                }

                Assert.That(Differing(holes, taken), Is.GreaterThan(50), "the new bodies are in the picture");
            }
        }

        [Test]
        public void DrawSlots_ABodyDrawnAsMoreThanItsRegionHolds_TakesItsOwnRegionAtTheEnd_AndWithinItsRegionItIsWrittenWhereItStands()
        {
            using (Twin twin = NewTwin(6))
            {
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                VpLogicalCutDisplay display = twin.kept.Display;
                var was = new Slots[6];
                for (int i = 0; i < 6; i++) was[i] = SlotsOf(display, twin.kept.bodies[i], "body " + i);
                Assert.That(display.DrawInstanceEnd, Is.EqualTo(6), "the layout: six records");
                var plane = Normalized(new float4(0f, 1f, 0f, 0f));

                // The last body cut: its region holds one record, so a region of two is taken at the end.
                SlotWork workBefore = SlotWork.Of(display);
                CutOperationId onLast = default;
                Both(twin, run => onLast = Admit(run.scene.ledger, run.bodies[5], plane));
                CollectBoth(twin, "a cut admitted on the last body");
                SlotWork work = SlotWork.Of(display).Since(workBefore);
                Slots last = SlotsOf(display, twin.kept.bodies[5], "the last body, cut");
                Assert.That(new[] { last.commandStart, last.instanceStart, last.instanceStride, last.renderFragments }, Is.EqualTo(new[] { 5, 6, 2, 2 }), "its command stays, its records are taken at the end");
                Assert.That(new[] { work.regionsTaken, work.regionsMoved, work.recordsMoved, work.registrations }, Is.EqualTo(new long[] { 1, 1, 1, 1 }), "one region taken: its own");
                Assert.That(display.DrawInstanceEnd, Is.EqualTo(8));
                for (int i = 0; i < 5; i++) AssertSlotsStand(display, twin.kept.bodies[i], was[i], "the last body cut, body " + i);

                // A body in the middle cut: the same, after the last body's region; no other moves.
                workBefore = SlotWork.Of(display);
                Both(twin, run => Admit(run.scene.ledger, run.bodies[2], plane));
                CollectBoth(twin, "a cut admitted on body 2");
                work = SlotWork.Of(display).Since(workBefore);
                Slots middle = SlotsOf(display, twin.kept.bodies[2], "body 2, cut");
                Assert.That(new[] { middle.commandStart, middle.instanceStart, middle.renderFragments }, Is.EqualTo(new[] { 2, 8, 2 }), "its command stays, its records are taken at the end");
                Assert.That(new[] { work.regionsTaken, work.regionsMoved, work.recordsMoved, work.registrations }, Is.EqualTo(new long[] { 1, 1, 1, 1 }), "one region taken: its own");
                Assert.That(display.DrawInstanceEnd, Is.EqualTo(10));
                AssertSlotsStand(display, twin.kept.bodies[5], last, "body 2 cut, the last body");
                foreach (int i in new[] { 0, 1, 3, 4 }) AssertSlotsStand(display, twin.kept.bodies[i], was[i], "body 2 cut, body " + i);

                // The cut on the last body comes to nothing (reclaimed as stale): it is drawn as one again, where it
                // stands, and keeps the room it has.
                workBefore = SlotWork.Of(display);
                Both(twin, run =>
                {
                    run.scene.ledger.NoteOwnershipChanged(run.bodies[5]);
                    Assert.That(run.scene.ledger.Publish(onLast, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Stale));
                });
                CollectBoth(twin, "the cut on the last body reclaimed as stale");
                work = SlotWork.Of(display).Since(workBefore);
                Slots again = SlotsOf(display, twin.kept.bodies[5], "the last body, whole again");
                Assert.That(new[] { again.commandStart, again.instanceStart, again.instanceStride, again.renderFragments }, Is.EqualTo(new[] { 5, 6, 2, 1 }), "written where it stands: its region is still its own, two records long");
                Assert.That(work.regionsTaken, Is.EqualTo(0), "no region taken");
                Assert.That(new[] { display.DrawInstanceEnd, display.SideCount }, Is.EqualTo(new[] { 10, 7 }), "the end stands; seven instances are drawn");
                AssertSlotsStand(display, twin.kept.bodies[2], middle, "the cut reclaimed, body 2");

                // Cut again: two records fit the region it kept, so nothing is taken.
                workBefore = SlotWork.Of(display);
                Both(twin, run => Admit(run.scene.ledger, run.bodies[5], plane));
                CollectBoth(twin, "the last body cut again");
                work = SlotWork.Of(display).Since(workBefore);
                Slots twice = SlotsOf(display, twin.kept.bodies[5], "the last body, cut again");
                Assert.That(new[] { twice.commandStart, twice.instanceStart, twice.instanceStride, twice.renderFragments }, Is.EqualTo(new[] { 5, 6, 2, 2 }), "written in the region it has");
                Assert.That(new[] { work.regionsTaken, work.registrations }, Is.EqualTo(new long[] { 0, 1 }), "no region taken; its registration alone written");
                Assert.That(display.DrawInstanceEnd, Is.EqualTo(10), "the end stands");

                // A new body takes the end of both: the record body 2 left is not its.
                _frame++;
                Both(twin, run => AddBody(run, Stand(6)));
                CollectBoth(twin, "a body taken in");
                Slots added = SlotsOf(display, twin.kept.bodies[6], "the new body");
                Assert.That(new[] { added.commandStart, added.instanceStart }, Is.EqualTo(new[] { 6, 10 }), "the end of the commands and the end of the records");
                Assert.That(new[] { display.DrawCommandEnd, display.DrawInstanceEnd }, Is.EqualTo(new[] { 7, 11 }));
                foreach (int i in new[] { 0, 1, 3, 4 }) AssertSlotsStand(display, twin.kept.bodies[i], was[i], "a body taken in, body " + i);
                AssertSlotsStand(display, twin.kept.bodies[2], middle, "a body taken in, body 2");
                CollectBoth(twin, "at rest");
            }
        }

        [Test]
        public void DrawSlots_ThroughAGeometryCommit_TheOthersStand_TheReplacedBodyDrawsNothing_TheSidesAreTakenAtTheEnd_AndNothingCastsTwoSidedAfterIt()
        {
            using (Twin twin = NewTwin(6, 64, true))
            {
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                VpLogicalCutDisplay display = twin.kept.Display;
                var was = new Slots[6];
                for (int i = 0; i < 6; i++) was[i] = SlotsOf(display, twin.kept.bodies[i], "body " + i);

                // Published and not committed: the body is drawn as its two clipped sides, cast two-sided.
                var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) made = default;
                Both(twin, run => made = CutAndPlace(run, 1, plane, Stand(1)));
                CollectBoth(twin, "a cut published");
                CollectBoth(twin, "the frame after");
                Slots provisional = SlotsOf(display, twin.kept.bodies[1], "the cut body");
                Assert.That(new[] { provisional.commandStart, provisional.renderFragments }, Is.EqualTo(new[] { was[1].commandStart, 2 }), "its command in its slot, drawn as two");
                int twoSided = 0;
                for (int c = 0; c < display.DrawCommandCount; c++) twoSided += display.CommandCastsTwoSided(c) ? 1 : 0;
                Assert.That(twoSided, Is.EqualTo(1), "the clipped body's command casts two-sided, and no other");
                foreach (int i in new[] { 0, 2, 3, 4, 5 }) AssertSlotsStand(display, twin.kept.bodies[i], was[i], "a cut published, body " + i);

                // The geometry commit: the registration is replaced by the two sides' own, taken at the end.
                int commandEnd = display.DrawCommandEnd, instanceEnd = display.DrawInstanceEnd;
                _frame++;
                Both(twin, run => Assert.That(CommitBothSides(run, 1, made.cut, plane, made.positive, made.negative), Is.True, "committed"));
                Kept before = Kept.Of(display);
                SlotWork workBefore = SlotWork.Of(display);
                CollectBoth(twin, "the commit");
                Kept kept = Kept.Of(display).Since(before);
                SlotWork work = SlotWork.Of(display).Since(workBefore);
                Slots above = SlotsOf(display, made.positive, "the positive side"), below = SlotsOf(display, made.negative, "the negative side");
                TestContext.Out.WriteLine("the commit: " + kept + "; " + work + "; positive side: " + above + "; negative side: " + below);
                AssertHoldsNoSlots(display, twin.kept.bodies[1], "the body the commit replaced");
                for (int c = 0; c < provisional.commandCount; c++) AssertDrawsNothing(display, provisional.commandStart + c, "the body the commit replaced");
                foreach (int i in new[] { 0, 2, 3, 4, 5 }) AssertSlotsStand(display, twin.kept.bodies[i], was[i], "the commit, body " + i);
                Assert.That(Math.Min(above.commandStart, below.commandStart), Is.EqualTo(commandEnd), "the sides' commands are taken at the end");
                Assert.That(Math.Min(above.instanceStart, below.instanceStart), Is.EqualTo(instanceEnd), "and their records");
                Assert.That(display.DrawCommandEnd, Is.EqualTo(commandEnd + above.commandCount + below.commandCount), "one after the other");
                Assert.That(work.registrations, Is.EqualTo(2), "the two sides' registrations written, no other");
                Assert.That(kept.commandsWritten, Is.EqualTo(provisional.commandCount + above.commandCount + below.commandCount), "the replaced body's commands made to draw nothing, the two sides' written");
                Assert.That(kept.written, Is.EqualTo(above.commandCount + below.commandCount), "an instance record a command of the two sides, no other");
                Assert.That(kept.wholeArguments + kept.wholeInstances, Is.EqualTo(0), "nothing sent whole");
                for (int c = 0; c < display.DrawCommandCount; c++)
                {
                    Assert.That(display.CommandCastsTwoSided(c), Is.False, "after the commit command " + c + " casts one-sided");
                }

                CollectBoth(twin, "the frame after the commit");
                Both(twin, run => run.at.Put(made.negative, Stand(1, -1f)).Put(run.bodies[4], Stand(4, 1f)));
                CollectBoth(twin, "a committed side and a body moved");
                foreach (int i in new[] { 0, 2, 3, 4, 5 }) AssertSlotsStand(display, twin.kept.bodies[i], was[i], "after the commit, body " + i);
                AssertSlotsStand(display, made.positive, above, "after the commit, the positive side");
                AssertSlotsStand(display, made.negative, below, "after the commit, the negative side");
                for (int c = 0; c < provisional.commandCount; c++) AssertDrawsNothing(display, provisional.commandStart + c, "collections after the commit");
                CollectBoth(twin, "at rest");
            }
        }

        [Test]
        public void DrawSlots_AtTheLimitOfWhatWasEverTaken_NothingMoreIsTakenIn_AlthoughFewerAreDrawn_AndWithNobodyToTellWhatIsAdoptedGoesOn()
        {
            // Room, and limits, for six commands. This display has no room-failure handler: a shortfall is refused and
            // nothing else happens. (A world gives it one, and then the same shortfall stops it: the case after this.)
            var run = new Run();
            run.scene = NewReservedScene(6, 16, 64, 64, new VpLogicalCutDisplayLimits(6, 16, 64, 64), null);
            Assert.That(run.scene, Is.Not.Null, "the display was made");
            using (run.scene)
            {
                VpLogicalCutDisplay display = run.Display;
                display.Placement = run.at;
                _frame++;
                for (int i = 0; i < 4; i++) AddSlotBody(run, Stand(i), false);

                void Settle(string what)
                {
                    _frame++;
                    Assert.That(display.TryBeginFrame(), Is.True, what + ": collected");
                    AssertGpuHoldsWhatIsAdopted(display, what);
                }

                Settle("four bodies");
                Settle("four bodies, the frame after");
                var was = new Slots[4];
                for (int i = 0; i < 4; i++) was[i] = SlotsOf(display, run.bodies[i], "body " + i);

                // One is retired: three commands draw, and four slots were taken.
                Assert.That(run.scene.ledger.Retire(run.bodies[1]), Is.True);
                Settle("body 1 retired");
                Settle("the frame after");
                Assert.That(new[] { display.DrawCommandCount, display.DrawCommandEnd }, Is.EqualTo(new[] { 3, 4 }), "three commands draw among the four slots ever taken");

                // A body of two commands fits the two slots left at the end.
                _frame++;
                AddSlotBody(run, Stand(4), true);
                Settle("a body of two commands");
                Slots two = SlotsOf(display, run.bodies[4], "the body of two commands");
                Assert.That(new[] { two.commandStart, two.commandCount }, Is.EqualTo(new[] { 4, 2 }), "at the end");
                Assert.That(new[] { display.DrawCommandCount, display.DrawCommandEnd }, Is.EqualTo(new[] { 5, 6 }), "five commands draw; the six slots the limit allows were taken");

                // One more body: five commands draw and the limit is six, but the slot the retired body left is not
                // taken again. It is refused where it is shown, and the display goes on as it is.
                _frame++;
                LogicalFragmentId more = run.scene.ledger.AddFragment();
                Assert.That(
                    display.TryShow(more, AppendCube(run.scene.storage, false), Stand(5)), Is.False,
                    "no slot is left at the end: the body is not taken in");
                Assert.That(display.IsHalted, Is.False, "a shortfall of room is not a stop");
                Settle("after the refusal");
                foreach (int i in new[] { 0, 2, 3 }) AssertSlotsStand(display, run.bodies[i], was[i], "after the refusal, body " + i);
                AssertSlotsStand(display, run.bodies[4], two, "after the refusal, the body of two commands");
                AssertDrawsNothing(display, was[1].commandStart, "after the refusal");
                Assert.That(new[] { display.DrawCommandCount, display.DrawCommandEnd }, Is.EqualTo(new[] { 5, 6 }));

                // Retiring another does not make room either: the answer is the same, and the rest is drawn as before.
                Assert.That(run.scene.ledger.Retire(run.bodies[2]), Is.True);
                Settle("body 2 retired");
                Settle("the frame after");
                _frame++;
                Assert.That(
                    display.TryShow(more, AppendCube(run.scene.storage, false), Stand(5)), Is.False,
                    "still no slot at the end, with four commands drawing");
                Assert.That(new[] { display.DrawCommandCount, display.DrawCommandEnd }, Is.EqualTo(new[] { 4, 6 }));
                run.at.Put(run.bodies[0], Stand(0, 1f));
                Settle("a body moved at the limit");
                foreach (int i in new[] { 0, 3 }) AssertSlotsStand(display, run.bodies[i], was[i], "at the limit, body " + i);
            }
        }

        /// <summary>
        /// The same limit as a world meets it: the display is given a handler (a world gives its termination), and the
        /// contract of every need past a limit holds for the end of the draw slots too -- it is told once, and the
        /// display stops rather than going on with what it has (DESIGN 4, 5.6). Retiring bodies does not bring room
        /// back, so that is where a display that has used its slots up ends.
        /// </summary>
        [Test]
        public void DrawSlots_AtTheLimitOfWhatWasEverTaken_WithAHandler_TheNeedIsToldOnce_AndTheDisplayStops()
        {
            var run = new Run();
            run.scene = NewReservedScene(4, 16, 64, 64, new VpLogicalCutDisplayLimits(4, 16, 64, 64), null);
            Assert.That(run.scene, Is.Not.Null, "the display was made");
            using (run.scene)
            {
                VpLogicalCutDisplay display = run.Display;
                display.Placement = run.at;
                var told = new List<string>();
                display.RoomFailureHandler = told.Add;
                _frame++;
                for (int i = 0; i < 4; i++) AddSlotBody(run, Stand(i), false);
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "four bodies: collected");
                Assert.That(run.scene.ledger.Retire(run.bodies[0]) && run.scene.ledger.Retire(run.bodies[1]), Is.True);
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "two retired: collected");
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.True, "the frame after: collected");
                Assert.That(new[] { display.DrawCommandCount, display.DrawCommandEnd }, Is.EqualTo(new[] { 2, 4 }), "two commands draw; the four slots the limit allows were taken");
                Assert.That(told, Is.Empty, "nothing was short so far");
                Assert.That(display.IsHalted, Is.False);

                _frame++;
                LogicalFragmentId more = run.scene.ledger.AddFragment();
                Assert.That(display.TryShow(more, AppendCube(run.scene.storage, false), Stand(5)), Is.False, "no slot is left at the end");
                Assert.That(told.Count, Is.EqualTo(1), "the need is told");
                StringAssert.Contains("draw commands", told[0]);
                StringAssert.Contains("limit 4", told[0]);
                Assert.That(display.IsHalted, Is.True, "and the display stops");
                Assert.That(display.HaltReason, Is.EqualTo(LogicalCutDisplayHaltReason.RoomNotEstablished));
                Assert.That(display.TryShow(more, AppendCube(run.scene.storage, false), Stand(5)), Is.False, "asked again");
                Assert.That(told.Count, Is.EqualTo(1), "told once only");
            }
        }

        [Test]
        public void DrawSlots_AGpuBatchReplacedByALargerOne_WithACommandOfNoInstanceAmongTheOthers_IsSentEverythingOnce_CountedApart()
        {
            // A first room of four instances: more bodies grow the room and the GPU batch with it.
            using (Twin twin = NewTwin(4, 4, false, true))
            {
                for (int f = 0; f < 3; f++) CollectBoth(twin, "settling " + f);
                VpLogicalCutDisplay display = twin.kept.Display;
                VpIndexedIndirectDrawBatch first = display.BodyBatchForTest;
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[1]), Is.True));
                CollectBoth(twin, "body 1 retired");
                CollectBoth(twin, "the frame after");
                Assert.That(new[] { display.DrawCommandCount, display.DrawCommandEnd }, Is.EqualTo(new[] { 3, 4 }), "a command of no instance among the others");

                // Seven more bodies, at the end: the room grows, and the batch is replaced.
                _frame++;
                Kept before = Kept.Of(display);
                Both(twin, run => { for (int i = 4; i < 11; i++) AddBody(run, Stand(i)); });
                CollectBoth(twin, "seven more bodies: the room and the batch grown");
                Kept kept = Kept.Of(display).Since(before);
                TestContext.Out.WriteLine("the batch replaced: " + kept);
                Assert.That(display.BodyBatchForTest, Is.Not.SameAs(first), "the batch was replaced by a larger one");
                Assert.That(new[] { display.DrawCommandCount, display.DrawCommandEnd }, Is.EqualTo(new[] { 10, 11 }), "ten bodies drawn in the eleven slots ever taken");
                AssertDrawsNothing(display, 1, "in the larger room");
                Assert.That(kept.wholeArguments, Is.EqualTo(display.DrawCommandEnd), "the new batch is sent every command slot");
                Assert.That(kept.wholeInstances, Is.EqualTo(display.DrawInstanceEnd), "and every instance record");
                Assert.That(kept.argumentElements + kept.instanceElements, Is.EqualTo(0), "counted apart from what is sent as a change");

                // From then on only what changes is sent again.
                for (int f = 0; f < 3; f++) CollectBoth(twin, "after the growth, frame " + f);
                before = Kept.Of(display);
                Both(twin, run => run.at.Put(run.bodies[9], Stand(9, 2f)));
                CollectBoth(twin, "a body moved in the larger room");
                kept = Kept.Of(display).Since(before);
                Assert.That(kept.wholeArguments + kept.wholeInstances, Is.EqualTo(0), "nothing whole again");
                Assert.That(kept.instanceElements, Is.EqualTo(1), "the moved body's record alone");
                CollectBoth(twin, "at rest");
            }
        }
    }
}
