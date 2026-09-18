using System.Collections.Generic;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The logical admission and publication of cuts as pure data (DESIGN 4.2, 7.1, 7.7, 8), driven by synthetic
    /// outcomes: the harness here decides whether a cut is a no-op, whether final physics is established on both sides
    /// or not, when geometry responsibility is done, and when a source's ownership changed under an active cut. Nothing
    /// of geometry, display, jobs or physics is exercised; what is checked is identity, state, authority, the shared
    /// incomplete budget, and the owner anchor sets the ledger carries through a publication.
    /// <para>
    /// Anchors are read back through the ledger's own queries, not through a test-side table: the publication itself is
    /// what gives each child its set.
    /// </para>
    /// </summary>
    public class LogicalCutLedgerTests
    {
        // s = y - 0.25 for this plane, so y = 4 is positive, y = -4 negative, y = 0.25 exactly on it.
        private static readonly float4 k_plane = new float4(0f, 1f, 0f, -0.25f);
        private static readonly float4 k_otherPlane = new float4(1f, 0f, 0f, 0.5f);
        private const float k_epsilon = 0.01f;

        private static readonly float3 k_high = new float3(0f, 4f, 0f);
        private static readonly float3 k_onPlane = new float3(1f, 0.25f, 1f);
        private static readonly float3 k_low = new float3(0f, -4f, 0f);

        private static LogicalCutLedger NewLedger(int maxIncomplete)
        {
            return new LogicalCutLedger(new LogicalCutIncompleteBudget(maxIncomplete));
        }

        private static CutOperationId AdmitOrFail(LogicalCutLedger ledger, LogicalFragmentId source, float4 plane)
        {
            LogicalCutAdmission admission = ledger.Admit(source, plane, true, out CutOperationId operation);
            Assert.That(admission, Is.EqualTo(LogicalCutAdmission.Admitted), "admission of " + source);
            Assert.That(operation.IsSet, Is.True, "an admitted request has an id");
            return operation;
        }

        /// <summary>
        /// Prepares the cut's anchor distribution and then publishes it, which is the order the product requires. Most
        /// tests here are about something else and use this; the ones about preparation call the two entrances
        /// themselves.
        /// </summary>
        private static LogicalCutResultOutcome PublishAfterPreparing(
            LogicalCutLedger ledger, CutOperationId cut, out LogicalFragmentId positive, out LogicalFragmentId negative)
        {
            ledger.PrepareAnchorDistribution(cut, k_epsilon, out _);
            return ledger.Publish(cut, out positive, out negative);
        }

        private static LogicalCutOperation OperationOf(LogicalCutLedger ledger, CutOperationId id)
        {
            Assert.That(ledger.TryGetOperation(id, out LogicalCutOperation operation), Is.True, "operation " + id + " exists");
            return operation;
        }

        private static LogicalFragmentState StateOf(LogicalCutLedger ledger, LogicalFragmentId id)
        {
            Assert.That(ledger.TryGetFragmentState(id, out LogicalFragmentState state), Is.True, "fragment " + id + " exists");
            return state;
        }

        private static List<float3> AnchorsOf(LogicalCutLedger ledger, LogicalFragmentId id)
        {
            var into = new List<float3>();
            Assert.That(ledger.TryGetAnchors(id, into), Is.True, "fragment " + id + " exists");
            return into;
        }

        [Test]
        public void ASuccessfulFinal_PublishesBothChildrenAndTheOperationTogether_AndTheParentStopsBeingATarget()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId parent = ledger.AddFragment();
            CutOperationId cut = AdmitOrFail(ledger, parent, k_plane);
            Assert.That(ledger.IsCurrentTarget(parent), Is.True, "the parent is a target while its cut is pending");
            Assert.That(ledger.TryGetActiveOperation(parent, out CutOperationId active) && active == cut, Is.True, "and the cut is its active operation");

            Assert.That(PublishAfterPreparing(ledger, cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied));

            // the whole new state, in one observation
            Assert.That(positive.IsSet && negative.IsSet && positive != negative, Is.True, "two distinct children");
            Assert.That(StateOf(ledger, positive), Is.EqualTo(LogicalFragmentState.Live), "the positive child is live");
            Assert.That(StateOf(ledger, negative), Is.EqualTo(LogicalFragmentState.Live), "and so is the negative one");
            LogicalCutOperation operation = OperationOf(ledger, cut);
            Assert.That(operation.state, Is.EqualTo(LogicalCutOperationState.Published));
            Assert.That(operation.source, Is.EqualTo(parent), "the operation names its parent");
            Assert.That(operation.positive, Is.EqualTo(positive), "its positive child");
            Assert.That(operation.negative, Is.EqualTo(negative), "and its negative child");
            Assert.That(operation.plane, Is.EqualTo(k_plane), "with the adopted plane");

            Assert.That(StateOf(ledger, parent), Is.EqualTo(LogicalFragmentState.Replaced), "the parent is replaced");
            Assert.That(ledger.IsCurrentTarget(parent), Is.False, "and is no longer a target");
            Assert.That(ledger.TryGetActiveOperation(parent, out _), Is.False, "with no active operation left on it");
            Assert.That(ledger.Admit(parent, k_otherPlane, true, out _), Is.EqualTo(LogicalCutAdmission.SourceNotLive), "so nothing more is admitted against it");
        }

        [Test]
        public void AFinalThatFailsOnOneSide_AbortsWithoutPublishingEitherSide_AndRetiresTheSource()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId source = ledger.AddFragment();
            CutOperationId cut = AdmitOrFail(ledger, source, k_plane);
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1));
            int fragmentsBefore = ledger.FragmentCount;

            Assert.That(ledger.Abort(cut), Is.EqualTo(LogicalCutResultOutcome.Applied));

            Assert.That(ledger.FragmentCount, Is.EqualTo(fragmentsBefore), "no child was published");
            LogicalCutOperation operation = OperationOf(ledger, cut);
            Assert.That(operation.state, Is.EqualTo(LogicalCutOperationState.Aborted));
            Assert.That(operation.positive.IsSet || operation.negative.IsSet, Is.False, "the operation has no children");
            Assert.That(StateOf(ledger, source), Is.EqualTo(LogicalFragmentState.Retired), "the source is retired");
            Assert.That(ledger.IsCurrentTarget(source), Is.False);
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.Zero, "and the unit came back once");
            Assert.That(ledger.Admit(source, k_otherPlane, true, out _), Is.EqualTo(LogicalCutAdmission.SourceNotLive), "a retired source admits nothing");
        }

        [Test]
        public void ASourceWithAnActiveCut_SkipsANewRequest_WhileAnotherFragmentIsStillAdmitted()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId first = ledger.AddFragment();
            LogicalFragmentId second = ledger.AddFragment();
            CutOperationId cut = AdmitOrFail(ledger, first, k_plane);
            int operationsBefore = ledger.OperationCount;

            Assert.That(ledger.Admit(first, k_otherPlane, true, out CutOperationId skipped), Is.EqualTo(LogicalCutAdmission.SourceActive));
            Assert.That(skipped.IsSet, Is.False, "a skipped request gets no id");
            Assert.That(ledger.OperationCount, Is.EqualTo(operationsBefore), "and consumed none");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "and took nothing");
            Assert.That(ledger.TryGetActiveOperation(first, out CutOperationId stillActive) && stillActive == cut, Is.True, "the first cut is untouched");

            Assert.That(ledger.Admit(second, k_otherPlane, true, out CutOperationId other), Is.EqualTo(LogicalCutAdmission.Admitted), "another fragment of the same object proceeds");
            Assert.That(other, Is.Not.EqualTo(cut));
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(2));
        }

        [Test]
        public void AfterLogicalPublication_AChildIsAdmitted_WhileTheParentGeometryIsStillIncomplete()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId parent = ledger.AddFragment();
            CutOperationId cut = AdmitOrFail(ledger, parent, k_plane);
            PublishAfterPreparing(ledger, cut, out LogicalFragmentId positive, out _);
            Assert.That(OperationOf(ledger, cut).IsIncomplete, Is.True, "geometry responsibility is still open");

            Assert.That(ledger.Admit(positive, k_otherPlane, true, out CutOperationId childCut), Is.EqualTo(LogicalCutAdmission.Admitted), "the child is admitted before the parent geometry completes");
            Assert.That(OperationOf(ledger, childCut).source, Is.EqualTo(positive));
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(2), "both the parent's and the child's cut are incomplete");
        }

        [Test]
        public void AFullBudget_ANoOp_AndASkip_LeaveTheBudgetAndTheIdsAlone()
        {
            LogicalCutLedger ledger = NewLedger(1);
            LogicalFragmentId first = ledger.AddFragment();
            LogicalFragmentId second = ledger.AddFragment();
            LogicalFragmentId third = ledger.AddFragment();

            // a no-op on a free source: classified before the limit is even looked at
            Assert.That(ledger.Admit(first, k_plane, false, out CutOperationId noOp), Is.EqualTo(LogicalCutAdmission.NoOp));
            Assert.That(noOp.IsSet, Is.False);
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.Zero, "a no-op takes nothing");
            Assert.That(ledger.TryGetActiveOperation(first, out _), Is.False, "and leaves the source without an active operation");

            CutOperationId cut = AdmitOrFail(ledger, first, k_plane);
            Assert.That(ledger.Budget.IsFull, Is.True, "at the limit");

            Assert.That(ledger.Admit(second, k_plane, true, out CutOperationId full), Is.EqualTo(LogicalCutAdmission.Full));
            Assert.That(full.IsSet, Is.False, "a request skipped for capacity gets no id");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "and takes nothing");
            Assert.That(ledger.TryGetActiveOperation(second, out _), Is.False, "nor is kept on its source");

            Assert.That(ledger.Admit(third, k_plane, false, out _), Is.EqualTo(LogicalCutAdmission.NoOp), "a no-op while full is still reported as a no-op, not as full");
            Assert.That(ledger.Admit(first, k_plane, true, out _), Is.EqualTo(LogicalCutAdmission.SourceActive), "and an active source is skipped before the limit is looked at");

            // the skipped requests were not saved: room appearing later does not revive them
            PublishAfterPreparing(ledger, cut, out _, out _);
            ledger.CompleteGeometry(cut);
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.Zero);
            Assert.That(ledger.TryGetActiveOperation(second, out _), Is.False, "the second fragment's skipped request did not come back");
            Assert.That(ledger.OperationCount, Is.EqualTo(1), "only the one admitted operation ever got an id");
        }

        /// <summary>
        /// DESIGN 7.7's limit is over every target: two objects' ledgers sharing one budget fill it between them, and
        /// each terminal ending gives its one unit back exactly once, from whichever ledger it belongs to.
        /// </summary>
        [Test]
        public void TheBudget_IsSharedAcrossObjects_AndEachEndingReturnsItsUnitOnce()
        {
            var budget = new LogicalCutIncompleteBudget(2);
            var one = new LogicalCutLedger(budget);
            var two = new LogicalCutLedger(budget);
            LogicalFragmentId a = one.AddFragment();
            LogicalFragmentId b = one.AddFragment();
            LogicalFragmentId c = two.AddFragment();
            LogicalFragmentId d = two.AddFragment();

            CutOperationId cutA = AdmitOrFail(one, a, k_plane);
            CutOperationId cutC = AdmitOrFail(two, c, k_plane);
            Assert.That(budget.IncompleteCutOperationCount, Is.EqualTo(2), "one from each object");
            Assert.That(one.Admit(b, k_plane, true, out _), Is.EqualTo(LogicalCutAdmission.Full), "the other object's admission filled this one");
            Assert.That(two.Admit(d, k_plane, true, out _), Is.EqualTo(LogicalCutAdmission.Full), "and vice versa");

            // publication returns nothing
            Assert.That(PublishAfterPreparing(one, cutA, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(budget.IncompleteCutOperationCount, Is.EqualTo(2), "logical publication gives no unit back");
            Assert.That(two.Admit(d, k_plane, true, out _), Is.EqualTo(LogicalCutAdmission.Full), "so the other object is still full");

            // completion returns one unit, once, and the other object can use it
            Assert.That(one.CompleteGeometry(cutA), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(budget.IncompleteCutOperationCount, Is.EqualTo(1));
            Assert.That(one.CompleteGeometry(cutA), Is.EqualTo(LogicalCutResultOutcome.NotActive));
            Assert.That(budget.IncompleteCutOperationCount, Is.EqualTo(1), "a duplicate completion returns nothing");
            CutOperationId cutD = AdmitOrFail(two, d, k_plane);
            Assert.That(budget.IsFull, Is.True, "and the freed unit went to the other object");

            // abort returns one unit, once
            Assert.That(two.Abort(cutC), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(budget.IncompleteCutOperationCount, Is.EqualTo(1));
            Assert.That(two.Abort(cutC), Is.EqualTo(LogicalCutResultOutcome.NotActive));
            Assert.That(budget.IncompleteCutOperationCount, Is.EqualTo(1), "a duplicate abort returns nothing");

            // stale returns one unit, once
            two.NoteOwnershipChanged(d);
            Assert.That(PublishAfterPreparing(two, cutD, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Stale));
            Assert.That(budget.IncompleteCutOperationCount, Is.Zero);
            Assert.That(two.Publish(cutD, out _, out _), Is.EqualTo(LogicalCutResultOutcome.NotActive));
            Assert.That(budget.IncompleteCutOperationCount, Is.Zero, "a second arrival of the stale result returns nothing");

            // both objects admit again from the freed budget
            Assert.That(one.Admit(b, k_plane, true, out _), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(two.Admit(d, k_plane, true, out _), Is.EqualTo(LogicalCutAdmission.Admitted));
            Assert.That(budget.IsFull, Is.True);
        }

        [Test]
        public void TheBudget_IsUnchangedByPublication_AndComesDownOnceOnCompletionOrTermination()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId a = ledger.AddFragment();
            LogicalFragmentId b = ledger.AddFragment();
            CutOperationId cutA = AdmitOrFail(ledger, a, k_plane);
            CutOperationId cutB = AdmitOrFail(ledger, b, k_plane);
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(2));

            PublishAfterPreparing(ledger, cutA, out _, out _);
            PublishAfterPreparing(ledger, cutB, out _, out _);
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(2), "logical publication does not bring the count down");

            Assert.That(ledger.CompleteGeometry(cutA), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "completion brings it down");
            Assert.That(ledger.CompleteGeometry(cutA), Is.EqualTo(LogicalCutResultOutcome.NotActive), "a second completion is refused");
            Assert.That(ledger.Terminate(cutA), Is.EqualTo(LogicalCutResultOutcome.NotActive), "and so is a termination after it");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "with the count untouched");

            Assert.That(ledger.Terminate(cutB), Is.EqualTo(LogicalCutResultOutcome.Applied), "a terminal notice closes the other one");
            Assert.That(OperationOf(ledger, cutB).state, Is.EqualTo(LogicalCutOperationState.Terminated));
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.Zero);
            Assert.That(ledger.CompleteGeometry(cutB), Is.EqualTo(LogicalCutResultOutcome.NotActive), "and completion after termination changes nothing");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.Zero);
        }

        [Test]
        public void AGeometryNotice_ForACutNeverPublished_IsRefused()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId source = ledger.AddFragment();
            CutOperationId cut = AdmitOrFail(ledger, source, k_plane);

            Assert.That(ledger.CompleteGeometry(cut), Is.EqualTo(LogicalCutResultOutcome.NotActive), "there is no geometry responsibility before publication");
            Assert.That(ledger.Terminate(cut), Is.EqualTo(LogicalCutResultOutcome.NotActive));
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "and the count is untouched");
            Assert.That(OperationOf(ledger, cut).state, Is.EqualTo(LogicalCutOperationState.Admitted), "the cut is still active");
        }

        [Test]
        public void DuplicateAndLateResults_NeverPublishTwice_OrDecrementTwice()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId source = ledger.AddFragment();
            CutOperationId cut = AdmitOrFail(ledger, source, k_plane);

            PublishAfterPreparing(ledger, cut, out LogicalFragmentId positive, out LogicalFragmentId negative);
            int fragmentsAfterPublish = ledger.FragmentCount;

            Assert.That(ledger.Publish(cut, out LogicalFragmentId again, out _), Is.EqualTo(LogicalCutResultOutcome.NotActive), "a duplicate success is refused");
            Assert.That(again.IsSet, Is.False);
            Assert.That(ledger.FragmentCount, Is.EqualTo(fragmentsAfterPublish), "and no third child appeared");
            Assert.That(ledger.Abort(cut), Is.EqualTo(LogicalCutResultOutcome.NotActive), "a late failure after success is refused");
            Assert.That(StateOf(ledger, positive), Is.EqualTo(LogicalFragmentState.Live), "the children stand");
            Assert.That(StateOf(ledger, negative), Is.EqualTo(LogicalFragmentState.Live));
            Assert.That(StateOf(ledger, source), Is.EqualTo(LogicalFragmentState.Replaced), "and the parent was not retired by it");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "the count still holds the open geometry responsibility");

            // the other order: aborted first, success arriving late
            LogicalFragmentId other = ledger.AddFragment();
            CutOperationId otherCut = AdmitOrFail(ledger, other, k_plane);
            ledger.Abort(otherCut);
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1));
            Assert.That(ledger.Publish(otherCut, out _, out _), Is.EqualTo(LogicalCutResultOutcome.NotActive), "a late success after abort is refused");
            Assert.That(ledger.Abort(otherCut), Is.EqualTo(LogicalCutResultOutcome.NotActive), "and so is a duplicate abort");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "with no second decrement");
            Assert.That(StateOf(ledger, other), Is.EqualTo(LogicalFragmentState.Retired));
        }

        [Test]
        public void AStaleResult_IsReclaimedOnce_WithoutRetiringTheSourceOrAnyOtherFragment()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId source = ledger.AddFragment();
            LogicalFragmentId bystander = ledger.AddFragment();
            CutOperationId cut = AdmitOrFail(ledger, source, k_plane);
            CutOperationId bystanderCut = AdmitOrFail(ledger, bystander, k_otherPlane);
            int fragmentsBefore = ledger.FragmentCount;

            // something other than this cut changed the source's ownership while the cut was in flight
            ledger.NoteOwnershipChanged(source);

            Assert.That(PublishAfterPreparing(ledger, cut, out LogicalFragmentId positive, out _), Is.EqualTo(LogicalCutResultOutcome.Stale), "the late success is stale");
            Assert.That(positive.IsSet, Is.False, "nothing was published");
            Assert.That(ledger.FragmentCount, Is.EqualTo(fragmentsBefore));
            Assert.That(OperationOf(ledger, cut).state, Is.EqualTo(LogicalCutOperationState.Stale));
            Assert.That(StateOf(ledger, source), Is.EqualTo(LogicalFragmentState.Live), "the current source is not retired by a stale result");
            Assert.That(ledger.IsCurrentTarget(source), Is.True);
            Assert.That(ledger.TryGetActiveOperation(source, out _), Is.False, "the stale operation is no longer active on it");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "the stale one gave its unit back once");

            Assert.That(ledger.Publish(cut, out _, out _), Is.EqualTo(LogicalCutResultOutcome.NotActive), "a second arrival of the same result changes nothing");
            Assert.That(ledger.Abort(cut), Is.EqualTo(LogicalCutResultOutcome.NotActive));
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "and does not decrement again");

            // the other fragment's cut is untouched by the source's change
            Assert.That(StateOf(ledger, bystander), Is.EqualTo(LogicalFragmentState.Live));
            Assert.That(PublishAfterPreparing(ledger, bystanderCut, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied), "another fragment's cut is not stale for it");

            // the source can be cut again, and a result for the new cut is not stale
            Assert.That(ledger.Admit(source, k_plane, true, out CutOperationId retry), Is.EqualTo(LogicalCutAdmission.Admitted), "the source accepts a new cut after the stale one");
            Assert.That(PublishAfterPreparing(ledger, retry, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied), "whose result is current");
        }

        [Test]
        public void AStaleFailure_IsReclaimedTheSameWay_AndDoesNotRetireTheSource()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId source = ledger.AddFragment();
            CutOperationId cut = AdmitOrFail(ledger, source, k_plane);
            ledger.NoteOwnershipChanged(source);

            Assert.That(ledger.Abort(cut), Is.EqualTo(LogicalCutResultOutcome.Stale), "a stale failure is not an abort");
            Assert.That(StateOf(ledger, source), Is.EqualTo(LogicalFragmentState.Live), "the source is not retired");
            Assert.That(OperationOf(ledger, cut).state, Is.EqualTo(LogicalCutOperationState.Stale));
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.Zero);
        }

        [Test]
        public void AnOwnershipChange_OnAnotherFragment_DoesNotMakeThisResultStale()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId source = ledger.AddFragment();
            LogicalFragmentId other = ledger.AddFragment();
            CutOperationId cut = AdmitOrFail(ledger, source, k_plane);

            ledger.NoteOwnershipChanged(other);

            Assert.That(PublishAfterPreparing(ledger, cut, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied), "only the source's own authority is consulted");
        }

        [Test]
        public void Ids_AreNeverReused()
        {
            LogicalCutLedger ledger = NewLedger(8);
            LogicalFragmentId root = ledger.AddFragment();

            // fragments and operations through every ending: abort, stale, completion, termination
            CutOperationId aborted = AdmitOrFail(ledger, root, k_plane);
            ledger.Abort(aborted);

            LogicalFragmentId a = ledger.AddFragment();
            CutOperationId stale = AdmitOrFail(ledger, a, k_plane);
            ledger.NoteOwnershipChanged(a);
            PublishAfterPreparing(ledger, stale, out _, out _);

            CutOperationId completed = AdmitOrFail(ledger, a, k_plane);
            PublishAfterPreparing(ledger, completed, out LogicalFragmentId aPositive, out LogicalFragmentId aNegative);
            ledger.CompleteGeometry(completed);

            CutOperationId terminated = AdmitOrFail(ledger, aPositive, k_otherPlane);
            PublishAfterPreparing(ledger, terminated, out LogicalFragmentId grandPositive, out LogicalFragmentId grandNegative);
            ledger.Terminate(terminated);

            CutOperationId next = AdmitOrFail(ledger, aNegative, k_plane);
            PublishAfterPreparing(ledger, next, out LogicalFragmentId nextPositive, out LogicalFragmentId nextNegative);

            var operations = new[] { aborted, stale, completed, terminated, next };
            for (int i = 0; i < operations.Length; i++)
            {
                for (int j = i + 1; j < operations.Length; j++)
                {
                    Assert.That(operations[i], Is.Not.EqualTo(operations[j]), "operation ids " + i + " and " + j);
                }

                Assert.That(operations[i].value, Is.EqualTo(i + 1), "ids are issued in order and none was skipped or taken back");
            }

            var fragments = new[] { root, a, aPositive, aNegative, grandPositive, grandNegative, nextPositive, nextNegative };
            for (int i = 0; i < fragments.Length; i++)
            {
                for (int j = i + 1; j < fragments.Length; j++)
                {
                    Assert.That(fragments[i], Is.Not.EqualTo(fragments[j]), "fragment ids " + i + " and " + j);
                }
            }

            Assert.That(ledger.FragmentCount, Is.EqualTo(fragments.Length), "every fragment id issued is accounted for, and the retired and replaced ones still hold theirs");
            Assert.That(StateOf(ledger, root), Is.EqualTo(LogicalFragmentState.Retired));
            Assert.That(StateOf(ledger, a), Is.EqualTo(LogicalFragmentState.Replaced));
        }

        [Test]
        public void AdmissionChecks_RunInTheSpecifiedOrder()
        {
            LogicalCutLedger ledger = NewLedger(1);
            LogicalFragmentId retired = ledger.AddFragment();
            LogicalFragmentId live = ledger.AddFragment();
            LogicalFragmentId other = ledger.AddFragment();
            ledger.Abort(AdmitOrFail(ledger, retired, k_plane));

            // a source that is not live is reported as such before its classification is looked at
            Assert.That(ledger.Admit(retired, k_plane, false, out _), Is.EqualTo(LogicalCutAdmission.SourceNotLive));

            // an active source is reported before its classification
            AdmitOrFail(ledger, live, k_plane);
            Assert.That(ledger.Admit(live, k_plane, false, out _), Is.EqualTo(LogicalCutAdmission.SourceActive));

            // the classification comes before the limit
            Assert.That(ledger.Budget.IsFull, Is.True, "the budget is full");
            Assert.That(ledger.Admit(other, k_plane, false, out _), Is.EqualTo(LogicalCutAdmission.NoOp));
            Assert.That(ledger.Admit(other, k_plane, true, out _), Is.EqualTo(LogicalCutAdmission.Full));
        }

        /// <summary>
        /// Zero is unset and only a positive value is an id (DESIGN 8: 0を未設定に予約). A negative value cannot be
        /// constructed, and an id that is unset or was never issued names nothing at any entrance — query, admission,
        /// result, notice — and changes neither state nor the budget.
        /// </summary>
        [Test]
        public void AnIdThatIsUnsetNegativeOrNeverIssued_NamesNothing_AndChangesNothing()
        {
            Assert.That(() => new LogicalFragmentId(-1), Throws.TypeOf<System.ArgumentOutOfRangeException>(), "a negative fragment id is refused where it is made");
            Assert.That(() => new CutOperationId(-1), Throws.TypeOf<System.ArgumentOutOfRangeException>(), "and so is a negative operation id");
            Assert.That(new LogicalFragmentId(0).IsSet, Is.False, "zero is unset");
            Assert.That(new CutOperationId(0).IsSet, Is.False);
            Assert.That(default(LogicalFragmentId).IsSet, Is.False, "and so is default");

            LogicalCutLedger ledger = NewLedger(2);
            LogicalFragmentId source = ledger.AddFragment();
            CutOperationId cut = AdmitOrFail(ledger, source, k_plane);
            int budgetBefore = ledger.Budget.IncompleteCutOperationCount;
            int fragmentsBefore = ledger.FragmentCount;
            int operationsBefore = ledger.OperationCount;
            var into = new List<float3>();

            var fragments = new[] { default(LogicalFragmentId), new LogicalFragmentId(0), new LogicalFragmentId(2), new LogicalFragmentId(int.MaxValue) };
            foreach (LogicalFragmentId id in fragments)
            {
                Assert.That(ledger.TryGetFragmentState(id, out _), Is.False, id + " has no state");
                Assert.That(ledger.IsCurrentTarget(id), Is.False, id + " is not a target");
                Assert.That(ledger.TryGetActiveOperation(id, out _), Is.False, id + " has no active operation");
                Assert.That(ledger.TryGetAnchors(id, into), Is.False, id + " has no anchor set");
                Assert.That(ledger.TryGetAnchorCount(id, out _), Is.False, id + " has no anchor count");
                Assert.That(ledger.IsFixedOwner(id), Is.False, id + " is not a fixed owner");
                Assert.That(ledger.Admit(id, k_plane, true, out CutOperationId issued), Is.EqualTo(LogicalCutAdmission.SourceNotLive), id + " is not admitted");
                Assert.That(issued.IsSet, Is.False);
                Assert.That(() => ledger.NoteOwnershipChanged(id), Throws.ArgumentException, id + " has no ownership to change");
            }

            var operations = new[] { default(CutOperationId), new CutOperationId(0), new CutOperationId(2), new CutOperationId(int.MaxValue) };
            foreach (CutOperationId id in operations)
            {
                Assert.That(ledger.TryGetOperation(id, out _), Is.False, id + " does not exist");
                Assert.That(ledger.PrepareAnchorDistribution(id, k_epsilon, out _), Is.EqualTo(AnchorPreparationOutcome.OperationNotActive), id + " cannot be prepared");
                Assert.That(ledger.TryGetPreparedAnchorDistribution(id, out _), Is.False, id + " has no prepared distribution");
                Assert.That(ledger.Publish(id, out LogicalFragmentId positive, out _), Is.EqualTo(LogicalCutResultOutcome.NotActive), id + " cannot be published");
                Assert.That(positive.IsSet, Is.False);
                Assert.That(ledger.Abort(id), Is.EqualTo(LogicalCutResultOutcome.NotActive), id + " cannot be aborted");
                Assert.That(ledger.CompleteGeometry(id), Is.EqualTo(LogicalCutResultOutcome.NotActive), id + " cannot complete");
                Assert.That(ledger.Terminate(id), Is.EqualTo(LogicalCutResultOutcome.NotActive), id + " cannot terminate");
            }

            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(budgetBefore), "the budget is untouched");
            Assert.That(ledger.FragmentCount, Is.EqualTo(fragmentsBefore), "no fragment was issued");
            Assert.That(ledger.OperationCount, Is.EqualTo(operationsBefore), "no operation was issued");
            Assert.That(StateOf(ledger, source), Is.EqualTo(LogicalFragmentState.Live), "the real source is as it was");
            Assert.That(OperationOf(ledger, cut).state, Is.EqualTo(LogicalCutOperationState.Admitted), "and so is the real cut");
        }

        [Test]
        public void TheLimit_MustBePositive_AndThePlaneFinite()
        {
            Assert.That(() => new LogicalCutIncompleteBudget(0), Throws.TypeOf<System.ArgumentOutOfRangeException>());
            Assert.That(() => new LogicalCutLedger(null), Throws.TypeOf<System.ArgumentNullException>());
            LogicalCutLedger ledger = NewLedger(1);
            LogicalFragmentId source = ledger.AddFragment();
            Assert.That(() => ledger.Admit(source, new float4(float.NaN, 0f, 0f, 0f), true, out _), Throws.ArgumentException);
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.Zero, "a refused plane admitted nothing");
        }

        // ----- owner anchor sets through the product path ------------------------------------------------------

        /// <summary>
        /// A registered set is the ledger's own copy: changing the list it came from afterwards, or the copy a query
        /// hands back, cannot change what the owner holds. An owner with no anchors is normal and dynamic.
        /// </summary>
        [Test]
        public void ARegisteredAnchorSet_IsTheLedgersOwnCopy_AndAnEmptyOneIsNormal()
        {
            LogicalCutLedger ledger = NewLedger(4);

            LogicalFragmentId none = ledger.AddFragment();
            Assert.That(AnchorsOf(ledger, none), Is.Empty, "a fragment added without anchors holds none");
            Assert.That(ledger.TryGetAnchorCount(none, out int noneCount) && noneCount == 0, Is.True);
            Assert.That(ledger.IsFixedOwner(none), Is.False, "so it is dynamic");

            LogicalFragmentId emptyList = ledger.AddFragment(new List<float3>());
            Assert.That(AnchorsOf(ledger, emptyList), Is.Empty, "and an explicitly empty set is the same thing");
            Assert.That(ledger.IsFixedOwner(emptyList), Is.False);

            var caller = new List<float3> { k_high, k_low };
            LogicalFragmentId owner = ledger.AddFragment(caller);
            Assert.That(AnchorsOf(ledger, owner), Is.EqualTo(new List<float3> { k_high, k_low }), "the set was registered");
            Assert.That(ledger.IsFixedOwner(owner), Is.True, "and the owner is fixed");

            // the caller's own list is not the ledger's
            caller.Clear();
            caller.Add(new float3(99f, 99f, 99f));
            Assert.That(AnchorsOf(ledger, owner), Is.EqualTo(new List<float3> { k_high, k_low }), "changing the caller's list changed nothing");

            // nor is the copy a query returns
            List<float3> queried = AnchorsOf(ledger, owner);
            queried.Clear();
            Assert.That(AnchorsOf(ledger, owner), Is.EqualTo(new List<float3> { k_high, k_low }), "and clearing a returned copy changed nothing");
            Assert.That(ledger.TryGetAnchorCount(owner, out int ownerCount) && ownerCount == 2, Is.True);

            // a non-finite position is the caller's mistake, and nothing is registered for it
            int fragmentsBefore = ledger.FragmentCount;
            Assert.That(
                () => ledger.AddFragment(new List<float3> { new float3(0f, float.NaN, 0f) }),
                Throws.ArgumentException,
                "a non-finite anchor is refused");
            Assert.That(ledger.FragmentCount, Is.EqualTo(fragmentsBefore), "and no fragment was added");
        }

        /// <summary>
        /// The publication itself gives each child its inherited set: positive, negative, and the on-plane anchor to
        /// both, once each. The parent's own set is left as it was.
        /// </summary>
        [Test]
        public void APublishedCut_GivesEachChildItsInheritedAnchors_AndTheirFixity()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId parent = ledger.AddFragment(new List<float3> { k_high, k_onPlane, k_low });
            CutOperationId cut = AdmitOrFail(ledger, parent, k_plane);

            Assert.That(ledger.PrepareAnchorDistribution(cut, k_epsilon, out AnchorDistributionResult prepared), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(prepared.status, Is.EqualTo(AnchorDistributionStatus.Ok));
            Assert.That(prepared.positiveCount, Is.EqualTo(2), "the high anchor and the on-plane one");
            Assert.That(prepared.negativeCount, Is.EqualTo(2), "the low anchor and the on-plane one");
            Assert.That(prepared.IsPositiveFixed && prepared.IsNegativeFixed, Is.True);

            Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied));

            Assert.That(AnchorsOf(ledger, positive), Is.EquivalentTo(new[] { k_high, k_onPlane }), "the positive child inherited its side");
            Assert.That(AnchorsOf(ledger, negative), Is.EquivalentTo(new[] { k_onPlane, k_low }), "and the negative child its own");
            Assert.That(ledger.IsFixedOwner(positive), Is.True, "both children are fixed");
            Assert.That(ledger.IsFixedOwner(negative), Is.True);
            Assert.That(AnchorsOf(ledger, parent), Is.Empty, "and the replaced parent let go of the set it handed on");
            Assert.That(ledger.IsFixedOwner(parent), Is.False, "it is no longer a target, so its fixity means nothing");

            // a cut with the anchors all on one side leaves the other child dynamic
            LogicalFragmentId oneSided = ledger.AddFragment(new List<float3> { k_high });
            CutOperationId oneSidedCut = AdmitOrFail(ledger, oneSided, k_plane);
            Assert.That(PublishAfterPreparing(ledger, oneSidedCut, out LogicalFragmentId above, out LogicalFragmentId below), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(AnchorsOf(ledger, above), Is.EqualTo(new List<float3> { k_high }));
            Assert.That(AnchorsOf(ledger, below), Is.Empty);
            Assert.That(ledger.IsFixedOwner(above), Is.True);
            Assert.That(ledger.IsFixedOwner(below), Is.False, "the side that inherited nothing is dynamic");

            // an owner with no anchors prepares normally, as both sides empty
            LogicalFragmentId dynamicOwner = ledger.AddFragment();
            CutOperationId dynamicCut = AdmitOrFail(ledger, dynamicOwner, k_plane);
            Assert.That(ledger.PrepareAnchorDistribution(dynamicCut, k_epsilon, out AnchorDistributionResult empty), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(empty.status, Is.EqualTo(AnchorDistributionStatus.Ok));
            Assert.That(empty.positiveCount, Is.Zero);
            Assert.That(empty.negativeCount, Is.Zero);
            Assert.That(ledger.Publish(dynamicCut, out LogicalFragmentId dynamicPositive, out LogicalFragmentId dynamicNegative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(AnchorsOf(ledger, dynamicPositive), Is.Empty);
            Assert.That(AnchorsOf(ledger, dynamicNegative), Is.Empty);
            Assert.That(ledger.IsFixedOwner(dynamicPositive) || ledger.IsFixedOwner(dynamicNegative), Is.False, "both children are dynamic");
        }

        /// <summary>
        /// Before publication there are no children at all, and publishing without a prepared distribution changes
        /// nothing — it is not read as "this owner had no anchors". The cut can still be prepared and published after.
        /// </summary>
        [Test]
        public void BeforePublication_ThereAreNoChildren_AndAnUnpreparedPublicationChangesNothing()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId parent = ledger.AddFragment(new List<float3> { k_high, k_low });
            CutOperationId cut = AdmitOrFail(ledger, parent, k_plane);

            int fragmentsBefore = ledger.FragmentCount;
            LogicalCutOperation admitted = OperationOf(ledger, cut);
            Assert.That(admitted.positive.IsSet || admitted.negative.IsSet, Is.False, "no child exists before publication");
            Assert.That(ledger.TryGetPreparedAnchorDistribution(cut, out _), Is.False, "and nothing is prepared yet");

            Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.AnchorsNotPrepared), "an unprepared publication is refused");
            Assert.That(positive.IsSet || negative.IsSet, Is.False, "with no child published");
            Assert.That(ledger.FragmentCount, Is.EqualTo(fragmentsBefore), "no fragment was issued");
            Assert.That(OperationOf(ledger, cut).state, Is.EqualTo(LogicalCutOperationState.Admitted), "the cut is still admitted");
            Assert.That(ledger.IsCurrentTarget(parent), Is.True, "the parent is still the current target");
            Assert.That(ledger.TryGetActiveOperation(parent, out CutOperationId active) && active == cut, Is.True, "with its operation still active");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "and the budget is untouched");
            Assert.That(AnchorsOf(ledger, parent), Is.EqualTo(new List<float3> { k_high, k_low }), "and so is its anchor set");

            // preparing afterwards works, and the publication then goes through
            Assert.That(ledger.PrepareAnchorDistribution(cut, k_epsilon, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(ledger.Publish(cut, out positive, out negative), Is.EqualTo(LogicalCutResultOutcome.Applied), "a refused publication did not spoil the cut");
            Assert.That(AnchorsOf(ledger, positive), Is.EqualTo(new List<float3> { k_high }));
            Assert.That(AnchorsOf(ledger, negative), Is.EqualTo(new List<float3> { k_low }));
        }

        /// <summary>
        /// A request that never passes admission — a no-op, an active source, a full budget — prepares nothing and
        /// leaves every anchor set as it was.
        /// </summary>
        [Test]
        public void ASkippedRequest_PreparesNothing_AndLeavesEveryAnchorSetAlone()
        {
            LogicalCutLedger ledger = NewLedger(1);
            LogicalFragmentId first = ledger.AddFragment(new List<float3> { k_high, k_low });
            LogicalFragmentId second = ledger.AddFragment(new List<float3> { k_onPlane });
            var firstAtStart = new List<float3> { k_high, k_low };
            var secondAtStart = new List<float3> { k_onPlane };

            // a no-op: no operation exists to prepare
            Assert.That(ledger.Admit(first, k_plane, false, out CutOperationId noOp), Is.EqualTo(LogicalCutAdmission.NoOp));
            Assert.That(ledger.PrepareAnchorDistribution(noOp, k_epsilon, out _), Is.EqualTo(AnchorPreparationOutcome.OperationNotActive), "a skipped request has no operation to prepare");
            Assert.That(AnchorsOf(ledger, first), Is.EqualTo(firstAtStart), "and no anchor set moved");

            // an active source
            CutOperationId cut = AdmitOrFail(ledger, first, k_plane);
            Assert.That(ledger.Admit(first, k_otherPlane, true, out _), Is.EqualTo(LogicalCutAdmission.SourceActive));
            Assert.That(AnchorsOf(ledger, first), Is.EqualTo(firstAtStart));

            // a full shared budget
            Assert.That(ledger.Admit(second, k_plane, true, out _), Is.EqualTo(LogicalCutAdmission.Full));
            Assert.That(AnchorsOf(ledger, second), Is.EqualTo(secondAtStart), "the fragment whose request was skipped is untouched");
            Assert.That(ledger.IsFixedOwner(second), Is.True, "and its fixity is unchanged");

            // only the admitted cut moves anything, and only when it is published
            Assert.That(PublishAfterPreparing(ledger, cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(AnchorsOf(ledger, positive), Is.EqualTo(new List<float3> { k_high }));
            Assert.That(AnchorsOf(ledger, negative), Is.EqualTo(new List<float3> { k_low }));
            Assert.That(AnchorsOf(ledger, second), Is.EqualTo(secondAtStart), "the other fragment still holds its own");
        }

        /// <summary>
        /// A prepared distribution is dropped, not published, when the cut ends as an Abort or turns out Stale, and
        /// the budget unit comes back exactly once either way.
        /// </summary>
        [Test]
        public void APreparedDistribution_IsNotPublishedByAnAbortOrAStaleResult()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId aborted = ledger.AddFragment(new List<float3> { k_high, k_low });
            LogicalFragmentId stale = ledger.AddFragment(new List<float3> { k_high, k_low });

            // prepared, then the final fails
            CutOperationId abortedCut = AdmitOrFail(ledger, aborted, k_plane);
            Assert.That(ledger.PrepareAnchorDistribution(abortedCut, k_epsilon, out AnchorDistributionResult abortedPrepared), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(abortedPrepared.positiveCount, Is.EqualTo(1), "a distribution was prepared");
            int fragmentsBeforeAbort = ledger.FragmentCount;

            Assert.That(ledger.Abort(abortedCut), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.FragmentCount, Is.EqualTo(fragmentsBeforeAbort), "no child took the prepared set");
            Assert.That(OperationOf(ledger, abortedCut).positive.IsSet, Is.False);
            Assert.That(ledger.TryGetPreparedAnchorDistribution(abortedCut, out _), Is.False, "the prepared distribution is gone, not kept as a record");
            Assert.That(AnchorsOf(ledger, aborted), Is.Empty, "and the retired source let go of its own set");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(0), "the unit came back once");
            Assert.That(ledger.Abort(abortedCut), Is.EqualTo(LogicalCutResultOutcome.NotActive), "a duplicate abort changes nothing");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.Zero);

            // prepared, then the result turns out stale
            CutOperationId staleCut = AdmitOrFail(ledger, stale, k_plane);
            Assert.That(ledger.PrepareAnchorDistribution(staleCut, k_epsilon, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            int fragmentsBeforeStale = ledger.FragmentCount;
            ledger.NoteOwnershipChanged(stale);

            Assert.That(ledger.Publish(staleCut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Stale));
            Assert.That(positive.IsSet || negative.IsSet, Is.False, "no child was published");
            Assert.That(ledger.FragmentCount, Is.EqualTo(fragmentsBeforeStale), "and none was issued");
            Assert.That(ledger.TryGetPreparedAnchorDistribution(staleCut, out _), Is.False, "the prepared distribution is gone with the stale operation");
            Assert.That(AnchorsOf(ledger, stale), Is.EqualTo(new List<float3> { k_high, k_low }), "while the source, still live, keeps its set");
            Assert.That(ledger.IsCurrentTarget(stale), Is.True);
            Assert.That(ledger.IsFixedOwner(stale), Is.True, "and is still a fixed owner");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.Zero, "the unit came back once");
            Assert.That(ledger.Publish(staleCut, out _, out _), Is.EqualTo(LogicalCutResultOutcome.NotActive), "and a second arrival returns nothing");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.Zero);
        }

        /// <summary>
        /// A child's re-cut distributes the child's own current set. The sibling keeps its set and its fixity, and an
        /// anchor that went to the sibling cannot come back through the grandchildren.
        /// </summary>
        [Test]
        public void ARecut_UsesOnlyTheChildsCurrentSet_AndLeavesTheSiblingAlone()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId parent = ledger.AddFragment(new List<float3> { k_high, k_low });
            CutOperationId first = AdmitOrFail(ledger, parent, k_plane);
            PublishAfterPreparing(ledger, first, out LogicalFragmentId upper, out LogicalFragmentId lower);
            Assert.That(AnchorsOf(ledger, upper), Is.EqualTo(new List<float3> { k_high }));
            Assert.That(AnchorsOf(ledger, lower), Is.EqualTo(new List<float3> { k_low }));

            // cut the upper child again, by y = 2
            var secondPlane = new float4(0f, 1f, 0f, -2f);
            CutOperationId second = AdmitOrFail(ledger, upper, secondPlane);
            Assert.That(PublishAfterPreparing(ledger, second, out LogicalFragmentId upperTop, out LogicalFragmentId upperBottom), Is.EqualTo(LogicalCutResultOutcome.Applied));

            Assert.That(AnchorsOf(ledger, upperTop), Is.EqualTo(new List<float3> { k_high }), "the child's own anchor went to its side");
            Assert.That(AnchorsOf(ledger, upperBottom), Is.Empty, "nothing else appeared");
            Assert.That(ledger.IsFixedOwner(upperBottom), Is.False, "so that grandchild is dynamic");
            Assert.That(AnchorsOf(ledger, upperTop).Contains(k_low), Is.False, "the sibling's anchor never returns");

            Assert.That(AnchorsOf(ledger, lower), Is.EqualTo(new List<float3> { k_low }), "the sibling's set is unchanged");
            Assert.That(ledger.IsFixedOwner(lower), Is.True, "and so is its fixity");
            Assert.That(AnchorsOf(ledger, parent), Is.Empty, "the replaced ancestor kept nothing, and was not consulted");
            Assert.That(AnchorsOf(ledger, upper), Is.Empty, "nor did the child once it was replaced in turn");
        }

        /// <summary>Another fragment's ownership change is no obstacle to this cut's normal publication (DESIGN 8).</summary>
        [Test]
        public void AnOwnershipChangeOnAnotherFragment_DoesNotStopThisPublication()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_high, k_low });
            LogicalFragmentId other = ledger.AddFragment(new List<float3> { k_onPlane });
            CutOperationId cut = AdmitOrFail(ledger, source, k_plane);
            Assert.That(ledger.PrepareAnchorDistribution(cut, k_epsilon, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));

            ledger.NoteOwnershipChanged(other);

            Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied), "only the source's own authority is consulted");
            Assert.That(AnchorsOf(ledger, positive), Is.EqualTo(new List<float3> { k_high }), "and the children got their sets");
            Assert.That(AnchorsOf(ledger, negative), Is.EqualTo(new List<float3> { k_low }));
            Assert.That(AnchorsOf(ledger, other), Is.EqualTo(new List<float3> { k_onPlane }), "while the other fragment keeps its own");
        }

        /// <summary>
        /// The notices that close an operation's geometry responsibility give the budget unit back and take nothing
        /// away from the children that were published.
        /// </summary>
        [Test]
        public void CompletionOrTermination_LeavesTheChildrensAnchorsInPlace()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId completed = ledger.AddFragment(new List<float3> { k_high, k_onPlane, k_low });
            LogicalFragmentId terminated = ledger.AddFragment(new List<float3> { k_high, k_low });

            CutOperationId completedCut = AdmitOrFail(ledger, completed, k_plane);
            PublishAfterPreparing(ledger, completedCut, out LogicalFragmentId cPositive, out LogicalFragmentId cNegative);
            Assert.That(ledger.CompleteGeometry(completedCut), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(AnchorsOf(ledger, cPositive), Is.EquivalentTo(new[] { k_high, k_onPlane }), "completion left the positive child's set alone");
            Assert.That(AnchorsOf(ledger, cNegative), Is.EquivalentTo(new[] { k_onPlane, k_low }), "and the negative child's");
            Assert.That(ledger.IsFixedOwner(cPositive) && ledger.IsFixedOwner(cNegative), Is.True, "with their fixity intact");

            CutOperationId terminatedCut = AdmitOrFail(ledger, terminated, k_plane);
            PublishAfterPreparing(ledger, terminatedCut, out LogicalFragmentId tPositive, out LogicalFragmentId tNegative);
            Assert.That(ledger.Terminate(terminatedCut), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(AnchorsOf(ledger, tPositive), Is.EqualTo(new List<float3> { k_high }), "termination left them alone too");
            Assert.That(AnchorsOf(ledger, tNegative), Is.EqualTo(new List<float3> { k_low }));

            // and the children can still be cut, from the sets they kept
            CutOperationId again = AdmitOrFail(ledger, cPositive, new float4(0f, 1f, 0f, -2f));
            Assert.That(PublishAfterPreparing(ledger, again, out LogicalFragmentId top, out LogicalFragmentId bottom), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(AnchorsOf(ledger, top), Is.EqualTo(new List<float3> { k_high }));
            Assert.That(AnchorsOf(ledger, bottom), Is.EqualTo(new List<float3> { k_onPlane }), "the on-plane anchor of the first cut is below the second plane");
        }

        /// <summary>
        /// Duplicates change nothing extra: a second preparation keeps the first one's distribution even with another
        /// epsilon, a second publication publishes no further child, and a second terminal notice returns no second
        /// unit.
        /// </summary>
        [Test]
        public void DuplicatePreparation_PublicationAndTerminalNotices_ChangeNothingExtra()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_high, k_onPlane, k_low });
            CutOperationId cut = AdmitOrFail(ledger, source, k_plane);

            Assert.That(ledger.PrepareAnchorDistribution(cut, k_epsilon, out AnchorDistributionResult firstPrepared), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(firstPrepared.positiveCount, Is.EqualTo(2));
            Assert.That(firstPrepared.negativeCount, Is.EqualTo(2));

            // a second preparation with an epsilon wide enough to change the answer does not overwrite it
            Assert.That(ledger.PrepareAnchorDistribution(cut, 10f, out AnchorDistributionResult secondPrepared), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(secondPrepared.positiveCount, Is.EqualTo(firstPrepared.positiveCount), "the prepared distribution stands");
            Assert.That(secondPrepared.negativeCount, Is.EqualTo(firstPrepared.negativeCount));

            Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(AnchorsOf(ledger, positive), Is.EquivalentTo(new[] { k_high, k_onPlane }), "the children took the first epsilon's result");
            Assert.That(AnchorsOf(ledger, negative), Is.EquivalentTo(new[] { k_onPlane, k_low }));
            int fragmentsAfterPublish = ledger.FragmentCount;

            // a duplicate publication, and a preparation after publication
            Assert.That(ledger.Publish(cut, out LogicalFragmentId again, out _), Is.EqualTo(LogicalCutResultOutcome.NotActive), "a duplicate publication is refused");
            Assert.That(again.IsSet, Is.False);
            Assert.That(ledger.FragmentCount, Is.EqualTo(fragmentsAfterPublish), "and issued no child");
            Assert.That(ledger.PrepareAnchorDistribution(cut, k_epsilon, out _), Is.EqualTo(AnchorPreparationOutcome.OperationNotActive), "a published operation cannot be prepared again");
            Assert.That(ledger.TryGetPreparedAnchorDistribution(cut, out _), Is.False, "and its distribution is no longer reported as prepared: it was handed on");
            Assert.That(AnchorsOf(ledger, positive), Is.EquivalentTo(new[] { k_high, k_onPlane }), "and the children are as they were");

            // duplicate terminal notices
            Assert.That(ledger.CompleteGeometry(cut), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.Zero);
            Assert.That(ledger.CompleteGeometry(cut), Is.EqualTo(LogicalCutResultOutcome.NotActive));
            Assert.That(ledger.Terminate(cut), Is.EqualTo(LogicalCutResultOutcome.NotActive));
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.Zero, "no second unit came back");
            Assert.That(AnchorsOf(ledger, positive), Is.EquivalentTo(new[] { k_high, k_onPlane }), "and the children still hold their anchors");
            Assert.That(AnchorsOf(ledger, negative), Is.EquivalentTo(new[] { k_onPlane, k_low }));
        }

        /// <summary>
        /// A refused distribution leaves the operation unprepared and the source alone: it is not an Abort, and the
        /// caller can prepare again with a usable epsilon.
        /// </summary>
        [Test]
        public void ARefusedPreparation_LeavesTheOperationUnprepared_AndDoesNotRetireTheSource()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId source = ledger.AddFragment(new List<float3> { k_high, k_low });
            CutOperationId cut = AdmitOrFail(ledger, source, k_plane);

            Assert.That(
                ledger.PrepareAnchorDistribution(cut, -1f, out AnchorDistributionResult refused),
                Is.EqualTo(AnchorPreparationOutcome.DistributionRefused),
                "a negative epsilon is refused by the distribution");
            Assert.That(refused.status, Is.EqualTo(AnchorDistributionStatus.InvalidEpsilon), "with the reason the distribution gave");
            Assert.That(ledger.TryGetPreparedAnchorDistribution(cut, out _), Is.False, "nothing is prepared");
            Assert.That(StateOf(ledger, source), Is.EqualTo(LogicalFragmentState.Live), "the source is not retired for it");
            Assert.That(OperationOf(ledger, cut).state, Is.EqualTo(LogicalCutOperationState.Admitted), "and the cut is still admitted");
            Assert.That(ledger.Budget.IncompleteCutOperationCount, Is.EqualTo(1), "with the budget untouched");
            Assert.That(ledger.Publish(cut, out _, out _), Is.EqualTo(LogicalCutResultOutcome.AnchorsNotPrepared), "so publication is still refused");

            // preparing properly afterwards works
            Assert.That(ledger.PrepareAnchorDistribution(cut, k_epsilon, out _), Is.EqualTo(AnchorPreparationOutcome.Prepared));
            Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied));
            Assert.That(AnchorsOf(ledger, positive), Is.EqualTo(new List<float3> { k_high }));
            Assert.That(AnchorsOf(ledger, negative), Is.EqualTo(new List<float3> { k_low }));
        }

        /// <summary>
        /// The same publication for a body that is not sitting at the origin: a placement with rotation and
        /// translation, with the anchors and the adopted plane brought into the coordinates the ledger works in
        /// through the conversion the product uses. The ledger itself has no notion of a frame and gains none here;
        /// what is checked is that a real placement, converted the ordinary way, still distributes and publishes the
        /// arrangement that was laid out.
        /// <para>
        /// The arrangement is authored in the body's OWN coordinates, where it is known by construction: one anchor a
        /// clear distance above the cut face, one a clear distance below it, and one exactly on it. The plane is then
        /// expressed in world from that same placement and handed back through
        /// <see cref="VpCutPlane.TryWorldToGeometryLocal"/>, which is the path under test. The expected sides and sets
        /// are the authored ones -- nothing here recomputes an expectation by running the conversion.
        /// </para>
        /// </summary>
        [Test]
        public void ANonIdentityPlacement_DistributesAndPublishesTheArrangementItWasGiven()
        {
            // The placement: a rotation about all three axes and a translation well away from the origin. No scale,
            // because a physics frame is rigid and the anchors are points in it.
            var placement = Matrix4x4.TRS(
                new Vector3(12f, -3.5f, 7.25f), Quaternion.Euler(24f, -63f, 41f), Vector3.one);

            // The arrangement, authored in the body's own coordinates. The cut face is y = 0.25 there, so:
            var above = new float3(0.6f, 4f, -0.2f);      // 3.75 above the face
            var onFace = new float3(1f, 0.25f, 1f);       // exactly on it
            var below = new float3(-0.3f, -4f, 0.4f);     // 4.25 below it

            // The same face expressed in world, from the placement itself: a point on the face and the face's normal,
            // carried by Unity's own transform of a point and of a direction. That is the opposite direction to the
            // conversion under test, and it is what gives this test a world plane to hand back.
            Vector3 faceNormalInWorld = placement.MultiplyVector(new Vector3(0f, 1f, 0f)).normalized;
            Vector3 faceOriginInWorld = placement.MultiplyPoint3x4(new Vector3(0f, 0.25f, 0f));
            var worldPlane = new float4(
                faceNormalInWorld.x, faceNormalInWorld.y, faceNormalInWorld.z,
                -Vector3.Dot(faceNormalInWorld, faceOriginInWorld));

            Assert.That(
                VpCutPlane.TryWorldToGeometryLocal(worldPlane, placement, out float4 localPlane), Is.True,
                "the world plane converts into the body's own coordinates");

            LogicalCutLedger ledger = NewLedger(2);
            LogicalFragmentId source = ledger.AddFragment(new List<float3> { above, onFace, below });
            Assert.That(ledger.IsFixedOwner(source), Is.True, "the source is fixed: it has anchors");

            CutOperationId cut = AdmitOrFail(ledger, source, localPlane);
            Assert.That(
                ledger.PrepareAnchorDistribution(cut, k_epsilon, out AnchorDistributionResult prepared),
                Is.EqualTo(AnchorPreparationOutcome.Prepared));

            // The counts the authored arrangement calls for: the one above and the one on the face go positive, the
            // one below and the one on the face go negative.
            Assert.That(prepared.status, Is.EqualTo(AnchorDistributionStatus.Ok));
            Assert.That(prepared.positiveCount, Is.EqualTo(2), "the anchor above the face, and the one on it");
            Assert.That(prepared.negativeCount, Is.EqualTo(2), "the anchor below the face, and the one on it");
            Assert.That(prepared.IsPositiveFixed, Is.True, "so the positive side is fixed");
            Assert.That(prepared.IsNegativeFixed, Is.True, "and so is the negative side");

            Assert.That(
                ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative),
                Is.EqualTo(LogicalCutResultOutcome.Applied));

            // And the sets themselves, as authored: the points are handed on unchanged, in the same coordinates they
            // were given in, with the one on the face inherited by both sides.
            Assert.That(
                AnchorsOf(ledger, positive), Is.EquivalentTo(new[] { above, onFace }),
                "the positive child holds the anchor above the face and the one on it");
            Assert.That(
                AnchorsOf(ledger, negative), Is.EquivalentTo(new[] { onFace, below }),
                "the negative child holds the one on the face and the anchor below it");
            Assert.That(ledger.IsFixedOwner(positive), Is.True, "both children are fixed");
            Assert.That(ledger.IsFixedOwner(negative), Is.True);
            Assert.That(AnchorsOf(ledger, source), Is.Empty, "and the replaced source let go of the set it handed on");
        }
    }
}
