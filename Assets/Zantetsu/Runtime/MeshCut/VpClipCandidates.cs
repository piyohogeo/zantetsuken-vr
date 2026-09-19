using System;
using System.Collections.Generic;
using Unity.Mathematics;
using Zantetsu.Rendering;

namespace Zantetsu.MeshCut
{
    /// <summary>
    /// One cut boundary as a half-space a fragment is on: the adopted face of an operation (with the ledger that issued
    /// it, so the same number from another ledger is another face) and the side kept.
    /// </summary>
    public readonly struct VpClipBoundary : IEquatable<VpClipBoundary>
    {
        public VpClipBoundary(VpCapFace face, float side)
        {
            this.face = face;
            this.side = side;
        }

        public readonly VpCapFace face;

        /// <summary>+1 keeps the positive side of the face's plane, -1 the negative.</summary>
        public readonly float side;

        public bool IsSet => face.IsSet;

        public bool Equals(VpClipBoundary other) => face == other.face && side == other.side;
        public override bool Equals(object obj) => obj is VpClipBoundary other && Equals(other);
        public override int GetHashCode() => (face.GetHashCode() * 397) ^ side.GetHashCode();
        public static bool operator ==(VpClipBoundary a, VpClipBoundary b) => a.Equals(b);
        public static bool operator !=(VpClipBoundary a, VpClipBoundary b) => !a.Equals(b);
    }

    /// <summary>
    /// One member of a fragment's candidate set (DESIGN 5.2 <c>TemporaryClipConstraintCandidateSet</c>): a boundary the
    /// fragment is under that the geometry it is drawn with does not already reflect, the adopted plane as the ledger
    /// holds it (in the source fragment's frame), whether the cut is still pending, and the nearest candidate it depends
    /// on -- the closest earlier boundary of the same chain that is itself a candidate, unset when there is none.
    /// </summary>
    public readonly struct VpClipCandidate
    {
        public VpClipCandidate(VpClipBoundary boundary, float4 plane, bool pending, VpClipBoundary requires)
        {
            this.boundary = boundary;
            this.plane = plane;
            this.pending = pending;
            this.requires = requires;
        }

        public readonly VpClipBoundary boundary;

        /// <summary>The operation's adopted plane as the ledger holds it, in its source fragment's frame.</summary>
        public readonly float4 plane;

        /// <summary>The cut is admitted and not yet published: its boundary is a pending one.</summary>
        public readonly bool pending;

        /// <summary>The candidate this one needs selected before it can be, or unset.</summary>
        public readonly VpClipBoundary requires;
    }

    /// <summary>What the selection decided for one candidate.</summary>
    public enum VpClipSelectionState
    {
        /// <summary>In the selected prefix: drawn as one of at most <see cref="VpClipCandidates.Capacity"/> planes.</summary>
        Selected = 1,

        /// <summary>Past the capacity: the prefix was already full.</summary>
        IgnoredCapacity = 2,

        /// <summary>
        /// At or after the first candidate whose required boundary does not come before it in the list -- after it, or
        /// not in it at all. The list is not reordered to repair that; everything from there on is Ignored.
        /// </summary>
        IgnoredOrder = 3,
    }

    /// <summary>
    /// The candidate collection and the at-most-eight selection of DESIGN 5.2 / T-089, as a CPU step over explicit
    /// input.
    /// <para>
    /// **Who supplies what.** The ledger supplies the order cuts were admitted in (read from its held order, never
    /// from an id's number), the identity of each operation from pending to published, and which operation and side
    /// made each fragment. The caller supplies which boundaries the geometry the fragment is **drawn with** already
    /// reflects -- a statement about that geometry, not about any operation having completed somewhere; Completed or
    /// Terminated is not read. There is no fallback when that is not known: the caller must say, even if the answer is
    /// "none".
    /// </para>
    /// <para>
    /// **Collection.** For a fragment, its chain: the operation and side that made it, then the one that made its
    /// source, up to a fragment no cut made -- and, when it is drawn as one side of its own pending cut, that cut and
    /// side last. A sibling's boundaries are never on the chain. Each boundary of the chain the drawn geometry does not
    /// reflect is a candidate; the candidates are listed in the ledger's admission order, so every ancestor comes before
    /// its descendants, and each names the nearest earlier candidate of the chain as the one it requires. A boundary
    /// keeps its face identity and side from pending to published, so it is never listed twice.
    /// </para>
    /// <para>
    /// **Selection.** From the front of the list, at most <see cref="Capacity"/> candidates, as a dependency-closed
    /// prefix: at the first candidate whose required boundary is not earlier in the list, that candidate and everything
    /// after it are Ignored, and nothing is reordered; past the capacity, the rest is Ignored. A candidate after an
    /// Ignored one is never brought forward. View, distance, visibility, colour, pass and fixed or free play no part.
    /// </para>
    /// <para>
    /// **What this does not do.** It writes nothing: not the ledger, anchors, logical state or work, not the input
    /// lists, not the cap records. Nothing is cancelled, reissued or waited for. The capacity bounds the selection
    /// only -- every candidate is listed however many there are. The candidates, the selection and the cap records are
    /// kept apart. Nothing is drawn from this yet.
    /// </para>
    /// </summary>
    public static class VpClipCandidates
    {
        /// <summary>The most planes one fragment is drawn with at once (DESIGN 5.2 <c>TemporaryClipPlaneCapacity</c>).</summary>
        public const int Capacity = VpInstanceClip.PlaneCapacity;

        /// <summary>
        /// Lists <paramref name="fragment"/>'s candidates into <paramref name="into"/> (cleared first). With
        /// <paramref name="pendingSide"/> 0 the fragment is drawn whole; with +1 or -1 it is drawn as that side of its own
        /// pending cut, which it must have. False, listing nothing, for a fragment that is not live, or a pending side
        /// with no pending cut.
        /// </summary>
        /// <param name="reflected">
        /// The boundaries the geometry this fragment is drawn with already reflects. Required: an empty set says
        /// "none", and there is no default.
        /// </param>
        public static bool TryCollect(
            LogicalCutLedger ledger,
            LogicalFragmentId fragment,
            float pendingSide,
            IReadOnlyCollection<VpClipBoundary> reflected,
            List<VpClipCandidate> into)
        {
            if (ledger == null)
            {
                throw new ArgumentNullException(nameof(ledger));
            }

            if (reflected == null)
            {
                throw new ArgumentNullException(nameof(reflected), "what the drawn geometry reflects must be said, even if it is nothing");
            }

            if (into == null)
            {
                throw new ArgumentNullException(nameof(into));
            }

            if (pendingSide != 0f && pendingSide != 1f && pendingSide != -1f)
            {
                throw new ArgumentOutOfRangeException(nameof(pendingSide), pendingSide, "0, +1 or -1");
            }

            into.Clear();

            // The chain is at most every operation plus the pending one, and the candidates at most every operation,
            // so these are always enough; the rule itself is the one CollectInto applies for every caller.
            var chain = new VpClipBoundary[ledger.OperationCount + 1];
            var candidates = new VpClipCandidate[ledger.OperationCount];
            if (CollectInto(ledger, fragment, pendingSide, true, reflected, chain, candidates, 0, candidates.Length, out int count)
                != CollectOutcome.Collected)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                into.Add(candidates[i]);
            }

            return true;
        }

        /// <summary>What <see cref="CollectInto"/> did.</summary>
        internal enum CollectOutcome
        {
            /// <summary>The candidates are written.</summary>
            Collected,

            /// <summary>
            /// Nothing to collect: the fragment is unknown, not live when that was required, or a pending side was asked
            /// of a fragment with no pending cut.
            /// </summary>
            NotCollectable,

            /// <summary>The chain is longer than the chain scratch.</summary>
            ChainOverflow,

            /// <summary>There are more candidates than the room given.</summary>
            CandidateOverflow,
        }

        /// <summary>
        /// The one collection rule, writing into fixed arrays: <paramref name="fragment"/>'s chain into
        /// <paramref name="chain"/>, and its candidates into <paramref name="into"/> from <paramref name="start"/>, at most
        /// <paramref name="capacity"/> of them. Nothing grows and nothing is allocated; a shortage is answered, never cut
        /// short. With <paramref name="requireLive"/> false the chain of a fragment that is replaced or retired is read
        /// too -- still only through the ledger's read-only calls, and only with a pending side of 0.
        /// </summary>
        internal static CollectOutcome CollectInto(
            LogicalCutLedger ledger,
            LogicalFragmentId fragment,
            float pendingSide,
            bool requireLive,
            IReadOnlyCollection<VpClipBoundary> reflected,
            VpClipBoundary[] chain,
            VpClipCandidate[] into,
            int start,
            int capacity,
            out int count)
        {
            count = 0;
            if (!ledger.TryGetFragmentState(fragment, out LogicalFragmentState state)
                || (requireLive && state != LogicalFragmentState.Live)
                || (pendingSide != 0f && state != LogicalFragmentState.Live))
            {
                return CollectOutcome.NotCollectable;
            }

            // The chain, from the fragment up, as boundaries: the side each ancestor's fragment is on.
            int length = 0;
            if (pendingSide != 0f)
            {
                if (!ledger.TryGetActiveOperation(fragment, out CutOperationId pendingOperation))
                {
                    return CollectOutcome.NotCollectable;
                }

                if (length >= chain.Length)
                {
                    return CollectOutcome.ChainOverflow;
                }

                chain[length++] = new VpClipBoundary(new VpCapFace(ledger, pendingOperation), pendingSide);
            }

            LogicalFragmentId at = fragment;
            while (ledger.TryGetOrigin(at, out CutOperationId origin, out float side))
            {
                if (length >= chain.Length)
                {
                    return CollectOutcome.ChainOverflow;
                }

                chain[length++] = new VpClipBoundary(new VpCapFace(ledger, origin), side);
                if (!ledger.TryGetOperation(origin, out LogicalCutOperation operation))
                {
                    break;
                }

                at = operation.source;
            }

            // In the ledger's admission order, the chain's boundaries the drawn geometry does not reflect.
            VpClipBoundary previous = default;
            for (int position = 0; ledger.TryGetOperationAtAdmission(position, out LogicalCutOperation operation); position++)
            {
                var face = new VpCapFace(ledger, operation.id);
                int index = IndexOfFace(chain, length, face);
                if (index < 0)
                {
                    continue;
                }

                VpClipBoundary boundary = chain[index];
                if (Contains(reflected, boundary))
                {
                    continue;
                }

                if (count >= capacity)
                {
                    count = 0;
                    return CollectOutcome.CandidateOverflow;
                }

                bool pending = operation.state == LogicalCutOperationState.Admitted;
                into[start + count++] = new VpClipCandidate(boundary, operation.plane, pending, previous);
                previous = boundary;
            }

            return CollectOutcome.Collected;
        }

        /// <summary>
        /// Selects from <paramref name="candidates"/> as a dependency-closed prefix of at most <see cref="Capacity"/>,
        /// writing each candidate's outcome into <paramref name="states"/> at the same index, and answers how many were
        /// selected. The candidates are read and never changed.
        /// </summary>
        public static int Select(IReadOnlyList<VpClipCandidate> candidates, VpClipSelectionState[] states)
        {
            if (candidates == null)
            {
                throw new ArgumentNullException(nameof(candidates));
            }

            if (states == null || states.Length < candidates.Count)
            {
                throw new ArgumentException("There is a state slot for every candidate.", nameof(states));
            }

            return Select(candidates, 0, candidates.Count, states);
        }

        /// <summary>
        /// The same selection over <paramref name="count"/> candidates from <paramref name="start"/>, writing the states
        /// at the same indices. The one selection rule, for every caller.
        /// </summary>
        internal static int Select(
            IReadOnlyList<VpClipCandidate> candidates, int start, int count, VpClipSelectionState[] states)
        {
            int selected = 0;
            bool broken = false;
            for (int i = 0; i < count; i++)
            {
                if (!broken)
                {
                    VpClipBoundary requires = candidates[start + i].requires;
                    if (requires.IsSet && IndexOf(candidates, start, requires, i) < 0)
                    {
                        // Its requirement is not before it: after it, or not listed. Not repaired by reordering.
                        broken = true;
                    }
                }

                if (broken)
                {
                    states[start + i] = VpClipSelectionState.IgnoredOrder;
                }
                else if (selected < Capacity)
                {
                    states[start + i] = VpClipSelectionState.Selected;
                    selected++;
                }
                else
                {
                    states[start + i] = VpClipSelectionState.IgnoredCapacity;
                }
            }

            return selected;
        }

        private static int IndexOfFace(VpClipBoundary[] chain, int length, VpCapFace face)
        {
            for (int i = 0; i < length; i++)
            {
                if (chain[i].face == face)
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool Contains(IReadOnlyCollection<VpClipBoundary> set, VpClipBoundary boundary)
        {
            foreach (VpClipBoundary item in set)
            {
                if (item == boundary)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>The index of <paramref name="boundary"/> among the candidates before <paramref name="before"/>, or -1.</summary>
        private static int IndexOf(IReadOnlyList<VpClipCandidate> candidates, int start, VpClipBoundary boundary, int before)
        {
            for (int i = 0; i < before; i++)
            {
                if (candidates[start + i].boundary == boundary)
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
