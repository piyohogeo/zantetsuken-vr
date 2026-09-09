# Known flaky tests (EditMode, full-suite only)

These tests pass deterministically in isolation and on re-run, but can fail
intermittently in a full EditMode suite run under batchmode load. Before
treating a full-suite failure as a regression, re-run the fixture or the full
suite.

## Filesystem rename race (PngJson capture index committer)

- `PngJsonCaptureRunCaptureIndexCommitterContractTests.Commit_CreateTemporary_WritesAndCommits`
  (line ~370: `receipt.IsIssuedFor(committer, operation, token)` Expected True).
- `PngJsonCaptureRunCaptureIndexCommitterContractTests.Commit_ReplaceInvalid_DeletesOnlyInvalidTmpAndCommits`
  (`FileNotFoundException: ... capture.index`).
- `PngJsonCaptureRunCaptureIndexCommitterContractTests.Commit_ReuseCanonical_RenamesTmpWithoutRewrite`
  (line ~399: `FileNotFoundException: ... capture.index`).
- `PngJsonCapturePublicationPhase0EndToEndTests.Recovery_NormalPath_PublishCommitCleanupNotifyRelease`
  (`IOException: capture.index is absent`). Order-dependent; leftover sandboxes
  showed `capture.indexV` / `capture.index<garbage>` / `capture.index.tmp`.

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
