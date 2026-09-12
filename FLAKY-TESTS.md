# Known flaky tests (EditMode, full-suite only)

Only the tests under "Active flakes" below may be re-run before a failure is
treated as a regression: they pass deterministically in isolation and on
re-run, but can fail intermittently in a full EditMode suite run under
batchmode load. Every other failure, including anything under "Under
investigation" or "Resolved", is a normal failure and must be investigated
rather than re-run.

## Active flakes

### Output-worker teardown timing (`StopFinalizedBackend` helper)

Any test that calls the `StopFinalizedBackend` helper (present in
`NvencCaptureRunCoordinatorContractTests`,
`NvencRunPublicationPlanCommitExecutionCoordinatorContractTests`, and the other
publication fixtures) can fail at the helper's `WaitForPhysicalStop`
("worker did not physically exit after the teardown") or
`Assert.That(h.Worker.TeardownCompleted, Is.True)` (both `Expected: True But was:
False`). Observed on `TraceFreeze_BeforeBackendJoin_RefusesNoTraceContact`,
`Collect_ExternalPoisonFirst_NoNormalReflection`,
`StopService_CommitOutcomeUnknown_Rejected`, and
`ExecutionResult_InvalidatedByLeaseRelease`.

## Under investigation

Failures here are open questions, not permission to re-run. Nothing below is
known to be a test-side race rather than a product defect, so a failure stays a
regression candidate until the cause is established. Move an entry to "Active
flakes" only once its reproduction conditions or a test-side race are
confirmed, or to "Resolved" once it is fixed.

## Retired

Entries here are tests that no longer exist. Nothing below is permission to
re-run anything, and nothing below records a proven product defect.

### Standalone: `Player_RunsAndCollectsItsConversionCommands`

The conversion-command sentinel failed 3 times across 142 recorded Standalone
runs with `generation 4 must complete` - a conversion completion that had not
arrived inside the test's single 5 s wait. At least 2 of those failures
(`20260912-110903-7c5e2c`, `20260912-122422-6a7834`) predate the decoder unit
that ended in 54ceb49, so the failure was not introduced by it.

Its shape was the reason it could stop a whole Standalone run: the 5 s wait was
performed on a thread of the test's own, and the coroutine then waited for that
thread with no deadline of its own, so a wait that did not return left the
Player with nothing to end it.

It was retired rather than given a longer timeout, because the same
observations are now made by the production-path nine-frame sentinel and by the
native contract test, on the real pipeline rather than on direct native calls:
the eight fixed sync slots all run real callbacks inside one Run before
anything is collected, the ninth frame reuses a returned slot under a later
generation, the native contract reuses one slot sixteen times distinguished
only by generation and covers the uncollected-release, generation-mismatch and
collect-once refusals, and the decode of that Run's chunk shows nine distinct
frames in the order they were accepted. The one observation that lived only
here - that Unity still draws after the render callbacks - moved into the
nine-frame sentinel, where it runs once after every conversion has completed
and before teardown.

Nothing here says a product defect was proven, and nothing here was resolved by
re-running: the failure was real, it was observed again during the decoder
unit's verification, and the test that produced it is gone.

## Resolved

### Submit worker: settle observed before the output queue was filled

`NvencOrderedSubmitWorkerServiceContractTests.BeginDrain_WhileRunning_IsRejected_WorkerContinues`
failed at `Assert.That(h.OutputQueue.Count, Is.EqualTo(1))`
(`Expected: 1 But was: 0`) right after the fixture's
`WaitSettled(h.SettledEvent, "worker did not process the accepted work")`.
Observed three times in total, in the full suite runs
`20260909-190835-7250d9`, `20260910-065539-58293d`, and
`20260910-173958-272c43`, with the same test, the same assertion, and the same
symptom every time.

This was a fixture race, not a product defect. The Submit Worker's `Settled`
event is a best-effort park notification, and a raise that was already in
flight before the record was enqueued completes afterwards while carrying no
information about it. The fixture consumed exactly one such settle and read
`OutputQueue.Count` as though it proved the work had been processed.

`NvencOrderedSubmitWorkerServiceContractTests.OutputQueue_SettleObservedAfterEnqueue_IsNotEvidenceOfProcessedWork`
demonstrates that mechanism deterministically, with two ordinary Settled
observers and no product change: the worker is held at the start of a raise,
the record is enqueued inside that window, the raise is then allowed to publish
its settle, and the queue is shown to be empty at that exact point. It
reproduces the same mis-judgement mechanism rather than replaying the original
interleaving.

Fixed in 7f26217, which changed the four sites that read `OutputQueue.Count`
straight after a settle wait to a bounded convergence on that count.

The same mis-judgement was still present on the other side of the fixture. In
the full suite run `20260912-132411-fb2e27`,
`NvencOrderedSubmitWorkerServiceContractTests.Drain_BeginDrainIdempotent` failed
at `Assert.That(h.Worker.DrainCompleted, Is.True)` (`Expected: True But was:
False`) immediately after a settle wait. That is the identical mechanism: a
settle is a wake hint, not evidence that the drain has completed, and the
positive reads of `DrainCompleted` had not been converted. This is not an active
flake and not a product defect - `DrainCompleted` itself is correct, and the
fixture was asking the wrong question.

`Drain_SettleObservedAfterBeginDrain_IsNotEvidenceOfCompletedDrain` demonstrates
it deterministically with the same two ordinary Settled observers: the worker is
held at the start of a raise, the drain is requested inside that window, the
raise is then allowed to publish its settle, and the drain is shown to be
incomplete at exactly that point. Every positive drain confirmation in that
fixture now goes through `WaitForDrainCompleted`, which re-checks the flag
against one deadline and uses the settle only as a wake hint, exactly as
`WaitForOutputQueueCount` does. The evidence for this fix is that deterministic
test and the replaced confirmations, not a successful re-run.

A failure of either assertion is now a regression to investigate, never
something to pass by re-running.

### Output worker: settle observed before terminal result became collectable

Tests that converged a Run chunk terminal through the fixtures'
`StopFinalizedBackend` and `FinalizeAndFreeze` helpers could fail at
`Assert.That(runCoordinator.TryCollectTerminal(out _), Is.True)`
(`Expected: True But was: False`) immediately after the preceding
`WaitSettled(...)` had observed a settle. Observed in
`NvencRunPublicationPlanCommitterContractTests.Commit_UnsupportedFileSystem_ThrowsBeforeContact`
(`20260910-080619-481d11`),
`NvencCaptureRunCoordinatorContractTests.BackendJoin_BindingSwappedAfterConstruction_PoisonsNoDispose`
and
`NvencRunPublicationServiceContractTests.CaptureIndex_SameServiceInstanceAndWorkerThread_NoSecondWorker`
(`20260910-082320-86a951`), and
`NvencCaptureRunCoordinatorContractTests.BackendJoin_PoolOccupied_RefusesNoDispose`
and `NvencRunPublicationPlanCommitContractTests.Builder_TamperedDescriptorKind_Throws`
(`20260910-082628-e82fe3`).

This was a fixture race, not a product defect. The Worker's `Settled` event is
documented as a best-effort notification raised when the Worker is about to
park, with production correctness never depending on subscribers, and the
Worker reaches its raise only after it has already reset its wake signal and
re-checked for work. A raise that was already in flight when a terminal request
was accepted therefore completes afterwards while carrying no information about
that request. The helpers used exactly one such settle as proof of convergence,
so the collect that followed it correctly reported that nothing had been
advanced yet.

`NvencOrderedOutputWorkerServiceContractTests.Terminal_SettleObservedAfterRequest_IsNotEvidence_ConvergenceConfirmsTheRealCondition`
demonstrates that mechanism deterministically, with two ordinary Settled
observers and no product change: the Worker is held at the start of a raise,
the request is accepted inside that window, the raise then publishes its
settle, and the terminal is shown to be uncollectable at that exact point -
the observed symptom, on demand. It reproduces the mechanism rather than
replaying any original interleaving.

Fixed in 7d22dd5, which routes every terminal convergence site through the
`TerminalConvergence` test helper. That helper treats `Settled` only as a wake
hint and confirms the real condition, `TryCollectTerminal`, inside a bounded
watchdog.

A failure of this assertion is now a regression to investigate, never something
to pass by re-running.

#### A further site of the same mechanism

`NvencCaptureRunCoordinatorContractTests.Collect_AfterPoison_DoesNotConsumeOutcome`
failed at `Assert.That(h.Worker.TryCollectTerminal(out ... ), Is.True)`
(`Expected: True But was: False`) in run `20260911-093312-7d9cf9`. The raw
results XML confirms the stack trace pointed at that collect, not at the
`IsFinalized` assertion after it.

`TerminalConvergence` takes a delegate, so it could technically have driven the
Worker's own `TryCollectTerminal` too. It was not used here because collecting
is what this test is about: the outcome has to stay unconsumed until after the
Poison, and converging on the direct collect would consume it beforehand and
lose the test's purpose. So the test waited for one `Settled` raise after
`TryRequestTerminal()` and collected immediately, and a raise already in flight
before the request was accepted let that collect correctly report that nothing
had been published yet - the same mechanism as above.

Fixed in f5a5ff7, test-only: the test now converges on the chunk context's own
finalization result inside a bounded watchdog, which reading does not consume,
and only then awaits a fresh settle. The Worker publishes the terminal outcome
after that finalization on its own single thread, so a settle observed from
that point is necessarily later than the publication.

The commit that immediately preceded the failure (9e8d4c7) changed only fixture
comments and one fake observation flag in a different fixture, so no code
dependency links them; it cannot be ruled out, though, that it shifted suite
timing enough to expose the race.

The PngJson capture index committer failures once listed here
(`Commit_CreateTemporary_WritesAndCommits`,
`Commit_ReplaceInvalid_DeletesOnlyInvalidTmpAndCommits`,
`Commit_ReuseCanonical_RenamesTmpWithoutRewrite`, and
`Recovery_NormalPath_PublishCommitCleanupNotifyRelease`, which showed
`capture.indexV` / `capture.index<garbage>` siblings) were not load or timing
related. They were a defect in the capture index rename, fixed in c1ccc7b.
