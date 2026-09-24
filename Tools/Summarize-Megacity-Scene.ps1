param([Parameter(Mandatory=$true)][string]$Player)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$root=Join-Path $repo 'docs/diagnostics/compact16uv-megacity-scene'
$utf8=New-Object System.Text.UTF8Encoding($false)
function Normalized($text) { return [regex]::Replace($text.Replace($env:USERPROFILE,'[USERPROFILE]').Replace($env:USERPROFILE.Replace('\','/'),'[USERPROFILE]').Replace($env:COMPUTERNAME,'[COMPUTER]').Replace("`r",''),'[ \t]+(?=\n|$)','') }
$build=Get-Content -Raw -Encoding UTF8 (Join-Path $root 'build-building.log')
if($build -notmatch 'PLAYER BUILD RESULT: result=Succeeded' -or $build -notmatch 'bytes errors=0') {throw 'Final Player build failed'}
$buildLines=($build -split "`n" | Where-Object {$_ -match '^(PLAYER BUILD |Compact16uv scene:|Compact16uv mono diagnostic build:|SCENE AB BUILD:)'}) -join "`n"
[IO.File]::WriteAllText((Join-Path $root 'build-trace.txt'),(Normalized $buildLines)+"`n",$utf8)
$rows=@(); $images=@()
foreach($number in 1..3) {
    $name="accepted$number"
    $log=Get-Content -Raw -Encoding UTF8 (Join-Path $root "$name.log")
    foreach($required in @('AUTHORED MEGACITY:','hullVertices=24 hullFaces=44 displayVertices=5326 stride=16','anchors=disabled','the cut committed.','the child''s cut committed.','IsReleased=True IsDrained=True displayDisposed=True','atlas bound after shutdown=False','finished with code 0')) {
        if(!$log.Contains($required)) {throw "Missing acceptance marker $name : $required"}
    }
    if($log -match 'FAILED:|Exception:|geometryFaults=[1-9]|broken=True|halted=True') {throw "Fault in $name"}
    $samples=@([regex]::Matches($log,'SANDBOX PLAYER: scene sample ([^:]+): frames=240 drawnFrames=(\d+) mainThreadMedianMs=([\d.]+) mainThreadP95Ms=([\d.]+) unityAllocatedBytes=(\d+) unityReservedBytes=(\d+) processWorkingSetBytes=(\d+) storageVertices=(\d+) vertexCapacityBytes=(\d+)') | ForEach-Object {
        if([int]$_.Groups[2].Value -ne 240) {throw "Incomplete draw window $name"}
        [ordered]@{phase=$_.Groups[1].Value;drawnFrames=[int]$_.Groups[2].Value;inclusiveMainMedianMs=[double]$_.Groups[3].Value;inclusiveMainP95Ms=[double]$_.Groups[4].Value;unityAllocatedBytes=[long]$_.Groups[5].Value;unityReservedBytes=[long]$_.Groups[6].Value;processWorkingSetBytes=[long]$_.Groups[7].Value;storageVertices=[int]$_.Groups[8].Value;vertexCapacityBytes=[long]$_.Groups[9].Value}
    })
    if($samples.Count -ne 2 -or $samples[0].storageVertices -ne 5326 -or $samples[1].storageVertices -le 5326) {throw "Unexpected geometry/windows in $name"}
    if($number -eq 1) {$expectedVertices=$samples[1].storageVertices} elseif($samples[1].storageVertices -ne $expectedVertices) {throw 'Repeated geometry count differs'}
    $provisional=[regex]::Match($log,'frames in which a provisional pair stood: (\d+)')
    if(!$provisional.Success -or [int]$provisional.Groups[1].Value -le 0) {throw "No observed provisional pair $name"}
    $lines=($log -split "`n" | Where-Object {$_ -match '^(SANDBOX PLAYER:|AUTHORED MEGACITY:)'} ) -join "`n"
    [IO.File]::WriteAllText((Join-Path $root "$name/trace.txt"),(Normalized $lines)+"`n",$utf8)
    $rows += [ordered]@{name=$name;provisionalObservedFrames=[int]$provisional.Groups[1].Value;samples=$samples}
    foreach($shot in @('01-before-the-cut','02-after-the-cut','03-children-apart','03b-children-apart-debug','04-after-the-child-cut','04b-after-recut-debug','05-after-the-ending')) {
        $path=Join-Path $root "$name/$shot.png"
        if(!(Test-Path $path)) {throw "Missing $path"}
        if($number -gt 1) {
            $comparison=(& 'C:/Python38/python.exe' (Join-Path $repo 'Tools/Compare-SceneAb-Pixels.py') (Join-Path $root "accepted1/$shot.png") $path | ConvertFrom-Json)
            if($LASTEXITCODE -ne 0 -or $comparison.changedPixels -ne 0) {throw "Repeated image differs: $name/$shot"}
            $images += [ordered]@{name="$name/$shot";comparison=$comparison}
        }
    }
}
$xmlPath=Join-Path $root 'editmode.xml'
[IO.File]::WriteAllText($xmlPath,(Normalized ([IO.File]::ReadAllText($xmlPath))),$utf8)
[xml]$xml=Get-Content -Raw -Encoding UTF8 $xmlPath
$test=$xml.'test-run'
if($test.result -ne 'Passed' -or [int]$test.failed -ne 0 -or [int]$test.skipped -ne 0 -or [int]$test.inconclusive -ne 0) {throw 'Regression failed/skipped'}
$hashes=@()
$files=@('Assets/Zantetsu/Runtime/Sandbox/SandboxCutWorldProbe.cs','Assets/Zantetsu/Runtime/Sandbox/SandboxPlayerCheck.cs','Assets/Zantetsu/Editor/Sandbox/Compact16uvSandboxSceneBuild.cs','Tools/Summarize-Megacity-Scene.ps1','DESIGN.md','docs/diagnostics/compact16uv-megacity-scene/README.md')
$files += @('Assets/Zantetsu/Settings/CutWorldSandboxProfile.asset','Assets/Licensed/Compact16uvIntake/SceneIntegration/Compact16uvMegacity.unity','Assets/Licensed/Compact16uvIntake/Resources/Static16Migration/Megacity.bytes','Assets/Licensed/Compact16uvIntake/Resources/AuthoredPhysicsMigration/Megacity.json')
foreach($slot in 0..2) {$files += "Assets/Licensed/Compact16uvIntake/SceneIntegration/MegacityForward$slot.mat"}
foreach($file in $files) {$hashes += [ordered]@{path=$file;sha256=(Get-FileHash (Join-Path $repo $file)).Hash.ToLowerInvariant()}}
foreach($file in @('CutWorldSandbox.exe','GameAssembly.dll','CutWorldSandbox_Data/globalgamemanagers')) {$hashes += [ordered]@{path="player/$file";sha256=(Get-FileHash (Join-Path $Player $file)).Hash.ToLowerInvariant()}}
Get-ChildItem -LiteralPath $root -File -Recurse | Where-Object {$_.Extension -in @('.png','.xml','.txt')} | ForEach-Object {$hashes += [ordered]@{path=$_.FullName.Substring($root.Length+1).Replace('\','/');sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}}
$report=[ordered]@{baselineCommit='2db5cc06';passed=$true;rows=$rows;repeatImages=$images;editmodePassed=[int]$test.passed;hashes=$hashes;scope='Actual Megacity ordinary scene, fixed diagnostic placement and post-commit poses, anchors disabled; no Legacy32 comparison, XR, current-pose character or peak claim'}
[IO.File]::WriteAllText((Join-Path $root 'summary.json'),($report|ConvertTo-Json -Depth 10).Replace("`r`n","`n")+"`n",$utf8)
