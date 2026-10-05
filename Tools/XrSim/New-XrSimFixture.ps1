#Requires -Version 5.1
<#
.SYNOPSIS
    Generates a candidate Meta XR Simulator VRS fixture: a recording of three distinct XR head poses.

.DESCRIPTION
    Recording automation uses the Meta XR Simulator v205 NON-PUBLIC SessionCapture gRPC service through
    Adapters/xrsim_v205_session_capture.py (fails on another version, no connection or a missing state
    transition). Meta documents only UI recording and automated replay; this generator is a v205-only
    experiment for test fixtures, not a runtime dependency of image regression tests, which replay verified
    fixtures through the documented session_capture automation (Invoke-XrSimReplay.ps1).

    One attempt:
      1. launches the harness Player in hold mode (XR_RUNTIME_JSON for that process only) and waits for READY:
         XR running, Single Pass Instanced on Direct3D 11, Simulator 205.x, camera following the XR head;
      2. brings the Simulator window to the foreground and clicks into it (input target);
      3. nudges the head forward and back and checks that the head moved and the camera followed;
      4. starts recording (adapter), drives three poses with the Simulator keys while checking that the
         Simulator window stays foreground before every key, stops recording (adapter);
      5. stops the Player and runs the recording QC (length, three distinct stationary poses, travel,
         camera follow in every recorded frame, eye matrices).
    A failed attempt is moved to <WorkRoot>\rejected\ with its reasons and never becomes a candidate. Attempts
    are retried up to MaxAttempts; every attempt and reason is written to generation-summary.json.

    The Simulator settings under %APPDATA%\MetaXR are saved first and written back byte for byte at the end,
    also on failure. The HKLM OpenXR runtime is read and compared only. The Git working tree must be unchanged.

    Exit codes: 0 a candidate passed QC, 1 every attempt was rejected, 2 infrastructure error.
#>
[CmdletBinding()]
param(
    [ValidateSet('along', 'across')][string]$View = 'along',
    [string]$WorkRoot = (Join-Path $env:LOCALAPPDATA 'Zantetsu\XrSim'),
    [string]$PlayerDirectory = '',
    [ValidateRange(1, 10)][int]$MaxAttempts = 3,
    # 2.5 s per pose puts the recording at about 10.0 s, near the middle of the 8..12 s QC window, for the
    # current plan (no pitch key, short move after the yaw). Measured alternatives: 2.0 s gave 8.3 s, close to
    # the lower bound; 3.0 s gave 12.4 s, past the upper bound, because every extra key costs roughly 1.15 s of
    # window re-resolution and foreground checks on top of its hold time.
    [double]$HoldSeconds = 2.5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'XrSim.psm1') -Force

$repo = Get-XrSimRepositoryRoot
$constants = Get-XrSimConstants
$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
if ($WorkRoot.StartsWith($repo, [StringComparison]::OrdinalIgnoreCase)) { Write-Error "WorkRoot must be outside the repository: $WorkRoot"; exit 2 }
if (-not $PlayerDirectory) { $PlayerDirectory = Join-Path $WorkRoot 'player' }
$playerExe = Join-Path $PlayerDirectory 'zantetsuken-vr.exe'
if (-not (Test-Path -LiteralPath $playerExe)) { Write-Error "harness Player not found: $playerExe (run Build-XrSimHarnessPlayer.ps1)"; exit 2 }

# Three poses: P1 is the pose after the nudge, P2 forward and turned left, P3 right and up.
# Movement and yaw only, no pitch: the VRS replay reproduces a pose combining pitch and yaw 1.108 deg away
# from the recorded rotation (deterministic to the bit across runs, cause not identified), which the 0.5 deg
# matching tolerance rejects. The tolerance stays at 0.5 deg; the pitched recording is kept as diagnostic
# material and the rotation difference is tracked as a separate item. P3 keeps the yaw applied at P2.
$motionPlan = @(
    @{ Pose = 'P1'; Keys = @() },
    # The short W after the rotation is deliberate. The replay reproduces a rotation as a staircase of about
    # 4.443 deg steps and stops one step short of the recorded settled value for as long as the head stays put:
    # measured, the recorded 33.329 deg came back as 31.1 deg for the whole 3.49 s hold and only reached
    # 33.329 deg once the next motion segment began. Moving briefly after the yaw puts that segment boundary
    # before the hold, so the pose that gets captured carries the settled rotation.
    @{ Pose = 'P2'; Keys = @(@('W', 500), @('Left', 400), @('W', 120)) },
    @{ Pose = 'P3'; Keys = @(@('D', 500), @('R', 400)) }
)

$batchId = '{0}-{1:yyyyMMdd-HHmmss}' -f $View, (Get-Date)
$batchDir = Join-Path $WorkRoot "generation\$batchId"
$rejectedRoot = Join-Path $WorkRoot 'rejected'
New-Item -ItemType Directory -Force -Path $batchDir, $rejectedRoot | Out-Null
$attempts = @()
$candidate = $null
$exitCode = 2

try {
    Assert-XrSimNoConflictingProcess
    Write-XrSimLog (Assert-XrSimRegistryNotSimulator)
    $registry = Get-XrSimOpenXrRegistryState
    $simulatorExeVersion = Get-XrSimSimulatorVersion
    $gitBefore = Get-XrSimGitState -RepositoryRoot $repo
    $python = Get-XrSimPython -WorkRoot $WorkRoot
    $adapter = Join-Path $PSScriptRoot 'Adapters\xrsim_v205_session_capture.py'
    $tool = Join-Path $PSScriptRoot 'Python\xrsim_fixture.py'
    $manifestPath = Join-Path $PlayerDirectory 'player-manifest.json'
    $playerManifest = if (Test-Path -LiteralPath $manifestPath) { Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json } else { $null }
    $windowWasRunning = [bool](Get-XrSimSimulatorWindow)
    $settings = Save-XrSimSimulatorSettings -BackupDirectory (Join-Path $batchDir 'simulator-settings-backup')
    Write-XrSimLog "batch $batchId; Simulator $simulatorExeVersion; settings saved ($(@($settings.Files).Count) files); max attempts $MaxAttempts"
}
catch {
    Write-Error "preflight failed: $_"
    exit 2
}

try {
    for ($attempt = 1; $attempt -le $MaxAttempts -and -not $candidate; $attempt++) {
        $runDir = Join-Path $batchDir "attempt-$attempt"
        New-Item -ItemType Directory -Path $runDir | Out-Null
        $vrs = Join-Path $runDir 'recording.vrs'
        $failures = New-Object System.Collections.Generic.List[string]
        $foregroundChecks = New-Object System.Collections.Generic.List[string]
        $adapterReports = [ordered]@{}
        $precheck = $null
        $player = $null
        Write-XrSimLog "attempt $attempt/${MaxAttempts}: $runDir"
        try {
            # -xrsimIgnoreFocus 1: the recording keys need the Simulator window to hold the OS focus, which
            # otherwise freezes the Player's TrackedPoseDriver and fails every camera-follow check. Test-only
            # and generation-only; the replay runs keep the unchanged conditions.
            $player = Start-XrSimHarnessPlayer -PlayerExe $playerExe -RunDirectory $runDir -HarnessArguments @('-xrsimMode', 'hold', '-xrsimView', $View, '-xrsimPath', 'VP3', '-xrsimRun', "attempt-$attempt", '-xrsimIgnoreFocus', '1')
            if (-not (Wait-XrSimFile -Path (Join-Path $runDir 'ready.json') -TimeoutSeconds 240 -Process $player)) { throw 'harness did not reach READY (see player.log / events.jsonl)' }
            $ready = Get-Content -LiteralPath (Join-Path $runDir 'ready.json') -Raw | ConvertFrom-Json
            $environment = Get-Content -LiteralPath (Join-Path $runDir 'environment.json') -Raw | ConvertFrom-Json
            Write-XrSimLog "READY: stereo $($environment.stereo_mode), api $($environment.graphics_api), Simulator $($environment.simulator_version) / $($environment.simulator_device_profile), camera following $($ready.camera_following) ($($ready.follow_error_m) m)"
            if (-not $ready.camera_following) { throw "camera not following the XR head at READY ($($ready.follow_error_m) m / $($ready.follow_error_deg) deg)" }

            $deadline = (Get-Date).AddSeconds(30)
            $window = $null
            while (-not $window -and (Get-Date) -lt $deadline) { $window = Get-XrSimSimulatorWindow; if (-not $window) { Start-Sleep -Milliseconds 500 } }
            if (-not $window) { throw 'Meta XR Simulator window not found' }
            if (-not (Set-XrSimSimulatorForeground -Window $window)) { throw "Simulator window could not be brought to the foreground (foreground: $(Get-XrSimForegroundOwner))" }
            $foregroundChecks.Add('initial: foreground')

            $check = & $python $adapter check --pid $player.Id --simulator-dir $constants.SimulatorDirectory --log-dir $constants.SimulatorLogDirectory
            $adapterReports.check = $check | ConvertFrom-Json
            if ($LASTEXITCODE -ne 0) { throw "v205 adapter check failed: $($adapterReports.check.error)" }
            # Launching the adapter can take the foreground, and the runtime can replace its frontend window: re-resolve it.
            $window = Get-XrSimSimulatorInputTarget
            if (-not $window) { throw "Simulator window is not the input target after the adapter check (foreground: $(Get-XrSimForegroundOwner))" }
            $foregroundChecks.Add('after adapter check: input target confirmed')

            $lastFrame = [int]((Get-Content -LiteralPath (Join-Path $runDir 'frames.csv') -Tail 1).Split(',')[0])
            foreach ($nudge in @(@('W', 300), @('S', 300))) {
                $window = Get-XrSimSimulatorInputTarget
                if (-not $window) { throw "Simulator window is not the input target before nudge key $($nudge[0]) (foreground: $(Get-XrSimForegroundOwner))" }
                Invoke-XrSimKey -Key $nudge[0] -Milliseconds $nudge[1]
                Start-Sleep -Milliseconds 900
            }
            $precheckJson = Invoke-XrSimPython -Python $python -Arguments @($tool, 'precheck', '--frames', (Join-Path $runDir 'frames.csv'), '--from-frame', $lastFrame)
            $precheck = ($precheckJson -join "`n") | ConvertFrom-Json
            if (-not $precheck.passed) { throw "precheck failed: $($precheck.reasons -join '; ')" }
            Write-XrSimLog "precheck: moved $([math]::Round($precheck.max_move_m, 3)) m, returned to $([math]::Round($precheck.final_offset_m, 3)) m, follow $($precheck.max_follow_error_m) m"

            $start = & $python $adapter record --pid $player.Id --simulator-dir $constants.SimulatorDirectory --log-dir $constants.SimulatorLogDirectory --path $vrs
            $adapterReports.record = $start | ConvertFrom-Json
            if ($LASTEXITCODE -ne 0) { throw "v205 adapter record failed: $($adapterReports.record.error)" }
            $window = Get-XrSimSimulatorInputTarget
            if (-not $window) { throw "Simulator window is not the input target after the recording started (foreground: $(Get-XrSimForegroundOwner))" }
            $foregroundChecks.Add('after record start: input target confirmed')
            foreach ($step in $motionPlan) {
                foreach ($key in $step.Keys) {
                    $window = Get-XrSimSimulatorInputTarget
                    if (-not $window) { throw "Simulator window is not the input target before $($step.Pose) key $($key[0]) (foreground: $(Get-XrSimForegroundOwner))" }
                    Invoke-XrSimKey -Key $key[0] -Milliseconds $key[1]
                }
                $foregroundChecks.Add("$($step.Pose): input target $([bool](Get-XrSimSimulatorInputTarget))")
                Start-Sleep -Milliseconds ([int]($HoldSeconds * 1000))
            }
            $stop = & $python $adapter stop --pid $player.Id --simulator-dir $constants.SimulatorDirectory --log-dir $constants.SimulatorLogDirectory
            $adapterReports.stop = $stop | ConvertFrom-Json
            if ($LASTEXITCODE -ne 0) { throw "v205 adapter stop failed: $($adapterReports.stop.error)" }
        }
        catch {
            $failures.Add("$_")
            Write-XrSimLog "attempt $attempt failure: $_"
            if ($player -and -not $player.HasExited -and -not $adapterReports.Contains('stop') -and $adapterReports.Contains('record')) {
                $stop = & $python $adapter stop --pid $player.Id --simulator-dir $constants.SimulatorDirectory --log-dir $constants.SimulatorLogDirectory
                $adapterReports.stop_after_failure = $stop | ConvertFrom-Json
            }
        }
        finally {
            if ($player) {
                Set-Content -LiteralPath (Join-Path $runDir 'stop') -Value 'stop'
                if (-not $player.WaitForExit(60000)) { Write-XrSimLog "player: $(Stop-XrSimPlayer -Process $player)" }
            }
        }

        $context = [ordered]@{
            id = "$batchId-a$attempt"; view = $View; generated_at = (Get-Date).ToString('o'); attempt = $attempt; max_attempts = $MaxAttempts
            simulator_exe_version = $simulatorExeVersion; player_manifest = $playerManifest; motion_plan = $motionPlan
            adapter = $adapterReports; foreground_checks = $foregroundChecks; precheck = $precheck; failures = $failures
        }
        $context | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $runDir 'generation-context.json') -Encoding UTF8
        $qcJson = Invoke-XrSimPython -Python $python -Arguments @($tool, 'qc-recording', '--run-dir', $runDir, '--vrs', $vrs, '--context', (Join-Path $runDir 'generation-context.json'), '--out-dir', $runDir)
        $qcExit = $LASTEXITCODE
        $qc = ($qcJson -join "`n") | ConvertFrom-Json
        $result = [ordered]@{ attempt = $attempt; directory = $runDir; passed = ($qcExit -eq 0 -and $failures.Count -eq 0); reasons = @($qc.reasons) }
        if ($result.passed) {
            $candidate = $runDir
            Write-XrSimLog "attempt $attempt passed QC: $($qc.record_seconds) s, plateaus $(@($qc.plateaus).Count), follow max $($qc.max_follow_error_m) m"
        }
        else {
            $rejected = Join-Path $rejectedRoot "$batchId-attempt-$attempt"
            Move-Item -LiteralPath $runDir -Destination $rejected
            $result.directory = $rejected
            $result.reasons | Set-Content -LiteralPath (Join-Path $rejected 'REJECTED.txt') -Encoding UTF8
            Write-XrSimLog "attempt $attempt rejected: $($result.reasons -join ' | ')"
        }
        $attempts += [pscustomobject]$result
    }
    $exitCode = if ($candidate) { 0 } else { 1 }
}
finally {
    $restore = @()
    # Close the Simulator window first: it writes its settings (e.g. device_data\tools_consent.txt) while running and
    # on exit, so restoring before it has closed can leave its later writes in place.
    if (-not $windowWasRunning) { $restore += @(Close-XrSimSimulatorWindows) }
    try { $restore += @(Restore-XrSimSimulatorSettings -Manifest $settings) } catch { $restore += "RESTORE FAILED: $_"; $exitCode = 2 }
    $checks = @()
    try { $checks += Assert-XrSimOpenXrRegistryUnchanged -Expected $registry } catch { $checks += "REGISTRY: $_"; $exitCode = 2 }
    try { $checks += Assert-XrSimGitStateUnchanged -RepositoryRoot $repo -Before $gitBefore } catch { $checks += "GIT: $_"; $exitCode = 2 }
    $summary = [ordered]@{
        batch = $batchId; view = $View; finished_at = (Get-Date).ToString('o'); candidate = $candidate
        attempts_made = @($attempts).Count; attempts_passed = @($attempts | Where-Object { $_.passed }).Count; attempts = $attempts
        settings_restore = $restore; state_checks = $checks
    }
    $summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $batchDir 'generation-summary.json') -Encoding UTF8
    $restore + $checks | ForEach-Object { Write-XrSimLog $_ }
    Write-XrSimLog "generation summary: $(Join-Path $batchDir 'generation-summary.json')"
}
exit $exitCode
