using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using Zantetsu.Rendering;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// The storage's vertices and indices on reserved address space with pages committed on demand (DESIGN 4.5.3,
    /// 4.5.4), with small real memory: the base never moves and nothing is copied when more is committed; room taken
    /// beyond the first commit beside a reservation another work holds is written without disturbing it; what is given
    /// back returns once; a commit or a reservation the backing refuses is told apart from room that is simply not free.
    /// Failures of the backing are made on purpose by <see cref="TestPageBacking"/>, over the product's own
    /// VirtualAlloc, never by exhausting memory.
    /// </summary>
    public unsafe class VpVirtualBackingTests
    {
        /// <summary>
        /// The product's backing with switches: a reservation refused after a number of them, a commit refused past a
        /// number of bytes per reservation, and a count of every call so that each release can be matched.
        /// </summary>
        internal sealed class TestPageBacking : IVpPageBacking
        {
            private readonly Dictionary<IntPtr, long> _committed = new Dictionary<IntPtr, long>();
            private readonly Dictionary<IntPtr, int> _released = new Dictionary<IntPtr, int>();

            public int Reserves;
            public int Commits;
            public int RefuseReservesFrom = int.MaxValue;
            public long CommitLimitBytes = long.MaxValue;
            public int ReleasedTwice;

            /// <summary>The rounding of reservations and commits: the product's 64 KiB unless a test asks for pages (4 KiB).</summary>
            public long GranularityBytes = VpWindowsPageBacking.Instance.Granularity;

            public long Granularity => GranularityBytes;

            public IReadOnlyDictionary<IntPtr, int> Released => _released;

            public bool TryReserve(long bytes, out IntPtr address, out string failure)
            {
                address = IntPtr.Zero;
                if (Reserves >= RefuseReservesFrom)
                {
                    failure = "test: reservation refused";
                    return false;
                }

                if (!VpWindowsPageBacking.Instance.TryReserve(bytes, out address, out failure))
                {
                    return false;
                }

                Reserves++;
                _committed[address] = 0;
                return true;
            }

            public bool TryCommit(IntPtr at, long bytes, out string failure)
            {
                IntPtr owner = IntPtr.Zero;
                long ownerOffset = long.MaxValue;
                foreach (IntPtr b in _committed.Keys)
                {
                    long offset = (long)at - (long)b;
                    if (offset >= 0 && offset < ownerOffset)
                    {
                        owner = b;
                        ownerOffset = offset;
                    }
                }

                if (ownerOffset + bytes > CommitLimitBytes)
                {
                    failure = "test: commit past " + CommitLimitBytes + " bytes refused";
                    return false;
                }

                if (!VpWindowsPageBacking.Instance.TryCommit(at, bytes, out failure))
                {
                    return false;
                }

                Commits++;
                _committed[owner] = Math.Max(_committed[owner], ownerOffset + bytes);
                return true;
            }

            public void Release(IntPtr address, long bytes)
            {
                _released.TryGetValue(address, out int n);
                _released[address] = n + 1;
                if (n > 0)
                {
                    ReleasedTwice++;
                    return;
                }

                VpWindowsPageBacking.Instance.Release(address, bytes);
            }
        }

        private static VpCpuGeometryStorage NewStorage(
            IVpPageBacking pages, int vertexReserve = 1 << 20, int vertexCommit = 64, int indexReserve = 1 << 22, int indexCommit = 64,
            int descriptors = 64, int blocks = 256)
        {
            return new VpCpuGeometryStorage(
                new VpCpuGeometryBacking
                {
                    pages = pages,
                    vertexReserve = vertexReserve,
                    vertexInitialCommit = vertexCommit,
                    indexReserve = indexReserve,
                    indexInitialCommit = indexCommit,
                },
                descriptors, 256, blocks, Allocator.Persistent);
        }

        // A closed box of 8 vertices and 12 triangles, one submesh, its own topology: a cut input.
        private static VpStoredGeometry AppendBox(VpCpuGeometryStorage storage, float size = 1f)
        {
            var vertices = new VpRenderVertex[8];
            for (int i = 0; i < 8; i++)
            {
                var p = new Vector3((i & 1) != 0 ? size : 0f, (i & 2) != 0 ? size : 0f, (i & 4) != 0 ? size : 0f);
                vertices[i] = V(p, (p - Vector3.one * (size / 2)).normalized);
            }

            uint[] indices =
            {
                0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5,
            };
            var topology = new[] { 0, 1, 2, 3, 4, 5, 6, 7 };
            Assert.That(
                storage.TryAppendPrepared(vertices, indices, topology, 8, new[] { new VpGeometrySubmesh(0, indices.Length, 0) }, out VpStoredGeometry geometry),
                Is.True, "the box is appended");
            return geometry;
        }

        private static VpRenderVertex V(Vector3 position, Vector3 normal)
            => new VpRenderVertex { position = position, normal = normal, uv0 = new Vector2(0.5f / 256f, 0.5f / 256f) };

        private static void* PointerOf<T>(NativeArray<T> array) where T : struct
            => NativeArrayUnsafeUtility.GetUnsafeBufferPointerWithoutChecks(array);

        [Test]
        public void CommittingBeyondTheFirstCommit_KeepsTheBaseAndWhatWasWritten()
        {
            var pages = new TestPageBacking();
            Assert.That(VpVirtualArray<int>.TryCreate(pages, 1 << 22, 1000, out VpVirtualArray<int> array, out string failure), Is.True, failure);
            try
            {
                NativeArray<int> view = array.View;
                void* before = PointerOf(view);
                for (int i = 0; i < 1000; i++)
                {
                    view[i] = i * 7;
                }

                int committedBefore = array.CommittedLength;
                Assert.That(array.TryCommitTo(3_000_000, out failure), Is.True, failure);
                Assert.That((IntPtr)PointerOf(array.View), Is.EqualTo((IntPtr)before), "the base did not move");
                Assert.That(array.CommittedLength, Is.GreaterThanOrEqualTo(3_000_000).And.GreaterThan(committedBefore));
                Assert.That(array.CommittedLength, Is.LessThanOrEqualTo(array.ReservedLength));
                for (int i = 0; i < 1000; i++)
                {
                    Assert.That(view[i], Is.EqualTo(i * 7), "what was written stays, nothing was copied");
                }

                view[2_999_999] = 42;
                Assert.That(array.View[2_999_999], Is.EqualTo(42), "the new pages are written through the same view");
                Assert.That(array.TryCommitTo(array.ReservedLength + 1, out failure), Is.False, "the reservation is the limit");
                StringAssert.Contains("limit", failure);
            }
            finally
            {
                array.Dispose();
            }

            Assert.That(pages.Released.Count, Is.EqualTo(1), "one reservation");
            Assert.That(pages.ReleasedTwice, Is.Zero, "released once");
            array.Dispose();
            Assert.That(pages.ReleasedTwice, Is.Zero, "disposing again releases nothing");
        }

        [Test]
        public void RoomBeyondTheFirstCommit_TakenBesideAnOpenReservation_LeavesThatReservationAsItWas()
        {
            var pages = new TestPageBacking();
            using (VpCpuGeometryStorage storage = NewStorage(pages))
            {
                VpStoredGeometry box = AppendBox(storage);
                int committedVertices = storage.CommittedVertexCapacity;
                int committedIndices = storage.CommittedIndexCapacity;

                // Another work's reservation within the first commit, written through its views.
                Assert.That(storage.TryReserveCutOutput(box, 16, 48, 2, 2, out VpCutOutputReservation first), Is.True);
                NativeArray<VpRenderVertex> firstVertices = first.NewVertices;
                NativeArray<uint> firstIndices = first.NewIndices;
                void* firstVertexPointer = PointerOf(firstVertices);
                void* firstIndexPointer = PointerOf(firstIndices);
                for (int i = 0; i < firstVertices.Length; i++)
                {
                    firstVertices[i] = V(new Vector3(i, 2 * i, 3 * i), Vector3.up);
                }

                for (int i = 0; i < firstIndices.Length; i++)
                {
                    firstIndices[i] = (uint)(1000 + i);
                }

                // A second, far past what is committed: pages are committed for it, and nothing of the first moves.
                Assert.That(storage.TryReserveCutOutput(box, 200_000, 900_000, 2, 2, out VpCutOutputReservation second), Is.True);
                Assert.That(storage.CommittedVertexCapacity, Is.GreaterThanOrEqualTo(second.VertexStart + second.NewVertexCapacity).And.GreaterThan(committedVertices));
                Assert.That(storage.CommittedIndexCapacity, Is.GreaterThan(committedIndices));
                Assert.That(storage.BackingFailure, Is.Null);
                NativeArray<VpRenderVertex> secondVertices = second.NewVertices;
                NativeArray<uint> secondIndices = second.NewIndices;
                secondVertices[secondVertices.Length - 1] = V(Vector3.one * 9, Vector3.up);
                secondIndices[secondIndices.Length - 1] = 77u;

                Assert.That((IntPtr)PointerOf(first.NewVertices), Is.EqualTo((IntPtr)firstVertexPointer), "the first's vertex view did not move");
                Assert.That((IntPtr)PointerOf(first.NewIndices), Is.EqualTo((IntPtr)firstIndexPointer), "nor its index view");
                for (int i = 0; i < firstVertices.Length; i++)
                {
                    Assert.That(firstVertices[i].position, Is.EqualTo(new Vector3(i, 2 * i, 3 * i)), "the first's vertices as written");
                }

                for (int i = 0; i < firstIndices.Length; i++)
                {
                    Assert.That(firstIndices[i], Is.EqualTo((uint)(1000 + i)), "the first's indices as written");
                }

                int freeWhileOpen = storage.FreeVertexRoom;
                Assert.That(storage.TryCancelCutOutput(second), Is.True);
                Assert.That(storage.TryCancelCutOutput(first), Is.True);
                Assert.That(storage.TryCancelCutOutput(first), Is.False, "a reservation goes back once");
                Assert.That(storage.FreeVertexRoom, Is.EqualTo(freeWhileOpen + 200_000 + 16), "everything taken came back");
                Assert.That(storage.LastRefusalIsTemporary, Is.False, "no refusal was temporary");
                TestContext.WriteLine(storage.DescribeRoom());
            }

            Assert.That(pages.Released.Count, Is.EqualTo(3), "vertices, topology entries and indices: three reservations");
            Assert.That(pages.ReleasedTwice, Is.Zero, "each released once");
        }

        [Test]
        public void ACommitTheBackingRefuses_RefusesTheRoom_TellsTheOwnerOnce_AndTakesNothing()
        {
            // Up to 2 MiB per reservation the backing commits; past it, it refuses.
            var pages = new TestPageBacking { CommitLimitBytes = 2 << 20 };
            var told = new List<string>();
            using (VpCpuGeometryStorage storage = NewStorage(pages))
            {
                storage.BackingFailureHandler = told.Add;
                VpStoredGeometry box = AppendBox(storage);
                int freeVertices = storage.FreeVertexRoom;
                int freeIndices = storage.FreeIndexRoom;
                int refusals = storage.RoomRefusalCount;

                // Room there is in the reservation, but pages the backing will not commit.
                Assert.That(storage.TryReserveCutOutput(box, 200_000, 64, 2, 2, out VpCutOutputReservation refused), Is.False);
                Assert.That(refused, Is.Null);
                Assert.That(told.Count, Is.EqualTo(1), "the owner is told");
                StringAssert.Contains("backing failed", told[0]);
                Assert.That(storage.BackingFailure, Is.EqualTo(told[0]));
                Assert.That(storage.RoomRefusalCount, Is.EqualTo(refusals + 1));
                Assert.That(storage.FreeVertexRoom, Is.EqualTo(freeVertices), "nothing was kept");
                Assert.That(storage.FreeIndexRoom, Is.EqualTo(freeIndices));

                // The indices likewise; the owner is not told a second time.
                Assert.That(storage.TryReserveCutOutput(box, 8, 1_000_000, 2, 2, out refused), Is.False);
                Assert.That(told.Count, Is.EqualTo(1), "told once");
                Assert.That(storage.FreeIndexRoom, Is.EqualTo(freeIndices), "the index range went back");

                // Room within what is committed is still given.
                Assert.That(storage.TryReserveCutOutput(box, 8, 24, 2, 2, out VpCutOutputReservation small), Is.True);
                Assert.That(storage.TryCancelCutOutput(small), Is.True);
            }

            Assert.That(pages.ReleasedTwice, Is.Zero);
        }

        [Test]
        public void RoomNotFreeWithinTheReservation_IsARefusal_NotABackingFailure()
        {
            var pages = new TestPageBacking();
            var told = new List<string>();
            using (VpCpuGeometryStorage storage = NewStorage(pages, vertexReserve: 4096, vertexCommit: 64, indexReserve: 16384, indexCommit: 64))
            {
                storage.BackingFailureHandler = told.Add;
                VpStoredGeometry box = AppendBox(storage);
                Assert.That(storage.TryReserveCutOutput(box, 4096, 24, 2, 2, out _), Is.False, "more vertices than the reservation holds");
                Assert.That(storage.TryReserveCutOutput(box, 8, 16384, 2, 2, out _), Is.False, "more indices than it holds");
                Assert.That(told, Is.Empty, "the absolute limit is not a backing failure");
                Assert.That(storage.BackingFailure, Is.Null);
                StringAssert.Contains("index range of 16384 refused", storage.LastRefusal);
            }
        }

        [Test]
        public void AReservationTheBackingRefuses_AtConstruction_HoldsNothing()
        {
            var pages = new TestPageBacking { RefuseReservesFrom = 2 };
            Assert.Throws<InvalidOperationException>(() => NewStorage(pages).Dispose());
            Assert.That(pages.Reserves, Is.EqualTo(2), "two reservations were made before the third was refused");
            Assert.That(pages.Released.Count, Is.EqualTo(2), "and both were given back");
            Assert.That(pages.ReleasedTwice, Is.Zero);

            var firstCommit = new TestPageBacking { CommitLimitBytes = 0 };
            Assert.Throws<InvalidOperationException>(() => NewStorage(firstCommit).Dispose(), "a first commit refused");
            Assert.That(firstCommit.Released.Count, Is.EqualTo(firstCommit.Reserves), "every reservation made was given back");
        }

        [Test]
        public void TheFixedSizeConstructor_CommitsEverythingAtOnce_AsBefore()
        {
            using (var storage = new VpCpuGeometryStorage(4096, 16384, 64, 256, 256, Allocator.Persistent))
            {
                Assert.That(storage.VertexCapacity, Is.EqualTo(4096));
                Assert.That(storage.CommittedVertexCapacity, Is.GreaterThanOrEqualTo(4096));
                Assert.That(storage.IndexCapacity, Is.EqualTo(16384));
                Assert.That(storage.CommittedIndexCapacity, Is.GreaterThanOrEqualTo(16384));
            }
        }
        [Test]
        public void AManagementAreaShort_WithNothingOfItComingBack_IsNotTemporary_AndWithItsHolderIs()
        {
            // Descriptors: all published, none returning -- not temporary. One retiring under a held read lease -- temporary.
            using (VpCpuGeometryStorage storage = NewStorage(VpWindowsPageBacking.Instance, descriptors: 3))
            {
                VpStoredGeometry first = AppendBox(storage);
                AppendBox(storage);
                AppendBox(storage);
                var vertices = new VpRenderVertex[8];
                for (int i = 0; i < 8; i++) vertices[i] = V(new Vector3(i, 0, 0), Vector3.up);
                uint[] indices = { 0, 2, 1 };
                var topology = new[] { 0, 1, 2, 3, 4, 5, 6, 7 };
                var submesh = new[] { new VpGeometrySubmesh(0, 3, 0) };
                Assert.That(storage.TryAppendPrepared(vertices, indices, topology, 8, submesh, out _), Is.False, "no descriptor");
                Assert.That(storage.LastRefusalIsTemporary, Is.False, "nothing returns a descriptor");
                StringAssert.Contains("cannot clear", storage.LastRefusal);

                Assert.That(storage.TryAcquireIndexReadLease(first.indexRange, out VpIndexReadLease lease, out _), Is.True);
                Assert.That(storage.TryRetireIndices(first.indexRange), Is.True, "retiring until its reader returns");
                Assert.That(storage.TryAppendPrepared(vertices, indices, topology, 8, submesh, out _), Is.False, "still no descriptor");
                Assert.That(storage.LastRefusalIsTemporary, Is.True, "the retiring one comes back");
                Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True);
                Assert.That(storage.TryAppendPrepared(vertices, indices, topology, 8, submesh, out _), Is.True, "and it did");
            }

            // Vertex blocks: none free, none held -- not temporary; held by an open reservation -- temporary.
            using (VpCpuGeometryStorage storage = NewStorage(VpWindowsPageBacking.Instance, blocks: 4))
            {
                VpStoredGeometry box = AppendBox(storage);
                Assert.That(storage.TryReserveCutOutput(box, 8, 24, 2, 3, out VpCutOutputReservation open), Is.True, "holding the other 3 blocks");
                Assert.That(storage.TryReserveCutOutput(box, 8, 24, 2, 1, out _), Is.False, "no block free");
                Assert.That(storage.LastRefusalIsTemporary, Is.True, "the open reservation holds blocks that come back");
                Assert.That(storage.TryReserveCutOutput(box, 8, 24, 2, 4, out _), Is.False, "more blocks than could ever be free");
                Assert.That(storage.LastRefusalIsTemporary, Is.False, "not temporary: even all of them would not do");
                Assert.That(storage.TryCancelCutOutput(open), Is.True);
                AppendBox(storage);
                AppendBox(storage);
                AppendBox(storage);
                Assert.That(storage.TryReserveCutOutput(box, 8, 24, 2, 1, out _), Is.False, "every block published");
                Assert.That(storage.LastRefusalIsTemporary, Is.False, "published blocks are not returned");
            }
        }
    }

    /// <summary>
    /// The GPU copy's growth by itself (DESIGN 4.5.4): a short buffer is replaced by one of at least twice its
    /// capacity, never past its limit; a need past the limit changes nothing and says why; a replaced buffer is
    /// released only once the readback requested at its replacement has completed, exactly once, and the current ones
    /// at the end.
    /// </summary>
    public class VpGpuIndexedGeometryBuffersGrowthTests
    {
        [Test]
        public void AShortBuffer_IsReplacedByALargerOne_WithinItsLimit_AndTheOldOneIsReleasedOnceTheGpuIsPastIt()
        {
            var buffers = new VpGpuIndexedGeometryBuffers(16, 32, 1000, 64);
            try
            {
                GraphicsBuffer oldVertices = buffers.VertexBuffer;
                GraphicsBuffer oldIndices = buffers.IndexBuffer;
                Assert.That(buffers.TryGrow(10, 20, out bool vertexGrew, out bool indexGrew, out string failure), Is.True);
                Assert.That(vertexGrew || indexGrew, Is.False, "room there already is: nothing replaced");

                Assert.That(buffers.TryGrow(17, 20, out vertexGrew, out indexGrew, out failure), Is.True, failure);
                Assert.That(vertexGrew, Is.True);
                Assert.That(indexGrew, Is.False);
                Assert.That(buffers.VertexCapacity, Is.EqualTo(32), "twice the old capacity");
                Assert.That(buffers.VertexBuffer, Is.Not.SameAs(oldVertices), "a new buffer");
                Assert.That(buffers.IndexBuffer, Is.SameAs(oldIndices), "the index buffer was not touched");
                Assert.That(buffers.RetiredCount, Is.EqualTo(1), "the replaced one is kept");
                Assert.That(oldVertices.IsValid(), Is.True, "and not released at the switch");

                Assert.That(buffers.TryGrow(40, 65, out vertexGrew, out indexGrew, out failure), Is.False, "past the index limit");
                StringAssert.Contains("limit", failure);
                Assert.That(buffers.VertexCapacity, Is.EqualTo(32), "and nothing changed");

                Assert.That(buffers.TryGrow(500, 64, out vertexGrew, out indexGrew, out failure), Is.True, failure);
                Assert.That(buffers.VertexCapacity, Is.EqualTo(500), "what is needed, when that is more than twice");
                Assert.That(buffers.IndexCapacity, Is.EqualTo(64), "twice, up to the limit");
                Assert.That(buffers.RetiredCount, Is.EqualTo(3));
                Assert.That(buffers.GrowthCount, Is.EqualTo(2));

                // The GPU is past them once their readbacks have completed: then, and not before, they are released.
                UnityEngine.Rendering.AsyncGPUReadback.WaitAllRequests();
                buffers.ReleaseRetired();
                Assert.That(buffers.RetiredCount, Is.Zero, "released once their readbacks completed");
                Assert.That(oldVertices.IsValid(), Is.False, "released");
                Assert.That(oldIndices.IsValid(), Is.False);
                buffers.ReleaseRetired();
            }
            finally
            {
                buffers.Dispose();
            }

            Assert.That(buffers.RetiredCount, Is.Zero, "the rest are released at the end");
        }
    }
}
