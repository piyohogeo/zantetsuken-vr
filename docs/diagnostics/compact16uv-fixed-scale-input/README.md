# 固定scale正規化のRuntime入力境界・共有・Player確認

2026-09-24、基点 `71b04d9a`。製品worktree `compact16uv-product-migration`。

## 実装範囲

前工程のテスト専用変換を `Zantetsu.Rendering.VpFixedScaleSkinCache` / `VpFixedScaleSkinInput` に移した。現状の製品にはSkinned専用の汎用Loaderがないため、共通のRuntime入力準備APIを追加し、既存のキャラクター診断sceneと実Driver／物理／表示commitの統合テストから使う。通常Gameplay sceneの自動移行、Importer hook、元アセットの書換えではない。

所有関係は「asset/scene scopeのcache → 各rig instanceのinput → unit-scale Renderer」。呼出し側がinputを破棄してからcacheを破棄する。

- 同一のsource Mesh参照・固定scaleをキーに準備Meshを共有する。正の一様scaleのみ。Meshは登録中に変更しない契約。
- scale 1は元Meshを借用し、追加Meshを作らない。0.01は頂点とbindposeを一度だけ変換する。inputを一旦全て解放しても、cacheの寿命中は準備Meshを再利用する。
- instanceごとの骨配列・rootBoneは元rigを共有する。元Rendererの描画設定・material property blockを引き継ぎ、元Rendererを停止、準備Rendererを元のenabled状態で開始する。
- `BakeCurrentPose`はunit-scale frameを確認して `BakeMesh(true)` を呼ぶ。Mesh全体のscale補正、追加16B packは行わない。元／準備Meshへの破壊的Bakeは拒否する。
- input解放時に準備Rendererを即座に無効化し、元Rendererのenabled状態を復帰する。PlayerではObject破棄はdeferred。cacheは生存inputがあれば破棄を拒否し、最後に自身が複製したMeshだけを破棄する。
- 正規化後も外側の親はunit scaleを維持する。動的scale・反射・shear・blendshapeは対応範囲外。原rig/UCX階層は変更しない。

Main threadでの読み込み／解放用APIであり、UpdateごとにPrepareする設計ではない。Rendererのenabled/material等をAnimationClipが直接駆動する場合のbinding移送や、rigの外側の独立したRenderer Transformアニメーションは一般化していない。明示寿命APIなので、任意GameObjectの削除だけでinput/cacheが自動解放されるとは扱わない。

## 統合・寿命検証

共有、別rig、scale別cache、unit Mesh借用、再利用、元表示復帰、早すぎるcache解放拒否、二重Dispose、Mesh所有権、blendshape／非一様・負・ゼロscale拒否、設定・property block維持をテストした。既存の各300決定的姿勢比較と、Casual／Professionalの19形状compoundの切断・再切断36commitも新Runtime入力経路で実行した。

外側親の非一様scaleによる登録拒否12件は引き続き期待拒否として分離する。入力API自身のBakeでもframe違反を拒否する。剛体lineage契約の緩和や形状の暗黙修復はない。

Focused 40/40、実Clip比較2件を追加した全EditModeは3704/3704、skip 0。ただし「実モーション確認」は未完了。import済みClipは存在するものの、下記の通り静止Clipだったためである。

### Clipの実体

両familyに `CanonicalSource`（10.375秒、3580 float curve）が1本ずつある。全curveを調べると値が変化するcurveは**0本**。各120時点で比較したWorld頂点の時間変化も**0**だった。正規化前後の最大World位置差はCasual 0、Professional 4.771e-7、World法線ベクトル差は0／2.170e-7。

これは静止Clipを実際に読み込んでサンプリングする経路の成立であり、歩行・戦闘等の実モーションを通した検証ではない。前工程から継続している300決定的Transform姿勢の動的比較とは区別する。対応rigの動くAnimationClip／FBXが別途必要。Clipを自作したり静止Clipを動いているものとみなして代用しない。

## Player

Unity 6000.3.22f1、Windows x64 IL2CPP Development、D3D11 mono。修復版2アセットのSHAをBuilderで照合し、専用private sceneから同一binaryを3独立processで実行した。XR設定はbuild時だけ一時停止して復帰する。通常製品sceneの参照先は変更しない。

各familyのimport済み静止 `CanonicalSource` Clipをlengthの23% / 67%で `SampleAnimation` し、後者はunit-scale共通親の移動・回転も加える。サンプリングは診断の骨回転・rootBone scaleをClip値へ上書きするため、Playerの2条件は同じ静止骨姿勢＋外側剛体配置の比較として扱う。Animator state machine／root-motion playbackではない。元Skinned表示、正規化後Skinned表示、Bake通常Mesh、16B VPを別々に撮影する。Rendererのscaleや違反許容を画像比較のために緩めない。

初回3実行すべてで28/28の厳密画像gateが合格し、各56画像のSHAもprocess間で完全一致した。元Skinned→準備Skinnedの4比較はpixel完全一致。準備Skinned→Bakeは輪郭差0・RGB最大差1/255。各stageの通常Mesh→VPでは輪郭差0を維持し、切断／再切断後は同じ公開済み出力の復号Meshをoracleとする。許容は前工程と同じ輪郭差0・RGB最大3/255。

キャプチャは512×512、MSAAなし、固定half-Lambert診断shadingと共有palette。各runは56画像。両切断stageでcap側を向くcameraを使用し、normal/debugのcap色切替を確認する。代表画像を目視でも確認した。Player側は16B storage単体の切断であり、物理owner／DAG commitの成立は別のEditMode統合証跡。両者を一つのGameplay scene検証として合算しない。

以前の外側非一様scale付きCasualの1 pixel差とは入力条件が異なる。今回28/28であっても、過去の `F-SKIN-RASTER` を解決・撤回したとはしない。

## CPU API費用とMesh追加量

各processで1回のcache miss準備、32回のcache hit準備、32回のBake warmup後に元／準備済みを交互順で各256回測定した。表は3 processの代表値（準備missは単発値の中央値、hit/Bakeはprocess内中央値の中央値）、単位µs。

| family | cache miss準備 | cache hit準備 | 元Bake | 準備済みBake | 追加Mesh量 |
|---|---:|---:|---:|---:|---:|
| Casual | 180.8 | 142.3 | 113.8 | 114.3 | 0 B |
| Professional | 418.7 | 130.2 | 114.3 | 96.5 | 749,112 B（約732 KiB） |

missはコード・assetが既に使用された状態での新cacheへの初回登録であり、プロセス初起動・初importのcold性能ではない。hitは新instance用Rendererの作成等を含み、Meshを再変換しない。input解放とdeferred destructionはこの時間に含まない。Bake測定には入力APIのframeチェックを含むが、16B変換、転送、物理、commit、全frameの費用は含まない。一般的な高速化／90fps保証や「切断Main費用増0」の実証ではない。

Mesh量はUnity `Profiler.GetRuntimeMemorySizeLong` によるObject単位の値であり、CPU頂点だけの量やGPU residencyではない。元Meshを保持するためProfessionalでは追加分が実際に発生する。cacheを共有すれば同一アセット・scaleで増える準備Meshは1個だが、rendererはinstanceごとに増える。rig、renderer、作業配列、読み込みpeak、16B pool、全sceneメモリはこの値に含めない。アセット生成／importで正規化を永続化して元のランタイム表現を不要にする最適化は未実装。

## 証跡・再実行

- `focused.xml`: Runtime入力・既存固定scaleテスト40件。追加の実Clip比較は全件回帰側に含む。
- `editmode.xml`: 実Clip比較を含む全3704件回帰。件数・skip・PIDは集約スクリプトで検証。
- `summary.json`: 画像gate、再現性、全PNG hash、Clip比較、API費用のprocess間範囲、binary/source hash。
- `run1/`: 代表56 PNG、report、trace。run2/3はreport/traceを保存し、重複PNGはローカルのみ。
- `Tools/Summarize-Fixed-Scale-Input.py`: PNGから被覆・輪郭・RGB差を独立再計算、XMLを匿名化、3processを集約。

Builderは従来の `Compact16uvCharacterSceneBuild.BuildPlayer` を `VP_CHARACTER_FIXED_SCALE=1`、`VP_CHARACTER_PLAYER_OUT` 指定で実行する。今回の外部出力名は `Compact16uvFixedScaleInput_20260924`。Playerには `VP_CHARACTER_SCENE_DIAGNOSTICS` と `-force-d3d11` を指定する。private scene、元モデル、binary、raw XML/logはコミットしない。

残工程は実際のcharacter Gameplay intakeでのcache/input寿命接続と物理ownerを含むPlayer検証、Animator駆動・root motion、切断フレーム全体のMain／メモリpeak測定。自前Burst skinning、XR、仮Stencil、Shadow、外側動的scaleは今回の合格範囲に含めない。
