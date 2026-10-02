# Development log readers

Readers for diagnosis records written to the DEBUG-only development logger (DESIGN 21.17, D-021). The logger is best
effort: an accepted record is queued, not known to be saved, and the session's drain at the end is bounded. A record is
therefore used only after the saved file has been checked against the counts its run wrote to the log. These readers are
diagnosis tools, not product checks: no product test's pass or fail depends on them.

## Writers

| writer_id | Source | Turned on by | End line in the log |
|---|---|---|---|
| `CutPhysicsStep` | `Assets/Zantetsu/Runtime/PhysicsCut/CutPhysicsStep.Diagnosis.cs`: each step decision; PlayMode test marks from `StepDiagnosisAction.cs` | `ZTK_STEP_DIAG=1` in the environment when the play session starts (DEBUG builds only) | `[step diagnosis] ended: attempted N (seq 1..N, the summary last), accepted A, refused: ...` |
| `CheckHits` | `Assets/Zantetsu/Runtime/Sandbox/SandboxPropSlashPlayerCheck.HitLog.cs`: each hit the check observed in its multi-NPC, building and MobPlan modes (formerly `multi-hits.csv` / `building-hits.csv`); records `start`, `hit`, `summary` | those check modes in a Development Player (`-zantetsuPropSlash <dir>` with `-zantetsuMultiNpc`, `-zantetsuBuildingSlash` or `-zantetsuMobPlan`); one recording per check walk (each iteration of `-zantetsuPropIterations`) | `[check hits] ended: recording R, attempted N (seq 1..N, the summary last), accepted A, refused: ...; ...; hits H` |
| `ViewDiagnosis` | `Assets/Zantetsu/Runtime/Sandbox/ViewDiagnosis.cs`: the head pose at each stage (tags `inputAfterUpdate`, `update`, `beforeRender`), and records `start`, `found`, `focus`, `pause`, `device`, `mark`, `state`, `summary` | the Player argument `-zantetsuViewDiag` (no value), or `ViewDiagnosis.Start()` in a test (DEBUG builds only) | `[view diagnosis] ended: recording R, attempted N (seq 1..N, the summary last), accepted A, refused: ...; ...; cost before the summary: ...` |

A record's value is a flat list of field names and values, starting with `seq` (1..N, one per record tried), `eventFrame`
(the frame when the record was made) and `seconds` (Stopwatch since the play session's reset; a decision's own reading).
The record has an explicit end: the summary is the last record (seq N, its own counts of seq 1..N-1), and nothing is
recorded after it. The end line in the log counts seq 1..N, the summary included.

`ViewDiagnosis` records start with `recording` as well: a session can hold several recorders (each test starts one), each
with its own seq 1..N and its own end line. A sample's `eventFrame` and `seconds` are read when the sample is taken, before
its values; `seconds` counts from the recorder's start. Its summary and end line also carry the record's own main-thread
cost (write_log and sample time; the allocated bytes are 0 where the runtime does not count them, which is not "none").

`CheckHits` records start with `recording` too: one per check walk, each with its own seq and end line. The `start`
record names the walk's mode, iteration and output directory (`directory`, which ties the recording to that run's other
files). In a `hit` record:
- `eventFrame` is the frame the check observed the hit in: its LateUpdate (order 400), reading the detector's new hit list.
  It is the same value as the old CSV's `frame`. It is not the frame the hit was evaluated in, which the hit does not carry
  and nothing makes up.
- `seconds` is the Stopwatch at that observation, counted from the recording's start.
- `slashId`, `of` (the lineage's name), `child` (1 when the fragment has an origin), `fragment`, `acceptance`, `admission`
  and `operation` are as in the old CSV.
- `at` is `SlashHitConfirmed.At`: the input time of the wave update whose sweep hit. It is in seconds on the replay's input
  clock: `Time.unscaledTimeAsDouble` at the replay's start, plus each replayed row's recorded offset, then plus each tick's
  unscaled delta. This clock is its own, not the Stopwatch.
- `side` is `SlashHitConfirmed.Side`: +1 or -1 for a side of a Provisional pair, 0 for a published owner.
- `update` is the recorder's replay index minus one at the observation, not an input-side update id: the last replayed input row (from 0). It stays at
  the last row while the ticks go on.

The check's verdicts do not read this record. The required hit Trace (`slash-hits.ztrace`) is a separate path. A complete
record says the record is complete, nothing more: a walk that ended early also ends its record, and whether its scenario
passed is the check's own verdict (`PROP SLASH: ok` / `FAILED` in the log).

A `CheckHits` recording of the multi-NPC or the building mode also holds `op` records, formerly `multi-ops.csv` /
`building-ops.csv`: one per accepted cut, written at the run's summary, all of them before the recording's `summary`. They
add no measurement: each carries what the check followed in memory.
- `eventFrame` and `seconds`: when the record was made, at the summary. They are not the moment of any stage.
- `acceptedFrame`, `provisionalFrame`, `finalFrame` and `committedFrame`: the frames the check observed each stage in, -1 when
  not observed. The stages are the hit (the hit's `eventFrame`; for a held root, its take-up), the Provisional publication,
  the operation leaving Admitted, and the geometry Committed. These are not the moments the product reached the stages.
- `acceptedTime` .. `committedTime`: the check's clock at those observations, `Time.unscaledTimeAsDouble` since the replay's
  start, in seconds, null when not observed. This clock is neither the Stopwatch of `seconds` nor the replay input clock of
  `at`.
- `ledgerState`, `positive` and `negative`: the ledger's values at the summary.
- `of` and `child` belong to the multi-NPC mode. The source's depth, anchors and fixedness and the cut's kind and normal
  belong to the building mode. They are null where the mode has none.
- `name`, `hull` and `fusion` are the check's own. A held root's name ends in `-held`. `hull` / `fusion` mark a Pending
  answered without an operation of its own, which is operation 0.
- Repeated rows and operation-0 rows are kept as they are.

The summary's ops tally keeps apart a mode that writes no ops from one that accepted none:
- `opsApplicable` is 0 for a mode that writes no ops (the MobPlan mode).
- `opsPlanned` is fixed from the accepted list before any op is written. It is null when the summary never reached the ops.
- `opsAttempted` and `opsAccepted` count the op records tried and the ones the logger accepted.

The end line adds `; ops planned P, attempted A, accepted C`, `; ops not applicable`, or `; ops applicable, not written`.

## Files

- `check_writer_jsonl.py`: the completeness check, per writer.
- `analyze_view.py`: per recording, the stages' order within a frame and the clock along seq, then per phase (cut at the
  `focus` and `pause` records) how much each stage of the head pose moved, the tracking states, and the camera against the
  driver's pose. It runs the check per recording first.
- `analyze_hits.py`: per recording, the hits by child/root and acceptance, by Slash and by observed frame, the logger's
  frame against `eventFrame`, and the record's cost. It runs the check per recording first. `hits_for_run(run_dir,
  check_dir)` gives other readers one run's hits: it finds the recording whose `start` names `check_dir`, checks it against
  `run_dir/player.log`, and raises `NotComplete` otherwise.
- `analyze_ops.py`: per recording, the ops checked against the summary's tally first; planned, attempted, accepted and saved
  must agree. Then the stages, in observed frames and in check-clock milliseconds, and how the ops answer the recording's
  hits. An op with an operation is matched by operation, slashId and source. A held root is matched by its Held hit, the
  same slashId and source, observed no later than the op's acceptance. An operation-0 op is matched by an operation-0
  Pending hit with the same slashId and source in the same frame, and operation 0 is never used as an id. One candidate by
  operation id is confirmed. One candidate by the held or the operation-0 rule is a candidate: those fields agree, which
  does not prove it is the same acceptance. None is unmatched, and several are ambiguous, which is reported and not picked. A hit with no op is
  classified by its acceptance: no cut by the rule or not taken up are by specification; Published or Pending is a record
  short. `ops_for_run(run_dir, check_dir)` gives other readers one run's ops, hits and matching, and raises `NotComplete`
  unless the record and its ops are complete.
- `analyze_step.py`: the step decisions inside `CutPhysicsStepPlayModeTests.TheStep_IsDecidedOncePerFrame...`, by reason, with the clock, budget and estimate they read. It runs the check first.

## Use

1. Run with the diagnosis on, keeping the run's log. In the Editor, give `-logFile <editor.log>`. For example, the PlayMode
   tests: `ZTK_STEP_DIAG=1` set, then
   `Unity.exe -batchmode -projectPath <repo> -runTests -testPlatform PlayMode -assemblyNames Zantetsu.PhysicsCut.PlayModeTests -testFilter "CutPhysicsStepPlayModeTests\.|StepDiagnosisPlayModeTests\." -testResults <results.xml> -logFile <editor.log>`.
   The test run's end ends the record.
2. Find the session file. The logger writes one file per play session:
   `C:\log\zantetsuken-vr\logger\<UTC>_<commit>_<guid>.jsonl`. Take the file created during the run that holds records of
   writer `CutPhysicsStep`. Listing the folder before and after the run is enough.
3. Check it against the log:
   `python check_writer_jsonl.py CutPhysicsStep "step diagnosis" <session.jsonl> <editor.log>`
   - COMPLETE (exit 0): every record from seq 1 to the explicit end was saved, none was refused, and none came after the summary.
   - PARTIAL (exit 2): complete up to the summary, but there are records after it. The run is not complete.
   - NOT CONFIRMED (exit 1): no end line, a refusal, a missing or repeated seq, the summary not saved, or a cut tail.
   For `ViewDiagnosis`: `python check_writer_jsonl.py ViewDiagnosis "view diagnosis" <session.jsonl> <log> [--recording R]`,
   and for `CheckHits`: `python check_writer_jsonl.py CheckHits "check hits" <session.jsonl> <player.log> [--recording R]`.
   Each recording is checked on its own; the run is COMPLETE only if every recording is.
4. Analyse: `python analyze_step.py <session.jsonl>:<editor.log>`, `python analyze_view.py <session.jsonl>:<log>`, `python analyze_hits.py <session.jsonl>:<player.log>`, or `python analyze_ops.py <session.jsonl>:<player.log>`. Give both, joined by `:`; absolute paths with drive
   letters are fine. Only a COMPLETE run is analysed; any other run is reported as "not analysed" with the check's verdict.

### The view diagnosis in the Player

Start the Development Player with `-zantetsuViewDiag` and `-logFile <player.log>`. End it normally (close its window, or
Application.Quit): the record ends at the quit request, before the logger stops. Then find the session file as above and
check it against `player.log`. Each run of the diagnosis adds load: formatting each record is on the main thread.

### The check's hits

The check runs only in the Player, by its arguments. The hit record ends at the check's records' close, in its ending,
before `Application.Quit`. An ending through `Application.quitting` (a forced one) comes after the logger has stopped, and
leaves that recording NOT CONFIRMED. `CheckHitLogPlayModeTests` exercise the recording itself in the Editor: no hit, two
iterations, an early end, a refusal, and after the end. `CheckOpLogPlayModeTests` exercise the op records: the multi-NPC and
building columns, unobserved times written null, operation 0, repeated rows, none accepted against not applicable, a
summary that never reached the ops, a refused op, and after the end. The op records come from the summary, so an ending
that never runs it (an early ending with a fixed code, or a forced one) leaves `opsPlanned` null, and the ops are not
analysed.

### In the Editor

`ViewDiagnosisPlayModeTests` start and stop their own recorders. They need no environment variable, and they record
whenever they run. `beforeRender` samples need something rendering, and a batch-mode Editor renders nothing. To see them,
run the tests without `-batchmode`.

### The dedicated end test

`StepDiagnosisPlayModeTests.StepDiagnosis_On_AfterTheEnd_NothingMoreIsRecorded` ends the session's record itself. It
therefore runs only in a launch of its own, with both `ZTK_STEP_DIAG=1` and `ZTK_STEP_DIAG_END_TEST=1` set and the filter
`StepDiagnosisPlayModeTests\.StepDiagnosis_On_`. In any other run it is skipped (ignored, not passed).

## Limits

- An end that does not reach the logger while it still accepts leaves the record NOT CONFIRMED. Examples: leaving Play in
  the Editor before the test run's end or before the view recorder is stopped, a killed or crashed process, or an exit
  that does not pass Unity's quit request.
- The logger's drain at the end is bounded (a 1 s join). Whether the summary was saved is decided after the fact, by the check.
- With the diagnosis on, each record is formatted on the main thread. Timings taken with it on carry that cost.
