# Megacity 建物・Anchor 付きプロップの実シーン確認

## 対象と範囲

生成シーンは `Assets/Licensed/BuildingAnchorScenario/BuildingAnchorCity.unity`。
元の XrSimCity を複製し、NPC と箱を除いて、実アセットを二つ登録する。
元シーン、元 blend、製品の切断・物理コードは変更しない。

| 対象 | 元アセット | building | 作者の Anchor | 配置・質量 |
| --- | --- | --- | --- | --- |
| 建物 | `ITHappy_Megacity_Buildings--bar_001.blend` | true | 21 点 | (0,0,16)、10,000 kg |
| プロップ | `ITHappy_Megacity_Props--advertising_001.blend` | false | 1 点 | (-3,0,5)、50 kg |

質量はこの試験の設定値。モデル・Convex・Anchor・フラグは元資産から読み取る。
両方とも作者の Anchor により初期状態は固定する。背景の建物は切断対象に登録しない。
これは汎用のアセット取り込み機能ではなく、二つの実資産を使うシナリオである。

切断面を固定して `CutWorldRoot.TryAsk` → 製品 driver の通常更新 → Provisional →
Final/Geometry Commit を通す。製品の Main 予算、Pending、cook、公開を迂回しない。
刀入力・Slash の命中判定はこのテストの範囲外。

## 自動シナリオ

1. 建物をローカル y=5 m で水平切断。上側は Anchor なし・dynamic・建物 World D6、下側は Anchor あり・固定。
2. プロップを y=1.2 m で水平切断。上側は dynamic、下側は固定。どちらにも建物 World D6 は付かない。
3. 建物の上側をローカル x=0.17 m で再切断。両側とも Anchor なしで、建物の深さは 2。
4. 固定された下側の位置、3 件の完了、Geometry fault の有無、通常終了時の解放を確認する。

記録は受付済み Operation の状態を追う。完了を待つ間に見えた Provisional も記録する。
タイムアウトを成功扱いせず、再受付もしない。撮影込みの機能確認であり性能測定ではない。
共有 profile の分離係数 k=1 を使用する。独自のインパルス上書きはしない。

## 作り直し

1. Blender 4.5 で次の exporter を実行する。出力が既にある場合は上書きを拒否するため、
   再生成前に既存の Inputs を別名で保存する。

```powershell
& 'C:/Program Files/Blender Foundation/Blender 4.5/blender.exe' `
  --background --factory-startup --disable-autoexec `
  --python Tools/Export-BuildingAnchor-Scenario.py -- `
  --sources 'C:/Users/%USERNAME%/src/zantetsuken-blender-pipeline-pilot/Generated/BottomHalfUV/MegacityMeshRepair/1.21.1-convex/Working' `
  --out Assets/Licensed/BuildingAnchorScenario/Inputs
```

2. Unity のメニュー `Zantetsu/Sandbox/Build Building and Anchor City Test` でシーンを作る。
   元の XrSimCity、Compact16uv の material/atlas が必要。
   生成前に製品の `VpCutInputGate` で両入力を検査する。
3. Player を作る場合は Unity の `-executeMethod
   Zantetsu.EditorTools.Sandbox.BuildingAnchorScenarioBuild.BuildPlayer` を使用する。
   出力は `Builds/BuildingAnchorScenario/Player`。Windows x64 IL2CPP、非 XR の固定カメラ。
   XR の自動初期化は build 中だけ無効化し、finally で元の値へ戻す。
   Unity build が RPAsset 等を更新した場合は、その差分を確認して build 前の内容へ戻す。
4. リポジトリから `./Tools/Run-BuildingAnchorScenario.ps1` を実行する。
   `Library/BuildingAnchorScenario/run-*` に画像、Player ログ、逐次書き出しの `scenario.txt` が残る。
   プロセスの exit 0 だけでなく、`FINISHED code=0 completed=3 released=True` を確認する。

Editor で生成シーンを開いて Play しても自動シナリオが動く。`automatic` をオフにすると
登録だけ行い、任意の観察に使える。

## 資産の扱い

出力 JSON・material・scene は `Assets/Licensed` 以下、Player・証拠は Git 管理外。
exporter は元 blend を保存せず、前後で SHA-256 を照合する。
描画形状を変えない modifier（法線用 Auto Smooth）だけを許可し、評価後の頂点位置・
polygon 接続の一致を確認する。Convex は既存の厳密な監査を通す。
元データの修復や、検査の閾値の緩和はしない。

## 実行結果（2026-09-28）

HEAD `178eecb3` にこのシナリオを追加し、Windows IL2CPP Development Player で確認。
最終結果は `Library/BuildingAnchorScenario/run4/scenario.txt`、build は `build7.log`。
**code 0、3 Operation すべて Completed、world.IsReleased=true**。

| 切断 | 正側 | 負側 |
| --- | --- | --- |
| 建物初回 | Anchor 0、dynamic、深さ 1、World D6 0.25 m | Anchor 21、固定、World D6 なし |
| プロップ初回 | Anchor 0、dynamic、建物扱いなし・World D6 なし | Anchor 1、固定、World D6 なし |
| 建物の正側を再切断 | Anchor 0、dynamic、深さ 2、World D6 0.125 m | 同左 |

3 件とも Provisional を観測し、固定／World D6 の組合せを確認した。
Final 後も Anchor の総数は保存され、2 秒の観察後に固定片の位置・姿勢が維持された。
撮影は `run4/01-before.png` ～ `06-prop-after.png`。画像でも実資産と切断線を確認した。
通常更新で進行しており、残り予算を試験用の固定値に置き換えていない。
この実行は Pending の長期保持、Slash 命中、XR 両眼、性能の検証ではない。

途中記録も `Library/BuildingAnchorScenario` に保持：

- `build.log` は sandbox 内のライセンス IPC 待ち、`build2.log` はそのプロセスによるロック。
- `build3.log` は Unity が Library 内への Player 出力を拒否。出力先を Builds に修正した。
- `run1`、`run2` は機能判定と通常終了が通ったが、画像が黒いため描画の証拠には使わない。
- `run3` は明示描画できたが、背景建物内のカメラ位置が不適切。また撮影フレーム内の
  shutdown が display の解放条件に違反して例外となった。テスト側でカメラ位置を直し、
  shutdown を次のフレームへ移した。製品のチェックは変更していない。
- 非 XR build でも OpenXR plugin の pre-init は HMD 不在の診断を出す。
  XR session による実行結果ではない。

既存の計測用 check、OpenXR 設定、ReferenceIntake の変更はこの単位では編集していない。
build が更新した PC_RPAsset は build 前の保存内容へ戻した。
コミット・push・DESIGN の変更は行っていない。
