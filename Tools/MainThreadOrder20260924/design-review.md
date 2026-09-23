# Main切断処理の設計レビュー — 2026-09-24

対象は新worktree `zantetsuken-vr-main-thread-order-20260924`、基準 `b19ce67a`。元repo・旧worktree・`C:\log` の証拠は読み取りのみ。本ノートは設計とコードの照合であり、性能結果ではない。今回のIL2CPP測定値は別レポートで判断する。過去文書の「保留」「未承認」は当時の作業境界であり、今回ユーザーが承認した実験を禁止する仕様とは扱わない。

## 前回から引き継いでいる実装

- `b19ce67a` は前回の実装 `1a50dbbc` を含む。Geometry重複登録のdescriptor→slot表、Provisional shape view、内部Collider List参照、8 corner距離の共用は既存基準であり、今回の新規改善へ再計上しない。
- `VpGeometryReferenceTable.cs:50,90,206,341,356` にdescriptor→slot表の確保・登録・retire・lookupを確認した。同ファイルのHEAD差分はない。storage所属・descriptor境界・現在世代・Publishedを確認した後の重複検査がO(1)である。**空きgeometry/instance slot探索は引き続き線形**なので、登録全体や全slot管理をO(1)化済みとは書かない。
- `LogicalCutLedger.cs:823–847` の幾何容量拡張も既存。`8bae0b1e` までに入った予約・part件数・fixture終了確認等を今回の追加成果と混同しない。
- 今回の製品候補は、軸平面の箱分割経路とmesh作業配列集約の2件。OrderProbeのBuildSide/Establish順序分岐・timer・newRoot区間、および測定用設定は診断差分であり、製品採用候補に混ぜない。

## 1. 箱の分割方法を入力の形に合わせる：今回の軸平面経路

DESIGN §7.2「Provisional質量特性」は、保守的な箱を面で分けた体積比、負側=親−正側、clip済み箱の近似重心、箱慣性と既存fallbackを要求する。6 tetrahedronへの分解自体は要求していない。したがって、**owner frameへ変換後の面が厳密に軸平行なら2つの直方体として積分する**変更は、この数値的意味と整合する。非軸平行をepsilonで軸へ丸める変更とは異なる。

`ProvisionalBoxMass.cs:89–104,247–285` はextentをdoubleへ昇格してから減算・積を行い、正負のslab体積と重心を求める。有限float boundsのvolume/momentはdouble範囲内に収まる。箱の取得、owner-frameへの平面変換、負側質量の減算、Bodyへ渡すfloat質量の成立判定、慣性のfallbackは共通経路に残る。面上・箱外・退化box・相対的に極薄いslabは旧6-tet経路へ戻す。

演算順が変わるため、全入力でのbit一致は主張しない。親質量範囲 `1e-30 <= parentMass <= float.MaxValue`、相対幅guard、親全質量での箱慣性の各成分が `1e-15..1e30` という条件は**高速経路を使う条件**であり、入力へ新しいclamp・最小質量・上限を導入するものではない。範囲外も旧経路で判定する。慣性の条件と最小側幅から両childの慣性がfloatの0/overflow境界から離れる。追加の独立slab式、斜め面、異なるframe、face/outside、退化・極値・mass/inertia fallback試験を意味の対照とする。親質量が通常値でも慣性だけfloat上限に隣接する反例を追加試験した。

## 2. 同じ寿命の管理配列をまとめる：今回のmeshSlots

DESIGN §7.2「実行分担」「数値Kernel」はJob型、配列layout、保持形状を実装詳細と明記している。一方、Request間独立、予約前にScheduleしない、Work回収前に返さない、失敗・取消時の一度だけの回収は残す必要がある。これらのために各fieldを別NativeArrayへする必要はない。

`PhysicsCutCook.cs:235,612,965` と `PhysicsCutJobs.cs:17,163,306` では、`meshIds / meshBounds / meshVertexCounts / bakeDone` の4配列を1つの `NativeArray<PhysicsCutMeshSlot>` に集約した。狙いはMainでの確保・安全性handle管理・回収の固定費削減であり、worker高速化を成果に数えない。

cut→Main適用→Bakeは同じRequest内で直列、Bakeの各iterationは自分のslotだけを更新する。未実行・未帰還を表す `bakeDone=0` は維持し、独立した完了reportも残す。ただし、従来3配列はUninitializedで、集約後はstruct全体をclearする。**clear量、padding、struct転記、worker側strideの増分もある**ため、確保回数の削減だけから時間短縮を断定しない。管理機構の撤去と配列統合による追加仕事を合わせて測る。

## 3. 成立済みの配置・幾何情報を再利用する：未採用の次候補

**同じMain呼出し内のReposition重複。** 現行buildは両側を配置・速度計算してからD6を作り、publicationはSourceの現在配置・運動を読み直してもう一度Repositionする。連続する内部Build→Publish入口を定義すれば重複削減の余地がある。ただし公開TryBuildの候補状態は読み取り可能で、build後にSourceが動いた場合も公開時の状態を反映する。単に片方を消すとこの挙動が変わる。両Actorの生成時のworld関係、D6 axis/anchors、分離Impulse一度、途中失敗の回収を揃えた専用経路で検証する必要がある。旧監査で保留だったことは永久禁止ではないが、現在の診断的活性化順反転だけではこの変更の正しさを証明しない。

**検証済み・不変なConvex boundsで分類を短絡。** DESIGN §7.6には「各頂点のsigned distanceを一度求める」とアルゴリズムまで書かれている。finite性とbounds包含が成立し、その後形状を変更しない入力なら、保守的な符号付き距離区間がepsilonから十分離れて完全に一側であるConvexは、頂点走査なしで同じsupport分類を証明できる。`ConvexCutOwnerKernel.QueryCapacity` と `Execute` は非Splitでdistance/sign配列を読まないので、不要なper-vertex書込みまで省く余地がある。現在の実装メモを守るためだけに全距離を必ずmaterializeする必要はない。

ただし現bankは変更可能で、現在の分類走査は非finite頂点・非finiteなfloat距離をその場で拒否する。boundsが古い場合やfloatのdot途中overflowを区間判定が見逃す場合、成功/拒否が変わる。採るなら**不変性または更新時の検証・bounds更新契約**と、現float predicateを包含する丸め誤差・overflow条件を明文化する。境界や証明不能な入力は既存走査へ戻す。全頂点Near-planeと支持の存在、集合全体のNo-op、正負epsilonの等号を同じに保つ。これは未実装・未測定の設計候補であり、現可変bankへ無条件で適用できる省略ではない。

## 読み取り根拠

- 新worktree [DESIGN.md](../../DESIGN.md) §7.2 / §7.6、および§7.2内の実装状況・箱保持・固定費削減記録。
- [前回レポート](../../docs/diagnostics/phase41-main-thread-optimization-2026-09-24.md) の採用範囲・設計候補。
- 元repo `docs/diagnostics/phase41-optimization-handoff-2026-09-23.md` §5 / §7 / §8。
- `C:\log\zantetsuken-vr\Phase41RequestAudit\resubmission-contract-and-joint.md`、`Phase41ActorEstablishBreakdown\build1\pre\source\`。
