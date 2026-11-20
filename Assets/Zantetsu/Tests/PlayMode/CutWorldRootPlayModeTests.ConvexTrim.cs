using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;
using Zantetsu.Sandbox;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The convex trim (<see cref="CutConvexTrim"/>, 2026-11-20, off by default) through the product's own world: a piece
    /// held off the floor by a convex that reaches below what is drawn of it comes down once the convex is trimmed, the
    /// anchored side stays where it is, and a cut of the trimmed piece reads the trimmed shape; a result overtaken by a cut
    /// is thrown away and a mesh PhysX refuses leaves the owner as it was; the additional split comes first; "too small" is
    /// a mark of its own; and the walk city's tree_012, cut level, is trimmed as the numbers alone have it.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private const float TrimFloorTop = -1f;

        [TearDown]
        public void PutTheTrimHooksBack()
        {
            CutConvexTrim.TestRefuseIssue = false;
            CutConvexTrim.TestRefuseBake = false;
            CutConvexTrim.TestFailMeshAt = -1;
            CutConvexTrim.TestRefuseHull = false;
        }

        /// <summary>The trim of this world, on, with a frame budget that is always open (a case that closes it gives its own).</summary>
        private static CutConvexTrim TrimOn(CutWorldRoot root)
        {
            Assert.That(root.EnableConvexTrim("a test"), Is.True);
            CutConvexTrim trim = root.ConvexTrim;
            trim.RemainingMainSeconds = () => 1.0;
            trim.LaterStepSeconds = () => 0.0;
            return trim;
        }

        /// <summary>The additional split with a frame budget that is always open, and the collections off (not what these cases are about).</summary>
        private static CutSpatialSplit SplitOpen(CutWorldRoot root)
        {
            CutSpatialSplit split = root.SpatialSplit;
            split.RemainingMainSeconds = () => 1.0;
            split.LaterStepSeconds = () => 0.0;
            split.Settings = SplitOn();
            root.EmptyOwners.Settings = EmptyOwnersOff();
            root.Groups.Settings = GroupsOff();
            return split;
        }

        private void AddTrimFloor()
        {
            GameObject floor = Track(new GameObject("Floor"));
            var box = floor.AddComponent<BoxCollider>();
            box.center = new Vector3(0f, TrimFloorTop - 0.5f, 0f);
            box.size = new Vector3(80f, 1f, 80f);
        }

        /// <summary>The positions the fragment's committed geometry names, each once, in the geometry's own frame.</summary>
        internal static List<float3> MeshPointsOf(CutWorldRoot root, LogicalFragmentId fragment)
        {
            Assert.That(root.Geometry.TryGetGeometry(fragment, out VpStoredGeometry geometry), Is.True, "the fragment's geometry is committed");
            Assert.That(root.Storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view), Is.True);
            NativeArray<VpRenderVertex>.ReadOnly vertices = root.Storage.Vertices;
            var seen = new HashSet<uint>();
            var points = new List<float3>();
            for (int i = 0; i < view.Length; i++)
            {
                if (seen.Add(view[i])) points.Add(vertices[(int)view[i]].position);
            }

            Assert.That(root.Storage.TryReleaseIndexReadLease(lease), Is.True);
            return points;
        }

        internal static float3[] ConvexPointsOf(PhysicsOwnerShape shape, int convex)
        {
            ConvexBrepRange range = shape.Convex(convex);
            ConvexBrepBank bank = shape.BankOf(convex);
            var points = new float3[range.vertexCount];
            for (int v = 0; v < points.Length; v++) points[v] = bank.vertices[range.vertexBase + v];
            return points;
        }

        /// <summary>The lowest world height of what is drawn of the fragment, and of what its convexes are, now.</summary>
        internal static float LowestMeshY(CutWorldRoot root, LogicalFragmentId fragment, PhysicsFragmentOwner owner)
        {
            Assert.That(owner.TryReadGeometryLocalToWorld(out Matrix4x4 toWorld), Is.True);
            float lowest = float.MaxValue;
            foreach (float3 p in MeshPointsOf(root, fragment)) lowest = Mathf.Min(lowest, toWorld.MultiplyPoint3x4(p).y);
            return lowest;
        }

        internal static float LowestConvexY(PhysicsFragmentOwner owner)
        {
            Matrix4x4 toWorld = owner.Root.transform.localToWorldMatrix * (Matrix4x4)owner.Shape.LocalToOwner;
            float lowest = float.MaxValue;
            for (int c = 0; c < owner.Shape.ConvexCount; c++)
            {
                foreach (float3 p in ConvexPointsOf(owner.Shape, c)) lowest = Mathf.Min(lowest, toWorld.MultiplyPoint3x4(p).y);
            }

            return lowest;
        }

        /// <summary>How far the convexes reach beyond what is drawn along one direction of the owner's frame, metres (negative: not at all).</summary>
        internal static float ReachBeyondMesh(CutWorldRoot root, LogicalFragmentId fragment, PhysicsFragmentOwner owner, float3 directionInOwner)
        {
            float4x4 geometryToOwner = (float4x4)owner.GeometryLocalToOwner.Value, shapeToOwner = owner.Shape.LocalToOwner;
            float mesh = float.NegativeInfinity, convexes = float.NegativeInfinity;
            foreach (float3 p in MeshPointsOf(root, fragment)) mesh = math.max(mesh, math.dot(directionInOwner, math.transform(geometryToOwner, p)));
            for (int c = 0; c < owner.Shape.ConvexCount; c++)
            {
                foreach (float3 p in ConvexPointsOf(owner.Shape, c)) convexes = math.max(convexes, math.dot(directionInOwner, math.transform(shapeToOwner, p)));
            }

            return convexes - mesh;
        }

        /// <summary>Every collider of the owner carries the mesh its shape names for a convex, one each.</summary>
        private static void AssertCollidersAreTheShapes(PhysicsFragmentOwner owner, string what)
        {
            var colliders = owner.Root.GetComponentsInChildren<MeshCollider>(true).Where(c => c.enabled).ToList();
            Assert.That(colliders.Count, Is.EqualTo(owner.Shape.ConvexCount), what + ": a collider a convex");
            for (int c = 0; c < owner.Shape.ConvexCount; c++)
            {
                Mesh mesh = owner.Shape.MeshOf(c);
                Assert.That(colliders.Count(k => k.sharedMesh == mesh), Is.EqualTo(1), what + ": convex " + c + "'s mesh is on exactly one collider");
            }
        }

        // A bar 2 x 0.4 x 0.4 drawn, and one convex that is the bar and 0.5 more below it: nothing is drawn where the
        // convex stands on the floor.
        private static readonly float3 k_barHalf = new float3(1f, 0.2f, 0.2f);
        private const float BarExcessBelow = 0.5f;

        private LogicalFragmentId AddBarOnAFatConvex(CutWorldRoot root, Vector3 at, IReadOnlyList<float3> anchors = null, float3? half = null, float excessBelow = BarExcessBelow)
        {
            float3 h = half ?? k_barHalf;
            float3[] drawn = BoxCornersAt(float3.zero, h);
            float3[] convex = BoxCornersAt(new float3(0f, -0.5f * excessBelow, 0f), new float3(h.x, h.y + (0.5f * excessBelow), h.z));
            PhysicsOwnerShape shape = NewBoxesShape(new List<float3[]> { convex });
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendBoxesGeometry(root.Storage, new List<float3[]> { drawn });
            var actor = TrackActor(new GameObject("Bar on a fat convex"));
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
            bool added = root.TryAddBody(actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, anchors, false, out LogicalFragmentId fragment);
            if (added) _registered.Add(actor);
            Assert.That(added, Is.True, "the body was taken into the world");
            return fragment;
        }

        /// <summary>Cuts the bar across at x = <paramref name="x"/> and waits until the cut is over: [0] the side of larger x, [1] the other.</summary>
        private static IEnumerator CutTheBarAcross(CutWorldRoot root, LogicalFragmentId bar, float x, LogicalFragmentId[] sides)
        {
            yield return CutInTwo(root, bar, new float4(1f, 0f, 0f, -x), sides);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the cut's record is over");
            yield return null;
            Assert.That(root.Geometry.TryGetGeometry(sides[0], out _) && root.Geometry.TryGetGeometry(sides[1], out _), Is.True, "both sides' geometry is committed");
        }

        private static ConvexTrimRecord RecordOf(CutConvexTrim trim, LogicalFragmentId fragment, ConvexTrimEnd end)
        {
            return trim.Records.LastOrDefault(r => r.fragment == fragment && r.end == end);
        }

        /// <summary>
        /// **A piece at rest on a convex that reaches below what is drawn of it comes down when the convex is trimmed.** The
        /// free side of a cut bar stands on the floor on 0.5 m of convex nothing is drawn in, and sleeps there; the trim, once
        /// on, gives the same fragment and the same body the trimmed convex -- its collider the new mesh, its owner the new
        /// shape, the old shape given up -- and wakes it: it falls and comes to rest on what is drawn. The side that holds the
        /// anchor is trimmed as well and stays exactly where it was, fixed. A cut of the trimmed piece then reads the trimmed
        /// shape: neither of its two children reaches below what is drawn.
        /// </summary>
        [UnityTest]
        public IEnumerator ConvexTrim_APieceRestingOnAConvexBelowItsMesh_ComesDownToWhatIsDrawn_TheAnchoredSideStays_AndACutReadsTheTrimmedShape()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            yield return null;
            AddTrimFloor();
            SplitOpen(root);
            // The convex's underside 0.05 above the floor; the anchor on the side of larger x.
            var at = new Vector3(0f, TrimFloorTop + 0.05f + k_barHalf.y + BarExcessBelow, 0f);
            LogicalFragmentId bar = AddBarOnAFatConvex(root, at, new[] { new float3(0.8f, 0f, 0f) });
            var sides = new LogicalFragmentId[2];
            yield return CutTheBarAcross(root, bar, 0.6f, sides);
            Assert.That(root.Owners.TryGet(sides[0], out PhysicsFragmentOwner held), Is.True);
            Assert.That(root.Owners.TryGet(sides[1], out PhysicsFragmentOwner free), Is.True);
            Assert.That(held.FixedByAnchors && held.Body.isKinematic, Is.True, "the side with the anchor is fixed");
            Assert.That(free.FixedByAnchors || free.Body.isKinematic, Is.False, "the other side is free");
            free.Body.useGravity = true;
            free.Body.WakeUp();

            // Trim off: it comes to rest on the convex's underside, with half a metre of air under what is drawn.
            yield return Until(() => free.Body.IsSleeping() && LowestConvexY(free) < TrimFloorTop + 0.03f, "the free side rests on the floor and sleeps");
            float gapBefore = LowestMeshY(root, sides[1], free) - TrimFloorTop;
            float heightBefore = free.Root.transform.position.y;
            Vector3 heldAt = held.Root.transform.position;
            Quaternion heldTurn = held.Root.transform.rotation;
            TestContext.Out.WriteLine("trim off: the drawn piece's lowest point is " + gapBefore.ToString("F3") + " m above the floor; reach below the mesh "
                + ReachBeyondMesh(root, sides[1], free, new float3(0f, -1f, 0f)).ToString("F3") + " m");
            Assert.That(gapBefore, Is.EqualTo(BarExcessBelow).Within(0.03f), "what is drawn floats by the convex's excess");
            Assert.That(root.ConvexTrim.Issued, Is.Zero, "off: nothing was issued");
            Assert.That(root.ConvexTrim.Turns, Is.Zero, "off: no turn was taken");

            // Trim on.
            PhysicsOwnerShape freeShapeBefore = free.Shape, heldShapeBefore = held.Shape;
            Rigidbody freeBody = free.Body;
            GameObject freeRoot = free.Root;
            float reachBefore = free.HitReach;
            CutConvexTrim trim = TrimOn(root);
            yield return Until(() => trim.Published >= 2 && trim.IsIdle, "both sides were trimmed");
            Assert.That(root.Owners.TryGet(sides[1], out PhysicsFragmentOwner freeNow) && ReferenceEquals(freeNow, free), Is.True, "the same fragment, the same owner");
            Assert.That(ReferenceEquals(free.Body, freeBody) && ReferenceEquals(free.Root, freeRoot), Is.True, "the same body and Root");
            Assert.That(ReferenceEquals(free.Shape, freeShapeBefore), Is.False, "another shape");
            Assert.That(freeShapeBefore.IsFreed && heldShapeBefore.IsFreed, Is.True, "the shapes before were given up (no work reads them any more)");
            AssertCollidersAreTheShapes(free, "the free side");
            AssertCollidersAreTheShapes(held, "the fixed side");
            Assert.That(ReachBeyondMesh(root, sides[1], free, new float3(0f, -1f, 0f)), Is.LessThan(1e-4f), "the convex ends where what is drawn ends");
            Assert.That(ReachBeyondMesh(root, sides[0], held, new float3(0f, -1f, 0f)), Is.LessThan(1e-4f));
            Assert.That(free.HitReach, Is.LessThan(reachBefore), "the reach a hit reads is the trimmed shape's");
            var shapes = new List<CurrentShape>();
            root.Owners.CollectCurrentShapes(shapes);
            Assert.That(shapes.Any(s => s.Fragment == sides[1] && ReferenceEquals(s.Shape, free.Shape)), Is.True, "a hit reads the new shape");
            Assert.That(trim.BodiesWoken, Is.EqualTo(1), "the free body was woken; the fixed one has nothing to wake");
            ConvexTrimRecord record = RecordOf(trim, sides[1], ConvexTrimEnd.Published);
            Assert.That(record, Is.Not.Null);
            Assert.That(record.verticesBeforeInAll, Is.EqualTo(8));
            Assert.That(record.verticesAfterInAll, Is.EqualTo(8));
            Assert.That(record.overhangBefore, Is.EqualTo(BarExcessBelow).Within(1e-3f));
            Assert.That(record.overhangAfter, Is.LessThan(1e-3f));
            Assert.That(root.SpatialSplit.ShapesReplaced, Is.EqualTo(2), "the additional split was told of both replacements");

            // It falls, and rests on what is drawn.
            yield return Until(() => free.Body.IsSleeping() && LowestMeshY(root, sides[1], free) - TrimFloorTop < 0.03f, "the free side fell and sleeps on what is drawn");
            float gapAfter = LowestMeshY(root, sides[1], free) - TrimFloorTop;
            float fell = heightBefore - free.Root.transform.position.y;
            TestContext.Out.WriteLine("trim on: the drawn piece's lowest point is " + gapAfter.ToString("F3") + " m above the floor; it fell " + fell.ToString("F3") + " m. " + trim.Describe());
            Assert.That(fell, Is.EqualTo(BarExcessBelow).Within(0.03f), "it came down by what was cut off");
            Assert.That(held.Root.transform.position, Is.EqualTo(heldAt), "the fixed side stands where it stood");
            Assert.That(held.Root.transform.rotation, Is.EqualTo(heldTurn));
            Assert.That(held.FixedByAnchors && held.Body.isKinematic, Is.True, "and is fixed still");

            // A cut of the trimmed piece: the next cut's input is the trimmed shape.
            var again = new LogicalFragmentId[2];
            yield return CutTheBarAcross(root, sides[1], -0.2f, again);
            foreach (LogicalFragmentId child in again)
            {
                Assert.That(root.Owners.TryGet(child, out PhysicsFragmentOwner owner) && owner.Body != null, Is.True);
                Assert.That(ReachBeyondMesh(root, child, owner, new float3(0f, -1f, 0f)), Is.LessThan(1e-4f), "a child of the trimmed piece does not reach below what is drawn");
                AssertCollidersAreTheShapes(owner, "a child of the trimmed piece");
            }

            yield return Until(() => trim.IsIdle, "the children were looked at for themselves");
            Assert.That(trim.EndedAs(ConvexTrimEnd.BelowThreshold), Is.EqualTo(2), "each child was given its own attempt, and nothing of it reaches beyond its mesh");
            Assert.That(root.GeometryFaults, Is.Zero);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A result overtaken by a cut is thrown away, and the children are looked at for themselves.** The numbers of an
        /// attempt are back and its Main stage is held for the budget; the fragment is cut meanwhile. When the frame opens the
        /// attempt ends stale -- no collider and no shape of anything is changed by it -- and each child, another mesh and
        /// another shape, gets an attempt of its own.
        /// </summary>
        [UnityTest]
        public IEnumerator ConvexTrim_AResultOvertakenByACut_IsThrownAway_AndTheChildrenAreTrimmedForThemselves()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            yield return null;
            SplitOpen(root);
            LogicalFragmentId bar = AddBarOnAFatConvex(root, Vector3.zero);
            var sides = new LogicalFragmentId[2];
            yield return CutTheBarAcross(root, bar, 0.6f, sides);
            CutConvexTrim trim = TrimOn(root);
            double[] remaining = { 1.0 };
            trim.RemainingMainSeconds = () => remaining[0];
            yield return Until(() => trim.Issued == 1, "an attempt is out");
            remaining[0] = 0.0;   // closed: the numbers are taken back, and the Main stage after them waits
            yield return Until(() => trim.HeldFor(ConvexTrimHold.BudgetShort) > 0, "the attempt waits for the budget with its numbers back");
            Assert.That(trim.HasAttempt, Is.True);
            Assert.That(trim.Published, Is.Zero);

            // Which of the two it is out for is the trim's own order: both are cut, so whichever it is, it is overtaken.
            var childrenA = new LogicalFragmentId[2];
            var childrenB = new LogicalFragmentId[2];
            yield return CutTheBarAcross(root, sides[0], 0.8f, childrenA);
            yield return CutTheBarAcross(root, sides[1], -0.2f, childrenB);
            Assert.That(trim.Published, Is.Zero, "nothing was published while the budget was closed");
            remaining[0] = 1.0;
            yield return Until(() => trim.IsIdle && trim.Published == 4, "the four children were trimmed");
            Assert.That(trim.EndedAs(ConvexTrimEnd.Stale), Is.EqualTo(1), "the attempt the cut overtook was thrown away. " + trim.Describe());
            foreach (LogicalFragmentId child in childrenA.Concat(childrenB))
            {
                Assert.That(root.Owners.TryGet(child, out PhysicsFragmentOwner owner), Is.True);
                Assert.That(ReachBeyondMesh(root, child, owner, new float3(0f, -1f, 0f)), Is.LessThan(1e-4f));
                AssertCollidersAreTheShapes(owner, "a child");
                Assert.That(RecordOf(trim, child, ConvexTrimEnd.Published), Is.Not.Null, "each child had its own attempt");
            }

            yield return EndWorld(root);
        }

        /// <summary>
        /// **A mesh PhysX makes no convex of leaves the owner as it was, and the input is not tried again; a queue with no
        /// place marks nothing.** The first issue finds no place: nothing is recorded and the fragment is looked at again.
        /// The attempt then runs, and its hull check is refused: the owner keeps its shape and its collider's mesh, the
        /// attempt ends as a failed cook, and no further attempt is issued for that mesh and that shape.
        /// </summary>
        [UnityTest]
        public IEnumerator ConvexTrim_ARefusedHull_LeavesTheOwnerAsItWas_AndAFullQueueMarksNothing()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            yield return null;
            SplitOpen(root);
            LogicalFragmentId bar = AddBarOnAFatConvex(root, Vector3.zero);
            var sides = new LogicalFragmentId[2];
            yield return CutTheBarAcross(root, bar, 0.6f, sides);
            Assert.That(root.Owners.TryGet(sides[0], out PhysicsFragmentOwner a), Is.True);
            Assert.That(root.Owners.TryGet(sides[1], out PhysicsFragmentOwner b), Is.True);
            PhysicsOwnerShape shapeA = a.Shape, shapeB = b.Shape;
            Mesh meshA = shapeA.MeshOf(0), meshB = shapeB.MeshOf(0);

            CutConvexTrim.TestRefuseIssue = true;
            CutConvexTrim trim = TrimOn(root);
            yield return Until(() => trim.QueueFull >= 2, "issues found no place");
            Assert.That(trim.Issued, Is.Zero);
            Assert.That(trim.HasAttempt, Is.False);
            Assert.That(trim.PendingCount, Is.GreaterThan(0), "the fragments wait to be looked at again: nothing was marked");

            CutConvexTrim.TestRefuseIssue = false;
            CutConvexTrim.TestRefuseHull = true;
            yield return Until(() => trim.IsIdle && trim.EndedAs(ConvexTrimEnd.CookFailed) == 2, "both attempts ended at the hull check");
            Assert.That(trim.Published, Is.Zero);
            Assert.That(ReferenceEquals(a.Shape, shapeA) && ReferenceEquals(b.Shape, shapeB), Is.True, "the owners keep their shapes");
            Assert.That(shapeA.IsFreed || shapeB.IsFreed, Is.False);
            Assert.That(shapeA.WorkUsers + shapeB.WorkUsers, Is.Zero, "the attempts' holds were given back");
            Assert.That(ReferenceEquals(shapeA.MeshOf(0), meshA) && ReferenceEquals(shapeB.MeshOf(0), meshB), Is.True);
            AssertCollidersAreTheShapes(a, "one side");
            AssertCollidersAreTheShapes(b, "the other");
            Assert.That(ReachBeyondMesh(root, sides[1], b, new float3(0f, -1f, 0f)), Is.EqualTo(BarExcessBelow).Within(1e-4f), "still reaching below what is drawn");

            // The hull would pass now, and the input is the same: it is not tried again.
            CutConvexTrim.TestRefuseHull = false;
            long issued = trim.Issued;
            for (int f = 0; f < 40; f++) yield return null;
            Assert.That(trim.Issued, Is.EqualTo(issued), "the same mesh and shape are not tried a second time");
            Assert.That(trim.Examine(sides[0]), Is.EqualTo(ConvexTrimPass.Done));
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The NPC planner's prop map takes the trimmed shape at its next gathering.** The map keeps its copies of the
        /// convexes by shape: before the trim its copy of the piece reaches 0.7 below the piece's centre; after the trim's
        /// publication its next builds are made from a copy of the new shape, which ends where what is drawn ends, and the
        /// copies of the shapes given up are dropped.
        /// </summary>
        [UnityTest]
        public IEnumerator ConvexTrim_TheNpcPlannersPropMap_TakesTheTrimmedShape_AtItsNextGathering()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            yield return null;
            SplitOpen(root);
            LogicalFragmentId bar = AddBarOnAFatConvex(root, Vector3.zero);
            root.Lifetime.MarkPropLineage(bar);
            var sides = new LogicalFragmentId[2];
            yield return CutTheBarAcross(root, bar, 0.6f, sides);
            // the planner's floor one metre below the bar's centre, the plane through the bar: both the fat convex and the trimmed one cross it
            var props = new PropObstacleSource(root, new Vector3(0f, -1f, 0f), null, new Zantetsu.Core.MobPlan.PropObstacleSettings { updateSeconds = 0f, planeHeightMeters = 1f, cells = 64 });
            var lowest = new Dictionary<int, float>();
            var ids = new Dictionary<int, int>();
            int builds = 0;
            props.Built += (input, map, copied) =>
            {
                builds++;
                for (int i = 0; i < input.Count; i++)
                {
                    lowest[input.Fragments[i]] = input.Shapes[i].Bounds[1];
                    ids[input.Fragments[i]] = input.Shapes[i].Id;
                }
            };
            try
            {
                float deadline = Time.realtimeSinceStartup + DeadlineSeconds;
                while (!(lowest.ContainsKey(sides[0].value) && lowest.ContainsKey(sides[1].value)) && Time.realtimeSinceStartup < deadline)
                {
                    props.Step(Time.timeAsDouble, 0f, 0f);
                    yield return null;
                }

                Assert.That(lowest.ContainsKey(sides[1].value), Is.True, "the map was built with both sides. " + props.Describe());
                Assert.That(lowest[sides[1].value], Is.EqualTo(-k_barHalf.y - BarExcessBelow).Within(1e-4f), "the map's copy before the trim reaches below what is drawn");
                int idBefore = ids[sides[1].value], knownBefore = props.ShapesKnown, copiedBefore = props.ShapesCopied;
                Assert.That(knownBefore, Is.EqualTo(2));

                CutConvexTrim trim = TrimOn(root);
                while (!(trim.Published == 2 && lowest[sides[0].value] > -k_barHalf.y - 1e-3f && lowest[sides[1].value] > -k_barHalf.y - 1e-3f && props.ShapesKnown == 2)
                       && Time.realtimeSinceStartup < deadline)
                {
                    props.Step(Time.timeAsDouble, 0f, 0f);
                    yield return null;
                }

                Assert.That(trim.Published, Is.EqualTo(2));
                Assert.That(lowest[sides[1].value], Is.EqualTo(-k_barHalf.y).Within(1e-4f), "the map's copy after the trim ends where what is drawn ends. " + props.Describe());
                Assert.That(ids[sides[1].value], Is.Not.EqualTo(idBefore), "a copy of the new shape");
                Assert.That(props.ShapesCopied, Is.EqualTo(copiedBefore + 2), "the two trimmed shapes were copied once each");
                Assert.That(props.ShapesKnown, Is.EqualTo(2), "the copies of the shapes given up were dropped");
                Assert.That(builds, Is.GreaterThan(2));
            }
            finally
            {
                props.Release();
            }

            yield return EndWorld(root);
        }

        /// <summary>
        /// **The bake is offered as an issue is, and a Main step waits for its budget however long.** A bake that found
        /// no place in the queue is not offered again while the additional split does not leave the frame to the trim;
        /// and once the bake is back, the hull check, the new shape and the switch wait for a frame whose budget holds
        /// them -- past the 180 turns after which a stage used to be let through -- and nothing is published meanwhile.
        /// </summary>
        [UnityTest]
        public IEnumerator ConvexTrim_TheBakeIsOfferedAsAnIssueIs_AndAMainStepWaitsForItsBudget_HoweverLong()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            yield return null;
            SplitOpen(root);
            LogicalFragmentId bar = AddBarOnAFatConvex(root, Vector3.zero);
            var sides = new LogicalFragmentId[2];
            yield return CutTheBarAcross(root, bar, 0.6f, sides);
            CutConvexTrim.TestRefuseBake = true;
            CutConvexTrim trim = TrimOn(root);
            bool room = true, shut = false;
            trim.SplitLeavesRoom = () => room;
            trim.RemainingMainSeconds = () => shut && trim.AttemptWaitsForMain ? 0.0 : 1.0;

            // the meshes are made; their bake finds no place
            yield return Until(() => trim.QueueFull >= 1 && trim.HasAttempt && !trim.AttemptWaitsForMain, "the first bake found no place in the queue");
            Assert.That(trim.Issued, Is.EqualTo(1));

            // the queue has room again, but the split does not leave the frame: the bake is not offered
            room = false;
            CutConvexTrim.TestRefuseBake = false;
            long full = trim.QueueFull, splitFirst = trim.HeldFor(ConvexTrimHold.SplitFirst);
            for (int i = 0; i < 30; i++) yield return null;
            Assert.That(trim.HasAttempt && !trim.AttemptWaitsForMain, Is.True, "the attempt waits with its meshes");
            Assert.That(trim.QueueFull, Is.EqualTo(full), "no offer was made while the split had the frame");
            Assert.That(trim.HeldFor(ConvexTrimHold.SplitFirst), Is.GreaterThanOrEqualTo(splitFirst + 29));
            Assert.That(trim.Published, Is.Zero);

            // the split leaves room: the bake goes out and comes back; then no frame's budget holds the next step
            shut = true;
            room = true;
            yield return Until(() => trim.AttemptWaitsForMain, "the bake came back");
            for (int i = 0; i < 260; i++) yield return null;
            Assert.That(trim.AttemptWaitsForMain, Is.True, "the attempt waits still");
            Assert.That(trim.Published, Is.Zero, "nothing was let through the budget. " + trim.Describe());
            Assert.That(trim.MostBudgetHolds, Is.GreaterThanOrEqualTo(250));
            Assert.That(root.Owners.TryGet(sides[1], out PhysicsFragmentOwner b), Is.True);
            Assert.That(ReachBeyondMesh(root, sides[1], b, new float3(0f, -1f, 0f)), Is.EqualTo(BarExcessBelow).Within(1e-4f), "the owner is as it was");

            // a frame with room: the steps are taken and both sides are trimmed
            shut = false;
            yield return Until(() => trim.Published == 2 && trim.IsIdle, "both sides were trimmed. " + trim.Describe());
            Assert.That(root.Owners.TryGet(sides[1], out b), Is.True);
            Assert.That(ReachBeyondMesh(root, sides[1], b, new float3(0f, -1f, 0f)), Is.LessThan(1e-3f));
            AssertCollidersAreTheShapes(b, "after the wait");
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The additional split comes first.** A lower body with a detached hand is the split's candidate, and the split's
        /// frame budget is closed: its turns issue nothing because they are held, which is not "nothing to do" -- the trim,
        /// whose own budget is open, issues nothing either. Once the split has published and found nothing more, the trim
        /// looks at what there is then: never at the fragment the split replaced.
        /// </summary>
        [UnityTest]
        public IEnumerator ConvexTrim_WaitsForTheAdditionalSplit_WhoseHeldTurnIsNotAnIdleOne()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            yield return null;
            double[] remaining = { 0.0 };
            CutSpatialSplit split = SplitClosed(root, remaining);
            LogicalFragmentId body = AddBodyWithAHand(root, new Vector3(0f, 3f, 0f));
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            CutConvexTrim trim = TrimOn(root);
            for (int f = 0; f < 40; f++) yield return null;
            Assert.That(split.Published, Is.Zero, "the split is held for its budget");
            Assert.That(trim.Issued, Is.Zero, "and the trim issues nothing before it. " + trim.Describe());
            Assert.That(trim.Examined, Is.Zero, "nor looks at anything");
            Assert.That(trim.HeldFor(ConvexTrimHold.SplitFirst), Is.GreaterThan(0));

            remaining[0] = 1.0;
            yield return Until(() => split.Published == 1 && trim.IsIdle && trim.Issued >= 3, "the split published, then the trim looked at the three pieces");
            Assert.That(trim.Records.Any(r => r.fragment == sides[1]), Is.False, "no attempt was ever issued for the fragment the split replaced");
            Assert.That(trim.Records.Select(r => r.fragment).Distinct().Count(), Is.EqualTo(3), "the side above and the split's two children");
            Assert.That(trim.EndedAs(ConvexTrimEnd.BelowThreshold), Is.EqualTo(3), "boxes drawn as they are: nothing reaches beyond its mesh. " + trim.Describe());
            yield return EndWorld(root);
        }

        /// <summary>
        /// **"Too small" is a mark of its own, taken from the parent where the child lies inside it.** A thin bar -- one
        /// extent above 0.2 m -- is cut: each side is looked at once and found too small, with no attempt. A side cut again
        /// gives two children that are too small without a look, and a look of their own would say the same.
        /// </summary>
        [UnityTest]
        public IEnumerator ConvexTrim_AThinPiece_IsTooSmall_AndItsChildrenAreWithoutALook()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            yield return null;
            SplitOpen(root);
            // 2 x 0.16 x 0.16 drawn; the convex 2 x 0.18 x 0.16: one extent above 0.2, whatever it is cut to along x.
            LogicalFragmentId bar = AddBarOnAFatConvex(root, Vector3.zero, null, new float3(1f, 0.08f, 0.08f), 0.02f);
            var sides = new LogicalFragmentId[2];
            yield return CutTheBarAcross(root, bar, 0.2f, sides);
            CutConvexTrim trim = TrimOn(root);
            yield return Until(() => trim.IsIdle && trim.PassedAs(ConvexTrimPass.SizeShort) == 2, "both sides were looked at and found too small");
            Assert.That(trim.Issued, Is.Zero);
            Assert.That(trim.PassedAs(ConvexTrimPass.NotCutMade), Is.EqualTo(0), "the bar itself was retired before the trim was on");
            Assert.That(trim.Examine(sides[0]), Is.EqualTo(ConvexTrimPass.SizeShort));

            var children = new LogicalFragmentId[2];
            yield return CutTheBarAcross(root, sides[1], -0.4f, children);
            for (int f = 0; f < 20; f++) yield return null;
            Assert.That(trim.SizeInherited, Is.EqualTo(2), "the two children took their parent's mark. " + trim.Describe());
            Assert.That(trim.SizeInheritedDisagreed, Is.Zero, "and a look of their own agrees");
            Assert.That(trim.PassedAs(ConvexTrimPass.SizeShort), Is.EqualTo(2), "no look was spent on them");
            Assert.That(trim.Examine(children[0]), Is.EqualTo(ConvexTrimPass.SizeShort));
            Assert.That(trim.Issued, Is.Zero);
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The split's diagnosis tells an ordinary candidate that waited beside an attempt made again, in one turn too.**
        /// The wait of a candidate is from the scan that found it to its issue; an attempt made again after a replaced
        /// shape counts as beside it where it was out at some moment between the two. Found by one scan and issued one
        /// after the other in the same turn, there is no turn between the two to count -- and the same where the attempt
        /// made again has ended by the ordinary candidate's issue. An ordinary candidate issued before the one made again
        /// is found had none beside it.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplitDiagnosis_AnOrdinaryCandidateBesideAnAttemptMadeAgain_IsToldSo_WithinOneTurnToo()
        {
            CutWorldRoot root = NewPlacedHullWorld();
            yield return null;
            CutSpatialSplit split = root.SpatialSplit;

            split.NoteCandidateWaitsForTests(new LogicalFragmentId(900001), new LogicalFragmentId(900002), 0);
            Assert.That(split.RetryCandidateWaits.issued, Is.EqualTo(1), "the candidate made again is told as one. " + split.DescribeCandidateWaits());
            Assert.That(split.OrdinaryWaitsBesideRetries.issued, Is.EqualTo(1), "issued after it in the same turn: beside it. " + split.DescribeCandidateWaits());
            Assert.That(split.OrdinaryWaitsWithout.issued, Is.Zero);

            split.NoteCandidateWaitsForTests(new LogicalFragmentId(900003), new LogicalFragmentId(900004), 1);
            Assert.That(split.RetryCandidateWaits.issued, Is.EqualTo(2));
            Assert.That(split.OrdinaryWaitsBesideRetries.issued, Is.EqualTo(2), "found while it was out, issued after it ended: beside it. " + split.DescribeCandidateWaits());
            Assert.That(split.OrdinaryWaitsWithout.issued, Is.Zero);

            split.NoteCandidateWaitsForTests(new LogicalFragmentId(900005), new LogicalFragmentId(900006), 2);
            Assert.That(split.RetryCandidateWaits.issued, Is.EqualTo(3));
            Assert.That(split.OrdinaryWaitsBesideRetries.issued, Is.EqualTo(2));
            Assert.That(split.OrdinaryWaitsWithout.issued, Is.EqualTo(1), "issued before the one made again was found: none beside it. " + split.DescribeCandidateWaits());
            Assert.That(split.ShapeRetriesIssued, Is.EqualTo(3));
            Assert.That(split.ShapeRetriesEndedAs(SpatialSplitEnd.NotFound), Is.EqualTo(3));

            split.PruneShapeRetries();
            Assert.That(split.ShapeRetryFragmentsKept, Is.Zero, "nothing is kept of the numbers no owner has");
            yield return EndWorld(root);
        }
    }
}
