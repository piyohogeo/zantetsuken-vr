$ErrorActionPreference='Stop'
$product=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')).Replace('\','/')
$private="$(Split-Path -Parent $product)/zantetsuken-assets-private"
$project="$private/Working/D6L-54515f9/Project"
$sibling="$private/Working/D6L-54515f9/zantetsuken-assets-private"
$conflicts=@(Get-CimInstance Win32_Process -Filter "Name='Unity.exe' OR Name='AssetImportWorker.exe'" | Where-Object {
    !$_.CommandLine -or $_.CommandLine.Replace('\','/').Contains($project)
})
if($conflicts.Count){throw 'Target project open or process identity unavailable'}
if(!(Test-Path -LiteralPath "$project/Assets/Zantetsu")){throw 'Prepare candidate first'}
# Add only previously omitted inputs; do not overwrite the tested runtime or Unity-generated settings.
foreach($item in Get-ChildItem -LiteralPath "$product/Assets"){
    if($item.Name -in @('Zantetsu','Zantetsu.meta','Settings','Settings.meta','XR','XR.meta')){continue}
    $target="$project/Assets/$($item.Name)"
    if(Test-Path -LiteralPath $target){throw "Preserve existing input: $target"}
    Copy-Item -LiteralPath $item.FullName -Destination $target -Recurse
}
if(Test-Path -LiteralPath "$project/Tools/Phase02"){throw 'Preserve existing Phase02 inputs'}
New-Item -ItemType Directory -Path "$project/Tools" -Force | Out-Null
Copy-Item -LiteralPath "$product/Tools/Phase02" -Destination "$project/Tools/Phase02" -Recurse
# The unchanged legacy test resolves a sibling repository. Copy only its adopted dataset,
# never junction the whole private repository (which would create a recursive tree).
if(Test-Path -LiteralPath "$sibling/Working/Phase0.2/Adopted"){throw 'Preserve existing licensed adopted inputs'}
New-Item -ItemType Directory -Path "$sibling/Working/Phase0.2" -Force | Out-Null
Copy-Item -LiteralPath "$private/Working/Phase0.2/Adopted" -Destination "$sibling/Working/Phase0.2/Adopted" -Recurse
'Completed scene, public fixture, and licensed fixture inputs inside private Working only.'
