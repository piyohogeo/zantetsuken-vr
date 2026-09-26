# 製品 SlashWave Core（U6 / Phase 4.50）の作業記録

2026-09-27。基点 main `c5ee4917`。

- この文書は作業記録で、DESIGN.md は変更していない。
- 計測日誌を DESIGN.md へ追記しない。

## 1. 生存 Wave の固定容量＝4

- **根拠**: 保存済みの実機列 `20260918-153438-normal`（7056 update、44 Latch）では、同時に生存した Wave は最大 2 本だった。U6 で製品 Core を通して再生した際にも、同じ 2 本を確認した。
- **扱い**: 余裕を見て 4 を固定容量とする。可変設定・待機 Queue・追い出しは設けない。
- **格納の規則**: 19.1.6 と T-035 に従う。
  - 公開直前にだけ空きを確かめ、Stroke Begin では予約しない。
  - 満杯のときは、その Stroke の Latch だけを見送る。
  - 同じ Stroke を後から発射し直すことはしない。

## 2. 製品へ移した責務と Sandbox に残した責務

**製品（`Zantetsu.Core`、`Runtime/Core/Slash/`）**

- `SlashWaveCore` は 1 つの入口 `Update(device sample, view, trackingSpace)` を持つ。1 回の更新で次を行う。
  - Expire と容量の返却。
  - Gesture：履歴、Edge Gate、accepted samples、Stroke Begin の view 確認。
  - Latch：公開直前の容量確認、SlashId の発行、world への確定。
  - 生存 Wave の更新：Live／Frozen Guide、候補、Close、Accepted Span の running max、Segment、この更新の Sweep。
- **交換境界**は `ISlashLatchEstimator`、`ISlashFrameEstimator`、`ISlashSpanCandidateEstimator`、`ISlashSpanCloseEstimator` の 4 つ。それぞれ、採用済みの第一候補を実装として 1 つだけ持つ。
  - `EmitterChordLatch`（0.35 m）
  - `FixedSpanAngleFrame`（150°）
  - `GuideRaySpanCandidate`（近平行 1e-3）
  - `CaptureTimeoutSpanClose`（0.25 s）
  - 速度と寿命は `SlashWaveFlight`（12 m/s、1.5 s）、Gesture の値は `SlashGestureSettings.Adopted`。
- **各 Wave が保持するもの**: Latch 時の Candidate・Close の推定器インスタンスと、Flight（速度・寿命）。
  - 現在の推定器や設定を差し替えても、影響するのは以後の Wave だけ。
  - Latch と Frame の推定器は、未 Latch の Stroke の評価に即座に反映される。
- **SlashId**: Trace と同じ `long`。1 から発行し、再利用しない。
- **U7 向けの出力**: `SweepCount`／`SweepAt` で、SlashId、固定の面・軸、前回／現在 Segment を取り出せる。
  - Latch した更新では、退化 Sweep（初期 Segment を 2 つ並べたもの）を 1 つ出す。
  - Expire した Wave は、その更新の Sweep を出さない。

**Sandbox（`SandboxRightHandKatana` ほか）**

- 担当する責務: デバイスの読み取り（右手 grip pose）、tracking space の指定、刀と Wave quad の表示、調整値（Inspector と開発 UI）の保持と Core への反映、Capture、Recorder の再生、診断表示、Current／Pinned 比較。
- 既存の内部 API はすべて Core の結果を読む。
- `SandboxSlashWaveStore` は削除した。並存するロジックはない。

## 3. 入力再生

- Recorder と input.csv が記録・再生するのは、Gesture の入力（tracking space の device pose と view）だけで、人工移動を含む world 軌跡は含まない。
- 再生では、各サンプルの時刻を「再生開始時刻＋記録時刻の差分」とし、その時刻を Wave の LatchedAt と更新時刻にそのまま使う。
