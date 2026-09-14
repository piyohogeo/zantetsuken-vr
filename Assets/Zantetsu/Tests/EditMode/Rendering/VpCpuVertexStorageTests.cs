using System;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;

namespace Zantetsu.Rendering.Tests
{
    /// <summary>
    /// Append-only CPU VP vertex storage (DESIGN 4.5.3): the view limited to committed vertices, writes into the
    /// uncommitted tail that stay invisible until committed, rejection beyond the free tail, and disposal.
    /// </summary>
    public class VpCpuVertexStorageTests
    {
        private static VpRenderVertex Vertex(float x)
        {
            return new VpRenderVertex { position = new Vector3(x, 0f, 0f), normal = Vector3.up, uv0 = new Vector2(x, 1f) };
        }

        private static void WriteTail(VpCpuVertexStorage storage, params float[] xs)
        {
            NativeArray<VpRenderVertex> tail = storage.GetUncommittedTail(xs.Length);
            for (int i = 0; i < xs.Length; i++)
            {
                tail[i] = Vertex(xs[i]);
            }
        }

        [Test]
        public void TheConstructor_CreatesAnEmptyStorageAndRejectsANegativeCapacity()
        {
            using (var storage = new VpCpuVertexStorage(8, Allocator.Persistent))
            {
                Assert.That(storage.Capacity, Is.EqualTo(8));
                Assert.That(storage.Count, Is.Zero);
                Assert.That(storage.Vertices.Length, Is.Zero);
            }

            Assert.Throws<ArgumentOutOfRangeException>(() => new VpCpuVertexStorage(-1, Allocator.Persistent));
        }

        [Test]
        public void CommittedTailWrites_AppendAfterTheEarlierVertices()
        {
            using (var storage = new VpCpuVertexStorage(8, Allocator.Persistent))
            {
                WriteTail(storage, 1f, 2f, 3f);
                Assert.That(storage.Vertices.Length, Is.Zero, "uncommitted writes are invisible");

                storage.Commit(3);
                WriteTail(storage, 4f, 5f);
                storage.Commit(2);

                Assert.That(storage.Count, Is.EqualTo(5));
                Assert.That(storage.Vertices.ToArray(), Is.EqualTo(new[] { Vertex(1f), Vertex(2f), Vertex(3f), Vertex(4f), Vertex(5f) }));
            }
        }

        [Test]
        public void AnAbandonedTailWrite_IsOverwrittenByTheNextOne()
        {
            using (var storage = new VpCpuVertexStorage(8, Allocator.Persistent))
            {
                WriteTail(storage, 1f);
                storage.Commit(1);
                WriteTail(storage, 7f, 8f, 9f);

                WriteTail(storage, 2f, 3f);
                storage.Commit(2);

                Assert.That(storage.Vertices.ToArray(), Is.EqualTo(new[] { Vertex(1f), Vertex(2f), Vertex(3f) }));
            }
        }

        [TestCase(-1)]
        [TestCase(6)]
        public void TailsAndCommitsOutsideTheFreeTail_ThrowWithoutChangingTheCount(int count)
        {
            using (var storage = new VpCpuVertexStorage(8, Allocator.Persistent))
            {
                WriteTail(storage, 1f, 2f, 3f);
                storage.Commit(3);

                Assert.Throws<ArgumentOutOfRangeException>(() => storage.GetUncommittedTail(count), "tail");
                Assert.Throws<ArgumentOutOfRangeException>(() => storage.Commit(count), "commit");

                Assert.That(storage.Count, Is.EqualTo(3));
                Assert.That(storage.GetUncommittedTail(5).Length, Is.EqualTo(5), "the whole free tail");
            }
        }

        [Test]
        public void AfterDispose_TheViewTailAndCommitThrowAndDisposingAgainDoesNothing()
        {
            var storage = new VpCpuVertexStorage(8, Allocator.Persistent);
            storage.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _ = storage.Vertices, "view");
            Assert.Throws<ObjectDisposedException>(() => storage.GetUncommittedTail(1), "tail");
            Assert.Throws<ObjectDisposedException>(() => storage.Commit(0), "commit");
            Assert.DoesNotThrow(storage.Dispose, "dispose again");
        }
    }
}
