$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$directory = Join-Path $root 'docs/diagnostics/compact16uv-migration'
$summaryPath = Join-Path $directory 'migration-summary.json'
$previousEvidence = @{}
if (Test-Path -LiteralPath $summaryPath) {
    foreach ($item in (([IO.File]::ReadAllText($summaryPath) | ConvertFrom-Json).evidence)) {
        $previousEvidence[$item.file] = $item
    }
}
$names = @('migration-first','migration-second','migration-third','migration-all-editmode','migration-player','migration-player-final','migration-final-editmode','migration-readback-repro','migration-readback-repro2','migration-verified-editmode','migration-playmode','migration-playmode-normal','migration-playmode-termination','migration-player-verified','migration-player-verified-fresh')
$evidence = @()
$runs = @()
foreach ($name in $names) {
    foreach ($extension in @('log','xml')) {
        $path = Join-Path $directory "$name.$extension"
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $originalHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
        $previous = $previousEvidence["$name.$extension"]
        if ($previous -and $previous.sha256 -eq $originalHash -and $previous.beforeAnonymizationSha256) {
            $originalHash = $previous.beforeAnonymizationSha256
        }
        $content = [IO.File]::ReadAllText($path)
        # Public evidence follows DESIGN 3.3: anonymize only this run's user-profile prefix.
        $content = $content.Replace($env:USERPROFILE, 'C:\Users\%USERNAME%').Replace($env:USERPROFILE.Replace('\','/'), 'C:/Users/%USERNAME%')
        [IO.File]::WriteAllText($path, $content, (New-Object Text.UTF8Encoding($false)))
        $evidence += [ordered]@{file="$name.$extension"; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant(); beforeAnonymizationSha256=$originalHash}
    }
    $xmlPath = Join-Path $directory "$name.xml"
    if (Test-Path -LiteralPath $xmlPath) {
        [xml]$xml = [IO.File]::ReadAllText($xmlPath)
        $run = $xml.'test-run'
        $runs += [ordered]@{name=$name; total=[int]$run.total; passed=[int]$run.passed; failed=[int]$run.failed; skipped=[int]$run.skipped; inconclusive=[int]$run.inconclusive; failures=@($xml.SelectNodes('//test-case[@result="Failed"]') | ForEach-Object {$_.fullname})}
    } else {
        $runs += [ordered]@{name=$name; result='NoTestResults'; note='Build/compile failure; inspect retained log. Not a passed test run.'}
    }
}
foreach ($imageName in @('player-images','player-images-verified')) {
    $imagePath = Join-Path $directory $imageName
    if (Test-Path -LiteralPath $imagePath) {
        $evidence += @(Get-ChildItem -LiteralPath $imagePath -File | ForEach-Object { [ordered]@{file=$imageName + '/' + $_.Name; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} })
    }
}
$changed = @(& git -C $root diff 4326c48 --name-only -- Assets DESIGN.md Tools/Summarize-Compact16uv-Migration.ps1)
$changed += @(& git -C $root ls-files --others --exclude-standard -- Assets/Zantetsu/Runtime/Rendering Assets/Zantetsu/Tests/StandaloneRendering Tools/Summarize-Compact16uv-Migration.ps1)
$source = @($changed | Sort-Object -Unique | ForEach-Object { [ordered]@{path=$_; sha256=(Get-FileHash -LiteralPath (Join-Path $root $_) -Algorithm SHA256).Hash.ToLowerInvariant()} })
$summary = [ordered]@{profile='Compact16uv-Product-CPU16'; baselineCommit='4326c48'; unity='6000.3.22f1'; cpuVertexBytes=16; gpuVertexBytes=16; mainUploadPackOperations=0; runs=$runs; evidence=$evidence; sources=$source; limitations=@('Player correctness is not a performance measurement.','Atlas texture switching, new asset intake, product scene residency and XR are separate gates.','Logs/XML anonymize the current user-profile path; before and after hashes are recorded.')}
$json = ($summary | ConvertTo-Json -Depth 8) + "`n"
[IO.File]::WriteAllText($summaryPath, $json.Replace("`r`n","`n"), (New-Object Text.UTF8Encoding($false)))
$runs | ForEach-Object { [pscustomobject]$_ } | Format-Table name,total,passed,failed,skipped,inconclusive,result
