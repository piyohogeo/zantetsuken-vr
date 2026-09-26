# 人工移動と Slash Gesture の座標の扱い（U4）

2026-09-26。基点 main `070a0a65`（U4 未コミット）。

> **TL の判断（2026-09-26）：人工移動を Gesture の運動量から除外する。** 移動中の攻撃禁止や、snap ごとの履歴リセットにはしない。

- この文書は今回決めた座標の扱いの記録で、DESIGN.md は変更していない。
- DESIGN §19.1（Gesture・Latch・Frame）と §19.1.5.1（Span Guide）へ反映するかは、別の変更として判断する。

## 1. 経緯

- U4 の最初の実装は、刀の grip pose を現在の trackingSpace（XR Origin の Camera Offset）で world へ変換してから Gesture へ渡していた。
- そのため、人工移動・snap turn が振りの速度・変位に入った。
  - 手を静止したまま、刃の向きへ 2 回 snap すると 1 回 Latch した（誤発射）。
  - 振りの途中の snap で、Wave の平面が 56〜76° 曲がった。
- 観測記録：`C:\log\zantetsuken-vr\U4PlayerLocomotion\GESTURE.md`

## 2. 決めた扱い

| 対象 | 座標 | 備考 |
|---|---|---|
| Grip 履歴・速度・Edge Gate・accepted samples・Plane／Frame 推定・view 方向 | tracking space（device の空間） | 実際の手振りと実空間での移動は含む。人工移動は含まない |
| 刀の表示 | world | 現在の trackingSpace で変換する |
| Latch で確定する面・起点・軸・初期 Segment | world | Latch 時点の trackingSpace で、同じ変換を一度だけ適用する。振りの途中に人工移動しても振りは続き、現在の配置から発射する |
| 確定済み Wave | world | 後から移動・回転させない |
| Span Open 中の Live Guide | world | 現在表示している刀から作る（§19.1.5.1）。人工移動を除かず、Latch 時の配置にも固定しない。発射後の移動で Span が変わることは不具合ではない |
| Close 後の Frozen Guide | world | Close 時点の Live Guide のまま |

- **変換**: trackingSpace の位置と回転だけを使う。XR Origin は等倍なので scale は使わない。trackingSpace が未設定なら world と同じ空間とみなし、Latch の値もそのまま渡す。
- **live の順序**: `SandboxLocomotionInput` を `DefaultExecutionOrder(-100)` にして、刀（既定順 0）より先に動かす。「人工移動の適用 → 刀の Gesture 評価と Latch」の順になる。
- **記録・再生**
  - Recorder と Capture は、Gesture へ渡す tracking space の grip pose と view を記録する。
  - 再生すると、別の配置でも同じ受付・Latch になり、Wave はその時点の配置に置かれる。
  - Capture の `conditions.txt` には、入力が tracking space、Wave が world であることを書く。
  - 既存の記録（2026-09-18）は XR Origin が原点・無回転の状態で取ったので、両者は一致する。

## 3. 確認した回帰試験

`KatanaArtificialMovementTests`（EditMode）で次を確認している。

- 静止した手に snap・移動を繰り返しても、受付・Latch が生じない。
- 同じ手振りに移動・snap を加えても、update ごとの受付数と Latch の update が人工移動なしと一致する。
- Latch 時の配置を取り除くと、面・起点・軸・初期 Span・初期 Segment が人工移動なしと一致する。
- 発射済み Wave の固定値は変わらず、Span Open の Guide は表示中の刀から評価される。
- 動きながら記録した列を別の配置で再生すると、受付・Latch・tracking space での Wave が一致する。
- 刀の表示位置、tracking space での view、移動→刀の更新順。

既存の実機列（`20260918-153438-normal`、7056 update）の再生では、人工移動なしの 44 Latch が、連続移動と 1 秒ごとの snap の下でも同じ update・同じ値（Root 座標系で 0.00°）で維持された。これは証拠フォルダの観測で確認したもので、通常の試験には含めていない。
