#Requires -Version 5.1
param([string]$PrivateRoot = (Join-Path $PSScriptRoot '..\..\..\zantetsuken-assets-private'))
$ErrorActionPreference = 'Stop'
$source = Join-Path $PrivateRoot 'MobPlan\ITHappy_f_1'
$destination = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\Assets\Licensed\MobPlan'))
$manifest = Get-Content -LiteralPath (Join-Path $source 'manifest.json') -Encoding utf8 -Raw | ConvertFrom-Json
if ($manifest.boneCount -ne 66 -or $manifest.clips.Count -ne 320) { throw 'Unexpected MobPlan bank' }
foreach ($clip in $manifest.clips) {
    $file = Join-Path $source $clip.file
    if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $clip.sha256) { throw "Hash mismatch (run git lfs pull in the private repo): $file" }
}
foreach ($entry in @(@('dataset.bytes', $manifest.datasetSha256), @('polygons.bytes', $manifest.mapSha256))) {
    if ((Get-FileHash -LiteralPath (Join-Path $source $entry[0]) -Algorithm SHA256).Hash -ne $entry[1]) { throw "Hash mismatch: $($entry[0])" }
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Get-ChildItem -LiteralPath $source -Force | Where-Object { $_.Name -ne '.gitattributes' } | Copy-Item -Destination $destination -Recurse -Force
Write-Output "Installed 320 verified 66-bone tables to $destination"
