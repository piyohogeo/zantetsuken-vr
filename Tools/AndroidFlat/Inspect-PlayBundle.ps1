#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Bundle,
    [Parameter(Mandatory=$true)][string]$VersionName,
    [Parameter(Mandatory=$true)][int]$VersionCode,
    [Parameter(Mandatory=$true)][string]$CertificateSha256,
    [Parameter(Mandatory=$true)][string]$ForbiddenWordsFile,
    [string]$AndroidPlayer='C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Data\PlaybackEngines\AndroidPlayer'
)
$ErrorActionPreference='Stop'
function Invoke-Checked([string]$Tool,[string[]]$ToolArguments) {
    $old=$ErrorActionPreference; $ErrorActionPreference='Continue'
    try { $lines=@(& $Tool @ToolArguments 2>&1 | ForEach-Object {"$_"}); $code=$LASTEXITCODE } finally { $ErrorActionPreference=$old }
    if($code -ne 0) { throw "tool failed ($code): $($lines | Select-Object -Last 4)" }
    return $lines
}
$Bundle=[IO.Path]::GetFullPath($Bundle)
$record=$Bundle+'.record'
[IO.Directory]::CreateDirectory($record) | Out-Null
$java=Join-Path $AndroidPlayer 'OpenJDK\bin\java.exe'
$tool=Join-Path $AndroidPlayer 'Tools\bundletool-all-1.17.2.jar'
$null=Invoke-Checked $java @('-jar',$tool,'validate',"--bundle=$Bundle")
$manifestLines=Invoke-Checked $java @('-jar',$tool,'dump','manifest',"--bundle=$Bundle")
[xml]$manifest=$manifestLines -join "`n"
$android='http://schemas.android.com/apk/res/android'
$root=$manifest.DocumentElement
if($root.GetAttribute('package') -cne 'com.zantetsuken.androidflat') { throw 'Unexpected bundle package' }
if($root.GetAttribute('versionCode',$android) -ne [string]$VersionCode -or $root.GetAttribute('versionName',$android) -cne $VersionName) { throw 'Bundle version mismatch' }
$sdk=$root.SelectSingleNode('uses-sdk')
if($sdk.GetAttribute('minSdkVersion',$android) -ne '32' -or $sdk.GetAttribute('targetSdkVersion',$android) -ne '36') { throw 'Bundle SDK mismatch' }
$app=$root.SelectSingleNode('application')
if($app.GetAttribute('debuggable',$android) -eq 'true') { throw 'Bundle is debuggable' }
$manifestLines | Set-Content -Encoding utf8 (Join-Path $record 'bundle-manifest.xml')
$signer=Join-Path $AndroidPlayer 'OpenJDK\bin\jarsigner.exe'
$signature=Invoke-Checked $signer @('-J-Duser.language=en','-J-Duser.country=US','-verify',$Bundle)
if(!($signature -match 'jar verified')) { throw 'Bundle JAR signature was not verified' }
$keytool=Join-Path $AndroidPlayer 'OpenJDK\bin\keytool.exe'
$cert=Invoke-Checked $keytool @('-J-Duser.language=en','-J-Duser.country=US','-printcert','-jarfile',$Bundle)
$actual=@($cert | Where-Object {$_ -match '^\s*SHA256:\s*(\S+)'} | ForEach-Object {$Matches[1] -replace ':',''})
if($actual.Count -ne 1 -or $actual[0] -ine ($CertificateSha256 -replace ':','')) { throw 'Bundle signing certificate mismatch' }
# The AAB uses the upload key. This local APK deliberately uses the existing public debug key to preserve device data.
$apkSet=Join-Path $record 'universal.apks'
$debugKey=Join-Path $env:USERPROFILE '.android\debug.keystore'
$null=Invoke-Checked $java @('-jar',$tool,'build-apks',"--bundle=$Bundle","--output=$apkSet",'--mode=universal','--overwrite',"--ks=$debugKey",'--ks-key-alias=androiddebugkey','--ks-pass=pass:android','--key-pass=pass:android')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($apkSet)
$apk=Join-Path $record 'universal.apk'
try { [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry('universal.apk'),$apk,$true) } finally {$zip.Dispose()}
& (Join-Path $PSScriptRoot 'Inspect-AndroidFlatApk.ps1') -Apk $apk -ExpectedTargetSdk 36 -Record (Join-Path $record 'apk-inspection.txt')
if($LASTEXITCODE -ne 0) { throw 'Generated APK inspection failed' }
foreach($package in @($Bundle,$apk)) {
    $alignment=Invoke-Checked 'python' @((Join-Path $PSScriptRoot 'Check-NativeAlignment.py'),$package)
    $alignment | Set-Content -Encoding utf8 ($package+'.alignment.json')
}
. (Join-Path $PSScriptRoot '..\ForbiddenWords.ps1')
$matcher=New-ForbiddenMatcher -Words (Read-ForbiddenWords -Path $ForbiddenWordsFile -RepositoryRoot ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))))
foreach($package in @($Bundle,$apk)) {
    $archive=[IO.Compression.ZipFile]::OpenRead($package)
    try {
        foreach($entry in $archive.Entries) {
            if((Get-ForbiddenCount -Matcher $matcher -Bytes ([Text.Encoding]::UTF8.GetBytes($entry.FullName))) -gt 0) { throw 'Distribution entry name contains forbidden text (name suppressed)' }
            $stream=$entry.Open();$memory=New-Object IO.MemoryStream
            try {$stream.CopyTo($memory); $hits=Get-ForbiddenCount -Matcher $matcher -Bytes $memory.ToArray()} finally {$stream.Dispose();$memory.Dispose()}
            if($hits -gt 0) { throw "Distribution artifact contains forbidden text ($hits matches; contents suppressed)" }
        }
    } finally {$archive.Dispose()}
}
$summary=[ordered]@{Bundle=$Bundle;Sha256=(Get-FileHash -LiteralPath $Bundle -Algorithm SHA256).Hash;VersionName=$VersionName;VersionCode=$VersionCode;Package='com.zantetsuken.androidflat';MinSdk=32;TargetSdk=36;CertificateSha256=$actual[0];AabSignature='upload key';LocalApkSignature='public debug key; Play will use its app signing key';Native16k=$true;ForbiddenText='passed';PlayConsoleUploaded=$false}
$summary | ConvertTo-Json | Set-Content -Encoding utf8 (Join-Path $record 'inspection.json')
$summary | ConvertTo-Json
