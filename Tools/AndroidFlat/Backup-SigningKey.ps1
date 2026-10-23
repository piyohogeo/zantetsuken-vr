#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$Keystore,
    [Parameter(Mandatory=$true)][string]$SigningCredential,
    [Parameter(Mandatory=$true)][string]$Destination,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$KeyAlias='android-play-upload',
    [string]$BackupPasswordVariable='ZANTETSU_SIGNING_BACKUP_PASSWORD',
    [string]$AndroidPlayer='C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Data\PlaybackEngines\AndroidPlayer'
)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
foreach($path in @($Keystore,$SigningCredential,$Destination)){
    $full=[IO.Path]::GetFullPath($path)
    if($full.Equals($project,[StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($project+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Signing and backup files must stay outside the repository'}
}
$Destination=[IO.Path]::GetFullPath($Destination)
if(Test-Path -LiteralPath $Destination){throw 'Backup destination already exists; nothing is overwritten'}
if([IO.Path]::GetExtension($Destination) -ine '.jks'){throw 'Backup destination must be .jks (PKCS12 contents)'}
if([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($BackupPasswordVariable))){throw 'Set the backup passphrase in the named process environment variable from a secure prompt or password manager; do not put it on a command line'}
$keytool=Join-Path $AndroidPlayer 'OpenJDK\bin\keytool.exe'
function Invoke-Keytool([string[]]$ToolArguments){
    $old=$ErrorActionPreference;$ErrorActionPreference='Continue'
    try {$lines=@(& $keytool '-J-Duser.language=en' '-J-Duser.country=US' @ToolArguments 2>&1 | ForEach-Object {"$_"});$code=$LASTEXITCODE} finally {$ErrorActionPreference=$old}
    if($code -ne 0){throw 'Signing backup keytool operation failed (details suppressed)'}
    return $lines
}
function Digest([string[]]$Lines){
    $sha=@($Lines | Where-Object {$_ -match '^\s*SHA256:\s*(\S+)'} | ForEach-Object {$Matches[1] -replace ':',''})
    if($sha.Count -ne 1){throw 'Expected exactly one signing certificate'}
    return $sha[0]
}
$oldPassword=$env:ZANTETSU_BACKUP_SOURCE_PASSWORD
try {
    $credential=Import-Clixml -LiteralPath $SigningCredential
    if($credential -isnot [pscredential] -or $credential.UserName -cne $KeyAlias){throw 'Signing credential alias mismatch'}
    $env:ZANTETSU_BACKUP_SOURCE_PASSWORD=$credential.GetNetworkCredential().Password
    $before=Digest (Invoke-Keytool @('-list','-v','-keystore',$Keystore,'-alias',$KeyAlias,'-storepass:env','ZANTETSU_BACKUP_SOURCE_PASSWORD'))
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Destination)) | Out-Null
    $null=Invoke-Keytool @('-importkeystore','-noprompt','-srckeystore',$Keystore,'-srcalias',$KeyAlias,'-srcstorepass:env','ZANTETSU_BACKUP_SOURCE_PASSWORD','-srckeypass:env','ZANTETSU_BACKUP_SOURCE_PASSWORD','-destkeystore',$Destination,'-deststoretype','PKCS12','-destalias',$KeyAlias,'-deststorepass:env',$BackupPasswordVariable,'-destkeypass:env',$BackupPasswordVariable)
    $after=Digest (Invoke-Keytool @('-list','-v','-keystore',$Destination,'-alias',$KeyAlias,'-storepass:env',$BackupPasswordVariable))
    if($before -cne $after){throw 'Backup certificate does not match the signing key'}
    [pscustomobject]@{Backup=$Destination;Alias=$KeyAlias;CertificateSha256=$after;Protection='Portable PKCS12 passphrase; store passphrase separately and move the backup to independent storage'}
} finally {$env:ZANTETSU_BACKUP_SOURCE_PASSWORD=$oldPassword;$credential=$null}
