#Requires -Version 5.1
[CmdletBinding()]
param([string]$Output,[string]$Unity='C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe')
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
if(!$Output){$Output=Join-Path $project 'Build\android-statistics-recovery.apk'}
$Output=[IO.Path]::GetFullPath($Output)
if(!$Output.StartsWith($project+'\Build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Probe output must remain under Build'}
if((Test-Path -LiteralPath $Output) -or (Test-Path -LiteralPath ($Output+'.record'))){throw 'Probe output exists; nothing is overwritten'}
if(Test-Path -LiteralPath (Join-Path $project 'Assets\Zantetsu\Runtime\Sandbox\Resources\ZantetsuPlayStatisticsConfig.asset')){throw 'Remove the local production send config before building the isolated check'}
$record=$Output+'.record';[IO.Directory]::CreateDirectory($record) | Out-Null
$saved=@{}
foreach($relative in @('ProjectSettings/ProjectSettings.asset','ProjectSettings/GraphicsSettings.asset','ProjectSettings/QualitySettings.asset','ProjectSettings/TimeManager.asset')){$saved[$relative]=[IO.File]::ReadAllBytes((Join-Path $project $relative))}
try {
 $arguments='-batchmode -quit -force-d3d11 -projectPath "'+$project+'" -buildTarget Android -executeMethod Zantetsu.EditorTools.AndroidFlat.AndroidFlatStatisticsRecoveryBuild.Build -zantetsuBuildMode commercial -statisticsRecoveryOut "'+$Output+'" -logFile "'+$record+'\build.log"'
 $process=Start-Process -FilePath $Unity -ArgumentList $arguments -WorkingDirectory $project -WindowStyle Hidden -PassThru
 $null=$process.Handle;$process.WaitForExit()
 if($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $Output)){throw 'Recovery check APK build failed; see its local build.log'}
} finally {foreach($relative in $saved.Keys){[IO.File]::WriteAllBytes((Join-Path $project $relative),$saved[$relative])}}
[pscustomobject]@{Apk=$Output;Package='com.zantetsuken.statisticsrecoveryprobe';Sha256=(Get-FileHash -LiteralPath $Output -Algorithm SHA256).Hash;ProductionConfiguration='not loaded';Receiver='synthetic loopback only';Published=$false} | ConvertTo-Json
