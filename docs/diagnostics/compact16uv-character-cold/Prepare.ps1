$ErrorActionPreference='Stop'
$product=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')).Replace('\','/')
$private="$(Split-Path -Parent $product)/zantetsuken-assets-private"
$project="$private/Working/D6C-2ae6c9f/Project"
$sibling="$private/Working/D6C-2ae6c9f/zantetsuken-assets-private"
if(Test-Path -LiteralPath $project){throw 'Preserve existing diagnostic candidate'}
New-Item -ItemType Directory -Path $project | Out-Null
foreach($folder in @('Assets','Packages','ProjectSettings')){Copy-Item -LiteralPath "$product/$folder" -Destination "$project/$folder" -Recurse}
New-Item -ItemType Directory -Path "$project/Tools" | Out-Null
Copy-Item -LiteralPath "$product/Tools/Phase02" -Destination "$project/Tools/Phase02" -Recurse
New-Item -ItemType Directory -Path "$sibling/Working/Phase0.2" -Force | Out-Null
Copy-Item -LiteralPath "$private/Working/Phase0.2/Adopted" -Destination "$sibling/Working/Phase0.2/Adopted" -Recurse
'Copied current product candidate and regression fixtures inside private Working. No old runtime overlay.'
