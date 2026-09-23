# IL2CPP order diagnostic: independent findings (2026-09-24)

順序変更を製品へ採用する根拠は、この4プロセスの測定からは得られない。Objectsの正負差はbuild順へ明確に追従するが、処理を後側へ移すこととpair全体の短縮は別である。SetActiveの側別費用も配分が変わる一方、Build+Publish合計の改善はケース・プロセスを通じて一貫しない。

対象は `player00`, `player11`, `player10`, `player01`。各runにbox/character、4mode、各8測定round、別枠warmupがある。`summary-order-final` の集約と元 `order.csv` を独立に照合し、Build+Publishを同じsampleのspan0+5で加算した後、同roundのmode0との差を求めた。24組（4run × 2case × 3変更mode）についてelapsedとcyclesの再計算が一致した。測定sampleはいずれもpublished/validated/cleaned=1、12 spansのcount=1。warmupは以下の数値に含めない。プロセスを混ぜてsample中央値を作っていない。

mode0はbuild正先・activate負先、mode1はbuildのみ逆、mode2はactivationのみ逆、mode3は両方逆。以下の「kcycles」はthread cycles / 1000であり、時間換算ではない。elapsedは経過時間であり、純CPU時間ではない。

Objects/newRootの側差は、elapsed・cyclesの双方で16/16比較（4run × 2case × activation固定の2組）すべてにおいてbuild反転で符号が反転し、先にbuildする側の方が高い。各run・case・modeの8sampleから得た「先側−後側」の中央値の範囲は次の通り。

| Span | Case | elapsed差の範囲 (us) | cycles差の範囲 (kcycles) |
|---|---|---:|---:|
| Objects | box | +8.30 .. +10.30 | +17.52 .. +21.76 |
| Objects | character | +8.80 .. +10.80 | +18.61 .. +22.84 |
| newRoot | box | +2.05 .. +3.05 | +4.43 .. +6.56 |
| newRoot | character | +2.40 .. +3.25 | +5.12 .. +6.78 |

したがって「positive固有のmanaged処理だから遅い」とする説明は支持されない。newRootにも順序差があるが、それだけでObjects差の全部は説明できない。同sampleのObjectsからnewRootを引いた残部についても、先側−後側のrun/mode中央値はboxで+5.90..+7.65 us、characterで+6.05..+7.85 us残る。これは診断上の差分であり、入れ子probeの費用は差し引いていない。残部のUnity内部処理・managed処理・計測費用への帰属は未確定。

SetActiveの正負差はactivation反転でも16/16比較すべてで符号が反転しない。両順序ともpositiveの方が高い。mode0→mode2ではpositiveの費用が増え、negativeの費用が減る。以下は各modeの4プロセス中央値の中央値。pair欄は同sample内のspan6+7を先に足してから中央値を求めており、子の中央値の和ではない。

| Case / metric | SetActive+ mode0→2 | SetActive- mode0→2 | pair mode0→2 |
|---|---:|---:|---:|
| box / us | 13.55 → 19.13 | 9.33 → 3.15 | 22.75 → 22.30 |
| box / kcycles | 28.93 → 40.74 | 20.06 → 7.00 | 48.76 → 47.78 |
| character / us | 26.90 → 33.65 | 17.98 → 11.23 | 45.20 → 44.90 |
| character / kcycles | 57.14 → 71.38 | 38.27 → 24.10 | 96.14 → 95.35 |

この比較ではJointは常にpositiveに存在する。positiveを先にactivateすると、Joint接続先negativeはまだinactiveである。characterのcollider数もpositive=15 / negative=9で異なる（boxは1/1）。したがってSetActiveの正負差を「最初の呼出しの費用」だけに帰属できず、Objectsの順序追従と同じ現象として扱わない。

Build+Publishのmode0比は次の通り。各プロセス内で8組の同round差（変更mode−mode0）を求め、その中央値を1プロセスの値とした。表の中央は4プロセスの値の中央値、括弧内は4プロセスの範囲。負値は短縮、正値は増加である。

| Case | Mode | elapsed差 中央 (範囲), us | cycles差 中央 (範囲), kcycles |
|---|---:|---:|---:|
| box | 1 | +3.15 (-0.65 .. +4.35) | +6.54 (-1.74 .. +9.42) |
| box | 2 | +0.73 (-1.05 .. +2.65) | +1.15 (-1.87 .. +5.58) |
| box | 3 | -1.18 (-3.15 .. +0.60) | -1.78 (-6.84 .. +0.66) |
| character | 1 | +2.43 (-3.45 .. +4.85) | +4.07 (-7.05 .. +10.92) |
| character | 2 | +2.38 (-9.10 .. +4.15) | +4.79 (-18.19 .. +8.00) |
| character | 3 | +0.85 (-0.70 .. +2.15) | +1.95 (-1.90 .. +4.29) |

mode3はbox elapsed/cyclesで4run中3runが短縮したが、characterではelapsedは2/4、cyclesは1/4にとどまる。mode0自体のBuild+Publish中央値はboxで78.95..87.85 us（166.99..185.77 kcycles）、characterで124.30..132.70 us（262.51..279.98 kcycles）のプロセス間幅がある。順序反転を一律の高速化として採用せず、元順序を維持する判断が妥当である。有意差・因果機構の確定は主張しない。

これは局所TryBuild/TryPublishの診断であり、入力setup、完全なRequest、Worker、描画、Final handoffを含まない。同round比較も別sample/別時点の測定である。外れ値除外、empty-span一括差引、warmupとの混合は行っていない。

証跡: [run summary](../../docs/diagnostics/phase41-main-thread-order-data-2026-09-24/summary-order/run-summary.csv)、[同round mode差](../../docs/diagnostics/phase41-main-thread-order-data-2026-09-24/summary-order/run-mode-comparisons.csv)、[順序追従](../../docs/diagnostics/phase41-main-thread-order-data-2026-09-24/summary-order/run-order-tracking.csv)、[側差](../../docs/diagnostics/phase41-main-thread-order-data-2026-09-24/summary-order/run-side-differences.csv)、[検証・source hashes](../../docs/diagnostics/phase41-main-thread-order-data-2026-09-24/summary-order/validation.json)。4run共通のsource fingerprintは `ac74cac748fd6e6ebd6d35c6cdd90d5b116a231b61a1104ebdec526eee938e26`。元測定は [player00](../../docs/diagnostics/phase41-main-thread-order-data-2026-09-24/raw/player00/order.csv)、[player11](../../docs/diagnostics/phase41-main-thread-order-data-2026-09-24/raw/player11/order.csv)、[player10](../../docs/diagnostics/phase41-main-thread-order-data-2026-09-24/raw/player10/order.csv)、[player01](../../docs/diagnostics/phase41-main-thread-order-data-2026-09-24/raw/player01/order.csv) に保存されている。
