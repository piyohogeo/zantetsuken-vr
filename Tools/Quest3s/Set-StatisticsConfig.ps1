#Requires -Version 5.1
<#
.SYNOPSIS
  Makes, or moves aside, the play statistics' local send configuration in a build tree (DESIGN 21.18, unit 9-B).

.DESCRIPTION
  The configuration is a Resources asset that is never committed (.gitignore). This script makes it from the environment
  through the Editor's PlayStatisticsConfigSetup.FromEnvironment in batch mode, so that no value is typed into a file by
  hand: the endpoint is the receiver's URL, the token is read from the environment variable named by -TokenVariable
  (ZANTETSU_INGEST_TOKEN_ALPHA for a distribution, ZANTETSU_INGEST_TOKEN for a developer build) and handed to the child
  process only, the role is external for a distribution. The token's value is never printed or written anywhere else.

  Making refuses a tree that already holds the asset or its .meta (it is never updated in place or overwritten: move it
  aside first with -Remove). Where making fails after Unity has written anything, the asset and its .meta are moved
  aside into -AsideDirectory before the error is thrown, so that a failed run leaves no token in the tree.
  With -Remove, the asset and its .meta are moved out of the tree into -AsideDirectory (never deleted: a build that was
  made with them stays reproducible by the record), so that no tree keeps a token between builds.

  Build-Distribution.ps1 -Statistics required runs both itself (make before the build, move aside in its finally);
  this script alone is for a tree that is not built by it.

.EXAMPLE
  .\Tools\Quest3s\Set-StatisticsConfig.ps1 -Project %USERPROFILE%\src\zantetsuken-vr-questbuild -Role external -TokenVariable ZANTETSU_INGEST_TOKEN_ALPHA -AsideDirectory C:\log\...\config-aside
  .\Tools\Quest3s\Set-StatisticsConfig.ps1 -Project %USERPROFILE%\src\zantetsuken-vr-questbuild -Remove -AsideDirectory C:\log\...\config-aside
#>
[CmdletBinding()]
param(
    [string]$Project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')),
    [string]$Endpoint = 'https://zantetsuken-telemetry-api.hogehogeo.workers.dev/v1/telemetry',
    [ValidateSet('external', 'developer')][string]$Role = 'external',
    [string]$TokenVariable = 'ZANTETSU_INGEST_TOKEN_ALPHA',
    [int]$TimeoutSeconds = 5,
    [switch]$Remove,
    [Parameter(Mandatory = $true)][string]$AsideDirectory,
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$Project = [IO.Path]::GetFullPath($Project).TrimEnd('\')
$AsideDirectory = [IO.Path]::GetFullPath($AsideDirectory).TrimEnd('\')
# Neither the project itself nor anything under it (it holds the token).
if ($AsideDirectory.Equals($Project, [StringComparison]::OrdinalIgnoreCase) -or $AsideDirectory.StartsWith($Project + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "-AsideDirectory must be outside the project (not the project itself either; it holds the token): $AsideDirectory" }
$asset = Join-Path $Project 'Assets\Zantetsu\Runtime\Sandbox\Resources\ZantetsuPlayStatisticsConfig.asset'
$meta = "$asset.meta"

# Moves the asset and its .meta, whichever are there, into a new folder under -AsideDirectory; never deletes or overwrites.
function Move-ConfigAside([string]$why) {
    $present = @($asset, $meta | Where-Object { Test-Path -LiteralPath $_ })
    if ($present.Count -eq 0) { return $null }
    $target = Join-Path $AsideDirectory ((Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + $why)
    if (Test-Path -LiteralPath $target) { throw "exists already: $target" }
    New-Item -ItemType Directory -Path $target | Out-Null
    foreach ($p in $present) { Move-Item -LiteralPath $p -Destination (Join-Path $target (Split-Path -Leaf $p)) }
    foreach ($p in $asset, $meta) { if (Test-Path -LiteralPath $p) { throw "still in the tree after the move: $p" } }
    return $target
}

if ($Remove) {
    $target = Move-ConfigAside 'removed'
    if ($null -eq $target) { "no statistics config in $Project (nothing to move aside)"; exit 0 }
    "statistics config moved aside: $target (holds the token: keep it out of any repository and record)"
    exit 0
}

foreach ($p in $asset, $meta) { if (Test-Path -LiteralPath $p) { throw "a statistics config is in the tree already ($p): it is never updated in place; move it aside first (-Remove -AsideDirectory ...)" } }
$token = [Environment]::GetEnvironmentVariable($TokenVariable)
if ([string]::IsNullOrEmpty($token)) { $token = [Environment]::GetEnvironmentVariable($TokenVariable, 'User') }
if ([string]::IsNullOrEmpty($token)) { throw "the environment variable $TokenVariable is not set (process or User); the token is never passed on a command line" }
if ($Endpoint -notmatch '^https://') { throw "the endpoint must be https: $Endpoint" }
$log = Join-Path $Project ('Logs\statistics-config-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.log')
[IO.Directory]::CreateDirectory((Split-Path -Parent $log)) | Out-Null
$arguments = '-batchmode -quit -force-d3d11 -projectPath "' + $Project + '" -executeMethod Zantetsu.Editor.Sandbox.PlayStatisticsConfigSetup.FromEnvironment -logFile "' + $log + '"'
# The child process alone gets the token under the name the Editor tool reads; this process's environment is untouched.
$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = $Unity; $start.Arguments = $arguments; $start.UseShellExecute = $false; $start.WorkingDirectory = $Project
$start.EnvironmentVariables['ZANTETSU_STATS_ENDPOINT'] = $Endpoint
$start.EnvironmentVariables['ZANTETSU_INGEST_TOKEN'] = $token
$start.EnvironmentVariables['ZANTETSU_STATS_ROLE'] = $Role
$start.EnvironmentVariables['ZANTETSU_STATS_TIMEOUT'] = [string]$TimeoutSeconds
$start.EnvironmentVariables['ZANTETSU_STATS_SEND'] = '1'
$token = $null
$made = $false
try {
    $process = [System.Diagnostics.Process]::Start($start)
    $process.WaitForExit()
    $start.EnvironmentVariables.Remove('ZANTETSU_INGEST_TOKEN')
    if ($process.ExitCode -ne 0) { throw "Unity exited $($process.ExitCode); see $log" }
    if (!(Test-Path -LiteralPath $asset)) { throw "Unity exited 0 but the asset is not there: $asset" }
    $line = @(Select-String -LiteralPath $log -Pattern '^PLAY STATISTICS CONFIG: ' | ForEach-Object { $_.Line }) | Select-Object -Last 1
    if (!$line) { throw "the Editor tool's line is not in $log" }
    if ($line -notmatch 'token present') { throw "the configuration has no token: $line" }
    if ($line -notmatch ('role ' + [regex]::Escape($Role))) { throw "the configuration's role is not $Role`: $line" }
    if ($line -notmatch ('endpoint ' + [regex]::Escape($Endpoint) + ',')) { throw "the configuration's endpoint is not $Endpoint`: $line" }
    $made = $true
}
finally {
    $start.EnvironmentVariables.Remove('ZANTETSU_INGEST_TOKEN')
    if (!$made) {
        $target = Move-ConfigAside 'failed-make'
        if ($null -ne $target) { "the making failed: what Unity wrote was moved aside to $target" }
    }
}
"statistics config made in $Project -- $($line -replace '^PLAY STATISTICS CONFIG: ', '')"
"(the token's value is in the asset only; move it aside after the build with -Remove)"
