#Requires -Version 5.1
<#
.SYNOPSIS
  The NPC skin benchmark's comparison matrix on a PC: the conditions (pose x shadows) for one count, both routes each,
  repeated, the two routes' order alternating between repeats (A/B, B/A, ...), one launch after another.
.DESCRIPTION
  The conditions are -Poses x -Shadows (SKIN-BENCH-2: the defaults are update x off/cascade1/cascade4). Every launch goes
  through Run-NpcSkinBench.ps1 with the same measurement (-MeasureFrames after -SettleFrames) and the same tolerances;
  nothing runs in parallel. Afterwards Summarize-NpcSkinBench.py writes the table (summary.md, summary.csv) under <RunRoot>,
  with each launch's route validity and comparison validity and the A/B start poses compared.
.EXAMPLE
  .\Run-NpcSkinBench-Matrix.ps1 -Exe <Player.exe> -RunRoot <folder> -Count 64 -Repeats 2 -Poses stop,update -Shadows off,cascade1,cascade4
#>
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$RunRoot,
    [ValidateRange(1, 512)][int]$Count = 64,
    [ValidateRange(1, 10)][int]$Repeats = 2,
    [ValidateSet('stop', 'update')][string[]]$Poses = @('update'),
    [ValidateSet('off', 'cascade1', 'cascade4')][string[]]$Shadows = @('off', 'cascade1', 'cascade4'),
    [double]$WarmupSeconds = 10,
    [double]$MeasureSeconds = 20,
    [int]$MeasureFrames = 1440,
    [int]$SettleFrames = 30,
    [double]$PoseToleranceMetres = 0.01,
    [double]$PoseToleranceDegrees = 1,
    [switch]$FixedView,
    [int]$PaletteJoinGap = 0,
    [ValidateSet('sync', 'worker')][string]$Gather = 'sync',
    [int]$WorkerFrom = 0,
    [switch]$Skin24Slice,
    [int]$UpdateCount = -1,
    [string[]]$Extra = @(),
    [Parameter(Mandatory = $true)][string]$Invoke,   # the Simulator launcher Invoke-XrSimPlayer.ps1 (see Run-NpcSkinBench.ps1 -Invoke)
    [string]$RepositoryRoot = ''
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $RunRoot)) { New-Item -ItemType Directory -Path $RunRoot | Out-Null }
$log = Join-Path $RunRoot 'matrix.log'
function Say([string]$text) { $line = "$((Get-Date).ToString('yyyy-MM-dd HH:mm:ss')) $text"; Add-Content -LiteralPath $log -Value $line -Encoding UTF8; Write-Output $line }
Say "matrix: count $Count, repeats $Repeats, poses $($Poses -join '/'), shadows $($Shadows -join '/'), measure $MeasureFrames frames (settle $SettleFrames), fixed view $([bool]$FixedView), join gap $PaletteJoinGap, exe $Exe"
$runs = @()
$faults = 0
for ($r = 1; $r -le $Repeats; $r++) {
    $order = if ($r % 2 -eq 1) { @('smr', 'skin24') } else { @('skin24', 'smr') }
    foreach ($pose in $Poses) {
        foreach ($shadow in $Shadows) {
            foreach ($route in $order) {
                $name = "n$Count-$route-$pose-$shadow-r$r"
                Say "== $name"
                $heavy = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(Unity|bee_backend|il2cpp|CutWorldSandbox|MetaXRSimulator)\.exe$' })
                if ($heavy.Count -gt 0) { Say "  STOPPED: running $($heavy.Name -join ', ')"; throw 'another Unity, Player or Simulator is running' }
                try {
                    & (Join-Path $PSScriptRoot 'Run-NpcSkinBench.ps1') -Name $name -Exe $Exe -RunRoot $RunRoot -Route $route -Count $Count -Pose $pose -Shadows $shadow `
                        -WarmupSeconds $WarmupSeconds -MeasureSeconds $MeasureSeconds -MeasureFrames $MeasureFrames -SettleFrames $SettleFrames `
                        -PoseToleranceMetres $PoseToleranceMetres -PoseToleranceDegrees $PoseToleranceDegrees -FixedView:$FixedView -PaletteJoinGap $PaletteJoinGap `
                        -Gather $Gather -WorkerFrom $WorkerFrom -Skin24Slice:$Skin24Slice -UpdateCount $UpdateCount `
                        -Extra $Extra -Invoke $Invoke -RepositoryRoot $RepositoryRoot 2>&1 | ForEach-Object { Say "  | $_" }
                } catch { $faults++; Say "  FAULT: $($_.Exception.Message)" }
                $runs += $name
            }
        }
    }
}
Say "matrix ended: $($runs.Count) launches, $faults faulted"
$python = Get-Command py -ErrorAction SilentlyContinue
if ($python) { & py -3 (Join-Path $PSScriptRoot 'Summarize-NpcSkinBench.py') $RunRoot 2>&1 | ForEach-Object { Say "  $_" } }
else { Say 'no py launcher: run Summarize-NpcSkinBench.py by hand' }
# The launches go on after a fault (the matrix is the record of every attempt), but the matrix itself ends as a failure
# when any faulted, so that a caller does not read an all-FAULT matrix as a success (TL review, 2026-10-31).
if ($faults -gt 0) { Say "FAILED: $faults of $($runs.Count) launches faulted (see the FAULT lines above)"; exit 1 }
exit 0
