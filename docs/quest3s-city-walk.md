# Quest 3S 街歩きシナリオ

## 対象

PC版の `Assets/Licensed/WalkCity/WalkCity.unity` と v7 の経路をQuestへ移植する。
経路はウェイポイントへの歩行、向き変更、記録済みSlash入力、道中で出会ったNPCへの攻撃で構成される。
元スクリプトは `C:\log\zantetsuken-vr\walk-city-2026-10-03\route\v7\script.txt`、脚本上限480秒、実行全体の上限565秒。
実際の命中・切断・NPCの補充は既存のproduct処理を通す。

## Quest設定

- 専用package `com.junic.zantetsu.questcitywalk`、専用scene `Assets/Licensed/Quest3s/QuestCityWalk.unity`。
- Vulkan / IL2CPP arm64、90Hz表示・AppSW 45fps、カラーMSAA4x、AppSWはカメラ移動MVのみ。
- AppSW物体距離制限30m、カラーnear/far 0.01m / 100m、通常のヘッドトラッキング。
- 物体単位のboundsカリング。距離除外用PSは追加しない。
- AppSpaceには追跡空間を配置するcameraの親のworld poseを送る。経路の人工移動・旋回を含む。
- G=1 / B=1、既存Androidスレッド優先度（Geometry=Mainより3低、Background=Mainより6低）とUnity Main affinityを使用。
- GPU初期geometryは32MiB、既存の自動成長を使用する。元のPC用512MiB初期設定から変更。
- SkinQuality Bone4、camera-only AppSW skin material、katana/waveの既存Quest用camera-only materialを使用。
- 一時capはAppSW用に描かない。通常物体とClip物体のnative描画分離を引き継ぐ。

## 起動順

`QuestNpcSlashDemo` の既存Questホストに街歩きscene/packageの分岐を追加した。
ロード直後は街の切断対象登録とcrowd起動を保持し、APK内入力と経路をpersistentDataPathへ展開する。
`StartPrepared`の観察処理が登録されてからcrowdを有効にし、NPC群の準備・街の切断対象登録を待つ。
登録終了後のstartup hookで実際のnative / Unity描画経路をprewarmし、開始待ち5秒の後に経路を開始する。
NPCデモと同じreal-time入力を使い、45fpsでも記録済み時刻が来た入力を全て消費する。長いframe timelineと終了ログの保存にも、既存の非同期／分割保存経路を使う。
PC版のXR Simulator用VRS待ち・高さ／視線補正は渡さず、実機の追跡姿勢で開始する。

## ビルド・再実行

worktreeで実行する。既存のQuest Vulkanプラグインとbaked shadersが必要。

```powershell
Tools/Quest3s/Build-CityWalk.ps1 -Development
Tools/Quest3s/Run-CityWalk.ps1 -Apk Build/quest-citywalk-dev.apk -NoProfiler
# Unity Profilerのbinary記録を含めて実行
Tools/Quest3s/Run-CityWalk.ps1
```

`-ScriptFile <host-path>`で経路を明示できる。ビルド時はAPKへ同梱し、起動時は実機へコピーする。
`-AppSwFarMeters 0`は追加距離制限なしの比較、`-1`は追加カリングなしの経路。
実行結果は `/sdcard/Android/data/com.junic.zantetsu.questcitywalk/files/runs`。
`comparison/city-walk-stages.txt`、`comparison/mobplan-steps.csv`、`comparison/city-walk-attacks.csv`、`comparison/frames.csv`と`demo-result.txt`を確認する。
シーン・ライセンス付きモデル・入力はignoredなprivate/StreamingAssets内に保存する。

## 実機確認

2026-10-09、Quest 3SでPC版v7の経路を2回実行した。修正版の記録は
`20261009T034648Z-citywalk-vp3c-native-7d5d75`。
Androidビルド成功・errors=0、更新APKをインストール済み。終了後も街の表示とヘッドトラッキングを保持している。

- 切断対象885個を登録、拒否0。開始時のNPCは20体。
- 73ステップ（Goto 49、Face 11、Slash 13）、13地点をすべて完了。歩行324.7m（脚本の329.1mに対して98.7%）。
- 脚本と切断完了まで380.1秒。終了コード0、チェック1,081項目を通過。
- 建物10棟を切断（53回、10棟を再切断）、小物31個（切断片への再切断159回）、NPC104体（補充84体、切断片への再切断25回）。
- 道中のNPC攻撃27回、対象命中27回、外れ0。NPCの出会い方によって回数は変わる（初回は30回）。
- native描画の拒否、ring full、pipeline failures、shadow format refusals、未描画XR frameはすべて0。prewarm 16/16、prewarm後のpipeline miss 0。
- 両眼の実機画像で街・NPC・影の表示を確認。
- 実機nice値はUnityMain=-20、GeometryPool-0=-17、BackgroundPool=-14。起動時のMain affinity指定は0x3c、実行中のOS側許可maskは0x38（CPU 3–5）だった。

最終APK `Build/quest-citywalk-dev.apk` のSHA256:
`BF66EE34305E41438BFD1211FBC8D495E69B33C3B70A800FCA222CF62D7FD267`。
ビルド・設定ログは `Logs/quest-citywalk/build.log`、`configure.log`。
回収した記録は `Logs/quest-citywalk/verified-run/<run>/`、集計は `analysis-verified.json`、実行ログは `live-verified.txt`。

### 計測値

Development Player、scenario / metrics / checks / detailed diagnosticsを有効にし、Unity Profilerのbinary記録はOFFで測定した。
`frames.csv`全17,070行、記録時間382.9秒（replay 17,064行、observe 5行、ending 1行）。
登録・prewarmや、その後のログ保存をすべて含む計測区間ではない。

| 指標 | 平均 | p50 | p95 | p99 | 最大 |
|---|---:|---:|---:|---:|---:|
| フレーム間隔 | 22.43ms | 22.22ms | 23.39ms | 27.10ms | 113.07ms |
| Main（FrameTimingManager、待ちを含む） | 8.40ms | 8.15ms | 10.92ms | 15.35ms | 106.50ms |
| XR App GPU | 14.93ms | 14.49ms | 20.64ms | 23.33ms | 26.34ms |

平均フレーム間隔からのFPSは44.58。33.33ms超48フレーム、44.44ms超19フレーム、66.67ms超2フレーム。
最大113.07msは経路開始直後（replayの0.11秒）。後半にはGPUが45fpsの予算22.22msを超える区間もある。
VrApiでAppSW `Type=App`の動作を確認したが、全区間45fpsを維持できたという結果ではない。
GPU側の負荷削減と開始時の短い停止は今後の最適化対象。
XRのGPU時間はAPIの最新値を保持した非同期サンプルで、各行のCPU・フレーム間隔と同じframeに対応するとは限らない。
`ftGpuMs` / `ftRenderMs` は0であり、計測不能として扱う。

### 初回チェックの修正

初回は経路・切断を完了したが終了コード14（2 failed / 1,001 held）だった。

1. SSAOを持たないQuest rendererでもSSAO無効を判定できるよう、機能の型と実際のactive flagを読む。renderer情報やflagが読めない場合は失敗として扱い、pipelineの適用・一致も確認する。
2. NPCのplan cycleを観測する最後の時刻を保持し、開いたcut windowはその時刻までで判定する。観測終了後のtimeline保存・集計時間が、cycleの停止時間へ入っていた。停止の上限3秒は維持する。

修正版では、cut windowをまたぐcycle間隔は最大1.54秒、最終windowの未観測端は0.85秒で、両方の判定を通過した。
シーンを保持する確認なので、worldの解放は検証対象に含まれない。
