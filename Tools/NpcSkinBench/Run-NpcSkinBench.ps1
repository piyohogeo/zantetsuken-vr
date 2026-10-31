#Requires -Version 5.1
<#
.SYNOPSIS
  One launch of the NPC skin benchmark (DESIGN 3.4, npc-skin-bench) on a PC under the Meta XR Simulator: one condition
  (route, count, pose, shadows), its results under <RunRoot>\<Name>\run (bench-frames.csv, bench-result.json/.txt, the picture).
.DESCRIPTION
  The Player is the scenario Player (Tools\Build-ScenarioPlayer.ps1 -Direct3D12, or the unit's build) that carries the
  NpcSkinBench scene. The launch is made through the city walk's Simulator launcher (Invoke-XrSimPlayer.ps1: the Simulator
  selected for this process only, its settings saved and restored, no head replay: the head stays where the Simulator puts
  it). Each route is a launch of its own: -Route smr gives -zantetsuSkinUnity, -Route skin24 gives -zantetsuSkinDraw. The
  The launcher is not in this repository: it is unit X1 (2026-10-03) of the city walk record, generalised from
  RunXr207.ps1, kept at <walk record>\repro-draft\final8\Tools\XrSim\Invoke-XrSimPlayer.ps1 (31,673 bytes, sha256
  778b280d5af301524259ed715b20702756e117df61513460f86a4bf91d4aa0fc), and needs the Meta XR Simulator v207 installed; give
  its path as -Invoke (mandatory). Another launcher may be given if it takes the same parameters (-Name, -RunRoot, -Exe,
  -Arguments, -RecordFiles, the Simulator's selection and restore).
  measurement is -MeasureFrames frames after the pose clock's resynchronisation and -SettleFrames (SKIN-BENCH-2): the same
  frames and steps in every launch. The Player exits by itself: 0 a valid sample, 23 completed but not a valid sample of
  the route asked for (fallback or refusals), 14 failed. Whether the comparison holds (the eye within -PoseToleranceMetres /
  -PoseToleranceDegrees, the rows in view) is in bench-result.json (comparisonValid), apart from the route's validity.
  The launcher returns normally whenever its own cleanup succeeded, whatever the Player did; this script then reads the
  end record (exit.txt: its last 'exit <code>' line) and the result, and throws -- a failure of this launch, a fault of
  the matrix -- when the record is missing, the Player was killed at the timeout or in the cleanup, its exit code is
  neither 0 nor 23, or bench-result.json is missing (TL review, 2026-10-31). Exit 23 is reported and returns normally:
  the result says why the sample is not valid.
.EXAMPLE
  .\Run-NpcSkinBench.ps1 -Name n64-skin24-update-cascade1 -Exe <Player.exe> -RunRoot <folder> -Route skin24 -Count 64 -Pose update -Shadows cascade1 -Invoke <path>\Invoke-XrSimPlayer.ps1
#>
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$RunRoot,
    [Parameter(Mandatory = $true)][ValidateSet('smr', 'skin24')][string]$Route,
    [ValidateRange(1, 512)][int]$Count = 64,
    [ValidateSet('stop', 'update')][string]$Pose = 'update',
    [ValidateSet('off', 'cascade1', 'cascade4')][string]$Shadows = 'cascade4',
    [ValidateRange(1, 64)][int]$Families = 8,
    [double]$WarmupSeconds = 10,
    [double]$MeasureSeconds = 20,
    [ValidateRange(0, 1000000)][int]$MeasureFrames = 1440,   # 20 s of 72 Hz Simulator frames; 0 measures by seconds
    [ValidateRange(0, 100000)][int]$SettleFrames = 30,
    [double]$PoseToleranceMetres = 0.01,
    [double]$PoseToleranceDegrees = 1,
    [switch]$FixedView,
    [ValidateRange(0, 1000000)][int]$PaletteJoinGap = 0,     # diagnosis: the skin palette writes joined across this many matrices (SKIN-BENCH-2 section 6)
    [ValidateSet('sync', 'worker')][string]$Gather = 'sync',  # SKIN-BENCH-3: the palette gathering (-zantetsuSkinGather)
    [ValidateRange(0, 100000)][int]$WorkerFrom = 0,           # SKIN-BENCH-3: dirty individuals from which the worker gathers (-zantetsuSkinGatherWorkerFrom; 0 always)
    [switch]$Skin24Slice,                                     # SKIN-BENCH-3: the skin draw's casters select by slice on Vulkan (-zantetsuSkin24SliceSelection)
    [ValidateRange(-1, 512)][int]$UpdateCount = -1,           # SKIN-BENCH-3: individuals posed each frame under update (-1 every one)
    [ValidateRange(0, 1000)][int]$CaptureFrames = 0,         # diagnosis: frames captured after the measurement by an attached external GPU profiler
    [string]$ProfilerFile = '',            # diagnosis: the Profiler's binary log of the measurement (such a run is not a sample)
    [string[]]$Extra = @(),
    [int]$TimeoutSeconds = 600,
    [Parameter(Mandatory = $true)][string]$Invoke,   # the Simulator launcher Invoke-XrSimPlayer.ps1 (outside this repository; its identity in .DESCRIPTION)
    [string]$RepositoryRoot = ''
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) { throw "Player not found: $Exe" }
if (-not (Test-Path -LiteralPath $RunRoot -PathType Container)) { throw "run root not found: $RunRoot" }
if (-not (Test-Path -LiteralPath $Invoke -PathType Leaf)) { throw "Invoke-XrSimPlayer.ps1 not found: $Invoke" }
$RunRoot = (Resolve-Path -LiteralPath $RunRoot).ProviderPath
$run = Join-Path $RunRoot $Name
if (Test-Path -LiteralPath $run) { throw "exists: $run" }
$routeArgument = if ($Route -eq 'skin24') { '-zantetsuSkinDraw' } else { '-zantetsuSkinUnity' }
$inv = [Globalization.CultureInfo]::InvariantCulture
$a = @('-screen-fullscreen', '0', '-screen-width', '960', '-screen-height', '600',
    '-zantetsuScenario', 'npc-skin-bench', '-zantetsuNpcSkinBench', "$run\run",
    '-zantetsuNpcSkinBenchCount', "$Count", '-zantetsuNpcSkinBenchPose', $Pose, '-zantetsuNpcSkinBenchShadows', $Shadows,
    '-zantetsuNpcSkinBenchFamilies', "$Families",
    '-zantetsuNpcSkinBenchWarmupSeconds', $WarmupSeconds.ToString($inv), '-zantetsuNpcSkinBenchMeasureSeconds', $MeasureSeconds.ToString($inv),
    '-zantetsuNpcSkinBenchMeasureFrames', "$MeasureFrames", '-zantetsuNpcSkinBenchSettleFrames', "$SettleFrames",
    '-zantetsuNpcSkinBenchPoseToleranceMetres', $PoseToleranceMetres.ToString($inv), '-zantetsuNpcSkinBenchPoseToleranceDegrees', $PoseToleranceDegrees.ToString($inv),
    '-zantetsuNpcSkinBenchPaletteJoinGap', "$PaletteJoinGap",
    '-zantetsuSkinGather', $Gather,
    $routeArgument)
if ($UpdateCount -ge 0) { $a += @('-zantetsuNpcSkinBenchUpdateCount', "$UpdateCount") }   # -1 (every individual) is the Player's default: not passed (a Player before SKIN-BENCH-3's parser fix refused the negative literal)
if ($FixedView) { $a += '-zantetsuNpcSkinBenchFixedView' }
if ($Skin24Slice) { $a += '-zantetsuSkin24SliceSelection' }
if ($WorkerFrom -gt 0) { $a += @('-zantetsuSkinGatherWorkerFrom', "$WorkerFrom") }
if ($CaptureFrames -gt 0) { $a += @('-zantetsuNpcSkinBenchCaptureFrames', "$CaptureFrames") }
if ($ProfilerFile) { $a += @('-zantetsuNpcSkinBenchProfiler', $ProfilerFile) }
if ($Extra.Count -gt 0) { $a += $Extra }
$record = [ordered]@{
    tool = $PSCommandPath; tool_sha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant(); recorded_at = (Get-Date).ToString('o')
    exe = $Exe; exe_sha256 = (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
    game_assembly_sha256 = $(if (Test-Path -LiteralPath (Join-Path (Split-Path $Exe) 'GameAssembly.dll')) { (Get-FileHash -LiteralPath (Join-Path (Split-Path $Exe) 'GameAssembly.dll') -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null })
    route = $Route; count = $Count; pose = $Pose; shadows = $Shadows; families = $Families; warmup_seconds = $WarmupSeconds; measure_seconds = $MeasureSeconds
    measure_frames = $MeasureFrames; settle_frames = $SettleFrames; pose_tolerance_metres = $PoseToleranceMetres; pose_tolerance_degrees = $PoseToleranceDegrees
    fixed_view = [bool]$FixedView; palette_join_gap = $PaletteJoinGap; capture_frames = $CaptureFrames; profiler_file = $ProfilerFile; player_arguments = $a; run_directory = $run
}
$common = @{ Exe = $Exe; RunRoot = $RunRoot; Name = $Name; PlayerArgs = $a; TimeoutSeconds = $TimeoutSeconds }
if ($RepositoryRoot) { $common.RepositoryRoot = $RepositoryRoot }
$launchError = $null
try {
    & $Invoke @common -RecordFiles @{ 'npc-skin-bench-inputs.json' = ([pscustomobject]$record | ConvertTo-Json -Depth 5) } 2>&1 |
        Where-Object { "$_" -match '^(exit|git|TIMEOUT|restored|WARNING|ERROR|FAILURES|cleanup|simulator log)' }
} catch { $launchError = $_ }
$result = Join-Path $run 'run\bench-result.txt'
if (Test-Path -LiteralPath $result) { Get-Content -LiteralPath $result -Encoding UTF8 } else { "no bench-result.txt in $run\run" }
$exitFile = Join-Path $run 'exit.txt'
if (Test-Path -LiteralPath $exitFile) { Get-Content -LiteralPath $exitFile -Encoding UTF8 | Select-Object -First 3 }
if ($launchError) { throw $launchError }
# The end record and the result, checked here (the launcher threw only for its own failures): see .DESCRIPTION.
if (-not (Test-Path -LiteralPath $exitFile)) { throw "no end record: $exitFile is missing (the launcher left no exit.txt)" }
$exitLines = @(Get-Content -LiteralPath $exitFile -Encoding UTF8)
$exitLine = @($exitLines | Where-Object { $_ -match '^exit \S+' } | Select-Object -Last 1)
if ($exitLine.Count -eq 0) { throw "the end record has no 'exit <code>' line: $exitFile" }
$code = ([regex]::Match($exitLine[0], '^exit (\S+)')).Groups[1].Value
if ($code -eq 'timeout' -or ($exitLines | Where-Object { $_ -match '^TIMEOUT' }).Count -gt 0) { throw "the Player did not end within $TimeoutSeconds s and was killed (exit.txt: '$($exitLine[0])'; see $run\player.log)" }
if ($code -notmatch '^\d+$') { throw "the Player did not end by itself (exit.txt: '$($exitLine[0])'; see $run\exit.txt)" }
if ($code -ne '0' -and $code -ne '23') { throw "the Player ended with exit $code (0 a valid sample, 23 completed but not a sample, 14 failed; see $run\exit.txt and $run\player.log)" }
$resultJson = Join-Path $run 'run\bench-result.json'
if (-not (Test-Path -LiteralPath $resultJson)) { throw "the Player ended with exit $code but left no result: $resultJson is missing" }
if ($code -eq '23') { "NOTE: the Player completed but its sample is not valid for the route asked (exit 23): see $result" }
