# SPDX-License-Identifier: MIT
#Requires -Version 5.1
[CmdletBinding()]
param([string]$Blender = 'C:\Program Files\Blender Foundation\Blender 4.5\blender.exe')
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$cache = Join-Path $root 'generated'
[IO.Directory]::CreateDirectory($cache) | Out-Null
$source = Join-Path $root 'source/bun_zipper.ply'
if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'b1acc63bece78444aa2e15bdcc72371a201279b98c6f5d4b74c993d02f0566fe') { throw 'Official Bunny hash mismatch' }
function Invoke-BunnyBlender([string[]]$Arguments) {
    & $Blender --background --factory-startup --python-exit-code 1 @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Bunny preparation failed ($LASTEXITCODE)" }
}
Invoke-BunnyBlender @('--python', (Join-Path $root 'prepare_source.py'))
Invoke-BunnyBlender @((Join-Path $cache 'source.blend'), '--python', (Join-Path $root 'repair.py'), '--', '--asset-family','Stanford','--asset-id','Bunny','--addon-parent',(Join-Path $root 'addons'),'--addon-version','1.23.0','--split-three-face-sheets','--output',(Join-Path $cache 'repair-report.json'),'--output-blend',(Join-Path $cache 'repaired.blend'))
$repairReport = Get-Content -LiteralPath (Join-Path $cache 'repair-report.json') -Raw | ConvertFrom-Json
if (!$repairReport.StrictRecipeHealthGatePass -or !$repairReport.PreservationGatePass -or !$repairReport.OutputBlendPath) { throw 'Repair health/preservation gate failed; do not use an older cached blend' }
Invoke-BunnyBlender @((Join-Path $cache 'repaired.blend'), '--python',(Join-Path $root 'convex.py'))
$convexReport = Get-Content -LiteralPath (Join-Path $cache 'convex_report.json') -Raw | ConvertFrom-Json
if ($convexReport.status -ne 'success') { throw 'Convex validation failed' }
Invoke-BunnyBlender @((Join-Path $cache 'convex.blend'), '--python',(Join-Path $root 'export_unity.py'))
Write-Output 'Prepared official Bunny for noncommercial builds. Commercial builds must exclude Assets/NonCommercialDemo.'
