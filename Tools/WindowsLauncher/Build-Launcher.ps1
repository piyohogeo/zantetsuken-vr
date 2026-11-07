#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework C# compiler was not found.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$executable = Join-Path $output 'Zantetsu.exe'
$shared = Join-Path $project 'Assets\Zantetsu\Runtime\Core\WindowsGraphicsPreference.cs'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /utf8output /warnaserror+ /main:Zantetsu.WindowsLauncher.Launcher "/out:$executable" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll (Join-Path $PSScriptRoot 'Launcher.cs') $shared
if ($LASTEXITCODE -ne 0) { throw "Launcher compilation failed ($LASTEXITCODE)." }
$settings = Join-Path $output '起動設定.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /utf8output /warnaserror+ /main:Zantetsu.WindowsLauncher.SettingsLauncher "/out:$settings" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll (Join-Path $PSScriptRoot 'Launcher.cs') $shared
if ($LASTEXITCODE -ne 0) { throw "Settings launcher compilation failed ($LASTEXITCODE)." }
Write-Output $executable
Write-Output $settings
