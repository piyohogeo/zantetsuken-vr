# Phase 4.1 Main切断処理の追加最適化と実行順の調査 — 2026-09-24

## 対象と結論

前回の変更を取り込んだ `b19ce67a` を基準に、別worktree `C:\Users\junic\src\zantetsuken-vr-main-thread-order-20260924`、ブランチ `phase41-main-thread-order-20260924` で作業した。元repoの作業ファイルは実験に使っていない。descriptor→登録slotによるO(1)の重複検査、Provisional metadata view、Colliderの具象Listアクセス、箱corner距離の共有は既存基準であり、今回の効果へ再計上しない。

実装と回帰試験は `3cd2349` に集約し、後続の1コミットにレポート・再実行ハーネス・測定証拠をまとめた。

今回の製品変更は、軸平面でのProvisional箱質量計算と、同じ寿命のnative管理配列4本の1本への統合。描画やworkerの高速化は成果に含めない。実行順の反転は診断専用で、製品の構築・公開順序は維持した。

同じIL2CPPバイナリを独立に4回起動した局所測定では、質量計算が約56–58%、native管理配列の確保・解放が約74–75%短縮した。絶対量はそれぞれ約0.31–0.33 µs、約0.41–0.42 µs/操作で、全切断の短縮量を測った結果ではない。Objectsの非対称性は構築順へ追従したが、順序変更でBuild＋Publishの合計が一貫して下がる証拠は得られなかった。

## 製品変更

### 軸平面の箱質量計算

`ProvisionalBoxMass.TryDivide` は、owner frameへ変換した平面の法線で2成分が厳密に0なら、箱の2つの直方体を直接積分する。一般平面は従来の6 tetrahedron分割を使う。近い軸への丸めやepsilonによる近似は導入しない。箱の取得、平面変換、負質量=親−正、float質量の成立条件、箱全体を使う慣性・元actorの慣性へのfallbackは共通経路に残す。

面上・箱外・退化・相対幅が `64 × double epsilon` 以下のsliceは従来経路へ戻す。演算順による端数の差でfloatへの変換時の成立・fallbackが反転しないよう、極端な親質量・箱慣性も従来経路へ戻す。これは高速経路の適用条件であって、新しい入力制限やclampではない。全入力でのbit一致は主張しない。

具体的には親質量 `1e-30..float.MaxValue`、親全質量での箱慣性の各成分 `1e-15..1e30` を高速経路の範囲とする。慣性は従来と同じfloatの `hi-lo` をdoubleへ昇格して評価し、幅条件と合わせて両側をfloatの0/overflow境界から離す。レビューでは半寸法 `2^64`、plane `(0,0,1,-2^62)`、親質量 `3.99999988079071` で、旧volume比の最後のbitが慣性fallbackを決める反例を見つけ、回帰試験に加えた。

DESIGN §7.2が要求する体積比・clipした箱の重心と整合する。6 tetrahedronへ分ける方法自体は仕様の要求ではなく、この入力では不要な一般処理である。

### mesh作業配列の統合

`PhysicsCutCook` / `PhysicsCutJobs` の `ids / bounds / vertexCounts / bakeDone` を `NativeArray<PhysicsCutMeshSlot>` に集約した。Mainで1切断あたりnative確保3回・解放3回を削減する。cut→Main適用→Bakeの順序、Bakeが自分のslotだけを更新すること、未帰還を表す0、回収前の解放禁止、失敗・取消時の解放を維持する。raw pointer aliasや安全性検査の無効化は使わない。

代償として配列本体は1 meshあたり33→36 B、clearするpayloadは1→36 Bとなる。箱2 meshで66→72 B / clear 2→72 B、Character10 meshで330→360 B / clear 10→360 B。workerのstrideとstruct転記も変わるが、その速度は今回の成果に数えない。局所測定は、このclear増分を含む確保・解放を比較する。

## 測定方法

Unity 6000.3.22f1、Windows x64。箱は半寸法0.25、plane `(0,1,0,0)`。Characterはhandoffのm_8（19 convex / 608 vertices）、plane `(0,0,1,-0.9)`、Provisional正15/負9 Collider。親質量12、source inertia `(4,4,4)`。旧実装は `b19ce67a` から取り出して同じ実行バイナリ内へ別名で組み込み、同じ入力で比較する。

- 質量計算: Mainの `TryDivide` 1回あたり。各版1024回のwarmup後、1024回batchをABBAまたはBAABで8 round。1版16 batchの中央値をprocess別に求める。
- native管理: 製品と同じ `PhysicsCutBlocks.Take` 3本＋clear byte配列と、clear slot配列1本の確保・IsCreated/Disposeを比較。各1024切断batch、8 round。report/arena/Mesh/worker/bakeは対象外。empty batchは別保存し、差し引かない。実製品では数frameにまたがる寿命を、このベンチは即時解放で測る。
- 実行順: 新しいsourceとpairについてMainの `TryBuild` / `TryPublish` を直接呼ぶ。各case・各modeでwarmup1対、測定8対。4 modeの位置はLatin squareで均等化し、逆列順・case順も別processで反転する。

時間はMainで呼び出した区間のStopwatch経過時間。worker待ちや描画を加算せず、外れ値を除外しない。batch平均は単発の計測精度ではない。前回のEditor単独値とIL2CPP値を同じ母集団として混ぜない。差分の合計を全Requestの短縮量とすることもできない。

順序診断では `QueryThreadCycleTime` も併記する。これは当該threadのuser/kernel cycleであり、時刻やCPU時間への変換は行わない。CPUの周波数条件等により、cycleをそのまま時間に変換できないことは[Microsoftの仕様](https://learn.microsoft.com/en-us/windows/win32/api/realtimeapiset/nf-realtimeapiset-querythreadcycletime)に従う。空のprobeの費用も保存し、一律に引かない。

### IL2CPPの結果

TestRunnerのWindows x64 **Development Player**、Direct3D11、640×400 Windowed、Quality=PC、MSAA=4、VSync=0、targetFrameRate=-1。Main managed thread 1、Stopwatch 10 MHz。通常のFixedUpdateを用い、強制GC・Physics.Simulate・affinity/priority変更はしていない。Playerの `ENABLE_UNITY_COLLECTIONS_CHECKS=false`、Editor回帰ではtrue。既存のビルド構成であり、この最適化のために安全性設定を変更したものではない。

起動順は `player00 → player11 → player10 → player01`。最初の数字はABBA/BAABの反転、次は箱/Characterの先後。各起動のexe・GameAssembly・UnityPlayer・global-metadataのhash一致を確認した。GameAssemblyは `33f35b6e49f95a0924abe488bb1754ad4ff883cd01e61bc229ebdbc4bd20216a`。buildの自動起動と初期Editor smokeは別保存し、この表へ混ぜない。

表は各processのbatch中央値を求めた後の、4 process中央値の中央値。括弧は4 process中央値の最小–最大。µs/操作。すべてのprocess内の同round対差も正の短縮となった。

| Main上の操作 | 基準 b19ce67a | 今回 | 中央値の短縮率 |
|---|---:|---:|---:|
| 箱の質量計算 | 0.5527 (0.5469–0.5555) | 0.2404 (0.2398–0.2420) | 56.5% |
| Character箱の質量計算 | 0.5668 (0.5643–0.5681) | 0.2403 (0.2398–0.2419) | 57.6% |
| 箱2 meshの管理配列確保＋解放 | 0.5565 (0.5549–0.5580) | 0.1373 (0.1358–0.1379) | 75.3% |
| Character10 meshの管理配列確保＋解放 | 0.5550 (0.5547–0.5582) | 0.1424 (0.1416–0.1435) | 74.3% |

同round差のprocess中央値の範囲は、質量計算で箱0.3057–0.3175 / Character0.3234–0.3339 µs、native管理で箱0.4158–0.4207 / Character0.4116–0.4162 µs。小さい固定費削減だが、ABBA/BAABとcase順を変えて再現した。[各processの結果](phase41-main-thread-order-data-2026-09-24/summary-micro/summary.md)・[全roundと校正値](phase41-main-thread-order-data-2026-09-24/summary-micro/summary.json)を保存した。

斜め平面では質量高速経路に入らず、その短縮率は適用できない。管理配列ベンチはMainの確保・解放のみであり、slotへの転記、スケジュール費用、数frame保持、他のnative配列やMeshの確保を含むCook全体のA/Bではない。大きなRequest時間の残差をこの2変更で解消したとは主張しない。

## 実行順と非対称性

| mode | BuildSide | SetActive |
|---|---|---|
| 0（製品の順序） | 正→負 | 負→正 |
| 1 | 負→正 | 負→正 |
| 2 | 正→負 | 正→負 |
| 3 | 負→正 | 正→負 |

jointはどのmodeでも正側にあり、負側へ接続する。活性化の反転は「接続先のbodyが既にactiveか」も変えるため、単純な呼出し順だけの介入ではない。呼出し間にframeを挟まない。公開後のCollider件数・質量・慣性・joint接続・active状態、および通常のFixedUpdate後の有限性を検証してから片付ける。

Objects区間はroot生成、shapeFrame生成・配置、Rigidbody追加、managed `PhysicsOwnerSide` 構築を含む。内側のnewRoot区間は最初の `new GameObject` だけ。古いhandoffのObjects区間と境界が完全に同じとは扱わない。正負の費用に加え、同じsample内で2側を加算した値を集計する。中央値同士を足してpair中央値としない。

局所fixtureはhelperのauthored mesh/bankをcase内で共有し、sourceはkinematic・gravityなし、anchorとseparation impulseなし。CutWorld全体、連続16体切断、full Request、Final handoffの性能測定ではない。Final handoffは別の実動作テストで検証する。

### 確認できた非対称性

ObjectsとnewRootは、4 process×2 case×activation順を固定した2比較の **16/16比較すべて** で、構築反転に伴って正負の費用差の符号が反転した。elapsedとthread cyclesの両方で再現した。正側固有の処理だけで説明することはできない。

| 区間 | 箱: 先側−後側 µs | Character: 先側−後側 µs |
|---|---:|---:|
| Objects | 8.30–10.30 | 8.80–10.80 |
| 内側のnewRoot | 2.05–3.05 | 2.40–3.25 |

各run・modeの8 sampleの差の中央値の範囲。Objects差のcyclesは箱17.52–21.76 kcycles、Character18.61–22.84 kcycles。単なる経過時間上の割込みだけでは説明しにくいが、内部原因は未確定。newRoot以外の残部にも約6–8 µsの差が残るため、root生成だけを原因とはしない。

SetActiveは別の傾向だった。activationを反転しても正負差は16/16比較で反転せず、常にjointを持つ正側が高い。mode0→2の4 process中央値の中央値は次のとおり。

| case | SetActive 正側 µs | 負側 µs | 同sampleの2側合計 µs |
|---|---:|---:|---:|
| 箱 | 13.55→19.13 | 9.33→3.15 | 22.75→22.30 |
| Character | 26.90→33.65 | 17.98→11.23 | 45.20→44.90 |

費用の配分は変わるが、合計の差は小さい。jointの所属・接続先のactive状態とCharacterのCollider件数差を含むため、Objectsと同じ「最初の呼出しが高い」現象とは扱わない。

Build＋Publish合計の同round差（変更mode−mode0）は、次のようにprocessによって正負が混在した。表は各processの8対差中央値の範囲、負が短縮。cycle差にも同様の不一致がある。

| 変更 | 箱 µs | Character µs |
|---|---:|---:|
| mode1: 構築だけ反転 | −0.65〜＋4.35 | −3.45〜＋4.85 |
| mode2: 活性化だけ反転 | −1.05〜＋2.65 | −9.10〜＋4.15 |
| mode3: 両方反転 | −3.15〜＋0.60 | −0.70〜＋2.15 |

製品順序mode0の合計中央値自体も箱78.95–87.85 µs / Character124.30–132.70 µsのprocess間幅がある。順序変更を高速化として採用せず、元の順序を残した。空probeの平均は0.0163–0.0230 µs / 388.3–395.0 cycles（異なる計測境界なので時間換算しない）。入れ子probeの費用は包含区間に入り、値から一括減算していない。詳細とrawからの独立再計算は [順序調査ノート](../../Tools/MainThreadOrder20260924/order-findings.md) にある。

## 検証・再現・未採用の候補

診断パッチは `Tools/MainThreadOrder20260924/run_diagnostics.py` のinstall/uninstallで退避・復元する。Build/Publicationに追加した順序分岐・probe、テスト用Assets、runInBackground等の設定は成果物へ残さない。ハーネス・集計コードはToolsへ、生CSV・環境・実行metadataは本レポートに対応する証拠ディレクトリへ保存する。大きいPlayerバイナリ・Library・全ログはworktreeのLogsに保持し、gitへは入れない。

最終の製品コードで **EditMode 314/314、PlayMode 41/41** が通った。前回基準288件へ、軸平面・慣性境界25件と実際のParallel Bake metadata保持1件を追加した。PlayModeは通常39件と意図的terminationの2件を別プロセスで実行した。IL2CPP最終buildの自動起動と4回の単独起動も各4/4（質量比較、管理配列比較、順序診断、実際のCook→Final handoff）を通過した。

通常回帰・4 Playerのログに `Leak Detected` はない。既存のtermination fixtureは意図的にPersistentを残して終了するため、単独起動で20件/18件の診断が出る。終了不能のworkerへ強制解放する変更はしていない。[ログのhashと診断](phase41-main-thread-order-data-2026-09-24/log-checks.json)、[最終回帰](phase41-main-thread-order-data-2026-09-24/regression2/editmode/summary.json)を保存した。

復元後の全PhysicsCut runtimeソースは、最終回帰時のhashに一致。4 Playerは最終buildと同じソース・バイナリであることを照合した。初期smokeや境界補強前buildもrawに残し、最終結果と区別した。[provenance](phase41-main-thread-order-data-2026-09-24/provenance.json)・[ファイルhash一覧](phase41-main-thread-order-data-2026-09-24/sha256.json)・[再実行手順](../../Tools/MainThreadOrder20260924/README.md)を参照。

元repoのtracked＋非ignored untracked **1602/1602ファイル** が開始時のSHA-256と一致し、追加・削除・変更なし、status一致、main HEADは `b19ce67a` のまま。[照合記録](phase41-main-thread-order-data-2026-09-24/original-verification.json)。共有 `.git` のworktree・branch・commit管理更新は発生する。元mainへのmergeは行っていない。

アルゴリズム仕様まで含む次候補は2つある。内部の連続Build→Publish専用経路ならReposition×2の重複を減らせる可能性があるが、公開時にsourceの最新運動を反映する契約、D6 anchor、失敗時の挙動を別途検証する必要がある。DESIGN §7.6の全頂点距離計算も、更新時に検証した不変boundsで一側所属を証明できれば省略可能だが、現bankは変更可能であり、非finiteやfloat演算のoverflowの拒否も現在の走査が担っている。現状へ無条件にbounds短絡を加える変更は採用していない。詳細は [設計レビュー](../../Tools/MainThreadOrder20260924/design-review.md) を参照。
