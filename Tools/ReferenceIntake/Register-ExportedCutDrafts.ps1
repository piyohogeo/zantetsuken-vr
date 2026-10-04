#Requires -Version 5.1
<# Register a completed export_cut_draft.py batch through the existing Phase 0.21 entrance.
The source blends are reference material only. Physics/anchor/bone data is an explicit input.
All output goes to the caller's private asset repository or export directory. No git operations.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true)][string]$SourceRoot,
    [Parameter(Mandatory = $true)][string]$ExportRoot,
    [Parameter(Mandatory = $true)][string]$DatasetName,
    [string[]]$Only = @()
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$register = Join-Path $PSScriptRoot 'Register-ReferenceAsset.ps1'
$sources = @(Get-ChildItem -LiteralPath $SourceRoot -File -Filter '*.blend' | Sort-Object Name)
if ($Only.Count) { $sources = @($sources | Where-Object { $Only -contains $_.BaseName }) }
if (-not $sources.Count) { throw 'No explicitly selected .blend sources' }
$rows = @()
foreach ($source in $sources) {
    $dir = Join-Path $ExportRoot $source.BaseName
    $exportPath = Join-Path $dir 'export.json'
    $m = Get-Content -LiteralPath $exportPath -Encoding UTF8 -Raw | ConvertFrom-Json
    if (-not $m.complete -or $m.sourceBlend -ne $source.Name -or
        $m.sourceBlendSha256 -ne $m.sourceBlendSha256After -or
        (Get-FileHash -LiteralPath $source.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $m.sourceBlendSha256) {
        throw "Incomplete export or changed source: $($source.Name)"
    }
    foreach ($file in $m.files) {
        $path = Join-Path $dir $file.file
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) {
            throw "Export file changed: $path"
        }
    }
    $textures = @($m.textures | ForEach-Object { Join-Path $dir $_.file })
    $name = $source.BaseName + '_convex_draft'
    $line = & $register -Root $Root register -Name $name -Fbx (Join-Path $dir $m.fbx) `
        -Textures $textures -Inputs (Join-Path $dir $m.inputs) -References @($source.FullName, $exportPath) `
        -Note 'Cut DAG reference draft: SOURCE render/rig FBX plus explicit convex, anchor and attachment input. Registration is not product or Gate approval.'
    if (-not $?) { throw "Registration failed: $name" }
    $sha = ($line -split ' ')[-1]
    if ($sha -notmatch '^[0-9a-f]{64}$') { throw "Unexpected registration output: $line" }
    $rows += [pscustomobject]@{ name = $name; assetSha = $sha; source = $source.FullName; sourceSha256 = $m.sourceBlendSha256; statistics = $m.statistics }
    Write-Output $line
}
$datasetResult = & $register -Root $Root dataset -Name $DatasetName -Assets @($rows | ForEach-Object { $_.name })
if (-not $?) { throw "Dataset registration failed: $DatasetName" }
Write-Output $datasetResult
$record = [pscustomobject]@{ dataset = $DatasetName; revision = ($datasetResult -split ' ')[-1]; assets = $rows; count = $rows.Count }
$reportPath = Join-Path $ExportRoot ('registration-' + $DatasetName + '.json')
[System.IO.File]::WriteAllText($reportPath, ($record | ConvertTo-Json -Depth 10) + "`n", [System.Text.UTF8Encoding]::new($false))
