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
    # Experiment switches the Editor's build reads from its command line (such as QuestStaticDemoBuild.NoPreloadExperimentArgument,
    # unit 8-C 2026-10-17): each is '-zantetsu' followed by letters and digits alone. Recorded in build.txt. Never for a distribution.
    [string[]]$ExperimentArguments = @(),
    # The play statistics' send configuration (unit 9-B): 'required' (the default) or 'none' (a build that sends nothing,
    # on purpose: no configuration may be in the project). With 'required' this script makes the configuration itself
    # from the environment variable -StatisticsTokenVariable (Set-StatisticsConfig.ps1) after moving any one already in
    # the tree aside, and moves the one it made aside again in its finally, whether the build succeeded or not: no tree
    # keeps a token after this script (TL review 2026-10-18 [P2]). The Editor checks what the runtime's load would find
    # (QuestDistributionBuild.CheckStatisticsConfig); the inspection's look at the APK is an auxiliary one.
    [ValidateSet('required', 'none')][string]$Statistics = 'required',
    [string]$StatisticsTokenVariable = 'ZANTETSU_INGEST_TOKEN_ALPHA',
    # Where the configurations are moved (outside the repository; it holds the token). Required with -Statistics required.
    [string]$StatisticsAside,
    [string]$AndroidPlayer = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Data\PlaybackEngines\AndroidPlayer',
    # The forbidden-word list (Tools/ForbiddenWords.ps1; outside the repository). Required (unit 11, TL 2026-10-18 [P1]):
    # before Unity starts the repository must pass Verify-PublicRepository.ps1 with it, and the APK's inspection
    # searches every entry and the signer's certificate with it.
    [string]$ForbiddenWordsFile = $env:ZANTETSU_FORBIDDEN_WORDS_FILE,
    # Where the licensed inputs' originals are copied while their source is blank for the build (InputSources.ps1;
    # outside the repository). Required when the tree holds such inputs.
    [string]$InputSourceAside
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$package = 'com.zantetsuken.quest'
# The distribution's devices (D-240). The Editor's definition (QuestDistributionTargets.CanonicalValue) must equal this:
# the build refuses otherwise, so the build, the inspection and the record never disagree.
$supportedDevices = 'quest3|quest3s'
$storeVariable = 'ZANTETSU_KEYSTORE_PASSWORD'; $keyVariable = 'ZANTETSU_KEY_PASSWORD'

# 1. What can be known before Unity starts.
if ($VersionCode -notmatch '^[0-9]{1,10}$' -or [int64]$VersionCode -lt 1 -or [int64]$VersionCode -gt 2147483647) { throw "VersionCode must be a whole number from 1 to 2147483647 written in digits alone, not '$VersionCode'" }
if ($VersionName -match '\s' -or $VersionName.Length -eq 0) { throw "VersionName must hold no space: '$VersionName'" }
# The alias and the version name travel to Unity on its command line, which splits at spaces: a value with one is
# refused here rather than carried wrongly (keytool alone would have taken it whole).
if ($KeyAlias -match '\s' -or $KeyAlias.Length -eq 0 -or $KeyAlias.StartsWith('-')) { throw "KeyAlias must hold no space and not begin with '-' (it is carried on Unity's command line): '$KeyAlias'" }
if ($VersionName.StartsWith('-')) { throw "VersionName must not begin with '-': '$VersionName'" }
foreach ($x in $ExperimentArguments) { if ($x -notmatch '^-zantetsu[A-Za-z0-9]+$') { throw "an experiment argument must be '-zantetsu' followed by letters and digits alone: '$x'" } }
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
$statisticsScript = Join-Path $PSScriptRoot 'Set-StatisticsConfig.ps1'
$statisticsEndpoint = 'https://zantetsuken-telemetry-api.hogehogeo.workers.dev/v1/telemetry'   # QuestDistributionBuild.StatisticsEndpoint
$statisticsAsset = Join-Path $project 'Assets\Zantetsu\Runtime\Sandbox\Resources\ZantetsuPlayStatisticsConfig.asset'
if ($Statistics -eq 'required') {
    if (!$StatisticsAside) { throw '-Statistics required needs -StatisticsAside (a folder outside the repository the configurations are moved to)' }
    $StatisticsAside = [IO.Path]::GetFullPath($StatisticsAside).TrimEnd('\')
    $projectRoot = $project.TrimEnd('\')
    # Neither the project itself nor anything under it (TL review 2026-10-18).
    if ($StatisticsAside.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $StatisticsAside.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "-StatisticsAside must be outside the repository (not the repository itself either): $StatisticsAside" }
    $hasToken = -not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($StatisticsTokenVariable)) -or -not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($StatisticsTokenVariable, 'User'))
    if (!$hasToken) { throw "the environment variable $StatisticsTokenVariable is not set (process or User)" }
}
# The repository itself, with the forbidden-word list required: a distribution is never built from a tree the public
# check refuses, nor without the list (the check's own exit code decides; its lines name no word). What it looks at
# here: the tracked files' current contents and paths, the ref names, and the metadata (authors, committers,
# messages) of the commits the built HEAD reaches -- NOT the contents of past versions in the history, which the
# pre-push hook scans before anything is published (Tools/Hooks/pre-push, -ScanHistoryContents). What is built is the
# current tree, and the APK itself is searched after the build (Inspect-DistributionApk.ps1).
if (!$ForbiddenWordsFile) { throw 'a distribution build needs -ForbiddenWordsFile (or ZANTETSU_FORBIDDEN_WORDS_FILE): the list kept outside the repository' }
$headCommit = (git -C $project rev-parse HEAD).Trim()
if ($headCommit -notmatch '^[0-9a-f]{40}$') { throw "the HEAD commit of $project could not be read" }
$hygiene = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $project 'Tools\Verify-PublicRepository.ps1') -RepositoryPath $project -ForbiddenWordsFile $ForbiddenWordsFile -RequireForbiddenWords -Revisions $headCommit 2>&1 | ForEach-Object { "$_" })
$hygieneExit = $LASTEXITCODE
$hygieneSummary = (@($hygiene | Where-Object { $_ -match 'Repository hygiene check|Forbidden words|Violation\(s\)|^ERROR' }) -join '; ') -replace '\s+', ' '
if ($hygieneExit -ne 0) { throw "the repository fails the public check with the forbidden-word list (exit $hygieneExit): $hygieneSummary -- run Tools\Verify-PublicRepository.ps1 -ForbiddenWordsFile <list> to see where" }
# The licensed inputs whose source names the machine's folders (unit 11): blanked for the build, put back after it.
. (Join-Path $PSScriptRoot 'InputSources.ps1')
$inputTargets = @(Find-InputSourceTargets $project)
$inputManifest = $null; $inputAside = $null
if ($inputTargets.Count -gt 0) {
    if (!$InputSourceAside) { throw "the tree holds $($inputTargets.Count) licensed inputs whose source is an absolute path: -InputSourceAside (a folder outside the repository) is needed to blank them for the build" }
    $InputSourceAside = [IO.Path]::GetFullPath($InputSourceAside).TrimEnd('\')
    $projectRootPath = $project.TrimEnd('\')
    if ($InputSourceAside.Equals($projectRootPath, [StringComparison]::OrdinalIgnoreCase) -or $InputSourceAside.StartsWith($projectRootPath + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "-InputSourceAside must be outside the repository: $InputSourceAside" }
    $inputAside = Join-Path $InputSourceAside ("$VersionName-$VersionCode-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
    if (Test-Path -LiteralPath $inputAside) { throw "exists already: $inputAside" }
}
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
# The files the build may change and must leave as they were: the player settings and the OpenXR package settings
# (the Meta Quest feature's target devices live there). Saved here, compared and put back afterwards whatever happened.
$watched = @(
    @{ Path = (Join-Path $project 'ProjectSettings\ProjectSettings.asset'); Saved = (Join-Path $record 'ProjectSettings.asset.before'); After = (Join-Path $record 'ProjectSettings.asset.after-unity'); Name = 'ProjectSettings.asset' },
    @{ Path = (Join-Path $project 'Assets\XR\Settings\OpenXR Package Settings.asset'); Saved = (Join-Path $record 'OpenXR Package Settings.asset.before'); After = (Join-Path $record 'OpenXR Package Settings.asset.after-unity'); Name = 'OpenXR Package Settings.asset' }
)
foreach ($w in $watched) {
    if (!(Test-Path -LiteralPath $w.Path -PathType Leaf)) { throw "the watched settings file is not there: $($w.Path)" }
    Copy-Item -LiteralPath $w.Path -Destination $w.Saved
}
$logs = Join-Path $project 'Logs/quest-distribution'
[IO.Directory]::CreateDirectory($logs) | Out-Null
$log = Join-Path $logs "build-$VersionName-$VersionCode.log"
$arguments = '-batchmode -quit -force-d3d11 -projectPath "' + $project + '" -buildTarget Android -executeMethod Zantetsu.EditorTools.Quest.QuestDistributionBuild.BuildApk -logFile "' + $log + '"' `
    + ' -zantetsuBuildMode commercial -zantetsuQuestOut "' + $Output + '"' `
    + ' -zantetsuDistVersionName ' + $VersionName + ' -zantetsuDistVersionCode ' + $VersionCode `
    + ' -zantetsuDistKeystore "' + $Keystore + '" -zantetsuDistKeyAlias ' + $KeyAlias + ' -zantetsuDistDevices ' + $supportedDevices
if ($ExperimentArguments.Count -gt 0) { $arguments += ' ' + ($ExperimentArguments -join ' ') }
$arguments += ' -zantetsuDistStatistics ' + $Statistics
if ($inputTargets.Count -gt 0) {
    $inputManifest = Join-Path $record 'input-sources.tsv'
    $arguments += ' -zantetsuDistInputSources "' + $inputManifest + '" -zantetsuDistInputSourcesAside "' + $inputAside + '"'
}
$head =git -C $project log --format='%H %s' -1
$dirty = @(git -C $project status --short --untracked-files=all)
@(
    "distribution build begun $(Get-Date -Format o)",
    "package $package; versionName $VersionName; versionCode $VersionCode; supported devices $supportedDevices",
    "keystore $Keystore; alias $KeyAlias; certificate sha256 $certificateSha256; $validity",
    "source $head",
    "public check before the build: exit $hygieneExit; $hygieneSummary",
    "uncommitted changes: $($dirty.Count)",
    "conditions: Release (no development option), commercial (-zantetsuBuildMode commercial), custom keystore, Android ARM64 IL2CPP Vulkan (the shared Quest build)",
    "experiment arguments: $(if ($ExperimentArguments.Count -gt 0) { $ExperimentArguments -join ' ' } else { 'none' })",
    "play statistics: $Statistics$(if ($Statistics -eq 'required') { "; token from $StatisticsTokenVariable; endpoint $statisticsEndpoint; role external; configurations moved aside to $StatisticsAside" }) (the token's value is never recorded)",
    "unity $Unity",
    "command $arguments"
) | Set-Content -LiteralPath (Join-Path $record 'build.txt') -Encoding UTF8
if ($dirty.Count -gt 0) {
    $dirty | Set-Content -LiteralPath (Join-Path $record 'status.txt') -Encoding UTF8
    # git's warnings on its error stream (such as a CRLF note) are not failures of the diff: read as data, exit code decides.
    $previousPreference = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $patch = @(git -C $project diff 2>$null); $diffExit = $LASTEXITCODE } finally { $ErrorActionPreference = $previousPreference }
    if ($diffExit -ne 0) { throw "git diff of the working tree failed (exit $diffExit)" }
    $patch | Out-File -LiteralPath (Join-Path $record 'working-tree.patch') -Encoding utf8
}
$exit = -1
$buildText = Join-Path $record 'build.txt'
$statisticsAsideFailure = $null
$inputSourceFailure = $null
$settingsFailures = @()
try {
    if ($inputTargets.Count -gt 0) {
        # Each original copied aside, then its source written empty; the Editor imports and checks them before the build.
        Set-BlankInputSources -Project $project -Targets $inputTargets -Aside $inputAside -Manifest $inputManifest
        "input sources: $($inputTargets.Count) licensed inputs blanked for the build (originals aside in $inputAside; manifest $inputManifest)" | Add-Content -LiteralPath $buildText -Encoding UTF8
    }
    if ($Statistics -eq 'required') {
        # A configuration already in the tree is kept, moved aside (never overwritten, updated or deleted); then the
        # distribution's own is made from the environment.
        $kept = @(& $statisticsScript -Project $project -Remove -AsideDirectory (Join-Path $StatisticsAside 'preexisting') -Unity $Unity)
        "statistics config before the build: $($kept -join ' ')" | Add-Content -LiteralPath $buildText -Encoding UTF8
        $madeLines = @(& $statisticsScript -Project $project -Endpoint $statisticsEndpoint -Role external -TokenVariable $StatisticsTokenVariable -AsideDirectory (Join-Path $StatisticsAside 'failed') -Unity $Unity)
        "statistics config made: $($madeLines[0])" | Add-Content -LiteralPath $buildText -Encoding UTF8
    }
    $process = Start-Process -FilePath $Unity -ArgumentList $arguments -WorkingDirectory $project -WindowStyle Hidden -PassThru
    $handle = $process.Handle
    $process.WaitForExit()
    $exit = $process.ExitCode
}
finally {
    if ($Statistics -eq 'required') {
        # Whatever happened: the configuration made for this build leaves the tree (moved aside, not deleted).
        # A failure here is recorded now and makes the whole run fail after the other settings are put back below
        # (TL review 2026-10-18 [P2]): a build whose token stayed in the tree never ends as a success.
        try {
            $moved = @(& $statisticsScript -Project $project -Remove -AsideDirectory (Join-Path $StatisticsAside 'used') -Unity $Unity)
            "statistics config after the build: $($moved -join ' ')" | Add-Content -LiteralPath $buildText -Encoding UTF8
        } catch {
            $statisticsAsideFailure = "the statistics config could NOT be moved aside after the build: $($_.Exception.Message)"
            $statisticsAsideFailure | Add-Content -LiteralPath $buildText -Encoding UTF8
        }
        $left = @($statisticsAsset, "$statisticsAsset.meta" | Where-Object { Test-Path -LiteralPath $_ })
        "statistics config in the tree after the build: $(if ($left.Count -eq 0) { 'none' } else { 'STILL THERE: ' + ($left -join ', ') })" | Add-Content -LiteralPath $buildText -Encoding UTF8
        if ($left.Count -gt 0 -and !$statisticsAsideFailure) { $statisticsAsideFailure = "the statistics config is still in the tree after the build: $($left -join ', ')" }
    }
    if ($inputManifest -and (Test-Path -LiteralPath $inputManifest)) {
        # Whatever happened: every original back (its hash checked), then Unity's import of them checked again, so the
        # tree and its import are the originals' after the run. Any failure fails the run (below).
        # An exception here (the put-back, the check's Unity not starting, the log's copy) is kept as the failure, never
        # thrown past the settings files' put-back below.
        try {
            $putBack = @(Restore-InputSources -Project $project -Aside $inputAside -Manifest $inputManifest)
            if ($putBack.Count -gt 0) {
                $inputSourceFailure = "the licensed inputs were NOT all put back: $($putBack -join '; ')"
            } else {
                $checkLog = Join-Path $logs "input-sources-original-$VersionName-$VersionCode.log"
                $checkExit = Invoke-InputSourceCheck -Unity $Unity -Project $project -Manifest $inputManifest -Aside $inputAside -Expect original -Log $checkLog
                if ($checkExit -ne 0) { $inputSourceFailure = "the licensed inputs are back, but Unity's import of them is not the originals' (check exit $checkExit; see $checkLog)" }
                try { if (Test-Path -LiteralPath $checkLog) { Copy-Item -LiteralPath $checkLog -Destination (Join-Path $record 'input-sources-original.log') } }
                catch { "the import check's log could not be copied into the record: $($_.Exception.Message)" | Add-Content -LiteralPath $buildText -Encoding UTF8 }
            }
        } catch {
            $inputSourceFailure = "the licensed inputs' put-back or its import check did not complete: $($_.Exception.Message)"
        }
        try { "input sources after the build: $(if ($inputSourceFailure) { $inputSourceFailure } else { 'every original back (hash) and imported again as the original (Unity check)' })" | Add-Content -LiteralPath $buildText -Encoding UTF8 } catch { }
    }
    # Each settings file put back on its own: a failure with one is kept and the others are still tried.
    foreach ($w in $watched) {
        try {
            $after = Get-Content -LiteralPath $w.Path -Raw
            $before = Get-Content -LiteralPath $w.Saved -Raw
            if ($after -ne $before) {
                Copy-Item -LiteralPath $w.Path -Destination $w.After
                Copy-Item -LiteralPath $w.Saved -Destination $w.Path -Force
                "$($w.Name) differed after Unity and was put back from the saved copy" | Add-Content -LiteralPath (Join-Path $record 'build.txt') -Encoding UTF8
            } else {
                "$($w.Name) unchanged after Unity" | Add-Content -LiteralPath (Join-Path $record 'build.txt') -Encoding UTF8
            }
        } catch {
            $settingsFailures += "$($w.Name) could NOT be compared or put back: $($_.Exception.Message)"
        }
    }
    try {
        foreach ($s in $settingsFailures) { $s | Add-Content -LiteralPath (Join-Path $record 'build.txt') -Encoding UTF8 }
        "unity exit $exit; ended $(Get-Date -Format o)" | Add-Content -LiteralPath (Join-Path $record 'build.txt') -Encoding UTF8
        if (Test-Path -LiteralPath $log) { Copy-Item -LiteralPath $log -Destination (Join-Path $record 'build.log') }
    } catch { $settingsFailures += "the end of the record could not be written: $($_.Exception.Message)" }
}
if ($settingsFailures.Count -gt 0) {
    throw "the settings were not all put back or recorded: $($settingsFailures -join '; ') (unity exit $exit; see $buildText)"
}
# Before anything about the build itself: whatever Unity did, a token left in the tree fails the run.
if ($statisticsAsideFailure) {
    "RESULT: failed -- $statisticsAsideFailure" | Add-Content -LiteralPath $buildText -Encoding UTF8
    throw "$statisticsAsideFailure (unity exit $exit; the APK, if any, is not to be used before the tree is clean; see $buildText)"
}
if ($inputSourceFailure) {
    "RESULT: failed -- $inputSourceFailure" | Add-Content -LiteralPath $buildText -Encoding UTF8
    throw "$inputSourceFailure (unity exit $exit; the APK, if any, is not to be used before the tree and its import are the originals'; see $buildText)"
}
if ($exit -eq 2) { throw "the request was refused by the Editor before the build (see ${log}: QUEST DISTRIBUTION BUILD refused)" }
if ($exit -ne 0) { throw "Unity failed ($exit); see $log" }
if (!(Test-Path -LiteralPath $Output)) { throw "Unity exited 0 but the APK is not there: $Output" }

# 3. The APK itself.
$apkSha256 = (Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash.ToLowerInvariant()
"apk sha256 $apkSha256; bytes $((Get-Item -LiteralPath $Output).Length)" | Add-Content -LiteralPath (Join-Path $record 'build.txt') -Encoding UTF8
& (Join-Path $PSScriptRoot 'Inspect-DistributionApk.ps1') -Apk $Output -ExpectedPackage $package -ExpectedVersionName $VersionName -ExpectedVersionCode $VersionCode -ExpectedCertificateSha256 $certificateSha256 -ExpectedSupportedDevices $supportedDevices -ExpectedStatistics $Statistics -ForbiddenWordsFile $ForbiddenWordsFile -Record (Join-Path $record 'inspection.txt') -ManifestDump (Join-Path $record 'manifest.txt') -BadgingDump (Join-Path $record 'badging.txt') -AndroidPlayer $AndroidPlayer
if ($LASTEXITCODE -ne 0) { throw "the APK failed its inspection (see $record\inspection.txt)" }
"distribution build of $package $VersionName ($VersionCode): $Output"
"apk sha256 $apkSha256"
"certificate sha256 $certificateSha256"
"record $record"
