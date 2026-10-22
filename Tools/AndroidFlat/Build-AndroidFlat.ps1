#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Development,
    [switch]$SkipConfigure,
    [switch]$ShaderAudit,
    [string]$Output,
    [string]$SourceScene,
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$Output) { $Output = Join-Path $project ('Build\android-flat' + $(if ($Development) { '-dev' }) + '.apk') }
$Output = [IO.Path]::GetFullPath($Output)
$logs = Join-Path $project 'Logs\android-flat'
[IO.Directory]::CreateDirectory($logs) | Out-Null
if (!(Test-Path -LiteralPath $Unity -PathType Leaf)) { throw "Unity not found: $Unity" }
$method = if ($SkipConfigure) { 'BuildApk' } else { 'ConfigureAndBuild' }
$extra = ' -zantetsuAndroidFlat -zantetsuAndroidFlatOut "' + $Output + '"'
if ($Development) { $extra += ' -zantetsuAndroidFlatDev' }
if ($ShaderAudit) { $extra += ' -zantetsuAndroidFlatShaderAudit' }
if ($SourceScene) { $extra += ' -zantetsuAndroidFlatSourceScene "' + $SourceScene + '"' }
$log = Join-Path $logs 'build.log'
$arguments = '-batchmode -quit -force-d3d11 -projectPath "' + $project + '" -buildTarget Android -executeMethod Zantetsu.EditorTools.AndroidFlat.AndroidFlatBuild.' + $method + ' -logFile "' + $log + '"' + $extra
# Reuse the Android native plugin and Vulkan shader assets (the bake includes both mono and stereo variants).
# If their sources changed, build Native/ZantetsuVpNative/Build-Vulkan.ps1 and VpNativeShaderBake.VulkanBatch first.
$process = Start-Process -FilePath $Unity -ArgumentList $arguments -WindowStyle Hidden -PassThru
$processHandle = $process.Handle
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "Unity failed ($($process.ExitCode)); see $log" }
if (!(Test-Path -LiteralPath $Output -PathType Leaf)) { throw "Unity reported success but no APK exists: $Output" }
$androidPlayer = Join-Path ([IO.Path]::GetDirectoryName($Unity)) 'Data\PlaybackEngines\AndroidPlayer'
& (Join-Path $PSScriptRoot 'Inspect-AndroidFlatApk.ps1') -Apk $Output -AndroidPlayer $androidPlayer -Record (Join-Path $logs 'apk-inspection.txt')
if ($LASTEXITCODE -ne 0) { throw "Android phone APK inspection failed; see $logs" }
Get-FileHash -LiteralPath $Output -Algorithm SHA256
