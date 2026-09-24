# 修正版Character UCXの製品worktree取り込み

2026-09-24 / Unity 6000.3.22f1 / Blender 4.5.13 LTS。
開始製品commit `fe45914a`、アセット側修正commit `5233b74`。

## 結論と範囲

Casual `f_1` / Professional `c_1` の修正済みコピーを別private intakeに固定した。
**38/38 UCXがsource・Renderer-bind-localの凸性監査と、Unity現在poseでの対応照合・cook呼出し・単体切断を通過**した。
元入力の3形状が非凸という履歴は維持し、元版8条件も修正版8条件と一緒に実行している。

対象テスト16/16、EditMode全件3,638/3,638が合格し、双方failed 0・skipped 0。
証拠は [summary.json](summary.json)、[focused.xml](focused.xml)、[editmode.xml](editmode.xml)。

これはEditor componentの受入であり、19形状をまとめた製品ownerの登録、共通切断面での表示・物理同時切断、
Rigidbody挙動、DAG commit、scene表示の受入完了ではない。既存のscene/製品参照先はまだ切り替えていない。

| 入力 | Casual | Professional | pose条件数 | 切断step合計 |
| --- | ---: | ---: | ---: | ---: |
| 元版（比較用） | 17受理・2拒否 | 18受理・1拒否 | 8 | 280 |
| 修正版 | 19受理・0拒否 | 19受理・0拒否 | 8 | 304 |

各poseは基準、骨回転、祖先TRS＋非一様scale、root bone非一様scale追加。
元版の不合格3個は引き続きcook/切断しない。修正版は欠落・除外なし。
各hullで2回のJob切断（後半はpositive childの再切断）、体積和、入力不変、arena境界を検査した。
修正版のcook呼出しは152 hull-pose × 5 = 760回。これはエラーなしの呼出し確認であり、PhysX内部のcooked topologyのreadbackではない。

## 入力と元版の保持

- 元版: `Assets/Licensed/Compact16uvIntake/`。manifest、blend、physics fixtureのSHA不変を検査。
- 修正版: `Assets/Licensed/Compact16uvConvexRepair/`。新しいModel/Texture GUIDを発行し、その他の元importer設定をそのままコピー。
- 修正版の位置・属性・topology mapは元版を参照し、Blenderの表示頂点と全UCXのRenderer-bind-local / mesh-local頂点は元fixtureと完全一致。
- fixture内の面変更はCasual4面・Professional2面のみ。頂点追加・移動、weld、再convex化、許容緩和なし。
- アセット側で保存済みの修正blendをバイトコピー。製品側ではBlender保存や修復を行わない。
- `.blend`は元の外部テクスチャへの相対参照を含むため、portable packageとしては扱わない。Unity用に元版と同じpalette画像を隣接`textures/`へコピーした。
- private入力・生頂点・商用画像はGit対象外。公開証拠はSHAと集計のみ。

| family | blend SHA-256 | 新fixture SHA-256 |
| --- | --- | --- |
| Casual | `d31ae878ec44862b5e55ef06ac4ef530ae8ab294b000505291bfe2b817c10ab2` | `bbe481867248beef03e88446279e9246861a3ff95641e8e554b6b85a785451ed` |
| Professional | `5a0d35c40cf0a8cba49b91c35037b06459b8850835e14620ba40a7f07fa09071` | `452c746ef4801493a7c78a127291810064cdad218b4f6e78d3c1fcc911c61c4d` |

## Unity側の独立比較

従来の元版8テストを削除せず、同じ検証本体へ修正版8テストを追加した。
fixture SHAをテスト内に固定し、入力manifestだけの変更で合格データへ置き換えられないようにした。

修正版では、実import済みの元版と表示頂点・normal・tangent・UV・submesh index・全bone weight・頂点ごとのbone数・bindpose・bone順序が完全一致した。
Casualは4,728頂点、Professionalは3,834頂点で、双方1 submesh。

全38 UCXについて、実import済みMeshFilterの頂点を既存の明示mirror-X参照へ一意対応させ、全32元頂点へ到達することを検査した。
さらにimport済み三角形をfixtureと**向き込みで**照合し、Unity側が修正面を別の対角線へ戻していないことを確認した。
対応検索はテストoracle専用であり、実入力の頂点やtopologyを書き換えない。

骨への写像は `bindpose * rendererBindVertex`、現在poseでは `renderer.worldToLocalMatrix * bone.localToWorldMatrix` を適用する。
独立oracleは実importされたUCXノードの現在World座標。最大World差は `1.12461817e-6` で元版と同じ。
誤boneへ写すnegative controlは最大約0.49〜0.58 world unitずれ、正常経路と明確に区別できた。

座標照合許容と凸性監査許容は従来のまま。ProfessionalのRenderer scale 0.01によるlocal距離の違いを誤ってWorld誤差と扱わない。
修正版の表示データ一致はimport済みMeshの比較であり、今回新しいPlayer画像は撮影していない。従来のCasual1画素差は解消扱いにしない。

## 再実行と証拠

初回のみ `Tools/Prepare-Character-Convex-Repair.py` をBlenderの `--background --disable-autoexec --python-exit-code 1 --python` で実行する。
既存private intakeやexport証拠がある場合は上書きを拒否する。
upstream sibling repositoryと元private intakeが必要で、任意入力を自動修復するツールではない。

対象テストは `-runTests -testPlatform EditMode -testFilter Compact16uvCharacterPhysicsTests`。
全件回帰はfilterを外す。private fixture欠落のIgnoredは受入不可で、skip 0を必要とする。
結果XMLはTemp外のこのディレクトリに `focused-raw.xml` / `editmode-raw.xml` として出力する。
`Tools/Summarize-Character-Convex-Repair.py --focused-pid <PID> --editmode-pid <PID>` が件数・実行PID・入力SHAを照合して匿名化XMLと`summary.json`を作る。
元版の公開証拠ディレクトリ `compact16uv-character-physics/` は上書きしない。

## 次工程

形状由来の3件の拒否は修正版で解消した。次はこの修正版を用いて、19形状のcompound ownerを同一pose/frameへ登録し、
表示側と同じ切断面を使った分割・所属・commitを検証する。component合格だけで製品全体の受入や参照先切替を済ませたことにはしない。
