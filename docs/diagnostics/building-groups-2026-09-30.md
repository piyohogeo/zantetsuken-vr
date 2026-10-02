# 建物の休止・融合・集約・予算（2026-09-29〜30）— 仕様・制限・検証の要約

Git 外の記録（`C:\log\zantetsuken-vr\PlayableCity\RESULTS-fusion.md` §1〜§14、`RESULTS-rest.md`、`RUNBOOK.md`、`COMMIT-BOUNDARIES.md`）の、コード側に残す要約。既定はすべて無効（profile `buildingRestEnabled`／`buildingFusionEnabled` は false）。

## 1. 何をするか
- **休止（BuildingRest）**：支持された建物の片を timeout 後に明示的に休ませる（Sleep／Kinematic）。Kinematic 休止は支持（地面・アンカー・下の片）の追跡と局所解除（切断片の上に立つ片だけを動的へ戻す）を持つ。
- **融合（BuildingFusion）**：休止した片を建物ごとに 1 つの Kinematic 群 Rigidbody に融合し、群への命中は群全体の切断（平面が横切る Member だけ論理切断、左右 2 つの複合 Body）。自由な側は dynamic 群として追跡し、休止したら建物の休止群へ合流。
- **集約**：期限（`buildingFusionDeadlineSeconds`、実時間）または別 Slash の命中で、建物の群を「固定 1・自由 1」へ戻す（休止群の合流、自由群の union＝運動量合成）。集約中の命中は保留して集約後に処理。
- **Main 予算と worker 準備**：Step の Main 時間予算（`buildingFusionMainBudgetMs`、既定 1.5 ms）の下で、Final 回収・snapshot・公開・union・融合・保留の振り分けを単位ごとに `MayStart()` で進める（保留中の質量反映の見込み費用を差し引く）。切断は Pending で受け付け、群ローカルに一度だけ保持した採用平面から各 Member の局所平面を作り（群が動きながらの分割 snapshot でも平面は一つ）、走査と箱の質量分割は共有 dispatcher の BackgroundPool で行い、後の Step で Main が全 Member を再判定して全部か無かで公開する。質量集計は Burst job を即時 schedule し Step 終端で完了・適用（Main 待ちは Step 終端へ移動、再予約分も計数）。
- **命中の振り分け**：同じ Slash かつ同じ採用平面（掃引ごとの識別 `ProvisionalCutAsk.adoptedPlaneId`）の後続命中は準備へ関連付け、切断中の群なら包含済み。それ以外は保留して群が自由になったら再振り分け（Member が Final で置換されていれば dropped）。1 命中 ＝ 1 記録（`BuildingFusion.Hits`：事象と、1 回だけ設定される結果。関連命中は準備の結果を受ける）。保留の集計 `HeldRouted` は「切断経路へ振り分けた件数」であり公開数ではない。

## 2. 所有権と終了
- snapshot の shape 保持と分類 block は offer までは融合、offer から回収までは dispatcher、回収後は再び融合が所有し、準備の終了時に所有者が解放する。dispatcher の Shutdown は worker 停止を確認して全 work を回収してから融合を Dispose する。取消された未開始の work は再 offer。Main での走査経路はない。
- 漏れの検出：`PhysicsCutClassification.Live`、`BuildingFusion.LiveSnapshotBlocks`、`PhysicsOwnerShape.WorkUsers`。

## 3. 物理時計と検証 step（融合の有無によらず作用）
- `ManualPhysicsClock.MaxOwedSteps = 2`：負う時間は 2 step まで、超過分は破棄して `DroppedSeconds` に計数（長い frame の追い付きは保存しない）。
- `CutPhysicsStep`：費用見積りだけで 45 回連続して見送ったら、または `RequestCostReevaluation()` が呼ばれたら、予算を超えても 1 回の検証 step を取り、見積りをその実測から始め直す。

## 4. 制限・留意
- 「アンカーを含む側は固定」を仕様とする：college_001 の 21 アンカー配置では自由部分が生じない。1 点アンカー（index 0）の診断構成で自由部分と集約を確認した。
- 採用平面の識別は掃引ごと。同じ Slash でも次 frame の掃引は別要求（保留）であり、「同じ Slash の後続命中をすべて一本化」するものではない。
- 大きな側の公開（約 100 Member で ≈1.1 ms）と最初の切断の質量適用（≈0.9 ms）は分割不能な単位として残る。
- 保留の最大待ちは frame 単位の進行の積み重ね（Player で最大 56.6 ms）。

## 5. 検証
- Editor 試験（main の作業木、2026-09-30）：`CutWorldRootPlayModeTests.(Fusion|KinematicRest|PhysicsStep_)` 46／46（`ag18`）、`FusionBudget_*` 12 件（取消直後の予算切れ、未開始／dispatcher 待機中／実行中の準備を残した終了、群を動かしながらの分割 snapshot、近接した別平面、同群・別群・別 Slash の命中振り分け）。
- Player（college_001、1 点アンカー、期限 0.9 s、7 Slash、125 s、`q3`）：**掃引 ID 判定より前の版**で fusion Main 最大 1.50 ms／frame（p99 1.07）、見送り 0、超過 1 Step 0.002 ms、旧形式の処理記録 55 行（保留の再振り分けを含む。ユニークな命中数ではない）すべてに処分、保留 19 件すべて振り分け済み、群切断 18、Final 112、物理停止なし。最終版（掃引 ID 判定・群ローカル平面・命中記録の分離・振り分け集計）は Editor 試験のみで検証しており、**Player 確認済みとは扱わない**。
- 性能比較（40 箱 × 8 片）：Rigidbody 40 vs 320、群切断 <1 ms（初回 22 ms JIT）。
- コミット境界の検証（branch `boundaries-2026-09-30b`）：A EditMode 86／86＋PlayMode 2／2、B EditMode 9／9、C EditMode 9／9＋PlayMode 2／2、D PlayMode 52 件は **51／52（`FusionAggregate_ByTheDeadline…` の融合後 `linearAfter > 0.1` が 0.0256 で外れた：反対向きに動く 2 群の合計運動量が小さかっただけで、保存は成立）、修正前の再実行 `FusionAggregate_` 4／4（`bnd2-D-play2`）**。第 1 branch（`boundaries-2026-09-30`、physics step の挿入位置が異なる版）の 52／52 とは別の結果として扱う。E PlayMode 1／1。その後、同試験を union 直前に両自由群へ既知の速度を与える決定的な形に補正し、修正後に `FusionAggregate_` 4／4（`bnd2-D-play3`、linear (12, 0, 0) → (12, 0, 0)）。

## 6. コミット境界（2026-09-30）
A 容量引き上げ・拘束の事前拒否・D6 切替 API → B snapshot 走査削減・計測 marker → C 物理時計・検証 step（時計試験を 2 step 上限へ補正、物理 step の焦点試験は融合無効の素の World）→ D 休止・融合・集約・予算・worker・命中処理 → E PlayableCity・建物 E2E・check・終了処理・工具・記録。Piece replay 試験（`BuildingRest` を使う）、c_21 診断試験、状態再生診断試験は今回のコミット対象外。

## 7. 最終版の単棟 Player 確認（2026-09-30、`320f88c9`、college_001＋床、アンカー index 0、Kinematic 休止・融合、World D6 なし、期限 0.9 s、予算 1.5 ms、入力 行 3800 から 7 Slash、Light 1 回）
- 結果：code 0、125 s。Player SHA-256 `427C7F94…18CC8590`、入力 SHA-256 `4897E35C…A4135D8E`。
- 機能：集約 7（期限 1・次 Slash 6）すべて完了、完了時は固定 1・自由 1、終了時は 1 Kinematic 群（147 Member、実 Body 1）。群切断 21、Member Operation 146 すべて Completed・子一致・Commit。融合 Member へのユニーク命中 39：Published 24、Dropped 15（別要求の保留中に Member が Final で置換）、pending 0、関連付け 3 は準備の結果を受けた。通常終了。
- 性能：fusion Main／frame 全体 中央値 0.005・p99 0.029・最大 1.639 ms（最大は最初の群切断の公開＋質量適用）、予算超過 3 Step（最大 0.139 ms）、質量 job の Main 待ち 1.71 ms、準備待ち最大 34.1 ms、保留待ち最大 56.1 ms、群 ≤9、Rigidbody ≤10、hull ≤1180、Simulate 最大 4.36 ms（cut 相）、見送り連続最大 2、検証 step 0、停止なし。
- **総合合格にしない**：PhysX の凸包 cooking 失敗。同じ mesh（`Zantetsu Physics Cut 0`）について、worker の bake と Main の Final の Collider 割り当てで 1 回ずつ（error 二組は二件の切断失敗ではない）。ログが示すのは「PhysX が有効と認めた頂点が四つ未満」で、元の入力頂点数や薄さが原因かは未確定、該当 Member も未特定。1 つの子 Member の Collider が有効な形状なしで公開され、当時の融合の Final・check はこれを検出しなかった。この run は性能と集約の証拠には使う。
- 未確認：union ごとの実 Body 減少の attachedRigidbody による確認（終了時のみ確認）、Player での worker・入力保持・影 Collider の個別回収計数、包含経路（0 件）。
- q3（旧判定・旧計数）とは命中・切断の数が異なるため、速度の改善率として比べない（q3：処理記録 55 行・群切断 18・Final 112、今回：ユニーク命中 39・群切断 21・Final 146）。

## 8. 公開前の形状成立確認（2026-09-30、未コミット）
- `Physics.BakeMesh` は成否を返さない。`Collider.GeometryHolder.Type` が、有効化された Collider が active な GameObject 上にあるときに限り、cooking 成功を `ConvexMesh`、PhysX が拒否した mesh を `Invalid` と返す（無効・inactive では区別できない）。試験 `ConvexHullValidityProbePlayModeTests` で確認。拒否される入力は、4 頂点のうち 3 点だけが異なり（1 点が重複または 1e-7 m 以内）3 軸に広がる形。
- `PhysicsCutCook` は bake の後、products を作る前に新規 mesh ごとに probe Collider で形状を確かめ、拒否されたら既存の `CookFailed` で cut を終え、拒否の記録（cut 通番、convex、正負、mesh、頂点数）を残す。通常経路は driver の失敗計数と Abort、融合経路は該当 Member の `Fail` で回収し、それぞれ一度だけ Operation・Member に帰属させる。形状が成立しない Collider は公開されない。薄片を黙って除外する処理は入れていない。
- check は PhysX の cooking error を mesh 名で数え、cook の拒否に対応しない error、形状のない有効 Collider、帰属されない拒否があれば不合格（`[cooking]`）。計数・回収済みの失敗は許容。
- 焦点試験：通常・融合の両経路で、試験の箱の切断の生成 mesh を bake 前に拒否される形へ置き換え（試験 hook）、公開なし・一度だけの計数・保持物の回収・その後の通常切断の成立を確認。自然にこの形を出す kernel 入力は見つけていない。Player での確認は未実施。

## 9. 形状検査の予算化と Player 再確認（2026-09-30、未コミット）
- 形状検査は cook の `Checking` 段で 1 mesh ずつ進み、翌 frame に持ち越せる。融合の切断は融合の Step が共通予算の下で進め、通常の切断は cook の pump が driver の frame 予算を要求間で共有して進める（frame に 1 件は必ず進める）。検査が終わるまで Final は公開されず、終了時は検査中の切断も回収される。1 切断の全 mesh を検査し、拒否 mesh はそれぞれ数え、切断失敗は一度。check の監査は共通終了で購読を解除する。
- 単棟 Player（`320f88c9`＋この差分、126 s、code 0）：cooking error 0、形状のない Collider 0、`[cooking]` 判定 ok。**拒否と回収の経路は Player では行使されず未確認**（Editor の焦点試験で確認済み）。検査の Main 費用は合計 5.6 ms／126 s、frame 最大 2.1 ms（最初の検査 1 単位：probe の初回生成）で、fusion Main の最大も 2.13 ms、予算超過 4 Step（最大 0.63 ms）。集約 10 すべて完了、物理停止なし、通常終了。台帳の室（34 > 32）による群切断の拒否 4 件が新たに出ており、検査段との因果は未確認。
- `CutWorldSandbox.exe` は IL2CPP の起動 stub で hash が build をまたいで同じ。版の識別には `GameAssembly.dll` の hash を使う。

## 10. probe の事前準備・未完了容量・終了後の最終照合（2026-09-30、未コミット）
- 検査用の probe は World の構築時に作り、既知の有効な四面体で一度読んでから無効化する。Player で準備 0.18 ms、最初の実切断の検査 1 件 0.047 ms（事前準備前は最初の検査 1 単位が 2.1 ms）。
- 未完了切断の容量 `maxIncompleteCuts` を 32 から 4096 へ（既定値、共有 profile、再生成される派生 profile）。群切断は交差する Member ごとに 1 つを同時に取るので、32 では台帳が空でも 34 Member の群切断が入らなかった。34 Member の群切断の受付・完了を焦点試験で確認、小容量での全体拒否の試験も通過。
- check の cooking 監査は、要約時は検査待ちを別に数え、World 回収後に最終照合する：各 error が「拒否・回収済み」か「未検査だが Abandoned・未公開・回収済み」でなければ不合格。bake error の mesh を未検査のまま終了する焦点試験で確認。
- 単棟 Player（125 s、code 0）：群切断の拒否 0（同時未完了の最大 14、容量上限は行使されず）、fusion Main 最大 1.43 ms・予算超過 0、終了後の監査は error 0。cooking の拒否と回収は Player では行使されず未確認。

## 11. NPC・プロップ・建物一棟の共存確認（2026-09-30、未コミット、不通過）
- 構成：PlayableCity seed14、建物は 1 点アンカー・D6 なし・休止と融合あり・期限 0.9 s・Main 予算 1.5 ms、`maxIncompleteCuts` 4096（再生成後の profile で確認）。
- 結果：約 130 s で表示の draw command が上限 2048 に達し、World が Player を終了した。check は INCOMPLETE で、通常終了と cooking の最終照合は行われていない（融合の要約は出た）。
- その前に、建物の Final が 2237 まで増え、物理時刻がほぼ止まり（連続見送り最大 172 frame。step した frame の Simulate は合計 2.79 s、p99 5.41 ms、最大 50.0 ms）、群切断の公開で融合 Main が最大 130 ms になった。
- 判定「建物が anchors で固定されて登録」が 1 件失敗した。原因は未確認。
- 原因調査と修正は TL の判断待ち。

## 12. 公開と質量の重複走査の除去、表示上限、共存の再実行（2026-09-30、未コミット）
- 表示の 6 上限を 65536 にそろえた（初期容量は維持）。記述子 8192 など伸びない表が先に効く。
- 群切断の公開と質量から、Member ごとの全切断の探索、Member ごとのアンカー再走査、全 copy と全 crossed Collider の組を除いた。除外は同じ Member と、bounds が接する組だけにした。copy は旧群の下に非有効で予算内に作り、側の質量標本は snapshot から作る。
- 1452 Member の合成群の公開は 169.5 ms から 29.0 ms（Editor）。
- 共存の再実行は台本の最後まで進んだが code 14。物理の連続見送り 251 frame、融合の保留 hit と集約が残った。建物の scenario 判定 2 件は check の集計が融合の公開を数えないためと見ている（未確認）。
- 残る費用の大きいもの：snapshot の Validate（合計 40.5 s、最大 185 ms）、融合の質量の適用（合計 10.4 s、最大 54 ms、原因未調査）、大きな公開の移動と除外（最大 59 ms と 61 ms）。
- cooking の拒否が Player で 1 件起き、経路どおり回収され、最終照合も合格した。

## 13. Validate の二乗走査、衝突除外の復元、質量適用の内訳、check の融合対応（2026-09-30、未コミット）
- snapshot の構造検証で、登録ごとに全登録の root と比べていたのを、root 表の重複記録を 1 回引く形にした。3000 登録で Validate は 233 ms から 10 ms（Editor）。拒否の結果と順序、snapshot の内容は同じ。
- 衝突除外の組を公開時の bounds で減らす案は取り下げ、全 copy と全 crossed Member の組へ戻した。
- 質量の適用を部分ごとに測るようにした。Editor の合成群では 1704 Member の適用が 0.06〜0.08 ms で、Player の 54 ms は再現しない。
- check は、融合の建物の成功を命中から Member Operation・台帳・Geometry Commit まで照合し、cooking 拒否は回収を確かめて別に数える。待機の完了条件に融合の待ちを含め、期限で終われば未完了として理由を記録する。群衆の計画間隔は欠測と停止を分けて判定する。
- cooking の拒否と回収は、共存の Player（cx3）でも確認済み。

## 14. 群切断の衝突除外を層の対で（2026-09-30、未コミット）
- 群切断ごとに専用の層の対を貸し、交差 Member の Collider を一方の層、その copy をもう一方の層へ移す。対の 2 層だけが互いに衝突せず、他はすべて基底層（Default）と同じに衝突する。関係は組ごとの除外と同じ。
- 1452 Member・交差 252 の合成群で、公開は 136.8 ms から 23.2 ms、除外の API 呼び出しは 63,504 から 504（Editor）。公開後の Simulate と後始末は同程度。
- 対は 12 組で process 共有。空きがなければ公開は受付前で待つ。最後の利用者が層の行列を元へ戻す。
- 実際の接触で、Final 保留中の移動・回転、2 切断の同時、一部だけの Final、空きなし、終了、cooking 拒否を確かめた。

## 15. 群切断で旧 Root・Body を片側に残す（2026-09-30、未コミット）
- 群切断は旧群の Root・Body を、移す Collider の少ない側に残し、新しい Body は反対側の 1 個だけ作る。正負の意味、アンカーによる固定／自由、質量と速度の継承、休止の追跡は切断後の側として整える。
- 1452〜1988 Member の合成群で、移動する Member は偏った切断で 1/6〜1/12、均等な切断で約半分になり、公開は 28 ms 前後から 8〜9 ms、均等で 21 ms から 16 ms（Editor）。
- それでも 1 回の公開は 1.5 ms を超える。残るのは、移る Member と影の付け替え、休止の追跡の付け替えからなる切替そのもの。
- 層の対が使えないときの組ごと除外への戻りは、理由別に数え、性能保証の対象外とした。

## 16. 共通の融合 Hull の試作：設計（2026-09-30、実装前）
- 群が Hull（群座標の convex 1 個、焼き込み Mesh、Collider 1 個）・アンカー・質量・表示 Member・消費済み Slash・世代を所有する。表示 Member は body・Collider・cook・影を持たない。
- 命中は群 Hull だけを候補にし、対象 ID と世代で受け付ける。1 命中 1 記録。消費は両子へ複製、融合で和。
- 準備は Worker（既存 cook）、hull 検査は共通予算、公開は物理 step 前の Main 1 区間。台帳受付前の失敗は旧状態維持と帰属。公開後の表示失敗は DAG の規則。
- 融合は同じ Body に集約した Hull から、群座標で凸包（増分法、L=128）。世代を照合して採用。失敗は旧 Hull 維持。質量は和、隙間から増やさない。

## 17. 共通の融合 Hull の試作：結果（2026-09-30、Editor、未コミット）
- 実装：`ConvexHullBuilder`／`HullBrep`、`BuildingHullFusion`、`CutWorldRoot.BuildingHull`、`SlashHitDetector` の Hull 対象、`PhysicsFragmentOwner.DisplayOnly`、profile の `buildingHullEnabled`（既定 false）。
- 1 Hull の建物で、命中 → Hull 切断（cook）→ 表示 Member の DAG 切断 → 落下・保持 → 集約・融合 → 再命中が通る。10 回の切断・融合で Collider 1・cook 10・最大頂点 8、表示 Member は 129 まで増える。
- 凸包 builder は、面の統合を基準平面と隣接で行い、頂点を平面の交点で再計算し、閉じた凸を検証する。密着した 2 片の融合 Hull も kernel が切る（52 回、失敗 0）。
- 公開の費用は表示側（Member の分類と admission）が大半で、Member 数に比例する。

## 18. 共通 Hull 試作の境界補正（2026-09-30、Editor、未コミット）
- 凸包：統合面の支持平面から半空間交差を双対 Hull で厳密に構成し、包絡の膨張が統合許容差（2e-3×範囲）を超える間は正確な面を戻す。検証（閉じた辺・Euler・平面性・凸性・全入力点の内包）は数値許容差（1e-6×範囲）で独立に行う。360 例（3 サイズ×3 姿勢×2 順）で shrink ≤ 6.9e-7×範囲、膨張 ≤ 1.56e-3×範囲。
- 融合：走査→Mesh 準備（Main）→Bake（Worker）→照合・交換（Main）。同期 fallback なし、投入拒否は待って再提示、待機中取消は再提示、実行中は放棄印。
- 公開：表示 Member の側は表示頂点の実走査（空側の子・退役 Member はどちらの側にもない）。表示の受付可否（live・他 Operation・容量・空き）を物理公開の前に判定し、不可なら物理も表示も旧状態で拒否、空き不足は旧状態で待つ。
- 計数：cook 要求と Bake（子 Mesh・融合）を分離、最大 Hull 数、初回と以後の公開時間、完了条件に未融合 Hull・表示 Operation・期限超過の対を含める。
- 試験：表示受付拒否、台帳の空き（待機と容量超過）、Worker 投入拒否・取消、融合失敗後の進行、形状誤差のサイズ依存。回帰 EditMode 1167／PlayMode 162／終了 6。

## 19. 共通 Hull と休止の接続、college_001 単棟（2026-09-30、Editor、未コミット）
- 所有：Hull on・Rest Kinematic on・旧 Fusion off・D6 off。登録は `TryAddBuildingHull`（`PlacedCuttableRegistration.RegisterHull`）。Rest の群 API（TrackGroup/SplitGroup/FuseIntoGroup、新規 RepointGroundColliders）と通知（GroupBusy/Held/Releasing）を Hull 側が所有。
- 集約は建物単位（固定 class／自由 class をそれぞれ 1 群へ）。融合拒否は 3 回で諦めて記録。
- builder：三角形 Hull の可視領域を隣接展開に、半空間交差をクリッピングに変更（実建物の同一平面上の点群で双対 Hull と ε 可視判定が破綻）。数値許容差 1e-5×範囲、近似幅 2e-3×範囲。
- college_001：10 切断・10 融合・休止 10、終了時 群 1・Body 1・Hull 1・Collider 1、膨張最大 3.8e-2 m、貫入増分最大 0.8 mm。初回 Step 17.2 ms（公開 17.0）、warm では 1.49 ms。
- 回帰 EditMode 1170／PlayMode 168／終了 6。

## 20. 限定補正と単棟 Player 1 回（2026-09-30、未コミット）
- 断念＝失敗完了（`IsSettledWithoutFailure`、理由付き終端、別群合体は新世代での再試行）、公開の側参照を線形に（`SideLookups`）、保存入力 2 件に独立検証（1e-5×範囲は採用値：1e-6 失敗・1e-5 通過の記録）。回帰 1170／169／6、焦点 15。
- Player（hull profile、anchor 0、床）：登録・9 切断・休止 2・集約 7（期限 1・次 Slash 5）・融合 5・表示 Commit 48 まで通ったが、断念 1（141 v > 128）、終了時 群 3、check の早期終了（固定 1＋自由 1 を settled 扱い）、同一 Slash の再命中 96、融合 Hull の貫入 2.31 m。停止、TL 判断待ち。

## 21. 重複受付・再集約・貫入判定・頂点上限・check（2026-09-30、Editor、未コミット）
- 予約（Pending／Held で固定、子・合体へ継承、自分の予約でだけ再開、完了で回収、Slash 終了後も要求は残る）。休止・解除で集約再評価（切断時計のまま）、完了判定は群構成（休止 2 群は未完了、固定 1＋自由 1 は完了、全群休止は別指標）。自由群は合体せず休止を待つ；採用前に相手別の貫入増分（既定 0.1 m）を判定し、超えれば「1 Hull化未達」で旧 Hull 維持。頂点上限は面除去（近似幅内・内包維持）で達成：131 点 → 127 v、膨張 5.16e-2 m、実 cook・再切断 OK。check は建物所属 Collider だけ、全 frame の費用、表示専用 owner の NRE 修正。回帰 1171／174／6。

## 22. 終了待ち補正と単棟 Player 再実行（2026-09-30、未コミット）
- `IsSettled`（自由 2 以上も待つ）と `IsOneHullAchieved`（実数で判定）を分離。未達群は複数 Hull を保持し再切断不可（制限）。全交換の相手別記録、群の最大速度。
- Player hf3：code 0、[hull] 12/12。22 切断・休止 22・集約 7（期限 4・次 Slash 3）・融合 1・未達 6（候補が未休止の他群を最大 15.1 m 貫入）・終了時 1 群 1 Hull。実行中の Collider は最大 23（未達期間は片数に比例）。Main 合計 物理 16.7／表示 1.2 ms、frame 合計 p99 0.87／max 2.62 ms。

## 23. シナリオ Player 実行（5 mm、2026-09-30、未コミット）— 不合格・停止
- 連続 Slash 30 s → 回復 → 保持 → 再切断 → 回復。hg3：code 14。公開 13・Dropped 4・NotAccepted 1297、集約 6 すべて未達（最初 +0.024 m、以後 2.7〜8.9 m の自由群貫入、最終 木 +0.253 m）、融合 0、終了時 1 群 14 Hull、未達 58.2 s。群 最大 9・Hull 14・Body 9。再切断区間 512 行・掃引 946・命中 0（交差なし）。Main 物理 19.0（命中 12.1）／表示 0.8 ms、frame 合計 p99 0.045／max 0.91 ms（5498 frame）。
- 所見：NotAccepted 経路は予約を作らず同じ Slash が毎サンプル再命中（check 系統判定 1283 件）。製品変更なし、TL 判断待ち。

## 24. 合体順序の変更：候補先行・採用時合体（2026-09-30、未コミット、Editor のみ）
- 休止群 2 つ以上から候補 Hull を Worker で構築・bake、参加群を除外して外部貫入を前後比較、採用直前に世代・相対姿勢・Kinematic を再確認、採用時だけ Body 合体。不採用は元の Body・Hull・Member を維持し再切断可；同じ休止クラスは再試行しない（切断・保持・解放で新クラス）。builder 拒否は 3 回で断念（失敗完了）。群は常に 1 Hull（United 撤去）。
- 即時 NotAccepted も閉じた予約を残す（同 Slash 同対象の再受付なし、別群は可）。未達最長は継続中を含む。
- check の再切断は合成 Slash（検出器経由、3 判定分離）。焦点 20/20（修正後）、EditMode 1171/1171、PlayMode 176/176、終了 6/6。Player 未実行。

## 25. Player 前の焦点試験で停止（2026-09-30、未コミット）
- 記録のみの追加（保留時間、融合済み表示、判定済みクラス再試行数、snapshot 列、Member 列、最後の Slash 時刻、再切断の種類別判定）後の hi1-play：19/20。CollegeHull_TenCuts が、最後のクラス [8,11]（102 点）で builder 拒否 ×3 → 断念（面の非凸 2.67 cm）。前回は同クラス 66 点で採用。軌跡は実時間依存で分岐。102 点を保存。Player 未起動、製品修正なし。

## 26. 102 点の再現と builder 補正、シナリオ Player（2026-09-30、未コミット）
- 原因：三角形 Hull の可視判定が浮動閾値で、細片の隣で凹の辺を許し、Hull が最大 11.7 m 非凸。その平面を戻した交差 round 1 で近接 4 平面の角ができ、併合半径ちょうど外の 2 頂点＋価数除去で面の頂点順が往復 → 2.68 cm 凹角。補正：向き判定を 2^-20 m 格子上の厳密判定に（許容差は据え置き）。102 点は 36 頂点で成立、独立検証に面内凸性（凹角・外角総和）を追加。回帰すべて通過。
- Player hk3：code 14。連続 Slash で公開 513、候補 68 のうち採用 0（未達 55）、群 1→514 と切断数に比例して増加。重複受付 0。回復未達（最後の Slash から 12.40 s）。合成再切断は掃引が隣接群に掛かる設計不備で 30,299 命中の過負荷、保留 28,408 件未終端。Hull 交換の貫入測定で Step 最大 559 ms。質量 0 群が 101.5 m/s。

## 27. 短時間 D6 拘束の試作（2026-09-30、未コミット、Editor のみ）
- 期限停止（0.25 s、既定無効）＋兄弟 D6（開き 0〜5 cm、Provisional と同じ自由度）。D6 あり／なしとも候補はすべて採用、群は最大 2。D6 ありは沈み込み・回転なし、なしは衝突除外で最大 0.378 m 沈み込み。拘束残 0。Kinematic 試作には未着手。
- 回帰で既存の college 10 回切断が builder の別欠陥（round 1 のクリップ直後に 1.61 cm 凹角、80 点）で断念 → 停止。

## 28. 80 点のクリップ補正と街の Player（2026-09-30、未コミット）
- 切り点の内挿で、面上判定の幅で内側に残した頂点を平面上として扱う（辺の外への外挿をなくす）。80 点は構築・独立検証とも合格。回帰すべて通過。Player ho3：code 14。回復・保持・融合済み Hull の再切断は合格、再切断後の融合が木で不採用。群は最大 53、拘束の開き逸脱・2.7 m の滑り・11 m/s を観測（床の押し戻しは未検証）。表示 Member 4,245、snapshot 最大 71.8 ms。

## 29. 常時 Kinematic＋表示アニメーション（2026-09-30、未コミット、Editor のみ）
- 1 Kinematic Body・1 Hull・1 Collider を常に保持、表示だけで切断・落下（0.25 s、水平 15 cm〜垂直 2 cm）、Hull は Worker で best-effort 交換（世代照合、建物あたり 1 件＋待ち列にまとめ）。焦点 8 試験と回帰すべて通過。表示 Member の増加（10 回で 228）は別単位。

## 30. Hull 更新の待機方式、check の補正、常時 Kinematic 版 Player（2026-09-30、未コミット）
- Hull 更新は実行中 1＋最新の待機 1（置き換えは省略として計数、古い結果は新しい要求の前へ戻さない）。check は開始時にモード分岐、各 frame で建物ごとに 1 Body・1 Collider・1 Hull を判定。snapshot の子 marker と表示カウンタの差分、Member の確定／空／未確定を記録。
- Player hr3：code 0、全判定合格。表示公開 33・Hull 交換 33（省略・拒否 0）。表示：33 切断で表示切断 12,345、Member 12,346（空 88%）、ledger 24,691／12,345、snapshot は 12,000 Member 超で p50 57 ms（Validate 19.5・Collect 29.5・Place 7.4）。表示の整理は次の単位。

## 31. 切断面に沿う滑り、表示分類の実 indices 化、カウンタ補正、Player ht3（2026-09-30、未コミット）
- 滑り：t＝g−(g·n)n。水平に近い面（傾き 5° 未満）では掃引の進行方向の面内成分、無ければ世界 +X、次に +Z の面内成分。下方向への fallback は無い。方向は公開時に固定し、法線の符号に依らない。距離・時間・側の規則は従来どおり。表示と Hull に同じ移動。
- 分類：Commit 済み geometry の実 indices が参照する頂点で判定。未 Commit・lease 拒否は待機、片側だけの Member は配置のみ。固定入力（箱を x=0 で切った正側の子を x=−0.5 で分類）で、ブロック方式の偽交差 2 から indices 方式 1 になり、Operation +1、空の子 0。
- カウンタ：容量拡張で作り直す snapshot の値を退避累積し、差分の負は 0。
- Player ht3：code 0、全判定合格。命中 33、表示 Operation 734（1 命中平均 22.24、最大 57）、空の子 0、保持 735、snapshot 2Snapshot p50 0.18／p99 3.24／max 21.4 ms、面内の移動の読み戻し最大 |d·n| 1.7e−7 m。hr3 との差は切断対象が変わっているため改善率にしない。
- 新しい観察：Hull が頂点上限 128 に達し、最後の 7 回の更新を拒否して旧 Hull を保持。Worker の走査は 1 回最大 150 ms。映像は ht4（画像 run）。

## 32. 計測と表記の補正、常時 Kinematic 建物の共存 Player hv3（2026-09-30、未コミット）
- 子の状態は、空・形状確定・後で再切断・後で退役・未完了・照合不一致（Completed なのに geometry なし）・ledger に無い、に分けた。表示の構造再構築・検証・配置の回数は、実行した frame に付けて frames.csv の marker と同じ行に書く（±1 frame の区分は廃止）。焦点試験で、構造再構築のある frame と Collect marker のある frame は完全に一致した。
- ht3 frame 2499 の 21.4 ms は、容量拡張（512→1024）を伴う frame として扱い、初回費用とはしない。子 marker の外の約 10.2 ms は未帰属のまま残す。
- 共存 hv3：code 0、判定 ok 895・FAILED 0。建物は命中 34（公開 32）、表示 Operation 1,033、各 frame 1 Body・1 Collider・1 Hull、Hull 交換 31・拒否 1。NPC は root 40・子 145、プロップは root 1・子 62、異種命中 32 Slash、補充 40・枠の再利用 11。計画の公開間隔の最大 1.26 s。通常終了し、cooking の最終照合は誤り 0。snapshot の最大 55.8 ms は容量拡張 frame（未帰属 17.4 ms）。アニメーション途中の再切断はこの台本では起きなかった。

## 33. 反映済み境界の照合の索引化と共存 Player hx3（2026-10-01、未コミット）
- 構造構築ごとに、登録ごとの reflected の索引を一度作り、Validate・Collect・Group の照合に使う。等価は `VpClipBoundary` のまま、キャッシュは構築をまたがない。固定入力（深さ 10〜80、20 登録）で、走査と索引の snapshot の全内容・順序、4 種の拒否が一致した。深さ 80 で、走査の比較 273,868 回が索引では 0 回（登録 1,588）、構築は 7.78 から 1.38 ms。
- §40 の訂正：Present 差引値は Main 作業時間ではない（負の frame 11）。公開待ち 251 ms は表示準備が予算で複数 frame に分かれたもので、アニメーション待ちではない。旧「建物の片」判定は適用外と表示する。
- 共存 hx3：code 0、判定 ok 1,100・FAILED 0。run の中身は hv3 と違うので改善率にはしない。拡張のない構造再構築で、Collect は p99 6.2・max 11.0 ms、Validate は p99 6.4・max 14.5 ms（hv3 は 18.2・25.2 と 14.7・25.4）。建物の公開待ちは最大 302.6 ms（準備 10 単位・10 frame）。独立区間で、途中の再切断（道のりの 13.7% で停止）、両命中の公開・Commit を確認した。

## 34. 範囲で片側と確定した Member の走査の省略（2026-10-01、未コミット）
- 表示の分類で、lease を取った後、Commit 済みの子 geometry 自身の記録された範囲（`TryGetPublishedExtent`）が、同じ座標・同じ許容差に浮動小数の保護幅を加えて確実に片側にあれば、実 indices を読まずに確定する。面をまたぐ・近い・範囲がない場合は従来の走査に戻す。未 Commit・空・lease 拒否・現在姿勢の扱いは変えない。
- 固定入力の 4,600 通りの面で、参照（全走査）と全件一致。片側が多い入力では 2.99 から 0.80 ms、交差が多い入力では 0.41 から 0.46 ms（範囲判定の分）。
- 共存 hy3：code 0、判定 ok 1,183・FAILED 0。分類 14,000 のうち 11,176 を範囲で確定。本編の建物の公開待ちは p50 15.9／最大 111.2 ms（準備は最大 4 単位）。run の中身は hx3 と違うので改善率にはしない。「読んだ indices」の過去の値は、対象 index 範囲の総長だった。

## 35. snapshot の Validate の内訳と改善、回帰の不通過で停止（2026-10-01、未コミット）
- Validate を、索引・入力検査（うち契約）・祖先・全 Operation と、配置だけの入力検査に分けて測る（時間と回数、frame ごとに合算、frames.csv に 19 列）。建物に似せた固定入力（2,029 登録）で、最大は契約の検査（構造 3.48・配置だけ 3.36 ms）で、その大半は indexer による行列要素の有限性の読みだった。
- 改善 1 件：行列の有限性を field で読む（同じ検査、順序・拒否も同じ）。同じ入力で、構造 Validate 10.19 から 7.86 ms、配置だけの構築 4.53 から 2.52 ms。snapshot の全内容と 5 種の拒否が旧新で一致した。
- 回帰：焦点 51／51、EditMode 1185／1185。PlayMode は 206／207 で、旧融合方式の FusionLarge 試験の「B：old body lives on」が再現性ありで不通過（再実行 2 回とも失敗、前単位では通過）。原因は未特定で、停止した（終了試験と共存 Player は未実行）。

## 36. 回帰不通過の原因（旧 Body の正常な合流）、試験の補正、共存 Player ia3（2026-10-01、未コミット）
- 試験専用の Body 寿命の記録で、FusionLarge の B（両側 anchored）の経路を確認した。公開時は旧 Body が保持側（負側）に正しく残り、Collider 912 もすべて旧 Body に付いていた。その後、負側の群が建物の休止群（正側）へ予算で分けて合流し、判定の 1 frame 前に完了して、旧 Body を「合流」理由で破棄した。不正な破棄ではなく、試験の前提（判定時点まで旧 Body が生存）が frame 時間に依存していた。
- 試験を分けた。公開時の ID 保持（Collider がすべて旧 Body）と、合流後の所属・質量・回収（記録上の合流完了と合流理由の破棄、他の理由がないこと、群と Body の解放、Member が生きている群にいること、Collider と質量）。製品の挙動は変えていない。
- 回帰：焦点 51、EditMode 1185、PlayMode 207、終了 6 がすべて通過。共存 ia3：code 0、判定 ok 1,081・FAILED 0。
- Player の Validate 内訳：再構築あり・拡張なしの 643 frame で、索引 427・祖先 430・入力 196（契約 136）・全 Operation 40 ms。配置だけの 7,614 frame は 1,306 ms で、うち契約 1,004 ms。表示性能は未解決。

## 37. 反映済み境界の索引の再利用と共存 Player ic3（2026-10-01、未コミット）
- 表示の登録が持つ境界列を、内部の不変型 `VpReflectedSet` にした。私有の配列と索引を持ち、登録時と Commit の境界追加時にだけ作る。snapshot はその索引で所属判定し、構築後は参照を手放す。一般の List・配列は、従来どおり構築ごとに作り直す。
- 固定入力：3 方式（走査・作り直し・集合の索引）で、5 入力の snapshot・拒否がすべて一致した。List の同件数の書き換えは次の構築に反映され、構築後に参照は残らない。索引部分は 1.31 から 0.09 ms、集合の生成は登録 2,029 件で 3.4 ms（1 回だけ）。保持量は約 66 バイト／境界。
- 共存 ic3：code 0。構造構築の索引は生成 0・再利用 446,423。索引部分は p99 0.33・max 0.52 ms。構造 Validate の最大部分は祖先の走査になった（max 7.3 ms）。表示の Commit は最長 17.5 ms で、中身は未分解。

## 38. 祖先走査の削減、集合生成の計測補正、Commit の内訳、共存 Player ie3（2026-10-01、未コミット）
- 固定入力で走査を分けた：ledger の読み出しが約 51%、所属判定が約 45%、root の照合が約 4%。祖先は平均 14.3 回訪れていた。構築中だけの系譜配列（ledger の事実を一度だけ読む）と、所属判定を集合の並び順で先に照合する（合わないときはハッシュ）変更を採用した。登録ごとの所属判定・平面検査は省略しない。旧方式と全項目・拒否が一致し、祖先は 2.95 から 1.21 ms になった。
- 集合生成は入口から計時するよう補正した（§45 の 10.99 ms は配列確保を含まない値だった）。Commit は試行ごとに段階の内訳を記録する。
- 共存 ie3：code 0。祖先 p99 1.32・max 3.38 ms、Validate p99 2.34・max 7.93 ms。最長 Commit 28.6 ms は途中再切断区間での GPU 複製の拡張（集合生成ではない）。外側の容量拡張と配置更新は残件。

## 39. GPU 複製の初期容量 512 MiB、Commit の CSV 補正、共存 Player ig3（2026-10-01、未コミット）
- VB 16,777,216 × 16 B・IB 67,108,864 × 4 B（各 256 MiB）を、コード既定値と共有 profile に揃えた。CPU 側と拡張方式は変えていない。Commit の拡張段は「GPU 容量管理」（旧バッファの回収と再転送を含む）と呼ぶ。CSV の欄は RFC 4180 で引用する。ie3 の原本は保存し、補正版を別に作った（1 行補正、1,853 行すべて 14 列）。
- 共存 ig3：code 0。512 MiB を読み戻し、本編・区間とも VB／IB の拡張 0 回、回収待ち 0。使用範囲の最大は頂点 2.62 M・index 9.45 M。Commit の最大は本編 4.3 ms（集合生成が大半、原因未特定）、区間 0.31 ms。ie3 の区間の 28.6 ms は出なかった。

## 40. Collect の内訳と、反映済み境界の Operation の再読み出しの省略、共存 Player ii3（2026-10-01、未コミット）
- Collect は候補収集（`CollectInto`）が約 90%。各境界の Operation を、祖先を辿るときと候補を作るときに 2 回読んでいた。反映済みかを先に聞き、反映済みなら 2 回目を読まない（答えは同じ）。旧順序と全項目・拒否・候補数が一致し、固定入力で Collect は 4.25 から 3.42 ms、読み出しは半分になった。
- 共存 ii3：code 0。読み出しは連鎖の境界とほぼ同数。再構築・拡張なし・GC なしで、Collect p99 9.2・max 12.5 ms、snapshot p99 18.3・max 21.2 ms。残りは各枝が根まで辿る処理で、共通の祖先を読み直している（次の候補）。Player で最大値が下がったことは示せていない。

## 41. Validate の系譜を Collect と共有、共存 Player jj3 は code 14 で停止（2026-10-01、未コミット）
- `VpLineageFacts` が、1 回の構造構築の中だけで祖先の事実を共有する。Validate が開き、構造の部分の終わりに閉じる。`CollectInto` は引数で受け取ったときだけ使い、既定は直接読む。
- 旧経路と全出力・拒否・不足の種類が一致した。同じ snapshot を ledger の更新・別の ledger・拒否の後に使い続けても、新しく作った snapshot と一致した。
- 固定入力では、構造構築全体が 6.67 から 5.47 ms、Collect の読み出しが 30,965 から 0 になった。Validate は変わらない。
- 共存 jj3 は MobPlan の「再利用枠の個体が切られた」が FAILED。NPC root 39（ii3 は 53）、再利用の始まりは 8,759 frame で、再利用の個体への命中は 0。原因は調べておらず、修正・再実行もしていない。

## 42. 再利用 NPC の確認区間、共存 Player kk3 は code 0（2026-10-01、未コミット）
- 本編の後に区間を置いた。slot の使用履歴・世代・handle で再利用を確かめた生存個体を 1 体選び、現在の形状に合わせた小さい合成 Slash を、通常の `Evaluate` に 1 回だけ渡す。命中対象として成立、検出、受付と公開、その Operation の Commit を同じ個体について追う。
- 本編の自然な命中による再利用個体の切断は、件数だけ出力し、合否には使わない。
- kk3：r1053（世代 2、slot casual-f_2）の fragment 3610 に命中し、Operation 1778 が frame 10146 に Completed と Committed。本編の自然な命中は 9 件。jj3 の不合格は記録として残し、NPC の切断数が減った理由は分かっていない。

## 43. 登録 root から上の連鎖の区間を Validate から Collect へ、共存 Player ll3 は exit 14 で停止（2026-10-01、未コミット）
- Validate が辿って照合した、登録 root から上の連鎖（境界・反映済みか・受付順）を、同じ構造構築の中だけ Collect／Group の収集に継ぎ足す。旧経路と全出力・拒否・fallback が一致した。固定入力で構造構築全体が 6.32 から 4.51 ms（建物に似せた入力）、Validate は変わらない。
- ll3 は、再利用確認区間の合成 Slash が建物の hull にも当たり（hit 39、表示 Operation 127）、その完了を待たずに終了したため、建物の終了時の確認 4 件が FAILED になった。修正・再実行はしていない。

## 44. 再利用確認区間の完了待ち、共存 Player mm3 は code 0（2026-10-01、未コミット）
- 合成 Slash の 1 回の Evaluate の全命中（NPC・プロップ・建物）を区間の記録で追う。受付済み切断の Final と Commit（許容する失敗は別に数える）、建物の表示 Operation・落下・Hull 更新の残りがなくなるまで、実時間 30 s を上限に待つ（Hull 拒否は完了として扱う）。焦点試験で、NPC が先に Commit する場合、Hull 拒否、期限切れを確認した。
- mm3：4 段階と完了待ちが合格（命中 1 件）。Collect の区間共存（§43）も通過。拡張なし・GC なしで Collect の最大 1.74 ms（訪問 0）、snapshot の最大 13.1 ms。ll3 の不合格は記録として残す。

## 45. 完了待ちの合否の補正（2026-10-01、未コミット）
- 「処理が終わった」と「許容できる結果だった」を分けた。
  - 許容する失敗は、失敗記録（driver の失敗、geometry の FailureOf）が名指しする場合だけにした。
  - 即時の異常回答（InvalidRequest・Aborted・Stale）は不合格にし、残る Operation は回収まで追う。
  - 待機中の World 終了、建物 hit の Abandoned、追跡対象の消失は正常完了にしない。
- 焦点試験 9／9、回帰はすべて通過。追加の Player は実行していない。

## 46. 通常拒否が残す Operation の追跡（2026-10-01、未コミット）
- 通常拒否でも、残った Operation（AnchorsRefused が残す active Operation など）は終端まで追う。拒否そのものは異常にしないが、期限内に終わらなければ不合格にする。焦点試験で、早期に合格しないこと、期限切れになること、World の通常終了で回収されることを確認した。回帰はすべて通過した。

## 47. Place の内訳（固定入力、2026-10-01、未コミット）
- render fragment 2,029 の配置更新だけの Place（1.6〜2.2 ms）では、配置の問い合わせが約 7 割（1.2〜1.4 ms）。そのうち `IsPlacement` を同じ行列に 2 回かける処理が 0.78 ms（1 回なら 0.41 ms）、供給元（Transform の読み出し）は 0.18〜0.20 ms。
- cap がある入力では、配置が動くと section の探索と作成が最大（比べる項目数はおよそ 2 乗で増える）。
- 改善対象には `IsPlacement` の重複を選んだ。Player の最大 9.04 ms の原因は断定していない。

## 48. 配置の検査の重複を削除、Place の計数を Player へ、共存 Player qq3 は code 0（2026-10-01、未コミット）
- `TryPlacementOf` の 2 回目の `IsPlacement` を削除した。旧経路と全出力・拒否・供給元を呼ぶ回数が一致した。固定入力（2,029 render fragment）で Place が 0.39〜0.56 ms 下がった。
- frames.csv に、構造構築と配置更新だけの Place の計数を分けて出力するようにした。
- qq3：配置更新だけ・GC なしで、Place は p99 1.31・最大 4.08 ms。最大の frame は section・cap なしで、render fragment あたり 3〜4 µs（中央値 0.64 µs）。件数では説明できず、原因は特定していない。

## 49. 製品の供給元を含めた Place の切り分け、診断 Player rs3（2026-10-01、未コミット）
- 診断用の切替で、Place を「問い合わせ／検査／残り」の 3 区間で計時した（結果は通常の pass と同じ）。
- 配置更新だけの frame の通常時：render fragment あたり 0.62 µs、うち製品の供給元を含む問い合わせ 0.40、検査 0.11、残り 0.10。
- 突出した frame（38／6,741）では、超過が入る区間が frame ごとに違い（問い合わせ・検査・残り）、件数は通常の frame と同程度。原因は特定していない。
- 再利用確認区間では、合成 Slash が建物にも当たり、完了待ちが建物の Commit まで待って合格した（Player で建物を待つ経路が通った）。

## 50. 配置供給元の通常時の費用（固定入力、2026-10-01、未コミット）
- display-only owner 2,029 個で、`PhysicsOwnerPlacementLookup` 1 回は 0.28〜0.29 µs（Editor）。内訳は、owner の読み取り関数 0.234（`Root.transform` 0.042、`localToWorldMatrix` 0.067、対応との行列積 0.090）、対応表 2 つ 0.024、lookup 側の確認 0.015。読み取り関数で同じ確認を繰り返す分は約 0.015（5%）。
- 改善対象には、結果が変わらず削減幅が最大の `Root.transform` の保持を選んだ。重複確認の整理（FusedSide の経路は残す）は次の候補。行列積の省略は、bit 単位で一致しないので選ばない。

## 51. owner の Root の Transform 参照の保持、共存 Player tt3 は code 0（2026-10-01、未コミット）
- `PhysicsFragmentOwner` が、Root の Transform 参照を 3 つのコンストラクタで保持し、Release で外す。world 行列は読み出しのたびに読み、確認も変えていない。
- 新旧の読み取り・行列・snapshot が bit 単位で一致した（移動、回転、縮尺、付け替え、withdraw、外部破棄、解放）。固定入力で lookup が 1 owner あたり 0.056 µs 減った。
- tt3：配置更新だけ・GC なしで、Place は p99 1.00 ms、最大 8.35 ms。突出の原因は未特定。

## 52. 常時 Kinematic の建物の切断停止（N）と移動距離 D(n)、共存 Player lm3 は code 0 だが途中再切断が未行使のため不合格（2026-10-01、未コミット）
- N=16・p=1・D₀=0.5 m。試験は焦点 72・EditMode 1214・PlayMode 227・終了 6 が全通過。
- Player：建物 1 が n 1→2→4→7→11→16 で停止し、停止後の掃引で切断なし。ただし head wait に置いた途中再切断区間は、`PlayableCity` が台本開始まで偽のため実行されず、未行使を失敗にする判定もなかった。修正案は TL 判断待ち（`PlayableCity/RESULTS-fusion.md` §58）。

## 53. 途中再切断の必須区間化、md5 の 2 件の切り分け、共存 Player ms3 は code 0（2026-10-01、未コミット）
- 途中再切断は head wait 内の必須区間になった。表示子が実際に動いたのを確認してから 2 本目を渡し、Player では 0.191 の位置で停止した。閾値は n 1→2→4→5→7→11→16 で停止し、停止後の掃引で切断はなかった。
- md5 の t = 0 は試験の物理時計によるもの（Step が進まない frame）。withdrawal の失敗は、初回評価の費用で Main 予算を使い切った Pending によるもので、仕様どおり。詳細は `PlayableCity/RESULTS-fusion.md` §60。
