# PERFORMANCE_PRINCIPLES

## 目的

この文書は、`zantetsuken-vr` における性能設計・実装・レビューの基本原則を定める。

個々の既存実装、過去の実装都合、暫定的な安全策よりも、本書の原則を優先する。

本書の目的は、性能値だけを良く見せることではない。  
**人間が承認した製品意味論・描画品質・数値精度の範囲を守りながら、特に Main Thread の実行時間と切断時 spike を最小化すること**を目的とする。

---

## 1. Frame Budget を常に意識する

90 Hz 動作では、1 frame の時間予算は約 11.11 ms である。

ただし、

> 「frame budget の 1%程度だから小さい」

とは考えないこと。

小さい固定費が多数積み重なると、切断・描画・Physics・入力・XR 等が同時に発生する実ゲームでは容易に spike となる。

特に hot path では、数十 µs を「十分小さい」として無条件に受け入れない。

---

## 2. Main Thread CPU Time を最も貴重な資源として扱う

Main Thread CPU time は、本プロジェクトにおいて最も貴重な計算資源の一つである。

Main Thread で行う必要のない処理は、可能な限り Main Thread から排除する。

以下は特に疑うこと。

- 数値計算
- Geometry / Physics の前処理
- 不要な validation
- 全走査
- copy / materialization
- allocation / zero fill
- bounds 再計算
- 重複 query
- bookkeeping
- Worker で実行可能な処理

Main Thread へ処理を戻すことを、軽量化の解決策として安易に採用しない。

---

## 3. 最重要 Critical Path

特に次の経路を最重要 critical path として扱う。

`切断入力 -> Provisional Physics -> 描画`

この経路は spike の主要因になり得る。

特に、**Skinned Character の初回切断経路が最もクリティカルであり、性能設計・最適化の最優先対象とする。**

この経路上では、**10 µs 単位でも削減対象として扱うこと。**

ただし、10 µs 未満の差を測定器の分解能を超えて追い回すことを要求するものではない。  
測定精度と効果量を考慮し、より大きい固定費を優先して除去する。

---

## 4. Main Thread Time と他資源を積極的に交換する

Main Thread CPU time を削減するため、以下を積極的に利用する。

- 事前計算
- 事前確保
- 再利用
- キャッシュ
- Worker 実行
- scratch / arena / pool
- immutable data の共有
- 既存成果物の再利用
- critical path 外への処理移動

本プロジェクトでは、合理的な範囲で、

**Memory / Worker Compute < Main Thread CPU Time**

という優先順位で設計する。

記憶容量や Worker 計算資源を節約するために、Main Thread 時間や latency を増やさないこと。

---

## 5. Independent Work を不必要に直列化しない

依存関係のない Work は並列実行可能であることを基本とする。

次のような制約を導入する場合は、仕様または correctness 上の具体的理由を示すこと。

- `max inflight = 1`
- global serialization
- global lock
- 単一 Owner / Resource と無関係な排他
- submission order と completion order の固定
- completion order = submission order という暗黙仮定

異なる Owner / Resource の Work は、必要な依存関係がない限り独立に flight できることを優先する。

実行順序を強制するより、

- Work identity
- Owner identity
- Transaction identity
- dependency
- commit / publication ordering

を明示して扱うこと。

---

## 6. 既存実装を保存すること自体には価値を置かない

性能原則の実現に不要な既存内部構造は、積極的に変更・削除してよい。

特に以下を神聖視しない。

- 中間 copy
- materialization
- zero initialization
- validation
- temporary container
- duplicated lookup
- duplicated bounds calculation
- redundant allocation
- bookkeeping layer
- unnecessary queue
- unnecessary state
- unnecessary wrapper
- unnecessary lifetime object
- unnecessary serialization
- unnecessary wait

**少数の確保で済む領域を、多数の小 allocation に分解して固定費を増やしてはならない。**
例えば、同じ寿命の1ブロックを、型や責務の分割・容量節約・管理の分かりやすさだけを理由に、10本の独立 allocation へ分けない。これは新規実装にも、既存実装の置換にも適用する。

まとめて確保できる同じ寿命の領域は、まとめた確保と slice / offset 等による利用、または既存領域の再利用を優先する。別々の寿命や API 制約などで分割が必要な場合は、その具体的理由と、確保・解放・管理を含む総固定費を確認する。

**既存実装を破壊することは、人間が承認した製品意味論・品質条件を守る限り推奨される。**

---

## 7. 描画品質・数値精度とのトレードオフは人間判断で積極的に許容する

**描画品質や数値精度の許容範囲を調整して性能を得ることは、有効な最適化である。人間判断のもとで積極的に提案・採用してよい。**

変わる見た目・数値精度と性能上の狙いを説明し、人間が承認した範囲で変更する。
承認済みの表現変更・近似・量子化等による差は、差があること自体を不具合や受入不可の理由にしない。
関係するテスト期待値・tolerance・受入条件も、承認された許容範囲に合わせて更新してよい。既存の bit-exact 一致や旧 tolerance を理由に、承認済みのトレードオフを拒否しない。

禁止するのはトレードオフそのものではなく、**未承認の変更と、品質条件や評価対象をすり替えた改善報告**である。

- 人間の承認なく、製品意味論・描画品質・数値精度・受入条件を緩める。
- 品質条件を変えた改善を、同じ品質条件での高速化として報告する。
- 別 subsystem の改善で対象 subsystem の問題を相殺し、解決済みと扱う。
- benchmark でしか成立しない静止 camera 等の条件や入力固有の shortcut を、通常プレイで得られる性能改善として扱う。
- 承認範囲と無関係なテスト期待値・tolerance まで緩め、不具合を隠す。

局所性能と全体性能は別々に記録する。品質・精度を変更した場合は、その変更条件と得られた性能効果を対応付けて報告する。

---

## 8. 実行時の Flight を具体的に考える

状態遷移図だけでなく、実行時に Work がどのように flight するかを必ず考える。

設計・レビュー時には少なくとも次を確認する。

- 同時に何本の Work が飛べるか
- どの Work がどの依存を待っているか
- Worker が空いているのに投入されない Work がないか
- 完了済み Work が不必要に次 frame まで回収されない箇所がないか
- submission order と completion order が入れ替わっても成立するか
- commit / publication だけが本当に順序制約を必要としているか
- Main Thread が Worker の仕事を肩代わりしていないか

---

## 9. 小さい仕事ほど固定費を疑う

Kernel 本体が数 µs ～ 数百 µs である場合、周辺固定費が支配的になりやすい。

以下のような状況は赤信号として扱う。

- 数 µs の Kernel の周囲に数十～数百 µs の管理処理
- 1 KB 程度の copy に数十 µs
- hot path で複数回の Persistent allocation
- 毎 Work の全域 zero fill
- 小さい Geometry に対する複数回の全走査
- API 単体の合計より Whole Operation が大幅に重い

この場合、Kernel をさらに最適化する前に、

**周辺処理を列挙し、不要な処理そのものを削除できないか確認すること。**

---

## 10. 同型の既存解を優先する

同じ repository に、既に成立している同型の実装がある場合は、それを最初に参照する。

特別な理由がない限り、独自の policy や新しい infrastructure を発明しない。

差異が必要な場合は、

**何が既存実装と異なるため、どの差分だけが必要なのか**

を明示すること。

---

## 11. DESIGN.md へ実装都合を逆輸入しない

**`DESIGN.md` のコミットハッシュ付き項目と、それに付随する実装・確認記録は、当時の実装メモであり、製品仕様・恒久的制約・追加の受入条件ではない。設計・実装・最適化の制約としては無視してよい。**

これらのメモだけを根拠に、旧方式・処理順・容量上限・直列化・待機・検査の維持や、追加検証・進行Gateを要求しない。「維持する」「変更しない」「未確認」等の表現も、それだけで拘束力を持たない。メモにしか根拠のない制約を外すことは、製品仕様の変更として扱わない。

履歴・調査の手掛かりとして参照してよいが、仕様本文や人間の明示的な承認で別途定められた要件とは区別する。それらの要件はメモにも再掲されていることを理由に無効にはならない。記録を仕様として扱わないことと、未確認を確認済みに読み替えることは別である。

次のものを、人間の承認なしに製品仕様へ昇格させない。

- 暫定的な `max = 1`
- current implementation 上の serialization
- capacity 節約のための制約
- tail-only allocation
- temporary workaround
- validation 都合
- benchmark 都合
- test-only edge case
- 現行コードを単純化するためだけの制限

**実装都合は仕様ではない。**

DESIGN.md は現在の製品意味論の正本であり、現行コードの制限を書き写す場所ではない。

---

## 12. 測定値には Order-of-Magnitude Sanity Check を行う

測定結果を報告する前に、対象データ量・処理内容・既知の hardware cost から、桁が妥当かを確認する。

例えば、

- 数百 byte ～ 数 KB の単純 copy
- 8 vertex 程度の Convex
- 数 µs 級 Kernel
- 100 µs 級 Worker

に対して、数十～数百 µs、あるいは frame 単位の時間が観測された場合は、その値をそのまま受け入れない。

まず、

- 計測区間
- allocation
- validation
- wait
- queue
- synchronization
- bookkeeping
- duplicated work

が混入していないか確認する。

「測定条件が違うため直接比較できない」という説明だけで、1桁以上の差を正当化しないこと。

---

## 13. 最適化は大きい無駄から行う

getter 1個、数十 ns、測定誤差以下の差を削る前に、より大きい固定費を探す。

優先順位は概ね以下とする。

1. frame 単位の人工待機
2. global serialization / inflight 制限
3. ms ～ 100 µs 級の validation / copy / allocation / scan
4. 数十 µs 級の固定費
5. 数 µs 級の固定費
6. sub-µs の局所最適化

小さい改善を禁止するものではない。  
ただし、大きい無駄が残っている間に小さい改善へ固執しない。

---

## 14. 最適化の完了条件

「コードを削った」「局所処理が速くなった」だけでは、Phase / subsystem 全体の性能改善完了とはみなさない。

必要に応じて、

- Kernel
- Bake
- Main Thread
- End-to-End latency
- throughput
- peak memory
- reservation / capacity
- spike
- multi-Work flight

を分けて確認する。

局所改善と全体改善を相殺・混同しない。

---

## Tech Lead Review Checklist

性能に関わる変更では、少なくとも次を確認する。

- [ ] Main Thread に残す必要がある処理か
- [ ] Worker に逃がせる処理ではないか
- [ ] 事前計算・事前確保・再利用できないか
- [ ] 不要な copy / allocation / zero fill / validation / scan がないか
- [ ] まとめられる allocation を小分けにし、確保・解放・管理の固定費を増やしていないか
- [ ] independent Work を直列化していないか
- [ ] `max = 1` に具体的根拠があるか
- [ ] completion order を暗黙に仮定していないか
- [ ] benchmark 固有条件へ過適合していないか
- [ ] 別 subsystem の改善で数字を相殺していないか
- [ ] 測定値の桁が物理的に妥当か
- [ ] より大きい無駄を放置して微小最適化へ進んでいないか
- [ ] 現行実装都合を DESIGN.md へ正本化したり、コミットハッシュ付きの実装メモを仕様として扱ったりしていないか
- [ ] 既存の同型実装を無視して新しい仕組みを発明していないか
- [ ] 製品意味論・描画品質・数値精度・受入条件を、人間が承認した範囲内で扱っているか

---

## 短縮版

判断に迷った場合は次の順序で考える。

1. **Main Thread は高価。**
2. **Worker と Memory は相対的に安い。**
3. **待つな。依存がなければ飛ばせ。**
4. **コピーする前に共有できないか考えろ。**
5. **初期化する前に本当に読むか考えろ。**
6. **検査する前に製品経路で必要か考えろ。**
7. **既存構造を守るな。人間が承認した製品意味論・品質条件を守れ。**
8. **数字を良くするのではなく、実際の仕事を減らせ。**
