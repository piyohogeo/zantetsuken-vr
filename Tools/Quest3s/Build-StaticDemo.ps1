#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Development,
    [switch]$SkipConfigure,
    [string]$Output,
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$Output) { $Output = Join-Path $project ('Build\quest-static' + $(if ($Development) { '-dev' }) + '.apk') }
$Output = [IO.Path]::GetFullPath($Output)
$logs = Join-Path $project 'Logs'
[IO.Directory]::CreateDirectory($logs) | Out-Null
function Invoke-Unity([string]$Method, [string]$Log, [string]$Extra) {
    $arguments = '-batchmode -quit -force-d3d11 -projectPath "' + $project + '" -buildTarget Android -executeMethod ' + $Method + ' -logFile "' + $Log + '" ' + $Extra
    $process = Start-Process -FilePath $Unity -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $handle = $process.Handle # Retain the process handle before it exits; ExitCode stays readable in Windows PowerShell.
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Unity failed ($($process.ExitCode)); see $Log" }
}
if (!$SkipConfigure) {
    Invoke-Unity 'Zantetsu.EditorTools.Quest.QuestStaticDemoBuild.Configure' (Join-Path $logs 'quest-configure.log') ''
}
$extra = '-zantetsuQuestOut "' + $Output + '"'
if ($Development) { $extra += ' -zantetsuQuestDev' }
Invoke-Unity 'Zantetsu.EditorTools.Quest.QuestStaticDemoBuild.BuildApk' (Join-Path $logs 'quest-build.log') $extra
Get-FileHash -LiteralPath $Output -Algorithm SHA256
# Source meshes, scene copy and baked StreamingAssets remain under ignored licensed paths.
# Run-StaticDemo.ps1 -Apk <apk> -Mode unity|vp3c -Profiler starts the shared comparison.
