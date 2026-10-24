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
$fileName=[IO.Path]::GetFileName($Apk)
Copy-Item -LiteralPath $Apk -Destination (Join-Path $assets $fileName)
[IO.File]::WriteAllText((Join-Path $assets ($fileName+'.sha256')),($hash.ToLowerInvariant()+'  '+$fileName+"`n"),[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $assets 'signing-certificate-sha256.txt'),($inspection.CertificateSha256+"`n"),[Text.UTF8Encoding]::new($false))
$notes=@'
WORLD CUTTER Android @VERSION@ 配布説明の草案

ARM64・Vulkanを利用できるAndroid 12L（API 32）以降向けの初期検証版です。動作確認端末はGalaxy S25 UltraとPixel 6 Proです。他端末の動作保証はまだありません。

APKをダウンロードし、ブラウザまたはファイル管理アプリから開いてインストールしてください。Androidが求める場合は、その提供元からのインストールを許可し、端末の検査・確認を通して導入してください。更新も新しいAPKを開く手動方式です。

左下の円で前後移動と旋回、画面上のフリックで切断します。Menuで描画・ジョイスティックを調整できます。人型キャラクターの切断・欠損表現を含みます。

統計送信は任意です。Menu > Statisticsで変更し、Privacy policyで通信せずに詳細を読めます。

versionCode: @CODE@
APK: @APK@
SHA-256: @HASH@
署名証明書SHA-256: @CERT@

公式16KBエミュレーター（x86_64上のARM64変換実行）で起動・切断・中断復帰を確認しました。ARM64の16KB実機は未確認です。両端末のDexFile.finalize警告は、ゲーム継続を確認していますが原因未確定です。
'@
$notes=$notes.Replace('@VERSION@',$inspection.VersionName).Replace('@CODE@',[string]$inspection.VersionCode).Replace('@APK@',$fileName).Replace('@HASH@',$hash.ToLowerInvariant()).Replace('@CERT@',$inspection.CertificateSha256)
[IO.File]::WriteAllText((Join-Path $Destination 'release-notes.draft.md'),$notes.Replace("`r`n","`n")+"`n",[Text.UTF8Encoding]::new($false))
$plan=[ordered]@{Status='local staging only';Published=$false;TagCreated=$false;GitHubDraftCreated=$false;VersionName=$inspection.VersionName;VersionCode=$inspection.VersionCode;Assets=@($fileName,"$fileName.sha256",'signing-certificate-sha256.txt','ThirdPartyNotices.txt');ApkContentType='application/vnd.android.package-archive';SourceCommit=(git -C $project rev-parse HEAD);SourceDirty=([bool](git -C $project status --porcelain));Remaining=$draft.checks}
$plan | ConvertTo-Json -Depth 6 | Set-Content -Encoding utf8 (Join-Path $Destination 'publication-plan.json')
"Local assets prepared: $Destination (no upload, release, draft or tag created)"
