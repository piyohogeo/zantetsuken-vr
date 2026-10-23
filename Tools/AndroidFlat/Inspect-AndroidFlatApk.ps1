#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Apk,
    [string]$ExpectedPackage = 'com.zantetsuken.androidflat',
    [string]$Record,
    [int]$ExpectedMinSdk = 32,
    [int]$ExpectedTargetSdk = 36,
    [string]$AndroidPlayer = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Data\PlaybackEngines\AndroidPlayer'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\Quest3s\ApkManifestChecks.ps1')
$Apk = [IO.Path]::GetFullPath($Apk)
if (!(Test-Path -LiteralPath $Apk -PathType Leaf)) { throw "APK not found: $Apk" }
$buildTools = Get-ChildItem -LiteralPath (Join-Path $AndroidPlayer 'SDK\build-tools') -Directory | Sort-Object Name -Descending | Select-Object -First 1
if (!$buildTools) { throw 'Android SDK build-tools are missing' }
$aapt = Join-Path $buildTools.FullName 'aapt2.exe'
$manifest = @(& $aapt dump xmltree --file AndroidManifest.xml $Apk 2>&1 | ForEach-Object { "$_" })
$manifestExit = $LASTEXITCODE
$badging = @(& $aapt dump badging $Apk 2>&1 | ForEach-Object { "$_" })
$badgingExit = $LASTEXITCODE
$tree = ConvertFrom-AaptXmlTree $manifest
$roots = @($tree | Where-Object Name -CEQ 'manifest')
$checks = [ordered]@{}
$checks['aapt2 reads the APK and manifest'] = $manifestExit -eq 0 -and $badgingExit -eq 0 -and $roots.Count -eq 1
$checks['separate phone application id'] = $roots.Count -eq 1 -and (Get-AttributeValue $roots[0] 'package') -ceq $ExpectedPackage
$checks['minimum SDK matches'] = @($badging | Where-Object { $_ -ceq "minSdkVersion:'$ExpectedMinSdk'" }).Count -eq 1
$checks['target SDK matches'] = @($badging | Where-Object { $_ -ceq "targetSdkVersion:'$ExpectedTargetSdk'" }).Count -eq 1
$checks['ARM64 only'] = @($badging | Where-Object { $_ -ceq "native-code: 'arm64-v8a'" }).Count -eq 1
$forbidden = @($manifest | Where-Object { $_ -match 'com\.oculus\.|com\.meta\.openxr\.|android\.hardware\.vr\.|android\.software\.vr\.mode' })
$checks['no Quest or VR requirements in final merged manifest'] = $forbidden.Count -eq 0
$launchers = @()
if ($roots.Count -eq 1) {
    foreach ($app in Get-ManifestChildren $roots[0] 'application') {
        foreach ($activity in Get-ManifestChildren $app 'activity') {
            foreach ($filter in Get-ManifestChildren $activity 'intent-filter') {
                $actions = @(Get-ManifestChildren $filter 'action' | ForEach-Object { Get-AttributeValue $_ 'name' })
                if ($actions -ccontains 'android.intent.action.MAIN') { $launchers += [pscustomobject]@{ Activity = $activity; Filter = $filter }; break }
            }
        }
    }
}
$checks['one normal phone launcher'] = $launchers.Count -eq 1
$checks['launcher supports autorotation'] = $false
$checks['launcher appears in recents'] = $false
if ($launchers.Count -eq 1) {
    $categories = @(Get-ManifestChildren $launchers[0].Filter 'category' | ForEach-Object { Get-AttributeValue $_ 'name' })
    $checks['one normal phone launcher'] = $categories -ccontains 'android.intent.category.LAUNCHER'
    $orientation = Get-AttributeValue $launchers[0].Activity 'screenOrientation'
    $checks['launcher supports autorotation'] = $orientation -in @(2,4,10,13,'user','sensor','fullSensor','fullUser')
    $exclude = Get-AttributeValue $launchers[0].Activity 'excludeFromRecents'
    $checks['launcher appears in recents'] = $null -eq $exclude -or (Test-AaptFalse $exclude)
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($Apk)
try { $checks['native Vulkan plugin is packaged for ARM64'] = @($zip.Entries | Where-Object FullName -CEQ 'lib/arm64-v8a/libZantetsuVpVulkan.so').Count -eq 1 }
finally { $zip.Dispose() }
$lines = @("APK $Apk", "SHA256 $((Get-FileHash -LiteralPath $Apk -Algorithm SHA256).Hash)")
foreach ($name in $checks.Keys) { $lines += "$(if ($checks[$name]) { 'PASS' } else { 'FAIL' }) $name" }
if ($Record) {
    $recordPath = [IO.Path]::GetFullPath($Record)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($recordPath)) | Out-Null
    $lines | Set-Content -LiteralPath $recordPath -Encoding UTF8
    $manifest | Set-Content -LiteralPath ($recordPath + '.manifest.txt') -Encoding UTF8
    $badging | Set-Content -LiteralPath ($recordPath + '.badging.txt') -Encoding UTF8
}
$lines
if (@($checks.Values | Where-Object { -not $_ }).Count -gt 0) { exit 1 }
exit 0
