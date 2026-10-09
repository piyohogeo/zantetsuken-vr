using System;
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// DESIGN 7.9.3's delayed collection of physics objects with nothing to show (<see cref="CutEmptyOwnerCollection"/>,
    /// Phase 5.7) through the product's own world. The fixture's box is drawn above the plane it is cut at, so the cut
    /// leaves a side below with half of the physics box and no geometry: what the collection is for. What it chooses and
    /// what it leaves, what one retirement ends and what it keeps, a choice overtaken by a cut or another retirement,
    /// nothing coming back afterwards, the frames it does not start in, and nothing at all while it is off. The world is
    /// ended by every case, so what it holds is given back at its release.
    /// </summary>
    public partial class CutWorldRootPlayModeTests
    {
        // The display geometry stands this far above the physics box's centre: wholly above the plane y = 0.
        private static readonly Vector3 k_drawnAbove = new Vector3(0f, 1.5f, 0f);
        private static readonly float4 k_planeUp = new float4(0f, 1f, 0f, 0f);

        private static CutEmptyOwnerCollectionSettings EmptyOwnersOn(int examinePerFrame = 64)
        {
            return new CutEmptyOwnerCollectionSettings(true, examinePerFrame, 1, CutWorldRoot.CompactionReserveSeconds, 0.0003);
        }

        private static CutEmptyOwnerCollectionSettings EmptyOwnersOff()
        {
            return new CutEmptyOwnerCollectionSettings(false, 64, 1, CutWorldRoot.CompactionReserveSeconds, 0.0003);
        }

        /// <summary>
        /// The collection of this world, on, with a frame budget the case decides: closed (nothing left, so every turn
        /// of the product's own frame is held) until the case opens it.
        /// </summary>
        private static CutEmptyOwnerCollection EmptyOwnersClosed(CutWorldRoot root, double[] remaining)
        {
            CutEmptyOwnerCollection collection = root.EmptyOwners;
            remaining[0] = 0.0;
            collection.RemainingMainSeconds = () => remaining[0];
            collection.Settings = EmptyOwnersOn();
            return collection;
        }

        /// <summary>The same box as <see cref="AddBody"/>'s, drawn above its physics, with anchors when a case gives some.</summary>
        private LogicalFragmentId AddBodyDrawnAbove(
            CutWorldRoot root, Vector3 at, bool building = false, IReadOnlyList<float3> anchors = null, bool flat = false)
        {
            PhysicsOwnerShape shape = NewBoxShape(out Mesh _);
            _disposables.Add(shape);
            VpStoredGeometry geometry = flat ? AppendFlatGeometry(root.Storage, k_drawnAbove) : AppendBoxGeometry(root.Storage, k_drawnAbove);

            var actor = TrackActor(new GameObject("Body"));
            actor.transform.position = at;
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.automaticCenterOfMass = false;
            body.automaticInertiaTensor = false;
            body.mass = (float)ParentMass;
            body.centerOfMass = Vector3.zero;
            body.inertiaTensor = new Vector3(4f, 4f, 4f);
            MeshCollider collider = actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = shape.MeshOf(0);

            bool added = root.TryAddBody(
                actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, anchors, building, out LogicalFragmentId fragment);
            if (added)
            {
                _registered.Add(actor);
            }

            Assert.That(added, Is.True, "the body was taken into the world");
            return fragment;
        }

        /// <summary>
        /// The box's topology with every corner on one segment: twelve triangles, each of no area, closed as the box is.
        /// A geometry that is not empty and shows nothing.
        /// </summary>
        private static VpStoredGeometry AppendFlatGeometry(VpCpuGeometryStorage storage, Vector3 offset)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                int start = indices.Count;
                for (int face = 0; face < k_faces.Length; face++)
                {
                    if (k_faces[face].submesh != submesh)
                    {
                        continue;
                    }

                    int[] c = k_faces[face].cycle;
                    uint b = (uint)vertices.Count;
                    for (int k = 0; k < 4; k++)
                    {
                        vertices.Add(new VpRenderVertex
                        {
                            position = new float3(0f, 0f, k_corners[c[k]].z) + (float3)(Vector3)offset,
                            normal = new float3(0f, 1f, 0f),
                            uv0 = new float2(0.5f, 0.5f),
                        });
                        topology.Add(c[k]);
                    }

                    indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                }

                submeshes.Add(new VpGeometrySubmesh(start, indices.Count - start, submesh == 0 ? SideMaterial : EndMaterial));
            }

            Assert.That(
                storage.TryAppendCuttable(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), k_corners.Length, submeshes.ToArray(),
                    out VpStoredGeometry geometry, out _),
                Is.True,
                "the geometry of triangles without area is appended");
            return geometry;
        }

        /// <summary>
        /// Cuts <paramref name="body"/> at y = 0 and waits until the cut is over: [0] the side above, with all of the
        /// geometry, [1] the side below, with half of the physics box and none.
        /// </summary>
        private static IEnumerator CutLeavingAnEmptySide(CutWorldRoot root, LogicalFragmentId body, LogicalFragmentId[] sides)
        {
            yield return CutInTwo(root, body, k_planeUp, sides);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the cut's record is over");
            Assert.That(root.Geometry.TryGetGeometry(sides[0], out _), Is.True, "the layout: the side above has the geometry");
            Assert.That(root.Geometry.HasNoGeometry(sides[1]), Is.True, "the layout: the side below is settled as empty");
            Assert.That(
                root.Owners.TryGet(sides[1], out PhysicsFragmentOwner below) && below.Body != null && below.Shape.ConvexCount > 0,
                Is.True, "the layout: the side below stands in the physics, with a convex");
        }

        /// <summary>Turns of the collection, one a frame with the budget open, until <paramref name="retired"/> retirements were started.</summary>
        private static IEnumerator CollectUntil(CutEmptyOwnerCollection collection, double[] remaining, int retired, string what)
        {
            remaining[0] = 1.0;
            yield return Until(() => collection.Retired >= retired, what);
            remaining[0] = 0.0;
        }

        private static bool IsRetired(CutWorldRoot root, LogicalFragmentId fragment)
        {
            return root.Ledger.TryGetFragmentState(fragment, out LogicalFragmentState state) && state == LogicalFragmentState.Retired;
        }

        /// <summary>
        /// **Only a fragment whose geometry a commit settled as empty is chosen.** In one world: a side left empty; its
        /// sibling with all the geometry; the two sides of a box cut far away and seen by no camera; a side holding
        /// nothing but triangles of no area; and a body never cut. The walk finds exactly the first.
        /// </summary>
        [UnityTest]
        public IEnumerator EmptyOwners_ChooseOnlyAFragmentWhoseGeometryIsSettledEmpty()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutEmptyOwnerCollection collection = EmptyOwnersClosed(root, remaining);

            LogicalFragmentId drawnAbove = AddBodyDrawnAbove(root, Vector3.zero);
            LogicalFragmentId farAway = AddBody(root, new Vector3(0f, 0f, -60f));
            LogicalFragmentId flat = AddBodyDrawnAbove(root, new Vector3(8f, 0f, 0f), flat: true);
            LogicalFragmentId uncut = AddBody(root, new Vector3(-8f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            var farSides = new LogicalFragmentId[2];
            var flatSides = new LogicalFragmentId[2];
            yield return CutLeavingAnEmptySide(root, drawnAbove, sides);
            yield return CutInTwo(root, farAway, k_planeUp, farSides);
            yield return CutLeavingAnEmptySide(root, flat, flatSides);
            yield return Until(() => root.Driver.Transactions.Count == 0, "every cut's record is over");
            Assert.That(root.Geometry.TryGetGeometry(farSides[0], out _) && root.Geometry.TryGetGeometry(farSides[1], out _), Is.True, "the layout: both far sides have geometry");
            Assert.That(root.Geometry.TryGetGeometry(flatSides[0], out _) && !root.Geometry.HasNoGeometry(flatSides[0]), Is.True,
                "the layout: the side above the flat body holds its triangles of no area");

            // Asked one by one: what each is.
            Assert.That(collection.TryChoose(sides[1], out EmptyOwnerCandidate chosen) && chosen.Fragment == sides[1], Is.True, "the side settled as empty");
            Assert.That(collection.TryChoose(sides[0], out _), Is.False, "its sibling has the geometry");
            Assert.That(collection.TryChoose(farSides[0], out _) || collection.TryChoose(farSides[1], out _), Is.False, "far and unseen is not empty");
            Assert.That(collection.TryChoose(flatSides[0], out _), Is.False, "triangles of no area are a geometry");
            Assert.That(collection.TryChoose(uncut, out _), Is.False, "never cut");
            Assert.That(collection.TryChoose(drawnAbove, out _), Is.False, "replaced by its cut: not live");

            var collected = new HashSet<LogicalFragmentId>();
            bool ordinaryRetirementSeen = false;
            root.Owners.OwnerRetiring += (fragment, owner) =>
            {
                if (collection.RetiringFragment == fragment)
                {
                    Assert.That(root.Geometry.HasNoGeometry(fragment), Is.True, "the check observes committed empty geometry before it is forgotten");
                    collected.Add(fragment);
                }
                else
                {
                    ordinaryRetirementSeen = true;
                }
            };

            // And by the walk, with the two empty sides: both go, a turn each, and nothing else does.
            yield return CollectUntil(collection, remaining, 1, "the first empty side was collected");
            remaining[0] = 1.0;
            yield return null;
            remaining[0] = 0.0;
            Assert.That(collection.Retired, Is.EqualTo(1), "the next structural collection does not share a frame with another retirement");
            Assert.That(collection.LastHold, Is.Not.EqualTo(EmptyOwnerHold.None), "the frame reflecting our retirement is held too");
            yield return CollectUntil(collection, remaining, 2, "the other empty side was collected later");
            yield return null;
            yield return null;
            Assert.That(collection.Retired, Is.EqualTo(2));
            Assert.That(IsRetired(root, sides[1]) && IsRetired(root, flatSides[1]), Is.True, "the two sides without geometry");
            foreach (LogicalFragmentId kept in new[] { sides[0], farSides[0], farSides[1], flatSides[0], uncut })
            {
                Assert.That(root.Ledger.IsCurrentTarget(kept) && root.Owners.TryGet(kept, out _), Is.True, "kept: " + kept);
            }

            Assert.That(collection.PassedAs(EmptyOwnerPass.NotEmpty), Is.GreaterThan(0), "the others were looked at and passed over as not empty");
            Assert.That(collected, Is.EquivalentTo(new[] { sides[1], flatSides[1] }), "only actual empty collections are identifiable to the city check");
            Assert.That(collection.RetiringFragment.IsSet, Is.False, "the retirement context does not outlive the notification");
            Assert.That(root.Ledger.Retire(uncut), Is.True);
            Assert.That(root.Owners.Retire(uncut), Is.True);
            root.Geometry.Forget(uncut);
            Assert.That(ordinaryRetirementSeen, Is.True, "an ordinary retirement must not be exempted as empty collection");
            Assert.That(collected.Contains(uncut), Is.False);
            TestContext.Out.WriteLine(collection.Describe());
            collection.Settings = EmptyOwnersOff();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A geometry still being computed is not empty.** The geometry work of the cut is held after its physics is
        /// published: the side that will turn out empty stands there, owned, and is not chosen. Once the work is let go
        /// and the cut commits, it is.
        /// </summary>
        [UnityTest]
        public IEnumerator AnEmptyOwner_IsNotChosenBeforeItsGeometryIsCommitted()
        {
            HoldingExecutor geometry = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.GeometryPool
                    ? geometry = Held(new HoldingExecutor(WorkerPoolExecutor.GeometryPool(2)))
                    : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutEmptyOwnerCollection collection = EmptyOwnersClosed(root, remaining);
            LogicalFragmentId body = AddBodyDrawnAbove(root, Vector3.zero);
            yield return null;

            geometry.HoldEverything = true;
            ProvisionalCutAsk ask = Ask(body, k_planeUp);
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            CutOperationId operation = AdmittedFor(root, new[] { body })[0];
            yield return Until(
                () => root.Ledger.TryGetOperation(operation, out LogicalCutOperation op) && op.state == LogicalCutOperationState.Published
                      && root.Owners.TryGet(op.positive, out _) && root.Owners.TryGet(op.negative, out _),
                "the cut's physics is published with both owners");
            LogicalCutOperation record = OperationOf(root, operation);
            Assert.That(root.Geometry.StageOf(operation), Is.Not.EqualTo(CutGeometryStage.Committed), "the layout: its geometry is held");

            Assert.That(collection.TryChoose(record.negative, out _), Is.False, "not settled yet: not empty");
            remaining[0] = 1.0;
            for (int i = 0; i < 6; i++)
            {
                yield return null;
            }

            Assert.That(collection.Retired, Is.Zero, "and the walk starts nothing for it");
            Assert.That(root.Ledger.IsCurrentTarget(record.negative), Is.True);
            remaining[0] = 0.0;

            geometry.HoldEverything = false;
            geometry.ReleaseEverything();
            yield return Until(() => root.Geometry.StageOf(operation) == CutGeometryStage.Committed, "the cut committed");
            Assert.That(root.Geometry.HasNoGeometry(record.negative), Is.True, "the layout: settled as empty now");
            yield return CollectUntil(collection, remaining, 1, "the side below was collected once its emptiness was settled");
            Assert.That(IsRetired(root, record.negative), Is.True);
            Assert.That(root.Ledger.IsCurrentTarget(record.positive), Is.True, "the side with the geometry stays");
            collection.Settings = EmptyOwnersOff();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Touching something, moving and holding an anchor are no reasons to wait.** Three empty sides: one resting
        /// against a collider, one given a velocity, one fixed by an anchor. Each is collected when its turn comes.
        /// </summary>
        [UnityTest]
        public IEnumerator AnEmptyOwner_Touching_Moving_OrAnchored_IsCollected()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutEmptyOwnerCollection collection = EmptyOwnersClosed(root, remaining);
            LogicalFragmentId touching = AddBodyDrawnAbove(root, new Vector3(0f, 0f, 0f));
            LogicalFragmentId moving = AddBodyDrawnAbove(root, new Vector3(8f, 0f, 0f));
            LogicalFragmentId anchored = AddBodyDrawnAbove(root, new Vector3(16f, 0f, 0f), anchors: new[] { new float3(0f, -0.5f, 0f) });
            yield return null;
            var touchingSides = new LogicalFragmentId[2];
            var movingSides = new LogicalFragmentId[2];
            var anchoredSides = new LogicalFragmentId[2];
            yield return CutLeavingAnEmptySide(root, touching, touchingSides);
            yield return CutLeavingAnEmptySide(root, moving, movingSides);
            yield return CutLeavingAnEmptySide(root, anchored, anchoredSides);

            // Something to rest on: a floor whose top is the bottom of the first box, and gravity for its empty side alone.
            GameObject floor = Track(new GameObject("Floor"));
            floor.transform.position = new Vector3(0f, -1.5f, 0f);
            floor.AddComponent<BoxCollider>().size = new Vector3(4f, 1f, 4f);
            root.Owners.TryGet(touchingSides[1], out PhysicsFragmentOwner touchingOwner);
            float restingAt = touchingOwner.Root.transform.position.y;
            touchingOwner.Body.useGravity = true;
            root.Owners.TryGet(anchoredSides[1], out PhysicsFragmentOwner anchoredOwner);
            Assert.That(anchoredOwner.FixedByAnchors, Is.True, "the layout: the empty side holds the anchor");
            long step = CutPhysicsStep.Clock.StepId;
            yield return Until(() => CutPhysicsStep.Clock.StepId >= step + 12, "twelve physics steps under gravity");
            Assert.That(Mathf.Abs(touchingOwner.Root.transform.position.y - restingAt), Is.LessThan(0.05f),
                "the layout: under gravity it has not fallen -- it rests on the floor");
            root.Owners.TryGet(movingSides[1], out PhysicsFragmentOwner movingOwner);
            movingOwner.Body.linearVelocity = new Vector3(0f, 0f, 2f);
            Assert.That(movingOwner.Body.linearVelocity.sqrMagnitude, Is.GreaterThan(0.01f), "the layout: it is moving");

            yield return CollectUntil(collection, remaining, 3, "the three empty sides were collected");
            Assert.That(IsRetired(root, touchingSides[1]) && IsRetired(root, movingSides[1]) && IsRetired(root, anchoredSides[1]), Is.True);
            Assert.That(root.Ledger.IsCurrentTarget(touchingSides[0]) && root.Ledger.IsCurrentTarget(movingSides[0]) && root.Ledger.IsCurrentTarget(anchoredSides[0]),
                Is.True, "their siblings stay");
            Assert.That(collection.Retired, Is.EqualTo(3));
            Assert.That(root.TerminationRequested, Is.False);
            collection.Settings = EmptyOwnersOff();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **One retirement ends its actor and the building World D6 that actor carries, once, and leaves the rest.**
        /// The empty side of a building's cut: its owner leaves the scene at once and its constraint is counted out
        /// once; the engine destroys the actor by the next frame. Its sibling keeps its owner, its D6, its mass, its
        /// collider mesh, its geometry reference and its place in the drawing; the cut's record is as it was; and
        /// further turns end nothing more.
        /// </summary>
        [UnityTest]
        public IEnumerator ABuildingsEmptyOwner_EndsItsActorAndItsD6Once_AndLeavesItsSiblingAndWhatTheyShare()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutEmptyOwnerCollection collection = EmptyOwnersClosed(root, remaining);
            LogicalFragmentId body = AddBodyDrawnAbove(root, Vector3.zero, building: true);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutLeavingAnEmptySide(root, body, sides);
            yield return null;
            Assert.That(root.Owners.TryGet(sides[1], out PhysicsFragmentOwner gone) && gone.BuildingWorldConstraint != null, Is.True,
                "the layout: the empty side carries a building World D6");
            Assert.That(root.Owners.TryGet(sides[0], out PhysicsFragmentOwner kept) && kept.BuildingWorldConstraint != null, Is.True,
                "the layout: so does its sibling");
            GameObject goneActor = gone.Root;
            ConfigurableJoint goneJoint = gone.BuildingWorldConstraint;
            ConfigurableJoint keptJoint = kept.BuildingWorldConstraint;
            Mesh keptMesh = kept.Shape.MeshOf(0);
            float keptMass = kept.Mass;
            int constraints = root.Owners.SystemConstraintCount;
            int owners = root.Owners.Count;
            int references = root.References.LiveGeometryCount;
            Assert.That(root.Ledger.TryGetOrigin(sides[1], out CutOperationId origin, out _), Is.True);
            LogicalCutOperation before = OperationOf(root, origin);
            Assert.That(IsDrawn(root, sides[0]) && !IsDrawn(root, sides[1]), Is.True, "the layout: only the side with geometry is drawn");

            Assert.That(collection.TryChoose(sides[1], out EmptyOwnerCandidate candidate), Is.True);
            Assert.That(collection.TryApply(in candidate, out EmptyOwnerPass refusal), Is.True, "applied: " + refusal);

            // At once: out of the ledger, the registry, the hits and the scene; counted out once.
            Assert.That(IsRetired(root, sides[1]), Is.True);
            Assert.That(root.Owners.TryGet(sides[1], out _), Is.False, "its owner is gone");
            Assert.That(IsHitTarget(root, sides[1]), Is.False, "and its hit shape");
            Assert.That(root.Owners.Count, Is.EqualTo(owners - 1));
            Assert.That(root.Owners.SystemConstraintCount, Is.EqualTo(constraints - 1), "its D6 is counted out, once");
            Assert.That(gone.IsWithdrawn && gone.IsReleased, Is.True);
            Assert.That(goneActor == null || !goneActor.activeInHierarchy, Is.True, "its actor is out of the scene in the same frame");
            Assert.That(root.Geometry.HasNoGeometry(sides[1]) || root.Geometry.TryGetGeometryFrame(sides[1], out _), Is.False, "the DAG keeps nothing for it");
            Assert.That(collection.Retired, Is.EqualTo(1));
            Assert.That(collection.ConstraintsRetired, Is.EqualTo(1));
            Assert.That(collection.ActorsLeaving, Is.EqualTo(1), "its destruction is the engine's, later in the frame");

            // The same candidate again, and further turns: nothing more.
            Assert.That(collection.TryApply(in candidate, out refusal), Is.False, "a retired fragment is not retired again");
            remaining[0] = 1.0;
            yield return null;
            yield return null;
            yield return null;
            remaining[0] = 0.0;
            Assert.That(goneActor == null, Is.True, "its actor is destroyed");
            Assert.That(goneJoint == null, Is.True, "and its D6 with it");
            Assert.That(collection.Retired, Is.EqualTo(1), "nothing more was retired");
            Assert.That(collection.ActorsDestroyed, Is.EqualTo(1), "the destruction was seen, once");
            Assert.That(collection.ActorsLeaving, Is.Zero);
            Assert.That(collection.ShapesFreed + collection.ShapesLeaving, Is.EqualTo(1), "its shape is followed once: gone back, or kept by what still reads its bank");
            TestContext.Out.WriteLine("the retired side's shape: freed " + collection.ShapesFreed + ", still kept " + collection.ShapesLeaving);
            Assert.That(root.Owners.SystemConstraintCount, Is.EqualTo(constraints - 1), "counted out once and no more");

            // What it leaves.
            Assert.That(root.Ledger.IsCurrentTarget(sides[0]), Is.True, "the sibling is live");
            Assert.That(root.Owners.TryGet(sides[0], out PhysicsFragmentOwner still) && ReferenceEquals(still, kept) && !kept.IsWithdrawn, Is.True, "with the same owner");
            Assert.That(keptJoint != null && ReferenceEquals(kept.BuildingWorldConstraint, keptJoint), Is.True, "and its own D6");
            Assert.That(kept.Mass, Is.EqualTo(keptMass), "no mass was moved to it");
            Assert.That(keptMesh != null && kept.Shape.MeshOf(0) == keptMesh && !kept.Shape.IsFreed, Is.True, "its collider mesh is kept");
            Assert.That(IsHitTarget(root, sides[0]) && IsDrawn(root, sides[0]), Is.True, "hit and drawn as before");
            Assert.That(root.References.LiveGeometryCount, Is.EqualTo(references), "no geometry reference was given back: the empty side had none");
            LogicalCutOperation after = OperationOf(root, origin);
            Assert.That(after.state == before.state && after.positive == before.positive && after.negative == before.negative && after.source == before.source,
                Is.True, "the cut's record is as it was");
            Assert.That(root.TerminationRequested, Is.False);
            TestContext.Out.WriteLine(collection.Describe());
            collection.Settings = EmptyOwnersOff();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A choice overtaken by a cut or by another retirement is not applied.** One empty side is chosen and then cut
        /// the ordinary way: while that cut is under way, and after it has replaced the fragment, the old choice is
        /// refused and nothing is retired by it. Another is chosen and then retired by somebody else: refused again,
        /// and nothing is ended twice.
        /// </summary>
        [UnityTest]
        public IEnumerator AChoiceOvertakenByACutOrAnotherRetirement_IsNotApplied()
        {
            HoldingExecutor unityJob = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.UnityJob
                    ? unityJob = Held(new HoldingExecutor(new UnityJobWorkExecutor(8)))
                    : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutEmptyOwnerCollection collection = EmptyOwnersClosed(root, remaining);
            LogicalFragmentId first = AddBodyDrawnAbove(root, Vector3.zero);
            LogicalFragmentId second = AddBodyDrawnAbove(root, new Vector3(8f, 0f, 0f));
            yield return null;
            var firstSides = new LogicalFragmentId[2];
            var secondSides = new LogicalFragmentId[2];
            yield return CutLeavingAnEmptySide(root, first, firstSides);
            yield return CutLeavingAnEmptySide(root, second, secondSides);

            // Chosen, then cut the ordinary way: held at its bake, so the cut is under way.
            Assert.That(collection.TryChoose(firstSides[1], out EmptyOwnerCandidate overtaken), Is.True);
            unityJob.HoldEverything = true;
            ProvisionalCutAsk ask = Ask(firstSides[1], new float4(1f, 0f, 0f, 0f));
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            Assert.That(root.Ledger.TryGetActiveOperation(firstSides[1], out _), Is.True, "the layout: its cut is under way");
            Assert.That(collection.TryApply(in overtaken, out EmptyOwnerPass refusal), Is.False);
            Assert.That(refusal, Is.EqualTo(EmptyOwnerPass.CutUnderWay));
            Assert.That(root.Ledger.IsCurrentTarget(firstSides[1]) && root.Owners.TryGet(firstSides[1], out _), Is.True, "it stays, for its cut");

            // The cut goes on and replaces it: the old choice is of a fragment that is no longer live.
            unityJob.HoldEverything = false;
            unityJob.ReleaseEverything();
            CutOperationId recut = AdmittedFor(root, new[] { firstSides[1] })[0];
            yield return Until(() => root.Geometry.StageOf(recut) == CutGeometryStage.Committed, "the cut of the empty side committed");
            yield return Until(() => root.Driver.Transactions.Count == 0, "its record is over");
            LogicalCutOperation record = OperationOf(root, recut);
            Assert.That(root.Ledger.IsCurrentTarget(firstSides[1]), Is.False, "the layout: replaced by its two sides");
            Assert.That(collection.TryApply(in overtaken, out refusal), Is.False, "the choice is not applied to what the fragment became");
            Assert.That(refusal, Is.EqualTo(EmptyOwnerPass.NotEmpty).Or.EqualTo(EmptyOwnerPass.NotLive));
            Assert.That(root.Ledger.IsCurrentTarget(record.positive) && root.Ledger.IsCurrentTarget(record.negative), Is.True, "its two sides stand");
            Assert.That(root.Owners.TryGet(record.positive, out _) && root.Owners.TryGet(record.negative, out _), Is.True);

            // Chosen, then retired by somebody else.
            Assert.That(collection.TryChoose(secondSides[1], out EmptyOwnerCandidate stale), Is.True);
            int constraints = root.Owners.SystemConstraintCount;
            Assert.That(root.Ledger.Retire(secondSides[1]), Is.True);
            Assert.That(root.Owners.Retire(secondSides[1]), Is.True);
            root.Geometry.Forget(secondSides[1]);
            int owners = root.Owners.Count;
            Assert.That(collection.TryApply(in stale, out refusal), Is.False, "already retired: nothing is ended twice");
            Assert.That(root.Owners.Count, Is.EqualTo(owners));
            Assert.That(root.Owners.SystemConstraintCount, Is.EqualTo(constraints));
            Assert.That(collection.Retired, Is.Zero, "no choice of this case was applied");
            Assert.That(collection.Refused, Is.EqualTo(3));
            Assert.That(root.Ledger.IsCurrentTarget(secondSides[0]), Is.True, "the sibling stays");
            yield return null;
            Assert.That(root.TerminationRequested, Is.False);
            collection.Settings = EmptyOwnersOff();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **What was collected does not come back, and the rest is cut again as usual.** After the empty side is
        /// collected, a cut asked of it is not accepted, nothing of it returns over the frames that follow, and further
        /// turns end nothing; its sibling is cut again -- accepted, published, committed, both sides drawn.
        /// </summary>
        [UnityTest]
        public IEnumerator ACollectedOwner_DoesNotComeBack_AndItsSiblingIsCutAgainAsUsual()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutEmptyOwnerCollection collection = EmptyOwnersClosed(root, remaining);
            LogicalFragmentId body = AddBodyDrawnAbove(root, Vector3.zero);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutLeavingAnEmptySide(root, body, sides);
            yield return CollectUntil(collection, remaining, 1, "the empty side was collected by the product's own frame");
            Assert.That(IsRetired(root, sides[1]), Is.True);
            int operations = root.Ledger.OperationCount;

            // A cut asked of it afterwards.
            ProvisionalCutAsk late = Ask(sides[1], new float4(1f, 0f, 0f, 0f));
            root.TryAsk(in late);
            remaining[0] = 1.0;
            for (int i = 0; i < 4; i++)
            {
                yield return null;
            }

            remaining[0] = 0.0;
            Assert.That(root.Ledger.OperationCount, Is.EqualTo(operations), "no cut of a collected fragment was accepted");
            Assert.That(IsRetired(root, sides[1]), Is.True, "still retired");
            Assert.That(root.Owners.TryGet(sides[1], out _), Is.False, "no owner came back");
            Assert.That(IsHitTarget(root, sides[1]) || IsDrawn(root, sides[1]), Is.False, "nothing of it is hit or drawn");
            Assert.That(root.Geometry.HasNoGeometry(sides[1]) || root.Geometry.TryGetGeometry(sides[1], out _), Is.False, "the DAG keeps nothing for it");
            Assert.That(collection.Retired, Is.EqualTo(1), "further turns ended nothing");

            // The sibling, cut again the ordinary way: across its geometry this time.
            var grandchildren = new LogicalFragmentId[2];
            yield return CutInTwo(root, sides[0], new float4(1f, 0f, 0f, 0f), grandchildren);
            yield return null;
            Assert.That(IsDrawn(root, grandchildren[0]) && IsDrawn(root, grandchildren[1]), Is.True, "both new sides drawn");
            Assert.That(root.TerminationRequested, Is.False);
            collection.Settings = EmptyOwnersOff();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Heavy frames and a short budget start nothing, and a later frame does.** With an empty side waiting: a turn
        /// in a frame where heavy work was noted, one where a garbage collection completed, one where a body was added,
        /// one while a cut is in flight and one with too little of the budget left each look at nothing and retire
        /// nothing. When none of that holds any more, the side is collected.
        /// </summary>
        [UnityTest]
        public IEnumerator HeavyFramesAndAShortBudget_StartNothing_AndALaterFrameDoes()
        {
            HoldingExecutor unityJob = null;
            CutWorldRoot root = NewWorld(
                out Shader _,
                destination => destination == WorkDestination.UnityJob
                    ? unityJob = Held(new HoldingExecutor(new UnityJobWorkExecutor(8)))
                    : null,
                null);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutEmptyOwnerCollection collection = EmptyOwnersClosed(root, remaining);
            LogicalFragmentId body = AddBodyDrawnAbove(root, Vector3.zero);
            LogicalFragmentId other = AddBody(root, new Vector3(8f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutLeavingAnEmptySide(root, body, sides);
            yield return null;
            yield return null;
            Assert.That(collection.HeldFor(EmptyOwnerHold.BudgetShort), Is.GreaterThan(0), "the layout: the product's own turns were held for the budget");
            Assert.That(collection.Examined, Is.Zero, "and looked at nothing");

            // From here the case takes the turns itself, in the update phase: the budget is open for exactly those.
            int HeldTurns(EmptyOwnerHold hold) => collection.HeldFor(hold);
            EmptyOwnerHold Turn()
            {
                remaining[0] = 1.0;
                collection.Step();
                remaining[0] = 0.0;
                return collection.LastHold;
            }

            // Heavy work noted in this frame.
            CutWorldFrameNotes.Note(CutWorldFrameNotes.Work.NpcRefill);
            int before = HeldTurns(EmptyOwnerHold.HeavyWork);
            Assert.That(Turn(), Is.EqualTo(EmptyOwnerHold.HeavyWork));
            Assert.That(HeldTurns(EmptyOwnerHold.HeavyWork), Is.EqualTo(before + 1));
            yield return null;

            // A garbage collection completed in this frame.
            GC.Collect();
            Assert.That(CutPhysicsStep.GcCollectionsThisFrame, Is.GreaterThan(0), "the layout: the collector's count moved in this frame");
            Assert.That(Turn(), Is.EqualTo(EmptyOwnerHold.GarbageCollection));
            yield return null;

            // The structure changed in this frame: a body was added.
            bool structure = false;
            for (int attempt = 0; attempt < 8 && !structure; attempt++)
            {
                AddBody(root, new Vector3(-8f - (4f * attempt), 0f, 0f));
                EmptyOwnerHold hold = Turn();
                Assert.That(hold, Is.EqualTo(EmptyOwnerHold.StructureChanged).Or.EqualTo(EmptyOwnerHold.GarbageCollection),
                    "a frame that added a body starts nothing");
                structure = hold == EmptyOwnerHold.StructureChanged;
                yield return null;
            }

            Assert.That(structure, Is.True, "a turn was held for the structure itself");

            // A cut in flight: held at its bake.
            unityJob.HoldEverything = true;
            ProvisionalCutAsk ask = Ask(other, k_planeUp);
            Assert.That(root.TryAsk(in ask), Is.True);
            yield return null;
            yield return null;
            Assert.That(root.Driver.Transactions.Count, Is.GreaterThan(0), "the layout: a cut is in flight");
            bool inFlight = false;
            for (int attempt = 0; attempt < 8 && !inFlight; attempt++)
            {
                EmptyOwnerHold hold = Turn();
                Assert.That(hold, Is.Not.EqualTo(EmptyOwnerHold.None), "nothing starts while a cut is in flight");
                inFlight = hold == EmptyOwnerHold.CutInFlight;
                yield return null;
            }

            Assert.That(inFlight, Is.True, "a turn was held for the cut in flight");
            unityJob.HoldEverything = false;
            unityJob.ReleaseEverything();
            yield return Until(() => root.Driver.Transactions.Count == 0, "the cut's record is over");
            yield return null;
            yield return null;

            // Too little of the budget left: twice the expected cost and the reserve are not there.
            int shortBefore = HeldTurns(EmptyOwnerHold.BudgetShort);
            remaining[0] = CutWorldRoot.CompactionReserveSeconds + (2.0 * collection.ExpectedSeconds) - 1.0e-5;
            collection.Step();
            Assert.That(collection.LastHold, Is.EqualTo(EmptyOwnerHold.BudgetShort).Or.EqualTo(EmptyOwnerHold.GarbageCollection));
            Assert.That(HeldTurns(EmptyOwnerHold.BudgetShort) + HeldTurns(EmptyOwnerHold.GarbageCollection), Is.GreaterThan(shortBefore));
            remaining[0] = 0.0;

            // Through all of that: nothing looked at, nothing retired, and the side still stands.
            Assert.That(collection.Examined, Is.Zero, "no held turn looked at anything");
            Assert.That(collection.Retired, Is.Zero);
            Assert.That(root.Ledger.IsCurrentTarget(sides[1]) && root.Owners.TryGet(sides[1], out _), Is.True, "what was passed over lives on");

            // And when the frames allow: collected.
            yield return CollectUntil(collection, remaining, 1, "the empty side was collected once the frames allowed");
            Assert.That(IsRetired(root, sides[1]), Is.True);
            Assert.That(collection.LastRetiringTurnSeconds, Is.GreaterThan(0.0), "the turn that retired was measured");
            Assert.That(collection.ExpectedSeconds, Is.EqualTo(collection.LastRetiringTurnSeconds), "and is what the next is expected to cost");
            TestContext.Out.WriteLine(collection.Describe());
            collection.Settings = EmptyOwnersOff();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A frame the display compacts in starts nothing.** With an empty side waiting and holes among the display's
        /// instance records, the display's compaction is let run on the product's path; the collection's turn of that
        /// same frame -- taken after the frame's collections -- is held for it, and the side is collected in a later one.
        /// </summary>
        [UnityTest]
        public IEnumerator AFrameTheDisplayCompactsIn_StartsNothing()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutEmptyOwnerCollection collection = EmptyOwnersClosed(root, remaining);
            root.Display.CompactionMinimumInterval = int.MaxValue;
            LogicalFragmentId body = AddBodyDrawnAbove(root, Vector3.zero);
            LogicalFragmentId other = AddBody(root, new Vector3(8f, 0f, 0f));
            root.Lifetime.MarkLineage(other);
            yield return null;
            var sides = new LogicalFragmentId[2];
            var otherSides = new LogicalFragmentId[2];
            yield return CutLeavingAnEmptySide(root, body, sides);
            yield return CutInTwo(root, other, k_planeUp, otherSides);
            yield return Until(() => root.Driver.Transactions.Count == 0, "every cut's record is over");
            Assert.That(root.Lifetime.TryRetire(otherSides[0], out string lifetimeRefusal), Is.True, lifetimeRefusal);
            yield return null;
            yield return null;
            yield return null;
            Assert.That(root.Display.DrawInstanceEnd, Is.GreaterThan(root.Display.DrawInstanceCount), "the layout: holes among the instance records");
            Assert.That(root.Display.Compactions, Is.Zero, "the layout: no compaction yet");
            Assert.That(collection.Examined, Is.Zero, "the layout: the collection has looked at nothing");

            root.Display.CompactionMinimumInterval = 1;
            yield return Until(() => root.Display.Compactions > 0 || root.Display.CompactionsSkipped > 120, "the display compacts (or keeps being refused)");
            root.Display.CompactionMinimumInterval = int.MaxValue;
            Assert.That(root.Display.Compactions, Is.EqualTo(1), "one compaction, on the product's path: " + root.Display.LastCompactionSkipReason);

            // This is the update after the frame that compacted: the collection's last turn was that frame's.
            Assert.That(root.Display.LastCompactionFrame, Is.EqualTo(Time.frameCount - 1), "the layout: the compaction was in the frame before this update");
            TestContext.Out.WriteLine("the turn of the frame that compacted was held as " + collection.LastHold + "; " + root.Display.DescribeCompaction());
            Assert.That(collection.LastHold, Is.EqualTo(EmptyOwnerHold.CompactionRan).Or.EqualTo(EmptyOwnerHold.GarbageCollection),
                "that frame's turn was held (a garbage collection in the same frame is asked first)");
            Assert.That(collection.Examined + collection.Retired, Is.Zero, "and looked at nothing");
            Assert.That(root.Ledger.IsCurrentTarget(sides[1]), Is.True, "the empty side lives on");

            yield return CollectUntil(collection, remaining, 1, "the empty side was collected in a later frame");
            Assert.That(IsRetired(root, sides[1]), Is.True);
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            collection.Settings = EmptyOwnersOff();
            yield return EndWorld(root);
        }

        /// <summary>
        /// **Off: nothing is looked at, and cutting goes on as before.** With the profile's own setting -- off -- an
        /// empty side stands through the frames, no turn is taken and nothing is counted; it is cut again the ordinary
        /// way, its sibling too, and the piece lifetime still retires a piece of its own. Switched on, the empty sides
        /// are collected by the product's own frames.
        /// </summary>
        [UnityTest]
        public IEnumerator Off_NothingIsLookedAt_AndCuttingGoesOnAsBefore()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            CutEmptyOwnerCollection collection = root.EmptyOwners;
            Assert.That(collection.Settings.enabled, Is.True, "the profile's own setting is on by default");
            collection.Settings = EmptyOwnersOff();
            collection.RemainingMainSeconds = () => 1.0;
            LogicalFragmentId body = AddBodyDrawnAbove(root, Vector3.zero);
            root.Lifetime.MarkLineage(body);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutLeavingAnEmptySide(root, body, sides);
            for (int i = 0; i < 8; i++)
            {
                yield return null;
            }

            collection.Step();
            Assert.That(collection.Turns + collection.Examined + collection.Candidates + collection.Retired, Is.Zero, "off: no turn, nothing looked at");
            Assert.That(root.Ledger.IsCurrentTarget(sides[1]) && root.Owners.TryGet(sides[1], out _), Is.True, "the empty side stands");

            // The empty side is cut again the ordinary way: two empty sides, both owned.
            var below = new LogicalFragmentId[2];
            yield return CutInTwo(root, sides[1], new float4(1f, 0f, 0f, 0f), below);
            Assert.That(root.Geometry.HasNoGeometry(below[0]) && root.Geometry.HasNoGeometry(below[1]), Is.True, "both of its sides are empty");

            // Its sibling too, across its geometry; and the piece lifetime retires a piece as it did.
            var above = new LogicalFragmentId[2];
            yield return CutInTwo(root, sides[0], new float4(1f, 0f, 0f, 0f), above);
            yield return null;
            Assert.That(IsDrawn(root, above[0]) && IsDrawn(root, above[1]), Is.True);
            Assert.That(root.Lifetime.TryRetire(above[0], out string lifetimeRefusal), Is.True, "the piece lifetime's own retirement: " + lifetimeRefusal);
            yield return Until(() => root.Driver.Transactions.Count == 0, "every cut's record is over");
            Assert.That(collection.Turns + collection.Examined, Is.Zero, "still nothing was looked at");

            // On: the product's own frames collect the two empty sides, and only them.
            collection.Settings = EmptyOwnersOn();
            yield return Until(() => collection.Retired >= 2, "the two empty sides were collected by the product's own frames");
            yield return null;
            Assert.That(IsRetired(root, below[0]) && IsRetired(root, below[1]), Is.True);
            Assert.That(root.Ledger.IsCurrentTarget(above[1]), Is.True, "the piece with geometry stays");
            Assert.That(collection.Retired, Is.EqualTo(2));
            Assert.That(root.TerminationRequested, Is.False);
            TestContext.Out.WriteLine(collection.Describe());
            collection.Settings = EmptyOwnersOff();
            yield return EndWorld(root);
        }
    }
}
