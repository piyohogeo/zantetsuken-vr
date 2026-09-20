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

        /// <summary>Writes into the span from <paramref name="start"/>, as the holder of that span would.</summary>
        private static void WriteSpan(VpCpuVertexStorage storage, int start, params float[] xs)
        {
            NativeArray<VpRenderVertex> span = storage.GetSpan(start, xs.Length);
            for (int i = 0; i < xs.Length; i++)
            {
                span[i] = Vertex(xs[i]);
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
        public void PublishedSpans_AreVisibleWhereTheyWereWritten()
        {
            using (var storage = new VpCpuVertexStorage(8, Allocator.Persistent))
            {
                WriteSpan(storage, 0, 1f, 2f, 3f);
                Assert.That(storage.Vertices.Length, Is.Zero, "an unpublished span is invisible");

                storage.Publish(0, 3);
                WriteSpan(storage, 3, 4f, 5f);
                storage.Publish(3, 2);

                Assert.That(storage.Count, Is.EqualTo(5));
                Assert.That(storage.Vertices.ToArray(), Is.EqualTo(new[] { Vertex(1f), Vertex(2f), Vertex(3f), Vertex(4f), Vertex(5f) }));
            }
        }

        /// <summary>
        /// Two spans open at once, published in the order they were finished rather than the order they were taken.
        /// Each is visible where it was written, and the slots of the one still open lie below the count without
        /// belonging to anything published.
        /// </summary>
        [Test]
        public void SpansPublishedOutOfOrder_AreEachVisibleWhereTheyWere()
        {
            using (var storage = new VpCpuVertexStorage(8, Allocator.Persistent))
            {
                WriteSpan(storage, 0, 1f, 2f);
                WriteSpan(storage, 2, 3f, 4f);

                storage.Publish(2, 2);
                Assert.That(storage.Count, Is.EqualTo(4), "publishing the later span reaches past the earlier one");
                Assert.That(
                    storage.Vertices.ToArray(),
                    Is.EqualTo(new[] { Vertex(1f), Vertex(2f), Vertex(3f), Vertex(4f) }),
                    "and the earlier span's slots hold what was written into them, published or not");

                storage.Publish(0, 2);
                Assert.That(storage.Count, Is.EqualTo(4), "publishing what lies behind does not move the count back");
            }
        }

        [Test]
        public void AnAbandonedSpanWrite_IsOverwrittenByWhoeverTakesItNext()
        {
            using (var storage = new VpCpuVertexStorage(8, Allocator.Persistent))
            {
                WriteSpan(storage, 0, 1f);
                storage.Publish(0, 1);
                WriteSpan(storage, 1, 7f, 8f, 9f);

                WriteSpan(storage, 1, 2f, 3f);
                storage.Publish(1, 2);

                Assert.That(storage.Vertices.ToArray(), Is.EqualTo(new[] { Vertex(1f), Vertex(2f), Vertex(3f) }));
            }
        }

        [TestCase(-1)]
        [TestCase(9)]
        public void SpansOutsideTheCapacity_ThrowWithoutChangingTheCount(int count)
        {
            using (var storage = new VpCpuVertexStorage(8, Allocator.Persistent))
            {
                WriteSpan(storage, 0, 1f, 2f, 3f);
                storage.Publish(0, 3);

                Assert.Throws<ArgumentOutOfRangeException>(() => storage.GetSpan(0, count), "span");
                Assert.Throws<ArgumentOutOfRangeException>(() => storage.Publish(0, count), "publish");
                Assert.Throws<ArgumentOutOfRangeException>(() => storage.GetSpan(6, 3), "a span past the end");

                Assert.That(storage.Count, Is.EqualTo(3));
                Assert.That(storage.GetSpan(3, 5).Length, Is.EqualTo(5), "the rest of the capacity is still a span");
            }
        }

        [Test]
        public void AfterDispose_TheViewTailAndCommitThrowAndDisposingAgainDoesNothing()
        {
            var storage = new VpCpuVertexStorage(8, Allocator.Persistent);
            storage.Dispose();

            Assert.Throws<ObjectDisposedException>(() => _ = storage.Vertices, "view");
            Assert.Throws<ObjectDisposedException>(() => storage.GetSpan(0, 1), "span");
            Assert.Throws<ObjectDisposedException>(() => storage.Publish(0, 0), "publish");
            Assert.DoesNotThrow(storage.Dispose, "dispose again");
        }
    }
}
