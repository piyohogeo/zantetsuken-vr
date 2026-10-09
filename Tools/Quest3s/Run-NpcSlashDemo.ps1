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
    [switch]$NoProfiler,
    [switch]$NoPrewarm,
    [switch]$QuitWhenDone,
    [switch]$LegacyNativeColour,
    [string]$Serial = '340YC10G7D0FZ9',
    [string]$Adb = 'C:\Program Files\RenderDocForMetaQuest\plugins\android\adb.exe'
)
$ErrorActionPreference = 'Stop'
$package = 'com.zantetsuken.quest.npcslash'
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
    nativeColourPreparation = !$LegacyNativeColour; prewarm = !$NoPrewarm; appSpacePoseMode = $AppSpacePoseMode }
$configFile = Join-Path ([IO.Path]::GetTempPath()) ('quest-npc-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($configFile, ($config | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    Invoke-Adb push $configFile "$deviceFiles/quest3s-config.json"
} finally { Remove-Item -LiteralPath $configFile -ErrorAction SilentlyContinue }
Invoke-Adb shell "am start -a android.intent.action.MAIN -n $package/com.unity3d.player.UnityPlayerGameActivity -e unity '-platform-android-unitymain-affinity 0x3c'"
Write-Output "Quest NPC Slash: mode=$Mode, AppSW=$(!$AppSwOff), profiler=$(!$NoProfiler), prewarm=$(!$NoPrewarm), AppSpace=$AppSpacePoseMode."
Write-Output "Pull profiles and results from $deviceFiles/runs. Check QUEST NPC, QUEST THREAD, PROP SLASH and VrApi."
