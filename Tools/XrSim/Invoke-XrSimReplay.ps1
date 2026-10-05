#Requires -Version 5.1
<#
.SYNOPSIS
    Replays a Meta XR Simulator VRS fixture into the harness Player through the documented automated replay only.

.DESCRIPTION
    For each entry of -Sequence ("VP3:1", "VP3C:1", ...) one harness Player run: persistent_data.json gets the
    documented "session_capture" block (exec_state replay, record_path, delay_start_ms, quit_when_complete),
    the Player captures the fixture's pose targets in order while the replay is in progress, and
    persistent_data.json is written back byte for byte after the run (also on failure). For each entry of
    -NoReplay ("VP3", "VP3C") one control run without a recording. No gRPC is used.

    Only fixtures whose fixture.json status is "verified" are accepted unless -AllowCandidate is given
    (Test-XrSimFixture.ps1 uses it); the VRS SHA-256 must match fixture.json.

    -HideSimulatorWindow hides the Simulator window while the runs execute (it cannot be prevented from
    launching; closing it makes the runtime relaunch it) and shows it again at the end.

    Exit codes: 0 all runs finished (their validity is judged by Test-XrSimFixture.ps1 / the caller), 2 infrastructure error.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$FixtureDirectory,
    [string]$WorkRoot = (Join-Path $env:LOCALAPPDATA 'Zantetsu\XrSim'),
    [string]$PlayerDirectory = '',
    [string]$OutputDirectory = '',
    [string[]]$Sequence = @('VP3:1', 'VP3C:1'),
    [string[]]$NoReplay = @(),
    [int]$DelayStartMs = 20000,
    [int]$RunTimeoutSeconds = 300,
    [switch]$AllowCandidate,
    [switch]$HideSimulatorWindow
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'XrSim.psm1') -Force

$repo = Get-XrSimRepositoryRoot
$constants = Get-XrSimConstants
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
if (-not $PlayerDirectory) { $PlayerDirectory = Join-Path $WorkRoot 'player' }
$playerExe = Join-Path $PlayerDirectory 'zantetsuken-vr.exe'
$fixturePath = Join-Path $FixtureDirectory 'fixture.json'
$targetsPath = Join-Path $FixtureDirectory 'targets.json'
foreach ($path in $playerExe, $fixturePath, $targetsPath) { if (-not (Test-Path -LiteralPath $path)) { Write-Error "missing: $path"; exit 2 } }
$fixture = Get-Content -LiteralPath $fixturePath -Raw | ConvertFrom-Json
$vrs = [IO.Path]::GetFullPath((Join-Path $FixtureDirectory $fixture.recording.file))
if ($fixture.status -ne 'verified' -and -not $AllowCandidate) { Write-Error "fixture status is '$($fixture.status)', not verified"; exit 2 }
if (-not (Test-Path -LiteralPath $vrs) -or (Get-FileHash -LiteralPath $vrs -Algorithm SHA256).Hash -ne $fixture.recording.sha256) { Write-Error "recording missing or SHA-256 differs: $vrs"; exit 2 }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $WorkRoot ('replay\{0}-{1:yyyyMMdd-HHmmss}' -f $fixture.id, (Get-Date)) }
if ($OutputDirectory.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) { Write-Error "OutputDirectory must be outside the repository"; exit 2 }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

try {
    Assert-XrSimNoConflictingProcess
    Write-XrSimLog (Assert-XrSimRegistryNotSimulator)
    $registry = Get-XrSimOpenXrRegistryState
    [void](Get-XrSimSimulatorVersion)
    $gitBefore = Get-XrSimGitState -RepositoryRoot $repo
    $windowWasRunning = [bool](Get-XrSimSimulatorWindow)
    $settings = Save-XrSimSimulatorSettings -BackupDirectory (Join-Path $OutputDirectory 'simulator-settings-backup')
    $originalPersistent = if (Test-Path -LiteralPath $constants.PersistentDataPath) { [IO.File]::ReadAllBytes($constants.PersistentDataPath) } else { [byte[]]@() }
}
catch {
    Write-Error "preflight failed: $_"
    exit 2
}

$runs = @()
$hiddenHandle = [IntPtr]::Zero
$exitCode = 0
function Hide-WindowIfRequested {
    if ($HideSimulatorWindow -and $script:hiddenHandle -eq [IntPtr]::Zero) {
        $deadline = (Get-Date).AddSeconds(15)
        while ($script:hiddenHandle -eq [IntPtr]::Zero -and (Get-Date) -lt $deadline) {
            $script:hiddenHandle = Set-XrSimSimulatorWindowHidden -Hidden $true
            if ($script:hiddenHandle -eq [IntPtr]::Zero) { Start-Sleep -Milliseconds 250 }
        }
    }
}

try {
    $plan = @($Sequence | ForEach-Object { [pscustomobject]@{ Mode = 'replay'; Path = $_.Split(':')[0]; Run = $_.Split(':')[1] } }) +
            @($NoReplay | ForEach-Object { [pscustomobject]@{ Mode = 'noreplay'; Path = $_; Run = 'control' } })
    foreach ($item in $plan) {
        if ($item.Path -notin @('VP3', 'VP3C')) { throw "unknown path $($item.Path)" }
        $runDir = Join-Path $OutputDirectory ('{0}-{1}-{2}' -f $item.Mode, $item.Path, $item.Run)
        New-Item -ItemType Directory -Path $runDir | Out-Null
        $started = Get-Date
        $player = $null
        $state = 'not started'
        try {
            # quit_when_complete must stay false: with true the Simulator quits the Player as soon as the
            # playback ends, before the harness writes result.json, so the run produces captures but no result
            # (and the Player then hung until RunTimeoutSeconds). The harness finishes and exits by itself.
            if ($item.Mode -eq 'replay') { Set-XrSimReplayAutomation -OriginalBytes $originalPersistent -RecordPath $vrs -DelayStartMs $DelayStartMs -QuitWhenComplete $false }
            $arguments = @('-xrsimMode', $item.Mode, '-xrsimView', $fixture.view, '-xrsimPath', $item.Path, '-xrsimRun', $item.Run)
            if ($item.Mode -eq 'replay') { $arguments += @('-xrsimTargets', $targetsPath) }
            $player = Start-XrSimHarnessPlayer -PlayerExe $playerExe -RunDirectory $runDir -HarnessArguments $arguments
            Hide-WindowIfRequested
            if (-not $player.WaitForExit($RunTimeoutSeconds * 1000)) { $state = "timeout; $(Stop-XrSimPlayer -Process $player)" } else { $state = "exited $($player.ExitCode)" }
        }
        catch {
            $state = "error: $_"
            if ($player) { [void](Stop-XrSimPlayer -Process $player) }
        }
        finally {
            if ($originalPersistent.Length -gt 0) { [IO.File]::WriteAllBytes($constants.PersistentDataPath, $originalPersistent) }
        }
        $persistentRestored = ($originalPersistent.Length -eq 0) -or (Test-XrSimBytesEqual -Expected $originalPersistent -Actual ([IO.File]::ReadAllBytes($constants.PersistentDataPath)))
        if (-not $persistentRestored) { throw 'persistent_data.json could not be written back' }
        $resultPath = Join-Path $runDir 'result.json'
        $summary = if (Test-Path -LiteralPath $resultPath) { Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json } else { $null }
        $valid = if ($summary) { @($summary.captures | Where-Object { $_.valid }).Count } else { 0 }
        $runs += [pscustomobject]@{
            mode = $item.Mode; path = $item.Path; run = $item.Run; directory = $runDir; started = $started.ToString('o'); ended = (Get-Date).ToString('o')
            player = $state; persistent_data_restored = $persistentRestored; harness_failed = if ($summary) { $summary.failed } else { $true }
            run_reasons = if ($summary) { @($summary.run_reasons) } else { @('no result.json') }; captures = if ($summary) { @($summary.captures).Count } else { 0 }; valid_captures = $valid
        }
        Write-XrSimLog ("{0} {1} {2}: player {3}, captures {4} (valid {5}), run reasons: {6}" -f $item.Mode, $item.Path, $item.Run, $state, $runs[-1].captures, $valid, ($runs[-1].run_reasons -join '; '))
    }
}
catch {
    Write-XrSimLog "replay batch error: $_"
    $exitCode = 2
}
finally {
    if ($originalPersistent.Length -gt 0) { [IO.File]::WriteAllBytes($constants.PersistentDataPath, $originalPersistent) }
    if ($hiddenHandle -ne [IntPtr]::Zero) { [void](Set-XrSimSimulatorWindowHidden -Hidden $false -Handle $hiddenHandle) }
    $restore = @()
    # Close the Simulator window first: it writes its settings while running and on exit.
    if (-not $windowWasRunning) { $restore += @(Close-XrSimSimulatorWindows) }
    try { $restore += @(Restore-XrSimSimulatorSettings -Manifest $settings) } catch { $restore += "RESTORE FAILED: $_"; $exitCode = 2 }
    $checks = @()
    try { $checks += Assert-XrSimOpenXrRegistryUnchanged -Expected $registry } catch { $checks += "REGISTRY: $_"; $exitCode = 2 }
    try { $checks += Assert-XrSimGitStateUnchanged -RepositoryRoot $repo -Before $gitBefore } catch { $checks += "GIT: $_"; $exitCode = 2 }
    [ordered]@{ fixture = $fixture.id; output = $OutputDirectory; delay_start_ms = $DelayStartMs; runs = $runs; settings_restore = $restore; state_checks = $checks } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'replay-summary.json') -Encoding UTF8
    $restore + $checks | ForEach-Object { Write-XrSimLog $_ }
}
exit $exitCode
