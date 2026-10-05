# Meta XR Simulator VRS fixture ツール

Meta XR Simulator の session capture（VRS）を、VP3 / VP3C の画像回帰テスト用 fixture として自動生成・検証する。
対象はテスト用 fixture だけで、製品 Runtime、Scene、XR / URP 設定、DESIGN.md は変更しない。

運用の考え方は「録画を毎回手で作る」ではなく、「自動生成して品質検査と検証に通った録画だけを標準 fixture にする」。
通常の画像回帰テストは、検証済み fixture を公式の自動再生で使うだけで、録画自動化（非公開 gRPC）を必要としない。

## v207 への移行（2026-09-28）

- `XrSim.psm1` の Simulator の場所と期待する版を v207.0（207.0.0.18.123）に変えた。
- v207 で確認したのは再生経路だけ：
  - プロセス限定の runtime 指定
  - 両眼描画と頭部姿勢
  - 既存 VRS の公式自動再生（`session_capture`）
  - 準備完了後の開始同期
  - 設定の復元と正常終了
- 既存の `moving.vrs` は、v205 と同じ姿勢を再現した（静止区間 0.000 m／0.00°）。記録は `C:\log\zantetsuken-vr\MobPlanSlash\xrsim207\VERIFY.md`。
- 録画生成（非公開 gRPC の `Adapters/xrsim_v205_session_capture.py`）は v205 専用のまま。v207 では動かず、対応もしない。
- v205 の性能値とは、同じ条件の比較にしない。

## 公式手順と非公開手段の区別

| 手段 | 位置づけ | このツールでの使い方 |
| --- | --- | --- |
| Simulator UI の「Record session」 | Meta が文書化した録画手順 | 手作業で録る場合の代替。ツールは使わない |
| `persistent_data.json` の `session_capture`（`exec_state: replay`） | Meta が文書化した standalone アプリの自動再生 | 検証と回帰テストの再生はすべてこれだけを使う |
| SessionCapture gRPC（`GotoRecord` / `GotoIdle` / `GetState`） | **非公開**。v205 の `SIMULATOR.dll` に埋め込まれた定義から読んだもの | `Adapters/xrsim_v205_session_capture.py` に隔離し、録画の開始・停止だけに使う |

非公開 gRPC は Meta XR Simulator v205 専用の実験手段で、公開 API ではない。アダプターは次のどれかが確認できなければ失敗する。

- インストール済み `MetaXRSimulator.exe` と、対象プロセスのランタイムログの版がどちらも 205.x
- 対象プロセスのループバックポートで SessionCapture サービスが応答する
- 各コマンドの応答に Result エラーがなく、状態が IDLE→RECORD→IDLE と遷移する

別の版の Simulator では録画生成は動かない。その場合も、検証済み fixture の再生（公式手順）は版の確認をしたうえで利用を検討する。

## 前提条件

- Meta XR Simulator v205 standalone が `C:\Program Files\MetaXRSimulator\v205.0` にある
- Simulator のランタイムトグルはオフ（システムの OpenXR ランタイムは変更しない。ツールは Player プロセスにだけ `XR_RUNTIME_JSON` を渡す）
- Unity 6000.3.22f1（`C:\Program Files\Unity\Hub\Editor\6000.3.22f1`）、Standalone の Scripting Backend が IL2CPP、グラフィックス API が D3D11、Stereo が Single Pass Instanced
- `Assets/Licensed/DisplayMeshes/` の表示メッシュ（git 管理外）がある
- Python 3.8 以降が PATH にある。初回は WorkRoot に venv を作り `requirements.txt` を pip で入れる（ネットワークが必要）
- ログインしたデスクトップの対話セッションで実行する（録画生成は Simulator ウィンドウを前面にしてキー入力を送る）
- 前面を奪うダイアログが出ていない。Player を**初めてのパスで**起動すると、Windows ファイアウォールの通知（「Windows セキュリティ」）が出て前面を占有し、生成が「前面にできない」で失敗する。ダイアログを閉じてから再実行する。通信はループバックだけで、ループバックはファイアウォールの対象外なので、受信許可は不要（閉じてブロックのままでよい）
- **Player のパスを固定する。** この通知は実行ファイルのパス単位で、一度答えれば同じパスでは再び出ない（2026-09-16: 最初のバッチで 1 回出たあと、同一パスの 4 バッチでは出ていない）。Player の場所は既定で `WorkRoot\player` なので、WorkRoot を一時ディレクトリやセッション毎のディレクトリにすると、そのたびにパスが変わって通知が再発する。`-PlayerDirectory "$env:LOCALAPPDATA\Zantetsu\XrSim\player"` のように固定した場所を使う（`Build-XrSimHarnessPlayer.ps1` の `-PlayerDirectory` は必須引数で既定値はない。生成・再生スクリプトは WorkRoot 既定 `%LOCALAPPDATA%\Zantetsu\XrSim` の下の `player\` を見るので、この場所に揃えると一致する）。ハーネスを作り直して同じパスに上書きする分には再発しない
- **起動前に、対象 exe だけに適用する受信ルールを確認・準備する（標準手順）**。通知が出るのは Player が非ループバックで待ち受けるため。2026-09-16 の実測（`Get-NetTCPConnection` / `Get-NetUDPEndpoint`、読み取りのみ）:
  - `zantetsuken-vr.exe`（Player）: TCP `0.0.0.0:55000` Listen、UDP `0.0.0.0:63746`、IPv6 ワイルドカード `:::14081` ほか。Development ビルドの Player 接続（プロファイラ）とみられる
  - Simulator との IPC は `::1` / `127.0.0.1` のみ（`synth_env_server` `::1:33792`、`MetaXRSimulator` `::1:33794` など）。ループバックは受信ルールの対象外なので、Player への受信ブロックは IPC を壊さない
  - `local_sharing_server.exe` は `:::33793` で非ループバック待ち受け（過去の通知で Allow ルールが作られている）
  - `MetaXRSimulator` と `zantetsuken-vr` は `2a03:2880:…:443`（Meta の IPv6 範囲）へ**外向き**接続する。受信ルールでは止まらない
  - 方針: その exe 専用の受信**ブロック**ルール（TCP / UDP）を第一候補とする。全プロファイルの通知 OFF、ファイアウォール停止、広域の許可、ダイアログの自動クリックはしない。ルール作成には管理者権限が必要

## 構成

| ファイル | 役割 |
| --- | --- |
| `XrSim.psm1` | 共通処理（プロセス限定のランタイム指定、レジストリ照合、Simulator 設定のバイト退避・復元、git 状態の比較、Simulator ウィンドウの特定・前面化とキー入力、Python venv） |
| `Build-XrSimHarnessPlayer.ps1` | 検証用ハーネス Player をリポジトリ外にビルドする |
| `Harness/XrSimHarness.cs`, `Harness/Editor/XrSimHarnessBuild.cs` | ハーネス。ビルド時だけ `Assets/_XrSimHarness/` にコピーし、ビルド後に削除する |
| `New-XrSimFixture.ps1` | 録画を生成し、品質検査に通ったものを候補（candidate）にする |
| `Test-XrSimFixture.ps1` | 候補を公式の自動再生で検証し、検証済みのときだけ正式 fixture に昇格する |
| `Invoke-XrSimReplay.ps1` | fixture を公式の自動再生で再生して撮影する（回帰テストの再生にも使う） |
| `Adapters/xrsim_v205_session_capture.py` | 非公開 gRPC の v205 専用アダプター（録画の開始・停止だけ） |
| `Python/xrsim_fixture.py` | 事前確認、録画の品質検査、検証評価 |
| `requirements.txt` | Python 依存（grpcio, numpy, pillow, scipy） |

## 作業ディレクトリ（WorkRoot）

既定は `%LOCALAPPDATA%\Zantetsu\XrSim`。リポジトリ内は指定できない。

| パス | 内容 |
| --- | --- |
| `player\` | ハーネス Player と `player-manifest.json`（git HEAD、ハーネスと実行ファイルの SHA-256） |
| `generation\<view>-<日時>\attempt-N\` | 生成の各試行（Player ログ、`frames.csv`、`events.jsonl`、`recording.vrs`、`fixture.json`、`targets.json`） |
| `generation\<view>-<日時>\generation-summary.json` | 全試行の結果と理由、設定復元とレジストリ・git の確認結果 |
| `rejected\<batch>-attempt-N\` | 不良として隔離した試行（`REJECTED.txt` に理由） |
| `verification\<id>-<日時>\` | 検証の再生 run、`verification.json`、`verification.md` |
| `fixtures\<名前>\` | 正式 fixture（検証済みの VRS、`fixture.json`、`targets.json`、検証結果） |
| `replay\` | 回帰テストでの再生結果 |
| `.venv\` | Python venv |

## 手順

PowerShell（Windows PowerShell 5.1）で実行する。

### 1. ハーネス Player のビルド

```powershell
.\Tools\XrSim\Build-XrSimHarnessPlayer.ps1 -PlayerDirectory "$env:LOCALAPPDATA\Zantetsu\XrSim\player"
```

- 一時ファイル（ハーネスのソース、Resources マテリアルと各 `.meta`）を追加してビルドし、終了後に 1 件ずつ削除する
- ビルドが書き換える追跡設定（`Assets/Settings`、`ProjectSettings`、`Packages`）はビルド前のバイト列から戻す
- 実行前後で git の HEAD と `status --porcelain --untracked-files=all` が一致しなければ失敗する
- ハーネスの C# を変えたら再ビルドする

### 2. 録画の生成

```powershell
.\Tools\XrSim\New-XrSimFixture.ps1 -View along -MaxAttempts 3
```

1 試行の流れ:

1. ハーネス Player を hold モードで、`-xrsimIgnoreFocus 1` を付けて起動し、READY を待つ。XR 起動、Single Pass Instanced かつ D3D11、Simulator 205.x、Unity カメラが XR 頭部に追従していること（カメラのローカル姿勢と頭部姿勢の差が 2 mm / 0.2° 以内）を確認する。フラグの意味と根拠は下の「既知の制約」を参照
2. Simulator ウィンドウを前面にし、ウィンドウ内をクリックして入力先にする。ウィンドウはタイトル `Meta XR Simulator` で特定する（プロセスの `MainWindowHandle` は Meta のデータ設定ダイアログを指すことがあり、そこへキーを送っても頭部は動かない。下の「既知の制約」を参照）
3. アダプターで版と接続を確認する
4. 頭部を少し前後に動かし（W / S）、頭部が動いて戻ること、カメラが追従していることを確認する
5. アダプターで録画を開始し、キーで 3 姿勢を作る（P1 静止 → W・← で P2 → D・R で P3、各 2.0 秒静止 = `-HoldSeconds` の既定）。移動と yaw だけを使い、pitch（↑ / ↓）は使わない（理由は下の「再生の回転忠実度」）。キーごとに Simulator ウィンドウが前面であることを確認する。各姿勢 2.5 秒だと録画が 12.01 秒になり 8〜12 秒の範囲をわずかに超えたため既定を 2.0 秒にした（実測 11.54 秒。静止判定の 0.5 秒に対しては十分な余裕がある）
6. アダプターで録画を停止し、Player を終了する
7. 品質検査を行う

品質検査（どれかに当てはまると不良）:

- ハーネスの失敗、READY 時の非追従、前面・入力先の喪失、アダプターの失敗
- Simulator ログの IDLE→RECORD が 1 回でない、VRS がない
- 録画時間が 8〜12 秒の範囲外
- 0.5 秒以上静止した姿勢が 3 つない。P1・P2・P3 のどの 2 つも 10 cm 以上または 10° 以上離れていない。P1→P3 が 25 cm 以上かつ 20° 以上でない。記録姿勢が Simulator の既定頭部姿勢の近くに留まる
- 録画中のどこかのフレームでカメラが頭部に追従していない
- 左右眼の view 行列の中心がカメラから 2 cm 以上ずれる、眼間距離が 4〜9 cm の範囲外、projection 行列が録画中に変わる

不良の試行は `rejected\` に移し、正式名にはしない。再試行は `-MaxAttempts` まで行い、全試行の理由を `generation-summary.json` に残す。
**再試行で通っても、カメラ非追従などの原因が解決したことにはならない。**

### 3. 検証と昇格

```powershell
.\Tools\XrSim\Test-XrSimFixture.ps1 -CandidateDirectory <generation\...\attempt-N> -Runs 3 -MinValidRuns 2 -FixtureName along-moving-v1 -Promote -HideSimulatorWindow
```

- 公式の自動再生（`session_capture`、`delay_start_ms` 既定 20000、`quit_when_complete: true`）で VP3 と VP3C を交互に各 `-Runs` 回、VRS なしの対照を VP3・VP3C 各 1 回実行する
- `persistent_data.json` は run ごとに元のバイト列へ戻す（失敗時も）
- 撮影は再生中に限る。地点の対応付けは再生状態と頭部姿勢の順序で行い、再生開始からの時刻だけでは決めない。P2 に先に達したら P1 は「飛ばした」として無効にする
- 各地点で、頭部が目標姿勢の 1 cm / 0.5° 以内に 8 フレーム静止したときに、左右眼の画像、URP の shadow atlas、VP3C の選択数を保存する

成功数から除外する run（理由を `verification.json` / `verification.md` に残す）:

- ハーネスの失敗、結果ファイルなし
- Player が視点を固定する前に再生が始まった（`replay_opened_before_ready`）
- 地点を飛ばした（1 地点先を撮った）、再生完了後にしか撮れなかった、タイムアウト
- 撮影フレームでカメラが頭部に追従していない
- 撮影時の頭部姿勢が録画の姿勢と 1 cm / 0.5° 以上違う

「検証済み」の条件:

- 録画ファイルの SHA-256 が `fixture.json` と一致する
- P1・P2・P3 のそれぞれで、有効な VP3 撮影と有効な VP3C 撮影が `-MinValidRuns` 件以上ある
- VRS なしの対照が有効で、録画の非既定姿勢を再現していない

画像の差（差分画素率、最大連結差分領域、明るくなった／暗くなった連結領域、atlas の texel 差、VP3C の選択数）は報告するが、検証済みかどうかの判定には使わない。
`-Promote` を付けると、検証済みのときだけ `fixtures\<名前>\` にコピーする（既存は上書きしない）。

### 4. 回帰テストでの再生

```powershell
.\Tools\XrSim\Invoke-XrSimReplay.ps1 -FixtureDirectory "$env:LOCALAPPDATA\Zantetsu\XrSim\fixtures\along-moving-v1" -Sequence VP3:1,VP3C:1 -HideSimulatorWindow
```

- `fixture.json` の status が `verified` で、VRS の SHA-256 が一致する fixture だけを受け付ける
- gRPC は使わない

## 状態の保護

- OpenXR ランタイムは Player プロセスにだけ `XR_RUNTIME_JSON` で指定する。HKLM の `ActiveRuntime` は読み取って前後で比較するだけで、書き換えない。Simulator がシステムランタイムになっていれば実行を拒否する
- `%APPDATA%\MetaXR` の設定ファイル（ログを除く）を実行前にバイト単位で保存し、終了時（失敗時も）に書き戻す。実行中に新しく作られた設定ファイルは削除する。Simulator のログは残す
- Simulator ウィンドウは実行前に起動していなければ、終了時に閉じる
- git の HEAD と status（未追跡ファイルを含む）が実行前後で一致することを確認する
- Player、録画、画像、ログはすべて WorkRoot に置く

## 既知の制約と未解決事項

- **Meta のデータ設定ダイアログ（ウィンドウの取り違え）**: Simulator のプロセスは、本体ウィンドウ（タイトル `Meta XR Simulator`、クラス `WinUIDesktopWin32WindowClass`）のほかに、タイトルなしのデータ設定ダイアログ（「About your data preferences」、320x500、同じクラスなので `#32770` では判別できない）を持つ。`Get-Process` の `MainWindowHandle` はこのダイアログを指すため、前面化とクリックは成功しても W / S などのキーはダイアログに入り、頭部は 1 mm も動かない（2026-09-16 に、事前確認が「入力が届かない（0.000 m）」で連続失敗して判明。ウィンドウを列挙してタイトルで本体を選ぶと、W の 1 打で頭部が 0.625 m 動いた）。そのため `Get-XrSimSimulatorWindow` は `MainWindowHandle` を使わず、タイトルで本体ウィンドウを探す。なお本ツールは `%APPDATA%\MetaXR` の設定を実行後に必ず元へ戻すので、`device_data\tools_consent.txt`（`ToolsNotified: null`）も戻り、次回起動でこのダイアログがまた出る。ダイアログの同意操作は利用者の判断なので、ツールはボタンを押さない
- **カメラ非追従（Player のフォーカス依存）**: Unity カメラ（シーンの TrackedPoseDriver、Input System 経由で `<XRHMD>/centerEyePosition` を読む）は、Player ウィンドウに OS のフォーカスがある間だけ XR 頭部に追従する。Simulator ウィンドウにフォーカスがある間は頭部が動いてもカメラは止まり、Player にフォーカスが戻ったフレームで追いつく（2026-09-16 に 2 往復で確認。プロジェクト設定は Run In Background 0、Input System 1.20.0、設定アセットなし＝既定の background behavior）。録画生成はキー入力のため Simulator にフォーカスが必要なので、この条件のままでは生成の事前確認・品質検査で必ず不良になる（2026-09-16 の実測: Simulator を前面にして W を送ると頭部は 0.625 m 動くが、同じフレームでカメラのローカル姿勢は 0 のままで `follow_error_m` = 0.625）。再生 run でも、Simulator ウィンドウが Player からフォーカスを奪うとその run は非追従として除外される。**対処（2026-09-16、生成のみ）**: 試験専用ハーネスに `-xrsimIgnoreFocus 1`（既定 OFF）を追加し、`Application.runInBackground = true` と Input System の `backgroundBehavior = IgnoreFocus` を実行時に設定する。同一ビルド・同一操作での A/B 実測: フラグありは頭部 0.5972 m に対しカメラのローカル姿勢も 0.597 で `follow_error_m` 0.0000、フラグなしはカメラが 0.000 のままで `follow_error_m` 0.5972。左右眼の view 行列も頭部と同量（0.5972）動き、眼間 6 cm と projection は不変。`New-XrSimFixture.ps1` はこのフラグを付けて Player を起動する。`environment.json` に `ignore_focus` / `run_in_background` / `input_background_behavior` を記録する。なお IgnoreFocus はフォーカス喪失時にデバイスを reset・disable しなくなる設定で、XR ランタイムの追従を保証する仕様ではない（上記は実測結果）。**再生（画像回帰）にはフラグを付けない**。再生条件を変えると既報告の VP3 / VP3C の 0 px 結果との比較可能性が失われるため、再生 run では従来どおり非追従の run を除外する。製品側の Run In Background（0）と Input System 設定は変更していない
- **再生の回転忠実度（多軸姿勢で約 1.1°）**: VRS 再生は頭部**位置**を厳密に再現する（実測 0.00000 m）一方、pitch と yaw を同時に含む姿勢では**回転**が録画値から系統的にずれる。2026-09-16 の実測: 録画 P3 `q=(-0.20392,-0.26213,-0.05677,0.94153)` に対し再生は `q=(-0.19481,-0.26266,-0.05424,0.94346)`、差 **1.108°**。この値は 3 本の再生 run（VP3 / VP3C）で**ビット単位まで同一**で、位置が一致する 290〜6,134 フレームの全区間で一定。ほぼ無回転の P1 は 0.000°、yaw のみの P2 は 0.023° で一致するので、ずれるのは多軸姿勢だけ。現行の照合許容差 0.5°（`targets.json` の `angleToleranceDeg`）では P3 が永久に一致せず、ハーネスは 90 秒の timeout 後に `not captured: timeout` とする。**方針（2026-09-16 決定）: 照合許容差 0.5° は維持し、標準 fixture の動作計画から pitch を外す（移動と yaw のみ）**。再現性があっても原因が特定できていない以上、契約側を緩めない。pitch を含む録画（`generation\along-20260916-080644\attempt-1`）は診断資料として保持し、回転差は別件として追跡する
- **`quit_when_complete` は false にする**: true にすると再生完了時に Simulator が Player を終了させるため、ハーネスが `Finish()` で `result.json` を書く前にプロセスが消え、撮影ファイルだけ残って run が無効になる。さらに終了直前でハングして `RunTimeoutSeconds`（既定 300 秒）を空費する。false ならハーネスが撮影後に自力で終了する（2026-09-16、4 run すべて `player exited 0` を確認）
- **再生開始時刻のずれ**: 再生の開始検出（Simulator ログの `Opened '<file>.vrs'`）と実際の頭部の動きが run によって 1 秒以上ずれることがある。そのため撮影地点は時刻ではなく姿勢の順序で決め、時刻のずれは記録だけする。`-DelayStartMs` を既定の 20000 から 12000 に下げた run では、視点を固定するより先に再生が始まり `replay_opened_before_ready` で run ごと除外された（2026-09-16）。既定値は下げない
- **ウィンドウ**: Simulator ウィンドウ（frontend）の起動は止められない。`use_batch_mode` を設定しても起動する。閉じるとランタイムが再起動し、再生が壊れる。`-HideSimulatorWindow` は非表示にするだけで、完全なヘッドレスではない
- **前面化**: 録画生成はウィンドウの前面化とクリックに依存する。別のウィンドウやダイアログが前面を奪うと失敗する（失敗理由に前面を占有したプロセスを記録する）
- **Simulator の範囲**: Simulator は OpenXR API レベルの模擬で、Quest の描画や性能は再現しない。Quest Link での最終確認は残る。GPU 時間の比較には使わない
- **表示メッシュ**: `Assets/Licensed/DisplayMeshes` のメッシュは巻き順と法線が反転している（取り込み経路の別件）。画像は Unity Mesh と VP で同じように見える
- **非公開 gRPC**: v205 以外では録画生成は動かない。Meta の更新で使えなくなる可能性がある

## 終了コード

| スクリプト | 0 | 1 | 2 |
| --- | --- | --- | --- |
| `Build-XrSimHarnessPlayer.ps1` | ビルド成功 | ビルド失敗 | 基盤エラー（状態不一致を含む） |
| `New-XrSimFixture.ps1` | 候補が品質検査に合格 | 全試行が不良 | 基盤エラー（設定復元・レジストリ・git の不一致を含む） |
| `Test-XrSimFixture.ps1` | 検証済み（指定時は昇格済み） | 検証不成立 | 基盤エラー |
| `Invoke-XrSimReplay.ps1` | 全 run が終了（有効性は呼び出し側で判定） | ― | 基盤エラー |
