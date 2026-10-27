#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$SmolvSourceDirectory,
    [Parameter(Mandatory=$true)][string]$DxcIncludeDirectory,
    [Parameter(Mandatory=$true)][string]$OutDirectory,
    [string]$VcVars='C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..')).TrimEnd('\')
$out=[IO.Path]::GetFullPath($OutDirectory).TrimEnd('\')
if(!$out.StartsWith($project+'\Build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Audit executables must stay below this repository Build directory'}
$smolv=[IO.Path]::GetFullPath($SmolvSourceDirectory)
$dxc=[IO.Path]::GetFullPath($DxcIncludeDirectory)
foreach($file in @($VcVars,(Join-Path $smolv 'smolv.cpp'),(Join-Path $smolv 'smolv.h'),(Join-Path $dxc 'dxcapi.h'))){if(!(Test-Path -LiteralPath $file -PathType Leaf)){throw 'An audit compiler input is missing'}}
[IO.Directory]::CreateDirectory($out)|Out-Null
$jobs=@(
    @{Source=(Join-Path $PSScriptRoot 'ContentAuditSmolv.cpp');Include=$smolv;Extra=('"'+(Join-Path $smolv 'smolv.cpp')+'"');Name='smolv-audit';Flags='/LD';Extension='.dll'},
    @{Source=(Join-Path $PSScriptRoot 'ContentAuditDxil.cpp');Include=$dxc;Extra='';Name='dxil-audit';Flags='';Extension='.exe'}
)
Push-Location -LiteralPath $out
try {
    foreach($job in $jobs){
        $command='"'+$VcVars+'" >nul && cl /nologo /MT /O2 /EHsc /std:c++17 '+$job.Flags+' /I "'+$job.Include+'" '+$job.Extra+' "'+$job.Source+'" /Fe:"'+(Join-Path $out ($job.Name+$job.Extension))+'"'
        & cmd.exe /c $command
        if($LASTEXITCODE -ne 0){throw 'Audit adapter compilation failed'}
    }
} finally {Pop-Location}
Write-Output 'Built the two local content-audit adapters; nothing copied into Assets'
