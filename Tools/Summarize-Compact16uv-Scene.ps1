$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$directory=Join-Path $root 'docs/diagnostics/compact16uv-scene'
$summaryPath=Join-Path $directory 'summary.json'
$old=@{}
if(Test-Path $summaryPath){foreach($item in ((Get-Content $summaryPath -Raw | ConvertFrom-Json).evidence)){$old[$item.file]=$item}}
$evidence=@();$runs=@();$measurements=@()
foreach($file in (Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object {$_.Extension -in @('.log','.xml')})){
    $relative=$file.FullName.Substring($directory.Length+1).Replace('\','/')
    $hash=(Get-FileHash $file.FullName).Hash.ToLowerInvariant()
    $before=if($old[$relative] -and $old[$relative].sha256 -eq $hash){$old[$relative].beforeAnonymizationSha256}else{$hash}
    $content=[IO.File]::ReadAllText($file.FullName).Replace($env:USERPROFILE,'C:\Users\%USERNAME%').Replace($env:USERPROFILE.Replace('\','/'),'C:/Users/%USERNAME%')
    [IO.File]::WriteAllText($file.FullName,$content,[Text.UTF8Encoding]::new($false))
    $evidence += [ordered]@{file=$relative;sha256=(Get-FileHash $file.FullName).Hash.ToLowerInvariant();beforeAnonymizationSha256=$before}
    if($file.Extension -eq '.xml'){
        [xml]$xml=$content;$run=$xml.'test-run'
        $runs += [ordered]@{name=$relative;total=[int]$run.total;passed=[int]$run.passed;failed=[int]$run.failed;skipped=[int]$run.skipped;inconclusive=[int]$run.inconclusive;
            failures=@($xml.SelectNodes('//test-case[@result="Failed"]') | ForEach-Object{$_.fullname})}
    }
    if($file.Extension -eq '.log'){
        $measurements+=@($content -split "`n" | Where-Object {$_ -match '^SANDBOX PLAYER: (scene sample|atlas bound|finished with code|the ending:)'} | ForEach-Object{[ordered]@{run=$relative;line=$_.Trim()}})
    }
}
foreach($file in (Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object {$_.Extension -notin @('.log','.xml') -and $_.Name -notin @('summary.json','.gitattributes')})){
    $evidence += [ordered]@{file=$file.FullName.Substring($directory.Length+1).Replace('\','/');sha256=(Get-FileHash $file.FullName).Hash.ToLowerInvariant()}
}
$changed=@(& git -C $root diff 737adcfa --name-only -- Assets/Zantetsu DESIGN.md Tools/Summarize-Compact16uv-Scene.ps1)
$changed+=@(& git -C $root ls-files --others --exclude-standard -- Assets/Zantetsu Tools/Summarize-Compact16uv-Scene.ps1)
$sources=@($changed | Sort-Object -Unique | ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash (Join-Path $root $_)).Hash.ToLowerInvariant()}})
$private=Join-Path $root 'Assets/Licensed/Compact16uvIntake/SceneIntegration'
$inputs=@(Get-ChildItem -LiteralPath $private -File | ForEach-Object{[ordered]@{file=$_.Name;sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}})
$accepted=@()
foreach($name in @('run7-visible.log','run8-visible.log','run9-visible.log')){
    $path=Join-Path $directory $name
    if(Test-Path $path){
        $log=[IO.File]::ReadAllText($path)
        if($log -match 'SANDBOX PLAYER: finished with code 0' -and $log -notmatch 'SANDBOX PLAYER: FAILED' -and
            [regex]::Matches($log,'frames=240 drawnFrames=240').Count -eq 2 -and
            $log -match 'IsReleased=True IsDrained=True displayDisposed=True' -and
            $log -match 'atlas bound after shutdown=False'){$accepted+=$name}
    }
}
$binaryRoot=Join-Path $env:LOCALAPPDATA 'Zantetsu/TestPlayers/SceneCompact16uv20260924NoXR'
$binaryHashes=@('CutWorldSandbox.exe','GameAssembly.dll','CutWorldSandbox_Data/globalgamemanagers' | ForEach-Object {
    $path=Join-Path $binaryRoot $_
    if(Test-Path $path){[ordered]@{file=$_;sha256=(Get-FileHash $path).Hash.ToLowerInvariant()}}
})
$summary=[ordered]@{schemaVersion=1;baselineCommit='737adcfa';profile='Compact16uv-ProductSandboxScene';tests=$runs;playerObservations=$measurements;evidence=$evidence;sources=$sources;privateSceneHashes=$inputs;
    excludedPlayerRuns=@{ 'run1.log'='IL2CPP unsupported Process API; coroutine aborted, task process stopped.'; 'run2.log'='Exit 0 but blank captures with idle XR; rendering/performance acceptance refused.'; 'run3.log'='XR stopped but hidden-window draws remained zero; blank-image guard returned exit 8.'; 'run4.log'='Offscreen camera target did not restore drawing; exit 8.'; 'run5.log'='Graphics-enabled batchmode also had zero draws and blank captures; exit 8.'; 'run6.log'='XR disabled before startup still had zero draws and blank captures; exit 8.' };
    acceptedVisiblePlayerRuns=$accepted;validatedBinarySourceCommit='2dd216fc';diagnosticBinaryHashes=$binaryHashes;
    renderingAndPerformanceAcceptance=if($accepted.Count -eq 3){'Representative synthetic product scene: three visible-host offscreen mono runs accepted, captures visually reviewed. No Legacy32 comparison or shipping/XR performance claim.'}else{'Visible-window acceptance incomplete; inspect logs and captures.'};
    limitations=@('Original synthetic product sandbox, not Megacity physics or character integration.','Inclusive Main Thread contains waits; working set is a process snapshot, not GPU residency or peak.','No matching Legacy32 comparison or measured memory reduction.','Original scene/materials unchanged; product main unmerged; XR unverified.')}
[IO.File]::WriteAllText($summaryPath,($summary | ConvertTo-Json -Depth 10).Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))
$runs | ForEach-Object{[pscustomobject]$_}|Format-Table name,total,passed,failed,skipped,inconclusive
