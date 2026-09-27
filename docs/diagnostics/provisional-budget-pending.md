# Provisional の予算終了時の Pending 持越し

## 正本との関係

DESIGN §7.1.1 の「予算終了時は既存 Pending として次の成立機会へ進める」を製品の受付・更新経路へ接続する変更。同期構築そのものを禁止する変更でも、無条件の同期構築を仕様として追認する変更でもない。DESIGN.md は変更しない。

以前の実装は、受付後に Provisional の構築・公開へ無条件に進んでいた。「同期経路しかないので繰越しが不要」という実装記録の説明は撤回する。

## 実装

- `RequestCut` / `RequestPreparedCut` は、分類と受付を一度だけ行う。既存 Transaction の Accepted 段階に、Operation、採用面、親質量、分類、入力 Shape の参照を保持する。
- 構築前と公開前に、同じ描画フレームの残り Main 時間と共有 Dispatch の残予算を確認する。Main 時間は `CutPhysicsStep` が Initialization で開始した既存の単調時計から読み、呼出しごとには再充填しない。
- 構築／公開の予測費用は、それぞれ driver が実測した直近5回の中央値。未計測時は0だが、残り時間が正でなければ開始しない。予測は超過しない保証ではなく、常時不足時の強制進行も行わない。
- 予算が足りれば従来どおり受付呼出し内で公開する。不足なら受付結果は Pending。Source Actor は公開まで Scene に残し、構築後に予算が尽きた場合は正負の未公開 candidate と lease を保持する。片側だけの公開や再構築はしない。未公開 candidate が持つ Joint も既存の容量に数える。
- 製品の Update / LateUpdate / 描画後の `Advance` が既存 Transaction を一機会に一度だけ再開する。再受付・再分類・待機ループ・強制Completeは行わない。同じフレームIDの Dispatch 予算は繰り返し呼んでも増えない。
- 再開時に既存 ledger の authority を照合する。公開時は Source の現在配置・運動を既存 publication で読み直す。予算待ちを Abort として扱わず、実際の構築不能・退役・終了は既存の回収経路へ渡す。
- NPC の受付後に prepared handle が破棄されても、Transaction の入力参照を維持する。Pending 時は既に登録した受付Poseの切断表示に描画を任せ、元の SkinnedMeshRenderer だけを無効にして二重描画を防ぐ。Pose 更新・motion body・root の退出は Provisional の公開境界で起きる。No-op／受付見送りにはこの変更を適用しない。

新しい Transaction ID、別Queue、Scheduler、永続schemaは追加しない。既存の受付結果 enum に Pending を追加し、内部段階は既存 Accepted を使う。

## 範囲

この修正は予算終了時の Provisional 構築・公開の持越しを対象にする。Final が先に揃った際の直接Final分岐は追加していない。既存経路では Final の投入は Provisional 公開後であり、正本の直接Final条件の実装完了をこの修正の成果として扱わない。

## 検証

実行ログと NUnit XML は `Library/PendingBudgetEvidence/`。Unity 6000.3.22f1 の Editor 実行。IL2CPP Player／XR の再build・実行は行っていない。

- 新規EditMode回帰：構築前の持越し、構築後の未公開candidate保持、再受付の拒否、同一frameでの予算非再充填、入力／candidate回収、Source退役、未公開Jointの容量計上、現在配置での公開。
- 新規PlayMode回帰：NPCの実Hitから受付、prepared handleの破棄、複数frameの資源保持、製品Updateでの再開、Final／Geometry Commit、二重描画防止。別の試験では予算の注入をせず、実際のMain時計を使って予算不足と後続frameでの完了を確認。
- 既存のHit座標試験2件には「即時公開に十分な予算」を明示。初回PlayModeではこの2件が Published 期待に対して Pending となって失敗した。座標や命中の期待値は変更していない。
- 表示参照を Missing にしてPendingの描画を消す案は、表示が不正入力として停止し Geometry が CpuPublished に留まったため撤回した（`play4`: 94 passed / 2 failed）。最終版は切断表示を維持し元rendererだけを無効にする。`play5`: 96/96 passed。
- 物理・公開のEditMode全体は、表示接続補正前に514/514通過（`edit-final`）。終了系は別プロセスで各1/1通過。通常ログに Leak Detected はなく、終了系では Persistent 15／17 の警告を記録した。過去の18／20件とのstack同一性はこの実行では検証していない。
- **最終版**：表示引継ぎ補正後も PhysicsCut EditMode 514/514（`edit-final2`）、関連PlayMode 96/96（`play5`）が通過。プロジェクト全試験の実行ではない。
- 初期実行の一部は Temp 配下に保存したため Unity の清掃後に残っていない。途中から Library 配下に切り替え、以後の失敗記録を含めて保持している。
