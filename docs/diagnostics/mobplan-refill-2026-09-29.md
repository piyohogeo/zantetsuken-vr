# MobPlan の補充・再準備の spike 対策（2026-09-29）

push は保留。対象は MobPlanModels（49 モデル、49 枠）の群衆。

**結論**（2026-09-29、TL 採用）
- 補充・再準備の spike を抑えた。補充関連 Main のフレーム合計の最大は次のとおり。
  - 通常入力：約 21.5 ms → 約 1 ms。
  - 6 体同時の退場（負荷確認）：約 2.2 ms。
- 同時に退場した枠の待ちは解消した。終了時の戻り待ち・準備中・開始待ち・壊れた枠はすべて 0。
- S4（表示開始の追加分割）は行わない。LOD 計画の事前作成で、表示開始の最大は 0.35 ms になった。
- 今回の改善は補充・再準備の spike に限る。ゲーム全体の処理落ちが解消したとは扱わない。Main 全体の p90 と秒単位の停止は未解決（下の「残件」）。

## 構成
- **表示開始の LOD 計画を事前に作る**
  - `PoseLodDirector.Prepare` が `PoseLodPlan` を返し、`Register(plan)` は登録だけを行う。
  - 群衆は Begin で、table bank を設定してから全枠の計画を作る。場所はリプレイ開始の前で、f14 では 49 枠で 474 ms、1 枠あたり最大 17 ms。
  - table bank が計画を作ったときと違う場合は、その場で全部の登録を行う（`lodPlanFallbacks`、f17・f18 とも 0）。
- **焼き込み済み Mesh の再利用**（`VpBakedConvexMeshes`）
  - 枠は、骨ローカルの凸 Mesh を初回の準備で一度だけ焼き、以後の再準備で使い回す。
  - 共有するのは、焼き込み後に変更されない Mesh だけ。姿勢は各入力の B-rep の複製と frame に入る。Mesh には入らない。
  - 寿命は既存の保持数（`PhysicsShapeSource`）で管理する。入力ごとに自分のソース（`Borrowing`）を持つので、入力ごとの数え方と再試行時の条件（保持数 1）は変わらない。最後の保持者（枠・入力・形状・継承した破片）が手放したときに Mesh を破棄する。
  - 焼き込み元と値で一致しない入力には使わない。
- **焼き込み元が一致しないときの焼き直し**
  - 再準備の中では焼かず、準備キューの独立した段階（Bake）で焼く。
  - 通常経路で一致する根拠：焼き込み元の入力（`_bankPoints`・`_bankOffsets`・`_bankIndices`・`_bankFaceEdges`・`_bankEdges`・`_ranges`）は、初回の準備で一度だけ代入される（SandboxNpcCharacter の準備の本体）。以後は準備のたびに Temp の NativeArray へ写すだけで、書き換える箇所はない。
  - 記録する数：不一致（`bakeMismatches`）、段階での焼き込み（`stageBakes`）、再準備の中の焼き込み（`preparationBakes`、spike の抜け道なので 0 のはず）。f17・f18 とも、すべて 0。
  - **Bake 段階への分離は、focused test（EditMode）でだけ確認した。f17・f18 では一度も実行されていない。** 今回の spike の削減は通常の固定モデルの枠によるもので、入力が変わったときの再 Bake の費用は測っていない。
- **群衆で共有する補充キュー**（`MobPlanRefillQueue`）
  - 表示開始の待ち（Start）、戻った枠の再準備（Prepare）、その前の Bake を、1 つのキューで順に処理する。個体ごとのコルーチンはない。
  - 段階を実行するのは、見積もり（直近 5 回の中央値。初期値で埋めて始める）が次の 2 つに収まるときだけ。
    - 補充の上限（1 フレーム 2 ms）の残り。
    - フレームの残り Main から予備（4 ms ＋ 物理の見込み時間）を引いた分。
  - 見積もりだけで上限を超える段階は実行しない。記録はするが、強行はしない。
  - **2 ms は厳密な実測の上限ではない。** 開始前の見積もりで判断し、処理の途中では中断しない方式である。今回の小幅な超過（下記）を理由に、さらに分割する必要はない。
  - 開始前の判断と実測の超過は、別々に記録する。
  - 表示開始の待ちは、計画側の基準にも残す。重複した要求は計画だけを差し替える。計画から消えた個体は取り消す。ワールド終了時はすべて取り消す。
  - 開始時の計画で位置が決まらない場合と、視界内の場合は、枠を取らずにその開始をやめる（枠は準備済みのまま残る）。
- **予算の計算**（二重に引いていないことの確認）
  - フレーム開始の時刻は Initialization の先頭で取る（`CutPhysicsStep.BeginFrame`）。
  - 補充は Update（`MobPlanCrowd`）で動く。物理の実行は PreLateUpdate の LateUpdate の直後。
  - つまり補充の時点の残り予算は、物理の時間をまだ差し引いていない。物理の見込み時間を予備に入れても二重にはならない。
  - 4 ms の予備は、Update より後の処理のための分である（LateUpdate、物理のあとの回収、描画の送信）。

## 計測
- Player：IL2CPP、XR Simulator、moving VRS、`script-v3-165.txt`、`-DelayStartMs 30000`、目の高さ 1.6 m、Piece lifetime threshold 64。
- 最終版は m24。f17（バーストなし）と f18（バーストあり）を各 1 回、どちらも exit 0。準備・終了込みの合計は約 7.2 分。
- **マーカーの集計範囲**
  - 「補充関連 Main のフレーム合計」は、`Zantetsu.MobPlan.Refill`・`Zantetsu.MobPlan.Collect`・`Zantetsu.MobPlan.Pool` の 1 フレームの合計。
  - 3 つは入れ子にならない。Refill と Pool は `MobPlanCrowd.UpdateCrowd` の中で並ぶ別々の範囲。Collect は dispatcher の回収（`SharedWorkFrame` の turn）で呼ばれる。補充の段階は work frame を回さないので、Collect が Refill や Pool の内側に入る経路はない。
  - 子のマーカー（`Refill.Start`、`Npc.Reprepare`、`Npc.Activate`、`MobPlan.Replacement`）は合計に入れていない。
  - キューが記録する「超過」は、各段階の実測（呼び出しの時間）を、その時点の上限の残りと比べたもの。キュー自身の判断（`HasReturnReady`・`NeedsBake` の確認、残り予算の読み取り）、Pool、Collect は含まない。

### 通常入力の前後比較（同じスクリプト）
| 項目（ms） | 変更前 f13（HEAD＋マーカー、m20） | 変更後 f17（m24） |
|---|---|---|
| 補充関連 Main のフレーム合計 最大 | 21.5 | 1.02（GC を含むフレーム）／0.79（GC なし） |
| 表示開始（`Npc.Activate`）最大 | 21.4 | 0.13 |
| 再準備（`Npc.Reprepare`、フレーム合計）最大 | 4.88 | 0.95 |
| キューの見送り（上限／フレーム残量）・上限超過の拒否・実測の超過 | — | 0 ／ 0 ・ 0 ・ 0 |

- 変更前に最も大きかったのは、表示開始のたびに作っていた LOD 計画だった（f13 の内訳：`Activate.Lod` 最大 21.3 ms）。

### 6 体同時の退場（f18、負荷確認）
- **負荷確認用のバーストである。** 検出器へ直接入力している。Gesture からの E2E 確認とは別物（`-zantetsuMobPlanRetireBurst 6 900`）。
  - 900 フレームごとに、Slash が飛んでいないフレームを選ぶ。プレイヤーに最も近い 6 体の腰を、1 本の Slash（1 つの id）で掃く。
  - 失敗したら、以後のバーストを止めて最初の失敗を一度だけ記録する。
  - 8 回すべてで 6 体が切れた（そのうち 1 回は 7 件の hit）。
- f16 は無効な計測として残す。バーストが 1 回の評価に 6 つの Slash id を渡し、検出器の同時上限（`SlashWaveCore.Capacity` = 4）を超えた。さらに、失敗後に毎フレーム再試行していた。
- 補充関連 Main のフレーム合計の最大：2.19 ms（GC を含むフレーム）。GC なしのフレームでは 2.06 ms（frame 7077：再準備 3 件で 1.88 ms、キューの判断が約 0.18 ms）。
- キューの記録
  - 実測の超過：1 回、0.016 ms。
  - 上限による見送り：6 回。フレーム残量による見送り：0 回。上限超過の拒否：0 回。
- 各段階の最大：表示開始 0.35 ms、再準備 0.92 ms、Bake は未実行。
- キューの最大長：開始待ち 7、戻り待ち 7、準備中 6。
- 待ち時間
  - 退場から再準備まで：中央値 15 ms、最大 62 ms。
  - 退場から空き枠になるまで：中央値 56 ms、最大 114 ms。
  - 表示開始の待ち：中央値 0.8 ms、最大 26 ms。
- 待ちの解消
  - 終了時の戻り待ち・準備中・開始待ち・壊れた枠は、すべて 0。
  - 見積もりの終了時の値は、再準備 0.59 ms、表示開始 0.07 ms。
- 再利用の個体
  - 113 個体のうち 64 が再利用の枠だった。
  - 再利用の個体の root 切断が確定したのは 44 件。切断の失敗は 0。
  - World は通常の形で終了し、すべてを返した（check の「the world ended the ordinary way and gave everything back」）。

## 試験
- EditMode 197/197、PlayMode 85/85（`C:\log\zantetsuken-vr\MobPlanModels\tests\r3-*`）。
- 追加した試験
  - `MobPlanRefillQueueTests`：順序、上限、重複の差し替え、取り消し、ワールド終了、枠なし・開始の中止、Bake の段階、上限超過を強行しないこと、判断と実測の超過の別記録。
  - `VpBakedConvexMeshesTests`：焼き込みを繰り返さないこと、同じ Mesh の共有、枠・入力・継承した破片のどの順で手放しても最後の保持者で破棄されること、別の入力を拒否すること。
  - `MobPlanSlotPoolTests`：再準備を 1 回に 1 枠だけ行うこと、Bake の段階。

## 残件（今回の対象外、原因は未特定）
- **計画の stale**：f18 で 129 件（f17 は 9 件）。退場のたびに世代が進むためという説明は推定で、確かめていない。待ちの解消とは別の残件。
- **長い停止**：f17 の frame 10786・10788 で、`FrameEvents.XRBeginFrame`（1.45 s）と `Gfx.WaitForPresentOnGfxThread`（1.38 s）に長い時間を観測した。補充関連のマーカーはそのフレームに出ていない。原因は特定していない。
- **Main 全体の p90**：起動ごとに大きく違う（f13 23.7、f14 15.5、f15 15.7、f17 23.6、f18 27.0 ms）。補充の改善とは別に扱う。
- **入力変更時の再 Bake の費用**：測っていない（上の「焼き込み元が一致しないときの焼き直し」）。

## コミットの対象
- **P（製品と試験）**
  - 変更ファイル 10 本。製品 9 本：PoseLodDirector、PhysicsOwnerShape、VpPreparedPhysicsInput、VpPreparedCharacterCut、VpPreparedCharacterCut.Request、CutPhysicsStep、MobPlanSlotPool、SandboxNpcCharacter、MobPlanCrowd。試験 1 本：MobPlanSlotPoolTests。
    - どれも作業の開始時点で HEAD と同一だった（変更前の写しと HEAD を比較。SandboxNpcCharacter は差分がすべて本件の内容であることを確認）。差分は全部本件の分。
  - 新規ファイル 8 本：製品 2 本（VpBakedConvexMeshes、MobPlanRefillQueue）、試験 2 本（MobPlanRefillQueueTests、VpBakedConvexMeshesTests）、それぞれの `.meta`。
  - 確認：別の worktree で HEAD＋P の compile と focused test を行った（EditMode 197/197、PlayMode 84/84）。PlayMode の件数が main（85）より 1 少ないのは、コミットしない C21 の診断試験がないため。
- **C（計測 check と実装記録）**
  - 新規：`SandboxPropSlashPlayerCheck.MobPlanBurst.cs` とその `.meta`、この記録。
  - 共有の `SandboxPropSlashPlayerCheck.cs`：本件の 6 行（バーストの呼び出し）だけを入れる。同じファイルの Building の未コミットの変更は含めない。
  - **P と独立であることは、コードで確認した**（C だけを当てた試験はしていない）。バーストが使うのは HEAD にある API だけ：`MobPlanCrowd.IsReady`・`Slots`・`FreeSlots`・`ReturningSlots`・`PreparingSlots`、`SandboxNpcCharacter.IsTarget`・`Handle`、`IsHitTarget`、`SlashSweep`、`SlashHitDetector.Evaluate`、check の既存のフィールドとメソッド。Building 側のものは使っていない。Building のファイルがない worktree で HEAD＋P＋C を compile して確かめた。

## Git の外にあるもの
- 起動：`C:\log\zantetsuken-vr\MobPlanSlash\RunMobPlanLoad.ps1`。今回、補充のマーカー（`MobPlan.Refill`、`Refill.Start`、`Npc.Prepare.SlotBake`）と Activate の内訳のマーカーを追加した。変更前の写しは `RunMobPlanLoad.before-*.ps1`。
- 集計：`C:\log\zantetsuken-vr\MobPlanModels\AnalyseActivate.py`。
- 試験の起動：`C:\log\zantetsuken-vr\MobPlanModels\RunRefillTests.ps1`。
- 計測：`C:\log\zantetsuken-vr\MobPlanModels\f13`〜`f18`、build：`build-m20`〜`build-m24`。
