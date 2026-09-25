param([Parameter(Mandatory=$true)][ValidatePattern('^(focused|full|physics)-[0-9]+$')][string]$RunName)
$ErrorActionPreference='Stop'
$product=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')).Replace('\','/')
$private="$(Split-Path -Parent $product)/zantetsuken-assets-private"
$project="$private/Working/D6L-54515f9/Project"
$output="$product/Logs/PreparedClassificationMigration/$RunName"
if(Test-Path -LiteralPath $output){throw 'Preserve existing run evidence'}
if(!(Test-Path -LiteralPath "$project/Assets/Zantetsu")){throw 'Prepare project first'}
$conflicts=@(Get-CimInstance Win32_Process -Filter "Name='Unity.exe' OR Name='AssetImportWorker.exe'" | Where-Object {
    !$_.CommandLine -or $_.CommandLine.Replace('\','/').Contains($project)
})
if($conflicts.Count){throw 'Target project open or process identity unavailable'}
New-Item -ItemType Directory -Path "$output/source" -Force | Out-Null
foreach($folder in @('Runtime','Tests')){
    if(@(Get-ChildItem -LiteralPath "$product/Assets/Zantetsu/$folder" -Recurse -File).Count -ne
       @(Get-ChildItem -LiteralPath "$project/Assets/Zantetsu/$folder" -Recurse -File).Count){throw "Extra or missing candidate file in $folder"}
    foreach($file in Get-ChildItem -LiteralPath "$product/Assets/Zantetsu/$folder" -Recurse -File){
        $rel=$file.FullName.Substring($product.Length+1)
        if((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath "$project/$rel").Hash){throw "Candidate mismatch $rel"}
    }
}
$sources=@('Runtime/PhysicsCut/ProvisionalCutDriver.cs','Runtime/PhysicsCut/PreparedCutLease.cs','Runtime/PhysicsCut/VpPreparedPhysicsInput.cs',
    'Tests/EditMode/PhysicsCut/ProvisionalCutDriverTests.cs','Tests/EditMode/PhysicsCut/VpPreparedPhysicsInputTests.cs',
    'Tests/EditMode/PhysicsCut/PreparedCutLeaseTests.cs','Tests/EditMode/PhysicsCut/PreparedInputRearmTests.cs')
foreach($rel in $sources){Copy-Item -LiteralPath "$project/Assets/Zantetsu/$rel" -Destination "$output/source/$([IO.Path]::GetFileName($rel))"}
$filter=if($RunName.StartsWith('focused')){' -testFilter D6_'}elseif($RunName.StartsWith('physics')){' -testFilter Zantetsu.PhysicsCut.Tests'}else{''}
$argsText='-batchmode -projectPath "'+$project+'" -runTests -testPlatform EditMode'+$filter+' -testResults "'+$output+'/results.xml" -logFile "'+$output+'/editor.log"'
$process=Start-Process "$env:ProgramFiles/Unity/Hub/Editor/6000.3.22f1/Editor/Unity.exe" -ArgumentList $argsText -WindowStyle Hidden -PassThru
"Unity PID: $($process.Id)"
$process.WaitForExit();"Unity exit: $($process.ExitCode)"
if($process.ExitCode -ne 0){exit $process.ExitCode}
[xml]$xml=Get-Content -Raw -LiteralPath "$output/results.xml"
$result=$xml.'test-run'
"$($result.passed)/$($result.total) passed, failed=$($result.failed), skipped=$($result.skipped)"
if($result.failed -ne '0' -or $result.skipped -ne '0' -or $result.inconclusive -ne '0'){throw 'Test failure or incomplete evidence'}
