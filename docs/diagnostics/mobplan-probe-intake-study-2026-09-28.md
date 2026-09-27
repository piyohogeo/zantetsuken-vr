# Animation Probe の MobPlan 取り込み調査

追記: 本文は取り込み着手前の調査記録。続くユーザー指示により実装へ進んだ。現在の実装・検証結果は [取り込み実装記録](mobplan-probe-intake-implementation-2026-09-28.md)、再現手順は [Tools/MobPlan](../../Tools/MobPlan/README.md) を参照。

調査日: 2026-09-28。対象は本リポジトリと `C:/Users/junic/src/zantetsuken-character-animation-probe`（以下 probe）、およびユーザー提示の Locomotion Search スクリーンショット2枚。今回は調査・導入案の作成までで、Runtime・DESIGN・生成データは変更していない。

調査時の HEAD は本体 `9c1ce982afcaa00dc0e8fca3c88f157a6a19056d`、probe `73a967de041072542d641b03e1e9c90a7409b447`。本体には作業中の変更があり、HEADだけでなく現作業ツリーを参照した。両方の Unity は `6000.3.22f1`。

## 推奨方針

probe の「Root Motion を持つ Clip 接続グラフを直接探索し、各NPCの候補から群衆の計画を選ぶ」方式を Phase 4.70 の実装として取り込む。探索数値コアと検証済み生成データを再利用し、計画公開・時間解決・実行先・現在姿勢適用・切断時退役は本体へ接続し直す。

初回は画像と同じ **INERTIAL / ITHappy_f_1 / C0 Optimized / 20体 / PlayerFlow** を対象とする。現行の Pose Table 評価と切断を使い、計画生成から現在のRoot移動・Clip切替、任意未来時刻の数値参照までを実装する。未来スキニング・先行切断は DESIGN の条件付き Phase 4.65 / 4.71 / 4.72 に残せる。

probe 全体のコピーや EditorWindow の製品化は必要ない。Global C0 の solver とリターゲット処理はまず probe 側のオフライン生成系として使い、成果物の取り込みを本体側で再現可能にする。

### 追加の採用判断（2026-09-28）

ユーザーの「probe側の実装に寄せる」により、プレイヤーの人工移動もprobe方式へ寄せる。先の「本体のRoot/HMDカプセル判定を残し、probeのデータだけを共用する」案から変更する。これは導入方針の決定であり、Runtimeの置換はまだ行っていない。

- NPC計画とプレイヤー人工移動は、同じ固定polygon mapとclearance場を使う。静的障害物の正本は `polygons.bin` とし、既存の箱・カプセル一覧を都市用に別途手作業で整備しない。
- プレイヤーの移動判定はXZ平面の円と `ClearanceAt` を使う。半径の初期値は画像のplayer circleと同じ1.5 m、NPCは0.35 mとする。円は人工移動／予測用で、PhysX接触Colliderを追加する意味ではない。
- probeの `DynamicCircle.Tick` のManual経路と同様、まずXZ両方向の移動先、次にXのみ、次にZのみを試し、全て不可なら並進しない。これは軸別slideであり、障害物接線への投影ではない。旋回は並進の拒否と独立に扱う。
- 旧方式の「Rootまたは予測HMD Capsuleが重なると移動・回転全体を拒否」は採用しない。HMDは視点・視野判定へ引き続き使い、実空間HMDの位置をclampしない。平面円による判定では頭だけが梁へ当たる等の高さ別制約を表さない。
- 実プレイヤー入力をゲーム側から渡す。probeのscripted playerの自動旋回・反転は試験用に留める。既存のXR入力をprobeのキーボード／ゲームパッド専用入力へ置き換える判断ではない。
- mapは初期化後に切断・破片・退役へ追従させない。clearance tileの初回生成をプレイ中のMain queryで発生させないよう準備する。

追加確認では、本体の判定器・入力接続・試験は存在する一方、`Sandbox.unity`、`XrSimCity.unity`、`MultiNpcCity.unity` の `PlayerLocomotionLevel.volumes` はいずれもSandboxの壁2枚＋柱1本であり、都市全域の障害物を登録済みではなかった。

実装時にはこの採用判断に合わせ、`DESIGN.md` §7.2.3、D-131/D-166、Phase 4.2/T-088、用語定義等の旧Primitive／HMD overlap／slide禁止の記述と対応試験を更新する。固定map、PhysX非接触、実HMD非Clamp、切断による通行境界更新なしの意味は継続する。現DESIGNの旧方式を新方式の実装制約として扱わない。

## 本体の現状

| 対象 | 確認した状態と接続先 |
| --- | --- |
| MobPlan | `DESIGN.md` §20、Phase 4.70 / T-092 に契約がある。具体的計画器は未実装。Traceには `MobPlanCreated/Extended/Invalidated`、`MobTierChanged`、`PlanGeneration` がある |
| Pose評価 | `Runtime/Core/Animation/PoseTable.cs` が v002/v003 の骨Tableを読み、解決済みSource Timeで補間する。Root trackは読み込まず、骨Pose部分のみ利用する |
| 現在再生 | `PoseTablePlayer` は1つの `TextAsset` をbindし、自前の開始時刻と `Time.timeAsDouble` からSource Timeを進める。MobPlanによる複数Clip切替・明示時刻入力には未対応 |
| 骨LOD | `PoseLodDirector` が必要骨と距離・視野による更新間隔を扱う。Hit時に `IPoseOnDemand` で現在Poseをそろえる。Hit範囲と共有キーは単一Tableを前提とする |
| NPC切断 | `SandboxNpcCharacter` が骨対応、固定scale、骨Convex、Hit登録、`VpPreparedCharacterCut` を接続済み。単純な再生サンプルだけの状態ではない |
| 切断時停止 | `PreparedCharacterWithdrawal` は元Renderer・登録した更新Behaviour・motion bodyの停止、またはroot全体の停止を行う。rootがactiveかどうかだけでNPCの生存判定をしてはいけない |
| Work | `CutWorldRoot.Dispatcher/Frame`、`SharedWorkDispatcher`、`WorkerPoolExecutor.BackgroundPool` を再利用できる。Backgroundは初期2 worker / Lowest |
| ローカル素材 | `Assets/Licensed/U8Npc/AS_Fast_WalkCycle_1.bytes`、`MultiNpc` の ITHappy f_1、`XrSimCity/Source/tiled-seed2.unity` 等が存在する。全Clipの導入と実際のRig/frame整合は別途確認する |
| 歩行領域 | `PlayerLocomotionOccupancy` は初期化時に固定した箱・カプセルのプレイヤー用領域として実装済み。都市全体の登録は未接続。今回の採用判断ではprobeのpolygon/clearanceと円判定へ置き換える |

主な参照: [DESIGN §20](../../DESIGN.md#20-モブ未来計画とai-lod)、[PoseTablePlayer](../../Assets/Zantetsu/Runtime/Core/Animation/PoseTablePlayer.cs)、[PoseLodDirector](../../Assets/Zantetsu/Runtime/Core/Animation/PoseLodDirector.cs)、[SandboxNpcCharacter](../../Assets/Zantetsu/Runtime/Sandbox/SandboxNpcCharacter.cs)、[SharedWorkDispatcher](../../Assets/Zantetsu/Runtime/MeshCut/SharedWorkDispatcher.cs)。

## probe から取り込めるもの

以下は probe の `Assets/LocomotionSearch/` を基準とする。

| ファイル群 | 取り扱い |
| --- | --- |
| `Runtime/Pose2.cs`, `ClipDataset.cs`, `AgentPlan.cs`, `WorldTrajectory.cs` | Clip/Root軌道/接続/停止経路、時刻付きsegmentと衝突評価を再利用。公開MobPlanの有効区間・世代契約を加える |
| `SingleAgentSearch.cs`, `MultiAgentSelector.cs`, `LocomotionPlanner.cs` | 時間bucket beam、時間正規化、旧候補再利用、候補互換性、greedy/pair repair、再探索、距離tierを移植する主対象 |
| `WalkableRaster.cs`, `PolygonWalkable.cs`, `GoalField.cs` | 静的障害物、保守的clearance、goalへの経路距離場を再利用。場の生成場所・cache寿命・診断counterを本体向けにする |
| `PlannerConfig.cs` | 設定値とINERTIAL用既定値を抽出。スクリーンショットとの差を明記した本体presetにする |
| `DynamicObstacle.cs` | 実プレイヤーSnapshotから定速予測を作る部分を利用。probeの手動・scripted円移動を実プレイヤー制御へ持ち込まない |
| `LocomotionSimulation.cs`, `Scenario.cs` | PlayerFlow、goal更新、recycle方針を抽出。Task起動、時刻、公開、NPC寿命、ファイルパスは本体側で構成する |
| `Editor/LocomotionDatasetBuilder.cs`, `LocomotionC0.cs` | 初回はprobe側で実行し、出力をimport。将来本体で再生成する必要が出た時に生成系の必要部分を移す |
| `Editor/LocomotionPreview3D.cs` | Root配置、Source Time、yaw派生、optimized table選択の参照実装。AssetDatabase依存のobserver自体はRuntimeへ移さない |
| `PlayerScenarioRunner.cs`, Editorのrunner/window/painter | 回帰用シナリオ・診断の参考。製品起動・描画経路には入れない |

ORCA、NavMeshAgent、Animator Controllerへの変換は不要。Clipを選ぶ探索とRoot軌道を分離して別々に駆動すると、probeの方式と現在／未来の整合を失う。

### 実在する生成物

画像のrunと一致する生成物を確認した。

- `Generated/LocomotionSearch/dataset-INERTIAL.bin`: 392,159 bytes（約383 KiB）。416ノード、15,909辺、self edge 166、stationary 18、wait-capable 2。10秒以内にwaitへ到達できるノード413、到達不能3。欠落Table 0、split segment 0。
- `Generated/LocomotionSearch/SceneMaps/tiled-seed2/polygons.bin`: 129,298 bytes。報告上779 polygon。画像の641/779はwindowに対する表示で、全データが641という意味ではない。
- C0 run: `LocalData/OptimizedPoseTables/20260927-112952-ITHappy_f_1-inertial-rootmotion-yaw-30_-20_-10_10_20_30-v0.3-locomotion/`。
- 上記runの `Tables` は320ファイル、合計414,867,220 bytes（約396 MiB）。manifestは643 bones、21,334 samples、17 boundary components、`rootAndTimingIdentical=true`、`targetChannelC0=true`、`fullFkC0=true`。
- C0 edge hash: `da14e762f616d51905ce09cea3f9f5e502b5da25872a931092b8be7fc8db7f2f`。graphと最適化Tableの組を固定する手掛かりに使える。

root-only datasetとbone Tableを分けた構成を維持する。yaw派生はrootを変更するが骨配列は共有できるため、416ノード分の骨データを複製する必要はない。20体で同じ320 Tableを共有する。上記396 MiBは現在の未削減入力のディスク合計で、製品へそのまま同梱する容量ではない。以下の必要骨抽出を導入し、TextAsset・読み込みbyte配列・managed展開の共存も含めて実メモリを確認する。

元の `Generated` / `LocalData` はprobeのGit対象外。本体では既存の `Assets/Licensed/` 配下等のprivate入力へ取り込む。コード・preset・import手順と、素材由来バイナリを分ける。ローカル絶対パスや `latest` 探索を製品Runtimeの依存にしない。

### 必要な66本へのTable削減（追加調査）

ユーザーの不要な骨データを省く意向を反映し、製品用Pose Tableは必要骨だけを保存する。**現状のprobeは643 Transform全てを保存しており、保存・ロード容量の削減は未実装。** `PoseBaker.Bake` はモデル直下以下の全Transform（モデルroot自身を除く）を列挙し、skin weightや製品の参照有無で絞っていない。C0最適化も同じ643本を保存する。

本体の66本は `C:/log/zantetsuken-vr/PoseLod/c2-bone-census/census.txt` および `PoseLodDefault/n20-default-crowd20/player.log` で確認できた。内訳は次のとおり。

| 対象（ITHappy f_1） | Tableに必要な本数 |
| --- | ---: |
| 非zero skin weightを持つ骨 | 63 |
| 上記以外の祖先Transform | 2 |
| RendererのTransform | 1 |
| 骨Convex・付属物のための追加Table骨 | 0（Convex骨19本は全て上記63本に含まれる） |
| 合計 / 不要なTable項目 | 66 / 577 |

既存調査は5時刻について必要66本のworld行列と4,728頂点を比較し、差0と記録している。本体のLODも `table bones 643, applied by level 66/36/34/32`。現状は643本を読み込んだ後に評価・適用だけを減らしており、Table本体とプレイヤーの配列は643本のままである。

製品への取り込み時に以下を行う。

1. 現在の `PoseLodDirector` と同じ選択条件（weighted bones、Hit骨、Renderer/root bone、付属物、その祖先）で必要path集合をオフライン抽出する。今回のf_1では66本。先頭66本やHumanoidの標準骨一覧で代用しない。
2. 既存のC0最適化済みTableからその66本の位置・回転・骨metadataだけを抽出し、parent/human bone indexを詰め直した製品用Tableをprivate側へ出力する。Root track、サンプル時刻、Clip duration、接続グラフは変えない。全Rigや全Transformを実行時に削除する作業ではない。
3. `SandboxNpcCharacter` の現在の `TryRequireBones(original.bones, ...)` は無weight骨も含めた358本を要求するため、製品で実際に使う骨と祖先の契約へ変更する。必要骨の検査を外すのではなく、LOD・Table export・切断準備が同じ必要集合を参照するようにする。
4. 必要な親を残した局所Poseの抽出なので、既存のC0補正結果を維持できる。まずC0後の抽出を行い、既存graph/C0を再計算しない。probeでの全骨診断用原本と製品用の66本版を区別する。
5. 元Tableと削減Tableで、採用Clipの必要骨・skinning・骨Convex、境界時刻のCurrent/Future、LOD/Hitを確認する。既存5時刻の結果を320 Clipの削減版の検証済みと読み替えない。

容量はmanifestの21,334 samplesとfloat32位置3＋回転4から計算すると、**骨Pose配列だけで366.3 MiB → 37.6 MiB（89.7%削減）**。骨path等も減るため総ファイル量は約40 MiB台が目安だが、正確なサイズはexport後に測る。これはCPU時間が同率で速くなるという意味ではない。現状の必要骨モードは既に66本だけを評価しているため、主な追加効果は保存容量、ロード・bind、メモリ削減にある。

## 初期パラメータ

### 画像で確認できる値

| 区分 | 設定 | 初期値 |
| --- | --- | --- |
| Dataset | clip dataset / character | INERTIAL / ITHappy_f_1 |
| 接続許容差 | body rotation / pelvis / effector | 15° / 100 mm / 100 mm |
| 接続条件 | velocity conditions | OFF |
| yaw派生 | loop / rates / minimum average speed | ON / ±10, ±20, ±30 °/s / 0.3 m/s |
| yaw派生 | Start/Stopにも生成 | OFF |
| planning set | base categories / neighbor hops / exclude | `*` / 0 / 空 |
| wait | manifest / manifest only | `Inertial/AS_Base,Inertial/AS_Base2` / ON |
| stop | max stop path / split idle | 10 s / OFF |
| Pose | Global C0 / ell | Optimized / 0.15 s |
| Map | map / scene | Scene / tiled-seed2 |
| Map | window / center X,Z / margin | 300 m / (-220, -170) / 15 m |
| Map | cell size / clearance cap | 0.1 m / 12 m |
| NPC | goals / count / seed / radius | PlayerFlow / 20 / 1 / 0.35 m |
| Flow | player ring / start radius | 8.3 m / 0（Player ringを使用） |
| Flow | pass offset / far goal distance / cone | 2.5–8.0 m / 15.9 m / 60° |
| Flow | waypoint switch / new goal when reached | 2 m / ON |
| Recycle | enabled / beyond | ON / 35 m |
| Recycle | spawn band / view / view range | 15–25 m / 110° / 50 m |
| Recycle | allow occluded spawn in view / cooldown | ON / 5 s |
| Player予測 | dynamic obstacle / radius | ON / 1.5 m |
| Player予測 | max speed / seed | 1.49 m/s / 7（seedはprobeのscripted player用） |
| Player予測 | prediction / extrapolate then hold | Constant Velocity / 2 s |
| 試験goal | goals inside obstacle probability | 0 |
| probe操縦 | manual turn / reverse factor | 120 °/s / 0.5 |
| probe攻撃 | attack range / angle | 10 m / 120° |
| 実行 | planning | Auto 1 Hz |

110°/50 m等はまず再現用の値。VRでのrecycle可視判定はHMDから見えるかで行い、移動方向だけからの視野判定では画面内teleportを防げない。1.49 m/sは実プレイヤー速度を強制する値ではなく、予測・再現試験の設定として扱う。攻撃10 m/120°もprobe再現用であり、本体のSlash命中判定を置き換える値ではない。

### 画像外の暫定値

画像にはPlanner/Weightsの全欄がない。以下は **現在コードの `PlannerConfig` に `ApplyDatasetDefaults("INERTIAL")` を適用した推奨初期値** であって、撮影時設定を完全に復元したものではない。

| 設定 | 推奨初期値 |
| --- | --- |
| commit target / plan horizon / search horizon | 2 s / 12 s / 6 s |
| cycle wall-clock budget | 800 ms（探索実行開始からのStopwatch経過時間。実CPU時間でもMainの許容停止時間でもなく、実行前のQueue待ちは含まない） |
| bucket / beam / states per clip per bucket | 1 s / 12 / 3 |
| evaluated successors / idle exit cap / expansions per agent | 6 / 8 / 2,000 |
| candidates / variants per family | 8 / 2 |
| sweeps / pair repair / regeneration rounds | 3 / ON / 2 |
| desired speed / goal radius | 0.9 m/s / 0.6 m |
| static margin / agent margin / collision sample | 0.02 m / 0.02 m / 10 Hz |
| root dataset sample | 30 Hz（DatasetBuildSettings既定値） |
| beam time normalization / reserved straight successors | ON / 2 |
| wProgress / wHeading / wSpeed / wIdle | 1 / 0.3 / 0.2 / 0.5 |
| wTurn / wFacing / wHeadingPath / wChurn / wOtherAgent | 0.2 / 3 / 0.8 / 0.3 / 3 |
| dynamic overlap weight / collision penalty / keep distance | 10 / 20 / 0.3 m |
| goal field | scene mapではON。cell 0.5 m、extra clearance 0.4 m |
| AI distance tier | near <8 m、mid <16 m、farそれ以遠 |
| mid / far replan interval | 2 / 3 s |
| mid / far commit target | 3 / 4 s（実コードのプレイヤー接近に応じた補正も移植する） |
| mid / far beam, candidates | 8, 6 / 6, 4 |
| blocked goal | standoff方式（mode 2） |

`Config/LocomotionSearch/prototype-defaults.json` はそのまま使わない。dataset欄が5°/30/50 mm・neighbor 1・COREMOTION系categories、scenario欄がArena・10体で画像と異なる。またINERTIAL用horizon等の選択時補正とも異なる。元の `LOCOMOTION_SEARCH_PLAN.md` の初期20秒horizonも現在のINERTIAL既定値ではない。

## 本体へ接続する際に必要な変更

### 1. 公開MobPlanと時間解決

`AgentPlan` を探索用として使い、公開する内容は完成したsegment列・対象Mob・PlanGeneration・有効区間・dataset/table identityを持つ不変Snapshotにする。`PlanSegment` の Clip / StartTime / EndTime / WorldStart に、datasetの `RangeFrom` とClip/table対応を組み合わせれば、任意時刻のRootとSource Timeを一緒に解決できる。

概念上は `TryResolve(plan, targetTime) -> { rootPose, clip, sourceTime, tableIdentity, planGeneration }` とする。通常の時間進行では世代を増やさず、計画変更・teleport・退役を世代で区別する。実時刻はGlobal FixedStepIdとの対応を持たせ、長時間sessionのfloat絶対時刻累積に依存せず、doubleの基準時刻と相対区間等で解決する。

probeの `PoseAt` は区間外でも先頭／最後のClipを返してclampする。これは未来計画が有効である証明にはならない。本体の公開APIでは区間外を失敗にし、現在表示の保持や停止tailと区別する。Clip境界は次segmentを採用し、境界直前・exact端点・Loop/ClampをCurrent/Futureで同じ規則にする。

### 2. Root移動とPose再生

本体の `PoseTable` はそのまま骨評価に使えるが、`PoseTablePlayer.ApplyNow` の独立時計はMobPlan入力へ置き換える／明示入力経路を加える。Clip切替のたびに `Configure -> Bind -> bytes読込 -> Transform.Find` を行わない。Table bankとRigの骨対応を共有・事前準備し、Clip切替では参照と時刻だけを選ぶ。

probe observerの配置は `W × inverse(H(t0)) × H(t)`。`W` はsegmentのWorldStart、`t0` は `RangeFrom`、`H` はyaw派生を含むRoot track。これを参照して、開始位置・yaw正規化・高さ・tilt・modelRoot/motionBodyのframeを確認する。骨だけ切り替えてRootを別速度で移動させたり、Root Motionを二度適用したりしない。

root-only datasetは平面XZ/yawかつ30 Hzで、observerはTableのRoot補間を使う。製品ではCurrent/Futureが同じRoot評価を使うよう一本化し、planner軌道との誤差を確認する。Y/tiltが必要な場合は元Root trackの残差を保持する。骨の補間は両実装とも位置lerp・最短符号の正規化quaternion lerpであり、基本方式は一致する。

### 3. 計画専用worker、場の構築、公開の整合単位

**ユーザーの「計画用workerを分離する」により、MobPlanは共有Background Poolから分離する。** 共有Poolでの先行比較を分離採用の条件にはしない。専用化は採用済み、下記の本数・OS優先度は実測で調整する初期案とする。Runtimeの変更はまだ行っていない。

`LocomotionSimulation.StartCycle` の `Task.Run` を、常設の計画専用workerへ置き換える。初期は1 thread、OS優先度Normal、コア固定なしを提案する。probeの `RunCycle` は1 cycleが直列なので、まずそのまま専用threadで動かす。1 threadは初期の資源配分であり、独立な集団や候補生成の並列化を恒久的に禁止するものではない。投機切断や任意maintenanceはこのworkerへ流さない。

既存の `IDispatchWork` / `IWorkExecutor` とDispatcherの投入・完了回収・寿命処理を再利用し、計画用途と専用の投入先を追加する案とする。現在のDispatcherは3実行先の固定配列、WorkerPoolExecutorはGeometry/Backgroundの2種を受け付けるため、単なる設定変更ではなく必要箇所を拡張する。別の汎用Schedulerは作らない。共有のMain側受付・frame予算まで専用化されるわけではないので、その待ちも計測し、他用途の滞留で計画受付が継続的に塞がれない有限容量の配分にする。

Mainは小さいSnapshot採取・投入・完了回収・公開だけを行う。同じ集団について古い周期要求をFIFOで溜めず、計算中の次要求は最新状態へまとめる。実行中の入力は変更せず、同じ集団の次cycleは公開・不採用を処理した最新baselineから作る。Queue満杯や期限超過時は有効な旧計画とstop tailを使い、同期再探索しない。

分離によりBackground Poolの長いWorkの後ろで待つ問題は避けられるが、OS上のCPU競合や計算量による遅延は残る。専用threadは専用CPUコアの保証ではない。現行共有Poolの固定2 worker / Lowestを維持しても、そこからMobPlanの計算能力を確保したことにはならない。

probeの `Deadline` は `Stopwatch` であり、`RunCycle` 開始時から計る。低優先度のためOSに実行させてもらえない時間も800 msを消費する一方、開始前のDispatcher／Pool待ちは含まない。初期案の「CPU budget」という表記はこの追加調査で訂正した。優先度を落とすと、単に完了が遅れるだけでなく、探索できる候補・再探索回数が減り、旧計画・停止候補が増える可能性がある。

追従性にはおおむね1 Hzで新しい有用な計画を公開できることが必要であり、採用可能性にはさらに `snapshot取得から公開までの時間 < frozen prefixの残り時間` が必要。初期commit目標2秒はClip境界まで伸びるが、必ずその時間内に処理できる保証ではない。12秒planは遅延時の移動継続・停止tailの余裕であり、プレイヤーへの応答を12秒遅らせてよい根拠ではない。旧計画でしばらく動けることと、計画更新が実用速度で追いつくことを分けて判定する。

このため、導入段階3で画像presetの実探索を計画専用workerへ載せ、最終段階を待たずに以下を測る。

- 通常のVR描画／入力、切断併走、実際に発生する他のBackground Workとの競合を含め、要求→開始の待ち時間、実行経過時間、完了→公開の時間を分ける。p95/p99と最大値、連続未更新時間を記録する。
- 実行時間だけでなく、公開更新間隔、timeout／stale率、探索量、旧候補採用、停止時間、プレイヤー付近の応答を確認する。採用されない結果だけを速く返しても合格にしない。
- 実行開始時に残る公開猶予を確認し、既に期限を失った仕事を探索しない。探索に渡す時間上限も残り猶予を考慮し、Queue待ちの後に常に追加800 msを使えると仮定しない。800 msは協調的な探索上限であり、厳密な完了時刻保証ではない。
- 比較時はNPC数・Clip・探索設定・表示品質をそろえる。骨Tableを643→66本にしてもroot-onlyの探索コストは直接には減らない。

専用workerでも不足する場合は、原因に応じて対処する。受付待ちならDispatcherの配分、OS上のCPU不足ならworker優先度／本数・他のWorkとの配分を調整する。CPU計算自体が重い場合は候補生成の独立部分等の最適化・並列化を検討する。いずれもMain／urgent物理／切断への影響を併せて測り、NPC数や探索品質を黙って下げて成立扱いしない。

なお、現行 `CutWorldProfile.BackgroundWorkerCount` は `WorkerPoolExecutor.BackgroundPool(capacity)` の引数に渡されており、実際のthread数は `DefaultBackgroundWorkerCount = 2` で固定。専用workerの設定ではthread数・保持容量・優先度を区別し、この曖昧さを引き継がない。Background側の設定整理は別問題であり、専用化の代わりにこの値だけを増やして済ませない。

実装時は採用判断に合わせて `DESIGN.md` §4.3/§4.4/§20.4等の「Mob計画はBackground Pool」の記述を更新する。有限投入、Main非待機、旧世代結果の不採用、利用中入力の保持、終了時の回収は継続する。

probeの `FieldFor` はgoal変更時にMainでDijkstraを生成する。SceneMapsのpassability構築、clearance tile初回生成も費用が大きく、画像ではtile生成が約9.8秒と表示されている。全域準備はロード中に行い、Runtimeのgoalに必要な距離場生成は計画専用worker側で扱って待ち／実行時間へ含める。最新goalの計画を共有Background Poolの場生成待ちへ戻さない。画像の300 m窓はclearanceを全域展開すると330 m相当・約44 MB規模となり、goal field群は別容量。必要範囲・共有・cache上限を測る。

probeの失効判定は主に `MinCommitUntil < now` と `LastRecycleTime > taskRequestedAt`。本体では入力時のMob世代、goal前提、map/settings identityと公開時状態を照合する。現在コードは完了時の最新GoalRevisionをplanned扱いするため、計算中のgoal変更も取りこぼさないよう投入時revisionで照合する。

候補選択はNPC同士の組合せに依存する。一体をteleportした結果だけ除いて他の古い組を公開すると、新しいNPC baselineとの互換性は保証できない。初期は影響した相互依存集合の結果をまとめて不採用にする保守的方式でよい。無関係な集合まで恒久的にglobal直列化する必要はない。同じ集団の次cycleは公開されたbaselineに依存する、という具体的理由で更新を調整する。

`PolygonWalkable` のlazy tileはlockを使い、`MapQueryStats` はstatic counter、planner obstacle参照とsegment scratchはThreadStaticである。複数Workへの移植時は診断counterの競合、共有cacheの待ち、スレッドローカル参照の保持を見直す。全面Burst/Native化や無allocation化は導入の前提にしない（DESIGN §20.1）。

### 4. 骨LOD、Hit、切断時退役

AI planning tierと既存の骨Pose LODは別の頻度制御として接続する。骨を間引いたframeもRootと論理Clip時刻は計画に従って進める。Hit要求時は同じ対象時刻のRootと必要骨を適用し、現在Convexで判定して同じPoseで切断入力を確定する。

`PoseLodDirector.RangeOf` は1 Clipの全sampleから保守的Hit範囲を作る。INERTIALの複数Clip・C0補正後Poseを扱う場合、初期ロード／オフラインで対象Clip集合の範囲を作り、骨maskとともにRig/Table集合で共有する。切替のたびに全sampleをMainで走査して登録し直さない。まず全必要骨で正しさを確認し、その後既存LODを接続する。

切断の採用／元NPC withdrawalの境界でplanner参加を終了し、PlanGenerationを失効させ、MobPlan駆動Behaviourもwithdrawalへ登録する。`IsWithdrawn` 等の既存状態に接続し、rootのactive状態だけを見ない。単なるHit候補や切断拒否で退役させない。登録済みWorkは完了後不採用・回収とし、借用中のRig/Meshを早期破棄しない。

移動体の初回切断ではmotion bodyのRoot姿勢・並進／角速度も合わせる。既存コードはmotion bodyの速度を切断側actorへ引き継ぐため、Transformだけ動かす実装では切断後の運動に反映されない可能性がある。

### 5. PlayerFlow、map、recycle

PlayerFlowはプレイヤー脇の通過点から先の遠いgoalへ進ませ、周囲に歩いているNPCを維持する方式として取り込む。実プレイヤーの位置・速度はゲーム側からSnapshot化し、2秒定速外挿後holdで評価する。これはsoft costであり、実プレイヤーとの完全な衝突回避を保証しない。

初回の比較は同じ `polygons.bin` と座標原点で行う。本体のtiled-seed2 sceneは存在するが、抽出元と同じ障害物配置・scaleかは未検証。製品用export時に確認する。追加の採用判断に従い、NPC計画とプレイヤー人工移動でこの固定map・clearance場を共用し、プレイヤーはprobeの平面円・軸別slideへ接続する。切断で変わる建物・瓦礫をmapへ反映する範囲は将来の別変更とする。

画像のrecycleは取り込み対象に含める。元の `TryFindSpawn`、現在位置＋2秒先との間隔判定を利用し、実HMDで見えないことを確認する。見えないspawnが見つからなければ延期する。probeの `Attack()` にある「自由点、それもなければその場」というfallbackを製品の補充規則へ流用しない。

本体の実攻撃はSlash/現在Pose切断を使用する。切断後は元NPCをteleportして復活させず、新しい生存個体として補充し、切断片と借用資源は既存寿命へ残す。遠方の未切断NPC recycleと切断後補充は同じspawn候補選択を使えても、資源寿命は分ける。

## 導入順序と完了確認

| 段階 | 作業 | 確認する結果 |
| --- | --- | --- |
| 1. 入力固定 | 画像preset、上記dataset/C0 run/mapをimportする手順とprivate配置、C0後の必要66本Table抽出を用意 | Clip/table/Rig対応、416/15,909、停止可能性、C0 runの一致、削減前後の必要Pose一致。元のリターゲット・C0を再実行せず再現できる |
| 2. 計画コア | `Runtime/Core/MobPlan/` 等へ数値コアを移植し、明示時刻の公開MobPlanを作る | 実際の探索による計画生成、legal edge、旧prefix保持、Current/Futureの任意順参照、範囲外拒否 |
| 3. 実行接続 | 計画専用workerを追加し、既存Dispatcherへ接続。Flow/場生成/失効を組み、競合時の追従性を先行計測 | 待ち／実行／公開遅延、計画品質と更新間隔から専用workerの配分を調整。遅延・Queue満杯・goal変更・teleport中の後着結果が現在状態を上書きしない。Mainが待たない |
| 4. 現在表示 | Table bankと明示入力で20体をRoot移動・Clip切替。C0 Table使用 | yaw variant、境界前後、長時間、Root/frame整合、必要骨、共有メモリ。Clip切替で読込spikeなし |
| 5. ゲーム統合 | Hit/切断withdrawal、motion body、骨LOD、実プレイヤーの共有map・円判定・slide、recycle/補充 | 歩行中の現在Pose切断、切断中の後着計画拒否、同じ個体の復活なし、視野内spawnなし。プレイヤーの全移動可／Xのみ可／Zのみ可／両軸不可と独立旋回 |
| 6. 実機条件計測 | 実際のscene・20体・移動するプレイヤー・切断併走でPlayerを計測 | Mainのframe費用、planner queue/CPU/latency、期限切れ、GC、メモリ、goal field、Table適用、切断spike |

数値コアをCoreへ置き、Dispatcherに依存するadapterを既存の構成層に置けば、CoreからMeshCutへ依存を足さずに進められる。専用assemblyが必要なら具体的な依存を見て追加する。

試験はT-018 / T-092と現在Pose切断の既存回帰を使う。最小の合成Clip/graphで時間・世代・公開を確認し、privateな実データで20体・画像presetを確認する。全probe benchmarkの再実施を前提にしない。静的違反やNPC同士の距離は回帰指標として測るが、本体の「多少の重なりを許容」を完全無衝突保証へ変えない。

probe報告のINERTIAL open-crossing（Mono、20体、60秒）p95 378 msや、別条件のIL2CPP 20体p95 132 msは参考に留める。画像の300 m都市、C0表示、実切断併走での費用を保証する測定ではない。今回Unity実行・ビルド・性能再測定は行っていない。

## 残る確認事項

- 画像外のPlanner/Weights値は上記のINERTIAL既定値を暫定採用する。撮影時の厳密な復元が必要な場合のみ追加の設定保存が必要。
- C0は位置／姿勢の接続を改善するがC1・速度連続性・足接地を保証しない。manifestの最大補正はpelvis約90 mm、関節約30.9°。probe報告ではstanding成分の足の動きが残る。本体で歩行・idle・切替を目視する。
- f_1から別のCasualモデルへ展開する際は骨名だけで流用判断せず、Rig・bind・scale・骨Convex・Tableの整合を確認する。初回は画像と同じf_1で閉じる。
- 300 m固定窓の外へプレイヤーが移動する運用、map更新、瓦礫回避、VR視野を使った補充範囲は初回比較後に対象を広げる。初回の範囲を実装・presetに明示する。

probe側の主な根拠は `Reports/LocomotionSearch/PHASE_LOG.md`（特に2026-09-27のC0・beam正規化・recycle・後着結果修正）、`SCENE_WALKABLE_MAP.md`、`Reports/GLOBAL_C0_OPTIMIZATION_RESULTS.md`、生成物のexport-report/manifest、および上記Runtime/Editorコード。古い計画書・保存JSONより、画像と現在の生成物・実装を優先して本案をまとめた。
