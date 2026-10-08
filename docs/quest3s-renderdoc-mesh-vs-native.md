# Quest 3S: Unity Mesh / VP3C-native RenderDoc比較

2026-10-09 JST。worktree `%USERPROFILE%/src/zantetsuken-vr-quest3s`、branch `quest3s-appsw`。実装は `ac94a020`。

## 結果

同じAPKで両方式をキャプチャし、Quest 3S上でリプレイした。CPUから発行する描画API数は減ったが、今回のGPUリプレイ時間はVP3C-nativeの方が長かった。

| 1 captured frame | Unity Mesh | VP3C-native |
| --- | ---: | ---: |
| フレーム全体のGPUリプレイ時間（各9 samples） | 16.360667 ms | 63.622778 ms |
| Vulkan描画API呼び出し数 | 602 | 198 |
| 上記のうちVPネイティブmulti-draw | 0 | 3 |
| RenderDocのdraw action数 | 602 | 5,001 |
| Vulkan graphics render pass数 | 3 | 7 |
| Compute dispatch数 | 0 | 9 |
| VP indirect entry数 | — | 4,806 |
| VP entryのうちinstance数 > 0 | — | 407 |
| VP entryのうちinstance数 = 0 | — | 4,399（91.53%） |

GPUリプレイ時間の比は約3.89倍。`adb-frame-time` の `measurement_succeeded=true`、`samples=9` を確認。これはキャプチャを再実行したGPU時間であり、通常アプリのfps、CPUフレーム時間、AppSW生成フレームを含む表示周期の測定ではない。以前のヘッドトラッキング計測（App GPU平均37.67ms）とは視点・計測方式が異なるため混ぜない。45fpsの予算は22.22msであり、nativeの今回のリプレイはこの予算を大きく超えている。

描画API呼び出し数もCPU所要時間そのものではない。native化でCPUが何ms短縮されたかは、この比較からは算出できない。

## 比較条件

- Quest 3S / Adreno 740、Unity 6000.3.22f1、Vulkan / SinglePassMultiview。
- `tiled_seed14`、同一Development APK。Build GUID `77fff344316f48da99f4b707449f5e12`、SHA256 `1304B74C116ACF114108F4F94A00AC001D640C986367BD58A6C6425E8C899366`。
- カラー1680×1760×2 / MSAA4、AppSW motion/depth 420×440×2 / MSAA1、影1024×1024 / 1 cascade。
- 同じQuest_Renderer設定 `m_UseNativeRenderPass=0`。以前のMesh計測で使った設定1との比較ではない。
- `appSw=true`、Unity Profiler無効、warmupLaps=0、keep open。撮影前にアプリが起動し、シーンが表示されている状態を待った。
- 比較用pathは `start,99,1.7,-32,180`、`hold,40`。`headTracked=false` とし、ユーザーにQuestを水平・静止させてもらって別々に撮影した。
- XRのposeには小さな違いが残った。画素ごとの同一視点検証には使わない。
- G=1 / B=1、Unity Main affinity 0x3c、Geometry=Mainよりnice値+3、Background=Mainより+6という既存設定を維持。
- RenderDoc for Meta Quest v68.18（forked from v1.41）。詳細プロファイリングを有効にした実機リプレイ。通常起動の電力・クロック条件と同一とは扱わない。

## draw actionとCPU呼び出しの区別

nativeの5,001 actionsの内訳は、Unity側195 draw actionsと、3個の `vkCmdDrawIndexedIndirect(1602)` に含まれる4,806子actions。

**CPU描画API呼び出しは195+3=198回**。Unity側195にはinstance数0の `vkCmdDrawIndexedIndirect(1)` が1回含まれる。VP部分は影・motion・カラーの各1回で合計3回。場面全体が3回という意味ではない。

| VP multi-draw | 親event | entries | instance数 > 0 | 提出triangle数（instance数を反映） |
| --- | ---: | ---: | ---: | ---: |
| Shadow | 228 | 1,602 | 91 | 56,008 |
| AppSW motion | 2,086 | 1,602 | 158 | 154,452 |
| Colour | 3,929 | 1,602 | 158 | 154,452 |

可視entryを詰めたcommand countではなく、全1,602 entriesを各passに渡している。`vkCmdDrawIndexedIndirectCount` は使用していない。instance数0はラスタライズ対象を作らないが、残ったcommand列の処理コストが何msかは未測定。

triangle数はdraw argumentsのindices/3×instancesを合計した値で、クリップ後の実ラスタライズ数や両眼合計のhardware counterではない。RenderDoc `stats.json` のnative triangle合計はinstance数0のentryも数えるため、そのまま実負荷として使わない。フレーム全体はUnity364,605 / native373,545 trianglesで約2.5%差があり、poseと選別方式の差も含む。

## 描画パスの分割

Unity Meshは影、motion、カラーの3 graphics render passes。

| VP3C-nativeの順序 | begin–end events | load操作 | draw actions |
| --- | --- | --- | ---: |
| Unity shadow | 49–201 | D=Clear | 39 |
| Native shadow | 220–1,835 | D=Load | 1,602 |
| Unity motion | 1,840–2,053 | C=Clear, DS=Clear | 78 |
| Native motion | 2,078–3,691 | C=Load, DS=Load | 1,602 |
| Unity colour | 3,700–3,881 | C=Clear, DS=Clear | 77 |
| Native colour | 3,920–5,534 | C=Load, DS=Load | 1,602 |
| Skybox | 5,540–5,558 | C=Load, DS=Load | 1 |

nativeの `passes.json` は11グループを返すが、4グループはcompute等のrender pass外区間である。Vulkan graphics render passは7個。

実装のnative eventで `EnsureOutsideRenderPass` → resource access/barrier → `EnsureInsideRenderPass` を使い、キャプチャでもpassの終了・再開とattachment loadが増えた。attachmentの退避・再読込がGPU負荷を増やす可能性がある。ただし、今回のカウンターでは帯域や時間への寄与をnative側で定量化できていない。

## シェーダーと画面

同じ大きな建物（32,976 indices、10,992 triangles）をUnity event1291 / native event4201で比較。

- 両方ともBack cull、CW、depth test/write有効、Greater Equal。
- native VSは4本のstorage bufferを参照し、SPIR-Vに `Capability(ClipDistance)` と `float[8] ... BuiltIn(ClipDistance)` が存在。Unity Meshの代表VSにはClipDistance出力がない。頂点参照・クリップ処理は別途調べる候補となる。
- `get_shader` のencoding表示はglslでも、保存された `.glsl` の内容はRenderDocのSPIR-V disassembly。ソースGLSLとして再コンパイルしない。
- 両方式で街、木、建物、影が見える。大きなgeometry欠落は目視では見つからなかった。native画像には視点の小さなずれがある。
- shadow、motion、motion-depth、最終カラーをそれぞれPNGで保存。motion/depthの両眼attachmentは存在し、今回のPNGはslice0。
- 静的シーンのmotion PNGはUnityがRGB=0、nativeが最大1/255の微小値。depthは両方とも最大2/255の暗い画像だが、非ゼロ値が存在する。reversed-Z depthの8bit可視化が暗いことを欠落と判断しない。
- これらのPNGは精度を落とした可視化であり、AppSW motion/depth/stencilの画素精度検証や、ランタイムでAppSWが実際に生成・表示したフレームの検証は完了していない。

## カウンター取得の限界

Unity MeshはPIL drawcall traceを3回取得でき、602 draws×12 countersが埋まった。nativeはAPI上success=trueでも `traces_run=0`、`actual_samples=0`、全5,001 recordsの `metrics={}`。**nativeの詳細GPUカウンターは未取得**。

nativeのGPU Duration / VS Invocations / PS Invocationsを別の `fetch_metrics` 経路で1回試したが、150秒でタイムアウトした。空のmetricsや初期値のLRZ state=0を、GPU稼働率0・LRZ無効の測定結果として使わない。フレーム全体の時間測定は両方式とも成功しており、この詳細カウンターの失敗と区別する。

## 次に調べる候補

1. native event前後のrender pass分割を減らす実験。attachment load/storeとフレームGPU時間が減るかを見る。
2. 可視indirect entriesの圧縮、および対応featureを確認した上でindirect countを使う実験。91.53%のzero-instance entriesを除いた差を測る。
3. 代表VSのstorage buffer参照と8 ClipDistanceを個別に切り替え、同じposeでGPU時間を比較する。

いずれも原因確認の候補で、今回の3.89倍の差を特定要因に割り当てた結論ではない。この比較でアプリ／プラグインコードは変更していない。

## 保存場所と再現

`Logs/renderdoc-quest-compare/`（ignored）に以下を保存。scene素材を含むRDCはGitへ追加しない。

- `unity/unity-msaa4.rdc`（frame1503、86,646,277 bytes）、`native/native-msaa4.rdc`（frame1074、129,063,713 bytes）。
- 各方式の `passes.json`、`draws.json`、`events.json`、pipeline/bindings、VS/PS disassembly、PNG、`gpu-counters.json`。
- `unity-frame-time.log`、`native-frame-time.log`、capture/launch/device logs、config/path、`comparison-summary.json`。
- `scripts/` に今回使用したローカル解析スクリプトを保存。Python 3.8とRenderDoc付属 `MCP/renderdoc_mcp/renderdoc_api.py` を使用。

```powershell
$env:RENDERDOC_CMD_PATH = 'C:\Program Files\RenderDocForMetaQuest\renderdoccmd.exe'
$env:PATH = 'C:\Program Files\RenderDocForMetaQuest\plugins\android;' + $env:PATH
& $env:RENDERDOC_CMD_PATH adb-frame-time --device=340YC10G7D0FZ9 --capture=Logs/renderdoc-quest-compare/unity/unity-msaa4.rdc --samples=9
& $env:RENDERDOC_CMD_PATH adb-frame-time --device=340YC10G7D0FZ9 --capture=Logs/renderdoc-quest-compare/native/native-msaa4.rdc --samples=9
```

最初のcaptureはファイル転送段階のbare `adb` がPATHになくCOPY_FAILEDになったが、端末に生成されたRDCをRenderDoc付属ADBで回収し、両方のリプレイ `degraded=false` を確認した。キャプチャ起動時には両方式でVulkan loaderのinvalid-instance警告も記録された。詳細はlaunch logsに残し、degraded=falseだけで通常アプリと完全に同等の実行条件だとは扱わない。以降は上記PATHを設定。Unity付属ADBとのバージョン切替によるserver再起動を避けるため、RenderDoc付属ADBを統一使用する。


## 通常動作への復帰

RenderDocのGPU debug layer設定が解除されていることを確認し、-Mode vp3c-native -CameraMode head -WarmupLaps 0 -KeepOpen で再起動した。Questのシステムダイアログ解除後、PID20228でcamera=head、msaa=4、native available=true / refused=0を確認。AppSWApplied=TrueでもVrApiはASW=0,Type=Appを報告しており、通常動作でAppSW生成が有効になったという確認には至っていない。保存ログは Logs/renderdoc-quest-compare/restore-app.log。
