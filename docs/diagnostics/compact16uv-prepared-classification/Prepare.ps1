$ErrorActionPreference='Stop'
$product=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')).Replace('\','/')
$private="$(Split-Path -Parent $product)/zantetsuken-assets-private"
$project="$private/Working/D6L-54515f9/Project"
if(Test-Path -LiteralPath $project){throw 'Preserve existing diagnostic project'}
New-Item -ItemType Directory -Path "$project/Assets" | Out-Null
foreach($folder in @('Zantetsu','Settings','XR')){
    Copy-Item -LiteralPath "$product/Assets/$folder" -Destination "$project/Assets/$folder" -Recurse
    Copy-Item -LiteralPath "$product/Assets/$folder.meta" -Destination "$project/Assets/$folder.meta"
}
foreach($folder in @('Packages','ProjectSettings')){Copy-Item -LiteralPath "$product/$folder" -Destination "$project/$folder" -Recurse}
& "$PSScriptRoot/Complete-Fixtures.ps1"
'Copied product candidate and regression fixtures; no prior runtime overlays.'
