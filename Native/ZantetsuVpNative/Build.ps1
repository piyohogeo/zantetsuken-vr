#Requires -Version 5.1
<#
.SYNOPSIS
  Builds the Zantetsu VP native draw plugin (Direct3D 12, x64) with the MSVC of Visual Studio 2022 and the Windows SDK,
  against the plugin headers of the Unity Editor the project uses, into a folder given: ZantetsuVpNative.dll (+ .pdb).
  It copies nothing into the project: the caller puts the DLL where Unity loads it from.

.DESCRIPTION
  One translation unit, no CMake, no dependency besides d3d12, d3dcompiler (reflection) and the Unity plugin headers.
  /MT so that the DLL needs no VC redistributable beside the Player.
#>
param(
    [Parameter(Mandatory = $true)][string]$OutDirectory,
    [string]$UnityPluginApi = 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Data\PluginAPI',
    [string]$VcVars = 'C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
)
$ErrorActionPreference = 'Stop'
foreach ($p in $UnityPluginApi, $VcVars) { if (-not (Test-Path -LiteralPath $p)) { throw "not found: $p" } }
if (-not (Test-Path -LiteralPath $OutDirectory -PathType Container)) { New-Item -ItemType Directory -Path $OutDirectory | Out-Null }
$source = Join-Path $PSScriptRoot 'ZantetsuVpNative.cpp'
$out = (Resolve-Path -LiteralPath $OutDirectory).ProviderPath
$command = "`"$VcVars`" >nul && cl /nologo /LD /MT /O2 /Zi /EHsc /W4 /WX /std:c++17 /utf-8 /DUNICODE /I `"$UnityPluginApi`" `"$source`" /Fo`"$out\ZantetsuVpNative.obj`" /Fd`"$out\ZantetsuVpNative.pdb`" /Fe`"$out\ZantetsuVpNative.dll`" /link /DEBUG /OPT:REF /OPT:ICF d3d12.lib d3dcompiler.lib dxguid.lib"
& cmd.exe /c $command
if ($LASTEXITCODE -ne 0) { throw "the build failed with $LASTEXITCODE" }
$dll = Join-Path $out 'ZantetsuVpNative.dll'
"built $dll $((Get-Item -LiteralPath $dll).Length) bytes sha256 $((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant())"
