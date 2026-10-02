# Development log readers

Readers for diagnosis records written to the DEBUG-only development logger (DESIGN 21.17, D-021). The logger is best
effort: an accepted record is queued, not known to be saved, and the session's drain at the end is bounded. A record is
therefore used only after the saved file has been checked against the counts its run wrote to the log. These readers are
diagnosis tools, not product checks: no product test's pass or fail depends on them.

## Writers

| writer_id | Source | Turned on by | End line in the log |
|---|---|---|---|
| `CutPhysicsStep` | `Assets/Zantetsu/Runtime/PhysicsCut/CutPhysicsStep.Diagnosis.cs`: each step decision; PlayMode test marks from `StepDiagnosisAction.cs` | `ZTK_STEP_DIAG=1` in the environment when the play session starts (DEBUG builds only) | `[step diagnosis] ended: attempted N (seq 1..N, the summary last), accepted A, refused: ...` |

A record's value is a flat list of field names and values, starting with `seq` (1..N, one per record tried), `eventFrame`
(the frame when the record was made) and `seconds` (Stopwatch since the play session's reset; a decision's own reading).
The record has an explicit end: the summary is the last record (seq N, its own counts of seq 1..N-1), and nothing is
recorded after it. The end line in the log counts seq 1..N, the summary included.

## Files

- `check_writer_jsonl.py`: the completeness check, per writer.
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
4. Analyse: `python analyze_step.py <session.jsonl>:<editor.log>`. Give both, joined by `:`; absolute paths with drive
   letters are fine. Only a COMPLETE run is analysed; any other run is reported as "not analysed" with the check's verdict.

### The dedicated end test

`StepDiagnosisPlayModeTests.StepDiagnosis_On_AfterTheEnd_NothingMoreIsRecorded` ends the session's record itself. It
therefore runs only in a launch of its own, with both `ZTK_STEP_DIAG=1` and `ZTK_STEP_DIAG_END_TEST=1` set and the filter
`StepDiagnosisPlayModeTests\.StepDiagnosis_On_`. In any other run it is skipped (ignored, not passed).

## Limits

- An end that does not reach the logger while it still accepts leaves the record NOT CONFIRMED. Examples: leaving Play in
  the Editor before the test run's end, or a killed or crashed process.
- The logger's drain at the end is bounded (a 1 s join). Whether the summary was saved is decided after the fact, by the check.
- With the diagnosis on, each record is formatted on the main thread. Timings taken with it on carry that cost.
