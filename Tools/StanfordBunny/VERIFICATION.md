# 検証記録 — 2026-10-12

環境: Unity 6000.3.22f1、Blender 4.5.13 LTS、Windows。

- コピーした公式PLYとMIT版MeshRepairから修復レシピを再実行。StrictRecipeHealthGatePass / PreservationGatePass ともにtrue。
- Apache-2.0アドオン＋V-HACD 4.1からConvexを再生成。32 hull、950頂点、1772tri、各hullは最大32頂点。閉鎖・凸性検証に合格。
- EditMode 32/32成功。auto/demo/commercial選択、商用時の間接参照・Resources除外、private退避後の内容と.meta復元、既存メニューとシナリオ選択を含む。
- PlayMode 1/1成功。公式Bunnyのcompound 32 hullを実際の切断kernel・PhysX cooking・描画geometryへ渡し、物理の最終引き継ぎを確認。
  左メニュー入力で開き、右レイでReset sceneを選択。3体→2体→1体へ再生成し、ワールドの置換とメニューの再利用を確認。
- Windows IL2CPP Player: privateなし + Autoで生成成功、起動してBunny 3体の登録を確認。
- Android ARM64 IL2CPP Quest APK: 明示Demoで生成成功、`com.junic.zantetsu.quest.bunnydemo`。
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
