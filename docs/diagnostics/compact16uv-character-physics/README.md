# 骨Physics Proxyの現在pose対応と凸性gate

2026-09-24 / Unity 6000.3.22f1 / Blender 4.5.13 / 開始commit `941844ee`。

## 結論

Casual／Professionalの各19 UCXをRenderer-localへ対応させる座標検証は成立した。一方、**元authoringの3 UCXが既存凸性監査に不合格のため、キャラクター全体の製品物理登録は保留**する。不合格を省いた35形状だけで完成したownerとはしない。

- 全38形状・1,216頂点について、基準pose／骨回転／祖先非一様scale／root bone非一様scaleの8条件が合格。World位置の最大差は1.12462e-6（約1.13µm、1 Unity world unit = 1mとした換算）。
- 凸性合格はCasual 17/19、Professional 18/19。この35形状を各poseでcookし、各2回のJob切断／再切断、体積和、入力不変、arena境界を確認。計280切断stepが合格。
- 不合格3形状は座標照合だけを行い、cook／切断／owner登録はしていない。座標テストの合格と、全物理入力の合格を混同しない。
- 全件回帰3,630/3,630・skip 0。結果は`summary.json`、`focused.xml`、`editmode.xml`。これはEditor component検証であり、IL2CPP／製品scene／DAG commit／現在poseの物理・表示同時切断の合格ではない。

## 拒否した元形状

監査は先行Megacityの`audit_hull`をそのまま再利用する。各面の外側へ出る他頂点の最大距離を調べ、許容は`max(1e-6, 最大軸extent * 1e-6)`。座標系ごとの数値であり、Renderer-localの値をそのままmと読まない。

| family / bone | 元UCX mesh-localの最大外側距離 | 元frameの許容 | Renderer-bind-localの最大外側距離 / 許容 |
| --- | ---: | ---: | ---: |
| Casual / DEF-thigh.L | 5.59796e-5 | 1e-6 | 5.59814e-5 / 1e-6 |
| Casual / DEF-thigh.R | 5.33726e-5 | 1e-6 | 5.33268e-5 / 1e-6 |
| Professional / DEF-upper_arm.L | 4.82394e-5 | 1e-6 | 0.00483176 / 2.80765e-5 |

変換後だけでなく元UCX座標でも逸脱するため、骨pose変換が新たに作った非凸性ではない。ProfessionalのRenderer scaleは0.01で、Renderer-local距離が約100倍になることと区別する。小さい逸脱ではあるが、入力B-repの凸性を前提とする切断と、PhysXが独自に作るcooked hullを同じ形状として扱う根拠にはしない。PhysXによる自動convex化を修復証拠として採用しない。

### 最小修正候補（未適用）

`failure-localization.json`に元face／vertex IDを固定した。各形状とも違反は隣接2面で、極小三角形ではない（最小高度は約0.0136〜0.0469、同frameの32 ULP相当分解能は約1.23e-6〜2.30e-6）。両三角形が共有する対角線を反対側へ張り替える候補を**Pythonリスト上だけ**で検証した。

| bone | 元face ID（0始まり） | 元共有対角線 | 候補対角線 | 候補の最大外側距離 |
| --- | --- | --- | --- | ---: |
| Casual thigh.L | 34, 35 | 8–16 | 22–24 | 4.02759e-8 |
| Casual thigh.R | 52, 53 | 20–24 | 5–8 | 3.68828e-8 |
| Professional upper_arm.L | 45, 46 | 14–20 | 1–16 | 6.74281e-18 |

候補はすべて元の32頂点位置を完全保持し、60面90辺、閉多様体／正体積／凸性の同じ監査を通る。許容1e-6を変えていない。ただしBlender Meshや`.blend`へは書き込まず、private fixtureも元の不合格を保持する。候補のUnity import／cook／切断を検証した結果ではない。代表2体以外の全asset調査でもない。

## 変換と独立照合

1. 固定`.blend`のSHAと、移動後の最新版WorkingのSHAを照合。Blenderは`--disable-autoexec`で開き、save／再convex化／面修復は行わない。
2. UCXの実world matrixと代表pose boneの逆行列からbone-localの形状関係を求める。PHYS_NULLのbone-parent offsetもこの関係に含む。
3. Blender data boneのrest matrixを通じて表示owner localへ写し、明示的なmirror XでRenderer-bind-localの頂点を出力する。元の頂点／面IDは維持し、反転行列に伴う面順反転だけ行う。bound fittingや表示meshの位置weldはしない。
4. Unityの監査済みMesh bindposeで`boneLocal = bindpose * rendererBindPoint`を準備する。現在poseでは`renderer.transform.worldToLocalMatrix * bone.localToWorldMatrix * boneLocal`で、表示入力と同じRenderer-localへ写す。Renderer／祖先scaleの追加焼込みはしない。
5. 独立oracleは**実際にimportされたUCX MeshFilterの現在Transform階層**。そのMesh頂点をWorldへ写した結果と、上記Renderer-local結果をRenderer TransformでWorldへ写した結果を照合する。別boneへ誤接続するnegative controlは全条件で約0.49〜0.58 world unitずれ、正常経路の微小誤差と区別できた。

元表示頂点は既存topology mapを通して全数照合する。初回のbit一致要求は両familyで不成立（`exact-coordinate-probe.xml`）。mirror-X参照との差はCasual最大4.16433e-7、Professional最大3.84268e-5 local unitで、double JSON読取りにしても消えなかった。最終照合はfloat演算の数値許容`8 * float machine epsilon * max(1, 最大頂点magnitude)`とし、変換行列をデータからfitしない。**この座標照合許容と凸性判定許容は別物であり、凸性の許容は変更していない。**

UCX oracleのmesh-local頂点は同じ明示mirror-X参照と上記数値許容内で一意対応を検査する。import時のnormal split由来の複数render vertexは同じ参照頂点を読むが、全32参照頂点への到達を要求する。この照合用の対応検索は入力B-repのtopology作成やgeometry変更には使わない。Worldでの経路比較は別途固定2e-5の上限を適用した。

## pose別の位置差

| pose | Casual max World距離 | Professional max World距離 |
| --- | ---: | ---: |
| 基準 | 4.22521e-7 | 8.40992e-7 |
| 骨回転 | 4.54913e-7 | 8.49889e-7 |
| 祖先scale追加 | 1.01852e-6 | 8.92081e-7 |
| root bone scale追加 | 1.12462e-6 | 9.61096e-7 |

元アセットは1 rig・358 boneで、代表bone metadataとPHYS_NULLのbone親を照合している。実AnimationClip評価ではなく、前工程と同じ明示poseを設定した。負／零scaleや任意のshear構成は未検証。

## cook／切断の範囲

凸性合格形状を各poseのRenderer-localへ写した後、既存`Physics.BakeMesh`でconvex cookを呼び、既存OwnerCutHarnessをJob実行する。各形状のboundsからaxis 1、次いでpositive側のaxis 0に面を置く。これは各convex単体の切断検証で、19形状をまとめたownerや表示meshと共通の一枚の面で切った結果ではない。

入力・各切断後の正負childでcookを呼ぶが、PhysX内部のcooked頂点／面を取得して比較したものではない。初期・childのcook呼出しにエラーが出ないこととB-repのJob切断を分けて記録する。同期／非同期のMain性能、実Rigidbody挙動、material／Anchor／frame寿命、commit・仮Stencil表示は未測定。

## 成果物と次工程

`Tools/Export-Character-Physics.py`は既存fixtureを上書きしない。privateの`Resources/CharacterPhysicsMigration/`に全38形状と`convexAccepted`フラグを置く。publicの`export.json`にはhash・形状数・監査集計だけを保存する。生の頂点座標はGitへ入れない。

Unityの`Compact16uvCharacterPhysicsTests`を`-runTests -testPlatform EditMode -testFilter Compact16uvCharacterPhysicsTests`で実行する。private fixture欠落時はIgnoredであり受入はskip 0。全件回帰ではfilterを外す。証拠はTemp外へ保存し、`Tools/Summarize-Character-Physics.py`でprocess IDを照合・匿名化する。

次の製品登録には、アセット側で上記3 UCXの三角形対角線を修正する最小候補を優先し、変更SHAで新しいintake・fixtureを固定して再監査することを推奨する。生成処理で同種の非凸な対角線を選ばない確認も必要。今回の作業ではupstreamや固定アセットを書き換えていない。製品側で不合格形状を黙って除去、許容を緩和、または再convex化する救済経路も追加していない。
