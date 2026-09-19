using System;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// Candidate collection and the at-most-eight selection (DESIGN 5.2, T-089): the ledger's own chains, the drawn
    /// geometry's reflected boundaries given explicitly by the test, and the CPU selection. This is not a check of any
    /// real geometry commit: what the drawn geometry reflects is stated here, not produced.
    /// <para>
    /// Chains, publication, siblings, Completed and Terminated come from a real <see cref="LogicalCutLedger"/>. Input
    /// in an order the ledger cannot produce -- a descendant before its ancestor, a requirement not listed -- is
    /// written out by hand, and those tests say so.
    /// </para>
    /// </summary>
    public class VpClipCandidatesTests
    {
        private static LogicalCutLedger NewLedger()
        {
            return new LogicalCutLedger(new LogicalCutIncompleteBudget(32));
        }

        /// <summary>A distinct plane for the k-th cut, so planes are told apart by value too.</summary>
        private static float4 Plane(int k)
        {
            return new float4(0f, 1f, 0f, -0.1f * (k + 1));
        }

        private static CutOperationId AdmitAndPrepare(LogicalCutLedger ledger, LogicalFragmentId source, int k)
        {
            Assert.That(ledger.Admit(source, Plane(k), true, out CutOperationId cut), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(ledger.PrepareAnchorDistribution(cut, 0.01f, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            return cut;
        }

        private static (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) CutAndPublish(
            LogicalCutLedger ledger, LogicalFragmentId source, int k)
        {
            CutOperationId cut = AdmitAndPrepare(ledger, source, k);
            Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            return (cut, positive, negative);
        }

        /// <summary>
        /// A chain of <paramref name="length"/> published cuts from a new root, each cutting the child on the side
        /// given by <paramref name="sides"/> (cycled). Answers the cuts, the sides followed, and the last child.
        /// </summary>
        private static (List<CutOperationId> cuts, List<float> sides, LogicalFragmentId leaf) Chain(
            LogicalCutLedger ledger, int length, params float[] sides)
        {
            var cuts = new List<CutOperationId>();
            var followed = new List<float>();
            LogicalFragmentId at = ledger.AddFragment();
            for (int k = 0; k < length; k++)
            {
                (CutOperationId cut, LogicalFragmentId positive, LogicalFragmentId negative) = CutAndPublish(ledger, at, k);
                float side = sides.Length == 0 ? 1f : sides[k % sides.Length];
                cuts.Add(cut);
                followed.Add(side);
                at = side > 0f ? positive : negative;
            }

            return (cuts, followed, at);
        }

        private static VpClipBoundary B(LogicalCutLedger ledger, CutOperationId cut, float side)
        {
            return new VpClipBoundary(new VpCapFace(ledger, cut), side);
        }

        private static List<VpClipCandidate> Collect(
            LogicalCutLedger ledger, LogicalFragmentId fragment, float pendingSide = 0f, params VpClipBoundary[] reflected)
        {
            var into = new List<VpClipCandidate>();
            Assert.That(VpClipCandidates.TryCollect(ledger, fragment, pendingSide, reflected, into), Is.True, "collected");
            return into;
        }

        private static VpClipSelectionState[] Select(IReadOnlyList<VpClipCandidate> candidates, out int selected)
        {
            var states = new VpClipSelectionState[candidates.Count];
            selected = VpClipCandidates.Select(candidates, states);
            return states;
        }

        // ----- the ledger's reading ------------------------------------------------------------------------------

        /// <summary>The held admission order is read by position, and a child's origin and side by the operations' records.</summary>
        [Test]
        public void TheLedger_GivesItsAdmissionOrder_AndEachChildsOriginAndSide()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            LogicalFragmentId other = ledger.AddFragment();
            (CutOperationId a, LogicalFragmentId aPlus, LogicalFragmentId aMinus) = CutAndPublish(ledger, root, 0);
            CutOperationId b = AdmitAndPrepare(ledger, other, 1);

            Assert.That(ledger.TryGetOperationAtAdmission(0, out LogicalCutOperation first), Is.True);
            Assert.That(first.id, Is.EqualTo(a), "the first admitted is first");
            Assert.That(ledger.TryGetOperationAtAdmission(1, out LogicalCutOperation second), Is.True);
            Assert.That(second.id, Is.EqualTo(b));
            Assert.That(second.state, Is.EqualTo(LogicalCutOperationState.Admitted));
            Assert.That(ledger.TryGetOperationAtAdmission(2, out _), Is.False, "past the end");
            Assert.That(ledger.TryGetOperationAtAdmission(-1, out _), Is.False);

            Assert.That(ledger.TryGetOrigin(aPlus, out CutOperationId origin, out float side), Is.True);
            Assert.That(origin, Is.EqualTo(a));
            Assert.That(side, Is.EqualTo(1f));
            Assert.That(ledger.TryGetOrigin(aMinus, out _, out float minus), Is.True);
            Assert.That(minus, Is.EqualTo(-1f));
            Assert.That(ledger.TryGetOrigin(root, out _, out _), Is.False, "no cut made the root");
            Assert.That(ledger.TryGetOrigin(default, out _, out _), Is.False);
        }

        // ----- counts and the capacity -----------------------------------------------------------------------------

        /// <summary>
        /// No cut, three, exactly eight and ten: every candidate is listed however many there are, and the first eight
        /// are selected; the rest are Ignored for capacity.
        /// </summary>
        [Test]
        public void NoneWithinExactlyEightAndBeyond()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId alone = ledger.AddFragment();
            List<VpClipCandidate> none = Collect(ledger, alone);
            Assert.That(none, Is.Empty, "a fragment no cut made has no candidate");
            Assert.That(VpClipCandidates.Select(none, Array.Empty<VpClipSelectionState>()), Is.Zero);

            foreach (int length in new[] { 3, 8, 10 })
            {
                (List<CutOperationId> cuts, List<float> sides, LogicalFragmentId leaf) = Chain(ledger, length, 1f, -1f);
                List<VpClipCandidate> candidates = Collect(ledger, leaf);
                Assert.That(candidates.Count, Is.EqualTo(length), length + ": every boundary of the chain is listed");
                VpClipSelectionState[] states = Select(candidates, out int selected);
                Assert.That(selected, Is.EqualTo(Math.Min(length, VpClipCandidates.Capacity)), length + ": selected");
                for (int i = 0; i < length; i++)
                {
                    Assert.That(candidates[i].boundary, Is.EqualTo(B(ledger, cuts[i], sides[i])), length + ": candidate " + i + " in admission order");
                    Assert.That(
                        states[i],
                        Is.EqualTo(i < VpClipCandidates.Capacity ? VpClipSelectionState.Selected : VpClipSelectionState.IgnoredCapacity),
                        length + ": state " + i);
                }
            }
        }

        // ----- chains, sides and siblings ------------------------------------------------------------------------

        /// <summary>
        /// Root cut by A; its positive child by B; B's negative child by C. The grandchild on C's positive side is under
        /// A kept positive, B kept negative and C kept positive, each requiring the one before. A's negative child, a
        /// sibling of the cut branch, is under A negative only: nothing of B or C reaches it.
        /// </summary>
        [Test]
        public void AChainKeepsItsAncestorsHalfSpaces_AndASiblingStaysOut()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            (CutOperationId a, LogicalFragmentId aPlus, LogicalFragmentId aMinus) = CutAndPublish(ledger, root, 0);
            (CutOperationId b, LogicalFragmentId bPlus, LogicalFragmentId bMinus) = CutAndPublish(ledger, aPlus, 1);
            (CutOperationId c, LogicalFragmentId cPlus, _) = CutAndPublish(ledger, bMinus, 2);

            List<VpClipCandidate> deep = Collect(ledger, cPlus);
            Assert.That(deep.Count, Is.EqualTo(3));
            Assert.That(deep[0].boundary, Is.EqualTo(B(ledger, a, 1f)));
            Assert.That(deep[1].boundary, Is.EqualTo(B(ledger, b, -1f)));
            Assert.That(deep[2].boundary, Is.EqualTo(B(ledger, c, 1f)));
            Assert.That(deep[0].requires.IsSet, Is.False, "the first requires nothing");
            Assert.That(deep[1].requires, Is.EqualTo(deep[0].boundary));
            Assert.That(deep[2].requires, Is.EqualTo(deep[1].boundary));
            Assert.That(deep[1].plane, Is.EqualTo(Plane(1)), "the plane the ledger holds");

            List<VpClipCandidate> sibling = Collect(ledger, aMinus);
            Assert.That(sibling.Count, Is.EqualTo(1), "the other child of A");
            Assert.That(sibling[0].boundary, Is.EqualTo(B(ledger, a, -1f)));

            List<VpClipCandidate> middle = Collect(ledger, bPlus);
            Assert.That(middle.Count, Is.EqualTo(2), "B's positive child: A and B, not C");
            Assert.That(middle[1].boundary, Is.EqualTo(B(ledger, b, 1f)));
        }

        /// <summary>
        /// A pending cut drawn as one side has that boundary, pending, last on its chain; published, the child on that
        /// side has the same boundary -- the same face, side and plane -- once, no longer pending. A pending cut on a
        /// cut child keeps the ancestors before it.
        /// </summary>
        [Test]
        public void PendingToPublished_KeepsTheBoundary_WithoutADuplicate()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            CutOperationId a = AdmitAndPrepare(ledger, root, 0);

            List<VpClipCandidate> pending = Collect(ledger, root, 1f);
            Assert.That(pending.Count, Is.EqualTo(1));
            Assert.That(pending[0].boundary, Is.EqualTo(B(ledger, a, 1f)));
            Assert.That(pending[0].pending, Is.True);
            Assert.That(Collect(ledger, root, 0f), Is.Empty, "drawn whole while pending: no boundary");

            Assert.That(ledger.Publish(a, out LogicalFragmentId aPlus, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
            List<VpClipCandidate> published = Collect(ledger, aPlus);
            Assert.That(published.Count, Is.EqualTo(1), "one boundary, not two");
            Assert.That(published[0].boundary, Is.EqualTo(pending[0].boundary), "the same face and side");
            Assert.That(published[0].plane, Is.EqualTo(pending[0].plane), "the same plane");
            Assert.That(published[0].pending, Is.False);
            Assert.That(VpClipCandidates.TryCollect(ledger, root, 1f, Array.Empty<VpClipBoundary>(), new List<VpClipCandidate>()), Is.False,
                "the replaced root is not drawn any more");

            CutOperationId b = AdmitAndPrepare(ledger, aPlus, 1);
            List<VpClipCandidate> onChild = Collect(ledger, aPlus, -1f);
            Assert.That(onChild.Count, Is.EqualTo(2));
            Assert.That(onChild[0].boundary, Is.EqualTo(B(ledger, a, 1f)));
            Assert.That(onChild[1].boundary, Is.EqualTo(B(ledger, b, -1f)));
            Assert.That(onChild[1].requires, Is.EqualTo(onChild[0].boundary));
        }

        // ----- what the drawn geometry reflects -------------------------------------------------------------------

        /// <summary>
        /// A chain of nine: the ninth is Ignored. Once the drawn geometry reflects the first boundary, that boundary
        /// leaves the candidates, and the ninth is selected in the place it freed.
        /// </summary>
        [Test]
        public void AReflectedAncestor_FreesItsPlace_ForTheBoundaryThatNeedsIt()
        {
            LogicalCutLedger ledger = NewLedger();
            (List<CutOperationId> cuts, List<float> sides, LogicalFragmentId leaf) = Chain(ledger, 9, 1f);

            List<VpClipCandidate> before = Collect(ledger, leaf);
            VpClipSelectionState[] beforeStates = Select(before, out _);
            Assert.That(beforeStates[8], Is.EqualTo(VpClipSelectionState.IgnoredCapacity), "the ninth, before");

            List<VpClipCandidate> after = Collect(ledger, leaf, 0f, B(ledger, cuts[0], sides[0]));
            Assert.That(after.Count, Is.EqualTo(8), "the reflected boundary is not a candidate");
            VpClipSelectionState[] afterStates = Select(after, out int selected);
            Assert.That(selected, Is.EqualTo(8));
            Assert.That(after[7].boundary, Is.EqualTo(B(ledger, cuts[8], sides[8])), "the ninth boundary");
            Assert.That(afterStates[7], Is.EqualTo(VpClipSelectionState.Selected), "is now selected");
            Assert.That(after[0].requires.IsSet, Is.False, "the second now requires nothing reflected");
        }

        /// <summary>
        /// The positive side reflected: the positive child has no candidate, and the negative child keeps its own.
        /// </summary>
        [Test]
        public void ThePositiveSideReflected_LeavesTheNegativeSide()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            (CutOperationId a, LogicalFragmentId aPlus, LogicalFragmentId aMinus) = CutAndPublish(ledger, root, 0);
            VpClipBoundary positive = B(ledger, a, 1f);

            Assert.That(Collect(ledger, aPlus, 0f, positive), Is.Empty, "reflected in the positive child's geometry");
            List<VpClipCandidate> negative = Collect(ledger, aMinus, 0f, positive);
            Assert.That(negative.Count, Is.EqualTo(1), "the negative child keeps its boundary");
            Assert.That(negative[0].boundary, Is.EqualTo(B(ledger, a, -1f)));
        }

        /// <summary>
        /// Completed and Terminated are not read: with nothing reflected in the drawn geometry, both boundaries stay.
        /// </summary>
        [Test]
        public void CompletedOrTerminated_DoesNotRemoveABoundaryTheGeometryDoesNotReflect()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            (CutOperationId a, LogicalFragmentId aPlus, _) = CutAndPublish(ledger, root, 0);
            (CutOperationId b, LogicalFragmentId bPlus, _) = CutAndPublish(ledger, aPlus, 1);
            Assert.That(ledger.CompleteGeometry(a), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.Terminate(b), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.TryGetOperation(a, out LogicalCutOperation first), Is.True);
            Assert.That(first.state, Is.EqualTo(LogicalCutOperationState.Completed));

            List<VpClipCandidate> candidates = Collect(ledger, bPlus);
            Assert.That(candidates.Count, Is.EqualTo(2), "a Completed and a Terminated boundary, neither reflected");
            Assert.That(candidates[0].boundary, Is.EqualTo(B(ledger, a, 1f)));
            Assert.That(candidates[1].boundary, Is.EqualTo(B(ledger, b, 1f)));
        }

        /// <summary>Operation 1 of another ledger is another face: stating it reflected removes nothing here.</summary>
        [Test]
        public void AReflectedBoundaryOfAnotherLedger_IsAnotherBoundary()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalCutLedger other = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            (CutOperationId a, LogicalFragmentId aPlus, _) = CutAndPublish(ledger, root, 0);
            (CutOperationId sameNumber, _, _) = CutAndPublish(other, other.AddFragment(), 0);
            Assert.That(sameNumber.value, Is.EqualTo(a.value), "the layout: the same number in both ledgers");

            List<VpClipCandidate> candidates = Collect(ledger, aPlus, 0f, B(other, sameNumber, 1f));
            Assert.That(candidates.Count, Is.EqualTo(1), "still a candidate");
        }

        // ----- order, written out --------------------------------------------------------------------------------

        private static VpClipCandidate W(LogicalCutLedger scope, int number, int requires)
        {
            return new VpClipCandidate(
                B(scope, new CutOperationId(number), 1f), Plane(number), false,
                requires > 0 ? B(scope, new CutOperationId(requires), 1f) : default);
        }

        /// <summary>
        /// Written out, an order the ledger cannot produce. X4 requires X3, which comes after it: X4 and everything after
        /// it are Ignored, X3 is not moved up, and X5 -- whose requirement X2 is selected -- is not brought forward
        /// either. A requirement not listed at all stops the prefix the same way.
        /// </summary>
        [Test]
        public void AnOutOfOrderInput_IsIgnoredFromTheViolationOn_AndNotReordered()
        {
            LogicalCutLedger scope = NewLedger();
            VpClipCandidate[] outOfOrder = { W(scope, 1, 0), W(scope, 2, 1), W(scope, 4, 3), W(scope, 3, 2), W(scope, 5, 2) };
            VpClipCandidate[] copy = (VpClipCandidate[])outOfOrder.Clone();
            VpClipSelectionState[] states = Select(outOfOrder, out int selected);
            Assert.That(selected, Is.EqualTo(2));
            Assert.That(states, Is.EqualTo(new[]
            {
                VpClipSelectionState.Selected, VpClipSelectionState.Selected, VpClipSelectionState.IgnoredOrder,
                VpClipSelectionState.IgnoredOrder, VpClipSelectionState.IgnoredOrder,
            }));
            Assert.That(outOfOrder, Is.EqualTo(copy), "the input is not reordered or changed");

            VpClipCandidate[] missing = { W(scope, 1, 0), W(scope, 3, 2), W(scope, 4, 3) };
            VpClipSelectionState[] missingStates = Select(missing, out int missingSelected);
            Assert.That(missingSelected, Is.EqualTo(1));
            Assert.That(missingStates[1], Is.EqualTo(VpClipSelectionState.IgnoredOrder), "its requirement is not listed");
            Assert.That(missingStates[2], Is.EqualTo(VpClipSelectionState.IgnoredOrder));
        }

        // ----- nothing is written ---------------------------------------------------------------------------------

        /// <summary>Collecting and selecting change nothing in the ledger, the reflected set or the candidates.</summary>
        [Test]
        public void CollectingAndSelecting_WriteNothing()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment(new List<float3> { new float3(0f, 0.05f, 0f) });
            (CutOperationId a, LogicalFragmentId aPlus, _) = CutAndPublish(ledger, root, 0);
            AdmitAndPrepare(ledger, aPlus, 1);

            int operations = ledger.OperationCount;
            int fragments = ledger.FragmentCount;
            int incomplete = ledger.Budget.IncompleteCutOperationCount;
            var statesBefore = new List<LogicalCutOperationState>();
            for (int i = 0; ledger.TryGetOperationAtAdmission(i, out LogicalCutOperation operation); i++)
            {
                statesBefore.Add(operation.state);
            }

            Assert.That(ledger.TryGetAnchorCount(aPlus, out int anchors), Is.True);
            var reflected = new List<VpClipBoundary> { B(ledger, a, -1f) };
            List<VpClipCandidate> candidates = Collect(ledger, aPlus, 1f, reflected.ToArray());
            var copy = new List<VpClipCandidate>(candidates);
            Select(candidates, out _);

            Assert.That(ledger.OperationCount, Is.EqualTo(operations));
            Assert.That(ledger.FragmentCount, Is.EqualTo(fragments));
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(incomplete), "no budget taken or returned");
            for (int i = 0; ledger.TryGetOperationAtAdmission(i, out LogicalCutOperation operation); i++)
            {
                Assert.That(operation.state, Is.EqualTo(statesBefore[i]), "operation " + i + " unchanged");
            }

            Assert.That(ledger.TryGetAnchorCount(aPlus, out int anchorsAfter), Is.True);
            Assert.That(anchorsAfter, Is.EqualTo(anchors), "anchors unchanged");
            Assert.That(ledger.TryGetActiveOperation(aPlus, out _), Is.True, "the pending cut is still pending");
            Assert.That(candidates, Is.EqualTo(copy), "the candidates are as collected");
            Assert.That(reflected.Count, Is.EqualTo(1));
        }

        /// <summary>
        /// There is no default for what the drawn geometry reflects, and a side is 0, +1 or -1. Asking for a pending side
        /// of a fragment with no pending cut lists nothing.
        /// </summary>
        [Test]
        public void MissingOrMalformedInput_IsRefused()
        {
            LogicalCutLedger ledger = NewLedger();
            LogicalFragmentId root = ledger.AddFragment();
            var into = new List<VpClipCandidate>();
            Assert.Throws<ArgumentNullException>(() => VpClipCandidates.TryCollect(ledger, root, 0f, null, into));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => VpClipCandidates.TryCollect(ledger, root, 0.5f, Array.Empty<VpClipBoundary>(), into));
            Assert.That(VpClipCandidates.TryCollect(ledger, root, 1f, Array.Empty<VpClipBoundary>(), into), Is.False,
                "no pending cut to be one side of");
            Assert.That(into, Is.Empty);
        }
    }
}
