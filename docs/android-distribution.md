# WORLD CUTTER Android版の配布準備

Android版はGitHub Releasesから署名付きAPKを手動配布します。現在はローカルの配布候補を準備する段階であり、Release・タグ・GitHub上のDraftは作成しません。

## ビルドと検査

`Tools/AndroidFlat/Build-Distribution.ps1` は、既存のAndroid専用鍵を使って、API 36・min API 32・ARM64・Vulkan・IL2CPP ReleaseのAPKを生成します。applicationIdは `com.zantetsuken.androidflat` です。設定・非公開入力のsource欄・統計の一時設定は成功・失敗とも復元します。署名パスワードは環境変数またはWindowsで保護された資格情報から読み、コマンドラインへ出しません。

```powershell
Tools/AndroidFlat/Build-Distribution.ps1 -VersionName 0.1.0-alpha.1 -VersionCode 6 -Keystore <外部のandroid-play-upload.jks> -KeyAlias android-play-upload -SigningCredential <外部のandroid-play-upload.credential.clixml> -AsideDirectory <外部のビルド原本退避先>
```

`Inspect-DistributionApk.ps1` は最終APKの署名証明書・v2署名・非debuggable・版・非VR Manifest・16KB整列・展開したファイル名と内容の禁止語を検査し、隣の `.record` へ結果を置きます。APKの隣の `.sha256` は配布用です。ビルドログや `.record` は開発用の記録であり、配布ファイルには含めません。

統計は既存送信先・external区分で有効にします。ゲーム内で同意した場合に限り収集・送信します。追加の統計項目はありません。プライバシー文書は [android-privacy-policy.md](android-privacy-policy.md) に置き、アプリ内にも通信せずに読める本文を持たせます。公開名はhogehogeo、問い合わせ先は [GitHub Issues](https://github.com/piyohogeo/zantetsuken-vr/issues) です。施行日は初回公開日とし、本番で確認したCloudflare側のログ説明とともにcode 6のアプリ内本文へ反映しました。現在は未公開です。

## 初回導入と更新

配布対象はARM64とVulkanを利用できるAndroid 12L（API 32）以降です。実機検証済みはGalaxy S25 UltraとPixel 6 Proです。16KB互換性はRemote Test LabのGalaxy A56 5Gでも縦画面の基本動作を確認しました。他端末やA56の横画面・長時間性能の動作保証はまだありません。

APKのContent-Typeは `application/vnd.android.package-archive` として配布し、実際のダウンロード応答でも確認します。ローカル検証では汎用のContent-TypeだとEdgeがパッケージインストーラーへ渡さず、正しいAPK形式の応答で導入・更新できました。GitHubからの実ダウンロードは、公開指示を受けた後の確認とします。[GitHub Release Assets API](https://docs.github.com/en/rest/releases/assets#upload-a-release-asset)

利用者は公式のGitHub ReleaseからAPKをダウンロードし、ブラウザまたはファイル管理アプリから開きます。Androidが求める場合は、その提供元からのインストールを許可します。端末の検査・確認を通して導入します。更新も新しいAPKを同じ方法で開きます。

正式版は同じapplicationId・署名鍵を継続し、versionCodeを増やします。旧debug署名の検証APKには、正式署名のAPKをそのまま上書きできません。PixelとS25は正式署名へ切替済みです。検証端末の初回切替では、既存アプリの削除で設定と統計の同意状態が初期化されます。データを消す操作は、切替対象と影響を確認してから行います。

`Prepare-ReleaseAssets.ps1 -Apk <検査済みAPK>` はAPK・チェックサム・公開証明書指紋・APKから取り出した第三者表記をBuild下へまとめます。公開前の残件と配布説明の草案も同じローカルフォルダへ置き、アップロードはしません。

署名鍵の保管先はリポジトリ外です。現在のDPAPI資格情報は同じWindowsユーザーとPCに依存します。鍵と、別PCでも復旧できるパスワードの保管を別媒体で行い、公開前に復旧方法を確認します。`Backup-SigningKey.ps1` は、プロセス環境変数 `ZANTETSU_SIGNING_BACKUP_PASSWORD` から新しいパスフレーズを読み、元の鍵を変更せず外部の新規 `.jks` へ可搬PKCS12として書き出して証明書を照合します。パスフレーズは安全な入力やパスワードマネージャーから設定し、チャットやコマンドラインに書かないでください。

## 公開前の残件

- 長時間負荷は2026-10-24に実施。S25 Ultraの縦画面は同一シーンで30分完走した。Pixel 6 Proの横画面は旋回しながらでも約11分で頂点ブロック不足による自己終了に達したため、容量・保持／回収条件への対応方針と再試験が残る。Pixelの切断片が多い場面ではFPS低下も記録した。詳細は末尾の負荷試験結果を参照する。
- 通信失敗の制御試験と16KB環境の確認は下記の範囲で完了。Galaxy A56 5Gの横画面はラボで確認できなかった項目として残す。
- タグとReleaseの作成は別の明示指示を受けてから行う。公開後にGitHubの実ダウンロード応答と導入を確認する。配布対応のコミットとAPKの対応はローカル検証記録へ残す。

両端末で記録した `DexFile.finalize` のAssertionErrorは、ゲームが継続していたことと原因未確定を既知事項として残します。Play Console・AAB提出・Data safetyフォーム・IARC・ストア画像・テスター登録・pre-launch reportは今回の配布工程から外します。API 36は維持します。

## テスター向け操作説明

左下の半透明円で前後移動と左右旋回を行います。視点の上下角は固定です。操作UI以外の画面をフリックすると、その位置と方向に沿って切断します。ジョイスティックと切断は同時に使えます。

右上のMenuで移動・旋回の速さ、FSR、描画倍率などを変更できます。Joystick positionは縦画面と横画面で別々に保存します。MultiDrawIndirectとDrawIndirectCountの変更はApply renderingでシーンをリセットして反映します。非対応項目はグレー表示になります。

統計送信は任意です。Menu > Statisticsで切り替え、Privacy policyで詳細を読めます。不具合の連絡にはアプリ版、端末モデル、OS、画面方向、描画設定、再現手順を添えます。個人情報を含む端末ログをそのまま公開しないでください。

## 今回の確認結果

最新の候補は `0.1.0-alpha.1`／versionCode 6です。公開名・窓口・運用ログ説明を含む5ページのポリシーと第三者表記を同梱しました。署名・Manifest・版・非debuggable・全8個のsoの16KB整列・展開した禁止語検査に合格し、第三者表記435,739バイトがレビュー済み本文と一致しました。APKのSHA-256は `5A7502FCF9996E97856C3232E0D9BCD353C70960C9858164C4C9932550CACFC5` です。更新後のEditModeは133件成功です。code 6はS25で導入と縦横画面の実機確認を完了しました。以下のPixelの端末結果はcode 4までの履歴です。

Pixel 6 Proはユーザーの承認を受けて旧debug署名アプリを削除し、Edgeで取得したcode 3を通常のパッケージインストーラーから導入しました。続いて同じ正式署名のcode 4を同じ導線で更新し、移動速度1.74と統計の拒否状態が保持されること、インストールされたAPKのSHA-256が候補と一致することを確認しました。移動速度は確認後に1.49へ戻しています。統計はOFFのままです。切断・中断復帰・ポリシーの縦画面表示とページ送りも確認しました。

ダウンロードはGitHubへの公開を伴わず、一時ローカルHTTPをWi-Fi ADBの転送でブラウザへ届ける方法です。転送とサーバーは確認後に解除・停止しました。GitHubの実ダウンロードURLは公開後の確認です。code 4の確認時点ではS25のWi-Fi接続先を使用できず、既存データを保持していました。その後の正式署名への切替とcode 6の確認は後述します。この時点では16KB環境の実行・長時間負荷・通信失敗からの復帰が残っていました。2026-10-24の追加確認は後述します。

ユーザーがPixelを実際に横向きにした後、描画が1440×3120から3120×1440へ切り替わり、Safe Areaが2975×1440、垂直FOVが49.55028度になることを確認しました。横画面でメニュー、移動・旋回・フリック切断、ジョイスティック位置の変更と復元、ポリシー全4ページと前後・戻る操作、再読み込み時の中央ウィンドウと回転アニメーションを確認しました。ジョイスティックは左15%・下30%へ戻し、シーン再読み込み後もその値と統計OFFが保持されています。Pixelでは初回同意ダイアログを再表示させていませんでしたが、その後S25のcode 6で横画面の初回同意を確認しました。画像・動画・ログは `Logs/android-flat/github-pixel-landscape-*` に保存しています。

可搬バックアップの補助処理は、破棄用の鍵で再暗号化・再読込・証明書一致・元パスワードでの読込拒否を確認しました。その後、ユーザーがOneDrive内のリポジトリ外フォルダへ本番鍵の可搬バックアップを作成し、ランダム生成したパスフレーズを保存しました。バックアップファイルの存在・PKCS12形式と、元とは異なる暗号化コンテナーであることを確認しました。作成スクリプトは成功時に新しいバックアップの再読込と証明書照合を行います。続いて、OneDriveからブラウザーでダウンロードしたコピーに対し、保存済みパスフレーズのみで `keytool -certreq` による署名を行う手順を案内し、ユーザーから「復旧確認OK」の報告を受けました。元のDPAPI資格情報を使わない復旧確認を完了として記録しました。公開名・問い合わせ先は、その後のユーザーの指定によりhogehogeoとGitHub Issuesへ決定しました。code 6で確定文面を反映した再ビルドとパッケージ検査を完了しました。

最新のローカル配布候補一式は `Build/github-distribution-0.1.0-alpha.1-6`、検証記録はAPK隣の `.record/device-validation.json` です。旧code 4の一式と実機記録は履歴として保持します。Release・タグ・GitHub上のDraftは作成していません。配布対応のコミットとAPKの対応は、同じ検証記録とローカルの `publication-plan.json` へ記録します。

## 第三者表記と通信・ログの確認（2026-10-23）

code 4と最新候補code 6の `BuildReport.GetLatestReport()` から梱包元3,791件を棚卸ししました。非商用のStanford Bunny素材は含まれていません。市街地と人物はITHappyのMegacity・Casual Characters・Professional Characters、歩行はRBStudioのInertial Locomotion Animationsへ対応します。購入元はユーザーから提示された下記のページです。

| 素材 | 購入元・確認した条件 |
|---|---|
| Megacity | [CGTrader](https://www.cgtrader.com/3d-models/exterior/cityscape/megacity)／Royalty Free |
| Casual Characters | [CGTrader](https://www.cgtrader.com/3d-models/character/human-anatomy/characters-3-01)／Royalty Free |
| Professional Characters | [CGTrader](https://www.cgtrader.com/3d-models/character/other/cartoon-characters-3-06-professions)／Royalty Free |
| Inertial Locomotion Animations | [Fab](https://www.fab.com/listings/a7038492-23a2-4a35-91ae-8c4741a12d31)／公開商品ページの実表示でStandard LicenseとPersonal／Professionalの2区分を確認。購入時の区分は未確認 |

CGTraderの[規約21A](https://www.cgtrader.com/pages/terms-and-conditions)は独自形式でゲームに組み込んだ配布を認め、素材単体の再配布を禁じています。元のblend・FBX・uasset等を公開リポジトリや配布物へ置かず、UnityのPlayerデータと独自のランタイム形式として使用する方針を維持します。[Fab Standard License](https://www.fab.com/eula)は対応する他エンジンでの利用と組み込んだ作品の配布を認めています。ブラウザーで商品ページのStandard License表記とPersonal／Professionalの選択肢を確認しました。両区分の利用権は同じです。ユーザーは購入履歴の区分表示を確認できなかったため、購入時の区分は確認済みとは扱いません。購入記録は手元で保管し、公開物には含めません。

`Prepare-ThirdPartyNotices.py --inventory <監査JSON>` は、実際の梱包元に現れる17パッケージの提供ライセンス、UnityとAndroid NDKの提供する第三者表記、FSR 1と補助ヘッダーの許諾文を収集します。上流の文書一式を保守的に保持しており、記載されたすべてのコンポーネントがAndroid実行時に存在するという意味ではありません。生成先は `Assets/StreamingAssets/ThirdPartyNotices.txt` です。素材の出所・権利者も同じ文書に併記します。

配布ビルドは第三者表記がなければ停止し、APK検査は `assets/ThirdPartyNotices.txt` がレビューした本文とSHA-256まで一致することを確認します。配布ファイル準備ではAPKから本文を取り出し、APK・チェックサム・証明書指紋に加えて `ThirdPartyNotices.txt` を同梱します。

Cloudflareは本番設定を読み取り専用で確認しました。R2の `sessions/` は90日後に削除対象となるルールが有効です。Workerの呼び出しログは100%サンプリングで保存され、LogpushとTail Consumerは無効でした。配信ソースマップ内の元コードはローカルの受信コードと一致し、consoleへのログ出力はありません。IPはレート制限用HMACへ変換し、生のIPを統計JSONや制限用ストレージへ保存しない処理です。Cloudflare自身の通信処理・運用ログと統計レコードを区別してポリシーへ記載します。Workers Logsの保持上限は現行の[公式資料](https://developers.cloudflare.com/workers/observability/logs/workers-logs/)でFreeが3日、Paidが7日です。本番設定やWorkerコードの変更・再デプロイは行っていません。

ローカルの証拠は `Logs/android-flat/github-distribution-asset-inventory.json`、`github-third-party-notices.json`、`github-cloudflare-configuration-audit.json`、`github-code6-asset-inventory.json`、`github-fab-license-public-verification.json` です。認証値・統計レコードは公開文書へ含めません。

## S25 Ultraの正式署名版への切替（2026-10-23）

ユーザーからS25の設定初期化の承認を受け、Wi-Fi ADBでSM-S938Qへ接続しました。旧code 2と正式版の証明書が異なることを実際に確認してから旧アプリを削除し、code 6を導入しました。インストール済みbase APKのSHA-256は配布候補と一致しています。導入はADB経由であり、ブラウザー導入の確認とは区別します。

ユーザーが実際に横向きへ回した状態で2340×1080、Safe Area 2244×1080、垂直FOV 49.55028度を確認しました。初回同意ダイアログは全文とAgree／Don't sendの両ボタンが表示され、Don't sendの選択後にプレイできました。フリック切断と分離した破片、ジョイスティックの前進・旋回、メニュー、ポリシー全5ページとNext／Previous／Backを確認しました。

ホームへの中断から同じPIDで復帰し、統計OFFを維持しました。バックグラウンド・フォーカス喪失時のログも、未同意のため集計・送信しないことを示しています。Android 16／API 36、ページサイズ4096です。DexFile.finalizeの既知AssertionErrorは3回記録されましたがゲームは継続し、SRPでのstereoTargetEye設定警告も起動時にあります。クラッシュは記録されていません。その後ユーザーが縦向きへ戻し、1080×2340、Safe Area 1080×2244、垂直FOV 90度でメニュー・統計OFF・ポリシー全5ページとジョイスティックを確認しました。確認後はゲームを前面に戻しています。その後Pixelもcode 6へ更新して確認しました。この時点では長時間負荷・通信失敗からの復帰・16KB環境での実行は未確認でした。2026-10-24の追加確認は後述します。

画像・ゲームプロセスのログは `Logs/android-flat/github-s25-code6-*`、候補APKに対応する結果は `.record/device-validation.json` へ保存します。生の端末ログは公開物へ含めません。Release・タグ・GitHub上のDraftは作成していません。

## Pixel 6 Proのcode 6への更新（2026-10-23）

以前のmDNS接続先でWi-Fi ADBの再接続に成功しました。既存code 4のbase APKが検査済み候補とSHA-256まで一致することを確認してから、同じ正式署名のcode 6を `install -r` で更新しました。アプリデータは削除していません。導入後のbase APKのSHA-256もcode 6と一致しました。

移動速度1.49・旋回120・FSR1 ON／75%／Sharpness 0.50と統計のDeclined状態を保持し、初回同意は再表示されませんでした。Android 17／API 37、ページサイズ4096、Mali-G78ではVP3Cへフォールバックし、MultiDrawIndirect／DrawIndirectCountは利用不可のグレー表示でした。横画面3120×1440／Safe Area 2975×1440／垂直FOV 49.55028度で、切断のVFXと分離した破片、ポリシー5ページと新しい運用ログ・公開名・窓口の本文、同じPIDでのホーム中断復帰を確認しました。統計OFFを保ち、復帰時のログにも未集計・未送信が記録されています。

今回のプロセスではUnityエラー・Fatal exception/signalはなく、既知のDexFile.finalize AssertionErrorは2回記録されました。code 6のPixelは横画面を確認し、縦横の物理回転はS25のcode 6とPixelのcode 4の記録で区別します。長時間のFPS安定性を確認した試験ではありません。画像・ログは `Logs/android-flat/github-pixel-code6-*` に保存し、端末はゲームの前面表示へ戻しています。

## 通信失敗からの復帰の制御試験（2026-10-24）

S25 UltraのWi-Fi ADB接続で、製品と同じ `PlayStatisticsSender` と `PlayStatisticsUnityTransport` を別パッケージのDevelopment APKから動かしました。接続拒否から受信再開・HTTP 503から201・タイムアウトから201で、次の送信機会まで自動再試行しないこと、同じスナップショットを再送すること、成功後の重複を抑えることを確認しました。受信できた503・タイムアウトの前後は本文SHA-256も一致しています。送信中の同意撤回では中断・保留破棄・完了通知抑止・再送拒否を確認しました。4ケース成功です。

診断用の合成データと偽トークンだけをPCの127.0.0.1へ送り、本番Cloudflareには送っていません。製品アプリの同意や設定には触れていません。無線の物理的な切断や、ゲーム全体のバックグラウンド送信を再現した試験ではありません。後者の拒否状態での動作は前述の製品APK実機試験で確認しています。

再実行には `Build-StatisticsRecoveryProbe.ps1` で専用APKを作り、`Serve-StatisticsRecoveryCheck.py --record <ローカルJSONL>` を起動し、対象端末だけにWi-Fi ADBのreverseをtcp:18765とtcp:18766へ設定して導入・起動します。パッケージは `com.zantetsuken.statisticsrecoveryprobe` です。端末の `files/statistics-recovery.json` と受信記録を照合し、終了時に専用APK・転送・受信サーバーを撤去します。ビルドは本番送信設定があれば拒否し、共有設定を復元します。診断コードは専用defineとDevelopmentの両方で制限し、通常の配布ビルドには含めません。

証拠は `Logs/android-flat/github-statistics-recovery-validation.json` と `github-statistics-recovery-receipts.jsonl` です。今回の専用APKは撤去済みです。

## 16KBエミュレーターの実行（2026-10-24）

ユーザーのSDKライセンス同意を受け、独立したBuild下のSDKへGoogle公式Emulator 37.2.12と `system-images;android-36;google_apis_ps16k;x86_64` revision 7を導入しました。`adb shell getconf PAGE_SIZE` は16384、ABIはx86_64／arm64-v8a、native bridgeは `libndk_translation.so` です。配布候補code 6をそのまま導入し、導入済みbase APKのSHA-256一致を確認しました。

初回同意画面からDon't sendを選び、街の描画・ジョイスティック前進・Slash・切断後の分離した破片・設定メニュー・同じPIDでのホーム中断復帰を確認しました。VP3C-NATIVEの固定長パスで、MultiDrawIndirectは有効、非対応のDrawIndirectCountはグレー表示です。ゲームプロセスにUnityエラー・Fatal exception/signalは記録されず、拒否中の中断では未集計・未送信のログを確認しました。

これは[公式の16KBユーザー空間シミュレーション](https://developer.android.com/guide/practices/page-sizes)を使うx86_64環境で、ARM64コードを変換実行しています。ARM64の16KBカーネルを持つ実機の確認や、端末のFPS・長時間安定性の検証には代用しません。この時点ではARM64実機は未確認でした。続くRemote Test Labの結果は次節へ記載します。画像・ログ・結果は `Logs/android-flat/github-emulator16k-code6-*`、APKに対応する記録は `.record/device-validation.json` に保持します。

この時点では長時間負荷はユーザーの指定で後回しでした。その後の実施結果は末尾に記載します。配布資料と検証ツールはコミットし、実際のコミットSHA・配布APKのハッシュ・診断専用の追加分をローカルの検証記録へ対応づけます。Release・タグ・GitHub上のDraft・アップロードは行いません。

## 16KBのARM64実機での確認（2026-10-24）

ユーザーがSamsung Remote Test Labへの候補APKのアップロードを承認し、Edgeでログイン・16KB端末の予約・RDB接続を行いました。対象はGalaxy A56 5G（SM-A566B、Android 16／API 36）です。`PAGE_SIZE=16384`、`uname -m=aarch64`、ABIはarm64-v8a、native bridgeは0を確認しました。エミュレーターのARM64変換実行とは別の、ネイティブARM64実機の結果です。code 6をそのまま導入し、導入済みbase APKのSHA-256一致を確認しました。

縦画面1080×2340／Safe Area 1080×2248／垂直FOV 90度で、初回同意のDon't send、街の描画、ジョイスティック前進と旋回、Slashと分離した切断片、設定メニュー、ポリシー全5ページと戻る操作を確認しました。ホームから同じPIDで復帰し、Statistics OFFの表示と未集計・未送信のログを確認しました。GPUはSamsung Xclipse 540、VP3C-NATIVEの圧縮パス、MultiDrawIndirectとDrawIndirectCountが有効、subgroupは64です。確認プロセスにUnityエラー・Fatal exception/signal・DexFile.finalizeのAssertionErrorはありませんでした。長時間の安定性やFPS測定の結果ではありません。

A56の横画面は未確認です。ラボでの回転操作中にContent blocked／FLAG_SECUREの保護表示が出ました。確認時点のゲームのウィンドウにはFLAG_SECUREがなく、APKにもDevice Adminの要求はありませんでしたが、保護表示の発生源は確定していません。保護は変更せず通常起動へ戻し、ユーザーがラボの表示枠を横にした後もAndroid内容とアプリの実描画は縦のままでした。確認中にはDevice Adminの制限トースト、テストAPK撤去後のユーザー画像にはOneDriveの停止ダイアログも見えました。これらはゲームのクラッシュと混同せず、ラボ環境の観測事項として区別します。S25とPixelの縦横確認は前節の別の記録です。

テストAPKはこの専用リモート端末から削除し、一時的な向きの指定を戻し、RDBを切断・停止しました。ユーザーにはWeb Clientの予約終了を案内しています。証拠は `Logs/android-flat/github-a56-16k-*` とAPK隣の `.record/device-validation.json` へ保存します。生ログ・端末画像は公開ファイルへ含めません。配布APKは変更しておらず、Release・タグ・GitHub上のDraft・アップロードは行っていません。

## 連続切断・旋回の負荷試験（2026-10-24）

配布候補code 6の同一APKをWi-Fi ADBで操作し、USB・AC・ワイヤレス給電がないことを確認して計測しました。FSR 1 ON・描画倍率75%・Sharpness 0.50、移動1.49・旋回120と統計OFFを維持しています。S25は縦1080×2340／VP3C-NATIVE、Pixelは横3120×1440／VP3Cです。画面の向き・解像度・自動輝度は揃えていないため、端末間の電力比較を統制した試験ではありません。

最初の操作は前後と左右の旋回を交互に戻しており、切断グループが視界内に残りやすい条件でした。この条件ではS25が50.78秒、別の新規起動でも87.24秒、Pixelが69.76秒で頂点ブロック不足による自己終了へ達しました。グループ回収は破片・共有物・復元元の関連Boundsが視界外であることを要求します。この固定方向の容量到達は、回収を許す長時間試験やメモリリークの証拠とは区別します。

その後は4方向のフリックと2秒の右旋回を繰り返し、一部に前進成分を入れました。旋回を相殺せず、切断の合間に回収が動ける時間を設け、開始時のシーンリセット後は同一シーンを継続しています。

| 端末 | 結果 | FPS | バッテリー温度・熱状態 | 採取したPSSの最大値 |
|---|---|---|---|---|
| S25 Ultra | 30分完走・同じPID | 平均60.055、最初／最後の5分60.059／60.049 | 33.4→34.9℃、熱状態0 | 2164.8 MiB |
| Pixel 6 Pro | 約11分07秒で自己終了 | 10分39秒までの採取平均55.05、最後の30秒区間17.36 | 31.3→37.4℃、熱状態最大1 | 2603.8 MiB |

S25は実present間隔108,074件を採取し、時間のカバー率は約99.89%、p99は16.649ms、33.4ms超は2件、50ms超は0件でした。PSSは増減し、終了時は1931.8 MiBでした。Pixelは10分の画像で14FPSを表示し、667.17秒に`StorageCapacity`で終了しました。頂点ブロックは65,536中65,339使用、23連続必要な予約に対して最大連続空き17でした。頂点グループは665件解放済みのログがあるため、回収が全く動いていない説明には限定できません。Androidの終了履歴も`EXIT_SELF`／status 0です。容量・保持／回収条件と、重い場面で60FPSを満たさない挙動は未解決として残します。今回の試験では容量や回収の実装を変更していません。

FPSは[Androidの描画統計](https://developer.android.com/topic/performance/issues/slow-session)に従ってゲームのSurfaceViewのactual-present時刻から集計しました。電力は[BatteryManager](https://developer.android.com/reference/android/os/BatteryManager)に対応する電流・電荷量と電圧から、電池側の端末全体の概算を2通りで算出しています。

| 端末・電力の採取区間 | 電流サンプル×電圧の時間平均 | 電荷量差×平均電圧÷時間 | 電池残量 |
|---|---|---|---|
| S25：約30分03秒 | 1.964W | 2.337W（275.8mAh） | 96→91% |
| Pixel：中断前の約10分39秒 | 2.919W | 3.966W（170mAh） | 78→74% |

算出方法に差があるため、外部電力計の確定値やGPU単体の消費電力とは扱いません。画面・Wi-Fi・ADB入力／計測の負荷も含みます。Pixelの値を30分の測定結果へ外挿しません。

証拠は`Logs/android-flat/sustained-20261024/rotation30-validation.json`、各端末の`metrics.csv`、進捗JSONL、画像、採取したプロセスログです。ログは保持されていたダンプと途中からの継続取得であり、全時間の非致命的エラーを漏れなく捕捉したとは扱いません。両端末の既知のDex finalizer AssertionErrorも記録に残します。終了後は入力と専用ログ取得を停止し、S25の設定・統計OFFを画像で再確認してホームへ戻しました。Pixelはゲームプロセスが終了しています。両端末の導入済みAPKハッシュは候補と一致し、配布APKは変更していません。Release・タグ・GitHub上のDraft・アップロードは行っていません。
