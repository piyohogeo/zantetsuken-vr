# VP Cascade ShadowCaster の巻き方向調査

対象: Unity 6000.3.22f1 / URP・Core 17.3.0 / D3D11 / NVIDIA RTX 3090 / Quest 3SのQuest Link SPI（90Hz）。ブランチ `vp-stage3-indexed-indirect-probe`、調査開始時 HEAD `297c70d`。

## 結論と修正

注入 raster pass の直前に、空の `RenderGraph.AddUnsafePass` を追加した。`AllowGlobalStateModification(true)` で順序を固定し、空パスの除去を防ぐ。その後の raster pass は既存の影 atlas を `AccessFlags.ReadWrite` で使用する。`Auto` は `Cull Back` を選ぶ。

この処理は、**直前の native render pass との結合を切り、注入描画の前に新しい native render pass を開始する**。後続パスとの結合まで禁止するものではない。カメラ行列・XR の行列・`SetInvertCulling` は変更せず、記録時の `GL.invertCulling` も参照しない。

同じVPジオメトリを描く比較条件では、EditMode RT・非XR Player・Quest Link SPIで実 atlas のtexel一致を確認した。ただし、**個別選別や他のcasterを含めた全atlasの無条件な完全一致までは達成していない**。元の実装にもある差分を下記に分けて記録している。XRの通常Forward画像も、追跡変動があるため厳密一致の合格とはしていない。

公開 API で native pass 境界を作れることはソースで確認できる。一方、D3D11 の front-face state をエンジンが再計算する詳しい条件は native engine 内部にあり、公開 C# ソースからの証明はできない。したがって巻き方向に関する結論は、この固定バージョンと実測経路の範囲に限定する。

変更ファイル:

- `Assets/Zantetsu/Runtime/RenderingUrp/VpCascadeShadowPass.cs`: 境界パスとAutoの選択方法、説明コメント。
- `Assets/Zantetsu/Runtime/Rendering/VpCulledIndexedShadowCaster.shader`: 説明コメントのみ。頂点変換・bias・shader passの内容は変更なし。
- `Assets/Zantetsu/Tests/EditMode/RenderingUrp/VpCascadeShadowPassTests.cs`: 実atlasを比較する2ケースと読み戻し。
- `Assets/Zantetsu/Tests/EditMode/RenderingUrp/VpShadowMapCopyPass.cs`: 基準フレームのsnapshot対応。
- 本報告書。元のprobeファイルの未追跡状態は維持し、commitは作っていない。主要3ファイルの開始時snapshotからの差分は`Logs/VpCullWinding/review.patch`にも保存した。

## ソースから確認できたこと

以下のパッケージは変更していない。

- URP: `Library/PackageCache/com.unity.render-pipelines.universal@de1a320b3ed3`
- Core: `Library/PackageCache/com.unity.render-pipelines.core@202510c2d2a2`

| 確認事項 | ソース位置 |
| --- | --- |
| Main/Additional shadow → カメラ状態の再設定 → `AfterRenderingShadows` の順で記録する | URP `Runtime/UniversalRendererRenderGraph.cs:747–764` |
| カメラ再設定は attachment を持たない raster pass で、`cmd.SetupCameraProperties(camera)` を実行する | URP `Runtime/ScriptableRenderer.cs:994–1047`、呼出しは1020行 |
| `_ProjectionParams.x` の処理は GfxDevice 内部で、カメラ再設定が他の shader properties も上書きすることを明記 | 同 `1010–1040` |
| attachment がない raster pass を native pass に結合することは意図された動作 | Core `Runtime/RenderGraph/Compiler/PassesData.cs:848–855` |
| 異なる depth texture への描画は `DifferentDepthTextures` で結合を拒否する | 同 `867–875` |
| non-raster pass は native pass chain を切る | 同 `839–843`、`NativePassCompiler.cs:673–676` |
| 次の raster pass で `BeginRenderPass` を発行する | `NativePassCompiler.cs:2015–2022` |
| 直前の native pass は `EndRenderPass` で終了する | 同 `2078–2099` |
| global-state 許可は pass culling を無効にするが、raster pass の結合禁止ではない | Core `Runtime/RenderGraph/RenderGraphBuilders.cs:64–72` |
| `ReadWrite` depth の読み込みと後続利用により atlas の Load/Store を維持する | `NativePassCompiler.cs:1297–1325` |

`AddUnsafePass` の公開 API 説明にも native pass の分離に関する説明がある（Core `RenderGraph.cs:1448–1455`）。[Unity 6.3 マニュアル](https://docs.unity3d.com/6000.3/Documentation/Manual/urp/render-graph-unsafe-pass.html)も、unsafe pass により結合最適化が制限されることを説明している。

空の unsafe pass 自体は render target や資源を触らない。Core の `NativePassCompiler.ExecuteSetRenderTargets` は attachment がない場合、target 設定を行わない（1864行以降）。資源の寿命は前後の raster pass の atlas 依存で保持される。

この構造は、依頼書の「実 atlas と別 depth target で正しい cull が違う」「公開 UV origin は同じ」という観測と整合する。`GetTextureUVOrigin` はテクスチャの座標系を表し、現在の native winding state を問い合わせる API ではない。`camera.targetTexture == null` による環境別の Front/Back 判定は採用していない。

## 検証方法と記録

証拠・再現用スクリプト・深度ダンプは `Logs/VpCullWinding/` に保存した。Player ビルドでは専用の `Assets/_TempVpCullWinding` に一時ソースを配置し、終了時に削除する。元のバッチソースに計測用変更を加えず、PlayerSettings の明示変更も行わない。ビルドによる設定ファイルの変更は事前コピーで復元し、ハッシュで確認する。

### EditMode

- 修正前の既存6テスト: 6/6成功（`baseline.xml`）。別 target 比較は Auto/Back が差0、Front は546,249 texel差。
- 修正後の最終テスト: 8/8成功（`final-editmode.xml`）。元の6テストに、実際の URP atlas を別フレームの基準と比較する2ケース（post-processing off/on）を追加。
- 実 atlas の正確な比較では、両者が同じジオメトリを描くよう影の個別選別を無効にし、VP以外のcasterを除外。Auto/Back は4,194,304 texelすべて一致、Front は546,249 texel差。
- 元の画像比較は、地面の影、カスケード選別、Forward選別、soft shadows、SceneView型カメラを引き続き含む。色の比較は既存どおりチャンネル差2超を差分とする。

### Player

通常の描画経路では `VP3`（基準）→`VP3SP`（Forward＋注入）→`VP3C`（Forward選別＋影選別）→`VP3` の順に、実 atlas とフレーム画像を取得する。最後の基準で時間変化も確認する。別 target の Auto/Back/Front 比較も併用する。

修正前 Player の非XR/alongでは、`VP3SP`、`VP3C` とも1,180,619 texel差、基準の反復は差0だった。別 target は Auto/Back が差0、Front が1,178,131 texel差。この記録は `player-xroff-auto-20260915-200157-1ccf200f/comparison.json` にある。

XRでは頭部姿勢がフレーム間で変化するため、別フレームの完全一致だけでは判定できない場合がある。このため、診断用に同一フレームの URP atlas を保存→実 atlas を clear→同じ実 atlas に注入→前後の深度を比較する経路も用意した。この診断では同じ影行列を比較できるが、**事前コピー自体が native pass 結合を切るため、元の結合不具合の再現には使えない**。通常描画経路の検証と組み合わせる。

修正後の測定結果:

| 経路・条件 | 影マップ | 画像／対照 |
| --- | --- | --- |
| 非XR通常経路、along/across、影選別off、地面caster off | `VP3SP` / `VP3C` は両視点とも全4,194,304 texel一致 | 画像は許容誤差を使わない比較でも差0。Forward選別ありのVP3Cも同じ |
| 非XR通常経路、top/along/across、影選別on | 基準との差は順に0 / 102,479 / 279,034 texel | 3視点すべて画像は厳密差0 |
| 非XR通常経路、top/along/across、影選別on、地面caster on | 基準との差は順に0 / 102,298 / 273,816 texel | 3視点すべて画像は厳密差0。地面を含む既存影の見え方も維持 |
| 非XR同一フレームの実atlas、along | Auto/Backとも差0 | Frontは1,178,131 texel差 |
| Quest Link SPI同一フレームの実atlas、along/across | Auto/Backとも両視点で全4,194,304 texel一致 | Frontは順に926,543 / 915,471 texel差。参照・結果の非空性と実行ログも検査 |

主な証拠ディレクトリ（すべて `Logs/VpCullWinding/` 配下）:

- `player-xroff-auto-20260915-200856-42d54113`: 非XR、影選別off、通常経路の完全一致。
- `player-xroff-auto-20260915-200755-3e66fe14`: 非XR、影選別on、通常画像一致とatlas差。topの別target比較にも136,566 texel差があり、この条件を完全一致の根拠には使っていない。
- `player-xroff-auto-20260915-201719-92cf3618`: 非XR、影選別on・地面caster onの3視点。
- `player-xroff-auto-20260915-201120-388e413a`: 非XR同一フレーム実atlasの対照検証。
- `player-xron-auto-20260915-201211-9ef0e179`: SPI同一フレーム実atlasの検証。生の参照と結果を別ファイルで保持。このセッションは途中でFOCUSEDからVISIBLEへ遷移したが、SPI描画と両方のreadbackは継続した。性能検証には用いていない。
- `player-xron-auto-20260915-200938-cd4d479c`: SPI通常経路、影選別off、Forward選別on。基準反復にもalong 396,442 / across 119,433 texelの変動があり、別フレーム完全一致は判定不能。

さらに、修正前Playerで正しい向きの`Front`を強制した`VP3C`と、境界追加後の`Auto`（Back）の`VP3C`を直接比較した。影選別onのalong/acrossで**実atlas同士が全texel一致**した。記録は`player-xroff-front-20260915-201646-9ed00d50`と`original-front-vs-boundary-auto.json`。このA/Bは、境界追加で必要なCullが変わったことと、選別ありの102,479 / 279,034 texel差が今回追加されたものではないことを支持する。

SPI通常経路の画像は1600×900の両眼ミラー。元の計測と同じ「RGBチャンネル差32超・8近傍連結」で、alongの最大差分領域は基準反復66/80 px（左/右）、VP3SPは最大121/110、VP3Cは140/131。acrossは基準28/24、VP3SPは24/16、VP3Cは21/30だった。大きなForward崩壊は確認していないが、一部が基準変動を上回るため、すべてを追跡ノイズと断定しない。Forward選別はalong 403/960、across 570/960、両眼の`misses=0`、`eye_matrix_max_diff=0`を確認した。

Quest Linkを再接続した後、**影の個別選別on**でも通常経路を計測した（`player-xron-auto-20260915-201810-0ab9c886`）。カスケード別選択数はalong `611/960/960/960`、across `272/620/621/621`、両眼の`misses=0`。最大画像差分領域はalongの基準反復109/98 pxに対してVP3C最大140/140、acrossは基準39/42に対してVP3C最大36/39だった。こちらも基準が変動するため厳密一致は未判定。途中の起動失敗run（201313、201503）は検証結果に含めていない。一時的な装着検知シミュレーションは`automation_disable`で通常状態へ戻した。

### 合格範囲と残件

巻き方向の問題については、設定変更なしの公開API対策を実装し、同一ジオメトリ条件の3経路で実atlas一致を確認した。非XRの通常経路では、元の正しいFront強制の結果と修正後Auto/Backも一致する。

依頼の「カスケード選別・他casterを含めても常に全atlasが完全一致」を無条件な合格条件とする場合は、**未達**である。以下は別途判断・調査が必要:

- 個別選別で描かなくなるatlas領域も、単一batchと同じ値を要求するか。画像の一致と全atlasの一致は今回同値ではなかった。
- 地面を含めた1量子の深度差の詳細原因。
- 追跡姿勢を固定した条件などでのXR通常Forward画像の厳密比較。
- native pass境界による性能への影響。

再現用の実行例:

```powershell
python Logs/VpCullWinding/prepare_player_sources.py
& Logs/VpCullWinding/Build-ValidationPlayer.ps1
& Logs/VpCullWinding/Run-ValidationPlayer.ps1 -Xr off -CullSplits off -Views 'along,across'
& Logs/VpCullWinding/Run-ValidationPlayer.ps1 -Xr on -GroundCaster off -Views 'along,across' -ImageSteps 'VP3actualA_a,VP3actualB_a,VP3actualF_a'
```

正常起動した各runはPlayerを自動終了する。比較が失敗／基準が変動した場合は非ゼロ終了で、合格に読み替えない。

## 巻き方向と区別した既存差分

追加検証で以下の2点を発見した。元のパス実装に戻したA/B比較でも同じ値を再現しており、native pass 境界追加による回帰ではない。

1. **個別カスケード選別と全 atlas の比較**: EditModeのsceneでは、個別選別を有効にすると50,039 texel（地面あり50,444 texel）が異なる。元の単一batchは大きな共有boundsを使って全インスタンスを提出するため、より細かい選別とは描くジオメトリが同一ではない。画像比較は一致している。記録: `original-culling.xml` と `boundary.xml`。
2. **他のURP casterを含む深度**: 地面あり・影の個別選別なしでも1,072 texelが異なる。差はcascade 2で841、cascade 3で231。両側とも書き込み済みで、D16換算の差は+1が1,071 texel、−1が1 texel。繰り返した基準は差0。丸めの詳細原因は未特定。元の実装でも同じ値で、全 atlas の厳密一致という一般的な主張はこの条件ではできない。記録: `original-ground.xml`、`boundary-exact.xml`、`ground-probe-post-*` のRFloatダンプ。

テストの許容誤差を広げてこれらを合格扱いにはしていない。巻き方向の全texel一致テストは同じジオメトリを比較する条件に限定し、地面・選別を含む既存画像テストは維持した。

## 他の候補と最小URP変更案

### カメラ行列を上書きして復元する案

URP自身の `ShadowUtils.RenderShadowSlice` は、CPU側のview/projectionを `cmd.SetViewProjectionMatrices` に渡す（`ShadowUtils.cs:276–284`）。一方、RenderGraph用の `ScriptableRenderer.SetCameraMatrices` と `SetupRenderGraphCameraProperties` は internal（`ScriptableRenderer.cs:232,994`）。公開 `CommandBuffer` 版のカメラ復元helperは `URP_COMPATIBILITY_MODE` の条件付きである（194–230行）。

非XRなら公開 `cameraData.GetViewMatrix()/GetProjectionMatrix()` を戻す案、SPIなら公開 `XRBuiltinShaderConstants.UpdateBuiltinShaderConstants` と `SetBuiltinShaderConstants` を併用する案は残る。ただしXR内部は更新をキャッシュする（`UniversalCameraData.cs:37–85`）ため、後続URPパスが自動で上書きを直すとは限らない。完全な状態復元を保証する一つの公開APIも見つかっていない。

`SetViewProjectionMatrices` だけが clip planes や shader time を変更するという根拠もないため、「これらの復元に必ずSetupCameraPropertiesが必要」とまでは断定しない。今回は行列上書きを必要としない境界方式を検証した。

### URPに拡張点を設ける場合

公開APIの境界方式を採用しない場合は、MainLightShadowCasterPassの各slice内に追加描画hookを設けるのが小さい。`ShadowUtils.RenderShadowSlice` のrenderer-list描画直後、scissor/depth biasを戻す前（281–284行）で呼び出し、cascade index・正確なslice・bias・light情報と描画用command bufferを渡す。使う外部bufferの依存もpass記録時に宣言できるようにする。

これならURPと同じ行列・viewport・winding・depth biasの範囲で描け、後続のURP標準カメラ再設定が復元を担う。`RenderShadowSlice` の戻り直後のcallbackでは、すでにリセットされたdepth biasを再設定する必要がある。packageへの変更は今回実施していない。

BRG はvariant stripping設定の変更を伴うため、この調査では実装・設定変更を行っていない。

## 適用範囲

- 現在のmain atlas／diagnostic targetはBottomLeftであり、`GL.GetGPUProjectionMatrix(proj, true)` と組み合わせる。任意のTopLeft targetや別graphics APIへの一般化は未検証。
- native render passesを無効化した別設定は未検証。従来compilerはraster passごとにtargetを設定するが、今回の境界効果とは区別する。
- 境界によりshadow atlasのStore/Loadが増える可能性がある。性能測定は今回の正しさの検証とは別途必要。
- URP package・renderer asset・ProjectSettings・DESIGN.mdを変更する恒久対策は含まない。
- 最終確認時、ProjectSettings／Packagesの事前ハッシュからの差分は0。DESIGN.md・PC_Renderer.asset・PC_RPAsset.assetのSHA-256も開始時と一致し、一時Assetsフォルダーは残っていない。ビルド時にUnityが再保存したPC_RPAsset.assetは事前のバイト列に復元している。
