# Quest 3S: 通常物体と即切断Clip描画の分離

2026-10-09 JST。worktree `C:/Users/%USERNAME%/src/zantetsuken-vr-quest3s`、branch `quest3s-appsw`。

> **2026-10-11 追記（分類の変更。現行の契約は DESIGN.md 4.5.9）。** 以下は当時の実装の記録である。現在は分類が**コマンド単位**になっている：`PlaneCount > 0` のinstanceを1つでも含むstable commandは、全instance（平面数0のものを含む）をdirectへ渡し、通常のmulti-drawには何も残さない。GPU選別はClip storageを読まず、束縛もしない（どのcommandを外すかはcull commandの記録が言う）。この変更後のQuest Vulkanの発行コードは実機でもEditorでも実行していない。

## 実装した経路

| 対象 | 選択・draw | VSの入力 | Clip |
| --- | --- | --- | --- |
| 通常物体 | GPU culling → 既存multi-draw | 頂点・visible list・instance transform（影は既存cull commandも） | バッファ読込・計算・出力書込をコンパイル時に除去 |
| 即切断表示 | CPUの少数リスト → `vkCmdDrawIndexed` | 頂点storage 1本＋小さなuniform | 最大8平面のClipDistance |

Quest Vulkan native経路が対象。`PlaneCount > 0` のinstanceだけdirectへ渡し、同じstable command内の通常instanceはmulti-drawへ残す。Unity Mesh／Unity VP3C／Windows D3D12の従来経路は従来の選択を維持する。

- Clipのinstance番号リストは構造変更・geometry commit・compaction時に採択前に準備してswapする。placement更新では現在のtransform／平面を参照し、全シーンを毎frame走査しない。
- 通常GPU選別からClip instanceをbody／casterとも除外する。Clip0ではcomputeのClip読込分岐も実行しない。
- directは間接argument、GPU visible list、instance transform storage、Clip storage、cull commandを参照しない。連続する同じgeometry buffer／materialを最大32物体のpacketにまとめ、pipeline／descriptorを一度bindして個別のdirect indexed drawを記録する。少数物体の追加CPU frustum選別は行わずhardware clipに任せる。
- uniformは32×208=6656 bytes。各recordはcolumn-major matrix64、signed planes128、count16。Quest SinglePassMultiviewではinstanceCount1、firstInstanceがrecord番号。DirectのSinglePassInstanced経路は未対応。
- 影・motion・カラーごとに通常とClipの全resource／descriptor／constantを先に準備し、group callbackから同一pass内に描く。別passを追加しない。レイアウトが一致するGPU globalsはコピー結果を共有し、物体ごとのcompute dispatchを増やさない。
- Clip0では追加packet、direct定数アップロード、groupイベントを発行しない。通常VSはClip pointerを取得・bindしない。indirect draw命令用argument pointerはVS bindingとは独立して取得する。
- Vulkan ringのconstant枠を16へ拡張（影付きdirect stereoは9枠必要）、payloadを64 KiBとした。PCは8枠／8 KiB。ベイクはCB／resource容量も検査する。
- ABI1のDraw240bytes／completion位置／Counters80bytesを保持。Vulkan draw version2でdirect／group extensionを付加する。inline childを全てコピーしてからrootを再利用し、rootだけをconsumeする。prepare失敗、serial更新、sentinelによる破棄、safe frame退役を検査した。

## AppSW

現在のClip後表面とdepthを再ラスタライズし、**同じ現在のworld positionを現在／前カメラVPで投影**する。前物体transformと物体移動MVを使わない。Clip direct motionにも既存のstencil設定を適用する。物体が動く場合のAppSW補間品質は今回の検証対象に含めない。

MSAA4、display90 Hz／AppSW45、G=1／B=1、Main affinity0x3c、Geometry nice=Main+3、Background nice=Main+6を維持。

## 実機確認

Unity6000.3.22f1、Quest3S／Adreno740、Vulkan SinglePassMultiview、`tiled_seed14`。Development IL2CPP APK `Build/quest-clip-split-v3-dev.apk`、BuildGUID `39804a549f02439cb94e731666570283`、SHA256 `D2144CE0B355A3AF3788DAF262EB6911012F2DF92EEB6C4463EBB285447EC0EF`。

同じAPKを通常Clip0と、camera前方の診断quad2個（全体保持／右半分保持）で撮影した。診断は既定無効。比較path `start,99,1.7,-32,180`／`hold,40`。XR poseには撮影間の差があるため性能のA/B測定とは扱わない。

| RenderDoc確認 | 通常Clip0 | 診断Clip2 |
| --- | ---: | ---: |
| graphics render pass開始数（空pass込み） | 5 | 5 |
| 影／AppSW motion／カラーpass数 | 2／2／1 | 2／2／1 |
| native multi-draw呼出し／frame | 3 | 3 |
| Clip direct draw／frame | 0 | 6（各stage2） |
| compute dispatch／frame | 8 | 9 |
| degraded | false | false |

1回の追加dispatchはcasterの異なる定数レイアウトに対応する共有コピー。カラー・motionのコピー回数は増えていない。direct eidsは影1841／1842、motion3708／3709、カラー5548／5549。各drawはindices6／instance1。両眼カラーで全体／半分の描画を目視確認した。

通常とdirectの影・motion・カラーVS6本を実機キャプチャから取り出し、ベイクしたSPIR-Vとのbyte一致を確認した。通常はClip buffer／ClipDistance capability／Clip出力memberアクセス0、storage数は影4／motion3／カラー3。directは各storage1＋6656B UBO。Unity共通gl_PerVertexには未使用のClip member宣言が残るが、通常VSからそのアクセス・書込・Clip capabilityは無く、Clip出力処理は無い。

ベイク全40 variant（既存14＋通常14＋direct12）を独立検査。代表colour VSは従来22696→通常13180 bytes、motion15404→7552 bytes、shadow27808→17056 bytes。これはshaderサイズの比較でありGPU時間の短縮量ではない。

最終EditMode tests **29/29 Passed**、failed0／skipped0。混在instance分類、候補採択／placement／compaction、GPU選別、direct／groupコピーとcompletion寿命を検証。ARM64 native buildはwarnings as errorsで成功。Quest standalone probeは全80 shader stagesとABI／malformed input／direct範囲／group重なり／prepare寿命を通過。

診断とRenderDoc layerを解除して通常headモードに復帰。PID18118、directClipProbe=False、MSAA4、AppSWApplied=True、native available=true／refused0。45fpsの安定達成を証明する測定は今回行っていない。実際の一時的な切断操作を含む長時間検証は別途必要。

## 再現と保存

```powershell
# 通常headモード
Tools/Quest3s/Run-StaticDemo.ps1 -Mode vp3c-native -CameraMode head -WarmupLaps 0 -KeepOpen
# Clip2診断を追加（性能測定用ではない）
Tools/Quest3s/Run-StaticDemo.ps1 -Mode vp3c-native -CameraMode head -WarmupLaps 0 -KeepOpen -DirectClipProbe
```

ignored `Logs/renderdoc-clip-split/normal/` と `probe/` にRDC、events／draws／passes／summary、VS binary JSON、両眼PNGを保存。probeには `vs-structure-verification.json`。`Logs/clip-shader-summary.json` に全variant検査、`quest-clip-contract-probe-v2.log` にnative probe、`quest-clip-split-final-editmode.xml/log` にtests、`quest-clip-restored-head.log` に復帰ログを保存。APK／RDC／scene素材はGitへ追加しない。

先行するpass準備の変更は[カラー準備分離](quest3s-vulkan-colour-pass-preparation.md)を参照。
