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
    /// <summary>
    /// A registration root's chain as its structure build's validation read it (2026-10-01): the boundaries from the root
    /// up, bottom first, in <see cref="boundaries"/> from <see cref="start"/>; the offsets (bottom first) of those the
    /// registration's reflected set does not hold; and whether they read in admission order from the top down. A
    /// collection whose walk up reaches <see cref="root"/> takes these instead of walking and matching them again. Valid only
    /// within that build, for that ledger and that registration's reflected set: the snapshot passes it under those
    /// conditions only.
    /// </summary>
    internal readonly struct VpChainSegment
    {
        public VpChainSegment(LogicalFragmentId root, VpClipBoundary[] boundaries, int start, int length, int[] unreflected, int unreflectedStart, int unreflectedCount, bool ordered)
        {
            this.root = root;
            this.boundaries = boundaries;
            this.start = start;
            this.length = length;
            this.unreflected = unreflected;
            this.unreflectedStart = unreflectedStart;
            this.unreflectedCount = unreflectedCount;
            this.ordered = ordered;
        }

        public readonly LogicalFragmentId root;
        public readonly VpClipBoundary[] boundaries;
        public readonly int start, length;
        public readonly int[] unreflected;
        public readonly int unreflectedStart, unreflectedCount;
        public readonly bool ordered;
        public bool IsSet => boundaries != null;
    }

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
        /// too -- still only through the ledger's read-only calls, and only with a pending side of 0. With
        /// <paramref name="lineage"/> (a structure build's, open for this ledger) the chain's ancestors are taken from the
        /// facts that build has read, and only those it has not are read from the ledger; without it (the default) every
        /// ancestor is read from the ledger. The chain and the candidates are the same either way. With
        /// <paramref name="segment"/> (2026-10-01: the registration root's chain as the same build's validation read and
        /// matched it), a walk that reaches its root takes it instead of walking and matching it again; the chain's
        /// length, its order, the candidates and every refusal are the same.
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
            out int count,
            VpLineageFacts lineage = null,
            in VpChainSegment segment = default)
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
            bool spliced = false;
            while (true)
            {
                // The registration root's chain, read and matched already in this build: taken as it stands -- unless it
                // does not read in admission order (then walked, and the order check below finds it as always).
                if (segment.IsSet && at == segment.root && segment.ordered && !reflectedAfterReadForTest
                    && !(orderViolationAtForTest >= 0 && orderViolationAtForTest < segment.length))
                {
                    spliced = true;
                    break;
                }

                LineageVisits++;
                CutOperationId origin;
                float side;
                bool operationKnown;
                LogicalFragmentId source;
                if (lineage != null)
                {
                    long read = LineageReads;
                    if (!lineage.TryGet(ledger, at, out origin, out side, out operationKnown, out source, out _, ref LineageReads, ref LineageHits))
                    {
                        break;
                    }

                    if (LineageReads != read) OperationReads++;
                }
                else
                {
                    LineageReads++;
                    if (!ledger.TryGetOrigin(at, out origin, out side))
                    {
                        break;
                    }

                    OperationReads++;
                    operationKnown = ledger.TryGetOperation(origin, out LogicalCutOperation operation);
                    source = operation.source;
                }

                if (length >= chain.Length)
                {
                    return CollectOutcome.ChainOverflow;
                }

                chain[length++] = new VpClipBoundary(new VpCapFace(ledger, origin), side);
                ChainSteps++;

                // An origin whose operation is not known ends the chain here, its boundary added: the collection's own
                // rule, never the validation's Lineage refusal.
                if (!operationKnown)
                {
                    break;
                }

                at = source;
            }

            if (chainOnlyForTest)
            {
                return CollectOutcome.Collected;
            }

            if (spliced)
            {
                return CollectSpliced(ledger, reflected, chain, length, in segment, into, start, capacity, out count);
            }

            // In the ledger's admission order, the chain's boundaries the drawn geometry does not reflect. The chain was
            // read from the fragment up, and each boundary on it was admitted before the one below it (a fragment is cut
            // only once it exists, and it exists only once the cut above it was published; the ledger appends operations
            // and never reorders them), so the chain read from the top down **is** the admission order of these
            // boundaries: only the chain's own operations are read, not every operation the ledger ever admitted
            // (2026-09-29: the scan of the whole history, once per branch, was what grew with the history and the depth).
            // The order is checked as it is walked; a chain that did not hold it -- none does -- is listed by the scan.
            VpClipBoundary previous = default;
            int lastAdmitted = -1;
            for (int i = length - 1; i >= 0; i--)
            {
                VpClipBoundary boundary = chain[i];
                if (boundary.face.operation.value <= lastAdmitted || orderViolationAtForTest == length - 1 - i)
                {
                    OrderFallbacks++;
                    count = 0;
                    return CollectByAdmissionScan(ledger, reflected, chain, length, into, start, capacity, out count);
                }

                lastAdmitted = boundary.face.operation.value;
                LogicalCutOperation operation;
                if (reflectedAfterReadForTest)
                {
                    OperationReads++;
                    if (!ledger.TryGetOperation(boundary.face.operation, out operation) || Contains(reflected, boundary))
                    {
                        continue;
                    }
                }
                else
                {
                    // A boundary the geometry reflects is no candidate whatever its operation says: asked first, so that its
                    // operation is not read a second time (2026-10-01; either way it is skipped, as before).
                    ReflectedLookups++;
                    if (Contains(reflected, boundary))
                    {
                        continue;
                    }

                    OperationReads++;
                    if (!ledger.TryGetOperation(boundary.face.operation, out operation))
                    {
                        continue;
                    }
                }

                if (count >= capacity)
                {
                    count = 0;
                    return CollectOutcome.CandidateOverflow;
                }

                bool pending = operation.state == LogicalCutOperationState.Admitted;
                into[start + count++] = new VpClipCandidate(boundary, operation.plane, pending, previous);
                CandidatesMade++;
                previous = boundary;
            }

            return CollectOutcome.Collected;
        }

        /// <summary>How many chains were not in admission order from the top down and were listed by the scan. Expected 0.</summary>
        public static int OrderFallbacks { get; private set; }

        // The chain with a registration root's segment taken in: below the root, chain[0..below) as walked; the segment
        // above. The same events in the same order as the whole chain's top-down pass: the segment's boundaries first (in
        // admission order, checked when it was made; its reflected ones skipped), then those below, checked on from the
        // segment's bottom. A violation below copies the segment into the chain and lists the whole by the scan.
        private static CollectOutcome CollectSpliced(
            LogicalCutLedger ledger, IReadOnlyCollection<VpClipBoundary> reflected, VpClipBoundary[] chain, int below,
            in VpChainSegment segment, VpClipCandidate[] into, int start, int capacity, out int count)
        {
            count = 0;
            if (below + segment.length > chain.Length)
            {
                ChainSteps += chain.Length - below;   // as many as the walk would have placed before the room ran out
                return CollectOutcome.ChainOverflow;
            }

            SegmentSplices++;
            SegmentBoundaries += segment.length;
            ChainSteps += segment.length;
            VpClipBoundary previous = default;
            for (int u = segment.unreflectedCount - 1; u >= 0; u--)
            {
                VpClipBoundary boundary = segment.boundaries[segment.start + segment.unreflected[segment.unreflectedStart + u]];
                OperationReads++;
                if (!ledger.TryGetOperation(boundary.face.operation, out LogicalCutOperation operation))
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
                CandidatesMade++;
                previous = boundary;
            }

            int lastAdmitted = segment.length > 0 ? segment.boundaries[segment.start].face.operation.value : -1;
            for (int i = below - 1; i >= 0; i--)
            {
                VpClipBoundary boundary = chain[i];
                if (boundary.face.operation.value <= lastAdmitted || orderViolationAtForTest == segment.length + below - 1 - i)
                {
                    OrderFallbacks++;
                    System.Array.Copy(segment.boundaries, segment.start, chain, below, segment.length);
                    count = 0;
                    return CollectByAdmissionScan(ledger, reflected, chain, below + segment.length, into, start, capacity, out count);
                }

                lastAdmitted = boundary.face.operation.value;
                ReflectedLookups++;
                if (Contains(reflected, boundary))
                {
                    continue;
                }

                OperationReads++;
                if (!ledger.TryGetOperation(boundary.face.operation, out LogicalCutOperation operation))
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
                CandidatesMade++;
                previous = boundary;
            }

            return CollectOutcome.Collected;
        }

        /// <summary>For observation: the reflected-set lookups the collections made, the segments taken in, and their boundaries (not walked, not matched).</summary>
        internal static long ReflectedLookups, SegmentSplices, SegmentBoundaries;

        /// <summary>Tests only: the boundary that many from the top of a chain is taken as out of admission order (the scan's path, which no real chain takes).</summary>
        internal static int orderViolationAtForTest = -1;

        /// <summary>Tests only (a cost split): the chain is read and nothing more is done (no candidates).</summary>
        internal static bool chainOnlyForTest;

        /// <summary>For observation, over every collection on the main thread: the chain boundaries read, the operations read, the candidates made.</summary>
        internal static long ChainSteps, OperationReads, CandidatesMade;

        /// <summary>For observation, over every collection's chain walk: the ancestors visited, their facts read from the ledger, and those taken from a build's facts.</summary>
        internal static long LineageVisits, LineageReads, LineageHits;

        /// <summary>Tests only: each chain boundary's operation read before the reflected set is asked (the collection before 2026-10-01).</summary>
        internal static bool reflectedAfterReadForTest;

        // The listing by the ledger's held order, over every operation admitted: the one the chain walk above replaces,
        // kept for a chain that did not read in admission order.
        private static CollectOutcome CollectByAdmissionScan(
            LogicalCutLedger ledger, IReadOnlyCollection<VpClipBoundary> reflected, VpClipBoundary[] chain, int length,
            VpClipCandidate[] into, int start, int capacity, out int count)
        {
            count = 0;
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
            // A snapshot's index of the registration's set answers as the set's scan would, without the scan.
            if (set is VpReflectedIndex index)
            {
                return index.Contains(boundary);
            }

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
