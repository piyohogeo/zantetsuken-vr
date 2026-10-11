using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The spatial split's convex-cut branch (DESIGN 7.9.2, the extension of 2026-10-11) through the product's own
    /// world: where no plane leaves the convexes whole, a plane that leaves every triangle whole is taken and the
    /// convexes that cross it are cut. The fixture's bodies are the trunk and the detached hand of the first
    /// version's cases, with other convexes: one box wrapped round both, or a bar from inside the trunk to inside
    /// the hand beside a convex of each. What is cut and what is not, the triangle minimum and a change of it, the
    /// frames in which no Main stage begins, and what an overtaken or abandoned attempt gives back.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static CutSpatialSplitSettings SplitCutOn(int minCutSideTriangles)
        {
            return new CutSpatialSplitSettings(
                true, 64, CutWorldRoot.CompactionReserveSeconds, 0.0002, 0.0015, new SpatialSplitSearchLimits(64, 8, 16, 131072),
                true, minCutSideTriangles, 0.0005, 0.0002, 0.003);
        }

        private static CutSpatialSplit SplitCutClosed(CutWorldRoot root, double[] remaining, int minCutSideTriangles)
        {
            CutSpatialSplit split = SplitClosed(root, remaining);
            split.Settings = SplitCutOn(minCutSideTriangles);
            return split;
        }

        /// <summary>A body of the convexes and the drawn boxes given: one owner, one geometry of closed parts.</summary>
        private LogicalFragmentId AddBodyOf(CutWorldRoot root, Vector3 at, List<float3[]> convexes, List<float3[]> drawn)
        {
            PhysicsOwnerShape shape = NewBoxesShape(convexes);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendBoxesGeometry(root.Storage, drawn);
            var actor = TrackActor(new GameObject("Body of boxes"));
            actor.transform.position = at;
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            for (int c = 0; c < convexes.Count; c++)
            {
                MeshCollider collider = actor.AddComponent<MeshCollider>();
                collider.cookingOptions = PhysicsCutCook.DefaultCooking;
                collider.convex = true;
                collider.sharedMesh = shape.MeshOf(c);
            }

            bool added = root.TryAddBody(actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, null, false, out LogicalFragmentId fragment);
            if (added)
            {
                _registered.Add(actor);
            }

            Assert.That(added, Is.True, "the body was taken into the world");
            return fragment;
        }

        /// <summary>The trunk and the detached hand drawn, with one convex round both.</summary>
        private LogicalFragmentId AddWrappedBody(CutWorldRoot root, Vector3 at)
        {
            float3[] trunk = BoxCornersAt(float3.zero, new float3(1f));
            float3[] hand = BoxCornersAt(new float3(HandLowX + 0.25f, -0.5f, 0f), new float3(0.25f));
            float3[] wrap = BoxCornersAt(new float3(1.125f, 0f, 0f), new float3(2.125f, 1f, 1f));
            return AddBodyOf(root, at, new List<float3[]> { wrap }, new List<float3[]> { trunk, hand });
        }

        private static int SplitMeshesAlive()
        {
            return Resources.FindObjectsOfTypeAll<Mesh>().Count(m => m != null && m.name.StartsWith("Zantetsu Spatial Split ", StringComparison.Ordinal));
        }

        /// <summary>
        /// **A separation that cuts nothing comes first.** With the convex-cut branch on, the lower body and its
        /// detached hand -- a convex each -- are separated by the first search as before: no mesh-led search for it,
        /// no convex cut, no mesh made, no bake, no hull read. What holds one convex round one part -- the side
        /// above, and then each of the two the split made -- is now looked at too: the mesh-led search finds nothing
        /// for it, and it is marked.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitCut_ASeparationThatCutsNothing_ComesFirst_AndNothingIsCutOrBaked()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitCutClosed(root, remaining, 1);
            LogicalFragmentId body = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            LogicalFragmentId upper = sides[0], lower = sides[1];
            Assert.That(split.Examine(upper), Is.EqualTo(SpatialSplitPass.Candidate), "one convex may be cut: a candidate now");

            // What the line that says the split was switched on says of it: the settings as they are.
            string cutOn = CutWorldRoot.DescribeSplit(SplitCutOn(200)), cutOff = CutWorldRoot.DescribeSplit(SplitOn());
            Assert.That(cutOn, Does.Contain("convex cut on").And.Contain("fewer triangles has 200 or more").And.Contain("one convex is looked at too"));
            Assert.That(cutOn, Does.Not.Contain("no convex is cut").And.Not.Contain("two or more convexes"));
            Assert.That(cutOff, Does.Contain("convex cut off").And.Contain("two or more convexes").And.Contain("no convex is cut"));

            // The run's own minimum, as a Player's arguments give it: a whole number of one or more after the argument.
            string argument = CutWorldRoot.SpatialSplitMinTrianglesArgument;
            Assert.That(CutWorldRoot.TryReadMinTriangles(new[] { "player.exe", "-zantetsuSpatialSplit", argument, "100" }, out int given, out string refusal) && given == 100 && refusal == null, Is.True);
            Assert.That(CutWorldRoot.TryReadMinTriangles(new[] { "player.exe", "-zantetsuSpatialSplit" }, out given, out refusal) || refusal != null, Is.False, "not given: the profile's stands, and nothing is refused");
            foreach (string[] bad in new[] { new[] { "player.exe", argument }, new[] { "player.exe", argument, "0" }, new[] { "player.exe", argument, "-5" }, new[] { "player.exe", argument, "many" } })
            {
                Assert.That(CutWorldRoot.TryReadMinTriangles(bad, out given, out refusal), Is.False, string.Join(" ", bad));
                Assert.That(refusal, Does.Contain("not by a whole number"), string.Join(" ", bad));
            }

            remaining[0] = 1.0;
            yield return Until(() => split.Published == 1 && split.IsIdle && !split.HasAttempt, "the lower side was split and the walk came to rest");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            Assert.That(split.LastSource, Is.EqualTo(lower));
            Assert.That(split.PublishedWithCut, Is.Zero, "nothing was cut for it");
            Assert.That(split.CutAttempts, Is.Zero);
            Assert.That(split.CutMeshesMade + split.HullsChecked, Is.Zero, "no mesh was made and no hull read");
            Assert.That(split.BakeSeconds, Is.Zero, "and nothing was baked");
            Assert.That(split.MeshLedSearches, Is.EqualTo(3), "the mesh-led search ran for the side above and for each of the two the split made");
            Assert.That(split.EndedAs(SpatialSplitEnd.NotFound), Is.EqualTo(3), "and found nothing: each is one part of the mesh");
            Assert.That(split.Examine(upper), Is.EqualTo(SpatialSplitPass.Suppressed));
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **One convex round the trunk and the hand is cut, and the two are ordinary objects.** At the default
        /// minimum the hand's twelve triangles are too few: nothing is published and the piece is marked. With the
        /// minimum lowered the mark no longer holds -- it was of another search -- and the piece is split: the convex
        /// is cut by the plane in the mesh's gap, two meshes are made, baked and read, and the two objects stand where
        /// the piece stood with its mass between them and its triangles, every one, shared out whole. One of the
        /// two is cut again as any object.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitCut_OneConvexRoundTheTrunkAndTheHand_IsCut_AtTheMinimumAsked_AndTheTwoAreOrdinaryObjects()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitCutClosed(root, remaining, CutSpatialSplitSettings.DefaultMinCutSideTriangles);
            LogicalFragmentId body = AddWrappedBody(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, 1);
            LogicalFragmentId upper = sides[0], lower = sides[1];
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner), Is.True);
            double lowerMass = lowerOwner.Mass;
            Assert.That(root.Geometry.TryGetGeometry(lower, out VpStoredGeometry lowerGeometry), Is.True);
            List<(uint, uint, uint)> lowerTriangles = TrianglesOf(root, lowerGeometry);

            // At the default minimum, 50: the hand's side has twelve triangles.
            Assert.That(CutSpatialSplitSettings.DefaultMinCutSideTriangles, Is.EqualTo(50), "the layout: the default minimum");
            remaining[0] = 1.0;
            yield return Until(() => split.EndedAs(SpatialSplitEnd.NotFound) == 2 && split.IsIdle && !split.HasAttempt, "both pieces were tried at the default minimum");
            remaining[0] = 0.0;
            Assert.That(split.Published, Is.Zero, "twelve triangles are fewer than the default minimum");
            Assert.That(split.MeshLedSearches, Is.EqualTo(2));
            Assert.That(split.CutMeshesMade, Is.Zero);
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Suppressed), "marked: not tried again as it is");

            // The minimum lowered: another search, of which the mark says nothing.
            split.Settings = SplitCutOn(12);
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Candidate), "the mark was of the search before");
            remaining[0] = 1.0;
            yield return Until(() => split.Published == 1 || split.EndedAs(SpatialSplitEnd.NotBuildable) + split.EndedAs(SpatialSplitEnd.NotClassified) > 0, "the cut split ended");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            Assert.That(split.Published, Is.EqualTo(1), "published; last refusal: " + split.LastRefusal);
            Assert.That(split.LastSource, Is.EqualTo(lower));
            Assert.That(split.PublishedWithCut, Is.EqualTo(1));
            Assert.That(split.ConvexesCut, Is.EqualTo(1), "the one convex was cut");
            Assert.That(split.CutMeshesMade, Is.EqualTo(2), "a mesh a part");
            Assert.That(split.HullsChecked, Is.EqualTo(2), "each read after its bake");
            Assert.That(split.HullsRefused, Is.Zero);
            Assert.That(split.LastSearch.meshLed && split.LastSearch.crossingConvexes == 1, Is.True);

            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(root.Ledger.IsCurrentTarget(lower), Is.False, "the piece was replaced");
            Assert.That(root.Owners.TryGet(lower, out _), Is.False);
            Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA) && root.Owners.TryGet(b, out PhysicsFragmentOwner ownerB), Is.True);
            root.Owners.TryGet(b, out ownerB);
            Assert.That(ownerA.Shape.ConvexCount == 1 && ownerB.Shape.ConvexCount == 1, Is.True, "each holds its part of the cut convex");
            Assert.That(ownerA.Mass + ownerB.Mass, Is.EqualTo(lowerMass).Within(lowerMass * 1e-5), "the piece's mass, between the two");
            Assert.That(ownerA.Mass > 0.0 && ownerB.Mass > 0.0, Is.True);
            foreach (PhysicsFragmentOwner owner in new[] { ownerA, ownerB })
            {
                MeshCollider collider = owner.Root.GetComponentInChildren<MeshCollider>();
                Assert.That(collider != null && collider.sharedMesh != null && collider.convex, Is.True, "a convex collider of its own mesh");
                Assert.That(collider.GeometryHolder.Type, Is.EqualTo(UnityEngine.LowLevelPhysics.GeometryType.ConvexMesh), "which PhysX made a convex of");
            }

            Assert.That(root.Geometry.TryGetGeometry(a, out VpStoredGeometry geometryA) && root.Geometry.TryGetGeometry(b, out VpStoredGeometry geometryB), Is.True);
            root.Geometry.TryGetGeometry(b, out geometryB);
            List<(uint, uint, uint)> shared = TrianglesOf(root, geometryA);
            shared.AddRange(TrianglesOf(root, geometryB));
            Assert.That(shared, Is.EquivalentTo(lowerTriangles), "every triangle of the piece, once, corner order kept: none was cut");
            yield return null;
            yield return null;
            Assert.That(IsDrawn(root, a) && IsDrawn(root, b) && IsDrawn(root, upper), Is.True, "all three drawn");

            // The trunk's part -- the heavier -- is cut again, as any object: its cooked part of the convex is a cut's input.
            LogicalFragmentId trunk = ownerA.Mass > ownerB.Mass ? a : b;
            var again = new LogicalFragmentId[2];
            yield return CutInTwo(root, trunk, new float4(0f, 0f, 1f, 0f), again);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the cut of the split's object is over");
            Assert.That(root.Owners.TryGet(again[0], out _) && root.Owners.TryGet(again[1], out _), Is.True, "two pieces of it");
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A convex across the gap is cut and the convexes on either side are inherited.** The lower body holds the
        /// rest of the trunk, the hand and a bar from inside one to inside the other: the first search has no plane
        /// (the bar bridges the gap), the mesh-led one has, and only the bar is cut -- two meshes, not six.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitCut_AConvexAcrossTheGap_IsCut_AndTheConvexesOnEitherSideAreInherited()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitCutClosed(root, remaining, 12);
            LogicalFragmentId body = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f), convexAcross: true);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, 3);
            LogicalFragmentId lower = sides[1];
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner), Is.True);
            Mesh[] inherited = lowerOwner.Root.GetComponentsInChildren<MeshCollider>().Select(c => c.sharedMesh).ToArray();

            remaining[0] = 1.0;
            yield return Until(() => split.Published == 1 || split.EndedAs(SpatialSplitEnd.NotBuildable) + split.EndedAs(SpatialSplitEnd.NotClassified) > 0, "the split of the lower side ended");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(split.Describe());
            Assert.That(split.Published, Is.EqualTo(1), "published; last refusal: " + split.LastRefusal);
            Assert.That(split.LastSource, Is.EqualTo(lower));
            Assert.That(split.PublishedWithCut, Is.EqualTo(1));
            Assert.That(split.ConvexesCut, Is.EqualTo(1), "the bar alone");
            Assert.That(split.CutMeshesMade, Is.EqualTo(2));
            Assert.That(root.Owners.TryGet(split.LastPositive, out PhysicsFragmentOwner ownerA), Is.True);
            Assert.That(root.Owners.TryGet(split.LastNegative, out PhysicsFragmentOwner ownerB), Is.True);
            Assert.That(ownerA.Shape.ConvexCount == 2 && ownerB.Shape.ConvexCount == 2, Is.True, "a whole convex and a part of the bar each");
            Mesh[] now = ownerA.Root.GetComponentsInChildren<MeshCollider>().Concat(ownerB.Root.GetComponentsInChildren<MeshCollider>()).Select(c => c.sharedMesh).ToArray();
            Assert.That(now.Count(m => inherited.Contains(m)), Is.EqualTo(2), "the trunk's and the hand's cooked meshes are the ones they were");
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **No Main stage of a cut split begins in a frame with a known spike or short of budget, and a later frame
        /// goes on.** The three stages -- the meshes made, the cooked hulls read, the publication -- are each held
        /// while heavy work is noted in the frame (the attempt is then not even looked at) and while nothing is left
        /// of the budget; the worker's result and the baked meshes are kept meanwhile, and the parent stands.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitCut_AFrameWithHeavyWork_OrShortOfBudget_BeginsNoMainStage_AndALaterFrameGoesOn()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitCutClosed(root, remaining, 12);
            LogicalFragmentId body = AddWrappedBody(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, 1);
            LogicalFragmentId lower = sides[1];

            // Held while heavy work is noted or nothing is left: the count named does not move. Then let through.
            IEnumerator HeldThenLetThrough(Func<int> count, int expected, string what)
            {
                int before = count();
                int leftAlone = split.CutTurnsLeftAlone;
                for (int i = 0; i < 5; i++)
                {
                    remaining[0] = 1.0;
                    CutWorldFrameNotes.Note(CutWorldFrameNotes.Work.NpcRefill);
                    yield return null;
                }

                Assert.That(count(), Is.EqualTo(before), what + ": not in a frame with heavy work noted, the budget open");
                Assert.That(split.CutTurnsLeftAlone, Is.GreaterThanOrEqualTo(leftAlone + 4), what + ": the attempt was left alone in those frames");
                remaining[0] = 0.0;
                for (int i = 0; i < 5; i++)
                {
                    yield return null;
                }

                Assert.That(count(), Is.EqualTo(before), what + ": not with nothing left of the budget");
                Assert.That(root.Ledger.IsCurrentTarget(lower), Is.True, what + ": the parent stands meanwhile");
                remaining[0] = 1.0;
                for (int i = 0; i < 600 && count() < expected; i++)
                {
                    yield return null;
                }

                remaining[0] = 0.0;
                Assert.That(count(), Is.EqualTo(expected), what + ": in a later frame");
            }

            // The numbers: issued with the budget open, collected with it closed.
            remaining[0] = 1.0;
            yield return Until(() => split.AttemptFragment == lower, "an attempt was issued for the lower side");
            remaining[0] = 0.0;
            yield return Until(() => split.AttemptCutsConvexes, "its numbers came back with a convex to cut");
            Assert.That(split.CutAttempts, Is.Zero, "not looked at yet: the budget is closed");
            Assert.That(split.CutMeshesMade, Is.Zero, "the layout: nothing was made while the budget was closed");

            yield return HeldThenLetThrough(() => split.CutMeshesMade, 2, "the meshes");
            Assert.That(split.HullsChecked, Is.Zero, "the layout: the hulls are another stage");
            yield return Until(() => split.AttemptIsCollected, "the bake came back");
            yield return HeldThenLetThrough(() => split.HullsChecked, 2, "the cooked hulls");
            Assert.That(split.Published, Is.Zero, "the layout: the publication is another stage");
            yield return HeldThenLetThrough(() => split.Published, 1, "the publication");
            TestContext.Out.WriteLine(split.Describe());
            Assert.That(split.PublishedWithCut, Is.EqualTo(1));
            Assert.That(root.Ledger.IsCurrentTarget(lower), Is.False);
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **An attempt overtaken by a cut of its piece publishes nothing and gives its meshes back, and so does one
        /// out at the world's end.** With the meshes made and the budget closed, the piece is cut: the attempt is
        /// stale, the two it would have published never exist, the meshes it made are destroyed and the piece's cut
        /// goes on as any. A second body's attempt is then left with its meshes in the bake while the world ends.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitCut_AnAttemptOvertakenByACut_OrOutAtTheWorldsEnd_PublishesNothing_AndGivesItsMeshesBack()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitCutClosed(root, remaining, 12);
            int meshesBefore = SplitMeshesAlive();
            LogicalFragmentId body = AddWrappedBody(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, 1);
            LogicalFragmentId lower = sides[1];

            remaining[0] = 1.0;
            yield return Until(() => split.AttemptFragment == lower && split.CutMeshesMade == 2, "the lower side's meshes were made");
            remaining[0] = 0.0;
            Assert.That(SplitMeshesAlive(), Is.EqualTo(meshesBefore + 2), "the layout: two meshes of the attempt's");

            // The piece is cut while the attempt waits.
            var pieces = new LogicalFragmentId[2];
            yield return CutInTwo(root, lower, new float4(0f, 0f, 1f, 0f), pieces);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the cut of the piece is over");
            remaining[0] = 1.0;
            yield return Until(() => split.EndedAs(SpatialSplitEnd.Stale) == 1, "the attempt ended as stale");
            remaining[0] = 0.0;
            Assert.That(split.Published, Is.Zero, "nothing of it was published");
            Assert.That(root.Owners.TryGet(pieces[0], out _) && root.Owners.TryGet(pieces[1], out _), Is.True, "the cut's two pieces stand");
            yield return null;
            yield return null;
            Assert.That(SplitMeshesAlive(), Is.EqualTo(meshesBefore), "the meshes it made were destroyed");

            // Another body, its attempt left in the bake at the world's end.
            LogicalFragmentId second = AddWrappedBody(root, new Vector3(0f, 0f, 20f));
            yield return null;
            var secondSides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, second, secondSides, 1);
            int made = split.CutMeshesMade;
            remaining[0] = 1.0;
            yield return Until(() => split.CutMeshesMade == made + 2, "the second body's meshes were made");
            Assert.That(split.HasAttempt, Is.True, "the layout: the attempt is out");
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.TerminationRequested, Is.False);
            yield return EndWorld(root);
            yield return null;
            yield return null;
            Assert.That(SplitMeshesAlive(), Is.EqualTo(meshesBefore), "nothing of it is left after the world");
        }

        /// <summary>
        /// **A stale attempt that cuts convexes is neither looked at nor given back in a frame the gate holds** --
        /// not only for the frame's spikes: with nothing left of the budget, in a frame the ledger changed in, and in
        /// the frame after the display's room grew. Its meshes stand and nothing is examined in all those frames;
        /// the first frame the whole gate lets through looks at it, finds it stale and gives everything back, once.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitCut_AStaleAttempt_IsNotLookedAtOrGivenBack_ShortOfBudget_InAStructuralFrame_OrAfterTheRoomGrew()
        {
            CutWorldRoot root = NewWorld(out Shader _, null, null, SmallDisplayRoom(1024));
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitCutClosed(root, remaining, 12);
            int meshesBefore = SplitMeshesAlive();
            LogicalFragmentId body = AddWrappedBody(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, 1);
            LogicalFragmentId lower = sides[1];

            remaining[0] = 1.0;
            yield return Until(() => split.AttemptFragment == lower && split.CutMeshesMade == 2, "the lower side's meshes were made");
            remaining[0] = 0.0;
            yield return Until(() => split.AttemptIsCollected, "their bake came back");

            // The piece is cut: the attempt is stale from here, and nothing has looked at it since.
            var pieces = new LogicalFragmentId[2];
            yield return CutInTwo(root, lower, new float4(0f, 0f, 1f, 0f), pieces);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the cut of the piece is over");
            yield return null;

            void StillUntouched(string when)
            {
                Assert.That(split.HasAttempt && split.AttemptFragment == lower, Is.True, when + ": the attempt is still held");
                Assert.That(split.EndedAs(SpatialSplitEnd.Stale), Is.Zero, when + ": not ended");
                Assert.That(SplitMeshesAlive(), Is.EqualTo(meshesBefore + 2), when + ": its meshes stand");
            }

            // Nothing left of the budget.
            int budgetHolds = split.HeldFor(SpatialSplitHold.BudgetShort);
            for (int i = 0; i < 10; i++)
            {
                yield return null;
            }

            StillUntouched("short of budget");
            Assert.That(split.HeldFor(SpatialSplitHold.BudgetShort), Is.GreaterThanOrEqualTo(budgetHolds + 4));

            // A frame the ledger changed in, the budget open: a body is added in each.
            int structureHolds = split.HeldFor(SpatialSplitHold.StructureChanged);
            for (int i = 0; i < 5; i++)
            {
                remaining[0] = 1.0;
                AddBody(root, new Vector3(40f + (4f * i), 0f, 0f));
                yield return null;
            }

            StillUntouched("in frames the ledger changed in");
            Assert.That(split.HeldFor(SpatialSplitHold.StructureChanged), Is.GreaterThanOrEqualTo(structureHolds + 5), "each of those frames was held as a structural one, the budget open");

            // The frame after the display's room grew (the collection of the frame a body was added in grows it): held
            // as that before the budget is asked at all.
            int roomHolds = split.HeldFor(SpatialSplitHold.DisplayRoomGrown);
            for (int i = 0; i < 12 && split.HeldFor(SpatialSplitHold.DisplayRoomGrown) == roomHolds; i++)
            {
                remaining[0] = 0.0;
                AddBody(root, new Vector3(80f + (4f * i), 0f, 0f));
                yield return null;
                yield return null;
            }

            Assert.That(split.HeldFor(SpatialSplitHold.DisplayRoomGrown), Is.GreaterThan(roomHolds), "the layout: a frame was held for the room grown");
            StillUntouched("after the room grew");

            // The first frame the whole gate lets through.
            remaining[0] = 1.0;
            yield return Until(() => split.EndedAs(SpatialSplitEnd.Stale) == 1, "the attempt ended as stale");
            remaining[0] = 0.0;
            Assert.That(split.Published, Is.Zero);
            yield return null;
            yield return null;
            Assert.That(SplitMeshesAlive(), Is.EqualTo(meshesBefore), "the meshes it made were destroyed");
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A collider mesh that cannot be made ends the attempt once, with what was made before it, and the input
        /// is not tried again.** The second of the two meshes fails: the first is destroyed with the attempt, nothing
        /// is baked or published, the piece stands, and later frames make no mesh again for it.
        /// </summary>
        [UnityTest]
        public IEnumerator SplitCut_AMeshThatCannotBeMade_EndsTheAttemptOnce_WithWhatWasMade_AndTheInputIsNotTriedAgain()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitCutClosed(root, remaining, 12);
            int meshesBefore = SplitMeshesAlive();
            LogicalFragmentId body = AddWrappedBody(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, 1);
            LogicalFragmentId lower = sides[1];
            CutSpatialSplit.FailMeshAtForTest = 1;
            try
            {
                remaining[0] = 1.0;
                yield return Until(() => split.CutMeshFailures == 1, "the second mesh could not be made");
                Assert.That(split.EndedAs(SpatialSplitEnd.NotBuildable), Is.EqualTo(1), "the attempt ended there");
                Assert.That(split.CutMeshesMade, Is.EqualTo(1), "the layout: one mesh was made before the failure");
                Assert.That(split.LastRefusal, Does.Contain("could not be made"));
                Assert.That(split.Published, Is.Zero);
                Assert.That(root.Ledger.IsCurrentTarget(lower), Is.True, "the piece stands");
                Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Suppressed), "and is marked");
                int issued = split.Issued;
                for (int i = 0; i < 30; i++)
                {
                    yield return null;
                }

                remaining[0] = 0.0;
                Assert.That(split.CutMeshFailures, Is.EqualTo(1), "once");
                Assert.That(split.CutMeshesMade, Is.EqualTo(1), "no mesh was made again");
                Assert.That(split.Issued - issued, Is.LessThanOrEqualTo(1), "the piece was not issued for again (the side above may have been)");
                Assert.That(split.BakeSeconds, Is.Zero, "nothing was baked");
                Assert.That(SplitMeshesAlive(), Is.EqualTo(meshesBefore), "the one mesh made went back with the attempt");
            }
            finally
            {
                CutSpatialSplit.FailMeshAtForTest = -1;
            }

            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }
    }
}
