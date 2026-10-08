# Quest 3S: Vulkan nativeカラーの準備分離

2026-10-09 JST。専用worktree `%USERPROFILE%/src/zantetsuken-vr-quest3s`、branch `quest3s-appsw`。

後続の[通常物体／Clip direct描画の分離](quest3s-vulkan-clip-route-split.md)で、通常VSのClip処理を除去した。以下はその前段のカラー準備分離の記録。

## 結果

カラーのconstantコピー、resource barrier、descriptor準備をURP opaquesより前へ移し、native描画をUnityのカラーpass内に記録する経路を実装した。Unity opaques、VP native、Skyboxが1個のVulkan render passにまとまった。また、従来のnativeイベントの事前条件が作っていた空passを影・motion・カラーから取り除いた。

| 項目 | 元の実装 | 最終APK: 従来カラー経路 | 最終APK: 準備分離経路（既定） |
| --- | ---: | ---: | ---: |
| graphics render pass開始数（空pass込み） | 10 | 7 | 5 |
| 描画を含むgraphics render pass | 7 | 7 | 5 |
| 空graphics render pass | 3 | 0 | 0 |
| Shadow / Motion / Colourのpass数 | 3 / 3 / 4 | 2 / 2 / 3 | 2 / 2 / 1 |
| VP multi-draw API呼び出し／frame | 3 | 3 | 3 |
| Compute dispatch／frame | 9 | 9 | 9 |
| GPUリプレイ時間（各9 samples） | 前回63.623 ms | 58.884222 ms | 37.902556 ms |

最終APK内の比較ではリプレイ時間が約35.6%短くなった。XR poseには差が残るため、短縮分の全てをpass統合の効果とは断定しない。元の実装の時間は以前の別キャプチャであり、同一APK比較には含めない。

**45fpsの22.22 ms予算にはまだ届かない。** この時間はQuest上でキャプチャを再実行したGPU時間であり、通常アプリfpsやAppSWの表示周期ではない。nativeの詳細hardware countersは今回取得しておらず、attachment帯域や各処理への時間の割当ては未測定。

前回の[Unity Mesh比較](quest3s-renderdoc-mesh-vs-native.md)で総数を7と記載していた点を訂正した。RenderDocの `passes.json` は描画のないpassを省略するため、今回は `events.json` の `vkCmdBeginRenderPass2` も全数数えた。元の総数は10、描画を含むpassは7。

## 実装

- `VpNativeDrawRoute.BodyPass` のVulkanカラー経路を `BeforeRenderingOpaques` の準備passに変更。GPU globalsのconstantコピーとnative Prepareイベントを記録する。準備passはカラー／depth attachmentを解決・読み出さず、RenderGraphによる早期の空clear passを避ける。
- 新しい `ColourDrawPass` は `AfterRenderingOpaques` のraster pass。カラー／depth attachmentを宣言し、PreparedDrawイベントだけを発行する。ここでcompute dispatch、AccessBuffer、texture transitionは行わない。
- native Prepareはresource access/barrier、descriptor生成、constant uploadを実行。PreparedDrawは現在のUnity recording stateを再取得し、同一frame／serialを検証してbindとmulti-drawを記録する。準備時のrecording stateへのポインタを保持しない。
- Prepare失敗時にも共有blockをconsumeしない。後続drawがエラーを返すか、camera completion sentinelが破棄を証明するまでblockを保持する。準備済みdescriptorはUnityのsafe frameで退役し、期限切れblockを参照しない。
- combined Drawイベントのrender-pass事前条件を `EnsureInside` から `DontCare` へ変更。Prepareがすぐ終了させる空passを事前に開かない。PreparedDrawは `EnsureInside` を使用する。
- Shadow／AppSW motionは従来の場所でconstantコピーと描画を行う。特にmotionのprevious/current stereo VPとNDC globalsを読む時点を維持した。独自queue submit／waitは追加していない。C ABI versionと既存構造体サイズは維持。
- Windows D3D12経路は従来動作。新しいnativeイベントIDはAndroid playerのVulkan経路で取得する。

`nativeColourPreparation=true` が既定値。比較用に `Run-StaticDemo.ps1 -LegacyNativeColour` で旧カラー経路へ切り替えられる。このスイッチでも空passを除去する共通修正は有効。

## 比較条件とキャプチャ

- Quest 3S、Unity 6000.3.22f1、Vulkan / SinglePassMultiview、`tiled_seed14`。
- 同一Development IL2CPP APK `Build/quest-colour-prepare-v2-dev.apk`。Build GUID `dd7ab230899d4c63920b5c758fd71b79`、SHA256 `D6957E56A96D57665A86C70154734868463E886CF9681F5673BEE135EDB6952E`。
- カラー1680×1760×2 / MSAA4、AppSW motion/depth 420×440×2 / MSAA1、影1024×1024 / 1 cascade。Quest_Rendererの `m_UseNativeRenderPass=0` を維持。
- 比較用path `start,99,1.7,-32,180`、`hold,40`、headTracked=false。appSw=true、Profiler無効、warmupLaps=0、keep open。撮影ごとのXR poseには差がある。
- G=1 / B=1、Main affinity 0x3c、Geometry nice=Main+3、Background nice=Main+6を維持。
- RenderDoc for Meta Quest v68.18（fork v1.41）。両方 `degraded=false`、時間測定 `measurement_succeeded=true`、samples=9。

| 最終APKのdraw arguments | 従来カラー | 準備分離 |
| --- | ---: | ---: |
| 提出triangle数（indices/3×instances） | 363,621 | 363,905 |
| ShadowのVP nonzero entries | 109 | 110 |
| MotionのVP nonzero entries | 160 | 159 |
| ColourのVP nonzero entries | 160 | 159 |
| VP indirect entries総数 | 4,806 | 4,806 |

triangle差は284、約0.078%。これはクリップ後のhardware triangle数ではない。Shadowのentry1300が0→1（508 triangles）、Motion／Colourのentry336が1→0（各112 triangles）という差があり、その他のVP entriesのtriangle／instance argumentsは一致した。約91%のzero-instance entriesは残り、indirect countによる圧縮は未実装。

準備分離のpass begin–endはShadow 49–181 / 193–1808、Motion 1813–2034 / 2051–3665、Colour 3697–5517。最終カラーをslice0／slice1で書き出し、両眼の街・木・建物・影・空を目視確認した。XR姿勢が違うため画素一致検証ではなく、motion/depth/stencilの精度検証を完了したという意味でもない。

## 検証

- ARM64 native libraryをwarnings as errorsでビルド成功。最終Development IL2CPP APKのビルドと実機インストール成功。
- 既存native data／pointer契約のEditMode tests: **24/24 Passed**。`Logs/quest-colour-editmode.xml`。
- Android standalone contract probeを実機で実行。ABI／parser／malformed input／イベント範囲に加え、Prepare失敗時の未consume、serial更新後の古いdescriptor拒否、draw時のversion拒否、破棄されたPrepareの安全な退役を確認。結果 `paired preparation lifetime: passed`。このprobe実行は最初の準備分離版で、最終版の追加変更はイベント事前条件とRenderGraph attachment宣言のみ。
- 最終版の実機では描画拒否、ring不足、pipeline failure、未実行event、再使用によるresult紛失はいずれも0。native multi-drawは各frameのshadow／motion／colourの3回を維持。
- 撮影後にRenderDoc GPU debug layer設定が解除されていることを確認し、通常のheadモードへ戻した。PID8554、nativeColourPreparation=True、msaa=4、native available=true／refused=0。
- 復帰後ログは `AppSWApplied=True`、VrApi `ASW=0` と後続の `ASW=14` を記録。fpsは観察範囲で26–28/90。ASWの非ゼロ値は確認できたが、45fpsの安定動作やAppSWの画素品質を証明する測定ではない。制御した性能比較には使わない。

## 保存と再現

`Logs/renderdoc-colour-prepare/` はignored。scene素材を含むRDCとAPKはGitへ追加しない。

- `legacy/legacy-msaa4.rdc`: frame943、154,829,962 bytes。
- `prepared/prepared-msaa4.rdc`: frame1619、159,628,935 bytes。
- 各ディレクトリのinfo／passes／events／draws／pipeline／summary JSON、shadow／motion／motion-depth／colour PNG。preparedには `colour-eye0.png` と `colour-eye1.png` も保存。
- `legacy-frame-time.log`／`prepared-frame-time.log`、v2のcapture／launch／device logs、config／path、`comparison-summary.json`、`restored-head.log`。
- `scripts/` にPython SDK解析スクリプトを保存。中間v1キャプチャは `legacy-v1/`／`prepared-v1/` に保管し、最終比較と分けている。

```powershell
# 通常の新経路（head tracking）
Tools/Quest3s/Run-StaticDemo.ps1 -Mode vp3c-native -CameraMode head -WarmupLaps 0 -KeepOpen
# 従来カラー経路を選ぶ場合は -LegacyNativeColour を追加

$env:RENDERDOC_CMD_PATH = 'C:\Program Files\RenderDocForMetaQuest\renderdoccmd.exe'
$env:PATH = 'C:\Program Files\RenderDocForMetaQuest\plugins\android;' + $env:PATH
& $env:RENDERDOC_CMD_PATH adb-frame-time --device=340YC10G7D0FZ9 --capture=Logs/renderdoc-colour-prepare/legacy/legacy-msaa4.rdc --samples=9
& $env:RENDERDOC_CMD_PATH adb-frame-time --device=340YC10G7D0FZ9 --capture=Logs/renderdoc-colour-prepare/prepared/prepared-msaa4.rdc --samples=9
```

## 残る作業

ShadowとMotionはそれぞれUnity／nativeの2passのまま。さらに3passへ近づけるには、それぞれのresource準備を前へ出す必要がある。Shadow定数の取得時点と、XRDepthMotionPassが設定するcurrent／previous／NDC globalsの取得時点を検証しながら進める。カラーと同じ移動をそのまま適用するとAppSWの値を早く読み過ぎる可能性がある。

45fpsを目指す次の候補は、この残るpass分割の削減、可視indirect entriesの圧縮、storage buffer頂点参照／ClipDistanceの個別計測。今回の変更はカラー準備分離と空pass除去まで。
