# Character compound / 共通面 / 実commitの統合検証

2026-09-24、開始commit `9a499e1`、Windows Editor / Unity 6000.3.22f1。
入力は前工程の `Compact16uvConvexRepair`。元版・修正版のblend、fixtureを変更していない。

## 結論：部分成立と新しいframe制約

**Casualの基準pose・骨回転poseは、19 UCXをまとめたownerと16B表示Geometryを同じ面で切り、実commit・再切断まで成立した。**
一方、Casualの祖先scale付き2条件とProfessionalの全4条件は表示登録で拒否される。
これは以前修正した非凸形状とは別の `F-CHARACTER-LINEAGE-SCALE` であり、全キャラクターの統合完了ではない。

| family / pose | 統合結果 | 原因・到達点 |
| --- | --- | --- |
| Casual 0 / 1 | 成立 | compound登録、共通面、物理公開、表示commit、positive再切断、移動後表示追従 |
| Casual 2 / 3 | 未成立 | 非一様scaleを含むlineage→Geometry写像が剛体限定gateに不合格 |
| Professional 0 / 1 | 未成立 | 元Renderer scale 0.01により同写像がscaleを含む |
| Professional 2 / 3 | 未成立 | 元0.01に祖先非一様scaleが加わる |

各条件を既存のメモリ初期状態 `-1`（製品のまま）/ `0` / `0xCD` で実行した。
成立は2 pose × 3状態 = **6ケース・12回の実commit**、拒否確認は6 pose × 3状態 = **18ケース**。
期待する拒否テストの合格を、製品統合の合格に数えない。

対象テスト24/24、EditMode全件3,662/3,662が合格し、failed 0・skipped 0。
この件数には上記18件の期待拒否を含む。証拠は [summary.json](summary.json)、[focused.xml](focused.xml)、[editmode.xml](editmode.xml)。

## 使用した製品経路

既存 `CutGeometryConnectionTests` をpartial化し、入力だけを実アセットに差し替えた。
`ProvisionalCutDriver`、`PhysicsOwnerRegistry`、`PhysicsCutCook`、`CutDag`、`VpLogicalCutDisplay`、
`VpDisplayGeometryCommit` と実GPU buffer転送、実Rigidbody/MeshColliderを使う。
既存fixtureと同じexecutorで仕事を実行し、テスト側は回収順序だけを制御する。
Runtimeコードは変更していない。

- Animatorを停止して明示poseを設定し、`BakeMesh(true)`からRenderer-localの16B表示入力を作る。
- 修正版fixtureの19 UCXをbindpose・現在boneから同じRenderer-localへ写す。
- Physics OwnerはRendererの位置・回転、scale 1の剛体frameとする。
- `G = ownerWorld.inverse * rendererWorld` とし、物理頂点はGを適用したowner-local、表示頂点はRenderer-localを保持する。
- physics shapeのLocalToOwnerはidentity。表示のGeometryLocalToOwnerはG、lineageToGeometryLocalはGの逆行列。
- colliderは実UCXを三角形化したもの。テスト箱や全体AABBに置き換えない。
- 第1面はcompound ownerのaxis 1 bounds、第2面はpositive childのaxis 0 boundsの56.85%位置。同じ採用面を一度だけDriverへ渡す。

成立したケースでは以下を確認した。

1. 初期ownerに19形状・19 collider。全入力形状のsupport分類を独立計算と照合。
2. 表示が先なら物理子の公開までcommitしない。物理が先なら表示Geometryを待たずにhandoffする。2回の切断で両順序を検査。
3. child数は入力数＋Split数となり、継承形状を落とさない。再切断ではSplitと正負の継承が共存する。
4. 体積和、親Rigidbody質量に対する正負質量和、子のRigidbody→fragment解決、collider数が成立する。
5. 物理と表示の全参照頂点が同じowner-local半空間へ所属する。表示Kernelの面は独立な `transpose(G) * plane` と照合する。
6. 実commitにより旧owner/旧display Geometryが退役し、未完了予算が0へ戻る。子Geometryを次回の実入力として読む。
7. 初期16B payload・初期B-rep不変、arena guard正常。実ownerを移動・回転した後も表示登録行列が追従する。
8. 既存のguard付き終了手順で仕事の終了を確認して資源を解放する。

体積はconvexの加算であってBoolean unionではない。親質量12はfixture値であり、実キャラクターの質量推定を評価したものではない。
材質は既存fixtureの診断材質で、paletteの画像比較は行っていない。Physics simulationもstepしていない。

## 成立ケースの形状配分（3初期状態で同値）

| pose / stage | 入力 | Split | positiveへ継承 | negativeへ継承 | 正負出力形状数 |
| --- | ---: | ---: | ---: | ---: | --- |
| Casual 0 / 0 | 19 | 19 | 0 | 0 | 19 / 19 |
| Casual 0 / 1 | 19 | 9 | 3 | 7 | 12 / 16 |
| Casual 1 / 0 | 19 | 19 | 0 | 0 | 19 / 19 |
| Casual 1 / 1 | 19 | 4 | 2 | 13 | 6 / 17 |

## scale条件が止まる理由

`VpMultiCutSnapshot.IsWithinInputContract` は表示配置自体には有限・可逆なaffine変換を許す一方、
`lineageToGeometryLocal` には `IsRigid`、すなわち正規直交回転＋並進、determinant +1を要求する。
`VpLogicalCutDisplay.TryShow` はこのgateをGPU uploadより前に通す。
既存 `VpMultiCutSnapshotTests.TheInputContract_IsCheckedWithNoPlaneToConvert` もscale写像の拒否を明示的に検査しており、単なる偶発的な失敗ではない。

今回の拒否テストでは、同じbounds・同じ表示配置でlineage写像だけidentityにした契約検査は合格し、
実際のGの逆行列を与えると不合格になることを局在確認した。identityを実際の登録に使って面をずらす回避は行わない。
実TryShowの拒否、GPU vertex/index upload 0、cut/commit 0、19形状を保持したsourceを確認した。
lineage写像のdeterminantはCasual 2/3で約0.874126、Professional 0/1で1,000,000、2/3で約874,126。

物理側も `PhysicsOwnerBuildOutcome.FrameNotRigid` により数値local→Ownerのscale/shearを拒否する。
物理頂点をRenderer-localのまま置き、scaleをcollider Transformに渡す案では解消しない。

## 初回失敗を保持

初回24条件は全失敗した。18件は上記frame拒否。
残る6件は第1回commitまで通った後、解放済みDAG仕事から診断用kernel planeを読もうとしたテスト計測位置の誤りだった。
plane取得を仕事が保持されているcommit前へ移し、Runtimeの寿命契約を変更せず訂正した。
初回XMLは `initial-failures.xml`、訂正後の成立/拒否テストは `focused.xml` に別保存する。

## 再実行・未完了

対象filterは `RepairedCharacter_`、全件回帰はfilterなし。
`Tools/Summarize-Character-Compound.py` が初回/対象/全件の実行PID、結果件数、入力SHA、資源解放を照合し匿名化XML・集計を作る。
生XMLとlogはGit対象外。private fixtureが無い場合のIgnoredは受入不可で、検証ではskip 0を要求する。

次工程にはframe契約の選択が必要。Renderer-local入力維持を優先するなら、**表示側のlineage写像をscale対応へ拡張する案を別途検証**する。
その場合も剛体Ownerへ渡す物理形状の契約は維持し、単に`IsRigid`を外すのではなく、plane、仮cap、epsilon、登録/commit、再切断の前提を一緒に確認する。
別案は表示も含むGeometryを共通剛体frameへ変換することだが、現在のRenderer-local契約と頂点準備工程の変更になる。
この判断は未実施で、Professionalを合格扱いにするためのscale除去・許容緩和・入力上書きはしていない。

実AnimationClip、Rigidbody simulation、Player画像・仮Stencil/Shadow/XR、性能、IL2CPP、製品sceneの参照先切替は未完了。
