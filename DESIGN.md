# VR斬鉄剣ゲーム 技術設計書

*即時シェーダ切断と非同期メッシュ／物理更新による、低遅延・反復切断パイプライン*

| 項目 | 内容 |
| --- | --- |
| 文書目的 | Codexで継続更新するプロジェクト設計上の正本 |
| ステータス | Draft v1.5 / PoC実装準備・観測／未来評価設計段階 |
| 作成日 | 2026-08-21 |
| 最終更新 | 2026-09-24 |
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

- 生涯切断数や全Pending Cut数ではなく、実際にBatchへ投入する`TemporaryRenderCapRecordSet`、対象の共用Geometry、固定長`SelectedTemporaryClipPlaneSet`が一時描画コストを決める。Geometry Commit済みの境界は一時描画費用へ含めず、即時Clip容量を超えた面について追加のClip Plane評価・対応Stencil Volume submitを行わない。

- 切断対象の表示とStencil Volumeは、幾何切断に必要なTopologyを持つ同じ共用Geometryを正本とし、一度の切断・Cap生成結果を両用途へ使用する。通常切断の正常成功は正負二集合、各側一つのFinal Physics OwnerとLogicalFragmentとする。Compound Physics Proxyは独立した物理表現として残し、製品用Strict Solid Cut Meshは生成・常駐・Fallbackのいずれにも使用しない。

- 建物由来の動的分裂子には7.2.2の独立World D6を使うが、倒壊防止や変位上限を保証しない。一般外部Jointを製品切断対象から外し、正負二集合・点Anchor・Graphなしの方針を維持する。

- バックグラウンド結果は世代番号で検証し、古い結果を安全に破棄できるようにする。

- 短期プロトタイプでは対象範囲と品質契約を限定し、計測結果に基づいて拡張する。

- 基本Playableを現在状態のProp／Humanoid切断で先に完成させる。後段では飛翔する斬撃波の到達時間を計算猶予として利用し、命中前に未来姿勢、表示／Stencil共用VP Geometry、Convexの切断を投機的に評価する。

- 予測結果は確定・条件付き・投機の信頼度と世代番号を持ち、実接触時の検証に成功したものだけをコミットする。

- 非同期処理と状態遷移は最初のPoCから相関ID付きで記録し、性能計測と因果関係の調査を同じ時間軸で行えるようにする。

- デバッグ映像はUnity側の選択的Captureを使用し、Traceを補助する証拠としてFrameIdと対応付ける。

## 3. スコープ

### 3.1 初期垂直スライス

Phase 0.55でSlash操作を先に調整し、4.50～4.52でProp＋動作中NPCの基本PlayableをPredictionなしで成立させる。以下の街区・製品Assetを含む仕上げはPhase 6／7で行い、後続最適化・コンテンツ完成を4.52へ前倒ししない。Gameplay命中は19.1の一本Segment Sweep、表示は19.1.8のSlashWave VFXとする。

- 街区1つ、切断可能プロップ約10種、NPC 1体、刀1本。建物を切断対象に含め、道路は切断対象に含めない。

- 単一切断、連続切断、処理中の再切断を検証する。処理中の再切断は先行`LogicalCutOperation`が公開済みで、Geometry Work／Geometry Commitだけが未完了の区間を保証対象とする。先行Operation未公開の親とその仮表示領域への後続切断は受付けない。

- 即時clip表示、仮断面、後追い共用VP Geometry切断、Convex差し替えまでを一連で実装。

- 切断対象は閉じた静的メッシュと、切断時に姿勢を固定できるHumanoidに限定。

- 通常切断では非空の切断結果を寸法だけで生成省略・消去せず通常Fragmentとして扱い、専用Convexを持たない共用Geometryも同SideのFinal Physics Ownerへ所属させる。生成後のLogicalFragment全体の任意寿命終了は7.10に従う。

- PCVRを対象とし、実アプリの両眼描画90fpsを性能目標とする。XRコンポジタの再投影は瞬間的な取りこぼしへの安全網であり、常用前提にしない。

- 実装と性能計測は非VRモードから開始する。Phase 0.5でQuest 3S有線Quest Linkの最小XR確認、0.51～0.54でBlade入力、Gesture／Plane、HitなしSlashWave Coreと第一候補、Sandbox UIを段階実装し、0.55で実機探索・調整を行う。VP表示系列は並行可能とし、1.50～1.52の能力確認後にPhase 2へ進む。実切断との統合は4.50以降とする。

- 剣を素早く振ると斬撃波が拡大し、有限速度で飛翔する。表示は19.1.8に従う。接触時の即時分離を主要攻撃表現の目標とし、必要な準備と同フレーム表示の扱いは4.5.2に従う。

後期の独立した任意拡張として、確定済みの物理所有単位を二つの通常物体へ分ける追加空間分割と、共用Geometryが確定空の通常物理物体を終了する物理GCを7.9に定める。両機能は個別に有効化でき、通常切断の成立条件にも相互の必須依存にもせず、初期垂直スライスの必須範囲へ前倒ししない。

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

Windows PCVR向けに生成する標準Playerは、PoC用途のPlayerを含めx64 IL2CPPとし、Development Build／非Development Buildで共通とする。Editor上のEdit Mode／Play Modeおよび開発用HarnessにはPlayer化・IL2CPP化を要求せず、Mono Playerは補助的な機能確認・参考測定に使用してよい。Mono Playerの提供・継続互換性は要求しない。製品性能の判断は14章に従う。

IL2CPP設定の反映と初回Player確認は、次の独立した作業単位で行い、現在接続済みの代表シナリオをx64 IL2CPP Development Playerでbuild・起動・短時間実行できることを確認する。製品性能評価やコンテンツ完成まで延期しない。以後、製品Playerに新しい主要経路を初めて接続する既存Phaseの完了時に、そのPhaseの代表シナリオをIL2CPP Playerで確認する。内部実装だけの作業単位やEditor／HarnessだけのPhaseには追加のPlayer確認を要求せず、全試験のPlayer移植や毎変更のPlayer buildも要求しない。

Unityを更新するときもプロジェクトは作り直さない。Unity Hubへ新旧Editorを並存させ、Gitの専用アップグレードブランチでバックアップ、Package互換性確認、Editor変換、再インポート、固定テスト、非VR性能基準、XRスモークテストの順に検証する。合格するまで旧Editorを削除せず、`ProjectVersion.txt`、`manifest.json`、`packages-lock.json`の変更をレビュー対象とする。

Unityメジャー版ごとの恒久的なプロジェクト複製は作らず、リポジトリ直下の1プロジェクトを正本とする。同時比較が必要な更新作業だけ、リポジトリ外の兄弟ディレクトリへGit worktreeを作成し、検証後に破棄する。`Library`等の生成物はworktree間で共有しない。

## 4. システムアーキテクチャ

共通Player終了出口へ送るのは、共用Geometry切断またはFragment／Boundary／Operation構築で既存境界から表面化した予期しない内部エラー、4.5.4で定めるCPU backing容量の不成立、同節のGPU backing容量の不成立、5.6の必須Stencil構成の不成立の4条件とする。製品PlayerのComposition Root上の共通Player lifecycle境界が、Renderer初期化より前からPlayer寿命を通じて単一の非永続終了要求latchを所有する。Renderer初期化処理と切断Coordinatorはこの境界へ報告し、切断受付・Commitも同じlatchを参照する。外部Poolを含むWorkerの異常は既存の通知・回収境界でMainへ渡し、Workerはlatchや終了APIを直接操作しない。Mainが最初に扱った原因でlatchを一方向に確定し、同じMain Threadで直列に処理する新規切断受付と未公開切断成果物のCommitを閉じてから、既存ログへbest effortで一度記録し、製品Playerの終了APIを一度呼ぶ。「終了要求後」はAPI呼出し後ではなくlatch確定後を指す。エラーに関わる未公開成果物を公開せず、初期化中はGameplayを開始しない。同一sessionでのrollback・整合回復、全Work／資源回収、ログ永続化、Trace／Captureのトリガー・post-roll・freeze・保存・Publication・CaptureComplete・durable flush完了、終了所要時間は保証しない。ログが使えなくても終了を妨げず、回収・保存を待たない。任意の保存は非待機のbest effortに限る。内部エラーの全検出は保証せず、このためのValidator・詳細Reason・永続schema・復旧基盤を追加しない。通常の世代失効・出力予約不足、7.1の個別物理退役、Capture-only Fail Fast／Poison、Tool／Benchmark／Trace・Capture Run単位の失敗は既存境界を維持する。

> **状態モデル** 生存LogicalFragmentごとの一回の物理置換は7.1の短寿命PhysicsSplitTransactionが所有する。正負Final PhysicsとLogical Publicationを一体で公開した後はTransactionを終了し、各子は一Final Physics Ownerを専有する。Pending CutはGeometry責務だけを残し、4.5.6の祖先順Geometry CommitまたはBranch退役で完了する。具体的GeometryとCutBoundaryRecordは後着でき、Work完了・CPU範囲Published・GPU転送はGeometry Pipeline内部で扱う。

### 4.1 コンポーネント境界

| サブシステム | 責務 |
| --- | --- |
| Blade Pose Adapter | 右手ControllerのOpenXR Grip Poseへ単一のGripToKatanaOffsetを適用し、BladeAxis、EdgeDirection、SideNormal、追跡有効性を提供 |
| Cut State | LogicalFragmentの生存性、各Source最大1件のActive PhysicsSplitTransaction、Pending CutとGeometry依存、採用面・Side・親子履歴、受付上限を管理する |
| Temporary Slice Renderer | clip、論理破片を追従配置へ描く表示、仮断面、切断縁演出 |
| Visual Slice Worker | 4.5.2で導入を採用した未来Rig Poseの非同期ベイク・VP入力準備、VP入力の三角形切断、断面生成、属性補間、VPプールへの正負Index直接出力 |
| Physics Slice Worker | 7.2のOwner単位Cut/Cook処理とFinal候補生成 |
| Commit Controller | 8章のauthority照合、Final Physics／Logical Publicationの一体切替、4.5.6のGeometry Commit、7.1の単独退役を担当する。建物D6、7.9の任意処理と7.10の退役も同じ公開・退役境界へ接続する |
| Blade Gesture／Plane Detector | Grip／Tracking、Gate、accepted samples、Stroke BeginとPlane候補を作る。対象Hitを決定しない |
| Slash Latch／Frame Estimator | LatchReadyとValid／固定軸を独立に返す交換可能境界。内部方式は19.1へ集約 |
| Slash Span Candidate／Close Estimator | Raw候補の有効性と現在刀入力の受付終了を別々に判断する。必要な一時状態をWave寿命中に保持できる |
| SlashWave Simulator | 19.1の生成条件を一か所で判定してWaveを公開し、固定面・軸、TravelDistance、AcceptedSpan、単一Segmentと寿命を管理する。対象や切断状態を解釈しない |
| SlashWave Hit Detector／VFX | Hitは現在採用Convexへの閉Segment Sweepと系譜消費、VFXは19.1.8の表示専用平面表現。Hitの詳細は19.1 |
| Slash Candidate／Prediction | Phase 4.53以降に投機候補範囲を列挙し、基本Waveと実Hit検索を変更しない |
| Future Evaluation Scheduler | 不変Snapshotから既存DAGでReady化し、4.3／4.4の実行先への投入・回収を採否・Commitへ接続する |
| Mob Future Planner | 20章のRoot姿勢とAnimation入力を整合した未来計画、更新・失効を担当する。具体的な生成・保持・負荷制御方式は研究後に決める |
| Animation Pose Evaluator | 19.3の明示入力と対象時刻から、リターゲット済み骨Pose Tableを使ってCurrent／Future共通のRig Poseを生成する |
| Observability／Trace | Profiler計測、状態イベント、Work Item／Job相関、boundedな履歴、診断保存、Editorタイムラインを提供 |
| Visual Capture | Unity側の選択的片眼録画と異常時静止画をTraceへ関連付ける |

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

自前の非同期CPU Workは、標準Unity Job SystemとOS優先度を下げた二つの常設スレッドプールへ分担する。Workは論理作業単位、JobとPoolは実行先であり、分類・依存解消・投入・完了回収・採否・Commitは既存のMain側DAGと各Subsystemに残す。C# Taskの大量発行をCPU計算の標準にしない。

| 新規投入先 | 用途 | 初期設定 |
| --- | --- | --- |
| Unity Job System | 現在状態の物理安全、受付済みPhysicsSplitTransactionのConvex数値処理・Physics.BakeMesh等、Final Physics／Logical Publicationに必要なurgent Work | Unity worker数・OS優先度は変更しない |
| Geometry Pool | 受付済み切断の共用Geometry切断・Cap・属性処理等の数値Work | BelowNormal、G=8 |
| Background Pool | 命中前の投機Geometry／Convex、未来Pose・VP入力準備、Mob計画、任意追加分割等の遅延可能な数値Workと必要なPhysics.BakeMesh | Lowest、B=2 |

この分類は自前の新規投入に適用し、Unity内部Jobの専有・再分類を意味しない。命中前から投入済みのWorkの継続と、命中後の未投入・後続Workの分類は4.4に従う。G/Bは起動時の実装設定でよく、8／2は暫定初期値とする。実ゲームのMain／描画／Physics／urgent時間、下位処理量と滞留を測ってO-035で調整し、実行時の自動伸縮・最適化を要求しない。Unity内部を含む合計thread数がコア数を超えることを許容するが、本数と資源容量は有限に保つ。

外部Poolは既存Burst数値KernelへのDirect Callを初期経路とし、初回呼出しの準備をMainで済ませ、不変入力とcaller提供scratch／outputを渡す。FunctionPointerは実装上選択できるが両方式の恒久搭載は要求しない。外部からのIJob.Run／Schedule／Completeや自動並列分配の再実装を前提にせず、Batch方法は必要な範囲で選ぶ。空なら待機し、投入済みの独立WorkはMainの追加pumpを待たず順次実行する。CPUを譲るためだけのKernel内yieldや人工的な細分化を要求しない。

通常切断の新規Bakeは7.2のurgent Unity Job経路へ、未命中の投機・任意追加分割のBakeはMainのMesh適用後にBackground Poolの通常C#処理へ投入する。非urgent BakeをUnity Jobへ投げ直さず、BurstからPhysics.BakeMeshを直接呼ばない。Mainで取得したMesh識別子とCooking Profileを渡し、Queue待ち・実行中を含めMeshを利用終了まで保持して変更・破棄せず、実行先を問わず同一Meshの同時Bakeを禁止する。Playerで読取り可能な入力、Bake後のGeometry不変、適用先とのCooking Profile一致、既存Bake共有枠と7章の採否・失敗・回収境界を維持する。

通常命中の同期SkinnedMeshRenderer.BakeMeshとCPU入力準備、現在骨へのPose適用、資源予約、Unity Mesh適用、Collider／Rigidbody構築、GPU更新、公開・退役は既存Main／物理境界に残す。外部WorkからGameObject、Component、Transform、Renderer、Rigidbodyを操作しない。軽量な同期Pose評価やGC巡回まで一律に外部化せず、現在Sceneを未来へ進めない。Task／Awaitableは従来のI/O、保存、Editor、外部待機、Unity非同期制御に使用できる。

OSへCPU時間配分を委ね、固定クォータ、最低帯域、厳密な開始・完了順や期限は保証しない。Queue満杯時に限らず、下位Workとのcache・SMT・帯域・クロック等の共有によってMain・Physics・描画が遅れることを許容する。OSのthreadプリエンプションをWorkの安全な取消と同一視せず、利用中資源を保護する。3.1／16章の両眼90fpsと即時表示の性能目標は維持する。

**Mainのコア配置。** WindowsのP/E異種コア環境では実Topologyと使用許可から利用可能なPコア集合を求め、Mainへthread-selected CPU Setsを指定する。非異種コアでは指定不要とする。Mainのhard affinity・OS優先度、process-default CPU Sets、既存の他threadの配置・優先度は変更せず、解除時は元の指定へ戻す。導入時に適用・保持・復元と実配置を確認し、取得・適用不能を成功と扱わない。CPU Setsによる絶対的なEコア排除は保証せず、不成立環境への対応は別途判断する。常時監視・再設定、hard affinityへの自動切替、新しいPlayer終了条件は設けない。

### 4.4 Ready WorkのDispatchと完了回収

Mainの共有Dispatchは、既存DAGで依存が解消したWorkについて、入力保持・出力予約・有限容量・描画フレーム残予算を確認し、投入時の用途に従って4.3へ渡す。投入機会や共有資源が競合すればurgentを先に扱うが、プール間の厳密な実行順、同順位stable順、投機Deadline sortは要求しない。外部投入を上位Readyなし・Unity workerの空き待ちへ依存させず、条件が揃えば並行投入できる。予測対象時刻とQueue順位を区別し、Main予算から下位CPU時間を厳密に配賦する機構を設けない。

一描画フレーム内で、非blocking完了回収→依存解消→Ready Work投入を複数回進められるものとし、同じ残予算を共有して呼出しごとに再充填しない。進捗がなければその機会を終え、未完了Workのbusy polling、spin、強制Complete、PlayerLoop／Commit再入や全Work待ちを通常経路へ持ち込まない。配置・回数は実装詳細であり、同一フレーム完成は保証しない。

**フレーム更新入口（2026-09-21）。**上の「一描画フレーム内で複数回進める」を実際に行う入口を Runtime に置いた。`SharedWorkFrame.Update(frameId)` は、frame を開いた後、

完了回収 → Main側の結果処理・資源返却・Commit・依存解消 → 新たなReady Workの投入

を**進捗がある限り**繰り返す。参加者は自分のMain側処理を持つもの（`IMainThreadPump`）で、現時点では表示Geometryの切断runner、Cut DAG、Physics Cut/Cook が実装する。**当時は Dispatch を呼ぶ製品コードが一つも無く**、回収の後に準備できたWorkは呼出し順の都合で翌フレームへ送られていた。

- **進捗の定義。**実際の状態遷移・完了回収・予約の取得／返却・実投入だけを進捗とする。Pendingのまま、予約拒否、満杯Queueへの再提示は進捗ではない。進捗がなければその機会を終える。
- **予算。**`BeginFrame` は frame ID が変わらなければ補充しないので、同一フレームの全機会——同じ frame ID での再度の `Update` を含む——が**同じ残予算**を共有する。予算で制限するのは従来どおり**投入と回収の機会数**であり、Main側の結果処理そのものを別途数える予算は作らない。**予算切れはそれ自体が終了条件である。**残予算が無くなった機会でも参加者のpumpは一度行い——最後の予算で得た回収結果を持ち越さず処理し、握っている資源を返すため——その後に戻る。Main側だけが動いていることは、予算が尽きた後の追加機会の理由にしない。次の機会は回収も投入もできないからである。残りは次フレームへ持ち越す。
- **親子依存。**Cut DAGは、nodeが動いた周ごとに切断runnerを**もう一度**pumpする。親Commitで作った子のRequestが、同じ呼出しの中で予約を得て投入されるためである。外側のDispatch回数だけを増やしても、子が内部pumpを通らなければ解消しない。
- **何も出ていない機会。**投入中のWorkが1件も無い機会は、どの実行先にも完了を照会しない。実行先が返し得るWorkはすべてこのDispatchで受理され、同じ手順で投入記録に載り、回収されるまで残るので、記録が空ならどの実行先もこちらのWorkを持っていない。**「前回何も返らなかった」は理由にしない**——投入記録が残り、予算がある間は従来どおり実行先を順に照会する（その間にWorkerが完了し得るため）。**予算切れで照会を止めるのは従来どおりであり、記録が空になった後の後続実行先への照会を省くのが今回の変更である**（基準にはこの条件が無く、予算が残れば照会を続けていた）。記録を空にした当の実行先は自分の回収ループを最後まで回り、もう一度照会して「無い」と答えられる。省くのはその後続の実行先への照会である。同様に、待機Workが1件も無い機会はどの実行先にも投入を申し出ない。待機列は回収の**後に**読むので、Collectのcallbackが差し出したWorkは従来どおり同じ機会で投入される。終了（`Shutdown`）の回収は記録によらず必ず全実行先へ照会する。
- **待たない。**未完了Workの同期待ち・強制Complete・sleep・spin・busy pollingは通常駆動に無い。実行先が満杯、Queueに空きが無い、予算が尽きた——いずれもその周回を終える理由であり、待つ理由ではない。最後の回収機会より後に完了したWorkは次フレームへ持ち越す。
- **再入禁止。**`Update` の中から `Update` は呼べない。CollectやCommitのコールバックから呼び戻すことは、既存のDispatch再入禁止と同じ理由で拒否する。

**構成根は未接続である。**この入口を呼ぶScene・Playerの駆動は無く、`Update` の呼出し元は現時点で試験だけである。入口を追加したことは自動駆動の完成を意味しない。

待機、Queue投入済み・実行中の入力／scratch／output、完了未回収の結果をboundedに保つ。下位Workだけでurgentの待機・投入余地を使い切らず、外部WorkをUnity workerの可用枠へ数えない。共有メモリ・Bake枠等の実資源制限は維持する。Queue満杯では新規投入を成立させず、受付済み切断Workは既存Pending／DAGへ未投入で保持し、後続Dispatchへ委ねる。未受付要求は既存受付・見送り規則に従う。満杯を理由とする受付済みWorkの取消、追出し、同期救済、その場の空き待ち・反復再投入を行わない。

不要な未投入Workは既存authorityと利用者に従って取消可能とする。投入済みWorkは強制中断せず、不要化・失効しても資源を早期解放せず、完了後に8章の採否・回収へ送る。Queue内の未開始Workの除去は必須にせず、実装する場合だけ実行との競合なく一度だけ終端・回収する。混雑回避のために正常な受付済みWorkを破棄しない。

**命中後の実行先。** 有効な同じ入力・採用面の投入済み投機は、Queue待ちを含め元の実行先で継続する。命中時点で未投入のWork、およびそこから新たにReadyになる後続Workは、Physicsならurgent Unity Job、共用GeometryならGeometry Poolへ分類する。例えばBackgroundの数値処理を継続しても、後続の未投入Bakeはurgentへ送る。投入済みWorkの昇格・移送・重複発行や先着競争は行わない。同じ必要成果物の到着待ちは許容するが、無関係な下位WorkやBatch全体の完了をurgentの前提にしない。入力・面が不一致の投機は既存規則で不採用とする。

完了通知は書込み終了後に安全に渡し、Main回収から既存DAG・アロケータ・採否へ接続する。低優先Workの新規投入条件で回収を止めず、有限の完了経路でも通知・資源所有を取りこぼさない。Unity Jobは完了確認後の必要なCompleteで所有権を戻し、外部Workは書込みと利用の終了を確認する。同期・通知形式は実装詳細とし、依存を投入前に解消して外部workerを依存待ちや長い共有lockで塞ぐことを標準にしない。

実行先は公開条件を変更しない。PhysicsはSource Final Physics・採用面・必要Anchor等から進み、表示Geometry生成を待たない。Geometry Kernelは直前祖先Geometry Commitを待ち、同じ更新内の祖先順Commitを許す。CPU内部Published、GPU転送、Gameplay公開を同一視しない。未来用非同期ベイクも入力準備から既存DAGとMain予算へ含め、候補全件を同期ベイクしてから切断Workだけを投入しない。

通常停止では新規投入を閉じ、未開始Workを一度だけ処理または取消し、実行中Workの利用終了とworker停止を確認してから資源を解放する。通常フレームの同期救済には使わず、共通Player終了の全Work／回収を待たない例外は維持する。Trace停止は21.16に従う。

実装状況（2026-09-20追記。上の本文は変更しない）。切断WorkのGeometry Poolでの実行とMainでの完了回収、およびそれを使う切断DAGは実装済みである。分担・停止手順・成立範囲は4.5.6の「実装状況」による。

既存Profiler／TraceとTaskIdで投入・実行・完了・採否・公開を相関させ、片側No-opと混雑見送りは既存CutNoOpCount／CutAdmissionSkippedCountで区別する。Queue・Batch・容量配分・予算・通知の内部形式は固定せず、別DAG・汎用Scheduler・互換層を追加しない。Managed allocationとGC停止は許容するが、Trace Writer等の局所的な制約は緩めない。

### 4.5 実行時表示Geometryと描画段階

Windows PCVR／Unity 6.3 LTS／URPで、メインスレッドの直列処理と同期待ちを減らすため、通常Mesh表示からVertex Pulling（VP）へ移行する。draw call数の最小化自体を目的にせず、照明はグローバルな並行光源1つ＋ambientとする。表現・変換・メモリ管理・描画発行をPhase 0.9～0.94で先に成立させ、切断・Cap・物理・未来予測は後続Phaseで接続する。

#### 4.5.1 表現・正本・Geometry参照

アセット読み込み時の表示表現はUnity Meshとし、同形状のInstanceはMeshを共有する。即切断と実切断後の表示／StencilはVPを使い、通常命中の同期ベイク・Mesh→VP変換と、未来予測用WorkからのVP入力生成は4.5.2に従う。有効な変換済み入力は再利用する。切断生成破片はUnity Meshへ戻さず、スキニング中の通常描画はSkinnedMeshRendererに残す。一時ベイクMeshと元Mesh由来のskinning入力は独立した切断正本ではない。

VPはグローバルVertex／Index Buffer方式、VertexはAoSとする。CPU側VP表現を切断Geometryの正本、GPU側を描画用の実質的コピーとし、Geometry参照とInstanceのTransform／frame写像等を分離する。同じ現在GeometryについてUnity MeshまたはCPU側VPとは別に、並行して更新・切断する権威メッシュを持たない。表示とStencilは一度の切断結果から同じ面集合・属性・Topologyを使う。GPUコピー、Topology Metadata、切断作業構造・Cache、非同期用旧世代、不変の投機Snapshot、未採用VP入力・出力は許容する。元共有Meshも他の未切断Instanceが使う限り保持でき、Physics Convexは独立表現のままとする。

VPのGeometry参照はグローバルIndex範囲等で表し、複数参照による選択描画と、同じGeometryを別Transformで描くInstanceを扱う。幾何処理に必要なTopology情報を保持するが、内部表現をVP共通型として固定せず、通常切断のための全島列挙や島別の恒久ID・物理所有者・描画範囲構築を要求しない。通常切断の出力配置は4.5.6、追加空間分割での範囲内部の面配分は7.9に従う。

Geometry参照、正負集合を表すLogicalFragment、物理所有単位、描画要求を同一視しない。別Topologyであることだけで別LogicalFragment／Rigidbody／drawを要求せず、通常の物理出力と所属は7.2.1／7.6に従う。

AoSの属性集合・strideは各実装時点で固定し、属性追加時に変換・切断属性処理・shaderを更新できる境界を残す。永久固定のlayout、実行中schema変更、複数layout共存、旧Bufferの無停止移行を要求しない。

Compact16uv移行ではCPU永続VP頂点・切断予約出力・GPU頂点を同じ16Bにする。layoutは`position float3`（offset 0）、`normal oct8x2/sbyte`（offset 12）、`UV uint8x2`（offset 14）。UVは`(index+0.5)/256`へ復号する。CPU32Bの並行常駐やMain上のupload前再packを置かず、切断Workerは必要属性だけ局所floatへ復号して補間し、新規頂点を予約済み16Bへ直接書く。位置とtopologyの精度・識別は変更しない。Worker計算増よりMain費用と頂点memory削減を優先する（benchmark U7）。GPU転送・schedule・回収はMainに残り、全体Main費用0や全memory半減を意味しない。

入力UVの対応域は有限な`[0,1]`とし、両端を含め最近傍のbyte centreへ量子化する。import端点の丸め余白だけは`2.3841858e-7`（1における2 ULP）まで許容する。Unity標準Sphereの`1.00000012`をこれで扱う。BottomHalfUVアセットの中心値は正確に保持する。余白を超える域外／非有限UV、非有限位置、非有限／zero normalは入力準備で拒否し、wrapや制限のないclampを行わない。normalは単位方向としてoct量子化し、長さは保持しない。再切断の累積属性誤差は許容するが、位置・index・topology差の許容へ流用しない。移植状況・テスト証拠は`docs/diagnostics/compact16uv-migration/`を参照する。

同一binaryの転送境界比較では、代表Megacityの切断前／3回再切断後の同じ属性を16Bと復号32Bに置き、同じ要素容量・転送範囲でSetData費用を測る。結果は`docs/diagnostics/compact16uv-upload/`。大転送で16B側のCPU費用低下と頂点capacityの半減を確認したが、少量の新規tailでは差が小さい。これはGPU非消費bufferへの転送診断であり、製品scene全体のLegacy32比較、元float属性の切断比較、GPU residency、growth／retirement peakとは分ける。比較用32Bコピーは明示diagnostic実行時だけ作り、製品経路へ並行常駐を追加しない。

#### 4.5.2 準備と表示採用

製品sceneの同条件比較は`docs/diagnostics/compact16uv-scene-ab/`に記録する。診断専用32B／通常16Bの2 Playerで、同じ64×64分割box、物理convex、容量、切断列、commit後固定poseを使い、driver Update／LateUpdateとcamera準備・描画登録のMain wall時間を取得する。6独立processで位置・index hashと3組の最終画像が一致し、定常Mainはほぼ同等（再切断後32B 21.2／16B 21.1 µs）。Unity割当のframe末観測peak中央値は142.280→135.127 MiBとなった。切断時の時間はrun間変動が大きく一般的な高速化とはしない。これはdense合成sceneの製品経路検証であり、実asset物理／skin、GPU residency、grow／retirement最悪peak、XRの証拠へ流用しない。診断用32B compiler定義は通常worktreeから撤去し、製品release前に追加shader比較variantのstrip／撤去を確認する。

計測上、Unity 6000.3.22f1のIL2CPP実装では`GC.GetAllocatedBytesForCurrentThread`が未実装で0を返すため、これをmanaged allocationなしの証拠にしない。過去upload／appearanceの0は未測定へ訂正し、scene追試ではUnityのframe全体GC counterを別項目にする。frame全体のGC量を製品scopeだけへ帰属させない。

Compact16uvのStatic入力はEditor/offlineで一度だけ16Bへ変換し、`VpStatic16File`からCPU storageへ登録できる。ファイル内indexはmesh-local UInt32とし、配置先のglobal offsetへ登録時に一度だけrebasingする。topology IDとmaterial対応はauthoring由来で保持し、位置weldで生成しない。登録時の16B一時配列・入力gate・storage copyは残るが、定常frameでoct/UV encodeやfloat32 Mesh展開を行わず、source配列を永続cacheとして保持しない。skin／blend shapeをStaticとして固定姿勢化する代替経路にはしない。現段階は代表Megacityの取り込み境界であり、既存Sceneの自動置換ではない（`docs/diagnostics/compact16uv-intake/`）。

代表Megacityの実物理入力は、表示に使った同じBottomHalfUV `.blend`からauthoring済みUCXを取り出す。移動後の最新版と固定入力のSHA一致を確認し、owner local→import済みMesh localの明示変換と既存topology対応で全表示頂点・全三角形を照合する。古いPhase 0.21の別SHAの物理JSONや合成boxを代用しない。24頂点44面のconvex・21 Anchorを非公開fixtureに固定し、同一3平面でのconvex Job切断／再切断、BakeMesh呼出し、16B表示storage切断は単体検証済み（`docs/diagnostics/compact16uv-physics-intake/`）。Anchorは抽出のみで登録動作は未検証。これは製品sceneのowner登録／commit／表示追従／寿命、PhysX内部形状の同一性、Legacy32性能比較、キャラクター現在poseの合格を意味しない。

実Megacityの製品scene接続は別途`docs/diagnostics/compact16uv-megacity-scene/`に記録する。同じUCX＋Static16を専用private sceneへ登録し、3材質slot・共通actor frame（unit scale、Mesh Z-up→World Y-up）で通常のroot／driver／Worker／Cook／commit／cameraを通す。建物本体の横切断とpositive child再切断を独立3 processで確認し、geometry fault 0、頂点数5,326→6,078→6,974、正常drain／display破棄／atlas解除、7場面の画像再現を確認した。commit後kinematic固定と明示移動による観察であり、Anchor固定・自由落下の物理応答・元prefab配置の再現ではない。登録入力のTextAssetはこのfixtureでは常駐する。Main／memoryは16B単独の参考sampleに留め、Legacy32差・peak削減・現在poseキャラクターやXRの証拠にしない。

実MegacityのLegacy32／16B比較は`docs/diagnostics/compact16uv-megacity-ab/`。通常容量65,536頂点・262,144 indexで各3 processを測定し、同じStatic16由来の初期復号属性と全3 checkpointの位置／index hashが一致した。32Bは元float assetでなく同じ量子化入力の復号を保持する対照で、新規属性は16Bだけ再量子化する。定常Main scopeの再切断後中央値は32B 20.90／16B 22.65 µs、範囲は重なり、一般的な高速化や費用増0を主張しない。頂点capacityはCPUとlogical GPU bufferで各2→1 MiBだが、Unity割当の観測peak中央値は118.343→119.662 MiBであり、この小規模sceneの全体peak削減は確認できなかった。最終画像差は0.225%のpixel・最大5/255、debug cap maskは完全一致。容量節約と実経路成立を支持する結果として保持し、dense合成sceneのpeak削減をこの実sceneへ転用しない。一時32B定義を撤去後、通常16BのEditMode 3,614件が合格した。

通常命中で即切断開始に必要なSkinned入力は、現在の実Bone Poseを使う同期`SkinnedMeshRenderer.BakeMesh(mesh, useScale: true)`→CPUデータ取得・VP変換を基本経路とする。未来予測用に限り、Phase 4.65で`ResolvedAnimationPoseInput`→19.3の不変Rig Pose→4.3のBurst数値処理による線形ブレンドスキニング→共通CPU側VP入力を限定実装し、同期経路との比較から導入の採否を決める。目的は複数候補の頂点処理と同期待ちをMain Threadへ集中させないことであり、ベイク総時間の短縮や負荷ゼロを要求しない。非スキニング対象はベイクを省き、必要な形状・姿勢で準備済みなら再利用する。

Skinned対象の切断前の共通VP入力は、元SkinnedMeshRendererのTransformを基準とするlocal空間とし、Root Bone localやWorld空間を混在させない。同期経路と比較基準のSkinnedMeshRenderer.BakeMeshはともに`useScale: true`へ固定する。Unity 6.3のこの引数はRenderer Transformのscaleを補償する指定である。非同期経路はRoot Boneを含む骨Poseと対応するbindposeをRenderer基準へ変換したskinning変換で骨由来のscaleを反映し、bindposeやRoot Boneのscaleを別途重ね掛けしない。Rendererおよび祖先のobject scaleは、VPからWorldへの既存Transform／frame写像で一度だけ適用し、VP頂点へ追加で焼き込まない。切断面も同じ入力空間へ写し、切断後の物理frameへの配置・表示追従は4.5.6に従う。

2026-09-24の現在pose component検証により、以前の`useScale: false`指定を訂正した（`docs/diagnostics/compact16uv-current-pose/`）。Casual／Professionalの固定入力は最新版SHAと一致し、基準pose・骨回転・祖先非一様scale・root bone非一様scaleの計8条件で、`true`の出力が全weightのRenderer-local行列oracleに一致し、16B storageへの登録と各2回の切断が通った。`false`はProfessionalの元Renderer scale 0.01や祖先scale付きCasualで不一致となり、丸め誤差として許容しない。旧benchmarkの`false`を基準にした結果はその出力空間での履歴として残し、Renderer-local統合の証拠へ転用しない。本検証はEditorのcomponent単体であり、製品sceneの現在pose取込み、骨Physics Proxyとの共通frame、表示／Stencil、IL2CPP、性能、未来非同期skinの採用を完了扱いにしない。

Phase 4.65は限定実装・品質と負荷の比較・人間による採否決定までを必須とし、効果がなければ導入見送りを正常な完了結果とする。採用時だけ本体の既存DAG／VPプールへ接続し、以下の非同期経路の本体契約、Phase 4.71の未来VP準備、Phase 4.72の人形先行切断統合を適用する。不採用時はこれらを必須範囲から外し、4.70の軌道・Animation計画と4.52の現在Pose同期切断を残す。人形の先行準備による命中時負荷削減を必達にせず、準備費用・表示開始は本節の通常同期経路に従う。採否は開発時の判断であり、実行時の自動切替・再挑戦や代替の未来Pose同期ベイクを追加しない。判断と理由を本書へ記録し、不採用となった本体設計は削除してGit履歴へ残せる。

未来用Workは元Mesh由来の不変な頂点属性・weight・bindpose・骨対応を読み込み・登録時などに準備して共有し、候補ごとの取出し・再構築を避ける。共通VP用AoSへ直接出力するか、一時Native出力からWork側で同じ形式へ変換する。ベイク済みUnity Meshの生成・再読取りを挟む義務はなく、頂点数に比例する変換・コピーをMain Threadへ戻さない。Work分割、並列度、入力layout、型・field列、初期対応Asset・変形機能は実装と代表入力の比較で選ぶ。

Pose評価と頂点スキニングを分け、現在Sceneを未来Poseへ変更しない。Table数値評価と現在骨への適用は19.3に従い、ProbeのTransform収集方式を必須にしない。同じRig Poseから表示側非同期ベイクと骨Physics Proxyの姿勢化へ分岐し、必要なPhysics Proxy・Anchor・frame等の入力依存は維持するが、描画頂点ベイクだけを理由に物理を待たせない。

VP入力準備と実切断出力を区別し、後者の正負直接配置・転送は4.5.6に従う。入力Pose・sourceデータは読者の寿命まで不変とし、Work出力は4.5.3の範囲所有権と既存の世代・回収規則へ接続する。未完了Workを翌フレーム固定で強制完了せず、完了した仕事を回収する。ベイク完了をGPU転送・表示Commitの自動トリガーにせず、準備中は現在表示を維持する。

SkinnedMeshRenderer.BakeMeshとのbit単位一致は要求せず、同じ入力Poseと本節のRenderer local／scale規約、有効weight条件で必要属性の誤差と見た目を少数の代表入力で確認する。Qualityによるweight制限の違いや未対応BlendShapeを丸め誤差として扱わず、初期対応範囲を限定できる。非同期経路非対応のweight数・BlendShape・scale構成では未来Workの準備を行わず、実命中時に現在Poseの既存同期経路を使う。非同期経路非対応であること自体を異常扱いせず、未来Pose用の同期ベイクや新しい救済経路は追加しない。6.2のcanonical posed position共有、必要属性のfinite性、Topology対応、表示／Stencil／再切断の共用Geometry契約は維持する。参考測定の誤差値を固定上限や実行時の全頂点比較へ転記しない。

実命中時は19章／20.5の既存世代・予測前提・Pose／面条件で準備済み表現・切断成果物を採用する。採用検証のためだけに毎回同期SkinnedMeshRenderer.BakeMeshを追加せず、有効な準備済みVP入力・切断成果物を再利用する。準備が未完成または不採用なら、即時表示に必要な入力は現在Poseの同期経路で準備し、そのために投機Workを強制完了しない。後着結果は既存規則で採否・回収し、異なる入力Poseの結果を混用しない。初期は即切断開始に必要なCPUベイク・VP変換・転送を命中を処理するフレーム内に収め、同フレームの描画から分離を見せることを目標とする。未準備なら必要な準備後に表示を開始する。表示開始時に残る準備費用を負担するが、この遅延許容を複数フレーム分割の実装要求にはしない。実測前に全対象の同フレーム表示を保証せず、4.5.4の容量拡張時停止は既存の許容として区別する。幾何切断完了より先に仮表示する原則は維持する。

転送は同フレームの描画が更新データを使える順序で発行し、この目標のためにMain ThreadへGPU完了待ちを追加しない。転送発行時間だけを表示開始時間とみなさず、代表入力の準備費用、実際の表示開始フレーム、フレーム全体の90fps目標との両立を既存計測で確認する。

Meshからデータを取得する初期経路はCPU-readableを前提とし、AcquireReadOnlyMeshData等で不要な取出しコピーを抑える。このAPIはAoS変換・GPU転送まで省略せず、Snapshot保持中の元Mesh変更ではコピーが発生し得る。SkinnedMeshRenderer.BakeMesh自体は同期CPU処理であり、自動的な背景Jobとみなさない。その結果は通常Mesh入力として変換へ渡せるが、未来用Work出力にMesh経由を要求しない。0.92だけのためにPose Evaluatorや人形切断を完成させない。

#### 4.5.3 CPUプール・範囲所有権

CPUのVertex／Indexはそれぞれ単一の大きな線形領域とし、Mainの専用アロケータが入力参照寿命と出力予約を所有する。Workへ渡すNativeArray view／unsafe pointerの形状は実装詳細とし、view全域とアクセス許可範囲を区別する。Unity JobでVPプールを扱う場合はNativeDisableContainerSafetyRestrictionでcontainer単位の粗い依存判定を外せるが、外部Workを含む範囲所有と利用終了を省略しない。

| 範囲の区分 | 許可するアクセス |
| --- | --- |
| Free | 割当可能 |
| Reserved | 所有Workだけが読み書きする出力予約。他Work・転送処理は触れない |
| Published | 書込み完了後に公開した読取り専用範囲。複数読者が参照可能。再利用可能なPublished IBの一時読取りにはRead Leaseが必要 |
| Retiring | 退役が確定した範囲。新しいRead Leaseを取得できず、取得済みのRead Leaseだけが読取りを継続できる。全Lease返却後にFreeへ移る |

VPプールへのWorkアクセスは内部Published入力と自分のReserved出力に限る。同一Geometry Work内部の段階間所有権移管はWork完了回収後に行い、共有書込みを許可しない。実使用部分を内部Publishedにし、未使用予約を解除する。切断間の入力は直前祖先のCommitted Geometryに限り、未Commit成果物を後続切断へ公開しない。Publishedはアロケータ内部の読取り許可であり、Geometry Commit・GPU転送・Gameplay／Trace状態を表さない。安全属性の解除でWork完了や実データ依存を省略しない。

再利用可能なPublished IBを一時的に読む読者は、IBの範囲単位のRead Leaseを保持する。Read LeaseはPublished状態の範囲からだけ取得でき、同じ範囲へ複数の読者が同時に取得できる。CPU処理、外部Work／Unity Job、GPU転送元としての一時読取りを保護する。退役要求で範囲はRetiringへ移り、新しい取得を拒否する。既存のLeaseがすべて一度ずつ返却された後だけFreeにし、再利用する。二重返却、範囲の再利用後に届いた古いLease、Retiring／Freeからの取得は不正として検出する。Leaseの取得、Retiringへの遷移、Work完了回収後の返却、Freeへの遷移はメインスレッドのアロケータが直列に管理し、Workerは範囲の状態を直接変更しない。

数値Kernelの入出力は6.1に従う。新規Vertexは一つの連続予約の先頭から実使用部分を詰めて出力し、継承Vertexは既存番号を参照する。新規Indexは4.5.6の一つの連続予約へ正側、負側の順で出力する。属性seamやCapのために必要な新規Render Vertexはこの新規Vertex出力へ含める。表示切断に厳密Count→確保→Writeを必須にせず、出力予約不足では範囲外へ書く前に容量不足で終了し、部分成果物を公開せず再予約・再実行できる。7.9の新Index領域も同じ予約規則を使う。形状不正・世代不一致の救済や切断受付の再試行へ広げず、範囲外書込み後の例外回復を設けない。

Published Vertexは保持中に上書き・移動せず、子から既存番号で継承参照する。移動はInstance Transform／frame写像で扱い、継承のためだけに全頂点を複製・書換えしない。Published済みVBの回収・再利用と、そのために追加する所有・利用管理は任意Phase 7.1で導入する。それ以前は追記・保持のままでよく、前段Phaseの実装・完了条件にしない。

導入時のVBは、ObjectIdが所有する確保範囲を当該ObjectIdの生存LogicalFragmentがなくなるまで保守的に保持し、その後に既存の資源寿命と予算に従って回収する方式でよい。全Fragment終了は親子置換等の一体公開後の状態で判断する。一部Fragmentの生存によって不要Vertexを含む範囲が残ることを許容し、Fragment別・頂点別の早期回収や全範囲の同一Frame解放を要求しない。

VBにはGeometry range単位のRead LeaseをPhase 0.93で導入しない。Geometryが参照するVertex集合は継承参照により不連続になり得るため、Published Vertexは既存どおりObjectId単位で保守的に保持する。将来VBを回収するときは、当該ObjectIdの生存LogicalFragmentがなく、未完了Work・CPU読者・転送元利用も終了したことをObjectId単位で保証してからFreeにする。

VB／IBは各回収単位の正本参照と必要な利用が終了してから一度だけFreeし、再利用する。未実行Workを含む入力保持・出力予約、CPU／Work／転送元読者、他の生存利用者を保護する。IBは既存の範囲別回収を維持し、ObjectId全体の終了待ちにしない。所有範囲・利用状況の管理、回収待ちの保持・確認・分割方法、およびVB／IB間の実装共用は実装詳細とする。

CPU再利用自体はGPU Bufferを変更せず、GPU完了待ちをCPU解放条件にしない。Read LeaseはCPU・Work・転送元の一時読取りだけを保護し、提出済みdrawのGPU完了を表さない。Leaseの返却のためにGPU完了待ちを追加しない。対応GPU offset更新と旧描画利用とのhazardはVP転送／Renderer内部で処理し、再利用したoffsetの新内容を旧内容の転送済み判定で省略しない。成果物別GPU部分rangeのallocate／freeを設けず、Geometry参照とRenderer登録は一度だけ退役する。旧GPU Buffer全体の解放は4.5.4に従う。

0.93の再利用対象は主に退役Indexと、Vertex／Indexの未使用・失敗予約であり、これらの回収をPhase 7.1やObjectId全体の終了待ちへ延期しない。未公開予約の回収は初期のVertex追記方針に反しない。単純な空き領域のbest-effort再利用でよく、最適配置、断片化解消、コンパクション、Buffer縮小やCPUページのOS返却を要求しない。表示Instance／Geometry参照の退役は生存物体の寿命Policyとは別であり、0.93へ7.9／7.10のPolicyを前倒ししない。

#### 4.5.4 CPU仮想予約・GPU容量と転送

本節のPlayer終了対象は、初期化または受付済みの有効な仕事に必要なCPU／GPUのbacking容量を、設定済み絶対上限超過、VirtualAlloc／page commit失敗、GPU／device／API上限超過または確保失敗により成立させられない場合に限る。上限に達した事実だけでは終了しない。Queue満杯は既存の受付・待機規則、per-cut出力範囲の予約不足は4.5.3の非公開終了・再予約・再実行に従う。

CPUはWindows x64のVirtualAllocで大きな仮想アドレス領域を予約し、利用前に必要ページをcommitする方式を基本案とする。予約内では基底アドレスを動かさない。16 GiB等は仮想予約量の例であり、既定容量・初期物理使用量・必須試験規模にしない。仮想予約成功は後続page commit成功を保証しない。本節のCPU backing容量不成立は4章の共通Player終了に従う。

頂点番号は32bitとし、全VBはNativeArray<Vertex>、全IBはNativeArray<uint>のviewを使える。各viewの要素数はint.MaxValue以下、byte数・offset演算は64bitとし、要素数とbyte上限を混同しない。32bit頂点番号を理由にVBを4 GiBへ制限しない。外部所有領域のviewにはConvertExistingDataToNativeArrayとAllocator.None等を使い、分割プールや64bit頂点番号を要求しない。

GPUはPhase 0.92で採用する固定設定の初期容量を確保し、不足時に大きなBufferを作り必要データを移す。新内容を利用できる準備・依存順序を整えてから描画境界で参照を切り替える。旧Bufferを使う描画登録を更新または退役し、旧Bufferを参照する投入済みGPU処理の終了後に一度だけ解放する。Fence方式や新しい状態体系は固定せず、通常更新でのGPU完了待ちを要求しない。この再確保・コピー・切替に伴うSTWを許容し、本節のGPU backing容量不成立は4章の共通Player終了に従う。GPU単一Buffer上限と新旧Bufferの一時共存を考慮し、CPU固定予約とGPU再確保を混同せず、GPUが必ず先に尽きると仮定しない。無停止回復・縮退描画を追加しない。

実切断出力の転送時期と未転送の継承Vertexは4.5.6に従う。転送は追加・変更範囲を対象とし、安全にまとめられる更新をまとめてSetData回数を減らす。別WorkのReserved範囲や未commit CPUページをまとめ読みせず、CPU／GPU offset対応を保つ。スパース配置自体を失敗にしない。Graphics.CopyBufferは元・先の総byte数一致を要求するため、そのまま容量拡張に使わない。初期はSTW中のCPU正本からの必要範囲再転送、または単純なGPU範囲コピーを選び、両方のFallback実装を要求しない。

停止・終了の許容は本プールの容量処理に限定し、通常Mesh／VP混在のPass境界へメインスレッドからのGPU完了待ちを追加しない。使用中領域・参照の保護は維持し、巨大stressではなく小容量と少数Jobで拡張・不足・再利用を確認する。

#### 4.5.5 描画Stageと採用判断

通常MeshとVPを同じURP描画へ参加させ、必要なColor／Depth／Shadow順序とリソース依存を設定する。頂点取得・描画命令生成を切断Kernelから分離し、切断Clip／Stencilの接続はPhase 1以降で行う。

| Stage | 構成 | 目的・導入 |
| --- | --- | --- |
| 1 | Direct・非indexed draw＋shader-side indexing＋属性Pulling | 基本表示・Meshからの移行。Phase 0.92。DirectはMeshRendererを意味しない |
| 2 | Indirect・非indexed draw＋shader-side indexing＋属性Pulling | 描画要求集約・引数管理・個別発行を見直す。Phase 0.94でPhase 1前に実装・比較する |
| 3 | Indexed Indirect＋属性Pulling | hardware vertex reuse。Phase 0.94時点では実測に応じた後段最適化とし、D-178により現在の採用経路とする |

**現在の採用経路はStage 3とする（D-178、2026-09-18の人間判断）。** Phase 0.94でStage 2を採用した事実とその比較結果は過去の記録として保持し、Stage 3の成果へ書き換えない。Stage 3のStage 2に対する性能優位は未比較・未確認であり、追加のStage 2／3比較を採用条件にしない。Stage 2の実装・試験は削除せず、性能改善を断定しない。

Stage 2はAPI置換だけで改善とみなさず、実際の発行経路と主スレッドへの効果を測る。全異種Geometryの単一native draw化、頂点再利用・ベイク・転送の改善をStage 2へ要求しない。固定改善率・全Scene高速化をPhase 1着手条件にせず、効果が乏しければ人間判断でStage 1を採用したまま進める。実行時自動Fallbackは設けない。GRDは通常Mesh側の比較候補でありForward+を必要とするが、独自VP描画の集約機構とは同一視しない。

GPUカリングの責務は独自描画側に置くが、0.9xで高度な遮蔽判定・全面GPU生成を要求しない。GPU並行ベイク、Stage 3、GRD採用、コンパクション、layout汎用化、追加権威Meshを0.9xの初期完成条件にしない。Stage 3はその後D-178で採用経路となったが、これは0.9xの完成条件を変えるものではない。将来のBuffer用途選定ではD3D11のIndex＋Structured併用不可等を確認し、Index＋Raw等を検討できるが、AoS方針や初期layoutを変更する前提にはしない。

物体固有Motion Vector、XR提出・再投影の新機能、Quest単体Application SpaceWarpを0.9xへ追加しない。仕様・既存実装への必要な破壊的変更は許容するが、Capture、Fixture生成、物理、支持・世代管理の再設計を目的にしない。VP導入だけを理由にFixture形式の作り直しや旧Asset／Fixtureの一括再生成を要求しない。

#### 4.5.6 正負Indexの直接配置と転送・Commit

通常切断の新規Indexは、一つの連続予約の先頭から正側の全Index、続いて負側の全Indexを隙間なく出力する。各側の生成Capも対応する列へ含める。正負別のallocationや、物理所有者別に後から再集約する工程を設けない。

正負の実使用Index数をn0、n1、予約先頭をbaseとすると、正側は`[base, base+n0)`、負側は`[base+n0, base+n0+n1)`を参照する。片側Geometryが空ならその側の要素数は0とし、paddingやダミーIndexを置かない。末尾の未使用予約は初期化・転送せず既存アロケータで解除する。負側開始位置に必要なn0は切断内の件数計算や出力順序で決め、別Count JobとMain Thread往復を必須にしない。切断・Cap生成の作業領域と予約不足時の4.5.3の扱いは維持する。

正負のGeometryは同SideのFinal Physics Ownerへ対応付ける。Material・frame等に必要な描画分割はRenderer側で扱い、連続配置や1回の転送を全Passでの厳密1 drawと同一視しない。

継承Vertexは既存番号を使い、新規交点・Cap頂点等だけを予約範囲へ追加生成する。Triangle切断、正負への振り分け、必要な非交差IndexコピーとTopology参照更新は通常の出力生成に含める。面集合、Triangle内頂点順、属性、windingを保存し、表示・Stencil・再切断は同じIndex正本を使用する。

新規Index列は実使用範囲`[base, base+n0+n1)`だけを一度のSetDataでGPUへ送る。正負別の2回転送、未使用予約の転送、中間配置の先行転送と後の再転送を行わない。対象全体のGeometryが無変更で既存GPU Index範囲をそのまま再利用できる場合は、新規Index書込み・当該切断のIndex SetDataは0回とする。受付前No-opもこれに含む。Geometryが変わる場合や、Triangle非交差でも正負配分に新Indexが必要な場合は省略しない。

この1回／0回は通常切断の当該新規Index出力だけの転送回数である。Vertex、Descriptor、初回Mesh→VP準備、GPU容量拡張時の再転送は別とする。先行準備で転送済みの有効な出力は再利用し、命中だけを理由に再転送しない。未転送入力範囲の継承時に必要な転送は、GPU転送済み範囲の再利用による0回と区別する。

表示Geometryの外部状態変更境界はGeometry Commitだけとする。Geometry Workは共用Geometryと描画方式に依存しない参照・Topology Metadataを生成し、Color／Depth／ShadowCaster／Stencil／Culling／Indirect描画のDescriptor、Draw List、Batch、Indirect Argumentsを出力・成功条件・Commit条件に含めない。Work完了、CPU Published、GPU更新の準備・発行・完了は内部処理であり、別の公開Ready／Upload状態を作らない。

Geometry Workは受付時に登録できるが、同Branchの直前祖先Geometry Commit後のCommitted Geometryを入力としてKernelを実行する。初回は登録済み基底Geometryを使う。Geometryが内部で先に完成しても、7.1のFinal Physics／Logical Publication前にはCommitしない。出力予約不足だけは4.5.3の非公開再予約・再実行を使い、後続Physics／Logical Publicationを待たせる条件にはしない。

Geometry Commitでは正負の具体的Geometry／RenderFragment参照、frame、実在CutBoundaryRecord、実体化済みCutと残るTemporary集合を一つの整合した状態として公開し、当該GeometryStateをCommittedとする。当該CutのTemporary Clip／Capだけを外し、後続面を新基底へ付け替え、Pending CutのGeometry責務と未完了件数を一度だけ完了する。Surface BoundaryがなければRecordは0件、Geometry空SideにはRendererもダミーGeometryも作らずFinal Physics子を残す。

RendererはCommit済みGeometryとTemporary集合から経路固有Descriptorを構築し、一つの描画状態Snapshot内の全Passで、同じ採用済みSnapshot・Geometry・frame・Selected面集合を参照する。実際に適用するclip面は、本体・Depth・ShadowがSelected面集合の全面、Stencil Volumeが通常Colorではその集合のうち自身のCap面だけ、最後のColorでは全面であり、全Passで同一ではない（5.6、D-183、D-186）。CommitはDraw List生成、Render Thread命令発行、GPU転送／Draw完了、XR提出、実画面表示、次フレームを待たない。必要なGPU更新が新Geometryを読むDrawより先に実行される順序をVP転送／Rendererが保証する。収集前のCommitは当該収集、収集後のCommitは次回収集から使い、表示済み証明は要求しない。

Cut AのGeometry未完了中にA子のCut Bが物理・論理公開済みでも、Aを先にCommitして`G0 + Temporary A + Temporary B -> GA + Temporary B -> GAB`へ進む。A子が既に置換済みならGAを現在のB子孫の表示基底へ対応付け、A子をliveへ戻さない。B KernelはA Commit後に実行する。同じPlayer LoopでA Commit後にBを完了・Commitできれば続けてよく、中間GAを一度描く義務はない。中間Commitの省略、子孫による祖先の代替materialize、Geometry Transactionを作らない。

A正側だけがBで再切断されていれば、A負側はGA-へ、B枝はGA+とTemporary Bへ進む。BがAbortしてその枝が退役済みならA負側だけを表示へCommitする。全子孫が退役し、表示・後続Geometry Work・他の生存読者に不要なら履歴完成だけの計算を続けずPendingを終端・回収する。未完成Operation Traceは21章の既存Incomplete扱いとする。履歴Recordは置換済み中間Geometryを強く所有しない。

採否は8章のPending／Branch authorityで照合し、別Fragmentの世代更新や子孫切断だけで祖先Geometryを失効させない。共用Geometryの予期しない内部エラーは4章の共通Player終了へ送り、後続へ代替Geometryを推測適用しない。

実装状況（2026-09-20追記。上の目標仕様と既存の決定本文は変更しない）。実切断からGeometry Commit・表示差替えまでの最小接続を3単位で実装し、さらに採用面のframe写像を切断入力へ接続する1単位を加えた。いずれもlocal mainへ統合した。詳細はここにまとめ、関係する節からは本項を参照する。

- **非同期切断Work（773bb09。2026-09-21に一段経路へ変更、下記）。** **当時の経路は二段である。**容量問合せと切断Kernelの実行をGeometry Poolで行う（容量問合せも入力形状を走査するためMainに残さない）。入力取得（読取リース）、出力予約、結果処理と資源解放はMainに残す。完了通知は完了を記録するだけで、storageに触れる処理は次のPumpで行い、Dispatchの内側で予約取得や公開をしない。storageのcut出力予約は同時1件のままで、後続の切断は未投入で待ち、容量枯渇にも切断失敗にも読み替えない。受付終了後もdispatcherの完了回収とPumpによる排出が必要で、それを終えるまで実行中だった切断の予約・リース・結果は戻らない。同期入口は既存呼出しのために残し、予約の形・出力の指し先・容量再試行規則・側の記述と公開は両経路で共用する。
- **Cut DAGの骨組み（c97e43c）。** 受付済みOperationとその仕事・入力・結果の対応、依存が解消した仕事の投入、Mainでの完了回収、Physics／Logical PublicationとGeometry Commitそれぞれの成立条件の確認、失効・放棄・終了時の回収を担う薄い接続役である。論理親子とOperation状態の正本は既存台帳のままで、7.10の単独退役の口だけを最小で追加した。Physics／Logical PublicationはGeometryを待たず、公開済みの子は先行Geometry未完成でも再切断を受け付ける。後続のKernelは直前祖先のCommitted Geometryを読み、祖先のCPU公開出力を先取りしない。祖先Commit後は同じ更新内で後続をReadyにできる。後着成果物の採用は、Operationが公開済みであることだけでなく、その枝に生存する読み手があることと、切った基底がSourceの現在Geometryであることを照合する。空Geometryは正常な結果としてKernelを起こさずダミーも作らずに後続へ伝える。採用しない成果物のProduced側Index範囲は一度だけ退役させ、Geometryの内部エラーは呼出側へ一度だけ通知して4章の共通終了へ渡せるようにし、Physics失敗へは読み替えない。dispatcherの所有とフレーム駆動、Commitの実処理は呼出側に残す。
- **実Geometry Commitと表示差替え（0df0392）。** 切断結果の転送、正負Geometryの表示登録への差替え、実在境界の記録を一つの変更として行う。転送は追加頂点が1回、正負Indexは連続した1回のSetDataで、借用側（平面が当たらなかった側）は0回である。実体化した切断の仮clip／仮Capは、各側がその境界を自分のSideで反映済みとして登録されることで外れ、後続のTemporary面は新しい基底の上で作り直される。Snapshotが登録rootから下の分離を加算し、rootより前は配置が持つ契約（5.5）に合わせ、rootが子へ降りる分の分離は各側の配置へ取り込む（確定済みAnchor配分で自由な側だけ。固定側は動かさない）。Commitは表示の収集と分けてあり、settle済みの収集はそのまま描き、Commitは次の収集から入る。差し替えられた旧登録の参照は次の採用まで保持してから解放する。描画データの容量は差替え後で判定し、旧参照の保持は参照表自身の取得可能条件で判定する。登録枠不足は転送・登録前に判定し、この拒否では成果物の所有権を移さず、後の機会に再試行する。境界記録はCommit時点の正負Geometryと各側の配置を非所有で持ち、Cap三角形が生成された切断だけに作る（借用側・空側は0件）。反映済み集合は仮clip／仮Capを外すための情報であり、境界記録とは別物として扱う。
- **採用面のframe写像の切断入力への接続（b98661b）。** 採用面は受付時のSource Fragmentの論理frameの値であり、Kernelは実行に使うCommitted Geometryのlocalで切る。この2つを区別した。**台帳の採用面、`CutGeometryCommit`が公開する面、実在境界記録の面は受け取った値のまま保持**し、**Kernelへ渡す面だけ**をGeometry localへ変換する。変換後の値で台帳・Commit・実在境界記録の採用面を上書きしない。変換は既存の平面変換処理を、切断が実際に提供される時点で一度だけ行う。
  写像は呼出側が基底Geometryと一緒に渡し、**表示登録とDAGに同じ写像**を渡す。DAGはRendererの内部状態を読まない。基底とその写像は受付時ではなく**実行開始時**に取得するので、祖先のGeometryが未完成のうちに後続切断を受け付け・論理公開することは妨げられず、その後続は祖先のCommitが残した基底と写像で実行される。Commitは正負の各側へ基底の写像を引き継ぐ（**空側にも引き継ぐ**ので、しばらく空のままの枝でも対応が失われない）。成果物の採否は、Geometryの一致だけでなく**そのGeometryが読まれた写像との一致**でも判定する。
  **継承の成立範囲は、子のlocal frameを建て直さない現行経路である。**建て直すCommitを導入する場合の継承はその接続の責務であり、ここでは扱わない。写像を渡さない従来の入口は「論理frameとGeometry localが同一」という明示的な契約として残し、**写像が無い基底をidentityで補わない**（変換できない場合と同じく、その切断を行わない）。Ownerの現在world配置をKernelの切断面に混ぜない（表示専用Offsetとその畳み込みは撤去済みで、混ざり得る項としても存在しない）。容量再試行、入力リース、失効・放棄、成果物の所有権、空側・入力再利用の扱いは変えていない。

確認の範囲（単位ごとに分け、他単位の証拠を流用しない。静的Geometryを用いた確認である。Cut DAGとGeometry Commitの接続試験では、実Physics／cookの代わりに台帳の論理公開を駆動した）。

- 非同期切断Work（773bb09、記録：Phase3GeometryCommitのcutwork-20260920）：最終版の関連試験105/105。実際のGeometry Poolを通ることをthread識別で確認した。終了時の確認は、仕事がexecutorへ投入済みで取消不能だが`Begin()`の実行開始前という条件であり、worker実行中の終了は確かめていない。全EditModeは未実行。
- Cut DAG（c97e43c、記録：cutdag-20260920）：最終版の関連試験95/95。順序は完了通知の制御で決め、実時間の競争に頼らない。参考アセットを使った1/1は通知順序の修正より前の実行で、再実行していない。全EditModeは未実行。
- 実Geometry Commit（0df0392、記録：geocommit-20260920）：最終版の関連試験76/76。その前段の84/84と非XR画像（images6）は最後の修正より前の状態の証拠であり、再撮影も全EditModeも行っていない。
- frame写像の接続（b98661b、記録：dagframe-20260920）：関連試験221/221（Cut DAG、Geometry Commit、平面変換、配置、台帳、表示、非同期切断、入力Gate、Snapshotを含む）。追加は6件で、identity入力が変わらないこと、非恒等の写像で対照と一致すること、祖先Commit前に受付・公開された後続が引き継いだ写像で切られること、台帳とCommitの採用面が上書きされないこと、空側・入力再利用でも対応が失われないこと、Geometryと写像を取り違えないことを見る。期待する面は製品の変換処理を呼ばずにテスト内で導出した。**回転＋平行移動の対照試験が照合したのは、Kernelへ渡された入力面と、成果物の種別（produced／borrowed／empty）および各件数（頂点数・index数・Cap三角形数）である。Geometry全体が一致したという確認ではなく、頂点座標や接続情報全体の一致は確かめていない。**全EditModeは未実行。

非XRの画像（単眼、1 sample、D3D11、通常Color）は、同じ配置でのTemporary表示と実Geometry表示を比べたものである。色は完全一致の比較で、単一切断とA→BのA Commit後はいずれも262144画素すべて一致した。A・Bとも Commit後は1画素（(85,338)）だけ色が異なり、同じ画素で生Depthが4.18e-03異なる。この1画素は原因未確定として残し、正常とも、最後のColorの品質例外（5.2の品質例外8）とも扱わない。Depthは、単一切断とA Commit後の比較では完全一致で約5.5万～7.2万画素に差があり、最大差はそれぞれ2.84e-08、3.17e-08だった。A・B両Commit後は最大差4.18e-03で、1e-5を超えたのは色も異なる上記1画素だけだった。完全一致の差分と閾値別の集計は区別し、原因は未確定とする。

frame写像の接続（b98661b）の非XR比較は、上の画像とは別の証拠であり、混ぜない。非恒等の写像（論理frameからGeometry localへ`Translate(0, −0.4, 0)`、採用面`y = 0`）の1組で、**ハーネス側の面変換を介さず**、採用面をそのままDAGへ渡して**実のCut DAG → 非同期Work → 製品のGeometry Commit**を通した。条件は単眼、1 sample、D3D11、同じ配置・カメラ・材質である。Commit前後で**色と被覆は一致**した（正負そろって色の相違0/262144、被覆は両方129074で片方だけ0・0。上側だけは色0/262144、被覆94664／0・0。下側だけは色0/262144、被覆34410／0・0）。**生Depthは一致していない**：正負そろって74,460画素（最大3.837e-07）、上側だけ51,084画素（最大3.837e-07）、下側だけ23,376画素（最大4.075e-08）が相違し、いずれも1e-5を超えた画素は0である。正負双方の数値と片側別の数値は別の集計であり、混同しない。**この差の原因は未確定であり、Depthの完全一致とは書かない。**許容差も品質例外も広げていない。この比較が示すのは、ハーネスが面を変換しなくてもTemporary表示と実切断が同じ面を使うことであって、Depthが一致することではない。

参考アセット（Phase 0.21のMegacity代表、asset SHA e2f27b1a…1383681）から使ったのは、登録済みcut-physics入力のownerのworldMatrixと21個のAnchorだけで、切断したGeometryは合成の箱である。実アセットのメッシュ・Convex・rigを切断した証拠ではない。

未接続・未確認（この単位では扱わない）。動く子の配置をPhysicsから表示へ渡す接続は、その後7.2の「実装状況」の範囲で成立した（9993ece。Selected面だけで成立する非Character・直接Final経路）。**公開後もGeometry Commitまで描かれるのはSourceの表示登録である**が、その登録から作る各RenderFragmentは自分のFragmentのOwnerに追従するので、登録が残ることは追従が無いことを意味しない。Ignored集約の配置はD-187の範囲で実装済みである（f72f7d0。5.6の「実装状況」）。残るのはCharacterのskinningと骨に付いたConvexとの接続、製品全体の構成根への接続、この経路でのPlayerと性能、統合後の追加検証（frame写像の接続についても、統合後に追加の検証は行っていない）。これらが残ることは、過去に別の条件で確認済みの描画条件を未確認へ戻す意味ではなく、過去のXR証拠を今回のCommit経路の証拠に流用する根拠にもならない。Phase 3全体および関連する受入れ項目の完了は意味しない。

Phase 5.6の追加分割で必要になる面の配分、新Index領域への振り分けコピー・転送・旧範囲退役は7.9へ置く。通常切断へ全島列挙を前倒ししない。配置・転送回数は既存計測で確認し、新しいRuntime監視、品質Gate、数値SLAを追加しない。

## 5. 即時表示レンダラ

即切断と実切断後の表示／Stencilは4.5の同じVP Geometryを使用する。本章の即時表示は必要なベイク・VP変換後に開始し、有効な先行準備を再利用する。実切断CPU結果完成後も4.5.6のGeometry Commitまでは同じ規則で継続する。Cap単位compaction／部分更新の禁止はStencil描画最適化の制限であり、グローバルプールの必要な更新・拡張を禁止しない。

### 5.1 分離表示

元メッシュを論理破片ごとに描画し、各切断面の正負符号に応じてフラグメントをclipする。論理上の切断幅（Kerf）は0とし、自由破片が相対移動した結果としてのみ隙間と断面が見える。単一切断ではGeometryが存在するSideごとに最大1の論理的な描画インスタンスを持ち、両側に存在すれば2、片側だけなら1とする。Geometry未確定の間は既存の親Geometryを正負にclipして仮表示し、空判定の完了を表示開始条件にしない。確定空のSideにはダミーRendererを作らず、不要な仮表示を7.6に従って退役する。**表示だけを追加で移動させる処理は持たない。**複数切断では、論理破片が保持する半空間の組み合わせだけを描画する。

**各Sideは、それが追従する配置にそのまま描く。**表示用のOffsetは持たず、系譜加算も継承もしない。Provisional公開前は正負が同じSourceの配置でclipされるため、両者は境界を共有し、**人工的な隙間は出ない**（これを許容する）。Provisional公開後は、正負それぞれが対応する物理Ownerの配置へ追従し、見える隙間は**物理が実際に離れた結果としてのみ**生じる。Kerfは引き続き0で、面そのものを動かすことはしない。

表示側がSeparation量、その系譜加算、固定側のOffset、`IgnoredTemporaryClipBoundarySet`に属する境界のOffsetを持つという以前の規定は、表示専用Offsetの撤去にともない**撤回する**。物理側の分離Impulse、Sibling D6のanchor-offset、Anchorによる固定判定と配分は7章のまま変更しない。

**実装状況（2026-09-21追記。上の本文は変更しない）。**Provisional公開後に正負がそれぞれ対応する物理Ownerの配置へ追従するための接続を実装した。配置照会は、その描画単位を**そこへ置いている枝自身**——Fragmentと、受付済み切断と、その側——を受け取る。渡す三つ組は構造確定時に枝から settled され、Snapshotの構造再利用でも持ち越すので、毎フレームの台帳探索は増えない。Ignored集約では、配置を代表するのは**走査順で先頭の生存枝**であり（D-187）、表示記録用のside情報（集約の根とその側）は変更していない。両者は集約時に別の対象を指すため、配置照会には前者だけを渡す。成立範囲と確認の範囲は7.2の「実装状況」による。

固定Anchorの有無を仮描画の省略条件にしない。両側固定でもclip、仮Cap、実切断・Cap生成を通常どおり行う。固定側へ表示上の移動を与えないことは、どの側にも表示上の移動を与えないことに含まれる（物理側のImpulseは7章による）。最新世代や両側Anchorを理由とする簡易な支持Cullも設けない。境界の細い亀裂、輪郭線、局所的な線状Z-fighting、軽微なチラツキを薄い切断痕として許容するが、通常の正向き閉Shellの欠落や任意の面状Z-fightingへ拡張しない。

### 5.2 仮断面とステンシル

Stencilは仮断面キャップのマスク生成に使い、表示と同じ6章の共用Geometryを参照する。TemporaryRenderCapRecordSetは、Operation公開前の受付済みPending Cutと、公開後のGeometry未Commitの切断面・Sideから導出する。公開時は同じ採用面のRecordへ重複なく引き継ぎ、固定／動的による選別をしない。各Recordの共用Geometry表裏から符号付きWindingを記録し、ローカルOBBと切断面の交差から作る有限なCap Bounds Polygonを正のWinding領域だけ描く。Geometryの向きの反転、正規化、符号一様性証明を行わない。

- Clip Plane：物体を正負に分ける。隙間は物理Ownerが実際に離れたときだけ見え、clip自体は何も動かさない。D3D11では選択した切断面をRasterizerの`SV_ClipDistance`で評価する。

- Stencil：切断平面上で元物体内部に相当する範囲をマスクし、仮断面を塗る。

- 実断面Mesh：バックグラウンド処理完了後に仮断面を置換する。

即時Rendererが当該RenderFragmentへ適用するGeometry未Commitの切断面をTemporaryClipConstraintCandidateSetとする。Operation公開前は受付済みPending Cut、公開後は当該Fragmentに関係する未Commit切断履歴から導出し、祖先半空間を維持する。Cap Record集合とは役割を分け、支持による候補除外は行わない。候補、各候補のSide、Selected／Ignoredの選択状態は、描画を一つのRenderFragmentへ集約する場合も、配下の論理枝（生存LogicalFragment、またはPending Cutの片側）ごとに対応を保って保持し、RenderFragment単位の平坦な一列へまとめて失わない。

各Operationの採用面は、受付時のsource Fragmentのframeにおける値である。異なるframeの採用面を暗黙に同一座標系として扱わない。即時表示の登録は、配下の全採用面が同じ論理座標系にあることを呼出側が明示した一つの写像（Fragment Physics Frameから表示Geometryのlocalへ）を持ち、その写像を適用する範囲は、登録した根Fragmentから公開済みLogicalCutOperationとPending Cutでたどれる系譜に限る。今回のPhase 2接続は、登録した系譜内で採用面が共通の論理座標系にある入力を対象とする。子ごとのframeを扱う後続接続の要件を免除するものではない。同じ系譜を複数の登録で重複して描かないよう、登録済みの根の祖先または子孫の登録を受け付けない。登録入口が複数あっても、Snapshot構築と描画は一つの経路とする。

候補は既存のPending Cut列とLogicalCutOperation公開列を受付の古い順に走査し、未Commit祖先制約を子孫制約より必ず先に置くstable順で選ぶ。Pending CutからOperation由来Recordへ移る際も同じ受付位置を保ち、同じ切断面を重複登録しない。選択結果は候補列の先頭から最大8面のdependency-closed prefixとし、ある子孫境界を選ぶために必要な未Commit祖先境界が選択外なら、その子孫も選ばない。通常の公開処理は祖先を子孫より先に列へ追加する不変条件を持ち、復元データがこの順序を満たさない場合は新しい順へ並べ替えず、違反境界以降をIgnoredとして背景Geometry完成へ委ねる。ID値によるsortや別の優先度Metadataを追加せず、左右眼、Color、Depth、ShadowCaster、Stencil Volumeの全Passで同じ選択結果を共有する。共有するのは選択結果であり、実際に適用するclip面は、Color・Depth・ShadowCasterが全Selected面、Stencil Volumeが通常Colorでは自身のCap面だけ、最後のColorではRenderFragmentの全Selected面とする（5.6、D-183、D-186）。カメラ距離、眼、Pass、毎フレームの可視性で順序を変えない。候補追加、LogicalCutOperation公開、Geometry Commit、RenderFragmentとCutBoundaryの対応関係変更のいずれかが候補資格または依存関係を変えた場合、状態変更を公開する同じ描画更新境界で再構築する。

D3D11／Shader Model 5の即時切断は、RenderFragmentごとの`SelectedTemporaryClipPlaneSet`の上限を`TemporaryClipPlaneCapacity = 8`とし、選択した最大8面を`SV_ClipDistance`だけで評価する。Vertex Shaderから`SV_ClipDistance0/1`の合計8 componentへ出力し、未使用componentは全頂点で正の有限値とする。このShader Variantでは`SV_CullDistance`を使用しない。切断面評価用のPixel Shader `clip()`経路は設けない。面数や平面値によるMaterial、Keyword、Pass、Draw分割、可変長Buffer、動的Loop上限の増加は行わない。

dependency-closed prefixへ入らない後発Pending Cut／境界は`IgnoredTemporaryClipBoundarySet`とし、`SelectedTemporaryClipPlaneSet`へ含めず、Color／Depth／Shadowのclip制約として使わず、対応Stencil Volumeをsubmitしない。Ignored境界からは、clip制約、対応Stencil Volume、描画用Cap板のいずれも生成しない（表示専用Offsetは5.1により存在しない）。Pending Cutまたは公開済みLogicalCutOperation、Geometry状態、Logical Fragment、切断履歴、世代、点Anchor配分と所有者単位の固定／動的、共用Geometry／Convex Work、物理Commit、優先度付けは変更・破棄せず、背景Geometry Commitで正しい形状へ収束させる。無視された新しい面は一時的に即時表示されず、影もその面より前の形状となり得るが、選択済み祖先の外側にGeometryを復活させずSiblingを重ねないbounded degradationとする。このためIgnored境界では表示を分岐させず、Ignored境界で分かれる論理枝は、同じ表示元Geometry・配置・反映済み境界・frame写像を引き継ぐ同じ表示登録の中でだけ、最初のIgnored境界より手前の形状を一つのRenderFragmentとして一度だけ描く。異なるGeometry、配置、反映済み境界、frame写像を持つ対象は、選択済みprefixが同じでもまとめない。ここで比べる「配置」は**表示登録が保持する配置**であり、別々の登録を新たにまとめることはしない。同じ登録の中の枝が現在それぞれ別のOwner配置にあることは、この条件が禁じるものではなく、D-187で許容する。この一つのRenderFragmentの**基準配置は、集約の根ではなく、そのグループの走査順で先頭の生存枝について既存の配置照会が返す基準配置**を使う（D-187）。すなわちその枝のOwnerのworld変換と、Geometry local→Owner localの対応（5.6）から作った値であり、**Owner配置をそのまま描画行列にはしない**。Geometryの原点とOwnerの原点が異なる場合もこの対応で保つ。取るのは基準配置だけであり、Ignored面をclip制約・描画用Capへ加えることは引き続きしない（表示専用Offsetは5.1により存在しない）。一つの形状を一つの配置で描く以上、この表示は近似である。**次を許容する**：Ignored面が残る間、先頭枝以外の物理部分と表示の位置・向き・形状が離れること（先頭枝についても、描くのはIgnored面で切る前の形状なので、その剛体が持たない体積を伴う）。その乖離による他物体への表示上のめり込み。Geometry CommitでSelectedの範囲が進む、集約の根やグループ構成や先頭枝が変わる、あるいは集約が解けるときの瞬間的な跳びと形状変化（**集約が完全に解ける前の変化を含む**）。これらに数値上限、品質Gate、人間確認は求めない。この許容はIgnored集約の基準配置に限る。通常のSelected表示の規則と、`MaxStencilColors`の最後のColorの取扱い（品質例外8、D-185・D-186）は、これで緩めない。Ignored面による新しい開口は作らず、Selectedの祖先境界の開口は従来どおりCapで描く。祖先境界のGeometryへの反映等により、未反映のIgnored境界が選択対象へ入った場合は、同じ描画更新境界で表示の分岐を反映する。当該境界自身がGeometryへ反映された場合は、そのGeometryによる表示へ移行する。Plane overflowを理由に既存Workをcancel、再発行、同期完了してはならない。

`TemporaryRenderCapRecordSet`、Cap Bounds Polygon、StencilのColor割当て、Cap板側のDraw Listは、描画更新境界ごとに`SelectedTemporaryClipPlaneSet`に選ばれた境界だけから構築する。Stencil Volumeは、通常Colorでは5.6の可視・非空のCap仕事ごとに、選択済み境界のうちそのCap自身の面とSideだけでclipした共用Geometryをsubmitし、同じRenderFragmentの他のSelected面ではclipしない（D-183）。最後のColorでは、残りのCap仕事が属するRenderFragmentごとに、全Selected面でclipした共用Geometryを1回だけsubmitする（D-186）。候補追加、LogicalCutOperation公開、Geometry Commit等で選択結果が変わった場合は、同じ描画更新境界で全体を再構築し、既存配列からの個別除去、有効フラグ、Buffer compaction、部分更新を行わない。Ignored境界の即時断面は、背景Geometry Commitまたは選択への編入まで表示されない。通常Colorでは別のVolume GroupのResidual Stencilが到達し得るCap仕事を分離し、非互換な蓄積を同じ通常Colorへ混ぜない。通常Colorに入らないCap仕事は、5.6に従い最後のColorで旧方式により描く（D-185、D-186）。Ignored境界の断面を補うための追加Mesh生成、代替VFXは追加しない。

- Cap Bounds PolygonはOBBの12辺と切断平面を交差させ、epsilonで重複を除いた3～6頂点を平面上で並べて生成する。複数のTemporary Render Boundaryでは、SelectedTemporaryClipPlaneSet内のほかの面が定める論理破片の半空間で凸多角形clipする。未受付・Ignoredの面をこの即時描画用clip集合へ含めない。凸多角形を半空間1つでclipすると頂点は高々1増えるため、clip後の頂点数は初期断面の最大6に選択上限の最大8面を加えた最大14とし、初期断面の最大6と区別する。clipで面積が残らないPolygonは、そのCapに描く範囲がないことを表し、不可視やIgnoredとは区別する。

- 描画用Cap Recordは、描画するRenderFragmentと、選択済みの1つの境界（採用面とSide）の組とする。複数の切断面を持つのはRenderFragmentであり、各Cap Recordは引き続き1つの境界を表す。

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
8. `MaxStencilColors`の最後のColor（5.6、D-185、D-186）では、そのColorに由来する欠落、Stencil混入、余計なCap、重複、誤ったDepth／Occlusionを許容する。余計なCapのDepthが他の物体や通常Colorの結果を隠すことを含む。通常Colorの割当て規則はこの例外で緩めない。経緯：この扱いはD-183で一度廃止され、Color上限を超えるとカメラ準備を拒否していたが、D-185でその拒否を撤回した。
9. Camera内部またはNear Plane近傍では、部分Cap、欠落、余計なCap、内部面、Stencil混入、左右眼差を許容する。専用検出を要求せず、隠されない表示をD-131に従って許容する。

注1、3～6は契約内の向き構成、注2は入力拒否、注7はカウント範囲外、注8は最後のColorだけの取扱い、注9はCamera近傍だけの取扱いである。共用Geometry契約に反する入力を通常経路へ投入する許可や、通常の正向き閉Shellでの欠落・混入の一般許容へ拡張しない。仮Capの品質例外は共用Geometryの生成、検証、Commit契約を緩和しない。

### 5.3 断面色と最小デバッグ表示

通常表示では、即時仮断面とGeometry Commit済みの実Capを、共通トゥーンシェーダーの低彩度グレーで描画し、陰影段数、輪郭、ライト応答を揃える。Compact16uv対応のpalette materialでは既存palette上半分の予約slotを利用する。Texture Mapping用のCap UV展開、特殊陰影、色選択用Material／submeshは追加しない。atlas未採用materialでは既存の固定`CutSurfaceColor`経路を維持する。

Compact16uvの実Cap新規頂点はUV byte slot `(247,247)`、復号値`(247.5/256,247.5/256)`を保持する。負UV markerは廃止する。atlas経路では実cap `(247,247)`・仮cap `(239,247)`の各中心±2の5×5 patchを256² textureに置く。通常atlasは両者グレー、debug atlasは実cap緑・仮cap赤。仮capはpass固定UVとし、両capともmaterial tint／UV Transformを適用せずmip 0から色を取得する。専用Vertex field、Vertex Color、別UV channelは追加しない。

製品worktreeのatlas対応は明示的opt-inとする。`VpCutSurfaceAtlas.Bind`で共有normal/debug textureを設定し、確認済み共有materialの`_VpUsePaletteAtlas`を有効化する。`VpLogicalCutDisplay.SetCapPaletteAtlasEnabled`は所有する仮cap materialだけを有効化する。texture/materialをrendererごとに複製しない。通常surfaceはnormal atlasを参照し続け、debug switchではcap参照だけを切り替える。これによりdebug patchの粗いmipから通常surfaceへの色混入を避ける。normal/debug画像の生成はoffline、切替はカメラ群の描画登録前に行い、描画途中のper-camera差替えは契約外とする。`SetColours`は非atlas経路だけへ作用する。未opt-in materialと未binding時の従来表示を保ち、全製品sceneの導入済みとはしない。設定・画像検証範囲は`docs/diagnostics/compact16uv-atlas/`に記録する。代表Megacityの未切断・3回の切断／再切断を3視点で比較した結果は`docs/diagnostics/compact16uv-appearance/`に記録し、全material・製品scene性能・XRの確認とは分ける。

scene起動点の`CutWorldRoot`には任意のnormal/debug atlas pairを指定できる。両方未指定なら従来経路を維持する。指定時は256² pairとglobal bindingの排他的所有を要求し、不備・既存bindingとの競合はstorage確保前のauthoring errorとする。共有forward materialはofflineでopt-in済みにし、Rootはdisplay生成後にbindingと所有仮cap materialのopt-inを行う。通常終了ではdisplay破棄後に自分のbindingだけを解除し、texture assetを破棄しない。additive scene間のatlas調停機構は追加しない。専用Sandbox sceneによる起動・切断／再切断・終了と描画確認は`docs/diagnostics/compact16uv-scene/`に記録する。非表示Playerで描画0となった測定は除外し、同一binaryの可視window＋offscreen mono出力で3 Runの実描画とMain／working-set sampleを確認した。合成bodyの定常区間であり、Legacy32比較・実アセット物理scene・GPU residency・peak・XRの性能確認とは分ける。

元Assetの負UVは4.5.1の入力契約に従って拒否する。予約slotを通常surfaceへ割り当てないことはasset pipeline契約とする。UV markerは表示色の選択だけに使い、Topology、切断、物理、Hitの判定には使わない。これはD-171の旧負UV許容方針をCompact16uv移行範囲で置き換える。

デバッグ表示は全対象共通の有効／無効だけとし、描画する断面ごとに次を適用する。

| 描画対象 | 通常表示 | デバッグ表示 |
| --- | --- | --- |
| `TemporaryRenderCapRecordSet`から描く即時仮断面 | 固定グレー | 赤 |
| Geometry Commit済みの共用Geometryに含まれる実Cap | 固定グレー | 緑 |

Geometry Commit前は仮断面、Commit後は計算経路によらず実断面とする。デバッグ有効時は再切断中も既存実Capは緑、新しい仮Capは赤として共存できる。Stable移行による色解除・色保持モードは設けない。

実装状況（2026-09-20追記。上の目標仕様は変更しない）。色の選択と現行の陰影への接続は01472bbで実装し、166584cでlocal mainへ統合した。通常表示では仮断面も実Capも共通の断面色を読み、デバッグ表示では同じ一つのスイッチで仮断面が赤、実Capが緑になる。両者の違いはBase Colorの選択だけで、本体・実Cap・表示経路の仮断面は同じ陰影計算（`VpShadeSurface`）を通る。これは本体にあった現行の陰影計算を共通化したものであり、本節が定める共通トゥーンの仕上げ（陰影段数、輪郭、ライト応答）ではない。仮断面はSnapshotが持つworldの外向き法線で陰影を付けるため、同じColorに別の向きの面が入っても分かれる。色の反映時点は、仮断面がカメラ準備時、実Capが描画時であり、切り替えは対象カメラ群の準備前に行う。今回の実装では、準備済みのカメラへ色変更を自動反映する仕組みは追加していない。確認の範囲と残る未確認は5.6の「実装状況」による。

色値とデバッグ有効状態はGlobal Shader Constant、共有atlas bindingまたは既存のDraw／Fragment Descriptorで指定する。色変更だけを目的とする既存Vertex／Indexの書換え、VB／IB再生成・SetData、Geometry複製は行わない。切断成果物自体の生成・転送・Commitは従来どおり行う。

計算経路、待機、Reject／Fallback理由、Physics状態の詳細は既存Trace／Profilerで確認し、断面色へ割り当てない。断面専用の点滅・縞・縁取り、常時テキスト、固定パネル、色覚補助表示、公開schema・状態機械を追加しない。Exact RGB、色遷移Animation、表示保持時間は固定しない。

### 5.4 即時切断中のShadow Map

影はRealtime Shadow Mapを使用する。即時切断中の論理破片はShadowCaster PassでもカラーPassと同じper-instance切断平面と論理破片Sideを適用する。一方、Shadow Mapには色付き断面を描く必要がないため、Stencilによる仮断面キャップは生成せず、ShadowCasterだけを両面描画して開口の奥にある外殻裏面を遮蔽面として使用する。

この方式は、閉じた元形状に対する外部Shadowの被覆範囲を低コストで近似するが、本来は切断面キャップが書くはずの深度より奥側の外殻深度がShadow Mapへ入る場合がある。切断面が床／壁に近い場合、薄い物体、非閉形状、Self Shadow、Shadow Bias、Cascade境界等では接地影の浮きや漏れが発生し得る。即時状態の短時間近似として許容し、4.5.6のGeometry Commit後は実断面を含む閉形状と片面ShadowCasterへ戻す。

- `Cull`はper-instance属性ではなく描画状態として扱い、Shadow描画を原則としてStable片面群の`Cull Back`とPending両面群の`Cull Off`へ分ける。UnityのRenderer経路では`ShadowCastingMode.On`／`TwoSided`、専用Renderer経路では対応するShadowCaster Variantを使用する。

- 「2回」はShadow Map全体が必ず2 Draw Callだけになる意味ではない。Light、Cascade／Shadow Map Slice、Mesh、Material、Shader VariantなどのBatch単位ごとに、少なくとも片面群と両面群へ分かれるという意味とする。

- 切断平面は5.2の固定上限Instance Recordに、選択済みの面・件数と、各面へ反映済みのFragment Sideとして保持する。ShadowCasterもColor Passと同一の選択結果とSideを使い、5.2のRaster clippingとPlane overflow規則に従う。**このRecordは表示用の移動量を持たない。**切断数や平面値でMaterial、Shader Keyword、Passを増やさず、同じCull群のBatchを維持する。

- Stable Instanceをclip対応Shadow Shaderへ統合するか、一時Clip面なしのInstance専用の高速経路へ分けるかは実測で決める。全ShadowCasterを常時`Cull Off`にしてDraw群を統合する案は、裏面Raster／overdraw増加を測定せず採用しない。

### 5.5 コスト制御

- 同一物体の`TemporaryRenderCapRecordSet`件数は、固定側を含むBatch投入Cap Record 2～4枚を通常時の品質／費用上の目安とする。Geometry Commit済みRecordは数えない。この目安はRuntime制御を発生させず、各切断は通常の非同期生成・Commitに従う。対応Recordの回収は4.5.6に従う。

- Clip Plane評価数の上限と超過処理は5.2に従う。1枚のCap Polygonまたは1個のRenderFragmentが複数の未Commit半空間制約を受けるため、Cap Record件数とClip Plane件数は同義ではない。

- 選択面数と`IgnoredTemporaryClipBoundaryCount`をProfiler Counterと既存の選択対象診断へ出す。断面専用パネルは要求しない。Plane overflowによってGeometry Workの優先度、依存関係、cancel／再発行規則を変更せず、Frame内の待機や同期Commitを禁止する。

- Stencilは切断面ごとの一時作業領域として再利用し、恒久的なビット割当は行わない。

### 5.6 スクリーンスペースStencil Batch

Stencil Bufferは画面座標ごとに共有されるため、すべての即時切断物体を無条件に同じ通常Colorへ蓄積しない。Stencil処理の単位は、描画するRenderFragmentと選択済みの1つのCap境界（採用面とSide）の組である`Cap仕事`とする。通常ColorでのCap仕事のStencil Volumeは、その登録の共用GeometryをそのRenderFragmentの配置で描き、そのCap自身の面とSideだけでclipする。同じRenderFragmentの他のSelected面はVolumeのclipへ入れない。最後のColorだけは後述の旧方式とする（D-186）。他のSelected面でもclipすると、同じ破片の手前向きの開口と奥向きの開口の計数が打ち消し合い、可視Capが欠落する（D-182）。本体・Depth・ShadowはこれまでどおりRenderFragmentの全Selected面でclipし、Ignored面は発行しない。

通常Colorで1回だけ発行できるVolumeは、同じ表示登録（共用Geometry、配置、frame写像、反映済み境界）、同じCap境界（採用面の識別とSide）で、Volumeへ実際に渡すGeometry範囲、配置Transform、符号付き平面がすべて同一のCap仕事に限る。これを`Stencil Volume Group`とする。同一とは、渡す値そのものが一致することであり、epsilon比較による同一視ではない。近いが同一でない平面や配置は実際の蓄積が異なり得るため別Groupとし、Colorを共有できるかは投影競合判定で決める。他のSelected面はGroupの条件へ含めない。同じGroupのVolumeは1Colorにつき1回だけ発行し、そのGroupの全Cap仕事を同じColorで描く。可視色は5.3で共通とし、色違いによるGroup分割を行わない。6章の共通入力Gateに合格したGeometryは正、負、混合符号を分離条件にせず、入力Geometryの向きを保存して符号付き加算する。同じColorのMaskは、そのColorに発行したVolumeごとの寄与`W_i`（そのVolumeのGeometry内部の符号付き寄与）を蓄積した`sum(W_i) > 0`である。Volumeは各Groupで1回だけ発行し、同じVolumeを共有するCap仕事の件数だけWを足すことはしない。Maskは厳密な幾何Unionではなく、各寄与が正しく得られて最終値が符号判定範囲内なら`{sum(W_i) > 0} ⊆ union_i {W_i > 0}`となる。正逆相殺やColor分割によるMask差は5.2の品質例外へ限定する。

StencilはParityの`Invert`や飽和演算ではなく、共通入力Gateに合格したGeometryのFront／Back Faceに対する`IncrementWrap／DecrementWrap`からなる8bit Winding Count方式を使う。基準値を`B = 128`、符号付きWindingを`W`、格納値を`S = (B + W) mod 256`とし、各Color開始時とColor間で専用Stencil Byteの全8bitを128へ初期化する。Rect描画で初期化する場合は`Ref 128`と`Replace`を使う。Capは`S > 128`だけを描画し、Unity ShaderLabでは`Ref 128 / Comp Less / ReadMask 255`とする。Counterは`ReadMask 255 / WriteMask 255`、Cap側のStencil書込みは無効とし、`S <= 128`のsampleはColorもDepthも書かない。裏面透明という通常Color方針をStencil集計からBack Faceを除外する意味にせず、Front／Back双方を対称なclip／Depth条件で扱う。Rasterizer／Transform補正は通常Colorのfront-face規約と一致させ、Geometryの向きをPositiveへ直す補正や二重補正を行わない。

最終Windingが`-127 <= W <= 127`なら`S > 128`は`W > 0`と一致するが、この範囲の証明、検査、監視、補正は行わない。途中のWrapは正常な演算として扱い、Mesh数、Triangle数、Draw数、途中累積値の上限として解釈しない。Count容量によるBatch分割、Fallback、別形式Counterを設けず、範囲外でも同じ8bit演算と比較を続け、結果は5.2の品質例外とする。

`Residual Stencil Support`は集計後に`S != 128`となる画面領域を表す論理概念であり、実Stencilから検出・再構成・監視する対象ではない。Volume Groupの計数完了後に残留が生じ得る範囲は、そのGroupのCap面だけでOBBを切った切り詰め前の`初期断面`（最大6頂点、そのRenderFragmentの配置で置いたもの）が包む。これは計数の途中でStencilを書き換える画素全体とは異なるため、Color内で全Volumeの計数が完了する前にCapを挟まない。負残留も別の正寄与を打ち消し得るため、通常Colorの競合判定から除外しない。通常Colorでは、異なるVolume Groupの初期断面の左右眼投影をResidual Supportの保守的な重複判定に使い、重なり得る組を別Colorへ分離する。描画用Capの投影が非交差であることは、Colorを共有する根拠にしない。Geometry入力条件は5.7で上流が確定し、描画ごとに再検証しない。

`Stencil Conflict Graph`は分離が必要な対象間の関係を表す論理モデルに限る。全Graphの構築・保存、全組合せ走査、stable順、Greedy Coloring、最小彩色、異なる方式で同じColor番号を得ることを要求しない。通常Colorの左右眼分離条件、Color上限、各Color内の全Volume後に全Capを描く順序を満たす範囲で、候補検索、データ構造、Color割当て方式を実測から選ぶ。

描画するColor数は固定上限`N = MaxStencilColors >= 1`で制限する。先頭の最大N−1枠を通常Color、最後の1枠を統合用の最後のColorとして予約する（D-186）。どの通常Colorでも、異なるVolume Groupのうち、両眼の保守的な投影判定で非交差を確認できない組を別Colorへ分離する。異なるGroupでも、投影が離れていることを確認できれば同じ通常Colorを共有できる。通常Colorに入らないVolume Groupは、その全Cap仕事をまとめて最後のColorへ送り、Groupの一部だけを通常側に残さない。N=1では可視・非空の全Cap仕事が最後のColorに入る。残りがなければ、最後のColorの初期化・Volume・Capは発行しない。

最後のColorは旧方式で描く（D-185、D-186）。順序は、Stencilの128初期化、残りのCap仕事が属するRenderFragmentを重複排除して各RenderFragmentの全Selected面でclipしたVolume（Geometryの全submeshを描く一式）を1回ずつ、残りの各Cap仕事の切り詰め済み描画用Capを1回ずつ、とする。同じRenderFragmentの別のCapが通常Colorに割り当て済みでも、そのCapを最後のColorで再描画しない。初期化とVolumeはDepthを書かず、Capは現在のDepth規則で描く。Depthの保存・復元は追加しない。最後のColorに由来する誤描画は5.2の品質例外8とし、通常Colorの割当て規則は緩めない。数は、通常ColorのVolume Group数、最後のColorで描くRenderFragment数、使用Color数、最後のColorのCap数を区別する。

Color上限だけを理由に、カメラ準備と描画を拒否しない（D-185。D-183③を置換）。準備容量の不足、不正入力、資源・世代・寿命による拒否は従来どおりとする。拒否した準備では部分upload・部分描画・以前のカメラ準備結果の再使用を行わない。Snapshotと既存GPU内容は保持してよいが、その失敗した準備でRenderを許可しない。これはdisplayの永続停止とは分け、次の準備試行を認める。上限超過時の描画方式の選定（O-049）は、D-185・D-186で解決済みである。同期的な追加描画、自動的な上限拡張、上限外Colorの生成、遠距離／小画面Capの省略、代替VFX、Job優先度変更、再彩色による救済を追加しない。GPU時間はProfiler等で測定・調整する性能目標であり、厳密な実行時上限、監視制御、多段Fallbackは設けない。

- Broadphaseでは、安全Marginを含む物体OBBの左右眼投影矩形を使ってよい。重なる組だけ、各Volume Groupの初期断面を左右眼へ投影して再判定する。初期断面には、Selected面の描画用Cap Bounds Polygonを作るときに既に生成・保持した初期断面を読み取り専用で参照し、カメラ準備で作り直さない。これは保持済みの断面に用途を加えるものであり、描画されない面のために断面を生成することは引き続き行わない。可視Cap仕事だけがVolumeを発行するため、不可視のCapを理由に再判定を打ち切る扱いは使わない。初期断面が不正・欠落・非有限の場合や、Near Plane交差・`w <= 0`などで投影を確定できない場合は競合として扱い、非競合としない。どちらの判定も非交差なら安全という悲観的な証明として扱い、Raster／MSAA、頭部移動誤差を考慮してBoundsを保守的に拡張する。

- （D-183）通常ColorでのStencil Volumeの共有とColorの判定には、次項の`CapCompatibilityKey`（全Temporary BoundaryのSide・World Planeのepsilon一致）を使わず、Volume Groupの同一性と初期断面の投影競合を使う。World Plane一致epsilonは、Volumeの共有にもColorの判定にも使わない。Facing epsilon（Cap仕事の可視性）と、OBB／初期断面投影のMargin（投影競合）は引き続き使う。Ignored境界で集約したRenderFragmentのCap仕事は、集約の根となる論理Fragmentの選択済み境界だけから作る。次項は旧RenderFragment単位方式の記録として残す。

- `CapCompatibilityKey`は順序を正規化した表示対象`CutPlaneId`列とSide Maskから作り、Raw floatだけをHashの正本にしない。同じSlash由来でも、19.5.1の対象別リベースまたは対象の移動・回転によってWorld Planeが異なり得るため、現在の操作World Planeをepsilon比較し、一致しなければ別Groupへ分離する。片方だけに追加Temporary Render Boundaryがある場合も互換ではない。互換判定は、対象の反映済みを除く全Temporary Boundaryの識別・Side・現在のWorld Planeで行い、不可視を理由に条件を落とさない。Ignored境界で集約したRenderFragmentの条件列は、集約の根となる論理Fragment（最初のIgnored境界が切ったFragment）の反映済みを除く全Temporary Boundaryから作る。受付順prefixの選択のため、これらは全て選択済みとなる。符号分類とWinding容量はKeyへ含めない。

- `LogicalCutOperation`公開前の即時Capは、親`RenderFragment × Pending Cutの採用面`から導出し、確定した論理子や境界を先取りしない。公開後のキャップの幾何可視性は元Object単位ではなく、`論理破片 × 切断面`の`CapRecord`単位で判定する。同じ切断面でも正負破片の断面Normalは逆向きになるため、片側が裏向きでも反対側を自動的に省略しない。支持による描画省略を行わない。

- LogicalCutOperationは7.1.2のFinal Physicsと同じ境界で、親、正負2子、採用面とSide対応を一度だけ公開する。具体的GeometryとCutBoundaryRecordを待たず、各SideのGeometryと実Capは4.5.6のGeometry Commitで後着する。空のGeometryを補うダミーRendererは作らない。

- DirectChildCountは2とし、正負各1とする。CutBoundaryCountは実在Boundaryの件数と一致させ、0件も正常とする。IDは0を予約した正の32bit intとし、CutOperationId／LogicalFragmentLocalId／CutBoundaryLocalIdはObjectIdの寿命中に種別ごとに非再利用とする。ParentObjectGenerationはuint全域とし入力Snapshotと一致させる。親・子・境界のIDは種別内で重複なく構成し、世代・参照は既存公開条件で照合する。構築中に表面化した予期しない内部エラーは4章に従う。

- Boundary件数の共通上限256は撤去する。256を超える場合の処理量・メモリ使用の増加は人間承認済みとし、無制限の収容や同一時間での完了は保証しない。有限容量・範囲チェック・checked演算と既存の容量／内部エラー境界を維持し、容量値と格納形状は実装詳細とする。Boundary専用の上限Profileや救済経路は追加しない。

- CutBoundaryRecordはGeometry Commitで確定する採用面・Side・子Geometry／frame参照を表す。複数Contour／Capを一つのLoopへ潰さず、Geometryが空のSideには架空参照を作らない。境界0件も正常であり、子数は変更しない。履歴は退役Geometry／Actorを強く所有しない。

- `TemporaryRenderCapRecordSet`内の現在のWorld Cap Planeについて、各眼の値を`d = dot(CapNormal, EyePosition - CapPoint)`とし、`dLeft < -FacingEpsilon && dRight < -FacingEpsilon`の場合だけCapRecordをFacingで除外する。等号とepsilon帯を含むその他の場合はSingle Pass Instanced用Recordを残す。Frustum外判定も同じ段階で行い、可視・非空のCap仕事だけがVolume／Capを発行する。可視Cap仕事を持たないRenderFragmentにはStencil Clear／Volume／Cap処理を行わない。判定は現在フレームだけから行い、過去の可視状態を保持しない。epsilon境界の往復による投入切替と頭部微動時の点滅を許容する。通常のclip済み破片カラー描画とShadowCasterは消さない。

- Cap処理は`受付済み未Commit面・SideからRecord構築 -> TemporaryClipConstraintCandidateSetのstable選択 -> 両眼Frustum／Facing Cullによる可視・非空のCap仕事 -> 同一VolumeのStencil Volume Group -> 初期断面の投影競合による通常Color割当て（先頭の最大N−1枠。入らないGroupは最後のColorへ） -> 通常Colorごとの128初期化／そのColorの全Volume／全Cap描画 -> 残りがあれば最後のColorの128初期化／RenderFragment単位の全Selected面clipのVolume／残りのCap描画`の順とする（D-183、D-186）。描画するCapには、他のSelected面で切り詰めたCap Bounds Polygon（最大14頂点）を使う。Operation公開時に同じ採用面のRecordへ一度だけ引き継ぐ。同じColor内のVolume→Cap順を維持する。Plane容量超過で論理状態を変更せず、描画用Cap Record集合は選択結果から描画更新境界で再構築する。

- Cap仕事、Stencil Volume Group、初期断面の投影入力は、別の契約として型と責務を分ける（D-183）。
  - Cap仕事：RenderFragmentと1つのCap境界、可視性、描画用Polygonと初期断面への参照。
  - Volume Group：同一のVolume入力と、それを共有するCap仕事の集まり。
  - 初期断面の投影入力：Groupの初期断面、各眼の投影結果とその確定可否。
  - 最後のColorの入力（D-186）：通常Colorに入らなかったCap仕事と、それらが属するRenderFragmentを重複排除した集合。通常ColorのVolume Groupとは別に扱う。
  - RenderFragmentを表す既存のTargetと`capsComplete`の意味は上書きしない。新経路では、旧RenderFragment方式の`capsComplete`は使わない。

- 製品へ接続する前に、次を確認する（D-183）。括弧内は2026-09-20時点の既存証拠との対応で、記載のない範囲は未照合とする。
  - 凹形状、穴、複数の島を持つGeometry。（非XRの自動比較：K1星形の柱、K2輪、K3三つの島。非XRの陰影付き画像一覧の人間確認。XR Simulator（固定姿勢、Single Pass Instanced、4x）：K1・K2で両眼とも製品とCap別参照の画像が一致。K3のXRと、K1～K3のQuest Linkでの確認は行っていない）
  - Facing epsilon帯、および左右眼で可視性が異なる場合。（CPU試験：片眼だけ可視のCapとepsilon帯内のCapを残す。表示・画像での確認は未照合）
  - 初期断面は重なるが、描画用Capは離れる場合。（CPU試験、表示試験、非XRの自動比較：X5）
  - 厳密に一致するVolumeの共有と、微小に異なる場合の非共有。（CPU試験：同一Volumeは1 Group、100万分の1の差は別Group。表示試験：公開と配置変更を通じて1回だけ発行。非XRの自動比較：X6）
  - Color上限の超過（カメラ準備の拒否）、4x MSAA、複数カメラ。（D-185以降、上限超過は拒否せず最後のColorで描く。bb9cd38で実装済みで、その確認の範囲は下の「実装状況」による。次の拒否の確認は旧契約の証拠であり、新方式の検証には使わない。上限超過：CPU試験・表示試験の拒否と次の準備、非XRでT3-v1を試験上限4で拒否、XR Simulator状態12の成功→拒否→回復。4x：XR Simulatorと、Quest LinkのL1・T3で撮影した描画自身のColor／Depth-Stencilが4 sample。複数カメラ：表示試験だけで、画像では未照合）
  - 6章のGateに合格する実アセットの人形を使った、人間向けの画像一覧。（非XRの陰影付き画像一覧のK4：静的な1姿勢・2視点で、見える問題の指摘なし。XR Simulatorの固定姿勢でも製品とCap別参照の画像が一致。当該アセットの6章Gate合格は非XRとXR Simulatorの記録で確認済み。独立した幾何期待値はなく、Gate合格も画像の一致も描画の幾何的な正しさの証明ではない）

- 実装状況（2026-09-20追記。D-181・D-183の当時の本文と成立範囲は変更しない）。**以下の実装記録・確認記録に現れる表示用Offsetおよび分離Offsetは、その時点の実装にあったものであり、2026-09-21に撤去済みである（5.1）。当時の記述としてそのまま残す。**
  - 実装済み：
    - D-181の複数切断Snapshot（f528426）、既存分類器による分類（a90cd90）、全登録の一経路表示（5581624）。
    - D-183のCPU処理：Cap仕事、厳密一致のStencil Volume Group、初期断面の投影によるColor判定（6c27b54）。
    - 製品のカメラ準備・upload・描画への接続（dce1487）。Volumeは自身の面・Sideだけでclipし、描画するCapには他のSelected面で切り詰めたPolygonを使い、描画用に生成・保持した断面を再利用する。Color上限内に割り当てられなければカメラ準備を拒否し、次の準備試行で再び準備できる（D-183③の当時の契約。dce1487の実装として記録に残す。この拒否はbb9cd38で置き換えた）。
    - D-185・D-186の最後のColor（bb9cd38。local mainへ統合済み）。通常Colorは先頭の最大N−1枠でCap仕事方式を維持し、そこに入らないGroupは分割せず最後のColorへ送る。最後のColorは、その仕事のRenderFragmentを重複排除して各1回、全Selected面でclipしたVolume（Ignored面は含めない）を発行し、残りのCapだけを描く。Color上限だけでは準備も描画も拒否しない。準備容量・不正入力・資源・世代・寿命による拒否、採用時の容量検査、カメラごとのBatch、参照解放、upload失敗時の扱いは維持する。過去の拒否・回復の試験と確認（CPU試験・表示試験、非XRのT3-v1、XR Simulator状態12）は旧契約の確認であり、新方式の検証済み証拠にしない。
    - 断面色と現行の陰影への接続（01472bb。166584cでlocal mainへ統合済み）。仮Capは5.3の断面色を読み、通常は共通のグレー、デバッグでは赤（実Capは緑）で、違いはBase Colorの選択だけである。本体・実Cap・仮Capは同じ`VpShadeSurface`を通り、仮CapはSnapshotのworldの外向き法線を使う。現行の陰影計算の共通化であって、5.3の共通トゥーンの仕上げではない。反映時点は仮Capがカメラ準備時、実Capが描画時（5.3）。Material経由の色とGlobal定数の色が色空間の扱いで食い違っていたため、Materialへ渡す直前で補正した。Cap PassにMain Lightの影のShader variantが増えた。GPU書込みの失敗時の扱いと、生成途中で確保した資源の解放は、いずれも既存の寿命管理に組み込んだ（内部のバッファ構成は実装詳細とする）。
    - RenderFragmentごとの配置を受け取る表示基盤（d5334f8。local mainへ統合済み）。登録が置くのはそれが持つGeometryであり、公開された切断の正負が別々に動き始めると足りなくなる（祖先のGeometryは共有し、その配置は共有しない）。そこで表示は、生存Fragmentがどこに立つかを照会でき、各RenderFragmentをそこで描く。本体、Stencil Volume、Cap多角形と法線、clip半空間、分離の合算、Boundsは同じ採用Snapshotの一つの行列から作られ、描画中に照会を読み直さない。移動だけを理由にGeometryを複製・再転送せず、別Rendererも別台帳も作らない。
      **（2026-09-20、表示専用Offsetの撤去により更新）基準配置がすべてである。**5.1の分離Offsetは存在しないので、Geometry Commitが配置へ取り込む分離も、毎回の収集で合算する分もない。追従している登録では、置換済みの中間FragmentのOwnerを要求せずに、その登録から描かれる**生存子孫へ表示基底を引き継ぐ**扱いを維持する。**以下に記した確認は撤去前の方式に対するものであり、撤去後の証拠として読み替えない。**
      照会の答えは、明示的な静的構成（登録の配置で描く）、追従対象とその現在位置、追従対象なのに位置が言われていない場合（古い位置には描かず拒否）を区別する。Commitで置換済み中間Fragment等の基準配置が必要な場合は、登録のGeometry配置を用いる（台帳の現在対象でないFragmentは自分の配置を持たない）。退役済みの枝を描画へ戻す意味ではない。受付済みで未公開の側は独立したFragmentではなく、枝の根であるSource自身が答える。照会を渡さない従来の静的経路はそのまま維持する。行列の保持方法や照会の列挙値は実装詳細であり、設計契約として固定しない。
    - 共用Scene Volume ProfileのBloom無効化（36377368）。人間がBloomを必要としないと判断し、無効化した。限定した対照比較でpost-processingの関与は確認したが、Bloom単独の因果は切り分けていない。
  - 確認済み（成立範囲付き。記録はPhase2CapJobDisplayの要約を参照）：
    - EditMode：全EditMode 3140/3140は固定容量修正の前の結果である。修正後は関連試験198/198だけを実行し、全EditModeは再実行していない。
    - 非XR（単眼、MSAAなし）の自動比較：14視点で、製品とCap別参照のColor・最終Depthが一致した。製品経路のDepthは非XRの最終値で確認したものであり、以前のハーネスで取得した段階別のDepthは別の証拠として、製品経路の段階別確認には流用しない。独立した幾何期待値に対して、Edge帯の外で欠落・余分はない。本体Depthの3画素（T3-v2で1、K1-v1で2）は原因が未確定である。
    - 非XRの人間確認：陰影付き画像一覧17組（K4を含む）の全102項目で、問題なしが選ばれた。
    - XR Simulator（固定姿勢、Single Pass Instanced、4x、Bloom無効）：状態01～10の16組のA/B比較で、製品とCap別参照の両眼画像が一致した（K1・K2・K4を含む）。状態11で全Capが不可視であることを確認し、状態12（Color上限の成功→拒否→回復）は別の実行で確認した。描画時のColor／Depth-Stencil Attachmentの名前・形式・sample数を、撮影したフレームと対応付けて取得した。
    - XR Simulatorの保存画像の人間確認：左右眼別の216項目で、問題なしが選ばれた。回答者と回答時刻は空欄である。固定姿勢の画像の確認であり、HMDでの立体視の確認ではない。
    - Quest Link（本人1名、座位でほぼ静止）：L1（A公開・B保留）で、単色・陰影とも異常の報告はない。赤い面どうしの境界はこの配置では判定不能だった。
    - Quest LinkのL1 B公開前後：公開は1回で結果はApplied。公開を反映した最初の採用Snapshotで準備が成功した。描画範囲・配置・clip・Offset・Capの幾何は一致し、断面の再生成とGeometry再転送はない。公開後の見え方に異常の指摘はない。切り替えの瞬間に一時的な異常がなかったかは、本人の回答「みてたとおもいます」に基づくため未確定である。
    - Quest LinkのT3：同じ破片に隣接する3枚の断面を同時に見て、面の向きと互いの境界を見分けられ、異常の報告はない。1つの静的な姿勢に限る。
    - 以上はCap仕事方式（dce1487まで）の証拠である。最後のColor（bb9cd38）の証拠は次に分けて記録する。以前のSimulator・Quest Link・保存画像の人間確認を、最後のColorの確認済み証拠に流用しない。
  - 最後のColor（bb9cd38）の確認（記録：Phase2CapJobDisplayのlastcolour-20260920とlastcolour-images-20260920-063140）：
    - EditMode：試験の補強前に全EditMode 3144/3144、補強後に関連試験203/203。補強後の全EditModeは実行していない。
    - 発行内容：関連試験で、描画範囲の期待値は登録した幾何のストレージ情報から、配置の期待値は`TryShow`へ渡した行列から作った。これらとclipを組にして、発行されたVolume command 1件ごとに期待するcommandを1件ずつ消費し、重複も欠落もないことを確認した。最後のColorのclipはそのRenderFragmentのclipと照合し、その面とOffsetは同じRenderFragmentのCap recordとも照合した。恒等でない配置と、範囲の異なる2 submeshの場合を含む。
    - 非XR（単眼、1 sample、D3D11）の7視点：製品が発行した内容が、その準備の分類結果どおりであり、準備の拒否は0件だった。画像ハーネスが直接照合したのは各Colorの件数とclipであり、描画範囲と配置は上のEditModeの範囲である。
    - 画質：最後のColorを使わない対照（S1）とX6は、Cap別参照とColor・最終Depthが一致した。最後のColorを使うT3では断面が欠け、5.2の品質例外8の観測として記録する。最後のColor一般に参照との一致は求めない。
  - 断面色と陰影の接続（01472bb）の確認（記録：Phase2CapJobDisplayのcapcolour-20260920、capshading-20260920、画像はcapcolour-images-20260920-084758）：
    - 寿命管理の2点（GPU書込みの失敗、生成途中の解放）の修正前：全EditMode 3149/3149と、非XRの2視点の画像確認。
    - 同修正後：関連試験281/281。全EditModeと画像は再実行していない。
    - 非XR（単眼、1 sample、D3D11）：通常→デバッグ→通常の順に描き、2つの通常表示が画素まで同じであること、断面以外の色と最終Depthが切り替えで動かないこと、同じworld法線・同じ通常色の対照（marker付きの実Cap面と仮Cap）が同じ画素になることを確認した。
    - marker付きの実Capは色の選択を見るためのものであり、実Geometry Commit接続の確認ではない。
    - 166584cへの統合後に追加の試験は行っていない。
    - 以前のXR Simulator・Quest Linkで使った検証用の赤い陰影Shaderは、今回の製品Shaderの確認済み証拠にしない。
  - RenderFragmentごとの配置（d5334f8）の確認（記録：Phase4Physicsのrfplacement-20260920）：
    - 最終の関連試験146/146。**Snapshot単体の確認**（独立した移動・回転での配置・平面・法線・Cap頂点、設計注の行程、照会なしとの同値、退役枝を描かず尋ねもしないこと、採用済みSnapshotの不変性、位置未指定の拒否）と、**製品の`TryCommitCut`を通した確認**（借用経路3件、Produced経路3件）を区別して数える。既存の表示・Commit・Stencilの関連試験はこの範囲に含めて再実行し、変更していない。
    - 非XR（単眼、1 sample）の独立配置比較：一方を90°回した2側を、照会を使わず既知の配置へ手で置いた参照（各側単独・合成）と比べ、**色・生Depthとも相違0画素**。
    - 非XR（同条件）のCommit前後比較：**色と被覆は一致**（被覆は両方129074、片方だけ0／0）。正負を合わせた生Depthは74,460画素で相違し、**最大約3.84e-7、1e-5超は0画素**。別経路のラスタライズによる数値差の可能性があるが原因は未確定で、Depthの完全一致とも原因確定とも扱わない。許容差も品質例外も広げていない。
    - 先行して撮ったCommit前後の比較（images3）は、ハーネスが台帳の採用面をKernelにも渡したため両側が別の面で切られており、**不採用**とする。修正後のimages4と混ぜない。
    - 統合後の追加検証は行っていない。
  - 未確認：断面色と陰影の接続（01472bb）については、4x MSAA、Single Pass Instanced、XR、Player、性能、影を落とすものがある場面が未確認である。Shader variant数とコンパイル時間の増加も測っていない。5.3の共通トゥーンの仕上げは残る。実Geometry Commitとの接続は、4.5.6の「実装状況」に記した範囲で成立した（そこに挙げた未接続・未確認は残る）。最後のColor（bb9cd38）については、4x MSAA、Single Pass Instanced、XR（Simulator・実機）、Player、性能が未確認である。この記録のために追加試験は行わない。共通して、XRでのDepthの直接取得、Player、性能（CPU／GPU時間、製品相当の負荷での評価。固定ケースの発行数と成功／拒否の件数は記録済み）、Quest Linkでの他の姿勢・移動中とL1・T3以外の形状、XR SimulatorでのK3と固定姿勢以外、Cap仕事方式での複数カメラの画像、表示・画像でのFacing epsilon帯、8bitの排他利用と一般構成への保証（確認した構成のAttachmentの特定とは別。Phase 1.52の記録は変更しない）。K4には独立した幾何期待値がない。以前のXR Simulator・Quest Link確認に使った赤い陰影は検証用のShaderであり、5.3の共通トゥーンによる通常表示の完成ではない。
  - 未解決：O-034（製品の`MaxStencilColors`など。試験値8は製品値ではない）。実Geometry Commitとの接続は4.5.6の「実装状況」の範囲で成立しており、Final Physicsのこの表示経路への接続と動く子の配置の受渡しも、7.2の「実装状況」の範囲で成立した（9993ece。Selected面だけで成立する非Character・直接Final経路）。Ignored集約の配置もD-187の範囲で成立した（f72f7d0。上の「実装状況」）。そこに残る未接続・未確認（Physics／Geometry Workの自動駆動と製品の構成根、Hit／Queryの解決、Provisionalの公開・建物D6・handoff、Character、Player・性能）は7.2の「実装状況」による。Provisionalの未公開構築自体は7.2の「実装状況」の範囲で成立しており（f06f462）、この表示経路への接続は残る。
  - RenderFragmentごとの配置（d5334f8）に残るもの：
    - Physics Owner対応・公開処理からこの配置照会への製品接続は、**9993eceで実装しlocal mainへ統合した**（成立範囲は7.2の「実装状況」。Selected面だけで成立する非Character・直接Final経路）。照会はその時点のOwnerのworld変換と、系譜の最初のOwnerに明示されたGeometry local→Owner localの対応から基準配置を作り、公開時に子へ継承する。この基盤の単位の時点では、表示側が配置を受け取れるようになっただけで、Ownerの位置を伝えるものが無かった。
    - Ignored集約の基準配置は、**D-187で方式が決まり（人間判断、2026-09-20）、f72f7d0で実装してlocal mainへ統合した**。集約は引き続き一度だけ描き、その基準配置をグループの走査順で先頭の生存枝について既存の配置照会が返す基準配置から取る。配置だけを取り、Ignored面はclip・Capへ加えない。
      **変えたのは配置照会の対象だけ**である（`VpMultiCutSnapshot`のグループ化で、集約されたグループに限り照会先を集約の根から先頭の生存枝へ替える）。**集約の根、グループの同一性、Selectedの候補とSideは維持する**：描かれるRenderFragmentの根も、グループを決める根と選択済みprefixの一致も、根の系譜の検査も、Cap仕事の作り方も従来のままで、代表枝へ置き換えていない。非集約の枝は根がその枝自身なので、照会する値は以前と同一である。
      **使うのは既存照会の基準配置**（Ownerのworld変換とGeometry local→Owner localの対応）であり、そこへ登録の畳み込みを掛ける従来の経路をそのまま通る。**当時、表示用Offsetの合算、Geometry Commitの畳み込み、Selected面による本体・Cap・Stencil Volume・Shadowの生成は変更していない**（前二者はその後2026-09-21に撤去した。Selected面による生成は現在も変更していない）。Ignored面は何も追加していない。
      **集約の根のOwner不在による恒久停止の経路は解消した。**公開済みのIgnored境界の根は置換済みでOwnerを持たないため、従来は照会が位置なしと答え、Snapshotが入力契約の不成立で終わり、表示が恒久停止していた（この停止の記録は履歴として残す）。照会先が生存枝になったことでこの経路へ入らない。
      **一方、変更していない停止**：`RetiredInsideAggregate`（集約の内側の退役による停止）と、生存する追従対象にOwner自体が欠落した場合の扱い（5.6の照会契約どおり、位置なしとして拒否する）。静的配置への退避も、停止後の復旧機構も追加していない。
      決定前の状況は次のとおりで、記録として残す：集約を解くと、Ignored面で切っていない同じ形状が枝の数だけ別の場所に描かれる。単一配置で集約を維持すると、集約形状がそれが表すどの剛体からも離れる。9993eceでOwnerからの接続が入った後も、集約rootが公開で置換済みのSourceであればOwnerは無く、照会は位置なしと答え、Snapshotは入力契約の不成立で終わる（`VpLogicalCutDisplay`の恒久停止に至る）。
      確認（記録：Phase4Physicsのaggplacement-20260920）：最終の関連試験**231/231**。**3つの経路を別の証拠として分ける。**
      ①**実の凸切断・cook・公開を9回**通して1本の鎖を作り、9本目が容量超過でIgnoredになる状態で、実の配置照会と実の表示収集が成立し停止しないことを確認した（1件）。**この試験はGeometry Commitを通していない。**
      ②**集約の分裂と解消**は、試験が作った`ReusesInput`（正側が入力を借用）と`Empty`（負側）の結果を**製品の`TryCommitCut`**へ渡して確認した（2件）。**実Geometryの切断・生成は通していない**（Kernelもstorageの切断も走らない）。
      ③残る4件は**Snapshot単体**の確認である（台帳とSnapshotの構築だけで、表示・Commit・物理は通さない）。
      期待する代表・配置・Selected／Ignoredは、いずれも試験入力から決めており、製品の出力から逆算していない。**撮影・人間確認・全数EditMode・XR・性能測定、統合後の追加検証はいずれも未実施**である。過去の画像を今回の証拠へ流用しない。
      なお、①の鎖を作る途中で凸Kernelが失敗した。**その原因は、後の診断で `ConvexCutOwnerStatus.EmptySide`（その平面がそのOwnerを分割しない）と特定した**（記録：Phase4Physicsのconvexcut-capacity-20260920）。切断を繰り返して正側のconvexが1つになり、次の平面がその1つを外したためである。容量はどの段でも不足していなく（`QueryCapacity`の要求と予約が一致し、書いた量は常に予約以下）、**削減処理は一度も走っていない**。**従来ここに「既知のreduction容量に当たる」と書いていたが、それは根拠を確かめずに推測を記したもので誤りである。****平面をprismの軸沿いに変えたのは試験入力の変更であり、製品には触れていない。**この診断で製品の修正、容量の変更、Outcomeの追加は行っていない（`KernelFailed`のままでも `KernelStatus` に`EmptySide` が残るので、原因の識別に新しいOutcomeは要らない）。新しい拒否契約にも必須試験にもしない。受付側が必ず分割を保証する責務を負うのか、`EmptySide` のときAbort以外の扱いを認めるかは、**この診断だけでは決めていない**。2026-09-18にreduction容量へ入れたEuler境界の正否についても、この診断は何も言わない（当時の採り範囲の再確認はしていない）。この診断の1件の実行は、上の関連231/231とは別の実行であり、合算しない。
    - Cut DAGのframe写像の接続は、**b98661bで実装しlocal mainへ統合した**（成立範囲は4.5.6の「実装状況」）。台帳・Commitの採用面は受付時の値のまま保持し、Kernelへ渡す面だけをGeometry localへ変換する。継承の成立範囲は、子のlocal frameを建て直さない現行経路である。
      当時の記録は証拠としてそのまま残す：この表示基盤の単位の時点では**写像が適用されておらず**、その単位の入力（`lineageToGeometryLocal`が`Translate(0,−0.4,0)`、採用面が`y = 0`）で不一致を確認した。その時の描画確認は、ハーネス側で面を変換して実切断を行い、製品の`TryCommitCut`を通したものである。b98661bの比較はハーネスの面変換を外して実のDAG経路を通しており、別の証拠として4.5.6にある。
    - 全数EditMode、XR、Player、性能は今回未確認である。7.1.2全体やPhaseの完了は意味しない。
  - 以上はCap仕事方式の接続単位、最後のColorの実装単位、断面色と陰影の接続の区切りであり、Phase 2全体、T-066・T-067・T-089の完了を意味しない。未確認事項の扱いはD-184に従う。RenderFragmentごとの配置（d5334f8）は表示側の基盤の区切りであり、Phase 3・Phase 4やその受入れ項目の完了を意味しない。

- Camera内部／Near Plane近傍の表示は5.2の品質例外とD-131に従う。Stable Geometry置換後はTemporary Stencil由来の部分Capを残さない。

- 仮Cap処理中にStencil Byte全8bitを排他的に使える構成だけを対応対象とする。Renderer初期化時に既知の設定と必要なAttachmentを一度確認し、成立しなければ4章の共通Player終了に従う。部分Bit利用、Stencilなしで継続する代替経路、復旧状態機械、毎フレームの構成再検証は作らない。既存Depth／Stencil Attachmentで成立する場合は別Textureの確保を要求しない。Stencil Byteの恒久的な物体割当は行わない。

### 5.7 共用Geometry・更新・描画の責務境界

前処理または切断対象登録は6章の共通入力契約を一度だけ検証し、合格したGeometryだけを表示／Stencilの共通正本として公開する。契約内入力に対する切断出力は6.4の構成契約を継承し、製品Runtimeで出力Geometryを再検証しない。4.5.6の公開条件が揃った後、表示とStencilへ一つのGeometry Commitとして公開する。用途別の適否、修復、別Mesh、同期完成待ちを追加しない。

点Anchor集合と、そこから導出される所有者の固定／動的、描画対象、選択済み切断平面、ローカルCap Boundsはそれぞれの入力変更時に担当側が更新する。同じフレームでの参照公開、世代・Commit条件を維持し、毎描画で切断履歴を再評価しない。

**構造評価と配置評価の分離（2026-09-21）。**表示Snapshotの構築を、入力変更時だけ行う**構造**と、毎フレーム行う**配置**の二つに分ける。当時の実装はフレームごとに全体を作り直しており、切断状態が変わらなくても履歴を再評価していた。

- **入力変更時だけ**：台帳の構造検証（各登録rootの生存と系譜の走査、root重複の検査）、系譜探索と候補収集、Selected／Ignored選定、Ignored集約のグループ化、各RenderFragmentが**どのFragmentの位置に立つか**の決定、および**描画データ構築で必要になる台帳由来の事実**——各RenderFragmentがどの切断のどちら側か・公開済みか・Anchorで固定か、各候補のCapが公開済みか・どの子を作ったか・固定か。後者は当初毎フレーム求め直しており、**公開済み枝の由来を引くたびにOperation列を順に読んでいた**ため、構造を再利用しても走査が残っていた。
- **毎フレーム**：各登録の現在配置がplacementであることと、その箱の断面がfloatで計算できること、各RenderFragmentの**現在の基準配置の照会**、World Plane・法線・clip・Cap断面。カメラ準備は従来どおりフレームごとに行う。
- **拒否の順序は変えない。**検証は従来どおり一つで、「全登録の入力契約 → 登録ごとに root の存在・保守的断面・root 重複・系譜の走査」の順である。再利用経路は**この順序の中で、構造が既に答えた検査だけを飛ばす**。順序そのものを組み替えない。

**変更検出。**台帳は自身の記録を書き換えるたびに**revision**を進める（表示を経由しない更新も数える）。表示側は自身がSnapshotへ入れるもの——登録の追加・終了、Geometry Commitによる本体とreflectedの変更、配置照会実装の差し替え——を数える自前のrevisionを持つ。両者が前回と同じときだけ構造を再利用する。**全Operation走査やハッシュ計算は行わない。**変更時は全体を作り直し、Capの個別削除や部分更新は行わない。

**保持場所と寿命。**構造は採用済みSnapshotが持ち、候補Snapshotへは**配列複製**で引き継ぐ（台帳走査も系譜探索も伴わない）。寿命はSnapshotのものであり、別の台帳や汎用Cacheは作らない。外部から渡されるreflected集合は登録時に自前配列へ複製して保持するので、呼出側が後から変更しても取りこぼしは生じない。

**採否との関係。**revisionを「処理済み」として記録するのは、転送まで終えて候補を**採用した後**だけである。容量拒否や不成立で採用できなかったフレームは記録を進めない。したがって、**未採用の構造変更があれば次の機会にも構造から作り直す**。構造変更がないまま拒否された場合は、採用済み構造の再利用のまま再試行する——記録を進めないことは「必ず作り直す」ことではなく、「採用していない変更を済んだことにしない」ことである。D-187の集約rootと配置照会対象の区別、照会のMissing／不正配置の検出、RetiredInsideAggregate・容量拒否・恒久停止の区別、同一フレーム内での差し替え禁止は、いずれも従来どおりである。

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

必要容量の照会・見積りはKernel実装と同じ側が所有し、呼出側が領域を用意して数値Workへ渡す。正確な出力Countを先行Jobで求める方式は要求しない。

**現行実装（2026-09-21）。**通常の容量取得は、入力の規模記述だけから初期容量を見積もる。取得済みIndex範囲の三角形数`T`、範囲数、Topology頂点数を読み、**頂点・Indexの中身や三角形は走査しない**。交差三角形数は`K̂ = min(T, max(1, ceil(8 * sqrt(T))))`とする。

**これは初期予約のための経験則であり、任意の入力に成立する保証ではない。**式の形の根拠は「平面は面と曲線で交わるので、交差三角形は帯をなし、面積が`T`で伸びる一方その長さは`sqrt(T)`で伸びる傾向がある」という見方であり、係数8は既存計測が扱う少数の通常閉形状（交差数は根の数倍）から選んだ。平面に沿って並ぶ形状などはこれをはるかに超えうる。見積りから予約量を作る式は照会経路と共有する。

**不足は通常の事象として扱う。**不足した実行は予約の外へ書く前に終了し、必要量（頂点・Indexは正確、scratchは実際の`K`から求めた推奨値）を返す。呼出側は6.5の既存規則で予約し直して再実行し、**その規則が許す回数だけ繰り返す**。`K̂`が`T`に届く場合でもCapの補助頂点とarenaは見積りのままであり、scratch不足を解消した次の実行でVertex／Index不足が判明することもある。**一度で成立することも、次の一度で決着することも保証しない。**

**交差数を実測する照会経路も上界ではない。**検証・比較用に残す照会（実測した交差数から算定する経路）は、三角形数と交差数は正確だが、Capの補助頂点とper-cycle arenaは見積りを含む。したがって照会から作った予約でも不足しうる。

**算術。**容量計算は途中・alignment・最後の型変換まで64bitで行い、`int`として表現できるかを一箇所で判定する。**Kernelの実行入口も同じ計数を使う**ので、呼出側が先に容量を問い合わせたかどうかに安全性が依存しない。三角形数の合計が`int`に収まらない場合は、負へ回り込むときも小さい正数へ戻るときも同じく不正入力として、分類・書込みの前に終える。表現できない場合は**小さい値へ丸めない**。照会・見積りは「表現不能」を返し、呼出側は再試行ではなく容量不成立（`CapacityOverflow`）で終える。実行側のlayoutと不足時の推奨量も同じ算定を用い、推奨量が表現不能なら負値で返して既存の「大きすぎて表現できない要求」経路へ入る。

同期・非同期の両入口が同じ見積りを使い、**容量取得のための Worker と事前走査工程は持たない**。通常経路は「入力の読取寿命を確保 → 規模から見積り → Mainで予約 → 切断Worker → 完了回収・結果処理」で、不足時だけ完了回収後に6.5の再予約・再実行へ入る。

**平面が外れる場合の予約。**どちらの側に全体が乗るかは走査しなければ分からないので、初期予約は取る。入力借用として判明した実行は予約を未使用のまま丸ごと返す。借用・空側の結果そのものは変更しない。

**複数同時予約と独立切断の並列実行（2026-09-21）。**storageは出力全体を**専有範囲（span）**として割り当てる。対象はVB、頂点対応表、submesh記述子、vertex block、およびIB（IBは従来から範囲アロケータを持つ）。**当時の実装は、IB以外をそれぞれの配列末尾から取る方式で、開いた予約は同時1件に限られていた。**

- **割当て。**空き範囲をアドレス順に保持し、収まる最初の範囲の先頭から取る（first fit）。返却時は前後の空き範囲と併合する。成長・圧縮・移動は行わない。**一度渡したspanは動かさない**ので、別の予約が領域を取っても実行中Workerの入力・出力・scratchのポインタを無効にしない。
- **確定。**使用分だけをその予約自身の位置へ公開する。VBの公開位置は高水位であり、生存頂点数ではない。逆順完了では後発の公開が先行の未公開領域を飛び越えるため、高水位未満に未公開スロットが生じうる。**どの公開済みGeometryもそのスロットを指さない。**
- **未使用部分の回収時期。**確定の時点で、予約のうち書かれなかった残りを**その場で**返す。取消の時点で、4領域すべてを丸ごと返す。返却は隣接併合されるため、取消・再試行を繰り返しても容量は恒久的に失われない。
- **retirementは対象外。**公開済みGeometryを退役させるとIB範囲は戻るが頂点spanは戻らない。これは本単位以前からの仕様であり、変更していない。
- **残る待機条件。**①空き容量が足りない場合、**同じrunnerの別Request**が領域を保持している間はReadyで待つ。runnerが見ているのは自分のRequestだけであり、別runnerや同期切断による保持は検出しない。その場合は容量失敗となる。②いずれかの予約が開いている間、storageへの通常のappendは従来どおり拒否する。③親のGeometry Commitを入力に要する子切断は、従来どおり依存解消を待つ。
- **容量上のトレードオフ。**複数の予約が同時に領域を保持するので、同じ空き容量で同時に走れる切断数は各予約の見積り量に依存する。見積りは上界ではなく余裕を含むため（本節）、実使用より多くの領域が一時的に押さえられる。断片化は隣接併合で抑えるが、寿命の異なるspanが交互に並ぶと最大連続空きが空き総量より小さくなりうる。

**予約枠待ちは容量失敗ではない。**同じrunnerの別Requestが唯一のcut出力予約を保持している間、後続はReadyのまま未投入で待つ。これは**当時の**同時1件制限による待機であり（その制限は2026-09-21に解消した。下記「複数同時予約」）、容量不足にも切断失敗にも読み替えない。初期見積りがstorageの空き容量に収まらない場合（`TryReserve`が拒否する場合）だけが`StorageCapacity`である。成否の扱いは6.5に従い、予約不足は4.5.3の非公開終了・再予約・再実行へ、表面化した予期しない内部エラーは4章の共通終了へ接続する。契約内入力の未対応を正常な失敗や偽の空出力で代替しない。Unity Object・Scene操作、論理ID・世代・authority、資源取得、Schedule、GPU転送、Geometry CommitとRenderer固有情報はKernelの外で扱う。型・field・layout・関数名は実装詳細とし、内部工程の別Job化や永続的な引渡しartifactを要求しない。

元surfaceの既存submesh対応を保持し、実CapのIndexは本体の既存Draw rangeへ含める。実Capはその本体と同じMaterial・同じShader Pass・同じDraw callで描き、5.3のraw UV判定で通常Textureの取得と固定断面色の選択だけを分岐し、以後の陰影処理を共用する。複数の既存Draw rangeがある場合のCap配分は実装詳細とし、Cap用のsubmesh・Drawは追加しない。この同一Draw規則は実Capを対象とし、即時仮断面のStencil Volume／Cap描画は5章に従う。複数Renderer、submesh、閉Componentを一つの巨大Geometryへ結合する義務はない。幾何処理に必要なTopology対応を保ち、Geometry参照と描画集約、LogicalFragment、物理所有単位を分離する（4.5.1）。属性seamによるRender Vertex分裂、非同期Jobのための世代保持、CPU／GPU表現、物理Convexも別Geometryとは扱わない。

### 6.2 共通入力契約

`RenderCutTopologyMap`は属性seamをまたぐ論理Topologyの対応と、別Topologyの区別に必要な情報を持つ。通常切断の初期実装ではpersistent adjacency、BVH、全Component一覧・恒久IDを前提にせず、必要なedge・局所隣接・ContourをIndexとTopology対応から切断中のscratchへ構築する。元surfaceと生成Capの属性分裂後も再切断に必要な対応を残す。対応の格納形式、配列数、識別方法は実装詳細とし、位置一致による推測weldで代替しない。

`RenderCutTopologyMap`が表すTopologyについて、各Edgeへちょうど2面が接続し、その2面が共有Edgeを互いに逆方向へたどること、各Topology Vertexの周囲の面が一つの閉じたfanを作ることを要求する。使用するposition／属性はfinite、index／submesh／Topology参照は有効とし、同じTopology Vertexのposed positionと同じOriginal Edgeの交点positionは一度生成したcanonical値を共有する。

元surfaceのUV符号は入力Gateに含めず、5.3の誤表示許容を適用する。UVを含む使用属性のfinite条件は維持する。

Disconnectedな閉Component、全体反転した閉Component、bind pose／skinning後のSelf-intersection、別Topology Component間のIntersection／Overlap、Internal／Nested Shell、別Topologyとして表現されたCoincident／Duplicate Componentを許容する。物体全体を単一連結成分にせず、座標一致を理由に別Componentをweldしない。一方、Boundary Edge、3面以上が共有するEdge、一つのTopology Vertexを共有する複数fan、局所winding不整合は共用入力として受理しない。

この契約は閉じた向き整合Topologyの契約であり、自己交差のない幾何Solidの証明ではない。全Mesh自己交差、inside／outside、Generalized Winding Number、signed volume、外向き判定、向き正規化を入力Gateへ追加しない。

基底AssetはImport／前処理または切断対象登録時に一度だけ検証し、合格後は切断側が不変条件を継承する。UV／Normal／Material seamはFBX control point等の由来Topologyで対応付ける。由来が不明なseamを位置探索で推測したり、開放Boundaryのまま受理しない。不合格入力は切断可能Geometryとして登録せず、Runtime修復、Stencil専用Shell、表示だけを許す切断経路へ降格しない。修正しない不合格入力は切断対象外にできる。

### 6.3 Runtimeの面積0 Triangle

Runtimeの共用Geometryは面積0のTriangleを通常の入力・成功出力として保持できる。面積0だけを理由に面やedge-useを除去、非寄与化、修復、Commit拒否せず、閉鎖・edge／vertex manifold・局所winding整合を論理Topologyで維持したまま表示、Stencil、再切断へ使用する。

退化面の属性はfiniteに保つが、定義できない幾何法線の正規化を要求しない。採用する基底Assetの面は非退化とするが、この品質条件をスキニング後または切断後のRuntime表示Geometryへ再適用しない。Physics ConvexのRuntime条件は7.2に従う。Fixture固有の検証は17章の実装詳細とする。

### 6.4 切断とCap生成

Cを6.2と6.3のRuntime共通契約とすると、既存の有効な切断平面に対する切断処理は次を構成上満たす。入力座標・属性・切断平面はfiniteかつ既存製品Asset／Poseの通常範囲内とし、float演算がoverflowする極端な値は対象外とする。このためのRuntime範囲検査や数値Profileは追加しない。

```text
C(入力Geometry)
    ⇒ 各非空出力Geometryについて C(出力Geometry)
```

これは切断アルゴリズムの構成契約であり、出力全走査・変更部検査・finite走査・Topology Validator・Validation Jobを製品Runtimeへ置かない。交差判定、分類、補間、退化時の固定値選択、出力予約範囲を越えないための容量分岐は切断処理またはメモリ安全境界として維持する。出力契約はT-083のオフラインHarnessで確認する。Topology Vertex単位のsigned distanceを一度だけ確定し、OnPlaneはPositive側へ所有させて同じ分類を全incident Triangleで共有する。全頂点OnPlaneのTriangleはPositive側へ1回だけ保持し、そのTriangleからCap segmentを生成しない。平面がvertex／edge／faceを通る場合、同一点に複数のcut portが生じる場合、極小／面積0 Triangle、契約内Self-intersectionを通常ケースとして扱い、別Topology由来のportを位置近傍だけで接続しない。

各出力の元surfaceが持つ切断境界Half-edgeに対し、Capは逆方向の境界Half-edgeを持つ。切断Boundary EdgeにはCap側の面をちょうど1枚接続し、Cap内部Edgeには互いに逆方向の2面を接続して、頂点周囲も閉じた単一fanにする。非退化CapのNormal／Tangentはこのwindingと一致させる。切断平面は位置、signed distance、射影等へ使用できるが、実Capの表裏は元surfaceの有向境界から決め、全体反転した入力の向きを作り直さない。生成Cap Triangleの全Render Vertexには5.3の固定UV slotを設定し、元surfaceとの属性seamは既存のRender Vertex分裂で扱う。既存実Capの再切断では通常の属性補間で固定slotを継承する。Texture Mapping用のCap UV生成は行わず、幾何処理とNormal／Tangent生成は維持する。

Half-edge、edge hash、圧縮adjacency、Contour表現、三角形化、局所交差処理は、共通契約を満たす範囲で実装と実測から選ぶ。単純なContourへfan等を使うことは禁止しないが、不正入力や不正出力を重複Cap、逆向き重複面、Open Chain封鎖、Non-manifold lane分解で救済する段階列は要求しない。異なる閉ComponentやTrackをBoolean Unionせず、契約内の自己交差処理に必要なら局所Arrangementを共通Kernel内で使用できる。

### 6.5 公開と失敗

切断CPU出力の完成後はRuntime出力Validatorを挟まず、4.5.6のFinal／Logical公開後・祖先順Geometry Commitへ進む。待ち合わせは正常な未完了であり、用途別世代を作らない。8章のauthorityを失った成果物は不採用・回収し、共通Player終了へ送らない。

出力予約不足は4.5.3の非公開終了・再予約・再実行に従い、切断受付の再試行へ広げない。共用Geometry切断または論理構築で表面化した予期しない内部エラーは4章、CPU／GPUの絶対容量不成立は4.5.4に従う。物理・支持のCommit単位、Actor状態、世代・資源寿命は変更しない。

## 7. 物理切断

### 7.1 PhysicsSplitTransactionとLogicalFragment退役

一つの生存LogicalFragmentを置換するActive PhysicsSplitTransactionは最大1件とする。同じObjectId内の別LogicalFragmentは並行できる。Transactionは受付Snapshot・採用面・Sourceの物理所有変更authority、Provisional／未公開Final資源、点Anchor配分と受付時の親Rigidbody質量Snapshotを一回の物理分裂のために保持する。Geometry DAG、公開子孫・current leaf、Cut履歴、Geometry完了待ち、最終資源回収待ちは所有しない。既存CutOperationId、Pending Cut、Work識別・所有情報、生存性と所有参照を使い、新しい公開Transaction ID・状態／Reason enumを追加しない。

#### 7.1.1 未公開構築とProvisional公開

受付後は4.5.2の必要入力準備を経て即時clip／Stencil／仮Capを開始し、Final Convex／cookを並行して進める。点AnchorやWork入力未完了、未投入、Queue／Bake枠待ち、物理Step待ちは通常のPendingであり、構築不能とみなさない。

必要入力が揃った時点で、7.6のrobust support分類に従う正負2 Actor、旧Cooked ConvexのShape Instance、必要なSibling D6とBuilding World D6を一度だけall-or-none構築する。Provisional公開前にFinal一式が公開可能ならProvisionalを省略して直接Final Commitする。それ以外でProvisional一式の構築を試みて成立しなければ未公開成果物を破棄し、Transaction Abortとする。旧物理を維持したFinalだけの継続、再試行Queue、厳密予約Protocol、Actor／Joint Poolを必須にしない。

旧Cooked Convex GeometryをProvisional Shape Instanceへ結び付ける前に当該GeometryのProvisionalCollisionResourceLeaseを取得する。公開までは現在の旧物理を変更せず、途中のActor／Shape／ConstraintをGameplayや物理Stepへ部分公開しない。cleanupの内部順序は実装詳細であり、参照するShape／Actorと必要なPhysics Stepの寿命を満たして当該Leaseを一度だけ返す。別Leaseの参照消失は待たず、Geometry自体は全参照と最後のLease返却前に破棄しない。Timeoutだけで退役・Lease返却しない。

構築に成功したProvisional一式は、安全なPhysics境界で旧ActorをSceneから外す操作と正負2 Actorおよび必要なConstraintのScene投入をall-or-noneに切り替える。LogicalFragmentは未分裂のまま、両ActorのHitを同じSource LogicalFragmentへ解決する。旧資源の回収は既存の参照・Physics Step寿命規則に従う。

Provisionalを省略して直接Final Commitする場合を除き、切断受付時に必要入力が揃い、4.4の既存予算条件のもとでその描画フレーム内に安全なPhysics公開境界を確保できる通常ケースでは、Provisional一式の構築と公開を受付フレーム内で行う。呼出し側は実際のPlayerLoopと受付位置に合わせてこの経路を接続し、構築後の一律の翌フレーム送りやPump／Dispatchの呼出し順だけによる固定待ちを設けない。FinalのConvex切断・cookや表示Geometry切断の完了を待たない。入力未完了、予算終了または安全な公開機会がない場合は既存Pendingとして次の成立機会へ進め、構築不能時のAbort規則は変更しない。同フレーム化のための強制Complete・busy polling・再入は4.4に従って行わず、物理シミュレーションにも再入しない。Scene公開後の移動・分離は物理Stepの結果であり、同フレームに見える隙間は保証しない。

Provisional生成のためにConvex切断、Mesh複製、Physics.BakeMeshを行わない。同系譜Sibling間のCollision responseだけを無効にし、外界とのCollisionは有効にする。旧Convex共有によるGhost Contact、早い接触、外部物体へのImpulse重複を許容する。点Anchorを持つ所有者は固定しOffset／Impulseは0、持たない側だけを動かす。GeometryのSide空／非空を物理固定の条件にしない。

- 同じ切断で生じたProvisional Sibling間の`ProvisionalSeparationConstraint`は、Unity `ConfigurableJoint`によるanchor-offset D6へ固定する。`autoConfigureConnectedAnchor=false`とし、生成時の採用切断面法線をJoint所有Actorのlocal spaceへ変換して`axis`に設定する。`secondaryAxis`は同じlocal spaceの非平行なfinite方向から決定論的に直交化する。接線2軸の並進と全相対回転をLocked、XをLimitedとする。対称Linear Limitを`±1 m`、法線方向のanchor offsetを`1 m`として、生成時の相対位置を内向き境界、外向き`2 m`を反対側境界とする。これはJoint座標系での設定区間であり、毎Stepの厳密な変位保証ではない。両anchorは、生成時のWorld位置関係をJoint X座標の内向き境界へ置くよう、それぞれのRigidbody local spaceで設定する。軸の符号、Jointを置くSibling側、offsetを置くanchor側、直交基底の具体的な選択は実装詳細とする。Drive、Spring／Damper、Projection、Gameplay用Break Force／Torqueは使わず、存続中のaxis、secondaryAxis、anchor、connected anchor、Linear Limitを更新しない。

D6上限での停止・引戻し・Impulse・jitter・表示の違和感、引止めと除去後の運動差は正常な近似として許容し、検出・再中心化・Limit拡張・独自Pose／Velocity補正を行わない。法線上限は上記固定値だけで、物体寸法・Impulse・速度・経過時間から求めない。待機時間の上限は保証せず、Kerfは0とする。

ProvisionalSeparationConstraintはFinal handoff、置換、AbortまたはActor退役で既存Step・参照寿命後に一度だけ外し、除去のためのpose／COM線速度／角速度補償を行わない。BuildingWorldD6Constraintは別のActor寿命で維持し、7.2.2に従う。

実装状況（2026-09-20追記、2026-09-21更新。上の目標仕様と既存の決定本文は変更しない）。本節のうち、非Character・非建物についての正負Provisional候補、共有Shape、Sibling D6を未公開で構築して回収するところまでは実装済みである（f06f462）。**2026-09-21に、その候補のProvisional公開——旧ActorのScene除去と正負2 Actor・Sibling D6のall-or-none投入——と、公開済み対の保持・終了、両Actorから同じSource Fragmentへの解決、正負別の表示追従までを実装した。**論理子は作らず、`Publish` を呼ばない。ただし公開してよいかの判断は**既存 Final 経路と同じ `PreparePublication`** に任せる（所有変更 authority の不一致、Anchor 未準備、非 Active をここで出し、Stale の回収も一度だけ行う）。**Anchor 未準備は拒否であり、構築不能 Abort へ変換しない**（未準備のまま公開すると Snapshot が正負枝を作れず、表示が止まる）。**切替成立前の例外は両側を Scene から外してから伝搼させ**、旧 Source と片側が同時に残る状態を作らない。公開済み対の終了責務は対応を保つ Registry に一本化している。公開済み対は受付済み切断（`CutOperationId`）の側に保持し、新しい公開ID・状態体系は追加していない。分離Impulseは**子ごとに1値ずつ呼出側から受け取る**形にし、強さの算出・向きの定義・製品値は実装していない（7.2の目標仕様による。向きは人間判断待ち）。

**未実装のまま残るもの。**製品の受付経路とそれを駆動する呼出し主体（したがって受付フレーム内公開＝T-091の確認）、入力Shapeの借用に対する保持の所有記録、Final成果物の保持先、Building World D6、Provisional→Final handoff、Hit検出そのもの、Character。公開・終了の寿命条件については、**標準のFixedUpdate自動シミュレーションであり対象Sceneの手動シミュレーションを挟まないUpdate区間**という範囲で、旧Actorが即時にSceneから外れること・共有Meshは最後の保持者が返すこと・BankはOwner終了と読み手の終了の両方で解放されることを実装と試験で確認した。この範囲外の駆動（手動シミュレーションを混ぜるもの）へは一般化しない。成立範囲と確認の範囲は7.2の「実装状況」による。

FixedSupportAnchorは物理所有単位（Owner）に所属する独立したfiniteな点集合とする。固定支持を設定する入力は所有者とFragment Physics Frame内local位置を明示し、支持を設定しない入力の集合は空とする。個々のConvex／Cellとの対応や、初期・切断後・cook後の形状および表示Geometryへの包含・表面一致・近接を要求しない。同じframe内では形状変更に合わせて位置を修正せず、recenter等のframe変更時だけ既存の写像と数値許容で同じ点を表す。

受付成立後、Source Ownerの現在Anchor集合だけを、受付Snapshot、採用面、同じframeと既存のfiniteかつ非負なanchorEpsilon=eで分類する。`s = dot(planeNormal, anchorPosition) + planeDistance`がs>eなら正側、s<-eなら負側、-e<=s<=eなら正負各子へ1回ずつ継承する。無効値をOnPlaneへ分類しない。Convexのsupport・非交差継承・clip・内接削減・cook結果とは独立に配分し、各子OwnerはAnchorが一つでもあれば全体をStatic／Kinematicで固定し、なければ動的とする。実Hitと受付は19.1.7／7.6に従い、Anchorをsupportへ加えず、片側No-opでは集合を変更しない。

Anchorは固定判定と分裂時の配分に用いる論理点であり、接触・接着を証明しない。形状外の点を許容し、点位置による配分の結果として離れた部分の固定や浮遊、固定される側の変化を許容する。最近傍・包含探索、投影・位置補正、間接支持判定を行わず、Object全体、Sibling、祖先の集合や共有ShapeからAnchorを再収集・復活させない。

点分類はConvex切断・cookを待たず入力と採用面から先行でき、少数Anchorは同期実行してよい。投機結果は8章の対象authority、必要な既存Anchor世代、Sourceの現在集合、採用面とframeで採否する。未完了Work、不正入力・世代不一致と資源寿命は既存境界に従う。支持付き対象は19.5.1の初期リベース対象外のままとする。

ProvisionalとFinalは同じ受付Snapshot・採用面から得た正負子Ownerの配分を使い、Final handoffでも対応する子の集合を維持する。Shape共有・交換、内接削減・再cookからAnchorを再生成・再配分しない。直接Finalの場合も同じ配分を使う。集合の格納・共有・コピー方法は実装詳細とし、専用ID・frame型・対応表や追加の状態を要求しない。

#### 7.1.2 Final PhysicsとLogical Publication

正常成功には正負各1のFinal Physics Owner、7.2の各側の質量特性・形状構築条件、必要なAnchor、frame、cook、Actor／Shape／D6を成立させる。片側を成立させられない場合は一Owner成功や片側だけの公開に変換せずAbortする。Final handoffのpose／COM線速度／角速度と質量継承は7.2に従う。

正負2子ID、親・子・面・Sideを持つLogicalCutOperation、Final Owner参照、Anchor／frame、Hit／QueryとTemporary表示対応を未公開で準備する。安全な物理境界と同じMain Thread更新区間で、Final Physics Commit、Sourceの現在対象終了、Operationと2子公開、Hit／Query／Temporary追従先切替、Transaction終了、Pending CutからPhysics authorityを外す処理を行う。途中に新規受付、Query結果のLogical解決、他の所有変更、Renderer状態収集を挟まず、旧完全状態または新完全状態だけを観測させる。単一CPU命令のatomic storeは要求しない。

公開時に確定する各LogicalFragmentの生成元Operationと正負Sideは、既存台帳内の不変な関係として保持し、履歴件数に依存しないO(1)の照会で取得する。子の公開と同じ境界で関係を公開し、公開前の子は見せない。直接登録した根には生成元がなく、不正IDの照会も生成元なしを返す。生成元Operationが完了・終端しても関係を変えず、既存のID非再利用規則を維持する。保持形式は実装詳細とし、既存のTryGetOrigin利用経路をこの直接照会へ置き換え、照会ごとのOperation履歴走査を残さない。

Geometry Work、具体的Vertex／Index、実Cap、CutBoundaryRecord、GPU転送をPublication前提にしない。成功後は生存LogicalFragmentとFinal Physics Ownerが1対1となり、Geometry未完成でも各子を再切断・単独退役できる。親の現在対象終了は祖先Pending／Geometry Workの失効を意味せず、Geometry責務は4.5.6へ残す。

#### 7.1.3 Abort・Staleと単独退役

Provisional一式の構築不能、またはLogical Publication前にFinal Physicsを成立させられないと確定した場合はTransaction AbortとしてSourceを退役する。待機、PhysicsSplitTimeoutだけではAbortしない。同じTransactionのProvisionalについて既存Unity／物理所有／Constraint管理境界から継続不能異常が判明した場合もAbortし、成功後のFinal Ownerに同様の異常が判明した場合は対応LogicalFragmentを退役する。既存境界から明示されたnonfinite stateや実際のConstraint破綻を扱うが、包括的毎Step監視、速度Profile、Snapshot、復元・Kinematic Freezeを設けない。正常なD6 Limit到達は異常にしない。4章の共用Geometry／論理構築、絶対backing容量、必須Stencilの致命条件は共通Player終了を維持し、個別物理退役との相互Fallbackを作らない。

退役はPhase 4の共通低レベル処理とし、対象LogicalFragmentの生存性・Hit・Query・新規受付、通常／Temporary表示を終了し、Active Transactionがあれば終端する。当該対象へ未公開成果物をCommitせず、旧Final／Provisional／未公開Finalの当該所有Actor・Shape・システム所有D6をSceneから除去する。Geometry参照とRenderer登録を退役し、投入済みWork、CPU範囲、Leaseは読者と必要Stepの終了後に回収する。Published済みVBの回収だけは4.5.3の導入時期・条件に従う。Siblingへ質量・Anchor・Geometryを移送せず、親へ再合流しない。Source消失による質量消失・接触変化を許容し、旧物理の恒久採用や復旧を行わない。IDとCut履歴を再利用・書換えせず、履歴が資源を強く所有しない。

成功／Abort後の遅延回収は必要な小さいcleanup情報へ移管し、Transactionを延命しない。外部authority喪失は8章のStale回収であり、古いTransactionが現在のSourceや他の所有資源を退役させてはならない。

### 7.2 Convex Cut/Cookと運動継承

一つの生存Final Physics Ownerが持つCompound Convex集合を一つの処理単位とする。入力Physics ProxyのAsset／登録時Gateは維持し、Runtimeの1 Convex当たり頂点上限は`L = 128`を入力と最終出力へ適用する。Compound全体の合計上限ではない。これはRuntime入力・出力の規約であり、既存Assetの一括再生成は要求しない。

**実行分担。** Owner単位の分類、非交差ConvexのSide継承、交差Convexのplane clip、交点・切断面生成、必要な内接削減、質量特性の近似とCollider入力生成を、4.3のurgentなmanaged Unity Job内から呼ぶBurst static kernelへまとめ、MeshDataへ出力する。Main ThreadでUnity Meshへ適用した後、managed Jobで必要な`Physics.BakeMesh`を行い、既存の安全なMain Thread／Physics境界でFinal Commitする。この分担は受付済みPhysicsの新規投入を対象とし、投入済み投機は4.4に従って継続する。投機・任意追加分割の数値処理とBakeは4.3のBackground Poolへ置き、MainのMesh適用・採否・公開境界を共用する。数値処理の内部工程を必須の別Job・公開Stage・状態にしない。複数Ownerを外側でBatch Scheduleしてよく、Job型、配列layout、Mesh資源の保持形状は実装詳細とする。

**同時予約件数（2026-09-21）。**Physics Cut/Cookは、1つの切断が抱える資源——kernelのworst-case容量で確保したarena、Jobが報告に使う小配列、worst-case枚数分のwritable mesh dataとUnity Mesh——を**予約**として数え、**同時に予約を保持できるRequest数**に上限を置く。予約は取得から成果物の引渡し（またはCut終了）まで保持し、その区間は数値処理・MainのMesh適用・Bakeにまたがる。**これは件数の上限であって、固定のバイト上限ではない。**各Requestの規模は入力によって異なるため、件数だけで一定のメモリ量を保証しない。4.4の有限な資源使用は、この件数によって保つ。

**実行枠とは別の制限だが、無関係ではない。**実行側を縛るのは、Dispatcherの**waitingCapacity**（待機キューに置ける数）、各実行先の**capacity**（受理済みで未回収のWork数）、および**frame budget**（1フレームの投入・回収の機会数）である（4.4）。予約件数はこれらとは別の軸だが、**予約できなければ投入されない**ので、実効的な並行数にも影響する。

**Requestは互いに独立である。**arenaと作業用資源はRequestごとに持ち、ある切断の領域を別の切断が読み書きしない。したがって**独立Ownerは同時に予約を保持し、同時にDispatcherへ投入できる**。**一方がBakeの投入・完了を待っていても、その予約保持は別Ownerの数値処理投入を妨げない**——ただし他方が進むには、**残る予約枠・実行先の空き・frame予算・自身の予約に要する資源**が揃っていることが条件である。Physics側はworst-case予約のままとし、Geometry側の見積り・再試行方式は持ち込まない。入力の読取寿命、未公開成果物の所有権、取消・失敗時の一度だけの回収は変更しない。実行中の資源は早期に返さず、Workが回収された時点で返す。

**件数の既定値は置かない。**同時に許す量を知るのは構成根であり、黙って引き継がれた既定値は誰も選んでいない製品設定になる。上限到達時は従来どおり、後続Requestは予約を取らず未投入のまま待ち、解放後に再開する（拒否ではない）。

**現状（2026-09-21）。**PhysicsCutCookを組み立てる**構成根は未接続**であり、**製品値は未選定**である。この時点で行ったのは、既定値の撤去による**必須引数化**と、**予約数2での並行性の確認**（同時予約・同時Schedule、Bake完了回収の保留中の他Owner進行、逆順完了、取消と失敗それぞれの回収、上限到達時の待機と再開）までである。製品の並列度を変更したわけではない。

受付に必要なrobust support scanは7.6に従って同期実行してよい。同節で分割対象としたConvexだけを採用面d = 0で切り、その他は対応Sideへ未切断で継承する。既存Bake共有枠・Dispatcher・Work依存を使い、cut/cookの分担を理由に新しいSchedulerや公開状態を作らない。

**数値Kernel。** Phase 3.9で先行実装し、現在のCompound Convex B-rep、採用面と同じ局所frameのsigned distance・support分類、親質量等の必要値、およびcaller提供のscratch／output範囲を受ける。7.6で確定したdistance・分類を共用し、別の受付判定で置き換えない。正負B-rep、質量特性、実使用量と成否を返し、入力Convexから未切断継承先・正負出力への対応を取得できるものとする。対応は当該呼出し内の情報でよく、恒久Convex IDを要求しない。呼出側は数値Workの投入から実行完了まで入力B-rep・採用面・distance／support分類・親質量等を保持して不変とし、scratch／output範囲も有効に保つ。Kernelは予約外へ書かず、完了後に参照を保持しない。outputの後続利用中の保持・回収は既存の資源寿命に従う。Unity Object、資源取得、Schedule、公開・退役はKernelの外で扱う。具体的な型・field・layout・関数名は実装詳細とする。Phase 4では同じmanaged Job内で数値処理とMeshData出力を接続でき、分離を理由に別Job・永続中間成果物・引渡し状態を要求しない。

**容量。** 数値Workに使う同じ入力B-repのConvex数・頂点数・edge数等と実装表現上の上限から、Kernel実装側の容量式を同期評価してworst-caseのscratch／output必要量を算出する。呼出側が全必要範囲を予約し、その範囲を渡して数値WorkをScheduleする。容量照会では実clipや出力生成を行わない。half-edge、正負出力、補面、Polygon参照等を実際の表現に応じて含め、Lを中間scratch上限へ流用しない。予約した領域内で処理が完結する構成とし、容量式は実装時に導出して小さい境界Fixtureで確認する。Runtime Count pass、途中拡張、容量不足による再実行、部分出力公開は設けない。共有資源の一時的な不足は既存Pending、絶対backing容量の不成立は4.5.4に従い、予約未成立のWorkをScheduleしない。これはPhysics側の契約であり、表示VPの4.5.3の範囲予約・再実行を変更しない。

**形状の構築。** Runtime Physics Convexは全体としてfiniteで正体積の閉凸形状を構成する。2026-09-13の人間承認により、浮動小数点丸めに由来し、Kernelの局所predicate上で退化扱いとなるface／edgeや同位置に丸められた頂点を、それだけで失敗とはしない。閉鎖・向き整合のTopologyと定義される幾何量のfinite性を維持し、これらを含む採用B-repを次回切断入力として使用できるものとする。局所退化の除去・修復は要求しない。完成出力のfinite、面向き、閉性、凸性、重複、自己交差、包含を再走査するRuntime Validatorを設けない。Runtimeでは構築中の局所predicate、メモリ範囲、質量計算の成立と、既存境界で判明するcook／物理構築不成立を扱う。内部エラーの全検出は保証せず、検出保証のための専用Result・Reason・Buffer・Traceを作らない。

頂点数がLを超える出力だけを、通常clip結果の内側に収まるL以下の有効なConvexへ内接削減する。切断前ConvexをP、採用側半空間をH、通常clip結果をC、削減結果をQとして、`Q ⊆ C = P ∩ H ⊆ P`を構築条件とする。削減対象、削除順、頂点の移動・生成、局所再構成、頂点由来、決定性の範囲は実装詳細とする。L以下の有効なConvexを構成できなければ当該Physics処理を失敗とし、一般凸包再構築や別形状Fallbackで救済しない。採用した削減後B-repをcookと次回切断の基底とする。

Convex内部の識別は非公開の配列位置または実装handleでよい。形状処理に必要な入出力対応は維持し、Ownerの点Anchor配分は7.1に従って独立に行う。

**数値frameと物理への対応。** 数値精度のため、Compound中心近傍へ数値B-repの共通局所frameをrecenterするP1方針を採用する（2026-09-13人間承認）。Phase 3.9ではその共通局所frameの入力・出力で数値処理を実行・確認する。Phase 4でB-rep・採用面・OwnerのAnchor集合・Colliderの座標を対応付け、recenter前後で座標表現の丸め誤差を許容して同じworld形状を表し、Actorのpose・運動を維持して接続する。Cook用座標変換だけで数値B-repのrecenterを実施済みとはしない。具体的な変換方式・内部表現は実装詳細とし、専用frame型や数値上限Gateは追加しない。

**Final質量特性。** 切断受付時の親Rigidbody質量をSnapshotし、Final質量の正本とする。正負子の質量・重心・慣性は採用Convex集合から近似し、各Convexの体積を重複控除せず加算してよい。CompoundのBoolean Union、重複領域の厳密控除、表示Meshの体積積分、永続的なConvex別配分Metadataを要求しない。

正常分裂では、正負各Final Ownerの質量がfiniteかつ正で、正負子の質量合計が親質量に一致し、重心・慣性がfiniteかつSolverへ設定可能であることを要求する。計算方法、加算順、近似方式は実装詳細とし、成立しなければ当該Physics処理を失敗とする。最小質量やSiblingへの質量移送で補わない。次回切断では公開済みFinal OwnerのRigidbody質量を新しい親質量として使い、Provisionalの一時massからFinal正本を作り直さない。

**Provisional質量特性。** 一時質量は現在Source Actorの保守的OBBと今回のCut Planeから近似する。OBB正負体積の和がfiniteかつ正なら、その比で受付時の親質量を配分し、負側は親質量から正側質量を引いて求める。OBB体積が全0、非finite、演算不能なら等分する。center of massはclip済みOBBの近似重心、inertiaは保守的OBB／AABBの箱慣性とし、非finiteなら直前Actor inertiaを一時質量／親質量でscaleする。各子へfiniteな正質量とSolverへ設定可能な特性を成立させられなければ7.1のAbortへ進む。これらは短命なSolver用近似であり、Final正本には使用しない。

専用Physics Convexを持たない表示部分には独立質量を作らず、7.6に従って同Sideの物理所有者へ所属させる。表示部分の数を理由に質量を増減しない。

**完了と公開。** `Physics.BakeMesh`を含む既存境界からcook／Final構築不成立が判明した通常切断は、7.1のTransaction AbortとSource退役へ接続する。同じUnity Meshを複数Workから同時にBakeしない。完了後にSource生存性、Transaction authority、入力Physics、Cooking Profileと8章の世代契約を照合し、有効な正負Final Physicsだけを7.1で一体公開する。Stale成果物は適用せず回収し、現在のSourceを退役させない。7.9の任意処理は同じ数値Kernelと実行分担を再利用してよいが、失敗時は候補不採用・親維持とする。

- Parent ActorからProvisional Actorを初めて作る時だけ、速度継承の正本点を`FragmentRenderAnchor`とする。Source ActorのCOM線速度から`v_anchor = v_sourceCOM + omega_source x (anchor - COM_source)`を求め、`v_provisionalCOM = v_anchor + omega_source x (COM_provisional - anchor)`、`omega_provisional = omega_source`を設定し、切断命中時の表示Fragment poseとAnchor点速度を連続させる。Provisionalを省略して直接Finalへ分裂する場合も同じ初回分裂式を使用する。質量変更前後の運動量、角運動量、運動エネルギー保存は要求しない。

- 分離Impulseの強さは、適用対象となる各子Ownerの初回適用時の質量と、向きの情報を入力として決定し、各子Ownerへ適用する分離Impulseの大きさ（N·s）を表すスカラー値だけを返す。返す値は速度増分ではない。質量はProvisional経路ではその一時質量、直接Final経路ではFinal質量を使い、強さの決定のためにFinal完成を待たない。入力の「向き」が指す対象と基準座標は別途人間が指定し、実装担当の判断で確定しない。質量・向きと強さの対応はアート調整対象とし、初期の仮値を許容するが、実装内部の固定定数・固定計算式を製品値として確定せず、後から調整できる形にする。この算出は既存の適用方向を変更しない。UI、設定の保存形式、具体値・曲線・補間方法は本仕様では定めない。

- ProvisionalからFinal Colliderへのhandoffでは物理Actorを正本とし、ActorのWorld pose、COM線速度、角速度をそのまま維持して、Final Shape、center of mass、inertiaだけを同一Actorへ置換する。Render Anchorを維持するためのActor pose補正や、新COMに合わせた線速度変換を行わない。採用する自前B-repは由来Convex内に、分割したものは採用半空間内にも収まるよう本節の構築規則で生成し、本節の数値frame対応に従ってFragment Physics Frameへ配置する。recenterによる座標表現の丸め誤差は本節の数値frame条件に従う。19.5.1の採用Local Plane由来のFinalにも同じ構築条件とframe対応を適用し、命中時Snapshotまたは予測Pose／速度へActorを戻さない。Actorを予測Pose等へ移動して適用を成立させない。cookによる形状・接触差は7.3に従う。表示GeometryはActorへ従属し、local origin／frame差によりFinal Commit時に瞬間的な位置・姿勢差が出ても許容する。分離ImpulseはProvisional生成時に一度だけ加え、Final Commitで重ねて再適用しない。Provisionalを省略して直接Finalへ分裂する場合だけCommit時に分離Impulseを加える。Final Shape交換直後のSibling pairは既存の一時衝突抑止を使用できるが、外界とのGhost Contact履歴を理由にpose／velocityを巻き戻さない。

- Final handoffでの子OwnerのAnchor集合は7.1の配分を維持し、固定側のOffset／Impulseは0とする。

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

実装状況（2026-09-20追記、2026-09-21更新。上の目標仕様と既存の決定本文は変更しない）。物理切断の数値処理・cook、その成果物から作る正負Owner候補の構築、直接Final分裂についての物理・論理とFragment→Owner対応の公開接続、そのOwnerから表示の配置照会への接続、そしてProvisional一式の未公開構築を5単位で実装し、いずれもlocal mainへ統合した。**2026-09-21に6単位目としてProvisional公開を実装した（未統合）。**その範囲は下の「Provisional公開（2026-09-21）」による。詳細はここにまとめ、関係する節からは本項を参照する。**Selected面だけで成立する非Character・直接Final経路については、公開された正負が別々に動くとTemporary表示がそれに追従する。**Provisionalについては未公開の構築と回収までが成立しており、Provisional公開（Scene投入の切替）とProvisional→Final handoffは残る。Ignored集約の配置はその後f72f7d0で成立した（5.6の「実装状況」）。ただしHit／Queryの解決、Provisionalの公開とhandoff、Character、製品の構成根が残るため、7.1.2全体の一体公開は未完成である。

- **Cut／Cookの未公開成果物まで（74bbd8e）。** 一つのOwnerのConvex切断と、それが生成した形状のcookを、未公開の成果物まで接続する。数値処理とBakeは既存の共用dispatcherへ別々の仕事として投入し、Scheduleは各仕事のBeginの中で行う。入力の保持、Kernelへの容量問合せと予約、Mesh適用、成果物の引渡しはMainに残す。clip・内接削減・質量特性・生成分のMesh dataは一つのJobにまとめ、Bakeはmanagedのため別Jobとする。予約はKernel自身のworst-case照会に従って投入前に一括で取り、取れない切断と投入を断られた切断は未投入で待つ。ここに容量の拡大再試行はなく、Geometry側の再試行規則も持ち込まない。所有権は、平面が当たらなかったConvexが借用（入力のもの、既存のcook済み形状で、Mesh生成もcookもしない）、分割されたConvexの正負が生成（成果物自身のarenaと専用Mesh、7.3の一つのProfileで1回だけbake）である。成果物は各側の質量・重心・慣性、入力Convexとの対応、呼出側が渡したOwner frameへの変換を併せて持ち、破棄でarenaとMeshを返す。入力は所有しないため、借用部品を読むには呼出側の入力が成果物より長く生きる必要がある。予約は取消・終了・途中失敗のいずれでも一度だけ戻し、使用中のJobが終わる前には戻さない。完了印は、数値Jobが自分の本体の末尾へ到達したことと、Bakeが各Meshについて呼出しから復帰したことを言うだけで、cookされた形状の正しさは何も言わない（`Physics.BakeMesh`に成否の戻り値はなく、発明もしていない）。仕事の回収は例外時にも記録し、その例外はその投入の失敗として扱う。
- **正負Final Owner候補の未公開構築まで（2135bf4）。** 上の成果物から、各側1つのFinal Physics Ownerを未公開で構築する。1側はRigidbody 1つで、その側のConvexは生成分も継承分も同じRigidbody配下のCollider群（Compound）となり、島ごと・Convexごとの剛体は作らない。生成分は今回cookしたMeshを、継承分は対応する入力Convexの既存cook済みMeshをそのまま借用し、複製も再bakeもしない。Colliderには成果物が持つ同じProfileをMeshより先に与える。座標は、Owner配置（位置と回転。回転は一度だけ正規化し、Transformへ渡す値と重心・速度の計算に使う値を同一にする）と、数値局所frameからOwner frameへの変換を子オブジェクトの変換として与える形で対応させ、Mesh座標は書き換えない。剛体でない（scale・shear・鏡映を含む）frame変換は拒否する。質量特性はKernelの値を使い、重心と慣性を同じ変換でOwner frameへ移し、慣性はRigidbodyが受け取る主軸表現へ変換する（対称行列の対角化は再利用できる既存処理がないため本単位で実装した数値処理であり、本節が求める「Solverへ設定可能」を満たすための実装詳細である）。各側の質量が有限かつ正であること、正負の合計が受付時の親質量Snapshotに一致すること、重心が有限であること、主軸モーメントが有限かつ正であることを要求し、成立しなければ失敗とする（最小質量の代入もSibling間の移送もしない）。固定は7.1の既存Anchor配分をそのまま使って再分類せず、Anchorを受けた側をkinematicとして速度を与えず、他方へ本節の初回分裂の速度を設定する。候補はinactiveで作るため物理Scene・Hit／Queryへ入らず、両側が揃わなければ何も渡さない（途中失敗では既に作った側も破棄する）。inactiveなRigidbodyは質量以外（重心・慣性・速度）を保持しないため、決めた値は候補側に保持し、bodyへ書く経路を一つにまとめてある。**後続の公開処理では、有効化した後にその適用をもう一度行う必要がある。** 候補が所有するのは作った2つのオブジェクトとその構成要素だけで、生成Meshは成果物の、借用Meshは呼出側のものであり、解放順は候補が先・成果物が後になる。失敗は呼出側へ返すだけで、台帳のAbortやSource退役はここで行わない。これは直接Final分裂の候補構築であって、Provisional→Final handoff（現在Actorのpose・COM線速度・角速度を維持し、形状と質量特性だけを同じActorへ置換する処理）の実装ではなく、通常切断でProvisionalを省略する仕様変更でもない。
- **物理・論理とOwner対応の公開接続（cc168a6）。** 直接Final分裂について、上の候補を公開する。生存Fragmentがどの物理Ownerかという対応を、台帳が既に持つFragmentを鍵として保持し、Fragmentの再切断入力に使う対応を用意した。Hit／Queryの解決からこの対応への接続は未実装である。公開する対象のFragmentは当該Operationの記録から取り、呼出側が指定した場合は一致を要求する。切り出した入力Shapeが現在のOwnerのものであることも照合し、受付後に別Ownerへ差し替わった成果物は適用しない。判定順序は、台帳の既存判定（生存性・Active Operation・authority・Anchor準備と子2件分の領域確保）を先に通し、その後で現在Owner／Shapeの照合へ進む。台帳の判定は台帳内で共用し、外側へ複製しない。
  公開直前にSourceの現在の姿勢・COM線速度・角速度を読み、候補の配置と初回分裂の速度をそこから作り直す（質量・重心・慣性は数値局所frameの量なので変えない。Anchorを持つ側は固定のままで速度・Offsetを与えない）。通常の移動はStaleではない。分離Impulseは直接Final経路で一度だけ、自由な側にのみ、採用面法線の各側方向へ加える（値は呼出側が渡すもので、製品値は本節では定めない）。続いて同じMain更新区間で、候補の有効化と質量特性等の適用、台帳による論理公開、生成Meshの所有権移転、子2件のOwner登録、Sourceの物理対象の退役を行う。途中に物理Step、新規受付、Query解決、Renderer収集を挟まない。表示への切替は行わない（cc168a6時点の記述。表示の配置照会への接続は下の9993eceの項による。そこでも切替の区間はここと同じで、子2件の対応登録が表示の切替でもある）。
  資源の寿命は次のとおり。生成Meshの所有権は公開が成立した一点でのみ移り、成立しない呼出しは呼出側の所有のまま残す。継承Meshは供給元を共有保持し、使う側が全ていなくなったときに一度だけ返すため、Sourceの退役だけでは子が継承した形状を失わない。正負は別々に保持するので片側だけ退役できる。子の入力B-repは自分の1 bankへ複製して持つ（数値Kernelが1 bankを読むため）。Ownerの退役は、まず物理・Query対象から外し、その後に破棄と資源返却を行う二段とし、実行中のWorkが読む入力bankは、Ownerの退役とWork完了のうち遅い方まで保持する。
  失敗は区別する。公開前の物理不成立は7.1.1の通常の継続としてAbortへ送り、台帳経由でSourceを退役させる。台帳の拒否（Stale・非Active・Anchor未準備）はSourceを元のまま残し、Staleでは現在Sourceを退役させない。公開前の予期しない内部例外は、その呼出しが作ったものを返してから伝播させ、**通常の物理不成立へ読み替えない**。公開後の例外は、公開済みという事実を保ったまま伝播させ、登録済みOwnerの資源を未公開候補として解放しない。一般的なrollback機構は設けない。内部の型・field・試験用フックは実装詳細であり、設計契約として固定しない。
- **Owner配置から表示の配置照会への接続（9993ece）。** Selected面だけで成立する非Character・直接Final経路について、Fragment→Owner対応を5.6の配置照会へつなぐ。**対応はOwner自身が持つ**ので、その寿命はOwnerの寿命と一致し、別の対応表も管理機構も作らない。系譜の最初のOwnerを登録するときに、その系譜の表示GeometryがそのOwnerの座標でどこにあるかという**Geometry local→Owner localの対応を呼出側が明示する**。照会は、その時点のOwnerのworld変換とこの対応から**基準配置**を作って返す。
  **公開時に子へ継承する。**両側は公開時点のSourceの配置そのものへ置かれ、子ごとのOwner frameを建て直さないので、対応は値のまま両子へ渡る。祖先の登録時world配置から取り直さない。子2件の記録は公開の前に作るため、**切替は既存の公開更新区間**（子2件の対応登録→候補の引渡し→Sourceの物理退役）そのものであり、間に新しい段を設けない。置換済みの中間Fragmentは対応ごと消えるので、描画のためだけに残らない。退役・Stale・公開前Abortでも、対応はOwnerと一緒に消えるか一緒に残る。
  **返すのは基準配置だけである。**物理位置や分離Impulseをそこへ足さず、二重にも適用しない。**（2026-09-20追記：5.1の表示用Offsetと、Geometry Commitが各Geometryのframeへ畳み込んだ分は撤去されたので、表示側に足される項はもう無い。）**4.5.6でKernelの切断面を決める系譜→Geometry localの写像とも別物として扱う。表示は採用済みSnapshotを描画中に読み直さず、Ownerの移動・回転は**次のSnapshot収集**に現れる。
  **場にOwnerが無い場合とWithdraw済みの場合は、対応の有無によらず位置なしとして扱う**（古い位置には描かない）。これは、**使用中のOwnerが対応を持たないこと＝その表示は別の方法で構成されているという明示**とは区別する。照会側の列挙値や内部の保持方法は実装詳細であり、設計契約として固定しない。新しい上限・拒否条件・製品値は追加していない。
- **Provisional一式の未公開構築まで（f06f462）。** 非Character・非建物について、7.6の分類が確定した一つの切断の正負Provisional候補を未公開で構築し、破棄するまでを実装する。両Actor・共有Shape・Sibling D6はinactiveであり、Source、台帳、Registry、表示のいずれも変更しない。
  **形状は旧cooked Meshの配分である。**確定済みの7.6分類に従って各側が名指すConvexを決め、面が交差するConvexは切らずに両側が同じものを共有し、供給元の保持を取得する。明示的なConvex切断、Mesh複製、`Physics.BakeMesh`の呼出しは行わない。ColliderにはSourceと同じProfileをMeshより先に与える。**PhysX内部のcook回数は計測していない。**
  **一時質量特性は本節の近似である。**Source Actor frameに軸を合わせ、Shapeの配置を反映した全Convexを包む保守的な箱（fitではない）を今回の面で分け、体積比で受付時の親質量を配分して負側は親質量から正側を引く。体積を分けられない場合に親質量を等分することと、慣性が非finiteな場合に直前Actor inertiaを質量比でscaleすることは本節の規定どおりで、後者ではその慣性の主軸の向きも保持する。**体積が求まらない側のcenter of massを箱の中心で代えるのは、本節が「clip済みOBBの近似重心」と定めた場合に当てはまらない入力に対する今回の実装上の選択であり、本節が挙げた既定と一括にはしない。**さらに、質量はBodyへ渡すfloatでも有限かつ正であることを成立条件とし、clampや最小質量・上限値では補わない。**これらは短命なSolver用近似であり、Finalの質量正本とは区別する。**
  **運動と固定は既存の配分による。**7.1の確定済みAnchor配分をそのまま使い、固定側には速度を与えず、他方へ本節の初回分裂の速度を候補へ記録する。分離Impulseはこの単位では適用しない。
  **Sibling D6は7.1.1の設定を構築するまでである。**候補が所有するのは、この呼出しが作ったものと、そのために取得した保持だけであり、一度だけ回収する。構築途中の例外は、作ったものと保持を回収したうえで呼出側へ伝播する。内部の型・field・試験用の接合点・数値処理の細部は実装詳細であり、設計契約ではない。

確認の範囲（単位ごとに分け、他単位の証拠を流用しない。合成入力による確認である）。

- Cut／Cook（74bbd8e、記録：Phase4Physicsのcutcook-20260920）：最終版の関連試験55/55。数値は同じKernelを直接実行した結果と照合し、借用・生成の別、生成Meshが当該Profileで凸Colliderとして使えること、queue・投入先・frame予算が許さない間はScheduleされないこと、段階が一対一でdispatcherを空にして終わること、2件目のOwnerが予約を未投入で待つことを含む。追加した回帰は、Cutを省略して報告が全ゼロのまま返る場合と、CutのComplete()の後に置いたフックから例外を投げる場合の2つであり、Bake側の例外やComplete()自身が投げる場合を直接試したものではない。参考アセット（Megacityの非Character 1件）での1/1は、dispatcher接続の作り直しより前の実行で、その後は再実行していない。全EditModeは未実行。
- 正負Owner候補（2135bf4、記録：Phase4Physicsのowners-20260920）：最終版の関連試験57/57。分割と継承が混在するCompoundが各側1 Ownerになること、箱の半分の質量・重心・主軸モーメントが寸法から別に求めた値と一致すること、回した・動かした数値frameを回した・動かしたOwnerの中に置いてもColliderの頂点・重心・慣性が一致すること、同じ向きを表す長さの違うquaternionが同じOwnerを作ること、既知の主慣性と回転から作った非対角成分を持つ行列が出力から再構成できること、Anchorを受けた側が固定で他方が初回分裂の速度を持つこと、剛体でないframe・合わない親質量・欠けた継承形状が何も作らずに拒否されること、途中失敗と候補破棄が作ったものだけを回収して借用Meshを壊さないことを含む。質量特性については、候補を一時的に有効化して設定値を読み戻せることを1件で確認した。これは値がSolverへ設定可能であることの確認であって、物理Stepを回した動作確認ではない。後者は行っていない。全EditMode、参考アセットの実行、撮影は行っていない。
- 公開接続（cc168a6、記録：Phase4Physicsのpublication-20260920）：最終版の関連試験97/97。一つの分裂が通り、子2件がLive・SourceがReplacedとなって対応が子のものになり、実Bodyが質量・重心・慣性を保持すること。候補構築後にSourceが移動・回転した場合に公開時点の姿勢へ置かれ、その時点の運動から初回分裂の速度が決まること。Anchorを受けた側が固定・静止で、他方だけが分離Impulseを一度受けること。Anchor未準備、Operationの記録と異なるFragmentの指定、台帳へ知らせずに差し替えたOwnerが、いずれも物理を変えずに拒否されること。所有権と借用資源の寿命（Sourceの退役後も継承Meshが生存、片側退役でも他方が無事、最後の側の退役で初めて成果物が一度だけ返る）。Ownerの退役で、破棄の前にQueryから外れること。切断を投入した後にOwnerを退役させても入力bankが保持され、Work回収後に一度だけ返ること。Geometryが無いまま公開子を再切断でき、その入力が子の現在Physics（自分のbankのConvexと現在の質量）であること。限定したQuery（OverlapBox）が2つのOwnerだけを見つけ、限定した1ステップで固定側が動かず自由側が動くこと。
  追加した2件は、Ownerを差し替えてNoteOwnershipChangedを呼んだ場合にOperationがStaleとして一度だけ終端し予算が戻ること（2回目の呼出しはNotActiveで予算は二重に戻らない）と、切替完了直後に注入した例外が通常の物理不成立や公開前Abortへ読み替えられずそのまま伝播することである。**切替途中の各処理の例外を直接検証したものではない。** 全EditMode、参考アセットの実行、撮影、XR、統合後の追加検証は行っていない。

- Owner配置から表示への接続（9993ece、記録：Phase4Physicsのownerfollow-20260920）：最終の関連試験**202/202**。追加8件と既存2件への補強のうち、**切断・公開を伴うケースでは実の凸切断・cook・本節の公開処理を通す**。**Owner不在・対応なし・Withdraw済みの照会確認は、登録したOwnerに対して直接行うもので、切断・cook・公開を通していない。**期待値はいずれも照会の答えを参照せず、試験が置いたOwnerのtransformと与えた対応から別に組み立てている。内容は、候補構築後にSourceが移動・回転してもPending表示と公開後の子が登録時の位置へ戻らないこと（基準配置だけでなく分離込みの描画位置で見る）、正負が独立に移動・回転したとき本体・clip半空間・Cap平面・外向き法線・Cap頂点がそれぞれのOwnerに追従すること、Anchor固定側が分離を取らずに追従すること、Geometry localとOwner localが異なる配置で分離が消失も二重適用もしないこと、A→Bの再切断で置換済み中間Fragmentの対応が無くなり生存子孫が追従を続けること、台帳拒否で対応が増減せず片側退役が他方を終わらせないこと、Owner不在・Withdraw済み・対応なしの区別、収集後にOwnerを動かしても採用済みSnapshotが変わらず次の収集で更新されること、Stale後に現在Ownerを残して対象境界が描画から外れること、物理不成立のAbort後に表示がそのFragmentを描かなくなることである。
  **確認経路を区別する。**A→Bの祖先Geometry Commitの試験は、表示Geometryを**同期で切って製品の`TryCommitCut`**を通したものである。非XRの画像は**実のCut DAGと非同期Workを経由**した別経路の証拠であり、混ぜない。
  非XR画像（単眼、1 sample、D3D11。実の凸切断・cook・本節の公開を通し、側を動かすとはOwnerのtransformを動かすことである）は2ケース。独立移動・回転を、照会を使わず同じ配置へ手で置いた参照と比べて**色の相違0/262144**。Geometry Commit前後で**色0/262144、被覆は一致**（両方129074、片方だけ0・0）。**生Depthは一致していない**：独立配置の比較で48,868画素（最大9.313e-10）、Commit前後で39,017画素（最大3.846e-07）、片側別は15,641（最大3.846e-07）と23,376（最大4.075e-08）が相違し、いずれも1e-5超は0画素である。色・被覆の集計と生Depthの集計は分けて扱い、**Depth差の原因は未確定**として記録する。Depthの完全一致とは書かず、許容差も品質例外も広げていない。
  **この画像は、その後に入れたWithdraw判定順の修正より前の実行**であり、修正後の照会契約を確認したものではない。撮影時点の表示経路（追従・実の公開・実のCommit）は変わっていない。再撮影と、統合後の追加検証は行っていない。

**保守的な箱の取得（2026-09-21）。**この箱は**Shape自身が保持する**。

- **Convex単位で保持し、継承する。**保守的なBoundsは**Convexごとに**持ち、**そのConvexが系に入った場所で一度だけ確定**して、以後はShapeからShapeへ**引き継ぐ**。Shapeの箱は、そのShapeが採用したConvexのBoundsの**集約**である。**Boundsの取得・集約について言えば、Shapeを構成する処理は渡されたBoundsを集約するだけで頂点を読まない**（これはBoundsのための走査ではない。**B-rep頂点の複製はauthored Shapeを構成するときだけ**であり、側のShapeは複製せず元のbankを借用する。2026-09-22の採用範囲を参照）。
  - **Authored（外部由来）**：入口検証でその頂点を走査する。**その一巡の結果**（Collider boundsと実頂点の和）をそのConvexのBoundsとする。走査はここだけである。
  - **生成Convex**：Jobが Mesh を書きながら測った min/max を成果物に載せて運ぶ。Mainで測り直さない。Mesh 自身の `bounds` は同じ数値を中心と大きさのfloat対に通したもので**内側へ丸まりうる**ため、Jobの測定値の側を採り、Collider boundsとは和をとる（物理が触れるのはColliderである）。
  - **継承Convex・Provisionalの部分集合**：対応するBoundsを**そのまま引き継ぐ**。
- **座標系。**Shapeの**局所frame**（`LocalToOwner`の変換元、すなわちB-rep頂点と同じ系）。
- **生成・更新の時期。**ConvexのBoundsはそのConvexが系に入った時、Shapeの箱はShapeを構成する時に、それぞれ一度だけ確定する。Shapeの**構成（どのConvexか）とShape内配置**はShapeの生存中に変わらないため、再計算しない。構成や配置が違えばそれは**別のShape**であり、その時に自分の箱を持つ。部分集合Shape・共有Shapeも同じ経路で構成されるので同じ扱いになる。**Provisionalの構築は正負それぞれのShapeを作るが、そこでBoundsのために頂点を読むことはない**（**各側はB-repを複製せず、元のbankをそのまま借用する**。2026-09-22の採用範囲を参照）。
- **Ownerのworld移動・回転では再計算しない。**世界での位置はこの箱の一部ではない。
- **Provisional構築時に毎回行うのは、その箱の8頂点を`LocalToOwner`でActor frameへ移し、軸並行に包み直すことだけ**である。回転を含むframeでは、頂点を走査した場合より箱は緩くなりうる。Source形状を確実に包む限りこれを許容し、主軸探索や最小体積OBBは行わない。
- **入口検証。**外部由来のMeshを受け取る唯一の経路（authored Shapeの構成）で、**各Meshのboundsが対応Convexを覆っているか**を確認する。判定には**箱の大きさに対する相対の許容差**があり、**その範囲に収まる僅かなはみ出しは受理する**（floatのboundsがそれ自身の頂点より内側へ丸まりうるため）。**受理と、保持するBoundsは別である**——保持するBoundsには**実頂点を含める**ので、受理された僅かなはみ出しも保存箱の外には出ない。許容差を超えるはみ出しは拒否する。これは**入力の妥当性の検査**であって、保持する箱の安全性はこれに依存しない。**座標系の食い違いをすべて検出できるわけではない**——別座標系でも十分大きい箱なら覆ってしまう。切断が作ったShapeはこの検査を行わない。**Provisional構築のたびに検証を繰り返さない。**

この変更は箱の取得・保持・更新に限る。箱の分割、体積比による質量配分、重心・慣性の近似、fallback条件と主軸保持、質量の表現可能性判定はいずれも変更しない。

- Provisional一式の未公開構築（f06f462、記録：Phase4Physicsのprovisional-build-20260920）：最終の関連試験**95/95**。これは新規16件を含む一回の結果であり、過程の9/9・72/72・94/94とは合算しない。確認したのは、一時質量の数値（対称・非対称・非軸平行の各切断について、各側の質量と重心を別に求めた閉形式と照合する）、候補が保持する記録値、D6の設定値、両側が同じMeshオブジェクトを共有すること、入力不備の拒否、未公開のままでの保持と破棄、構築途中の例外での回収である。**平板・線分・極端な数値といった診断用の入力を含む。**
  この単位では次を成立させていない。**実Bodyへの値の適用、Solverの拘束動作、Sibling衝突抑止の実動作**（両Actorをinactiveのまま物理Sceneへ入れず、Stepもしていない）。**公開後のLease、および必要なPhysics Stepを跨ぐ資源寿命**（一度もSceneへ入れない候補の取得・解放だけであり、仮の待ち時間やStep数も置いていない）。**Provisional公開、Final handoff、Hit／QueryとTemporary表示への接続。**建物World D6、Character、自動駆動と製品の構成根。全数・撮影・XR・Player・性能、統合後の追加検証。これらが残ることは、過去に別の経路・別の条件で確認済みの項目を未確認へ戻す意味ではなく、過去の画像を今回の証拠に流用する根拠にもならない。

Provisional→Final handoff（2026-09-21）。公開済み Provisional 対の 2 Actor を**そのまま**Final へ移す実装。

**同じ Actor で移行する。**正負それぞれの既存 Actor・Rigidbody・World 上の位置は変えず、その上に載っているもの——Collider と質量特性——を入れ替える。handoff 直前の **pose・COM 線速度・角速度をそのまま維持**し、Render Anchor への位置補正も、新 COM に合わせた速度変換も、受付時姿勢への巻戻しもしない。分離 Impulse は再適用しない（Provisional 公開時に一度）。Anchor 配分は受付時に確定したものを子が継ぐ（再配分しない）。Sibling D6 は対とともに終了し、運動補償は入れない。Final の質量・重心・慣性は**直接 Final と同じ規則**（`PhysicsOwnerBuilder` の既存計算を別の入口から使う）。直接 Final の経路は変更していない。

**親質量は受付時の Snapshot である。**Final 質量の照合元は、その切断が受け付けられた時点で Source の Rigidbody から一度読んだ値であり、要求の所有記録（`ProvisionalCutTransaction.ParentMass`）が持つ。一時対の 2 Body を足し戻して親質量とはしない——一時質量は箱近似の分割と float 丸めを経ており、公開後に Body へ触れたものが入り込むため、Final 成果物の妥当性判定の基準にできない。同じ Snapshot を一時分割と Final 照合の両方が使う。

**準備と公開境界を分ける。**公開済み構成を壊す前に、台帳の `PreparePublication`、正負の Final 質量特性、**正負両側の Final Collider（Actor 上に生成・Cook 済みで、有効化していない）**、正負の Final Shape（借用部分をこの時点で読み、Final 側が自分の保持を取る）、対応の枠を用意する。失敗しうるものはすべてこの準備に入るため、**負側の準備が失敗しても正側は変更されていない**。準備中の失敗では未公開の準備物（生成した Collider・Shape）だけを回収し、公開済み対はそのまま立っている。Collider は **Component を作った直後に回収対象へ登録**してから設定する：設定（Cook）で例外になった Component も、必ず外される側に入る。

切替区間（Main Thread・物理 Step 外、間に受付・Query 解決・収集を挟まない）で、**対が Final Shape を受け取る**（同時に一時 Shape が返る）→ 両 Actor の Final 化 → 台帳の論理正負子公開 → 各子を Final Owner として登録 → **対を対応から外す（Actor は破棄しない）＋成果物の所有権移転** → 旧 Source の Owner を退役、を一体で行う。

**切替中に使い始めた Shape は対が持つ。**Actor がその上に立つ前に対へ渡すので、切替途中で例外になっても、その Shape は「公開済み対の通常の終了」（`EndProvisional`）で Actor と一緒に回収される——呼出しのローカル変数だけに残ることはなく、この呼出し自身は切替以降 Dispose をしない。成功時は Actor とともに子の Owner へ移り、対は返さない。**成果物の所有権移転は対を対応から外すのと同じ点**に置いてあり、「その切断にまだ対があるか」と「成果物がまだ呼出側のものか」が同じ問いになる——切替の前後を判別する呼出側は対応に尋ねればよい。1 Actor の Final 化は**置換対象の旧 Collider の無効化 → Shape Frame を成果物の数値 local frame へ → 準備済み Collider の有効化 → 置換対象の破棄要求**の順で、**無効化が破棄より先**である：Play 時の破棄は更新ループ後に遅延するため、破棄要求だけでは旧 Collider が同じフレームの Query に答え続ける。**流用する Collider はこの順序に入らない**——側がすでに持つ Collider が、その部分の最終 Mesh をそのまま載せ、同じ cooking profile で、凸で、有効で、切替が設定する Shape Frame の姿勢が現在の姿勢と同じであれば、その Collider は無効化も破棄もされず切替の間ずっと答え続ける（2026-09-22 の採用範囲）。

対の終了（`EndProvisional`、Actor を破棄する）と**所有権移転（`TryHandOverProvisional`、Actor を渡す）を別の操作として分けた**のが最小の変更である。Geometry 切断・Commit は待たず、物理公開で Geometry 責務の未完了枠を返すこともしない（台帳の Operation は公開後も終えない）。

**Actor から論理子への解決。**対応は Body から fragment への逆引きを持ち、Provisional の間は両 Actor が「まだ分かれていない Source」とその側（±1）を答え、handoff 後は**各 Actor が自分がなった子**を答える（側は 0）。退役した子の Actor は何も答えない。Hit 検出そのものはここにはない。

**駆動への接続。**B の `ProvisionalCutDriver` が成果物を回収したその更新の中で handoff を試みる（呼出し順だけで翌フレームへ送らない）。適用前に台帳・authority・所有構成・Cooking Profile を照合する。**通常の Actor 移動は Stale にしない**。失敗の区別：台帳の拒否（Stale を含む）は物理を変えず、**Stale では現在の Source を退役させない**／Final 構築不能は DESIGN 7.1.1 の通常の Abort へ／準備中の例外は作ったものを戻して伝播し、**切替開始後の例外は Scene が使用している資源（Final Shape が持つ Mesh 保持）を未公開資源として返さない**。

**公開成立後に例外が出た場合**も、論理子・Final Owner・成果物の所有権移転はすでに済んでいる。駆動側は対応に対を尋ねてそれを判別し、**記録を「成果物を所有していない」状態へ合わせてから**例外を伝播する——そうしないと、後の終了処理が Final 子の Collider が使用中の Mesh を直接破棄する経路が残る。

入力 Shape の保持は、借用部分の最後の読取りと Final 側の保持取得が終わってから返す。handoff 呼出し自身が成果物に取る保持は、成功・不採用・取消・失敗のいずれの経路でも**一度だけ**返す（`finally`）。所有権が移っていなければ返しても何も解放されず、移っていれば**最後の子が退役したときに**生成 Mesh と成果物が解放される。

**確認**は EditMode の製品更新入口経由で、成功（同じ Rigidbody で正負子が公開される）、運動維持（移動・回転・速度を与えても pose と COM 線速度・角速度が維持され、質量特性だけ Final になる。Impulse の二重適用なし）、対応切替（旧 Source の Provisional 照会が Missing、各子が自分で答え、実表示の収集が 2 つになる）、寿命（旧 Collider と旧 Owner が消え、Final Collider が残り、D6 が終了し、借用 Mesh の保持が Final 側へ移って最後に一度だけ返る）、**生成 Mesh の最終回収**（子を 1 つ退役させても返らず、最後の子の退役で返る）、**片側準備失敗**（負側の準備を失敗させると正側の Collider・Shape Frame は同一のまま、公開なし、対はそのまま）、**公開後例外**（子は公開されたまま、記録は成果物を持たず、以後の終了処理が子の Mesh を壊さない）、Stale（適用せず、現在 Source を退役させない）、継続利用（公開した子をそのまま受付へ渡せ、兄弟は不変）、**Actor→論理子の解決**。後の 3 つは、**その欠陥を製品コードへ戻した対照実行でその試験だけが落ちる**ことを確かめてある。

**切替途中の例外**は EditMode で 1 件：両側を Final 化した後・論理公開の前で例外にすると、公開は起きず対は対応に残り、**対が持つ Shape は切替が使い始めた新しい方**で、Actor が立っている間は返らない（一時 Shape はその時点で返っている）。明示終了すると、その Shape・成果物・入力保持が一度ずつ返り、生成 Mesh は破棄され、外から来た authored Mesh は解放されない。この試験も、新しい Shape を対へ渡さない旧形へ戻した対照でその 1 件だけが落ちることを確かめてある。

**Play 時**は小さい PlayMode 試験で、handoff が返った時点で旧 Collider は破棄待ちのまま**どれも有効でなく**、Final Collider だけが有効であること、フレーム経過後も両 Actor が立っており旧 Collider・Sibling D6 が残らないこと、各 Actor が自分の子へ解決することを確認した。シミュレーションの Step・性能・XR は対象外である。

**残件（現在）。**Hit 検出そのもの（Actor→論理子の解決だけがここにある）、Scene 常設の構成根、建物 World D6、Character、分離 Impulse の算出式と「向き」、XR・性能測定。**A・B の時点で残件だった handoff は、この単位で成立した**——A の「Final handoff は残る」、B の「保持した成果物の行き先」はこの実装が受けている。handoff の成立は Phase 全体の完成を意味しない。

CutWorldSandbox の IL2CPP Player 確認（2026-09-22）。接続済みの切断経路が Editor の外でも成立することを、**Windows x64・IL2CPP・Development Player** で確認した。性能評価でも全試験の Player 移植でもない。

**build 入口。**対象 Scene（`Assets/Scenes/CutWorldSandbox.unity`）だけを明示する最小の入口をメニューと `-executeMethod` の双方から再実行できる形で用意した。**設定は読み取って記録するだけで変更しない**：backend が IL2CPP でなければ build せずに止まる。実設定は build ログに残る（`backend=IL2CPP target=StandaloneWindows64 il2cppConfiguration=Release il2cppCodeGeneration=OptimizeSpeed stripping=Minimal apiCompatibility=NET_Standard_2_0 graphicsApis=Direct3D11 autoGraphicsApi=False`）。Player・build ログ・Player ログ・画像はいずれもリポジトリ外に出す。

**Editor では見えなかった 2 つの不成立要因。**①`Mesh.MeshData.SetVertexBufferParams` の `params VertexAttributeDescriptor[]` は呼出しごとに managed 配列を確保するため、Burst コンパイル対象の cook job で BC1028 になる。**Editor では試験 1 件の失敗で済むが、Player build はこれで止まる。**同じ指定を `NativeArray<VertexAttributeDescriptor>`（`Allocator.Temp`）で渡す 1 か所のヘルパにまとめて解消した。②stencil の 3 shader は `Shader.Find` で名前から探されるだけで、どのアセットからも参照されない。Player build には収録されず、`Shader.Find` が null を返して**表示の作成が失敗し、DESIGN 4 の共通 Player 終了 latch が起動直後に Player を終わらせる**。その 3 本だけを Always Included Shaders に加えて解消した。link.xml の包括追加、stripping の停止、Mono への切替はしていない。

**確認した経路**（実 Player、ウィンドウ表示、`-nographics` なし）。①初期 body が**表示 material 自身の色で**描かれる、②切断要求が handoff を経て実 Geometry Commit に達する（**段階値はログで確認**し、画像から推定しない）、実 Actor を離すと正負の子それぞれと断面が見える、③公開された子を既存の入口から再切断でき、Physics・Geometry・表示まで進む、④通常終了で `IsReleased`／`IsDrained`／表示の破棄に到達する（**プロセスが閉じたことを資源回収の証拠にしない**）。**Provisional の対が立っていたのは 2 フレーム**で、その画像は取得していないため、**Provisional の見え方を Player で確認したとは書かない**。

**確認していない範囲。**Hit 検出、XR、性能、Shadow／Depth／Stencil の全面検証、Scene unload・強制終了時の回収保証、手操作（Space／C／E）での Player 確認。

Phase 4.1 固定費削減の採用範囲（2026-09-22）。切断経路の Main 固定費について、この単位で採用した変更と、その成立範囲だけを記す。測定の道具立て（区間 timer・API 台帳・Player の測定 driver・harness）と、未採用の描画保持は製品コードに含めない。

- **Extent（走査の撤去）。**表示 Geometry の参照頂点範囲と bounds は、**その Geometry を作った側が記録する**：cut では Kernel が出力 index を書く各所（通常三角形・lone 三角形の分割・cap）で range ごとの min/max と側ごとの参照 lo/hi を積み、Main は O(range) の変換と記録だけを行う。登録経路（配列 append・Mesh append）は登録時に一度測って記録する。**描画は記録を読むだけで、index を走査しない**。記録のない Geometry は描画側が拒否する（測り直さない）。公開入口は bounds を必須とし、bounds なし・参照範囲不正の commit は公開せずに false を返す。
- **OneBlock／Borrow。**authored Shape は自分の 1 block に B-rep を複製する（従来どおり）。**側の Shape は複製せず、各 Convex が元の bank をそのまま指す**。Kernel は Convex ごとの bank を読む。Shape・分類・arena の native 配列は、それぞれ 16 byte 整列の 1 block にまとめる。寿命は 3 つの計数で独立に決まる：Shape 自身の終了、Work の使用、**block の借用者**。借用者は block の**究極の所有者**を保持し、Mesh は借用者が自分で保持するので、所有者が Mesh 保持を先に手放しても Mesh は残る。
- **Final Collider の流用。**借用 part については、**その part の入力 Convex に対応する**側の Collider を流用する。対応は側の Shape が持つ：Provisional の側は「自分の Convex i は元の Shape の Convex convexes[i] である」という記録を構築時に 1 度だけ書き、側の Collider は Convex と同じ順で作られるので、part の `inputConvex` から Collider が 1 度の索引で決まる（同じ Mesh を持つ Collider を探すことはしない。Mesh が同じでも別の part は別の Convex である）。その Collider が同じ Mesh・同じ cooking profile・凸・有効で、切替が設定する Shape Frame の姿勢が現在と同じであれば、そのまま最終集合に入れる。**置換対象だけ**を無効化し、切替後に破棄する。準備の失敗は**この呼出しが作ったものだけ**を戻し、流用した Collider は側のまま残す。
  **走査回数。**準備は入力 Convex 数の表を 1 度初期化し、側の Convex を 1 度走って「入力 Convex → 側の Collider 番号」を書き、各 part はその 1 要素を読んで取ったら消す（同じ Convex を二度流用できない）。切替は側の Collider を、準備が書いた保持フラグを見ながら 2 度走るだけで、集合に含まれるかを尋ねない。したがって準備と切替を合わせて各 part・各 Collider を定数回ずつ扱う（Convex 数に対する二乗はない）。
- **scratch のゼロ初期化省略。**非同期 Geometry 切断の kernel scratch は `UninitializedMemory` で確保する。Kernel と cap は読む領域を必ず先に書く（明示初期化か、計数の範囲内の書込み）ので、ゼロ値に依存する領域はない。**同期経路は従来どおり初期化して確保**し、独立した比較基準として残す。Worker 側に初期化を足してはいない。
- **小修正。**Provisional 側 Actor の姿勢設定を生成時に行わない（build と publish の Reposition は途中状態が要るので残す）。Final handoff は質量と運動だけを書き、publication が同じ Actor に設定済みの自動質量・kinematic フラグを再設定しない。Collider 準備の一時 list を減らす。

**更新順による遅延削減（上の CPU 固定費削減とは別）。**`Update` と `LateUpdate` が使うフレーム源を構成根の `CurrentFrame` に統一し、`LateUpdate` では表示収集の**前に**同じフレーム id でもう一度 frame を進める。同じ id なので予算は補充されず、frame はその frame に与えられた分だけを使う。この 2 度目の turn は**完了の照会と回収を行う**：投入した仕事が更新側の turn の終了後に終わっていれば、次のフレームの更新を待たずに同じフレームで回収される。**完了待ちの busy polling も強制完了も追加しない**ので、`LateUpdate` までに終わっていない仕事はそのまま翌フレームへ持ち越す——待ちがなくなるわけではない（turn は何も動かなくなった時点で終わり、記録した run では、更新側の turn が終わった時点で投入した work はまだ戻っていなかった）。Sandbox の probe は driver より前（実行順 −150）に置き、同じフレームの driver 更新で受付が取り上げられるようにした——これは sandbox の都合であり、実際の Hit 経路がどこから要求するかを決めるものではない。

**成立範囲（版を区別する）。**採用したこのコードで確認したのは Editor の**関連 EditMode 1061/1061・切断系 PlayMode 16/16**である。**性能としては、Character（19 convex）の Geometry 切断 pump が約 70 µs 減ったことだけが確認できている**（scratch のゼロ初期化省略。Windows x64 IL2CPP Development Player、正順・反転の比較）が、**これは整理前の計測版（区間 timer・台帳・測定 driver を含む版）での比較結果であり、採用コードを再 build して測り直したものではない**。**箱（1 convex）の切断 Main 約 0.31 ms は改善していない。**未解決のまま残すもの：一呼出しあたり約 100 µs の膨張の原因、遅延破棄の実費用（較正からの参考推定のみ）、間欠失敗、空 dispatch 約 15 µs、Collider 流用の照合が Convex 数に対して二乗になり得ること。

最小 Scene・カメラ描画の接続（2026-09-21）。構成根までつながった切断経路を、Scene を再生すると実際に画面へ描かれる状態にした。

**Scene と設定。**`Assets/Scenes/CutWorldSandbox.unity`（Build Settings 登録済み）に、`CutWorldRoot`＋明示した `CutWorldProfile` アセット、表示 material、描くカメラ、床、代表の切断対象と確認用入力を置く。Scene は Editor のメニュー（`Zantetsu/Build the cut world sandbox scene`）から**同じ値で作り直せる**。既存 Sandbox Scene は変更していない。**Scene を開いて Play するだけで再現**でき、試験が非公開 field を書き換えなければ起動しない構成にはしていない。確認用入力は既存の受付へ要求を渡すだけで、Hit 検出の代替も操作 UI 基盤も作らない。

**カメラ描画。**`CutWorldCameraDrawing` が、与えられたカメラを表示へ登録し、**そのカメラの描画開始時**（`RenderPipelineManager.beginCameraRendering`）に `TryPrepareCamera` → `VpLogicalCutDisplay.Render(layer, camera)` を 1 回だけ呼ぶ。**収集は製品の LateUpdate、描画はその結果を使う**。**この位置にしたのは実測による**：本プロジェクト（Unity 6000.3.22f1・URP 17.3・D3D11）で、**この表示の呼出しにカメラを指定した場合**、同じ命令を Update／LateUpdate で登録すると記録上はすべて描画済みでも画素が 1 つも出ず、`beginCameraRendering` で登録すると出ることを測定した。これは**この構成・この呼出しについての観測**であり、Unity の描画 API 一般や他バージョン・他パイプライン、カメラを指定しない呼出しについては測定していない。表示は stencil 作業をカメラ単位で持つためカメラを指定する必要があり、指定を外す（全カメラ描画）選択は採らない。カメラ側から受付・Commit・Snapshot 構造の再評価は行わず、同じカメラに二重描画しない。表示が開いていないフレーム（`IsFrameOpen` が false）は何も描かず、それは正常な状態である。無効化・通常終了の後は、破棄済みの表示・buffer に触れない。material・Stencil・Depth・Shadow は既存契約のままで、新しい Renderer Framework も汎用カメラ管理も作っていない。**今回対応するカメラ構成は、単一カメラ・単一 layer・SolidColor の URP 既定**である。

**断面の見せ方。**表示専用 Offset は追加していない。断面を露出させる確認は実 Actor の移動・回転で行い、Scene が渡す分離 Impulse は**確認用の値**として製品の未決の算出式・向きと区別している。人工的な隙間も、見えない断面の赤画素も要求しない。

**確認**（PlayMode、実 Scene・実カメラ、実画素）。**対象はその色で数える**：床とクリア色はいずれも灰色なので、表示 material に混ざらない色を与え（側面＝青、端面＝橙）、切断面は表示自身の debug 色（実 Cap＝緑、Provisional の面＝赤）で数える。**「背景と違う画素」という数え方はしない**（床だけでも通ってしまう）。見たのは、①切断前に body がその Actor の位置に自分の色で描かれ、何も無い場所には無く、Cap も Provisional の面も 0 画素であること、②受付のフレームに Provisional の対が立ち、そのフレームの記録（観測部品が収集の後に書き取る）と**同じフレームの画像**に対象の画素があること、③ handoff と Geometry Commit を跨ぐ全フレームで対象の画素が 0 にならず、**Commit の後に収集が settle したフレーム**について、そのフレームの記録（Committed・対は立たない・body は描かれない・子がそれぞれ 1 断片）と**そのフレームの画像**（記録した子の位置に対象の画素がある）が対応すること、④ Commit 後に一方の子を実際に動かすと**各子の断面それぞれに Cap が出**、**断面の直外側**（同じ高さの体の真横と各子の反対側の端）には Cap が 1 画素も無く、離れた隙間には対象の画素が無いこと（Cap の漏れも旧表示の残留も無い）、⑤ Final の回収を保持して Provisional の期間を延ばした状態でも、同じ判定で各側の断面が Provisional の色で描かれ、直外側には出ないこと、⑥子の再切断後も描かれていること、⑦通常終了では受付と描画が閉じ、**通常フレームだけで**回収と解放に到達し、以後対象の画素が 0 になること。

**確認していない範囲。**Shadow、生 Depth、Stencil buffer の内容そのもの、XR、性能。**描画全体を検証したとは書かない。**Scene unload・アプリ終了時の回収保証も、この単位では完成扱いにしない。

最小構成根と PlayMode 自動駆動の接続（2026-09-21）。これまで試験側で組み立てていた部品を、製品側の構成根 1 つ（`CutWorldRoot` + `CutWorldProfile`）で結び、Unity の通常の Update／LateUpdate だけで 受付 → Provisional 公開 → Final handoff → 表示 Geometry Commit → 子の再切断 まで進む状態にした。新しい Scheduler・汎用 DI・独自 PlayerLoop は追加していない。

**組み立てと所有者。**`CutWorldRoot`（MonoBehaviour、実行順 −200）が `Awake` で台帳・`PhysicsOwnerRegistry`・配置照会・storage・参照表・表示・dispatcher と 3 つの destination・`SharedWorkFrame`・`PhysicsCutCook`・`CutDag`（実 Commit は `VpDisplayGeometryCommit`、失敗通知は root 自身）・`ProvisionalCutDriver` を生成し、driver を bind する。**駆動はしない**：一つのものが駆動し、それは driver（Unity の Update／LateUpdate）である。構成根は「作る」「登録する」「終える」だけを行い、二重駆動しない。body の登録は `TryAddBody` 1 呼出しで、初期 Physics Shape・基準 Geometry・Fragment・表示登録を同時に結ぶ。拒否しうる表示登録を先に行い、拒否されたら発行した Fragment を退役させて**何も登録されていない状態で false を返す**（半登録を残さない）。成功した body の Object は**この世界のもの**になり、その Fragment の退役（＝その body を置き換える切断）で対応が破棄する。**2 つの frame 写像を別々に受け取る**：`lineageToGeometryLocal`（系統の論理 frame → Geometry 座標。カーネルの面変換と子への継承に使う）と `geometryLocalToOwner`（Geometry → 物理 Owner 座標。描画が Actor に追従する根拠）。

**設定は製品側から。**`CutWorldProfile`（ScriptableObject）1 つから供給する。Kernel の頂点上限は DESIGN の **L = 128**。Cook 同時予約数は既定 **4**（2 以上であることを検証で強制する：1 では独立 Owner の切断が直列化し、一方の完了待ちで他方が止まる）。storage 容量・dispatcher の待ち枠／緊急予約／フレーム予算・destination の容量・表示の各容量・分類 epsilon・Anchor epsilon・未完了 Cut の上限・終了の期限もここに置く。**容量設定は「件数」であって固定バイト上限ではない**（epsilon は長さ、終了期限は時間である）。分離 Impulse の算出式と「向き」はここで決めない（切断ごとに呼出側が 2 値で与える）。

**通常更新への接続。**標準の FixedUpdate 自動シミュレーションを前提に、受付を取り上げた `Update` の中で Provisional を公開し、同じフレームの `LateUpdate` の実表示収集に反映する。同一フレーム内の追加機会は同じ残予算を使う（`Advance` の交互反復）。同期待ち・強制 Complete・busy polling・独自 PlayerLoop は追加していない。

**終了の順序。**終了は一度要求し、**通常フレームを跨いで**進める。`Shutdown` の最初の呼出しで 受付停止 → 受付済み切断をすべて終了 → **driver の駆動停止** → DAG と Cook を閉じる（実行中は中断しない）。以後は構成根が、そのフレーム自身の id で frame を回す。**架空の frame id を作らないので予算は補充されない**：その回収も submit も、同じフレームの残予算を共有する（回収も予算を消費する）。回数は制限せず、呼出側が同じフレームでもう一度求めても同じ残予算の続きになる。外に出ているものが無くなってから dispatcher を停止し、**その確認結果が解放を決める**：workers の停止が確認できたときだけ Owner・表示・storage・destination を解放する。**期限の到来は確認ではない**ので、確認できない場合は何も解放せずにその旨を残す（Worker が読んでいる可能性のあるメモリを解放しない）。**回収に必要な Frame／Cook／DAG は driver より後に消える。**

**共通 Player 終了（4 章）。**構成根が非永続の終了要求 latch を持ち、Renderer 初期化の前から存在する。共用 Geometry 切断の失敗と、**表示に必要な容量・Stencil 構成の不成立（初期化時）**が原因で、Main がその latch を一方向に確定する。確定後は**新規切断受付**（製品の受付入口が同じ latch を読む）と**未公開成果物の公開**——表示 Geometry Commit だけでなく **Final handoff による論理子公開**も——を閉じ、その後で一度だけログし、Player の終了 API を一度だけ呼ぶ。**これは通常終了ではない**：全 Work 回収・停止確認・資源解放は開始せず、構成根の `OnDestroy` でも、**駆動側（`ProvisionalCutDriver`）の `OnDestroy`** でも通常終了へ入らない（DESIGN 4 が回収・解放を保証しないため。明示的な終了呼出しは従来どおり）。profile の値の不整合やMaterial 欠落のような構成ミスは従来どおりログと無効化で、終了へは広げない。

**共通 Player 終了（4 章）。**構成根が非永続の終了要求 latch を持つ。共用 Geometry 切断の失敗は Main でその latch を一方向に確定し、**新規切断受付**と**未公開成果物の Commit**（Commit を latch の後ろに置く）を閉じてから、一度だけログし、Player の終了 API を一度だけ呼ぶ。これは上の終了とは別経路であり、待ち合わせ・全 Work 回収・資源解放・Fragment 退役はしない。

**確認**は PlayMode で、構成根を実際に生成・初期化し、試験から `Advance`／`DriveUpdate`／`DriveLateUpdate` を呼ばずに行った：①受付を取り上げたフレームで Provisional が公開され、**そのフレームの収集**で 1 つの body が 2 つの側として描かれる（Final 完了は待ち条件ではない）。②自動駆動だけで handoff と実 Geometry Commit が完了し、生成した子をもう一度切断できる。③2 つの独立 Owner の**物理 Work が同じ destination（Unity Job）へ同時に**渡っていることで並行を確認し（Stage 名や待ち行列ではなく、Owner ごとの数値 Work を識別する）、一方の完了待ちで他方が止まらない。④対象 Work を「完了済みだが未回収」に固定して終了を要求し、**予約と入力 Geometry の読取保持が残る**こと、解放後は**通常フレームだけで**回収と解放が完了することを確認した。⑤切断入力として受理されていない Geometry を切ろうとした失敗が終了要求を起こし、受付が閉じ、終了 API が一度だけ呼ばれ、**その後に通常終了へ入らない**ことを、終了 API を差し替えて確認した。⑥**別の要求の Final 成果物が回収可能な状態で**その失敗を起こし、**その成果物が実際に回収されたうえで**（要求は終わり、記録が成果物を保持している）、以後の更新の後段でも**論理子が公開されない**こと（対はそのまま）を確認した。

**残件。**Scene 常設アセット（profile と root を持つ Scene／Prefab）と描画そのもの（`VpLogicalCutDisplay.Render` を呼ぶカメラ側）はこの単位に含めていない。Scene unload やアプリ終了で `OnDestroy` の順序が保証されない場合の終了は、**明示的な `Shutdown` とは別**であり未確認である。Hit 検出、建物 World D6、Character、Impulse の算出・向き、XR・性能調整も対象外。

物理受付と表示 Geometry DAG・Commit の接続（2026-09-21）。一度の受付で、同じ CutOperationId のもとに物理と表示 Geometry の両責務が動く製品経路を成立させた。

**受付は一度だけ。**`ProvisionalCutDriver` は robust support の判定（DESIGN 7.6）で切断が成立すると判断した後に、台帳へ**一度だけ**受付を求める。表示 Geometry がある場合はその要求を `CutDag.TryAdmit` を通して出し、台帳が発行したまさにその Operation に Geometry の仕事が 1 つ登録される。表示 Geometry がない構成では従来どおり台帳へ直接求める。No-op（片側に支持がない平面）と受付拒否では Operation が生じないので、Geometry Node も Work も生じない。第二の台帳・独自 ID・汎用イベント基盤は追加していない。この接続では論理子公開を Final handoff に任せ、**DAG から重ねて `Publish` しない**（`CutDag` は受付を台帳へ行い、Geometry 完了時は `CompleteGeometry`、Commit なしの終了時は `Terminate` で台帳を更新する。行わないのは論理子の公開だけである）。

**実際の Commit へ接続する。**Commit は `VpDisplayGeometryCommit`（`VpLogicalCutDisplay.TryCommitCut`）——GPU 転送と表示登録の更新を伴う実際の入口——であり、試験用の代替ではない。Commit が不成立なら何も変わらず、成果物は DAG が保持したまま後の機会に再提示される（部分公開はしない）。未完了枠は **Geometry 責務の完了時**に `CompleteGeometry` で返る。Final handoff は返さない。Geometry の失敗は `ICutGeometryFault` へ渡り、物理不成立へは読み替えない。表示 Offset・履歴の毎フレーム再探索・容量問合せ Worker は復活させていない。

**座標。**受付面は Source fragment の論理 frame の値で、そのまま台帳が採用し Commit が公開する。カーネルが読む面はその値を**基準 Geometry 自身の frame**へ 1 度だけ変換したもので、変換は Geometry が読まれる時点で行う。物理 Owner の現在配置はそのどちらでもなく、描画がそれに追従するだけである。動いた Actor の現在位置から受付面を作り直すことも、Commit 時に Actor を戻すこともしない。

**更新と終了。**駆動は `SharedWorkFrame` の**同一フレーム**で行う。`Advance` は**同じ frame id のまま、frame の更新と回収を交互に繰り返す**。回収は常に更新の直後にあり、**最後の更新の後にも必ず回収がある**：handoff による論理子公開はその Operation の Commit 条件であり、その Commit は子切断カーネルの依存解消である一方、frame の更新は別要求の数値・Bake を回収してその要求を handoff 可能にするからである。どちらを端に残しても、呼出し順だけで生じる固定待ちになる。追加の機会は、**直前の回収が実際に何かを終わらせ、かつフレームの残予算がある**ときだけ取る。この 2 つは別物で、一方が他方を代弁しない：frame の更新は予算 0 でも参加者を一度 Pump するので「更新が動いた」は「まだ進めてよい」ではなく、逆に更新が何も動かさなくても直後の回収が公開を進めることはある。そこで残予算は `SharedWorkDispatcher.RemainingBudget` に直接尋ね、回収は自分の進捗で語り、いずれにせよ回収が最後に来る。同じ id の再呼出しは予算を補充しない（`SharedWorkFrameProgress.Plus` は 1 フレーム分として合算する）。同期待ち・強制 Complete・busy polling は追加していない。Abort・Stale・明示終了では、DAG は台帳の状態を読んで自分の Node を終了し、未公開の成果物（生成した index range）と入力保持を既存の回収時点で返す。**Physics の要求記録が handoff 後に消えても Geometry の責務は残る**（Node は Commit か回収まで生きる）。Driver 終了後に残る Work の回収主体は **`SharedWorkFrame` に参加している `CutDag` 自身**であり、frame の所有者が pump する（`ProvisionalCutRecovery` と同じ扱い）。DAG の生成・破棄は呼出側のもので、この駆動は所有しない。

**確認**（EditMode・製品更新入口経由・実 storage／実 display／実 GPU 転送／実 Actor）：①Geometry 未完了でも Provisional→Final と論理子公開が進み、後着の実 Commit で子が自分の Geometry を持ち未完了枠が返る。②Geometry が先に終わっても論理子公開前には Commit されず、公開後に適用される。③親 Geometry 未完了のまま Final 子を再切断でき、子のカーネルは親 Commit を待ち、**親が Commit したその更新で**実際に destination へ投入される。④描画は Commit の前後とも実 Actor の配置へ追従する（移動・回転を与えて確認）。⑤Geometry 実行中の明示終了では Commit されず、成果物と枠が一度だけ返り、物理側は既存規則どおり Source を退役させる。⑥1 要求に Operation と Geometry Node が 1 つずつ、No-op ではどちらも増えない。

**なお未接続。**Scene 常設の構成根（storage・display・DAG・driver を実シーンで組み立てる場所）はこの単位の対象外で、基準 Geometry の登録と body の表示登録は呼出側が行う。Hit 検出、建物 World D6、Character、Impulse の算出・向き、XR・性能調整も対象外である。

Provisional 受付・保持・駆動の接続（2026-09-21）。A の公開部品を製品コードから駆動する接続を実装した。

**製品の呼出し箇所。**`ProvisionalCutDriver`（MonoBehaviour 1 つ、`Bind` で台帳・対応・Cook・`SharedWorkFrame`・表示を外から受け取り、何も生成・所有しない）。`Update` が呼ぶ `DriveUpdate` が、**その更新の中で**それまでに届いた要求（`Ask`）を取り上げ、各要求について **分類 → 受付 → Anchor 配分 → 未公開対の構築 → Provisional 公開 → Final 切断の投入**まで行い、続けて `SharedWorkFrame.Update` が同一フレームの残予算で回収・投入を進める。`LateUpdate` が呼ぶ `DriveLateUpdate` が**実際の表示入口** `VpLogicalCutDisplay.TryBeginFrame` を公開の**後**に呼ぶ（Snapshot の確定規則 5.6 には触れない）。別の位相にいる呼出側は `Ask` で要求を置き、更新の位相にいる呼出側は `RequestCut` を直接呼べる——どちらも同じ経路である。標準の FixedUpdate 自動シミュレーションを前提とし、切替区間に手動 Step も外部コールバックも挟まない。発火元（武器・試験・ツール）はこのドライバの外で、受付後の処理はすべてここを通る。

**§7.6 分類の製品実装。**`PhysicsCutClassification` が受付対象の現在の Shape と採用面から、Kernel が要求する per-vertex の符号付き距離・厳密な符号と、per-convex の robust-support 分類を一度だけ作る。**Provisional 構築と Final 投入は同じこの 1 回の走査を読む**（試験ハーネスへの依存は無くなった）。**受付の条件は集合全体の正負 support の有無**（`SplitsBothSides`）であり、Convex の配分結果ではない——support の無い Convex は配分規則どおり正側へ行くが、それだけでは切り落とすものが無いので**受付前の no-op**（`LogicalCutAdmission.NoOp` と同じ意味）として台帳へ何も渡さない。配分規則そのものは変更していない。計算した距離が非有限なら分類を拒否し、near-plane 扱いへ落とさない。概算質量用 Bounds の頂点走査は復活させていない——分類は per-vertex 入力を作るための走査であり、質量の保守的な箱は各 Convex が持つ値から作る別物である。

**所有権の取得・移転・返却点**（`ProvisionalCutTransaction` が切断ごとに 1 つ持つ）。取得：分類は `RequestCut` で作られ、入力 Shape の保持は Cook 投入時に `AcquireForWork` で取る。移転：未公開候補は公開で対へ渡り（`Detach`）、対の終了は対応（`EndProvisional`）の仕事。返却：分類は要求が終わった時点で返し、**Final 成果物は保持先を持って保たれ**（handoff 未実装を理由に破棄しない）、**入力の保持は成果物を手放す時に一度だけ返す**——Job 完了でも要求終了でも公開待ち・公開拒否でもない。

**終了はどの理由でも同じところへ閉じる。**台帳の拒否は物理を変えず、`Stale` は台帳が回収して**どの Fragment も退役させない**。構築不能・公開の物理不成立・**Cook が何も生まなかった場合**は、対を Scene から外し台帳へ Abort を求め、Applied のときだけ Source が退役する（この区別は台帳が持つ）。**通常終了（`EndCut`）と破棄（`OnDestroy` → `EndEveryCut`）はこの同じ経路を通る。**

**Anchor 配分は待機にしない。**既存 API が返すのは `Prepared`／`OperationNotActive`／`DistributionRefused` で、同じ epsilon と同じ Anchor 集合で再度求めても答えは変わらないため、再開を待つ状態は作らない。`DistributionRefused` はそれ自身の理由として返し（DESIGN が Abort へ読み替えるのは infeasibility に限るので Abort にしない）、`OperationNotActive` は台帳が既に終えたものとして扱う。

**資源の回収と、受付済み要求の管理終了は別である。**`DistributionRefused` と切替前の例外では、台帳の受付と未完了枠が**そのまま残る**。したがって不要になった資源（分類・未公開候補）だけを戻し、**その受付を終了できる記録は残す**（`Unestablished`）。自動再試行は追加せず、閉じるのは既存の明示終了入口（`EndCut`／`EndEveryCut`）で、そこで台帳の Active Operation と未完了枠が解消される。`OperationNotActive` は台帳に残るものが無いので記録も終える。

**例外は切替の成否で分ける。**切替**前**なら未公開資源を戻して伝播する。切替**後**なら——対応が対を持っているかで判定する——**公開済みの対とその所有記録を保ち**、未公開失敗へ読み替えずに伝播する。保った記録は後から `EndCut` で対・台帳・資源を終わらせられる。

**終了要求と回収完了は別。**投入済み Work が戻るまで分類配列と入力保持は残る。終了を求められた記録は `ProvisionalCutRecovery`（`IMainThreadPump`）へ移り、**それは `SharedWorkFrame` の参加者**なので、Driver が消えた後も Frame を pump する側によって運ばれ、Work が戻った時に一度だけ返る。同期待ちも強制 Complete もしない。

**T-091 の確認条件。**別の位相で置かれた要求が `Update` の呼ぶ入口（`DriveUpdate`）で取り上げられ、**その 1 回の呼出しの中で** Provisional 公開まで到達し（公開フレーム番号が `Update` 実行時のフレームと一致）、続いて `LateUpdate` の呼ぶ入口（`DriveLateUpdate`）が**実際の表示入口** `VpLogicalCutDisplay.TryBeginFrame` を通して同じフレームで収集し、その収集が受付済み切断の**正負 2 側**を持つこと。確認時点で **Final 切断は未完了**（予約取得・数値・Bake・表示 Geometry はいずれも公開の待ち条件ではない）。実投入は destination の受理と Begin で見る。

**残件。**Final handoff（保持した成果物の行き先）、Hit 検出そのもの、建物 World D6、Character、XR・性能測定。`anchorEpsilon`・分類 epsilon・Kernel の頂点上限・Cook 同時予約数は**いずれも外から供給する値**で、製品値はここで決めていない（分類 epsilon と `anchorEpsilon` を同一の数にするかも決めていない）。**Scene への常設接続は無い**：ドライバは Scene に置かれれば駆動するが、それを置いて `Bind` する構成根はまだ無い。B の成立は切断ライフサイクル全体の完成を意味しない。

生成元照会（2026-09-21）。7.1.2の「公開時に確定する生成元OperationとSideを、既存台帳内の不変な関係としてO(1)で照会する」を実装した。保持先は**既存のFragment記録**で、子のIDを発行するその呼出しの中で生成元と側を書き、以後書き換えない（新しい公開API・第二の台帳は作っていない）。`TryGetOrigin`はOperation履歴の走査をやめ、そのFragmentの記録を一度読むだけになった。直接登録した根と不正ID・未発行IDは従来どおり生成元なしを返し、公開前の子はIDが存在しないので照会できない。Operationの完了・終端、子自身の再切断・退役でも関係は変わらない。ID非再利用、公開前の準備、既存の更新通知は変更していない。**`OriginLookups`は照会回数**であり、履歴走査回数ではない（旧説明を訂正した）。既存の利用経路（Snapshot・候補収集・表示）は同じ`TryGetOrigin`を通るのでそのまま直接照会になり、**祖先探索は必要な段数だけ1段ずつ続く**（全体をO(1)とは扱わない）。入力変更時のSnapshot全体検証・再構築、受付順のOperation走査（生成元探索ではない）、「不変フレームでは構造を再探索しない」既存の性質はいずれも維持している。確認は既存の台帳・Snapshot・配置・Commit試験で行い、走査の撤去と直接参照はコード経路で確認した（専用ベンチマーク・性能SLA・新しい観測基盤は追加していない）。19.1.9のSlash側は、Slash専用Cache・通知・消費状態の伝播も履歴のGC／圧縮も追加していない。

Provisional公開（2026-09-21）。**成立した範囲**は、非Character・非建物について、構築済みの未公開候補を一度の Main Thread 更新で公開するところまでである。旧Ownerは`Withdraw`でSceneから外れ（破棄も資源返却もしない）、正負2 Actorが有効化されて質量・重心・慣性・速度を実際に保持し、Sibling D6を載せた側が後に入る。**論理子は作らない**（`Publish`を呼ばない）。ただし台帳を読むだけではない：公開してよいかの判断は既存Final経路と同じ`PreparePublication`に任せるので、**Staleの回収はそこで一度だけ行われ**、公開前の物理不成立では既存規則どおり`Abort`とSource退役へ送る。公開後も両ActorのHitは同じSource Fragmentへ解決する（対を`CutOperationId`の側に保持し、各Bodyから対を引く）。表示は、受付済み切断の側を名指す照会に対してその側のActorへ追従し、**側を名指せない照会（本体そのもの、または別の切断の側）は既存のMissingで拒否する**——片側や退役した旧Ownerへ暗黙に退避しない。新しい拒否理由は追加していない。対のない通常の照会は従来どおりである。公開済み対の終了入口（2 Actorとconstraintの破棄、2 Shapeの返却）を持ち、未公開候補の`Dispose`とは別経路で、いずれも一度だけ返す。分離Impulseは子ごとに1値ずつ呼出側から受け取る。失敗の区別は既存規則のままで、台帳の拒否（authority移動・Anchor未準備・非Active）では物理を変えず、公開前の物理不成立はAbortとSource退役へ送る。**例外の回収は、2 Actorが入る区間——旧OwnerがまだScene内にある間——に限る**。そこでは両側をSceneから外して伝播させるので「Sourceは元のまま」が実際に成立する。**旧Ownerを外した時点から先は取り戻さない**（Sceneから外したOwnerは戻さない）。その先の手順が成立する条件——台帳の判断、対応の枠、この切断とこのSourceに既存の対が無いこと——は切替前に確認しており、切替開始後の例外は未公開失敗へ読み替えず内部エラーとして伝播する。

**確認の範囲**は EditMode 試験と、終了・破棄についての PlayMode 試験 1 件である。Sceneと対応そのものを見ており（どのObjectが有効か、Bodyが実際に何を持つか、照会と解決が何を答えるか）、Stageやフラグでは代えていない。Sibling間の衝突抑止は**実際にStepを進めて**確認し、**同じ入力で `enableCollision` を戻す対照**で判定が変わることまで確かめた（抑止時は離れず、抑止を外すと離れる）。**対照が示すのはSiblingが離れるようになることまで**で、外部Bodyが押すことの確認は抑止あり側でのみ行っており、抑止を外した側では見ていない。有効化順序について示したのは「採用した順序で公開され、1 Step 後も両 Actor と参照が残る」までであり、他の順序の危険性や拘束の効果は示していない。**終了・破棄については PlayMode の小さい確認を 1 件追加した**：旧 Owner を先に退役しても対の Mesh が生存し、対の終了で Scene・逆引きから外れ、フレームが明けると遅延破棄によって 2 Actor が残らないことまでである。

**未確認・未接続。**製品の受付経路とそれを駆動する呼出し主体が無いため、**受付フレーム内公開（T-091）は未確認**である。試験からAPIを直接呼ぶ経路をもって製品接続とは扱わない。入力Shapeの借用に対する保持（`AcquireForWork`／`ReleaseFromWork`）の所有記録は実装しておらず、**保持の責務は呼出側に残る**。Final成果物の保持先、Provisional→Final handoff、Building World D6、Hit検出そのもの、Character、Player・性能、XR、統合後の追加検証も残る。寿命条件の説明は**標準のFixedUpdate自動シミュレーションで、対象Sceneの手動シミュレーションを挟まないUpdate区間**に限り、任意の駆動へ一般化しない。EditModeの即時破棄をもってPlay時の遅延破棄を確認済みとは扱わない。

未接続・未確認（これら5単位では扱わない）。**Ignored集約の配置**は、先行する4単位（74bbd8e／2135bf4／cc168a6／9993ece）の時点では未解決だった。集約RenderFragmentのrootは最初のIgnored境界のSourceであり、そのSourceが公開で置換済みであればOwnerは存在しないため、照会がそれを位置なしと答え、Snapshotが入力契約の不成立で終わり、表示が恒久停止していた（当時の状態として記録に残す）。**その後D-187で方式が決まり、f72f7d0で実装してlocal mainへ統合し、この停止は解消した**（5.2／5.6、およびD-187を参照）。集約を解いて本体を重複表示することも、静的配置へ退避して追従を止めることも、新しい上限・拒否Gateの追加もしていない。それでも、以下が残るため**7.1.2全体の一体公開は未完成**である。Cut DAG（4.5.6）のGeometry経路への接続のうち、採用面のframe写像は4.5.6の「実装状況」の範囲で成立した（b98661b）。この公開経路からGeometry側を駆動する接続は残る。Hit／Queryの解決自体（対応を引く先は用意したが、解決処理は書いていない）。Provisionalのうち未公開の構築と回収はf06f462の範囲で成立したが、**Provisional公開（旧ActorのScene除去と正負・Constraint投入のall-or-none切替）、公開後のLeaseと必要なPhysics Stepを跨ぐ資源寿命、建物World D6、Provisional→Final handoffは残る。**先行する公開・表示接続（cc168a6／9993ece）は直接Final経路のものであり、これらの完成でも、通常切断を旧物理のままFinal待ちへ変える仕様変更でもない。Characterのskinningと骨に付いたConvex、製品全体の構成根、この経路でのPlayerと性能、統合後の追加検証。これらが残ることは、過去に別の条件で確認済みの項目を未確認へ戻す意味ではなく、今回の証拠をそれらへ流用する根拠にもならない。Phase 4全体および関連する受入れ項目の完了は意味しない。

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
| `IsBuildingDerived` | 建物として明示的に登録する初期所有者はtrue、その他はfalse。通常切断・追加分割の子孫へ継承する |
| `BuildingSplitDepth` | 初期値0の非負整数。建物由来の系譜で公開した1→2物理分裂の深さを表し、表示切断数は数えない。非建物の系譜は0を維持する |

建物由来をRuntimeのBounds、名前、Material等から推測しない。Phase 4.3では手書きSynthetic入力を使う。

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

Runtime本体と既知Constraintの識別境界は、ロードマップ上の独立Phase 4.3で通常切断へ接続する。Phase 4の汎用物理、4.1のCut/Cook Profiling、4.2のPlayer非接触を再オープンせず、4.50より前にT-094で完了する。任意分割・GCへの統合は5.6／5.7で確認し、これらや未来予測本体を4.3の完了条件へ前倒ししない。

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

### 7.4 切断作業ブロックの初期化

一つの切断が取る作業用ブロックのうち、**読む範囲がすべて先に書かれるもの**は初期化せずに取る。対象は次の5領域とする。分類走査の1ブロック（convex範囲・bank・side・signed distance・sign class・distance base）、Arenaの1ブロック（B-repの5表・outcome・scratch）、および予約時の`meshIds`・`meshBounds`・`meshVertexCounts`。

**ゼロを意味として読む領域は初期化を残す。** Jobの報告（`ranToEnd`が「走らなかった実行」を表す）と、Bakeの完了印（要素ごとの0が「その要素は返ってこなかった」を表す）は、従来どおり確保時に消去する。初期化の省略を一律に広げない。

省略の根拠は**読取りと書込みの対応付け**とする。分類走査は各配列を使用する全範囲に書く。Kernelは、outcomeを読む実行（成功）では全input convex分を書き、bankは自身が生成したと述べた範囲だけを読ませる。ClipとReductionは、自身のscratchへsentinelを書いてから読む（Reductionは容量からの配置と再利用を前提に、範囲外を死として明示初期化する）。予約の3配列は、Jobが全スロットへ数と箱を書き、Main threadが全スロットへidを書いてからBakeが読む。失敗・早期終了・取消・未実行・容量不足の各経路では、これらを読む前に終了する。

初期内容を変える試験（取ったまま／ゼロ／ゼロと似ていない値）は**補強証拠**とし、省略の証明としない。Reductionの成功経路とR1経路を製品Arenaへ通す確認を含める。Arena内の全読取りの独立監査は完了していない。

費用の確認は7.5の測定点に従う。確保オプションを切り替える計測用の分岐・カウンタ・ハーネスは**製品に置かない**。

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

同じdistanceとsupport分類を受付・Provisional配分・Convex分割可否へ共用する。分割対象B-repの実切断はd > 0／d < 0／d == 0で行い、eを実切断の正負分類、面offset、snap、Kerfへ流用しない。一方のrobust supportを欠くConvexはd = 0を横断していても未切断継承し、反対側へepsilon程度張り出す近似を許容する。点Anchorは7.1に従ってOwnerの現在集合から独立配分し、anchorEpsilonと本節のepsilonを同一値にすることは要求しない。

片側supportなしはNo-opであり、対象状態・世代・Anchor・表示・物理・既存履歴を変更せず、ID、Pending Cut、Transaction、切断／cook／Commit仕事を作らない。命中観測と非破壊通知は残せるが、要求を保存・再実行しない。接線・頂点／辺／Face接触だけ、反対側がepsilon以内だけ、全頂点Near-planeはNo-opとなる。別Convexが明確に正負へ分かれたCompoundは交差Convexなしでも受付け、Boundary 0の物理1→2を許容する。入力判定に出力Convex・Cap・連結成分や表示Geometryの二次判定を生成しない。

このmarginはcook成功を保証しない。受付後に判明した7.2の形状・質量特性・cook不成立、Anchor・必須D6・frame不成立は7.1のAbortへ送る。正常成功は常に正負2 Final Ownerと2 LogicalFragmentで、Geometryは同Sideへ所属する。Geometry片側または両側が空でもPhysics子は残し、反対側へのGeometry付属、一子両Side、Merge／Alias、所有者共有を設けない。

Geometry空は実Cap・面積0 Triangleを含む面集合0件で判断し、未計算・失敗・体積0・Stencil非描画を空にしない。Geometry参照0件の子はRendererなしの通常物理対象であり、ダミーGeometry・Cap・消滅VFXを作らない。任意の7.9物理GCまたは7.10の寿命終了を適用しない場合は、通常Object／Level寿命で維持する。Phase 1～3では15章のHarness合成Physics入力だけを使い、製品に代替Hull／Physics Modeを残さない。

### 7.7 切断受付上限

既存容量設定のMaxIncompleteCutOperationCountは受付済みPending Cutを全対象合計で1件ずつ数える。Physics実行、Geometry依存待ち、Commit待ちを含め、Transaction、子、Job数を多重計数しない。Active Sourceへの見送り、No-op、未命中投機、7.9／7.10の任意処理は数えない。

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

準備中も元物体を正本として動かし、任意処理の完了を通常切断の受付・成立・公開の条件にしない。共有資源の競合による処理開始・完了の遅延は7.9.5の範囲で許容する。同一対象の任意変更は既存Main Threadの受付・Commit順で直列化し、公開直前に前提を再確認する。通常切断の受理、別更新または退役が先行した候補は公開せず、未投入仕事は既存取消、投入済み仕事は完了後の不採用・回収へ送る。候補を最新対象へ推測適用しない。No-opや受付見送りで対象が不変なら、それだけで候補を失効させない。

#### 7.9.2 追加空間分割

成功は、一つの物理所有単位から、それぞれ非空の共用Geometry、有効な非空Convex集合、正規質量、既知の支持を持つ通常物体2個を得ることとする。Rendererだけの分割や、全Geometryを再び同じ所有者へ戻す結果は成功としない。2はこの追加分割一回の出力物理所有単位数であり、閉Component数や系譜・Scene全体のActor数ではない。各出力に複数閉Componentを含めてよい。表示なし第三物体や小さい側の生成省略は追加しない。

初期方式は固定された対象局所frameの一枚の平面で、現在の共用面集合を横切らず正負の非空集合へ配分する。大きなIndex範囲も内部をTriangle単位等で調べ、既存数値許容内で欠落・重複なく配分できることを確認する。既存Boundsが使える箇所は使うが、通常切断へ全島列挙を前倒ししない。探索は有限の試行・作業量で打ち切り、GJK、全組合せ、最良平面、完全分離を要求しない。候補なし・確認不能は7.9.5へ送る。

既存の表面・実Cap・面積0 Triangleを属性・winding・Topologyを保って配分し、新しい表示切断面やCapを生成しない。分割先別の新Index領域へ振り分けコピーし、必要範囲を転送する。比較ソートは必須でなく、Countと正負への書出しでよい。既存Vertexを参照し、全頂点複製、表示専用第二Index正本、全プールコンパクションは要求しない。新旧Indexの一時共存を許容し、旧CPU範囲は4.5.3のCPU正本参照・Job・転送元読者寿命後に再利用する。この処理を通常切断へ戻さない。

現在採用中のPhysics Convex B-repを同じ平面で分類し、非交差ConvexはB-rep・再利用可能なcook済み資源を継承する。交差Convexだけ7.2のclipと必要なcookを行い、全採用集合から質量特性を近似し、共用Geometry全体の凸包再構築、Convex Union、未切断Shapeを両側へ複製するProvisionalを作らない。表示Geometryの対応有無にかかわらず全採用Convexを切断子または非交差継承として配分する。片側有効Convexなし、正質量不成立、実cook不成立、資源・構成の成立不能では候補全体を不採用とし、元物体を残す。一時的な枠不足は7.9.5と区別する。

共用Geometryは今回配分した同じ側の物理所有者へ所属させる。反対側付属で二物体化を偽装しない。点AnchorはSource Ownerの現在集合から7.1に従って配分し、各子の固定／動的を導出する。Geometry付属をAnchor移送に流用しない。

Anchor配分と通常の物理条件を成立させて公開する。建物由来の分割には7.2.2を共用し、両子へIsBuildingDerivedを継承して公開時にDepthを親から1段進め、Anchorなしの動的子だけに新しいWorld D6を生成する。親D6は付け替えず、親Actorとともに退役する。必要な子D6の構築が不成立なら候補全体を不採用とし、親Actor・Depth・D6基準姿勢・Limitを維持する。単純な点Anchorの固定を保持できる対象を一律除外しない。

追加分割開始時の親Rigidbody質量をSnapshotし、7.2に従って採用Convex集合から子の質量特性を近似して親質量を保存する。共通祖先の全Sibling分を再配分しない。Geometryの個数・面積・寸法から質量を作らない。公開直前の現在pose／速度へ接続し、共用GeometryのWorld配置を通常の数値許容内で保つ。動的子には7.2の初回物理分裂の速度継承式、固定側には既存無運動規則を使い、攻撃・分離ImpulseとOffset、親poseの巻戻しを加えない。

両出力のGeometry参照、Convex／cook資源、質量・点Anchor、通常Actor・必要な建物D6と登録先を準備し、安全なMain Thread／物理Step境界で所有構成を一括切替する。描画は切替前か切替後の完全な構成だけを使い、片側先行公開、親の先行削除、表示／Stencilの別時点移管をしない。準備・公開前検証の不成立では未公開資源だけ回収する。

現在のLogicalFragmentと物理所有参照で、各Geometry範囲の再切断対象・frame・支持・物理所有者を一意にする。必要な通常IDと子配分情報は既存の発行・系譜規則で作り、過去IDの再利用、過去Operationの直接子・作成時世代の書換えはしない。既存の切断履歴・実在境界を維持し、現在世代への対応を原子的に更新する。新しいGameplay Cut Plane、CutBoundary、SlashHitConfirmed、LogicalCutOperationは作らず、対応を安全に構築できない候補は見送る。

公開後は専用分割片ではなく通常の切断対象とし、現在の共用Geometry・Convex・Anchor集合から再切断する。旧Actor・そのシステム所有Constraintと不要Shape／Mesh参照は7.9.4に従って退役する。公開後の継続不能な個別物理異常は7.1のLogicalFragment退役へ従い、任意候補専用rollbackを追加しない。

#### 7.9.3 表示なし物理物体の遅延回収

物理GCはManaged GCや全参照追跡型GCではなく、共用Geometryが確定空の生存物理所有単位を終了するゲーム上の寿命Policyである。7.9.1を満たし、所属する共用Geometry全体の面集合が0件で、有効な非空Physics Convexを持つ対象だけを候補とする。実Cap・面積0 Triangle・全Renderer／submesh相当範囲を含め、画面外、遮蔽、Renderer無効、Stencil相殺・非描画、体積0、未完成・失敗を空の証拠にしない。

一面でも共用Geometryが残る物体は回収せず、個別Convexを間引かない。仮Actor・未公開一時資源は7.1の回収へ送る。対象自身の建物D6はActorと同じ寿命で退役する。Anchorなしだけを回収許可にしない。

通常の外界接触や移動だけでは回収を禁止しない。当たり判定消失による周辺物体の運動変化を許容し、接触中の完全無影響証明、Sleep必須、過去Impulseの取消を要求しない。開始・適用時の候補条件が一致する場合だけ、安全な物理境界で当該所有単位とそのLogicalFragmentの生存・受付、Physics Scene、動的な所属Query等の登録を終了する。Level初期化時の固定PlayerLocomotionOccupancyは対象Objectの退役で変更しない。同じObjectId・祖先履歴を持つ無関係Siblingは削除しない。

生存LogicalFragmentを終了する低レベル処理はPhase 4の7.1を再利用し、本節は表示なし物体を選ぶ遅延Policyだけを追加する。質量は世界から除去し、Siblingへの移送・再正規化をしない。正常分割時の親質量保存と退役時の質量消失を区別する。

候補検出は既存物体一覧の有限件数巡回と確定Geometry件数等でよい。毎Frame全Mesh走査、逆被覆対応表、専用Asset前処理、VFXは追加しない。巡回順・頻度の高度な最適化や回収期限を初期要求にしない。

#### 7.9.4 非命中公開・退役と既存世代への接続

本節の所有構成変更・退役と7.10の退役は、命中を伴わない公開を許可する。候補は既存TaskId等の相関情報と成果物所有者、対象のObject・Geometry・Physics・Anchorの必要な世代・構成参照で識別する。準備・見送り・不採用だけではObjectGenerationを進めず、同じ基底の通常予測を失効させない。

追加分割の公開では対象の既存ObjectGenerationと実際に変更するPhysics等の既存世代を同じ境界で進める。GCは退役を既存の生存性／世代付き参照へ反映する。いずれも旧所有構成への予測・後着成果物を適用しない。履歴ID・作成時世代を書き換えず、世代wrap・ID再利用・別対象への古い適用を許さない。通常Transaction／Geometryの照合は8章に従い、別LogicalFragmentのObjectGeneration更新だけでは失効させない。

通常切断の実Hit・受付・採否条件は緩めず、本節の仕事へPending Cutを作らず、MaxIncompleteCutOperationCountにも数えない。通常切断が先に受理されたら任意成果物を不採用にし、任意変更が先に公開済みなら以後の切断は更新後の対象へ適用する。中間の曖昧な受付対象を公開しない。

登録終了と最終資源解放を分け、投入済み仕事の完了・回収責任と入力保持を維持する。移管した参照、他の生存対象・履歴が必要なMetadata、参照中の共用Geometry／Cooked Geometryを解放せず、旧Actor・Shape・システム所有Constraint・Meshは既存のStep・Work・GPU寿命に従って一度だけ退役する。全履歴圧縮、全メモリ即時解放、専用世代別GC、物理巻戻しを要求しない。

独立Maintenance世代、専用ID空間、Registry、永続形式、第二の状態機械は設けない。通常IDと生存Workの所有情報は既存規則を使う。観測は既存Task lifecycleと少数Counterで種別・成功／見送り・不採用・回収数を区別し、切断成功数や架空のCut Operation Trace束へ混ぜない。

#### 7.9.5 単一試行・再試行抑止と予算

無効な機能は候補走査・新規Workを発行しない。追加分割の数値処理と必要なPhysics.BakeMeshは4.3のBackground Poolへ、Mesh適用・構築・公開・回収は既存Main／物理境界へ接続する。4.4のReady・予約・容量・Main残予算が揃えば、上位ReadyやUnity workerの稼働状態を理由に投入を待たせない。共有CPU資源・Bake枠の競合による後着通常切断の遅延を許容するが、urgentの投入余地を転用せず、専用pool・追加予約枠・昇格・同期救済・必達期限を設けない。

追加分割は全対象を通じて未回収の試行を初期上限1件とする。枠が空き、軽量な入口条件を満たし、現在入力の再試行抑止がない物体を有限巡回で1件選ぶ。幾何的な分割可否をMain Threadで先に確定せず、一つの試行内で背景の候補探索・配分確認から必要なConvex処理・cook、回収・公開直前検証まで進める。候補平面を同じ試行へ引き継ぎ、候補判定だけの必須先行Job、恒久的な成功候補Cache、新公開APIを要求しない。

1件の枠は投入待ち、計算中、cook待ち／実行中、適用待ち、不採用時の安全な回収まで保持し、探索完了だけでは空けない。Compoundの後続cookは既存Batch・投入上限内で少数ずつ進め、大量Workを一括投入しない。工程分割は実装詳細とし、実行先は4.3に従う。完了回収は新規投入条件で止めず、通常予算で継続する。

物体側には「現在入力では今回の方式で追加分割不成立のため再試行しない」という印だけを既存世代・構成参照へ結び付ける。通常切断または関係するGeometry／Convex・所属・Anchor入力の変更で無効化し、共通の剛体並進・回転や時間経過だけでは再探索しない。数学的な分割不能証明ではなく、通常切断・GCの受付にも使わない。進行状態は1件の試行と既存Workで持ち、物体側に重複した状態体系を作らない。

| 結果・状況 | 処理 |
| --- | --- |
| 成功し適用直前も入力有効 | 通常物体2個を原子的に公開し、試行を終了・回収 |
| 候補なし、数値・形状・点Anchor配分・実cook結果・必要D6構築等で不成立 | Main Threadで入力一致を確認した場合だけ抑止を記録し、元物体維持・回収 |
| 通常切断等で前提変更、対象退役 | 成功も失敗も現在対象へ反映せず回収。新入力の抑止印を変更しない |
| 未実行、一時的なQueue／実行枠・予算不足 | 抑止を記録しない。未開始なら後の巡回へ、進行中なら同じ1件枠で延期するか抑止なしで取り下げ・回収 |

成功後の新しい所有構成へ親の不成立印を引き継がない。古い結果の不採用・印の無効化は7.9.4の照合を使い、専用Cache基盤・再試行Queueを作らない。GCは独自に有限巡回し、分割の抑止・成功・完了やその試行枠を実行条件にしない。この独立性も共有資源の競合による遅延がないことを保証しない。

Main Threadの構築・公開・回収も通常予算内とし、収まらなければ延期・見送りにする。別分離方式、強制消去、Proxy再生成、VFX救済、Work同期待機、Mainでの同期cook救済、通常切断の受付停止、優先度昇格、同Frame無限再投入を追加しない。

#### 7.9.6 承認済みの許容と適用範囲

| 許容する結果 | 省く要求／維持する境界 |
| --- | --- |
| 未実装・無効・延期・不成立でも元物体が残る | 通常切断の救済や過去Commitの取消を要求しない。両Phaseを省略してPhase 6へ進める |
| 一枚の平面による2分割で扱えない配置が残る | 任意形状の完全分離、N成分一括分離、最良平面を要求しない |
| 点Anchor配分・必要な建物D6構築を成立させられない対象を見送る | 代替Constraintや部分公開で成立させない |
| 通常切断先行で任意成果物が不採用になり、共有CPU資源・Bake枠の競合で通常切断が遅れる | 4.3／4.4の費用・順序の許容に従う。任意処理の完了を通常切断の公開条件にせず、失効成果物は利用終了後に回収する |
| 有限試行が不成立なら同じ入力で再探索しない | 完全な分離不能証明を要求しない。未実行・一時枠不足・古い結果を不成立へ混ぜない |
| 低優先のため実行・回収が遅れ、期限内完了しない | 公平化・必達期限・容量回復の同期待機を要求しない |
| GCまで不可視物体が接触・Impulse・支持を持ち、無効なら通常寿命まで残る | 即時削除を要求せず、対象自身のシステム所有Constraintを既存寿命で退役する |
| GCの当たり判定消失で周辺運動が変わる | 通常接触・移動だけで回収を禁止せず、過去の接触効果を巻き戻さない |
| GCで対象の質量も世界から消える | Siblingへ移送・再正規化しない。分割時の親質量保存は維持する |
| GC前の生成・必要cook・シミュレーション費用を負担する | 作らず消す最適化を追加しない |

不正Geometryの成功扱い、面積0 Triangleの削除、非finite／不正物理の適用、未計算を空とする判断、世代不一致公開、使用中資源解放、支持・安全Constraintの無断除去は許容しない。分割成立のために表示を消さない。

小さい通常物体の消滅は本節の対象外とし、LogicalFragment全体の任意寿命終了は7.10に従う。基本フェード、三角形霧散・Fallback Geometry消滅、消滅予定部分の生成省略は要求せず、先行Metadata、予約Buffer、専用状態を用意しない。旧小破片分類、被覆対応Graph、Shared解決専用機構、Shard／Atlas／専用Arenaを復活させない。

#### 7.9.7 完了条件と最小確認

無効時の通常切断を維持し、有効時は分割成功後に通常再切断でき、GC後は退役対象が復活しないことを完了条件とする。追加機能固有の確認は次の少数合成Fixtureに限る。T-007／T-059／T-074／T-086／T-091は各担当の世代競合、引渡し、点Anchor、資源寿命の検証を再利用し、Provisional固有契約や全matrixを本節へ複製しない。

| 確認群 | 最低限の期待結果 |
| --- | --- |
| 独立した有効化 | 両方無効・分割のみ・GCのみで通常切断が成立し、無効機能の候補仕事なし |
| 分割成功 | 離れた二つの閉Geometryを一つのConvexがまたぐ例と非交差Compound例で、探索から必要cook・公開まで1件の試行で進み、通常所有者2個を生成。実Capを含む面集合・World配置・正規質量・再切断基底を維持。単一Index範囲の内部に両出力の面がある場合も新範囲へ配分・転送し、使用中の旧範囲を早期解放しない |
| 分割見送り | 単一平面で分離不能、面近傍、片側有効Convexなし、質量不成立、容量／cook・必要な建物D6構築不成立で元物体不変。部分公開・架空境界・Geometry消去なし |
| 支持・frame | Anchorを持つOwner内の離れた島も固定され、分割後は7.1の点配分でAnchorがない側だけ動的になる。既存frame写像、OnPlane継承と旧共有資源からのAnchor非復活を確認する |
| GC選択 | 真の面集合0だけが候補。面積0 Triangleのみの非空、Renderer無効、画面外、未完了Geometry、可視Compound内の代表参照なしConvexは対象外 |
| 建物分割（Phase 5.6） | 成功時は7.2.2に従って建物由来の両子Depthを親から1段進め、Anchorなし動的子だけに新D6を生成して親D6を親Actorとともに退役する。不成立時は親Actor・Depth・D6基準姿勢・Limitが不変 |
| GC退役 | 接触中の表示なし物体を丸ごと回収し質量移送なし。対象Actorとその建物D6を既存寿命後に一度だけ退役する。共有資源・過去履歴・無関係Siblingを誤解放しない |
| 競合・寿命 | 通常切断・別更新後に古い成功公開と失敗抑止書込みを拒否。任意公開後の旧予測成果物も拒否。二重公開・解放・切断件数変更なし |
| 再試行抑止 | 同一入力の不成立後は再探索せず、通常切断・GCは継続。関係入力変更で再対象化し、剛体移動だけでは再試行しない。未実行・Queue不足は不成立にしない |
| 低優先・容量 | cook・適用・不採用回収を遅らせても未回収試行は全体1件以内。完了回収は新規投入のアイドル待ちにせず、Compound cook大量投入なし。利用可能枠0・Queue満杯でも同期回復・昇格・無限再投入なし |

専用Test ID、旧Shared／Debris試験の復活、大規模Benchmark、P50／P95／P99製品保証を追加しない。探索方式、保存layout、巡回頻度、製品予算、効果の実測は実装・測定時に決め、未決を通常切断の不成立やPhase 6着手の障害にしない。

### 7.10 任意のLogicalFragment GC

LogicalFragment GCは、生存LogicalFragmentを表示・物理を含めて退役させる、Phase 7.1の独立した任意最適化とする。Managed GCや7.9.3の確定空物理GCとは別の寿命Policyであり、7.9の追加分割・物理GCに依存しない。通常切断をGC予定に基づく生成省略経路へ変更せず、導入・省略条件は15章に従う。

候補走査・ヒント計算・退役開始は通常処理を優先し、Main Threadの残予算に余裕がある場合だけ進める。生存Fragment数、VB／IBやフレーム予算の逼迫を契機に、軽量な視野外判定・ユーザーからの距離等を選択のヒントにできる。方式・組合せ・閾値・巡回頻度・処理件数は実装詳細とし、列挙した方式の全実装を要求しない。逼迫を予算不足時の強制実行条件にせず、無効時は候補走査と専用の新規仕事を行わない。通常切断をGCの進行待ちにしない。

対象のActive PhysicsSplitTransaction中は除外し、退役直前に生存性、Transactionなし、対象参照と退役authorityの有効性を確認する。他FragmentのTransaction終了や対象Geometryの完成を必須条件にしない。非命中でも7.1.3の共通退役を使い、無関係Siblingを退役させない。GCのためのTransaction、Pending Cut、LogicalCutOperationを作らず、後着成果物の不採用・生存SiblingのGeometry進行・不要Workの終端は4.5.6／8章に従う。

VB／IB回収は4.5.3に従い、導入した回収をAbort等も含む共通退役出口へ接続する。GCの有効化・対象除外・開始予算を既存Abortの条件へ追加しない。開始済みの遅延回収は4.4の通常完了回収へ接続し、GCの新規実行条件や無効化で止めない。候補選択・退役・回収・管理の費用は既存予算とProfilerで扱い、既存機能への速度ペナルティをbest effortで避ける。追加費用ゼロや固定改善率は要求しない。

2026-09-13の人間承認により、小破片・動的物体に限らず、非空の可視物体や固定Anchor付き建物片もPolicy次第で対象とし、突然の消滅、質量・接触・支持の消失と周辺運動の不自然さを許容する。フェード・消滅VFX・運動補償を要求しない。固定PlayerLocomotionOccupancyは7.2.3のまま維持するため、消滅後も通行を妨げ得る。回収量・実行／回収期限・容量上限内への収束を保証せず、同期GCによる容量救済、Work／GPU同期待ち、予算超過での強制進行を追加しない。4.5.4の容量処理と利用中範囲の安全条件を維持する。

**導入時の最小確認。** Phase 7.1の実装時だけ、少数の既存または合成Fixtureで以下を確認する。既存の退役・世代・資源寿命試験を再利用し、新Test ID・専用基盤・全組合せmatrix・長時間の容量収束試験を追加しない。

| 確認群 | 期待結果 |
| --- | --- |
| 選択と既存経路の独立 | 無効・予算不足・対象Transaction中は新GCを開始しない。別FragmentのTransactionをObjectId全体の禁止条件にせず、GC無効時もAbortと開始済み回収は進む |
| 個別退役 | 非空Geometryを持つ対象を退役でき、Geometry未完成の代表例でも後着結果で復活せず、生存Siblingを維持する |
| ObjectId寿命と利用保護 | 親子の一体置換を全滅と誤認せず、生存Fragmentや利用者のあるVBを保持する。最後のFragmentがGCまたはAbortで退役した例で、未実行・実行中Workを含む必要な利用終了後に回収できる |
| 再利用と費用 | 回収したVBを実際に再割当し、CPU内容と必要なGPU更新を確認する。IB・未使用／失敗予約の既存回収を維持し、二重解放・利用中上書きなし。既存Profilerで通常経路の追加費用と回収効果を確認する |

## 8. 世代管理と非同期制御

Wave、実Hit、そこから派生した仕事・成果物は既存SlashIdで同じSlashへ対応付け、別Slashや再利用された格納先の古い結果と混同しない。整数幅、採番方法と内部参照保護は実装詳細とする。次の発射で以前の生存Waveを失効させず、Wave Expireだけで受付済み切断を失効させない。受付済み処理の採否は以下の対象・Pending・Transaction／Branch authorityに従う。

ObjectGenerationはObject全体のPrediction・Cache・Reset／退役・Trace・Maintenanceの粗い識別へ残すが、別LogicalFragmentだけの更新を通常切断の単独Reject理由にしない。通常受付は4.2、非命中所有変更は7.9、任意の単独退役は7.10に従う。

Physics CommitはSource生存性、SourceのActive CutOperationId、当該Transactionが持つ物理系譜・所有変更authority、既存Object生存性と4章の共通Player終了境界で照合する。自らのSource Final OwnerからProvisional構成への置換と通常のpose／速度／Sleep変化は失効理由にしない。別Transaction・Maintenance・GC・外部SystemのOwner／Shape／Constraint構成変更でauthorityを失った仕事はStale回収し、現在Sourceを退役させない。個別物理成立不能のAbortとは区別する。

Geometry Work／Commitは有効Pending、直前祖先Geometry Commit、当該Branchの生存子孫・表示・後続Work等の読者、表示を古いGeometryへ戻さないことを照合する。子孫切断や別FragmentのObjectGeneration更新だけで祖先を失効させず、履歴完成だけを継続理由にしない。Sourceが正常置換された後も必要な祖先Geometryを採用できる。非再利用LogicalFragment ID、Active CutOperation、既存Owner identity／世代、Pending親子関係で閉じ、新しい公開Fragment世代やObject終了latchを追加しない。

投機採用はBaseObjectGeneration、SlashId、LogicalFragmentRef、SlashFrameとPose／面・入力Snapshot、必要なら既存Anchor情報を照合する。Object全体を前提にしたPredictionは粗い世代不一致で不採用にできるが、受付後の通常Transaction／Geometryは上記の局所authorityへ従う。Workを強制中断せず完了後に回収する。

物理PendingはTransaction内のWork依存であり、Logical Publication後にはPhysics Pendingを残さない。固定／動的は所有Anchorから導出する。Geometryの外部状態変更は4.5.6のGeometry Commitに従う。

非同期成果物は完成後に上記の世代・前提・authorityと照合して適用し、古い成果物は適用せず回収する。一度適用した成果物を二重Commitせず、Work／Reader／Physics Step等が参照中の資源を早期解放しない。Work実行、成果物採否、公開、資源寿命の表現を共通状態enumへ統合せず、各Subsystemの境界に従う。

CutBoundaryRecordはGeometry Commit時に実在Boundaryだけを後着公開し、CutBoundaryId、CutPlaneId、正負の別子へのSide参照、必要frameと作成時世代を持つ。面／frame identityは19.5.1の採用面へ結び、元SourceSlashPlaneと混同しない。再切断に必要な履歴は維持するが退役Geometry／Actorを強く所有せず、Boundary完成をLogical Publication条件にしない。

Kerfは0とし、表示側はどの側にも移動を与えない（固定側の物理Impulseが0であることは7章による）。固定を理由に仮描画を省略せず、同一位置の正負Capを通常Colorで常時両面描画しない。

## 9. リグ付き人形の切断

関節をフリーズできるため、切断時点でアニメーション世界から静的破壊世界へ移送する。実際の現在姿勢とボーン行列をスナップショットし、4.5.2の同期経路または有効な先行準備から共通VP入力へ合流して、同じ採用Poseの即時clipと一般プロップと共通の切断処理を使う。Phase 4.52は先行準備なしの同期経路で基本切断を完了し、非同期ベイクの導入採用時だけPhase 4.72で先行成果物の再利用・未完成／成果物不採用時の通常経路を統合する。表示開始時に残る準備費用と表示開始フレームは4.5.2に従う。

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

7.9の後続任意処理は本章の既存GeometryとOwnerのAnchor入力を再利用し、専用Asset分類、Shard、逆被覆表、全Asset再生成、Schema改訂を要求しない。

| 層 | 用途 | 品質契約 |
| --- | --- | --- |
| 共用Cut Geometry | 通常表示、最終破片、即時Stencil Volume | 6章の閉鎖・edge／vertex manifold・局所winding整合済みTopology。複数submesh、Disconnectedな閉Component、Component間のIntersection／Overlapを許容 |
| 幾何Topology Metadata | 共用Geometryの切断・Cap生成 | 6.2のTopology／属性seam対応。切断中の作業構造と区別する |
| Physics Proxy | 接触とConvex切断 | 少数の低頂点Convex／Compound。各Convexは有効な閉凸形状だが、Compound内の相互Overlapを許容 |
| 固定Anchor入力 | 所有者全体の固定 | 固定を設定する入力だけが所有者とfiniteなFrame内local位置を明示する。点集合の配分・固定判定は7.1に従う |

本隊は完成済みAssetを利用し、製品Assetの生成・修復・再生成工程を必須としない。テスト・計測用の完成済みAssetと付随入力の受入れ・内容識別・所在解決は10.2.3、旧入力経路の既存依存は10.2.2に従う。利用側は6章／7章等の既存入力契約を満たす入力を用意し、不合格入力を切断対象として登録しない。必要なTopology・Physics Proxy・Anchor・建物属性をFBXへ一括格納する義務はなく、制作方法・設定形式は実装詳細とする。本隊だけでのAsset量産・自動修復・再生成を保証せず、製品化には別途Asset制作が必要になり得る。部品間の接着情報は保持せず、未指定の固定やAnchorの所有者を推測しない。

表示・実Cap・Stencil・次回切断は同一の共用Cut Geometry正本を参照する。正本は4.5.1に従い複数Geometry参照で構成してよく、役割別に並行更新・切断するUnity Mesh、基底、派生物、適否、Cacheを作らない。4.5.1のGPUコピー・作業構造・旧世代・投機Snapshot・未採用VPは許容する。製品用Strict Solidの扱いは2章に従う。

#### 10.2.2 Phase 0.2の既存依存と撤去

Phase 0.2はObsoleteな旧入力経路とし、新しい利用元・用途・Fixtureを接続せず、旧系列の拡張・再開を行わない。既存のSandbox、Editor検証、回帰テスト、公開Synthetic／非公開Licensed入力の利用だけを暫定維持する。用途が依存する間は必要な読込み・動作を維持し、そのための既存入力からのローカル生成や修正は許すが、旧形式・Reader・再生成手順の恒久互換は保証しない。

既存用途の移行または終了に合わせて旧依存を減らし、不要になったPhase 0.2固有のコード・成果物を削除し、最後の依存解消後に本節の暫定規定も削除する。移行に必要な変換・置換は許可する。外部の完成済みAssetは10.2.3へ、Synthetic回帰入力は通常のテストへ必要な範囲で移せばよく、全成果物の移植・一括移行・専用migration基盤を要求しない。共用Harness・Verifierは撤去対象に含めず、必要な回帰範囲と10.8の公開／非公開・利用許可境界を維持する。Obsolete属性や依存確認方法は実装詳細とし、参照件数の固定・永続baseline・専用CI検査を必須にしない。

#### 10.2.3 Phase 0.21 Reference Asset Intake

先行独立研究、日々のAsset作業、外部ツール等から、利用許可のある完成済みAssetをテスト・計測へ新規に受け入れる唯一の正規経路とする。新しい利用元は本経路へ接続し、Phase 0.2へ依存を追加しない。通常のSynthetic入力・テスト内の手続き生成は本入口への登録を要求しない。Datasetは参考実行のサンプリング母集団であり、全件の実行義務や製品への適合を表さない。旧経路の既存用途を移す場合も利用側に必要な範囲で接続し、全Consumer共通APIや全依存の移行を0.21完了条件へ追加しない。

**受入れと内容識別。** 正本は書出し済みFBX、読込みに必要なTexture、および処理入力として明示登録する付随ファイルの実体とする。付随ファイルには、FBX外で用意するTopology・Physics Proxy・Anchor・建物属性等が該当する。各実体をSHA-256で追跡し、その内容・組合せ・役割・対応関係をAsset SHAで識別する。DatasetはAsset SHAの集合を正本とし、その集合から内容revisionを識別する。付随入力の追加・変更はAsset SHAへ反映するが、今回の拡張だけで既存のFBX＋TextureのみのAsset SHAを変更せず、全件再登録を要求しない。処理に使わない元の`.blend`、Recipe、Script、研究repository、作業メモ等は任意の参考情報とし、外側の配置・表示名・参考情報だけの変更は内容更新にしない。生成工程の本隊移植・再生成・byte一致・由来監査や、研究側の可変な作業treeへの依存を要求しない。再試験用に保持するrevisionは付随入力を含む参照実体も保持するが、全履歴の永久保存や旧形式Loaderの維持は要求しない。

**配置と所在解決。** 初期配置先は非公開Asset repositoryの`Working/Phase0.21`とする。directory名から用途・Recipe・版を推定せず、登録情報とSHAで対象を解決する。結果から実体の所在を引き、存在する場合は隣接する原資料等も容易に開けるようにする。登録外の周辺ファイルを暗黙の入力にしない。登録・索引・保存形式・SHA算出方法・操作UIは実装詳細とし、公開／非公開と利用許可の境界は10.8に従う。本入口への登録自体は製品採用や共有許可を与えない。

**参考実行。** 明示指定したDatasetまたはAssetについて、利用側Harnessが入力準備・適否判断・対象処理・サンプリングと実行量を決める。各Runは、使用したDataset revisionまたはAsset SHA、実際の入力・処理・条件、成否と得られた観測値を利用側Harnessの実施記録へ残し、実行中は選択したAsset SHA／Dataset revisionの入力内容を使用する。切断面・実行設定等のファイル外の条件はRun記録へ値として残す。失敗・入力不適合・未対応・未実行を成功とせず、比較可能な項目だけ固定入力の結果と比較する。少数Assetを限定した処理へ通すことから始め、全Assetと全試験条件の直積、完全巡回、全Consumer対応を要求しない。FBXと登録した付随入力から必要な数値入力・Topology等を得られるかは利用側で確認し、見た目の一致だけを同じ入力の証拠にしない。入口で推測修復せず、既存のGeometry／Physics入力条件とRuntime／オフライン検証の分離を維持する。

**標準実行との境界。** 参考Datasetの有無・更新・結果は、標準回帰・Benchmark・CIの対象・集計・合否やPhase完了条件を自動変更しない。標準実行は本入口を暗黙に探索せず、更新型Datasetの将来revisionへ追随しない。参考実行の未実施・失敗・未観測・乖離解消・結果閲覧を進行Gateや自動の未完了負債にしない。ただし、調査で確認した既存必須要求への違反は、その既存基準で扱う。発見だけでDataset全体を必須回帰へ昇格しない。既存要求を確認する個別AssetやSynthetic回帰ケースの追加・差替えは、対象を固定して通常作業として行え、個別の人間承認を要求しない。必須対象・評価基準・Phase完了条件を拡大する変更だけを人間判断とし、本書の契約を変える場合は改訂する。

### 10.5 Synthetic Fixtureとの区別

`Synthetic Watertight Test Fixture`は切断Kernel、Cap Loop、反復切断、cook確認の既知正解入力であり、製品AssetやRuntime同梱物ではない。異常系Fixtureには意図的なBoundary、Non-manifold、自己交差、重複面等を持たせてよい。実Assetが同じ条件を満たしても、それだけで製品採用や代表Assetの合格を意味しない。

### 10.8 公開リポジトリとライセンス境界

公開可能なコードと合成Fixtureは公開できる。Synty／Poly Pro Universeの入力Asset、`.unitypackage`、付属`.meta`、生成された共用Cut Geometry、Physics Proxy、加工済み断面素材は公開しない。非公開Asset・派生物の配置先をgitignoreし、公開履歴への混入をCIで検査する。

Synty POLYGON City Packの購入原本は、公開Unityリポジトリと分離した非公開Git LFSリポジトリ`C:\Users\%USERNAME%\src\zantetsuken-assets-private`で管理する。2026-08-26時点で、`Vendor\Synty\POLYGON_City\v5\Original`へ`POLYGON_City_SourceFiles_v5.zip`と`POLYGON_City_Unity_2022_3_v1_12_4.unitypackage`を格納済みであり、両ファイルはLFS対象である。ダウンロード元と格納先のSHA-256一致を確認済みとする。

非公開リポジトリへのアクセスは各Assetライセンス上の許可を持つ開発チームだけに限定する。購入原本は変更せず保存し、展開したFBX／Texture、非公開Fixture／Asset対応表、加工済み共用Cut Geometry、Physics Proxyなどのライセンス派生物も公開Git履歴へ入れない。公開リポジトリから参照する場合も、公開Submodule、公開Release、公開CI Artifact、共有Cacheを経由してAsset本体を配布しない。

公開CIは公開可能な合成入力で切断ロジックを検証する。Syntyを用いる処理と製品ビルドは、許可されたローカル環境または限定private runnerだけで実行し、公開Artifactと共有Cacheへ生成物を残さない。

## 11. モーション方針

モーションは原則として既製HumanoidクリップをUnityでオフラインリターゲットし、19.3の骨Pose TableへBakeする。NPCはIdle、Walk、Run、Turn、Startled、Run Awayを初期最小セットとし、Pose Layer／MirrorとプレイヤーIKのscopeはD-136に従う。切断時は現在姿勢を固定して物理へ移行するため、切断方向ごとの専用死亡モーションは作らない。

NPCのCurrent／Futureは19.3の共通Table評価を使い、RootとAnimation入力の計画は20章に従う。命中時には実際に表示したBone Poseをスナップショットし、既存の予測・実Pose照合を維持して静的破壊世界へ移送する。

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
| D-013 | 開発順序 | 非VR PoCと性能評価を先行し、最小XR確認、Slash UXの段階実装と実機調整、選択済みVP経路のXR／即時Clip／Stencil能力確認を15章の依存順で進める。新設Phaseは実装層や成果物契約を増やさない | 人間承認済み、2026-09-11 |
| D-014 | 検証HMD | Quest 3Sを有線Quest Linkで初期PCVR検証に使用 | 確定 |
| D-015 | 攻撃演出 | 有限速度で飛翔する斬撃波を19.1.8の表示専用VFXで描く。接触時の即時分離を目標とし、準備待ちの扱いは4.5.2に従う | 人間承認済み、2026-09-13。VFXの形状・視覚的接触の許容は19.1.8に従う |
| D-016 | 先行計算 | 到達猶予で未来姿勢、表示／Stencil共用VP Geometry、Convex切断を投機評価 | 確定 |
| D-017 | 未来評価 | 既存の未来イベントDAGと世代・前提検証を維持し、Ready Workを4.3／4.4へ渡す。別のCPU Schedulerを作らない | 確定。未投入取消、完了後採否と各Subsystemの公開境界を維持する |
| D-018 | 自由飛行剛体の直接予測 | 剛体の未来運動は19.3の直接予測Gate内だけをPhase 4.54でO(1)予測し、対象外・前提不一致は4.51へ進む。静止／姿勢固定の4.53とAnimation／MobPlan評価は維持する | 人間承認済み、2026-09-13。接触・転動の先行率改善を製品範囲から外し、命中後の処理費用・準備待ちは現在状態経路で引き受ける |
| D-019 | 文書管理 | 本Markdownを唯一の設計正本とし、DOCXは使用しない | 確定 |
| D-020 | 観測基盤 | 21章のProfiler・Trace・Captureで性能、因果関係、対応画像を確認する | 確定。検証用形式と手順は17章の実装詳細とする |
| D-021 | ログ方針 | 任意の開発診断の記録は21.17の簡易JSONLロガーへ統一する。既存Traceの状態遷移はenumと整数IDで記録し、高頻度の文字列生成とDebug.Log連打を避ける。簡易ロガーの費用・欠落許容は21.17に従う | 人間承認済み、2026-09-20。既存Profiler・Trace・Captureと幾何・物理の整合性は維持する |
| D-025 | 公開Repo | Licensed入力と派生Assetはローカル／許可された非公開環境に限定し、公開Git履歴・Artifact・共有Cacheへ含めない。詳細は10.8に従う | 確定 |
| D-029 | Unity実行環境 | Unity Hub管理領域のUnity 6.3 LTS 6000.3.22f1を使用し、ProjectVersion.txtで完全固定する | 確定 |
| D-030 | Repository構成 | 専用Repo直下をUnity Project Rootとし、ユーザーパスは%USERNAME%で匿名化する | 確定 |
| D-031 | Unity CLI | PoC初期は使用せず、固定版Unity.exeのbatchmodeを基準にする | 確定 |
| D-035 | 刀姿勢入力 | 右手ControllerのOpenXR Grip Poseと単一のGripToKatanaOffsetで刀1本の位置・回転を決定する（19.1.11） | 人間承認済み、2026-09-13。PoC・初期製品の左手持ち・持ち手切替・二刀流は対象外 |
| D-036 | 片刃判定 | 刀身軸方向を除いた運動とEdgeDirectionの緩い内積Gateで、峰側の復路を除外 | 確定 |
| D-037 | 刃筋難度 | SideNormal横滑りや厳密な角度を不合格条件にせず、遊びやすい判定を優先 | 確定 |
| D-038 | 刀の衝突 | 刀へ物理反発Colliderを付けず、有効な論理Sweep以外は全オブジェクトを素通り | 確定 |
| D-039 | 追跡異常 | Pose無効時は未発射の振り候補とSample履歴を破棄し、再追跡直後の見かけ速度からSlashを生成しない | 確定 |
| D-040 | Unity更新 | プロジェクトを作り直さず、Hubで新旧Editorを並存し、Gitアップグレードブランチ上で変換・回帰検証する | 確定 |
| D-041 | Unityディレクトリ | Unity Project Rootは1つを正本とし、版別の恒久コピーは作らない。同時比較時だけ兄弟Git worktreeを使用する | 確定 |
| D-042 | モブ未来計画 | 現在Sceneを未来へ進めず、RootとAnimation入力が整合する有効な計画を切断先行評価へ利用する。具体方式は20章に従う | 確定 |
| D-043 | MobPlan世代 | MobPlanへPlanGenerationと前提条件を付け、介入や経路変更時は旧計画と依存する投機結果を無効化する | 確定 |
| D-044 | AI LOD | 介入しやすい対象の応答を優先し、猶予のある対象では計画再利用で費用を抑える。Tier・判定指標・更新頻度は20章に従う | 確定 |
| D-045 | 遠距離モブ | 介入までの猶予を有効なRoot・Animation計画と切断先行準備へ利用する。特定の経路生成・予約方式は要求しない | 確定 |
| D-046 | MobPlan Commit | 未来モブ姿勢に基づく切断成果物は、実命中、ObjectGeneration、PlanGeneration、姿勢許容誤差の一致時だけCommitする | 確定 |
| D-052 | 候補範囲 | 候補範囲は投機専用で、実HitはSlashWave Segment Sweepだけで確定する。有限包絡がなければ全Hitを含まない先行準備範囲を使い、範囲外は現在状態経路へ進む | 確定。19.1.10／Phase 4.53 |
| D-060 | PoC録画負荷 | 21.7／21.15の選択的Captureを使用し、非待機とbounded資源を維持する | 確定。早期の録画条件は実装詳細 |
| D-063 | Capture相関 | 撮影した画像とFrame・対象・処理を対応付ける。 | 確定。Record形式は固定しない |
| D-067 | cooking非同期化 | Bake／cookingは即切断表示・初回仮運動のクリティカルパスから外す。表示は4.5.2の準備後に開始し、実Geometryは4.5.6の現在の参照・frameと転送条件で公開する。固定側も描画するが動かさない | 確定 |
| D-069 | 物理分裂Commit | Bake済みConvexの完成後、物理ステップ境界で左右Rigidbodyへ分裂し、親の線速度・角速度から各重心位置の速度を継承する | 廃止（D-132でProvisional生成時の分裂とFinal handoffへ置換） |
| D-070 | Cooking Profile | 初期製品は7.3の単一ProfileをBakeとColliderへ同一指定する | Phase 4で構成を選ぶ |
| D-074 | 全体低重力 | 空中斬り猶予を増やすため世界全体を低重力にし、PoC仮値を約0.5Gとする。周辺物理値は先に作り込まずプレイ後に判断する | 技術検証付き確定 |
| D-075 | 重力一元管理 | `WorldPhysicsProfile`を正本とし、Unity Physics、未来予測、解析軌道、VFXへ同じ重力を供給してRunごとに記録する | 確定 |
| D-076 | 即時Shadow | 即時切断中は同じper-instance clipを適用した両面ShadowCasterで影を近似し、Shadow Map用Stencil断面は描かない | 技術検証付き確定。2026-09-20追記：表示専用Offsetの撤去にともない「分離Offsetも同じく適用する」部分を削除した。ShadowCasterとColor Passが同一のclipと配置を使うことは変えない |
| D-077 | Shadow Batch | Shadow描画をStable片面群とPending両面群へ分け、切断平面は固定長Instance Recordで渡して平面値・切断数によるDraw分割を避ける | 技術検証付き確定 |
| D-078 | 有限仮キャップ | 即時キャップ板をローカルOBBと切断平面の3～6頂点交差多角形から生成し、他のTemporary Render Boundary半空間でclipしてからStencilで実輪郭へ制限する | 確定 |
| D-079 | Stencil Color割当て | 左右眼いずれかで保守的な可視Cap Boundsが重なる非互換対象を通常Colorでは分離する。Conflict Graphは論理モデルに限り、全Graph構築、全組合せ走査、Greedy Coloring、stableなColor番号を要求しない。`MaxStencilColors`へ収まらない対象は最後のColorへ統合する | 技術検証付き確定 |
| D-080 | Stencil互換Group | 全World Cut Plane、Side／半空間、Cap描画状態が一致し、6章の共通入力Gateに合格したGeometryは向きと符号を保存したまま同じStencil Colorへ加算できる。Maskの意味は`sum(W_i) > 0`であり、正逆相殺による欠落を許容して幾何学的Unionを保証しない | 技術検証付き確定 |
| D-081 | 両眼Cap可視性Cull | 論理破片×切断面ごとに5.6のFacing条件で判定し、全Capが除外される互換Groupは彩色前にStencil Clear／Volume／Cap処理から除外する | 技術検証付き確定 |
| D-082 | Stencil競合領域 | 集計後に`S != 128`となるResidual Stencil Supportを可視Cap Boundsで保守的に包み、Raw Stencil書込みの途中重なりは競合としない。各眼でOBB投影または可視Cap Boundsのどちらかが非交差なら通常Colorの共有を許可する。実StencilからSupportを検出・監視しない | 技術検証付き確定 |
| D-083 | バックグラウンド実行基盤 | 4.3のurgent Unity Job、二つの低優先度OS PoolとBurst実行、MainのPコアCPU Sets配置を採用する。Unity状態適用はMainに残す | 人間承認済み、2026-09-18。G8/B2は暫定設定とし、Probeの成立を本隊統合・製品性能達成と扱わない |
| D-084 | Owner単位Cut/Cook | 受付済みPhysicsの新規投入は7.2のurgent Unity Job内Burst、MainのMesh適用、Unity JobのPhysics.BakeMesh、Final Commitへ分担する。投機・任意処理と命中後の継続は4.3／4.4に従う | 人間承認済み、2026-09-18。内部工程の別Job化を必須にしない |
| D-086 | Native採用Gate | Unity Built-in 3D Physicsの`Physics.BakeMesh`を製品経路の正本とし、Native PhysXを製品依存へ含めない。T-076を含む製品統合実測でUnity経路が性能要件を破り、Unity側で解消できず、別途承認したNative統合Prototypeが成立した場合だけ、切断破片のQuery／接触／Scene同期を含む物理経路の部分置換を再検討する。Cook時間の倍率差だけでは置換しない | 確定 |
| D-088 | 閉Topologyの自己交差契約 | Topological Watertightと自己交差のないGeometrically Valid Solidを区別する。自己交差はD-117の共用Geometryで許容する。個々のPhysics Convexには自己交差を許可しないが、Compoundを構成する別Convex同士のIntersection／Overlapは許容する。Geometrically Valid Solidを共用Geometryの入力条件にしない | 確定 |
| D-090 | ライセンスAsset保管 | Synty購入原本と派生物は、公開Unity Repoの兄弟に置く非公開Git LFS Repo`C:\Users\%USERNAME%\src\zantetsuken-assets-private`で管理し、許可されたチーム以外へ共有しない | 確定 |
| D-091 | 固定物体の切断 | FixedSupportAnchorをOwnerの独立した点集合とし、7.1の点位置による配分・固定判定を使う。7.6のConvexだけによる受付・No-opを維持する | 人間承認済み、2026-09-17。Convex／Cell対応を撤去し、点配置による固定先の変化・離れた部分の固定・浮遊を許容する |
| D-092 | ゼロ幅切断 | Kerfは0とする。両側固定でも通常の仮描画・実切断・Cap生成を行う | 確定。2026-09-20追記：表示専用Offsetの撤去にともない「固定側のOffsetは0」を削除した（物理側のImpulseが0であることは7.1のまま） |
| D-093 | 切断痕の許容 | 固定側のCapと実Geometry境界の細い亀裂、輪郭線、線状Z-fighting、軽微なチラツキを許容する。通常Capは片面描画とし、正常な正向き閉Shellの面状Z-fighting・Cap欠落・混入は5.2の明示的例外以外では不具合とする | 確定 |
| D-094 | 状態の粒度 | 物理分裂、Geometry完成度、Work Result採否を独立管理する。所有者の固定／動的と表示の可否を分離する | 確定 |
| D-095 | Anchorと公開の実装順 | Phase 1で正負子・切断履歴の公開とOwnerの点Anchor配分を合成入力で確認し、Phase 4で実Convex・Actor・cookへ接続する | 確定 |
| D-098 | Pending Cutと描画集合 | 4.2／4.5.6に従い、受付時のPending Cutと、Final Physicsと一体のLogical Publication、後着Geometry Commitを分離する。実体化した面のTemporaryだけを回収する | 確定 |
| D-117 | 表示／Stencil共用Geometry | 切断対象の表示・実Cap・Stencil・次回切断は、閉鎖・edge／vertex manifold・局所winding整合済みの同一の共用Geometry正本を参照する。正本は4.5.1に従い複数Geometry参照で構成してよく、一度の切断・Cap生成で同じ不変条件を各出力へ継承する。Self-intersection、別Topologyの閉Component間のIntersection／Overlap、Internal／Nested／Coincident、全体反転、Runtimeの面積0 Triangleは許容する。入力不合格や不正出力を用途別Geometry、修復、簡易表示Proxyで救済せず、全Meshのinside／outside、自己交差、向き正規化をRuntimeへ追加しない | 確定 |
| D-119 | 正符号8bit Stencil | 専用8bit Stencil Byteを128へ初期化し、IncrementWrap／DecrementWrapで`S=(128+W) mod 256`を得て`S>128`だけを描画する。Winding上界の証明・検査・容量分割は行わず、範囲外の誤描画は5.2の品質例外とする。8bitを排他利用できない構成ではゲームを開始せず、部分Bitや代替経路を作らない | 確定 |
| D-120 | Convex由来の質量特性 | D-133でFinal正本とProvisional近似を分離したため旧契約を廃止する | 廃止 |
| D-121 | 非Union標準Asset表現 | 共用Cut Geometryと幾何Topology、Compound Physics Proxy、必要な点Anchorを標準とする。別TopologyをUnionせず切断・Capし、通常結果を正負二集合へまとめる。Capは向きを保存してsum(W_i)>0で描く。製品用Strict Solidは生成・常駐・Fallbackしない | 確定 |
| D-124 | Player非接触 | D-131へ置換 | 廃止 |
| D-127 | 即時Clip Plane予算 | 即時切断は5.2の最大8面の`SV_ClipDistance`と既存Plane overflow規則を使用する。選択外の面は背景Geometryへ委ね、左右眼・全対象Passの選択一致を維持する | 人間承認済み、2026-09-13。9面目以降は5.2のIgnoredによる表示遅れ・影の差と、残存Cap板の最後の統合Colorでの表示・Depth品質例外を許容し、表示遅れの時間・発生頻度を保証しない。T-089で確認。残存Ignored Cap板に関する取扱いはD-180で置換（2026-09-19）。最大8面、左右眼・全Passの選択一致、表示遅れの時間・頻度を保証しない判断は引き続き有効 |
| D-130 | 有限予算Dispatch | 4.4の用途別投入、有限容量・Main予算、urgentの投入余地、非blocking回収と同一フレーム内の進行を採用する。未投入・後続Workは命中後の用途で分類し、投入済み投機は継続する | 人間承認済み、2026-09-18。同順位stable順・投機Deadline順・投機とMaintenanceの全順序を保証しない。Queue満杯に限らない下位処理との競合・GC停止・必要な投入済み投機の待ちを許容する。受付済みWorkの複数フレーム遅延、仮表示・仮物理の長期化と後続受付見送りを許容し、同期救済・先着競争は追加しない。確認はT-090 |
| D-131 | Player非接触の限定保証 | Player Body／Handとプロップ／破片はPhysX非接触とし、刀と斬撃波は論理SweepでInteractionする。人工移動はD-166の固定Occupancyへの候補次姿勢Overlap Rejectだけで扱い、実空間HMDはClampしない。Camera近傍視界保護（Fade／Vignette／Mask等）とその専用検出・状態・設定をPoC・初期製品から撤去し、代替機構を追加しない。Camera被り、未登録物体を含む内部視点、Near Planeでの内部面、5.2の仮Cap品質例外が隠されずに見えることを許容する。Camera overlapを切断・物理・Geometry Commit失敗へ昇格せず、そのためのCamera／物体の強制移動、完全Mesh検査、Stencil修復、Job取消・再発行、同期Fallbackを行わない | T-088付き確定。2026-09-10、人間承認により視界保護を撤去。通常視点のCap品質とStable Geometry契約は維持する |
| D-132 | Provisional Rigidbody／Collision Proxy | 7.1の短寿命Transaction内で旧Cooked Convexを再cookせず共有し、必要な2 Actorをall-or-none公開する。7.2の質量近似・初回速度継承・Actor優先Final handoffを使う | T-091で確認。成功・Abort・退役は7.1を正本とする |
| D-133 | Final質量正本とProvisional近似 | Final質量正本は受付時の親Rigidbody質量。7.2の採用Convex集合による近似と親質量保存を使い、重複控除・Boolean Union・Convex別配分系譜を要求しない。Provisional一時massはFinal正本にしない | 人間承認済み。T-085／T-091で確認 |
| D-134 | Mob計画方式の研究後採用 | 移動・Animation計画、保持・更新・AI LODの具体方式は先行独立研究後に人間が選定し、Phase 4.70で20章の意味契約へ接続する。方式未決の間は固定計画による接続検証を進められるが、製品計画機能の完了とはしない | 人間承認済み、2026-09-13。同じSeedからの計画再生成・経路hash一致とMob計画固有の無割当保証を外す |
| D-135 | NPC Pose Table一本化 | 製品RuntimeのCurrent／Futureを19.3のリターゲット済み骨Pose Tableと共通評価へ統一する。対象時刻と明示入力を正本とし、オンライン代替Backend・方式再比較を要求しない。品質・容量・費用は本体で確認する | 人間承認済み、2026-09-13。方式統一のため追加費用を引き受け、高速化を保証しない |
| D-136 | IK／Pose Layer scope | 初期予測対象NPCのLook、腕／Foot IK、左右反転等のオンラインPose補正はCurrent／Future双方で対象外とする。導入は別の設計変更とし、将来入力Schemaを先行させない。実測Controller姿勢によるプレイヤー腕IKは別scopeとする | D-009を置換。T-018付き確定 |
| D-138 | 短時間NVENC確認 | Phase 0.11は21.15の実際に使用するCapture経路での複数Frame確認と、非待機・容量・寿命・故障分離で完了する | 完了、2026-09-14。固定録画条件・内部方式・試験階層を維持する義務を外す |
| D-148 | 旧入力経路の撤去とAsset受入れの統一 | Phase 0.2への新規依存・拡張を止め、既存用途は10.2.2の暫定維持・段階撤去に従う。外部完成済みAssetの新規受入れは10.2.3へ統一する | 人間承認済み、2026-09-18。旧Phaseの再開・全成果物移植・恒久互換を要求しない |
| D-149 | 剛体Local Plane実姿勢リベース | 19.5.1の自由飛行剛体だけ、予測Pose差の一致判定を世代・前提と実姿勢での面誤差Gateへ置き換える。攻撃SourceSlashPlaneは不変、命中前の仮Local Planeを命中時に一度だけ採用／Fallback確定し、7.6の受付判定と、受付済みのTemporary／Provisional／Stable／Finalへ共通使用する。面採否と受付判定はMesh／Collider Readyから独立し、未完成だけを理由に切断位置を変更しない。対象ごとの面差と固定点近似の未検出差を許容するが、Actor pose／速度を予測または命中Snapshotへ戻さず、自前B-repの包含・支持安全・世代検証を維持する | 確定。4.2／19.1／19.5の姿勢一致規則に対する限定例外。D-046のMob／Skinned契約は変更しない。O(1)直接予測式の標準採用とは独立 |
| D-159 | 可変長Trace導入 | Phase 0.12～0.14で21.16のWriter、Paged History、保存／読込みを段階導入する | 確定。旧形式の読込み維持は要求せず、実行時の所有権・非待機・容量境界は維持する |
| D-160 | コミット後の任意分割・物理GC | 7.9を正本として、確定後の単一平面による通常物体2個への追加分割と、共用面集合0の物理所有単位の寿命終了を独立した任意機能とする。既存の低優先Dispatchと非命中公開・退役を使い、追加分割は全体1未回収試行と入力別不成立抑止で管理する。通常切断・過去Commitを救済対象にせず、実行不能なら元物体を残す | 人間承認済み、2026-09-08。Phase 5.6／5.7は省略可能。許容事項は7.9.6、最小確認は7.9.7。実装・実測済みを意味しない |
| D-161 | 実行時表示表現と描画ロードマップ | 4.5の読み込み時Mesh、切断時VP、CPU AoS／Index正本とGPUコピー、範囲所有権、保持中のVertex不変・初期の追記保持と任意Phase 7.1での回収・再利用（4.5.3）、退役Index再利用を採用する。通常切断は単一Index予約へ正負を直接配置し、物理採否に依存する再集約を行わない。必要転送と現在の参照・frameで表示公開し、CPU範囲Publishedは内部処理とし、Final Physics／Logical Publication後のGeometry Commitと祖先順Kernelは4.5.6に従う。Phase 0.9～0.94でStage 2まで比較する | 人間承認済み。準備・変換の表示開始負荷とGPU拡張STWを許容し、容量限界は4章の共通Player終了に従う。Stage 2高速化は必達でなく、効果が乏しければStage 1を採用できる。Phase 5.6のIndexコピー・新旧共存費用を許容する。実装・実測済みを意味しない |
| D-162 | 未来予測用SkinnedMesh 非同期ベイクの採否判断 | 4.5.2を正本として、Phase 4.65で不変Rig Poseから共通VP入力を生成する限定実装を同期経路と比較し、人間が導入の採否を決める。採用時だけ既存DAG／VPプールへの本体接続、4.71の未来VP準備、4.72の人形先行切断統合を行う。通常命中の同期SkinnedMeshRenderer.BakeMeshと通常SkinnedMeshRenderer描画は維持する | 人間承認済み、2026-09-09。効果がなければ導入見送りも4.65の正常完了とし、人形の先行準備による命中時負荷削減を必達にしない。対応範囲の限定と非bit一致は4.5.2に従う。総時間短縮・翌フレーム完成は保証せず、4.52は採否待ちにしない。実装・本体実測済みを意味しない |
| D-163 | 物理Convexの内接削減 | 7.2に従い、L超過出力を通常clip結果の内側に収まるL以下の有効なConvexへ削減する。方式・対象・頂点由来・決定性の範囲は実装詳細。成立しなければ当該Physics処理を失敗とする | 人間承認済み。接触・質量特性の近似許容は7.2、失敗終端は7.1／7.9に従う |
| D-164 | 正負二集合と直接Index出力 | 7.2.1／7.6の正負集合と点Anchorによる固定判定、4.5.6の単一予約への正負Index連続出力・新規転送1回／再利用時0回を使う。島の独立化は任意Phase 5.6へ置く | 人間承認済み、2026-09-10。一体運動・空中浮遊・接着情報喪失・遅延分離・分割不能・Bounds拡大・倒壊と任意分割のIndexコピー／新旧共存を許容する。完全分離・倒壊防止・性能改善を必達にしない |
| D-165 | Phase 4.3 建物World D6と一般外部Joint撤去 | 7.2.2を正本とし、建物由来の動的な1→2物理分裂子へ独立World D6を一つ生成する。垂直並進Free、水平並進・全回転Limited、3値によるDepth別指数Limitを使う。一般外部Jointの継承・付け替え・GC保護・予測を撤去する。建物は製品の切断対象、道路は非対象とする（O-005解決）。Runtime本体は独立Phase 4.3、任意分割・GC統合は5.6／5.7 | 人間承認済み、2026-09-10。7.2.2の品質・運動・費用・製品入力制限を許容する。Depthは7.2.2の予定値をProvisionalと正式子で共有し、非建物はfalse／0を維持する。実装・拘束効果の検証済みを意味しない |
| D-166 | 固定Locomotion Occupancyと退出系撤去 | 7.2.3を正本としてLevel初期化時の固定Primitive集合と候補次姿勢Overlapによる要求全体Rejectだけを採用する。動的追従、ForcedOccupancyOverlapと退出状態・探索・専用ID・Profile・容量・作業領域、および退出系の専用試験を撤去し、Reject TraceからPolicyと侵入深度を削る。O-040を解決する | 人間承認済み、2026-09-10。切断・移動・退役後の通行境界不一致、未登録Geometryへの侵入、薄壁の飛越え、slide・部分移動なし、Lean後の人工移動停止、配置前提違反時の自動復旧なし、Reject詳細観測の喪失を許容する。Fade撤去と通常Geometry・物理契約は維持し、Runtime Occupancy更新は必要になった場合に別変更で決定する。実装済みを意味しない |
| D-167 | 単一Segment SlashWave | 19.1のLatch／Frame／Span Candidate／Close境界、Raw候補とAcceptedSpanのrunning maximum、単一Segment、WaveLifetime、19.1.8のSlashWave VFXと開発UIを採用する | 人間承認済み、2026-09-11。VFX簡素化は2026-09-13承認。完了済み区間は再評価せず現在区間の増加領域へのHitを許容する。Estimator切替は生存Waveを変更せず、一時状態の寿命をWave内に閉じる。19.1.6の容量満杯時の新Latch見送り・非遅延発射と、Expire先行による末尾区間の命中抜けを許容 |
| D-168 | 現在採用Convexと系譜Hit消費 | 19.1.7／19.1.9の4端点の閉凸包Sweep（退化を含む）と現在採用Convexを正本とし、直接消費したLogicalFragmentRefだけを一時保持し、7.1.2の生成元関係をたどって祖先を判定する | 人間承認済み、2026-09-11。受付見送りでも同Slashでは再試行せず、無関係Fragmentは個別Hitできる。Slash専用の系譜Cache・通知・恒久的な消費履歴を追加しない |
| D-169 | 基本Playable先行Phase | 0.55でUX、4.50～4.52でWaveと現在状態切断を先行完成し、Predictionを後段へ分ける。4.1は性能曲線、Slash Deadlineへの適用は4.53とする | 人間承認済み、2026-09-11。15章の依存・省略条件を正本とし、4.55の内部方式は変更しない。Traceの現行相関は21.16.6に従い、旧形式の扱いは17章に従う |
| D-170 | 第一候補のGuide Ray交点 | 19.1.5.1のBegin剣先方向T、Begin→Latch Emitter chordのS、Live／Frozen Guide交点r／q、Invalid保持とClose後勾配を第一候補とする | 人間承認済み、2026-09-11。具体epsilon・q許容等はUI調整。Clamp、別交点Fallback、軸回転を追加せず、比較方式は同じ出力境界内で交換できる |
| D-171 | 断面色と最小デバッグ | 5.3に従い通常は仮断面と実断面を共通トゥーンの固定グレー、デバッグ有効時は仮断面を赤、実断面を緑とする。実Capの固定負UV markerは表示色選択だけに使い、処理経路色と専用表示契約を撤去する | 人間承認済み、2026-09-11。元Assetの負UVによる通常表示・デバッグ表示の誤表示を許容し、UV検査・修正・登録拒否を追加しない。O-004を解決 |
| D-172 | 建物Assetの一般化 | Structural Slab系列を撤去し、建物も一般Geometry／Compound Physics Proxy／点Anchor／World D6で扱う。旧生成物への既存依存は10.2.2、建物属性の指定元は7.2.2に従う | 人間承認済み、2026-09-11。壁板数・Box対応・下端両側Anchor・入口用箱分割の固定条件を外し、物理近似とAnchor配置の違いを許容する |
| D-173 | 共用Geometry切断／Player終了 | 共用Geometryの出力は6.4の構成契約で保証し、製品Runtimeの出力検査を撤去する。継続不能な4条件は4章の共通Player終了へ集約し、対象別の限定退役・復旧と終了前の完全診断保存保証を撤去する | 人間承認済み、2026-09-11。入力Gate・メモリ安全・通常の世代照合・予約不足再実行・物理Fault・通常Captureの診断保存能力は維持する |
| D-174 | Provisional Sibling D6 | `ProvisionalSeparationConstraint`は7.1の固定anchor-offset D6を採用し、方式比較・動的Limit決定・Actor／Joint Pool導入判断を初期要件から外す | 人間承認済み、2026-09-11。D-132の方式とO-044の法線Limitを確定。有限区間による引止め・運動差を許容し、7.1の既存境界から判明した異常の扱いと資源寿命に従う |
| D-175 | 短寿命PhysicsSplitTransactionとCommit直列Geometry | 4.2、4.5.6、7.1～7.7、8、21章に従い、Final Physics／Logical Publication、祖先順Geometry Commit、個別退役へ整理する | 人間承認済み、2026-09-12。物理成立不能によるSource退役と正常退役に起因するIncompleteOperationTraceを許容する。新しい公開ID、状態・Reason、監視・復旧、GPU部分範囲freeを設けない |
| D-176 | 任意LogicalFragment GCとVB回収 | 7.10の寿命Policyと4.5.3のPublished済みVB回収を任意Phase 7.1で導入する。前段への非前倒し・省略条件は15章に従う | 人間承認済み、2026-09-13。消滅・物理変化・固定Occupancy残存の許容と最小確認は7.10に従う |
| D-177 | Player Scripting Backend | 製品と異なるBackendの測定で製品性能を確定せず、Player固有の不成立を開発終盤まで持ち越さないため、3.3の標準Playerと早期・統合時の動作確認、14章の性能判断を採用する | 人間承認済み、2026-09-14 |
| D-178 | VP描画StageのStage 3維持 | 4.5.5のStage 3（Indexed Indirect＋属性Pulling）を現在の採用経路とし、Phase 1の表示・Clip・Stencil／Cap・Shadowをこの経路のまま維持する | 人間承認済み、2026-09-18。Stage 3のStage 2に対する性能優位は未比較・未確認であり、それを採用の妨げとしない。追加のStage 2／3比較を採用条件にせず、Stage 2の実装・試験も削除しない。Phase 0.94のStage 2採用と比較結果は過去の記録として変更しない。採用MSAA構成は本決定に含まず未決定のまま残す |
| D-179 | 採用MSAA構成 | 採用URP Asset（現在の品質レベルPCが参照するPC_RPAsset）のMSAAを4xとし、PC品質レベルのantiAliasingを同じ4に揃える | TL判断、2026-09-19。本人の実機比較を根拠とする。見やすさへの本人の回答と、設定採用の技術判断は区別する。根拠は、XR SimulatorでのMSAA無効と4xの比較（4xで描画時のColor／Depth-Stencilがともに4 sample、基本表示・Clip・Cap／Depth・2 Color・正負とOffset（当時の表示専用Offset。2026-09-21に撤去済み）・Shadowが両構成で成立し、両者の差は物体の輪郭に限られた）と、Quest Linkでの本人の目視比較（どちらがMSAAかを伝えずに4xを見やすいと回答）である。性能は測定しておらず、90fps等の達成やすべての場面での画質を保証しない。Playerビルドでは確認していない。Mobile品質レベルのURP Assetは変更していない。D-178時点で未決定とした採用MSAA構成は本決定で定める。資料はMSAA比較資料（MsaaDecision/20260919-device-comparison ほか） |
| D-180 | Ignored Cap板を残す制約の撤回 | 5.2の`IgnoredTemporaryClipBoundarySet`に属する境界から描画用Cap板を生成しない。D-127の備考にある「残存Cap板の最後の統合Colorでの表示・Depth品質例外」は当時の判断として記録に残し、本決定以降はIgnored Cap板を残す前提を置かない | 人間判断、2026-09-19。Ignored境界の即時断面は背景Geometry Commitまたは選択への編入まで表示されない。論理状態、Anchor、背景処理は変更しない |
| D-181 | 複数切断の即時表示の具体化 | D-180を具体化し、5.1／5.2／5.6に次を定める。Ignored境界では表示を分岐させず、同じ表示登録内で最初のIgnored境界より手前の形状を一つのRenderFragmentとして一度だけ描く。描画用clip・Stencil Volume・Cap板は選択済み境界だけから描画更新境界ごとに構築し、個別除去・compactionは行わない。候補・Side・選択状態は論理枝ごとに保持する。採用面のframe写像は呼出側が明示し、その適用範囲を登録した根からの系譜に限る。Snapshot構築・描画は一経路とし、同じ系譜の重複登録を拒否する | TL判断、2026-09-19。Cap Record・候補の保持容量は呼出側の明示容量とし、製品値は定めない。Phase 2およびT-089の完了を意味しない。実装と確認は未実施（2026-09-19時点の記述）。2026-09-20追記：実装済み（f528426、a90cd90、5581624）。現在の状況は5.6の「実装状況」を参照する。**同日さらに追記：表示専用Offsetの撤去にともない「仮分離Offsetは確定済みAnchor配分による系譜加算とし、Ignored境界は加えない」を削除した**（5.1）。集約の規則、frame写像の明示、一経路と重複登録の拒否は維持する。Phase 2およびT-089の完了は引き続き意味しない |
| D-182 | 複数切断Stencilの可視Cap欠落：人間の指摘と確認 | Quest Linkの実機確認（2026-09-19、A公開・B保留の2段切断）で、見た人が「上側の２回切れた直方体が断面ではなくて直方体の内面に赤い面ができてます」と報告し、確認を中断した。非XRの固定視点13組（単一切断、直交・斜交2面、3面、平行2面、9段目Ignored、Color設計2配置）で、現行方式（全Selected面でclipしたVolume）とCap別方式（自身の面・Sideだけでclipし、Capごとに初期化）を並べて見た人が確認した。現行方式は、複数Selected面の片を含む11組で「欠けあり」。Cap別方式は13組すべてで欠け・はみ出し・前後関係の異常の報告なし。「余計な内部面」は、赤面に陰影がなく欠けと区別できないため参考とする | 人間の指摘と確認、2026-09-19。異常の報告はQuest Link実機での観察であり、原因の確認とCap別方式の成立は非XRの画像比較による。その成立範囲は単眼・凸形状・MSAAなしに限り、複雑な形状とXRでの成立を意味しない。各回答の時刻は記録されていない |
| D-183 | Cap仕事単位のStencilへの変更 | D-182を受け、5.2／5.6に次を定める。①処理順は、可視・非空のCap仕事（RenderFragmentとCap境界の組）→同一VolumeのGroup→投影競合によるColor割当て→描画。Volumeは自身の面・Sideだけでclipし、Colorの競合判定には切り詰め前のOBB初期断面を、描画するCapには他のSelected面で切り詰めたPolygonを使う。Color内は初期化→全Volume→全Cap。本体・Depth・Shadowの全Selected面clipは変えない。②Volumeを1回にまとめるのは、同じ登録・同じ境界とSideで、Volumeへ実際に渡すGeometry・配置・平面が同一の場合だけ（epsilonで同一視しない）。近いが同一でない仕事は別Groupとし、投影競合でColorの共有を決める。World Plane一致のepsilonは、Stencilの共有にも判定にも使わない（表示専用Offsetは撤去済みで、その一致判定は存在しない）。③Color上限内に安全に割り当てられなければ、理由を区別してカメラ準備を拒否する。部分upload・部分描画・以前の準備結果の再使用を行わず、失敗した準備でRenderを許可しない。displayの永続停止とは分け、次の準備試行を認める。同期的な追加描画・自動的な上限拡張は追加しない。④描画用に生成・保持済みの初期断面を、競合判定に読み取り専用で使う（用途の追加。描かれない面の断面生成は引き続き行わない）。カメラ準備で作り直さず、断面はそのRenderFragmentの配置のまま読む。⑤Cap仕事・Volume Group・初期断面の投影入力を別の契約とし、RenderFragmentのTargetと`capsComplete`の意味を上書きしない。初期断面の不正・欠落・非有限を非競合としない | TL判断、2026-09-19。置換範囲：D-079の「`MaxStencilColors`へ収まらない対象は最後のColorへ統合する」と「保守的な可視Cap Bounds」による判定、D-080の互換Groupの定義（全World Cut Plane・Side・Offset・Cap描画状態の一致による同一Colorへの加算）、D-081の互換Group単位のCull（Cap仕事単位へ）、D-082の可視Cap Boundsによる競合領域（初期断面へ）、D-127備考の最後の統合Colorでの表示・Depth品質例外、5.2の品質例外8。D-079のConflict Graphに関する記述、D-080の符号保存と`sum(W_i) > 0`の意味、D-081の両眼Facing条件、D-082の実Stencil非監視は維持する。③は誤った蓄積を混ぜないための暫定の拒否であり、上限超過時の通常表示はO-049に残す。初期断面による分離（C-proposed）の確認は単眼・凸形状の自動比較に限り、描画用Capの非交差だけで共有した場合のはみ出し（X5のC-naive）は自動比較だけの証拠で、人間は確認していない。製品実装・製品接続前の確認（5.6）は未実施（2026-09-19時点の記述）。2026-09-20追記：CPU処理（6c27b54）と、製品のカメラ準備・upload・描画への接続（dce1487）を実装済み。製品接続前の確認項目と既存証拠の対応、残る未確認・未解決は5.6の「実装状況」を参照する。2026-09-20追記：③（Color上限だけによるカメラ準備の拒否）はD-185・D-186で置換した。①②④⑤は通常Colorについて維持する |
| D-184 | 描画検証の運用とXR確認系列の終了 | 描画の検証は、自動レンダリング、画像比較、必要な場合のDepth取得を中心とする。人間確認は、保存した画像一覧をまとめて確認する方法を基本とする。Cap仕事方式について2026-09-19～20に行った追加のXR確認系列（XR Simulator、Quest Link）はここで終了する。未確認事項を埋めることだけを理由に、実機・SimulatorのXR試験を自動的に追加しない。XR固有の問題で再確認が必要な場合に限り、目的・得られる情報・所要時間を示し、人間の事前了承を得てから行う。未確認事項は未確認として記録に残すが、それだけを理由に後続の実装を待たせ続けない | 人間判断、2026-09-20。製品の両眼描画要件（4章、5章、T-004、T-089など）を削除する決定ではなく、未確認事項を合格に変更する決定でもない。検証形式の扱いは17章に従い、14章の各試験の方法はこの運用の範囲で行う |
| D-185 | Color上限による拒否の撤回と最後のColorの旧方式 | Color上限だけを理由とするカメラ準備・描画の拒否（D-183③）を撤回する。通常ColorはCap仕事方式を維持し、`MaxStencilColors`の最後のColorでは、通常Colorに入らない残りのCap仕事を旧方式（RenderFragment単位で全Selected面によりclipしたVolume）でまとめて描く。最後のColorに由来する欠け、Stencil混入、余計なCap、重複、誤ったDepth／Occlusionを許容する。余計なCapのDepthが他の物体や通常Colorの結果を隠すことも、この許容に含む | 人間判断、2026-09-20。通常Colorの割当て規則を緩める決定ではない。許容は5.2の品質例外8に限り、D-184の検証方針は変更しない。割当てと発行の具体はD-186、製品の上限値はO-034に残す。実装・確認は未実施（決定時点の記述）。2026-09-20追記：bb9cd38で実装し、local mainへ統合した。確認の範囲は5.6の「実装状況」を参照する |
| D-186 | 最後のColorの割当てと発行 | D-185を具体化し、5.6に次を定める。①`N = MaxStencilColors >= 1`のうち、先頭の最大N−1枠を通常Color、最後の1枠を統合用に予約する。②通常Colorは、厳密一致のStencil Volume Groupと、初期断面による両眼の投影競合判定を維持する。通常Colorに入らないGroupは、その全Cap仕事をまとめて最後のColorへ送り、Groupの一部だけを通常側へ残さない。N=1では可視・非空の全Cap仕事が最後のColorに入る。残りがなければ、最後のColorの初期化・Volume・Capは発行しない。③最後のColorは、Stencilの128初期化 → 残りのCap仕事が属するRenderFragmentを重複排除し、各RenderFragmentの全Selected面でclipしたVolumeを1回ずつ（1回はGeometryの全submeshを描く一式） → 残りの各Cap仕事の切り詰め済み描画用Capを1回ずつ、の順とする。同じRenderFragmentの別のCapが通常Colorに割り当て済みでも、そのCapを最後のColorで再描画しない。④初期化とVolumeはDepthを書かず、Capは現在のDepth規則で描く。Depthの保存・復元は追加しない。⑤本体・Depth・Shadow、Snapshot、Selected／Ignored、断面キャッシュの規則は変えない。Ignored面のclip・Cap・断面生成は復活させない。Color上限以外の容量不足、不正入力、資源・世代・寿命による拒否は緩和しない。上限の自動拡張、同期的な追加描画、多段の救済は追加しない | TL具体化、2026-09-20。旧分類器や、epsilonによる旧互換判定（`CapCompatibilityKey`）の復活を要求しない。数は、通常ColorのVolume Group数、最後のColorで描くRenderFragment数、使用Color数、最後のColorのCap数を区別して扱う。実装・確認は未実施（決定時点の記述）。2026-09-20追記：bb9cd38で実装し、local mainへ統合した。確認の範囲は5.6の「実装状況」を参照する |
| D-187 | Ignored集約の基準配置を先頭の生存枝から取る | 5.2／5.6に次を定める。Ignored境界で集約した一つのRenderFragmentの基準配置を、集約の根（最初のIgnored境界のSource）ではなく、**そのグループの走査順で先頭の生存枝について既存の配置照会が返す基準配置**とする。これはその枝のOwnerのworld変換とGeometry local→Owner localの対応（5.6）から作る値であり、Owner配置をそのまま描画行列にはしない。Geometryの原点とOwnerの原点が異なる場合もこの対応で保つ。**（2026-09-20、表示専用Offsetの撤去により更新）表示用Offsetの合算もGeometry Commitの分離畳み込みも、もはや存在しない。基準配置は先頭の生存枝の照会結果そのものである。**取るのは基準配置だけで、Ignored面をclip制約・描画用Capへ加えない。5.2の「異なる配置を持つ対象はまとめない」が比べるのは表示登録が保持する配置であり、同じ登録内の枝が別々の現在Owner配置にあることは本決定で許容する。別々の登録を新たにまとめる変更ではない。集約は引き続き一度だけ描き、枝ごとに本体を重複表示しない。Ignored面が残る間の、先頭枝以外の物理部分との位置・向き・形状の乖離、その乖離による他物体への表示上のめり込み、Selectedの範囲が進むとき・集約の根やグループや先頭枝が変わるとき・集約が解けるときの瞬間的な跳びと形状変化（完全に解ける前の変化を含む）を許容する。集約の根のOwner不在によって配置照会が答えられず表示が停止する経路は、この接続で解消する対象とする | 人間判断、2026-09-20。数値上限、品質Gate、人間確認は求めない。許容はIgnored集約の基準配置に限り、通常のSelected表示の規則と最後のColorの取扱い（品質例外8、D-185・D-186）は緩めない。先頭枝以外との乖離は相対並進・相対回転・形状の大きさに依存し、上限は示さない。D-181の集約規則（一度だけ描く、描画用clip・Volume・Capは選択済み境界だけから作る、候補・Side・選択状態を論理枝ごとに保持する）と、D-180のIgnored Cap板を作らない決定は変更しない。`RetiredInsideAggregate`（集約の内側の退役による停止）と、生存する追従対象にOwner自体が欠落した場合の扱いは、この決定で変更しない。実装・確認は未実施（決定時点の記述）。2026-09-20追記：f72f7d0で実装し、local mainへ統合した。変えたのは配置照会の対象だけで、集約の根・グループの同一性・Selectedの候補とSideは維持している。確認の範囲（3つの経路の区別と、未実施の項目）は5.6の「実装状況」を参照する |
| D-188 | 切断作業ブロックの初期化省略 | 7.4に次を定める。①分類走査の1ブロック、Arenaの1ブロック、予約時の`meshIds`・`meshBounds`・`meshVertexCounts`の5領域を、初期化せずに確保する。②Jobの報告（`ranToEnd`）とBakeの完了印（`bakeDone`）は、0を「書かれなかった」ことの意味として読むため、確保時の消去を残す。③根拠は読取りと書込みの対応付けとし、初期内容を変える試験は補強証拠として扱う。確認にはReductionの成功経路とR1経路を製品Arenaへ通す場合を含める。④確保オプションを切り替える計測用の分岐・カウンタ・計測ハーネスは製品に置かない | TL指示による実装、2026-09-22。削除したゼロ書込みは1切断あたり箱14,720 B（内訳176／14,480／8／48／8）、Character m_8で164,400 B（4,608／159,472／40／240／40）。**時間の裏付けは、IL2CPP Playerでの「Character・予約区間」に限る**：成功したPlayer build 1本・測定4実行（正順2・反転2、箱とCharacterは同じPlayer）で、warm中央値が4実行とも19〜24 µs短い。**分類区間の差は確認できない**（±1 µs）。**切断Main計測区間合計の改善は未確認**とする（4実行とも新側が小さいが、実行間ばらつきが大きく、予約区間の差を超える分を説明できない）。**箱は符号が一定せず、時間短縮は未確認**。Editorでの同種比較も予約区間だけが両順序で短く、Main合計は確認できない。Arena内の全読取りの独立監査は未完了。実装は未コミット（決定時点の記述） |

## 13. 未決事項

| ID | 論点 | 選択／質問 | 影響 | 決定時期 |
| --- | --- | --- | --- | --- |
| O-001 | 初期ターゲット | 解決済み：PCVRを採用（D-011） | Quest単体は当面スコープ外 | 2026-08-21 |
| O-002 | 目標FPS | 解決済み：両眼描画90fpsを基準（D-012） | 再投影は安全網として扱う | 2026-08-21 |
| O-004 | 断面表現 | 解決済み：初期仕様ではカテゴリ別の断面色・模様・追加記号・内部部品表現を設けず、全対象を同じ固定グレーとする（D-171） | 専用アート、Material、Vertex属性を追加しない | 2026-09-11 |
| O-005 | 切断可能範囲 | 解決済み：人間判断により建物は切断対象、道路は非対象（D-165） | 建物対応をロードマップに含め、道路切断は実装範囲に含めない | 2026-09-10 |
| O-006 | 破片寿命 | 7.9／7.10の各任意Phaseで、Policyの選択・閾値・頻度・予算を実装・実測から調整する | 物理CPU・視覚密度・回収効果。全方式の実装や容量収束・期限を保証しない | 各任意Phaseの実装時。未決を前段Gateにしない |
| O-007 | Collider仮状態 | 旧Collider維持時間と周辺破片の例外判定。Player Body／Handと刀は物理接触せず、刀は論理Sweepのみ | 違和感と実装複雑度 | T-005後 |
| O-008 | NPC構成 | Synty人物をそのまま使う範囲と顔・体型改造量 | 独自性と制作工数 | アート検証時 |
| O-009 | データ保存 | 切断状態をセーブ対象とするか | 再現性・容量・ロード時間 | ゲームループ決定時 |
| O-010 | ネットワーク | 将来的なマルチプレイ要否 | 切断イベント同期設計 | 企画判断 |
| O-016 | Unity CLI再評価 | 実験的CLIとUnity PipelineをCIへ採用するか | 保守性、自動導入、外部依存 | CI構築時 |
| O-017 | Slash調整 | 19.1の実装済みLatch／Frame／Span Candidate／Close方式、Begin選択、Emitter、epsilon／q許容、必要時だけの候補上限、速度・寿命と実装済みVFXの主要な調整値を開発UIで調整する。生存Wave固定容量は0.55の観測から4.50開始前に決める | 応答・操作感・Span形状 | Phase 0.55以降。同じ出力契約内の交換で本書改訂・Phase再開を要求しない |
| O-019 | Edge Gate閾値 | Edge Lead Score、CutSample速度・位置、次の振りの再準備条件、異常速度上限。Phase 0.52は機能確認用の暫定値とし、最終値を固定しない | 復路誤発射、取りこぼし、連続斬り感 | Phase 0.55で実機調整、4.50で製品回帰（T-038～T-041） |
| O-020 | Grip校正 | 右手用ユーザー校正機能の提供要否。Phase 0.5のGateにしない | 刀表示の一致、刃方向判定、導入工数 | Phase 0.55または後続UX判断 |
| O-021 | AI LOD | 介入への応答と計画再利用を両立する負荷制御方式・判定指標・更新頻度 | CPU予算、見た目、予測再利用率 | 研究とT-045の本体実測で決める |
| O-022 | MobPlan有効期間 | 採用方式で扱う計画期間と更新時期 | 切断計算猶予、無効化率、メモリ | 研究とT-044～T-046の本体実測で決める |
| O-024 | Unity更新頻度 | 6000.3.22f1から同一LTSパッチへ更新する条件と回帰基準 | 修正取込み、再インポート時間、安定性 | 更新候補発生時 |
| O-032 | 最終重力と周辺調整 | 0.35G／0.5G／0.7G／1.0Gの採用値と、反発、Drag、分離Impulse、Animation、破片寿命の追加調整要否 | 空中斬り成功率、世界の重量感、テンポ、物理安定性 | T-064のプレイテスト後 |
| O-033 | Shadow近似品質 | 両面・キャップなし近似を許容する距離／時間、Stable専用Shader分離、問題時の簡易Shadow Cap導入条件 | Shadow GPU時間、Draw、接地影、Self Shadow、実装複雑度 | T-065後 |
| O-034 | Stencil Batch予算 | `MaxStencilColors`、OBB／初期断面投影のMargin、Facing epsilonを決める（World Plane一致のepsilonは、D-183以降Stencilの共有と判定に使わない。表示専用Offsetは撤去したので、その一致判定はもとより存在しない）。Count方式は128初期化の正符号8bitへ固定し、相殺・Color超過の救済条件、距離別Cap省略、別Backendは追加しない | CPU分類・Color割当て時間、Stencil GPU時間、Draw、Cap仕事・Volume Group・Color数、最後のColorへ送るCap仕事の頻度、仮断面品質 | T-066～T-068後 |
| O-035 | 実行並列度・Bake共有枠 | 4.3の暫定G8/B2を起点に、同時Work数とBake共有枠を実ゲームで調整する | Main配置条件、Main／描画／Physics／urgent時間、下位処理量・滞留。受付・メモリ容量とは区別する | 各経路の統合・T-069／T-076で調整。最適値未決を着手Gateにしない |
| O-039 | Cut/Cook容量予算 | scratchメモリと`MaxIncompleteCutOperationCount`を実測から調整する | 予約量・ピーク使用量、Pending時間、受付見送り頻度 | T-076後 |
| O-040 | Player壁境界応答 | 解決済み：D-166の固定Occupancyと候補次姿勢Overlap Rejectを採用し、退出系を撤去する | 通行境界と表示／物理の不一致、全体Reject・自動復旧なしを許容 | 2026-09-10 |
| O-044 | Provisional Physics設定 | Actor／ShapeInstance／Constraint容量、Pending警告時間 | 生成／破棄CPU、Broadphase、Solver時間、Ghost Contact、接触Impulse、handoff品質 | T-091後。新しい監視・復旧契約は作らない |
| O-045 | Mob計画方式と予算 | 研究結果を踏まえた方式選定と、必要な処理・メモリ・計画再利用の予算 | 介入応答、計画費用、停止・重なり、再利用状況 | 方式は人間判断で採用し、T-092で本体確認する。先行切断採用状況による調整は条件付き4.72へ遅延する |
| O-047 | 剛体リベース許容値 | RigidCutRebaseProfileV1の法線角度、Bounds内plane field差、左右眼ProxyPoint pixel差の閾値を少数の距離／サイズ／斬撃例で校正する。8点近似を真の切断線最大誤差としない | 投機再利用率、切断縁とVFXのズレ、VR知覚、命中時CPU | T-093／任意Phase 4.55。未校正でも有効な試験Profileを明示し、製品値の未設定を無制限許容にしない |
| O-048 | Building World D6設定 | L1、A1、共通減衰率rと、建物D6を含む既存Constraint容量の製品値 | 拘束挙動、Joint数、生成・退役・Physics Step費用。倒壊防止率・最大変位・性能SLAは追加しない | Phase 4.3／T-094の少数FixtureとProfilerで判断 |
| O-049 | Stencil Color上限超過時の描画スケジュール | 解決済み：方式選定はD-185（人間判断）とD-186（TL具体化）で解決した。Color上限だけによるカメラ準備の拒否（D-183③）を撤回し、通常Colorに入らないCap仕事を最後のColorで旧方式により描き、その誤描画を5.2の品質例外8として許容する。実装・確認は未実施（決定時点の記述）。2026-09-20追記：bb9cd38で実装した。確認は5.6の「実装状況」に挙げた範囲に限り、全面的な検証ではない。方式選定は引き続き解決済みとする | 最後のColorへ送るCap仕事とRenderFragmentの数、通常ColorのVolume Group数、使用Color数、Stencil GPU時間 | 2026-09-20 |

## 14. 技術検証項目

製品Playerの性能合否・実行予算を判断する測定は、3.3のIL2CPP Playerを基準とする。性能測定は必要になる既存Phaseで行い、3.3の初回Player動作確認へ測定や性能目標の達成を前倒ししない。Editor／Monoの測定は開発中の参考値とし、機能確認やKernel単体の比較・退行調査に利用できるが、製品性能の確認を代替しない。Development／非Development、観測と21.17の簡易ログ出力の有無等の実行構成は既存の測定記録で区別し、IL2CPPという名称だけで同条件と扱わない。保存形式は17章の実装詳細とする。本変更だけを理由に完了済みPhaseを再開せず、全試験・過去測定の再実行を要求しない。

CPU性能の測定では実行先・並列度・Main配置条件を区別し、取得できるクロック／CPU performance状態を診断に使ってよい。固定値や意味が不確かなcounterを実効クロックの証明にせず、取得不能を完了Gateにしない。記録方法・項目・周期は実装詳細とし、常設collector・clock固定・専用schemaを要求しない。

4章の共通Player終了出口を最初に接続する既存Phaseでは、終了APIをfakeに置き換えた短いシナリオで、Worker通知だけではlatchが変わらずMain回収時に確定すること、ログ・終了APIより先に受付・Commitが閉じて終了APIを一度だけ呼ぶこと、後続要求でログ・終了処理を重複実行しないことを確認する。専用Test ID・Phase、実Player終了、全エラー直積・競合網羅・診断保存試験は追加しない。

検証形式・試験手順の扱いは17章に従う。描画の検証方法、人間確認の形、XR試験を追加する条件はD-184に従う。下表は確認する能力を定め、過去のCapture／Trace／Fixture形式や専用試験の維持を要求しない。

Phase 5.6／5.7の任意機能固有の確認は7.9.7、任意Phase 7.1は7.10へ集約し、導入時だけ適用する。下表の既存試験は担当契約の範囲で再利用する。既存Phaseの完了条件へ追加機能を前倒しせず、専用Test IDや大規模性能matrixを追加しない。

| ID | 対象 | 合格の考え方 | 方法 |
| --- | --- | --- | --- |
| T-001 | 斬撃検出 | 高速な刀でも切り抜けず、一意な切断面が得られる | 速度別1000回で欠落率と重複率を計測 |
| T-002 | 即時分離 | 必要なベイク・VP変換後に正負が別々にclipされて表示される（4.5.2）。**表示専用Offsetの撤去後は、Provisional公開前に隙間は見えない**ので、隙間の視認を条件にしない | GPUタイムと入力から表示開始までのフレーム・残る準備費用を記録し、代表入力で4.5.2の同フレーム表示目標とフレーム全体の負荷を確認する |
| T-003 | 複数Pending | 2〜4切断で画質と性能が許容範囲 | 切断数別にCPU/GPU、Draw、overdrawを比較 |
| T-004 | Stencil断面 | 正常な正向きの共用Geometryで、5.2の明示的品質例外を除き穴・はみ出し・片眼ずれがない | 箱、凹形、人形の共用Geometryを通常の外部視点で両眼確認する。符号・Color・Plane overflowの扱いはT-066／T-067／T-089に従い、この試験へ重複展開しない。Camera内部／Near Plane近傍は5.2／D-131の品質例外に従う |
| T-005 | Convex切断 | 7.2の上限内の有効な閉凸出力と内接性を確認する | 小さいオフラインFixtureでL境界、超過出力の内接削減、削減後B-repの再切断可能性を確認する。削減不能は既存失敗終端へ接続し、成功例をAbortだけで代替しない。頂点由来・削除順別のmatrixは要求しない |
| T-006 | 共用Geometry切断 | 断面が閉じ、元surfaceのUV／法線／submeshが保持され、同じ結果が表示／Stencilへ使われる。生成Capの固定UV slotと再切断時の継承を維持する | 代表Fixtureを多方向に連続切断。断面専用submeshの存在は合格条件にしない |
| T-007 | 受付制限と対象authority | 4.2／8章のSource単位の受付・失効と4.5.6の祖先順Geometryを確認する | AがActive中の同じSourceへの要求を見送り、保存・再実行しない。他のLogicalFragmentは同じObject内でも並行でき、他枝のObjectGeneration更新でAをRejectしない。AのFinal Physics／Logical公開後はGeometry未完了でも子のBを受付・公開できるが、B KernelはA Geometry Commitを待つ。同じTransactionのProvisional置換・pose／速度／Sleep変化は自己失効せず、外部authority変更はStale回収して現在Sourceを退役させない |
| T-008 | Skinned切断 | Tableで動作するNPCの実Pose Snapshotから同期ベイク・静的破片への移行が成立する | Phase 4.52で限定再生中の各部位を切断する。移動計画・未来評価全体・非同期ベイクの完成を要求しない |
| T-010 | 破片予算 | 代表的な連続プレイでCPU／メモリ予算を満たす。任意GCによる常時の容量収束や無制限の切断寿命は保証しない | 10分間の代表的な連続切断ストレス試験。新GCの未実装・無効・省略自体を不合格にせず、採用構成の費用・使用量を確認する。Phase 7.1固有の完了確認を本試験の新規実施へ依存させない |
| T-011 | XR描画 | Single Pass環境で両眼のclip／Stencilが一致 | Phase 1.50～1.52の基本VP／即時Clip／Stencilの低レベル確認を再利用し、Phase 2で製品状態を含むclip／Stencilの左右眼スクリーンショットと実機確認 |
| T-012 | Collider cooking | 4.3／7.2に従い、通常経路でCook本体や未完了処理の強制待ちをMainへ戻さない | ProfilerでCut/Cook実行、必要なMain操作、待ちを区別する。全スパイクの消滅を合格条件にしない |
| T-013 | 非VR性能基準 | 同一負荷を自動再生し、変更前後を比較可能 | 固定カメラ、固定乱数、切断スクリプトで計測 |
| T-014 | Quest Link XR | Quest 3S有線Quest Linkの90HzモードとSingle Passで、単純Geometryと右手用の暫定固定GripToKatanaOffsetを適用した刀が両眼表示され、右手Controllerへ追従する | Phase 0.5。HMD内目視とProfilerで基本表示・追従と一度の追跡喪失／復帰を確認し、無効Poseを利用しない。固定測定時間、P95／P99、製品90fps SLA、任意校正UI、Slash生成は要求しない |
| T-015 | 斬撃波先行切断 | 接触前の完了率が即時レンダラ負荷を有意に減らす | Phase 4.53。距離、速度、対象数別に事前完了率とPending時間を測定 |
| T-016 | 未来評価器統合 | DAGでReadyになった投機Workの投入、取消・採否、実Hit時Commitが競合なく成立する | Phase 4.53で遅延・進路変更・再切断による取消・不採用と有効成果物の再利用を確認する。命中後の継続・未投入Workの実行先はT-090／4.4へ従い、Deadline順を合格条件にしない |
| T-017 | 自由飛行剛体の直接予測 | 19.3の直接予測が本体状態と統合され、対象外・前提不一致は現在状態経路へ進む | Phase 4.54で開始Snapshot、重心／Actor原点、回転、WorldPhysicsProfile、FixedStep境界、予測Horizon、Gate対象外を確認する。点Anchor、接触／転動、既知Constraint付き対象を直接予測へ入れない。面リベースはT-093で確認し、外部Probeの測定値を製品保証にしない |
| T-018 | Pose Table評価 | 19.3の同じ有効入力からCurrent／Future共通のPoseを要求順に依存せず生成する | Phase 4.61で任意時刻評価・追加時刻進行なし、Loop／Clamp・終端・明示Clip切替、入力不一致拒否、D-136のscopeを少数例で確認する。採用Rig／ClipのBake・サンプル間補間品質と現在骨への適用を確認し、費用・容量は21.2に従う。計画統合は4.70、切断成果物Commitは条件付き4.72へ残す |
| T-019 | Trace相関と完全性 | 21.3／21.4の因果関係と欠落の扱いを満たす | 代表的な処理の公開・完了・破棄を追跡し、不完全な記録を完全な再現根拠にしないことを確認する。保存形式や旧Readerを固定しない |
| T-020 | Trace負荷 | Trace観測処理がGameplayを待たせず、競合や性能判断を歪めない | 代表負荷でTrace観測有無の費用・メモリ・記録欠落を確認する。21.17の簡易ロガーに非待機・無割当・無影響を要求する試験にはしない |
| T-026 | 公開Repo分離 | 公開履歴と成果物にSynty入力・派生Assetが混入せず、原本が非公開Git LFS Repoだけに存在する | ignore、CI検査、履歴スキャン、LFS追跡状態、private remoteのアクセス権を確認 |
| T-032 | Unity版固定 | PATHやHub既定版に関係なく6000.3.22f1だけで開き、誤版起動を拒否できる | ProjectVersion、明示exe、batchmode、Package Lockと別版併存を検査 |
| T-033 | Repository衛生 | 公開Repoに生成Cache、ユーザー実名パス、Synty Assetが混入しない | ignore、機密パターン、絶対パス、履歴をCIで検査 |
| T-034 | Slash UX実機探索 | 完成済みSandboxを使い、利用可能なLatch／Frame／Span／Close構成と暫定Presetを得る | Phase 0.55。Quest Linkで第一候補から試し、人間が試すと決めた候補を必要に応じて追加し、19.1.12の主要値表示・Pose再生・Current／Pinned比較で実装済みVFXを含めて調整する。第一候補のLive／Frozen Guide、Raw／Accepted Span、Invalid保持、Close後勾配と連続斬りの同時生存Wave数を確認する。0.54までの機能確認を本試験の合格とはせず、全候補行列・最終値・ログ互換を要求しない |
| T-035 | SlashWave Core | 発射時の面・軸・初期形状と評価設定の不変性、AcceptedSpan非減少、有限寿命を維持する | Phase 4.50。VFXはPhase 0.53の19.1.8確認を再利用する。減少・Invalid保持、完了済み区間の非再評価と現在区間の増加領域、採用方式のClose後評価、Expire、複数Wave、UI切替後の生存Wave不変とSpan／Close一時状態の寿命を確認する。小さい固定容量で、容量まで公開→満杯時は新Latchだけ見送り→既存Wave不変→既存WaveのExpireで容量返却→見送ったStrokeは遅延Latchされず、再準備後の新Strokeは返却と同じ更新でも通常Latch、を一連で確認する。ExpireしたWaveはその更新のSegment／Sweepを出力しない |
| T-036 | Segment Hit | 現在採用Convexとの共通閉Sweepと系譜消費が成立する | Phase 4.51。生成時の退化入力、4端点の閉凸包（通常は平行四辺形／台形）、増加領域Hit、Bounds／VFX非authority、厚み・端点領域なし、無関係Fragment個別Hit、子孫再Hit禁止と受付見送り後の非再試行を確認する。軸が平行または反平行で線分へ退化する代表1件を同じ現在採用Convexへの共通Queryで扱い、軸補正・厚み・端点領域・別Hitアルゴリズムを追加しない |
| T-037 | 投機候補範囲 | 範囲外でも現在状態切断が成立する | Phase 4.53。有限包絡の保守Boundsと包絡を保証しない先行準備範囲を区別し、範囲外実Hitを4.51へ接続する。Clamp・範囲拡張・再探索を追加しない |
| T-038 | Edge Direction Gate | 刃側の広い振り角を許容し、峰側移動はSlashを生成しない | Phase 0.52は少数固定Pose列、0.55で実機調整、4.50で製品回帰。Score閾値、速度、移動量、Sample Window別に往路・復路・斜め振りTraceを再生 |
| T-039 | 連続斬り | 復路で誤Slashを生成せず、返した刀の次の有効斬りを受理する | Phase 0.52は少数固定Pose列、0.55で実機調整、4.50で製品回帰。代表的な抜刀・復路・返し・右手の刀による左右方向の連続斬りで誤発射・欠落・再準備を確認する。各1000回の固定行列は要求しない |
| T-040 | 刀の非接触と新Wave受付 | 発射条件・再準備条件の不成立時に刀自身から新Wave・物理応答・Gameplay Hit・刀由来Hapticsを生成せず、生存Waveはその後のGate状態から独立に継続する | Phase 4.50は新Waveを生成・公開しないことと刀Colliderによる物理応答がないことを確認し、実対象Hit・Hit Detector・対象Queryを要求しない。Phase 4.51は発射条件・再準備条件の不成立中も生存WaveのSweep／Hit評価が継続し、刀自身から対象Query・Hit・Hapticsを生成しないことを確認する。生存Waveの実Hit通知・Hapticsは禁止対象に含めない |
| T-041 | Tracking復帰 | 追跡喪失と再取得で巨大速度や誤Slashを生成しない | Phase 0.51／0.52は履歴Resetと受付を少数固定Pose列で確認し、0.55で実機調整、4.50で製品回帰。Controller遮蔽、Pose無効化、位置飛びを記録・再生しSample Resetを確認 |
| T-043 | Unity更新再現性 | Project再作成や版別コピーなしで新Editorへ更新でき、旧版へGitで復帰できる | 専用ブランチと一時worktreeでProjectVersion、Package Lock、固定テスト、XRスモークを検査 |
| T-044 | MobPlan参照の一貫性 | 公開済みの同じ計画を同じ時刻で参照した結果が一致し、Current／Futureが整合する | 20.2のRootとPose評価入力を確認する。同じSeedからの計画再生成・経路hash一致は要求しない |
| T-045 | AI LOD予算 | 採用した負荷制御で有限な計画費用・メモリと介入への応答を両立する | 4.70で20.4の能力を既存Profiler／Traceで確認する。固定四Tierや全組合せmatrixを要求しない |
| T-046 | MobPlan無効化 | 前提変更・切断・退役後に旧計画・Pose・依存成果物を適用しない | 20.3／20.6の失効と使用中入力の保持・回収を確認する。固定Group方式やVersion型ごとの独立試験は要求しない |
| T-048 | モブ先行切断 | 有効計画から必要候補をPose評価して先行成果物を利用する | 4.65採用時の4.72で実切断統合と先行完成・採用状況を確認する。不採用時は要求しない |
| T-049 | Mob Trace因果相関 | 計画・Work・Slashと採否の因果を既存IDで追える | MobId／PlanGeneration／SlashId／TaskIdで確認する。計画単体は4.70、未来VP準備は条件付き4.71、切断Commitは条件付き4.72で扱い、後段不採用を計画Traceの未完了にしない |
| T-050 | 断面表示一貫性 | 通常表示で仮断面と実断面が同じグレー・同じトゥーン応答となり、差し替えで陰影や輪郭が目立って変化しない | 共通トゥーン設定下で箱、凹形、人物を多方向に切断し、両眼映像とフレーム差分を確認する。既存FixtureでBase Texture／Material UV Transformを変更しても実断面色が変わらないことを確認する |
| T-051 | 断面デバッグ表示 | 5.3の通常グレー／仮断面赤／実断面緑が描画対象に対応し、デバッグ切替でGeometryを書き換えない | Final／Logical公開前、公開後Geometry未Commit、Geometry Commit後を一連で確認し、再切断では既存実Capの緑と新しい仮Capの赤を維持する。色変更だけを目的とするVertex／Index書換え・VB／IB転送・Geometry複製がないことを確認する。処理経路別の色行列、色覚補助、専用パネル、独立性能SLAを要求しない |
| T-054 | Unity選択的録画 | Frameと画像が対応し、21.15の非待機・容量・寿命・故障分離を満たす | Phase 0.11では21.15のCapture経路での複数Frame処理・出力確定・decode対応と資源寿命を確認する。旧固定fps・Frame数・試験階層を要求しない |
| T-058 | Pending物理共有 | 4.5.2の入力準備条件に従って切断表示を開始し、cook遅延中の共有Colliderによるめり込みと透明接触が許容範囲に収まる | Bake遅延を0～数秒へ変え、表示開始フレーム、分離量、接触差、Timeout品質低下を測定する。処理中は同一Sourceへの再切断を拒否し、Final／Logical公開成功後は子への切断を受付可能とし、Final不成立時は7.1に従ってSourceを退役することを確認する |
| T-059 | 物理分裂Commit | 7.1／7.2の初回分裂とFinal handoffのpose／速度・一体公開を確認する | 並進・回転・接触中の代表例で、Anchor点速度継承とFinal Actorのpose／速度維持、主スレッド時間、Impulse、視覚差を記録する |
| T-064 | 全体低重力プレイ | 一般プレイヤーが空中物体を狙いやすく、世界全体の浮遊感とゲームテンポが許容でき、全軌道系で重力が一致する | 0.35G／0.5G／0.7G／1.0Gを同一投擲・切断Scenarioで比較し、滞空時間、斬撃成功率、主観評価、Physics／予測／VFXの軌道差を記録 |
| T-065 | 即時切断Shadow | Stencil Capなしの両面Shadowが即時状態で許容でき、clipと配置がカラー像と一致し、片面／両面群分割が90fps予算を阻害しない | 箱、薄板、凹形、非閉形状を床／壁近傍で切り、単一Directionalの各Cascade、Bias条件について実Capとの差分、漏れ、peter-panning、Shadow Draw、GPU時間を比較 |
| T-066 | Stencil Color割当て | 通常Color（先頭の最大N−1枠）では、左右眼いずれかで初期断面の投影が重なる異なるVolume Groupを分離する。通常Colorに入らないGroupは全Cap仕事をまとめて最後のColorで旧方式により描き、残りがなければ最後のColorを発行しない。実行Color数は`MaxStencilColors`以下に保ち、Color上限だけでカメラ準備を拒否しない（D-185、D-186）。各Colorで全Volume後に全Capを描く | 左右眼だけで初期断面が重なる配置、OBBは重なるが初期断面は非交差の配置、描画用Capは離れるが初期断面は重なる配置、全重複、非重複、小さいColor上限（N=1とN=2、最後のColorへの送り、残りがない場合の非発行）を確認し、CPU分類、Cap仕事・通常ColorのVolume Group・最後のColorのRenderFragmentとCap・使用Colorの各数、Clear／Volume／Cap GPU時間、Drawを測定する。全Graph／全Edge、特定のColor番号、方式間で同じ彩色結果を要求しない |
| T-067 | 正符号Stencil／Stencil Volume Group | 128初期化とWrap加減算から得る`S>128`が範囲内の`W>0`と一致し、共通契約を満たすGeometryを符号保存のまま共有できる。通常Colorでは異なるVolume GroupのResidual Supportの分離条件を守り、同一のVolume入力を持つCap仕事だけがVolumeを共有する（D-183）。最後のColorではRenderFragment単位の全Selected面clipのVolumeを使い、そこでの混入・欠落は5.2の品質例外8とする（D-186）。描画結果には5.2の明示的品質例外を適用する | Phase 1.52の低レベル確認を再利用し、Phase 2で製品状態・Stencil Volume Group（D-183。旧方式の互換Group〔D-080〕ではない）との統合を確認する。正向き箱、凹形、同方向重複と、生のCount `-1／0／+1／+2`が`127／128／129／130`になる小さいFixtureで`Ref 128 / Comp Less`、Read／Write Mask、IncrementWrap／DecrementWrap、Color／Depth writeを確認する。全体反転閉Mesh、正逆重複、別TopologyのCoincident／Nested／Self-intersection、負determinant Transform、World Plane差、左右眼を試し、向き正規化や二重Transform補正がないことを確認する。範囲外Winding、入力Gateの不合格行列、削除済みの符号証明、向き正規化、Winding上界、Count容量分割、符号別Groupをこの描画試験へ追加しない。8bit排他不能構成はゲーム開始を拒否する |
| T-068 | 両眼Cap可視性Cull | Facingでは両眼ともepsilonを越えて明確に裏向きのCapだけを除外し、片眼可視・epsilon帯内・正負Capを誤って除外せずStencil仕事を削減する | 左右眼のFacing一致／不一致、epsilon境界の内外、正負Cap、Frustum内外の固定配置でCull判定、Stencil Draw／GPU時間、左右眼画像差を比較する |
| T-069 | Owner単位Cut/Cook | 7.2の実行分担と一体Commitを確認する | 受付済みPhysicsの新規投入をurgent Unity Jobで実行し、Burst kernelからMeshDataへ出力する。Main ThreadのMesh適用後にmanaged Jobで`Physics.BakeMesh`を行う。同一Meshの同時Bakeがなく、stale非適用、all-or-none Commit、cook不成立時のAbortを少数Fixtureで確認する。cook後の品質差の扱いは7.3に従う |
| T-072 | 固定物体の即時切断 | cook遅延中も7.1の配分でAnchorを持つ所有者全体が固定される。固定を理由に仮描画を省略しない。**表示専用Offsetは無いので、「Anchorなし側だけが仮分離する」ことは表示の条件にしない**（物理が離れた結果としてのみ見える） | 単一・両側・OnPlane Anchor、同Sideの離れた島、連続切断、先行結果Rejectを少数例で確認する。cook遅延caseでは固定・仮分離と固定側の誤Impulse・変位がないことを確認し、全体固定による浮遊とAnchor喪失後の大型物体の落下・回転を許容する。cook失敗caseではFinalを部分公開せず、7.1に従ってSourceを退役することを確認する |
| T-074 | 点Anchorと論理切断公開 | 7.1のOwnerの点Anchor配分とFinal Physics／Logical Publication、後着Boundaryを確認する | Phase 1のHarness内合成Final成功／失敗入力で正負2子の一体公開またはSource退役を確認する。7.1.2の生成元照会は根・正負子・再切断子孫、不正ID、公開前の非可視性とOperation終端後の関係維持を既存試験で確認し、既存照会経路からの履歴走査除去は実装確認とする。Sourceの現在集合だけからの正負／OnPlane両側継承、非identityなlocal frame、子再切断時のSibling不変・Anchor非復活を含む。Phase 3でGeometry Commit時のBoundary 0／後着、Operation／ChildからのTrace相関を確認し、実物理はPhase 4へ接続する。8章に従って他枝の世代更新とauthority喪失を区別する。欠落・重複・件数不一致・IncompleteOperationTraceを完全Traceの合格根拠にしない |
| T-076 | Cut/Cook Profiling | 7.5の代表Fixtureで製品経路の費用を確認する | Owner当たりkernel時間、Cut/Cook全体時間、処理量、Bake数、Final Commit時間、scratch予約・使用量、失敗・stale・Abort数を確認し、O-035／O-039の調整に使う。保存形式と反復方法はHarness実装詳細とする |
| T-083 | 共用Geometry切断 | 共通契約を満たす入力を任意平面で切り、実Capを含む各非空出力が同じ閉鎖・edge／vertex manifold・局所winding整合を継承し、4.5.6の正負直接配置と転送・公開条件を満たしてから、表示とStencilへ同じ世代・Triangle集合としてCommitされる | 箱、凹形、複数の閉Component、全体反転、skinning後Self-intersection、別TopologyのCoincident／Nested Componentを切る。planeがvertex／edge／faceを通る場合、同一点複数port、極小／面積0 Triangleを含め、元surfaceと逆向きのCap boundary、Cap内部Edgeの2 incidence、単一vertex fan、canonical position、finite属性、再切断後の同契約を小さいFixtureのオフラインHarnessで検査する。面積0 Triangleを含む非空GeometryとTriangle数0の空出力を区別し、後者へdummy Mesh／Cap／Rendererを作らない。合成Final成功で2子を先に公開し、Geometryが片側または両側空でも子数を変えずRendererなしとする。Boundary 0／後着と祖先順Commitを確認する。用途別の二度目の切断／Cap生成／Uploadと製品Runtime出力Validatorがなく、世代不一致の通常不採用と出力予約不足時の非公開を確認する。出力予約不足だけは4.5.3の再予約・再実行を許容し、プール容量限界は4.5.4に従う。全Mesh self-intersection／inside-outside検査、旧救済経路、方式別試験を追加しない |
| T-084 | 共用Geometry入力Gate | 正常な閉Meshと全体反転を受理し、Boundary Edge、局所winding不整合、3面以上Edge、複数fan共有Vertexを切断可能Geometryとして登録しない。属性seamと別Topologyの同位置Componentを混同しない | 正向き箱、全体反転、複数の閉Component、Self-intersection、別TopologyのCoincident／Nested Component、UV／Normal seamを受理する。開放Boundary、1／3／4面Edge、T-junction、局所反転、複数fan共有Vertex、共有position不一致、NaN／Inf、不正index／Topology参照を入力準備時にRejectする。Runtime生成の面積0 TriangleをGeometry全体の空と誤判定せず、片側空No-opはこの入力Gateではなく7.6の現在Convex分類で判定する。全Mesh自己交差、inside／outside、signed volume、向き正規化を実行せず、同じ不合格行列をStencil描画試験へ重複させない |
| T-085 | Convex質量特性と質量保存 | 7.2の親質量保存とFinal質量特性を確認する | 少数の単一・重複Compound・連続切断で、正負子のfiniteな正質量、親質量保存、finiteでSolverへ設定可能な重心・慣性、Provisional mass非流用を確認する。加算順・配分系譜・固定近似方式のGoldenを要求しない |
| T-086 | robust supportと正負二集合 | 7.6の一つのepsilonによる受付・未切断継承と2所有者を確認する | 接線・Near-plane・等号で片側supportなしはNo-op、両robust supportがあるConvexはd = 0で分割する。非交差Compound全体が両supportを持てば受付け、全頂点Near-planeの別Convexは正側へ未切断継承する。U字・離れた島も同Sideの一所有者にまとめ、両Physics非空でGeometry片側／両側空ならRendererなしの子を残す。受付後の片側物理不成立はAbortであり、反対側所有者へのGeometry付属で救済しない。Convex外Anchorの代表例で、片側No-opではAnchor不変、別Convexへの実Hitと両supportで受付成立した場合は非交差Convexの継承先によらず点のSideへ配分されることを確認する。非UnionのStencil符号加算とSibling衝突抑止は維持する |

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
| T-089 | 即時Clip Plane上限 | D3D11／Quest Linkの左右眼とColor／Depth／ShadowCaster／Stencil Volumeで5.2の同一選択結果を使い、最大8面のRaster clippingと既存Plane overflow処理が成立する | Phase 1.51の固定Clip確認と1.52のStencil確認を再利用し、Phase 2で少数Fixtureへ製品状態・候補選択を統合する。未使用・容量内・上限8面を採用MSAA構成で確認し、切断面評価用Pixel経路を持たない。受付順・未Commit祖先優先、Operation公開時の重複なしの引継ぎ、既存契機による再選択を確認する。同じ枝に9面以上の未Commit境界がある例で、先頭最大8面を選択し、残りをIgnoredとする。祖先半空間／Sibling分離、対応Stencil Volume非submit、論理・背景処理の継続、Geometry CommitによるTemporary回収を確認する。Ignored境界からclip・対応Stencil Volume・描画用Cap板を生成しないこと、Ignored境界で分かれた論理枝を同じ表示登録内で最初のIgnored境界より手前の形状として一度だけ描きSiblingを重ねないこと、Selectedの祖先境界の開口Capを維持すること、選択結果の変化時に同じ描画更新境界で再構築することを確認する。具体的なFixture列・反復数は実装詳細とし、採用経路の品質・費用は既存Profiler／Harnessで確認する |
| T-090 | 共有Dispatchと切断受付 | 4.3／4.4の実行先、有限容量・Main予算、urgent投入余地、取消・採否・寿命を確認する | Phase 1の合成Workで用途別投入と設定優先度、上位Readyがあっても独立した外部Workを投入できることを確認する。命中前の投入済みWorkは継続し、未投入・後続Physicsはurgent、GeometryはGへ入り、重複実行しない。代表的なA→B→Cで同一フレームの後続投入、予算非再充填、未完了時の非待機を確認する。Queue満杯でも受付済みWorkを失わずDAGに保持し、条件成立後に投入する。取消・失効・後着で二重適用・早期解放がなく、通常停止でWork／Writer利用終了後に回収する。既存Lease・世代試験を再利用する。全対象合計の`MaxIncompleteCutOperationCount`直前／一致、同一Frameの複数対象、同一Slashの部分受付を試し、見送りが状態・世代・新規仕事を変えず再実行されないこと、件数が受付時に増え、7.7のGeometry Commitまたは終端時に一度だけ減ることを確認する。Final／Logical公開や内部CPU完了だけでは減らない。OSの厳密な開始順・固定遅延、内部Queue型・固定Counter・無割当を合格条件にしない |
| T-091 | PhysicsSplitTransactionとProvisional | 7.1／7.2のall-or-none構築、一体Publication、Abort、退役、Lease寿命を確認する | 少数の単一／Compound・Anchorあり／なし入力で、Provisional再cook 0、旧Geometry共有、Siblingのみ衝突抑止、外界Collision、Provisionalでの親質量保存、初回速度継承、Actor優先Final handoffと自前B-repの包含・frame条件を確認する。Final先着では直接Final、Provisional構築不能・片側Final不成立では部分公開せずSource退役となる。Geometry未完成で正負2子を公開し、PendingのPhysics責務が終了することを確認する。7.1のD6設定と内向き境界、代表的な内向き抑止・外向き上限挙動を確認し、正常Limit到達は異常にしない。既存境界からの継続不能通知はActive中ならAbort、成功後なら該当LogicalFragmentだけの退役へ送る。Timeoutのみでは退役しない。Solverの数値SLA、異常監視の網羅、方式比較・Pool必須化は要求しない |
| T-092 | Mob計画の本体導入 | Phase 4.70で採用方式による計画生成・現在適用・未来参照・更新を20章へ接続する | T-044～T-046／T-049を再利用し、失効・非公開結果の回収・資源保護・既存予算との接続を確認する。境界例は採用方式に応じて選び、固定計画だけで製品計画機能を完了扱いしない。条件付きの未来VP準備と実切断統合は4.71／4.72へ残す |
| T-093 | 剛体切断Local Planeリベース | 実姿勢を維持し、面採否と切断受付判定をGeometry Work完成から独立させ、受付済みの全表示・物理処理が同じ操作面へ収束する | 任意Phase 4.55。未実装・延期・不採用を基本ゲームの不合格にしない。19.5.1の対象、接線／法線並進、回転、重心と原点不一致、左右眼差、閾値直前／一致／超過、Near Plane／非finite／Profile欠落、Scope外を小さい固定Fixtureで確認する。同じDescriptor／実SnapshotでMesh完成済み・遅延中・Work失敗を切り替えても採用面と7.6の片側空判定が同一で、Ready前にNo-opならCutOperationId／Pending Cut／操作／世代／表示／物理／仕事を作らず、受付済みならCutOperationIdとPending CutがLogicalCutOperationより先に公開され、Operation未公開のTemporary表示と後のOperation／Provisionalが同じ採用面を使うことを確認する。Operation公開時はPending Cut由来の描画位置と面を維持して重複Recordを作らず、同面Work継続／後追い、後着不採用Work回収、世代更新前Snapshotと受付後世代の対応を検査する。処理中は同一Sourceへの再切断を拒否し、Final／Logical公開成功後は子への切断を受付可能とし、Final不成立時は7.1に従ってSourceを退役する。FinalまでActorが動いても巻戻しなし、面変更なし、自前B-repの由来Convex包含、実速度継承、Impulse一回をT-091の代表caseで確認する。SourceSlashPlane／有限Sweep不変、別対象の面差とCap Group分離、LogicalCutOperation作成Traceに先行できるPending Cut面の記録・復元と欠落／重複／混在拒否、後着Operation Traceとの相関を検査する。少数の動画像で近似誤差とVFX差を目視し、既存Captureを使用する。全距離・視野角・閾値の直積、全Contour最大誤差証明、新しい長時間性能SLAを要求しない |
| T-094 | Building World D6 lifecycle | 7.2.2の予定Depthと正式公開、D6構成・寿命を確認する | 少数Syntheticで建物true／非建物false・初期Depth 0、建物だけの予定Depth更新、Provisionalと正式子への同値継承、直接Final、Abort時非公開、Anchorあり／なし、有限・非負なLimit、生成時相対位置・回転0とFree／Limited設定を確認する。Final handoffで再加算・D6再生成・基準リセットせず、所有Actorと同じ寿命で一度だけ回収する。整数上限・underflow境界、Solverの変位・jitter等を合格閾値にしない。7.9の任意処理は各後続Phaseで確認する |

未来用非同期ベイクの確認は少数のSkinned Fixtureと既存の共用Geometry検証を使う。Phase 4.65は固定Rig Poseの限定実装で、4.5.2の条件を揃えたSkinnedMeshRenderer.BakeMeshとの必要属性・頂点／Topology対応、非同期回収、21.2の負荷比較を行い、導入の採否を決定する。対応入力の少数比較にRendererとRoot Boneのframeが異なる例および非単位scaleを含め、入力をWorldへ写した形状も比較してscaleの二重適用・適用漏れがないことを確認する。効果がなければ不採用で完了し、本体DAG／VPプール接続、MobPlan、実命中、Ragdoll、人形切断の完成を要求しない。Phase 4.52の基本切断はT-008で確認し、T-018のPose／前提検証とT-092の計画単体確認を後段統合待ちにしない。

非同期ベイクを採用した場合のPhase 4.72では、T-008／T-018／T-092の人形先行切断への接続として、準備済み結果の採用、未完成・Pose不一致時の現在Pose同期経路、世代失効後の後着結果回収を少数ケースで確認する。既存Geometry／Physics Commitへ接続し、4.5.6の直接Index出力・独立転送・現在frameでの公開条件と、実Actor／現在Sceneを予測へ巻き戻さない規則を維持する。21.2の同時候補負荷と先行完成・採用状況を測定する。新しい試験ID、専用Framework、全Asset・全変形機能の網羅試験は要求しない。

T-093の再切断・世代照合は8章に従う。子孫切断の受付だけで祖先GeometryをRejectせず、外部authority喪失と区別する。Operation公開前の受付見送りと非再生はT-007で確認する。

T-005／T-085／T-086の数値部分はPhase 3.9で確認する。削減不能等は数値結果として呼出側へ返す。実Actor／cook、Abort、Provisional、公開・退役との接続、およびT-059／T-069／T-074／T-091の実物理部分はPhase 4で確認し、数値部分だけで統合試験を完了扱いにしない。Probe由来のFixture・生成器・Verifierは必要なものを選抜・改変でき、検証方法とケース集合は17章の実装詳細とする。Verifierを製品Runtimeへ接続しない。

T-005の削減成功例をPhase 3.9の再切断とPhase 4のT-091 cook／handoffへ接続し、採用した削減済みB-repが次回入力となり、既存の自前B-rep包含・frame・親質量保存を満たすことを確認する。縮小由来の接触消失・運動変化は7.2の品質許容とし、Actorの巻戻しや不正形状の公開を成功扱いしない。削減専用の試験ID・Fixture体系・cook結果抽出基盤は追加しない。

T-007／T-083でA Geometry未完了のままBを受付・公開し、A Commitで現在B子孫へGA＋Temporary Bを適用してTemporary Aだけを回収する。B Kernel・CommitはA Commit後とし、同じ更新内でもよい。片枝の退役は生存SiblingのCommitを妨げず、全読者退役なら履歴完成だけの計算を続けず既存IncompleteOperationTraceとする。描画経路固有DescriptorはRendererが構築し、同じ描画Snapshotの全PassでCommitted GeometryとTemporary集合が一致する（一致するのは参照する集合であり、実際に適用するclip面は、本体・Depth・Shadowが全Selected面、Stencil Volumeが通常Colorでは自身のCap面だけ、最後のColorでは全Selected面。5.6、D-183、D-186）。

T-074のOwner点集合の確認をT-091のProvisionalへ接続し、直接Finalを含め7.1の配分が一貫することを確認する。旧Cooked Geometry共有・Final Shape交換・再cookで再配分されず、既存recenterの写像で同じ点を表すことを少数の既存Fixtureで確認する。新しい試験体系は追加しない。

T-091の公開タイミングは、7.1.1の条件が揃った代表ケースを製品の受付・公開経路に通し、FinalのConvex切断・cookと表示Geometry切断が未完了でも受付とProvisional公開の描画フレーム番号が一致することを確認する。試験だけで公開APIを直接呼び、製品の呼出し経路を未接続のまま本確認を完了扱いにしない。

T-091のLease確認は、各旧Cooked GeometryをProvisional Shapeへ結び付ける前の取得、部分構築時の非公開、保護参照の解消と必要なPhysics Step後の一度だけの返却、最後の参照・Lease前のGeometry破棄禁止に絞る。少数の失敗・交換・Staleでuse-after-free、二重返却、leakがないことを確認し、内部破棄順は固定しない。cleanupの遅延でTransactionを延命せず、TimeoutだけではLeaseを返さない。

## 15. 実装ロードマップ

Phase IDは文字列とし、0.5と0.50、1.5と1.50、4.50と旧4.5を同一視せず、一括改番しない。Slash UX系列は0.5→0.51→0.52→0.53→0.54→0.55、表示系列は0.9→0.91→0.92→0.93→0.94→1→1.50→1.51→1.52→2→3→4→4.1→4.3とする。両系列は0.5後に並行可能で、1.50は0.5と0.94／1を前提とし、0.51～0.55を待たない。0.55と4.3の双方から4.50→4.51→4.52へ合流し、基本Playableを成立させる。4.52以降は4.53→4.54→任意4.55と、4.52→4.61→4.65→4.70の分岐とする。4.71は4.65採用時だけ未来VP準備を既存DAG／VPプールへ接続し、4.72は4.52＋4.53＋4.71を統合する。4.55は基本ゲームと4.61以降の必須依存にせず、4.65不採用なら4.71／4.72を省略して4.70からPhase 6へ進み、採用時は4.71／4.72を経てPhase 6へ進む。いずれも未完了負債にしない。

Phase 0.21は10.2.3の受入れ責務を定め、最初に必要とするPhaseが、その用途に必要な登録・内容識別・所在解決・Harness接続を具体化する。初回利用元と接続先は実施記録へ残し、特定のPhase番号や全Consumer共通API・汎用Runnerを先に固定しない。独立した先行作業でも利用Phaseとの並行実装でもよく、未実装Consumerの前倒しを要求しない。個別データの持込みや独立した他Phaseの進行を0.21完了待ちにせず、10.2.3に従う既存Harnessへの接続を先行できる。導入後の入力更新・参考実行と、後続用途に必要な拡張・変更は通常作業とし、同じ受入れ責務の範囲では0.21を再開しない。

Phase 2.9は6章の共用Geometry数値Kernel、Phase 3.9は7.2のPhysics Convex数値Kernelを先行実装する独立分岐とする。2.9はPhase 0.x～2、3.9はPhase 0.x～3の完了を着手・merge条件にせず、専用branchで開発し、ゲーム経路に未接続でもmainへ早期mergeできる。Phase 3はPhase 2までの基盤とその時点の2.9のKernelを表示／Stencilへ接続し、Phase 4はPhase 1～3と3.9のKernelを実物理へ接続する。先行mergeはAPI・layoutの凍結ではなく、残る意味契約と現行利用箇所・試験を維持して破壊的変更・置換・削除できる。旧API維持・互換層・migrationを要求しない。前段の基盤・合成入力をKernel利用待ちにせず、Phase 1～3のHarness内合成Final Physics入力も維持する。

Phase 2.9の初期移植元は `zantetsuken-mesh-cut-probe` の `FINAL_REPORT.md`（2026-09-09）が選定したBurstのscan＋edge hash＋分割capとし、seam対応と本隊のAoS／追記出力／6章契約への適合を行う。これは初期実装選択であり方式の恒久固定ではない。Probe全体、不採用Backend、全測定の再現、Probe側の描画・転送設計の採用はmerge条件にしない。移植元の未対応を本隊入力の除外条件へせず、6章との差を実コードと選抜ケースで閉じる。

3.9の初期移植元は独立Probe `zantetsuken-convex-cut-cook-probe` の `REPORT.md`（2026-09-13追補）のA-Walk、Burst R0→R1、double質量計算とする。これは初期実装選択であり方式の恒久固定ではない。Probe全体、比較Backend、旧managed prototype、全測定の再現はmerge条件にせず、公開SyntheticとLicensed入力の既存分離を維持する。

今回追加・細分化するPhaseは既存実装を段階的に完成させる境界とし、後続機能の仮実装やPhase専用のRuntime状態・Coordinator・Scene・Assembly・Logger・Schema・Receipt／Proof・引渡しartifactを追加しない。0.5～0.55は一つのSandbox Sceneを継続使用し、0.5時点の空Sceneを恒久保存しない。0.53のCoreは4.50へ、1.51／1.52のShader・PassはPhase 2へ接続する。観測は既存Trace／Profiler、画面・Consoleと19.1.12の開発情報を使い、任意診断の記録は21.17へ統一する。調整値は暫定とし、操作値は0.55、製品Wave容量は4.50前、Stencil予算はPhase 2以降の既存Open Itemで判断する。

1.50～1.52は、既存またはテスト内で手続き生成した少数の固定合成Fixtureで能力を確認する。固定Descriptor／Color割当てを新しい製品modeとして残さず、Dataset・Generator Framework・別描画Backendを追加しない。不成立でも事実と再現条件を本書へ記録すればProbeは閉じられるが、依存する後続PhaseとPhase 2へは進まない。代替経路・部分Bit・Stencilなし継続・同期Fallback・Recoveryは自動追加せず、別の人間判断へ戻す。T-011／T-067／T-089は共通の低レベル確認を再利用し、Phase 2で製品状態との接続を確認する。新Test IDや同じ試験行列の複製は作らない。

| 段階 | 焦点 | 主要成果物 | 完了条件 |
| --- | --- | --- | --- |
| Phase 0 | 非VR基盤・観測（完了済み） | 固定Unity環境、非VR観測、Profiler／Traceと対応Capture | 必要な性能と因果関係・Frame相関を確認でき、観測資源をboundedに管理しCapture失敗をGameplayへ拡大しない。形式変更だけで再実行・再承認しない |
| Phase 0.1 | Capture非同期化（完了済み） | Main Threadを長時間待たせないCapture処理 | 21.15の非同期・容量・資源寿命・故障分離を引き継ぐ。Encoder、Worker、保存・通知形式、検証方法は実装詳細とし、今回の改訂で作り直さない |
| Phase 0.11 | 短時間NVENC確認（完了済み） | 対応環境のGPU画像から短い映像を生成する非同期Capture | 21.15のCapture経路での複数Frame確認と、非待機・容量・寿命・故障分離を満たす。固定fps・Frame数・時間・試験階層は要求しない |
| Phase 0.12 | 可変長Trace Writer | D-159と21.16のprivate Writer、producer専用固定容量Payload／Runtime Index Ring、固定Event mask、bounded Drain、stop／join後の単純sealを同一移行系列の内部backendとして実装する | 通常writeに共有locked RMWと実行中allocationがなく、payloadコピー完了後だけRuntime Indexが公開される。lane FIFO、wrap、Index／Payload容量不足、oversize Drop、固定件数Drain、最終Drainを検証し、現行WriterとCPU時間、copy byte、allocation、Dropを比較する。Release既定の切替と旧経路削除はまだ行わない |
| Phase 0.13 | MemoryBounded Paged Trace History | ProfileでPage size／Page数／総容量を決めてRun開始前に確保するPayload Page列、Pageごとの`CommittedByteCount`、History全体の64 bit `CommittedRecordCount`を0.12 backendへ追加する。History Index、Page状態enum、live Snapshotを持たない | 21.16.3の最大record全体のPage収容条件とProducer Lane容量条件を開始前に確認する。record全体を単一Pageへ書いた後だけcommit値を進め、Page末尾不足、History満杯、確保不能を待機や拡張なしでReject／Dropできる。停止後Viewは全record配列を生成しない。Release既定はまだ切り替えない |
| Phase 0.14 | 可変長Trace保存・読込みと切替 | 21.16.4のboundedな保存・読込みを接続する | 記録の相関と不完全性を維持し、全record配列を作らず保存・読込みできる。Release既定の採用と製品接続先がない場合の完了条件は21.16.1に従い、置換済み旧経路を削除できる。形式・旧Reader・Goldenの維持は要求しない |
| Phase 0.21 | 外部完成済みAsset受入れの正規経路 | 最初の利用Phaseに必要な10.2.3のAsset登録・内容識別・所在解決と一つのHarness接続 | 少数の実Assetで登録・更新と保持したrevisionの読込みを確認し、一つの対応Harnessへ限定サンプルを渡して実処理・結果記録まで通す。結果から実体と存在する参考資料へ辿れ、参考入力の有無・更新・失敗が標準実行の対象・集計・合否を自動変更しない。登録ツールだけで完了とせず、全Asset成功・全件実行・全Harness対応・乖離解消は要求しない |
| Phase 0.5 | 最小XRスモーク（完了済み） | 共用Sandbox Sceneの初期状態、OpenXR、Quest 3S有線Link、右手ControllerのGrip Pose＋暫定固定Offset、BladeAxis／EdgeDirection／SideNormal、位置・回転の利用可否を表す一つの追跡有効性、Single Pass | T-014だけで基本XRを確認する。Profilerは90Hzモードと明白な継続破綻の確認に使い、速度履歴、Gate、Stroke／Plane、Wave、校正UI、製品性能SLAを含めない |
| Phase 0.51 | Blade Sample／追跡不連続（完了済み） | 刀姿勢・軸・Cut Sample Point・時刻・追跡有効性を後続処理へ渡す内部Sampleと履歴Reset | 固定Pose列と右手Controllerの実入力で、位置または回転が無効なSampleを除外し、追跡喪失前と復帰後を速度区間として結ばない。型・field列は固定せず、速度閾値・Gesture・調整UIは含めない |
| Phase 0.52 | Gesture受付／Plane候補（完了済み） | Cut Sample Point速度と長軸成分除外、Edge Lead Score、暫定閾値、accepted samples、Stroke Begin、SourceSlashPlane候補、復路拒否・再準備 | 少数固定Pose列で往路受付、復路／峰側拒否、刀を返した新Stroke受付、斜め振り、復帰後の新Sample蓄積を確認し、受付列からfiniteなPlane候補を得る。Latch・Frame確定・SlashId・Waveは含めず、閾値は0.55で調整する |
| Phase 0.53 | HitなしSlashWave Coreと第一候補（完了済み） | 19.1のLatch／Frame、Emitter、初期Segment、19.1.5.1第一候補、Raw／Accepted Span、Live／Frozen Guide、Close、速度・Lifetime、前回／現在Segmentと閉Sweep領域、SlashWave VFX、開発用有限Wave格納 | 固定Pose列からGesture／Plane→有効FrameとLatch→初期Segment→Raw／Accepted Span→Close／Frozen Guide→Expireまで動作する。19.1のfinite条件・公開順序・満杯時規則を使い、Span非減少、Invalid保持、Plane／軸不変、CloseとExpireの分離、容量返却・複数Waveを確認する。少数Waveで19.1.8の面内配置、飛翔・Span拡大への追従と静的Geometry再利用を確認し、毎更新・毎描画のためのVertex／Index再生成・書換え・転送がないことをコードと必要時のProfilerで確かめる。Query・Hit・Cut State・Prediction・製品容量値は含めず、第一候補をモックで代替しない |
| Phase 0.54 | Sandbox診断・再生・比較UI（完了済み） | 同じCoreに19.1.12の可視化、Pose記録再生、Current／Pinned比較、実装済み第一候補名の表示を接続 | 第一候補を実入力・同じ記録Pose列で観察・再生・比較できる。比較候補がなければ常設の偽候補を作らず、交換境界の確認だけ一時Test Double等を使える。操作感の採否・最終値は決めない |
| Phase 0.55 | Slash UX実機探索・調整（完了済み） | 調整UI・Dump・Pose記録再生・Current／Pinned比較を使った第一候補の実機観察と、採用デフォルト値の確定 | T-034の第一候補観察・調整範囲。Quest 3S有線Linkで第一候補を観察し、採用デフォルトをMin speed 3.5、Latch chord 0.35、Span capture 0.25、Begin view dot 0.5に確定した。実機では体感良好。頭が上を向いた状態では上向きBeginが残り得る。大spanはVFX上の既知観察で、今回の操作感では支障なし。Hit/VFX側で必要なら扱う。第一候補の採用や全方式比較を義務づけず、採用構成を4.50へ引き継ぐ。同じ出力契約内の変更だけで0.51～0.54を再開しない |
| Phase 0.9 | 読み込みとUnity Mesh表示（完了済み） | 少数の代表AssetをUnity Meshで表示し、同形状InstanceはMeshを共有する。並行光源1つ＋ambient、基本表示・影 | 別Transformの複数配置で共有Meshと影を確認する。切断登録のTopology試験を前倒ししない。Phase 0.2のlicensed fixture由来の4カテゴリと組込みMeshの両方で共有Mesh・別Transform・影を確認し、形状データは公開リポジトリへ入れていない |
| Phase 0.91 | Unity Mesh表示最適化Probe（完了済み） | 同じ代表SceneでForward、Forward+、Forward+＋GRDを比較し、Mesh／Material共有と実際のGRD適用状態を確認する | Main Threadの描画準備・関連待ち、Render Thread、GPU時間から採用構成と理由を記録する。速度Gate、多数ライト・GPU Occlusionの全組合せ比較は要求しない。Adopted Grid代表SceneでForward、Forward+、Forward+＋GRDをQuest Link x64 IL2CPP Development Player上で比較し、Forwardを採用した。GRDはXR Single Pass InstancedでもHybrid Batch Groupとして適用されたが、この代表規模ではDraw／Batches削減がCPU／GPU時間改善へつながらなかったため不採用とした |
| Phase 0.92 | VP Stage 1（完了済み） | 明示操作でMesh→CPU AoS／Indexプール→GPU表現へ変換し、Direct・非indexed＋shader-side indexing／属性Pullingで描画する。AcquireReadOnlyMeshData等による取得、更新範囲の集約、両プール追記、Mesh／VP混在と影、複数Geometry参照の選択・表示構成・別Transform Instance | 少数Geometryで変換・混在表示・影・複数参照構成・選択と、互いに離れた複数部分を含む固定Geometryを一つの連続Index範囲として描けることを、Component検出・列挙・専用Metadataを前提とせず確認し、変換マイクロベンチを取得する。小さい初期GPU容量から1回拡張し、コピー・描画境界での参照切替後も既存表示を保ち、旧参照の更新・退役と投入済みGPU利用の終了後に旧Bufferが一度だけ解放されることを確認する。変換・転送費用と実際の表示開始フレームを既存計測で確認する。実切断・人形切断は要求しない。Phase 0.2のlicensed fixture由来4 Geometryで、32-byte AoS変換、単一CPU／GPU pool、複数rangeの選択、同一Geometryの別Transform、通常Meshとの混在、影、離れた部分を含む単一連続Index範囲、GPU容量拡張と旧Bufferのreadback完了後の一度だけの解放を確認した。x64 IL2CPP Development Playerでは1 Meshの通常準備は最大の代表例でmedian 0.135ms／p95 0.176msで、通常・容量拡張とも操作フレームから表示した。Quest Link 90Hzの96 Instance比較では96回のDirect発行がmedian 0.050ms／p95 0.064ms、GC 0で、全体CPU／GPUは通常Meshと同等範囲だった。一方でRender ThreadとSetPassが増えたため、Stage 2比較へ引き継ぐ。Shader負荷差を含むGPU差をVP方式の改善とはみなさない |
| Phase 0.93 | 消去と範囲アロケーション（完了済み） | 表示Instance／Geometry参照退役、4.5.3の予約・公開・寿命管理、退役Indexと未使用・失敗予約のbest-effort再利用。Published Vertex再利用・コンパクションなし | 少数の合成Jobで共通入力読取りと非重複出力への並行書込み、完了後公開、未使用予約回収、参照中Indexの再利用抑止、退役後再利用、予約不足後の再実行、完了回収後のReserved所有権引継ぎ、CPU範囲Publishedが内部読取り許可であることを確認する。切断器へ依存しない。Index範囲のFree／Reserved／Published／Retiring状態とRead Lease、address-ordered first-fitの予約・分割・隣接結合、部分Publishによる未使用末尾返却、追記Vertexと再利用Indexへ分けて格納するCPU storageとMesh変換（成功時Publish、失敗時Cancel）、表示Instance／Geometry参照の一度だけの退役を確認した。合成Job 2本が同一Published入力をそれぞれのRead Leaseで読み、非重複Reserved出力へ並行書込みし、Main Threadの完了回収後だけ部分Publishで所有を引き継いだ。予約不足はCancel後に必要数で再予約・再実行した。Lease保持中に退役した範囲はRetiringで再利用されず、最後の返却後に再利用され、Vertexは追記保持のままだった。x64 IL2CPP Development Playerで、予約・部分Publish・Lease読取り・退役・Cancel・再利用の定常サイクル100,000回を3 Run実行し、managed allocationはすべて0 byteだった。 |
| Phase 0.94 | VP Stage 2（完了済み） | Stage 1のGeometry表現・アロケータを維持してIndirect・非indexed＋shader-side indexing／属性Pullingを実装し、描画要求の集約・引数管理・個別発行を見直す。正負連続Index配置による粒度削減をIndirect APIの自動融合とみなさない | Stage 1と同じ小規模代表Sceneで機能を維持し、実際の発行経路とMain Threadへの効果を比較して採用を判断する。固定改善率・全Scene高速化は要求せず、効果が乏しければ人間判断でStage 1を採用して進める。4 Geometry・96 Instanceの同一構成をQuest Link 90Hz・Single Pass Instancedのx64 IL2CPP Development Playerで比較し、Stage 2ではForwardとShadowを分離したIndirect 2発行に集約した。SPIのForward引数だけを左右眼分に倍化し、Shadowは論理Instance数のままとすることで、左右眼の表示・カラー画像上の遮蔽関係・影をStage 1と一致させ、非XR自動テストでは深度関係も維持した。以下の時間はいずれも各構成3 Runのmedianの中央値である。VP GridのCPU側描画API発行は96回から2回、発行区間は0.028msから0.005msへ短縮し、区間内GC allocationは0だった。CPU Render Thread Frame Timeは0.685msから0.544ms、ExecuteRenderGraphは0.408msから0.197msへ低下した。CPU Main Thread全体はStage 1の1.536ms対Stage 2の1.581msでRun間のばらつきに収まり、全体改善は確認できなかった。GPU Frame Timeは1.448ms対1.415msで範囲が重なり、再現性のある悪化はなかった。ここでいう2発行はCPU側API呼出しであり、Indirect commandやGPU draw callが2件という意味ではない。Stage 2を採用し、GPUカリングとゲーム規模での種類数・Instance数のスケーリングは別作業とする |
| Phase 1 | 即時切断／Dispatch境界（完了済み） | Phase 0.9～0.94のVP基盤、4.3の実行先・Main配置、合成GeometryとOwnerの点Anchor集合、正負論理子・切断履歴の公開、単一clip・仮分離・簡易断面、Harness内の合成Final Physics入力（低頂点1 Hull）、片側空No-op・受付上限、4.4の共有Dispatch・物理仕事の投入余地・取消・非blocking完了回収 | 少数の合成入力と選抜済みFixtureで即時表示を確認し、Harness内合成Final成功／失敗からT-074の公開・Abort・Anchor配分を確認する。実Convex切断・Rigidbody・cookと固定物理の結合はPhase 4へ置く。支持を理由に描画を省略しない。未公開親への受付制限、No-op／混雑時の非変更、公開前の子先取り禁止を確認する。T-090の合成Workで同一フレーム内の複数Dispatchを確認し、後続Phaseを内部Queue型へ依存させない。2026-09-18に要求と実装・試験を突合し、この範囲で完了とした。確認できた範囲は、合成Final成功／失敗による正負2子とOperationの一体公開およびSource退役、Sourceの現在集合からの正負配分とOnPlane両側継承、非identityな剛体配置を既存の面変換経路で通した配分と公開、公開前の子先取り拒否とActive Sourceへの受付見送りおよび公開後の子受付、用途別Dispatchと有限容量・予算非再充填・取消・非blocking回収と同一フレーム内の複数Dispatch、指定されたIL2CPP Player確認（実thread・Burst・共有入力／独立出力・Queue待ち・通常停止）、Main CPU Setsの明示適用・読戻し・復元と他threadを変更しないこと、単一切断の論理状態から仮分離・簡易断面表示までの接続である。**今回の完了に含まない範囲**：実Physics／実Convex／cook、Geometry Commit、4.3／4.4の機構を製品の起動／終了点へ接続すること、多物体・再切断・自動Color分類の表示、Shadow品質、採用MSAA構成の決定。これらは今回の完了範囲に含めず、該当する後続作業または別途の判断事項として残す |
| Phase 1.50（完了済み） | 選択済みVP経路のXR／Single Pass確認 | 選択済みVP経路（D-178によりStage 3。0.94の採用記録はStage 2）、既存Geometry参照・Instance Transform・Draw DescriptorのColor／Depth描画 | 0.5とPhase 1の完了後、Quest Linkで一つのGeometryを複数Transformで表示し、左右眼のGeometry／Transform／Instance選択、片眼欠落がないことを確認する。Clip・Stencil・Shadow品質・Stage再比較・製品90fps SLAは含めない。2026-09-18に既存証拠を条件へ照合し完了とした。Stage 3経路で一つの合成Geometryを5 Instanceの別Transformで表示し、左右眼それぞれで全Instanceの選択・片眼欠落なし・二重化なし・向きと大きさの入れ替わりなしと、通常Meshとの前後関係によるColor／Depthの成立を確認した。成立条件はEditor Play Mode／Quest Link／D3D11／Single Pass Instanced／MSAA無効であり、Playerビルドと性能はこの完了に含めない。証拠一覧は照合資料Phase150152Closure/20260918-212317（訂正版）を参照する |
| Phase 1.51（完了済み） | VP Clip確認 | 少数の固定合成入力による面・Side・Offset（当時の表示専用Offset。2026-09-21に撤去済み）、5.2の最大8面のRaster clipping。入力field構成と具体的なFixture列は実装詳細 | Color／Depth／ShadowCasterで同じ固定入力を使い、未使用・容量内・上限8面のSV_ClipDistance、採用MSAA構成と左右眼の一致を確認する。Pending Cut、候補選択、Plane overflow、Cap／Stencil、Shadow画質・性能評価は含めない。2026-09-19に、既存の固定Clip確認（Stage 3経路、MSAA無効）と採用MSAA構成4x（D-179）での確認を合わせ、要求範囲で完了とした。4xでは描画時のColor／Depth-Stencilがともに4 sampleであり、XR Simulatorで未使用・容量内・上限8面の切除、正負とOffset、ShadowCasterの片面／両面の差がMSAA無効時と同じく成立し、左右眼の画像を取得した。Shadowの影の位置は、既存シーンの光源設定とURPの主光源選択規則から事後に算出した位置への照合であり、どの光源が選ばれたかは実行時に直接観測していない。Quest Linkでは本人1名による各状態1回の目視比較を行った。成立条件はEditor Play Mode／D3D11／Single Pass Instancedであり、Playerビルドと性能はこの完了に含めない。証拠一覧はMSAA比較資料を参照する |
| Phase 1.52（完了済み） | VP Stencil基本確認 | 正向きの固定合成閉Geometry、既知Cap Polygon、固定Clip Descriptor、少数の固定割当てStencil Color | 全8bitを使い、Colorごとの128初期化、Wrap加減算、Ref 128 / Comp LessでS>128だけがColor／Depthを書くこと、全Volume後に全Cap、Color間の再初期化、左右眼一致を確認する。Cap生成・Cull・Compatibility・Residual Support・Color割当て・Pending Stateを前倒ししない。2026-09-18に既存証拠を条件へ照合し完了とした。根拠は三つの組合せとする。Shader側の設定（Init `Ref 128 / Comp Always / Replace`とRead／Write Mask 255、Volumeの両面IncrementWrap／DecrementWrap、Cap `Ref 128 / Comp Less`とWrite Mask 0、Init／VolumeのColor／Depth非書込み、Colorごとの3 render queueによる初期化→Volume→Capの順序）、非XRの画素試験（生Count `-1／0／+1／+2`で正のときだけCapが出ること、Volumeなしで0画素、本体外へ出ないこと、不合格SampleがColorもDepthも書かないこと、2色でのBase再開と先行描画の保持）、固定ColorのXR確認（Quest LinkのSingle Pass Instancedで左右眼のCap成立、漏れ・欠け・前後関係、2色の分離）である。成立条件はEditor Play Mode／Quest Link／D3D11／Single Pass Instanced／MSAA無効であり、Playerビルドと性能はこの完了に含めない。Stencil値の直接readback、Wrap境界（255→0）、実Attachmentの特定と8bit排他利用は、今回の低レベル確認では直接検証しておらず、一般構成への保証は含めない。これらを本Phaseの新しい完了条件にはしないが、T-067とPhase 2、4章が求めるAttachment・8bit排他利用の要件を免除するものではなく、後続の製品接続で確認する。証拠一覧は照合資料Phase150152Closure/20260918-212317（訂正版）を参照する |
| Phase 2 | 仮断面・影強化 | 表示／Stencil共用基底Geometry、6.2の入力Gate、`RenderCutTopologyMap`、T-084、ゼロKerf、LogicalCutOperation、TemporaryRenderCapRecordSet、OBB交差Cap Bounds Polygon、両眼Frustum／Facing Cull、128初期化の正符号8bit IncrementWrap／DecrementWrap Stencil、Residual Stencil Supportの保守的投影競合、符号保存のCap仕事とStencil Volume Group（D-183）、`MaxStencilColors`と最後のColorの旧方式（D-185、D-186）、Color単位Volume／Cap Batch、`TemporaryClipConstraintCandidateSet`、5.2の最大8面のRaster clippingとPlane overflow処理、共通トゥーンの粘土色グレー、Temporary／Committed断面デバッグ色、ShadowCaster用同一即時Clip、XR両眼対応、Pending Cut／Stable履歴管理、T-067／T-089 | 1.50～1.52の成立後、そのShader／Passへ製品状態・Cap生成・Batchを接続する。2～4連続切断と複数対象で、表示とStencil Volumeが同じ合格済み基底／Stable Geometry、Topology、windingを参照し、用途別Geometryや描画時のGeometry再検証を持たない。通常Colorは左右眼の非互換Residual Supportを分離し、Color数を固定上限内に保ち、同じColorでは全Volume後に全Capを描く。Self-intersection、別Topologyの重複／Coincident、Internal／Nested、全体反転を向き保存で受理し、共通入力Gate不合格は切断対象へ登録しない。符号証明、向き正規化、Winding上界、Count容量分割、符号別Groupは作らない。`S=(128+W) mod 256`と`S>128`を使い、範囲外は5.2の品質例外とする。通常Colorに入らないCap仕事は最後のColorで旧方式により描き、その誤描画は5.2の品質例外8とする（D-185、D-186。8bit Windingの範囲外に関する上記の品質例外とは別の扱い）。GPU時間は測定対象に留める。8bitを排他利用できない構成は4章の共通Player終了に従い、部分Bitや代替経路を持たない。候補面の選択・全Pass／両眼への共有と超過処理を5.2に従って接続する。超過した後発面からは即時Stencil Volume・描画用Cap板・clipを生成せず、論理／背景処理を残す。Cap pair／Coverage探索、Cap単位Buffer compaction、Mesh部分更新、多段Fallbackを行わない。Color割当ては5.6の実装自由度に従い、全Graph構築を必須としない。Camera内部／Near Plane近傍では5.2の品質例外を許容する。Shadow MapではStencil Capなしの影近似を使用する |
| Phase 2.9 | 表示／Stencil共用メッシュ切断Kernel先行実装 | 6章のTriangle切断・属性補間・Contour／Cap生成・Topology対応更新を行い、caller提供のグローバルVB／IB範囲へ出力するBurst数値Kernelと検証コード | 現行Unity環境でbuild・実際のBurst実行を確認し、6章の共通契約、seam、Cap、再切断、既存Vertex再利用、新規Vertex・正負Indexの直接配置と容量安全を代表入力で確認する。製品アロケータ・Job wrapper・GPU・Renderer・Geometry Commitへ未接続でも完了する。性能確認は14章、検証詳細は17章に従う |
| Phase 3 | 共用表示／Stencilジオメトリ統合 | Phase 2.9の数値KernelのGeometry Pool実行と、既存VPプール・範囲所有権・共有Dispatch・GPU転送・Renderer・4.5.6の祖先順Geometry Commitとの接続 | T-006／T-083の統合部分を確認し、数値確認は2.9を再利用する。合成Final Physics／Logical公開後の正負Geometry・Boundaryの後着、A→BのKernel／Commit順序、Temporary置換と全Passの共用を成立させる。新規Indexの転送1回／再利用時0回、空Geometryへのdummy非生成、Stale回収・予約不足の非公開を維持する。重い頂点処理をMainへ戻さず、通常の全Job／GPU待ちを追加しない。容量・内部エラーは4章／4.5、出力契約は6章に従い、実cookはPhase 4へ残す |
| Phase 3.9（完了済み） | Physics Convex数値Kernel先行実装 | 7.2のB-rep clip、非交差継承、内接削減、質量特性、worst-case容量照会を行うBurst数値Kernelと最小Harness | 現行Unity環境でbuild・Burst実行でき、7.2／7.6の数値契約、削減成功、局所退化を含む採用B-repの再切断、予約範囲内の実行を代表入力で確認する。中心近傍の共通局所frameを使用し、Actor・Anchor・Cook Frameとの接続はPhase 4に残す。MeshData、製品アロケータ・Job wrapper、Mesh／Bake／Actor／Commitへ未接続でも完了し、Phase 4／4.1の完了とは扱わない。検証詳細は17章に従う |
| Phase 4 | 物理 | 7.1の短寿命PhysicsSplitTransaction、7.6のrobust support、実Actor／Shape／cookとLogical Publicationの一体公開、旧Cooked Geometry Lease、anchor-offset D6、Phase 3.9の数値Kernelを使う7.2のOwner単位Cut/Cook統合・事前容量予約・P1 recenterの物理接続、単一Cooking Profile、初回速度継承・Final handoff、0.5G仮設定 | Phase 1～3のHarnessとPhase 3.9の数値Kernelを実物理へ接続し、T-005／T-059／T-069／T-074／T-085／T-086／T-091を確認する。正常成功は正負2所有者、Geometry空はRendererなしとし、Final先着・Provisional構築不能・Final不成立・Stale・個別退役を7.1で閉じる。通常LogicalFragmentを単独退役する低レベル処理も本Phaseで実装し、7.9／7.10のGC Policyと4.5.3のPublished済みVB回収は前倒ししない。切断・BakeのMain Thread停止を避け、暫定的な実行枠・メモリ予算で既存Fixtureを回帰する。Unity経路の要件違反だけD-086で再検討する |
| Phase 4.1 | Cut/Cook Profiling | Phase 4の製品経路と代表Fixture、既存Profiler／Harness | T-076で7.5の費用を確認し、O-035／O-039の暫定実行枠・メモリ予算を調整する。保存形式、分位、反復数は実装詳細。Slashの到達Deadlineへの適用はPhase 4.53へ分ける |
| Phase 4.2 | Player非接触Locomotion | Player Layer非接触、Level初期化時の固定PlayerLocomotionOccupancy、候補次姿勢Overlap Reject、T-088 | 人工移動の要求全体Rejectと、物理所有者・切断・Commit・Fragment・GCへ追従しない固定集合をT-088で確認する。実空間HMDはClampせず、Camera被り・内部視点はD-131の許容に従う。退出処理や将来のOccupancy更新を要求しない |
| Phase 4.3 | 建物由来子のWorld D6と一般外部Joint撤去 | 7.2.2のIsBuildingDerived／BuildingSplitDepth、通常1→2公開でのWorld D6生成、指数Limit、Actor寿命と既存失敗境界への接続、既知Constraint識別、T-094 | 手書きSyntheticで生成・建物由来だけの予定Depthと正式公開・Abort・Final handoff時の維持・構築不能を確認する。一般外部Jointの継承・付け替え・保護・予測を要求せず、4.54が既知Constraintを識別できる。拘束効果を保証せず、5.6分割・5.7 GC・未来予測本体を待たず完了する |
| Phase 4.50 | 製品SlashWave Core | 製品のGesture入口、交換可能Latch／Frame／Span Candidate／Span Close Estimator、Latch済みFrame、0.53のCoreと0.55で採用した構成、Raw／Accepted Span accumulator、単一Segment、WaveLifetime、SlashWave VFX、複数生存Wave用の有限固定容量、追跡異常と再準備 | 発射時の面・軸・初期形状と評価設定が19.1.4に従って保たれ、Raw候補が減少・InvalidでもAccepted Spanが縮まない。完了済み過去更新区間を再評価せず、現在更新区間内のSpan増加領域は後続Hit Phaseの共通Sweepへ渡せる。Span Close後は採用方式の候補評価を使い、WaveはWaveLifetimeで有限終了する。Latch／Frameの切替は未Latch評価へ、Span Candidate／Closeの切替は後続Slashへ反映し、生存WaveはLatch時方式・設定と必要な一時状態を維持する。19.1.6とT-035に従い、公開前の容量確認と満杯時の新Latch見送り・同じStrokeの非再試行を確認する。実対象Hit、Cut、候補範囲、Predictionを要求しない |
| Phase 4.51 | Segment HitとProp現在状態切断 | 生成時の退化Segmentを含む共通閉Segment Sweep、現在採用Physics Convex集合とのNarrowphase、`LogicalFragmentRef`系譜単位消費、`SlashHitConfirmed`、既存7.6受付、現在状態からの通常Prop切断 | Predictionを全て無効にしても、Slash生成→飛翔→実Hit→即時表示→Geometry／Physics処理の基本Propループが成立する。消費済み系譜と祖先関係を持たない別Fragmentへ個別Hitでき、消費済みFragmentの子孫は同Slashで再切断しない |
| Phase 4.52 | Humanoid現在Pose切断 | 少数Tableの準備・Current再生、実Bone Pose Snapshot、同期SkinnedMeshRenderer.BakeMesh→VP、共用切断、骨Physics Proxy分類、物理移行、T-008 | 必要なTable評価の最小部分を接続し、動作中NPCを現在Poseから切断できる。限定再生・固定入力でよく、移動研究・未来評価全体・非同期ベイクを待たない。Prop＋NPCの基本Playableを完成とみなせる |
| Phase 4.53 | 静止／姿勢固定対象の先行切断 | 導出可能な場合の保守的Candidate Flight Bounds、導出不能時の有限な先行準備範囲、候補列挙、DAG、Ready Workの共有Dispatch投入、静止姿勢の投機Geometry／Convex、実Segment HitだけのCommit Gate、空振り回収、Phase 4.1性能曲線のSlash Deadlineへの当てはめ | 静止対象で先行成果物を再利用でき、有限包絡を保証できない構成では先行準備範囲外を未準備のまま許容する。範囲外を含む実Hitは4.51の候補検索・現在状態切断へ戻り、Gameplay Clamp、範囲拡張、再探索を要求しない。基本ゲームの成立条件にしない |
| Phase 4.54 | 自由飛行剛体の直接予測 | O(1)固定刻み予測、`DirectRigidPredictionEligibilityGate`、WorldPhysicsProfile／FixedStep統合、T-017 | 対象内だけ先行成果物を作り、対象外または検証不一致は4.51へ戻る。Local Planeリベースは必須にしない |
| Phase 4.55（任意） | 剛体Local Planeリベース | 現行19.5.1／D-149／T-093の限定実装・比較・採否 | 基本ゲームまたは4.54の必須条件にしない。未実装・延期・不採用でも後続Phaseへ進める |
| Phase 4.61 | Pose Table評価の本体確認 | 19.3の共通評価とT-018 | 4.52の最小評価を継続利用し、Current適用とFuture任意順評価、Clip・時刻・骨対応・再生規則を確認する。方式再選定、MobPlan生成、頂点ベイク、人形先行切断は要求しない |
| Phase 4.65 | 未来予測用非同期ベイクの比較・採否 | 4.5.2の不変Rig Pose＋共有source skinning入力から共通CPU側VP入力を生成する限定実装と同期経路の比較 | 少数の対応済みSkinned入力と固定Poseで品質・非同期回収・Main Thread負荷を14章／21.2に従って比較し、人間が導入の採否を決める。効果がなければ不採用も正常完了。本体DAG／VPプール接続と人形切断の完成は不要 |
| Phase 4.70 | Mob未来計画の本体導入 | 研究後に人間が採用した移動・Animation計画方式と20章への接続、T-092 | 現在／未来整合、更新・失効・寿命・予算を確認する。固定計画による先行接続検証だけでは完了しない。非同期ベイク不採用でも計画単体で閉じ、未来VP入力・人形先行切断は要求しない |
| Phase 4.71（条件付き） | 未来VP入力準備統合 | 4.61＋4.65採用結果＋4.70を4.53の投機DAGと既存VPプールへ接続し、候補Pose／VP入力準備・失効・回収を行う | 4.65不採用ならPhase自体を省略し、未完了負債にしない。人形の実切断Commitをまだ要求しない |
| Phase 4.72（条件付き） | Humanoid先行切断統合 | 4.52の現在Pose経路、4.53の投機経路、4.71の未来VP入力を接続 | 有効成果物採用、未完成／Pose不一致時の4.52同期経路、世代失効回収を確認する。4.65不採用なら省略可能 |
| Phase 5.6 | 追加空間分割（任意） | 7.9の単一平面探索・範囲内部の面配分、新Index領域への振り分けコピー・必要転送・旧領域Free、既存Convex処理・cook・質量・点Anchor、7.2.2の建物Depth・子D6生成と親D6退役、非命中公開、全体1未回収試行と入力別抑止 | 大きなIndex範囲内の離れた部分を2物体へ分け、Vertex共有と読者寿命後の旧Index回収を確認する。成功後は通常再切断でき、不成立・無効時は元物体が通常完成状態で残る。通常切断とGCを依存させず、7.9.5の共有資源競合による遅延を許容する。Phase自体を省略可能 |
| Phase 5.7 | 表示なし物理物体の遅延回収（任意） | 7.9の確定空判定、物理所有単位の登録終了、既存Actor／Shape／システム所有Constraint／Job資源退役への接続 | 7.9.7のGC選択・接触中退役・共有資源寿命・所有D6の一度だけの退役・後着成果物拒否を確認し、無関係Siblingを維持する。追加分割の実装・有効化・成功へ依存せず、Phase自体を省略可能 |
| Phase 6 | コンテンツ | Synty City街区、10プロップ、単一並行光源＋ambientでのシェーダ統一、既製モーション | 垂直スライスとして一連の遊びが成立 |
| Phase 7 | 実測後最適化 | 端末別品質、破片LOD、既存Profiler／TraceとT-076による負荷確認、遠距離確定、ストレス試験 | ターゲット実機で性能予算を満たす。Schedulerを含め具体的な不足が確認された箇所だけ、別設計変更として必要な最適化を行う。初期Cooking Profileより再cookに実測上の価値がある場合、別設計変更として任意導入を検討する |
| Phase 7.1（任意） | LogicalFragment GCとVB回収 | 7.10の寿命Policy、4.5.3のPublished済みVB回収・再利用、必要な所有・利用管理と7.1.3の共通退役への接続 | 7.10の最小確認を行い、通常切断・Abort・生存Siblingを維持して個別退役と不要VBの実再利用を確認する。方式と内部構造は固定せず、改善量・回収期限・容量収束を保証しない。Phase自体を省略可能 |

Phase 0.9～0.94はPhase 1より前に実施する。Phase 1.0という呼称も同じPhase 1を指し、既存Phaseは一括改番しない。各0.9xは先行成果を使いつつ独立に完了でき、即切断、実切断、Cap、物理切断、未来予測の完成をGateにしない。Stageは描画方式の段階でありPhase番号とは別である。0.94の実装・比較は必須とし、採用結果に応じてPhase 1へ進む。実行時自動Fallbackを追加しない。

Phase 1.50と1.52は、2026-09-18に既存証拠を各行の条件へ照合して完了とした。Stage 3経路での確認（非XR、XR Simulator、Quest Link）を根拠とし、成立条件はEditor Play Mode／Quest Link／D3D11／Single Pass Instanced／MSAA無効である。これらの確認はいずれもMSAA無効の構成で行ったものであり、その記録は変更しない。採用MSAA構成はその後D-179で4xと定め、Phase 1.51は2026-09-19に既存の固定Clip確認と4xでの確認を合わせて完了とした（Phase 1.51行）。MSAA無効での確認を4xでの確認とは扱わない。1.50・1.52の完了は、Playerビルドでの成立、性能、実Attachmentの特定と8bit排他利用へは広げない。これらは今回の低レベル確認では直接検証しておらず、一般構成への保証は含めないという限定であって、T-067とPhase 2、4章が求める要件を免除するものではない。

Phase 2のうち、Cap仕事方式（D-183）の製品接続の単位、最後のColor（D-185・D-186、bb9cd38）の実装単位、5.3の断面色と現行の陰影への接続（01472bb）は、2026-09-20に区切った（5.6の「実装状況」）。Phase 2全体とT-066・T-067・T-089は完了扱いにしない。

Phase 3のうち、非同期切断Work（773bb09）、Cut DAGの骨組み（c97e43c）、実Geometry Commitと表示差替え（0df0392）、採用面のframe写像の切断入力への接続（b98661b）の4単位は、2026-09-20に区切った（4.5.6の「実装状況」）。確認は静的Geometryと合成Final Physicsによるものである。Final Physicsの公開からこの表示経路への配置の受渡しは、その後7.2の「実装状況」の範囲で成立した（9993ece。Selected面だけで成立する非Character・直接Final経路）。Ignored集約の配置も、その後D-187の範囲で成立した（f72f7d0。5.6の「実装状況」）。残るのはPhysics／Geometry Workの自動駆動、製品の構成根、Character、この経路でのPlayer・性能で、いずれも未接続または未確認である。Phase 3全体とT-006・T-083・T-074のPhase 3部分は完了扱いにしない。

Phase 4のうち、Cut／Cookの未公開成果物まで（74bbd8e）、正負Final Owner候補の未公開構築まで（2135bf4）、直接Final分裂の物理・論理とOwner対応の公開接続（cc168a6）、Owner配置から表示の配置照会への接続（9993ece）、Provisional一式の未公開構築まで（f06f462）の5単位は、2026-09-20に区切った（7.2の「実装状況」）。Temporary表示の追従は、**Selected面だけで成立する非Character・直接Final経路**について成立した。確認は合成入力によるもので、Ignored集約の配置はその後D-187の範囲で成立した（f72f7d0。置換済みSourceを集約rootとしたときの停止は解消した。5.6の「実装状況」）。Hit／Queryの解決、Provisionalの公開・建物D6・Final handoff（Provisionalの未公開構築と回収はf06f462の範囲で成立した）、この公開経路からGeometry側を駆動する接続（採用面のframe写像はb98661bで成立。4.5.6の「実装状況」）、Character、製品の構成根、Player・性能は未接続または未確認である。7.1.2全体の一体公開は未完成であり、Phase 4全体と関連する受入れ項目は完了扱いにしない。

0.9／0.92の完了記録にあるPhase 0.2入力は当時の実施証跡であり、新規利用や今後の再実行に同じ入力を要求する規範ではない。旧入力の移行だけで完了済みPhaseの測定・承認を再実施しない。

0.92はMeshデータ取得、AoS変換／CPUコピー、GPU転送発行を分けて軽量計測し、SetData呼出し時間を実GPU処理時間と混同しない。初回確保・容量拡張は通常変換と別に測り、新たなBenchmark Dataset／Schema／保存基盤を作らない。

Phase 2.9の直接出力をPhase 3で既存VPプール・転送・現在frameでの公開へ接続する。切断Kernelの意味・Topology・Cap品質・世代の有効性・表示／Stencil同時公開は維持し、Final物理所属の確定をIndex配置・転送の前提にしない。詳細layoutやKernel内部の書込み方式は必要な段階まで未決とし、物理用の処理は7.2の実行分担に従う。Phase 4.53の剛体先行計算と、4.5.2の人形経路分離も同じ出力と準備済み範囲再利用へ接続する。0.92へAnimation／Pose Evaluatorを前倒ししない。GPU並行ベイクは実測に応じた後段最適化に残す。Stage 3はD-178により現在の採用経路であり、後段最適化には残っていない。

Phase 4.52で少数TableのCurrent評価を接続し、4.61は同じ評価処理のT-018確認で閉じる。4.65は固定Rig Poseによる限定非同期ベイク・比較・人間採否を維持し、Pose Table採用を非同期ベイク採用へ読み替えない。4.52／4.61／4.65は移動研究の完成を待たない。4.70は研究結果から人間が製品方式と必要な要求・完了条件を決めて本体へ導入し、研究側の全仕様・Dataset・UIを自動採用しない。内部構造は実装詳細に留める。非同期ベイク採用時だけ4.71で未来VP準備、4.72で先行切断を統合し、不採用なら両Phaseを省略する。4.70と採用時の4.71／4.72を終えてPhase 6へ進み、少数Fixtureのために0.21の全Consumer対応を前倒ししない。

Phase 1～3はHarness内の合成Final Physics成功／失敗入力により7.1のLogical Publication／Abortと4.5.6のGeometry順序を検証する。製品Runtime用の代替Physics Modeや公開schemaは作らない。SourceがActive中は受付を見送り、別LogicalFragmentは並行可能とする。Final／Logical公開後はGeometry未完了でも子を受付け、後続Kernelだけ祖先Commitを待つ。実Actor／cook／D6との一体公開はPhase 4で確認する。

4.3／4.4の共通投入・回収とMain配置はPhase 1のDispatch責務で導入し、着手済みなら差分作業とする。T-090の通常完了・失効はTest Doubleを使い、実thread・Burst・共有入力／独立出力・Queue待ちと完了未回収の保護は少数のIL2CPP Playerシナリオで確認する。Main配置は対応Windows hybrid環境でTopologyに基づく集合、設定・読戻し・復元と少数の配置sampleを確認し、自コードが他threadの設定を変更しないことを確認する。全OS／CPU行列や絶対的E排除の証明を要求しない。

Phase 3で実MeshCutをGeometry Poolへ、Phase 4で通常Physicsの数値処理とBakeをurgent Unity Jobへ接続する。非urgent Bakeは最初に利用する投機・任意分割の担当Phaseで、MainのMesh適用→BackgroundのBake→Main回収・利用を固定版IL2CPP Playerで確認し、同一Mesh多重Bake防止・失効／停止時の寿命は既存試験を再利用する。未実装の非urgent用途をPhase 4へ前倒しせず、独立Probeや2.9／3.9の全面再検証を要求しない。未来数値処理・Mob計画・任意GCは各担当Phaseで必要な範囲を接続し、3.3／14章のPlayer確認を共用する。

Phase 1は合成OwnerとfiniteなFrame内点集合で7.1の配分・固定判定、正負子とOperationの一体公開を確認する。Phase 4は同じ規則を実Owner／Actor・既存frame、Provisional／Finalへ接続する。Asset受入れへ製品用Anchorの生成や入力の一括再登録を遡及要求せず、実際に付随入力を変更した場合の内容識別は10.2.3に従う。

Phase 4のcook待ちは7.1の一度だけのProvisional構築と直接Finalの順序に従う。通常Pendingと構築不能を区別し、成立不能はSource退役へ閉じる。Final handoffで採用する自前B-repの包含は7.2に従い、Colliderのpose／速度を補正して救済しない。旧Cooked Geometry共有・D6・Leaseの確認はT-091へ集約する。

Phase 4.3はPhase 4.2の後、4.50の前に置く独立Runtime Phaseであり、章番号4.3とは区別する。汎用物理のPhase 4、既存Baselineの4.1、Player非接触の4.2の完了条件へ建物D6を混在させない。Phase 4.54はその既知Constraint識別境界を使用する。

Phase 5.6／5.7は本体の既存入力・公開・退役境界を使う後期の独立した任意Phaseであり、双方または片方を省略してPhase 6へ進んでも未完了負債にしない。専用処理・試験を前段へ前倒しせず、7.9.7を実装する機能だけに適用する。

Phase 7.1は通常切断・共通退役・VPプールへ後付けする後期の独立した任意最適化とする。Published済みVBのFreeと必要な管理情報を含め、前段Phaseへ準備実装・hook・未使用APIを要求しない。導入時の既存コード変更は本Phaseで行い、旧内部APIの維持や二重実装を要求しない。Phase 5.6／5.7やPhase 7全体の完了を前提にせず、他Phaseも本Phaseの実装・有効化を待たない。未実装・延期・無効・省略を基本Playable・垂直スライス・他Phaseの未完了負債にしない。独立Phaseという区切りのために別の状態体系・Coordinator・Assembly・Sceneを設けない。

## 16. 垂直スライス受け入れ基準

基本Playableは4.51／4.52の現在状態経路で成立させる。本章のPrediction・MobPlan・製品Assetの確認は担当する後続Phaseへ適用し、基本Playableへ前倒ししない。任意4.55と条件付き4.71／4.72の省略条件は15章に従う。

Temporary Stencil Capの見え方に関する本章の受入れ基準には、5.2の明示的品質例外を適用する。物理Convexの内接削減による接触・形状・No-op・質量特性の変化には7.2の許容を適用する。即時表示は4.5.2の必要なベイク・VP変換後に始まり、残る準備費用による表示開始の遅れを許容する。4.5.4のGPU容量拡張に伴う停止と容量限界での開始拒否／終了を許容し、無制限の切断寿命を要求しない。固定状態による仮描画省略を行わない費用、7.9の任意分割でのIndexコピー・新旧範囲共存を許容する。実断面Geometryの品質、支持・物理の安全条件、世代の有効性は緩和せず、表示Commitは4.5.6の現在frame・参照・転送条件に従う。

- 刀の高速移動でも代表プロップを安定して切断できる。

- 必要なVP準備後に始まるclipと仮断面が両眼で一致する。両側が固定でもclip／Capを維持する（表示側はどの側も移動させない。物理側のImpulseは7章による）。幾何学的なFrustum／Facing Cullと5.2の即時Clip上限を使い、支持による表示状態・再有効化待ちを持たない。

- 通常断面は全体と同じトゥーン陰影の粘土色グレーで統一され、仮断面から実断面への差し替えで特殊な質感変化が見えない。

- 即時切断物体のShadowはカラー表示と同じclipと配置に追従し、両面Shadow近似からStable実断面の片面Shadowへ移る際に目立つ影の跳びがない。

- 左右眼の一方だけでも初期断面の投影が重なる異なるVolume Groupは、通常Colorで分離される。描画用Capの投影が非交差でも、初期断面が重なれば同じColorにしない。OBB投影と初期断面のどちらかが両眼で非交差なら、同じColorへまとめてよい。通常Colorに入らないVolume Groupは最後のColorで旧方式により描かれ、その誤描画は5.2の品質例外8として許容する。Color上限だけを理由にカメラ準備は拒否されない（D-185、D-186）。5.2の明示的品質例外を除き、別物体のStencilによる仮断面のはみ出しがない。Conflict Graphの明示構築は要求しない（D-183）。

- 通常Colorでは、同じ登録・同じCap境界とSideで、Volumeへ渡すGeometry・配置・平面が同一のCap仕事は、Volumeを1回だけ発行して同じColorで描かれる。最後のColorでは、同じGroupの仕事が複数のRenderFragmentにまたがっても、VolumeはRenderFragmentごとに発行する（D-186）。別々に動いて配置や平面が変わったフレームでは、別のVolume Groupへ分かれる。

- 通常Colorでは、1つの破片に複数のSelected面があっても、可視Capが他のSelected面の開口との計数の打ち消しで欠落しない（D-182）。最後のColorでの欠落は5.2の品質例外8とする。

- FacingによるStencil処理の省略は、両眼ともFacing epsilonを越えて明確に裏向きのCap仕事に限る。片眼だけ可視、またはepsilon帯内のCapをFacingで省略しない。

- 5.3に従い、デバッグ有効時は仮断面が赤、公開済み実断面が緑となり、無効時は両者が通常グレーとなる。元AssetのUV対応域は4.5.1とし、対応域外を拒否する。

- Geometryは4.5.6の祖先順にCommitし、実体化したTemporaryだけを回収する。Final Physics／Logical Publicationを先に成立させ、Geometry CommitはDraw List構築・GPU完了・実表示を待たない。一つの描画Snapshot内の全Passで同じGeometryとTemporary集合（Selected面集合）を参照し（実際に適用するclip面は、本体・Depth・Shadowが全Selected面、Stencil Volumeが通常Colorでは自身のCap面だけ、最後のColorではRenderFragmentの全Selected面）、GPU更新が対応Drawに先行する順序はRenderer側で保つ。

- 表示／Stencil共用VP Geometry切断と導入採用時の未来数値処理は4.3の所定Poolから既存Burst Kernelを実行し、物理Cut/Cookは7.2の用途別分担で処理する。Mainの必要入力準備・Unity状態適用・資源構築・GPU更新・公開を維持し、通常更新で未完了Workの強制完了・同期待ちを行わない。

- 7.1のTransactionがProvisionalまたは直接Finalから正負2 Final OwnerとLogicalFragmentを一体公開し、Geometry未完成でも子を受付けられる。Provisional・Final構築不能はSource退役とし、部分公開や旧物理の恒久採用で救済しない。質量・速度・自前B-repの包含・D6・Leaseは7.1／7.2の条件を満たす。

- 既存境界から判明した個別物理の継続不能は7.1のAbort／LogicalFragment単独退役へ送る。共通Player終了対象は4章に限り、通常退役と混同しない。

- Phase 4.1で7.5／T-076の軽量測定を行い、Owner単位の実行費用・処理量・scratch使用量から実行枠とメモリ予算を調整する。

- Operation未公開の親と仮表示領域への後続切断は、無関係な確定済み対象を止めず、状態・世代・先行仕事を変えずに見送られ、保存・再実行されない。Operation公開後はGeometry未完了でも公開済み子を受付け、Kernelだけ祖先Geometry Commitを待ち、古いジョブ結果で形状が巻き戻らない。

- Phase 4.52では先行準備なしの同期経路で移動中のNPCを切断し、姿勢固定から剛体破片への移行が成立する。Phase 4.65で未来用非同期ベイクの限定実装・品質と負荷の比較・採否決定を完了し、導入採用時だけPhase 4.72で先行成果物採用・未完成／成果物不採用時の通常経路・失効回収を統合する。導入見送り時は人形の先行準備による命中時負荷削減を受入条件にせず、4.5.2の通常同期経路と表示開始の許容を使う。数値差・対応範囲は4.5.2、最小確認は14章／21.2に従う。

- 代表的な連続切断シナリオで目標フレームレートとメモリ予算を満たす。

- 共用Cut Geometryはfiniteかつ参照有効で、各Topology Edgeに逆向きの2面、各Topology Vertexに一つの閉fanを持つことを入力準備時に一度検証する。Self-intersection、別Topologyの閉Component間のIntersection／Overlap、Internal／Nested／Coincident、全体反転を許容し、表示とStencilが同じGeometry、winding、Topologyを使用する。不合格入力は切断対象へ登録しない。切断出力は6.4の構成契約を継承し、Runtime出力検査を持たず、表面化した予期しない内部エラーは4章に従う。用途別Geometry、Runtime修復、符号証明、正規化、Winding上界、Count容量分割を持たない。仮Capは専用Stencil Byteの全8bitを排他利用できる構成だけで`S=(128+W) mod 256`と`S>128`を使用し、成立しない構成は4章の共通Player終了に従う。入力Physics Proxyの各ConvexはAsset／登録時Gateで自己交差、面反転、退化のない閉凸形状として検証し、切断出力は7.2の構築条件に従う。Compound内の別Convex同士のIntersection／Overlapは許容する。

- 相互に食い込む部品や凹形状、同Sideの離れた島を含んでも、通常切断の正常成功は正負それぞれ1論理子・1 Final Physics Ownerとする。島ごとの独立剛体化を行わず、一体運動・接触力共有・固定時の空中浮遊を許容する。同じCap境界・Sideで同一のVolume入力を持つCap仕事は、Geometry Unionなしで符号を保存して同じStencil Volume Groupへ入り、通常Colorでは`sum(W_i) > 0`として描画される。最後のColorではRenderFragmentごとのVolumeで描き、その誤描画は5.2の品質例外8とする（D-186）。正逆相殺による欠落は5.2の品質例外とする。

- 固定支持は7.1のOwner点集合と採用面による配分から導出する。T-072／T-074／T-086／T-091で受付No-op、固定／動的、引継ぎ・frame・非復活を確認し、公開・世代・資源寿命は既存規則を維持する。

- 切断対象として採用するGeometryが6章の共通入力契約に合格する。修正しない不合格入力は切断対象外にできる。

- 7.6のSource生存性・Active Transaction確認、Final Convexのrobust support、7.7の容量の順に受付ける。No-opと受付見送りは既存Counterで区別し、状態・世代・表示・物理・履歴・新規仕事を変更せず保存・再試行しない。正常成功時は7.1の正負2 Final Owner／Logical childとOperationを一体公開し、実在BoundaryだけをGeometry Commitで後着させる。

- 非空共用Geometryは同SideのFinal Physics Ownerへ所属し、Geometryが片側または両側空でも正負2物理子を残す。専用Convex、極小Rigidbody、反対側への所属探索・質量移送を要求しない。Rendererなしの子は通常のObject／Level寿命または7.9／7.10の任意GCへ従い、未計算・失敗を空にしない。

- Final質量は7.2に従って受付時の親Rigidbody質量を保存し、採用Convex集合から質量特性を近似する。Provisional一時massを流用せず、正負両子の質量特性を成立させられなければAbortとする。

- Synty／Poly Pro Universe入力と派生した共用Cut Geometry／Physics Proxyが公開Git履歴、公開CI Artifact、公開キャッシュへ含まれない。

- 飛翔斬撃波の到達時刻と候補列挙が再現可能で、静止対象では接触前の先行切断が安定して成功する。

- 19.1とT-034～T-036に従い、早期Latchした初期Segment、Raw候補のrunning maximum、採用方式のClose後評価、固定面・軸、現在区間だけの閉Sweep、有限寿命、19.1.8のSlashWave VFX、系譜Hit消費が成立する。各EstimatorのUI切替は生存Waveを変更しない。

- 刃側を先行させる広い角度の振りは切断でき、同じ刀向きの復路・峰側移動ではSlashが発生しない。

- 刀は発射可否・再準備・追跡状態によらず、19.1.11とT-040に従い地形、プロップ、NPCへ物理的に引っ掛からない。

- Quest右手ControllerのGrip PoseにOffsetを適用した刀姿勢とBladeFrameが整合し、追跡復帰時に誤Slashを生成しない。

- 予測が外れた場合も4.5.2の必要な現在Pose入力を準備して即時切断レンダラへ接続し、古い成果物をコミットしない。

- Quest 3Sの有線Quest Link環境で、頭部追従だけでなく剣、切断、破片を含む実アプリの両眼描画が原則90fpsを維持する。

- 任意の`SlashId`から候補検索、予測、各切断Task、検証、Commitまたは破棄までをEditorタイムライン上で追跡できる。

- NPCのCurrent／Future Poseは19.3の共通Table評価に従い、RootとAnimation入力は同じ対象時刻・有効計画へ整合する。4.70では採用した計画方式で介入への応答・費用・失効・資源寿命を確認する。有効区間外を確定予測として使用せず、現在状態の切断を妨げない。人形先行切断との接続は条件付き4.71／4.72に従う。

- Unity Editor更新時にプロジェクトを作り直さず、専用ブランチで固定テストとXRスモークテストを実行し、不合格なら旧固定版へ復帰できる。

- PoCでは選択的な片眼映像または静止画をFrameIdからTraceへ対応付けられ、録画停止時と比較して90fps性能判断を歪めない。


- 大型建物は共用Cut GeometryとCompound Physics Proxyで切断でき、点Anchorを失った所有単位は通常の動的物理へ進む。建物由来の動的な1→2分裂子には7.2.2のWorld D6を生成・維持できる。落下・横倒し・完全倒壊を引き続き許容し、D6の実際の拘束効果やSolver品質を合格条件にしない。一般外部Joint付き物体は製品切断対象に含めない。

- Player Body／Handはプロップ／破片へ物理Impulseを与えず、人工移動はLevel初期化時の固定PlayerLocomotionOccupancyに候補次姿勢がOverlapすれば要求全体をRejectする。物理所有者・切断・Commit・Fragment・GCへ追従せず、7.2.3の通行境界不一致と移動制限を許容する。HMDの実空間移動ではCamera位置を強制変更せず、Camera被り・内部視点・Near Planeおよび仮Capの表示品質はD-131と5.2に従う。刀とSlashWave Segmentによる切断Interactionは非接触化後も成立する。

7.9の任意分割・物理GCを実装する場合の追加受入条件は7.9.7とする。無効時の通常切断維持を確認し、有効時は成功分割後の通常再切断とGC退役の非復活を確認する。未実装・無効を垂直スライス不合格にしない。任意Phase 7.1の導入時の追加受入条件と消滅・容量非保証の許容は7.10に従い、未導入を不合格にしない。

## 17. Codexでの継続更新ルール

開発用検証（Test、Benchmark、Probe、Fixture、Harness、Capture、Trace）の保存形式・Schema・Codec・Golden・Manifest・Receipt・Report・Index・Profile・file構成・hash・version・Loader・実行／再開手順・反復回数・試験階層は、本書で製品Runtimeの外部形式またはSubsystem間の意味的互換契約として明示したもの、および21.17の簡易ロガーに明示する契約を除き実装詳細とする。DESIGNは成立させる能力を定め、検証方法だけの変更に改訂を要求しない。Runtimeの所有権・資源寿命・非待機・容量境界・安全な失敗・公開状態・Geometry／Physics契約は維持する。これには、所有権移転、対象ファイルを操作する権限、安全なteardown、完成済み出力の公開に必要な意味的条件を含む。その確認・受渡し方法と型・保存表現は実装詳細とし、既存ReceiptやOS lockそのものの維持を義務にしない。

任意の開発診断の記録は21.17へ統一し、別のLogger、診断Dump、独自の診断ファイル出力・収集機構を新設しない。既存の同用途処理は必要な記録を移すか削除し、互換ラッパー・二重記録・旧形式移行ツールを残さない。Profiler・Trace・Capture、調整用Preset・再生用Pose列、テスト／計測成果物は維持し、それらを名目とした任意診断の別経路は作らない。同じ実装内に同居する場合も用途で分け、再生・再計算に必要な入力と条件を削除しない。文字列定数や値変換だけの補助処理、画面・操作UIは維持でき、Unity／外部PackageのConsole改造や全Console出力の捕捉を要求しない。

この一本化で置換するログ規定と任意診断保存の旧仕様は、以下の履歴保持規則の例外として直接削除・置換し、経緯をGitへ委ねる。同じ責務のDecision／Test IDを継続し、旧仕様や互換経路を温存しない。Phase 0.55のDump等の完了記録は過去の事実として維持するが、継続使用する任意診断保存は一本化の対象とする。

10.2.3の参考Dataset更新・検証方法の変更と既存要求を確認する個別ケースの追加・差替えは通常作業とし、個別承認制にしない。必須対象・評価基準・Phase完了条件の拡大は同節の人間判断に従う。日々のSHAやケース一覧を本書へ転記しない。

2026-09-12の人間承認により、Phase 0／0.1／0.11の旧形式維持・旧Reader・同一手順での再生成を将来義務にしない。旧成果物が現行ツールで読めなくなることを許容し、現在の利用に必要な相関・用途と残る意味的契約を満たす範囲で、依存関係に基づき実装を変更・削除できる。既存実装・試験・成果物はそのまま利用でき、改訂だけを理由に再実行・再承認・作り直しを要求しない。0.12～0.14に残る旧形式維持要求にも同じ整理を適用する。

今回外す検証詳細と専用Decision・Test・Open Item・用語、およびD-127の8面化で撤去するPixel切断面評価と専用設定・観測・比較試験は、以下の履歴保持規則の例外として直接削除する。経緯はGitへ委ね、ID欠番を許容する。旧仕様の付録、廃止台帳、互換層、新しい完了証明は追加しない。

- 決定が変わった場合は既存行を消さず、状態を『廃止』にして代替決定IDを記録する。ただし、未実装のTemporary Stencil Capと表示／Stencil共用Geometry、その直接の入力・切断・試験・Benchmark契約、および撤去した旧小破片／Render―Convex品質分類／Shared Convex解決／GPU Debrisとその専用Decision・状態・ID・前処理・Trace・試験・Phase契約に限り、旧仕様を削除・置換してGit履歴だけに残してよい。また、人間承認済みの正負二集合化に伴い撤去する接続Graph、Attachment、間接支持、支持由来の表示状態・Cull、建物専用Safety Tetherと、その専用前処理・保存読込・Schema・Validator・設定・エラー・Fallback・Decision・用語・試験・Trace・Phase契約は、互換用の空表現や旧Readerを残さず削除してGit履歴だけに残す。さらにD-165で撤去する一般外部Jointの継承・付け替え・保護・予測と、その専用の試験・用語・Phase記述も削除してGit履歴へ残す。この撤去にProvisionalSeparationConstraintとBuildingWorldD6Constraintを含めない。D-166で撤去する動的Occupancy追従と退出系の状態・探索・専用ID・設定・容量・作業領域・用語・試験・Trace payload・Phase契約も、旧Readerや互換表現を残さず直接削除し、詳細はGit履歴へ委ねる。人間決定で撤去するNative PhysX比較Probeとその専用契約も、互換表現や旧Readerを残さず削除し、Git履歴へ委ねる。専用IDの欠番は許容し、廃止行や対応台帳を追加しない。この限定撤去を一般のCapture／Trace基盤、残る物理・資源寿命・Anchor・世代・Commitへ拡張せず、既存の永続IDを別意味へ再利用しない。

- D-167～D-170で置換する動的Front、形状追加状態、Vertex／Edgeと生成時刻、一価性・U字・自己交差、厚み・端点領域、VFX／Hit一致、専用Decision・Open Item・試験・Phase・現行Runtime／v2相関は削除し、Git履歴へ委ねる。欠番を許容しIDを別意味へ再利用しない。旧Slash Runtime・二重記録・変換器を追加せず、過去形式の扱いは本章の検証詳細規則に従う。一般のGeometry／Physics／世代／Commit／Capture基盤へ撤去範囲を広げない。

- D-172の撤去対象は廃止行や旧schema全文を残さず削除し、詳細はGit履歴へ委ねる。旧生成物への既存依存は10.2.2に従う。

- D-173で撤去する旧Runtime出力Validator・Operation公開前限定退役・Player終了前の完全診断保存保証と、これらに固有の試験は、廃止行・互換表現を残さず削除してGit履歴へ委ねる。共通Player終了境界の確認は14章に従う。

- D-175で撤去する長寿命の物理Group、旧物理恒久採用、Snapshot／Fault Frozen、専用状態・Reason・Trace・試験、未Commit Geometry貸出しの旧契約は直接削除し、Git履歴へ委ねる。互換enum・旧Reader・空record・migration・二重実装を作らず、ID欠番を許容して別意味へ再利用しない。一般の完成済みTrace／Capture形式は対象外とする。

- D-018の変更で撤去する未来予測専用の局所Sceneと専用契約・参照は直接削除し、経緯をGitへ委ねる。空Phase・互換表現を残さず欠番を許容し、通常Worldの物理と残る予測経路は維持する。

- D-017／D-083／D-130の実行先・Dispatch変更で置換する一律Job拘束と詳細な投入順は、旧本文・試験へ残さずGit履歴へ委ねる。同じ責務のIDを継続し、DAG・TaskId相関・世代・公開・資源寿命と他Subsystemの契約を維持する。

- Phase 0.5系列とSlashの内部構造・二重識別・状態名・未採用比較方式・重複記述・左手持ち対応、および19.1.8で置換するVFX形状・式・必須演出項目の撤去は、旧詳細を温存せずGitへ委ねる。同じ責務のDecision／Test IDは継続し、欠番は再利用しない。19.1.5.1の第一候補の説明・式・更新順序は本文に維持し、既存コードの一括改名や互換層を要求しない。

- Phase 0.9～1と関連するGeometry仕様の旧工程・所属／Commit条件、不要な共通型・Profile・固定識別表現・Boundary件数上限は直接削除し、経緯をGitへ委ねる。同じ責務のDecision／Test IDは継続し、既存実装の一括改名・再生成や互換層を要求しない。Topology、公開・資源寿命、0.91／0.94の比較実験は維持する。

- Phase 2.9／3.9の追加と7.2の局所退化許容で置換する旧記述は直接削除・短縮し、経緯はGitへ委ねる。旧仕様の付録や互換表現を追加しない。

- 7.3のcook品質許容により外す形状忠実性Gate・修復要求と曖昧な包含保証は、旧仕様の付録・廃止台帳・互換表現を残さず直接削除・短縮し、経緯はGitへ委ねる。Probeの観測結果や診断基準は遡って書き換えない。

- 今回撤去するオンラインPose Backendと方式比較、移動計画の具体手法・内部構造・将来方式の先行契約、および専用Decision・Open Item・用語・試験・Phase記述は直接削除・置換し、経緯をGitへ委ねる。同じ責務のIDは継続し、不要なIDは欠番とする。旧詳細・互換層を残さず、無関係な公開・資源寿命・切断・観測契約や研究原資料を変更しない。

- 7.10の導入で置換するPublished Vertexの永久非再利用、退役Policyの適用範囲、容量収束の旧記述は直接置換・短縮し、経緯はGitへ委ねる。同じ責務のIDを維持し、廃止全文・互換層を追加しない。

- 未決事項は結論、根拠、決定日を追記して決定事項へ移す。

- 技術検証は測定環境、再現手順、数値結果、スクリーンショット／Profiler参照を残す。

- 描画の検証とXR試験の追加はD-184に従う。未確認事項は未確認として残し、それを埋めることだけを理由にXR試験を追加しない。

- ロードマップのPhase完了条件を満たす前に次Phaseへ進む場合は、既知の負債として記録する。ただし、15章の独立分岐（0.21未完了中の他Phase進行と2.9／3.9の先行実装・未接続mergeを含む）・任意4.55・条件付き4.71／4.72の省略は未完了負債にせず、任意Phase 5.6／5.7も双方または片方を省略してPhase 6へ進める。任意Phase 7.1の未実装・延期・無効・省略も15章に従い未完了負債にしない。

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
| TemporaryRenderCapRecordSet | 当該フレームにCap板Batchへ投入するRecord集合。各RecordはRenderFragmentと1つの選択済み境界の組。Stencil Volumeの投入対象は5.2の選択・可視性・Color分類に従う。4.2の表示可能なPending CutとGeometry未Commit境界のうち、`SelectedTemporaryClipPlaneSet`に選ばれた境界から構成し、固定状態で省略しない。Frustum／Facing CullでRecordを選別する。Plane overflow時は選択結果に従って描画更新境界で再構築し、個別除去やcompactionは行わない。件数は固定側を含むBatch投入Cap Record数であり、Color／Depthを書いた板数ではない。Geometry Commit後は対応Recordを外す |
| TemporaryClipConstraintCandidateSet | 1個のRenderFragmentへ関係するGeometry未Commit切断半空間制約の集合。Pending Cutと公開済みOperationの採用面・祖先制約から構成し、固定状態で省略しない。Cap Record集合とは区別する。Ignored境界でRenderFragmentを集約した場合も、配下の論理枝ごとに候補・Side・選択状態の対応を保持する |
| SelectedTemporaryClipPlaneSet | 5.2に従いRenderFragmentごとに選択する最大8面の即時描画Plane集合。受付順・未Commit祖先優先のdependency-closed prefixを左右眼とColor／Depth／ShadowCaster／Stencil Volumeで共有し、Operation公開時は同じ受付位置と面を維持する |
| IgnoredTemporaryClipBoundarySet | Candidateのうちdependency-closedな最大8面prefixへ入らない後発Pending Cut／境界。5.2に従いPlane選択とclip制約から除外し、対応Stencil Volume、描画用Cap板を生成しない。Pending Cut、公開済みLogicalCutOperation、論理／物理状態、世代、背景Geometry／Convex処理は維持する |
| 共用Cut Geometry | 表示・実Cap・Stencil Volume・次回切断が参照する同一のGeometry正本。4.5.1に従い複数Geometry参照で構成してよい。閉鎖・edge／vertex manifold・局所winding整合済みTopologyを持ち、各世代で同じTriangle集合と向きを全用途へ公開する。Self-intersection、別Topologyの閉Component間のIntersection／Overlap、Internal／Nested／Coincident、全体反転、Runtimeの面積0 Triangleを許容する |
| Physics Proxy | 物理接触と高速切断のための低複雑度Convex／Compound。各Convexは閉凸契約を満たすが、同一Compound内の別Convex同士はOverlapしてよく、Strict SolidやConvex Boolean Unionを入力に要求しない |
| ProvisionalRigidbody | 7.1のTransaction内で予定正負子のpose／速度／外界Collisionを先行させる短命Actor。旧Cooked Geometryを共有し、7.2のOBB／等分近似はFinal質量正本にしない |
| ProvisionalSeparationConstraint | 同じ切断で生じたProvisional Siblingに使うUnity `ConfigurableJoint`。固定anchor-offset D6の構成、有限区間の品質許容と寿命は7.1に従う。Sibling Collision無効化およびBuildingWorldD6Constraintとは別の役割を持つ |
| ProvisionalCollisionResourceLease | 1つの旧Cooked Convex Geometryを複数のProvisional Shape Instanceが安全に共有する所有権Token。各GeometryをProvisional Shape Instanceへ結び付ける前に取得し、7.1に従って当該Leaseが保護する参照と必要な物理Stepの寿命を満たして一度だけ返す。Geometry自体は最後のLease返却前に破棄しない |
| FragmentRenderAnchor | Parent ActorからProvisional Actorを初めて分裂させる際、表示Fragmentの初期World poseと点速度を連続させるstableな基準Transform。Final handoffでは物理Actorを優先するためActor pose補正やCOM速度変換には使用せず、表示Geometryが新しい物理frameへ追従して瞬間移動することを許容する |
| FixedSupportAnchor | 物理所有単位に所属し、Fragment Physics Frame内で表すfiniteな論理点。個々のConvex／Cellとの対応・包含を要求せず、7.1の配分とOwner全体の固定判定に使う |
| IsBuildingDerived | 初期所有者の明示的な登録入力に基づく建物由来の継承boolean。サイズや名前からRuntimeで推定しない。7.2.2を正本とする |
| BuildingSplitDepth | 7.2.2の建物由来の非負分裂深さ。初期0、予定子DepthをProvisional D6と正式子で共有する。非建物は0を維持し、Final handoffで再加算しない |
| BuildingWorldD6Constraint | 7.2.2の建物由来・Anchorなしの動的分裂子が一つ持つWorld接続D6。Actor寿命で保持し、ProvisionalSeparationConstraintと分離する |
| PlayerLocomotionOccupancy | 7.2.3のLevel初期化時に確定するworld-space不変の低複雑度Primitive集合。人工移動の候補Player Root／予測HMD CapsuleのOverlap Rejectに使用し、物理所有者・切断・Commit・Fragment・GCへ追従しない |
| LogicalFragment | 再切断・退役の論理単位。正常切断はFinal Physicsと同時に正負各1子を公開し、各子が一Final Physics Ownerを専有する。Geometry未完成でも受付でき、同Sideの離れた島を含められる。7.9の非命中再編成・退役と7.10の退役でも架空Operationを作らない |
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
| RenderFragment | 正負Sideの共用Geometryを表示する単位。同Sideの離れた島を含められ、全島の個別列挙を要求しない。7.6に従い現在の物理所有者へ追従する。5.2のIgnored境界で分かれた論理枝は、同じ表示登録内で最初のIgnored境界より手前の形状として一つのRenderFragmentにまとめる |
| コミット後の追加空間分割 | 確定した1物理所有単位の共用面集合を横切らない一枚の平面で配分し、必要なConvex clip・cook後に通常物体2個へ再編成する7.9の任意処理。非命中で公開し、新しいGameplay切断面・実境界・Cut Operationを作らない |
| 物理GC | 共用Geometry全体の面集合が確定0の通常物理所有単位を、7.9の条件で丸ごと終了する任意のゲーム上の寿命Policy。Managed GC・個別Convex間引きではなく、追加分割から独立する |
| LogicalFragment GC | 生存LogicalFragment全体を表示・物理ごと退役させる7.10の任意寿命Policy。Published済みVBの回収条件は4.5.3に従い、Managed GCや確定空物理GCとは区別する |
| MaxIncompleteCutOperationCount | 全対象で同時に保持できる受付済み未完了切断数。対象LogicalFragmentへの一回の受付を1件とする既存容量設定の0より大きい固定値であり、片側空No-op、受付見送り、投機候補、7.9の任意分割・物理GCと7.10の退役は数えない |
| WorldPhysicsProfile | 世界重力を正本として保持し、Unity Physics、予測、解析運動、VFXへ同じ値を供給するバージョン付き設定 |
| Pending Two-Sided Shadow | 即時切断中だけ、開いた外殻の裏面をShadow Mapへ書いて断面キャップの遮蔽を近似する両面ShadowCaster経路 |
| Cap Bounds Polygon | 対象のローカルOBBと切断平面の交差から生成する有限な仮キャップ板。切り詰める前の初期断面は最大6頂点で、Colorの投影競合判定にも使う。他のSelected半空間で切り詰めた描画用Polygonは最大14頂点で、空の結果もあり得る（5.2、5.6） |
| Stencil Conflict Graph | 通常Colorで分離が必要な対象間の関係を表す論理モデル。全Graph、全Edge、特定の構築・彩色手順を要求しない |
| CapCompatibilityKey | 全World Cut Plane、Side／半空間を表すStencil共有互換Key。可視色、符号分類とWinding容量を含めない。D-183以降、Stencilの共有とColorの判定には使わない |
| Cap仕事 | 描画するRenderFragmentと選択済みの1つのCap境界（採用面とSide）の組。両眼で可視・非空のものだけがStencil Volume／Capを発行する（5.6） |
| Stencil Volume Group | 同じ表示登録・同じCap境界とSideで、Volumeへ実際に渡すGeometry範囲・配置Transform・符号付き平面が同一のCap仕事の集まり。Volumeを1Colorにつき1回だけ発行する。epsilonで同一視しない。通常Colorの単位であり、最後のColorではGroupを使わずRenderFragment単位でVolumeを発行する（5.6、D-186） |
| 最後のColor | `MaxStencilColors`の最後の1枠。通常Colorに入らないCap仕事をまとめ、RenderFragment単位で全Selected面によりclipしたVolumeと、残りの切り詰め済みCapで描く。残りがなければ発行しない。誤描画は5.2の品質例外8（D-185、D-186） |
| 初期断面 | Cap面だけでOBBを切った、他の面で切り詰める前の断面（最大6頂点）。描画用に保持したものを、Colorの投影競合判定に読み取り専用で使う（5.6） |
| Winding Count Stencil | 共用Cut GeometryのFront／Backで排他的に予約したStencil Byte全8bitを128へ初期化してIncrementWrap／DecrementWrapし、`S=(128+W) mod 256`のうち`S>128`だけを描画する方式。Saturateおよび部分Bit Counterは使用しない |
| Residual Stencil Support | Front／Back集計後に`S != 128`となる画面領域を表す論理概念。実Stencilから検出しない。Volume Groupの計数完了後の残留は初期断面が包み、その左右眼投影を通常Colorの保守的な投影重複判定に使う（D-183） |
| Cap Visibility Cull | 論理破片×切断面のCapRecordを5.6のFacing条件で判定し、Cap仕事単位でStencil彩色前に除外する処理 |
| SlashWave | 19.1の不変面・軸、一本Segment、AcceptedSpan、有限WaveLifetimeを持つ飛翔攻撃 |
| Stroke Begin／Slash Latch／Span Open・Close・Closed／Wave Expire | 開始Sample選択、現在時刻での公開、刀入力受付期間と終了、刀入力終了後の評価期間、Wave寿命終了。時間順と未Close時の意味は19.1.1 |
| Slash Latch／Frame／Span Candidate／Span Close Estimator | 19.1の交換可能な出力境界。方式・設定と一時状態の保持範囲は19.1.4 |
| RawSpanCandidate／AcceptedSpan | 前者は有効性付きの非単調な希望長、後者はWaveが受理するrunning maximum。非遡及は完了済み区間の非再評価を意味する |
| SlashWave Segment／SpanAxis／TravelAxis | 19.1のAからBへの一本前縁と、固定されたSpan方向・進行方向。軸の直交は要求しない |
| Span Guide Ray | 19.1.5.1の第一候補で使う、Span Open中のLive Emitter／剣先方向とClose後の固定Emitter／方向による半直線 |
| Current Adopted Physics Convex Set | 7.6と実Hitで共用する現在採用Convex集合。旧／暫定Convexを含められるが表示Geometryの外包を保証しない |
| SlashWave VFX | 19.1.8に従う、Slash面内の表示専用平面表現 |
| Candidate Flight Bounds／先行準備範囲 | 前者は有限Span包絡から導出可能な場合の保守範囲、後者は全Hit包含を保証しない有限な投機範囲。実Hit検索を制限しない |
| ObjectGeneration | Object全体のPrediction等に用いる単調増加の粗い変更識別。更新と採否は4.2、7.9、7.10、8章に従う |
| BaseObjectGeneration | 投機ジョブが入力としてスナップショットしたObjectGeneration |
| Commit | 有効な成果物を既存境界で整合した状態として公開する操作。採否は8章、物理・論理公開は7.1、Geometry公開は4.5.6に従う |
| BladeFrame | 刀Prefab内でBladeAxis、EdgeDirection、SideNormalと判定Sample Pointを定義するローカル座標系 |
| Edge Lead Score | 刀身軸方向を除いた運動と刃方向の内積。正なら刃が先行し、負なら峰が先行する |
| Future Event DAG | 未来の候補接触、姿勢予測、切断、Commitを依存関係で表した評価グラフ |
| Work Item／TaskId | Unity Job、外部Pool、I/O、GPU処理等を横断して追跡する論理作業単位と相関ID。C# Task型に限定しない |
| Animation Clip Catalog | 使用Clip、duration、Loop／Clamp等の再生規則と内容を識別する情報。19.3のTable評価で現在／未来の意味を揃える。保存形式・hash構成は実装詳細 |
| ExplicitAnimationState | ゲーム側が所有するClipと解決済みSource Time／Phase等の明示Animation入力。19.3に従い、Animator内部Stateから復元しない |
| ResolvedAnimationPoseInput | 対象時刻へ解決した明示Animation入力とTable・Rig骨対応・評価規則を識別できる不変入力。型・保持形状は実装詳細 |
| FutureAnimationPoseEvaluator | 19.3の骨Pose Tableを使うCurrent／Future共通評価。対象時刻を再進行せず、入力と現在Sceneを変更しない |
| Cut/Cook Profiling | 7.5の代表Fixtureによる軽量測定。結果を実行枠・scratchメモリ・同時未完了切断数の調整に使う |
| Unity Built-in 3D Physics | GameObject／Rigidbody系で使用するUnity内蔵NVIDIA PhysX統合。DOTSの`Unity Physics`パッケージとは別物 |
| Native採用Gate | D-086に定める、Native物理経路の部分置換を再検討する条件 |
| Confidence | 未来結果をDeterministic／Conditional／Speculativeに分類した信頼度 |
| Trace Event | 状態遷移、Taskライフサイクル、Commit結果を整数IDと時刻で表す軽量イベント |
| Flow Event | 投入元とUnity Job／外部Pool等の別スレッド実行をUnity Profiler内で結ぶ相関情報 |
| Flight Recorder | boundedな履歴を診断保存へ利用する観測機能。保持方式・保存形式は担当Phaseと17章に従う |
| Synthetic Watertight Test Fixture | プログラムまたは固定版Blenderスクリプトから決定論的に生成する閉Triangle Meshのテスト／Benchmark専用入力。製品AssetやライセンスAssetの派生物ではなく、Runtime同梱物、代表Asset合格条件にはしない |
| Boundary Loop | 片面または開放Meshで、1面だけに属するEdgeが形成する穴の輪郭 |
| RenderCutTopologyMap | 共用Cut Geometryのposed positionから独立したTopology対応・系譜を表す情報。属性seamと別Topologyを区別し、6.2／6.4の位置・交点共有とContour接続に使う。内部表現と識別方法は実装詳細とする |
| Topological Watertight | Boundary Edgeがなく各Edgeが規定数のFaceへ接続する閉Topology。自己交差のない3D Solidまでは保証しない |
| Geometrically Valid Solid | Topological Watertightに加え、面向きが整合し、非隣接Faceの自己交差、面反転、退化がなく、内外と体積を一意に扱える形状 |

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

2026-09-18の人間による採用判断で、SpanAxisをEmitter chord方向からTravelAxisとの150°固定へ変更する。初期Span長はchord長を維持する。Guide、Close後の成長、候補の採否規則は変更しない。実機記録45 Latchの比較では急増が減ったが、Close後の成長は残り、暴走解消や体感改善の保証とはしない。

```text
TravelAxis T = D_B
side = sign(cross2_N(T, E_L - E_B))
SpanAxis   S = normalize(cos(150°) * T + side * sin(150°) * cross(N, T))
```

`S`と`T`は平面内で150°をなし、chordの示す進行側を選ぶ。`E_L - E_B`が正規化不能、`D_B`が平面へ射影不能、sideを判別できない等ではFrameを無効とし、別軸へのfallbackは追加しない。

Wave速度を正の有限定数`c`とし、Latch後のA点を次で定める。

```text
A(t) = E_B + c * (t - t_L) * T     for t >= t_L
```

Latch時は`A(t_L) = E_B`、初期`AcceptedSpan = length(E_L - E_B)`、`B(t_L) = E_B + S * AcceptedSpan`とする。初期Segmentの長さはEmitter chord長だが、方向を変えたため終端は一般に`E_L`と一致しない。最終Spanは確定せず、初期値を交点式へ依存させない。

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
3. 祖先判定は、7.1.2の生成元関係と、そのOperationの親IDを必要な段数だけたどる。
4. Slash専用の系譜Cache、子公開通知、切断側からSlashへの消費状態伝播を追加しない。
5. 祖先に消費済みFragmentがなければ、現在候補をSlash側集合へ追加してから、既存の切断受付へHitを一度だけ渡す。
6. 切断受付、片側空No-op、受付上限見送り、未公開親による受付拒否等の結果にかかわらず、同じSlashでは当該系譜を再試行しない。
7. 消費済みFragmentと祖先関係を持たない別の現在Fragmentは、同じSlashからそれぞれ独立に命中できる。
8. 消費済みFragmentから後に生成された直接・間接子は、既存履歴の祖先判定によって同じSlashから再命中しない。
9. 別`SlashId`は同じ現在LogicalFragmentへ通常どおり命中できる。
10. 集合はSlash終了時に回収し、LogicalFragmentへ恒久的なSlash履歴を追加しない。

生成元照会のO(1)条件を、祖先探索全体やSnapshot構築の定時間保証へ広げない。入力変更時のSnapshot再構築と現在の切断状態の確認は維持し、履歴の削除・圧縮・GCや汎用Cache、第二の台帳を追加しない。

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

投機費用は既存候補数上限と4.4の有限投入・未投入候補取消で扱い、命中確率は受付前Filterにだけ使う。予測到達時刻は維持するがDeadline sortを要求しない。投入済みWorkの継続・失効回収と命中後の新規投入先は4.4に従う。4.1の性能曲線をSlash速度・寿命・Span・候補数へ当てはめる判断は4.53で行う。

#### 19.1.11 Quest Grip Poseと片刃方向Gate

PoC・初期製品の刀は1本とし、刀入力には右手Controllerのみを使用する。左手持ち・持ち手切替・二刀流は対象外とする。右手ControllerのOpenXR `grip pose`から位置・回転・Tracking Stateを取得し、刀Prefabの単一の`GripToKatanaOffset`を適用して刀姿勢を決める。`aim pose`は刀姿勢の正本に使用しない。表示モデル・任意の物理グリップアタッチメント差はこのOffsetで調整する。具体値は暫定設定・開発時調整で決める。この限定は刀入力だけに適用し、左手Controllerの非刀用途は変更しない。右手入力の無効時は本節の追跡規則に従い、左手への自動代替を行わない。

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

観測は既存Observability／Trace、Unity Profiler、Sandbox画面・Consoleを使い、任意の開発診断の記録は17章／21.17へ統一する。調整・再評価用Presetと再生用Pose列の保持・保存形式、および内部値の表現は実装詳細とし、旧形式互換や専用保存契約を要求しない。診断出力を製品状態・Commit・Recovery・Trace完全性の正本にせず、生存Waveが実際に使用する確定済み設定の不変性は維持する。

### 19.2 ゲーム専用の遅延評価・投機実行器

未来評価は不変Snapshotから既存DAGを構成し、Readyな数値Workを4.3／4.4へ渡す。必要な対象時刻・予測前提・信頼度・世代・成果物を保持し、命中時に既存条件で採否する。実行順位のためだけの推定費用・締切・順位Metadataを重複保持しない。

```text
Unity現在世界 -> 不変Snapshot -> 既存DAG -> Ready Workを所定の実行先へ投入
             -> 世代・前提検証 -> Commit
```

| 信頼度 | 主な対象 | コミット条件 |
| --- | --- | --- |
| Deterministic | 静止物、確定済み切断面から作る幾何成果物 | `SlashHitConfirmed`、Slash／SlashFrame、BaseObjectGenerationの一致 |
| Conditional | 既知Animation、単純運動、確定済みMobPlan | Deterministic条件に加え、Animation入力・Identity／PlanGeneration／予測前提の一致 |
| Speculative | 直接予測Gateを通った自由飛行剛体で、命中までに前提が崩れ得るもの | Deterministic条件に加え、実接触時の姿勢・Physics状態照合に合格 |

実行境界は4.3に従う。VP出力・転送・Geometry Commitは4.5.6に従う。物理適用は表示Geometryの完成を待たず7.1に従って進める。

### 19.3 未来姿勢の求め方

表示切断の先行計算も4.5.6のCommitted入力を使い、未Commit祖先成果物を入力にしない。入力準備は4.5.2に従い、Skinned対象の先行計算は非同期ベイク導入採用時に不変Rig Poseから共通VP入力を生成して共用Geometry切断へ接続する。同じRig Poseから骨Physics Proxyを姿勢化し、表示頂点ベイクだけの待ちを物理へ追加しない。準備中は現在Sceneと表示を維持し、4.5.6の正負直接Index出力・転送まで準備して、19.5の既存採用条件を満たすVP・成果物を命中時に再利用する。Final物理所属の確定を転送条件にせず、非スキニングまたは有効な準備済み入力では不要な工程を省く。

| 対象状態 | 予測方法 |
| --- | --- |
| 静止／姿勢固定 | 4.53で現在姿勢を使って先行計算 |
| 自由飛行・単純重力 | 4.54で`DirectRigidPredictionEligibilityGate`を満たす剛体だけ固定Unity／PhysX版の固定刻みに準拠したO(1)直接予測を使用する。Gate対象外・前提不一致は4.51へ進む。任意4.55のLocal Planeリベースは19.5.1の独立した採用Gateを使う |
| 既知またはMobPlanで確定したAnimation | 対象時刻へ解決した明示Animation入力から、Current／Future共通の骨Pose Table評価でRig Poseを生成 |
| 接触・転動 | 接触・転動の未来運動を先読みせず、4.51の現在状態経路で処理する |

`DirectRigidPredictionEligibilityGate`は開始Snapshotだけから判定する内部boolean受付条件とする。動的かつSleep中でなく、`useGravity=true`、damping 0、Rigidbody Constraintsなし、既知のシステム所有Constraintなし、Fixed Step境界、`WorldPhysicsProfile`一致、既知Contactなし、予約済みForce／Torque・スクリプト駆動・Animation駆動・ユーザー介入なしであり、予測区間に速度Clampが適用されず、既存候補情報から衝突可能性が判明していない場合だけ受理する。未来区間の完全な無衝突証明は要求せず、受付後に衝突または介入が判明した成果物は既存の実命中検証で破棄する。Gate結果用の状態、enum、Profile、Proof、永続ArtifactまたはTrace Eventは追加しない。

一般外部Jointは7.2.2の製品入力契約で除外するため、Gateで任意Jointを列挙しない。点Anchorで固定された対象、接触・転動中の対象と、既知のBuildingWorldD6Constraint／ProvisionalSeparationConstraint付き対象は直接予測から外す。未来運動の予測が必要なGate対象外の剛体は4.51へ進む。静止／姿勢固定として先行計算できる対象は、点Anchorの有無によらず4.53に従う。

製品RuntimeのNPC Current／Future Pose評価は、対象Rigへリターゲット済みの骨Pose Tableを用いる。ゲーム側が対象時刻へ解決した明示Animation入力から共通の評価処理でRig Poseを生成し、現在表示ではBone Transformへ適用し、未来評価では数値Poseとして利用する。オンラインPlayable／Animatorを代替のPose生成経路にしない。

入力は、Global FixedStepIdに対応する対象時刻、使用Clipと解決済みのSource TimeまたはPhase、Table・Rigの骨対応と評価規則を識別できるものとする。同じ有効入力・Table・評価規則からは要求順によらず同じPoseを生成し、評価器自身はPlaybackRateや現在時刻から再び時刻を進めず、Clipを選び直さず、入力と現在Sceneを変更しない。型名・field配置・Buffer形式は実装詳細とする。

Clipのduration、Loop／Clamp、終端とサンプル間補間の意味をTable評価側で一意にし、現在と未来で共有する。Loop境界を逆向きに解釈せず、Clamp終端は最終Poseを保持する。初期は単一Clip・無ブレンドとし、明示されたClip切替を現在／未来へ同じ時刻で反映する。切替先・切替時刻の計画方法を本節では決めない。未知Clip、非finiteな入力、不正な参照・骨対応・入力Identity不一致からPoseを公開しない。

HumanoidリターゲットとTable生成・検証には、Editor／オフラインのPlayable・Animatorを利用できる。Tableのサンプル設定・圧縮・保存形式・対象骨は、必要な品質と骨階層・skinning・骨Physics Proxyの対応を保つ範囲で実装時に決める。通常描画はSkinnedMeshRendererに残し、姿勢のTable化を頂点の事前ベイクやRuntime頂点スキニング方式の採用と同一視しない。

初期予測対象NPCではLook、腕／Foot IK、左右反転等のオンラインPose補正を現在／未来の双方で使わない。導入は別の設計変更とし、将来用Schemaを先行実装しない。プレイヤーの実測Controller姿勢・腕IKはこの制限の対象外とする。

命中時の実Bone Pose Snapshotと、Root姿勢・計画世代・入力Identity・実命中の採用検証を維持する。計画不在・失効・予測不一致・先行準備未完成の場合は、§4.5.2の現在Pose同期経路へ接続する。Table採用を理由に実Pose照合を省略したり、未来Poseのために現在Sceneを書き換えたりしない。未来用非同期ベイクの採否と接続条件は§4.5.2に従う。

Table欠落・不正や入力不成立時は不正Poseを公開せず、既存の有効な現在表示を保持する。オンライン代替Backendへ戻さず、現在Poseの保持を失効した未来計画の有効性と同一視しない。数値評価の同期／Batch／外部実行は4.3の境界内で選び、現在骨への適用と区別する。初期の全面非同期化は要求しない。

自由飛行のO(1)固定刻み直接予測は外部Probeで技術成立を確認済みとする。T-017では本体のSnapshot、WorldPhysicsProfile、FixedStep、重心／Actor原点および回転処理との統合回帰を確認する。Probeのケース数、誤差値、性能倍率、暫定許容値は本体の製品保証へ転記しない。

### 19.5 スケジューリングとCommit

表示出力には4.5.6の直接配置・必要転送と公開条件を適用し、物理適用とは分離する。命中済み表示仕上げと投機準備は4.3／4.4の用途別投入で進め、必要なMetadataを物理から隠さない。有効な準備済み最終配置は再転送せず採用し、以下の実命中・面・世代条件を維持する。

投入・容量・Main予算は4.4に従い、未完了依存は既存DAGで投入前に解決する。命中確率は受付前Filterに使い、一時描画費用は既存Profilerで観測する。

投機ジョブは`SlashId`、対象`LogicalFragmentRef`、確定した`SlashFrame`、`ObjectId`、`BaseObjectGeneration`、共用Geometry・物理・MobPlanの必要なGeneration、Animation入力Identity、予測到達時刻へ既存入力Snapshotで相関する。各成果物への重複field保持は要求しない。Commitには対応するSlashWave Segment Sweepの`SlashHitConfirmed`を必須とし、識別子、SourceSlashPlane、世代、予測前提のいずれかが一致しない結果は適用せず回収する。19.5.1の対象剛体だけはWorld姿勢一致をLocal Plane採用Gateへ置き換え、その操作に確定した面・基底frameと一致するGeometryだけを使用する。これにより、Candidate Flight Boundsへ入っただけの空振り候補や、古い非同期結果が新しい切断状態を上書きすることを防ぐ。

上記は通常の命中切断成果物のCommit条件である。7.9の確定後の任意分割・退役と7.10の退役は各節の非命中公開条件に従い、公開後は旧所有構成向けの予測成果物を拒否する。任意候補の準備・見送りだけでは通常予測を失効させない。

#### 19.5.1 剛体切断成果物の実姿勢リベース

本節は任意Phase 4.55で実装・比較・採否を判断する。未実装・延期・不採用時は現在状態経路を使い、4.54の完了条件にしない。

**目的と初期Scope。** 物体を予測World姿勢へ移動せず、予測時に仮決定した切断前物体ローカルの面と、その面から先行生成したGeometryを実姿勢に取り付ける。姿勢誤差を消す方式ではない。面接線方向の並進差は吸収しやすいが、法線方向の差と回転差は切断位置／向きへ残る。同一Slash内の対象ごとの面差と、近似検査で検出しきれない局所的な切断縁／VFX差を許容する。

初期対象は、単一RigidBody frameで全対象Geometryを表せる、点Anchorなし・接触なし・システム所有Constraintなしの自由飛行剛体だけとする。Geometry／local shape pose／scale／Topologyが予測開始から命中まで不変であり、一定重力、linear／angular dampingなし、外力／外部Torque介入なし、FixedStep境界、予測Horizon 0.5秒以下を要求する。点Anchor付き建物、BuildingWorldD6Constraint／ProvisionalSeparationConstraint付き対象、Skinned Mesh、骨相対Pose変化、再切断／Mesh世代変更、可変scale、shear、接触／転動は対象外とする。scaleは事前にGeometryへ固定し、予測・実姿勢の写像は正規直交回転＋並進だけとする。D-046のMobPlan／骨Pose検証を緩和しない。既存の接触／介入履歴と予測前提Snapshotで条件を確認できなければ対象外とし、初期版で完全な無衝突証明器を新設しない。

**面とframeの正本。** 面は正規化法線nと距離dの4係数で表し、符号は `dot(n,x)+d=0`、Positive／NegativeはSource面からの向きを保つ。符号を任意反転して子IDを交換しない。切断前のParentLogicalFragmentLocalId、BaseObjectGenerationと固定Geometry-to-Physics-frame写像をLocal Planeのframe identityとする。PredictedObjectPose／ActualObjectPoseはこの同じframeからWorldへの変換であり、重心位置をMesh原点として代用しない。

| データ | 正本・寿命 |
| --- | --- |
| SourceSlashPlane | Latch済みSlashFrameの不変World面。有限SlashWave Segment Sweep、実Hit、Slash飛翔VFX、攻撃方向、因果Traceに使用 |
| PredictedObjectLocalCutPlane | 命中前の予測Poseから求める仮Local Plane。Geometry Workの完成とは独立した小さいimmutable Descriptorとして先に公開する |
| SelectedObjectLocalCutPlane | 命中時に操作単位で一度だけ確定する面。予測面採用または実命中面Fallbackのどちらかであり、全子と後着成果物の共通入力 |
| CommittedCutPlane | 採用時はActualObjectPoseから再構成するWorld面。以後は親から子へ引き継いだLocal Planeを各Actorの現行frameへ写したもの。命中時World位置へ固定しない |

列ベクトルの同次変換をT、planeの4係数をπとすると、`πlocal = transpose(Tpredicted) * πsource`、`πworld = inverseTranspose(Tactual) * πlocal`とする。点の変換でplane normalを処理せず、nとdを同じ正の長さで正規化する。実命中Fallbackは同じ規則でTactualからπsourceをlocalへ変換する。有限性、法線非zero、既存Geometry Kernelの数値／frame前提を検証する。選択後に係数を再推定・再量子化せず、子frameへの必要な座標変換だけを系譜から行う。World座標で生成した予測成果物は入力frameへ安全に還元できる場合だけ利用し、単にActor Transformへ予測位置を代入しない。

**命中時の一回選択。** 予測面DescriptorはSlashId／SourceSlashFrame、ObjectId／BaseObjectGeneration、ParentLogicalFragmentLocalId、Mesh／Physics／Topology世代、予測基底・対象FixedStepId、WorldPhysicsProfileとRebase Profileのidentity、固定local frame写像を保持する。実命中時の検証順は対象Scope、Descriptor有無、identity／世代／Step／前提、数値／幾何条件、視覚Gateとし、不一致を姿勢リベースで隠さない。初期版では予測対象FixedStepIdと実命中SnapshotのStepを一致させ、連続時刻の外挿で代用しない。Mesh／Collider WorkのReady、成功／失敗、残り時間は面の採否入力へ含めない。

1. SourceSlashPlaneに属する実SlashWave Segment SweepのSlashHitConfirmedを必須とし、候補列挙だけでは面や切断を公開しない。
2. ObjectGeneration更新前の実Physics poseと世代を同じStepのSnapshotとして取得する。Mesh表示の補間Transformを物理Snapshotの代わりにしない。
3. Gate合格ならPredictedObjectLocalCutPlane、不在／不合格なら実命中から求めたLocal PlaneをSelectedObjectLocalCutPlaneとする。実姿勢からも有効面を構築不能な場合は通常の切断失敗・回収経路を使い、不正Planeを公開しない。
4. Pending Cut登録・世代更新・表示変更より前に、7.6のSource生存性とActive Transactionなしを確認してから、同じ実SnapshotとSelectedObjectLocalCutPlaneでFinal Physics Convex集合をrobust support分類する。片側空No-opまたは`MaxIncompleteCutOperationCount`到達時は面Descriptorをゲーム状態へ公開せず、CutOperationId、LogicalCutOperation、子、境界、Pending仕事を作らない。この判定にもMesh／Collider WorkのReadyを使用しない。
5. 受付を通過した場合だけ、正のCutOperationIdを持つ同じ切断のPending Cut登録・基底から次世代への遷移・Local Plane確定を原子的に行う。この時点ではLogicalCutOperation、論理子、CutBoundaryRecordを公開しない。Operation公開前のTemporary clip／Capと局所VFX、点Anchor配分と所有者単位の固定／動的の導出、Provisional配分・OBB質量近似・分離Constraintは同じ面を読む。最初にSource面で表示して後から予測面へ切り替える二段階公開を禁止する。
6. 選択Descriptorに対応する投入済みWorkを4.4に従って継続し、未投入・後続Workは命中後の用途でurgent／Geometry Poolへ投入する。同じ計算を競争のため再発行せず、面採用のための強制CompleteやMainでの同期切断・cookを行わない。不採用面の投入済みWorkは完了後に回収する。

基底世代は命中したPending Cut自身による既知の次世代への更新と結び付けて保持し、単に現在世代と旧BaseObjectGenerationが異なることだけで対応成果物を破棄しない。受付後の通常Transaction／Geometryは8章の局所authorityで照合し、別LogicalFragmentの更新や子孫切断だけで祖先GeometryをRejectしない。外部authority喪失はStale回収する。Snapshot取得からPending Cut公開までに前提が変化した場合は古い選択を公開せず通常の再評価へ送る。Pending Cut公開後はPlane採否を再実行せず、後にLogicalCutOperationを公開する場合も同じ面を継承する。後着Work失敗、予算超過、既存境界で判明した自前B-repの構築不成立、View変化でも元Source面へ切り替えず、Geometryは確定面のまま既存経路で進め、物理を成立させられなければ7.1のAbortへ進む。

**boundedな初期視覚Gate。** `RigidCutRebaseProfileV1`はversion／content identity、有限・非負の`MaxPlaneNormalAngleRadians`（π未満）、`MaxPlaneFieldErrorMeters`、`MaxProxyPointPixelError`を持つ。測定条件は判定時の左右眼View Projectionと各viewport pixel寸法へ固定する。未設定／不正Profile、片眼情報欠落ではリベースを採用しない。値の校正はO-047で行い、Probeの1 mm／0.5度／5 mmや画面幅1%を自動採用しない。

Mesh Workとは独立した基底Geometryの保守的local Boundsの8 cornerを実poseへ写した固定8点だけを使用する。正規化したSource面(nS,dS)と候補World面(nC,dC)について、法線角度と`maxCorner(abs(dot(nC-nS,p)+dC-dS))`を検査する。後者はBounds内のsigned plane field差の上限であり、全切断線の距離上限ではない。同じ各cornerから両面への直交投影点を作り、左右眼それぞれで対応2点のpixel距離を比較し、その最大をProxyPointPixelErrorとする。生成済みCap／Mesh頂点をsample選択へ使用せず、Work Readyによって点集合を変更しない。非finite、Near Plane上／背面を含む投影不能、無効Boundsは不採用とし、画面外の点をclampして誤差を小さくしない。全条件が閾値以下のときだけ採用する。

これは8点の近似であり、真の切断線／シルエット最大pixel誤差、注視追跡、VFX全頂点比較、接線付近のTopology一致を保証しない。多少の未検出差を許容し、全Contour生成や全Triangle走査を採用Gateへ追加しない。World上限も併用し、遠距離で見えにくいことだけを理由に無制限の面差を許さない。有限Sweepの命中対象を増やさず、SlashHitConfirmedを取消・偽装せず、同一Slashの別対象にもリベース面を伝播しない。

**後続物理と表示。** 採用するのはGeometryと操作Local Planeであり、予測速度、予測World COM、支持・外界接触・安全判定の結果ではない。実Actorの最新pose／速度と7.2の親質量Snapshot・分離Impulse一回規則を使う。選択時のActualObjectPoseへ後日Actorを巻き戻さず、各子のPhysics Frameに保持したGeometryをその時点のActorへ取り付ける。Final用の自前B-repは7.2の由来Convex内に収まる構築条件とPhysics Frame対応に従い、完成出力の包含再走査は要求しない。攻撃Impulseの意味方向はSource Slash、分離Constraint／幾何Offsetの法線は採用面として区別し、両者が同じ法線であると仮定しない。必要な点配分や物理公開条件が未確定の間は既存Work依存で物理適用を待ち、成立不能と確定した場合は7.1のAbortへ進む。リベースを成立させるためにActorのposeを補正したり固定側へImpulseを加えたりしない。cookに由来する形状・接触差は7.3に従う。Render補間は既存Actor従属の範囲だけで行い、表示―物理誤差蓄積／すり合わせ状態を復活させない。

## 20. モブ未来計画とAI LOD

### 20.1 方針と採用判断

モブの軌道生成、Animation選択・時刻進行、計画の保持・更新、AI LODの具体方式は、先行独立研究の今後の結果を踏まえて本体導入時に決定する。現在のDESIGNは特定の移動Kernel、計画アルゴリズム、データ構造、補充方式を既定方式にしない。未採用方式の仮の製品実装や交換基盤を要求しない。

移動とAnimationのどちらを先に決めるか、共同計画にするか、実装をいくつの処理へ分けるかは未決とする。ゲーム側がRoot姿勢とAnimation入力の正本を所有し、同じ対象時刻の両者を整合させる。オンラインAnimator内部Stateの読戻しとRootの二重更新を行わない。オフライン取得したRoot Motionを計画データに利用することは禁止せず、その採用も本改訂では決めない。

Mob計画固有の起動時全量Native確保・Runtime成長禁止・無割当保証は要求せず、Managed allocationとそれに伴うGC停止を許容する。有限容量・フレーム予算は4.4に従い、他SubsystemのNative制約、Trace Writer、VP資源契約は変更しない。

### 20.2 計画の公開契約

有効なMobPlanから、Global FixedStepIdに対応する対象時刻のRoot姿勢と§19.3のPose Table評価入力を取得できるものとする。計画の対象、世代・前提、有効区間を識別し、異なる時刻・世代のRootとAnimation入力を混用しない。現在の適用と未来の参照は同じ有効な計画内容・時刻の意味に従う。未来評価のために現在Sceneを進めたり巻き戻したりしない。

公開済みの同じ計画を同じ時刻で参照した結果の一貫性は要求するが、計画を同じSeedから作り直した際の経路・探索順・hashの完全一致は共通要件にしない。計画の格納形式、Sample間隔、Clip列・区間の表現、時刻解決と補間の内部処理は、上記の意味を満たす採用方式で決める。共有ClipのPose Tableと、全Mobの未来全時刻の骨Pose保存は区別し、未来骨Poseは必要な候補・時刻について評価する。

### 20.3 更新・公開・資源寿命

計画更新は必要な整合単位で完成内容を公開し、読者へ未完成な内容や新旧が矛盾したRoot／Animation入力を見せない。使用中入力を保持し、失効した計画の結果を現在の計画へ適用せず、読者・Workの寿命後に安全に回収する。具体的な公開単位、Buffer、同期手段は固定せず、既存の非同期制御へ接続する。

計画の未準備・失効・有効区間外を確定した未来姿勢として扱わない。通常経路で計画完成をMain Threadから待たず、無制限再試行・全群衆の強制同期再計算で補わない。こうした場合の現在の移動継続・停止等は採用方式で定め、不正な姿勢・参照を公開せず、現在状態からの切断を妨げない。

### 20.4 負荷と介入への応答

プレイヤーが介入しやすい対象の応答を優先し、猶予のある対象では計画の再利用により費用を抑える。具体的なTier構成、判定指標、計画期間、更新頻度、枯渇時の動作は研究と本体実測から決定する。品質・更新頻度の切替だけでRootとAnimationの意味を変更しない。

計画Workは4.3のBackground Poolと4.4の有限投入・Main予算へ接続する。専用Schedulerや同期救済を追加せず、具体的な負荷・メモリ・再利用状況は既存Profiler／Traceで確認する。

多少のMob重なり、遠方Mobの短時間停止、単一Clip切替のPose popという既存の品質許容は維持する。現在／未来の整合を、完全な衝突回避や速度連続性の新しい保証へ読み替えず、特定の縮退アルゴリズムも固定しない。

### 20.5 切断投機との統合

Phase 4.65で未来用非同期ベイクを採用した場合だけ、MobPlanの有効な対象時刻を§19.3のRig Poseへ解決し、§4.5.2の未来VP入力準備と骨Physics Proxy姿勢化へ接続する。Phase 4.71は準備要求・結果取得・失効・回収、Phase 4.72は切断成果物と実命中の統合を担当する。既存TaskId、入力Snapshot、世代・Identityへ相関し、実命中時に既存のPose・面・前提・authorityを検証する。

未準備・失効・不採用時は§4.5.2の現在Pose同期経路へ進み、未来準備のために実命中切断を止めない。4.65不採用時は4.71／4.72を省略でき、4.52の現在Pose切断と4.70の計画単体は維持する。具体的な計画方式を、この条件付き接続のために先取りしない。

### 20.6 無効化と観測

プレイヤー介入、経路・Intent等の前提変更、対象切断・退役によって計画が無効になった場合は、PlanGeneration等の既存識別で旧計画と依存Pose・切断成果物を失効させる。計画済み内容の時間進行と、計画そのものの変更を区別する。無効化・再計画の粒度と検出方法は採用方式で決定し、特定のNavMeshや予約機構を必須にしない。

MobId、PlanGeneration、Work・Slashとの因果相関を既存Traceへ残す。未投入仕事の取消、投入済み仕事の完了後不採用・回収と使用中資源の保護は§4.4／§8に従う。研究方式固有のEvent・Counter・保存Schemaを先行追加しない。

計画済みAnimationの時間進行だけではPlanGenerationを更新せず、計画内容の変更は既存PlanGenerationで扱う。第二のruntime Animation Generationを追加しない。単なるPlayer Physics Contactを必須の無効化要因にせず、ゲーム側で観測できる介入を扱う。

## 21. 観測・トレース設計

Phase 0／0.1は完了済みの観測・非同期Capture能力として扱う。Phase 0.11は21.15の短時間NVENC確認、0.12～0.14は21.16の可変長Trace導入を担当する。保存形式、旧読込み、検証方法の扱いは17章に従い、過去Phaseを今回の改訂だけで再実行・再承認しない。

### 21.1 目的と責務分離

再現困難な競合、世代不一致、予測の無効化、古い成果物のCommitを調査できるよう、性能はProfiler、状態と因果関係はTrace、描画内容はCaptureで確認し、時刻・フレーム・対象IDで突き合わせる。映像だけでゲーム状態や処理の因果関係を判定しない。任意の開発診断の記録は21.17へ統一し、既存Profiler・Trace・Captureとは責務を分ける。

7.9の任意処理は既存Task lifecycleと少数Counterで通常切断と区別する。所有者再編成全体を復元する専用Trace束を要求せず、Operationの記録は実際の切断履歴を表す。

### 21.2 Unity Profiler計測

処理種類ごとの軽量なProfiler計測を使い、IDをMarker名やホットパスの文字列へ埋め込まない。Work Itemの投入、Unity Job／外部Poolでの実行、完了、CommitをFlowで関連付け、スレッドをまたぐ依存と各処理の費用を確認できるようにする。具体的なMarker名、集計・保存方法は実装詳細とし、実行構成とCPU状態の診断は14章に従う。

Pose Tableは少数の採用Rig／Clipについて、Table総容量・初期化・数値評価・現在骨への適用費用を本体の対象条件で既存Profilerにより確認する。Bake・補間品質はT-018で扱い、Probeの固定サンプル設定・全Clip・比較Backend・試験matrixを製品保証へ転記しない。

未来用非同期ベイクはPhase 4.65の限定実装で代表的な複数候補の同時要求を同期経路と比較し、Pose準備、Schedule／完了回収、AoS処理を含むMain Thread負荷と、Worker費用・結果到着時間を分けて確認する。既存Profilerと小さい比較記録を使い、4.5.2の品質とMain Thread負荷削減の結果から人間が導入の採否を決める。効果がなければ同期SkinnedMeshRenderer.BakeMesh維持を正常な完了結果とし、本体接続を要求しない。採用時は対応範囲・実装構成を決めて本体へ接続し、4.72で転送等の残る費用と先行切断完了率・採用状況を確認する。比較と採否決定を省いて4.65完了とせず、Phase 4.52の基本動作はその結果待ちにしない。固定高速化率、全候補の翌フレーム完成、大規模試験matrix、新しい計測schemaは要求しない。7.5のCollider用Physics.BakeMeshの測定とは分ける。

提案書が引用する外部の非同期スキニング参考測定は、原資料を本リポジトリでは未検証の参考情報として扱う。本体への導入判断はPhase 4.65の比較結果に基づき、外部測定のCPU・構成・数値・実装例を成立条件や必須Fixtureにしない。

### 21.3 Traceの相関と公開

Slash、LogicalFragment／Object、MobPlan、Work Itemの生存期間と世代、Schedule・完了・採否・Commit・破棄を既存IDと時刻・Frame／FixedStepで関連付ける。TaskIdはWork Itemを表し、Fragment識別子へ流用しない。未投入取消と、Unity Job／外部Poolへ投入済みWorkの完了後不採用・回収を区別する。

CutOperationId、LogicalFragmentLocalId、CutBoundaryLocalIdは0を未設定に予約した正の32bit intとし、ObjectIdの生存期間全体で種別ごとに一意かつ非再利用とする。親の基底世代と後着記録時の現世代を区別し、別枝の更新だけで不正な履歴にしない。これらの意味はRuntimeの識別契約であり、Traceのfield配置や符号化を固定しない。

Logical Publication後に親と正負2子の関係を、Geometry Commit後に実在Boundaryと存在するSideの子参照・履歴完成を記録する。Boundaryが0でも完了できる。ObjectIdとCutOperationIdで相関し、欠落・重複・参照や件数の不整合を完全なOperation履歴として扱わない。

ゲーム状態の公開とTrace記録は非トランザクションとする。公開後にbest effortで記録し、書込失敗でゲーム状態を巻き戻さない。Geometry完成前に全読者が正常退役した場合はOperation／Childだけの未完履歴を許容し、架空Boundaryや完了を補わず、履歴完成だけの計算を続けない。正常退役自体をTrace書込失敗に数えないが、保存Trace全体がIncompleteとなり得る。

剛体リベースでは19.5.1の採用面、親frame・基底世代、採用理由を当該Pending Cutへ相関し、実際に選択した面を復元できるよう記録する。RigidCutPlaneChoiceは未選択／PredictedLocal／ActualHitLocalを区別し、未選択を公開しない。面の確定はTrace成功へ依存せず、記録の欠落・別操作や世代の混在を完全な選択とみなさない。記録だけで全物理軌道や採用Gateの再実行を保証しない。

PlayerLocomotionRejectedは固定Occupancyへの候補次姿勢Overlapによる要求全体Rejectを表し、侵入深度・Primitive ID・専用理由を記録しない。現行Slash相関は21.16.6に従う。Eventの分割数、field割当、数値token、固定長／可変長の保存表現は実装詳細とする。

### 21.4 履歴と診断保存

本節は既存Traceを対象とし、簡易ロガーの追記・外部読取り・終了は21.17に従う。Traceの履歴・Queue・保存処理はboundedとし、過負荷でGameplayを待機させない。記録の欠落、履歴上書き、不完全なOperation履歴や保存の不成立を、完全な再現根拠として扱わない。保存Traceが完全であるという判断には、必要な記録が揃っていることを含める。判定の符号化や保存検証の方式は実装詳細とする。

診断保存ではproducerと読者の資源寿命を守り、書込み中の領域を完成済みとして公開・再利用しない。通常の手動・診断保存は利用できるが、4章の共通Player終了時には保存の開始・完了を保証しない。過去形式を将来も読めることや、失敗した保存のRecoveryを恒久契約にしない。

### 21.5 Editor Timeline

時刻／フレームとSlash・Object・MobPlan・Taskを使って履歴を検索し、世代、処理結果、採否理由、依存関係と対応画像を確認できるようにする。不完全な記録は完全な状態再現として表示しない。採用中の保存TraceをPlay Mode外でも閲覧できるようにする。UI構成と旧形式の読込み維持は実装詳細とする。

### 21.6 性能上の規則

既存観測ではホットパスでTaskごとのDebug.Log、文字列化、全状態の毎フレームSnapshotを行わない。観測資源をboundedに管理し、記録・回収・保存の負荷と欠落を既存Profilerで確認する。Development Buildでは通常有効、非Development Buildでは無効または重大異常だけとする。21.17の簡易ロガーには同節の同期処理・allocation・遅延許容を適用し、既存Traceの制約は緩めない。

### 21.7 映像キャプチャとTrace同期

#### 21.7.1 目的と証拠の範囲

映像はTemporary／Committed断面、VFX、表示の巻戻りを確認する補助情報とする。未撮影やDropを別フレームの画像で補わない。証拠の範囲はUnity側の実際の取得画像に限り、OpenXR提出画像や最終HMD像との一致を保証しない。Unity側Captureが正常でもHMDだけに問題が出る場合、追加調査が必要になり得ることを許容する。

#### 21.7.2 Unity側の選択的キャプチャ

Unity側から必要な片眼映像または静止画を選択取得する。Window録画やHMD Mirrorを必須にせず、非同期・bounded資源・資源寿命・故障分離と短時間NVENC確認は21.15に従う。fps、寸法、Frame数、保存期間・形式は実装詳細とする。

#### 21.7.3 Capture相関

CaptureしたFrameとTraceの時刻・FrameId・対象IDを関連付ける。相関のために全Recordへ共通field列、Manifest、Receipt、専用保存schemaを要求しない。

### 21.15 非同期Captureと短時間NVENC確認

Phase 0／0.1から引き継ぐ能力は、非VR観測、Frame相関、必要なCapture、非同期処理、bounded資源とCapture失敗の分離とする。Phase 0.11では実際に使用するCapture経路で、対応環境のGPU画像から同一sessionの複数Frameを処理し、短時間のNVENC出力を確定する。正常Runのdecode結果が受付けたFrameの件数・順序に対応することを確認し、連続処理中も資源使用量をboundedに保つ。Main／Render ThreadはNVENCやGPUの完了、bitstream取得、file I/Oを待たず、in-flight容量の枯渇でGameplayを待たせない。

入力Texture、変換先、Encoder入力・出力、保存用bufferは、それぞれの非同期利用が終わるまで再利用・破棄しない。SourceのGPU利用完了とEncoder処理完了を同一視せず、正常終了では利用者の停止と参照終了を確認して安全に資源を回収する。資源を再利用する実装では、その利用終了後にだけ再利用することを確認する。再利用自体を必須にせず、Worker本数、Queue、Slot、所有型、通知形式、内部の準備・解放手順は固定しない。

Captureの不成立、故障、対応外構成はCapture内に閉じ、ゲーム停止を要求しない。ただし共有Device／Driver自体の喪失後の描画継続は保証しない。安全な所有状態や処理の停止を確認できない場合は、成功・完了・安全な解放を推測せず、同process内でCaptureを再開しない。後から呼出しが帰還してもこの制限を解除せず、process再起動を境界とする。Main／Renderに停止中Workerの帰還やJoinを待たせない。

実行中のPhase 0.11は改訂前に承認済みの方式でそのまま完了してよく、今回の改訂に合わせたWorker・Queue・Pool・NVENC・Publication・試験の作り直しを要求しない。完了条件は本節の能力と寿命条件とし、旧固定fps・Frame数・時間・試験階層を要求しない。

### 21.16 Phase 0.12～0.14 可変長Trace移行

#### 21.16.1 適用範囲と切替

対象はDomain Trace、Writer、History、保存／読込み、および以下の終了境界と製品接続とする。Encoderや映像取得経路を変更しない。0.12～0.14を同一変更系列の内部checkpointとする。製品Trace接続先が存在する場合は0.14でRelease既定を切り替える。製品Trace接続先が存在しない場合は、本系列のWriter／Historyについて、producer利用終了後に最終Drain・seal・保存を順に実行でき、保存・読込みで相関と不完全性を維持できる終了境界を確認すれば0.14を完了できる。実Capture終了経路への接続とRelease既定の採用は製品接続時に行い、その接続先の前倒し実装を0.14の完了条件にしない（2026-09-13人間承認）。旧新backendの並行搭載・二重記録を要求せず、置換済みのWriterと専用試験は同系列で削除できる。旧保存形式のLoader・Golden・再exportを維持する義務はなく、17章の扱いに従う。

#### 21.16.2 Writer、Lane、Drain、seal

Run開始前にimmutableなTrace Profileから固定Event mask、producerごとのPayload Ring容量、Runtime Index Ring容量、通常Drainの最大record件数、`MaxPayloadLength`を確定し、0.13以降は同じProfileからHistory Page size／Page数も確定する。各checkpointでbackendが要求するunmanaged領域の必要量をchecked算出し、全領域をRun開始前に確保する。単一allocationや複合ownerは要求しない。具体的な容量値、32 GiB、1 GiB等を既定値、最低値または製品保証にせず、確保不能または不変条件不成立ならRunを開始しない。Run中に領域を拡張／縮小せず、別lane／Pool探索、借用、動的Profile、sampling、severity taxonomyを追加しない。

外部Poolのworkerも既存producerとして接続する。1 laneは同時に1 producerと1 consumerだけが使う固定容量SPSCとし、backend非公開のBurst互換value-type Writerを構築時に単一laneへbindする。Writerは`IsEnabled(EventMaskBit)`と`TryWrite(RecordKind, PayloadPointer, PayloadLength)`だけを提供し、callerはpayload構築前にmaskを確認して正確な長さを決める。Writerはunmanagedなcaller memoryからlane所有Payload Ringへ必ずコピーし、return後にcaller memoryを参照しない。zero-copy、外部buffer lifetime移譲、managed配列、boxing、文字列化、serialize中の長さ決定を通常writeへ導入しない。

Runtime Index Entryは`RecordKind`、64 bit単調増加`PayloadStart`、`PayloadLength`だけを持ち、永続schemaではない。payloadの符号化と種別値の管理は実装詳細とする。WriterはIndex 1件と末尾paddingを含むPayload領域の空きを確認し、payload全体をコピーしてprivate index slotを書いた後、index write positionをrelease公開する。この公開だけをlane上のrecord commit pointとし、consumerはacquire済みentryが指すpayloadだけを読む。容量不足、oversize、内部Rejectは待機、拡張、別lane探索を行わず失敗を返し、lane-local Drop Countをsaturating加算する。Event単位の詳細な失敗Reasonは保存しない。Profileで無効なEventはDropへ数えない。

通常Drainはlaneを固定順round-robinで巡回し、構成された最大record件数で終了する。時間budget、動的quota、厳密なlane間公平性、producer間global sequenceを導入しない。単一lane FIFOだけを保証し、lane間の因果関係はTimestamp、FrameId、FixedStepIdおよびpayload内Domain IDで解釈する。正規停止順は`新規producer受付停止 -> 投入済みWork終了／Worker停止 -> 全ownerがWriter使用終了 -> 全lane最終Drain -> Drop集約 -> seal`とする。Trace Writerはこのstop／joinを信頼し、Registry、Lease、Receipt、per-event Active Writerで再証明しない。stop／join後のstale Writer使用はReleaseで未定義動作とする。

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

投機成果物はLatch済みSlashFrame、LogicalFragmentRef、BaseObjectGeneration、必要なGeometry／Physics／MobPlan世代とAnimation入力Identity、到達Step／時刻へ既存Snapshotで相関し、Commitには対応するSlashHitConfirmedを要求する。Estimator identityやSegmentIdを追加しない。相関の意味を全Recordの共通field列へ固定せず、保存形式・旧Reader・Goldenは17章に従う。

Slashの調整・再評価用入力と開発情報の扱いは19.1.12に従う。

### 21.17 開発用簡易ロガー

任意の開発診断用に、managedコードから直接使える単一のシングルトンを置く。公開記録APIは`write_log(writer_id, tag, value)`だけとし、各recordへ時刻・フレームカウンタ・writer_id・tag・valueの5項目を保存する。Burst内部へmanaged呼出しや別ロガーを追加せず、必要な記録は既存のmanaged境界で行う。別threadの記録を厳密な描画フレームへ帰属させる同期は要求せず、フレーム値の取得方法は実装詳細とする。

時刻は各呼出し中に取得したUTCのPOSIX秒（1970-01-01T00:00:00Z起点、うるう秒を累積加算しない）をJSON数値で保存する。初期実装では100ns単位に相当する小数点以下7桁までの取得値を保持し、秒・ミリ秒への切捨てやfloat／double経由の桁落ちを行わない。時計の実分解能・絶対精度・単調増加は保証せず、独自の時刻補間・補正を設けない。

writer_idとtagは利用者が選ぶ文字列で、ロガーは解釈・正規化・ID発行・登録・衝突検査をしない。writer_idの別用途との衝突は後から追加する側が回避する。tagの表記・意味にJSON構造や共有Schemaを要求しない。valueの初期対応は数値・文字列と、それらを列挙するiteratorとする。iteratorは呼出し中に列挙して同じ1 recordのJSON arrayへ格納し、呼出し後までlazyに保持しない。必要なUnity Vector等の型対応は同じロガーへ追加でき、値の表現・型対応の追加に共有Schema・登録・旧形式互換・本書の改訂を要求しない。

有効条件はUnity標準の`DEBUG`だけとし、独自フラグを設けない。未定義時は初期化・記録処理を条件付きコンパイルで外す。戻り値を持つAPIはC#のConditional属性を使用できないため、呼出し側の`#if DEBUG`で引数評価ごと除去する。既存呼出しもこの形式とし、エラーハンドリングは追加しない。残るAPI本体は列挙・シリアライズ・記録せず無効を返す。呼出し前の別文による値生成や、API宣言等の最終バイナリからの完全除去は保証しない。

DEBUG有効時は各Play Mode開始／Player起動で旧sessionの受付を停止し、旧Workerへbest effortの排出・closeを要求する。新sessionの専用Workerが、`C:\log\zantetsuken-vr\logger`以下へ開始タイムスタンプとコミットハッシュを含む名前の新しい`.jsonl`を開く。以前のログを上書きしない。開いた`StreamWriter`をWorkerだけが所有して`AutoFlush = true`とし、呼出元threadで値の列挙とJSON生成を同期処理した後、完成recordを有限Queueへ投入する。Queueの既定容量は65,536件とし、満杯なら空きを待たず`write_log`が満杯エラーを返して新規recordを拒否する。受付成功は永続化成功を意味しない。WorkerがFIFOで1行ずつ追記し、I/O中はproducer共通lockを保持しない。容量は待機record数であり、Workerで処理中の最大1件を含めない。複数threadの行混在を防ぐが、producer間の厳密な記録順序は要求しない。2026-09-20の人間指示により書込みをWorkerへ分離する。

出力はUTF-8（BOMなし）のJSONL、行終端はLFとし、Writerを開いたまま別プロセスが読めるようにする。読者はLF終端の正常なJSON行を使い、末尾の未完了・破損部分や最新recordがまだ読めないことを許容する。外部読者との同期、record書込みの原子性、ファイル全体の一貫したSnapshot、電源断への永続化は保証しない。AI等による直接読取りを用途とし、専用Reader／Viewerは作らない。

Play停止／Player終了の標準通知で受付を停止し、Workerへ待機recordの排出とWriterのbest effort closeを要求する。Worker終了待ちは有限時間とし、timeout後も旧Workerは旧sessionだけを扱い、新sessionのQueueやWriterへ触れない。Writerの作成・書込み・closeはWorkerだけが行う。session不在・停止中・I/O失敗後は受付不可を返し、自動再生成しない。終了時の全ログ回収・異常終了時のcloseは保証しない。ロガー自身の初期化・I/O失敗は診断の欠落として扱い、Gameplay・Commit・共通Player終了要求へ波及させない。4章の終了前記録を本ロガーへ置き換えず、そのlock取得・同期保存・close成功を終了API呼出しの前提にしない。再試行・代替Logger・Recovery・新しい共通Player終了条件を要求しない。

2026-09-20の人間承認により、DEBUG有効時の値変換・Queue lock待ち・allocation・GC・有限のWorker終了待ちによるフレーム停止とタイミング変化、ログ欠落・ファイル増加を許容する。非Development Playerではこの任意診断を取得しない。ローテーション・自動削除・耐障害保存を要求せず、ログの増加と後処理は開発時の運用で扱う。製品性能目標、幾何・物理整合性と既存Profiler・Trace・Captureの契約は維持し、測定構成を14章で区別する。記録を製品状態・Commit・Recovery・Trace完全性の正本にせず、Trace Run・Profile・producer登録・History・seal・終了時保存を利用条件にしない。既存の必須Trace記録をJSONLだけで代替しない。

導入は通常の共通コード変更とし、17章の同用途出力の移行・削除を含める。導入時は少数例で値・時刻の桁保持、複数threadの行混在なしと外部読取り、Domain Reload無効を含むPlay再開始・終了、DEBUG未定義時の引数評価・列挙・ファイル生成なしと移行範囲を確認する。既存Phaseを再実行せず、専用Phase・Test ID・Benchmark・Logger台帳・CI検出器を追加しない。Player確認は3.3を共用する。開始・終了callback、ファイル名の細部、コミット情報の取得・Playerへの受渡し、JSONキー・型対応の細部は実装詳細とする。3.3のユーザーパス匿名化と10.8の公開／非公開・利用許可境界を維持し、ローカル保存を公開許可とみなさない。

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


- [Unity XR Interaction Toolkit 3.0 Action-based Controller](https://docs.unity3d.com/Packages/com.unity.xr.interaction.toolkit%403.0/manual/xr-controller-action-based.html)

- [Unity XR Interaction Toolkit 3.0 Controller State](https://docs.unity3d.com/Packages/com.unity.xr.interaction.toolkit%403.0/api/UnityEngine.XR.Interaction.Toolkit.XRControllerState.html)

- [Unity 6.3 ProfilerMarker](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Unity.Profiling.ProfilerMarker.html)

- [Unity 6.3 Profiler Flow](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Unity.Profiling.LowLevel.Unsafe.ProfilerUnsafeUtility.CreateFlow.html)

- [Unity 6.3 ProfilerModule](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Unity.Profiling.Editor.ProfilerModule.html)

- [Unity 6.3 ProfilerRecorder](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Unity.Profiling.ProfilerRecorder.html)

- [Blender 4.5コマンドライン実行](https://docs.blender.org/manual/en/4.5/advanced/command_line/index.html)

- [Blender 4.5 Python API](https://docs.blender.org/api/4.5/)

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
