# 現在poseのRenderer-local契約とCompact16uv入力

2026-09-24 / Unity 6000.3.22f1 / Windows Editor EditMode / 製品worktree。開始commit `a163b0c4`。

## 結論

`BakeMesh(useScale: false)`をRenderer-local入力として扱う旧DESIGNの指定を訂正する。`true`によるscale補償で、Casual／Professional各4条件の現在poseから16B storageへの格納・Burst切断・positive側再切断が成立した。Runtimeの現在pose入口や製品scene接続はまだ追加していない。

[Unity 6.3 API](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/SkinnedMeshRenderer.BakeMesh.html)は、`useScale`をRenderer Transformのscale補償指定と説明する。公式説明と今回の実測が一致した。引数名から「falseならobject scaleを含まない」と解釈しない。

## 入力・方法

- Casual `f_1`: 4,728 render vertices、20,304 indices、3,454 authored topology IDs。
- Professional `c_1`: 3,834 render vertices、17,742 indices、3,005 authored topology IDs。
- 固定`.blend`と移動後の最新版`ITHappyCharacterMeshRepair/.../1.23.0-convex-remap_palette/Working`のSHA-256が一致。詳細は`summary.json`。
- 元のposition／normal／UV／index／全weight・bindposeをパス込み順序付きhashで既存intake manifestへ照合してからtopology mapを再利用する。位置からのweldや近傍推定はしない。
- 両代表は最大4 influences、blendshapeなし。今回の実行はRenderer quality Auto、global FourBones。全weight線形ブレンドの位置oracleは`renderer.transform.worldToLocalMatrix * bone.localToWorldMatrix * bindpose`。object scaleの追加乗算なし。
- インスタンスのAnimatorを停止し、基準姿勢と全boneの小回転を明示設定。条件2では祖先の並進／回転とscale `(1.3, 0.8, 1.1)`、条件3ではさらにroot bone local scaleへ`(1.05, 0.95, 1.1)`を乗算する。実アニメーションクリップ評価や時間同期の検証ではない。
- `BakeMesh(true)` → `VpMeshConverter` → `VpCpuGeometryStorage.TryAppendCuttable` → `VpStorageCut`の2平面。各回は現在boundsのaxis 1／0、`d=-center-.137*extent`。新規頂点生成、status Ok、managed fallbackなし、open contour 0、初期Published頂点不変を検証する。
- BakeのUV／index完全保持、float3位置の16B変換前後完全一致、UV量子化中心の完全保持、法線oct量子化誤差を確認。比較用の全weight CPU skinはテストoracleであり、製品への自前skinning採用ではない。

## 失敗分類と訂正

初回`false`は6条件中2合格・4失敗。分類は`F-SKIN-FRAME`。Casualの元Renderer scaleは1、Professionalは0.01でroot bone scaleは1。初回XMLはUnityのTemp自動掃除により非保持であり、この件数は観察記録。最終回帰のnegative controlで同じ不一致を再現し、そのXMLを保存した。

| 条件 | Casual falseの最大local位置誤差 | Professional falseの最大local位置誤差 |
| --- | ---: | ---: |
| 0 基準 | 0.000000477 | 182.411 |
| 1 骨回転 | 0.000000477 | 182.596 |
| 2 祖先scale追加 | 0.368524 | 182.955 |
| 3 root bone scale追加（最終試験） | 0.405393 | 201.237 |

Professionalのlocal単位はRenderer scale 0.01をWorld写像で適用する前の単位。これをWorldの182m誤差と読まない。`false`の出力は`Scale(renderer.lossyScale) * localOracle`に最大約1.55e-6で一致した。`true`でscaleを補償すれば、同じRenderer Transformを一度適用する契約に合う。root bone由来のscaleはoracleに残して検証した。

最終テストには`false`の不一致を期待するnegative controlを残した。初回失敗を無視・skipしたのではなく、出力空間の指定を訂正した。途中のscale調査用run（比較Mesh再利用で変形差チェックを汚したrunを含む）や一時コンパイル修正は採用結果に含めない。

## trueでの結果

| 条件 | Casual max local位置誤差 | Professional max local位置誤差 | Casual / Professional max oct法線誤差 |
| --- | ---: | ---: | ---: |
| 0 基準 | 4.771e-7 | 4.602e-5 | 0.8814° / 0.8977° |
| 1 骨回転 | 4.770e-7 | 4.642e-5 | 0.9334° / 0.9104° |
| 2 祖先scale追加 | 6.601e-7 | 6.547e-5 | 0.9336° / 0.9104° |
| 3 root bone scale追加 | 6.509e-7 | 7.830e-5 | 0.9407° / 0.8924° |

同じRenderer TransformでWorldへ写した位置誤差は全条件で最大1.073e-6。位置の比較許容はこのテスト固有の`2e-5 * max(1, baked bounds diagonal)`で、製品の固定精度保証ではない。法線の表はBake出力に対する初回oct量子化誤差であり、独立の法線skinning oracleや切断後累積誤差の測定ではない。

8条件・16切断すべて合格。scaleを加えた同じpose間でも浮動小数点差によってCasualの再切断後頂点数は7,593対7,590となるため、異なるframe条件間のbit単位・出力頂点数一致は主張しない。各条件の閉輪郭とstorage入力契約を検証した結果である。

初期16B頂点payloadはCasual 75,648 bytes、Professional 61,344 bytes。32Bなら各151,296／122,688 bytesとなる計算上の比較で、Mesh／weight／index／scratchを含む総memory実測ではない。

## 証拠・再実行・未完了

`editmode.xml`と`summary.json`を保存する。対象8件の個別出力は全件回帰XMLから抽出する。対象を絞った8/8合格後、全件回帰3,622/3,622・skip 0を確認した。テストは`Compact16uvCurrentPoseTests`。private intakeがない環境ではIgnoredになるため、本受入はskip 0を要求する。

`Tools/Summarize-CurrentPose.ps1`はTempの全件回帰結果と最新版source hashを照合して証拠を生成する。Unity実行は`-batchmode -runTests -testPlatform EditMode -testFilter Compact16uvCurrentPoseTests`。全件ではfilterを外し、`-testResults <worktree>/Temp/current-pose-full.xml`を指定する。Tempは次のEditor起動で掃除されるため、その前に集約する。privateアセットはcommitしない。

今回の試験は配列取出し・oracle・NUnit比較を含み、Main費用やGC、Worker offload、90fpsの性能結果ではない。元benchmarkの`false`とscale付きmatrixの対比較はその空間での履歴として保存し、ここで性能値を再解釈しない。

次工程は専用character sceneへの現在pose入力接続と通常Skinned表示→VPの切替観察。その後、同じposeのauthored骨Physics Proxy、切断面、commit／表示追従／寿命を同じframeで検証する。IL2CPP、負／零scale、任意のshear構成、4超weight、blendshape、画像／Stencil／Shadow／XR、現在pose取込み費用、自前非同期skinningの採否は未完了。
