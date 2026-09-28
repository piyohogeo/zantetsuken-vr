# MobPlanCity の複数モデル代表シナリオ（2026-09-29）

push は保留。commit は、製品（P）・計測の check（C）・Editor の診断（D）に分けて入れた（`mobplan-models-2026-09-29.md` の追記6）。単一モデル版（`MobPlanCity.unity`）と、その過去の記録はそのまま残している。

## 1. 49 モデルの構成

**49 の出どころ**
- blender-pipeline-pilot の DESIGN §47.1 で、19 Hull の Gate を通った Casual 24（f_1〜f_11、m_1〜m_13。f_12 はベンダー側の重みの欠陥で除外）と、Professional 25（c_1〜c_25）。
- 上流ファイル：`zantetsuken-blender-pipeline-pilot/Generated/BottomHalfUV/ITHappyCharacterMeshRepair/{Casual,Professional}Characters/1.23.0-convex-remap_palette/Working`

**確認の経路**（既存の検査を、両 pack に使えるよう写して使った。判定は変えていない）

| 段 | 内容 | 使った検査 | 結果 |
|---|---|---|---|
| ① rig | 骨の rest（`matrix_local`）と rig・表示 object の位置を、f_1 と比べる | `rest_compare.py`（MultiNpcSlash の写し） | Casual 24：差 0.0。Professional 25：差 1.40e-6（しきい値 1e-5 未満、違う骨 0。rig の位置も同じ）。ただし表示 object の world 行列は、0.01 倍・X 軸 −90° で f_1 と違う（cm 単位の構成） |
| ② Hull と表示 mesh | 表示 mesh が 1 つであること。19 Hull の厳格監査（辺の flip だけの修復を含む）と、renderer bind 座標での再監査 | `intake_mobplan.py`（`intake_multinpc.py` の写し） | 38 が合格、11 が不合格 |
| ③ 切断入力（topology） | Unity の import mesh と Blender の元 mesh を対応付ける監査。対応表が切断入力に要る。あわせて UV と palette を確認 | `MobPlanModelsIntake.cs`（`MultiNpcIntake.cs` の写し） | 21 が合格、17 が不合格 |
| ④ Pose Table | 骨を駆動できるか。製品の `SandboxNpcCharacter` が、表示に使う骨と Hull の骨を Table が駆動することを、準備のたびに確かめる | 製品の準備時の検査 | 21 モデル、30 枠がすべて準備できた（broken 0） |

- **Pose Table の共有**：使える Pose Table は、f_1 用に作られた 1 組（320 Table、66 骨）だけ。
  - 共有してよい根拠は、①の rest の一致（Table は骨の局所位置も持つので、名前が同じなだけでは足りない）と、④の骨の検査。
  - Professional の rest は f_1 と 1.4e-6 違う。見た目で問題がないことは、画像では確かめていない。
- **今回の取り込み経路では未成立のモデル（当初 28。追記の切り分けの後は 23）**：資産として使えないとは判断していない。黙って別のモデルに置き換えたり、検査を緩めたりもしていない。
  - **表示 mesh が 2 つ（3）**：m_8、m_13、c_12（リュック付き）。今の経路は表示 mesh 1 つを前提にしている。
  - **Hull の厳格監査に不合格（8）**：c_4、c_5、c_6、c_13、c_17、c_20、c_21、c_25（`nonconvex or inward face`）。
    - flip では直らないという表示が付いていないので、renderer bind 座標（表示 object が 0.01 倍）での再監査で落ちたと考えられる。推測で、確かめていない。
  - **topology 監査に不合格（17）**：f_3、f_7、f_10、m_3、m_9、m_10、m_11、m_12、c_3、c_7、c_8、c_11、c_14、c_15、c_18、c_19、c_24。
    - 三角形の順序や署名の不一致、あいまいな三角形、対応しない頂点。c_7 と c_19 は位置のずれも大きい。
    - m_10、m_11、m_12 は、MultiNpcSlash のときにも不合格だった。
- **一覧**：`C:\log\zantetsuken-vr\MobPlanModels\models-49.csv`（状態、理由、頂点数、Hull、修復した Hull、表示・Pose・Hit、切断の数）

**使用可のモデル（21）**
- Casual 14：f_1、f_2、f_4、f_5、f_6、f_8、f_9、f_11、m_1、m_2、m_4、m_5、m_6、m_7
- Professional 7：c_1、c_2、c_9、c_10、c_16、c_22、c_23

## 2. シナリオの構成

**場面**
- `Assets/Licensed/MobPlanModels/MobPlanModelsCity.unity`（Git 外）
- builder：`Assets/Licensed/MobPlanModels/Editor/MobPlanModelsSceneBuild.cs`

**作り方**
- MobPlanCity の、単一モデルの 20 体と template を外し、モデルごとの NPC に置き換えた。
- NPC の作り方は XrSimCity の `MakeNpc` と同じ。
- 計画、Slash、切断、補充、影、床、world、check は MobPlanCity のまま。箱は置いていない。

**枠の構成**（30）
- 使用可の 21 モデルに 1 枠ずつ、固定の順で並べ、その順の先頭 9 モデルに 2 つ目の枠を足した（単一モデル版と同じ、20 体が活動して 10 枠が予備）。
- 固定の順：Casual と Professional を交互に、番号順（f_1、c_1、f_2、c_2、f_4、c_9、f_5、c_10、f_6、c_16、f_8、c_22、f_9、c_23、f_11、m_1、m_2、m_4、m_5、m_6、m_7）。
- 最初の 20 体は、異なる 20 モデル。21 番目の m_7 は、最初の補充で登場する。
- 49 モデル全部を登場させる構成は、今回の取り込み経路で成立したのが 21 モデル（追記の後は 26）のため、作れていない。
- 枠はモデルに固定：同じ枠の renderer、骨、DirectSkin の入力を使い続ける。空き枠がなければ、既存の待機を使う。
- 49 モデル分を事前に準備する構成は、今回の確認のための条件で、製品の常駐方針ではない。

**製品の変更**（最小限）
- `MobPlanCrowd`：枠の共有データ（解析済みの intake と Hull、固定倍率の mesh）を、crowd 全体で 1 つから、モデルごと（`SandboxNpcCharacter.SlotShareKey`）に分けた。
  - 以前は 1 つを全枠で共有しており、1 モデルを前提にしていた。
- `SandboxNpcCharacter`：
  - `SlotShareKey` と `Family` を足した（観測用）。
  - 別のモデルの共有データを受け取った枠は、失敗として止まるようにした（防護）。

**計測（check）**
- `SandboxPropSlashPlayerCheck.MobPlanModels.cs`（新規）：モデルごとの表示・Pose・Hit 候補、個体の数、切断（受付・公開・Commit、再利用した枠の個体、子）、DirectSkin、枠の失敗を集計する。
  - 出力：`mobplan-models.csv`、`mobplan-model-events.csv`（終了時に 1 回だけ書く）。
- 判定を 1 件足した：個体を持ったモデルは、表示され、活性化の後に Pose が当たり、Hit 候補になった。

## 3. 機能の結果

| 起動 | Player | 内容 | 結果 |
|---|---|---|---|
| f2 | m2 | script-v3-165 | exit 0。21 モデルすべてが、表示・Pose・Hit 候補になった。実 Slash から受付・公開・Commit まで進んだのは 20 モデル（m_7 は 1 回登場したが切られていない） |
| cm7 | m3 | 確認用：m_7 を順の先頭に置いた builder の引数。script-v3-60 | m_7 の切断 2 件が Commit（再利用した枠の個体 1 件を含む）。子の切断 2 件も Commit |
| f1 | m1 | 最初の起動 | 失敗（exit 15）。check の対象 NPC が、名前順の先頭にある予備枠（休眠中）になった。builder の枠の名前を直して解消（f2 以降） |

- f2 では、根の切断の Commit が 20 モデル、子の再切断の Commit が 19 モデル、再利用した枠の個体の切断の Commit が 16 モデルだった。
- f2、cm7 と計測の起動 q1・q2（同じシナリオ）を合わせると、使用可の 21 モデルすべてで、次の 3 つを確認した。
  - 実 Slash から受付・公開・Commit まで
  - 子の再切断の Commit
  - 再利用した枠の個体の切断の Commit
- f2 の詳細：
  - 開始後の DirectSkin 入力の作成は 0。枠の追加の生成も 0。空き枠の待ちも 0。
  - 受付はすべて Published（Pending／Held は起きなかった）。
  - 通常の終了で、すべて返した。scenario の判定に失敗はない。

## 4. 費用（計測）

**条件**
- 撮影なし、v207（72 Hz）、目 1.6 m・水平、詳細ログなし、`script-v3-100`（直進の前まで、100 s）、寿命管理のしきい値 64（確認用の値）。
- 起動：複数モデル版 m4 を 2 回（q1、q2）、単一モデル版 d28（同じコード）を 1 回（s1）。
- 合計 6 分 38 秒（準備と終了を含む）。

**結果**（Main、ms。中央値／p90／最大）

| | q1（複数モデル） | q2（複数モデル） | s1（単一モデル） |
|---|---|---|---|
| 初期準備（枠 30） | 4.29 s | 4.05 s | 0.85 s |
| 準備の割り当て（Unity）／Mono heap | +54.3／+71.4 MB | +53.9／+71.7 MB | +56.7／+67.9 MB |
| 補充 Frame | 4.67／8.13／14.23（35） | 5.33／6.65／9.17（40） | 5.14／6.43／11.62（32） |
| 　起動そのもの（`Npc.Activate`） | 0.68／1.29／10.59 | 0.73／1.19／10.10 | 0.89／1.30／1.86 |
| 再準備 Frame | 6.54／10.75／17.85（38） | 7.20／11.35／30.49（44） | 7.86／10.17／12.18（34） |
| 　再準備そのもの | 1.43／2.26／4.74 | 1.45／1.86／2.27 | 1.58／2.37／2.55 |
| どちらもない Frame | 3.96／5.68／23.18 | 3.94／5.91／26.80 | 3.86／5.31／15.29 |
| 11.1 ms 超の補充・再準備 Frame | 6 | 7 | 3 |
| 開始後の DirectSkin 作成／枠の追加生成／空き枠待ち | 0／0／0 | 0／0／0 | 0／0／0 |
| 移動距離 | 28.7 m | 30.0 m | 29.2 m |

**モデルによる違い**
- **再準備**：モデルごとの中央値は 1.21〜1.84 ms で、頂点数（3,356〜7,968）との相関は −0.06。
- **起動**：大きかったのは、どちらも m_7 の初登場の 1 回（q1 10.59 ms、q2 10.10 ms）。
  - m_7 は、補充で初めて登場する唯一のモデル。ほかのモデルの初登場は最初の 20 体で、計測の範囲より前に起きる。
  - 初登場の起動の中央値は 0.80 ms、2 回目以降は 0.72 ms。単一モデル版の予備枠の初起動は、最大 1.86 ms。
  - モデルの初登場の 1 回だけの費用（例えば mesh の初回の表示の準備）の可能性があるが、中身は確かめていない。
- **初期準備**：複数モデル版は約 5 倍かかった。
  - 候補：各モデルの最初の枠が、`intake.json`（2.1 MB、21 モデル分の対応表を含む）を丸ごと解析する（21 回）。
  - この準備は計測の範囲より前なので、marker では確かめていない。

**11.1 ms を超えた補充・再準備 Frame の内訳**（計 16）
- 単独で除いたら 11.1 ms 以下になる数：物理の step 13、補充・再準備そのもの 10、check 6、GC 5（前の単位と同じく、寄与の目安）。
- 複数モデル版だけに出たのは、m_7 の初起動の 2 件。

**比較の扱い**
- 単一モデル版との違いを、モデルの数だけによる厳密な性能差とは断定しない。起動ごとの経路や、補充の順が違うため。
- 72 Hz での Main 時間であり、90 fps で間に合うことの証明ではない。

## 残件・判断待ち

- **今回の取り込み経路で成立したのは 49 モデル中 21（追記の後は 26）**。21 モデル版の結果は中間の成果で、49 モデル版の完了とはしない。
  - 残り 28 を使うには、表示 mesh 2 つへの対応、Professional の Hull の再監査（座標の倍率）、topology の対応表の修正（上流の mesh か、監査のどちらか）が要る。いずれも今回は行っていない。
- m_7 の初起動（約 10 ms）の中身は未調査。
- 初期準備時間（約 4 s）の原因は、確かめていない。
- Professional の見た目（rest の 1.4e-6 の差、表示 object の倍率）は、画像で確かめていない。
- 最適化（モデルをまたぐ cache、動的ロード、枠のモデルの差し替え、計画の変更）には進んでいない。

## 証拠

- `C:\log\zantetsuken-vr\MobPlanModels\`
  - 調査と取込み：`survey`、`intake`、`models-49.csv`
  - 起動：`f1`、`f2`、`cm7`、`q1`、`q2`、`s1`
  - build：`build-m1`〜`build-m4`
  - コードの状態：`code-state-m1`、`code-state-m4`
  - 道具：`tools`
- 単一モデル版の Player d28：`C:\log\zantetsuken-vr\MobPlanSlash\build-d28`

## 追記（2026-09-29、TL 指示）：Hull 不合格 8 件の切り分けと見た目の確認

### 1. Hull 不合格 8 件の原因

**監査の条件の比較**

| 経路 | 監査する座標 | 単位 | 許容値 |
|---|---|---|---|
| 上流の厳格 Gate（`audit_convex_boundary.py`） | UCX object の局所座標 | m | `max(1e-6, 広がり×1e-6)` |
| Compact16uv の経路（`Export-Character-Physics.py`）と、今回の取り込み（`intake_mobplan.py`） | renderer bind（表示 object の局所座標） | その renderer の単位（Professional は cm） | 同じ式 |
| 製品 | Hull の頂点を `VpFixedScaleSkinInput.PrepareBindPoint`（renderer の倍率を掛ける）で m にしてから、骨の bind で骨の局所座標にする | m | 凸性を再び監査することはない |

- 変換の式（mirror @ owner⁻¹ @ rig @ bone rest @ bone 局所）は、Compact16uv の経路と同じ。
- Professional の表示 object は、0.01 倍・X 軸 −90°（cm 単位の構成）。

**失敗 1 件（c_4）と成功 1 件（c_1）の確認**
- 道具：`C:\log\zantetsuken-vr\MobPlanModels\tools\hull_frame_probe.py`（読み取りのみ）。
- c_4 の不合格は `DEF-forearm.L`（面 17）と `DEF-forearm.R`（面 13。flip 1 回の後）。

| 座標 | forearm.L の外側への距離／許容値 | 判定 |
|---|---|---|
| UCX 局所（m） | 3.59e-7 m ／ 1e-6 m（比 0.36） | 合格 |
| renderer bind（cm、float32） | 3.56e-5 cm ／ 2.76e-5 cm（比 1.29） | 不合格 |
| renderer bind（cm、float64） | 3.59e-5 cm ／ 2.76e-5 cm（比 1.30） | 不合格 |
| 同じ座標を m に戻したもの | 3.59e-7 m ／ 1e-6 m（比 0.36） | 合格 |

- 物理的な凹みは同じ 0.36 µm。許容値の下限 1e-6 は単位によらない絶対値なので、cm の座標では広がり×1e-6（0.28 µm 相当）が効き、m より約 3.6 倍厳しくなる。
- float32 と float64 で結果は同じなので、精度の問題ではない。変換の順序、回転、面の向きも原因ではない。
- c_1 は、同じ座標でも外側への距離が 1e-15 程度で、どの座標でも合格。
- 製品は Hull を m に直してから使い、凸性を再び監査しない。したがって cm の座標での監査は、製品が使う座標と違う座標での判定になっていた（取り込みの問題）。

**8 件への拡大**（`hull-probe/eight.json`）
- c_4、c_5、c_6、c_13、c_17、c_20、c_25 の 7 件は同じ原因。cm でだけ不合格で、m では合格。外側への距離は 0.13〜0.65 µm。
  - c_5、c_6、c_13、c_17 は、float32 と float64 で判定が分かれた。許容値の境目にある判定。
- c_21 は別の原因。`DEF-foot.R` が UCX 局所でも m でも不合格（外側への距離 0.182 m、面 10）で、flip による修復もできない（「no convexifying flip」）。
  - 上流 DESIGN（2422 行、2642 行）は、これを c_21 の足 Hull の、面積 約 1.06e-7 m² の非常に細い三角形の面平面に由来する値で、実際の凹みの深さではないと記録している。

**修正**（取り込みの道具のみ。`intake_mobplan.py` v2）
- 変換後の監査を、製品が凸形状を作る座標（renderer bind × 表示 object の均一な倍率 = m）で行う。
- 監査の関数、許容値、fixture に保存する renderer bind の頂点は変えていない。
- Casual（倍率 1）には影響しない。m の許容値は cm の許容値より厳しくならないので、既に合格した 38 件は変わらない。
- **結果**：
  - 7 件が Hull の段に合格した。
  - Unity の topology 監査で、c_4、c_6、c_17、c_20、c_25 の 5 件が合格した。c_5 と c_13 は topology 対応で未成立（別の理由）。
  - c_21 は、細い三角形の面を理由に未成立（修復はしていない）。
- **成立したモデル**：21 → 26（Casual 14、Professional 12）。一覧は `models-49.csv` を更新した（旧版は `models-49.v1.csv`）。

**今回の取り込み経路で未成立の 23 件**（理由は分けて残す）
- 表示 mesh が 2 つ（3）：m_8、m_13、c_12
- topology の対応（19）：f_3、f_7、f_10、m_3、m_9、m_10、m_11、m_12、c_3、c_5、c_7、c_8、c_11、c_13、c_14、c_15、c_18、c_19、c_24
- Hull の細い三角形の面（1）：c_21

### 2. Professional の見た目

**大きさ**（Player の計測。各モデルが最初に描画されたとき）
- Professional 12 モデルの描画の高さは 1.89〜2.05 m（歩行の姿勢の範囲）。自分の表示 mesh の bind の高さは 1.84〜1.95 m（表示 object の倍率 0.01 を含む）。
- Casual は 1.93〜2.04 m ／ 1.85〜1.96 m で、同じ範囲。倍率の二重適用や未適用（100 倍の差）はない。

**向きと歩行の姿勢**（Editor での並べた描画。`MobPlanModelsLook.cs`、`look-1\`）
- 同じ取り込み済みモデルを、取り込んだままの表示と、製品が使う歩行の Pose Table（f_1 用、`Inertial_AS_Fast_WalkCycle_1`）を骨に当てたものとで、正面と横から並べた。
- Professional 12 モデルと参照の f_1 で、大きさ（約 1.85 m）、向き、歩行の姿勢が同じようにそろっている。崩れや反転はない。
- Table の 66 本のうち、Professional は 65 本が結び付く。結び付かないのは `f_1`（f_1 の表示 object の transform のチャネル）。
  - f_1 以外のモデルでは、表示 object の transform を Table が動かさず、そのモデル自身の取り込みの値（Professional は 0.01 倍・回転 0）のまま。これは Casual の他モデルも同じ。
- 取り込んだ blend には、モデル自身の歩行（Walk）の clip がない（`CanonicalSource` だけ）。モデル自身の歩行との比較はしていない（Walk を持つのは pipeline の Canonical blend）。

**Player での拡大画像**（`v5\run\closeups\`、check の診断用カメラ。`-zantetsuModelCloseups` のときだけ）
- 歩行：c_1、c_2、c_9、c_10、c_16、c_17、c_20、c_22、c_23、c_25。大きさ・向き・歩行の姿勢は自然。
  - c_20 は、スカートの裾の下の太ももに、明るい斑が見える。原因は確かめていない（歩行の姿勢でスカートと脚が交差している可能性）。
- 実 Slash の後（2 Frame 後と 60 Frame 後）：c_16、c_17、c_20、c_22、c_23。切断面で分かれた片が、切断の位置から倒れ、地面にある。
- 診断用カメラについて：
  - 表示が描くのは、その Frame の収集が確定した後（`IsFrameOpen`）だけなので、描画は `Application.onBeforeRender` で行う。
  - check の LateUpdate で描くと、片は描かれない（v2〜v4 で確認）。

### 3. 機能（26 モデル）

- f3（m6、既定の順、script-v3-165）：exit 0。
  - 26 モデル・30 枠を準備（4.66 s、broken 0）。開始後の DirectSkin 作成 0。空き枠の待ち 0。
  - 個体を持ったのは 21 モデルで、そのうち 20 モデルを Commit まで確認した。
- 新しく成立した 5 モデル（c_4、c_6、c_17、c_20、c_25）：f3 と v3〜v5 を合わせて、全モデルで次を確認した。
  - 表示・Pose・Hit 候補
  - 根の切断の Commit
  - 子の再切断の Commit
  - 再利用した枠の個体の切断の Commit
- **気づいた点：枠の再利用の順**
  - 順の後ろのモデル（m_5、m_6、m_7、c_23、c_25）が、f3 では一度も登場しなかった。
  - 原因：`MobPlanSlotPool.Advance` が、準備中の枠がなくなるたびに（再準備の後も）空き枠を枠の順に並べ直す。そのため、返った前の方の枠が先に使われる。
  - この関数の説明は「最初の準備がすべて終わったとき」に並べるとしており、コードと違う。
  - 製品の変更になるので、今回は直していない。49 モデルすべてを補充で登場させるには、この順の扱いの判断が要る。

## 追記2（2026-09-29、TL 指示）：対応モデルの追加と、補充で後ろのモデルまで登場させる

成立したモデル：**26 → 39**（Casual 21、Professional 18）。未成立 10。一覧は `C:\log\zantetsuken-vr\MobPlanModels\models-49.csv`（前の版は `models-49.v2.csv`）。

### 1. topology 対応の 19 件：原因別の切り分け

**調べ方**（読み取りのみの道具。`C:\log\zantetsuken-vr\MobPlanModels\tools\`、Unity 側は `Assets/Licensed/MobPlanModels/Editor/MobPlanModelsTopologyDiag.cs` ほか）
- Unity の三角形ごとに、Blender 元の三角形を**位置だけで**探した（元の座標の X を反転。46 モデルとも監査の当てはめ変換は同じ）。
  - 19 件すべてで、Unity の全三角形に位置の一致する元の三角形がある。形が失われた三角形はない。
- そのうえで、監査の指紋（UV の色マス＋辺²/外接箱対角²を 1e-5 で丸めたもの）と、監査が実際に作った対応を、位置と突き合わせた。
- 取り込みの位置の雑音（Unity の頂点と、元の頂点の、対応する組の距離の最大）は、26 モデルで外接箱対角の 1.6〜2.2e-7 倍（Casual、Professional とも）。

**原因と件数**

| 原因 | 何が起きていたか | モデル |
|---|---|---|
| A. 指紋の丸めの境界 | 辺の鍵がちょうど x.5 にあり、Unity 側と元側で別の整数に丸まる（例：153.499985 と 153.500076）。形は一致しているのに、候補の束に入らない | f_3、f_7、f_10、m_10、c_8、c_18（と c_13、m_3 の一部） |
| C. 小さい三角形での誤対応 | 数 mm の三角形では、辺²を最長辺²で割った比の差 1e-4 という判定が、座標の雑音より細かい。本当の元の三角形が判定で落ち、ほぼ合同な別の部品（ボタンなどの繰り返し部品）がただ 1 つの候補として採られた。数本の誤対応で位置の当てはめ（最小二乗）が崩れ、位置での補完が全く効かなくなり、千個前後の頂点が未対応になっていた | c_7（誤対応 3 頂点）、c_11（12）、c_19（7） |
| D. Unity の頂点溶接 | 取り込み設定 `weldVertices: 1` が、別の島にある同じ位置・同じ属性の元頂点を 1 つの Unity 頂点にまとめる。1 つの Unity 頂点が 2 つの元頂点を表すので、対応表（Unity 頂点 → 元頂点 1 つ）で表せない | m_3、m_12、c_19 |
| E. 並べ方の曖昧さ | 島どうしが接する同位置の頂点で、三角形の置き方（角の割り当て）が 2 通り残る。位置合わせの後なら位置で決まる | c_5 |
| B. 完全に重なる複製の島 | 頭部の鼻・口の大きさの島が、位置・UV・weight・法線・面の向きまで同一で 2 組ある。ベンダーの原本（Canonical）にすでにある | m_9、m_11、c_3、c_13、c_14、c_15、c_24（結合後の m_13、c_12 も同じ） |

- 上流の MeshRepair は、頂点だけでつながる箇所を「同位置の別頂点へ分離」する（上流 DESIGN 25 章）。D は、Unity の溶接がそれを元に戻していたもの。

**直したこと**（取り込みの道具だけ。製品は変えていない）
- 監査（`MobPlanModelsIntake.cs`。ベンチマークから写した部分）の 3 点：
  - A：Unity の三角形は、辺の鍵が雑音の範囲で取りうる隣の鍵も引く。
  - C：候補の辺は、比ではなく長さで比べる。許容は雑音の上限（外接箱対角 × 1e-6。実測の約 4.5 倍）の 2 倍。
  - E：位置の当てはめの後は、候補の各角が元の頂点の位置（同じ許容）にあることも求める。
- 合格の条件は変えていない：三角形ごとに元がただ 1 つ、衝突なし、未対応の頂点なし、同じ元頂点を表す Unity 頂点がすべて同じ位置。
- D：m_3、m_12、c_19 の 3 件だけ、取り込み設定を `weldVertices: 0` にした。増えた頂点は 64、2、1 個。同じ元頂点の角の溶接は Unity が引き続き行う。
- **確認**
  - 以前に成立した 26 件は、対応表、位置・法線・UV・添字・weight の hash がすべて同じ。
  - 新しく成立した 12 件（A 6、C 2、D 3、E 1）は、別の道具で全 Unity 頂点を位置で照合した。対応先の位置の違い 0。元の三角形と Unity の三角形は 1 対 1。

**残り（B）について**
- 2 つの島は、どの属性も同じ。どちらの島にどちらを対応させても、描画も切断も同じになる。
- ただし「どちらでもよい」ものを選ぶのは、曖昧な対応を結ぶことになる。今回は結んでいない（TL 判断待ち）。
- 取れる道（案）：
  1. 完全に同一だと確かめた島どうしに限り、決まった規則で対応させる（取り込みの監査の変更）。
  2. 取り込み時に複製の片方を除く（資産の修理）。
  3. 未成立のままにする。
- 溶接を切っても B は解けない（m_9、c_13 で確認）。

### 2. 表示 mesh が 2 つの 3 件：取り込み時に 1 つの skinned mesh へ

- 道具：`tools/merge_multimesh.py`（上流の blend は読むだけ。結合した blend を新しく保存する）、`intake_mobplan.py` v3（結合した blend を元として監査・複写し、上流の hash も残す）。
- **結合の中身**
  - 本体（id の名前の object）を持ち主として残し、他の表示 mesh を Blender の join で入れる。Armature、Subdivision、material 枠、object の変換は本体のもの。
  - 頂点・角の法線・UV（層の名前で）・weight（group の名前で）・material は、join が本体の object 空間へ運ぶ。溶接はしない。
  - c_12 の背負いかばんは、本体と座標系が違う（本体は 0.01 倍・X −90°の cm、かばんは等倍の m）。join がかばんを本体の cm の空間へ移すので、倍率は本体の変換で 1 回だけかかる。
  - c_12 のかばんだけにある「Auto Smooth」node modifier は、join の前にかばんへ適用した（見た目の陰影を data として運ぶため）。
  - Subdivision は viewport 0（Unity の取り込みが書き出す段）、render 2 で、他の全モデルと同じ。
- **Blender での照合**（rig を rest にし、全角の world 位置・法線・UV マス・weight を多重集合で照合）：3 件とも一致。位置の違いは最大 4.8e-7 m（c_12 の座標系の移し替え）、法線は最大 0.04°。
- **Hull**：上流の `_2meshes_`（結合版用）19 個をそのまま使う。
  - hull の監査は 3 件とも合格。flip で直した hull はそれぞれ 1、5、1 個で、他のモデルと同じ程度。
  - 表示の頂点に対する hull の覆い方（hull の中にある割合）：
    - かばんの頂点：73〜85%。hull の外への距離は最大 1.2〜1.6 cm。
    - 単一 mesh の f_1：80%、最大 1.55 cm。
  - c_12 のかばんの数値は、同じかばんの m_13 と一致した（中央値 −0.0148 m、最大 0.0121 m）。cm の座標系の移し替えが正しく、倍率の二重適用がないことの確認。
- **Unity での前後比較**（`MobPlanModelsMergeCheck.cs`）
  - 結合前の上流 blend を同じ設定で取り込み（`probe-unmerged/`）、結合版と並べて比べた。
  - 比べた姿勢：取り込んだままと、歩行の Pose Table の 0、1/4、1/2、3/4。
  - 3 件とも、world 頂点が両方向ですべて一致した（UV マス同一、法線 1°以内）。距離は最大 6.1e-7 m。
  - 描画（同じ material、正面と横）：m_8、m_13 は差 0 画素。c_12 は 1 画素（30/255）。
  - この道具は最初、BakeMesh の結果を world へ移す式を誤っていた（renderer の倍率 0.01 が抜けた）。直した後の数値が上のもの。
- **結果**
  - m_8：成立。
  - m_13、c_12：結合自体は確認済みだが、本体に B の複製の島があるため、topology 対応で未成立。

### 3. 枠の再利用の順（製品の変更）

- `MobPlanSlotPool.Advance`：枠の順に並べるのは最初の準備がすべて終わったときの 1 回だけにした。その後に再準備された枠は、空き枠の最後に加わる。
- 返す条件、再準備の回数の上限、空き枠がないときの待ちは変えていない。重み付けはしていない。
- 集中試験 `ASlotPreparedAgain_JoinsTheEndOfTheFreeOnes_SoTheSlotsBehindAreTakenFirst`（EditMode、5 枠）
  - 前の枠が繰り返し返っても、待っていた後ろの枠が先に使われることを確かめる。
  - 取られる順が s2、s3、s4、s0、s2、s3 になる。
  - `MobPlanSlotPoolTests` 5/5 合格。

### 4. c_21 と c_20

**c_21（DEF-foot.R）**：形の凹みではない。面の組の中の針状の三角形が原因。
- 独立の計算（float64。頂点の 3 つ組から凸包の面を総当たりで求めた）：
  - 32 頂点すべてが凸包の境界上にある。1 µm より深い凹みはない。
  - 60 面すべてが凸包の面の平面上にある。
- 監査が 0.182 m を出す面 10（頂点 15、19、22。面積 1.055e-7 m²、上流の記録と一致）：
  - 頂点 15 は、長さ 25.6 cm の辺 19–22 から 0.82 µm しか離れていない（3 点がほぼ一直線）。
  - その 0.82 µm のずれで巻き順の法線が内向きになり、hull の奥行き全体が「外側」と計算される。
- 製品の凸形状の切断は、保存された面から面の平面を作る。この面はそのままでは使えない。
- 取れる道（案）：上流での hull の作り直し、または面の張り直し。どちらも資産の修理になるため TL の判断を待つ。許容値は変えていない。

**c_20 の太ももの明るい斑**：取り込み由来ではない（ベンダーの原本の塗り分け）。
- 取り込んだままの姿勢（歩行なし）の Editor 描画でも出る。歩行の交差ではない。
- ベンダーの原本（Canonical、元の texture）でも、同じ位置の 24 面が灰色がかった色（#b9aba6）。取り込み版ではパレットで #c8b2aa。
  - 原本の 24 面は、すべて取り込み版の同色の面と頂点位置が一致した。
  - 取り込み版の同色の 51 面のうち 49 面は、原本の同色の面の頂点上にある。残る 2 面は調べていない。
  - 両者の外接箱は一致した。

### 5. 実行での確認（Player、XR Simulator v207、script-v3-165、目の高さ 1.6 m・水平）

**f4（m11、既定の順、39 枠）**：exit 0。
- 39 モデル・39 枠を準備した（8 秒前後。この回 7.60 s、+67.8 MB、broken 0）。
- 39 モデルすべてが個体を持ち、描画・Pose・Hit 候補になった。順の最後の m_8、m_10、m_12 も登場した（補充による）。
- 根の切断の Commit：37 モデル（m_6、c_25 は当たらなかった）。
- 新しく成立した 13 モデルは、すべて根の切断の Commit まで確認した。

**f5（m12、m_8・m_3・m_12 を先頭に置いた確認用の build。casual-m_8 の拡大画像つき）**：exit 18。
- exit 18 は、check の「VRS の再生が準備完了より前に始まった（比較できない回）」という分類。失敗は 0。
  - 準備が 8.7 s に延び、準備完了が 15.9 s になって、VRS の開始（20 s 遅延）に近づいたため。
  - 計測の回ではないので、機能の確認には使う。39 枠で計測するときは、VRS の開始遅延の見直しが要る。
- m_8：根の切断の Commit 2（再利用した枠の個体 1 を含む）、子の再切断の Commit 2。
  - 拡大画像（`f5\run\closeups\`）：1 回目は首と肩の高さの切断。60 Frame 後の頭側の片は、かばんの上部と肩ひもを付けたまま地面にある。本体とかばんを横切る切断が Commit された。
  - 子の再切断がどの片に当たったかは調べていない。
- m_3、m_12（f4 では子の再切断なし）：子の再切断の Commit が 7、6。

### 6. 未成立の 10 件

- 完全に重なる複製の島（9）：m_9、m_11、m_13、c_3、c_12、c_13、c_14、c_15、c_24（TL 判断待ち）
- Hull の針状の面（1）：c_21（上流の hull の面の組。TL 判断待ち）

### 7. 差分の分け方

- **製品（Git）**
  - `MobPlanSlotPool.cs`（再利用の順）
  - `MobPlanSlotPoolTests.cs`（集中試験）
  - 前の単位からの未 commit の `MobPlanCrowd.cs`、`SandboxNpcCharacter.cs`（モデルごとの共有）
- **check（Git）**：この単位では変えていない（前の単位の未 commit 分のみ）。
- **取り込みの道具（Git 外）**
  - `Assets/Licensed/MobPlanModels/Editor/`：`MobPlanModelsIntake.cs`（監査の 3 点、監査のみの出力先 `-mobPlanModelsAuditOut`）、`MobPlanModelsTopologyDiag.cs`、`MobPlanModelsAuditTrace.cs`、`MobPlanModelsMergeCheck.cs`
  - `C:\log\zantetsuken-vr\MobPlanModels\tools\`：`merge_multimesh.py`、`intake_mobplan.py` v3、`topology_traits.py`、`object_state.py`、`duplicate_layers.py`、`duplicate_islands.py`、`multi_mesh_probe.py`、`hull_cover.py`、`ucx_dump.py`、`hull_shape.py`、`region_colours.py`、`grey_faces.py`
- **資産（Git 外）**
  - 結合した 3 つの blend
  - m_3、m_12、c_19 の取り込み設定（weld 0）
  - `topology/blender-source-3.json`
  - `intake.json`（39 件）
  - `probe-unmerged/`（前後比較用）
  - 前の `intake.json` などは `C:\log\zantetsuken-vr\MobPlanModels\intake\v2-26\` に移した。

## 追記3（2026-09-29、TL 指示）：明示 ID による対応表（重複島の 9 モデル）

成立したモデル：**39 → 48**。未成立は c_21 の 1 件（Hull の針状の面。この経路では直らない）。一覧は `models-49.csv`（前の版は `models-49.v3.csv`）。

### 1. ID の渡し方
- 道具：`tools/add_topology_id.py`（Blender）。
  - 対象は、切断入力の正本と同じ mesh（`characters/` の blend。結合・MeshRepair・三角形化の済んだもの）。
  - UV 層 `ztk_topology_id` を最後に 1 つ加える。u = Blender の頂点番号（blender-source の topology 頂点 ID）、v = その頂点の島（連結成分）の番号。
  - 完全に重なる 2 つの島は、頂点番号も島番号も別になる。
- 他は変えない：頂点・面とその順、他の UV 層、描画に使う UV（map1）、法線、weight、modifier。
- 書いた後に読み直して確かめた：全角の ID が正しい。頂点位置・面・map1 は元と同じ。
- Unity では、UV の最後の channel に Float32×2 の整数のまま届いた。
  - map1 と UVMap を持つモデルは channel 2、map1 だけのモデルは channel 1。
  - 取り込み設定の mesh 圧縮は 0、lightmap UV の生成は 0。製品が読むのは UV0 だけ。

### 2. 並べ替え・分割・溶接を通して正しく読めるか（48 モデルすべて、`MobPlanModelsIdProbe.cs`）
- **ID で作った対応表**：48 件すべて、監査の受け入れ条件を満たした。
  - 各頂点が元の頂点の位置にある（誤差は最大 4.6e-5 cm 相当。許容は外接箱対角 × 2e-6）。
  - 島番号が一貫している。三角形が元と 1 対 1。角の UV マスが一致。未使用の元頂点がない。
- **既に成立した 39 件**：
  - ID の対応表は、監査で作った対応表と完全に一致した（元 ID・位置・UV0 の多重集合）。
  - 表示の属性（位置・法線・UV0・weight）と頂点数も変わらない。
- **重複島の 9 件**：Unity の頂点が 42〜110 個増えた。溶接が 2 つの島の頂点を 1 つにまとめなくなったため。
  - 前の取り込みの頂点は、すべてそのまま残っている。
  - 増えた頂点のうち、前の取り込みにない属性を持つのは m_9 14、m_11 14、c_14 4、c_24 2 個。
    - 違いは法線だけ（最大 1.36°）。UV0 と weight は同じ。
    - 4 件とも、Blender のその頂点自身の角の法線と一致した（最大 0.022°）。
  - 前の取り込みでは、溶接が 2 つの島の頂点を 1 つにし、もう一方の島の法線を使っていた。ID の取り込みの方が元に忠実。
  - 2 つの島は「位置・UV・weight・法線まで同一」と前の追記に書いたが、これは誤り（丸めた比較のため）。法線はわずかに違う。

### 3. 取り込みの変更（Git 外、`MobPlanModelsIntake.cs`）
- ID の経路を使う条件：元の記録（blender-source の mesh 属性）に `ztk_topology_id` があること。
  - 読むのは最後の UV channel。channel の数が元の UV 層の数と一致することも求める。
  - 対応表は ID そのもの。上の受け入れ条件で検査する（位置の照合は検査として残す）。
- 使う topology の記録は、ファイル名に加えて blend の hash でも選ぶ（ID を付けた blend は `topology/blender-source-4.json` で読み直した）。
- 9 モデルの `characters/` の blend を ID 版に置き換えた（.meta と GUID はそのまま）。前の blend は `C:\log\zantetsuken-vr\MobPlanModels\intake\characters-before-id\`。
- 既に成立した 39 件は、従来の経路のまま。intake.json の対応表と hash は 39 件とも同じ。
- 「どちらでも同じ」として結ぶ案と、片方を除く案は採っていない。

### 4. 見た目・歩行・切断
- **歩行**：前の取り込みと ID の取り込みを比べた（取り込んだままと、歩行の Pose Table 0、1/4、1/2、3/4）。
  - 9 件とも、前の取り込みの全頂点が ID の取り込みにある（world で最大 7.2e-7 m）。
  - ID の取り込みの頂点は、m_9、m_11 の各 2 個（上の法線の違い）を除いて、すべて前の取り込みにある。
  - 描画（正面・横）は全 9 件で差 8/255 を超える画素 0。
- **f6**（m13、既定の順、48 枠。exit 18＝VRS の再生が準備完了より前に始まった、比較できない回。失敗 0）
  - 準備 11.7 s、+77.8 MB、broken 0。
  - 48 モデルすべてが個体を持ち、描画・Pose・Hit 候補になった。
  - 根の切断の Commit は 41 モデル。9 件のうち m_9、m_11 はこの回では当たらなかった。
- **f7**（m14、9 件を先頭。exit 0、準備 10.7 s）
  - 9 件すべてで、根の切断の Commit（1〜2 回。再利用した枠の個体を含む）と、子の再切断の Commit（2〜22 回）。
  - 拡大画像（`f7\run\closeups\`）：m_9 の歩行中の顔（鼻の島を含む）は自然。m_11 の切断後の画像は人が多く、m_11 の片を特定できない。切断は Commit の数で確認している。

### 5. 残り
- c_21（Hull の針状の面）：別に扱う（上流での面の張り直し）。
- 48 枠の準備（約 11 s）が、VRS の開始（20 s 遅延）に近い。計測の前に開始の遅延を見直す必要がある。
- 実行時の mesh から ID の channel を外す処理は入れていない（製品は UV0 だけを読む）。
- 道具（Git 外）：`tools/add_topology_id.py`、`Editor/MobPlanModelsIdProbe.cs`、`MobPlanModelsMergeCheck.cs`（比較先フォルダの引数）。
- 確認用の取り込み：`probe-id/`、`probe-noid/`。

## 追記4（2026-09-29、TL 指示）：48 モデルを明示 ID の経路へ統一、実行用 mesh、同期の条件

成立の範囲は 48／49 モデル。c_21 は Hull 修復の別件として扱う。

### 1. 48 モデルを ID の経路に統一
- 残りの 39 件の blend にも、`tools/add_topology_id.py` で ID を付けた。前の blend から新しく作り、`characters/` を置き換えている（.meta と GUID はそのまま）。
  - 前の 48 件は `C:\log\zantetsuken-vr\MobPlanModels\intake\characters-before-id\` にある。
  - topology の記録は `topology/blender-source-5.json`（48 件）。これで置き換えた `-4` は `intake\topology-superseded\` へ移した。
- 取り込み（`MobPlanModelsIntake.cs`）：
  - 全モデルに ID を求める。ID の層がない場合、または ID の検査に合格しない場合は、推測の経路へ戻さない。取り込みエラーとして、理由（不合格の検査の名前と件数）を `refused.json` に書く。
  - 形から推測する監査（`Audit`）は、コードとして残してあるだけで、使っていない。
- **統一前後の照合**：48 件すべてで、intake.json の次の項目が統一前（`intake\v4-48\`）と同じ。
  - 対応表
  - 頂点数、submesh 数、骨の数
  - 位置・法線・UV0・添字と topology・weight と bind pose の hash
- **ID の欠落時の確認**：f_1 の blend を一時的に ID なしの版へ戻し、監査だけの出力先で実行した。
  - 結果は「casual-f_1: the blend has no topology id layer (ztk_topology_id)」で拒否された。推測の経路へは戻らない。
  - その後 ID 版へ戻し、hash の一致を確かめた。

### 2. ID の channel は、実行用 mesh からだけ外す
- 取り込みで対応表が決まった後、取り込んだ mesh の複製から、ID を入れた UV channel（最後の channel）だけを消す。これを `runtime/<pack>/<object 名>.asset` に保存する。
  - 再溶接と最適化はしない。
  - blend の正本と、取り込みの検査に使う mesh には ID が残る。
  - 準備や補充のたびに外す処理はない。
- scene の構築で、各枠の renderer にこの mesh を付ける（名前と頂点数を確かめる）。
  - scene は 48 個の runtime mesh をすべて参照している（GUID で確認）。
- **確認**
  - 取り込みの中：頂点数、各 submesh の添字、位置、法線、tangent、ID 以外の UV channel、weight、bind pose が、取り込んだ mesh と同じ。ID の channel だけがない。
  - 保存後に読み直して、intake.json の hash と照合した（`MobPlanModelsRuntimeMeshCheck.cs`）：位置・法線・UV0・添字・weight と bind pose の hash、頂点数、channel 数（取り込みより 1 少ない）が **48／48 一致**。
  - 対応表は取り込んだ mesh と同じ頂点の並びで使えるので、変わらない。重複島は、別の頂点のまま残る。
- 最初に保存したとき、asset が file 名（`casual-f_1`）に改名されることが検査で見つかった。runtime は renderer を mesh の名前（`f_1`）で探すため、`<pack>/<object 名>.asset` に変えて作り直した。

### 3. VRS の開始条件
- 実測の準備完了時刻（Player の起動からの時間）：
  - 39 枠：14.4 s、15.9 s
  - 48 枠：17.7 s、19.0 s（f7、f6）
- runner（`C:\log\zantetsuken-vr\MobPlanSlash\RunMobPlanLoad.ps1`）に `-DelayStartMs` を加えた。既定は 20000 のままで、この単位の起動では 30000 を使う。
  - 準備完了の判定（頭の動きを待つ）は変えていない。
- f8 と f9 は、準備完了の 6.3 s と 5.7 s 後に再生が始まり、同期が成立した。
- **f5 と f6（exit 18）**：同期の条件が成立しなかった起動。製品の切断の失敗ではない。登場などの観測は残すが、正式な統合確認の成功には数えない。

### 4. 最終確認
- **f8**（m15、既定の順、48 枠、runtime mesh。同期成立、exit 0、通常終了「the world ended the ordinary way and gave everything back」）
  - 48 枠を準備した（12.3 s、+78.0 MB、broken 0）。
  - 48 モデルすべてが個体を持ち、描画・Pose・Hit 候補になった。失敗 0。
  - 補充と再利用：再準備 54、再登場 54。再利用した枠の個体を持ったモデルは 26。
  - Commit：根の切断があったモデル 45、再利用した枠の個体の根の切断 9、子の再切断 157。
- **f9**（m16、m_3・m_12 を先頭。同期成立、exit 0）
  - 同期した回で子の再切断が未確認だった 2 件を補った。m_3 は根 2・子 8、m_12 は根 1・子 9。
- EditMode `MobPlanSlotPoolTests` 5/5 合格（最終の状態で再実行）。

**切断の実績の一覧**（`C:\log\zantetsuken-vr\MobPlanModels\cut-record-48.csv`、`tools/cut_table.py`）
- 同期した回（f3、v3〜v5、f4、f7、f8、f9。すべて exit 0）の Commit と、同期しなかった回（f5、f6）の Commit を分けて数えた。
- Commit の確認：48 件すべてで、同期した回に根の切断と子の再切断の Commit がある。
- 見た目の確認（拡大画像）は Commit の確認と分けて扱う：

| 画像 | 撮れたモデル | 実際に見て確かめたモデル |
|---|---|---|
| 歩行中 | 22 | 11 |
| 切断後（片が描かれる v5 以降） | 14 | 6（m_8 と、Professional の 5） |

- 表示の自動の比較（画素の差、頂点の照合）は、この一覧とは別に扱う。
  - 結合の 3 件、ID を付けた 48 件の取り込みの照合、重複島 9 件の歩行時の比較。

### 5. 最終差分（commit と push の前）
- **製品（Git）**
  - `MobPlanSlotPool.cs`（再利用の順）と `MobPlanSlotPoolTests.cs`
  - 前の単位からの未 commit：`MobPlanCrowd.cs`、`SandboxNpcCharacter.cs`（モデルごとの共有）
  - 明示 ID のために製品は変えていない。製品は UV0 だけを読み、renderer を mesh の名前で探す。
- **check（Git）**：この単位では変えていない。前の単位からの未 commit 分（`SandboxPropSlashPlayerCheck*.cs`、新しい `.MobPlanModels`、`.MobPlanCloseups`、`.MobPlanEyeView`、`.MobPlanAllocStages`）がある。
- **取り込みの道具（Git 外）**
  - `Assets/Licensed/MobPlanModels/Editor/`：`MobPlanModelsIntake.cs`（ID の経路のみ・拒否の理由・実行用 mesh）、`MobPlanModelsSceneBuild.cs`（実行用 mesh を付ける）、`MobPlanModelsIdProbe.cs`、`MobPlanModelsRuntimeMeshCheck.cs`、`MobPlanModelsMergeCheck.cs`、`MobPlanModelsTopologyDiag.cs`、`MobPlanModelsAuditTrace.cs`
  - `C:\log\zantetsuken-vr\MobPlanModels\tools\`：`add_topology_id.py`、`merge_multimesh.py`、`intake_mobplan.py` v3、`cut_table.py` ほか
  - runner `RunMobPlanLoad.ps1`（`-DelayStartMs`）
- **資産（Git 外）**
  - ID 付きの 48 blend
  - `topology/blender-source-5.json`
  - `intake.json`（48 件）
  - `runtime/`（48 mesh）
  - `MobPlanModelsCity.unity`（既定の順、48 枠）
  - 確認用の取り込み：`probe-id/`、`probe-noid/`、`probe-unmerged/`
- DESIGN.md、`OpenXR Package Settings.asset`、`Register-ReferenceAsset.ps1` の変更は、この単位より前からあるもので、この単位の差分に含めない。

## 追記5（2026-09-29、TL 指示）：締める前の整理

### 1. c_21 の残件（方針の訂正）
- 追記2〜4 で「Hull 修復の別件」「上流での面の張り直し」「TL 判断待ち（修復）」と書いたが、訂正する。
- c_21 の残件は、**元の Hull のままで、cook・接触・切断・再切断・実 Slash を確認する別件**である。修復が必要かどうかは、まだ決まっていない。
- 分かっていること（追記2）：
  - DEF-foot.R の面 10 は針状の三角形で、巻き順の法線が内向きになる。
  - 形の凹みは 1 µm 未満。
- 製品がこの面をどう扱うかは、まだ確かめていない（cook、接触、切断の各段で未確認）。

### 2. 記録上の区別
- **確認済み**：48 モデルの登場（f8）と、根の切断・子の再切断の Commit（同期した回。`cut-record-48.csv`）。
- **全モデルの見た目は未確認**：
  - 拡大画像を実際に見て確かめたのは、歩行中 11 モデル、切断後 6 モデル。
  - 自動の比較（頂点の照合、画素の差）は、結合の 3 件、ID の照合の 48 件、重複島 9 件の歩行時で行った。見た目の確認とは別に扱う。

**準備時間は、計測区間が違う 2 つの値である**

| 値 | 計測区間 | 回と値 |
|---|---|---|
| 準備完了時刻（head wait の ready） | Player の起動から、crowd の準備完了（全枠の準備と 20 体の登場）まで。起動・scene の読み込み・枠の準備を含む。VRS の開始遅延を決めるのに使った値 | 48 枠：f6 19.0 s、f7 17.7 s、f8 19.7 s、f9 19.6 s。39 枠：f4 14.4 s、f5 15.9 s |
| 枠の準備時間（mobplan pool: prepared in） | crowd が枠の準備を始めてから、全枠の準備が終わるまで（`MobPlanCrowd.PoolPrepareSeconds`）。起動と scene の読み込みを含まない | 48 枠：f6 11.7 s、f7 10.7 s、f8 12.3 s、f9 12.2 s。39 枠：f4 7.6 s、f5 8.7 s |

- f8 の 12.3 s は、枠の準備時間。同じ f8 の準備完了時刻は 19.7 s（再生は準備完了の 6.3 s 後に始まった）。

### 3. 最終の製品差分での焦点試験（最終の作業木で実行、結果は `C:\log\zantetsuken-vr\MobPlanSlash\tests\`）

| 対象 | 試験 | 結果 |
|---|---|---|
| 枠の再利用の順 | EditMode `MobPlanSlotPoolTests`（`final-slotpool`） | 5/5 |
| DirectSkin の貸し出しの寿命（作成 1 回、保留中は返らない、持ち主の終了、world の終了で二重に解放しない） | PlayMode `ALentInput*`、`AnOwnerEnding*`、`TheWorldsEnd_WithALentInput*`（`final-focused-playmode`） | 5/5 |
| Hit の登録（切断した character だけを放す） | PlayMode `AHitThatTakesACharactersCut*`（同上） | 1/1 |
| `SandboxNpcCharacter` の骨の検査と姿勢 | PlayMode `PoseTablePlayerPlayModeTests`（同上） | 6/6 |
| `SandboxNpcCharacter` の切断・撤去・予算による保留 | PlayMode `U8_*`、`Withdrawal_*`、`BudgetPending_*`（`final-focused-playmode-2`） | 11/11 |

- **既存の試験がない範囲**：モデルごとの共有（`SlotShareKey`、別モデルの共有を読まない防止、共有の scale cache の利用者数）は、`PrepareAsSlot` を使う試験がない。
  - 上の試験は、どれも枠にしない character で動く。
  - この範囲は統合起動でしか確かめていない（f8：共有 48、broken 0、失敗 0）。
  - 焦点試験を加えるかどうかは TL の判断を待つ。

### 4. コミット対象と依存関係（ステージ・コミット・push は保留中）

**P：製品と試験（この 2 単位分）**
- `MobPlanSlotPool.cs`、`MobPlanSlotPoolTests.cs`（再利用の順）
- `MobPlanCrowd.cs`、`SandboxNpcCharacter.cs`（モデルごとの共有。前の単位からの未 commit）
- **依存**：HEAD の `SandboxPropSlashPlayerCheck.MobPlan.cs` の 316 行が `_crowd.SlotShareInUse` を読む。P はこれを除くので、P だけでは compile が通らない。
  - この check の 1 か所（shared reads の行を `SharedReads`、`SlotShareCount` へ）を P と同じ commit に入れる必要がある（hunk 単位の分割）。
  - 代わりに互換の member を残す方法もある。

**C：計測の check（MobPlan。前の単位からの未 commit）**
- `SandboxPropSlashPlayerCheck.MobPlan.cs`（上の 1 か所以外）、`.MobPlanDisplay.cs`、`.MobPlanHitRegistry.cs`、`.MobPlanLifetime.cs`
- 新しい file：`.MobPlanEyeView.cs`、`.MobPlanAllocStages.cs`、`.MobPlanModels.cs`、`.MobPlanCloseups.cs`（と .meta）
- **依存**：P（`SharedReads`、`SlotShareCount`、`Family`）。
  - `.MobPlan.cs` は、新しい 4 file の関数を呼ぶので、一緒に入れる。

**D：診断（Editor のみ、どの build にも入らない）**
- `Assets/Zantetsu/Editor/Sandbox/MobPlanAllocationDiagnosis.cs`（planner の割り当ての単位）
- `Zantetsu.Core.MobPlan` だけに依存する（`MobPlanCrowd` は説明の中の名前だけ）。単独で入れられる。

**R：実装記録（docs）**
- `docs/diagnostics/mobplan-models-2026-09-29.md`（この単位）
- 前の MobPlan の単位の `planner-allocation-2026-09-29.md`、`refill-reprepare-load-2026-09-28.md`、`realloc-diagnosis-2026-09-28.md`
- コードに依存しない。licensed の中身（blend、画像、ログ）は含まず、Git 外の場所を名前で指すだけ。

**入れないもの**
- **Building E2E**：`SandboxPropSlashPlayerCheck.cs` と `.MultiNpc.cs` の差分は、すべて Building E2E のもの（`Building*` を呼ぶ）。関連する未追跡の `.Building*.cs`、`BuildingSlashE2E.cs`、`BuildingAnchorScenario*`、`Tools/Export-Building*.py` ほか。この単位の commit に混ぜない。
- **前からある差分**：DESIGN.md、`OpenXR Package Settings.asset`、`Register-ReferenceAsset.ps1`。
- `Assets/Licensed/` の全部（blend、intake.json、runtime mesh、scene、取り込みの道具の C#）。
- `Tools/__pycache__/Export-Compact16uv-Physics.cpython-311.pyc`（Blender の Python が取り込みの道具から読み込んだときにできたもの。消していない）。

**入れる前の確認（提案）**：HEAD から git worktree を作り、P（と check の 1 か所）、次に P＋C の patch を当てて、それぞれ compile できることを確かめる。

### 5. Git 外の取り込みの道具と資産の再生成

**版**
- Unity 6000.3.22f1
- Blender 4.5.13 LTS
- `Tools/Export-Compact16uv-Physics.py`：2db5cc06、変更なし

**上流**
- `C:\Users\%USERNAME%\src\zantetsuken-blender-pipeline-pilot\Generated\BottomHalfUV\ITHappyCharacterMeshRepair\<Casual|Professional>Characters\1.23.0-convex-remap_palette\Working\`
- 取り込み設定の雛形：`Assets/Licensed/Compact16uvConvexRepair/character-casual/ITHappy_CasualCharacters--f_1.blend.meta`、`character-professional/...--c_1.blend.meta`（GUID だけ新しくする）

**手順**（道具は `C:\log\zantetsuken-vr\MobPlanModels\tools\`。括弧内は SHA-256 の先頭 16 桁）

1. **Hull の監査と複写**：`intake_mobplan.py`（v3、7589ac759fa7aa71。v2 から結合の指定を加えただけ）と `flip_repair.py`（443d9acd26178278）。
   - 45 件を `characters/` へ複写し、`Resources/MobPlanModelsPhysics/<family>.json` を書く。c_21 は Hull の段で記録され、複写されない。
   - 記録：`intake\intake-report.json`、`intake-report-v2-seven.json`
2. **結合**：`merge_multimesh.py`（46b838401e80178b）で m_8、m_13、c_12 を `merge\` に作る。続けて `intake_mobplan.py` に `<pack:id>=<merged blend>` を渡す。
   - 記録：`intake\intake-report-v3-merged.json`
3. **取り込み設定**：m_3、m_12、c_19 の .meta だけ `weldVertices: 0`。他は雛形のまま。
4. **明示 ID**：`add_topology_id.py`（5f5b64dd113125e7）で、48 件の blend に ID の層を加える。
   - ID を付ける前の blend は `intake\characters-before-id\`
5. **topology の記録**：`topology_source.py`（4d3ac4708ffcff93）で `topology/blender-source-5.json` を作る（48 件、e4eef5288677ada4）。
6. **Unity の取り込み**：`MobPlanModelsIntake.RunFromCommandLine`（`Editor/MobPlanModelsIntake.cs` a514c3fc920d0faa）
   - `intake.json`（523d2aea8605476e）、`topology/refused.json`、`topology/unity-topology-audit.json`、`runtime/<pack>/<id>.asset`（48 件）を書く。
   - どれも上書きはしない。作り直すときは、前の出力を `intake\` へ移してから実行する。
7. **scene と Player**：`MobPlanModelsSceneBuild.BatchBuild` と `BuildPlayerFromCommandLine`（`MobPlanModelsSceneBuild.cs` 6195d90ccbc6cd9f）。
   - build の後に `PC_RPAsset.asset` の 3 項目を戻し、`settings-before-build.sha256` で照合する。
8. **実行**：`C:\log\zantetsuken-vr\MobPlanSlash\RunMobPlanLoad.ps1`（32e13424b560e265）。
   - 引数は `-DelayStartMs 30000`、`script-v3-165.txt`、`-zantetsuEyeHeight 1.6 -zantetsuEyeLevel`、`-zantetsuPieceLifetimeThreshold 64`。

**確認の道具**（`Assets/Licensed/MobPlanModels/Editor/`）
- `MobPlanModelsIdProbe.cs`
- `MobPlanModelsRuntimeMeshCheck.cs`
- `MobPlanModelsMergeCheck.cs`
- `MobPlanModelsTopologyDiag.cs`
- `MobPlanModelsAuditTrace.cs`

**保存先**
- 資産：`Assets/Licensed/MobPlanModels/`（Git 外）
- 記録・画像・Player：`C:\log\zantetsuken-vr\MobPlanModels\`

## 追記6（2026-09-29、TL 指示）：モデル別共有の焦点試験と、commit の境界

### 1. モデル別共有の焦点試験（製品の経路：`PrepareAsSlot`）
- file：`Assets/Zantetsu/Tests/PlayMode/PreparedCharacterSlotSharePlayModeTests.cs`（`ProvisionalMassFlagActivationPlayModeTests` の partial）。
- 既存の fixture を使った：
  - `ColdWorld`（準備済みの world）
  - `LentBonedRig`（DirectSkin を貸し出せる rig）
  - authored box（`BoxCorners`、`BoxFaceIndices`）
  - `CharacterLevel`、`Evaluate`、`UntilCommitted`
  - Pose Table の作り方（`PoseTablePlayerPlayModeTests.Table`。private から internal にしただけ）
- 汎用の管理機構は加えていない。共有の中の値（利用者数、cache）は reflection で読む。

**3 つの試験**
- `SlotsOfOneModelShare_AndAnotherModelsSlotHasItsOwn`
  - 同じモデルの 2 枠は同じ key で、1 つの共有を使う（2 枠目は解析せずに共有から読む：SharedReads 1、利用者 2、1 つの scale cache に入力 2）。
  - 別モデルの枠は、別の共有・別の cache を使う。
- `AnotherModelsShare_IsRefused_AndTheShareIsLeftAsItWas`
  - モデル a を持つ共有をモデル b の枠に渡すと、準備されない（handle なし、失敗の理由あり）。
  - 共有は変わらない：a の entry、読み取り数、利用者数 1、同じ cache と入力 1。a の枠は、そのまま活性化できる。
- `OneSlotGoing_LeavesTheOtherUsable_AndTheLastFreesTheShareOnce`
  - 片方の枠を破棄すると、利用者 1 になる。cache は残り、入力 1 を持つ。
  - 残る枠を活性化（描画）し、Slash で切断して Commit まで確認した（片が描かれる）。
  - 最後の枠を破棄すると、共有の cache は null になり、入力 0。前の cache は新しい入力を受けない（破棄済み）。
- 結果：3/3 合格（本体の作業木 `share-tests-3`、別 worktree `P-playmode-share`）。

### 2. commit の境界の確認（別 worktree。HEAD 8cca35b8 から detach、Library は複写）
- worktree：`C:\Users\%USERNAME%\src\zantetsuken-vr-mpslice`
- 当てた patch：`C:\log\zantetsuken-vr\MobPlanModels\commit\`

| 段 | 内容 | 確認 |
|---|---|---|
| P | HEAD ＋ P ＋ check の 1 か所 | compile error 0。`MobPlanSlotPoolTests` 5/5、共有の試験 3/3 |
| P＋C | その上に C | compile error 0、`MobPlanSlotPoolTests` 5/5 |
| P＋C＋D | その上に D | compile error 0（`Zantetsu.Sandbox.Editor.dll` の中で compile された） |

- P に入れる check の 1 か所は、`_crowd.SharedReads` と `_crowd.SlotShareCount`（P の member）だけを使う。C だけにある宣言には依存しない（HEAD ＋ P で compile が通る）。
- P の各 file は、検証した本体の作業木の file と byte 単位で同じ。check の file は「HEAD ＋ 1 行」。
- **詳細ログの切替と集計**：自動の試験はない。既存の確認は次の 2 つ。
  - f8（C を含む build、詳細なし）：「mobplan detail files: off」。詳細の file は書かれない。集計（shared reads、models 48、モデル別の集計）は書かれる。
  - `C:\log\zantetsuken-vr\MobPlanSlash\c2-detailon\`：詳細あり（前の単位）。

### 3. commit の中身
- **P（製品と試験）**
  - `MobPlanSlotPool.cs`、`MobPlanCrowd.cs`、`SandboxNpcCharacter.cs`
  - `SandboxPropSlashPlayerCheck.MobPlan.cs` の 1 か所
  - `MobPlanSlotPoolTests.cs`、`PreparedCharacterSlotSharePlayModeTests.cs`（と .meta）、`PreparedCharacterSlashHitPlayModeTests.cs`（`Table` を internal に）
  - この記録
- **C（計測の check）**
  - `SandboxPropSlashPlayerCheck.MobPlan.cs`（残り）、`.MobPlanDisplay.cs`、`.MobPlanHitRegistry.cs`、`.MobPlanLifetime.cs`
  - 新しい `.MobPlanEyeView.cs`、`.MobPlanAllocStages.cs`、`.MobPlanModels.cs`、`.MobPlanCloseups.cs`（と .meta）
  - 記録 `refill-reprepare-load-2026-09-28.md`、`realloc-diagnosis-2026-09-28.md`
- **D（Editor の診断）**
  - `Assets/Zantetsu/Editor/Sandbox/MobPlanAllocationDiagnosis.cs`（と .meta）
  - 記録 `planner-allocation-2026-09-29.md`
- **入れないもの**
  - Building E2E の差分（`SandboxPropSlashPlayerCheck.cs`、`.MultiNpc.cs` と、関連する未追跡 file）
  - DESIGN.md、`OpenXR Package Settings.asset`、`Register-ReferenceAsset.ps1`
  - `Assets/Licensed/`
  - `Tools/__pycache__`、`ProjectSettings/SceneTemplateSettings.json`
- **Git 外の再生成の手順**：この記録の追記5 の 5.

### 4. c_21
- 修復を前提にしない。元の Hull のままで、製品の経路（cook、接触、切断、再切断、実 Slash）を確かめる残件として残す。
