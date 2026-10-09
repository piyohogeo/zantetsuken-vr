#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('vp3c', 'vp3c-native')][string]$Mode = 'vp3c-native',
    [string]$Apk,
    [int]$InputStart = 2227,
    [ValidateRange(1, 4096)][int]$InputRows = 512,
    [ValidateRange(30, 1800)][int]$BudgetSeconds = 565,
    [string]$ScriptFile,
    [ValidateRange(0, 120)][float]$StartDelaySeconds = 5,
    [switch]$AppSwOff,
    [ValidateSet('camera', 'tracking-origin')][string]$AppSpacePoseMode = 'tracking-origin',
    [ValidateSet(-1, 0, 30, 60)][int]$AppSwFarMeters = 30,
    [ValidateSet('off', 'capture', 'reuse')][string]$AppSwFixedView = 'off',
    [switch]$NoProfiler,
    [switch]$NoPrewarm,
    [switch]$QuitWhenDone,
    [switch]$LegacyNativeColour,
    [string]$Serial = '340YC10G7D0FZ9',
    [string]$Adb = 'C:\Program Files\RenderDocForMetaQuest\plugins\android\adb.exe'
)
$ErrorActionPreference = 'Stop'
$package = 'com.zantetsuken.quest.citywalk'
$deviceFiles = "/sdcard/Android/data/$package/files"
function Invoke-Adb {
    & $Adb -s $Serial @args
    if ($LASTEXITCODE -ne 0) { throw "adb failed: $args" }
}
if ($Apk) {
    $deviceApk = '/data/local/tmp/quest-citywalk.apk'
    try {
        Invoke-Adb push ([IO.Path]::GetFullPath($Apk)) $deviceApk | Out-Null
        Invoke-Adb shell pm install -r -g $deviceApk
    } finally { Invoke-Adb shell rm -f $deviceApk }
}
Invoke-Adb shell am force-stop $package
Invoke-Adb shell mkdir -p $deviceFiles
$config = @{ mode = $Mode; appSw = !$AppSwOff; profiler = !$NoProfiler; keepOpen = !$QuitWhenDone;
    inputStart = $InputStart; inputRows = $InputRows; startDelaySeconds = $StartDelaySeconds;
    nativeColourPreparation = !$LegacyNativeColour; prewarm = !$NoPrewarm; appSpacePoseMode = $AppSpacePoseMode; cityWalkBudgetSeconds = $BudgetSeconds;
    appSwDistanceExperiment = ($AppSwFarMeters -ge 0 -and !$AppSwOff -and $Mode -eq 'vp3c-native'); appSwFarMeters = [Math]::Max(0, $AppSwFarMeters); appSwFixedView = $AppSwFixedView }
if ($ScriptFile) { Invoke-Adb push ([IO.Path]::GetFullPath($ScriptFile)) "$deviceFiles/CityWalkOverride.txt"; $config.cityWalkScriptFile = "$deviceFiles/CityWalkOverride.txt" }
$configFile = Join-Path ([IO.Path]::GetTempPath()) ('quest-citywalk-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($configFile, ($config | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    Invoke-Adb push $configFile "$deviceFiles/quest3s-config.json"
} finally { Remove-Item -LiteralPath $configFile -ErrorAction SilentlyContinue }
Invoke-Adb shell "am start -a android.intent.action.MAIN -n $package/com.unity3d.player.UnityPlayerGameActivity -e unity '-platform-android-unitymain-affinity 0x3c'"
Write-Output "Quest City Walk: mode=$Mode, AppSW=$(!$AppSwOff), profiler=$(!$NoProfiler), prewarm=$(!$NoPrewarm), AppSpace=$AppSpacePoseMode, AppSWfar=$AppSwFarMeters, fixedView=$AppSwFixedView."
Write-Output "Pull profiles and results from $deviceFiles/runs. Check QUEST CITY WALK, QUEST THREAD, PROP SLASH and VrApi."
