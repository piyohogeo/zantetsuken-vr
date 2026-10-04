#Requires -Version 5.1
<#
.SYNOPSIS
    Phase 0.21 reference asset intake (DESIGN 10.2.3): registers exported FBX + texture entities into the private
    asset repository by content hash, forms datasets of asset SHAs with content revisions, and resolves an asset or
    dataset revision back to the stored entities and their reference materials.

.DESCRIPTION
    Layout under <Root>\Working\Phase0.21 (the private asset repository):

      blobs\<sha256><ext>              immutable content-addressed entities (FBX, textures, inputs, reference materials)
      registry\manifests\<assetSha>.json immutable description of one asset content: fbx + textures + inputs (+ references)
      registry\assets\<name>.json       display registration: current asset SHA, history, notes
      registry\datasets\<name>.json     current revision and retained revisions (each a sorted set of asset SHAs)

    Asset SHA = SHA-256 of the canonical text "fbx:<sha>\n" + "texture:<file name>:<sha>\n" (textures sorted by name).
    With -Inputs, append ordinally sorted lines input:<compact JSON [role,target,file,sha256]>\n. Without inputs,
    including an empty descriptor, the legacy FBX + texture identity is unchanged. -Inputs names a JSON array
    [{"role":"physics-proxy","target":"render-object-name","path":"proxy.json"}]. Paths are relative to the
    descriptor (absolute paths are also accepted); role and target explicitly describe the input's correspondence.
    The descriptor's location is not identity. Its registered input entities, roles, targets and file names are.
    Display names, placement and reference materials (.blend copies, export manifests, notes) do not enter it.
    Dataset revision = SHA-256 of the sorted asset SHAs joined by "\n". Registering here grants neither product
    adoption nor sharing permission; the licence boundary of DESIGN 10.8 applies to everything stored.

.EXAMPLE
    .\Register-ReferenceAsset.ps1 -Root C:\...\zantetsuken-assets-private-x register -Name asset_a `
        -Fbx ...\asset_a.fbx -Textures ...\texture.png -References ...\asset_a.export.json,...\source.blend -Note "..."
    .\Register-ReferenceAsset.ps1 -Root ... dataset -Name phase2_9-reference -Assets asset_a,asset_b
    .\Register-ReferenceAsset.ps1 -Root ... resolve -Dataset phase2_9-reference
    .\Register-ReferenceAsset.ps1 -Root ... resolve -AssetSha <sha>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Root,
    [Parameter(Mandatory = $true, Position = 0)][ValidateSet('register', 'dataset', 'resolve', 'verify')][string]$Command,
    [string]$Name,
    [string]$Fbx,
    [string[]]$Textures = @(),
    [string]$Inputs,
    [string[]]$References = @(),
    [string]$Note = '',
    [string[]]$Assets = @(),
    [string]$AssetSha,
    [string]$Dataset,
    [string]$Revision
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-RegistryName([string]$value) {
    if ([string]::IsNullOrWhiteSpace($value) -or $value.IndexOfAny([System.IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
        $value.EndsWith('.') -or $value.EndsWith(' ')) {
        throw "registration name must be one file name, not a path: '$value'"
    }
}
if ($Name) { Assert-RegistryName $Name }
if ($Dataset) { Assert-RegistryName $Dataset }
foreach ($a in $Assets) { Assert-RegistryName $a }
if ($AssetSha -and $AssetSha -notmatch '^[0-9a-fA-F]{64}$') { throw '-AssetSha must be a SHA-256 hex digest' }

$base = Join-Path $Root 'Working\Phase0.21'
$blobs = Join-Path $base 'blobs'
$manifests = Join-Path $base 'registry\manifests'
$assetsDir = Join-Path $base 'registry\assets'
$datasetsDir = Join-Path $base 'registry\datasets'
foreach ($d in @($blobs, $manifests, $assetsDir, $datasetsDir)) { New-Item -ItemType Directory -Force $d | Out-Null }

function Get-Sha256([string]$path) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }

function Get-TextSha256([string]$text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { ($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($text)) | ForEach-Object { $_.ToString('x2') }) -join '' } finally { $sha.Dispose() }
}

function Add-Blob([string]$path) {
    $sha = Get-Sha256 $path
    $ext = [System.IO.Path]::GetExtension($path).ToLowerInvariant()
    $target = Join-Path $blobs ($sha + $ext)
    if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $path -Destination $target }
    elseif ((Get-Sha256 $target) -ne $sha) { throw "existing blob does not match its hash: $target" }
    [pscustomobject]@{ file = [System.IO.Path]::GetFileName($path); sha256 = $sha; bytes = (Get-Item -LiteralPath $path).Length; blob = ($sha + $ext) }
}

function Write-Json($object, [string]$path) {
    $json = $object | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($path, $json + "`n", (New-Object System.Text.UTF8Encoding($false)))
}

function Read-Json([string]$path) { Get-Content -LiteralPath $path -Raw -Encoding UTF8 | ConvertFrom-Json }

$now = (Get-Date).ToUniversalTime().ToString('o')

switch ($Command) {
    'register' {
        if (-not $Name -or -not $Fbx) { throw 'register needs -Name and -Fbx' }
        $fbxEntry = Add-Blob $Fbx
        $textureEntries = @()
        foreach ($t in $Textures) { $textureEntries += Add-Blob $t }
        $textureEntries = @($textureEntries | Sort-Object file)
        $canonical = "fbx:$($fbxEntry.sha256)`n"
        foreach ($t in $textureEntries) { $canonical += "texture:$($t.file):$($t.sha256)`n" }
        $inputEntries = @()
        if ($Inputs) {
            $descriptorPath = (Resolve-Path -LiteralPath $Inputs).ProviderPath
            $descriptorDir = [System.IO.Path]::GetDirectoryName($descriptorPath)
            $descriptorText = Get-Content -LiteralPath $descriptorPath -Raw -Encoding UTF8
            if (-not $descriptorText.TrimStart().StartsWith('[')) { throw '-Inputs must contain a JSON array' }
            $inputRecords = [System.Collections.Generic.SortedDictionary[string,object]]::new([System.StringComparer]::Ordinal)
            $inputKeys = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)
            $inputSpecs = ConvertFrom-Json -InputObject $descriptorText
            foreach ($spec in $inputSpecs) {
                foreach ($field in @('role', 'target', 'path')) {
                    if ($null -eq $spec -or $null -eq $spec.PSObject.Properties[$field] -or
                        $spec.$field -isnot [string] -or [string]::IsNullOrWhiteSpace($spec.$field)) {
                        throw "each input needs a non-empty string '$field'"
                    }
                }
                $inputPath = if ([System.IO.Path]::IsPathRooted($spec.path)) { $spec.path } else { Join-Path $descriptorDir $spec.path }
                $e = Add-Blob $inputPath
                $key = ConvertTo-Json -InputObject @($spec.role, $spec.target, $e.file) -Compress
                if (-not $inputKeys.Add($key)) { throw "duplicate input role/target/file: $key" }
                $record = ConvertTo-Json -InputObject @($spec.role, $spec.target, $e.file, $e.sha256) -Compress
                $inputRecords.Add($record, [pscustomobject]@{
                    role = $spec.role; target = $spec.target; file = $e.file
                    sha256 = $e.sha256; bytes = $e.bytes; blob = $e.blob
                })
            }
            foreach ($record in $inputRecords.Keys) {
                $canonical += "input:$record`n"
                $inputEntries += $inputRecords[$record]
            }
        }
        $assetSha = Get-TextSha256 $canonical
        $referenceEntries = @()
        foreach ($r in $References) {
            $e = Add-Blob $r
            $kind = switch ([System.IO.Path]::GetExtension($r).ToLowerInvariant()) { '.blend' { 'blend' } '.json' { 'export-manifest' } default { 'reference' } }
            $referenceEntries += [pscustomobject]@{ kind = $kind; file = $e.file; sha256 = $e.sha256; bytes = $e.bytes; blob = $e.blob }
        }
        $manifest = [pscustomobject]@{
            assetSha = $assetSha; canonical = $canonical; fbx = $fbxEntry; textures = $textureEntries
            inputs = $inputEntries; references = $referenceEntries; registeredUtc = $now; name = $Name; note = $Note
        }
        $manifestPath = Join-Path $manifests ($assetSha + '.json')
        # the manifest of one content is immutable: a later registration of the same content (any name, any note) keeps it
        if (-not (Test-Path -LiteralPath $manifestPath)) { Write-Json $manifest $manifestPath }
        $assetPath = Join-Path $assetsDir ($Name + '.json')
        $history = @()
        if (Test-Path -LiteralPath $assetPath) {
            $old = Read-Json $assetPath
            $history = @($old.history)
            if ($old.assetSha -ne $assetSha) { $history += [pscustomobject]@{ assetSha = $old.assetSha; replacedUtc = $now } }
        }
        $asset = [pscustomobject]@{ name = $Name; assetSha = $assetSha; updatedUtc = $now; note = $Note; fbx = $fbxEntry.file; textures = @($textureEntries | ForEach-Object { $_.file }); history = $history }
        Write-Json $asset $assetPath
        Write-Output "$Name $assetSha"
    }
    'dataset' {
        if (-not $Name -or $Assets.Count -eq 0) { throw 'dataset needs -Name and -Assets' }
        $shas = @()
        foreach ($a in $Assets) {
            $p = Join-Path $assetsDir ($a + '.json')
            if (-not (Test-Path -LiteralPath $p)) { throw "asset '$a' is not registered" }
            $shas += (Read-Json $p).assetSha
        }
        $shas = @($shas | Sort-Object -Unique)
        $revision = Get-TextSha256 (($shas -join "`n") + "`n")
        $path = Join-Path $datasetsDir ($Name + '.json')
        $revisions = @()
        if (Test-Path -LiteralPath $path) { $revisions = @((Read-Json $path).revisions) }
        $existing = $revisions | Where-Object { $_.revision -eq $revision }
        if (-not $existing) { $revisions += [pscustomobject]@{ revision = $revision; createdUtc = $now; assetShas = $shas; assets = @($Assets) } }
        $ds = [pscustomobject]@{ name = $Name; revision = $revision; assetShas = $shas; assets = @($Assets); updatedUtc = $now; revisions = $revisions }
        Write-Json $ds $path
        Write-Output "$Name $revision"
    }
    'resolve' {
        $targets = @()
        if ($AssetSha) { $targets = @($AssetSha) }
        elseif ($Dataset) {
            $ds = Read-Json (Join-Path $datasetsDir ($Dataset + '.json'))
            $rev = if ($Revision) { $ds.revisions | Where-Object { $_.revision -eq $Revision } } else { $ds.revisions | Where-Object { $_.revision -eq $ds.revision } }
            if (-not $rev) { throw "revision not retained in dataset '$Dataset'" }
            $targets = @($rev.assetShas)
            Write-Output "dataset $Dataset revision $($rev.revision) ($($targets.Count) assets)"
        } else { throw 'resolve needs -AssetSha or -Dataset' }
        foreach ($sha in $targets) {
            $m = Read-Json (Join-Path $manifests ($sha + '.json'))
            Write-Output "asset $sha ($($m.name))"
            Write-Output "  fbx      $(Join-Path $blobs $m.fbx.blob)"
            foreach ($t in $m.textures) { Write-Output "  texture  $(Join-Path $blobs $t.blob)  ($($t.file))" }
            # Old manifests do not have an inputs property and remain readable without re-registration.
            if ($null -ne $m.PSObject.Properties['inputs']) {
                foreach ($i in $m.inputs) { Write-Output "  input    $(Join-Path $blobs $i.blob)  (role=$($i.role), target=$($i.target), file=$($i.file))" }
            }
            foreach ($r in $m.references) { Write-Output "  $($r.kind.PadRight(8)) $(Join-Path $blobs $r.blob)  ($($r.file))" }
        }
    }
    'verify' {
        $bad = 0
        foreach ($f in Get-ChildItem -LiteralPath $blobs -File) {
            $expected = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
            if ((Get-Sha256 $f.FullName) -ne $expected) { Write-Output "MISMATCH $($f.Name)"; $bad++ }
        }
        Write-Output "verified $((Get-ChildItem -LiteralPath $blobs -File).Count) blobs, $bad mismatches"
        if ($bad -gt 0) { exit 1 }
    }
}
