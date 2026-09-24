param([string]$BenchmarkRoot='C:/Users/junic/src/zantetsuken-mesh-vp-transform-benchmark',
      [string]$PipelineRoot='C:/Users/junic/src/zantetsuken-blender-pipeline-pilot')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$private=Join-Path $root 'Assets/Licensed/Compact16uvIntake'
if(Test-Path -LiteralPath $private){ throw 'Use a fresh intake directory; existing private inputs are not overwritten.' }
$old=Get-Content "$BenchmarkRoot/Artifacts/Compact16uvFollowup/u0-20260923/source-manifest.json" -Raw | ConvertFrom-Json
$snap=Get-Content "$BenchmarkRoot/Artifacts/Compact16uvFollowup/u0-20260923/snapshot-diff.json" -Raw | ConvertFrom-Json
$maps=Get-Content "$BenchmarkRoot/Artifacts/Compact16uvFollowup/u3-topology/unity-topology-map.json" -Raw | ConvertFrom-Json
$texture="$PipelineRoot/Generated/PaletteCompatibility/Textures1_bottom_256.png"
if((Get-FileHash $texture).Hash.ToLowerInvariant() -ne $old.texture.sha256){throw 'Current palette differs from verified snapshot'}
$records=@()
foreach($family in $old.families){
    $entry=$family.files | Where-Object asset_id -eq $family.representative_asset_id
    $source=$entry.source_absolute_path
    if((Get-FileHash $source).Hash.ToLowerInvariant() -ne $entry.sha256){throw "Current source changed: $($entry.asset_id)"}
    $dest=Join-Path $private $family.family
    New-Item -ItemType Directory -Path "$dest/textures" -Force | Out-Null
    $assetPath="Assets/Licensed/Compact16uvIntake/$($family.family)/$($entry.asset_id).blend"
    Copy-Item -LiteralPath $source -Destination (Join-Path $root $assetPath)
    $meta="$BenchmarkRoot/$($family.representative_relative_asset).meta"
    Copy-Item -LiteralPath $meta -Destination ((Join-Path $root $assetPath)+'.meta')
    Copy-Item -LiteralPath $texture -Destination "$dest/textures/Textures1_bottom_256.png"
    Copy-Item -LiteralPath "$BenchmarkRoot/Assets/BenchmarkData/Compact16uvFollowup/u0-20260923/representatives/$($family.family)/textures/Textures1_bottom_256.png.meta" -Destination "$dest/textures/Textures1_bottom_256.png.meta"
    $snapshot=$snap.assets | Where-Object assetId -eq $entry.asset_id
    $map=$maps.results | Where-Object {$_.assetPath -like "*/$($entry.asset_id).blend"}
    if(@($snapshot).Count -ne 1 -or @($map).Count -ne 1 -or !$map.passed){throw 'Missing/ambiguous reference snapshot/topology'}
    $records += [ordered]@{family=$family.family; assetPath=$assetPath; sourceSha256=$entry.sha256;
        importerSha256=(Get-FileHash $meta).Hash.ToLowerInvariant(); textureSha256=$old.texture.sha256;
        currentPositionHash=$snapshot.currentPositionHash; currentNormalHash=$snapshot.currentNormalHash;
        currentUvHash=$snapshot.currentUvHash; currentIndexTopologyHash=$snapshot.currentIndexTopologyHash;
        currentWeightHash=$snapshot.currentWeightHash; objectName=$map.objectName;
        topologyCount=$map.sourceVertexCount; topologyMap=$map.topologyMap }
}
$manifest=[ordered]@{schemaVersion=1; baselineProductCommit='c083815b';
    topologyEvidenceSha256=(Get-FileHash "$BenchmarkRoot/Artifacts/Compact16uvFollowup/u3-topology/unity-topology-map.json").Hash.ToLowerInvariant();
    snapshotEvidenceSha256=(Get-FileHash "$BenchmarkRoot/Artifacts/Compact16uvFollowup/u0-20260923/snapshot-diff.json").Hash.ToLowerInvariant();
    assets=$records}
[IO.File]::WriteAllText("$private/intake.json",($manifest | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
Write-Output "Staged $($records.Count) hash-verified representatives under ignored Assets/Licensed; original source files are unchanged."
