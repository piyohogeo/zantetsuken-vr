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
      4. Forbidden words (for example a personal name), read from a list kept
         outside the repository (-ForbiddenWordsFile, or the environment
         variable ZANTETSU_FORBIDDEN_WORDS_FILE): in every tracked file's bytes
         (text and binary, as ASCII/UTF-8 and as UTF-16LE, case-insensitively),
         in tracked paths, in ref names and in commit metadata (author,
         committer, message) of every ref; with -ScanHistoryContents also in
         every blob reachable from any ref. The words themselves are never
         written: a path is shown with each word masked, a commit by its hash
         and the field, and counts only.

      When no list is given, step 4 is reported as NOT CHECKED (exit 0), or as
      a failure to run (exit 2) with -RequireForbiddenWords. A clean result
      covers only what is listed as scanned: compressed data inside a file
      (a zstd .blend, a .gz, a PNG's compressed text) is not opened. A history
      object that cannot be read whole is a failure to run (exit 2), never a
      blob passed over.

      The distribution and publication entries require the list: the
      distribution build (Tools/Quest3s/Build-Distribution.ps1: the current
      contents and the built HEAD's commit metadata, not the history's past
      contents), the pre-push hook (Tools/Hooks/pre-push: the pushed commits
      with -ScanHistoryContents; this local check is what stops a leak before
      publication) and the CI workflow (.github/workflows/repository-hygiene.yml,
      after a push; the list from the repository secret
      ZANTETSU_FORBIDDEN_WORDS).

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
param(
    # A UTF-8 text file outside the repository, one word per line ('#' starts a comment).
    [string]$ForbiddenWordsFile = $env:ZANTETSU_FORBIDDEN_WORDS_FILE,
    # Fail (exit 2) when no forbidden-word list is given, instead of reporting it as not checked.
    [switch]$RequireForbiddenWords,
    # Also scan the contents of every blob reachable from the revisions (slow).
    [switch]$ScanHistoryContents,
    # The commits whose metadata and (with -ScanHistoryContents) reachable blobs are scanned for forbidden words:
    # every ref's by default; a push gives the commits it sends (Tools/Hooks/pre-push).
    [string[]]$Revisions = @('--all'),
    # The repository (or worktree) to check; this script's own by default.
    [string]$RepositoryPath
)

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
    $gitRoots = @(& git -C $PSScriptRoot rev-parse --show-toplevel 2>$null)
    $gitRootExit = $LASTEXITCODE
    if ($gitRootExit -eq 0 -and $gitRoots.Count -gt 0) {
        return [System.IO.Path]::GetFullPath($gitRoots[0].ToString().Trim())
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

if ($RepositoryPath) {
    $given = @(& git -C $RepositoryPath rev-parse --show-toplevel 2>$null)
    if ($LASTEXITCODE -ne 0 -or $given.Count -eq 0) { Fail-Infrastructure 'The -RepositoryPath given is not a Git working tree.' }
    $root = [System.IO.Path]::GetFullPath($given[0].ToString().Trim())
} else {
    $root = Get-RepositoryRoot
}
# A hook passes several commits as one comma-separated argument (-File cannot pass an array).
$Revisions = @($Revisions | ForEach-Object { "$_" -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
if ($Revisions.Count -eq 0) { Fail-Infrastructure '-Revisions names no commit.' }
foreach ($rev in $Revisions) {
    if ($rev -ne '--all' -and $rev -notmatch '^\^?[0-9a-f]{40}$') { Fail-Infrastructure '-Revisions takes --all or full commit hashes (each may begin with ^).' }
}

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
# Stop at string/Markdown delimiters so an anonymous account is not combined
# with the following expression or closing quote. Escape the double quote
# also for Windows PowerShell 5.1 native-command argument passing.
$userPattern = 'C:(\\+|/+)Users(\\+|/+)[^\\/\"''`]+'
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
    if ($fragment -notmatch '^C:(\\+|/+)Users(\\+|/+)(?<name>[^\\/"''`]+)$') { continue }
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

# --- 5. Forbidden words (list kept outside the repository; never echoed) ---
. (Join-Path $PSScriptRoot 'ForbiddenWords.ps1')
$forbiddenSummary = 'NOT CHECKED (no list given: -ForbiddenWordsFile or ZANTETSU_FORBIDDEN_WORDS_FILE)'
$matcher = $null
if ($ForbiddenWordsFile) {
    try { $matcher = New-ForbiddenMatcher -Words (Read-ForbiddenWords -Path $ForbiddenWordsFile -RepositoryRoot $root) }
    catch { Fail-Infrastructure $_.Exception.Message }
} elseif ($RequireForbiddenWords) {
    Fail-Infrastructure 'No forbidden-word list was given (-ForbiddenWordsFile or ZANTETSU_FORBIDDEN_WORDS_FILE) and -RequireForbiddenWords is set.'
}

if ($matcher) {
    foreach ($f in $trackedFiles) {
        if ([string]::IsNullOrWhiteSpace($f)) { continue }
        $shown = Hide-ForbiddenWords $matcher $f
        if ($shown -ne $f) { Add-Violation 'forbidden word' 'in a tracked path' $shown }
        $full = Join-Path $root $f
        if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue }
        $n = Get-ForbiddenCount $matcher ([System.IO.File]::ReadAllBytes($full))
        if ($n -gt 0) { Add-Violation 'forbidden word' ("in a tracked file's contents ({0} match(es))" -f $n) $shown }
    }

    $refNames = @(& git -C $root for-each-ref '--format=%(refname)' 2>&1)
    if ($LASTEXITCODE -ne 0) { Fail-Infrastructure "'git for-each-ref' failed." }
    foreach ($r in $refNames) {
        $shown = Hide-ForbiddenWords $matcher "$r"
        if ($shown -ne "$r") { Add-Violation 'forbidden word' 'in a ref name' $shown }
    }

    # The commits and objects looked at: every ref's (the default), or those reachable from -Revisions (a push's).
    $savedEncoding = [Console]::OutputEncoding
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    try {
        $log = (& git -C $root log @Revisions '--format=%H%x1f%an%x1f%ae%x1f%cn%x1f%ce%x1f%B%x1e' 2>&1) -join "`n"
        $logExit = $LASTEXITCODE
    } finally {
        [Console]::OutputEncoding = $savedEncoding
    }
    if ($logExit -ne 0) { Fail-Infrastructure ("'git log' of the revisions failed (exit {0})." -f $logExit) }
    $commitCount = 0
    $fieldNames = @('author name', 'author email', 'committer name', 'committer email', 'message')
    foreach ($entry in $log.Split([char]0x1e)) {
        $parts = $entry.Trim("`n").Split([char]0x1f)
        if ($parts.Count -lt 6) { continue }
        $commitCount++
        for ($k = 1; $k -le 5; $k++) {
            if ((Hide-ForbiddenWords $matcher $parts[$k]) -ne $parts[$k]) {
                Add-Violation 'forbidden word' ("in a commit's {0}" -f $fieldNames[$k - 1]) $parts[0]
            }
        }
    }

    $historySummary = 'not scanned (use -ScanHistoryContents)'
    if ($ScanHistoryContents) {
        $objects = @(& git -C $root rev-list --objects @Revisions 2>&1)
        if ($LASTEXITCODE -ne 0) { Fail-Infrastructure ("'git rev-list --objects' of the revisions failed (exit {0})." -f $LASTEXITCODE) }
        $pathsBySha = @{}
        foreach ($line in $objects) {
            $text = "$line"
            $idx = $text.IndexOf(' ')
            if ($idx -le 0) { continue }
            $sha = $text.Substring(0, $idx)
            if (-not $pathsBySha.ContainsKey($sha)) { $pathsBySha[$sha] = New-Object System.Collections.Generic.List[string] }
            $pathsBySha[$sha].Add($text.Substring($idx + 1))
        }
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'git'
        $psi.Arguments = '-C "' + $root + '" cat-file --batch'
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        # The child's standard input is opened with the console's input encoding, whose byte order mark then comes
        # before the first request and makes it name no object (seen 2026-10-18: the first object came back
        # "missing"). It is started under an encoding without one, and the requests are written as ASCII bytes.
        $savedInputEncoding = [Console]::InputEncoding
        try {
            [Console]::InputEncoding = New-Object System.Text.UTF8Encoding($false)
            $proc = [System.Diagnostics.Process]::Start($psi)
        } finally {
            [Console]::InputEncoding = $savedInputEncoding
        }
        $stdin = $proc.StandardInput.BaseStream
        $stdout = $proc.StandardOutput.BaseStream
        $blobs = 0
        $hitPaths = New-Object 'System.Collections.Generic.SortedSet[string]'
        foreach ($sha in $pathsBySha.Keys) {
            $request = [System.Text.Encoding]::ASCII.GetBytes($sha + "`n")
            $stdin.Write($request, 0, $request.Length)
            $stdin.Flush()
            $header = New-Object System.Text.StringBuilder
            while (($b = $stdout.ReadByte()) -ne 10) {
                if ($b -lt 0) { Fail-Infrastructure ("'git cat-file --batch' ended early at {0}; the history could not be scanned." -f $sha) }
                [void]$header.Append([char]$b)
            }
            # Every object rev-list named must come back whole: a missing object, any other answer or a stream that
            # ends early is a failure to scan, never an object passed over.
            $fields = $header.ToString().Split(' ')
            if ($fields.Count -eq 2 -and $fields[1] -eq 'missing') {
                Fail-Infrastructure ("An object reachable from the revisions is missing ({0}); the history could not be scanned." -f $sha)
            }
            [int64]$size = 0
            if ($fields.Count -ne 3 -or $fields[0] -ne $sha -or -not [int64]::TryParse($fields[2], [ref]$size) -or $size -lt 0) {
                Fail-Infrastructure ("'git cat-file --batch' gave an unexpected answer for {0}; the history could not be scanned." -f $sha)
            }
            $buffer = New-Object byte[] $size
            $read = 0
            while ($read -lt $size) {
                $got = $stdout.Read($buffer, $read, [int]($size - $read))
                if ($got -le 0) { Fail-Infrastructure ("'git cat-file --batch' ended early in {0}; the history could not be scanned." -f $sha) }
                $read += $got
            }
            if ($stdout.ReadByte() -ne 10) {
                Fail-Infrastructure ("'git cat-file --batch' gave a malformed answer for {0}; the history could not be scanned." -f $sha)
            }
            if ($fields[1] -ne 'blob') { continue }
            $blobs++
            if ((Get-ForbiddenCount $matcher $buffer) -gt 0) {
                foreach ($p in $pathsBySha[$sha]) { [void]$hitPaths.Add((Hide-ForbiddenWords $matcher $p)) }
            }
        }
        $stdin.Close()
        if ($stdout.ReadByte() -ne -1) {
            Fail-Infrastructure "'git cat-file --batch' wrote more than was asked; the history could not be scanned."
        }
        $proc.WaitForExit()
        if ($proc.ExitCode -ne 0) {
            Fail-Infrastructure ("'git cat-file --batch' ended with exit code {0}; the history could not be scanned." -f $proc.ExitCode)
        }
        foreach ($p in $hitPaths) { Add-Violation 'forbidden word' 'in a blob reachable from the revisions, at path' $p }
        $historySummary = '{0} blobs reachable from {1}' -f $blobs, ($Revisions -join ' ')
    }

    $forbiddenSummary = 'checked {0} word(s): tracked files and paths, {1} ref names, metadata of {2} commits ({3}); history contents: {4}; compressed data inside files is not opened' -f $matcher.Count, $refNames.Count, $commitCount, ($Revisions -join ' '), $historySummary
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
    Write-Host ('  Forbidden words              : {0}' -f $forbiddenSummary)
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
Write-Host ('  Forbidden words: {0}' -f $forbiddenSummary)
Write-Host ''
Write-Host 'Resolve the violations above before publishing to the public repository.'
exit 1
