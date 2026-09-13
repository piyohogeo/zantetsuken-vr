# Phase 2.9 表示／Stencil共用メッシュ切断Kernel と Phase 0.21 初回Consumer の実施記録

2026-09-13。Unity 6000.3.22f1（Editor batchmode、`-nographics`）、Burst 1.8.30、Collections 2.6.8、Mathematics 1.3.3、Windows 11 Pro、Intel Core i7-14700。基準はDESIGN.md（commit d4bf466、2026-09-13 13:40 +0900、SHA-256 `d115f86e…3ff9e`）。移植元は `zantetsuken-mesh-cut-probe` commit c77dd96（`Docs/FINAL_REPORT.md` 2026-09-09）が選定したBurstの scan＋edge hash＋分割cap。

## 1. 成果物と場所

| 区分 | 場所 |
| --- | --- |
| 数値Kernel（Runtime） | `Assets/Zantetsu/Runtime/MeshCut/`（assembly `Zantetsu.MeshCut`：`MeshCutKernel`、`MeshCutCap`、`MeshCutScratch`、`MeshCutTypes`、`RenderVertex`） |
| 検証（Editor専用、製品Runtime外） | `Assets/Zantetsu/Editor/MeshCut/Verification/`（assembly `Zantetsu.MeshCut.Verification`：managed Geometry view、論理Topology Validator、double参照、契約Verifier、guard付きArena、Harness、Synthetic生成器、最小Job wrapper） |
| 標準回帰（EditMode、公開Syntheticのみ） | `Assets/Zantetsu/Tests/EditMode/MeshCut/`（`MeshCutKernelTests` 12件、`MeshCutPerformanceTests` 1件：product単独の記録用） |
| Phase 0.21 開発ツール | `Tools/ReferenceIntake/export_repaired_blend_to_fbx.py`、`Tools/ReferenceIntake/Register-ReferenceAsset.ps1`、`Assets/Zantetsu/Editor/MeshCut/ReferenceIntake/`（assembly `Zantetsu.MeshCut.ReferenceIntake`：FBX binary reader、FBX→Kernel入力、参考Run入口 `ReferenceCutRun`） |
| 非公開実体・登録・参考Run記録 | `zantetsuken-assets-private` branch `phase0.21-reference-intake`、`Working/Phase0.21/`（`blobs/`、`registry/`、`runs/`） |

## 2. 数値Kernelの契約（実装したもの）

**入力（`MeshCutInput`）。** グローバルAoS VB／IBのview（`RenderVertex*`＋要素数、`uint*`＋要素数。byte演算は64bit、要素数はint）、submeshごとのIndex範囲、`RenderCutTopologyMap`（render vertexブロック→Topology Vertex id）、同じ局所frameの平面 `s(x) = dot(n, x) + w`。全域viewはアドレッシング用で、走査するのは指定範囲とその参照先だけ。

**分類（§6.4）。** Topology Vertexごとに signed distance を一度だけ確定（初回参照のrender vertexのcanonical positionから）し、全incident Triangleで共有する。`d >= 0` をPositive。OnPlane（`d == 0`）はPositive所有、epsilon帯・第三状態なし。全頂点OnPlaneのTriangleは通常のPositive Triangleとして1回だけ保持し、Cap segmentを出さない。OnPlane頂点と負頂点を持つTriangleは通常の交差Triangleで、交点はその頂点位置に一致する別Topology Vertexになる（同一点複数port）。

**交点と属性。** 交点はTopology edge (lo, hi) をkeyに1個（両incident Triangleで共有、param `dLo/(dLo-dHi)` をdoubleで求めfloatへ）。位置は `(1-f)*pLo + f*pHi`（端点で厳密一致）。属性は面側ごとに、その面のrender pair (rLo, rHi) で補間（`(node, render pair)` をkeyに最大2 slot/node）。seamをまたいで混ぜない。Normal／Tangentは再正規化（cancel時は固定fallback）、tangent.wは低id端点から継承。

**Cap。** 元surface輪郭の逆向き（Cap traversal）に対し分割cap（simple cycleはreflex ear clipping＋z-order、非simpleは交差・接触点で分割し面積0スリバーで貼り戻す）。長さ2の閉輪郭は表面で閉じている（Cap不要）。Cap render vertexはnodeごと・側ごとに新規（surfaceとhard edge）。Normalは平面法線にCapの巻き方向の符号（面積和）を付けたもの、`uv0 = (-0.5, 0)` 固定、Tangentは平面U軸（w=1）。反転入力では反転したCapと反転Normal。

**出力（§4.5.3／4.5.6）。** 既存Vertexはグローバル番号を継承参照。新規render vertex（補間slot、Cap render vertex、aux）だけを一つの予約の先頭から詰め、両側で共有する。新規Indexは一つの予約へ `[正側 submesh 0..S-1 表面, 正側Cap][負側 …]` の順に書き、Capはその側の最後の非空submesh範囲に含める（Cap専用range／drawなし）。片側空なら0。全体が片側なら入力rangeを再利用し新規Index／Vertexを書かない（`reusesInput`）。K=0でも両側に別Componentがあれば振り分けて出力。Boundsは側ごと（表面頂点＋node）。Topology出力：新規vertexごとのTopology id（補間slotとCap render vertexは `base + node`、auxは `base + nodeCount + i`）、任意でnode→edge key／param。子のmapは親のブロック＋追加ブロック。

**容量。** `QueryCapacity` が分類passを走らせ（scratch不要）、T・K厳密、node≤2K、slot≤2/node、Cap render≤2/node、auxは見積り。`Execute` は予約外へ書く前に容量分岐し、`CapacityVertex`／`CapacityIndex` は正確な必要量、`CapacityScratch` は固定部厳密＋Cap arenaの倍を `required*` に返す。callerは非公開のまま再予約・再実行できる（テストで収束を確認）。scratchは呼出側提供の1ブロック（固定部＋aux record＋per-cycle bump arena）。

## 3. probe（移植元）との差と適合

| 項目 | probe | 本隊 |
| --- | --- | --- |
| 分類 | 3状態（`|d| <= 1e-6·extent` をon-plane、on-plane edgeの側bit、through-vertex、共面faceは法線反対側、面積0共面faceは拒否） | 2状態、OnPlaneはPositive所有、epsilonなし。全頂点OnPlane Triangleは保持・Cap segmentなし（§6.3／6.4）。面積0は入力・出力とも通常 |
| seam | C#基準実装のみ（Burst未移植） | Burst Kernelに面側ごとのrender pair slotを実装、Cap render vertexの分裂 |
| 出力 | 側ごとに全頂点を複製（materialize）、SoA | 既存Vertex継承、新規だけ追記、AoS、正負連続配置、submesh保持 |
| Cap UV | 平面座標 | 固定 `(-0.5, 0)`（§5.3）、再切断では通常補間で継承 |
| 契約内未対応 | 長さ3以上の逆向き輪郭は未検証（corpusになし） | 二重化領域はSynthetic回帰で確認：長さ2輪郭は表面で閉じ、平面上の平板は全体Positive再利用、2枚の両面シートの4世代再切断（往復する退化輪郭）は分割capが閉じないためfanで閉じる（下記） |
| 分割capの閉鎖確認 | disk関係式（三角形数）のみ | disk関係式に加え、cap自身のedge incidence（cycle edgeは1回・正方向、内部edgeは2回・逆方向）を確認し、満たさなければその輪郭をfan（0番node起点の扇、常に組合せ的disk）で閉じ `capFanFallbacks` に数える。同一位置の別Topology node（以前の切断由来のaux複製、OnPlaneの複数port）を持つ輪郭で発生する |
| 出力Validator | verifier分離 | 製品Runtime側にValidatorなし。検証はテスト側のみ |

発見・修正した点：初期実装では平面距離を角ごとに再計算しており、大きなT・小さなKの入力（16k Triangle・grazing）で移植元の1.74倍かかった。Topology Vertexごとの一度きりの確定（§6.4の文言どおり）へ変えた結果、全ケースで移植元より速くなった（§5）。

## 4. 標準回帰（公開Synthetic、非公開Asset・probe・Blender不要）

`Zantetsu.MeshCut.Tests`（EditMode）。すべてBurst実行を確認（managed fallback markerが0）。

| テスト | 確認内容 |
| --- | --- |
| RepresentativeInputs_ThreePlanes | box（n=4、seamあり／なし）、sphere（seamあり／なし）、torus、star prism（凹）、lemniscate tube（閉じた自己交差面）、nested shells（内側反転）、bar field（複数閉Component）、反転box、同一Topology重複Component、Overlapping Component、面積0 Triangle入り box、自己接触断面のprism、2 submesh box × 3平面（center／nasty／grazing）。各非空出力の閉鎖・edge／vertex manifold・局所winding、既存頂点の側、交点のdouble参照一致、面側ごとの属性補間（seam混合検出）、Cap marker／Normal符号／hard edge、正負連続配置、T+2K+caps恒等式、simple loopの面積恒等式、canonical position共有 |
| PlaneThroughVertices_Edges_AndAFace | 四面体の頂点通過、八面体の赤道（4頂点＋edge）、box中央ringのedge通過（seamあり／なし）、box上面が平面上（面はPositiveに残りCapが閉じる）、opposite coincident pair（2輪郭は表面で閉じCapなし）、面積0 Triangleの交差、notch先端が底辺に厳密に接する断面（同一点複数port・自己接触輪郭） |
| WholeMeshOnOneSide | 遠方平面での入力再利用（新規Index／Vertex 0、`QueryCapacity` が事前に告知）、下面接平面は全体Positive、平面上の平板（全頂点OnPlane）は全体Positive再利用・負側は真の空 |
| KZero_ComponentsOnBothSides | K=0で別Componentが正負に分かれる：nodeなし・Capなし・Index振り分けだけ |
| Seams_KeepSidesApart | UV seamでnodeごとに第2 render vertex、Cap render vertexは node×2側 |
| NegativeSourceUv | 負UVの元surfaceを拒否せず補間で保持、初回Capのmarker、Capを横切る再切断でmarkerを通常補間で継承 |
| RecutChain | sphere（seam）、自己交差lemniscate（aux付き分割cap）、同一Topology重複Component、2枚のopposite coincident pair、nested shells、自己接触prism を各4世代、出力をそのまま次回入力へ。既存Vertexの番号継承と世代ごとのTopologyブロック追加、fan fallbackの発生（coincident pairの4世代目で2輪郭） |
| TwoSubmeshes | submesh範囲の連続性、Capが最後の非空範囲に入り第1範囲にCapがない |
| CapacityDeficits | Vertex／Index 1不足で正確な必要量、scratch不足→再予約ループの収束、guard／入力hash／無関係領域不変。direct／Job両経路 |
| ScratchContents_JobPath | 0x00／0xA5／0xFF／乱数2種／Job経路／余剰scratchで出力hashがbit一致 |
| ConcurrentJobs | 同一不変入力を4 Jobが別予約・別scratchで同時切断、各出力を検証し逐次実行と一致 |
| InvalidReferences | view外index／未mapping頂点は `InvalidInput`（書込みなし） |

グローバルVB／IBは非zero base（例：vertex 1013／index 2047）と隙間付きで配置し、既存プール・隙間・予約末尾がbit単位で不変であることを毎回確認している。

再現：`powershell -NoProfile -ExecutionPolicy Bypass -File Tools\Run-UnityEditModeTests.ps1 -TimeoutSeconds 1800`（全EditMode）、または `Unity.exe -batchmode -nographics -projectPath <clone> -runTests -testPlatform EditMode -testFilter Zantetsu.MeshCut.Tests -testResults <xml> -logFile <log>`。

## 5. 性能（実Burst経路）

条件：Editor batchmode、`BurstCompiler.Options.EnableBurstSafetyChecks = false`、`CompileSynchronously = true`、warm-up 200回、その後300サンプルを probe→product→product+seams の順に交互実行、中央値[µs]。測定区間はKernel呼出しのみ（Verifier・I/O・出力読み戻しを含まない）。移植元は同一プロセス内のテスト専用コピー（probe c77dd96 の `CutJob`／`BurstCapping`、seamなし・game_vertex 9 float）で、同じ論理メッシュ・同じ平面・同じKで対比較した。このコピーは比較の記録後に削除し（commit「Add the mesh cut verification harness, EditMode tests and the temporary probe baseline」の `Tests/EditMode/MeshCut/ProbeBaseline` にGit履歴として残る）、恒久的な第二Kernelは置かない。残した `MeshCutPerformanceTests` はproduct単独（seamなし／あり）を同条件で記録する。

| 入力 | T | K | probe | product | product+seams | product/probe | 新規V product / 出力V probe | 出力Index |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | --- | ---: |
| box n=20 | 4800 | 182 | 159.3 | 110.5 | 103.7 | 0.69 | 546 / 2766 | 16572 |
| sphere 64×32 | 3968 | 170 | 119.1 | 81.7 | 78.9 | 0.69 | 510 / 2326 | 13932 |
| torus 96×48 | 9216 | 426 | 324.0 | 247.2 | 236.9 | 0.76 | 1278 / 5460 | 32736 |
| sphere 128×64 | 16128 | 338 | 369.8 | 275.1 | 267.7 | 0.74 | 1014 / 8742 | 52428 |
| star prism 32 | 1152 | 136 | 98.0 | 72.2 | 63.8 | 0.74 | 408 / 850 | 5076 |
| lemniscate 128（自己交差） | 2304 | 258 | 193.1 | 113.2 | 105.6 | 0.59 | 776 / 1672 | 10008 |
| sphere 128×64 grazing | 16128 | 74 | 186.3 | 137.1 | 136.0 | 0.74 | 222 / 8214 | 49260 |

処理範囲の違い：probeは両側の全頂点を複製（出力V列）し論理Indexを書く。productは新規render vertexだけを追記し（新規V列）、グローバルIndex列を正負連続で書き、seam slot・Cap固定UV・Bounds・Topology出力を含む。seamありは同じ論理メッシュでrender vertexが1.0〜2.3倍だが時間差は小さい（新規頂点数が数%増えるだけ）。個別ケースは±10〜20%のrunノイズを持ち、比の中央値だけを読む。旧runの絶対値との比較や固定倍率は合否にしていない。性能テスト自体は数値を記録するだけで、Burst実行と結果の妥当性以外を判定しない。

未測定：Player build、Safety Checks on（Editor既定）での数値、AoS strideの別layout、複数Jobの並列スループット。

## 6. Phase 0.21 初回Consumer（参考Asset）

**受入れ。** 利用許可のあるprobe入力（`zantetsuken-blender-pipeline-pilot` の mesh-repair recipe 1.15.6 のWorking `.blend`、ITHappy購入Asset由来、非公開repositoryに原本あり）から8件を選び、pilot付属のBlender 4.5.13（probeの書出しと同じ版。DESIGN §10.3.1の固定版4.5.12は本隊に未配置）で元ファイルを変更せず一度だけFBXへ書き出した（modifier未適用の基底メッシュ、corner法線・UV・接線、Unity軸、単位scale適用、packed textureを隣へ書出し）。FBX＋Textureが正本、`.blend` コピーとexport manifestは任意の参考資料として同じ登録に関連付けた。

**識別。** 実体はSHA-256のcontent-addressed blob。Asset SHA = `fbx:<sha>` と `texture:<name>:<sha>`（名前順）の正規文字列のSHA-256（表示名・配置・参考資料は含まない）。Dataset revision = Asset SHA集合（ソート）のSHA-256。保持revisionは `registry/datasets/<name>.json` に集合ごと残り、`registry/manifests/<assetSha>.json`（不変）とblobから読める。`resolve` で実体と参考資料の所在を引ける。今回のDataset `phase2_9-reference` は revision `7fa3726d…343e2`（8 Asset、blob 27件）。

**Topology取出し。** 独自のFBX binary reader（zlib配列、7.4／7.5レコード対応）でcontrol point・PolygonVertexIndex・ByPolygonVertexのNormal／UV／Tangent／Binormal・材質を読み、control point idをTopology Vertexとし、cornerを（control point, normal, uv, tangent）の厳密一致でrender vertexにまとめる（probe exporterと同じ規則、位置weldなし）。材質ごとにsubmesh範囲。単位は `UnitScaleFactor/100` でm。Model transformは書出し時にbakeし、非identityなら記録。

**参考Run。** `ReferenceCutRun`（`-executeMethod`、明示指定したdataset revisionまたはAsset SHAだけ）が各geometryをprobeと同じ4平面class（center／dense／grazing／nasty）で切り、テスト側Verifierで検証、大きい側をさらに再切断、Kernel時間の中央値（5回）を記録する。入力契約（§6.2）に合わない入力は事実だけ記録して切らない。記録は非公開側 `Working/Phase0.21/runs/<runId>.json|.md`。結果の要約は §7。標準EditModeは非公開repositoryを一切参照しない（Test assemblyはReferenceIntakeを参照せず、Runは `-executeMethod` 専用）。

## 7. 参考Runの結果

非公開側 `Working/Phase0.21/runs/` に3回のRunを残した（同一Dataset revision `7fa3726d…343e2`、各切断は5回反復の中央値、Burst safety checks off、`-intakeRepeat 5`）。Asset名や個別数値の対応表は非公開側のみに置き、ここには集計だけを記す。

| Run | Kernel／Verifierの状態 | 結果 |
| --- | --- | --- |
| `20260913-055327-e5b5c0` | fan fallbackなし、Verifierのsimple判定にnear-contact epsなし | 8 Asset・8 geometry・32切断すべてOk、Verifier合格30／32。再切断1件がCapFailed（分割capがdiskとして閉じない）、再切断4件でedge incidence違反（4面edge・boundary edge）、多数の微小sliver（sin 1e-6〜1e-4）に対する巻き方向警告 |
| `20260913-060848-56fe6f` | fan fallback（disk関係式不成立時）、Verifierのsimple判定をKernelと同じnear-contact epsに | 32切断すべてOk、30／32合格。再切断のedge incidence違反は残る（disk関係式は成立するがcapのedgeが2回・逆方向にならない） |
| `20260913-061559-b67146` | 分割cap後にcap自身のedge incidenceを確認しfanへ、Verifierの巻き方向判定をKernelと同じ射影で行いsliverのnoise床を除外 | **32切断すべてOk・32合格、大きい側の再切断32件もすべて合格**。fan fallbackは再切断の4輪郭（2 Asset）だけ。入力不適合0 |

最終Runの要約（`.md` 記録より）：

| 入力の規模 | T | render vertex／control point | Kernel中央値[µs]（center／dense／grazing／nasty） |
| --- | ---: | ---: | --- |
| 最大メッシュ（leisure、717 Component） | 29262 | 41554／16061 | 722／433／322／457 |
| Character（cartoon） | 4388 | 3994／2226 | 92／238／68／101 |
| Character（casual、self-intersecting loop） | 5418 | 5715／2751 | 92／462／82／108 |
| 建物（folded loops、163 Component） | 3944 | 8292／2240 | 118／164／46／285 |
| 小物（coincident face） | 576 | 1116／366 | 68／47／16／84 |
| 小物・landscape 3件（T=8〜52） | 8〜52 | 20〜96／8〜28 | 2〜13 |

比較可能性：probeの数値（`Docs/ITHAPPY_DATASET.md`、同Assetの `.mesh` 入力、seamなしgame_vertex、SoA）とは入力形式（FBX経由・Unity軸・seam付きAoS）と処理範囲（既存頂点継承・追記出力・固定Cap UV・Bounds）が異なるため、同一入力の性能比較としては扱わない。参考として、最大メッシュのcap込み切断がprobeの報告値（840 µs、cold.withcap、別マシン負荷）と同じ桁にある。Topology取出しは control point（=Blenderの論理頂点）とTriangle数がpilotの記録と一致した（例：4388 Triangle／2226頂点、24／16、8／8）。render vertex数はprobeのexporter（属性をround(6)で丸めて重複排除）と本reader（厳密一致）で異なり、同じ入力ではない。

実体への辿り方：`runs/<runId>.json` の各Assetに `assetSha`・`fbxBlob`・`references`（`.blend` コピー、export manifest）があり、`Tools/ReferenceIntake/Register-ReferenceAsset.ps1 -Root <private> resolve -AssetSha <sha>` または `resolve -Dataset phase2_9-reference` で `blobs/` の実体パスと参考資料を列挙できる。

## 8. 完了判定と残件

**全EditMode suite。** `Tools/Run-UnityEditModeTests.ps1 -TimeoutSeconds 3000`。probe baselineコピーを含む状態で Run ID `20260913-151657-f6b95a`、コピー削除後の最終状態で Run ID `20260913-152207-61451a`：いずれも7047／7047成功、skip・inconclusive 0、Git状態の変化なし。基底c430883時点の総数は固定せず、今回の追加はMeshCut 13件。

**Phase 2.9 の判定：完了。** 現行Unity環境でbuildと実Burst実行（managed fallback marker 0、Editor batchmode）、§6の共通契約（閉鎖・manifold・winding・OnPlane・退化・自己交差・別Topology重複・反転）、seam、Cap（固定負UV markerと再切断継承）、再切断、既存Vertex再利用、新規Vertex・正負Indexの直接配置、非zero base・疎参照、容量安全（予約不足の非公開失敗と正確な必要量、scratch再利用の決定性）、共有入力の並行Jobを代表入力で確認し、性能を実Burst経路で移植元と同一runで対比較した（0.59〜0.76倍）。製品アロケータ・Job wrapper・GPU・Renderer・Geometry Commitへは未接続。

**Phase 0.21 の判定：初回Consumerまで完了。** 少数実Asset（8件）で登録・Asset SHA・Dataset revision・保持revisionの読込み（`resolve`、blob hash照合）、表示切断Harnessへの限定サンプル投入（4平面＋再切断、5回反復）、実処理、結果記録、実体・参考資料の所在解決を通した。登録更新（同名Assetの再登録でhistoryへ旧Asset SHAが残る経路）はツール上あるが今回は実Assetで1回登録のみ。全Asset・全条件・全Consumer対応・probe数値との乖離解消は対象外。標準EditModeはDatasetを参照しない（Test assemblyはReferenceIntakeを参照せず、Runは `-executeMethod` 専用）。

**発見したKernel側の欠陥と修正。** (1) 角ごとの距離再計算による大T小Kでの退行（§3）。(2) 二重化領域・同一位置複数nodeの輪郭で分割capがdisk関係式を満たさない／満たしても閉じない（公開Synthetic「2枚のcoincident pairの4世代再切断」と実Assetの再切断で再現）→ cap自身のedge incidence確認とfan fallback（§3）。(3) 検証側：sliverの巻き方向判定をKernelと同じ射影・noise床で行うよう修正（Kernel欠陥ではない）。

**未測定・残件。** Player buildでの数値、Safety Checks on の数値、複数Jobの並列スループット、AoS layout変更時の再測定。fan fallbackした輪郭のCapは組合せ的にはdiskだが、退化でない自己交差輪郭に対しては重なり・反転Triangleを含み得る（最終Runでは再切断4輪郭のみ）。分割capを同一位置複数nodeへ強くする（連続する同一位置nodeの事前縮約など）は将来項目。

**Phase 3へ残る統合事項。** VPプール・範囲所有権（Reserved／Published）と再予約・再実行ループの接続、`RenderCutTopologyMap` のブロック管理（親ブロック＋追加ブロックの保持と退役）、新規Index範囲の1回転送・再利用時0回、Bounds／submesh範囲のDescriptor化、CutBoundaryRecord用のnode対応（`nodeEdgeKeys`／`nodeParams` は任意出力）、Cap UV marker（`uv0.x < 0`）のShader判定、`RenderVertex` layout（48 byte、float UV）の確定、Skinned入力のベイクとの接続。
