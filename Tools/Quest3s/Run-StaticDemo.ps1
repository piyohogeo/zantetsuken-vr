#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('unity', 'vp3c', 'vp3c-native')][string]$Mode = 'vp3c',
    [string]$Apk,
    [ValidateSet('head', 'path')][string]$CameraMode = 'head',
    [string]$PathFile,
    [string]$GeometryFile,
    [int]$WarmupLaps = 1,
    [switch]$Profiler,
    [switch]$AppSwOff,
    [switch]$KeepOpen,
    [switch]$LegacyNativeColour,
    [switch]$DirectClipProbe,
    [string]$Serial = '340YC10G7D0FZ9',
    [string]$Adb = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
)
$ErrorActionPreference = 'Stop'
$package = 'com.junic.zantetsu.queststatic'
$deviceFiles = "/sdcard/Android/data/$package/files"
function Invoke-Adb {
    & $Adb -s $Serial @args
    if ($LASTEXITCODE -ne 0) { throw "adb failed: $args" }
}
if ($Apk) { Invoke-Adb install -r -g $Apk }
Invoke-Adb shell am force-stop $package
Invoke-Adb shell mkdir -p $deviceFiles
$config = @{ mode = $Mode; warmupLaps = $WarmupLaps; appSw = !$AppSwOff; profiler = [bool]$Profiler; quitWhenDone = (!$KeepOpen -and $CameraMode -eq 'path'); headTracked = ($CameraMode -eq 'head') }
if ($PathFile) { Invoke-Adb push $PathFile "$deviceFiles/compare-path.txt"; $config.pathFile = "$deviceFiles/compare-path.txt" }
if ($GeometryFile) { Invoke-Adb push $GeometryFile "$deviceFiles/override-geometry.vpdc"; $config.geometryFile = "$deviceFiles/override-geometry.vpdc" }
$config.nativeColourPreparation = !$LegacyNativeColour
$config.directClipProbe = [bool]$DirectClipProbe
$configFile = Join-Path ([IO.Path]::GetTempPath()) ('quest-static-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($configFile, ($config | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    Invoke-Adb push $configFile "$deviceFiles/quest3s-config.json"
} finally { Remove-Item -LiteralPath $configFile -ErrorAction SilentlyContinue }
# Quest 3S measured CPU capacities: cores 0-1=848; cores 2-5=1024 (2026-10-08). Use Unity's official mask setting.
Invoke-Adb shell "am start -a android.intent.action.MAIN -n $package/com.unity3d.player.UnityPlayerGameActivity -e unity '-platform-android-unitymain-affinity 0x3c'"
Write-Output "Run $Mode (camera=$CameraMode) started. After completion: adb -s $Serial pull $deviceFiles/runs <output-folder>"
Write-Output 'Verify QUEST THREAD, QUEST STATIC and DRAW COMPARE in logcat; VrApi must report ASW=90,Type=App for AppSW.'
