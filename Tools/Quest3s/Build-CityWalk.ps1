#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Development,
    [switch]$SkipConfigure,
    [string]$Output,
    [string]$InputFile,
    [string]$ScriptFile,
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$Output) { $Output = Join-Path $project ('Build\quest-citywalk' + $(if ($Development) { '-dev' }) + '.apk') }
$Output = [IO.Path]::GetFullPath($Output)
$logs = Join-Path $project 'Logs/quest-citywalk'
[IO.Directory]::CreateDirectory($logs) | Out-Null
function Invoke-Unity([string]$Method, [string]$Log, [string]$Extra) {
    $arguments = '-batchmode -quit -force-d3d11 -projectPath "' + $project + '" -buildTarget Android -executeMethod ' + $Method + ' -logFile "' + $Log + '" ' + $Extra
    $process = Start-Process -FilePath $Unity -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $handle = $process.Handle
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Unity failed ($($process.ExitCode)); see $Log" }
}
# Uses the existing Quest Vulkan plugin and baked shaders; rebuild them with Build-NpcSlashDemo when their source changes.
if (!$SkipConfigure) {
    $extra = ''
    if ($InputFile) { $extra += ' -zantetsuQuestNpcInput "' + [IO.Path]::GetFullPath($InputFile) + '"' }
    if ($ScriptFile) { $extra += ' -zantetsuQuestCityScript "' + [IO.Path]::GetFullPath($ScriptFile) + '"' }
    Invoke-Unity 'Zantetsu.EditorTools.Quest.QuestCityWalkBuild.Configure' (Join-Path $logs 'configure.log') $extra
}
$extra = '-zantetsuQuestOut "' + $Output + '"'
if ($Development) { $extra += ' -zantetsuQuestDev' }
Invoke-Unity 'Zantetsu.EditorTools.Quest.QuestCityWalkBuild.BuildApk' (Join-Path $logs 'build.log') $extra
Get-FileHash -LiteralPath $Output -Algorithm SHA256
