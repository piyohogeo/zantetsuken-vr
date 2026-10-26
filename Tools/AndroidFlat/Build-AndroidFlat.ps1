#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Development,
    [switch]$SkipConfigure,
    [switch]$ShaderAudit,
    [string]$Output,
    [string]$SourceScene,
    # Omitted: retain the existing validation-build behaviour. Required: make a temporary send configuration.
    [ValidateSet('', 'required')][string]$Statistics = '',
    [ValidateSet('developer', 'external')][string]$StatisticsRole = 'developer',
    [string]$StatisticsTokenVariable = 'ZANTETSU_INGEST_TOKEN',
    [Alias('AsideDirectory')][string]$StatisticsAside,
    [string]$StatisticsEndpoint = 'https://zantetsuken-telemetry-api.hogehogeo.workers.dev/v1/telemetry',
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$Output) { $Output = Join-Path $project ('Build\android-flat' + $(if ($Development) { '-dev' }) + '.apk') }
$Output = [IO.Path]::GetFullPath($Output)
$logs = Join-Path $project 'Logs\android-flat'
[IO.Directory]::CreateDirectory($logs) | Out-Null
if (!(Test-Path -LiteralPath $Unity -PathType Leaf)) { throw "Unity not found: $Unity" }
$statisticsScript = Join-Path $PSScriptRoot '..\Quest3s\Set-StatisticsConfig.ps1'
$statisticsAsset = Join-Path $project 'Assets\Zantetsu\Runtime\Sandbox\Resources\ZantetsuPlayStatisticsConfig.asset'
$statisticsRecord = Join-Path $logs 'statistics-build.txt'
if ($Statistics -eq 'required') {
    if (!$StatisticsAside) { throw '-Statistics required needs -StatisticsAside (a folder outside the project)' }
    $StatisticsAside = [IO.Path]::GetFullPath($StatisticsAside).TrimEnd('\')
    $projectRoot = $project.TrimEnd('\')
    if ($StatisticsAside.Equals($projectRoot, [StringComparison]::OrdinalIgnoreCase) -or $StatisticsAside.StartsWith($projectRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "-StatisticsAside must be outside the project: $StatisticsAside" }
    foreach ($path in $statisticsAsset, "$statisticsAsset.meta") {
        if (Test-Path -LiteralPath $path) { throw "a statistics config already exists ($path): keep it unchanged; move it aside explicitly before a required build" }
    }
    if (!(Test-Path -LiteralPath $statisticsScript -PathType Leaf)) { throw "statistics configuration helper not found: $statisticsScript" }
    if ($StatisticsEndpoint -notmatch '^https://') { throw "the statistics endpoint must be https: $StatisticsEndpoint" }
    $hasToken = -not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($StatisticsTokenVariable)) -or -not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($StatisticsTokenVariable, 'User'))
    if (!$hasToken) { throw "the environment variable $StatisticsTokenVariable is not set (process or User); no token is passed on a command line" }
    "statistics required: send on; role $StatisticsRole; endpoint $StatisticsEndpoint; token from $StatisticsTokenVariable (value never recorded)" | Tee-Object -FilePath $statisticsRecord
}
$method = if ($SkipConfigure) { 'BuildApk' } else { 'ConfigureAndBuild' }
$extra = ' -zantetsuAndroidFlat -zantetsuAndroidFlatOut "' + $Output + '"'
if ($Development) { $extra += ' -zantetsuAndroidFlatDev' }
if ($ShaderAudit) { $extra += ' -zantetsuAndroidFlatShaderAudit' }
if ($SourceScene) { $extra += ' -zantetsuAndroidFlatSourceScene "' + $SourceScene + '"' }
$log = Join-Path $logs 'build.log'
$arguments = '-batchmode -quit -force-d3d11 -projectPath "' + $project + '" -buildTarget Android -executeMethod Zantetsu.EditorTools.AndroidFlat.AndroidFlatBuild.' + $method + ' -logFile "' + $log + '"' + $extra
# Reuse the Android native plugin and Vulkan shader assets (the bake includes both mono and stereo variants).
# If their sources changed, build Native/ZantetsuVpNative/Build-Vulkan.ps1 and VpNativeShaderBake.VulkanBatch first.
$statisticsMade = $false
$buildFailure = $null
$statisticsAsideFailure = $null
try {
    if ($Statistics -eq 'required') {
        # The helper reads the named token from the environment, passes it only to its child, and cleans up failed creation.
        $madeLines = @(& $statisticsScript -Project $project -Endpoint $StatisticsEndpoint -Role $StatisticsRole -TokenVariable $StatisticsTokenVariable -AsideDirectory (Join-Path $StatisticsAside 'failed') -Unity $Unity)
        $statisticsMade = $true
        $madeLine = @($madeLines | Where-Object { $_ -like 'statistics config made in *' }) | Select-Object -Last 1
        if (!$madeLine -or $madeLine -notmatch 'token present' -or $madeLine -notmatch ', send on, role ' -or $madeLine -notmatch ('role ' + [regex]::Escape($StatisticsRole) + '$') -or $madeLine -notmatch ('endpoint ' + [regex]::Escape($StatisticsEndpoint) + ',')) {
            throw 'the required statistics configuration did not pass the endpoint/token/send/role checks'
        }
        foreach ($path in $statisticsAsset, "$statisticsAsset.meta") { if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "the required statistics configuration is missing: $path" } }
        $madeLine | Tee-Object -FilePath $statisticsRecord -Append
    }
    $process = Start-Process -FilePath $Unity -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $processHandle = $process.Handle
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Unity failed ($($process.ExitCode)); see $log" }
    if (!(Test-Path -LiteralPath $Output -PathType Leaf)) { throw "Unity reported success but no APK exists: $Output" }
}
catch { $buildFailure = $_ }
finally {
    if ($statisticsMade) {
        # Only this run's configuration is moved, never a preexisting asset. A cleanup failure also fails the build.
        try {
            $moved = @(& $statisticsScript -Project $project -Remove -AsideDirectory (Join-Path $StatisticsAside 'used') -Unity $Unity)
            $moved | Tee-Object -FilePath $statisticsRecord -Append
            foreach ($path in $statisticsAsset, "$statisticsAsset.meta") { if (Test-Path -LiteralPath $path) { throw "statistics configuration is still in the project: $path" } }
        }
        catch {
            $statisticsAsideFailure = "the statistics configuration could not be moved aside after the build: $($_.Exception.Message)"
            $statisticsAsideFailure | Tee-Object -FilePath $statisticsRecord -Append
        }
    }
}
if ($statisticsAsideFailure) { throw $statisticsAsideFailure }
if ($buildFailure) { throw $buildFailure }
$androidPlayer = Join-Path ([IO.Path]::GetDirectoryName($Unity)) 'Data\PlaybackEngines\AndroidPlayer'
& (Join-Path $PSScriptRoot 'Inspect-AndroidFlatApk.ps1') -Apk $Output -AndroidPlayer $androidPlayer -Record (Join-Path $logs 'apk-inspection.txt')
if ($LASTEXITCODE -ne 0) { throw "Android phone APK inspection failed; see $logs" }
Get-FileHash -LiteralPath $Output -Algorithm SHA256
