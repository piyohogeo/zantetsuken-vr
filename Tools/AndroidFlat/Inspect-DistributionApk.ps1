#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Apk,
    [Parameter(Mandatory=$true)][string]$VersionName,
    [Parameter(Mandatory=$true)][int]$VersionCode,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9A-Fa-f]{64}$')][string]$CertificateSha256,
    [Parameter(Mandatory=$true)][string]$ForbiddenWordsFile,
    [string]$AndroidPlayer='C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Data\PlaybackEngines\AndroidPlayer'
)
$ErrorActionPreference='Stop'
function Invoke-Checked([string]$Tool,[string[]]$ToolArguments) {
    $old=$ErrorActionPreference;$ErrorActionPreference='Continue'
    try {$lines=@(& $Tool @ToolArguments 2>&1 | ForEach-Object {"$_"});$code=$LASTEXITCODE} finally {$ErrorActionPreference=$old}
    if($code -ne 0){throw "Distribution inspection tool failed ($code): $($lines | Select-Object -Last 3)"}
    return $lines
}
$Apk=[IO.Path]::GetFullPath($Apk);$record=$Apk+'.record'
[IO.Directory]::CreateDirectory($record) | Out-Null
$tools=Get-ChildItem -LiteralPath (Join-Path $AndroidPlayer 'SDK\build-tools') -Directory | Sort-Object Name -Descending | Select-Object -First 1
if(!$tools){throw 'Android build-tools are missing'}
. (Join-Path $PSScriptRoot '..\ForbiddenWords.ps1')
$matcher=New-ForbiddenMatcher -Words (Read-ForbiddenWords -Path $ForbiddenWordsFile -RepositoryRoot ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))))
& (Join-Path $PSScriptRoot 'Inspect-AndroidFlatApk.ps1') -Apk $Apk -Record (Join-Path $record 'manifest-inspection.txt') -AndroidPlayer $AndroidPlayer
if($LASTEXITCODE -ne 0){throw 'Phone Manifest inspection failed'}
$badging=Invoke-Checked (Join-Path $tools.FullName 'aapt2.exe') @('dump','badging',$Apk)
$package=@($badging | Where-Object {$_ -match '^package:'})
if($package.Count -ne 1 -or $package[0] -notmatch ("\bversionCode='"+$VersionCode+"'") -or $package[0] -notmatch ("\bversionName='"+[regex]::Escape($VersionName)+"'")){throw 'APK version mismatch'}
if($badging -match '^application-debuggable'){throw 'A distribution APK must not be debuggable'}
$oldJava=$env:JAVA_HOME
try {
    $env:JAVA_HOME=Join-Path $AndroidPlayer 'OpenJDK'
    $signature=Invoke-Checked (Join-Path $tools.FullName 'apksigner.bat') @('verify','--print-certs','-v',$Apk)
} finally {$env:JAVA_HOME=$oldJava}
if((Get-ForbiddenCount -Matcher $matcher -Bytes ([Text.Encoding]::UTF8.GetBytes(($signature -join "`n")))) -gt 0){throw 'Forbidden text in APK signing certificate (contents suppressed)'}
if(!($signature -match '^Verified using v2 scheme \(APK Signature Scheme v2\): true')){throw 'APK Signature Scheme v2 did not verify'}
$signers=@($signature | Where-Object {$_ -match '^Signer #\d+ certificate SHA-256 digest: ([0-9a-fA-F]{64})$'} | ForEach-Object {$Matches[1]})
if($signers.Count -ne 1 -or $signers[0] -ine $CertificateSha256){throw 'APK signing certificate mismatch'}
if($signature -match '^Signer #\d+ certificate DN: .*CN=Android Debug'){throw 'A distribution APK must not use an Android debug key'}
$signature | Set-Content -Encoding utf8 (Join-Path $record 'signature.txt')
$null=Invoke-Checked (Join-Path $tools.FullName 'zipalign.exe') @('-c','-P','16','-v','4',$Apk)
$alignment=Invoke-Checked 'python' @((Join-Path $PSScriptRoot 'Check-NativeAlignment.py'),$Apk)
$alignment | Set-Content -Encoding utf8 (Join-Path $record 'native-alignment.json')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive=[IO.Compression.ZipFile]::OpenRead($Apk);$count=0
try {
    foreach($entry in $archive.Entries){
        if((Get-ForbiddenCount -Matcher $matcher -Bytes ([Text.Encoding]::UTF8.GetBytes($entry.FullName))) -gt 0){throw 'Forbidden text in APK entry name (name suppressed)'}
        $stream=$entry.Open();$memory=New-Object IO.MemoryStream
        try {$stream.CopyTo($memory);$hits=Get-ForbiddenCount -Matcher $matcher -Bytes $memory.ToArray()} finally {$stream.Dispose();$memory.Dispose()}
        if($hits -gt 0){throw 'Forbidden text in APK contents (contents suppressed)'}
        $count++
    }
} finally {$archive.Dispose()}
$hash=(Get-FileHash -LiteralPath $Apk -Algorithm SHA256).Hash
[IO.File]::WriteAllText($Apk+'.sha256',($hash.ToLowerInvariant()+'  '+[IO.Path]::GetFileName($Apk)+"`n"),[Text.UTF8Encoding]::new($false))
$summary=[ordered]@{Apk=$Apk;Sha256=$hash;VersionName=$VersionName;VersionCode=$VersionCode;Package='com.zantetsuken.androidflat';MinSdk=32;TargetSdk=36;CertificateSha256=$signers[0].ToUpperInvariant();SignatureV2=$true;Debuggable=$false;Native16kAlignment=$true;ForbiddenEntriesScanned=$count;ForbiddenText='passed';Published=$false}
$summary | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $record 'inspection.json')
$summary | ConvertTo-Json
