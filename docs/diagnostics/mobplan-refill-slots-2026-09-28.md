# MobPlan 補充の準備済み枠と切断入力ゲートの共有：実装記録（2026-09-28）

MobPlanCity の補充で、Main が 1 フレームに 200 ms 前後止まっていた。その対応の実装記録。計測の記録は `C:\log\zantetsuken-vr\MobPlanSlash\`（RESULTS-v2.md、RESULTS-v3.md）。

## 1. 準備済みの固定枠（MobPlanCrowd、SandboxNpcCharacter）

- **枠の数**：表示中の 20 体に、予備（`spareSlots`、既定 10）を加える。起動引数 `-mobPlanSpareSlots N` で変えられる。
- **準備はシナリオ開始前にすべて済ませる**：予備の複製（template の Instantiate）、JSON の読み込み、必要骨の抽出、切断入力の準備（`TryPrepareCharacterCut`）。
  - 計画のロードと並行して進め、両方が終わってから最初の 20 体を枠から起動する。
- **枠のあいだで共有するもの**（`SandboxNpcCharacter.SlotShare`）：
  - JSON の解析結果（intake、hull）。解析は最初の 1 枠だけ。
  - 固定スケールのメッシュのキャッシュ（`VpFixedScaleSkinCache`）。メッシュは 1 つで、最後の枠と一緒に破棄する。
- **枠ごとに持つもの**：レンダラー、骨、Pose の再生、hull の bank（再準備用に保持）、LOD の登録内容、切断の handle。
- **休眠状態**：描画しない、Pose を更新しない、motion body を無効にする。Hit 検出器・LOD・計画には登録しない。
- **起動**（`Activate`）の順序：
  1. 計画上の位置に置く（MobPlanCrowd の `ApplyRoot`）。
  2. motion body を有効にし、現在の Pose を適用する。
  3. 表示、LOD、Hit 検出器の順に登録する（LOD を Hit 検出器に渡すため、LOD の登録が先になる。同じ関数の中なので、その間に Hit の評価は入らない）。
  4. motion body を有効にした状態で、計画から継承速度を求め直す（`SetPlannedMotion`）。移動の差分から速度を取ることはしない。
  5. 計画に登録する。
- **補充**：空き枠を 1 つ取って起動する。空きがなければ、その新個体はその回の公開に加えない。次の cycle の baseline から外れるので、計画側（`PrepareExternal`）が落とす。補充の位置と視野外の条件は、計画側の既存の規則のまま。その場で生成したり、完了を同期で待ったりはしない。

## 2. 返却と再準備

- **返却できる条件**（`SandboxNpcCharacter.IsReturnReady`）：次の 3 つがすべて成り立つこと。
  1. 切断に使った handle が破棄済み。Held や処理中なら破棄されないので、入力は書き換えられない。
  2. 個体が退役済み（切断の公開で部品が止まっている）。
  3. その切断の操作が公開済みで、形状が Commit 済み（ledger と Geometry の段階）。
- **流れ**：
  - 退役（`RetireWithdrawn`）で、LOD の登録を外し、返却待ちに入れる。
  - 条件が揃ったものを、1 フレームに 1 体だけ再準備する（`TryReprepare`）。
  - 再準備でやること：古い handle を Hit 検出器から外す、cold の準備を返す、保持している bank から新しい handle を作る、部品の退役を確認し直す。その後は休眠状態に戻る。
  - cold の準備が終わったら、空き枠に戻す。
- **新しい個体として扱う**：handle が新しいので、最初の Hit で新しい fragment（系譜の根）になる。古い計画 id、系譜、Hit の消費は引き継がない。計画 id も、計画側が新しく出したもの。
- **補充の瞬間にしないこと**：生成、JSON の読み込み、全準備（`FullPreparations` は開始後に増えない）。再準備は退役の後、別のフレームで行う。

## 2a. DirectSkin 入力の所有を枠の寿命へ（v4）

- **所有者**：枠（`SandboxNpcCharacter`）。
  - 最初の準備で 1 つだけ作る（`DirectCreations`）。
  - 切断の handle を作るたびに、その handle に貸す（`TryPrepareCharacterCut(..., lentDirect, ...)`）。
  - 枠の破棄（OnDestroy）で、handle を破棄した後に 1 回だけ解放する。
- **貸出**（`VpDirectSkinInput.TryLend`／`Return`／`Borrower`）：
  - 一度に借りられるのは 1 つの handle だけ。
  - 貸すときと返すときに、取得済みの Pose を捨てる。次の命中では必ず現在の Pose を取得する。
  - handle は、終わるときに入力を返すが、解放はしない。
  - 貸出中に `Dispose` すると `InvalidOperationException` になる（早期の解放を防ぐ）。
- **Held の要求**：handle が終わらないので、入力は借りられたままで、命中時の Pose を持ち続ける。枠の返却条件（handle が破棄済み）も成り立たないので、その枠は再利用されない。返却条件は緩めていない。
- **切断後の geometry**：入力を借り続けない。
  - 保存先（`TryAppendDirectSkin`）は、同期で書き込み、範囲だけを持つ。
  - 表示スロットは、表示が始まった時点（`Take`）で producer への参照を捨てる。
  - `VpDirectSkinOutput` は、切断を要求する呼び出しの中だけで使われる。
- **作り直す条件**：renderer、その `sharedMesh`、topology 配列（参照）、topology 数のどれかが、作ったときと違う場合だけ作り直す（既存の cold 契約）。汎用のキャッシュや、毎回のハッシュ走査はしていない。
  - 旧入力が貸出中なら、作り直さない。新しい切断の準備もせず、不成立（`Failure`）にする。旧入力は捨てず、所有したまま残す。
  - 枠の返却条件にも、「入力が返されていること」を明示して加えた（条件を強める方向の変更）。
- **所有者が返却より先に終わるとき**（`VpDirectSkinInput.DisposeWhenReturned`）：
  - 貸出中でなければ、その場で解放する。
  - 貸出中なら解放の要求だけを残し、借り手が返した時点（`Return`）で一度だけ解放する。
  - handle の終了が busy で延期されている場合も、延期された終了で返されたときに解放される。
  - 枠の OnDestroy はこれを使う。警告を出して参照を捨てることはしない。汎用の参照カウントは加えていない。
- **枠を使わない経路**（handle が自分で作る）は、従来どおり、handle が作って handle が解放する。

## 2b. 退役の通知（`MobPlanCrowd.ActorRetired`）

- **通知の時点**：crowd が withdrawal した個体を計画から外すとき（`RetireWithdrawn`）、`ActorRetired` は計画から外す**直前**に通知する。
  - その後の、計画からの除去、LOD からの退出、枠の返却（`pool.Return`）は、この順のまま同じ Frame に行う。
  - 購読者は観測（計測の check）だけで、製品の状態は変えない。
- **理由**：crowd は、命中した個体を、命中と同じ Frame の自分の Update の中で外すことがある（x18・x19 で各 1 回、2026-09-28）。
  - 通知を除去の後に置いていたときは、観測の側がその時点の計画の評価を読めず、系譜の根の名前の記録も漏れた。
  - これが、統合確認の check の失敗 2 件（姿勢の一致、枠の再利用の順序）の原因だった。
  - 除去の前に通知すれば、観測の側は、その個体の同じ時刻の計画の評価と根を記録できる。
- 製品の受付・公開・Commit・枠の再利用の順序と、命中時の姿勢の取得には誤りがなかった。DirectSkin の返却条件と、命中のたびに現在の姿勢を取り直す条件は、変えていない。

## 3. 切断入力ゲートの共有（`VpCutInputGate`、`VpCutInputConnectivity`）

- 検査の契約（DESIGN 6.2）は変えていない。
- 辺と扇の検査は、三角形の index、topology 配列、topology 数だけで決まる。この部分だけ、World が検査済みのもの（`CutWorldRoot.CutInputConnectivity`）を持つ。
- 再利用してよいのは、今回の 3 つが保持しているコピーと、要素単位ですべて一致したときだけ。Mesh・asset・ハッシュでは判断しない。検査に落ちた入力は保持しない。保持は最大 8 件。World の Release で捨てる。
- 個体ごとの検査は毎回行う：参照と範囲、三角形の topology 頂点、属性の有限性、位置の整合。
- 保存先への追加時のゲート（`VpCpuGeometryStorage`）は、従来どおり全部を検査する。
- これは実装の詳細として、ここに記録する。DESIGN には固定しない（TL 判断）。

## 4. 観察用に加えたもの

- `MobPlanCrowd`：`SlotCount`、`FreeSlots`、`ReturningSlots`、`PreparingSlots`、`BrokenSlots`、`WaitedForSlot`、`ReusedActivations`、準備の時間とメモリ（`PoolPrepareSeconds`、`PoolAllocatedBytes`、`PoolMonoBytes`）。
- `SandboxNpcCharacter`：`IsPrepared`、`Activations`、`Reprepared`、`FullPreparations`、`LeaveLevelOfDetail()`。
- `SlashHitDetector`：`HasCharacter`、`CharacterCount`。
- `PoseLodDirector`：`CharacterCount`。
- marker：`Zantetsu.MobPlan.*`、`Zantetsu.Npc.*`、`Zantetsu.CharacterCut.Prepare.*`、`Zantetsu.DirectSkin.Create.*`。

## 5. 試験と残っていること

- **試験**
  - EditMode `MobPlanSlotPoolTests`（偽の枠）：空き枠は準備後にだけ、枠の順に出る。空きがなければ取り出さず、生成もしない。返却条件が成立するまで再準備しない。再準備は 1 回に 1 体。失敗した枠は外す。
  - PlayMode（`PreparedCharacterLentDirectPlayModeTests`）の 3 件：
    - 同じ入力を 2 つの handle が順に借りても、作成は 1 回。2 回目の切断は、自分の命中時の Pose（z 7）でスキニングされる（前回の z 5 ではない）。
    - Held の間は入力を貸し出したまま（Pose も保持）。他へ貸せず、解放もできない。Held が終わると戻る。
    - World が終わっても、入力は早く解放されず、二重にも解放されない。
    - 所有者が、準備済みの handle の貸出中に終わる：その場では解放されず、handle が終わって返したときに一度だけ解放される。
    - 所有者が切断の最中（退役の OnDisable の中、handle は busy で終了が延期）に終わる：切断は入力を使って公開まで進み、延期された終了で返されたときに一度だけ解放される。
- **費用**（v207 の XR、Main 中央値）：
  - 補充フレーム：約 4.5 ms
  - 再準備フレーム：約 6.2 ms（最大 12 ms）。再準備そのものは約 1.4 ms で、大半は物理入力の作成（凸包メッシュの焼き込み、約 1.0 ms）。
  - 再準備フレームの Main は改善したが、11.1 ms 超過は残る。これは Main の稼働時間の値で、XR のフレーム全体が間に合うことを示すものではない。
- **実行中に通っていない経路**：空き枠がなくて待つ経路と、Held・Pending の個体。どちらも上の決定的な試験で確かめた。
