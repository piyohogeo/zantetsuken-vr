# Quest 3S AppSW 距離制限の比較

## 条件と実装

2026-10-09、`quest3s-appsw` worktreeで実測。対象は現在の10体NPC Slashデモ。
街はUnity Mesh、切断後のNPCの断片はVP3C-native Vulkanで描画する。

- カラーのnear/farは **0.01m / 100m**、カラー1680×1760×2・MSAA 4x。
- AppSWは45fps → 90Hz、420×440×2・MSAA 1x、カメラ移動だけのMV。
- AppSpaceは修正済みの`tracking-origin`。HMDの追跡姿勢をAppSpaceの人工移動として送らない。
- AppSWの追加距離制限だけを **なし / 60m / 30m** に切り替える。
- ユーザー指定に従い **物体単位のboundsカリングだけ**。PSの距離判定・discardは追加しない。境界と交差する物体は全体を残す。投影行列、深度エンコード、MVシェーダーは既存のまま。
- Unity側はXRの元のカリング面に距離面を追加して再カリングし、AppSW renderer listを作成する間だけその結果を使う。その後、元の結果を戻す。
- VP側は既存GPUカリングで距離面を追加した別のvisible/indirect出力を作る。カラー・shadow用の選択バッファを上書きしない。
- Unity側のURP内部フックは17.3.0に限定した実験用の互換処理。PackageCacheは変更しない。`link.xml`でIL2CPPの反射対象を保存する。
- 起動オプション`AppSwFarMeters=-1`が従来の通常経路。実験は明示的に有効にしたときだけ動く。

## 計測

切断完了後（`measurement done code=0`, `sceneRetained=True`）のフレームを使用。
最初の視点固定はCamera Offsetを切断前から動かしてしまい、刀の追跡空間→ワールド変換にも影響した。
固定を切断完了後へ移した修正版で **全3条件を再撮影**。初期の`0/60/30`、`post-*`撮影は最終集計に使わない。

最終証拠は `Logs/appsw-far/final-0`, `final-60`, `final-30`。
保存したワールドのカメラ姿勢を再利用し、RenderDocのdrawcall profilingモードで取得。
固定GPU周波数492MHz。PILは各条件3回、全体GPU再生時間は各条件9回。
全条件でカウンターの`actual_samples=3`, `traces_run=3`, `consistent=true`を確認した。

| AppSW追加制限 | Unity物体draw数 | Unity投入インデックス数 | Unity MV draw換算ms | VP MV draw換算ms | 全体GPU再生ms |
|---|---:|---:|---:|---:|---:|
| 追加制限なし | 203 | 380,706 | 2.590 | 0.656 | 16.604 |
| 60m | 77 | 113,922 | 0.824 | 0.638 | 16.018 |
| 30m | 33 | 55,608 | 0.422 | 0.654 | 15.519 |

**時間の意味:** MV draw換算時間は各drawのPIL `Clocks`を492MHzで換算して合計した値。
render pass全体のタイムスタンプではなく、load/store・pass境界・computeは含まない。
drawの換算値は全体GPU時間と単純加算できない。全体GPU再生時間は別の計測であり、CPUやOpenXRコンポジターの時間を含まない。
投入インデックス数は`triangles × instances × 3`。背景のfullscreen triangleは表から除外した。
unique vertex数や実際のVS invocation数とは異なり、Vulkan multiviewの両眼分を掛けた値でもない。

### 比較の成立と限界

- Unityのカラー描画は全条件で **203 draws / 126,902 triangles**、Unity shadow描画の投入triangle数は **37,288** で同じ。
- 保存姿勢は全条件で同じ。GPUから取得したAppSWの前・現在両眼VP行列の最大要素差は、基準に対して60mで`4.6581e-5`、30mで`1.7881e-7`。小さな追跡・浮動小数の差はある。
- カラー画像の静的な街（左眼の上1050行）の8bit RGB平均絶対差は、60mで`0.001474`、30mで`0.000072`。カラーの街の配置は維持されている。
- 切断後のNPCの形状・落ち着いた位置にはケース間の小差がある。VP motionとVP colourは、**各ケース内で投入triangle数が一致**。基準54,540、60m53,254、30m53,522。近距離のVP断片は距離制限による削減が出ていない。
- UnityのAppSW投入インデックスは60mで約70%、30mで約85%減る。Unity MV draw換算時間は2.590 → 0.824 → 0.422ms。
- 全体GPU再生は16.604 → 16.018 → 15.519ms。NPCの形状差があるため、約0.59 / 1.09msの差の全てを距離制限の効果とは断定しない。
- 遠方のVPオブジェクトの削減率はこのNPCシーンでは測れていない。VPだけで街を描く静的シーンでの結果とは分けて扱う。
- Unityの追加CPUカリングとVPの追加GPUカリングのコストが発生する。CPUカリングには`Zantetsu.AppSwDistance.UnityCull`マーカーを用意したが、この比較ではCPUコストを個別計測していない。

## 見た目の確認

通常のヘッドトラッキングで、追加制限なし → 60m → 30mを比較。
遠くの信号機を見て頭を左右へ動かし、二重像・震えと境界付近の物体を確認する。
| AppSW追加制限 | ユーザーの実機確認 |
|---|---|
| 追加制限なし | 二重像・震えなし |
| 60m | 変化なし・問題なし |
| 30m | 変化なし・問題なし |

この視点・頭の左右移動では30mも画質の変化が報告されず、UnityのAppSW描画負荷を減らす候補になる。
全体GPU再生時間にはNPC形状差があり、追加CPUカリングの個別コストも未計測なので、採用時の判断はその点を含める。
比較後は`AppSwFarMeters=-1`, `AppSwFixedView=off`で通常設定へ戻した。実機ログで45fps・AppSW `Type=App`・`Stale=0`を確認（`Logs/appsw-far/live-restored.txt`）。

## 再実行

既に比較APKがインストールされたQuestで、worktreeから実行する。

```powershell
Tools/Quest3s/Run-NpcSlashDemo.ps1 -AppSwFarMeters 0 -AppSwFixedView off
Tools/Quest3s/Run-NpcSlashDemo.ps1 -AppSwFarMeters 60 -AppSwFixedView off
Tools/Quest3s/Run-NpcSlashDemo.ps1 -AppSwFarMeters 30 -AppSwFixedView off
# 従来の通常経路へ戻す
Tools/Quest3s/Run-NpcSlashDemo.ps1 -AppSwFarMeters -1 -AppSwFixedView off
```

数値用の視点保存は`AppSwFixedView=capture`、再利用は`reuse`。
いずれも切断完了後に固定する。見た目の確認には必ず`off`を使う。
追加制限なしはカラーfar100mまでの従来のカリング範囲であり、無限遠まで描画する指定ではない。

APK: `Build/quest-npc-appsw-far-dev.apk`。
SHA256: `A5AA4D613301C0089096FD3820AB869A7A182674DA187783E4C845AD50CDC6C8`。
ビルド診断: `Logs/appsw-far/build-pose-fixed.log`、集計: `Logs/appsw-far/summary.json`。
