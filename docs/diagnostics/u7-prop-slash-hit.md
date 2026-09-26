# Segment Hit と Prop の現在状態切断（U7 / Phase 4.51）の作業記録

2026-09-27。基点 main `9467532f`。

- この文書は作業記録で、DESIGN.md は変更していない。
- 実行結果・画像・計測値はリポジトリ外の記録にあり、ここには経路と判断だけを書く。

## 1. 交差判定（19.1.7）

- **正本**: 更新ごとの 4 端点 `conv{A_(n-1), B_(n-1), A_n, B_n}`（`SlashSweep`）と、Fragment が現在採用している Convex（自前 B-rep）。
- **方式**（`SlashSweepConvexQuery`）
  - `Q` は Wave の固定面上にあるので、`Q ∩ C` は `Q ∩ (C ∩ 面)` に等しい。
  - 断面は「面上の頂点」と「両端が面の厳密に反対側にある辺の交点」の閉凸包。空なら非命中。
  - 面内の分離軸で判定する。軸は次の 4 種類。
    - 各面法線の面内成分（断面の辺法線）。
    - `Q` の 2 点差の面内法線と、その方向。
    - Convex の各辺の面内成分。
  - 分離軸が 1 本でもあれば非命中、なければ命中（閉集合、接触は命中）。
- **扱い方**
  - Latch 更新の退化 Segment、平行四辺形、Span 増加の台形、三角形、軸が平行・反平行の線分は、すべて同じ Query に通す。補正・Clip・Reject はしない。
  - 判定厚み、端点 Sphere、刀 Collider、VFX、Render Triangle は使わない。
- **Frame**: Owner の world 変換と `PhysicsOwnerShape.LocalToOwner` を合成し、Convex の数値 frame へ変換する。Sweep の点と面は逆変換、面は `transpose(M)·plane` で移す。
- **候補抽出**（v2 で変更）
  - Physics Scene の照会は使わない。`PhysicsOwnerRegistry.CollectCurrentShapes` で、その時点の Convex 集合を 1 更新に 1 回集める。
    - 対象は Scene にある公開済み Owner と、立っている Provisional 側。
  - Convex ごとに、記録済みの箱（`ConvexBounds`、全頂点を含む）と 4 点の箱を、その Convex の frame で閉区間として比べる。
  - 候補抽出と詳細判定は、同じ「Owner の Transform × `LocalToOwner` × B-rep」を読む。
- **Collider を使わない理由**
  - 手動 Physics（autoSyncTransforms 無効）では、Collider は直前の Step か同期のときの位置にある。
  - Transform で置き直された Body（切断が読み、次の Step の起点にもなる配置）を、Collider の照会は取りこぼす。
  - v1 の OverlapBox で、この取りこぼしを Detector の入口から再現した（5 m の移動と回転で非命中）。
  - cook 差の margin も不要になった。
- **費用**: 1 Sweep あたり、生存形状の数に比例する。E2E の規模では問題にならない。空間索引は今回持たない。Sweep が 0 件の更新では、消費集合の寿命処理だけを行い、形状は集めない。

## 2. 系譜消費（19.1.9）

- `SlashLineageConsumption`
  - 生存 Slash ごとに、直接 Hit で消費した `LogicalFragmentId` の小さな集合を持つ。slot は `SlashWaveCore.Capacity` 個。
  - 候補自身か祖先のどれかが集合にあれば、消費済みとする。祖先は `LogicalCutLedger.TryGetOrigin` → Operation の source を段数分たどる（7.1.2）。
  - 未消費なら集合に追加し、そのあとで受付へ 1 回だけ渡す。
  - 受付結果（No-op、Active、容量）にかかわらず、同じ Slash では再試行しない。
- 生存 Wave の一覧から消えた SlashId の集合は、次の評価の最初に回収する。
- 系譜 Cache、子公開通知、第二の台帳、Fragment 側の Slash 履歴は持たない。
- Detector の公開意味は `LogicalFragmentId`（台帳が発行する不透明 ID）だけで、ObjectId は含まない。

## 3. 受付への接続（4.2、7.6、7.7）

- `SlashHitDetector.Evaluate(core)` は、刀が `SlashWaveCore.Update` を呼んだ直後、同じ Update 相で呼ばれる。
  - その更新の全 Sweep を先に評価して Hit を集める（列挙）。
  - 列挙がすべて終わってから、見つけた順に `ProvisionalCutDriver.RequestCut` へ渡す（受付）。
  - 受付は Provisional pair を公開して source を Scene から外すので、候補を読んでいる途中では受付しない。
- 渡す面は、Hit の判定に使ったのと同じ Convex frame の値。列挙と受付の間に Simulate は入らない。
- **受付順の明示**: 通常経路（prepared lease なし）の `RequestCutCore` では、Shape を読む前に台帳の生存確認と Active 確認を行う。
  - 結果は `NotAccepted` と、`SourceNotLive` または `SourceActive`。
  - 以前は、Active な source の Owner が Withdraw 済みであることから、同じ位置で `InvalidRequest` として断っていた。受付されるものの範囲は変わらない。
  - prepared lease（キャラクタ経路）の契約は変えていない。
- `SlashHitConfirmed` は Detector の出力として、評価ごとに保持する（SlashId、Fragment、Side、受付結果、Operation）。
- **Trace（v2）**
  - `TraceEventType.SlashHitConfirmed = 47` を追加した（既存値は変えていない）。
  - 受付の後、Hit ごとに 1 record を、合成側が渡した既存の Paged Trace lane（`TraceLaneWriter`、`AttachTrace`）へ書く。
  - payload は `SlashHitConfirmedTraceRecord`（28 byte、little-endian）。
    - 内容: SlashId、更新時刻、Fragment、Operation（0＝発行なし）、受付結果、Admission、Side、Latch 更新フラグ。
    - 受付されなかった Hit も記録する。
  - lane が受け取れない record は、lane 自身の drop として数えるだけ。Hit と受付は変えず、再書込も待機もしない。
  - 保存と読戻しは既存の `TracePagedHistory` と `TracePagedHistoryFileStore` を使う。ファイル形式は変えていない。
  - 製品全体の Trace 合成 root はまだない。
  - Sandbox の Player 確認（`-zantetsuPropTrace`）では、確認用の構成が次の順で扱う。
    - lane を 1 本と history を作り、Detector に渡し、毎フレーム drain する。
    - 終了時に lane を外し、Finish・保存・読戻しを行う。
  - 引数なしでは何も作られず、Gameplay は同じ。

## 4. Sandbox の接続

- `SandboxPropCutSetup`（`-executeMethod`）は Sandbox.unity に次を 1 回だけ追加する。
  - `Prop Cut World`: CutWorldRoot、描画、箱、`SandboxSlashPropHit`。
  - `Prop Floor`: BoxCollider。
  - `Prop Check View`: 確認用カメラ。
- 箱は `SandboxCutWorldProbe` を使う（重力あり、Anchor なし）。`cutKeys` を off にし、Hit 以外から切断を頼まない。
- 箱の位置（中心 (1.4, 1.2, 2.0)、0.4×2.4×0.4 m）は、保存済み実機列の抜粋（行 1336〜1847）の Wave から選んだ。
  - 事前の静的解析では、Slash 4 が上部を、Slash 5 が下部の子を通る。
  - 再現のための配置で、Hit の規則には関与しない。
- 刀・Player は layer 8 で、何とも衝突しない。刀には Collider がない。

## 5. 時刻の対応

- 入力再生は 1 行を 1 フレームで流す（Recorder の既存の再生）。
  - 各行の再生時刻＝再生開始時刻＋記録時刻の差分。これがそのまま Core の更新時刻と Wave の時刻になる。
- 物理は実時間で進む（U1 の `CutPhysicsStep`、45 Hz）。
- Player 確認では、フレームレートを記録と同じ 90 に指定する。
  - 各 Hit で、記録時刻の差分・実時間の差分・物理時刻・未消化時間を記録する。
  - 早送りと区別するため、両者の差も記録する。
