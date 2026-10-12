# Quest 3S VP3C-NATIVE Vulkan

2026-10-09 JST。専用worktree `C:/Users/%USERNAME%/src/zantetsuken-vr-quest3s`、ブランチ `quest3s-appsw`。

最新のカラー準備分離・pass統合と測定結果は[準備分離の記録](quest3s-vulkan-colour-pass-preparation.md)を参照。以下の初期実装の計測値は履歴として保持する。

## 実装

Android ARM64の `libZantetsuVpVulkan.so` を追加。Questの `vp3c-native` モードはUnityのVulkan command buffer上に `vkCmdDrawIndexedIndirect` を記録する。GPUが作った24-byte entryの先頭command IDを飛ばし、残る20-byte indexed argumentsをstride 24で読む。1602 commandをCPUで1602回発行せず、一つのmulti-drawで渡す。

- カラー、主光源の影、AppSWの再ラスタライズをネイティブ描画。静的シーンでは各1回、合計**3 multi-draw／frame**。1602×3個のGPU draw command自体は残る。
- AppSWは既存probe由来の `XRMotionVectors` shaderを使用。URPのXRDepthMotionPassの後に、実際のXR motion/depth targetへgeometryを再ラスタライズし、stencil=1を書き込む。previous/current stereo VPとNDC modifierはURPのGPU globalsからコピーする。
- UnityのVulkanプラグインAPIでbuffer／textureを取得してbarrierを設定。Unityのqueueに独自submitやwaitを行わない。自前pipeline／descriptor／upload領域はUnityの `safeFrameNumber` に基づいて再使用・解放する。
- shaderはUnity 6000.3.22f1のVulkan compilerからSPIR-Vを生成。set／binding／stageを実SPIR-Vと照合し、reflectionに不足・衝突があれば拒否する。
- プラグインをpreloadし、Unityのdevice作成時に対応済みの `multiDrawIndirect` と `drawIndirectFirstInstance` を有効化する。hardware supportだけで利用可能と判定しない。
- SinglePassMultiviewではinstanceを倍化しない。PCのD3D12 Single Pass Instanced経路は従来の倍化を維持する。
- Vulkan native eventのrender-pass条件に合わせ、Quest_Rendererの `m_UseNativeRenderPass=0`。RenderGraphを使用する。以前のUnity Mesh／VP3C計測はこの設定が1なので、今回の結果と厳密な同条件比較ではない。

従来のWindows D3D12 backendと別ライブラリ。Windows上のVulkan backendは今回の対象外。

## 実機計測

Development APK SHA256 `1304B74C116ACF114108F4F94A00AC001D640C986367BD58A6C6425E8C899366`、Build GUID `77fff344316f48da99f4b707449f5e12`。

ヘッドトラッキングで40秒相当warm-up後に40秒を記録。972行すべてを集計し、静的条件・登録対象不変・route発行の一致は `comparison conditions: held`。描画拒否／ring不足／pipeline failureはいずれも0。カラー・motion・casterが各1883回発行され、native counterでも合計5649回のmulti-drawを確認（warm-upを含む累計）。

| 項目 | 平均 | P95 |
| --- | ---: | ---: |
| 実アプリfps | 24.27 fps | — |
| フレーム時間 | 41.20 ms | 49.40 ms |
| XR App GPU | 37.67 ms | 46.26 ms |
| CPU Main | 5.88 ms | 6.60 ms |
| Unity側 Draw Calls Count | 33.44 | 220 |

**45fpsは未達**。要求は90Hz／AppSW45fpsだが、計測中のVrApiは `ASW=0,Type=App`、GPU時間も22.2msを超える。native化だけでGPU負荷が解決したとは扱わない。頭部poseは任意で、保存時には上向きの視線も含む。通常VP3Cの以前の約22fpsとの速度差を性能改善の証拠にしない。

`Logs/quest-vulkan-device-runs/20261008T203344Z-vp3c-native-e12e98/` にCSVとProfiler raw（87,331,231 byte、PD3U header）を保存。`Logs/quest-vulkan-summary.json` と `Logs/quest-vulkan-native-profile-filtered.log` に集計／実機ログ。GC collection増加0、XR dropped counter増加1700。Profiler開始時の129.8msフレームを含め、行を除外していない。


Unity Profilerの `Draw Calls Count` はプラグインが直接記録したVulkan callを数えない。CSVのUnity側call数と、プラグイン自身の `draws recorded` を分けて読む。今回のVP部分は3回／frameで、場面全体のCPU draw call数が3という意味ではない。

MSAA4、カラー1680×1760×2、motion420×440×2／MSAA1、主光源の影1cascade、G=1／B=1、Main affinityとnice相対値は従来設定を維持。頭部追従、測定後もnative描画を保持する。自動経路を使わない計測なので、以前のpath比較との速度差を断定しない。

## 検証と残る範囲

- NDKのARM64ビルド（warnings as errors）、14 shader variant／28 stageのbake、IL2CPP APK作成とQuestへのインストールを確認。
- 実機のABI／SPIR-V parser検証で28 stage、壊れた入力、event rangeを確認。両眼の街と影をスクリーンショットで確認。
- 共有managed描画のEditModeテストは519/519成功。`Logs/quest-vulkan-rendering-tests.xml` に保存。PC D3D12 native描画の実画面確認は今回再実行していない。
- 描画拒否0はcommand記録が成功したという確認。AppSW depth/motionを画素単位で参照実装と比較した検証はまだ行っていない。
- VulkanのcasterはGPU選別のshadow-view unionを描く。draw IDによるcascadeごとのvertex rejectionは無効。現在の1cascadeでは使用しない最適化で、複数cascadeでの性能検証は今後必要。
- AppSW geometry motionは静的対象を前提とする。動くobject／切断／Cap／skinned characterは今回のデモの検証対象外。
- 起動時のUnity側drawに、VP buffer未設定のVulkan warningが各1回残る。描画中に繰り返していないが、原因は未解決。
- Vulkan版はnative mutex待ち時間の詳細計測APIを実装していない（lock statsは未取得）。FrameTimingManagerのGPU／render thread時間も本機では0で未取得。App GPUはXR値を使う。

## 再現

Unity Android module／NDK、ignoredのLicensed scene／素材が必要。

```powershell
# native .so、SPIR-V、Quest Scene／設定、APKを生成
.\Tools\Quest3s\Build-StaticDemo.ps1 -Development -Output .\Build\quest-native-vulkan-dev.apk

# install／頭部追従／1 lap相当warm-up／40秒計測／終了後もnativeを保持
.\Tools\Quest3s\Run-StaticDemo.ps1 -Apk .\Build\quest-native-vulkan-dev.apk -Mode vp3c-native -CameraMode head -Profiler -KeepOpen

# /sdcard/Android/data/com.junic.zantetsu.queststatic/files/runs/<run>/ を回収後
python .\Tools\Quest3s\Summarize-StaticDemo.py <run-folder> --output .\Logs\quest-vulkan-summary.json
```

通常起動の既定モードは引き続きUnity描画の `vp3c`。nativeを選ぶときは `-Mode vp3c-native` を指定する。`-SkipNativeBake` はshaderを変更していない再ビルドに限る。native sourceだけをビルドする場合は `Native/ZantetsuVpNative/Build-Vulkan.ps1`。
