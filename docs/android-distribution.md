# WORLD CUTTER Android版の配布準備

Android版はGitHub Releasesから署名付きAPKを手動配布します。現在はローカルの配布候補を準備する段階であり、Release・タグ・GitHub上のDraftは作成しません。

## ビルドと検査

`Tools/AndroidFlat/Build-Distribution.ps1` は、既存のAndroid専用鍵を使って、API 36・min API 32・ARM64・Vulkan・IL2CPP ReleaseのAPKを生成します。applicationIdは `com.zantetsuken.androidflat` です。設定・非公開入力のsource欄・統計の一時設定は成功・失敗とも復元します。署名パスワードは環境変数またはWindowsで保護された資格情報から読み、コマンドラインへ出しません。

```powershell
Tools/AndroidFlat/Build-Distribution.ps1 -VersionName 0.1.0-alpha.1 -VersionCode 3 -Keystore <外部のandroid-play-upload.jks> -KeyAlias android-play-upload -SigningCredential <外部のandroid-play-upload.credential.clixml> -AsideDirectory <外部のビルド原本退避先>
```

`Inspect-DistributionApk.ps1` は最終APKの署名証明書・v2署名・非debuggable・版・非VR Manifest・16KB整列・展開したファイル名と内容の禁止語を検査し、隣の `.record` へ結果を置きます。APKの隣の `.sha256` は配布用です。ビルドログや `.record` は開発用の記録であり、配布ファイルには含めません。

統計は既存送信先・external区分で有効にします。ゲーム内で同意した場合に限り収集・送信します。追加の統計項目はありません。プライバシー文書は [android-privacy-policy.md](android-privacy-policy.md) に置き、アプリ内にも通信せずに読める本文を持たせます。公開名と問い合わせ先は確定待ちです。

## 初回導入と更新

配布対象はARM64とVulkanを利用できるAndroid 12L（API 32）以降です。実機検証済みはGalaxy S25 UltraとPixel 6 Proです。他端末の動作保証はまだありません。

APKのContent-Typeは `application/vnd.android.package-archive` として配布し、実際のダウンロード応答でも確認します。ローカル検証では汎用のContent-TypeだとEdgeがパッケージインストーラーへ渡さず、正しいAPK形式の応答で導入・更新できました。GitHubからの実ダウンロードは、公開指示を受けた後の確認とします。[GitHub Release Assets API](https://docs.github.com/en/rest/releases/assets#upload-a-release-asset)

利用者は公式のGitHub ReleaseからAPKをダウンロードし、ブラウザまたはファイル管理アプリから開きます。Androidが求める場合は、その提供元からのインストールを許可します。端末の検査・確認を通して導入します。更新も新しいAPKを同じ方法で開きます。

正式版は同じapplicationId・署名鍵を継続し、versionCodeを増やします。現在の検証APKはdebug署名のため、正式署名のAPKでそのまま上書きできません。検証端末の初回切替では、既存アプリの削除で設定と統計の同意状態が初期化されます。データを消す操作は、切替対象と影響を確認してから行います。

`Prepare-ReleaseAssets.ps1 -Apk <検査済みAPK>` はAPK・チェックサム・公開証明書指紋だけをBuild下へまとめます。公開前の残件と配布説明の草案も同じローカルフォルダへ置き、アップロードはしません。

署名鍵の保管先はリポジトリ外です。現在のDPAPI資格情報は同じWindowsユーザーとPCに依存します。鍵と、別PCでも復旧できるパスワードの保管を別媒体で行い、公開前に復旧方法を確認します。`Backup-SigningKey.ps1` は、プロセス環境変数 `ZANTETSU_SIGNING_BACKUP_PASSWORD` から新しいパスフレーズを読み、元の鍵を変更せず外部の新規 `.jks` へ可搬PKCS12として書き出して証明書を照合します。パスフレーズは安全な入力やパスワードマネージャーから設定し、チャットやコマンドラインに書かないでください。

## 公開前の残件

- S25 Ultraで今回の候補を確認する。Pixelでは正式署名APKのブラウザ導入と、同じ鍵による更新時の設定保持を確認済み。S25の既存データを削除する承認は得ていない。
- 初回同意ダイアログの横画面表示を実機で確認する。カメラ・操作・設定・ポリシー・読み込みは、Pixelを実際に回して確認済み。保存済みの拒否状態は維持している。
- 連続切断、中断復帰、ネットワーク断を確認する。16KB環境での実行は未確認として区別する。
- 公開名・問い合わせ先・施行日と、Cloudflareの通信・ログの説明を確定する。
- 同梱する第三者アセット・ライブラリのライセンス表記を確認する。
- 配布対応をコミットし、APKとソースの対応を記録する。タグとReleaseの作成は別の明示指示を受けてから行う。

両端末で記録した `DexFile.finalize` のAssertionErrorは、ゲームが継続していたことと原因未確定を既知事項として残します。Play Console・AAB提出・Data safetyフォーム・IARC・ストア画像・テスター登録・pre-launch reportは今回の配布工程から外します。API 36は維持します。

## テスター向け操作説明

左下の半透明円で前後移動と左右旋回を行います。視点の上下角は固定です。操作UI以外の画面をフリックすると、その位置と方向に沿って切断します。ジョイスティックと切断は同時に使えます。

右上のMenuで移動・旋回の速さ、FSR、描画倍率などを変更できます。Joystick positionは縦画面と横画面で別々に保存します。MultiDrawIndirectとDrawIndirectCountの変更はApply renderingでシーンをリセットして反映します。非対応項目はグレー表示になります。

統計送信は任意です。Menu > Statisticsで切り替え、Privacy policyで詳細を読めます。不具合の連絡にはアプリ版、端末モデル、OS、画面方向、描画設定、再現手順を添えます。個人情報を含む端末ログをそのまま公開しないでください。

## 今回の確認結果

`0.1.0-alpha.1`／versionCode 4の正式署名APKを作成し、署名・Manifest・版・非debuggable・全8個のsoの16KB整列・展開した禁止語検査に合格しました。EditModeは133件成功です。

Pixel 6 Proはユーザーの承認を受けて旧debug署名アプリを削除し、Edgeで取得したcode 3を通常のパッケージインストーラーから導入しました。続いて同じ正式署名のcode 4を同じ導線で更新し、移動速度1.74と統計の拒否状態が保持されること、インストールされたAPKのSHA-256が候補と一致することを確認しました。移動速度は確認後に1.49へ戻しています。統計はOFFのままです。切断・中断復帰・ポリシーの縦画面表示とページ送りも確認しました。

ダウンロードはGitHubへの公開を伴わず、一時ローカルHTTPをWi-Fi ADBの転送でブラウザへ届ける方法です。転送とサーバーは確認後に解除・停止しました。GitHubの実ダウンロードURLは公開後の確認です。S25 Ultraは既知のWi-Fi接続先を使用できず、今回の候補は未導入で、既存のデータは保持しています。16KB環境の実行、長時間の負荷確認、送信失敗からの復帰は残っています。

ユーザーがPixelを実際に横向きにした後、描画が1440×3120から3120×1440へ切り替わり、Safe Areaが2975×1440、垂直FOVが49.55028度になることを確認しました。横画面でメニュー、移動・旋回・フリック切断、ジョイスティック位置の変更と復元、ポリシー全4ページと前後・戻る操作、再読み込み時の中央ウィンドウと回転アニメーションを確認しました。ジョイスティックは左15%・下30%へ戻し、シーン再読み込み後もその値と統計OFFが保持されています。初回同意ダイアログは再表示させていないため、横画面の実機確認は残件です。画像・動画・ログは `Logs/android-flat/github-pixel-landscape-*` に保存しています。

可搬バックアップの補助処理は、破棄用の鍵で再暗号化・再読込・証明書一致・元パスワードでの読込拒否を確認しました。本番鍵の独立したバックアップはまだ実行していません。公開名・問い合わせ先はユーザーの回答に従い未定のままです。

ローカルの配布候補一式は `Build/github-distribution-0.1.0-alpha.1`、検証記録はAPK隣の `.record/device-validation.json` です。Release・タグ・GitHub上のDraftは作成していません。配布対応のコミットとAPKの対応は、同じ検証記録とローカルの `publication-plan.json` へ記録します。
