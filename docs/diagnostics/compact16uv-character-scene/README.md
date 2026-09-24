# 現在poseキャラクターのSkinned→Bake→Compact16uv表示

2026-09-24 / 開始commit `44576a19` / Unity 6000.3.22f1 / Windows x64 IL2CPP Development / D3D11 / mono。

## 判定

専用private scene `Compact16uvCurrentPose.unity`で、実SkinnedMeshRendererから現在poseを`BakeMesh(true)`で取り込み、既存16B storage／GPU buffer／indexed indirect表示へ接続した。最終3独立processの各24比較中23件が厳密gateを通過し、**全面合格ではない**。

- Bake通常Mesh→16B VPは各20/20合格。全比較で輪郭差0、初回oct法線量子化によるRGB最大差2/255。切断／再切断後の通常Mesh oracleとVPはpixel完全一致。
- 実Skinned表示→Bake通常Meshは各3/4合格。scale付きCasualだけ輪郭差1 pixelが残った。許容を広げず`F-SKIN-RASTER`として保存する。
- 4条件×各2切断×3 processの24切断はstatus Ok、open contour 0、managed fallbackなし。
- 各processの48画像は3回ともPNG hash完全一致。実capのnormal/debug色切替は全4条件の両切断stageで可視。
- 通常16B設定でのEditMode全件回帰は3,622/3,622合格、skip 0。画像の厳密gateとは別の判定である。
- 製品のCutWorldRoot／物理owner／DAG commitへはまだ接続していない。骨Physics Proxy登録・通常Gameplay scene移行完了とは呼ばない。

## 入力と試験scene

前工程で順序付き属性・weight・index hashとtopology対応を監査した固定Casual `f_1`、Professional `c_1`を使用する。Builderで`.blend`のSHAをintake manifestへ再照合し、後続EditMode全件回帰でも同じimportを監査する。固定のmetadataは`summary.json`と`physics-inventory.json`。

各familyについて、前工程の骨小回転poseと、それに祖先の並進・回転・非一様scale `(1.3,.8,1.1)`、root boneの非一様scale `(1.05,.95,1.1)`を追加したposeを検証する。Animatorを止めた明示poseであり、AnimationClipや戦闘中のpose同期・未来予測ではない。

実SkinnedMeshRenderer、Bake後MeshRenderer、VPは同じ共有palette／固定half-Lambertの診断shadingで比較する。元アセットの任意のURP材質・Shader全機能の再現ではない。通常Mesh側は既存`Compact16uv Mesh Oracle` shaderを使い、VPのstructured-buffer読み出しとは別の頂点fetchになる。

各captureは512×512、MSAAなし、透明clear、固定camera。切断前は全身を同じ画角で比較し、切断後は残すpositive半分のcap外向き面が見える方向へcameraを移す。対比較の2画像間ではcameraを変えない。元Skinned表示とBake Meshの比較後に16Bへ変換するため、Bake自体の差とoct8量子化を分離できる。

CutはRenderer-localの現在boundsからaxis 1、再切断axis 0を選び、`d=-center-.137*extent`。16Bの既存同期Burst経路を使うが、これはWorker offload測定ではない。切断後のMesh oracleは同じPublished出力をfloat属性へ復号したもので、独立した切断アルゴリズムではなくGPU fetch／index／変換の照合である。

## 1 pixel差の切り分け

残差はCasual scale付きposeの画像座標 **(195,177)**（PNG左上原点）。Skinned側は透明、Bake側はRGBA `(188,151,137,255)`で、262,144画素中1画素（約0.000381%）。共通の被覆領域のRGB最大差は1/255。全RGB差ありは23画素で、比較対象の被覆unionは28,098画素。

同じBake Mesh→16B VPでは輪郭差0であるため、今回の1画素差は16B pack／GPU decodeより前のSkinned→Bake境界で生じている。前工程のRenderer-local位置誤差と今回の局所差からskin計算精度とraster境界の差が候補だが、ここでは原因を断定しない。API不具合や任意sceneでの最大誤差保証とも呼ばない。通常の見た目として許容するかは別判断であり、厳密gateとPlayer exit code 1を成功へ書き換えない。

## capの観測

| family / pose | 初回切断 normal→debug変化画素 | 再切断 normal→debug変化画素 |
| --- | ---: | ---: |
| Casual 骨回転 | 23,782 | 1,935 |
| Casual scale追加 | 28,402 | 5,535 |
| Professional 骨回転 | 3,368 | 2,627 |
| Professional scale追加 | 4,436 | 2,487 |

ここで見ているのは完了した実capだけ。仮Stencil cap、実cap／仮cap共存、ShadowCaster、XRはこのsceneでは検証していない。

初期pilot run1–3は全身cameraを切断後も固定したため、一部のcapが背面になり色変化を観測できなかった。入力形状や判定許容を変更せず、切断後cameraだけ補正したrun4–6を正本とする。初期pilotをcap表示合格には数えない。Skinned→Bakeの1 pixel差は両系列とも同一である。

## 物理Proxyの現状

同じ固定`.blend`をBlender 4.5.13でread-only確認。両familyとも19 UCX、各32頂点60面、358 boneのrigを持つ。UCXは直接bone parentではなく`UCX → PHYS_NULL → rig/bone`で、`representative_bone`とPHYS_NULLのbone親が19件すべて一致する。

この記録はauthoring metadataのinventoryであり、convex品質、import済みUnity boneとの座標一致、現在poseでのcookやowner登録の合格ではない。古いm_8 JSONや合成boxで代用しない。次工程ではこの固定UCXをbone-localへ輸出し、Unity側の同じ現在poseを使ったRenderer-local写像を検証してから物理登録へ進む。

## 証拠・再実行・制約

- `run4/`: 48枚の代表PNG、report、trace。`run5/`・`run6/`: 同一binaryの別process report／trace。重複PNGは作業環境に保持し、Gitには代表だけを入れる。全48画像のSHAは`summary.json`。
- `summary.json`: 比較判定、差分画素と3×3近傍、cap観測、binary／probe／intake hash、回帰結果。`editmode.xml`: 匿名化した全件回帰。
- `physics-inventory.json`: 生の頂点座標を含めない親子・bone対応metadata。
- private scene／元モデル／材質、外部PlayerはGitへ入れない。元の製品sceneとXR設定は変更しない。

Builderは`Zantetsu.EditorTools.Sandbox.Compact16uvCharacterSceneBuild.BuildPlayer`、出力は環境変数`VP_CHARACTER_PLAYER_OUT`。最終binaryは外部`Compact16uvCharacterSceneCaps_20260924/CutWorldSandbox.exe`。実行時に`VP_CHARACTER_SCENE_DIAGNOSTICS`と`-force-d3d11`を指定する。集約は`Tools/Summarize-Character-Scene.py`。この集約は既知の1件失敗をそのまま検証する診断であり、全件合格するCIテストではない。

回帰XMLの初回取込みは`--xml <raw XML> --expected-pid <Unity PID>`でprocess IDを照合する。以後は保存済みの匿名化XMLを再検証する。UnityのTempは終了時にも掃除され得るため、再実行の`-testResults`にはTemp外の新しい保存先を使う。今回のXMLは同じPID 70028がpersistentDataPathにも保存した結果から回収し、最終記録へ固定した。

画像保存・CPU readback・Managed配列・比較用Mesh複製を含むため、Main時間／GC／メモリpeak／90fpsの性能結果には使わない。常駐16Bプールは維持するが、診断用Meshも保持している。製品向け最小常駐量を実証したものではない。
