#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$VersionName,
    [Parameter(Mandatory=$true)][ValidateRange(1,2100000000)][int]$VersionCode,
    [Parameter(Mandatory=$true)][string]$Keystore,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$KeyAlias,
    [string]$SigningCredential,
    [Parameter(Mandatory=$true)][string]$AsideDirectory,
    [string]$ForbiddenWordsFile,
    [string]$StatisticsTokenVariable='ZANTETSU_INGEST_TOKEN_ALPHA',
    [string]$Output,
    [string]$Unity='C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
)
& (Join-Path $PSScriptRoot 'Build-Distribution.ps1') @PSBoundParameters -Format Bundle
