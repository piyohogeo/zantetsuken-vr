using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Zantetsu.Rendering.Tests
{
    public class VpDirectSkinInputTests
    {
        readonly List<Mesh> ownedMeshes = new List<Mesh>();
        GameObject root;
        Mesh mesh;
        SkinnedMeshRenderer renderer;
        Transform[] bones;
        static readonly int[] Topology = { 0, 1, 2, 3 };
        static readonly int[] Triangles = { 0, 2, 1, 0, 1, 3, 0, 3, 2, 1, 2, 3 };

        [SetUp] public void SetUp()
        {
            root = new GameObject("Direct16 synthetic rig");
            var go = new GameObject("renderer"); go.transform.SetParent(root.transform, false);
            renderer = go.AddComponent<SkinnedMeshRenderer>(); renderer.quality = SkinQuality.Bone4;
            bones = new Transform[4];
            for (int i = 0; i < bones.Length; i++)
            {
                bones[i] = new GameObject("bone " + i).transform; bones[i].SetParent(root.transform, false);
            }
            mesh = Own(new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up, Vector3.forward },
                normals = Enumerable.Repeat(new Vector3(.2f, .3f, -.7f).normalized, 4).ToArray(),
                uv = new[] { new Vector2(.5f/256, 255.5f/256), new Vector2(31.5f/256, 63.5f/256),
                    new Vector2(128.5f/256, 127.5f/256), new Vector2(255.5f/256, .5f/256) },
                triangles = Triangles, bindposes = Enumerable.Repeat(Matrix4x4.identity, 4).ToArray(),
                boneWeights = Enumerable.Repeat(new BoneWeight { boneIndex0 = 0, weight0 = .4f,
                    boneIndex1 = 1, weight1 = .3f, boneIndex2 = 2, weight2 = .2f, boneIndex3 = 3, weight3 = .1f }, 4).ToArray() });
            renderer.sharedMesh = mesh; renderer.bones = bones; renderer.rootBone = bones[0];
        }
        Mesh Own(Mesh value) { ownedMeshes.Add(value); return value; }
        [TearDown] public void TearDown()
        {
            Object.DestroyImmediate(root);
            foreach (var item in ownedMeshes) Object.DestroyImmediate(item);
            ownedMeshes.Clear();
        }
        VpDirectSkinInput Input()
        {
            Assert.That(VpDirectSkinInput.TryCreate(renderer, Topology, 4, out var input), Is.True);
            return input;
        }
        static VpCpuGeometryStorage Storage(int vertices = 32, int indices = 96, int descriptors = 8, int submeshes = 8, int blocks = 8)
            => new VpCpuGeometryStorage(vertices, indices, descriptors, submeshes, blocks, Allocator.Persistent);
        static uint[] ReadIndices(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            Assert.That(storage.TryAcquireIndexReadLease(geometry.indexRange, out var lease, out var view), Is.True);
            try { return view.ToArray(); }
            finally { Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True); }
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)]
        public void CurrentPoseMatchesUnityBake_AndBoundsUvTopologyIndices(int pose)
        {
            using var input = Input(); using var storage = Storage();
            var bake = Own(new Mesh());
            if (pose > 0)
            {
                renderer.transform.SetLocalPositionAndRotation(new Vector3(.2f, -.3f, .1f), Quaternion.Euler(3, 17, -9));
                for (int i = 0; i < bones.Length; i++)
                {
                    bones[i].localPosition = new Vector3(.1f*i, -.03f*i, .07f*i);
                    bones[i].localRotation = Quaternion.Euler(7*i, -11*i, 4*i);
                    if (pose == 2) bones[i].localScale = new Vector3(1 + .05f*i, 1 - .04f*i, 1 + .03f*i);
                }
            }
            renderer.BakeMesh(bake, true);
            Assert.That(input.TryAppendTo(storage, out var geometry), Is.True);
            Assert.That(geometry.cutInputAccepted && geometry.hasTopology, Is.True);
            Assert.That(VpRenderVertex.Stride, Is.EqualTo(16));
            Assert.That(storage.TryGetPublishedExtent(geometry, out int start, out int count, out var bounds), Is.True);
            Assert.That(start, Is.Zero); Assert.That(count, Is.EqualTo(4));
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            for (int i = 0; i < 4; i++)
            {
                var v = storage.Vertices[i];
                Assert.That(Vector3.Distance(v.position, bake.vertices[i]), Is.LessThan(3e-6f));
                Assert.That(Vector3.Angle(v.normal, bake.normals[i]), Is.LessThan(1.4f));
                Assert.That(v.uv0, Is.EqualTo(mesh.uv[i]));
                min = Vector3.Min(min, v.position); max = Vector3.Max(max, v.position);
            }
            Assert.That(Vector3.Distance(bounds.min, min), Is.LessThan(2e-7f));
            Assert.That(Vector3.Distance(bounds.max, max), Is.LessThan(2e-7f));
            Assert.That(ReadIndices(storage, geometry), Is.EqualTo(Triangles.Select(i => (uint)i)));
            Assert.That(storage.TryGetTopology(geometry, out var topology, out int topologyCount), Is.True);
            Assert.That(topologyCount, Is.EqualTo(4));
            Assert.That(topology.ToArray(), Is.EqualTo(Topology));
        }

        [Test] public void RepeatedPosesUseNewRanges_NonzeroOffset_DifferentStorageRejected()
        {
            using var input = Input(); using var storage = Storage(); using var foreign = Storage();
            Assert.That(input.TryAppendTo(storage, out var first), Is.True);
            var old = storage.Vertices.ToArray();
            foreach (var bone in bones) bone.localPosition = new Vector3(3, 2, 1);
            Assert.That(input.TryAppendTo(storage, out var second), Is.True);
            Assert.That(second.vertexStart, Is.EqualTo(4));
            Assert.That(ReadIndices(storage, second), Is.EqualTo(Triangles.Select(i => (uint)(i + 4))));
            for (int i = 0; i < 4; i++) Assert.That(storage.Vertices[i], Is.EqualTo(old[i]));
            Assert.That(foreign.TryGetPublishedExtent(first, out _, out _, out _), Is.False);
            Assert.That(input.TryAppendTo(foreign, out var other), Is.True);
            Assert.That(other.vertexStart, Is.Zero);
            Assert.That(storage.TryGetPublishedExtent(other, out _, out _, out _), Is.False);
        }

        [TestCase("vertices")] [TestCase("indices")] [TestCase("descriptors")]
        [TestCase("submeshes")] [TestCase("blocks")]
        public void CapacityFailureDoesNotPublishOrLeakSpans(string shortage)
        {
            using var input = Input();
            using var storage = Storage(shortage == "vertices" ? 3 : 4, shortage == "indices" ? 11 : 12,
                shortage == "descriptors" ? 0 : 1, shortage == "submeshes" ? 0 : 1, shortage == "blocks" ? 0 : 1);
            for (int i = 0; i < 3; i++)
            {
                Assert.That(input.TryAppendTo(storage, out _), Is.False);
                Assert.That(storage.VertexCount, Is.Zero);
                Assert.That(storage.TryGetCommittedVertices(0, 1, out _), Is.False);
            }
        }

        [Test] public void InvalidGeneratedNormalCancelsAllSpans_ThenValidPoseFitsExactCapacity()
        {
            using var input = Input(); using var storage = Storage(4, 12, 1, 1, 1);
            foreach (var bone in bones) bone.localScale = Vector3.zero;
            for (int i = 0; i < 3; i++) Assert.That(input.TryAppendTo(storage, out _), Is.False);
            Assert.That(storage.VertexCount, Is.Zero);
            Assert.That(storage.TryGetCommittedVertices(0, 1, out _), Is.False);
            foreach (var bone in bones) bone.localScale = Vector3.one;
            Assert.That(input.TryAppendTo(storage, out var g), Is.True);
            Assert.That(g.vertexStart, Is.Zero);
            Assert.That(ReadIndices(storage, g), Is.EqualTo(Triangles.Select(i => (uint)i)));
            Assert.That(input.TryAppendTo(storage, out _), Is.False);
        }

        // Another owner's cut holds room in the same storage while this character's first cut is appended.
        [TestCase(true)] [TestCase(false)]
        public void OpenCutReservationLeavesDirectAppendItsOwnRoom_AndEndsWithoutTouchingIt(bool commitA)
        {
            using var input = Input(); using var storage = Storage();
            Assert.That(input.TryAppendTo(storage, out var parent), Is.True);
            Assert.That(storage.TryReserveCutOutput(parent, 4, 12, 2, 2, out var a), Is.True);
            Assert.That(storage.TryGetIndexState(a.indexRange, out _, out int aIndexStart, out _), Is.True);
            WriteMarked(a, 4);
            foreach (var bone in bones) bone.localPosition = new Vector3(3, 2, 1);

            Assert.That(input.TryAppendTo(storage, out var b), Is.True, "B appends while A's reservation is open");
            Assert.That(a.IsClosed, Is.False, "and A's reservation stays open");
            AssertApart(b.vertexStart, b.vertexCount, a.VertexStart, a.NewVertexCapacity, "vertices");
            AssertApart(parent.vertexStart, parent.vertexCount, b.vertexStart, b.vertexCount, "vertices of the parent");
            AssertApart(b.submeshStart, b.submeshCount, a.SubmeshStart, a.SubmeshCapacity, "submeshes");
            AssertApart(b.blockStart, b.blockCount, a.VertexBlockStart, a.VertexBlockCapacity, "blocks");
            Assert.That(storage.TryGetIndexState(b.indexRange, out _, out int bIndexStart, out int bIndexCount), Is.True);
            AssertApart(bIndexStart, bIndexCount, aIndexStart, a.NewIndexCapacity, "indices");
            Assert.That(ReadIndices(storage, b), Is.EqualTo(Triangles.Select(i => (uint)(i + b.vertexStart))));
            var before = Snapshot(storage, b);

            // B is published above A's room: the high-water now covers A, and still nothing of A can be read or sent.
            Assert.That(storage.VertexCount, Is.GreaterThanOrEqualTo(a.VertexStart + a.NewVertexCapacity));
            Assert.That(storage.TryGetCommittedVertices(a.VertexStart, a.NewVertexCapacity, out _), Is.False);
            Assert.That(storage.TryGetCommittedVertices(0, storage.VertexCount, out _), Is.False);
            Assert.That(storage.TryGetIndexState(a.indexRange, out var aState, out _, out _), Is.True);
            Assert.That(aState, Is.EqualTo(VpIndexRangeState.Reserved));
            using (var gpu = new GraphicsBuffer(GraphicsBuffer.Target.Structured, storage.VertexCapacity, VpRenderVertex.Stride))
            {
                Assert.That(VpStoredGeometryTransfer.TryUploadCommittedVertices(
                    storage, gpu, a.VertexStart, a.NewVertexCapacity, out int refused), Is.False);
                Assert.That(refused, Is.Zero);
                Assert.That(VpStoredGeometryTransfer.TryUploadCommittedVertices(
                    storage, gpu, 0, storage.VertexCount, out refused), Is.False);
                Assert.That(refused, Is.Zero);
                Assert.That(VpStoredGeometryTransfer.TryUploadCommittedVertices(
                    storage, gpu, b.vertexStart, b.vertexCount, out int sent), Is.True);
                Assert.That(sent, Is.EqualTo(4));
            }

            // A ends after B: committed, or given back.
            if (commitA)
            {
                Assert.That(CommitMarked(storage, a, 4, out var positive, out var negative), Is.True);
                Assert.That(storage.TryGetCommittedVertices(a.VertexStart, 4, out var aVertices), Is.True);
                for (int i = 0; i < 4; i++) Assert.That(aVertices[i].position.x, Is.EqualTo(100 + i));
                Assert.That(ReadIndices(storage, positive).Length, Is.EqualTo(3));
                Assert.That(ReadIndices(storage, negative).Length, Is.EqualTo(3));
            }
            else
            {
                Assert.That(storage.TryCancelCutOutput(a), Is.True);
                Assert.That(storage.TryCancelCutOutput(a), Is.False);
                Assert.That(storage.TryGetCommittedVertices(a.VertexStart, a.NewVertexCapacity, out _), Is.False);
            }
            Assert.That(a.IsClosed, Is.True);
            Assert.That(Snapshot(storage, b), Is.EqualTo(before), "B is as it was published");
        }

        // B's own shortage or failed write, beside A's open reservation: B gives back what it took and A is untouched.
        [TestCase("vertices")] [TestCase("indices")] [TestCase("descriptors")]
        [TestCase("submeshes")] [TestCase("blocks")] [TestCase("write")]
        public void DirectAppendFailureBesideOpenReservation_GivesBackOnlyItsOwnRoom(string failure)
        {
            // Room for the parent, A's reservation (its range and the descriptor its split will use) and B, less one
            // of B's needs.
            using var input = Input();
            using var storage = Storage(failure == "vertices" ? 11 : 12, failure == "indices" ? 35 : 36,
                failure == "descriptors" ? 3 : 4, failure == "submeshes" ? 3 : 4, failure == "blocks" ? 3 : 4);
            Assert.That(input.TryAppendTo(storage, out var parent), Is.True);
            Assert.That(storage.TryReserveCutOutput(parent, 4, 12, 2, 2, out var a), Is.True);
            WriteMarked(a, 4);
            int highWater = storage.VertexCount, room = a.VertexStart + a.NewVertexCapacity;
            if (failure == "write") foreach (var bone in bones) bone.localScale = Vector3.zero;

            for (int i = 0; i < 3; i++) Assert.That(input.TryAppendTo(storage, out _), Is.False);
            Assert.That(storage.VertexCount, Is.EqualTo(highWater));
            Assert.That(storage.TryGetCommittedVertices(room, 1, out _), Is.False);
            Assert.That(a.IsClosed, Is.False, "A's reservation is still open");
            for (int i = 0; i < 4; i++) Assert.That(a.NewVertices[i].position.x, Is.EqualTo(100 + i));

            if (failure == "write")
            {
                // Everything B took came back: a valid pose takes the same room and fits it exactly beside A.
                foreach (var bone in bones) bone.localScale = Vector3.one;
                Assert.That(input.TryAppendTo(storage, out var b), Is.True);
                Assert.That(b.vertexStart, Is.EqualTo(room));
                Assert.That(ReadIndices(storage, b), Is.EqualTo(Triangles.Select(i => (uint)(i + b.vertexStart))));
            }
            Assert.That(CommitMarked(storage, a, 4, out _, out _), Is.True, "A commits as it would have alone");
            Assert.That(storage.TryGetCommittedVertices(a.VertexStart, 4, out var aVertices), Is.True);
            for (int i = 0; i < 4; i++) Assert.That(aVertices[i].position.x, Is.EqualTo(100 + i));
        }

        // The descriptors: the parent's, two per open reservation (its range and the one its split will use), and B's.
        // With one fewer, B is refused: the one a commit needs is already the reservation's. Every reservation commits.
        [TestCase(1, true)] [TestCase(1, false)] [TestCase(2, true)] [TestCase(2, false)]
        public void DirectAppendLeavesEveryOpenCommitItsDescriptor(int open, bool roomForB)
        {
            using var input = Input();
            using var storage = Storage(descriptors: 1 + 2 * open + (roomForB ? 1 : 0));
            Assert.That(input.TryAppendTo(storage, out var parent), Is.True);
            var reservations = new VpCutOutputReservation[open];
            for (int r = 0; r < open; r++)
            {
                Assert.That(storage.TryReserveCutOutput(parent, 4, 12, 2, 2, out reservations[r]), Is.True);
                WriteMarked(reservations[r], 4);
            }
            int highWater = storage.VertexCount;

            Assert.That(input.TryAppendTo(storage, out var b), Is.EqualTo(roomForB), "B takes the last descriptor only if spare");
            if (!roomForB)
            {
                Assert.That(storage.VertexCount, Is.EqualTo(highWater));
                Assert.That(input.TryAppendTo(storage, out _), Is.False, "and is refused again, taking nothing");
            }
            var before = roomForB ? Snapshot(storage, b) : null;
            for (int r = 0; r < open; r++)
            {
                Assert.That(CommitMarked(storage, reservations[r], 4, out var positive, out var negative), Is.True,
                    "reservation " + r + " commits both sides");
                Assert.That(ReadIndices(storage, positive).Length + ReadIndices(storage, negative).Length, Is.EqualTo(6));
            }
            if (roomForB) Assert.That(Snapshot(storage, b), Is.EqualTo(before));
            // Every descriptor is in use now: nothing is left for another registration.
            Assert.That(input.TryAppendTo(storage, out _), Is.False);
        }

        // A holds a reservation; B appends; B's own cut then asks for output room; A commits both sides. With four
        // descriptors B's reservation finds none and is refused, taking nothing. With six it is granted and both commit.
        [TestCase(4)] [TestCase(6)]
        public void LaterReservationAfterDirectAppendCannotTakeTheDescriptorAnOpenCommitNeeds(int descriptors)
        {
            using var input = Input(); using var storage = Storage(descriptors: descriptors);
            Assert.That(input.TryAppendTo(storage, out var parent), Is.True);
            Assert.That(storage.TryReserveCutOutput(parent, 4, 12, 2, 2, out var a), Is.True);
            WriteMarked(a, 4);
            foreach (var bone in bones) bone.localPosition = new Vector3(3, 2, 1);
            Assert.That(input.TryAppendTo(storage, out var b), Is.True, "B appends beside A's reservation");
            int highWater = storage.VertexCount, freeIndices = storage.FreeIndexRoom;

            bool granted = storage.TryReserveCutOutput(b, 4, 12, 2, 2, out var bCut);
            Assert.That(granted, Is.EqualTo(descriptors >= 6), "B's reservation is granted only if descriptors of its own remain");
            if (!granted)
            {
                Assert.That(bCut, Is.Null);
                Assert.That(storage.VertexCount, Is.EqualTo(highWater));
                Assert.That(storage.FreeIndexRoom, Is.EqualTo(freeIndices), "a refused reservation takes no index room");
            }
            else WriteMarked(bCut, 4);

            Assert.That(CommitMarked(storage, a, 4, out var aPositive, out var aNegative), Is.True, "A commits both sides");
            Assert.That(ReadIndices(storage, aPositive).Length + ReadIndices(storage, aNegative).Length, Is.EqualTo(6));
            Assert.That(ReadIndices(storage, b), Is.EqualTo(Triangles.Select(i => (uint)(i + b.vertexStart))));
            if (granted)
            {
                Assert.That(CommitMarked(storage, bCut, 4, out var bPositive, out var bNegative), Is.True, "B's cut commits too");
                Assert.That(ReadIndices(storage, bPositive).Length + ReadIndices(storage, bNegative).Length, Is.EqualTo(6));
            }
        }

        // The held descriptor goes back when it is not used: a one-sided commit, and a cancel. Three descriptors: the
        // parent's and the reservation's two; the append afterwards needs the one given back.
        [TestCase(true)] [TestCase(false)]
        public void HeldSplitDescriptorGoesBackWhenNotUsed(bool commitOneSide)
        {
            using var input = Input(); using var storage = Storage(descriptors: 3);
            Assert.That(input.TryAppendTo(storage, out var parent), Is.True);
            Assert.That(storage.TryReserveCutOutput(parent, 4, 12, 2, 2, out var a), Is.True);
            Assert.That(input.TryAppendTo(storage, out _), Is.False, "every descriptor is held");
            WriteMarked(a, 4);
            if (commitOneSide)
            {
                uint at = a.NewVertexBase;
                Assert.That(storage.TryCommitCutOutput(a, 4, a.Parent.topologyVertexCount, 6, 0,
                    new[] { new VpGeometrySubmesh(0, 6, 0) }, 1, 0, new VpGeometryBounds[1], at, at, 1, 0,
                    out var positive, out var negative), Is.True);
                Assert.That(ReadIndices(storage, positive).Length, Is.EqualTo(6));
                Assert.That(negative.indexRange, Is.EqualTo(default(VpIndexRangeHandle)));
            }
            else Assert.That(storage.TryCancelCutOutput(a), Is.True);
            Assert.That(input.TryAppendTo(storage, out var next), Is.True, "the held descriptor came back");
            Assert.That(ReadIndices(storage, next).Length, Is.EqualTo(12));
        }

        static void AssertApart(int start, int count, int otherStart, int otherCount, string what)
        {
            Assert.That(start + count <= otherStart || otherStart + otherCount <= start, Is.True,
                what + ": [" + start + ", " + (start + count) + ") and [" + otherStart + ", " + (otherStart + otherCount) + ")");
        }
        static void WriteMarked(VpCutOutputReservation reservation, int vertices)
        {
            var newVertices = reservation.NewVertices; var newTopology = reservation.NewVertexTopology;
            for (int v = 0; v < vertices; v++)
            {
                newVertices[v] = new VpRenderVertex { position = new Vector3(100 + v, 0, 0) };
                newTopology[v] = 0;
            }
            var newIndices = reservation.NewIndices;
            for (int i = 0; i < 6; i++) newIndices[i] = reservation.NewVertexBase;
        }
        static bool CommitMarked(VpCpuGeometryStorage storage, VpCutOutputReservation reservation, int vertices,
            out VpStoredGeometry positive, out VpStoredGeometry negative)
        {
            uint at = reservation.NewVertexBase;
            return storage.TryCommitCutOutput(reservation, vertices, reservation.Parent.topologyVertexCount, 3, 3,
                new[] { new VpGeometrySubmesh(0, 3, 0), new VpGeometrySubmesh(0, 3, 0) }, 1, 1,
                new VpGeometryBounds[2], at, at, at, at, out positive, out negative);
        }
        static string Snapshot(VpCpuGeometryStorage storage, VpStoredGeometry geometry)
        {
            Assert.That(storage.TryGetCommittedVertices(geometry.vertexStart, geometry.vertexCount, out var vertices), Is.True);
            Assert.That(storage.TryGetTopology(geometry, out var topology, out int topologyCount), Is.True);
            Assert.That(storage.TryGetPublishedExtent(geometry, out int start, out int count, out var bounds), Is.True);
            Assert.That(storage.TryGetSubmeshes(geometry, out var submeshes), Is.True);
            Assert.That(storage.TryGetVertexBlocks(geometry, out var blocks, out _), Is.True);
            return string.Join("|", vertices.Select(v => v.position.ToString("G9") + v.normal.ToString("G9") + v.uv0.ToString("G9")))
                + "/" + string.Join(",", topology.ToArray()) + "/" + topologyCount + "/" + string.Join(",", ReadIndices(storage, geometry))
                + "/" + start + "," + count + "," + bounds.min.ToString("G9") + bounds.max.ToString("G9")
                + "/" + string.Join(",", submeshes.Select(m => m.indexOffset + ":" + m.indexCount + ":" + m.materialIndex))
                + "/" + string.Join(",", blocks.Select(k => k.vertexStart + ":" + k.vertexCount));
        }

        // Every region starts on 16 bytes, lies inside the block, and overlaps no other; the sizes are the element
        // counts'. Counts that would not fit one block, or are negative, are refused rather than wrapped.
        [TestCase(4, 12, 4)] [TestCase(1, 3, 1)] [TestCase(7, 21, 3)] [TestCase(12345, 67890, 77)] [TestCase(0, 0, 0)]
        public unsafe void LayoutRegionsAreAlignedApartAndInside(int vertices, int indexCount, int boneCount)
        {
            Assert.That(VpDirectSkinInput.TryLayout(vertices, indexCount, boneCount, out var l), Is.True);
            var regions = new (long at, long size)[]
            {
                (l.matrices, (long)boneCount * sizeof(float4x4)), (l.weights, (long)vertices * sizeof(BoneWeight)),
                (l.positions, (long)vertices * 12), (l.normals, (long)vertices * 12), (l.topology, (long)vertices * 4),
                (l.indices, (long)indexCount * 4), (l.bounds, 24), (l.valid, 4), (l.uv, (long)vertices * 2),
            };
            for (int a = 0; a < regions.Length; a++)
            {
                Assert.That(regions[a].at % 16, Is.Zero, "region " + a + " aligned");
                Assert.That(regions[a].at + regions[a].size, Is.LessThanOrEqualTo(l.bytes), "region " + a + " inside");
                for (int b = a + 1; b < regions.Length; b++)
                    Assert.That(regions[a].at + regions[a].size <= regions[b].at || regions[b].at + regions[b].size <= regions[a].at,
                        Is.True, "regions " + a + " and " + b + " apart");
            }
            Assert.That(l.bytes, Is.LessThan(regions.Sum(r => r.size) + 16 * regions.Length), "only alignment padding added");
        }

        [TestCase(int.MaxValue, 0, 0)] [TestCase(0, int.MaxValue, 0)] [TestCase(0, 0, int.MaxValue)]
        [TestCase(-1, 0, 0)] [TestCase(0, -1, 0)] [TestCase(0, 0, -1)] [TestCase(67108864, 0, 0)]
        public void LayoutRefusesCountsThatDoNotFitOneBlock(int vertices, int indexCount, int boneCount)
        {
            Assert.That(VpDirectSkinInput.TryLayout(vertices, indexCount, boneCount, out _), Is.False);
        }

        // Nothing is read that was not written: the block filled with 0x00, with 0xFF (NaN in every float), or left as
        // it comes gives the same vertices, topology, indices and bounds, before and after a pose change.
        [Test] public void BlockContentsBeforeWritingNeverReachTheOutput()
        {
            var results = new List<string>();
            try
            {
                foreach (int fill in new[] { -1, 0x00, 0xFF })
                {
                    VpDirectSkinInput.Fill = fill;
                    foreach (var bone in bones) bone.localPosition = Vector3.zero;
                    using var input = Input(); using var storage = Storage();
                    Assert.That(input.NativeBytes, Is.GreaterThan(0));
                    Assert.That(input.TryAppendTo(storage, out var first), Is.True, "fill " + fill);
                    foreach (var bone in bones) bone.localPosition = new Vector3(.3f, -.2f, .1f);
                    Assert.That(input.TryAppendTo(storage, out var second), Is.True, "fill " + fill);
                    results.Add(Snapshot(storage, first) + "#" + Snapshot(storage, second));
                }
            }
            finally { VpDirectSkinInput.Fill = -1; }
            Assert.That(results[1], Is.EqualTo(results[0]), "0x00 fill");
            Assert.That(results[2], Is.EqualTo(results[0]), "0xFF fill");
        }

        // One block for the whole input, held from creation and given back once, however often Dispose is called.
        [Test] public void OneBlockHeldUntilDispose_ThenNoneAndPublishedDataStays()
        {
            var input = Input(); using var storage = Storage();
            Assert.That(VpDirectSkinInput.TryLayout(4, 12, bones.Length, out var layout), Is.True);
            Assert.That(input.NativeBytes, Is.EqualTo(layout.bytes));
            Assert.That(input.TryAppendTo(storage, out var g), Is.True);
            var before = Snapshot(storage, g);
            input.Dispose(); Assert.That(input.NativeBytes, Is.Zero);
            input.Dispose(); Assert.That(input.NativeBytes, Is.Zero);
            Assert.That(Snapshot(storage, g), Is.EqualTo(before), "published storage outlives the input");
            Assert.Throws<ObjectDisposedException>(() => input.TryAppendTo(storage, out _));
        }

        [Test] public void DisposingInputDoesNotRetirePublishedData_AndDisposedStorageThrows()
        {
            using var input = Input(); using var storage = Storage();
            Assert.That(input.TryAppendTo(storage, out var g), Is.True);
            input.Dispose(); input.Dispose();
            Assert.That(ReadIndices(storage, g).Length, Is.EqualTo(12));
            Assert.Throws<ObjectDisposedException>(() => input.TryAppendTo(storage, out _));
            using var other = Input(); storage.Dispose();
            Assert.Throws<ObjectDisposedException>(() => other.TryAppendTo(storage, out _));
        }

        [TestCase("quality")] [TestCase("frame")] [TestCase("mesh")] [TestCase("bone")]
        public void ChangedDynamicInputIsRejectedBeforePublication(string change)
        {
            using var input = Input(); using var storage = Storage();
            if (change == "quality") renderer.quality = SkinQuality.Bone1;
            if (change == "frame") root.transform.localScale = new Vector3(1, 2, 1);
            if (change == "mesh") renderer.sharedMesh = Own(Object.Instantiate(mesh));
            if (change == "bone") Object.DestroyImmediate(bones[3].gameObject);
            Assert.That(input.TryAppendTo(storage, out _), Is.False);
            Assert.That(storage.VertexCount, Is.Zero);
        }

        [TestCase("normal")] [TestCase("uv")] [TestCase("open")] [TestCase("blendshape")]
        [TestCase("submeshes")] [TestCase("quality")] [TestCase("frame")] [TestCase("topology")]
        [TestCase("unreferenced")] [TestCase("missing-bone")] [TestCase("bindpose")] [TestCase("weights")]
        public void UnsupportedColdInputDoesNotCreateProducer(string invalid)
        {
            int[] topo = Topology;
            if (invalid == "normal") mesh.normals = Enumerable.Repeat(Vector3.zero, 4).ToArray();
            if (invalid == "uv") mesh.uv = Enumerable.Repeat(new Vector2(2, 0), 4).ToArray();
            if (invalid == "open") mesh.triangles = new[] { 0, 1, 2 };
            if (invalid == "blendshape") mesh.AddBlendShapeFrame("test", 100, new Vector3[4], new Vector3[4], new Vector3[4]);
            if (invalid == "submeshes") mesh.subMeshCount = 2;
            if (invalid == "quality") renderer.quality = SkinQuality.Bone2;
            if (invalid == "frame") renderer.transform.localScale = Vector3.one * .01f;
            if (invalid == "topology") topo = new[] { 0, 1, 2, 9 };
            if (invalid == "missing-bone") { renderer.bones = new[] { bones[0], bones[1], bones[2], null }; }
            if (invalid == "bindpose") { var b = mesh.bindposes; b[3].m00 = float.NaN; mesh.bindposes = b; }
            if (invalid == "weights") { var w = mesh.boneWeights; w[0].weight0 = .2f; mesh.boneWeights = w; }
            if (invalid == "unreferenced")
            {
                // Changing the vertex count clears Unity's attribute arrays; capture them first.
                var p = mesh.vertices; var n = mesh.normals; var u = mesh.uv; var w = mesh.boneWeights;
                mesh.vertices = p.Concat(new[] { p[0] }).ToArray();
                mesh.normals = n.Concat(new[] { n[0] }).ToArray();
                mesh.uv = u.Concat(new[] { u[0] }).ToArray();
                mesh.boneWeights = w.Concat(new[] { w[0] }).ToArray();
                mesh.triangles = Triangles;
                topo = new[] { 0, 1, 2, 3, 0 };
            }
            Assert.That(VpDirectSkinInput.TryCreate(renderer, topo, 4, out var input), Is.False);
            Assert.That(input, Is.Null); Assert.That(renderer.enabled, Is.True);
        }

        [Test] public void MoreThanFourInfluencesRefusedEvenWhenRendererQualityIsFour()
        {
            renderer.bones = bones.Concat(new[] { root.transform }).ToArray();
            // Build a variable-weight mesh directly, rather than converting legacy BoneWeight vertex channels.
            mesh = Own(new Mesh { vertices = mesh.vertices, normals = mesh.normals, uv = mesh.uv,
                triangles = Triangles, bindposes = Enumerable.Repeat(Matrix4x4.identity, 5).ToArray() });
            renderer.sharedMesh = mesh;
            using var counts = new NativeArray<byte>(new byte[] { 5, 5, 5, 5 }, Allocator.Temp);
            var values = Enumerable.Range(0, 20).Select(i => new BoneWeight1 { boneIndex = i % 5, weight = .2f }).ToArray();
            using var weights = new NativeArray<BoneWeight1>(values, Allocator.Temp);
            mesh.SetBoneWeights(counts, weights);
            Assert.That(VpDirectSkinInput.TryCreate(renderer, Topology, 4, out _), Is.False);
        }

        [Test] public void TopologyIsCopiedCold_AndSourceChangeRequiresRecreation()
        {
            var topology = (int[])Topology.Clone();
            Assert.That(VpDirectSkinInput.TryCreate(renderer, topology, 4, out var input), Is.True);
            using (input)
            using (var storage = Storage())
            {
                topology[0] = 999;
                Assert.That(input.TryAppendTo(storage, out var g), Is.True);
                Assert.That(storage.TryGetTopology(g, out var actual, out _), Is.True);
                Assert.That(actual.ToArray(), Is.EqualTo(Topology));
                input.Dispose();
                var p = mesh.vertices; p[1] = Vector3.right * 2; mesh.vertices = p;
                using var replacement = Input();
                Assert.That(replacement.TryAppendTo(storage, out var changed), Is.True);
                Assert.That(storage.Vertices[changed.vertexStart + 1].position, Is.EqualTo(p[1]));
                Assert.That(storage.Vertices[g.vertexStart + 1].position, Is.EqualTo(Vector3.right));
            }
        }

        [Test] public void RetiredIndexHandleCannotReadTheNextRegistration()
        {
            using var input = Input(); using var storage = Storage(8, 12, 1, 2, 2);
            Assert.That(input.TryAppendTo(storage, out var first), Is.True);
            Assert.That(storage.TryRetireIndices(first.indexRange), Is.True);
            Assert.That(input.TryAppendTo(storage, out var next), Is.True);
            Assert.That(storage.TryGetPublishedExtent(first, out _, out _, out _), Is.False);
            Assert.That(ReadIndices(storage, next), Is.EqualTo(Triangles.Select(i => (uint)(i + 4))));
        }

        [TestCase(false)] [TestCase(true)]
        public void AttributeSeamsRequireIdenticalPositionAndWeightArithmetic(bool mismatch)
        {
            var p = mesh.vertices; var n = mesh.normals; var u = mesh.uv; var w = mesh.boneWeights;
            mesh.vertices = Triangles.Select(i => p[i]).ToArray();
            mesh.normals = Triangles.Select(i => n[i]).ToArray(); mesh.uv = Triangles.Select(i => u[i]).ToArray();
            var seamWeights = Triangles.Select(i => w[i]).ToArray();
            if (mismatch) { seamWeights[3].weight0 = .3f; seamWeights[3].weight1 = .4f; }
            mesh.boneWeights = seamWeights; mesh.triangles = Enumerable.Range(0, 12).ToArray();
            bool accepted = VpDirectSkinInput.TryCreate(renderer, Triangles, 4, out var input);
            using (input)
            {
                Assert.That(accepted, Is.EqualTo(!mismatch));
                if (accepted)
                {
                    using var storage = Storage(); Assert.That(input.TryAppendTo(storage, out _), Is.True);
                    Assert.That(storage.Vertices[0].position, Is.EqualTo(storage.Vertices[3].position));
                }
            }
        }
    }
}
