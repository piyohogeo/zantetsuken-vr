#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$OutputDirectory,
    [switch]$InstallPlayerBootstrap
)
$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$template = Join-Path $output 'CutWorldSandbox.exe'
if ($InstallPlayerBootstrap -and !(Test-Path -LiteralPath $template -PathType Leaf)) {
    throw 'Installing the Player bootstrap requires the generated CutWorldSandbox.exe as its resource template.'
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework C# compiler was not found.' }
[IO.Directory]::CreateDirectory($output) | Out-Null
$shared = Join-Path $project 'Assets\Zantetsu\Runtime\Core\WindowsGraphicsPreference.cs'
$settings = Join-Path $output '起動設定.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /utf8output /warnaserror+ /main:Zantetsu.WindowsLauncher.SettingsLauncher "/out:$settings" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll (Join-Path $PSScriptRoot 'Launcher.cs') $shared
if ($LASTEXITCODE -ne 0) { throw "Settings launcher compilation failed ($LASTEXITCODE)." }
$resolver = Join-Path $output 'Zantetsu.Graphics.exe'
& $compiler /nologo /target:exe /platform:x64 /optimize+ /codepage:65001 /utf8output /warnaserror+ /main:Zantetsu.WindowsLauncher.GraphicsResolver "/out:$resolver" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Management.dll (Join-Path $PSScriptRoot 'Launcher.cs') (Join-Path $PSScriptRoot 'GraphicsResolver.cs') $shared
if ($LASTEXITCODE -ne 0) { throw "Graphics resolver compilation failed ($LASTEXITCODE)." }
if ($InstallPlayerBootstrap) {
    & (Join-Path $PSScriptRoot 'Build-PlayerBootstrap.ps1') -OutputDirectory $output -TemplateExecutable $template
}
# Remove only the former outer launcher after the replacement helpers and optional bootstrap succeeded.
# A locked or otherwise undeletable file fails the build, so it cannot enter a completed package.
$obsolete = Join-Path $output 'Zantetsu.exe'
if (Test-Path -LiteralPath $obsolete -PathType Leaf) { Remove-Item -LiteralPath $obsolete -Force }
Write-Output $settings
Write-Output $resolver
