# 検証記録 — 2026-10-12

環境: Unity 6000.3.22f1、Blender 4.5.13 LTS、Windows。

- コピーした公式PLYとMIT版MeshRepairから修復レシピを再実行。StrictRecipeHealthGatePass / PreservationGatePass ともにtrue。
- Apache-2.0アドオン＋V-HACD 4.1からConvexを再生成。32 hull、950頂点、1772tri、各hullは最大32頂点。閉鎖・凸性検証に合格。
- EditMode 32/32成功。auto/demo/commercial選択、商用時の間接参照・Resources除外、private退避後の内容と.meta復元、既存メニューとシナリオ選択を含む。
- PlayMode 1/1成功。公式Bunnyのcompound 32 hullを実際の切断kernel・PhysX cooking・描画geometryへ渡し、物理の最終引き継ぎを確認。
  左メニュー入力で開き、右レイでReset sceneを選択。3体→2体→1体へ再生成し、ワールドの置換とメニューの再利用を確認。
- Windows IL2CPP Player: privateなし + Autoで生成成功、起動してBunny 3体の登録を確認。
- Android ARM64 IL2CPP Quest APK: 明示Demoで生成成功、`com.zantetsuken.quest.bunnydemo`。
- Commercial指定: private不足時にexit 1で拒否。Bunnyへのfallbackが起きないことを確認。
- BuildReportの内容監査: notice / Bunny JSON / world prefab / profileのみがStanford由来のデモ入力として同梱されることを確認。生成ツールや元アーカイブはPlayerに含まれない。

実機のコントローラ操作・Quest上の見え方や速度は未検証です。
privateを配置した商用Player自体は、このワークツリーにprivateデータがないため生成していません。

再確認の入口:

```powershell
.\Tools\StanfordBunny\Prepare-Bunny.ps1
.\Tools\Build-ScenarioPlayer.ps1 -BuildMode Auto
.\Tools\Quest3s\Build-ScenarioPlayer.ps1 -BuildMode Demo -Development
```

Unity Test Runnerフィルタ: EditMode `BunnyBuildPolicyTests;SandboxScenarioTests;PlayMenu`、PlayMode `BunnyDemoTests`。
ログ/XMLと描画画像は `Logs/bunny-*`、内容監査はWindows出力の `zantetsu-content-audit.json` とAPK隣の `.content-audit.json`。

## Linkでの切断インパルス修正

- Bunny固有の固定0.1 N·sから、既存play profileと同じ質量比例のJ = 1 × massへ変更。初回とリセット後の両方に適用。
- PlayModeの実Bunny切断で、物理公開直後の両断片の初速+1 / -1 m/sを確認。3→2→1のリセット後も質量比例設定が残ることを確認（1/1成功）。
- Windows Auto Playerを再生成（errors 0）。Quest APKは今回更新していません。

## PCVR（Link）— 2026-10-27

- ビルド：Windows x64 IL2CPPのDevelopment Player。
  - ビルド元はvr2の21b57d77で、privateデータはありません。
  - コマンドは `Tools/Build-ScenarioPlayer.ps1 -BuildMode Demo -Output <新しいフォルダ>` で、exit 0でした。
  - 内容監査は `noncommercialBunnyDemo: true` でした。
  - シーンはZantetsuBootとStanfordBunnyDemoの2つです。
  - グラフィックスAPIはDirect3D 11だけです。
- 実行：Quest 3SをLinkで接続し、Meta Horizon LinkのOpenXRランタイム（Oculus 1.208.0）で動かしました。表示は90 Hz、引数は `-logFile` だけです。
  - 起動
    - OpenXRセッションはFOCUSEDまで進みました。
    - `play` はStanfordBunnyDemoで始まりました。
    - 物理は表示ステップの90 Hzで、本体描画はVP3C（Unityの呼び出し）でした。
  - 操作：開始時は3体でした。メニューから `Reset scene` を6回行い、6回とも20体で作り直されました。
  - 切断：合計1,175回、生きている破片は最大382個、失敗した切断は0件でした。
  - 終了：例外は0件でした。ウィンドウを閉じて、通常どおり終了しました。
- 確かめていないこと
  - Release Player
  - Direct3D 12でのネイティブ描画の経路
  - 72 Hzなど90 Hz以外の表示レート
  - Air Link
  - Quest 3S以外の端末
  - 長時間の連続プレイ
  - フレーム時間の評価

## PCVR（Link）、Direct3D 12とVP3C-NATIVE — 2026-10-27

- ビルド：`Tools/Build-ScenarioPlayer.ps1 -BuildMode Demo -Direct3D12 -Output <新しいフォルダ>` で、exit 0、errors 0でした。
  - このビルドの間だけ、グラフィックスAPIを「Direct3D 12、Direct3D 11」にしました。ビルド後はDirect3D 11だけ（`m_APIs: 02000000`）に戻りました。
  - `VpInstanceCull` は2 of 2 variantsでした。
- 実行：Quest 3SをLinkで接続し、Oculusランタイムの90 Hzで動かしました。引数は `-logFile` だけです。
  - 起動
    - `graphics=Direct3D12`
    - `VP BODY ROUTE: VP3C-NATIVE`（理由は「the default of a Windows Direct3D 12 Player (D-215)」）
    - 物理は表示ステップの90 Hz
  - 操作：開始時は3体でした。`Reset scene` を10回行い、10回とも20体で作り直されました。
  - 切断：合計1,418回、生きている破片は最大299個、失敗した切断は0件でした。
  - 描画：11の世界すべてで `VP BODY ROUTE (end)` が「依頼した数＝記録した数」で、描画を断った数と、描けなかったカメラは0でした。
  - 終了：例外は0件でした。ウィンドウを閉じて、通常どおり終了しました。
- 確かめていないこと
  - Release Player
  - Direct3D 12が使えないPCでのDirect3D 11への切り替え
  - 90 Hz以外の表示レート
  - Air Link
  - フレーム時間の評価
