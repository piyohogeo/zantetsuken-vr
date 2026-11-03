#Requires -Version 5.1
<#
.SYNOPSIS
  One launch of the GPU bake probe (DESIGN 4.5.14, skin-bake-probe) on a PC under the Meta XR Simulator: the probe's record
  (skin-bake-probe.md/.csv/.json and the pictures) under <RunRoot>\<Name>\run.
.DESCRIPTION
  The Player is the scenario Player that carries the SkinBakeProbe scene (Tools\Build-ScenarioPlayer.ps1 -Direct3D12 with the
  scene made by SkinBakeProbeSceneBuild). The launch goes through the city walk's Simulator launcher Invoke-XrSimPlayer.ps1
  (unit X1 of the walk record, kept outside this repository; see Tools\NpcSkinBench\Run-NpcSkinBench.ps1 for its identity):
  give its path as -Invoke. The Player exits by itself: 0 every case passed, 23 the probe ran but a case failed (the record says
  which), 14 the probe could not run. The end record (exit.txt) and the record file are checked here: a Player failure, a
  timeout or a missing record is a failure of this launch.
.EXAMPLE
  .\Run-SkinBakeProbe.ps1 -Name probe1 -Exe <Player.exe> -RunRoot <folder> -Invoke <path>\Invoke-XrSimPlayer.ps1
#>
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$RunRoot,
    [ValidateSet('both', 'small', 'npc')][string]$Subject = 'both',
    [ValidateRange(2, 100000)][int]$DrawFrames = 60,
    [switch]$NoDraw,
    [switch]$NoPicture,
    [ValidateSet('', 'burst', 'gpu')][string]$Bench = '',       # SKIN-BAKE-PROBE-2: the performance comparison of one route in place of the cases
    [ValidateRange(10, 100000)][int]$BenchFrames = 300,
    [ValidateRange(0, 10000)][int]$BenchWarmup = 60,
    [ValidateRange(1, 8)][int]$BenchBodies = 1,
    [string]$BenchSeries = '',       # SKIN-BAKE-PROBE-3: the bench as a series of conditions (route+bodies[xrepeat], comma separated) in place of -Bench
    [ValidateRange(0, 64)][int]$BenchLoad = 0,
    [ValidateRange(0, 8)][int]$CutBench = 0,        # SKIN-GPU-FIRST-1: the cut bench's bodies (the product's prepared cuts under -SkinBake)
    [ValidateRange(0, 60)][int]$CutStagger = 0,
    [ValidateRange(1, 20)][int]$CutRounds = 1,
    [switch]$CutNoVerify,            # the cut bench as a performance run: no picture, no readback in the measured frames
    [ValidateRange(0, 60)][int]$CutPictureFrames = 0,   # SKIN-LINES-1: verification runs picture every frame from the last warmup frame to +N after the last request
    [ValidateSet('both', 'nocast', 'noreceive', 'none')][string]$Shadows = 'both',   # SKIN-LINES-1: the shadow condition (-zantetsuSkinBakeProbeShadows)
    [switch]$CutSurfaceDebug,        # SKIN-LINES-1 step 2: the cut-surface debug colours (real caps green, temporary faces red)
    [switch]$CapsOff,                # SKIN-LINES-1 step 2: the stencil cap pass withheld
    [ValidateSet('legacy', 'horizontal')][string]$CutPlane = 'legacy',   # PLANE-1: the cut bench's plane (-zantetsuSkinBakeProbeCutPlane)
    [ValidateRange(0, 60)][int]$HoldCpuReady = 0,       # PLANE-1 diagnosis: the gpu route's CPU bake held out of the dispatcher N frames
    [switch]$MotionCapture,          # PLANE-1 diagnosis: the AppSW motion output of the pictured frames read back (no effect without the motion pass)
    [switch]$MotionCheck,            # MOTION-CAPTURE-1: the capture-method check in place of the cases
    [double]$MotionCheckStepCm = 1.0,
    [ValidateRange(1, 120)][int]$MotionCheckFrames = 6,
    [ValidateRange(1, 120)][int]$MotionCheckHold = 12,
    [switch]$MotionView,             # MOTION-CAPTURE-1 step 6: the cut bench's eye follows the check's sequence from the first request (with -MotionCapture; StepCm/Frames/Hold above)
    [ValidateSet('none', 'draw', 'skin-prepare', 'skin-dispatch', 'draw+skin-dispatch')][string]$Warm = 'none',   # SKIN-GPU-FIRST-STARTUP-1: the preparation before the bench (the PC has no QuestDrawPrewarm: 'draw' is recorded as not warmed)
    [ValidateSet('', 'cpu', 'gpu', 'float')][string]$SkinBake = '',   # -zantetsuSkinBake cpu|gpu|float ('' leaves the Player's default: cpu)
    [double]$EdgeRight = 0.95,       # the edge windows' x (metres along the root): the PC picture's right edge (partly cut) and left edge
    [double]$EdgeLeft = -4.1,
    [ValidateSet('native', 'default')][string]$BodyRoute = 'native',   # -zantetsuVp3cNative (the plugin's indirect route) or the Player's default
    [string[]]$Extra = @(),
    [int]$TimeoutSeconds = 600,
    [Parameter(Mandatory = $true)][string]$Invoke,
    [string]$RepositoryRoot = ''
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "Player not found: $Exe" }
if (-not (Test-Path -LiteralPath $RunRoot -PathType Container)) { throw "run root not found: $RunRoot" }
if (-not (Test-Path -LiteralPath $Invoke -PathType Leaf)) { throw "Invoke-XrSimPlayer.ps1 not found: $Invoke" }
$RunRoot = (Resolve-Path -LiteralPath $RunRoot).ProviderPath
$run = Join-Path $RunRoot $Name
if (Test-Path -LiteralPath $run) { throw "exists: $run" }
$a = @('-screen-fullscreen', '0', '-screen-width', '960', '-screen-height', '600',
    '-zantetsuScenario', 'skin-bake-probe', '-zantetsuSkinBakeProbe', "$run\run", '-zantetsuSkinBakeProbeSubject', $Subject, '-zantetsuSkinBakeProbeDrawFrames', "$DrawFrames")
if ($NoDraw) { $a += '-zantetsuSkinBakeProbeNoDraw' }
if ($NoPicture) { $a += '-zantetsuSkinBakeProbeNoPicture' }
$a += @('-zantetsuSkinBakeProbeEdgeRight', $EdgeRight.ToString([Globalization.CultureInfo]::InvariantCulture), '-zantetsuSkinBakeProbeEdgeLeft', $EdgeLeft.ToString([Globalization.CultureInfo]::InvariantCulture))
if ($BenchSeries) { $a += @('-zantetsuSkinBakeProbeBenchSeries', $BenchSeries, '-zantetsuSkinBakeProbeBenchFrames', "$BenchFrames", '-zantetsuSkinBakeProbeBenchWarmup', "$BenchWarmup") }
if ($BenchLoad -gt 0) { $a += @('-zantetsuSkinBakeProbeBenchLoad', "$BenchLoad") }
if ($CutBench -gt 0) { $a += @('-zantetsuSkinBakeProbeCutBench', "$CutBench", '-zantetsuSkinBakeProbeCutStagger', "$CutStagger", '-zantetsuSkinBakeProbeCutRounds', "$CutRounds", '-zantetsuSkinBakeProbeBenchFrames', "$BenchFrames", '-zantetsuSkinBakeProbeBenchWarmup', "$BenchWarmup") }
if ($CutNoVerify) { $a += '-zantetsuSkinBakeProbeCutNoVerify' }
if ($CutPictureFrames -gt 0) { $a += @('-zantetsuSkinBakeProbeCutPictureFrames', "$CutPictureFrames") }
if ($Shadows -ne 'both') { $a += @('-zantetsuSkinBakeProbeShadows', $Shadows) }
if ($CutSurfaceDebug) { $a += '-zantetsuSkinBakeProbeCutSurfaceDebug' }
if ($CapsOff) { $a += '-zantetsuSkinBakeProbeCapsOff' }
if ($CutPlane -ne 'legacy') { $a += @('-zantetsuSkinBakeProbeCutPlane', $CutPlane) }
if ($HoldCpuReady -gt 0) { $a += @('-zantetsuSkinBakeProbeHoldCpuReady', "$HoldCpuReady") }
if ($MotionCapture) { $a += '-zantetsuSkinBakeProbeMotionCapture' }
if ($MotionCheck) { $a += @('-zantetsuSkinBakeProbeMotionCheck', '-zantetsuSkinBakeProbeMotionCheckStepCm', $MotionCheckStepCm.ToString([Globalization.CultureInfo]::InvariantCulture), '-zantetsuSkinBakeProbeMotionCheckFrames', "$MotionCheckFrames", '-zantetsuSkinBakeProbeMotionCheckHold', "$MotionCheckHold") }
if ($MotionView) { $a += @('-zantetsuSkinBakeProbeMotionView', '-zantetsuSkinBakeProbeMotionCheckStepCm', $MotionCheckStepCm.ToString([Globalization.CultureInfo]::InvariantCulture), '-zantetsuSkinBakeProbeMotionCheckFrames', "$MotionCheckFrames", '-zantetsuSkinBakeProbeMotionCheckHold', "$MotionCheckHold") }
if ($Warm -ne 'none') { $a += @('-zantetsuSkinBakeProbeWarm', $Warm) }
if ($SkinBake) { $a += @('-zantetsuSkinBake', $SkinBake) }
if ($Bench) { $a += @('-zantetsuSkinBakeProbeBench', $Bench, '-zantetsuSkinBakeProbeBenchFrames', "$BenchFrames", '-zantetsuSkinBakeProbeBenchWarmup', "$BenchWarmup", '-zantetsuSkinBakeProbeBenchBodies', "$BenchBodies") }
if ($BodyRoute -eq 'native') { $a += '-zantetsuVp3cNative' }
if ($Extra.Count -gt 0) { $a += $Extra }
$record = [ordered]@{
    tool = $PSCommandPath; tool_sha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant(); recorded_at = (Get-Date).ToString('o')
    exe = $Exe; exe_sha256 = (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
    game_assembly_sha256 = $(if (Test-Path -LiteralPath (Join-Path (Split-Path $Exe) 'GameAssembly.dll')) { (Get-FileHash -LiteralPath (Join-Path (Split-Path $Exe) 'GameAssembly.dll') -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null })
    subject = $Subject; draw_frames = $DrawFrames; no_draw = [bool]$NoDraw; no_picture = [bool]$NoPicture; body_route = $BodyRoute; player_arguments = $a; run_directory = $run
    bench = $Bench; bench_frames = $BenchFrames; bench_warmup = $BenchWarmup; bench_bodies = $BenchBodies; edge_right = $EdgeRight; edge_left = $EdgeLeft
    bench_series = $BenchSeries; bench_load = $BenchLoad
    cut_bench = $CutBench; cut_stagger = $CutStagger; cut_rounds = $CutRounds; skin_bake = $SkinBake; cut_no_verify = [bool]$CutNoVerify
}
$common = @{ Exe = $Exe; RunRoot = $RunRoot; Name = $Name; PlayerArgs = $a; TimeoutSeconds = $TimeoutSeconds }
if ($RepositoryRoot) { $common.RepositoryRoot = $RepositoryRoot }
# The GPU's clocks, temperature and load sampled once a second during the launch where nvidia-smi exists (SKIN-BAKE-PROBE-3): the
# requested clocks are not taken as the actual ones. The log lands beside the run's record after the launch.
$smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
$clockLog = Join-Path $RunRoot ($Name + '-gpu-clocks.csv')
$clockProcess = $null
if ($smi) { $clockProcess = Start-Process -FilePath $smi.Source -ArgumentList @('--query-gpu=timestamp,clocks.sm,clocks.mem,clocks.gr,temperature.gpu,utilization.gpu,power.draw,pstate', '--format=csv', '-l', '1') -WindowStyle Hidden -PassThru -RedirectStandardOutput $clockLog }
$launchError = $null
try {
    & $Invoke @common -RecordFiles @{ 'skin-bake-probe-inputs.json' = ([pscustomobject]$record | ConvertTo-Json -Depth 5) } 2>&1 |
        Where-Object { "$_" -match '^(exit|git|TIMEOUT|restored|WARNING|ERROR|FAILURES|cleanup|simulator log)' }
} catch { $launchError = $_ }
if ($clockProcess) {
    try { Stop-Process -Id $clockProcess.Id -Force -ErrorAction SilentlyContinue } catch { }
    Start-Sleep -Seconds 1   # the redirected file is released after the process ends
    if ((Test-Path -LiteralPath $run) -and (Test-Path -LiteralPath $clockLog)) { try { Move-Item -LiteralPath $clockLog -Destination (Join-Path $run 'gpu-clocks.csv') -Force -ErrorAction Stop } catch { Copy-Item -LiteralPath $clockLog -Destination (Join-Path $run 'gpu-clocks.csv') -Force } }
}
$md = Join-Path $run 'run\skin-bake-probe.md'
if (Test-Path -LiteralPath $md) { Get-Content -LiteralPath $md -Encoding UTF8 | Select-Object -First 4 } else { "no skin-bake-probe.md in $run\run" }
$exitFile = Join-Path $run 'exit.txt'
if ($launchError) { throw $launchError }
if (-not (Test-Path -LiteralPath $exitFile)) { throw "no end record: $exitFile is missing" }
$exitLines = @(Get-Content -LiteralPath $exitFile -Encoding UTF8)
$exitLine = @($exitLines | Where-Object { $_ -match '^exit \S+' } | Select-Object -Last 1)
if ($exitLine.Count -eq 0) { throw "the end record has no 'exit <code>' line: $exitFile" }
$code = ([regex]::Match($exitLine[0], '^exit (\S+)')).Groups[1].Value
if ($code -eq 'timeout' -or ($exitLines | Where-Object { $_ -match '^TIMEOUT' }).Count -gt 0) { throw "the Player did not end within $TimeoutSeconds s and was killed (exit.txt: '$($exitLine[0])')" }
if ($code -notmatch '^\d+$') { throw "the Player did not end by itself (exit.txt: '$($exitLine[0])')" }
if ($code -ne '0' -and $code -ne '23') { throw "the Player ended with exit $code (0 every case passed, 23 a case failed, 14 the probe could not run; see $run\exit.txt and $run\player.log)" }
if (-not (Test-Path -LiteralPath $md)) { throw "the Player ended with exit $code but left no record: $md is missing" }
if ($code -eq '23') { "NOTE: the probe ran but a case failed (exit 23): see $md" }
"record: $md"
