# c_21 の元の Hull を製品の経路で確認（2026-09-29）

push は保留。c_21 の Hull は変えていない。

**結論**（2026-09-29、TL 採用）
- **c_21**：既知の切断の失敗を許容して採用した（元の Hull のまま。取り込みの例外は c_21 の DEF-foot.R だけ）。
- **失敗の後の枠の取り残し**：解消した（abort が落ち着いた枠は戻る）。
- **失敗の発生頻度**：評価していない。Hull の修復と kernel の修正は、頻度を見て判断する。
- **失敗の記録**
  - World の終了時の 1 行に、失敗の総数・内訳・最初の失敗の識別情報が出る。check を動かさない通常の起動でも出る。
  - モデル別・根／子別の試行数と失敗数は、check を動かしたときだけ出る。
- この記録の前半（下の「対象」から「一般の再切断の失敗」まで）は、補正前の確認である。その時点では、製品のコードを変えていなかった。

## 対象
- ITHappy Professional c_21 の 19 Hull。上流の blend をそのまま読んだ。
- 取り込みと同じ扱いにした。ただし、flip で直らない Hull は元の面のまま残し、拒否の理由だけを記録した（`C:\log\zantetsuken-vr\MobPlanModels\tools\c21_fixture.py`）。
  - fixture：`C:\log\zantetsuken-vr\MobPlanModels\c21\professional-c_21.json`。本番の取り込みには入れていない。
  - DEF-foot.R：元の面のまま。監査は「nonconvex or inward face」で不合格。
  - DEF-shin.R：他のモデルと同じ flip の修復を受けた。
  - 他の 17 Hull：監査に合格。
- 問題の面は DEF-foot.R の面 10（頂点 15、19、22）。
  - 頂点 15 は、長さ 0.256 m の辺 19–22 から 8.25e-7 m。
  - 巻き順から計算した法線が内向きになるため、監査が不合格にする。
- 確認は PlayMode ではなく、EditMode の診断試験で行った（`Assets/Zantetsu/Tests/EditMode/PhysicsCut/C21OriginalHullDiagnosisTests.cs`、`[Explicit]`、commit しない）。
  - 製品の部品をそのまま使った：質量の規則（面の巻き順で四面体を足す）、cook（点から凸包、製品の cooking の設定）、Owner の切断の kernel（job 経由）。
  - 対照：左足（DEF-foot.L）に、左右を反転した同じ条件を当てた。
  - 報告：`c21\editmode-diagnosis.txt`、`c21\editmode-contact.txt`

## 製品が面をどう使うか（コードの確認）
- **面の平面**：製品は面の平面を持たない。凸かどうかや面の向きの検査もない。
- **cook**：点だけから凸包を作る。面の組と巻き順は使わない。
- **質量**：面の巻き順で四面体を足す。
- **切断の kernel**：頂点がどちら側にあるかと、面の輪だけで切る。出力で見るのは、閉じていること、Euler の式、体積が正であることだけ。
- kernel が失敗すると `KernelFailed` になり、その Owner は何も公開しない。source の退役や transaction の中止は、ここではつながっていない（`PhysicsCutCook.cs:669-674`）。

## 結果

| 項目 | 右足（元、針状の面あり） | 左足（対照） | 判断 |
|---|---|---|---|
| 質量（B-rep と、点の凸包との比較） | 体積の差 3.1e-7（相対）、重心の差 2.9e-8 m、慣性の差 5.3e-7（相対） | 2e-8、3e-9 m、8e-8 | 実害なし |
| cook（collider の外接箱と B-rep の差） | 2.6e-8 m | 1.7e-8 m | 実害なし |
| 接触（floor に落として 3 s。面 9、10、11、26 を下にした 4 通り＋無作為 20 通り） | 24 通りすべて静止。床への食い込み 0.007〜1.2 mm | 24 通りすべて静止。0.004〜2.0 mm | 差なし |
| 針状の面の長い辺を含む平面（36 角度 × 11 offset） | 396 回中 35 回が `CutFailed(WalkFailed)`。いずれも offset が 0 か ±1e-7 m のとき | 0 回。普通の辺を含む平面でも、左右とも 0 回（各 396 回） | **針状の面に固有の失敗** |
| 頂点 15 を通る平面（無作為 200）、針状の面を横切る平面（22） | すべて Ok | すべて Ok | 実害なし |
| 再切断（同じ頂点をちょうど通る 2 回目の平面） | 失敗あり | 普通の頂点でも失敗あり（8/96、1/72、2/64、3/64） | kernel の一般の性質で、c_21 に固有ではない |

- 切断が成功した場合の体積の保存は、どちらの足も 1e-7（相対）以下（再切断は 5e-5 以下）。
- `BrepVerifier`（許容値 1e-6 × 外接の大きさ）は、両足の元の Hull にも切断した片にも違反を出した。監査より厳しい許容値のため、左右を区別する材料にはしない。
- collider の ClosestPoint による距離（左足の頂点で 1.6 cm など）は、外接箱の一致と食い違う。原因は調べていないので、判断には使っていない。

## 範囲の訂正（TL 指示）
- 「質量・cook・接触には実害がない」は、**今回試した条件では問題を観測しなかった**と読む。
- ±1e-7 m は**観測した失敗の条件**である。失敗する領域の厳密な境界や、実際のゲームで起きる確率ではない（確率は測っていない）。

## 追記：KernelFailed の後の製品の動き（PlayMode、製品の経路）
- 試験：`Assets/Zantetsu/Tests/PlayMode/C21KernelFailedPlayModeTests.cs`（`[Explicit]`、commit しない）。報告：`c21\playmode-kernelfailed.txt`
- **組み立て**
  - c_21 DEF-foot.R の元の Hull（m 単位、骨は単位行列）を持つ群衆の枠を、`SandboxNpcCharacter` の `PrepareAsSlot` で作り、活性化した。表示 mesh は Hull 自身の三角形。
  - `SlashHitDetector` に、針状の面の長い辺を含む平面の sweep を当てた（EditMode で失敗した角度）。
  - 対照を 2 つ置いた：同じ平面を 1e-5 m ずらして別の右足の枠に当てたもの、左足の枠を普通に切ったもの。
- **結果**（針状の面の長い辺を含む平面）

| 時点 | 観測 |
|---|---|
| Hit | 1 件、acceptance `Published`。character は通常どおり撤去された |
| 次の Frame | kernel が `CutFailed(WalkFailed)`（convex 0）。cut request が `KernelFailed`、ledger が `Aborted`、geometry が `Reclaimed`、transaction は終了 |
| 資源 | 保持していた入力や未公開の候補は残らない。物理の owner 0。描画の片 2 が 1 Frame だけ出て、その次の Frame に 0 |
| その後 | character は消える（片も、物理も、Hit の対象も残らない） |
| 枠の戻り | `IsReturnReady` は偽のまま（geometry が Committed でなく Reclaimed のため）。群衆ではこの枠が戻らない（判定式からの推論で、群衆の中では試していない） |
| 次の Slash | この character には当たるものがない（Hit 0） |
| 同じ world の他の character | 普通に切れる（1e-5 m ずらした平面も、左足も、Completed・Committed・戻れる） |
| 共通終了 | 進まない（`TerminationRequested` は偽）。試験の最後に world は通常どおり終了し、資源を返した |

- 失敗の後は、world を壊さずに止まる。ただし、その character は片を残さずに消え、群衆の枠が 1 つ戻らなくなる。
- この扱いは c_21 に固有ではなく、どのモデルでも `KernelFailed` が起きれば同じになる。c_21 は、その起きる条件（針状の面）を持つ。

## 追記：診断用の scene で、通常の Slash の経路
- **診断用の取り込み**（本番の 48 モデルと、その scene・intake は変えていない。本番の intake.json の hash は 523d2aea… のまま）
  - `MobPlanModelsIntake -mobPlanModelsDiag diag/`：c_21 の ID 付き blend と topology を `diag/` から読み、`diag/intake.json` と `diag/runtime/` に書く。
  - Hull の fixture は `diag/hulls/`（DEF-foot.R は元の面のまま）。
  - scene builder の引数：`-mobPlanModelsIntakePath`、`-mobPlanModelsScenePath`、`-mobPlanModelsHullsDir`
- **刀の入力**：保存した入力（`SlashSpan60918-153438-normal\input.csv`）の gripY を、全行で −1.35 m 平行移動した（`c21\input-ankle-minus1.35.csv`）。
  - 根拠：主な水平の振り（行 2300〜2319）で、刃の 75% の点が 1.42〜1.83 m にある。足首の目標は world 0.18 m（床は 0.1 m）。
  - 他の振りは 0.85〜2.8 m にあるので、一部は床より下になる。
  - runner に `-SlashInputPath` を加えた（既定は元の入力）。
- **f10**（m17、48 モデル＋c_21、c_21 を先頭）：exit 14。
  - c_21 は登場し描画されたが、どの Slash にも当たらなかった。
  - 失敗は、scenario の期待「返った枠がまた使われる」の 2 件。根の切断 13 に対して空き枠が 29 あり、固定の順では返った枠を使う前に終わった。製品の失敗ではない。
  - 他のモデルでは、根の Commit 13、子の Commit 22。abort はなかった。
- **f11**（m18、c_21 だけの 30 枠）：同期は成立（準備完了の 18.1 s 後に再生）、exit 0、通常終了。
  - c_21 の個体 36（再利用した枠 6）、根の切断 16（公開・Commit とも 16、再利用した枠 1）、子の再切断の Commit 19、broken 0、失敗 0。
  - 接触：片の記録 52 件がすべて「床 −0.02 m 以上」。
  - 拡大画像 17 枚（歩行 1、切断 8 回の 2 Frame 後と 60 Frame 後）。倒れた片は写っている。
  - **この 16 回の切断で DEF-foot.R の Hull 自体が分かれたかは、記録にない**（Hull ごとの結果を check が書かない）。
- これは Gesture → SlashWave → Hit → 切断の実の経路。上の PlayMode の確認（直接 Hit を評価したもの）とは別である。
- 既知の極端な平面を、手の操作で再現することはしていない。

## 追記：一般の再切断の失敗（別件）
- 普通の頂点をちょうど通る 2 回目の平面の失敗について、最小の入力（切った後の片と平面）を 4 件保存した：`c21ecut-repro-*.json`
- 単独で再生しても 4 件とも `CutFailed(WalkFailed)` になる（`c21ecut-repro-replay.txt`、試験 `RecutRepro_OrdinaryVertex_FailsAlone`）。
- c_21 を修復しても、この失敗はなくならない。

## 判断のための整理
- **監査の不合格**：cook の失敗は意味しない（cook は点から作る）。
- **製品での既知の失敗**：針状の面の長い辺をほぼ含む平面で `WalkFailed` → `KernelFailed` → character が消え、群衆の枠が戻らない。
- **通常の実 Slash**（f11）：根・子の切断、表示、接触、Commit、通常終了はいずれも成立した。ただし、足の Hull が分かれたかは未確認。
- **採用の判断には 2 つの論点がある**
  - `KernelFailed` の後の扱い（character が消え、枠が戻らない）は、モデルによらない製品の論点。
  - c_21 は、その失敗が起きる条件（針状の面）を持つ。
- 製品のコードと監査の条件は変えていない。

## 追記：採用の条件、失敗の計数、失敗の後の枠の返却（TL 指示）

- **方針**：c_21 は、既知の失敗を計数することを条件に採用する。Hull の修復と kernel の修正は、失敗の頻度を見て判断する。
  - 今回の確認は「頻発しない」ことの証明ではない。
- 切断の失敗と、失敗の後に枠が永久に戻らないことは分けて扱う。
  - 片が消えることは、今回は許容する。
  - 枠が戻らないことは、最小限の補正をした。

### 1. 失敗の計数（製品：`ProvisionalCutDriver`）
- **数える場所**：cut が products なしで終わり abort されるところ（`CollectEndedCuts`）で、1 回だけ数える。record は終わった cut を 1 回しか受け取らないので、毎 Frame 加算されることはない。
- **数えるもの**
  - 総数 `FailedCutCount`、そのうちの `KernelFailedCount`
  - 切断の状態ごとの内訳（`KernelFailuresWithCutStatus`）
  - 最初の 8 件の識別情報（operation、終わり方、kernel の状態、切断の状態、失敗した convex、frame）。それ以上は保存しない。どの cut がどう失敗したかを示すだけで、切断面と入力の形は保存しないので、これだけでは再現できない。
  - event `CutFailed`（1 件ごとに 1 回）
- **check**（`SandboxPropSlashPlayerCheck.MobPlanModels.cs`）
  - event を受け、accepted cut と operation で突き合わせる。
  - モデルごとに、根の切断と子の再切断を分けて、試行数と失敗数、失敗の種類を `mobplan-models.csv` に加えた。
  - 終了時に 1 行で集計し、保存した最初の数件を、モデル・根／子・Slash と一緒に書く。
  - 詳細ログの引数がない通常の起動でも出る。

### 2. 失敗の後の枠の返却（製品：`SandboxNpcCharacter.IsReturnReady`）
- **補正前**：Committed でないと戻れなかったため、abort された枠は永久に戻らなかった（前の PlayMode の診断で確認）。
- **補正後**：Committed に加えて、次の 3 つがすべて成り立つときも戻れる。
  - ledger が `Aborted`
  - geometry が `Reclaimed`
  - driver がその operation の record を持たない（生きている record も、work の戻りを待つ recovery の record もない。`ProvisionalCutDriver.IsSettled`、`ProvisionalCutRecovery.Holds`）
- `Reclaimed` という状態だけでは戻さない。既存の条件（handle の終了、撤去済み、DirectSkin の入力の返却）はそのまま。
- 群衆の check：枠の再利用の検査で、前の個体の cut が「失敗として計数され、abort された」場合も正しい返却と扱う（`SandboxPropSlashPlayerCheck.MobPlan.cs`）。
- **焦点試験** `ASlotWhoseCutFailed_ComesBackAfterTheAbortSettles_AndItsNextIndividualIsCut`（PlayMode）
  - licensed のデータを使わない合成の針状の Hull を使う。0.25 m の箱の辺の 2/3 の位置に、8.25e-7 m 内側へずらした頂点を置いたもの。
  - その辺を 60° で含む平面で `WalkFailed` を再現する。EditMode での探索では、同じ形の針状の面は、ずらす向きによって同じ失敗を起こした（6 通りのうち 4 通り）。
  - 確認すること：
    - 失敗が 1 回だけ計数される（その後の Frame でも増えない）。
    - abort が落ち着くまでは戻れず、落ち着いたら戻れる。
    - 同じ枠が再準備され、新しい個体として描画・Hit の対象になる。
    - 通常の Slash が当たり、Commit まで進む。Commit の後も枠は戻れる。

### 3. c_21 を通常の 49 モデルへ
- **取り込み**：`intake_mobplan.py` v4 に名指しの例外 `ACCEPTED_AUDIT_FAILURES` を加えた。
  - 対象は c_21 の DEF-foot.R だけ。上流 blend の hash、flip の拒否理由、監査の失敗の文言がすべて一致するときだけ、元の面のまま通す。
  - 他の拒否と、他の blend の同じ Hull は、今までどおり拒否する。
  - 例外を使ったことは、報告の `acceptedAuditFailures` に残る。
  - 監査の失敗が起きなくなったら、例外を消すよう要求して止まる。
- **intake**：49 モデル。前の 48 件は、対応表とすべての hash が同じ。実行用 mesh 49/49 が一致した。
- **Player m19、f12**（既定の順、49 枠）：同期は成立（準備完了の 9.75 s 後に再生）、exit 0、通常終了。
  - 準備 9.49 s、broken 0。49 モデルすべてが登場し、描画・Pose・Hit 候補になった。
  - 根の Commit 42 モデル、再利用した枠の個体 20。
  - 失敗の集計：失敗 0、試行は根 49・子 162。
  - c_21 は登場したが、この回では当たらなかった。
- **f11 の扱い**：通常の Slash の成功例として扱う。問題の右足の Hull が実際に分かれた証拠とはしない。

### 4. 試験（最終の作業木）
- PlayMode 27/27：新しい試験、共有の 3 試験、DirectSkin の貸し出しの寿命、Hit の登録、U8、撤去、予算による保留、骨の検査
- EditMode `MobPlanSlotPoolTests` 5/5
- 失敗が実際に起きたときの check の突き合わせは、Player では試していない（f12 は失敗 0）。driver の計数は焦点試験で確認した。

### 範囲
- 自動の再試行、元の NPC への巻き戻し、Hull の修復はしていない。
- 監査の条件は変えていない。
- 例外は c_21 の DEF-foot.R に限る。

### 追記（TL 指示）：check なしの通常の起動での記録と、失敗があるときの突き合わせ
- **World の終了時の 1 行**（製品：`CutWorldRoot`）
  - World が終わるとき（通常の終了の開始、または破棄。Player の終了要求の場合も含む）に 1 回だけ、driver の `FailureSummary()` を log に出す。
  - 内容：失敗の総数、`KernelFailed` の数と切断の状態ごとの内訳、保存した最初の失敗の識別情報。
  - check を動かさない Link の通常の起動でも出る。毎 Frame の log はない。終了時に待つこともない。
- **制限**：モデル別・根／子別の試行数と失敗数は、check だけにある。どの operation がどのモデルの切断かを知っているのは check なので、World の 1 行は総数・内訳・識別情報だけである。
- **突き合わせの処理を、試験できる形にした**
  - check の突き合わせ（失敗の受け取り、operation からモデル・根／子への帰属、再利用の判定 `FailedBefore`、終了時の集計）を `MobPlanCutFailureTally`（Sandbox）へ移した。check はこれを使う。
- **決定的な確認**（合成の針状の Hull の焦点試験 `ASlotWhoseCutFailed_…` に加えた）
  - 失敗した Hit の operation に、失敗 1 件が対応した（`KernelFailed`、`WalkFailed`）。
  - check への報告は 1 回だけで、二重には数えない。同じ operation を 2 回渡しても、試行は 1 回と数える。
  - その後の通常の切断は、失敗に数えない。
  - 集計：根の試行 2・失敗 1、種類は `KernelFailed/WalkFailed` 1、どの切断にも属さない失敗 0。子として渡せば、子の失敗として数える。
  - 再利用の判定：失敗した operation は「その前に失敗した」と判定し、通常の operation はそう判定しない。
  - World の終了時の log（試験の中で通常の終了をさせたもの）：`CUT WORLD D6H cold world at its end: failed cuts 1, KernelFailed 1 [WalkFailed 1], kept 1; operation 1 frame 15 KernelFailed kernel CutFailed clip WalkFailed convex 0`
- **試験**
  - PlayMode 37/37（前の焦点試験に、World の終了の保護の試験を加えた）
  - EditMode のプールの試験 5/5
- 長い統合起動はしていない。f12 のあとの製品の変更は、終了時の 1 行だけである。
