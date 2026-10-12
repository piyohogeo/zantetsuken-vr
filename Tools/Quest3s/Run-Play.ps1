#Requires -Version 5.1
# Play on the Quest (DESIGN 3.4): the scene run as it is, with a person's own movement, turning and katana. No script,
# no measurement, no ending. The configuration names the scenario and the draw route's mode and nothing of a run.
[CmdletBinding()]
param(
    [ValidateSet('vp3c', 'vp3c-native')][string]$Mode = 'vp3c-native',
    [string]$Apk,
    [switch]$AppSwOff,
    [ValidateSet('camera', 'tracking-origin')][string]$AppSpacePoseMode = 'tracking-origin',
    [switch]$FixedCommands,        # the way back from the command compaction (DESIGN 4.5.10, D-217)
    [string]$Package = 'com.junic.zantetsu.quest',   # the APK of every scenario; a one-scene APK with a cut world also plays
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
# The distance experiment of the scripted runs is theirs: play asks for AppSW alone.
$config = @{ scenario = 'play'; mode = $Mode; appSw = !$AppSwOff; appSpacePoseMode = $AppSpacePoseMode;
    appSwDistanceExperiment = $false; nativeIndirectCount = !$FixedCommands }
$configFile = Join-Path ([IO.Path]::GetTempPath()) ('quest-play-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($configFile, ($config | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    Invoke-Adb push $configFile "$deviceFiles/quest3s-config.json"
} finally { Remove-Item -LiteralPath $configFile -ErrorAction SilentlyContinue }
Invoke-Adb shell "am start -a android.intent.action.MAIN -n $package/com.unity3d.player.UnityPlayerGameActivity -e unity '-platform-android-unitymain-affinity 0x3c'"
Write-Output "Quest play: mode=$Mode, AppSW=$(!$AppSwOff), AppSpace=$AppSpacePoseMode, commandCompaction=$(!$FixedCommands), package=$package."
Write-Output 'Check ZANTETSU SCENARIO, QUEST PLAY and ZANTETSU MENU in logcat. The left controller menu button opens the pause menu.'
