#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the Windows Standalone NVENC sentinel tests into a fixed player path
    outside the repository, then validates the results XML (fail closed).

.DESCRIPTION
    The Unity Test Framework builds a throw-away test player for every
    Standalone run. Left to itself it writes that player into a new temporary
    directory each time, so a Windows Firewall decision a human makes for the
    player never matches the next run's executable. This runner pins the player
    to one absolute path outside the repository, which makes a single one-time
    firewall decision hold for every later run.

    The script itself never adds, removes or inspects firewall rules and never
    changes notification settings: granting (or denying) access stays a human
    decision made once, and Private/Domain-only is the recommended answer.

    The Unity editor is an explicit required argument. No Unity Hub scan and no
    registry lookup is performed, and no account-specific absolute path is
    stored in this file: the default player location is derived from
    LOCALAPPDATA at run time.

    Exit codes:
      0 = the sentinel assembly ran and every test passed
      1 = tests ran but failed / skipped / inconclusive are present
      2 = infrastructure error (editor missing or version mismatch, refused
          player path, process conflict, launch failure, timeout, compile,
          license or player-build failure, missing/corrupt/stale XML, or zero
          tests)

.PARAMETER UnityEditorPath
    Full path to the Unity.exe that must run the tests. Required; its version
    is checked against ProjectSettings/ProjectVersion.txt.

.PARAMETER PlayerDirectory
    Directory the test player is built into. Optional; defaults to
    "%LOCALAPPDATA%\Zantetsu\TestPlayers\WindowsStandalone64". A directory
    inside the repository is refused before any side effect. The Test Framework
    treats -buildPlayerPath as a directory and names the executable itself, so
    the fixed executable this produces is
    "<PlayerDirectory>\PlayerWithTests\PlayerWithTests.exe" - that is the path a
    firewall rule has to name, and it is printed by every run.

.PARAMETER TimeoutSeconds
    Maximum seconds to wait for the Unity process. Default is 1800.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass `
      -File .\Tools\Run-NvencStandaloneTests.ps1 `
      -UnityEditorPath 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$UnityEditorPath,

    [string]$PlayerDirectory,

    [int]$TimeoutSeconds = 1800
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

$RequiredEditorVersion = '6000.3.22f1'
$TestAssemblyName = 'Zantetsu.Observability.StandaloneTests'

# ---------------------------------------------------------------------------
# Infrastructure failure helper (always exits with code 2)
# ---------------------------------------------------------------------------
function Fail-Infrastructure {
    param([string]$Message)
    [Console]::Error.WriteLine("ERROR: $Message")
    exit 2
}

# ---------------------------------------------------------------------------
# Repository root resolution (independent of the current directory, worktree
# aware). Failures are infrastructure errors.
# ---------------------------------------------------------------------------
function Get-RepositoryRoot {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Fail-Infrastructure 'Git is required but was not found on PATH.'
    }
    $root = (& git -C $PSScriptRoot rev-parse --show-toplevel 2>$null | Select-Object -First 1)
    if ($LASTEXITCODE -eq 0 -and $root) {
        return [System.IO.Path]::GetFullPath($root.ToString().Trim())
    }
    $current = $PSScriptRoot
    while ($current) {
        if (Test-Path -LiteralPath (Join-Path $current '.git')) {
            return $current
        }
        $parent = Split-Path -Parent $current
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
    Fail-Infrastructure 'Could not locate a Git repository root.'
}

# ---------------------------------------------------------------------------
# Editor verification. The path is given, never discovered; only its existence
# and its version agreement with the project are checked.
# ---------------------------------------------------------------------------
function Assert-Editor {
    param([string]$RepoRoot, [string]$EditorPath)
    if (-not (Test-Path -LiteralPath $EditorPath -PathType Leaf)) {
        Fail-Infrastructure "Unity editor not found at: $EditorPath"
    }
    $versionFile = Join-Path $RepoRoot 'ProjectSettings\ProjectVersion.txt'
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
        Fail-Infrastructure "ProjectVersion.txt not found: $versionFile"
    }
    $version = $null
    $fullVersion = $null
    foreach ($line in (Get-Content -LiteralPath $versionFile)) {
        if ($line -match '^m_EditorVersionWithRevision:\s*(.+)$') {
            $fullVersion = $Matches[1].Trim()
        } elseif ($line -match '^m_EditorVersion:\s*(.+)$') {
            $version = $Matches[1].Trim()
        }
    }
    if (-not $version) {
        Fail-Infrastructure 'Could not read m_EditorVersion from ProjectVersion.txt.'
    }
    if ($version -ne $RequiredEditorVersion) {
        Fail-Infrastructure "Editor version mismatch: ProjectVersion.txt=$version, required=$RequiredEditorVersion."
    }
    return [pscustomobject]@{
        Version     = $version
        FullVersion = $fullVersion
    }
}

# ---------------------------------------------------------------------------
# Player location resolution. Returns the build directory and the executable
# the Test Framework will place inside it, refusing anything inside the
# repository before a directory is created or Unity is started. The default is
# derived from LOCALAPPDATA at run time so that no account-specific absolute
# path is stored in this file.
# ---------------------------------------------------------------------------
function Resolve-PlayerLocation {
    param([string]$RepoRoot, [string]$Requested)

    $candidate = $Requested
    if (-not $candidate) {
        $localAppData = $env:LOCALAPPDATA
        if (-not $localAppData) {
            Fail-Infrastructure 'LOCALAPPDATA is not set; pass -PlayerDirectory explicitly.'
        }
        $candidate = Join-Path $localAppData 'Zantetsu\TestPlayers\WindowsStandalone64'
    }

    if (-not [System.IO.Path]::IsPathRooted($candidate)) {
        Fail-Infrastructure "The player directory must be absolute (got: $candidate)."
    }

    $full = $null
    try {
        $full = [System.IO.Path]::GetFullPath($candidate).TrimEnd('\')
    } catch {
        Fail-Infrastructure "The player directory could not be resolved ($candidate): $($_.Exception.Message)"
    }

    if ([System.IO.Path]::GetExtension($full)) {
        Fail-Infrastructure "The player directory must be a directory, not a file (got: $full)."
    }

    # Refuse the repository itself and anything under it. Compared on the
    # normalised full paths, so "..", a trailing separator or differently cased
    # segments cannot slip a player build into the working tree.
    $rootNorm = [System.IO.Path]::GetFullPath($RepoRoot).TrimEnd('\') + '\'
    if (($full + '\').StartsWith($rootNorm, [System.StringComparison]::OrdinalIgnoreCase)) {
        Fail-Infrastructure "The test player must not be built inside the repository (directory: $full, repository: $RepoRoot)."
    }

    # The Test Framework names the executable; this runner only fixes where it
    # goes. The resulting path is verified after the run.
    return [pscustomobject]@{
        Directory  = $full
        Executable = Join-Path $full 'PlayerWithTests\PlayerWithTests.exe'
    }
}

# ---------------------------------------------------------------------------
# Pre-detect Unity process conflicts for THIS project. Unity Hub and
# Unity.Licensing.Client are never treated as conflicts. If process
# information cannot be obtained, fail closed.
# ---------------------------------------------------------------------------
function Get-ProjectUnityConflicts {
    param([string]$RepoRoot)
    try {
        $procs = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe' OR Name='AssetImportWorker.exe'" -ErrorAction Stop)
    } catch {
        Fail-Infrastructure "Could not query running processes (fail closed): $($_.Exception.Message)"
    }
    $rootNorm = $RepoRoot.Replace('\', '/').TrimEnd('/').ToLowerInvariant()
    $conflicts = @()
    foreach ($p in $procs) {
        $cmd = [string]$p.CommandLine
        $cmdNorm = $cmd.Replace('\', '/').ToLowerInvariant()
        if (-not $cmdNorm) {
            # Cannot prove it is a different project -> fail safe.
            $conflicts += [pscustomobject]@{ ProcessId = $p.ProcessId; Name = $p.Name; Reason = 'command line unavailable' }
        } elseif ($cmdNorm.Contains($rootNorm)) {
            $conflicts += [pscustomobject]@{ ProcessId = $p.ProcessId; Name = $p.Name; Reason = "opening this project ($RepoRoot)" }
        }
    }
    return @($conflicts)
}

# ---------------------------------------------------------------------------
# Run ID: yyyyMMdd-HHmmss-<short random>
# ---------------------------------------------------------------------------
function New-RunId {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $random = (Get-Random -Minimum 0 -Maximum 0xFFFFFF).ToString('x6')
    return "$timestamp-$random"
}

# ---------------------------------------------------------------------------
# Results XML validation (missing/corrupt XML or zero tests = infrastructure)
# ---------------------------------------------------------------------------
function Read-TestSummary {
    param([string]$XmlPath)
    if (-not (Test-Path -LiteralPath $XmlPath -PathType Leaf)) {
        Fail-Infrastructure "Results XML not found: $XmlPath"
    }
    try {
        [xml]$doc = Get-Content -LiteralPath $XmlPath -Raw
        $run = $doc.'test-run'
        if ($null -eq $run) {
            throw '<test-run> element is missing.'
        }
        return [pscustomobject]@{
            result       = [string]$run.result
            total        = [int]$run.total
            passed       = [int]$run.passed
            failed       = [int]$run.failed
            inconclusive = [int]$run.inconclusive
            skipped      = [int]$run.skipped
        }
    } catch {
        Fail-Infrastructure "Failed to read/parse results XML ($XmlPath): $($_.Exception.Message)"
    }
}

# ---------------------------------------------------------------------------
# Unity log inspection (case-insensitive). Any match = fail (exit 2). Ordinary
# test failures are not listed here: they are classified from the results XML
# as exit code 1.
# ---------------------------------------------------------------------------
function Assert-UnityLogClean {
    param([string]$LogPath)
    if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        Fail-Infrastructure "Unity log not found: $LogPath"
    }
    $infraPatterns = @(
        'error CS',
        'Compilation failed',
        'Scripts have compiler errors',
        'Licensing initialization failed',
        'Failed to build player'
    )
    $infraMatches = @(Select-String -Path $LogPath -Pattern $infraPatterns -SimpleMatch)
    if ($infraMatches.Count -gt 0) {
        [Console]::Error.WriteLine('ERROR: Unity log contains infrastructure failure indicators:')
        foreach ($m in $infraMatches) {
            [Console]::Error.WriteLine('  line {0} [{1}]: {2}' -f $m.LineNumber, $m.Pattern, $m.Line.Trim())
        }
        [Console]::Error.WriteLine("Log file: $LogPath")
        exit 2
    }
}

# ===========================================================================
# Main
# ===========================================================================
$repoRoot = Get-RepositoryRoot

$editor = Assert-Editor $repoRoot $UnityEditorPath
$unityVersion = if ($editor.FullVersion) { $editor.FullVersion } else { $editor.Version }

# Decided before anything is created or launched.
$player = Resolve-PlayerLocation $repoRoot $PlayerDirectory

if ($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 3600) {
    Fail-Infrastructure "TimeoutSeconds must be in the range 1..3600 (got $TimeoutSeconds)."
}

$conflicts = @(Get-ProjectUnityConflicts $repoRoot)
if ($conflicts.Count -gt 0) {
    $desc = ($conflicts | ForEach-Object { "PID $($_.ProcessId) ($($_.Name)): $($_.Reason)" }) -join '; '
    Fail-Infrastructure "Unity or AssetImportWorker is already running for this project ($desc). Refusing to start; existing processes are not terminated."
}

New-Item -ItemType Directory -Path $player.Directory -Force | Out-Null
if (-not (Test-Path -LiteralPath $player.Directory -PathType Container)) {
    Fail-Infrastructure "Could not create the player directory: $($player.Directory)"
}

$runId = New-RunId
$outDir = Join-Path $repoRoot ("Logs\UnityTests\" + $runId)
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
if (-not (Test-Path -LiteralPath $outDir -PathType Container)) {
    Fail-Infrastructure "Could not create output directory: $outDir"
}
$resultXml = Join-Path $outDir 'StandaloneResults.xml'
$logPath = Join-Path $outDir 'StandaloneTests.log'

$startTime = Get-Date

# Path arguments are explicitly quoted so paths containing spaces are passed
# intact to Unity.
$unityArguments = '-batchmode -projectPath "{0}" -runTests -testPlatform StandaloneWindows64 -buildPlayerPath "{1}" -assemblyNames {2} -testResults "{3}" -logFile "{4}"' -f `
    $repoRoot, $player.Directory, $TestAssemblyName, $resultXml, $logPath

$unityProc = $null
try {
    $unityProc = Start-Process -FilePath $UnityEditorPath -ArgumentList $unityArguments -PassThru -ErrorAction Stop
} catch {
    Fail-Infrastructure "Failed to start Unity: $($_.Exception.Message)"
}
if ($null -eq $unityProc) {
    Fail-Infrastructure 'Failed to launch Unity (no process handle was returned).'
}
$unityPid = $unityProc.Id

# Wait for the launched editor process. The test player is started and stopped
# by the Test Framework itself and is never terminated from here; only a
# timeout terminates the editor this script launched.
$didExit = $false
try {
    $didExit = $unityProc.WaitForExit($TimeoutSeconds * 1000)
} catch {
    Stop-Process -Id $unityPid -Force -ErrorAction SilentlyContinue
    Fail-Infrastructure "Failed while waiting for the Unity process (terminated only the launched PID $unityPid): $($_.Exception.Message)"
}
$elapsedSeconds = ((Get-Date) - $startTime).TotalSeconds

if (-not $didExit) {
    Stop-Process -Id $unityPid -Force -ErrorAction SilentlyContinue
    Fail-Infrastructure "Standalone test run timed out after $TimeoutSeconds seconds; terminated only the launched Unity PID $unityPid (Hub, Licensing Client and other Unity processes were left untouched)."
}

$unityExitCode = 0
try {
    $unityExitCode = $unityProc.ExitCode
} catch {
    Fail-Infrastructure "Failed to read the Unity exit code: $($_.Exception.Message)"
}

# Results XML is mandatory.
$summary = Read-TestSummary $resultXml
if ((Get-Item -LiteralPath $resultXml).LastWriteTime -lt $startTime) {
    Fail-Infrastructure "Results XML is older than the run start time (stale result): $resultXml"
}
if ($summary.total -le 0) {
    Fail-Infrastructure "Results XML reports zero tests (total=$($summary.total))."
}

Assert-UnityLogClean $logPath

# The point of this runner is that the player really is the same executable
# every time, so a missing one is an infrastructure failure even if the tests
# passed: it would mean the run went somewhere else.
if (-not (Test-Path -LiteralPath $player.Executable -PathType Leaf)) {
    Fail-Infrastructure "The test player was not found at the fixed path: $($player.Executable)"
}

$allPassed = ($summary.result -eq 'Passed' -and
              $summary.passed -eq $summary.total -and
              $summary.failed -eq 0 -and
              $summary.inconclusive -eq 0 -and
              $summary.skipped -eq 0)

# A non-zero Unity exit code must not be ignored: with a Passed XML it is an
# infrastructure anomaly; with a failed XML it is the ordinary test-failure
# path handled below.
if ($allPassed -and $unityExitCode -ne 0) {
    Fail-Infrastructure "Unity exited with code $unityExitCode although the results XML reports Passed."
}

Write-Host ''
if ($allPassed) {
    Write-Host 'Unity Standalone (Windows x64) tests: PASSED' -ForegroundColor Green
} else {
    Write-Host 'Unity Standalone (Windows x64) tests: FAILED' -ForegroundColor Red
}
Write-Host ('  Unity version : {0}' -f $unityVersion)
Write-Host ('  Assembly      : {0}' -f $TestAssemblyName)
Write-Host ('  Player        : {0}' -f $player.Executable)
Write-Host ('  Run ID        : {0}' -f $runId)
Write-Host ('  Unity PID     : {0}' -f $unityPid)
Write-Host ('  Unity exit    : {0}' -f $unityExitCode)
Write-Host ('  Elapsed       : {0:N1} s' -f $elapsedSeconds)
Write-Host ('  Results XML   : {0}' -f $resultXml)
Write-Host ('  Log           : {0}' -f $logPath)
Write-Host ('  total/passed/failed/skipped/inconclusive : {0}/{1}/{2}/{3}/{4}' -f $summary.total, $summary.passed, $summary.failed, $summary.skipped, $summary.inconclusive)

if ($allPassed) {
    Write-Host '  Exit code     : 0'
    exit 0
} else {
    Write-Host '  Exit code     : 1'
    exit 1
}
