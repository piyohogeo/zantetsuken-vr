#Requires -Version 5.1
<#
.SYNOPSIS
  One launch of the NPC skin benchmark on the Quest (DESIGN 3.4, npc-skin-bench): the configuration pushed, the APK
  started with the skin route in the intent's unity extra. The results land under the app's files:
  /sdcard/Android/data/com.zantetsuken.quest.npcskinbench/files/runs/<utc>-skinbench-<route>-<id>/ (bench-frames.csv,
  bench-result.json/.txt, picture-<route>.png, quest-platform.txt with the AppSW state). Pull them with adb afterwards.
  The measurement is -MeasureFrames frames (900: 20 s at 45 Hz) after the pose clock's resynchronisation and -SettleFrames
  (SKIN-BENCH-2); -FixedView holds the eye's pose for every pass (the rig counter-moves the head).
.EXAMPLE
  .\Run-NpcSkinBench.ps1 -Apk <apk> -Route skin24 -Count 64 -Pose update -Shadows cascade1 -FixedView
#>
[CmdletBinding()]
param(
    [ValidateSet('vp3c', 'vp3c-native')][string]$Mode = 'vp3c-native',
    [string]$Apk,
    [Parameter(Mandatory = $true)][ValidateSet('smr', 'skin24')][string]$Route,
    [ValidateRange(1, 512)][int]$Count = 64,
    [ValidateSet('stop', 'update')][string]$Pose = 'update',
    [ValidateSet('off', 'cascade1', 'cascade4')][string]$Shadows = 'cascade4',
    [ValidateRange(1, 64)][int]$Families = 8,
    [double]$WarmupSeconds = 10,
    [double]$MeasureSeconds = 20,
    [ValidateRange(0, 1000000)][int]$MeasureFrames = 900,
    [ValidateRange(0, 100000)][int]$SettleFrames = 30,
    [double]$PoseToleranceMetres = 0.01,
    [double]$PoseToleranceDegrees = 1,
    [switch]$FixedView,
    [ValidateRange(0, 1000000)][int]$PaletteJoinGap = 0,
    [ValidateSet('sync', 'worker')][string]$Gather = 'sync',  # SKIN-BENCH-3: the palette gathering (configuration skinGather and -zantetsuSkinGather)
    [ValidateRange(0, 100000)][int]$WorkerFrom = 0,           # SKIN-BENCH-3: dirty individuals from which the worker gathers (configuration skinGatherWorkerFrom)
    [switch]$Skin24Slice,                                     # SKIN-BENCH-3: the skin draw's casters select by slice (configuration skin24SliceSelection and -zantetsuSkin24SliceSelection)
    [ValidateRange(-1, 512)][int]$UpdateCount = -1,           # SKIN-BENCH-3: individuals posed each frame under update
    [switch]$AppSwOff,
    [switch]$Profiler,              # diagnosis: the Profiler's binary log of the measurement (not a sample)
    [switch]$NoPicture,
    [switch]$QuitWhenDone,
    [string]$Package = 'com.zantetsuken.quest.npcskinbench',
    [string]$Serial = '340YC10G7D0FZ9',
    [string]$Adb = 'C:\Program Files\RenderDocForMetaQuest\plugins\android\adb.exe'
)
$ErrorActionPreference = 'Stop'
$deviceFiles = "/sdcard/Android/data/$Package/files"
function Invoke-Adb {
    & $Adb -s $Serial @args
    if ($LASTEXITCODE -ne 0) { throw "adb failed: $args" }
}
if ($Apk) {
    $deviceApk = '/data/local/tmp/quest-npcskinbench.apk'
    try {
        Invoke-Adb push ([IO.Path]::GetFullPath($Apk)) $deviceApk | Out-Null
        Invoke-Adb shell pm install -r -g $deviceApk
    } finally { Invoke-Adb shell rm -f $deviceApk }
}
Invoke-Adb shell am force-stop $Package
Invoke-Adb shell mkdir -p $deviceFiles
# DESIGN 3.4: the launch names its scenario; the benchmark's own fields beside the common ones (mode, AppSW).
$config = @{ scenario = 'npc-skin-bench'; mode = $Mode; appSw = !$AppSwOff; appSpacePoseMode = 'tracking-origin'; keepOpen = !$QuitWhenDone
    skinRoute = $Route; npcSkinBenchCount = $Count; npcSkinBenchPose = $Pose; npcSkinBenchShadows = $Shadows; npcSkinBenchFamilies = $Families
    npcSkinBenchWarmupSeconds = $WarmupSeconds; npcSkinBenchMeasureSeconds = $MeasureSeconds; npcSkinBenchMeasureFrames = $MeasureFrames; npcSkinBenchSettleFrames = $SettleFrames
    npcSkinBenchPoseToleranceMetres = $PoseToleranceMetres; npcSkinBenchPoseToleranceDegrees = $PoseToleranceDegrees; npcSkinBenchFixedView = [bool]$FixedView
    npcSkinBenchPaletteJoinGap = $PaletteJoinGap; npcSkinBenchProfiler = [bool]$Profiler; npcSkinBenchPicture = !$NoPicture
    skinGather = $Gather; skinGatherWorkerFrom = $WorkerFrom; skin24SliceSelection = [bool]$Skin24Slice; npcSkinBenchUpdateCount = $UpdateCount }
$configFile = Join-Path ([IO.Path]::GetTempPath()) ('quest-npcskinbench-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($configFile, ($config | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    Invoke-Adb push $configFile "$deviceFiles/quest3s-config.json"
} finally { Remove-Item -LiteralPath $configFile -ErrorAction SilentlyContinue }
# The skin route also in the intent's unity extra (the Player's own arguments; the configuration's skinRoute selects the same).
$unityArguments = '-platform-android-unitymain-affinity 0x3c ' + $(if ($Route -eq 'skin24') { '-zantetsuSkinDraw' } else { '-zantetsuSkinUnity' }) + " -zantetsuSkinGather $Gather" + $(if ($Skin24Slice) { ' -zantetsuSkin24SliceSelection' } else { '' })
Invoke-Adb shell "am start -a android.intent.action.MAIN -n $Package/com.unity3d.player.UnityPlayerGameActivity -e unity '$unityArguments'"
Write-Output "Quest NPC skin bench: route=$Route, mode=$Mode, AppSW=$(!$AppSwOff), count=$Count, pose=$Pose, shadows=$Shadows, families=$Families, warmup=$WarmupSeconds s, measure=$MeasureFrames frames (settle $SettleFrames), fixedView=$([bool]$FixedView), joinGap=$PaletteJoinGap, gather=$Gather, skin24Slice=$([bool]$Skin24Slice), updateCount=$UpdateCount, profiler=$([bool]$Profiler)."
Write-Output "Results: $deviceFiles/runs/<utc>-skinbench-$Route-<id>/ (bench-result.txt first). Check the launch record 'command line:', 'VP SKIN ROUTE:', 'NPC SKIN BENCH:' and 'VP BODY ROUTE (end)' (taken = issued for ordinary and skin bodies and motion) in logcat."
