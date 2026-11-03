#Requires -Version 5.1
<#
.SYNOPSIS
  One run of the GPU bake probe on the Quest (DESIGN 4.5.14, skin-bake-probe): the configuration pushed, the APK started,
  exit 0 only when the record appeared on the device AND adb pull returned 0 AND skin-bake-probe.md/.json are at -PullTo (2 = no record in time, 3 = pull failed or incomplete),
  the record waited for and pulled. The record lands under the app's files:
  /sdcard/Android/data/com.zantetsuken.quest.skinbakeprobe/files/runs/<utc>-skinbakeprobe-<id>/ (skin-bake-probe.md/.csv/.json,
  the pictures, quest-platform.txt). With -PullTo the run folder is pulled there when its record appears (or the wait ends).
  Launch only with the headset worn: the Player renders only then.
.EXAMPLE
  .\Run-SkinBakeProbe.ps1 -Apk <apk> -PullTo <folder>
#>
[CmdletBinding()]
param(
    [ValidateSet('vp3c', 'vp3c-native')][string]$Mode = 'vp3c-native',
    [string]$Apk,
    [ValidateSet('both', 'small', 'npc')][string]$Subject = 'both',
    [ValidateRange(2, 100000)][int]$DrawFrames = 45,
    [switch]$NoDraw,
    [switch]$NoPicture,
    [ValidateSet('', 'burst', 'gpu')][string]$Bench = '',       # SKIN-BAKE-PROBE-2: the performance comparison of one route in place of the cases
    [ValidateRange(10, 100000)][int]$BenchFrames = 300,
    [ValidateRange(0, 10000)][int]$BenchWarmup = 60,
    [ValidateRange(1, 8)][int]$BenchBodies = 1,
    [string]$BenchSeries = '',       # SKIN-BAKE-PROBE-3: the bench as a series of conditions (route+bodies[xrepeat], comma separated) in place of -Bench
    [ValidateRange(0, 64)][int]$BenchLoad = 0,
    [ValidateRange(0, 8)][int]$CutBench = 0,        # SKIN-GPU-FIRST-1: the cut bench's bodies (the product's prepared cuts under -SkinBake)
    [ValidateRange(0, 60)][int]$CutStagger = 0,
    [ValidateRange(1, 20)][int]$CutRounds = 1,
    [switch]$CutNoVerify,            # the cut bench as a performance run: no picture, no readback in the measured frames
    [ValidateSet('', 'cpu', 'gpu', 'float')][string]$SkinBake = '',   # -zantetsuSkinBake cpu|gpu|float in the intent's unity extra ('' leaves the Player's default: cpu)
    [ValidateSet('legacy', 'horizontal')][string]$CutPlane = 'legacy',   # PLANE-1: the cut bench's plane
    [ValidateRange(0, 60)][int]$CutPictureFrames = 0,   # SKIN-LINES-1: verification runs picture every frame pre..+N after the last request
    [ValidateRange(0, 60)][int]$HoldCpuReady = 0,       # PLANE-1 diagnosis: the gpu route's CPU bake held out of the dispatcher N frames
    [ValidateSet('both', 'nocast', 'noreceive', 'none')][string]$Shadows = 'both',   # SKIN-LINES-1: the shadow condition
    [switch]$MotionCapture,          # PLANE-1 diagnosis: the AppSW motion output of the pictured frames read back
    [switch]$MotionCheck,            # MOTION-CAPTURE-1: the capture-method check (two quads, the view moved by the frame number)
    [double]$MotionCheckStepCm = 1.0,
    [ValidateRange(1, 120)][int]$MotionCheckFrames = 6,
    [ValidateRange(1, 120)][int]$MotionCheckHold = 12,
    [switch]$MotionView,             # MOTION-CAPTURE-1 step 6: the cut bench's eye follows the check's sequence from the first request (with -MotionCapture)
    [ValidateSet('none', 'draw', 'skin-prepare', 'skin-dispatch', 'draw+skin-dispatch')][string]$Warm = 'none',   # SKIN-GPU-FIRST-STARTUP-1: the preparation before the bench
    [double]$EdgeRight = 0.6,        # the edge windows' x (metres along the root) in the Quest's one-eye picture: right edge (partly cut), left edge
    [double]$EdgeLeft = -2.8,
    [switch]$AppSwOff,
    [switch]$QuitWhenDone,
    [string]$PullTo = '',
    [int]$WaitSeconds = 300,
    [string]$Package = 'com.zantetsuken.quest.skinbakeprobe',
    [string]$Serial = '340YC10G7D0FZ9',
    [string]$Adb = 'C:\Program Files\RenderDocForMetaQuest\plugins\android\adb.exe'
)
$ErrorActionPreference = 'Stop'
$deviceFiles = "/sdcard/Android/data/$Package/files"
function Invoke-Adb {
    & $Adb -s $Serial @args
    if ($LASTEXITCODE -ne 0) { throw "adb failed: $args" }
}
function Ask { $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'; try { & $Adb -s $Serial @args 2>$null } finally { $ErrorActionPreference = $old } }
if ($Apk) {
    $deviceApk = '/data/local/tmp/quest-skinbakeprobe.apk'
    try {
        Invoke-Adb push ([IO.Path]::GetFullPath($Apk)) $deviceApk | Out-Null
        Invoke-Adb shell pm install -r -g $deviceApk
    } finally { Invoke-Adb shell rm -f $deviceApk }
}
Invoke-Adb shell am force-stop $Package
Invoke-Adb shell mkdir -p $deviceFiles
$before = @(Ask shell "ls $deviceFiles/runs 2>/dev/null")
$config = @{ scenario = 'skin-bake-probe'; mode = $Mode; appSw = !$AppSwOff; appSpacePoseMode = 'tracking-origin'; keepOpen = !$QuitWhenDone
    skinBakeProbeSubject = $Subject; skinBakeProbeDrawFrames = $DrawFrames; skinBakeProbeDraw = !$NoDraw; skinBakeProbePicture = !$NoPicture
    skinBakeProbeBench = $Bench; skinBakeProbeBenchFrames = $BenchFrames; skinBakeProbeBenchWarmup = $BenchWarmup; skinBakeProbeBenchBodies = $BenchBodies
    skinBakeProbeEdgeRight = $EdgeRight; skinBakeProbeEdgeLeft = $EdgeLeft
    skinBakeProbeBenchSeries = $BenchSeries; skinBakeProbeBenchLoad = $BenchLoad
    skinBakeProbeCutBench = $CutBench; skinBakeProbeCutStagger = $CutStagger; skinBakeProbeCutRounds = $CutRounds; skinBakeProbeCutNoVerify = [bool]$CutNoVerify
    skinBakeProbeCutPlane = $CutPlane; skinBakeProbeCutPictureFrames = $CutPictureFrames; skinBakeProbeHoldCpuReady = $HoldCpuReady; skinBakeProbeShadows = $Shadows
    skinBakeProbeMotionCapture = [bool]$MotionCapture
    skinBakeProbeMotionCheck = [bool]$MotionCheck; skinBakeProbeMotionCheckStepCm = $MotionCheckStepCm; skinBakeProbeMotionCheckFrames = $MotionCheckFrames; skinBakeProbeMotionCheckHold = $MotionCheckHold; skinBakeProbeMotionView = [bool]$MotionView; skinBakeProbeWarm = $Warm }
$configFile = Join-Path ([IO.Path]::GetTempPath()) ('quest-skinbakeprobe-' + [guid]::NewGuid().ToString('N') + '.json')
try {
    [IO.File]::WriteAllText($configFile, ($config | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    Invoke-Adb push $configFile "$deviceFiles/quest3s-config.json"
} finally { Remove-Item -LiteralPath $configFile -ErrorAction SilentlyContinue }
$unityArguments = '-platform-android-unitymain-affinity 0x3c'
if ($SkinBake) { $unityArguments += " -zantetsuSkinBake $SkinBake" }
Invoke-Adb shell "am start -a android.intent.action.MAIN -n $Package/com.unity3d.player.UnityPlayerGameActivity -e unity '$unityArguments'"
Write-Output "Quest skin bake probe: mode=$Mode, AppSW=$(!$AppSwOff), subject=$Subject, drawFrames=$DrawFrames, draw=$(!$NoDraw), picture=$(!$NoPicture), keepOpen=$(!$QuitWhenDone)$(if ($Bench) { ", BENCH $Bench frames=$BenchFrames warmup=$BenchWarmup bodies=$BenchBodies" })$(if ($BenchSeries) { ", BENCH series $BenchSeries frames=$BenchFrames warmup=$BenchWarmup load=$BenchLoad" })"
if (-not $PullTo) { Write-Output "Record: $deviceFiles/runs/<utc>-skinbakeprobe-<id>/skin-bake-probe.md. Check the launch record 'SKIN BAKE PROBE:' and 'QUEST SKIN BAKE PROBE:' lines."; exit 0 }
$began = Get-Date
$run = $null; $done = $false
while (((Get-Date) - $began).TotalSeconds -lt $WaitSeconds) {
    Start-Sleep -Seconds 10
    if (-not $run) { $now = @(Ask shell "ls $deviceFiles/runs 2>/dev/null"); $new = @($now | Where-Object { $before -notcontains $_ -and $_ }); if ($new.Count -ge 1) { $run = $new[-1].Trim(); Write-Output "run folder on the device: $run" } }
    if ($run) { $r = (Ask shell "ls $deviceFiles/runs/$run/skin-bake-probe.md 2>/dev/null"); if ("$r" -match 'skin-bake-probe') { $done = $true; break } }
}
Write-Output "record present: $done after $([int]((Get-Date) - $began).TotalSeconds) s"
# The record is taken only when adb pull itself returned 0 AND the required record files are at the destination (SAVE-1 review:
# a broken connection or a failed save on the PC must not end as a success). Exit 0 = record present and pulled; 2 = no record
# appeared on the device within -WaitSeconds; 3 = the record is on the device but the pull failed or is incomplete.
$pulled = $false
if ($run) {
    New-Item -ItemType Directory -Force -Path $PullTo | Out-Null
    $ErrorActionPreference = 'Continue'
    $pullOutput = @(& $Adb -s $Serial pull "$deviceFiles/runs/$run" "$PullTo\$run" 2>&1 | ForEach-Object { "$_" })
    $pullExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($pullOutput.Count -gt 0) { Write-Output $pullOutput[-1] }
    $required = @('skin-bake-probe.md', 'skin-bake-probe.json')
    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath "$PullTo\$run\$_" -PathType Leaf) })
    if ($pullExit -ne 0) { Write-Output "PULL FAILED: adb pull exit $pullExit (the record stays on the device: $deviceFiles/runs/$run)" }
    elseif ($missing.Count -gt 0) { Write-Output "PULL INCOMPLETE: $($missing -join ', ') missing in $PullTo\$run (the record stays on the device: $deviceFiles/runs/$run)" }
    else {
        $pulled = $true
        Write-Output "pulled to $PullTo\$run"
        Get-Content -LiteralPath "$PullTo\$run\skin-bake-probe.md" | Select-Object -First 3
    }
}
if ($done -and $pulled) { exit 0 } elseif ($done) { exit 3 } else { exit 2 }
