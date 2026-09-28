# check の計測負荷の削減と、計画の再計算 1 回の割り当て元（2026-09-29）

push は保留。commit は、製品（P）・計測の check（C）・Editor の診断（D）に分けて入れた（`mobplan-models-2026-09-29.md` の追記6）。製品のコードは変えていない（診断用に一時的に入れた印は削除済み。下の 2.）。

## 1. check の計測負荷の削減（計測側）

**変更**（check のみ）
- **詳細ファイルの切替**：Frame ごとの詳細ファイルは、診断用の引数 `-zantetsuMobPlanDetail` があるときだけ書く。
  - 対象：Hit 登録（`mobplan-hit-registry*.csv`）、表示の追跡（`mobplan-display.csv`）、寿命（`mobplan-lifetime.csv`）、視点（`mobplan-eye.csv`）
  - 引数がないとき（性能を測る起動）は、行の組み立て・文字列化・書き込みをしない。
- **残したもの**：
  - scenario の判定、件数、最初の失敗の情報、終了時の集計（Hit 登録の集計、表示の最初の未描画、寿命の終了時の値、視点の集計）
  - 命中ごとの 1 行（display track、hit registry at hit）
- **AutoFlush**：live モードのときだけにした（record、events、multi-hits、および上の詳細ファイル）。
  - 異常で終了するときは、既存の経路（`Application.quitting` → `MultiQuitting` → `MultiClose` → `MobPlanClose`）が writer を閉じ、その時点までの内容を書き出す。
- **寿命の記録**：csv がないときも、視点の高さの保持と、最大値の集計は続ける（以前は、csv がないと先頭で戻っていた）。
- **Hit 登録の集計**：「handles seen」は、詳細の有無にかかわらず全 handle を数える。
  - c1 では 23（詳細なしのとき、離脱期間のある handle だけに番号が付いていた）。
  - 修正後の c3 では 57。

**確認**（短い起動、v207、目 1.6 m・水平、`script-v3-60`、合計 4 分 43 秒）

| 起動 | Player | 詳細 | exit | 失敗 | 詳細ファイル | check の中央値／p90 | 3 ms 以上 | Frame の割り当ての中央値 |
|---|---|---|---|---|---|---|---|---|
| c1 | d26 | なし | 0 | なし | 出ない | 0.163／0.259 ms | 2 | 0.005 MB |
| c2 | d26 | あり | 0 | なし | 4 種が出る | 0.258／0.399 ms | 8 | 0.026 MB |
| c3 | d27 | なし | 0 | なし | 出ない | — | — | — |

- c3 の「handles seen」は 57。
- scenario の件数が 22 と 23 で違うのは、個体名の付いた退役の判定（1 件）の数による。
- 起動ごとに再生の長さ（Slash の回数）が違った：c1 は 60.3 s・6 回、c2 は 70.2 s・7 回。

## 2. 計画の再計算 1 回の割り当て元（Editor、Mono）

**方法**（`Assets/Zantetsu/Editor/Sandbox/MobPlanAllocationDiagnosis.cs`、Editor のみ、build に入らない）
- **条件**：実際の MobPlan の資産（`Assets/Licensed/MobPlan/MobPlanAssets.asset`）を使い、群衆と同じ load と、同じ定常の再計算を行う。
  - 20 NPC、`MobPlanPreset`、1 秒に 1 回
  - プレイヤーは 0.42 m/s で歩き、8 秒ごとに 36° 向きを変える
  - 3 回に 1 回、個体を 1 体、基準から外す（切断による退役に相当）
- **計測の場所**：専用の worker thread で処理し、その worker 側で測った。Main は、その worker を待つだけ。
- **限界**：
  - Unity の Mono でも、thread ごとの割り当て量（`GC.GetAllocatedBytesForCurrentThread`）は 0 しか返らなかった。
  - そのため、GC の heap の使用量を測った。Main は待っているだけなので、増える分は worker によるもの。
  - 先に heap を大きくしてから collect し、GC が起きた区間は除いた。
  - 校正：1,000,000 バイトの配列 1 つは 1,003,520 バイト、小さい配列 1000 個（中身 約 1 MB）は 1.18〜1.23 MB と読めた。
- **一時的な印**：`RunCycle` の段ごとの内訳を取るため、`LocomotionPlanner.cs` に一時的な印（`DiagnosticMark`）を入れて 1 回測った。
  - 印は測定の後に削除した。ファイルは印を入れる前と一致し、HEAD とも差分がない。
- **比較の扱い**：Mono での内訳は、原因を特定するために使う。IL2CPP での量や効果とは同じに扱わない。
  - Player（IL2CPP）では、再計算 1 回あたり約 7.5 MB（process 全体の値からの推定）。

**結果**（60 回、定常は 4 回目以降。GC の起きた回を除く）

| 段 | 定常の中央値 | p90 | 最大 |
|---|---|---|---|
| 1 回の合計 | 10.6〜10.9 MB | 15.9〜16.6 MB | 20.4〜23.1 MB |
| `PrepareExternal` | 5.37 MB | 10.7〜12.5 MB | 16.1 MB |
| `RunCycle` | 4.33〜4.43 MB | 5.8〜5.9 MB | 6.5〜7.8 MB |
| `AcknowledgeExternal`、config の複製、再配置の判定、公開する計画の作成 | 合わせて 4 KB 程度 | — | — |

**`RunCycle` の段ごとの内訳**（一時的な印による。中央値）

| 段 | 中央値 | p90 |
|---|---|---|
| 候補の探索（2） | 2.25 MB | 3.86 MB |
| 再生成（3b） | 2.08 MB | 2.32 MB |
| 接頭部と旧い接尾部（1） | 45 KB | — |
| 両立性と割り当て（3） | 16 KB | — |
| 公開（4） | 4 KB | — |
| その他の段 | 0 | — |

**大きい割り当ての内訳**

1. **ゴールの場（`GoalField.distance`）**
   - 用途：ゴールまでの経路距離の表（Dijkstra）。
   - 大きさ：地図 330 m × 330 m を 0.5 m のセルで覆う `float[660 × 660]`。1 つ作るごとに 1,785,856 B（計測値）。
   - 作成回数：新しいゴールのセルごとに 1 つ。60 回の再計算で 209 個、1 回あたり中央値 3、最大 9。
     - `PrepareExternal` の割り当ては、その回に作った場の数とほぼ一致した（5 個で 8.95 MB、4 個で 7.16 MB、1 個で 1.81 MB。場を除いた残りは中央値 16 KB）。
     - 流れのゴール（`UpdateFlowGoals`）が、プレイヤーに合わせて `flowWaypointStepMeters` ごとに動くので、新しいセルのゴールが続けて出る。
   - 保持期間：`LocomotionSimulation.goalFields` に最大 256 個まで保持され、それを超えると全部が消える。
     - 最大で約 457 MB が生きたまま残る。この測定では、最後に 230 個が残っていた。
   - 割合：定常の割り当ての 58.1%。
2. **探索の状態（`SingleAgentSearch.Run`）**
   - 評価した後継ごとに、`CostBreakdown` の複製（`Clone`）と `State` を作る（`EvaluateClip` 自体は、作業用の 1 つを使い回している）。
   - 回帰で、評価 1 件あたり約 261 B（切片 約 2.6 MB、相関 0.68）。候補の探索での評価数は 1 回に 1,068〜13,022 件。
   - 再生成の段（中央値 2.08 MB）の評価数は metrics にないので、同じ内訳かは確かめていない。
   - 保持期間：その回の中だけ（選ばれた候補の区間は、計画として残る）。
   - 割合：定常の割り当ての約 40%。
3. **初回の準備**（1 回だけ。定常の再計算とは別）
   - 合計は約 129〜131 MB で、以後も保持される。
   - 内訳：歩行の tile 50.6 MB、simulation の初期化 36.2 MB（初期 20 個分のゴールの場 約 35.7 MB を含む）、pose bank 41.6〜43.5 MB、dataset 0.27 MB、地図 0.07 MB。

## 3. 修正案（1 つ、未実装）

**ゴールの場の距離配列を、この回に使っていない古い場から引き継ぐ**
- **方法**：
  - 場の cache に上限（例えば 32 個）を設ける。
  - 新しい場を作るとき、上限に達していれば、この回にまだ使っていない、最も古い場の `distance` 配列をそのまま使う。
  - `Build` は全セルを +∞ で初期化してから計算するので、前の内容は残らない。
  - 計算そのもの、ゴール、経路の結果は変わらない。
- **安全の前提**：公開済みの計画や、別の計算が参照するデータを上書きしないこと。コードで確かめたのは次の点。
  - 場を参照するのは、その回の snapshot（`AgentSnapshot.Field`）と探索の要求（`SearchRequest.Field`）、simulation 内の進み具合の判定とゴールの選択だけ。いずれも計画の worker の中で、1 回の再計算の間だけ使う。
  - 公開する計画（`PublishedMobPlan`）と、回の結果（`CycleResult`）は、場を持たない。
  - 再計算は同時に 1 つだけ（群衆の `busy`）。
  - 同じ回の中で使った場は引き継ぎの対象にしない（最後に使った回を記録する）ので、その回の snapshot が参照している場は上書きされない。
- **効果の見込み**（Mono での推定。IL2CPP では測っていない）：
  - 定常の割り当ての約 58%（1 回あたり約 6 MB）がなくなる。
  - 保持量は最大 約 457 MB から約 57 MB（32 個）になる。
  - 場の計算時間は変わらない。
  - GC の回数への効果は、Player で測って確かめる必要がある。
- **範囲外**（今回は出さない）：
  - 汎用の pool
  - ゴールの量子化など、計画アルゴリズムの変更
  - 探索の状態の構造の変更（`CostBreakdown` を値型にするなど）

## 残すこと

- Collect の内部の費用は未解明のまま。今回の割り当ての診断で、前回の物理処理との重なりが解決したとは扱わない。
- 固定の Frame 延期と、凸包 Mesh の所有権の変更は保留（TL）。
- 表現：
  - Player で見た約 7.5 MB は、process 全体の測定による。計画の再計算は「有力な発生元」として扱う。
  - Mono の内訳は、原因の特定のためのもの。

## 証拠

- `C:\log\zantetsuken-vr\MobPlanSlash\`
  - check の確認：`c1-detailoff`、`c2-detailon`、`c3-detailoff`、`build-d26`、`build-d27`、`code-state-detail*`
  - 計画：`planner-alloc-2`〜`planner-alloc-5-compile`（`-4` に `RunCycle` の段ごとの内訳と、その時の harness）
  - 印を入れる前の `LocomotionPlanner.cs`：`LocomotionPlanner.before-marks.cs`
