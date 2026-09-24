$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$directory=Join-Path $root 'docs/diagnostics/compact16uv-appearance'
$summaryPath=Join-Path $directory 'summary.json'
$old=@{}
if(Test-Path $summaryPath){foreach($item in ((Get-Content $summaryPath -Raw | ConvertFrom-Json).evidence)){$old[$item.file]=$item}}
$evidence=@();$runs=@();$measurements=@()
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
        foreach($case in $xml.SelectNodes('//test-case')){
            foreach($line in ([string]$case.output -split "`n" | Where-Object {$_ -match '^(Appearance|Palette appearance) '})){
                $measurements += [ordered]@{run=$file.BaseName;test=$case.fullname;result=$case.result;measurement=$line.Trim()}
            }
        }
    }
}
foreach($file in (Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object {$_.Extension -notin @('.log','.xml') -and $_.Name -notin @('summary.json','.gitattributes')})){
    $evidence += [ordered]@{file=$file.FullName.Substring($directory.Length+1).Replace('\','/');sha256=(Get-FileHash $file.FullName).Hash.ToLowerInvariant()}
}
$changed=@(& git -C $root diff da7e35ee --name-only -- Assets/Zantetsu DESIGN.md Tools/Summarize-Compact16uv-Appearance.ps1)
$changed+=@(& git -C $root ls-files --others --exclude-standard -- Assets/Zantetsu Tools/Summarize-Compact16uv-Appearance.ps1)
$sources=@($changed | Sort-Object -Unique | ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash (Join-Path $root $_)).Hash.ToLowerInvariant()}})
$private=Join-Path $root 'Assets/Licensed/Compact16uvIntake/Resources/AppearanceMigration'
$inputs=@(Get-ChildItem -LiteralPath $private -File | ForEach-Object{[ordered]@{file=$_.Name;sha256=(Get-FileHash $_.FullName).Hash.ToLowerInvariant()}})
$summary=[ordered]@{schemaVersion=1;baselineCommit='da7e35ee';profile='Compact16uv-ActualMegacityAppearance';runs=$runs;measurements=$measurements;evidence=$evidence;sources=$sources;privateReferenceHashes=$inputs;
    limitations=@('One pinned static Megacity asset, not all assets or characters.','Cut Mesh oracle uses the same published CPU16 cut output, not independent Legacy32 cutting.','Isolated upload CPU timing is not total Main, GPU execution, residency or a 32B comparison.','No product scene replacement, XR acceptance or main merge.')}
[IO.File]::WriteAllText($summaryPath,($summary | ConvertTo-Json -Depth 10).Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))
$runs | ForEach-Object{[pscustomobject]$_}|Format-Table name,total,passed,failed,skipped,inconclusive
