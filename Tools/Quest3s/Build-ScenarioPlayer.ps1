#Requires -Version 5.1
# The Quest APK of every scenario (DESIGN 3.4): the boot scene, the city walk's scene (play runs in it too), the static
# scene and the ten-NPC scene in one package (com.zantetsuken.quest). It configures nothing: run the three scenes'
# own builds (Build-StaticDemo, Build-NpcSlashDemo, Build-CityWalk) first, which make the scenes, the Vulkan plugin, the
# baked shaders and the files under StreamingAssets; this only builds the package from what they left.
[CmdletBinding()]
param(
    [switch]$Development,
    [ValidateSet('Auto','Demo','Commercial')][string]$BuildMode = 'Auto',
    [string]$Output,
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$Output) { $Output = Join-Path $project (('Build\quest-scenarios-' + $BuildMode.ToLowerInvariant()) + $(if ($Development) { '-dev' }) + '.apk') }
$Output = [IO.Path]::GetFullPath($Output)
$logs = Join-Path $project 'Logs/quest-scenarios'
[IO.Directory]::CreateDirectory($logs) | Out-Null
$log = Join-Path $logs 'build.log'
$arguments = '-batchmode -quit -force-d3d11 -projectPath "' + $project + '" -buildTarget Android -executeMethod Zantetsu.EditorTools.Quest.QuestScenarioPlayerBuild.BuildApk -logFile "' + $log + '" -zantetsuQuestOut "' + $Output + '"'
$arguments += ' -zantetsuBuildMode ' + $BuildMode.ToLowerInvariant()
if ($Development) { $arguments += ' -zantetsuQuestDev' }
$process = Start-Process -FilePath $Unity -ArgumentList $arguments -WorkingDirectory $project -WindowStyle Hidden -PassThru
$handle = $process.Handle
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "Unity failed ($($process.ExitCode)); see $log" }
Get-FileHash -LiteralPath $Output -Algorithm SHA256
