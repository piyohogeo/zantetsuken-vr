#Requires -Version 5.1
<#
.SYNOPSIS
    Verifies that the public repository contains no prohibited content.

.DESCRIPTION
    Implements the first safety net for DESIGN.md D-025, D-030, D-090 and
    T-026, T-033. The script scans the Git repository for:

      1. Prohibited directories/files that are currently tracked or that ever
         existed in the commit history.
      2. User-specific absolute paths embedded in currently tracked text files,
         detected independently of the account that runs this script (normal,
         forward-slash and JSON double-escaped forms).
      3. Unity generated directories that must never be tracked.

    Exit codes:
      0 - repository is clean
      1 - one or more violations were found
      2 - the script could not run (Git unavailable or a required Git command
         failed)

    This script depends only on PowerShell and Git. No external PowerShell
    modules or additional packages are required.

    NOTE: the license asset contents themselves are never written to standard
    output; only offending paths (and line numbers for text scans) are shown.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
# Native Git commands report failures through $LASTEXITCODE rather than the
# PowerShell error stream. 'Continue' keeps Git's stderr output (for example
# "fatal: not a git repository") from becoming a terminating error, so the
# explicit exit-code checks below remain in control of the script's exit code.
$ErrorActionPreference = 'Continue'

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Fail-Infrastructure {
    param([string]$Message)
    [Console]::Error.WriteLine("ERROR: $Message")
    exit 2
}

function Get-RepositoryRoot {
    # Prefer Git, which also understands worktrees and bare repositories.
    $gitRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel 2>$null | Select-Object -First 1)
    if ($LASTEXITCODE -eq 0 -and $gitRoot) {
        return [System.IO.Path]::GetFullPath($gitRoot.ToString().Trim())
    }

    # Fallback: walk up the directory tree looking for a .git entry
    # (a directory, or a file for linked worktrees).
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

function ConvertTo-RepoPath {
    param([string]$Path)
    return $Path.Replace('\', '/').ToLowerInvariant()
}

# Prohibited directories (checked as path prefixes, case-insensitively).
$script:ForbiddenDirectories = @(
    'assets/licensed/',
    'assets/thirdpartyprivate/',
    'generated/cutassets/',
    'vendor/synty/'
)

function Test-ProhibitedDirectory {
    param([string]$RepoPath)
    $p = ConvertTo-RepoPath $RepoPath
    foreach ($dir in $script:ForbiddenDirectories) {
        $name = $dir.TrimEnd('/')
        if ($p -eq $name -or $p.StartsWith($dir)) {
            return "prohibited directory '$name/'"
        }
    }
    return $null
}

function Test-ProhibitedFile {
    param([string]$RepoPath)
    $p = ConvertTo-RepoPath $RepoPath
    $fileName = [System.IO.Path]::GetFileName($p).ToLowerInvariant()
    $extension = [System.IO.Path]::GetExtension($p).ToLowerInvariant()

    # Any Unity package archive is prohibited.
    if ($extension -eq '.unitypackage') {
        return 'prohibited file *.unitypackage'
    }

    # Blender executable binary (the portable layout is Tools/Blender/<ver>/blender.exe).
    if ($fileName -eq 'blender.exe') {
        return 'prohibited Blender executable binary'
    }
    if ($p -match '^tools/blender/[^/]+/blender\.exe$') {
        return "prohibited path 'Tools/Blender/*/blender.exe'"
    }

    # Synty original/derivative archives. Synty distributes its source packs as
    # zip/7z/rar/unitypackage archives whose names use the "POLYGON" product
    # prefix or the "Synty" vendor name. This is a name-based heuristic; the
    # authoritative protection for Synty content remains the prohibited
    # directory rules above plus the .unitypackage rule and .gitignore.
    $isArchive = ($extension -eq '.zip' -or $extension -eq '.7z' -or $extension -eq '.rar')
    if ($isArchive -and ($p.Contains('synty') -or $p.Contains('polygon'))) {
        return 'prohibited Synty original/derivative archive'
    }

    return $null
}

function Get-HistoryPaths {
    param([string[]]$Objects)
    $result = @()
    foreach ($line in $Objects) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        # "git rev-list --objects --all" emits "<sha> <path>"; paths can contain
        # spaces, so split only on the first space.
        $idx = $line.IndexOf(' ')
        if ($idx -le 0) { continue }
        $result += $line.Substring($idx + 1)
    }
    return $result
}

# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Fail-Infrastructure 'Git is required but was not found on PATH.'
}

$root = Get-RepositoryRoot

$violations = New-Object System.Collections.Generic.List[object]
function Add-Violation {
    param([string]$Scope, [string]$Reason, [string]$Path)
    $violations.Add([pscustomobject]@{ Scope = $Scope; Reason = $Reason; Path = $Path })
}

# --- 1. Prohibited directories/files among currently tracked files ---
$trackedFiles = @(& git -C $root ls-files 2>&1)
if ($LASTEXITCODE -ne 0) {
    Fail-Infrastructure ("'git ls-files' failed (exit {0}): {1}" -f $LASTEXITCODE, ($trackedFiles -join ' '))
}
foreach ($f in $trackedFiles) {
    if ([string]::IsNullOrWhiteSpace($f)) { continue }
    $dirReason = Test-ProhibitedDirectory $f
    if ($dirReason) { Add-Violation 'tracked path' $dirReason $f; continue }
    $fileReason = Test-ProhibitedFile $f
    if ($fileReason) { Add-Violation 'tracked file' $fileReason $f }
}

# --- 2. Prohibited directories/files anywhere in the commit history ---
$historyOutput = & git -C $root rev-list --objects --all 2>&1
if ($LASTEXITCODE -ne 0) {
    Fail-Infrastructure ("'git rev-list --objects --all' failed (exit {0}): {1}" -f $LASTEXITCODE, ($historyOutput -join ' '))
}
$historyObjects = @($historyOutput)

foreach ($p in (Get-HistoryPaths $historyObjects)) {
    if ([string]::IsNullOrWhiteSpace($p)) { continue }
    $dirReason = Test-ProhibitedDirectory $p
    if ($dirReason) { Add-Violation 'history path' $dirReason $p; continue }
    $fileReason = Test-ProhibitedFile $p
    if ($fileReason) { Add-Violation 'history file' $fileReason $p }
}

# --- 3. User-specific absolute paths in currently tracked text files ---
# Detects a profile path under the C: drive Users folder in three notations:
# normal backslashes, forward slashes, and JSON double-escaped backslashes.
# Detection is independent of the account running this script, so a path to
# another developer's profile (for example the CI "runneradmin" account
# scanning for a developer's home folder) is still flagged. Anonymous "%VAR%"
# placeholders such as "%USERNAME%" are explicitly allowed.
$userPattern = 'C:(\\+|/+)Users(\\+|/+)[^\\/]+'
$userHits = @(& git -C $root grep -o -I -n -i -E $userPattern 2>&1)
$userGrepExit = $LASTEXITCODE
if ($userGrepExit -gt 1) {
    Fail-Infrastructure ("'git grep' failed (exit {0}): {1}" -f $userGrepExit, ($userHits -join ' '))
}
foreach ($line in $userHits) {
    # "git grep -o -n" emits "path:line:matched-fragment".
    $idx1 = $line.IndexOf(':')
    if ($idx1 -le 0) { continue }
    $idx2 = $line.IndexOf(':', $idx1 + 1)
    if ($idx2 -le $idx1) { continue }
    $file = $line.Substring(0, $idx1)
    $lineNo = $line.Substring($idx1 + 1, $idx2 - $idx1 - 1)
    $fragment = $line.Substring($idx2 + 1)

    # Extract the account name that follows the Users folder segment. A
    # "%VAR%" placeholder is anonymous and therefore allowed; any other name
    # is user-specific and is reported below.
    if ($fragment -notmatch '^C:(\\+|/+)Users(\\+|/+)(?<name>[^\\/]+)$') { continue }
    $name = $Matches['name']
    if ($name -match '^%[^%]+%$') { continue }

    # Only the file path and line number are reported; file contents are never
    # echoed to standard output.
    Add-Violation 'tracked text' 'user-specific absolute path' ("$file`:$lineNo")
}

# --- 4. Unity generated directories must never be tracked ---
$unityGeneratedDirectories = @('Library', 'Temp', 'Logs', 'Obj', 'Build', 'Builds', 'UserSettings', 'Recordings')
foreach ($f in $trackedFiles) {
    if ([string]::IsNullOrWhiteSpace($f)) { continue }
    $p = ConvertTo-RepoPath $f
    foreach ($dir in $unityGeneratedDirectories) {
        $d = $dir.ToLowerInvariant()
        if ($p -eq $d -or $p.StartsWith($d + '/')) {
            Add-Violation 'tracked path' "Unity generated directory '$dir' must not be tracked" $f
            break
        }
    }
}

# ---------------------------------------------------------------------------
# Report
# ---------------------------------------------------------------------------
if ($violations.Count -eq 0) {
    Write-Host ''
    Write-Host 'Repository hygiene check: PASSED' -ForegroundColor Green
    Write-Host ''
    Write-Host ('  Tracked files scanned        : {0}' -f $trackedFiles.Count)
    Write-Host ('  History objects scanned      : {0}' -f $historyObjects.Count)
    Write-Host '  Prohibited directories/files : none'
    Write-Host '  User-specific absolute paths : none'
    Write-Host '  Unity generated directories  : none tracked'
    Write-Host ''
    exit 0
}

Write-Host ''
Write-Host 'Repository hygiene check: FAILED' -ForegroundColor Red
Write-Host ''
Write-Host ('  Violation(s): {0}' -f $violations.Count)
Write-Host ''
foreach ($group in ($violations | Group-Object Scope | Sort-Object Name)) {
    Write-Host ('  [{0}]' -f $group.Name) -ForegroundColor Yellow
    foreach ($v in $group.Group) {
        Write-Host ('    - {0}: {1}' -f $v.Reason, $v.Path)
    }
}
Write-Host ''
Write-Host 'Resolve the violations above before publishing to the public repository.'
exit 1
