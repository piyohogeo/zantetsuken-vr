$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$root=Join-Path $repo 'docs/diagnostics/compact16uv-physics-intake'
$utf8=New-Object System.Text.UTF8Encoding($false)
$tests=@()
foreach($name in @('focused','editmode')) {
    $path=Join-Path $root "$name.xml"
    $text=[IO.File]::ReadAllText($path).Replace($env:USERPROFILE,'[USERPROFILE]').Replace($env:USERPROFILE.Replace('\','/'),'[USERPROFILE]').Replace($env:COMPUTERNAME,'[COMPUTER]')
    $text=[regex]::Replace($text.Replace("`r`n","`n"),'[ \t]+(?=\n|$)','')
    [IO.File]::WriteAllText($path,$text,$utf8)
    [xml]$xml=$text
    $run=$xml.'test-run'
    if($run.result -ne 'Passed' -or [int]$run.failed -ne 0 -or [int]$run.skipped -ne 0 -or [int]$run.inconclusive -ne 0) {throw "Failed or skipped: $name"}
    $actual=$xml.SelectNodes('//test-case') | Where-Object {$_.fullname -like '*Compact16uvAuthoredPhysicsTests*'}
    if(@($actual).Count -ne 1 -or $actual.result -ne 'Passed') {throw 'Missing actual-asset test'}
    $tests += [ordered]@{name=$name;passed=[int]$run.passed;failed=[int]$run.failed;skipped=[int]$run.skipped;actualAssetOutput=$actual.SelectSingleNode('output').InnerText}
}
& 'C:/Python38/python.exe' -B (Join-Path $repo 'Tools/Test-Compact16uv-Physics.py')
if($LASTEXITCODE -ne 0) {throw 'Python controls failed'}
$offlinePath=Join-Path $root 'offline.json'
[IO.File]::WriteAllText($offlinePath,[IO.File]::ReadAllText($offlinePath).Replace("`r`n","`n"),$utf8)
$offline=Get-Content -Raw -Encoding UTF8 $offlinePath | ConvertFrom-Json
if(!$offline.passed) {throw 'Offline gate failed'}
$scriptHash=(Get-FileHash (Join-Path $repo 'Tools/Export-Compact16uv-Physics.py')).Hash.ToLowerInvariant()
if($scriptHash -ne $offline.scriptSha256) {throw 'Exporter changed after fixture generation'}
$hashes=@()
$files=@('Tools/Export-Compact16uv-Physics.py','Tools/Test-Compact16uv-Physics.py','Tools/Summarize-Compact16uv-Physics.ps1','Assets/Zantetsu/Tests/EditMode/PhysicsCut/Compact16uvAuthoredPhysicsTests.cs','DESIGN.md')
$files += @('README.md','offline.json','focused.xml','editmode.xml') | ForEach-Object {"docs/diagnostics/compact16uv-physics-intake/$_"}
foreach($file in $files) {$hashes += [ordered]@{path=$file;sha256=(Get-FileHash (Join-Path $repo $file)).Hash.ToLowerInvariant()}}
$result=[ordered]@{baselineCommit='deafb0aa';passed=$true;pythonControlsPassed=7;tests=$tests;hashes=$hashes;scope='Actual authored physics + Static16 offline binding and component tests only; no product-scene integration/performance claim';privateFixtureSha256=$offline.fixtureSha256}
[IO.File]::WriteAllText((Join-Path $root 'summary.json'),($result|ConvertTo-Json -Depth 8).Replace("`r`n","`n")+"`n",$utf8)
