#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('unity', 'vp3c', 'vp3c-native')][string]$Mode = 'vp3c',
    [string]$Apk,
    [ValidateSet('head', 'path')][string]$CameraMode = 'head',
    [string]$PathFile,
    [string]$GeometryFile,
    [int]$WarmupLaps = 1,
    [ValidateRange(0, 2147483647)][int]$SceneObjectLimit = 0,
    [switch]$Profiler,
    [switch]$AppSwOff,
    [switch]$KeepOpen,
    [switch]$LegacyNativeColour,
    [switch]$DirectClipProbe,
    [switch]$LegacyClipColour,
    [switch]$LegacyClipMotion,
    [switch]$LegacyClipShadows,
    [switch]$NoConstantSharing,
    [switch]$IndirectCount,          # accepted and needs no saying: the command compaction is the default (DESIGN 4.5.10, D-217)
    [switch]$FixedCommands,          # the way back: the fixed-count multi-draw
    [switch]$IndirectCountSelfTest,  # diagnosis: the wave kernel's self-test with a readback
    [switch]$IndirectCountVerify,
    [switch]$IndirectCountDrill,
    [int]$IndirectCountFirstSlot = 0, # diagnosis (2026-10-10): the count slot a pass's first run takes (1 = the scene's one run from countBufferOffset 4)
    [string]$Package = 'com.zantetsuken.quest.static',   # the APK that runs it: this scenario's own, or the one of every scenario (com.zantetsuken.quest)
    [string]$Serial = '340YC10G7D0FZ9',
    [string]$Adb = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
)
$ErrorActionPreference = 'Stop'
$package = $Package
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
# DESIGN 3.4: the launch names its scenario; a Player given none runs play.
$config.scenario = 'static-wait'
$config.sceneObjectLimit = $SceneObjectLimit
$config.nativeColourPreparation = !$LegacyNativeColour
$config.directClipProbe = [bool]$DirectClipProbe
$config.nativeNoClipColour = !$LegacyClipColour
$config.nativeNoClipMotion = !$LegacyClipMotion
$config.nativeNoClipShadows = !$LegacyClipShadows
$config.nativeShareGpuConstants = !$NoConstantSharing
# DESIGN 4.5.10 (D-217): the GPU compaction of the visible commands + vkCmdDrawIndexedIndirectCount by default; -FixedCommands keeps the fixed draw.
if ($IndirectCount -and $FixedCommands) { throw '-IndirectCount and -FixedCommands exclude each other' }
$config.nativeIndirectCount = !$FixedCommands
$config.nativeIndirectCountSelfTest = [bool]$IndirectCountSelfTest
$config.nativeIndirectCountVerify = [bool]$IndirectCountVerify
$config.nativeIndirectCountDrill = [bool]$IndirectCountDrill
$config.nativeIndirectCountFirstSlot = $IndirectCountFirstSlot
$configFile = Join-Path ([IO.Path]::GetTempPath()) ('quest-static-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($configFile, ($config | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    Invoke-Adb push $configFile "$deviceFiles/quest3s-config.json"
} finally { Remove-Item -LiteralPath $configFile -ErrorAction SilentlyContinue }
# Quest 3S measured CPU capacities: cores 0-1=848; cores 2-5=1024 (2026-10-08). Use Unity's official mask setting.
Invoke-Adb shell "am start -a android.intent.action.MAIN -n $package/com.unity3d.player.UnityPlayerGameActivity -e unity '-platform-android-unitymain-affinity 0x3c'"
Write-Output "Run $Mode (camera=$CameraMode) started. After completion: adb -s $Serial pull $deviceFiles/runs <output-folder>"
Write-Output 'Verify QUEST THREAD, QUEST STATIC and DRAW COMPARE in logcat; VrApi must report ASW=90,Type=App for AppSW.'
