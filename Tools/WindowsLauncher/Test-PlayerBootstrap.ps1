#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$VcVars='C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat'
)
$ErrorActionPreference='Stop'
if(-not $OutputDirectory){$OutputDirectory=Join-Path $env:TEMP ('Zantetsu bootstrap tests '+[Guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $output){throw 'Use a fresh native test output directory.'}
[IO.Directory]::CreateDirectory($output)|Out-Null
$fixtures=Join-Path $output 'fixtures'
$production=Join-Path $output 'production'
[IO.Directory]::CreateDirectory($fixtures)|Out-Null
$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$testSources=Join-Path $PSScriptRoot 'Tests'
$template=Join-Path $fixtures 'Template.exe'
& $compiler /nologo /target:winexe /platform:x64 /codepage:65001 /utf8output /warnaserror+ ('/out:'+$template) (Join-Path $testSources 'PlayerBootstrapTemplate.cs')
if($LASTEXITCODE -ne 0){throw 'Resource template fixture compilation failed.'}
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 /utf8output /warnaserror+ ('/out:'+(Join-Path $fixtures 'Zantetsu.Graphics.exe')) (Join-Path $testSources 'PlayerBootstrapResolverFixture.cs')
if($LASTEXITCODE -ne 0){throw 'Resolver fixture compilation failed.'}
[IO.Directory]::CreateDirectory($production)|Out-Null
$generatedPlayer=Join-Path $production 'CutWorldSandbox.exe'
Copy-Item -LiteralPath $template -Destination $generatedPlayer
$templateHash=(Get-FileHash -LiteralPath $template).Hash
& (Join-Path $PSScriptRoot 'Build-PlayerBootstrap.ps1') -OutputDirectory $production -TemplateExecutable $generatedPlayer -VcVars $VcVars
$backups=@(Get-ChildItem -LiteralPath (Join-Path $production 'CutWorldSandbox_BackUpThisFolder_ButDontShipItWithYourGame') -Filter 'PreviousPlayer.exe' -Recurse -File)
if($backups.Count -ne 1 -or (Get-FileHash -LiteralPath $backups[0].FullName).Hash -ne $templateHash){throw 'The atomic replacement did not preserve the generated Player.'}
if((Get-FileHash -LiteralPath $template).Hash -ne $templateHash){throw 'The original fixture template changed.'}
$environment=@(& cmd.exe /d /s /c ('"'+$VcVars+'" >nul && set'))
if($LASTEXITCODE -ne 0){throw 'The fixture compiler environment could not be prepared.'}
$held=@{}
try {
    foreach($name in @('PATH','INCLUDE','LIB','LIBPATH')){
        $entry=@($environment|Where-Object {$_ -match ('(?i)^'+$name+'=')})
        if($entry.Count -ne 1){throw 'The fixture compiler environment is incomplete.'}
        $held[$name]=[Environment]::GetEnvironmentVariable($name,'Process')
        [Environment]::SetEnvironmentVariable($name,$entry[0].Substring($name.Length+1),'Process')
    }
    $native=(Get-Command cl.exe -ErrorAction Stop).Source
    $common=@('/nologo','/MT','/O2','/EHsc','/W4','/WX','/std:c++17','/utf-8','/DUNICODE','/D_UNICODE')
    foreach($variant in @('normal','no-entry')){
        $name=if($variant -eq 'normal'){'UnityPlayer'}else{'UnityPlayer.NoEntry'}
        $arguments=$common+@('/LD',(Join-Path $testSources 'PlayerBootstrapUnityFixture.cpp'),
            ('/Fo'+(Join-Path $fixtures ($name+'.obj'))),('/Fe'+(Join-Path $fixtures ($name+'.dll'))))
        if($variant -eq 'no-entry'){$arguments+='/DZANTETSU_FIXTURE_NO_ENTRY'}
        & $native @arguments /link /DEBUG:NONE
        if($LASTEXITCODE -ne 0){throw 'Fake Unity library compilation failed.'}
    }
    $arguments=$common+@((Join-Path $testSources 'PlayerBootstrapErrorFixture.cpp'),
        ('/Fo'+(Join-Path $fixtures 'BootstrapError.obj')),('/Fe'+(Join-Path $fixtures 'BootstrapError.exe')),
        '/link','/SUBSYSTEM:WINDOWS','/DEBUG:NONE','user32.lib')
    & $native @arguments
    if($LASTEXITCODE -ne 0){throw 'Noninteractive error fixture compilation failed.'}
}
finally {
    foreach($name in $held.Keys){[Environment]::SetEnvironmentVariable($name,$held[$name],'Process')}
}
$runner=Join-Path $output 'PlayerBootstrapNativeTests.exe'
& $compiler /nologo /target:exe /platform:x64 /codepage:65001 /utf8output /warnaserror+ /main:PlayerBootstrapNativeTests ('/out:'+$runner) (Join-Path $testSources 'PlayerBootstrapNativeTests.cs') (Join-Path $testSources 'PlayerBootstrapResolverFixture.cs')
if($LASTEXITCODE -ne 0){throw 'Native fixture test runner compilation failed.'}
$arguments=@((Join-Path $production 'CutWorldSandbox.exe'),(Join-Path $fixtures 'BootstrapError.exe'),$fixtures,(Join-Path $output 'cases'),$template)
& $runner @arguments
if($LASTEXITCODE -ne 0){throw 'Native Player bootstrap tests failed.'}
Write-Output ('Native fixture evidence: '+$output)
