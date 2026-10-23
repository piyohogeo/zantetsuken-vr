#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$KeyDirectory,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$KeyAlias='android-play-upload',
    [string]$AndroidPlayer='C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Data\PlaybackEngines\AndroidPlayer'
)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
$KeyDirectory=[IO.Path]::GetFullPath($KeyDirectory).TrimEnd('\')
if ($KeyDirectory.Equals($project,[StringComparison]::OrdinalIgnoreCase) -or $KeyDirectory.StartsWith($project+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Upload keys must be outside the repository' }
$keystore=Join-Path $KeyDirectory 'android-play-upload.jks'
$credentialFile=Join-Path $KeyDirectory 'android-play-upload.credential.clixml'
$certificate=Join-Path $KeyDirectory 'android-play-upload-certificate.pem'
foreach($path in @($keystore,$credentialFile,$certificate)) { if(Test-Path -LiteralPath $path) { throw 'An upload key or credential already exists; nothing is overwritten' } }
$keytool=Join-Path $AndroidPlayer 'OpenJDK\bin\keytool.exe'
if(!(Test-Path -LiteralPath $keytool)) { throw 'keytool is unavailable' }
[IO.Directory]::CreateDirectory($KeyDirectory) | Out-Null
$bytes=New-Object byte[] 36
$rng=[Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$password=[Convert]::ToBase64String($bytes)
$secure=ConvertTo-SecureString -String $password -AsPlainText -Force
# Windows DPAPI: only this Windows user on this PC can decrypt this credential. No plaintext password file.
[pscredential]::new($KeyAlias,$secure) | Export-Clixml -LiteralPath $credentialFile
$oldStore=$env:ZANTETSU_KEYSTORE_PASSWORD; $oldKey=$env:ZANTETSU_KEY_PASSWORD
try {
    $env:ZANTETSU_KEYSTORE_PASSWORD=$password; $env:ZANTETSU_KEY_PASSWORD=$password
    & $keytool '-J-Duser.language=en' '-J-Duser.country=US' -genkeypair -noprompt -alias $KeyAlias -keyalg RSA -keysize 4096 -sigalg SHA256withRSA -validity 10000 -storetype PKCS12 -keystore $keystore '-storepass:env' ZANTETSU_KEYSTORE_PASSWORD '-keypass:env' ZANTETSU_KEY_PASSWORD -dname 'CN=WORLD CUTTER Android Upload'
    if($LASTEXITCODE -ne 0) { throw 'Upload key generation failed; preserve the protected credential for recovery' }
    & $keytool -exportcert -rfc -keystore $keystore -alias $KeyAlias '-storepass:env' ZANTETSU_KEYSTORE_PASSWORD -file $certificate
    if($LASTEXITCODE -ne 0) { throw 'Public certificate export failed' }
} finally {
    $env:ZANTETSU_KEYSTORE_PASSWORD=$oldStore; $env:ZANTETSU_KEY_PASSWORD=$oldKey
    $password=$null; [Array]::Clear($bytes,0,$bytes.Length)
}
[pscustomobject]@{Keystore=$keystore;Alias=$KeyAlias;ProtectedCredential=$credentialFile;PublicCertificate=$certificate;Protection='Windows DPAPI / current user and PC'}
