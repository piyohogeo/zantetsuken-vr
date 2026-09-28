# 再準備フレームの約 2 MB の割り当て元（診断、2026-09-28）

push は保留。commit は、製品（P）・計測の check（C）・Editor の診断（D）に分けて入れた（`mobplan-models-2026-09-29.md` の追記6）。製品のコードは変えていない。追加したのは check の診断だけ。

## 1. 方法

- **起動**：診断用の短い起動 2 回。
  - a1：Player d24
  - a2：Player d25（a1 に、check の MobPlan 部分の割り当ての分離を加えた）
- **条件**：
  - v207（72 Hz）、目 1.6 m、水平視点、予備の枠 10、寿命管理のしきい値 64
  - script-v3 の 60 s までの写し（`script-v3-60.txt`）
  - 各起動は約 68 秒の再生で、合計は 10 分以内。
- **診断の内容**（check のみ、`-zantetsuAllocStages` のときだけ動く）：
  - 実行順を固定した probe で、Main thread の 1 Frame を 7 区間に分けた：

    | 区間 | 入っている処理 |
    |---|---|
    | a | 先頭〜群衆の前 |
    | b | 群衆の Update（再準備・補充・計画の回収） |
    | c | 姿勢・Hit・切断の要求 |
    | d | ほかの Update と LateUpdate（World の回収・寿命管理・頂点の回収） |
    | e | check の LateUpdate |
    | f | LateUpdate の後の物理の step |
    | g | 描画と AfterRendering |

  - 区間ごとに、GC の heap の増分と GC の回数を記録した。
  - 記録は事前に確保した配列に入れ、終了時に一度だけ書く（`mobplan-alloc.csv`）。
  - a2 では、check の MobPlan 部分の heap の増分も分けた。
  - check の内部に marker を足した：`Check.MobPlanFrame`、`.MobPlanDisplay`、`.MobPlanLifetime`、`.MobPlanHitRegistry`。
- **限界**：
  - IL2CPP では、thread ごとの割り当て量（`GC.GetAllocatedBytesForCurrentThread`）が 0 しか返さない。
  - そのため、process 全体の heap の増分（`GC.GetTotalMemory`）を使った。worker の割り当ては、その時に動いている Main の区間に数えられる。
  - Unity の「GC Allocated In Frame」も process 全体の値と一致した（GC のない Frame で、heap の合計との比の中央値は 0.96〜0.97）。

## 2. 結果

**割り当て元の分離**

| | a1 | a2 |
|---|---|---|
| 計画の再計算中の Frame（worker で計算中） | 460 Frame で 678 MB | 451 Frame で 650 MB |
| 　計画 1 回あたり | 7.53 MB（90 回） | 7.47 MB（87 回） |
| それ以外の Frame | 1 Frame あたり 0.031 MB | 0.032 MB |
| 　うち check（区間 e の中央値） | 0.029 MB | 0.029 MB |
| GC.Collect | 66 回（1.12 回／s）、中央値 2.15 ms | 66 回（1.11 回／s）、中央値 1.97 ms |

**区間ごとの中央値**（a2、GC のない区間）

| Frame の種類 | b | e | f |
|---|---|---|---|
| 再準備 Frame（22） | 0.080 MB | 1.784 MB | 0.524 MB |
| 補充 Frame（21） | — | 0.045 MB | 0.811 MB |
| どちらもない Frame（4,245） | — | 0.029 MB | 0 |

- 区間 b には、再準備・退役・補充・計画の依頼がすべて入る。
- 再準備 Frame の区間 e の大部分は、check の MobPlan 部分（中央値 1.769 MB）だった。
- ただし、1 MB を超えた check の区間 e（41 Frame、各 1.75〜1.82 MB）は、すべて計画の再計算中だった。再計算中でない Frame では 0 件。
- 再準備 Frame 22 のうち 18 が、再計算中だった。退役と補充と同じ群衆の Update で、計画の再計算も依頼されるため。
- 再計算中の Frame（a2）：heap は 1 Frame あたり中央値 1.41 MB。再計算中でない Frame は 0.029 MB。

**判定**（TL レビューにより表現を限定、2026-09-29）
- 確認できたのは、大きな割り当てが計画の再計算中に集中していたこと。
  - process 全体の測定なので、約 7.5 MB のすべてが計画の worker によるものとまでは確定していない。
  - 計画の再計算は「有力な発生元」として扱う。
- 再準備 Frame の約 2 MB は、再準備自身の割り当てとは扱わない。
  - 根拠は、再計算中かどうかとの一致と、区間の位置（check や物理の区間に、決まった大きさで出る）。thread ごとの計測ではない。
- 再準備自身の割り当ては 0.08 MB 以下。算出方法と限界は次のとおり。
  - 算出方法：区間 b（群衆の Update 全体。再準備・退役・補充・計画の依頼を含む）の GC heap の増分の、再準備 Frame での中央値（a2 で 0.080 MB、a1 で 0.055 MB）。GC が起きた区間は除いた。再準備だけを切り出した値ではなく、区間 b 全体の上限としての値。
  - 限界：
    - process 全体の heap の値なので、同じ時間に動く worker の割り当ても含みうる。
    - Boehm の heap の値は数 KB 単位で、Frame ごとのずれがある（GC のない Frame で、「GC Allocated In Frame」との比の p10 は 0.48〜0.57、p90 は 1.27）。
    - 中央値なので、個々の再準備がこれを超えた可能性は残る。
- 全割り当ての 84%（a2 で 771 MB 中 650 MB。a1 は 798 MB 中 678 MB で 85%）が計画の再計算中だった。GC（約 2 ms、1 秒に約 1 回）の有力な原因の候補。ただし、GC がどの割り当てで起きたかは測っていない。
- 計画 1 回の内訳は、Editor（Mono）で別に調べた（`planner-allocation-2026-09-29.md`）。

## 3. 再準備が作り直しているもの

p1〜p3 の再準備 158 回。marker の中央値／p90。

| 処理 | 時間 | 毎回必要か |
|---|---|---|
| 再準備の全体（`Npc.Reprepare`） | 1.51／2.23 ms | — |
| 物理入力の作成（`CharacterCut.Prepare.Physics`） | 1.10／1.62 ms | 下を参照 |
| 退役の確認（`Npc.Prepare.Withdraw`） | 0.23／0.44 ms | 階層の確認。個体状態に依存するので毎回 |
| cold の準備 | 0.09 ms | 個体ごと |
| Hit 形状 | 0.02 ms | 個体ごと |
| 表示の枠 | 0.004 ms | 個体ごと |
| DirectSkin（入力の作成・枠の作り直し） | 0 | 枠で再利用済み |

**物理入力（`VpPreparedPhysicsInput`）**
- 毎回、凸包ごとに次を作る：
  - 頂点配列（Vector3[] と float3[]）
  - 三角形の List と、その配列
  - Mesh
  - `Physics.BakeMesh`（焼き込み）
- 同じ枠では、材料（hull bank、範囲、骨）は毎回同じ。焼き込んだ Mesh も同じ内容になる。
- **同じ枠で保持・再利用できるもの**：凸包の Mesh と、その焼き込み結果。
- **ただし、所有権が変わる**：
  - Mesh は `PhysicsShapeSource.OwnPreparedMeshes` として handle の形状が所有し、形状の終わりに一緒に返る。
  - 枠で保持すると、所有と寿命が変わる。今回の範囲（寿命・所有権を変えない）には入らない。
- **寿命・所有権を変えずに除ける配列**：
  - 管理配列（頂点・三角形）は凸包 1 つあたり数百バイトから数 KB で、再準備全体でも 0.08 MB 以下。
  - 除いても、GC への影響は小さい。
- **判定**：再準備の割り当てについて、最小の修正案は出さない（必要がない）。時間の大部分は焼き込みで、それを減らすには所有権の変更が要る。

## 4. check の負荷

**通常の Frame**
- 時間の中央値は約 0.3 ms。割り当ては 1 Frame あたり 0.029 MB で、再計算中でない Frame の割り当てのほぼすべてにあたる。

**3 ms 以上の Frame**（a1 18 件、a2 14 件）
- 一番大きかった部分の内訳（a2）：

  | 部分 | 件数 |
  |---|---|
  | Hit 登録の記録 | 5 |
  | 表示の追跡 | 4 |
  | 寿命の記録 | 3 |
  | その他 | 2 |

- 14 件中 7 件は、同じ Frame に GC がある。
- GC のない高い Frame（a1 で、寿命の記録が 3.0〜5.5 ms の 7 件など）について：
  - 寿命と Hit 登録の csv は、1 行ごとに disk へ書き出す（`AutoFlush = true`）。
  - それが時々の書き出し待ちになっている可能性がある（推測。未計測）。

**方針の案**（計測負荷の削減。製品の改善とは別）
- 性能を測る起動では、Frame ごとの詳細記録を止め、診断の起動だけで出す。
  - 対象：Hit 登録の行、寿命の csv、表示の追跡の行、視点の csv、割り当ての probe。
- 機能の判定（scenario）は、メモリ上の集計で残す。
- `AutoFlush` は live モードだけに限る。

## 5. 修正候補のまとめ

- **製品（再準備）**：割り当ての修正は不要。時間（焼き込み 1.1 ms）を減らすには、枠での Mesh の保持（所有権の変更）が必要で、今回の範囲の外。
- **製品（計画の再計算）**：
  - 1 回あたり約 7.5 MB（process 全体の値からの推定）。GC の主因の候補。
  - 割り当ての内訳（どの配列か）は未調査。
  - 次に調べるなら、EditMode（Mono）で計画 1 回の割り当てを thread ごとに測り、呼び出し元を特定する。
- **計測（check）**：上の 4. の方針。
- **Collect の内部の調査と、固定の Frame 延期**：今回の結果を見てから判断する（TL）。

## 証拠

- `C:\log\zantetsuken-vr\MobPlanSlash\`
  - 起動：`a1-alloc`、`a2-alloc`
  - build：`build-d24`、`build-d25`
  - コードの状態：`code-state-alloc`、`code-state-alloc2`
  - 集計：`AnalyseAlloc.py`
- **作業ツリーの check の差分**（未コミット）：
  - `SandboxPropSlashPlayerCheck.MobPlanAllocStages.cs`（新規、診断のみ）
  - `SandboxPropSlashPlayerCheck.MobPlanEyeView.cs`（前の単位）
  - `.MobPlan.cs`（呼び出しと marker）
