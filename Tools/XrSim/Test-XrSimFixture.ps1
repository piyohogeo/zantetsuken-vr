#Requires -Version 5.1
<#
.SYNOPSIS
    Verifies a candidate VRS fixture with the documented automated replay and promotes it only when verified.

.DESCRIPTION
    Runs Invoke-XrSimReplay.ps1 on the candidate (VP3 and VP3C alternately, -Runs each, plus one no-VRS control
    per path), then Python/xrsim_fixture.py verify:

      - captures are taken only while the replay is in progress, matched to P1 -> P2 -> P3 by replay state and
        head-pose order (never by replay time alone); a run that skipped a pose (ran ahead), whose camera did not
        follow the head, whose replay opened before the view was fixed, or that failed, is excluded from the
        success count with its reasons;
      - recorded, replayed and no-VRS head poses are compared; the fixture is "verified" only when every pose
        has at least -MinValidRuns valid VP3 and VP3C captures and the no-VRS controls stay away from the
        recorded non-default poses. Image, shadow atlas and VP3C selection comparisons are reported, not gated.

    With -Promote and a verified result, the recording, targets.json, fixture.json (status verified) and the
    verification report are copied to <WorkRoot>\fixtures\<FixtureName>\ (never overwritten).

    Exit codes: 0 verified (and promoted when asked), 1 not verified, 2 infrastructure error.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$CandidateDirectory,
    [string]$WorkRoot = (Join-Path $env:LOCALAPPDATA 'Zantetsu\XrSim'),
    [string]$PlayerDirectory = '',
    [ValidateRange(1, 10)][int]$Runs = 3,
    [ValidateRange(1, 10)][int]$MinValidRuns = 2,
    [int]$DelayStartMs = 20000,
    [string]$FixtureName = '',
    [switch]$Promote,
    [switch]$HideSimulatorWindow
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'XrSim.psm1') -Force

$WorkRoot = [IO.Path]::GetFullPath($WorkRoot)
$candidateFixture = Get-Content -LiteralPath (Join-Path $CandidateDirectory 'fixture.json') -Raw | ConvertFrom-Json
if ($candidateFixture.status -ne 'candidate') { Write-Error "fixture status is '$($candidateFixture.status)'; only QC-passed candidates are verified"; exit 2 }
if ($Promote -and -not $FixtureName) { Write-Error '-Promote needs -FixtureName'; exit 2 }
$verifyDir = Join-Path $WorkRoot ('verification\{0}-{1:yyyyMMdd-HHmmss}' -f $candidateFixture.id, (Get-Date))
New-Item -ItemType Directory -Force -Path $verifyDir | Out-Null

$sequence = @()
for ($i = 1; $i -le $Runs; $i++) { $sequence += "VP3:$i"; $sequence += "VP3C:$i" }
$replayArgs = @{
    FixtureDirectory = $CandidateDirectory; WorkRoot = $WorkRoot; OutputDirectory = (Join-Path $verifyDir 'runs'); Sequence = $sequence
    NoReplay = @('VP3', 'VP3C'); DelayStartMs = $DelayStartMs; AllowCandidate = $true; HideSimulatorWindow = $HideSimulatorWindow
}
if ($PlayerDirectory) { $replayArgs.PlayerDirectory = $PlayerDirectory }
& (Join-Path $PSScriptRoot 'Invoke-XrSimReplay.ps1') @replayArgs
if ($LASTEXITCODE -eq 2) { Write-Error 'replay runs failed (infrastructure); see replay-summary.json'; exit 2 }

$python = Get-XrSimPython -WorkRoot $WorkRoot
$verifyJson = Invoke-XrSimPython -Python $python -Arguments @((Join-Path $PSScriptRoot 'Python\xrsim_fixture.py'), 'verify', '--fixture', (Join-Path $CandidateDirectory 'fixture.json'),
    '--runs', (Join-Path $verifyDir 'runs\replay-*'), (Join-Path $verifyDir 'runs\noreplay-*'), '--out-dir', $verifyDir, '--min-valid', $MinValidRuns)
$verifyExit = $LASTEXITCODE
$verifyJson | ForEach-Object { $_ }
if ($verifyExit -gt 1) { exit 2 }

if ($verifyExit -eq 0 -and $Promote) {
    $target = Join-Path $WorkRoot "fixtures\$FixtureName"
    if (Test-Path -LiteralPath $target) { Write-Error "fixture already exists, not overwritten: $target"; exit 2 }
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    Copy-Item -LiteralPath (Join-Path $CandidateDirectory $candidateFixture.recording.file) -Destination $target
    Copy-Item -LiteralPath (Join-Path $CandidateDirectory 'targets.json') -Destination $target
    Copy-Item -LiteralPath (Join-Path $verifyDir 'fixture.verified.json') -Destination (Join-Path $target 'fixture.json')
    Copy-Item -LiteralPath (Join-Path $verifyDir 'verification.json') -Destination $target
    Copy-Item -LiteralPath (Join-Path $verifyDir 'verification.md') -Destination $target
    $copied = Get-FileHash -LiteralPath (Join-Path $target $candidateFixture.recording.file) -Algorithm SHA256
    if ($copied.Hash -ne $candidateFixture.recording.sha256) { Write-Error 'promoted recording SHA-256 differs'; exit 2 }
    Write-XrSimLog "promoted verified fixture: $target"
}
Write-XrSimLog "verification report: $(Join-Path $verifyDir 'verification.md')"
exit $verifyExit
