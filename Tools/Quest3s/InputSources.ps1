#Requires -Version 5.1
# The licensed inputs' source field in a distribution build (unit 11, TL 2026-10-18): dot-sourced by
# Build-Distribution.ps1. The placed cuttables' inputs under Assets/Licensed (Git does not hold them) carry in "source"
# the absolute path of the file they were made from, which would go into the APK. For the build alone, in the tree
# being built, that one value is written empty; the original is copied aside first and put back after the build
# whatever happened, its hash checked; Unity's import is checked in both states
# (Zantetsu.EditorTools.Quest.QuestDistributionInputSources). Nothing of an input's content is printed or recorded.
#
# A file is an input of the confirmed format when it is a .json under Assets/Licensed holding each of the keys of
# PlacedCuttableInput's data, exactly one "source" key whose value is an absolute path (a drive letter or a leading
# slash), and nothing else is touched: every byte outside that value stays as it is (checked).

$script:InputKeys = @('schemaVersion', 'topologyCount', 'name', 'source', 'sha256', 'isBuilding', 'isCuttable', 'render', 'indices', 'topology', 'hulls')
$script:SourcePattern = [regex]'"source"\s*:\s*"((?:[^"\\]|\\.)*)"'
$script:Utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Get-FileSha256([byte[]]$Bytes) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash($Bytes)) -replace '-', '').ToLowerInvariant() } finally { $sha.Dispose() }
}

# The inputs of the confirmed format with an absolute source, as paths relative to the project ('/' separated).
function Find-InputSourceTargets([string]$Project) {
    $licensed = Join-Path $Project 'Assets\Licensed'
    $targets = @()
    if (!(Test-Path -LiteralPath $licensed)) { return $targets }
    foreach ($file in Get-ChildItem -LiteralPath $licensed -Recurse -File -Filter '*.json') {
        $text = $script:Utf8NoBom.GetString([IO.File]::ReadAllBytes($file.FullName))
        $isInput = $true
        foreach ($k in $script:InputKeys) { if ($text.IndexOf('"' + $k + '"', [StringComparison]::Ordinal) -lt 0) { $isInput = $false; break } }
        if (!$isInput) { continue }
        $found = $script:SourcePattern.Matches($text)
        if ($found.Count -ne 1) { continue }
        $value = $found[0].Groups[1].Value
        if ($value -notmatch '^([A-Za-z]:(\\\\|/)|/)') { continue }
        $targets += $file.FullName.Substring($Project.TrimEnd('\').Length + 1).Replace('\', '/')
    }
    return $targets
}

# Writes each target's source empty in place, its original copied first to <Aside>\<relative path>; the manifest
# (QuestDistributionInputSources) gets one line a target as it is done, so a failure part way still leaves every file
# changed so far in it to be put back. Throws on any failure (the caller's finally puts back what the manifest names).
function Set-BlankInputSources([string]$Project, [string[]]$Targets, [string]$Aside, [string]$Manifest) {
    New-Item -ItemType Directory -Path $Aside -Force | Out-Null
    if (Test-Path -LiteralPath $Manifest) { throw "the input-source manifest exists already: $Manifest" }
    New-Item -ItemType File -Path $Manifest | Out-Null
    foreach ($rel in $Targets) {
        $path = Join-Path $Project $rel
        $bytes = [IO.File]::ReadAllBytes($path)
        $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
        $text = $script:Utf8NoBom.GetString($bytes, $(if ($bom) { 3 } else { 0 }), $bytes.Length - $(if ($bom) { 3 } else { 0 }))
        # The text must come back to the same bytes, so that writing it changes no byte but the value's.
        $again = $script:Utf8NoBom.GetBytes($text)
        if ($bom) { $again = [byte[]](0xEF, 0xBB, 0xBF) + $again }
        if ((Get-FileSha256 $again) -ne (Get-FileSha256 $bytes)) { throw "an input is not plain UTF-8 text (it would not be written back byte for byte): $rel" }
        $found = $script:SourcePattern.Matches($text)
        if ($found.Count -ne 1) { throw "an input changed since it was found (not one source): $rel" }
        $g = $found[0].Groups[1]
        $blankText = $text.Substring(0, $g.Index) + $text.Substring($g.Index + $g.Length)
        # Every character outside the value as it was: the blank text is the original with that one span taken out.
        if ($blankText.Length -ne $text.Length - $g.Length -or $blankText.Substring(0, $g.Index) -ne $text.Substring(0, $g.Index) -or $blankText.Substring($g.Index) -ne $text.Substring($g.Index + $g.Length)) { throw "the blank text of $rel is not the original less its source" }
        $blankBytes = $script:Utf8NoBom.GetBytes($blankText)
        if ($bom) { $blankBytes = [byte[]](0xEF, 0xBB, 0xBF) + $blankBytes }
        $copy = Join-Path $Aside $rel
        New-Item -ItemType Directory -Path (Split-Path -Parent $copy) -Force | Out-Null
        if (Test-Path -LiteralPath $copy) { throw "an original is in the aside folder already (not overwritten): $rel" }
        [IO.File]::WriteAllBytes($copy, $bytes)
        if ((Get-FileSha256 ([IO.File]::ReadAllBytes($copy))) -ne (Get-FileSha256 $bytes)) { throw "the original of $rel was not copied aside whole" }
        # The line first, then the change: a failure between them leaves an original that equals the file.
        # UTF-8 without a byte order mark (the Editor reads it so): an asset path may hold any character.
        [IO.File]::AppendAllText($Manifest, ("{0}`t{1}`t{2}`t{3}`n" -f $rel, (Get-FileSha256 $blankBytes), (Get-FileSha256 $bytes), $rel), $script:Utf8NoBom)
        [IO.File]::WriteAllBytes($path, $blankBytes)
    }
}

# Puts every original the manifest names back and checks its hash. Returns the failures (empty: all back).
function Restore-InputSources([string]$Project, [string]$Aside, [string]$Manifest) {
    $failures = @()
    if (!(Test-Path -LiteralPath $Manifest)) { return $failures }
    foreach ($line in [IO.File]::ReadAllLines($Manifest, $script:Utf8NoBom)) {
        if (!$line) { continue }
        $f = $line.Split("`t")
        if ($f.Count -ne 4) { $failures += "a malformed manifest line"; continue }
        $path = Join-Path $Project $f[0]
        $copy = Join-Path $Aside $f[3]
        try {
            [IO.File]::WriteAllBytes($path, [IO.File]::ReadAllBytes($copy))
            if ((Get-FileSha256 ([IO.File]::ReadAllBytes($path))) -ne $f[2]) { $failures += "$($f[0]) is not its original after the put-back" }
        } catch { $failures += "$($f[0]) could not be put back: $($_.Exception.Message)" }
    }
    return $failures
}

# Unity's import checked from disk (QuestDistributionInputSources.CheckFromCommandLine): 'original' after the put-back,
# 'blank' to check a blank state on its own. Returns Unity's exit code (0: as expected).
function Invoke-InputSourceCheck([string]$Unity, [string]$Project, [string]$Manifest, [string]$Aside, [string]$Expect, [string]$Log) {
    $arguments = '-batchmode -quit -force-d3d11 -projectPath "' + $Project + '" -buildTarget Android -executeMethod Zantetsu.EditorTools.Quest.QuestDistributionInputSources.CheckFromCommandLine -logFile "' + $Log + '"' `
        + ' -zantetsuDistInputSources "' + $Manifest + '" -zantetsuDistInputSourcesAside "' + $Aside + '" -zantetsuDistInputSourcesExpect ' + $Expect
    $process = Start-Process -FilePath $Unity -ArgumentList $arguments -WorkingDirectory $Project -WindowStyle Hidden -PassThru
    $null = $process.Handle
    $process.WaitForExit()
    return $process.ExitCode
}
