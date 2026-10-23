#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$VersionName,
    [Parameter(Mandatory=$true)][ValidateRange(1,2100000000)][int]$VersionCode,
    [Parameter(Mandatory=$true)][string]$Keystore,
    [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$KeyAlias,
    [string]$SigningCredential,
    [Parameter(Mandatory=$true)][string]$AsideDirectory,
    [string]$ForbiddenWordsFile,
    [string]$StatisticsTokenVariable='ZANTETSU_INGEST_TOKEN_ALPHA',
    [ValidateSet('Apk','Bundle')][string]$Format='Apk',
    [string]$Output,
    [string]$Unity='C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe'
)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
$Keystore=[IO.Path]::GetFullPath($Keystore)
$AsideDirectory=[IO.Path]::GetFullPath($AsideDirectory).TrimEnd('\')
foreach($privatePath in @($Keystore,$AsideDirectory,$SigningCredential)) {
    if(!$privatePath) {continue};$full=[IO.Path]::GetFullPath($privatePath)
    if($full.Equals($project,[StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($project+'\',[StringComparison]::OrdinalIgnoreCase)) {throw 'Signing and private backup paths must stay outside the repository'}
}
if(!(Test-Path -LiteralPath $Keystore -PathType Leaf)) {throw 'Upload keystore is missing'}
if(!$ForbiddenWordsFile) {$ForbiddenWordsFile=(git -C $project config --get zantetsu.forbiddenWordsFile)}
. (Join-Path $PSScriptRoot '..\ForbiddenWords.ps1')
$words=Read-ForbiddenWords -Path $ForbiddenWordsFile -RepositoryRoot $project
$matcher=New-ForbiddenMatcher -Words $words
. (Join-Path $PSScriptRoot '..\Quest3s\InputSources.ps1')
& (Join-Path $PSScriptRoot '..\Verify-PublicRepository.ps1') -RepositoryPath $project -RequireForbiddenWords -ForbiddenWordsFile $ForbiddenWordsFile -Revisions (git -C $project rev-parse HEAD)
if($LASTEXITCODE -ne 0) {throw 'Public repository verification failed before the build'}
$androidPlayer=Join-Path ([IO.Path]::GetDirectoryName($Unity)) 'Data\PlaybackEngines\AndroidPlayer'
$keytool=Join-Path $androidPlayer 'OpenJDK\bin\keytool.exe'
$extension=if($Format -eq 'Bundle'){'.aab'}else{'.apk'}
$method=if($Format -eq 'Bundle'){'BuildBundle'}else{'BuildApk'}
if(!$Output) {$Output=Join-Path $project "Build\android-github-$VersionName-$VersionCode$extension"}
$Output=[IO.Path]::GetFullPath($Output)
if([IO.Path]::GetExtension($Output) -ine $extension) {throw "Output must be $extension"}
if((Test-Path -LiteralPath $Output) -or (Test-Path -LiteralPath ($Output+'.record'))) {throw 'Output or inspection record exists; nothing is overwritten'}
$statsAsset=Join-Path $project 'Assets\Zantetsu\Runtime\Sandbox\Resources\ZantetsuPlayStatisticsConfig.asset'
foreach($file in @($statsAsset,"$statsAsset.meta")) {if(Test-Path -LiteralPath $file) {throw 'Existing statistics config must be moved aside explicitly before this build'}}
$hasToken=![string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($StatisticsTokenVariable)) -or ![string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($StatisticsTokenVariable,'User'))
if(!$hasToken) {throw 'The statistics ingest environment variable is unavailable'}
$record=$Output+'.record';[IO.Directory]::CreateDirectory($record) | Out-Null
$settings=@{}
foreach($rel in @('ProjectSettings/ProjectSettings.asset','ProjectSettings/TimeManager.asset')) {$settings[$rel]=[IO.File]::ReadAllBytes((Join-Path $project $rel))}
$inputTargets=@(Find-InputSourceTargets -Project $project)
$runAside=Join-Path $AsideDirectory (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
$inputAside=Join-Path $runAside 'inputs';$inputManifest=Join-Path $runAside 'inputs.tsv'
$statsScript=Join-Path $PSScriptRoot '..\Quest3s\Set-StatisticsConfig.ps1'
$oldStore=$env:ZANTETSU_KEYSTORE_PASSWORD;$oldKey=$env:ZANTETSU_KEY_PASSWORD
$made=$false;$buildFailure=$null;$cleanupFailures=@()
try {
    if($SigningCredential) {
        $credential=Import-Clixml -LiteralPath $SigningCredential
        if($credential -isnot [pscredential] -or $credential.UserName -cne $KeyAlias) {throw 'Protected signing credential does not match the key alias'}
        $env:ZANTETSU_KEYSTORE_PASSWORD=$credential.GetNetworkCredential().Password
        $env:ZANTETSU_KEY_PASSWORD=$env:ZANTETSU_KEYSTORE_PASSWORD
    }
    if(!$env:ZANTETSU_KEYSTORE_PASSWORD -or !$env:ZANTETSU_KEY_PASSWORD) {throw 'Signing passwords must be in the environment or a Windows-protected signing credential'}
    $oldPreference=$ErrorActionPreference;$ErrorActionPreference='Continue'
    try {$listed=@(& $keytool '-J-Duser.language=en' '-J-Duser.country=US' -list -v -keystore $Keystore -alias $KeyAlias '-storepass:env' ZANTETSU_KEYSTORE_PASSWORD 2>&1 | ForEach-Object {"$_"});$listExit=$LASTEXITCODE} finally {$ErrorActionPreference=$oldPreference}
    if($listExit -ne 0) {throw 'Upload key alias/password verification failed'}
    $sha=@($listed | Where-Object {$_ -match '^\s*SHA256:\s*(\S+)'} | ForEach-Object {$Matches[1] -replace ':',''})
    if($sha.Count -ne 1) {throw 'Upload certificate digest not found'}
    if((Get-ForbiddenCount -Matcher $matcher -Bytes ([Text.Encoding]::UTF8.GetBytes(($listed -join "`n")))) -gt 0) {throw 'Upload certificate contains forbidden text (contents suppressed)'}
    if($inputTargets.Count -gt 0) {Set-BlankInputSources -Project $project -Targets $inputTargets -Aside $inputAside -Manifest $inputManifest}
    $null=& $statsScript -Project $project -Role external -TokenVariable $StatisticsTokenVariable -AsideDirectory (Join-Path $runAside 'failed-config') -Unity $Unity
    $made=$true
    $log=Join-Path $record 'build.log'
    $arguments='-batchmode -quit -force-d3d11 -projectPath "'+$project+'" -buildTarget Android -executeMethod Zantetsu.EditorTools.AndroidFlat.AndroidFlatDistributionBuild.'+$method+' -logFile "'+$log+'" -zantetsuAndroidFlat -zantetsuBuildMode commercial -zantetsuDistVersionName '+$VersionName+' -zantetsuDistVersionCode '+$VersionCode+' -zantetsuDistKeystore "'+$Keystore+'" -zantetsuDistKeyAlias '+$KeyAlias+' -zantetsuAndroidFlatOut "'+$Output+'"'
    if($inputTargets.Count -gt 0) {$arguments+=' -zantetsuDistInputSources "'+$inputManifest+'" -zantetsuDistInputSourcesAside "'+$inputAside+'"'}
    $process=Start-Process -FilePath $Unity -ArgumentList $arguments -WorkingDirectory $project -WindowStyle Hidden -PassThru
    $null=$process.Handle;$process.WaitForExit()
    if($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $Output)) {throw "Android distribution build failed (Unity exit $($process.ExitCode)); see $log"}
} catch {$buildFailure=$_}
finally {
    if($made) {
        try {$null=& $statsScript -Project $project -Remove -AsideDirectory (Join-Path $runAside 'used-config') -Unity $Unity} catch {$cleanupFailures+='Statistics config cleanup failed'}
    }
    if($inputTargets.Count -gt 0 -and (Test-Path -LiteralPath $inputManifest)) {
        try {
            $failures=@(Restore-InputSources -Project $project -Aside $inputAside -Manifest $inputManifest)
            if($failures.Count -gt 0) {$cleanupFailures+='Input originals were not restored'}
            else {
                $code=Invoke-InputSourceCheck -Unity $Unity -Project $project -Manifest $inputManifest -Aside $inputAside -Expect original -Log (Join-Path $record 'input-restore.log')
                if($code -ne 0) {$cleanupFailures+='Restored input imports failed verification'}
            }
        } catch {$cleanupFailures+='Input restoration check failed'}
    }
    foreach($rel in $settings.Keys) {
        try {[IO.File]::WriteAllBytes((Join-Path $project $rel),$settings[$rel])} catch {$cleanupFailures+='Project settings restoration failed'}
    }
    $env:ZANTETSU_KEYSTORE_PASSWORD=$oldStore;$env:ZANTETSU_KEY_PASSWORD=$oldKey;$credential=$null
    foreach($file in @($statsAsset,"$statsAsset.meta")) {if(Test-Path -LiteralPath $file) {$cleanupFailures+='Statistics config remains in the tree'}}
}
if($cleanupFailures.Count -gt 0) {throw ($cleanupFailures -join '; ')}
if($buildFailure) {throw $buildFailure}
if($Format -eq 'Bundle') {
    & (Join-Path $PSScriptRoot 'Inspect-PlayBundle.ps1') -Bundle $Output -VersionName $VersionName -VersionCode $VersionCode -CertificateSha256 $sha[0] -ForbiddenWordsFile $ForbiddenWordsFile -AndroidPlayer $androidPlayer
} else {
    & (Join-Path $PSScriptRoot 'Inspect-DistributionApk.ps1') -Apk $Output -VersionName $VersionName -VersionCode $VersionCode -CertificateSha256 $sha[0] -ForbiddenWordsFile $ForbiddenWordsFile -AndroidPlayer $androidPlayer
}
if($LASTEXITCODE -ne 0) {throw 'Android distribution inspection failed'}
