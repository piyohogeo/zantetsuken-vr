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
    /// The bent partition of DESIGN 7.9.2's additional split (<see cref="CutSpatialSplitBentSearch"/>, 2026-10-15)
    /// through the product's own world. The fixture's body is a head crossing the waist and, below it, a U of three
    /// boxes with a part inside the U -- five convexes and five closed parts of one geometry -- so a cut at the waist
    /// leaves a lower side no plane separates: the part lies inside the hull of the U's corners. What the planes
    /// alone make of it (nothing, and a mark), what the bent partition publishes and where, an anchored U that stays
    /// fixed while the part goes free, and a mesh that bridges the part to the U, which keeps the object.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static CutSpatialSplitSettings SplitOnWithBent(bool cutConvexes = false, int minCutSideTriangles = CutSpatialSplitSettings.DefaultMinCutSideTriangles)
        {
            CutSpatialSplitSettings settings = SplitOn();
            settings.cutConvexes = cutConvexes;
            settings.minCutSideTriangles = minCutSideTriangles;
            settings.bentSearch = true;
            settings.bentLimits = SpatialSplitBentLimits.Default;
            return settings;
        }

        /// <summary>
        /// A head crossing the waist (x 0..1, y -1.2..1.5), standing on the U's left wall and reaching 0.2 into it,
        /// and below the waist a U -- a bottom over x 0..3 at y -4..-3, walls at x 0..1 and 2..3 up to y -1 -- with a
        /// part inside it, x 1.3..1.7, y -2.7..-1.3: one owner of five convexes, one geometry of five closed parts.
        /// The part lies inside the hull of the U's corners and the head's lower part meets the wall, so no plane
        /// leaves the lower side's convexes and triangles whole on two sides. <paramref name="meshBridge"/> draws a
        /// bar from the part into the right wall with no convex of its own.
        /// </summary>
        private LogicalFragmentId AddUWithAPartInside(CutWorldRoot root, Vector3 at, IReadOnlyList<float3> anchors = null, bool meshBridge = false)
        {
            float3[] head = BoxCornersAt(new float3(0.5f, 0.15f, 0f), new float3(0.5f, 1.35f, 0.5f));
            float3[] bottom = BoxCornersAt(new float3(1.5f, -3.5f, 0f), new float3(1.5f, 0.5f, 0.5f));
            float3[] left = BoxCornersAt(new float3(0.5f, -2f, 0f), new float3(0.5f, 1f, 0.5f));
            float3[] right = BoxCornersAt(new float3(2.5f, -2f, 0f), new float3(0.5f, 1f, 0.5f));
            float3[] part = BoxCornersAt(new float3(1.5f, -2f, 0f), new float3(0.2f, 0.7f, 0.5f));
            var convexes = new List<float3[]> { head, bottom, left, right, part };
            var drawn = new List<float3[]> { head, bottom, left, right, part };
            if (meshBridge)
            {
                drawn.Add(BoxCornersAt(new float3(2f, -2f, 0f), new float3(0.6f, 0.1f, 0.1f)));
            }

            PhysicsOwnerShape shape = NewBoxesShape(convexes);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendBoxesGeometry(root.Storage, drawn);

            var actor = TrackActor(new GameObject("U with a part inside"));
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

            bool added = root.TryAddBody(actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, anchors, false, out LogicalFragmentId fragment);
            if (added)
            {
                _registered.Add(actor);
            }

            Assert.That(added, Is.True, "the body was taken into the world");
            return fragment;
        }

        private static string DescribeBentSearch(in SpatialSplitSearchResult s)
        {
            return "search: " + s.outcome + ", planes: directions " + s.directionsTried + " tested " + s.planesTested + ", mesh-led " + s.meshLed + " directions " + s.meshDirectionsTried
                + " tested " + s.meshPlanesTested + " crossing " + s.crossingConvexes + "; bent " + s.bent + ", pieces " + s.bentPieces + ", frames " + s.bentFramesTried + ", seeds " + s.bentSeedsTried
                + ", candidates " + s.bentCandidates + ", iterations " + s.bentIterations + ", elements verified " + s.bentElementsVerified
                + ", triangle visits " + s.triangleVisits + ", least clearance " + s.gap + " (margin " + s.margin + "), triangles +" + s.positiveTriangles
                + " -" + s.negativeTriangles + ", convexes +" + s.positiveConvexes + " -" + s.negativeConvexes;
        }

        /// <summary>
        /// **A part inside a U is left by the planes and split by a bent partition, where it stood.** With the bent
        /// partition off (the profile's default) the planes find nothing and the lower side is marked; switched on,
        /// the mark is of another search and the lower side is a candidate again, and it is replaced by two
        /// fragments at one boundary: the part alone in one owner and the U with the cut head in the other, each
        /// with the source's own collider meshes (nothing cooked), at the source's placement with the velocity its
        /// own centre of mass had and the source's mass shared out, each drawn from its own registration with its
        /// share of the source's triangles over the same vertices, every triangle once. No operation, no pending cut,
        /// no transaction; the source's origin is what both carry.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitBent_APartInsideAU_IsLeftByThePlanes_AndSplitByABentPartition_WhereItStood()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            LogicalFragmentId body = AddUWithAPartInside(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, lowerConvexes: 5);
            LogicalFragmentId upper = sides[0], lower = sides[1];
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner), Is.True);
            GameObject lowerActor = lowerOwner.Root;
            PhysicsOwnerShape lowerShape = lowerOwner.Shape;
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Candidate), "the side with the U and the part");

            // 1. The planes alone (the bent partition off, as the profile's default): no plane, and the lower side is marked.
            remaining[0] = 1.0;
            yield return Until(() => split.EndedAs(SpatialSplitEnd.NotFound) == 1 && !split.HasAttempt, "the plane search ran and found none");
            remaining[0] = 0.0;
            Assert.That(split.Published, Is.Zero);
            Assert.That(split.SuppressedNow, Is.EqualTo(1), "marked: not again under these settings");
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Suppressed));
            Assert.That(split.BentSearches, Is.Zero, "the bent partition was not entered: it is off");
            Assert.That(split.LastSearch.bent, Is.False);
            TestContext.Out.WriteLine("planes alone: " + DescribeBentSearch(split.LastSearch));

            // 2. The bent partition on: another search, so the mark says nothing, and the lower side is a candidate again.
            split.Settings = SplitOnWithBent();
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Candidate), "the mark was made under another search");
            Assert.That(split.SuppressedNow, Is.EqualTo(1), "the mark itself stays, of the search it was made under");

            // Moving and turning: no reason to wait, and what the two inherit.
            lowerOwner.Body.useGravity = false;
            lowerOwner.Body.linearVelocity = new Vector3(0.3f, 0f, 1f);
            lowerOwner.Body.angularVelocity = new Vector3(0f, 0.5f, 0f);

            Assert.That(root.Geometry.TryGetGeometry(lower, out VpStoredGeometry lowerGeometry), Is.True);
            List<(uint, uint, uint)> lowerTriangles = TrianglesOf(root, lowerGeometry);
            List<int> lowerMaterials = MaterialsOf(root, lowerGeometry);
            var lowerMeshes = new List<Mesh>();
            for (int c = 0; c < lowerShape.ConvexCount; c++) lowerMeshes.Add(lowerShape.MeshOf(c));
            Matrix4x4? lowerFrame = lowerOwner.GeometryLocalToOwner;
            Assert.That(root.Geometry.TryGetGeometryFrame(lower, out Matrix4x4 lowerLineageFrame), Is.True);
            Assert.That(root.Ledger.TryGetOrigin(lower, out CutOperationId origin, out float originSide), Is.True);
            int operations = root.Ledger.OperationCount;
            int incomplete = root.Ledger.Budget.IncompleteCutOperationCount;
            int liveCut = root.Ledger.LiveCutFragmentCount;
            int owners = root.Owners.Count;
            int vertices = root.Storage.VertexCount;
            int classifications = PhysicsCutClassification.Live;

            OwnerMoment source = default;
            var entered = new List<OwnerMoment>();
            Action<LogicalFragmentId, PhysicsFragmentOwner> onAdded = (fragment, owner) =>
            {
                if (!root.Ledger.TryGetSplitSource(fragment, out LogicalFragmentId of) || of != lower)
                {
                    return;
                }

                if (entered.Count == 0 && root.Owners.TryGet(lower, out PhysicsFragmentOwner standing))
                {
                    source = OwnerMoment.Of(lower, standing);
                }

                entered.Add(OwnerMoment.Of(fragment, owner));
            };
            root.Owners.OwnerAdded += onAdded;
            remaining[0] = 1.0;
            yield return Until(
                () => split.Published == 1 || split.EndedAs(SpatialSplitEnd.NotFound) + split.EndedAs(SpatialSplitEnd.NotBuildable) + split.EndedAs(SpatialSplitEnd.NotClassified) > 1,
                "the split of the lower side ended");
            remaining[0] = 0.0;
            root.Owners.OwnerAdded -= onAdded;
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine("bent: " + DescribeBentSearch(split.LastSearch) + "; last refusal: " + split.LastRefusal);
            Assert.That(split.Published, Is.EqualTo(1), "published by the bent partition");
            Assert.That(split.PublishedBent, Is.EqualTo(1));
            Assert.That(split.BentSearches, Is.EqualTo(1));
            Assert.That(split.BentFound, Is.EqualTo(1));
            SpatialSplitSearchResult search = split.LastSearch;
            Assert.That(search.bent && search.outcome == SpatialSplitSearchOutcome.Found, Is.True);
            Assert.That(search.bentPieces, Is.GreaterThanOrEqualTo(3), "a bent surface, not a plane");
            Assert.That(search.gap, Is.GreaterThanOrEqualTo(search.margin), "the least clearance is the margin or more");
            Assert.That(search.positiveConvexes + search.negativeConvexes, Is.EqualTo(5));
            Assert.That(Mathf.Min(search.positiveConvexes, search.negativeConvexes), Is.EqualTo(1), "the part alone on its side");
            Assert.That(split.LastSource, Is.EqualTo(lower));
            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(a.IsSet && b.IsSet && a != b, Is.True);

            // The ledger: two ordinary fragments in the source's place, and nothing of a cut.
            Assert.That(root.Ledger.TryGetFragmentState(lower, out LogicalFragmentState lowerState) && lowerState == LogicalFragmentState.Replaced, Is.True);
            Assert.That(root.Ledger.IsCurrentTarget(a) && root.Ledger.IsCurrentTarget(b), Is.True, "both are targets");
            Assert.That(root.Ledger.TryGetOrigin(a, out CutOperationId originA, out float sideA) && originA == origin && sideA == originSide, Is.True, "the source's own origin");
            Assert.That(root.Ledger.TryGetOrigin(b, out CutOperationId originB, out float sideB) && originB == origin && sideB == originSide, Is.True);
            Assert.That(root.Ledger.TryGetRoot(a, out LogicalFragmentId rootA) && rootA == body && root.Ledger.TryGetRoot(b, out LogicalFragmentId rootB) && rootB == body, Is.True);
            Assert.That(root.Ledger.OperationCount, Is.EqualTo(operations), "no operation");
            Assert.That(root.Ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(incomplete), "no incomplete cut");
            Assert.That(root.Driver.Transactions.Count, Is.Zero, "no transaction");
            Assert.That(root.Ledger.LiveCutFragmentCount, Is.EqualTo(liveCut + 1), "one live cut fragment became two");
            Assert.That(LivePiecesOf(root, body), Is.EquivalentTo(new[] { upper, a, b }));
            Assert.That(root.Ledger.TryGetParent(a, out LogicalFragmentId parentA) && parentA == lower
                        && root.Ledger.TryGetParent(b, out LogicalFragmentId parentB) && parentB == lower, Is.True, "as objects, both came from the source");

            // The physics: two owners of the source's convexes, its very meshes, its mass, where it stood, moving as it moved.
            Assert.That(root.Owners.TryGet(lower, out _), Is.False, "the source's owner is gone");
            Assert.That(root.Owners.Count, Is.EqualTo(owners + 1), "one owner became two");
            Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA), Is.True);
            Assert.That(root.Owners.TryGet(b, out PhysicsFragmentOwner ownerB), Is.True);
            Assert.That(new[] { ownerA.Shape.ConvexCount, ownerB.Shape.ConvexCount }, Is.EquivalentTo(new[] { 1, 4 }), "the part alone, and the U with the cut head");
            var meshesNow = new List<Mesh>();
            for (int c = 0; c < ownerA.Shape.ConvexCount; c++) meshesNow.Add(ownerA.Shape.MeshOf(c));
            for (int c = 0; c < ownerB.Shape.ConvexCount; c++) meshesNow.Add(ownerB.Shape.MeshOf(c));
            Assert.That(meshesNow, Is.EquivalentTo(lowerMeshes), "the source's own collider meshes: nothing was cooked");
            Assert.That(ownerA.Root.GetComponentsInChildren<MeshCollider>().Length + ownerB.Root.GetComponentsInChildren<MeshCollider>().Length, Is.EqualTo(5));
            Assert.That(ownerA.FixedByAnchors || ownerB.FixedByAnchors, Is.False, "no anchor: both free");
            Assert.That(ownerA.GeometryLocalToOwner, Is.EqualTo(lowerFrame));
            Assert.That(ownerB.GeometryLocalToOwner, Is.EqualTo(lowerFrame));
            Assert.That(IsHitTarget(root, a) && IsHitTarget(root, b) && !IsHitTarget(root, lower), Is.True);

            Assert.That(entered.Count, Is.EqualTo(2), "the two entered at the one boundary");
            Assert.That(entered[0].mass + entered[1].mass, Is.EqualTo(source.mass).Within(1e-4f * source.mass), "the source's mass, shared out");
            Assert.That(entered.All(e => e.mass > 0f), Is.True);
            Vector3 moment = (entered[0].mass * entered[0].centre) + (entered[1].mass * entered[1].centre);
            Assert.That(Vector3.Distance(moment / source.mass, source.centre), Is.LessThan(1e-3f), "and its centre of mass");
            foreach (OwnerMoment e in entered)
            {
                Assert.That(Vector3.Distance(e.position, source.position), Is.LessThan(1e-5f), "at the source's placement: no offset");
                Assert.That(Quaternion.Angle(e.rotation, source.rotation), Is.LessThan(1e-2f));
                Vector3 expected = source.velocity + Vector3.Cross(source.angular, e.centre - source.centre);
                Assert.That(Vector3.Distance(e.velocity, expected), Is.LessThan(1e-3f), "the velocity its own centre of mass had: no impulse");
                Assert.That(Vector3.Distance(e.angular, source.angular), Is.LessThan(1e-4f), "and the source's turning");
            }

            // The geometry: the same vertices, every triangle of the source in exactly one of the two, materials as they were.
            Assert.That(root.Geometry.TryGetGeometry(a, out VpStoredGeometry geometryA), Is.True);
            Assert.That(root.Geometry.TryGetGeometry(b, out VpStoredGeometry geometryB), Is.True);
            Assert.That(root.Geometry.TryGetGeometryFrame(a, out Matrix4x4 frameA) && frameA == lowerLineageFrame, Is.True);
            Assert.That(root.Geometry.TryGetGeometryFrame(b, out Matrix4x4 frameB) && frameB == lowerLineageFrame, Is.True);
            List<(uint, uint, uint)> trianglesA = TrianglesOf(root, geometryA);
            List<(uint, uint, uint)> trianglesB = TrianglesOf(root, geometryB);
            Assert.That(trianglesA.Count > 0 && trianglesB.Count > 0, Is.True);
            Assert.That(trianglesA.Concat(trianglesB), Is.EquivalentTo(lowerTriangles), "none lost, none doubled, corner order kept");
            Assert.That(Mathf.Min(trianglesA.Count, trianglesB.Count), Is.EqualTo(12), "the part's twelve triangles alone on its side");
            Assert.That(MaterialsOf(root, geometryA), Is.EqualTo(lowerMaterials));
            Assert.That(MaterialsOf(root, geometryB), Is.EqualTo(lowerMaterials));
            Assert.That(root.Storage.VertexCount, Is.EqualTo(vertices), "no vertex was added");
            Assert.That(root.Display.HasOwnRegistration(a) && root.Display.HasOwnRegistration(b), Is.True);
            yield return null;
            yield return null;
            Assert.That(IsDrawn(root, a) && IsDrawn(root, b) && IsDrawn(root, upper), Is.True, "both drawn, and the side above as before");
            Assert.That(IsDrawn(root, lower) || root.Display.HasOwnRegistration(lower), Is.False);

            yield return Until(
                () => lowerActor == null && lowerShape.IsFreed
                      && (!root.Storage.TryGetIndexState(lowerGeometry.indexRange, out VpIndexRangeState old, out _, out _) || old == VpIndexRangeState.Free),
                "the source's actor, shape and index range went back");
            Assert.That(lowerMeshes.All(m => m != null), Is.True, "the inherited collider meshes are alive");
            Assert.That(PhysicsCutClassification.Live, Is.EqualTo(classifications), "the attempt's own classification went back");
            Assert.That(split.SuppressedNow, Is.Zero, "the published source's mark went with it");
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **An anchored U stays fixed, and the part inside it goes free.** The anchor in the U's bottom is judged by
        /// its signed distance to the surface: it goes with the U's side, which stays fixed where it stood; the part's
        /// side has none and is free; the source's set is let go.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitBent_AnAnchoredU_StaysFixed_AndThePartInsideIsFree()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            split.Settings = SplitOnWithBent();
            var anchor = new float3(1.5f, -3.5f, 0f);
            LogicalFragmentId body = AddUWithAPartInside(root, new Vector3(0f, 0f, 0f), anchors: new[] { anchor });
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, lowerConvexes: 5);
            LogicalFragmentId lower = sides[1];
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner) && lowerOwner.FixedByAnchors && lowerOwner.Body.isKinematic, Is.True,
                "the layout: the lower side holds the anchor and is fixed");
            Vector3 stoodAt = lowerOwner.Root.transform.position;
            float lowerMass = lowerOwner.Mass;

            remaining[0] = 1.0;
            yield return Until(() => split.Published == 1 || split.EndedAs(SpatialSplitEnd.NotFound) + split.EndedAs(SpatialSplitEnd.NotBuildable) > 0, "the split ended");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(DescribeBentSearch(split.LastSearch) + "; last refusal: " + split.LastRefusal);
            Assert.That(split.Published, Is.EqualTo(1), "the lower side was split");
            Assert.That(split.PublishedBent, Is.EqualTo(1));
            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA), Is.True);
            Assert.That(root.Owners.TryGet(b, out PhysicsFragmentOwner ownerB), Is.True);
            bool partIsA = ownerA.Shape.ConvexCount == 1;
            LogicalFragmentId part = partIsA ? a : b, u = partIsA ? b : a;
            PhysicsFragmentOwner partOwner = partIsA ? ownerA : ownerB, uOwner = partIsA ? ownerB : ownerA;
            Assert.That(uOwner.Shape.ConvexCount, Is.EqualTo(4));
            Assert.That(AnchorsOf(root, u), Is.EquivalentTo(new[] { anchor }), "the anchor is the U's own");
            Assert.That(AnchorsOf(root, part), Is.Empty, "the part has none");
            Assert.That(AnchorsOf(root, lower), Is.Empty, "the source let its set go");
            Assert.That(uOwner.FixedByAnchors && uOwner.Body.isKinematic, Is.True, "the U stays fixed");
            Assert.That(partOwner.FixedByAnchors || partOwner.Body.isKinematic, Is.False, "the part is free");
            Assert.That(Vector3.Distance(uOwner.Root.transform.position, stoodAt), Is.LessThan(1e-5f), "where the source stood");
            Assert.That(uOwner.Mass + partOwner.Mass, Is.EqualTo(lowerMass).Within(1e-3f), "the source's mass, shared out");
            yield return null;
            yield return null;
            Assert.That(IsDrawn(root, part) && IsDrawn(root, u), Is.True);
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A mesh that bridges the part to the U keeps the object**, after every search in its order: the plane that
        /// leaves the convexes whole, the mesh-led plane (the convex cut on), and the bent partition all find nothing,
        /// the lower side is marked and is not tried again, and nothing is published. The object stands as it was.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitBent_AMeshBridge_KeepsTheObject_AfterEverySearch_AndMarksIt()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            split.Settings = SplitOnWithBent(cutConvexes: true);
            LogicalFragmentId body = AddUWithAPartInside(root, new Vector3(0f, 0f, 0f), meshBridge: true);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, lowerConvexes: 5);
            LogicalFragmentId lower = sides[1];
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner), Is.True);
            int owners = root.Owners.Count;
            int classifications = PhysicsCutClassification.Live;

            // With the convex cut on, the side above (one convex) is a candidate too: both sides are searched and marked.
            remaining[0] = 1.0;
            yield return Until(() => split.SuppressedNow == 2 && !split.HasAttempt, "the searches ran for both sides and found nothing");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(DescribeBentSearch(split.LastSearch));
            Assert.That(split.EndedAs(SpatialSplitEnd.NotFound), Is.EqualTo(2));
            Assert.That(split.Published, Is.Zero);
            Assert.That(split.LastSearch.outcome, Is.EqualTo(SpatialSplitSearchOutcome.NotFound));
            Assert.That(split.MeshLedSearches, Is.EqualTo(2), "the mesh-led plane was looked for, for both sides, before the bent partition");
            Assert.That(split.BentSearches, Is.EqualTo(1), "and the bent partition after it, for the lower side alone (the side above has one convex)");
            Assert.That(split.BentFound, Is.Zero);
            Assert.That(split.BentCandidates, Is.GreaterThanOrEqualTo(1), "the lower side's convexes alone close, so its triangles were read, and the bar joins the part to the wall");
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Suppressed), "not tried again for this input");
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner still) && ReferenceEquals(still, lowerOwner), Is.True, "the object stands as it was");
            Assert.That(root.Owners.Count, Is.EqualTo(owners));
            Assert.That(PhysicsCutClassification.Live, Is.EqualTo(classifications));
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// A head crossing the waist on a body A (x 0..2, y -2..-1; the head x 0..1, y -1.2..1.5), a part B beside it
        /// (x 3..4, y -2..-1), and a convex C with nothing drawn for it reaching from over A to over B below the
        /// waist (x 1.5..3.5, y -0.4..-0.2): below the waist, no plane leaves the convexes whole; a plane at x 2.5
        /// leaves the triangles whole and would cut C; a bent partition leaves B alone above it with A, C and the
        /// head's lower part below.
        /// </summary>
        private LogicalFragmentId AddTwoPartsUnderAConvexBridge(CutWorldRoot root, Vector3 at)
        {
            float3[] head = BoxCornersAt(new float3(0.5f, 0.15f, 0f), new float3(0.5f, 1.35f, 0.5f));
            float3[] a = BoxCornersAt(new float3(1f, -1.5f, 0f), new float3(1f, 0.5f, 0.5f));
            float3[] b = BoxCornersAt(new float3(3.5f, -1.5f, 0f), new float3(0.5f, 0.5f, 0.5f));
            float3[] bridge = BoxCornersAt(new float3(2.5f, -0.3f, 0f), new float3(1f, 0.1f, 0.5f));
            var convexes = new List<float3[]> { head, a, b, bridge };
            var drawn = new List<float3[]> { head, a, b };
            PhysicsOwnerShape shape = NewBoxesShape(convexes);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendBoxesGeometry(root.Storage, drawn);
            var actor = TrackActor(new GameObject("Two parts under a convex bridge"));
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

        /// <summary>
        /// **Where a plane would cut a convex, the bent partition is taken and nothing is cut or cooked.** With the
        /// convex cut on (its triangle minimum at one, so that the mesh-led plane would be allowed to cut the bridge),
        /// the lower side is published by the bent partition: the part alone in one owner, the rest in the other,
        /// every collider mesh the source's own; no attempt cut a convex, no collider mesh was made and no hull read.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitBent_WhereAPlaneWouldCutAConvex_TheBentPartitionIsTaken_AndNothingIsCutOrCooked()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            split.Settings = SplitOnWithBent(cutConvexes: true, minCutSideTriangles: 1);
            LogicalFragmentId body = AddTwoPartsUnderAConvexBridge(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, lowerConvexes: 4);
            LogicalFragmentId lower = sides[1];
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner), Is.True);
            var lowerMeshes = new List<Mesh>();
            for (int c = 0; c < lowerOwner.Shape.ConvexCount; c++) lowerMeshes.Add(lowerOwner.Shape.MeshOf(c));
            int classifications = PhysicsCutClassification.Live;

            remaining[0] = 1.0;
            yield return Until(() => split.Published == 1 || split.EndedAs(SpatialSplitEnd.NotFound) + split.EndedAs(SpatialSplitEnd.NotBuildable) > 1, "the split of the lower side ended");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(DescribeBentSearch(split.LastSearch) + "; " + split.DescribeConvexCut());
            Assert.That(split.Published, Is.EqualTo(1));
            Assert.That(split.LastSource, Is.EqualTo(lower));
            Assert.That(split.PublishedBent, Is.EqualTo(1), "published by the bent partition");
            SpatialSplitSearchResult last = split.LastSearch;
            Assert.That(CutSpatialSplit.IsBentResult(in last), Is.True);
            Assert.That(last.meshLed, Is.False, "the mesh-led plane was not looked for, for the lower side");
            Assert.That(split.CutAttempts, Is.Zero, "no attempt came back with convexes to cut");
            Assert.That(split.PublishedWithCut == 0 && split.ConvexesCut == 0 && split.CutMeshesMade == 0 && split.HullsChecked == 0, Is.True, "nothing cut, made or cooked");
            Assert.That(root.Owners.TryGet(split.LastPositive, out PhysicsFragmentOwner ownerA), Is.True);
            Assert.That(root.Owners.TryGet(split.LastNegative, out PhysicsFragmentOwner ownerB), Is.True);
            Assert.That(new[] { ownerA.Shape.ConvexCount, ownerB.Shape.ConvexCount }, Is.EquivalentTo(new[] { 1, 3 }), "the part alone; A, the bridge and the cut head");
            var meshesNow = new List<Mesh>();
            for (int c = 0; c < ownerA.Shape.ConvexCount; c++) meshesNow.Add(ownerA.Shape.MeshOf(c));
            for (int c = 0; c < ownerB.Shape.ConvexCount; c++) meshesNow.Add(ownerB.Shape.MeshOf(c));
            Assert.That(meshesNow, Is.EquivalentTo(lowerMeshes), "the source's own collider meshes, the bridge's whole: nothing cooked");
            yield return null;
            yield return null;
            Assert.That(IsDrawn(root, split.LastPositive) && IsDrawn(root, split.LastNegative), Is.True);
            yield return Until(() => PhysicsCutClassification.Live == classifications, "the attempt's classification went back");
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A bent partition found, then overtaken by a cut, a retirement or a change of settings before it is
        /// published, is not published and marks nothing.** Three U bodies; each attempt's work is held back from
        /// the collection once finished (the bent partition found), and meanwhile its fragment is cut the ordinary
        /// way, or retired by the piece lifetime, or the search's settings are changed. Each result is stale: nothing
        /// is published, no owner is made, nothing is marked, the classification the result carried goes back, and
        /// the fragment stands as its cut or its retirement left it.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitBent_ABentResultOvertaken_ByACut_ARetirement_OrAChangeOfSettings_IsNotPublished_AndMarksNothing()
        {
            HoldingExecutor background = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.BackgroundPool
                    ? background = Held(new HoldingExecutor(WorkerPoolExecutor.BackgroundPool(2)))
                    : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            split.Settings = SplitOnWithBent();
            var bodies = new[] { AddUWithAPartInside(root, new Vector3(0f, 0f, 0f)), AddUWithAPartInside(root, new Vector3(0f, 0f, 12f)), AddUWithAPartInside(root, new Vector3(0f, 0f, 24f)) };
            foreach (LogicalFragmentId body in bodies) root.Lifetime.MarkLineage(body);
            yield return null;
            var lowers = new List<LogicalFragmentId>();
            foreach (LogicalFragmentId body in bodies)
            {
                var sides = new LogicalFragmentId[2];
                yield return CutAtTheWaist(root, body, sides, lowerConvexes: 5);
                lowers.Add(sides[1]);
            }

            int owners = root.Owners.Count;
            int classifications = PhysicsCutClassification.Live;

            // 1. Cut the ordinary way while the result is held.
            background.HoldEverything = true;
            remaining[0] = 1.0;
            yield return Until(() => split.HasAttempt, "an attempt was issued");
            remaining[0] = 0.0;
            LogicalFragmentId cutUnder = split.AttemptFragment;
            Assert.That(lowers, Does.Contain(cutUnder), "for one of the lower sides");
            yield return Until(() => background.HoldingCount > 0, "its work finished and is held back from the collection");
            var pieces = new LogicalFragmentId[2];
            yield return CutInTwo(root, cutUnder, new float4(0f, 0f, 1f, 0f), pieces);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the cut's record is over");
            Assert.That(root.Ledger.IsCurrentTarget(cutUnder), Is.False, "the layout: replaced by its cut");
            int afterCut = root.Owners.Count;
            background.HoldEverything = false;
            background.ReleaseEverything();
            yield return Until(() => !split.HasAttempt, "the result arrived and was given back");
            Assert.That(split.EndedAs(SpatialSplitEnd.Stale), Is.EqualTo(1), "stale: what it was computed from is no longer the fragment's");
            Assert.That(split.BentFound, Is.EqualTo(1), "the bent partition had been found");
            Assert.That(split.Published + split.PublishedBent, Is.Zero);
            Assert.That(split.SuppressedNow, Is.Zero, "and nothing is recorded as failed");
            Assert.That(root.Owners.Count, Is.EqualTo(afterCut), "no owner was made");
            Assert.That(root.Ledger.IsCurrentTarget(pieces[0]) && root.Ledger.IsCurrentTarget(pieces[1]), Is.True, "the cut's own pieces stand");
            Assert.That(PhysicsCutClassification.Live, Is.EqualTo(classifications), "the result's classification went back");

            // 2. Retired while the result is held.
            background.HoldEverything = true;
            remaining[0] = 1.0;
            yield return Until(() => split.HasAttempt, "another attempt was issued");
            remaining[0] = 0.0;
            LogicalFragmentId retiredUnder = split.AttemptFragment;
            yield return Until(() => background.HoldingCount > 0, "its work finished and is held back");
            Assert.That(root.Lifetime.TryRetire(retiredUnder, out string why), Is.True, "retired by the piece lifetime's own retirement: " + why);
            int afterRetirement = root.Owners.Count;
            background.HoldEverything = false;
            background.ReleaseEverything();
            yield return Until(() => !split.HasAttempt, "the result arrived and was given back");
            Assert.That(split.EndedAs(SpatialSplitEnd.Stale), Is.EqualTo(2));
            Assert.That(split.BentFound, Is.EqualTo(2));
            Assert.That(split.Published, Is.Zero);
            Assert.That(split.SuppressedNow, Is.Zero);
            Assert.That(IsRetired(root, retiredUnder), Is.True, "retired, and nothing of it came back");
            Assert.That(root.Owners.Count, Is.EqualTo(afterRetirement));
            Assert.That(PhysicsCutClassification.Live, Is.EqualTo(classifications));

            // 3. The settings changed while the result is held: another search, whose result this is not.
            background.HoldEverything = true;
            remaining[0] = 1.0;
            yield return Until(() => split.HasAttempt, "a third attempt was issued");
            remaining[0] = 0.0;
            LogicalFragmentId changedUnder = split.AttemptFragment;
            Assert.That(root.Owners.TryGet(changedUnder, out PhysicsFragmentOwner standing), Is.True);
            yield return Until(() => background.HoldingCount > 0, "its work finished and is held back");
            CutSpatialSplitSettings narrower = SplitOnWithBent();
            narrower.bentLimits.maxPieces = SpatialSplitBentLimits.DefaultPieces - 1;
            split.Settings = narrower;
            background.HoldEverything = false;
            background.ReleaseEverything();
            yield return Until(() => !split.HasAttempt, "the result arrived and was given back");
            Assert.That(split.EndedAs(SpatialSplitEnd.Stale), Is.EqualTo(3), "stale: of a search with other settings");
            Assert.That(split.BentFound, Is.EqualTo(3));
            Assert.That(split.Published, Is.Zero);
            Assert.That(split.SuppressedNow, Is.Zero, "nothing marked: a stale result says nothing of the input");
            Assert.That(root.Ledger.IsCurrentTarget(changedUnder), Is.True, "the fragment stands");
            Assert.That(root.Owners.TryGet(changedUnder, out PhysicsFragmentOwner now) && ReferenceEquals(now, standing) && !now.IsWithdrawn, Is.True, "with the same owner");
            Assert.That(root.Ledger.SplitReplacements, Is.Zero);
            Assert.That(root.Display.SplitCommits, Is.Zero);
            Assert.That(PhysicsCutClassification.Live, Is.EqualTo(classifications));
            yield return null;
            yield return null;
            Assert.That(IsDrawn(root, changedUnder) && IsDrawn(root, pieces[0]) && IsDrawn(root, pieces[1]), Is.True);
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            TestContext.Out.WriteLine(split.Describe());
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A bent partition found, with no index room for its two ranges, is given back and the object stands; it
        /// is published when the room is back.** A storage small enough for the case to take all of its free index
        /// room: the result ends RoomShort, marks nothing (a shortage of room is not a failed search), makes no
        /// owner and changes no registration, and its classification goes back; the room given back, a second
        /// attempt publishes by the bent partition.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitBent_ABentResultShortOfIndexRoom_IsGivenBack_TheObjectStands_AndIsPublishedWhenTheRoomIsBack()
        {
            CutWorldRoot root = NewWorld(
                out Shader _, null, null,
                profile =>
                {
                    SetPrivate(profile, "indexReserve", 2048);
                    SetPrivate(profile, "indexInitialCommit", 2048);
                    SetPrivate(profile, "gpuIndexInitialCapacity", 2048);
                });
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            split.Settings = SplitOnWithBent();
            LogicalFragmentId body = AddUWithAPartInside(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides, lowerConvexes: 5);
            LogicalFragmentId lower = sides[1];
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner), Is.True);
            int owners = root.Owners.Count;
            int classifications = PhysicsCutClassification.Live;

            void Unchanged(string when)
            {
                Assert.That(split.Published, Is.Zero, when);
                Assert.That(root.Ledger.IsCurrentTarget(lower), Is.True, when + ": live as it was");
                Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner now) && ReferenceEquals(now, lowerOwner) && !now.IsWithdrawn, Is.True, when + ": with the same owner");
                Assert.That(root.Owners.Count, Is.EqualTo(owners), when);
                Assert.That(root.Ledger.SplitReplacements, Is.Zero, when);
                Assert.That(root.Display.SplitCommits, Is.Zero, when);
                Assert.That(root.Display.HasOwnRegistration(lower), Is.True, when + ": drawn from its own registration");
            }

            // No index room: every range the two could take is taken by reservations of the case's own.
            Assert.That(root.Geometry.TryGetGeometry(lower, out VpStoredGeometry lowerGeometry), Is.True);
            int needed = 3 * TrianglesOf(root, lowerGeometry).Count;
            var hogs = new List<VpCutOutputReservation>();
            while (root.Storage.TryReserveCutOutput(lowerGeometry, 0, needed, 0, 0, out VpCutOutputReservation hog))
            {
                hogs.Add(hog);
                Assert.That(hogs.Count, Is.LessThan(256), "the layout: the small storage runs out");
            }

            Assert.That(hogs.Count, Is.GreaterThan(0), "the layout: the room was there before");
            remaining[0] = 1.0;
            yield return Until(() => split.EndedAs(SpatialSplitEnd.RoomShort) == 1 && !split.HasAttempt, "the attempt was given back for the room");
            remaining[0] = 0.0;
            TestContext.Out.WriteLine(DescribeBentSearch(split.LastSearch) + "; last refusal: " + split.LastRefusal);
            Assert.That(split.BentFound, Is.EqualTo(1), "the bent partition had been found");
            Assert.That(split.SuppressedNow, Is.Zero, "a shortage of room is not a failed search");
            Assert.That(PhysicsCutClassification.Live, Is.EqualTo(classifications), "the result's classification went back");
            Unchanged("no index room");

            // The room back, the budget open: tried again, and published.
            foreach (VpCutOutputReservation hog in hogs)
            {
                Assert.That(root.Storage.TryCancelCutOutput(hog), Is.True);
            }

            remaining[0] = 1.0;
            yield return Until(() => split.Published == 1, "published once the room was back");
            remaining[0] = 0.0;
            Assert.That(split.Issued, Is.EqualTo(2), "by a second attempt");
            Assert.That(split.PublishedBent, Is.EqualTo(1));
            Assert.That(split.LastSource, Is.EqualTo(lower));
            Assert.That(root.Owners.Count, Is.EqualTo(owners + 1));
            yield return null;
            yield return null;
            Assert.That(IsDrawn(root, split.LastPositive) && IsDrawn(root, split.LastNegative), Is.True);
            yield return Until(() => PhysicsCutClassification.Live == classifications, "the published attempt's classification went back too");
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }
    }
}
