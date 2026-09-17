#Requires -Version 5.1
<#
.SYNOPSIS
    Runs the shared-dispatch Windows Standalone (IL2CPP) player tests into a
    fixed player path outside the repository, then validates the results XML
    (fail closed).

.DESCRIPTION
    DESIGN 4.3 and 4.4 have parts a test double cannot stand in for: real
    always-on threads at the priority they were configured with, a Burst direct
    call prepared on the main thread and then called from a pool worker, a work
    waiting in a pool queue, ended work held until the main thread takes it, and
    the normal stop. Those are what the Zantetsu.MeshCut.StandaloneTests
    assembly checks, and a player is the only place it can.

    The Unity Test Framework builds a throw-away test player for every
    Standalone run and, left to itself, writes it into a new temporary directory
    each time - so a Windows Firewall decision a human makes for one player
    never matches the next run's executable. Like Run-NvencStandaloneTests.ps1,
    this runner pins the player to one absolute path outside the repository, so
    that one human decision keeps holding.

    That path is this harness's own, and deliberately not the one the NVENC
    runner pins: a build refuses a directory that already holds a player built
    with another scripting backend ("Build path contains a project previously
    built with the Mono2x scripting backend"), so two harnesses sharing one
    directory can block each other. The first run of a new path may therefore
    need a one-time firewall decision from a human, because the test player
    connects back to the editor. This script never adds, removes or inspects
    firewall rules, and never deletes a player another tool pinned.

    The scripting backend is not changed here: the player is built with whatever
    ProjectSettings already says, which for Standalone in this project is
    IL2CPP. Nothing here needs or starts XR.

    The Unity editor is an explicit required argument: no Unity Hub scan and no
    registry lookup.

    Exit codes:
      0 = the dispatch assembly ran in the player and every test passed
      1 = tests ran but failures / skips / inconclusive results are present
      2 = infrastructure error (editor missing or version mismatch, refused
          player directory, launch failure, timeout, or a missing, corrupt or
          empty results XML)

.PARAMETER UnityEditorPath
    Full path to the Unity.exe that must run the tests. Required; its own
    ProductVersion must equal the version and revision in
    ProjectSettings/ProjectVersion.txt.

.PARAMETER PlayerDirectory
    Directory the test player is built into. Optional; defaults to
    "%LOCALAPPDATA%\Zantetsu\TestPlayers\WindowsStandalone64Dispatch", which is
    this harness's own. A directory inside the repository is refused before any
    side effect, and so is one holding a player of another backend. The Test Framework
    names the executable itself, so the fixed executable is
    "<PlayerDirectory>\PlayerWithTests\PlayerWithTests.exe" - the path a
    firewall rule has to name. It is printed by every run.

.PARAMETER TimeoutSeconds
    Maximum seconds to wait for the Unity process. Default is 3600, because an
    IL2CPP player build is not quick.

.EXAMPLE
    powershell.exe -NoProfile -ExecutionPolicy Bypass `
      -File .\Tools\Run-DispatchStandaloneTests.ps1 `
      -UnityEditorPath 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$UnityEditorPath,

    [string]$PlayerDirectory,

    [int]$TimeoutSeconds = 3600
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Continue'

$TestAssemblyName = 'Zantetsu.MeshCut.StandaloneTests'

function Fail-Infrastructure {
    param([string]$Message)
    [Console]::Error.WriteLine("ERROR: $Message")
    exit 2
}

function Get-RepositoryRoot {
    # The exit code of a native command is not consulted here: redirecting its stderr in Windows PowerShell can
    # leave $LASTEXITCODE meaning nothing. What is checked is the answer itself.
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Fail-Infrastructure 'Git is required but was not found on PATH.'
    }

    $root = (& git -C $PSScriptRoot rev-parse --show-toplevel | Select-Object -First 1)
    if ($root) {
        $full = [System.IO.Path]::GetFullPath($root.ToString().Trim())
        if (Test-Path -LiteralPath $full -PathType Container) {
            return $full
        }
    }

    Fail-Infrastructure 'Could not locate a Git repository root.'
}

function Assert-Editor {
    param([string]$RepoRoot, [string]$EditorPath)

    if (-not (Test-Path -LiteralPath $EditorPath -PathType Leaf)) {
        Fail-Infrastructure "Unity editor not found at: $EditorPath"
    }

    $versionFile = Join-Path $RepoRoot 'ProjectSettings\ProjectVersion.txt'
    if (-not (Test-Path -LiteralPath $versionFile -PathType Leaf)) {
        Fail-Infrastructure "ProjectVersion.txt not found: $versionFile"
    }

    $projectVersionWithRevision = $null
    foreach ($line in (Get-Content -LiteralPath $versionFile)) {
        if ($line -match '^m_EditorVersionWithRevision:\s*(.+)$') {
            $projectVersionWithRevision = $Matches[1].Trim()
        }
    }
    if (-not $projectVersionWithRevision) {
        Fail-Infrastructure 'Could not read m_EditorVersionWithRevision from ProjectVersion.txt.'
    }
    if ($projectVersionWithRevision -notmatch '^(\S+)\s*\(([^)]+)\)$') {
        Fail-Infrastructure "Could not split m_EditorVersionWithRevision: $projectVersionWithRevision"
    }
    $expected = $Matches[1] + '_' + $Matches[2]

    $actual = $null
    try {
        $actual = (Get-Item -LiteralPath $EditorPath).VersionInfo.ProductVersion
    } catch {
        Fail-Infrastructure "Could not read the version information of ${EditorPath}: $($_.Exception.Message)"
    }
    if (-not $actual) {
        Fail-Infrastructure "The Unity executable reports no ProductVersion: $EditorPath"
    }
    if ($actual.Trim() -ne $expected) {
        Fail-Infrastructure "Editor version mismatch: $EditorPath reports '$($actual.Trim())', the project requires '$expected'."
    }
}

function Resolve-PlayerDirectory {
    param([string]$RepoRoot, [string]$Requested)

    $candidate = $Requested
    if (-not $candidate) {
        if (-not $env:LOCALAPPDATA) {
            Fail-Infrastructure 'LOCALAPPDATA is not set; pass -PlayerDirectory explicitly.'
        }
        $candidate = Join-Path $env:LOCALAPPDATA 'Zantetsu\TestPlayers\WindowsStandalone64Dispatch'
    }

    if (-not [System.IO.Path]::IsPathRooted($candidate)) {
        Fail-Infrastructure "The player directory must be absolute (got: $candidate)."
    }

    $full = [System.IO.Path]::GetFullPath($candidate).TrimEnd('\')
    if ([System.IO.Path]::GetExtension($full)) {
        Fail-Infrastructure "The player directory must be a directory, not a file (got: $full)."
    }

    $rootWithSeparator = $RepoRoot.TrimEnd('\') + '\'
    if ($full.Equals($RepoRoot.TrimEnd('\'), 'OrdinalIgnoreCase') -or
        $full.StartsWith($rootWithSeparator, 'OrdinalIgnoreCase')) {
        Fail-Infrastructure "The player directory must be outside the repository (got: $full)."
    }

    if (-not (Test-Path -LiteralPath $full -PathType Container)) {
        try {
            New-Item -ItemType Directory -Path $full -Force | Out-Null
        } catch {
            Fail-Infrastructure "Could not create the player directory ${full}: $($_.Exception.Message)"
        }
    }

    return $full
}

$repoRoot = Get-RepositoryRoot
Assert-Editor -RepoRoot $repoRoot -EditorPath $UnityEditorPath
$playerDirectory = Resolve-PlayerDirectory -RepoRoot $repoRoot -Requested $PlayerDirectory
$playerExecutable = Join-Path $playerDirectory 'PlayerWithTests\PlayerWithTests.exe'

$runId = (Get-Date).ToString('yyyyMMdd-HHmmss') + '-' + [System.Guid]::NewGuid().ToString('N').Substring(0, 6)
$outputDirectory = Join-Path $repoRoot ('Logs\UnityTests\' + $runId)
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$resultsPath = Join-Path $outputDirectory 'StandaloneResults.xml'
$logPath = Join-Path $outputDirectory 'StandaloneTests.log'

Write-Output ''
Write-Output "Shared dispatch Standalone (IL2CPP) player tests"
Write-Output "  Assembly  : $TestAssemblyName"
Write-Output "  Run ID    : $runId"
Write-Output "  Player    : $playerExecutable"
Write-Output "  Results   : $resultsPath"
Write-Output "  Log       : $logPath"

$unityArguments = '-batchmode -projectPath "{0}" -runTests -testPlatform StandaloneWindows64 -buildPlayerPath "{1}" -assemblyNames {2} -testResults "{3}" -logFile "{4}"' -f `
    $repoRoot, $playerDirectory, $TestAssemblyName, $resultsPath, $logPath

$started = Get-Date
$unityProcess = $null
try {
    $unityProcess = Start-Process -FilePath $UnityEditorPath -ArgumentList $unityArguments -WindowStyle Hidden -PassThru -ErrorAction Stop
} catch {
    Fail-Infrastructure "Could not start the Unity editor: $($_.Exception.Message)"
}

if (-not $unityProcess.WaitForExit($TimeoutSeconds * 1000)) {
    try { $unityProcess.Kill() } catch { }
    Fail-Infrastructure "The Unity process did not finish within $TimeoutSeconds seconds; it was terminated."
}

$elapsed = ((Get-Date) - $started).TotalSeconds
Write-Output ("  Unity exit: {0}" -f $unityProcess.ExitCode)
Write-Output ("  Elapsed   : {0:N1} s" -f $elapsed)

if (-not (Test-Path -LiteralPath $resultsPath -PathType Leaf)) {
    Fail-Infrastructure "No results XML was written: $resultsPath"
}

$xml = $null
try {
    $xml = [xml](Get-Content -LiteralPath $resultsPath -Raw)
} catch {
    Fail-Infrastructure "The results XML could not be read: $($_.Exception.Message)"
}

$run = $xml.'test-run'
if (-not $run) {
    Fail-Infrastructure 'The results XML has no test-run element.'
}

$total = [int]$run.total
$passed = [int]$run.passed
$failed = [int]$run.failed
$skipped = [int]$run.skipped
$inconclusive = [int]$run.inconclusive

Write-Output ("  total/passed/failed/skipped/inconclusive : {0}/{1}/{2}/{3}/{4}" -f $total, $passed, $failed, $skipped, $inconclusive)

if ($total -le 0) {
    Fail-Infrastructure 'The run reports zero tests; the assembly did not run.'
}

foreach ($case in $xml.SelectNodes('//test-case')) {
    if ($case.result -ne 'Passed') {
        Write-Output ("  {0}: {1}" -f $case.result, $case.fullname)
        $message = $case.SelectSingleNode('.//message')
        if ($message) {
            Write-Output ("      {0}" -f $message.InnerText)
        }
    }
}

if ($failed -gt 0 -or $skipped -gt 0 -or $inconclusive -gt 0) {
    Write-Output 'Shared dispatch Standalone player tests: FAILED'
    exit 1
}

Write-Output 'Shared dispatch Standalone player tests: PASSED'
exit 0
