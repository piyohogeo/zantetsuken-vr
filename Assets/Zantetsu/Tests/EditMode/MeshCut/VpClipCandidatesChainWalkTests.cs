using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The candidate collection reads the chain from the top down instead of scanning every operation the ledger ever
    /// admitted (2026-09-29). Against a reference copy of the scan, over generated histories -- deep re-cuts, pending cuts,
    /// aborted cuts, reflected boundaries, retired fragments, unrelated history, and too little room -- the candidates,
    /// their order, their dependencies, the pending flags and the outcomes are the same; and the reads the ledger counts
    /// no longer grow with the history that has nothing to do with the fragment.
    /// </summary>
    public class VpClipCandidatesChainWalkTests
    {
        private static float4 Plane(int k) => new float4(math.normalize(new float3(1f, 0.25f * (k % 5), 0.5f * (k % 3))), -0.1f * (k + 1));

        private static CutOperationId Admit(LogicalCutLedger ledger, LogicalFragmentId source, int k)
        {
            Assert.That(ledger.Admit(source, Plane(k), true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            return cut;
        }

        // Published and, unless asked to stay incomplete, its geometry completed: a published cut counts against the
        // ledger's incomplete budget until then, and a long history would fill it.
        private static (LogicalFragmentId positive, LogicalFragmentId negative) Publish(LogicalCutLedger ledger, CutOperationId cut, bool leaveIncomplete = false)
        {
            Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            if (!leaveIncomplete) Assert.That(ledger.CompleteGeometry(cut), Is.EqualTo(LogicalCutResultOutcome.Applied));
            return (positive, negative);
        }

        // The listing by the ledger's held admission order, as CollectInto did it before: the reference.
        private static VpClipCandidates.CollectOutcome Reference(
            LogicalCutLedger ledger, LogicalFragmentId fragment, float pendingSide, bool requireLive,
            IReadOnlyCollection<VpClipBoundary> reflected, VpClipBoundary[] chain, VpClipCandidate[] into, int start, int capacity, out int count)
        {
            count = 0;
            if (!ledger.TryGetFragmentState(fragment, out LogicalFragmentState state)
                || (requireLive && state != LogicalFragmentState.Live) || (pendingSide != 0f && state != LogicalFragmentState.Live))
                return VpClipCandidates.CollectOutcome.NotCollectable;
            int length = 0;
            if (pendingSide != 0f)
            {
                if (!ledger.TryGetActiveOperation(fragment, out CutOperationId pendingOperation)) return VpClipCandidates.CollectOutcome.NotCollectable;
                if (length >= chain.Length) return VpClipCandidates.CollectOutcome.ChainOverflow;
                chain[length++] = new VpClipBoundary(new VpCapFace(ledger, pendingOperation), pendingSide);
            }

            LogicalFragmentId at = fragment;
            while (ledger.TryGetOrigin(at, out CutOperationId origin, out float side))
            {
                if (length >= chain.Length) return VpClipCandidates.CollectOutcome.ChainOverflow;
                chain[length++] = new VpClipBoundary(new VpCapFace(ledger, origin), side);
                if (!ledger.TryGetOperation(origin, out LogicalCutOperation operation)) break;
                at = operation.source;
            }

            VpClipBoundary previous = default;
            for (int position = 0; ledger.TryGetOperationAtAdmission(position, out LogicalCutOperation operation); position++)
            {
                var face = new VpCapFace(ledger, operation.id);
                int index = -1;
                for (int i = 0; i < length; i++) if (chain[i].face == face) { index = i; break; }
                if (index < 0) continue;
                VpClipBoundary boundary = chain[index];
                bool isReflected = false;
                foreach (VpClipBoundary r in reflected) if (r == boundary) { isReflected = true; break; }
                if (isReflected) continue;
                if (count >= capacity) { count = 0; return VpClipCandidates.CollectOutcome.CandidateOverflow; }
                bool pending = operation.state == LogicalCutOperationState.Admitted;
                into[start + count++] = new VpClipCandidate(boundary, operation.plane, pending, previous);
                previous = boundary;
            }

            return VpClipCandidates.CollectOutcome.Collected;
        }

        private sealed class History
        {
            public LogicalCutLedger ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(64));
            public readonly List<LogicalFragmentId> live = new List<LogicalFragmentId>();
            public readonly List<LogicalFragmentId> replaced = new List<LogicalFragmentId>();
            public readonly List<LogicalFragmentId> retired = new List<LogicalFragmentId>();
            public readonly List<LogicalFragmentId> withPending = new List<LogicalFragmentId>();
            public int operations, aborted;
        }

        // A history: `roots` lineages cut `depth` deep on random sides (with sibling branches kept live), `noise`
        // unrelated roots cut once each, a few pending cuts on leaves, a few aborted cuts, and a few retired leaves.
        private static History Make(int seed, int roots, int depth, int noise)
        {
            var random = new System.Random(seed);
            var h = new History();
            LogicalCutLedger ledger = h.ledger;
            int k = 0;
            for (int r = 0; r < roots; r++)
            {
                LogicalFragmentId at = ledger.AddFragment();
                for (int d = 0; d < depth; d++)
                {
                    // Unrelated history interleaved with this lineage's cuts.
                    for (int n = 0; n < noise; n++) { Publish(ledger, Admit(ledger, ledger.AddFragment(), k++)); h.operations++; }
                    // One cut in eight stays Published but not Completed (its geometry still on its way).
                    (LogicalFragmentId positive, LogicalFragmentId negative) = Publish(ledger, Admit(ledger, at, k++), leaveIncomplete: random.Next(8) == 0 && !ledger.Budget.IsFull);
                    h.operations++;
                    bool goPositive = random.Next(2) == 0;
                    h.live.Add(goPositive ? negative : positive);   // the sibling stays a live leaf
                    h.replaced.Add(at);
                    at = goPositive ? positive : negative;
                }

                h.live.Add(at);
            }

            // Pending cuts on some live leaves (admitted, not published); aborted cuts on others (admitted, aborted).
            for (int i = 0; i < h.live.Count; i++)
            {
                int roll = random.Next(6);
                if (ledger.Budget.IsFull) break;
                if (roll == 0) { Admit(ledger, h.live[i], k++); h.operations++; h.withPending.Add(h.live[i]); }
                else if (roll == 1) { CutOperationId cut = Admit(ledger, h.live[i], k++); h.operations++; Assert.That(ledger.Abort(cut), Is.EqualTo(LogicalCutResultOutcome.Applied)); h.aborted++; }
            }

            // Some leaves without a pending cut retire.
            for (int i = h.live.Count - 1; i >= 0; i--)
            {
                if (h.withPending.Contains(h.live[i]) || random.Next(5) != 0) continue;
                ledger.Retire(h.live[i]);
                h.retired.Add(h.live[i]);
                h.live.RemoveAt(i);
            }

            return h;
        }

        private static IReadOnlyCollection<VpClipBoundary> Reflected(LogicalCutLedger ledger, LogicalFragmentId fragment, System.Random random)
        {
            // A random subset of the fragment's own chain boundaries, and one boundary of another ledger (never reflected).
            var set = new List<VpClipBoundary>();
            LogicalFragmentId at = fragment;
            while (ledger.TryGetOrigin(at, out CutOperationId origin, out float side))
            {
                if (random.Next(3) == 0) set.Add(new VpClipBoundary(new VpCapFace(ledger, origin), side));
                if (!ledger.TryGetOperation(origin, out LogicalCutOperation op)) break;
                at = op.source;
            }

            set.Add(new VpClipBoundary(new VpCapFace(new LogicalCutLedger(new LogicalCutIncompleteBudget(1)), new CutOperationId(1)), 1f));
            return set;
        }

        private static void AssertSame(VpClipCandidate[] a, VpClipCandidate[] b, int count, string what)
        {
            for (int i = 0; i < count; i++)
            {
                Assert.That(a[i].boundary, Is.EqualTo(b[i].boundary), what + ": boundary " + i);
                Assert.That(a[i].plane, Is.EqualTo(b[i].plane), what + ": plane " + i);
                Assert.That(a[i].pending, Is.EqualTo(b[i].pending), what + ": pending " + i);
                Assert.That(a[i].requires, Is.EqualTo(b[i].requires), what + ": requires " + i);
            }
        }

        [TestCase(1, 3, 6, 2)]
        [TestCase(2, 5, 12, 5)]
        [TestCase(3, 8, 20, 1)]
        [TestCase(4, 2, 30, 20)]
        public void TheChainWalk_ListsWhatTheAdmissionScanListed_OverGeneratedHistories(int seed, int roots, int depth, int noise)
        {
            History h = Make(seed, roots, depth, noise);
            LogicalCutLedger ledger = h.ledger;
            var random = new System.Random(seed * 7919);
            var chain = new VpClipBoundary[ledger.OperationCount + 1];
            var chainRef = new VpClipBoundary[ledger.OperationCount + 1];
            var got = new VpClipCandidate[ledger.OperationCount];
            var want = new VpClipCandidate[ledger.OperationCount];
            int compared = 0, pendingCases = 0, overflowCases = 0, retiredCases = 0;
            int fallbacksBefore = VpClipCandidates.OrderFallbacks;

            void Compare(LogicalFragmentId fragment, float pendingSide, bool requireLive, IReadOnlyCollection<VpClipBoundary> reflected, int capacity, int chainRoom, string what)
            {
                var c = new VpClipBoundary[chainRoom];
                var cr = new VpClipBoundary[chainRoom];
                VpClipCandidates.CollectOutcome a = VpClipCandidates.CollectInto(ledger, fragment, pendingSide, requireLive, reflected, c, got, 3, capacity, out int n);
                VpClipCandidates.CollectOutcome b = Reference(ledger, fragment, pendingSide, requireLive, reflected, cr, want, 3, capacity, out int m);
                Assert.That(a, Is.EqualTo(b), what + ": outcome");
                Assert.That(n, Is.EqualTo(m), what + ": count");
                if (a == VpClipCandidates.CollectOutcome.Collected) AssertSame(got, want, n + 3, what);
                if (a == VpClipCandidates.CollectOutcome.CandidateOverflow) overflowCases++;
                compared++;
            }

            foreach (LogicalFragmentId leaf in h.live)
            {
                IReadOnlyCollection<VpClipBoundary> reflected = Reflected(ledger, leaf, random);
                Compare(leaf, 0f, true, reflected, ledger.OperationCount, chain.Length, "leaf " + leaf.value);
                Compare(leaf, 0f, true, Array.Empty<VpClipBoundary>(), ledger.OperationCount, chain.Length, "leaf " + leaf.value + " nothing reflected");
                // Too little room, both ways.
                Compare(leaf, 0f, true, reflected, 2, chain.Length, "leaf " + leaf.value + " two candidates of room");
                Compare(leaf, 0f, true, reflected, ledger.OperationCount, 3, "leaf " + leaf.value + " three of chain");
                if (h.withPending.Contains(leaf))
                {
                    pendingCases++;
                    Compare(leaf, 1f, true, reflected, ledger.OperationCount, chain.Length, "leaf " + leaf.value + " pending +");
                    Compare(leaf, -1f, true, reflected, ledger.OperationCount, chain.Length, "leaf " + leaf.value + " pending -");
                }
                else
                {
                    Compare(leaf, 1f, true, reflected, ledger.OperationCount, chain.Length, "leaf " + leaf.value + " no pending cut");
                }
            }

            foreach (LogicalFragmentId gone in h.retired)
            {
                retiredCases++;
                Compare(gone, 0f, false, Reflected(ledger, gone, random), ledger.OperationCount, chain.Length, "retired " + gone.value);
                Compare(gone, 0f, true, Array.Empty<VpClipBoundary>(), ledger.OperationCount, chain.Length, "retired " + gone.value + " live required");
            }

            foreach (LogicalFragmentId old in h.replaced)
            {
                Compare(old, 0f, false, Array.Empty<VpClipBoundary>(), ledger.OperationCount, chain.Length, "replaced " + old.value);
            }

            Assert.That(compared, Is.GreaterThan(roots * depth));
            Assert.That(pendingCases + retiredCases, Is.GreaterThan(0), "the history had pending and retired cases");
            Assert.That(overflowCases, Is.GreaterThan(0), "some room was too little");
            Assert.That(h.aborted, Is.GreaterThan(0), "the history had aborted cuts");
            Assert.That(VpClipCandidates.OrderFallbacks, Is.EqualTo(fallbacksBefore), "every chain read in admission order: the scan was never needed");
        }

        /// <summary>
        /// The same fragment drawn in a small history and in one with a hundred times the unrelated cuts: the reads
        /// the ledger counts for its collection are the same, and none of them is a read of the admission order.
        /// </summary>
        [Test]
        public void CollectingOneFragment_ReadsItsChainOnly_HoweverLongTheUnrelatedHistory()
        {
            long ReadsToCollect(int noise, out int operations)
            {
                var ledger = new LogicalCutLedger(new LogicalCutIncompleteBudget(8));
                int k = 0;
                LogicalFragmentId at = ledger.AddFragment();
                for (int d = 0; d < 10; d++)
                {
                    for (int n = 0; n < noise; n++) Publish(ledger, Admit(ledger, ledger.AddFragment(), k++));
                    (LogicalFragmentId positive, LogicalFragmentId _) = Publish(ledger, Admit(ledger, at, k++));
                    at = positive;
                }

                operations = ledger.OperationCount;
                var chain = new VpClipBoundary[ledger.OperationCount + 1];
                var into = new VpClipCandidate[ledger.OperationCount];
                long origins = ledger.OriginLookups, admissions = ledger.AdmissionOrderReads;
                Assert.That(VpClipCandidates.CollectInto(ledger, at, 0f, true, Array.Empty<VpClipBoundary>(), chain, into, 0, into.Length, out int count),
                    Is.EqualTo(VpClipCandidates.CollectOutcome.Collected));
                Assert.That(count, Is.EqualTo(10), "ten boundaries, none reflected");
                Assert.That(ledger.AdmissionOrderReads - admissions, Is.Zero, "the admission order is not scanned");
                return ledger.OriginLookups - origins;
            }

            long small = ReadsToCollect(1, out int fewOperations);
            long large = ReadsToCollect(100, out int manyOperations);
            Assert.That(manyOperations, Is.GreaterThan(fewOperations * 50));
            Assert.That(large, Is.EqualTo(small), "the chain's reads do not grow with the unrelated history");
            Assert.That(small, Is.EqualTo(11), "one origin read per boundary and one that finds the root");
        }
    }
}
