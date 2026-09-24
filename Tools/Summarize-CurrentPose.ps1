param([string]$Repository = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$destination = Join-Path $Repository 'docs/diagnostics/compact16uv-current-pose'
[IO.Directory]::CreateDirectory($destination) | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
function Save-Evidence([string]$source, [string]$name) {
    $content = [IO.File]::ReadAllText((Join-Path $Repository $source))
    $content = $content.Replace($env:USERPROFILE, '<USERPROFILE>').Replace($env:USERPROFILE.Replace('\','/'), '<USERPROFILE>').Replace($env:COMPUTERNAME, '<COMPUTER>')
    # XML attribute values need escaped replacement tokens.
    $content = $content.Replace('<USERPROFILE>', '[USERPROFILE]').Replace('<COMPUTER>', '[COMPUTER]').Replace("`r", '')
    $content = (($content -split "`n" | ForEach-Object { $_.TrimEnd() }) -join "`n").TrimEnd() + "`n"
    [IO.File]::WriteAllText((Join-Path $destination $name), $content, $utf8)
    return [xml]$content
}
$full = Save-Evidence 'Temp/current-pose-full.xml' 'editmode.xml'
$run = $full.'test-run'
if ($run.result -ne 'Passed' -or [int]$run.passed -ne 3622 -or [int]$run.failed -ne 0 -or [int]$run.skipped -ne 0) { throw 'Test acceptance failed.' }
$componentCases = @($full.SelectNodes('//test-case[@classname="Zantetsu.PhysicsCut.Tests.Compact16uvCurrentPoseTests"]'))
if ($componentCases.Count -ne 8 -or @($componentCases | Where-Object { $_.result -ne 'Passed' }).Count -ne 0) { throw 'Current pose component acceptance failed.' }
$inputManifest = Get-Content (Join-Path $Repository 'Assets/Licensed/Compact16uvIntake/intake.json') -Raw | ConvertFrom-Json
$assets = @()
foreach ($entry in ($inputManifest.assets | Where-Object { $_.family -like 'character-*' })) {
    $familyDir = if ($entry.family -eq 'character-casual') { 'CasualCharacters' } else { 'ProfessionalCharacters' }
    $latestRelative = 'Generated/BottomHalfUV/ITHappyCharacterMeshRepair/' + $familyDir + '/1.23.0-convex-remap_palette/Working/' + [IO.Path]::GetFileName($entry.assetPath)
    $latest = Join-Path (Split-Path $Repository -Parent) ('zantetsuken-blender-pipeline-pilot/' + $latestRelative)
    $fixedHash = (Get-FileHash (Join-Path $Repository $entry.assetPath)).Hash.ToLowerInvariant()
    $latestHash = (Get-FileHash $latest).Hash.ToLowerInvariant()
    if ($fixedHash -ne $entry.sourceSha256 -or $latestHash -ne $fixedHash) { throw 'Source provenance changed.' }
    $assets += [ordered]@{ family=$entry.family; source=$entry.assetPath; latestSource=$latestRelative; sourceSha256=$fixedHash; latestSourceSha256=$latestHash; topologyCount=$entry.topologyCount }
}
$cases = @($componentCases | ForEach-Object { [ordered]@{ name=$_.name; result=$_.result; output=$_.output.InnerText } })
$summary = [ordered]@{
    schemaVersion=1; unity='6000.3.22f1'; baseCommit='a163b0c4'; platform='Windows Editor EditMode';
    disposition='Renderer-local BakeMesh(true) component gate; no scene, physics rig, performance or IL2CPP acceptance';
    originalFalseObservation=@{ total=6; passed=2; failed=4; classification='F-SKIN-FRAME'; rawXmlRetained=$false; reproducedBy='Final component negative controls' };
    componentSubset=@{ total=8; passed=8; skipped=0 }; fullEditMode=@{ total=3622; passed=3622; skipped=0 };
    assets=$assets; cases=$cases;
    testSourceSha256=(Get-FileHash (Join-Path $Repository 'Assets/Zantetsu/Tests/EditMode/PhysicsCut/Compact16uvCurrentPoseTests.cs')).Hash.ToLowerInvariant()
}
[IO.File]::WriteAllText((Join-Path $destination 'summary.json'), ($summary | ConvertTo-Json -Depth 12).Replace("`r", '') + "`n", $utf8)
Write-Output 'Current-pose evidence accepted: component subset 8/8, full 3622/3622, both latest sources equal pinned inputs.'
