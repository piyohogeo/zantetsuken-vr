using System;
using UnityEngine;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// The geometry commit of the cut DAG, done on a <see cref="VpLogicalCutDisplay"/> (DESIGN 4.5.6): the two sides
    /// the kernel produced take the place of the body they were cut from, with the boundary each side now reflects, in
    /// one change. The transfer to the GPU is part of that change — the appended vertices once and the two sides'
    /// indices as the one contiguous run they were written as — and the display's own order guarantees that what a
    /// draw reads has been transferred before it is drawn.
    /// <para>
    /// **What it is not.** It adopts no snapshot, prepares no camera and draws nothing: a commit changes what the next
    /// collection is built from, and a collection that has already settled this frame keeps drawing what it settled. A
    /// refusal is an ordinary "not established": nothing of what is shown has moved, no ownership has changed, and the
    /// cut is offered again at a later opportunity. Whether the operation may be committed at all — its publication,
    /// its branch, its basis — belongs to <see cref="CutDag"/> and is not judged again here.
    /// </para>
    /// <para>
    /// **What each side becomes.** A produced side is its own geometry; a side that is the input borrowed back stays
    /// the geometry it already was, kept by the same registration; an empty side has none, and gets no renderer and no
    /// stand-in geometry. That is what is handed back for the branch to be cut from next.
    /// </para>
    /// </summary>
    public sealed class VpDisplayGeometryCommit : ICutGeometryCommit
    {
        private readonly VpLogicalCutDisplay _display;

        public VpDisplayGeometryCommit(VpLogicalCutDisplay display)
        {
            _display = display ?? throw new ArgumentNullException(nameof(display));
        }

        /// <summary>How many commits this has established.</summary>
        public int Commits { get; private set; }

        /// <summary>How many were refused as not established, each of which changed nothing.</summary>
        public int Refusals { get; private set; }

        public bool TryCommit(in CutGeometryCommit commit, out CutGeometryCommitted committed)
        {
            committed = default;
            if (!_display.TryCommitCut(
                    commit.source,
                    commit.operation,
                    new Vector4(commit.plane.x, commit.plane.y, commit.plane.z, commit.plane.w),
                    commit.positiveFragment,
                    commit.positive,
                    commit.negativeFragment,
                    commit.negative,
                    commit.capTriangles))
            {
                Refusals++;
                return false;
            }

            // What each side is from now on, which is what a later cut of it reads: the geometry it was produced as,
            // the input it borrowed back, or nothing at all where the side is empty.
            committed.positive = commit.positive.IsEmpty ? default : commit.positive.geometry;
            committed.negative = commit.negative.IsEmpty ? default : commit.negative.geometry;
            Commits++;
            return true;
        }
    }
}
