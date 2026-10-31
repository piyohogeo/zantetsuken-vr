#Requires -Version 5.1
<#
.SYNOPSIS
  The Quest 3S APK of the NPC skin benchmark (DESIGN 3.4, npc-skin-bench): the benchmark scene made from the configured
  Quest city-walk scene (Build-CityWalk.ps1's Configure must have run in this tree), built alone as package
  com.zantetsuken.quest.npcskinbench. Uses the existing Quest Vulkan plugin and baked shaders.
#>
[CmdletBinding()]
param(
    [switch]$Development,
    [switch]$SkipConfigure,
    [string]$Output,
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$Output) { $Output = Join-Path $project ('Build\quest-npcskinbench' + $(if ($Development) { '-dev' }) + '.apk') }
$Output = [IO.Path]::GetFullPath($Output)
$logs = Join-Path $project 'Logs/quest-npcskinbench'
[IO.Directory]::CreateDirectory($logs) | Out-Null
function Invoke-Unity([string]$Method, [string]$Log, [string]$Extra) {
    $arguments = '-batchmode -quit -force-d3d11 -projectPath "' + $project + '" -buildTarget Android -executeMethod ' + $Method + ' -logFile "' + $Log + '" ' + $Extra
    $process = Start-Process -FilePath $Unity -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $handle = $process.Handle
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Unity failed ($($process.ExitCode)); see $Log" }
}
if (!$SkipConfigure) { Invoke-Unity 'Zantetsu.EditorTools.Quest.QuestNpcSkinBenchBuild.Configure' (Join-Path $logs 'configure.log') '' }
$extra = '-zantetsuQuestOut "' + $Output + '"'
if ($Development) { $extra += ' -zantetsuQuestDev' }
Invoke-Unity 'Zantetsu.EditorTools.Quest.QuestNpcSkinBenchBuild.BuildApk' (Join-Path $logs 'build.log') $extra
Get-FileHash -LiteralPath $Output -Algorithm SHA256
