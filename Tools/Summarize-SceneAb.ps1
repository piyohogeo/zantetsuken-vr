param([Parameter(Mandatory=$true)][string]$Player16, [Parameter(Mandatory=$true)][string]$Player32)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$root=Join-Path $repo 'docs/diagnostics/compact16uv-scene-ab'
$utf8=New-Object System.Text.UTF8Encoding($false)
Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {$_.Extension -in @('.log','.xml')} | ForEach-Object {
    $value=[IO.File]::ReadAllText($_.FullName).Replace($env:USERPROFILE,'[USERPROFILE]').Replace($env:USERPROFILE.Replace('\','/'),'[USERPROFILE]')
    [IO.File]::WriteAllText($_.FullName,$value,$utf8)
}
function Median($values) { $s=@($values|Sort-Object); return $s[[int][Math]::Floor($s.Count/2)] }
function Percentile95($values) { $s=@($values|Sort-Object); return $s[[int][Math]::Ceiling(.95*$s.Count)-1] }
function ScopeValues($samples) { return @($samples | ForEach-Object {$_.driverUpdateUs+$_.driverLateUs+$_.cameraSubmissionUs}) }
function HashEntry($path,$name) { return [pscustomobject]@{path=$name; bytes=(Get-Item -LiteralPath $path).Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()} }
$rows=@(); $reference=$null; $pictures=@()
$tests=@()
foreach($name in @('editmode','rendering')) {
    [xml]$xml=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root "$name.xml")
    $test=$xml.'test-run'
    if($test.result -ne 'Passed' -or [int]$test.failed -ne 0 -or [int]$test.skipped -ne 0 -or [int]$test.inconclusive -ne 0) {throw "Regression $name failed or skipped"}
    $tests += [pscustomobject]@{name=$name;passed=[int]$test.passed;failed=[int]$test.failed;skipped=[int]$test.skipped}
}
foreach($stride in @(16,32)) { foreach($number in 1..3) {
    $name="accepted$stride-$number"
    $data=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root "$name/scene-ab.json") | ConvertFrom-Json
    $log=Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $root "$name.log")
    if(!$data.passed -or $data.stride -ne $stride -or $data.shaderLegacy -ne ($stride -eq 32) -or $data.vertexCapacity -ne 524288 -or $data.indexCapacity -ne 2097152 -or $log -notmatch 'finished with code 0' -or $log -match 'FAILED:|Exception:' -or $log -notmatch 'IsReleased=True IsDrained=True displayDisposed=True') { throw "Failed run $name" }
    $signature=$data.checkpoints | ConvertTo-Json -Compress
    if($null -eq $reference) {$reference=$signature} elseif($reference -ne $signature) {throw "Geometry mismatch $name"}
    $before=@($data.samples|Where-Object phase -eq 0|Select-Object -Last 240)
    $after=@($data.samples|Where-Object phase -eq 4|Select-Object -Last 240)
    if($before.Count -ne 240 -or $after.Count -ne 240 -or $before[-1].drawnFrames-$before[0].drawnFrames -ne 239 -or $after[-1].drawnFrames-$after[0].drawnFrames -ne 239) {throw "Missing rendered steady frames $name"}
    $cut=@($data.samples|Where-Object {$_.phase -in @(1,2)})
    $recut=@($data.samples|Where-Object phase -eq 3)+@($data.samples|Where-Object phase -eq 4|Select-Object -First 1)
    $cutValues=ScopeValues $cut; $recutValues=ScopeValues $recut
    $rows += [pscustomobject][ordered]@{
        name=$name; stride=$stride; frames=$data.samples.Count; frameGcRecorderValid=$data.frameGcRecorderValid
        beforeProductMedianUs=(Median (ScopeValues $before)); beforeProductP95Us=(Percentile95 (ScopeValues $before))
        afterProductMedianUs=(Median (ScopeValues $after)); afterProductP95Us=(Percentile95 (ScopeValues $after))
        beforeMainMedianMs=(Median $before.previousFrameMainMs); afterMainMedianMs=(Median $after.previousFrameMainMs)
        cutFrames=$cut.Count; cutProductSumUs=($cutValues|Measure-Object -Sum).Sum; cutProductPeakUs=($cutValues|Measure-Object -Maximum).Maximum
        recutFrames=$recut.Count; recutProductSumUs=($recutValues|Measure-Object -Sum).Sum; recutProductPeakUs=($recutValues|Measure-Object -Maximum).Maximum
        beforeUnityAllocatedBytes=(Median $before.unityAllocated); afterUnityAllocatedBytes=(Median $after.unityAllocated)
        observedUnityAllocatedPeakBytes=($data.samples.unityAllocated|Measure-Object -Maximum).Maximum
        observedUnityReservedPeakBytes=($data.samples.unityReserved|Measure-Object -Maximum).Maximum
        beforeWorkingSetBytes=(Median $before.workingSet); afterWorkingSetBytes=(Median $after.workingSet)
        lifetimeWorkingSetPeakBytes=($data.samples.lifetimePeakWorkingSet|Measure-Object -Maximum).Maximum
        wholeFrameGcBytes=($data.samples.previousFrameGcAllocatedBytes|Measure-Object -Sum).Sum
        scopedManagedAllocation='unavailable (-1), not zero'
        cpuVertexCapacityBytes=524288L*$stride; logicalGpuVertexCapacityBytes=524288L*$stride
    }
} }
foreach($number in 1..3) {
    $left=Join-Path $root "accepted16-$number/04-after-the-child-cut.png"
    $right=Join-Path $root "accepted32-$number/04-after-the-child-cut.png"
    $comparison=(& python (Join-Path $PSScriptRoot 'Compare-SceneAb-Pixels.py') $left $right) | ConvertFrom-Json
    if($LASTEXITCODE -ne 0 -or $comparison.changedPixels -ne 0) {throw "Final images differ, pair $number; investigate before accepting"}
    $pictures += [pscustomobject]@{pair=$number; comparison=$comparison}
}
$hashes=@()
$hashes+=HashEntry 'C:/Program Files/Unity/Hub/Editor/6000.3.22f1/Editor/Data/il2cpp/libil2cpp/icalls/mscorlib/System/GC.cpp' 'installed-il2cpp/icalls/mscorlib/System/GC.cpp'
foreach($stride in @(16,32)) {
    $player=if($stride -eq 16){$Player16}else{$Player32}
    foreach($file in @('CutWorldSandbox.exe','GameAssembly.dll','CutWorldSandbox_Data/globalgamemanagers')) {$hashes+=HashEntry (Join-Path $player $file) "player$stride/$file"}
}
$changed=@(git -c "safe.directory=$($repo.Replace('\','/'))" -C $repo diff --name-only 30c63733 -- Assets/Zantetsu)
$untracked=@(git -c "safe.directory=$($repo.Replace('\','/'))" -C $repo ls-files --others --exclude-standard -- Assets/Zantetsu)
foreach($file in @($changed+$untracked|Sort-Object -Unique)) {$hashes+=HashEntry (Join-Path $repo $file) $file}
foreach($file in @('Tools/Summarize-SceneAb.ps1','Tools/Compare-SceneAb-Pixels.py','DESIGN.md','docs/diagnostics/compact16uv-scene-ab/README.md')) {$hashes+=HashEntry (Join-Path $repo $file) $file}
Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {$_.Name -notin @('summary.json','README.md','.gitattributes')} | ForEach-Object {$hashes+=HashEntry $_.FullName $_.FullName.Substring($root.Length+1).Replace('\','/')}
$result=[ordered]@{
    baselineCommit='30c63733'; passed=$true; geometrySignature=($reference|ConvertFrom-Json); rows=$rows; pictures=$pictures; regressionTests=$tests; hashes=$hashes
    exclusions=@('smoke16: initial counter API unsupported; unfixed grandchild pose','run32-1: geometry matched, but post-recut pose not pinned','build32.log: Player-only ABI symbol failed Unity type-layout check')
    interpretation='Dense synthetic product path; scoped Main wall time, previous-frame inclusive Main and GC; sampled Unity peaks and OS lifetime working-set high-water; no GPU residency or pool growth claim'
}
[IO.File]::WriteAllText((Join-Path $root 'summary.json'),($result|ConvertTo-Json -Depth 14)+[Environment]::NewLine,$utf8)
$rows|Format-Table name,beforeProductMedianUs,afterProductMedianUs,cutProductSumUs,recutProductSumUs,observedUnityAllocatedPeakBytes,lifetimeWorkingSetPeakBytes
