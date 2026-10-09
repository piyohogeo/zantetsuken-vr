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

Androidビルドは成功・errors=0。設定ログで切断対象885個、G=1/B=1、カラーfar100mを確認した。`Build/quest-citywalk-dev.apk`のSHA256は`075F2EF2CAB947548FEF17A2924F77B13033B0840F96CD642E9525FA56A660F0`。最終APKをQuestへインストール済み。実機は装着待ちのため、経路の確認待ち。ビルドログは`Logs/quest-citywalk/build.log`、設定ログは`configure.log`。
