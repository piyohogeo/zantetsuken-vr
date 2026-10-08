using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// Static registration and placement data not collected again for a step (TL, 2026-10-08; DESIGN 5.6). A lookup
    /// that vouches for a fragment (<see cref="IVpFixedPlacementSource"/>) makes its render fragments **fixed** once
    /// they were asked and stood: a later step alone asks them no more, and a collection after a step whose pass would
    /// have no target at all -- every one fixed or held and far, nothing told -- lets the adopted snapshot stand as a
    /// collection with no step does. Fixed is the lookup's guarantee, never a reading: a dynamic body that stood twice
    /// is held (D-205), not fixed. The structure tally, the instance takes and the release pass over the registrations
    /// follow from the structure and are kept while it stands. Every case runs that display beside the same display
    /// collecting everything every time, and compares what the two draw after every collection.
    /// </summary>
    public partial class VpLogicalCutDisplayMultiCutTests
    {
        // The run's own lookup with the lookup's word on top: the fragments named are vouched for as fixed.
        private sealed class VouchingPlacements : IVpFragmentPlacement, IVpFixedPlacementSource
        {
            private readonly IVpFragmentPlacement _inner;
            public readonly HashSet<LogicalFragmentId> fixedOnes = new HashSet<LogicalFragmentId>();
            public long vouchesAsked;

            public VouchingPlacements(IVpFragmentPlacement inner)
            {
                _inner = inner;
            }

            public VpFragmentPlacementKind TryGetGeometryLocalToWorld(LogicalFragmentId fragment, CutOperationId operation, float side, out Matrix4x4 geometryLocalToWorld)
                => _inner.TryGetGeometryLocalToWorld(fragment, operation, side, out geometryLocalToWorld);

            public bool IsPlacementFixed(LogicalFragmentId fragment)
            {
                vouchesAsked++;
                return fixedOnes.Contains(fragment);
            }
        }

        private VouchingPlacements Vouch(Twin twin, params int[] bodies)
        {
            var vouching = new VouchingPlacements(twin.kept.at);
            foreach (int b in bodies) vouching.fixedOnes.Add(twin.kept.bodies[b]);
            twin.kept.Display.Placement = vouching;
            return vouching;
        }

        // Passes skipped, reuses without a step, tallies run / kept, instance takes run / kept.
        private static long[] StaticWork(VpLogicalCutDisplay d) =>
            new[] { d.PlacementPassesSkipped, d.PlacementReuses, d.StructureTallies, d.StructureTalliesSkipped, d.InstanceTakes, d.InstanceTakesSkipped };

        private static string FixedLine(in VpHeldPlacementTotals d) =>
            "passes selective " + d.passesSelective + " structure " + d.passesStructure + "; asked ordinary " + d.queriedOrdinary + ", near " + d.queriedNear + ", told " + d.queriedNotified
            + ", fixed " + d.queriedFixed + "; not asked: held " + d.omitted + ", fixed " + d.omittedFixed + "; fixed taken " + d.fixedTaken + ", unfixed " + d.unfixed
            + ", promoted " + d.promoted + ", demoted " + d.demoted;

        // Two collections, the second at a new step: the first places every body anew (the structure; a body whose
        // placement is the identity -- body 0 at x = 0 -- stands as the structure placed it and is fixed already), the
        // second asks every one and finds it standing -- the vouched-for are all fixed by then.
        private void SettleFixed(Twin twin, System.Action step, int expectedFixed, string what)
        {
            VpHeldPlacementTotals t = twin.kept.Display.HeldPlacementTotals;
            CollectBoth(twin, what + ": the structure");
            step();
            CollectBoth(twin, what + ": standing at a new step");
            VpHeldPlacementTotals d = Since(twin.kept.Display, t);
            TestContext.Out.WriteLine(what + ": " + FixedLine(d));
            Assert.That(twin.kept.Display.FixedPlacements, Is.EqualTo(expectedFixed), what + ": fixed by the lookup's word");
            Assert.That(d.fixedTaken, Is.EqualTo(expectedFixed), what + ": taken over the two passes");
        }

        [Test]
        public void FixedPlacements_StaticOnly_AStepAsksNothing_RunsNoPass_NoTally_NoInstanceTake_AndDrawsWhatCollectingEverythingDraws()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                var host = new HoldHost();
                host.points.Add(k_byTheFirstTwo);   // near the first two: a HELD one there would be asked; a fixed one is not
                host.Attach(display);
                VouchingPlacements vouching = Vouch(twin, 0, 1, 2, 3, 4, 5, 6, 7);
                SettleFixed(twin, () => host.step++, 8, "eight static bodies");
                Assert.That(display.HeldPlacements, Is.Zero, "fixed, not held: nothing in the tree");
                AssertTreeHolds(display, 0, "fixed bodies");

                // Steps go on; nothing is asked, no pass is made, the tally and the instance takes are kept.
                for (int i = 0; i < 4; i++)
                {
                    host.step++;
                    long[] work = PlaceWork(display), statics = StaticWork(display);
                    long vouches = vouching.vouchesAsked;
                    VpHeldPlacementTotals t = display.HeldPlacementTotals;
                    CollectBoth(twin, "step " + i + " with nothing to ask");
                    VpHeldPlacementTotals d = Since(display, t);
                    Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 0, 0, 0, 0 }), "no pass, no query, nothing kept or placed");
                    Assert.That(Minus(StaticWork(display), statics), Is.EqualTo(new long[] { 1, 0, 0, 1, 0, 1 }), "the pass skipped; the tally and the instance take kept");
                    Assert.That(d.omittedFixed, Is.EqualTo(8), "the eight fixed ones not asked");
                    Assert.That(vouching.vouchesAsked, Is.EqualTo(vouches), "the lookup is not asked for its word either");
                    Assert.That(display.FixedPlacements, Is.EqualTo(8));
                }

                // A collection with no new step reuses as before (D-204): not a skipped pass.
                long[] before = StaticWork(display);
                CollectBoth(twin, "no new step");
                Assert.That(Minus(StaticWork(display), before), Is.EqualTo(new long[] { 0, 1, 0, 1, 0, 1 }));

                // With no reference point at all (nothing near), the same.
                host.points.Clear();
                host.step++;
                before = StaticWork(display);
                CollectBoth(twin, "a step with no reference point");
                Assert.That(Minus(StaticWork(display), before), Is.EqualTo(new long[] { 1, 0, 0, 1, 0, 1 }));
            }
        }

        [Test]
        public void FixedPlacements_WhenOnlyTheCameraMoves_TheCollectionIsKept_AndTheCameraIsPreparedEveryFrame()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                var host = new ViewHost();
                Camera camera = ViewCamera(display);
                host.points.Add(new Vector3(6f, 0f, 0f));
                host.cameras.Add(camera);
                host.Attach(display);
                Vouch(twin, 0, 1, 2, 3, 4, 5, 6, 7);
                SettleFixed(twin, () => host.step++, 8, "eight static bodies");

                // The camera moves and turns every frame, with and without a step: the collection stands, the camera is
                // prepared (its culling and stencil are the camera's own work, not the collection's).
                long[] before = StaticWork(display);
                long views = display.CameraViewsNoted;
                for (int i = 0; i < 6; i++)
                {
                    Matrix4x4 view = ViewOf(-4f + 3f * i, 6f + 3f * i);
                    Assert.That(display.NoteCameraView(camera, 1, view, view), Is.True);
                    host.points[0] = new Vector3(3f * i, 0f, 0f);
                    if ((i & 1) == 0) host.step++;
                    CollectBoth(twin, "the camera moved, frame " + i);
                    Assert.That(display.TryPrepareCamera(camera), Is.True, "the camera is prepared from the standing collection");
                }

                long[] d = Minus(StaticWork(display), before);
                TestContext.Out.WriteLine("camera moving: passes skipped " + d[0] + ", reuses " + d[1] + ", tallies run " + d[2] + " kept " + d[3] + ", takes run " + d[4] + " kept " + d[5]);
                Assert.That(d[0] + d[1], Is.EqualTo(6), "every collection stood the adopted snapshot: three for a step, three for none");
                Assert.That(d[0], Is.EqualTo(3));
                Assert.That(new[] { d[2], d[4] }, Is.EqualTo(new long[] { 0, 0 }), "no tally and no take for a camera that moved");
                Assert.That(display.CameraViewsNoted - views, Is.EqualTo(6));
            }
        }

        [Test]
        public void FixedPlacements_MixedWithDynamicBodies_OnlyTheMovingOnesAreAsked_AndTheFixedAreNeverAskedForAStep()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                var host = new HoldHost();
                host.points.Add(new Vector3(18f, 0f, 0f));   // near bodies 6 and 7 (x 18, 21): a held one there is asked; the fixed ones never are
                host.Attach(display);
                Vouch(twin, 0, 1, 2, 3, 4, 5);   // 6 and 7 are dynamic: both stand through the settling (held after two steps); 6 then moves every step
                SettleFixed(twin, () => host.step++, 6, "six static, two dynamic");
                host.step++;
                CollectBoth(twin, "a third step: bodies 6 and 7 held");
                AssertTreeHolds(display, 2, "bodies 6 and 7");

                for (int i = 0; i < 4; i++)
                {
                    host.step++;
                    Both(twin, run => run.at.Put(run.bodies[6], Stand(6, 0.5f * (i + 1))));
                    long[] work = PlaceWork(display), statics = StaticWork(display);
                    VpHeldPlacementTotals t = display.HeldPlacementTotals;
                    CollectBoth(twin, "step " + i + ": body 6 moved");
                    VpHeldPlacementTotals d = Since(display, t);
                    TestContext.Out.WriteLine("step " + i + ": " + FixedLine(d));
                    Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 2, 1, 1 }), "one pass: the two dynamic bodies asked; 6 placed anew, 7 kept");
                    Assert.That(new[] { d.omittedFixed, d.queriedFixed, d.omitted, d.queriedOrdinary + d.queriedNear }, Is.EqualTo(new long[] { 6, 0, 0, 2 }), "six fixed not asked; the two dynamic ones asked (held near, or ordinary once moved)");
                    Assert.That(Minus(StaticWork(display), statics), Is.EqualTo(new long[] { 0, 0, 0, 1, 0, 1 }), "the pass ran; tally and takes kept (the structure stands)");
                    Assert.That(display.AdoptedSnapshot.TryGetRenderFragment(6, out VpMultiCutRenderFragment moved), Is.True);
                    Assert.That(moved.geometryLocalToWorld.m13, Is.EqualTo(0.5f * (i + 1)), "body 6 drawn where it moved to");
                }

                Assert.That(display.FixedPlacements, Is.EqualTo(6));
            }
        }

        [Test]
        public void FixedPlacements_AreAskedAgainWhenTold_WhenNothingIsNamed_WhenTheLookupChanges_AndStartOrdinaryWhenTheStructureChanges()
        {
            using (Twin twin = NewTwin(8))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                List<LogicalFragmentId> bodies = twin.kept.bodies;
                var host = new HoldHost();
                host.points.Add(k_byTheFirstTwo);
                host.Attach(display);
                var told = new ToldChanges(host, display);
                VouchingPlacements vouching = Vouch(twin, 0, 1, 2, 3, 4, 5, 6, 7);
                SettleFixed(twin, () => host.step++, 8, "eight static bodies");

                // (a) A fixed body is put elsewhere with no step, and that is told with it: asked, found moved, placed
                // anew -- ordinary now; at the next step it stands, vouched for, and is fixed again. Nobody else is asked.
                Both(twin, run => run.at.Put(run.bodies[3], Stand(3, 1f)));
                told.Of(bodies[3]);
                VpHeldPlacementTotals t = display.HeldPlacementTotals;
                long[] work = PlaceWork(display);
                CollectBoth(twin, "body 3 told");
                VpHeldPlacementTotals d = Since(display, t);
                TestContext.Out.WriteLine("told: " + FixedLine(d));
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 1, 0, 1 }), "one query: the one told");
                Assert.That(new[] { d.queriedFixed, d.unfixed, d.omittedFixed }, Is.EqualTo(new long[] { 1, 1, 7 }));
                Assert.That(display.FixedPlacements, Is.EqualTo(7));
                Assert.That(display.AdoptedSnapshot.TryGetRenderFragment(3, out VpMultiCutRenderFragment moved), Is.True);
                Assert.That(moved.geometryLocalToWorld.m13, Is.EqualTo(1f), "drawn where it was put");
                host.step++;
                t = display.HeldPlacementTotals;
                CollectBoth(twin, "the step after: body 3 stands");
                d = Since(display, t);
                Assert.That(new[] { d.queriedOrdinary, d.fixedTaken, d.omittedFixed }, Is.EqualTo(new long[] { 1, 1, 7 }));
                Assert.That(display.FixedPlacements, Is.EqualTo(8));

                // (b) A change told with no target: every fixed one is asked once; standing, each stays fixed.
                told.OfNothingNamed();
                t = display.HeldPlacementTotals;
                work = PlaceWork(display);
                CollectBoth(twin, "a change named nothing");
                d = Since(display, t);
                Assert.That(Minus(PlaceWork(display), work), Is.EqualTo(new long[] { 1, 8, 8, 0 }), "every one asked, every one standing");
                Assert.That(new[] { d.queriedFixed, d.unfixed, d.omittedFixed, d.invalidations }, Is.EqualTo(new long[] { 8, 0, 0, 1 }));
                Assert.That(display.FixedPlacements, Is.EqualTo(8));
                host.step++;
                long[] statics = StaticWork(display);
                CollectBoth(twin, "the step after");
                Assert.That(Minus(StaticWork(display), statics)[0], Is.EqualTo(1), "skipped again");

                // (c) The lookup replaced by one that vouches the same: the structure is settled again (the lookup is part
                // of it), the parts carried, and every held and fixed one asked once; vouched for, they stay fixed.
                var another = new VouchingPlacements(twin.kept.at);
                foreach (LogicalFragmentId body in bodies) another.fixedOnes.Add(body);
                display.Placement = another;
                t = display.HeldPlacementTotals;
                work = PlaceWork(display);
                CollectBoth(twin, "the lookup replaced");
                d = Since(display, t);
                TestContext.Out.WriteLine("lookup replaced: " + FixedLine(d));
                Assert.That(d.passesStructure, Is.EqualTo(1));
                Assert.That(Minus(PlaceWork(display), work)[1], Is.EqualTo(8), "every one asked once");
                Assert.That(new[] { d.queriedFixed, d.unfixed, d.carried }, Is.EqualTo(new long[] { 8, 0, 8 }));
                Assert.That(display.FixedPlacements, Is.EqualTo(8));

                // The lookup replaced by one that vouches for nothing: asked once, every one is ordinary from there, and
                // then held over two steps as any dynamic body -- never fixed again.
                display.Placement = twin.kept.at;
                t = display.HeldPlacementTotals;
                CollectBoth(twin, "a lookup that vouches for nothing");
                d = Since(display, t);
                Assert.That(new[] { d.queriedFixed, d.unfixed }, Is.EqualTo(new long[] { 8, 8 }));
                Assert.That(display.FixedPlacements, Is.Zero);
                for (int i = 0; i < 2; i++) { host.step++; CollectBoth(twin, "a step with the plain lookup " + i); }
                Assert.That(display.FixedPlacements, Is.Zero, "not fixed by standing");
                AssertTreeHolds(display, 8, "held instead, as dynamic bodies are");

                // Back to the vouching lookup: every held one asked once, found standing and vouched for -- fixed, out of the tree.
                display.Placement = vouching;
                t = display.HeldPlacementTotals;
                CollectBoth(twin, "vouched for again");
                d = Since(display, t);
                TestContext.Out.WriteLine("vouched for again: " + FixedLine(d));
                Assert.That(new[] { d.queriedNotified, d.fixedTaken, d.treeRemovals }, Is.EqualTo(new long[] { 8, 8, 8 }), "the eight held asked as told, each fixed and taken out of the tree");
                Assert.That(display.FixedPlacements, Is.EqualTo(8));
                AssertTreeHolds(display, 0, "fixed again");

                // (d) A new body shown: the structure changes; the eight carry their state (not asked), the new one is
                // asked, and vouched for it is fixed at the next step.
                _frame++;
                Both(twin, run => AddBody(run, Stand(20)));
                vouching.fixedOnes.Add(bodies[8]);
                t = display.HeldPlacementTotals;
                CollectBoth(twin, "a body shown");
                d = Since(display, t);
                TestContext.Out.WriteLine("a body shown: " + FixedLine(d));
                Assert.That(new[] { d.passesStructure, d.carried, d.fresh, d.omittedFixed, d.queriedOrdinary }, Is.EqualTo(new long[] { 1, 8, 1, 8, 1 }));
                host.step++;
                CollectBoth(twin, "the step after");
                Assert.That(display.FixedPlacements, Is.EqualTo(9));

                // (e) A body retired: the structure changes; the others carry; the retired one is gone. The structure
                // settles over the collections that follow (the registration let go and its slots let go each change the
                // display's inputs, and each of the two snapshots then goes through the structure once): collected until
                // a collection keeps it whole.
                Both(twin, run => Assert.That(run.scene.ledger.Retire(run.bodies[7]), Is.True));
                CollectBoth(twin, "body 7 retired");
                long reuses = display.PlacementReuses;
                int settling = 0;
                while (display.PlacementReuses == reuses && settling < 6)
                {
                    settling++;
                    CollectBoth(twin, "after the retirement, collection " + settling);
                }

                TestContext.Out.WriteLine("after the retirement the inputs stood again at collection " + settling + " (a reuse); fixed " + display.FixedPlacements);
                Assert.That(display.PlacementReuses, Is.GreaterThan(reuses), "the inputs settled within six collections");
                Assert.That(display.FixedPlacements, Is.EqualTo(8));

                // A collection with no step reuses the adopted snapshot (D-204), so the other snapshot goes through the
                // structure once at the next step; the step after that has nothing to ask.
                host.step++;
                CollectBoth(twin, "a step: the other snapshot goes through the structure");
                host.step++;
                statics = StaticWork(display);
                t = display.HeldPlacementTotals;
                work = PlaceWork(display);
                CollectBoth(twin, "a step after the retirement");
                d = Since(display, t);
                TestContext.Out.WriteLine("a step after the retirement: " + FixedLine(d) + "; place work " + string.Join("/", Minus(PlaceWork(display), work))
                    + "; walks " + display.StructureWalks + ", kept whole " + display.StructuresKeptWhole + "; fixed " + display.FixedPlacements + ", held " + display.HeldPlacements);
                Assert.That(Minus(StaticWork(display), statics), Is.EqualTo(new long[] { 1, 0, 0, 1, 0, 1 }), "skipped again, the structure standing: " + FixedLine(d));
            }
        }

        [Test]
        public void FixedPlacements_ThroughACutAndItsCommit_TheClippedSidesAreNeverFixed_TheOthersStay_AndTheCommittedSidesAreFixedWhenVouchedFor()
        {
            using (Twin twin = NewTwin(8, 64, true))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                var host = new HoldHost();
                host.points.Add(k_byTheFirstTwo);
                host.Attach(display);
                VouchingPlacements vouching = Vouch(twin, 0, 1, 2, 3, 4, 5, 6, 7);
                SettleFixed(twin, () => host.step++, 8, "eight cuttable bodies");

                // A cut admitted and published on body 4: its two sides are clipped -- asked every pass, never fixed,
                // whatever the lookup says of them. The other seven stay fixed through it.
                var plane = Normalized(new float4(0.2f, 1f, 0.1f, -0.1f));
                CutOperationId admitted = default;
                Both(twin, run => admitted = Admit(run.scene.ledger, run.bodies[4], plane));
                CollectBoth(twin, "a cut admitted and prepared");
                LogicalFragmentId positive = default, negative = default;
                Both(twin, run =>
                {
                    Assert.That(run.scene.ledger.Publish(admitted, out positive, out negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
                    run.at.Put(positive, Stand(4)).Put(negative, Stand(4));
                });
                vouching.fixedOnes.Add(positive);
                vouching.fixedOnes.Add(negative);
                CollectBoth(twin, "the cut published");
                for (int i = 0; i < 3; i++)
                {
                    host.step++;
                    VpHeldPlacementTotals t = display.HeldPlacementTotals;
                    long[] work = PlaceWork(display);
                    CollectBoth(twin, "a step with the clipped sides, " + i);
                    VpHeldPlacementTotals d = Since(display, t);
                    TestContext.Out.WriteLine("clipped sides, step " + i + ": " + FixedLine(d));
                    Assert.That(Minus(PlaceWork(display), work)[1], Is.EqualTo(2), "the two clipped sides asked, nobody else");
                    // The first side is placed anew for its selected boundaries; the second for its ranges beginning
                    // elsewhere (the keep test's earlier condition): neither is fixed, whatever the lookup says of them.
                    Assert.That(new[] { d.omittedFixed, d.queriedSelected + d.queriedOrdinary, d.fixedTaken }, Is.EqualTo(new long[] { 7, 2, 0 }), "seven fixed not asked; the clipped are asked and not fixed");
                }

                Assert.That(display.FixedPlacements, Is.EqualTo(7));

                // The commit: the registration is replaced by the two sides' own, each a fragment of its own, unclipped
                // -- asked, standing, vouched for: fixed. Then steps ask nothing again.
                _frame++;
                Both(twin, run => Assert.That(CommitBothSides(run, 4, admitted, plane, positive, negative), Is.True, "committed"));
                CollectBoth(twin, "the commit");
                host.step++;
                CollectBoth(twin, "the step after the commit");
                Assert.That(display.FixedPlacements, Is.EqualTo(9), "seven and the two committed sides");
                host.step++;
                long[] statics = StaticWork(display);
                CollectBoth(twin, "a step with nothing to ask");
                Assert.That(Minus(StaticWork(display), statics)[0], Is.EqualTo(1), "the pass skipped");
            }
        }

        [Test]
        public void FixedPlacements_ACollectionRefused_LeavesWhatIsAdopted_AndTheNextAdoptionReflectsTheChange()
        {
            using (Twin twin = NewTwin(4, 4, false, true))
            {
                VpLogicalCutDisplay display = twin.kept.Display;
                var host = new HoldHost();
                host.Attach(display);
                VouchingPlacements vouching = Vouch(twin, 0, 1, 2, 3);
                SettleFixed(twin, () => host.step++, 4, "four static bodies in a small room");
                host.step++;
                CollectBoth(twin, "a step skipped");
                Drawn before = Capture(display);

                // More bodies arrive than the room holds and the room cannot be made larger: refused. What was adopted
                // stands -- the four fixed where they are -- and nothing of the kept state is taken for adopted.
                _frame++;
                for (int i = 4; i < 9; i++) AddBody(twin.kept, Stand(i));
                display.FailRoomAllocationForTest = () => true;
                long takes = display.InstanceTakes, tallies = display.StructureTallies;
                _frame++;
                Assert.That(display.TryBeginFrame(), Is.False, "the collection is refused");
                Drawn after = Capture(display);
                Assert.That(after.sides.Count, Is.EqualTo(before.sides.Count), "as many instances as were adopted");
                for (int i = 0; i < before.sides.Count; i++) Assert.That(after.sides[i].Equals(before.sides[i]), Is.True, "side " + i + " as adopted");
                Assert.That(display.FixedPlacements, Is.EqualTo(4), "the fixed ones stand");
                Assert.That(display.StructureTallies - tallies, Is.EqualTo(1), "the tally ran for the changed registrations (and refused the room)");
                Assert.That(display.InstanceTakes - takes, Is.Zero, "nothing taken for a collection that stopped before");

                // The room is to be had: the next collection takes the new bodies in; the other display gets the same.
                display.FailRoomAllocationForTest = null;
                for (int i = 4; i < 9; i++) AddBody(twin.everything, Stand(i));
                for (int b = 4; b < 9; b++) vouching.fixedOnes.Add(twin.kept.bodies[b]);
                CollectBoth(twin, "the bodies taken in");
                Assert.That(display.InstanceTakes - takes, Is.EqualTo(1), "the instances taken by the adopting collection");
                host.step++;
                CollectBoth(twin, "standing");
                Assert.That(display.FixedPlacements, Is.EqualTo(9));
                host.step++;
                long[] statics = StaticWork(display);
                CollectBoth(twin, "a step with nothing to ask");
                Assert.That(Minus(StaticWork(display), statics), Is.EqualTo(new long[] { 1, 0, 0, 1, 0, 1 }));
            }
        }
    }
}
