#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Output,
    [string]$Unity = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if (!$Output) { $Output = Join-Path $project 'Logs\android-flat-smoke' }
$Output = [IO.Path]::GetFullPath($Output)
[IO.Directory]::CreateDirectory($Output) | Out-Null
$log = Join-Path $Output 'unity.log'
# Do not add -quit: the runner must enter Play Mode, cross domain reloads and finish its frame-based checks first.
$arguments = '-batchmode -force-d3d11 -projectPath "' + $project + '" -buildTarget Android -executeMethod Zantetsu.EditorTools.AndroidFlat.AndroidFlatSmoke.Run -zantetsuAndroidFlat -zantetsuAndroidFlatSmokeOut "' + $Output + '" -logFile "' + $log + '"'
$process = Start-Process -FilePath $Unity -ArgumentList $arguments -WindowStyle Hidden -PassThru
$processHandle = $process.Handle
$process.WaitForExit()
if ($process.ExitCode -ne 0) { throw "Android phone smoke failed ($($process.ExitCode)); see $Output" }
$report = Join-Path $Output 'smoke.txt'
if (!(Test-Path -LiteralPath $report -PathType Leaf)) { throw "Smoke returned success without a report: $report" }
Get-Content -LiteralPath $report
