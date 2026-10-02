# check 撮影基盤の一本化（設計記録、2026-10-02、実装前に記録）

> Cleanup update (2026-10-02): The unused Phase 0/0.1 PNG/JSON capture path and all Phase 0.11 NVENC code, native plugin, tests, and build entry points have been removed. The NVENC and PNG/JSON sections below record the pre-cleanup investigation. The current check/movie capture uses JPEG and retains only the shared GPU readback components.

対象は check と画像 run の撮影だけ。製品の挙動・設定、既定無効、VB／IB 各 256 MiB は変えない。既存 API と内部構造の互換性は保たない。

## 1. 既存資産の経路と、確認済みの範囲

### 1.1 Phase 0.11 NVENC（`Runtime/Observability/Nvenc*`、`Native/ZantetsuNvenc`、`Plugins/x86_64/ZantetsuNvenc.dll`）
- **入口**：本番の composition root は無い。組み立てているのは Standalone 試験（`Tests/Standalone/NvencNativeGraphicsObservationSentinelTests.cs` の SentinelRun）だけ。
- **取得**：Main thread で、撮影用 RenderTexture を 8 枠の work slot（1280×720 sRGB 固定、`NvencBringUpProfileV1`）に GPU 変換する。Main thread での資源の受け渡しは、`NvencMainThreadResourceTeardown` を経由する。
- **エンコード**：native の NVENC session。D3D11 のみで、共有 context に `ID3D11Multithread` の保護をかける。
  - H.264 の全フレーム IDR（GOP 1）、QP 28、目標 30 fps、1 Run あたり 120 tick。
  - 待ち行列はすべて 8 枠固定：submit worker、output worker、submit→output、frame completion。
- **保存**：Annex-B chunk を file session に追記し、終了処理で chunk を確定する。chunk の上限は 256 MiB、1 access unit の上限は 16 MiB。
- **終了**：
  - deadline の定数（提出窓 4000 ms、確定 30000 ms、回復 10000 ms）は定義されているが、経路で使われていない。
  - native の出力コピーは `WaitForSingleObject(..., INFINITE)` で待つ（`NvencEncoderSession.cpp:1958`）。
- **確認済みの範囲**：
  - 2026-09-14、RTX 3090 で Standalone 試験 10／10 合格、9 frame を順に復号した（`Logs/UnityTests/20260914-092657-03c0fa/StandaloneResults.xml`）。
  - 実ゲームの描画との並走、`ID3D11Multithread` 保護の描画コスト、120 tick を超える連続撮影、frame ごとの実時刻の記録は、いずれも未確認。
  - Editor と PlayMode では動かない（Standalone Win64 のみ）。
- **本日の再確認**：`Tools/Run-NvencStandaloneTests.ps1` は exit 2。NVENC の判定ではなく、player build の段階で失敗した。
  - 原因：固定の player 出力先（`%LOCALAPPDATA%\Zantetsu\TestPlayers\WindowsStandalone64`）に、2026-09-14 の Mono build が残っている。この project は IL2CPP のため、Unity が build を拒否した。
  - 副作用：player build が、追跡対象のファイル 2 つを書き換えた。どちらも明示パッチで HEAD の値に戻した。
    - `Assets/Settings/PC_RPAsset.asset`：URP prefilter 3 行（`C:\log\...\capture-unify\nvenc-run-side-effect-PC_RPAsset.diff`）
    - `ProjectSettings/ProjectSettings.asset`：`preloadedAssets` に 3 件（`nvenc-run-side-effect-ProjectSettings.diff`）。こちらは試験後の確認まで気づかなかった。
  - 出力先を変えての再実行はしていない。

### 1.2 Phase 0／0.1 CPU 経路（`Runtime/Observability`）
- **読み戻しの部品**：
  - `CaptureFrameReadbackBufferPool`：固定 slot の NativeArray（Persistent）。Main thread 専用。
  - `UnityRenderTextureReadbackDispatcher`：`AsyncGPUReadback.RequestIntoNativeArray` で slot に読み戻す。`TryStart`／`TryCollect`／`GetBuffer`／`Release` を poll で回し、割り当ては無い。
  - 確認済みの範囲：EditMode 試験（BufferPool、PixelLayout、PngEncoder、BackendAbstraction）。
- **保存側**：`PngJsonCaptureEvidence*` が worker thread で PNG と JSON を書く。
  - 製品の呼び出し元は無い。試験経路のみ確認済み。

### 1.3 今回の撮影コードと重複
| 実装 | 内容 | 問題 |
|---|---|---|
| `Sandbox/CheckCaptureCamera`（pk4） | 画角・切替・描画に加え、`AsyncGPUReadback.Request` の callback、frame ごとの `ToArray()`、専用 thread の JPEG | `Dispose` が `WaitAllRequests` と `Join(30 s)` で Main を止める。frame ごとに 3.7 MB を割り当てる。省略した frame の時刻が残らない。 |
| `Sandbox/BuildingSlashE2E` の movie | 読み戻しの callback、`Task.Run` の JPEG | 件数の上限が無い（共有 pool に積み上がる）。`OnDestroy` が Sleep で最大 10 s 待つ。実時刻を残さない。読み戻しの frame が 1 つずれる（`frameCount − 1`）。 |
| `XrSimCityEyeBurst`（Licensed、Git 外） | 目の映像と、同期 PNG の連写 | 今回の範囲外。`-xrsimEyeNoBursts` で止めたまま。 |
| check の `Picture`（3 枚の節目 PNG） | 同期の `ReadPixels` | 撮影基盤ではなく判定の証拠。残す。 |

## 2. 採用構成

```
CheckCaptureCamera（画角・切替・描画）
  └─ endCameraRendering で Offer(RenderTexture, frame, 実時刻, shot)
CheckFrameSink（保存側。CPU JPEG）
  ├─ 受付：保存中か、上限件数、状態（Running のみ受ける）
  ├─ 読み戻し：UnityRenderTextureReadbackDispatcher と CaptureFrameReadbackBufferPool（Phase 0/0.1 を流用）
  │     読み戻し中は最大 2。満杯なら「省略：読み戻し」
  ├─ エンコード・書き込み：専用 thread 1 本。待ちは最大 2、実行中は 1
  │     満杯なら「省略：エンコード」。slot は書き込みが終わるまで返さない
  ├─ CheckFrameLedger（共通の会計）：要求ごとに 1 行（frame, 実時刻, shot, 結果, 段階の時刻）
  │     要求・受付・保存・省略（理由別）・エラー・取消、待ち列の最大
  └─ 終了：BeginFinish／Cancel／エラー → 期限つきで回収。Main は Join も Sleep もしない
```

- **カメラ側**：既存の撮影 shot（世界固定、切替は呼び出し側）と、頭の追従を担う。
  - 担当：撮影用 RenderTexture を作り、camera drawing と display に登録する。
  - 保存のコードは持たない。
  - `Dispose` はカメラの登録を外して破棄するが、RenderTexture は sink に渡す（`RetireSource`）。sink は読み戻しが残っていないことを確かめてから解放する。
- **World 更新・snapshot**：二重に実行しない。撮影カメラは display のカメラ一覧に加わるだけ（pk4 と同じ）。ゲームのカメラ・入力・LOD・寿命の参照は変えない（`GameViewGuard` で毎 frame 確かめる）。
- **Main thread の仕事**（毎 frame、`Application.onBeforeRender` で sink 自身が回す）：
  - 完了した読み戻しの回収
  - エンコード待ちへの投入、または省略
  - 書き込み済み slot の返却
  - 期限の判定
  - 割り当ては無い：行の表は上限件数で事前確保し、パスは worker が作る。
- **worker**：
  - slot の NativeArray を読み、JPEG に変換してファイルを書く。
  - 書き込み先は `ICheckFrameWriter`。試験では遅い出力先と失敗する出力先に差し替える。
  - 書き込みは取消を受け付ける。
- **時刻**：
  - frame ごとの実時刻は `Time.realtimeSinceStartupAsDouble`。その camera の描画が終わった時点で取る。
  - 省略した要求も行として残す（`frames.csv`：`frame,real,shot,outcome`）。
  - 動画は保存した行の実時刻差を各画像の表示時間にする。省略した区間は前の画像が保持し、詰めない。
- **終了・取消・エラー**：
  - `BeginFinish()`：受付を止める。読み戻しの残りとエンコードの待ちを書き切る。
  - `Cancel(why)`：待ちを捨てて「取消」と数える。実行中の書き込みには取消を通知する。
  - エラー：読み戻しのエラー、または書き込みの例外。最初のエラーで状態を Failed にし、Cancel と同じ回収をする。
  - 期限（既定 10 s）：過ぎたら DeadlineExceeded を記録して残りを取り消す。その後も回収は続け、最後に残った資源（読み戻し中・thread・slot）を記録する。
  - Main thread は待たない。
  - 回収が済むと、`frames.csv` と `switches.txt`（カメラ側）を書き、dispatcher、pool、RenderTexture を解放する。
- **無効時**：`-zantetsuCaptureCamera` が無ければ、sink もカメラも thread も作らない。生存数を静的な計数で確かめる。
- **check の終わり**：
  - 通常の終わり：`9-end` の前に、生きている sink を `BeginFinish` し、終端状態になるまで frame を回して待つ。sink 自身の期限で必ず終わる。
  - 早い終わり（`Finish(code)`）：`Cancel` だけをかけ、待たない。
- **BuildingSlashE2E の movie**：独自の読み戻し・Task・Sleep を撤去し、同じ sink に置き換える。カメラと引数は残す。
  - frame は、その camera の描画が終わった frame と実時刻で記録する。

## 3. NVENC と CPU JPEG の比較、採用理由
| 観点 | NVENC（Phase 0.11） | CPU JPEG（採用） |
|---|---|---|
| 動作環境 | Standalone D3D11 のみ。PlayMode／Editor では試験できない | Editor、PlayMode、Player のいずれでも動く |
| 連続撮影 | 1 Run 120 tick、chunk 256 MiB。4000 frame の撮影には Run の継ぎ足しが要り、未実装 | 件数の上限は引数のみ |
| 時刻 | frame ごとの実時刻の sidecar が無い。30 fps 固定の H.264 時間軸 | frame ごとの実時刻を行で持つ |
| 終了 | deadline の定数は未使用、native が INFINITE で待つ | 期限つきで、Main は待たない（今回作る） |
| ゲームへの影響 | 共有 D3D11 context の Multithread 保護のコストが未測定 | 読み戻し 2、エンコード待ち 2 の範囲。pk4 で 4000 frame 省略 0（群衆は合格。JPEG と planner の競合は未検証のまま） |
| 画質・容量 | H.264 QP 28 の全 IDR | JPEG q85（1280×720 で 1 枚あたり約 100〜200 KB） |
| 本日の接続確認 | build の段階で失敗（§1.1）。接続可否は未再確認 | — |

- **採用**：CPU JPEG。理由：
  - 今回必要な、PlayMode での試験、実時刻、長時間、期限つきの終了をすべて満たすのは CPU JPEG だけ。
  - NVENC は Phase 0.11 の資産として残すが、check には接続しない。
- **暗黙の fallback は設けない**：方式は引数と構成で一つに決まる。NVENC が使えないときに JPEG へ切り替える経路は作らない。
- **NVENC を接続する前提**（今回は作らない）：
  1. 固定の player 出力先の整理と、Standalone 試験の再合格
  2. Run の継ぎ足し（120 tick を超える撮影）と、frame ごとの実時刻の sidecar
  3. deadline 定数の実装と、INFINITE 待ちの撤去
  4. 共有 context の保護が描画に与えるコストの計測
  5. `CheckFrameLedger` で受付・計数・終了の会計を共通にする（RenderTexture を GPU のまま渡す backend として）

## 4. 残す・置き換える・撤去する
- **残す**：
  - Phase 0.11 NVENC 一式（未接続のまま）
  - Phase 0/0.1 の `UnityRenderTextureReadbackDispatcher`、`CaptureFrameReadbackBufferPool`（流用）
  - `PngJsonCaptureEvidence*`（呼び出し元なし。触らない）
  - check の節目 PNG
  - `GameViewGuard`
- **置き換える**：
  - `CheckCaptureCamera` の保存部分 → `CheckFrameSink`
  - `BuildingSlashE2E` の movie 保存 → `CheckFrameSink`
- **撤去する**：
  - `CheckCaptureCamera` の `AsyncGPUReadback.Request` callback、`BlockingCollection`、専用 thread、`WaitAllRequests`、`Join`、frame ごとの `ToArray()`
  - `BuildingSlashE2E` の `Task.Run`、`ConcurrentBag` の byte 配列、`OnDestroy` の Sleep 待ち

## 5. 試験（PlayMode）
1. **無効時**：sink、カメラ、thread のいずれも作られない（静的な生存数 0、カメラ数が不変）。
2. **ゲーム側の参照・判定**：撮影カメラを毎 sample 動かしても、固定入力に対する katana の判定が一致する。`Camera.main`、寿命のカメラ、目の平面、ゲームカメラの親・tag・stereo・target・姿勢も変わらない（pk4 の試験を新構成で維持）。
3. **遅い出力先・満杯**：書き込みを 1 枚 150 ms に遅らせる。
   - Main の `Offer`＋`Pump` の最大時間が小さいこと。
   - 省略が理由別に数えられ、すべての要求が終端の結果を持つこと。
   - 保存行の実時刻が描画時の実時刻と一致し、省略区間が残ること。
4. **回収**：正常終了、処理中の取消、書き込みエラーのそれぞれで、
   - 期限内に終端状態に至る
   - 生存 thread 0、読み戻し中 0、slot 返却済み、RenderTexture 解放
   - Main は待たない
   - 期限超過（取消を無視する出力先）でも、超過を記録して Main は待たない
5. **保存結果を開く**：
   - 既知の画（上半分にだけ物体、背景は純赤と中間灰）を撮る。保存 JPEG を独立に開き、次を確かめる。
     - 上下の向き
     - チャンネルの順（赤が赤）
     - 中間灰の値（sRGB のまま）
   - `frames.csv` の実時刻から VFR の動画を作り、その長さ＝最初から最後の保存 frame までの実時間＋最後の 1 枚、であることを確かめる（再生速度）。
