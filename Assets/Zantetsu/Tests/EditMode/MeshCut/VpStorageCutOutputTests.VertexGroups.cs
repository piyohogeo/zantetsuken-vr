using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// DESIGN 4.5.3's vertex room by lineage (VpCpuGeometryStorage.VertexGroups.cs): a root and every cut of it are one
    /// group; the group's room is held while any range of it can still be read or a reservation on it is open, and is
    /// given back once, whole, when none can -- to be handed out again as it stands, leaving every other lineage as it
    /// was.
    /// </summary>
    public unsafe partial class VpStorageCutOutputTests
    {
        /// <summary>
        /// A root cut in two is one group. It is held while the parent, either side, or a lease on a retired side can
        /// still read it; once the last range is Free it is quiet, is marked for another look, is given back once and
        /// not twice, and the next append takes its vertex room from where the root was.
        /// </summary>
        [Test]
        public void ALineagesVertexRoom_IsHeldWhileAnyRangeCanBeRead_AndGivenBackOnceWhenNone()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(Cut(storage, subject, TiltedPlane(), out VpStorageCutResult result), Is.True, "cut");
                VpStoredGeometry positive = result.positive.geometry;
                VpStoredGeometry negative = result.negative.geometry;
                Assert.That(storage.TryGetVertexGroup(subject, out int group), Is.True, "the root opened a group");
                Assert.That(storage.TryGetVertexGroup(positive, out int positiveGroup) && positiveGroup == group, Is.True, "a side is of its root's group");
                Assert.That(storage.TryGetVertexGroup(negative, out int negativeGroup) && negativeGroup == group, Is.True);
                int freeBefore = storage.FreeVertexRoom;
                var changed = new List<int>();

                Assert.That(storage.TryRetireIndices(subject.indexRange), Is.True);
                Assert.That(storage.TryRetireIndices(positive.indexRange), Is.True);
                Assert.That(storage.IsVertexGroupQuiet(group), Is.False, "the negative side can still be read");
                Assert.That(storage.TryReleaseVertexGroup(group), Is.False);

                Assert.That(storage.TryAcquireIndexReadLease(negative.indexRange, out VpIndexReadLease lease, out _), Is.True);
                Assert.That(storage.TryRetireIndices(negative.indexRange), Is.True);
                Assert.That(storage.IsVertexGroupQuiet(group), Is.False, "a lease on the retired side still reads it");
                Assert.That(storage.TryReleaseVertexGroup(group), Is.False);
                storage.TakeChangedVertexGroups(changed);

                changed.Clear();
                Assert.That(storage.TryReleaseIndexReadLease(lease), Is.True);
                storage.TakeChangedVertexGroups(changed);
                Assert.That(changed, Does.Contain(group), "letting the last lease go marks the group");
                Assert.That(storage.IsVertexGroupQuiet(group), Is.True);

                Assert.That(storage.TryReleaseVertexGroup(group), Is.True, "given back");
                Assert.That(storage.TryReleaseVertexGroup(group), Is.False, "once");
                Assert.That(storage.VertexGroupsReleased, Is.EqualTo(1));
                Assert.That(storage.VerticesReleased, Is.EqualTo(RenderVertices + positive.vertexCount));
                Assert.That(storage.FreeVertexRoom, Is.EqualTo(freeBefore + RenderVertices + positive.vertexCount));

                VpStoredGeometry reuser = AppendOpen(storage, BuildQuad(new float3(3, 3, 3)));
                Assert.That(reuser.vertexStart, Is.EqualTo(subject.vertexStart), "the next append takes the room where the root was");
                Assert.That(storage.TryGetCommittedVertices(reuser.vertexStart, reuser.vertexCount, out NativeArray<VpRenderVertex> written), Is.True);
                Assert.That(written[0].position, Is.EqualTo(BuildQuad(new float3(3, 3, 3)).Vertices[0].position), "with its own content");
            }
        }

        /// <summary>An open reservation on a lineage holds its room, and closing it marks the group for another look.</summary>
        [Test]
        public void AnOpenReservation_HoldsItsLineagesRoom()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry subject = Append(storage, prepared);
                Assert.That(storage.TryGetVertexGroup(subject, out int group), Is.True);
                Assert.That(storage.TryReserveCutOutput(subject, 8, 12, 4, 2, out VpCutOutputReservation reservation), Is.True);
                Assert.That(storage.TryRetireIndices(subject.indexRange), Is.True);
                Assert.That(storage.IsVertexGroupQuiet(group), Is.False, "a reservation is open on it");
                Assert.That(storage.TryReleaseVertexGroup(group), Is.False);

                var changed = new List<int>();
                storage.TakeChangedVertexGroups(changed);
                changed.Clear();
                Assert.That(storage.TryCancelCutOutput(reservation), Is.True);
                storage.TakeChangedVertexGroups(changed);
                Assert.That(changed, Does.Contain(group), "closing the reservation marks the group");
                Assert.That(storage.TryReleaseVertexGroup(group), Is.True);
            }
        }

        /// <summary>
        /// Releasing one lineage leaves another as it was: its group, its ranges and its vertices, while the room given
        /// back is written over by a new append.
        /// </summary>
        [Test]
        public void ReleasingOneLineage_LeavesAnotherLineageAsItWas()
        {
            Prepared prepared = BuildPrepared();
            using (VpCpuGeometryStorage storage = NewStorage())
            {
                VpStoredGeometry gone = Append(storage, prepared);
                VpStoredGeometry kept = Append(storage, prepared);
                Assert.That(Cut(storage, kept, TiltedPlane(), out VpStorageCutResult keptCut), Is.True);
                Assert.That(storage.TryGetVertexGroup(gone, out int goneGroup) && storage.TryGetVertexGroup(kept, out int keptGroup) && goneGroup != keptGroup, Is.True);
                storage.TryGetVertexGroup(kept, out int other);
                Assert.That(storage.TryGetCommittedVertices(kept.vertexStart, kept.vertexCount, out NativeArray<VpRenderVertex> keptVertices), Is.True);
                VpRenderVertex[] before = keptVertices.ToArray();

                Assert.That(storage.TryRetireIndices(gone.indexRange), Is.True);
                Assert.That(storage.TryReleaseVertexGroup(goneGroup), Is.True);
                VpStoredGeometry reuser = AppendOpen(storage, BuildQuad(new float3(9, 9, 9)));
                Assert.That(reuser.vertexStart, Is.EqualTo(gone.vertexStart), "the room given back is taken again");

                Assert.That(storage.IsVertexGroupQuiet(other), Is.False, "the other lineage is untouched");
                Assert.That(storage.TryGetCommittedVertices(kept.vertexStart, kept.vertexCount, out NativeArray<VpRenderVertex> after), Is.True);
                Assert.That(after.ToArray(), Is.EqualTo(before), "its vertices are as they were");
                Assert.That(storage.TryGetIndexState(keptCut.positive.geometry.indexRange, out VpIndexRangeState state, out _, out _) && state == VpIndexRangeState.Published, Is.True);
                Assert.That(storage.TryGetVertexBlocks(keptCut.positive.geometry, out _, out _), Is.True, "and its metadata reads");
                Assert.That(storage.TryReleaseVertexGroup(12345), Is.False, "a group not held is not released");
            }
        }
    }
}
