#Requires -Version 5.1
<#
.SYNOPSIS
  Reads a distribution APK itself (DESIGN 3.5) -- not the settings it was built from -- and checks: the package, the
  versionName and versionCode against the ones asked for; the APK Signature Scheme v2 verification; the signer's
  certificate against the distribution certificate; android:debuggable false or absent. Prints one line a check and
  writes them to -Record; exit 0 when every check holds, 1 otherwise. Uses aapt2 and apksigner of Unity's Android SDK.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Apk,
    [string]$ExpectedPackage = 'com.zantetsuken.quest',
    [Parameter(Mandatory = $true)][string]$ExpectedVersionName,
    [Parameter(Mandatory = $true)][string]$ExpectedVersionCode,
    [Parameter(Mandatory = $true)][string]$ExpectedCertificateSha256,   # the distribution certificate's SHA-256, 64 hex digits; always compared
    [string]$Record,
    [string]$ManifestDump,      # where the manifest as aapt2 dumps it is written (the record); empty: not written
    [string]$BadgingDump,       # where aapt2's badging is written; empty: not written
    [int]$ExpectedMinSdk = 32,
    [int]$ExpectedTargetSdk = 34,
    [Parameter(Mandatory = $true)][string]$ExpectedSupportedDevices,   # the distribution's devices (quest3|quest3s), from the wrapper's definition
    [string]$AndroidPlayer = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Data\PlaybackEngines\AndroidPlayer',
    # The play statistics' send configuration the APK must carry ('required') or must not ('none'): the Resources asset's name
    # and the decided endpoint are looked for in the APK's serialized data (assets/bin/Data, Managed/ excluded). An AUXILIARY look (TL review 2026-10-18): a
    # byte search cannot prove presence or absence; the guarantee is the Editor's check of what the runtime's load finds
    # (QuestDistributionBuild.CheckStatisticsConfig) and the Player's own install line on the device. Nothing of the token is printed.
    [ValidateSet('required', 'none')][string]$ExpectedStatistics = 'required'
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ApkManifestChecks.ps1')
$Apk = [IO.Path]::GetFullPath($Apk)
if (!(Test-Path -LiteralPath $Apk -PathType Leaf)) { throw "no such APK: $Apk" }
if ($ExpectedCertificateSha256 -notmatch '^[0-9A-Fa-f]{64}$') { throw "ExpectedCertificateSha256 must be 64 hexadecimal digits, not '$ExpectedCertificateSha256'" }
$buildTools = Get-ChildItem -LiteralPath (Join-Path $AndroidPlayer 'SDK\build-tools') -Directory | Sort-Object Name -Descending | Select-Object -First 1
if (!$buildTools) { throw "no build-tools under $AndroidPlayer" }
$aapt2 = Join-Path $buildTools.FullName 'aapt2.exe'; $apksigner = Join-Path $buildTools.FullName 'apksigner.bat'
foreach ($t in $aapt2, $apksigner) { if (!(Test-Path -LiteralPath $t)) { throw "not found: $t" } }
if (!$env:JAVA_HOME) { $env:JAVA_HOME = Join-Path $AndroidPlayer 'OpenJDK' }

# A native tool's stderr and exit code are read as data (a failed read is a failed check, not a script error).
$script:nativeExit = -1
function Invoke-Native([scriptblock]$command) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = @(& $command 2>&1 | ForEach-Object { "$_" }); $script:nativeExit = $LASTEXITCODE }
    finally { $ErrorActionPreference = $previous }
    return $output
}

$lines = @("inspection of $Apk at $(Get-Date -Format o)", "sha256 $((Get-FileHash -LiteralPath $Apk -Algorithm SHA256).Hash.ToLowerInvariant()); bytes $((Get-Item -LiteralPath $Apk).Length)")
$checks = [ordered]@{}

# The manifest as aapt2 reads it from the APK.
$badging = Invoke-Native { & $aapt2 dump badging $Apk }
$badgingExit = $script:nativeExit
$lines += "aapt2 dump badging exit $badgingExit, lines $($badging.Count)"
$checks['aapt2 read the APK (dump badging exit 0 with output)'] = ($badgingExit -eq 0) -and ($badging.Count -gt 0)
$packageLine = @($badging | Where-Object { $_ -match "^package: name='([^']*)' versionCode='([^']*)' versionName='([^']*)'" }) | Select-Object -First 1
if ($packageLine -and $packageLine -match "^package: name='([^']*)' versionCode='([^']*)' versionName='([^']*)'") {
    $name = $Matches[1]; $code = $Matches[2]; $versionName = $Matches[3]
    $lines += "package $name; versionCode $code; versionName $versionName"
    $checks['package is ' + $ExpectedPackage] = ($name -eq $ExpectedPackage)
    $checks['versionCode is ' + $ExpectedVersionCode] = ($code -eq $ExpectedVersionCode)
    $checks['versionName is ' + $ExpectedVersionName] = ($versionName -eq $ExpectedVersionName)
} else {
    $lines += 'aapt2 dump badging gave no package line'
    $checks['package line read'] = $false
}
$debuggable = @($badging | Where-Object { $_ -match '^application-debuggable' }).Count
$manifest = Invoke-Native { & $aapt2 dump xmltree --file AndroidManifest.xml $Apk }
$manifestExit = $script:nativeExit
# The manifest was read when aapt2 exited 0 and printed the manifest element with its package: a failed or empty read
# is never taken as "debuggable absent".
$manifestRead = ($manifestExit -eq 0) -and (@($manifest | Where-Object { $_ -match '^\s*E: manifest' }).Count -eq 1) -and (@($manifest | Where-Object { $_ -match '^\s*A: package=' }).Count -eq 1)
$lines += "aapt2 dump xmltree exit $manifestExit, lines $($manifest.Count); manifest element and package read: $manifestRead"
$checks['aapt2 read the manifest (dump xmltree exit 0 with the manifest element)'] = $manifestRead
$debuggableAttribute = @($manifest | Where-Object { $_ -match 'android:debuggable\(0x0101000f\)=(\S+)' } | ForEach-Object { $Matches[1] })
$lines += "application-debuggable lines $debuggable; android:debuggable attribute values: $(if ($debuggableAttribute.Count -eq 0) { 'absent' } else { $debuggableAttribute -join ',' })"
$checks['android:debuggable is false or absent'] = $manifestRead -and ($debuggable -eq 0) -and (@($debuggableAttribute | Where-Object { $_ -ne 'false' }).Count -eq 0)
if ($ManifestDump) { $manifest | Set-Content -LiteralPath $ManifestDump -Encoding UTF8 }
if ($BadgingDump) { $badging | Set-Content -LiteralPath $BadgingDump -Encoding UTF8 }

# The Android settings and the manifest's guarantees (DESIGN 3.5, D-239), from the manifest tree: the SDK levels, the
# install location, the launch activity and its intent-filter, excludeFromRecents, the headtracking feature.
$tree = $null
if ($manifestRead) { $tree = ConvertFrom-AaptXmlTree $manifest }
$guarantees = Test-DistributionManifest -Root $tree -MinSdk $ExpectedMinSdk -TargetSdk $ExpectedTargetSdk -ExpectedDevices $ExpectedSupportedDevices
foreach ($k in $guarantees.Facts.Keys) { $lines += "manifest: $k = $($guarantees.Facts[$k])" }
foreach ($k in $guarantees.Checks.Keys) { $checks[$k] = $guarantees.Checks[$k] }

# The signature as apksigner verifies it.
$verify = Invoke-Native { & $apksigner verify --print-certs -v $Apk }
$verifyExit = $script:nativeExit
$lines += "apksigner exit $verifyExit"
$lines += @($verify | Where-Object { $_ -match '^(Verifies|Verified using v[0-9.]+ scheme|Number of signers|Signer #\d+ certificate (DN|SHA-256 digest)|DOES NOT VERIFY|WARNING|ERROR)' })
$v2 = @($verify | Where-Object { $_ -match '^Verified using v2 scheme \(APK Signature Scheme v2\): true' }).Count -eq 1
$checks['APK Signature Scheme v2 verifies'] = ($verifyExit -eq 0) -and $v2
$signers = @($verify | Where-Object { $_ -match '^Signer #\d+ certificate SHA-256 digest: ([0-9a-f]+)' } | ForEach-Object { $Matches[1] })
$checks['one signer'] = ($signers.Count -eq 1)
$checks['the signer is the distribution certificate'] = ($signers.Count -eq 1) -and ($signers[0] -eq $ExpectedCertificateSha256.ToLowerInvariant())
$debugSigner = @($verify | Where-Object { $_ -match '^Signer #\d+ certificate DN: .*CN=Android Debug' }).Count
$checks['not the Android debug certificate'] = ($debugSigner -eq 0)

# The play statistics' configuration in the APK (unit 9-B), an auxiliary look: the Resources asset's object name and the decided
# endpoint in the serialized data under assets/bin/Data. Present when a distribution must send (required), absent when the
# build was asked to send nothing (none).
$statisticsName = 'ZantetsuPlayStatisticsConfig'
$statisticsEndpoint = 'https://zantetsuken-telemetry-api.hogehogeo.workers.dev/v1/telemetry'
$statisticsPresent = $null
$endpointPresent = $null
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($Apk)
    try {
        # Unity 6 keeps the serialized data under assets/bin/Data in files named by hash (no resources.assets, seen
        # 2026-10-18 on v29), so every entry there is searched -- except the managed code (Managed/: the runtime's
        # load name is a constant of the code itself, which says nothing of the asset).
        $entries = @($zip.Entries | Where-Object { $_.FullName -like 'assets/bin/Data/*' -and $_.FullName -notlike 'assets/bin/Data/Managed/*' -and $_.Length -gt 0 })
        $nameIn = @(); $endpointIn = @(); $read = 0
        foreach ($entry in $entries) {
            $stream = $entry.Open()
            try { $memory = New-Object System.IO.MemoryStream; $stream.CopyTo($memory); $bytes = $memory.ToArray() } finally { $stream.Dispose() }
            $read += $bytes.Length
            $text = [System.Text.Encoding]::ASCII.GetString($bytes)
            if ($text.Contains($statisticsName)) { $nameIn += $entry.FullName }
            if ($text.Contains($statisticsEndpoint)) { $endpointIn += $entry.FullName }
        }
        if ($entries.Count -gt 0) {
            $statisticsPresent = $nameIn.Count -gt 0
            $endpointPresent = $endpointIn.Count -gt 0
            $lines += "assets/bin/Data (Managed/ excluded): $($entries.Count) entries, $read bytes searched (auxiliary byte search)"
            $lines += "statistics config name in: $(if ($nameIn.Count) { $nameIn -join ', ' } else { 'none' })"
            $lines += "decided endpoint in: $(if ($endpointIn.Count) { $endpointIn -join ', ' } else { 'none' })"
        } else {
            $lines += 'assets/bin/Data: no entries in the APK'
        }
    } finally { $zip.Dispose() }
} catch { $lines += "assets/bin/Data could not be read: $($_.Exception.Message)" }
if ($ExpectedStatistics -eq 'required') { $checks['auxiliary: the play statistics config name and the decided endpoint are in the serialized data (required)'] = ($statisticsPresent -eq $true -and $endpointPresent -eq $true) }
else { $checks['auxiliary: neither the play statistics config name nor the endpoint is in the serialized data (none)'] = ($statisticsPresent -eq $false -and $endpointPresent -eq $false) }

$all = $true
foreach ($k in $checks.Keys) { $ok = $checks[$k]; if (!$ok) { $all = $false }; $lines += "   [$(if ($ok) { 'ok' } else { 'FAIL' })] $k" }
$lines += "INSPECTION: $(if ($all) { 'PASSED' } else { 'FAILED' })"
$lines | ForEach-Object { $_ }
if ($Record) { $lines | Set-Content -LiteralPath $Record -Encoding UTF8 }
if ($all) { exit 0 } else { exit 1 }
