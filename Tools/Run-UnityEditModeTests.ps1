#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the Unity EditMode tests with the fixed Unity 6.3 LTS editor, then
    validates the results XML, Unity log and Git state (fail closed).

.DESCRIPTION
    Resolves the repository root from the script location (current directory
    independent, worktree aware), verifies the fixed Unity editor and its
    version, refuses to run while the same project is already open, launches
    "Unity.exe -batchmode -runTests" without -quit, waits for the process
    (default 300 seconds), then requires:

      - a parseable results XML with result=Passed, total>0, passed==total,
        failed=0, inconclusive=0 and skipped=0;
      - a Unity log without compiler / license / test-failure indicators;
      - no new tracked changes introduced into the Git working tree.

    Exit codes:
      0 = all tests passed and XML / log / Git state are clean
      1 = tests ran but failed / skipped / inconclusive are present
      2 = infrastructure error (editor missing or version mismatch, process
          conflict, launch failure, timeout, license or compile failure,
          missing/corrupt XML, zero tests, or new tracked changes)

.PARAMETER TimeoutSeconds
    Maximum seconds to wait for the Unity process to finish. Default is 300.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass `
      -File .\Tools\Run-UnityEditModeTests.ps1

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass `
      -File .\Tools\Run-UnityEditModeTests.ps1 -TimeoutSeconds 120
#>

[CmdletBinding()]
param(
    [int]$TimeoutSeconds = 300
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

$UnityEditorPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
$RequiredEditorVersion = '6000.3.22f1'

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
# Fixed editor verification. Returns the version (and full revision) found in
# ProjectVersion.txt. Does not fall back to another editor.
# ---------------------------------------------------------------------------
function Assert-Editor {
    param([string]$RepoRoot)
    if (-not (Test-Path -LiteralPath $UnityEditorPath -PathType Leaf)) {
        Fail-Infrastructure "Unity editor not found at: $UnityEditorPath"
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
        Fail-Infrastructure "Editor version mismatch: ProjectVersion.txt=$version, required=$RequiredEditorVersion. Falling back to another editor is not allowed."
    }
    return [pscustomobject]@{
        Version     = $version
        FullVersion = $fullVersion
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
# Git status (porcelain v1) lines
# ---------------------------------------------------------------------------
function Get-GitStatusLines {
    param([string]$RepoRoot)
    $out = @(& git -C $RepoRoot status --porcelain=v1 2>&1)
    if ($LASTEXITCODE -ne 0) {
        Fail-Infrastructure "git status failed (exit $LASTEXITCODE)."
    }
    return @($out)
}

# ---------------------------------------------------------------------------
# Binary-safe staged/unstaged diff hash (raw bytes, not stringified)
# ---------------------------------------------------------------------------
function Get-GitDiffHashes {
    param([string]$RepoRoot)
    $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ('zantetsuken-gitdiff-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $tempDir -Force | Out-Null
    try {
        $stagedFile = Join-Path $tempDir 'staged.diff'
        $unstagedFile = Join-Path $tempDir 'unstaged.diff'

        # "--output" makes Git write the patch straight to disk as raw bytes
        # (PowerShell never touches the content). "--binary" includes the
        # base85-encoded content of binary files, so further changes to an
        # already-dirty binary file are detected too.
        & git -C $RepoRoot diff ('--output=' + $stagedFile) --cached --binary --full-index --no-ext-diff
        if ($LASTEXITCODE -ne 0) {
            Fail-Infrastructure "git diff --cached failed (exit $LASTEXITCODE)."
        }
        & git -C $RepoRoot diff ('--output=' + $unstagedFile) --binary --full-index --no-ext-diff
        if ($LASTEXITCODE -ne 0) {
            Fail-Infrastructure "git diff failed (exit $LASTEXITCODE)."
        }

        $stagedHash = (Get-FileHash -LiteralPath $stagedFile -Algorithm SHA256).Hash
        $unstagedHash = (Get-FileHash -LiteralPath $unstagedFile -Algorithm SHA256).Hash
        return ($stagedHash + ':' + $unstagedHash)
    } finally {
        Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Test-LogContains {
    param([string]$LogPath, [string]$Pattern)
    $matches = @(Select-String -Path $LogPath -Pattern $Pattern -SimpleMatch)
    return ($matches.Count -gt 0)
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
# Unity log inspection (case-insensitive). Any match = fail (exit 2).
# ---------------------------------------------------------------------------
function Assert-UnityLogClean {
    param([string]$LogPath)
    if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) {
        Fail-Infrastructure "Unity log not found: $LogPath"
    }
    # Infrastructure-level failures (compile / license / headless). "Test run
    # failed" is intentionally not listed here: it also appears for ordinary
    # test failures, which are classified from the results XML as exit code 1.
    $infraPatterns = @(
        'error CS',
        'Compilation failed',
        'Scripts have compiler errors',
        'Licensing initialization failed',
        'com.unity.editor.headless'
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

$editor = Assert-Editor $repoRoot
$unityVersion = if ($editor.FullVersion) { $editor.FullVersion } else { $editor.Version }

$conflicts = @(Get-ProjectUnityConflicts $repoRoot)
if ($conflicts.Count -gt 0) {
    $desc = ($conflicts | ForEach-Object { "PID $($_.ProcessId) ($($_.Name)): $($_.Reason)" }) -join '; '
    Fail-Infrastructure "Unity or AssetImportWorker is already running for this project ($desc). Refusing to start; existing processes are not terminated."
}

if ($TimeoutSeconds -lt 1 -or $TimeoutSeconds -gt 3600) {
    Fail-Infrastructure "TimeoutSeconds must be in the range 1..3600 (got $TimeoutSeconds)."
}

$runId = New-RunId
$outDir = Join-Path $repoRoot ("Logs\UnityTests\" + $runId)
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
if (-not (Test-Path -LiteralPath $outDir -PathType Container)) {
    Fail-Infrastructure "Could not create output directory: $outDir"
}
$resultXml = Join-Path $outDir 'EditModeResults.xml'
$logPath = Join-Path $outDir 'EditModeTests.log'

$beforeStatus = @(Get-GitStatusLines $repoRoot)
$beforeDiffHash = Get-GitDiffHashes $repoRoot

$startTime = Get-Date

# Note: $args is a reserved automatic variable; use a purpose-specific name.
# Path arguments are explicitly quoted so worktree paths containing spaces are
# passed intact to Unity.
$unityArguments = '-batchmode -projectPath "{0}" -runTests -testPlatform EditMode -testResults "{1}" -logFile "{2}"' -f $repoRoot, $resultXml, $logPath

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

# Wait for the launched Unity process to actually finish (the editor process
# runs the tests and exits when they complete; its import workers are children
# and are torn down with it).
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
    Fail-Infrastructure "Unity test run timed out after $TimeoutSeconds seconds; terminated only the launched Unity PID $unityPid (Hub, Licensing Client and other Unity processes were left untouched)."
}

$unityExitCode = 0
try {
    $unityExitCode = $unityProc.ExitCode
} catch {
    Fail-Infrastructure "Failed to read the Unity exit code: $($_.Exception.Message)"
}

$afterStatus = @(Get-GitStatusLines $repoRoot)
$afterDiffHash = Get-GitDiffHashes $repoRoot

# Results XML is mandatory.
$summary = Read-TestSummary $resultXml
if ((Get-Item -LiteralPath $resultXml).LastWriteTime -lt $startTime) {
    Fail-Infrastructure "Results XML is older than the run start time (stale result): $resultXml"
}
if ($summary.total -le 0) {
    Fail-Infrastructure "Results XML reports zero tests (total=$($summary.total))."
}

# Unity log must be clean of compiler / license / test-failure indicators.
Assert-UnityLogClean $logPath

# Git state must not gain new tracked changes. The status-line comparison is an
# auxiliary check (it catches new/untracked files); the staged/unstaged diff hash
# comparison below is the primary check and also catches further modifications
# to files that were already dirty before the run.
$newChanges = @($afterStatus | Where-Object { $beforeStatus -notcontains $_ })
if ($newChanges.Count -gt 0) {
    [Console]::Error.WriteLine('ERROR: Unity run introduced new tracked changes:')
    foreach ($line in $newChanges) { [Console]::Error.WriteLine("  $line") }
    exit 2
}
if ($beforeDiffHash -ne $afterDiffHash) {
    [Console]::Error.WriteLine('ERROR: Unity run modified tracked file contents (staged/unstaged diff hash changed).')
    exit 2
}

$allPassed = ($summary.result -eq 'Passed' -and
              $summary.passed -eq $summary.total -and
              $summary.failed -eq 0 -and
              $summary.inconclusive -eq 0 -and
              $summary.skipped -eq 0)

# "Test run failed" is a test-failure indicator, not an infrastructure one. If
# the XML already shows a test failure, that is classified below as exit 1; if
# the XML claims Passed while the log disagrees, fail closed.
if ($allPassed -and (Test-LogContains $logPath 'Test run failed')) {
    Fail-Infrastructure 'Results XML reports Passed but the Unity log contains "Test run failed".'
}

# A non-zero Unity exit code must not be ignored: with a Passed XML it is an
# infrastructure anomaly; with a failed XML it is the ordinary test-failure
# path handled below.
if ($allPassed -and $unityExitCode -ne 0) {
    Fail-Infrastructure "Unity exited with code $unityExitCode although the results XML reports Passed."
}

Write-Host ''
if ($allPassed) {
    Write-Host 'Unity EditMode tests: PASSED' -ForegroundColor Green
} else {
    Write-Host 'Unity EditMode tests: FAILED' -ForegroundColor Red
}
Write-Host ('  Unity version : {0}' -f $unityVersion)
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
