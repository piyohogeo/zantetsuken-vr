# Quest ALPHA 配布：署名とバージョン管理

対象は、配布用 APK の署名と更新管理（D-238）、Android 設定と Manifest の指定項目（D-239）、配布対象の機種と `supportedDevices`（D-240）。Store へのアップロードやテスターへの配布は別工程（DESIGN 3.5）。正式配布鍵での検証、Dashboard の Package Name 照合、ALPHA 経由の配布・導入確認は残件。

## 入口

```
Tools\Quest3s\Build-Distribution.ps1 -VersionName <名> -VersionCode <正の整数> -Keystore <keystore の場所> -KeyAlias <alias> [-Output <apk>]
```

- 既存の全シナリオ APK のビルド（`Build-ScenarioPlayer.ps1` ＝ `QuestScenarioPlayerBuild`）に、薄い入口 `QuestDistributionBuild.BuildApk` を重ねたもの。シーンの選択・Commercial の判定・Bunny デモの除外・Vulkan プラグイン・IL2CPP の設定は既存処理のまま。
- **Release・Commercial を明示**：`-zantetsuBuildMode commercial` を常に付け、`-zantetsuQuestDev` があれば拒否する。素材（`Assets/Licensed` のシーン）が無いときは既存の方針どおり失敗し、Bunny デモへは切り替わらない。
- **package ID**：`com.zantetsuken.quest`（既存本体）。別の ID は指定できない。Meta の Developer Dashboard でアプリに登録されている **Package Name**（App ID とは別の識別子）がこれと違う場合は、署名の前に整合させる（未確認：Dashboard の Package Name。配布担当が確認する）。
- **事前の拒否**（Unity を起動せずに終了）：versionCode が 1〜2147483647 の数字でない、versionName に空白がある、keystore が無い・`.keystore`/`.jks` でない・リポジトリ内にある、パスワードの環境変数が無い、alias が keystore に無い（または keystore のパスワードが違う）、出力 APK が既にある。
- **Editor 側の拒否**（exit 2、ログに `QUEST DISTRIBUTION BUILD refused: <理由>`）：同じ検査を Unity の中でも行う（引数の欠落・重複・不正、mode が commercial でない、開発オプション）。デバッグ署名へ暗黙に切り替わることはない（`useCustomKeystore=true` と alias が無ければ Unity 自身が失敗する）。
- **ビルド中だけの設定**（identifier、versionName/versionCode、custom keystore、keystore 名、alias、2 つのパスワード）は `SettingsScope` が成功・失敗どちらでも元へ戻し、パスワードは空にする。さらにラッパーが `ProjectSettings/ProjectSettings.asset` をビルド前に保存し、終了後に差があれば元へ戻す（差があった事実は記録に残る）。
- 出力：既定 `Build\quest-alpha-<VersionName>-<VersionCode>.apk`（`Build/` は Git 管理外）。記録は `<apk>.record\`。

## 秘密情報の渡し方

- keystore のパスワードは環境変数 **`ZANTETSU_KEYSTORE_PASSWORD`**、鍵のパスワードは **`ZANTETSU_KEY_PASSWORD`** に、ビルドを行うシェルの中だけで設定する。コマンドライン引数にはしない（ラッパーは alias の確認に `keytool -storepass:env` を使い、Unity は環境変数を継承する）。ログ・記録・Git 管理ファイルには書かれない（記録には keystore の場所・alias・証明書の SHA-256 だけ）。
- 例（PowerShell 5.1 でもマスク入力。環境変数は `finally` で必ず消す）：

```
function Read-Secret([string]$prompt) {
    $secure = Read-Host -AsSecureString $prompt                      # 入力は画面に出ない
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}
try {
    $env:ZANTETSU_KEYSTORE_PASSWORD = Read-Secret 'keystore password'
    $env:ZANTETSU_KEY_PASSWORD = Read-Secret 'key password'
    .\Tools\Quest3s\Build-Distribution.ps1 -VersionName 0.2.0 -VersionCode 2 -Keystore D:\keys\zantetsu-alpha.keystore -KeyAlias zantetsu-alpha
}
finally {
    Remove-Item Env:\ZANTETSU_KEYSTORE_PASSWORD, Env:\ZANTETSU_KEY_PASSWORD -ErrorAction SilentlyContinue
}
```

- alias と versionName は Unity のコマンドラインで運ぶので空白を含められない（ラッパーが事前に拒否する）。

- 署名鍵（keystore）はリポジトリの外に置く（ラッパーがリポジトリ内の場所を拒否する）。`.gitignore` は `Build/` と `*.apk` を除外しているが、keystore はそもそもリポジトリに置かない。

## 鍵の作成・保管・バックアップ

- **配布鍵は配布担当（TL が指名する 1 人）が作り、保管する。** 検証用に作った使い捨ての鍵（`perf\distribution\test-key\test-only.keystore`、CN=ZANTETSU TEST ONLY）は配布には使わない。
- 作成（Unity 同梱の JDK の keytool。パスワードは環境変数から）：

```
$env:ZANTETSU_KEYSTORE_PASSWORD = ...; $env:ZANTETSU_KEY_PASSWORD = ...
& 'C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK\bin\keytool.exe' -genkeypair -keystore D:\keys\zantetsu-alpha.keystore -alias zantetsu-alpha -keyalg RSA -keysize 2048 -validity 10000 -dname 'CN=<組織>, O=<組織>' -storepass:env ZANTETSU_KEYSTORE_PASSWORD -keypass:env ZANTETSU_KEY_PASSWORD
```

- 保管：keystore ファイルとパスワードは別の場所に（例：ファイルは暗号化ドライブ、パスワードはパスワードマネージャ）。バックアップは 2 か所以上（オフライン媒体を 1 つ含める）。失えば同じ package の更新ができなくなる。
- 証明書の SHA-256（`keytool -list -v`）を記録し、以後のすべての配布 APK の署名者がこれと一致することを検査で確かめる。

## バージョンの更新手順

- `versionCode` は**自動増分しない**。配布担当が、**既に配布（アップロード）した最大の versionCode より大きい値**を明示する。手順：(1) 前回の `<apk>.record\build.txt` の `versionCode` と Dashboard の最新ビルドの番号を見る、(2) それより大きい整数を `-VersionCode` に与える、(3) `-VersionName` は人が読む版名（空白なし。例 `0.2.1`）。
- 同じ versionCode を再ビルドしない（出力 APK が既にあればラッパーが止まる）。
- 更新（上書きインストール）は同じ package ID・同じ署名鍵で versionCode が大きいときだけ成功する。デバッグ署名版（開発ビルド、`CN=Android Debug`）が入っている端末には配布鍵版を上書きできない（署名不一致、`INSTALL_FAILED_UPDATE_INCOMPATIBLE`）。これは配布鍵同士の更新試験とは別の事象で、端末のデータを消すアンインストールは**自動では行わない**（人が判断する）。

## Android 設定と Manifest（D-239）

配布入口は毎回、完成 APK が次の状態であることを保証する（Meta の Manifest 要件に基づく）。

| 項目 | 完成 APK の期待値 | 決め方 |
| --- | --- | --- |
| minSdkVersion | 32 | `SettingsScope` が `PlayerSettings.Android.minSdkVersion` に設定（ビルド中だけ） |
| targetSdkVersion | 34（自動選択にしない） | 同 `targetSdkVersion = AndroidApiLevel34` |
| compileSdkVersion | 記載される場合は target 以上 | Unity／SDK の既定のまま（実値は記録の `manifest.txt`；Unity 6000.3 は target と同じ値を書いた） |
| installLocation | auto（0） | `preferredInstallLocation = Auto` ＋ Manifest 後処理で `android:installLocation="auto"` |
| 起動 Activity | `excludeFromRecents=true` | Manifest 後処理：MAIN を持つ唯一の Activity（`com.unity3d.player.UnityPlayerGameActivity` でなければ失敗）に付与 |
| 起動用 intent-filter | MAIN と `com.oculus.intent.category.VR` | OpenXR（XR Management）の生成を活かし、無ければ category を追加 |
| headtracking | `uses-feature` required=true、version=1 | 同上、無ければ追加・値が違えば設定 |

- Manifest 後処理は `QuestDistributionManifest.Hook`（`IPostGenerateGradleAndroidProject`、order 10000＝XR Management の 1 の後）。配布ビルド中（`QuestDistributionManifest.Required`）だけ動き、生成された Gradle プロジェクトの **2 つの Manifest** を扱う：unityLibrary 本体 `unityLibrary/src/main/AndroidManifest.xml`（installLocation、起動 Activity の excludeFromRecents、intent-filter の VR category）と、XR Management が feature の要件を書く自前のライブラリ `unityLibrary/xrmanifest.androidlib/AndroidManifest.xml`（supportedDevices の meta-data、headtracking、focusaware）。Gradle のマージで、完成 APK では supportedDevices の meta-data が `application` 直下に、headtracking の `uses-feature` が `manifest` 直下に入る。足りない属性を最小限追加し（headtracking はどちらかに在ればそこで値を揃え、どちらにも無いときだけ本体に追加）、PackageCache の編集や Manifest の置き換えはしない。`ConfigureCommon` は呼ばない。
- min／target SDK と installLocation は `SettingsScope` が保存・適用・復元する（成功時・例外時とも）。

## 配布対象の機種（D-240）

初期 ALPHA の対象は **Quest 3 と Quest 3S** の 2 機種。完成 APK の `application` 直下：

```
<meta-data android:name="com.oculus.supportedDevices" android:value="quest3|quest3s" />
```

- 定義は 1 か所：`QuestDistributionTargets.Devices`（内部名 → 正規名：`eureka` → `quest3`、`quest3s` → `quest3s`）、`CanonicalValue = "quest3|quest3s"`。ラッパーの `$supportedDevices` はこれと一致していなければビルドが拒否する（`-zantetsuDistDevices`）。検査の期待値もラッパーから渡す（完成 APK から逆算しない）。
- ビルド中：`QuestDistributionTargets.Scope` が Android の OpenXR 設定の Meta Quest Support feature の `targetDevices` を `SerializedObject` で読み、対象 2 機種だけ `enabled` にし、終了後（例外時も）元へ戻す。内部名（`manifestName`）は書き換えない。feature が無い・無効・`targetDevices` が無い・対象機種が定義に無い場合は、設定変更前に理由を出して拒否する。`ConfigureCommon`／`RefreshFeatures`・PackageCache の編集はしない。
- Manifest：OpenXR の生成（XR Management のライブラリ Manifest `xrmanifest.androidlib` に `eureka|quest3s`）を配布 Hook が検査し、unityLibrary 本体とライブラリの `application` 直下を合わせて `com.oculus.supportedDevices` がちょうど 1 件・機種集合が定義と一致（内部名 `eureka` は `quest3` と読む）するときだけ、在る側で正規表記 `quest3|quest3s` に書く。不足・余計・未知名・重複・空は失敗（黙って補正しない）。再適用で変化なし、他の meta-data は保持。
- ラッパーは `ProjectSettings.asset` と `Assets/XR/Settings/OpenXR Package Settings.asset` をビルド前に保存し、終了後に差があれば戻す（記録に残る）。
- 機種ごとの実機検証状況：**Quest 3S**＝実機確認済み（テスト鍵）。**Quest 3**＝ALPHA 対象・実機未検証。テスターによる最初の検証項目：ALPHA での導入、起動、両眼表示、コントローラー操作、メニュー、通常プレイ時のフレーム時間。

## APK の検査

```
Tools\Quest3s\Inspect-DistributionApk.ps1 -Apk <apk> -ExpectedVersionName <名> -ExpectedVersionCode <番号> -ExpectedCertificateSha256 <64桁> -ExpectedSupportedDevices 'quest3|quest3s' [-Record <txt>] [-ManifestDump <txt>] [-BadgingDump <txt>]
```

設定値ではなく APK そのものから（Unity 同梱 SDK の `aapt2`・`apksigner`）。Manifest は `aapt2 dump xmltree` を木に読み（`Tools/Quest3s/ApkManifestChecks.ps1`）、属性の所属要素で判定する（別要素に同じ文字列があっても合格にしない；数値・16 進・`(type …)` 表記を扱う；読み取り・解析失敗は FAIL）：`aapt2` の 2 回の読み取り（badging と Manifest）が exit 0 で必要な出力を持つこと（読み取り失敗は検査失敗）、package ID／versionName／versionCode が指定値と一致、APK Signature Scheme v2 の検証に成功、署名者が 1 人で配布証明書（必須引数、64 桁の 16 進）と一致（Android debug 証明書でない）、`android:debuggable` が false か未指定；minSdk 32・targetSdk 34・compileSdk ≥ target（記載時）・installLocation が存在し 0・MAIN を持つ唯一の Activity が `UnityPlayerGameActivity` でその intent-filter に MAIN と VR category・その Activity に excludeFromRecents=true・manifest 直下の headtracking `uses-feature` が required=true／version=1。すべて成立で exit 0。対応機種：`application` 直下の `com.oculus.supportedDevices` がちょうど 1 件（Activity 内の同名は数えない）、値が存在し、`|` で分けた機種集合が `quest3` と `quest3s` の 2 つに一致（順序不問、大文字小文字は厳密、空値・空要素・重複・不足・余計・内部名 `eureka` は不合格）。期待値は `-ExpectedSupportedDevices`（必須）で渡す。記録には Manifest のダンプ（`manifest.txt`）・badging（`badging.txt`）・権限一覧・対応機種の期待値と実値も残す。ラッパーはビルド後にこれを自動で行い、`<apk>.record\inspection.txt` に残す。

記録（`<apk>.record\build.txt`）：package・versionName・versionCode、keystore の場所・alias・証明書 SHA-256、ソース commit、未コミット差分の有無（あれば `status.txt`／`working-tree.patch`）、ビルド条件、Unity のコマンド、ProjectSettings の復元の有無、APK の SHA-256。秘密情報は含まない。

## システムによる中断と復帰（D-241）の確認手順

配布ビルドは、Horizon のシステムUI（入力フォーカスの喪失）やスリープ（アプリケーションの停止）でゲームの進行を止め、復帰時に移動・斬撃・UI 操作が誤発火しない（DESIGN 4.6「停止理由を区別する」「中断と復帰」）。端末確認は通常の起動経路（設定注入・CPU 固定なし）で行う。

| 操作 | 確認内容 |
| --- | --- |
| 通常プレイ中にシステムUIへ移り、戻る | フォーカス喪失中にゲーム・入力が停止し、刀とポインターが消える。復帰後は正常操作できる |
| 手動メニューを開いたまま中断・復帰 | メニューと手動停止が残り、勝手に Resume しない |
| スティック／トリガーを保持して中断・復帰 | 勝手な移動・旋回・クリック・斬撃が出ず、離した後の操作が正常 |
| スライダー操作中に中断・復帰 | ドラッグが勝手に継続せず、設定値が変わらない |
| 電源ボタンでスリープし、復帰 | 停止時間分の進行や物理の急変がなく、設定が保持される |
| 中断・復帰を繰り返す | 停止解除漏れ、入力が戻らない状態、誤操作がない |
| コントローラーの追跡喪失・復帰 | 保持中のトリガーが新規クリックとして扱われない。ログ `ZANTETSU MENU: the input is not read: cause=RightNotTracked … / the input is read again` が実際の追跡喪失の証拠（cause が LeftNotValid／RightNotValid／ValueNotRead／PoseNotRead なら別の事象） |
| メニューで値を変え、閉じずにスリープ → 復帰 → アプリ再起動 | 変えた値が保持される（中断開始時の flush） |

実機での観測（Quest 3S、2026-10-17、v7）：システムUI・電源スリープ・HMD 脱着のそれぞれで XR フォーカス喪失による停止・復帰を確認した。`OnApplicationPause` 通知の有無は当時のログ（集約状態の変化のみ）では判定できない。HMD 脱着では OpenXR セッションが停止・再開する。**未解決の不具合（復帰時）**：再装着後に視点が 30〜50 cm 高くなる（Meta ボタン長押しの再センターが暫定回避策。原因は未切り分け、次の優先作業）。コントローラー単独の追跡喪失は遮蔽では再現しなかった（未確認）。

ログで見る行（遷移時のみ）：`XR FOCUS PAUSE: focus lost / focus back`、`GAME PAUSE: system pause began / ended; reasons=…; paused=…`、`GAME PAUSE: progress paused / resumed`、`ZANTETSU MENU: system pause began / ended …`、`ZANTETSU MENU: the walking input is the game's again`。HMD の脱着で実際にどの通知（`OnApplicationPause`、フォーカス）が来たかはログから記録する。通知が来なかったケースは停止成功として扱わない。

## 検証結果

- 2026-10-17、テスト鍵（配布鍵ではない）で：v1（versionCode 1）・v2（versionCode 2）の検査 10/10 PASSED、v1 → v2 の上書き更新に成功（起動、旋回速度の設定と設定ファイルが維持、v1 への後退は `INSTALL_FAILED_VERSION_DOWNGRADE`）。詳細は `C:\log\zantetsuken-vr\walk-city-2026-10-03\perf\distribution\DISTRIBUTION-1.md`。配布鍵での検証は、鍵の提供後に同じ手順で行う（未完了）。
