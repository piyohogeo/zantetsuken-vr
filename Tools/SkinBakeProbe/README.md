# Skin bake probe tools (DESIGN 4.5.14 / 4.5.15)

Measurement and evaluation tools of the `skin-bake-probe` scenario: the GPU bake probe and its cut bench, which measure the
skin bake routes (`-zantetsuSkinBake cpu|float|gpu`, default cpu) of a prepared character's cut. None of them changes the
product's defaults; every launch names its route explicitly.

## Launch

- PC (Meta XR Simulator, D3D12 Release Player): `Run-SkinBakeProbe.ps1 -Name <new run name> -Exe <Player>\CutWorldSandbox.exe -RunRoot <folder> -Invoke <path>\Invoke-XrSimPlayer.ps1 -RepositoryRoot <repo> [-Subject npc -CutBench <bodies> -SkinBake cpu|float|gpu -BenchFrames 120 -BenchWarmup 60 -CutRounds 10 -CutPlane horizontal -CutNoVerify -NoPicture ...]`.
  The Player is built by `Tools/Build-ScenarioPlayer.ps1` (the probe scene is carried when `SkinBakeProbeSceneBuild` has made it from the licensed source scene; `Assets/Licensed` is not in the repository).
- Quest 3S (Vulkan Release APK): `Tools/Quest3s/Build-SkinBakeProbe.ps1 -Output <apk>` builds the APK (package `com.zantetsuken.quest.skinbakeprobe`), `Tools/Quest3s/Run-SkinBakeProbe.ps1 [-Apk <apk>] -PullTo <folder> [the same bench arguments]` installs the APK when one is given (otherwise the installed package runs), pushes the configuration, runs one launch and pulls the record. The device serial and the adb executable are parameters, with the same defaults as the other `Tools/Quest3s/Run-*.ps1` scripts.

A run folder holds `skin-bake-probe.md/.json` (facts, cases), `skin-bake-cut-requests.csv`, `skin-bake-cut-events.csv`, `skin-bake-frame-marks.csv`, `skin-bake-ftm-raw.csv`, `skin-bake-cut-frames.csv`, `skin-bake-stats.csv` and, in verification runs, pictures, readbacks and motion captures.

## Evaluate

| Tool | Unit | What it reads and says |
|---|---|---|
| `Summarize-SkinBakeEval.py <run folders...> --window 32 [--out .md] [--json .json]` | SKIN-GPU-FIRST-EVAL-1 | The cut bench per round: the hit frame and the frames after it (FTM attributed by start timestamp), per-request reaction deltas from the hooks, the frame-level metrics and the local Main total per request apart, round 0 apart, invalid rounds left out, comparisons within/beyond the spread, grouped by platform and build. |
| `Test-SkinBakeEvalSummary.py` | EVAL-1 | 19 synthetic negatives of the summarizer (exit 0 = all pass). |
| `Summarize-SkinBakeStartup.py` | SKIN-GPU-FIRST-STARTUP-1 | The frames around each request from the clock-attributed marks (first-time costs). |
| `Summarize-SkinBakeCut.py` | SKIN-GPU-FIRST-1/-2 | The earlier cut bench summary (verification runs, per request). |
| `Compare-SkinBakeCutEvents.py`, `Compare-SkinBakeCutPictures.py` | PLANE-1, SKIN-LINES-1 | Verification runs' pictures compared by event and across routes/shadow conditions. |
| `Evaluate-SkinBakeMotionCut.py`, `Evaluate-SkinBakeMotionCheck.py`, `Test-SkinBakeMotionEvaluators.py` | MOTION-CAPTURE-1 | The AppSW motion captures of a Quest verification run evaluated at the bodies' pieces with shape masks; the capture-method check; negatives on a saved run. |
| `Summarize-SkinBakeBench.py`, `Summarize-SkinBakeBench-Series.py` | SKIN-BAKE-PROBE-2/-3 | The probe's bench runs (no cut). |
| `Compare-SkinBakeProbe-Pictures.py` | SKIN-BAKE-PROBE-1/-2 | The probe's picture pairs (CPU vs GPU bake). |

Python 3.8+. The summaries (`Summarize-*.py`, `Test-SkinBakeEvalSummary.py`) use the standard library only; the picture and motion tools (`Compare-*.py`, `Evaluate-*.py`, `Test-SkinBakeMotionEvaluators.py`) need numpy and Pillow. Run the tools from any directory; they take run folders as arguments.

## Outside this repository

- The PC launch goes through the city walk's Simulator launcher `Invoke-XrSimPlayer.ps1` (the repro-draft tree, not in this repository); `Run-SkinBakeProbe.ps1` takes its path with `-Invoke`.
- The series scripts that run 12 launches per platform (`Run-PC-CutBench.ps1`, `Run-Quest-CutBench.ps1`), the Unity batch chains (`Run-Bench-Chain*.ps1`) and the measurement records (`perf/skin-gpu-first/*.md`, the run folders, summaries, APKs and Players) live in the measurement log tree, not here. They are not needed to run one launch or to evaluate a saved run folder.
- The Quest tools use the adb that owns the device (one adb version only; see `Tools/Quest3s`).
