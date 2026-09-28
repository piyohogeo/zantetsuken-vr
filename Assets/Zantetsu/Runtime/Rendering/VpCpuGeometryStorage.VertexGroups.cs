using System;
using System.Collections.Generic;

namespace Zantetsu.Rendering
{
    /// <summary>
    /// DESIGN 4.5.3's reclamation of published vertex room, by lineage. Every root append (a prepared, cuttable, mesh or
    /// direct skin geometry with vertices) opens a vertex group, and every cut of a geometry of that group adds what it
    /// kept to the same group: a cut's children name their parent's blocks, so the first block of any geometry is its
    /// root's, and that block's start is the group's key. The group holds its vertex, submesh and vertex block spans
    /// together, the index ranges its geometries published, and a count of the cut reservations open on it.
    /// <para>
    /// **Quiet.** A group is quiet when no reservation of it is open and every index range it published is Free:
    /// retired and no longer leased. Then nothing can read what it holds -- a draw holds a geometry reference and so a
    /// Published range, a cut's work holds a read lease on its input's range, a transfer is made only for a live
    /// geometry -- and nothing can add to it, since a cut needs a Published input. Its metadata is refused too, because a
    /// geometry's blocks and submeshes are readable only while its range is Published or Retiring.
    /// </para>
    /// <para>
    /// **Released once.** <see cref="TryReleaseVertexGroup"/> gives a quiet group's spans back to their allocators in
    /// one step, and forgets the group; the room is handed out again by the next reservation or append, as it stands
    /// and without being cleared. Whether the group's lineage has anything live left is the owner's to decide
    /// (DESIGN 4.5.3 keeps a lineage's room until its last logical fragment is gone); this storage only says what is
    /// quiet, and marks a group for another look when one of its ranges is retired or a lease on one is let go, or when
    /// a reservation on it closes. Nothing here waits for the GPU or touches a GPU buffer, and committed pages and GPU
    /// capacity are kept.
    /// </para>
    /// </summary>
    public sealed partial class VpCpuGeometryStorage
    {
        private struct GroupAllocation
        {
            public int vertexStart, vertexCount, submeshStart, submeshCount, blockStart, blockCount;
        }

        private sealed class VertexGroup
        {
            public readonly List<GroupAllocation> allocations = new List<GroupAllocation>(2);
            public readonly List<VpIndexRangeHandle> ranges = new List<VpIndexRangeHandle>(2);
            public int openReservations;
        }

        private readonly Dictionary<int, VertexGroup> _vertexGroups = new Dictionary<int, VertexGroup>();
        private readonly HashSet<int> _changedVertexGroups = new HashSet<int>();

        /// <summary>Vertex groups this storage holds: lineages whose room has not been given back.</summary>
        public int VertexGroupCount => _vertexGroups.Count;

        /// <summary>Vertex groups given back, and the vertex slots they returned, since this storage was made.</summary>
        public int VertexGroupsReleased { get; private set; }

        public long VerticesReleased { get; private set; }

        /// <summary>
        /// The vertex group <paramref name="geometry"/> belongs to: its root's, named by the start of its first block.
        /// False for a geometry with no block, or whose root opened no group (one with no vertices).
        /// </summary>
        public bool TryGetVertexGroup(VpStoredGeometry geometry, out int group)
        {
            ThrowIfDisposed();
            return TryGroupOf(geometry, out group);
        }

        /// <summary>Moves the groups marked for another look since the last call into <paramref name="into"/>.</summary>
        public void TakeChangedVertexGroups(List<int> into)
        {
            ThrowIfDisposed();
            foreach (int group in _changedVertexGroups)
            {
                into.Add(group);
            }

            _changedVertexGroups.Clear();
        }

        /// <summary>Whether <paramref name="group"/> is held here and quiet (see the class notes). Reads only.</summary>
        public bool IsVertexGroupQuiet(int group)
        {
            ThrowIfDisposed();
            return _vertexGroups.TryGetValue(group, out VertexGroup held) && IsQuiet(held);
        }

        /// <summary>
        /// Gives a quiet group's vertex, submesh and vertex block spans back, once, and forgets the group. False, giving
        /// back nothing, for a group that is not held here or is not quiet.
        /// </summary>
        public bool TryReleaseVertexGroup(int group)
        {
            ThrowIfDisposed();
            if (!_vertexGroups.TryGetValue(group, out VertexGroup held) || !IsQuiet(held))
            {
                return false;
            }

            _vertexGroups.Remove(group);
            _changedVertexGroups.Remove(group);
            foreach (GroupAllocation a in held.allocations)
            {
                GiveBackSpans(a.vertexStart, a.vertexCount, a.submeshStart, a.submeshCount, a.blockStart, a.blockCount);
                VerticesReleased += a.vertexCount;
            }

            VertexGroupsReleased++;
            return true;
        }

        private bool IsQuiet(VertexGroup group)
        {
            if (group.openReservations > 0)
            {
                return false;
            }

            foreach (VpIndexRangeHandle range in group.ranges)
            {
                if (_indices.TryGetState(range, out VpIndexRangeState state, out _, out _) && state != VpIndexRangeState.Free)
                {
                    return false;
                }
            }

            return true;
        }

        private bool TryGroupOf(VpStoredGeometry geometry, out int group)
        {
            group = 0;
            if (geometry.blockCount <= 0 || geometry.blockStart < 0 || geometry.blockStart >= _vertexBlocks.Length)
            {
                return false;
            }

            group = _vertexBlocks[geometry.blockStart].vertexStart;
            return _vertexGroups.ContainsKey(group);
        }

        /// <summary>A root append published: its spans open a group of their own. A root with no vertices opens none.</summary>
        private void OpenVertexGroup(int vertexStart, int vertexCount, int submeshStart, int submeshCount, int blockStart, int blockCount)
        {
            if (vertexCount <= 0)
            {
                return;
            }

            if (_vertexGroups.ContainsKey(vertexStart))
            {
                // Spans never overlap while taken, so a live group cannot start where a new root's vertices start.
                throw new InvalidOperationException("a vertex group already starts at " + vertexStart);
            }

            var group = new VertexGroup();
            group.allocations.Add(new GroupAllocation
            {
                vertexStart = vertexStart, vertexCount = vertexCount, submeshStart = submeshStart, submeshCount = submeshCount,
                blockStart = blockStart, blockCount = blockCount,
            });
            _vertexGroups.Add(vertexStart, group);
        }

        /// <summary>What a cut's commit kept, added to its parent's group.</summary>
        private void AddToVertexGroup(int group, int vertexStart, int vertexCount, int submeshStart, int submeshCount, int blockStart, int blockCount)
        {
            if (group < 0 || !_vertexGroups.TryGetValue(group, out VertexGroup held))
            {
                return;
            }

            held.allocations.Add(new GroupAllocation
            {
                vertexStart = vertexStart, vertexCount = vertexCount, submeshStart = submeshStart, submeshCount = submeshCount,
                blockStart = blockStart, blockCount = blockCount,
            });
        }

        /// <summary>A published geometry's range, joined to its group; its descriptor's record names the group.</summary>
        private int JoinVertexGroup(VpIndexRangeHandle range, VpStoredGeometry geometry)
        {
            if (!TryGroupOf(geometry, out int group))
            {
                return -1;
            }

            _vertexGroups[group].ranges.Add(range);
            return group;
        }

        /// <summary>A reservation opened on <paramref name="parent"/>: its group, or -1 when the parent has none.</summary>
        private int OpenReservationOnGroup(VpStoredGeometry parent)
        {
            if (!TryGroupOf(parent, out int group))
            {
                return -1;
            }

            _vertexGroups[group].openReservations++;
            return group;
        }

        private void CloseReservationOnGroup(int group)
        {
            if (group >= 0 && _vertexGroups.TryGetValue(group, out VertexGroup held))
            {
                held.openReservations--;
                _changedVertexGroups.Add(group);
            }
        }

        /// <summary>A range was retired, or a lease on it let go: its group, if any, is marked for another look.</summary>
        private void NoteRangeLetGo(VpIndexRangeHandle range)
        {
            if ((uint)range.descriptor >= (uint)_appendOfDescriptor.Length)
            {
                return;
            }

            AppendRecord record = _appendOfDescriptor[range.descriptor];
            if (record.generation == range.generation && record.vertexGroup > 0 && _vertexGroups.ContainsKey(record.vertexGroup - 1))
            {
                _changedVertexGroups.Add(record.vertexGroup - 1);
            }
        }

        private string DescribeVertexGroups()
        {
            return "vertex groups " + _vertexGroups.Count + " held, " + VertexGroupsReleased + " released (" + VerticesReleased + " vertices)";
        }
    }
}
