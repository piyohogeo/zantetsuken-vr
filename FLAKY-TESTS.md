# Known flaky tests (EditMode, full-suite only)

These tests pass deterministically in isolation and on re-run, but can fail
intermittently in a full EditMode suite run under batchmode load. Before
treating a full-suite failure as a regression, re-run the fixture or the full
suite.

## Output-worker teardown timing (`StopFinalizedBackend` helper)

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

## Resolved — not flaky any more

The PngJson capture index committer failures once listed here
(`Commit_CreateTemporary_WritesAndCommits`,
`Commit_ReplaceInvalid_DeletesOnlyInvalidTmpAndCommits`,
`Commit_ReuseCanonical_RenamesTmpWithoutRewrite`, and
`Recovery_NormalPath_PublishCommitCleanupNotifyRelease`, which showed
`capture.indexV` / `capture.index<garbage>` siblings) were not load or timing
related. They were a defect in the capture index rename, fixed in c1ccc7b.
Treat any failure of these tests as a regression and do not re-run to pass.
