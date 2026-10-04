#Requires -Version 5.1
<#
.SYNOPSIS
    Isolated synthetic checks for Phase 0.21 content identity and sidecar registration. No Unity or private assets.
.DESCRIPTION
    Creates a fresh temporary repository and synthetic files. Leaves that directory in place for inspection.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$registerScript = Join-Path $PSScriptRoot 'Register-ReferenceAsset.ps1'
$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('zantetsu-reference-intake-' + [guid]::NewGuid().ToString('N'))
$sourceDir = Join-Path $testRoot 'source'
$repositoryRoot = Join-Path $testRoot 'repository'
New-Item -ItemType Directory -Path $sourceDir | Out-Null
$script:checks = 0

function Write-Text([string]$path, [string]$value) {
    [System.IO.File]::WriteAllText($path, $value, [System.Text.UTF8Encoding]::new($false))
}
function Write-Descriptor([string]$path, [object[]]$items) {
    Write-Text $path (ConvertTo-Json -InputObject $items -Depth 8)
}
function Hash-Text([string]$value) {
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    try { (($hasher.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($value)) | ForEach-Object { $_.ToString('x2') }) -join '') }
    finally { $hasher.Dispose() }
}
function Check([bool]$condition, [string]$message) {
    if (-not $condition) { throw "FAILED: $message" }
    $script:checks++
    Write-Host "PASS $message"
}
function Register([string]$name, [string]$descriptor = '', [string[]]$references = @()) {
    $arguments = @{ Root = $repositoryRoot; Command = 'register'; Name = $name; Fbx = $fbx; Textures = @($texture); References = $references }
    if ($descriptor) { $arguments.Inputs = $descriptor }
    & $registerScript @arguments | Out-Host
    $asset = Get-Content -LiteralPath (Join-Path $repositoryRoot "Working\Phase0.21\registry\assets\$name.json") -Raw -Encoding UTF8 | ConvertFrom-Json
    return $asset.assetSha
}
function Manifest([string]$sha) {
    return Get-Content -LiteralPath (Join-Path $repositoryRoot "Working\Phase0.21\registry\manifests\$sha.json") -Raw -Encoding UTF8 | ConvertFrom-Json
}

$fbx = Join-Path $sourceDir 'synthetic.fbx'
$texture = Join-Path $sourceDir 'synthetic.png'
$proxy = Join-Path $sourceDir 'proxy.json'
$anchor = Join-Path $sourceDir 'anchor.json'
$reference = Join-Path $sourceDir 'source.blend'
$descriptor = Join-Path $sourceDir 'inputs.json'
Write-Text $fbx 'synthetic FBX identity fixture, not a parsable model'
Write-Text $texture 'synthetic texture identity fixture'
Write-Text $proxy '{"vertices":[0,1,2]}'
Write-Text $anchor '{"anchor":[0,0,0]}'
Write-Text $reference 'reference-only version one'

$legacy = Register 'legacy'
$fbxHash = (Get-FileHash -LiteralPath $fbx -Algorithm SHA256).Hash.ToLowerInvariant()
$textureHash = (Get-FileHash -LiteralPath $texture -Algorithm SHA256).Hash.ToLowerInvariant()
$legacyExpected = Hash-Text "fbx:$fbxHash`ntexture:synthetic.png:$textureHash`n"
Check ($legacy -ceq $legacyExpected) 'legacy FBX + texture canonical identity is unchanged'

Write-Descriptor $descriptor @()
$empty = Register 'empty-inputs' $descriptor
Check ($empty -ceq $legacy) 'empty sidecar descriptor does not change legacy identity'
$referenceOnly = Register 'reference-only' '' @($reference)
Write-Text $reference 'reference-only version two'
$referenceChanged = Register 'reference-only' '' @($reference)
Check (($referenceOnly -ceq $legacy) -and ($referenceChanged -ceq $legacy)) 'reference changes do not change Asset SHA'

# This path is relative to the descriptor, not to the caller working directory.
$proxySpec = [pscustomobject]@{ role = 'physics-proxy'; target = 'render-body'; path = 'proxy.json' }
$anchorSpec = [pscustomobject]@{ role = 'anchor'; target = 'render-body'; path = 'anchor.json' }
Write-Descriptor $descriptor @($proxySpec, $anchorSpec)
$first = Register 'with-inputs' $descriptor
$manifest = Manifest $first
Check ($first -cne $legacy) 'explicit processing inputs change Asset SHA'
Check ($manifest.inputs.Count -eq 2) 'manifest holds both processing inputs'
foreach ($entry in $manifest.inputs) {
    $blobPath = Join-Path $repositoryRoot ('Working\Phase0.21\blobs\' + $entry.blob)
    Check ((Test-Path -LiteralPath $blobPath) -and ((Get-FileHash -LiteralPath $blobPath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $entry.sha256)) "sidecar blob is retained with its content hash: $($entry.role)"
}
$resolved = @(& $registerScript -Root $repositoryRoot resolve -AssetSha $first) -join "`n"
Check ($resolved.Contains('role=physics-proxy, target=render-body, file=proxy.json')) 'resolve exposes the input role, correspondence and blob'

Write-Descriptor $descriptor @($anchorSpec, $proxySpec)
$reordered = Register 'reordered' $descriptor
Check ($reordered -ceq $first) 'descriptor order does not change identity'
$proxySpec.path = $proxy
$anchorSpec.path = $anchor
Write-Descriptor $descriptor @($proxySpec, $anchorSpec)
$absolute = Register 'absolute-paths' $descriptor
Check ($absolute -ceq $first) 'absolute versus descriptor-relative paths do not change identity'

$proxySpec.role = 'physics-proxy-alternate'
Write-Descriptor $descriptor @($proxySpec, $anchorSpec)
$roleChanged = Register 'role-changed' $descriptor
Check ($roleChanged -cne $first) 'input role changes Asset SHA'
$proxySpec.role = 'physics-proxy'
$proxySpec.target = 'different-render-body'
Write-Descriptor $descriptor @($proxySpec, $anchorSpec)
$targetChanged = Register 'target-changed' $descriptor
Check ($targetChanged -cne $first) 'input correspondence changes Asset SHA'
$proxySpec.target = 'render-body'
$renamedProxy = Join-Path $sourceDir 'proxy-renamed.json'
Copy-Item -LiteralPath $proxy -Destination $renamedProxy
$proxySpec.path = $renamedProxy
Write-Descriptor $descriptor @($proxySpec, $anchorSpec)
$fileChanged = Register 'file-changed' $descriptor
Check ($fileChanged -cne $first) 'registered input file name changes Asset SHA'
$proxySpec.path = $proxy

& $registerScript -Root $repositoryRoot dataset -Name 'sample' -Assets @('with-inputs') | Out-Host
$datasetPath = Join-Path $repositoryRoot 'Working\Phase0.21\registry\datasets\sample.json'
$firstRevision = (Get-Content -LiteralPath $datasetPath -Raw -Encoding UTF8 | ConvertFrom-Json).revision
Write-Text $proxy '{"vertices":[0,1,2,3]}'
Write-Descriptor $descriptor @($proxySpec, $anchorSpec)
$contentChanged = Register 'with-inputs' $descriptor
Check ($contentChanged -cne $first) 'input contents change Asset SHA'
& $registerScript -Root $repositoryRoot dataset -Name 'sample' -Assets @('with-inputs') | Out-Host
$dataset = Get-Content -LiteralPath $datasetPath -Raw -Encoding UTF8 | ConvertFrom-Json
Check (($dataset.revision -cne $firstRevision) -and ($dataset.revisions.Count -eq 2)) 'dataset update retains the earlier content revision'
$oldResolution = @(& $registerScript -Root $repositoryRoot resolve -Dataset 'sample' -Revision $firstRevision) -join "`n"
Check ($oldResolution.Contains("asset $first ")) 'old dataset revision resolves the previous input content'

# Model a pre-extension manifest, whose inputs property did not exist.
$legacyManifest = Manifest $legacy
$legacyManifest.PSObject.Properties.Remove('inputs')
$legacyManifestPath = Join-Path $repositoryRoot "Working\Phase0.21\registry\manifests\$legacy.json"
Write-Text $legacyManifestPath ($legacyManifest | ConvertTo-Json -Depth 8)
$legacyResolution = @(& $registerScript -Root $repositoryRoot resolve -AssetSha $legacy) -join "`n"
Check ($legacyResolution.Contains("asset $legacy ")) 'resolve reads legacy manifests without an inputs property'

Write-Descriptor $descriptor @($proxySpec, $proxySpec)
$duplicateRejected = $false
try { Register 'duplicate' $descriptor | Out-Null }
catch { $duplicateRejected = $_.Exception.Message.Contains('duplicate input role/target/file') }
Check $duplicateRejected 'duplicate role, correspondence and file are rejected'

$pathNameRejected = $false
try { Register '..\outside-registry' | Out-Null }
catch { $pathNameRejected = $_.Exception.Message.Contains('registration name must be one file name') }
Check $pathNameRejected 'registration name cannot escape its registry directory'

Write-Descriptor $descriptor @([pscustomobject]@{ role = 'anchor'; path = $anchor })
$missingTargetRejected = $false
try { Register 'missing-target' $descriptor | Out-Null }
catch { $missingTargetRejected = $_.Exception.Message.Contains("each input needs a non-empty string 'target'" ) }
Check $missingTargetRejected 'processing input correspondence must be explicit'

$verification = @(& $registerScript -Root $repositoryRoot verify) -join "`n"
Check ($verification -match 'verified \d+ blobs, 0 mismatches') 'verify covers all stored entities including sidecars'
Write-Host "Completed: $script:checks checks passed. Synthetic evidence retained at $testRoot"
