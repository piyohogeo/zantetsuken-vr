# 固定scaleの読み込み時正規化プローブ

2026-09-24。基点 `a55bbf8`、Unity 6000.3.22f1、Windows Editor EditMode。

## 結論

Professionalの固定0.01を読み込み時に頂点・bindposeへ焼き込み、切断入力のRendererをscale 1にする方式は、今回の2素材では成立した。表示側の剛体限定lineage契約を緩めることなく、Professionalも19形状compoundの切断・commit・再切断を通過した。Casualのscale 1も同じ経路で成立した。

製品LoaderやImporterへの組込みではなく、テスト用の読み込み直後のメモリ上変換である。元アセット、修復版アセット、rig/UCX階層、scene参照先、製品Runtimeは変更していない。固定入力scaleの正規化を先に進める判断材料とし、一般的な動的scale対応完了とは扱わない。

## 方式

`CharacterFixedScalePreparation.Create`で、正の一様scale `s` に限り、Meshを別インスタンスとして用意する。

- 頂点 `p' = s * p`、bindpose `B' = B * Scale(1/s)`、boundsも同じ倍率。
- 正の一様scaleなので元のnormal/tangent、UV、weight、indexはそのまま。
- unit-scaleの共通親の下に別Rendererを置き、元RendererのWorld位置・回転を引き継ぐ。骨配列とrootBoneは元rigを共有する。
- UCX fixtureのRenderer-bind頂点も `s` 倍して新bindposeで評価する。元fixtureのファイルは変更しない。

位置について `boneWorld * (B * Scale(1/s)) * (s * p) = boneWorld * B * p` となる。変換前後で元の骨・UCX階層の全World行列が変わらないことを確認した。

これは**骨を含む全Transformのscaleを1にする処理ではない**。従来rigには単位換算scaleが残るが、切断入力／表示Geometry側のRenderer-local単位を正規化する。骨階層とアニメーションの参照パスを維持できるため、今回はrig全体やAnimationClipを書き換える方式を採らなかった。比較用Rendererは二重描画を避けて無効化している。

頂点・bindpose変換は読み込み準備時の一度であり、このプローブの切断ごとにscale補正やMain側16B再packを追加していない。ただしテストはケースごとにMeshをcloneしており、製品で必要なアセット／scale単位の共有キャッシュや元Rendererの切替・所有権処理は未実装。処理時間・メモリ使用量は未測定。

## 変換同値性

各300姿勢で元版と準備版を `BakeMesh(true)` し、World頂点位置とWorld正規化法線を比較した。骨回転・微小移動、親の移動／回転に加え、rootBoneは100姿勢ずつ基準scale、一様1.05倍、非一様 `(1.05, 0.95, 1.1)` 倍とした。

| family | 固定scale | 最大World位置差 | 最大World法線ベクトル差 |
|---|---:|---:|---:|
| Casual | 1 | 9.000104e-7 | 6.462708e-7 |
| Professional | 0.01 | 1.04605726e-6 | 5.503352e-7 |

許容差はいずれも2e-5。準備版Rendererのscale 1、元Meshの頂点・bindpose不変、UV・normal・tangent・weight・index一致を確認した。**Unity BakeMeshの比較であり、自前Burst skinningの合格判定ではない**。300姿勢は決定的なTransform操作で、実AnimationClip再生ではない。元版との一致は、非一様bone scaleに対する物理的に理想的な法線を独立検証したという意味でもない。

## 19形状compound・実commit

各familyで基準pose 0、骨回転pose 1、骨回転＋rootBone非一様scaleのpose 4を検証した。外側のRenderer親にはscaleを加えない。

2 family × 3 pose × 3メモリ初期状態（-1 / 0x00 / 0xCD）＝18ケース。各ケースで共通平面による全19形状の初回切断と、公開された正側childの再切断を行い、**計36commit**が成立した。3初期状態でログ上の分類・体積・commit結果は完全一致。

| family / pose | 初回 split / +継承 / -継承 → +形状 / -形状 | 再切断 split / +継承 / -継承 → +形状 / -形状 |
|---|---|---|
| Casual / 0 | 19 / 0 / 0 → 19 / 19 | 9 / 3 / 7 → 12 / 16 |
| Casual / 1 | 19 / 0 / 0 → 19 / 19 | 4 / 2 / 13 → 6 / 17 |
| Casual / 4 | 18 / 0 / 1 → 18 / 19 | 8 / 1 / 9 → 9 / 17 |
| Professional / 0 | 3 / 8 / 8 → 11 / 11 | 6 / 2 / 3 → 8 / 9 |
| Professional / 1 | 4 / 8 / 7 → 12 / 11 | 4 / 2 / 6 → 6 / 10 |
| Professional / 4 | 4 / 8 / 7 → 12 / 11 | 4 / 2 / 6 → 6 / 10 |

既存製品Driver、Registry、Cook、DAG、表示commitをテストfixtureから通した。全UCX頂点の変換前後位置、平面pullback、正負半空間、分割／継承、体積・質量和、source退役・child公開、body→logical ID、owner移動／回転への表示追従、元16B payloadとB-repの不変性、guard、未完了予算0、guarded drainを確認した。volumeは形状ごとの和であり、重なったconvexのunion体積ではない。詳細値は `summary.json`。

## 残る拒否と範囲外

読み込み時の正規化後に共通親へ非一様scale `(1.3, 0.8, 1.1)` を加えるpose 2/3は、2 family × 2 pose × 3初期状態＝12ケースで引き続き `F-CHARACTER-LINEAGE-SCALE`。登録時にcut / commit / upload 0で拒否される。これは安全な拒否の確認であり、統合合格12件とは数えない。

正の固定一様scaleの入力に限定する。負scale、shear、blendshapeは今回対象外。動的な外側scaleは引き続き別の設計課題であり、剛体契約を緩めていない。実Rigidbody simulation、Playerの画面比較、XR、IL2CPP、性能、自前Burst skinning、実AnimationClip、生成／インポート時の永続アセット化、製品sceneの参照先切替は未検証／未実装。

## 証跡・次工程

- `focused.xml`: 32/32、skip 0。比較2件、統合18件、期待拒否12件。
- `editmode.xml`: 全3694/3694、skip 0。従来の未正規化入力の拒否テストも維持。
- `summary.json`: 受入範囲、比較誤差、36切断の分類・体積、拒否条件、ソースSHA。
- `Tools/Summarize-Fixed-Scale.py`: PID・件数・解放・初期状態一致を検査し、XML匿名化。修復版intake manifest、元／修復版blend、全fixtureのSHAを前回証跡と照合。

次は準備Meshの共有／寿命とRenderer切替を明示した製品入力境界への組込み、および実ClipとPlayer表示での確認。生成／インポート時に同じ表現を永続化できればロード時変換も省けるが、今回の合格をそのままImporter対応完了とはしない。
