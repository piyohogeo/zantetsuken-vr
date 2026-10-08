# Quest 3S 静的シーン移植・初回実機計測

2026-10-09 JST。作業ブランチ `quest3s-appsw`、worktree `%USERPROFILE%/src/zantetsuken-vr-quest3s`。元のPCVR checkoutへの合流はまだ行っていない。

## 現在の状態

追記: Android ARM64のVulkan版VP3C-NATIVEを追加。実装・新しい計測・再現手順は [Quest 3S VP3C-NATIVE Vulkan](quest3s-vp3c-native-vulkan.md) を参照。以下の初回計測はUnity描画経路の記録として残す。

`tiled_seed14` のUnity Mesh／Unity描画のVP3CをQuest 3Sで起動し、CSVとUnity Profilerのrawファイルを取得した。MSAA 4xではUnity Meshは45fpsのAppSWに到達したが、VP3Cは約22fpsで未達。VP3Cの45fps達成を完了扱いにしない。

最新版の通常起動はヘッドトラッキング。自動経路を使う比較は `-CameraMode path` を明示する。`-KeepOpen` または通常のhead起動では、計測後も選択した描画経路とWorldを保持する。以前の実装は測定終了時にVPを解放して元のMeshRendererを復帰させていたため、終了後の快適さをVP3Cの性能とみなせなかった。この切り替えを修正した。

## 固定した条件と実機確認

| 項目 | 設定／確認 |
| --- | --- |
| Scene | `Assets/Licensed/WalkCity/WalkCity.unity`からQuest専用Sceneを生成。tiled-seed14-v3、885 instance、1061 renderer、1602 command、登録対象の約127万triangle |
| 描画方式 | Unity Mesh、VP3CのGPU選別＋Unity indirect。独自ネイティブ描画プラグインは使わない |
| AppSW | `zantetsuken-vp-appsw-probe`のVpRasterに基づく`XRMotionVectors`再ラスタライズ＋URP標準XRDepthMotionPass |
| Unity／API | 6000.3.22f1、IL2CPP ARM64、Vulkan、SinglePassMultiview |
| 周期 | 物理表示90Hzを要求。SpaceWarpでアプリ45fpsを要求。`xrReportedHz≈45`と`AppSWApplied=True`だけでは達成判定しない |
| URP | Quest_RPAsset、Forward、RenderGraph、scale 1、HDRなし、MSAA **4x**、主光源の影1024／距離50／1cascade／softなし |
| 実描画ターゲット | カラー1680×1760×2／MSAA4。AppSW motion/depth 420×440×2／MSAA1、RGBA16F＋D24S8。診断featureはdescriptorを読むだけでGPUパスを追加しない |
| Worker | Geometry 1、Background 1、Planning 1 |
| Main affinity | Unity公式起動設定 `-platform-android-unitymain-affinity 0x3c`。実際の許可範囲はOS制限を受けてcore **3–5**。全て測定済みfast core |
| nice | Main=-20、Geometry=-17（Main+3）、Background=-14（Main+6）、Planning=-20。setpriority後のreadback一致を確認 |
| メモリ | Androidのmmap／mprotect／munmap。実機で予約・拡張・アドレス維持・データ保持・解放を確認。clip stride144、cull stride48 |
| 静的条件 | crowd、刀、移動入力、cut登録を停止。切断・skinned characterなし。対象のphysics条件と測定中の容量・転送不変を記録 |

Windows VirtualAlloc、OS thread/clock照会はAndroid経路を追加。GPU構造体のstride/alignmentとVulkanのindirect draw IDの扱いを修正。PCのnative shader bytesも対応する構造体配置で再生成した。Questではその描画経路を選ばない。

Scene内のScenarioRenderPipelineがQuest用AssetをPC用WalkCity_RPAssetで上書きする問題を修正。起動時に実pipelineがQuest_RPAssetであることも検査する。

## 同一経路の40秒測定

初回用の経路は `start,99,1.7,-32,180 → hold 5秒 → turn 360度/30秒 → hold 5秒`。同じ経路を1周warm-upし、次の1周を記録した。これはPC比較の実経路を取り込んだものではない。Development build、numeric diagnosis有効、raw Profiler記録あり。全記録行を含めて集計した。GPU値はXRDisplaySubsystemのApp GPU time、P95はnearest-rank。

### MSAA 4x（採用設定）

Build GUID `63d691f632fd42f18bb164b66c880bdd`。

| 方式 | 記録行 | 平均fps | フレーム時間平均／P95 | App GPU平均／P95 | Main時間平均／P95 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Unity Mesh | 1795 | 44.87 | 22.29／22.23ms | 10.71／12.98ms | 6.20／7.05ms |
| VP3C | 874 | 21.85 | 45.77／48.36ms | 40.63／50.30ms | 6.43／6.96ms |

Unity MeshはVrApiの`FPS≈45/90`、`ASW≈90,Type=App`を確認。VP3Cは`FPS≈22/90`、`ASW=0,Type=App`。要求は有効でも実際のAppSW生成は成立していない。GPU使用率もほぼ上限で、4xのVP描画に残るGPU負荷を調べる必要がある。

データ（worktreeの`Logs/quest-device-runs/`内、Gitには含めない）:

- VP3C: `20261008T193142Z-vp3c-25d144/`（raw約209MB）
- Unity Mesh: `20261008T193348Z-unity-fcc717/`（raw約155MB）
- 集計: `Logs/quest-msaa4-summary.json`
- 実機ログ: `Logs/quest-vp3c-msaa4.log`、`Logs/quest-unity-msaa4.log`、各`-vrapi.log`

### MSAA 1x（参考）

Build GUID `05f297750f25420f856b54938e9a7e49`。probe初期設定に合わせた1xで先に取得した値。採用4xの達成を示す値として使わない。

| 方式 | 記録行 | 平均fps | App GPU平均／P95 |
| --- | ---: | ---: | ---: |
| Unity Mesh | 1793 | 44.79 | 7.21／8.20ms |
| VP3C | 1794 | 44.84 | 14.46／15.31ms |

両方式で`ASW≈90,Type=App`を確認した。データは`20261008T192334Z-vp3c-53d66c/`と`20261008T192534Z-unity-7a5d6a/`。集計は`Logs/quest-msaa1-summary.json`。

### ヘッドトラッキング・描画保持の確認

最新版Build GUID `77786e47a6fa4cedb248fed3439fba53`。MSAA4、VP3C、warm-up lap0、40秒raw取得。自動位置／方向制御は初期配置の一度だけ。記録したeyeは実際の頭部pose。終了後に`scene retained: vp3c`と`mode=vp3c camera=head phase=Done`を確認し、App GPU時間も終了後まで高いままであることを確認した。測定後のMeshへの切り替えはなくなった。

頭部追従が滑らかでもAppSW達成の証拠ではない。Metaの[コンポジタ説明](https://developers.meta.com/vr/essentials/the-compositor/)では、TimeWarpが表示直前の頭部回転に合わせて完成済みフレームを補正する。今回の体感にもその寄与が考えられるが、寄与の量は計測していない。

このrunは頭の動きが任意なので同一経路比較に混ぜない。869行、平均21.71fps、App GPU平均42.88ms。`20261008T193845Z-vp3c-ae3591/`、`Logs/quest-msaa4-head-summary.json`、`Logs/quest-vp3c-head-msaa4.log`に保存。

## 結果の限界と残る問題

- VP3Cの起動・登録時にVulkanが`_VpCullCommands`、`_VpVisible`、`_VpInstanceClip`、`_VpInstanceObjectToWorld`、`_VpVertices`未設定によるdraw skipを各1回報告する。両眼の街は描画できるが、原因と影／motionの完全性は未解決。警告が繰り返し出ないことを完全性の証明にしない。
- raw記録開始直後に130–158ms程度の長いフレームがある。Profiler開始と時刻が一致するが、因果の断定や恣意的な行削除はしていない。全行と欠落数を残した。
- FrameTimingManagerのGPU時間とrender thread時間は全行0で未取得。ゼロの実処理時間とは扱わない。GPUはXR取得値を使う。
- CPU／GPUのクロックはQuestの自動制御。頭部pitchも残るため画素単位の同一条件ではない。今回の数値から方式間の絶対的な優劣を確定しない。
- Unityのtriangle counterはindirect描画のtriangle数をMeshRendererと同じように数えていない。対象inventoryのtriangle数と区別する。
- PC比較の正式経路への差し替え、GPUパスごとの費用特定、影／AppSW depth-motionの一致、切断／Cap／キャラクター、温度が上がった長時間運転はこの初回で合格としていない。

## 再現

Unity Android moduleと接続済みQuestが必要。ignoredの`Assets/Licensed`を元worktreeからこのworktreeへ配置する。Quest専用Sceneとgeometry bankは生成物としてignoredのまま保持する。

```powershell
# Android設定・Quest Scene生成・ARM64 APK
.\Tools\Quest3s\Build-StaticDemo.ps1 -Development

# 操作確認：頭部追従、VP3Cを計測後も保持（既定camera=head）
.\Tools\Quest3s\Run-StaticDemo.ps1 -Apk .\Build\quest-static-dev.apk -Mode vp3c -CameraMode head -KeepOpen

# 再現可能な経路で両方式を別々に測る
.\Tools\Quest3s\Run-StaticDemo.ps1 -Mode vp3c -CameraMode path -Profiler -KeepOpen
.\Tools\Quest3s\Run-StaticDemo.ps1 -Mode unity -CameraMode path -Profiler -KeepOpen
# PC比較の経路を使う場合は -PathFile <path.txt> を両方に指定する

# 指定した完了runだけを集計
python .\Tools\Quest3s\Summarize-StaticDemo.py <vp-run-folder> <unity-run-folder> --output .\Logs\quest-summary.json
```

次のrunは前のrunの結果が書かれた後に開始する。アプリのデータディレクトリ`/sdcard/Android/data/com.zantetsuken.quest.static/files/runs/`からADBで回収する。起動スクリプトはUnityのaffinity引数を一つのremote commandとして渡す。

## 検証記録

- Android APK: 最新版ビルド成功、errors=0、実機へインストール済み。
- Windows上の関連EditMode: 174/174、最後の描画確認57/57（重複あり）。構造体配置・Windows描画の変更後に取得した前回記録。全てのQuest機能が合格したという意味ではない。
- 今回の共有runner変更後のPC PlayMode（準備・計測・終了、Unity／VP）は2/2成功。`Logs/quest-runner-playmode.xml`／同`.log`に保存。
- 実機mmap smoke、Main affinity、worker nice readback、実ターゲットMSAA、両方式の40秒記録とヘッド追従後の描画保持を確認。
