#Requires -Version 5.1
<#
.SYNOPSIS
  The ALPHA distribution build of the Quest APK of every scenario (DESIGN 3.5): Build-ScenarioPlayer.ps1's build as a
  Release, commercial, custom-signed APK with the version the distributor names, checked before and after.

.DESCRIPTION
  Before Unity starts: the version code must be a whole number from 1 upward, the keystore file must exist, both
  passwords must be in the environment (ZANTETSU_KEYSTORE_PASSWORD, ZANTETSU_KEY_PASSWORD -- never on a command line,
  never in a log, never in a file of the repository), and the alias must be in the keystore (keytool reads the store
  password from the environment variable, -storepass:env). The distribution certificate's SHA-256 is recorded from it.
  Then Unity runs Zantetsu.EditorTools.Quest.QuestDistributionBuild.BuildApk with -zantetsuBuildMode commercial and no
  development option; the Editor refuses (exit 2) anything missing or malformed, builds otherwise, and puts its player
  settings back whether or not the build succeeds. This script also saves ProjectSettings/ProjectSettings.asset before
  Unity starts and puts it back afterwards whatever happened, so no signing or version setting stays in the tree.
  After the build, Inspect-DistributionApk.ps1 reads the APK itself: package, versionName, versionCode, the v2 signature,
  the signer's certificate against the keystore's, and android:debuggable. The record (no secret in it) goes beside
  the APK: <output>.record\.
  The version code is never incremented here: the distributor names one larger than every one uploaded before
  (docs/quest3s-alpha-distribution.md).

.EXAMPLE
  $env:ZANTETSU_KEYSTORE_PASSWORD = (Read-Host -AsSecureString 'keystore password' | ConvertFrom-SecureString -AsPlainText)  # PowerShell 7
  $env:ZANTETSU_KEY_PASSWORD = ...
  .\Tools\Quest3s\Build-Distribution.ps1 -VersionName 0.2.0 -VersionCode 2 -Keystore D:\keys\zantetsu-alpha.keystore -KeyAlias zantetsu-alpha
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$VersionName,
    [Parameter(Mandatory = $true)][string]$VersionCode,     # a string on purpose: it is checked as digits, not rounded or parsed loosely
    [Parameter(Mandatory = $true)][string]$Keystore,
    [Parameter(Mandatory = $true)][string]$KeyAlias,
    [string]$Output,
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe',
    [string]$AndroidPlayer = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Data\PlaybackEngines\AndroidPlayer'
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$package = 'com.zantetsuken.quest'
$storeVariable = 'ZANTETSU_KEYSTORE_PASSWORD'; $keyVariable = 'ZANTETSU_KEY_PASSWORD'

# 1. What can be known before Unity starts.
if ($VersionCode -notmatch '^[0-9]{1,10}$' -or [int64]$VersionCode -lt 1 -or [int64]$VersionCode -gt 2147483647) { throw "VersionCode must be a whole number from 1 to 2147483647 written in digits alone, not '$VersionCode'" }
if ($VersionName -match '\s' -or $VersionName.Length -eq 0) { throw "VersionName must hold no space: '$VersionName'" }
# The alias and the version name travel to Unity on its command line, which splits at spaces: a value with one is
# refused here rather than carried wrongly (keytool alone would have taken it whole).
if ($KeyAlias -match '\s' -or $KeyAlias.Length -eq 0 -or $KeyAlias.StartsWith('-')) { throw "KeyAlias must hold no space and not begin with '-' (it is carried on Unity's command line): '$KeyAlias'" }
if ($VersionName.StartsWith('-')) { throw "VersionName must not begin with '-': '$VersionName'" }
$Keystore = [IO.Path]::GetFullPath($Keystore)
if (!(Test-Path -LiteralPath $Keystore -PathType Leaf)) { throw "the keystore does not exist: $Keystore" }
if ($Keystore -notmatch '\.(keystore|jks)$') { throw "the keystore must be a .keystore or .jks file: $Keystore" }
if ($Keystore.StartsWith($project, [StringComparison]::OrdinalIgnoreCase)) { throw "the keystore must not be inside the repository: $Keystore" }
foreach ($v in $storeVariable, $keyVariable) { if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($v))) { throw "the environment variable $v is not set (set it in this shell; it is never passed on a command line)" } }
if (!$Output) { $Output = Join-Path $project ("Build\quest-alpha-$VersionName-$VersionCode.apk") }
$Output = [IO.Path]::GetFullPath($Output)
if ($Output -notmatch '\.apk$') { throw "Output must be an .apk file: $Output" }
if (Test-Path -LiteralPath $Output) { throw "exists already (a distribution APK is never overwritten): $Output" }
$record = "$Output.record"
if (Test-Path -LiteralPath $record) { throw "exists already: $record" }
$keytool = Join-Path $AndroidPlayer 'OpenJDK\bin\keytool.exe'
if (!(Test-Path -LiteralPath $keytool)) { throw "keytool not found: $keytool" }

# The alias must be in the keystore, and its certificate is the one the APK must be signed with.
$listed = & $keytool '-J-Duser.language=en' '-J-Duser.country=US' -list -v -keystore $Keystore -alias $KeyAlias '-storepass:env' $storeVariable 2>&1
if ($LASTEXITCODE -ne 0) { throw "the alias '$KeyAlias' is not in the keystore, or the keystore password is wrong (keytool exit $LASTEXITCODE)" }
$fingerprint = @($listed | Where-Object { $_ -match '^\s*SHA256:\s*([0-9A-F:]+)' } | ForEach-Object { $Matches[1] }) | Select-Object -First 1
if (!$fingerprint) { throw 'keytool listed the alias but no SHA256 fingerprint was read' }
$certificateSha256 = ($fingerprint -replace ':', '').ToLowerInvariant()
$validity = @($listed | Where-Object { $_ -match '^\s*Valid from' }) | Select-Object -First 1

# 2. The build, with the settings file saved and put back whatever happens.
New-Item -ItemType Directory -Path $record | Out-Null
$settings = Join-Path $project 'ProjectSettings\ProjectSettings.asset'
$saved = Join-Path $record 'ProjectSettings.asset.before'
Copy-Item -LiteralPath $settings -Destination $saved
$logs = Join-Path $project 'Logs/quest-distribution'
[IO.Directory]::CreateDirectory($logs) | Out-Null
$log = Join-Path $logs "build-$VersionName-$VersionCode.log"
$arguments = '-batchmode -quit -force-d3d11 -projectPath "' + $project + '" -buildTarget Android -executeMethod Zantetsu.EditorTools.Quest.QuestDistributionBuild.BuildApk -logFile "' + $log + '"' `
    + ' -zantetsuBuildMode commercial -zantetsuQuestOut "' + $Output + '"' `
    + ' -zantetsuDistVersionName ' + $VersionName + ' -zantetsuDistVersionCode ' + $VersionCode `
    + ' -zantetsuDistKeystore "' + $Keystore + '" -zantetsuDistKeyAlias ' + $KeyAlias
$head = git -C $project log --format='%H %s' -1
$dirty = @(git -C $project status --short --untracked-files=all)
@(
    "distribution build begun $(Get-Date -Format o)",
    "package $package; versionName $VersionName; versionCode $VersionCode",
    "keystore $Keystore; alias $KeyAlias; certificate sha256 $certificateSha256; $validity",
    "source $head",
    "uncommitted changes: $($dirty.Count)",
    "conditions: Release (no development option), commercial (-zantetsuBuildMode commercial), custom keystore, Android ARM64 IL2CPP Vulkan (the shared Quest build)",
    "unity $Unity",
    "command $arguments"
) | Set-Content -LiteralPath (Join-Path $record 'build.txt') -Encoding UTF8
if ($dirty.Count -gt 0) { $dirty | Set-Content -LiteralPath (Join-Path $record 'status.txt') -Encoding UTF8; git -C $project diff | Out-File -LiteralPath (Join-Path $record 'working-tree.patch') -Encoding utf8 }
$exit = -1
try {
    $process = Start-Process -FilePath $Unity -ArgumentList $arguments -WorkingDirectory $project -WindowStyle Hidden -PassThru
    $handle = $process.Handle
    $process.WaitForExit()
    $exit = $process.ExitCode
}
finally {
    $after = Get-Content -LiteralPath $settings -Raw
    $before = Get-Content -LiteralPath $saved -Raw
    if ($after -ne $before) {
        Copy-Item -LiteralPath $settings -Destination (Join-Path $record 'ProjectSettings.asset.after-unity')
        Copy-Item -LiteralPath $saved -Destination $settings -Force
        "ProjectSettings.asset differed after Unity and was put back from the saved copy" | Add-Content -LiteralPath (Join-Path $record 'build.txt') -Encoding UTF8
    } else {
        "ProjectSettings.asset unchanged after Unity" | Add-Content -LiteralPath (Join-Path $record 'build.txt') -Encoding UTF8
    }
    "unity exit $exit; ended $(Get-Date -Format o)" | Add-Content -LiteralPath (Join-Path $record 'build.txt') -Encoding UTF8
    if (Test-Path -LiteralPath $log) { Copy-Item -LiteralPath $log -Destination (Join-Path $record 'build.log') }
}
if ($exit -eq 2) { throw "the request was refused by the Editor before the build (see ${log}: QUEST DISTRIBUTION BUILD refused)" }
if ($exit -ne 0) { throw "Unity failed ($exit); see $log" }
if (!(Test-Path -LiteralPath $Output)) { throw "Unity exited 0 but the APK is not there: $Output" }

# 3. The APK itself.
$apkSha256 = (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash.ToLowerInvariant()
"apk sha256 $apkSha256; bytes $((Get-Item -LiteralPath $Output).Length)" | Add-Content -LiteralPath (Join-Path $record 'build.txt') -Encoding UTF8
& (Join-Path $PSScriptRoot 'Inspect-DistributionApk.ps1') -Apk $Output -ExpectedPackage $package -ExpectedVersionName $VersionName -ExpectedVersionCode $VersionCode -ExpectedCertificateSha256 $certificateSha256 -Record (Join-Path $record 'inspection.txt') -ManifestDump (Join-Path $record 'manifest.txt') -BadgingDump (Join-Path $record 'badging.txt') -AndroidPlayer $AndroidPlayer
if ($LASTEXITCODE -ne 0) { throw "the APK failed its inspection (see $record\inspection.txt)" }
"distribution build of $package $VersionName ($VersionCode): $Output"
"apk sha256 $apkSha256"
"certificate sha256 $certificateSha256"
"record $record"
