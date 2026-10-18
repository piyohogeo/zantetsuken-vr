#Requires -Version 5.1
# Forbidden words (unit 11, TL 2026-10-18): words such as a personal name that must appear neither in the public
# repository nor in a distributed APK. The list is a UTF-8 text file kept OUTSIDE the repository (one word a line,
# '#' starts a comment); the words themselves are never written by anything here -- a text is shown with each word
# replaced by '<forbidden>', and a match is reported by where it is and how many there are.
#
# Dot-sourced by Tools/Verify-PublicRepository.ps1 and Tools/Quest3s/Inspect-DistributionApk.ps1.
#
# What a matcher finds: a word's UTF-8 bytes and its UTF-16LE bytes, case-insensitively for ASCII letters, in the
# bytes it is given. It does not open compressed data (a zstd .blend, a .gz, a PNG's compressed text): the callers
# give it raw file contents, or an APK entry already inflated by the zip reader.

function Read-ForbiddenWords {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        # The list must not be inside this folder (the repository it guards).
        [string]$RepositoryRoot
    )
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw 'The forbidden-word list was not found (its path is not shown).'
    }
    $full = [System.IO.Path]::GetFullPath($Path)
    if ($RepositoryRoot) {
        $prefix = [System.IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/').Replace('/', '\') + '\'
        if ($full.Replace('/', '\').StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The forbidden-word list must be kept outside the repository.'
        }
    }
    $words = @(Get-Content -LiteralPath $full -Encoding UTF8 |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_ -and -not $_.StartsWith('#') })
    if ($words.Count -eq 0) {
        throw 'The forbidden-word list has no words.'
    }
    return , $words
}

function New-ForbiddenMatcher {
    param([Parameter(Mandatory = $true)][string[]]$Words)
    # Bytes are read as ISO-8859-1 (one character a byte), so a word's UTF-8 bytes and its UTF-16LE bytes can both be
    # looked for with an ordinary case-insensitive search.
    $latin1 = [System.Text.Encoding]::GetEncoding(28591)
    $needles = @()
    foreach ($w in $Words) {
        $needles += $latin1.GetString([System.Text.Encoding]::UTF8.GetBytes($w))
        $needles += $latin1.GetString([System.Text.Encoding]::Unicode.GetBytes($w))
    }
    return [pscustomobject]@{
        Count       = $Words.Count
        Latin1      = $latin1
        Needles     = $needles
        MaskPattern = '(?i)' + (($Words | ForEach-Object { [regex]::Escape($_) }) -join '|')
    }
}

function Get-ForbiddenCount {
    param([Parameter(Mandatory = $true)]$Matcher, [Parameter(Mandatory = $true)][AllowEmptyCollection()][byte[]]$Bytes)
    if ($Bytes.Length -eq 0) { return 0 }
    $haystack = $Matcher.Latin1.GetString($Bytes)
    $n = 0
    foreach ($needle in $Matcher.Needles) {
        $i = 0
        while (($i = $haystack.IndexOf($needle, $i, [StringComparison]::OrdinalIgnoreCase)) -ge 0) {
            $n++
            $i += $needle.Length
        }
    }
    return $n
}

function Hide-ForbiddenWords {
    param([Parameter(Mandatory = $true)]$Matcher, [AllowEmptyString()][string]$Text)
    if (-not $Text) { return $Text }
    return [regex]::Replace($Text, $Matcher.MaskPattern, '<forbidden>')
}
