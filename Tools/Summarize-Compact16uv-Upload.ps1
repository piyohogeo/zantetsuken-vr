param([Parameter(Mandatory=$true)][string]$PlayerDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$evidence = Join-Path $repo 'docs/diagnostics/compact16uv-upload'
$utf8 = New-Object System.Text.UTF8Encoding($false)
# Diagnostic logs only: no private source assets are copied into evidence.
Get-ChildItem -LiteralPath $evidence -File | Where-Object { $_.Extension -in @('.log','.xml') } | ForEach-Object {
    $content = [IO.File]::ReadAllText($_.FullName)
    $content = $content.Replace($env:USERPROFILE, '[USERPROFILE]').Replace($env:USERPROFILE.Replace('\','/'), '[USERPROFILE]')
    [IO.File]::WriteAllText($_.FullName, $content, $utf8)
}
$runs = @()
foreach ($number in 1..3) {
    $json = Join-Path $evidence "run$number/upload-comparison.json"
    $run = Get-Content -Raw -Encoding UTF8 -LiteralPath $json | ConvertFrom-Json
    $log = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $evidence "run$number.log")
    if (!$run.passed -or $run.rows.Count -ne 21 -or $log -notmatch 'finished with code 0' -or $log -match 'COMPARISON FAILED|Exception:') { throw "Failed run $number" }
    foreach ($row in $run.rows) {
        if (!$row.readbackPassed -or $row.samples16Us.Count -ne 61 -or $row.samples32Us.Count -ne 61 -or $row.count -ne $row.copies * $row.sourceCount -or $row.cpuCapacity32 -ne 2*$row.cpuCapacity16) { throw "Invalid cell in run $number" }
    }
    $runs += $run
}
function Median($values) { $sorted = @($values | Sort-Object); return $sorted[[int][Math]::Floor($sorted.Count/2)] }
$aggregates = @()
foreach ($first in $runs[0].rows) {
    $cells = @($runs | ForEach-Object { $_.rows | Where-Object { $_.stage -eq $first.stage -and $_.range -eq $first.range -and $_.copies -eq $first.copies } })
    if ($cells.Count -ne 3) { throw 'Mismatched cell keys' }
    foreach ($cell in $cells) { if ($cell.count -ne $first.count -or $cell.capacity -ne $first.capacity -or $cell.sourceStart -ne $first.sourceStart) { throw 'Mismatched workload' } }
    $aggregates += [pscustomobject][ordered]@{
        stage=$first.stage; range=$first.range; copies=$first.copies; count=$first.count; capacity=$first.capacity
        median16Us=(Median $cells.median16Us); median32Us=(Median $cells.median32Us)
        p95_16Us=(Median $cells.p95_16Us); p95_32Us=(Median $cells.p95_32Us)
        runMedian16Min=($cells.median16Us | Measure-Object -Minimum).Minimum; runMedian16Max=($cells.median16Us | Measure-Object -Maximum).Maximum
        runMedian32Min=($cells.median32Us | Measure-Object -Minimum).Minimum; runMedian32Max=($cells.median32Us | Measure-Object -Maximum).Maximum
        managed16Total=($cells.managed16 | Measure-Object -Sum).Sum; managed32Total=($cells.managed32 | Measure-Object -Sum).Sum
        cpuCapacity16=$first.cpuCapacity16; cpuCapacity32=$first.cpuCapacity32
        gpuLogicalCapacity16=$first.gpuLogicalCapacity16; gpuLogicalCapacity32=$first.gpuLogicalCapacity32
    }
}
function HashEntry([string]$path, [string]$label) { return [ordered]@{ path=$label; bytes=(Get-Item -LiteralPath $path).Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() } }
$hashes = @()
foreach ($relative in @('Assets/Zantetsu/Runtime/Sandbox/CompactVertexUploadComparison.cs','Assets/Zantetsu/Runtime/Sandbox/SandboxPlayerCheck.cs','Assets/Zantetsu/Editor/Sandbox/Compact16uvSandboxSceneBuild.cs','Tools/Summarize-Compact16uv-Upload.ps1','DESIGN.md','docs/diagnostics/compact16uv-upload/README.md')) {
    $hashes += HashEntry (Join-Path $repo $relative) $relative
}
$hashes += HashEntry (Join-Path $repo 'Assets/Licensed/Compact16uvIntake/Resources/Static16Migration/Megacity.bytes') 'private-fixture/Megacity.bytes'
foreach ($relative in @('CutWorldSandbox.exe','GameAssembly.dll','CutWorldSandbox_Data/globalgamemanagers')) { $hashes += HashEntry (Join-Path $PlayerDirectory $relative) "player/$relative" }
Get-ChildItem -LiteralPath $evidence -Recurse -File | Where-Object { $_.Extension -in @('.log','.xml') -or $_.Name -eq 'upload-comparison.json' } | ForEach-Object {
    $hashes += HashEntry $_.FullName $_.FullName.Substring($evidence.Length+1).Replace('\','/')
}
[xml]$tests = Get-Content -Raw -Encoding UTF8 -LiteralPath (Join-Path $evidence 'editmode.xml')
$testRun = $tests.'test-run'
if ($testRun.result -ne 'Passed' -or [int]$testRun.failed -ne 0 -or [int]$testRun.skipped -ne 0) { throw 'EditMode regression did not pass completely' }
$summary = [ordered]@{
    baselineCommit='481d8045'; sourceIdentity='baseline plus recorded diagnostic source hashes; no product layout or cutting-kernel change'
    passed=$true; independentProcesses=3; cellsPerProcess=21; measuredCallsPerLayout=3843
    orderSeeds=@($runs.orderSeed); editModePassed=[int]$testRun.passed
    unity=$runs[0].unity; graphics=$runs[0].graphics; device=$runs[0].device
    interpretation='CPU SetData submission only; quantized attributes expanded for Legacy32 control; no draw consumer, no indices, no two-world performance or RSS/peak claim'
    aggregates=$aggregates; hashes=$hashes
}
[IO.File]::WriteAllText((Join-Path $evidence 'summary.json'), ($summary | ConvertTo-Json -Depth 12)+[Environment]::NewLine, $utf8)
$aggregates | Format-Table stage,range,copies,count,median32Us,median16Us,managed16Total,managed32Total
