#Requires -Version 5.1
<#
.SYNOPSIS
  DIAGNOSIS (SKIN-BENCH-2 section 4): one launch of the NPC skin benchmark on a PC under RenderDoc and the Meta XR Simulator,
  with -zantetsuNpcSkinBenchCaptureFrames frames captured after the measurement (the Player asks RenderDoc through Unity's
  ExternalGPUProfiler). Such a run is not a performance sample: its bench-result.json is kept apart from the matrix.
.DESCRIPTION
  renderdoccmd capture launches the Player with the Simulator chosen for that process alone (XR_RUNTIME_JSON), as the ordinary
  launcher does, but without the launcher's settings backup: no head replay is written, nothing of the Simulator's settings is
  changed. The capture files land in <RunRoot>\<Name>\capture\ (one .rdc a captured frame), the benchmark's results in
  <RunRoot>\<Name>\run. Analyze-RenderDocCapture.py (run inside qrenderdoc) then sums the GPU durations by marker.
.EXAMPLE
  .\Run-NpcSkinBench-Capture.ps1 -Name cap-skin24-stop-cascade4 -Exe <Player.exe> -RunRoot <folder> -Route skin24 -Pose stop -Shadows cascade4
#>
param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$RunRoot,
    [Parameter(Mandatory = $true)][ValidateSet('smr', 'skin24')][string]$Route,
    [ValidateRange(1, 512)][int]$Count = 64,
    [ValidateSet('stop', 'update')][string]$Pose = 'stop',
    [ValidateSet('off', 'cascade1', 'cascade4')][string]$Shadows = 'cascade4',
    [ValidateRange(1, 64)][int]$Families = 8,
    [double]$WarmupSeconds = 6,
    [int]$MeasureFrames = 360,
    [int]$SettleFrames = 30,
    [ValidateRange(1, 1000)][int]$CaptureFrames = 2,
    [int]$PaletteJoinGap = 0,
    [string]$RenderDocCmd = 'C:\Program Files\RenderDocForMetaQuest\renderdoccmd.exe',
    [string]$RuntimeJson = 'C:\Program Files\MetaXRSimulator\v207.0\meta_openxr_simulator.json',
    [int]$TimeoutSeconds = 400
)
$ErrorActionPreference = 'Stop'
foreach ($f in $Exe, $RenderDocCmd, $RuntimeJson) { if (-not (Test-Path -LiteralPath $f -PathType Leaf)) { throw "not found: $f" } }
if (-not (Test-Path -LiteralPath $RunRoot -PathType Container)) { New-Item -ItemType Directory -Path $RunRoot | Out-Null }
$run = Join-Path $RunRoot $Name
if (Test-Path -LiteralPath $run) { throw "exists: $run" }
$heavy = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -match '^(Unity|bee_backend|il2cpp|CutWorldSandbox|MetaXRSimulator|qrenderdoc)\.exe$' })
if ($heavy.Count -gt 0) { throw "running: $($heavy.Name -join ', ')" }
New-Item -ItemType Directory -Path "$run\capture" | Out-Null
$inv = [Globalization.CultureInfo]::InvariantCulture
$routeArgument = if ($Route -eq 'skin24') { '-zantetsuSkinDraw' } else { '-zantetsuSkinUnity' }
$a = @('-screen-fullscreen', '0', '-screen-width', '960', '-screen-height', '600', '-logFile', "$run\player.log",
    '-zantetsuScenario', 'npc-skin-bench', '-zantetsuNpcSkinBench', "$run\run",
    '-zantetsuNpcSkinBenchCount', "$Count", '-zantetsuNpcSkinBenchPose', $Pose, '-zantetsuNpcSkinBenchShadows', $Shadows, '-zantetsuNpcSkinBenchFamilies', "$Families",
    '-zantetsuNpcSkinBenchWarmupSeconds', $WarmupSeconds.ToString($inv), '-zantetsuNpcSkinBenchMeasureFrames', "$MeasureFrames", '-zantetsuNpcSkinBenchSettleFrames', "$SettleFrames",
    '-zantetsuNpcSkinBenchPaletteJoinGap', "$PaletteJoinGap", '-zantetsuNpcSkinBenchCaptureFrames', "$CaptureFrames", '-zantetsuNpcSkinBenchNoPicture', $routeArgument)
$record = [ordered]@{ tool = $PSCommandPath; recorded_at = (Get-Date).ToString('o'); exe = $Exe; exe_sha256 = (Get-FileHash -LiteralPath $Exe -Algorithm SHA256).Hash.ToLowerInvariant()
    renderdoc = $RenderDocCmd; runtime_json = $RuntimeJson; route = $Route; count = $Count; pose = $Pose; shadows = $Shadows; capture_frames = $CaptureFrames; player_arguments = $a; diagnosis = 'not a performance sample' }
Set-Content -LiteralPath "$run\capture-inputs.json" -Value ([pscustomobject]$record | ConvertTo-Json -Depth 4) -Encoding UTF8
$env:XR_RUNTIME_JSON = $RuntimeJson
$quoted = ($a | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $RenderDocCmd
$psi.Arguments = 'capture --wait-for-exit --working-dir "' + (Split-Path $Exe) + '" --capture-file "' + "$run\capture\frame" + '" "' + $Exe + '" ' + $quoted
$psi.UseShellExecute = $false
$psi.EnvironmentVariables['XR_RUNTIME_JSON'] = $RuntimeJson
$psi.RedirectStandardOutput = $true; $psi.RedirectStandardError = $true
$started = Get-Date
$p = [System.Diagnostics.Process]::Start($psi)
$out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
if (-not $p.WaitForExit($TimeoutSeconds * 1000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; Get-Process CutWorldSandbox -ErrorAction SilentlyContinue | Stop-Process -Force; "TIMEOUT after $TimeoutSeconds s" }
"renderdoccmd exit $($p.ExitCode) after $([int]((Get-Date) - $started).TotalSeconds) s"
Set-Content -LiteralPath "$run\renderdoccmd.txt" -Value (@("exit $($p.ExitCode)", '--- stdout', $out.Result, '--- stderr', $err.Result) -join "`r`n") -Encoding UTF8
Get-ChildItem -LiteralPath "$run\capture" -Filter *.rdc | ForEach-Object { "capture $($_.Name) $($_.Length) B" }
if (Test-Path -LiteralPath "$run\run\bench-result.txt") { Get-Content -LiteralPath "$run\run\bench-result.txt" -Encoding UTF8 }
