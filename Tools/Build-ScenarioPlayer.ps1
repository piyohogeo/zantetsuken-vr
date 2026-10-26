#Requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('Auto','Demo','Commercial')][string]$BuildMode = 'Auto',
    [string]$Output,
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe',
    # Direct3D 12 first, then 11, for this build only: the Player starts on Direct3D 12 and draws through the native plugin (VP3C-NATIVE)
    [switch]$Direct3D12
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$Output) { $Output = Join-Path $project ('Build/scenarios-' + $BuildMode.ToLowerInvariant()) }
$Output = [IO.Path]::GetFullPath($Output)
$logs = Join-Path $project 'Logs/scenarios'
[IO.Directory]::CreateDirectory($logs) | Out-Null
$log = Join-Path $logs ('build-' + $BuildMode.ToLowerInvariant() + '.log')
$arguments = '-batchmode -quit -force-d3d11 -projectPath "' + $project + '" -buildTarget StandaloneWindows64 -executeMethod Zantetsu.EditorTools.Sandbox.ScenarioPlayerBuild.BuildPlayerFromCommandLine -logFile "' + $log + '" -zantetsuPlayerOut "' + $Output + '" -zantetsuBuildMode ' + $BuildMode.ToLowerInvariant()
if ($Direct3D12) { $arguments += ' -zantetsuPlayerD3D12' }
$process = Start-Process -FilePath $Unity -ArgumentList $arguments -WorkingDirectory $project -WindowStyle Hidden -PassThru
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "Unity failed ($($process.ExitCode)); see $log" }
Get-Content -LiteralPath (Join-Path $Output 'zantetsu-content-audit.json')
