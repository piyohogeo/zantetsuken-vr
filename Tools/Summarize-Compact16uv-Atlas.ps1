$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$directory=Join-Path $root 'docs/diagnostics/compact16uv-atlas'
$summaryPath=Join-Path $directory 'summary.json'
$old=@{}
if(Test-Path $summaryPath){foreach($item in ((Get-Content $summaryPath -Raw | ConvertFrom-Json).evidence)){$old[$item.file]=$item}}
$evidence=@();$runs=@()
foreach($file in (Get-ChildItem -LiteralPath $directory -File | Where-Object {$_.Extension -in @('.log','.xml')})){
    $hash=(Get-FileHash $file.FullName).Hash.ToLowerInvariant()
    $before=if($old[$file.Name] -and $old[$file.Name].sha256 -eq $hash){$old[$file.Name].beforeAnonymizationSha256}else{$hash}
    $content=[IO.File]::ReadAllText($file.FullName).Replace($env:USERPROFILE,'C:\Users\%USERNAME%').Replace($env:USERPROFILE.Replace('\','/'),'C:/Users/%USERNAME%')
    [IO.File]::WriteAllText($file.FullName,$content,[Text.UTF8Encoding]::new($false))
    $evidence += [ordered]@{file=$file.Name;sha256=(Get-FileHash $file.FullName).Hash.ToLowerInvariant();beforeAnonymizationSha256=$before}
    if($file.Extension -eq '.xml'){
        [xml]$xml=$content;$run=$xml.'test-run'
        $runs += [ordered]@{name=$file.BaseName;total=[int]$run.total;passed=[int]$run.passed;failed=[int]$run.failed;skipped=[int]$run.skipped;inconclusive=[int]$run.inconclusive;
            failures=@($xml.SelectNodes('//test-case[@result="Failed"]') | ForEach-Object{$_.fullname})}
    }
}
foreach($file in (Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object {$_.Extension -notin @('.log','.xml') -and $_.Name -notin @('summary.json','.gitattributes')})){
    $evidence += [ordered]@{file=$file.FullName.Substring($directory.Length+1).Replace('\','/');sha256=(Get-FileHash $file.FullName).Hash.ToLowerInvariant()}
}
$changed=@(& git -C $root diff 336f031 --name-only -- Assets/Zantetsu DESIGN.md Tools/Summarize-Compact16uv-Atlas.ps1)
$changed+=@(& git -C $root ls-files --others --exclude-standard -- Assets/Zantetsu Tools/Summarize-Compact16uv-Atlas.ps1)
$sources=@($changed | Sort-Object -Unique | ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash (Join-Path $root $_)).Hash.ToLowerInvariant()}})
$private=Join-Path $root 'Assets/Licensed/Compact16uvIntake/Resources/PaletteAtlas'
$atlases=@(Get-ChildItem -LiteralPath $private -File | Where-Object {$_.Extension -in @('.png','.meta')} | ForEach-Object{[ordered]@{file=$_.Name;sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}})
$summary=[ordered]@{schemaVersion=1;baselineCommit='336f031';profile='Compact16uv-PaletteAtlas';runs=$runs;evidence=$evidence;sources=$sources;privateAtlasHashes=$atlases;
    sourcePaletteSha256='88fdb7640dcadbef46db9bb52a533180ae0ef2d5bec5bea599d024c3614ebcc5';
    limitations=@('Shared pair and material opt-in; no scene is automatically migrated.','Editor texture memory estimates are not WDDM residency.','Private atlas PNGs are excluded from Git; rendered diagnostic images are retained.','Mono test results do not establish XR or all-material correctness.')}
[IO.File]::WriteAllText($summaryPath,($summary | ConvertTo-Json -Depth 10).Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))
$runs | ForEach-Object{[pscustomobject]$_}|Format-Table name,total,passed,failed,skipped,inconclusive
