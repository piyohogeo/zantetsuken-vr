# Quest ALPHA 配布：署名とバージョン管理

対象は「配布用 APK の署名と更新管理」だけ。Target API・Manifest 全般・対応機種の調整は次の単位、Store へのアップロードやテスターへの配布は別工程（DESIGN 3.5）。

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

## APK の検査

```
Tools\Quest3s\Inspect-DistributionApk.ps1 -Apk <apk> -ExpectedVersionName <名> -ExpectedVersionCode <番号> -ExpectedCertificateSha256 <64桁> [-Record <txt>]
```

設定値ではなく APK そのものから（Unity 同梱 SDK の `aapt2`・`apksigner`）：`aapt2` の 2 回の読み取り（badging と Manifest）が exit 0 で必要な出力を持つこと（読み取り失敗は検査失敗）、package ID／versionName／versionCode が指定値と一致、APK Signature Scheme v2 の検証に成功、署名者が 1 人で配布証明書（必須引数、64 桁の 16 進）と一致（Android debug 証明書でない）、`android:debuggable` が false か未指定。すべて成立で exit 0。ラッパーはビルド後にこれを自動で行い、`<apk>.record\inspection.txt` に残す。

記録（`<apk>.record\build.txt`）：package・versionName・versionCode、keystore の場所・alias・証明書 SHA-256、ソース commit、未コミット差分の有無（あれば `status.txt`／`working-tree.patch`）、ビルド条件、Unity のコマンド、ProjectSettings の復元の有無、APK の SHA-256。秘密情報は含まない。

## 検証結果

- 2026-10-17、テスト鍵（配布鍵ではない）で：v1（versionCode 1）・v2（versionCode 2）の検査 10/10 PASSED、v1 → v2 の上書き更新に成功（起動、旋回速度の設定と設定ファイルが維持、v1 への後退は `INSTALL_FAILED_VERSION_DOWNGRADE`）。詳細は `C:\log\zantetsuken-vr\walk-city-2026-10-03\perf\distribution\DISTRIBUTION-1.md`。配布鍵での検証は、鍵の提供後に同じ手順で行う（未完了）。
