# 表示容量の拡張 実装記録（2026-09-28）

commit／push は行っていない。

## 不具合

World 全体の論理ブランチ容量（`CutWorldProfile.branchCapacity`、既定 128）を超えると、次のことが起きた。

- 多重切断の Snapshot が `CapacityExceeded` を返し、`VpLogicalCutDisplay.RefuseForRoom` が、それを記録も終了もせずに見送った。
- その結果、最後に採用した Snapshot が描かれ続けた。新しい切断は公開・Commit まで進むのに、描画一覧に登録されなかった。
- 観察：
  - 実機 Link（r140731）では、48 件目以降の切断。
  - XR Simulator v207（x12、200 秒）では、42 件目以降の切断。
- 原因は枠の再利用でも DirectSkin の再利用でもない。

## 方針（TL 判断）

- 表示一覧・Snapshot・Stencil 準備と、その関連作業領域は、明示した初期容量から設定上限まで拡張する。
- 切り替えるのは、完全な新 Snapshot だけ。
- 上限超過・確保不能は、共通 Player 終了とする。
- `MaxStencilColors` は拡張しない。
- 片の寿命管理（DESIGN §7.10）で表示の正しさを担保する方法は採らない。
- DESIGN §5.6 と D-186 ⑤ に反映した。

## 実装

### 上限の型と Profile
- `VpLogicalCutDisplayLimits`：commands／instances／branches／candidates の上限。`default` は固定（初期容量＝上限）で、従来と同じ。
- `CutWorldProfile`：既存の容量は初期容量として残し、`*CapacityLimit` を新設した。
  - 既定の上限は 2048／4096／1024／4096。Geometry 参照と表示インスタンス参照は 4096。
  - PC 向け Playable の暫定設定。長時間プレイの容量保証ではない。
  - 上限は初期容量以上であること（`IsUsable` で確認する）。

### 不足の種類
- `VpMultiCutSnapshot.Shortage`：Branches（走査の stack を含む）／Candidates／ChainDepth／RenderFragments／Caps。
- chain depth は系譜の上限なので、拡張しない。

### 拡張の手順（`VpLogicalCutDisplay`）
- 収集の冒頭で、候補側（構築中の Snapshot、候補の配列、Stencil の作業領域、Cap 法線の CPU 作業領域、Cap-job 分類）を、その時点の容量に合わせる。
  - 採用中の Snapshot と配列には触れない。
  - 採用で入れ替わって小さくなった側は、次の収集で作り直す。
- Snapshot が不足した場合：その種類を拡張して作り直す。
  - RenderFragments と Caps の不足は、インスタンスを拡張して解消する。
  - 1 回の拡張で、少なくとも 2 倍（または上限まで）にする。
  - 作り直しの回数は、4 種類それぞれの「現在の容量から上限までの段数」の合計を超えない。翌フレームへは持ち越さない。
- Snapshot ができた後、描画コマンドとインスタンスが不足していれば、必要量まで直接拡張する（少なくとも 2 倍）。

### GPU 資源
- 本体の batch と Cap 法線：新しい Buffer を作り、候補の全体を書き込み、採用の時点で切り替える。
- カメラの Stencil batch：その Frame の準備より前に作り直す（準備は毎 Frame、書き直すため）。
- 置き換えた Buffer：各 batch の `RetirementFence` を AsyncGPUReadback で読み、完了したら解放する。
  - 通常更新では GPU を待たない。待つのは Dispose だけ。
  - readback が失敗した Buffer は、Dispose まで保持する。

### Geometry 参照表（`VpGeometryReferenceTable`）
- slot 配列を上限まで伸長する。token（slot と世代）は、そのまま有効。
- `HasRoomForGeometriesWithDisplayInstances` は、上限までの伸長余地も空きとして数える。

### 拡張できない場合
- 対象：
  - 上限超過（Snapshot、コマンド、インスタンス、表示インスタンス、Stencil）
  - 確保不能
  - 登録時の上限超過（TryShow、TryCommitCut）
- 記録：種類・要求量・保持量・上限・結果を 1 行、1 回だけ `RoomFailureHandler` に渡す。
- 表示は停止し（`RoomNotEstablished`）、その Frame は開かない。
- `CutWorldRoot` は `RoomFailureHandler` を `RequestTermination` につなぐ。
- Handler がない場合（ライブラリ単体）は、従来どおり拒否し、`LastRoomFailure` に残す。
- 拡張が成功したときは、1 回につき 1 行ログを出す。

## 試験

- EditMode：`VpLogicalCutDisplayRoomGrowthTests.cs`（`VpLogicalCutDisplayMultiCutTests` の partial、5 件）
  - 小容量を超えたとき、既存の片と新しい片が同じ新 Snapshot に入ること。置き換えた GPU 資源が解放されること。
  - 初期容量すべて 1 から、1 回の収集で 3 種類を合わせて 18 回拡張しても、上限内なら成立すること。
  - 拡張後も、動く片の配置が更新されること。
  - 上限超過・確保失敗で通知が 1 回、表示が停止し、Dispose で参照がすべて戻ること。Handler がない場合は従来の拒否になること。
  - 登録時の上限超過も通知されること。
- PlayMode：`CutWorldRootPlayModeTests.DisplayRoom.cs`
  - World の更新ループの中で拡張し、すべての片が描画一覧に登録されること。
  - 上限を超えると共通終了が 1 回起きること（fixture の都合で単独実行）。
- 結果：
  - 表示関連の EditMode 19 クラス：310／310。
  - PlayMode：拡張 2／2、終了 1／1。
  - 1 回目の EditMode で、既存の readback 確認試験が 1 件失敗した。再現せず、原因は未特定。
- XR Simulator v207、200 秒（x13）：
  - 切断 59 件すべてで、子が描画一覧に登録された。
  - 45 件目でブランチ容量が 128 から 256 に拡張された（1 回）。
  - 境界の前後で、両眼の代表画像を確認した。

## 単独確認（開始時 HEAD `2319df75` に本件だけを入れた worktree）

- 場所：`zantetsuken-vr-roomslice`。入れたものは次だけ。
  - 表示容量のパッチ（共有ファイルは本件の hunk だけ）
  - 新しい試験と .meta
  - この記録
- コンパイル：エラー 0。
- 結果：
  - EditMode の新規 5 件：5／5。
  - 上限による終了（単独のプロセス）：1／1。
  - World での容量拡張の試験と、既存の GPU 拡張の試験（同じプロセスで、この順）：取り込み直後の初回に、既存の GPU 拡張の試験が 1 回失敗した（「every cut was accepted」で 16 件を期待、実際は 14 件）。
  - 同じ試験をその後、単独で 2 回、同じ順で 2 回流し、4 回とも通過した。
- 失敗の切り分け（原因は未確定）：
  - 依存不足ではない：コンパイルは通り、同じ試験がその後 4 回通っている。
  - 本件の不具合とも示せていない：
    - この試験は既定の容量のままで、表示の拡張は起きない。
    - その実行のログに、表示の拒否・停止・終了・Abort はない。
  - 候補の仕組み：Final の引き渡しが同じ Frame で済んだ transaction は `Driver.Transactions` から外れる。この試験は、切断を頼んだ 1 Frame 後にその数を読むので、観察の競合で 14 件になりうる。2026-09-22 の CutWorldRoot の断続的な失敗と同じ種類。確認はしていない。
- s2 の失敗の記録：原因は未確定。コード上、観察のタイミングに依存する不備を確認した。
  - 記録：`C:\log\zantetsuken-vr\MobPlanSlash\commit-prep-room\s2-playmode-growth\`
- 試験の補正（別のコミット、本件より前）：
  - 受け付けた数を、ledger の受付記録（`TryGetOperationAtAdmission`）から、各切断元の Operation ID として取るようにした。対象は親の 16 件と子の 1 件。
  - 受付数・全件 Commit・GPU と CPU の一致の assertion は、そのまま残した。
  - 本件の World 拡張の試験も、同じ読み方にした。
  - 補正後、単独 worktree での結果：該当試験の単独 1／1、s2 と同じ 2 件・同じ順序 2／2。
- x13（v207、200 秒）は、先行する未コミット変更（MobPlan の補充、DirectSkin の再利用など）を含む、統合した Player での確認である。本件単独の Player では確認していない。

## 未完・残件

- 修正後の Link（実機）での境界越え：未検証。Playable の受入の残件。次に操作するときに、最終版の Player で確認する。
- 片の寿命管理（DESIGN §7.10）。
- Hit 登録数が 19 で、生存数 20 と合わない件。
- 終了時の NativeArray の 510 バイトの差。
