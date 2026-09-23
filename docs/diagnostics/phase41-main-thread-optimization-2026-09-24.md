# Phase 4.1 Main切断処理の再調査と実装 — 2026-09-24

## 結論

更新後の `8bae0b1e1c7f530a19a91f00603404d291170980` を基準に、新しいworktreeで再調査・実装・測定した。主な改善は、Geometryの重複登録判定のO(1)化と、Provisional shapeが元shapeの不変なmanagedメタデータを共有する変更である。

測定対象はMain上の切断関連処理だけ。描画時間・workerの速度改善は成果に含めていない。今回の簡易測定は **Unity Editor Monoの局所的なStopwatch経過時間** であり、IL2CPP Playerの切断全体やMainのCPU占有時間を直接測ったものではない。引き継ぎ文書の「Unity APIの積み上げでは説明できない時間」の原因を解明したとは結論しない。

## 基準と作業場所

- 元repo: `C:\Users\junic\src\zantetsuken-vr`、main `8bae0b1e`。
- 今回のworktree: `C:\Users\junic\src\zantetsuken-vr-main-thread-20260924`。
- ブランチ: `phase41-main-thread-20260924`。
- 実装＋回帰試験は `1a50dbb` に集約。後続の1コミットに本レポート・raw測定値・再実行ハーネスをまとめた。元mainへのmergeは実行していない。
- 前回提案の台帳容量の幾何増加、Geometry登録のhigh-waterまでの走査は、更新後mainに既に入っている。shapeのper-convex List容量予約、productsの正負part List容量予約、fixture終了確認の修正も取り込み済み。これらを今回の改善量に再計上しない。
- 元repoの作業ファイルは実験に使わず、開始時にtrackedおよび非ignored untrackedの1552ファイルのSHA-256とstatusを保存した。Gitのworktree・branch・commit管理に伴う共有`.git`の更新は発生する。
- 完了時の照合で1552/1552ファイルが一致。追加・削除・内容変更なし、git statusも一致、元mainのHEADも `8bae0b1e` のまま。
- 9月23日の最適化handoffと9月24日のnative leak handoffを参照した。後者よりmainはさらに進んでいるため、現行コードと実行結果を優先した。

## 実装

### 1. descriptor → Geometry slotによる重複登録判定

`VpGeometryReferenceTable` にdescriptor容量と同じ長さの `int[]` を1本追加。未登録は0、登録済みはgeometry slot+1を保持する。登録と正常retireで更新し、lookupでは登録先のindex generationと照合する。Dictionary、別オブジェクト、切断ごとの割当は追加していない。

従来のhigh-waterまでの走査を、使用履歴・生存数によらないO(1)にした。追加常駐メモリはdescriptor容量×4 Bと配列ヘッダ。handoffの2048 descriptor設定なら配列本体8 KiB、今回の登録単体ベンチの4096 descriptor設定なら16 KiB。

`CanRegister` がstorage所属・descriptor範囲・現在世代・Publishedを検証した後だけlookupする。外部からindexをretireしてdescriptorを再利用した場合、新世代のmappingを古いGeometry tokenが消せないこと、lease返却前のretireとslot再利用を追加試験した。constructorでstorageをclaimする前に配列を確保する保証、atomicなGeometry＋display instance登録、世代枯渇の拒否を維持した。

**O(1)化したのは重複登録検査だけ。** 空きGeometry slot / display instance slotの探索、storage入力検証、Geometry Commit全体までO(1)になったわけではない。Renderingフォルダの変更だが、切断後のGeometry登録経路のMain固定費を対象としている。

### 2. Provisional shapeのメタデータ共有

`PhysicsOwnerShape.ProvisionalSide` は、元shapeのconvex range / bank / bank owner / bounds / mesh一覧を毎回別配列・Listへ詰め直していた。Provisionalはその選択viewとし、convex index対応だけを持つ。nested viewのindexは平坦化する。`ProvisionalOwnerBuilder.TryBuild` では既存の分類検証走査で正負件数を数え、exact sizeのindex配列を一度作り、その所有権を内部入口へ渡す。

元shape自体をwork holdで延命する方式は採らず、Mesh sourceとultimate bank ownerへの直接holdを維持した。元shapeや中間viewのDispose後も選択側が生存でき、未使用native資源の回収時期・holdカウンターは従来どおり。途中失敗のDisposeとrollbackも残した。

ソース上の割当数は、public `ProvisionalSide` で正負を作る対で14件減り、通常の `TryBuild` 1切断では一時Listも含め18件減る。これは **割当サイトの差分** であり、実測GC bytesではない。今回のEditorでは既知4096 B配列への `GC.GetAllocatedBytesForCurrentThread` の応答が0だったため、GC bytesは測定不能として扱った。

代償は、viewの生存中、選択外も含む元shapeのmanaged table参照を保持すること。nativeのhold対象は増やさない。通常shapeのgetterにもview判定が1つ入る。public `Meshes` はviewでのみ遅延生成の `IReadOnlyList` adapterとなる。型をListへcastする契約や範囲外indexの例外型を固定する契約はないが、実体型とその例外型は変わる。

### 3. Collider内部ループの具象Listアクセス

`PhysicsOwnerBuild` のFinal Collider準備とAdoptの内部ループは、内部の同じ `List<MeshCollider>` を直接使う。publicの `IReadOnlyList` は維持する。Count/indexごとのinterface dispatchを避け、Unity API呼出し、流用検証、保持flag、切替順序は変えない。

保存済みIL2CPP生成コード上の呼出し数分析では、箱14件、Character118件/cutのinterfaceアクセスを除ける。ただしこれは今回の採用コードをIL2CPP buildして測った時間ではない。Unity APIが支配的な区間で、簡易測定から単独の時間効果は確定できなかった。

### 4. 箱質量計算の共有corner距離

`ProvisionalBoxMass` の6 tetrahedron × 正負2側で重複していたplane距離を、箱の8 cornerについて一度だけ求める。dot＋plane.wは48回から8回となる。stack使用量は64 B増える。正負の符号反転、厳密な `> 0` 判定、交点補間、体積・一次momentの加算順序は維持する。

軸方向3ケースについて、正負normal・中央・面近傍の分割を独立なslab体積/重心と比較する試験を追加した。既存の斜め平面・fallback・質量契約の試験も通している。

## 簡易測定

### 条件

Unity `6000.3.22f1`、WindowsのEditor Mono、Main managed thread ID 1、Stopwatch 10 MHz。各版を別Unityプロセスで起動し、順番は `baseline → o1 → view → colliders → mass → all → all → mass → colliders → view → o1 → baseline`。全12起動、5760行を採用し、外れ値除外はしなかった。

入力はhandoffと同じ箱（半寸法0.25、plane `(0,1,0,0)`）と実intake Character m_8（19 convex / 608 vertices、plane `(0,0,1,-0.9)`）。CharacterのProvisional正15/負9、Finalで保持14/新規10、箱の保持0/新規2を測定時assertした。parent mass=12、inertia=(4,4,4)、separationなし。

- shape対の生成＋Dispose: warmup 1 batch後、256反復平均×8 sample。
- 箱質量分割: warmup 1 batch後、1024反復平均×8 sample。
- Provisional build / 正負Collider準備 / 正負Adopt: warmup 4対後、それぞれ単発64 sample。各対を新しく構築する。
- 重複登録検査: warmup 1 batch後、4096 lookup平均×8 sample。private `IsRegistered` へのdelegate作成は計測外。

各runの中央値を求め、表は2 run中央値の中央値。括弧は2 run中央値の最小–最大。単位はµs/操作。API区間内の割込み・Ready・GCを排除したCPU時間ではなく、QPCの100 ns目盛りが100 nsの計測精度を保証するわけでもない。

actorは非活性。測定中に描画しない。cookは事前に完了させ、worker実行は測定外。collider Adopt内のEditMode `DestroyImmediate` は実Playerの遅延Destroyと違う。公開・SetActive・Final handoff全体やcut全体は測っていない。build、shape生成、質量分割は包含関係にあるため、短縮量を足さない。

### 個別変更

| 変更・対象操作 | baseline µs | 単独変更 µs | 差分 | 判断 |
|---|---:|---:|---:|---|
| O(1)化: live16 未登録lookup | 0.040662 (0.020886–0.060437) | 0.001782 (0.001709–0.001855) | −95.6% | 局所短縮。絶対量は約0.039 µs |
| O(1)化: live32 未登録lookup | 0.058875 (0.049231–0.068518) | 0.001776 (0.001709–0.001843) | −97.0% | 局所短縮 |
| O(1)化: 使用履歴2048/live16 未登録lookup | 2.009381 (1.941138–2.077625) | 0.001807 (0.001709–0.001904) | −99.91% | stress条件。通常16体へこの量を適用しない |
| shape view: 箱の正負生成＋Dispose | 1.921582 (1.753516–2.089648) | 1.226074 (1.183984–1.268164) | −36.2% | 局所短縮 |
| shape view: Characterの正負生成＋Dispose | 5.960352 (5.449219–6.471484) | 3.766211 (3.142188–4.390234) | −36.8% | 局所短縮 |
| shape view: 箱のProvisional build | 40.050 (35.950–44.150) | 36.525 (34.750–38.300) | −8.8% | run範囲が重なる。親区間の効果は未確定 |
| shape view: CharacterのProvisional build | 90.200 (73.500–106.900) | 78.275 (69.300–87.250) | −13.2% | run範囲が重なる。親区間の効果は未確定 |
| Collider List: 箱の準備 / Adopt | 4.900 / 2.950 | 4.400 / 2.650 | −10.2% / −10.2% | baseline変動24.5% / 16.9%。効果未確定 |
| Collider List: Characterの準備 / Adopt | 23.200 / 12.900 | 18.925 / 10.400 | −18.4% / −19.4% | baseline変動41.4% / 38.8%。効果未確定 |
| corner距離共有: 箱の質量分割 | 4.140186 (3.861230–4.419141) | 3.922388 (3.798486–4.046289) | −5.3% | 別process比較では効果未確定 |
| corner距離共有: Character箱の質量分割 | 4.230078 (3.841211–4.618945) | 4.413232 (4.330371–4.496094) | ＋4.3% | 短縮を主張できない。追加比較は後述 |

baseline変動は `(run中央値の最大−最小) / 2 run中央値の中央値`。登録のlate duplicate/live512を含む全結果・run別p95・比較対象外区間は保存したCSV/集計に残す。数nsのlookup値はbatch平均であり、1呼出しをその精度で測定した値ではない。

### 全変更

| 対象 | baseline µs | all µs | 観測差分 |
|---|---:|---:|---:|
| 箱 Provisional build | 40.050 (35.950–44.150) | 35.275 (34.000–36.550) | −4.775 µs / −11.9% |
| Character Provisional build | 90.200 (73.500–106.900) | 70.725 (69.300–72.150) | −19.475 µs / −21.6% |
| 箱 shape対生成＋Dispose | 1.921582 (1.753516–2.089648) | 1.955469 (1.449805–2.461133) | ＋1.8% |
| Character shape対生成＋Dispose | 5.960352 (5.449219–6.471484) | 3.326074 (3.127539–3.524609) | −44.2% |

統合版で箱shape対が一貫して速いとは観測できなかったことも残す。独立2起動ずつの記述比較であり、上記build差をPlayerの改善率や因果効果として保証しない。

### 箱質量の追加比較と採否

別process比較で符号が揺れたため、基準版の `ProvisionalBoxMass` を型名だけ変えてtest assemblyに置き、現実装と同じshape入力でABBA順（旧・新・新・旧）に比較した。各version 1024回のwarmup後、1024回/batch × 4 batch/round × 8 round/case、独立2 process、計128行。全出力のmass・COM・慣性・慣性回転・plane normalとboundsは測定前後とも **bit一致** を確認した。

各roundの旧2 batch平均と新2 batch平均の差を求め、その8 round差の中央値をrun代表値とした。表は2 run代表値の中央値で、前の別process比較とも、各versionの中央値の差とも混ぜない。

| 対象 | paired差 µs | 各runのpaired差 µs | round比率の中央値 |
|---|---:|---:|---:|
| 箱 | −0.105823 | −0.182446 / −0.029199 | −2.29% |
| Characterの箱 | −0.028345 | −0.031714 / −0.024976 | −0.63% |

代表値はごく小さい短縮側だが、roundごとの増減が混在する。Characterのrun1は8 round中4件だけが短縮側で、安定した効果は確定しない。式の共有は小規模で結果も維持できるため採用したが、主要な解決策とは扱わない。Collider List化は時間効果未確定のまま、既存storageを使う単純な内部アクセス変更として採用した。O(1)化とshape viewを今回の主要な成果とする。

## DESIGNのアルゴリズム指定について

実装メモを必須仕様として扱わず、観測可能な振る舞いと資源寿命を基準に判断した。今回はDESIGN本文を書き換えていない。

- §7.2のProvisional質量は箱体積比・近似重心・慣性fallbackを要求するが、6 tetrahedron分割という手段は要求しない。今回の距離共有はそのまま導入できる。さらに軸平行planeを長さ比と区間中央の閉形式で処理する余地がある。今回両シナリオのplaneは該当するが、未実装・未測定。一方の小断片を全体からの差で求める無条件簡略化は桁落ちの懸念がある。
- §7.6付近の各頂点のdistance走査は、保守的boundsで完全に一側だと証明できるconvexについて省略する余地がある。既存入力分析ではCharacter608頂点中448頂点が候補。ただし現在のnative bankは変更可能で、古いboundsや非finite頂点を従来の走査が検出している。検証済み入力の不変性を規定するか、更新時にbounds/検証状態を維持しないまま省略すると契約が変わる。時間効果は未測定。
- 初期速度継承のanchor経由2段式は `v_child = v_source + omega × (COM_child − COM_source)` に約分でき、crossが2回から1回となる。浮動小数点の演算順の差とfinite検査を確認する必要がある。小規模・未実装・未測定。
- buildとpublicationの姿勢・速度再取得/再配置は、連続する内部入口でのみ省ける可能性がある。分離された公開APIではbuild後のsource移動を反映する必要があるため、単純削除はしない。
- Stale検証、失敗時rollback、native/Mesh hold、終了未確認時の資源保持をまとめて削る根拠はない。正常パスの例外処理が大きな占有時間の原因だという測定証拠も得ていない。

## 回帰試験・証拠・再実行

関連EditMode **288/288 passed**（PhysicsCut、VpGeometryReferenceTable、descriptor追加試験、LogicalCutLedger）。追加したのはdescriptor再利用2件、shape viewの寿命・nested view・入力snapshot・products所有権等6件、箱質量の軸方向3件。独立した読取レビューでもshapeの所有権とdescriptor mappingの破綻は見つからなかった。

切断系PlayModeは **39＋1＋1＝41/41 passed**、各process exit 0、inconclusive/skipped 0。最初にnamespace一括で走らせた際は34 passed / 7 inconclusive / exit 2だった。既存 `CutWorldRootPlayModeTests` が意図的なtermination後に資源を保持し、後続caseのSetUpを拒否するため。基準版でも34/7/exit 2を再現した。終了要求の2試験をそれぞれ独立processに分離し、残り39件を一緒に実行した。ガードの解除や資源の強制回収で試験を通したわけではない。

EditMode、通常PlayMode39件、比較12起動、追加比較2起動には `Leak Detected` 警告なし。terminationの独立2試験はPersistent 23 / 21 allocation警告を残す。namespace一括の23警告は基準版でも再現した。これらは終了後回収を約束しない既存試験であり、今回は警告を抑制していない。全allocationの個別帰属までは確認していないので、プロジェクト全体のleak-freeを主張しない。

証拠は [phase41-main-thread-20260924](phase41-main-thread-20260924/) に同梱した。

- [全測定集計](phase41-main-thread-20260924/report-summary.md)、[集計CSV](phase41-main-thread-20260924/flat-summary.csv)、`comparison/run*/{physics,registration}.csv`: 全5760行。
- [同一process質量比較](phase41-main-thread-20260924/paired-mass/paired-summary.md)、同フォルダのraw CSVとJSON: 全128行。
- [測定条件とsource/input SHA-256](phase41-main-thread-20260924/runs.json)、[回帰試験結果とコマンド](phase41-main-thread-20260924/regression-results.json)、[元repoの照合結果](phase41-main-thread-20260924/original-files-verification.json)。
- Unity生ログ・NUnit XML・実験時の基準/候補source保存は、このworktreeの `Logs/MainThreadOptimization20260924/`。大容量のLibrary、ログ、Editor生成設定はcommitに含めない。

再実行用は [Tools/MainThreadOptimization20260924](../../Tools/MainThreadOptimization20260924/) にある。Unityパス・基準commit・専用worktree名を固定した実験runnerで、baseが変わった際は比較対象と試験inventoryを見直す。実行中の同worktreeをEditorや別実験で使わない。実験の間だけtest assemblyへハーネスを配置し、Unityが終了してから版を差し替え、終了時に候補sourceを復元する。専用worktree外の製品ファイルや既存診断を変更しない。

```powershell
python Tools/MainThreadOptimization20260924/run_experiments.py --output Logs/MainThreadOptimization20260924/new-comparison --sequence comparison
python Tools/MainThreadOptimization20260924/summarize.py Logs/MainThreadOptimization20260924/new-comparison --output Logs/MainThreadOptimization20260924/new-comparison/summary
python Tools/MainThreadOptimization20260924/run_experiments.py --output Logs/MainThreadOptimization20260924/new-paired --sequence paired-mass
python Tools/MainThreadOptimization20260924/summarize_paired_mass.py Logs/MainThreadOptimization20260924/new-paired --output Logs/MainThreadOptimization20260924/new-paired/summary
python Tools/MainThreadOptimization20260924/run_experiments.py --output Logs/MainThreadOptimization20260924/new-regression --sequence regression
```

各outputは未作成のパスを指定する。Character入力は既存 `C:\log\zantetsuken-vr\Phase41ActPlayer\intake\m8` のhulls/render JSONを読む。必要ならそのprocessの `ZANTETSU_AUDIT_INTAKE` を指定する。入力asset自体は同梱していない。runnerは終了要求テストを分けて実行する。`playmode-baseline` は旧ガードによる7 inconclusiveを再現確認する専用モードで、通常の成功判定とは区別している。
