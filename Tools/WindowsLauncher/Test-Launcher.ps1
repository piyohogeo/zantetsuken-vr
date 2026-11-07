#Requires -Version 5.1
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
if (!$OutputDirectory) { $OutputDirectory = Join-Path $env:TEMP ('Zantetsu launcher tests ' + [guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw 'Use a new output directory so no Player can be overwritten or run.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$shared = Join-Path $project 'Assets\Zantetsu\Runtime\Core\WindowsGraphicsPreference.cs'
& (Join-Path $PSScriptRoot 'Build-Launcher.ps1') -OutputDirectory $output
$settingsName = -join ([char[]]@(0x8D77,0x52D5,0x8A2D,0x5B9A))
if (!(Test-Path -LiteralPath (Join-Path $output ($settingsName + '.exe')))) { throw 'The Japanese settings executable name is incorrect.' }
if (Test-Path -LiteralPath (Join-Path $output ($settingsName + '.lnk'))) { throw 'Build unexpectedly created a nonportable settings shortcut.' }
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 /utf8output /warnaserror+ ("/out:" + (Join-Path $output 'CutWorldSandbox.exe')) (Join-Path $PSScriptRoot 'Tests\FakePlayer.cs')
if ($LASTEXITCODE -ne 0) { throw 'Fake child compilation failed.' }
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 /utf8output /warnaserror+ /main:Zantetsu.WindowsLauncher.LauncherTests ("/out:" + (Join-Path $output 'LauncherTests.exe')) /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll (Join-Path $PSScriptRoot 'Launcher.cs') $shared (Join-Path $PSScriptRoot 'Tests\LauncherTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Launcher test compilation failed.' }
& (Join-Path $output 'LauncherTests.exe') $output
if ($LASTEXITCODE -ne 0) { throw 'Launcher tests failed.' }
Write-Output ('Test artifacts: ' + $output)
