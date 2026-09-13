# VR斬鉄剣ゲーム 技術設計書

*即時シェーダ切断と非同期メッシュ／物理更新による、低遅延・反復切断パイプライン*

| 項目 | 内容 |
| --- | --- |
| 文書目的 | Codexで継続更新するプロジェクト設計上の正本 |
| ステータス | Draft v1.5 / PoC実装準備・観測／未来評価設計段階 |
| 作成日 | 2026-08-21 |
| 最終更新 | 2026-09-13 |
| 想定エンジン | Unity 6.3 LTS 6000.3.22f1 + OpenXR + URP |
| 採用アセット | Synty POLYGON City Pack（主素材）、Poly Pro Universe（比較・補助素材） |
| 初期対象 | PCVR、90Hz基準。Quest単体版は当面スコープ外 |
| 検証用HMD | Meta Quest 3SをQuest LinkでPCVR接続 |

> **設計の核** 刀の放つ斬撃波への応答として、必要なベイク・VP変換後にGPU仮切断を表示し、共用VP Geometryと物理Convexをバックグラウンドで切断して追いつかせる。有効な先行準備は再利用し、表示開始負荷と容量処理の許容は4.5に従う。プレイヤーが感じる応答時間と、正確な幾何・物理更新を分離する。

## 1. エグゼクティブサマリー

本企画は、VR空間内の多様なプロップや人形を、刀の放つ斬撃波に沿って任意方向に両断できるアクションゲームである。最大の体験価値は、斬撃直後に隙間が開いて対象が分離したように見える即応性と、その後に破片が自然に物理挙動へ移行する一貫性にある。

推奨アーキテクチャはUnityを基盤とし、OpenXR、ステレオ描画、シーン管理、Rigidbodyなどを利用しながら、切断判定、仮切断レンダラ、メッシュ切断、Convex切断、世代管理を独自サブシステムとして実装する構成である。フルスクラッチのエンジン開発は行わない。

見た目はSynty POLYGON City Packを素材基盤とし、限定パレット、セル陰影、輪郭線、独自の看板・グラフィティでポップなローポリ都市へ統一する。特定作品の直接模倣ではなく、Y2K的な都市感、誇張されたシルエット、色面の強さをデザイン原則として抽出する。

## 2. 体験目標と設計原則

- 斬撃入力に対する見た目の反応を、幾何切断完了より先に提示する。

- 表示と物理の不一致時間を短くし、周辺破片が透明な旧Colliderへ接触する状態を最小化する。プレイヤー身体・手は初期仕様ではプロップ／破片とPhysX接触せず、刀も物理衝突させず切断可能時の論理Sweepだけを使用する。人工移動はLevel初期化時の固定Occupancyに対する候補次姿勢Overlapで要求全体をRejectし、実空間HMDはClampしない。Camera被り、未登録物体の内部視点、即時StencilのCamera-inside破綻はD-131の許容に従う。

- 生涯切断数や全Pending Cut数ではなく、実際にBatchへ投入する`TemporaryRenderCapRecordSet`、対象の共用Geometry、固定長`SelectedTemporaryClipPlaneSet`が一時描画コストを決める。Geometry Commit済みの境界は一時描画費用へ含めず、Hybrid Clip容量を超えた面について追加のClip Plane評価・対応Stencil Volume submitを行わない。

- 切断対象の表示とStencil Volumeは、幾何切断に必要なTopologyを持つ同じ共用Geometryを正本とし、一度の切断・Cap生成結果を両用途へ使用する。通常切断の正常成功は正負二集合、各側一つのFinal Physics OwnerとLogicalFragmentとする。Compound Physics Proxyは独立した物理表現として残し、製品用Strict Solid Cut Meshは生成・常駐・Fallbackのいずれにも使用せず、Global Solid Reconstructionは将来研究だけに隔離する。

- 建物由来の動的分裂子には7.2.2の独立World D6を使うが、倒壊防止や変位上限を保証しない。一般外部Jointを製品切断対象から外し、正負二集合・点Anchor・Graphなしの方針を維持する。

- バックグラウンド結果は世代番号で検証し、古い結果を安全に破棄できるようにする。

- 短期プロトタイプでは対象範囲と品質契約を限定し、計測結果に基づいて拡張する。

- 基本Playableを現在状態のProp／Humanoid切断で先に完成させる。後段では飛翔する斬撃波の到達時間を計算猶予として利用し、命中前に未来姿勢、表示／Stencil共用VP Geometry、Convexの切断を投機的に評価する。

- 予測結果は確定・条件付き・投機の信頼度と世代番号を持ち、実接触時の検証に成功したものだけをコミットする。

- 非同期処理と状態遷移は最初のPoCから相関ID付きで記録し、性能計測と因果関係の調査を同じ時間軸で行えるようにする。

- デバッグ映像はTraceを補助する証拠としてFrameIdと同期し、PoC初期はUnity側の選択的キャプチャ、必要性確認後はOpenXR Projection Swapchain Captureを段階導入する。

## 3. スコープ

### 3.1 初期垂直スライス

Phase 0.55でSlash操作を先に調整し、4.50～4.52でProp＋動作中NPCの基本PlayableをPredictionなしで成立させる。以下の街区・製品Assetを含む仕上げはPhase 5.5～7で行い、後続最適化・コンテンツ完成を4.52へ前倒ししない。Gameplay命中は19.1の一本Segment Sweep、表示は19.1.8のSlashWave VFXとする。

- 街区1つ、切断可能プロップ約10種、NPC 1体、刀1本。建物を切断対象に含め、道路は切断対象に含めない。

- 単一切断、連続切断、処理中の再切断を検証する。処理中の再切断は先行`LogicalCutOperation`が公開済みで、Geometry Work／Geometry Commitだけが未完了の区間を保証対象とする。先行Operation未公開の親とその仮表示領域への後続切断は受付けない。

- 即時clip表示、仮断面、後追い共用VP Geometry切断、Convex差し替えまでを一連で実装。

- 切断対象は閉じた静的メッシュと、切断時に姿勢を固定できるHumanoidに限定。

- 非空の切断結果は寸法だけで消去せず通常Fragmentとして扱い、専用Convexを持たない共用Geometryも同SideのFinal Physics Ownerへ所属させる。

- PCVRを対象とし、実アプリの両眼描画90fpsを性能目標とする。XRコンポジタの再投影は瞬間的な取りこぼしへの安全網であり、常用前提にしない。

- 実装と性能計測は非VRモードから開始する。Phase 0.5でQuest 3S有線Quest Linkの最小XR確認、0.51～0.54でBlade入力、Gesture／Plane、HitなしSlashWave Coreと第一候補、Sandbox UIを段階実装し、0.55で実機探索・調整を行う。VP表示系列は並行可能とし、1.50～1.52の能力確認後にPhase 2へ進む。実切断との統合は4.50以降とする。

- 剣を素早く振ると斬撃波が拡大し、有限速度で飛翔する。表示は19.1.8に従う。接触時の即時分離を主要攻撃表現の目標とし、必要な準備と同フレーム表示の扱いは4.5.2に従う。

Phase 5.5より後の任意拡張として、確定済みの物理所有単位を二つの通常物体へ分ける追加空間分割と、共用Geometryが確定空の通常物理物体を終了する物理GCを7.9に定める。両機能は個別に有効化でき、通常切断の成立条件にも相互の必須依存にもせず、初期垂直スライスの必須範囲へ前倒ししない。

### 3.2 初期スコープ外

- 布、髪、軟体、流体を含む連続変形シミュレーション。

- 切断後もアニメーションを継続するSkinned Mesh。

- ネットワーク対戦向けの決定論的切断同期。

- 自己交差や非多様体を含む任意入力メッシュの完全保証。

- 数千体規模の同時動的破片。

- Quest 3Sを含むQuest単体実行向けの性能保証とAndroidビルド最適化。

### 3.3 開発環境とリポジトリ構成

Unity EditorはUnity Hubの管理領域へインストールし、プロジェクト内へEditor本体を複製しない。初期固定版はUnity 6.3 LTS `6000.3.22f1`とし、Unity Hubが導入した次の実行ファイルを基準とする。文書・ログ・設定に記録するユーザーディレクトリは実名を使わず、Windows環境変数`%USERNAME%`で匿名化する。

```text
C:\Program Files\Unity\Hub\Editor\6000.3.22f1\Editor\Unity.exe
```

公開UnityリポジトリとライセンスAsset専用の非公開リポジトリは兄弟ディレクトリとして分離する。公開リポジトリ直下をUnityプロジェクトルートとし、`Game`などの追加階層や同名フォルダの二重化は行わない。非公開リポジトリはGit LFSを使用する。

```text
C:\Users\%USERNAME%\src\
  zantetsuken-vr\                    # 公開Unityリポジトリ
    .git\
    .gitignore
    DESIGN.md
    Assets\
    Packages\
    ProjectSettings\
    BlenderPipeline\
    Tools\
  zantetsuken-assets-private\        # 非公開Git LFSリポジトリ
    .git\
    .gitattributes
    Vendor\Synty\POLYGON_City\v5\Original\
      POLYGON_City_SourceFiles_v5.zip
      POLYGON_City_Unity_2022_3_v1_12_4.unitypackage
```

新規作成には空の`Universal 3D`テンプレートを使用し、URPを初期設定する。`Universal 3D Sample`は使用しない。Hubでプロジェクト名を`zantetsuken-vr`、保存場所を`C:\Users\%USERNAME%\src`とした場合、最終作成先が上記リポジトリ直下であることを確認する。既存リポジトリがあるため作成を拒否された場合は、一時ディレクトリに生成した`Assets`、`Packages`、`ProjectSettings`だけをリポジトリ直下へ移す。

Gitでは`Assets`、`Packages`、`ProjectSettings`を管理し、`Library`、`Temp`、`Logs`、`Obj`、`Builds`、`UserSettings`を除外する。Unityの完全版は`ProjectSettings/ProjectVersion.txt`、Package依存は`Packages/manifest.json`と`Packages/packages-lock.json`を正本として固定する。

Unity CLIはPoC初期には使用しない。これはEditorやRuntimeに必須ではなく、現時点では実験的な外部管理ツールである。自動テストとビルドはまず固定版`Unity.exe`を明示パスから`-batchmode`で起動する。CI導入時にCLIの成熟度とUnity Pipeline依存を再評価し、導入する場合もプロジェクト形式の変更とは分離する。AI AssistantとHubのソース管理連携も初期作成時は無効とし、既存Gitを使用する。

Unityを更新するときもプロジェクトは作り直さない。Unity Hubへ新旧Editorを並存させ、Gitの専用アップグレードブランチでバックアップ、Package互換性確認、Editor変換、再インポート、固定テスト、非VR性能基準、XRスモークテストの順に検証する。合格するまで旧Editorを削除せず、`ProjectVersion.txt`、`manifest.json`、`packages-lock.json`の変更をレビュー対象とする。

Unityメジャー版ごとの恒久的なプロジェクト複製は作らず、リポジトリ直下の1プロジェクトを正本とする。同時比較が必要な更新作業だけ、リポジトリ外の兄弟ディレクトリへGit worktreeを作成し、検証後に破棄する。`Library`等の生成物はworktree間で共有しない。

## 4. システムアーキテクチャ

共通Player終了出口へ送るのは、共用Geometry切断またはFragment／Boundary／Operation構築で既存境界から表面化した予期しない内部エラー、4.5.4で定めるCPU backing容量の不成立、同節のGPU backing容量の不成立、5.6の必須Stencil構成の不成立の4条件とする。製品PlayerのComposition Root上の共通Player lifecycle境界が、Renderer初期化より前からPlayer寿命を通じて単一の非永続終了要求latchを所有する。Renderer初期化処理と切断Coordinatorはこの境界へ報告し、切断受付・Commitも同じlatchを参照する。Workerの異常は既存の通知・回収境界でMainへ渡し、Workerはlatchや終了APIを直接操作しない。Mainが最初に扱った原因でlatchを一方向に確定し、同じMain Threadで直列に処理する新規切断受付と未公開切断成果物のCommitを閉じてから、既存ログへbest effortで一度記録し、製品Playerの終了APIを一度呼ぶ。「終了要求後」はAPI呼出し後ではなくlatch確定後を指す。エラーに関わる未公開成果物を公開せず、初期化中はGameplayを開始しない。同一sessionでのrollback・整合回復、全Job／資源回収、ログ永続化、Trace／Captureのトリガー・post-roll・freeze・保存・Publication・CaptureComplete・durable flush完了、終了所要時間は保証しない。ログが使えなくても終了を妨げず、回収・保存を待たない。任意の保存は非待機のbest effortに限る。内部エラーの全検出は保証せず、このためのValidator・詳細Reason・永続schema・復旧基盤を追加しない。通常の世代失効・出力予約不足、7.1の個別物理退役、Capture-only Fail Fast／Poison、Tool／Benchmark／Trace・Capture Run単位の失敗は既存境界を維持する。

> **状態モデル** 生存LogicalFragmentごとの一回の物理置換は7.1の短寿命PhysicsSplitTransactionが所有する。正負Final PhysicsとLogical Publicationを一体で公開した後はTransactionを終了し、各子は一Final Physics Ownerを専有する。Pending CutはGeometry責務だけを残し、4.5.6の祖先順Geometry CommitまたはBranch退役で完了する。具体的GeometryとCutBoundaryRecordは後着でき、Job完了・CPU範囲Published・GPU転送はGeometry Pipeline内部で扱う。

### 4.1 コンポーネント境界

| サブシステム | 責務 |
| --- | --- |
| Blade Pose Adapter | OpenXR Grip Poseへ持ち手別のGripToKatanaOffsetを適用し、BladeAxis、EdgeDirection、SideNormal、追跡有効性を提供 |
| Cut State | LogicalFragmentの生存性、各Source最大1件のActive PhysicsSplitTransaction、Pending CutとGeometry依存、採用面・Side・親子履歴、受付上限を管理する |
| Temporary Slice Renderer | clip、論理破片の分離オフセット、仮断面、切断縁演出 |
| Visual Slice Worker | 4.5.2で導入を採用した未来Rig PoseのJobベイク・VP入力準備、VP入力の三角形切断、断面生成、属性補間、VPプールへの正負Index直接出力 |
| Physics Slice Worker | 7.2のOwner単位Cut/Cook処理とFinal候補生成 |
| Commit Controller | 8章のauthority照合、Final Physics／Logical Publicationの一体切替、4.5.6のGeometry Commit、7.1の単独退役を担当する。建物D6と7.9の任意処理も同じ公開・退役境界へ接続する |
| Blade Gesture／Plane Detector | Grip／Tracking、Gate、accepted samples、Stroke BeginとPlane候補を作る。対象Hitを決定しない |
| Slash Latch／Frame Estimator | LatchReadyとValid／固定軸を独立に返す交換可能境界。内部方式は19.1へ集約 |
| Slash Span Candidate／Close Estimator | Raw候補の有効性と現在刀入力の受付終了を別々に判断する。必要な一時状態をWave寿命中に保持できる |
| SlashWave Simulator | 19.1の生成条件を一か所で判定してWaveを公開し、固定面・軸、TravelDistance、AcceptedSpan、単一Segmentと寿命を管理する。対象や切断状態を解釈しない |
| SlashWave Hit Detector／VFX | Hitは現在採用Convexへの閉Segment Sweepと系譜消費、VFXは19.1.8の表示専用平面表現。Hitの詳細は19.1 |
| Slash Candidate／Prediction | Phase 4.53以降に投機候補範囲を列挙し、基本Waveと実Hit検索を変更しない |
| Future Evaluation Scheduler | 4.4の有限容量・フレーム予算と意味上の優先順位でReady WorkをScheduleし、非blocking完了回収を既存DAG／Commitへ接続する |
| Mob Future Planner | 副作用のない固定ステップ移動Kernelと`AnimationPlannerV1`からMobPlanのRoot軌道と`ExplicitAnimationStateV1`を生成し、Nearのライブ更新、Mid／Farの軌道再生、粗い無効化を同じ世代契約で接続 |
| Animation Pose Evaluator | immutableな明示Animation Stateと対象`FixedStepId`からcanonical Bone順のRig Poseを生成する。controllerなしPlayable／Mixer、Pose Table等を交換可能Backendとし、AnimatorController内部状態をCurrent／Futureの正本にしない |
| Observability／Trace | Profiler計測、状態イベント、Work Item／Job相関、boundedな履歴、診断保存、Editorタイムラインを提供 |
| Visual Capture | Unity側の選択的片眼録画と異常時静止画をTraceへ関連付け、後期にはOpenXR API LayerによるProjection Swapchain Captureを提供 |
| Asset Preprocessor | Blenderをヘッドレス実行し、ライセンスAssetから表示／Stencil共用Geometry、幾何Topology、点Anchor、Compound Physics Proxy、検証レポートをローカル生成。Phase 5.5で建物由来Metadataと初期Depthを生成する。製品用Strict Solidは生成しない |

### 4.2 切断イベントの時系列

- 刀の連続姿勢を収集し、Edge Direction Gateを通過したGestureだけをSlash候補とする。この段階では対象の命中も対象世代も変更しない。

- 19.1.4のWave生成処理で発射を確定し、初期Segment・SlashWave VFX・共通Sweepを同時に開始する。識別は8章、容量と満杯時の扱いは19.1.6に従う。

- Waveは固定TravelAxisへ進み、有効Raw候補のrunning maximumからAcceptedSpanと現在Segmentを更新する。入力終了・Expireと更新順序は19.1を正本とし、Guide処理は採用方式に従う。

- 共通Sweepと現在採用Physics Convexの交差を確認し、LogicalFragmentRef系譜単位の未消費候補を先に消費してから既存受付へ渡す。No-op／見送りでも同Slashの再試行はしない。

- Phase 4.53以降は実Hitと独立に候補範囲を列挙し、BaseObjectGenerationと予測前提を使って投機計算する。候補だけではPending Cutを作らず、未準備・不採用・範囲外の実Hitは現在状態経路へ進む。

- 実HitはPhysics Scene上の現在Convexとの交差で確認し、`SlashHitConfirmed`を記録する。Transaction中の旧／Provisional Convexへの実Hitも観測できるが、受付処理はSourceの生存性、Active Transactionなし、Final Physics入力Snapshot、7.6のrobust support、7.7の容量の順に確認する。Activeな同じSourceと仮表示領域への要求は分類・ID発行・仕事登録前に見送り、保存・再実行しない。同じObjectIdの別LogicalFragmentは並行できる。

- 対象状態を変える前に実姿勢と採用Local Planeを確定する。任意Phase 4.55の対象剛体は19.5.1、対象外・未実装は現在Poseからの面を使う。No-opまたは容量見送りは表示・物理・Anchor・世代・既存仕事を変更せず、新しいCutOperationId、Pending Cut、Transactionを作らない。

- 受付を通過した場合だけCutOperationId、Source、採用面、受付Snapshot、Pending CutとPhysicsSplitTransactionを登録し、受付前の基底世代を保持してObjectGeneration更新・未完了件数加算を同じMain境界で行う。必要な4.5.2の入力準備後に即時clip／Stencil／仮Capを開始し、Physics Workを開始、Geometry Workを登録する。直前祖先Geometryが未CommitならGeometry Kernelだけを待たせる。点Anchor配分などの必要入力未完了は既存Work依存で待つ。

- 7.1の正負Final Physics CommitとLogicalCutOperation／正負2子のPublicationを外部的に一体で行う。Hit／Query／Temporary表示の追従先を子へ切り替え、Transactionを終了する。Geometry結果、実Cap、CutBoundaryRecord、GPU転送はこのPublicationの前提にしない。以後はGeometry未完成でも子の再切断、即時複数面表示、物理処理、後続Geometry Work登録を開始できる。

- 投機成果物は8章と19.5.1の入力・Pose・面条件が一致する場合だけ再利用する。未完成なら同じ採用面で既存仕事を継続し、不採用なら現在状態経路を使う。未Commitの祖先CPU Geometryを後続切断Kernelへ貸し出さず、表示切断の実行・Commitは4.5.6に従う。個別物理失敗は7.1、共用Geometry／論理構築の予期しない内部エラーは4章に従う。

- Physics責務を終えたPending Cutは、自分のGeometry Commitまたは生存Branch不要による終端まで残す。BoundaryはGeometry Commit時に後着し、実体化した面のTemporary表示だけを回収する。公開済みOperationが履歴にあるだけでは後着成果物の採用authorityを証明しない。

### 4.3 バックグラウンド実行モデル

フレーム内または複数フレームにまたがるCPU計算は、C# `Task`を大量発行せず、Unity C# Job SystemとBurstを基本とする。メインスレッドはUnity Objectを数値スナップショットへ変換し、締切と優先度に従ってJobをBatch Scheduleする。Job本体は`NativeArray`、`NativeList`、`NativeStream`等のアンマネージデータだけを扱い、GameObject、Component、Transform、Renderer、Rigidbodyを直接操作しない。

- Job向き：候補交差、三角形分類、表示／Stencil共用VP Geometry切断、Convex平面クリップ、断面・質量特性生成、未来軌道／MobPlanのBatch評価、未来Rig Poseからの頂点スキニング・VP形式への変換（4.5.2）、対応APIによるCollider Bake、7.9の任意分割時のIndex振り分けコピー。

- メインスレッド向き：通常命中の同期SkinnedMeshRenderer.BakeMesh・CPUデータ取得／VP変換、Backendに応じたPose評価（19.3）、JobのSchedule・完了回収、`JobHandle`依存関係、世代／命中検証、表示VPの範囲管理・GPU更新・Geometry参照公開、7.2の物理用MeshDataのMesh適用、Rigidbody／Collider生成、描画フレーム／物理ステップ境界のCommit。

- `Task`／Unity `Awaitable`向き：ファイルI/O、Trace／録画保存、Editorツール、外部プロセス待機、Unity非同期APIの進行制御。CPU幾何計算の標準実行基盤にはしない。

極小Jobを対象ごとに無制限発行せず、同種処理を`IJobFor`／`IJobParallelFor`等でBatch化する。JobはSchedule後に中断できないため、投機前提が崩れた場合もメインスレッドから`Complete`を強制せず、完了後にGeneration不一致として破棄する。`TaskId`はC# `Task`型を意味せず、Job、I/O、GPU処理を含む論理Work Itemの相関IDとして維持する。

7.9の任意分割もこのJob／Main Thread境界を使い、全体1件の試行を探索から必要cook・採否・回収まで保持する。物理GCの登録終了は既存の安全なMain Thread／物理Step境界へ接続し、専用WorkerやC# Task大量発行を追加しない。

### 4.4 Ready WorkのDispatchと完了回収

Main Threadの共有Dispatchは、既存DAGで依存が解消したReady Workを、有限容量・描画フレーム予算と次の意味上の優先順でScheduleする。現在状態の物理安全、命中済みPhysics、命中済みGeometry仕上げ、命中前の投機、Maintenance／任意品質向上の順とし、同順位の順序は安定させる。投機同士では到達Deadlineを考慮できるが、厳密なDeadline順は要求しない。低優先仕事が進まない場合を許容し、完了期限を保証しない。

一描画フレーム内に、完了済みWorkの非blocking回収、依存解消、新たにReadyになったWorkのScheduleを複数回進められるようにする。同じフレームの後の実行機会で、先にScheduleしたJobが完成済みなら、残予算と実行枠の範囲で後続をScheduleできる。各機会は同じ描画フレームの残予算を共有し、呼出しごとに予算を再充填しない。回収・依存解消・Scheduleに進捗がなければその機会を終了し、未完了Jobを待つbusy polling、spin、強制Complete、PlayerLoop再入や全Work一括完了待ちを通常経路に持ち込まない。呼出しの配置・回数と予算の表現は実装詳細とし、短いJobも同一フレームで完成するとは保証しない。

待機Workと同時実行資源はboundedに保つ。物理安全・命中済みPhysics以外の仕事だけで、それらの待機Queueへの投入余地を使い切らない。容量配分の実現方式は固定せず、実行中JobからWorker／Bake枠を取り戻すことや、後着仕事の即時実行は保証しない。Queue満杯では新規投入を成立させず、既存Workの追出し、通常経路の待機、無制限再投入を行わず、呼出側の既存Pending・見送り等へ戻す。

受付済み切断の必須仕事が実Queue満杯で投入不能になった場合だけ、切断Coordinatorは既存Schedule・Job完了・結果回収・適用を通常フレーム予算を超えて同期進行し、一度再投入してよい。進捗のない待機・再投入、PlayerLoop／Commit再入、全切断の一括完了待ちは行わず、次のPhysics Step等が必要なら既存Pendingへ持ち越す。この例外を投機・Maintenance、Capture／Trace、描画やGeometry失敗処理へ広げない。

不要になった未Schedule Workは取消可能とする。Schedule済みJobは中断せず、完了後に8章の世代・前提・authorityで採否し、古い成果物を適用せず回収する。完了回収は低優先仕事の新規Schedule条件によって止めない。4.3の同種処理のBatch化を維持するが、高優先Workを低優先Batchの完成待ちへ依存させない。Maintenanceの新規Scheduleは、上位のReady Workがなく、Worker／Bake枠と当該フレームの残予算に余裕がある場合に限る。投入済みJob／cookによる後着通常切断の遅延は7.9の許容に従う。

Schedule順は実データ依存や公開境界を置き換えない。物理はSource Final Physics、採用面とAnchor入力から進め、後続Geometry Kernelや描画仕上げを待つ依存を作らない。後続Geometry Kernelは直前祖先Geometry Commitを待ち、成果物の公開は4.5.6と各Subsystemの既存境界に従う。採用後の未来用Jobベイクも入力準備から同じDAG・フレーム予算へ含め、候補全件を同期ベイクしてから切断Jobだけを投入しない。

既存Profiler／Traceで、待機、Schedule／完了の進行、長い待ち、Worker／Bake共有枠の負荷、世代・前提不一致による不採用を調査できるようにする。既存TaskId等による因果相関を維持し、Counter列と保存形式は実装詳細とする。切断受付の片側空No-opと混雑見送りは既存CutNoOpCount／CutAdmissionSkippedCountで区別する。

Queue・型・API・tie-break・容量配分・予算Accountingは実装詳細とし、既存実装を改名・置換するための互換層を要求しない。Schedulerの無割当保証は要求せず、実行中のManaged allocationとそれに伴うGC停止を許容する。将来方式は現時点で列挙せず、実測で具体的な不足が確認された場合に別途設計する。

### 4.5 実行時表示Geometryと描画段階

Windows PCVR／Unity 6.3 LTS／URPで、メインスレッドの直列処理と同期待ちを減らすため、通常Mesh表示からVertex Pulling（VP）へ移行する。draw call数の最小化自体を目的にせず、照明はグローバルな並行光源1つ＋ambientとする。表現・変換・メモリ管理・描画発行をPhase 0.9～0.94で先に成立させ、切断・Cap・物理・未来予測は後続Phaseで接続する。

#### 4.5.1 表現・正本・Geometry参照

アセット読み込み時の表示表現はUnity Meshとし、同形状のInstanceはMeshを共有する。即切断と実切断後の表示／StencilはVPを使い、通常命中の同期ベイク・Mesh→VP変換と、未来予測用JobからのVP入力生成は4.5.2に従う。有効な変換済み入力は再利用する。切断生成破片はUnity Meshへ戻さず、スキニング中の通常描画はSkinnedMeshRendererに残す。一時ベイクMeshと元Mesh由来のskinning入力は独立した切断正本ではない。

VPはグローバルVertex／Index Buffer方式、VertexはAoSとする。CPU側VP表現を切断Geometryの正本、GPU側を描画用の実質的コピーとし、Geometry参照とInstanceのTransform／frame写像等を分離する。同じ現在GeometryについてUnity MeshまたはCPU側VPとは別に、並行して更新・切断する権威メッシュを持たない。表示とStencilは一度の切断結果から同じ面集合・属性・Topologyを使う。GPUコピー、Topology Metadata、切断作業構造・Cache、非同期用旧世代、不変の投機Snapshot、未採用VP入力・出力は許容する。元共有Meshも他の未切断Instanceが使う限り保持でき、Physics Convexは独立表現のままとする。

VPのGeometry参照はグローバルIndex範囲等で表し、複数参照による選択描画と、同じGeometryを別Transformで描くInstanceを扱う。幾何処理に必要なTopology情報を保持するが、内部表現をVP共通型として固定せず、通常切断のための全島列挙や島別の恒久ID・物理所有者・描画範囲構築を要求しない。通常切断の出力配置は4.5.6、追加空間分割での範囲内部の面配分は7.9に従う。

Geometry参照、正負集合を表すLogicalFragment、物理所有単位、描画要求を同一視しない。別Topologyであることだけで別LogicalFragment／Rigidbody／drawを要求せず、通常の物理出力と所属は7.2.1／7.6に従う。

AoSの属性集合・strideは各実装時点で固定し、属性追加時に変換・切断属性処理・shaderを更新できる境界を残す。永久固定のlayout、実行中schema変更、複数layout共存、旧Bufferの無停止移行を要求しない。

#### 4.5.2 準備と表示採用

通常命中で即切断開始に必要なSkinned入力は、現在の実Bone Poseを使う同期`SkinnedMeshRenderer.BakeMesh(mesh, useScale: false)`→CPUデータ取得・VP変換を基本経路とする。未来予測用に限り、Phase 4.65で`ResolvedAnimationPoseInput`→19.3の不変Rig Pose→Burst Jobによる線形ブレンドスキニング→共通CPU側VP入力を限定実装し、同期経路との比較から導入の採否を決める。目的は複数候補の頂点処理と同期待ちをMain Threadへ集中させないことであり、ベイク総時間の短縮や負荷ゼロを要求しない。非スキニング対象はベイクを省き、必要な形状・姿勢で準備済みなら再利用する。

Skinned対象の切断前の共通VP入力は、元SkinnedMeshRendererのTransformを基準とするlocal空間とし、Root Bone localやWorld空間を混在させない。同期経路と比較基準のSkinnedMeshRenderer.BakeMeshはともに`useScale: false`へ固定する。Job経路はRoot Boneを含む骨Poseと対応するbindposeをRenderer基準へ変換したskinning変換で骨由来のscaleを反映し、bindposeやRoot Boneのscaleを別途重ね掛けしない。Rendererおよび祖先のobject scaleは、VPからWorldへの既存Transform／frame写像で一度だけ適用し、VP頂点へ追加で焼き込まない。切断面も同じ入力空間へ写し、切断後の物理frameへの配置・表示追従は4.5.6に従う。

Phase 4.65は限定実装・品質と負荷の比較・人間による採否決定までを必須とし、効果がなければ導入見送りを正常な完了結果とする。採用時だけ本体の既存DAG／VPプールへ接続し、以下のJob経路の本体契約、Phase 4.71の未来VP準備、Phase 4.72の人形先行切断統合を適用する。不採用時はこれらを必須範囲から外し、4.70の軌道・Animation計画と4.52の現在Pose同期切断を残す。人形の先行準備による命中時負荷削減を必達にせず、準備費用・表示開始は本節の通常同期経路に従う。採否は開発時の判断であり、実行時の自動切替・再挑戦や代替の未来Pose同期ベイクを追加しない。判断と理由を本書へ記録し、不採用となった本体設計は削除してGit履歴へ残せる。

未来用Jobは元Mesh由来の不変な頂点属性・weight・bindpose・骨対応を読み込み・登録時などに準備して共有し、候補ごとの取出し・再構築を避ける。共通VP用AoSへ直接出力するか、一時Native出力からJob側で同じ形式へ変換する。ベイク済みUnity Meshの生成・再読取りを挟む義務はなく、頂点数に比例する変換・コピーをMain Threadへ戻さない。Job分割、並列度、入力layout、型・field列、初期対応Asset・変形機能は実装と代表入力の比較で選ぶ。

Pose評価と頂点スキニングを分け、現在Sceneを未来Poseへ変更しない。Pose EvaluatorのBackend選択とMain Threadに残る評価費用は19.3に従い、ProbeのTransform収集方式を必須にしない。同じRig Poseから表示側Jobベイクと骨Physics Proxyの姿勢化へ分岐し、必要なCell・frame等の入力依存は維持するが、描画頂点ベイクだけを理由に物理を待たせない。

VP入力準備と実切断出力を区別し、後者の正負直接配置・転送は4.5.6に従う。入力Pose・sourceデータは読者の寿命まで不変とし、Job出力は4.5.3の範囲所有権と既存の世代・回収規則へ接続する。未完了Jobを翌フレーム固定で強制Completeせず、完了した仕事を回収する。ベイク完了をGPU転送・表示Commitの自動トリガーにせず、準備中は現在表示を維持する。

SkinnedMeshRenderer.BakeMeshとのbit単位一致は要求せず、同じ入力Poseと本節のRenderer local／scale規約、有効weight条件で必要属性の誤差と見た目を少数の代表入力で確認する。Qualityによるweight制限の違いや未対応BlendShapeを丸め誤差として扱わず、初期対応範囲を限定できる。Job非対応のweight数・BlendShape・scale構成では未来Job準備を行わず、実命中時に現在Poseの既存同期経路を使う。Job非対応であること自体を異常扱いせず、未来Pose用の同期ベイクや新しい救済経路は追加しない。6.2のcanonical posed position共有、必要属性のfinite性、Topology対応、表示／Stencil／再切断の共用Geometry契約は維持する。参考測定の誤差値を固定上限や実行時の全頂点比較へ転記しない。

実命中時は19章／20.5の既存世代・予測前提・Pose／面条件で準備済み表現・切断成果物を採用する。採用検証のためだけに毎回同期SkinnedMeshRenderer.BakeMeshを追加せず、有効な準備済みVP入力・切断成果物を再利用する。準備が未完成または不採用なら、即時表示に必要な入力は現在Poseの同期経路で準備し、そのために投機Jobを強制Completeしない。後着結果は既存規則で採否・回収し、異なる入力Poseの結果を混用しない。初期は即切断開始に必要なCPUベイク・VP変換・転送を命中を処理するフレーム内に収め、同フレームの描画から分離を見せることを目標とする。未準備なら必要な準備後に表示を開始する。表示開始時に残る準備費用を負担するが、この遅延許容を複数フレーム分割の実装要求にはしない。実測前に全対象の同フレーム表示を保証せず、4.5.4の容量拡張時停止は既存の許容として区別する。幾何切断完了より先に仮表示する原則は維持する。

転送は同フレームの描画が更新データを使える順序で発行し、この目標のためにMain ThreadへGPU完了待ちを追加しない。転送発行時間だけを表示開始時間とみなさず、代表入力の準備費用、実際の表示開始フレーム、フレーム全体の90fps目標との両立を既存計測で確認する。

Meshからデータを取得する初期経路はCPU-readableを前提とし、AcquireReadOnlyMeshData等で不要な取出しコピーを抑える。このAPIはAoS変換・GPU転送まで省略せず、Snapshot保持中の元Mesh変更ではコピーが発生し得る。SkinnedMeshRenderer.BakeMesh自体は同期CPU処理であり、自動的な背景Jobとみなさない。その結果は通常Mesh入力として変換へ渡せるが、未来用Job出力にMesh経由を要求しない。0.92だけのためにPose Evaluatorや人形切断を完成させない。

#### 4.5.3 CPUプール・範囲所有権

CPUのVertex／Indexはそれぞれ単一の大きな線形領域とし、Jobへ全域のNativeArray viewを渡す。viewの範囲と実際のアクセス許可範囲を分け、切断・未来用ベイクJobのVPプール対象フィールドにNativeDisableContainerSafetyRestrictionを使用する。container単位の粗い依存判定に代わり、メインスレッド管理の専用アロケータが入力参照寿命と出力予約を所有する。

| 範囲の区分 | 許可するアクセス |
| --- | --- |
| Free | 割当可能 |
| Reserved | 所有Jobだけが読み書きする出力予約。他Job・転送処理は触れない |
| Published | 書込み完了後に公開した読取り専用範囲。複数読者が参照可能 |

VPプールへのJobアクセスは内部Published入力と自分のReserved出力に限る。同一Geometry Work内部の段階間所有権移管はJob完了回収後に行い、共有書込みを許可しない。実使用部分を内部Publishedにし、未使用予約を解除する。切断間の入力は直前祖先のCommitted Geometryに限り、未Commit成果物を後続切断へ公開しない。Publishedはアロケータ内部の読取り許可であり、Geometry Commit・GPU転送・Gameplay／Trace状態を表さない。安全属性の解除でJob完了や実データ依存を省略しない。

数値Kernelの入出力は6.1に従う。新規Vertexは一つの連続予約の先頭から実使用部分を詰めて出力し、継承Vertexは既存番号を参照する。新規Indexは4.5.6の一つの連続予約へ正側、負側の順で出力する。属性seamやCapのために必要な新規Render Vertexはこの新規Vertex出力へ含める。表示切断に厳密Count→確保→Writeを必須にせず、出力予約不足では範囲外へ書く前に容量不足で終了し、部分成果物を公開せず再予約・再実行できる。7.9の新Index領域も同じ予約規則を使う。形状不正・世代不一致の救済や切断受付の再試行へ広げず、範囲外書込み後の例外回復を設けない。

Published Vertexは上書き・再利用せず、子から継承参照する。移動はInstance Transform／frame写像で扱い、継承のためだけに全頂点を複製・書換えしない。CPU Index rangeは旧GeometryのCPU正本参照とCPU／Job／転送元読者がなくなれば再利用できる。CPU再利用自体はGPU Bufferを変更せず、GPU完了待ちをCPU解放条件にしない。対応GPU offset更新と旧描画利用とのhazardはVP転送／Renderer内部で処理する。成果物別GPU部分rangeのallocate／freeを設けず、Geometry参照とRenderer登録は一度だけ退役する。旧GPU Buffer全体の解放は4.5.4に従う。

0.93の再利用対象は主に退役Indexと、Vertex／Indexの未使用・失敗予約であり、未公開予約の回収はVertex追記方針に反しない。単純な空き領域のbest-effort再利用でよく、最適配置、断片化解消、コンパクションを要求しない。表示Instance／Geometry参照の退役は物理GCとは別であり、0.93へ7.9の生存物体終了を前倒ししない。

#### 4.5.4 CPU仮想予約・GPU容量と転送

本節のPlayer終了対象は、初期化または受付済みの有効な仕事に必要なCPU／GPUのbacking容量を、設定済み絶対上限超過、VirtualAlloc／page commit失敗、GPU／device／API上限超過または確保失敗により成立させられない場合に限る。上限に達した事実だけでは終了しない。Queue満杯は既存の受付・待機規則、per-cut出力範囲の予約不足は4.5.3の非公開終了・再予約・再実行に従う。

CPUはWindows x64のVirtualAllocで大きな仮想アドレス領域を予約し、利用前に必要ページをcommitする方式を基本案とする。予約内では基底アドレスを動かさない。16 GiB等は仮想予約量の例であり、既定容量・初期物理使用量・必須試験規模にしない。仮想予約成功は後続page commit成功を保証しない。本節のCPU backing容量不成立は4章の共通Player終了に従う。

頂点番号は32bitとし、全VBはNativeArray<Vertex>、全IBはNativeArray<uint>のviewを使える。各viewの要素数はint.MaxValue以下、byte数・offset演算は64bitとし、要素数とbyte上限を混同しない。32bit頂点番号を理由にVBを4 GiBへ制限しない。外部所有領域のviewにはConvertExistingDataToNativeArrayとAllocator.None等を使い、分割プールや64bit頂点番号を要求しない。

GPUはPhase 0.92で採用する固定設定の初期容量を確保し、不足時に大きなBufferを作り必要データを移す。新内容を利用できる準備・依存順序を整えてから描画境界で参照を切り替える。旧Bufferを使う描画登録を更新または退役し、旧Bufferを参照する投入済みGPU処理の終了後に一度だけ解放する。Fence方式や新しい状態体系は固定せず、通常更新でのGPU完了待ちを要求しない。この再確保・コピー・切替に伴うSTWを許容し、本節のGPU backing容量不成立は4章の共通Player終了に従う。GPU単一Buffer上限と新旧Bufferの一時共存を考慮し、CPU固定予約とGPU再確保を混同せず、GPUが必ず先に尽きると仮定しない。無停止回復・縮退描画を追加しない。

実切断出力の転送時期と未転送の継承Vertexは4.5.6に従う。転送は追加・変更範囲を対象とし、安全にまとめられる更新をまとめてSetData回数を減らす。別JobのReserved範囲や未commit CPUページをまとめ読みせず、CPU／GPU offset対応を保つ。スパース配置自体を失敗にしない。Graphics.CopyBufferは元・先の総byte数一致を要求するため、そのまま容量拡張に使わない。初期はSTW中のCPU正本からの必要範囲再転送、または単純なGPU範囲コピーを選び、両方のFallback実装を要求しない。

停止・終了の許容は本プールの容量処理に限定し、通常Mesh／VP混在のPass境界へメインスレッドからのGPU完了待ちを追加しない。使用中領域・参照の保護は維持し、巨大stressではなく小容量と少数Jobで拡張・不足・再利用を確認する。

#### 4.5.5 描画Stageと採用判断

通常MeshとVPを同じURP描画へ参加させ、必要なColor／Depth／Shadow順序とリソース依存を設定する。頂点取得・描画命令生成を切断Kernelから分離し、切断Clip／Stencilの接続はPhase 1以降で行う。

| Stage | 構成 | 目的・導入 |
| --- | --- | --- |
| 1 | Direct・非indexed draw＋shader-side indexing＋属性Pulling | 基本表示・Meshからの移行。Phase 0.92。DirectはMeshRendererを意味しない |
| 2 | Indirect・非indexed draw＋shader-side indexing＋属性Pulling | 描画要求集約・引数管理・個別発行を見直す。Phase 0.94でPhase 1前に実装・比較する |
| 3 | Indexed Indirect＋属性Pulling | hardware vertex reuse。実測に応じた後段最適化 |

Stage 2はAPI置換だけで改善とみなさず、実際の発行経路と主スレッドへの効果を測る。全異種Geometryの単一native draw化、頂点再利用・ベイク・転送の改善をStage 2へ要求しない。固定改善率・全Scene高速化をPhase 1着手条件にせず、効果が乏しければ人間判断でStage 1を採用したまま進める。実行時自動Fallbackは設けない。GRDは通常Mesh側の比較候補でありForward+を必要とするが、独自VP描画の集約機構とは同一視しない。

GPUカリングの責務は独自描画側に置くが、0.9xで高度な遮蔽判定・全面GPU生成を要求しない。GPU並行ベイク、Stage 3、GRD採用、コンパクション、layout汎用化、追加権威Meshを初期完成条件にしない。将来のBuffer用途選定ではD3D11のIndex＋Structured併用不可等を確認し、Index＋Raw等を検討できるが、AoS方針や初期layoutを変更する前提にはしない。

物体固有Motion Vector、XR提出・再投影の新機能、Quest単体Application SpaceWarpを0.9xへ追加しない。仕様・既存実装への必要な破壊的変更と進行中Phase 0.11／別ブランチPhase 0.2への波及は許容するが、Capture、Fixture生成、物理、支持・世代管理の再設計を目的にしない。VP導入だけを理由にFixture形式の作り直しや旧Asset／Fixtureの一括再生成を要求しない。

#### 4.5.6 正負Indexの直接配置と転送・Commit

通常切断の新規Indexは、一つの連続予約の先頭から正側の全Index、続いて負側の全Indexを隙間なく出力する。各側の生成Capも対応する列へ含める。正負別のallocationや、物理所有者別に後から再集約する工程を設けない。

正負の実使用Index数をn0、n1、予約先頭をbaseとすると、正側は`[base, base+n0)`、負側は`[base+n0, base+n0+n1)`を参照する。片側Geometryが空ならその側の要素数は0とし、paddingやダミーIndexを置かない。末尾の未使用予約は初期化・転送せず既存アロケータで解除する。負側開始位置に必要なn0は切断内の件数計算や出力順序で決め、別Count JobとMain Thread往復を必須にしない。切断・Cap生成の作業領域と予約不足時の4.5.3の扱いは維持する。

正負のGeometryは同SideのFinal Physics Ownerへ対応付ける。Material・frame等に必要な描画分割はRenderer側で扱い、連続配置や1回の転送を全Passでの厳密1 drawと同一視しない。

継承Vertexは既存番号を使い、新規交点・Cap頂点等だけを追記する。Triangle切断、正負への振り分け、必要な非交差IndexコピーとTopology参照更新は通常の出力生成に含める。面集合、Triangle内頂点順、属性、windingを保存し、表示・Stencil・再切断は同じIndex正本を使用する。

新規Index列は実使用範囲`[base, base+n0+n1)`だけを一度のSetDataでGPUへ送る。正負別の2回転送、未使用予約の転送、中間配置の先行転送と後の再転送を行わない。対象全体のGeometryが無変更で既存GPU Index範囲をそのまま再利用できる場合は、新規Index書込み・当該切断のIndex SetDataは0回とする。受付前No-opもこれに含む。Geometryが変わる場合や、Triangle非交差でも正負配分に新Indexが必要な場合は省略しない。

この1回／0回は通常切断の当該新規Index出力だけの転送回数である。Vertex、Descriptor、初回Mesh→VP準備、GPU容量拡張時の再転送は別とする。先行準備で転送済みの有効な出力は再利用し、命中だけを理由に再転送しない。未転送入力範囲の継承時に必要な転送は、GPU転送済み範囲の再利用による0回と区別する。

表示Geometryの外部状態変更境界はGeometry Commitだけとする。Geometry Workは共用Geometryと描画方式に依存しない参照・Topology Metadataを生成し、Color／Depth／ShadowCaster／Stencil／Culling／Indirect描画のDescriptor、Draw List、Batch、Indirect Argumentsを出力・成功条件・Commit条件に含めない。Job完了、CPU Published、GPU更新の準備・発行・完了は内部処理であり、別の公開Ready／Upload状態を作らない。

Geometry Workは受付時に登録できるが、同Branchの直前祖先Geometry Commit後のCommitted Geometryを入力としてKernelを実行する。初回は登録済み基底Geometryを使う。Geometryが内部で先に完成しても、7.1のFinal Physics／Logical Publication前にはCommitしない。出力予約不足だけは4.5.3の非公開再予約・再実行を使い、後続Physics／Logical Publicationを待たせる条件にはしない。

Geometry Commitでは正負の具体的Geometry／RenderFragment参照、frame、実在CutBoundaryRecord、実体化済みCutと残るTemporary集合を一つの整合した状態として公開し、当該GeometryStateをCommittedとする。当該CutのTemporary Clip／Capだけを外し、後続面を新基底へ付け替え、Pending CutのGeometry責務と未完了件数を一度だけ完了する。Surface BoundaryがなければRecordは0件、Geometry空SideにはRendererもダミーGeometryも作らずFinal Physics子を残す。

RendererはCommit済みGeometryとTemporary集合から経路固有Descriptorを構築し、一つの描画状態Snapshot内の全Passで同じGeometry・面集合・frameを使う。CommitはDraw List生成、Render Thread命令発行、GPU転送／Draw完了、XR提出、実画面表示、次フレームを待たない。必要なGPU更新が新Geometryを読むDrawより先に実行される順序をVP転送／Rendererが保証する。収集前のCommitは当該収集、収集後のCommitは次回収集から使い、表示済み証明は要求しない。

Cut AのGeometry未完了中にA子のCut Bが物理・論理公開済みでも、Aを先にCommitして`G0 + Temporary A + Temporary B -> GA + Temporary B -> GAB`へ進む。A子が既に置換済みならGAを現在のB子孫の表示基底へ対応付け、A子をliveへ戻さない。B KernelはA Commit後に実行する。同じPlayer LoopでA Commit後にBを完了・Commitできれば続けてよく、中間GAを一度描く義務はない。中間Commitの省略、子孫による祖先の代替materialize、Geometry Transactionを作らない。

A正側だけがBで再切断されていれば、A負側はGA-へ、B枝はGA+とTemporary Bへ進む。BがAbortしてその枝が退役済みならA負側だけを表示へCommitする。全子孫が退役し、表示・後続Geometry Work・他の生存読者に不要なら履歴完成だけの計算を続けずPendingを終端・回収する。未完成Operation Traceは21章の既存Incomplete扱いとする。履歴Recordは置換済み中間Geometryを強く所有しない。

採否は8章のPending／Branch authorityで照合し、別Fragmentの世代更新や子孫切断だけで祖先Geometryを失効させない。共用Geometryの予期しない内部エラーは4章の共通Player終了へ送り、後続へ代替Geometryを推測適用しない。

Phase 5.6の追加分割で必要になる面の配分、新Index領域への振り分けコピー・転送・旧範囲退役は7.9へ置く。通常切断へ全島列挙を前倒ししない。配置・転送回数は既存計測で確認し、新しいRuntime監視、品質Gate、数値SLAを追加しない。

## 5. 即時表示レンダラ

即切断と実切断後の表示／Stencilは4.5の同じVP Geometryを使用する。本章の即時表示は必要なベイク・VP変換後に開始し、有効な先行準備を再利用する。実切断CPU結果完成後も4.5.6のGeometry Commitまでは同じ規則で継続する。Cap単位compaction／部分更新の禁止はStencil描画最適化の制限であり、グローバルプールの必要な更新・拡張を禁止しない。

### 5.1 分離表示

元メッシュを論理破片ごとに描画し、各切断面の正負符号に応じてフラグメントをclipする。論理上の切断幅（Kerf）は0とし、自由破片が相対移動した結果としてのみ隙間と断面が見える。単一切断ではGeometryが存在するSideごとに最大1の論理的な描画インスタンスを持ち、両側に存在すれば2、片側だけなら1とする。Geometry未確定の間は既存の親Geometryを正負にclipして仮表示し、空判定の完了を表示開始条件にしない。確定空のSideにはダミーRendererを作らず、不要な仮表示を7.6に従って退役する。自由側へは必要最小限の仮分離Offsetを与える。複数切断では、論理破片が保持する半空間の組み合わせだけを描画する。

破片の表示オフセットはスキニング後またはワールド変換後に加える。スキニング前に加えると、ボーン姿勢によって分離方向が歪むため避ける。

固定Anchorの有無を仮描画の省略条件にしない。両側固定でもclip、仮Cap、実切断・Cap生成を通常どおり行い、固定側のOffset／Impulseは0とする。最新世代や両側Anchorを理由とする簡易な支持Cullも設けない。境界の細い亀裂、輪郭線、局所的な線状Z-fighting、軽微なチラツキを薄い切断痕として許容するが、通常の正向き閉Shellの欠落や任意の面状Z-fightingへ拡張しない。

### 5.2 仮断面とステンシル

Stencilは仮断面キャップのマスク生成に使い、表示と同じ6章の共用Geometryを参照する。TemporaryRenderCapRecordSetは、Operation公開前の受付済みPending Cutと、公開後のGeometry未Commitの切断面・Sideから導出する。公開時は同じ採用面のRecordへ重複なく引き継ぎ、固定／動的による選別をしない。各Recordの共用Geometry表裏から符号付きWindingを記録し、ローカルOBBと切断面の交差から作る有限なCap Bounds Polygonを正のWinding領域だけ描く。Geometryの向きの反転、正規化、符号一様性証明を行わない。

- Clip Plane：物体を正負に分け、隙間の空いた分離表示を作る。D3D11の初期実装ではRasterizerの`SV_ClipDistance`を優先し、固定上限を超えた少数だけをPixel Shaderの`clip()`で補う。

- Stencil：切断平面上で元物体内部に相当する範囲をマスクし、仮断面を塗る。

- 実断面Mesh：バックグラウンド処理完了後に仮断面を置換する。

即時Rendererが当該RenderFragmentへ適用するGeometry未Commitの切断面をTemporaryClipConstraintCandidateSetとする。Operation公開前は受付済みPending Cut、公開後は当該Fragmentに関係する未Commit切断履歴から導出し、祖先半空間を維持する。Cap Record集合とは役割を分け、支持による候補除外は行わない。

候補は既存のPending Cut列とLogicalCutOperation公開列を受付の古い順に走査し、未Commit祖先制約を子孫制約より必ず先に置くstable順で選ぶ。Pending CutからOperation由来Recordへ移る際も同じ受付位置を保ち、同じ切断面を重複登録しない。選択結果は候補列の先頭から最大12面のdependency-closed prefixとし、ある子孫境界を選ぶために必要な未Commit祖先境界が選択外なら、その子孫も選ばない。通常の公開処理は祖先を子孫より先に列へ追加する不変条件を持ち、復元データがこの順序を満たさない場合は新しい順へ並べ替えず、違反境界以降をIgnoredとして背景Geometry完成へ委ねる。ID値によるsortや別の優先度Metadataを追加せず、左右眼、Color、Depth、ShadowCaster、Stencil Volumeの全Passで同じ選択結果を共有する。カメラ距離、眼、Pass、毎フレームの可視性で順序を変えない。候補追加、LogicalCutOperation公開、Geometry Commit、RenderFragmentとCutBoundaryの対応関係変更のいずれかが候補資格または依存関係を変えた場合、状態変更を公開する同じ描画更新境界で再構築する。

D3D11／Shader Model 5のPoC Profileは`RasterClipPlaneCapacity = 8`、`PixelClipPlaneCapacity = 4`、`TemporaryClipPlaneCapacity = 12`を初期値とする。`SV_ClipDistance`と`SV_CullDistance`の合計component上限8をRaster側の正本とし、このShader Variantでは`SV_CullDistance`を使用しない。先頭8面をVertex Shaderから`SV_ClipDistance0/1`の合計8 componentへ出力し、未使用componentは全頂点で正の有限値へ固定する。続く最大4面だけを固定長per-instance配列と`PixelClipCount`からPixel Shaderの`clip()`で評価する。面数や平面値によるMaterial、Keyword、Pass、Draw分割、可変長Buffer、動的Loop上限の増加を行わない。MSAA時は先頭8面のRasterizer clippingによるcoverageを正本とし、Pixel fallback境界との微小なedge品質差は短時間の品質低下として許容する。

dependency-closed prefixへ入らない後発Pending Cut／境界は`IgnoredTemporaryClipBoundarySet`とし、`SelectedTemporaryClipPlaneSet`へ含めず、Color／Depth／Shadowのclip制約として使わず、対応Stencil Volumeをsubmitしない。Cap板Recordは既存Batchへ残り得る。Pending Cutまたは公開済みLogicalCutOperation、Geometry状態、Logical Fragment、切断履歴、世代、点Anchor配分と所有者単位の固定／動的、共用Geometry／Convex Job、物理Commit、優先度付けは変更・破棄せず、背景Geometry Commitで正しい形状へ収束させる。無視された新しい面は一時的に即時表示されず、影もその面より前の形状となり得るが、選択済み祖先の外側にGeometryを復活させずSiblingを重ねないbounded degradationとする。Plane overflowを理由に既存Jobをcancel、再発行、同期完了してはならない。

`TemporaryRenderCapRecordSet`、Cap Bounds Polygon、StencilのColor割当て、Cap板側のDraw ListはPlane overflow時にもcompaction／部分更新しない。Stencil Volumeは既存の可視性・Color分類に従い、`SelectedTemporaryClipPlaneSet`に選ばれた境界に対応するものだけをsubmitする。Cap板RecordはIgnored境界のものも従来Batchへ残してよい。対応Volumeも別RecordのResidual Stencilもないsampleは初期値128のままなので板はColor／Depthを書かない。通常Colorでは別RecordのResidual Stencilが到達し得る非互換Capを分離し、最後の統合Colorでは混入、欠落、余計なCapおよび誤ったDepthを許容する。Ignored Capを隠すための追加Mesh生成、有効フラグ、Buffer compaction、個別Draw除去、代替VFXは追加しない。

- Cap Bounds PolygonはOBBの12辺と切断平面を交差させ、epsilonで重複を除いた3～6頂点を平面上で並べて生成する。複数のTemporary Render Boundaryでは、SelectedTemporaryClipPlaneSet内のほかの面が定める論理破片の半空間で凸多角形clipする。未受付・Ignoredの面をこの即時描画用clip集合へ含めない。

- Cap Bounds Polygonは物体表面との正確な交差輪郭ではないため、最終的な凹形状、穴、部品輪郭はStencilで制限する。実表面との輪郭を三角形化できた場合は実断面Meshとして扱い、Stencilへ重複して依存しない。

- 固定側も実断面を生成・公開し、正負で逆向きの法線を持つ共通の片面トゥーンMaterialを基本とする。Cull Offの両面描画は通常カラーPassで常用しない。固定側のCapに生じる線状切断痕は許容するが、正常な正向き閉Shellで明示的例外に該当しない画面規模の面状Z-fighting、可視Cap欠落、Cap Bounds外または非互換対象へのStencil混入は不具合とする。

> **共用Geometry入力契約** 表示とStencil Volumeは6章の閉鎖・edge／vertex manifold・局所winding整合済みTopologyを同じ入力契約として使用する。入力準備時に一度だけ合否を確定し、描画側で再検証しない。自己交差、別Topology Component間の交差／重複、Internal／Nested Shellおよび全体反転は許容するが、Boundary Edge、3面以上の共有Edge、複数fanを共有するTopology Vertex、局所winding不整合は切断可能Geometryとして受理しない。

Temporary Stencil Capは次の品質例外を持つ。これらを検出、証明、修復する新しいMetadata、監視、再描画、別Cap方式、同期Mesh完成待ち、Job取消／再発行は追加しない。

1. 全体反転した閉Componentの仮Capは描画されなくてよい。
2. 共通入力契約に反する局所的な向き反転は切断可能Geometryとして登録しない。
3. 別Topology Componentの正負交差、重複、Coincident、Nested構成では符号相殺により仮Capが欠落してよい。
4. Self-intersectionのうちWindingが0以下となる領域は描画されなくてよい。
5. Internal、Nested、Duplicate、Coincident等の向き構成により被覆が減ってよい。
6. 正規化されていない向き構成では、幾何学的な内部すべての描画を要求しない。
7. 最終Windingが`-127..127`の外へ出た場合は、8bit wrapによる欠落または余計なCapを許容する。範囲内証明、検出、監視、補正、専用Profileおよび意図的な範囲外stress試験を要求しない。
8. `MaxStencilColors`の最後の統合Colorでは、混入、欠落、重複、誤ったDepth／Occlusion、Ignored Cap板の可視化を許容する。
9. Camera内部またはNear Plane近傍では、部分Cap、欠落、余計なCap、内部面、Stencil混入、左右眼差を許容する。専用検出を要求せず、隠されない表示をD-131に従って許容する。

注1、3～6は契約内の向き構成、注2は入力拒否、注7はカウント範囲外、注8はColor統合、注9はCamera近傍だけの取扱いである。共用Geometry契約に反する入力を通常経路へ投入する許可や、通常の正向き閉Shellでの欠落・混入の一般許容へ拡張しない。仮Capの品質例外は共用Geometryの生成、検証、Commit契約を緩和しない。

### 5.3 断面色と最小デバッグ表示

通常表示では、即時仮断面とGeometry Commit済みの実Capを、共通トゥーンシェーダーの固定`CutSurfaceColor`（低彩度グレー）で描画する。陰影段数、輪郭、ライト応答を揃え、断面専用Texture、Texture atlas領域、Texture Mapping用UV展開、特殊陰影、色選択用Material／submeshを追加しない。

実Capの新規Render Vertexには生成時に`uv0 = (-0.5, 0)`を設定する。ShaderはMaterialのUV Transformと通常Texture Sampleより前にraw `uv0.x < 0`を判定し、負の場合は通常Textureを使わず断面色をBase Colorとして共通トゥーン陰影へ渡す。marker成分の格納形式は負値を保持できるものとする。専用Vertex field、Vertex Color、別UV channel、複数の負UV帯域を断面識別へ追加しない。

元Assetの負UVによって元surfaceが断面色と誤認され、通常表示ではTextureが出ずグレー、デバッグ表示では緑になることを許容する。Triangle内でUVの符号が混在する場合の部分的な誤表示も許容する。このためのUV検査・修正・登録拒否・代替markerを要求しない。UV markerは表示色の選択だけに使い、Topology、切断、物理、Hitの判定には使わない。

デバッグ表示は全対象共通の有効／無効だけとし、描画する断面ごとに次を適用する。

| 描画対象 | 通常表示 | デバッグ表示 |
| --- | --- | --- |
| `TemporaryRenderCapRecordSet`から描く即時仮断面 | 固定グレー | 赤 |
| Geometry Commit済みの共用Geometryに含まれる実Cap | 固定グレー | 緑 |

Geometry Commit前は仮断面、Commit後は計算経路によらず実断面とする。デバッグ有効時は再切断中も既存実Capは緑、新しい仮Capは赤として共存できる。Stable移行による色解除・色保持モードは設けない。

色値とデバッグ有効状態はGlobal Shader Constantまたは既存のDraw／Fragment Descriptorで指定する。色変更だけを目的とする既存Vertex／Indexの書換え、VB／IB再生成・SetData、Geometry複製は行わない。切断成果物自体の生成・転送・Commitは従来どおり行う。

計算経路、待機、Reject／Fallback理由、Physics状態の詳細は既存Trace／Profilerで確認し、断面色へ割り当てない。断面専用の点滅・縞・縁取り、常時テキスト、固定パネル、色覚補助表示、公開schema・状態機械を追加しない。Exact RGB、色遷移Animation、表示保持時間は固定しない。

### 5.4 即時切断中のShadow Map

影はRealtime Shadow Mapを使用する。即時切断中の論理破片はShadowCaster PassでもカラーPassと同じper-instance切断平面、論理破片Side、分離Offsetを適用する。一方、Shadow Mapには色付き断面を描く必要がないため、Stencilによる仮断面キャップは生成せず、ShadowCasterだけを両面描画して開口の奥にある外殻裏面を遮蔽面として使用する。

この方式は、閉じた元形状に対する外部Shadowの被覆範囲を低コストで近似するが、本来は切断面キャップが書くはずの深度より奥側の外殻深度がShadow Mapへ入る場合がある。切断面が床／壁に近い場合、薄い物体、非閉形状、Self Shadow、Shadow Bias、Cascade境界等では接地影の浮きや漏れが発生し得る。即時状態の短時間近似として許容し、4.5.6のGeometry Commit後は実断面を含む閉形状と片面ShadowCasterへ戻す。

- `Cull`はper-instance属性ではなく描画状態として扱い、Shadow描画を原則としてStable片面群の`Cull Back`とPending両面群の`Cull Off`へ分ける。UnityのRenderer経路では`ShadowCastingMode.On`／`TwoSided`、専用Renderer経路では対応するShadowCaster Variantを使用する。

- 「2回」はShadow Map全体が必ず2 Draw Callだけになる意味ではない。Light、Cascade／Shadow Map Slice、Mesh、Material、Shader VariantなどのBatch単位ごとに、少なくとも片面群と両面群へ分かれるという意味とする。

- 切断平面は5.2の固定上限Instance Recordに`RasterClipPlaneCount`、`RasterClipPlanes[8]`、`PixelClipPlaneCount`、`PixelClipPlanes[4]`、各面へ反映済みのFragment Side、`SeparationOffset`として保持する。ShadowCasterもColor Passと同一のstable選択結果を使い、先頭8面は`SV_ClipDistance`、続く最大4面はPixel Shader `clip()`、超過面は即時Shadowから無視する。切断数や平面値でMaterial、Shader Keyword、Passを増やさず、同じCull群のBatchを維持する。

- Stable Instanceをclip対応Shadow Shaderへ統合するか、`RasterClipPlaneCount = 0 && PixelClipPlaneCount = 0`専用の高速経路へ分けるかは実測で決める。全ShadowCasterを常時`Cull Off`にしてDraw群を統合する案は、裏面Raster／overdraw増加を測定せず採用しない。

### 5.5 コスト制御

- 同一物体の`TemporaryRenderCapRecordSet`件数は、固定側とBatchに残るIgnored板を含むBatch投入Cap Record 2～4枚を通常時の品質／費用上の目安とする。Geometry Commit済みRecordは数えない。この目安はRuntime制御を発生させず、各切断は通常の非同期生成・Commitに従う。対応Recordの回収は4.5.6に従う。

- `TemporaryClipPlaneCapacity = 12`はClip Plane評価数の上限とする。1枚のCap Polygonまたは1個のRenderFragmentが複数の未Commit半空間制約を受けるため、Cap Record件数とClip Plane件数は同義ではない。超過時もClip Plane評価はRaster 8面、Pixel 4面、残り無視とする。

- `RasterClipPlaneCount`、`PixelClipPlaneCount`、`IgnoredTemporaryClipBoundaryCount`をProfiler Counterと既存の選択対象診断へ出す。断面専用パネルは要求しない。Plane overflowによってGeometry Jobの優先度、依存関係、cancel／再発行規則を変更せず、Frame内の待機や同期Commitを禁止する。

- Stencilは切断面ごとの一時作業領域として再利用し、恒久的なビット割当は行わない。

### 5.6 スクリーンスペースStencil Batch

Stencil Bufferは画面座標ごとに共有されるため、すべての即時切断物体を無条件に同じ通常Colorへ蓄積しない。現在の全World Cut Plane、各PlaneのFragment Side／半空間、分離Offsetが一致する対象を`CapCompatibilityKey`で同じ互換Groupへまとめる。可視色は5.3で共通とし、色違いによるGroup分割を行わない。6章の共通入力Gateに合格したGeometryは正、負、混合符号を分離条件にせず、入力Geometryの向きを保存して符号付き加算する。互換Groupの意味は厳密な幾何Unionではなく`sum(W_i) > 0`であり、各寄与が正しく得られて最終値が符号判定範囲内なら`{sum(W_i) > 0} ⊆ union_i {W_i > 0}`となる。正逆相殺やColor分割によるMask差は5.2の品質例外へ限定する。

StencilはParityの`Invert`や飽和演算ではなく、共通入力Gateに合格したGeometryのFront／Back Faceに対する`IncrementWrap／DecrementWrap`からなる8bit Winding Count方式を使う。基準値を`B = 128`、符号付きWindingを`W`、格納値を`S = (B + W) mod 256`とし、各Color開始時とColor間で専用Stencil Byteの全8bitを128へ初期化する。Rect描画で初期化する場合は`Ref 128`と`Replace`を使う。Capは`S > 128`だけを描画し、Unity ShaderLabでは`Ref 128 / Comp Less / ReadMask 255`とする。Counterは`ReadMask 255 / WriteMask 255`、Cap側のStencil書込みは無効とし、`S <= 128`のsampleはColorもDepthも書かない。裏面透明という通常Color方針をStencil集計からBack Faceを除外する意味にせず、Front／Back双方を対称なclip／Depth条件で扱う。Rasterizer／Transform補正は通常Colorのfront-face規約と一致させ、Geometryの向きをPositiveへ直す補正や二重補正を行わない。

最終Windingが`-127 <= W <= 127`なら`S > 128`は`W > 0`と一致するが、この範囲の証明、検査、監視、補正は行わない。途中のWrapは正常な演算として扱い、Mesh数、Triangle数、Draw数、途中累積値の上限として解釈しない。Count容量によるBatch分割、Fallback、別形式Counterを設けず、範囲外でも同じ8bit演算と比較を続け、結果は5.2の品質例外とする。

`Residual Stencil Support`は集計後に`S != 128`となる画面領域を表す論理概念であり、実Stencilから検出・再構成・監視する対象ではない。負残留も別Recordの正寄与を打ち消し得るため、通常Colorの競合判定から除外しない。通常Colorでは既存の物体OBBと可視Cap Boundsの左右眼投影をResidual Supportの保守的な重複判定に使い、非互換対象を別Colorへ分離する。Geometry入力条件は5.7で上流が確定し、描画ごとに再検証しない。

`Stencil Conflict Graph`は分離が必要な対象間の関係を表す論理モデルに限る。全Graphの構築・保存、全組合せ走査、stable順、Greedy Coloring、最小彩色、異なる方式で同じColor番号を得ることを要求しない。通常Colorの左右眼分離条件、Color上限、各Color内の全Volume後に全Capを描く順序を満たす範囲で、候補検索、データ構造、Color割当て方式を実測から選ぶ。

描画するColor数は固定上限`MaxStencilColors >= 1`で制限する。先頭の最大`MaxStencilColors - 1`個を通常Colorとし、配置できない対象は最後のColorへ直接まとめる。最後のColorでは非互換対象の分離を要求せず、5.2の品質例外を適用する。`MaxStencilColors = 1`では全対象を最後のColorへ入れる。上限外Colorの生成、遠距離／小画面Capの省略、代替VFX、Job優先度変更、再彩色による救済を要求しない。GPU時間はProfiler等で測定・調整する性能目標であり、厳密な実行時上限、監視制御、多段Fallbackは設けない。

- Broadphaseでは分離Offsetと安全Marginを含む物体OBBの左右眼投影矩形を使う。重なる組だけ、表向きのOBB切断面から得たCap Bounds Polygonを左右眼へ投影して再判定する。どちらの判定も非交差なら安全という悲観的な証明として扱い、Near Plane交差、Raster／MSAA、頭部移動誤差を考慮してBoundsを保守的に拡張する。

- `CapCompatibilityKey`は順序を正規化した表示対象`CutPlaneId`列、Side Mask、Offsetから作り、Raw floatだけをHashの正本にしない。同じSlash由来でも、19.5.1の対象別リベースまたは対象の移動・回転によってWorld Planeが異なり得るため、現在の操作World Planeをepsilon比較し、一致しなければ別Groupへ分離する。片方だけに追加Temporary Render Boundaryがある場合も互換ではない。符号分類とWinding容量はKeyへ含めない。

- `LogicalCutOperation`公開前の即時Capは、親`RenderFragment × Pending Cutの採用面`から導出し、確定した論理子や境界を先取りしない。公開後のキャップの幾何可視性は元Object単位ではなく、`論理破片 × 切断面`の`CapRecord`単位で判定する。同じ切断面でも正負破片の断面Normalは逆向きになるため、片側が裏向きでも反対側を自動的に省略しない。支持による描画省略を行わない。

- LogicalCutOperationは7.1.2のFinal Physicsと同じ境界で、親、正負2子、採用面とSide対応を一度だけ公開する。具体的GeometryとCutBoundaryRecordを待たず、各SideのGeometryと実Capは4.5.6のGeometry Commitで後着する。空のGeometryを補うダミーRendererは作らない。

- DirectChildCountは2とし、正負各1とする。CutBoundaryCountは実在Boundaryの件数と一致させ、0件も正常とする。IDは0を予約した正の32bit intとし、CutOperationId／LogicalFragmentLocalId／CutBoundaryLocalIdはObjectIdの寿命中に種別ごとに非再利用とする。ParentObjectGenerationはuint全域とし入力Snapshotと一致させる。親・子・境界のIDは種別内で重複なく構成し、世代・参照は既存公開条件で照合する。構築中に表面化した予期しない内部エラーは4章に従う。

- Boundary件数の共通上限256は撤去する。256を超える場合の処理量・メモリ使用の増加は人間承認済みとし、無制限の収容や同一時間での完了は保証しない。有限容量・範囲チェック・checked演算と既存の容量／内部エラー境界を維持し、容量値と格納形状は実装詳細とする。Boundary専用の上限Profileや救済経路は追加しない。

- CutBoundaryRecordはGeometry Commitで確定する採用面・Side・子Geometry／frame参照を表す。複数Contour／Capを一つのLoopへ潰さず、Geometryが空のSideには架空参照を作らない。境界0件も正常であり、子数は変更しない。履歴は退役Geometry／Actorを強く所有しない。

- `TemporaryRenderCapRecordSet`内の現在のWorld Cap Planeについて、各眼の値を`d = dot(CapNormal, EyePosition - CapPoint)`とし、`dLeft < -FacingEpsilon && dRight < -FacingEpsilon`の場合だけCapRecordをFacingで除外する。等号とepsilon帯を含むその他の場合はSingle Pass Instanced用Recordを残す。Frustum外判定も同じ段階で行い、互換Group内に幾何可視な実描画CapがなければStencil Clear／Volume／Cap処理を丸ごと省略する。判定は現在フレームだけから行い、過去の可視状態を保持しない。epsilon境界の往復による投入切替と頭部微動時の点滅を許容する。通常のclip済み破片カラー描画とShadowCasterは消さない。

- Cap処理は`受付済み未Commit面・SideからRecord構築 -> TemporaryClipConstraintCandidateSetのstable選択 -> 両眼Frustum／Facing Cull -> CapCompatibility Group -> 全Cap不可視Group Cull -> 通常Color割当て／最後のColor統合 -> Colorごとの128初期化／選択対象の全Volume／全Cap描画`の順とする。Operation公開時に同じ採用面のRecordへ一度だけ引き継ぐ。同じColor内のVolume→Cap順を維持し、Plane容量超過でCap Record集合や論理状態を変更しない。

- Camera内部／Near Plane近傍の表示は5.2の品質例外とD-131に従う。Stable Geometry置換後はTemporary Stencil由来の部分Capを残さない。

- 仮Cap処理中にStencil Byte全8bitを排他的に使える構成だけを対応対象とする。Renderer初期化時に既知の設定と必要なAttachmentを一度確認し、成立しなければ4章の共通Player終了に従う。部分Bit利用、Stencilなしで継続する代替経路、復旧状態機械、毎フレームの構成再検証は作らない。既存Depth／Stencil Attachmentで成立する場合は別Textureの確保を要求しない。Stencil Byteの恒久的な物体割当は行わない。

### 5.7 共用Geometry・更新・描画の責務境界

前処理または切断対象登録は6章の共通入力契約を一度だけ検証し、合格したGeometryだけを表示／Stencilの共通正本として公開する。契約内入力に対する切断出力は6.4の構成契約を継承し、製品Runtimeで出力Geometryを再検証しない。4.5.6の公開条件が揃った後、表示とStencilへ一つのGeometry Commitとして公開する。用途別の適否、修復、別Mesh、同期完成待ちを追加しない。

点Anchor集合と、そこから導出される所有者の固定／動的、描画対象、選択済み切断平面、ローカルCap Boundsはそれぞれの入力変更時に担当側が更新する。同じフレームでの参照公開、世代・Commit条件を維持し、毎描画で切断履歴を再評価しない。

描画側は公開済みの共用Geometryと選択平面を使用し、Geometryや切断履歴の再検証、不成立原因の再検出、修復方法の選択、実Stencilの漏れ・相殺成否の監視、Triangle／Edge走査による別判定を行わない。現在のView、Transform、World Plane、投影Bounds、Facing、描画状態に依存する処理だけを描画時に行う。

入力不正の検出は入力準備側で行い、出力不変条件の検査はT-083等のオフライン回帰Harnessに限る。同じ不正条件をStencil描画、GPU画像比較、両眼試験で反復しない。契約内入力の符号・比較・描画条件はStencil実装時に小さいFixtureで確認し、状態公開・参照切替・世代／Commitは担当側で確認する。汎用Validator／Cache Framework、新しい状態機械、範囲外Windingのstress試験を追加しない。

7.9の確定後の任意分割は、既存の面集合とTopologyを配分し、所有者・frame・参照を同じ公開境界で切り替える。表示／Stencilの共用契約は維持し、新しい共用Geometryの切断面・Capや描画側の再検証を追加しない。

## 6. 表示／Stencil共用メッシュ切断

### 6.1 共用Geometryの正本

切断対象では、表示とStencil Volumeが同じGeometry世代、表面／実Cap Triangle集合、winding、Topology正本を参照する。現在の共用Geometryは4.5のUnity MeshまたはCPU側VPを正本とし、VP移行後は複数Geometry参照で構成できる。用途別の基底／派生Mesh、Index巻き替え、閉鎖面、Cache、Commit状態を持たず、4.5.6の正負Indexを直接出力し、同じVP正本からGPUコピーを更新する。分類、交点計算、切断、Cap生成を用途ごとに再実行しない。物理Convexはこの統合の対象外とする。

| 区分 | 内容 |
| --- | --- |
| 入力 | グローバルAoS VB／IBのview、posedな入力Geometryを表すIndex範囲等、必要なsubmesh・Topology／属性seam対応、同じ局所frameの採用面 |
| 作業・出力領域 | caller提供scratch、新規Vertex／Indexの各連続書込み許可範囲、必要なTopology等の出力領域 |
| 処理 | 6.2～6.4の分類・交点共有・元surface順序を保つTriangle clip・属性補間・Contour／有向Cap生成と、4.5.6の正負直接配置 |
| 結果 | 正負Geometryの範囲・Bounds、新規Vertex／正負Indexの実使用量、再切断と既存Boundary構築に必要な数値上の対応情報、成否 |

数値KernelはPhase 2.9で先行実装する。全域viewはアドレッシングのためのもので、処理対象は指定された入力Geometryとその参照先に限り、他物体を含む全VBの走査・初期化を前提にしない。呼出側は数値Workの投入から実行完了まで、指定入力とTopology・面を保持して不変とし、scratch／output範囲を有効かつ当該実行専有に保つ。他Workの別予約範囲への書込みは4.5.3に従う。Kernelは入力を変更せず、予約外へ書かず、完了後に参照を保持しない。成果物の保持・回収は4.5の既存資源寿命に従う。

必要容量の照会・見積りはKernel実装と同じ側が所有し、呼出側が領域を用意して数値Workへ渡す。正確な出力Countを先行Jobで求める方式は要求しない。成否の扱いは6.5に従い、予約不足は4.5.3の非公開終了・再予約・再実行へ、表面化した予期しない内部エラーは4章の共通終了へ接続する。契約内入力の未対応を正常な失敗や偽の空出力で代替しない。Unity Object・Scene操作、論理ID・世代・authority、資源取得、Schedule、GPU転送、Geometry CommitとRenderer固有情報はKernelの外で扱う。型・field・layout・関数名は実装詳細とし、内部工程の別Job化や永続的な引渡しartifactを要求しない。

元surfaceの既存submesh対応を保持し、実CapのIndexは本体の既存Draw rangeへ含める。実Capはその本体と同じMaterial・同じShader Pass・同じDraw callで描き、5.3のraw UV判定で通常Textureの取得と固定断面色の選択だけを分岐し、以後の陰影処理を共用する。複数の既存Draw rangeがある場合のCap配分は実装詳細とし、Cap用のsubmesh・Drawは追加しない。この同一Draw規則は実Capを対象とし、即時仮断面のStencil Volume／Cap描画は5章に従う。複数Renderer、submesh、閉Componentを一つの巨大Geometryへ結合する義務はない。幾何処理に必要なTopology対応を保ち、Geometry参照と描画集約、LogicalFragment、物理所有単位を分離する（4.5.1）。属性seamによるRender Vertex分裂、非同期Jobのための世代保持、CPU／GPU表現、物理Convexも別Geometryとは扱わない。

### 6.2 共通入力契約

`RenderCutTopologyMap`は属性seamをまたぐ論理Topologyの対応と、別Topologyの区別に必要な情報を持つ。通常切断の初期実装ではpersistent adjacency、BVH、全Component一覧・恒久IDを前提にせず、必要なedge・局所隣接・ContourをIndexとTopology対応から切断中のscratchへ構築する。元surfaceと生成Capの属性分裂後も再切断に必要な対応を残す。対応の格納形式、配列数、識別方法は実装詳細とし、位置一致による推測weldで代替しない。

`RenderCutTopologyMap`が表すTopologyについて、各Edgeへちょうど2面が接続し、その2面が共有Edgeを互いに逆方向へたどること、各Topology Vertexの周囲の面が一つの閉じたfanを作ることを要求する。使用するposition／属性はfinite、index／submesh／Topology参照は有効とし、同じTopology Vertexのposed positionと同じOriginal Edgeの交点positionは一度生成したcanonical値を共有する。

元surfaceのUV符号は入力Gateに含めず、5.3の誤表示許容を適用する。UVを含む使用属性のfinite条件は維持する。

Disconnectedな閉Component、全体反転した閉Component、bind pose／skinning後のSelf-intersection、別Topology Component間のIntersection／Overlap、Internal／Nested Shell、別Topologyとして表現されたCoincident／Duplicate Componentを許容する。物体全体を単一連結成分にせず、座標一致を理由に別Componentをweldしない。一方、Boundary Edge、3面以上が共有するEdge、一つのTopology Vertexを共有する複数fan、局所winding不整合は共用入力として受理しない。

この契約は閉じた向き整合Topologyの契約であり、自己交差のない幾何Solidの証明ではない。全Mesh自己交差、inside／outside、Generalized Winding Number、signed volume、外向き判定、向き正規化を入力Gateへ追加しない。

基底AssetはImport／前処理または切断対象登録時に一度だけ検証し、合格後は切断側が不変条件を継承する。UV／Normal／Material seamはFBX control point等の由来Topologyで対応付ける。由来が不明なseamを位置探索で推測したり、開放Boundaryのまま受理しない。不合格入力は切断可能Geometryとして登録せず、Runtime修復、Stencil専用Shell、表示だけを許す切断経路へ降格しない。必要なAsset修正や前処理Recipeをすべて自動実装する義務はなく、修正しない入力は切断対象外にできる。

### 6.3 Runtimeの面積0 Triangle

Runtimeの共用Geometryは面積0のTriangleを通常の入力・成功出力として保持できる。面積0だけを理由に面やedge-useを除去、非寄与化、修復、Commit拒否せず、閉鎖・edge／vertex manifold・局所winding整合を論理Topologyで維持したまま表示、Stencil、再切断へ使用する。

退化面の属性はfiniteに保つが、定義できない幾何法線の正規化を要求しない。アセット前処理の非退化品質条件は変更せず、スキニング後または切断後のRuntime表示Geometryへ再適用しない。Physics ConvexのRuntime条件は7.2に従う。Fixture固有の検証は17章の実装詳細とする。

### 6.4 切断とCap生成

Cを6.2と6.3のRuntime共通契約とすると、既存の有効な切断平面に対する切断処理は次を構成上満たす。入力座標・属性・切断平面はfiniteかつ既存製品Asset／Poseの通常範囲内とし、float演算がoverflowする極端な値は対象外とする。このためのRuntime範囲検査や数値Profileは追加しない。

```text
C(入力Geometry)
    ⇒ 各非空出力Geometryについて C(出力Geometry)
```

これは切断アルゴリズムの構成契約であり、出力全走査・変更部検査・finite走査・Topology Validator・Validation Jobを製品Runtimeへ置かない。交差判定、分類、補間、退化時の固定値選択、出力予約範囲を越えないための容量分岐は切断処理またはメモリ安全境界として維持する。出力契約はT-083のオフラインHarnessで確認する。Topology Vertex単位のsigned distanceを一度だけ確定し、OnPlaneはPositive側へ所有させて同じ分類を全incident Triangleで共有する。全頂点OnPlaneのTriangleはPositive側へ1回だけ保持し、そのTriangleからCap segmentを生成しない。平面がvertex／edge／faceを通る場合、同一点に複数のcut portが生じる場合、極小／面積0 Triangle、契約内Self-intersectionを通常ケースとして扱い、別Topology由来のportを位置近傍だけで接続しない。

各出力の元surfaceが持つ切断境界Half-edgeに対し、Capは逆方向の境界Half-edgeを持つ。切断Boundary EdgeにはCap側の面をちょうど1枚接続し、Cap内部Edgeには互いに逆方向の2面を接続して、頂点周囲も閉じた単一fanにする。非退化CapのNormal／Tangentはこのwindingと一致させる。切断平面は位置、signed distance、射影等へ使用できるが、実Capの表裏は元surfaceの有向境界から決め、全体反転した入力の向きを作り直さない。生成Cap Triangleの全Render Vertexには5.3の固定負UV markerを設定し、元surfaceとの属性seamは既存のRender Vertex分裂で扱う。既存実Capの再切断では通常の属性補間で負markerを継承する。Texture Mapping用のCap UV生成は行わず、幾何処理とNormal／Tangent生成は維持する。

Half-edge、edge hash、圧縮adjacency、Contour表現、三角形化、局所交差処理は、共通契約を満たす範囲で実装と実測から選ぶ。単純なContourへfan等を使うことは禁止しないが、不正入力や不正出力を重複Cap、逆向き重複面、Open Chain封鎖、Non-manifold lane分解で救済する段階列は要求しない。異なる閉ComponentやTrackをBoolean Unionせず、契約内の自己交差処理に必要なら局所Arrangementを共通Kernel内で使用できる。

### 6.5 公開と失敗

切断CPU出力の完成後はRuntime出力Validatorを挟まず、4.5.6のFinal／Logical公開後・祖先順Geometry Commitへ進む。待ち合わせは正常な未完了であり、用途別世代を作らない。8章のauthorityを失った成果物は不採用・回収し、共通Player終了へ送らない。

出力予約不足は4.5.3の非公開終了・再予約・再実行に従い、切断受付の再試行へ広げない。共用Geometry切断または論理構築で表面化した予期しない内部エラーは4章、CPU／GPUの絶対容量不成立は4.5.4に従う。物理・支持のCommit単位、Actor状態、世代・資源寿命は変更しない。

## 7. 物理切断

### 7.1 PhysicsSplitTransactionとLogicalFragment退役

一つの生存LogicalFragmentを置換するActive PhysicsSplitTransactionは最大1件とする。同じObjectId内の別LogicalFragmentは並行できる。Transactionは受付Snapshot・採用面・Sourceの物理所有変更authority、Provisional／未公開Final資源、点Anchor配分と受付時の親Rigidbody質量Snapshotを一回の物理分裂のために保持する。Geometry DAG、公開子孫・current leaf、Cut履歴、Geometry完了待ち、最終資源回収待ちは所有しない。既存CutOperationId、Pending Cut、Work識別・所有情報、生存性と所有参照を使い、新しい公開Transaction ID・状態／Reason enumを追加しない。

#### 7.1.1 未公開構築とProvisional公開

受付後は4.5.2の必要入力準備を経て即時clip／Stencil／仮Capを開始し、Final Convex／cookを並行して進める。点AnchorやWork入力未完了、未Schedule、Queue／Bake枠待ち、物理Step待ちは通常のPendingであり、構築不能とみなさない。

必要入力が揃った時点で、7.6のrobust support分類に従う正負2 Actor、旧Cooked ConvexのShape Instance、必要なSibling D6とBuilding World D6を一度だけall-or-none構築する。Provisional公開前にFinal一式が公開可能ならProvisionalを省略して直接Final Commitする。それ以外でProvisional一式の構築を試みて成立しなければ未公開成果物を破棄し、Transaction Abortとする。旧物理を維持したFinalだけの継続、再試行Queue、厳密予約Protocol、Actor／Joint Poolを必須にしない。

旧Cooked Convex GeometryをProvisional Shape Instanceへ結び付ける前に当該GeometryのProvisionalCollisionResourceLeaseを取得する。公開までは現在の旧物理を変更せず、途中のActor／Shape／ConstraintをGameplayや物理Stepへ部分公開しない。cleanupの内部順序は実装詳細であり、参照するShape／Actorと必要なPhysics Stepの寿命を満たして当該Leaseを一度だけ返す。別Leaseの参照消失は待たず、Geometry自体は全参照と最後のLease返却前に破棄しない。Timeoutだけで退役・Lease返却しない。

構築に成功したProvisional一式は、安全なPhysics境界で旧ActorをSceneから外す操作と正負2 Actorおよび必要なConstraintのScene投入をall-or-noneに切り替える。LogicalFragmentは未分裂のまま、両ActorのHitを同じSource LogicalFragmentへ解決する。旧資源の回収は既存の参照・Physics Step寿命規則に従う。

Provisional生成のためにConvex切断、Mesh複製、Physics.BakeMeshを行わない。同系譜Sibling間のCollision responseだけを無効にし、外界とのCollisionは有効にする。旧Convex共有によるGhost Contact、早い接触、外部物体へのImpulse重複を許容する。点Anchorを持つ所有者は固定しOffset／Impulseは0、持たない側だけを動かす。GeometryのSide空／非空を物理固定の条件にしない。

- 同じ切断で生じたProvisional Sibling間の`ProvisionalSeparationConstraint`は、Unity `ConfigurableJoint`によるanchor-offset D6へ固定する。`autoConfigureConnectedAnchor=false`とし、生成時の採用切断面法線をJoint所有Actorのlocal spaceへ変換して`axis`に設定する。`secondaryAxis`は同じlocal spaceの非平行なfinite方向から決定論的に直交化する。接線2軸の並進と全相対回転をLocked、XをLimitedとする。対称Linear Limitを`±1 m`、法線方向のanchor offsetを`1 m`として、生成時の相対位置を内向き境界、外向き`2 m`を反対側境界とする。これはJoint座標系での設定区間であり、毎Stepの厳密な変位保証ではない。両anchorは、生成時のWorld位置関係をJoint X座標の内向き境界へ置くよう、それぞれのRigidbody local spaceで設定する。軸の符号、Jointを置くSibling側、offsetを置くanchor側、直交基底の具体的な選択は実装詳細とする。Drive、Spring／Damper、Projection、Gameplay用Break Force／Torqueは使わず、存続中のaxis、secondaryAxis、anchor、connected anchor、Linear Limitを更新しない。

D6上限での停止・引戻し・Impulse・jitter・表示の違和感、引止めと除去後の運動差は正常な近似として許容し、検出・再中心化・Limit拡張・独自Pose／Velocity補正を行わない。法線上限は上記固定値だけで、物体寸法・Impulse・速度・経過時間から求めない。待機時間の上限は保証せず、Kerfは0とする。

ProvisionalSeparationConstraintはFinal handoff、置換、AbortまたはActor退役で既存Step・参照寿命後に一度だけ外し、除去のためのpose／COM線速度／角速度補償を行わない。BuildingWorldD6Constraintは別のActor寿命で維持し、7.2.2に従う。

- 固定を設定する入力には、必要な1個以上のfiniteな点FixedSupportAnchorと、その初期Logical Convex Cell、Fragment Physics Frame内local位置を明示する。初期Cellとlocal位置は不変とし、現在所属は各Cellが保持するAnchor集合で表す。Cell参照は内部配列位置または実装handleでよく、公開IDや祖先一覧を要求しない。Anchor内に単一のmutableな現在Cellを持たせず、支持を設定しない入力には点Anchorを要求しない。

- 各切断では入力Cellが現在持つAnchorだけを、受付Snapshot、採用面、同じframeと既存のfiniteかつ非負なanchorEpsilon=eで分類する。`s = dot(planeNormal, anchorPosition) + planeDistance`がs>eなら正側、s<-eなら負側、-e<=s<=eなら両側へ継承する。無効値をOnPlaneへ分類しない。非交差Cellは対応する子へ継承し、OnPlaneでは同じAnchor値を有効な各子集合へ1回ずつ格納する。

- 同Sideの別Convexや、旧Shapeを共有するだけのSiblingへAnchorを複写しない。所有者単位で継承集合の有無を集約すればよい。内接削減、再cook、Collider頂点や祖先全一覧からAnchorを再生成・再発見せず、最近傍検索・包含探索・間接支持判定を行わない。

- 点分類はcookを待たず入力と採用面から先行でき、少数Anchorは同期実行してよい。投機結果は8章の対象authorityと既存Anchor世代、採用面、入力Cell、frame、継承集合との一致を確認して採用する。未完了JobはWork依存で待ち、不正入力・世代不一致は既存検証と回収へ従う。支持付き対象は19.5.1の初期リベース対象外のままとする。

#### 7.1.2 Final PhysicsとLogical Publication

正常成功には正負各1のFinal Physics Owner、7.2の各側の質量特性・形状構築条件、必要なAnchor、frame、cook、Actor／Shape／D6を成立させる。片側を成立させられない場合は一Owner成功や片側だけの公開に変換せずAbortする。Final handoffのpose／COM線速度／角速度と質量継承は7.2に従う。

正負2子ID、親・子・面・Sideを持つLogicalCutOperation、Final Owner参照、Anchor／frame、Hit／QueryとTemporary表示対応を未公開で準備する。安全な物理境界と同じMain Thread更新区間で、Final Physics Commit、Sourceの現在対象終了、Operationと2子公開、Hit／Query／Temporary追従先切替、Transaction終了、Pending CutからPhysics authorityを外す処理を行う。途中に新規受付、Query結果のLogical解決、他の所有変更、Renderer状態収集を挟まず、旧完全状態または新完全状態だけを観測させる。単一CPU命令のatomic storeは要求しない。

Geometry Work、具体的Vertex／Index、実Cap、CutBoundaryRecord、GPU転送をPublication前提にしない。成功後は生存LogicalFragmentとFinal Physics Ownerが1対1となり、Geometry未完成でも各子を再切断・単独退役できる。親の現在対象終了は祖先Pending／Geometry Workの失効を意味せず、Geometry責務は4.5.6へ残す。

#### 7.1.3 Abort・Staleと単独退役

Provisional一式の構築不能、またはLogical Publication前にFinal Physicsを成立させられないと確定した場合はTransaction AbortとしてSourceを退役する。待機、PhysicsSplitTimeoutだけではAbortしない。同じTransactionのProvisionalについて既存Unity／物理所有／Constraint管理境界から継続不能異常が判明した場合もAbortし、成功後のFinal Ownerに同様の異常が判明した場合は対応LogicalFragmentを退役する。既存境界から明示されたnonfinite stateや実際のConstraint破綻を扱うが、包括的毎Step監視、速度Profile、Snapshot、復元・Kinematic Freezeを設けない。正常なD6 Limit到達は異常にしない。4章の共用Geometry／論理構築、絶対backing容量、必須Stencilの致命条件は共通Player終了を維持し、個別物理退役との相互Fallbackを作らない。

退役はPhase 4の共通低レベル処理とし、対象LogicalFragmentの生存性・Hit・Query・新規受付、通常／Temporary表示を終了し、Active Transactionがあれば終端する。当該対象へ未公開成果物をCommitせず、旧Final／Provisional／未公開Finalの当該所有Actor・Shape・システム所有D6をSceneから除去する。Geometry参照とRenderer登録を退役し、Schedule済みJob、CPU範囲、Leaseは読者と必要Stepの終了後に回収する。Siblingへ質量・Anchor・Geometryを移送せず、親へ再合流しない。Source消失による質量消失・接触変化を許容し、旧物理の恒久採用や復旧を行わない。IDとCut履歴を再利用・書換えせず、履歴が資源を強く所有しない。

成功／Abort後の遅延回収は必要な小さいcleanup情報へ移管し、Transactionを延命しない。外部authority喪失は8章のStale回収であり、古いTransactionが現在のSourceや他の所有資源を退役させてはならない。

### 7.2 Convex Cut/Cookと運動継承

一つの生存Final Physics Ownerが持つCompound Convex集合を一つの処理単位とする。入力Physics ProxyのAsset／登録時Gateは維持し、Runtimeの1 Convex当たり頂点上限は`L = 128`を入力と最終出力へ適用する。Compound全体の合計上限ではない。10.2.2のPhysicsCookInput Role Gateとは独立したRuntime規約であり、既存Assetの一括再生成は要求しない。

**実行分担。** Owner単位の分類、非交差ConvexのSide継承、交差Convexのplane clip、交点・切断面生成、必要な内接削減、質量特性の近似とCollider入力生成を、managed Job内から呼ぶBurst static kernelへまとめ、MeshDataへ出力する。Main ThreadでUnity Meshへ適用した後、managed Jobで必要な`Physics.BakeMesh`を行い、既存の安全なMain Thread／Physics境界でFinal Commitする。数値処理の内部工程を必須の別Job・公開Stage・状態にしない。複数Ownerを外側でBatch Scheduleしてよく、Job型、配列layout、Mesh資源の保持形状は実装詳細とする。

受付に必要なrobust support scanは7.6に従って同期実行してよい。同節で分割対象としたConvexだけを採用面d = 0で切り、その他は対応Sideへ未切断で継承する。既存Bake共有枠・Dispatcher・Work依存を使い、cut/cookの分担を理由に新しいSchedulerや公開状態を作らない。

**数値Kernel。** Phase 3.9で先行実装し、現在のCompound Convex B-rep、採用面と同じ局所frameのsigned distance・support分類、親質量等の必要値、およびcaller提供のscratch／output範囲を受ける。7.6で確定したdistance・分類を共用し、別の受付判定で置き換えない。正負B-rep、質量特性、実使用量と成否を返し、入力Convexから未切断継承先・正負出力への対応を取得できるものとする。対応は当該呼出し内の情報でよく、恒久Convex IDを要求しない。呼出側は数値Workの投入から実行完了まで入力B-rep・採用面・distance／support分類・親質量等を保持して不変とし、scratch／output範囲も有効に保つ。Kernelは予約外へ書かず、完了後に参照を保持しない。outputの後続利用中の保持・回収は既存の資源寿命に従う。Unity Object、資源取得、Schedule、公開・退役はKernelの外で扱う。具体的な型・field・layout・関数名は実装詳細とする。Phase 4では同じmanaged Job内で数値処理とMeshData出力を接続でき、分離を理由に別Job・永続中間成果物・引渡し状態を要求しない。

**容量。** 数値Workに使う同じ入力B-repのConvex数・頂点数・edge数等と実装表現上の上限から、Kernel実装側の容量式を同期評価してworst-caseのscratch／output必要量を算出する。呼出側が全必要範囲を予約し、その範囲を渡して数値WorkをScheduleする。容量照会では実clipや出力生成を行わない。half-edge、正負出力、補面、Polygon参照等を実際の表現に応じて含め、Lを中間scratch上限へ流用しない。予約した領域内で処理が完結する構成とし、容量式は実装時に導出して小さい境界Fixtureで確認する。Runtime Count pass、途中拡張、容量不足による再実行、部分出力公開は設けない。共有資源の一時的な不足は既存Pending、絶対backing容量の不成立は4.5.4に従い、予約未成立のWorkをScheduleしない。これはPhysics側の契約であり、表示VPの4.5.3の範囲予約・再実行を変更しない。

**形状の構築。** Runtime Physics Convexは全体としてfiniteで正体積の閉凸形状を構成する。2026-09-13の人間承認により、浮動小数点丸めに由来し、Kernelの局所predicate上で退化扱いとなるface／edgeや同位置に丸められた頂点を、それだけで失敗とはしない。閉鎖・向き整合のTopologyと定義される幾何量のfinite性を維持し、これらを含む採用B-repを次回切断入力として使用できるものとする。局所退化の除去・修復は要求しない。完成出力のfinite、面向き、閉性、凸性、重複、自己交差、包含を再走査するRuntime Validatorを設けない。Runtimeでは構築中の局所predicate、メモリ範囲、質量計算の成立と、既存境界で判明するcook／物理構築不成立を扱う。内部エラーの全検出は保証せず、検出保証のための専用Result・Reason・Buffer・Traceを作らない。

頂点数がLを超える出力だけを、通常clip結果の内側に収まるL以下の有効なConvexへ内接削減する。切断前ConvexをP、採用側半空間をH、通常clip結果をC、削減結果をQとして、`Q ⊆ C = P ∩ H ⊆ P`を構築条件とする。削減対象、削除順、頂点の移動・生成、局所再構成、頂点由来、決定性の範囲は実装詳細とする。L以下の有効なConvexを構成できなければ当該Physics処理を失敗とし、一般凸包再構築や別形状Fallbackで救済しない。採用した削減後B-repをcookと次回切断の基底とする。

Convex内部の識別は非公開の配列位置または実装handleでよい。現在のConvexと点Anchor集合の対応は7.1に従って保持し、非交差継承と正負／OnPlane配分に使う。AnchorをCollider頂点や削減結果から再生成しない。

**数値frameと物理への対応。** 数値精度のため、Compound中心近傍へ数値B-repの共通局所frameをrecenterするP1方針を採用する（2026-09-13人間承認）。Phase 3.9ではその共通局所frameの入力・出力で数値処理を実行・確認する。Phase 4でB-rep・採用面・Anchor・Colliderを対応付け、recenter前後で座標表現の丸め誤差を許容して同じworld形状を表し、Actorのpose・運動を維持して接続する。Cook用座標変換だけで数値B-repのrecenterを実施済みとはしない。具体的な変換方式・内部表現は実装詳細とし、専用frame型や数値上限Gateは追加しない。

**Final質量特性。** 切断受付時の親Rigidbody質量をSnapshotし、Final質量の正本とする。正負子の質量・重心・慣性は採用Convex集合から近似し、各Convexの体積を重複控除せず加算してよい。CompoundのBoolean Union、重複領域の厳密控除、表示Meshの体積積分、永続的なConvex別配分Metadataを要求しない。

正常分裂では、正負各Final Ownerの質量がfiniteかつ正で、正負子の質量合計が親質量に一致し、重心・慣性がfiniteかつSolverへ設定可能であることを要求する。計算方法、加算順、近似方式は実装詳細とし、成立しなければ当該Physics処理を失敗とする。最小質量やSiblingへの質量移送で補わない。次回切断では公開済みFinal OwnerのRigidbody質量を新しい親質量として使い、Provisionalの一時massからFinal正本を作り直さない。

**Provisional質量特性。** 一時質量は現在Source Actorの保守的OBBと今回のCut Planeから近似する。OBB正負体積の和がfiniteかつ正なら、その比で受付時の親質量を配分し、負側は親質量から正側質量を引いて求める。OBB体積が全0、非finite、演算不能なら等分する。center of massはclip済みOBBの近似重心、inertiaは保守的OBB／AABBの箱慣性とし、非finiteなら直前Actor inertiaを一時質量／親質量でscaleする。各子へfiniteな正質量とSolverへ設定可能な特性を成立させられなければ7.1のAbortへ進む。これらは短命なSolver用近似であり、Final正本には使用しない。

専用Physics Convexを持たない表示部分には独立質量を作らず、7.6に従って同Sideの物理所有者へ所属させる。表示部分の数を理由に質量を増減しない。

**完了と公開。** `Physics.BakeMesh`を含む既存境界からcook／Final構築不成立が判明した通常切断は、7.1のTransaction AbortとSource退役へ接続する。同じUnity Meshを複数Workから同時にBakeしない。完了後にSource生存性、Transaction authority、入力Physics、Cooking Profileと8章の世代契約を照合し、有効な正負Final Physicsだけを7.1で一体公開する。Stale成果物は適用せず回収し、現在のSourceを退役させない。7.9の任意処理は同じ数値Kernelと実行分担を再利用してよいが、失敗時は候補不採用・親維持とする。

- Parent ActorからProvisional Actorを初めて作る時だけ、速度継承の正本点を`FragmentRenderAnchor`とする。Source ActorのCOM線速度から`v_anchor = v_sourceCOM + omega_source x (anchor - COM_source)`を求め、`v_provisionalCOM = v_anchor + omega_source x (COM_provisional - anchor)`、`omega_provisional = omega_source`を設定し、切断命中時の表示Fragment poseとAnchor点速度を連続させる。Provisionalを省略して直接Finalへ分裂する場合も同じ初回分裂式を使用する。質量変更前後の運動量、角運動量、運動エネルギー保存は要求しない。

- ProvisionalからFinal Colliderへのhandoffでは物理Actorを正本とし、ActorのWorld pose、COM線速度、角速度をそのまま維持して、Final Shape、center of mass、inertiaだけを同一Actorへ置換する。Render Anchorを維持するためのActor pose補正や、新COMに合わせた線速度変換を行わない。採用する自前B-repは由来Convex内に、分割したものは採用半空間内にも収まるよう本節の構築規則で生成し、本節の数値frame対応に従ってFragment Physics Frameへ配置する。recenterによる座標表現の丸め誤差は本節の数値frame条件に従う。19.5.1の採用Local Plane由来のFinalにも同じ構築条件とframe対応を適用し、命中時Snapshotまたは予測Pose／速度へActorを戻さない。Actorを予測Pose等へ移動して適用を成立させない。cookによる形状・接触差は7.3に従う。表示GeometryはActorへ従属し、local origin／frame差によりFinal Commit時に瞬間的な位置・姿勢差が出ても許容する。分離ImpulseはProvisional生成時に一度だけ加え、Final Commitで重ねて再適用しない。Provisionalを省略して直接Finalへ分裂する場合だけCommit時に小さな分離Impulseを加える。Final Shape交換直後のSibling pairは既存の一時衝突抑止を使用できるが、外界とのGhost Contact履歴を理由にpose／velocityを巻き戻さない。

- Final handoffはSource Logical Convex CellのAnchor local位置と所属系譜を同じ子Cellへ引き継ぎ、Collider頂点・Shape共有・再cookからAnchorを再構築しない。物理固定は採用側が継承したAnchorの有無で決め、固定側のOffset／Impulseは0とする。

- 表示用MeshとCollider用Meshを分離し、Collider cooking用形状は低頂点・閉形状に保つ。

内接削減による次の品質変化は、後から形状を修復する前提の一時状態ではなく正式な近似結果として人間承認済みとする。

| 許容する結果 | 追加しない要求 |
| --- | --- |
| Colliderが表示より小さくなり、角・断面付近の接触取りこぼし、表示のめり込み、接触消失による落下・傾き・周辺運動の変化が生じる | 表示の完全被覆、接触中の削減禁止、接触維持用の拡張・位置補正 |
| 正負Collider断面の不一致・物理的な隙間、再切断での累積縮小、局所的な大きい欠けが生じる | 体積損失率・最大表面距離・累積誤差の品質上限、誤差履歴、後追い復元。表示のKerfは従来どおり0 |
| 現在採用Hull外にだけ存在する表示GeometryへのSlashWave Segmentは実Hitにならず、切れない | No-op専用Hull、表示Geometryでの二次受付判定、失われた当たり範囲の再生成 |
| 子への質量配分・重心・慣性が削減前の正確な切断結果と異なる | 削減前の質量特性を別正本とする補正。親質量保存は維持する |

内接削減による採用形状・体積損失・接触・質量配分の変化を許容し、損失最適性、品質上限、実装変更をまたぐ同一形状を保証しない。cook後の形状・Query・接触差の許容は7.3に従う。Actor pose／物理frame、Anchorと資源寿命は維持し、構築条件は小さいオフラインFixtureで確認する。

7.9の追加分割は本節のConvex Kernel・質量近似、初回物理分裂の速度継承を再利用するが、命中・Pending・Provisionalを経由せず、攻撃／分離ImpulseとOffsetを加えない。本節の通常切断用の分離Impulse規則を任意分割へ適用しない。

#### 7.2.1 通常切断の正負二集合と非Union

一つの論理切断対象の共用GeometryとSource Final Physics Convex集合へ同じ採用面を適用する。受付・分割対象の選別は7.6に従い、正常成功は正負各1のFinal Physics OwnerとLogicalFragment、合計2子とする。これはConvex数、閉Shell・Contour・Cap数やScene全体のActor数の上限ではない。同Sideの離れた島は列挙して物体化せず、同じ所有者へ所属させる。全GeometryのHull、Union、隙間を埋める形状やJointを作らない。

LogicalFragmentは連結成分ではなく正負集合と再切断範囲を表す。他のSiblingや祖先全体へ切断対象を広げない。Operation公開後はGeometry未完了でも子を受付け、後続Kernelは直前祖先Geometry Commitを待つ（4.2／4.5.6）。

同Sideの島は相対姿勢・運動を共有し、一部への接触や外力が離れた部分へ作用すること、Anchorと同じ所有者に属する部品の空中浮遊、疎な集合のBounds拡大と同じ論理対象内の離れた部分への同一平面切断を許容する。部品間の接着意図は保持せず、後の追加分割で独立してよい。島ごとの独立化は7.9の任意処理だけとし、分割不能・未検出・未実装のままでも通常切断は完成する。遅れて分離した結果を最初から独立していた場合の軌道へ補償しない。Actor数の削減に比例してShape・Broadphase・Solver費用が減る保証はなく、効果は既存Profilerで測定する。

幾何切断に必要なRenderCutTopologyMap、Original Edge交点共有、Half-edge等の局所隣接、Contour接続、閉鎖・manifold・windingの継承は6章に従う。別Topologyを位置一致でweldせず、自己交差・非Unionの既存許容を維持する。

- 正負の別Rigidbody間でCollider overlapが大Impulseを生じる場合は、同一Cut Operation由来Sibling間だけ既存の一時衝突抑止を使用できる。外界接触を維持し、既存の相対分離閾値・Timeoutで再有効化する。安全に戻せなければ抑止を維持してTraceし、物理終端を理由に必要な抑止を強制解除しない。

- Collider生成とcookは7.2の実行分担に従う。7.9の非命中処理も同じKernelを使えるが、架空CutOperationを作らない。

#### 7.2.2 建物由来子のWorld D6と製品Jointスコープ

建物由来の動的な1→2物理分裂子へ、Worldに直接接続する`BuildingWorldD6Constraint`を一つ生成する。水平移動・回転の制限はbest-effortとし、落下・横転・完全倒壊を許容する。正負二集合、点Anchorによる固定判定、ProvisionalSeparationConstraint、Sibling衝突抑止、一般物理Fault処理とPlayer非接触は維持する。親・Sibling・建物Root・Ground用Rigidbodyを接続先にせず、Tree／Graph、到達性、構造解析を導入しない。

**Metadataと更新境界**

建物由来の判定と分裂の深さのために、各物理所有者へ継承可能な正本Metadataとして次の二値だけを追加する。実装名は固定しない。

| 値 | 初期値・意味 |
| --- | --- |
| `IsBuildingDerived` | 製品Asset Recipeで明示した建物の初期所有者はtrue、その他はfalse。通常切断・追加分割の子孫へ継承する |
| `BuildingSplitDepth` | 初期値0の非負整数。建物由来の系譜で公開した1→2物理分裂の深さを表し、表示切断数は数えない。非建物の系譜は0を維持する |

建物由来をRuntimeのBounds、名前、Material等から推測しない。建物Metadataの製品生成はPhase 5.5へ置き、Phase 4.3では手書きSynthetic入力を使う。

通常切断では`IsBuildingDerived=true`の場合だけ、Provisional構築前（省略時はFinal構築前）に予定子Depthを飽和する親Depth＋1で求め、必要なBuilding World D6へ使用する。Final Physics／Logical Publication時に同じ値を正負の正式子へ公開し、Abortでは公開しない。非建物はfalse／0を維持する。Depthは非負とし、整数型と飽和方法は実装詳細へ置く。7.9の追加分割も同じ予定値で構築し、成功公開時だけ子へ継承する。No-op、不採用、Staleでは更新しない。Final handoff、COM／inertia更新で再加算、存続D6再生成、基準姿勢・anchor・Limitのリセットをしない。

**適用と設定**

D6生成対象は、`IsBuildingDerived=true`かつ点Anchorなしの動的所有者で、1→2物理分裂の予定子（Provisionalを含む）だけとする。点Anchorが一つでもある子はStaticまたはKinematicとし、建物D6を付けない。初期建物そのものへのD6追加は必須範囲に含めない。

基準位置・回転は子Actorを公開する物理境界でのWorld poseとし、生成直後の相対並進・相対回転が0となるようActor側／World側AnchorとJoint frameを構成する。予測姿勢・命中Snapshot・祖先姿勢へ巻き戻さない。World Y方向の並進はFree、水平2軸の並進と全3軸の回転はLimitedとし、水平2軸には同一距離Limit、全回転軸には生成時姿勢に対する同一の対称角度Limitを使う。

設定値は初回分裂子の水平距離`L1`、角度`A1`、共通減衰率`r`（`0 < r < 1`）の3値とし、Depth `d >= 1`に対して`L(d) = L1 * r^(d-1)`、`A(d) = A1 * r^(d-1)`を適用する。算出Limitは有限・非負とし、underflowの処理は実装詳細へ置く。正のminimum、Locked切替、深度別table、距離と角度で別の減衰率を追加しない。Drive、Spring／Damperによる復元、Projection／Transform Snap、Gameplay用Break Force／Torque、形状・質量・接触による動的Limit変更を使わない。製品値はO-048で決め、実際の総変位・総回転の上限を式から保証しない。

**生成・維持・退役**

| Constraint | 接続先 | 寿命・目的 |
| --- | --- | --- |
| `ProvisionalSeparationConstraint` | 正負Sibling | Provisional期間の旧Convex共有中の再侵入抑止 |
| `BuildingWorldD6Constraint` | World | 当該Actor寿命にわたる水平移動・回転のbest-effort制限 |

両者は統合しない。通常切断ではProvisional子、Provisionalを経由しなければ初めて1→2公開するFinal子の公開前構築へ必要D6を含める。成功したD6はFinal handoffで作り直さない。再分裂では親D6を付け替え・複製せず、新しい動的子ごとに現在World poseを基準として生成し、親D6は親Actorと同じ既存物理Step・参照寿命後に一度だけ退役する。Abort・退役や物理GCの対象も同じ所有資源寿命に従う。専用Lease、Constraint世代、親子参照表を追加しない。

必要D6は既存Constraint容量とActor／Shapeのall-or-none構築へ含め、D6なしの対象子を成功扱いしない。通常切断の必要入力・実行・物理Step待ちは7.1のPending、構築不能はAbortへ送る。7.9の任意分割では候補全体を不採用とし、親Actor・Depth・D6を変更しない。専用状態、Reason、Retry Queue、代替Constraintを設けない。

**製品入力の限定**

切断対象として登録する所有者は、切断システム所有の`ProvisionalSeparationConstraint`と`BuildingWorldD6Constraint`を除き、別RigidbodyまたはWorldとのJoint／Constraint関係を持たない。他物体のJoint接続先も対象外とする。扉、吊り看板、Chain／Spring付きプロップ、別Gameplay Systemが拘束する物体を初期製品の切断対象に含めない。

一般外部JointはAsset／Scene登録時の切断対象契約で除外できれば十分とし、毎Frameの探索、接続先からの逆参照、子への継承・付け替え・複製、GC保護、分割見送り、専用Trace・理由を要求しない。登録後に別Systemが外部Jointを追加することは契約外とする。将来のシステム所有Constraint追加は個別判断とし、汎用Constraint Provider／Plugin層を先行実装しない。

**人間承認済みの許容とPhase境界**

倒壊抑制が効かないこと、垂直方向の無制限移動、建物Root／Platformへ追従しないこと、独立したWorld基準と世代ごとの移動・回転の累積を許容する。小さいLimitでのjitter・drift・Solver未収束、接触・分離Impulse・Sibling抑止との競合、不自然な隙間・姿勢、penetration・接触Impulse・表示不連続もD6で防ぐ保証を置かない。動的建物子ごとのJoint数・Solver CPU・メモリ・生成退役費用の増加、一般Joint付き製品の除外、建物D6付き物体の初期未来予測対象外を許容する。拘束効果の監視・救済や性能SLAを追加せず、7.1の既存境界から判明した異常、8章のauthority、メモリ・資源寿命に従う。

Runtime本体と既知Constraintの識別境界は、ロードマップ上の独立Phase 4.3で通常切断へ接続する。Phase 4の汎用物理、4.1のCut/Cook Profiling、4.2のPlayer非接触を再オープンせず、4.50より前にT-094で完了する。製品Recipeは5.5、任意分割・GCへの統合は5.6／5.7で確認し、これらや未来予測本体を4.3の完了条件へ前倒ししない。

#### 7.2.3 プレイヤー非接触Locomotion

Player Body／Handとプロップ／破片のPhysX Layer接触を無効化し、刀Gestureから生成したSlashWave Segmentの論理SweepでInteractionする。人工移動は、Level初期化時に確定する固定`PlayerLocomotionOccupancy`への候補次姿勢Queryで扱う。

- OccupancyはLevel／Asset authoringで選んだ建物壁板、固定大型プロップ、Level境界等のOBB／Box／Capsuleによる低複雑度Primitive集合とする。初期化後はworld-space位置・回転・寸法と集合を変更せず、現在の物理所有者、Pending、切断、Geometry／Physics Commit、Fragmentの生成・移動・分裂・退役、物理GCへ追従しない。RuntimeのBounds・名前・Material等から登録対象を推定せず、元Objectへの所有参照を維持する必要はない。

- Root側の判定Volumeは単一Capsuleとする。Rootの判定VolumeとHMD Capsuleの寸法・基準local poseは、Player側の明示的authoring値を正本とし、Player初期化後は変更しない。基準local poseはそれぞれRoot相対・HMD相対とする。Root側は候補Root姿勢、HMD側は現在の追跡poseを候補Rootへ写した姿勢に、この設定を適用して固定集合へQueryする。Runtime Boundsや接触Colliderから推定しない。Rootの判定Volumeまたは予測HMD CapsuleがOverlapする場合は要求全体をRejectし、いずれもOverlapしなければ要求を許可する。現在姿勢との侵入深度比較、壁slide、部分適用、退出方向探索、反復Sweep／Line Searchは行わない。候補次姿勢だけの判定により、途中の薄い壁を飛び越える場合があることを許容する。

- spawn、Reset destination、別SystemによるPlayer Root直接配置は、Rootの判定Volumeと配置後のHMD Capsuleが固定Occupancyと非Overlapであることを呼出側／authoring側の前提とする。違反時の自動退出や安全Pose探索は提供しない。実空間HMD LeanによるOverlapは許容し、HMD・Player Rootを強制移動しない。侵入を浅くする方向でも候補がまだOverlapする人工移動は拒否するため、一歩で外へ出られなければ人工移動できないことを許容し、実空間で頭を戻すか既存の明示的Resetへ委ねる。専用の重なり状態や復旧経路は設けない。

- PoC／初期製品では切断開口を通行境界へ反映せず、元の壁・大型物体が倒壊・移動・退役しても固定Occupancyが空間を塞ぎ続ける。固定Volumeによる余分な閉塞、未登録Geometryや動的物体・Fragmentへの人工移動での侵入を許容し、表示／物理との不一致をGeometry・Physics・Commit・Assetの失敗にしない。物理CommitをPlayer位置だけで拒否・巻戻ししない。

- Runtime Occupancy更新が必要になった場合は別の設計変更で決定し、現在の実装・試験・Phase完了条件には含めない。

- 実空間HMD非Clamp、Camera overlap非失敗、視界効果の撤去と表示品質例外はD-131／5.2に従う。Player接触を物理世界から除外することで、身体接触による未来Physics結果の無効化を発生させない。Player位置と斬撃は依然として介入条件だが、Fragmentの投機物理Commit条件へPlayer接触Impulse履歴を追加しない。

### 7.3 Collider Cooking Profile

初期製品は一つの明示的なCooking Profileだけで成立させる。具体的なcookingOptionsはPhase 4の実装で選び、`Physics.BakeMesh`と適用先`MeshCollider.cookingOptions`へ同じ構成を指定する。Bake後にMesh形状を変更しない。入力Gateと7.2の自前B-rep構築条件は維持する。

処理が完了し、`Physics.BakeMesh`を含む既存境界でcook不成立が判明せず、必要なCollider／Shapeを構築できる場合、既存の入力・資源・世代・公開条件に従ってその結果を採用する。2026-09-13の人間承認により、cookによる膨張・縮小・厚み付与・局所形状差、それに伴う新たな接触・penetration・運動変化、Unity Physics Queryの精度差・取りこぼしを許容する。入力B-repとの一致・内接性、同じBounds／support／Raycast／ClosestPoint結果を合格条件にしない。既存境界で判明したcook・物理構築不成立や継続不能は、7.1／7.2／7.9の失敗処理に従う。

自前B-repは次回切断・Final質量特性の正本に残し、cook後形状から再構成しない。Gameplay命中と受付は19.1.7／7.6を維持し、Unity Raycastへ置換しない。形状忠実性のためのcook前後のRuntime Oracle、thinness判定、形状修復・retryや、同じ一致条件によるPhase完了・製品合格Gateを設けない。既存Probe Oracleやcook smokeは診断に利用できる。数値Kernelの不正出力、P1 recenter・Cook Frameの座標変換ミスをcook品質差として許容せず、新しい成功戻り値・エラーの完全検出・Console解析・cook後形状抽出も要求しない。

### 7.5 Cut/Cook Profiling

Phase 4.1は、代表Fixtureで製品経路の費用を確認する軽量な測定点とする。Burst Convex kernelのOwner当たり時間、Mesh適用とBakeを含むCut/Cook全体のEnd-to-End時間、owner/s・convex/s・Bake数、Final Commit時間、scratch予約量とピーク使用量、cook失敗・stale reject・Abort数を既存Profiler／Harnessで確認する。

保存形式、Marker名、分位、反復数はHarness実装詳細とし、専用の永続Schema・Codec・Golden・Loaderや固定性能matrixを要求しない。O-035／O-039の実行枠・メモリ予算の調整に使い、高度なSchedulerは必要性が実測で示された後だけ検討する。

### 7.6 robust supportによる受付と正負二Owner

切断受付入力はActive Transaction外のSource Final Physics Ownerが持つ現在Convex集合だけとする。Sourceの生存性とTransactionなしを先に確認し、受付Snapshot・採用面から各頂点のsigned distance dを一度求める。一つの有限・非負な距離epsilon eについて、d > +eをPositive support、d < -eをNegative support、-e <= d <= +eをNear-planeとする。等号・Near-planeはどちらのsupportにも数えず、集合全体に両supportがある場合だけ受付ける。非finite・分類不成立をNear-planeへ丸めず、既存入力Gate／内部エラー境界へ送る。

| 各Convexのrobust support | Final切断・継承 | Provisional配分 |
| --- | --- | --- |
| 正負両方 | 採用面d = 0で分割 | 両側 |
| 正側だけ | 未切断で正側へ継承 | 正側 |
| 負側だけ | 未切断で負側へ継承 | 負側 |
| どちらもなし | 未切断でPositive側へ継承 | Positive側 |

同じdistanceとsupport分類を受付・Provisional配分・Convex分割可否へ共用する。分割対象B-repの実切断はd > 0／d < 0／d == 0で行い、eを実切断の正負分類、面offset、snap、Kerfへ流用しない。一方のrobust supportを欠くConvexはd = 0を横断していても未切断継承し、反対側へepsilon程度張り出す近似を許容する。点Anchor配分は7.1のCell規則を維持する。

片側supportなしはNo-opであり、対象状態・世代・Anchor・表示・物理・既存履歴を変更せず、ID、Pending Cut、Transaction、切断／cook／Commit仕事を作らない。命中観測と非破壊通知は残せるが、要求を保存・再実行しない。接線・頂点／辺／Face接触だけ、反対側がepsilon以内だけ、全頂点Near-planeはNo-opとなる。別Convexが明確に正負へ分かれたCompoundは交差Convexなしでも受付け、Boundary 0の物理1→2を許容する。入力判定に出力Convex・Cap・連結成分や表示Geometryの二次判定を生成しない。

このmarginはcook成功を保証しない。受付後に判明した7.2の形状・質量特性・cook不成立、Anchor・必須D6・frame不成立は7.1のAbortへ送る。正常成功は常に正負2 Final Ownerと2 LogicalFragmentで、Geometryは同Sideへ所属する。Geometry片側または両側が空でもPhysics子は残し、反対側へのGeometry付属、一子両Side、Merge／Alias、所有者共有を設けない。

Geometry空は実Cap・面積0 Triangleを含む面集合0件で判断し、未計算・失敗・体積0・Stencil非描画を空にしない。Geometry参照0件の子はRendererなしの通常物理対象であり、ダミーGeometry・Cap・消滅VFXを作らない。任意の7.9物理GCが未実装・対象外でも通常Object／Level寿命で維持する。Phase 1～3では15章のHarness合成Physics入力だけを使い、製品に代替Hull／Physics Modeを残さない。

### 7.7 切断受付上限

既存容量設定のMaxIncompleteCutOperationCountは受付済みPending Cutを全対象合計で1件ずつ数える。Physics実行、Geometry依存待ち、Commit待ちを含め、Transaction、子、Job数を多重計数しない。Active Sourceへの見送り、No-op、未命中投機、7.9任意処理は数えない。

Mainの受付処理は4.2のSource／Active確認と7.6のsupport判定の後、上限未満の場合だけPending登録と件数加算を一体で行う。満杯なら新規仕事や状態変更を行わず要求を保存・再実行しない。同じSlashの一部対象だけ切れることを許容する。

Physics TransactionはFinal／Logical Publicationで終了するがPendingは自分のGeometry CommitまたはBranch退役・Stale終端まで残り、一度だけ減算する。A Commit前にB／CのPhysicsとPendingが存在してよい。Queue／Bake枠／Step／期限待ちだけで取消さず、Geometry依存解消後のWorkだけを既存Dispatcherへ送る。Provisional構築を試みた後の不成立は待機へ戻さず7.1のAbortとする。終了後の遅延回収は件数・Transactionを延命せず既存所有責任へ渡す。新しい待機Queue、予約Protocol、Schedulerを作らない。

`MaxIncompleteCutOperationCount`は0より大きい固定値として既存容量設定へ置き、独立Profile、動的最適化、ヒステリシス、全DAGの将来資源予約を追加しない。製品値はT-076／O-039の実測後に決め、それ以前は明示した保守的な試験値を使う。

### 7.8 全体低重力

空中物体斬りの猶予を自然に増やし、世界全体の挙動を統一するため、個別の空中斬り補助ではなく全体低重力を初期方針とする。PoCの仮値は標準重力の約0.5倍、`(0, -4.9, 0) m/s^2`とするが、最終値はプレイテストで決める。

- `WorldPhysicsProfile`を重力の唯一の設定元とし、起動時に`Physics.gravity`へ適用する。物理予測、解析軌道、その他の非物理VFXも同じ値を参照し、`-9.81`などを各実装へ直接記述しない。

- 重力値はInspectorまたは開発用設定から変更可能にし、初期比較候補を0.35G／0.5G／0.7G／1.0Gとする。各Runの重力ベクトルとProfile版をTrace／Run Manifestへ保存する。

- PoC初期は反発係数、Drag、切断分離Impulse、モブのジャンプ／落下Animation、破片寿命を低重力専用に作り込まず、既定値または仮値を使用する。実プレイで具体的な違和感が確認されてから個別に調整する。

- `Time.timeScale`による常時スローモーションは重力調整の代用にせず、入力、斬撃波、非同期処理、物理予測の時間軸を通常速度に保つ。PoCでは対象別Gravity Scaleも導入せず、必要性がプレイから判明した場合だけ拡張する。

### 7.9 コミット後の任意処理

確定済みの通常物体に対し、追加空間分割と表示なし物理物体の遅延回収（物理GC）を独立した任意機能として設ける。両方無効・分割のみ・GCのみでも通常切断・Commit・再切断は成立し、未実行・延期・不成立を過去の切断の未完了、Commit失敗、再試行理由へ変更しない。即時応答は通常切断優先、幾何精度は共用面集合の保存、物理整合は所有単位の再編成または寿命終了、性能予算は低優先処理の費用と処理後の物体数へ影響する。性能改善量・発生頻度・期限内完了は保証しない。

#### 7.9.1 共通の対象と通常切断優先

本節の物体は、一つの生存LogicalFragmentと、それが専有するFinal Physics Owner、Convex、共用Geometryを指す。同じObjectIdの全Siblingを一括対象にせず、開始・適用時の両方で次を確認する。

- 表示／Stencil共用GeometryとPhysics Proxyが確定し、所属・frame・参照が有効である。LogicalCutOperation公開済み、またはGeometryだけCommittedでは足りない。
- Active PhysicsSplitTransaction、未完了祖先制約、Anchor配分やProvisional引渡しがなく、通常のFinal Physicsを採用している。
- 計算用の不変な局所Geometryと現在の物理frameへの写像を保持でき、形状・scale・所属・支持の変化を既存の世代／前提照合で検出できる。共通の剛体並進・回転だけで静止を要求せず、適用は現在pose／速度へ接続する。

別の処理が所有構成を同時に書き換える対象は初期は見送ってよい。読み取り専用の予測Jobだけを理由に永久に対象外にはせず、7.9.4の失効・資源寿命で扱う。条件確認不能なら元物体を残し、判定修復や専用待機状態を作らない。

準備中も元物体を正本として動かし、任意処理の完了を通常切断の受付・成立・公開の条件にしない。共有資源の競合による処理開始・完了の遅延は7.9.5の範囲で許容する。同一対象の任意変更は既存Main Threadの受付・Commit順で直列化し、公開直前に前提を再確認する。通常切断の受理、別更新または退役が先行した候補は公開せず、未Schedule仕事は既存取消、実行中仕事は完了後の不採用・回収へ送る。候補を最新対象へ推測適用しない。No-opや受付見送りで対象が不変なら、それだけで候補を失効させない。

#### 7.9.2 追加空間分割

成功は、一つの物理所有単位から、それぞれ非空の共用Geometry、有効な非空Convex集合、正規質量、既知の支持を持つ通常物体2個を得ることとする。Rendererだけの分割や、全Geometryを再び同じ所有者へ戻す結果は成功としない。2はこの追加分割一回の出力物理所有単位数であり、閉Component数や系譜・Scene全体のActor数ではない。各出力に複数閉Componentを含めてよい。表示なし第三物体や小さい側の生成省略は追加しない。

初期方式は固定された対象局所frameの一枚の平面で、現在の共用面集合を横切らず正負の非空集合へ配分する。大きなIndex範囲も内部をTriangle単位等で調べ、既存数値許容内で欠落・重複なく配分できることを確認する。既存Boundsが使える箇所は使うが、通常切断へ全島列挙を前倒ししない。探索は有限の試行・作業量で打ち切り、GJK、全組合せ、最良平面、完全分離を要求しない。候補なし・確認不能は7.9.5へ送る。

既存の表面・実Cap・面積0 Triangleを属性・winding・Topologyを保って配分し、新しい表示切断面やCapを生成しない。分割先別の新Index領域へ振り分けコピーし、必要範囲を転送する。比較ソートは必須でなく、Countと正負への書出しでよい。既存Vertexを参照し、全頂点複製、表示専用第二Index正本、全プールコンパクションは要求しない。新旧Indexの一時共存を許容し、旧CPU範囲は4.5.3のCPU正本参照・Job・転送元読者寿命後に再利用する。この処理を通常切断へ戻さない。

現在採用中のPhysics Convex B-repを同じ平面で分類し、非交差ConvexはB-rep・再利用可能なcook済み資源を継承する。交差Convexだけ7.2のclipと必要なcookを行い、全採用集合から質量特性を近似し、共用Geometry全体の凸包再構築、Convex Union、未切断Shapeを両側へ複製するProvisionalを作らない。表示Geometryの対応有無にかかわらず全採用Convexを切断子または非交差継承として配分する。片側有効Convexなし、正質量不成立、実cook不成立、資源・構成の成立不能では候補全体を不採用とし、元物体を残す。一時的な枠不足は7.9.5と区別する。

共用Geometryは今回配分した同じ側の物理所有者へ所属させる。反対側付属で二物体化を偽装しない。点Anchorは入力Cell集合から7.1の正負／OnPlane両側分類で継承し、その所有者が一つでもAnchorを持てば固定、なければ動的とする。Geometry付属をAnchor移送に流用しない。

Anchor配分と通常の物理条件を成立させて公開する。建物由来の分割には7.2.2を共用し、両子へIsBuildingDerivedを継承して公開時にDepthを親から1段進め、Anchorなしの動的子だけに新しいWorld D6を生成する。親D6は付け替えず、親Actorとともに退役する。必要な子D6の構築が不成立なら候補全体を不採用とし、親Actor・Depth・D6基準姿勢・Limitを維持する。単純な点Anchorの固定を保持できる対象を一律除外しない。

追加分割開始時の親Rigidbody質量をSnapshotし、7.2に従って採用Convex集合から子の質量特性を近似して親質量を保存する。共通祖先の全Sibling分を再配分しない。Geometryの個数・面積・寸法から質量を作らない。公開直前の現在pose／速度へ接続し、共用GeometryのWorld配置を通常の数値許容内で保つ。動的子には7.2の初回物理分裂の速度継承式、固定側には既存無運動規則を使い、攻撃・分離ImpulseとOffset、親poseの巻戻しを加えない。

両出力のGeometry参照、Convex／cook資源、質量・点Anchor、通常Actor・必要な建物D6と登録先を準備し、安全なMain Thread／物理Step境界で所有構成を一括切替する。描画は切替前か切替後の完全な構成だけを使い、片側先行公開、親の先行削除、表示／Stencilの別時点移管をしない。準備・公開前検証の不成立では未公開資源だけ回収する。

現在のLogicalFragment／Cell表現で、各Geometry範囲の再切断対象・frame・支持・物理所有者を一意にする。必要な通常IDと子配分情報は既存の発行・系譜規則で作り、過去IDの再利用、過去Operationの直接子・作成時世代の書換えはしない。既存の切断履歴・実在境界を維持し、現在世代への対応を原子的に更新する。新しいGameplay Cut Plane、CutBoundary、SlashHitConfirmed、LogicalCutOperationは作らず、対応を安全に構築できない候補は見送る。

公開後は専用分割片ではなく通常の切断対象とし、現在の共用Geometry・Convex・Anchor集合から再切断する。旧Actor・そのシステム所有Constraintと不要Shape／Mesh参照は7.9.4に従って退役する。公開後の継続不能な個別物理異常は7.1のLogicalFragment退役へ従い、任意候補専用rollbackを追加しない。

#### 7.9.3 表示なし物理物体の遅延回収

物理GCはManaged GCや全参照追跡型GCではなく、共用Geometryが確定空の生存物理所有単位を終了するゲーム上の寿命Policyである。7.9.1を満たし、所属する共用Geometry全体の面集合が0件で、有効な非空Physics Convexを持つ対象だけを候補とする。実Cap・面積0 Triangle・全Renderer／submesh相当範囲を含め、画面外、遮蔽、Renderer無効、Stencil相殺・非描画、体積0、未完成・失敗を空の証拠にしない。

一面でも共用Geometryが残る物体は回収せず、個別Convexを間引かない。仮Actor・未公開一時資源は7.1の回収へ送る。対象自身の建物D6はActorと同じ寿命で退役する。Anchorなしだけを回収許可にしない。

通常の外界接触や移動だけでは回収を禁止しない。当たり判定消失による周辺物体の運動変化を許容し、接触中の完全無影響証明、Sleep必須、過去Impulseの取消を要求しない。開始・適用時の候補条件が一致する場合だけ、安全な物理境界で当該所有単位とそのLogicalFragmentの生存・受付、Physics Scene、動的な所属Query等の登録を終了する。Level初期化時の固定PlayerLocomotionOccupancyは対象Objectの退役で変更しない。同じObjectId・祖先履歴を持つ無関係Siblingは削除しない。

生存LogicalFragmentを終了する低レベル処理はPhase 4の7.1を再利用し、本節は表示なし物体を選ぶ遅延Policyだけを追加する。質量は世界から除去し、Siblingへの移送・再正規化をしない。正常分割時の親質量保存と退役時の質量消失を区別する。

候補検出は既存物体一覧の有限件数巡回と確定Geometry件数等でよい。毎Frame全Mesh走査、逆被覆対応表、専用Asset前処理、VFXは追加しない。巡回順・頻度の高度な最適化や回収期限を初期要求にしない。

#### 7.9.4 非命中公開・退役と既存世代への接続

本節の所有構成変更・退役だけは、命中を伴わない公開を許可する。候補は既存TaskId等の相関情報と成果物所有者、対象のObject・Geometry・Physics・Anchorの必要な世代・構成参照で識別する。準備・見送り・不採用だけではObjectGenerationを進めず、同じ基底の通常予測を失効させない。

追加分割の公開では対象の既存ObjectGenerationと実際に変更するPhysics等の既存世代を同じ境界で進める。GCは退役を既存の生存性／世代付き参照へ反映する。いずれも旧所有構成への予測・後着成果物を適用しない。履歴ID・作成時世代を書き換えず、世代wrap・ID再利用・別対象への古い適用を許さない。通常Transaction／Geometryの照合は8章に従い、別LogicalFragmentのObjectGeneration更新だけでは失効させない。

通常切断の実Hit・受付・採否条件は緩めず、本節の仕事へPending Cutを作らず、MaxIncompleteCutOperationCountにも数えない。通常切断が先に受理されたら任意成果物を不採用にし、任意変更が先に公開済みなら以後の切断は更新後の対象へ適用する。中間の曖昧な受付対象を公開しない。

登録終了と最終資源解放を分け、Schedule済み仕事の完了・回収責任と入力保持を維持する。移管した参照、他の生存対象・履歴が必要なMetadata、参照中の共用Geometry／Cooked Geometryを解放せず、旧Actor・Shape・システム所有Constraint・Meshは既存のStep・Job・GPU寿命に従って一度だけ退役する。全履歴圧縮、全メモリ即時解放、専用世代別GC、物理巻戻しを要求しない。

独立Maintenance世代、専用ID空間、Registry、永続形式、第二の状態機械は設けない。通常IDと生存Workの所有情報は既存規則を使う。観測は既存Task lifecycleと少数Counterで種別・成功／見送り・不採用・回収数を区別し、切断成功数や架空のCut Operation Trace束へ混ぜない。

#### 7.9.5 単一試行・再試行抑止と予算

無効な機能は候補走査も追加Jobも発行しない。候補計算、Convex処理、必要なcookと回収は既存Job／Completionを使い、新規仕事は4.4のMaintenanceとして余裕がある場合だけScheduleする。共有Queueの容量競合と、Schedule済みJob／cookによるWorker／Bake枠の一時占有・投入済み費用により、後から到来した通常切断の処理開始や完了が遅れてよい。4.4の物理仕事への投入余地を転用せず、専用Scheduler、追加予約枠、同期回復、優先度昇格、必達期限を追加しない。

追加分割は全対象を通じて未回収の試行を初期上限1件とする。枠が空き、軽量な入口条件を満たし、現在入力の再試行抑止がない物体を有限巡回で1件選ぶ。幾何的な分割可否をMain Threadで先に確定せず、一つの試行内で背景の候補探索・配分確認から必要なConvex処理・cook、回収・公開直前検証まで進める。候補平面を同じ試行へ引き継ぎ、候補判定だけの必須先行Job、恒久的な成功候補Cache、新公開APIを要求しない。

1件の枠は投入待ち、計算中、cook待ち／実行中、適用待ち、不採用時の安全な回収まで保持し、探索完了だけでは空けない。Compoundの後続cookは既存Batch・投入上限内で少数ずつ進め、大量Workを一括投入しない。全工程を一つのJobにする必要はなく、数値計算・対応BakeはJob、Unity Object操作・公開は既存Main Thread／物理境界へ置く。完了回収は新規試行のアイドル条件待ちにせず、通常予算で継続する。

物体側には「現在入力では今回の方式で追加分割不成立のため再試行しない」という印だけを既存世代・構成参照へ結び付ける。通常切断または関係するGeometry／Convex・所属・Anchor入力の変更で無効化し、共通の剛体並進・回転や時間経過だけでは再探索しない。数学的な分割不能証明ではなく、通常切断・GCの受付にも使わない。進行状態は1件の試行と既存Workで持ち、物体側に重複した状態体系を作らない。

| 結果・状況 | 処理 |
| --- | --- |
| 成功し適用直前も入力有効 | 通常物体2個を原子的に公開し、試行を終了・回収 |
| 候補なし、数値・形状・点Anchor配分・実cook結果・必要D6構築等で不成立 | Main Threadで入力一致を確認した場合だけ抑止を記録し、元物体維持・回収 |
| 通常切断等で前提変更、対象退役 | 成功も失敗も現在対象へ反映せず回収。新入力の抑止印を変更しない |
| 未実行、一時的なQueue／実行枠・予算不足 | 抑止を記録しない。未開始なら後の巡回へ、進行中なら同じ1件枠で延期するか抑止なしで取り下げ・回収 |

成功後の新しい所有構成へ親の不成立印を引き継がない。古い結果の不採用・印の無効化は7.9.4の照合を使い、専用Cache基盤・再試行Queueを作らない。GCは独自に有限巡回し、分割の抑止・成功・完了やその試行枠を実行条件にしない。この独立性も共有資源の競合による遅延がないことを保証しない。

4.4の必須切断Queue満杯時の同期進行例外は本節の両機能へ適用しない。Main Threadの構築・公開・回収も通常予算内とし、収まらなければ延期・見送りにする。別分離方式、強制消去、Proxy再生成、VFX救済、Job同期待機、同期cook、通常切断の受付停止、優先度昇格、同Frame無限再投入を追加しない。

#### 7.9.6 承認済みの許容と適用範囲

| 許容する結果 | 省く要求／維持する境界 |
| --- | --- |
| 未実装・無効・延期・不成立でも元物体が残る | 通常切断の救済や過去Commitの取消を要求しない。両Phaseを省略してPhase 6へ進める |
| 一枚の平面による2分割で扱えない配置が残る | 任意形状の完全分離、N成分一括分離、最良平面を要求しない |
| 点Anchor配分・必要な建物D6構築を成立させられない対象を見送る | 代替Constraintや部分公開で成立させない |
| 通常切断優先で任意成果物が不採用になり、共有Queue／Worker／Bake枠の一時占有で後着の通常切断の処理開始・完了が遅れる | 投入済みJob／cookの費用を負担する。任意処理の完了を通常切断の受付・成立・公開条件にせず、前提が変わった実行中成果物は完了後に不採用・回収する |
| 有限試行が不成立なら同じ入力で再探索しない | 完全な分離不能証明を要求しない。未実行・一時枠不足・古い結果を不成立へ混ぜない |
| 低優先のため実行・回収が遅れ、期限内完了しない | 公平化・必達期限・容量回復の同期待機を要求しない |
| GCまで不可視物体が接触・Impulse・支持を持ち、無効なら通常寿命まで残る | 即時削除を要求せず、対象自身のシステム所有Constraintを既存寿命で退役する |
| GCの当たり判定消失で周辺運動が変わる | 通常接触・移動だけで回収を禁止せず、過去の接触効果を巻き戻さない |
| GCで対象の質量も世界から消える | Siblingへ移送・再正規化しない。分割時の親質量保存は維持する |
| GC前の生成・必要cook・シミュレーション費用を負担する | 作らず消す最適化を追加しない |

不正Geometryの成功扱い、面積0 Triangleの削除、非finite／不正物理の適用、未計算を空とする判断、世代不一致公開、使用中資源解放、支持・安全Constraintの無断除去は許容しない。分割成立のために表示を消さない。

小さい通常物体の消滅・基本フェード、三角形霧散・Fallback Geometry消滅、消滅予定部分の生成省略は今回対象外とし、先行Metadata、予約Buffer、専用状態を用意しない。旧小破片分類、被覆対応Graph、Shared解決専用機構、Shard／Atlas／専用Arenaを復活させない。

#### 7.9.7 完了条件と最小確認

無効時の通常切断を維持し、有効時は分割成功後に通常再切断でき、GC後は退役対象が復活しないことを完了条件とする。追加機能固有の確認は次の少数合成Fixtureに限る。T-007／T-059／T-074／T-086／T-091は各担当の世代競合、引渡し、点Anchor、資源寿命の検証を再利用し、Provisional固有契約や全matrixを本節へ複製しない。

| 確認群 | 最低限の期待結果 |
| --- | --- |
| 独立した有効化 | 両方無効・分割のみ・GCのみで通常切断が成立し、無効機能の候補仕事なし |
| 分割成功 | 離れた二つの閉Geometryを一つのConvexがまたぐ例と非交差Compound例で、探索から必要cook・公開まで1件の試行で進み、通常所有者2個を生成。実Capを含む面集合・World配置・正規質量・再切断基底を維持。単一Index範囲の内部に両出力の面がある場合も新範囲へ配分・転送し、使用中の旧範囲を早期解放しない |
| 分割見送り | 単一平面で分離不能、面近傍、片側有効Convexなし、質量不成立、容量／cook・必要な建物D6構築不成立で元物体不変。部分公開・架空境界・Geometry消去なし |
| 支持・frame | 点Anchorを持つ島と持たない島が同じ所有者ではともに固定され、分割後はAnchorがない側だけ動的になる。OnPlane継承と旧共有資源からのAnchor非復活を確認する |
| GC選択 | 真の面集合0だけが候補。面積0 Triangleのみの非空、Renderer無効、画面外、未完了Geometry、可視Compound内の代表参照なしConvexは対象外 |
| 建物分割（Phase 5.6） | 成功時は7.2.2に従って建物由来の両子Depthを親から1段進め、Anchorなし動的子だけに新D6を生成して親D6を親Actorとともに退役する。不成立時は親Actor・Depth・D6基準姿勢・Limitが不変 |
| GC退役 | 接触中の表示なし物体を丸ごと回収し質量移送なし。対象Actorとその建物D6を既存寿命後に一度だけ退役する。共有資源・過去履歴・無関係Siblingを誤解放しない |
| 競合・寿命 | 通常切断・別更新後に古い成功公開と失敗抑止書込みを拒否。任意公開後の旧予測成果物も拒否。二重公開・解放・切断件数変更なし |
| 再試行抑止 | 同一入力の不成立後は再探索せず、通常切断・GCは継続。関係入力変更で再対象化し、剛体移動だけでは再試行しない。未実行・Queue不足は不成立にしない |
| 低優先・容量 | cook・適用・不採用回収を遅らせても未回収試行は全体1件以内。完了回収は新規投入のアイドル待ちにせず、Compound cook大量投入なし。利用可能枠0・Queue満杯でも同期回復・昇格・無限再投入なし |

専用Test ID、旧Shared／Debris試験の復活、大規模Benchmark、P50／P95／P99製品保証を追加しない。探索方式、保存layout、巡回頻度、製品予算、効果の実測は実装・測定時に決め、未決を通常切断の不成立やPhase 6着手の障害にしない。

## 8. 世代管理と非同期制御

Wave、実Hit、そこから派生した仕事・成果物は既存SlashIdで同じSlashへ対応付け、別Slashや再利用された格納先の古い結果と混同しない。整数幅、採番方法と内部参照保護は実装詳細とする。次の発射で以前の生存Waveを失効させず、Wave Expireだけで受付済み切断を失効させない。受付済み処理の採否は以下の対象・Pending・Transaction／Branch authorityに従う。

ObjectGenerationはObject全体のPrediction・Cache・Reset／退役・Trace・Maintenanceの粗い識別へ残すが、別LogicalFragmentだけの更新を通常切断の単独Reject理由にしない。通常受付は4.2、非命中所有変更は7.9に従う。

Physics CommitはSource生存性、SourceのActive CutOperationId、当該Transactionが持つ物理系譜・所有変更authority、既存Object生存性と4章の共通Player終了境界で照合する。自らのSource Final OwnerからProvisional構成への置換と通常のpose／速度／Sleep変化は失効理由にしない。別Transaction・Maintenance・GC・外部SystemのOwner／Shape／Constraint構成変更でauthorityを失った仕事はStale回収し、現在Sourceを退役させない。個別物理成立不能のAbortとは区別する。

Geometry Work／Commitは有効Pending、直前祖先Geometry Commit、当該Branchの生存子孫・表示・後続Work等の読者、表示を古いGeometryへ戻さないことを照合する。子孫切断や別FragmentのObjectGeneration更新だけで祖先を失効させず、履歴完成だけを継続理由にしない。Sourceが正常置換された後も必要な祖先Geometryを採用できる。非再利用LogicalFragment ID、Active CutOperation、既存Owner identity／世代、Pending親子関係で閉じ、新しい公開Fragment世代やObject終了latchを追加しない。

投機採用はBaseObjectGeneration、SlashId、LogicalFragmentRef、SlashFrameとPose／面・入力Snapshot、必要なら既存Anchor情報を照合する。Object全体を前提にしたPredictionは粗い世代不一致で不採用にできるが、受付後の通常Transaction／Geometryは上記の局所authorityへ従う。Jobを強制中断せず完了後に回収する。

物理PendingはTransaction内のWork依存であり、Logical Publication後にはPhysics Pendingを残さない。固定／動的は所有Anchorから導出する。Geometryの外部状態変更は4.5.6のGeometry Commitに従う。

非同期成果物は完成後に上記の世代・前提・authorityと照合して適用し、古い成果物は適用せず回収する。一度適用した成果物を二重Commitせず、Job／Reader／Physics Step等が参照中の資源を早期解放しない。Job実行、成果物採否、公開、資源寿命の表現を共通状態enumへ統合せず、各Subsystemの境界に従う。

CutBoundaryRecordはGeometry Commit時に実在Boundaryだけを後着公開し、CutBoundaryId、CutPlaneId、正負の別子へのSide参照、必要frameと作成時世代を持つ。面／frame identityは19.5.1の採用面へ結び、元SourceSlashPlaneと混同しない。再切断に必要な履歴は維持するが退役Geometry／Actorを強く所有せず、Boundary完成をLogical Publication条件にしない。

Kerfは0、固定側のOffset／Impulseは0とする。固定を理由に仮描画を省略せず、同一位置の正負Capを通常Colorで常時両面描画しない。

## 9. リグ付き人形の切断

関節をフリーズできるため、切断時点でアニメーション世界から静的破壊世界へ移送する。実際の現在姿勢とボーン行列をスナップショットし、4.5.2の同期経路または有効な先行準備から共通VP入力へ合流して、同じ採用Poseの即時clipと一般プロップと共通の切断処理を使う。Phase 4.52は先行準備なしの同期経路で基本切断を完了し、Jobベイクの導入採用時だけPhase 4.72で先行成果物の再利用・未完成／成果物不採用時の通常経路を統合する。表示開始時に残る準備費用と表示開始フレームは4.5.2に従う。

- 切断対象として登録する身体・衣服・髪の各Skinned Componentは6章の共通入力契約を満たし、命中時の同じPoseと切断平面から一度だけ切断・Cap生成して表示とStencilへ使用する。開放装飾を身体の別Shellで救済せず、非切断と明示した部品はこの処理へ入れない。

- 物理はボーン単位の簡略Convex／カプセル群を固定姿勢へ変換してから分類・クリップする。

- 関節は廃止し、同じ論理破片に属するColliderをCompound Colliderとしてまとめる。

- 初速はルート速度、角速度、可能なら直前のボーン運動、切断分離速度から構成する。

## 10. アセットとアートパイプライン

### 10.1 ビジュアル方針

- Synty POLYGON City Packを都市、建物、車、看板、小物、人物の基盤として採用する。

- 限定カラーパレット、2〜3段階のセル陰影、距離調整可能な輪郭線を全素材へ適用する。

- 標準テクスチャの印象を弱め、顔、看板、ステッカー、グラフィティを独自化する。

- 小物はVRで輪郭と相互作用可能性が読み取れるよう、細部よりシルエットと色面を優先する。

- 特定作品名を制作指示の最終仕様にせず、一般化した視覚要素として管理する。

### 10.2 切断可能アセットのRuntime標準表現

表示の実行時表現は4.5に従い、読み込み時Unity Meshから切断時VPへ移行する。必要なGeometry参照と幾何処理用のTopology情報を保持し、描画用Descriptor、論理・物理所属と区別する。実切断の出力・転送・公開は4.5.6に従う。Fixture保存形式はRuntime AoS表現と独立した実装詳細であり、VP導入だけで形式改訂や一括再生成を要求しない。

7.9の後続任意処理は本章の既存Geometry／Cell／Anchor入力を再利用し、専用Asset分類、Shard、逆被覆表、全Asset再生成、Schema改訂を要求しない。

| 層 | 用途 | 品質契約 |
| --- | --- | --- |
| 共用Cut Geometry | 通常表示、最終破片、即時Stencil Volume | 6章の閉鎖・edge／vertex manifold・局所winding整合済みTopology。複数submesh、Disconnectedな閉Component、Component間のIntersection／Overlapを許容 |
| 幾何Topology Metadata | 共用Geometryの切断・Cap生成 | 6.2のTopology／属性seam対応。切断中の作業構造と区別する |
| Physics Proxy | 接触とConvex切断 | 少数の低頂点Convex／Compound。各Convexは有効な閉凸形状だが、Compound内の相互Overlapを許容 |
| 固定Anchor入力 | 所有者全体の固定 | 固定を設定する入力だけが点Anchorの初期Logical Convex CellとfiniteなFrame内local位置を明示する |

Blender側ではTransform適用、原点・単位統一、共通Material、三角形化、共用Geometryの閉鎖・manifold・winding検証、Compound Physics Proxy生成、Unity書き出しをプリセット化する。幾何切断・Asset処理に必要なComponent情報だけを保持し、部品間の接着情報は生成・保存・読込み・検証しない。入力の食い込みへBoolean Unionや全体inside-outside検証を要求しない。固定を設定する場合は点Anchorの初期Cellとfiniteなlocal位置を出力し、未指定の固定や対応を推測しない。

表示・実Cap・Stencil・次回切断は同一の共用Cut Geometry正本を参照する。正本は4.5.1に従い複数Geometry参照で構成してよく、役割別に並行更新・切断するUnity Mesh、基底、派生物、適否、Cacheを作らない。4.5.1のGPUコピー・作業構造・旧世代・投機Snapshot・未採用VPは許容する。製品Asset Schema、Preprocess Cache、Build、Runtime FallbackはStrict Solid用の参照や生成物も持たない。

#### 10.2.2 Phase 0.2の採用Fixtureと凍結

Phase 0.2の要求範囲は凍結する。先行branchから採用する少数Geometryと用途対応をmergeし、後続実装が読み取って利用できる時点で完了とする。表示切断、Physics Cook、正しさ確認の用途を区別し、公開可能なSynthetic入力と非公開のLicensed入力を分離する。製品Asset全体の互換性、製品Preprocessorの完成、Strict Solid、実Cook成功、Runtime品質は保証しない。製品への登録は6.2／7.2の入力条件に従う。

先行branchの有用な実装・Schema・Golden・Preset・Script・選抜結果・関連データは、そのまま採用できる。同branchのDESIGN.md、Architecture Candidate、実施計画は本書の正本へ採用せず、競合時はmainの簡素化後DESIGN.mdを優先する。形式統合、再canonical化、migration、全再生成、再選抜、旧quota・カテゴリ網羅性の達成や旧監査の再実行をmerge条件にしない。

検証用形式と手順は17章の実装詳細とし、採用済み入力を現在の用途で利用できることだけを引き継ぐ。旧Structural Slab系列を含め、旧形式のReader、同じ手順での再生成、旧採否の再評価を将来まで要求しない。既存実装は実際の依存に応じて利用し、将来のFixture追加・置換はその時点の目的に応じた別作業として扱い、旧Phase 0.2を再開しない。

#### 10.2.3 Phase 0.21 Reference Asset Intake

先行独立研究、日々のAsset作業、外部ツール等から、利用許可のある完成済みAssetをテスト・計測へ受け入れる開発用の入口とする。Datasetは参考実行のサンプリング母集団であり、全件の実行義務や製品への適合を表さない。Phase 0.2の凍結・採用入力は維持し、同Phaseの再開・形式移行を要求しない。

**受入れと内容識別。** 正本は書出し済みFBXと読込みに必要なTextureの実体とし、元の`.blend`、Recipe、Script、研究repository、作業メモ等は任意の参考情報とする。生成工程の本隊移植・再生成・byte一致・由来監査や、研究側の可変な作業treeへの依存を要求しない。実体をSHA-256で追跡し、FBXと必要Textureの内容・組合せ・対応関係をAsset SHAで識別する。DatasetはAsset SHAの集合を正本とし、その集合から内容revisionを識別する。外側の配置・表示名・参考情報だけの変更は内容更新にしない。再試験用に保持するrevisionは参照実体も保持するが、全履歴の永久保存や旧形式Loaderの維持は要求しない。

**配置と所在解決。** 初期配置先は非公開Asset repositoryの`Working/Phase0.21`とする。directory名から用途・Recipe・版を推定せず、登録情報とSHAで対象を解決する。結果から実体の所在を引き、存在する場合は隣接する原資料等も容易に開けるようにする。登録外の周辺ファイルを暗黙の入力にしない。登録・索引・保存形式・SHA算出方法・操作UIは実装詳細とし、公開／非公開と利用許可の境界は10.8に従う。本入口への登録自体は製品採用や共有許可を与えない。

**参考実行。** 明示指定したDatasetまたはAssetについて、利用側Harnessが入力準備・適否判断・対象処理・サンプリングと実行量を決める。各Runは、使用したDataset revisionまたはAsset SHA、実際の入力・処理・条件、成否と得られた観測値を利用側Harnessの実施記録へ残し、実行途中のDataset更新を混入させない。失敗・入力不適合・未対応・未実行を成功とせず、比較可能な項目だけ固定入力の結果と比較する。少数Assetを限定した処理へ通すことから始め、全Assetと全試験条件の直積、完全巡回、全Consumer対応を要求しない。FBXから必要な数値入力・Topology等を得られるかは利用側で確認し、見た目の一致だけを同じ入力の証拠にしない。入口で推測修復せず、既存のGeometry／Physics入力条件とRuntime／オフライン検証の分離を維持する。

**標準実行との境界。** 参考Datasetの有無・更新・結果は、標準回帰・Benchmark・CIの対象・集計・合否やPhase完了条件を自動変更しない。標準実行は本入口を暗黙に探索せず、更新型Datasetの将来revisionへ追随しない。参考実行の未実施・失敗・未観測・乖離解消・結果閲覧を進行Gateや自動の未完了負債にしない。ただし、調査で確認した既存必須要求への違反は、その既存基準で扱う。発見だけでDataset全体を必須回帰へ昇格しない。既存要求を確認する個別AssetやSynthetic回帰ケースの追加・差替えは、対象を固定して通常作業として行え、個別の人間承認を要求しない。必須対象・評価基準・Phase完了条件を拡大する変更だけを人間判断とし、本書の契約を変える場合は改訂する。

### 10.3 Blenderヘッドレス前処理

Blenderを手作業用DCCだけでなく、ライセンスAssetをローカル変換するバッチプロセッサとして使用する。システムに既存のBlenderやPATH上の`blender`には依存せず、プロジェクト専用の固定版を明示パスから`--background --factory-startup --python --python-exit-code 1`で起動する。PythonスクリプトとAsset別Recipeから共用Cut Geometry、切断用Topology、Compound Physics Proxy、検証レポートを生成する。製品用Strict Solidは生成しない。

```text
Licensed Display Asset
  -> Import／Transform・単位統一
  -> Closed Component抽出
  -> Component単位の閉鎖修復・簡略化・三角形化
  -> 共用Cut Geometry／Compound Convex検証
  -> ローカル生成物とレポート出力
```

#### 10.3.1 専用Blenderの配置とバージョン固定

Windowsでは公式Portable ZIP版を使用し、初期固定版をBlender 4.5.12 LTS Windows x64とする。既存の古いインストール版は更新・削除せず共存させる。実行側は常にリポジトリルートから解決した専用`blender.exe`の絶対パスを使用し、PATH、ファイル関連付け、ユーザー既定アドオンに依存しない。

`--factory-startup`を指定して個人設定の影響を排除する。必要な設定、Geometry Nodesテンプレート、Pythonスクリプトはリポジトリ側を正本とする。版は`4.5`のような系列指定ではなく`4.5.12`まで固定し、更新は互換性検証と生成Cacheの一括無効化を伴う明示的な設計変更として扱う。

```text
Tools/
  Blender/
    4.5.12/
      blender.exe              # ローカル配置、Git対象外
BlenderPipeline/
  blender-version.json         # 版、公式URL、SHA-256、platform、architecture
  bootstrap.ps1                # 取得、ハッシュ検証、展開
  run-preprocess.ps1           # 専用exeを明示パス起動
  scripts/                     # Python前処理
  recipes/                     # Asset別Recipe
  templates/                   # ライセンスAssetを含まないテンプレート
Generated/
  CutAssets/                   # ローカル生成物、Git対象外
```

Blender本体は約400MB規模のため公開Gitへ含めない。`blender-version.json`とBootstrapだけをコミットし、初回セットアップ時に公式配布ZIPを取得して公式SHA-256と照合後に展開する。オフライン環境では同一ZIPを手動配置できるようにし、Bootstrapは既存ファイルの版とハッシュが一致すればネットワークを要求しない。CIも同じManifestを使用し、許可された環境だけが取得する。

起動ラッパーは`--version`の結果がManifestと一致しない場合に処理を開始せず失敗させる。生成レポートにはBlender完全版、実行ファイルのハッシュ、OS／architecture、Script版、Recipe Hashを記録する。

### 10.4 自動処理とRecipe

変換、結合、Voxel Remesh、簡略化、検証、書き出しは自動化する。一方、ドア、窓、中庭、車庫、トンネルなどの意味を形状だけから完全には判断できないため、Asset別のPreprocess Recipeを正本とする。

Recipeは少なくとも以下を記述する。

- `FillAll`／`PreserveCavity`／`SeparateParts`／`RenderOnly`の処理モード。
- Solidへ含める／除外するObjectまたはCollection規則。
- 窓、ドア、底面などの封鎖面または封鎖規則。
- タイヤ、窓、看板、装飾などの別部品指定。
- 建物のチャンク境界。
- 製品用FixedSupportAnchorを持つ対象では、各finite点の初期Logical Convex Cell、Fragment Physics Frame内local位置。未指定の支持または対応をRuntimeで推測しない。
- Voxel Size、Adaptivity、簡略化上限。
- 期待Bounds、体積範囲、最大面数。

単純な家具は無設定または共通Preset、車と建物は初回だけRecipeを調整し、以後は無人で再生成する。結果は`Success`、`NeedsReview`、`Failed`に分類し、警告だけで不正なSolidを採用しない。

### 10.5 Component閉鎖修復とGlobal Solid研究の分離

製品前処理は、切断対象Componentを6章の共用入力契約へ適合させるか切断対象外とし、Component間のBoolean Union、全体inside／outside判定、Generalized Winding Number、Voxel内部充填からのStrict Solid再構成を行わない。開放Boundary、局所winding不整合、edge／vertex Non-manifoldをRuntimeで救済せず、Self-intersectionや相互に食い込む別の閉Componentは共用Geometryとして許容する。標準Runtime、製品Asset Preprocessor、代表Assetの合格条件、高品質Fallback、Cache SchemaのいずれもStrict Solidを前提にしない。

Voxel／SDF Union、内部Flood Fill、制約付きSurface Projection、Global Watertight化、厳密な体積／inside-outside検証は`Future Research: Global Solid Reconstruction`へ移す。この研究はPhase 5.5以前の依存、完了条件、製品Fallbackではなく、開始時期も未定とする。研究成果を将来採用する場合は新しいArtifact種別、Profile、性能・品質Gateを別の設計変更として追加し、現在の共用Cut Geometry／Physics契約を暗黙に強化しない。

プログラムまたは固定版Blenderスクリプトで箱、柱、凹形状、複数Shell等を生成する`Synthetic Watertight Test Fixture`は製品Strict Solidとは別物として維持する。これは切断Kernel、Cap Loop、反復切断、Cook Benchmarkの既知正解入力であり、ライセンスAssetから生成せず、製品Asset Preprocessorの出力でもRuntime同梱物でもない。異常系Fixtureには意図的なBoundary、Non-manifold、自己交差、重複面等を持たせてよい。実Assetが偶然同じGateを満たしても補助比較へ使えるだけで、代表Assetでの成功やPhase 5.5完了を要求しない。

### 10.6 BlenderテンプレートとPythonの分担

公開可能な空の`.blend`テンプレートにGeometry Nodes、入力Collection、封鎖Collection、出力Collection、検証用設定を保持できる。Pythonはファイル入出力、Recipe適用、パラメータ設定、処理実行、検証、終了コードを担当する。これにより、失敗AssetだけをGUIで開いて中間状態を確認できる。

Voxel RemeshではUVや元の頂点属性を保持する必要はない。製品用Global Solidは生成しない。断面はTexture Mapping用UV展開や断面Textureへ依存せず、Runtimeでは5.3のraw UV markerで固定断面色を選ぶ。Blender側で断面用UV展開、断面Texture Bake、色別Geometryを生成しない。元Assetの負UVに対する修正も要求しない。

### 10.7 キャッシュとUnity連携

Unity Editorから`Build Licensed Cut Assets`、`Rebuild Selected Asset`、`Validate Generated Assets`を起動できるようにする。自動Asset Importのたびに全件を再生成せず、以下からCache Keyを作る。

```text
Source Asset Hash
+ Recipe Hash
+ Preprocess Script Version
+ Blender Full Version
+ Blender Executable Hash
+ Platform／Architecture
```

Cache Keyが変化したAssetだけを再生成する。大量処理の並列化はBlender内のPython Threadではなく、メモリ予算を設定した複数のヘッドレスBlender Processで行う。

製品用点Anchorの初期所属Cellとlocal位置はRecipe Hashへ含め、別のAnchor Cache、Runtime再探索結果または頂点配列indexをCache Keyへ追加しない。Phase 0.2の採用入力に製品用Anchorを要求せず、Cache・形式の扱いは17章に従う。

### 10.8 公開リポジトリとライセンス境界

変換コード、汎用Recipe Schema、ライセンスAssetを含まないテンプレート、検証コード、Blender版Manifest、Bootstrapは公開する。Blender本体、Synty／Poly Pro Universeの入力Asset、`.unitypackage`、付属`.meta`、生成された共用Cut Geometry、Physics Proxy、加工済み断面素材は公開しない。`/Tools/Blender/`と`/Generated/`をgitignoreし、公開履歴への混入をCIで検査する。

Synty POLYGON City Packの購入原本は、公開Unityリポジトリと分離した非公開Git LFSリポジトリ`C:\Users\%USERNAME%\src\zantetsuken-assets-private`で管理する。2026-08-26時点で、`Vendor\Synty\POLYGON_City\v5\Original`へ`POLYGON_City_SourceFiles_v5.zip`と`POLYGON_City_Unity_2022_3_v1_12_4.unitypackage`を格納済みであり、両ファイルはLFS対象である。ダウンロード元と格納先のSHA-256一致を確認済みとする。

非公開リポジトリへのアクセスは各Assetライセンス上の許可を持つ開発チームだけに限定する。購入原本は変更せず保存し、展開したFBX／Texture、Phase 0.2のEarly Licensed Fixture／Asset対応表、加工済み共用Cut Geometry、Physics Proxyなどのライセンス派生物も公開Git履歴へ入れない。公開リポジトリから参照する場合も、公開Submodule、公開Release、公開CI Artifact、共有Cacheを経由してAsset本体を配布しない。

公開CIはPlaceholder Assetで前処理と切断ロジックを検証する。Syntyを用いる変換と製品ビルドは、許可されたローカル環境または限定private runnerだけで実行し、公開Artifactと共有Cacheへ生成物を残さない。

## 11. モーション方針

モーションは原則として既製HumanoidクリップをUnityでリターゲットする。NPCはIdle、Walk、Run、Turn、Startled、Run Awayを初期最小セットとする。Phase 4.70のV1予測対象NPCでは頭・胸の視線、腕IK、Foot IK等のプロシージャルPose Layerと左右反転を現在表示と未来評価の双方で無効化し、Catalog登録済みClip Poseだけを正本とする。これらのLayerや反転は、入力、weight／mode、適用順、世代、Evaluator Identityをimmutableな共通Pose Evaluation Inputへ追加し、現在／未来Backendが同じ処理を行えるようになった後だけ再導入する。切断時は現在姿勢を固定して物理へ移行するため、切断方向ごとの専用死亡モーションは作らない。

NPCのCurrent／Future Animation State、Clock、Clip選択、Transitionはゲーム側`AnimationPlannerV1`を正本とし、Animator／AnimatorControllerから読み戻さない。Unity Animatorは必要ならHumanoid RetargetingとPose出力先として残すが、controllerなしPlayableまたは他のPose Evaluatorへ交換してもMobPlanと切断Predictionを変更しない。命中時にはBackendが実際に表示したBone Poseをスナップショットし、予測との差を検証してから静的破壊世界へ移送する。

- NPC：MixamoまたはQuaternius Universal Animation Library系の既製モーションを候補とする。

- プレイヤー：刀と手はVRコントローラーの実測姿勢を使用し、必要なら腕だけTwo Bone IKで補間する。

- 全身アバターは初期段階で必須にせず、手袋と刀だけでも体験検証を可能にする。

- V1予測対象NPCの群衆多様性はClip、再生速度、位相だけで作り、左右反転と視線対象の変更は行わない。反転または視線対象による多様化は、それぞれのimmutableな明示入力を現在／未来の共通評価へ導入した後段だけで有効化する。

## 12. 決定事項

| ID | 領域 | 決定 | 状態 |
| --- | --- | --- | --- |
| D-001 | エンジン | Unityを採用し、切断系のみ独自サブシステム化 | 確定 |
| D-002 | XR／描画 | Unity 6.3 LTS 6000.3.22f1 + OpenXR + URPを初期構成とする | 確定 |
| D-003 | 即時応答 | 必要なベイク・VP変換後にGPU仮表示を先行し、共用VP Geometry／Convexを非同期更新（4.5） | 確定 |
| D-004 | 仮断面 | clipで分離、Stencilで仮断面、最終的に実断面へ置換 | 確定 |
| D-005 | 非同期整合 | ジョブ結果を世代番号で無効化・コミット制御 | 確定 |
| D-006 | 人形 | 切断時に姿勢固定し、静的Mesh／剛体破片へ移行 | 確定 |
| D-007 | アセット | Synty POLYGON City Packを主素材に採用 | 確定 |
| D-008 | アート | 共通セルシェーダ、輪郭線、限定パレット、独自看板で統一 | 確定 |
| D-009 | モーション | 既製Humanoidモーションをリターゲットし、IKで補正 | 廃止：Mob Prediction対象とそれ以外のIK scopeをD-136で分離 |
| D-011 | 対象環境 | 初期製品スコープをPCVRとし、Quest単体対応は当面除外 | 確定 |
| D-012 | 性能目標 | 実アプリの両眼描画90fpsを基準とし、再投影を常用前提にしない | 確定 |
| D-013 | 開発順序 | 非VR PoCと性能評価を先行し、最小XR確認、Slash UXの段階実装と実機調整、選択済みVP経路のXR／Hybrid Clip／Stencil能力確認を15章の依存順で進める。新設Phaseは実装層や成果物契約を増やさない | 人間承認済み、2026-09-11 |
| D-014 | 検証HMD | Quest 3Sを有線Quest Linkで初期PCVR検証に使用 | 確定 |
| D-015 | 攻撃演出 | 有限速度で飛翔する斬撃波を19.1.8の表示専用VFXで描く。接触時の即時分離を目標とし、準備待ちの扱いは4.5.2に従う | 人間承認済み、2026-09-13。VFXの形状・視覚的接触の許容は19.1.8に従う |
| D-016 | 先行計算 | 到達猶予で未来姿勢、表示／Stencil共用VP Geometry、Convex切断を投機評価 | 確定 |
| D-017 | 未来評価 | 未来イベントDAG、世代・前提検証、Commitから成る評価器を実装し、Ready Workを4.4の共有Dispatchへ渡す | 確定。Schedule前取消、完了後採否と各Subsystemの公開境界を維持する |
| D-018 | 自由飛行剛体の直接予測 | 剛体の未来運動は19.3の直接予測Gate内だけをPhase 4.54でO(1)予測し、対象外・前提不一致は4.51へ進む。静止／姿勢固定の4.53とAnimation／MobPlan評価は維持する | 人間承認済み、2026-09-13。接触・転動の先行率改善を製品範囲から外し、命中後の処理費用・準備待ちは現在状態経路で引き受ける |
| D-019 | 文書管理 | 本Markdownを唯一の設計正本とし、DOCXは使用しない | 確定 |
| D-020 | 観測基盤 | 21章のProfiler・Trace・Captureで性能、因果関係、対応画像を確認する | 確定。検証用形式と手順は17章の実装詳細とする |
| D-021 | ログ方針 | 状態遷移をenumと整数IDで記録し、高頻度の文字列生成とDebug.Log連打を避ける | 確定 |
| D-022 | Asset前処理 | 固定バージョンのBlenderをヘッドレス実行し、Python＋テンプレートで一括変換 | 確定 |
| D-023 | Global Solid生成 | 製品用Strict Solid生成は廃止する。Voxel Union、内部充填、Global Watertight化は将来研究であり、Runtime、Asset Preprocessor、Fallback、代表Asset合格条件へ含めない | 廃止／研究隔離 |
| D-024 | 例外処理 | 全自動判定に依存せず、Asset別Recipeで部品分類、封鎖、空洞保持、チャンクを指定 | 確定 |
| D-025 | 公開Repo | 変換コードと空テンプレートは公開し、Synty入力と派生生成物はローカル限定・gitignore対象 | 確定 |
| D-026 | 開放Mesh修復 | 境界Loop封鎖、Solidify、Voxel Closing、内部充填を段階的に自動実行 | 確定 |
| D-027 | 意味的開口 | 窓・入口・中庭など形状だけで判断不能な開口はRecipeまたはNeedsReviewへ送る | 確定 |
| D-028 | Blender実行環境 | 公式Portable ZIP版Blender 4.5.12 LTSをプロジェクト専用に配置し、ManifestとSHA-256で完全固定する | 確定 |
| D-029 | Unity実行環境 | Unity Hub管理領域のUnity 6.3 LTS 6000.3.22f1を使用し、ProjectVersion.txtで完全固定する | 確定 |
| D-030 | Repository構成 | 専用Repo直下をUnity Project Rootとし、ユーザーパスは%USERNAME%で匿名化する | 確定 |
| D-031 | Unity CLI | PoC初期は使用せず、固定版Unity.exeのbatchmodeを基準にする | 確定 |
| D-035 | 刀姿勢入力 | OpenXR Grip Poseと持ち手別GripToKatanaOffsetで刀の位置・回転を決定 | 確定 |
| D-036 | 片刃判定 | 刀身軸方向を除いた運動とEdgeDirectionの緩い内積Gateで、峰側の復路を除外 | 確定 |
| D-037 | 刃筋難度 | SideNormal横滑りや厳密な角度を不合格条件にせず、遊びやすい判定を優先 | 確定 |
| D-038 | 刀の衝突 | 刀へ物理反発Colliderを付けず、有効な論理Sweep以外は全オブジェクトを素通り | 確定 |
| D-039 | 追跡異常 | Pose無効時は未発射の振り候補とSample履歴を破棄し、再追跡直後の見かけ速度からSlashを生成しない | 確定 |
| D-040 | Unity更新 | プロジェクトを作り直さず、Hubで新旧Editorを並存し、Gitアップグレードブランチ上で変換・回帰検証する | 確定 |
| D-041 | Unityディレクトリ | Unity Project Rootは1つを正本とし、版別の恒久コピーは作らない。同時比較時だけ兄弟Git worktreeを使用する | 確定 |
| D-042 | モブ未来計画 | Unityの通常AIとは別にMob Future Plannerを設け、遠距離モブほど長い未来区間を副作用なく計画する | 確定 |
| D-043 | MobPlan世代 | MobPlanへPlanGenerationと前提条件を付け、介入や経路変更時は旧計画と依存する投機結果を無効化する | 確定 |
| D-044 | AI LOD | プレイヤーが介入可能になるまでの最短時間を基準にNear／Mid／Far／Dormantの計画精度と更新頻度を切り替える | 確定 |
| D-045 | 遠距離モブ | Far／Dormantモブはキネマティックな経路と`ExplicitAnimationStateV1`全体を先行確定し、切断計算の猶予へ利用する。粗い時空間予約は初期成立条件に含めない任意の後段拡張とし、D-134の段階導入に従う | 技術検証付き確定 |
| D-046 | MobPlan Commit | 未来モブ姿勢に基づく切断成果物は、実命中、ObjectGeneration、PlanGeneration、姿勢許容誤差の一致時だけCommitする | 確定 |
| D-052 | 候補範囲 | 候補範囲は投機専用で、実HitはSlashWave Segment Sweepだけで確定する。有限包絡がなければ全Hitを含まない先行準備範囲を使い、範囲外は現在状態経路へ進む | 確定。19.1.10／Phase 4.53 |
| D-059 | 映像キャプチャ段階導入 | PoC初期はUnity側の選択的キャプチャを使用し、切断PoC成立後にOpenXR API Layer方式を追加検証する | 確定 |
| D-060 | PoC録画負荷 | 21.7／21.15の選択的Captureを使用し、非待機とbounded資源を維持する | 確定。早期の録画条件は実装詳細 |
| D-061 | OpenXR Capture責務 | Windows PCVRのD3D11（D-137／21.7.4）だけから開始し、Projection Swapchain ImageをRelease前に専用GPU TextureへCopyしてTraceと同期する | 技術検証付き確定 |
| D-062 | 映像の証拠範囲 | Projection Captureはアプリ提出画像の証拠とし、Meta compositor、Reprojection、レンズ補正、Quest Link圧縮後の最終HMD像は保証しない | 確定 |
| D-063 | Capture相関 | 撮影した画像とFrame・対象・処理を対応付ける。OpenXR固有の相関は21.7に従う | 確定。Record形式は固定しない |
| D-065 | Capture Fail Fast | 実行時のGraphics API、Format、Sample Count、Array Size、Layer、SubImageが固定Profileと違う場合は録画だけを停止し、構成差をTraceする | 確定 |
| D-066 | Capture環境記録 | 21.7.6に従い測定環境を識別し、環境差を同一条件として比較しない | 確定。保存形式・照合方法は実装詳細 |
| D-067 | cooking非同期化 | Bake／cookingは即切断表示・初回仮運動のクリティカルパスから外す。表示は4.5.2の準備後に開始し、実Geometryは4.5.6の現在の参照・frameと転送条件で公開する。固定側も描画するが動かさない | 確定 |
| D-069 | 物理分裂Commit | Bake済みConvexの完成後、物理ステップ境界で左右Rigidbodyへ分裂し、親の線速度・角速度から各重心位置の速度を継承する | 廃止（D-132でProvisional生成時の分裂とFinal handoffへ置換） |
| D-070 | Cooking Profile | 初期製品は7.3の単一ProfileをBakeとColliderへ同一指定する | Phase 4で構成を選ぶ |
| D-074 | 全体低重力 | 空中斬り猶予を増やすため世界全体を低重力にし、PoC仮値を約0.5Gとする。周辺物理値は先に作り込まずプレイ後に判断する | 技術検証付き確定 |
| D-075 | 重力一元管理 | `WorldPhysicsProfile`を正本とし、Unity Physics、未来予測、解析軌道、VFXへ同じ重力を供給してRunごとに記録する | 確定 |
| D-076 | 即時Shadow | 即時切断中は同じper-instance clip／分離Offsetを適用した両面ShadowCasterで影を近似し、Shadow Map用Stencil断面は描かない | 技術検証付き確定 |
| D-077 | Shadow Batch | Shadow描画をStable片面群とPending両面群へ分け、切断平面は固定長Instance Recordで渡して平面値・切断数によるDraw分割を避ける | 技術検証付き確定 |
| D-078 | 有限仮キャップ | 即時キャップ板をローカルOBBと切断平面の3～6頂点交差多角形から生成し、他のTemporary Render Boundary半空間でclipしてからStencilで実輪郭へ制限する | 確定 |
| D-079 | Stencil Color割当て | 左右眼いずれかで保守的な可視Cap Boundsが重なる非互換対象を通常Colorでは分離する。Conflict Graphは論理モデルに限り、全Graph構築、全組合せ走査、Greedy Coloring、stableなColor番号を要求しない。`MaxStencilColors`へ収まらない対象は最後のColorへ統合する | 技術検証付き確定 |
| D-080 | Stencil互換Group | 全World Cut Plane、Side／半空間、Offset、Cap描画状態が一致し、6章の共通入力Gateに合格したGeometryは向きと符号を保存したまま同じStencil Colorへ加算できる。Maskの意味は`sum(W_i) > 0`であり、正逆相殺による欠落を許容して幾何学的Unionを保証しない | 技術検証付き確定 |
| D-081 | 両眼Cap可視性Cull | 論理破片×切断面ごとに5.6のFacing条件で判定し、全Capが除外される互換Groupは彩色前にStencil Clear／Volume／Cap処理から除外する | 技術検証付き確定 |
| D-082 | Stencil競合領域 | 集計後に`S != 128`となるResidual Stencil Supportを可視Cap Boundsで保守的に包み、Raw Stencil書込みの途中重なりは競合としない。各眼でOBB投影または可視Cap Boundsのどちらかが非交差なら通常Colorの共有を許可する。実StencilからSupportを検出・監視しない | 技術検証付き確定 |
| D-083 | バックグラウンド実行基盤 | CPU幾何・予測計算はC# Taskの大量発行ではなくJob System＋Burstを基本とし、Task／AwaitableはI/Oと非同期制御へ限定する。Unity Objectの適用とGeneration Commitはメインスレッドで行う | 確定 |
| D-084 | Owner単位Cut/Cook | 7.2のmanaged Job内Burst static kernel、Main ThreadのMesh適用、managed Jobの`Physics.BakeMesh`、Final Commitへ分担する。数値内部工程のJob分割を必須にしない | 人間承認済み、2026-09-12 |
| D-086 | Native採用Gate | Unity Built-in 3D Physicsの`Physics.BakeMesh`を製品経路の正本とし、Native PhysXを製品依存へ含めない。T-076を含む製品統合実測でUnity経路が性能要件を破り、Unity側で解消できず、別途承認したNative統合Prototypeが成立した場合だけ、切断破片のQuery／接触／Scene同期を含む物理経路の部分置換を再検討する。Cook時間の倍率差だけでは置換しない | 確定 |
| D-087 | Voxel後Surface Projection | 製品前処理としての採用を取り消し、制約付きSurface ProjectionはT-071の`Future Research: Global Solid Reconstruction`へ隔離する。Phase 5.5以前の依存、完了条件、製品Fallbackにしない | 廃止／研究隔離 |
| D-088 | 閉Topologyの自己交差契約 | Topological Watertightと自己交差のないGeometrically Valid Solidを区別する。自己交差はD-117の共用Geometryで許容する。個々のPhysics Convexには自己交差を許可しないが、Compoundを構成する別Convex同士のIntersection／Overlapは許容する。Geometrically Valid Solidは合成試験と将来研究だけの用語とする | 確定 |
| D-090 | ライセンスAsset保管 | Synty購入原本と派生物は、公開Unity Repoの兄弟に置く非公開Git LFS Repo`C:\Users\%USERNAME%\src\zantetsuken-assets-private`で管理し、許可されたチーム以外へ共有しない | 確定 |
| D-091 | 固定物体の切断 | 点FixedSupportAnchorの初期CellとfiniteなFrame内local位置を入力とし、現在Cellの集合だけを採用面とanchorEpsilonで正負／OnPlane両側へ継承する。所有者全体はAnchorがあれば固定、なければ動的とする。Shape共有・内接削減・再cook・反対側Geometry付属からAnchorを生成・移送しない | 確定 |
| D-092 | ゼロ幅切断 | Kerfは0、固定側のOffset／Impulseは0とする。両側固定でも通常の仮描画・実切断・Cap生成を行う | 確定 |
| D-093 | 切断痕の許容 | 固定側のCapと実Geometry境界の細い亀裂、輪郭線、線状Z-fighting、軽微なチラツキを許容する。通常Capは片面描画とし、正常な正向き閉Shellの面状Z-fighting・Cap欠落・混入は5.2の明示的例外以外では不具合とする | 確定 |
| D-094 | 状態の粒度 | 物理分裂、Geometry完成度、Work Result採否を独立管理する。所有者の固定／動的と表示の可否を分離する | 確定 |
| D-095 | Anchorと公開の実装順 | Phase 1で正負子・切断履歴の公開と単純Anchor配分を合成入力で確認し、Phase 4で実Convex・Actor・cookへ接続する | 確定 |
| D-098 | Pending Cutと描画集合 | 4.2／4.5.6に従い、受付時のPending Cutと、Final Physicsと一体のLogical Publication、後着Geometry Commitを分離する。実体化した面のTemporaryだけを回収する | 確定 |
| D-117 | 表示／Stencil共用Geometry | 切断対象の表示・実Cap・Stencil・次回切断は、閉鎖・edge／vertex manifold・局所winding整合済みの同一の共用Geometry正本を参照する。正本は4.5.1に従い複数Geometry参照で構成してよく、一度の切断・Cap生成で同じ不変条件を各出力へ継承する。Self-intersection、別Topologyの閉Component間のIntersection／Overlap、Internal／Nested／Coincident、全体反転、Runtimeの面積0 Triangleは許容する。入力不合格や不正出力を用途別Geometry、修復、簡易表示Proxyで救済せず、全Meshのinside／outside、自己交差、向き正規化をRuntimeへ追加しない | 確定 |
| D-119 | 正符号8bit Stencil | 専用8bit Stencil Byteを128へ初期化し、IncrementWrap／DecrementWrapで`S=(128+W) mod 256`を得て`S>128`だけを描画する。Winding上界の証明・検査・容量分割は行わず、範囲外の誤描画は5.2の品質例外とする。8bitを排他利用できない構成ではゲームを開始せず、部分Bitや代替経路を作らない | 確定 |
| D-120 | Convex由来の質量特性 | D-133でFinal正本とProvisional近似を分離したため旧契約を廃止する | 廃止 |
| D-121 | 非Union標準Asset表現 | 共用Cut Geometryと幾何Topology、Compound Physics Proxy、必要な点Anchorを標準とする。別TopologyをUnionせず切断・Capし、通常結果を正負二集合へまとめる。Capは向きを保存してsum(W_i)>0で描く。製品用Strict Solidは生成・常駐・Fallbackせず、Global Solid Reconstructionは将来研究に限る | 確定 |
| D-124 | Player非接触 | D-131へ置換 | 廃止 |
| D-127 | 即時Clip Plane予算 | D3D11 PoCは`SV_ClipDistance` 8面を性能／MSAA品質上の優先経路、Pixel Shader `clip()` 4面を固定Fallbackとし、RenderFragmentごとの容量超過面は5.2に従いPlane選択とColor／Depth／Shadowのclip制約から除外し、対応Stencil Volumeをsubmitしない。Operation公開前は受付済みPending Cut、公開後は当該Fragmentの未Commit面・Sideを候補とし、Pending Cut列とLogicalCutOperation公開列を受付の古い順に辿る未Commit祖先優先のdependency-closed prefixを左右眼と全Passへ共有する。Operation公開時も同じ受付位置と面を維持し、重複登録しない。新しい後発境界の即時表示より祖先半空間とSibling分離を優先し、論理履歴、背景Geometry／Physics処理、Cap Record集合を変更しない。Ignored VolumeのCap板は残してよく、最後の統合Colorでの可視化と誤Depthを5.2の品質例外とする | T-089付き確定 |
| D-130 | 有限予算Dispatch | 4.4の優先順、有限容量・フレーム予算、物理仕事の投入余地、非blocking回収と同一フレーム内の複数Dispatchを採用する。既存のQueue満杯時の同期進行例外は切断側に限定する | 人間承認済み、2026-09-13。内部方式は実装詳細とし、Schedulerの無割当と厳密なDeadline順の保証を外す。Managed allocationに伴うGC停止と、より近い締切の仕事が後になることを許容する。T-090で意味上の境界を確認する |
| D-131 | Player非接触の限定保証 | Player Body／Handとプロップ／破片はPhysX非接触とし、刀と斬撃波は論理SweepでInteractionする。人工移動はD-166の固定Occupancyへの候補次姿勢Overlap Rejectだけで扱い、実空間HMDはClampしない。Camera近傍視界保護（Fade／Vignette／Mask等）とその専用検出・状態・設定をPoC・初期製品から撤去し、代替機構を追加しない。Camera被り、未登録物体を含む内部視点、Near Planeでの内部面、5.2の仮Cap品質例外が隠されずに見えることを許容する。Camera overlapを切断・物理・Geometry Commit失敗へ昇格せず、そのためのCamera／物体の強制移動、完全Mesh検査、Stencil修復、Job取消・再発行、同期Fallbackを行わない | T-088付き確定。2026-09-10、人間承認により視界保護を撤去。通常視点のCap品質とStable Geometry契約は維持する |
| D-132 | Provisional Rigidbody／Collision Proxy | 7.1の短寿命Transaction内で旧Cooked Convexを再cookせず共有し、必要な2 Actorをall-or-none公開する。7.2の質量近似・初回速度継承・Actor優先Final handoffを使う | T-091で確認。成功・Abort・退役は7.1を正本とする |
| D-133 | Final質量正本とProvisional近似 | Final質量正本は受付時の親Rigidbody質量。7.2の採用Convex集合による近似と親質量保存を使い、重複控除・Boolean Union・Convex別配分系譜を要求しない。Provisional一時massはFinal正本にしない | 人間承認済み。T-085／T-091で確認 |
| D-134 | Mob軌道Cacheの段階導入 | `MobPlan.RootTrajectory`の初期生成方式を、副作用のない固定ステップ二相更新、Waypoint／Lane Desired Motion、固定長未来Sample Queue、再生補間、移動距離由来`ExplicitAnimationStateV1`、`PlanGeneration`による粗い全Plan／Group無効化とする。Nearは同じKernelをライブ実行し、Mid／Farは有効なQueueを主に再生する。初期成立条件へORCA、依存Graph、部分再計算、Flow Field、軌道圧縮、時空間予約を含めず、実測後の後段最適化とする | T-092付き段階導入 |
| D-135 | 明示Animation State正本 | Current／Future AnimationのState、Clock、Transition、Blendの意味上の正本をゲーム側`ExplicitAnimationState`とGlobal FixedStepIdとする。Animator／AnimatorController／AnimatorControllerPlayableは任意のPose出力／Preview／Legacy Backendへ降格し、内部状態の読戻しやController逐次rolloutを標準MobPlan経路にしない。現在表示、未来切断、CPU Skinningは同じ対象Stepへ解決済みStateから交換可能なPose Evaluatorへ分岐する。ClipのLoop／Clamp、canonical duration、Source Time写像は`AnimationAssetSetVersion`へ結合したCatalogを正本とし、V1予測対象ではプロシージャルIKを無効化する。全骨Poseの全Mob／全Sample先行保存は行わない | T-018／T-044／T-046付き確定 |
| D-136 | IK／Pose Layer scope | Phase 4.70のV1予測対象NPCはCatalog登録済みのリターゲット済みClip Poseだけを使い、Look、腕IK、Foot IK、左右反転等を現在表示と未来評価の双方で無効化する。補正や反転はLayer入力Snapshot、weight／mode、適用順、Generation、Identityをimmutableな共通Pose Evaluation Inputへ追加して全Backendで同じ処理を行える後段だけに許可する。VR Controller実測姿勢から表示するプレイヤー腕のTwo Bone IK等、Mob Predictionへ入力されないIKは別scopeとしてこの制限の対象外とする | D-009を置換。T-018付き確定 |
| D-137 | 後期OpenXR Capture構成 | Phase 4.8は21.7.4のWindows PCVR／D3D11固定構成を使う | 確定。Phase 0.11の短時間NVENC確認とは分離する |
| D-138 | 短時間NVENC確認 | Phase 0.11は21.15の実際に使用するCapture経路での複数Frame確認と、非待機・容量・寿命・故障分離で完了する | 人間承認済み。固定録画条件・内部方式・試験階層を維持する義務を外し、進行中実装はそのまま完了できる |
| D-148 | Phase 0.2の凍結 | 採用する少数Geometryと用途対応だけを引き継ぎ、10.2.2に従いmergeして利用できる時点で完了する | 人間承認済み。旧quota・網羅性・形式互換・同一手順の再生成を維持する義務を外す |
| D-149 | 剛体Local Plane実姿勢リベース | 19.5.1の自由飛行剛体だけ、予測Pose差の一致判定を世代・前提と実姿勢での面誤差Gateへ置き換える。攻撃SourceSlashPlaneは不変、命中前の仮Local Planeを命中時に一度だけ採用／Fallback確定し、7.6の受付判定と、受付済みのTemporary／Provisional／Stable／Finalへ共通使用する。面採否と受付判定はMesh／Collider Readyから独立し、未完成だけを理由に切断位置を変更しない。対象ごとの面差と固定点近似の未検出差を許容するが、Actor pose／速度を予測または命中Snapshotへ戻さず、自前B-repの包含・支持安全・世代検証を維持する | 確定。4.2／19.1／19.5の姿勢一致規則に対する限定例外。D-046のMob／Skinned契約は変更しない。O(1)直接予測式の標準採用とは独立 |
| D-159 | 可変長Trace導入 | Phase 0.12～0.14で21.16のWriter、Paged History、保存／読込みを段階導入する | 確定。旧形式の読込み維持は要求せず、実行時の所有権・非待機・容量境界は維持する |
| D-160 | コミット後の任意分割・物理GC | 7.9を正本として、確定後の単一平面による通常物体2個への追加分割と、共用面集合0の物理所有単位の寿命終了を独立した任意機能とする。既存の低優先Dispatchと非命中公開・退役を使い、追加分割は全体1未回収試行と入力別不成立抑止で管理する。通常切断・過去Commitを救済対象にせず、実行不能なら元物体を残す | 人間承認済み、2026-09-08。Phase 5.6／5.7は省略可能。許容事項は7.9.6、最小確認は7.9.7。実装・実測済みを意味しない |
| D-161 | 実行時表示表現と描画ロードマップ | 4.5の読み込み時Mesh、切断時VP、CPU AoS／Index正本とGPUコピー、範囲所有権、Published Vertex追記、退役Index再利用を採用する。通常切断は単一Index予約へ正負を直接配置し、物理採否に依存する再集約を行わない。必要転送と現在の参照・frameで表示公開し、CPU範囲Publishedは内部処理とし、Final Physics／Logical Publication後のGeometry Commitと祖先順Kernelは4.5.6に従う。Phase 0.9～0.94でStage 2まで比較する | 人間承認済み。準備・変換の表示開始負荷とGPU拡張STWを許容し、容量限界は4章の共通Player終了に従う。Stage 2高速化は必達でなく、効果が乏しければStage 1を採用できる。Phase 5.6のIndexコピー・新旧共存費用を許容する。実装・実測済みを意味しない |
| D-162 | 未来予測用SkinnedMesh Jobベイクの採否判断 | 4.5.2を正本として、Phase 4.65で不変Rig Poseから共通VP入力を生成する限定実装を同期経路と比較し、人間が導入の採否を決める。採用時だけ既存DAG／VPプールへの本体接続、4.71の未来VP準備、4.72の人形先行切断統合を行う。通常命中の同期SkinnedMeshRenderer.BakeMeshと通常SkinnedMeshRenderer描画は維持する | 人間承認済み、2026-09-09。効果がなければ導入見送りも4.65の正常完了とし、人形の先行準備による命中時負荷削減を必達にしない。対応範囲の限定と非bit一致は4.5.2に従う。総時間短縮・翌フレーム完成は保証せず、4.52は採否待ちにしない。実装・本体実測済みを意味しない |
| D-163 | 物理Convexの内接削減 | 7.2に従い、L超過出力を通常clip結果の内側に収まるL以下の有効なConvexへ削減する。方式・対象・頂点由来・決定性の範囲は実装詳細。成立しなければ当該Physics処理を失敗とする | 人間承認済み。接触・質量特性の近似許容は7.2、失敗終端は7.1／7.9に従う |
| D-164 | 正負二集合と直接Index出力 | 7.2.1／7.6の正負集合と点Anchorによる固定判定、4.5.6の単一予約への正負Index連続出力・新規転送1回／再利用時0回を使う。島の独立化は任意Phase 5.6へ置く | 人間承認済み、2026-09-10。一体運動・空中浮遊・接着情報喪失・遅延分離・分割不能・Bounds拡大・倒壊と任意分割のIndexコピー／新旧共存を許容する。完全分離・倒壊防止・性能改善を必達にしない |
| D-165 | Phase 4.3 建物World D6と一般外部Joint撤去 | 7.2.2を正本とし、建物由来の動的な1→2物理分裂子へ独立World D6を一つ生成する。垂直並進Free、水平並進・全回転Limited、3値によるDepth別指数Limitを使う。一般外部Jointの継承・付け替え・GC保護・予測を撤去する。建物は製品の切断対象、道路は非対象とする（O-005解決）。Runtime本体は独立Phase 4.3、製品Recipeは5.5、任意分割・GC統合は5.6／5.7 | 人間承認済み、2026-09-10。7.2.2の品質・運動・費用・製品入力制限を許容する。Depthは7.2.2の予定値をProvisionalと正式子で共有し、非建物はfalse／0を維持する。実装・拘束効果の検証済みを意味しない |
| D-166 | 固定Locomotion Occupancyと退出系撤去 | 7.2.3を正本としてLevel初期化時の固定Primitive集合と候補次姿勢Overlapによる要求全体Rejectだけを採用する。動的追従、ForcedOccupancyOverlapと退出状態・探索・専用ID・Profile・容量・作業領域、および退出系の専用試験を撤去し、Reject TraceからPolicyと侵入深度を削る。O-040を解決する | 人間承認済み、2026-09-10。切断・移動・退役後の通行境界不一致、未登録Geometryへの侵入、薄壁の飛越え、slide・部分移動なし、Lean後の人工移動停止、配置前提違反時の自動復旧なし、Reject詳細観測の喪失を許容する。Fade撤去と通常Geometry・物理契約は維持し、Runtime Occupancy更新は必要になった場合に別変更で決定する。実装済みを意味しない |
| D-167 | 単一Segment SlashWave | 19.1のLatch／Frame／Span Candidate／Close境界、Raw候補とAcceptedSpanのrunning maximum、単一Segment、WaveLifetime、19.1.8のSlashWave VFXと開発UIを採用する | 人間承認済み、2026-09-11。VFX簡素化は2026-09-13承認。完了済み区間は再評価せず現在区間の増加領域へのHitを許容する。Estimator切替は生存Waveを変更せず、一時状態の寿命をWave内に閉じる。19.1.6の容量満杯時の新Latch見送り・非遅延発射と、Expire先行による末尾区間の命中抜けを許容 |
| D-168 | 現在採用Convexと系譜Hit消費 | 19.1.7／19.1.9の4端点の閉凸包Sweep（退化を含む）と現在採用Convexを正本とし、直接消費したLogicalFragmentRefだけを一時保持、既存Operation履歴をO(N)走査する | 人間承認済み、2026-09-11。受付見送りでも同Slashでは再試行せず、無関係Fragmentは個別Hitできる。親API・Cache・通知・恒久履歴を追加しない |
| D-169 | 基本Playable先行Phase | 0.55でUX、4.50～4.52でWaveと現在状態切断を先行完成し、Predictionを後段へ分ける。4.1は性能曲線、Slash Deadlineへの適用は4.53とする | 人間承認済み、2026-09-11。15章の依存・省略条件を正本とし、4.55の内部方式は変更しない。Traceの現行相関は21.16.6に従い、旧形式の扱いは17章に従う |
| D-170 | 第一候補のGuide Ray交点 | 19.1.5.1のBegin剣先方向T、Begin→Latch Emitter chordのS、Live／Frozen Guide交点r／q、Invalid保持とClose後勾配を第一候補とする | 人間承認済み、2026-09-11。具体epsilon・q許容等はUI調整。Clamp、別交点Fallback、軸回転を追加せず、比較方式は同じ出力境界内で交換できる |
| D-171 | 断面色と最小デバッグ | 5.3に従い通常は仮断面と実断面を共通トゥーンの固定グレー、デバッグ有効時は仮断面を赤、実断面を緑とする。実Capの固定負UV markerは表示色選択だけに使い、処理経路色と専用表示契約を撤去する | 人間承認済み、2026-09-11。元Assetの負UVによる通常表示・デバッグ表示の誤表示を許容し、UV検査・修正・登録拒否を追加しない。O-004を解決 |
| D-172 | 建物Assetの一般化 | Structural Slab系列を撤去し、建物も一般Geometry／Compound Physics Proxy／点Anchor／World D6で扱う。確定済みPhase 0.2生成物の扱いは10.2.2、製品Recipeは15章に従う | 人間承認済み、2026-09-11。壁板数・Box対応・下端両側Anchor・入口用箱分割の固定条件を外し、物理近似とAnchor配置の違いを許容する |
| D-173 | 共用Geometry切断／Player終了 | 共用Geometryの出力は6.4の構成契約で保証し、製品Runtimeの出力検査を撤去する。継続不能な4条件は4章の共通Player終了へ集約し、対象別の限定退役・復旧と終了前の完全診断保存保証を撤去する | 人間承認済み、2026-09-11。入力Gate・メモリ安全・通常の世代照合・予約不足再実行・物理Fault・通常Captureの診断保存能力は維持する |
| D-174 | Provisional Sibling D6 | `ProvisionalSeparationConstraint`は7.1の固定anchor-offset D6を採用し、方式比較・動的Limit決定・Actor／Joint Pool導入判断を初期要件から外す | 人間承認済み、2026-09-11。D-132の方式とO-044の法線Limitを確定。有限区間による引止め・運動差を許容し、7.1の既存境界から判明した異常の扱いと資源寿命に従う |
| D-175 | 短寿命PhysicsSplitTransactionとCommit直列Geometry | 4.2、4.5.6、7.1～7.7、8、21章に従い、Final Physics／Logical Publication、祖先順Geometry Commit、個別退役へ整理する | 人間承認済み、2026-09-12。物理成立不能によるSource退役と正常退役に起因するIncompleteOperationTraceを許容する。新しい公開ID、状態・Reason、監視・復旧、GPU部分範囲freeを設けない |

## 13. 未決事項

| ID | 論点 | 選択／質問 | 影響 | 決定時期 |
| --- | --- | --- | --- | --- |
| O-001 | 初期ターゲット | 解決済み：PCVRを採用（D-011） | Quest単体は当面スコープ外 | 2026-08-21 |
| O-002 | 目標FPS | 解決済み：両眼描画90fpsを基準（D-012） | 再投影は安全網として扱う | 2026-08-21 |
| O-004 | 断面表現 | 解決済み：初期仕様ではカテゴリ別の断面色・模様・追加記号・内部部品表現を設けず、全対象を同じ固定グレーとする（D-171） | 専用アート、Material、Vertex属性を追加しない | 2026-09-11 |
| O-005 | 切断可能範囲 | 解決済み：人間判断により建物は切断対象、道路は非対象（D-165） | 建物対応をロードマップに含め、道路切断は実装範囲に含めない | 2026-09-10 |
| O-006 | 破片寿命 | 最大動的破片数、消去時間、スリープ規則。7.9の確定空物理GCとその接触・質量消失の許容は確定し、巡回頻度・製品予算・効果の実測は実装時に決める | 物理CPUと視覚密度。GCへのSleep必須化や期限保証はしない | T-010後／任意Phase 5.7 |
| O-007 | Collider仮状態 | 旧Collider維持時間と周辺破片の例外判定。Player Body／Handと刀は物理接触せず、刀は論理Sweepのみ | 違和感と実装複雑度 | T-005後 |
| O-008 | NPC構成 | Synty人物をそのまま使う範囲と顔・体型改造量 | 独自性と制作工数 | アート検証時 |
| O-009 | データ保存 | 切断状態をセーブ対象とするか | 再現性・容量・ロード時間 | ゲームループ決定時 |
| O-010 | ネットワーク | 将来的なマルチプレイ要否 | 切断イベント同期設計 | 企画判断 |
| O-012 | Voxel品質 | Asset分類別のVoxel Size、Adaptivity、穴封鎖閾値 | 輪郭精度、面数、処理時間 | T-022後 |
| O-014 | 自動修復閾値 | 自動封鎖径、平面誤差、Solidify厚、Voxel Closing半径 | 誤封鎖、輪郭誤差、処理成功率 | T-027～T-029後 |
| O-015 | Blender更新方針 | 4.5.12 LTSから次版へ更新する判断基準と更新頻度 | API互換性、生成差分、保守期間 | LTS更新候補発生時 |
| O-016 | Unity CLI再評価 | 実験的CLIとUnity PipelineをCIへ採用するか | 保守性、自動導入、外部依存 | CI構築時 |
| O-017 | Slash調整 | 19.1の実装済みLatch／Frame／Span Candidate／Close方式、Begin選択、Emitter、epsilon／q許容、必要時だけの候補上限、速度・寿命と実装済みVFXの主要な調整値を開発UIで調整する。生存Wave固定容量は0.55の観測から4.50開始前に決める | 応答・操作感・Span形状 | Phase 0.55以降。同じ出力契約内の交換で本書改訂・Phase再開を要求しない |
| O-019 | Edge Gate閾値 | Edge Lead Score、CutSample速度・位置、次の振りの再準備条件、異常速度上限。Phase 0.52は機能確認用の暫定値とし、最終値を固定しない | 復路誤発射、取りこぼし、連続斬り感 | Phase 0.55で実機調整、4.50で製品回帰（T-038～T-041） |
| O-020 | Grip校正 | 左右持ちの暫定固定OffsetはPhase 0.5で使用する。ユーザー校正機能の提供は0.5のGateにしない | 刀表示の一致、刃方向判定、導入工数 | Phase 0.55または後続UX判断 |
| O-021 | AI LOD境界 | Near／Mid／Far／Dormantを分ける最短介入時間、距離、更新周期 | CPU予算、見た目、予測再利用率 | T-045後 |
| O-022 | MobPlan Horizon | Tier別の`HorizonSampleCount`と`CommittedThroughFixedStepId`の長さ | 切断計算猶予、無効化率、メモリ | T-044～T-046後 |
| O-023 | モブ予約 | 粗い時空間予約のセル寸法、競合解決、群衆密度上限 | 交差回避、自然さ、計画費用 | T-047後 |
| O-024 | Unity更新頻度 | 6000.3.22f1から同一LTSパッチへ更新する条件と回帰基準 | 修正取込み、再インポート時間、安定性 | 更新候補発生時 |
| O-026 | 後期連続録画 | Phase 4.8で採用の要否と容量・停止時間を判断する。Phase 0.11の固定値を引き継がない | Gameplay負荷と調査用途 | Phase 4.8 |
| O-027 | API Layer対象 | Phase 4.8はD-137のD3D11構成から開始する。Phase 0.11にOpenXR API Layerを前倒ししない | API互換性・GPU所有権 | Phase 4.8 |
| O-028 | 最終像録画 | Meta compositor／Quest Link後の映像を併録する条件と手段 | Reprojection、圧縮、HMD固有不具合の切分け | T-056後 |
| O-032 | 最終重力と周辺調整 | 0.35G／0.5G／0.7G／1.0Gの採用値と、反発、Drag、分離Impulse、Animation、破片寿命の追加調整要否 | 空中斬り成功率、世界の重量感、テンポ、物理安定性 | T-064のプレイテスト後 |
| O-033 | Shadow近似品質 | 両面・キャップなし近似を許容する距離／時間、Stable専用Shader分離、問題時の簡易Shadow Cap導入条件 | Shadow GPU時間、Draw、接地影、Self Shadow、実装複雑度 | T-065後 |
| O-034 | Stencil Batch予算 | `MaxStencilColors`、OBB／Cap Bounds Margin、World Plane一致epsilon、Facing epsilonを決める。Count方式は128初期化の正符号8bitへ固定し、相殺・Color超過の救済条件、距離別Cap省略、別Backendは追加しない | CPU分類・Color割当て時間、Stencil GPU時間、Draw、最後の統合Color比率、仮断面品質 | T-066～T-068後 |
| O-035 | Job実行予算 | 同時Work数とBake共有枠を実測から調整する | Worker費用、Pending滞留、実機Frame時間 | T-069／T-076後 |
| O-037 | Surface Projection研究条件 | Trusted Exterior分類、最大距離、法線内積、包含Margin、最小厚み、Reduction前後の再Projection条件、自己交差検出精度 | Silhouette回復、Solid堅牢性、自動成功率、前処理時間 | T-071研究を開始する場合だけ |
| O-039 | Cut/Cook容量予算 | scratchメモリと`MaxIncompleteCutOperationCount`を実測から調整する | 予約量・ピーク使用量、Pending時間、受付見送り頻度 | T-076後 |
| O-040 | Player壁境界応答 | 解決済み：D-166の固定Occupancyと候補次姿勢Overlap Rejectを採用し、退出系を撤去する | 通行境界と表示／物理の不一致、全体Reject・自動復旧なしを許容 | 2026-09-10 |
| O-043 | Hybrid Clip予算校正 | Raster 8面を固定したままPixel fallbackを0～4面のどこへ置くか、Stable専用Shader分離 | GPU時間、MSAA edge品質、Shader register／varying | T-089後 |
| O-044 | Provisional Physics設定 | Actor／ShapeInstance／Constraint容量、Pending警告時間 | 生成／破棄CPU、Broadphase、Solver時間、Ghost Contact、接触Impulse、handoff品質 | T-091後。新しい監視・復旧契約は作らない |
| O-045 | Mob軌道Cache Profile | Crowd StepのFixedStep倍率、Tier別Horizon／Refill閾値、最大Mob／Sample数、同時再計画Group数、Live Fallback予算、Hold許容時間、将来のGrid Cell／MaxNeighbors／ORCA Horizon | CPU、Nativeメモリ、Queue枯渇率、停止時間、重なり、先行切断Commit率 | T-092の計画単体結果で初期設定を決め、先行切断Commit率による判断はJobベイク採用時のPhase 4.72の統合結果へ遅延し、不採用時は要求しない。4.70の完了を塞がず、ORCA値は追加導入時だけ確定 |
| O-046 | Animation Pose Evaluator | controllerなしPlayable／MixerとRetarget済みPose Tableの採用、Rig Pose Buffer形式・Bone順、Source Timeの数値精度、Main Thread／Job Batch予算、2 Source BlendおよびimmutableなLook／IK Layer入力の導入時期 | Pose誤差、Main Thread時間、Job Throughput、Pose Tableメモリ、Humanoid Retarget品質、先行切断採用率 | T-018後。人形の先行切断採用率による評価はJobベイク採用時のPhase 4.72とし、不採用時は要求せず、4.61の完了を塞がない。Loop／ClampとSource Timeの意味契約は未決にしない |
| O-047 | 剛体リベース許容値 | RigidCutRebaseProfileV1の法線角度、Bounds内plane field差、左右眼ProxyPoint pixel差の閾値を少数の距離／サイズ／斬撃例で校正する。8点近似を真の切断線最大誤差としない | 投機再利用率、切断縁とVFXのズレ、VR知覚、命中時CPU | T-093／任意Phase 4.55。未校正でも有効な試験Profileを明示し、製品値の未設定を無制限許容にしない |
| O-048 | Building World D6設定 | L1、A1、共通減衰率rと、建物D6を含む既存Constraint容量の製品値 | 拘束挙動、Joint数、生成・退役・Physics Step費用。倒壊防止率・最大変位・性能SLAは追加しない | Phase 4.3／T-094の少数FixtureとProfilerで判断 |

## 14. 技術検証項目

4章の共通Player終了出口を最初に接続する既存Phaseでは、終了APIをfakeに置き換えた短いシナリオで、Worker通知だけではlatchが変わらずMain回収時に確定すること、ログ・終了APIより先に受付・Commitが閉じて終了APIを一度だけ呼ぶこと、後続要求でログ・終了処理を重複実行しないことを確認する。専用Test ID・Phase、実Player終了、全エラー直積・競合網羅・診断保存試験は追加しない。

検証形式・試験手順の扱いは17章に従う。下表は確認する能力を定め、過去のCapture／Trace／Fixture形式や専用試験の維持を要求しない。

Phase 5.6／5.7の任意機能固有の確認は7.9.7へ集約し、下表の既存試験は担当契約の範囲で再利用する。既存Phaseの完了条件へ追加機能を前倒しせず、専用Test IDや大規模性能matrixを追加しない。

T-027～T-030は後続Phaseで採用した前処理に適用する。未採用の修復方式を試験のために実装せず、凍結したPhase 0.2の再開を要求しない。

| ID | 対象 | 合格の考え方 | 方法 |
| --- | --- | --- | --- |
| T-001 | 斬撃検出 | 高速な刀でも切り抜けず、一意な切断面が得られる | 速度別1000回で欠落率と重複率を計測 |
| T-002 | 即時分離 | 必要なベイク・VP変換後に仮分離が視認できる（4.5.2） | GPUタイムと入力から表示開始までのフレーム・残る準備費用を記録し、代表入力で4.5.2の同フレーム表示目標とフレーム全体の負荷を確認する |
| T-003 | 複数Pending | 2〜4切断で画質と性能が許容範囲 | 切断数別にCPU/GPU、Draw、overdrawを比較 |
| T-004 | Stencil断面 | 正常な正向きの共用Geometryで、5.2の明示的品質例外を除き穴・はみ出し・片眼ずれがない | 箱、凹形、人形の共用Geometryを通常の外部視点で両眼確認する。符号・Color・Plane overflowの扱いはT-066／T-067／T-089に従い、この試験へ重複展開しない。Camera内部／Near Plane近傍は5.2／D-131の品質例外に従う |
| T-005 | Convex切断 | 7.2の上限内の有効な閉凸出力と内接性を確認する | 小さいオフラインFixtureでL境界、超過出力の内接削減、削減後B-repの再切断可能性を確認する。削減不能は既存失敗終端へ接続し、成功例をAbortだけで代替しない。頂点由来・削除順別のmatrixは要求しない |
| T-006 | 共用Geometry切断 | 断面が閉じ、元surfaceのUV／法線／submeshが保持され、同じ結果が表示／Stencilへ使われる。生成Capの固定負UV markerと再切断時の継承を維持する | 代表Fixtureを多方向に連続切断。断面専用submeshの存在は合格条件にしない |
| T-007 | 受付制限と対象authority | 4.2／8章のSource単位の受付・失効と4.5.6の祖先順Geometryを確認する | AがActive中の同じSourceへの要求を見送り、保存・再実行しない。他のLogicalFragmentは同じObject内でも並行でき、他枝のObjectGeneration更新でAをRejectしない。AのFinal Physics／Logical公開後はGeometry未完了でも子のBを受付・公開できるが、B KernelはA Geometry Commitを待つ。同じTransactionのProvisional置換・pose／速度／Sleep変化は自己失効せず、外部authority変更はStale回収して現在Sourceを退役させない |
| T-008 | Skinned切断 | 姿勢固定から静的破片への切替が見えない | Phase 4.52。歩行・走行・腕振り中に各部位を切断 |
| T-009 | 入力モデル耐性 | 契約内モデルは自動前処理で切断可能になる | 変換検査とエラーレポートを確認 |
| T-010 | 破片予算 | 連続プレイでCPU／メモリが上限内へ収束 | 10分間の連続切断ストレス試験 |
| T-011 | XR描画 | Single Pass環境で両眼のclip／Stencilが一致 | Phase 1.50～1.52の基本VP／Hybrid Clip／Stencilの低レベル確認を再利用し、Phase 2で製品状態を含むclip／Stencilの左右眼スクリーンショットと実機確認 |
| T-012 | Collider cooking | バックグラウンド化後にメインスレッドスパイクが残らない | Profilerで切断前後フレームを追跡 |
| T-013 | 非VR性能基準 | 同一負荷を自動再生し、変更前後を比較可能 | 固定カメラ、固定乱数、切断スクリプトで計測 |
| T-014 | Quest Link XR | Quest 3S有線Quest Linkの90HzモードとSingle Passで、単純Geometryと左右別の暫定固定GripToKatanaOffsetを適用した刀が両眼表示され、Controllerへ追従する | Phase 0.5。HMD内目視とProfilerで基本表示・追従と一度の追跡喪失／復帰を確認し、無効Poseを利用しない。固定測定時間、P95／P99、製品90fps SLA、任意校正UI、Slash生成は要求しない |
| T-015 | 斬撃波先行切断 | 接触前の完了率が即時レンダラ負荷を有意に減らす | Phase 4.53。距離、速度、対象数別に事前完了率とPending時間を測定 |
| T-016 | 未来評価器統合 | DAGで依存が解消した投機Workが共有Dispatchへ入り、取消・完了後採否・実Hit時Commitが競合なく成立する | Phase 4.53。遅延、進路変更、再切断で未Schedule取消、Schedule済み成果物の世代・前提不一致による不採用、有効成果物の実Hit時Commitを確認し、T-090の優先順と統合する。投機同士の厳密なDeadline順は合格条件にしない |
| T-017 | 自由飛行剛体の直接予測 | 19.3の直接予測が本体状態と統合され、対象外・前提不一致は現在状態経路へ進む | Phase 4.54で開始Snapshot、重心／Actor原点、回転、WorldPhysicsProfile、FixedStep境界、予測Horizon、Gate対象外を確認する。点Anchor、接触／転動、既知Constraint付き対象を直接予測へ入れない。面リベースはT-093で確認し、外部Probeの測定値を製品保証にしない |
| T-018 | 明示Animation State／未来姿勢 | AnimatorController内部Stateを正本にせず、同じ対象Stepへ解決済みの明示Stateから現在／未来Rig Poseを任意順で再生成し、接触姿勢を十分な精度で予測できる | Phase 4.61で独立Pose評価を完了し、MobPlan固有の統合は4.70、切断成果物Commitは条件付き4.72で確認する。単一Clip、Loop境界`0.98 -> 1.02`、0／複数cycle Phase、Clamp Clipの`nextDown(1.0)`／`1.0`／`> 1.0`と終端Hold、負Phase Reject、Clip hard switch、Hold、Near表示、Mid／Far未来Sampleを使う。同一Planから`tick 140 -> 103 -> 172 -> 121`と時系列順に評価し、同一Backendのcanonical Bone順Poseが要求順や直前のEvaluator呼出しに依存しないこと、Evaluatorが`PlaybackRateCyclesPerSecond`で追加進行しないことを確認する。AnimatorController State／Trigger／Clock／Transitionの読戻しと目的tickまでの逐次rolloutが標準経路で実行されないこと、現在表示Backendも明示Stateへ従属し独自Phase進行しないことを計測・検査する。controllerなしPlayable／MixerとRetarget済みPose Tableを同じState／Rig Identityで比較し、代表骨位置・回転誤差、実接触Pose誤差、Main Thread時間、Job Batch Throughput、固定Cacheメモリを記録する。V1予測対象では現在／未来の双方でLook／腕／Foot IK、視線多様化、左右反転が無効であり、Backend固有設定から暗黙にMirrorされないことも検査する。Clip ID／Mode／durationまたはAsset／Evaluation Profile Identity不一致、PlanGeneration更新、非finite Phase／Rate、未知Clipでは旧Pose／依存切断をCommitせず実姿勢Fallbackへ移る。最大finite値付近のRate／FixedDeltaによるstep duration乗算Infinity、phase delta乗算Infinity、Phase加算Infinityを各段階でRejectして旧StateをHoldし、最小subnormal付近のRateが乗算underflowで0になった場合はfiniteな0進捗として受理することを確認する |
| T-019 | Trace相関と完全性 | 21.3／21.4の因果関係と欠落の扱いを満たす | 代表的な処理の公開・完了・破棄を追跡し、不完全な記録を完全な再現根拠にしないことを確認する。保存形式や旧Readerを固定しない |
| T-020 | Trace負荷 | 観測処理がGameplayを待たせず、競合や性能判断を歪めない | 代表負荷で観測有無の費用・メモリ・記録欠落を確認する |
| T-022 | Blenderバッチ | GUIなしで代表Assetを変換し、失敗を終了コードとレポートで検出 | 家具、車、建物を連続処理して出力を検査 |
| T-023 | Solid品質 | 生成物がwatertight、向き整合、退化面なしで切断可能 | 非多様体Edge、体積、面数と多方向切断を自動検査 |
| T-024 | 例外Recipe | 開口、空洞、別部品、建物チャンクを再現可能に指定できる | 車と建物の初回設定後に無人再生成 |
| T-025 | 前処理キャッシュ | 入力未変更時に再生成せず、変更時のみ確実に無効化 | 入力、Recipe、Script、Blender版を個別変更 |
| T-026 | 公開Repo分離 | 公開履歴と成果物にSynty入力・派生Assetが混入せず、原本が非公開Git LFS Repoだけに存在する | ignore、CI検査、履歴スキャン、LFS追跡状態、private remoteのアクセス権を確認 |
| T-027 | 境界Loop封鎖 | 小さく平面的な欠損を誤接続せず自動封鎖できる | 穴径・平面誤差・頂点数を変えた合成Meshで検査 |
| T-028 | 片面Solidify | 分類別厚みと法線規約で閉じた薄肉Solidを生成できる | 壁、屋根、看板、車体パネルで検査 |
| T-029 | Voxel修復 | 微小隙間を閉じつつ窓・入口・トンネルを誤封鎖しない | Closing半径別に表面誤差、体積変化、判定結果を比較 |
| T-030 | 修復失敗判定 | 危険な生成物をSuccess扱いせずNeedsReview／Failedへ送れる | 大開口、反転法線、分岐境界、自己交差を投入 |
| T-031 | Blender環境再現性 | 古いBlenderがインストール済みでも専用版だけが使われ、別PC／CIで同一生成結果になる | PATHに別版を置き、Bootstrap、版照合、SHA-256、不正Archive拒否、出力Hashを検査 |
| T-032 | Unity版固定 | PATHやHub既定版に関係なく6000.3.22f1だけで開き、誤版起動を拒否できる | ProjectVersion、明示exe、batchmode、Package Lockと別版併存を検査 |
| T-033 | Repository衛生 | 公開Repoに生成Cache、ユーザー実名パス、Synty Assetが混入しない | ignore、機密パターン、絶対パス、履歴をCIで検査 |
| T-034 | Slash UX実機探索 | 完成済みSandboxを使い、利用可能なLatch／Frame／Span／Close構成と暫定Presetを得る | Phase 0.55。Quest Linkで第一候補から試し、人間が試すと決めた候補を必要に応じて追加し、19.1.12の主要値表示・Pose再生・Current／Pinned比較で実装済みVFXを含めて調整する。第一候補のLive／Frozen Guide、Raw／Accepted Span、Invalid保持、Close後勾配と連続斬りの同時生存Wave数を確認する。0.54までの機能確認を本試験の合格とはせず、全候補行列・最終値・ログ互換を要求しない |
| T-035 | SlashWave Core | 発射時の面・軸・初期形状と評価設定の不変性、AcceptedSpan非減少、有限寿命を維持する | Phase 4.50。VFXはPhase 0.53の19.1.8確認を再利用する。減少・Invalid保持、完了済み区間の非再評価と現在区間の増加領域、採用方式のClose後評価、Expire、複数Wave、UI切替後の生存Wave不変とSpan／Close一時状態の寿命を確認する。小さい固定容量で、容量まで公開→満杯時は新Latchだけ見送り→既存Wave不変→既存WaveのExpireで容量返却→見送ったStrokeは遅延Latchされず、再準備後の新Strokeは返却と同じ更新でも通常Latch、を一連で確認する。ExpireしたWaveはその更新のSegment／Sweepを出力しない |
| T-036 | Segment Hit | 現在採用Convexとの共通閉Sweepと系譜消費が成立する | Phase 4.51。生成時の退化入力、4端点の閉凸包（通常は平行四辺形／台形）、増加領域Hit、Bounds／VFX非authority、厚み・端点領域なし、無関係Fragment個別Hit、子孫再Hit禁止と受付見送り後の非再試行を確認する。軸が平行または反平行で線分へ退化する代表1件を同じ現在採用Convexへの共通Queryで扱い、軸補正・厚み・端点領域・別Hitアルゴリズムを追加しない |
| T-037 | 投機候補範囲 | 範囲外でも現在状態切断が成立する | Phase 4.53。有限包絡の保守Boundsと包絡を保証しない先行準備範囲を区別し、範囲外実Hitを4.51へ接続する。Clamp・範囲拡張・再探索を追加しない |
| T-038 | Edge Direction Gate | 刃側の広い振り角を許容し、峰側移動はSlashを生成しない | Phase 0.52は少数固定Pose列、0.55で実機調整、4.50で製品回帰。Score閾値、速度、移動量、Sample Window別に往路・復路・斜め振りTraceを再生 |
| T-039 | 連続斬り | 復路で誤Slashを生成せず、返した刀の次の有効斬りを受理する | Phase 0.52は少数固定Pose列、0.55で実機調整、4.50で製品回帰。代表的な抜刀・復路・返し・左右連続斬りで誤発射・欠落・再準備を確認する。各1000回の固定行列は要求しない |
| T-040 | 刀の非接触と新Wave受付 | 発射条件・再準備条件の不成立時に刀自身から新Wave・物理応答・Gameplay Hit・刀由来Hapticsを生成せず、生存Waveはその後のGate状態から独立に継続する | Phase 4.50は新Waveを生成・公開しないことと刀Colliderによる物理応答がないことを確認し、実対象Hit・Hit Detector・対象Queryを要求しない。Phase 4.51は発射条件・再準備条件の不成立中も生存WaveのSweep／Hit評価が継続し、刀自身から対象Query・Hit・Hapticsを生成しないことを確認する。生存Waveの実Hit通知・Hapticsは禁止対象に含めない |
| T-041 | Tracking復帰 | 追跡喪失と再取得で巨大速度や誤Slashを生成しない | Phase 0.51／0.52は履歴Resetと受付を少数固定Pose列で確認し、0.55で実機調整、4.50で製品回帰。Controller遮蔽、Pose無効化、位置飛びを記録・再生しSample Resetを確認 |
| T-043 | Unity更新再現性 | Project再作成や版別コピーなしで新Editorへ更新でき、旧版へGitで復帰できる | 専用ブランチと一時worktreeでProjectVersion、Package Lock、固定テスト、XRスモークを検査 |
| T-044 | MobPlan再現性 | 同じ入力、Seed、NavMesh、PlanGeneration、Animation Clip Catalogから同じRoot軌道と明示Animation State列を生成できる | 固定シーンの計画Hash、経路、RootTrajectory、Clip ID、非wrap累積Phase、PlaybackRateCyclesPerSecond、Catalog内容hash、Group epochを比較し、Animator内部State、評価Backend差、暗黙のMirror設定をPlan生成・Pose評価入力へ混入させない |
| T-045 | AI LOD予算 | 遠距離モブ数を増やしても計画CPUとメモリが予算内に収まり、近距離反応を阻害しない | Tier別人数、更新周期、Horizonを変えてProfilerとTraceを比較 |
| T-046 | MobPlan無効化 | プレイヤー介入、経路遮断、別切断で旧計画と依存Animation State／Rig Pose／切断成果物がCommitされない | PlanGenerationを意図的に更新し、旧Stateを表示・未来評価へ再利用しないこと、Task破棄と実姿勢Fallbackを自動照合する。通常Animation変更に別runtime Animation Generationを作らず、Rig／Asset／Evaluation Profile Identity変更だけを独立検証する |
| T-047 | 時空間予約 | Farモブ同士が粗い予約下で目立って重ならず、予約計算が局所的に完了する | 密度別に競合数、再計画数、CPU時間、見た目を測定 |
| T-048 | モブ先行切断 | 遠距離モブの計画済み明示Animation Stateから必要候補だけをPose評価し、命中前のMesh／Convex完了率を改善する | 距離、Tier、Horizon、Pose Evaluator Backend別にCommit率、破棄率、Pending時間、評価Bone数を比較し、全Mob／全Sampleの全骨Pose先行生成が実行されないことを確認する |
| T-049 | Mob Trace完全性 | MobPlan生成から利用、無効化、再計画、切断Commitまで因果を追跡できる | MobId、PlanGeneration、SlashId、TaskIdで保存Traceを自動照合 |
| T-050 | 断面表示一貫性 | 通常表示で仮断面と実断面が同じグレー・同じトゥーン応答となり、差し替えで陰影や輪郭が目立って変化しない | 共通トゥーン設定下で箱、凹形、人物を多方向に切断し、両眼映像とフレーム差分を確認する。既存FixtureでBase Texture／Material UV Transformを変更しても実断面色が変わらないことを確認する |
| T-051 | 断面デバッグ表示 | 5.3の通常グレー／仮断面赤／実断面緑が描画対象に対応し、デバッグ切替でGeometryを書き換えない | Final／Logical公開前、公開後Geometry未Commit、Geometry Commit後を一連で確認し、再切断では既存実Capの緑と新しい仮Capの赤を維持する。色変更だけを目的とするVertex／Index書換え・VB／IB転送・Geometry複製がないことを確認する。処理経路別の色行列、色覚補助、専用パネル、独立性能SLAを要求しない |
| T-054 | Unity選択的録画 | Frameと画像が対応し、21.15の非待機・容量・寿命・故障分離を満たす | Phase 0.11では21.15のCapture経路での複数Frame処理・出力確定・decode対応と資源寿命を確認する。Phase 4.8の連続録画採否・負荷確認を前倒しせず、旧固定fps・Frame数・試験階層を要求しない |
| T-055 | OpenXR Projection Capture | D3D11固定ProfileでRelease前CopyがSwapchain所有権、Texture Array、左眼SubImage Rect／Array Indexを正しく扱い、提出画像を破損しない。MSAA、別API、想定外LayerはFail Fast | 正常Profileで非録画時との画像・Frame timing差を比較し、MSAA、D3D12、別Array Size、追加App Layerを故意に与えて録画停止とTrace理由を検査 |
| T-056 | Capture相関と限界 | predictedDisplayTime、Pose、TestRunId、ゲーム内ID、画像が一意に対応し、Projection正常／最終HMD異常を区別できる | 意図的な描画不具合、Dropped Frame、Reprojection、Link品質低下を発生させ、Unity Capture、API Layer Capture、HMD観察を比較 |
| T-057 | Capture環境識別 | 21.7.6に従い異なる環境のRunを同一条件として比較しない | 代表的な構成差を識別できることを確認する。保存形式と差分照合方法は実装詳細 |
| T-058 | Pending物理共有 | 4.5.2の入力準備条件に従って切断表示を開始し、cook遅延中の共有Colliderによるめり込みと透明接触が許容範囲に収まる | Bake遅延を0～数秒へ変え、表示開始フレーム、分離量、接触差、Timeout品質低下を測定する。処理中は同一Sourceへの再切断を拒否し、Final／Logical公開成功後は子への切断を受付可能とし、Final不成立時は7.1に従ってSourceを退役することを確認する |
| T-059 | 物理分裂Commit | 7.1／7.2の初回分裂とFinal handoffのpose／速度・一体公開を確認する | 並進・回転・接触中の代表例で、Anchor点速度継承とFinal Actorのpose／速度維持、主スレッド時間、Impulse、視覚差を記録する |
| T-064 | 全体低重力プレイ | 一般プレイヤーが空中物体を狙いやすく、世界全体の浮遊感とゲームテンポが許容でき、全軌道系で重力が一致する | 0.35G／0.5G／0.7G／1.0Gを同一投擲・切断Scenarioで比較し、滞空時間、斬撃成功率、主観評価、Physics／予測／VFXの軌道差を記録 |
| T-065 | 即時切断Shadow | Stencil Capなしの両面Shadowが即時状態で許容でき、clip／Offsetがカラー像と一致し、片面／両面群分割が90fps予算を阻害しない | 箱、薄板、凹形、非閉形状を床／壁近傍で切り、単一Directionalの各Cascade、Bias条件について実Capとの差分、漏れ、peter-panning、Shadow Draw、GPU時間を比較 |
| T-066 | Stencil Color割当て | 通常Colorでは左右眼いずれかでResidual Stencil Supportが重なる非互換対象を分離し、実行Color数を`MaxStencilColors`以下に保つ。配置できない対象は最後のColorへ入り、各Colorで全Volume後に全Capを描く | 左右眼だけでCapが重なる配置、OBBは重なるがCapは非交差の配置、全Cap重複、非重複、小さいColor上限を確認し、CPU分類、Color数、統合Color比率、Clear／Volume／Cap GPU時間、Drawを測定する。全Graph／全Edge、特定のColor番号、方式間で同じ彩色結果、統合Colorの画像正解を要求しない |
| T-067 | 正符号Stencil／互換Group | 128初期化とWrap加減算から得る`S>128`が範囲内の`W>0`と一致し、共通契約を満たすGeometryを符号保存のまま共有できる。通常Colorでは非互換Residual Supportの分離条件を守る。描画結果には5.2の明示的品質例外を適用し、最後の統合Colorでは非互換対象の分離を要求しない | Phase 1.52の低レベル確認を再利用し、Phase 2で製品状態・互換Groupとの統合を確認する。正向き箱、凹形、同方向重複と、生のCount `-1／0／+1／+2`が`127／128／129／130`になる小さいFixtureで`Ref 128 / Comp Less`、Read／Write Mask、IncrementWrap／DecrementWrap、Color／Depth writeを確認する。全体反転閉Mesh、正逆重複、別TopologyのCoincident／Nested／Self-intersection、負determinant Transform、World Plane差、左右眼を試し、向き正規化や二重Transform補正がないことを確認する。範囲外Winding、入力Gateの不合格行列、削除済みの符号証明、向き正規化、Winding上界、Count容量分割、符号別Groupをこの描画試験へ追加しない。8bit排他不能構成はゲーム開始を拒否する |
| T-068 | 両眼Cap可視性Cull | Facingでは両眼ともepsilonを越えて明確に裏向きのCapだけを除外し、片眼可視・epsilon帯内・正負Capを誤って除外せずStencil仕事を削減する | 左右眼のFacing一致／不一致、epsilon境界の内外、正負Cap、Frustum内外の固定配置でCull判定、Stencil Draw／GPU時間、左右眼画像差を比較する |
| T-069 | Owner単位Cut/Cook | 7.2の実行分担と一体Commitを確認する | managed Job内Burst kernelからMeshDataへ出力し、Main ThreadのMesh適用後にmanaged Jobで`Physics.BakeMesh`を行う。同一Meshの同時Bakeがなく、stale非適用、all-or-none Commit、cook不成立時のAbortを少数Fixtureで確認する。cook後の品質差の扱いは7.3に従う |
| T-071 | Global Solid Reconstruction研究 | Voxel／SDF Union、内部充填、Surface Projectionから自己交差のないGlobal Solidを再構成できるかを将来研究する。製品Phase、代表Asset合格条件、Fallbackには使用しない | 開始時期未定。研究を開始する場合だけ独立DatasetとArtifact Schemaを新設し、標準Closed Component／Stencil／Compound Convex経路へ影響しない比較として実施する |
| T-072 | 固定物体の即時切断 | cook遅延中もAnchorを持つ所有者全体が固定され、Anchorなし側だけが仮分離する。固定を理由に仮描画を省略しない | 単一・両側・OnPlane Anchor、同Sideの離れた島、連続切断、先行結果Rejectを少数例で確認する。cook遅延caseでは固定・仮分離と固定側の誤Impulse・変位がないことを確認し、全体固定による浮遊とAnchor喪失後の大型物体の落下・回転を許容する。cook失敗caseではFinalを部分公開せず、7.1に従ってSourceを退役することを確認する |
| T-074 | 点Anchorと論理切断公開 | 7.1の点Anchor継承とFinal Physics／Logical Publication、後着Boundaryを確認する | Phase 1のHarness内合成Final成功／失敗入力で正負2子の一体公開またはSource退役を確認する。OnPlane両側継承、非identityなlocal frame、子再切断時のSibling不変を含む。Phase 3でGeometry Commit時のBoundary 0／後着、Operation／ChildからのTrace相関を確認し、実物理はPhase 4へ接続する。8章に従って他枝の世代更新とauthority喪失を区別する。欠落・重複・件数不一致・IncompleteOperationTraceを完全Traceの合格根拠にしない |
| T-076 | Cut/Cook Profiling | 7.5の代表Fixtureで製品経路の費用を確認する | Owner当たりkernel時間、Cut/Cook全体時間、処理量、Bake数、Final Commit時間、scratch予約・使用量、失敗・stale・Abort数を確認し、O-035／O-039の調整に使う。保存形式と反復方法はHarness実装詳細とする |
| T-078 | 採用Fixtureの利用 | 10.2.2の採用ファイルと用途対応を後続から利用でき、公開／非公開の境界を守る | mergeする集合で確認する。旧quota、全再生成、旧形式互換・再監査を要求しない |
| T-083 | 共用Geometry切断 | 共通契約を満たす入力を任意平面で切り、実Capを含む各非空出力が同じ閉鎖・edge／vertex manifold・局所winding整合を継承し、4.5.6の正負直接配置と転送・公開条件を満たしてから、表示とStencilへ同じ世代・Triangle集合としてCommitされる | 箱、凹形、複数の閉Component、全体反転、skinning後Self-intersection、別TopologyのCoincident／Nested Componentを切る。planeがvertex／edge／faceを通る場合、同一点複数port、極小／面積0 Triangleを含め、元surfaceと逆向きのCap boundary、Cap内部Edgeの2 incidence、単一vertex fan、canonical position、finite属性、再切断後の同契約を小さいFixtureのオフラインHarnessで検査する。面積0 Triangleを含む非空GeometryとTriangle数0の空出力を区別し、後者へdummy Mesh／Cap／Rendererを作らない。合成Final成功で2子を先に公開し、Geometryが片側または両側空でも子数を変えずRendererなしとする。Boundary 0／後着と祖先順Commitを確認する。用途別の二度目の切断／Cap生成／Uploadと製品Runtime出力Validatorがなく、世代不一致の通常不採用と出力予約不足時の非公開を確認する。出力予約不足だけは4.5.3の再予約・再実行を許容し、プール容量限界は4.5.4に従う。全Mesh self-intersection／inside-outside検査、旧救済経路、方式別試験を追加しない |
| T-084 | 共用Geometry入力Gate | 正常な閉Meshと全体反転を受理し、Boundary Edge、局所winding不整合、3面以上Edge、複数fan共有Vertexを切断可能Geometryとして登録しない。属性seamと別Topologyの同位置Componentを混同しない | 正向き箱、全体反転、複数の閉Component、Self-intersection、別TopologyのCoincident／Nested Component、UV／Normal seamを受理する。開放Boundary、1／3／4面Edge、T-junction、局所反転、複数fan共有Vertex、共有position不一致、NaN／Inf、不正index／Topology参照を入力準備時にRejectする。Runtime生成の面積0 TriangleをGeometry全体の空と誤判定せず、片側空No-opはこの入力Gateではなく7.6の現在Convex分類で判定する。全Mesh自己交差、inside／outside、signed volume、向き正規化を実行せず、同じ不合格行列をStencil描画試験へ重複させない |
| T-085 | Convex質量特性と質量保存 | 7.2の親質量保存とFinal質量特性を確認する | 少数の単一・重複Compound・連続切断で、正負子のfiniteな正質量、親質量保存、finiteでSolverへ設定可能な重心・慣性、Provisional mass非流用を確認する。加算順・配分系譜・固定近似方式のGoldenを要求しない |
| T-086 | robust supportと正負二集合 | 7.6の一つのepsilonによる受付・未切断継承と2所有者を確認する | 接線・Near-plane・等号で片側supportなしはNo-op、両robust supportがあるConvexはd = 0で分割する。非交差Compound全体が両supportを持てば受付け、全頂点Near-planeの別Convexは正側へ未切断継承する。U字・離れた島も同Sideの一所有者にまとめ、両Physics非空でGeometry片側／両側空ならRendererなしの子を残す。受付後の片側物理不成立はAbortであり、反対側所有者へのGeometry付属で救済しない。非UnionのStencil符号加算とSibling衝突抑止は維持する |

T-006／T-083の数値部分と4.5.6の直接配置はPhase 2.9で確認する。継承Vertex再利用・新規Vertex連続出力、非zero baseや疎な既存参照、共有入力と非重複出力、容量不足・入力不変・scratch再利用を確認できるものとする。GPU転送、世代・authority、Boundary公開、表示／Stencilへの同時反映、祖先順Geometry CommitはPhase 3で確認し、数値部分だけで統合確認を完了扱いにしない。Probe由来Fixture・生成器・Verifierは必要なものを選抜・改変でき、検証方法・ケース集合は17章に従う。製品Runtime出力Validatorは設けない。

Phase 2.9の性能確認は実Burst経路の数値処理・割当・出力量を対象とし、比較可能な同一入力では先行実装との退行を調べる。seam・AoS・追記出力による処理範囲の差を区別し、大きな退行は原因と結果を記録する。比較不能・未測定を確認済みとせず、Probeの全測定再現・固定倍率・第二Kernelの恒久維持は要求しない。参考Assetは10.2.3を利用でき、全Asset移植や0.21完了を前提にしない。

4.5.6の表示出力は既存T-007／T-083／T-086／T-090／T-091の該当確認へ次を統合する。数値配置はPhase 2.9、転送・公開はPhase 3、実物理との接続はPhase 4とし、小さいFixtureと統合時の意図的な完了遅延で確認する。別Test ID、専用大規模Suite、Benchmark Schema、Trace Eventを追加しない。

| 確認する順序・条件 | 既存試験へ統合する期待動作 |
| --- | --- |
| 正負直接出力（T-083／T-086） | 一つの予約へCapを含む正側、負側の順で連続配置し、n0・n1から範囲を決める。未使用tailを公開・転送せず、面集合・向き・Topology・再切断結果を維持する |
| Index転送（T-007／T-083） | 新規出力の実使用範囲を一度のSetDataで転送する。入力Geometry全体と既存GPU範囲を変更せず再利用する場合だけ新規Index書込み・転送0回とし、片側出力や個別非交差Triangleだけでは省略しない |
| 表示CPU結果が先着（T-083／T-090） | CPU生成・転送は内部処理であり、Final Physics／Logical Publication前にGeometry Commitしない |
| 物理が先着（T-086／T-091） | Geometryを待たず正負Final Physics／Logical childを公開し、Temporary表示を子へ追従させる |
| 物理成立不能（T-086／T-091） | 7.1のAbortでSourceを退役し、当該未公開Geometryを現在状態へCommitしない |
| 再切断と寿命（T-007／T-083／T-091） | 受付は祖先Geometry未完了でも可能だがKernelは祖先Commit後とする。CPU Publishedは内部処理であり、CPU Index再利用をGPU完了待ちへ結び付けない（4.5.3）。Pending件数はGeometry Commitまたは不要化の終端で完了する |
| 先行計算（T-007／T-083） | 有効な準備済みGPU範囲を再利用し、命中後の再配置・再転送を要求しない。前提不一致は既存の不採用・回収に従う |
| ID | 対象 | 合格の考え方 | 方法 |
| --- | --- | --- | --- |
| T-088 | Player非接触Locomotion | 固定Occupancyへの候補次姿勢Overlapによる要求全体Reject、HMD非Clamp、固定集合の非追従が成立する | 少数の固定壁Fixtureで非Overlap候補の許可、Rootまたは予測HMD CapsuleがOverlapする要求の全体Rejectを確認する。元物体の移動・切断・Commit・退役後も固定集合が変わらず、切断開口へ通行境界を追従させないことを確認する。HMD Lean後も追跡poseをClampせず、候補が重なる人工移動は拒否されることを確認する。切断・描画・未来物理の既存試験は変更せず、本Fixtureとの組合せを要求しない |
| T-089 | Hybrid Clip Plane予算 | D3D11／Quest LinkのColor、Depth、Shadow、Stencil Volumeで同一のstable Plane選択を使い、Raster 8面とPixel fallback最大4面でGPU時間とMSAA edge品質を保ちながら、容量超過面を5.2に従いPlane選択とclip制約から除外し、対応Stencil Volumeをsubmitしない | Phase 1.51の固定Clip確認を再利用し、Phase 2で製品状態・候補選択との統合を確認する。0、1、7、8、9、12、13、32候補面を持つ単一／複数RenderFragmentを用意し、先頭8面が`SV_ClipDistance`、9～12面がPS `clip()`、残りがIgnoredになることをShader captureとProfiler Counterで確認する。Operation公開前後で受付済みの未Commit面・Sideを引き継ぎ、固定による候補除外がないこと、RenderFragment対応変更時に同じ描画更新境界で候補を再構築することを確認する。Pending Cut列とLogicalCutOperation公開列を通した受付の古い順、左右眼、Color／Depth／Shadow／Stencil各Pass、カメラ移動、画面外復帰で選択が一致し点滅しないこと、Operation公開時に同じ面と受付位置を保って重複しないことを検査する。同一枝へ13回以上連続切断し、選択列が未Commit祖先についてdependency-closedで、Ignoredな後発面により祖先外Geometry復活やSibling重複を生じないことを確認する。順序違反した復元Fixtureは違反以降をIgnoredとして背景完成へ委ねる。Ignored Pending Cut／境界でもPending CutまたはCutBoundaryRecord、世代、支持、背景共用Geometry／Convex Jobが残り、同期待機やJob cancel／再発行を生じずStable Commitで正しい形状へ収束することを確認する。Ignored VolumeのCap板をBatchへ残し、通常Colorで別ResidualがないsampleはStencil 128のためColor／Depthを書かないこと、通常Colorの非互換Residualは分離されること、最後の統合Colorでは板の可視化と誤Depthを5.2の例外として扱うことを確認する。Ignored専用フラグ、compaction、代替VFXを要求しない。MSAA 1x／2x／4x／8x、pixel-bound／vertex-bound Sceneで全PS clip、Hybrid、Raster 8のみを比較し、Pixel fallback数とStable専用Shader分離をO-043へ記録する |
| T-090 | 共有Dispatchと切断受付 | 4.4の優先順、有限容量・予算、物理仕事の投入余地、取消・完了後採否、同一フレーム内の後続Dispatchが成立する | Phase 1の合成Workで、現在状態の必須仕事が投機・Maintenanceより先、命中済みPhysicsが命中済みGeometryより先になること、低優先投入で物理仕事の待機Queueへの余地を使い切らないこと、未Schedule取消、古い成果物の不採用、二重適用なしを確認する。代表的なA→B→Cで、同一フレームの後の機会に完成済み結果を回収して後続をScheduleでき、呼出しごとに予算が増えず、未完了時は待たずに戻ることを確認する。Queue満杯でも通常経路は待機・追出し・無制限再試行をしない。受付済み切断の必須仕事だけは4.4の同期進行と一度の再投入を確認し、進捗不能・Physics Step待ちは既存Pendingへ戻る。全対象合計の`MaxIncompleteCutOperationCount`直前／一致、同一Frameの複数対象、同一Slashの部分受付を試し、見送りが状態・世代・新規仕事を変えず再実行されないこと、件数が受付時に増え、7.7のGeometry Commitまたは終端時に一度だけ減ることを確認する。Final／Logical公開や内部CPU完了だけでは減らない。内部型・Queue方式・固定Counter・無割当・厳密なDeadline順を合格条件にしない |
| T-091 | PhysicsSplitTransactionとProvisional | 7.1／7.2のall-or-none構築、一体Publication、Abort、退役、Lease寿命を確認する | 少数の単一／Compound・Anchorあり／なし入力で、Provisional再cook 0、旧Geometry共有、Siblingのみ衝突抑止、外界Collision、Provisionalでの親質量保存、初回速度継承、Actor優先Final handoffと自前B-repの包含・frame条件を確認する。Final先着では直接Final、Provisional構築不能・片側Final不成立では部分公開せずSource退役となる。Geometry未完成で正負2子を公開し、PendingのPhysics責務が終了することを確認する。7.1のD6設定と内向き境界、代表的な内向き抑止・外向き上限挙動を確認し、正常Limit到達は異常にしない。既存境界からの継続不能通知はActive中ならAbort、成功後なら該当LogicalFragmentだけの退役へ送る。Timeoutのみでは退役しない。Solverの数値SLA、異常監視の網羅、方式比較・Pool必須化は要求しない |
| T-092 | Mob固定ステップ軌道Cache | Phase 4.70で同じSnapshot、Intent、Path、Seed、PlanGeneration、Animation Clip Catalogから同じRoot軌道と`ExplicitAnimationStateV1`列を生成し、Nearのライブ更新とMid／FarのQueue再生が同じ移動Kernel／Animation Plannerを共有し、計画単体で成立する | 固定MobId順、Current／Next二相更新、FixedStep倍率、Waypoint／Lane、Queue wrap、Horizon補充、Render補間、移動距離由来PhaseとPlaybackRateCyclesPerSecondを再生し、V1ではMirror入力もBackend固有Mirrorも生成しない。Render補間では`HorizonSampleCount = 1`、`2`、開始直前、開始ちょうど、終端ちょうど、終端超過、最後のSample直前1 FixedStepでoff-by-oneやHold条件の逆転がなく、`stepId < StartFixedStepId`では先頭Sample全体でHoldし、`stepId < CommittedThroughFixedStepId`のときだけ同一Clip Stateを補間することを確認する。Group公開は全Mob descriptor検証後の単一Group epoch atomic storeだけが読取可能点で、Commit途中の一部Mobだけ新HorizonまたはClip／Phase／Rateになる観測がないこと、旧Job完了、入力末尾Sample slotのpin／Snapshot、wrap時の未再生上書き禁止、Reader完了境界後の旧slot回収、epochのwrap／ABA対策を検査する。`HorizonSampleCount * CrowdStepScale`、`StartFixedStepId`加算、`stepId`減算のchecked overflowではPlan／補充を公開せず既存区間維持のHoldとなり、`FixedStepId`のwrap／再利用で古いSampleが未来区間として再利用されないことを確認する。NavMeshAgent／Root MotionがRoot位置を二重更新しないこと、全Plan／Group無効化でGenerationが進み旧軌道・未来姿勢と、Jobベイク導入時のVP入力準備結果が採用されないことを確認する。人形の切断成果物の実Commit拒否はJobベイク採用時のPhase 4.72で統合確認する。固定容量の最大Mob／Sample数、Background Queue満杯、Mid／Farのunderflow、Near Live Fallback予算超過では再確保・Main Thread待機・無制限再試行を起こさず、規定のState全体Holdと固定Profiler Counterへ低下し、既存MobPlan lifecycle Traceが矛盾しないことを確認する。ORCA、依存Graph、Flow Fieldを無効のままでもPlayableで、多少のMob重なりを許容してCPU、Nativeメモリ、Queue枯渇率、計画再利用率を測定し、Jobベイク採用時だけPhase 4.71で候補からの未来Pose／VP準備要求・結果取得・失効を確認する。先行切断完了率・Commit率はその後のPhase 4.72で測定し、不採用時は両確認を要求しない |
| T-093 | 剛体切断Local Planeリベース | 実姿勢を維持し、面採否と切断受付判定をGeometry Job完成から独立させ、受付済みの全表示・物理処理が同じ操作面へ収束する | 任意Phase 4.55。未実装・延期・不採用を基本ゲームの不合格にしない。19.5.1の対象、接線／法線並進、回転、重心と原点不一致、左右眼差、閾値直前／一致／超過、Near Plane／非finite／Profile欠落、Scope外を小さい固定Fixtureで確認する。同じDescriptor／実SnapshotでMesh完成済み・遅延中・Job失敗を切り替えても採用面と7.6の片側空判定が同一で、Ready前にNo-opならCutOperationId／Pending Cut／操作／世代／表示／物理／仕事を作らず、受付済みならCutOperationIdとPending CutがLogicalCutOperationより先に公開され、Operation未公開のTemporary表示と後のOperation／Provisionalが同じ採用面を使うことを確認する。Operation公開時はPending Cut由来の描画位置と面を維持して重複Recordを作らず、同面Job継続／後追い、後着不採用Job回収、世代更新前Snapshotと受付後世代の対応を検査する。処理中は同一Sourceへの再切断を拒否し、Final／Logical公開成功後は子への切断を受付可能とし、Final不成立時は7.1に従ってSourceを退役する。FinalまでActorが動いても巻戻しなし、面変更なし、自前B-repの由来Convex包含、実速度継承、Impulse一回をT-091の代表caseで確認する。SourceSlashPlane／有限Sweep不変、別対象の面差とCap Group分離、LogicalCutOperation作成Traceに先行できるPending Cut面の記録・復元と欠落／重複／混在拒否、後着Operation Traceとの相関を検査する。少数の動画像で近似誤差とVFX差を目視し、既存Captureを使用する。全距離・視野角・閾値の直積、全Contour最大誤差証明、新しい長時間性能SLAを要求しない |
| T-094 | Building World D6 lifecycle | 7.2.2の予定Depthと正式公開、D6構成・寿命を確認する | 少数Syntheticで建物true／非建物false・初期Depth 0、建物だけの予定Depth更新、Provisionalと正式子への同値継承、直接Final、Abort時非公開、Anchorあり／なし、有限・非負なLimit、生成時相対位置・回転0とFree／Limited設定を確認する。Final handoffで再加算・D6再生成・基準リセットせず、所有Actorと同じ寿命で一度だけ回収する。整数上限・underflow境界、Solverの変位・jitter等を合格閾値にしない。7.9の任意処理は各後続Phaseで確認する |

未来用Jobベイクの確認は少数のSkinned Fixtureと既存の共用Geometry検証を使う。Phase 4.65は固定Rig Poseの限定実装で、4.5.2の条件を揃えたSkinnedMeshRenderer.BakeMeshとの必要属性・頂点／Topology対応、非同期回収、21.2の負荷比較を行い、導入の採否を決定する。対応入力の少数比較にRendererとRoot Boneのframeが異なる例および非単位scaleを含め、入力をWorldへ写した形状も比較してscaleの二重適用・適用漏れがないことを確認する。効果がなければ不採用で完了し、本体DAG／VPプール接続、MobPlan、実命中、Ragdoll、人形切断の完成を要求しない。Phase 4.52の基本切断はT-008で確認し、T-018のPose／前提検証とT-092の計画単体確認を後段統合待ちにしない。

Jobベイクを採用した場合のPhase 4.72では、T-008／T-018／T-092の人形先行切断への接続として、準備済み結果の採用、未完成・Pose不一致時の現在Pose同期経路、世代失効後の後着結果回収を少数ケースで確認する。既存Geometry／Physics Commitへ接続し、4.5.6の直接Index出力・独立転送・現在frameでの公開条件と、実Actor／Animator内部Stateを予測へ巻き戻さない規則を維持する。21.2の同時候補負荷と先行完成・採用状況を測定する。新しい試験ID、専用Framework、全Asset・全変形機能の網羅試験は要求しない。

T-093の再切断・世代照合は8章に従う。子孫切断の受付だけで祖先GeometryをRejectせず、外部authority喪失と区別する。Operation公開前の受付見送りと非再生はT-007で確認する。

T-005／T-085／T-086の数値部分はPhase 3.9で確認する。削減不能等は数値結果として呼出側へ返す。実Actor／cook、Abort、Provisional、公開・退役との接続、およびT-059／T-069／T-074／T-091の実物理部分はPhase 4で確認し、数値部分だけで統合試験を完了扱いにしない。Probe由来のFixture・生成器・Verifierは必要なものを選抜・改変でき、検証方法とケース集合は17章の実装詳細とする。Verifierを製品Runtimeへ接続しない。

T-005の削減成功例をPhase 3.9の再切断とPhase 4のT-091 cook／handoffへ接続し、採用した削減済みB-repが次回入力となり、既存の自前B-rep包含・frame・親質量保存を満たすことを確認する。縮小由来の接触消失・運動変化は7.2の品質許容とし、Actorの巻戻しや不正形状の公開を成功扱いしない。削減専用の試験ID・Fixture体系・cook結果抽出基盤は追加しない。

T-007／T-083でA Geometry未完了のままBを受付・公開し、A Commitで現在B子孫へGA＋Temporary Bを適用してTemporary Aだけを回収する。B Kernel・CommitはA Commit後とし、同じ更新内でもよい。片枝の退役は生存SiblingのCommitを妨げず、全読者退役なら履歴完成だけの計算を続けず既存IncompleteOperationTraceとする。描画経路固有DescriptorはRendererが構築し、同じ描画Snapshotの全PassでCommitted GeometryとTemporary集合が一致する。

T-074の点Anchor確認をT-091のProvisionalへ接続する。同じ旧Cooked Geometryを共有しても分類側だけがAnchorを継承し、再切断で祖先Bufferから復活せず、Final Shape交換と同形状再cookで位置・所属が変わらないことを確認する。少数の既存Fixtureを使い、新しい試験体系は追加しない。

T-091のLease確認は、各旧Cooked GeometryをProvisional Shapeへ結び付ける前の取得、部分構築時の非公開、保護参照の解消と必要なPhysics Step後の一度だけの返却、最後の参照・Lease前のGeometry破棄禁止に絞る。少数の失敗・交換・Staleでuse-after-free、二重返却、leakがないことを確認し、内部破棄順は固定しない。cleanupの遅延でTransactionを延命せず、TimeoutだけではLeaseを返さない。

## 15. 実装ロードマップ

Phase IDは文字列とし、0.5と0.50、1.5と1.50、4.50と旧4.5を同一視せず、一括改番しない。Slash UX系列は0.5→0.51→0.52→0.53→0.54→0.55、表示系列は0.9→0.91→0.92→0.93→0.94→1→1.50→1.51→1.52→2→3→4→4.1→4.3とする。両系列は0.5後に並行可能で、1.50は0.5と0.94／1を前提とし、0.51～0.55を待たない。0.55と4.3の双方から4.50→4.51→4.52へ合流し、基本Playableを成立させる。4.52以降は4.53→4.54→任意4.55と、4.52→4.61→4.65→4.70の分岐とする。4.71は4.65採用時だけ未来VP準備を既存DAG／VPプールへ接続し、4.72は4.52＋4.53＋4.71を統合する。4.55は基本ゲームと4.61以降の必須依存にせず、4.65不採用なら4.71／4.72を省略して4.70から4.8へ進む。いずれも未完了負債にしない。

Phase 0.21は10.2.3の受入れ責務を定め、最初に必要とするPhaseが、その用途に必要な登録・内容識別・所在解決・Harness接続を具体化する。初回利用元と接続先は実施記録へ残し、特定のPhase番号や全Consumer共通API・汎用Runnerを先に固定しない。独立した先行作業でも利用Phaseとの並行実装でもよく、Phase 0.2の完了・再開や未実装Consumerの前倒しを要求しない。個別データの持込みや独立した他Phaseの進行を0.21完了待ちにせず、既存Harnessへの入力は先行できる。導入後の入力更新・参考実行と、後続用途に必要な拡張・変更は通常作業とし、同じ受入れ責務の範囲では0.21を再開しない。

Phase 2.9は6章の共用Geometry数値Kernel、Phase 3.9は7.2のPhysics Convex数値Kernelを先行実装する独立分岐とする。2.9はPhase 0.x～2、3.9はPhase 0.x～3の完了を着手・merge条件にせず、専用branchで開発し、ゲーム経路に未接続でもmainへ早期mergeできる。Phase 3はPhase 2までの基盤とその時点の2.9のKernelを表示／Stencilへ接続し、Phase 4はPhase 1～3と3.9のKernelを実物理へ接続する。先行mergeはAPI・layoutの凍結ではなく、残る意味契約と現行利用箇所・試験を維持して破壊的変更・置換・削除できる。旧API維持・互換層・migrationを要求しない。前段の基盤・合成入力をKernel利用待ちにせず、Phase 1～3のHarness内合成Final Physics入力も維持する。

Phase 2.9の初期移植元は `zantetsuken-mesh-cut-probe` の `FINAL_REPORT.md`（2026-09-09）が選定したBurstのscan＋edge hash＋分割capとし、seam対応と本隊のAoS／追記出力／6章契約への適合を行う。これは初期実装選択であり方式の恒久固定ではない。Probe全体、不採用Backend、全測定の再現、Probe側の描画・転送設計の採用はmerge条件にしない。移植元の未対応を本隊入力の除外条件へせず、6章との差を実コードと選抜ケースで閉じる。

3.9の初期移植元は独立Probe `zantetsuken-convex-cut-cook-probe` の `REPORT.md`（2026-09-13追補）のA-Walk、Burst R0→R1、double質量計算とする。これは初期実装選択であり方式の恒久固定ではない。Probe全体、比較Backend、旧managed prototype、全測定の再現はmerge条件にせず、公開SyntheticとLicensed入力の既存分離を維持する。

今回追加・細分化するPhaseは既存実装を段階的に完成させる境界とし、後続機能の仮実装やPhase専用のRuntime状態・Coordinator・Scene・Assembly・Logger・Schema・Receipt／Proof・引渡しartifactを追加しない。0.5～0.55は一つのSandbox Sceneを継続使用し、0.5時点の空Sceneを恒久保存しない。0.53のCoreは4.50へ、1.51／1.52のShader・PassはPhase 2へ接続する。観測は既存Trace／Profiler、画面・Consoleと19.1.12の開発情報を使う。調整値は暫定とし、操作値は0.55、製品Wave容量は4.50前、Stencil予算はPhase 2以降の既存Open Itemで判断する。

1.50～1.52は、既存またはテスト内で手続き生成した少数の固定合成Fixtureで能力を確認する。固定Descriptor／Color割当てを新しい製品modeとして残さず、Dataset・Generator Framework・別描画Backendを追加しない。不成立でも事実と再現条件を本書へ記録すればProbeは閉じられるが、依存する後続PhaseとPhase 2へは進まない。代替経路・部分Bit・Stencilなし継続・同期Fallback・Recoveryは自動追加せず、別の人間判断へ戻す。T-011／T-067／T-089は共通の低レベル確認を再利用し、Phase 2で製品状態との接続を確認する。新Test IDや同じ試験行列の複製は作らない。

| 段階 | 焦点 | 主要成果物 | 完了条件 |
| --- | --- | --- | --- |
| Phase 0 | 非VR基盤・観測（完了済み） | 固定Unity環境、非VR観測、Profiler／Traceと対応Capture | 必要な性能と因果関係・Frame相関を確認でき、観測資源をboundedに管理しCapture失敗をGameplayへ拡大しない。形式変更だけで再実行・再承認しない |
| Phase 0.1 | Capture非同期化（完了済み） | Main Threadを長時間待たせないCapture処理 | 21.15の非同期・容量・資源寿命・故障分離を引き継ぐ。Encoder、Worker、保存・通知形式、検証方法は実装詳細とし、今回の改訂で作り直さない |
| Phase 0.11 | 短時間NVENC確認 | 対応環境のGPU画像から短い映像を生成する非同期Capture | 21.15のCapture経路での複数Frame確認と、非待機・容量・寿命・故障分離を満たす。既存の承認済み方式で完了でき、固定fps・Frame数・時間・試験階層は要求しない。製品連続録画形式は確定しない |
| Phase 0.12 | 可変長Trace Writer | D-159と21.16のprivate Writer、producer専用固定容量Payload／Runtime Index Ring、固定Event mask、bounded Drain、stop／join後の単純sealを同一移行系列の内部backendとして実装する | 通常writeに共有locked RMWと実行中allocationがなく、payloadコピー完了後だけRuntime Indexが公開される。lane FIFO、wrap、Index／Payload容量不足、oversize Drop、固定件数Drain、最終Drainを検証し、現行WriterとCPU時間、copy byte、allocation、Dropを比較する。Release既定の切替と旧経路削除はまだ行わない |
| Phase 0.13 | MemoryBounded Paged Trace History | ProfileでPage size／Page数／総容量を決めてRun開始前に確保するPayload Page列、Pageごとの`CommittedByteCount`、History全体の64 bit `CommittedRecordCount`を0.12 backendへ追加する。History Index、Page状態enum、live Snapshotを持たない | 21.16.3の最大record全体のPage収容条件とProducer Lane容量条件を開始前に確認する。record全体を単一Pageへ書いた後だけcommit値を進め、Page末尾不足、History満杯、確保不能を待機や拡張なしでReject／Dropできる。停止後Viewは全record配列を生成しない。Release既定はまだ切り替えない |
| Phase 0.14 | 可変長Trace保存・読込みと切替 | 21.16.4のboundedな保存・読込みを接続する | 記録の相関と不完全性を維持し、全record配列を作らず保存・読込みできる。Release既定の採用と製品接続先がない場合の完了条件は21.16.1に従い、置換済み旧経路を削除できる。形式・旧Reader・Goldenの維持は要求しない |
| Phase 0.2 | 採用Fixtureの凍結 | 少数Geometry、表示切断／Physics Cook／正しさ確認の用途対応、公開Synthetic／非公開Licensed入力 | 10.2.2の採用ファイルと用途対応をmergeし、後続から利用できる時点で完了する。旧quota・全再生成・再監査・形式統合を条件にしない |
| Phase 0.21 | Reference Asset Intake | 最初の利用Phaseに必要な10.2.3のAsset登録・内容識別・所在解決と一つのHarness接続 | 少数の実Assetで登録・更新と保持したrevisionの読込みを確認し、一つの対応Harnessへ限定サンプルを渡して実処理・結果記録まで通す。結果から実体と存在する参考資料へ辿れ、参考入力の有無・更新・失敗が標準実行の対象・集計・合否を自動変更しない。登録ツールだけで完了とせず、全Asset成功・全件実行・全Harness対応・乖離解消は要求しない |
| Phase 0.5 | 最小XRスモーク | 共用Sandbox Sceneの初期状態、OpenXR、Quest 3S有線Link、左右Grip Pose＋暫定固定Offset、BladeAxis／EdgeDirection／SideNormal、位置・回転の利用可否を表す一つの追跡有効性、Single Pass | T-014だけで基本XRを確認する。Profilerは90Hzモードと明白な継続破綻の確認に使い、速度履歴、Gate、Stroke／Plane、Wave、校正UI、製品性能SLAを含めない |
| Phase 0.51 | Blade Sample／追跡不連続 | 刀姿勢・軸・Cut Sample Point・Emitter・時刻・追跡有効性を後続処理へ渡す内部Sampleと履歴Reset | 固定Pose列と実入力の左右で、位置または回転が無効なSampleを除外し、追跡喪失前と復帰後を速度区間として結ばない。型・field列は固定せず、速度閾値・Gesture・調整UIは含めない |
| Phase 0.52 | Gesture受付／Plane候補 | Cut Sample Point速度と長軸成分除外、Edge Lead Score、暫定閾値、accepted samples、Stroke Begin、SourceSlashPlane候補、復路拒否・再準備 | 少数固定Pose列で往路受付、復路／峰側拒否、刀を返した新Stroke受付、斜め振り、復帰後の新Sample蓄積を確認し、受付列からfiniteなPlane候補を得る。Latch・Frame確定・SlashId・Waveは含めず、閾値は0.55で調整する |
| Phase 0.53 | HitなしSlashWave Coreと第一候補 | 19.1のLatch／Frame、Emitter、初期Segment、19.1.5.1第一候補、Raw／Accepted Span、Live／Frozen Guide、Close、速度・Lifetime、前回／現在Segmentと閉Sweep領域、SlashWave VFX、開発用有限Wave格納 | 固定Pose列からGesture／Plane→有効FrameとLatch→初期Segment→Raw／Accepted Span→Close／Frozen Guide→Expireまで動作する。19.1のfinite条件・公開順序・満杯時規則を使い、Span非減少、Invalid保持、Plane／軸不変、CloseとExpireの分離、容量返却・複数Waveを確認する。少数Waveで19.1.8の面内配置、飛翔・Span拡大への追従と静的Geometry再利用を確認し、毎更新・毎描画のためのVertex／Index再生成・書換え・転送がないことをコードと必要時のProfilerで確かめる。Query・Hit・Cut State・Prediction・製品容量値は含めず、第一候補をモックで代替しない |
| Phase 0.54 | Sandbox診断・再生・比較UI | 同じCoreに19.1.12の可視化、実装済み方式と値の切替入口、Pose記録再生、Current／Pinned比較、非canonical Presetを接続 | 第一候補を実入力・同じ記録Pose列で観察・再生・比較できる。Latch／Frame変更は未Latch評価、Span／Close変更は後続Waveだけへ反映する。比較候補がなければ常設の偽候補を作らず、交換境界の確認だけ一時Test Double等を使える。操作感の採否・最終値は決めない |
| Phase 0.55 | Slash UX実機探索・調整 | 完成済みSandbox、利用可能な構成、暫定Preset、同時生存Wave数の観測 | T-034。Quest 3S有線Linkで第一候補から開始し、人間が試すと決めた候補を必要に応じて追加・比較・調整する。往路・復路・返し・斜め振り・連続斬り・追跡復帰を実機確認する。第一候補の採用や全方式比較を義務づけず、採用構成を4.50へ引き継ぐ。同じ出力契約内の変更だけで0.51～0.54を再開しない |
| Phase 0.9 | 読み込みとUnity Mesh表示 | 少数の代表AssetをUnity Meshで表示し、同形状InstanceはMeshを共有する。並行光源1つ＋ambient、基本表示・影 | 別Transformの複数配置で共有Meshと影を確認する。切断登録のTopology試験を前倒ししない |
| Phase 0.91 | Unity Mesh表示最適化Probe | 同じ代表SceneでForward、Forward+、Forward+＋GRDを比較し、Mesh／Material共有と実際のGRD適用状態を確認する | Main Threadの描画準備・関連待ち、Render Thread、GPU時間から採用構成と理由を記録する。速度Gate、多数ライト・GPU Occlusionの全組合せ比較は要求しない |
| Phase 0.92 | VP Stage 1 | 明示操作でMesh→CPU AoS／Indexプール→GPU表現へ変換し、Direct・非indexed＋shader-side indexing／属性Pullingで描画する。AcquireReadOnlyMeshData等による取得、更新範囲の集約、両プール追記、Mesh／VP混在と影、複数Geometry参照の選択・表示構成・別Transform Instance | 少数Geometryで変換・混在表示・影・複数参照構成・選択と、互いに離れた複数部分を含む固定Geometryを一つの連続Index範囲として描けることを、Component検出・列挙・専用Metadataを前提とせず確認し、変換マイクロベンチを取得する。小さい初期GPU容量から1回拡張し、コピー・描画境界での参照切替後も既存表示を保ち、旧参照の更新・退役と投入済みGPU利用の終了後に旧Bufferが一度だけ解放されることを確認する。変換・転送費用と実際の表示開始フレームを既存計測で確認する。実切断・人形切断は要求しない |
| Phase 0.93 | 消去と範囲アロケーション | 表示Instance／Geometry参照退役、4.5.3の予約・公開・寿命管理、退役Indexと未使用・失敗予約のbest-effort再利用。Published Vertex再利用・コンパクションなし | 少数の合成Jobで共通入力読取りと非重複出力への並行書込み、完了後公開、未使用予約回収、参照中Indexの再利用抑止、退役後再利用、予約不足後の再実行、完了回収後のReserved所有権引継ぎ、CPU範囲Publishedが内部読取り許可であることを確認する。切断器へ依存しない |
| Phase 0.94 | VP Stage 2 | Stage 1のGeometry表現・アロケータを維持してIndirect・非indexed＋shader-side indexing／属性Pullingを実装し、描画要求の集約・引数管理・個別発行を見直す。正負連続Index配置による粒度削減をIndirect APIの自動融合とみなさない | Stage 1と同じ小規模代表Sceneで機能を維持し、実際の発行経路とMain Threadへの効果を比較して採用を判断する。固定改善率・全Scene高速化は要求せず、効果が乏しければ人間判断でStage 1を採用して進める |
| Phase 1 | 即時切断／Dispatch境界 | Phase 0.9～0.94のVP基盤、合成Geometryと点Anchor、正負論理子・切断履歴の公開、単一clip・仮分離・簡易断面、Harness内の合成Final Physics入力（低頂点1 Hull）、片側空No-op・受付上限、4.4の共有Dispatch・物理仕事の投入余地・取消・非blocking完了回収 | 少数の合成入力と選抜済みFixtureで即時表示を確認し、Harness内合成Final成功／失敗からT-074の公開・Abort・Anchor配分を確認する。実Convex切断・Rigidbody・cookと固定物理の結合はPhase 4へ置く。支持を理由に描画を省略しない。未公開親への受付制限、No-op／混雑時の非変更、公開前の子先取り禁止を確認する。製品Preprocessorを前倒しせず、T-090の合成Workで同一フレーム内の複数Dispatchを確認し、後続Phaseを内部Queue型へ依存させない |
| Phase 1.50 | 選択済みVP経路のXR／Single Pass確認 | 0.94で採用したVP経路、既存Geometry参照・Instance Transform・Draw DescriptorのColor／Depth描画 | 0.5とPhase 1の完了後、Quest Linkで一つのGeometryを複数Transformで表示し、左右眼のGeometry／Transform／Instance選択、片眼欠落がないことを確認する。Clip・Stencil・Shadow品質・Stage再比較・製品90fps SLAは含めない |
| Phase 1.51 | VP Hybrid Clip確認 | 少数の固定合成入力による面・Side・Offset、Raster 8＋Pixel 4。入力field構成と具体的なFixture列は実装詳細 | Color／Depth／ShadowCasterで同じ固定入力を使い、先頭8面のSV_ClipDistanceと続く4面のPS clip、左右眼の一致を確認する。Pending Cut、候補選択、13面以上、Cap／Stencil、Shadow画質・性能評価は含めない |
| Phase 1.52 | VP Stencil基本確認 | 正向きの固定合成閉Geometry、既知Cap Polygon、固定Clip Descriptor、少数の固定割当てStencil Color | 全8bitを使い、Colorごとの128初期化、Wrap加減算、Ref 128 / Comp LessでS>128だけがColor／Depthを書くこと、全Volume後に全Cap、Color間の再初期化、左右眼一致を確認する。Cap生成・Cull・Compatibility・Residual Support・Color割当て・Pending Stateを前倒ししない |
| Phase 2 | 仮断面・影強化 | 表示／Stencil共用基底Geometry、6.2の入力Gate、`RenderCutTopologyMap`、T-084、ゼロKerf、LogicalCutOperation、TemporaryRenderCapRecordSet、OBB交差Cap Bounds Polygon、両眼Frustum／Facing Cull、128初期化の正符号8bit IncrementWrap／DecrementWrap Stencil、Residual Stencil Supportの保守的投影競合、符号保存のCapCompatibility Group、`MaxStencilColors`と最後の統合Color、Color単位Volume／Cap Batch、`TemporaryClipConstraintCandidateSet`、`SV_ClipDistance` 8面＋PS `clip()` 4面＋5.2のPlane overflow処理、共通トゥーンの粘土色グレー、Temporary／Committed断面デバッグ色、ShadowCaster用同一Hybrid Clip／Offset、XR両眼対応、Pending Cut／Stable履歴管理、T-067／T-089 | 1.50～1.52の成立後、そのShader／Passへ製品状態・Cap生成・Batchを接続する。2～4連続切断と複数対象で、表示とStencil Volumeが同じ合格済み基底／Stable Geometry、Topology、windingを参照し、用途別Geometryや描画時のGeometry再検証を持たない。通常Colorは左右眼の非互換Residual Supportを分離し、Color数を固定上限内に保ち、同じColorでは全Volume後に全Capを描く。Self-intersection、別Topologyの重複／Coincident、Internal／Nested、全体反転を向き保存で受理し、共通入力Gate不合格は切断対象へ登録しない。符号証明、向き正規化、Winding上界、Count容量分割、符号別Groupは作らない。`S=(128+W) mod 256`と`S>128`を使い、範囲外は5.2の品質例外とする。最後のColorでは混入、欠落、余計なCap、誤Depthを許容し、GPU時間は測定対象に留める。8bitを排他利用できない構成は4章の共通Player終了に従い、部分Bitや代替経路を持たない。候補面は古い未Commit祖先制約を優先するdependency-closedなstable順で全Pass／両眼へ共有し、8面をRaster、続く4面をPixelで処理する。超過した後発面は即時Stencil VolumeをsubmitせずCap板と論理／背景処理を残す。Cap pair／Coverage探索、Cap単位Buffer compaction、Mesh部分更新、多段Fallbackを行わない。Color割当ては5.6の実装自由度に従い、全Graph構築を必須としない。Camera内部／Near Plane近傍では5.2の品質例外を許容する。Shadow MapではStencil Capなしの影近似を使用する |
| Phase 2.9 | 表示／Stencil共用メッシュ切断Kernel先行実装 | 6章のTriangle切断・属性補間・Contour／Cap生成・Topology対応更新を行い、caller提供のグローバルVB／IB範囲へ出力するBurst数値Kernelと検証コード | 現行Unity環境でbuild・実際のBurst実行を確認し、6章の共通契約、seam、Cap、再切断、既存Vertex再利用、新規Vertex・正負Indexの直接配置と容量安全を代表入力で確認する。製品アロケータ・Job wrapper・GPU・Renderer・Geometry Commitへ未接続でも完了する。性能確認は14章、検証詳細は17章に従う |
| Phase 3 | 共用表示／Stencilジオメトリ統合 | Phase 2.9の数値Kernelと、既存VPプール・範囲所有権・共有Dispatch・GPU転送・Renderer・4.5.6の祖先順Geometry Commitとの接続 | T-006／T-083の統合部分を確認し、数値確認は2.9を再利用する。合成Final Physics／Logical公開後の正負Geometry・Boundaryの後着、A→BのKernel／Commit順序、Temporary置換と全Passの共用を成立させる。新規Indexの転送1回／再利用時0回、空Geometryへのdummy非生成、Stale回収・予約不足の非公開を維持する。重い頂点処理をMainへ戻さず、通常の全Job／GPU待ちを追加しない。容量・内部エラーは4章／4.5、出力契約は6章に従い、実cookはPhase 4へ残す |
| Phase 3.9（完了済み） | Physics Convex数値Kernel先行実装 | 7.2のB-rep clip、非交差継承、内接削減、質量特性、worst-case容量照会を行うBurst数値Kernelと最小Harness | 現行Unity環境でbuild・Burst実行でき、7.2／7.6の数値契約、削減成功、局所退化を含む採用B-repの再切断、予約範囲内の実行を代表入力で確認する。中心近傍の共通局所frameを使用し、Actor・Anchor・Cook Frameとの接続はPhase 4に残す。MeshData、製品アロケータ・Job wrapper、Mesh／Bake／Actor／Commitへ未接続でも完了し、Phase 4／4.1の完了とは扱わない。検証詳細は17章に従う |
| Phase 4 | 物理 | 7.1の短寿命PhysicsSplitTransaction、7.6のrobust support、実Actor／Shape／cookとLogical Publicationの一体公開、旧Cooked Geometry Lease、anchor-offset D6、Phase 3.9の数値Kernelを使う7.2のOwner単位Cut/Cook統合・事前容量予約・P1 recenterの物理接続、単一Cooking Profile、初回速度継承・Final handoff、0.5G仮設定 | Phase 1～3のHarnessとPhase 3.9の数値Kernelを実物理へ接続し、T-005／T-059／T-069／T-074／T-085／T-086／T-091を確認する。正常成功は正負2所有者、Geometry空はRendererなしとし、Final先着・Provisional構築不能・Final不成立・Stale・個別退役を7.1で閉じる。通常LogicalFragmentを単独退役する低レベル処理も本Phaseで実装し、7.9のGC Policyは前倒ししない。切断・BakeのMain Thread停止を避け、暫定的な実行枠・メモリ予算で既存Fixtureを回帰する。Unity経路の要件違反だけD-086で再検討する |
| Phase 4.1 | Cut/Cook Profiling | Phase 4の製品経路と代表Fixture、既存Profiler／Harness | T-076で7.5の費用を確認し、O-035／O-039の暫定実行枠・メモリ予算を調整する。保存形式、分位、反復数は実装詳細。Slashの到達Deadlineへの適用はPhase 4.53へ分ける |
| Phase 4.2 | Player非接触Locomotion | Player Layer非接触、Level初期化時の固定PlayerLocomotionOccupancy、候補次姿勢Overlap Reject、T-088 | 人工移動の要求全体Rejectと、物理所有者・切断・Commit・Fragment・GCへ追従しない固定集合をT-088で確認する。実空間HMDはClampせず、Camera被り・内部視点はD-131の許容に従う。退出処理や将来のOccupancy更新を要求しない |
| Phase 4.3 | 建物由来子のWorld D6と一般外部Joint撤去 | 7.2.2のIsBuildingDerived／BuildingSplitDepth、通常1→2公開でのWorld D6生成、指数Limit、Actor寿命と既存失敗境界への接続、既知Constraint識別、T-094 | 手書きSyntheticで生成・建物由来だけの予定Depthと正式公開・Abort・Final handoff時の維持・構築不能を確認する。一般外部Jointの継承・付け替え・保護・予測を要求せず、4.54が既知Constraintを識別できる。拘束効果を保証せず、製品Recipe・5.6分割・5.7 GC・未来予測本体を待たず完了する |
| Phase 4.50 | 製品SlashWave Core | 製品のGesture入口、交換可能Latch／Frame／Span Candidate／Span Close Estimator、Latch済みFrame、0.53のCoreと0.55で採用した構成、Raw／Accepted Span accumulator、単一Segment、WaveLifetime、SlashWave VFX、複数生存Wave用の有限固定容量、追跡異常と再準備 | 発射時の面・軸・初期形状と評価設定が19.1.4に従って保たれ、Raw候補が減少・InvalidでもAccepted Spanが縮まない。完了済み過去更新区間を再評価せず、現在更新区間内のSpan増加領域は後続Hit Phaseの共通Sweepへ渡せる。Span Close後は採用方式の候補評価を使い、WaveはWaveLifetimeで有限終了する。Latch／Frameの切替は未Latch評価へ、Span Candidate／Closeの切替は後続Slashへ反映し、生存WaveはLatch時方式・設定と必要な一時状態を維持する。19.1.6とT-035に従い、公開前の容量確認と満杯時の新Latch見送り・同じStrokeの非再試行を確認する。実対象Hit、Cut、候補範囲、Predictionを要求しない |
| Phase 4.51 | Segment HitとProp現在状態切断 | 生成時の退化Segmentを含む共通閉Segment Sweep、現在採用Physics Convex集合とのNarrowphase、`LogicalFragmentRef`系譜単位消費、`SlashHitConfirmed`、既存7.6受付、現在状態からの通常Prop切断 | Predictionを全て無効にしても、Slash生成→飛翔→実Hit→即時表示→Geometry／Physics処理の基本Propループが成立する。消費済み系譜と祖先関係を持たない別Fragmentへ個別Hitでき、消費済みFragmentの子孫は同Slashで再切断しない |
| Phase 4.52 | Humanoid現在Pose切断 | 命中時Bone Pose Snapshot、同期SkinnedMeshRenderer.BakeMesh→VP、共用切断、骨Physics Proxy分類、物理移行、T-008 | Jobベイク、未来Pose、MobPlan、先行成果物なしで動作中NPCを現在Poseから切断できる。ここでProp＋NPCを含む基本垂直スライスを完成とみなせる |
| Phase 4.53 | 静止／姿勢固定対象の先行切断 | 導出可能な場合の保守的Candidate Flight Bounds、導出不能時の有限な先行準備範囲、候補列挙、DAG、Ready Workの共有Dispatch投入、静止姿勢の投機Geometry／Convex、実Segment HitだけのCommit Gate、空振り回収、Phase 4.1性能曲線のSlash Deadlineへの当てはめ | 静止対象で先行成果物を再利用でき、有限包絡を保証できない構成では先行準備範囲外を未準備のまま許容する。範囲外を含む実Hitは4.51の候補検索・現在状態切断へ戻り、Gameplay Clamp、範囲拡張、再探索を要求しない。基本ゲームの成立条件にしない |
| Phase 4.54 | 自由飛行剛体の直接予測 | O(1)固定刻み予測、`DirectRigidPredictionEligibilityGate`、WorldPhysicsProfile／FixedStep統合、T-017 | 対象内だけ先行成果物を作り、対象外または検証不一致は4.51へ戻る。Local Planeリベースは必須にしない |
| Phase 4.55（任意） | 剛体Local Planeリベース | 現行19.5.1／D-149／T-093の限定実装・比較・採否 | 基本ゲームまたは4.54の必須条件にしない。未実装・延期・不採用でも後続Phaseへ進める |
| Phase 4.61 | 未来Animation Pose評価 | `ExplicitAnimationStateV1`、Clip Catalog、`ResolvedAnimationPoseInput`、交換可能Pose Evaluator、Playable／Pose Table比較、T-018 | VPベイク、切断、MobPlanなしで任意対象StepのRig Poseを評価できる。Backend選択は後から交換可能 |
| Phase 4.65 | 未来予測用Jobベイクの比較・採否 | 4.5.2の不変Rig Pose＋共有source skinning入力から共通CPU側VP入力を生成する限定実装と同期経路の比較 | 少数の対応済みSkinned入力と固定Poseで品質・非同期回収・Main Thread負荷を14章／21.2に従って比較し、人間が導入の採否を決める。効果がなければ不採用も正常完了。本体DAG／VPプール接続と人形切断の完成は不要 |
| Phase 4.70 | Mob未来計画 | RootTrajectory、`ExplicitAnimationStateV1`、PlanGeneration、Queue、T-092 | Jobベイク不採用でも計画単体で閉じる。未来VP入力、人形先行切断を要求しない |
| Phase 4.71（条件付き） | 未来VP入力準備統合 | 4.61＋4.65採用結果＋4.70を4.53の投機DAGと既存VPプールへ接続し、候補Pose／VP入力準備・失効・回収を行う | 4.65不採用ならPhase自体を省略し、未完了負債にしない。人形の実切断Commitをまだ要求しない |
| Phase 4.72（条件付き） | Humanoid先行切断統合 | 4.52の現在Pose経路、4.53の投機経路、4.71の未来VP入力を接続 | 有効成果物採用、未完成／Pose不一致時の4.52同期経路、世代失効回収を確認する。4.65不採用なら省略可能 |
| Phase 4.8 | OpenXR Projection Capture＋正式録画判断（4.70と有効化した4.71／4.72の後） | 21.7.4の固定構成、Release前GPU Copy、Trace相関と負荷確認 | 切断PoCの異常を提出画像とTraceで調査でき、想定外構成ではCaptureだけを停止する。連続録画が必要ならT-054の実測から容量・停止時間を満たす方式を選び、0.11の短時間確認方式を自動採用しない。API Layerまたは連続録画を個別に見送れる |
| Phase 5.5 | Asset自動前処理 | Phase 0.2の採用入力と利用可能な知見を参考に、完全なPortable Blender Manifest／Bootstrap、固定版ヘッドレス実行、Asset別Recipe、表示／Stencil共用Cut Geometryと`RenderCutTopologyMap`、必要な幾何Topology Metadata、Component単位の閉鎖・manifold・局所winding整合、見た目を保つReduction、UV／Material再構成、点Anchor入力、建物由来Metadataと初期Depth、Compound Physics Proxy、検証、キャッシュを実装する | Phase 0.2でRejectした複雑Assetも対象に含め、代表家具・車・建物を別PCでもGUIなしで再現生成する。相互に食い込む閉ComponentをBoolean Unionせず共用Geometryへ通し、FBX control point／Import topologyからattribute seamを越える安定したTopology対応とcanonical posed positionを生成し、6章の共通入力Gateに合格したGeometryだけを切断対象へ公開する。開放Boundary、局所winding不整合、edge／vertex Non-manifoldはAsset修正またはRecipeで解決し、解決しない入力を切断対象外とする。用途別Stencil Shell、小部品専用分類・消去用ID・Shard、符号証明、signed-volume分類、向き正規化、Winding上界Metadata、Runtime修復を生成・保存しない。Geometryの同Side所属は7.6に従う。製品用Strict Solidを生成・検証・Fallbackせず、その成功を代表Assetの合格条件にしない。Phase 1／4の合成入力を実AssetのGeometry・Convex・点Anchorへ接続し、Phase 0.2より広いAsset範囲と製品品質を達成する |
| Phase 5.6 | 追加空間分割（任意） | 7.9の単一平面探索・範囲内部の面配分、新Index領域への振り分けコピー・必要転送・旧領域Free、既存Convex処理・cook・質量・点Anchor、7.2.2の建物Depth・子D6生成と親D6退役、非命中公開、全体1未回収試行と入力別抑止 | 大きなIndex範囲内の離れた部分を2物体へ分け、Vertex共有と読者寿命後の旧Index回収を確認する。成功後は通常再切断でき、不成立・無効時は元物体が通常完成状態で残る。通常切断とGCを依存させず、7.9.5の共有資源競合による遅延を許容する。Phase自体を省略可能 |
| Phase 5.7 | 表示なし物理物体の遅延回収（任意） | 7.9の確定空判定、物理所有単位の登録終了、既存Actor／Shape／システム所有Constraint／Job資源退役への接続 | 7.9.7のGC選択・接触中退役・共有資源寿命・所有D6の一度だけの退役・後着成果物拒否を確認し、無関係Siblingを維持する。追加分割の実装・有効化・成功へ依存せず、Phase自体を省略可能 |
| Phase 6 | コンテンツ | Synty City街区、10プロップ、単一並行光源＋ambientでのシェーダ統一、既製モーション | 垂直スライスとして一連の遊びが成立 |
| Phase 7 | 実測後最適化 | 端末別品質、破片LOD、既存Profiler／TraceとT-076による負荷確認、遠距離確定、ストレス試験 | ターゲット実機で性能予算を満たす。Schedulerを含め具体的な不足が確認された箇所だけ、別設計変更として必要な最適化を行う。初期Cooking Profileより再cookに実測上の価値がある場合、別設計変更として任意導入を検討する |

Phase 0.9～0.94はPhase 1より前に実施する。Phase 1.0という呼称も同じPhase 1を指し、既存Phaseは一括改番しない。各0.9xは先行成果を使いつつ独立に完了でき、即切断、実切断、Cap、物理切断、未来予測の完成をGateにしない。Stageは描画方式の段階でありPhase番号とは別である。0.94の実装・比較は必須とし、採用結果に応じてPhase 1へ進む。実行時自動Fallbackを追加しない。

0.92はMeshデータ取得、AoS変換／CPUコピー、GPU転送発行を分けて軽量計測し、SetData呼出し時間を実GPU処理時間と混同しない。初回確保・容量拡張は通常変換と別に測り、新たなBenchmark Dataset／Schema／保存基盤を作らない。

Phase 2.9の直接出力をPhase 3で既存VPプール・転送・現在frameでの公開へ接続する。切断Kernelの意味・Topology・Cap品質・世代の有効性・表示／Stencil同時公開は維持し、Final物理所属の確定をIndex配置・転送の前提にしない。詳細layoutやKernel内部の書込み方式は必要な段階まで未決とし、物理用の処理は7.2の実行分担に従う。Phase 4.53の剛体先行計算と、4.5.2の人形経路分離も同じ出力と準備済み範囲再利用へ接続する。0.92へAnimation／Pose Evaluatorを前倒ししない。Stage 3とGPU並行ベイクは実測に応じた後段最適化に残す。

Phase 4.61はT-018の独立未来Rig Pose評価で閉じ、Jobベイク、MobPlan、人形切断を要求しない。4.65は限定実装・比較・人間採否までを行う。4.70はJobベイクの採否にかかわらず計画単体で閉じ、採用時だけ4.71で未来VP準備、4.72で先行切断を統合する。不採用なら両Phaseを省略する。4.52は同期経路で独立完了し、少数Fixtureのために5.5の製品Preprocessorを前倒ししない。

Phase 1～3はHarness内の合成Final Physics成功／失敗入力により7.1のLogical Publication／Abortと4.5.6のGeometry順序を検証する。製品Runtime用の代替Physics Modeや公開schemaは作らない。SourceがActive中は受付を見送り、別LogicalFragmentは並行可能とする。Final／Logical公開後はGeometry未完了でも子を受付け、後続Kernelだけ祖先Commitを待つ。実Actor／cook／D6との一体公開はPhase 4で確認する。

Phase 1は手書きのLogical Convex Cell参照、finiteな点Anchor、Fragment Physics Frame内local位置を使い、CellのAnchor集合への半空間継承、所有単位内にAnchorがあれば固定・なければ動的とする判定、正負の確定子とOperationの原子的な公開を純粋データで確認する。Phase 4は同じ規則を実Convex系譜、旧Cooked Geometryを共有するProvisional、Final Shape引継ぎへ接続する。Phase 5.5は固定支持を設定する対象AssetのRecipeで初期Cellと点Anchorを生成する。Phase 0.2の採用入力へ製品用Anchorの生成を遡及要求しない。

Phase 4のcook待ちは7.1の一度だけのProvisional構築と直接Finalの順序に従う。通常Pendingと構築不能を区別し、成立不能はSource退役へ閉じる。Final handoffで採用する自前B-repの包含は7.2に従い、Colliderのpose／速度を補正して救済しない。旧Cooked Geometry共有・D6・Leaseの確認はT-091へ集約する。

Phase 4.3はPhase 4.2の後、4.50の前に置く独立Runtime Phaseであり、章番号4.3とは区別する。汎用物理のPhase 4、既存Baselineの4.1、Player非接触の4.2の完了条件へ建物D6を混在させない。Phase 4.54はその既知Constraint識別境界を使用する。

Phase 5.5の建物Recipeでは、10.2の一般契約に従う共用Cut Geometry・幾何Topology Metadata・Compound Physics Proxy・必要なFixedSupportAnchorと、IsBuildingDerived=true・BuildingSplitDepth=0を生成する。その他の初期所有者はfalse／0とする。壁板抽出、Box数・厚み、開口の扱い、点Anchor配置はAsset Recipeの実装詳細とし、一般のGeometry／Convex／Anchor契約の範囲で物理近似と配置の違いを許容する。Phase 0.2の入力利用は10.2.2に従い、製品metadataを前倒ししない。

Phase 5.6／5.7はPhase 5.5より後に置く独立した任意Phaseであり、双方または片方を省略してPhase 6へ進んでも未完了負債にしない。専用前処理・Runtime・試験をPhase 1～5.5へ前倒しせず、7.9.7を実装する機能だけに適用する。

## 16. 垂直スライス受け入れ基準

基本Playableは4.51／4.52の現在状態経路で成立させる。本章のPrediction・MobPlan・製品Asset・正式録画の確認は担当する後続Phaseへ適用し、基本Playableへ前倒ししない。任意4.55と条件付き4.71／4.72の省略条件は15章に従う。

Temporary Stencil Capの見え方に関する本章の受入れ基準には、5.2の明示的品質例外を適用する。物理Convexの内接削減による接触・形状・No-op・質量特性の変化には7.2の許容を適用する。即時表示は4.5.2の必要なベイク・VP変換後に始まり、残る準備費用による表示開始の遅れを許容する。4.5.4のGPU容量拡張に伴う停止と容量限界での開始拒否／終了を許容し、無制限の切断寿命を要求しない。固定状態による仮描画省略を行わない費用、7.9の任意分割でのIndexコピー・新旧範囲共存を許容する。実断面Geometryの品質、支持・物理の安全条件、世代の有効性は緩和せず、表示Commitは4.5.6の現在frame・参照・転送条件に従う。

- 刀の高速移動でも代表プロップを安定して切断できる。

- 必要なVP準備後に始まるclipと仮断面が両眼で一致する。両側が固定でもclip／Capを維持し、固定側のOffset／Impulseは0とする。幾何学的なFrustum／Facing Cullと8＋4面の既存制限を使い、支持による表示状態・再有効化待ちを持たない。

- 通常断面は全体と同じトゥーン陰影の粘土色グレーで統一され、仮断面から実断面への差し替えで特殊な質感変化が見えない。

- 即時切断物体のShadowはカラー表示と同じclip／分離Offsetに追従し、両面Shadow近似からStable実断面の片面Shadowへ移る際に目立つ影の跳びがない。

- 左右眼の一方だけで非互換な可視Cap Boundsが重なる複数の即時切断対象は通常Colorで分離され、OBB投影が重なっても両眼の可視Cap Boundsが非交差なら同一Colorへまとめられる。5.2の明示的品質例外を除き、別物体のStencilによる仮断面のはみ出しがない。Conflict Graphの明示構築は要求しない。

- 同じ全切断面とキャップ状態を共有する対象は重なっても同じStencil Colorへ統合され、別々に動いてWorld Planeが変わったフレームでは自動的に別Groupへ分かれる。

- FacingによるStencil処理の省略は、全Capが両眼ともFacing epsilonを越えて明確に裏向きの互換Groupに限る。片眼だけ可視またはepsilon帯内のCapをFacingで省略しない。

- 5.3に従い、デバッグ有効時は仮断面が赤、公開済み実断面が緑となり、無効時は両者が通常グレーとなる。元Assetの負UVによる通常表示を含む誤表示は許容する。

- Geometryは4.5.6の祖先順にCommitし、実体化したTemporaryだけを回収する。Final Physics／Logical Publicationを先に成立させ、Geometry CommitはDraw List構築・GPU完了・実表示を待たない。一つの描画Snapshot内の全Passで同じGeometryとTemporary集合を使い、GPU更新が対応Drawに先行する順序はRenderer側で保つ。

- 表示／Stencil共用VP Geometry切断はJob＋Burst主体、物理ConvexのCut/Cookは7.2の実行分担で処理する。Main Threadには通常命中の同期入力準備とBackendに応じたPose評価、表示VPの範囲管理・GPU更新・参照公開、物理Mesh公開とCollider／Rigidbodyの境界Commitが残り、導入採用時の未来の頂点スキニング・VP形式変換は4.5.2のJob経路で処理する。通常更新では未完了Jobへの強制`Complete`によるフレーム停止がない。

- 7.1のTransactionがProvisionalまたは直接Finalから正負2 Final OwnerとLogicalFragmentを一体公開し、Geometry未完成でも子を受付けられる。Provisional・Final構築不能はSource退役とし、部分公開や旧物理の恒久採用で救済しない。質量・速度・自前B-repの包含・D6・Leaseは7.1／7.2の条件を満たす。

- 既存境界から判明した個別物理の継続不能は7.1のAbort／LogicalFragment単独退役へ送る。共通Player終了対象は4章に限り、通常退役と混同しない。

- Phase 4.1で7.5／T-076の軽量測定を行い、Owner単位の実行費用・処理量・scratch使用量から実行枠とメモリ予算を調整する。

- Operation未公開の親と仮表示領域への後続切断は、無関係な確定済み対象を止めず、状態・世代・先行仕事を変えずに見送られ、保存・再実行されない。Operation公開後はGeometry未完了でも公開済み子を受付け、Kernelだけ祖先Geometry Commitを待ち、古いジョブ結果で形状が巻き戻らない。

- Phase 4.52では先行準備なしの同期経路で移動中のNPCを切断し、姿勢固定から剛体破片への移行が成立する。Phase 4.65で未来用Jobベイクの限定実装・品質と負荷の比較・採否決定を完了し、導入採用時だけPhase 4.72で先行成果物採用・未完成／成果物不採用時の通常経路・失効回収を統合する。導入見送り時は人形の先行準備による命中時負荷削減を受入条件にせず、4.5.2の通常同期経路と表示開始の許容を使う。数値差・対応範囲は4.5.2、最小確認は14章／21.2に従う。

- 代表的な連続切断シナリオで目標フレームレートとメモリ予算を満たす。

- Phase 0.2は10.2.2の採用ファイルと用途対応を後続が利用でき、SyntheticとLicensedの公開境界を守ることを確認する。

- Phase 5.5では10種類のアセットが、Blenderヘッドレス処理によって表示／Stencil共用Cut Geometry、切断用Topology、Compound Physics Proxyの自動またはRecipe駆動工程を通過する。製品用Strict Solidは生成せず、その成功を代表AssetまたはPhase 5.5の合格条件にしない。

- 共用Cut Geometryはfiniteかつ参照有効で、各Topology Edgeに逆向きの2面、各Topology Vertexに一つの閉fanを持つことを入力準備時に一度検証する。Self-intersection、別Topologyの閉Component間のIntersection／Overlap、Internal／Nested／Coincident、全体反転を許容し、表示とStencilが同じGeometry、winding、Topologyを使用する。不合格入力は切断対象へ登録しない。切断出力は6.4の構成契約を継承し、Runtime出力検査を持たず、表面化した予期しない内部エラーは4章に従う。用途別Geometry、Runtime修復、符号証明、正規化、Winding上界、Count容量分割を持たない。仮Capは専用Stencil Byteの全8bitを排他利用できる構成だけで`S=(128+W) mod 256`と`S>128`を使用し、成立しない構成は4章の共通Player終了に従う。入力Physics Proxyの各ConvexはAsset／登録時Gateで自己交差、面反転、退化のない閉凸形状として検証し、切断出力は7.2の構築条件に従う。Compound内の別Convex同士のIntersection／Overlapは許容する。製品Asset Preprocessorの出力は同一入力・Recipe・Blender版から再現可能に生成される。

- 相互に食い込む部品や凹形状、同Sideの離れた島を含んでも、通常切断の正常成功は正負それぞれ1論理子・1 Final Physics Ownerとする。島ごとの独立剛体化を行わず、一体運動・接触力共有・固定時の空中浮遊を許容する。同じCut条件を共有するCapはGeometry Unionなしで符号を保存して同一Stencil互換Groupへ入り、`sum(W_i) > 0`として描画される。正逆相殺による欠落は5.2の品質例外とする。

- 固定支持を設定する入力は、必要な点FixedSupportAnchorについて初期Logical Convex CellとFragment Physics Frame内のfiniteなlocal位置を明示する。現在所属をCellのAnchor集合で表し、切断ごとに正側／負側／OnPlane両側へ継承する。所有単位内に継承Anchorが一つでもあれば全体を固定し、なければ動的とする。他Cell、旧Cooked Geometry共有、頂点Buffer、内接削減や表示Geometryの反対側所属からAnchorを新設・移送・復活させない。必要なWorkは既存依存で待ち、不正入力・世代不一致は既存規則で不採用とする。

- 切断対象として採用するGeometryが6章の共通入力契約に合格する。修正を行う場合は採用した前処理Recipeの範囲で確認し、修正しない不合格入力は切断対象外にできる。未採用の修復方式の実装を完了条件にしない。

- 7.6のSource生存性・Active Transaction確認、Final Convexのrobust support、7.7の容量の順に受付ける。No-opと受付見送りは既存Counterで区別し、状態・世代・表示・物理・履歴・新規仕事を変更せず保存・再試行しない。正常成功時は7.1の正負2 Final Owner／Logical childとOperationを一体公開し、実在BoundaryだけをGeometry Commitで後着させる。

- 非空共用Geometryは同SideのFinal Physics Ownerへ所属し、Geometryが片側または両側空でも正負2物理子を残す。専用Convex、極小Rigidbody、反対側への所属探索・質量移送を要求しない。Rendererなしの子は通常のObject／Level寿命または任意7.9 GCへ従い、未計算・失敗を空にしない。

- Final質量は7.2に従って受付時の親Rigidbody質量を保存し、採用Convex集合から質量特性を近似する。Provisional一時massを流用せず、正負両子の質量特性を成立させられなければAbortとする。

- Synty／Poly Pro Universe入力と派生した共用Cut Geometry／Physics Proxyが公開Git履歴、公開CI Artifact、公開キャッシュへ含まれない。

- 飛翔斬撃波の到達時刻と候補列挙が再現可能で、静止対象では接触前の先行切断が安定して成功する。

- 19.1とT-034～T-036に従い、早期Latchした初期Segment、Raw候補のrunning maximum、採用方式のClose後評価、固定面・軸、現在区間だけの閉Sweep、有限寿命、19.1.8のSlashWave VFX、系譜Hit消費が成立する。各EstimatorのUI切替は生存Waveを変更しない。

- 刃側を先行させる広い角度の振りは切断でき、同じ刀向きの復路・峰側移動ではSlashが発生しない。

- 刀は発射可否・再準備・追跡状態によらず、19.1.11とT-040に従い地形、プロップ、NPCへ物理的に引っ掛からない。

- Quest左右コントローラのGrip PoseとBladeFrameが一致し、追跡復帰時に誤Slashを生成しない。

- 予測が外れた場合も4.5.2の必要な現在Pose入力を準備して即時切断レンダラへ接続し、古い成果物をコミットしない。

- Quest 3Sの有線Quest Link環境で、頭部追従だけでなく剣、切断、破片を含む実アプリの両眼描画が原則90fpsを維持する。

- 任意の`SlashId`から候補検索、予測、各切断Task、検証、Commitまたは破棄までをEditorタイムライン上で追跡できる。

- Nearのライブ更新とMid／Farの計画済み軌道が同じ固定ステップ移動Kernelを共有し、Current／Future表示は同じゲーム側明示Animation Stateを交換可能なPose Evaluatorへ渡す。AnimatorController rolloutへ依存せず、Jobベイク採用時は遠距離モブのRoot軌道とAnimation Stateを人形の切断先行計算へ利用できる。プレイヤー介入時は旧`PlanGeneration`の軌道・Rig Pose・切断成果物が適用されず、Queue枯渇時も古い軌道を無期限に再生しない。

- Unity Editor更新時にプロジェクトを作り直さず、専用ブランチで固定テストとXRスモークテストを実行し、不合格なら旧固定版へ復帰できる。

- PoCでは選択的な片眼映像または静止画をFrameIdからTraceへ対応付けられ、録画停止時と比較して90fps性能判断を歪めない。

- OpenXR API Layerを有効にした検証では、D3D11固定Capture Profile上でProjection画像と`predictedDisplayTime`、Pose、TestRunId、Slash／Object／Task IDを一意に関連付け、API Layer自身のGPU／CPU負荷も別計測できる。Profile逸脱時はゲームを止めず録画だけをFail Fastし、理由と実構成を記録する。

- 大型建物は共用Cut GeometryとCompound Physics Proxyで切断でき、点Anchorを失った所有単位は通常の動的物理へ進む。建物由来の動的な1→2分裂子には7.2.2のWorld D6を生成・維持できる。落下・横倒し・完全倒壊を引き続き許容し、D6の実際の拘束効果やSolver品質を合格条件にしない。一般外部Joint付き物体は製品切断対象に含めない。

- Player Body／Handはプロップ／破片へ物理Impulseを与えず、人工移動はLevel初期化時の固定PlayerLocomotionOccupancyに候補次姿勢がOverlapすれば要求全体をRejectする。物理所有者・切断・Commit・Fragment・GCへ追従せず、7.2.3の通行境界不一致と移動制限を許容する。HMDの実空間移動ではCamera位置を強制変更せず、Camera被り・内部視点・Near Planeおよび仮Capの表示品質はD-131と5.2に従う。刀とSlashWave Segmentによる切断Interactionは非接触化後も成立する。

7.9の任意分割・物理GCを実装する場合の追加受入条件は7.9.7とする。無効時の通常切断維持を確認し、有効時は成功分割後の通常再切断とGC退役の非復活を確認する。未実装・無効を垂直スライス不合格にしない。

## 17. Codexでの継続更新ルール

開発用検証（Test、Benchmark、Probe、Fixture、Harness、Capture、Trace）の保存形式・Schema・Codec・Golden・Manifest・Receipt・Report・Index・Profile・file構成・hash・version・Loader・実行／再開手順・反復回数・試験階層は、本書で製品Runtimeの外部形式またはSubsystem間の意味的互換契約として明示したものを除き実装詳細とする。DESIGNは成立させる能力を定め、検証方法だけの変更に改訂を要求しない。Runtimeの所有権・資源寿命・非待機・容量境界・安全な失敗・公開状態・Geometry／Physics契約は維持する。これには、所有権移転、対象ファイルを操作する権限、安全なteardown、完成済み出力の公開に必要な意味的条件を含む。その確認・受渡し方法と型・保存表現は実装詳細とし、既存ReceiptやOS lockそのものの維持を義務にしない。

10.2.3の参考Dataset更新・検証方法の変更と既存要求を確認する個別ケースの追加・差替えは通常作業とし、個別承認制にしない。必須対象・評価基準・Phase完了条件の拡大は同節の人間判断に従う。日々のSHAやケース一覧を本書へ転記しない。

2026-09-12の人間承認により、Phase 0／0.1／0.11／0.2の旧形式維持・旧Reader・同一手順での再生成を将来義務にしない。旧成果物が現行ツールで読めなくなることを許容し、現在の利用に必要な相関・用途と残る意味的契約を満たす範囲で、依存関係に基づき実装を変更・削除できる。既存実装・試験・成果物はそのまま利用でき、改訂だけを理由に再実行・再承認・作り直しを要求しない。0.12～0.14に残る旧形式維持要求にも同じ整理を適用する。

今回外す検証詳細と専用Decision・Test・Open Item・用語は、以下の履歴保持規則の例外として直接削除する。経緯はGitへ委ね、ID欠番を許容する。旧仕様の付録、廃止台帳、互換層、新しい完了証明は追加しない。

- 決定が変わった場合は既存行を消さず、状態を『廃止』にして代替決定IDを記録する。ただし、未実装のTemporary Stencil Capと表示／Stencil共用Geometry、その直接の入力・切断・試験・Benchmark契約、および撤去した旧小破片／Render―Convex品質分類／Shared Convex解決／GPU Debrisとその専用Decision・状態・ID・前処理・Trace・試験・Phase契約に限り、旧仕様を削除・置換してGit履歴だけに残してよい。また、人間承認済みの正負二集合化に伴い撤去する接続Graph、Attachment、間接支持、支持由来の表示状態・Cull、建物専用Safety Tetherと、その専用前処理・保存読込・Schema・Validator・設定・エラー・Fallback・Decision・用語・試験・Trace・Phase契約は、互換用の空表現や旧Readerを残さず削除してGit履歴だけに残す。さらにD-165で撤去する一般外部Jointの継承・付け替え・保護・予測と、その専用の試験・用語・Phase記述も削除してGit履歴へ残す。この撤去にProvisionalSeparationConstraintとBuildingWorldD6Constraintを含めない。D-166で撤去する動的Occupancy追従と退出系の状態・探索・専用ID・設定・容量・作業領域・用語・試験・Trace payload・Phase契約も、旧Readerや互換表現を残さず直接削除し、詳細はGit履歴へ委ねる。人間決定で撤去するNative PhysX比較Probeとその専用契約も、互換表現や旧Readerを残さず削除し、Git履歴へ委ねる。専用IDの欠番は許容し、廃止行や対応台帳を追加しない。この限定撤去を一般のCapture／Trace基盤、残る物理・資源寿命・Anchor・世代・Commitへ拡張せず、既存の永続IDを別意味へ再利用しない。

- D-167～D-170で置換する動的Front、形状追加状態、Vertex／Edgeと生成時刻、一価性・U字・自己交差、厚み・端点領域、VFX／Hit一致、専用Decision・Open Item・試験・Phase・現行Runtime／v2相関は削除し、Git履歴へ委ねる。欠番を許容しIDを別意味へ再利用しない。旧Slash Runtime・二重記録・変換器を追加せず、過去形式の扱いは本章の検証詳細規則に従う。一般のGeometry／Physics／世代／Commit／Capture基盤へ撤去範囲を広げない。

- D-172の撤去対象は廃止行や旧schema全文を残さず削除し、詳細はGit履歴へ委ねる。確定済みPhase 0.2生成物の扱いは10.2.2に従う。

- D-173で撤去する旧Runtime出力Validator・Operation公開前限定退役・Player終了前の完全診断保存保証と、これらに固有の試験は、廃止行・互換表現を残さず削除してGit履歴へ委ねる。共通Player終了境界の確認は14章に従う。

- D-175で撤去する長寿命の物理Group、旧物理恒久採用、Snapshot／Fault Frozen、専用状態・Reason・Trace・試験、未Commit Geometry貸出しの旧契約は直接削除し、Git履歴へ委ねる。互換enum・旧Reader・空record・migration・二重実装を作らず、ID欠番を許容して別意味へ再利用しない。一般の完成済みTrace／Capture形式は対象外とする。

- D-018の変更で撤去する未来予測専用の局所Sceneと専用契約・参照は直接削除し、経緯をGitへ委ねる。空Phase・互換表現を残さず欠番を許容し、通常Worldの物理と残る予測経路は維持する。

- D-017／D-130で簡素化するSchedulerと非同期Dispatchの旧内部契約は直接削除し、経緯はGitへ委ねる。同じ責務のDecision・Test IDは継続し、既存実装は実装詳細として利用できる。一般のTaskId相関、世代・公開・資源寿命と他Subsystemの契約は変更しない。

- Phase 0.5系列とSlashの内部構造・二重識別・状態名・未採用比較方式・重複記述、および19.1.8で置換するVFX形状・式・必須演出項目の撤去は、旧詳細を温存せずGitへ委ねる。同じ責務のDecision／Test IDは継続し、欠番は再利用しない。19.1.5.1の第一候補の説明・式・更新順序は本文に維持し、既存コードの一括改名や互換層を要求しない。

- Phase 0.9～1と関連するGeometry仕様の旧工程・所属／Commit条件、不要な共通型・Profile・固定識別表現・Boundary件数上限は直接削除し、経緯をGitへ委ねる。同じ責務のDecision／Test IDは継続し、既存実装の一括改名・再生成や互換層を要求しない。Topology、公開・資源寿命、0.91／0.94の比較実験は維持する。

- Phase 2.9／3.9の追加と7.2の局所退化許容で置換する旧記述は直接削除・短縮し、経緯はGitへ委ねる。旧仕様の付録や互換表現を追加しない。

- 7.3のcook品質許容により外す形状忠実性Gate・修復要求と曖昧な包含保証は、旧仕様の付録・廃止台帳・互換表現を残さず直接削除・短縮し、経緯はGitへ委ねる。Probeの観測結果や診断基準は遡って書き換えない。

- 未決事項は結論、根拠、決定日を追記して決定事項へ移す。

- 技術検証は測定環境、再現手順、数値結果、スクリーンショット／Profiler参照を残す。

- ロードマップのPhase完了条件を満たす前に次Phaseへ進む場合は、既知の負債として記録する。ただし、15章の独立分岐（0.21未完了中の他Phase進行と2.9／3.9の先行実装・未接続mergeを含む）・任意4.55・条件付き4.71／4.72の省略は未完了負債にせず、任意Phase 5.6／5.7も双方または片方を省略してPhase 6へ進める。

- 新しい機能提案は『即時応答』『幾何精度』『物理整合』『性能予算』のどれへ影響するかを明記する。

- DOCXを再生成せず、このMarkdownのみを正本として更新する。

## 18. 用語

| 用語 | 定義 |
| --- | --- |
| VP Geometry | グローバルVertex／Indexプール上の表示／Stencil共用表現。VertexはAoS。CPU側が切断正本、GPU側が描画コピー。寿命・公開は4.5に従う |
| Geometry参照 | 正負Sideの共用Geometry範囲への参照。幾何処理が必要とする局所Component識別を保持できるが、通常物理所有単位をComponent数で増やさない |
| Stable Geometry | 4.5.6に従ってCommitした共用VP Geometry。対応するFinal Physics／Logical Publicationは先に完了しており、描画時刻はRenderer側の状態収集・発行順序に従う |
| PhysicsSplitTransaction | 7.1の一回の物理所有変更を扱う短寿命record。Final Physics／Logical PublicationまたはAbort／Staleで終了し、Geometry DAG・子孫・履歴・遅延cleanupを所有しない |
| Pending Cut | 4.2で受付けるCutOperationId・Source・基底世代・採用面を持つ記録。7.1のTransaction終了後はGeometry責務だけを保持し、4.5.6のCommitまたは不要化で完了する。公開子への受付を止めず、具体的Boundaryは後着できる |
| TemporaryRenderCapRecordSet | 当該フレームにCap板Batchへ投入するRecord集合。Stencil Volumeの投入対象は5.2の選択・可視性・Color分類に従う。4.2の表示可能なPending CutとGeometry未Commit境界から構成し、固定状態で省略しない。Frustum／Facing CullでRecordを選別する。最大12面の選択は`SelectedTemporaryClipPlaneSet`に適用し、Plane overflowでCap Record集合を削らない。件数は固定側とBatchに残るIgnored板を含むBatch投入Cap Record数であり、Color／Depthを書いた板数ではない。Geometry Commit後は対応Recordを外す |
| TemporaryClipConstraintCandidateSet | 1個のRenderFragmentへ関係するGeometry未Commit切断半空間制約の集合。Pending Cutと公開済みOperationの採用面・祖先制約から構成し、固定状態で省略しない。Cap Record集合とは区別する |
| SelectedTemporaryClipPlaneSet | CandidateをPending Cut列とLogicalCutOperation公開列の受付順に辿ったdependency-closed prefixから、Raster 8面とPixel fallback最大4面へ割り当て、左右眼とColor／Depth／Shadow／Stencil Volumeで共有する固定長の即時描画Plane集合。Operation公開時は同じ受付位置と面を維持する |
| IgnoredTemporaryClipBoundarySet | Candidateのうちdependency-closedな最大12面prefixへ入らない後発Pending Cut／境界。5.2に従いPlane選択とclip制約から除外し、対応Stencil Volumeをsubmitしない。Cap板Recordは既存Batchへ残り得る。Pending Cut、公開済みLogicalCutOperation、論理／物理状態、世代、背景Geometry／Convex処理は維持する |
| 共用Cut Geometry | 表示・実Cap・Stencil Volume・次回切断が参照する同一のGeometry正本。4.5.1に従い複数Geometry参照で構成してよい。閉鎖・edge／vertex manifold・局所winding整合済みTopologyを持ち、各世代で同じTriangle集合と向きを全用途へ公開する。Self-intersection、別Topologyの閉Component間のIntersection／Overlap、Internal／Nested／Coincident、全体反転、Runtimeの面積0 Triangleを許容する |
| Physics Proxy | 物理接触と高速切断のための低複雑度Convex／Compound。各Convexは閉凸契約を満たすが、同一Compound内の別Convex同士はOverlapしてよく、Strict SolidやConvex Boolean Unionを入力に要求しない |
| ProvisionalRigidbody | 7.1のTransaction内で予定正負子のpose／速度／外界Collisionを先行させる短命Actor。旧Cooked Geometryを共有し、7.2のOBB／等分近似はFinal質量正本にしない |
| ProvisionalSeparationConstraint | 同じ切断で生じたProvisional Siblingに使うUnity `ConfigurableJoint`。固定anchor-offset D6の構成、有限区間の品質許容と寿命は7.1に従う。Sibling Collision無効化およびBuildingWorldD6Constraintとは別の役割を持つ |
| ProvisionalCollisionResourceLease | 1つの旧Cooked Convex Geometryを複数のProvisional Shape Instanceが安全に共有する所有権Token。各GeometryをProvisional Shape Instanceへ結び付ける前に取得し、7.1に従って当該Leaseが保護する参照と必要な物理Stepの寿命を満たして一度だけ返す。Geometry自体は最後のLease返却前に破棄しない |
| FragmentRenderAnchor | Parent ActorからProvisional Actorを初めて分裂させる際、表示Fragmentの初期World poseと点速度を連続させるstableな基準Transform。Final handoffでは物理Actorを優先するためActor pose補正やCOM速度変換には使用せず、表示Geometryが新しい物理frameへ追従して瞬間移動することを許容する |
| FixedSupportAnchor | 固定支持を表すfiniteな点。初期Logical Convex CellとFragment Physics Frame内local位置を入力とし、各Cellの集合で保持する。OnPlaneでは同じ値を正負の各子集合へ1回ずつ継承する。所有単位内に一つでもあればStatic／Kinematicで固定とし、他Cellへ支持を伝播しない |
| IsBuildingDerived | 製品Recipeが指定する建物由来の継承boolean。サイズや名前からRuntimeで推定しない。7.2.2を正本とする |
| BuildingSplitDepth | 7.2.2の建物由来の非負分裂深さ。初期0、予定子DepthをProvisional D6と正式子で共有する。非建物は0を維持し、Final handoffで再加算しない |
| BuildingWorldD6Constraint | 7.2.2の建物由来・Anchorなしの動的分裂子が一つ持つWorld接続D6。Actor寿命で保持し、ProvisionalSeparationConstraintと分離する |
| PlayerLocomotionOccupancy | 7.2.3のLevel初期化時に確定するworld-space不変の低複雑度Primitive集合。人工移動の候補Player Root／予測HMD CapsuleのOverlap Rejectに使用し、物理所有者・切断・Commit・Fragment・GCへ追従しない |
| LogicalFragment | 再切断・退役の論理単位。正常切断はFinal Physicsと同時に正負各1子を公開し、各子が一Final Physics Ownerを専有する。Geometry未完成でも受付でき、同Sideの離れた島を含められる。7.9の非命中再編成・退役でも架空Operationを作らない |
| CutBoundaryRecord | Geometry Commit時に確定する実在面・Side・子Geometry／frame・作成時世代の記録。Logical Publicationから後着でき、物理連結Edgeや資源の強い所有者にはしない |
| SourceSlashPlane／SelectedObjectLocalCutPlane | 前者はSlashFrameの不変World攻撃面、後者は命中時に対象操作ごとに一度確定する切断前物体相対の面。19.5.1の予測面採否はMesh Readyに依存せず、全表示／物理成果物が選択面を共有する |
| CommittedCutPlane | SelectedObjectLocalCutPlaneを対応する実Actor／子frameへ写したWorld面。命中時のWorld位置を固定したり予測PoseをActorへ設定する指示ではない |
| RigidCutRebaseProfileV1 | 初期自由飛行剛体のリベースGateを固定するversion付き設定。法線角度、Bounds内plane field差、固定8点の左右眼投影差の上限を持ち、未設定や投影不能では通常面へFallbackする |
| LogicalCutOperation | 一つの受付済み切断の親ID／世代、正負2子ID、採用面・Side対応。7.1のFinal Physicsと一体公開し、4.5.6のGeometry Commitで実在するCutBoundary参照が後着し、0件も正常とする。受付前No-opでは生成しない |
| CutOperationId | 0を未設定用に予約し、ObjectIdの生存期間全体で一意かつ非再利用とする正の32bit int。受付成功時にPending Cutへ発行するため、対応LogicalCutOperationより先に存在できる。OperationのTrace相関にも使用する |
| LogicalFragmentLocalId | 0を未設定用に予約し、ObjectIdの生存期間全体で一意かつ非再利用とする正の32bit int |
| CutBoundaryLocalId | 0を未設定用に予約し、ObjectIdの生存期間全体で一意かつ非再利用とする正の32bit int |
| Kerf | 切断によって除去される物理的な幅。本作では0とし、見える隙間は破片の相対移動だけで生じる |
| Cooking Profile | `Physics.BakeMesh`と適用先`MeshCollider.cookingOptions`へ同一指定する構成。初期製品では7.3の一つだけを使う |
| RenderFragment | 正負Sideの共用Geometryを表示する単位。同Sideの離れた島を含められ、全島の個別列挙を要求しない。7.6に従い現在の物理所有者へ追従する |
| コミット後の追加空間分割 | 確定した1物理所有単位の共用面集合を横切らない一枚の平面で配分し、必要なConvex clip・cook後に通常物体2個へ再編成する7.9の任意処理。非命中で公開し、新しいGameplay切断面・実境界・Cut Operationを作らない |
| 物理GC | 共用Geometry全体の面集合が確定0の通常物理所有単位を、7.9の条件で丸ごと終了する任意のゲーム上の寿命Policy。Managed GC・個別Convex間引きではなく、追加分割から独立する |
| MaxIncompleteCutOperationCount | 全対象で同時に保持できる受付済み未完了切断数。対象LogicalFragmentへの一回の受付を1件とする既存容量設定の0より大きい固定値であり、片側空No-op、受付見送り、投機候補、7.9の任意分割・物理GCは数えない |
| WorldPhysicsProfile | 世界重力を正本として保持し、Unity Physics、予測、解析運動、VFXへ同じ値を供給するバージョン付き設定 |
| Pending Two-Sided Shadow | 即時切断中だけ、開いた外殻の裏面をShadow Mapへ書いて断面キャップの遮蔽を近似する両面ShadowCaster経路 |
| Cap Bounds Polygon | 対象のローカルOBBと切断平面の交差から生成し、他のTemporary Render Boundary半空間でclipする3～6頂点の有限な仮キャップ板 |
| Stencil Conflict Graph | 通常Colorで分離が必要な対象間の関係を表す論理モデル。全Graph、全Edge、特定の構築・彩色手順を要求しない |
| CapCompatibilityKey | 全World Cut Plane、Side／半空間、分離Offsetを表すStencil共有互換Key。可視色、符号分類とWinding容量を含めない |
| Winding Count Stencil | 共用Cut GeometryのFront／Backで排他的に予約したStencil Byte全8bitを128へ初期化してIncrementWrap／DecrementWrapし、`S=(128+W) mod 256`のうち`S>128`だけを描画する方式。Saturateおよび部分Bit Counterは使用しない |
| Residual Stencil Support | Front／Back集計後に`S != 128`となる画面領域を表す論理概念。実Stencilから検出せず、可視Cap Boundsを通常Colorの保守的な投影重複判定に使う |
| Cap Visibility Cull | 論理破片×切断面のCapRecordを5.6のFacing条件で判定し、全Capが除外される互換GroupをStencil彩色前に除外する処理 |
| SlashWave | 19.1の不変面・軸、一本Segment、AcceptedSpan、有限WaveLifetimeを持つ飛翔攻撃 |
| Stroke Begin／Slash Latch／Span Open・Close・Closed／Wave Expire | 開始Sample選択、現在時刻での公開、刀入力受付期間と終了、刀入力終了後の評価期間、Wave寿命終了。時間順と未Close時の意味は19.1.1 |
| Slash Latch／Frame／Span Candidate／Span Close Estimator | 19.1の交換可能な出力境界。方式・設定と一時状態の保持範囲は19.1.4 |
| RawSpanCandidate／AcceptedSpan | 前者は有効性付きの非単調な希望長、後者はWaveが受理するrunning maximum。非遡及は完了済み区間の非再評価を意味する |
| SlashWave Segment／SpanAxis／TravelAxis | 19.1のAからBへの一本前縁と、固定されたSpan方向・進行方向。軸の直交は要求しない |
| Span Guide Ray | 19.1.5.1の第一候補で使う、Span Open中のLive Emitter／剣先方向とClose後の固定Emitter／方向による半直線 |
| Current Adopted Physics Convex Set | 7.6と実Hitで共用する現在採用Convex集合。旧／暫定Convexを含められるが表示Geometryの外包を保証しない |
| SlashWave VFX | 19.1.8に従う、Slash面内の表示専用平面表現 |
| Candidate Flight Bounds／先行準備範囲 | 前者は有限Span包絡から導出可能な場合の保守範囲、後者は全Hit包含を保証しない有限な投機範囲。実Hit検索を制限しない |
| ObjectGeneration | Object全体のPrediction等に用いる単調増加の粗い変更識別。更新と採否は4.2、7.9、8章に従う |
| BaseObjectGeneration | 投機ジョブが入力としてスナップショットしたObjectGeneration |
| Commit | 有効な成果物を既存境界で整合した状態として公開する操作。採否は8章、物理・論理公開は7.1、Geometry公開は4.5.6に従う |
| BladeFrame | 刀Prefab内でBladeAxis、EdgeDirection、SideNormalと判定Sample Pointを定義するローカル座標系 |
| Edge Lead Score | 刀身軸方向を除いた運動と刃方向の内積。正なら刃が先行し、負なら峰が先行する |
| Future Event DAG | 未来の候補接触、姿勢予測、切断、Commitを依存関係で表した評価グラフ |
| Work Item／TaskId | Job、I/O、GPU処理等を横断して追跡する論理作業単位と相関ID。C# `Task`型に限定しない |
| MobTrajectoryKernelV1 | Global FixedStepの整数倍で、固定MobId順のCurrent StateからNext Stateを二相更新する副作用のない初期群衆移動Kernel。Waypoint／Lane Desired Motionだけを扱い、Nearのライブ更新とMobPlan未来生成で共有する。NavMeshAgent、Root Motion、RigidbodyによるRoot位置更新と併用しない |
| AnimationPlannerV1 | Behavior Intent、Locomotion、Root速度／向き、累積移動距離から副作用なく`ExplicitAnimationStateV1`を生成するゲーム側Planner。現在表示BackendやAnimator内部Stateを入力正本にしない |
| Animation Clip Catalog | 正のAnimationClipIdごとに`Loop`／`Clamp`、finiteかつ正のcanonical DurationSeconds、Clip content identityを固定するcanonical表。固定property順とClip ID昇順から算出する内容hashを`AnimationAssetSetVersion`へ結合し、Mode／duration変更を同一Asset版として扱わない |
| ExplicitAnimationState | Current／Future双方のAnimation Source、Source Time／Phase、Playback、Blend／Transition等を表現するゲーム側の副作用のない値状態。標準経路ではAnimator／Controller内部Stateから復元せず、必要な履歴を明示する |
| ExplicitAnimationStateV1 | 正のint範囲の単一AnimationClipId、finiteかつ0以上のbinary64非wrap累積Phase、finiteかつ0以上のbinary64 PlaybackRateCyclesPerSecondからなる、所属SampleのFixedStepIdへ解決済みの初期表現。同一Clip間だけPhase補間し、異Clip境界はhard switchする。将来の2 Source Blend等は互換意味境界を保ったschema拡張とする |
| ResolvedAnimationPoseInput | 対象`FixedStepId`、そのStepへ解決済みの`ExplicitAnimationState`、Rig／Animation Asset Set／Evaluation Profile Identityを一体で保持するimmutable入力。裸のStateや別StepのStateをEvaluatorへ渡さない |
| FutureAnimationPoseEvaluator | `ResolvedAnimationPoseInput`からcanonical Bone順Rig Poseを生成する交換可能境界。controllerなしPlayable、Pose Table、将来SamplerをBackendにでき、評価要求順や暗黙Controller rolloutへ依存せず、PlaybackRateによる追加の時刻進行を行わない |
| MobTrajectorySample | 1つの`MobId + PlanGeneration + FixedStepId`に属する固定間隔Sample。position、velocity、heading、Locomotion、ExplicitAnimationState、経路カーソルを持ち、固定長Ring Buffer内でMid／Far再生と未来姿勢生成に利用する |
| MobTrajectory Hold | 有効Sample不足または固定容量／Live Fallback予算超過時に、最後の有限なRoot姿勢と`ExplicitAnimationStateV1`全体を維持するbounded degradation。古い軌道の無期限外挿、Clip／Rateの独自変更、同期全群衆再計算、Buffer再確保を行わない |
| Cut/Cook Profiling | 7.5の代表Fixtureによる軽量測定。結果を実行枠・scratchメモリ・同時未完了切断数の調整に使う |
| Unity Built-in 3D Physics | GameObject／Rigidbody系で使用するUnity内蔵NVIDIA PhysX統合。DOTSの`Unity Physics`パッケージとは別物 |
| Native採用Gate | D-086に定める、Native物理経路の部分置換を再検討する条件 |
| Confidence | 未来結果をDeterministic／Conditional／Speculativeに分類した信頼度 |
| Trace Event | 状態遷移、Taskライフサイクル、Commit結果を整数IDと時刻で表す軽量イベント |
| Flow Event | Schedule元と別スレッド／Job上の実行をUnity Profiler内で結ぶ相関情報 |
| Flight Recorder | boundedな履歴を診断保存へ利用する観測機能。保持方式・保存形式は担当Phaseと17章に従う |
| Early Licensed Fixture | Phase 0.2で採用する非公開の補助Geometry。用途を識別して利用し、製品Asset全体の互換性や製品品質を保証しない |
| Synthetic Watertight Test Fixture | プログラムまたは固定版Blenderスクリプトから決定論的に生成する閉Triangle Meshのテスト／Benchmark専用入力。製品AssetやライセンスAssetの派生物ではなく、製品Preprocessor成果物、Runtime同梱物、代表Asset合格条件にはしない |
| LicensedRepresentative Dataset | 採用したLicensed Fixtureの非公開集合。Geometry・画像・Asset対応を公開しない |
| Preprocess Recipe | Assetごとの包含・除外部品、封鎖、空洞保持、分割、Voxel品質を記述する設定 |
| Preprocess Cache Key | 入力、Recipe、Script、Blender版のハッシュから生成する再構築判定値 |
| Boundary Loop | 片面または開放Meshで、1面だけに属するEdgeが形成する穴の輪郭 |
| Voxel Closing | 体積を膨張後に収縮してVoxel数個以下の隙間を閉じる形態学的処理 |
| RenderCutTopologyMap | 共用Cut Geometryのposed positionから独立したTopology対応・系譜を表す情報。属性seamと別Topologyを区別し、6.2／6.4の位置・交点共有とContour接続に使う。内部表現と識別方法は実装詳細とする |
| Topological Watertight | Boundary Edgeがなく各Edgeが規定数のFaceへ接続する閉Topology。自己交差のない3D Solidまでは保証しない |
| Geometrically Valid Solid | Topological Watertightに加え、面向きが整合し、非隣接Faceの自己交差、面反転、退化がなく、内外と体積を一意に扱える形状 |
| Trusted Exterior | 元Render AssetのうちSurface Projection先としてRecipeが許可した外表面。内部面、装飾、合成封鎖面は原則除外する |
| Constrained Surface Projection | Voxel再構成面をTrusted Exteriorへ距離・法線・包含等の条件付きで戻し、失敗頂点をVoxel位置へFallbackする処理 |
| NeedsReview | 自動処理は完了したが意味または品質を保証できず、人間の確認を要求する結果 |

## 19. 飛翔斬撃と未来評価アーキテクチャ

### 19.1 単一Segment SlashWave

Blade Gesture／Plane DetectorがGrip／Tracking、最小速度・移動量、Edge Gate、accepted samples、Stroke BeginとPlane候補を担当する。PlaneはLatchまでの複数Sampleの刀身長軸と振り方向から推定する。Wave生成は19.1.4に集約し、VFX／Hit／Prediction／Cut StateはEstimator内部を参照しない。これらは責務境界であり、別MonoBehaviour・Assembly・Loggerを要求しない。

#### 19.1.1 Slashの時間用語

本節の`Stroke Begin`、`Slash Latch`、`Span Open`、`Span Close`、`Span Closed`、`Wave Expire`は、Slash生成・入力受付・Wave寿命の時間境界または期間を表す。これらの名称だけを理由に、別々の公開enum、状態機械、MonoBehaviour、Trace Eventまたは永続fieldを設けない。

| 用語 | 記号／field | 意味 |
| --- | --- | --- |
| `Stroke Begin Sample` | `t_B` | 現在の未Latch strokeに属するaccepted Blade Sampleから、現在のBegin選択方式が開始基準として選んだSample。最初のTracking Sample、固定速度閾値の通過Sample、Edge Gate成立Sampleのいずれかへ固定しない。Latch前は選択結果を更新できるが、Latch時に当該Slash用としてsnapshotした後は変更しない |
| `Slash Latch` | `t_L`／`LatchedAt` | 19.1.4の生成条件が揃った現在時刻に一度Waveを公開する操作。過去のPeakまたはSample時刻へ遡及せず、この境界からWave、VFX、Hitおよび必要な候補準備を開始する。生成更新のSpan評価は19.1.6に従う |
| `Span Open` | `t_L`以後、最初の`Span Close`または`Wave Expire`まで | Latch済みWaveが現在の刀入力をSpan候補生成へ反映できる期間 |
| `Span Close` | `t_C`／`SpanClosedAt` | 当該Waveへの現在刀入力の反映を不可逆に終了する境界とその現在時刻。Wave Expireとは別で、Close自体ではWaveを終了せずAcceptedSpanを固定しない。Close後は採用方式の候補評価を行う |
| `Span Closed` | `t_C`以後、`Wave Expire`まで | Span CloseがWave Expireより前に成立した場合だけ存在する刀入力終了後の評価期間 |
| `Wave Expire` | `t_X = t_L + WaveLifetime` | 当該SlashWaveの飛翔、Span候補評価、VFXおよびHit評価を終了する境界。Span Close、Gestureの再準備、次SlashのLatchとは別である |

Span CloseがWave Expireより前に成立する場合は`t_B <= t_L <= t_C < t_X`とする。Closeが成立しないままWave Expireへ到達した場合、`SpanClosedAt`は未設定のままで、Span OpenはWave Expireにより終了する。

単独の`Begin`という表記は避けて`Stroke Begin`を用いる。入力受付の終了には`Span Close`、Wave寿命の終了には`Wave Expire`を用い、両者を同じ語で表さない。

#### 19.1.2 Slash Latch Estimator

accepted blade samples、現在時刻・stroke開始情報からWait／LatchReady相当だけを返す。API名、型、内部状態の有無を固定しない。

- Timeout、移動距離、振り角度、速度Peak通過、Peak後の低下、距離／角度のPlateau、その組合せ等を実装候補にできる。
- Latch Estimatorは`SourceSlashPlane`、`SpanAxis`、`TravelAxis`、Hit、切断対象を決定しない。
- LatchReadyは生成許可の一条件に限り、実際の公開は19.1.4のWave生成処理で行う。
- 有効なLatch Estimatorは開発UIから実行中に手動切替できる。切替後の評価から新方式を使い、Latch済みSlashは影響を受けない。
- Estimator内部値をHit／Prediction／Commitの照合条件へ追加しない。保持形式と診断は19.1.4／19.1.12に従う。
- 複数Estimatorの自動多数決、自動Fallback連鎖、最良方式のRuntime探索を要求しない。旧Estimatorを互換性のため保持する義務も置かない。

#### 19.1.3 Slash Frame Estimator

SourceSlashPlane、Stroke Begin Sample、Latchまでのaccepted samplesからValid、SpanAxis、TravelAxisを返す。API名、型、field列は固定しない。

- `SpanAxis`と`TravelAxis`はfiniteな単位ベクトルで、`SourceSlashPlane`上にある。直交は要求しない。
- `SpanAxis`の正方向はSegmentの内側端点から拡張終点へ向かう。
- `TravelAxis`の正方向はSlashWaveのA点が前進する方向である。
- 有効なFrameを作れない場合はLatchしない。Latch瞬間の単一poseへ暗黙Fallbackしない。
- Latchした両軸はSlash寿命中に回転・反転させない。
- 第一候補の軸・射影条件は19.1.5.1に従う。
- 有効なFrame Estimatorは開発UIから実行中に手動切替できる。切替後の評価／Latchだけへ反映し、Latch済みSlashは確定済みの軸値だけを使う。
- 製品Gameplayで複数Estimatorを自動比較・合成・多数決する機能は要求しない。Estimator実装は同じ出力契約内で後から交換でき、旧方式保持を要求しない。

#### 19.1.4 Latch、Emitter Sample、初期Segment

既存のWave生成処理が、LatchReady、同じ現在評価の有効SourceSlashPlane／Frame、19.1.5のfiniteな初期Segmentと19.1.6の公開直前の容量確認を一か所で判定し、すべて成立した現在時刻にだけ一度生成する。過去時刻へ遡及して公開しない。

発射時の面・軸・初期形状と当該Waveの評価に必要な方式・設定を、評価中に変わらないよう扱う。後の設定変更で生存Waveの挙動を変えず、必要な参照を利用中に解放しない。保持形式、値の導出・共有、内部recordの分割は実装詳細とし、説明用の変数ごとに独立fieldを要求しない。Estimator内部値をHit／Prediction／Commitの新しい照合条件にしない。

刀身途中の調整可能なEmissionControlPointをaccepted Blade Sampleへ適用してEmitter位置を得る。その変更で刀速、Edge Gate、Plane推定等のControl Pointを暗黙に変更しない。第一候補の射影・初期chordは19.1.5.1に従う。

#### 19.1.5 単一Segment、Raw Span候補、Accepted Span

SlashWaveのGameplay Segmentは全候補方式で次の共通形を使う。

```text
A(t) = WaveOrigin + TravelDistance(t) * TravelAxis
B(t) = A(t) + AcceptedSpan(t) * SpanAxis
```

第一候補のWaveOriginと初期Segmentは19.1.5.1に従う。

- `A(t)`はSegmentのSpan座標0の端点であり、World-spaceで静止する点ではない。
- `B(t)`は`A(t)`、`SpanAxis`、`AcceptedSpan(t)`だけから決まり、別の方向・速度状態を持たない。
- `Slash Span Candidate Estimator`は各更新で`Valid`と`RawSpanCandidate`を返す。候補値には単調性を要求しない。
- SlashWave側はLatch時の初期値から`AcceptedSpan`を所有する。有効かつ下記finite Segment条件を満たす候補だけ、`AcceptedSpan[n] = max(AcceptedSpan[n - 1], candidate.Value)`で受け入れ、それ以外は前値を維持する。

Wave生成時とSpan候補の受入時には、固定入力と採用予定Spanを維持した場合に残り寿命内のA／Bをfiniteに生成できることを条件とする。不成立ならWaveを生成せず、またはSpan候補を受け入れず前値を維持する。Clampや代替経路を設けない。検査式は実装詳細とし、この条件のための専用上限・追加statusを要求しない。

したがって、実際のSegment長は縮まらない。Estimatorは過去最大、Hit、VFX、切断状態を知らず、Wave側は候補の幾何的導出方法を知らない。

通常式へ`clamp`を必須化しない。候補Estimatorは少なくとも非finite、定義不能または意味上不成立の候補を`Invalid`にできる。必要になった場合は有限な候補上限をEstimatorの有効性判定として開発UIへ出せるが、Clamp、Fallback列、別Topologyを先に要求しない。

Accepted Spanが後続更新で増加しても、完了済みの過去更新区間へ遡ってSegment、Sweep、HitまたはVFXを再生成・再評価しない。現在評価中の更新区間では、前回Segmentと現在Segmentが張る閉Sweep全体に当該更新のSpan増加領域を含め、その領域への命中を許容する。

##### 19.1.5.1 第一候補：固定Span直線とLive／Frozen Blade Guide Rayの交点

この方式をPhase 0.53で動作する第一候補として実装し、0.54で可視化・再生・比較UIへ接続する。0.55はこの方式から実機試行を開始し、人間が試すと決めた比較候補を必要に応じて同じ出力境界へ追加できる。既存方式の欠陥証明や全候補の先行実装・網羅比較は要求しない。第一候補の実装時に式を再選定せず、採用自体は義務づけない。4.50は0.55の採用構成を引き継ぎ、複数方式の常時搭載を要求しない。

##### 記号と固定値

`SourceSlashPlane`の単位法線を`N`とし、平面内ベクトルの符号付き2次元外積を次で表す。

```text
cross2_N(x, y) = dot(N, cross(x, y))
```

時刻を`Stroke Begin = t_B`、`Slash Latch = t_L`、`Span Close = t_C`とする。`EmissionControlPoint`から得た平面上の位置を`E_B`、`E_L`、`E(t)`、`E_C`、平面へ射影・正規化した剣先方向を`D_B`、`D(t)`、`D_C`とする。`t_C`はSpan CloseがWave Expireより前に成立した場合だけ存在する。

式で使うEmitter位置はSourceSlashPlaneへ点として射影し、剣先方向は平面成分を正規化する。射影・正規化不能なら当該Estimator出力を無効とし、Planeからの実入力偏差を別のGameplay自由度へ持ち込まない。WaveOriginはBegin時Emitter位置E_Bに一致する。これらは数学上の値であり、同名の独立した保存fieldを要求しない。

第一候補の固定軸は次である。

```text
TravelAxis T = D_B
SpanAxis   S = normalize(E_L - E_B)
```

`S`と`T`の直交は要求しない。`E_L - E_B`が正規化不能、`D_B`が平面へ射影不能等ではFrameを無効とする。

Wave速度を正の有限定数`c`とし、Latch後のA点を次で定める。

```text
A(t) = E_B + c * (t - t_L) * T     for t >= t_L
```

Latch時は`A(t_L) = E_B`、初期`AcceptedSpan = length(E_L - E_B)`、`B(t_L) = E_L`とする。Latch時の初期SegmentはStroke BeginからLatchまでに実際に通過したEmitter chordであり、最終Spanは確定しない。初期値を交点式へ依存させない。

###### Span Open／Span ClosedのGuide Ray

Span Open中は、現在Emitter位置から現在剣先方向へ延びるGuide Rayを使う。Span Closeが成立した更新では、その更新のaccepted sampleを含めた現在Emitter位置と剣先方向を`E_C`／`D_C`としてsnapshotし、以後は同じFrozen Guide Rayを使う。

```text
GuideOrigin(t), GuideDirection(t) =
    E(t), D(t)       while SpanOpen
    E_C, D_C         while SpanClosed

GuideRay(t, q) = GuideOrigin(t) + q * GuideDirection(t), q >= 0
```

Span CloseはGuideをLiveからFrozenへ切り替えるだけであり、`AcceptedSpan`を固定せず、Waveを終了させない。

###### RawSpanCandidateの交点式

Bの幾何候補は、Aを通る固定Span直線とGuide Rayの交点である。

```text
A(t) + r(t) * S = GuideOrigin(t) + q(t) * GuideDirection(t)
```

したがって、

```text
r(t) = cross2_N(GuideOrigin(t) - A(t), GuideDirection(t))
       / cross2_N(S, GuideDirection(t))

q(t) = cross2_N(GuideOrigin(t) - A(t), S)
       / cross2_N(S, GuideDirection(t))
```

`r(t)`を`RawSpanCandidate`とする。この候補は刀姿勢またはA点の進行により増減してよく、単調性を要求しない。

第一候補で最低限Invalidとする条件は次である。

- `abs(cross2_N(S, GuideDirection))`が調整可能な正の近平行閾値以下。交点式の除算には元の符号付き分母を使用する
- 入力、`r`または`q`が非finite
- `r < 0`
- Guide Rayの後方交点となる`q < 0`。数値許容はPhase 0.55のUIで調整できる

Invalid時は`AcceptedSpan`を変更しない。別の交点、直線へのFallback、軸回転、Clamp、過去Sampleへの遡及を行わない。

###### Wave側の受入

19.1.5のfinite Segment条件も満たす有効な候補についてのみ、

```text
AcceptedSpan(t) = max(previous AcceptedSpan, r(t))
B(t) = A(t) + AcceptedSpan(t) * S
```

とする。Raw候補が減少した期間には、実B点がGuide Ray上にあることを要求しない。Guide Rayは現在望ましいSpan候補を生成する入力であり、Gameplay拘束ではない。

交点式が定義可能な固定Guideでは、Close時のRaw値をr_CとしてSpan Close後のRaw候補は次の線形式となる。

```text
r(t) = r_C + c * kappa_C * (t - t_C)

kappa_C = -cross2_N(T, D_C) / cross2_N(S, D_C)
```

`kappa_C > 0`ならSpan Close後のRaw候補は増加し、それが過去のAccepted Spanを超えた時点からAccepted Spanも再び広がる。`kappa_C <= 0`またはRaw候補が過去最大を下回る間はAccepted Spanを維持する。距離拡大用の独立`k_d`をこの第一候補へ追加しない。

###### 更新順序

一更新内の意味順序は次とする。実装関数の分割は固定しない。Phase 0.53～0.55／4.50はSweep領域の生成までとし、対象Query・実Hit評価は4.51で接続する。

1. 現在時刻が`LatchedAt + WaveLifetime`以上の既存WaveをExpireさせ、容量を返却する。退役したWaveはその更新のSpan／Close・Segment／VFX・Sweep／Hit評価を行わず、寿命境界までの最終Sweepも補わない。最後の生存更新から寿命境界までの末尾区間に命中が抜け得ることを許容する。
2. 現在の有効Blade Sampleをaccepted sample列へ追加する。
3. 未LatchならLatch／Plane／Frameと19.1.5のfinite Segment条件を評価し、19.1.6の容量確認に成功した場合だけ初期Segmentと初期Accepted Spanを公開する。この更新で返却された容量も使用できる。満杯時は当該Strokeを終了し、そのWaveの後続処理へ進まない。 新規Latchに成功したWaveは、初期Segment／VFXを公開し、19.1.7の退化Sweepを一度生成して当該Waveの更新を終了する。対象Query接続後は同じSweepで実Hitを一度評価する。手順4～7のRaw候補・Close評価等は次回更新から開始する。
4. Latch済みなら、Span Open中は現在SampleのLive Guide、Span Closed中は既にsnapshotしたFrozen Close Guideを使用してRaw候補を一度評価する。
5. Span Open中だけSpan Close Estimatorを評価し、Closeなら同じ現在SampleをFrozen Close Guideとしてsnapshotする。Close成立更新で既に得たLive Guide候補は失わず、Frozen Close Guideによる候補評価は次の更新から使用する。同一更新で同じGuideを二重評価しない。
6. 有効なRaw候補のうち19.1.5のfinite Segment条件を満たすものをrunning maximumへ受け入れる。
7. 現在A／B Segmentを更新し、19.1.8に従ってVFXの表示状態を更新する。前回／現在Segmentを共通Sweepへ渡す。

具体的な近平行epsilon、qの許容、必要時だけのfinite候補上限は開発UIで調整する。

#### 19.1.6 WaveLifetime、容量、Gesture再準備

全方式で、既存WaveのExpire・容量返却を新Latchより先に行い、新規Latch更新は初期Segment／VFXと退化Sweepの一度の生成で終了する。Raw候補・Close評価は次回更新から開始し、入力受付中は現在Sampleの候補評価後にCloseを評価する。Close成立更新の候補を失わず、その更新ではClose後評価を重ねない。候補受入後にSegmentとVFXの表示状態を更新し、前回／現在SegmentからSweepを生成する。第一候補での具体的な七手順とGuide処理は19.1.5.1に従い、他方式にFrozen Guideを要求しない。

- 初期SlashWaveはLatch時にsnapshotした有限・正のWave速度で飛翔し、`LatchedAt`基準の有限な`WaveLifetime`だけを終了の正本とする。ExpireしたWaveは当該更新のSegment／VFX／Sweep／Hitを出力せず、寿命境界までの末尾Sweepを補わない。末尾区間の命中抜けは許容する。
- 最大到達距離は`WaveSpeed * WaveLifetime`から導出し、独立した最大飛距離設定または競合する第二終了条件を持たない。
- 将来速度曲線を導入する場合も、最大到達距離はLifetime内の速度積分から導出し、独立した寿命正本を増やさない。
- Latch後に刀入力をSpanへ受け付ける期間と、Waveが飛翔・命中可能な寿命を同じTimerにするか、`SpanCaptureTimeout`だけを短く分けるかは調整可能とする。
- Gesture側の再発射準備と既に飛翔中のSlashWave寿命を分離する。同じ振りから重複発射しない。前のWaveが生存中でも、新しいGestureが再準備条件を満たし、生存Wave容量に空きがあれば別Slashを生成できる。
- Phase 0.53の開発容量は同じCoreの有限格納に与える暫定設定とし、下記の公開前確認・満杯時規則を共用する。Phase 0.55の連続斬りで同時生存Wave数を観測し、Phase 4.50開始前に生存Wave用の有限な固定容量を一つ決める。今回、具体値は固定しない。19.1.4の生成処理で公開直前に空きを確認・確保し、Stroke Begin時の予約は行わない。
- 満杯なら新しいLatchだけを見送り、既存Waveの形状・寿命を変更せず、強制退役させない。新Wave・初期Segment・VFXを公開せず、そのSweep・Hit・Predictionを開始しない。有効な振りでも発射されないことを許容する。当該Stroke候補は終了し、保存・待機・遅延Latch・再試行に再利用しない。空き発生後も同じ振りを発射せず、既存の再準備条件を満たして検出した新しいStroke Beginだけを次候補とする。
- ID発行順・欠番の有無や入力Bufferの消去方法は固定しない。専用公開状態、予約・待機Queue、容量解放時の再試行、Eviction Policy、容量不足専用canonical Trace Eventを追加しない。観測は19.1.12に従う。
- 形状追加専用の公開状態を作らず、必要な内部値は「Span Openか」と「Waveが生存中か」で足りる。

#### 19.1.7 Gameplay命中

Slash生成時を含む各更新で、前回Segmentと現在Segmentの4端点の**閉凸包**をGameplay Sweepの正本として一つだけ評価する。

```text
Q_n = conv{A_(n-1), B_(n-1), A_n, B_n}
```

これはSweep領域の定義であり、汎用Hull生成器の導入を要求しない。

- 生成時は`PreviousSegment = CurrentSegment = InitialSegment`とし、同じQueryへ退化入力を渡す。別の初期Overlapアルゴリズムを設けない。
- 通常はSpan不変なら平行四辺形、Span増加中なら台形となり、端点の一致等では三角形、軸が平行・反平行または生成時には線分へ退化できる。これらを同じ閉凸包Sweepの退化入力として扱い、別のGameplay規則・Hit形状を追加しない。軸平行やSweep退化だけを理由にFrameを補正・Clip・Rejectせず、非平行制約・最小軸間角度を追加しない。既存のfinite・単位方向・投影可能性の条件は維持する。
- Accepted Spanの非遡及は、完了済みの過去更新区間を後から再評価しないという意味に限定する。現在評価中の更新区間では、前回Segmentと現在Segmentの閉凸包全体をHit領域とし、当該更新のSpan増加によって広がる領域への命中を許容する。
- Gameplay判定厚み、端点Circle／Sphere、折れ線辺列、中間頂点、自己交差処理を追加しない。
- 命中の正本はGameplay Segment Sweepであり、VFX、Candidate Flight Bounds／先行準備範囲、刀Collider、Particle衝突ではない。

候補LogicalFragmentRefへのNarrowphaseはPhysics Scene上の現在採用Convex集合と現在local poseを使う。Active Transaction中の旧／Provisional Convexへの実Hitは観測できるが、切断受付は7.6の順序で見送る。受付・No-op分類・次のConvex切断入力はTransaction外のFinal Physics Ownerだけとする。

上記の閉凸包Sweepを`Q`、現在採用Convexを`C_i`として、少なくとも一つについて`Q ∩ C_i`が非空なら実Hitとする。AABB／OBB、候補Flight Bounds、Convex集合Bounds等をBroadphaseへ使用できるが、それらのOverlapだけでHitを確定しない。Render Triangleとの二次判定、未完成Final Convexの同期生成、Unity Collider callbackを別authorityとして追加しない。現在採用Hull外にだけ存在する表示Geometryへ当たらないことは、7.2の品質許容に従う。

#### 19.1.8 SlashWave VFX

VFXは`SourceSlashPlane`上の表示専用表現とし、事前生成した静的平面Geometryを再利用する。一方の面内軸を`SpanAxis`、他方をこれに直交する面内軸とする。現在のGameplay Segmentの位置とSpanに合わせ、位置・姿勢・寸法を一つのTransformまたは同等のper-instance変換で更新する。VFXの毎更新・毎描画のためにVertex／Indexを再生成・書換え・転送しない。

見た目の形状と表面表現はアート／実装詳細とし、Gameplay Hitや投機候補範囲の正本にしない。三日月形、可視輪郭とGameplay Segmentの端点一致、前方への膨らみは保証せず、見た目が実Hitより先行・遅行することを人間承認済みとする。視覚的な接触順・時間差を保証する補正は追加しない。

> 注：Textureを使うかMesh輪郭で表現するかは実装詳細であり、両方式の実装を要求しない。表面演出に必要な少量のShader定数更新と、Geometryの初期生成・初回転送は、上記のVertex／Index再生成・書換え・転送の禁止に含めない。変換の原点・寸法・基底の選択は実装詳細とし、特定のComponent構成、厳密1 draw、切断用VPプールへの統合を要求しない。

#### 19.1.9 同一SlashのLogicalFragment系譜単位消費

同じSlashが同一現在Fragmentへ毎更新再命中したり、自分で生成した子を再切断したりすることを防ぎつつ、消費済み系譜と祖先関係を持たない別の現在Fragmentは個別に切れるようにする。

1. 生存中Slashは、直接Hitを消費した不透明な`LogicalFragmentRef`の小さな一時集合だけを保持する。
2. 新しい候補Fragmentが実Segment Sweepと交差したとき、候補自身またはいずれかの祖先が消費済みかを確認する。
3. 祖先判定は、Cut Stateが既に保持する`LogicalCutOperation`履歴の親IDと直接子IDを読み取り、必要時に線形走査して行う。
4. 新しい親参照field／API、逆引き表、系譜Cache、子公開通知、切断側からSlashへの消費状態伝播を追加しない。
5. 祖先に消費済みFragmentがなければ、現在候補をSlash側集合へ追加してから、既存の切断受付へHitを一度だけ渡す。
6. 切断受付、片側空No-op、受付上限見送り、未公開親による受付拒否等の結果にかかわらず、同じSlashでは当該系譜を再試行しない。
7. 消費済みFragmentと祖先関係を持たない別の現在Fragmentは、同じSlashからそれぞれ独立に命中できる。
8. 消費済みFragmentから後に生成された直接・間接子は、既存履歴の祖先判定によって同じSlashから再命中しない。
9. 別`SlashId`は同じ現在LogicalFragmentへ通常どおり命中できる。
10. 集合はSlash終了時に回収し、LogicalFragmentへ恒久的なSlash履歴を追加しない。

通常の切断Operation履歴は公開順で親が子より前に存在するため、末尾からの一回の線形走査等で祖先を復元できる実装を許容する。極端な世代数を想定せず、当たり候補ごとのO(N)走査を初期正本とする。計算量改善を目的とするMetadataは実測で必要になった場合だけ別提案とする。

Slash Hit Detectorの公開意味には`ObjectId`を含めず、`LogicalFragmentRef`だけを扱う。Cut Stateが既存履歴を探索する内部実装でObject scopeやGenerationを使うことは妨げない。`ObjectGeneration`等は投機成果物の採否へ維持するが、Slashの一括Hit消費単位にはしない。

Phase 5.6の非命中追加分割との同時実行上の系譜扱いはPhase 5.6の統合時に確認し、Phase 4.51へ専用連携を前倒ししない。

#### 19.1.10 Candidate Flight Bounds／先行準備範囲

候補列挙用の範囲はPhase 4.53で初めて導入し、Phase 0.51～0.55、4.50、4.51、4.52の完了条件にしない。

- 現在のSpan Candidate構成について、`WaveLifetime`内の全`AcceptedSpan`を包含する有限包絡を導出できる場合だけ、Latch済み`SourceSlashPlane`、共通の`WaveOrigin`、固定`SpanAxis`／`TravelAxis`、`WaveSpeed`、`WaveLifetime`およびそのSpan包絡から保守的な`Candidate Flight Bounds`を作れる。
- 有限包絡を保証できない構成では、Phase 4.53の設定で有限な**先行準備範囲**を使用できる。この範囲は全実Hitを包含する保守Boundsではなく、範囲外の対象は先行準備されないことを許容する。
- Phase 4.51の実Hit Broadphase／NarrowphaseをPhase 4.53の候補集合または先行準備範囲内へ限定しない。範囲外で実Segment Hitした対象は、既存の現在状態切断経路を使用する。
- Gameplay SpanへのClamp、先行準備範囲の動的拡張、範囲外Hit後の候補再探索、先行準備の再試行を追加しない。有限包絡を利用するか有限な先行準備範囲に留めるかはPhase 4.53で決める。
- いずれの範囲も投機候補列挙専用であり、Hit、Pending Cut、ObjectGeneration更新を発生させない。
- 実Hitは常に現在のGameplay Segment Sweepと現在採用Physics Convex集合とのNarrowphaseで確定する。
- VFXの見た目の形状は候補範囲の意味上の正本にしない。
- 基本SlashWave、Prop切断、Humanoid現在Pose切断は候補範囲なしで完成する。

投機費用は既存の候補数上限、4.4の共有Dispatchと進路外の未Schedule候補取消で制御する。到達Deadlineは投機同士の優先判断に利用できる。命中確率は受付前Filterにだけ使用する。Schedule済みJobは中断せず、完了後に世代・前提を検証して不採用・回収する。4.1の性能曲線をSlash速度・寿命・Span・候補数へ当てはめる判断は4.53で行う。

#### 19.1.11 Quest Grip Poseと片刃方向Gate

QuestコントローラのOpenXR `grip pose`から位置・回転・Tracking Stateを取得し、刀Prefabの`GripToKatanaOffset`を掛けて刀姿勢を決める。`aim pose`は照準用であり、剣を握る姿勢の正本には使用しない。左右持ち、表示モデル、任意の物理グリップアタッチメント差はOffsetで吸収する。

刀Prefab内の`BladeFrame`へ次を定義する。

| 軸／点 | 定義 |
| --- | --- |
| `BladeAxis` | 柄から切先へ向かう刀身長軸 |
| `EdgeDirection` | 峰から刃へ向かう、刃が先行すべき方向 |
| `SideNormal` | 刀身の平たい面に垂直な方向 |
| `CutSamplePoint` | 柄から刀身長の約70%を初期候補とする速度Sample点 |

各SampleではCutSample Pointの位置差から速度を求め、刀身長軸方向の突き成分を除く。

```text
lateralVelocity = velocity
  - dot(velocity, BladeAxis) * BladeAxis

edgeLeadScore = dot(
  normalize(lateralVelocity),
  EdgeDirection)
```

`edgeLeadScore`が正なら刃が先行し、負なら峰が先行する。Latchの前提として最小速度、最小移動量とともに`edgeLeadScore > threshold`を要求する。初期検証値は`threshold = 0.15`、CutSample Point速度1.5～2.0m/s、移動量15～25cm、Sample Window 30～60msとし、T-038で調整する。閾値0.15は刃方向から約81度まで許す緩い半球判定であり、刃筋の精密評価を目的としない。

SideNormal方向への横滑り量や理想平面からの角度は合格条件にしない。多少刀が寝る、手首が傾く、斜めに振る場合も、刃側が概ね先行すれば切断を許可する。同じ向きの刀を戻すと速度だけが逆転してScoreが負になるため、新しいSlashを生成しない。プレイヤーが刀を返して刃を新しい運動方向へ向ければ、再準備条件を満たした後に次のSlashを生成できる。

刀の表示Objectには物理反発するColliderを持たせない。Edge Direction Gateは新しいSlashのLatch受付を制御する。発射条件または再準備条件を満たさない間は新しいSlashを生成しないが、生存WaveのSweepは刀の状態とは独立に継続する。したがって切れない状態の刀は地形、プロップ、NPCを完全に素通りする。切断可能時も刀を物理的に引っ掛けず、論理Hit、VFX、音、Hapticsだけを発生させる。

Tracking StateでPositionまたはRotationが無効になった場合は未発射の振り候補とSample履歴を破棄し、復帰直後は新しいWindowが蓄積するまでLatchしない。復帰前後を結ぶ見かけ上の巨大速度を斬撃として採用せず、速度・角速度の異常上限も設ける。すでにLatchedされ刀から独立して飛翔中のSlashWaveは追跡喪失後も継続する。

#### 19.1.12 Slash UX Sandboxと開発UI

Phase 0.5で作った一つのQuest Link Sandbox Sceneを使い、0.51～0.53でBlade入力、Gesture／Plane、HitなしCoreと第一候補、0.54で診断・再生・比較UIを完成させる。0.55は第一候補から実機試行を開始し、人間が試すと決めた候補を同じ出力境界へ追加・比較・調整して、利用可能な構成と暫定Presetを得る。第一候補の採用、全候補の先行実装・網羅比較、最終値、実切断・Physics・Predictionを要求しない。

初期UIには、実装済み方式の主要な調整値と挙動を理解するための主要な計算結果を表示する。調整対象はLatch／Frame／Span Candidate／Close方式、Begin選択、Sample Window／weight、Emitter、Gate・再準備・Tracking復帰、各方式の閾値、Wave速度・寿命と実装済みVFXの主要な調整値とする。最大到達距離は表示できるが独立設定にしない。

刀軌跡とBegin／Latch／Close、採用面・軸・Segment／SweepとVFX、Raw／Accepted Span、不採用の原因と同時生存Wave数を観察できるようにする。第一候補ではLive／Frozen Guide、Invalid理由、交点のr／q、符号付き分母とその絶対値による近平行判定、kappa_CとClose後の変化を初期表示に含め、主要な調整値から挙動を確認できるようにする。配置・まとめ方は実装詳細とし、全内部変数の網羅、常時全表示、方式変更後も同じ項目を維持する義務は置かない。軸が平行に近いときのVFX／Sweepの見え方も観察し、共通契約へ軸Clipを追加しない。現在採用ConvexとHit候補の表示は4.51以降とする。

同じ記録Pose列を再生し、Current／Pinned設定と実装済み方式を比較できるようにする。Latch／Frameの切替は未Latch評価へ、Span Candidate／Closeの切替は後続Waveへ反映し、生存Waveの方式・設定と利用中参照は19.1.4に従う。この調整・再生・比較能力は後続開発Buildでも維持する。Shipping同梱は要求せず、同じ出力境界内の方式交換・調整だけで本書改訂や0.51～0.55の再開を要求しない。

観測は既存Observability／Trace、Unity Profiler、Sandbox画面・Consoleと必要時の簡単な開発用出力を使い、独自Loggerを追加しない。調整・再評価用Preset、Pose列と内部値の保持・出力形式は実装詳細とし、旧形式互換や専用保存契約を要求しない。診断出力を製品状態・Commit・Recovery・Trace完全性の正本にしないが、生存Waveが実際に使用する確定済み設定の不変性は維持する。

### 19.2 ゲーム専用の遅延評価・投機実行器

Unity上に、未来イベントを必要時まで遅延しながら、空き計算資源では締切の近い結果を先行評価する専用層を実装する。

```text
Unity現在世界
  -> 不変スナップショット
  -> 未来イベントDAG
  -> 締切・費用・信頼度による先行評価
  -> 世代と前提条件の検証
  -> Unity世界へCommit
```

各評価ノードは入力スナップショット、依存ノード、到達締切、推定費用、予測信頼度、対象世代、キャンセル条件、成果物を持つ。

| 信頼度 | 主な対象 | コミット条件 |
| --- | --- | --- |
| Deterministic | 静止物、確定済み切断面から作る幾何成果物 | `SlashHitConfirmed`、Slash／SlashFrame、BaseObjectGenerationの一致 |
| Conditional | 既知Animation、単純運動、確定済みMobPlan | Deterministic条件に加え、Animation／PlanGeneration／予測前提の一致 |
| Speculative | 直接予測Gateを通った自由飛行剛体で、命中までに前提が崩れ得るもの | Deterministic条件に加え、実接触時の姿勢・Physics状態照合に合格 |

メインスレッドはUnity状態を数値データへスナップショットし、Job SystemとBurstは予測、頂点分類、交差、断面生成を行う。UnityのGameObject、Transform、Animatorをワーカージョブから直接操作しない。VP出力・転送・Geometry Commitは4.5.6に従う。物理適用は表示Geometryの完成を待たず7.1に従って進める。

### 19.3 未来姿勢の求め方

表示切断の先行計算も4.5.6のCommitted入力を使い、未Commit祖先成果物を入力にしない。入力準備は4.5.2に従い、Skinned対象の先行計算はJobベイク導入採用時に不変Rig Poseから共通VP入力を生成して共用Geometry切断へ接続する。同じRig Poseから骨Physics Proxyを姿勢化し、表示頂点ベイクだけの待ちを物理へ追加しない。準備中は現在Sceneと表示を維持し、4.5.6の正負直接Index出力・転送まで準備して、19.5の既存採用条件を満たすVP・成果物を命中時に再利用する。Final物理所属の確定を転送条件にせず、非スキニングまたは有効な準備済み入力では不要な工程を省く。

| 対象状態 | 予測方法 |
| --- | --- |
| 静止／姿勢固定 | 4.53で現在姿勢を使って先行計算 |
| 自由飛行・単純重力 | 4.54で`DirectRigidPredictionEligibilityGate`を満たす剛体だけ固定Unity／PhysX版の固定刻みに準拠したO(1)直接予測を使用する。Gate対象外・前提不一致は4.51へ進む。任意4.55のLocal Planeリベースは19.5.1の独立した採用Gateを使う |
| 既知またはMobPlanで確定したAnimation | 対象`FixedStepId`の副作用のない`ExplicitAnimationState`を解決し、交換可能なPose Evaluatorで任意時刻Poseを生成 |
| 接触・転動 | 接触・転動の未来運動を先読みせず、4.51の現在状態経路で処理する |

`DirectRigidPredictionEligibilityGate`は開始Snapshotだけから判定する内部boolean受付条件とする。動的かつSleep中でなく、`useGravity=true`、damping 0、Rigidbody Constraintsなし、既知のシステム所有Constraintなし、Fixed Step境界、`WorldPhysicsProfile`一致、既知Contactなし、予約済みForce／Torque・スクリプト駆動・Animation駆動・ユーザー介入なしであり、予測区間に速度Clampが適用されず、既存候補情報から衝突可能性が判明していない場合だけ受理する。未来区間の完全な無衝突証明は要求せず、受付後に衝突または介入が判明した成果物は既存の実命中検証で破棄する。Gate結果用の状態、enum、Profile、Proof、永続ArtifactまたはTrace Eventは追加しない。

一般外部Jointは7.2.2の製品入力契約で除外するため、Gateで任意Jointを列挙しない。点Anchorで固定された対象、接触・転動中の対象と、既知のBuildingWorldD6Constraint／ProvisionalSeparationConstraint付き対象は直接予測から外す。未来運動の予測が必要なGate対象外の剛体は4.51へ進む。静止／姿勢固定として先行計算できる対象は、点Anchorの有無によらず4.53に従う。

Current／Future Animationの意味上の正本は、ゲーム側が保持する副作用のない`ExplicitAnimationState`とGlobal `FixedStepId`である。`Animator`、`AnimatorController`、`AnimatorControllerPlayable`の内部State、Clock、Trigger、Transition、BlendをAnimation Planへ読み戻さず、稼働中Controllerを未来へ進めたり巻き戻したりしない。Future Pose `T+n`を得るためにControllerを`T+1 ... T+n-1`へ逐次rolloutする方式を標準経路にしない。

`FutureAnimationPoseEvaluator`は対象`FixedStepId`、そのStepへ解決済みのimmutableな`ExplicitAnimationState`、対象Rig／Animation Asset Set／Evaluation ProfileのIdentityを一体化した`ResolvedAnimationPoseInput`を受け、canonical Bone順のRig Pose Bufferを出力する。裸のState、別Stepへ属するState、Catalog Identity不一致を受理せず、Evaluator自身は`PlaybackRate`や現在時刻からPhaseを追加進行しない。入力Stateと現在Sceneを変更せず、同じ入力とBackendでは要求順に依存しない同じPoseを生成する。内部Cacheや前処理は許可するが、任意時刻評価に必要な履歴をEvaluatorの隠れた可変状態へだけ保持しない。履歴依存方式を後から導入する場合は、Transition元、Source Time、Blend、Foot／Inertialization履歴要約等を明示StateまたはPlanへ含める。

共通Pose Evaluatorは意味境界であり、全BackendをBurst Jobから呼べるとは仮定しない。Pose Table／custom samplerは固定長Bufferを使うJob Batch候補、Unity Playable／Animator出力はpool済みGraphを使うMain Thread予算対象としてSchedulerがBackend別にRoutingする。Playable評価をWorkerへ偽装したり、候補ごとのGameObject／Graph生成、Main Threadの無制限Evaluateを行わない。

AnimatorコンポーネントはHumanoid Retargeting、Avatar Binding、Playable出力先、現在表示のための任意Backendとして使用できるが、上位Stateのauthorityではない。標準V1はゲーム側Stateからcontrollerなしの`AnimationClipPlayable`／Mixer、または事前Bake済みPose Tableへ明示Source Time／Weightを渡す。AnimatorController／AnimatorControllerPlayableはLegacy Bridge、Editor Preview、比較Probeへ隔離し、削除してもMobPlan、未来評価、切断Predictionの公開契約を変更しない。

現在表示と未来評価は同じ対象Stepへ解決済みの`ExplicitAnimationState`から分岐する。Near Mobの表示Backendも独自にClip遷移やPhase進行を決めず、ゲーム側Stateを消費する。V1予測対象NPCではLook、腕IK、Foot IK等のプロシージャルPose Layerと左右反転を双方で無効化し、現在表示だけに適用しない。後段で再導入する場合は、Layer入力Snapshot、weight／MirrorMode、適用順、Generation、Identityを`ResolvedAnimationPoseInput`へ加え、全Backendで同じ意味を適用する。命中時に実際のBone Poseをスナップショットして最終証拠とする既存規則は維持し、予測State、Root Pose、代表骨Pose、Plan／Asset Identityが許容範囲外なら成果物を破棄して実姿勢から通常の後追い切断へ戻す。

自由飛行のO(1)固定刻み直接予測は外部Probeで技術成立を確認済みとする。T-017では本体のSnapshot、WorldPhysicsProfile、FixedStep、重心／Actor原点および回転処理との統合回帰を確認する。Probeのケース数、誤差値、性能倍率、暫定許容値は本体の製品保証へ転記しない。

### 19.5 スケジューリングとCommit

表示出力には4.5.6の直接配置・必要転送と公開条件を適用し、物理適用とは分離する。命中済み表示仕上げと投機準備は4.4の優先順で進め、必要なMetadataを物理から隠さない。有効な準備済み最終配置は再転送せず採用し、以下の実命中・面・世代条件を維持する。

優先順・容量・フレーム予算は4.4に従い、未完了依存は既存DAGでReady投入前に解決する。命中確率は受付前Filterに使い、一時描画費用は既存Profilerで観測する。投機Deadlineは優先判断に利用でき、遠距離候補は低優先の余裕があるときに進める。

投機ジョブは`SlashId`、対象`LogicalFragmentRef`、確定した`SlashFrame`、`ObjectId`、`BaseObjectGeneration`、共用Geometry・物理・Animation・MobPlanの各Generation、予測到達時刻を保持する。Commitには対応するSlashWave Segment Sweepの`SlashHitConfirmed`を必須とし、識別子、SourceSlashPlane、世代、予測前提のいずれかが一致しない結果は適用せず回収する。19.5.1の対象剛体だけはWorld姿勢一致をLocal Plane採用Gateへ置き換え、その操作に確定した面・基底frameと一致するGeometryだけを使用する。これにより、Candidate Flight Boundsへ入っただけの空振り候補や、古い非同期結果が新しい切断状態を上書きすることを防ぐ。

上記は通常の命中切断成果物のCommit条件である。7.9の確定後の任意分割・退役は非命中公開条件に従い、公開後は旧所有構成向けの予測成果物を拒否する。任意候補の準備・見送りだけでは通常予測を失効させない。

#### 19.5.1 剛体切断成果物の実姿勢リベース

本節は任意Phase 4.55で実装・比較・採否を判断する。未実装・延期・不採用時は現在状態経路を使い、4.54の完了条件にしない。

**目的と初期Scope。** 物体を予測World姿勢へ移動せず、予測時に仮決定した切断前物体ローカルの面と、その面から先行生成したGeometryを実姿勢に取り付ける。姿勢誤差を消す方式ではない。面接線方向の並進差は吸収しやすいが、法線方向の差と回転差は切断位置／向きへ残る。同一Slash内の対象ごとの面差と、近似検査で検出しきれない局所的な切断縁／VFX差を許容する。

初期対象は、単一RigidBody frameで全対象Geometryを表せる、点Anchorなし・接触なし・システム所有Constraintなしの自由飛行剛体だけとする。Geometry／local shape pose／scale／Topologyが予測開始から命中まで不変であり、一定重力、linear／angular dampingなし、外力／外部Torque介入なし、FixedStep境界、予測Horizon 0.5秒以下を要求する。点Anchor付き建物、BuildingWorldD6Constraint／ProvisionalSeparationConstraint付き対象、Skinned Mesh、骨相対Pose変化、再切断／Mesh世代変更、可変scale、shear、接触／転動は対象外とする。scaleは事前にGeometryへ固定し、予測・実姿勢の写像は正規直交回転＋並進だけとする。D-046のMobPlan／骨Pose検証を緩和しない。既存の接触／介入履歴と予測前提Snapshotで条件を確認できなければ対象外とし、初期版で完全な無衝突証明器を新設しない。

**面とframeの正本。** 面は正規化法線nと距離dの4係数で表し、符号は `dot(n,x)+d=0`、Positive／NegativeはSource面からの向きを保つ。符号を任意反転して子IDを交換しない。切断前のParentLogicalFragmentLocalId、BaseObjectGenerationと固定Geometry-to-Physics-frame写像をLocal Planeのframe identityとする。PredictedObjectPose／ActualObjectPoseはこの同じframeからWorldへの変換であり、重心位置をMesh原点として代用しない。

| データ | 正本・寿命 |
| --- | --- |
| SourceSlashPlane | Latch済みSlashFrameの不変World面。有限SlashWave Segment Sweep、実Hit、Slash飛翔VFX、攻撃方向、因果Traceに使用 |
| PredictedObjectLocalCutPlane | 命中前の予測Poseから求める仮Local Plane。Geometry Jobの完成とは独立した小さいimmutable Descriptorとして先に公開する |
| SelectedObjectLocalCutPlane | 命中時に操作単位で一度だけ確定する面。予測面採用または実命中面Fallbackのどちらかであり、全子と後着成果物の共通入力 |
| CommittedCutPlane | 採用時はActualObjectPoseから再構成するWorld面。以後は親から子へ引き継いだLocal Planeを各Actorの現行frameへ写したもの。命中時World位置へ固定しない |

列ベクトルの同次変換をT、planeの4係数をπとすると、`πlocal = transpose(Tpredicted) * πsource`、`πworld = inverseTranspose(Tactual) * πlocal`とする。点の変換でplane normalを処理せず、nとdを同じ正の長さで正規化する。実命中Fallbackは同じ規則でTactualからπsourceをlocalへ変換する。有限性、法線非zero、既存Geometry Kernelの数値／frame前提を検証する。選択後に係数を再推定・再量子化せず、子frameへの必要な座標変換だけを系譜から行う。World座標で生成した予測成果物は入力frameへ安全に還元できる場合だけ利用し、単にActor Transformへ予測位置を代入しない。

**命中時の一回選択。** 予測面DescriptorはSlashId／SourceSlashFrame、ObjectId／BaseObjectGeneration、ParentLogicalFragmentLocalId、Mesh／Physics／Topology世代、予測基底・対象FixedStepId、WorldPhysicsProfileとRebase Profileのidentity、固定local frame写像を保持する。実命中時の検証順は対象Scope、Descriptor有無、identity／世代／Step／前提、数値／幾何条件、視覚Gateとし、不一致を姿勢リベースで隠さない。初期版では予測対象FixedStepIdと実命中SnapshotのStepを一致させ、連続時刻の外挿で代用しない。Mesh／Collider JobのReady、成功／失敗、残り時間は面の採否入力へ含めない。

1. SourceSlashPlaneに属する実SlashWave Segment SweepのSlashHitConfirmedを必須とし、候補列挙だけでは面や切断を公開しない。
2. ObjectGeneration更新前の実Physics poseと世代を同じStepのSnapshotとして取得する。Mesh表示の補間Transformを物理Snapshotの代わりにしない。
3. Gate合格ならPredictedObjectLocalCutPlane、不在／不合格なら実命中から求めたLocal PlaneをSelectedObjectLocalCutPlaneとする。実姿勢からも有効面を構築不能な場合は通常の切断失敗・回収経路を使い、不正Planeを公開しない。
4. Pending Cut登録・世代更新・表示変更より前に、7.6のSource生存性とActive Transactionなしを確認してから、同じ実SnapshotとSelectedObjectLocalCutPlaneでFinal Physics Convex集合をrobust support分類する。片側空No-opまたは`MaxIncompleteCutOperationCount`到達時は面Descriptorをゲーム状態へ公開せず、CutOperationId、LogicalCutOperation、子、境界、Pending仕事を作らない。この判定にもMesh／Collider JobのReadyを使用しない。
5. 受付を通過した場合だけ、正のCutOperationIdを持つ同じ切断のPending Cut登録・基底から次世代への遷移・Local Plane確定を原子的に行う。この時点ではLogicalCutOperation、論理子、CutBoundaryRecordを公開しない。Operation公開前のTemporary clip／Capと局所VFX、点Anchor配分と所有者単位の固定／動的の導出、Provisional配分・OBB質量近似・分離Constraintは同じ面を読む。最初にSource面で表示して後から予測面へ切り替える二段階公開を禁止する。
6. 選択Descriptorを根拠に、既存の対応Jobを継続／優先化する。未発行なら同じ基底Geometryと選択面で後追いJobを投入し、面採用のためにJob.Complete、同期切断、同期cookを行わない。不採用面のSchedule済みJobは完了後に回収する。

基底世代は命中したPending Cut自身による既知の次世代への更新と結び付けて保持し、単に現在世代と旧BaseObjectGenerationが異なることだけで対応成果物を破棄しない。受付後の通常Transaction／Geometryは8章の局所authorityで照合し、別LogicalFragmentの更新や子孫切断だけで祖先GeometryをRejectしない。外部authority喪失はStale回収する。Snapshot取得からPending Cut公開までに前提が変化した場合は古い選択を公開せず通常の再評価へ送る。Pending Cut公開後はPlane採否を再実行せず、後にLogicalCutOperationを公開する場合も同じ面を継承する。後着Job失敗、予算超過、既存境界で判明した自前B-repの構築不成立、View変化でも元Source面へ切り替えず、Geometryは確定面のまま既存経路で進め、物理を成立させられなければ7.1のAbortへ進む。

**boundedな初期視覚Gate。** `RigidCutRebaseProfileV1`はversion／content identity、有限・非負の`MaxPlaneNormalAngleRadians`（π未満）、`MaxPlaneFieldErrorMeters`、`MaxProxyPointPixelError`を持つ。測定条件は判定時の左右眼View Projectionと各viewport pixel寸法へ固定する。未設定／不正Profile、片眼情報欠落ではリベースを採用しない。値の校正はO-047で行い、Probeの1 mm／0.5度／5 mmや画面幅1%を自動採用しない。

Mesh Jobとは独立した基底Geometryの保守的local Boundsの8 cornerを実poseへ写した固定8点だけを使用する。正規化したSource面(nS,dS)と候補World面(nC,dC)について、法線角度と`maxCorner(abs(dot(nC-nS,p)+dC-dS))`を検査する。後者はBounds内のsigned plane field差の上限であり、全切断線の距離上限ではない。同じ各cornerから両面への直交投影点を作り、左右眼それぞれで対応2点のpixel距離を比較し、その最大をProxyPointPixelErrorとする。生成済みCap／Mesh頂点をsample選択へ使用せず、Job Readyによって点集合を変更しない。非finite、Near Plane上／背面を含む投影不能、無効Boundsは不採用とし、画面外の点をclampして誤差を小さくしない。全条件が閾値以下のときだけ採用する。

これは8点の近似であり、真の切断線／シルエット最大pixel誤差、注視追跡、VFX全頂点比較、接線付近のTopology一致を保証しない。多少の未検出差を許容し、全Contour生成や全Triangle走査を採用Gateへ追加しない。World上限も併用し、遠距離で見えにくいことだけを理由に無制限の面差を許さない。有限Sweepの命中対象を増やさず、SlashHitConfirmedを取消・偽装せず、同一Slashの別対象にもリベース面を伝播しない。

**後続物理と表示。** 採用するのはGeometryと操作Local Planeであり、予測速度、予測World COM、支持・外界接触・安全判定の結果ではない。実Actorの最新pose／速度と7.2の親質量Snapshot・分離Impulse一回規則を使う。選択時のActualObjectPoseへ後日Actorを巻き戻さず、各子のPhysics Frameに保持したGeometryをその時点のActorへ取り付ける。Final用の自前B-repは7.2の由来Convex内に収まる構築条件とPhysics Frame対応に従い、完成出力の包含再走査は要求しない。攻撃Impulseの意味方向はSource Slash、分離Constraint／幾何Offsetの法線は採用面として区別し、両者が同じ法線であると仮定しない。必要な点配分や物理公開条件が未確定の間は既存Work依存で物理適用を待ち、成立不能と確定した場合は7.1のAbortへ進む。リベースを成立させるためにActorのposeを補正したり固定側へImpulseを加えたりしない。cookに由来する形状・接触差は7.3に従う。Render補間は既存Actor従属の範囲だけで行い、表示―物理誤差蓄積／すり合わせ状態を復活させない。

## 20. モブ未来計画とAI LOD

### 20.1 責務分離

UnityのNavMesh、Animation、Behavior系機能から高水準Intent、歩行可能領域、Path Cornerを取得してよいが、それらをそのまま未来へ進めたり巻き戻したりしない。ゲーム側に副作用のない`Mob Future Planner`を設け、高水準Intent、経路、速度プロファイル、Root軌道、`ExplicitAnimationStateV1`全体を数値データとして一定期間先まで焼き込む。Future Evaluation Schedulerはこの計画を読み取り、斬撃波の到達予定時刻におけるモブ姿勢と切断候補を先行評価する。

初期`MobTrajectoryKernelV1`を歩行Root位置・速度・向きの正本とする。`NavMeshAgent`の内部回避／移動積分、Animator Root Motion、Rigidbody、Behavior側Transform書換えを同時に位置の正本としない。NavMeshは経路／Corridorの取得、AnimatorはKernelが確定した明示Animation StateからPoseを生成・表示する任意Backendに限定する。同じKernelをNearの現在Tick更新と未来Trajectory生成の両方から呼び、Tier切替で別の運動モデルへ飛ばないようにする。

Crowd Stepは独立したwall-clockを持たず、Global `FixedStepId`の正の整数倍として進める。Kernelは固定MobId順のReadOnly `CurrentState`からWriteOnly `NextState`を作る二相更新とし、同一Step内で先に更新したMobの結果を別Mobが読まない。初期Desired Motionは副作用のないWaypoint／歩道Lane追従だけとし、位置、速度、向き、経路カーソル、累積移動距離、Locomotion状態を固定長SoAへ保持する。共有PRNGの消費順には依存せず、速度差等は`MobId + PlanGeneration + purpose`由来のstateless Seedで決める。

Animation PlanningとPose Evaluationを分離する。ゲーム側`AnimationPlannerV1`はBehavior Intent、Locomotion State、Root速度／向き、累積移動距離から`ExplicitAnimationStateV1`を生成し、`MobTrajectorySample`へRoot Stateと同じGroup epochで格納する。現在表示、未来切断、CPU Skinning／Bone ProxyはそのStateを共通の意味境界から読む。Pose Evaluator BackendはClip／Blend選択を逆向きにPlannerへ通知せず、現在表示用Animatorの内部状態からMobPlanを復元しない。

### 20.2 MobPlanデータ

`MobPlan`は最低限、次を保持する。

```text
MobId / PlanGeneration / RandomSeed
CreatedAt / StartFixedStepId / CommittedThroughFixedStepId / HorizonSampleCount
Intent / Preconditions / InvalidationReasons
NavMeshPathCorners / SpeedProfile / RootTrajectory
ExplicitAnimationStateV1(AnimationClipId / Phase / PlaybackRateCyclesPerSecond)
SpaceTimeReservations
```

V1では`SpaceTimeReservations`を空集合にでき、予約生成をMobPlan成立条件にしない。`NavMeshPathCorners`にはWaypoint／Laneから導出した固定経路列を格納でき、Unity NavMeshを使用しないSceneでも同じMobPlan schemaを使う。

時刻はwall-clockやFrame番号ではなくGlobal `FixedStepId`を正本とする。`StartFixedStepId`は`RootTrajectory`先頭SampleのFixedStepId、`CommittedThroughFixedStepId`は**最後にCommitされたSampleのFixedStepId（inclusive）**を表す。`HorizonSampleCount`は計画する全Sample数`N`で、Sample i（`0 <= i < N`）は`StartFixedStepId + i * CrowdStepScale`（`CrowdStepScale`はCrowd StepのFixedStep倍率）へ置き、計画Horizon終端は`StartFixedStepId + (N - 1) * CrowdStepScale`とする。Render時刻やSlash到達時刻から姿勢を選ぶときは、wall-clockやFrame時刻をGlobal `FixedStepId`へ変換する。`stepId < StartFixedStepId`では先頭Sample（Sample 0）でHoldし、除算を行わない。`StartFixedStepId <= stepId`では`i = (stepId - StartFixedStepId) / CrowdStepScale`（非負差の整数除算であり、C#の0方向切り捨てはこの範囲では数学的なfloorと一致する）をSample index、剰余を`CrowdStepScale`で割った値を補間係数とし、`stepId < CommittedThroughFixedStepId`のときだけSample iとi+1（i+1番目のSampleが存在する）を補間する。`stepId >= CommittedThroughFixedStepId`では補間せず最後のCommit済みSampleでHoldする。FixedDelta変更、pause、1表示Frame内の複数Physics Stepでも同じ式を使う。`CreatedAt`は観測時刻としてTraceへ残すが、軌道index計算や補間には使わない。

`FixedStepId`系の整数は符号付き64bit（`long`）とし、`CrowdStepScale > 0`、`1 <= HorizonSampleCount <= MaxSampleCount`を不変条件とする。`HorizonSampleCount * CrowdStepScale`、`StartFixedStepId + ...`、`stepId - StartFixedStepId`はchecked演算とし、overflow／underflowではPlanと補充を公開せず既存公開区間を維持してHoldする。`FixedStepId`はRun中にwrapや再利用をせず、枯渇（符号付き64bit上限到達）では新規計画と補充を停止して既存区間を維持する。

`RootTrajectory`のV1実体は固定間隔の`MobTrajectorySample`を保持する固定長Ring Bufferとし、各Sampleは`FixedStepId`、position、velocity、heading、LocomotionState、`ExplicitAnimationStateV1`、経路カーソルを持つ。全Sampleは対応する`MobId + PlanGeneration`へ属し、異なる世代のSampleを同じ有効区間として連結しない。別のFuture Animation Ringやruntime Animation Generationを追加せず、RootとAnimation Stateを20.3の同一Group epochでall-or-none公開する。骨行列は全Mob・全Sampleへ保存せず、斬撃候補になったモブについてだけ該当Sampleから未来Skeleton Poseを評価する。

`ExplicitAnimationStateV1`は正のint範囲の`AnimationClipId`、finiteかつ0以上のbinary64非wrap累積`Phase`（単位はcycle）、finiteかつ0以上のbinary64 `PlaybackRateCyclesPerSecond`を持つ。各Stateは、それを含む`MobTrajectorySample.FixedStepId`におけるPhaseへ解決済みである。任意の対象Stepでは20.2のSample選択と補間を先に行って単一の`ResolvedAnimationPoseInput`を作り、Pose EvaluatorはそのPhaseをsampleするだけで`PlaybackRateCyclesPerSecond`による追加進行を行わない。RateはPlannerが後続Sampleを作るための明示的なPhase速度と診断値である。時間駆動では、まず`stepDurationSeconds = (binary64)CrowdStepScale * FixedDeltaSeconds`、次に`phaseDelta = PlaybackRateCyclesPerSecond * stepDurationSeconds`、最後に`phaseNext = Phase + phaseDelta`の順でbinary64演算する。各乗算・加算の直後に`double.IsFinite`相当を検査し、いずれかが非finiteなら新しいGroup epochを公開せず最後の有効State全体をHoldする。finiteな正値がunderflowまたは丸めで0となること、および`phaseNext == Phase`となることは0進捗として許容し、異常扱いにしない。`checked`を使うのは`FixedStepId`、count、index等の整数演算だけで、浮動小数点安全性の根拠にしない。歩行の距離駆動時は`PlaybackRateCyclesPerSecond = speedMetersPerSecond / StrideLengthMetersPerCycle`として同じ単位へ正規化し、累積移動距離から求めたPhaseをSampleへ保存する。`FixedDeltaSeconds`、Stride Length、Rateまたは除算結果が非finite、`FixedDeltaSeconds <= 0`、`StrideLengthMetersPerCycle <= 0`なら新しいGroup epochを公開しない。

`AnimationClipId`はcanonicalな`Animation Clip Catalog`を参照する。各EntryはClip ID、`PlaybackMode = Loop | Clamp`、finiteかつ正のbinary64 `DurationSeconds`、Clip content identityを持ち、Clip ID昇順のCatalog bytes／hashを`AnimationAssetSetVersion`へ結合する。Loopでは`localPhase = Phase - floor(Phase)`、`sourceTimeSeconds = localPhase * DurationSeconds`とする。Clampでは`localPhase = min(Phase, 1.0)`、`sourceTimeSeconds = localPhase * DurationSeconds`とし、`Phase == 1.0`および1超過は最終Poseを保持する。隣接SampleのClip IDが同じ場合だけ累積Phaseをbinary64で線形補間するため、Loopの`0.98 -> 1.02`を逆方向へ補間しない。隣接SampleのClip IDが異なる場合、V1は次Sampleの`FixedStepId`まで前Sample Clipを保持し、境界でhard switchしてPose popを許容する。AnimatorControllerへ暗黙Crossfadeを委ねない。

2 Source Blend、Transition State、Look／腕／Foot IK、左右反転、Inertialization Stateは後段schema拡張とし、V1予測対象NPCでは現在表示と未来評価の双方で無効化する。導入時はSource ID／各Source Time／Weight、Pose Layer入力Snapshot／MirrorMode／適用順、必要な履歴、Generation／Identityを明示入力へ追加する。未知Clip ID、CatalogのMode／duration／content identity不一致、非finite／負Phase、非finite／負Rate、補間またはSource Time変換中の非finite化ではGroup補充を公開せず既存epochを維持し、現在表示は最後の有効な`ExplicitAnimationStateV1`全体でHoldする。

通常のClip／Phase／PlaybackRate変更は`PlanGeneration`へ従わせ、第二のruntime Animation Generationを設けない。Pose評価と依存切断成果物は`MobId`、`PlanGeneration`、`ObjectGeneration`、対象`FixedStepId`に加え、`RigDefinitionVersion`、Catalog内容hashを含む`AnimationAssetSetVersion`、`AnimationEvaluationProfileVersion`等のimmutable Asset／Evaluator Identityを必要な範囲で保持する。不一致時は旧PoseをCommitせず、同じPlan内でBackendだけを差し替えた結果として扱わない。

Buffer、Mob State、計画Group、Sampleは起動時またはScene load時の固定長Native領域から割り当て、Runtime成長とManaged allocationを行わない。Tier別Horizon、Refill閾値、最大Mob数、最大Sample数、同時再計画Group数は`MobTrajectoryProfile`に置く。容量不足時に既存Bufferを追い出したりMain Threadで待機せず、新規延長を拒否して既存の有効区間を維持する。

`CommittedThroughFixedStepId`までは、計画を変更するとプレイヤーから不自然に見える範囲として原則維持する。ただしプレイヤー接近・攻撃、経路遮断、モブ自身の切断など安全性やゲーム応答を優先すべき事象では即座に無効化できる。予約機能を導入した後は、予約競合も同じ即時無効化理由へ加える。再計画時は`PlanGeneration`を進め、旧計画へ依存する未来姿勢と切断成果物をStaleにする。

### 20.3 公開Ring Bufferと補充の所有権契約

`RootTrajectory`の固定長Ring Bufferは、再生側が読む公開区間とWorkerが書く非公開区間を分離する。Workerは公開Ringへ直接書かず、補充Work Itemごとに予約済みの非公開staging sliceへSample列を書く。旧`PlanGeneration`のJobは非公開staging以外へ一切書けない。

補充Work Itemの結果は`MobId`または`GroupId`、`PlanGeneration`、開始`FixedStepId`、`SampleCount`、入力末尾Sampleの値Snapshot（`FixedStepId`、位置、速度、向き、経路カーソル）を保持し、Worker完了時点では公開しない。値Snapshotを持たない実装では、Commitまたは回収まで入力末尾SampleのslotをpinするLeaseを保持し、Job完了前に元slotが再利用されて入力証拠が変わらないようにする。

公開は次のCrowd Step境界でMain Threadが行う。各Mobの補充結果をimmutableな公開descriptorへ構築し、staging内の全Sampleについて、finite性、連続Step、`PlanGeneration`一致、経路カーソル連続性、固定容量内を検証する。Group内の全対象Mobの検証が成功した後にだけ、単一のGroup publication slot index／epochを1回のatomic storeでCommitし、Readerは同じGroup epochに属するdescriptorだけを読む。Mobごとのhead／countを個別に公開せず、Group epoch切替の中間状態を読取側が観測しない。検証失敗、旧`PlanGeneration`、容量不足ではstaging sliceとdescriptorだけを回収し、既存の公開epoch・公開区間を変更しない。

Group補充はGroup内の全対象Mobをall-or-noneで公開し、Mob単位の部分Commitを許さない。一部Mobのstaging検証が失敗した場合はGroup全体を回収して既存公開epochを維持し、Group内の一部Mobだけが新しいHorizonへ進む状態を作らない。

公開はrelease-store、読取りはacquire-loadのメモリ順序とし、Readerは各Crowd Stepの開始時にGroup epochを1回だけacquire-loadし、そのepochに属するdescriptorだけを参照する。旧epochのdescriptorとSample slotは、全Readerがそのepochの読取りを完了したことをReader完了境界で確認できた後にだけ再利用する。世代別の固定slot（2個のGroup publication slotを交互に使い、3世代目を書く前に前々世代を回収する等）で、読取り中の旧slotを新しい補充が追い越して上書きしない。epochは符号付き64bitの単調増加値とし、Run中にwrapや再利用をしない。Readerはepoch値とdescriptorの`PlanGeneration`を組で照合してABAを防ぎ、同じepoch値が別内容へ再利用されないことを保証する。

wrap時は未再生Sampleの上書きを禁止する。補充によりRingの書込位置が未再生Sampleを追い越すか、同一Groupに未確定の補充Work Itemが既に存在する場合は新規補充を要求せず、既存の有効区間とepochを維持してHoldへ低下する。同一Groupの補充Work Itemは同時に1件だけとし、Commitまたは回収まで次の補充を要求しない。

### 20.4 プレイヤー介入時間によるAI LOD

距離だけでなく、プレイヤーが移動・斬撃波・その他の操作でモブへ影響できる最短時間を`MinInterventionTime`として推定し、計画Tierを切り替える。

| Tier | 状態 | 計画方針 |
| --- | --- | --- |
| Near | 介入が目前 | `MobTrajectoryKernelV1`を現在Crowd Stepでライブ実行し、同じSnapshotから短いHorizonも生成する。プレイヤー反応を優先 |
| Mid | 数秒の猶予 | 有効な固定長RootTrajectoryを主に再生し、短区間をBackground補充する |
| Far | 十分な猶予 | 同じKernelとPlannerでキネマティックなRoot軌道と`ExplicitAnimationStateV1`全体を長めに焼き込み、粗い経路だけを使用 |
| Dormant | 介入困難・非表示 | 低頻度のIntent／経路計画だけを保持し、必要時まで詳細姿勢を遅延生成 |

Far／Dormantでは個々のRigidbodyや完全な群衆衝突を先読みせず、NavMesh上の経路区間だけを確定する。粗い時空間予約はV1の成立条件に含めず、O-023で寸法と競合解決を確定した後段拡張とする。Nearへ近づいてもNavMeshAgent等の別Integrationへ切り替えず、同じKernelをQueue再生からライブ実行へ切り替える。Tier切替時にRoot姿勢、速度、経路カーソル、累積移動距離、`ExplicitAnimationStateV1`のClip ID／累積Phase／PlaybackRate全体とCatalog Identityを引き継ぎ、Backend側に独自Stateを再生成させない。

Mid／FarはQueueの隣接Sampleを時刻補間してTransformと`ExplicitAnimationStateV1`へ反映する。Clip IDが同じ区間だけ累積PhaseとRateを補間し、Clip IDが異なる区間は前Stateを保持して境界でState全体をhard switchする。歩行Phaseは累積移動距離／Stride Lengthから求め、再生Frame rateへ依存させない。Queue残量がRefill閾値を下回るとGroup単位の延長Work Itemを1件だけ要求する。Generationが変わった旧延長Jobは中断せず、完了後に不採用として回収する。

有効Sampleが現在Crowd Stepまで存在しない場合は古い軌道を外挿し続けない。Nearは固定されたLive Fallback予算内で同じKernelを1 Step進め、Mid／Farは最後の有限なRoot姿勢と`ExplicitAnimationStateV1`全体を保持する。Hold中にPhase、Clip ID、PlaybackRateを独自更新しない。Live Fallback予算または固定容量を超えたNearも同じHoldへ低下し、同期的な全群衆再計算、Buffer再確保、同一Frameの無制限再試行を行わない。Hold時間、Underflow数、Fallback数は固定Profiler Counterで観測し、上限値はO-045で実測後に決める。Traceは既存のMobPlan作成・延長・無効化・再計画・Prediction採否を正本とし、V1成立前にFallback専用Eventを必須追加しない。

### 20.5 切断投機との統合

Jobベイクの導入採用時は、斬撃波候補がモブへ到達する時刻を`MobPlan`上でサンプルし、未来Rig Poseから4.5.2のJobベイクによるVP入力準備と骨Physics Proxyの姿勢化へ分岐する。Phase 4.71は候補の準備要求・結果取得・失効まで、Phase 4.72は共用Geometryへの切断面適用、骨Physics Proxy分類、4.5.6の正負Index直接出力・転送および実命中での再利用までを接続する。成果物は既存TaskId等の相関情報／入力Snapshot／世代・Identity契約に結び付け、実命中時に既存の採用条件を再確認する。保持形状は固定せず、既存入力Snapshotを共有参照でき、各成果物への相関情報の重複保持を要求しない。計画が維持されていれば遠距離ほど完成済み成果物を再利用でき、未完成・不採用時は4.5.2の現在Pose同期経路と既存の後追い・回収へ接続する。

Mob Future Plannerも4.4の共有Dispatchとフレーム予算を使う。近距離で命中Deadlineを持つ未来Pose準備は遠距離MobPlan延長より先に扱い、どちらも現在状態の必須Physics／Geometry仕事より先にScheduleしない。

### 20.6 無効化と観測

主な無効化要因は、プレイヤーの介入可能領域への侵入、NavMesh変更、経路上の新障害、Behaviorの高優先Intent、Animation遷移、外力、対象の切断である。予約機能を導入した後は別モブとの予約競合も無効化要因へ加える。`MobPlanCreated`、`MobPlanExtended`、`MobTierChanged`、`MobPlanInvalidated`、`MobReplanned`、`MobPredictionUsed`、`MobPredictionRejected`をTraceへ記録し、`ReservationCreated`は予約機能導入後にだけ記録する。`MobId`と`PlanGeneration`から依存Taskを辿れるようにする。

V1の無効化粒度は単一Mob Planまたは固定Mob Group全体だけとし、影響依存を厳密に解析しない。無効化は`PlanGeneration`を進め、未再生Sample、未来Skeleton Pose、依存する投機的切断成果物を同じ世代検証でStaleにする。Player Bodyはプロップ等と非接触であるため、単なるPlayer Physics Contactを無効化要因に要求せず、攻撃、介入領域、Script Intent、Path変更、対象切断などゲーム側で観測可能なEventを正本とする。

### 20.7 段階導入とFuture Works

Phase 4.70の最初のPlayable実装は、固定ステップ二相更新、Waypoint／Lane Desired Motion、固定長未来Queue、Rootと`ExplicitAnimationStateV1`全体の再生補間、Loop／Clamp Clip Catalog、移動距離由来Phase、粗い世代無効化、既存Dispatcherへの補充投入までとする。4.65でJobベイクを採用した場合だけ、4.71で候補の未来Pose／VP準備要求・結果取得・失効を接続し、4.72で人形の実命中・先行切断完成・Commitを統合する。これらを4.70の完了条件にしない。不採用時は計画単体で完了し、先行切断の統合を要求しない。V1予測対象NPCではプロシージャルLook／腕／Foot IKと左右反転を現在／未来の双方で無効化する。この段階ではモブ同士の多少の重なり、遠方Mobの短時間停止、全Plan／Group再計算、Clip hard switchのPose popを許容し、ORCA、細粒度依存解析、Pose Layer／Mirror再導入を正しさの条件にしない。

重なりがプレイ上またはT-092の実測で問題になった場合だけ、次段として固定容量Uniform Grid、固定Cell走査順、`MaxNeighbors`、固定順Constraintを持つbounded ORCAを同じ`MobTrajectoryKernel`のDesired Motion後段へ追加する。ORCA追加後もFar／Dormantへ完全な群衆衝突を必須にせず、Tierごとに無効化できる。Grid／Neighbor／作業領域の容量超過ではMobを黙って省略せず、その計画GroupをORCAなしのLane追従またはHoldへ固定的に低下させる。

空間／Mob Group Chunk、Active／Candidate Interaction記録、Reverse Dependency DAG、Tick単位の部分再計算、新規Interaction用Guard Band、Flow Field、軌道圧縮はFuture Worksとする。これらは再計算量を減らす最適化であり、欠落しても全Plan／Group単位の再生成で正しく動作する。Flow Fieldを追加する場合も`DesiredMotionProvider -> MobTrajectoryKernel`境界だけへ接続し、ORCAや未来QueueがPath実装を直接参照しない。

## 21. 観測・トレース設計

Phase 0／0.1は完了済みの観測・非同期Capture能力として扱う。Phase 0.11は21.15の短時間NVENC確認、0.12～0.14は21.16の可変長Trace導入を担当する。保存形式、旧読込み、検証方法の扱いは17章に従い、過去Phaseを今回の改訂だけで再実行・再承認しない。

### 21.1 目的と責務分離

再現困難な競合、世代不一致、予測の無効化、古い成果物のCommitを調査できるよう、性能はProfiler、状態と因果関係はTrace、描画内容はCaptureで確認し、時刻・フレーム・対象IDで突き合わせる。映像だけでゲーム状態や処理の因果関係を判定しない。

7.9の任意処理は既存Task lifecycleと少数Counterで通常切断と区別する。所有者再編成全体を復元する専用Trace束を要求せず、Operationの記録は実際の切断履歴を表す。

### 21.2 Unity Profiler計測

処理種類ごとの軽量なProfiler計測を使い、IDをMarker名やホットパスの文字列へ埋め込まない。Work ItemのSchedule、Job開始、完了、CommitをFlowで関連付け、スレッドをまたぐ依存と各処理の費用を確認できるようにする。具体的なMarker名、集計・保存方法は実装詳細とする。

未来用JobベイクはPhase 4.65の限定実装で代表的な複数候補の同時要求を同期経路と比較し、Pose準備、Schedule／完了回収、AoS処理を含むMain Thread負荷と、Worker費用・結果到着時間を分けて確認する。既存Profilerと小さい比較記録を使い、4.5.2の品質とMain Thread負荷削減の結果から人間が導入の採否を決める。効果がなければ同期SkinnedMeshRenderer.BakeMesh維持を正常な完了結果とし、本体接続を要求しない。採用時は対応範囲・実装構成を決めて本体へ接続し、4.72で転送等の残る費用と先行切断完了率・採用状況を確認する。比較と採否決定を省いて4.65完了とせず、Phase 4.52の基本動作はその結果待ちにしない。固定高速化率、全候補の翌フレーム完成、大規模試験matrix、新しい計測schemaは要求しない。7.5のCollider用Physics.BakeMeshの測定とは分ける。

提案書が引用する外部の非同期スキニング参考測定は、原資料を本リポジトリでは未検証の参考情報として扱う。本体への導入判断はPhase 4.65の比較結果に基づき、外部測定のCPU・構成・数値・実装例を成立条件や必須Fixtureにしない。

### 21.3 Traceの相関と公開

Slash、LogicalFragment／Object、MobPlan、Work Itemの生存期間と世代、Schedule・完了・採否・Commit・破棄を既存IDと時刻・Frame／FixedStepで関連付ける。TaskIdはWork Itemを表し、Fragment識別子へ流用しない。Schedule前取消と、Schedule済みJobの完了後の不採用・回収を区別する。

CutOperationId、LogicalFragmentLocalId、CutBoundaryLocalIdは0を未設定に予約した正の32bit intとし、ObjectIdの生存期間全体で種別ごとに一意かつ非再利用とする。親の基底世代と後着記録時の現世代を区別し、別枝の更新だけで不正な履歴にしない。これらの意味はRuntimeの識別契約であり、Traceのfield配置や符号化を固定しない。

Logical Publication後に親と正負2子の関係を、Geometry Commit後に実在Boundaryと存在するSideの子参照・履歴完成を記録する。Boundaryが0でも完了できる。ObjectIdとCutOperationIdで相関し、欠落・重複・参照や件数の不整合を完全なOperation履歴として扱わない。

ゲーム状態の公開とTrace記録は非トランザクションとする。公開後にbest effortで記録し、書込失敗でゲーム状態を巻き戻さない。Geometry完成前に全読者が正常退役した場合はOperation／Childだけの未完履歴を許容し、架空Boundaryや完了を補わず、履歴完成だけの計算を続けない。正常退役自体をTrace書込失敗に数えないが、保存Trace全体がIncompleteとなり得る。

剛体リベースでは19.5.1の採用面、親frame・基底世代、採用理由を当該Pending Cutへ相関し、実際に選択した面を復元できるよう記録する。RigidCutPlaneChoiceは未選択／PredictedLocal／ActualHitLocalを区別し、未選択を公開しない。面の確定はTrace成功へ依存せず、記録の欠落・別操作や世代の混在を完全な選択とみなさない。記録だけで全物理軌道や採用Gateの再実行を保証しない。

PlayerLocomotionRejectedは固定Occupancyへの候補次姿勢Overlapによる要求全体Rejectを表し、侵入深度・Primitive ID・専用理由を記録しない。現行Slash相関は21.16.6に従う。Eventの分割数、field割当、数値token、固定長／可変長の保存表現は実装詳細とする。

### 21.4 履歴と診断保存

観測用の履歴・Queue・保存処理はboundedとし、過負荷でGameplayを待機させない。記録の欠落、履歴上書き、不完全なOperation履歴や保存の不成立を、完全な再現根拠として扱わない。保存Traceが完全であるという判断には、必要な記録が揃っていることを含める。判定の符号化や保存検証の方式は実装詳細とする。

診断保存ではproducerと読者の資源寿命を守り、書込み中の領域を完成済みとして公開・再利用しない。通常の手動・診断保存は利用できるが、4章の共通Player終了時には保存の開始・完了を保証しない。過去形式を将来も読めることや、失敗した保存のRecoveryを恒久契約にしない。

### 21.5 Editor Timeline

時刻／フレームとSlash・Object・MobPlan・Taskを使って履歴を検索し、世代、処理結果、採否理由、依存関係と対応画像を確認できるようにする。不完全な記録は完全な状態再現として表示しない。採用中の保存TraceをPlay Mode外でも閲覧できるようにする。UI構成と旧形式の読込み維持は実装詳細とする。

### 21.6 性能上の規則

ホットパスでTaskごとのDebug.Log、文字列化、全状態の毎フレームSnapshotを行わない。観測資源をboundedに管理し、記録・回収・保存の負荷と欠落を既存Profilerで確認する。Development Buildでは通常有効、Release Buildでは無効または重大異常だけとする。

### 21.7 映像キャプチャとTrace同期

#### 21.7.1 目的と証拠の範囲

映像はTemporary／Committed断面、VFX、左右眼差、表示の巻戻りを確認する補助情報とする。撮影した画像とゲームのFrame・対象・処理を対応付け、未撮影やDropを別フレームの画像で補わない。

#### 21.7.2 Phase A：Unity側の選択的キャプチャ

PoCではUnity側から必要な片眼映像または静止画を選択取得する。Window録画やHMD Mirrorを必須にせず、同期GPU Readbackやencode・保存の待機でGameplayを止めない。Phase 0.11の短時間NVENC確認と、Phase 4.8の連続録画の採否判断を分離する。早期確認のfps、寸法、Frame数、保存期間・形式は実装詳細とする。

#### 21.7.3 Capture相関

CaptureしたFrameとTraceの時刻・FrameId・対象IDを関連付け、後期OpenXR CaptureではOpenXR Frame、predictedDisplayTime、Pose、眼とSubImageも対応付ける。相関のために全Recordへ共通field列、Manifest、Receipt、専用保存schemaを要求しない。

#### 21.7.4 Phase B：OpenXR Projection Swapchain Capture

切断PoCとUnity選択録画の有用性を確認した後、必要ならWindows PCVR専用のOpenXR API Layerを追加する。開発Capture Profileは次へ固定し、汎用録画製品としての互換性は目標にしない。

| 項目 | 固定値 |
| --- | --- |
| Platform | Windows PCVR／Quest 3S有線Link／90Hz |
| Unity | 6.3 LTS 6000.3.22f1とPackage Lock |
| Graphics API | Direct3D 11のみ。Auto Graphics APIを無効化し、Editorも`-force-d3d11`で照合 |
| Color | SDR／sRGB 8bit |
| MSAA | 無効、`sampleCount = 1`を要求 |
| Dynamic Resolution | 無効 |
| Stereo | Single Pass Instanced／2D Texture Arrayを期待 |
| App Composition | Projection Layer 1枚。アプリ由来の追加Quad等は初期非対応 |
| Continuous Capture | 左眼、45fps、必要に応じ縮小解像度 |
| Encoder | 開発PCで利用可能なHardware Encoder 1系統だけを選定 |

API Layerは`xrCreateSwapchain`、`xrEnumerateSwapchainImages`、Acquire／Wait／Release、`xrWaitFrame`、`xrEndFrame`を追跡し、SwapchainのFormat、Width、Height、Sample Count、Array Size、Image Indexを管理する。設定を固定してもこれらの実値はRuntimeから取得し、決め打ちしたTexture HandleやImage Indexへ依存しない。

次の構成差を検出した場合、ゲーム本体やOpenXR Frame Loopは継続したままCaptureだけを無効化し、`UnsupportedCaptureConfig`と実値をTraceする。

```text
Graphics API != D3D11
HDRまたは未対応Format
sampleCount != 1
期待外のarraySize／Texture Layout
Dynamic ResolutionまたはImage Rectの想定外変化
Projection以外の未対応App Composition Layer
Eye／Array Indexを一意に対応付けられない
GPU Queue上で安全にCopy順序を保証できない
```

```text
xrWaitFrame -> predictedDisplayTimeを記録
xrAcquireSwapchainImage
xrWaitSwapchainImage
Unityが描画コマンドを投入
API LayerがxrReleaseSwapchainImageをIntercept
  -> 下流へReleaseする前に専用GPU TextureへCopy／MSAA Resolveを投入
  -> Graphics APIのQueue順序、Resource State、Array Sliceを保証
  -> 下流のxrReleaseSwapchainImageを呼ぶ
xrEndFrameをIntercept
  -> Composition Layer、SubImage Rect、Array Index、眼とCopyを対応付け
専用TextureをGPU Encoderへ渡す
```

Releaseを下流Runtimeへ渡した後のSwapchain Imageをアプリ所有物として読み書きしない。Copy／Resolveがアプリ描画より後、Runtime利用より前になるよう、対象Graphics APIのQueueと同期規則を守る。CPU待ちや全Texture Readbackで順序を保証するとVRフレームを阻害するため、GPU Queue上で完結できない構成は不採用とする。

「GPU-to-CPU Readbackなし」はフルサイズRGBA画像をCPUへ戻さないという意味に限定する。GPU Texture Copy、MSAA Resolve、Texture Array Slice選択、色空間／NV12等への変換、ハードウェアEncode、圧縮BitstreamのCPU／Disk転送は必要であり、各段階をProfilerMarkerとGPU Timestampで別計測する。

#### 21.7.5 取得範囲の限界

OpenXR API Layerが記録するのはUnityアプリが提出したProjection Swapchain Imageであり、Meta compositorが後段で行うReprojection／TimeWarp、追加Overlay、レンズ歪み補正、フレーム再利用、Quest Link圧縮後の最終HMD像は含まない。したがって、切断面、VFX、左右眼内容、古いMesh Commitの調査には使用できるが、HMD固有の残像、Link圧縮、Compositor timingの最終証拠にはしない。

Projection Captureが正常でHMD観察だけ異常な場合に限り、O-028で最終像側の併録を追加検討する。API Layerを入れた状態と外した状態でApp GPU Time、Compositor GPU Time、Dropped Frame、Frame Presentを比較し、録画機構自身が問題を作っていないことを必須条件とする。

#### 21.7.6 環境差

測定に使ったUnity／Package、Runtime、GPU／Driver、SwapchainとLink等の構成を識別し、異なる環境の結果を同一条件として比較しない。記録媒体、Profile ID、Manifest形式やhashによる照合方法は実装詳細とする。

### 21.15 非同期Captureと短時間NVENC確認

Phase 0／0.1から引き継ぐ能力は、非VR観測、Frame相関、必要なCapture、非同期処理、bounded資源とCapture失敗の分離とする。Phase 0.11では実際に使用するCapture経路で、対応環境のGPU画像から同一sessionの複数Frameを処理し、短時間のNVENC出力を確定する。正常Runのdecode結果が受付けたFrameの件数・順序に対応することを確認し、連続処理中も資源使用量をboundedに保つ。Main／Render ThreadはNVENCやGPUの完了、bitstream取得、file I/Oを待たず、in-flight容量の枯渇でGameplayを待たせない。

入力Texture、変換先、Encoder入力・出力、保存用bufferは、それぞれの非同期利用が終わるまで再利用・破棄しない。SourceのGPU利用完了とEncoder処理完了を同一視せず、正常終了では利用者の停止と参照終了を確認して安全に資源を回収する。資源を再利用する実装では、その利用終了後にだけ再利用することを確認する。再利用自体を必須にせず、Worker本数、Queue、Slot、所有型、通知形式、内部の準備・解放手順は固定しない。

Captureの不成立、故障、対応外構成はCapture内に閉じ、ゲーム停止を要求しない。ただし共有Device／Driver自体の喪失後の描画継続は保証しない。安全な所有状態や処理の停止を確認できない場合は、成功・完了・安全な解放を推測せず、同process内でCaptureを再開しない。後から呼出しが帰還してもこの制限を解除せず、process再起動を境界とする。Main／Renderに停止中Workerの帰還やJoinを待たせない。

実行中のPhase 0.11は改訂前に承認済みの方式でそのまま完了してよく、今回の改訂に合わせたWorker・Queue・Pool・NVENC・Publication・試験の作り直しを要求しない。完了条件は本節の能力と寿命条件とし、旧固定fps・Frame数・時間・試験階層を要求しない。製品用連続録画形式はPhase 4.8で必要性を判断し、この短時間確認の形式を自動的に昇格しない。

### 21.16 Phase 0.12～0.14 可変長Trace移行

#### 21.16.1 適用範囲と切替

対象はDomain Trace、Writer、History、保存／読込み、および以下の終了境界と製品接続とする。Encoderや映像取得経路を変更しない。0.12～0.14を同一変更系列の内部checkpointとする。製品Trace接続先が存在する場合は0.14でRelease既定を切り替える。製品Trace接続先が存在しない場合は、本系列のWriter／Historyについて、producer利用終了後に最終Drain・seal・保存を順に実行でき、保存・読込みで相関と不完全性を維持できる終了境界を確認すれば0.14を完了できる。実Capture終了経路への接続とRelease既定の採用は製品接続時に行い、その接続先の前倒し実装を0.14の完了条件にしない（2026-09-13人間承認）。旧新backendの並行搭載・二重記録を要求せず、置換済みのWriterと専用試験は同系列で削除できる。旧保存形式のLoader・Golden・再exportを維持する義務はなく、17章の扱いに従う。

#### 21.16.2 Writer、Lane、Drain、seal

Run開始前にimmutableなTrace Profileから固定Event mask、producerごとのPayload Ring容量、Runtime Index Ring容量、通常Drainの最大record件数、`MaxPayloadLength`を確定し、0.13以降は同じProfileからHistory Page size／Page数も確定する。各checkpointでbackendが要求するunmanaged領域の必要量をchecked算出し、全領域をRun開始前に確保する。単一allocationや複合ownerは要求しない。具体的な容量値、32 GiB、1 GiB等を既定値、最低値または製品保証にせず、確保不能または不変条件不成立ならRunを開始しない。Run中に領域を拡張／縮小せず、別lane／Pool探索、借用、動的Profile、sampling、severity taxonomyを追加しない。

1 laneは同時に1 producerと1 consumerだけが使う固定容量SPSCとし、backend非公開のBurst互換value-type Writerを構築時に単一laneへbindする。Writerは`IsEnabled(EventMaskBit)`と`TryWrite(RecordKind, PayloadPointer, PayloadLength)`だけを提供し、callerはpayload構築前にmaskを確認して正確な長さを決める。Writerはunmanagedなcaller memoryからlane所有Payload Ringへ必ずコピーし、return後にcaller memoryを参照しない。zero-copy、外部buffer lifetime移譲、managed配列、boxing、文字列化、serialize中の長さ決定を通常writeへ導入しない。

Runtime Index Entryは`RecordKind`、64 bit単調増加`PayloadStart`、`PayloadLength`だけを持ち、永続schemaではない。payloadの符号化と種別値の管理は実装詳細とする。WriterはIndex 1件と末尾paddingを含むPayload領域の空きを確認し、payload全体をコピーしてprivate index slotを書いた後、index write positionをrelease公開する。この公開だけをlane上のrecord commit pointとし、consumerはacquire済みentryが指すpayloadだけを読む。容量不足、oversize、内部Rejectは待機、拡張、別lane探索を行わず失敗を返し、lane-local Drop Countをsaturating加算する。Event単位の詳細な失敗Reasonは保存しない。Profileで無効なEventはDropへ数えない。

通常Drainはlaneを固定順round-robinで巡回し、構成された最大record件数で終了する。時間budget、動的quota、厳密なlane間公平性、producer間global sequenceを導入しない。単一lane FIFOだけを保証し、lane間の因果関係はTimestamp、FrameId、FixedStepIdおよびpayload内Domain IDで解釈する。正規停止順は`新規producer受付停止 -> Job完了／Worker停止 -> 全ownerがWriter使用終了 -> 全lane最終Drain -> Drop集約 -> seal`とする。Release Loggerはこのstop／joinを信頼し、Registry、Lease、Receipt、per-event Active Writerで再証明しない。stop／join後のstale Writer使用はReleaseで未定義動作とする。

#### 21.16.3 MemoryBounded Paged History

HistoryはProfileで指定した固定数のPayload PageをRun開始前に確保し、単一History Writerがframed recordを保存順に追記する。各Pageは`CommittedByteCount`だけを持ち、History全体で64 bit `CommittedRecordCount`を1個だけ持つ。History Index、Page状態enum、参照count、Page Lease、実行中export、live Snapshotを設けない。

Page内recordは`RecordLength | RecordKind | Payload`の連続byte列とする。`RecordLength`はLength field自身を除いた`RecordKind + Payload`の合計byte数を表し、Readerは`現在位置 + LengthFieldSize + RecordLength`を次record位置とする。Headerを含む最大record全体が1つのHistory Pageの書込み可能領域に収まり、`MaxPayloadLength <= Producer Lane Payload容量`であることをRun開始前に確認する。recordが現在Page末尾へ全体で収まらない場合は末尾を未使用のまま次Page先頭へ移り、1 recordをPage間で分割しない。

History Writerはrecord全体の空きを確認し、Headerとpayloadをコピーした後だけ当該Pageの`CommittedByteCount`とHistoryの`CommittedRecordCount`を進める。単一Writerかつ停止後だけ読むためatomic更新は要求しない。Page不足、History容量不足、checked演算失敗はgameplayを待機させずHistory Dropとして集約しTraceをIncompleteにする。停止後Viewは各Pageのcommitted prefixだけを列挙し、全recordを別配列へコピーしない。

#### 21.16.4 Trace保存と読込み

seal済みHistoryのcommitted prefixを、全recordの別配列を作らずboundedな作業領域で保存・読込みする。書込み中や保存失敗の結果を完成済みとして公開せず、記録欠落・不整合を完全な再現根拠にしない。具体的なfile構成、version、Header、field、hash、公開手順、Reader検証方法は実装詳細とする。電源断durabilityや未完成fileの部分救済は要求しない。

#### 21.16.5 完了条件と非目標

0.12は21.16.2のWriter公開順・所有権・非待機・bounded Drain、0.13は21.16.3の容量とcommitted prefix・停止後View、0.14は21.16.4の保存・読込みと21.16.1の接続先に応じた完了条件を確認する。代表負荷で記録・回収・保存の費用、allocation、欠落とメモリを確認する。具体的なFixture、形式の境界値、試験手順は実装詳細とし、旧形式互換試験や巨大な条件直積を完了条件にしない。

初版はMemoryBoundedだけとし、Rolling、Background／Segment Writer、保持中Page再利用、record分割、Context Registry、sampling、動的Profile、全producer間total order、Timeline random access、部分file修復、敵対的改ざん検知を追加しない。実測または具体的な用途が必要性を示した場合だけ別Phaseで判断する。

#### 21.16.6 単一Segment SlashWaveの相関

現行Runtimeの相関にFrontEdgeIdや動的Front専用Eventを使わない。SlashLatchedはWave公開、SlashExpiredは寿命終了、SlashHitConfirmedは現在採用Convexへの実Hitを表す。Task、Prediction、世代、Commitの相関を維持し、不要なGesture内部Eventは省略できる。

投機成果物はLatch済みSlashFrame、LogicalFragmentRef、BaseObjectGeneration、必要なGeometry／Physics／Animation／MobPlan世代、到達Step／時刻へ既存Snapshotで相関し、Commitには対応するSlashHitConfirmedを要求する。Estimator identityやSegmentIdを追加しない。相関の意味を全Recordの共通field列へ固定せず、保存形式・旧Reader・Goldenは17章に従う。

Slashの調整・再評価用入力と開発情報の扱いは19.1.12に従う。

## 22. 参考資料

Unity Packageの正確な採用版は`Packages/manifest.json`と`Packages/packages-lock.json`を正本とする。以下のXR Interaction Toolkit／Animation Riggingリンクは設計参照であり、実装開始時にPackage Lockの版へ合わせて更新する。

- [Unity 6リリースサポート](https://unity.com/releases/unity-6/support)

- [Unity Hub Editor管理](https://docs.unity.com/en-us/hub/install-editors)

- [Unity 6.3 Universal 3D／URPプロジェクト作成](https://docs.unity3d.com/6000.3/Documentation/Manual/urp/creating-a-new-project-with-urp.html)

- [Unity 6.3 Editorコマンドライン引数](https://docs.unity3d.com/6000.3/Documentation/Manual/EditorCommandLineArguments.html)

- [Unity 6.3 Graphics API設定](https://docs.unity3d.com/6000.3/Documentation/Manual/configure-graphicsAPIs.html)

- [Unity CLI](https://docs.unity.com/en-us/hub/use-unity-cli)

- [OpenXR Grip Pose仕様](https://registry.khronos.org/OpenXR/specs/1.1-khr/html/xrspec.html)

- [OpenXR 1.1 Swapchain／Frame Submission仕様](https://registry.khronos.org/OpenXR/specs/1.1-khr/html/xrspec.html#rendering)

- [Khronos OpenXR API Layer仕様](https://github.com/KhronosGroup/OpenXR-SDK-Source/blob/main/specification/loader/api_layer.adoc)

- [Unity XR Interaction Toolkit 3.0 Action-based Controller](https://docs.unity3d.com/Packages/com.unity.xr.interaction.toolkit%403.0/manual/xr-controller-action-based.html)

- [Unity XR Interaction Toolkit 3.0 Controller State](https://docs.unity3d.com/Packages/com.unity.xr.interaction.toolkit%403.0/api/UnityEngine.XR.Interaction.Toolkit.XRControllerState.html)

- [Unity 6.3 ProfilerMarker](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Unity.Profiling.ProfilerMarker.html)

- [Unity 6.3 Profiler Flow](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Unity.Profiling.LowLevel.Unsafe.ProfilerUnsafeUtility.CreateFlow.html)

- [Unity 6.3 ProfilerModule](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Unity.Profiling.Editor.ProfilerModule.html)

- [Unity 6.3 ProfilerRecorder](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Unity.Profiling.ProfilerRecorder.html)

- [Blender 4.5コマンドライン実行](https://docs.blender.org/manual/en/4.5/advanced/command_line/index.html)

- [Blender 4.5 Windows Portable ZIP](https://docs.blender.org/manual/en/4.5/getting_started/installing/windows.html)

- [Blender 4.5公式配布・SHA-256](https://download.blender.org/release/Blender4.5/)

- [Blender 4.5 Python API](https://docs.blender.org/api/4.5/)

- [Blender 4.5 Voxel Remesh API](https://docs.blender.org/api/4.5/bpy.ops.object.html)

- [Blender 4.5 Remesh Modifier](https://docs.blender.org/manual/en/4.5/modeling/modifiers/generate/remesh.html)

- [Blender 4.5 Shrinkwrap Modifier](https://docs.blender.org/manual/en/4.5/modeling/modifiers/deform/shrinkwrap.html)

- [Blender 4.5 Decimate Modifier](https://docs.blender.org/manual/en/4.5/modeling/modifiers/generate/decimate.html)

- [Blender 4.5 BMesh Fill Operators](https://docs.blender.org/api/4.5/bmesh.ops.html)

- [Blender 4.5 Solidify Modifier](https://docs.blender.org/manual/en/4.5/modeling/modifiers/generate/solidify.html)

- [Blender 4.5 Mesh to Volume](https://docs.blender.org/manual/en/4.5/modeling/geometry_nodes/mesh/operations/mesh_to_volume.html)

- [Blender 4.5 Volume to Mesh](https://docs.blender.org/manual/en/4.5/modeling/modifiers/generate/volume_to_mesh.html)

- [Unity 6.3 C# Job System](https://docs.unity3d.com/6000.3/Documentation/Manual/job-system.html)

- [Unity 6.3 Mesh.AcquireReadOnlyMeshData](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Mesh.AcquireReadOnlyMeshData.html)

- [Unity 6.3 NativeDisableContainerSafetyRestriction](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Unity.Collections.LowLevel.Unsafe.NativeDisableContainerSafetyRestrictionAttribute.html) ／ [外部メモリのNativeArray view](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Unity.Collections.LowLevel.Unsafe.NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray.html)

- [Windows VirtualAlloc：仮想予約とpage commit](https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/nf-memoryapi-virtualalloc)

- [Unity 6.3 SkinnedMeshRenderer.BakeMesh](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/SkinnedMeshRenderer.BakeMesh.html)

- [Unity 6.3 Graphics.CopyBuffer](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Graphics.CopyBuffer.html) ／ [GPU Buffer容量上限](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/SystemInfo-maxGraphicsBufferSize.html)

- [Unity 6.3 GPU Resident Drawer](https://docs.unity3d.com/6000.3/Documentation/Manual/urp/gpu-resident-drawer.html) ／ [RenderPrimitivesIndirect](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Graphics.RenderPrimitivesIndirect.html) ／ [GraphicsBuffer.Target.Structured](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/GraphicsBuffer.Target.Structured.html)

- [Unity 6.3 Mesh.AllocateWritableMeshData](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Mesh.AllocateWritableMeshData.html)

- [Unity 6.3 Mesh.ApplyAndDisposeWritableMeshData](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Mesh.ApplyAndDisposeWritableMeshData.html)

- [Unity 6.3 PlayableGraph](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Playables.PlayableGraph.html)

- [Unity 6.3 XRDisplaySubsystem](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/XR.XRDisplaySubsystem.html)

- [Unity 6.3 XR Mirror View Blit](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/XR.XRDisplaySubsystem.GetMirrorViewBlitDesc.html)

- [NVIDIA Video Codec SDK](https://developer.nvidia.com/video-codec-sdk)

- [Unity 6.3 Physics.BakeMesh](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Physics.BakeMesh.html)

- [Unity 6 Built-in 3D Physics／DOTS Physics区分](https://docs.unity3d.com/6000.0/Documentation/Manual/PhysicsSection.html)

- [Unity Native plug-ins](https://docs.unity3d.com/jp/current/Manual/plug-ins-native.html)

- [NVIDIA PhysX 5.4 Convex Mesh cooking](https://nvidia-omniverse.github.io/PhysX/physx/5.4.1/docs/Geometry.html)

- [NVIDIA PhysX PxCreateConvexMesh](https://nvidia-omniverse.github.io/PhysX/physx/5.3.0/_api_build/group__cooking.html)

- [Unity 6.3 Mesh Collider最適化](https://docs.unity3d.com/6000.3/Documentation/Manual/physics-optimization-cpu-mesh-cooking-options.html)

- [Unity Visual Effect Graph](https://docs.unity3d.com/ja/current/Manual/com.unity.visualeffectgraph.html)

- [Unity 6.3 Physics.gravity](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Physics-gravity.html)

- [Unity ShadowCastingMode](https://docs.unity3d.com/ja/current/ScriptReference/Rendering.ShadowCastingMode.html)

- [Unity Graphics.RenderMeshIndirect](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Graphics.RenderMeshIndirect.html)

- [Unity Graphics.RenderPrimitivesIndirect](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Graphics.RenderPrimitivesIndirect.html)

- [Unity Graphics.RenderPrimitivesIndexedIndirect](https://docs.unity3d.com/6000.0/Documentation/ScriptReference/Graphics.RenderPrimitivesIndexedIndirect.html)

- [Unity 6.3 ShaderLab Stencil](https://docs.unity3d.com/6000.3/Documentation/Manual/SL-Stencil.html)

- [Unity 6.3 Humanoid Animation Import](https://docs.unity3d.com/6000.3/Documentation/Manual/ConfiguringtheAvatar.html)

- [Unity Animation Rigging - Two Bone IK](https://docs.unity3d.com/ja/Packages/com.unity.animation.rigging@1.2/manual/constraints/TwoBoneIKConstraint.html)

- [Synty POLYGON City Pack](https://syntystore.com/products/polygon-city-pack)

- [Adobe Mixamo FAQ](https://helpx.adobe.com/creative-cloud/faq/mixamo-faq.html)

- [Quaternius Universal Animation Library 2](https://quaternius.com/packs/universalanimationlibrary2.html)
