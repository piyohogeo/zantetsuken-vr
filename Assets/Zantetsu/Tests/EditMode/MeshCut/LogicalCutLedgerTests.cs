using NUnit.Framework;
using Unity.Mathematics;

namespace Zantetsu.MeshCut.Tests
{
    /// <summary>
    /// The logical admission and publication of cuts as pure data (DESIGN 4.2, 7.1, 7.7, 8), driven by synthetic
    /// outcomes: the harness here decides whether a cut is a no-op, whether final physics is established on both sides
    /// or not, when geometry responsibility is done, and when a source's ownership changed under an active cut. Nothing
    /// of geometry, display, jobs or physics is exercised; what is checked is identity, state, authority and the
    /// shared incomplete budget.
    /// </summary>
    public class LogicalCutLedgerTests
    {
        private static readonly float4 k_plane = new float4(0f, 1f, 0f, -0.25f);
        private static readonly float4 k_otherPlane = new float4(1f, 0f, 0f, 0.5f);

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

        [Test]
        public void ASuccessfulFinal_PublishesBothChildrenAndTheOperationTogether_AndTheParentStopsBeingATarget()
        {
            LogicalCutLedger ledger = NewLedger(4);
            LogicalFragmentId parent = ledger.AddFragment();
            CutOperationId cut = AdmitOrFail(ledger, parent, k_plane);
            Assert.That(ledger.IsCurrentTarget(parent), Is.True, "the parent is a target while its cut is pending");
            Assert.That(ledger.TryGetActiveOperation(parent, out CutOperationId active) && active == cut, Is.True, "and the cut is its active operation");

            Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative), Is.EqualTo(LogicalCutResultOutcome.Applied));

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
            ledger.Publish(cut, out LogicalFragmentId positive, out _);
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
            ledger.Publish(cut, out _, out _);
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
            Assert.That(one.Publish(cutA, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied));
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
            Assert.That(two.Publish(cutD, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Stale));
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

            ledger.Publish(cutA, out _, out _);
            ledger.Publish(cutB, out _, out _);
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

            ledger.Publish(cut, out LogicalFragmentId positive, out LogicalFragmentId negative);
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

            Assert.That(ledger.Publish(cut, out LogicalFragmentId positive, out _), Is.EqualTo(LogicalCutResultOutcome.Stale), "the late success is stale");
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
            Assert.That(ledger.Publish(bystanderCut, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied), "another fragment's cut is not stale for it");

            // the source can be cut again, and a result for the new cut is not stale
            Assert.That(ledger.Admit(source, k_plane, true, out CutOperationId retry), Is.EqualTo(LogicalCutAdmission.Admitted), "the source accepts a new cut after the stale one");
            Assert.That(ledger.Publish(retry, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied), "whose result is current");
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

            Assert.That(ledger.Publish(cut, out _, out _), Is.EqualTo(LogicalCutResultOutcome.Applied), "only the source's own authority is consulted");
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
            ledger.Publish(stale, out _, out _);

            CutOperationId completed = AdmitOrFail(ledger, a, k_plane);
            ledger.Publish(completed, out LogicalFragmentId aPositive, out LogicalFragmentId aNegative);
            ledger.CompleteGeometry(completed);

            CutOperationId terminated = AdmitOrFail(ledger, aPositive, k_otherPlane);
            ledger.Publish(terminated, out LogicalFragmentId grandPositive, out LogicalFragmentId grandNegative);
            ledger.Terminate(terminated);

            CutOperationId next = AdmitOrFail(ledger, aNegative, k_plane);
            ledger.Publish(next, out LogicalFragmentId nextPositive, out LogicalFragmentId nextNegative);

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

            var fragments = new[] { default(LogicalFragmentId), new LogicalFragmentId(0), new LogicalFragmentId(2), new LogicalFragmentId(int.MaxValue) };
            foreach (LogicalFragmentId id in fragments)
            {
                Assert.That(ledger.TryGetFragmentState(id, out _), Is.False, id + " has no state");
                Assert.That(ledger.IsCurrentTarget(id), Is.False, id + " is not a target");
                Assert.That(ledger.TryGetActiveOperation(id, out _), Is.False, id + " has no active operation");
                Assert.That(ledger.Admit(id, k_plane, true, out CutOperationId issued), Is.EqualTo(LogicalCutAdmission.SourceNotLive), id + " is not admitted");
                Assert.That(issued.IsSet, Is.False);
                Assert.That(() => ledger.NoteOwnershipChanged(id), Throws.ArgumentException, id + " has no ownership to change");
            }

            var operations = new[] { default(CutOperationId), new CutOperationId(0), new CutOperationId(2), new CutOperationId(int.MaxValue) };
            foreach (CutOperationId id in operations)
            {
                Assert.That(ledger.TryGetOperation(id, out _), Is.False, id + " does not exist");
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
    }
}
