$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$directory=Join-Path $root 'docs/diagnostics/compact16uv-intake'
$output=Join-Path $directory 'summary.json'
$old=@{}
if(Test-Path $output){foreach($item in ((Get-Content $output -Raw | ConvertFrom-Json).evidence)){$old[$item.file]=$item}}
$evidence=@()
$runs=@()
foreach($file in (Get-ChildItem -LiteralPath $directory -File | Where-Object {$_.Extension -in @('.log','.xml')})) {
    $hash=(Get-FileHash $file.FullName).Hash.ToLowerInvariant()
    if($old[$file.Name] -and $old[$file.Name].sha256 -eq $hash){$before=$old[$file.Name].beforeAnonymizationSha256}else{$before=$hash}
    $content=[IO.File]::ReadAllText($file.FullName).Replace($env:USERPROFILE,'C:\Users\%USERNAME%').Replace($env:USERPROFILE.Replace('\','/'),'C:/Users/%USERNAME%')
    [IO.File]::WriteAllText($file.FullName,$content,[Text.UTF8Encoding]::new($false))
    $evidence += [ordered]@{file=$file.Name;sha256=(Get-FileHash $file.FullName).Hash.ToLowerInvariant();beforeAnonymizationSha256=$before}
    if($file.Extension -eq '.xml'){
        [xml]$xml=$content;$run=$xml.'test-run'
        $runs += [ordered]@{name=$file.BaseName;total=[int]$run.total;passed=[int]$run.passed;failed=[int]$run.failed;skipped=[int]$run.skipped;inconclusive=[int]$run.inconclusive;
            failures=@($xml.SelectNodes('//test-case[@result="Failed"]')|ForEach-Object{$_.fullname})}
    }
}
foreach($file in (Get-ChildItem -LiteralPath $directory -Recurse -File | Where-Object {$_.Extension -notin @('.log','.xml') -and $_.Name -notin @('summary.json','.gitattributes')})){
    $relative=$file.FullName.Substring($directory.Length+1).Replace('\','/')
    $evidence += [ordered]@{file=$relative;sha256=(Get-FileHash $file.FullName).Hash.ToLowerInvariant()}
}
$changed=@(& git -C $root diff c083815b --name-only -- Assets/Zantetsu DESIGN.md Tools/Prepare-Compact16uv-Intake.ps1 Tools/Summarize-Compact16uv-Intake.ps1)
$changed+=@(& git -C $root ls-files --others --exclude-standard -- Assets/Zantetsu Tools/Prepare-Compact16uv-Intake.ps1 Tools/Summarize-Compact16uv-Intake.ps1)
$sources=@($changed|Sort-Object -Unique|ForEach-Object{[ordered]@{path=$_;sha256=(Get-FileHash (Join-Path $root $_)).Hash.ToLowerInvariant()}})
$private=Join-Path $root 'Assets/Licensed/Compact16uvIntake'
$input=Get-Content "$private/intake.json" -Raw | ConvertFrom-Json
$summary=[ordered]@{schemaVersion=1;baselineCommit='c083815b';profile='Compact16uv-Static16-Intake';runs=$runs;evidence=$evidence;sources=$sources;
    inputProvenance=[ordered]@{topologyEvidenceSha256=$input.topologyEvidenceSha256;snapshotEvidenceSha256=$input.snapshotEvidenceSha256;
        privateManifestSha256=(Get-FileHash "$private/intake.json").Hash.ToLowerInvariant();assets=@($input.assets|ForEach-Object{[ordered]@{family=$_.family;asset=(Split-Path $_.assetPath -Leaf);sourceSha256=$_.sourceSha256;importerSha256=$_.importerSha256;textureSha256=$_.textureSha256}})};
    limitations=@('Representative static intake only, not all assets or automatic scene migration.','Player correctness is not performance or resident memory evidence.','Atlas, current-pose skinning, full asset topology export and XR remain separate.')}
[IO.File]::WriteAllText($output,($summary|ConvertTo-Json -Depth 10).Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))
$runs|ForEach-Object{[pscustomobject]$_}|Format-Table name,total,passed,failed,skipped,inconclusive
