using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
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
    /// DESIGN 7.9.2's additional split, first version (<see cref="CutSpatialSplit"/>, Phase 5.6: no building, no
    /// convex cut) through the product's own world. The fixture's body is a trunk with a detached hand beside it --
    /// two convexes and two closed parts of one geometry -- so a cut at the waist leaves a lower side that holds half
    /// of the trunk and the whole hand in one owner: what the split is for. What it publishes and where, what stays
    /// one object (a mesh that bridges the gap, a convex across it, a gap within the margin), a result overtaken by a
    /// cut or a retirement, the frames and the room it does not publish in, and nothing at all while it is off. The
    /// world is ended by every case, so what it holds is given back at its release.
    /// </summary>
    public unsafe partial class CutWorldRootPlayModeTests
    {
        private static CutSpatialSplitSettings SplitOn()
        {
            return new CutSpatialSplitSettings(
                true, 64, CutWorldRoot.CompactionReserveSeconds, 0.0002, 0.0015, new SpatialSplitSearchLimits(64, 8, 16, 131072));
        }

        /// <summary>
        /// The split of this world, on, with a frame budget the case decides: closed (nothing left, so every turn of
        /// the product's own frame is held) until the case opens it. The collections are not what these cases are about.
        /// </summary>
        private static CutSpatialSplit SplitClosed(CutWorldRoot root, double[] remaining, bool theProductsOwnStepEstimate = false)
        {
            CutSpatialSplit split = root.SpatialSplit;
            remaining[0] = 0.0;
            split.RemainingMainSeconds = () => remaining[0];
            if (!theProductsOwnStepEstimate)
            {
                // The simulation still to come is a case's own to give, where its budget is exact to a few microseconds.
                split.LaterStepSeconds = () => 0.0;
            }

            split.Settings = SplitOn();
            root.EmptyOwners.Settings = EmptyOwnersOff();
            root.Groups.Settings = GroupsOff();
            return split;
        }

        private static float3[] BoxCornersAt(float3 centre, float3 half)
        {
            var corners = new float3[k_corners.Length];
            for (int i = 0; i < corners.Length; i++)
            {
                corners[i] = centre + (k_corners[i] * half);
            }

            return corners;
        }

        /// <summary>One authored shape of several boxes, a convex each, in one bank: the B-rep and the collider mesh of each are the same corners.</summary>
        private PhysicsOwnerShape NewBoxesShape(IReadOnlyList<float3[]> boxes)
        {
            int n = boxes.Count;
            var localOffsets = new[] { 0, 4, 8, 12, 16, 20, 24 };
            var localIndices = new List<int>();
            foreach ((int[] cycle, int _) in k_faces)
            {
                localIndices.AddRange(cycle);
            }

            BuildEdges(localOffsets, localIndices.ToArray(), out int[] localFaceEdges, out BrepEdge[] localEdges);
            var allVertices = new List<float3>();
            var allOffsets = new List<int>();
            var allIndices = new List<int>();
            var allFaceEdges = new List<int>();
            var allEdges = new List<BrepEdge>();
            var ranges = new ConvexBrepRange[n];
            var meshes = new List<Mesh>();
            for (int c = 0; c < n; c++)
            {
                // Face offsets are relative to the convex's own face indices; face indices and edges are local to it.
                ranges[c] = new ConvexBrepRange
                {
                    vertexBase = allVertices.Count, vertexCount = k_corners.Length,
                    faceBase = allOffsets.Count, faceCount = k_faces.Length,
                    faceIndexBase = allIndices.Count, faceIndexCount = localIndices.Count,
                    edgeBase = allEdges.Count, edgeCount = localEdges.Length,
                    maxFaceLoop = 4,
                };
                allVertices.AddRange(boxes[c]);
                allOffsets.AddRange(localOffsets);
                allIndices.AddRange(localIndices);
                allFaceEdges.AddRange(localFaceEdges);
                allEdges.AddRange(localEdges);
                meshes.Add(NewColliderMeshOf(boxes[c]));
            }

            var vertices = default(NativeArray<float3>);
            var offsets = default(NativeArray<int>);
            var indices = default(NativeArray<int>);
            var edgesOfFaces = default(NativeArray<int>);
            var edgeTable = default(NativeArray<BrepEdge>);
            try
            {
                vertices = new NativeArray<float3>(allVertices.ToArray(), Allocator.Persistent);
                offsets = new NativeArray<int>(allOffsets.ToArray(), Allocator.Persistent);
                indices = new NativeArray<int>(allIndices.ToArray(), Allocator.Persistent);
                edgesOfFaces = new NativeArray<int>(allFaceEdges.ToArray(), Allocator.Persistent);
                edgeTable = new NativeArray<BrepEdge>(allEdges.ToArray(), Allocator.Persistent);
                var bank = new ConvexBrepBank
                {
                    vertices = (float3*)vertices.GetUnsafePtr(),
                    faceOffsets = (int*)offsets.GetUnsafePtr(),
                    faceIndices = (int*)indices.GetUnsafePtr(),
                    faceEdges = (int*)edgesOfFaces.GetUnsafePtr(),
                    edges = (BrepEdge*)edgeTable.GetUnsafePtr(),
                };
                return PhysicsOwnerShape.Authored(bank, ranges, meshes, PhysicsShapeSource.External(), float4x4.identity);
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

        private Mesh NewColliderMeshOf(float3[] corners)
        {
            var mesh = Track(new Mesh { name = "Authored collider", hideFlags = HideFlags.HideAndDontSave });
            var vertices = new Vector3[corners.Length];
            for (int i = 0; i < corners.Length; i++)
            {
                vertices[i] = corners[i];
            }

            mesh.vertices = vertices;
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2, 4, 5, 6, 4, 6, 7, 0, 1, 5, 0, 5, 4,
                2, 3, 7, 2, 7, 6, 1, 2, 6, 1, 6, 5, 0, 4, 7, 0, 7, 3,
            };
            UnityEngine.Physics.BakeMesh(mesh.GetEntityId(), true, PhysicsCutCook.DefaultCooking);
            return mesh;
        }

        /// <summary>The display geometry of several closed boxes, as one cut input of two submeshes: every box's sides, then every box's ends.</summary>
        private static VpStoredGeometry AppendBoxesGeometry(VpCpuGeometryStorage storage, IReadOnlyList<float3[]> boxes)
        {
            var vertices = new List<VpRenderVertex>();
            var topology = new List<int>();
            var indices = new List<uint>();
            var submeshes = new List<VpGeometrySubmesh>();
            for (int submesh = 0; submesh < 2; submesh++)
            {
                int start = indices.Count;
                for (int box = 0; box < boxes.Count; box++)
                {
                    float3[] corners = boxes[box];
                    for (int face = 0; face < k_faces.Length; face++)
                    {
                        if (k_faces[face].submesh != submesh)
                        {
                            continue;
                        }

                        int[] c = k_faces[face].cycle;
                        float3 normal = math.normalize(math.cross(corners[c[1]] - corners[c[0]], corners[c[2]] - corners[c[0]]));
                        uint b = (uint)vertices.Count;
                        var uv = new[]
                        {
                            new float2(0.05f, 0.1f), new float2(0.95f, 0.1f), new float2(0.95f, 0.9f), new float2(0.05f, 0.9f),
                        };
                        for (int k = 0; k < 4; k++)
                        {
                            vertices.Add(new VpRenderVertex { position = corners[c[k]], normal = normal, uv0 = uv[k] });
                            topology.Add((k_corners.Length * box) + c[k]);
                        }

                        indices.AddRange(new[] { b, b + 1, b + 2, b, b + 2, b + 3 });
                    }
                }

                submeshes.Add(new VpGeometrySubmesh(start, indices.Count - start, submesh == 0 ? SideMaterial : EndMaterial));
            }

            Assert.That(
                storage.TryAppendCuttable(
                    vertices.ToArray(), indices.ToArray(), topology.ToArray(), k_corners.Length * boxes.Count, submeshes.ToArray(),
                    out VpStoredGeometry geometry, out _),
                Is.True,
                "the display geometry of the body and its hand is appended");
            return geometry;
        }

        // The hand: a small box beside the trunk and wholly below the waist (y = 0), a gap of 1.75 between them.
        private const float HandLowX = 2.75f;

        /// <summary>
        /// A trunk (the fixture's box) and a hand beside it, below the waist: one owner of two convexes, one geometry
        /// of two closed parts. <paramref name="meshBridge"/> draws a bar from the trunk to the hand with no convex of
        /// its own; <paramref name="convexAcross"/> adds a convex from inside the trunk to inside the hand, with
        /// nothing drawn for it. Both lie below the waist.
        /// </summary>
        private LogicalFragmentId AddBodyWithAHand(
            CutWorldRoot root, Vector3 at, IReadOnlyList<float3> anchors = null, float handLowX = HandLowX, bool meshBridge = false,
            bool convexAcross = false, bool building = false)
        {
            float3[] trunk = BoxCornersAt(float3.zero, new float3(1f));
            float3[] hand = BoxCornersAt(new float3(handLowX + 0.25f, -0.5f, 0f), new float3(0.25f));
            var convexes = new List<float3[]> { trunk, hand };
            var drawn = new List<float3[]> { trunk, hand };
            if (meshBridge)
            {
                drawn.Add(BoxCornersAt(new float3(0.5f * (1f + handLowX), -0.5f, 0f), new float3(0.5f * (handLowX - 1f), 0.1f, 0.1f)));
            }

            if (convexAcross)
            {
                convexes.Add(BoxCornersAt(new float3(1.75f, -0.5f, 0f), new float3(1.25f, 0.1f, 0.1f)));
            }

            PhysicsOwnerShape shape = NewBoxesShape(convexes);
            _disposables.Add(shape);
            VpStoredGeometry geometry = AppendBoxesGeometry(root.Storage, drawn);

            var actor = TrackActor(new GameObject("Body with a hand"));
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
        /// Cuts <paramref name="body"/> at the waist and waits until the cut is over: [0] the side above, of one
        /// convex, [1] the side below, which holds what is left of the trunk and the hand in one owner.
        /// </summary>
        private static IEnumerator CutAtTheWaist(CutWorldRoot root, LogicalFragmentId body, LogicalFragmentId[] sides, int lowerConvexes = 2)
        {
            yield return CutInTwo(root, body, k_planeUp, sides);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the cut's record is over");
            yield return null;
            Assert.That(root.Owners.TryGet(sides[0], out PhysicsFragmentOwner upper) && upper.Shape.ConvexCount == 1, Is.True,
                "the layout: the side above is one convex");
            Assert.That(
                root.Owners.TryGet(sides[1], out PhysicsFragmentOwner lower) && lower.Body != null && lower.Shape.ConvexCount == lowerConvexes,
                Is.True, "the layout: the side below holds the rest of the trunk and the hand in one owner");
            Assert.That(root.Geometry.TryGetGeometry(sides[1], out _), Is.True, "the layout: its geometry is committed");
            Assert.That(root.Display.HasOwnRegistration(sides[1]), Is.True, "the layout: and it is drawn from its own registration");
        }

        private static List<(uint, uint, uint)> TrianglesOf(CutWorldRoot root, VpStoredGeometry geometry)
        {
            Assert.That(
                root.Storage.TryAcquireIndexReadLease(geometry.indexRange, out VpIndexReadLease lease, out NativeArray<uint>.ReadOnly view),
                Is.True, "the geometry's indices are readable");
            var triangles = new List<(uint, uint, uint)>(view.Length / 3);
            for (int i = 0; i + 2 < view.Length; i += 3)
            {
                triangles.Add((view[i], view[i + 1], view[i + 2]));
            }

            Assert.That(root.Storage.TryReleaseIndexReadLease(lease), Is.True);
            return triangles;
        }

        private static List<int> MaterialsOf(CutWorldRoot root, VpStoredGeometry geometry)
        {
            Assert.That(root.Storage.TryGetSubmeshes(geometry, out NativeArray<VpGeometrySubmesh>.ReadOnly submeshes), Is.True);
            var materials = new List<int>();
            for (int s = 0; s < submeshes.Length; s++)
            {
                materials.Add(submeshes[s].materialIndex);
            }

            return materials;
        }

        private static List<float3> AnchorsOf(CutWorldRoot root, LogicalFragmentId fragment)
        {
            var anchors = new List<float3>();
            Assert.That(root.Ledger.TryGetAnchors(fragment, anchors), Is.True);
            return anchors;
        }

        /// <summary>What one owner was at a moment: where it stood and how it moved.</summary>
        private struct OwnerMoment
        {
            internal LogicalFragmentId fragment;
            internal Vector3 position, velocity, angular, centre;
            internal Quaternion rotation;
            internal float mass;

            internal static OwnerMoment Of(LogicalFragmentId fragment, PhysicsFragmentOwner owner)
            {
                return new OwnerMoment
                {
                    fragment = fragment,
                    position = owner.Root.transform.position,
                    rotation = owner.Root.transform.rotation,
                    velocity = owner.Body.linearVelocity,
                    angular = owner.Body.angularVelocity,
                    centre = owner.Body.worldCenterOfMass,
                    mass = owner.Body.mass,
                };
            }
        }

        /// <summary>
        /// **A lower body and a detached hand become two ordinary objects, where they stood.** The lower side of a
        /// waist cut, moving and turning, is replaced by two fragments at one boundary: each with its own owner of the
        /// convexes on its side -- the very collider meshes, nothing cooked -- at the source's placement, with the
        /// velocity its own centre of mass had and the source's mass shared out; each with its own registration and
        /// its share of the source's triangles over the same vertices, every triangle once, submeshes and materials
        /// as they were. No operation, no pending cut and no transaction is made, and the source's own origin is what
        /// both carry.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplit_ALowerBodyAndADetachedHand_BecomeTwoOrdinaryObjects_WhereTheyStood()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining, theProductsOwnStepEstimate: true);
            LogicalFragmentId body = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f));
            yield return null;
            Assert.That(split.Examine(body), Is.EqualTo(SpatialSplitPass.NotCutMade), "a registered body no cut made is passed over, hand or no hand");
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            LogicalFragmentId upper = sides[0], lower = sides[1];
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner), Is.True);
            GameObject lowerActor = lowerOwner.Root;
            PhysicsOwnerShape lowerShape = lowerOwner.Shape;

            // Asked one by one: what each is.
            Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Candidate), "the side with the hand");
            Assert.That(split.Examine(upper), Is.EqualTo(SpatialSplitPass.FewConvexes), "one convex is on one side of anything");
            Assert.That(split.Examine(body), Is.EqualTo(SpatialSplitPass.NotLive), "replaced by its cut");
            Assert.That(split.Turns, Is.GreaterThan(0), "the layout: the product's own turns were taken");
            Assert.That(split.Examined + split.Issued, Is.Zero, "and held for the budget: nothing looked at, nothing issued");

            // A Slash that hit the lower side and cut nothing has consumed it (DESIGN 19.1.9): what the two inherit of that.
            var consumption = new SlashLineageConsumption(root.Ledger, 4);
            const long slash = 41;
            Assert.That(consumption.TryConsume(slash, lower), Is.True, "the layout: the Slash consumed the lower side, which lives on");

            // Moving and turning: no reason to wait, and what the two inherit.
            lowerOwner.Body.useGravity = false;
            lowerOwner.Body.linearVelocity = new Vector3(0.3f, 0f, 1f);
            lowerOwner.Body.angularVelocity = new Vector3(0f, 0.5f, 0f);

            Assert.That(root.Geometry.TryGetGeometry(lower, out VpStoredGeometry lowerGeometry), Is.True);
            List<(uint, uint, uint)> lowerTriangles = TrianglesOf(root, lowerGeometry);
            List<int> lowerMaterials = MaterialsOf(root, lowerGeometry);
            var lowerMeshes = new[] { lowerOwner.Shape.MeshOf(0), lowerOwner.Shape.MeshOf(1) };
            Matrix4x4? lowerFrame = lowerOwner.GeometryLocalToOwner;
            Assert.That(root.Geometry.TryGetGeometryFrame(lower, out Matrix4x4 lowerLineageFrame), Is.True);
            Assert.That(root.Ledger.TryGetOrigin(lower, out CutOperationId origin, out float originSide), Is.True);
            int operations = root.Ledger.OperationCount;
            int incomplete = root.Ledger.Budget.IncompleteCutOperationCount;
            int liveCut = root.Ledger.LiveCutFragmentCount;
            int owners = root.Owners.Count;
            int vertices = root.Storage.VertexCount;
            long indexTransfers = root.Display.SplitIndexTransfers;
            int classifications = PhysicsCutClassification.Live;

            // At the publication itself: the source as it stands, and each of the two as it enters.
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

            // The source's retirement is told apart from any other: the split names the fragment it is replacing while its owner retires.
            var namedAtRetirement = new List<LogicalFragmentId>();
            Action<LogicalFragmentId, PhysicsFragmentOwner> onRetiring = (fragment, owner) =>
            {
                if (fragment == lower)
                {
                    namedAtRetirement.Add(split.ReplacingFragment);
                }
            };
            root.Owners.OwnerRetiring += onRetiring;
            remaining[0] = 1.0;
            yield return Until(() => split.Published == 1 || split.EndedAs(SpatialSplitEnd.NotFound) + split.EndedAs(SpatialSplitEnd.NotBuildable) > 0,
                "the split of the lower side ended");
            remaining[0] = 0.0;
            root.Owners.OwnerAdded -= onAdded;
            root.Owners.OwnerRetiring -= onRetiring;
            Assert.That(namedAtRetirement, Is.EqualTo(new[] { lower }), "named at the source's retirement, once");
            Assert.That(split.ReplacingFragment.IsSet, Is.False, "and not after it");
            Assert.That(split.EventsKept, Is.EqualTo(split.Published), "each publication is kept for the world's end");
            TestContext.Out.WriteLine(split.Describe());
            TestContext.Out.WriteLine("last refusal: " + split.LastRefusal + "; search: " + split.LastSearch.outcome + ", plane (owner) " + split.LastSearch.planeOwner
                + ", gap " + split.LastSearch.gap + ", margin " + split.LastSearch.margin + ", triangles +" + split.LastSearch.positiveTriangles + " -" + split.LastSearch.negativeTriangles);
            Assert.That(split.Published, Is.EqualTo(1), "published");
            Assert.That(split.LastSource, Is.EqualTo(lower));
            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(a.IsSet && b.IsSet && a != b, Is.True);

            // The ledger: two ordinary fragments in the source's place, and nothing of a cut.
            Assert.That(root.Ledger.TryGetFragmentState(lower, out LogicalFragmentState lowerState) && lowerState == LogicalFragmentState.Replaced, Is.True);
            Assert.That(root.Ledger.IsCurrentTarget(a) && root.Ledger.IsCurrentTarget(b), Is.True, "both are targets");
            Assert.That(root.Ledger.TryGetOrigin(a, out CutOperationId originA, out float sideA) && originA == origin && sideA == originSide, Is.True, "the source's own origin");
            Assert.That(root.Ledger.TryGetOrigin(b, out CutOperationId originB, out float sideB) && originB == origin && sideB == originSide, Is.True);
            Assert.That(root.Ledger.TryGetRoot(a, out LogicalFragmentId rootA) && rootA == body && root.Ledger.TryGetRoot(b, out LogicalFragmentId rootB) && rootB == body, Is.True,
                "the body's own family: no group of their own");
            Assert.That(root.Ledger.OperationCount, Is.EqualTo(operations), "no operation");
            Assert.That(root.Ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(incomplete), "no incomplete cut");
            Assert.That(root.Driver.Transactions.Count, Is.Zero, "no transaction");
            Assert.That(root.Ledger.LiveCutFragmentCount, Is.EqualTo(liveCut + 1), "one live cut fragment became two");
            Assert.That(LivePiecesOf(root, body), Is.EquivalentTo(new[] { upper, a, b }));
            Assert.That(root.Ledger.TryGetOperation(origin, out LogicalCutOperation made) && made.negative == lower && made.positive == upper, Is.True,
                "the cut that made the source still names it");
            Assert.That(root.Ledger.TryGetParent(a, out LogicalFragmentId parentA) && parentA == lower
                        && root.Ledger.TryGetParent(b, out LogicalFragmentId parentB) && parentB == lower, Is.True, "as objects, both came from the source");

            // The Slash that consumed the source has consumed both: neither is hit by it again; another Slash, and the side above, are as they were.
            Assert.That(consumption.IsConsumed(slash, a) && consumption.IsConsumed(slash, b), Is.True, "both of the two are consumed for the Slash that consumed the source");
            Assert.That(consumption.TryConsume(slash, a) || consumption.TryConsume(slash, b), Is.False, "so the same Slash is not passed on for either");
            Assert.That(consumption.IsConsumed(slash + 1, a) || consumption.IsConsumed(slash, upper), Is.False);

            // The physics: two owners of the source's convexes, its very meshes, its mass, where it stood, moving as it moved.
            Assert.That(root.Owners.TryGet(lower, out _), Is.False, "the source's owner is gone");
            Assert.That(lowerOwner.IsWithdrawn && lowerOwner.IsReleased, Is.True);
            Assert.That(root.Owners.Count, Is.EqualTo(owners + 1), "one owner became two");
            Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA), Is.True, "the first of the two has its owner");
            Assert.That(root.Owners.TryGet(b, out PhysicsFragmentOwner ownerB), Is.True, "and the second");
            Assert.That(ownerA.Shape.ConvexCount == 1 && ownerB.Shape.ConvexCount == 1, Is.True, "a convex each");
            Assert.That(new[] { ownerA.Shape.MeshOf(0), ownerB.Shape.MeshOf(0) }, Is.EquivalentTo(lowerMeshes), "the source's own collider meshes: nothing was cooked");
            Assert.That(ownerA.Root.GetComponentsInChildren<MeshCollider>().Length + ownerB.Root.GetComponentsInChildren<MeshCollider>().Length, Is.EqualTo(2));
            Assert.That(ownerA.Building.IsBuildingDerived || ownerB.Building.IsBuildingDerived, Is.False);
            Assert.That(ownerA.FixedByAnchors || ownerB.FixedByAnchors, Is.False, "no anchor: both free");
            Assert.That(ownerA.GeometryLocalToOwner, Is.EqualTo(lowerFrame), "the display follows each as it followed the source");
            Assert.That(ownerB.GeometryLocalToOwner, Is.EqualTo(lowerFrame));
            Assert.That(IsHitTarget(root, a) && IsHitTarget(root, b) && !IsHitTarget(root, lower), Is.True, "the hit targets are the two");

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

            Assert.That(source.velocity.magnitude, Is.GreaterThan(0.5f), "the layout: the source was moving at the publication");

            // The geometry: the same vertices, every triangle of the source in exactly one of the two, materials as they were.
            Assert.That(root.Geometry.TryGetGeometry(lower, out _) || root.Geometry.TryGetGeometryFrame(lower, out _), Is.False, "the DAG keeps nothing for the source");
            Assert.That(root.Geometry.TryGetGeometry(a, out VpStoredGeometry geometryA), Is.True, "the first of the two has its geometry");
            Assert.That(root.Geometry.TryGetGeometry(b, out VpStoredGeometry geometryB), Is.True, "and the second");
            Assert.That(root.Geometry.TryGetGeometryFrame(a, out Matrix4x4 frameA) && frameA == lowerLineageFrame, Is.True, "cut next in the source's own frame");
            Assert.That(root.Geometry.TryGetGeometryFrame(b, out Matrix4x4 frameB) && frameB == lowerLineageFrame, Is.True);
            List<(uint, uint, uint)> trianglesA = TrianglesOf(root, geometryA);
            List<(uint, uint, uint)> trianglesB = TrianglesOf(root, geometryB);
            Assert.That(trianglesA.Count > 0 && trianglesB.Count > 0, Is.True);
            Assert.That(trianglesA.Concat(trianglesB), Is.EquivalentTo(lowerTriangles), "none lost, none doubled, corner order kept");
            Assert.That(MaterialsOf(root, geometryA), Is.EqualTo(lowerMaterials), "the source's submeshes, in order, with their materials");
            Assert.That(MaterialsOf(root, geometryB), Is.EqualTo(lowerMaterials));
            Assert.That(root.Storage.VertexCount, Is.EqualTo(vertices), "no vertex was added");
            Assert.That(root.Geometry.GeometrySplits, Is.EqualTo(1));

            // The display: one transfer of exactly those indices, and the two drawn from their own registrations.
            Assert.That(root.Display.SplitCommits, Is.EqualTo(1));
            Assert.That(root.Display.SplitIndexTransfers, Is.EqualTo(indexTransfers + 1), "one index transfer");
            Assert.That(split.IndicesTransferred, Is.EqualTo(3L * lowerTriangles.Count), "of the source's indices and no more");
            Assert.That(split.ActorsAdded == 1 && split.RegistrationsAdded == 1, Is.True);
            Assert.That(root.Display.HasOwnRegistration(a) && root.Display.HasOwnRegistration(b), Is.True);
            yield return null;
            yield return null;
            Assert.That(IsDrawn(root, a) && IsDrawn(root, b) && IsDrawn(root, upper), Is.True, "both drawn, and the side above as before");
            Assert.That(IsDrawn(root, lower) || root.Display.HasOwnRegistration(lower), Is.False, "the source is drawn no more");
            Assert.That(split.CollectionsAfterPublication, Is.EqualTo(1), "the publication's own frame's collection was read, apart");
            Assert.That(split.HeldFor(SpatialSplitHold.StructureChanged), Is.GreaterThan(0), "and the turn after it was held for the structure it changed");

            // What the source held: its actor destroyed, its shape and its index range given back once nothing reads
            // them -- and the collider meshes the two inherited still there, on their colliders.
            yield return Until(
                () => lowerActor == null && lowerShape.IsFreed
                      && (!root.Storage.TryGetIndexState(lowerGeometry.indexRange, out VpIndexRangeState old, out _, out _) || old == VpIndexRangeState.Free),
                "the source's actor, shape and index range went back");
            Assert.That(root.Storage.TryGetIndexState(geometryA.indexRange, out VpIndexRangeState stateA, out _, out _) && stateA == VpIndexRangeState.Published, Is.True);
            Assert.That(root.Storage.TryGetIndexState(geometryB.indexRange, out VpIndexRangeState stateB, out _, out _) && stateB == VpIndexRangeState.Published, Is.True);
            Assert.That(lowerMeshes.All(m => m != null), Is.True, "the inherited collider meshes are alive");
            Assert.That(
                ownerA.Root.GetComponentsInChildren<MeshCollider>().Concat(ownerB.Root.GetComponentsInChildren<MeshCollider>()).Select(c => c.sharedMesh),
                Is.EquivalentTo(lowerMeshes), "and are what the two objects' colliders use");
            Assert.That(ownerA.Shape.IsFreed || ownerB.Shape.IsFreed, Is.False);
            Assert.That(PhysicsCutClassification.Live, Is.EqualTo(classifications), "the attempt's own scan went back");

            // The record.
            Assert.That(split.Issued, Is.EqualTo(1));
            Assert.That(split.WorkerSeconds, Is.GreaterThan(0.0));
            Assert.That(split.IssueSeconds > 0.0 && split.BuildSeconds > 0.0 && split.SwitchSeconds > 0.0, Is.True, "the Main times, apart");
            Assert.That(split.LastSearch.outcome, Is.EqualTo(SpatialSplitSearchOutcome.Found));
            Assert.That(split.LastSearch.positiveConvexes == 1 && split.LastSearch.negativeConvexes == 1, Is.True);
            Assert.That(split.SuppressedNow, Is.Zero);
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            TestContext.Out.WriteLine(split.Describe());
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The two are cut again as any object, and end with their group, whose original comes back.** Each of the
        /// two is cut the ordinary way -- accepted, published, committed, both sides drawn; then the body's whole
        /// group, the pieces of the split among it, is ended in one retirement and its origin is asked to return, once.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplit_TheTwoObjects_AreCutAgain_AndEndWithTheirGroup_WhoseOriginalComesBack()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            CutFragmentGroupCollection groups = GroupsOf(root, new Vector3(0f, 0f, 50f));
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            LogicalFragmentId body = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f));
            var origin = new OriginDouble { restore = new Bounds(Vector3.zero, Vector3.one) };
            groups.Register(body, origin);
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            remaining[0] = 1.0;
            yield return Until(() => split.Published == 1, "the lower side was split");
            remaining[0] = 0.0;
            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA), Is.True, "the first of the two has its owner");
            Assert.That(root.Owners.TryGet(b, out PhysicsFragmentOwner ownerB), Is.True, "and the second");
            LogicalFragmentId hand = ownerA.Mass < ownerB.Mass ? a : b;
            LogicalFragmentId trunk = hand == a ? b : a;
            yield return null;
            yield return null;

            // Each cut again the ordinary way, through its own middle.
            var handSides = new LogicalFragmentId[2];
            var trunkSides = new LogicalFragmentId[2];
            yield return CutInTwo(root, hand, new float4(1f, 0f, 0f, -(HandLowX + 0.25f)), handSides);
            yield return CutInTwo(root, trunk, new float4(1f, 0f, 0f, 0f), trunkSides);
            yield return Until(() => root.Driver.Transactions.Count == 0, "every cut's record is over");
            yield return null;
            LogicalFragmentId[] pieces = { sides[0], handSides[0], handSides[1], trunkSides[0], trunkSides[1] };
            Assert.That(pieces.All(p => IsDrawn(root, p)), Is.True, "every piece is drawn");
            Assert.That(pieces.All(p => root.Owners.TryGet(p, out PhysicsFragmentOwner o) && o.Body != null && IsHitTarget(root, p)), Is.True, "owned, and a hit target");
            Assert.That(LivePiecesOf(root, body), Is.EquivalentTo(pieces), "the body's group: the pieces of the split's two among it");
            Assert.That(root.Ledger.TryGetOrigin(handSides[0], out CutOperationId recut, out _)
                        && root.Ledger.TryGetOperation(recut, out LogicalCutOperation record) && record.source == hand, Is.True, "a cut of the split's fragment");
            Assert.That(root.GeometryFaults, Is.Zero);

            // The whole group ends at one boundary, and its original comes back once the display has let it go.
            Assert.That(groups.TryChoose(body, out CutGroupCandidate candidate), Is.True, "the group is chosen whole");
            Assert.That(groups.TryApply(in candidate, out CutGroupPass refusal), Is.True, "ended: " + refusal);
            Assert.That(groups.LastRetiredFragments, Is.EqualTo(pieces.Length), "every piece in the one retirement");
            Assert.That(origin.ended, Is.EqualTo(1));
            Assert.That(pieces.All(p => IsRetired(root, p) && !root.Owners.TryGet(p, out _) && !IsHitTarget(root, p)), Is.True, "every piece: retired, no owner, no hit shape");
            Assert.That(root.Ledger.LiveCutCountOf(body), Is.Zero);
            yield return Until(() => groups.Returned == 1, "the original stands again once a collection was adopted");
            Assert.That(root.Display.HoldsAnythingOf(body), Is.False, "the display holds nothing of the group: the split's registrations went with it");
            Assert.That(origin.returned, Is.EqualTo(1), "asked once");
            yield return Until(() => groups.Rearmed == 1, "and is a cut target again");
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A mesh that bridges the gap, a convex across it and a gap within the margin each keep the object one, and
        /// it is not tried again.** Three bodies: the search runs once for each lower side, finds no plane, and marks
        /// it; the frames after issue nothing more and every owner stands as it was. A piece cut the ordinary way is a
        /// new input and is tried once.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplit_AMeshBridge_AConvexAcross_OrAGapWithinTheMargin_KeepsTheObject_AndIsNotTriedAgain()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            float margin = CutSpatialSplit.MarginOf(root.Profile);
            Assert.That(margin, Is.GreaterThan(0f), "the layout: the profile's epsilons give a margin");
            LogicalFragmentId bridged = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f), meshBridge: true);
            LogicalFragmentId crossed = AddBodyWithAHand(root, new Vector3(0f, 0f, 12f), convexAcross: true);
            LogicalFragmentId near = AddBodyWithAHand(root, new Vector3(0f, 0f, 24f), handLowX: 1f + (1.5f * margin));
            LogicalFragmentId building = AddBodyWithAHand(root, new Vector3(0f, 0f, 36f), building: true);
            yield return null;
            var bridgedSides = new LogicalFragmentId[2];
            var crossedSides = new LogicalFragmentId[2];
            var nearSides = new LogicalFragmentId[2];
            var buildingSides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, bridged, bridgedSides);
            yield return CutAtTheWaist(root, crossed, crossedSides, lowerConvexes: 3);
            yield return CutAtTheWaist(root, near, nearSides);
            yield return CutAtTheWaist(root, building, buildingSides);
            Assert.That(root.Owners.TryGet(buildingSides[1], out PhysicsFragmentOwner buildingOwner) && buildingOwner.Building.IsBuildingDerived, Is.True,
                "the layout: a building's lower side, with a hand a plane would separate");
            Assert.That(split.Examine(buildingSides[1]), Is.EqualTo(SpatialSplitPass.Building), "building-derived: passed over in this first version");
            LogicalFragmentId[] lowers = { bridgedSides[1], crossedSides[1], nearSides[1] };
            var before = new PhysicsFragmentOwner[3];
            for (int i = 0; i < 3; i++)
            {
                Assert.That(split.Examine(lowers[i]), Is.EqualTo(SpatialSplitPass.Candidate), "a candidate by the light checks: nothing geometric is decided on Main");
                Assert.That(root.Owners.TryGet(lowers[i], out before[i]), Is.True);
            }

            int owners = root.Owners.Count;
            remaining[0] = 1.0;
            yield return Until(() => split.SuppressedNow == 3 && !split.HasAttempt, "the search ran for each and found no plane");
            TestContext.Out.WriteLine(split.Describe());
            Assert.That(split.Issued, Is.EqualTo(3), "one attempt each");
            Assert.That(split.EndedAs(SpatialSplitEnd.NotFound), Is.EqualTo(3));
            Assert.That(split.Published, Is.Zero);

            // Not again: the frames go by with the budget open, and nothing more is issued.
            for (int i = 0; i < 24; i++)
            {
                yield return null;
            }

            Assert.That(split.Issued, Is.EqualTo(3), "nothing was tried a second time");

            // And nothing is walked for nothing: the round left nothing to try, so the walk stands idle -- no fragment
            // is looked at and no turn is taken -- while nothing it reads changes.
            Assert.That(split.IsIdle, Is.True, "the round ended with every fragment passed over or tried: the walk stands idle");
            Assert.That(split.Rounds, Is.GreaterThanOrEqualTo(1));
            int examinedIdle = split.Examined, turnsIdle = split.Turns;
            long idleTurns = split.IdleTurns;
            double walkIdle = split.WalkSeconds;
            for (int i = 0; i < 12; i++)
            {
                yield return null;
            }

            Assert.That(split.Examined, Is.EqualTo(examinedIdle), "nothing was looked at while idle, the budget open");
            Assert.That(split.Turns, Is.EqualTo(turnsIdle), "and no turn was taken");
            Assert.That(split.WalkSeconds, Is.EqualTo(walkIdle), "nor any of the walk's time spent");
            Assert.That(split.IdleTurns, Is.GreaterThanOrEqualTo(idleTurns + 10), "the frames went by as idle turns");
            Assert.That(split.PassedAs(SpatialSplitPass.Suppressed), Is.Zero, "the tried ones were not gone round again");
            for (int i = 0; i < 3; i++)
            {
                Assert.That(split.Examine(lowers[i]), Is.EqualTo(SpatialSplitPass.Suppressed));
                Assert.That(root.Ledger.IsCurrentTarget(lowers[i]), Is.True, "live as it was");
                Assert.That(root.Owners.TryGet(lowers[i], out PhysicsFragmentOwner now) && ReferenceEquals(now, before[i]) && !now.IsWithdrawn, Is.True, "with the same owner");
                Assert.That(root.Display.HasOwnRegistration(lowers[i]) && IsDrawn(root, lowers[i]), Is.True, "drawn as it was");
            }

            Assert.That(root.Owners.Count, Is.EqualTo(owners));
            Assert.That(root.Ledger.SplitReplacements, Is.Zero);
            Assert.That(root.Display.SplitCommits, Is.Zero);
            Assert.That(split.PassedAs(SpatialSplitPass.Building), Is.GreaterThan(0), "the building's side was looked at and passed over");
            Assert.That(root.Owners.TryGet(buildingSides[1], out PhysicsFragmentOwner buildingNow) && ReferenceEquals(buildingNow, buildingOwner), Is.True);
            remaining[0] = 0.0;

            // Cut the ordinary way, the bridged one's pieces are other inputs: the one with the hand is tried, once.
            var pieces = new LogicalFragmentId[2];
            yield return CutInTwo(root, lowers[0], new float4(1f, 0f, 0f, 0f), pieces);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the cut's record is over");
            remaining[0] = 1.0;
            yield return Until(() => split.Issued == 4 && !split.HasAttempt, "the piece with the hand and the bridge was tried");
            for (int i = 0; i < 12; i++)
            {
                yield return null;
            }

            remaining[0] = 0.0;
            Assert.That(split.Issued, Is.EqualTo(4), "and not again");
            Assert.That(split.EndedAs(SpatialSplitEnd.NotFound), Is.EqualTo(4), "the bridge still joins them");
            Assert.That(split.PassedAs(SpatialSplitPass.Suppressed), Is.GreaterThan(0), "the cut ended the wait: the others were looked at again and passed over as tried, by their marks");
            Assert.That(split.IsIdle, Is.True, "and the walk stands idle again");
            Assert.That(split.Published, Is.Zero);
            Assert.That(split.SuppressedNow, Is.EqualTo(3), "the mark of the fragment that is gone went with it");
            Assert.That(root.Ledger.IsCurrentTarget(pieces[0]) && root.Ledger.IsCurrentTarget(pieces[1]), Is.True);
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.TerminationRequested, Is.False);
            TestContext.Out.WriteLine(split.Describe());
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **A result overtaken by a cut or by a retirement is not published, and marks nothing.** The search's work
        /// is held back at its destination while its fragment is cut the ordinary way -- the cut goes on beside the
        /// attempt -- and, for another, while its fragment is retired. When each result arrives it is given back
        /// unused: no fragment, owner or registration is made, and the input is not recorded as one that failed.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplit_AResultOvertakenByACutOrARetirement_IsNotPublished_AndMarksNothing()
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
            LogicalFragmentId first = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f));
            LogicalFragmentId second = AddBodyWithAHand(root, new Vector3(0f, 0f, 12f));
            root.Lifetime.MarkLineage(first);
            root.Lifetime.MarkLineage(second);
            yield return null;
            var firstSides = new LogicalFragmentId[2];
            var secondSides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, first, firstSides);
            yield return CutAtTheWaist(root, second, secondSides);
            int owners = root.Owners.Count;

            // An attempt, its work finished and held back from the collection.
            background.HoldEverything = true;
            remaining[0] = 1.0;
            yield return Until(() => split.HasAttempt, "an attempt was issued");
            remaining[0] = 0.0;
            LogicalFragmentId cutUnder = split.AttemptFragment;
            Assert.That(cutUnder == firstSides[1] || cutUnder == secondSides[1], Is.True, "for one of the two lower sides");
            yield return Until(() => background.HoldingCount > 0, "its work finished and is held back from the collection");

            // The fragment is cut the ordinary way while the attempt is out: the cut is not held by it.
            var pieces = new LogicalFragmentId[2];
            yield return CutInTwo(root, cutUnder, new float4(0f, 0f, 1f, 0f), pieces);
            yield return Until(() => root.Driver.Transactions.Count == 0, "the cut's record is over");
            Assert.That(root.Ledger.IsCurrentTarget(cutUnder), Is.False, "the layout: replaced by its cut");
            Assert.That(split.HasAttempt, Is.True, "the layout: the attempt is still out");
            int afterCut = root.Owners.Count;
            Assert.That(afterCut, Is.EqualTo(owners + 1));

            background.HoldEverything = false;
            background.ReleaseEverything();
            yield return Until(() => !split.HasAttempt, "the result arrived and was given back");
            Assert.That(split.EndedAs(SpatialSplitEnd.Stale), Is.EqualTo(1), "stale: what it was computed from is no longer the fragment's");
            Assert.That(split.Published, Is.Zero);
            Assert.That(split.SuppressedNow, Is.Zero, "and nothing is recorded as failed");
            Assert.That(root.Ledger.SplitReplacements, Is.Zero);
            Assert.That(root.Owners.Count, Is.EqualTo(afterCut), "no owner was made");
            Assert.That(root.Ledger.IsCurrentTarget(pieces[0]) && root.Ledger.IsCurrentTarget(pieces[1]), Is.True, "the cut's own pieces stand");
            yield return null;
            Assert.That(IsDrawn(root, pieces[0]) && IsDrawn(root, pieces[1]), Is.True);

            // Another attempt, and its fragment is retired while it is out.
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
            Assert.That(split.Published, Is.Zero);
            Assert.That(split.SuppressedNow, Is.Zero);
            Assert.That(IsRetired(root, retiredUnder), Is.True, "retired, and nothing of it came back");
            Assert.That(root.Owners.Count, Is.EqualTo(afterRetirement));
            Assert.That(root.Ledger.SplitReplacements, Is.Zero);
            yield return null;
            yield return null;
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            TestContext.Out.WriteLine(split.Describe());
            split.Settings = default;
            yield return EndWorld(root);
        }

        // One update of a wave in the plane z = at, its span along y over [yLow, yHigh], travelling along x from xFrom to xTo.
        private static SlashSweep AlongX(long slash, float at, float xFrom, float xTo, float yLow, float yHigh)
        {
            return new SlashSweep(
                slash, 0.0, false, new Plane(Vector3.forward, -at), Vector3.right, Vector3.up,
                new Vector3(xFrom, yLow, at), new Vector3(xFrom, yHigh, at),
                new Vector3(xTo, yLow, at), new Vector3(xTo, yHigh, at));
        }

        /// <summary>
        /// **Through the detector: a fragment a Slash hit without cutting it is split, and that Slash hits neither of
        /// the two.** A sweep that only touches the lower side's bottom face is a real hit the acceptance turns into a
        /// no-op: the fragment is consumed for that Slash and lives on. While the Slash is still live the fragment's
        /// owner is split in two. The two carry the origin of the fragment they replaced -- the waist cut -- so by
        /// origins alone they would lead past the consumed fragment; the detector's consumption walks the object's
        /// ancestry, and the same Slash sweeping through both hits nothing. Another Slash hits each, and what it cuts
        /// from them is not hit by the first either.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplit_AFragmentASlashHitWithoutCuttingIt_IsSplit_AndThatSlashHitsNeitherOfTheTwo()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);

            // Anchored under the trunk: the lower side stays where it was cut, so a sweep can touch its bottom face exactly.
            LogicalFragmentId body = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f), anchors: new[] { new float3(0f, -0.9f, 0f) });
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            LogicalFragmentId upper = sides[0], lower = sides[1];
            Assert.That(root.Owners.TryGet(upper, out PhysicsFragmentOwner upperOwner), Is.True);
            upperOwner.Body.isKinematic = true;   // the case's own arrangement: the side above stays out of the sweeps below
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner) && lowerOwner.Body.isKinematic, Is.True,
                "the layout: the lower side holds the anchor and stands still");
            var detector = new SlashHitDetector(root, in k_hitSettings);
            const long first = 5, second = 6;

            // The first Slash touches the lower side's bottom face, exactly: a real hit that cuts nothing.
            Evaluate(detector, Level(first, -1f, -3f, 5f), first);
            List<SlashHitConfirmed> touching = Hits(detector);
            Assert.That(touching.Count, Is.EqualTo(1), "touching the face is a hit, and of the lower side alone");
            Assert.That(touching[0].Fragment, Is.EqualTo(lower));
            Assert.That(touching[0].Acceptance, Is.EqualTo(ProvisionalCutAcceptance.EmptySide), "a no-op: one side with no support");
            Assert.That(root.Ledger.IsCurrentTarget(lower), Is.True, "nothing changed: the fragment lives on");
            Assert.That(detector.Consumption.IsConsumed(first, lower), Is.True, "and is consumed for that Slash");
            yield return null;

            // A sweep through the trunk's lower half and the hand, below the side above.
            SlashSweep Through(long slash) => AlongX(slash, 0f, -3f, 5f, -3f, -0.3f);
            Evaluate(detector, Through(first), first);
            Assert.That(detector.HitCount, Is.Zero, "the layout: the Slash does not try the fragment it consumed again, though this sweep crosses it");

            // The fragment's owner is split in two while that Slash is live.
            remaining[0] = 1.0;
            yield return Until(() => split.Published == 1 || split.EndedAs(SpatialSplitEnd.NotFound) + split.EndedAs(SpatialSplitEnd.NotBuildable) > 0,
                "the split of the lower side ended");
            remaining[0] = 0.0;
            Assert.That(split.Published, Is.EqualTo(1), "published: " + split.Describe());
            Assert.That(split.LastSource, Is.EqualTo(lower));
            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA), Is.True);
            Assert.That(root.Owners.TryGet(b, out PhysicsFragmentOwner ownerB), Is.True);
            PhysicsFragmentOwner handOwner = ownerA.Mass < ownerB.Mass ? ownerA : ownerB;
            handOwner.Body.isKinematic = true;   // the case's own arrangement: the free part stays inside the sweep
            Assert.That(root.Ledger.TryGetOrigin(a, out CutOperationId originA, out _) && root.Ledger.TryGetOperation(originA, out LogicalCutOperation waist)
                        && waist.source == body, Is.True, "the layout: by origins the two lead straight to the body, past the consumed fragment");

            // The same Slash, through both of the two: neither is hit.
            Evaluate(detector, Through(first), first);
            Assert.That(detector.HitCount, Is.Zero, "the Slash that consumed the fragment hits neither of the two that replaced it");
            Assert.That(detector.Consumption.IsConsumed(first, a) && detector.Consumption.IsConsumed(first, b), Is.True);
            Assert.That(root.Ledger.IsCurrentTarget(a) && root.Ledger.IsCurrentTarget(b), Is.True, "and both stand uncut");
            Assert.That(root.Ledger.TryGetActiveOperation(a, out _) || root.Ledger.TryGetActiveOperation(b, out _), Is.False);
            yield return null;
            Evaluate(detector, Through(first), first);
            Assert.That(detector.HitCount, Is.Zero, "nor in a later update");

            // Another Slash hits each of the two on its own.
            Evaluate(detector, Through(second), first, second);
            List<SlashHitConfirmed> other = Hits(detector);
            Assert.That(other.ConvertAll(h => h.Fragment), Is.EquivalentTo(new[] { a, b }), "another Slash hits both, and only them");
            Assert.That(other.TrueForAll(h => h.Acceptance == ProvisionalCutAcceptance.Published), Is.True, "each accepted on its own");
            yield return Until(
                () => root.Geometry.StageOf(other[0].Operation) == CutGeometryStage.Committed
                      && root.Geometry.StageOf(other[1].Operation) == CutGeometryStage.Committed,
                "both cuts committed");
            yield return Until(() => root.Driver.Transactions.Count == 0, "their records are over");

            // What the second Slash cut from the two descends from the consumed fragment as well: the first hits none of it.
            Evaluate(detector, Through(first), first, second);
            Assert.That(detector.HitCount, Is.Zero, "the first Slash hits nothing the second cut from the two");
            Assert.That(LivePiecesOf(root, body).Count, Is.EqualTo(5), "the layout: the side above and the four pieces");
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>A placed prop's input made here: the trunk and the detached hand, one submesh, anchored under the trunk.</summary>
        private static PlacedCuttableInput TrunkAndHandInput()
        {
            float3[][] boxes =
            {
                BoxCornersAt(float3.zero, new float3(1f)),
                BoxCornersAt(new float3(HandLowX + 0.25f, -0.5f, 0f), new float3(0.25f)),
            };
            var faceIndices = new List<int>();
            foreach ((int[] cycle, int _) in k_faces)
            {
                faceIndices.AddRange(cycle);
            }

            var render = new List<PlacedCuttableInput.Vertex>();
            var indices = new List<uint>();
            var topology = new List<int>();
            var hulls = new List<PlacedCuttableInput.Hull>();
            for (int box = 0; box < boxes.Length; box++)
            {
                float3[] corners = boxes[box];
                foreach ((int[] c, int _) in k_faces)
                {
                    float3 normal = math.normalize(math.cross(corners[c[1]] - corners[c[0]], corners[c[2]] - corners[c[0]]));
                    uint first = (uint)render.Count;
                    var uv = new[] { new Vector2(0.05f, 0.1f), new Vector2(0.95f, 0.1f), new Vector2(0.95f, 0.9f), new Vector2(0.05f, 0.9f) };
                    for (int k = 0; k < 4; k++)
                    {
                        render.Add(new PlacedCuttableInput.Vertex { position = (Vector3)corners[c[k]], normal = (Vector3)normal, uv = uv[k] });
                        topology.Add((k_corners.Length * box) + c[k]);
                    }

                    indices.AddRange(new[] { first, first + 1, first + 2, first, first + 2, first + 3 });
                }

                hulls.Add(new PlacedCuttableInput.Hull
                {
                    vertices = corners.Select(p => (Vector3)p).ToArray(),
                    faceOffsets = new[] { 0, 4, 8, 12, 16, 20, 24 },
                    faceIndices = faceIndices.ToArray(),
                });
            }

            return new PlacedCuttableInput
            {
                schemaVersion = 1, topologyCount = k_corners.Length * boxes.Length, name = "trunk_and_hand", source = "made by the test", sha256 = "",
                isBuilding = false, isCuttable = true,
                render = render.ToArray(), indices = indices.ToArray(), topology = topology.ToArray(), hulls = hulls.ToArray(),
                anchors = new[] { new Vector3(0f, -0.9f, 0f) },
            };
        }

        /// <summary>
        /// **A placed prop's piece is split, goes with its group, and the instance comes back.** No licensed prop has
        /// convexes apart, so the input is made here: a trunk and a detached hand, registered through the city's own
        /// registrar and cut by a Slash through the detector. The lower piece -- the rest of the trunk and the hand in
        /// one owner, fixed by the prop's anchor -- is split in two: the part with the anchor stays fixed, the hand is
        /// free, and both are pieces of the prop's own group. The city check's observation of that retirement holds for
        /// it and for no other. One of the two is cut again by a Slash; then the whole group, the split's pieces among
        /// it, is collected in one retirement, the instance stands again as it was placed, and is a cut target again.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplit_APlacedPropsPiece_IsSplit_GoesWithItsGroup_AndTheInstanceComesBack()
        {
            PlacedCuttableInput made = TrunkAndHandInput();
            var input = new TextAsset(JsonUtility.ToJson(made)) { name = "trunk_and_hand" };
            PlacedCuttableInput data = PlayableCityCuttable.ParsedInput(input);
            Assert.That(data.hulls.Length == 2 && data.indices.Length == made.indices.Length && data.anchors.Length == 1, Is.True, "the layout: the input as the registrar reads it");
            CutWorldRoot root = NewPlacedHullWorld();
            try
            {
                root.Driver.RemainingMainSeconds = () => 1.0;
                CutFragmentGroupCollection groups = GroupsOf(root, new Vector3(0f, 0f, 60f));
                var remaining = new double[1];
                CutSpatialSplit split = SplitClosed(root, remaining);
                yield return null;
                var detector = new SlashHitDetector(root, in k_hitSettings);
                var at = new Vector3(0f, -1f, 0f);
                GameObject instance = RefusalInstance("trunk and hand", at, out Renderer[] renderers, out Collider[] colliders);
                string asPlaced = States(renderers, colliders);
                PlayableCityCuttable c = PlaceRegistrar(root, input, "trunk and hand (registrar)", Vector3.zero, 0f, Vector3.one);
                c.target = instance.transform;
                c.instanceRenderers = renderers;
                c.instanceColliders = colliders;
                c.deferUntilCut = true;
                c.DetectorForTest = detector;
                yield return UntilWithin(() => c.IsCutTarget || c.Failure != null, 5f, "the cut target made");
                Assert.That(c.Failure, Is.Null);
                PlacedCuttableCandidate candidate = c.Candidate;

                // Cut at the waist by a Slash: two pieces, the lower one with the hand.
                Evaluate(detector, ThroughTheMiddle(1, data, at), 1);
                List<SlashHitConfirmed> hits = Hits(detector);
                Assert.That(hits.Count, Is.EqualTo(1));
                Assert.That(hits[0].Acceptance == ProvisionalCutAcceptance.Published || hits[0].Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "cut: " + hits[0].Acceptance);
                LogicalFragmentId first = candidate.Source;
                Assert.That(groups.IsKnown(first), Is.True, "its cut made it a group");
                yield return Until(() => root.Geometry.StageOf(hits[0].Operation) == CutGeometryStage.Committed, "the cut committed");
                yield return Until(() => root.Driver.Transactions.Count == 0, "its record is over");
                Assert.That(States(renderers, colliders), Is.EqualTo("off,off|off,off"), "the layout: the instance has left");
                List<LogicalFragmentId> pieces = LivePiecesOf(root, first);
                Assert.That(pieces.Count, Is.EqualTo(2), "the layout: two pieces");
                LogicalFragmentId lower = pieces.Single(p => root.Owners.TryGet(p, out PhysicsFragmentOwner o) && o.Shape.ConvexCount == 2);
                LogicalFragmentId upper = pieces.Single(p => p != lower);
                Assert.That(root.Owners.TryGet(upper, out PhysicsFragmentOwner upperOwner), Is.True);
                upperOwner.Body.isKinematic = true;   // the case's own arrangement: the piece above stays out of the sweep below
                Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner) && lowerOwner.FixedByAnchors, Is.True,
                    "the layout: the lower piece holds the prop's anchor");
                Assert.That(split.Examine(lower), Is.EqualTo(SpatialSplitPass.Candidate), "a prop's piece of two convexes is a candidate");
                Assert.That(split.Examine(upper), Is.EqualTo(SpatialSplitPass.FewConvexes));
                yield return null;
                Assert.That(pieces.All(p => IsDrawn(root, p)), Is.True, "the layout: both drawn");

                // The city check's observation, asked at every retirement of an owner from here on.
                var retirements = new List<(LogicalFragmentId fragment, bool splitInTwo)>();
                Action<LogicalFragmentId, PhysicsFragmentOwner> onRetiring =
                    (fragment, owner) => retirements.Add((fragment, SandboxPropSlashPlayerCheck.ObservesASplitInTwo(root, fragment)));
                root.Owners.OwnerRetiring += onRetiring;

                // The lower piece is split in two.
                remaining[0] = 1.0;
                yield return Until(() => split.Published == 1 || split.EndedAs(SpatialSplitEnd.NotFound) + split.EndedAs(SpatialSplitEnd.NotBuildable) > 0,
                    "the split of the prop's lower piece ended");
                remaining[0] = 0.0;
                TestContext.Out.WriteLine(split.Describe());
                Assert.That(split.Published, Is.EqualTo(1), "published; last refusal: " + split.LastRefusal);
                Assert.That(split.LastSource, Is.EqualTo(lower));
                LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
                Assert.That(retirements, Is.EqualTo(new[] { (lower, true) }), "the check observes the split's retirement of the piece, once, as a split");
                Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA), Is.True);
                Assert.That(root.Owners.TryGet(b, out PhysicsFragmentOwner ownerB), Is.True);
                bool handIsA = ownerA.Mass < ownerB.Mass;
                LogicalFragmentId hand = handIsA ? a : b, trunk = handIsA ? b : a;
                PhysicsFragmentOwner handOwner = handIsA ? ownerA : ownerB, trunkOwner = handIsA ? ownerB : ownerA;
                Assert.That(trunkOwner.FixedByAnchors && trunkOwner.Body.isKinematic, Is.True, "the part with the prop's anchor stays fixed");
                Assert.That(handOwner.FixedByAnchors || handOwner.Body.isKinematic, Is.False, "the hand is a free piece");
                handOwner.Body.isKinematic = true;   // the case's own arrangement: it stays where it is from here
                Assert.That(LivePiecesOf(root, first), Is.EquivalentTo(new[] { upper, a, b }), "pieces of the prop's own group");
                Assert.That(groups.IsKnown(first), Is.True);
                Assert.That(root.Ledger.TryGetRoot(a, out LogicalFragmentId rootA) && rootA == first, Is.True);
                Assert.That(IsHitTarget(root, a) && IsHitTarget(root, b) && !IsHitTarget(root, lower), Is.True);
                Assert.That(States(renderers, colliders), Is.EqualTo("off,off|off,off"), "the instance stays away");
                yield return null;
                yield return null;
                Assert.That(IsDrawn(root, a) && IsDrawn(root, b) && IsDrawn(root, upper), Is.True, "all three drawn");

                // The trunk's part is cut again by a Slash.
                Evaluate(detector, Level(2, at.y - 0.5f, at.x - 1.5f, at.x + 1.5f), 2);
                List<SlashHitConfirmed> again = Hits(detector);
                Assert.That(again.Count, Is.EqualTo(1), "one hit: the trunk's part");
                Assert.That(again[0].Fragment, Is.EqualTo(trunk));
                Assert.That(again[0].Acceptance == ProvisionalCutAcceptance.Published || again[0].Acceptance == ProvisionalCutAcceptance.Pending, Is.True, "cut again: " + again[0].Acceptance);
                yield return Until(() => root.Geometry.StageOf(again[0].Operation) == CutGeometryStage.Committed, "the cut of the split's fragment committed");
                yield return Until(() => root.Driver.Transactions.Count == 0, "its record is over");
                List<LogicalFragmentId> all = LivePiecesOf(root, first);
                Assert.That(all.Count, Is.EqualTo(4), "the piece above, the hand, and the trunk part's two");
                Assert.That(all.Contains(hand) && all.Contains(upper) && !all.Contains(trunk), Is.True);

                // The whole group ends in one retirement, the split's pieces among it; none of those retirements is a split's.
                int retirementsBefore = retirements.Count;
                groups.Settings = GroupsOn(1);
                yield return Until(() => groups.GroupsRetired == 1, "the prop's group was collected");
                groups.Settings = GroupsOff();
                Assert.That(groups.PropsRetired, Is.EqualTo(1));
                Assert.That(groups.LastRetiredFragments, Is.EqualTo(4), "every piece in the one retirement");
                Assert.That(all.All(p => IsRetired(root, p) && !root.Owners.TryGet(p, out _) && !IsHitTarget(root, p)), Is.True, "every piece: retired, no owner, no hit shape");
                Assert.That(retirements.Count, Is.GreaterThan(retirementsBefore), "the layout: the collection's retirements were notified");
                Assert.That(retirements.Count(r => r.splitInTwo), Is.EqualTo(1), "and none of them is observed as a split: only the one that was");

                // Back as it stood, and a cut target again.
                yield return Until(() => groups.Returned == 1, "the instance stands again");
                Assert.That(root.Display.HoldsAnythingOf(first), Is.False, "the display holds nothing of its pieces, the split's among them");
                Assert.That(States(renderers, colliders), Is.EqualTo(asPlaced), "its renderers and colliders as placed");
                yield return Until(() => groups.Rearmed == 1, "a cut target again");
                Assert.That(candidate.Source.IsSet, Is.False, "no fragment until its next hit");
                Assert.That(candidate.IsHitTarget && detector.HasPlaced(candidate), Is.True, "a candidate of the detector again");
                Assert.That(groups.IsKnown(first), Is.False);
                Assert.That(root.Ledger.LiveCutFragmentCount, Is.Zero);
                root.Owners.OwnerRetiring -= onRetiring;
                Assert.That(root.GeometryFaults, Is.Zero);
                Assert.That(root.Display.IsHalted, Is.False);
                Assert.That(root.TerminationRequested, Is.False);
                TestContext.Out.WriteLine(groups.Describe());
                split.Settings = default;
                yield return EndWorld(root);
            }
            finally
            {
                TrackPlacedRegistrars();
                DisposeCandidates();
            }
        }

        /// <summary>
        /// **The walk itself and the simulation still to come are asked of the budget.** With three bodies no cut made
        /// -- nothing to issue for, so a turn that walks is a walk alone. The step this frame's decision is still to
        /// take is not the turn's to spend: a budget that would do without it holds the turn with it. A budget that
        /// runs out after the first look stops the walk there, and the next turn goes on from where it stopped; and
        /// the walk's own cost -- the round's start, a look -- is measured and becomes what the next turn is expected
        /// to cost, though no candidate was found.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplit_TheWalkItself_AndTheStepStillToCome_AreAskedOfTheBudget()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            AddBody(root, new Vector3(0f, 0f, 0f));
            AddBody(root, new Vector3(8f, 0f, 0f));
            AddBody(root, new Vector3(16f, 0f, 0f));
            for (int i = 0; i < 4; i++)
            {
                yield return null;
            }

            Assert.That(split.Examined, Is.Zero, "the layout: the product's own turns were held for the budget");
            Assert.That(split.ExpectedExamineSeconds, Is.EqualTo(CutSpatialSplit.SeedExamineSeconds), "the layout: nothing of the walk is measured yet");
            Assert.That(split.ExpectedRoundSeconds, Is.EqualTo(CutSpatialSplit.SeedRoundSeconds));
            Assert.That(
                split.ExpectedChooseSeconds,
                Is.EqualTo(CutSpatialSplit.SeedRoundSeconds + CutSpatialSplit.SeedExamineSeconds + split.ExpectedIssueSeconds).Within(1e-12),
                "a turn is expected to cost a round's start, one look and an issue");

            // A turn of the case's own, in the update phase, with the budget it gives; the product's own turns stay closed.
            SpatialSplitHold Turn(Func<double> budget)
            {
                split.RemainingMainSeconds = budget;
                split.Step();
                split.RemainingMainSeconds = () => remaining[0];
                return split.LastHold;
            }

            // The simulation still to come: the same budget that would do without it does not do with it.
            const double later = 0.004;
            split.LaterStepSeconds = () => later;
            bool heldForTheStep = false;
            for (int attempt = 0; attempt < 8 && !heldForTheStep; attempt++)
            {
                SpatialSplitHold hold = Turn(() => CutWorldRoot.CompactionReserveSeconds + (2.0 * split.ExpectedChooseSeconds) + later - 1.0e-5);
                Assert.That(hold, Is.Not.EqualTo(SpatialSplitHold.None), "the reserve and twice the turn's cost are there, the step's room is not");
                heldForTheStep = hold == SpatialSplitHold.BudgetShort;
                yield return null;
            }

            Assert.That(heldForTheStep, Is.True, "a turn was held for the budget with the step allowed for");
            Assert.That(split.Examined, Is.Zero, "and looked at nothing");

            // A budget that runs out after the first look: the walk stops there.
            int asked = 0;
            bool walked = false;
            for (int attempt = 0; attempt < 8 && !walked; attempt++)
            {
                asked = 0;
                walked = Turn(() => asked++ == 0 ? 1.0 : 0.0) == SpatialSplitHold.None;
                yield return null;
            }

            Assert.That(walked, Is.True, "a turn walked");
            Assert.That(split.Examined, Is.EqualTo(1), "one fragment was looked at, and the budget was asked before the next");
            Assert.That(split.WalksCutShort, Is.EqualTo(1));
            Assert.That(split.PassedAs(SpatialSplitPass.NotCutMade), Is.EqualTo(1));
            Assert.That(split.Issued, Is.Zero);

            // The walk was measured and learned, with no candidate found: the round's start and the look.
            Assert.That(split.WalkSeconds, Is.GreaterThan(0.0));
            Assert.That(split.RoundSeconds, Is.GreaterThan(0.0).And.LessThanOrEqualTo(split.WalkSeconds));
            Assert.That(split.ExpectedRoundSeconds, Is.EqualTo(split.RoundSeconds), "the one round's start measured is what the next is expected to cost");
            Assert.That(split.ExpectedExamineSeconds, Is.EqualTo(split.WalkSeconds - split.RoundSeconds).Within(1e-9), "and the one look");
            Assert.That(split.ExpectedChooseSeconds, Is.EqualTo(split.ExpectedExamineSeconds + split.ExpectedIssueSeconds).Within(1e-12),
                "no round is due now: a look and an issue");

            // The next turn goes on from where the walk stopped, and ends the round.
            split.LaterStepSeconds = () => 0.0;
            bool ended = false;
            for (int attempt = 0; attempt < 8 && !ended; attempt++)
            {
                ended = Turn(() => 1.0) == SpatialSplitHold.None;
                yield return null;
            }

            Assert.That(ended, Is.True);
            Assert.That(split.Examined, Is.EqualTo(3), "the other two, and no fragment twice");
            Assert.That(split.Rounds, Is.EqualTo(1));
            Assert.That(split.WalksCutShort, Is.EqualTo(1), "the budget held: the walk was not stopped again");
            Assert.That(split.PassedAs(SpatialSplitPass.NotCutMade), Is.EqualTo(3));
            Assert.That(split.Issued + split.Published, Is.Zero);

            // The round ended with nothing to try: the walk stands idle. Turns with the whole budget look at nothing,
            // are not counted as turns taken and spend none of the walk's time.
            Assert.That(split.IsIdle, Is.True, "the round left nothing to try");
            Assert.That(split.IdleWaits, Is.EqualTo(1));
            int turnsBefore = split.Turns;
            long idleBefore = split.IdleTurns;
            double walkBefore = split.WalkSeconds;
            for (int i = 0; i < 6; i++)
            {
                Turn(() => 1.0);
                yield return null;
            }

            Assert.That(split.Examined, Is.EqualTo(3), "nothing was looked at while idle");
            Assert.That(split.Turns, Is.EqualTo(turnsBefore), "an idle turn is not a turn taken: nothing is asked of the frame");
            Assert.That(split.WalkSeconds, Is.EqualTo(walkBefore));
            Assert.That(split.IdleTurns, Is.GreaterThanOrEqualTo(idleBefore + 6));

            // Something an examination reads changes -- a body is added: the wait ends, and the next round looks at
            // every owner there is now.
            AddBody(root, new Vector3(24f, 0f, 0f));
            Assert.That(split.IsIdle, Is.False, "the ledger and the owners changed");
            for (int attempt = 0; attempt < 12 && split.Examined < 7; attempt++)
            {
                Turn(() => 1.0);
                yield return null;
            }

            Assert.That(split.Examined, Is.EqualTo(7), "a new round over the four owners");
            Assert.That(split.Rounds, Is.EqualTo(2));
            Assert.That(split.IsIdle, Is.True, "and idle again after it");
            Assert.That(root.TerminationRequested, Is.False);
            TestContext.Out.WriteLine(split.Describe());
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **An attempt out at the world's ending is given back, and the ending finishes.** The search's work is held
        /// at its destination when the ending is asked for: nothing is freed while it is out, and once it is collected
        /// the ordinary frames finish the ending with everything the attempt held given back -- its lease, its hold
        /// on the shape, its scan.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplit_AnAttemptOutAtTheWorldsEnding_IsGivenBack_AndTheEndingFinishes()
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
            LogicalFragmentId body = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            Assert.That(root.Owners.TryGet(sides[1], out PhysicsFragmentOwner lowerOwner), Is.True);
            PhysicsOwnerShape lowerShape = lowerOwner.Shape;
            int classifications = PhysicsCutClassification.Live;

            background.HoldEverything = true;
            remaining[0] = 1.0;
            yield return Until(() => split.HasAttempt, "an attempt was issued");
            remaining[0] = 0.0;
            yield return Until(() => background.HoldingCount > 0, "its work finished and is held back from the collection");
            Assert.That(PhysicsCutClassification.Live, Is.EqualTo(classifications + 1), "the layout: the attempt holds its scan");

            Assert.That(root.Shutdown(), Is.False, "the ending cannot finish while the attempt's work is out");
            yield return null;
            yield return null;
            Assert.That(root.IsReleased, Is.False, "nothing was freed while it was still out");
            Assert.That(lowerShape.IsFreed, Is.False, "the shape it reads is still there");

            // Let it be collected: the ordinary frames finish the ending.
            yield return EndWorld(root);
            Assert.That(root.IsReleased, Is.True);
            Assert.That(root.SpatialSplit, Is.Null, "the world let its split go");
            Assert.That(split.HasAttempt, Is.False, "and the split its attempt");
            Assert.That(split.Published, Is.Zero, "nothing was published at the ending");
            Assert.That(PhysicsCutClassification.Live, Is.EqualTo(classifications), "the attempt's scan went back");
            Assert.That(lowerShape.IsFreed, Is.True, "and its hold on the shape, which the world's release then freed");
        }

        /// <summary>
        /// **Off, short of budget or short of room, nothing is published and nothing is done in its place; a later
        /// frame publishes.** The profile's default is on, with the convex cut, at 50 triangles. With a profile
        /// that switches it off -- this world's -- no turn is taken. On with the budget closed,
        /// nothing is looked at. An attempt issued and collected is not published while the budget is closed, nor
        /// while the storage has no index room for the two ranges -- that one is given back and marks nothing -- and
        /// the object stands as it was through all of it. With the room back and the budget open it is published.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplit_Off_ShortOfBudget_OrShortOfRoom_PublishesNothing_AndALaterFrameDoes()
        {
            CutSpatialSplitSettings byDefault = default;
            CutWorldRoot root = NewWorld(
                out Shader _, null, null,
                profile =>
                {
                    // The profile as it comes, read before this world's own is switched off.
                    byDefault = profile.SpatialSplit;
                    SetPrivate(profile, "spatialSplitEnabled", false);

                    // A storage small enough for the case to take all of its free index room.
                    SetPrivate(profile, "indexReserve", 2048);
                    SetPrivate(profile, "indexInitialCommit", 2048);
                    SetPrivate(profile, "gpuIndexInitialCapacity", 2048);
                });
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[] { 1.0 };
            CutSpatialSplit split = root.SpatialSplit;
            Assert.That(split, Is.Not.Null);
            Assert.That(byDefault.enabled && byDefault.cutConvexes && byDefault.minCutSideTriangles == 50, Is.True,
                "the profile's default: the split on, its convex cut on, at 50 triangles");
            Assert.That(split.Settings.enabled, Is.False, "switched off by this world's profile");
            split.RemainingMainSeconds = () => remaining[0];
            split.LaterStepSeconds = () => 0.0;
            root.EmptyOwners.Settings = EmptyOwnersOff();
            root.Groups.Settings = GroupsOff();
            LogicalFragmentId body = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f));
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            LogicalFragmentId lower = sides[1];
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner), Is.True);
            int owners = root.Owners.Count;

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

            // Off: the frames go by with the budget open.
            for (int i = 0; i < 8; i++)
            {
                yield return null;
            }

            split.Step();
            Assert.That(split.Turns + split.Examined + split.Issued, Is.Zero, "off: no turn, nothing looked at, nothing issued");
            Assert.That(split.HasAttempt, Is.False);
            Unchanged("off");

            // On, the budget closed: turns are taken and held.
            remaining[0] = 0.0;
            split.Settings = SplitOn();
            for (int i = 0; i < 6; i++)
            {
                yield return null;
            }

            Assert.That(split.Turns, Is.GreaterThan(0));
            Assert.That(split.HeldFor(SpatialSplitHold.BudgetShort), Is.GreaterThan(0), "held for the budget");
            Assert.That(split.Examined + split.Issued, Is.Zero, "nothing looked at, nothing issued");
            Unchanged("no budget");

            // Too little of it: the reserve and twice the expected cost of the turn -- a round's start, a look, an issue -- are not there.
            Assert.That(split.ExpectedChooseSeconds, Is.GreaterThan(split.ExpectedIssueSeconds), "the walk is part of what a turn is expected to cost");
            remaining[0] = CutWorldRoot.CompactionReserveSeconds + (2.0 * split.ExpectedChooseSeconds) - 1.0e-5;
            yield return null;
            yield return null;
            Assert.That(split.Issued, Is.Zero, "a budget just under the reserve and twice the expected cost issues nothing");

            // The issue alone, then closed again: the result is collected and its publication held.
            remaining[0] = 1.0;
            yield return Until(() => split.HasAttempt, "an attempt was issued once the budget allowed");
            remaining[0] = 0.0;
            Assert.That(split.Issued, Is.EqualTo(1));
            Assert.That(split.AttemptFragment, Is.EqualTo(lower));
            int heldBefore = split.HeldFor(SpatialSplitHold.BudgetShort);
            for (int i = 0; i < 12; i++)
            {
                yield return null;
            }

            Assert.That(split.HasAttempt, Is.True, "the attempt is kept until it is applied or given back");
            Assert.That(split.HeldFor(SpatialSplitHold.BudgetShort), Is.GreaterThan(heldBefore), "its publication was held for the budget");
            Unchanged("a result held for the budget");

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
            yield return Until(() => !split.HasAttempt, "the attempt was given back");
            remaining[0] = 0.0;
            Assert.That(split.EndedAs(SpatialSplitEnd.RoomShort), Is.EqualTo(1), "no room for the two index ranges");
            Assert.That(split.SuppressedNow, Is.Zero, "a shortage of room is not a failed search");
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
            Assert.That(split.LastSource, Is.EqualTo(lower));
            Assert.That(root.Owners.Count, Is.EqualTo(owners + 1));
            Assert.That(split.ExpectedIssueSeconds, Is.GreaterThan(0.0));
            yield return null;
            yield return null;
            Assert.That(IsDrawn(root, split.LastPositive) && IsDrawn(root, split.LastNegative), Is.True);
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.Display.IsHalted, Is.False);
            Assert.That(root.TerminationRequested, Is.False);
            TestContext.Out.WriteLine(split.Describe());
            split.Settings = default;
            yield return EndWorld(root);
        }

        /// <summary>
        /// **The anchors go to the side they lie on: the part that holds one stays fixed and the other is free.** A
        /// body anchored under its trunk: the lower side of its waist cut is fixed, and so is the trunk's part after
        /// the split, with the anchor in its own set; the hand has none and is a free body.
        /// </summary>
        [UnityTest]
        public IEnumerator SpatialSplit_AnAnchoredPart_StaysFixed_AndTheOtherIsFree()
        {
            CutWorldRoot root = NewWorld(out Shader _);
            root.Driver.RemainingMainSeconds = () => 1.0;
            var remaining = new double[1];
            CutSpatialSplit split = SplitClosed(root, remaining);
            var anchor = new float3(0f, -0.9f, 0f);
            LogicalFragmentId body = AddBodyWithAHand(root, new Vector3(0f, 0f, 0f), anchors: new[] { anchor });
            yield return null;
            var sides = new LogicalFragmentId[2];
            yield return CutAtTheWaist(root, body, sides);
            LogicalFragmentId lower = sides[1];
            Assert.That(root.Owners.TryGet(lower, out PhysicsFragmentOwner lowerOwner) && lowerOwner.FixedByAnchors && lowerOwner.Body.isKinematic, Is.True,
                "the layout: the lower side holds the anchor and is fixed");
            Assert.That(AnchorsOf(root, lower), Is.EquivalentTo(new[] { anchor }));
            Vector3 stoodAt = lowerOwner.Root.transform.position;
            float lowerMass = lowerOwner.Mass;

            remaining[0] = 1.0;
            yield return Until(() => split.Published == 1, "the lower side was split");
            remaining[0] = 0.0;
            LogicalFragmentId a = split.LastPositive, b = split.LastNegative;
            Assert.That(root.Owners.TryGet(a, out PhysicsFragmentOwner ownerA), Is.True, "the first of the two has its owner");
            Assert.That(root.Owners.TryGet(b, out PhysicsFragmentOwner ownerB), Is.True, "and the second");
            bool handIsA = ownerA.Mass < ownerB.Mass;
            LogicalFragmentId hand = handIsA ? a : b, trunk = handIsA ? b : a;
            PhysicsFragmentOwner handOwner = handIsA ? ownerA : ownerB, trunkOwner = handIsA ? ownerB : ownerA;
            Assert.That(AnchorsOf(root, trunk), Is.EquivalentTo(new[] { anchor }), "the anchor is the trunk part's own");
            Assert.That(AnchorsOf(root, hand), Is.Empty, "the hand has none");
            Assert.That(AnchorsOf(root, lower), Is.Empty, "the source let its set go");
            Assert.That(trunkOwner.FixedByAnchors && trunkOwner.Body.isKinematic, Is.True, "the part with the anchor stays fixed");
            Assert.That(handOwner.FixedByAnchors || handOwner.Body.isKinematic, Is.False, "the other is free");
            Assert.That(Vector3.Distance(trunkOwner.Root.transform.position, stoodAt), Is.LessThan(1e-5f), "where the source stood");
            Assert.That(trunkOwner.Mass + handOwner.Mass, Is.EqualTo(lowerMass).Within(1e-3f), "the source's mass, shared out");
            yield return null;
            yield return null;
            Assert.That(IsDrawn(root, hand) && IsDrawn(root, trunk), Is.True);
            Assert.That(root.GeometryFaults, Is.Zero);
            Assert.That(root.TerminationRequested, Is.False);
            split.Settings = default;
            yield return EndWorld(root);
        }
    }
}
