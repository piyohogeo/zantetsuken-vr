#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('vp3c', 'vp3c-native')][string]$Mode = 'vp3c-native',
    [string]$Apk,
    [int]$InputStart = 2227,
    [ValidateRange(1, 4096)][int]$InputRows = 512,
    [ValidateRange(0, 120)][float]$StartDelaySeconds = 5,
    [switch]$AppSwOff,
    [ValidateSet('camera', 'tracking-origin')][string]$AppSpacePoseMode = 'tracking-origin',
    [ValidateSet(-1, 0, 30, 60)][int]$AppSwFarMeters = 30,
    [ValidateSet('off', 'capture', 'reuse')][string]$AppSwFixedView = 'off',
    [switch]$NoProfiler,
    [switch]$NoPrewarm,
    [switch]$QuitWhenDone,
    [switch]$LegacyNativeColour,
    [string]$Package = 'com.junic.zantetsu.questnpcslash',   # the APK that runs it: this scenario's own, or the one of every scenario (com.junic.zantetsu.quest)
    [string]$Serial = '340YC10G7D0FZ9',
    [string]$Adb = 'C:\Program Files\RenderDocForMetaQuest\plugins\android\adb.exe'
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
$config = @{ mode = $Mode; appSw = !$AppSwOff; profiler = !$NoProfiler; keepOpen = !$QuitWhenDone;
    inputStart = $InputStart; inputRows = $InputRows; startDelaySeconds = $StartDelaySeconds;
    nativeColourPreparation = !$LegacyNativeColour; prewarm = !$NoPrewarm; appSpacePoseMode = $AppSpacePoseMode;
    appSwDistanceExperiment = ($AppSwFarMeters -ge 0 -and !$AppSwOff -and $Mode -eq 'vp3c-native'); appSwFarMeters = [Math]::Max(0, $AppSwFarMeters); appSwFixedView = $AppSwFixedView }
# DESIGN 3.4: the launch names its scenario; a Player given none runs play.
$config.scenario = 'npc-slash'
$configFile = Join-Path ([IO.Path]::GetTempPath()) ('quest-npc-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($configFile, ($config | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    Invoke-Adb push $configFile "$deviceFiles/quest3s-config.json"
} finally { Remove-Item -LiteralPath $configFile -ErrorAction SilentlyContinue }
Invoke-Adb shell "am start -a android.intent.action.MAIN -n $package/com.unity3d.player.UnityPlayerGameActivity -e unity '-platform-android-unitymain-affinity 0x3c'"
Write-Output "Quest NPC Slash: mode=$Mode, AppSW=$(!$AppSwOff), profiler=$(!$NoProfiler), prewarm=$(!$NoPrewarm), AppSpace=$AppSpacePoseMode, AppSWfar=$AppSwFarMeters, fixedView=$AppSwFixedView."
Write-Output "Pull profiles and results from $deviceFiles/runs. Check QUEST NPC, QUEST THREAD, PROP SLASH and VrApi."
