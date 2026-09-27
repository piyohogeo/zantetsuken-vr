# MobPlan probe intake

Runtime numeric code originates in `zantetsuken-character-animation-probe/Assets/LocomotionSearch/Runtime`.
The product uses the same INERTIAL search, graph, stop tails and PlayerFlow rules. Its host owns scheduling,
publication, the real player, withdrawal and replacement. Probe attack/respawn fallbacks and `Task.Run` are excluded.

Licensed data lives in the private repository at `MobPlan/ITHappy_f_1` and installs into ignored
`Assets/Licensed/MobPlan`. No licensed table or mesh belongs in this public repository.

1. Run `Install-PrivateMobPlan.ps1` after fetching the private repository's LFS objects.
2. The existing `Compact16uvConvexRepair`, `U8Npc` and `XrSimCity` licensed fixtures supply the audited Casual rig,
   convexes, materials and city scene. Install these through the existing private intake workflow.
3. Open `Assets/Licensed/MobPlan/MobPlanCity.unity`. Rebuild with **Zantetsu > MobPlan > Build imported city scene**
   when script metadata or the source city changes. Press Play and wait for `MobPlan ready`.

To regenerate the reduced bank from the probe's C0 output:

```powershell
# Unity batch executeMethod: Zantetsu.Sandbox.Editor.MobPlanIntake.ExportRequiredBones
python Tools/MobPlan/import_probe.py `
  --probe ../zantetsuken-character-animation-probe `
  --run 20260927-112952-ITHappy_f_1-inertial-rootmotion-yaw-30_-20_-10_10_20_30-v0.3-locomotion `
  --paths Assets/Licensed/MobPlan/required-bones.txt `
  --output Assets/Licensed/MobPlan
```

The exporter checks the rig's nonzero weights, renderer/root references, hull bones and ancestor closure.
The importer copies the required sample bytes, keeps root/timing/contact data, remaps parent/humanoid indices,
and writes source/output SHA-256 hashes. It does not rebake or rerun C0. 320 tables / 21,334 samples shrink
from 414,867,220 to 42,747,116 bytes. Runtime yaw variants share the same bone arrays.

`MobPlanPreset` records the screenshot parameters. The city has a fixed `(390, .1, 150)` offset from the probe
polygon coordinates. NPCs and artificial player movement share the warmed fixed map; cuts do not alter it.
The plan worker is one Normal-priority thread, with no CPU affinity. The 800 ms search allowance is elapsed
wall time, including preemption; queue and goal-field delays reduce the available publication allowance.
The main thread never waits for a cycle. Pending requests are not accumulated; stale group results are discarded.

`MobPlanPlayVerification.Run` is an opt-in Unity batch smoke test with 20 live NPCs and an actual current-pose cut.
Logs and measurements go under ignored `Logs/MobPlan`. Unit tests cover time queries, the circle slide rule,
and planning progress while the Background pool is blocked. Editor measurements do not establish VR frame time.

## IL2CPP worker measurement

The opt-in `MobPlanProfileBuild.Run` entry builds the existing imported city as a Windows x64 IL2CPP
Development Player with the project's compiler settings. It temporarily disables XR initialization and restores
that setting in `finally`; this is a mono-rendered CPU workload measurement. It adds only the
`ZANTETSU_MOBPLAN_PROFILE` diagnostic define. Normal builds do not contain the worker CPU probe or automatic run.

```powershell
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
$out = 'C:\log\zantetsuken-vr\MobPlanIL2CPP'
New-Item -ItemType Directory -Force -Path $out | Out-Null
Start-Process $unity -WindowStyle Hidden -Wait -ArgumentList @(
  '-batchmode', '-projectPath', ('"{0}"' -f (Get-Location).Path),
  '-executeMethod', 'Zantetsu.Sandbox.Editor.MobPlanProfileBuild.Run',
  '-zantetsuPlayerOut', "$out\player", '-logFile', "$out\build.log")
# Run one Player at a time, after the build has exited.
Start-Process "$out\player\CutWorldSandbox.exe" -WindowStyle Hidden -Wait -ArgumentList @(
  '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '720',
  '-mobPlanProfile', "$out\moving", '-mobPlanScenario', 'moving',
  '-mobPlanSeconds', '120', '-logFile', "$out\moving.log")
python Tools/MobPlan/analyze_profile.py "$out\moving"
```

`-mobPlanScenario` accepts `stationary`, `moving`, or `cutting`. Each run warms up for 10 seconds after map/table
loading, then measures for `-mobPlanSeconds`. Moving input repeats a 24-second forward/reverse/turn sequence
through the normal circle occupancy. Cutting additionally requests one current-pose cut approximately every
20 seconds while the planner is busy. The scene keeps 20 live NPCs through the ordinary replacement path.
Rendering stays enabled; VSync is off, target rate is 90, and the application runs in the background.

`cycles.csv` records dispatch wait, the whole worker body, collection wait, Windows `GetThreadTimes` CPU
time (kernel + user), acceptance and planner phase/candidate/LOD counters. `frames.csv` records frame intervals,
live count and plan expiration. `summary.txt`, `environment.txt` and `cuts.csv` record validation and conditions.
The analyzer excludes cycles requested before the warmup boundary, reports upper order-statistic percentiles
(`sorted[ceil((n - 1) * p)]`),
and normalizes CPU usage to **one logical core**. Whole-worker time includes goal fields and publication data
construction, whereas the planner's `timeout` also includes reaching an individual candidate search slice;
it does not by itself mean the publication deadline was missed. Loading is reported separately in the Player log.
Completed cycles only are counted; a job still in flight when the process exits is right-censored.
