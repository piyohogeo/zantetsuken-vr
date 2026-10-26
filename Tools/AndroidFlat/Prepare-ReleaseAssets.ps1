#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$Apk,[string]$Destination)
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..')).TrimEnd('\')
$Apk=[IO.Path]::GetFullPath($Apk)
$inspection=Get-Content -Raw -Encoding utf8 ($Apk+'.record\inspection.json') | ConvertFrom-Json
$hash=(Get-FileHash -LiteralPath $Apk -Algorithm SHA256).Hash
if($hash -cne $inspection.Sha256 -or !$inspection.SignatureV2 -or $inspection.Debuggable -or $inspection.ForbiddenText -ne 'passed'){throw 'The current APK has not passed the distribution inspection'}
if(!$Destination){$Destination=Join-Path $project ('Build\github-assets-'+$inspection.VersionName+'-'+$inspection.VersionCode)}
$Destination=[IO.Path]::GetFullPath($Destination).TrimEnd('\')
if(!$Destination.StartsWith($project+'\Build\',[StringComparison]::OrdinalIgnoreCase)){throw 'Local staging must stay below this repository Build directory'}
if(Test-Path -LiteralPath $Destination){throw 'Local staging already exists; nothing is overwritten'}
$draft=Get-Content -Raw -Encoding utf8 (Join-Path $PSScriptRoot 'DistributionDraft.json') | ConvertFrom-Json
if($draft.candidate.versionCode -ne $inspection.VersionCode -or $draft.candidate.versionName -cne $inspection.VersionName -or $draft.candidate.sha256 -ine $hash){throw 'Update the distribution draft for this inspected APK before preparing release documents'}
$validationPath=$Apk+'.record\device-validation.json'
if(!(Test-Path -LiteralPath $validationPath -PathType Leaf)){throw 'The APK build-source record is missing'}
$validation=Get-Content -Raw -Encoding utf8 $validationPath | ConvertFrom-Json
$sourceCommit=[string]$validation.SourceCommit
if($validation.ApkSha256 -ine $hash -or $sourceCommit -notmatch '^[0-9a-fA-F]{40}$' -or $draft.candidate.sourceCommit -ine $sourceCommit){throw 'The build-source record does not match the inspected APK and draft'}
$templatePath=Join-Path $PSScriptRoot 'ReleaseNotes.template.md'
$notes=[IO.File]::ReadAllText($templatePath,[Text.Encoding]::UTF8)
$fileName=[IO.Path]::GetFileName($Apk)
$notes=$notes.Replace('@VERSION@',$inspection.VersionName).Replace('@CODE@',[string]$inspection.VersionCode).Replace('@APK@',$fileName).Replace('@HASH@',$hash.ToLowerInvariant()).Replace('@CERT@',$inspection.CertificateSha256).Replace('@SOURCE@',$sourceCommit)
if($notes -match '@[A-Z][A-Z0-9_]*@'){throw 'The release notes contain an unresolved placeholder'}
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($Apk)
try {
    $noticeEntry=$zip.GetEntry('assets/ThirdPartyNotices.txt')
    if(!$noticeEntry){throw 'The APK has no bundled third-party notices'}
    $noticeStream=$noticeEntry.Open();$noticeMemory=New-Object IO.MemoryStream
    try {$noticeStream.CopyTo($noticeMemory);$noticeBytes=$noticeMemory.ToArray()} finally {$noticeStream.Dispose();$noticeMemory.Dispose()}
} finally {$zip.Dispose()}
$hasher=[Security.Cryptography.SHA256]::Create()
try {$noticeHash=([BitConverter]::ToString($hasher.ComputeHash($noticeBytes))).Replace('-','')} finally {$hasher.Dispose()}
if($noticeHash -cne $inspection.ThirdPartyNoticesSha256){throw 'The bundled third-party notices have not passed inspection'}
$assets=Join-Path $Destination 'assets';[IO.Directory]::CreateDirectory($assets) | Out-Null
[IO.File]::WriteAllBytes((Join-Path $assets 'ThirdPartyNotices.txt'),$noticeBytes)
Copy-Item -LiteralPath $Apk -Destination (Join-Path $assets $fileName)
[IO.File]::WriteAllText((Join-Path $assets ($fileName+'.sha256')),($hash.ToLowerInvariant()+'  '+$fileName+"`n"),[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $assets 'signing-certificate-sha256.txt'),($inspection.CertificateSha256+"`n"),[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $Destination 'release-notes.draft.md'),$notes.Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))
$plan=[ordered]@{Status='local staging only';Published=$false;TagCreated=$false;GitHubDraftCreated=$false;VersionName=$inspection.VersionName;VersionCode=$inspection.VersionCode;Assets=@($fileName,"$fileName.sha256",'signing-certificate-sha256.txt','ThirdPartyNotices.txt');ApkContentType='application/vnd.android.package-archive';SourceCommit=$sourceCommit;SourceDirty=if($null -ne $validation.SourceClean){-not [bool]$validation.SourceClean}else{$null};DocumentationCommit=(git -C $project rev-parse HEAD);DocumentationDirty=([bool](git -C $project status --porcelain));VerificationScope=$draft.verificationScope;Checks=$draft.checks;Remaining=$draft.remaining}
$plan | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $Destination 'publication-plan.json')
"Local assets prepared: $Destination (no upload, release, draft or tag created)"
