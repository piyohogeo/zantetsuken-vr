using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.TestTools;
using Zantetsu.ConvexCut;
using Zantetsu.Core.Slash;
using Zantetsu.MeshCut;
using Zantetsu.Rendering;

namespace Zantetsu.PhysicsCut.PlayModeTests
{
    /// <summary>
    /// The fragments' reach in Find (TL, 2026-10-05; DESIGN 19.1.7, D-193): a fragment's shape is passed over, before its
    /// frame is read as a matrix, composed with its placement and its world box made, when the sweep is certainly
    /// farther from its owner's position than the reach its owner keeps for it. The cases: far shapes have their
    /// position read and nothing more (the frames read are counted where they are read); in a world being cut, every
    /// shape passed over takes everything after it all the same and none is a hit, and at every frame the reach kept for
    /// every current shape -- owners, both sides of every published pair, shapes replaced on the same Root -- holds every
    /// vertex of that shape as it stands; shapes off their owner's origin, turned, scaled and large, on Roots turned,
    /// scaled and under a scaled parent, are not missed; a pair's two sides and a shape on two owners each keep their
    /// own; what cannot be vouched for takes the tests it took before; and the cost of one update, both ways, in this
    /// Editor -- written out, not judged.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private struct ReachTally
        {
            public int shapes, withReach, sides, sidesWithReach, shapeChangedOnItsRoot;
            public double loosest;
        }

        // Every current shape of the world: the reach handed with it is no less than the distance from its owner's
        // position to every vertex of every convex of it as it stands now (the owner's world matrix with the shape's
        // placement on it) -- for an owner, and for each side of a published pair with that side's own Root.
        private void CheckKeptReaches(CutWorldRoot root, List<CurrentShape> list, Dictionary<Transform, PhysicsOwnerShape> shapeOf, ref ReachTally tally, string when)
        {
            root.Owners.CollectCurrentShapes(list);
            foreach (CurrentShape s in list)
            {
                tally.shapes++;
                if (s.Side != 0f) tally.sides++;
                if (shapeOf != null)
                {
                    if (shapeOf.TryGetValue(s.Owner, out PhysicsOwnerShape before) && !ReferenceEquals(before, s.Shape)) tally.shapeChangedOnItsRoot++;
                    shapeOf[s.Owner] = s.Shape;
                }

                if (s.Reach < 0f) continue;
                tally.withReach++;
                if (s.Side != 0f) tally.sidesWithReach++;
                double farthest = FarthestVertex(s.Owner, s.Shape);
                if (farthest > s.Reach * (1.0 + 1e-6))
                {
                    Assert.Fail(when + ": fragment " + s.Fragment.value + " side " + s.Side + ": a vertex stands " + farthest + " from its owner's position, the reach kept is " + s.Reach);
                }

                if (farthest > 1e-3) tally.loosest = math.max(tally.loosest, s.Reach / farthest);
            }
        }

        // How far from the Transform's position the farthest vertex of the shape stands now, in the world.
        private static double FarthestVertex(Transform ownerTransform, PhysicsOwnerShape shape)
        {
            Matrix4x4 owner = ownerTransform.localToWorldMatrix;
            float4x4 placement = shape.LocalToOwner;
            var origin = new double3((float3)ownerTransform.position);
            double farthest = 0.0;
            for (int k = 0; k < shape.ConvexCount; k++)
            {
                ConvexBrepBank bank = shape.BankOf(k);
                ConvexBrepRange range = shape.Convex(k);
                for (int v = 0; v < range.vertexCount; v++)
                {
                    float3 local = bank.vertices[range.vertexBase + v];
                    double3 inOwner = new double3(placement.c0.xyz) * local.x + new double3(placement.c1.xyz) * local.y + new double3(placement.c2.xyz) * local.z + new double3(placement.c3.xyz);
                    double3 world = new double3((float3)(Vector3)owner.GetColumn(0)) * inOwner.x + new double3((float3)(Vector3)owner.GetColumn(1)) * inOwner.y
                                    + new double3((float3)(Vector3)owner.GetColumn(2)) * inOwner.z + new double3((float3)(Vector3)owner.GetColumn(3));
                    farthest = math.max(farthest, math.length(world - origin));
                }
            }

            return farthest;
        }

        [UnityTest]
        public IEnumerator FragmentReach_FarShapes_HaveTheirPositionReadAndNoFrame()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            const int shapes = 64;
            AddBodyGrid(root, shapes, 8, 4f, true);
            yield return null;
            var old = new SlashHitDetector(root, in k_hitSettings) { fragmentReachReject = false };
            var now = new SlashHitDetector(root, in k_hitSettings);
            long slash = 0;
            void Both(string what, int expectHits, long expectFramesAtMost, long expectPositions, params Func<long, SlashSweep>[] make)
            {
                slash++;
                SlashSweep[] sweeps = make.Select(m => m(slash)).ToArray();
                long oldFrames = old.FragmentFramesRead, oldPositions = old.FragmentPositionsRead, oldBeyond = old.FragmentsBeyondReach, oldVisited = old.FragmentsVisited;
                old.Evaluate(sweeps, new[] { slash });
                List<string> oldHits = Hits(old).ConvertAll(HitText);
                long frames = now.FragmentFramesRead, positions = now.FragmentPositionsRead, beyond = now.FragmentsBeyondReach, visited = now.FragmentsVisited;
                now.Evaluate(sweeps, new[] { slash });
                List<string> nowHits = Hits(now).ConvertAll(HitText);
                TestContext.Out.WriteLine(what + " (" + sweeps.Length + " sweeps over " + shapes + " shapes): hits " + nowHits.Count
                    + "; as before: visits " + (old.FragmentsVisited - oldVisited) + ", positions read " + (old.FragmentPositionsRead - oldPositions) + ", passed over by the reach " + (old.FragmentsBeyondReach - oldBeyond)
                    + ", frames read " + (old.FragmentFramesRead - oldFrames)
                    + "; now: visits " + (now.FragmentsVisited - visited) + ", positions read " + (now.FragmentPositionsRead - positions) + ", passed over by the reach " + (now.FragmentsBeyondReach - beyond)
                    + ", frames read " + (now.FragmentFramesRead - frames));
                Assert.That(nowHits, Is.EqualTo(oldHits), what + ": the same hits in the same order");
                Assert.That(nowHits.Count, Is.EqualTo(expectHits), what);
                Assert.That(old.FragmentFramesRead - oldFrames, Is.EqualTo(shapes), what + ": as before, every shape's frame is read");
                Assert.That(old.FragmentPositionsRead - oldPositions, Is.EqualTo(0), what + ": as before, no position is read apart");
                Assert.That(now.FragmentsVisited - visited, Is.EqualTo((long)shapes * sweeps.Length), what + ": every shape is still gone through for every sweep");
                Assert.That(now.FragmentPositionsRead - positions, Is.EqualTo(expectPositions), what + ": every owner's position is read, once an update");
                Assert.That(now.FragmentFramesRead - frames, Is.LessThanOrEqualTo(expectFramesAtMost), what + ": only the shapes near the sweep have their frame read");
                Assert.That(now.FragmentFramesRead - frames + 0L, Is.GreaterThanOrEqualTo(expectHits), what);
            }

            // A touch on the first box's top (a hit, answered as a no-op): its frame and its neighbours' at most.
            Both("a sweep on the first box", 1, 4, shapes, s => Level(s, 1f, -0.5f, 0.5f, -0.5f, 0.5f));
            // 30 m over everything: no frame at all.
            Both("a sweep 30 m over the field", 0, 0, shapes, s => Level(s, 30f, 10f, 13f, 10f, 13f));
            // 200 m beside the field.
            Both("a sweep 200 m beside the field", 0, 0, shapes, s => Level(s, 0.5f, -203f, -200f, 0f, 3f));
            // Two sweeps of one Slash in one update, at two corners: each position still read once.
            Both("two sweeps at two corners in one update", 1, 8, shapes, s => Level(s, 1f, -0.5f, 0.5f, -0.5f, 0.5f), s => Level(s, 30f, 27f, 29f, 27f, 29f));
            Assert.That(PhysicsFragmentOwner.HitReachSettles, Is.GreaterThanOrEqualTo(shapes), "a reach was settled for every owner when it was made");
            long settles = PhysicsFragmentOwner.HitReachSettles;
            Both("another sweep", 0, 0, shapes, s => Level(s, 30f, 10f, 13f, 10f, 13f));
            Assert.That(PhysicsFragmentOwner.HitReachSettles, Is.EqualTo(settles), "no reach is settled by an update");
            yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator FragmentReach_InAWorldBeingCut_PassesOverOnlyWhatWouldNotBeHit_AndEveryKeptReachHoldsItsShape()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;   // the Main budget is not this case's subject
            AddBodyGrid(root, 16, 4, 4f, true);
            yield return null;
            var detector = new SlashHitDetector(root, in k_hitSettings) { fragmentReachCheckForTest = true };
            var random = new System.Random(20261005);
            var list = new List<CurrentShape>(64);
            var shapeOf = new Dictionary<Transform, PhysicsOwnerShape>();
            var tally = new ReachTally { loosest = 1.0 };
            int hits = 0, published = 0, sweepsGiven = 0;
            CheckKeptReaches(root, list, shapeOf, ref tally, "before any cut");
            for (int round = 0; round < 40; round++)
            {
                // One to three sweeps an update, of one Slash: quads of many sizes somewhere over the grid, turned any way.
                long slash = round + 1;
                var sweeps = new SlashSweep[1 + round % 3];
                for (int k = 0; k < sweeps.Length; k++)
                {
                    var centre = new Vector3((float)(random.NextDouble() * 16.0 - 2.0), (float)(random.NextDouble() * 3.0 - 1.5), (float)(random.NextDouble() * 16.0 - 2.0));
                    Quaternion turn = Quaternion.Euler((float)(random.NextDouble() * 360.0), (float)(random.NextDouble() * 360.0), (float)(random.NextDouble() * 360.0));
                    sweeps[k] = Quad(slash, centre, turn, 0.5f + (float)(random.NextDouble() * 5.5), 0.2f + (float)(random.NextDouble() * 4.8));
                }

                detector.Evaluate(sweeps, new[] { slash });
                sweepsGiven += sweeps.Length;
                for (int i = 0; i < detector.HitCount; i++)
                {
                    hits++;
                    if (detector.HitAt(i).Acceptance == ProvisionalCutAcceptance.Published) published++;
                }

                Assert.That(detector.FragmentReachDisagreements, Is.Zero, "round " + round + ": a shape passed over by its reach was a hit");
                CheckKeptReaches(root, list, shapeOf, ref tally, "round " + round + ", after its hits");
                for (int f = 0; f < 3; f++)
                {
                    yield return null;   // the cuts go on in the ordinary frames: pairs published, their shapes replaced, their actors handed over
                    CheckKeptReaches(root, list, shapeOf, ref tally, "round " + round + ", frame " + f);
                }
            }

            TestContext.Out.WriteLine("sweeps " + sweepsGiven + "; sweep-by-shape visits " + detector.FragmentsVisited + ", passed over by the reach " + detector.FragmentsBeyondReach
                + " (each tested all the way all the same: hits among them " + detector.FragmentReachDisagreements + "; kept by the world box " + detector.FragmentsBeyondReachKeptByBox
                + "), hits passed on " + hits + " (published " + published + ")");
            TestContext.Out.WriteLine("current shapes checked over all frames " + tally.shapes + " (with a reach " + tally.withReach + "); of them sides of published pairs " + tally.sides
                + " (with a reach " + tally.sidesWithReach + "); a Root seen with another shape than before " + tally.shapeChangedOnItsRoot
                + " times; the reach over the farthest vertex at most x" + tally.loosest.ToString("F4"));
            Assert.That(detector.FragmentsBeyondReach, Is.GreaterThan(100), "the reach did pass shapes over");
            Assert.That(published, Is.GreaterThan(4), "the world was cut while it ran");
            Assert.That(tally.withReach, Is.EqualTo(tally.shapes), "every current shape had a reach");
            Assert.That(tally.sidesWithReach, Is.GreaterThan(0), "sides of published pairs were among them, each with its own reach");
            Assert.That(tally.shapeChangedOnItsRoot, Is.GreaterThan(0), "a Root was given another shape while it ran, and its reach held the new one");
            for (int f = 0; f < 30; f++) yield return null;
            CheckKeptReaches(root, list, shapeOf, ref tally, "at the end");
            yield return EndWorld(root);
        }

        // The box shape with its corners scaled and moved in its own frame, placed in its owner by localToOwner.
        private PhysicsOwnerShape NewBoxShapeOf(float3 cornerScale, float3 cornerOffset, float4x4 localToOwner)
        {
            var corners = new float3[k_corners.Length];
            for (int i = 0; i < corners.Length; i++) corners[i] = k_corners[i] * cornerScale + cornerOffset;
            var faceOffsets = new[] { 0, 4, 8, 12, 16, 20, 24 };
            var faceIndices = new List<int>();
            foreach ((int[] cycle, int _) in k_faces) faceIndices.AddRange(cycle);
            BuildEdges(faceOffsets, faceIndices.ToArray(), out int[] faceEdges, out BrepEdge[] edges);
            var vertices = default(NativeArray<float3>);
            var offsets = default(NativeArray<int>);
            var indices = default(NativeArray<int>);
            var edgesOfFaces = default(NativeArray<int>);
            var edgeTable = default(NativeArray<BrepEdge>);
            try
            {
                vertices = new NativeArray<float3>(corners, Allocator.Persistent);
                offsets = new NativeArray<int>(faceOffsets, Allocator.Persistent);
                indices = new NativeArray<int>(faceIndices.ToArray(), Allocator.Persistent);
                edgesOfFaces = new NativeArray<int>(faceEdges, Allocator.Persistent);
                edgeTable = new NativeArray<BrepEdge>(edges, Allocator.Persistent);
                var bank = new ConvexBrepBank
                {
                    vertices = (float3*)vertices.GetUnsafePtr(), faceOffsets = (int*)offsets.GetUnsafePtr(), faceIndices = (int*)indices.GetUnsafePtr(),
                    faceEdges = (int*)edgesOfFaces.GetUnsafePtr(), edges = (BrepEdge*)edgeTable.GetUnsafePtr(),
                };
                var range = new ConvexBrepRange
                {
                    vertexBase = 0, vertexCount = corners.Length, faceBase = 0, faceCount = k_faces.Length,
                    faceIndexBase = 0, faceIndexCount = faceIndices.Count, edgeBase = 0, edgeCount = edges.Length, maxFaceLoop = 4,
                };
                var mesh = Track(new Mesh { name = "Authored collider (placed)", hideFlags = HideFlags.HideAndDontSave });
                mesh.vertices = corners.Select(c => (Vector3)c).ToArray();
                mesh.triangles = new[]
                {
                    0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                    2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3,
                };
                UnityEngine.Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
                return PhysicsOwnerShape.Authored(bank, new[] { range }, new List<Mesh> { mesh }, PhysicsShapeSource.External(), localToOwner);
            }
            finally
            {
                if (edgeTable.IsCreated) edgeTable.Dispose();
                if (edgesOfFaces.IsCreated) edgesOfFaces.Dispose();
                if (indices.IsCreated) indices.Dispose();
                if (offsets.IsCreated) offsets.Dispose();
                if (vertices.IsCreated) vertices.Dispose();
            }
        }

        // A body with that shape on a Root placed, turned and scaled as given (under a parent when one is given), taken
        // into the world. Kinematic: nothing of this case is the simulation's.
        private LogicalFragmentId AddShapedBody(CutWorldRoot root, PhysicsOwnerShape shape, Vector3 at, Quaternion turn, Vector3 scale, Transform parent, out PhysicsFragmentOwner owner)
        {
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendBoxGeometry(root.Storage, default);
            var actor = TrackActor(new GameObject("Shaped body"));
            if (parent != null)
            {
                actor.transform.SetParent(parent, false);
                actor.transform.SetLocalPositionAndRotation(at, turn);   // in its parent
            }
            else
            {
                actor.transform.SetPositionAndRotation(at, turn);
            }

            actor.transform.localScale = scale;
            var body = actor.AddComponent<Rigidbody>();
            body.useGravity = false;
            body.isKinematic = true;
            MeshCollider collider = actor.AddComponent<MeshCollider>();
            collider.cookingOptions = PhysicsCutCook.DefaultCooking;
            collider.convex = true;
            collider.sharedMesh = shape.MeshOf(0);
            bool added = root.TryAddBody(actor, shape, geometry, Matrix4x4.identity, Matrix4x4.identity, null, false, out LogicalFragmentId fragment);
            if (added) _registered.Add(actor);
            Assert.That(added, Is.True, "the body was taken into the world");
            Assert.That(root.Owners.TryGet(fragment, out owner), Is.True);
            return fragment;
        }

        private static float3[] WorldVertices(PhysicsFragmentOwner owner)
        {
            float4x4 m = math.mul((float4x4)owner.Root.transform.localToWorldMatrix, owner.Shape.LocalToOwner);
            ConvexBrepBank bank = owner.Shape.BankOf(0);
            ConvexBrepRange range = owner.Shape.Convex(0);
            var world = new float3[range.vertexCount];
            for (int v = 0; v < world.Length; v++) world[v] = math.transform(m, bank.vertices[range.vertexBase + v]);
            return world;
        }

        [UnityTest]
        public IEnumerator FragmentReach_ShapesOffTheOrigin_Turned_Scaled_Large_AreNotMissed()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            yield return null;
            GameObject parent = TrackActor(new GameObject("scaled parent"));
            parent.transform.position = new Vector3(1400f, 0f, 0f);
            parent.transform.localScale = new Vector3(2f, 2f, 2f);
            GameObject turnedParent = TrackActor(new GameObject("turned and scaled parent"));
            turnedParent.transform.SetPositionAndRotation(new Vector3(1800f, 0f, 0f), Quaternion.Euler(20f, 50f, 10f));
            turnedParent.transform.localScale = new Vector3(1.5f, 1f, 2.5f);
            float4x4 id = float4x4.identity;
            Quaternion none = Quaternion.identity;
            var one = new float3(1f);
            // exact: whether every vertex stands at coordinates the arithmetic gives exactly (a level sweep on its top is then a touch)
            var cases = new List<(string name, PhysicsFragmentOwner owner, LogicalFragmentId fragment, bool exact)>();
            void Add(string name, PhysicsOwnerShape shape, Vector3 at, Quaternion turn, Vector3 scale, Transform under, bool exact)
            {
                LogicalFragmentId f = AddShapedBody(root, shape, at, turn, scale, under, out PhysicsFragmentOwner o);
                cases.Add((name, o, f, exact));
            }

            Add("a plain box on its owner's origin", NewBoxShapeOf(one, 0f, id), new Vector3(0f, 0f, 0f), none, Vector3.one, null, true);
            Add("its corners 6 m off its own frame's origin", NewBoxShapeOf(one, new float3(6f, 0f, 2f), id), new Vector3(200f, 0f, 0f), none, Vector3.one, null, true);
            Add("placed 5 m off in its owner", NewBoxShapeOf(one, 0f, float4x4.Translate(new float3(0f, 4f, -3f))), new Vector3(400f, 0f, 0f), none, Vector3.one, null, true);
            Add("placed off, scaled 2 by 0.5 by 4 in its owner", NewBoxShapeOf(one, 0f, float4x4.TRS(new float3(3f, 1f, 0f), quaternion.identity, new float3(2f, 0.5f, 4f))), new Vector3(600f, 0f, 0f), none, Vector3.one, null, true);
            Add("placed off and turned in its owner", NewBoxShapeOf(one, new float3(1f, 0f, 0f), float4x4.TRS(new float3(0f, 5f, -4f), quaternion.Euler(0.5f, 0.7f, 0.9f), one)), new Vector3(800f, 0f, 0f), none, Vector3.one, null, false);
            Add("large (32 m)", NewBoxShapeOf(new float3(16f), 0f, id), new Vector3(1000f, 0f, 0f), none, Vector3.one, null, true);
            Add("its Root scaled 2 by 1 by 4", NewBoxShapeOf(one, new float3(2f, 0f, 0f), id), new Vector3(1200f, 0f, 0f), none, new Vector3(2f, 1f, 4f), null, true);
            Add("its Root under a parent scaled 2", NewBoxShapeOf(one, new float3(0f, 0f, 3f), id), new Vector3(0f, 0f, 0f), none, Vector3.one, parent.transform, true);
            Add("its Root turned", NewBoxShapeOf(one, new float3(4f, 0f, 0f), id), new Vector3(1600f, 0f, 0f), Quaternion.Euler(35f, 110f, 20f), Vector3.one, null, false);
            Add("its Root turned and scaled under a turned and scaled parent, the shape placed off, turned and scaled",
                NewBoxShapeOf(new float3(1f, 2f, 1f), new float3(3f, 0f, 0f), float4x4.TRS(new float3(0f, 2f, 5f), quaternion.Euler(0.3f, 1.2f, 0.4f), new float3(1f, 3f, 0.5f))),
                new Vector3(0f, 1f, 0f), Quaternion.Euler(15f, 70f, 40f), new Vector3(1f, 2f, 1f), turnedParent.transform, false);
            yield return null;

            var list = new List<CurrentShape>(16);
            var tally = new ReachTally { loosest = 1.0 };
            CheckKeptReaches(root, list, null, ref tally, "as made");
            Assert.That(tally.withReach, Is.EqualTo(cases.Count), "every one has a reach");
            var old = new SlashHitDetector(root, in k_hitSettings) { fragmentReachReject = false };
            var now = new SlashHitDetector(root, in k_hitSettings);
            var checking = new SlashHitDetector(root, in k_hitSettings) { fragmentReachCheckForTest = true };
            long slash = 0;
            foreach ((string name, PhysicsFragmentOwner owner, LogicalFragmentId fragment, bool exact) in cases)
            {
                float3[] world = WorldVertices(owner);
                float3 origin = owner.Root.transform.position;
                float3 lo = world.Aggregate((a, b) => math.min(a, b)), hi = world.Aggregate((a, b) => math.max(a, b));
                float3 farthest = world.OrderByDescending(v => math.distance(v, origin)).First();
                float reach = owner.HitReach, distance = math.distance(farthest, origin), halfDiagonal = 0.5f * math.distance(lo, hi);
                float3 outward = math.normalizesafe(farthest - origin, new float3(0f, 1f, 0f));
                TestContext.Out.WriteLine(name + ": reach " + reach.ToString("F4") + ", its farthest vertex " + distance.ToString("F4") + " from its owner's position; half its world box's diagonal "
                    + halfDiagonal.ToString("F4") + "; world box " + lo + " .. " + hi);
                Assert.That(reach, Is.GreaterThanOrEqualTo(distance), name + ": the reach holds its farthest vertex");
                // the farthest vertex's distance itself for a Root under no unevenly scaled parent (its own scale is in
                // the carrying of the corners); under a turned, unevenly scaled parent the parent's part is a bound
                Transform over = owner.Root.transform.parent;
                bool bounded = over != null && (over.localScale.x != over.localScale.y || over.localScale.x != over.localScale.z);
                Assert.That(reach, Is.LessThanOrEqualTo(bounded ? distance * 2.6f : distance * 1.001f + 1e-3f), name + ": the reach is not far over its farthest vertex");

                SlashSweep Small(long s, float3 c) => Level(s, c.y, c.x - 0.05f, c.x + 0.05f, c.z - 0.05f, c.z + 0.05f);

                // (a) well beyond the reach, along the farthest vertex's direction: passed over, nothing read but the position.
                slash++;
                long frames = now.FragmentFramesRead, beyond = now.FragmentsBeyondReach;
                now.Evaluate(new[] { Small(slash, origin + outward * (reach * 1.05f + 1f)) }, new[] { slash });
                Assert.That(now.HitCount, Is.EqualTo(0), name + ": beyond");
                Assert.That(now.FragmentFramesRead - frames, Is.EqualTo(0), name + ": beyond its reach no frame is read (its own, or any other's)");
                Assert.That(now.FragmentsBeyondReach - beyond, Is.EqualTo(cases.Count), name + ": every shape was passed over by its reach");

                // (b) just outside its farthest vertex, by half the margin: a small quad in the plane that faces the
                //     owner's position there (the shape lies wholly on the near side of it, so nothing is cut). Not
                //     certainly beyond: it is not passed over -- its frame is read, and only its.
                slash++;
                frames = now.FragmentFramesRead;
                float margin = 1e-4f * (math.cmax(math.abs(origin)) + reach) + 1e-4f;
                Vector3 justOutside = farthest + outward * (0.5f * margin);
                now.Evaluate(new[] { Quad(slash, justOutside, Quaternion.FromToRotation(Vector3.up, outward), 0.1f, 0.1f) }, new[] { slash });
                Assert.That(now.FragmentFramesRead - frames, Is.EqualTo(1), name + ": just outside its farthest vertex its frame is read, and only its");
                foreach (SlashHitConfirmed h in Hits(now)) Assert.That(h.Acceptance != ProvisionalCutAcceptance.Published && h.Acceptance != ProvisionalCutAcceptance.Pending, Is.True, name + ": nothing is cut there: " + h.Acceptance);

                // (c) on a shape whose vertices stand at exact coordinates: a level sweep on its top is a touch -- a hit,
                //     answered as a no-op -- and one a little above it is none; the same hits as before either way.
                if (exact)
                {
                    float3 top = world.OrderByDescending(v => v.y).First();
                    foreach (float above in new[] { 0f, 0.01f })
                    {
                        slash++;
                        SlashSweep sweep = Level(slash, top.y + above, lo.x - 0.25f, hi.x + 0.25f, lo.z - 0.25f, hi.z + 0.25f);
                        old.Evaluate(new[] { sweep }, new[] { slash });
                        List<SlashHitConfirmed> oldHits = Hits(old);
                        now.Evaluate(new[] { sweep }, new[] { slash });
                        List<SlashHitConfirmed> nowHits = Hits(now);
                        Assert.That(nowHits.ConvertAll(HitText), Is.EqualTo(oldHits.ConvertAll(HitText)), name + ": the same hits as with every frame read");
                        Assert.That(nowHits.Count, Is.EqualTo(above == 0f ? 1 : 0), name + (above == 0f ? ": met on its top" : ": not met above its top"));
                        if (above == 0f)
                        {
                            Assert.That(nowHits[0].Fragment, Is.EqualTo(fragment), name);
                            Assert.That(nowHits[0].Acceptance != ProvisionalCutAcceptance.Published && nowHits[0].Acceptance != ProvisionalCutAcceptance.Pending, Is.True,
                                name + ": a touch, not a cut: " + nowHits[0].Acceptance);
                        }
                    }
                }

                // (d) sweeps all round it, at its world box's faces and corners and a little off them, each tested all
                //     the way though passed over: none passed over by the reach is a hit.
                var around = new System.Random(4000 + (int)slash);
                for (int k = 0; k < 40; k++)
                {
                    slash++;
                    float3 pick = new float3(around.Next(3) - 1, around.Next(3) - 1, around.Next(3) - 1);
                    float3 at = 0.5f * (lo + hi) + pick * (0.5f * (hi - lo) + (float)around.NextDouble() * 0.3f * (k % 4));
                    Quaternion turn = Quaternion.Euler(0f, (float)around.NextDouble() * 360f, 0f);
                    // only level sweeps that do not go through it: at or above its top, or at or below its bottom
                    at.y = k % 2 == 0 ? hi.y + (float)around.NextDouble() * 0.5f * (k % 4) : lo.y - (float)around.NextDouble() * 0.5f * (k % 4);
                    if (!exact) at.y += k % 2 == 0 ? 0.02f : -0.02f;   // clear of a top or a bottom that is not exactly there
                    checking.Evaluate(new[] { Quad(slash, at, turn, 1.5f, 0.5f) }, new[] { slash });
                    Assert.That(checking.FragmentReachDisagreements, Is.Zero, name + ": sweep " + k + ": passed over by its reach, and a hit");
                }
            }

            TestContext.Out.WriteLine("the reach over the farthest vertex at most x" + tally.loosest.ToString("F4") + " (the turned and scaled parent's: its stretch is bounded, not measured exactly)");
            foreach ((string name, PhysicsFragmentOwner owner, LogicalFragmentId fragment, bool _) in cases) Assert.That(root.Ledger.IsCurrentTarget(fragment), Is.True, name + ": nothing was cut");
            yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator FragmentReach_APairsTwoSides_AShapeReplaced_AndAShapeOnAnotherOwner_EachKeepTheirOwn()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            yield return null;
            GameObject NewRoot(string name, Vector3 at, Vector3 scale)
            {
                GameObject o = TrackActor(new GameObject(name));
                o.SetActive(false);   // nothing of this case enters the physics scene
                o.transform.position = at;
                o.transform.localScale = scale;
                return o;
            }

            PhysicsOwnerShape Shape(float size, float3 offset)
            {
                PhysicsOwnerShape s = NewBoxShapeOf(new float3(size), offset, float4x4.identity);
                _disposables.Add(s);
                return s;
            }

            float Farthest(float size, float3 offset, float stretch) => stretch * math.length(new float3(size) + math.abs(offset));

            // A pair: the positive side's Root unscaled with a small shape off its origin, the negative side's Root scaled 3
            // with another shape. Each side's reach is of its own Root and its own shape.
            GameObject plusRoot = NewRoot("pair +", new Vector3(10f, 0f, 0f), Vector3.one), minusRoot = NewRoot("pair -", new Vector3(-50f, 0f, 0f), new Vector3(3f, 3f, 3f));
            PhysicsOwnerShape plusShape = Shape(1f, new float3(2f, 0f, 0f)), minusShape = Shape(0.5f, new float3(0f, 0f, 1f));
            var plus = new PhysicsOwnerSide(true, plusRoot, null, plusRoot.AddComponent<Rigidbody>());
            var minus = new PhysicsOwnerSide(false, minusRoot, null, minusRoot.AddComponent<Rigidbody>());
            long settles = PhysicsFragmentOwner.HitReachSettles;
            var pair = new ProvisionalOwnerPair(default, default, plus, minus, plusShape, minusShape, null, null);
            Assert.That(PhysicsFragmentOwner.HitReachSettles - settles, Is.EqualTo(2), "both sides settled when the pair is made");
            float plusReach = pair.HitReach(true), minusReach = pair.HitReach(false);
            TestContext.Out.WriteLine("pair as made: + " + plusReach + " (its farthest corner " + Farthest(1f, new float3(2f, 0f, 0f), 1f) + "), - " + minusReach + " (" + Farthest(0.5f, new float3(0f, 0f, 1f), 3f) + ")");
            Assert.That(plusReach, Is.EqualTo(Farthest(1f, new float3(2f, 0f, 0f), 1f)).Within(1e-3f), "the positive side: its own shape on its own Root");
            Assert.That(minusReach, Is.EqualTo(Farthest(0.5f, new float3(0f, 0f, 1f), 3f)).Within(1e-2f), "the negative side: its own shape on its own Root, with that Root's scale");
            Assert.That(PhysicsFragmentOwner.HitReachSettles - settles, Is.EqualTo(2), "asking settles nothing again");

            // Moving and turning a side's Root changes nothing, and nothing is settled for it.
            plusRoot.transform.SetPositionAndRotation(new Vector3(300f, 20f, -7f), Quaternion.Euler(40f, 50f, 60f));
            Assert.That(pair.HitReach(true), Is.EqualTo(plusReach), "moved and turned: the same reach");
            Assert.That(PhysicsFragmentOwner.HitReachSettles - settles, Is.EqualTo(2));

            // The shapes replaced (the switch to the final ones): both sides are settled again at that moment, for the new ones.
            PhysicsOwnerShape plusFinal = Shape(4f, new float3(0f, 9f, 0f)), minusFinal = Shape(0.25f, 0f);
            pair.TakeFinalShapes(plusFinal, minusFinal);
            Assert.That(PhysicsFragmentOwner.HitReachSettles - settles, Is.EqualTo(4), "both sides settled again when their shapes are replaced");
            TestContext.Out.WriteLine("pair after its shapes were replaced: + " + pair.HitReach(true) + ", - " + pair.HitReach(false));
            Assert.That(pair.HitReach(true), Is.EqualTo(Farthest(4f, new float3(0f, 9f, 0f), 1f)).Within(1e-2f), "the positive side keeps the new shape's reach, not the one it had");
            Assert.That(pair.HitReach(false), Is.EqualTo(Farthest(0.25f, 0f, 3f)).Within(1e-2f), "the negative side too");

            // An owner: its reach is settled when it is made; the same shape on another owner, with another Root, has that owner's.
            GameObject ownerRoot = NewRoot("owner", new Vector3(0f, 0f, 80f), Vector3.one), otherRoot = NewRoot("other owner", new Vector3(0f, 0f, 160f), new Vector3(1f, 5f, 1f));
            PhysicsOwnerShape shared = Shape(1f, new float3(0f, 2f, 0f));
            var owner = new PhysicsFragmentOwner(ownerRoot, ownerRoot.AddComponent<Rigidbody>(), shared, false);
            var other = new PhysicsFragmentOwner(otherRoot, otherRoot.AddComponent<Rigidbody>(), shared, false);
            TestContext.Out.WriteLine("one shape on two owners: " + owner.HitReach + " and, on a Root scaled 5 along y, " + other.HitReach);
            Assert.That(owner.HitReach, Is.EqualTo(Farthest(1f, new float3(0f, 2f, 0f), 1f)).Within(1e-3f));
            Assert.That(other.HitReach, Is.GreaterThanOrEqualTo(math.length(new float3(1f, 15f, 1f))), "the other owner's Root stretches it: its own reach");
            Assert.That(other.HitReach, Is.LessThanOrEqualTo(Farthest(1f, new float3(0f, 2f, 0f), 5f) * 1.001f), "by no more than its largest scale");

            // A Root's scale changed afterwards: the reach is the one settled until whoever changed it has it settled again.
            float before = owner.HitReach;
            ownerRoot.transform.localScale = new Vector3(2f, 2f, 2f);
            Assert.That(owner.HitReach, Is.EqualTo(before), "not read again by itself: the change's own code has it settled");
            owner.RefreshHitReach();
            Assert.That(owner.HitReach, Is.EqualTo(2f * before).Within(1e-2f), "settled again: with the Root's stretch as it is now");

            // Turning needs no settling, whatever is scaled: an owner scaled unevenly under a parent scaled unevenly is
            // stretched differently at every attitude -- its farthest vertex comes and goes -- and the reach settled once,
            // before any turn, holds it at every one of them.
            GameObject unevenParent = NewRoot("parent scaled unevenly", new Vector3(0f, 0f, 400f), new Vector3(1f, 1f, 3f));
            GameObject unevenRoot = NewRoot("owner scaled unevenly under it", Vector3.zero, new Vector3(3f, 1f, 1f));
            unevenRoot.transform.SetParent(unevenParent.transform, false);
            PhysicsOwnerShape turnedShape = Shape(1f, new float3(2f, 0f, 0f));
            var uneven = new PhysicsFragmentOwner(unevenRoot, unevenRoot.AddComponent<Rigidbody>(), turnedShape, false);
            float unevenReach = uneven.HitReach;
            double least = double.MaxValue, most = 0.0;
            long settledBeforeTurning = PhysicsFragmentOwner.HitReachSettles;
            for (int a = 0; a < 72; a++)
            {
                unevenRoot.transform.localRotation = Quaternion.Euler(13f * a, 5f * a, 7f * a);
                if (a % 3 == 0) unevenParent.transform.rotation = Quaternion.Euler(3f * a, 11f * a, 0f);
                double farthest = FarthestVertex(unevenRoot.transform, turnedShape);
                least = math.min(least, farthest);
                most = math.max(most, farthest);
                Assert.That(uneven.HitReach, Is.EqualTo(unevenReach), "attitude " + a + ": nothing is settled for a turn");
                if (farthest > unevenReach) Assert.Fail("attitude " + a + ": a vertex stands " + farthest + " from the owner's position, the reach settled before any turn is " + unevenReach);
            }

            Assert.That(PhysicsFragmentOwner.HitReachSettles, Is.EqualTo(settledBeforeTurning));
            TestContext.Out.WriteLine("an owner scaled (3,1,1) under a parent scaled (1,1,3), turned 72 ways: its farthest vertex between " + least.ToString("F3") + " and " + most.ToString("F3")
                + " from its position; the reach settled once " + unevenReach.ToString("F3"));
            Assert.That(most, Is.GreaterThan(least * 1.3), "the turns did change how far it reaches");

            // What cannot be vouched for has no reach: a Root whose stretch overflows, a shape given back.
            GameObject hugeRoot = NewRoot("owner whose stretch overflows", new Vector3(0f, 0f, 240f), new Vector3(3e19f, 3e19f, 3e19f));
            var huge = new PhysicsFragmentOwner(hugeRoot, hugeRoot.AddComponent<Rigidbody>(), shared, false);
            Assert.That(huge.HitReach, Is.LessThan(0f), "a stretch that overflows: no reach");
            Assert.That(FragmentHitReach.Settle(null, ownerRoot.transform), Is.LessThan(0f), "no shape: no reach");
            Assert.That(FragmentHitReach.Settle(shared, null), Is.LessThan(0f), "no owner: no reach");
            PhysicsOwnerShape gone = NewBoxShapeOf(new float3(1f), 0f, float4x4.identity);
            gone.Dispose();
            Assert.That(FragmentHitReach.Settle(gone, ownerRoot.transform), Is.LessThan(0f), "a shape given back: no reach");
            yield return EndWorld(root);
        }

        [UnityTest]
        public IEnumerator FragmentReach_WhatCannotBeVouchedFor_TakesTheTestsItTookBefore()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            LogicalFragmentId near = AddBody(root, new Vector3(0f, 0f, 0f));
            // An owner standing so far off that the square of its distance overflows a float: its reach is kept, and
            // nothing can be said of the distance -- it is not passed over early, and takes the tests it took before.
            LogicalFragmentId far = AddShapedBody(root, NewBoxShapeOf(new float3(1f), 0f, float4x4.identity), new Vector3(50f, 0f, 0f), Quaternion.identity, Vector3.one, null, out PhysicsFragmentOwner farOwner);
            farOwner.Root.SetActive(false);   // not in the physics scene: only where its Transform says it is matters here
            farOwner.Root.transform.position = new Vector3(1e20f, 0f, 0f);   // finite, and the square of the distance to anything near is not
            yield return null;
            Assert.That(farOwner.HitReach, Is.GreaterThan(0f), "its reach is kept: where it stands is no part of it");
            var old = new SlashHitDetector(root, in k_hitSettings) { fragmentReachReject = false };
            var now = new SlashHitDetector(root, in k_hitSettings);
            long slash = 0;
            foreach (SlashSweep sweep in new[] { Level(++slash, 1f, -0.5f, 0.5f, -0.5f, 0.5f), Level(++slash, 30f, -0.5f, 0.5f, -0.5f, 0.5f), Level(++slash, 1f, 100f, 101f, 0f, 1f) })
            {
                old.Evaluate(new[] { sweep }, new[] { sweep.SlashId });
                List<string> oldHits = Hits(old).ConvertAll(HitText);
                long frames = now.FragmentFramesRead, beyond = now.FragmentsBeyondReach, positions = now.FragmentPositionsRead;
                now.Evaluate(new[] { sweep }, new[] { sweep.SlashId });
                Assert.That(Hits(now).ConvertAll(HitText), Is.EqualTo(oldHits), "sweep " + sweep.SlashId + ": the same hits");
                Assert.That(now.FragmentPositionsRead - positions, Is.EqualTo(2), "both positions read");
                Assert.That(now.FragmentsBeyondReach - beyond, Is.LessThanOrEqualTo(1), "sweep " + sweep.SlashId + ": the one whose distance cannot be squared is not passed over by its reach");
                Assert.That(now.FragmentFramesRead - frames, Is.GreaterThanOrEqualTo(1), "sweep " + sweep.SlashId + ": its frame is read, as before");
            }

            Assert.That(root.Ledger.IsCurrentTarget(near) && root.Ledger.IsCurrentTarget(far), Is.True, "nothing was cut");
            yield return EndWorld(root);
        }


    }
}
