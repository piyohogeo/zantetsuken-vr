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

### Submit worker: settle observed before the output queue was filled

`NvencOrderedSubmitWorkerServiceContractTests.BeginDrain_WhileRunning_IsRejected_WorkerContinues`
failed at `Assert.That(h.OutputQueue.Count, Is.EqualTo(1))`
(`Expected: 1 But was: 0`) right after the fixture's
`WaitSettled(h.SettledEvent, "worker did not process the accepted work")`. So
the settle signal had been observed while the queue was still empty. This test
does not call `StopFinalizedBackend` and does not belong to the teardown-timing
entry above.

Cause not established, and it is not known whether this is a test-side race or
a worker defect. Observed twice, in the full suite runs
`20260909-190835-7250d9` and `20260910-065539-58293d`, with the same assertion
and message both times. Other full suite runs passed, but the test has not been
shown to be deterministic in isolation.

The second observation came from a run whose only change was additive: new
files this fixture does not reference. No direct code dependency on that change
was found, but whether the added tests shifted suite duration, thread
scheduling, or machine load enough to expose the race has not been evaluated,
so that change is not excluded as a trigger. Either way the root cause is
undetermined, so this entry stays here rather than moving to "Active flakes",
and a failure of it is still a regression candidate to investigate.

### Output worker: settle observed before terminal result became collectable

`NvencRunPublicationPlanCommitterContractTests.Commit_UnsupportedFileSystem_ThrowsBeforeContact`
failed inside the fixture's `StopFinalizedBackend` helper at
`Assets/Zantetsu/Tests/EditMode/NvencRunPublicationPlanCommitterContractTests.cs`
line 549, `Assert.That(h.RunCoordinator.TryCollectTerminal(out _), Is.True)`
(`Expected: True But was: False`). The collect returned false immediately after
the preceding `WaitSettled(h.SettledEvent, "worker did not converge the
finalize request")` had observed the settle signal.

This failure is inside `StopFinalizedBackend` but is not the teardown-timing
entry under "Active flakes": that entry covers the physical-stop wait and
`h.Worker.TeardownCompleted`, which is a later boundary. It is also not the
Submit worker entry above, which is a different test and a different
assertion. Until the cause is known, do not merge this entry into either of
them.

Observed once, in the full suite run `20260910-080619-481d11`. That run was a
deliberately perturbed one: the PngJson capture-complete cleanup backend had
been temporarily reverted to flushing the grandparent directory in order to
prove that a new regression test detects the old flush target. The three clean
full suite runs around it - `20260910-080413-410905`, `20260910-080900-96c8f2`,
and `20260910-081054-4f81ca` - all passed, so the failure has not reproduced.

No direct code dependency between the reverted PngJson flush target and this
fixture was found. Whether that revert, or the surrounding suite changes,
shifted suite duration, thread scheduling, or machine load enough to expose the
failure has not been evaluated, so neither is excluded as a trigger. It is
undetermined whether this is a test-side race or a product defect, so a failure
of this test stays a regression candidate to investigate and this entry is not
permission to re-run it.

## Resolved

The PngJson capture index committer failures once listed here
(`Commit_CreateTemporary_WritesAndCommits`,
`Commit_ReplaceInvalid_DeletesOnlyInvalidTmpAndCommits`,
`Commit_ReuseCanonical_RenamesTmpWithoutRewrite`, and
`Recovery_NormalPath_PublishCommitCleanupNotifyRelease`, which showed
`capture.indexV` / `capture.index<garbage>` siblings) were not load or timing
related. They were a defect in the capture index rename, fixed in c1ccc7b.
