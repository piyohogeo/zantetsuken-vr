param([Parameter(Mandatory=$true)][ValidatePattern('^(focused|full|play|related)-[0-9]+$')][string]$RunName)
$ErrorActionPreference='Stop'
$product=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')).Replace('\','/')
$private="$(Split-Path -Parent $product)/zantetsuken-assets-private"
$project="$private/Working/D6C-2ae6c9f/Project"
$output="$product/Logs/CharacterColdMigration/$RunName"
if(Test-Path -LiteralPath $output){throw 'Preserve existing run evidence'}
$conflicts=@(Get-CimInstance Win32_Process -Filter "Name='Unity.exe' OR Name='AssetImportWorker.exe'" | Where-Object {
    !$_.CommandLine -or $_.CommandLine.Replace('\','/').Contains($project.Replace('\','/'))
})
if($conflicts.Count){throw 'Target project open or process identity unavailable'}
foreach($folder in @('Runtime','Tests')){
    if(@(Get-ChildItem -LiteralPath "$product/Assets/Zantetsu/$folder" -Recurse -File).Count -ne
       @(Get-ChildItem -LiteralPath "$project/Assets/Zantetsu/$folder" -Recurse -File).Count){throw "Extra or missing candidate file in $folder"}
    foreach($file in Get-ChildItem -LiteralPath "$product/Assets/Zantetsu/$folder" -Recurse -File){
        $rel=$file.FullName.Substring($product.Length+1)
        if((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath "$project/$rel").Hash){throw "Candidate mismatch $rel"}
    }
}
New-Item -ItemType Directory -Path "$output/source" -Force | Out-Null
$sources=@('Runtime/PhysicsCut/CutWorldRoot.cs','Runtime/PhysicsCut/VpPreparedCharacterCut.cs',
    'Tests/EditMode/PhysicsCut/PreparedCharacterColdTests.cs','Tests/PlayMode/ProvisionalMassFlagActivationPlayModeTests.cs',
    'Tests/PlayMode/PreparedCharacterColdPlayModeTests.cs')
foreach($rel in $sources){Copy-Item -LiteralPath "$project/Assets/Zantetsu/$rel" -Destination "$output/source/$([IO.Path]::GetFileName($rel))"}
$platform=if($RunName.StartsWith('play') -or $RunName.StartsWith('related')){'PlayMode'}else{'EditMode'}
$filter=if($RunName.StartsWith('full')){''}elseif($RunName.StartsWith('related')){' -testFilter Zantetsu.PhysicsCut.PlayModeTests.ProvisionalMassFlagActivationPlayModeTests'}else{' -testFilter D6H_'}
$arguments='-batchmode -projectPath "'+$project+'" -runTests -testPlatform '+$platform+$filter+' -testResults "'+$output+'/results.xml" -logFile "'+$output+'/editor.log"'
$process=Start-Process "$env:ProgramFiles/Unity/Hub/Editor/6000.3.22f1/Editor/Unity.exe" -ArgumentList $arguments -WindowStyle Hidden -PassThru
"Unity PID: $($process.Id)"; $process.WaitForExit(); "Unity exit: $($process.ExitCode)"
if($process.ExitCode -ne 0){exit $process.ExitCode}
[xml]$xml=Get-Content -Raw -LiteralPath "$output/results.xml"; $result=$xml.'test-run'
"$($result.passed)/$($result.total) passed, failed=$($result.failed), skipped=$($result.skipped)"
if($result.result -ne 'Passed' -or $result.failed -ne '0' -or $result.skipped -ne '0' -or $result.inconclusive -ne '0'){throw 'Test failure or incomplete evidence'}
