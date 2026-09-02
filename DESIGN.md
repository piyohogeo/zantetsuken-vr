# VR斬鉄剣ゲーム 技術設計書

*即時シェーダ切断と非同期メッシュ／物理更新による、低遅延・反復切断パイプライン*

| 項目 | 内容 |
| --- | --- |
| 文書目的 | Codexで継続更新するプロジェクト設計上の正本 |
| ステータス | Draft v1.5 / PoC実装準備・固定Capture Profile／同期映像／未来評価設計段階 |
| 作成日 | 2026-08-21 |
| 最終更新 | 2026-08-31 |
| 想定エンジン | Unity 6.3 LTS 6000.3.22f1 + OpenXR + URP |
| 採用アセット | Synty POLYGON City Pack（主素材）、Poly Pro Universe（比較・補助素材） |
| 初期対象 | PCVR、90Hz基準。Quest単体版は当面スコープ外 |
| 検証用HMD | Meta Quest 3SをQuest LinkでPCVR接続 |

> **設計の核** 刀の放つ斬撃波が触れた瞬間はGPUによる仮切断を表示し、表示メッシュと物理Convexをバックグラウンドで切断して追いつかせる。プレイヤーが感じる応答時間と、正確な幾何・物理更新を分離する。

## 1. エグゼクティブサマリー

本企画は、VR空間内の多様なプロップや人形を、刀の放つ斬撃波に沿って任意方向に両断できるアクションゲームである。最大の体験価値は、斬撃直後に隙間が開いて対象が分離したように見える即応性と、その後に破片が自然に物理挙動へ移行する一貫性にある。

推奨アーキテクチャはUnityを基盤とし、OpenXR、ステレオ描画、シーン管理、Rigidbodyなどを利用しながら、切断判定、仮切断レンダラ、メッシュ切断、Convex切断、世代管理を独自サブシステムとして実装する構成である。フルスクラッチのエンジン開発は行わない。

見た目はSynty POLYGON City Packを素材基盤とし、限定パレット、セル陰影、輪郭線、独自の看板・グラフィティでポップなローポリ都市へ統一する。特定作品の直接模倣ではなく、Y2K的な都市感、誇張されたシルエット、色面の強さをデザイン原則として抽出する。

## 2. 体験目標と設計原則

- 斬撃入力に対する見た目の反応を、幾何切断完了より先に提示する。

- 表示と物理の不一致時間を短くし、周辺破片が透明な旧Colliderへ接触する状態を最小化する。プレイヤー身体・手は初期仕様ではプロップ／破片とPhysX接触せず、刀も物理衝突させず切断可能時の論理Sweepだけを使用する。人工移動によるモデル化済みOccupancyへの代表的な新規侵入だけを簡易Queryで抑制し、実空間HMDはClampしない。Camera被り、未登録物体の内部視点、即時StencilのCamera-inside破綻を許容し、視界保護はbest-effortとする。

- 生涯切断数や全Pending Cut数ではなく、実際にBatchへ投入する`TemporaryRenderCapRecordSet`の件数、対象Cut Shell、固定長`SelectedTemporaryClipPlaneSet`が一時描画コストを決める構造にする。意味上の`ActiveTemporaryBoundarySet`とは分離し、`HasDetached`またはCull失効済み操作で実装簡略化のため残す補助Dormant Capも描画費用と枚数上限へ数える。Suppressed Cap、Fully Fixed Cullされた操作、Committed済み境界は費用へ含めず、Hybrid Clip容量を超えた境界はRenderer費用を増やさない。

- 標準Runtime表現をDisplay Mesh、`ClosedCutComponentSet`、`CutConnectivityGraph`、実行時Cut Shell、Compound Physics Proxyとする。役割は分離するが、同じGeometryが複数契約を満たす場合はBuffer／Meshを共有してよく、1物体へ役割ごとの実Mesh複製を必須にしない。製品用Strict Solid Cut Meshは生成・常駐・Fallbackのいずれにも使用せず、Global Solid Reconstructionは将来研究だけに隔離する。

- バックグラウンド結果は世代番号で検証し、古い結果を安全に破棄できるようにする。

- 短期プロトタイプでは対象範囲と品質契約を限定し、計測結果に基づいて拡張する。

- 飛翔する斬撃波の到達時間を計算猶予として利用し、命中前に未来姿勢、表示Mesh、Convexの切断を投機的に評価する。

- 予測結果は確定・条件付き・投機の信頼度と世代番号を持ち、実接触時の検証に成功したものだけをコミットする。

- 非同期処理と状態遷移は最初のPoCから相関ID付きで記録し、性能計測と因果関係の調査を同じ時間軸で行えるようにする。

- デバッグ映像はTraceを補助する証拠としてFrameIdと同期し、PoC初期はUnity側の選択的キャプチャ、必要性確認後はOpenXR Projection Swapchain Captureを段階導入する。

## 3. スコープ

### 3.1 初期垂直スライス

- 街区1つ、切断可能プロップ約10種、NPC 1体、刀1本。

- 単一切断、連続切断、処理中の再切断を検証。

- 即時clip表示、仮断面、後追い表示Mesh切断、Convex差し替えまでを一連で実装。

- 切断対象は閉じた静的メッシュと、切断時に姿勢を固定できるHumanoidに限定。

- 極小破片は物理化せず、デブリ演出または消去へフォールバック。

- PCVRを対象とし、実アプリの両眼描画90fpsを性能目標とする。XRコンポジタの再投影は瞬間的な取りこぼしへの安全網であり、常用前提にしない。

- 実装と性能計測は非VRモードから開始し、切断PoC成立後にQuest 3Sの有線Quest Linkで早期XRスモークテストを行う。本格的なVR操作・空間UIは、その後に導入する。

- 剣を素早く振ると三日月形の斬撃波が扇状に広がり、有限速度で飛翔する。接触時に対象が即座に分離する演出を主要攻撃表現とする。

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

> **状態モデル** 各切断対象はStable Geometry、Geometry未CommitのPending Cuts、全切断履歴を保持するCutBoundaryRecord群を持つ。バックグラウンド成果物が`Ready`になっただけではTemporary Rendererを外さず、描画フレーム境界で実Meshの適用と`GeometryState = Committed`がともに成功した後にだけ対応境界を一覧から除去する。CutBoundaryRecord、Cut Plane、論理Fragment、支持履歴はStable側へ移して保持し、Collider未完成は別の物理状態軸で管理する。

### 4.1 コンポーネント境界

| サブシステム | 責務 |
| --- | --- |
| Blade Pose Adapter | OpenXR Grip Poseへ持ち手別のGripToKatanaOffsetを適用し、BladeAxis、EdgeDirection、SideNormal、追跡有効性を提供 |
| Blade Sweep Detector | 刀身の連続姿勢からswept volumeとGesture Sampleを構築し、速度・移動量・Edge Direction Gateを評価。対象への最終命中は確定しない |
| Cut State | Stable世代、Geometry未CommitのPending Cut列、Temporary Render Boundary列、永続CutBoundaryRecord／論理破片、ジョブ状態、上限管理 |
| Temporary Slice Renderer | clip、論理破片の分離オフセット、仮断面、切断縁演出 |
| Visual Slice Worker | スキニング焼き込み、三角形切断、断面生成、属性補間、MeshData出力 |
| Physics Slice Worker | Convex平面クリップ、質量特性計算、Collider Bake／cooking |
| Commit Controller | 世代検証後、描画フレーム／物理ステップ境界で安全に差し替え |
| Debris Budgeter | 小片統合、寿命、スリープ、非物理デブリ化、総数制限 |
| Slash Gesture／Wave Simulator | 刀軌道から切断面と初期SlashFrontを早期Latchし、SpanAxisに対して一価・単調な粗い折れ線前縁の飛翔、Extending中の頂点／辺追加、逆行・自己交差によるFinalized、到達予定時刻、実接触を管理。VFXの前縁はこの判定形状と一致させる |
| Future Evaluation Scheduler | 初期版は固定優先度Class、締切、固定容量、Schedule前取消だけを扱う差し替え可能なDispatch境界とし、将来版で実測費用・信頼度・aging等を追加する。実行済みJob成果物は世代検証で破棄・再利用する |
| Prediction Physics | 必要な局所物理島を独立PhysicsSceneで先読みし、命中予定姿勢を生成 |
| Mob Future Planner | 副作用のない固定ステップ移動Kernelと`AnimationPlannerV1`からMobPlanのRoot軌道と`ExplicitAnimationStateV1`を生成し、Nearのライブ更新、Mid／Farの軌道再生、粗い無効化を同じ世代契約で接続 |
| Animation Pose Evaluator | immutableな明示Animation Stateと対象`FixedStepId`からcanonical Bone順のRig Poseを生成する。controllerなしPlayable／Mixer、Pose Table等を交換可能Backendとし、AnimatorController内部状態をCurrent／Futureの正本にしない |
| Observability／Trace | Profiler計測、状態イベント、Work Item／Job相関、固定長履歴、異常時保存、Editorタイムラインを提供 |
| Visual Capture | Unity側の選択的片眼録画と異常時静止画をTraceへ関連付け、後期にはOpenXR API LayerによるProjection Swapchain Captureを提供 |
| Asset Preprocessor | Blenderをヘッドレス実行し、ライセンスAssetからClosed Cut Component、Cut Connectivity／Attachment Metadata、Stencil契約、Compound Physics Proxy、検証レポートをローカル生成。製品用Strict Solidは生成しない |

### 4.2 切断イベントの時系列

- 刀の連続姿勢を収集し、Edge Direction Gateを通過したGestureだけをSlash候補とする。この段階では対象の命中も対象世代も変更しない。

- 十分な軌道が得られた時点で`SlashId`、`SlashGeneration`、切断面、粗い折れ線の初期`SlashFront`をLatchする。三日月VFXと前縁の飛翔・命中判定を同時に開始し、初期前縁と重なる対象はその時点で実命中とする。

- 切断面、最大飛距離、許容する最大前縁範囲から保守的な`Candidate Flight Bounds`を作り、候補対象を列挙する。各対象の`BaseObjectGeneration`を記録して未来姿勢、表示Mesh、Convex切断を投機開始するが、候補列挙だけではPending Cutを追加しない。

- Extending中も既存`SlashFront`を停止させず有限速度で前進させ、観測された振りのうち`SpanAxis`方向へ単調に進む部分だけを同一平面内の頂点／辺として追加する。微小な逆行は手ぶれとして無視し、明確な逆行、非隣接辺との交差、頂点順序の反転では現在SlashをFinalizedする。各辺を前フレーム位置から現在位置まで細い帯状にSweepし、三日月VFXの前縁と同じ形状で実交差を確認する。

- 実命中時に`HitConfirmed`を記録し、対象の`ObjectGeneration`を更新してPending Cutを追加する。同フレーム中に固定支持を分類し、境界ごとの`ExposureState`を確定する。`Active`境界は直ちにシェーダで正負側をclipし、仮断面、切断縁、音、火花、Hapticsを開始する。両側が固定された`Dormant`境界は境界単独では即時clip、Stencil、仮Cap、分離を要求せず、背景Geometry処理だけを継続する。ただし、同じ`LogicalCutOperation`が`HasDetached`またはCull失効済みなら、描画実装を単純化するため、そのDormant境界のCap Recordを補助Capとして`TemporaryRenderCapRecordSet`へ投入できる。この例外でもDormant側のOffset、Impulse、切断演出を起動しない。分類未完了、世代不一致、接続が曖昧など、安全な露出状態を決定できない境界は`Suppressed`とし、clip、Stencil、仮Cap、Offset、Impulseをすべて起動しない。再分類に成功した時点で`Active`または`Dormant`へ遷移する。

- 投機成果物が命中したSlash／Segment、確定した`SlashFrame`、基底対象世代、予測姿勢と一致すれば、表示・物理成果物を描画フレーム／物理ステップ境界でコミットする。

- 投機成果物が未完成または検証不一致なら、実命中時の状態を基底として表示MeshとConvex切断を優先ジョブへ投入する。`Active`境界が要求する即時表示と、Operation規則により選ばれた補助Dormant Capを完了まで継続する。Dormant境界は単独では描画を要求せず、Suppressed境界は常に抑止する。

- `Ready`な実ジオメトリは描画フレーム境界で実Meshへ適用し、その適用成功と同じ原子的Commitで`GeometryState`を`Committed`へ進めてStable Geometryとする。`Ready`になった時点ではTemporary Rendererを外さず、Commit成功後にだけ対応境界をTemporary Renderer用Pending一覧から削除して一時描画コストを回収する。`CutBoundaryRecord`自体、Cut Plane、論理Fragment、FixedSupportGraph Edge、支持・露出履歴は削除せずStable側へ保持する。Collider未完成は`PendingPhysicsSplit`、`PendingSupportClassification`、`PendingAnchoredSplit`等の物理状態で独立して追跡する。

### 4.3 バックグラウンド実行モデル

フレーム内または複数フレームにまたがるCPU計算は、C# `Task`を大量発行せず、Unity C# Job SystemとBurstを基本とする。メインスレッドはUnity Objectを数値スナップショットへ変換し、締切と優先度に従ってJobをBatch Scheduleする。Job本体は`NativeArray`、`NativeList`、`NativeStream`等のアンマネージデータだけを扱い、GameObject、Component、Transform、Renderer、Rigidbodyを直接操作しない。

- Job向き：候補交差、三角形分類、表示Mesh切断、Convex平面クリップ、断面・質量特性生成、未来軌道／MobPlanのBatch評価、対応APIによるCollider Bake。

- メインスレッド向き：JobのSchedule、`JobHandle`依存関係、世代／命中検証、`MeshData`のMeshへの適用、Renderer参照差し替え、Rigidbody／Collider生成、描画フレーム／物理ステップ境界のCommit。

- `Task`／Unity `Awaitable`向き：ファイルI/O、Trace／録画保存、Editorツール、外部プロセス待機、Unity非同期APIの進行制御。CPU幾何計算の標準実行基盤にはしない。

極小Jobを対象ごとに無制限発行せず、同種処理を`IJobFor`／`IJobParallelFor`等でBatch化する。JobはSchedule後に中断できないため、投機前提が崩れた場合もメインスレッドから`Complete`を強制せず、完了後にGeneration不一致として破棄する。`TaskId`はC# `Task`型を意味せず、Job、I/O、GPU処理を含む論理Work Itemの相関IDとして維持する。

### 4.4 段階導入するSoft Real-Time Dispatch

Playableな切断ループを早期に成立させるため、初期`FutureEvaluationDispatcherV1`は高度な最適化器ではなく、Main Thread上でSchedule前のWork Itemだけを並べる固定容量の非厳密Soft Real-Time Dispatcherとする。Unity Job SystemへScheduleした後の優先度変更、preemption、中断、Worker Threadの独自置換は行わない。後期実装を捨てて差し替えてもProducer、DAG、Job Kernel、Commit Controllerを変更しなくてよいよう、公開境界を次へ限定する。

```text
TryEnqueue(in EvaluationWorkItem, out WorkToken) -> EnqueueOutcome
TryCancelQueued(WorkToken) -> bool
DispatchReady(in DispatchBudget) -> DispatchReport
CollectCompleted(in CompletionBudget) -> CompletionReport
TryGetState(WorkToken, out WorkItemState) -> bool
```

`EvaluationWorkItem`は`TaskId`、`PriorityClass`、`HasDeadline`、有限・非負の単調時計値`DeadlineTimestamp`、Job種別／Batch Key、推定費用Bucket、入力世代Snapshot、成果物所有者を持つ入力Descriptorであり、`EnqueueSequence`を持たない。Deadlineなしは`HasDeadline=false`かつ`DeadlineTimestamp=0`で表し、NaN／Infやsentinel最大値を保存しない。受付順は`Descriptor検証 -> Accepting／Sequence残量検査 -> Queue Slot予約 -> Sequence発行とRecord公開`へ固定する。最後まで成功した受付だけについて、Dispatcher instance内で1から単調増加する`uint EnqueueSequence`を内部`QueuedWorkRecord`へ保存する。0を未設定に予約してwrap／再利用せず、`uint.MaxValue`を発行済みならSlot予約前に`SequenceExhausted`として停止する。Descriptor不正、容量不足、NotAccepting、SequenceExhaustedではRecordを公開せずSequenceを消費しない。

`WorkToken`は受付済み内部Recordを世代付きで参照する不透明Handleであり、bit layoutやEnqueueSequenceを公開契約にしない。`TryGetState`が返す診断Snapshotは同じ内部RecordのStateとEnqueueSequenceを読み出せるが、呼出側はSequenceを書き戻せない。Traceの`Value1`も内部Recordの値だけを正本とする。`EnqueueOutcome`は`Invalid=0`、`Accepted=1`、`CapacityExceeded=2`、`InvalidDescriptor=3`、`SequenceExhausted=4`、`NotAccepting=5`の固定値とし、受付失敗時の`WorkToken`はInvalidとする。DispatcherはUnity Object、切断Geometry、MobPlan内容を解釈せず、Jobの構築とCommitも行わない。DAG Coordinatorが依存完了を判定し、ReadyになったWork ItemだけをDispatcherへ渡す。将来Backendが依存Graphや費用モデルを内部化しても、この受付・取消・Dispatch・Completion境界と`WorkToken`を維持する。

初期`PriorityClass`は数値を固定し、値が小さいほど高優先とする。

| 値 | PriorityClass | 初期対象 |
| ---: | --- | --- |
| 0 | `CriticalPhysicsSafety` | 固定支持分類、Safety Tether前提、Impulse／Offset可否、物理Commit安全条件 |
| 1 | `ConfirmedPhysics` | 命中済みConvex切断、Fast Cook、Collider分裂に必要な処理 |
| 2 | `ConfirmedGeometry` | 命中済み表示Mesh、実Cap、Stable Geometryへの置換 |
| 3 | `NearDeadlinePrediction` | 命中前だが到達締切が近いMesh／Convex／姿勢の投機評価 |
| 4 | `BackgroundMaintenance` | Fast Simulation再cook、Shared Convex精密化、遠距離MobPlan延長、未来Animation焼き込み、Cache／品質向上 |

同一Class内は`HasDeadline=true`を先にして`DeadlineTimestamp -> EnqueueSequence`のstable順とし、Deadlineなしは同Class末尾へ置く。比較は`HasDeadline`を別keyとして行い、内部に正の無限大を生成しない。V1は命中確率、信頼度、画面面積、厳密な費用式、aging、動的Class変更を順位計算へ入れず、`EstimatedCostBucket`は計測とDispatch予算の粗い控除にだけ使う。低優先度のstarvationは許容可能な品質低下としてCounterへ記録し、物理安全を逆転させる公平化は行わない。

QueueとWork Tokenは起動時に固定長領域を確保し、実行中の成長とGC allocationを禁止する。V1は単一の固定長binary heapまたは同等の固定Class列でよく、内部表現をAPIへ公開しない。`TotalQueueCapacity`に加えて`CriticalReservedSlots`を持ち、Class 2～4は予約分を消費できず、Class 0～1だけが全容量を利用できる。満杯時に既存Itemを追い出したりMain Threadで待機せず`CapacityExceeded`を返す。呼出側は既存の即時Renderer、旧Collider共有、未延長MobPlan等の各機能固有Fallbackを継続し、同一Frame内で無制限再試行しない。

`DispatchBudget`は1 Tickの`MaxScheduleCount`、`MaxEstimatedWorkerCost`、Job種別ごとの既存同時実行上限を持つ。選択した同種Itemは可能な範囲でBatch化するが、高優先Itemを低優先Batchの完成待ちへ依存させない。BackgroundはClass 0～3のReady Itemがなく、予約済みWorker／Bake枠と当該Tick予算に余裕がある場合だけScheduleする。Fast CookはClass 1、後追いFast Simulation再cookとShared Convex ResolutionはClass 4とする。表示Meshは即時Rendererで隠せるためClass 2とし、物理安全処理より先にしない。

V1の異常処理は、無効Descriptorの受付拒否、容量超過、Schedule前取消、Schedule済み成果物のGeneration Reject、二重Completion拒否だけを必須とする。優先度継承、deadline miss recovery、queue間work stealing、adaptive cost learning、aging、複数端末別係数、厳密なCPU予約は実装しない。これらはProfiler CounterとT-016／T-076の実測後、必要なものだけを後Phaseの`FutureEvaluationDispatcherV2`へ追加する。V1実装の内部を破棄しても、上記API、PriorityClassの意味、Work Token、Trace相関、世代Commit契約は維持する。

観測は既存`TaskScheduled／TaskStarted／TaskCompleted／TaskCancelled／CommitRejected／ResultDisposed`を使用する。V1 Dispatcherを通るTask lifecycle Eventでは共通`TaskId`をWork Tokenへ一致させ、`Value0=PriorityClass`、`Value1=EnqueueSequence`とし、いずれもuint値をbinary64へ正確に格納する。Deadlineと費用BucketはProfiler側へ記録し、既存Trace schemaへ追加fieldを設けない。受付失敗はV1では個別Trace Eventを増やさずOutcomeとCounterへ反映する。Profiler CounterはClass別Queued数、Running数、Schedule数、CapacityExceeded数、SequenceExhausted数、Deadline超過数、最古待機時間、Critical予約枠残数、Tickごとの推定／実Worker時間を最低限とする。V1はDeadline超過を自動修復せず、機能固有Fallbackを継続して計測事実だけを残す。

## 5. 即時表示レンダラ

### 5.1 分離表示

元メッシュを論理破片ごとに描画し、各切断面の正負符号に応じてフラグメントをclipする。論理上の切断幅（Kerf）は0とし、自由破片が相対移動した結果としてのみ隙間と断面が見える。単一切断では正側・負側の2インスタンスを描き、自由側へ必要最小限の仮分離Offsetを与える。複数切断では、論理破片が保持する半空間の組み合わせだけを描画する。

破片の表示オフセットはスキニング後またはワールド変換後に加える。スキニング前に加えると、ボーン姿勢によって分離方向が歪むため避ける。

FixedSupportGraph上で連結な切断境界の両側Fragmentがともに固定なら、その`CutBoundaryRecord`の`ExposureState`を`Dormant`とする。本設計ではKerfが常に0であり、Fixed Fragmentの表示Offsetと相対運動も0という不変条件を別途持つため、Dormant判定でKerf、Offset、相対Transform、後続Detached状態を重複確認しない。ExposureStateの判定単位は境界ごとだが、PoCの即時Renderer全体省略は後述する一回の`LogicalCutOperation`単位で行う。同じ親LogicalFragmentへの一回の切断で生じた全直接子がFixedでCull未失効なら、その切断操作の全即時clip、Stencil、仮Cap、Shadow近似を省略する。Unknownがなく一つでもDetached、またはFully Fixed Cull失効済みなら、Fixed同士のDormant境界を含む全非Suppressed Capを通常Batchへ残し、Cap単位の除去やペア追跡を行わない。UnknownがあるIncomplete操作では既知Active Capだけを描く。バックグラウンドの実Mesh切断が完成したら、Fixed Fragmentを同一Transformのまま実断面付きMeshへ差し替えてよい。境界に生じる細い亀裂、輪郭線、線状Z-fighting、軽微なチラツキは「極めて薄い切断痕」として許容する。後続切断でAnchorへ到達できない論理破片が生じた瞬間、その破片に接する過去のDormant境界をまとめてActiveへ変更し、完成済みFragmentはRenderer交換なしで動かし、未完成境界だけを即時レンダラで補う。

### 5.2 仮断面とステンシル

ステンシルは切断そのものではなく、仮断面キャップのマスク生成に使う。プリプロセス済み`Stencil Cut Shell Base`または直前のStable Cut Shellから、Geometry未CommitのPending Cutを適用した論理上の実行時Cut Shellを導出する。意味上の`ActiveTemporaryBoundarySet`は`ExposureState == Active && GeometryState != Committed`、すなわちGeometryが`Pending`または`Ready`の境界集合とする。実際にBatchへ投入する`TemporaryRenderCapRecordSet`は後述する`OperationSupportState`と`FullyFixedCullInvalidated`から別途導出し、`HasDetached`またはCull失効済み操作ではDormant補助Capを含み得る。各Recordに対応するOriented Closed Cut Shellの表裏面から対象境界の内部領域をStencilへ記録し、対象のローカルOBBと切断平面の交差から作る有限な`Cap Bounds Polygon`をStencil非ゼロ領域だけ描画する。

- Clip Plane：物体を正負に分け、隙間の空いた分離表示を作る。D3D11の初期実装ではRasterizerの`SV_ClipDistance`を優先し、固定上限を超えた少数だけをPixel Shaderの`clip()`で補う。

- Stencil：切断平面上で元物体内部に相当する範囲をマスクし、仮断面を塗る。

- 実断面Mesh：バックグラウンド処理完了後に仮断面を置換する。

即時Rendererが当該`RenderFragment`へ適用し得るGeometry未Commit境界を、操作単位の支持状態で絞った集合を`TemporaryClipConstraintCandidateSet`とする。`FullyFixedCullEligible`な操作からは1面も入れない。`OperationSupportState == HasDetached`または`FullyFixedCullInvalidated == true`の操作では当該Fragmentに関係する全非Suppressed境界を入れ、`Incomplete`では当該Fragmentに関係する既知Active境界だけを入れる。Suppressed境界と、Incomplete操作内のDormant境界は候補にしない。この資格規則は`TemporaryRenderCapRecordSet`の三値集約と同じだが、集合自体はCap Record集合と別に保持する。

候補は既存のCutBoundary Record公開列を古い順に走査し、未Commit祖先制約を子孫制約より必ず先に置くstable順で選ぶ。選択結果は候補列の先頭から最大12面のdependency-closed prefixとし、ある子孫境界を選ぶために必要な未Commit祖先境界が選択外なら、その子孫も選ばない。通常の公開処理は祖先を子孫より先に列へ追加する不変条件を持ち、復元データがこの順序を満たさない場合は新しい順へ並べ替えず、違反境界以降をIgnoredとして背景Geometry完成へ委ねる。ID値によるsortや別の優先度Metadataを追加せず、左右眼、Color、Depth、ShadowCaster、Stencil Volumeの全Passで同じ選択結果を共有する。カメラ距離、眼、Pass、毎フレームの可視性で順序を変えない。候補追加、Geometry Commit、`ExposureState`遷移、`OperationSupportState`遷移、`FullyFixedCullInvalidated`変更、RenderFragmentとCutBoundaryの対応関係変更のいずれかが候補資格または依存関係を変えた場合、状態変更を公開する同じ描画更新境界で再構築する。

D3D11／Shader Model 5のPoC Profileは`RasterClipPlaneCapacity = 8`、`PixelClipPlaneCapacity = 4`、`TemporaryClipPlaneCapacity = 12`を初期値とする。`SV_ClipDistance`と`SV_CullDistance`の合計component上限8をRaster側の正本とし、このShader Variantでは`SV_CullDistance`を使用しない。先頭8面をVertex Shaderから`SV_ClipDistance0/1`の合計8 componentへ出力し、未使用componentは全頂点で正の有限値へ固定する。続く最大4面だけを固定長per-instance配列と`PixelClipCount`からPixel Shaderの`clip()`で評価する。面数や平面値によるMaterial、Keyword、Pass、Draw分割、可変長Buffer、動的Loop上限の増加を行わない。MSAA時は先頭8面のRasterizer clippingによるcoverageを正本とし、Pixel fallback境界との微小なedge品質差は短時間の品質低下として許容する。

dependency-closed prefixへ入らない後発面は`IgnoredTemporaryClipBoundarySet`とし、即時RendererのColor／Depth／Shadow／Stencil Volume入力からだけ除外する。CutBoundaryRecord、Exposure／Geometry状態、Logical Fragment、切断履歴、世代、支持判定、表示Mesh／Cut Shell／Convex Job、物理Commit、優先度付けは変更・破棄せず、背景Geometry Commitで正しい形状へ収束させる。無視された新しい境界は一時的に即時表示されず、影もその境界より前の形状となり得るが、選択済み祖先の外側にGeometryを復活させずSiblingを重ねないbounded degradationとする。Plane overflowを理由に既存Jobをcancel、再発行、同期完了してはならない。

`TemporaryRenderCapRecordSet`、Cap Bounds Polygon、Stencil Conflict Graph、Color、Draw ListはPlane overflow時にもcompaction／部分更新しない。Ignored境界に対応するStencil Volume Recordだけを単純にsubmitせず、Cap板Recordは従来Batchへ残してよい。対応VolumeがないColorはClear後のStencilが0なので板はColor／Depthを書かない。別RecordのResidual StencilがそのCap Boundsへ到達し得る場合は既存のCompatibility／Conflict Graphで別Colorへ分離し、分離を証明できなければそのColorのStencil仮Cap全体を既存Fallbackへ送る。Ignored Capを隠すための追加Mesh生成、Buffer compaction、個別Draw除去はPoCへ追加しない。

- Cap Bounds PolygonはOBBの12辺と切断平面を交差させ、epsilonで重複を除いた3～6頂点を平面上で並べて生成する。複数のTemporary Render Boundaryでは、`SelectedTemporaryClipPlaneSet`に属するほかの表示中切断面が定める論理破片の半空間で凸多角形clipし、選択済み切断面同士の交差を即時表示へ反映する。SuppressedおよびIgnoredなPending Cutはこの即時描画用clip集合へ含めない。全直接子Fixedの`LogicalCutOperation`は操作単位で除外し、それ以外の操作ではDormant境界を含む全非Suppressed Capを通常経路へ残せる。

- Cap Bounds Polygonは物体表面との正確な交差輪郭ではないため、最終的な凹形状、穴、部品輪郭はStencilで制限する。実表面との輪郭を三角形化できた場合は実断面Meshとして扱い、Stencilへ重複して依存しない。

- 全直接子Fixedの切断操作は仮断面描画を要求しないが、実断面Meshの生成と公開は停止しない。実断面は共通の片面トゥーンMaterialを基本とし、正負Fragmentで逆向きの法線を持たせる。Cull Offの両面描画は通常カラーPassで常用しない。一つでもDetachedな直接子がある操作でFixed同士のCapを通常Batchへ残した結果の線状亀裂、輪郭線、局所的Z-fightingは許容するが、画面規模の面状Z-fightingや可視Cap欠落は不具合とする。

> **入力品質上の注意** Stencilの表裏カウントは、自己交差のない幾何Solidではなく、Raster上で閉じた有向Triangle chainを最低契約とする。表示Meshを直接使わず、プリプロセス済み`Stencil Cut Shell Base`から現在世代のCut Shellを派生する。各Topology Edgeの有向incidence総和が0、共有Topology Edgeのposed positionがbit一致、局所windingが整合し、finite positionと有効indexを持つことを要求する。各Edgeが必ず2 Faceに属することや、非隣接Faceの自己交差が0であることは要求しない。閉じたDisconnected Component、Non-manifold Vertex、有向incidenceを相殺できる偶数Edge UseのNon-manifold Edge、Duplicate／Coincident Face、Internal／Nested Shell、skinning後Self-intersectionを許容し、仮断面を`Stencil Winding Count != 0`の領域と定義する。逆向きCoincident FaceはTopology検査だけでは検出せず、同一Shell内で符号が相殺した領域をこの定義どおり空として扱う。局所winding不整合、未相殺Boundary／T-junction、共有Edge位置不一致は単一Stencil Passで安全に扱えないためFallbackする。このStencil専用契約は6章のランタイム表示Mesh切断入力および個々のPhysics Convex契約と独立に判定する。

`Stencil Cut Shell Base`の全体検証はImport／Blender前処理時に一度だけ、Edge Useをstable keyで集計するO(Triangle + Edge)の`OrientedShellValidator`として行う。Topology Edgeのcanonical方向を小さい`TopologyVertexId`から大きいIDへ固定し、各TriangleのEdge Useがcanonical方向なら`+1`、逆なら`-1`をchecked integerで加算し、各Edgeの総和0を要求する。同じTopology Vertexを参照する全Edge Useは同じcanonical position recordを共有し、別Topology Edgeを座標一致だけでmergeしない。Topology adjacencyのComponent走査とcanonical順のbinary64 signed-volume集計も同じ線形工程で行うが、signed volumeは全体向きの候補であり、自己交差Shellの局所Winding符号が一様であることまでは証明しない。`Positive／Negative`として共有Groupへ参加できるのは、前処理`UniformWindingSignCertificate`が、対応View／Skinning Profile内の全非ゼロ領域で符号が一様であることを保証し、かつsigned volumeの絶対値がProfile epsilonを超えるComponentだけとする。Negative ComponentはTriangle windingを一括反転して`PositiveNormalized`へ正規化する。Certificateなし、体積がepsilon以内、非有限、または対応Skinning RecipeがPolarity維持を保証しない場合はShellの`StencilPolarity`を`Unknown`とする。自己交差検査やUniform Sign証明をコアの必須前処理にせず、許容的ShellはUnknownのまま利用できる。Unknown Shellは別ShellとStencil Countを共有せず、`StencilShellInstanceId`を含む専用Groupへ隔離する。負determinant World Transformは描画時の`EffectiveStencilPolarity`へXORし、Front／BackのIncrement／Decrementを交換してPositiveへ補正する。

各Shellはさらに、対応View／Skinning Profile内の任意pixelで生じ得る絶対Windingの保守的上界`MaxAbsoluteWindingBound`を前処理証明として持つ。自己交差のない正規化済み単一閉Componentは1、複数の独立閉Componentは各Component Boundのchecked和を初期証明にできる。許容的なSelf-intersection等でより小さい上界を証明できない場合はTriangle数等から導く安全な上界を保存し、255を超えるか証明不能なら`Unknown`とする。厳密なBound取得のためにMesh全体self-intersection検査を必須化しない。ランタイムCut Shell切断ではclipにより既存Boundを増やさず、生成Capが閉鎖不変条件を満たす通常経路では親Boundを継承する。Fallback Cap等で継承を証明できない場合だけUnknownへ降格する。

Mesh全体のself-intersection、inside／outside、generalized winding、outer／inner shellの幾何分類を実行しない。ランタイムでは別の全Mesh検査Passを追加せず、Cut Shell切断の既存Count／Write／Commit内で今回変更したOriginal Edge、生成Edge、Capの有向incidence balance、共有position、finite性とBound継承可否だけを局所集計する。未変更領域は前世代の検証済み不変条件を継承し、局所検証失敗時は新Cut ShellをCommitせず即時表示の安全なFallbackへ送る。スキニングはTopology／indexを変更せず、同じTopology Vertexからcanonical posed positionを一度だけ生成する限りTopology再検証を要求しない。

Cameraまたは片眼の視点が即時切断中のCut Shell内部へ入る、あるいはNear PlaneがStencil VolumeのFront／Back対応を切る場合、Stencil Countが画面全体で閉じず、仮Capが画面の一部だけに現れる、内部面／裏面が見える、左右眼で結果が異なる等の一時的な破綻を許容する。この状態を検出するためのRender Mesh内外判定、Ray parity、generalized winding、全Cut ShellとのCamera包含判定は追加しない。Camera重なりだけを理由に切断Job、物理Commit、Geometry Commitを取消・再発行せず、同期Mesh切断や別Cap方式へ切り替えない。これはCameraがShell外にある通常条件でのStencil閉鎖性、Count上限、Group分離を緩和するものではなく、Temporary Renderer使用中のCamera-inside／Near-Plane例外である。Stable Geometryへの置換後はTemporary Stencil由来の部分Capを残さないが、Cameraが実Geometry内またはNear Plane近傍にあること自体による不自然な見え方は引き続き許容する。

### 5.3 断面マテリアルとデバッグ表示

通常断面は、全体のポップなトゥーン表現と同じ共通シェーダーへ、粘土を思わせる彩度の低いグレーをBase Colorとして渡す。断面専用のトライプラナー、ノイズ、凹凸、内部グラデーション、写実的な特殊シェーダーは使用しない。仮断面と実断面は生成方法が異なるが、通常表示時の陰影段数、輪郭、ライト応答、グレー色を一致させ、差し替えを目立たせない。

デバッグモードでは同じトゥーンシェーダーのBase Colorだけを処理経路に応じて上書きする。Unlit化はせず、断面の向きと立体感を維持する。

| 断面色／表示 | 意味 |
| --- | --- |
| 赤 | 即時レンダラの仮断面が現在表示中 |
| 青 | 命中前に完成した先行切断成果物が、FrontEdge命中時の検証に成功してCommit済み |
| 緑 | SlashFront命中後に切断計算を開始し、実MeshへCommit済み |
| 水色 | 先行成果物の一部を再利用し、命中後に残りを完成してCommit済み |
| 黄 | 表示Meshは完成したがPhysics Proxy／ConvexはまだPending |
| オレンジ | 計算予算超過、タイムアウト、簡易形状など品質低下フォールバック |
| 紫の点滅 | 予測不一致またはGeneration不一致で先行成果物をReject |
| 黒い縞／縁 | 表示形状とColliderが一時的に不一致 |
| 通常グレー | 通常表示。Temporary Renderer対象がないStable表示にも使用 |

赤は現在の仮表示状態、青／緑／水色は最終成果物の計算経路を表すため、典型的には`赤 -> 青／緑／水色 -> 通常グレー`と遷移する。経路解析モードではStable後も青／緑／水色を保持できるようにし、通常の状態確認モードではStable移行後に色上書きを解除する。通常グレーは「静止」を意味せず、破片が運動中でも表示と物理が確定していれば使用する。

色だけへ依存せず、Reject、Pending Physics Split、品質低下には点滅、縞、縁取りを併用する。詳細文字は全断面へ常時描画せず、選択中の1対象だけを単一の画面／手首固定デバッグパネルへ5～10Hz程度で表示する。全件の詳細はEditor Timelineと保存Traceを正本とする。

### 5.4 即時切断中のShadow Map

影はRealtime Shadow Mapを使用する。即時切断中の論理破片はShadowCaster PassでもカラーPassと同じper-instance切断平面、論理破片Side、分離Offsetを適用する。一方、Shadow Mapには色付き断面を描く必要がないため、Stencilによる仮断面キャップは生成せず、ShadowCasterだけを両面描画して開口の奥にある外殻裏面を遮蔽面として使用する。

この方式は、閉じた元形状に対する外部Shadowの被覆範囲を低コストで近似するが、本来は切断面キャップが書くはずの深度より奥側の外殻深度がShadow Mapへ入る場合がある。切断面が床／壁に近い、薄い物体、非閉形状、Self Shadow、Shadow Bias、Cascade境界、近距離Point／Spot Lightでは接地影の浮きや漏れが発生し得る。即時状態の短時間近似として許容し、Stable Mesh Commit後は実断面を含む閉形状と片面ShadowCasterへ戻す。

- `Cull`はper-instance属性ではなく描画状態として扱い、Shadow描画を原則としてStable片面群の`Cull Back`とPending両面群の`Cull Off`へ分ける。UnityのRenderer経路では`ShadowCastingMode.On`／`TwoSided`、専用Renderer経路では対応するShadowCaster Variantを使用する。

- 「2回」はShadow Map全体が必ず2 Draw Callだけになる意味ではない。Light、Cascade／Shadow Map Slice、Mesh、Material、Shader VariantなどのBatch単位ごとに、少なくとも片面群と両面群へ分かれるという意味とする。

- 切断平面は5.2の固定上限Instance Recordに`RasterClipPlaneCount`、`RasterClipPlanes[8]`、`PixelClipPlaneCount`、`PixelClipPlanes[4]`、各面へ反映済みのFragment Side、`SeparationOffset`として保持する。ShadowCasterもColor Passと同一のstable選択結果を使い、先頭8面は`SV_ClipDistance`、続く最大4面はPixel Shader `clip()`、超過面は即時Shadowから無視する。切断数や平面値でMaterial、Shader Keyword、Passを増やさず、同じCull群のBatchを維持する。

- Stable Instanceをclip対応Shadow Shaderへ統合するか、`RasterClipPlaneCount = 0 && PixelClipPlaneCount = 0`専用の高速経路へ分けるかは実測で決める。全ShadowCasterを常時`Cull Off`にしてDraw群を統合する案は、裏面Raster／overdraw増加を測定せず採用しない。

### 5.5 コスト制御

- 同一物体の`TemporaryRenderCapRecordSet`件数に上限を設ける。初期候補は実際に描くCap Record 2〜4枚とし、`HasDetached`またはCull失効済み操作で残す補助Dormant Capも1枚ずつ数える。Suppressed Cap、Fully Fixed Cullされた操作、Committed済みCutBoundaryRecordは数えない。意味上のActive境界数だけで上限判定してはならない。

- Cap Record 2～4枚は通常時に背景再構築を促す品質／費用目標であり、`TemporaryClipPlaneCapacity = 12`はShader処理量を固定する絶対安全上限である。1枚のCap Polygonまたは1個のRenderFragmentが、ほかの未Commit境界による複数の半空間制約を受けるため両者の件数は同義ではない。通常目標を超えた瞬間に同期再構築せず、Raster 8面、Pixel 4面、残り無視という固定処理量を維持する。

- 上限到達時は補助Dormant Capを含む`TemporaryRenderCapRecordSet`を基準に、複数切断をまとめて再構築し、古いGeometry未Commit境界をStable Meshへ焼き込む。`Ready`から実Mesh適用と`Committed`への遷移が同じ描画フレーム境界で成功した後にだけ対応Cap Recordを実描画集合から外し、Active境界集合と切断履歴そのものは独立して保持する。

- `RasterClipPlaneCount`、`PixelClipPlaneCount`、`IgnoredTemporaryClipBoundaryCount`をProfiler Counterと選択対象のデバッグ表示へ出す。Plane overflowによってGeometry Jobの優先度、依存関係、cancel／再発行規則を変更せず、Frame内の待機や同期Commitを禁止する。

- 画面外・遠距離・停止中の物体を優先的に確定する。

- 小さすぎる論理破片は描画／物理の対象から外し、簡易デブリへ統合する。

- Stencilは切断面ごとの一時作業領域として再利用し、恒久的なビット割当は行わない。

### 5.6 スクリーンスペースStencil Batch

Stencil Bufferは画面座標ごとに共有されるため、すべての即時切断物体を無条件に同じStencilへ蓄積しない。現在の全World Cut Plane、各PlaneのFragment Side／半空間、分離Offset、Cap Material、法線、デバッグ色、Fade等が一致し、`StencilPolarity == PositiveNormalized`で、後述する8bit Count予算にも合格した対象だけを`CapCompatibilityKey`で同じ互換Groupへまとめる。このGroup内では各Shellの内部寄与が同符号なので、スクリーンスペースで重なっても非ゼロWinding Maskの和集合を保証できる。`StencilPolarity == Unknown`なShellは`StencilShellInstanceId`をKeyへ含む専用Groupとし、別ShellとCountを共有しない。同一Unknown Shell内部の逆向きCoincident等による相殺は非ゼロWinding semanticsとして許容するが、複数Shell間の相殺を「和集合」と呼ばない。

StencilはParityの`Invert`や飽和演算ではなく、検証済みOriented Closed Cut ShellのFront／Back Faceに対する`IncrementWrap／DecrementWrap`からなる8bit Winding Count方式を使い、各Color開始時に専用Stencil Byteの全8bitを0へClearし、Capを`Stencil != 0`で描画する。`IncrementSaturate／DecrementSaturate`は可逆な閉領域相殺を壊すため使用しない。閉じた部分ではFront／Backが`+1 - 1 = 0`へ相殺され、切断による開口部だけに非ゼロの`Residual Stencil Support`が残る。Self-intersection、Duplicate／Coincident Face、Nested ShellがCountの絶対値を増やしても、証明済みBound内なら非ゼロMaskとして受理し、実断面Meshとの差を許容する。

8bit経路へ投入する各Recordは既知の`MaxAbsoluteWindingBound`を持たなければならない。同じCapCompatibility Group内のRecordをstable順にFirst-Fitして1個以上の`StencilCountBatch`へ分割し、各BatchでBoundをchecked `uint`加算した`BatchWindingBound <= 255`を必須とする。単一RecordのBoundが255を超える、BoundがUnknown、またはchecked加算がoverflowする場合、そのRecordを8bit Stencilへ投入しない。同じ互換Groupから分かれたSibling Batch間にはStencil Conflict Graphで無条件Edgeを張り、必ず別Colorとして個別にClear／Volume／Cap描画する。初期Fallbackは`Stencil仮Cap省略 -> clip分離表示継続 -> 表示Mesh Job優先度引上げ -> 実Capへ直接置換`とし、検証済みStrict Shellへの切替やR16／R32 signed maskは将来の測定付きFallbackに限定する。Wrapで256の倍数へ戻る経路と飽和による閉領域残留を仕様上到達不能にし、T-067／T-084はこの上限契約を検証する。

各フレーム、`StencilCountBatch`をノードとし、左眼または右眼のどちらかで保守的な可視Cap Boundsが重なる非互換Batch間と、同じ互換Groupから分割されたSibling Batch間へ辺を張る`Stencil Conflict Graph`を構築する。物体OBB投影矩形と可視Cap Boundsはどちらも安全側の非交差証明に使い、Siblingでない組は各眼でいずれかが非交差ならその眼では競合しない。次数または画面面積の大きい順にFirst-Fit Greedy Coloringし、同じColor内では「全眼で可視Cap Boundsが非重複」または「重複してもキャップ互換かつBatchWindingBound合計が255以下」のどちらかを保証する。同じColorへ複数の互換Batchを再統合する場合もColor単位でBoundをchecked再加算する。

各Colorについて、対象Rectの予約Stencil領域をクリアし、Color内の全Cut Shellを共通Stencil Volume Phaseへ投入した後、対応する全Cap Bounds Polygonを共通Cap Phaseへ投入する。Color内では非互換な`Residual Stencil Support`同士が重ならないため、Rawな途中書き込みの重なりを許容しつつ、物体別Stencil IDを持たず同じStencil操作を再利用できる。Shader Passは全対象で共通化できるが、Mesh／Material等により各Phaseが複数Drawへ分かれることは許容する。

- Broadphaseでは分離Offsetと安全Marginを含む物体OBBの左右眼投影矩形を使う。重なる組だけ、表向きのOBB切断面から得たCap Bounds Polygonを左右眼へ投影して再判定する。どちらの判定も非交差なら安全という悲観的な証明として扱い、Near Plane交差、Raster／MSAA、頭部移動誤差を考慮してBoundsを保守的に拡張する。

- `CapCompatibilityKey`は順序を正規化した表示対象`CutPlaneId`列、Side Mask、Offset、Material／Debug State、正規化済み`EffectiveStencilPolarity`から作り、Raw floatだけをHashの正本にしない。Polarity Unknownでは`StencilShellInstanceId`も含める。同じSlash由来でも、対象が別々に移動・回転した後は現在のWorld Planeをepsilon比較し、一致しなければ別Groupへ分離する。片方だけに追加Temporary Render Boundaryがある場合も互換ではない。`MaxAbsoluteWindingBound`は互換性そのものではなくStencilCountBatch／Color内の容量制約として別にchecked加算する。

- キャップの幾何可視性は元Object単位ではなく、`論理破片 × 切断面`の`CapRecord`単位で判定する。同じ切断面でも正負破片の断面Normalは逆向きになるため、片側が裏向きでも反対側を自動的に省略しない。一方、FixedによるStencil全体省略はCap pairではなく`LogicalCutOperation`単位で判定する。

- `LogicalCutOperation`は`CutOperationId`、`ParentLogicalFragmentId`、`ParentObjectGeneration`、その一回の切断で生成した全`DirectChildLogicalFragmentId`、全`CutBoundaryId`、作成時のSupportGraphGeneration、`OperationSupportState`、`FullyFixedCullInvalidated`を保持する。親LogicalFragmentは未切断Assetに限らず、過去の切断から生じたFragmentでもよい。子数が2でも3以上でも正負物体の1対1ペア、World Plane一致検索、Cap Coverage照合、一対多境界対応をPoCで行わない。

- 構築不変条件は`DirectChildCount`が2～64、`CutBoundaryCount`が1～256、各IDが0を予約した正の32bit `int`であることとする。`CutOperationId`、`LogicalFragmentLocalId`、`CutBoundaryLocalId`はそれぞれ`ObjectId`の生存期間内で一意かつ非再利用とする。`ParentObjectGeneration`と作成時`SupportGraphGeneration`は`uint`全域を有効範囲とし、現在の入力Snapshotと一致させる。親IDは既存Fragmentを指し、直接子IDは親と異なり相互に重複せず、境界IDも相互に重複してはならない。各境界は同じ操作の異なる2直接子を結び、全直接子は少なくとも1境界へ接続する。空の境界集合、子数0／1、重複ID、未知ID、世代不一致、上限超過は操作全体を原子的にRejectし、Fragment、Boundary、Operationを一つも公開しない。

- `OperationSupportState`は構築Validatorを通過した子数2以上のOperationにだけ導出し、三値へ固定する。直接子に`Unknown`が1つでもあれば`Incomplete`、Unknownがなく`Detached`が1つ以上なら`HasDetached`、全子`Anchored`なら`FullyFixed`とし、判定優先順位を`Incomplete > HasDetached > FullyFixed`とする。default値は`Incomplete=0`とし、未初期化状態をFullyFixed扱いしない。全子固定かどうかを別booleanへ重複保存せず、この三値だけを正本とする。

- `FullyFixedCullEligible = OperationSupportState == FullyFixed && !FullyFixedCullInvalidated`とする。Eligibleなら操作全体の`TemporaryRenderCapRecordSet`を空にする。`HasDetached`ならGeometry未Commitかつ`ExposureState != Suppressed`の全Cap Record、すなわちActiveと補助Dormantを集合へ入れる。`Incomplete`なら`ActiveTemporaryBoundarySet`に属する既知Active境界のCap Recordだけを入れ、Dormant補助CapとSuppressed境界を入れない。FullyFixedでもCull失効済みなら保守的にHasDetachedと同じ全非Suppressed規則を使う。Fixed同士の不要Capを個別除去せず、Buffer compaction、Cap Bounds Meshの部分更新、Draw List再構築を行わない。

- 後続切断で過去操作の直接子が親として置換・細分されることを検出したら、新しい子や境界を公開する前に、その直接子を生成した過去`LogicalCutOperation.FullyFixedCullInvalidated`を同一フレームで不可逆に`true`へする。過去のDormant境界をActive化する場合も、その境界が属する全過去OperationをActive化前に失効させる。PoCでは過去の直接子集合を子孫集合へ再構築せず、一度失効したFully Fixed Cullを再有効化しない。これによりActive化した過去境界が古いFullyFixed判定で隠れ続けることを防ぐ。後続切断自体は対象子Fragmentを親にする新しい`LogicalCutOperation`として独立に三値集約する。

- `HasDetached`またはCull失効済み操作で通常Batchへ残したFixed Capによる局所的な線状Z-fightingは許容し、実測で必要になった場合だけ将来の`CutBoundaryPatch`単位最適化を追加する。

- Dormant判定は描画最適化であり、Cut Plane、論理Fragment、ObjectGeneration、FixedSupportGraphの切断Edge、バックグラウンド表示Mesh／Convex処理を削除しない。後続切断、Anchor喪失、Constraint破断で対象成分がDetachedになった場合は、境界となる全Dormant Cutを再有効化する。

- `FullyFixedCullEligible`な操作を先に除外した後、`TemporaryRenderCapRecordSet`内の現在のWorld Cap Planeについて`dot(CapNormal, EyePosition - CapPoint)`を左右眼で評価する。両眼とも明確に裏向きのCapRecordを幾何不可視とし、片眼だけ表向きならSingle Pass Instanced用Recordを残す。互換Group内に幾何可視な実描画Capが一つもない場合も、Stencil Clear／Volume／Cap処理を丸ごと省略する。

- カメラが切断面近傍にある場合の左右眼不一致と頭部微動による点滅を避けるため、Facing epsilonと1～2フレーム相当のヒステリシスを候補とする。Frustum外判定も同じ段階で行うが、通常のclip済み破片カラー描画とShadowCasterは消さない。

- Cap処理順は`Support Connectivity更新 -> 置換直接子／Active化境界の祖先Operation Cull失効 -> 過去境界Dormant／Active再評価 -> OperationSupportState三値集約 -> FullyFixedCullEligible導出 -> ActiveTemporaryBoundarySet／TemporaryRenderCapRecordSet構築 -> TemporaryClipConstraintCandidateSetのstable選択 -> 両眼Frustum／Facing Cull -> EffectiveStencilPolarity導出 -> CapCompatibility Group -> 全Cap不可視Group Cull -> StencilCountBatch分割／Bound検証 -> Stencil Conflict Graph -> Greedy Coloring／Color Bound再検証 -> 選択済みStencil Volume／全Cap板描画`とする。Cull失効と境界Active化の順序を逆転させない。切断操作単位の固定長Child Support集約だけを早期判定に使い、Cap pair／Coverage判定や、描画対象操作内のCap Buffer compaction／Mesh部分更新はPoCで行わない。Plane容量超過はこの順序の選択済みStencil Volume submitだけへ作用し、後段のCap Record集合や前段の論理状態を変更しない。

- PoCは単純な全組み合わせ`O(M^2)`とGreedy Coloringを使用する。Pending対象数が増えてCPU費用が問題になった場合だけ、スクリーングリッド／Sweep and Pruneへ置換する。最小彩色は求めない。

- 可視Cap Boundsを`Residual Stencil Support`の保守的上界として使用する前提は、検証済みOriented Closed Cut Shell、共有Edgeのbit一致、Front／Backで対称なclip、相殺を妨げないDepth／Stencil設定、十分なRaster／MSAA Marginである。Self-intersectionや逆向きCoincident FaceによるMask内のWinding 0自体は、このSupport上界を破らない。未相殺Boundary／T-junction、Near Plane、カメラ内部、片面だけのDepth失敗などでCap外に非ゼロ値が残り得る場合は同一Batchへ入れず、安全なFallbackへ送る。

- Colorごとに深度を消去せず、専用Stencil Byteの全8bitだけを対象Rect／ScissorでZeroへ戻す。このPass期間は8bit全部をWinding Counterへ排他的に予約し、URPのほかのStencil用途と同時使用しない。Renderer Featureの注入位置または対象Depth／Stencil Attachmentで全8bitを確保できない構成は8bit経路を無効化してFallbackし、部分Bit Maskで255上限を装わない。Stencil Byteの恒久的な物体割当は行わない。

- 全対象が重なる最悪時にColor数がPending対象数まで増えることを許容しつつ、最大Color数とStencil GPU予算を設ける。超過時は遠距離／小画面対象のキャップ省略、単色VFX化、表示Mesh Job優先度引上げの順で品質低下し、誤ったStencilを描かない。

## 6. 表示メッシュ切断

### 6.1 入力と出力

| 区分 | 内容 |
| --- | --- |
| 入力 | posed頂点、法線、接線、UV、色、submesh、index、切断平面、`RenderCutTopologyMap`（未用意ならindexから保守的に生成）、論理破片ID、世代番号、`RenderCutRobustnessProfile` |
| 処理 | 頂点単位の符号分類、三角形clip、Original Edge単位の交点共有、Topology由来Contour Track構築、局所Cap三角形化 |
| 出力 | 正負破片Mesh、断面submesh、Bounds、切断由来Boundary閉鎖証拠、Cap経路／品質診断、任意の体積候補、コミット用メタデータ |

### 6.2 Unity実装方針

- C# Job SystemとBurstでアンマネージデータを処理する。

- 読み取りには`Mesh.AcquireReadOnlyMeshData`、生成には`Mesh.AllocateWritableMeshData`を用いる。ReadOnly `MeshDataArray`は保持中に元Meshを変更しなければ原則コピーなしのSnapshotとしてJobから参照し、複数Meshは一括取得してSafety Tracking費用を抑える。連続切断ではRendererのMeshを毎回再取得せず、Stable Render FragmentのNative Geometryと`RenderCutTopologyMap`を次世代の正本として引き継ぐ。Stencil用Cut Shellとは別の入力契約である。

- Jobは頂点、Index、Vertex Layout、SubMesh、Bounds候補をWritable `MeshData`へ出力する。頂点／Index数が事前に定まらない処理は`Count Job -> Native領域確保 -> Write Job`の二段階を基本とする。

- 完成データはGeneration検証後、メインスレッドで`Mesh.ApplyAndDisposeWritableMeshData`により`UnityEngine.Mesh`へ適用し、Renderer参照だけを描画境界でCommitする。重い頂点処理をメインスレッドへ戻さない。

- 切断に交差した論理破片だけを再構築し、物体全体の再処理を避ける。

- 頂点や辺の近傍を通る切断には後述の共通epsilon規則を用い、入力TriangleごとではなくTopology Vertexごとに分類を一度だけ確定する。

### 6.3 ランタイム表示Meshの最小入力契約

表示Mesh切断では、厳密なsolid Boolean、inside／outside、generalized winding number、outer／inner shell、入力全体のconnected component、duplicate／coincident surface除去、事前self-intersection検査を要求しない。Self-intersectionはbind poseだけでなくskinning後のposed surfaceにも許容し、正常なゲーム入力として扱う。Internal Geometry、Nested Shell、Disconnected Component、Duplicate Face、Coincident Face、Inconsistent Windingも、それぞれを独立したTriangle／Topology Trackとして切断・Capしてよい。結果に二重Surface、二重Cap、内部Cap、重複Triangleが残ることを許容する。

この節の「穴を残さない」は、各出力Fragmentについて切断処理が新たに作ったBoundary Half-edgeへCapがちょうど1つ以上接続され、切断由来の開口を残さないことを意味する。切断前から存在し、切断影響帯へ入らないBoundary Edgeまで自動修復する意味ではない。出力Mesh全体のGlobal Watertightを要求する用途は、入力をWatertightにするか別のBoundary Repair工程を通す。表示Meshの許容条件からStencil Capの成立条件を導出せず、Stencilは5章のCut Shell契約とT-067で独立に判断する。

| 層 | 条件 |
| --- | --- |
| 必須 | plane、posed position、使用する属性が有限。index、submesh range、Topology参照が範囲内。固定上限内でCount／Writeがoverflowしない。同一Job中にTopologyと入力Bufferが変化しない |
| Fast Path | 切断影響帯のContour Portがすべて次数2でclosed cycleを作り、各cycleの平面射影がsimple polygon。切断に触れる既存Boundaryがなく、曖昧なNon-manifold Edge Sheet分岐がない |
| Fallbackで許容 | 閾値以下のDegenerate Triangle、切断に触れない既存Boundary、局所Non-manifold Edge／Vertex、Duplicate／Coincident Face、Disconnected／Nested Shell、Inconsistent Winding、3D self-intersection、2D self-intersecting Cap Contour、短Edge、同一点の複数Topological Port |
| Commit不能 | NaN／Inf、範囲外index、壊れたTopology参照、同一Topology Edge内で有限な交点を作れない、局所Trackを閉じるFallbackが上限内で完了しない、配列／件数／時間予算超過。結果をCommitせず即時Rendererを維持し、再処理またはProxyへFallbackする |

| Mesh健全性項目 | ランタイム表示Mesh切断での扱い |
| --- | --- |
| Degenerate Triangle | 許容。posed geometryで閾値以下をCount段階に除去し、局所Contourが開けばOpen Chain fallbackへ送る |
| NaN／Inf | 原則不許可。参照頂点、plane、必須属性のいずれかで検出したJob結果をCommitしない |
| Boundary Edge／Non-watertight | 切断影響帯外なら許容し検査対象を局所化する。切断Contourへ接続する場合はOpenChainBridgeを試し、元Boundary自体は修復しない |
| Non-manifold Edge | 2面を超えるedge-useをcut-local laneへ分けられる場合は許容。分岐／未対応portを上限内で閉じられなければ該当結果をCommitしない |
| Non-manifold Vertex | incident edge topologyを独立Vertex Fanへ分けられる限り許容。edge topologyが正常なfan同士を同一点という理由で統合しない |
| Duplicate Face | 許容。TriangleInstanceIdを別に保ち、重複Contour／Capも保持できる |
| Disconnected Component | 許容。事前component分類を要求せず、得られたTopology Trackを独立処理する |
| Inconsistent Winding | 許容。side分類とContour接続にface windingを使わず、Cap向きはplaneと出力sideから生成する |
| Self-intersection | 原則許容。posed Mesh全体を事前検査せず、生成された各Cap Trackの2D交差だけを局所検査する |
| Internal Geometry／Nested Shell | 許容。外殻／内殻を分類せず各Trackを独立Capし、内部Capと重複領域を許容する |
| Coincident／Overlapping Face | 可能な限り許容。Topology系譜が別なら同じ座標でも別Trackとし、二重surface／capを許容する |

Degenerate Triangleは入力拒否理由にせず、posed positionを用いて`twiceArea <= AreaEpsilon`ならCount段階で除去または非寄与として扱う。除去後のedge-useとContour Portだけで局所Topologyを構成し直す。Duplicate Faceは除去せずTriangle Instance IDを保つ。面向きはside分類、Contour接続、Cap領域判定へ使用せず、生成Capの向きは切断平面と出力sideだけから決める。通常Capは5.3節と同じ片面トゥーンMaterialを使い、正負Fragmentで逆向きの生成法線とwindingを与える。`BoundaryFan`／`OpenChainBridge`／`DegenerateClosure`で片面欠落が実測された場合だけ、該当Cap Recordを両面描画または同位置の逆向き重複Triangleへ降格できる。元faceのwinding不整合をCapへ伝播させず、通常カラーPass全体を常時`Cull Off`にはしない。

### 6.4 Topology系譜による交点共有とContour接続

`RenderCutTopologyMap`は少なくとも安定した`TopologyVertexId`、`OriginalEdgeId`、`TriangleInstanceId`、各Triangle Cornerの`EdgeUseId`を持つ。必要に応じて、attribute seamで分裂したRender Vertexを同じTopology Vertex／Original Edgeへ対応付ける`CutPositionId`、Non-manifold Vertexの独立fanを表す`VertexFanId`、Non-manifold Edgeの局所surface laneを表す`EdgeSheetLaneId`を持てる。Half-edge、edge hash、圧縮adjacencyのどれで保存するかは性能測定で選べるが、以下の識別と接続結果を変えてはならない。

Topology Mapはbind poseまたはImport時のindex topologyから作り、skinningではpositionだけを更新してIDを維持する。FBX control pointや前処理Topologyを取得できる場合はUV／Normal seamを越えて同じOriginal Edgeへ対応付ける。同じ`CutPositionId`を持つseam vertexのposed positionは、同じskin weight／bone transformから1回だけ生成したcanonical positionを参照する。外部入力が同じCutPositionIdへ異なるpositionを与えた場合は平均やepsilon weldをせず、Topologyを保った分離が可能なら別IDへ降格し、不可能ならCommitしない。Topology MapがないRuntime Meshはunordered index pairからOriginal Edgeを構築できるが、別indexのseamを空間位置だけでweldしない。その場合、seamは既存Boundaryとして扱い、Global Watertightを保証しない。

厳密にplaneを横切るOriginal Edgeの交点positionは、orderedなTopology endpoint、共有済みsigned distance、固定式からOriginal Edgeごとに一度だけ計算・量子化する。全incident Triangleとattribute seamのEdge Useは同じ`CutPositionId`を参照し、UV、Normal、Tangent、Colorは各Edge Useのcorner属性から個別補間できる。これにより同じOriginal Edgeを共有する隣接Triangleが別々のfloat演算で位置の異なる交点を作ることを禁止する。

Contour Graphのnodeは3D位置ではなく`ContourPortKey`で識別する。通常のedge crossingは`OriginalEdgeId + EdgeSheetLaneId`、plane上の元vertexを通る場合は`TopologyVertexId + VertexFanId + LocalPortOrdinal`を用いる。triangleが生成したcut segmentは`TriangleInstanceId`を持つGraph edgeとし、元Triangleのedge-use adjacencyだけで次segmentへ接続する。座標一致、epsilon内、nearest segmentだけを理由に異なるContourPortKeyをmergeまたは接続してはならない。同一点に別surface由来の複数portが存在しても別nodeのまま保持する。

Original Edgeのincident Edge Useが2ならFast Pathの1 laneとする。1なら既存Boundary endpoint、3以上ならcut-local Non-manifold fallbackへ送る。Fallbackは同じOriginal EdgeのEdge Use集合内だけで、既存`EdgeSheetLaneId`、Vertex Fan、Material／Submesh hint、Triangle Instanceのstable順を使って決定論的なlaneへ分解できる。異なるOriginal Edgeや別Topology Componentを位置近傍で結ばない。局所分解後にclosed Trackを作れる限りNon-manifold Edgeを許容し、奇数fan、未対応Edge Use、分岐が残るTrackだけをOpen Chain fallbackまたはCommit不能へ送る。Non-manifold Vertexはincident edge topologyがfanへ分けられる限り許容し、vertex座標だけでfanを統合しない。

### 6.5 Signed Distanceと切断固有の退化規則

正規化済みplaneと全finite posed positionからObject単位Bounds diagonalを求め、`DistanceEpsilon = max(AbsoluteDistanceEpsilon, RelativeDistanceEpsilon * BoundsDiagonal)`、`LengthEpsilon`、`AreaEpsilon`を`RenderCutRobustnessProfile`から1回だけ確定する。各Topology Vertexのsigned distanceはbinary64で一度だけ評価して保存し、`d < -DistanceEpsilon`をNegative、`d > DistanceEpsilon`をPositive、それ以外をOnPlaneとする。同じTopology Vertexを参照する全Triangleはこの保存値と分類を再利用し、Triangle単位の再計算を禁止する。

Topology判断用のsymbolic sideは`OnPlane -> Positive`へ固定する。これは幾何positionを動かす処理ではなく、vertex／edge／coplanar caseの所有sideを一意にするtie-breakである。Strict Positive／Negativeのedgeだけ内部交点を作り、OnPlane endpointとNegative endpointのedgeは元OnPlane vertexを交点として使う。interpolation parameterはordered endpointの保存distanceから固定式で計算して`[0,1]`へclampし、非有限化を拒否する。

| ケース | 規則 |
| --- | --- |
| planeがvertexを通る | Topology Vertexの共有OnPlane分類とVertex Fan別Contour Portを使う。位置が同じ別Topology Vertexをmergeしない |
| planeがedgeを通る | 両endpoint OnPlaneのedgeは交差Edgeとして再計算せずPositive所有とする。隣接Triangleの第三頂点がNegativeで生じるcut segmentだけをTopology Trackへ追加する |
| triangleがcoplanar | 全頂点OnPlaneならPositive側へ1回だけ保持し、Cap segmentを生成しない。両側複製はOptional Debug Profileに限定する |
| intersection segmentが短い | 同じTriangle Instance／Contour Track内で`Length <= LengthEpsilon`なら同じportへcollapseする。別Topology Trackの近接segmentとはmergeしない |
| skinned triangleが極小 | `twiceArea <= AreaEpsilon`なら入力から軽量除去し、edge-use寄与も除く。除去によりTrackが開いた場合はOpen Chain fallbackへ送る |
| 生成edgeが短い | source port系譜が同じ場合だけcollapseする。位置だけが近い異なるportは重複edgeとして保持する |
| 同一点に複数port | 別node、別intersection vertexまたは同positionのduplicate vertexとして保持し、各Trackを独立Capする |

### 6.6 Cap Fast Pathと局所Fallback

ContourはTopology Trackごとに処理し、Mesh全体のself-intersection broadphaseを実行しない。次数2のclosed Trackをplaneの固定直交basisへ射影し、非隣接segmentの2D交差がなく、3点以上の非共線positionを持つ場合はsimple contour Fast Pathとしてlinear／near-linearなear clippingまたは同等の固定三角形化を使う。複数Trackの包含関係、outer／hole、shell内外を推定せず、Nested／Internal Trackもそれぞれ独立に埋めるため重複Capを許容する。

2D self-intersection検査は実際に生成された各Trackだけへ行い、AABB binまたはsweepで非隣接segment候補を調べる。異なるContour Track同士が交差、重複、coincidentでも互いをsplit／mergeしない。同一Trackがself-intersectする場合だけ、予算内ならsegment交点で分割したplanar graph／arrangementを作り、bounded faceをeven-oddで選んで三角形化する。元Meshのwindingと3D inside／outsideを使用しない。

Arrangementが数値的に曖昧、coincident segmentを含む、または工程予算へ近づいた場合は`Boundary Fan Fallback`を使う。各closed Trackに固定候補順で選んだplane上anchorを1つ置き、各有向boundary segmentに対して`anchor, vi, vi+1`を1枚生成する。Anchorが外部にあってもよく、重複／交差Triangleを許容する。このfanは幾何学的な領域正解ではなく、各切断Boundary EdgeへCap Triangleを接続して見た目の穴を塞ぐことを目的とする。Anchor edgeはTrack内で対になり、Track間では共有しない。全点がほぼ共線、重複して面積を持たないTrackではdegenerate closure triangleを許容し、診断へ記録する。

既存Boundaryへ到達したOpen Chainは、切断に関係しない既存Boundaryを位置探索して全面修復しない。同じTopology Trackから得たchainの両端だけをplane上のsynthetic chordで閉じ、`OpenChainBridge`として同位置・逆向きの2枚のBoundary Fanを生成できる。これによりsynthetic chordはCap同士で少なくとも2 incidenceを持ち、元のcut-derived chainはsurface 1枚＋重複Cap 2枚の局所Non-manifold edgeとなるがBoundaryにはならない。二重Cap／二重surfaceを許容する本用途では、誤った別surfaceへの空間接続よりこの冗長閉鎖を優先する。この処理は切断由来開口を局所的に塞ぐが、元からある穴をGlobal Watertightにしない。endpointが3個以上へ分岐し、Topology lane内でchainを一意に分離できない場合、またはchord／fan上限を超える場合はそのFragment GeometryをCommitしない。

### 6.7 Commit検証と品質低下

Count／Write後は、入力全体のSolid妥当性ではなくcut-local不変条件だけを検証する。各出力の切断由来Boundary Half-edgeが対応するCap boundaryへ接続されていること、Track内の非synthetic内部Cap edgeが規定の偶数回現れること、全indexが有効でpositionがfinite、件数が事前Count以下、Generationが一致することを要求する。Duplicate／Coincident Capによるedge multiplicity、元から存在するBoundary、元surfaceのself-intersection／winding不整合はReject理由にしない。

結果は`SimpleContour`、`LocalArrangement`、`BoundaryFan`、`OpenChainBridge`、`DegenerateClosure`、`Uncappable`の`CapConstructionPath`と、除去Degenerate数、Non-manifold lane数、Open Chain数、2D交差数、重複Cap Triangle数を持つ。`Uncappable`、非有限、Topology破損、予算超過では古いStable Geometryを維持して即時clip／Stencilとは独立した表示Fallbackを継続し、Voxel化や全Mesh修復を命中フレームの同期経路へ入れない。

## 7. 物理切断

### 7.1 一時状態

ColliderのBake／cookingは視覚切断と初回の破片別運動のクリティカルパスに含めない。`Active`な切断境界は命中フレームからclipと仮断面を表示し、支持分類と固定容量が許可する場合は、既存のcook済みConvex Geometryを再cookせず再利用した`Provisional Rigidbody`へ各Logical Fragmentを所属させる。`Dormant`境界は単独では即時表示や運動を要求しないが、Operation規則による補助Cap描画を妨げない。支持がUnknown、世代不一致、Provisional Actor／Shape／Constraint容量超過、または原子的構築失敗なら、D-068由来の単一Rigidbody／旧Collider `FragmentGroup`を保守Fallbackとして維持し、部分的なProvisional分裂を公開しない。

- 刀は旧Colliderを含む物理Colliderへ接触させず、Edge Direction Gate成立中の論理SweepだけでHitを判定する。プレイヤーの手・身体も初期仕様ではプロップ／破片へ接触Impulseを与えず、移動制限と視界保護は7.2.3の非接触Locomotion経路で扱う。

- 全Logical Fragmentが`Detached`と確定した通常経路は`ProvisionalPhysicsSplit`、1つ以上が`Anchored`で残りが既知なら`ProvisionalAnchoredSplit`へ入る。物理ステップ境界で各物理子につき1 Actorを原子的に作り、Detached子はDynamic、Anchored子はStatic／Kinematicまたは元の固定Constraintへ接続する。両側AnchoredでDormantな操作は不要なActor分裂を省略できる。構築前提を満たさない場合だけ`PendingPhysicsSplit`／`PendingAnchoredSplit`へ入り、従来の1 Rigidbodyと旧Colliderを正本として外力をGroup全体へ適用する。

- Provisional Shapeは元Actorのcook済みConvex Geometryとlocal poseを共有参照し、Provisional生成のためのConvex切断、Mesh複製、`Physics.BakeMesh`を行わない。既存Convexの全頂点が新Cut Planeの片側にある場合はその側の子だけへShape Instanceを割り当て、平面と交差するかepsilon内／分類不能なら両側へ割り当てる。連続切断では現在Provisional Actorが参照するShape集合へ同じ規則を適用し、Geometry Resourceを再利用する。Geometry共有がBackend上で安全に成立しない場合は同期cookへ落とさず単一FragmentGroup Fallbackを使う。

- 共有Geometryごとに`ProvisionalCollisionResourceLease`を取得してからShape Instanceを構築し、全Actor／Shape／Constraint／Lease取得成功後だけ新物理状態を公開する。失敗時は逆順rollbackし、旧Actorを維持する。Final Shape交換または新世代Provisional置換では旧ShapeをPhysics Sceneから除去し、参照する全Actorの物理ステップが完了した後だけLeaseを返す。最後のLease返却前にCooked Geometryを破棄せず、連続切断、Generation Reject、Timeoutでも二重返却しない。

- 同じProvisional分裂系譜のSibling Actor間はCollision responseを無効化するが、それ以外の静的／動的WorldとのCollisionは通常どおり全て有効にする。交差Convexの複製により、表示より早い接触、Sibling側へ張り出すGhost Contact、同じ外部物体から複数Actorへの接触、短時間のImpulse重複を許容する。これらを切断失敗またはSolverへ投入禁止な初期penetrationとはみなさず、非finite state、Profile上限を超える速度／角速度、Constraint破綻だけを公開後Fault Frozen対象とする。

- 同じ切断で生じたProvisional Sibling間には、相対回転と切断面接線2軸を保持し、法線方向の分離を許可しつつ切断直後より深い再侵入を防ぐ短命な`ProvisionalSeparationConstraint`を持たせる。D6 Joint、速度射影、相対Pose補正等の実装はT-091で比較し、PoCは最も単純で安定する方式を選ぶ。Constraint生成失敗時に一部Actorだけを公開しない。

- 公開済みProvisional GroupはGroup単位の固定容量二重Buffer`ProvisionalLastFinitePhysicsSnapshot`を持つ。各Slot Headerは`ObjectId`、`ObjectGeneration`、既存物理Clockの`FixedStepId`、`ActorCount`を、Entryは対応する正の`LogicalFragmentLocalId`昇順のWorld pose、COM線速度、角速度を持つ。Fixed Step完了後に非公開Staging Slotへ全Actorを同じStepから収集し、Actor集合、ID順、世代、件数、全数finiteを検証した後だけ、公開Slot indexを1回のatomic storeで切り替える。途中失敗、Fault検出、Actor集合変更、世代変更ではStagingを破棄して直前の完全な公開Slotを維持し、Actorごとの部分更新を公開しない。Provisional Actor集合の初回公開も全Actor分のStep境界Snapshot作成成功と同じ原子的Commitへ含め、2 Slotは同じGroup内で交互に使うがGroup破棄までは別Groupへ再割当しない。

- いずれかのActorで非finite state、線速度／角速度のProfile上限超過、またはConstraint runtime破綻を検出した場合は、対応`LogicalFragmentLocalId`昇順に全Faultを収集し、固定優先順位`NonFiniteActorState > ConstraintRuntimeFailed > LinearVelocityLimitExceeded > AngularVelocityLimitExceeded`、同順位では最小LogicalFragmentLocalIdの原因をPrimary Faultとする。進行中のSnapshot Stagingを必ず破棄し、次の物理ステップ境界でGroup全体を最後に公開済みの完全なGroup Snapshotへ復元して`ProvisionalFaultFrozen`へexactly onceで遷移させる。全Sibling Constraintを外し、全ActorをKinematic化し、速度／角速度0、蓄積Force／Torque消去をGroup単位で原子的に行う。Faultを検出した現StepのActor値は、全数finiteに見える場合でも封じ込め入力に使用しない。完全Snapshotがない、または全Actorの原子的封じ込めを事前検証できない場合はGroup全Actor／ShapeをPhysics Sceneからまとめて除外し、完全Snapshotがあれば表示をそのGroup姿勢へ残し、なければ全該当表示を非表示にする。部分Kinematic化、個別Actorだけの復帰、旧Groupへの再合流を行わない。

- `ProvisionalFaultFrozen`ではGeometry／表示Meshの背景処理だけを継続できるが、新しいProvisional分裂、Final Collider handoff、Dynamicへの自動復帰、切断Impulseを禁止する。到着済みまたは後着の物理成果物はCommitせず回収し、Cooked Geometry LeaseはFrozen Actor／ShapeをSceneから除去する最終破棄まで維持する。Primary Faultは`ProvisionalRuntimeFaultReason`、封じ込め結果は独立した`ProvisionalFaultContainmentDisposition`として確定し、原因を結果で上書きしない。このSnapshotは物理安全のFail-closed専用であり、表示―物理誤差の蓄積、補間、すり合わせには使用しない。

- FragmentGroup内に支持分類未完了、世代不一致、または接続が曖昧なLogicalFragmentが1つでもあれば、Group物理状態は`PendingSupportClassification`へ入る。この状態では旧Rigidbody、Collider、ConstraintおよびTransformを変更せず、Provisional Actor生成、Group全体の分離Offset、切断Impulse、自由側解析運動を禁止する。一方、境界単位の描画判定は独立して維持し、`Active`と確定済みの境界ではclip、Stencil、仮Cap、非運動の切断演出を許可し、`Suppressed`境界ではすべての即時切断表示を禁止する。支持再分類と背景Geometry処理を進め、全LogicalFragmentが既知になった時点でProvisional分裂を原子的に試み、失敗時は単一Group Fallbackへ遷移する。再分類不能が予算時間を超えて継続する場合は、保守的な未分裂Fallbackを維持してTraceへ記録し、同期的な重いGraph処理やcookでフレームを停止させない。

- 地面、壁、建物基礎などへ固定された対象は、分離運動または切断Impulseを適用する前に`FixedSupportAnchor`を切断平面の正負半空間へ分類し、必要最小限の接続判定を完了する。既知かつ容量内なら`ProvisionalAnchoredSplit`、構築不能なら`PendingAnchoredSplit`へ入る。いずれも固定側または固定側を含む共有旧Rigidbody／旧Colliderへ切断Impulseを与えてはならない。

- 単一の連結Convexと1個以上のFixedSupportAnchorだけで表せる対象は、各Anchorについて`dot(planeNormal, anchorPosition) + planeDistance`の符号を評価するだけで固定側を決める。正側だけにAnchorがあれば正側固定、負側だけなら負側固定、両側なら両側固定、どちらにもなければ通常の自由分裂とする。平面から`anchorEpsilon`以内のAnchorはPoCでは保守的に両側固定として扱い、破断可能な固定具は後続仕様とする。

- Compound Convex、建物チャンク、複数支持部を持つ対象は、プリプロセス済み`CutConnectivityGraph`へFixedSupportAnchorをRootとして付加した`FixedSupportGraph` Viewを使用する。ComponentFragment／Physics Proxy／構造チャンクをNode、SurfaceAdjacency／AttachmentPatch／構造接続をEdgeとして保持し、切断面で失われるEdgeを除いた後にRootから到達可能なGraph成分を固定、到達不能な成分を自由と分類する。これは完全なConvex B-rep切断、質量特性計算、`Physics.BakeMesh`より先に行う軽量判定である。

- `ProvisionalAnchoredSplit`では固定側の表示Offset、速度、切断Impulseを0とし、自由側だけをDynamic Provisional Actorとして分離する。自由側の旧Shape過大被覆による早い接触とGhost Contactは許容する。Provisional構築に失敗して`PendingAnchoredSplit`へ落ちた場合だけ、元の未切断Colliderを固定状態のまま残し、自由側を衝突なしの解析表示で分離できる。切断幅は0であり、両側固定なら切断をDormantにして即時分離を見せず、どちらにも分離Impulseを与えない。

- FixedSupportGraphは最新の1切断面だけでなく、現在ObjectGenerationへ蓄積された全切断面で区切られた論理破片ごとにAnchor到達性を再評価する。例えば建物の最初の縦切断で両側が基礎へ接続していればDormantのままとし、交差する2面目によってAnchorなしの部品が初めて生じた時点で、その部品に接する1面目と2面目の断面を同時に可視化して分離する。

- FixedSupport分類は命中フレーム内で完了する固定長処理を目標とし、少数Anchorの半空間分類は同期実行してよい。SlashFrameと候補対象の未来姿勢が命中前に確定している場合は投機評価し、実命中、ObjectGeneration、Anchor／Graph世代、切断面の一致をCommit条件とする。不一致または未完了時は対象境界を`Suppressed`として全即時表示と運動を抑止し、再分類へ送る。

- 断面間の小さな見た目上のめり込み、見えている切断隙間に旧Colliderが残ることに加え、Provisional Shapeが表示Fragmentより張り出してめり込む前に外界へ衝突することを許容する。違和感とBroadphase拡大を限定するため、Provisional中の分離距離とConstraint法線移動には物体寸法と想定Impulseに基づく上限を設ける。Kerfは常に0であり、仮分離Offsetとは別パラメータとする。

- 後続の斬撃Hitと幾何切断はProvisional／旧Colliderではなく、Pending Cutを適用した論理破片とCut Shellを参照する。先行cook完了を待たず現在のLogical Fragmentを再切断し、旧世代成果物はGeneration Rejectする。新世代Provisional構築は現ActorのPose／速度と共有Shape参照を基底に原子的に置換し、Actor、Shape Instance、Constraintの固定容量を超えた場合は新しい子だけを部分公開せず現物理Groupを維持する。

- Convex生成と`Physics.BakeMesh`をバックグラウンドで完了させ、成果物と世代が有効なら各Provisional ActorのCollider、mass、center of mass、inertiaをFinal値へ物理ステップ境界で原子的に置換する。単一Group Fallbackの場合だけ、このCommit時に複数Rigidbodyへ初分裂する。Bakeの遅延や失敗は即時表示または有効なProvisional運動を巻き戻す理由にしない。

- Collider差し替えとRigidbody分裂は物理ステップ境界で行う。

- Pending／Provisionalが予算時間を超えた場合はTraceへ`PhysicsSplitTimeout`を記録してcookをConfirmedPhysics優先度へ引き上げ、現時点で有効な単一GroupまたはProvisional Actorを維持する。無効なFinal Convexは簡易Proxy、Compound Primitive、または非物理デブリへ品質低下させ、メインスレッドで同期cookしてフレームを停止させない。Provisional状態を短命とみなすProfile期限、Actor／Shape／Constraint上限、異常速度上限はT-091後に校正し、期限超過だけを理由にPose／速度を巻き戻さない。

### 7.2 Convex切断と運動継承

- 凸多面体を切断平面でクリップする。結果の正負側も凸となる。

- Physics ProxyのwatertightなConvex B-repをNative形式で保持し、頂点の正負分類、各面のPolygon clipping、交点／切断面Polygon生成、重複頂点統合、凸性・閉性検証、体積・重心・慣性計算をJob＋Burstで行う。一般凸包の再計算は原則行わない。

- 出力数が不定なため、`ConvexCountJob -> Native領域確保 -> ConvexWriteJob -> ValidationJob`を基本Pipelineとする。多数破片は同種段階をBatch化し、1破片ごとの極小Job乱発を避ける。

- 交差するConvexだけを切り、片側に完全にあるColliderはそのまま該当破片へ移す。

- 質量特性のRuntime正本は表示MeshやStrict Solid Cut Meshではなく、切断対象のPhysics Convex B-repとする。表示Triangle全体の体積積分、Convex同士のBoolean Union、重複領域の厳密な控除は行わない。

- 切断前の各Physics Convexはbinary64でfiniteかつ0以上の`PhysicsConvexMassWeight`を持つ。同一FragmentGroupのConvexを`LogicalConvexFragmentLocalId`昇順へ並べ、IEEE 754 binary64の左畳みで`weightSum = (((0 + w0) + w1) + ...)`を求める。加算の再関連付け、並列Reduction、FMAによる式変更をCommit用結果では禁止し、各入力と各中間和がfinite、最終`weightSum > 0`、親Rigidbody質量がfiniteかつ正であることを必須とする。各Convexの配分質量は`assignedMass_i = parentMass * (weight_i / weightSum)`とする。Weight 0のConvexは衝突形状として保持できるが質量・慣性項へ寄与せず、全Weight 0、非有限、加算overflowではFinal分裂Commitを禁止して現有効物理状態を維持する。

- Provisional Actorの一時質量は、Cap Bounds等で既に必要な現在Source Actorの保守的OBBと今回のCut Planeだけを使う固定長切断から求める。OBB正負側のfiniteかつ非負な近似体積を`V+`／`V-`とし、両側にDirect Childが存在して`V+ + V-`がfiniteかつ正なら`M+ = budgetMass * (V+ / (V+ + V-))`、`M- = budgetMass - M+`の固定順でSide Budgetを作る。OBB体積が全0、非finite、または演算不能なら存在する正負Sideへ等分し、片側だけなら全量をその側へ残す。同一Sideに複数のDisconnected Direct Childがある場合はLogicalFragmentLocalId昇順でSide Budgetを等Weight分配し、最後のChildを`sideBudget - precedingMassSum`として丸め残差を吸収する。蓄積全Cut PlaneでOBBを再clipせず、連続切断は現在ChildのCanonical Mass Budgetと現在OBBへこの一段処理を繰り返す。正でないChild mass、非finite、underflowで有効質量を作れない場合はProvisional分裂せず単一Group Fallbackへ送る。

- Provisional center of massはclip済みOBBの近似重心、inertiaはその保守的OBB／AABB箱慣性を割当質量で求め、近似が非finiteなら直前Actor inertiaを`provisionalMass / sourceMass`でscaleする。これらは短命なSolver用近似であり、Gameplay上の質量正本または次世代の`PhysicsConvexMassWeight`入力に使用しない。別途immutableな`CanonicalMassBudget`として切断直前の正規親質量とWeight系譜を保持し、Final Commitは必ずそこから本節の正規計算を行う。連続Provisional切断では現在Childへ割り当てたCanonical Mass Budgetをさらに分割し、一時Actor massから正本を作り直さない。

- Compound Convex同士が重なっていても生のConvex体積を単純加算して親質量を決めず、重複領域を二重計上しない。Weightは接触Geometryの体積そのものではなく、親の質量を保存しながら各Convexへ割り当てる物理近似Metadataである。

- 切断後はRigidbodyの動的／固定予定にかかわらず、各物理Commit対象Fragmentが所有するConvexだけを同じLocal ID順とbinary64左畳みで再集計し、`fragmentWeightSum`がfiniteかつ正であることを原子的Commitの必須条件とする。Weight 0のConvexを複数保持できるのは、同じFragment内に正のWeightを持つConvexが1個以上ある場合だけである。

- `fragmentWeightSum == 0`の子は質量0のRigidbody／任意の最小質量を生成しない。その子に属する全RenderFragmentが既存のMicro／Debris安全条件を満たし、FixedSupportAnchor、Gameplay重要部品、`Ambiguous`／`PreserveFallback`を含まない場合だけ、子全体を非物理デブリとして不可逆に消去できる。Weight 0なので他の子へ質量移送は行わない。それ以外は正WeightのSiblingを含むCut Operation全体のFinal物理Commitを拒否し、単一FragmentGroupまたは有効なProvisional Actor集合を維持する。部分的なFinal Rigidbody／Collider Commitや実装固有の質量再配分を禁止する。

- 非交差ConvexのWeightは所属する子Fragmentへそのまま継承する。交差Convexは、そのConvexに割り当て済みのWeightだけを正負の出力Convexの有効体積比で分ける。体積比は`positiveVolume / (positiveVolume + negativeVolume)`とその補数を、正側、負側の固定順binary64加算から求め、両体積がfiniteかつProfileの`epsVolume`より大きいことを要求する。複数世代の切断でも子孫Weightの合計を親Weightと一致させ、最終的な全物理Fragmentの質量合計を切断直前Rigidbodyの質量と一致させる。非有限体積、体積和0、演算overflow、許容誤差外の質量不一致では正確経路をCommitしない。

- 出力の片側体積が`epsVolume`以下で、その側の全RenderFragmentが既存のMicro／Debris安全条件を満たす場合は、極小側を物理Fragmentにせず消去し、交差前ConvexのWeight全量を反対側へ継承する。極小側が重要、大型、Ambiguous、または消去可否未確定ならWeightを恣意的に再配分せず、現有効物理状態を維持する。これにより極小体積除算を避け、消去した破片用のFinal Rigidbodyを生成しない。

- 各出力Convexはbinary64で`convexVolume`、局所重心、密度1の局所慣性`I_unitDensity`を計算する。正のWeightを持つConvexでは`convexVolume > epsVolume`を必須とし、`densityScale = assignedMass / convexVolume`、`I_assigned = I_unitDensity * densityScale`で割当質量へ変換する。`I_unitDensity * assignedMass`とはしない。1つの物理Fragmentを構成する全ConvexをLocal ID順の質量加重平均と平行軸の定理で合成して`centerOfMass`と慣性テンソルを得る。重なったConvexの重心・慣性もWeight付きCompound近似として受理し、厳密なUnion Solidの質量特性とはみなさない。

- Physics Proxyを持たないMicro Attachment／表示専用小部品には独立質量を作らない。その寄与は前処理時にHost側ConvexのWeightへ含めた近似とし、消去時にも極小Rigidbodyや質量移送を発生させない。

- Final質量特性の品質低下順は、`Convex多面体の正確な局所積分 -> ConvexごとのOBB箱慣性のWeight付き合成 -> Fragment全体OBB／AABB箱慣性`とする。Weight和0／非finite、親質量不正はGeometry近似では修復せず、現有効な単一FragmentGroupまたはProvisional Actor集合を維持する。正のWeightを持つConvexの体積不正または局所慣性不正だけをOBB以下へFallbackできる。下位経路でも同じassignedMass、親質量保存、finite、正の主慣性、決定的な軸規約を必須とし、同期Render Mesh積分やStrict Solid生成へFallbackしない。どの段階も成立しなければ現物理を維持してTraceし、Final Commitを遅延または拒否する。

- Parent ActorからProvisional Actorを初めて作る時だけ、速度継承の正本点を`FragmentRenderAnchor`とする。Source ActorのCOM線速度から`v_anchor = v_sourceCOM + omega_source x (anchor - COM_source)`を求め、`v_provisionalCOM = v_anchor + omega_source x (COM_provisional - anchor)`、`omega_provisional = omega_source`を設定し、切断命中時の表示Fragment poseとAnchor点速度を連続させる。単一Group FallbackからFinalへ初分裂する場合も同じ初回分裂式を使用する。質量変更前後の運動量、角運動量、運動エネルギー保存は要求しない。

- ProvisionalからFinal Colliderへのhandoffでは物理Actorを正本とし、ActorのWorld pose、COM線速度、角速度をそのまま維持して、Final Shape、center of mass、inertiaだけを同一Actorへ置換する。Render Anchorを維持するためのActor pose補正や、新COMに合わせた線速度変換を行わない。Final Geometryは各Source Provisional Shapeの切断結果として同じFragment Physics Frameに保持し、由来Convexのhalf-space内に`FinalContainmentEpsilon`付きで収まることをCommit前に検証する。証明不能、許容外への張り出し、local frame不一致ではFinalを公開せずProvisionalを維持する。これによりFinal Collider自体を瞬間移動させて外界へ再penetrationさせる経路を作らない。表示GeometryはActorへ従属し、local origin／frame差によりFinal Commit時に瞬間的な位置・姿勢差が出ても許容する。分離ImpulseはProvisional生成時に一度だけ加え、Final Commitで重ねて再適用しない。単一Group FallbackからFinalへ初分裂する場合だけCommit時に小さな分離Impulseを加える。Final Shape交換直後のSibling pairは既存の一時衝突抑止を使用できるが、外界とのGhost Contact履歴を理由にpose／velocityを巻き戻さない。

- `PendingAnchoredSplit`のCommitでは、Anchorから到達可能な破片を静的／Kinematicまたは元の固定Constraintへ残し、到達不能な自由破片だけにRigidbody、継承速度、分離Impulseを与える。複数Anchorが切断面の両側へ残る場合は両側を固定し、接続グラフ上で自由と証明できない破片へImpulseを与えない。

- 表示用MeshとCollider用Meshを分離し、Collider cooking用形状は低頂点・閉形状に保つ。

#### 7.2.1 独立閉Componentと接続Graph

標準Runtime経路は、Intersect／Overlapする部品を事前Boolean Unionした単一Strict Solidを要求しない。各部品を独立して閉じられる`ClosedCutComponentSet`として保持し、任意平面でComponentごとに切断・Capする。Component同士の内部Surface、二重Surface、二重Cap、重複体積は許容し、表示、Stencil、PhysicsのいずれでもRuntime幾何Unionを行わない。

- `CutConnectivityGraph`のNodeは現在世代の`ComponentFragment`または対応するLogical Convex Cellとし、Edgeは同一Component内の`SurfaceAdjacency`と、別Component間の`AttachmentPatch`から構成する。親子関係は生成履歴だけに使用し、物理的な接続性の正本にしない。

- 凹Componentは1回の平面切断から3個以上の`ComponentFragment`を生成してよい。切断後のTriangle／Convex adjacencyと残存Attachment Edgeに対するGraph connected-componentsを求めてLogicalFragmentを構築するため、単純な正側子／負側子または親子1対2を前提にしない。

- `AttachmentPatch`を連続した幾何領域としてRuntime交差判定せず、固定少数の`AttachmentLink`配列で近似する。1 PatchはstableなPatch ID、Component ID組、重要度と、`1..MaxAttachmentLinkCount`件のAttachmentLinkを持つ。初期Profileは`MaxAttachmentLinkCount = 8`とし、Linkを`AttachmentLinkId`昇順へcanonical化する。各LinkはA／BそれぞれについてComponent ID、元Topology PrimitiveまたはLogical Convex Cell ID、barycentric／local座標からなるfiniteな`AttachmentEndpointAnchor`を1個ずつ持ち、切断後の子系譜へ位置だけでなくTopology IDから追跡する。広い接合は複数Link、点接合は1 Linkで近似し、単なるAABB overlapやRuntime最近傍探索からLinkを新設しない。

- 各Endpointのworld positionは切断Kernelと同じObject Transform Snapshot／SlashFrameから求め、共通World Cut Planeへbinary64、固定演算順のsigned-distance式を適用する。`d > attachmentEpsilon`を`Positive`、`d < -attachmentEpsilon`を`Negative`、それ以外を`OnPlane`とする。`attachmentEpsilon`はfiniteかつ0以上でAsset／Run Profileへ固定し、EndpointごとやComponentごとに変更しない。Linkの切断決定表は次を唯一の契約とする。

| Endpoint A | Endpoint B | Attachment Link結果 |
| --- | --- | --- |
| `Positive` | `Positive` | 両Endpointの正側子ComponentFragment間にEdgeを維持 |
| `Negative` | `Negative` | 両Endpointの負側子ComponentFragment間にEdgeを維持 |
| `Positive` | `Negative` | Linkを切断済みとして除去 |
| `Positive` | `OnPlane` | Linkを切断済みとして除去 |
| `Negative` | `Positive` | Linkを切断済みとして除去 |
| `Negative` | `OnPlane` | Linkを切断済みとして除去 |
| `OnPlane` | `Positive` | Linkを切断済みとして除去 |
| `OnPlane` | `Negative` | Linkを切断済みとして除去 |
| `OnPlane` | `OnPlane` | Linkを切断済みとして除去 |

1 Patch内で正側Linkと負側Linkが残れば両側へ独立Edgeを作り、全Linkが除去されればPatch由来接続を失う。単一点LinkはPatch分割の厳密表現ではなく点接合近似であり、この全9組み合わせの決定表により実装差を許さない。

- Endpoint Anchorを子系譜へ一意に対応付けられない、ID／positionが不正、Link数がProfile範囲外の場合、そのLinkまたはPatchを推測修復しない。影響対象が`VisualOnlyMicro`で既存消去条件を満たす場合だけMicroとして消去し、それ以外は必ず`PendingSupportClassification`へ送る。Timeout時は接続維持を仮定した旧FragmentGroupの未分裂Fallbackへ固定し、実装者がLink除去／接続維持を選択できないようにする。

- Graph更新とconnected-components判定が完了するまでは、即時表示だけをComponent単位で進め、物理は旧FragmentGroupを維持する。確定後に各Graph成分へ対応Convex、`PhysicsConvexMassWeight`、Support到達性を集約してRigidbody分裂へ進む。

- Graph更新成果物は`ObjectGeneration`、入力`CutConnectivityGraphGeneration`、CutOperationId、切断面を保持し、すべて一致した場合だけ公開する。古いGraph成分、部分的なNode／Edge更新、Attachment Patch判定失敗をCommitせず、旧FragmentGroupを維持して再評価する。

- 同じ切断から生じた独立Componentの可視CapがD-080のWorld Cut Plane、Side、Offset、Material／描画状態、Polarity、8bit Count予算を共有する場合は、同じStencil互換Groupへ投入して`Stencil Count != 0`の画面上の論理Unionとして描く。CPUでCap PolygonやComponent GeometryをUnionしない。Overlap領域のCount増加、二重Cap、許容済みの線状Z-fightingは受理する。

- 重なっていたComponentが別Rigidbodyへ分かれ、Collider overlapが一時的な大Impulseを発生させる場合は、同一Cut Operation由来Sibling間だけ衝突を一時抑止できる。外部物体との衝突は維持し、相対分離がProfile閾値を超えるか固定Timeoutへ到達した時点で再有効化する。安全に再有効化できない場合はSibling衝突を無理に戻さずTraceして品質低下する。

- 検証済み左右ConvexをWritable `MeshData`へ出力し、メインスレッドで別々の`UnityEngine.Mesh`へ一括適用する。そのMesh ID列を`IJobParallelFor`へ渡し、`Physics.BakeMesh(meshId, true, cookingOptions)`をバックグラウンド実行する。同一Meshを複数Jobから同時にBakeしない。

- Bake Job完了後も即時適用せず、`SlashId`、`ObjectGeneration`、入力Physics Proxy世代、Cooking ProfileをCommit Controllerで検証する。有効な成果物だけを物理ステップ境界で左右の`MeshCollider.sharedMesh`へ設定し、Rigidbody分裂と運動継承を行う。Schedule済みJobは中断せず、古い成果物は回収する。

#### 7.2.2 大型固定プロップとSafety Tether Tree

建物、巨大看板、塔、大型機械など、完全倒壊させるとレベル、Nav、カメラ安全性、物理予算を破綻させやすい対象は`LargeStructuralProp`として分類する。構造的な固定支持を表す`FixedSupportGraph`と、切断後の移動量だけをゲーム的に制限する`SafetyTetherTree`を別の正本として保持する。前者では切断をまたぐEdgeを除去してAnchored／Detachedを判定する一方、後者ではDetached化した大型Fragment間にも切断面をまたぐ安全テザーを残す。Safety Tetherは支持判定、Dormant／Active判定、質量配分を変更せず、切断済みFragmentをFixedへ戻さない。

- `SafetyTetherTree`は地面から生える有向非循環木とする。論理的なGround Rootの直下には、`SupportState == Anchored`かつ1個以上の生存`FixedSupportAnchor`へ到達する全Fixed Fragmentを置く。この集合を`LogicalFragmentLocalId`昇順に列挙し、Ground Rootから各FragmentへTopology専用のRoot Linkを厳密に1本ずつ持たせる。複数Fixed Fragmentから代表1個だけを選ばない。Root LinkはJoint、Spring、Anchor対、`SafetyTetherLevel`、移動Limitを生成しない。各動的大型Fragmentは親方向の物理`SafetyTetherEdge`を厳密に1本、子方向を0本以上持つ。すべての動的大型Fragmentから親Edge／Root Linkを辿るとFixed Fragmentを経由してGround Rootへ到達しなければならない。通常経路では動的Root用のワールド並進テザーを追加しない。

- 物理分裂Commit時に新しい大型Rigidbodyを公開する直前、旧Tree、切断後LogicalFragment、CutBoundaryRecord、FixedSupport分類から新Treeを原子的に構築する。Geometry Commit、Collider再cook、Fast Simulation昇格、Shared Convex精密化、同じ論理物理GroupのUnity Rigidbody再生成だけではTree、Level、制限をリセットしない。

- 大型Fragmentの即時解析Offsetを開始する前にも、cook不要の同じTopology規則から`PendingSafetyTetherPlan`を作る。確定予定の親、Anchor、Level、相対並進上限、World回転上限の範囲だけで仮運動を表示し、物理Commit時のTreeと一致させる。Planが未確定または世代不一致ならActiveなclip／Capは表示できるが、大型Fragmentの解析Offsetと回転を0に保ち、同期Joint構築やcookを視覚クリティカルパスへ入れない。

- 既存物理Edgeは、その親側／子側AnchorをTopology系譜から含む切断後Fragmentへ継承する。AnchorがCut Plane上または複数子へ曖昧に対応する場合は、Anchorを含む有効Convex、Anchorからの距離、PhysicsConvexMassWeight、Fragment Local IDの固定優先順位で一意に選ぶ。選択を証明できない大型Fragmentでは推測して部分Commitせず、旧FragmentGroup維持または`SafetyFrozen`へ送る。

- Tree構築順は固定する。最初に全root-linked Fixed FragmentとRoot Linkを`LogicalFragmentLocalId`昇順で挿入する。次に、旧Treeの物理Edgeを`SafetyTetherEdgeLocalId`昇順で上記Topology系譜へ写像し、検証済み継承Edgeとして新Forestへ先に挿入する。継承Edgeの子が今回root-linked Fixed Fragmentになった場合だけ、Root LinkをIncoming Edgeの正本として旧物理Edgeを決定的に退役させる。それ以外で同じ子へのIncoming Edge重複、親子同一化、Endpoint／Anchorの曖昧化、Edge ID重複、Cycle、世代不一致が1件でもあれば、どちらかを恣意的に落とさずTree計画全体をRejectして旧FragmentGroup維持または`SafetyFrozen`へ送る。

- Root Linkと継承Edgeの挿入後、親Linkを辿ってSynthetic Ground Rootへ到達する全Nodeを初期接続済み集合とする。まだ到達しない継承Forestの各成分は、Incoming Edgeを持たない成分Rootを厳密に1個持たなければならず、成分内Edgeを分解・付け替えない。一回の切断で3個以上へ分かれる場合も全BoundaryへJointを作らず、未接続成分Rootと接続済み集合の間にある新規候補から「共有する切断面Patch面積が最大、同値ならCutBoundaryLocalId、親Fragment Local ID、成分Root Fragment Local ID順」で1本を選ぶ。そのEdgeで成分全体を接続済みへ加える処理を全成分が尽きるまで繰り返す。候補なし、成分Rootが0個または複数、Incoming Edge二重化、Cycle発生時はfail-closedとする。複数Fixed Fragmentから単一の優先Rootを再選択しない。

- 新規`SafetyTetherEdge`のAnchorは、対応する正負Fragment間の切断面と切断前Physics Convexまたは保守的OBBの交差Polygonから求める。複数Patchでは最大面積Patchを採用し、同値はCutBoundaryLocalId／Patch Local ID順、Anchor位置はその面積重心をWorld Cut Planeへ投影した点とする。実Fragment Mesh完成後に精密Cap重心へ動かさず、Commit時の正負Anchor位置を固定する。Anchor再配置による後発Impulseを発生させない。

- 相対並進制限はEdgeの`SafetyTetherLevel`へ属し、`limit(level) = initialLimit * decay^level`とする。`initialLimit`はfiniteかつ0以上、`decay`はfiniteかつ`0 < decay < 1`を要求し、正の`minLimit`は設けない。Ground Root直下の固定Fragmentから最初の動的子へ向かうEdgeをLevel 0とし、その子から新しく生えるEdgeをLevel 1として深さごとに増やす。初期候補は`initialLimit = 0.4 m`、`decay = 0.5`とし、Level 0から任意の有限深度までの累積追加並進を幾何級数`initialLimit * (1 - decay^(depth + 1)) / (1 - decay)`、無限深度の上限を`initialLimit / (1 - decay) = 0.8 m`とする。binary32／binary64で深いLevelの値が0へunderflowした場合は0を維持し、正の下限へClampしない。数値はProfile化しT-087後に決める。

- 回転は相対Treeへ伝播させず、各大型Rigidbodyの論理的な物理分裂Commit時のWorld姿勢を`WorldRotationOrigin`として制限する。`StructuralSplitGeneration`は実際にFragmentGroupが複数物理Groupへ分裂した場合だけ子へ`parent + 1`で継承し、Engine Object再生成では進めない。親Generationが`uint.MaxValue`で新たな物理分裂が必要な場合は0へwrap／再利用せず、`StructuralSplitGenerationExhausted`としてCut Operation全体をRejectして旧FragmentGroupを維持し、維持不能なら`SafetyFrozen`へ送る。角度上限も`angleLimit(generation) = initialAngle * angleDecay^generation`、`0 < angleDecay < 1`とし、正の`minAngle`を設けず、少しずつ曲がるが横倒ししにくい挙動を優先する。

- テザーは移動を完全固定するものではなく、制限近傍でSpring／Damperを強め、Hard Limitを越えない候補構成とする。Tether接続SiblingはKerf 0の一致面や重複Convexから大Impulseを生じないよう相互衝突を無効化してよい。外部物体との衝突は維持するが、Playerとは7.2.3により接触しない。Joint解法、独自Force／Clamp、Spring値、Damper値はT-087で比較し、ProjectionやTransform Snapは常用しない。

- Tree再構築失敗、Ground Root到達不能、制限値非finite、Edge／Anchor世代不一致、Constraint予算超過では、新しい大型Rigidbodyを自由落下状態で部分公開しない。Tree内容が変わらないNo-op再評価は現Generationを維持し、再構築成功として数えない。Tree変更が必要な時点で現`SafetyTetherTreeGeneration == uint.MaxValue`ならGenerationをwrap／再利用せず`SafetyTetherGenerationExhausted`としてRejectする。旧FragmentGroupを維持できるならCommitを遅延し、すでに公開済みで維持不能なら現在の安全姿勢で速度を0にして`SafetyFrozen`へ移す。空中静止や構造的不自然さは許容し、レベル全体の倒壊より安全性を優先する。

#### 7.2.3 プレイヤー非接触Locomotion

初期仕様ではPlayer Body／Hand用Layerとプロップ／破片のPhysics Layer間の接触を無効化し、プレイヤーは物体を押さず、物体もプレイヤーを押しやらない。床移動と人工移動による代表的な壁への新規侵入はRigidbody接触ではなく、建物壁板、固定大型プロップ、レベル境界から作る低複雑度`PlayerLocomotionOccupancy`に対する次姿勢Queryで抑制する。これはCameraと全Render Geometryの厳密な非交差を保証する仕組みではない。斬撃は従来どおりBlade／SlashFrontの論理Sweepで成立するため、非接触化しても主要Interactionを失わない。

- Occupancyは表示Triangleや切断前の旧Colliderをそのまま使用せず、現在のStructural Slab OBB／少数Primitiveとレベル固定境界から作る。Tethered Fragmentの確定物理姿勢へFixed Step後に追従し、Pending中は保守的な旧領域を維持する。切断開口を移動可能域へ反映する時期はStable Geometryと確定物理姿勢が揃った後とし、即時Rendererだけを根拠に通行を許可しない。

- スティック移動や人工移動では、Player Rootと予測HMD Capsuleの次姿勢がモデル化済み禁止Occupancyへ新規侵入する操作を止める。実空間の6DoF頭部移動を仮想Camera Clampや物理Impulseで押し戻さず、簡易Volumeとの重なりが検出できた場合だけNear-Wall Fade／視界マスクをbest-effortな視界保護として適用する。Fadeが全てのGeometry被りを隠すことは保証しない。

- HMD視点の近似判定は、PlayerLocomotionOccupancyに登録された大型固定／構造プロップとレベル境界のOBB、Box、Capsule等に対する小さなHead Sphereまたは予測HMD CapsuleのOverlapまでとする。小型プロップ、Micro Debris、装飾、VisualOnly Component、一般の非接触Fragment、実Render Triangle、複雑な切断面を網羅しない。Meshのinside／outside、Ray parity、generalized winding、切断後Mesh全体への包含Queryを行わず、未登録物体や非干渉物体がHMD視点へ被ること、Cameraがそれらの内部へ入ること、Near Planeで内部面が見えることを許容する。

- 即時切断物体へHMDが入った場合は5.2のTemporary Stencil例外を適用する。スクリーンスペースの一部だけに仮Capが現れる、Capが欠ける、内部面が見える、左右眼で差が出る状態を許容し、Player／Cameraまたは物体を強制移動しない。Camera overlapを切断Topology、物理、Geometry Commitの失敗Reasonにせず、切断Jobの取消・再発行や同期Fallbackも行わない。

- `PlayerLocomotionPolicy`は`NewEntryReject=1`、`PushOut=2`、`ExitOnly=3`の固定候補とし、0を未設定、未知値をRejectする。すでに禁止領域へ入ったPlayerを外へ押し出す`PushOut`と、「侵入深度を増やす移動だけ拒否し、減らす移動は許可する」`ExitOnly`はプレイテスト後に選ぶ。それまでは接触Impulseを導入せず、`NewEntryReject`と視界保護をPoC正本とする。

- PoCの`NewEntryReject`中に、静止中のPlayerへTethered Fragment側のOccupancy更新が重なった場合は`ForcedOccupancyOverlap`へ入る。Slab／Tetherの物理CommitをPlayer位置だけを理由にRejectまたは巻き戻さず、PlayerをImpulseや強制位置補正で押し出さない。Episode開始時にはLocomotion Upに直交する2次元`AllowedLocomotionPlane`と`ExitSearchMaxHorizontalExpansion = 2.0 m`という寸法上限だけを固定し、world-space `ExitSearchBounds`の位置は固定しない。各Fixed Stepの候補生成前に、現在Player Capsuleと、人工移動要求の平面内長さ`s`を全方向へ適用し得る保守的`CandidateSweepEnvelope`とのunionを現在Player位置基準で一度だけ求める。全Line Search候補は長さ`<= s`なのでこのEnvelopeへ含まれなければならない。必要な水平拡張がProfile上限を超える、またはBoundsが非finiteならVolume Queryや部分候補評価を行わず`OccupancyExitBlocked(SearchBoundsExceeded)`へ移る。

- 上限内なら、そのFixed Stepの`ExitSearchBounds`を上記unionへ確定し、このBoundsと保守的Boundsが交差する全Occupancy Primitiveを先に収集して全候補共通のVolume集合とする。候補ごとにBoundsやVolume集合を変えず、現在／候補姿勢で非Overlapなら深度0として評価する。これにより通常LocomotionでEpisode開始地点から任意距離移動済みでも、現在Playerへ侵入しているVolumeは収集対象になる。単一の最深Volumeだけを正本にせず、垂直法線をAllowed Planeへ射影してzeroになる場合はその法線を退出候補に使用しない。PoC Profileの初期値は`MaxExitVolumeCount = 8`、`ExitLineSearchSteps = 4`、`MaxExitCandidateCount = 192`、`ExitBlockedFixedStepLimit = 15`、`MaxForcedOverlapFixedSteps = 180`とし、T-088後に校正する。

- Volume収集、法線、方向、展開済み候補、Depth Vector、MetricはProfile上限で初期化時に一度だけ確保する固定長Native／unmanaged作業領域へ格納し、Episode中のManaged allocation、Buffer成長、GCを禁止する。Broadphaseは`MaxExitVolumeCount + 1`件の固定長NonAlloc Bufferまたは同等のoverflow flag付きQueryを使い、上限超過を無制限列挙せず検出する。結果が`MaxExitVolumeCount`を超えた場合はID順の先頭だけを使わず、深度計算とPlayer移動を開始する前に`OccupancyExitBlocked(VolumeCapacityExceeded)`へ移る。非zeroな人工移動要求の平面内長さを`s`とする。有限候補方向は、平面へ射影した要求方向、各Overlap Volumeの解析的外向き法線の非zero射影、全有効法線の正規化和、全ての非zeroな法線2本の正規化和をこの順で生成し、Volume組はLocal ID辞書順、同じcanonical binary32方向は最初の1件だけ残す。各方向について固定`ExitLineSearchSteps`の`stepLength = s * 2^-k`、`k = 0..ExitLineSearchSteps-1`を評価する。中心一致等で法線が一意でないPrimitiveはlocal `+X, -X, +Y, -Y, +Z, -Z`順の軸をWorld／Allowed Planeへ射影し、最初の非zero方向だけを候補にする。要求量`s > 0`以外の最小進捗量を設けない。

- 方向生成前に、収集Volume数から`1 + V + 1 + V * (V - 1) / 2`のchecked整数上限を求め、`ExitLineSearchSteps`を掛けた展開候補上限が`MaxExitCandidateCount`以下であることを検証する。deduplicateによる減少を容量成立の前提にしない。checked overflowまたは上限超過では候補を途中まで生成・評価せず`OccupancyExitBlocked(CandidateCapacityExceeded)`へ移り、そのTickまでに作った部分候補の移動を適用しない。

- 同一Fixed Step Snapshot上の各候補について、収集済み全Volumeの非負binary64侵入深度を固定長領域へ求める。`ExitMetric = (MaxDepth, SumDepth, DepthByVolumeId[])`とし、`MaxDepth`は全値の最大、`SumDepth`はLocal ID昇順のbinary64左畳み、末尾VectorもLocal ID昇順とする。非Overlapは0、非finiteは候補Rejectとする。現在姿勢よりこのTupleがIEEE 754数値順で辞書式に厳密減少する候補だけを許可し、最小Metric、同値なら要求方向とのdotが最大、さらに同値なら候補生成順が早いものを適用する。このためPlayer自身の適用移動が同一Snapshot上で過去姿勢へ周期的に戻ることはない。適用後は次Fixed Stepの更新済みSnapshotで再評価する。

- `ForcedOccupancyOverlap`へ入ったFixed Stepを0として、候補の有無やMetric進捗にかかわらずEpisode経過をsaturating counterで数える。全深度が`occupancyExitEpsilon`以下になる前に`MaxForcedOverlapFixedSteps`へ到達した場合は`OccupancyExitBlocked(EpisodeTimeout)`へ移る。減少候補がないTickではPlayerを動かさず、物理Impulseや任意軸Fallbackで押し出さない。非zero入力が`ExitBlockedFixedStepLimit`回連続しても減少候補を得られない場合は`OccupancyExitBlocked(NoDecreasingCandidate)`へfail-closedする。Candidate SweepがBounds寸法上限を超える場合は前述の`SearchBoundsExceeded`を使用する。Blocked後は人工並進を停止してNear-Wall Fade／視界マスクを維持し、明示的なユーザー操作による最後の安全なLocomotion Poseへの復帰、Level Reset、またはOccupancy側が移動して減少候補が再出現した場合だけ再開する。全侵入深度がfiniteかつ`<= occupancyExitEpsilon`になれば重なりなしへSnapして通常の`NewEntryReject`へ復帰する。実空間HMD 6DoFは常にClampしない。これは`ExitOnly`を最終Policyとして採用したことを意味せず、移動Occupancy起因の安全Fallbackに限定する。

- Player接触を物理世界から除外することで、プレイヤーの身体接触による未来Physics結果の無効化を発生させない。Player位置と斬撃は依然として介入条件だが、Fragmentの投機物理Commit条件へPlayer接触Impulse履歴を追加しない。

### 7.3 Collider Cooking Profile

ランタイム生成するPhysics Proxyは、自前のConvexクリップと検証器でwatertight、面向き、凸性、退化三角形、重複頂点、極短辺、自己交差、頂点・面数上限を保証する。契約を満たしたMeshでは`EnableMeshCleaning`と`WeldColocatedVertices`を無効化する構成を有力候補とし、Unityへ重複作業をさせない。検証に失敗した入力を軽量ProfileのままBakeせず、簡易Proxy、Compound Primitive、または非物理デブリへ品質低下させる。

初回の物理分裂には原則Fast Cookを使い、`PendingPhysicsSplit`を早く終了させる。物理分裂後、余剰CPU時間に同一形状をFast Simulationで再Bakeし、価値のある破片だけを低優先度で昇格させる。両Profileの単独比較に加え、この二段階運用が総コストを下げるか実測する。

| Profile | `MeshColliderCookingOptions`候補 | 目的 |
| --- | --- | --- |
| Fast Cook | `None` | 任意の追加工程を省き、Bake待ちキューと`PendingPhysicsSplit`滞留を短縮 |
| Fast Simulation | `CookForFasterSimulation` | 追加cookを許容し、完成後の破片衝突・Query負荷を低減 |

Fast Simulationへの昇格候補は、長寿命、プレイヤー近傍、接触／Query頻度が高い、今後も動く見込みがある破片を優先する。短命デブリ、遠距離、Sleep済み、すぐ消去予定の破片はFast Cookのまま残してよい。Upgrade同時実行数、待ちキュー、二重Meshの一時メモリへ上限を設ける。

使用中のFast Cook Meshを別Profileで再Bakeせず、同じ形状を持つ別のFast Simulation Meshを生成してバックグラウンドBakeする。成果物は`ObjectGeneration`を保持し、再切断などで世代が変わった場合は適用せず回収する。Bake完了後は物理ステップ境界で`cookingOptions`と`sharedMesh`を同時に切り替える。接触中は接触キャッシュ再構築やWakeによる跳ねを避けるため原則延期し、Sleep中または非接触時を優先する。安全な機会が来なければFast Cookを維持する。

`None`はcook自体の省略ではない。`UseFastMidphase`はConvexでの効果を前提にせず、別測定で利益が確認された場合だけProfileへ加える。`Physics.BakeMesh`と適用先`MeshCollider.cookingOptions`には必ず同じProfileを設定し、Bake後にMesh形状を変更しない。同一Meshを複数Workerから同時にBakeしない。

### 7.4 Native PhysX Cooking比較Probe

物理実装の正本はGameObject／Rigidbodyで利用するUnity Built-in 3D Physics（Unity内蔵PhysX）とし、DOTSの`Unity Physics`パッケージとは区別する。早期に小さなNative PhysX Probeを作り、Unity `Physics.BakeMesh`経路の実測値と、PhysX APIへ完全なConvex Topologyを直接渡す経路の理論的な改善幅を比較する。Probeは測定専用であり、初期製品Runtimeの依存にはしない。

| 経路 | 入力／API | 確認するもの |
| --- | --- | --- |
| U1 | Unity Mesh＋`Physics.BakeMesh(meshId, true, cookingOptions)` | 採用予定経路のEnd-to-End費用 |
| N1 | Native PhysX、頂点＋`eCOMPUTE_CONVEX` | 一般凸包計算を含む近似比較 |
| N2 | Native PhysX、頂点＋Polygon＋Index、`eCOMPUTE_CONVEX`なし | 自前B-repを利用して凸包計算を省く改善上限 |
| N3 | N2を`PxCreateConvexMesh`で直接生成 | Stream serialize／loadを省いたリアルタイム生成上限 |

同じ自前Convex切断結果を入力し、頂点／面数、Cooking設定、検証有無、Allocator、Thread数、Warm-up、Release相当Build、CPU Affinityを可能な範囲で揃える。Unity同梱PhysXとProbe側PhysXの版が一致しない場合は両版を`GeometryBenchmarkRunManifest`へ記録し、差をAPI経路だけの因果差と断定しない。

計測は少なくとも、自前Convex clipping=`PolygonClip`、Descriptor／MeshData構築=`DescriptorBuild`／`MeshDataBuild`、Native境界転送=`NativeBoundaryTransfer`、`ApplyAndDisposeWritableMeshData`=`MeshApply`、Hull計算=`HullComputation`、PhysX内部形式生成=`PhysXFormatBuild`、Stream処理=`StreamSerialize`／`StreamLoad`、`Physics.BakeMesh`=`Bake`、Collider Commit=`Commit`へ分離する。8、16、32、64、128、255頂点級、単発／Batch、同時Slash、Fast Cook／Fast SimulationでP50／P95／P99、Throughput、Worker占有、Main Thread時間、一時／最終メモリ、失敗率、生成頂点／面、接触／Query品質を比較する。

Native PhysXが生成した`PxConvexMesh`またはCook済みBinaryをUnity `MeshCollider`へ注入する公開経路は前提にしない。大差が出ても、まずUnity経路のBatch化、Cooking Profile、入力簡略化、二段階Collider、Cacheで要件を満たせるか確認する。Native採用は、Unity経路のP99が実際にPending／90Hz予算を破り、差が継続的かつ大きく、Unity側で回避不能な工程にあり、Native成果物を実ゲームへ統合する別の小型Prototypeが成立した場合だけ再検討する。この場合はcook関数だけの交換ではなく、切断破片のQuery／接触／Scene同期を含む物理経路の部分置換として見積もる。

### 7.5 Geometry／Cook Microbenchmark

表示Mesh切断、Convex切断、暫定ローポリモデル生成、cookの各実装が個別に正しい結果を生成できた時点で、予算校正と追加最適化を行う前の初期製品実装Baselineを取得する。Job／Burst、Batch、Fast Cook／Fast Simulation等のPhase 3／4で採用済み基本経路はBaselineに含む。目的は最速値の宣伝ではなく、入力規模から単発完了時間、Jobキュー滞留、斬撃波到達までの完了率、同時Pending上限を見積もるための容量モデルを作ることである。Phase 0.25のT-070は固定ConvexによってUnity／Native cook経路の差と改善上限を早期に調べるProbeであり、T-076の前提ではない。Phase 4.1のT-076は製品の表示Mesh／Convex／Proxy生成経路が完成した後に取得し、T-069の統合測定を補完するとともに、T-070の早期結果を製品入力分布と工程内訳から再解釈するBaselineとする。

計測単位は次に固定し、複数工程を一つの数値へ混ぜない。

| 対象 | 分離して測る工程 | 主な規模軸 |
| --- | --- | --- |
| 表示Mesh | 頂点／Triangle平面分類、Count、Write、Original Edge交点共有、Contour Track構築、2D交差検出、Simple Cap、局所Arrangement、Boundary Fan／Open Chain、cut-local検証、接続成分、Metadata、Writable MeshData構築 | 入力／出力Triangle数、交差Original Edge数、Contour Track数、2D交差数、CapConstructionPath別件数、Non-manifold lane／Open Chain数、Fragment数、累積切断面数 |
| Physics Convex | Convex Count、Polygon clipping、切断面生成、Write、Validation、体積／重心／慣性、Collider用MeshData構築 | Convex数、各Convexの頂点／面数、交差Convex率、出力Convex数 |
| Temporary Low-Poly Proxy | Bounds／切断面からの簡易表示Proxy、簡易Convex、Compound Primitiveまたは汎用ローポリFallbackの生成 | 目標Triangle／Primitive数、Fragment数、入力Bounds／切断面数 |
| Cook／Commit | `Physics.BakeMesh`のFast Cook／Fast Simulation、Mesh公開、Collider Commit | Convex頂点数、Bake数、Batch Size、Profile、同時実行数 |

同じPure Native入力と出力Bufferを使い、表示Mesh／Convex／Temporary Proxyの計算Kernelだけを同期実行する`Single-Thread Kernel`と、実際の`Schedule -> Worker実行 -> Complete`を使う`Job Batch`を分離する。前者は`µs/op`、入力／出力要素当たり時間、P50／P95／P99を記録し、Job Schedule、GC、Unity Object生成を含めない。Unity API境界を含む`Physics.BakeMesh`、Mesh公開、Collider CommitはPure Kernel値へ混ぜず、直列の単発LatencyとBatch時のEnd-to-End値として別記する。Job側は`cuts/s`、`input triangles/s`、`output triangles/s`、`convexes/s`、`cooks/s`、Job End-to-End latency、Schedule時間、Worker占有率、Main Thread Commit時間を記録する。単発Jobのレイテンシと十分なBatchを連続投入した定常Throughputを混同しない。

固定Datasetには、公開可能な合成Fixtureをcanonical正本として、表示Mesh 500／1,000／3,000／10,000／30,000 Triangle級、Convex 8／16／32／64／128／255頂点級、1／4／16／64 Convex、2／4／8 Fragment、中央切断／端切断／非交差、単一／複数断面、単純／複数Cap Loopを含める。Cap Loop等の閉形状既知正解にはSynthetic Watertight Test Fixtureを使う。暫定Proxyは50／100／250／500 Triangleまたは1／4／16 Primitive級を初期候補とする。Phase 0.2で自動選抜したSynty／Poly Pro Universe等に由来する`LicensedRepresentative` Render／Convex Fixtureも非公開の補助Suiteとして測定し、RenderはOriginal、Boundary Fill、約100、500、1,000、2,000、5,000、10,000 Triangleを要求するDirect Variantに加え、Voxel64／128／256基底と限定Post-Decimateを比較する。各要求値と実出力を分離し、Manifestの規模軸には実Triangle数を使う。合成Fixtureの代替や全Asset互換性の証拠にはせず、公開結果から入力GeometryやAsset対応を復元できるデータは保存しない。

Release Player相当、Burst有効、Jobs Debugger／Safety Checks無効を採用判断用の正本とし、Editor値は開発時の回帰検出専用とする。Cold start、初回JIT／Burst Compile、Allocator拡張は定常値と分け、Managed GC、Native一時メモリ、失敗／Fallback率も記録する。結果の正しさを事前検証し、無効出力や早期Rejectを成功経路の高速値へ混ぜない。Temporary Low-Poly ProxyはT-077の正しさ検証を通過した実装済み品質段階だけをT-076の性能比較へ含め、未実装の目標品質を0コストとして扱わない。

性能測定の環境情報は既存の`TraceRunManifest`を拡張せず、別schemaの`GeometryBenchmarkRunManifest`へ保存する。1 Manifestは単一`DatasetCaseId`の固定入力に対する「単一`BenchmarkTarget`、単一`BenchmarkStage`、単一`ExecutionMode`、単一`CookingProfile`、単一`BenchmarkMetric`、単一`MeasurementUnit`を反復する1測定系列」だけを表す。T-070／T-076の1回のHarness実行は複数Manifestを生成し、全系列へ同じ`BenchmarkSuiteId`を付けて束ねる。各系列は異なる`BenchmarkRunId`を持ち、同じSuite内でRun IDと`Target + Stage + ExecutionMode + CookingProfile + Metric + Unit + DatasetId + DatasetContentSha256 + DatasetCaseId + BatchSize`の組を重複させない。別case、別工程、別指標は同じManifestへ混在させず、同じSuiteの別系列として保存する。

独立したSchema Version、canonical UTF-8 JSON Codec、content SHA-256、Golden Fixtureを持つ。Schema v1のproperty順と型・値域は次を正本とする。`nullable`と明記したもの以外は必須かつ非nullである。

| Property | JSON型 | 必須性・範囲・意味 |
| --- | --- | --- |
| `SchemaVersion` | integer | 必須。v1では厳密に`1` |
| `BenchmarkSuiteId` | string | 必須。小文字RFC 4122 UUID `D`形式。1回のT-070／T-076 Harness実行を識別 |
| `BenchmarkRunId` | string | 必須。小文字RFC 4122 UUID `D`形式。Suite内で一意な1測定系列ID。再利用禁止 |
| `GitCommit` | string | 必須。cleanなRepositoryのHEADを表す小文字16進40桁または64桁。空文字は禁止 |
| `UnityVersion` | string | 必須。Trim済み1～128文字。空／空白だけ／前後空白は禁止 |
| `BurstVersion` | string | 必須。Trim済み1～128文字。空／空白だけ／前後空白は禁止 |
| `CollectionsVersion` | string | 必須。Trim済み1～128文字。空／空白だけ／前後空白は禁止 |
| `UnityPhysXVersion` | string | 必須。Trim済み1～128文字。空／空白だけ／前後空白は禁止 |
| `NativePhysXVersion` | string／null | Native PhysX TargetではTrim済み1～128文字を必須とし、それ以外は厳密に`null` |
| `CpuName` | string | 必須。Trim済み1～256文字。空／空白だけ／前後空白は禁止 |
| `OperatingSystem` | string | 必須。Trim済み1～256文字。空／空白だけ／前後空白は禁止 |
| `WorkerCount` | integer | 必須。計測時の設定値`1..1024` |
| `PowerProfile` | string | 必須。Trim済み1～128文字。空／空白だけ／前後空白は禁止 |
| `BuildConfiguration` | string enum | 必須。`UnityReleasePlayer`／`NativeRelease`／`EditorDevelopment`。採用判断は前二者だけ |
| `BurstEnabled` | boolean | 必須。実測値。Native専用系列では`false` |
| `SafetyChecksEnabled` | boolean | 必須。実測値 |
| `DatasetId` | string | 必須。`[A-Za-z0-9._-]{1,128}` |
| `DatasetContentSha256` | string | 必須。小文字64桁`[0-9a-f]{64}` |
| `DatasetCaseId` | string | 必須。Dataset内の固定入力caseを表す`[A-Za-z0-9._-]{1,128}`。同じDatasetContentSha256内で意味を変更しない |
| `InputTriangleCount` | integer | 必須。`0..2147483647`。非該当Targetでは0 |
| `OutputTriangleCount` | integer | 必須。`0..2147483647`。確定出力または検証済み期待値。非該当Targetでは0 |
| `IntersectedEdgeCount` | integer | 必須。`0..2147483647`。非該当Targetでは0 |
| `CapLoopCount` | integer | 必須。`0..2147483647`。非該当Targetでは0 |
| `FragmentCount` | integer | 必須。`0..2147483647`。非該当Targetでは0 |
| `InputConvexCount` | integer | 必須。`0..2147483647`。非該当Targetでは0 |
| `OutputConvexCount` | integer | 必須。`0..2147483647`。非該当Targetでは0 |
| `InputConvexVertexCount` | integer | 必須。全入力Convexの合計頂点数`0..2147483647`。非該当Targetでは0 |
| `OutputConvexVertexCount` | integer | 必須。全出力Convexの合計頂点数`0..2147483647`。非該当Targetでは0 |
| `PrimitiveCount` | integer | 必須。Temporary Proxy等のPrimitive数`0..2147483647`。非該当Targetでは0 |
| `CutPlaneCount` | integer | 必須。`0..2147483647`。切断を伴わないTargetでは0 |
| `BatchSize` | integer | 必須。`JobBatch`では`2..1000000`、それ以外のExecutionModeでは厳密に1 |
| `WarmupIterations` | integer | 必須。`0..1000000`。定常Baselineでは1以上、Cold測定だけ0を許可 |
| `MeasurementIterations` | integer | 必須。`1..1000000`。GeometryBenchmarkResult v1の最大Sample試行数と一致 |
| `CookingProfile` | string enum／null | Cook Targetでは`FastCook`／`FastSimulation`を必須とし、表示Mesh／Convex切断／Proxy／Commit等の非Cook Targetでは厳密に`null` |
| `BenchmarkTarget` | string enum | 必須。`DisplayMeshCut`／`ConvexCut`／`TemporaryLowPolyProxy`／`UnityBakeMesh`／`NativePhysXComputeHull`／`NativePhysXCompleteTopology`／`NativePhysXDirectInsertion`／`MeshPublish`／`ColliderCommit` |
| `BenchmarkStage` | string enum | 必須。`WholePipeline`／`PlaneClassification`／`Count`／`Write`／`IntersectionMerge`／`TopologyIntersectionShare`／`ContourTrackBuild`／`ContourIntersectionTest`／`CapLoopBuild`／`CapTriangulation`／`CapArrangement`／`BoundaryFan`／`OpenChainBridge`／`Connectivity`／`Metadata`／`PolygonClip`／`CutFaceBuild`／`Validation`／`MassProperties`／`DescriptorBuild`／`MeshDataBuild`／`ProxyGeneration`／`NativeBoundaryTransfer`／`MeshApply`／`HullComputation`／`PhysXFormatBuild`／`StreamSerialize`／`StreamLoad`／`DirectInsertion`／`Bake`／`Schedule`／`WorkerExecution`／`Complete`／`Commit` |
| `ExecutionMode` | string enum | 必須。`SingleThreadKernel`／`SerialApiLatency`／`JobSingle`／`JobBatch`／`MainThreadCommit` |
| `BenchmarkMetric` | string enum | 必須。`Latency`／`Throughput`／`InputRate`／`OutputRate`／`WorkerOccupancy`／`ManagedAllocation`／`NativeMemoryPeak`／`FailureRate`／`ScheduleCount` |
| `MeasurementUnit` | string enum | 必須。`Microseconds`／`MicrosecondsPerOperation`／`OperationsPerSecond`／`CutsPerSecond`／`InputTrianglesPerSecond`／`OutputTrianglesPerSecond`／`ConvexesPerSecond`／`CooksPerSecond`／`Percent`／`Bytes`／`Count`／`FailuresPerMillionOperations`から1つだけ選ぶ |
| `TraceRunManifestContentSha256` | string／null | Trace参照時は小文字64桁`[0-9a-f]{64}`、未参照時は厳密に`null` |

canonical JSONは全propertyを上表順序で常に出力し、UTF-8 BOMなし、余分な空白と末尾改行なし、不変Cultureの数値表現とする。nullable propertyも省略せず上表の条件で文字列またはJSON `null`を出力する。Cook Targetは`UnityBakeMesh`と3つの`NativePhysX*`、Native Targetは3つの`NativePhysX*`と定義する。CodecはTargetとStage、ExecutionMode、`NativePhysXVersion`、`CookingProfile`の組合せを検証し、`WholePipeline`以外のStageを無関係なTargetへ指定できないようにする。

`BenchmarkTarget × BenchmarkStage`の許可集合は次を正本とする。`Schedule`／`WorkerExecution`／`Complete`はJob実装を持つTargetだけで使用し、表にない組合せはCodecでRejectする。`WholePipeline`は対象のEnd-to-End系列であり、下位Stageの代用として工程別必須系列を省略してはならない。

| BenchmarkTarget | 許可するBenchmarkStage |
| --- | --- |
| `DisplayMeshCut` | `WholePipeline`、`PlaneClassification`、`Count`、`Write`、`IntersectionMerge`、`TopologyIntersectionShare`、`ContourTrackBuild`、`ContourIntersectionTest`、`CapLoopBuild`、`CapTriangulation`、`CapArrangement`、`BoundaryFan`、`OpenChainBridge`、`Connectivity`、`Metadata`、`Validation`、`MeshDataBuild`、`Schedule`、`WorkerExecution`、`Complete` |
| `ConvexCut` | `WholePipeline`、`PlaneClassification`、`Count`、`PolygonClip`、`CutFaceBuild`、`Write`、`Validation`、`MassProperties`、`MeshDataBuild`、`Schedule`、`WorkerExecution`、`Complete` |
| `TemporaryLowPolyProxy` | `WholePipeline`、`ProxyGeneration`、`Validation`、`MeshDataBuild`、`Schedule`、`WorkerExecution`、`Complete` |
| `UnityBakeMesh` | `WholePipeline`、`DescriptorBuild`、`MeshDataBuild`、`MeshApply`、`NativeBoundaryTransfer`、`Bake`、`Schedule`、`WorkerExecution`、`Complete` |
| `NativePhysXComputeHull` | `WholePipeline`、`DescriptorBuild`、`NativeBoundaryTransfer`、`HullComputation`、`PhysXFormatBuild`、`StreamSerialize`、`StreamLoad` |
| `NativePhysXCompleteTopology` | `WholePipeline`、`DescriptorBuild`、`NativeBoundaryTransfer`、`PhysXFormatBuild`、`StreamSerialize`、`StreamLoad` |
| `NativePhysXDirectInsertion` | `WholePipeline`、`DescriptorBuild`、`NativeBoundaryTransfer`、`PhysXFormatBuild`、`DirectInsertion` |
| `MeshPublish` | `WholePipeline`、`MeshApply`、`Commit` |
| `ColliderCommit` | `WholePipeline`、`Commit` |

規模軸はDataset caseの固定説明変数であり、Samplesとは独立してManifestへ保存する。`DisplayMeshCut`／`MeshPublish`はTriangle／Edge／Cap／Fragment軸、`ConvexCut`はConvex／Convex Vertex／Fragment／Cut Plane軸、`TemporaryLowPolyProxy`はTriangle／Fragment／Primitive／Cut Plane軸、Cook Target／`ColliderCommit`はConvex／Convex Vertex軸を使用する。使用しない軸は厳密に0とし、同じ`DatasetId + DatasetContentSha256 + DatasetCaseId`で軸値が異なるManifestを同一Suiteへ含めない。CodecとSuite LoaderはこのTarget別規模軸規則を検証する。

同一`BenchmarkSuiteId`内では、1つの`DatasetId`を厳密に1つの`DatasetContentSha256`へ対応させる。Suite Loaderは全Manifestを読む際に`DatasetId -> DatasetContentSha256`の写像を構築し、同じDatasetIdから異なるhashが1件でも現れたSuite全体を、Resultの読込や容量式tableへのjoin前にRejectする。異なるDataset版を比較する場合は別`BenchmarkSuiteId`で測定するか、版を表す別`DatasetId`を明示的に割り当てる。同一Suite内でhashだけを変えてLatency、Throughput、FailureRate等の系列を混在させることは禁止する。

`BenchmarkStage × ExecutionMode`の許可集合も固定し、Target×Stage表との積で最終的な許可組合せを決める。

| BenchmarkStage分類 | 許可するExecutionMode |
| --- | --- |
| `PlaneClassification`、`Count`、`Write`、`IntersectionMerge`、`TopologyIntersectionShare`、`ContourTrackBuild`、`ContourIntersectionTest`、`CapLoopBuild`、`CapTriangulation`、`CapArrangement`、`BoundaryFan`、`OpenChainBridge`、`Connectivity`、`Metadata`、`PolygonClip`、`CutFaceBuild`、`Validation`、`MassProperties`、`MeshDataBuild`、`ProxyGeneration` | `SingleThreadKernel`、`JobSingle`、`JobBatch` |
| `DescriptorBuild` | `SingleThreadKernel`、`SerialApiLatency`、`JobSingle`、`JobBatch` |
| `NativeBoundaryTransfer` | `SerialApiLatency`、`JobSingle`、`JobBatch` |
| `HullComputation`、`PhysXFormatBuild`、`StreamSerialize`、`StreamLoad`、`DirectInsertion` | `SerialApiLatency` |
| `Bake` | `SerialApiLatency`、`JobSingle`、`JobBatch` |
| `Schedule`、`WorkerExecution`、`Complete` | `JobSingle`、`JobBatch` |
| `MeshApply`、`Commit` | `MainThreadCommit` |

`WholePipeline`だけはTarget別にModeを固定する。`DisplayMeshCut`／`ConvexCut`／`TemporaryLowPolyProxy`は`SingleThreadKernel`／`JobSingle`／`JobBatch`、`UnityBakeMesh`は`SerialApiLatency`／`JobSingle`／`JobBatch`、3つのNative PhysX Targetは`SerialApiLatency`、`MeshPublish`／`ColliderCommit`は`MainThreadCommit`だけを許可する。さらにNative PhysX Targetの下位Stageはすべて`SerialApiLatency`、`MeshPublish`／`ColliderCommit`の下位Stageはすべて`MainThreadCommit`へ限定する。`JobBatch`は`BatchSize >= 2`、それ以外は`BatchSize == 1`を要求する。これにより`ColliderCommit + SingleThreadKernel`、`PlaneClassification + MainThreadCommit`等をCodecでRejectする。

MetricとUnitの許可組合せも固定する。`Latency`は`Microseconds`／`MicrosecondsPerOperation`、`Throughput`は`OperationsPerSecond`／`CutsPerSecond`／`ConvexesPerSecond`／`CooksPerSecond`、`InputRate`は`InputTrianglesPerSecond`、`OutputRate`は`OutputTrianglesPerSecond`、`WorkerOccupancy`は`Percent`、`ManagedAllocation`／`NativeMemoryPeak`は`Bytes`、`FailureRate`は`Percent`／`FailuresPerMillionOperations`、`ScheduleCount`は`Count`だけを許可する。UUID、enum、文字列長、数値範囲、SHA-256の長さ／小文字／文字種もCodecで検証する。

canonical Suite開始時に一度だけ、公開Repositoryで`git status --porcelain=v1 --untracked-files=all`相当の結果が空であることを必須検証し、その時点のHEADを全Manifest共通の`GitCommit`として固定する。出力先はRepository外の`%LOCALAPPDATA%\Zantetsuken\Benchmarks\<BenchmarkSuiteId>.tmp\`を既定とし、Suite中にRepositoryへManifest、Result、Logを生成しない。全測定終了後かつ最終化前に、HEADが開始時の`GitCommit`と一致し作業ツリーが引き続きcleanであることを再検証する。途中でstaged、unstaged、未追跡変更が生じた場合はSuite全体をRejectし、一時出力を確定しない。

dirty状態の非公式な対話計測は画面表示だけ許可できるが、canonical Manifest、content hash、Result、Suite Indexを保存せず、回帰比較、容量校正、Native採用判断へ使用しない。Golden Fixture等のRepository内成果物更新はBenchmark Suiteとは別の実装作業として行い、測定開始前にコミットする。

各`GeometryBenchmarkRunManifest`には、同じ`BenchmarkSuiteId`／`BenchmarkRunId`を持つcanonical `GeometryBenchmarkResult`を厳密に1件対応させる。Result Schema v1のproperty順は`SchemaVersion`、`BenchmarkSuiteId`、`BenchmarkRunId`、`ManifestContentSha256`、`SampleCount`、`RejectedSampleCount`、`Samples`、`Aggregate`で固定する。

| Result property | JSON型 | 契約 |
| --- | --- | --- |
| `SchemaVersion` | integer | 厳密に`1` |
| `BenchmarkSuiteId` | string | 対応Manifestと同じ小文字UUID |
| `BenchmarkRunId` | string | 対応Manifestと同じ小文字UUID |
| `ManifestContentSha256` | string | 対応するcanonical Manifest bytesの小文字64桁SHA-256 |
| `SampleCount` | integer | `1..MeasurementIterations`かつ`Samples`長と一致 |
| `RejectedSampleCount` | integer | `0..MeasurementIterations-1`かつ`SampleCount + RejectedSampleCount == MeasurementIterations` |
| `Samples` | number array | 取得順を維持した、ManifestのMetric／Unitに従う有限・非負値。長さは`SampleCount` |
| `Aggregate` | object | property順を`Count`、`Minimum`、`Maximum`、`Mean`、`P50`、`P95`、`P99`に固定。CountはSampleCountと一致し、残りはSamplesから決定論的に再計算できる有限・非負値 |

Resultの浮動小数点は負の0を`0`へ正規化し、NaN／正負Infinityを禁止して、不変Cultureの最短round-trip JSON numberで表す。`Bytes`／`Count`系列の`Samples`と、Samplesから値を選ぶ`Minimum`／`Maximum`／`P50`／`P95`／`P99`だけは`0..2^53-1`の整数に限定する。`Aggregate.Count`は常にSampleCountと同じintegerであり、Unit固有の測定値範囲には含めない。一方、`Aggregate.Mean`はBytes／Count系列を含む全Unitで有限・非負のcanonical doubleを許可し、例えばSamples `[1,2]`のMeanは`1.5`とする。`Percent`系列は`Samples`と`Aggregate.Minimum`／`Maximum`／`Mean`／`P50`／`P95`／`P99`だけを`0..100`へ制限し、`Aggregate.Count`は101以上でもよい。

PercentileはSamplesを数値昇順に並べたnearest-rank法`index = ceil(p * Count) - 1`でP50／P95／P99を求める。Meanは`sum = +0.0`から開始し、Samplesの取得順に各値をIEEE 754 binary64のround-to-nearest, ties-to-evenで`sum = sum + sample`と左畳みし、最後に同じbinary64規則で`sum / Count`を1回だけ行う。途中または除算後に非有限値となったResultはRejectし、負の0は0へ正規化する。並べ替え、pairwise／Kahan等の補償加算、FMA、拡張精度による中間値保持を許可しない。Codecはこの手順でAggregateを再計算し、canonical JSON numberの再parse後のbinary64 bit patternが一致することを要求する。Result content SHA-256はResult自身へ埋め込まず、canonical Result bytesから計算してSuite Indexへ格納する。

`RejectedSampleCount`は、Timer不成立、Harness内部例外、測定中断、sample値の破損など「対象処理の成否を観測できなかった試行」だけを数える。切断、Proxy生成、cook、Commit等の対象処理が正常に実行されて失敗／Fallbackを返した試行は有効な観測であり、Rejectedへ移さない。`FailureRate + Percent`系列では単一試行を成功=`0`、失敗／Fallback=`100`、Batch試行を`失敗operation数 / 全operation数 * 100`としてSamplesへ含める。`FailureRate + FailuresPerMillionOperations`では同じ比率を100万operation当たりへ換算する。他Metricにも失敗試行の経過時間、attempt数、allocation等の定義済み値を可能な限り含め、結果の存在しない指標だけを別系列のRejectedとする。全`MeasurementIterations`が計測不能で`SampleCount == 0`となるRunはResultを生成せず、Suite全体をRejectする。

Result Schema v1は`MaxSampleCount = 1000000`、`MaximumCanonicalByteCount = 67108864`（64 MiB）をハード上限とする。`MeasurementIterations`、`SampleCount`、`SampleCount + RejectedSampleCount`はいずれもMaxSampleCount以下でなければならない。Result Loaderは`maxCanonicalByteCount`と`maxSampleCount`を必須引数として受け、呼び出し値を`1..MaximumCanonicalByteCount`および`1..MaxSampleCount`へ制限する。schema上限を暗黙使用する無引数／無制限overloadは提供しない。

Result Loaderは配列や全file Bufferを確保する前に、seek可能な入力ではfile長、非seek入力では上限付きCounting Streamでbyte上限を検査する。`Samples`より前に現れる`SampleCount`を読み、呼び出し側上限、schema上限、対応ManifestのMeasurementIterations、対応するSuite Index EntryのSampleCountと照合してからだけ固定長領域を確保する。宣言件数より多いJSON要素、過剰nesting、末尾data、上限到達後の追加readをRejectする。Suite Loaderは対応Index EntryのSampleCount以下の値を各Result Loaderの`maxSampleCount`として渡し、攻撃的または破損したResultによる無制限確保を禁止する。

Manifest Schema v1は`ManifestMaximumCanonicalByteCount = 65536`（64 KiB）をハード上限とする。Manifest Loaderは`maxManifestCanonicalByteCount`を必須引数として受け、`1..ManifestMaximumCanonicalByteCount`だけを許可する。seek可能／非seek入力ともResultと同じ事前byte検査を行い、上限内と確認するまで全file Bufferを確保しない。Manifestには可変長配列を許可せず、文字列はproperty表の個別上限も同時に検証する。

Suiteの対応関係はcanonical `GeometryBenchmarkSuiteIndex`で確定する。Index Schema v1のproperty順は`SchemaVersion`、`BenchmarkSuiteId`、`GitCommit`、`EntryCount`、`Entries`とし、Entriesは`BenchmarkRunId`のordinal昇順で並べる。各Entryのproperty順は`BenchmarkRunId`、`ManifestContentSha256`、`ResultContentSha256`、`SampleCount`、`RejectedSampleCount`とする。`IndexMaxEntryCount = 100000`、`IndexMaximumCanonicalByteCount = 67108864`（64 MiB）をハード上限とし、EntryCountは`1..IndexMaxEntryCount`かつEntries長と一致し、Run ID重複を禁止する。Index Loaderは`maxIndexCanonicalByteCount`と`maxEntryCount`を必須引数として受け、各値を`1..`各schema上限へ制限する。file／streamのbyte上限を先に検査し、Entriesより前のEntryCountを呼び出し側上限とschema上限へ照合してからだけ配列を確保する。Loaderは各Manifest／Resultを再hashし、Suite／Run ID、Manifest参照hash、件数、Indexの両content hashが一致しなければBundle全体をRejectする。

ResultとSuite IndexもManifestと同じUTF-8 BOMなし、余分な空白／末尾改行なし、固定property順、未知property禁止、canonical再serialize一致の規則を使う。Result／Indexの`SchemaVersion`はinteger `1`、Suite／Run IDは小文字UUID、`GitCommit`はManifest群と同じ小文字40／64桁、content hashは小文字64桁、件数は非負integerとしてCodecで型と範囲を検証する。Index EntryのSampleCount／RejectedSampleCountは対応Resultと一致し、EntryCountはSuite内のManifest件数およびResult件数の双方と一致しなければならない。Manifest／Result／Indexの全Loaderはschema上限以下の呼び出し側byte上限を必須とし、配列を持つResult／Indexは件数上限も必須とする。無引数、既定で無制限、または配列確保後にしか件数を検査しないAPIを提供しない。

Suite完了時は`<BenchmarkRunId>.manifest.json`と`<BenchmarkRunId>.result.json`を一時ディレクトリへ書いて再読込・再hashした後、`suite.index.json`を最後に書く。全検証成功後だけ一時ディレクトリを同じ親上の`<BenchmarkSuiteId>\`へ原子的にRenameする。Indexがない、一時suffixのまま、hash不一致、余分な未登録Result／Manifestがあるディレクトリは未完成として比較対象へ入れない。これにより同じManifestへ異なる実測データを後付けしてもIndex hash検証で検出する。

`TraceRunManifest`本体、Codec、Golden Hash、Trace bundle形式は変更せず、Benchmark Manifestの未知Schema Version、未知property、順序違反、canonical再serialize不一致は比較対象からRejectする。

各RunのManifestにある`DatasetCaseId`、Triangle／Edge／Cap／Fragment／Convex／Vertex／Primitive／Cut Plane／Batch Size軸を説明変数、対応ResultのSamples／Aggregateを目的変数として、保守的なP95／P99容量式を作る。Suite LoaderはManifest／Result／Indexをjoinした機械可読tableを生成でき、容量式の各行から元のSuite／Run／Dataset caseへ戻れるようにする。実行時Schedulerは後にこの式と実測キュー長からDeadlineまでの完了見込みを推定できるが、初期実装では係数を最適化に直結させず、Worker時間予算、Batch Size、同時Bake数、Temporary Renderer上限を決める根拠として使用する。コード変更時も同一Dataset case系列を比較する。

### 7.6 微小付属物の消去

Physics Proxyで表現しないアンテナ、細い取手、小装飾などは、プリプロセス時に`Micro Attachment`として本体から識別可能にする。斬撃の切断帯に触れたMicro Attachmentは、極小の表示Mesh／Collider／Rigidbodyを生成せず、`HitConfirmed`と同じフレームで部品全体を不可逆に消去する。即時シェーダで一度切れた部品が実Meshへの差し替え時に復活する挙動は禁止する。

- 小部品はTriangle数だけでなく、接触、支持、Gameplay、シルエットへの寄与から`VisualOnlyMicro`または`PhysicsSignificantAttachment`へ前処理分類する。`VisualOnlyMicro`には最初から専用Physics Convexを作らず、Host側のCompound ConvexとMassWeight近似へ吸収する。切断後に独立物理Fragmentへ昇格させず、切断帯へ触れた場合はMicro Attachment消去へ送る。

- 取手、脚、太い配管、支持点を含む部品など`PhysicsSignificantAttachment`には専用Convexを作り、未切断時はHostと同じRigidbodyのCompound Colliderへ含める。Component GeometryとConvexは1対1でなくてよく、1 Render Componentを複数Convexで覆うこと、複数の表示小物をHost Convexへ近似的に吸収することを許容する。

- 分類不能、小さくない、またはGameplay上重要な部品をMicro扱いで消去しない。専用Convexがなくても大型なFragmentは旧Collider共有、Temporary Proxy、または未分裂Fallbackへ残し、物理表現不能であることだけを理由に不可逆消去しない。

- 切断帯と重ならないMicro Attachmentは、Anchorが属する側の表示破片へそのまま付属させる。

- 切断帯と重なる、両側へまたがる、またはAnchor所属が曖昧なMicro Attachmentは全体を消去する。必要なら同フレームに火花や非物理の小片VFXを出し、粉砕として見せる。

- 消去状態は`AttachmentId`と`AliveMask`でObjectGenerationへ含め、即時表示、バックグラウンド表示Mesh、Cut Shell派生、再切断、保存Traceが同じ状態を参照する。古い成果物による再出現を世代検証で拒否する。

- Micro Attachmentは原則としてPhysics Proxyへ含めない。ゲーム上重要、シルエット上大きい、相互作用対象となる部品はRecipeで除外し、通常部品として処理する。

- 実装を単純にするため、Blender前処理で対象の連結成分を別Renderer／別Componentへ分離する構成を優先する。統合Meshのまま扱う必要があるAssetだけ、頂点／三角形の`AttachmentId`とGPU生存Maskを使用する。

### 7.7 GPU Micro Debris

Micro Attachment消去時は、元部品の実Geometryを事前生成したShard Clusterへ分け、Vertex Pullingと間接描画でGPU上に短時間飛散させる。1体全体が2,000～3,000 Triangle程度というAsset予算から、Micro Attachment 1件は通常20～150 Triangle程度を想定し、シーン全体の通常Active量は数千Triangle以下とする。`HitConfirmed`と同じフレームに元RendererのAliveMaskを落とし、CPUから共有`GpuMicroDebrisSystem`へ発生Event Recordを1件だけ送る。

この命中同フレーム経路は、Blender前処理または手作業で`AttachmentId`、対象Triangle、`ShardId`が事前確定したMicro Attachmentだけに使用する。任意切断によって新しく生じる小さな論理Fragmentは、即時clip段階では接続成分、面積、体積、Triangle集合を確定できないため、Shader側で微小破片と推測しない。即時切断中は通常Fragmentと同じ塊としてclip表示し、実表示Mesh切断が接続成分を確定するまで形状を保つ。

任意切断由来FragmentをGPU Micro Debrisへ移す主判定は、見た目の小ささではなく「独立した物理Convex集合で安全に表現できるか」とする。実表示Mesh切断が生成した連結な`RenderFragment`と、cook前の自前Convex切断が生成した`LogicalConvexFragment`の対応を二部グラフとして構築し、各RenderFragmentへ`PhysicsRepresentationStatus`を付ける。対応判定は論理ConvexのTopologyが完成すれば実行でき、`Physics.BakeMesh`の完了を待たない。

| `PhysicsRepresentationStatus` | 固定値 | 条件 | 基本処理 |
| --- | ---: | --- | --- |
| Pending | 0 | 対応判定前または判定Job実行中 | デブリ化、Collider生成、物理Commitを禁止し、塊表示と既存物理を維持 |
| Represented | 1 | 1個以上の専有LogicalConvexFragment集合がRenderFragmentを安全な精度で被覆 | 通常の物理Fragmentとして維持。凹形状の1 RenderFragment対複数Convexを正常系として含む |
| Missing | 2 | RenderFragmentに対応するLogicalConvexFragmentがない | 非物理デブリ候補。重要・大型なら消さずProxy再構築または未分裂Fallback |
| Shared | 3 | 必要なLogicalConvexFragmentの一部または全部を複数RenderFragmentが共有 | Shared連結成分へ解決Roleを付与し、小さく非重要な非代表だけをデブリ候補にする。複数が大型なら同じ暫定物理GroupへCommitした後、単一平面分離可能な場合だけ後追いConvex分割を試す |
| Ambiguous | 4 | 対応Edge、被覆率、専有割当を安全に確定できない | デブリ化と物理Commitを禁止し、FragmentGroup共有またはProxy再構築へFallback |

Sharedは状態だけで代表関係を表さず、各RenderFragmentへ`SharedResolutionRole`を別フィールドとして持たせる。

| `SharedResolutionRole` | 固定値 | 意味 |
| --- | ---: | --- |
| None | 0 | 未設定、またはPhysicsRepresentationStatusがShared以外 |
| Keeper | 1 | Shared連結成分で物理Convexを代表して保持するFragment |
| DebrisCandidate | 2 | 小さく非重要で、Keeperから切り離してGPU Debris化できる非代表Fragment |
| PreserveFallback | 3 | 大型、重要、同率、または安全に代表を決められず、共有物理のまま保持するFragment |

1 RenderFragment対複数の専有Convexは`Represented`とする。複数RenderFragment対複数Convexでも共有関係と被覆が決定的なら`Shared`連結成分として扱い、不確かな対応Edgeを含む場合だけ`Ambiguous`とする。RenderFragmentが専有Convexだけで十分に被覆され、余分なShared Convexを割当から除外できる場合は`Represented`としてよい。

PoCではConvex集合の厳密なBoolean Unionや完全体積証明を要求しない。切断面と親Convexの系譜、Bounds交差、RenderFragmentから選ぶ固定数の包含Sample、推定体積被覆率、境界距離を組み合わせ、固定閾値から十分離れた場合だけ`Represented`／`Missing`／`Shared`を確定する。閾値近傍、世代不一致、Edge競合は`Ambiguous`へ落とす。`Missing`または`SharedResolutionRole == DebrisCandidate`であることをデブリ候補の主条件とし、Triangle数、面積、体積、画面寸法は候補を実際に消してよいかを守る補助条件にする。

Gameplay上重要、シルエット上大きい、相互作用対象、または複数の大きなRenderFragmentが同じConvexを共有する場合は、物理対応が不完全でも微小破片として消さない。SharedのKeeperは体積、面積、意味的重要度、既存Constraint／Anchorとの関係から決め、同値時のTie-breakを固定して決定論的にする。明確なKeeperがない場合は全Fragmentを`PreserveFallback`とする。

`PhysicsRepresentationStatus`と`SharedResolutionRole`は上表の数値を明示したenumとし、default初期化を安全状態へ固定する。対応判定前は`Pending`、Roleは`None`とする。Shared以外のRoleは常に`None`でなければならず、SharedはRoleが`None`の間、デブリ化と物理Commitを禁止する。`Pending`／`Ambiguous`もデブリ化と物理Commitを禁止する。不正なStatus／Role組み合わせは不変条件違反としてTraceし、未分裂Fallbackへ移行する。

#### 7.7.1 Shared Convexの後追い単一平面解決

大型または重要な複数RenderFragmentが同じLogicalConvexFragment集合を共有し、全員が`PreserveFallback`となる場合、その共有は初回物理Commitを妨げない。該当RenderFragmentを同じ暫定FragmentGroup、単一Rigidbody、単一Collider集合へ所属させ、同じConvexを複数Rigidbodyへ複製しない。表示Fragmentは独立したまま同じ物理Transformへ追従し、Colliderが空間上の凹みやRenderFragment間を余分に覆うこと、凹み内部の部品が空中に残ることを正式な品質低下として許容する。

Shared Convexの精密化は視覚・斬撃・初回物理Commitのクリティカルパスへ入れず、Commit後の低優先度Jobでのみ行う。PoCで精密化するのは、Shared連結成分内の`PreserveFallback` RenderFragmentが厳密に2個だけで、各々を分離対象A／Bへ一意に割り当てられる場合に限る。3個以上、DebrisCandidate処理後も2個へ確定しない場合、または対応が`Ambiguous`な場合は探索を行わず`PreserveFallback`を終端結果とする。一般Convex decomposition、Boolean Union、任意個数の平面探索、組合せ的な集合分割は行わない。

`SharedConvexResolutionProfile`のPoC初期値は、`AbsoluteSeparationEpsilonMeters=0.00001`、`RelativeSeparationEpsilon=0.000001`、`MaxSupportVerticesPerFragment=65536`、`MaxSharedConvexInputCount=64`、`MaxGjkIterations=64`、`MaxPendingSharedConvexResolutionJobs=32`、`MaxConcurrentSharedConvexResolutionJobs=2`、`SharedConvexResolutionRequestSlotCount=32`、`SharedConvexResolutionWorkSlotCount=2`とする。Request Slot CountはMax Pendingと、Work Slot CountはMax Concurrentと厳密に一致させ、`0 < MaxConcurrent <= MaxPending`を要求する。積と総Native byte数をcheckedで起動時に検証し、Profile不正または固定領域の事前確保失敗ではShared Convex Resolution全体を無効化して共有物理を維持する。対象2 Fragmentのworld-space Bounds union diagonalを`D`として、`SharedSeparationEpsilon = max(AbsoluteSeparationEpsilonMeters, D * RelativeSeparationEpsilon)`をbinary64で1回求める。Count／積のoverflow、非finite Bounds、1 Job内上限超過は配列確保や部分評価前に`Indeterminate`とし、Support探索、simplex、全頂点再検証はProfile上限の固定長Native作業領域だけを使う。

Schedulerは32件の固定Request Slot、2件の固定Native Work Slot、Request Slot indexを保持する固定長FIFOだけを持ち、Runtimeで配列拡張、追加Native確保、同期待機を行わない。`Pending`は受付済みでOutcome未確定のQueued＋Scheduled／Running Job総数とする。Slot予約より先に、Main Threadの単一Admission Coordinatorが現状態から`ObjectId`、`TargetObjectGeneration`、`SharedGroupLocalId`、入力Shared Convex数をimmutableな`SharedConvexResolutionAdmissionCandidate`へ固定する。Candidate自体はSlotやNative Geometryを所有せず、以後の予約成功／失敗Eventの世代相関正本となる。Request Slot予約に成功した場合だけTaskIdを発行し、Candidate、TaskId、immutableな入力Geometry handleをSlotへ移して所有させる。Work Slotが空くまでQueued JobはRequest Slotだけを保持し、SchedulerはFrameをblockしない。

空Request Slotがない場合はJobもTaskIdも発行しない。Admission CoordinatorはCandidateの`ObjectId + TargetObjectGeneration + SharedGroupLocalId`が依然として現行Shared Groupと一致することの再検証と、当該Groupへの`CapacityExceeded` exactly-once確定を同じ原子的なcompare-and-set線形化点で行う。成功時だけCandidateのTargetObjectGenerationを使ってCapacityExceededを確定・Trace試行し、共有物理を維持して同じGeneration／SharedGroupLocalIdでは再試行しない。予約失敗とこの線形化点の間にGeneration変更、Group置換、既存終端Outcome確定のいずれかが起きた場合、Candidateを`StaleBeforeAdmission`として破棄し、Outcome、TaskId、Finished Eventを一切生成しない。新世代は別Candidateとして通常Admissionを行える。これによりSlotなしCapacity経路でも世代取得元が存在し、古いCandidateが新GroupへCapacityExceededを誤確定しない。

Queued JobをWork Slotへ移すときだけ、そのWork Slotの固定Support／simplex／検証Bufferを排他的に割り当て、Running数をMax Concurrent以下に保つ。Schedule済みJobは取消・作業領域横取りをせず、後からSupersedeされてもJob完了が観測されるまでRequest SlotとWork Slotの両方を占有する。まだScheduleしていないQueued JobがSupersedeされた場合はGJKを開始せず、FIFO上で`Superseded`へ終端化してRequest Slotを返す。Job完了時は、世代検証、Outcome確定、成果物Commitまたは破棄、Trace発行試行、入力／出力handle解放の順に行い、その後にWork Slot、Request Slotの順で厳密に1回だけ返却する。Trace失敗時も物理状態を巻き戻したりSlotをリークさせたりせず、既存Trace完全性契約でRunをIncompleteにする。

単一平面分離可能性の正本は、各RenderFragmentに属する全posed vertexがfiniteであり、その頂点凸包同士の距離が`2 * SharedSeparationEpsilon`より大きく、両側にepsilonの余白を持つstrictな平面で分離できることとする。凸包Meshは生成せず、頂点集合の最大内積点を返すSupport関数を使うbounded GJK distance Jobで暗黙の凸包を扱う。AABB／OBB／k-DOP非交差は候補平面を早期取得する十分条件として使えるが、Bounds交差だけを分離不能の証明にしてはならない。GJKが有限な最近接Witness対と距離を返した場合は、その差方向と中点から候補平面を作り、両集合の全頂点をbinary64 signed distanceで再走査して、一方が`<= -SharedSeparationEpsilon`、他方が`>= +SharedSeparationEpsilon`に完全に入ることをCommit前に検証する。距離が`2 * epsilon`以下または等号の場合は成功にしない。

凸包が交差、包含、接触、または距離が`2 * SharedSeparationEpsilon`以下なら`UnseparableBySinglePlane`とする。GJKの反復上限、頂点／作業領域上限、非finite、ゼロ法線、収束不能、全頂点検証不一致は`Indeterminate`とする。どちらも同じObjectGeneration／SharedGroupLocalIdでは再試行せず、Shared Groupをそのまま維持する終端Fallbackである。典型例は、1個の共有Convexが凹型RenderFragmentとその凹み内の別RenderFragmentを同時に覆い、両者の凸包が交差または包含する場合である。

平面検証に成功した場合だけ、現在Commit済みShared GroupのNative Convex B-rep集合をその平面で切り、正負の出力Convex集合、RenderFragment対応、finiteな正体積、MassWeight保存、FixedSupport／Constraint／Safety Tether割当を検証する。すべて成功した成果物だけを後続物理ステップ境界で原子的に別物理Groupへ差し替える。片側空、Profile上限超過、割当曖昧、質量／支持／Constraint検証失敗では部分Commitせず、`SplitValidationFailed`として元Shared Groupを維持する。AnchoredとDetachedが混在した未解決Groupは全体をAnchored側へ倒してOffset／Impulseを与えず、全員Detachedなら共有Group全体を一緒に運動させる。

精密化Job実行中に新しい斬撃が同じShared Groupへ命中しても待機しない。旧JobはGenerationで論理的にSupersedeし、Schedule済みなら完了後に成果物を破棄する。新しい切断はその時点でCommit済みの共有Convex B-repへ新しいGameplay Cut Planeを適用し、必要なら再び共有状態のまま初回Commitしてから、固定Request Slotを取得できる場合だけ新世代の後追い単一平面解決Jobを発行する。`UnseparableBySinglePlane`／`Indeterminate`／`SplitValidationFailed`／`CapacityExceeded`は同世代中の時間経過だけでは再試行せず、後続切断によってRenderFragment集合またはObjectGenerationが変わった場合だけ新しい判定対象になる。

`SharedConvexResolutionOutcome`は`Invalid=0`、`Resolved=1`、`UnseparableBySinglePlane=2`、`Indeterminate=3`、`SplitValidationFailed=4`、`Superseded=5`、`CapacityExceeded=6`の固定値とする。defaultのInvalidは精密化未実行を表し、Resolved以外はColliderを別Rigidbodyへ分裂させない。`SharedConvexResolutionFinished`はOutcome 1～6だけを受理し、Invalidを渡したBuilder／CodecはEventを公開せず不変条件違反として共有物理を維持する。内部不変条件診断には既存`FallbackActivated`とappend-onlyな`InvalidSharedConvexResolutionOutcome` Reasonを使い、Finishedの代用品にしない。Outcome 1～6はゲーム状態へexactly onceで確定し、対応する`SharedConvexResolutionFinished`の構築／enqueueはその確定結果を消費する単一Coordinatorが最大1回だけ試行する。enqueue成功時は当該Outcomeに対応するEventが厳密に1件、失敗時は0件となる。失敗時にTraceを再試行せず、Outcome、物理状態、Slot解放をrollbackせず、既存Trace完全性契約でRunをIncompleteにする。同じOutcomeから2件以上のFinished Eventを構築／enqueueすることを禁止する。`UnseparableBySinglePlane`、`SharedConvexResolutionIndeterminate`、`SharedConvexSplitValidationFailed`、`SharedConvexResolutionSuperseded`、`SharedConvexResolutionCapacityExceeded`をappend-onlyなTraceReasonへ追加する。

`SharedConvexResolutionFinished`の共通`ObjectGeneration`はEvent発生時の現世代ではなく、Slot予約前にAdmission Candidateへ固定した`TargetObjectGeneration`とする。予約成功時はCandidateをRequest Slotへ移して保持し、予約失敗時のCapacityExceededはSlotを介さず同じCandidate値を使う。完了処理は、まず現ObjectGenerationとTargetを比較し、不一致なら幾何結果にかかわらずOutcomeを`Superseded`へ上書きし、旧世代のSharedGroupLocalIdとTargetObjectGenerationを持つFinished Eventを発行してから成果物を破棄する。世代一致時だけResolvedのCommitまたは他の終端Outcomeを公開する。これにより新世代で同じLocal ID値が使用されても、Trace上の`ObjectId + TargetObjectGeneration + SharedGroupLocalId`から元の評価対象を一意に復元する。

対応グラフの結果が確定するまでは任意切断由来Fragmentを通常の塊として表示する。MissingまたはShared非代表かつ補助条件を満たした場合だけ、表示Geometry Commitと同時、または論理Convex結果が後着した時点でGPU Micro Debrisへ引き渡す。塊として切れた後に少し遅れて崩れる演出を正式な挙動として許容する。clipされた表面Triangleをその場でばらす「clip＋ポリゴン崩壊」は実装せず、切断前GeometryのTriangleを紙吹雪状に剥がす近似も使用しない。

- Blender前処理で、接続、面Normal、Material、面積上限を基準に隣接2～8 Triangle程度を同じ`ShardId`へまとめる。Triangle単位の紙吹雪感を避け、各Shard内は元Meshの形状を保ったまま共通の並進・回転を行う。小さすぎる部品はTriangle単位でもよい。

- 事前生成GeometryはVertex Buffer、Corner／Index Buffer、Shard MetadataからなるImmutableな共有`Debris Geometry Atlas`へAssetロード時に登録し、Micro AttachmentとAssetの寿命に合わせて保持する。Vertex Shaderは`SV_VertexID`等からCorner、元Vertex、ShardIdを引き、Shard単位のTransformを適用する。

- Runtime生成されたRenderFragmentは事前Atlasへ追記せず、別の固定容量`Runtime Debris Geometry Arena`へ置く。実表示Mesh切断Jobが接続成分と候補Metadataを生成し、論理Convexとの対応判定がMissingまたは`SharedResolutionRole == DebrisCandidate`を確定した後、そのJob出力からDebris用Corner Streamを生成してArenaのPage／Ring Sliceへ転送する。物理対応確定前に推測生成しない。

- `DebrisEventId`は0をInvalid用に予約した`uint`とし、1つの`GpuMicroDebrisSystem`実行セッションを1つのTrace Runと一致させ、そのRun内で1から単調発行して再利用しない。Arena Slice、Event Record、最終Draw Fence、Traceを同じIDで関連付け、Trace上の一意キーは既存共通フィールドの`TestRunId + DebrisEventId`とする。カウンタの再初期化は、全Active／Retiring EventがなくArenaが完全にQuiescentで、かつ新しい`TestRunId`を発行して新規実行セッションを開始するときだけ許可する。同じTrace Run内ではQuiescentになっても戻さず、Wrapが近づいた場合はRun終了まで新規Runtime Eventを停止してFallbackする。

- Runtime Arenaの各Sliceは`DebrisEventId`とObjectGenerationが排他的に所有する。Slice状態は固定値`Invalid=0`、`Allocated=1`、`Active=2`、`Retiring=3`、`Reusable=4`の`RuntimeDebrisSliceState`で表し、`Allocated -> Active -> Retiring -> Reusable`と遷移する。Event寿命終了後は新しいCommand Bufferから参照せず、最終Drawの後ろへUnity／Graphics APIが提供する`GraphicsFence`または同等の完了証拠を挿入する。`Retiring`から`Reusable`へ進める条件は、完了証拠が成立し、かつ設定した最小保持Frame数を経過したことの論理積とする。固定Frame遅延だけを完了証拠の代替にしてはならない。

- Runtime Arenaのライフサイクルはappend-onlyな専用Trace Eventの`RuntimeDebrisSliceAllocated`、`RuntimeDebrisSliceActivated`、`RuntimeDebrisSliceRetiring`、`RuntimeDebrisSliceReclaimed`で記録する。4イベントとも`Value0`へ`DebrisEventId`を整数値として格納する。`uint`の全範囲はIEEE 754 `double`で正確に表現できるため、signed intの`FromState`／`ToState`へIDを格納しない。イベント別の残りフィールドとTimeline解釈はTrace契約表を正本とする。

- Fence未完了または完了証拠を取得できないSliceは回収せず、Arenaが枯渇しても使用中Sliceを上書きしない。容量不足時にBuffer再確保、GPU待機、メインスレッド同期を行わず、汎用ローポリ破片、短いディザ消去、即時消去の順にFallbackする。対象環境で完了証拠を利用できない場合はRuntime実Geometry経路を無効化する。Arena容量、Page寸法、最大同時Upload、Fence待ち時間、Retiring数、Allocation失敗数をO-031／T-063で測定する。

- Event RecordはGeometry Offset、発生Transform、切断面法線、親Rigidbodyの点速度、基底色、乱数Seed、開始時刻、寿命を持つ。各ShardにGameObject、Transform、Rigidbody、Colliderを作らず、位置を`p(t) = p0 + v0 * t + 0.5 * g * t^2`、回転をSeed由来の軸・角速度・経過時間からShader内で直接求め、CPUの毎フレーム更新を行わない。

- 全Active Eventを固定長BufferとIndirect Command Bufferへまとめ、同じMaterialでは原則1 Draw、Material差を含めても2～3 Draw以内を初期目標とする。EventごとのBuffer再確保、Geometry再転送、GameObject生成を行わない。

- 破片は親の点速度へ切断面法線方向の初速とSeed由来のばらつきを加えて飛ばす。寿命の初期候補は0.3～0.8秒とし、終了時は半透明BlendではなくZWrite可能なOpaque／Alpha Clipのディザで消滅させる。Shadow Pass、Collider、Light Probe個別更新は持たない。

- ディザ閾値はワールド座標または破片ローカル座標から生成する安定Noiseを使用し、左右眼で同じ表面点が同じ生存判定になるようにする。スクリーン座標だけに依存するランダムディザは使用しない。

- 影、Motion Vector、個別ライト、破片同士と地面の衝突を無効化する。飛散範囲を含む保守的Boundsを持つ共有Batchへまとめ、間接描画またはVFX Graph Mesh Particleで描画する。

- 初期予算は、1 Event 20～150 Triangle、通常Active合計500～3,000 Triangle、品質低下開始5,000～8,000 Triangle、Hard Cap候補10,000 Triangle、Active Event 8～32とする。1～2万TriangleはMicro Attachment通常仕様ではなく、全身／大きめ破片まで流した場合のStress Testに限定する。Triangle数に加えて両眼の画面占有面積とOverdrawを予算化し、超過時は古いEventの寿命短縮、Shard統合、汎用破片、火花／Quad、即時ディザ消去の順で品質低下する。

Phase 1のPoCでは、手作業で事前Shard化した専用テストMeshを入力し、VFX Graphで外観、汎用破片Fallback、URP／XR適合性を素早く検証する。この段階では任意切断結果の微小判定、Triangle抽出、AliveMask連携を受け入れ条件に含めない。実Geometry経路は固定長Event Buffer、Geometry Atlas、Shard Metadata、解析運動Shader、`Graphics.RenderPrimitivesIndirect`／同等APIによる専用Vertex Pulling実装を第一候補とする。GPU Eventなど実験的機能への依存は必須にしない。

### 7.8 全体低重力

空中物体斬りの猶予を自然に増やし、世界全体の挙動を統一するため、個別の空中斬り補助ではなく全体低重力を初期方針とする。PoCの仮値は標準重力の約0.5倍、`(0, -4.9, 0) m/s^2`とするが、最終値はプレイテストで決める。

- `WorldPhysicsProfile`を重力の唯一の設定元とし、起動時に`Physics.gravity`へ適用する。物理予測、解析軌道、GPU Micro Debris、その他の非物理VFXも同じ値を参照し、`-9.81`などを各実装へ直接記述しない。

- 重力値はInspectorまたは開発用設定から変更可能にし、初期比較候補を0.35G／0.5G／0.7G／1.0Gとする。各Runの重力ベクトルとProfile版をTrace／Run Manifestへ保存する。

- PoC初期は反発係数、Drag、切断分離Impulse、モブのジャンプ／落下Animation、破片寿命を低重力専用に作り込まず、既定値または仮値を使用する。実プレイで具体的な違和感が確認されてから個別に調整する。

- `Time.timeScale`による常時スローモーションは重力調整の代用にせず、入力、斬撃波、非同期処理、物理予測の時間軸を通常速度に保つ。PoCでは対象別Gravity Scaleも導入せず、必要性がプレイから判明した場合だけ拡張する。

## 8. 世代管理と非同期制御

各SlashはGestureのLatch時に単調増加する`SlashGeneration`を持つ。各切断対象は確定状態を示す`ObjectGeneration`を持ち、`SlashFront`のSweepによる実命中が確認され、Pending Cutを登録した時点でだけ更新する。空振り、候補列挙、投機ジョブ開始では対象世代を進めない。

投機ジョブは開始時の`BaseObjectGeneration`、`SlashId`、`SlashGeneration`、命中した`FrontEdgeId`、`SlashFrame`に加え、支持判定を使用する場合は`AnchorGeneration`と`SupportGraphGeneration`を保持する。ジョブを強制キャンセルするのではなく、完成時およびCommit時に、実命中と各識別子・世代・前提条件を検証する。一致しない成果物はコミットせず破棄し、安全に再利用できる中間資産だけを回収する。

状態は1個のObject単位enumへ集約せず、物理分裂、Fragment支持、切断境界の露出、Geometry完成度、非同期Work Resultの採否を直交する軸として保持する。同じ対象は、Activeな新規境界、Dormantな過去境界、Suppressedな分類待ち境界、完成済み実Mesh、未完成Colliderを同時に持ち得る。

| Object／FragmentGroupの物理状態 | 意味 | 許可される処理 |
| --- | --- | --- |
| Stable Unsplit | 物理的には未分裂で、1つのRigidbody／Colliderが確定済み | 新規切断の物理基底に使用 |
| Pending Physics Split | Provisional構築不能時の保守Fallback。FragmentGroupの1 Rigidbody／旧Colliderを共有し、表示と論理破片だけが分離済み | Convex生成とBakeを待ちながら、後続切断と外力をGroup全体で受理 |
| Pending Support Classification | FragmentGroup内にUnknownなLogicalFragmentが1つ以上あり、物理分裂方法をまだ決定できない | 旧Rigidbody／Collider／Constraint／Transformを維持し、Group全体のOffset、Impulse、解析運動を禁止する。支持再分類と背景Geometry処理を進めつつ、既知のActive境界だけはclip／Stencil／仮Capを許可する |
| Pending Anchored Split | Provisional構築不能時の保守Fallback。固定側分類済みだがCollider未分裂で、旧Colliderを固定したまま自由側だけを衝突なしで仮表示 | Anchor／接続判定結果を維持し、完全Convex切断とBakeを待つ。共有物理へ切断Impulseを与えない |
| Provisional Physics Split | 全子Detachedと確定後、子ごとのRigidbodyが旧cook済みConvexを再利用して外界Collisionと分離運動を先行するが、Final Collider／質量特性は未完成 | Sibling Collisionを無効化し、Provisional Constraintで再侵入を抑えながら後続切断、外力、非同期cookを受理 |
| Provisional Anchored Split | 支持分類済みの各子がProvisional Actorを持ち、Anchored子は固定、Detached子だけがDynamic。Final Collider／質量特性は未完成 | 固定側のOffset／Impulseを0に保ち、自由側の外界Collisionと分離運動、後続切断、非同期cookを受理 |
| Provisional Fault Frozen | 公開後の非finite、速度上限超過、Constraint破綻をGroup単位で封じ込めた不可逆な物理安全状態 | 全Actorを直前のfinite姿勢でKinematic化するかGroup全体をPhysics Sceneから除外し、Constraint、外力、新規物理分裂、Final handoff、自動復帰を禁止する。表示Geometryの背景処理と最終破棄だけを許可 |
| Stable Fast Cook | Fast Cook Colliderで物理分裂済み | 通常物理を継続し、必要なら低優先度Upgradeを予約 |
| Physics Upgrade Pending | 別MeshをFast Simulationで再Bake中 | 現Colliderを維持し、世代変更時はUpgradeを破棄 |
| Stable Fast Simulation | Fast Simulation Colliderへ安全に差し替え済み | 長寿命・高接触破片として通常物理を継続 |

| `LogicalFragment.SupportState` | 意味 | 許可される処理 |
| --- | --- | --- |
| Anchored | FixedSupportAnchorから到達可能 | OffsetとImpulseを0に保ち、固定物理へ追従 |
| Detached | Anchorから到達不能であることを証明済み | Active境界を介した仮分離と、Collider完成後の物理分裂を許可 |
| Unknown | 分類未完了、世代不一致、または接続が曖昧 | 動かさず再分類またはFallbackを待つ |

| `CutBoundaryRecord.ExposureState` | 条件 | 描画・運動 |
| --- | --- | --- |
| Dormant | 境界両側のFragmentが`Anchored`で相対移動しない | 境界単独ではclip、Stencil、仮Cap、Offset、Impulseを要求せず、背景Geometry処理を継続。ただし`HasDetached`／Cull失効済みOperationでは補助CapとしてStencil／Cap Batchへ投入され得る。Offset、Impulse、切断演出は禁止のまま |
| Active | 分類結果から境界を露出可能と証明済み | clip、Stencil、仮Cap、切断演出を起動可能。Offset／Impulseは境界のSupport決定表に加え、FragmentGroup物理状態が許可する場合だけ適用 |
| Suppressed | 分類不能で、安全な露出状態を決定できない | clip、Stencil、仮Cap、Offset、Impulseを起動せず、再分類後にDormantまたはActiveへ遷移 |

SupportからExposureへの変換は次の完全な決定表を正本とする。本作では安全性と`PendingSupportClassification`の無運動契約を優先し、`Detached + Unknown`も`Suppressed`とする。正負は対称であり、表にない組み合わせを実装側で推測してはならない。

| 正側Support | 負側Support | Exposure | Offset／Impulse |
| --- | --- | --- | --- |
| Anchored | Anchored | Dormant | 両側とも禁止 |
| Anchored | Detached | Active | Detached側だけ許可 |
| Detached | Anchored | Active | Detached側だけ許可 |
| Detached | Detached | Active | 両側とも許可 |
| Anchored | Unknown | Suppressed | 両側とも禁止 |
| Detached | Unknown | Suppressed | 両側とも禁止 |
| Unknown | Anchored | Suppressed | 両側とも禁止 |
| Unknown | Detached | Suppressed | 両側とも禁止 |
| Unknown | Unknown | Suppressed | 両側とも禁止 |

上表は境界ごとのExposureを決めるために使用し、Object／FragmentGroupの物理状態を境界ごとに決めてはならない。FragmentGroup物理状態は、現在Groupに属する全LogicalFragmentの`SupportState`を次の優先順位で集約して一意に決める。

1. `Unknown`が1つでもある場合は`PendingSupportClassification`。
2. 全LogicalFragmentが既知で、`Anchored`が1つ以上ある場合は`PendingAnchoredSplit`。
3. 全LogicalFragmentが`Detached`の場合は`PendingPhysicsSplit`。

この集約は切断追加、再分類、Anchor／Graph世代変更のたびに全LogicalFragmentを対象として再評価する。`PendingSupportClassification`が選ばれた場合は、別の既知境界が`Active`でもGroup全体のOffset／Impulse／解析運動を禁止する。ただし、そのActive境界固有のclip／Stencil／仮Capは描画できる。`Suppressed`境界の表示禁止は常に優先する。

| `CutBoundaryRecord.GeometryState` | 意味 | 許可される処理 |
| --- | --- | --- |
| Pending | 実Fragment Meshが未完成 | Active境界が要求する即時Rendererと、Operation規則が選んだ補助Dormant Capで補う。Dormantは単独では要求せず、Suppressedは常に抑止 |
| Ready | 最新世代の実Geometry成果物が完成し、Commit待ち | Active境界が要求する即時Rendererと選択済み補助Dormant Capを継続したまま世代と命中条件を検証し、描画フレーム境界で実Meshを適用 |
| Committed | 実Geometryの表示適用に成功済み | 同じ原子的Commitで即時Rendererの対応仕事を回収し、後続切断の表示Geometry基底に使用。物理Commit完了は含意しない |

| 非同期`WorkResultState` | 意味 | 許可される処理 |
| --- | --- | --- |
| Scheduled | Work Itemが予約済み | Job開始またはSchedule前取消を待つ |
| Running | Jobが実行中 | 完了結果を生成し、直接Unity Objectを変更しない |
| Ready | 成果物が完成し、Commit検証待ち | 最新の前提・各Generationと照合 |
| Stale | 完成時点で前提または世代が古い | Commitせず、必要なら再利用可能な中間資産だけ回収 |
| Committed | 有効な成果物を境界タイミングで適用済み | 二重Commitを禁止し、解放処理へ進む |
| Disposed | 成果物と一時領域を解放済み | 以後の適用・参照を禁止 |

`Dormant`、`Active`、`Suppressed`はCut Plane全体でもObject全体でもなく、切断によって生じた連結な論理Fragment境界ごとの属性とする。同じCut Plane上でも、ある境界Loopは両側固定でDormant、別の境界LoopはDetached部品に接してActive、分類不能な境界LoopはSuppressedとなり得る。`CutBoundaryRecord`は少なくとも`BoundaryId`、`CutPlaneId`、正負の`LogicalFragmentId`、`ExposureState`、`GeometryState`、作成時のObject／Anchor／SupportGraph各Generationを保持する。

支持Topologyの最小ランタイムモデルは`FixedSupportAnchor`、`FixedSupportNode`、`FixedSupportEdge`、`LogicalFragment`、`LogicalCutOperation`、`CutBoundaryRecord`から成る。切断面で失われるEdgeを反映してAnchor到達性を再計算し、各LogicalCutOperationが保持する直接子SupportStateから`Incomplete／FullyFixed／HasDetached`を導出する。後続切断で直接子を置換する場合は祖先OperationのFully Fixed Cullを先に失効させ、その後Detached成分に接する過去のDormant境界を同一フレームでActive化する。表示MeshやColliderが未完成でも、Cut Plane、論理Fragment、LogicalCutOperation、Cull失効履歴、Graph Edge、世代情報は破棄しない。

固定支持切断では次を不変条件とする。

- `Kerf`は常に0であり、仮分離Offsetとは別設定とする。
- 固定側の表示Offsetと切断Impulseは常に0とする。
- 自由であると証明できない`Unknown`側は動かさない。
- `Suppressed`境界ではclip、Stencil、仮Cap、Offset、Impulseを起動しない。
- `PendingSupportClassification`中は旧Rigidbody、Collider、Constraint、Transformを変更しない。
- `PendingSupportClassification`中でも既知のActive境界のclip、Stencil、仮Capは許可するが、Group全体のOffset、Impulse、解析運動は禁止する。
- Dormant化してもCut Plane、論理Fragment、Graph Edge、`ObjectGeneration`を保持する。
- 後続切断では最新面だけでなく、蓄積された全切断面に対してAnchor到達性を再評価する。
- Dormant解除時はDetached成分に接する関係境界を同一フレームでActive化する。
- 投機結果のCommit条件へ`AnchorGeneration`と`SupportGraphGeneration`を含める。
- 同一位置の正負Capを通常カラーPassで常時両面描画しない。

## 9. リグ付き人形の切断

関節をフリーズできるため、切断時点でアニメーション世界から静的破壊世界へ移送する。Animatorの現在姿勢とボーン行列をスナップショットし、表示は同じ姿勢のまま即時clipする。バックグラウンドではCPUスキニングで現在姿勢を通常Meshへ焼き込み、以後は一般プロップと同じ切断処理へ合流する。

- 身体・衣服・髪など複数Skinned Meshへ共通の切断平面を適用する。

- 身体または統合Cut Shellだけで断面を生成し、衣服・髪は原則clipのみとする。

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

| 層 | 用途 | 品質契約 |
| --- | --- | --- |
| Display Mesh | 通常表示と最終破片 | 外観優先。複数submeshを許容 |
| Stencil Cut Shell Base | 即時仮断面用Cut Shellの基底 | finite、有効index、共有Edge位置一致、Topology Edgeごとの有向incidenceが0。Self-intersection、均衡Non-manifold、Duplicate／Coincidentを許容 |
| Closed Cut Component／Connectivity Metadata | 独立部品の切断、Cap、分離判定 | Componentごとに閉鎖可能。Component間のIntersection／Overlapを許容し、SurfaceAdjacency／AttachmentPatch Graphを持つ |
| Physics Proxy | 接触とConvex切断 | 少数の低頂点Convex／Compound。各Convexは有効な閉凸形状だが、Compound内の相互Overlapを許容 |

Blender側の共通変換工程として、Transform適用、原点・単位統一、共通マテリアル化、三角形化、Closed Component抽出、Surface Adjacency／Attachment Patch／固定長Attachment Link生成、小部品の`VisualOnlyMicro`／`PhysicsSignificantAttachment`分類、Stencil Cut Shell Base／Compound Physics Proxy生成、Unity向け書き出しをプリセット化する。Component同士が食い込んでいてもBoolean Unionを標準工程へ入れない。Stencil Cut Shell Baseは`OrientedShellValidator`だけを必須Gateとし、高価な全体自己交差／inside-outside検証を要求しない。実行時Cut ShellはUnity側でStencil Cut Shell Baseまたは直前のStable Cut Shellから派生させる。Micro Attachmentには安定した`AttachmentId`、Bounds、Anchor、AttachmentPatch／Link Endpoint、重要部品除外フラグを出力する。

これらは論理的な役割であり、役割ごとにUnity Meshを必ず複製するという意味ではない。Display GeometryがStencil／Closed Component契約も満たす場合は同じVertex／Index BufferとTopology Metadataを参照する。製品Asset Schema、Preprocess Cache、Build、Runtime FallbackはStrict Solid用の参照や生成物を持たない。

#### 10.2.1 建物用Structural Slab候補

Poly Pro Universe実AssetのBlender人力調査から、典型的な建物を装飾付きの厚い壁板4枚以上で外周構成する近似は、Boolean Unionや建築構造解析を要求せず自動化しやすい候補とする。各`StructuralSlabComponent`は独立して閉鎖・切断・CapできるRender／Stencil Componentと、原則1個の直方体Physics Convexを持ち、建物1周分を同じ固定FragmentGroup／Compound Rigidbodyへまとめる。入口や通行可能な開口を1箱で塞ぐ場合だけ、左右と上部等の少数箱へ分割する。窓枠、柱、モールド、看板等はVisualOnlyMicroまたはPhysicsSignificantAttachmentとしてSlabへ接続し、装飾Geometryを構造Convexへ忠実に反映しない。

各Slabの下端両側を初期`FixedSupportAnchor`候補とし、外周角や意図した構造接続だけを少数Attachment Linkで結ぶ。同じSlabを完全横断する2切断によって両Ground Anchorおよび外周Linkから切り離された中間成分はDetachedとなり、Collider Bake後に動的大型Fragmentへ移行できる。1回の切断で必ず落下する、または任意の2平面で必ず分離するとは保証せず、実際のCutConnectivityGraph到達性を正本とする。

Phase 0.2では個別建物の最終Recipeを作らず、Poly Pro Universeの`Building`カテゴリを人間が事前観察して、概ね直方体の外周、単純な矩形またはほぼ矩形のFootprint、4面程度の壁へ分けやすい構造を持つ「豆腐型建物」だけを明示的な処理対象へ選ぶ。塔、ドーム、橋、複雑な中庭、多棟連結、段状高層部等はPhase 0.2の自動処理失敗とせずScope外にする。窓枠、柱、モールド、看板、入口の張り出し、壁への小物食い込み、複数Object／Componentは豆腐型の除外理由にしない。対象内から、大きな平面状閉Component、保守的OBB、外周配置を共通Presetで抽出できた少数を`StructuralSlabCandidate`としてT-087の非公開Fixtureへ利用してよい。製品用のGround Anchor、入口保持、角Link、装飾分類、Safety Tether MetadataはPhase 5.5のAsset Recipeで確定し、早期成功を全Buildingまたは全Assetへ一般化しない。

#### 10.2.2 早期Licensed Fixture選抜

Phase 5.5の全Asset対応前に、Phase 0.2でSyntyおよびPoly Pro Universe等のライセンスAssetから多数のモデルを固定版Blenderへ一括投入し、簡易処理だけで成功した少数を表示テストと性能測定へ使用する。これは製品用Asset前処理の前倒しではなく、手作業、Asset別Recipe、最終外観調整を原則行わない使い捨て可能な選抜工程である。ただし、Poly Pro Universeの`Building`だけは全形状への自動対応を試みず、10.2.1の豆腐型条件を人間が判定して固定Catalogへ記録した対象だけを一括処理へ投入する。この人力作業はMesh修正や成功結果を見た後の選別ではなく、処理前のScope制限である。Scope内の失敗Assetを個別修理して網羅率を上げず、時間上限または検証失敗で即Rejectして次の候補へ進む。Poly Pro Universeの人力調査で、元MeshへBlenderの`Select All by Trait -> Non Manifold`相当を適用して`F`で封鎖する操作が多くの単純開口へ有効だったため、この操作を一般解ではなく共通簡易Presetの探索候補として含める。

```text
Licensed Source FBX群
  -> Import／Transform・単位適用
  -> Object／Material／Triangle統計
  -> 三角形化
  -> 重複頂点・退化面・孤立要素の最低限除去
  -> 面向き再計算
  -> OriginalからBoundaryLoopFill／BlindNonManifoldFill候補を独立生成
  -> 基底Render／Convex Gate
  -> Original／Direct Decimate系列生成
  -> Voxel64／Voxel128／Voxel256基底と限定Post-Decimate系列生成
  -> VariantごとのRender／Convex再検証
  -> 成功したLicensed Render／Convex Fixtureだけを非公開Datasetへ固定
```

穴封鎖候補は元Geometryを上書きせず、`Original`、`BoundaryLoopFill`、`BlindNonManifoldFill`を独立Variantとして生成する。Component同士のBoolean Union、別Object間の頂点結合、位置だけを根拠とする開口の統合は行わない。

- `BoundaryLoopFill`を本命経路とする。面がちょうど1枚だけ接続するBoundary EdgeだけをObject／Topology Componentごとに抽出し、Boundary部分グラフの全頂点次数が2である閉Loopだけをstableな最小Topology ID順へ列挙する。Profileの穴径、平面誤差、個数を満たすLoopを1件ずつ個別に`F`相当で封鎖し、生成N-gonを固定Presetで三角形化する。Open Chain、分岐、重複Edge、別Loop間共有頂点、上限超過は推測修復せず当該VariantをRejectする。

- `BlindNonManifoldFill`は人手操作に近い探索用経路とする。固定Blender版の`mesh.select_non_manifold`で使うBoundary／Wire／Multiple Faces／Non Contiguous／Verticesの各booleanと、`F`相当の`mesh.edge_face_add`、三角形化Operator引数をPresetの必須値としてhash対象へ含める。Object内のTopology Componentごとに固定順で実行するが、成功を仮定しない。離れた開口を結ぶ巨大面、自己交差N-gon、Bounds横断面を生成し得るため、最終Render／Convex GateとHard Bounds／表面偏差Gateを必須とする。Licensed選抜では閉Solid、自己交差なし、体積一致を証明せず、合格しても初期Profileでは`BenchmarkOnly`に限定する。別Object／別ComponentをまたぐFillやBoolean Unionは行わない。

両経路とも生成面へ専用の内部Materialや製品品質UVを要求せず、Phase 0.2では単色再構築可能なTopology Fixtureとして扱う。`BoundaryLoopFill`が失敗しても`Original`、Voxel、別Reduction Variantを連鎖Rejectせず、`BlindNonManifoldFill`の成功をPhase 5.5の一般修復能力またはAsset互換率の根拠にしない。

初期VariantIdはRender Tierの`boundaryfill`／`blindfill`へ固定し、`DatasetCaseId`を`fixture_017.render.boundaryfill`等とする。Phase 0.2ではFill結果からDirect Decimate子を展開せず、Triangle数違いは既存Original／Direct／Voxel系列で確保する。Convex Fixtureは合格したRender候補または直接構築したConvex入力を`ConvexBuild`の別Variantとして生成できるが、親Variant自身と同じDatasetCaseIdを再利用しない。ConvexBuild EntryとDataset Indexは親のTier、Variant ID、DatasetCaseId、Geometry hashを保持し、親が`BenchmarkOnly`なら全子孫Convexも`BenchmarkOnly`へ固定する。特に`blindfill`由来Convexを`Selected`へ昇格させない。

選抜Tierは次に分離する。同一Assetが複数Tierへ合格してもよい。

| Tier | 用途 | 早期合格条件 |
| --- | --- | --- |
| `Render Fixture` | 即時clip、Mesh切断Kernel、MeshData公開、見た目確認 | Profileのfinite／epsilon／Bounds／Triangle／連結成分Gateを満たす。開放面、複数Submesh、複数連結成分を許容 |
| `Synthetic Watertight Test Fixture` | Cap Loop、反復切断、Stable Fragment Meshの既知正解 | プログラムまたは固定版Blenderスクリプトで生成する箱、柱、凹形状、複数Shell等。ライセンスAsset由来の生成成功を要求せず、製品Preprocessor成果物にしない |
| `Convex Fixture` | Convex切断、`Physics.BakeMesh`、Cook Probe | Render候補または直接生成入力から、ProfileのHull数、Hull頂点／Face数、合計頂点、正体積上限内の単一Convex／簡易Compoundを生成できる |

Licensed canonical schema v1のTierは`Render`／`Convex`だけとし、`solid` tokenを定義・予約・受理しない。`SolidSignedVolumeV1`、`SolidGeometryValidatorV1`等は別のSynthetic Watertight Dataset専用契約に隔離し、`EarlyFixtureSelectionReport`、`LicensedRepresentativeDatasetIndex`、`LicensedFixtureSelectionReceipt`、Licensed Profile hashへ含めない。このschema v1は未実装なので、旧Licensed Solidとの移行互換性を設けず初期正本を置き換える。

Licensed Render Fixtureには、三角形化後の`Original`、元表面へ直接適用する要求Triangle Target `Tri100`／`Tri500`／`Tri1000`／`Tri2000`／`Tri5000`／`Tri10000`、Topologyを再構成するVoxel Remesh系列を候補として持たせる。`Tri1000`等は正確な出力Triangle数ではなくDecimateへ与えた要求Preset名であり、実際の規模軸はcanonical化後の`ActualOutputTriangleCount`とする。Reduction比は`ActualOutputTriangleCount / SourceTriangleCount`から導出する。Direct DecimateとVoxel後Post-Decimateにはそれぞれ固定Presetだけを使用し、手動ウェイト、局所修正、Target別の見た目調整を行わない。Synthetic Watertight Test Fixtureの規模系列はGenerator引数で直接作り、Licensed Reduction Reportへ混在させない。

- Direct Decimateでは、三角形化後のVariant全体について`RequestedDecimateRatio = TargetTriangleCount / SourceTriangleCount`をbinary64で1回計算し、固定Blender版のDecimate Ratioへ設定して1回だけ評価する。複数Objectでは同じRatioを各対象へ適用し、最終的に再三角形化してVariant全体のActualを数える。Blender Decimateは正確なFace／Triangle count指定ではないため、Target一致を合格条件にせず、Targetへ合わせる反復探索、Target別の局所修正、Triangleの追加／削除による帳尻合わせを行わない。元MeshがTarget以下なら増やさず、そのTargetは`NoOp`としてReportだけへ記録してGeometryを複製しない。元MeshがTargetを1 Triangleでも上回る場合は削減率にかかわらず生成を試みる。Voxel後Post-DecimateもVoxel基底をSourceとして同じ規則を使う。

- 異なるTargetが同じ出力hashになった場合はGeometryを1件へ重複排除し、ReportにAlias関係を残す。

- 各Licensed Variantは実際の出力Triangle数でRender Gateを再検証する。Targetからの差だけをGeometry RejectまたはProfileUnsupported理由にせず、ReportのTarget、Actual、両者から導出できる偏差を選抜時に参照する。Synthetic Watertight Test Fixtureは別のGenerator／Validatorでwatertight、面向き、退化、自己交差等の意図した正解条件を検証する。元Assetや別Targetまで連鎖Rejectしない。複数Presetが同じ実Triangle帯へ集中してもTarget値を実数として偽装せず、Dataset選抜では不足しているActual帯を埋める少数を優先してよい。

- 元から100／500 Triangle級の小プロップは`Original`として低Triangle帯へ含め、より大きなAssetをTri100／Tri500へ強制削減したVariantと区別する。極端なReduction Variantは形状検証を通れば性能限界測定用`BenchmarkOnly`として保持できるが、見た目代表値には使用しない。

- Licensed RenderのTriangle Target、Synthetic Watertight Fixtureの規模引数、Convexの頂点／Hull／Compound削減は別系列とし、`Tri100`等をCollider品質の指定として解釈しない。

Voxel Remesh基底はSourceとTriangle数が同じ、近い、またはSourceより増える場合でも、閉形状化、自己交差の解消可能性、連結、面配置が異なるため生成する。最長ローカルBounds辺を基準に`Voxel64`=`BoundsMax / 64`、`Voxel128`=`BoundsMax / 128`、`Voxel256`=`BoundsMax / 256`の相対Voxel Sizeを初期Presetとし、World Scaleだけで解像度が変わらないようにする。Voxel基底とSourceの出力hashが一致する場合だけAlias化できる。

Variant爆発を避けるため、初期Post-Decimate行列は次へ限定する。`Base`はVoxel Remesh直後を意味する。

| Voxel基底 | 生成するPost-Decimate候補 |
| --- | --- |
| `Voxel256` | `Base`、`Tri10000`、`Tri5000` |
| `Voxel128` | `Base`、`Tri2000`、`Tri1000` |
| `Voxel64` | `Base`、`Tri500`、`Tri100` |

Licensed Voxel Variantは`fixture_017.render.vox128.base`のようにRender Tierを含む`DatasetCaseId`を使う。Voxel基底と各Post-Decimate結果はRender Gateを通し、形状検証を通ってもSilhouette／表面偏差が大きい結果は`BenchmarkOnly`へ分類する。簡易なBounds差と元表面へのsampled距離はReportへ残すが、体積誤差、watertight性、自己交差なしをLicensed合否へ使用せず、Phase 0.2ではSurface Projectionや手動修正を行わない。

早期Licensed Fixtureの`DatasetCaseId`は`{SourceFixtureId}.{TierToken}.{VariantId}`で構築し、TierTokenを`render`／`convex`へ固定する。例えばDirect Decimateは`fixture_017.render.original`、Voxelは`fixture_017.render.vox128.base`を使う。SourceFixtureIdは最大64文字、VariantIdは最大48文字とし、構築結果が既存Manifestの`[A-Za-z0-9._-]{1,128}`へ収まり、Dataset内で一意であることをCodecが検証する。Synthetic Watertight DatasetはSourceFixtureIdを使わず、Generator IDを含む別Case規則とする。Benchmark時の実入力は各生成Variantなので、既存`GeometryBenchmarkRunManifest.InputTriangleCount`には`ActualOutputTriangleCount`を格納し、`OutputTriangleCount`は切断等のBenchmark対象処理後のTriangle数として従来どおり使用する。Source、Tier、Process Mode、Voxel Size、Post-Reduction Target、Reduction比、Applied状態はDatasetCaseIdで対応する`EarlyFixtureSelectionReport`から復元し、Benchmark schemaへ意味の重複するpropertyを追加しない。

早期工程ではTrusted Exteriorへの投影、製品品質の見た目を保つReduction、UV／Material再構成、Micro Attachment／FixedSupportGraph、意味を伴う開口保持、車・建物別Recipeを必須にしない。Boundary Fillは既存開口を機械的に塞ぐだけで、入口、窓、車内等を意味的に保存する保証を持たない。ProfileのHard Bounds、表面偏差、決定論的Triangle／Component／Voxel Cell上限を満たせないVariantはGeometryRejectedまたはProfileUnsupportedとする。Boundary／Non-Manifold統計は修復効果の診断として記録できるが、Licensed RenderをSolid Gateへ通さず、全Mesh自己交差候補を列挙しない。120秒／4 GiBの運用上限超過はResourceLimitExceededとして再試行し、形状不合格にはしない。未採用Asset／VariantはPhase 5.5まで保留する。

簡単なAssetだけが残る選抜バイアスを隠さないため、Source Catalog全数、Phase 0.2 Eligible数、Buildingの人力Scope除外数、処理投入総数、Tier別合格数、`AssetCategory`、固定境界の`SourceTriangleBand`、`GeometryProcessMode`、`ReductionVariant`、`SourceTriangleCount`、`ReductionTargetTriangleCount`、`ActualOutputTriangleCount`、`ReductionRatio`、`ReductionApplied`、`VoxelResolutionCells`、`VoxelSize`、`PostReductionTargetTriangleCount`、Bounds差、sampled表面距離、連結成分、Boundary Edge、非多様体Edge、向き不整合Edge、全Attemptの処理時間／Peak Working Set／Tool結果、Reject Stage／ReasonをCatalogと`EarlyFixtureSelectionReport`のjoinから復元できるようにする。家具、車、豆腐型Building、道路設備、小物と複数Triangle帯から少数ずつ固定し、最速の単純形状だけに偏らせない。ただし、この合格集合からBuildingカテゴリ全体、各ライセンスAsset集全体の互換率、Phase 5.5の成功率を主張しない。

公開可能な合成Fixtureをcanonical Benchmark Datasetの正本として維持する。Synty／Poly Pro Universe等に由来するFixtureは同じHarnessとManifest／Result schemaで測る非公開の`LicensedRepresentative` Datasetとし、合成入力から得た容量式が実Asset分布でも大きく外れないかを確認する補助系列に限定する。入力Geometry、派生Mesh、選抜レポートのAsset名対応表は非公開Asset Repoへ置き、公開RepoにはライセンスGeometryを含まないScript、Schema、匿名化した集計だけを置く。公開可能性が不明な結果は非公開を既定とする。

##### EarlyFixtureSelectionProfile v1

選抜Gate、形状品質区分、決定論的入力上限、運用上の資源上限はversion付きcanonical JSON `EarlyFixtureSelectionProfile`へ固定する。Profile v1のproperty順と値は次を初期正本とし、変更時はProfile hashと全派生Fixtureを無効化する。

この設計改訂時点ではProfile／Source Catalog／Report／Dataset Index v1のCodec、Loader、Golden Fixture、事前登録artifactはまだ実装・生成されていない。したがって`ProfileId`の`early-licensed-v1`化、`GeometryProcessMode`／`RejectStage`追加、親Variant参照property、Building Scope property、10,000 Triangle帯追加、Licensed Solid Tier／Volume／Self-intersection propertyの削除は、公開済みv1の移行ではなく未実装の初期v1正本の置換として扱う。v1 artifactを1件でも生成・登録した後は既存v1のproperty順、enum値、意味を変更せず、同種の変更にはSchemaVersionを上げて旧v1 LoaderとGolden bytesを維持する。

| Property | JSON型 | v1値／意味 |
| --- | --- | --- |
| `SchemaVersion` | integer | `1` |
| `ProfileId` | string | `early-licensed-v1` |
| `AssetCategories` | string array | 固定順で`["Furniture","Vehicle","Building","RoadEquipment","SmallProp","Character","Other"]`。Reportで許可するカテゴリ集合 |
| `SourceTriangleBandUpperBounds` | integer array | 固定長6、厳密に`[100, 500, 1000, 2000, 5000, 10000]`。Source Triangle帯の上限 |
| `AbsoluteEpsilonMeters` | number | `0.000001` |
| `RelativeEpsilon` | number | `0.000001`。Asset epsilonは`max(AbsoluteEpsilonMeters, BoundsDiagonal * RelativeEpsilon)` |
| `MinBoundsDiagonalMeters` | number | `0.001` |
| `MaxBoundsDiagonalMeters` | number | `1000` |
| `MinNonZeroExtentAxes` | integer | `2`。各軸extentがAsset epsilonを超えるかで数える |
| `AutoFillMaxHoleDiameterRelative` | number | `0.02`。Boundary Loop最大頂点間距離／BoundsDiagonal |
| `AutoFillMaxHoleDiameterMeters` | number | `0.05`。自動封鎖可能径はrelative値とabsolute値の小さい方 |
| `AutoFillPlanarityRelative` | number | `0.001`。Loop頂点から最小二乗平面への最大距離／Loop径 |
| `AutoFillPlanarityMeters` | number | `0.00001`。許容平面誤差はrelative値とabsolute値の大きい方 |
| `AutoFillMaxHoleCount` | integer | `16` |
| `RepresentativeBoundsExtentError` | number | `0.05`。各軸extentのSource比最大誤差 |
| `HardBoundsExtentError` | number | `0.25`。超過はGeometry Reject |
| `HardBoundsCenterShift` | number | `0.05`。中心移動／Source BoundsDiagonal。超過はGeometry Reject |
| `RepresentativeSurfaceDistanceP95` | number | `0.02`。Source BoundsDiagonalで正規化した双方向sampled距離P95 |
| `SurfaceSampleCountPerDirection` | integer | `4096`。Source hash由来seedの面積加重sample |
| `MaxConvexVerticesPerHull` | integer | `255` |
| `MaxConvexFacesPerHull` | integer | `255` |
| `MaxCompoundHullCount` | integer | `16` |
| `MaxCompoundTotalVertices` | integer | `2048` |
| `MaxSourceTriangleCount` | integer | `200000` |
| `MaxVariantTriangleCount` | integer | `200000` |
| `MaxConnectedComponentCount` | integer | `256` |
| `MaxEstimatedVoxelCellCount` | integer | `16777216` |
| `SoftTimeoutSeconds` | integer | `120`。資源状態判定専用で形状Gateに使わない |
| `RetryTimeoutSeconds` | integer | `300` |
| `MaxWorkingSetBytes` | integer | `4294967296` |
| `ResourceRetryCount` | integer | `1`。再試行は単一Blender Process、並列なし |

Render Gateは全頂点／属性が有限、Triangleが非退化、BoundsDiagonalがProfile範囲内、非ゼロextent軸が2以上、出力Triangle／連結成分が上限内であることを要求する。Triangle非退化の最終定義は、候補をZCG座標binary32へ量子化した後の`ZcgNumericKernelV1`による`twiceArea > epsArea`とする。Licensed RenderへSolid Gate、正体積、watertight、向き統一、全Mesh自己交差なしを要求しない。`BoundaryLoopFill`はProfileの径、平面誤差、個数をすべて満たし、次数2の単純閉Loopだけに適用する。`BlindNonManifoldFill`はこれらの事前Loop保証を持たないためHard形状偏差Gateを省略できず、通過結果も`BenchmarkOnly`とする。

Convex GateはHull数1..16、各Hullの頂点4..255、Face 4..255、全Hull頂点合計2048以下、各Hullの正の有限体積を要求する。上限を超える形状を暗黙に再簡略化せず、そのConvex VariantをGeometry Rejectする。

SourceとVariantのBounds extent誤差25%超または中心移動5%超はGeometry Rejectとする。Bounds extent相対誤差はSource extentがAsset epsilonを超える軸だけで求め、薄い／平面軸は絶対誤差がAsset epsilon以下かを検査する。Hard Gate内でも、Bounds extent誤差5%または双方向sampled表面距離P95 2%を超えたVariantは`BenchmarkOnly`とし、見た目代表値へ使わない。これにより「Bounds妥当」「主要Silhouetteが崩れる」を数値判定へ置き換える。

形状偏差のSource基準は正規化済みOriginal Renderとし、`BoundaryLoopFill`、`BlindNonManifoldFill`、Direct Decimate、Voxel Remesh、Voxel Post-Decimateを同じBounds／中心／sampled表面距離契約で比較する。Licensed Reportは`VolumeError`を持たず、閉じたように見えるFill／Voxel結果でも体積正本やSolid親として扱わない。ConvexBuildはRender親のQualityClassを継承するが、親Renderの体積や内部／外部を推論しない。

Source／Variant Triangle、連結成分、推定Voxel Cellの決定論的上限超過は`ProfileUnsupported`とする。一方、上限内の処理におけるwall-clock、Working Set、Tool crash等は形状不合格にせず`ResourceLimitExceeded`または`ToolFailed`とする。最初の資源超過後は同じ入力hash、Profile、Script、Presetを単一Process・並列なしで1回だけ再試行し、300秒または4 GiBを再度超えた場合は`ResourceDeferred`とする。最初の試行だけが上限へ達し、再試行が処理完了した場合の最終Statusは結果に応じて`Selected`、`BenchmarkOnly`、`GeometryRejected`、`ProfileUnsupported`、`NoOp`または`Alias`とする。各試行はEntry内の固定順`Attempts`へ独立保存し、初回がTimeout／MemoryLimit／ToolFailureのどれだったか、時間、Peak Working Set、Tool終了結果を失わない。`ResourceLimitExceeded`は再試行待ちの中間Statusであり、このStatusを含むReportからDataset Index／Receiptを確定してはならない。再試行完了後は決定表の完了Status、`ResourceDeferred`または`ToolFailed`へ必ず収束させる。Resource状態のVariantはLicensed Datasetへ入れず、後日の同一契約による再実行を許可する。処理時間とPeak Working Setは観測値として記録するが、Tier合否、Geometry hash、Dataset hashの入力には使用しない。

##### Canonical Selection Report／Licensed Dataset Index／Receipt

`EarlyFixtureSelectionProfile`、`EarlyFixtureSourceCatalog`、`CanonicalBundleIndex`、`EarlyFixtureSelectionReport`、`LicensedRepresentativeDatasetIndex`、`LicensedFixtureSelectionReceipt`は独立したSchema Version、canonical UTF-8 JSON Codec、content SHA-256を持つ。共通規則はBOMなし、余分な空白／末尾改行なし、固定property順、未知property禁止、nullable propertyも省略せずJSON `null`、hashは小文字64桁、浮動小数点は有限・負の0を0へ正規化した最短round-trip表現とする。

`EarlyFixtureSourceCatalog` v1はImport処理より前に作り、Source母集合、Phase 0.2対象範囲、匿名IDを固定する。root property順は`SchemaVersion`、`CatalogId`、`EligibilityRuleId`、`EntryCount`、`Entries`とし、SchemaVersionはinteger `1`、CatalogIdは`[A-Za-z0-9._-]{1,128}`、EligibilityRuleIdはstring enum `phase02-general-v1`／`phase02-polypro-boxlike-building-v1`、EntryCountは1..100000かつ配列長と一致する。Entryは`SourceFixtureId`のordinal順で、property順を`SourceFixtureId`、`AssetCategory`、`Phase02Eligibility`、`ScopeReason`、`SourceRelativePath`、`SourceFileSha256`とする。SourceFixtureIdはCatalog内で一意な`[A-Za-z0-9_-]{1,64}`の匿名ID、AssetCategoryはProfileの許可値、SourceRelativePathは後述のSource Bundle Indexに存在する正規化相対path、SourceFileSha256はそのfile bytesのSHA-256とする。

`Phase02Eligibility`はstring enum `EligibleGeneral`／`EligibleBoxLikeBuilding`／`ExcludedBuildingShape`とする。`phase02-general-v1`では全Entryを`EligibleGeneral／NotApplicable`へ固定する。`phase02-polypro-boxlike-building-v1`ではBuilding以外を`EligibleGeneral／NotApplicable`とし、Buildingは人間が処理前に10.2.1の形状範囲を判定して、対象なら`EligibleBoxLikeBuilding／BoxLikeHumanSelection`、対象外なら`ExcludedBuildingShape`と、`ComplexFootprint`／`TowerOrDome`／`CourtyardOrBridge`／`MultiBuildingOrStepped`／`OtherBuildingShape`のいずれかをScopeReasonへ記録する。別Ruleの値を混在させてはならない。Excluded EntryはBlender Variant、Attempt、Report Entryを生成せず、GeometryRejected／ToolFailed／Resource状態へ数えない。Catalog全Entry数、Eligible数、Excluded理由別件数を選抜集計の分母として併記し、処理成功率の分母はEligibleだけと明示する。人力判断を後から自動再現することは要求しないが、固定Catalog、EligibilityRuleId、Source Bundle内のCatalog bytesから同じ投入集合を完全再現できなければならない。これによりBlender起動／FBX Importに失敗してTriangle数を得られなくても、対象Source、Scope、カテゴリ、入力file hashを復元できる。

Source／Script／Preset bundleはarchive file自体やdirectory timestampをhashせず、展開済みtreeから作るcanonical `CanonicalBundleIndex` v1で識別する。root property順は`SchemaVersion`、`BundleKind`、`EntryCount`、`Entries`、SchemaVersionはinteger `1`、BundleKindは`Source`／`Script`／`Preset`、EntryCountは1..100000かつ配列長と一致する。各Entryのproperty順は`RelativePath`、`ByteLength`、`ContentSha256`とし、RelativePathのUTF-8 byte列によるordinal昇順、ByteLengthは0..2147483647、ContentSha256はfile bytesの小文字64桁SHA-256とする。

RelativePathはbundle rootからの相対pathをUnicode NFCへ正規化し、separatorを`/`へ統一する。空path、先頭`/`、drive／UNC prefix、末尾`/`、空segment、`.`／`..` segment、NUL／control文字、backslashをRejectし、正規化後の完全一致とUnicode simple case-fold後の衝突をともにRejectする。通常fileだけを列挙し、symlink、junction、reparse point、device、socket等はRejectする。空directory、directory名、timestamp、ACL、所有者、archive圧縮方式はIndexへ含めない。CanonicalBundleIndex artifact自体はindexed rootの外へ出力し、自己参照Entryへ含めない。file bytesは変換せずそのままhashし、Indexのcanonical bytesのSHA-256をBundle Content SHA-256とする。同じ展開file集合ならZIP等のcontainer bytesや展開時刻が違っても同じbundle hashになる。

Source Bundleにはcanonical Source Catalog bytesを予約path`metadata/early_fixture_source_catalog.v1.json`の通常Entryとして必ず含め、Catalogが参照する全SourceRelativePathとSourceFileSha256をBundle Indexへ1対1照合する。Catalog外の補助fileをSource Bundleへ含めてもよいが、選抜対象SourceはCatalog Entryだけとする。`SourcePackageContentSha256`、`ScriptBundleContentSha256`、`PresetBundleContentSha256`は、それぞれBundleKindが一致するCanonicalBundleIndex bytesのSHA-256であり、Report／Index Codecは参照Indexを再hashして一致を検証する。これによりSourceFixtureIdとAssetCategoryの対応、Script、Presetの算出対象がすべてhashへ閉じる。

`CanonicalBundleVerifier`は既存Bundle Indexと明示された対応rootを受け取り、Index生成時と同一規則でrootを再帰列挙する。symlink／junction／reparse point等をRejectし、正規化した通常file path集合がIndex EntryのRelativePath集合と完全一致することを要求する。欠落file、Indexにない余分な通常file、path重複／case-fold衝突をRejectし、各fileの実byte長とraw bytes SHA-256をByteLength／ContentSha256へ照合する。Index artifact自体はroot外にあることを要求し、探索順、mtime、archive bytes、キャッシュ済みhashだけで検証を省略しない。

Phase 0.2 HarnessはBlenderを起動する前にSource／Script／Presetの3 rootをそれぞれVerifierへ通し、その時点の3 Bundle Index content hashをSelection Runへ固定する。Report／Dataset Index生成後、Receiptを確定する直前に同じ3 rootと同じIndex bytesでもう一度完全照合し、file集合、長さ、内容またはIndex hashが開始時から変化していればRun全体をRejectしてReceiptを作らない。Report／Index CodecによるIndex bytesの再hashはこの実tree照合の代替ではなく、Receipt確定済みRunの再利用時も、対応rootが提供される処理ではVerifier合格を必須とする。

Report v1のproperty順は`SchemaVersion`、`SelectionRunId`、`ProfileContentSha256`、`SourcePackageContentSha256`、`BlenderVersion`、`BlenderExecutableSha256`、`ScriptBundleContentSha256`、`PresetBundleContentSha256`、`HostProfileId`、`DatasetIndexContentSha256`、`EntryCount`、`Entries`とする。`SelectionRunId`は小文字UUID、各version／ID stringはTrim済み1..128文字、`DatasetIndexContentSha256`はDatasetを確定できた場合だけhash、それ以外は`null`とする。Entriesは`SourceFixtureId + Tier + GeometryProcessMode + VariantId`のordinal順で並べ、EntryCountは0..100000かつ配列長と一致する。

ReportはSource Catalogで`EligibleGeneral`または`EligibleBoxLikeBuilding`とされた全SourceFixtureIdを少なくとも1 Entryで被覆する。`ExcludedBuildingShape`はSource Catalogとそこから導出するScope集計だけに存在し、Report Entry、Variant、Attempt、Geometry Rejectを生成しない。Eligible SourceについてBlender Processを開始できない場合は`Launch`、Process開始後に固定Script／Presetの初期化、version検証、引数検証へ失敗してImportへ到達しない場合は`Bootstrap`、Source fileの読込／FBX解析失敗は`Import`として区別する。これらによりVariant展開へ到達しなかったEligible Sourceには、`Tier=Render`、`GeometryProcessMode=Original`、`VariantId=original`の決定的な失敗Entryを1件作り、Status／Attemptへ実際のStageとToolまたはResource失敗を記録する。開始したVariant試行は成功・失敗を問わずそれぞれ固有Entryを持たせ、後続失敗をSource Catalogや成功Entryだけで代用しない。CatalogにないSourceFixtureIdをReportへ追加することは禁止する。

各Report Entryのproperty順と型は次に固定する。

| Entry property | JSON型 | 契約 |
| --- | --- | --- |
| `SourceFixtureId` | string | Catalogと同じ匿名化した`[A-Za-z0-9_-]{1,64}` |
| `SourceGeometrySha256` | string | Source CatalogのSourceFileSha256と一致する小文字64桁。Import失敗時もSource file bytesから取得可能 |
| `AssetCategory` | string enum | `Furniture`／`Vehicle`／`Building`／`RoadEquipment`／`SmallProp`／`Character`／`Other`。Source内で不変 |
| `SourceTriangleBand` | string enum／null | `UpTo100`／`From101To500`／`From501To1000`／`From1001To2000`／`From2001To5000`／`From5001To10000`／`Over10000`。Triangle数取得前のLaunch／Bootstrap／Import失敗時だけ`null` |
| `Tier` | string enum | `Render`／`Convex`。`Solid`および未知値は禁止 |
| `GeometryProcessMode` | string enum | `Original`／`BoundaryLoopFill`／`BlindNonManifoldFill`／`DirectDecimate`／`VoxelRemesh`／`VoxelPostDecimate`／`ConvexBuild` |
| `VariantId` | string | 同じSourceFixtureId＋Tier内で一意な`[A-Za-z0-9._-]{1,48}` |
| `ParentTier` | string enum／null | Renderからの`ConvexBuild`では`Render`を必須。直接生成／ImportしたConvexとそれ以外は`null` |
| `ParentVariantId` | string／null | `ConvexBuild`では同じSourceFixtureId＋ParentTier内のSelected／BenchmarkOnly親VariantIdを必須。それ以外は`null` |
| `ParentDatasetCaseId` | string／null | `ConvexBuild`では親Entryの非null DatasetCaseIdと厳密一致。それ以外は`null` |
| `ParentGeometrySha256` | string／null | `ConvexBuild`では親EntryのOutputGeometrySha256と一致する小文字64桁。それ以外は`null` |
| `DatasetCaseId` | string／null | `Selected`／`BenchmarkOnly`だけ必須。`SourceFixtureId.TierToken.VariantId`と厳密一致。それ以外は`null` |
| `Status` | string enum | `Selected`／`BenchmarkOnly`／`GeometryRejected`／`ProfileUnsupported`／`NoOp`／`Alias`／`ResourceLimitExceeded`／`ResourceDeferred`／`ToolFailed` |
| `CanonicalVariantId` | string／null | `NoOp`／`Alias`では同じSourceFixtureId＋Tier内にある既存Selected／BenchmarkOnly VariantIdを必須。それ以外は`null` |
| `OutputGeometrySha256` | string／null | Geometry生成成功時は小文字64桁。それ以外は`null` |
| `SourceTriangleCount` | integer／null | 取得済みなら`1..MaxSourceTriangleCount`、上限超過記録は`1..2147483647`。事前解析不能なLaunch／Bootstrap／Import失敗時だけ`null` |
| `ReductionTargetTriangleCount` | integer／null | Direct Target時は1以上。それ以外は`null` |
| `ActualOutputTriangleCount` | integer／null | Geometry生成成功時は1以上。それ以外は`null` |
| `ConnectedComponentCount` | integer／null | ZCG後検査完了時は1以上。それ以前の失敗は`null` |
| `ReductionRatio` | number／null | Actual／Source。Geometry生成成功時だけ有限・正 |
| `ReductionApplied` | boolean | Decimateを実行したか |
| `VoxelResolutionCells` | integer／null | Voxel64／128／256。それ以外は`null` |
| `VoxelSize` | number／null | Voxel時だけ正のmeter値 |
| `PostReductionTargetTriangleCount` | integer／null | Voxel Post-Decimate時だけ1以上 |
| `BoundsExtentError` | number／null | Source比最大誤差 |
| `BoundsCenterShift` | number／null | Source diagonal比 |
| `SurfaceDistanceP95` | number／null | Licensed RenderのSource diagonal比 |
| `BoundaryEdgeCount` | integer／null | 検査完了時は0以上 |
| `NonManifoldEdgeCount` | integer／null | 検査完了時は0以上 |
| `OrientationMismatchEdgeCount` | integer／null | 検査完了時は0以上 |
| `ConvexHullCount` | integer／null | Convex時だけ0以上 |
| `ConvexTotalVertexCount` | integer／null | Convex時だけ0以上 |
| `AttemptCount` | integer | `1..2` |
| `Attempts` | object array | `AttemptCount`件。AttemptOrdinal昇順、最大2件。下記固定schema |
| `RejectStage` | string enum | `None`／`Launch`／`Bootstrap`／`Import`／`Normalize`／`BoundaryFill`／`ProfileGuard`／`RenderGate`／`ConvexGate`／`VoxelRemesh`／`Decimate`／`CanonicalGeometry`／`ResourceGuard`／`Export` |
| `RejectReason` | string enum | `None`／`NonFinite`／`DegenerateBounds`／`DegenerateTriangle`／`Boundary`／`NonManifold`／`Orientation`／`BoundsDeviation`／`ConvexLimit`／`InputLimit`／`OutputLimit`／`VoxelCellLimit`／`Timeout`／`MemoryLimit`／`ToolFailure` |

各Attemptのproperty順は`AttemptOrdinal`、`AttemptStatus`、`ProcessMilliseconds`、`PeakWorkingSetBytes`、`ToolExitCode`、`RejectStage`、`RejectReason`とする。`AttemptOrdinal`は1始まりの連番、`AttemptStatus`は`Succeeded`／`ResourceLimitExceeded`／`ToolFailed`、時間は有限の0以上、Peakは0以上のinteger、`ToolExitCode`はProcessが終了codeを返した場合だけsigned 32-bit integer、それ以外は`null`とする。`Succeeded`ではAttemptのReject Stage／Reasonを`None`、資源超過またはTool失敗では該当Stage／Reasonを必須とする。Entryの最終Reject Stage／Reasonは最終分類結果、Attempts内は各実行結果を表し、相互に上書きしない。`AttemptCount == Attempts.length`を要求し、2件目は1件目が`ResourceLimitExceeded`の場合だけ許可する。初回超過後に成功したEntryは`AttemptCount=2`、Attemptsが`ResourceLimitExceeded`、`Succeeded`の順となる。

最終Entry StatusとAttempt列の許可組合せは次の完全決定表に固定し、表にない組合せをCodecでRejectする。角括弧内はAttemptStatusの順序である。

| 最終Entry Status | 許可Attempt列 | Entry最終Reject Stage／Reason |
| --- | --- | --- |
| `Selected`／`BenchmarkOnly`／`NoOp`／`Alias` | `[Succeeded]`または`[ResourceLimitExceeded, Succeeded]` | `None／None` |
| `GeometryRejected` | `[Succeeded]`または`[ResourceLimitExceeded, Succeeded]` | Geometryを棄却した実Stageと、`NonFinite`から`ConvexLimit`までの該当Geometry Reason。資源／Tool Reasonは禁止 |
| `ProfileUnsupported` | `[Succeeded]`または`[ResourceLimitExceeded, Succeeded]` | `ProfileGuard`と`InputLimit`／`OutputLimit`／`VoxelCellLimit`のいずれか |
| `ResourceLimitExceeded` | `[ResourceLimitExceeded]`だけ | `ResourceGuard`と`Timeout`または`MemoryLimit`。再試行待ちの中間Reportだけで許可 |
| `ResourceDeferred` | `[ResourceLimitExceeded, ResourceLimitExceeded]`だけ | `ResourceGuard`と2件目の`Timeout`または`MemoryLimit` |
| `ToolFailed` | `[ToolFailed]`または`[ResourceLimitExceeded, ToolFailed]` | Tool失敗が発生した実Stageと`ToolFailure` |

Attempt単位の`ResourceLimitExceeded`は超過が実際に発生した`Launch`／`Bootstrap`／`Import`／`Normalize`／`BoundaryFill`／`RenderGate`／`ConvexGate`／`VoxelRemesh`／`Decimate`／`CanonicalGeometry`／`Export`のいずれかと、Reason `Timeout`／`MemoryLimit`を持つ。Attempt単位の`ToolFailed`も失敗が起きた実StageとReason `ToolFailure`、`Succeeded`は`None／None`だけを許可する。Entry全体のResource Statusだけは最終Stageを`ResourceGuard`へ集約し、最終Reasonを末尾Attemptと一致させる。2件目は常に最終Attemptであり、ToolFailed後のretry、Succeeded後のretry、3件目を禁止する。最終Statusが`ResourceLimitExceeded`の未完了Reportでは`DatasetIndexContentSha256=null`とし、Dataset Index／Receiptを確定してはならない。これにより`Selected + ToolFailed`、1 Attemptの`ResourceDeferred`、`GeometryRejected + ResourceLimitExceeded`等を表現不能にする。

同じ`SourceFixtureId`を持つ全EntryはSource Catalogと同一の`SourceGeometrySha256`／`AssetCategory`を持ち、そのカテゴリがProfileの`AssetCategories`に含まれることを要求する。`SourceTriangleCount`が非nullなら`SourceTriangleBand`も必須で、Profileの`SourceTriangleBandUpperBounds`からCodecが再計算して一致を検証する。両方の`null`は、全Attemptが`Launch`／`Bootstrap`／`Import`のいずれかでGeometry取得前に失敗し、最終Statusが`ToolFailed`、`ResourceLimitExceeded`または`ResourceDeferred`の場合だけ許可する。この場合、Triangle依存の形状統計とReductionRatioも`null`にする。片方だけの`null`、Geometry取得後の`null`、不明値を0として保存することは禁止する。カテゴリはSource Catalogで固定し、処理成否やVariant結果から後付け変更しない。

Report Entryを持てるSourceFixtureIdはCatalogで`EligibleGeneral`または`EligibleBoxLikeBuilding`のEntryだけとする。Catalogの全Eligible Sourceは、Import前失敗を含め少なくとも1件のReport Entryから参照されなければならず、`ExcludedBuildingShape`を参照するEntry、CatalogにないSource、Eligible Sourceの完全欠落をReport CodecがRejectする。Scope集計はCatalogから導出し、Excluded Source用の擬似Tier、空Attempt、GeometryRejected Entryを作らない。

`VariantId`の一意keyは`SourceFixtureId + Tier + VariantId`とし、Render／Convex間では同じVariantIdを許可する。Selected／BenchmarkOnlyの`DatasetCaseId`は上記Tier付き構築式と厳密一致し、Index全体で重複してはならない。`CanonicalVariantId`によるNoOp／Alias参照も同じSourceFixtureId＋Tier内だけに限定し、TierをまたぐAlias化や参照を禁止する。

`GeometryProcessMode == ConvexBuild`では4つのParent propertyをすべて必須とし、`ParentTier=Render`へ固定する。同じReport内の同一SourceFixtureId、ParentVariantIdに一致するRender親Entryが`Selected`または`BenchmarkOnly`で存在し、ParentDatasetCaseId／ParentGeometrySha256が親の値と一致することをCodecが検証する。親が`BenchmarkOnly`なら子ConvexのStatusは成功時も`BenchmarkOnly`だけを許可し、`Selected`をRejectする。親の`GeometryProcessMode == BlindNonManifoldFill`でも同じ伝播規則により子Convexを`BenchmarkOnly`へ固定する。Convex親、Solid親、別Source親、欠落親、hash不一致をRejectする。直接生成またはImportしたConvexは`Tier=Convex／GeometryProcessMode=Original`として親propertyをすべて`null`にし、ConvexBuild以外でも4 propertyをすべて`null`とする。

`NoOp`と`Alias`は新しいDatasetCaseを作らず、`DatasetCaseId=null`とし、`CanonicalVariantId`で既存のcanonical Variantへ対応させる。参照先がSelected／BenchmarkOnlyとして存在しない場合はNoOp／Aliasにせず、基底と同じ失敗Status／Reasonを記録する。Geometry Reject、ProfileUnsupported、Resource状態、ToolFailedもDataset Indexへ入れない。

`LicensedRepresentativeDatasetIndex` v1のproperty順は`SchemaVersion`、`DatasetId`、`ProfileContentSha256`、`SourcePackageContentSha256`、`BlenderVersion`、`BlenderExecutableSha256`、`ScriptBundleContentSha256`、`PresetBundleContentSha256`、`VariantCount`、`Variants`とする。SchemaVersionはinteger `1`、DatasetIdは`[A-Za-z0-9._-]{1,128}`、VariantsはDatasetCaseIdのordinal順で並べ、DatasetCaseIdはIndex内で一意、VariantCountは1..100000かつ配列長と一致する。各Variantのproperty順は`DatasetCaseId`、`SourceFixtureId`、`Tier`、`GeometryProcessMode`、`VariantId`、`ParentTier`、`ParentVariantId`、`ParentDatasetCaseId`、`ParentGeometrySha256`、`QualityClass`、`GeometryFormat`、`GeometryFormatVersion`、`GeometryRelativePath`、`GeometryByteLength`、`GeometryContentSha256`、`SourceGeometrySha256`、`SourceTriangleCount`、`ActualInputTriangleCount`、`ReductionTargetTriangleCount`、`VoxelResolutionCells`、`PostReductionTargetTriangleCount`とする。Tierは`Render`／`Convex`だけ、GeometryFormatはstring enum `ZantetsuCanonicalGeometry`、GeometryFormatVersionはinteger `1`、QualityClassは`Representative`／`BenchmarkOnly`、GeometryByteLengthは16..67108864、Triangle／Voxel countsは0以上のinteger、nullableなTarget／Voxel propertyは非該当時に明示`null`とする。ConvexBuild VariantではParent property 4件を必須、ParentTierを`Render`へ固定し、同じIndex内の親Render VariantへDatasetCaseId／SourceFixtureId／VariantId／GeometryContentSha256を完全照合する。親QualityClassが`BenchmarkOnly`なら子も`BenchmarkOnly`を必須とする。直接生成ConvexおよびConvexBuild以外のVariantではParent propertyをすべて`null`とし、Index LoaderはReportと同じ由来伝播規則を独立に再検証する。Solid Tier、Solid親、Synthetic Fixture参照はすべてRejectする。

##### Synthetic Watertight Dataset専用Validator

閉形状既知正解用の`SyntheticWatertightFixtureProfile`、`SyntheticWatertightDatasetIndex`、`SyntheticFixtureValidationResult`はLicensed schemaと別version／別content hashを持ち、本節のcanonical UTF-8 JSON共通規則を再利用する。Profile v1のproperty順は`SchemaVersion`、`ProfileId`、`AbsoluteEpsilonMeters`、`RelativeEpsilon`、`MaxTriangleCount`、`MaxConnectedComponentCount`、`MaxSelfIntersectionCount`、`SelfIntersectionAlgorithm`、`MaxCandidatePairCount`とし、値をそれぞれinteger `1`、`synthetic-watertight-v1`、`0.000001`、`0.000001`、`200000`、`256`、`0`、`ClosedTriangleDistanceV1`、`2000000`へ固定する。

Dataset Index v1のproperty順は`SchemaVersion`、`DatasetId`、`ProfileContentSha256`、`ScriptBundleContentSha256`、`CaseCount`、`Cases`とする。各Caseは`DatasetCaseId`、`GeneratorId`、`GeneratorRecipeContentSha256`、`GeometryRelativePath`、`GeometryByteLength`、`GeometryContentSha256`、`ValidationResultContentSha256`の順とし、DatasetCaseId ordinal順、1..100000件、一意な正規化`.zcg` path、16..67108864 byte、各小文字64桁hashを要求する。Generator RecipeはGenerator引数を含むcanonical JSON fileとしてScript Bundleへ収録し、そのbytesをhashする。Validation Result v1は`SchemaVersion`、`DatasetCaseId`、`ProfileContentSha256`、`GeometryContentSha256`、`TriangleCount`、`ConnectedComponentCount`、`BoundaryEdgeCount`、`NonManifoldEdgeCount`、`OrientationMismatchEdgeCount`、`SelfIntersectionCandidatePairCount`、`SelfIntersectionCount`、`TotalSignedVolume`、`Passed`、`FailureReason`の順とする。`TriangleCount`は非負integerとし、後続Count／Volumeは当該Gateへ到達前に失敗した場合だけ`null`、到達した場合は非負integer／finite numberとする。Passedでは全統計を非null、TotalSignedVolumeを正のfinite値とする。FailureReasonは`None`／`NonFinite`／`Degenerate`／`Boundary`／`NonManifold`／`Orientation`／`NonPositiveVolume`／`SelfIntersection`／`CandidatePairLimit`／`InputLimit`／`ValidatorUnavailable`とし、Passedなら`None`、不合格なら非Noneを必須とする。

Profile／Validation Resultは64 KiB、Dataset Indexは100000 Case／64 MiBをschema上限とし、Loaderへ同値以下の呼出側byte／件数上限と配列確保前検査を必須とする。Indexへ入れるCaseはPassedだけとし、不合格ResultはGenerator Runの診断Bundleへ保持する。Licensed SourceFixtureId、Licensed Tier、GeometryProcessMode、Parent property、QualityClass、VolumeError、Selection Status／Reject Stage／Reasonを使用しない。

以下の`SolidSignedVolumeV1`、`SolidGeometryValidatorV1`、`SolidCandidateBvhV1`、`ClosedTriangleDistanceV1`という互換名はSynthetic Watertight Datasetのテスト用Validatorだけを指す。製品Strict Solid、Licensed Solid Tier、製品Preprocessor成果物を意味せず、Licensed Harnessから呼び出さない。Synthetic側でProfile上限または形状Gateに失敗したcaseは`SyntheticFixtureValidationResult`を不合格としてDataset Indexへ採用せず、Licensed Reportの`GeometryRejected`／`ProfileUnsupported`へ変換しない。

##### ZantetsuCanonicalGeometry v1

Phase 0.2のBenchmark GeometryはFBX、OBJ、glTF、Blender file等を直接保存せず、決定的なbinary `ZantetsuCanonicalGeometry`（ZCG）v1へ変換する。v1は形状切断／Cook Benchmarkに必要な位置、面Topology、Convex Hull境界だけを正本とし、object名、material名、UV、Normal、色、Animation、custom property、timestamp、exporter metadataを含めない。表示確認時の法線と単色MaterialはDecoder側で再構築し、製品用Asset表現とは分離する。

全integerはunsigned little-endian、浮動小数点はIEEE 754 binary32 little-endianとする。BlenderからZCGへの座標変換は次の順序と式へ固定する。列vectorを使用し、評価済みObjectのlocal頂点を`p_local`、Object world行列を`M_object`、そのSource Fixture用にImport時に作る合成Asset Rootのworld行列を`M_root`、Blender sceneのmeter／Blender Unitを表す正の有限値を`s = scene.unit_settings.scale_length`とする。まずbinary64で`p_b = inverse(M_root) * M_object * [p_local.x,p_local.y,p_local.z,1]`を評価して全Object transformをasset-local Blender右手系へBakeし、次にtranslationを含む全成分へ単位scaleを適用して`p_m = s * p_b.xyz`、最後に固定基底変換`p_zcg = C * p_m`を行う。

```text
C = | 1 0 0 |
    | 0 0 1 |
    | 0 1 0 |

(x_zcg, y_zcg, z_zcg) = (s * x_b, s * z_b, s * y_b)
```

したがってZCGはlocal meter、Y-up、`+Z` forwardの左手系となる。単位scaleをObject／Root行列より前へ適用したり、translationだけを未scaleにしたり、別軸の符号を反転してはならない。`M_root`／`M_object`の成分と行列積は取得順binary64、各dot積は左からの加算、FMA無効で評価する。Asset Root逆行列が特異、scaleが非正／非有限、変換後座標が非有限ならRejectする。

Blender評価Meshのface loopは、`inverse(M_root) * M_object`の線形成分が負determinantならObject transform Bake時に1回だけ反転し、Bake後のBlender右手系で評価時のfront-facingを保つ。Synthetic Watertight Fixture／Convexはその後に外向きCCWへOrientation Gateで統一し、開放Licensed Renderは評価時の向きを保つ。`C`のdeterminantは`-1`なので、Blender右手系のCCW loopはindex順を追加反転せずZCG左手系の外向きclockwise loopになる。TriangulationはTransform Bake、負determinant補正、Synthetic Watertight／Convex向き統一の後、`C`適用前に行う。

変換後floatはround-to-nearest-ties-to-evenでbinary32化し、NaN／InfinityをReject、負の0を正の0へ正規化する。Headerは4 byte ASCII magic `ZCG1`、1 byte `GeometryKind`（`1=TriangleMesh`、`2=ConvexSet`）、3 byte zero reserved、8 byte unsigned payload lengthの計16 byteとし、宣言長はfile長から16を引いた値と厳密一致させる。可変padding、末尾data、未知Kind、非zero reservedをRejectする。

ZCGの全幾何判定は、格納対象の正規化済みbinary32 positionをbinary64へ正確に拡張した値だけを正本とする共通`ZcgNumericKernelV1`を使う。Blender側の元double座標、Normal、既存Plane、Unity側float計算を判定へ混ぜない。演算はIEEE 754 binary64 round-to-nearest-ties-to-even、FMA／fast-math無効、積と差を式の記載順、dotと総和を左畳みで行う。`dot(a,b) = ((a.x*b.x + a.y*b.y) + a.z*b.z)`、`crossRH(a,b) = (a.y*b.z-a.z*b.y, a.z*b.x-a.x*b.z, a.x*b.y-a.y*b.x)`、`length(c) = sqrt(((c.x*c.x + c.y*c.y) + c.z*c.z))`へ固定し、sqrtはIEEE 754 correctly-rounded binary64を使用する。

検証対象domainのbinary32 positionから各軸min／maxをpositionのcanonical順に比較して求め、軸差を`dx,dy,dz`とする。`D = sqrt(((dx*dx + dy*dy) + dz*dz))`、`epsDistance = max(Profile.AbsoluteEpsilonMeters, D * Profile.RelativeEpsilon)`、`epsArea = epsDistance * epsDistance`、`epsVolume = epsArea * epsDistance`とする。Dが非正／非有限ならRejectする。距離／半空間誤差はepsilon以下を包含側とする一方、非退化面積と正体積はそれぞれ`> epsArea`、`> epsVolume`を必須とし、等号は退化側としてRejectする。

`TriangleMesh` payloadは`uint32 PositionCount`、`uint32 TriangleCount`、続いてPositionCount件の`float32 x,y,z`、TriangleCount件の`uint32 i0,i1,i2`とする。元Geometryを位置だけのtriangle soupへ展開し、完全に同じ正規化positionを1件へweldして、positionを数値`x,y,z`のlexicographic昇順へ並べ直す。各Triangleは新indexへremapし、windingを反転せず3 indexをcyclic rotationして辞書順最小表現にし、Triangle列全体を`i0,i1,i2`の辞書順へsortする。範囲外index、同一頂点を含むTriangle、同一index tripleの重複をRejectする。Licensed Render TierとSynthetic Watertight TierはこのKindを使う。

Triangle退化判定のdomainはTriangleMesh全体とし、上記domain Boundsからepsilonを1回だけ計算する。各Triangleについて`u=v1-v0`、`w=v2-v0`、`twiceArea = length(crossRH(u,w))`を記載順binary64で計算し、`twiceArea > epsArea`だけを合格とする。`twiceArea == epsArea`とそれ未満はRejectし、binary32 positionの1 ULP差で境界をまたぐ場合もこの比較結果をそのまま使用する。実面積へ0.5を掛けてから比較したり、TriangleごとのBounds、Blender double、Unity float、近似Normal長を使ってはならない。

座標変換のGolden Fixtureは`M_root=identity`、`M_object=translation(10,20,30)`、`s=0.5`、Blender local triangle `[(1,2,3),(4,6,5),(-2,7,11)]`を入力とする。ZCG変換、position sort、triangle cyclic rotation後はpositions `[(4,20.5,13.5),(5.5,16.5,11),(7,17.5,13)]`、triangle `[0,1,2]`、payload length 56、file length 72でなければならない。完成fileのhexは`5a4347310100000038000000000000000300000001000000000080400000a441000058410000b04000008441000030410000e04000008c4100005041000000000100000002000000`、SHA-256は`5210748ea4fe7a8f349b52e919af7dd1aad4c542a91fb741806bf517f2426cdbf`へ固定する。

`ConvexSet` payloadは`uint32 HullCount`の後にHull recordを連結する。各Hull recordは`uint32 PositionCount`、`uint32 FaceCount`、position列、各Faceの`uint32 IndexCount`とindex列からなる。Hull内positionはTriangleMeshと同じ規則でweld／sort／remapする。Face loopは外向きwindingを維持したままcyclic rotationで辞書順最小化し、Face列をIndexCountとindex列の辞書順へsortする。各Hullを一時canonical bytesへserializeし、そのbytesのunsigned byte lexicographic昇順でHull recordをsortする。Convex TierはこのKindを使う。

Convexの検証domainはHullごととし、各Hullのbinary32 position Boundsから`ZcgNumericKernelV1`で`epsDistance`／`epsArea`／`epsVolume`を独立に計算する。比較境界と演算精度は共通Kernelから変更しない。

各Faceはcanonical rotation後の`v0`を固定し、`i=1..IndexCount-2`の順に`c = -crossRH(v[i]-v0, v[i+1]-v0)`を計算して、`length(c) > epsArea`となる最初のtripletをPlane生成へ使う。存在しなければFaceを退化としてRejectする。`n = c / length(c)`、`d = -(((n.x*v0.x + n.y*v0.y) + n.z*v0.z))`とし、このPlaneをそのpolygon faceの唯一の解釈とする。Face全頂点で`abs((((n.x*v.x + n.y*v.y) + n.z*v.z) + d)) <= epsDistance`を要求し、非平面polygonをepsilon内だけ許可する。Hull全頂点について同じ値が`<= epsDistance`であることを要求し、1点でも正側へ超過したHullを非凸または内向きFaceとしてRejectする。

Topologyは各FaceのIndexCount 3以上、範囲内でFace内重複indexなし、重複Faceなしを要求し、各undirected edgeがちょうど2 Faceに現れてdirected向きが互いに逆であることを閉鎖条件とする。Hull bounds centerを`r`とし、canonical Face順と各Faceのfan順で`V = left_sum(-dot(v0-r, crossRH(v[i]-r, v[i+1]-r)) / 6)`を計算する。ZCGのclockwise外向き規約では`V > epsVolume`を必須とし、`V <= epsVolume`、負volume、非有限volumeをRejectする。Face半空間、閉鎖edge、正volumeの全条件を通ったものだけをConvexとして扱う。3未満のFace index、ProfileのHull／Vertex／Face上限超過もRejectする。

ZCG Encoderは同じ正規化Geometryから常に同じbytesを生成し、`GeometryContentSha256`は完成ZCG file bytes全体のSHA-256とする。Alias判定も同じSourceFixtureId＋Tier内のこのhashで行う。Verifier／Benchmark LoaderはIndexのFormat／VersionでDecoderを選び、decode後に同じEncoderで再serializeしたbytesが入力fileとbyte-for-byte一致しなければnon-canonicalとしてRejectする。これにより元Triangle／Vertex／Hullの列挙順、FBX metadata、container timestampはGeometry hashへ影響せず、位置、winding、Topologyの変化だけがcanonical bytesへ反映される。

全VariantはZCG encode後にfileをDecoderで読み直し、decodeされたbinary32 positionとcanonical indexだけを入力として最終Gateを再実行する。Licensed Render Tierはfinite、Bounds、Triangle退化／重複、Early Licensed ProfileのTriangle／Component上限だけを再検証する。別DatasetのSynthetic Watertight FixtureはSynthetic Profileを使い、それらに加えてundirected edge key `(min(i0,i1), max(i0,i1))`をcanonical Triangle順で構築し、出現1回をBoundary、3回以上をNon-Manifold、2回でもdirected向きが逆でないものをOrientation不整合として数え、すべて0を要求する。binary32 weld後のTriangle edge adjacencyから連結成分を再構築し、成分はその成分が含む最小canonical Triangle indexの昇順、成分内Triangleはglobal canonical Triangle順を保つ。

Synthetic Watertight Fixtureのsigned volumeは成分ごとに次の`SolidSignedVolumeV1`だけで計算する。成分で参照されるpositionをcanonical position index順に走査してbinary64のcomponent Bounds `min`／`max`を求め、参照点を各軸について`r = min + (max - min) * 0.5`の順で計算する。Triangle `(v0,v1,v2)`ごとに`a=v0-r`、`b=v1-r`、`c=v2-r`、`q=crossRH(b,c)`、`numerator=-dot(a,q)`、`term=numerator/6.0`をこの順にbinary64で評価する。`V0=+0.0`から成分内canonical Triangle順に`Vk+1=Vk+termk`を左畳みし、除算後のtermだけを加算する。式の再結合、原点基準への置換、pairwise／Kahan加算、FMA、除算の後回しは禁止する。成分Boundsから共通Numeric Kernelで算出した`epsVolume`に対し、有限な`V > epsVolume`だけを合格とし、`V == epsVolume`を含む`V <= epsVolume`、負値、非有限値をRejectする。Synthetic Validation Result用の全体Volumeは成分順に各合格`V`を同じbinary64左畳みで加算し、途中または最終値が非有限ならRejectする。

`SolidGeometryValidatorV1`はSynthetic Watertight ZCG bytesを入力とするversion固定の共有Validatorを唯一の正本とし、Synthetic Profileの`SelfIntersectionAlgorithm`は`ClosedTriangleDistanceV1`だけを許可する。Synthetic Fixture Generator／Blender HarnessはPython独自predicateを実装せず、ZCG encode後にSynthetic Script Bundleへhash固定された共有Validatorを呼び出す。Unity Editor側のSynthetic Dataset検証とT-081も同じValidator artifactを使用する。実装artifact、CLI引数、終了codeはSynthetic Script Bundle hashの対象とし、利用不能・version不一致・未知algorithmをSynthetic Validation失敗として扱い、別ライブラリへFallbackしない。Licensed Harness、Licensed Report、製品Preprocessorからは呼び出さず、生成前Gateの結果や元Blender doubleで再判定しない。

`ClosedTriangleDistanceV1`はbinary32から正確にbinary64へ展開した2つの閉Triangle間の最小二乗距離を決定論的に求める。候補は、Aの3頂点から閉Triangle Bへのpoint-triangle二乗距離、Bの3頂点から閉Triangle Aへの同距離、Aの3 closed edgeから閉Triangle Bへのsegment-triangle二乗距離、Bの3 closed edgeから閉Triangle Aへの同距離、AとBの各3 edgeによる9組のclosed-segment間二乗距離の順とし、各群内はlocal vertex／edge番号の辞書順で評価する。segment-triangleはsegmentとTriangle planeの交点parameterが閉区間`[0,1]`にある場合、固定式で`u`、`v`、`w=1-u-v`の順にbinary64 barycentricを計算し、`u >= 0 && v >= 0 && w >= 0`ならface interior／boundary貫通として距離0のwitnessを返す。等号は包含し、比較不能／非有限ならこの0距離分岐を採用せず後続の保守的距離候補へ進む。非平行時のplane交点、平行／coplanar時の3 edgeとのsegment-segment、両endpointのpoint-triangle候補を固定順に評価するため、「一方のedgeが他方のface内部を貫通するが頂点もedge同士も接触しない」proper crossingも検出する。

point-triangle、segment-triangle、segment-segmentはversion固定のEricson型region testを、`ZcgNumericKernelV1`のbinary64演算順、`dot`、`crossRH`、除算、clampへ逐語的に固定した共有実装とする。各候補は二乗距離だけでなく両Triangle上のclosest witness `(pA,pB)`と各Triangleのbarycentricを返す。barycentric値およびsegment parameterの`0`と`1`は閉区間へ含め、clampは`x < 0 ? 0 : (x > 1 ? 1 : x)`、候補minimumはstrict `<`の場合だけ更新して同値なら先の候補を保持する。退化Triangleは先行Triangle Gateで、zero-length edgeまたは非有限な分母はTopology／退化Rejectで到達不能とし、predicate内で別形状へ降格しない。`epsDistanceSquared=epsDistance*epsDistance`もbinary64でこの順に一度だけ計算する。

自己交差候補は全`TriangleCount choose 2`を走査せず、version固定の`SolidCandidateBvhV1`で生成する。各Triangleのbinary64 AABBを各軸の正負へ`epsDistance`だけ拡張し、非有限化またはdomain Boundsを越える算術overflowをRejectする。primitive初期順はcanonical Triangle index順とし、各nodeでTriangle centroid Boundsのextentが最大の軸をsplit axisに選ぶ。同値はX、Y、Z順、軸上のstable sort keyは`centroid[axis]`のbinary64 total-order、次にcanonical Triangle indexとする。個数`n`のnodeは`floor(n/2)`で左右へ分割し、leafは1 Triangle、node IDはpreorderで付与する。比較、Bounds union、中央値、node作成順をこの規則から変更せず、SAHや並列schedule順をcanonical結果へ使わない。

候補生成はroot対rootから始める。同一node pairでは`(left,left)`、`(left,right)`、`(right,right)`、異なるnode pairではAABBが全3軸で閉区間交差する場合だけ下降する。両方leafなら`a < b`へ正規化してpairを出力し、片方だけ内部nodeならその左右を順に、両方内部nodeならprimitive数の多い側を分割し、同数ならnode IDの大きい側を分割する。この規則により各unordered leaf pairを最大1回だけ生成するが、出力後もuint32 `(a,b)`のradix sortで昇順へ正規化し、隣接重複を除去してから狭域判定へ渡す。重複の有無を診断値へ残し、重複があってもdeduplicate後の意味は変えない。

候補counter、node数、byte数はchecked unsigned 64-bitで配列確保前とappend前に検査する。一意候補がSynthetic Profileの`MaxCandidatePairCount=2000000`へ達した後、次の異なるpairを検出した時点で追加割当や狭域判定を行わず、Synthetic Validation Resultの`SelfIntersectionCandidatePairCount`を`MaxCandidatePairCount + 1`、結果を`CandidatePairLimit`不合格として終了する。候補counter、`2 * TriangleCount - 1`のnode数、pair／node byte長のいずれかがchecked overflowする場合も、割当前に同じsentinelと不合格へ収束させる。Triangle AABBのepsilon拡張だけが非有限化した場合は`NonFinite`不合格とする。これらをLicensed ProfileUnsupported／Resource retryへ変換せず、Synthetic Harness固有の固定容量失敗として扱う。

sort／deduplicate後の候補だけをcanonical pair `(a,b)`昇順に処理し、AABB broad phaseを通らなかったpairへ`ClosedTriangleDistanceV1`やResidual最適化を実行しない。position indexを共有していても候補pair自体を除外せず、まず共有を無視した閉Triangle同士の接触／近接を求める。

共有indexが1または2のpairには、同じ共有Validator artifactに含まれる`SharedSimplexResidualV1`を追加適用する。共有1 indexならそのpositionを閉point、共有2 indexなら2 positionをcanonical index昇順で結ぶ閉segmentとして共有simplex `S`を定義し、`N(S) = { x | squaredDistance(x,S) <= epsDistanceSquared }`を閉じた許可近傍とする。ここで接触集合を`CA = { x in closed A | squaredDistance(x, closed B) <= epsDistanceSquared }`、`CB = { x in closed B | squaredDistance(x, closed A) <= epsDistanceSquared }`と定義する。`CA union CB`の全点が`N(S)`に含まれることを証明できた場合だけ正規の共有simplex接触として許可し、1点でもstrictに外にあるwitnessを得た場合、または包含を証明できない場合は保守的に自己交差として数える。binary64で計算済みの`epsDistanceSquared`の直後の有限表現可能値を`epsOutsideSquared = nextUp(epsDistanceSquared)`とし、`nextUp`はIEEE 754 binary64の正方向に隣接する値を返すbit-level操作へ固定する。有限非負値について`distanceSquared > epsDistanceSquared`と`distanceSquared >= epsOutsideSquared`を同値として扱い、epsilon等号を残余側へ含めない。

`SharedSimplexResidualV1`は上記集合包含を別実装へ委ねず、`ClosedTriangleDistanceV1`が生成する全point-triangle、全segment-triangle、全segment-segment witnessに加え、coplanar時はdominant-axisへ射影した2D Sutherland-Hodgman閉Triangle clippingで得る全intersection polygon頂点、非coplanar時は各方向3 edgeのsegment-triangle交点をcanonical候補順に検査する。各接触witnessの`pA`と`pB`について共有pointまたはclosed segmentへの二乗距離を同じKernelで計算し、どちらかが`>= epsOutsideSquared`なら残余交差とする。さらにepsilon近接領域については、各Triangleのbarycentric domainを共有simplexから遠ざかる方向へ制約した二次距離最小化を、共有artifact内の固定`ResidualClosestPointV1`で実行する。共有vertexではその共有vertexのbarycentric weightが`< 1`となる3 edge／face region、共有edgeでは非共有vertex weightが`> 0`となるedge／face regionを固定region順に列挙し、`squaredDistanceToS >= epsOutsideSquared`の閉制約を満たす各regionの最小Triangle間二乗距離を求める。いずれかが`<= epsDistanceSquared`なら残余交差、全regionがstrictに超過した場合だけ包含証明成功とする。`squaredDistanceToS == epsDistanceSquared`は許可近傍内、`== epsOutsideSquared`は残余候補内とする。数値的にregionを分類不能、非有限値、`nextUp`を生成不能な値は残余交差側へ倒す。この実装sourceとgolden結果もScript Bundle hashへ含め、Blender／Unityで別の近似判定を持たない。

pair分類は次の完全決定表に固定する。

| 共有position index数 | 距離条件 | 判定 |
| --- | --- | --- |
| 3 | 任意 | 重複Triangleとして先行GateでReject。自己交差数へ到達しない |
| 2 | 全接触／epsilon近接集合が共有edgeの`N(S)`内と証明済み | 正規の共有edge接触として許可。directed向き不整合は先行Orientation GateでReject |
| 2 | `N(S)`外のwitnessあり、または包含証明不能 | 共有edge以外にもcoplanar overlap、proper crossing、非共有近接があるため自己交差1件 |
| 1 | 全接触／epsilon近接集合が共有vertexの`N(S)`内と証明済み | 正規の共有vertex接触として許可 |
| 1 | `N(S)`外のwitnessあり、または包含証明不能 | 共有vertex以外にもcoplanar overlap、proper crossing、非共有近接があるため自己交差1件 |
| 0 | `minimumSquaredDistance <= epsDistanceSquared` | coplanar overlap、proper crossing、非共有vertex／edge／face接触、epsilon以内のnear missを区別せず自己交差1件として数える |
| 0 | `minimumSquaredDistance > epsDistanceSquared` | 非交差。自己交差数へ加えない |

したがって「Triangle interiorだけ」という別predicateは持たず、Topologyで共有されたedge／vertexのepsilon近傍だけを明示的に許可する。共有index数だけを根拠にpair全体を除外してはならない。共有indexなしのpair、または共有simplex許可領域外の残余接触ではTriangle間距離がepsilonちょうどなら自己交差、binary64でその直外なら非交差とする。共有simplexからの距離がepsilonちょうどの正常接触は閉じた`N(S)`内として許可する。候補pairをcanonical順に処理した件数がSynthetic Profileの`MaxSelfIntersectionCount=0`以下であることを要求する。

Synthetic ZCG後GateでBoundary、Non-Manifold、向き、自己交差、成分volume、Bounds、Triangle退化のいずれかが失敗したFixtureは`SyntheticFixtureValidationResult.Passed=false`とし、Synthetic Dataset Indexへ含めない。Validation ResultのTriangle数、連結成分、Bounds、Volume、Boundary／Non-Manifold／SelfIntersection Candidate Pair／SelfIntersection統計は合格・不合格ともZCG decode後の値を正本とし、canonical化前の値を残さない。Candidate Pair上限超過だけは完全列挙せず、規定のsentinel `MaxCandidatePairCount + 1`を保存する。この結果をLicensed Report／Receiptへ書き戻さない。

ZCG v1のschema byte上限は64 MiBとし、Decoderはそれ以下の呼び出し側`maxBytes`を必須とする。HeaderとIndexのGeometryByteLengthを配列確保前に照合し、Licensed TriangleMeshはEarly Licensed Profileの`MaxVariantTriangleCount`、Synthetic TriangleMeshはSynthetic Profileの`MaxTriangleCount`以下、PositionCountは対応Triangle上限の3倍以下、ConvexSetはEarly Licensed ProfileのHull／Vertex／Face上限以下へ制限する。Dataset種別とProfile hashは呼出側がDecoder起動前に固定し、ZCG内容から別Profileを推測しない。全record長はchecked 64-bit算術でpayload長と突き合わせ、overflow、宣言数過剰、途中EOFをRejectしてからだけ配列を確保する。未知Format／Versionを別形式として推測decodeせずRejectする。

`GeometryRelativePath`はGeometry Dataset rootからの相対pathで、CanonicalBundleIndexと同じNFC、`/` separator、segment、control文字、case-fold衝突、通常file限定の規則を適用し、拡張子を小文字`.zcg`へ固定する。各Variantは異なるGeometryRelativePathを持ち、Index／Report／Receipt artifactはGeometry Dataset rootの外へ保存する。directory階層は固定しないが、pathはIndexのcanonical identityに含め、DatasetCaseId変更時に暗黙で使い回さない。

Index Codecは各Variantを最終Report内の同じDatasetCaseIdを持つSelected／BenchmarkOnly Entryへ厳密に1対1対応させ、Tier付きDatasetCaseId構築式、Process、VariantId、Source／Geometry hash、Source／Actual Triangle、Target／Voxel property、QualityClassが一致することを検証する。Verifierは明示されたGeometry Dataset rootを再帰列挙し、symlink／junction／reparse point等をRejectして、正規化した通常file path集合がIndexのGeometryRelativePath集合と完全一致することを要求する。欠落file、Indexにない余分な通常file、path重複／case-fold衝突をRejectし、各fileの実byte長をGeometryByteLength、raw bytesのSHA-256をGeometryContentSha256へ照合する。探索順や拡張子推測で対象fileを選ばない。ReportだけにあるNoOp／Alias／失敗／Resource EntryはIndex件数へ含めない。

`DatasetContentSha256`はcanonical `LicensedRepresentativeDatasetIndex` bytesそのもののSHA-256とし、後続`GeometryBenchmarkRunManifest`へ同じ`DatasetId`とともに格納する。変動するAttempt時間、Peak Working Set、HostProfileId、Report hashはDataset Indexへ含めないため、同じGeometry集合とTool／Profile hashなら実行時間が変わってもDataset hashは変化しない。

最終的な双方向監査は小さなcanonical `LicensedFixtureSelectionReceipt` v1で閉じる。property順は`SchemaVersion`、`SelectionRunId`、`DatasetId`、`ReportContentSha256`、`DatasetIndexContentSha256`、`DatasetContentSha256`とし、SchemaVersionはinteger `1`、SelectionRunIdはReportと同じ小文字UUID、DatasetIdはIndexと同じID、3 hashは小文字64桁とする。`DatasetIndexContentSha256 == DatasetContentSha256`を要求し、Report bytesとIndex bytesを再hashして両Content hashへ照合し、Report内のSelectionRunId／DatasetIndexContentSha256とIndex内のDatasetIdも一致させる。ReceiptはReportとIndexの両方がcanonical検証に合格した後、最後に原子的に確定するcommit markerであり、欠落または不一致ならその選抜Runを未確定としてBenchmarkへ渡さない。これによりDataset hashは時間情報から独立したまま、失敗EntryやAttempt履歴を含む特定Reportを特定Indexへ固定できる。

canonical Loaderのschema上限はProfile 64 KiB、Source Catalog 16 MiBかつ100000 Entry、各Canonical Bundle Index 16 MiBかつ100000 Entry、Report 64 MiBかつ100000 Entry／合計200000 Attempt、Dataset Index 64 MiBかつ100000 Variant、Receipt 64 KiBとする。すべてのLoaderは`maxBytes`を、Catalog／Bundle／Index Loaderは`maxEntries`を、Report Loaderは`maxEntries`と`maxAttempts`を呼び出し側から必須で受け取り、各値が0より大きく対応するschema上限以下でなければ呼出し自体をRejectする。無制限overloadやschema上限だけを暗黙使用するpublic APIは設けない。

Loaderは、(1) seek可能入力なら配列確保前に総byte長をschema上限と呼び出し側上限の小さい方へ照合する。非seek入力では有効limitを`min(schemaMaxBytes, maxBytes)`とし、最大`limit + 1` byteまで試読して、Parser bufferへ保持するのは先頭limit byteまでとする。`limit + 1`番目を1 byteでも取得した時点でSizeLimitExceededとしてRejectし、そのbyteをJSON parserやhashへ渡さない。ちょうどlimit byteでEOFなら受理可能とする。(2) JSON nesting最大8、単一string token最大1024 UTF-8 byte、property数を各固定schemaへ制限、(3) SchemaVersionと固定root property順を検証、(4) 宣言Entry／Variant／Attempt件数をschema上限と呼び出し側上限へ照合、(5) その後だけ配列を確保、(6) 全要素、実配列長、ordinal順、末尾dataなしを検証、の順で処理する。Reportの`AttemptCount`合計も`min(200000, maxAttempts)`以下かつ実Attempts総数と一致させる。Receipt Loaderは参照先を自動で無制限読込せず、検証側が各参照文書用の個別上限を明示して読み込む。

### 10.3 Blenderヘッドレス前処理

Blenderを手作業用DCCだけでなく、ライセンスAssetをローカル変換するバッチプロセッサとして使用する。システムに既存のBlenderやPATH上の`blender`には依存せず、プロジェクト専用の固定版を明示パスから`--background --factory-startup --python --python-exit-code 1`で起動する。PythonスクリプトとAsset別RecipeからClosed Cut Component Set、Cut Connectivity／Attachment Metadata、Stencil契約、Compound Physics Proxy、検証レポートを生成する。製品用Strict Solidは生成しない。

```text
Licensed Display Asset
  -> Import／Transform・単位統一
  -> Closed Component／Micro／Physics Significant分類
  -> Surface Adjacency／Attachment Patch／Topology Anchor付きLink生成
  -> Component単位の閉鎖修復・簡略化・三角形化
  -> Stencil／Compound Convex検証
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
- Voxel Size、Adaptivity、簡略化上限。
- `Trusted Exterior`へ含める／除外するObject、Collection、Materialまたは面分類規則。
- Projection方式、最大距離、法線一致閾値、適用Weight、外形包含Margin、Reduction後の再Projection上限。
- 期待Bounds、体積範囲、最大面数。

単純な家具は無設定または共通Preset、車と建物は初回だけRecipeを調整し、以後は無人で再生成する。結果は`Success`、`NeedsReview`、`Failed`に分類し、警告だけで不正なSolidを採用しない。

### 10.5 Component閉鎖修復とGlobal Solid研究の分離

製品前処理は、独立Componentごとに切断由来Capを閉じられる最低限の修復だけを行い、Component間のBoolean Union、全体inside／outside判定、Generalized Winding Number、Voxel内部充填からのStrict Solid再構成を行わない。片面、底面欠落、自己交差、相互に食い込む部品は、D-117／D-118の表示・Stencil契約と個別Physics Convex契約へ振り分ける。標準Runtime、製品Asset Preprocessor、代表Assetの合格条件、高品質Fallback、Cache SchemaのいずれもStrict Solidを前提にしない。

Voxel／SDF Union、内部Flood Fill、制約付きSurface Projection、Global Watertight化、厳密な体積／inside-outside検証は`Future Research: Global Solid Reconstruction`へ移す。この研究はPhase 5.5以前の依存、完了条件、製品Fallbackではなく、開始時期も未定とする。研究成果を将来採用する場合は新しいArtifact種別、Profile、性能・品質Gateを別の設計変更として追加し、現在のDisplay／Stencil／Physics契約を暗黙に強化しない。

プログラムまたは固定版Blenderスクリプトで箱、柱、凹形状、複数Shell等を生成する`Synthetic Watertight Test Fixture`は製品Strict Solidとは別物として維持する。これは切断Kernel、Cap Loop、反復切断、Cook Benchmarkの既知正解入力であり、ライセンスAssetから生成せず、製品Asset Preprocessorの出力でもRuntime同梱物でもない。異常系Fixtureには意図的なBoundary、Non-manifold、自己交差、重複面等を持たせてよい。実Assetが偶然同じGateを満たしても補助比較へ使えるだけで、代表Assetでの成功やPhase 5.5完了を要求しない。

### 10.6 BlenderテンプレートとPythonの分担

公開可能な空の`.blend`テンプレートにGeometry Nodes、入力Collection、封鎖Collection、出力Collection、検証用設定を保持できる。Pythonはファイル入出力、Recipe適用、パラメータ設定、処理実行、検証、終了コードを担当する。これにより、失敗AssetだけをGUIで開いて中間状態を確認できる。

Voxel RemeshではUVや元の頂点属性を保持する必要はない。製品用Global Solidは生成しない。断面はUVやトライプラナー質感へ依存せず、Unity側の共通トゥーンシェーダーへ粘土色グレーまたはデバッグBase Colorを渡して描画する。

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

### 10.8 公開リポジトリとライセンス境界

変換コード、汎用Recipe Schema、ライセンスAssetを含まないテンプレート、検証コード、Blender版Manifest、Bootstrapは公開する。Blender本体、Synty／Poly Pro Universeの入力Asset、`.unitypackage`、付属`.meta`、生成されたDisplay／Stencil Geometry、Physics Proxy、加工済み断面素材は公開しない。`/Tools/Blender/`と`/Generated/`をgitignoreし、公開履歴への混入をCIで検査する。

Synty POLYGON City Packの購入原本は、公開Unityリポジトリと分離した非公開Git LFSリポジトリ`C:\Users\%USERNAME%\src\zantetsuken-assets-private`で管理する。2026-08-26時点で、`Vendor\Synty\POLYGON_City\v5\Original`へ`POLYGON_City_SourceFiles_v5.zip`と`POLYGON_City_Unity_2022_3_v1_12_4.unitypackage`を格納済みであり、両ファイルはLFS対象である。ダウンロード元と格納先のSHA-256一致を確認済みとする。

非公開リポジトリへのアクセスは各Assetライセンス上の許可を持つ開発チームだけに限定する。購入原本は変更せず保存し、展開したFBX／Texture、Phase 0.2のEarly Licensed Fixture／Asset対応表、加工Display／Stencil Geometry、Physics Proxyなどのライセンス派生物も公開Git履歴へ入れない。公開リポジトリから参照する場合も、公開Submodule、公開Release、公開CI Artifact、共有Cacheを経由してAsset本体を配布しない。

公開CIはPlaceholder Assetで前処理と切断ロジックを検証する。Syntyを用いる変換と製品ビルドは、許可されたローカル環境または限定private runnerだけで実行し、公開Artifactと共有Cacheへ生成物を残さない。

## 11. モーション方針

モーションは原則として既製HumanoidクリップをUnityでリターゲットする。NPCはIdle、Walk、Run、Turn、Startled、Run Awayを初期最小セットとする。Phase 4.7のV1予測対象NPCでは頭・胸の視線、腕IK、Foot IK等のプロシージャルPose Layerと左右反転を現在表示と未来評価の双方で無効化し、Catalog登録済みClip Poseだけを正本とする。これらのLayerや反転は、入力、weight／mode、適用順、世代、Evaluator Identityをimmutableな共通Pose Evaluation Inputへ追加し、現在／未来Backendが同じ処理を行えるようになった後だけ再導入する。切断時は現在姿勢を固定して物理へ移行するため、切断方向ごとの専用死亡モーションは作らない。

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
| D-003 | 即時応答 | GPU仮表示を先行し、実Mesh／Convexを非同期更新 | 確定 |
| D-004 | 仮断面 | clipで分離、Stencilで仮断面、最終的に実断面へ置換 | 確定 |
| D-005 | 非同期整合 | ジョブ結果を世代番号で無効化・コミット制御 | 確定 |
| D-006 | 人形 | 切断時に姿勢固定し、静的Mesh／剛体破片へ移行 | 確定 |
| D-007 | アセット | Synty POLYGON City Packを主素材に採用 | 確定 |
| D-008 | アート | 共通セルシェーダ、輪郭線、限定パレット、独自看板で統一 | 確定 |
| D-009 | モーション | 既製Humanoidモーションをリターゲットし、IKで補正 | 廃止：Mob Prediction対象とそれ以外のIK scopeをD-136で分離 |
| D-010 | データ表現 | Display Mesh／基底Solid Cut Mesh／実行時Cut Shell／Physics Proxyを分離 | 廃止：D-121で製品用Strict Solidを削除 |
| D-011 | 対象環境 | 初期製品スコープをPCVRとし、Quest単体対応は当面除外 | 確定 |
| D-012 | 性能目標 | 実アプリの両眼描画90fpsを基準とし、再投影を常用前提にしない | 確定 |
| D-013 | 開発順序 | 非VR PoCと性能評価を先行し、早期XR確認後にVR操作・UIを導入 | 確定 |
| D-014 | 検証HMD | Quest 3Sを有線Quest Linkで初期PCVR検証に使用 | 確定 |
| D-015 | 攻撃演出 | 三日月形の斬撃波を扇状に有限速度で飛翔させ、接触時に分離 | 確定 |
| D-016 | 先行計算 | 到達猶予で未来姿勢、表示Mesh、Convex切断を投機評価 | 確定 |
| D-017 | 未来評価 | 未来イベントDAG、世代検証、Commitから成る評価器を実装する。初期Dispatcherは固定PriorityClass、Deadline、stable順、固定容量、Schedule前取消だけのV1とし、費用学習・aging等は実測後に必要なものだけV2へ追加する | 段階導入で確定 |
| D-018 | 物理予測 | 必要時に局所PhysicsSceneを固定刻みで先読みし、接触時に検証 | 技術検証付き確定 |
| D-019 | 文書管理 | 本Markdownを唯一の設計正本とし、DOCXは使用しない | 確定 |
| D-020 | 観測基盤 | 固定名ProfilerMarker、Flow Event、固定長TraceLogger、Editorタイムライン、異常時保存をPoC開始時から実装 | 確定 |
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
| D-032 | 斬撃早期確定 | 旧Core Slash方式。D-047のSlashFront方式へ置換 | 廃止：D-047 |
| D-033 | 軌道優先規則 | 旧Hit Envelope方式。D-048の動的折れ線前縁方式へ置換 | 廃止：D-048 |
| D-034 | Segment因果性 | 旧扇形Segment方式。D-049の頂点／辺生成時刻方式へ置換 | 廃止：D-049 |
| D-035 | 刀姿勢入力 | OpenXR Grip Poseと持ち手別GripToKatanaOffsetで刀の位置・回転を決定 | 確定 |
| D-036 | 片刃判定 | 刀身軸方向を除いた運動とEdgeDirectionの緩い内積Gateで、峰側の復路を除外 | 確定 |
| D-037 | 刃筋難度 | SideNormal横滑りや厳密な角度を不合格条件にせず、遊びやすい判定を優先 | 確定 |
| D-038 | 刀の衝突 | 刀へ物理反発Colliderを付けず、有効な論理Sweep以外は全オブジェクトを素通り | 確定 |
| D-039 | 追跡異常 | Pose無効時はPrimedと履歴を破棄し、再追跡直後の見かけ速度からSlashを生成しない | 確定 |
| D-040 | Unity更新 | プロジェクトを作り直さず、Hubで新旧Editorを並存し、Gitアップグレードブランチ上で変換・回帰検証する | 確定 |
| D-041 | Unityディレクトリ | Unity Project Rootは1つを正本とし、版別の恒久コピーは作らない。同時比較時だけ兄弟Git worktreeを使用する | 確定 |
| D-042 | モブ未来計画 | Unityの通常AIとは別にMob Future Plannerを設け、遠距離モブほど長い未来区間を副作用なく計画する | 確定 |
| D-043 | MobPlan世代 | MobPlanへPlanGenerationと前提条件を付け、介入や経路変更時は旧計画と依存する投機結果を無効化する | 確定 |
| D-044 | AI LOD | プレイヤーが介入可能になるまでの最短時間を基準にNear／Mid／Far／Dormantの計画精度と更新頻度を切り替える | 確定 |
| D-045 | 遠距離モブ | Far／Dormantモブはキネマティックな経路と`ExplicitAnimationStateV1`全体を先行確定し、切断計算の猶予へ利用する。粗い時空間予約は初期成立条件に含めない任意の後段拡張とし、D-134の段階導入に従う | 技術検証付き確定 |
| D-046 | MobPlan Commit | 未来モブ姿勢に基づく切断成果物は、実命中、ObjectGeneration、PlanGeneration、姿勢許容誤差の一致時だけCommitする | 確定 |
| D-047 | SlashFront早期発生 | Latch時に切断面と初期折れ線前縁を不可逆に確定し、三日月VFX、飛翔、命中判定を同時に開始する | 確定 |
| D-048 | 動的三日月前縁 | Extending中も既存前縁を前進させながら同一平面内へ頂点／辺を追加し、VFX前縁と当たり判定を一致させる | 確定 |
| D-049 | 前縁因果性 | 各頂点／辺に生成時刻を持たせ、生成前の衝突を発生させず、追加済み前縁と命中結果を巻き戻さない | 確定 |
| D-050 | Finalized意味 | Finalizedは折れ線形状への追加終了であり、完成した三日月前縁の飛翔と命中判定は寿命または最大距離まで継続する | 確定 |
| D-051 | 前縁Sweep | 当たり判定は現在位置の線だけでなく、各折れ線辺の前フレーム位置から現在位置までの帯状Sweepで行う | 確定 |
| D-052 | 候補Bounds | 最大到達領域は投機候補のBroadphaseだけに使用し、命中は必ず実際のSlashFront Sweepで確定する | 確定 |
| D-053 | 通常断面 | 全体と同じ共通トゥーンシェーダーへ粘土色グレーを渡し、断面専用の写実質感や特殊陰影は使用しない | 確定 |
| D-054 | 断面デバッグ色 | 赤＝即時仮断面、青＝先行Commit、緑＝命中後計算Commit、通常グレー＝Stableを基本とし、補助状態は水色／黄／オレンジ／紫／縞で表す | 確定 |
| D-055 | デバッグ文字 | 全断面への常時テキストを避け、選択中1対象の単一パネルとEditor Timeline／Traceへ詳細を集約する | 確定 |
| D-056 | 前縁一価制約 | SlashFrameへSpanAxis／TravelAxisを固定し、SlashFrontをSpan位置ごとに前進位置が1つだけの粗い曲線として扱う | 確定 |
| D-057 | U字折返し | Extending中の微小逆行は無視し、閾値を超える逆行、頂点順序反転、自己交差では現在SlashをFinalizedして復路を別Slash候補へ送る | 確定 |
| D-058 | 前縁整形制限 | 放出済み前縁を再配置せず、凸包やU字内部の充填で未通過領域を命中させない。整形は新規辺の採否・分割だけに限定する | 確定 |
| D-059 | 映像キャプチャ段階導入 | PoC初期はUnity側の選択的キャプチャを使用し、切断PoC成立後にOpenXR API Layer方式を追加検証する | 確定 |
| D-060 | PoC録画負荷 | 通常は片眼・30／45fps・必要に応じ縮小解像度のGPUエンコードを基本とし、異常時リングバッファと限定的な両眼原解像度静止画を保存する | 技術検証付き確定 |
| D-061 | OpenXR Capture責務 | Windows PCVRのD3D11（D-137の`OpenXrProjectionCaptureProfileV1`）だけから開始し、Projection Swapchain ImageをRelease前に専用GPU TextureへCopyしてTraceと同期する | 技術検証付き確定 |
| D-062 | 映像の証拠範囲 | Projection Captureはアプリ提出画像の証拠とし、Meta compositor、Reprojection、レンズ補正、Quest Link圧縮後の最終HMD像は保証しない | 確定 |
| D-063 | Capture相関 | Unity FrameId、OpenXR Frame連番、predictedDisplayTime、Pose、TestRunId、Slash／Object／Task ID、Commit経路を共通Capture Recordで関連付ける | 確定 |
| D-064 | 開発Capture Profile | Windows PCVR、D3D11のみ、SDR／sRGB、MSAAなし、Dynamic Resolutionなし、Single Pass Instanced、App Projection Layer 1枚、左眼45fpsを初期固定構成とする | 廃止：Phase 0.11の成立確認用30fpsとPhase 4.8のOpenXR用45fpsをD-137で別Profileへ分離 |
| D-065 | Capture Fail Fast | 実行時のGraphics API、Format、Sample Count、Array Size、Layer、SubImageが固定Profileと違う場合は録画だけを停止し、構成差をTraceする | 確定 |
| D-066 | Capture環境記録 | Unity／Package／Meta Runtime／Quest OS／GPU／Driver／Swapchain／Link設定をRun Manifestへ保存し、環境差のあるRunを同一条件として比較しない | 確定 |
| D-067 | cooking非同期化 | Collider Bake／cookingを視覚切断のクリティカルパスから外し、Active境界は完了前でも命中フレームから断面と相対移動による隙間を表示する。Dormant境界は単独では即時表示を要求しないが、HasDetached／Cull失効済みOperationでは実装簡略化用の補助Capとして描画され得る。この場合もDormant側の相対移動と切断演出は起動しない | 確定 |
| D-068 | Pending物理共有 | `PendingPhysicsSplit`中は左右の表示破片を1つのFragmentGroup、Rigidbody、旧Colliderへ追従させ、小幅のめり込みと隙間内の旧Colliderを一時許容する | 廃止（D-132で保守Fallbackへ限定） |
| D-069 | 物理分裂Commit | Bake済みConvexの完成後、物理ステップ境界で左右Rigidbodyへ分裂し、親の線速度・角速度から各重心位置の速度を継承する | 廃止（D-132でProvisional生成時の分裂とFinal handoffへ置換） |
| D-070 | Cooking Profile | Cleaning／Welding無効化を有力候補とし、Fast Cook／Fast Simulationの費用と効果を同条件で実測する | 技術検証付き確定 |
| D-071 | 二段階Collider | 初回はFast Cookで物理分裂し、価値のある破片だけを余剰時間に別MeshのFast Simulation Bakeへ昇格させる | 技術検証付き確定 |
| D-072 | 微小付属物 | Physics Proxyで表現しない微小付属物が切断帯へ触れた場合は、物理破片を作らずHitConfirmed時に不可逆に全体消去する | 確定 |
| D-073 | GPU微小破片 | Micro Attachment消去時は汎用ローポリ破片をGPU解析運動で飛散させ、衝突なしのワールド／ローカル空間ディザで短時間に消滅させる | 廃止：D-089で実Geometryを主経路、汎用破片をFallbackへ変更 |
| D-074 | 全体低重力 | 空中斬り猶予を増やすため世界全体を低重力にし、PoC仮値を約0.5Gとする。周辺物理値は先に作り込まずプレイ後に判断する | 技術検証付き確定 |
| D-075 | 重力一元管理 | `WorldPhysicsProfile`を正本とし、Unity Physics、未来予測、解析軌道、GPU破片、VFXへ同じ重力を供給してRunごとに記録する | 確定 |
| D-076 | 即時Shadow | 即時切断中は同じper-instance clip／分離Offsetを適用した両面ShadowCasterで影を近似し、Shadow Map用Stencil断面は描かない | 技術検証付き確定 |
| D-077 | Shadow Batch | Shadow描画をStable片面群とPending両面群へ分け、切断平面は固定長Instance Recordで渡して平面値・切断数によるDraw分割を避ける | 技術検証付き確定 |
| D-078 | 有限仮キャップ | 即時キャップ板をローカルOBBと切断平面の3～6頂点交差多角形から生成し、他のTemporary Render Boundary半空間でclipしてからStencilで実輪郭へ制限する | 確定 |
| D-079 | Stencil彩色Batch | 左右眼いずれかでスクリーンBoundsが重なり、かつD-080のキャップ互換条件を満たさない対象だけを競合とし、Greedy ColoringしたColor単位でStencil Volume／Cap処理をまとめる | 技術検証付き確定 |
| D-080 | Stencil互換Group | 全World Cut Plane、Side／半空間、Offset、Cap描画状態が一致し、StencilPolarityをPositiveへ正規化でき、同一Colorへ置く`StencilCountBatch`の`BatchWindingBound` checked和が255以下となる対象だけを、Winding Countの非ゼロ和集合として同じStencil Colorへ統合する。Polarity UnknownはShell固有Groupへ隔離する | 技術検証付き確定 |
| D-081 | 両眼Cap可視性Cull | 論理破片×切断面ごとに左右眼Facingを判定し、全Capが両眼とも裏向きの互換Groupは彩色前にStencil Clear／Volume／Cap処理から除外する | 技術検証付き確定 |
| D-082 | Stencil競合領域 | Front／Back相殺後の非ゼロ領域を可視Cap Boundsで保守的に包み、Raw Stencil書き込みの一時的な重なりは競合としない。各眼でOBB投影または可視Cap Boundsのどちらかが非交差なら同一Colorを許可する | 技術検証付き確定 |
| D-083 | バックグラウンド実行基盤 | CPU幾何・予測計算はC# Taskの大量発行ではなくJob System＋Burstを基本とし、Task／AwaitableはI/Oと非同期制御へ限定する。Unity Objectの適用とGeneration Commitはメインスレッドで行う | 確定 |
| D-084 | Convex Job Pipeline | Physics ProxyのConvex分割、検証、質量特性、MeshData出力と`Physics.BakeMesh`をJob化し、Mesh公開とCollider／Rigidbody Commitだけをメインスレッド／物理ステップ境界に残す | 技術検証付き確定 |
| D-085 | Native Cook比較Probe | Unity Built-in 3D Physicsの`Physics.BakeMesh`を製品経路の正本とし、Native PhysXの頂点Hull経路、完全Topology経路、直接生成経路を早期に測定専用Probeで比較する | 確定 |
| D-086 | Native採用Gate | Cook時間の倍率差だけでは置換せず、Unity経路が実際のP99／90Hz要件を破り、Unity側最適化で解消せず、Native統合Prototypeまで成立した場合だけ物理経路の部分置換を再検討する | 確定 |
| D-087 | Voxel後Surface Projection | Voxel／SDFをTopology修復用中間表現とし、簡略化前にTrusted Exteriorだけへ距離・法線・包含制約付きで投影する。Projection失敗部はVoxel位置へ戻し、UV／Material転送は必須としない | 技術検証付き確定 |
| D-088 | 閉Topologyの自己交差契約 | Topological Watertightと自己交差のないGeometrically Valid Solidを区別する。自己交差は表示MeshとD-118のStencil Cut Shellでは条件付き許容する。個々のPhysics Convexには自己交差を許可しないが、Compoundを構成する別Convex同士のIntersection／Overlapは許容する。Geometrically Valid Solidは合成試験と将来研究だけの用語とする | 確定 |
| D-089 | 実Geometry GPU消滅 | Micro Attachmentの実Geometryを事前Shard Cluster化し、Vertex Pulling、解析運動、Indirect Batch、Opaque Dither Clipで消滅させる。汎用ローポリ破片は遠距離・Runtime転送予算超過時のFallbackとする | 技術検証付き確定 |
| D-090 | ライセンスAsset保管 | Synty購入原本と派生物は、公開Unity Repoの兄弟に置く非公開Git LFS Repo`C:\Users\%USERNAME%\src\zantetsuken-assets-private`で管理し、許可されたチーム以外へ共有しない | 確定 |
| D-091 | 固定物体の切断 | 分離運動／Impulse前にFixedSupportAnchorの半空間分類と必要最小限の接続判定を完了し、固定側を動かさない。完全Convex切断とcookは非同期で後追いする | 確定 |
| D-092 | ゼロ幅・休眠切断 | Kerfを0とし、両側FragmentがFixedの連結境界をDormantとする。LogicalCutOperationの直接子Supportを`Incomplete／FullyFixed／HasDetached`へ集約し、失効していないFullyFixedの場合だけ全clip、Stencil Volume、仮Cap、分離を丸ごと省略する。HasDetachedまたはCull失効済みならFixed同士の補助Dormant Capを含む全非Suppressed Capを実描画集合へ入れ、Incompleteなら既知Active Capだけを描く。後続切断による直接子置換と過去境界Active化は祖先OperationのCullを先に不可逆失効させる | 確定 |
| D-093 | 潜在切断痕 | Dormantな実Fragment Mesh境界と、Detached直接子を含む操作の通常Batchへ残したFixed Capの細い亀裂、輪郭線、線状Z-fighting、軽微なチラツキを切断演出として許容する。通常Capは片面描画とし、画面規模の面状Z-fightingや可視Cap欠落は不具合として避ける | 確定 |
| D-094 | 支持状態の粒度 | Dormant／Active／SuppressedはObjectの排他的状態ではなく連結な`CutBoundaryRecord`ごとの`ExposureState`とする。LogicalCutOperationの三値`OperationSupportState`と不可逆な`FullyFixedCullInvalidated`はExposureStateを置き換えず、意味上のActive境界集合と実描画Cap集合を別々に導出する。物理分裂、Fragment支持、境界露出、切断操作Cull、Geometry完成度、Work Result採否を独立した状態軸で管理する | 確定 |
| D-095 | 支持モデルの実装順 | 描画側のDormant判定に必要な純粋データモデル、LogicalCutOperation、OperationSupportState三値集約、Cull失効、Anchor到達性、世代検証、単体テスト、Trace契約をPhase 2より前に実装する。Collider切断、Rigidbody生成、cookはPhase 4に維持する | 確定 |
| D-096 | 分類不能時の物理 | 境界の一方でもSupportStateがUnknownならその境界をSuppressedとする。FragmentGroup内にUnknownなLogicalFragmentが1つでもあれば物理をPendingSupportClassificationとし、旧物理状態を完全維持する。Detached＋Unknownも分類確定まで動かさない | 確定 |
| D-097 | 複数切断の物理集約 | FragmentGroupの物理状態は全LogicalFragmentから`Unknownあり`、`全既知かつAnchoredあり`、`全Detached`の優先順位で集約する。PendingSupportClassification中も既知のActive境界のclip／Stencil／仮Capは許可するが、Group全体の運動は禁止する | 確定 |
| D-098 | Pending Cutと描画集合 | Pending CutはGeometry未Commitの切断とする。`ActiveTemporaryBoundarySet`はActiveかつGeometry未Commitの意味上の境界集合、`TemporaryRenderCapRecordSet`はOperationSupportStateとCull失効から導出する実描画集合とし、HasDetached／失効済み操作の補助Dormant Capを後者だけへ含める。描画コストと2～4枚上限は後者で数え、実Mesh適用とGeometry Commitの同時成功後だけ対応Recordを外す。CutBoundaryRecord、Cut Plane、論理Fragment、LogicalCutOperation、支持・Cull失効履歴はStable側へ保持し、物理Pendingとは独立管理する | 確定 |
| D-099 | 微小Fragment崩壊時期 | clip＋ポリゴン崩壊は採用しない。事前分類済みMicro Attachmentだけ命中同フレームにGPU崩壊でき、任意切断由来Fragmentは実Meshと論理Convexの対応確定後にGeometry Commit時または後追いで崩壊させる | 確定 |
| D-100 | Runtimeデブリ主判定 | 任意切断由来Fragmentは物理Convex対応を主判定とし、MissingまたはSharedのDebrisCandidateをデブリ候補にする。1 Render対複数専有ConvexはRepresentedとし、幾何寸法は消去可否の安全条件、大型・重要・Ambiguousは物理Fallbackとする | 確定 |
| D-101 | Fragment Trace ID | Render／Convex／Shared GroupのLocalIdは0を未設定用に予約した正のintとし、ObjectId＋ObjectGeneration内で種別ごとに一意かつ非再利用とする。TaskIdはWork Item専用のまま維持し、固定Traceのイベント別フィールド割当で対応EdgeとShared連結成分を記録する | 確定 |
| D-102 | Runtime Debris Buffer | 事前Asset用Immutable Debris Geometry Atlasと、Runtime Fragment用固定容量Geometry Arenaを分離する。Arena Sliceは単調uintのDebrisEventIdが所有し、最終Draw後のFence等の完了証拠と最小保持期間の両方を満たしてから回収する。容量不足時は再確保・待機せず品質低下する | 確定 |
| D-103 | 物理表現enum初期値 | PhysicsRepresentationStatusはPending=0、Represented=1、Missing=2、Shared=3、Ambiguous=4、SharedResolutionRoleはNone=0、Keeper=1、DebrisCandidate=2、PreserveFallback=3で固定する | 確定 |
| D-104 | Debris Reject Trace | FragmentのReject／Fallback理由はappend-onlyなTraceReasonへ格納し、Value0／Value1へReason enumを重複保存しない。イベント別表でStatus、測定値、閾値の意味を固定する | 確定 |
| D-105 | Debris Trace相関 | Runtime Arenaの4段階ライフサイクルEventをappend-onlyで追加し、Value0へuint DebrisEventIdを格納する。一意キーはTestRunId＋DebrisEventIdとし、IDカウンタはArenaがQuiescentかつ新しいTestRunIdのTrace Runを開始するときだけ再初期化する | 確定 |
| D-106 | Geometry／Cook性能Baseline | T-070を早期Cook比較Probe、T-076を製品Geometry完成後の補完・再解釈Baselineとする。表示Mesh切断、Convex切断、検証済みTemporary Low-Poly Proxy、cookを工程別に、計算KernelのSingle-Thread µs/op、cook／Commitの単発Latency、Job Batchの定常Throughput／End-to-End latencyへ分けて測り、保守的なP95／P99容量式を作る | 確定 |
| D-107 | Benchmark Manifest分離 | GeometryBenchmarkRunManifestは単一Target／Stage／ExecutionMode／CookingProfile／Metric／Unitの1測定系列ごとに作り、BenchmarkSuiteIdで一回のHarness実行を束ねる。型・値域・組合せ・全property順・非該当値のJSON nullを固定し、canonical保存はclean Repositoryだけに許可する。既存TraceRunManifestとCodec／Golden Hash／bundle形式を変更しない | 確定 |
| D-108 | Benchmark Result Bundle | 各Run Manifestへraw Samplesと固定Aggregateを持つGeometryBenchmarkResultを1対1対応させ、GeometryBenchmarkSuiteIndexがManifest／Result hashと件数を固定する。Suite開始・終了時に同じclean HEADを検証し、Repository外でIndexを最後に書いて原子的に確定する | 確定 |
| D-109 | Benchmark Case／Loader契約 | 1 Manifestを単一DatasetCaseIdと固定規模軸へ限定し、Manifestの説明変数とResultの測定値から容量式を復元する。Target×Stage×ExecutionModeを許可表で検証し、Result v1は100万Sample／64 MiBをschema上限、呼び出し側の明示上限を必須とする。Bytes／Countのraw値と順序統計量は整数に限定するがMeanはcanonical doubleとする | 確定 |
| D-110 | Benchmark集計／全Loader境界 | Percentの0..100制約からAggregate.Countを除外し、Meanを取得順binary64左畳みで固定する。Rejectedは対象結果を観測不能な試行だけとし、対象処理の失敗はFailureRateへ含める。Manifestは64 KiB、Indexは10万Entry／64 MiBを上限とし、Manifest／Result／Indexの全Loaderへ呼び出し側上限を必須とする | 確定 |
| D-111 | Suite内Dataset同一性 | 同一BenchmarkSuiteIdでは1つのDatasetIdを厳密に1つのDatasetContentSha256へ対応させ、Suite Loaderがjoin前に全Manifestを検証する。異なるDataset版は別Suiteまたは明示的な別DatasetIdとして測定する | 確定 |
| D-112 | 早期実Asset Fixture | Phase 0.2でSynty／Poly Pro Universe等の多数モデルへ共通の簡易Blender処理を適用し、Render／Convex Gateを自動通過した少数だけを非公開LicensedRepresentative Datasetへ固定する。Watertight既知正解は別のSynthetic Fixtureから得る。個別修理と最終最適化は行わず、投入母数とReject理由を保持し、全Asset互換性の証拠にはしない | 確定 |
| D-113 | 早期Triangle Variant | Phase 0.2のLicensed Render FixtureはOriginalと約100／500／1,000／2,000／5,000／10,000 Triangleを要求する共通Decimate Presetで生成する。TargetはPreset名であり正確な出力数を要求せず、Target／SourceからRatioを1回算出して反復探索せず、canonical化後ActualをBenchmark規模軸とする。Source／Voxel基底がTargetを上回れば削減率に関係なく生成し、Target以下のNoOpと同一hash Aliasだけを重複Geometryから除外する。Synthetic Watertight Fixtureの規模系列とConvex削減系列は別にする | 確定 |
| D-114 | 早期Voxel Variant | Voxel64／128／256をTopology再構成系列としてDirect Decimateと分離し、SourceとのTriangle差や増減にかかわらず基底Variantを保持する。限定Post-Decimate行列だけを生成し、各結果を再検証して大偏差はBenchmarkOnlyとする | 確定 |
| D-115 | 早期Fixture canonical契約 | 数値Gate、カテゴリ、Triangle帯、決定論的／資源上限をEarlyFixtureSelectionProfileへ固定し、Import前のSource母集合とPhase 0.2 EligibilityをEarlyFixtureSourceCatalogへ固定する。Source／Script／Presetはcanonical file index bytesでhashし、Blender実行前とReceipt確定前に実treeとの完全一致を再検証する。VariantIdはSource＋Tier内で一意、DatasetCaseIdはTierを含める。Selection ReportはEligible Sourceだけを対象にLaunch／Bootstrap／Importを区別した完全決定表に従うStatus／Attempt列と変動時間を記録する。Licensed採用GeometryはZantetsuCanonicalGeometry v1へ正規化し、binary32 decode後にRender／Convex Gateを再実行する。Synthetic Watertight Geometryは別DatasetとGenerator hashで固定する。LicensedRepresentativeDatasetIndexは再検証合格GeometryのFormat／Version／相対path／byte長／canonical file hashを完全なfile許可リストとしてTool／Profile hashとともに確定する。Index canonical bytesのSHA-256をBenchmark DatasetContentSha256とし、Report／Index両hashをLicensedFixtureSelectionReceiptで監査可能に固定する | 確定 |
| D-116 | Synthetic自己交差Broad Phase | Synthetic Watertight Fixtureだけについて最大20万Triangleの全pair列挙を禁止し、epsilon拡張AABBの決定論的`SolidCandidateBvhV1`で候補を生成してcanonical pair順へsort／deduplicateする。200万一意候補をSynthetic Profile上限とし、次のpairでSynthetic Validationを`CandidatePairLimit`不合格へ停止する。Licensed Fixture、製品Preprocessor、Runtimeでは実行しない | 確定 |
| D-117 | 許容的表示Mesh切断 | ランタイム表示Meshはfinite positionと有効index／Topology参照、cut-localに閉鎖可能なEdge Topologyだけを最低契約とし、全MeshのSelf-intersection、Winding、Inside／Outside、Shell、Component、Duplicate／Coincident除去を要求しない。交点とContourは空間近傍でなくOriginal Edge／Edge Use／Vertex Fan系譜で接続し、simple contour Fast Path、局所Arrangement、重複を許すBoundary Fanの順にCapする。D-118のStencil有向閉鎖契約および個々のPhysics Convex契約とは分離する | 確定 |
| D-118 | 許容的Stencil Cut Shell | Stencil入力は自己交差のないSolidではなくOriented Closed Triangle Chainを最低契約とし、Self-intersection、閉Component、均衡Non-manifold、Duplicate／Coincident、Internal／Nested Shellを非ゼロWinding semanticsで許容する。全体検証は前処理時の線形`OrientedShellValidator`だけとし、ランタイムに全Mesh検査を追加せず、切断Kernelの既存Count／Write／Commitで変更EdgeとCapの有向incidence、共有position、finite性だけを局所確認する | 確定 |
| D-119 | Stencil Polarity／8bit予算 | UniformWindingSignCertificateを持つComponentだけをsigned volumeからPositiveへ正規化し、未証明ShellはUnknownとして共有Groupへ入れない。専用8bit Stencil Byte全体をIncrementWrap／DecrementWrap Counterとして使用し、証明済みMaxAbsoluteWindingBoundを255以下のStencilCountBatchへ分割する。Sibling Batchは別Colorとし、単独超過／Unknown Bound／8bit非排他構成はStencil仮Capを省略して実Cap完成を優先する | 確定 |
| D-120 | Convex由来の質量特性 | Runtimeの質量・重心・慣性は表示Mesh／Strict Solid／Convex UnionではなくPhysics Convex B-repから求める。重複Compoundはfiniteかつ正和の`PhysicsConvexMassWeight`をLocal ID順binary64左畳みで正規化して親質量を保存し、交差ConvexのWeightだけを子体積比で継承する。各物理Commit対象Fragmentもfiniteかつ正のWeight和を必須とし、Weight 0 Convexだけの子は安全条件を満たす場合だけ質量移送なしで非物理デブリとして消去し、それ以外はCut Operation全体の物理Commitを拒否して旧FragmentGroupを維持する。密度1慣性は`assignedMass / convexVolume`でscaleし、失敗時は規定OBB／AABBまたは旧物理維持へ低下する | 廃止（D-133でFinal正本とProvisional近似を分離） |
| D-121 | 非Union標準Asset表現 | 製品用Strict Solid Cut MeshをRuntime、Asset Preprocessor、代表Asset合格条件、高品質Fallbackから削除し、標準を`ClosedCutComponentSet + CutConnectivityGraph + Compound Physics Proxy`とする。交差Componentを独立切断・Capし、Topology Anchor付き固定少数Attachment Linkの完全決定表とGraph connected-componentsで2個以上の出力Fragmentを決め、同一Cut条件のCapはStencil非ゼロ和集合として描く。小部品はVisualOnlyMicroまたはPhysicsSignificantAttachmentへ分類し、前者へ専用Convexを作らない。Global Solid Reconstructionは将来研究だけに隔離する | 確定 |
| D-122 | Phase 0.2簡易Boundary Fill | Poly Pro Universeで有効性を確認したBlenderのNon-Manifold選択＋`F`相当を早期Fixture候補へ加える。本命は次数2のBoundary Loopを個別封鎖する`BoundaryLoopFill`、人手操作に近い`BlindNonManifoldFill`は厳格な事後Gate付きBenchmarkOnly探索とする。元Geometryを上書きせず、別Object／Componentの結合やBoolean Unionを行わず、失敗VariantだけをRejectする | 技術検証付き確定 |
| D-123 | 大型建物の構造近似 | 典型建物を独立して閉鎖・切断できる外周Structural Slab 4枚以上と少数Attachment Linkで近似し、Slabごとに原則1個、入口等では少数の直方体Convexを同一固定Compoundへ持たせる。装飾は別Componentとして接続し、厳密な建築構造解析とBoolean Unionを要求しない | 技術検証付き確定 |
| D-124 | Player非接触 | 初期仕様ではPlayer Body／Handとプロップ／破片のPhysX接触を無効化し、刀と斬撃波だけを論理SweepでInteractionさせる。移動可能域とCamera壁内侵入防止は簡易Occupancy Queryと視界保護へ分離する | 廃止（D-131で限定保証へ置換） |
| D-125 | Safety Tether Tree | LargeStructuralPropの構造Support Graphとは別に、Ground Anchorを持つ固定FragmentをRootとする非循環Safety Tether Treeを保持する。Detachedな大型Fragmentも切断面Anchorの相対並進テザーで親へ接続し、全動的Nodeから地面へ一意なPathを要求する。通常のワールド並進テザーは使用しない | 技術検証付き確定 |
| D-126 | Tether制限と回転安全 | 相対並進上限をSafetyTetherLevelに応じて指数減衰させ、回転は論理物理分裂時のWorld姿勢を原点としてStructuralSplitGenerationに応じて指数制限する。cook／Collider差し替えで世代を進めず、Tree再構築不能時は旧Group維持またはSafetyFrozenへFallbackする | 技術検証付き確定 |
| D-127 | 即時Clip Plane予算 | D3D11 PoCは`SV_ClipDistance` 8面を性能／MSAA品質上の優先経路、Pixel Shader `clip()` 4面を固定Fallbackとし、RenderFragmentごとの容量超過面は即時Color／Depth／Shadow／Stencil Volumeからだけ無視する。候補資格はOperationSupportState／FullyFixedCullEligibleと一致させ、選択は既存CutBoundary公開列の古い順による未Commit祖先優先のdependency-closed prefixとして左右眼と全Passへ共有する。新しい後発境界の即時表示より祖先半空間とSibling分離を優先し、論理履歴、背景Geometry／Physics処理、Cap Record集合を変更しない | T-089付き確定 |
| D-128 | Phase 0.2 Building Scope | Poly Pro UniverseのBuildingはSource Catalog全体を母集合として保持しつつ、人間が処理前に豆腐型と判定して固定した`EligibleBoxLikeBuilding`だけを自動選抜へ投入する。複雑形状の除外をGeometry失敗へ数えず、Catalog hashからEligibilityと理由を復元し、成功率をEligible集合内だけで報告する | 確定 |
| D-129 | Shared Convex単一平面Fallback | 複数の大型RenderFragmentが同じ物理Convexを共有する場合は共有Groupの初回Commitを優先し、Commit後に2集合の頂点凸包をbounded GJKで判定する。strictな単一分離平面を全頂点検証できる場合だけ後追い分割し、凸包交差／包含、曖昧、予算超過、検証失敗では同世代の再試行を打ち切って共有物理と空中浮遊を許容する。Pending／Concurrent Job、Request／Native Work Slotを固定容量化し、満杯時は非待機のCapacityExceededとする。後続切断は旧精密化Jobを待たず現行共有B-repを切り、新世代だけを再評価する | T-075付き確定 |
| D-130 | 最小優先度Dispatcher | 初期`FutureEvaluationDispatcherV1`は固定PriorityClass、Deadline、受付成功時に内部Recordへ発行するstable EnqueueSequence、固定容量、Critical予約枠、Schedule前取消、Dispatch／Completion予算だけを扱うMain Thread Soft Real-Time Dispatcherとする。入力`EvaluationWorkItem`はSequenceを持たず、Schedule済みJobを中断しない。物理安全、命中済み物理、命中済み表示、近締切投機、Backgroundの順を固定する。内部実装は破棄・交換可能とし、Producer／DAG／Kernel／Commitから見えるAPI、不透明WorkToken、Trace、Generation契約を維持する。費用学習、aging、work stealing、厳密予約は実測後に必要なものだけV2へ追加する | T-090付き段階導入 |
| D-131 | Player非接触の限定保証 | Player Body／Handとプロップ／破片のPhysX接触を無効化し、刀と斬撃波は論理SweepでInteractionさせる。人工移動によるモデル化済みOccupancyへの代表的な新規侵入だけを簡易Queryで抑え、実空間HMDはClampしない。視界保護はbest-effortとし、未登録／非干渉物体のCamera被り、物体内部視点、Near Planeでの内部面、即時Cut Shell内での部分Cap／Cap欠落／左右眼差を許容する。Camera overlapを切断、物理、Geometry Commit失敗へ昇格せず、Job再発行や同期Fallbackを行わない | T-088付き確定 |
| D-132 | Provisional Rigidbody／Collision Proxy | 支持分類後、Final Convex cookを待たず各物理子へRigidbodyを作り、既存cook済みConvexを再cookなしで再利用する。非交差Convexは該当側だけ、交差／曖昧Convexは両側へShape Instanceを割り当て、同系譜Sibling Collisionだけを無効化して外界Collisionを全て有効にする。Ghost Contactと早い接触を許容する一方、Provisional質量は既存のOBB切断近似、失敗時は等Weightで親Canonical Mass Budgetを保存する。Final handoffでは物理Actorのpose／COM線速度／角速度を正本として動かさず、包含検証済みFinal Shapeとmass／COM／inertiaを同一Actorへ置換する。表示は物理Actorへ追従し、frame差による瞬間的な表示移動を許容する。公開前のUnknown、容量超過、構築失敗ではD-068の単一Group方式を維持し、公開後の非finite、速度超過、Constraint破綻ではGroup全体を不可逆な`ProvisionalFaultFrozen`へ封じ込める | T-091付き技術検証確定 |
| D-133 | Final質量正本とProvisional近似 | Final質量特性はPhysics Convex B-repと`PhysicsConvexMassWeight`を正本とする。ConvexをLocal ID順binary64左畳みでfiniteかつ正和へ正規化して親質量を配分し、非交差Weightは継承、交差Weightだけを正負出力B-repの有効体積比で分ける。各出力の体積、重心、密度1慣性を求め、`assignedMass / convexVolume`でscaleして平行軸合成し、失敗時は規定のConvex OBB、Fragment OBB／AABB、現物理維持の順へ低下する。cook待ちProvisionalだけは現在OBBの一段平面切断体積比、失敗時は等WeightでCanonical Mass Budgetを保存した近似mass／COM／inertiaを使用し、Finalへ昇格しない。handoffでは物理Actorのpose／速度とFinal Colliderの非張り出しを表示連続性、質量、運動量、角運動量、運動エネルギーの連続より優先する | T-085／T-091付き技術検証確定 |
| D-134 | Mob軌道Cacheの段階導入 | `MobPlan.RootTrajectory`の初期生成方式を、副作用のない固定ステップ二相更新、Waypoint／Lane Desired Motion、固定長未来Sample Queue、再生補間、移動距離由来`ExplicitAnimationStateV1`、`PlanGeneration`による粗い全Plan／Group無効化とする。Nearは同じKernelをライブ実行し、Mid／Farは有効なQueueを主に再生する。初期成立条件へORCA、依存Graph、部分再計算、Flow Field、軌道圧縮、時空間予約を含めず、実測後の後段最適化とする | T-092付き段階導入 |
| D-135 | 明示Animation State正本 | Current／Future AnimationのState、Clock、Transition、Blendの意味上の正本をゲーム側`ExplicitAnimationState`とGlobal FixedStepIdとする。Animator／AnimatorController／AnimatorControllerPlayableは任意のPose出力／Preview／Legacy Backendへ降格し、内部状態の読戻しやController逐次rolloutを標準MobPlan経路にしない。現在表示、未来切断、CPU Skinningは同じ対象Stepへ解決済みStateから交換可能なPose Evaluatorへ分岐する。ClipのLoop／Clamp、canonical duration、Source Time写像は`AnimationAssetSetVersion`へ結合したCatalogを正本とし、V1予測対象ではプロシージャルIKを無効化する。全骨Poseの全Mob／全Sample先行保存は行わない | T-018／T-044／T-046付き確定 |
| D-136 | IK／Pose Layer scope | Phase 4.7のV1予測対象NPCはCatalog登録済みのリターゲット済みClip Poseだけを使い、Look、腕IK、Foot IK、左右反転等を現在表示と未来評価の双方で無効化する。補正や反転はLayer入力Snapshot、weight／mode、適用順、Generation、Identityをimmutableな共通Pose Evaluation Inputへ追加して全Backendで同じ処理を行える後段だけに許可する。VR Controller実測姿勢から表示するプレイヤー腕のTwo Bone IK等、Mob Predictionへ入力されないIKは別scopeとしてこの制限の対象外とする | D-009を置換。T-018付き確定 |
| D-137 | Capture Profile分離 | Phase 0.11の短時間NVENC成立確認は`NvencBringUpProfileV1`（Windows／NVIDIA／D3D11、SDR／sRGB、左眼30fps、1280×720固定のRGBA8 sRGB入力、BT.709 limited-range NV12変換）を使い、Phase 4.8のOpenXR Projection Captureは`OpenXrProjectionCaptureProfileV1`（Windows PCVR／D3D11、SDR／sRGB、MSAAなし、Dynamic Resolutionなし、Single Pass Instanced、App Projection Layer 1枚、左眼45fps）を使う。30fpsと45fpsを同じ正本値として扱わず、Profile IDをCapture EnvelopeとRun Manifestへ記録し、Artifactは所属RunとFrame Relationから同Profileへ結び付ける。入力寸法、ImageRect、PixelLayout、GraphicsFormat、orientation、色変換の不一致またはRun中変更ではゲームを止めずCaptureだけをFail Fastする | D-064を置換。詳細は21.15。確定 |
| D-138 | NVENC bounded chunk bring-up | Phase 0.11のnominalは指定GPU／Driverを持つTier C hardware qualificationでだけ30fpsの120 cadence tick／4秒提出窓と提出後30秒のFinalization期限を使う。fault／Backpressure／Freeze／deadlineはTier Aで最大16 tick相当のfake clock／fake completionによる決定論的stepへ置換し、実時間1秒／10秒を待たない。一般CIは120 Frame、実NVENC、外部Decoder processまたは実process再起動を要求しない。実再起動Recoveryの期限はTier Cの代表caseだけで新processが`BeginRecovery`へ到達した時点から測り、process起動時間を別診断値とする。NVENC出力は複数Frameのraw Annex B Access Unitをaccepted順に連結するboundedなRun chunk Artifactとし、Phase 0.11は1 Run＝1 chunkを固定する。Frameごとのfile、hash、flush、rename、Artifact Completionを禁止し、全Accepted encode／appendのdrainとFrame Completion回収後、Workerの`TryJoin`前にchunkのhash確定、close、renameとContext terminal result生成を各1回行う。単一の`NvencCaptureRunCoordinator`がRun開始からPlan commitまたはAbortまで`NvencRunChunkContext`、Session Ownership Lease、Trace Freeze状態、Coordinator内部Registry slotおよびDispositionを所有し、別CoordinatorへのOwnership Bundle移譲を行わない。Phase 0.11では`Flush(true)`を要求しない。書込み中、Plan未登録またはcommit結果不明のchunkはprocess crash、device loss、強制終了等で全体を失ってよく、部分修復・部分公開を行わず次回Recoveryへ判断を委ねる。NVENCはPNG用RGBA CPU readbackを通さずGPU TextureからNV12変換して圧縮bytesだけをCPUへ回収する。Phase 0.11のchunk形式を製品用連続録画形式とせず、複数chunk、正式なchunk長、GOP／Container／segment、durability頻度、index／seek／保持期間はPhase 4.8で実測して決定する | D-137を補足。Phase 0.11／4.8境界として確定 |
| D-139 | Capture thread非待機とCompletion分離 | Phase 0.1は固定Unity版でthread-safe契約を持つ現行`ImageConversion.EncodeNativeArrayToPNG`をWorkerから直接使い、Main Thread PNG Fallbackを持たず、既存の固定容量Completion Queueを維持する。Phase 0.11はMain ThreadとRender Threadの双方でNVENC Completion、GPU Fence、bitstream取得、hash、file I/Oを待たず、Render Thread／Native Plugin callbackはboundedなGPU work登録だけで戻る。入力`CaptureSurfaceLease`はGPU変換がSource Textureを参照し終えた非同期証拠後、NVENC Input SlotはNVENC完了とbitstream所有権移転後にそれぞれ別々に解放する。NVENC完了回収、accepted順整列、chunk append、streaming hash／ByteLength更新、Frame Completion生成を単一専用Workerへ直列化し、各Frameのencode終端を短いlockを許容する固定容量SPSC Frame Completion Queueからexactly onceで通知して`ProducedArtifactCount=0`とする。Run chunkの確定はFrame Completionと分離し、CoordinatorがContextの`Finalized`／`Abandoned` terminal resultをbounded pollする。待機可能な完了処理は専用Workerが担当し、いずれのPool、Queue、Work Slotまたはchunk buffer枯渇時も待機せずBackpressureまたはCapture失敗終端とする | 一部廃止：Phase 0.1部分は継続し、Phase 0.11の単一物理Worker／reorder部分をD-142で置換 |
| D-140 | Captureテスト実行階層 | Phase 0.1／0.11のRuntime安全契約を変えず、試験をTier A通常CI／毎コミット、Tier B対応環境NVENC統合、Tier C hardware qualification、Tier D手動診断へ分離する。Tier Aはfake clock／GPU Fence／NVENC completion／Publication Serviceと数Frame・小payloadだけで状態機械と安全不変条件を検査し、実時間待機、実NVENC、FFmpeg、実process再起動、大容量fileまたは実障害を禁止する。Tier Bはsource-controlled trigger manifestが要求する依存範囲変更時に短い実native結合だけ、Tier Cは承認対象candidate自身のbuild identityへ結合した120 Frame nominalと代表Recoveryだけ、Tier Dは実hang／device loss／process kill／disk full／最大chunk／長時間Captureだけを扱う。分類不能な変更はTier Bへfail closedとし、Tier C通過済みbuild artifactだけをPhase承認／リリース相当へ昇格する。上位Tierは下位Tierのfault直積を再実行せず、実環境結合を証明する最小sentinelだけを重ねる | テスト累積時間削減、遅延検出および保守的な条件付き実行として確定。詳細は21.15 |
| D-141 | Capture Artifact streaming verification | 共通Artifact Storeのlength／SHA-256検証は、Artifact全長に比例する配列を確保せず、事前上限付き固定／bounded pooled bufferと同一open handleを使うO(1) memoryのstreaming verificationとする。Phase 0／0.1のstaging／final検証とdurabilityは維持する。Phase 0.11 Freshはtrusted internalな`NvencChunkFinalizationResult`、同一process／Run／OS lock／Context、close済み確定staging、非上書き同一filesystem移動を全て満たす場合だけstaging全hashを省略し、finalを1回streaming検証したPublish Receiptを同一PublicationのCaptureCompleteへ再利用する。Recoveryはprocess-local結果を信頼せず再検証する | Artifact全長配列、同一Freshでの多重全hash、Main／Render Thread検証を禁止。詳細は21.15 |
| D-142 | NVENC ordered two-worker pipeline | Phase 0.11は単一Session／単一論理Consumerの内部を固定2本のOrdered Submit WorkerとOrdered Output Workerへ分ける。Accepted線形化点で固定Submission Queue末尾へ入ったFIFO順を正本とし、Work TokenのSlotIndex／Generationを数値sortしない。Submit WorkerはFIFO先頭のGPU変換完了を待って同順に`NvEncEncodePicture`を呼び、各Accepted Workについて排他的な`Submitted`または`FailedBeforeSubmit` recordを容量8の固定SPSC Submit-to-Output Queueへ厳密に1件、同順で渡す。Output Workerは同Queue先頭だけを処理し、`Submitted`では対応Event／Output BufferからBitstreamを回収してchunk append、hash／length更新、Frame Relation追加を行い、`FailedBeforeSubmit`ではappendせず所有資源を安全に解放または隔離する。Frame Completionは両variantともOutput Workerだけがexactly once生成する。後続Eventの先行signalを探索せず、任意順Completionのreorder状態を持たない。GPU変換または先行NVENC処理によるHead-of-line blockingとCapture Backpressureを許容する。WDDM非同期NVENC、単一Session、固定2 Workerまたは順序不変条件を利用できない構成と順序違反では、reorder、複数Session、同期FallbackまたはBitstream解析を追加せずCaptureだけをUnsupported／Fail Fastとする | D-139のPhase 0.11部分を置換。Phase 0／0.1、Publication／Recoveryは変更しない。T-054と21.15で短いTier B／C確認を行う |

## 13. 未決事項

| ID | 論点 | 選択／質問 | 影響 | 決定時期 |
| --- | --- | --- | --- | --- |
| O-001 | 初期ターゲット | 解決済み：PCVRを採用（D-011） | Quest単体は当面スコープ外 | 2026-08-21 |
| O-002 | 目標FPS | 解決済み：両眼描画90fpsを基準（D-012） | 再投影は安全網として扱う | 2026-08-21 |
| O-003 | Temporary Renderer上限 | 同一物体の`TemporaryRenderCapRecordSet`について、補助Dormantを含む実Cap 2、3、4枚のどれを標準上限とするか | 描画コストと連続斬り感 | T-003後 |
| O-004 | 断面表現 | 共通トゥーン＋粘土色グレーは確定。機械内部や人体で追加記号・部品表現を使う範囲 | 年齢区分とアート制作 | アート検証時 |
| O-005 | 切断可能範囲 | 建物・道路まで切断対象に含めるか | レベル設計とメモリ | 垂直スライス後 |
| O-006 | 破片寿命 | 最大動的破片数、消去時間、スリープ規則 | 物理CPUと視覚密度 | T-010後 |
| O-007 | Collider仮状態 | 旧Collider維持時間と周辺破片の例外判定。Player Body／Handと刀は物理接触せず、刀は論理Sweepのみ | 違和感と実装複雑度 | T-005後 |
| O-008 | NPC構成 | Synty人物をそのまま使う範囲と顔・体型改造量 | 独自性と制作工数 | アート検証時 |
| O-009 | データ保存 | 切断状態をセーブ対象とするか | 再現性・容量・ロード時間 | ゲームループ決定時 |
| O-010 | ネットワーク | 将来的なマルチプレイ要否 | 切断イベント同期設計 | 企画判断 |
| O-011 | Trace保存量 | リングバッファ秒数、最大イベント数、書き出し形式の最終値 | メモリ、調査可能時間、ツール工数 | T-020後 |
| O-012 | Voxel品質 | Asset分類別のVoxel Size、Adaptivity、穴封鎖閾値 | 輪郭精度、面数、処理時間 | T-022後 |
| O-013 | 建物分割 | Structural Slabの標準寸法、入口用Compound分割、Ground Anchor／角LinkのRecipe指定方法 | 局所切断性能とアート破綻 | T-024／T-087後 |
| O-014 | 自動修復閾値 | 自動封鎖径、平面誤差、Solidify厚、Voxel Closing半径 | 誤封鎖、輪郭誤差、処理成功率 | T-027～T-029後 |
| O-015 | Blender更新方針 | 4.5.12 LTSから次版へ更新する判断基準と更新頻度 | API互換性、生成差分、保守期間 | LTS更新候補発生時 |
| O-016 | Unity CLI再評価 | 実験的CLIとUnity PipelineをCIへ採用するか | 保守性、自動導入、外部依存 | CI構築時 |
| O-017 | Slash Latch閾値 | 刀先速度、移動量、Sample Window、方向分散、再発射間隔 | 誤発射、体感遅延、面安定性 | T-034後 |
| O-018 | SlashFront分解能 | 頂点追加の角度／時間／距離閾値、最大頂点数、辺分割・簡略化規則 | 当たり精度、VFX連続性、CPU負荷 | T-035～T-036後 |
| O-019 | Edge Gate閾値 | Edge Lead Score、CutSample速度・位置、Recovery解除、異常速度上限 | 復路誤発射、取りこぼし、連続斬り感 | T-038～T-041後 |
| O-020 | Grip校正 | 左右持ちの既定Offsetとユーザー校正を提供するか | 刀表示の一致、刃方向判定、導入工数 | XR操作検証時 |
| O-021 | AI LOD境界 | Near／Mid／Far／Dormantを分ける最短介入時間、距離、更新周期 | CPU予算、見た目、予測再利用率 | T-045後 |
| O-022 | MobPlan Horizon | Tier別の`HorizonSampleCount`と`CommittedThroughFixedStepId`の長さ | 切断計算猶予、無効化率、メモリ | T-044～T-046後 |
| O-023 | モブ予約 | 粗い時空間予約のセル寸法、競合解決、群衆密度上限 | 交差回避、自然さ、計画費用 | T-047後 |
| O-024 | Unity更新頻度 | 6000.3.22f1から同一LTSパッチへ更新する条件と回帰基準 | 修正取込み、再インポート時間、安定性 | 更新候補発生時 |
| O-025 | 前縁逆行閾値 | 無視する逆行距離／角度／継続時間、Span bin数、自己交差epsilon | 手ぶれ耐性、U字誤前縁、斬撃の途切れ感 | T-052～T-053後 |
| O-026 | Unity録画設定 | D-137によりPhase 0.11の`NvencBringUpProfileV1`は左眼30fps、Phase 4.8の`OpenXrProjectionCaptureProfileV1`は左眼45fpsで確定。縮小率、リング秒数、異常後保存時間、静止画枚数 | GPU負荷、保存量、調査可能性 | T-054後 |
| O-027 | API Layer対象 | 解決済み：Graphics APIはD3D11のみ（D-137）。Phase 0.11はOpenXR API Layerを導入せず、Windows／NVIDIA／D3D11固定の最小NVENC Backendだけを対象とする。AMF／QSV、OpenXR Projection Swapchain直接Capture、その他のEncoder比較はPhase 0.11の対象外とし、必要ならPhase 4.8以降で別途判断する | 実装工数、GPU同期、対応PC | Phase 0.11／Phase 4.8判断時 |
| O-028 | 最終像録画 | Meta compositor／Quest Link後の映像を併録する条件と手段 | Reprojection、圧縮、HMD固有不具合の切分け | T-056後 |
| O-029 | Collider Upgrade規則 | 寿命、距離、接触／Query頻度、Sleep状態による昇格Score、同時Upgrade数、メモリ上限 | Physics CPU、再cook費用、二重Meshメモリ、差し替え頻度 | T-060～T-061後 |
| O-030 | Micro Attachment閾値 | Bounds、体積比、画面上寸法、切断帯幅、Anchor判定、重要部品Recipeの標準値 | シルエット保持、消去頻度、前処理工数、極小破片数 | T-062後 |
| O-031 | GPU Micro Debris予算 | 通常500～3,000 Triangle、品質低下開始5,000～8,000、Hard Cap候補10,000、Active Event 8～32、寿命0.3～0.8秒を初期値とし、画面占有面積／Overdraw、Immutable Atlas容量、Runtime Arena容量／Page／最小保持Frame／Fence方式／同時Upload、Draw上限、Allocation失敗時Fallbackを決める | GPU時間、Draw／Batch、Buffer転送・メモリ、見た目の密度 | T-063後 |
| O-032 | 最終重力と周辺調整 | 0.35G／0.5G／0.7G／1.0Gの採用値と、反発、Drag、分離Impulse、Animation、破片寿命の追加調整要否 | 空中斬り成功率、世界の重量感、テンポ、物理安定性 | T-064のプレイテスト後 |
| O-033 | Shadow近似品質 | 両面・キャップなし近似を許容する距離／時間、Stable専用Shader分離、問題時の簡易Shadow Cap導入条件 | Shadow GPU時間、Draw、接地影、Self Shadow、実装複雑度 | T-065後 |
| O-034 | Stencil Batch予算 | 最大Color数、OBB／Cap Bounds Margin、World Plane一致epsilon、Facing epsilon／ヒステリシス、Stencil Clear／Count方式、相殺不成立時のFallback、上限超過時にキャップを省略する距離／画面寸法 | CPU分類・彩色時間、Stencil GPU時間、Draw、仮断面品質 | T-066～T-068後 |
| O-035 | Job実行予算 | フレームごとのSchedule数、Batch Size、Worker占有上限、複数フレームJobのNativeメモリAllocator／寿命、MeshData一括Commit数、Bake同時実行数 | 90fps安定性、投機完了率、Pending滞留、メモリ | T-069／T-076後 |
| O-036 | Native Cook再検討閾値 | 「継続的に大きい差」の倍率、Unity Bake P99／Pending許容時間、Worker占有、Native部分置換へ進む最低改善量と保守工数上限 | Backend選択、実装規模、Unity更新追従、再現性 | T-070／T-076を比較できるPhase 4.1完了後 |
| O-037 | Surface Projection閾値 | Trusted Exterior分類、最大距離、法線内積、包含Margin、最小厚み、Reduction前後の再Projection条件、自己交差検出精度 | Silhouette回復、Solid堅牢性、自動成功率、前処理時間 | T-071後 |
| O-038 | Render／Convex対応閾値 | 専有Convex集合の被覆を近似する系譜、Bounds、固定数包含Sample、推定体積被覆率、境界距離、Shared Keeper Score、DebrisCandidate最大寸法・重要度、Ambiguous Margin | 誤消去、Collider欠落、共有Convexのめり込み、判定費用 | T-075後 |
| O-039 | Geometry／Cook容量予算 | P95／P99容量式から、1フレーム当たりWorker時間、Deadline別の同時切断数、Temporary Renderer上限、Batch Size、同時Bake数、Proxy品質段階を決める | 先行計算完了率、命中後Pending時間、90fps安定性、品質低下頻度 | T-076後 |
| O-040 | Player壁境界応答 | 禁止Occupancy侵入時にPlayer Rootを押し戻すか、侵入深度を増やす移動だけ拒否して退出を許すか。finiteかつ0以上の`occupancyExitEpsilon`、ExitSearchMaxHorizontalExpansion、MaxExitVolumeCount、MaxExitCandidateCount、ExitLineSearchSteps、ExitBlockedFixedStepLimit、MaxForcedOverlapFixedSteps、Near-Wall Fadeの開始距離と強度 | VR快適性、壁抜け、Fixed Step予算、予測単純性 | T-088プレイテスト後 |
| O-041 | Safety Tether Profile | initial並進上限、並進／角度減衰率、初期World角度上限、Spring／Damper、Hard Limit、最大Node／Edge数、SafetyFrozen閾値。正のminimum並進／角度上限は設けない | 大型破片の浮遊感、倒壊防止、Solver費用 | T-087後 |
| O-042 | Structural Slab自動化 | 大型平面Component抽出、OBB厚み、入口認識、外周順、Anchor／角Link生成を共通PresetとAsset Recipeのどちらへ置くか | Blender自動成功率、建物品質、前処理工数 | Phase 0.2候補調査／T-087後 |
| O-043 | Hybrid Clip予算校正 | Raster 8面を固定したままPixel fallbackを0～4面のどこへ置くか、Stable専用Shader分離、Ignored境界が見える最長時間とGeometry Job優先度 | GPU時間、MSAA edge品質、Shader register／varying、連続斬り品質 | T-089後 |
| O-044 | Provisional Physics Profile | MaxProvisionalActor／ShapeInstance／Constraint数、2 Slot × MaxProvisionalGroup分のGroup Snapshot固定容量、Provisional警告／Fallback開始時間、分離距離、法線再侵入Limit、`FinalContainmentEpsilon`、異常線速度／角速度、D6対Custom Constraint、Actor／Joint Pool導入閾値 | 生成／破棄CPU、Snapshot更新時間／メモリ、Broadphase、Solver時間、Ghost Contact、接触Impulse、連続切断、handoff品質 | T-091後 |
| O-045 | Mob軌道Cache Profile | Crowd StepのFixedStep倍率、Tier別Horizon／Refill閾値、最大Mob／Sample数、同時再計画Group数、Live Fallback予算、Hold許容時間、将来のGrid Cell／MaxNeighbors／ORCA Horizon | CPU、Nativeメモリ、Queue枯渇率、停止時間、重なり、先行切断Commit率 | T-092後。ORCA値は追加導入時だけ確定 |
| O-046 | Animation Pose Evaluator | controllerなしPlayable／MixerとRetarget済みPose Tableの採用、Rig Pose Buffer形式・Bone順、Source Timeの数値精度、Main Thread／Job Batch予算、2 Source BlendおよびimmutableなLook／IK Layer入力の導入時期 | Pose誤差、Main Thread時間、Job Throughput、Pose Tableメモリ、Humanoid Retarget品質、先行切断採用率 | T-018後。Loop／ClampとSource Timeの意味契約は未決にしない |

## 14. 技術検証項目

| ID | 対象 | 合格の考え方 | 方法 |
| --- | --- | --- | --- |
| T-001 | 斬撃検出 | 高速な刀でも切り抜けず、一意な切断面が得られる | 速度別1000回で欠落率と重複率を計測 |
| T-002 | 即時分離 | 入力フレームから仮分離が視認できる | GPUタイムと表示開始フレームを記録 |
| T-003 | 複数Pending | 2〜4切断で画質と性能が許容範囲 | 切断数別にCPU/GPU、Draw、overdrawを比較 |
| T-004 | Stencil断面 | 閉形状で穴・はみ出し・片眼ずれがない | 箱、凹形proxy、人形Cut Shellを両眼確認 |
| T-005 | Convex切断 | 物理不一致時間が短く、差し替えで跳ねない | 完了時間、接触破綻、速度連続性を計測 |
| T-006 | 表示Mesh切断 | 断面が閉じ、UV／法線／submeshが保持される | 代表10プロップを多方向に連続切断 |
| T-007 | 世代競合 | ジョブ中の再切断で古い結果が適用されない | 意図的な遅延を入れて順序を反転 |
| T-008 | Skinned切断 | 姿勢固定から静的破片への切替が見えない | 歩行・走行・腕振り中に各部位を切断 |
| T-009 | 入力モデル耐性 | 契約内モデルは自動前処理で切断可能になる | 変換検査とエラーレポートを確認 |
| T-010 | 破片予算 | 連続プレイでCPU／メモリが上限内へ収束 | 10分間の連続切断ストレス試験 |
| T-011 | XR描画 | Single Pass環境で両眼のclip／Stencilが一致 | 左右眼スクリーンショットと実機確認 |
| T-012 | Collider cooking | バックグラウンド化後にメインスレッドスパイクが残らない | Profilerで切断前後フレームを追跡 |
| T-013 | 非VR性能基準 | 同一負荷を自動再生し、変更前後を比較可能 | 固定カメラ、固定乱数、切断スクリプトで計測 |
| T-014 | Quest Link XR | Quest 3S有線接続で両眼表示、追跡、90Hz、Single Passが成立 | HMD内目視とProfiler計測 |
| T-015 | 斬撃波先行切断 | 接触前の完了率が即時レンダラ負荷を有意に減らす | 距離、速度、対象数別に事前完了率とPending時間を測定 |
| T-016 | 未来評価器統合 | DAGがReady Work ItemをV1 Dispatcherへ渡し、未Schedule取消、Schedule済みJobの世代不一致破棄、Commitを競合なく行う | 遅延、進路変更、再切断でPriorityClass／Deadline順を意図的に反転し、T-090のQueue単体契約と統合する |
| T-017 | 局所物理予測 | 介入なしでは高率に再利用でき、予測費用が利益を下回る | 姿勢誤差、採用率、予測CPU時間を測定 |
| T-018 | 明示Animation State／未来姿勢 | AnimatorController内部Stateを正本にせず、同じ対象Stepへ解決済みの明示Stateから現在／未来Rig Poseを任意順で再生成し、接触姿勢を十分な精度で予測できる | 単一Clip、Loop境界`0.98 -> 1.02`、0／複数cycle Phase、Clamp Clipの`nextDown(1.0)`／`1.0`／`> 1.0`と終端Hold、負Phase Reject、Clip hard switch、Hold、Near表示、Mid／Far未来Sampleを使う。同一Planから`tick 140 -> 103 -> 172 -> 121`と時系列順に評価し、同一Backendのcanonical Bone順Poseが要求順や直前のEvaluator呼出しに依存しないこと、Evaluatorが`PlaybackRateCyclesPerSecond`で追加進行しないことを確認する。AnimatorController State／Trigger／Clock／Transitionの読戻しと目的tickまでの逐次rolloutが標準経路で実行されないこと、現在表示Backendも明示Stateへ従属し独自Phase進行しないことを計測・検査する。controllerなしPlayable／MixerとRetarget済みPose Tableを同じState／Rig Identityで比較し、代表骨位置・回転誤差、実接触Pose誤差、Main Thread時間、Job Batch Throughput、固定Cacheメモリを記録する。V1予測対象では現在／未来の双方でLook／腕／Foot IK、視線多様化、左右反転が無効であり、Backend固有設定から暗黙にMirrorされないことも検査する。Clip ID／Mode／durationまたはAsset／Evaluation Profile Identity不一致、PlanGeneration更新、非finite Phase／Rate、未知Clipでは旧Pose／依存切断をCommitせず実姿勢Fallbackへ移る。最大finite値付近のRate／FixedDeltaによるstep duration乗算Infinity、phase delta乗算Infinity、Phase加算Infinityを各段階でRejectして旧StateをHoldし、最小subnormal付近のRateが乗算underflowで0になった場合はfiniteな0進捗として受理することを確認する |
| T-019 | Trace完全性 | Slash生成からCommit／破棄までIDと状態遷移を欠落なく追跡でき、完全／不完全／旧形式Unknownを誤分類しない | 正常、未Schedule取消、Schedule済みJobのGeneration Reject、Operation作成束の欠落、enqueue失敗、通常post-roll容量超過、履歴上書き、直前bundle公開失敗、Summary欠落の各経路を自動照合する。Recorderでは`CapturedCount == TriggerHistoryCount + CapturedPostRollCount`とpost-roll上限Nを維持し、ExporterのSummary付きSnapshot／Manifestでは`EventCount == TriggerHistoryCount + (N + 1)`を維持する。通常Eventあり／空Captureの双方でSummaryの全共通フィールド、TraceCaptureOverflowCount、Reason優先順位、同一Timestamp／FrameId時の入力順tie-break、Timeline末尾位置を検査する。Enqueue FailureまたはOverflowはIncompleteとし、過去のbundle公開失敗Countだけでは現RunをIncompleteにしない。ライブCapture DraftにはManifest hashを要求せず、freeze後に最終ManifestでStaged Draftだけを既存CaptureFrameRecord／Artifactへ昇格し、Dropped tombstone、事前hash、部分的なRecord Registry、Trace公開前Artifactを公開しない。bundle v1／Manifest v1と最終Capture ArtifactのManifest hash照合を変えず、旧bundleは閲覧可能だが完全性Unknownとする |
| T-020 | Trace負荷 | 記録有効時も90fps予算とJobタイミングを実用範囲で維持 | 無効／有効時のCPU、GC、メモリ、イベント欠落、Draft Factory／Registry、readbackからPNG stagingまで、freeze時の予約済みIntegrity Summary追加、Capture Draft全件Finalization、Plan検証の費用を比較する。Summary追加不能、Finalization不能、SaveAtomic失敗を同じpre-trace公開試行失敗として1回だけ加算し、次回成功時の失敗回数繰越、成功後だけの繰越Count resetも検査する。Trace公開後のArtifact retryは同Countへ加算しない |
| T-021 | 異常時保存 | 不変条件違反時に直前履歴と追加履歴を再読込可能な形で保存 | 世代不一致・二重Commitを故意に発生させて確認 |
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
| T-034 | Slash Latch品質 | 素早い振りは振り終わり前に安定Latchし、同フレームに初期SlashFrontとVFXが発生する。小動作・構え直しでは誤発射しない | 速度、移動量、方向分散、持ち手別に入力Traceを再生して遅延・誤発射率・面角度誤差・初期前縁発生フレームを測定 |
| T-035 | 動的前縁因果性 | Extending中も既存前縁が停止・巻戻りせず、新規頂点／辺だけが生成時刻以後に追加される | 折返し、急停止、手首回転、面外運動で頂点位置、生成時刻、命中履歴を自動検査 |
| T-036 | VFX／判定一致 | 粗い折れ線の三日月前縁、帯状Sweep、衝突時刻が視覚上の前縁と一致し、高速時もトンネリングしない | 線分数、速度、FixedUpdate間隔、判定厚み別に低速撮影、Trace、既知標的との命中差を比較 |
| T-037 | 早期投機効果 | Latch開始が振り終わり開始より近距離応答と遠距離事前完了率を改善する | 距離、振り時間、波速別に入力から表示までの時間、計算猶予、Commit率、破棄率を比較 |
| T-038 | Edge Direction Gate | 刃側の広い振り角を許容し、峰側移動はSlashを生成しない | Score閾値、速度、移動量、Sample Window別に往路・復路・斜め振りTraceを再生 |
| T-039 | 抜刀連続斬り | 振り戻しで誤斬撃せず、刀を返した次の有効斬りは遅延なく受理 | 抜刀、復路、返し、左右連続斬りを各1000回実行し誤発射・欠落・再準備時間を測定 |
| T-040 | NonCutting素通り | Gate不成立時に刀が地形・Prop・NPCへ衝突応答やHitを発生させない | 低速移動、峰打ち、Recovery、静止状態でPhysics／Query／Hapticsを検査 |
| T-041 | Tracking復帰 | 追跡喪失と再取得で巨大速度や誤Slashを生成しない | Controller遮蔽、Pose無効化、位置飛びを記録・再生しSample Resetを確認 |
| T-042 | Grip Pose校正 | Quest左右コントローラで表示刀、BladeFrame、実際の握り感が一貫 | 左右手、標準Offset、任意校正で姿勢差とEdge Gate結果を比較 |
| T-043 | Unity更新再現性 | Project再作成や版別コピーなしで新Editorへ更新でき、旧版へGitで復帰できる | 専用ブランチと一時worktreeでProjectVersion、Package Lock、固定テスト、XRスモークを検査 |
| T-044 | MobPlan再現性 | 同じ入力、Seed、NavMesh、PlanGeneration、Animation Clip Catalogから同じRoot軌道と明示Animation State列を生成できる | 固定シーンの計画Hash、経路、RootTrajectory、Clip ID、非wrap累積Phase、PlaybackRateCyclesPerSecond、Catalog内容hash、Group epochを比較し、Animator内部State、評価Backend差、暗黙のMirror設定をPlan生成・Pose評価入力へ混入させない |
| T-045 | AI LOD予算 | 遠距離モブ数を増やしても計画CPUとメモリが予算内に収まり、近距離反応を阻害しない | Tier別人数、更新周期、Horizonを変えてProfilerとTraceを比較 |
| T-046 | MobPlan無効化 | プレイヤー介入、経路遮断、別切断で旧計画と依存Animation State／Rig Pose／切断成果物がCommitされない | PlanGenerationを意図的に更新し、旧Stateを表示・未来評価へ再利用しないこと、Task破棄と実姿勢Fallbackを自動照合する。通常Animation変更に別runtime Animation Generationを作らず、Rig／Asset／Evaluation Profile Identity変更だけを独立検証する |
| T-047 | 時空間予約 | Farモブ同士が粗い予約下で目立って重ならず、予約計算が局所的に完了する | 密度別に競合数、再計画数、CPU時間、見た目を測定 |
| T-048 | モブ先行切断 | 遠距離モブの計画済み明示Animation Stateから必要候補だけをPose評価し、命中前のMesh／Convex完了率を改善する | 距離、Tier、Horizon、Pose Evaluator Backend別にCommit率、破棄率、Pending時間、評価Bone数を比較し、全Mob／全Sampleの全骨Pose先行生成が実行されないことを確認する |
| T-049 | Mob Trace完全性 | MobPlan生成から利用、無効化、再計画、切断Commitまで因果を追跡できる | MobId、PlanGeneration、SlashId、TaskIdで保存Traceを自動照合 |
| T-050 | 断面表示一貫性 | 仮断面から実断面、Stableグレーへの移行で陰影や輪郭が目立って変化しない | 共通トゥーン設定下で箱、凹形、人物を多方向に切断し、両眼映像とフレーム差分を確認 |
| T-051 | 断面デバッグ表示 | 赤／青／緑等が実際の処理経路と一致し、色覚補助表示を含めても90fps予算を阻害しない | 各Commit／Reject／Pending経路を強制し、Traceとの一致、GPU時間、Draw、選択パネル更新負荷を測定 |
| T-052 | U字折返し | U字・往復軌道で同一SlashFrontが前後二重にならず、往路は維持され復路だけがFinalized後の別Slash候補になる | 逆行量、速度、角度、停止時間を変えた入力Traceで頂点順序、Finalized理由、命中分布を検査 |
| T-053 | 前縁一価性 | Extendingと飛翔の全時刻でSpan binごとの前進位置が1つ以下となり、非隣接辺交差と頂点順序反転がない | ランダム軌道と極端な手首運動を再生し、各更新後に不変条件を自動検査 |
| T-054 | Unity選択的録画 | 片眼映像、異常前後リング、限定静止画がFrameId／Traceと一致し、録画有効時も性能予算内 | 最新のTier C Phase 0.11 qualification成功を入口証拠とし、Phase 0.11ではWDDM非同期NVENC、固定2 WorkerのAccepted FIFO順submit／Output回収、reorderなし、固定memory streaming verification、Fresh final全file hash 1回、CaptureCompleteでのReceipt再利用、Main／Render Thread非待機を前提にする。Phase 4.8で解像度、30／45fps、リング長、正式chunk長、GOP、Container、segment、durability頻度、index、seek、保持期間別のGPU／CPU時間、Dropped Frame、保存遅延を比較する。上限を外す場合はRegistry／Publication Planの計算量、payload copy／hash回数、停止時Publication時間を先に実測・改善し、必要な場合だけ代替EncoderまたはCapture経路を再評価する。Phase 0.11のchunk形式を製品用連続録画形式へそのまま昇格しない。T-054はPhase 0を再オープンせず、Phase 0.1／0.11のTier A～DへPhase 4.8の性能matrixを混入させず、両Phaseの完了条件にも含めない |
| T-055 | OpenXR Projection Capture | D3D11固定ProfileでRelease前CopyがSwapchain所有権、Texture Array、左眼SubImage Rect／Array Indexを正しく扱い、提出画像を破損しない。MSAA、別API、想定外LayerはFail Fast | 正常Profileで非録画時との画像・Frame timing差を比較し、MSAA、D3D12、別Array Size、追加App Layerを故意に与えて録画停止とTrace理由を検査 |
| T-056 | Capture相関と限界 | predictedDisplayTime、Pose、TestRunId、ゲーム内ID、画像が一意に対応し、Projection正常／最終HMD異常を区別できる | 意図的な描画不具合、Dropped Frame、Reprojection、Link品質低下を発生させ、Unity Capture、API Layer Capture、HMD観察を比較 |
| T-057 | Capture環境識別 | Runtime／Driver／Swapchain等が変化したRunを別環境として識別し、固定Profile逸脱を見逃さない | Driver、Meta Runtime、Render Scale、Refresh Rate、Unity Packageを個別変更し、Run Manifest差分と比較拒否を確認 |
| T-058 | Pending物理共有 | cookを意図的に遅延・失敗させても切断表示が同フレームに始まり、共有Collider中のめり込みと透明接触が許容範囲に収まる | Bake遅延を0～数秒へ変え、表示開始フレーム、分離量、接触差、Timeout品質低下、後続切断を測定 |
| T-059 | 物理分裂Commit | FragmentGroupから左右Rigidbodyへの切替で位置・速度が連続し、Solverによる大きな跳ねやメインスレッド停止がない | 並進・回転・接触中にCommitし、重心速度誤差、Impulse、主スレッド時間、視覚フレーム差を記録 |
| T-060 | 二段階Cooking | Fast Cookで物理分裂を早め、選択的Fast Simulation昇格が再cook費用を上回るPhysics CPU削減を得る | Fast Cookのみ、Fast Simulationのみ、二段階を同一切断Traceで比較し、Bake時間P50／P95／P99、Pending時間、Upgrade率、10分間のPhysics CPUとピークメモリを測定 |
| T-061 | Collider Upgrade Commit | 別Meshへの差し替えで位置・速度と接触が連続し、再切断済みの古いUpgradeが適用されない | Sleep、自由運動、接触中、同時再切断を再現し、Wake、接触Impulse、主スレッド時間、Generation Reject、Mesh回収を確認 |
| T-062 | Micro Attachment消去 | 切断帯へ触れた微小付属物が命中フレームに消え、実Mesh Commit、再切断、古いJob完了後にも復活せず、重要部品を誤消去しない | アンテナ、取手、ミラー、装飾を切断帯の内外で切り、AliveMask、AttachmentId、ObjectGeneration、Trace、VFX、極小Rigidbody生成数を照合 |
| T-063 | GPU Micro Debris | Immutable Atlasの事前Shard GeometryとRuntime Arenaへ転送した物理表現不能Fragmentが、連続消去でもGameObject／Rigidbody／ColliderとGCを増やさず、通常数千Triangleを少数Drawで両眼安定描画し、予算超過時に段階的Fallbackできる。完了証拠なしにSliceを再利用せず、即時clip中のTriangle崩壊を発生させない。TestRunId＋DebrisEventIdからSlice所有者と全ライフサイクルを一意に復元できる | 1 Event 20～150、Active合計500～3,000、5,000～8,000、10,000、Stress 1～2万Triangleを比較する。Arena Page枯渇、同時Upload、Event終了、長時間GPU Stall、Fence未完了／非対応、最小保持Frame経過だけの状態、DebrisEventId Wrap手前、同一Run内Quiescent、新Trace Run開始を再生し、使用中Slice非上書き、Fence後回収、Allocation失敗、経路無効化、ID非再利用、Run境界だけでのカウンタ再初期化、4 Eventの相関復元、Fallback、GPU／CPU時間、Draw、転送メモリ、GCを測定する |
| T-064 | 全体低重力プレイ | 一般プレイヤーが空中物体を狙いやすく、世界全体の浮遊感とゲームテンポが許容でき、全軌道系で重力が一致する | 0.35G／0.5G／0.7G／1.0Gを同一投擲・切断Scenarioで比較し、滞空時間、斬撃成功率、主観評価、Physics／予測／GPU破片の軌道差を記録 |
| T-065 | 即時切断Shadow | Stencil Capなしの両面Shadowが即時状態で許容でき、clip／Offsetがカラー像と一致し、片面／両面群分割が90fps予算を阻害しない | 箱、薄板、凹形、非閉形状を床／壁近傍で切り、Directional各Cascade、Point、Spot、Bias条件について実Capとの差分、漏れ、peter-panning、Shadow Draw、GPU時間を比較 |
| T-066 | Stencil彩色Batch | 非互換な可視Cap Boundsが左右眼のいずれかで重なる対象だけを別Colorへ分離し、OBB投影またはCap Boundsの非交差で安全と証明できる対象を同一Colorへまとめ、Stencil混入なしでCPU／GPU予算内に収まる | 左右眼だけでCapが重なる配置、OBBは重なるがCapは非交差の配置、Near Plane交差、全Cap重複、非重複、多数Pendingを生成し、Conflict Graph、Color数、Stencil差分、彩色CPU、Clear／Volume／Cap GPU、Draw、Fallbackを測定 |
| T-067 | Stencil相殺・互換Group | 検証済みOriented Closed Cut Shellの閉部分ではFront／Backがゼロへ相殺され、Polarity正規化済み・Bound合格対象の非ゼロ領域が可視Cap Bounds内で和集合になる。Unknown／不一致／容量超過は確実に分離またはFallbackする | 同一Slashの静止／共通親／別Rigidbody、追加Cut、異Material、Debug色差、正負Polarity、epsilon内signed volume、Unknown固有Group、負determinant Transform、多重Countに加え、局所winding不正、未相殺Boundary、逆向きCoincident Faceによる同一Shell内0 Mask、Near Plane、カメラ内部、非対称clip／Depth、MSAA境界を作る。Bound 254／255／256、複数Record checked和、uint overflow、Unknown Bound、単一Record超過で、255以下だけがIncrementWrap／DecrementWrap経路を通り、超過はstableなStencilCountBatchへ分割され、Siblingに無条件Graph Edgeが張られ、分割不能はStencil省略となることを確認する。Color再統合時にも255上限を再検証し、Saturateまたは部分Bit Counterを使用せず、8bit排他不能構成がFallbackすること、残留Stencil範囲、Key分類、World Plane epsilon、画像差、Fallback、Color削減率を検査する |
| T-068 | 両眼Cap可視性Cull | 両眼とも裏向きの互換Groupだけが安全に早期除外され、片眼可視、面近傍、正負破片でCap欠落や点滅を起こさずStencil仕事を削減する | 左右眼でFacingが一致／不一致となる配置、面横断、頭部微動、正負Cap、Frustum外を再生し、Cull判定、ヒステリシス、Stencil Draw／GPU時間、左右眼画像差を比較 |
| T-069 | Convex Job Pipeline | Convex分割と複数`Physics.BakeMesh`がメインスレッドを停止させず、世代不一致成果物を適用せず、Pending物理共有から安全に分裂できる | 破片数、面数、同時Slash数、Fast Cook／Fast Simulationを変え、各Job段階時間、Schedule数、Worker占有、Main Thread Commit時間、Bake P50／P95／P99、Generation Reject、物理差し替え時Impulseを測定。同一Mesh同時Bakeを不変条件として検出する |
| T-070 | Unity／Native Cook Probe | U1／N1／N2／N3を同一入力と近似条件で再現測定し、Unity経路の実費用、Hull再計算の寄与、完全Topology／直接生成の改善上限を工程別に説明できる。製品Geometry完成前の早期Probeであり、T-076の前提ではない | 8～255頂点級、単発／Batch、Fast Cook／Fast SimulationをRelease相当で反復し、P50／P95／P99、Throughput、各工程時間、Thread占有、メモリ、失敗率、出力形状、接触／Query品質を測る。Target×Stage×ExecutionMode許可規則に従い、単一DatasetCaseIdと固定規模軸を持つ各系列のManifest／Resultを作り、Suite Indexでhashと件数を固定する。N1のHullComputation、N1／N2のPhysXFormatBuild／StreamSerialize／StreamLoad、N3のDirectInsertionを独立系列として復元でき、版違いと非利用可能なNative生成物を明記する |
| T-071 | Global Solid Reconstruction研究 | Voxel／SDF Union、内部充填、Surface Projectionから自己交差のないGlobal Solidを再構成できるかを将来研究する。製品Phase、代表Asset合格条件、Fallbackには使用しない | 開始時期未定。研究を開始する場合だけ独立DatasetとArtifact Schemaを新設し、標準Closed Component／Stencil／Compound Convex経路へ影響しない比較として実施する |
| T-072 | 固定物体の即時切断 | cook遅延中も固定側が動かず、自由と証明された側だけが仮分離し、Commit後も位置・速度・Constraintが連続する | 単一Anchor、両側Anchor、面近傍Anchor、Compound Graph、連続切断、先行評価Reject、cook遅延／失敗を再生し、分類時間、誤Impulse、固定点変位、自由側軌道、Traceを検査する |
| T-073 | Dormant Cut再可視化 | LogicalCutOperationをIncomplete／FullyFixed／HasDetachedへ一意に集約し、失効していないFullyFixedだけは子数にかかわらず即時Stencil／仮Cap／分離を起動しない。HasDetachedまたはCull失効済みではFixed同士の補助Dormant Capを含む全非Suppressed Cap、Incompleteでは既知Active Capだけを描く。交差する後続切断ではCull失効後にDetached部品とその全境界断面が同一フレームに現れる | 大型建物を縦1面、交差2面、3面で切り、2子全Fixed、凹形状の3子全Fixed、3子中2子Fixed＋1子Detached、Anchored／Detached／Unknown混在、切断済み親への後続Cut Operationを検査する。default Incomplete、三値優先順位、ActiveTemporaryBoundarySetとTemporaryRenderCapRecordSetの差、補助Dormantを含む実描画件数と2～4枚上限を確認する。過去FullyFixed操作の直接子を再切断し、Cull失効が境界Active化より先に同一フレームで起き、一度失効したCullが再有効化されないことを検査する。Cap pair／Coverage探索、Cap Buffer圧縮、Mesh部分更新を行わず、線状亀裂、局所Z-fighting、禁止する面状Z-fighting、Cap欠落、旧面復活、背景Job完了順、再切断世代も確認する |
| T-074 | 支持Topologyモデル | 同一物体にActive／Dormant／Suppressed境界とPending／Ready Geometryが混在しても状態を損なわず、境界決定表、FragmentGroup物理集約、LogicalCutOperation三値集約、Cull失効、全履歴面の再評価、物理状態遷移、世代Rejectが決定論的に動作する | 正負Supportの全9組み合わせに加え、`Anchored／Detached`のActive境界と`Unknown／Anchored`のSuppressed境界が同一Groupへ混在するFixtureを再生する。OperationSupportStateのdefault Incompleteと`Incomplete > HasDetached > FullyFixed`、子数2／3以上、Cull失効済みFullyFixedを検査する。PendingSupportClassification中に旧Rigidbody、Collider、Constraint、TransformとGroup運動が変わらず、既知Active境界だけがActiveTemporaryBoundarySetへ入り、Suppressed境界とDormant補助CapはTemporaryRenderCapRecordSetへ入らないことを確認する。HasDetached／Cull失効済みではDormantが自発的なExposure要求を持たないまま補助Capとして実描画集合へ入ることも検査する。子数0／1／65、境界数0／257、重複子ID／境界ID、親と同じ子ID、未知ID、自己境界、境界へ接続しない子、世代不一致を原子的にRejectし、部分的なOperation／Fragment／Boundary公開がないことを確認する。後続切断では祖先OperationのCull失効、過去境界Active化、三値再集約の順序、不可逆失効、再分類後の集約遷移、Timeout Fallbackに加え、Operation作成、全Child／Boundary／正負Endpoint Link、ParentObjectGeneration、SupportGraphGeneration、状態遷移、Cull失効とReject理由を固定Traceから復元する。3子以上で同じID集合から異なる接続Graphを作るFixture、Generationの0／`uint.MaxValue`、Endpoint欠落／重複／反転、作成Trace束の中断、件数不一致、完了マーカー欠落を検査し、不完全Traceを状態再現の合格根拠にしない純粋C#テストを行う |
| T-075 | Render／Convex対応とShared単一平面解決 | Pending／Represented／Missing／Shared／AmbiguousとNone／Keeper／DebrisCandidate／PreserveFallbackを固定値どおり決定論的に扱い、物理表現不能な小Fragmentだけをデブリ化して、大型・重要・未分類・曖昧なFragmentを誤消去しない。大型Sharedは初回Commitを待たせず、strictな単一平面で安全に分けられる2集合だけを後追い解決する | default初期化、全Status／Role組合せ、1 Render対1 Convex、1 Render対複数専有Convex、対応なし、複数Render対1 Convex、多対多、専有＋Shared混在、複数大型共有、閾値近傍、世代不一致を合成する。不正組合せReject、近似被覆、Keeper選択、未分裂Fallback、SharedGroupLocalIdの0予約・世代内一意性・非再利用、Trace Reasonと対応／Shared連結成分の復元を検査する。さらに、離れた2凸包、凸包距離が`2 * SharedSeparationEpsilon`ちょうど／1 ULP内外、接触、部分交差、完全包含、凹型Fragmentの凹み内に別Fragment、3 Fragment以上、Bounds非交差／Bounds交差だが凸包分離、GJK反復／頂点／Convex／作業領域上限、非finite、ゼロWitness法線を用意する。Bounds交差だけで不能判定しないこと、GJK候補平面を全posed vertexのbinary64 signed distanceで再検証すること、分離可能時だけConvex／MassWeight／支持／Constraintを原子的に差し替えることを確認する。不能、不確実、出力片側空、質量／支持検証失敗では同世代でJobを再発行せず、同一Rigidbody／Collider集合とPreserveFallbackを維持する。Anchored混在GroupへImpulseを与えず、全Detached Groupだけが一体運動することも検査する。Request Slot 0／1／32／33件、Work Slot 0／1／2件、Queued＋RunningのPending上限、固定FIFO、容量満杯時の非待機CapacityExceeded、同世代再試行なしを検査する。Running JobをSupersedeしても完了まで両Slotが再利用されず、Queued JobのSupersedeはWork Slot未取得のまま終端化し、完了／Trace失敗／結果破棄の各経路でSlotが厳密に1回返却されること、Native allocation／Buffer成長がないことを確認する。精密化Job中の再切断では完了時にTargetと現Generationを比較してからOutcomeをSupersededへ確定し、旧Job完了を待たずCommit済み共有B-repへ新Cut Planeを適用する。Finished Eventの共通ObjectGenerationが受付時Targetで、旧`ObjectId + Generation + SharedGroupLocalId`へ一意に結合され、幾何Outcome公開よりGeneration Reject Traceが先になることを確認する。Outcome 1～6をゲーム状態へexactly onceで確定し、Trace成功時は対応Finishedが1件、enqueue失敗時は0件かつRun Incompleteとなり、どちらもOutcome／物理状態をrollbackしないことを検査する。同じ確定結果の再消費と2件以上のFinished enqueueを拒否し、Trace失敗後も再試行しない。Invalid=0をFinished Builder／Codecへ渡すとEvent列が不変のまま不変条件違反となることも確認する |
| T-076 | Geometry／Cook Microbenchmark | 製品の表示Mesh切断、Convex切断、T-077検証済みTemporary Low-Poly Proxy生成、`Physics.BakeMesh`を工程別に再現測定し、単発レイテンシとJob定常処理容量を分離して、入力規模からP95／P99完了時間を見積もれる。T-070の早期Probeを製品入力分布から補完・再解釈する | 公開合成DatasetをRelease Player相当／Burst有効でWarm-up後に反復する。計算KernelのSingle-Thread µs/op、Bake／Commitの直列単発Latency、Job Batchのcuts／triangles／convexes／cooks per second、Schedule／Complete latency、Worker占有、Main Thread Commit、GC／Nativeメモリ、失敗率を規模別に保存する。Target×Stage×ExecutionMode許可規則、Metric／Unit組合せ、系列一意性を検査し、`ColliderCommit + SingleThreadKernel`と`PlaneClassification + MainThreadCommit`をRejectする。ManifestのDatasetCaseIdと全規模軸をResultへjoinし、Samples／P50／P95／P99と容量式の説明変数を一意に復元する。同一Suiteへ同じDatasetId・異なるDatasetContentSha256を持つLatency／Throughput等を混在させたFixtureをjoin前にSuite Rejectし、別Suiteまたは別DatasetIdなら受理する。Bytes／Count Samples `[1,2]`からMean `1.5`を取得順binary64左畳みで再計算し、101件以上のPercent系列でもCountを範囲違反にしない。対象処理の失敗／FallbackがRejectedではなくFailureRateへ入ること、一部計測不能時の件数、全試行計測不能時のSuite Rejectを検査する。Manifest／Result相互ID・hash・件数、Aggregate再計算、Result差し替え、欠落／余分Entry、開始／終了clean検証、途中HEAD変更、Repository外一時出力、Index-last原子的確定、未知Schema／property Rejectを試験する。Manifest 64 KiB、Result 64 MiB／100万Sample、Index 64 MiB／10万Entryと呼び出し側のより小さい上限、宣言件数超過、非seek入力、過剰nesting、末尾dataを配列確保前にRejectし、全Loaderに無制限APIが存在しないことを確認する |
| T-077 | Temporary Low-Poly Proxy正しさ | 実装した各品質段階が有限で決定的なGeometryを生成し、表示ProxyはBounds／切断側／Triangle上限、物理Proxyはwatertight／面向き／凸性またはCompound規約／PhysX上限を満たす。不正入力を成功扱いせず安全な下位Fallbackへ移す | 中央／端／非交差、薄形状、極端なAspect、複数Fragment、退化Bounds、NaN入力を合成し、同一入力Hashからの出力一致、有限頂点、退化面、Bounds逸脱、切断側分類、体積、凸性、Primitive重複、上限、Validation Reason、Fallback順を検査する。合格した品質段階だけをT-076へ渡す |
| T-078 | 早期Licensed Fixture選抜 | Asset別Recipeや手修正なしの共通Presetで多数のSynty／Poly Pro Universe等のモデルを処理し、Licensed Render／Convex Fixtureを決定論的に選抜できる。失敗を無理に通さず、合格少数の実Asset試験をPhase 0.25／1／3／4へ供給し、公開Repoへライセンス派生物を漏らさない | 家具、車、道路設備、小物、Profile固定のSource Triangle帯と、人間が処理前に選んだPoly Pro Universeの豆腐型Buildingを固定Blender／Script／Presetで2回処理する。CatalogのEligible／Excluded、BoundaryLoopFill／BlindNonManifoldFill、NoOp／Alias、Resource Retry、Report／Index／Receipt hash、親BenchmarkOnlyのConvex子への伝播を検証する。Licensed Assetにwatertight／Strict Solid合格を要求せず、Cap Loop等の閉形状既知正解は別のSynthetic Watertight Test Fixture Suiteで検証する。公開合成FixtureとLicensedRepresentativeの保存先を分離し、選抜集合をBuilding全体または全Asset互換性と誤認せず、公開Git／Artifact／CacheへAsset名対応表とGeometryを混入させない |
| T-079 | Early Fixture Reduction Variant | Original／Tri100／Tri500／Tri1000／Tri2000／Tri5000／Tri10000を同じLicensed Render Sourceから決定論的に生成し、要求Targetと実Triangle数を分離して切断性能と形状検証結果を比較できる | 元Triangle数が50、100、120、500、900、1,100、2,200、5,500、11,000以上のAssetを含める。Target以下のNoOp、SourceがTargetを1 Triangleだけ上回る生成、Targetから上下へ外れるDecimate出力、複数Targetが同じActual帯へ入る結果、異Target同一hash Alias、TargetごとのRender合否、BenchmarkOnly分類を検査する。RequestedDecimateRatioをTarget／Sourceから1回だけ算出し、反復探索や帳尻合わせを行わず、Target差だけでRejectしない。Synthetic Watertight FixtureはこのLicensed Reduction系列へ入れず、別DatasetのGenerator引数で規模を作る。NoOp／Alias、ReportのSource／Target／Actual／Ratio／Applied、DatasetCaseId、Manifest InputTriangleCount、Convex設定への非流出を確認する |
| T-080 | Early Fixture Voxel Variant | Voxel Remesh基底をTriangle削減率だけで省略せず、相対解像度と限定Post-DecimateのTopology／表示品質／性能差を再現測定できる | 同一SourceをVoxel64／128／256へ通し、SourceよりTriangleが減る、同数、1 Triangleだけ変わる、増える各caseを含めて全基底を保持する。Voxel基底とSourceの同一hash Alias、Bounds Scale変更時の相対Voxel Size一致、Voxel256のBase／Tri10000／Tri5000を含む限定行列、行列外Variant非生成、Voxel基底がPost Targetを1 Triangleだけ上回る生成を検査する。Post-DecimateもTarget一致を要求せず1回のRatio適用後Actualを正本にする。各Variantのfinite／Triangle／Bounds／sampled表面距離、Boundary／Non-Manifold診断、BenchmarkOnly、DatasetCaseId、Report項目、Manifest InputTriangleCount、決定論的Profile上限とResource状態を確認する。watertight、自己交差、体積、Solid GateはLicensed Voxelの合否・Reportへ含めず、別のSynthetic Watertight Datasetで検証する |
| T-081 | Early Fixture canonical schema／Synthetic Validator分離 | Licensed Profile／Source Catalog／Bundle Index／Report／Dataset Index／Receiptから投入母集団、全試行、Render／Convex採用集合を一意に復元でき、Synthetic Watertight検証が別schema／hashへ隔離される | Licensed schemaではTierをRender／Convexだけに固定し、Solid Tier、VolumeError、SolidGate、Solid親、SelfIntersection Candidate／Count、CandidatePairLimitを未知property／enumとしてRejectする。BoundaryLoopFill／BlindNonManifoldFill／VoxelをRender VariantとしてBounds／中心／sampled表面距離で評価し、全Mesh自己交差や体積を計算しない。ConvexBuild親はRenderだけ、直接生成Convexは親nullとし、親不在、Solid／Convex親、別Source、hash不一致、QualityClass伝播違反をRejectする。Profile property順、Triangle帯、Catalog被覆、Launch／Bootstrap／Import失敗、全Status／Attempt決定表、canonical Bundle／Geometry path、hash、Loader byte／件数上限、Receipt結合を検査する。ZCG共通部はFormat／Version／Kind、座標変換Golden、binary32量子化、Triangle／Convex境界、再serialize一致を検査する。別のSyntheticWatertightFixtureProfile／DatasetIndex／ValidationResultではBoundary、Non-Manifold、向き、成分volume、自己交差と固定容量BVHを検査し、`SolidSignedVolumeV1`、`ClosedTriangleDistanceV1`、`SharedSimplexResidualV1`のepsilon／1 ULP境界、proper crossing、coplanar overlap、共有simplex、Candidate上限をBlender／Unity共有Validatorで照合する。Synthetic失敗をLicensed GeometryRejected／ProfileUnsupportedへ変換せず、Synthetic GeometryをLicensed Report／Index／Receiptへ混入させない |
| T-082 | Capture Draft／Publication Recovery | 現行Record中心のライブCaptureをDraft中心へ置換し、最終Manifest確定前にもrequest、readback、PNG encodeを相関できる。freeze後はStaged Draftだけを原子的に最終Recordへ昇格し、Trace先行公開とCapture再試行を一意に復元できる | Factory／Registry／Submission／Scheduler／readback completionをDraftで通し、Drop Reason 0～9の固定値、既存1～4互換、各経路との一意対応、unknown Reject、lease予約失敗、Registry満杯、readback／encode失敗、PNG staging失敗、取消、freeze drain Timeoutのrollbackと`Pending -> Dropped`終端化を検査する。`受付停止 -> producer稼働中のbounded drain -> producer取消／join／静止 -> Terminal Intent Queue最終完全drain -> Queue／私有Buffer所有権照合 -> 残存Pending強制Drop -> 通常Trace producer静止 -> 通常FIFO完全Drain -> terminal列構築／専用Append -> Recorder Freeze -> Snapshot -> Summary`を各境界で停止させる。drain中とjoin直前の成功Stage／通常Drop Intentが最終drainで必ず処理され、完成済みPNGを理由9へ誤分類しないこと、drain中のEncoded／通常Drop EventとBarrier前残存Eventが通常領域だけへ入り、最大強制Drop＋RingFrozenが専用reserveへall-or-noneで入り、通常領域満杯でも`AwaitingFreezeTerminal`から早期Frozenしないことを確認する。terminal EventType／TestRunId／ID順／末尾Ring／件数、通常Queue非空時Append、直接APIの状態違反、PostRoll／reserve境界、通常領域overflow時Incomplete、reserve不足Profileを検査する。DroppedにPNGがなくてもFinalizerが成功し、StagedのPNG欠落、DroppedへのPNG混入、Pending残存、TestRunId／Context不一致、重複ID、件数不一致では最終Recordを1件も公開しない。Plan Schema v1のRunInitializationIdを含むproperty順／型／null禁止／最短integer／NFC、16 MiB／10万Entry／path／呼出側上限、非seek `limit + 1`を検査する。信頼base rootから`runs/run-{TestRunId}`を導出し、OS排他lockの同時取得拒否とprocess crash解放、staging作成直後／各init tmp・Rename／final作成／各ready確定でのcrashを再現する。片側root、空／tmp-only root、ready片側、完了後staging削除済みfinal-onlyを正しく復旧し、marker／InitializationId／Root hash／Peer hash不一致、同一／祖先base root、別Run再利用を隔離する。許可marker／tmp集合、rooted／UNC／drive／`.`／`..`／空segment／backslash／case-fold衝突／symlink／junction／reparse point／TOCTOU差し替えをRejectし、固定path導出とRun root内解決を要求する。staging file flush、Plan-last commit marker、Trace公開前durabilityを検査する。Trace公開前の各失敗点でFrozen入力とdurable stagingを保持し、Summary payload変更時はManifest hashではなくtrace／bundle index hashだけが変わることを確認する。Trace公開後はManifest hashを変えず、PNGだけ／sidecarだけ公開後のクラッシュから一致側を保持して欠落側だけを再試行し、内容衝突だけをhard errorとして上書きしない。`capture.index.tmp`書込中／flush後／rename前、Index確定直後／通知前／cleanup途中の各クラッシュを再現し、tmpを完了証拠にせず、Planと同一なら再利用、partialなら削除再生成、canonicalな所有不一致なら隔離する。全期待PNG／sidecar成功後に同じcanonical bytesの`capture.index`をPlan削除前にdurable確定し、CaptureCompleteと期待集合を復元する。Artifact検証は全長配列を作らない固定memory streaming length／hashを共通正本とし、Phase 0／0.1のdurabilityを維持する。Phase 0.11 Fresh専用条件ではfinal全hash 1回のPublish Receiptを同一CaptureCompleteへ再利用し、Recoveryはprocess-local証拠を捨てて再検証する。後日のArtifact削除／改変検出、pre-trace orphan隔離、明示放棄時のTraceOnlyCaptureIncomplete、bounded staging／verification buffer枯渇時のbackpressureも検査する |
| T-083 | 許容的表示Mesh切断 | 全MeshのSelf-intersection／inside-outside検査なしで、finiteかつTopology参照が有効なRender Meshを任意平面で切り、cut-local BoundaryをCapしてCommitできる。別surface由来Contourを位置だけで誤接続せず、同一Original Edgeの交点positionはbit一致する | bind pose正常だがskinning後にsurfaceが交差する人物、互いに貫通するshell、duplicate face、同位置で別indexのcoincident shell、nested／internal geometry、disconnected component、winding反転、non-manifold vertex、2／3／4面edge、既存boundaryが切断帯外／内、UV seam、面積0／極小triangleを用意する。planeがvertex／edgeを通る、coplanar triangle、zero-length segment、短生成edge、同一点複数portを全符号組合せで切る。Topology IDを保ったまま位置だけを一致させた別surfaceが別Trackとなり、Original Edgeを共有するtriangleの交点positionが一致することを確認する。SimpleContour、LocalArrangement、BoundaryFan、OpenChainBridge、DegenerateClosure、Uncappableを強制し、各cut-derived BoundaryへCap incidenceがあり、入力由来boundaryは勝手に位置weld／全面修復されないこと、予算超過／NaN／Inf／壊れたindexではStable Geometryを維持することを検査する。全Mesh self-intersection、generalized winding、shell／component分類が実行されていないことと、各経路のSingle-Thread時間／Job Throughput／一時メモリも測定する |
| T-084 | 許容的Stencil Cut Shell | 全Mesh self-intersection検査なしで、前処理済みOriented Closed Triangle Chain、Polarity／Winding Bound証明、局所切断不変条件から仮Capの非ゼロWinding Maskを生成できる。自己交差と均衡Non-manifoldは受理し、未相殺Topologyまたは未証明容量は安全にFallbackする | bind pose正常／skinning後自己交差、閉じた複数Component、有向incidence総和0の4／6面Edge、Non-manifold Vertex、Duplicate／Coincident、Internal／Nested Shellを受理する。3／5面Edge、局所winding反転、Boundary、T-junction、共有Edge position不一致、NaN／InfをRejectまたはStencil省略へ送る。前処理Certificateを持つComponentではComponent順や全Triangle windingを反転してもsigned volume、Positive正規化、Geometry hash、最終Maskが再現し、epsilon境界はUnknownになることを確認する。signed volumeが正でも正負の局所Winding領域を持つ自己交差FixtureはCertificateなしでUnknownとなり、別Shellと共有されない。逆向きCoincidentはTopology Gateを通り同一Shell内でWinding 0 Maskとなる。単一Component Bound 1、複数Component checked和、自己交差の保守Bound／Unknown、親Bound継承、Fallback CapによるUnknown降格を検査する。Import時`OrientedShellValidator`がO(Triangle + Edge)で決定し、連続切断では変更Edge／Capだけを局所集計して未変更領域を走査せず、失敗世代をCommitしない。skinning後も共有Topology Vertexがcanonical posed positionを共有し、自己交差発生だけでは再検証／FallbackしないことをProfilerとTraceで確認する |
| T-085 | Convex質量特性と質量保存 | 重複Compound Convex、非交差Convex、複数Convexを横断する切断、連続切断でも表示Mesh／Union Solidを積分せず、親質量を保存した子の質量・重心・慣性を決定論的に生成できる | 単一箱、重ならないCompound、部分／完全重複Compound、凹形状を覆う複数Convex、Physics ProxyなしのMicro Attachmentを切断する。LogicalConvexFragmentLocalId順のbinary64左畳み、並列Reduction／再関連付け禁止、各中間finite、親質量正、Weight 0許容、全Weight 0／非finite／overflow Rejectを検査する。さらに片側の子がWeight 0 Convexだけを持つFixtureを用意し、全RenderFragmentがMicro／Debris安全条件を満たす場合は質量移送なしでその子だけを非物理デブリとして消去し、FixedSupportAnchor／Gameplay重要部品／Ambiguous／PreserveFallbackを含む場合は正WeightのSiblingも含むCut Operation全体のFinal CommitをRejectして現有効な単一FragmentGroupまたはProvisional Actor集合を維持することを確認する。いずれも質量0／任意最小質量Rigidbody、部分的な物理Commit、実装固有の質量再配分を禁止する。`assignedMass = parentMass * (weight / weightSum)`、続いて`densityScale = assignedMass / convexVolume`、`I_assigned = I_unitDensity * densityScale`の固定順をGolden値へ照合し、誤って`I_unitDensity * assignedMass`とした結果をRejectする。切断前後の質量和、子孫世代の累積誤差、重心、慣性主値、平行軸合成、列挙順不変性、生体積加算による重複質量なし、非交差Weight不変、交差Weightだけの体積比分配を確認する。片側体積がepsVolume以下のMicroは消去して反対側へWeight全量を残し、重要／Ambiguousなら現有効物理を維持する。体積0／非有限／極薄Convexでは正確積分、Convex OBB、Fragment OBB／AABB、現物理維持の固定Fallback順とTraceを検査する。`MassProperties`のSingle-Thread時間、Job Throughput、一時メモリをT-076系列で測定する |
| T-086 | 非Union Component／Attachment Graph | 相互に食い込む独立Closed ComponentをStrict SolidへUnionせず切断・Capし、Topology Anchor付き固定少数Attachment LinkとGraphから2個以上のLogicalFragment、支持、Physics Convex対応を一意に決定できる | 本体へ小物が食い込むAsset、凹Componentが1切断で3個以上へ分かれるAsset、2個固定＋1個自由、1／8／9 Link Patch、複数Patch、同位置だが未接続のComponentを合成する。EndpointのPositive／Negative／OnPlane全9組み合わせ、epsilonちょうどと1 ULP内外を検査し、++だけ正側、--だけ負側へ残り、他7組が切断されることを固定する。同一Patchで正負Linkが別々に残る場合、全Link除去、Anchor子系譜解決失敗、不正ID／position、VisualOnlyMicro消去、非MicroのPending、Timeout時の旧Group維持を確認する。親子履歴ではなくSurfaceAdjacency／AttachmentLinkのGraph connected-componentsが正しい子集合を作り、Graph未確定中は旧FragmentGroupを維持する。同一平面／Side／Offset／Material／PolarityのComponent CapはGeometry Unionなしで同じStencil互換Groupへ入り、Overlap Countが非ゼロMaskとして描かれる。条件差、255 Count超過、Unknown Polarityは既存Fallbackへ分離される。VisualOnlyMicroへConvexがなく、PhysicsSignificantAttachmentだけがCompound Convexを持つこと、大型Missingを誤消去しないこと、OverlapしたSibling Colliderの一時衝突抑止／再有効化が大Impulseを作らないこと、Strict Solid非常駐時のPlayerメモリを検査する |

T-075では追加で、Request Slot満杯直前にAdmission Candidateへ旧`ObjectId + TargetObjectGeneration + SharedGroupLocalId`を固定し、予約失敗後も世代が同じ場合はcandidate Targetを共通ObjectGeneration、TaskId 0としてCapacityExceededをexactly once確定できることを検査する。予約失敗とcompare-and-setの間にGeneration変更、Group置換、既存Outcome確定をそれぞれ挿入し、旧Candidateが`StaleBeforeAdmission`としてOutcome／Task／Finished Eventを0件のまま破棄され、新世代GroupへCapacityExceededを誤設定せず、新世代の別Admissionを妨げないことを確認する。単一Admission Coordinatorへ同じCandidateの重複確定呼出しを注入しても最初の成功1件だけがゲーム状態を更新し、Trace成功ならFinished 1件、失敗なら0件＋Run Incomplete、後続呼出しはEventを生成しないことも固定テストに含める。

T-082では追加で、`MaxInFlightDraftCount`が受付済み全Pending Draftをqueue横断で厳密に制限し、Registry外Pendingが存在しないことを検査する。freeze時のimmutableな`ForcedDropFrameIdSet`に対して、terminal列の欠落、余分、重複、順序違反、Reason違反、TestRunId違反、Ring欠落／複数を個別に与え、すべてall-or-noneでRejectされcapture列が不変かつ`AwaitingFreezeTerminal`に留まることを確認する。Buffer構築失敗、検証失敗、reserve書込み失敗の各地点から同じ集合で再試行して初回成功時だけFrozenとなり、成功前はSnapshot／Summary／Manifest／Plan／Exportが不可能で、明示Abortではbundleが公開されないことを確認する。Run root所有権はstaging／finalの2 lock pathを決定順に取得するものとし、異なるstaging base＋同じfinal base、同じstaging base＋異なるfinal base、2本目取得失敗、逆順要求、process crashを試験する。途中失敗では先に得たhandleが解放され、両Run rootが未変更であり、再試行可能であることを確認する。

T-082ではさらに、通常領域に空きがある状態と満杯の状態の双方で`BeginFreezeTerminalAppend`だけが`CapturingPostRoll -> AwaitingFreezeTerminal`を起こし、producer稼働中、通常Queue非空、drain未照合、およびBegin再呼出しをRejectして状態とcapture列を変えないことを検査する。terminal reserve有効時のpublic `Freeze()`が直接Frozenへ進めないこと、Legacy reserve 0だけが旧契約を維持することも固定テストに含める。`MaxInFlightDraftCount`と`MaxDraftCountPerRun`の境界、終端Entryを保持したままPending Slotを再利用する長時間Run、総Entry 100,000件、100,001件目の受付拒否を試験する。Pending不足／総Entry不足の`CaptureFrameAdmissionRejected`はID 0とKind／Value1の固定割当を持ち、Draft／Dropped／Plan件数へ入らないこと、理由5を`RecordDropped`へ渡すとRejectされることを確認する。

T-082ではLogger Seal境界をproducer enter前／active中／退出直後／Sealing後／最終Drain後へ移動し、active writer数のincrement後に行う`Open`再確認の成功をenqueue成功の線形化点とする。この線形化点が`Open -> Sealing` CASより前のEventだけがQueueと通常領域へ入り、active writer数のincrementがCASより前でも`Open`再確認がCASより後ならQueueへ入らず、Sealing中の拒否としてcutoff前ならRun Failure Count、cutoff後／Sealed後ならPost-Seal診断Countだけを増やすことを検査する。raw ParallelWriterをCapture Runから取得できないこと、Main Thread EnqueueとBurst writerが同じgateを通ること、late enqueueがあってもBegin／Appendが停止しないことを確認する。forced drop 0件／1件／上限件で各terminal Eventの22 fieldを1 fieldずつ改変し、Draft Trace Context、Checkpoint、未使用0、状態、Reason、Value、負の0／非有限の不一致がall-or-noneでRejectされることを試験する。既存2引数constructorがreserve 0、Capture Factoryがchecked `MaxInFlightDraftCount + 1`を設定すること、internal constructorの負値／超過／overflow、reserve有効時public `Freeze()`のfalse・無変更、Legacy時の既存bool挙動を固定テストへ含める。

T-082ではFailure Count cutoff直前／同時／直後にSealable writerを競合させ、各拒否がSealed Run CountまたはPost-Seal診断Countの厳密に一方へ入り、Sealed Countと生成済みSummaryが以後変化しないことを検査する。Summary取得後に保持済みwriter copyから試行してもQueue、Sealed Count、bundleのStateが変わらず、Post-Seal Countだけが増えることを確認する。通常Draft Dropの理由6～8と強制Dropの理由9へ同じ非ゼロSlashId、FrontEdgeId、ObjectId、ObjectGeneration、TaskIdを持つ既存CaptureFrameTraceContextを与え、いずれも12相関fieldが一致し、`FromState=Pending(0)`、`ToState=Dropped(2)`、元ContextにないSlashGeneration／Mob／Planだけが0であることを全22 field Validatorで確認する。通常Draft Dropについては、単一Terminal CoordinatorへDrop対Drop、Drop対Stageを同時投入し、確定した先頭Intentだけが共有資源のrollbackまたはStaging採用、Registry終端遷移、Pending Slot解放を各1回実行し、敗者が勝者の資源へ触れないことを検査する。Dropped確定直後にLogger破棄、seal競合、Queue／Native書込み失敗を個別に注入し、Trace enqueueが失敗してもDraftがDroppedのまま、freeze時のForcedDropFrameIdSetへ入らず理由9へ再分類されないことを確認する。失敗した通常Drop Traceはcutoff前のRun Failure Countを増やしてRunをIncompleteにし、RegistryのDrop Trace発行状態は成功・失敗とも`Attempted`へ一度だけ進む。同じCaptureFrameIdで消費APIを再呼出ししてもEvent、Failure Count、Draft状態が増減しないことを検査する。Legacy `RecordDropped`の理由1～4は既存の`FromState=0`／`ToState=0`を維持し、新設`RecordDraftDropped`が理由6～8だけを受理すること、理由9を両通常APIへ渡すとRejectされterminal Builderだけが生成できることも固定テストへ含める。既存CaptureFrameProfileの7引数constructorと2引数`CreatePhaseZeroUnityLeftEye`の結果が不変でTrace容量を持たず、PhaseZeroCaptureProfileSetが4096／32／10000を返し、Profile ID不一致、Trace Profile境界、Factory構築を決定論的にReject／受理することを試験する。

T-082ではTerminal Intent Queue容量を`checked(2 * MaxInFlightDraftCount)`の直前／一致／1件超過で検査し、同一Draftの未処理Intent上限2件と同一DraftについてRun中に受理される総数上限2件、3件目の拒否、Queue全体満杯、Coordinator drainとの競合を試験する。`TerminalIntentEnqueueStatus`の全固定値について、`Accepted`だけが私有Buffer所有権をCoordinatorへ移し、`Backpressured`だけがproducer所有のまま再試行可能、`DraftAlreadyTerminal`／`IntentLimitExceeded`／`RunNotAccepting`はproducerが私有Bufferを解放して再試行しないこと、`InvalidIntent`は所有権を移さずRunをFail Fastすることを検査する。Queue満杯後はdrainでAcceptedへ進む一方、受理総数2件到達後の3件目は何度待ってもBackpressuredへ変化せず、無限再試行しないことを固定する。複数条件が同時成立する場合のstatus優先順も1件ずつ試験する。freeze取消時はBackpressured Intentを再試行して受理させるか、`RunNotAccepting`を受けてproducer自身が私有Bufferを解放し所有数0をacknowledgeするまでjoin成功とみなさない。join直前の最後のenqueue、join後Queue非空、最終drain途中を個別に停止し、最終drain後だけQueue件数0、受理数と処理数一致、Queue所有Buffer数0、producer保持Buffer数0となること、その後の残存Pendingだけが理由9になることを確認する。

| ID | 対象 | 合格の考え方 | 方法 |
| --- | --- | --- | --- |
| T-087 | LargeStructuralProp／Safety Tether Tree | 外周Structural Slabを連続切断して構造的にDetachedな大型Fragmentを作っても、Safety Tether Treeが循環せず全動的NodeからGround Rootへ到達し、下側の移動へ上側が追従しつつ累積並進とWorld回転がProfile上限内に留まる | 4面建物、入口用Compound、1 Slab 2切断、凹Slabの1切断3子、2個Fixed＋1個Detached、複数Ground Anchor、外周Cycle、複数断面Patchを合成する。全Fixed FragmentがID順でRoot Link化され初期接続済み集合へ入ること、Root Link IDの継承と新規発行順、継承物理EdgeをID順で先にForestへ挿入してから未接続成分だけを新規Patchで接続すること、Incoming Edge競合／Cycle／複数成分Rootをfail-closedにすること、最大Patch重心Anchor、同値ID規則、Level 0／1／2の0.4／0.2／0.1 m候補、有限深度の幾何級数和と無限上限0.8 m、極深Levelの0 underflow、StructuralSplitGeneration、World角度制限を検査する。`SafetyTetherTreeGeneration`が`uint.MaxValue - 1`から`uint.MaxValue`へ進む最後の成功、Max値でのNo-op維持、次のTree変更Reject、非wrap／非再利用を試す。`StructuralSplitGeneration`も`uint.MaxValue - 1`の親からMax値の子を作る最後の成功と、Max値の親をさらに分裂させるOperation全体のReject、旧Group維持、角度上限が初期値へ戻らないことを試す。cook／Collider Upgrade／同一Group再生成でLevelと世代が変わらず、下側Fragment移動へ上側が相対制限内で追従すること、Tether Sibling overlapが大Impulseを作らないことを確認する。Anchor曖昧、世代不一致、Cycle、Ground Root喪失、Joint予算超過、Generation枯渇では自由落下の部分Commitをせず旧Group維持またはSafetyFrozenになる。成功Traceはsynthetic Ground Rootを0とする`Rebuilt → EdgeLinked×EdgeCount → TraceCompleted`束から同じTreeを一意に復元でき、欠落、重複、余分、順序違反、Generation混在、Reject候補の混入を不完全として拒否する。Unity Jointと独自制約候補のFixed Step CPU、Solver iteration、振動、Sleep率も比較する |
| T-088 | Player非接触Locomotion | Player Body／Handが切断前後のプロップへImpulseを与えず、モデル化済みの簡易Occupancyだけで人工移動の代表的な新規壁内侵入を抑え、Camera被りを許容しながら刀／斬撃波Interactionと未来物理の再利用性を維持する | Player／Prop Layer接触を監視し、静止壁、Episode開始地点から2 m超を通常移動した後にPlayerへ侵入するTethered Slab、対向する2 Volume、角での3個以上のOverlap、中心一致、回転Primitiveの垂直法線、周期振動を誘発する交互最深Volume、Pending旧Collider、切断開口、HMDの実空間Leanを試す。各Fixed StepのExitSearchBoundsが現在Capsuleと最大候補Sweepのunionへ再配置され、遠方Slabを漏らさず、同一Tickの全候補でBounds／Volume集合が不変であることを確認する。最大候補Sweepが`ExitSearchMaxHorizontalExpansion`ちょうどなら評価でき、1 ULPまたは規定Fixtureで超えた場合はVolume Query、部分候補、Player移動なしで`SearchBoundsExceeded`となることも検査する。移動Slab起因では物理Commitを巻き戻さず`ForcedOccupancyOverlap`へ入り、AllowedLocomotionPlane上の有限候補を全関連Volumeで評価すること、適用ごとに`(MaxDepth, SumDepth, DepthByVolumeId[])`が厳密減少して同一Snapshot内の周期を作らないこと、epsilon未満の入力にも最小量を要求しないことを確認する。Volume数と展開候補数は各上限ちょうどで固定長領域内に収まり、1件超過、checked count overflow、候補容量超過では部分Volume／部分候補を評価せず、Playerを1 Tickも動かさず対応Reasonの`OccupancyExitBlocked`へ入ること、Episode中にManaged allocation／Buffer成長がないことを検査する。有効な平面内減少方向がないFixtureは振動や任意軸移動をせずBlockedへ移り、毎Tick厳密減少する極小進捗Fixtureも`MaxForcedOverlapFixedSteps`で`EpisodeTimeout`になることを確認する。Fade、明示的な安全Pose復帰、Occupancy移動後の再開、全深度がexit epsilon以下での通常Policy復帰、HMD非Clamp、Player接触Impulseによる物理予測Reject 0、SlashFront命中と切断Commitも検査する。さらに未登録の小型／装飾／Debris／非干渉物体がHMDへ被ってもCameraや物体を強制移動せず、厳密Mesh包含検査を起動しないこと、即時Cut Shell内部またはNear Plane交差で部分Cap、Cap欠落、内部面、左右眼差が生じても切断／物理／Geometry Commitを失敗させずJob再発行や同期Fallbackを行わないこと、Stable Geometry置換後にTemporary Stencil由来の部分Capを残さないことを確認する |
| T-089 | Hybrid Clip Plane予算 | D3D11／Quest LinkのColor、Depth、Shadow、Stencil Volumeで同一のstable Plane選択を使い、Raster 8面とPixel fallback最大4面でGPU時間とMSAA edge品質を保ちながら、容量超過面をRendererだけから安全に無視できる | 0、1、7、8、9、12、13、32候補面を持つ単一／複数RenderFragmentを用意し、先頭8面が`SV_ClipDistance`、9～12面がPS `clip()`、残りがIgnoredになることをShader captureとProfiler Counterで確認する。FullyFixed Eligibleは候補0、HasDetached／Cull失効済みは全非Suppressed、Incompleteは既知Activeだけとなることを検査する。境界追加／Commitを伴わない`Suppressed -> Active`、`Dormant -> Active`、OperationSupportState遷移、FullyFixed Cull失効、RenderFragment対応変更で同一フレームの候補が再構築されることを確認する。既存CutBoundary公開列の古い順、左右眼、Color／Depth／Shadow／Stencil各Pass、カメラ移動、画面外復帰で選択が一致し点滅しないことを検査する。同一枝へ13回以上連続切断し、選択列が未Commit祖先についてdependency-closedで、Ignoredな後発面により祖先外Geometry復活、Sibling重複、面状Z-fightingを生じないことを確認する。順序違反した復元Fixtureは違反以降をIgnoredとして安全に収束する。Ignored境界でもCutBoundaryRecord、世代、支持、背景Mesh／Cut Shell／Convex Jobが残り、同期待機やJob cancel／再発行を生じずStable Commitで正しい形状へ収束することを確認する。Ignored Volumeに対応するCap板をBatchへ残したまま、Clear後Stencil 0でColor／Depth writeを生じないこと、別RecordのResidual StencilがCap Boundsへ届くFixtureでは既存Conflict分離またはStencil仮Cap省略Fallbackが働き、誤った板を表示しないことを検査する。MSAA 1x／2x／4x／8x、pixel-bound／vertex-bound Sceneで全PS clip、Hybrid、Raster 8のみを比較し、Pixel fallback数とStable専用Shader分離をO-043へ記録する |
| T-090 | 最小優先度Dispatcher | 固定容量・非割当のV1が物理安全をBackgroundより先にScheduleし、低優先度投入でCritical予約枠を消費せず、Schedule前取消とSchedule済みGeneration Rejectを一意に処理する | 5 PriorityClass、同一Deadline、Deadlineなし、同一Class stable順、Queue上限一致／1件超過、Critical予約枠、Tick Schedule／費用予算、Background starvation、Batch化、取消競合、二重Completion、古いGenerationを合成Work Itemで再生する。低優先度Jobを大量投入した直後にもCriticalPhysicsSafetyとConfirmedPhysicsが次のDispatchで選ばれ、CapacityExceededで待機・eviction・同一Frame無限再試行・Managed allocationを生じないことを確認する。全`EnqueueOutcome`、無効Deadline、Sequence枯渇、受付失敗時のInvalid Tokenも境界試験へ含める。V2相当の高度機能を実装せず、受付結果はOutcome／Counter、受付成功後のSchedule／Completion／Cancel／Generation Rejectは既存Traceから復元し、存在しないEnqueue Eventを要求しない |
| T-091 | Provisional Rigidbody／Collision Proxy | cook待ち中も各既知物理子が連続したpose／速度と外界Collisionを持ち、再cookなしの旧Convex再利用から物理Actor優先でFinal Colliderへ移行する | 単一Convex、非交差Compound、切断面を横断するCompound、Anchored／Detached、両側Anchored、Unknown、2子／3子以上、同一半空間のDisconnected Child、cook待ち中の連続切断、外部Dynamic物体、床接触、Ghost Contact、小物の切断隙間侵入を試す。非交差Shapeが片側だけ、交差／epsilon内Shapeが両側へ割り当てられ、同系譜SiblingだけCollision無効、外界Collisionは全有効であることを確認する。Provisional生成でcook 0、Geometry Resource共有、固定容量内のActor／Shape／Constraint原子的公開、上限一致／1件超過で部分公開なしの単一Group Fallbackを検査する。OBB切断体積比とLocal ID順の残差吸収、全0／非finite時の等Weight、連続切断で各世代のProvisional質量和が親Canonical Mass Budgetと一致すること、正質量を作れない場合は部分公開しないことを確認する。初回Provisional生成ではRender Anchor pose／点速度／角速度が連続する一方、Final handoffではActor pose／COM線速度／角速度がbitwiseまたは規定epsilon内で不変で、Final Shape全頂点が由来Provisional Convex half-spaceの`FinalContainmentEpsilon`内に収まることを確認する。包含不能、張り出し、frame不一致ではFinalを公開せずProvisionalを維持し、Colliderを動かして外界penetrationを作らないこと、表示だけの瞬間移動を許容すること、Final分離Impulseを二重適用しないことも検査する。D6／Custom Constraintの再侵入、Solver時間、最大接触Impulse、Broadphase pair、生成／破棄時間、Sleep率を測り、非finite／異常速度／Constraint失敗だけが安全Fallbackとなること、Timeoutでも同期cookやpose巻戻しを行わないことを検査する |
| T-092 | Mob固定ステップ軌道Cache | 同じSnapshot、Intent、Path、Seed、PlanGeneration、Animation Clip Catalogから同じRoot軌道と`ExplicitAnimationStateV1`列を生成し、Nearのライブ更新とMid／FarのQueue再生が同じ移動Kernel／Animation Plannerを共有して先行切断へ接続できる | 固定MobId順、Current／Next二相更新、FixedStep倍率、Waypoint／Lane、Queue wrap、Horizon補充、Render補間、移動距離由来PhaseとPlaybackRateCyclesPerSecondを再生し、V1ではMirror入力もBackend固有Mirrorも生成しない。Render補間では`HorizonSampleCount = 1`、`2`、開始直前、開始ちょうど、終端ちょうど、終端超過、最後のSample直前1 FixedStepでoff-by-oneやHold条件の逆転がなく、`stepId < StartFixedStepId`では先頭Sample全体でHoldし、`stepId < CommittedThroughFixedStepId`のときだけ同一Clip Stateを補間することを確認する。Group公開は全Mob descriptor検証後の単一Group epoch atomic storeだけが読取可能点で、Commit途中の一部Mobだけ新HorizonまたはClip／Phase／Rateになる観測がないこと、旧Job完了、入力末尾Sample slotのpin／Snapshot、wrap時の未再生上書き禁止、Reader完了境界後の旧slot回収、epochのwrap／ABA対策を検査する。`HorizonSampleCount * CrowdStepScale`、`StartFixedStepId`加算、`stepId`減算のchecked overflowではPlan／補充を公開せず既存区間維持のHoldとなり、`FixedStepId`のwrap／再利用で古いSampleが未来区間として再利用されないことを確認する。NavMeshAgent／Root MotionがRoot位置を二重更新しないこと、全Plan／Group無効化でGenerationが進み旧軌道・未来姿勢・切断成果物がCommitされないことを確認する。固定容量の最大Mob／Sample数、Background Queue満杯、Mid／Farのunderflow、Near Live Fallback予算超過では再確保・Main Thread待機・無制限再試行を起こさず、規定のState全体Holdと固定Profiler Counterへ低下し、既存MobPlan lifecycle Traceが矛盾しないことを確認する。ORCA、依存Graph、Flow Fieldを無効のままでもPlayableで、多少のMob重なりを許容してCPU、Nativeメモリ、Queue枯渇率、再利用率、先行切断Commit率を測定する |

T-090では入力`EvaluationWorkItem`にSequence fieldが存在しないこと、Descriptor不正／CapacityExceeded／NotAcceptingが次の成功受付のSequenceを進めないこと、受付成功だけが連続Sequenceを内部Recordへ割り当てることを検査する。不透明`WorkToken`からSequenceのbit layoutを推測せず、`TryGetState`の診断SnapshotとTask lifecycle Trace `Value1`が同じ内部値を返すこと、古いToken世代が再利用SlotのSequenceへアクセスできないことも確認する。

T-091では`ProvisionalPhysicsCommitted`、`ProvisionalPhysicsFallbackActivated`、`ProvisionalPhysicsFinalized`、`ProvisionalPhysicsSafetyFrozen`の成功enqueueが各ゲーム結果につき1件、Trace enqueue失敗時は0件＋Run Incompleteで、状態rollback、Trace再試行、重複Eventがないことを検査する。全`FragmentGroupPhysicsState`固定値、公開前Fallback Reason 1～10、公開後Primary Fault Reason 1～4とTraceReasonの一対一対応、Containment Disposition 1～3、CutOperationId、ObjectGenerationをEventから復元し、Unknown state／Reason／Disposition、Generation不一致、同一結果二重消費を原子的にRejectする。公開前Fallback EventはFromStateとToStateが同じ実状態で、`Value1`だけが要求したProvisional状態になることを全開始状態で確認し、存在しないProvisional遷移をTimelineへ作らない。Resource LeaseはActor公開前の全件取得、構築失敗時の逆順rollback、Final交換／連続切断／Generation Reject／Timeout後のShape除去と物理ステップ完了、最後の参照後のGeometry破棄を試し、use-after-free、二重返却、Lease leakがないことを確認する。

T-091の公開後Fault試験では、2個以上のProvisional Actorへ非finite pose／速度、線速度上限一致と1 ULP超過、角速度上限一致と1 ULP超過、Constraint破断を個別および同時に注入する。Primary Faultが`NonFinite > Constraint > LinearVelocity > AngularVelocity`、同順位は最小LogicalFragmentLocalIdで一意に決まり、原因ReasonをContainment結果で上書きしないことを確認する。正常Fixed Stepごとに非公開Slotへ全Actorを収集し、同一FixedStepId、同一世代、ActorCount、LogicalFragmentLocalId順、finite性を全検証した後だけ公開Slotが切り替わることを確認する。Actor 1件目更新後、途中Actor更新中、検証後atomic切替直前にFault／世代変更／Actor集合変更を注入し、Stagingが破棄されて旧完全Group Snapshotだけが復元に使われ、現Step値や異なるFixed StepのSibling poseが混在しないことを検査する。Fault時はGroup全体だけが`ProvisionalFaultFrozen`へ一度遷移し、旧完全Snapshot復元はDisposition 1、Snapshot不在のScene除外は2、封じ込め事前検証失敗のScene除外は3となる。全Constraint解除、全Actor Kinematic、速度／角速度0、Force／Torque消去を検査し、Snapshot欠落、世代不一致、封じ込め事前検証失敗では部分FreezeせずGroup全Actor／ShapeをSceneから除外する。Fault後にFinal cook完了、後続切断、Trace enqueue失敗を発生させてもDynamic復帰、Final物理Commit、旧Group rollback、二重SafetyFrozen Event、Lease早期返却がなく、表示Mesh背景Commitだけが継続できることを検査する。Snapshot値を表示補間や表示―物理誤差収束へ使用する実装は不合格とする。

T-081では、`EarlyFixtureSelectionProfile`、Report、Licensed Dataset Indexのcanonical property列にSolid専用propertyが存在しないことをGolden bytesで固定する。`Tier=Solid`、`ParentTier=Solid`、`VolumeError`、`SolidGate`、`SelfIntersectionCandidatePairCount`、`SelfIntersectionCount`を含む入力は未知値／未知propertyとしてRejectする。`ConvexBuild`は4つの親propertyから同じSourceのRender親を一意に復元し、親QualityClassと`BlindNonManifoldFill`由来の`BenchmarkOnly`を子へ伝播する。直接生成Convexは`GeometryProcessMode=Original`かつ親property全nullとする。Early Fixture canonical schema v1は未実装の初期正本を本記述で置換したものとして最初のGolden Fixtureを作成し、1件でもv1 artifactを生成した後の変更ではSchemaVersionを上げて旧v1 LoaderとGolden Fixtureを維持する。

T-081ではSource Catalog v1についても、root property順、2種のEligibilityRuleId、Entry property順、`Phase02Eligibility`／`ScopeReason`のRule別全許可組合せを検査する。general RuleでのScope指定、Poly Pro RuleでのBuildingの`EligibleGeneral`、Building以外のScope指定、Eligible EntryのReport欠落、Excluded EntryのVariant／Attempt／Geometry Reject混入、Catalog全数・Eligible数・理由別除外数の不一致をRejectする。Eligibilityまたは理由だけを変更した場合もCatalog bytes、Source Bundle hash、Report参照、Receiptが連鎖して変わり、古い処理結果へ結合できないことを確認する。Source Triangle Bandは100／500／1,000／2,000／5,000／10,000の各境界値と直前／直後を再計算し、旧`Over5000`解釈を受理しない。

## 15. 実装ロードマップ

| 段階 | 焦点 | 主要成果物 | 完了条件 |
| --- | --- | --- | --- |
| Phase 0 | 非VR基盤・観測 | Unity 6.3 LTS 6000.3.22f1、Universal 3D／URP、Repo・ignore・Package Lock、固定テスト、Editor更新手順、入力抽象化、WorldPhysicsProfile、ProfilerMarker、Flow、TraceLogger、最小タイムライン、FrameId同期のUnity選択的キャプチャ、CaptureFrameDraft／CaptureDraftRunContext／Factory／Registry、Draft状態／Drop tombstone、append-only Drop Reason、Freeze Barrier／通常領域とterminal専用reserve／AwaitingFreezeTerminal、Draft対応Submission／Scheduler／readback completion、OS lock／二相Run root marker、Run専用Durable PNG Staging Store、CaptureFrameDraftFinalizer、canonical CapturePublicationPlan／path-safe bounded Loader、永続Capture Index／tmp Recovery、FrozenRunPublicationCoordinator、Summary付きExport Snapshot、Trace／Capture二段階公開と再試行Recovery、T-019／T-020／T-082 | 固定Editor版から非VRで再現可能な性能基準、重力Profile、Work Item／Job時系列、対応画像を取得する。ライブCaptureは最終Manifestを要求せずDraftとPNG stagingまで進む。受付停止後にin-flightをdrainしてproducerを静止し、通常FIFOを通常領域へ完全Drainしてから、強制Drop／RingFrozenだけを専用reserveへ直接追記してRecorderをFrozenにする。freeze時にPendingを残さずterminal TraceをFrozen列へ含める。Stagedだけを既存CaptureFrameRecordへ原子的に昇格し、Droppedは期待集合から除く。TestRunIdでRun rootを導出し、OS lockと相互binding markerで排他的に初期化／Recoveryして、PlanとstagingをTraceより先にdurable確定する。Trace bundle公開前失敗では同じFrozen Runを再構築し、公開後の一部Artifact失敗では最終Manifestを変えず、片側公開も含め欠落fileだけ再試行する。全期待CaptureFrameIdのPNG／sidecar検証後に永続`capture.index`を確定して初めてCaptureCompleteとなり、一時worktreeで更新・復帰手順も確認する |
| Phase 0.1 | Capture非同期化 | Phase 0で完成したPNG＋JSON形式と`PngJsonCaptureEvidenceBackend`を維持し、固定Unity版でthread-safeと規定された現行`ImageConversion.EncodeNativeArrayToPNG`をWorkerから使う単一路線とする。PNG encode、canonical JSON、hash、durable stagingを固定容量Workerへ移し、Main Thread PNG Fallback、実行時Capability分岐、別PNG libraryを実装しない。Main Thread上の`TryCollect*`は固定容量Completion経路の軽量pollと正式状態遷移への反映だけを行い、final publication、Recovery、CaptureComplete、cleanupは既存Coordinatorの責務を維持する | Tier A通常CIの数Frame固定FixtureだけでWorker受付、Completion Queue、Drain／Join、Worker例外、Main Thread Fallbackなし、Backpressure／Drop、FrameId／Trace相関、既存Loader／Verifier互換を検査する。Worker出力がlosslessに同じRGBA pixel、寸法、orientationを復元しcanonical JSONを維持することを確認するが、PNG圧縮bytes／hashのMain Thread時代との一致、大解像度、多数Frame、長時間I/Oまたは実時間cadenceを要求しない。Worker encode失敗時はMain Threadで再試行せず該当Captureを失敗終端し、ゲームを継続する。所有権、Completion順序、Drain／Join、Freeze、final Publication／Recovery契約を維持し、過負荷時はCaptureだけをBackpressureまたはDropする |
| Phase 0.11 | 最小NVENC bounded chunk Backend | D-137～D-142の`NvencBringUpProfileV1`とWindows／NVIDIA／D3D11 WDDM固定の`NvencCaptureEvidenceBackend`をPhase 0.1の非同期境界へ追加する。Tier Aは数Frame、fake clock／Fence／completion／Publication Service、小payloadでlifecycleとfaultを決定論的に検査する。Tier Bは依存範囲変更時の短い実NVENC結合、Tier Cだけを30fps／120 tick／4秒提出窓／30秒Finalizationのhardware-qualified nominal、Tier Dを実障害診断とする。GPU TextureからNV12変換してNVENCへ渡し、PNG用RGBA CPU readbackを経由しない。Main／Render ThreadでGPU／NVENC完了、bitstream、hash、I/Oを待たず、固定2 WorkerがAccepted FIFO順にNVENC submit、Output回収、単一Run chunk appendを行う。任意順Completionのreorder状態を持たない | Tier C nominalは120件すべてのAccepted／Frame Completion、1件の確定chunk Artifact、120件のFrame Relation、Contextの`Finalized` terminal result、局所Registry slotの`Registered -> Committed`、Plan commit、Publication、CaptureCompleteを要求する。Submit Worker drain／join後、Output Workerが全Frame Completionを回収し、停止前にchunk単位でhash確定、close、rename、terminal result生成を各1回行ってからjoinする。Fresh Publicationは専用trusted内部経路でstaging全hashを省略し、固定memoryでfinal全hashを1回だけ行い、Publish Receiptを同一CaptureCompleteへ再利用する。Recoveryは同Receiptを信頼せずstreaming再検証する。`Abandoned`または既知pre-commit失敗では通常Plan／CaptureCompleteへ進まず`Incomplete`、rename結果不明ではfileを変更せず旧process内の`CommitOutcomeUnknown`とし、Service静止後にLeaseを解放して新processのfile集合分類へ委ねる。H.264 High、IDR-only、SPS／PPS反復、P／B FrameとFrame間参照なし、CQP 28を要求設定として記録し、raw bytesを変更しない。`Flush(true)`は要求しない。0 Frame、未確定chunkまたは順序違反chunkは全体を破棄してよい。Tier Cはclean Decoder process 1回で確定chunk全体の120 Frame decode／寸法をstreaming確認し、画素比較は先頭／中央／末尾だけとする。固定2 Workerで30fpsを満たせないかWDDM非同期順序契約を維持できなければreorder／同期Fallbackへ移らずUnsupportedとする。Render callbackはboundedな登録だけで戻る。固定Surface／Work／Completion／chunk容量枯渇はBackpressureまたはCapture失敗終端、Run開始前のverification buffer構成不能はUnsupported、commit前の既知buffer不足はIncomplete、commit後またはRecovery中のbuffer不足はfileを変更しないdeferred経路へ固定する。このbounded chunk形式を連続運用へ昇格しない |
| Phase 0.2 | 早期Licensed Fixture選抜＋合成Watertight Fixture | 固定版Portable Blender最小Bootstrap、Source FBX列挙、Building人力Scope Catalog、共通簡易Preset、`BoundaryLoopFill`／`BlindNonManifoldFill`、`EarlyFixtureSelectionProfile`、`EarlyFixtureSourceCatalog`、Source／Script／Preset `CanonicalBundleIndex`と完全tree Verifier、Launch／Bootstrap／Import／BoundaryFill Stage、Licensed Render／Convex Gate、Original／Tri100／Tri500／Tri1000／Tri2000／Tri5000／Tri10000、Voxel64／128／256と限定Post-Decimate、別系統の`Synthetic Watertight Test Fixture` Generator／Validator、ZantetsuCanonicalGeometry v1 Encoder／Decoder／Numeric Kernel／ZCG後Gate、`EarlyFixtureSelectionReport`、`LicensedRepresentativeDatasetIndex`、`LicensedFixtureSelectionReceipt`、非公開Geometry Dataset、T-078／T-079／T-080／T-081 | Import前にSource母集合、Phase 0.2 Eligibility、カテゴリ、file hashをCatalogへ固定し、Poly Pro Universe Buildingは人間が処理前に選んだ豆腐型だけを投入する。多数のEligibleモデルを手修正・Asset別Recipeなしで一括処理し、少数のLicensed Render／Convex Fixtureを再現選抜する。Originalを上書きせず、Boundary Fill／Direct Reduction／Voxel Variantを独立生成し、TargetではなくActual Triangle数を正本とする。採用候補をZCG v1へ決定的serialize／decodeして各Gateを再実行し、Report／Index／Receiptのhashを固定する。Cap Loop、反復切断、Stable Fragment Meshの既知正解は、プログラムまたは固定Blenderスクリプトで生成したSynthetic Watertight Test Fixtureから得る。これをLicensed Report／Datasetへ混在させず、実AssetからのWatertight／Strict Solid生成成功、全Building／全Asset互換率、Phase 5.5の製品能力を主張しない |
| Phase 0.25 | Cook比較Probe | 公開合成Convex Dataset、Phase 0.2の非公開LicensedRepresentative Convex補助Dataset、U1 Unity BakeMesh Harness、N1／N2／N3 Native PhysX Harness、工程別Timer、Repository外のManifest／Result／Suite Index Bundle、結果レポート | 製品Geometry完成前の早期Probeとして、同一入力でUnity経路の実費用とNative改善上限をP50／P95／P99まで再現測定でき、N1／N2／N3の必須Stage差、版・設定差、Manifestと実測Resultのhash対応を記録できる。合成Datasetをcanonical正本とし、LicensedRepresentativeは実Asset傾向の補助確認に限定する。T-076の前提とはせず、Native PhysXを製品Runtime依存にはしない |
| Phase 0.5 | XRスモークテスト | OpenXR、Quest 3S有線Link、Grip Pose、Tracking State、GripToKatanaOffset、Single Pass | 空シーンで両眼90Hzと左右の刀姿勢・追跡復帰を確認 |
| Phase 1 | 即時切断／Dispatch境界 | `NoFixedSupport`と明示されたテスト対象、公開合成MeshとPhase 0.2の非公開Render Fixture、共通切断入力、単一clip、分離オフセット、簡易断面、ヒット演出、`EvaluationWorkItem`／`WorkToken`／`FutureEvaluationDispatcherV1` API、固定容量QueueとCritical予約枠、T-090、事前Shard済み専用テストMeshによるVertex Pulling／Indirect Batch描画性能PoC、VFX Graph汎用Fallback | 非VR入力で、固定支持を持たないと明示した箱と選抜済みSynty代表プロップに即時の隙間を表示する。支持属性が不明な対象や地面・壁・基礎へ固定された対象は切断対象へ入れない。任意切断由来の微小Fragment判定やclip＋ポリゴン崩壊は行わず、全Fragmentを通常の塊としてclip表示する。事前Shard済み専用Meshだけを通常数千Triangle・少数Drawで描画する。合成Work ItemでV1の固定順、容量、予約枠、取消、Completionを先に固定し、後続PhaseがQueue実装型へ依存しないことを確認する |
| Phase 1.5 | 固定支持Topology | `FixedSupportAnchor`、Node／Edge、`LogicalFragment`、`LogicalCutOperation`、`CutBoundaryRecord`、Support／Exposure／Geometry／Work Result状態軸、三値`OperationSupportState`、`FullyFixedCullInvalidated`、`PendingSupportClassification`、Support→Exposure決定表、全LogicalFragment→FragmentGroup物理状態集約、LogicalCutOperation構築Validator、Anchor到達性、Anchor／SupportGraph世代、Commit検証、純粋C#単体テスト、Operation作成／Link／状態遷移／Cull失効／Rejectの支持Trace契約 | 手書き／合成FixtureでT-074を満たし、Collider切断やcookなしで境界ごとのDormant／Active／Suppressed分類、操作ごとのIncomplete／FullyFixed／HasDetached集約、後続切断時のCull先行失効、複数境界混在時のGroup物理状態、分類不能時の物理完全維持と既知Active境界の描画、補助Dormant Cap、再分類遷移、全履歴面の再評価、世代不一致／不正Operationの原子的Reject、保守的Fallbackと固定TraceからのOperation復元を決定論的に再現できる。完了後に固定支持対象を切断対象へ追加する |
| Phase 2 | 仮断面・影強化 | Oriented Closed Cut Shell、`OrientedShellValidator`、UniformWindingSignCertificate、StencilPolarity正規化、MaxAbsoluteWindingBound、変更Edge／Capの局所有向incidence／Bound継承検証、ゼロKerf、Dormant Cut再有効化、`LogicalCutOperation`、三値`OperationSupportState`、`FullyFixedCullInvalidated`、`ActiveTemporaryBoundarySet`／`TemporaryRenderCapRecordSet`、Fully Fixed Cut Operation Cull、実Fragment Mesh早期公開、Ready中の表示継続と原子的Geometry Commit、OBB交差Cap Bounds Polygon、両眼Frustum／Facing Cull、Front／Back相殺とResidual Stencil Support検証、Polarity対応CapCompatibilityKey／互換Group、StencilCountBatch／Color内255 Count予算、可視Cap Bounds競合判定、専用8bit IncrementWrap／DecrementWrap Winding Stencil、左右眼Stencil Conflict Graph／Greedy Coloring、Color単位Volume／Cap Batch、`TemporaryClipConstraintCandidateSet`、`SV_ClipDistance` 8面＋PS `clip()` 4面＋Renderer-only overflow無視、共通トゥーンの粘土色グレー、処理経路デバッグ色、ShadowCaster用同一Hybrid Clip／Offset、Stable片面／Pending両面Batch、XR両眼対応、Pending Cut／Stable履歴管理、T-067／T-084／T-089 | 2～4連続切断と複数対象の画面重複でStencilが混入せず、意味上のActive境界集合と実際の描画Cap集合を分離できる。Stencil Cut Shellは前処理時の線形全体検証後、ランタイムに全Mesh self-intersection／inside-outside／再Watertight検査を追加せず、既存切断Kernelの局所Commit検証だけで閉鎖性とBoundを継承する。skinning後self-intersection、均衡Non-manifold、重複／Coincidentを非ゼロWinding semanticsで受理し、未相殺Boundary、共有Edge crackをStencil省略または安全なFallbackへ送る。Uniform Sign証明を持ちPositiveへ正規化済みのShellだけを共有Groupへ入れ、UnknownはShell固有Groupへ隔離する。既知Boundを255以下のStencilCountBatchへ分割し、Siblingを別Colorへ配置する。単独超過／Unknown Bound／専用8bit非確保はStencil仮Capを省略してclip表示と実Mesh完成を優先し、Saturateや部分Bit Counterを使用しない。補助Dormant Capを描画コストと実Cap 2～4枚上限へ数え、Ready到達だけでは表示を戻さず、実Mesh適用とCommitted遷移が同じ描画フレーム境界で成功した後だけ対応Recordを外す。RenderFragmentごとの候補資格をOperationSupportState／FullyFixedCullEligibleと一致させ、候補面は古い未Commit祖先制約を優先するdependency-closedなstable順で全Pass／両眼へ共有し、8面をRaster、続く4面をPixelで処理する。Exposure、Support、Cull失効、Fragment対応の遷移でも同一フレームに再構築する。超過した後発面は即時Stencil VolumeをsubmitせずCap板と論理／背景処理をそのまま残し、祖先外GeometryやSiblingを復活させず固定GPU処理量のままStable Geometryへ収束する。失効していないFullyFixedは子数にかかわらず大断面の即時Stencil仕事を発生させず、HasDetachedまたは失効済みでは全非Suppressed Cap、Incompleteでは既知Active Capだけを描く。後続切断では祖先OperationのCullを境界Active化より先に不可逆失効させる。Cap pair／Coverage探索、Cap単位Buffer compaction、Mesh部分更新を行わず、Geometry Commit後もCutBoundaryRecord、LogicalCutOperation、Cull失効履歴、支持履歴を残す。許容する線状亀裂／局所Z-fightingと禁止する面状Z-fightingを区別し、Detached化した瞬間に過去断面を欠落なく再表示する。OBBが重なってもCap非交差なら安全にBatchされ、互換Groupは統合され、両眼不可視またはCull EligibleなFullyFixed操作は欠落や点滅なく除外される。相殺不能入力はFallbackし、Shadow MapではStencil Capなしの影近似が許容範囲に収まる |
| Phase 3 | 表示ジオメトリ | Job＋Burst三角形切断、Count／Write Job、ReadOnly／Writable MeshData、`RenderCutTopologyMap`、Topology系譜の交点共有／Contour Track、共通signed-distance分類、Simple Contour Fast Path、局所2D Arrangement、Boundary Fan／Open Chain／Degenerate fallback、cut-local閉鎖検証、RenderFragment接続成分、Triangle数／面積／任意体積／重要度Metadata、後続Debris Corner Stream生成用出力、メインスレッドMesh公開、世代Commit、V1 Dispatcher Class 2接続、T-083 | 仮表示から実Meshへ無停止で置換し、重い頂点処理がMain Threadへ戻らない。公開のSynthetic Watertight／異常系Fixtureと選抜済みLicensed Render Fixtureで、skinning後self-intersection、duplicate／coincident face、nested shell、winding不整合、局所non-manifold、既存boundary、cut固有退化を含む切断由来Boundaryを塞ぐ。位置近傍だけで別Topology Trackを誤接続せず、全Mesh self-intersection／inside-outside／shell分類を同期前処理しない。任意切断由来Fragmentは物理Convex対応が確定するまで塊として表示され、Phase 3だけでは大きさを理由にデブリ化せず、clip中の表面Triangle崩壊を起こさない |
| Phase 4 | 物理 | 全体0.5G仮設定、FragmentGroup、PendingPhysicsSplit／PendingSupportClassification／PendingAnchoredSplit／ProvisionalPhysicsSplit／ProvisionalAnchoredSplit、全LogicalFragmentの物理状態集約、Phase 1.5支持モデルとの接続、分類不能時の旧物理完全維持とTimeout Fallback、Active境界描画とGroup運動の分離、固定側Impulse禁止、自由側解析仮運動、旧Cooked Convex Resource Lease、Provisional Actor／Shape／Separation Constraint、全外界Collision／Sibling抑止、OBB Provisional質量配分、CanonicalMassBudget、FragmentRenderAnchor初回分裂／物理優先Final handoff、`ClosedCutComponentSet`／`CutConnectivityGraph`、SurfaceAdjacency／AttachmentPatch、Graph connected-components、Native Convex B-rep、Compound内Overlap許容、Count／Write／Validation Job、RenderFragment／LogicalConvexFragment対応グラフ、近似被覆、Represented／Missing／Shared／Ambiguous、SharedResolutionRole、bounded GJKによるShared凸包単一平面判定、SharedConvexResolutionOutcome、cook前デブリ判定、Temporary Low-Poly Proxy生成Kernel／Validation／Fallback、Runtime Debris Geometry Arenaと後追いGPU崩壊、Job化`Physics.BakeMesh`、Fast Cook初回分裂、選択的Fast Simulation再Bake、Sibling Collider一時衝突抑止、別Mesh差し替え、Upgrade Scheduler、`PhysicsConvexMassWeight`、Convex由来の体積／重心／慣性とOBB／AABB Fallback、質量保存、速度継承、Generation Reject、Timeout品質低下、保守的な仮予算管理、Phase 0.2 Convex Fixture回帰、T-063／T-070／T-075／T-077／T-085／T-086／T-091との差分再確認 | cook遅延中も既知Active境界の即時表示を維持し、支持既知かつ容量内では再cookなしのProvisional Rigidbodyへ原子的に分裂して外界Collisionと連続運動を先行する。分類不能時は旧物理とGroup運動を変えない。交差する独立ComponentをUnionせず、凹Componentの3個以上の子もGraph成分として決める。1 Render対複数専有Convexを正常にRepresentedとし、物理表現不能な小Fragmentだけをデブリ化する。重複Compoundでも生体積を質量として二重計上せず、Weight継承で切断前後の質量和を保存する。大型・重要・Ambiguous、明確なKeeperのないSharedはまず共有物理GroupとしてCommitし、2集合の凸包をstrictに分ける単一平面を全頂点検証できた場合だけ後追い分割する。凸包交差／包含、不確実、予算超過、検証失敗では共有Colliderによる余分な被覆と空中浮遊を許容して同世代の再試行を止める。再切断は古い精密化を待たず、現行共有B-repから新世代を作る。Temporary Proxyの実装済み品質段階がT-077を通り、不正入力は下位Fallbackへ移る。T-076前はSchedule数、Worker占有、Batch、同時Bake、Nativeメモリへ保守的な仮上限を設定し、Arena不足でも待機・再確保しない。分類後は固定側を動かさず自由側だけを安全に分離する。公開合成Fixtureと選抜済みLicensed Convex Fixtureの両方で、Graph分割／Convex分割／質量特性／BakeがMain Threadを停止させず、二段階Colliderを安全に昇格する。Unity経路が要件を満たす限り維持し、満たさない場合だけD-086のGateを評価する |
| Phase 4.1 | Geometry／Cook性能Baseline | 固定合成Dataset、Phase 0.2 LicensedRepresentative補助Dataset、Single-Thread Kernel Harness、Job Batch Harness、表示Mesh／Convex／T-077検証済みTemporary Proxy／Bake工程Timer、Repository外のManifest／Result／Suite Index Bundle、P95／P99容量式 | Phase 3／4の正しい製品実装をT-076に従い、公開合成Datasetをcanonical正本、選抜済みLicensed Fixtureを別の非公開補助Suiteとして測定する。各DatasetCaseIdの固定規模軸とSamplesをjoinしてKernel単発µs、Bake／Commit単発Latency、定常Throughput、Job End-to-End latencyを再現する。Suite内DatasetId→DatasetContentSha256一意性、Target×Stage×ExecutionMode、FailureRate／Rejected契約、bounded Manifest／Result／Index Loaderを検証し、Phase 4の保守的仮上限を校正する。O-035／O-039の初期確定予算と斬撃波Deadlineまでに処理可能な対象数を根拠付きで決め、T-070の早期結果を再解釈できる |
| Phase 4.2 | 大型構造物安全制約／Player非接触 | `LargeStructuralProp`、`StructuralSlabComponent`、Ground Root、`SafetyTetherTree`／Edge／Level、切断面OBB／Convex Patch Anchor、決定論的Spanning Tree、相対並進Limit、World回転Limit、`StructuralSplitGeneration`、Sibling衝突抑止、`SafetyFrozen`、Player Layer非接触、`PlayerLocomotionOccupancy`、Near-Wall Fade、T-087／T-088 | 4面建物を2回以上切断しても全大型動的Fragmentが循環なしでGround Rootへ到達し、下側の移動へ上側が追従して累積移動・回転上限を守る。Tree不成立を自由落下の部分Commitで隠さず旧Group維持またはSafetyFrozenへ送る。Playerは物体へImpulseを与えず、簡易Occupancyでモデル化済み大型物体への人工移動侵入を抑えながら刀／斬撃波で切断できる。視界保護はbest-effortとし、非干渉物体のCamera被りと即時StencilのCamera-inside破綻を許容する。押し戻し対一方向退出は計測して未決事項へ根拠を残す |
| Phase 4.5 | 飛翔斬撃と未来評価 | Gesture状態機械、Edge Direction Gate、Recovery、NonCutting素通り、Slash Latch、Span／Travel Axis、単調・一価SlashFront、逆行／自己交差Finalized、前縁VFX、帯状Sweep、Candidate Flight Bounds、評価DAG、V1 DispatcherへのReady投入、先行切断、Commit検証 | 復路とU字軌道で二重前縁や誤斬撃を作らず、Latch直後から三日月前縁が飛翔・命中し、Extending中も前縁が成長しながら進み、遠距離対象の多くが接触時に完成Meshへ即移行する。DAGはDispatcher内部表現へ依存せず、Schedule前取消と世代RejectでV1へ接続する |
| Phase 4.6 | 予測拡張 | 局所PhysicsScene、対象Stepへ解決済みの`ExplicitAnimationStateV1`、Loop／Clamp Clip Catalog、`ResolvedAnimationPoseInput`／`FutureAnimationPoseEvaluator`境界、controllerなしPlayable／Pose Table比較Probe、random access未来Rig Pose、Asset／Evaluator Identity、信頼度別フォールバック、T-018 | AnimatorController rolloutなしで任意`FixedStepId`のPoseを評価し、現在表示と未来評価が同じ明示StateとCatalogを消費する。PlayableとPose Tableの代表骨誤差・Main Thread／Job費用を比較でき、Mode／duration／Identity不一致では実姿勢Fallbackへ移る。V1予測対象のプロシージャルIKと左右反転は双方で無効 |
| Phase 4.7 | モブ未来計画 | `MobTrajectoryKernelV1`、Waypoint／Lane Desired Motion、FixedStep同期二相更新、固定長Trajectory Queue、Near Live／Mid・Far Playback、MobPlan／PlanGeneration、`AnimationPlannerV1`、Rootと同一epochの`ExplicitAnimationStateV1`全体、粗い全Plan／Group無効化、V1 Dispatcher背景補充、Trace、T-092。ORCA／Chunk／依存Graph／Flow Field／Pose Layer／Mirrorは成立条件外 | 同じKernelが現在更新と未来RootTrajectoryを生成し、同じAnimation Plannerが現在／未来State全体を生成する。介入なしのMid／Farモブで計画再利用率と先行切断完了率が基準を満たし、介入時は旧Generationを安全に無効化する。Queue枯渇・固定容量超過でもState全体のHoldを許容してMain Thread Spikeや古いPose／切断Commitを起こさず、モブ同士の多少の重なり、IK／左右反転なし、V1 Clip hard switchのPose popを初期品質として許容する |
| Phase 4.8 | OpenXR Projection Capture＋正式録画判断 | `OpenXrProjectionCaptureProfileV1`、Windows API Layer、D3D11固定、SDR、MSAAなし、Dynamic Resolutionなし、Single Pass、Projection 1枚、左眼45fps、Release前GPU Copy、固定Profile検証、GPU Encode、Capture Record／Run Manifest同期。Phase 0.11の120 Frame上限を外す候補ではRegistry／Publication Planの計算量、正式chunk長、GOP／Container／segment、durability頻度、index／seek、保持期間、payload所有権／copy／hash回数、停止時Publication時間、詳細画質評価を追加する | 切断PoCの異常をProjection画像とTraceで再現調査でき、想定外構成はFail Fastし、非録画時との差が性能予算内。連続録画を採用する場合はPhase 0.11のbounded Run chunk形式を暗黙流用せず、T-054実測後の正式形式で容量・停止時間を満たす。不要ならAPI Layerまたは連続録画の導入を個別に見送れる |
| Phase 5 | 人形 | 共通Pose Evaluator出力、命中時実Bone Poseスナップショット、CPUスキニング、骨proxy分類、物理移行 | 基本動作中のNPCを任意方向に切断し、予測Pose不一致時もAnimator内部Stateへ巻き戻さず実際の表示Poseから後追い処理へ移る |
| Phase 5.5 | Asset自動前処理 | Phase 0.2の選抜Report／失敗例を入力に、完全なPortable Blender Manifest／Bootstrap、固定版ヘッドレス実行、Asset別Recipe、Render Mesh用`RenderCutTopologyMap`、`ClosedCutComponentSet`、SurfaceAdjacency／AttachmentPatch／1～8件のTopology Anchor付きAttachmentLink、Stencil Cut Shell Base用Topology／`OrientedShellValidator`／UniformWindingSignCertificate／StencilPolarity／MaxAbsoluteWindingBound証明、Component単位の開放修復、見た目を保つReduction、UV／Material再構成、VisualOnlyMicro／PhysicsSignificantAttachment分類、AttachmentId／Anchor／対象Triangle／ShardId生成、実Asset用FixedSupportGraph生成、Compound Physics Proxy／finite正和MassWeight／Debris Atlas生成、検証、キャッシュを実装する | Phase 0.2でRejectした複雑Assetも対象に含め、古いシステム版と共存しながら代表家具・車・建物を別PCでもGUIなしで再現生成する。相互に食い込む閉ComponentをBoolean Unionせず標準経路へ通し、接続はParent関係ではなくAttachment Link付きGraphへ固定する。各Link EndpointをTopology系譜へ追跡し、共通epsilonの完全決定表で同側Linkだけを残す。Render MeshはFBX control point／Import topologyからattribute seamを越える安定IDとNon-manifold fan／lane hintを生成し、skinning後も位置だけを更新してD-117へ渡す。Stencil Cut Shell Baseは同じTopology Vertexからcanonical posed positionを生成し、自己交差検出なしの線形有向incidence GateでD-118へ渡す。Uniform Sign証明を持つTopology Componentだけをsigned volumeでPositive正規化し、未証明PolarityまたはWinding BoundをUnknownとして保存する。VisualOnlyMicroには専用Convexを作らず、重要部品だけをPhysicsSignificantAttachmentとしてCompoundへ含める。製品用Strict Solidを生成・検証・Fallbackせず、代表Assetでの成功を完了条件にしない。Phase 1.5の合成Fixtureを実Asset由来Graphへ置き換えて同じ契約テストを通し、Phase 0.2より広いAsset範囲と製品品質を達成する |
| Phase 6 | コンテンツ | Synty City街区、10プロップ、シェーダ統一、既製モーション | 垂直スライスとして一連の遊びが成立 |
| Phase 7 | 実測後最適化 | 端末別品質、破片LOD、V1 Dispatcher Counter／T-076結果に基づく必要最小限のV2候補、遠距離確定、ストレス試験 | ターゲット実機で性能予算を満たす。費用学習、aging、動的優先度、work stealing等は実測で必要性が示されたものだけを追加し、不要ならV1を維持する |

Phase 4のcook待ち標準経路はD-132のProvisional Rigidbodyとし、支持分類後のActor／Shape／Constraint生成、旧Cooked Geometry共有、OBB質量近似、外界Collision、物理Actor pose／速度不変のFinal handoff、由来Convex half-space包含検証をT-091まで実装する。D-068の単一Rigidbody／旧Collider FragmentGroupはUnknown、固定容量超過、Backend共有不可、原子的構築失敗時だけの保守Fallbackとして残す。Provisional専用再cook、部分Actor公開、同期cook、Final Commit時の物理Actor pose／velocity補正はPhase 4完了条件に含めず、表示側の瞬間的な追従差は許容する。

Phase 4では公開前Fallbackと公開後Faultを別経路として実装する。公開前失敗は実状態を変えず`ProvisionalPhysicsFallbackActivated`へ要求種別だけを記録し、公開後異常は固定容量の直前finite物理Snapshotを用いてGroup全体を`ProvisionalFaultFrozen`へ不可逆遷移させる。後者は物理安全Classで次のDispatch対象とし、部分Freeze、旧Group rollback、自動Dynamic復帰、Final物理Commitを行わない。

Phase 5.5の建物Recipeでは、外周Structural Slab候補、入口回避用少数Compound Box、下端両側Ground Anchor、外周角Attachment Link、VisualOnlyMicro／PhysicsSignificantAttachment分類、Safety Tether用Patch／Topology系譜Metadataを生成する。Phase 0.2では共通Presetで成功した少数のStructuralSlabCandidateだけをT-087 Fixtureへ固定し、製品品質や全建物対応を要求しない。

## 16. 垂直スライス受け入れ基準

- 刀の高速移動でも代表プロップを安定して切断できる。

- `Active`境界では斬撃直後からclipと仮断面が両眼で一致する。FragmentGroupが`PendingSupportClassification`でなければ許可された側が離れて見え、同状態ならGroup運動を止めたまま切断線と仮断面だけを表示する。`Dormant`境界は単独では表示を要求せず相対移動もしないが、HasDetached／Cull失効済みOperationでは補助Capとして描画され得る。`Suppressed`境界は再分類まで即時切断表示と運動を起動しない。

- 通常断面は全体と同じトゥーン陰影の粘土色グレーで統一され、仮断面から実断面への差し替えで特殊な質感変化が見えない。

- 即時切断物体のShadowはカラー表示と同じclip／分離Offsetに追従し、両面Shadow近似からStable実断面の片面Shadowへ移る際に目立つ影の跳びがない。

- 左右眼の一方だけで非互換な可視Cap Boundsが重なる複数の即時切断対象はStencil Conflict Graphが別Colorへ分け、OBB投影が重なっても両眼の可視Cap Boundsが非交差なら同一Colorへまとめられ、別物体のStencilによる仮断面のはみ出しがない。

- 同じ全切断面とキャップ状態を共有する対象は重なっても同じStencil Colorへ統合され、別々に動いてWorld Planeが変わったフレームでは自動的に別Groupへ分かれる。

- 両眼とも裏向きのCap GroupはStencil処理ごと省略され、片眼だけ可視または切断面近傍では省略されず、頭部微動で仮断面が点滅しない。

- デバッグモードでは赤＝即時仮断面、青＝先行Commit、緑＝命中後計算CommitをTraceと一致して識別でき、Stable後は通常グレーへ戻せる。

- バックグラウンド完成後、表示MeshとColliderが目立つポップや停止なく差し替わる。

- 表示Meshと物理Convexの切断、検証、cookingはJob＋Burst主体で実行され、Main ThreadにはMesh公開とRenderer／Collider／Rigidbodyの境界Commitだけが残り、未完了Jobへの強制`Complete`によるフレーム停止がない。

- 支持分類済みの切断はFinal cookを待たず、旧Cooked Convex Geometryを共有する子別Provisional Rigidbodyへ物理ステップ境界で原子的に移行する。外界Collisionを全て有効、同系譜Siblingだけ無効とし、交差Shape由来の早い接触とGhost Contactを許容する。OBB近似によるProvisional質量和を保存し、Final handoffではActor pose／COM線速度／角速度を変えず、由来Convex内へ収まるFinal Collider／COM／inertiaだけを同一Actorへ置換する。表示の瞬間移動を許容する一方、Colliderのpose補正による新規penetration、二重分離Impulse、同期cookを生じない。Unknown／容量超過／構築失敗、Final包含不能は部分公開せず現有効なProvisionalまたは単一FragmentGroupを維持する。

- 公開済みProvisional Actorの非finite、速度／角速度超過、Constraint runtime破綻はGroup全体を`ProvisionalFaultFrozen`へ一度だけ遷移させ、直前finite物理姿勢で全ActorをKinematic化するか全Actor／ShapeをSceneから除外する。部分Freeze、Snapshotの表示補間利用、自動復帰、Fault後のFinal物理Commitを行わず、Trace失敗でも安全状態をrollbackしない。

- 表示Mesh、Convex、T-077検証済みTemporary Low-Poly Proxy、cookの固定DatasetベンチマークがRelease／Burst環境で再現でき、Single-Thread µs/op、Job定常Throughput、End-to-End P95／P99からWorker予算、同時切断数、Batch Size、同時Bake数を説明できる。単一DatasetCaseIdの規模軸、工程別Stage、許可されたExecutionMode、Manifest／Result hash、Samples／Aggregate件数をSuite Indexから検証でき、同じManifestへのResult差し替えを拒否する。同一Suiteでは各DatasetIdが厳密に1つのDatasetContentSha256へ対応し、異なるhashの系列を容量式へ混在させない。Manifest／Result／Index Loaderはそれぞれ64 KiB、64 MiB／100万Sample、64 MiB／10万Entryのschema上限と呼び出し側のより小さい上限を配列確保前に強制する。対象処理の失敗をFailureRateへ残し、計測不能な試行だけをRejectedとする。既存TraceRunManifest／bundleのCodecとGolden Hashは変化しない。

- Unity `Physics.BakeMesh`とNative PhysX比較Probeの入力、版、設定、工程別結果が再現可能に保存され、倍率差だけを理由にNative Backendが製品へ混入しない。Native再検討時はD-086のGateを満たした証拠を残す。

- 処理中に再切断しても、古いジョブ結果で形状が巻き戻らない。

- NPCを移動中に切断し、姿勢固定から剛体破片への移行が成立する。

- 代表的な連続切断シナリオで目標フレームレートとメモリ予算を満たす。

- Phase 0.2ではImport前CatalogとSource／Script／Preset Bundle Indexから母集合、Eligibility、カテゴリ、入力file、Script、Presetを再現し、Launch／Bootstrap／Import失敗を正しいStageへ残す。Poly Pro Universe Buildingは処理前に固定した豆腐型だけをEligibleとし、Scope外をGeometry失敗へ数えない。多数のEligibleモデルから個別修理なしでLicensed Render／Convex Fixtureを少数選抜し、BoundaryLoopFill／BlindNonManifoldFill、Original／Direct／Voxel、Target／Actual、NoOp／Alias、Resource状態、全Attempt、Reject、Geometry hashをReport／Index／Receiptから復元する。Licensed Assetのwatertight／Strict Solid成功を要求せず、Cap Loop等は別のSynthetic Watertight Test Fixture Suiteで検証する。選抜済み少数の成功を全Buildingまたは全Asset互換率として扱わず、LicensedRepresentative GeometryとAsset対応表は非公開Repoだけに存在する。

- 10種類のアセットが、Blenderヘッドレス処理によってDisplay／Closed Cut Component／Cut Connectivity Graph／Stencil Cut Shell Base／Compound Physics Proxyの自動またはRecipe駆動工程を通過する。製品用Strict Solidは生成せず、その成功を代表AssetまたはPhase 5.5の合格条件にしない。

- 各Physics Convexは閉凸形状として自己交差、面反転、退化のない検証に合格するが、Compound内の別Convex／Closed Component同士のIntersection／Overlapは許容する。Stencil Cut Shell Baseはfinite、有効index、共有Edge位置一致、有向incidence総和0を線形検証し、自己交差を合否へ含めない。UniformWindingSignCertificateを持つComponentだけがPositiveへ正規化され、未証明ShellはUnknownとして隔離される。既知`MaxAbsoluteWindingBound`から作る各`StencilCountBatch`の`BatchWindingBound`と、同一Colorへ再統合するBatch群のchecked和が255以下で、専用Stencil Byteの全8bitを排他利用できるときだけ8bit Stencilへ投入される。同じGeometryがDisplay、Closed Component、Stencil Cut Shell Baseの複数契約を満たす場合はRuntime Bufferを共有する。全成果物は同一入力・Recipe・Blender版から再現可能に生成される。

- 相互に食い込む部品と凹形状を含む代表Assetで、1切断から3個以上のComponent Fragmentが生じても、SurfaceAdjacencyとTopology Anchor付きAttachmentLinkの完全決定表からLogicalFragment、Fixed Support、Convex対応を再現できる。Endpointが同じ厳密SideのLinkだけをその側へ残し、正負不一致／OnPlane Linkを切断する。Anchor解決不能な非Micro対象では必ずPending、Timeoutでは旧Group維持とし、実装選択を許さない。Graph確定前は旧物理を維持し、親子履歴だけで接続を決めない。同じCut条件を共有する可視Component CapはGeometry Unionなしで同一Stencil互換Groupへ入り、非ゼロMaskとして欠落なく描画される。

- 制約付きSurface ProjectionがVoxel由来の大形状誤差を改善し、誤吸着または自己交差を生じる頂点はVoxel位置へFallbackする。採用／拒否理由とReduction前後の誤差がレポートに残る。

- 小さな欠損、底面欠落、片面シェル、微小隙間を自動修復でき、意味が曖昧な大開口は`NeedsReview`として停止する。

- 切断帯へ触れたMicro Attachmentは即時表示と確定Meshの双方から不可逆に消え、差し替えや古い非同期結果で復活せず、極小Rigidbodyを生成しない。

- Micro Attachment消去時は元部品の実GeometryをShard ClusterとしてGPUだけで飛散・ディザ消滅させ、通常500～3,000 Active Triangleを少数Drawで処理する。連続発生でもGameObject、Collider、Rigidbody、GCを増加させず、左右眼で同じ消滅模様に見え、予算超過時だけ汎用破片または即時消去へ低下する。

- 重複するCompound Physics Convexを含む対象でも、Final物理では生のConvex体積を単純加算せず`PhysicsConvexMassWeight`をLocal ID順binary64左畳みで正規化して、切断前Rigidbodyと全子Fragmentの質量合計を一致させる。Provisional物理もOBB切断近似または等Weightで親Canonical Mass Budgetを保存するが、その近似COM／inertiaをFinal値へ流用しない。全Weight 0／非finiteはFinal Commitせず、各物理Commit対象Fragmentもfiniteかつ正のWeight和を持つ。Weight 0 Convexだけの子はMicro／Debris安全条件を満たす場合だけ質量移送なしで非物理デブリとして消去し、満たさない場合は正WeightのSiblingを含むCut Operation全体を旧物理または有効なProvisional状態のまま維持する。質量0／任意最小質量Rigidbody、部分的なFinal Commit、任意の質量再配分を生成しない。密度1慣性を`assignedMass / convexVolume`でscaleする。表示Mesh／Strict Solid／Convex Boolean UnionをRuntime質量計算へ要求せず、重心・慣性はConvex由来のWeight付き近似または規定のOBB／AABB Fallbackからfiniteかつ正の値を得る。

- 自動修復前後のBounds、体積、表面偏差が記録され、許容値を外れた生成物を採用しない。

- Synty／Poly Pro Universe入力と派生したDisplay／Stencil／Physics Proxyが公開Git履歴、公開CI Artifact、公開キャッシュへ含まれない。

- 飛翔斬撃波の到達時刻と候補列挙が再現可能で、静止対象では接触前の先行切断が安定して成功する。

- 刀を十分に振った時点で切断面と初期SlashFrontが振り終わり前にLatchされ、三日月VFX、前縁Sweep、近距離対象の即時反応が同じフレームから始まる。

- 刃側を先行させる広い角度の振りは切断でき、同じ刀向きの復路・峰側移動ではSlashが発生しない。

- NonCutting、Recovery、追跡無効中の刀が地形、プロップ、NPCへ衝突応答せず完全に素通りする。

- Quest左右コントローラのGrip PoseとBladeFrameが一致し、追跡復帰時に誤Slashを生成しない。

- Latch後に軌道を変えても既確定面、生成済み前縁、命中が巻き戻らず、Extending中の追加辺を含むVFX前縁と衝突時刻が一致する。

- Finalized後は折れ線への追加だけが終了し、完成した三日月前縁が最大距離または寿命まで飛翔・命中判定を継続する。

- U字または明確な折返しを含む刀軌道でも、同一SlashFrontが前後二重や自己交差を作らず、生成済み前縁を保ったまま逆行地点でFinalizedする。

- 予測が外れた場合も即時切断レンダラへ安全にフォールバックし、古い成果物をコミットしない。

- Quest 3Sの有線Quest Link環境で、頭部追従だけでなく剣、切断、破片を含む実アプリの両眼描画が原則90fpsを維持する。

- 任意の`SlashId`から候補検索、予測、各切断Task、検証、Commitまたは破棄までをEditorタイムライン上で追跡できる。

- Nearのライブ更新とMid／Farの計画済み軌道が同じ固定ステップ移動Kernelを共有し、Current／Future表示は同じゲーム側明示Animation Stateを交換可能なPose Evaluatorへ渡す。遠距離モブのRoot軌道とAnimation Stateを切断先行計算へ利用でき、AnimatorController rolloutへ依存しない。プレイヤー介入時は旧`PlanGeneration`の軌道・Rig Pose・切断成果物が適用されず、Queue枯渇時も古い軌道を無期限に再生しない。

- Unity Editor更新時にプロジェクトを作り直さず、専用ブランチで固定テストとXRスモークテストを実行し、不合格なら旧固定版へ復帰できる。

- 不変条件違反時に直前30秒を目安とするTraceが保存され、Editorで再読込して原因系列を調査できる。

- PoCでは選択的な片眼映像または静止画をFrameIdからTraceへ対応付けられ、録画停止時と比較して90fps性能判断を歪めない。

- OpenXR API Layerを有効にした検証では、D3D11固定Capture Profile上でProjection画像と`predictedDisplayTime`、Pose、TestRunId、Slash／Object／Task IDを一意に関連付け、API Layer自身のGPU／CPU負荷も別計測できる。Profile逸脱時はゲームを止めず録画だけをFail Fastし、Run Manifestへ理由と実構成を残す。

- 大型建物は外周Structural Slabと少数Compound Convexで切断でき、同じSlabを2回切ってGround Anchor／角Linkから隔離した中間Fragmentが動的化する。Safety Tether TreeはSupport Graphから独立して地面RootへのPathを保ち、連続切断後も循環、無制限落下、横倒しを発生させず、失敗時は旧Group維持またはSafetyFrozenへ移る。

- Player Body／Handはプロップ／破片へ物理Impulseを与えず、人工移動はモデル化済みPlayerLocomotionOccupancyへ新規侵入しない。HMDの実空間移動ではCamera位置を強制変更せず、簡易Volumeで検出できた場合だけbest-effortの視界保護を行う。未登録または非干渉物体のCamera被り、物体内部視点、即時Cut Shell内での部分Cap／Cap欠落／左右眼差を許容し、それらを切断・物理・Geometry Commit失敗へ昇格しない。刀とSlashFrontによる切断Interactionは非接触化後も成立する。

## 17. Codexでの継続更新ルール

- 決定が変わった場合は既存行を消さず、状態を『廃止』にして代替決定IDを記録する。

- 未決事項は結論、根拠、決定日を追記して決定事項へ移す。

- 技術検証は測定環境、再現手順、数値結果、スクリーンショット／Profiler参照を残す。

- ロードマップのPhase完了条件を満たす前に次Phaseへ進む場合は、既知の負債として記録する。

- 新しい機能提案は『即時応答』『幾何精度』『物理整合』『性能予算』のどれへ影響するかを明記する。

- DOCXを再生成せず、このMarkdownのみを正本として更新する。

> **次の推奨アクション** Phase 0として非VR固定テストと共通切断入力に加え、ProfilerMarker、Flow Event、固定長TraceLogger、最小Editorタイムライン、FrameId付きの選択的静止画／片眼録画を先に用意する。まず公開合成箱で性能基準、完全なWork Item／Job時系列、対応画像を取得する。Phase 0の完了条件を変更せず完了させた後、Phase 0.1で既存Unity PNG EncoderをWorkerから使う単一路線へ移し、encodeからdurable stagingまでをMain Threadで待たない。続いてPhase 0.11でnominal 120 Frameとfault系最大16 tickの最小NVENC Backendを、1 Run 1 bounded chunkとして既存Artifact／Publication／Recovery／CaptureComplete経路へ接続する。Main／Render ThreadではNVENC／GPU完了を待たず、固定Surface PoolからBackpressureする。その後Phase 0.2で固定版Blenderの最小実行経路、共通簡易Preset、`BoundaryLoopFill`／`BlindNonManifoldFill`、`EarlyFixtureSelectionProfile`、Building人力Scopeを含むSource Catalog、3種のCanonical Bundle Index、ZCG v1 Encoder／Decoder、Report／Dataset Index／Receipt Codecを作り、多数のSynty／Poly Pro Universe等のEligibleモデルからLicensed Render／Convex Fixtureを自動選抜する。Cap Loop等の既知正解は別のSynthetic Watertight Test Fixture Generatorで用意し、実AssetからStrict Solidを生成しない。続いてPhase 0.25のCook比較Probeを合成Convex正本とReceipt検証済みIndex hashで識別したLicensedRepresentative補助Datasetで実施する。Phase 0.5のXR確認後、Phase 1で即時切断と同時にV1 Dispatcher APIと固定容量Queueを合成Work Itemで固定し、後続PhaseのJobを順次接続する。高度なSchedulerは先に作らず、T-076と実機Counter後に必要な機能だけPhase 7で追加する。OpenXR API Layerと製品用連続録画形式は切断PoC成立とT-054完了後まで実装しない。

## 18. 用語

| 用語 | 定義 |
| --- | --- |
| Stable Geometry | バックグラウンド生成が完了し、表示へ確定適用された実Fragment Mesh／Cut Shell。ColliderやRigidbodyのCommit完了は含意しない |
| Pending Cut | 実命中により登録済みだが、`CutBoundaryRecord.GeometryState`がまだ`Committed`ではない切断。ExposureStateによりActive／Dormant／Suppressedのいずれでもよく、Collider完成度は含意しない |
| ActiveTemporaryBoundarySet | `ExposureState == Active`かつ`GeometryState != Committed`の意味上のActive境界集合。Incomplete操作で描画可能な既知境界の基準にはするが、補助Dormant Capを含む実描画コストや枚数上限の正本ではない |
| TemporaryRenderCapRecordSet | 当該フレームに実際のStencil／Cap Batchへ投入するCap Record集合。FullyFixed Cull Eligibleなら空、HasDetachedまたはCull失効済みなら全非Suppressed未Commit Cap、IncompleteならActiveTemporaryBoundarySet対応Capから成る。補助Dormant Capも描画コストと実Cap 2～4枚上限へ数え、Geometry Commit成功後だけ対応Recordを外す |
| TemporaryClipConstraintCandidateSet | 1個のRenderFragmentへ関係するGeometry未Commit切断半空間制約をOperationSupportStateで絞った集合。FullyFixed Cull Eligibleは空、HasDetached／Cull失効済みは全非Suppressed、Incompleteは既知Activeだけとし、Cap Record集合とは独立に保持する |
| SelectedTemporaryClipPlaneSet | Candidateを既存CutBoundary Record公開列の古い順に辿ったdependency-closed prefixから、Raster 8面とPixel fallback最大4面へ割り当て、左右眼とColor／Depth／Shadow／Stencil Volumeで共有する固定長の即時描画Plane集合 |
| IgnoredTemporaryClipBoundarySet | Candidateのうちdependency-closedな最大12面prefixへ入らない後発境界。即時Rendererの各Passと対応Stencil Volume submitだけから除外するが、Cap Record、CutBoundaryRecord、論理／物理状態、世代、背景Geometry／Convex処理には残すbounded degradation集合 |
| Stencil Cut Shell Base | Blender／Import前処理で生成・線形検証する即時仮断面用基底形状。finite、有効index、共有Edge position一致、各Topology Edgeの有向incidence総和0を要求するOriented Closed Triangle Chainであり、Self-intersection、均衡Non-manifold、Duplicate／Coincident、Internal／Nested Shellを許容する。StencilPolarityとMaxAbsoluteWindingBound証明を併記する |
| Cut Shell | Stencil Cut Shell Baseまたは直前のStable Cut Shellへ確定済み切断を適用して派生する、現在のObjectGenerationを表すOriented Closedな実行時形状。Stencil内部判定と次回の局所切断に使う |
| ClosedCutComponentSet | 独立に閉鎖・切断・Capできる1個以上のComponent集合。Component間のIntersection／Overlap、内部／二重Surfaceを許容し、Boolean Unionを要求しない。同じGeometry BufferがDisplay／Stencil契約も満たす場合は共有できる |
| ComponentFragment | 1つのClosed Cut Componentを現在までの切断面で分割したTopology連結成分。1回の切断から同じSideへ複数個生成され得る |
| CutConnectivityGraph | ComponentFragmentまたはLogical Convex CellをNode、同一ComponentのSurfaceAdjacencyと別Component間の生存AttachmentLinkをEdgeとする接続性の正本。親子関係とは独立し、connected-componentsからLogicalFragmentを構築する。Topology／Attachment Metadata更新ごとに`CutConnectivityGraphGeneration`を進め、非同期成果物のCommit条件に含める |
| AttachmentPatch | 別Component間の接続を近似する安定ID付きMetadata Group。Component ID組、重要度、AttachmentLinkId昇順の1～8件の`AttachmentLink`配列を持ち、連続領域のRuntime交差やBooleanを要求しない |
| AttachmentLink | AttachmentPatch内の離散的な接続Edge。A／BそれぞれのTopology Anchorを持ち、両EndpointがPositiveなら正側、両方Negativeなら負側へだけ残り、正負不一致またはOnPlaneを含む場合は切断される |
| AttachmentEndpointAnchor | Component ID、元Topology PrimitiveまたはLogical Convex Cell ID、barycentric／local座標からなるLink Endpoint。finiteであり、位置最近傍ではなくTopology系譜から切断後の子へ追跡する |
| Physics Proxy | 物理接触と高速切断のための低複雑度Convex／Compound。各Convexは閉凸契約を満たすが、同一Compound内の別Convex同士はOverlapしてよく、Strict SolidやConvex Boolean Unionを入力に要求しない |
| PhysicsConvexMassWeight | 同一FragmentGroup内の各Physics Convexへ親質量の配分比を与えるbinary64、finite、0以上のFinal用Metadata。Local ID順の左畳み和がfiniteかつ正であることを要求し、`assignedMass = parentMass * (weight / weightSum)`の固定順で配分する。Convexの生体積とは独立して重複Compoundの二重計上を避け、非交差時は継承し、交差時は当該Convexの子体積比だけで分割する。切断後はFinal物理Commit対象Fragmentごとにも和がfiniteかつ正でなければならず、Weight 0 Convexだけの子は規定の安全条件を満たす場合だけ質量移送なしで非物理デブリとして消去し、それ以外はFinal CommitをRejectして現有効物理状態を維持する |
| FragmentGroup | 同じ切断系譜、Canonical Mass Budget、支持集約、世代、Final物理Commitを共有するLogical Fragment集合。Provisional成功時は複数Actorを持て、構築不能時は1つのRigidbody／旧Colliderへ縮退する |
| PendingPhysicsSplit | Provisional構築不能または未試行で、見た目と論理状態は切断済みだが、1つのRigidbody／旧Colliderを共有してFinal Colliderを待つ保守Fallback状態 |
| ProvisionalRigidbody | Final Convex cook前にLogical Fragment別のpose／速度／外界Collisionを持たせる短命Actor。旧cook済みConvex Geometryを共有し、OBB切断体積比または等WeightでCanonical Mass Budgetを保存した近似mass／COM／inertiaを持つが、Final質量特性の正本にはしない |
| ProvisionalSeparationConstraint | 同じ切断で生じたProvisional Siblingの相対回転と接線移動を抑え、切断面法線方向の分離だけを許可して初期位置より深い再侵入を防ぐ短命Constraint。Sibling Collision無効化とは別の役割を持つ |
| ProvisionalCollisionResourceLease | 1つのcook済みConvex Geometryを複数のProvisional Shape Instanceが安全に共有する所有権Token。Actor公開前に取得し、Shape除去と物理ステップ完了後に一度だけ返し、最後のLease前にGeometryを破棄しない |
| ProvisionalLastFinitePhysicsSnapshot | 公開済みProvisional Groupごとに2個の固定Slotを持ち、HeaderのObjectId／ObjectGeneration／FixedStepId／ActorCountと、LogicalFragmentLocalId順の全Actor pose／速度を同一Fixed Stepからall-or-noneで公開する物理安全Snapshot。Staging完了後のatomic Slot切替だけを読取可能点とし、`ProvisionalFaultFrozen`への封じ込め専用で表示―物理誤差の測定、補間、すり合わせには使用しない |
| CanonicalMassBudget | Provisional Actorの近似mass特性と分離して保持する、切断直前の正規親質量とPhysicsConvexMassWeight系譜。Provisional OBB配分とFinal mass／COM／inertia計算の親Budgetとして参照し、連続切断でもSolver用Actor massから作り直さない |
| FragmentRenderAnchor | Parent ActorからProvisional Actorを初めて分裂させる際、表示Fragmentの初期World poseと点速度を連続させるstableな基準Transform。Final handoffでは物理Actorを優先するためActor pose補正やCOM速度変換には使用せず、表示Geometryが新しい物理frameへ追従して瞬間移動することを許容する |
| FragmentGroupPhysicsState | 固定値`Invalid=0`、`StableUnsplit=1`、`PendingPhysicsSplit=2`、`PendingSupportClassification=3`、`PendingAnchoredSplit=4`、`ProvisionalPhysicsSplit=5`、`ProvisionalAnchoredSplit=6`、`StableFastCook=7`、`PhysicsUpgradePending=8`、`StableFastSimulation=9`、`ProvisionalFaultFrozen=10`。未知値を公開せず、TraceのFromState／ToStateへ同じ値を使用する |
| ProvisionalPhysicsFallbackReason | Provisional公開前の構築失敗専用。固定値`None=0`、`ActorCapacityExceeded=1`、`ShapeCapacityExceeded=2`、`ConstraintCapacityExceeded=3`、`GeometryShareUnsupported=4`、`ShapeClassificationInvalid=5`、`MassApproximationInvalid=6`、`ActorCreationFailed=7`、`ConstraintCreationFailed=8`、`GenerationMismatch=9`、`AtomicCommitFailed=10`。Fallback EventではNoneを禁止し、公開後異常へ流用しない |
| ProvisionalRuntimeFaultReason | 公開後FaultのPrimary原因専用。固定値`None=0`、`NonFiniteActorState=1`、`ConstraintRuntimeFailed=2`、`LinearVelocityLimitExceeded=3`、`AngularVelocityLimitExceeded=4`。複数原因はこの順を優先し、同順位は最小LogicalFragmentLocalIdを選ぶ。Safety Frozen EventではNoneを禁止する |
| ProvisionalFaultContainmentDisposition | Primary Fault後の封じ込め結果。固定値`Invalid=0`、`RestoredAtomicGroupSnapshotAndFrozen=1`、`RemovedFromPhysicsSceneSnapshotUnavailable=2`、`RemovedFromPhysicsSceneContainmentValidationFailed=3`。Faultを検出した現Step値は使用しない。Safety Frozen EventではInvalidを禁止し、Primary Fault Reasonと独立に記録する |
| FixedSupportAnchor | 地面、壁、基礎、固定Constraintなど、切断後も動かしてはいけない支持位置を表す点または小領域。Micro AttachmentのAnchorとは別概念 |
| FixedSupportGraph | CutConnectivityGraphへ構造接続とFixedSupportAnchor Rootを付加した軽量View。切断後に生存SurfaceAdjacency／AttachmentLinkを通って固定Anchorから到達可能なGraph成分を判定する |
| LargeStructuralProp | 完全倒壊を許すとレベル、安全性、物理予算を破綻させやすいため、Structural Slab近似、Safety Tether Tree、World回転制限、SafetyFrozen Fallbackを適用する大型固定プロップ分類 |
| StructuralSlabComponent | 建物外周等を構成する独立閉鎖可能な厚い壁板Component。装飾付きDisplay／Stencil Geometryと原則1個、入口等では少数の直方体Physics Convexを対応させ、下端Ground Anchorと外周Attachment Linkを持てる |
| Synthetic Ground Root | Safety Tether Treeだけが持つ非物理・非Fragmentの論理Root。LogicalFragmentLocalIdを持たず、Tree構造とTraceの親Node IDでは予約値0で表す。直下にはGround Anchorへ到達する固定Fragmentだけを接続する |
| SafetyTetherTree | FixedSupportGraphとは独立したゲーム安全用の有向非循環木。Ground Rootから固定Fragmentを経て全動的大型Fragmentへ到達し、切断で構造的にDetachedとなったFragmentも相対並進制限だけで親へ接続する。支持状態やExposureStateを変更しない |
| SafetyTetherEdge | Safety Tether Treeの親子Fragmentを、対応Cut Boundary上の固定Anchor対で接続する相対並進制約。各動的FragmentはIncoming Edgeを厳密に1本持ち、回転制限は含まない。Synthetic Ground Rootから直下Fixed FragmentへのRoot Linkも同じTree Edge ID空間とTraceへ含めるが、Cut Boundary、Anchor対、SafetyTetherLevel、物理Constraint、Spring、移動Limitを持たない。Root Linkかどうかは親Node IDが0であることから一意に導出する |
| SafetyTetherEdgeLocalId | 0を未設定用に予約し、物理EdgeとTopology専用Root Linkで共有する正の32bit int。ObjectIdの生存期間全体で一意かつ非再利用とする。再構築後も同じ親子とTopology系譜を保つ物理Edge、および同じFixed子を持つRoot Linkは同じIDを継承する。消滅IDは再利用しない。継承後の新規IDはObject単位の単調増加Allocatorから、まず新規Root Linkを子LogicalFragmentLocalId昇順、次に新規物理EdgeをSpanning Treeへの追加順で発行する。overflow時は部分Treeを公開せず旧Tree維持またはSafetyFrozenとする |
| SafetyTetherTreeGeneration | Safety Tether Treeの内容を変更する原子的な再構築成功時だけ進むuint世代。Geometry／Collider差し替えとTree No-opでは進めず、Tree Work Resultと物理分裂Commitの検証条件に含める。`uint.MaxValue`を有効な最後の世代とし、その後はwrap／再利用せずTree変更をRejectする |
| PendingSafetyTetherPlan | LargeStructuralPropの即時解析運動前に、cookなしの切断Topologyから求める予定Tree。親、切断面Anchor、Level、相対並進／World回転上限を保持し、未確定時は表示clip／Capだけを許可して大型Fragmentの仮運動を止める |
| SafetyTetherLevel | Ground Root直下の固定Fragmentから最初の動的子へ向かうEdgeを0としてTree深さごとに増える非負整数。相対並進上限を`initialLimit * decay^level`で導出し、正の下限へClampしない。cookやEngine Object再生成では変化しない |
| StructuralSplitGeneration | FragmentGroupが実際に複数物理Groupへ分裂した論理Commitだけで子へ`parent + 1`継承するuint世代。各大型RigidbodyのWorldRotationOriginと指数減衰角度上限に使用し、Geometry／Collider差し替えでは進めない。`uint.MaxValue`を最後の有効値とし、その親をさらに分裂させるOperationはwrap／再利用せず全体をRejectする |
| PlayerLocomotionOccupancy | PlayerとプロップのPhysX接触を使わず、大型固定／構造プロップとレベル境界のOBB／Box／Capsule等についてPlayer Root／予測HMD Capsuleの人工移動可能域を近似する低複雑度Volume集合。全Render MeshのCamera包含や非干渉物体の視点被りを判定・防止するものではなく、押し戻し対一方向退出のPolicyとも分離する |
| OccupancyVolumeLocalId | 0を未設定用に予約し、1つのLevel実行期間中にPlayerLocomotionOccupancyのPrimitiveへ一意かつ非再利用で割り当てる正の32bit int。有限退出候補の生成順、ExitMetricのDepth Vector順、同値判定を決定論的にするために使用する |
| PlayerLocomotionPolicy | 非接触Locomotionが禁止領域との重なりを扱う固定方針。`NewEntryReject=1`、`PushOut=2`、`ExitOnly=3`とし、0は未設定、未知値はRejectする。PoCは`NewEntryReject`を正本とし、`PushOut`対`ExitOnly`はプレイテスト後に決める |
| ForcedOccupancyOverlap | Player自身の操作ではなく移動するOccupancyが現在姿勢へ侵入した一時状態。物理CommitやHMD姿勢を巻き戻さず、Profile上限の固定長作業領域でAllowedLocomotionPlane上の有限候補を全関連Volumeについて評価し、決定論的ExitMetricが厳密減少する人工移動だけを量の下限なしで適用する。全侵入深度が`occupancyExitEpsilon`以下になれば通常Policyへ戻り、Episode期限内に戻らなければfail-closedする |
| OccupancyExitBlocked | ForcedOccupancyOverlapで容量、探索範囲、減少候補またはEpisode期限の契約を満たせないfail-closed状態。人工並進と物理的押し出しを止めてFadeを維持し、明示的な安全Pose復帰、Level Reset、またはOccupancy変化だけで再開する。`OccupancyExitBlockReason`を保持する |
| OccupancyExitBlockReason | 固定値`None=0`、`NoDecreasingCandidate=1`、`SearchBoundsExceeded=2`、`VolumeCapacityExceeded=3`、`CandidateCapacityExceeded=4`、`EpisodeTimeout=5`、`NonFiniteDepth=6`。0と未知値でBlocked状態を公開せず、容量超過を部分評価成功へ読み替えない |
| SafetyFrozen | Safety Tether Tree、Anchor、世代またはConstraint予算を安全に確定できない公開済み大型Fragmentを、現在の安全姿勢で速度0・Kinematic相当に固定する品質低下状態。空中静止を許容し自由落下や部分Commitを避ける |
| PendingSupportClassification | FragmentGroup内にUnknownなLogicalFragmentが1つ以上あり、旧Rigidbody、Collider、Constraint、TransformとGroup運動を完全維持したまま支持再分類と背景Geometry処理を進める物理状態。既知のActive境界はclip／Stencil／仮Capだけを表示でき、Timeout時も未分裂Fallbackを維持する |
| PendingAnchoredSplit | FixedSupport分類は完了したがCollider切断／Bakeは未完了で、旧Colliderを固定したまま自由側だけを衝突なしで仮表示する状態 |
| LogicalFragment | 蓄積された切断面で区切られた論理的な連結成分。Colliderや表示Meshの完成前から存在し、Anchor到達性と後続切断の基底になる |
| CutBoundaryRecord | 1つのCut Planeが作った連結なFragment境界。正負Fragment、ExposureState、GeometryState、作成時の各Generationを保持する |
| Dormant Cut Boundary | 境界両側のLogicalFragmentがFixedで可視分離を要求しない`CutBoundaryRecord`。PoCの描画省略は個別境界ではなく所属`LogicalCutOperation`の全直接子Fixed集約で決める。Detached子を含む操作では通常Batchへ残せ、実Mesh完成後は同一位置で公開でき、後続切断でDetached成分に接すればActive化する |
| LogicalCutOperation | 一つの親LogicalFragmentへ一回の切断を適用した論理操作。CutOperationId、親ID／世代、生成した2～64個の一意な直接子ID、1～256個の一意なCutBoundaryId、作成時SupportGraphGeneration、OperationSupportState、FullyFixedCullInvalidatedを保持する。親は切断済みFragmentでもよい。不正ID、空境界、未接続子、世代不一致などは部分公開せず操作全体をRejectする |
| CutOperationId | 0を未設定用に予約し、ObjectIdの生存期間全体で一意かつ非再利用とする正の32bit int。Operation系TraceではValue0へ格納する |
| LogicalFragmentLocalId | 0を未設定用に予約し、ObjectIdの生存期間全体で一意かつ非再利用とする正の32bit int |
| CutBoundaryLocalId | 0を未設定用に予約し、ObjectIdの生存期間全体で一意かつ非再利用とする正の32bit int |
| OperationSupportState | LogicalCutOperation直接子の三値集約。`Incomplete=0`はUnknownあり、`FullyFixed=1`は子数2以上かつ全Anchored、`HasDetached=2`はUnknownなしでDetachedあり。優先順位はIncomplete、HasDetached、FullyFixedとし、defaultを安全なIncompleteへ固定する |
| FullyFixedCullInvalidated | 過去Operationの直接子が後続切断で置換・細分された、または所属する過去境界をActive化する際に不可逆にtrueとなる描画Cull失効値。境界Active化より先に設定し、PoCでは直接子集合を子孫へ再構築せずfalseへ戻さない |
| FullyFixedCullEligible | `OperationSupportState == FullyFixed && !FullyFixedCullInvalidated`から導出する値。trueの場合だけLogicalCutOperation全体のTemporaryRenderCapRecordSetとTemporaryClipConstraintCandidateSetを空にできる |
| Suppressed Cut Boundary | 支持分類未完了、世代不一致、接続曖昧などにより安全な露出状態を決定できない`CutBoundaryRecord`。clip、Stencil、仮Cap、Offset、Impulseを起動せず、再分類後にDormantまたはActiveへ遷移する |
| Kerf | 切断によって除去される物理的な幅。本作では0とし、見える隙間は破片の相対移動だけで生じる |
| Cooking Profile | `Physics.BakeMesh`と`MeshCollider`へ同一指定するcookingOptionsの構成。初回分裂用Fast Cookと選択的Upgrade用Fast Simulationを使い分ける |
| Physics Upgrade | Stable Fast Cook破片と同じ形状の別MeshをFast Simulationで再Bakeし、安全な物理ステップ境界でColliderを昇格させる処理 |
| Micro Attachment | Physics Proxyで表現しない微小な付属部品。切断帯へ触れた場合は物理破片を作らず不可逆に全体消去する |
| VisualOnlyMicro | 接触、支持、Gameplay、主要Silhouetteへ重要な寄与がなく、専用Physics Convexを生成しない小部品分類。Hostの表示／MassWeight近似へ含め、切断帯へ触れた場合はMicro Attachmentとして消去できる |
| PhysicsSignificantAttachment | 接触、支持、Gameplayまたは主要Silhouette上の理由から専用Physics ConvexとAttachmentPatchを持つ付属部品。未切断時はHostと同じRigidbodyのCompound Colliderへ含める |
| Attachment AliveMask | AttachmentIdごとの生存状態。即時表示、確定Mesh、再切断、世代管理で共有し、消去済み部品の再出現を防ぐ |
| GPU Micro Debris | 事前分類済みMicro Attachment、または物理Convex対応がMissing／SharedのDebrisCandidateで補助的な消去条件も満たしたRuntime Fragmentの実GeometryをShard Cluster化し、Vertex Pulling、解析運動、Indirect Batch、Opaque Dither Clipで描く短寿命・衝突なしEffect。即時clip中のTriangle崩壊には使用せず、汎用ローポリ破片はFallback |
| RenderFragment | 実表示Mesh切断後の連結な表示成分。論理Convexとの対応確定までは塊として表示し、幾何寸法だけではデブリ化しない |
| LogicalConvexFragment | 自前Convex切断で生成されるcook前の論理物理成分。RenderFragmentとの対応判定には使用できるが、まだUnity Colliderとして適用済みとは限らない |
| PhysicsRepresentationStatus | RenderFragmentとLogicalConvexFragment集合の対応状態。`Pending=0`／`Represented=1`／`Missing=2`／`Shared=3`／`Ambiguous=4`で固定し、defaultは物理Commit禁止のPendingになる |
| SharedResolutionRole | Shared連結成分内のRenderFragmentへ付けるRole。`None=0`／`Keeper=1`／`DebrisCandidate=2`／`PreserveFallback=3`で固定し、Shared以外はNoneとする |
| RenderFragmentLocalId | ObjectId＋ObjectGeneration内だけで一意かつ非再利用とする正のintのRenderFragment識別子。0は未設定用に予約し、TaskIdとは独立 |
| LogicalConvexFragmentLocalId | ObjectId＋ObjectGeneration内だけで一意かつ非再利用とする正のintのLogicalConvexFragment識別子。0は未設定用に予約し、TaskIdとは独立 |
| SharedGroupLocalId | Shared対応グラフの連結成分を識別する正のint。0は未設定用に予約し、ObjectId＋ObjectGeneration内で一意かつ同一世代中は解体後も再利用しない |
| Shared Convex Resolution | 複数の大型RenderFragmentを同じ暫定物理GroupへCommitした後、2集合の頂点凸包をbounded GJKと全頂点signed-distance検証でstrictに分けられる場合だけ、共有Convexを単一平面で後追い分割する品質向上処理。一般Convex decompositionやBoolean Unionではない |
| SharedConvexResolutionProfile | Shared Convex Resolutionの絶対／相対epsilon、Support頂点、入力Convex、GJK反復、Pending／Concurrent Job数、固定Request／Native Work Slot数を固定するRuntime Profile。総Native byte数を起動時に検証し、上限超過でBufferを拡張・待機しない |
| SharedConvexResolutionAdmissionCandidate | Request Slot予約前に現行Shared Groupから作るimmutableな受付候補。ObjectId、TargetObjectGeneration、SharedGroupLocalId、入力Convex数だけを保持してSlot／TaskId／Native Geometryを所有せず、予約失敗時のCapacityExceeded世代相関にも使用する |
| SharedConvexResolutionOutcome | Shared Convex Resolutionの固定結果。`Invalid=0`、`Resolved=1`、`UnseparableBySinglePlane=2`、`Indeterminate=3`、`SplitValidationFailed=4`、`Superseded=5`、`CapacityExceeded=6`。Invalidは未実行状態でFinished Eventには禁止し、Resolved以外では別Rigidbodyへの分裂を行わない |
| UnseparableBySinglePlane | 対象2集合の頂点凸包が交差、包含、接触、または距離が`2 * SharedSeparationEpsilon`以下にあり、両側epsilon余白を持つstrictな単一分離平面が存在しない終端結果。同世代では再試行せず共有物理と空中浮遊を許容する |
| Debris Geometry Atlas | Micro Attachment等の事前生成Vertex、Corner／Index、Shard MetadataをAssetロード時に登録し、Asset寿命中は変更しないImmutableな共有GPU Buffer群 |
| DebrisEventId | 0をInvalid用に予約し、1 Trace Runに一致するGpuMicroDebrisSystem実行セッション内で1から単調発行して再利用しないuint ID。TraceではValue0のdoubleへ正確な整数として格納し、TestRunIdとの組でArena Slice、Event、Fenceを一意に関連付ける |
| RuntimeDebrisSliceState | Runtime Arena Sliceの状態。`Invalid=0`、`Allocated=1`、`Active=2`、`Retiring=3`、`Reusable=4`の固定値を持つ |
| Runtime Debris Geometry Arena | Runtime FragmentのCorner Streamを置く固定容量Page／Ring GPU Buffer。SliceをDebrisEventIdが所有し、最終Draw後のFence等による完了証拠と最小保持Frameの両方を満たした後にだけ回収する。容量不足時は再確保・待機せずFallbackする |
| CaptureDraftRunContext | ライブCaptureをRunへ関連付けるimmutableな内部Context。TestRunId等のRun開始時不変値を持つが、freezeまで未確定なTraceRunManifest／Manifest hashは持たない |
| CaptureFrameDraft | ライブCaptureの相関正本となるimmutableな内部Record。最終CaptureFrameRecordに必要な値からManifest参照だけを除き、Draft RegistryがTestRunId＋CaptureFrameIdで所有する |
| CaptureFrameDraftStatus | Draft Registry Entryの状態。`Pending=0`、PNG Staging Entry登録済みを示す`Staged=1`、最終Record／期待集合から除外する終端`Dropped=2`で固定する。Staged fileの永続化はPublication Plan確定前の別Gateとする |
| MaxInFlightDraftCount | Run内で全queue／workerを横断して同時に存在できるPending Draft数。終端時に再利用するPending Slot Poolの容量であり、Registry総Entry容量とは別 |
| MaxDraftCountPerRun | 1 Runで発行できるDraft Entry総数。1～100,000でMaxInFlightDraftCount以上とし、Staged／Dropped tombstoneを含むappend-only Entry Store容量およびPlan EntryCountの上限となる |
| CaptureFrameDropReason | Capture処理の拒否／Drop分類。既存0～4を維持し、`FrameDraftRegistryFull=5`はID発行前のCaptureFrameAdmissionRejected専用、`PngEncodeFailed=6`、`PngStagingStoreFull=7`、`CaptureCancelled=8`は正のIDを持つ通常Draft Drop専用、`FreezeDrainTimeout=9`はfreeze terminal Builderだけが生成する強制Drop専用としてappend-onlyで追加する |
| CaptureFrameAdmissionRejected | Entry StoreまたはPending SlotをID発行前に予約できなかった受付拒否Trace。CaptureFrameIdは0で、Dropped Draftを意味しない |
| CaptureFrameDraftTerminalCoordinator | Main Thread上で全DraftのStage／Drop Intentを一列に処理し、Draft共有資源、Registry終端遷移、Pending Slot解放を変更できる唯一の所有者。worker／callbackは共有資源をrollbackせず結果通知だけを行う |
| CaptureFrameDraftTerminalIntentQueue | Stage／Drop Intentをproducerから単一Terminal Coordinatorへ渡す固定長MPSC Queue。容量は`checked(2 * MaxInFlightDraftCount)`、同一Draftの未処理数とRun中受理総数は各最大2件で、enqueue成功時だけIntent私有Bufferの所有権をCoordinatorへ移す |
| TerminalIntentEnqueueStatus | Terminal Intent受付結果の固定enum。`Accepted=0`、`Backpressured=1`、`DraftAlreadyTerminal=2`、`IntentLimitExceeded=3`、`RunNotAccepting=4`、`InvalidIntent=5`。Acceptedだけが所有権移転、Backpressuredだけが再試行可能 |
| TerminalIntentOwnershipSnapshot | producer join後の最終drain完了を証明するimmutable集計。Queue件数0、受理Intent数と処理Intent数の一致、Queue所有私有Buffer数0、producer保持私有Buffer数0を必須とする |
| DraftDropTraceEmissionState | 通常Draft Drop Traceの直交状態。`None=0`、`Pending=1`、`Attempted=2`の固定値を持ち、Dropped確定時にPendingとなり、成功・失敗を問わず最初のenqueue試行前にAttemptedへ不可逆遷移する |
| RecordDraftDropped | Registry内のPendingなDrop Trace payloadをCaptureFrameIdで一度だけ消費して固定Eventをbest-effort生成するinternal Observer経路。Draft本体状態やPending Slotを変更せず、Legacy RecordDroppedおよびfreeze terminal Builderから分離する |
| CaptureFramePngStagingEntry | readback／encode済みPNGとbyte length／content hash／Draft IDを保持する未公開の一時成果物。canonical sidecarや最終Artifactではない |
| Capture Freeze Barrier | 新規受付停止後にin-flight Draftをdrainしてproducerを静止し、通常FIFOを通常領域へ完全Drainした後、immutableなForcedDropFrameIdSetと完全一致する強制Drop／RingFrozenだけを専用reserveへall-or-noneで直接AppendしてRecorderをFrozen化するMain Thread上の順序付きBarrier。Append失敗中はAwaitingFreezeTerminalへ留まりExportを禁止する |
| BeginFreezeTerminalAppend | producer静止、通常Queue空、FIFO drain完了を検証してCapturingPostRollからAwaitingFreezeTerminalへ遷移するBarrier専用API。terminal reserve有効時のpublic Freezeによる迂回を禁止する |
| SealableTraceWriter | Capture RunのTrace producerへ渡すBurst互換writer。atomicなRun Seal StateとActiveWriterCountを用い、seal後のenqueueをQueueへ入れずFailure Countだけへ記録する |
| SealAndDrainRunForFreeze | Loggerの当該Runを原子的にsealし、開始済みwriter退出後に通常Queueを完全DrainしてSealedを公開するFreeze Barrier専用protocol |
| CaptureTraceProfile | 既存CaptureFrameProfileから分離したTrace／Draft容量設定。CaptureProfileId、PostRollCapacity、MaxInFlightDraftCount、MaxDraftCountPerRunを持つimmutable型 |
| PhaseZeroCaptureProfileSet | 既存Phase 0 Frame Profileと、PostRoll 4096／同時Draft 32／Run総Draft 10000のCaptureTraceProfileを同じProfile IDで組にする標準Factory成果物 |
| CaptureTraceFlightRecorderFactory | CaptureFrameProfileとCaptureTraceProfileのIDを照合し、terminal reserveをchecked算出して新internal constructorでreserve有効Recorderを構築する唯一のCapture Run用Factory |
| CaptureFrameDraftTraceContext | 既存CaptureFrameTraceContextの12 fieldを受付時に欠落なく保持し、強制Drop terminal Eventへ通常Dropと同じ相関値を転記するDraft内immutable Context |
| FreezeTerminalCheckpoint | Logger seal／最終Drain直後にMain Threadで一度だけ採取し、CaptureRingFrozenの時系列fieldとTestRunIdの正本にするimmutable値 |
| ForcedDropFrameIdSet | Freeze deadlineでPendingからFreezeDrainTimeoutへ強制終端した全DraftのCaptureFrameIdを、正数・一意・昇順で固定したimmutable集合。terminal Trace列の完全性検証と再試行の正本 |
| TraceCaptureOverflowCount | Freeze Barrierの通常FIFO Drainでdrain済みだがNormalPostRollCapacity不足によりFrozen captureへ複製できなかったEvent数。SummaryのToStateへ保存し、非ゼロRunをIncompleteにする |
| SealedTraceEnqueueFailureCount | Logger sealの線形化可能なcutoffで確定する現Runのimmutable enqueue失敗数。SummaryとComplete判定が参照する正本 |
| PostSealTraceEnqueueAttemptCount | cutoff後／Sealed後のwriter違反をRun Countから分離して保持するprocess診断Counter。Trace bundle完全性には使用しない |
| Capture Run Root | 信頼済みstaging／final base rootとTestRunIdから`runs/run-{TestRunId}`として導出し、各base側の2本のOS排他lockと相互binding markerを持つ1 Run専用directory対。両lockを正規化path順で取得し、別Runや同時Coordinatorとの共有を禁止する |
| Capture Run Initialization Marker | 両Run rootをTestRunId、128 bit RunInitializationId、Root hash、相互init hashで結ぶcanonical `run.init`／`run.ready`。片側作成crashをlock下で復旧する二相初期化の正本 |
| CapturePublicationPlan | 最終Manifest確定後、全staging fileのdurable化後に最後に原子的確定するcanonical Schema v1の永続staging専用file。RunInitializationId、Staged Draft由来の期待CaptureFrameId集合、PNG／sidecarのstaging／最終path・長さ・hashを固定するが、Trace bundleの許可ファイル集合には加えない |
| Capture Artifact Index | Capture完了時に`capture.index`として永久保存するCapturePublicationPlanと同一canonical bytes。期待CaptureFrameId集合とArtifact hashを保持し、Plan cleanup後もCaptureCompleteの復元と欠落／改変検出を可能にする |
| CaptureComplete | Publication Planの全期待PNG／sidecarと最終Manifest参照を再照合し、同じ期待集合を持つ永続`capture.index`をdurable確定した後だけ成立するRun単位のCapture完了状態。一部Artifact成功やTrace bundle単独成功は含まない |
| Shard Cluster | 接続、Normal、Material、面積を基準に隣接する通常2～8 Triangleをまとめ、同じGPU Transformで飛散させる単位 |
| WorldPhysicsProfile | 世界重力を正本として保持し、Unity Physics、予測、解析運動、GPU Effectへ同じ値を供給するバージョン付き設定 |
| Pending Two-Sided Shadow | 即時切断中だけ、開いた外殻の裏面をShadow Mapへ書いて断面キャップの遮蔽を近似する両面ShadowCaster経路 |
| Cap Bounds Polygon | 対象のローカルOBBと切断平面の交差から生成し、他のTemporary Render Boundary半空間でclipする3～6頂点の有限な仮キャップ板 |
| Stencil Conflict Graph | CapCompatibilityKey別の互換Groupをノードとし、左右眼いずれかで保守的な可視Cap Boundsが重なる非互換Group間へ辺を張るStencil Batch彩色用グラフ |
| CapCompatibilityKey | 全World Cut Plane、Side／半空間、分離Offset、Cap Material／Debug／Fade状態、EffectiveStencilPolarityを正規化して表すStencil共有互換Key。Polarity UnknownではStencilShellInstanceIdも含み、Winding Boundは別のColor容量制約として扱う |
| StencilPolarity | Cut Shell ComponentのWinding符号Metadata。UniformWindingSignCertificateを持つComponentだけを前処理signed volumeからPositive／Negativeへ分類し、Negativeはwinding反転でPositiveNormalizedへ正規化する。未証明Componentを含むShellはUnknownとし、別ShellとStencil Countを共有しない |
| UniformWindingSignCertificate | 対応View／Skinning Profile内でCut Shell Componentの全非ゼロWinding領域が同一符号であることを専用の前処理成果物として保証するMetadata。signed volumeだけではこのCertificateにならず、未証明ComponentはPolarity Unknownとする |
| EffectiveStencilPolarity | 前処理済みStencilPolarityへWorld Transformの負determinant反転をXORした描画時Polarity。Front／BackのIncrement／Decrement交換に使用する |
| MaxAbsoluteWindingBound | 対応View／Skinning Profile内の任意pixelで1つのCut Shell Recordが寄与し得る絶対Windingの保守的上界。1～255の既知値またはUnknownを取り、既知値だけを8bit StencilのBatch候補にできる |
| StencilCountBatch | 同じCapCompatibility Group内のRecordをstableなFirst-Fitで分割した8bit Winding Countの実行単位。Sibling Batch同士は無条件に競合し、別Stencil Colorへ配置する |
| BatchWindingBound | 1つのStencilCountBatchに含まれる全Recordの既知MaxAbsoluteWindingBoundをchecked加算した値。255以下を必須とし、同じColorへ複数Batchを再統合する場合もchecked和を再検証する |
| Winding Count Stencil | Cut ShellのFront／Backで排他的に予約した専用Stencil Byteの全8bitをIncrementWrap／DecrementWrapし、Positive正規化済みかつBound合格した複数物体の非ゼロMaskを和集合化する方式。Saturateおよび部分Bit Counterは使用しない |
| Residual Stencil Support | Front／Back集計後もStencilが非ゼロとなる画面領域。整合したCut Shellでは切断開口部に限られ、可視Cap Boundsをその保守的上界として使う |
| Cap Visibility Cull | 論理破片×切断面のCapRecordを左右眼で判定し、全Capが両眼とも裏向きの互換GroupをStencil彩色前に除外する処理 |
| SlashGeneration | GestureをLatchするたびに進む、斬撃入力単位の単調増加番号 |
| ObjectGeneration | 対象への実命中とPending Cut登録時に進む、対象確定状態の単調増加番号 |
| BaseObjectGeneration | 投機ジョブが入力としてスナップショットしたObjectGeneration |
| Commit | 世代検証済み成果物を描画・物理状態へ原子的に差し替える操作 |
| SlashWave | 振り途中で切断面と初期SlashFrontをLatchし、Extending中も前縁を飛翔させながら同一平面へ頂点／辺を追加する論理状態 |
| Slash Latch | 刀軌道が閾値を満たした時点で、SlashId、SlashFrame、初期SlashFrontを不可逆に確定し、VFXと命中判定を開始する操作 |
| SlashFront | 三日月VFXの前縁と一致する、SlashFrameの2D座標で保持した粗い折れ線。各辺の帯状Sweepが実際の当たり判定となる |
| Front Vertex／Edge | SlashFrontを構成する点と線分。生成時刻、初期面内位置、移動方向、速度を持ち、生成後だけ飛翔・命中へ参加する |
| SpanAxis／TravelAxis | SlashFrame内で、三日月が横へ広がる方向と斬撃波が前進する方向。SlashFrontの一価性と逆行判定の基準 |
| Candidate Flight Bounds | 切断面、最大飛距離、最大前縁範囲から作る保守的なBroadphase領域。投機候補列挙専用で、命中確定には使用しない |
| BladeFrame | 刀Prefab内でBladeAxis、EdgeDirection、SideNormalと判定Sample Pointを定義するローカル座標系 |
| Edge Lead Score | 刀身軸方向を除いた運動と刃方向の内積。正なら刃が先行し、負なら峰が先行する |
| NonCutting | 刀を表示するが切断Sweepも物理衝突応答も生成せず、全オブジェクトを素通りする状態 |
| Future Event DAG | 未来の候補接触、姿勢予測、切断、Commitを依存関係で表した評価グラフ |
| Work Item／TaskId | Job、I/O、GPU処理等を横断して追跡する論理作業単位と相関ID。C# `Task`型に限定しない |
| EvaluationWorkItem | Dispatcherへ渡すReady状態の論理作業Descriptor。TaskId、固定PriorityClass、Deadline、Batch Key、推定費用Bucket、入力世代Snapshot、成果物所有者を持ち、Unity Object、Geometry内容、EnqueueSequenceを持たない。Sequenceは受付成功時にDispatcher内部Recordへだけ発行する |
| FutureEvaluationDispatcherV1 | Main Thread上で未Schedule Work Itemだけを固定容量・固定PriorityClass・Deadline順に選ぶ初期Soft Real-Time Dispatcher。内部Queue形式をAPIへ公開せず、後期Backendとの差し替え境界となる |
| CriticalReservedSlots | 低優先度Work Itemが消費できないQueue予約枠。CriticalPhysicsSafetyとConfirmedPhysicsだけが利用でき、Background投入後も物理安全作業の受付余地を残す |
| MobTrajectoryKernelV1 | Global FixedStepの整数倍で、固定MobId順のCurrent StateからNext Stateを二相更新する副作用のない初期群衆移動Kernel。Waypoint／Lane Desired Motionだけを扱い、Nearのライブ更新とMobPlan未来生成で共有する。NavMeshAgent、Root Motion、RigidbodyによるRoot位置更新と併用しない |
| AnimationPlannerV1 | Behavior Intent、Locomotion、Root速度／向き、累積移動距離から副作用なく`ExplicitAnimationStateV1`を生成するゲーム側Planner。現在表示BackendやAnimator内部Stateを入力正本にしない |
| Animation Clip Catalog | 正のAnimationClipIdごとに`Loop`／`Clamp`、finiteかつ正のcanonical DurationSeconds、Clip content identityを固定するcanonical表。固定property順とClip ID昇順から算出する内容hashを`AnimationAssetSetVersion`へ結合し、Mode／duration変更を同一Asset版として扱わない |
| ExplicitAnimationState | Current／Future双方のAnimation Source、Source Time／Phase、Playback、Blend／Transition等を表現するゲーム側の副作用のない値状態。標準経路ではAnimator／Controller内部Stateから復元せず、必要な履歴を明示する |
| ExplicitAnimationStateV1 | 正のint範囲の単一AnimationClipId、finiteかつ0以上のbinary64非wrap累積Phase、finiteかつ0以上のbinary64 PlaybackRateCyclesPerSecondからなる、所属SampleのFixedStepIdへ解決済みの初期表現。同一Clip間だけPhase補間し、異Clip境界はhard switchする。将来の2 Source Blend等は互換意味境界を保ったschema拡張とする |
| ResolvedAnimationPoseInput | 対象`FixedStepId`、そのStepへ解決済みの`ExplicitAnimationState`、Rig／Animation Asset Set／Evaluation Profile Identityを一体で保持するimmutable入力。裸のStateや別StepのStateをEvaluatorへ渡さない |
| FutureAnimationPoseEvaluator | `ResolvedAnimationPoseInput`からcanonical Bone順Rig Poseを生成する交換可能境界。controllerなしPlayable、Pose Table、将来SamplerをBackendにでき、評価要求順や暗黙Controller rolloutへ依存せず、PlaybackRateによる追加の時刻進行を行わない |
| MobTrajectorySample | 1つの`MobId + PlanGeneration + FixedStepId`に属する固定間隔Sample。position、velocity、heading、Locomotion、ExplicitAnimationState、経路カーソルを持ち、固定長Ring Buffer内でMid／Far再生と未来姿勢生成に利用する |
| MobTrajectory Hold | 有効Sample不足または固定容量／Live Fallback予算超過時に、最後の有限なRoot姿勢と`ExplicitAnimationStateV1`全体を維持するbounded degradation。古い軌道の無期限外挿、Clip／Rateの独自変更、同期全群衆再計算、Buffer再確保を行わない |
| Convex Job Pipeline | Native Convex B-repをCount／Write／Validation Jobで平面分割し、MeshData公開後に`Physics.BakeMesh` Jobを接続してCollider Commitへ渡す処理列 |
| Temporary Low-Poly Proxy | Stable Geometry／Colliderが未完成または検証失敗の間に使う、低Triangle表示形状、簡易Convex、Compound Primitive、汎用ローポリFallbackの総称。各実装品質段階の正しさをT-077、生成費用をT-076で測る |
| Geometry／Cook Microbenchmark | 表示Mesh切断、Convex切断、Temporary Low-Poly Proxy、cookを固定Datasetで工程別に測り、計算KernelのSingle-Thread µs/op、Bake／Commit単発Latency、Job Batch Throughput／End-to-End latencyから容量式を作る性能検証 |
| GeometryBenchmarkRunManifest | Cook ProbeとGeometry／Cook Microbenchmark専用のversion付きcanonical JSON。1 Manifestは単一DatasetCaseIdの固定規模軸と、単一Target／Stage／ExecutionMode／CookingProfile／Metric／Unitの1測定系列を表し、BenchmarkSuiteIdで複数系列を束ねる。同一SuiteではDatasetIdからDatasetContentSha256への写像を一意にする。Target×Stage×Mode、全propertyの型・値域・null条件・順序を固定し、clean Repositoryだけ保存を許可して既存TraceRunManifestを拡張しない。v1のLoader上限は64 KiB |
| DatasetCaseId | DatasetContentSha256で固定されたDataset内の1入力caseを識別するID。早期Licensed Fixtureでは`SourceFixtureId.TierToken.VariantId`を使い、Render／Convexの同名Variantを分離する。Synthetic Watertight Fixtureは別DatasetIdとGenerator由来Case IDを使う。同一Suiteでは1つのDatasetIdに1つのDatasetContentSha256だけを許可し、同じcaseの規模軸を不変とする。Manifestの説明変数とResultの測定値をjoinして容量式へ使用する |
| GeometryBenchmarkResult | 1 BenchmarkRunIdの取得順Samplesと、同じSamplesから決定論的に再計算できるCount／Minimum／Maximum／Mean／P50／P95／P99を保持するcanonical JSON。対応Manifestのcontent hashを持つ。v1は100万Sample／64 MiBをschema上限とし、Loaderにはそれ以下の明示上限を必須とする。Bytes／CountのSamplesと順序統計量は整数だがMeanは取得順binary64左畳みのcanonical doubleである。Rejectedは計測不能だけを数え、対象処理失敗はFailureRateへ残す |
| GeometryBenchmarkSuiteIndex | 1 BenchmarkSuiteId内の全RunについてManifest／Result content hashとsample／reject件数を固定するcanonical index。v1は10万Entry／64 MiBを上限とし、Loaderへそれ以下のbyte／件数上限を必須とする。Repository外の一時出力へ最後に書き、検証後にSuiteディレクトリを原子的に確定する |
| Unity Built-in 3D Physics | GameObject／Rigidbody系で使用するUnity内蔵NVIDIA PhysX統合。DOTSの`Unity Physics`パッケージとは別物 |
| Native Cook Probe | Unity `Physics.BakeMesh`と、別HarnessのNative PhysXによる頂点Hull／完全Topology／直接生成を同一Datasetで比較する測定専用実験。製品Backendではない |
| Native採用Gate | Unity経路の実要件違反、Unity側最適化の枯渇、大きな継続差、実ゲーム統合Prototype成立をすべて要求する部分置換の判断条件 |
| Prediction Physics | 独立PhysicsSceneで局所物理島を未来へ進め、命中予定姿勢を求める処理 |
| Confidence | 未来結果をDeterministic／Conditional／Speculativeに分類した信頼度 |
| Trace Event | 状態遷移、Taskライフサイクル、Commit結果を整数IDと時刻で表す軽量イベント |
| Flow Event | Schedule元と別スレッド／Job上の実行をUnity Profiler内で結ぶ相関情報 |
| Flight Recorder | 直近イベントを循環保持し、異常検出時に前後履歴を固定・保存する仕組み |
| Early Licensed Fixture | Phase 0.2でSynty／Poly Pro Universe等のライセンスAsset群のうちSource CatalogでEligibleとした対象へ共通簡易Presetを適用し、個別修理なしで自動選抜したRender／ConvexテストGeometry。Poly Pro Universe Buildingは人力選定した豆腐型だけを対象とし、製品用変換済みAssetや全Building／全Asset対応の証拠ではない |
| Synthetic Watertight Test Fixture | プログラムまたは固定版Blenderスクリプトから決定論的に生成する閉Triangle Meshのテスト／Benchmark専用入力。製品AssetやライセンスAssetの派生物ではなく、製品Preprocessor成果物、Runtime同梱物、代表Asset合格条件にはしない |
| SyntheticWatertightFixtureProfile | Synthetic Watertight Fixtureだけに適用するepsilon、Triangle／Component／自己交差候補上限と共有Validator algorithmを固定したcanonical Profile。EarlyFixtureSelectionProfileとは別hashを持つ |
| SyntheticWatertightDatasetIndex | Generator Recipe、Synthetic ZCG、合格Validation ResultのhashをCaseごとに固定するcanonical Index。Licensed Source／Tier／Reportを参照しない |
| SyntheticFixtureValidationResult | Synthetic ZCGの閉Topology、向き、成分volume、自己交差と上限を共有Validatorで検査したcanonical結果。不合格をLicensed GeometryRejectedへ変換しない |
| LicensedRepresentative Dataset | Early Licensed Fixtureを同じHarnessで測る非公開の補助Dataset。公開合成Fixtureのcanonical結果が実Asset傾向から大きく外れないか確認するために使い、入力GeometryとAsset対応は公開しない |
| EarlyFixtureSelectionProfile | Phase 0.2 Licensed選抜のAsset Category集合、Source Triangle Band境界、epsilon、穴封鎖、Bounds／表面品質、Render／Convex Gate、決定論的入力上限、資源上限、再試行を固定するversion付きcanonical JSON。Solid／体積／全Mesh自己交差契約を持たず、Profile hash変更で派生Fixtureを無効化する |
| EarlyFixtureSourceCatalog | Blender Import前に匿名SourceFixtureId、AssetCategory、Phase02Eligibility、ScopeReason、正規化SourceRelativePath、Source file hashを固定するcanonical非公開Catalog。Poly Pro Universe Buildingでは人力選定した豆腐型とScope外形状を区別し、Import前失敗でもSource母集合と実投入集合を復元できる。canonical bytesはSource Bundleへ含める |
| CanonicalBundleIndex | Source／Script／Presetの展開済み通常fileを正規化相対path、byte長、raw content hashで列挙するversion付きcanonical Index。空directoryやtimestampを無視し、symlink等を拒否する。Index bytesのSHA-256を各Bundle Content SHA-256とし、Verifierが実rootの欠落／余分file、長さ、hashをBlender前とReceipt前に完全照合する |
| ZantetsuCanonicalGeometry | Phase 0.2のLicensed Render Triangle Mesh、Synthetic Watertight Triangle Mesh、またはConvex Setを、meter／Y-up／左手系、正規化binary32位置、決定的なposition／face／hull順で保存するversion付きcanonical binary。v1は切断／Cook Benchmark用の形状Topologyだけを持ち、拡張子は`.zcg`、decode後の再serialize一致を必須とする |
| SolidSignedVolumeV1 | Synthetic Watertight ZCGの連結成分について、成分Bounds中心、canonical Triangle／成分順、triangleごとの除算、binary64左畳みを固定して正体積を判定するテスト専用volume契約。Licensed／製品Solidを意味しない |
| SolidGeometryValidatorV1 | Synthetic Watertight ZCGだけを読み、閉Topology、`SolidSignedVolumeV1`、`ClosedTriangleDistanceV1`を同一artifactで検証するversion固定Validator。Synthetic Script Bundleで内容を固定し、Licensed Harnessと製品Preprocessorから呼び出さない |
| SolidCandidateBvhV1 | Synthetic Watertight Triangleのepsilon拡張AABBから固定axis／median規則で構築し、自己交差の一意候補pairだけを生成するテスト専用の決定論的BVH。候補はcanonical順へsortし、Synthetic Profile上限で停止する。Licensed／製品Meshへは実行しない |
| ClosedTriangleDistanceV1 | 全Triangle pairを、固定順のpoint-to-closed-triangle／segment-to-closed-triangle／closed-segment距離候補と`epsDistance`で保守的に分類するSolid自己交差predicate。共有indexがあるpairも除外せず、`SharedSimplexResidualV1`で共有simplex近傍外の残余交差を検査する |
| Early Fixture Reduction Variant | 同じSource Fixtureから固定Direct Decimate Presetで作るOriginal／Tri100／Tri500／Tri1000／Tri2000／Tri5000／Tri10000。Tri名は正確な出力数でなく要求Targetを表す。DatasetCaseIdで区別し、実入力Triangle数をBenchmark Manifestへ、Source／Target／Actual／Ratio／Appliedを選抜Reportへ記録する。Voxel／Convex削減系列とは別物 |
| Early Fixture Voxel Variant | Licensed Render Sourceを相対Voxel SizeのVoxel64／128／256でTopology再構成した基底と、その限定Post-Decimate。Triangle差が小さくても基底を保持し、`vox128.base`等のVariantIdとRender Tier付きDatasetCaseId、Voxel Size、Bounds／表面偏差を記録する。Solid検証は行わない |
| EarlyFixtureSelectionReport | Phase 0.2でEligibleなSourceから生成を試みた全Variant Entryについて、version、Profile／Source／Blender／Script／Preset hash、AssetCategory、Profile固定SourceTriangleBand、Status、Process Mode、形状統計、最大2件の固定順Attempts、Resource状態、最終Reject Stage／Reasonを記録するcanonical非公開レポート。Scope外SourceはEntryを作らずSource Catalogだけに保持する。Attempt時間／Peak Working Setは観測値でありDataset hashへ含めない |
| LicensedRepresentativeDatasetIndex | Selected／BenchmarkOnly GeometryだけをTier付きDatasetCaseId順に列挙し、Profile／Source Package／Blender／Script／Preset、Geometry Format／Version／RelativePath／ByteLength／canonical Content hashを固定する非公開Index。Geometry rootの完全な通常file許可リストでもあり、このcanonical bytesのSHA-256をGeometryBenchmarkRunManifest.DatasetContentSha256とする |
| LicensedFixtureSelectionReceipt | SelectionRunId、DatasetId、ReportContentSha256、DatasetIndexContentSha256、DatasetContentSha256を結び、ReportとIndexのcanonical検証後に最後に原子的確定する小さなcommit marker。欠落・不一致時は選抜RunをBenchmarkへ渡さない |
| Preprocess Recipe | Assetごとの包含・除外部品、封鎖、空洞保持、分割、Voxel品質を記述する設定 |
| Preprocess Cache Key | 入力、Recipe、Script、Blender版のハッシュから生成する再構築判定値 |
| Boundary Loop | 片面または開放Meshで、1面だけに属するEdgeが形成する穴の輪郭 |
| BoundaryLoopFill | Phase 0.2の本命簡易封鎖Variant。Object／Topology Component内で面が1枚だけ接続するBoundary Edgeを抽出し、全頂点次数2かつProfile内の閉Loopだけをstable順に個別Fill／三角形化する。分岐やOpen Chainを推測修復しない |
| BlindNonManifoldFill | Poly Pro Universeの人力調査で有効性を確認したBlenderのNon-Manifold選択＋`F`操作を固定Presetで再現するPhase 0.2探索Variant。無条件採用せず、Hard形状偏差とZCG後Gateを通過した結果もBenchmarkOnlyに限定する |
| Voxel Closing | 体積を膨張後に収縮してVoxel数個以下の隙間を閉じる形態学的処理 |
| RenderCutTopologyMap | posed Render Meshの位置とは独立して維持する切断用Topology系譜。TopologyVertexId、OriginalEdgeId、TriangleInstanceId、EdgeUseIdを必須とし、必要ならCutPositionId、VertexFanId、EdgeSheetLaneIdを持つ |
| ContourPortKey | Cap Contourのnodeを空間座標ではなくOriginal Edge／Edge Sheet Lane、またはTopology Vertex／Vertex Fan／Local Portで一意化するKey。同一点にある別surfaceのportを統合しない |
| RenderCutRobustnessProfile | Distance／Length／Area epsilon、Contour／Arrangement／Open Chain／Triangle／byte／時間上限、許可Fallbackを固定するランタイム表示Mesh切断設定。Stencilおよび個別Physics Convexの各Profileとは別 |
| CapConstructionPath | `SimpleContour`、`LocalArrangement`、`BoundaryFan`、`OpenChainBridge`、`DegenerateClosure`、`Uncappable`からなる表示Mesh Cap生成経路と品質診断 |
| Boundary Fan Fallback | closedなTopology Trackの各boundary segmentとplane上anchorから重複を許すTriangle fanを生成し、領域のBoolean正解よりcut-derived Boundaryの封鎖を優先する局所Fallback |
| Cut-local Closure | 切断処理が新設したBoundary Half-edgeをCapへ接続して新しい開口を残さない保証。切断前から離れて存在するBoundaryの修復やGlobal Watertightは含意しない |
| Oriented Closed Triangle Chain | Topology Edgeごとの有向Face incidence総和が0でRaster上のBoundaryを残さないTriangle集合。各Edgeが2 Faceだけに属すること、Manifoldであること、自己交差がないこと、inside／outsideを一意に定められることは含意しない |
| OrientedShellValidator | Stencil Cut Shell Baseのfinite、index／Topology参照、共有Edge position、有向incidence balanceをO(Triangle + Edge)で前処理時に検査するValidator。全Mesh自己交差やinside／outsideは検査しない |
| Topological Watertight | Boundary Edgeがなく各Edgeが規定数のFaceへ接続する閉Topology。自己交差のない3D Solidまでは保証しない |
| Geometrically Valid Solid | Topological Watertightに加え、面向きが整合し、非隣接Faceの自己交差、面反転、退化がなく、内外と体積を一意に扱える形状 |
| Trusted Exterior | 元Render AssetのうちSurface Projection先としてRecipeが許可した外表面。内部面、装飾、合成封鎖面は原則除外する |
| Constrained Surface Projection | Voxel再構成面をTrusted Exteriorへ距離・法線・包含等の条件付きで戻し、失敗頂点をVoxel位置へFallbackする処理 |
| NeedsReview | 自動処理は完了したが意味または品質を保証できず、人間の確認を要求する結果 |

## 19. 飛翔斬撃と未来評価アーキテクチャ

### 19.1 SlashWaveを判定の正本にする

三日月形の斬撃波は、ParticleやVFX Graphの独立した衝突結果ではなく、ゲーム側の粗い折れ線`SlashFront`を判定と表示の共通データとする。振り終わりを待たず、十分な軌道が観測された時点で切断面と初期前縁を早期Latchし、その同じフレームからVFX、飛翔、命中判定を開始する。Latch後に刀の軌道が変化しても既存の切断面、生成済み前縁、確定済み命中を変更・取消せず、後続入力は同じ平面上へ前縁の頂点／辺を追加するか、別のSlashとして扱う。

#### 19.1.1 Gesture状態機械

```text
Idle／NonCutting
  -> Primed       刀速、移動量、方向安定度、Edge Direction Gateが成立
  -> Latched      SlashId、切断面、初期SlashFrontを不可逆に確定し、即時に飛翔・命中開始
  -> Extending    既存前縁を前進させながらSpanAxisへ単調に頂点／辺を追加
  -> Finalized    前縁形状への追加を不可逆に終了。完成前縁の飛翔は継続
  -> Recovery     速度低下、方向反転、またはGate不成立を待つ
  -> Idle
```

Latch前は入力不足またはEdge Direction Gate不成立としてキャンセルできる。Latch時には初期SlashFrontの現在位置で重なり検査も行い、すでに前縁へ接触している対象を即時命中とする。Latch後は切断面を変更せず、振りが弱まる、SpanAxis方向へ明確に逆行する、頂点順序が反転する、または新規辺が既存の非隣接辺と交差する場合は小さい三日月として早期Finalizedする。大きな方向転換や復路は既存Slashを歪めず、Recovery条件を満たした後の新しいSlash候補へ送る。

Finalizedは斬撃波の消滅や衝突停止ではなく、折れ線形状への頂点／辺追加が終わったことを表す。完成したSlashFrontは最大飛距離または寿命まで飛翔・命中判定を継続する。Gesture側はFinalized後にRecoveryへ入り、固定時間だけでなく速度低下、運動方向反転、またはGate不成立を一度確認してから次のSlashを許可する。飛翔中のSlashWaveとGestureのRecoveryは独立状態として併存できる。

切断面はLatchまでの複数Sampleから、刀身長軸と主要な振り方向が張る平面として安定化して求める。概念上は`normalize(cross(bladeAxis, swingDirection))`を法線とするが、単一フレーム差分には依存せず、直近Windowの平均または最小二乗、外れ値除去を使用する。

`SlashFrame`は少なくとも`SlashId`、`SlashGeneration`、`LatchedAt`、`FinalizedAt`、`PlaneOrigin`、`PlaneNormal`、平面内の`SpanAxis`、`TravelAxis`、初期方向を持つ。SpanAxisはLatchまでの主要な振り方向、TravelAxisは同一面内で斬撃波が前進する方向として直交化し、Latch後は固定する。Sample、SlashFront、VFXはこの平面の2D座標で保持・評価し、許容外の面外運動を切断面更新へ使用しない。

#### 19.1.2 動的SlashFrontと三日月VFX

SlashFrontは同一SlashFrame内の粗い折れ線で表し、初期値は三日月全体で4～8辺程度を検証候補とする。各`FrontVertex`は`VertexId`、`CreatedAt`、初期面内位置、移動方向、速度を持ち、隣接頂点間の`FrontEdge`は`FrontEdgeId`と有効化時刻を持つ。表示側は同じ頂点列から三日月を構築し、視覚上の前縁と判定上の折れ線を一致させる。

```text
FrontVertex[0] -- FrontEdge[0] -- FrontVertex[1] -- ... -- FrontVertex[n]
```

Latchedでは初期頂点／辺を生成して即時に飛翔させ、現在位置での重なり検査を行う。Extendingでは既存頂点を止めずに前進させながら、観測された振りの続きを折れ線の端へ追加する。新しい頂点／辺は`CreatedAt`より前の位置や衝突を持たず、既存頂点、既存辺、命中履歴を再サンプルや形状補正で巻き戻さない。

現在位置の細い線だけを調べると高速飛翔時に対象を通り抜けるため、各FrontEdgeについて前フレーム位置と現在位置が張る四辺形または細いプリズムをSweepする。判定には数cm程度の厚みを持たせ、頂点近傍は円／球状領域で接続して隙間を防ぐ。辺が距離または角度上限を超える場合は中間頂点を追加し、粗い折れ線のままVFXとの誤差と誤命中を制御する。

後から離れた点を1本の長い辺で結ぶと、生成瞬間に広い領域を誤命中させるため、追加距離と角度に上限を設ける。新しい辺は有効化された現在時刻の重なりだけを検査し、それ以前のSweepを生成しない。一度FrontEdgeで命中した対象は、後続の軌道変更やFinalizedを理由に未命中へ戻さない。

#### 19.1.3 U字折返しと前縁一価制約

刀の投影軌跡を時間順にそのまま折れ線化すると、U字の折返しで同じ横位置に前後2本の前縁が生じる。これを避けるため、SlashFrontをSpanAxis位置`u`に対してTravelAxis上の前進位置`v`が高々1つとなる粗い曲線として扱う。

```text
u = dot(projectedSample - PlaneOrigin, SpanAxis)
v = dot(projectedSample - PlaneOrigin, TravelAxis)
SlashFront: v = FrontDistance(u)
```

新しいSampleの`u`が直前に採用した値から許容幅を超えて正方向へ進む場合だけ、頂点／辺の追加候補とする。小さな負の差は手ぶれとして無視し、距離・角度・継続時間のいずれかが逆行閾値を超えた場合は現在SlashをFinalizedする。折返し後の復路を同じSlashFrontへ追加せず、RecoveryとEdge Direction Gateを通過した場合だけ別Slashとして生成する。

実装ではSpanAxisを8～16程度の粗いbinへ分け、未放出の追加候補について各binに前進位置を1つだけ保持する方法を初期候補とする。同一binに複数候補がある場合はTravelAxis方向で最も前の候補を残せるが、すでに生成・放出したFrontVertex／Edgeは置換または後退させない。

新規辺の採用前に、Span順序の反転、非隣接辺との2D交差、鋭い折返し、距離上限を検査する。違反時は辺を追加せず、理由をTraceしてFinalizedする。U字全体の凸包や外周を当たり判定にすると刀が通っていない内側まで切るため使用しない。前縁の整形は、まだ放出していない候補の間引きと長辺の分割だけに限定する。

飛翔更新でも頂点の移動によってSpan順序が反転したりFrontEdge同士が交差したりしないことを不変条件とする。頂点ごとの拡散方向を使う場合は、この順序を保つ移動則に制限する。不変条件を満たせない更新は前回の有効形状を維持して異常をTraceし、既存命中を取消さない。

#### 19.1.4 早期候補列挙と投機切断

切断面はLatch時点で不変になるため、最終的なSlashFront形状が未確定でも、切断面の厚み、最大飛距離、設計上許容する最大前縁範囲から`Candidate Flight Bounds`を作り、遠距離候補をBroadphase列挙できる。これは投機計算用の保守的Boundsであり、当たり判定には使用しない。候補への表示Mesh／Convex切断はLatchを依存条件として投機開始し、実際のFrontEdge Sweepによる命中だけをCommit Gateの条件とする。

```text
刀軌道Sample
  -> Slash Latch
       ├-> 初期SlashFront生成・飛翔・重なり検査 -> 近距離即時表示
       ├-> Candidate Flight Bounds候補列挙 -> 投機Mesh／Convex切断
       └-> Extending中も既存前縁を前進 + 頂点／辺追加
                              └-> FrontEdge Sweep実命中 -> Commit Gate
       -> Finalizedで形状追加終了 -> 完成前縁は飛翔継続
```

候補へ列挙されてもSlashFrontが実際に命中しなければ成果物を破棄する。実接触時に`FrontEdgeId`、対象位置、回転、Animation状態、対象世代を検証し、一致すれば完成成果物をコミットする。未完成または予測不一致なら即時切断レンダラへフォールバックし、実姿勢から後追い計算する。

利用可能な計算猶予は概ね「Latch後に残るExtending時間＋SlashFrontの飛翔時間」である。近距離では初期前縁の即時命中による低遅延を優先し、遠距離ほど長い猶予を投機切断へ利用する。先行評価は総計算量を消さないため、Candidate Flight Boundsの候補数上限、PriorityClass内の締切順Queue、進路外となった未Schedule候補の取消を必須とする。V1では命中確率を順位へ混ぜずCandidate列挙／受付前Filterにだけ使う。Schedule済みJobは中断せず、完了後にGeneration／前提検証で破棄する。

#### 19.1.5 Quest Grip Poseと片刃方向Gate

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

SideNormal方向への横滑り量や理想平面からの角度は合格条件にしない。多少刀が寝る、手首が傾く、斜めに振る場合も、刃側が概ね先行すれば切断を許可する。同じ向きの刀を戻すと速度だけが逆転してScoreが負になるため、新しいSlashを生成しない。プレイヤーが刀を返して刃を新しい運動方向へ向ければ、Recovery解除後に次のSlashを生成できる。

刀の表示Objectには物理反発するColliderを持たせない。切断はEdge Direction Gate成立中の独自Swept Volume Queryだけで検出し、NonCutting、Primed不成立、Recovery中はSweepを生成しない。したがって切れない状態の刀は地形、プロップ、NPCを完全に素通りする。切断可能時も刀を物理的に引っ掛けず、論理Hit、VFX、音、Hapticsだけを発生させる。

Tracking StateでPositionまたはRotationが無効になった場合はPrimedとSample履歴を破棄し、復帰直後は新しいWindowが蓄積するまでLatchしない。復帰前後を結ぶ見かけ上の巨大速度を斬撃として採用せず、速度・角速度の異常上限も設ける。すでにLatchedされ刀から独立して飛翔中のSlashWaveは追跡喪失後も継続する。

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
| Deterministic | 静止物、確定済み切断面から作る幾何成果物 | `HitConfirmed`、Slash／FrontEdge／SlashFrame、BaseObjectGenerationの一致 |
| Conditional | 既知Animation、単純運動、確定済みMobPlan | Deterministic条件に加え、Animation／PlanGeneration／予測前提の一致 |
| Speculative | 衝突中Rigidbody、外乱可能な対象 | Deterministic条件に加え、実接触時の姿勢・Physics状態照合に合格 |

メインスレッドはUnity状態を数値データへスナップショットし、Job SystemとBurstは予測、頂点分類、交差、断面生成を行う。UnityのGameObject、Transform、Animatorをワーカージョブから直接操作しない。完成Mesh、Collider、Rigidbodyの適用はCommit Controllerがメインスレッドまたは物理ステップ境界で行う。

### 19.3 未来姿勢の求め方

| 対象状態 | 予測方法 |
| --- | --- |
| 静止 | 現在姿勢を採用 |
| 自由飛行・単純重力 | 位置、速度、角速度から解析予測 |
| 既知またはMobPlanで確定したAnimation | 対象`FixedStepId`の副作用のない`ExplicitAnimationState`を解決し、交換可能なPose Evaluatorで任意時刻Poseを生成 |
| 接触・転動・Jointあり | 局所Prediction Physics |
| ユーザー／スクリプト依存 | 入力を複製できる範囲だけ投機評価 |

Current／Future Animationの意味上の正本は、ゲーム側が保持する副作用のない`ExplicitAnimationState`とGlobal `FixedStepId`である。`Animator`、`AnimatorController`、`AnimatorControllerPlayable`の内部State、Clock、Trigger、Transition、BlendをAnimation Planへ読み戻さず、稼働中Controllerを未来へ進めたり巻き戻したりしない。Future Pose `T+n`を得るためにControllerを`T+1 ... T+n-1`へ逐次rolloutする方式を標準経路にしない。

`FutureAnimationPoseEvaluator`は対象`FixedStepId`、そのStepへ解決済みのimmutableな`ExplicitAnimationState`、対象Rig／Animation Asset Set／Evaluation ProfileのIdentityを一体化した`ResolvedAnimationPoseInput`を受け、canonical Bone順のRig Pose Bufferを出力する。裸のState、別Stepへ属するState、Catalog Identity不一致を受理せず、Evaluator自身は`PlaybackRate`や現在時刻からPhaseを追加進行しない。入力Stateと現在Sceneを変更せず、同じ入力とBackendでは要求順に依存しない同じPoseを生成する。内部Cacheや前処理は許可するが、任意時刻評価に必要な履歴をEvaluatorの隠れた可変状態へだけ保持しない。履歴依存方式を後から導入する場合は、Transition元、Source Time、Blend、Foot／Inertialization履歴要約等を明示StateまたはPlanへ含める。

共通Pose Evaluatorは意味境界であり、全BackendをBurst Jobから呼べるとは仮定しない。Pose Table／custom samplerは固定長Bufferを使うJob Batch候補、Unity Playable／Animator出力はpool済みGraphを使うMain Thread予算対象としてSchedulerがBackend別にRoutingする。Playable評価をWorkerへ偽装したり、候補ごとのGameObject／Graph生成、Main Threadの無制限Evaluateを行わない。

AnimatorコンポーネントはHumanoid Retargeting、Avatar Binding、Playable出力先、現在表示のための任意Backendとして使用できるが、上位Stateのauthorityではない。標準V1はゲーム側Stateからcontrollerなしの`AnimationClipPlayable`／Mixer、または事前Bake済みPose Tableへ明示Source Time／Weightを渡す。AnimatorController／AnimatorControllerPlayableはLegacy Bridge、Editor Preview、比較Probeへ隔離し、削除してもMobPlan、未来評価、切断Predictionの公開契約を変更しない。

現在表示と未来評価は同じ対象Stepへ解決済みの`ExplicitAnimationState`から分岐する。Near Mobの表示Backendも独自にClip遷移やPhase進行を決めず、ゲーム側Stateを消費する。V1予測対象NPCではLook、腕IK、Foot IK等のプロシージャルPose Layerと左右反転を双方で無効化し、現在表示だけに適用しない。後段で再導入する場合は、Layer入力Snapshot、weight／MirrorMode、適用順、Generation、Identityを`ResolvedAnimationPoseInput`へ加え、全Backendで同じ意味を適用する。命中時に実際のBone Poseをスナップショットして最終証拠とする既存規則は維持し、予測State、Root Pose、代表骨Pose、Plan／Asset Identityが許容範囲外なら成果物を破棄して実姿勢から通常の後追い切断へ戻す。

### 19.4 局所Prediction Physics

通常世界とは別の`PhysicsScene`に、対象Rigidbody、到達までに接触し得る近傍Rigidbody、周辺静的Collider、Joint、必要な外力からなる局所物理島を複製する。固定時間刻みで斬撃波の到達予定時刻まで手動シミュレーションし、その未来姿勢から切断を開始する。

- 静止・解析予測で足りる対象はPhysicsSceneへ入れない。
- 予測シーンはプールし、同じ斬撃波の候補間で共有する。
- 未来ステップは複数フレームへ分散し、スパイクを避ける。
- ユーザー介入、範囲外衝突、スクリプト外力、Animation遷移、別切断を無効化要因として記録する。
- 完全な決定性に依存せず、実接触時に位置差、回転差、対象・Mesh・Physics・Animationの各Generationを照合する。

### 19.5 スケジューリングとCommit

初期優先度は4.4の固定`PriorityClass -> Deadline -> EnqueueSequence`だけで決める。未完了依存はDAG CoordinatorがReady投入前に解決し、推定費用はDispatchBudgetの粗い控除、命中確率は受付前Filter、一時描画費用はProfiler Counterとして保持する。これらを単一の動的Scoreへ合成するのは実測後のV2候補とする。遠距離候補はBackground枠の空き時間で処理し、近距離候補はDeadlineを優先する。

投機ジョブは`SlashId`、`SlashGeneration`、命中した`FrontEdgeId`、確定した`SlashFrame`、`ObjectId`、`BaseObjectGeneration`、表示Mesh・物理・Animation・MobPlanの各Generation、予測到達時刻を保持する。Commitには対応するFrontEdge Sweepの`HitConfirmed`を必須とし、識別子、切断面、世代、予測前提のいずれかが一致しない結果は適用せず回収する。これにより、Candidate Flight Boundsへ入っただけの空振り候補や、古い非同期結果が新しい切断状態を上書きすることを防ぐ。

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

斬撃波候補がモブへ到達する時刻を`MobPlan`上でサンプルし、予測姿勢のSkinned Mesh焼き込み、切断面適用、骨Physics Proxy分類を先行できる。成果物は`MobId`、`PlanGeneration`、`ObjectGeneration`、Animation状態、予測姿勢を保持し、実命中時にすべて検証する。計画が維持されていれば遠距離ほど完成済み成果物を再利用でき、介入で計画が変わった場合は即時レンダラと実姿勢からの後追い処理へ戻る。

計画生成自体がフレーム予算を圧迫しないよう、Mob Future PlannerもFuture Evaluation SchedulerのWork Itemとして実行する。近距離で命中Deadlineを持つ姿勢生成は`NearDeadlinePrediction`、遠距離MobPlanの延長は`BackgroundMaintenance`へ固定し、`CriticalPhysicsSafety`／`ConfirmedPhysics`／`ConfirmedGeometry`より先にScheduleしない。

### 20.6 無効化と観測

主な無効化要因は、プレイヤーの介入可能領域への侵入、NavMesh変更、経路上の新障害、Behaviorの高優先Intent、Animation遷移、外力、対象の切断である。予約機能を導入した後は別モブとの予約競合も無効化要因へ加える。`MobPlanCreated`、`MobPlanExtended`、`MobTierChanged`、`MobPlanInvalidated`、`MobReplanned`、`MobPredictionUsed`、`MobPredictionRejected`をTraceへ記録し、`ReservationCreated`は予約機能導入後にだけ記録する。`MobId`と`PlanGeneration`から依存Taskを辿れるようにする。

V1の無効化粒度は単一Mob Planまたは固定Mob Group全体だけとし、影響依存を厳密に解析しない。無効化は`PlanGeneration`を進め、未再生Sample、未来Skeleton Pose、依存する投機的切断成果物を同じ世代検証でStaleにする。Player Bodyはプロップ等と非接触であるため、単なるPlayer Physics Contactを無効化要因に要求せず、攻撃、介入領域、Script Intent、Path変更、対象切断などゲーム側で観測可能なEventを正本とする。

### 20.7 段階導入とFuture Works

Phase 4.7の最初のPlayable実装は、固定ステップ二相更新、Waypoint／Lane Desired Motion、固定長未来Queue、Rootと`ExplicitAnimationStateV1`全体の再生補間、Loop／Clamp Clip Catalog、移動距離由来Phase、粗い世代無効化、既存Dispatcherへの補充投入までとする。V1予測対象NPCではプロシージャルLook／腕／Foot IKと左右反転を現在／未来の双方で無効化する。この段階ではモブ同士の多少の重なり、遠方Mobの短時間停止、全Plan／Group再計算、Clip hard switchのPose popを許容し、ORCA、細粒度依存解析、Pose Layer／Mirror再導入を正しさの条件にしない。

重なりがプレイ上またはT-092の実測で問題になった場合だけ、次段として固定容量Uniform Grid、固定Cell走査順、`MaxNeighbors`、固定順Constraintを持つbounded ORCAを同じ`MobTrajectoryKernel`のDesired Motion後段へ追加する。ORCA追加後もFar／Dormantへ完全な群衆衝突を必須にせず、Tierごとに無効化できる。Grid／Neighbor／作業領域の容量超過ではMobを黙って省略せず、その計画GroupをORCAなしのLane追従またはHoldへ固定的に低下させる。

空間／Mob Group Chunk、Active／Candidate Interaction記録、Reverse Dependency DAG、Tick単位の部分再計算、新規Interaction用Guard Band、Flow Field、軌道圧縮はFuture Worksとする。これらは再計算量を減らす最適化であり、欠落しても全Plan／Group単位の再生成で正しく動作する。Flow Fieldを追加する場合も`DesiredMotionProvider -> MobTrajectoryKernel`境界だけへ接続し、ORCAや未来QueueがPath実装を直接参照しない。

## 21. 観測・トレース設計

### 21.1 目的と責務分離

再現困難な競合、世代不一致、予測の無効化、古い成果物のCommitを調査できるよう、観測基盤をPoC開始時から実装する。性能と因果関係は別の情報として記録し、同じ時刻・フレーム・相関IDで突き合わせる。

| 層 | 主目的 | 主な出力 |
| --- | --- | --- |
| Unity Profiler | CPU／Job／GPU時間とスレッド実行 | 固定名ProfilerMarker、Counter、Flow Event |
| Domain TraceLogger | ゲーム状態と非同期処理の因果関係 | Slash／Object／MobPlan／各Generation／Taskの状態イベント |
| Editor Timeline | Traceの検索と時系列表示 | レーン、期間、状態色、詳細、Hierarchy連携 |
| Flight Recorder | 再現不能バグの証拠保全 | 異常前後の固定長Traceと実行条件 |
| Visual Capture | 状態色を含むアプリ描画とTraceの対応 | Unity選択録画、異常時静止画、後期OpenXR Projection Capture |

Deep Profileの常用には依存せず、明示的な軽量計測を基本とする。

### 21.2 Unity Profiler計測

`ProfilerMarker`は処理種類ごとの固定名とし、IDを名前へ埋め込まない。初期マーカーは以下を基本とする。

```text
Zantetsu.Slash.CandidateSearch
Zantetsu.Slash.FrontAdvance
Zantetsu.Slash.FrontSweep
Zantetsu.Slash.TopologyValidate
Zantetsu.Future.PredictPose
Zantetsu.Physics.Predict
Zantetsu.Mesh.Classify
Zantetsu.Mesh.BuildCap
Zantetsu.Convex.Slice
Zantetsu.Physics.ProvisionalBuild
Zantetsu.Physics.ProvisionalConstraint
Zantetsu.Physics.ProvisionalHandoff
Zantetsu.Physics.ProvisionalFaultFreeze
Zantetsu.Support.Classify
Zantetsu.Support.Reachability
Zantetsu.Support.CommitValidate
Zantetsu.Commit.Validate
Zantetsu.Commit.Apply
Zantetsu.Trace.Drain
Zantetsu.Capture.Copy
Zantetsu.Capture.Encode
```

論理Work ItemのSchedule、Job開始、完了、CommitをProfiler Flow IDで結び、CPU Profiler Timeline上でスレッドをまたぐ依存関係を確認できるようにする。集計値にはProfilerRecorderまたはカスタムCounterを使用する。

### 21.3 Trace Event形式

Trace Eventは固定サイズを基本とし、ホットパスでは文字列、例外、可変長オブジェクトを保持しない。

```text
Timestamp / FrameId / FixedStep / ThreadId
SlashId / SlashGeneration / FrontEdgeId / ObjectId / ObjectGeneration
MobId / PlanGeneration / TaskId
CaptureFrameId / OpenXRFrameId / TestRunId
EventType / TaskType / FromState / ToState / Reason
Value0 / Value1
```

最低限記録するイベントは、`BladeTrackingLost`、`BladeTrackingRestored`、`BladeSamplesReset`、`EdgeGateEntered`、`EdgeGateRejected`、`SlashPrimed`、`SlashLatched`、`SlashFrontCreated`、`FrontVertexAdded`、`FrontEdgeActivated`、`FrontSampleIgnored`、`FrontTopologyRejected`、`SlashFinalizedByReversal`、`SlashFinalized`、`SlashFrontExpired`、`SlashRecoveryStarted`、`SlashRearmed`、`FrontHitConfirmed`、`CandidateDetected`、`TaskScheduled`、`TaskStarted`、`TaskCompleted`、`PredictionValidated`、`PredictionRejected`、`GenerationChanged`、`MobPlanCreated`、`MobPlanExtended`、`MobTierChanged`、`MobPlanInvalidated`、`MobReplanned`、`MobPredictionUsed`、`MobPredictionRejected`、`CaptureFrameQueued`、`CaptureFrameEncoded`、`CaptureFrameDropped`、`CaptureRingFrozen`、`ProjectionCaptureCopied`、`CommitStarted`、`CommitSucceeded`、`CommitRejected`、`FallbackActivated`、`TaskCancelled`、`ResultDisposed`とする。予約機能実装時には`ReservationCreated`をappend-onlyで追加する。支持判定実装時にはappend-onlyで`SupportClassificationPending`、`SupportClassificationRetried`、`SupportClassificationTimedOut`、`SupportClassified`、`AnchoredSplitStarted`、`AnchoredSplitCommitted`、`CutBoundaryDormant`、`CutBoundaryActivated`、`CutBoundarySuppressed`、`SupportResultRejected`、`SupportFallbackActivated`、`LogicalCutOperationCreated`、`LogicalCutOperationChildLinked`、`LogicalCutOperationBoundaryLinked`、`LogicalCutOperationBoundaryEndpointLinked`、`LogicalCutOperationTraceCompleted`、`OperationSupportStateChanged`、`FullyFixedCullInvalidated`、`LogicalCutOperationRejected`を追加する。Trace完全性実装時には`TraceIntegritySummary`をappend-onlyで追加する。Render／Convex対応実装時には`FragmentPhysicsRepresentationClassified`、`FragmentConvexMappingEdge`、`FragmentSharedRoleAssigned`、`FragmentDebrisPromoted`、`FragmentDebrisRejected`、`FragmentPhysicsFallbackActivated`、`SharedConvexResolutionFinished`をappend-onlyで追加する。Runtime Arena実装時には`RuntimeDebrisSliceAllocated`、`RuntimeDebrisSliceActivated`、`RuntimeDebrisSliceRetiring`、`RuntimeDebrisSliceReclaimed`をappend-onlyで追加する。Provisional物理実装時には`ProvisionalPhysicsCommitted`、`ProvisionalPhysicsFallbackActivated`、`ProvisionalPhysicsFinalized`、`ProvisionalPhysicsSafetyFrozen`をappend-onlyで追加する。Safety Tether実装時には`SafetyTetherTreeRebuilt`、`SafetyTetherEdgeLinked`、`SafetyTetherTreeTraceCompleted`、`SafetyTetherCommitRejected`、`SafetyFrozenEntered`、Player非接触移動実装時には`PlayerLocomotionRejected`をappend-onlyで追加する。既存Event名の`Task`は論理Work Itemを指し、`TaskId`をFragment識別子へ流用しない。`TaskCancelled`は原則としてSchedule前の取消または取消可能なI/O処理にだけ使用し、Schedule済みJobの不採用は`PredictionRejected`／`CommitRejected`と`ResultDisposed`で表す。

`RenderFragmentLocalId`と`LogicalConvexFragmentLocalId`は0を未設定用に予約した正の32bit `int`とし、`ObjectId + ObjectGeneration`をスコープとして種別ごとに一意かつ同一世代内で再利用しない。`SharedGroupLocalId`も0を未設定用に予約した正の32bit `int`とし、同じ`ObjectId + ObjectGeneration`内で一意かつ、連結成分の解体後も同一世代内では再利用しない。`CutOperationId`、`LogicalFragmentLocalId`、`CutBoundaryLocalId`、`SafetyTetherEdgeLocalId`は0を未設定用に予約した正の32bit `int`とし、`ObjectId`の生存期間全体で種別ごとに一意かつ非再利用とする。`LogicalCutOperationCreated`の共通`ObjectGeneration`は`ParentObjectGeneration`、`Value1`は作成時`SupportGraphGeneration`を格納し、どちらも宣言型`uint`の全域を許可する。その他のOperation系Eventの共通`ObjectGeneration`はEvent発生時の現世代を記録する。`SafetyTetherTreeGeneration`と`StructuralSplitGeneration`も`uint`全域を許可し、同じ`ObjectId`と組み合わせてTree／分裂履歴を復元する。Traceの`ObjectId`と各Cut／Tether系LocalId、または`ObjectId`／`ObjectGeneration`とRender／Convex系LocalIdを組み合わせて対象を一意に復元する。doubleへ格納するID、序数、件数は非負int範囲、Generationは`uint`全域とし、いずれもIEEE 754 binary64で整数精度を失わない。イベント別の固定フィールド割当は次を正本とし、汎用的なFrom／To State遷移と混同しない。

| EventType | FromState | ToState | Reason | Value0 | Value1 |
| --- | --- | --- | --- | --- | --- |
| `CaptureFrameAdmissionRejected` | 0 | 0 | `None` | `CaptureFrameAdmissionRejectKind` | `FrameDraftRegistryFull(5)`。共通CaptureFrameIdは0 |
| `CaptureFrameDropped`（通常Draft理由6～8） | `Pending(0)` | `Dropped(2)` | `None` | 0 | `PngEncodeFailed(6)`／`PngStagingStoreFull(7)`／`CaptureCancelled(8)`。共通fieldは対応Draft Trace Contextから転記 |
| `CaptureFrameDropped`（freeze terminal理由9） | `Pending(0)` | `Dropped(2)` | `None` | 0 | `FreezeDrainTimeout(9)`。通常Queueを通さずterminal Bufferへだけ構築 |
| `LogicalCutOperationCreated` | ParentLogicalFragmentLocalId | 初期`OperationSupportState` | `None` | CutOperationId | SupportGraphGeneration。ParentObjectGenerationは共通ObjectGeneration |
| `LogicalCutOperationChildLinked` | DirectChildLogicalFragmentLocalId | 初期`SupportState` | `None` | CutOperationId | 0始まりChild序数 |
| `LogicalCutOperationBoundaryLinked` | CutBoundaryLocalId | 初期`ExposureState` | `None` | CutOperationId | 0始まりBoundary序数 |
| `LogicalCutOperationBoundaryEndpointLinked` | CutBoundaryLocalId | DirectChildLogicalFragmentLocalId | `None` | CutOperationId | EndpointSlot。正側=0、負側=1 |
| `LogicalCutOperationTraceCompleted` | DirectChildCount | CutBoundaryCount | `None` | CutOperationId | 当該Operation作成Trace束の期待Event数 |
| `OperationSupportStateChanged` | 変更前`OperationSupportState` | 変更後`OperationSupportState` | `None` | CutOperationId | DirectChildCount |
| `FullyFixedCullInvalidated` | 0 | 1 | `None` | CutOperationId | `CullInvalidationTrigger` |
| `LogicalCutOperationRejected` | 0 | 0 | `InvalidLogicalCutOperation` | 割当済みなら候補CutOperationId、割当前なら0 | `LogicalCutOperationValidationError` bit mask |
| `TraceIntegritySummary` | 現Runの`TraceIntegrityState` | TraceCaptureOverflowCount（0～`int.MaxValue`） | 現Run完全なら`None`、enqueue失敗なら`TraceWriteFailureObserved`、それがなくcapture容量超過なら`TraceCaptureOverflowObserved` | SealedTraceEnqueueFailureCount | PriorBundlePublishFailureCount。監査専用で現Run完全性には不使用 |
| `FragmentPhysicsRepresentationClassified` | RenderFragmentLocalId | PhysicsRepresentationStatus | 正常時`None`、不変条件違反時は専用Reason | 推定被覆率 | 対応Convex数 |
| `FragmentConvexMappingEdge` | RenderFragmentLocalId | LogicalConvexFragmentLocalId | `None` | Overlap／包含Score | SharedGroupLocalId |
| `FragmentSharedRoleAssigned` | RenderFragmentLocalId | SharedResolutionRole | 正常時`None` | KeeperのRenderFragmentLocalId。Keeper自身は自身、未決定は0 | SharedGroupLocalId |
| `SharedConvexResolutionFinished` | SharedGroupLocalId | SharedConvexResolutionOutcome 1～6。Invalidは禁止 | `Resolved`では`None`、2～6はOutcome対応の専用Reason | GJK最近接距離。未取得／CapacityExceeded／Supersededは0 | 入力Shared Convex数。共通ObjectGenerationはSlot予約前Admission CandidateのTargetObjectGeneration。共通TaskIdは受付済みRequestのTaskId、SlotなしCapacityExceededだけ0。StaleBeforeAdmissionはEvent自体を生成しない |
| `ProvisionalPhysicsCommitted` | Commit前`FragmentGroupPhysicsState` | `ProvisionalPhysicsSplit`または`ProvisionalAnchoredSplit` | `None` | CutOperationId | 公開Provisional Actor数 |
| `ProvisionalPhysicsFallbackActivated` | 構築試行時の実`FragmentGroupPhysicsState` | FromStateと同じ実状態。Provisional未公開なので状態遷移を表さない | 専用Fallback Reason。`None`禁止 | CutOperationId | 要求した`ProvisionalPhysicsSplit(5)`または`ProvisionalAnchoredSplit(6)`。その他は禁止 |
| `ProvisionalPhysicsFinalized` | `ProvisionalPhysicsSplit`または`ProvisionalAnchoredSplit` | `StableFastCook` | `None` | CutOperationId | Final Actor数 |
| `ProvisionalPhysicsSafetyFrozen` | `ProvisionalPhysicsSplit`または`ProvisionalAnchoredSplit` | `ProvisionalFaultFrozen` | Primary `ProvisionalRuntimeFaultReason`。`None`禁止 | CutOperationId | `ProvisionalFaultContainmentDisposition`。`Invalid`禁止 |
| `FragmentDebrisPromoted` | RenderFragmentLocalId | PhysicsRepresentationStatus | `None` | Triangle数 | 推定体積 |
| `FragmentDebrisRejected` | RenderFragmentLocalId | PhysicsRepresentationStatus | Reject理由。`None`禁止 | Reasonが示す測定値／Score | 比較閾値 |
| `FragmentPhysicsFallbackActivated` | RenderFragmentLocalId | PhysicsRepresentationStatus | Fallback理由。`None`禁止 | 対応Convex数 | Fallback種別 |
| `RuntimeDebrisSliceAllocated` | RenderFragmentLocalId | `RuntimeDebrisSliceState.Allocated` | `None` | DebrisEventId | 割当Byte数 |
| `RuntimeDebrisSliceActivated` | RenderFragmentLocalId | `RuntimeDebrisSliceState.Active` | `None` | DebrisEventId | 0（予約） |
| `RuntimeDebrisSliceRetiring` | RenderFragmentLocalId | `RuntimeDebrisSliceState.Retiring` | `None` | DebrisEventId | 0（予約） |
| `RuntimeDebrisSliceReclaimed` | RenderFragmentLocalId | `RuntimeDebrisSliceState.Reusable` | `None` | DebrisEventId | Retiringから回収までのFrame数 |
| `SafetyTetherTreeRebuilt` | Synthetic Ground Root = 0 | TreeNodeCount。Synthetic Rootを除く固定＋動的Fragment数 | `None` | SafetyTetherTreeGeneration | EdgeCount |
| `SafetyTetherEdgeLinked` | 親Node ID。Synthetic Ground Rootは0、それ以外はLogicalFragmentLocalId | 子LogicalFragmentLocalId | `None` | SafetyTetherTreeGeneration | SafetyTetherEdgeLocalId |
| `SafetyTetherTreeTraceCompleted` | TreeNodeCount | EdgeCount | `None` | SafetyTetherTreeGeneration | 期待Event数=`2 + EdgeCount` |
| `SafetyTetherCommitRejected` | 0 | 0 | 専用Reject理由。`None`禁止 | 候補SafetyTetherTreeGeneration | 候補Edge数 |
| `CommitRejected`（Structural Split世代枯渇） | ParentLogicalFragmentLocalId | 0 | `StructuralSplitGenerationExhausted` | 現StructuralSplitGeneration=`uint.MaxValue` | 候補DirectChildCount |
| `SafetyFrozenEntered` | LogicalFragmentLocalId | 1 | 専用Fallback理由。`None`禁止 | SafetyTetherTreeGeneration | StructuralSplitGeneration |
| `PlayerLocomotionRejected` | `PlayerLocomotionPolicy` | 0 | `None` | 次姿勢の侵入深度 | 現姿勢の侵入深度 |

`PlayerLocomotionRejected`の侵入深度は同一の`PlayerLocomotionOccupancy`と距離単位で評価したfiniteかつ0以上のbinary64とする。拒否判定に用いた現姿勢と候補次姿勢の値をそのまま格納し、表示Mesh Triangleや旧Colliderから事後再計算した値へ置き換えない。

Safety Tether Treeの成功Traceは、同じ`ObjectId`について成功束3種だけを抽出した順序において、`SafetyTetherTreeRebuilt`、`SafetyTetherEdgeLocalId`昇順の`SafetyTetherEdgeLinked`を厳密に`EdgeCount`件、`SafetyTetherTreeTraceCompleted`の固定束として連続させる。`Rebuilt`と`TraceCompleted`のGeneration、TreeNodeCount、EdgeCountは完全一致し、両者の期待Event数は`2 + EdgeCount`と一致しなければならない。Synthetic Ground RootはFragmentではなく親Node ID 0だけで表し、子ID 0を禁止する。全LinkのGenerationは束のGenerationと一致し、Edge IDと子IDはそれぞれ正かつ束内一意、親IDは0または同じ束のNode集合に含まれる値とする。全Nodeは子として厳密に1回現れ、親0のLinkはTopology専用Root Linkとして1件以上存在し、全Nodeが親Linkを辿って0へ到達し、Cycleがなく、`TreeNodeCount == EdgeCount`であることを検証する。物理`SafetyTetherLevel`はRoot Linkを数えず、復元したTreeで最初の動的子へ入るEdgeを0として決定論的に再計算し、Traceへ重複保存しない。欠落、重複、余分なLink、順序違反、未知Node、件数不一致、Generation混在、未完了束は`IncompleteSafetyTetherTreeTrace`としてTimeline表示だけに留め、状態再現、Golden比較、T-087合格根拠に使用しない。

`SafetyTetherTreeGeneration`はTreeの構築と物理Commitが成功した場合だけゲーム状態の正規Generationとして原子的に公開する。成功後のTrace束はbest effortの観測記録であり、途中のTrace enqueue失敗で公開済みTreeを巻き戻さないが、そのRunを不完全として完全な束がないGenerationをTrace再現・Golden比較へ使用しない。`SafetyTetherCommitRejected`の`Value0`は試行した候補Generationであり、それ自体は現行Generationを進めず、対応する`Rebuilt`／`TraceCompleted`成功束の代用にならない。同じ数値を再試行後に正規化する場合も、後続する完全な成功束だけがTrace上の成功証拠となり、Timelineは直前のRejectを成功履歴へ読み替えない。現Generationが`uint.MaxValue`でTree変更を拒否した場合は`Reason=SafetyTetherGenerationExhausted`、`Value0=uint.MaxValue`とし、存在しない`MaxValue + 1`を表現しない。

Capture Draft Registry実装時には`CaptureFrameAdmissionRejected`を`TraceEventType`へappend-onlyで追加する。これはID発行前の受付拒否専用であり、正のIDを発行済みの処理だけを表す`CaptureFrameDropped`と同じID相関として解釈しない。共通`CaptureFrameId=0`、`Value0=CaptureFrameAdmissionRejectKind`、`Value1=FrameDraftRegistryFull(5)`へ固定する。`CaptureFrameAdmissionRejectKind`は固定値`None=0`、`PendingLimit=1`、`RunEntryLimit=2`とし、0および未知値をEvent生成時にRejectする。

支持判定のReasonには`AnchorGenerationMismatch`、`SupportGraphGenerationMismatch`、`SupportClassificationUnavailable`、`SupportConnectivityAmbiguous`、`InvalidLogicalCutOperation`を追加し、Trace完全性には`TraceWriteFailureObserved`と`TraceCaptureOverflowObserved`を追加する。Render／Convex対応とRuntime Debrisには`FragmentCoverageBelowThreshold`、`FragmentMappingAmbiguous`、`FragmentSharedKeeperUnavailable`、`FragmentProtectedByImportance`、`FragmentProtectedBySize`、`FragmentGenerationMismatch`、`InvalidPhysicsRepresentationState`、`InvalidSharedConvexResolutionOutcome`、`UnseparableBySinglePlane`、`SharedConvexResolutionIndeterminate`、`SharedConvexSplitValidationFailed`、`SharedConvexResolutionSuperseded`、`SharedConvexResolutionCapacityExceeded`、`RuntimeDebrisArenaFull`、`RuntimeDebrisFenceUnavailable`、`RuntimeDebrisUploadRejected`を追加する。Provisional公開前Fallbackには`ProvisionalActorCapacityExceeded`、`ProvisionalShapeCapacityExceeded`、`ProvisionalConstraintCapacityExceeded`、`ProvisionalGeometryShareUnsupported`、`ProvisionalShapeClassificationInvalid`、`ProvisionalMassApproximationInvalid`、`ProvisionalActorCreationFailed`、`ProvisionalConstraintCreationFailed`、`ProvisionalGenerationMismatch`、`ProvisionalAtomicCommitFailed`を追加し、`ProvisionalPhysicsFallbackReason` 1～10へ同順で一対一対応させる。Provisional公開後Faultには`ProvisionalNonFiniteActorState`、`ProvisionalConstraintRuntimeFailed`、`ProvisionalLinearVelocityLimitExceeded`、`ProvisionalAngularVelocityLimitExceeded`を追加し、`ProvisionalRuntimeFaultReason` 1～4へ同順で一対一対応させる。Snapshot不在と封じ込め検証失敗はTraceReasonにせず、`ProvisionalPhysicsSafetyFrozen.Value1`の`ProvisionalFaultContainmentDisposition` 2／3へ格納する。Safety Tether／大型分裂には`SafetyTetherCycleDetected`、`SafetyTetherGroundRootMissing`、`SafetyTetherAnchorAmbiguous`、`SafetyTetherGenerationMismatch`、`SafetyTetherGenerationExhausted`、`StructuralSplitGenerationExhausted`、`SafetyTetherBudgetExceeded`を追加する。いずれも既存`TraceReason`の次の未使用値へappend-onlyで明示値を割り当て、既存値を変更・再利用しない。Reject／Fallbackイベントは専用Reasonを必須とし、Reason enumを`Value0`／`Value1`へ重複保存しない。

Provisional物理のCommitted／Finalized／SafetyFrozen状態遷移と、状態を変えない公開前Fallback Outcomeは、それぞれゲーム側でexactly onceに確定する。対応する4種のTrace Eventの構築／enqueueは結果を消費する単一Coordinatorが最大1回だけ試行し、enqueue成功時は当該結果につき厳密に1件、失敗時は0件としてゲーム状態をrollback／再TraceせずRunをIncompleteにする。同じ結果から2件以上の同種Eventを生成しない。4 Eventの共通`ObjectGeneration`は対象結果のGeneration、`Value0`のCutOperationIdはObject生存期間中の正の非再利用IDとする。公開前FallbackはFromStateとToStateを同じ実状態に固定し、要求Provisional種別を`Value1`から復元する。SafetyFrozenは状態公開をTrace成功へ依存させない。Fallback／Faultでは内部ReasonとTraceReasonの固定対応を検証し、不正値なら部分物理を公開せず、公開前は既存Group維持、公開後はGroup全体のScene除外へfail closedする。

`CullInvalidationTrigger`は固定値`None=0`、`DirectChildReplaced=1`、`BoundaryActivated=2`とする。`LogicalCutOperationValidationError`は固定bit `InvalidId=1<<0`、`InvalidGeneration=1<<1`、`ChildCountOutOfRange=1<<2`、`BoundaryCountOutOfRange=1<<3`、`DuplicateChildId=1<<4`、`DuplicateBoundaryId=1<<5`、`ParentChildAlias=1<<6`、`UnknownReference=1<<7`、`SelfBoundary=1<<8`、`BoundaryOutsideDirectChildren=1<<9`、`UnconnectedDirectChild=1<<10`とし、複数違反をORして記録する。未知bitはRejectし、同じ入力から同じmaskを得る。Operation作成成功時のTrace束は`Created`、全Child Linkを序数順、各Boundary Linkとその正負Endpoint LinkをBoundary序数順、最後に`TraceCompleted`の順とする。期待Event数は`2 + DirectChildCount + 3 * CutBoundaryCount`であり、EndpointSlotは各Boundaryにつき0と1をちょうど1件ずつ要求する。`TraceCompleted`があり、期待Event数、ID、序数、両端、Generationがすべて一致する束だけを復元可能な完全Operation Traceとして扱う。以後の三値変化を`OperationSupportStateChanged`、最初の不可逆Cull失効を`FullyFixedCullInvalidated`で一度だけ記録する。不正構築時は作成Trace束を発行せず`LogicalCutOperationRejected`だけを記録する。

現行の固定サイズTrace Eventと`Value0`／`Value1`を維持し、支持判定では当面、期待値と実値のGenerationをこれらへ格納できる。支持イベント、Operationイベント、Fragmentイベント、Runtime Arenaイベント追加だけを理由にバイナリレコード構造を変更しない。Operation系8イベントでは`Value0`をCutOperationId専用としてTimelineが整数表示・検索する。Runtime Arenaの4イベントでは`Value0`をDebrisEventId専用としてTimelineが整数表示・検索し、発生フレームは共通`FrameId`フィールドを使用する。`FromState`／`ToState`は通常の汎用状態遷移ではなく上表のイベント固有割当として解釈する。`TraceEventType`と`TraceReason`の数値はappend-onlyとし、既存値の変更や再利用を禁止する。

Operation状態の公開とTrace enqueueは意図的に非トランザクションとする。構築Validator合格後、Fragment／Boundary／Operationをゲーム状態へ原子的に公開してから、メインスレッドで固定長の作成Trace束をbest effort enqueueする。TraceLoggerの破棄、容量／Nativeエラー等で途中失敗しても公開済みOperationを巻き戻さず、Trace書込経路とは独立したsaturating `uint`の`TraceEnqueueFailureCount`を増やす。末尾`LogicalCutOperationTraceCompleted`がない束、件数やTopologyが一致しない束はTimelineで`IncompleteOperationTrace`として表示し、状態再現・Golden比較・T-074合格根拠には使用しない。Trace失敗をゲーム状態のCommit失敗や`LogicalCutOperationRejected`として偽装しない。

Jobからは`NativeQueue<TraceEvent>.ParallelWriter`等のBurst互換経路へ書き込み、メインスレッドがフレーム末尾に回収する。毎フレーム全状態をスナップショットせず、状態遷移と重要な判断だけを記録する。

### 21.4 固定長バッファと異常時保存

初期値として直近30秒相当を固定長リングバッファで保持する。容量超過時は古い正常イベントを上書きし、記録処理を停止させない。

不変条件違反を検出した場合はバッファを保護し、可能なら追加で約5秒記録してから保存する。保存対象にはTrace本体のほか、ビルド識別子、シーン、乱数Seed、固定時間刻み、品質設定、対象世代、斬撃入力を含める。

Trace完全性のためにbundle v1の許可ファイル集合、`bundle.index`、strictな`TraceRunManifest` Schema v1を変更せず、第4ファイルやManifest propertyを追加しない。Capture Artifactが保持する`RunManifestContentSha256`も従来どおりimmutableなManifest v1 bytesのhashとし、Run途中の失敗Countで変化させない。`TraceFlightRecorder.PostRollCapacity`、`CapturedPostRollCount`、`CapturedCount`の公開契約は変更せず、Recorderの`CapturedPostRollCount=N`はFreeze BarrierのDrop／RingFrozenを含む実際に複製したSummary以外のpost-roll Event数、`CapturedCount=TriggerHistoryCount+N`とする。post-roll容量とは別にExporterがSummary用1枠を確保する。

Capture Run終了時のfreezeは`FrozenRunPublicationCoordinator`がMain Thread上で実行する単一Barrierとし、順序を`新規Capture受付停止 -> producer稼働中のin-flight Draft／Terminal Intent Queue bounded drain（成功時のCaptureFrameEncodedと通常失敗Dropは通常Queueへenqueue） -> deadline時の残存producer取消要求 -> Queueをdrainしながらproducerが未受理Intentを再試行または私有Bufferを解放 -> 全producer join／静止 -> Terminal Intent Queueを空まで最終drain -> TerminalIntentOwnershipSnapshot照合 -> その時点でもPendingなDraftだけを理由9のDropped tombstoneへ強制終端しForcedDropFrameIdSet確定 -> 同Runの全通常Trace producer静止 -> TraceLogger.SealAndDrainRunForFreezeで当該Runをsealして通常FIFOを通常領域へ完全Drain -> FreezeTerminalCheckpoint採取とterminal Event列構築 -> BeginFreezeTerminalAppendでAwaitingFreezeTerminalへ遷移 -> terminal Event列を専用reserveへ直接Append -> Recorder Freeze -> Frozen Snapshot生成 -> TraceIntegritySummary追加`へ固定する。producer join直前に追加されたStage／Drop Intentも最終drainで通常終端処理へ反映し、最終drainと所有権照合より前に残存Pendingを列挙または理由9へ変更してはならない。Begin APIを省略した直接Appendまたはpublic `Freeze()`による迂回を禁止する。

Capture Run用`TraceLogger`は1つの`TestRunId`へbindし、Open／Sealing／Sealedのappend-onlyなRun Seal Stateとatomicな`ActiveWriterCount`を持つ。Main Thread専用の`SealAndDrainRunForFreeze(TestRunId)`は、新規producer停止と既存Job joinの証拠を検証し、OpenからSealingへcompare-exchangeした後、全enqueue入口を閉じ、開始済みwriterの`ActiveWriterCount == 0`を確認してから通常Queueを最後までDrainし、Queue空を再検査する。その後、Sealing中の拒否とseal前までの全enqueue失敗を含むmutable Run counterを線形化可能なcutoff操作で閉じ、immutableな`SealedTraceEnqueueFailureCount`を確定してからだけSealedを公開する。このseal、writer退出待ち、最終Drain、Failure Count cutoffをFreeze Barrierの単一不可分protocolとして扱い、別の通常DrainとQueue空確認の組合せで代用しない。

Capture Runでは生の`NativeQueue<TraceEvent>.ParallelWriter`をproducerへ公開せず、共有Seal StateとActiveWriterCountを参照するBurst互換`SealableTraceWriter`だけを渡す。各enqueueはactive countをatomic incrementし、Seal StateがOpenであることを再確認してからQueueへ書き、finally相当でdecrementする。enqueue成功の線形化点は、このactive increment後の`Open`再確認が成功した瞬間とする。active increment自体はEventの開始または受理を意味しない。`Open`再確認の成功が`Open -> Sealing` CASより前ならseal側がwriter退出を待ってEventを最終Drainへ含め、CASが先なら、そのwriterがactive increment済みでもSealingを観測した拒否としてQueueへ格納しない。Sealingを観測した拒否は、Run counterがcutoff前なら当該Runのmutable `TraceEnqueueFailureCount`をsaturating incrementする。cutoff操作と各拒否の計上先選択は同じatomic gateで線形化し、各試行を必ずcutoff前または後の一方だけへ分類する。Sealedまたはcutoff後を観測した試行はRun counterへ触れず、Queueにも入れず、process単位のsaturating `PostSealTraceEnqueueAttemptCount`だけを増やす。Main Threadの`TraceLogger.Enqueue`も同じgateを通す。したがってQueue空確認後のlate enqueueで`AwaitingFreezeTerminal`が停止する競合を作らない。Legacyの非Capture Loggerだけは既存raw `JobWriter` APIを維持できる。

`FreezeTerminalTraceReserve = CaptureTraceProfile.MaxInFlightDraftCount + 1`件を`PostRollCapacity`内へ事前予約し、`NormalPostRollCapacity = PostRollCapacity - FreezeTerminalTraceReserve`とする。実装済み`CaptureFrameProfile`は画像取得の7項目だけを持つ既存immutable型として維持し、既存public 7引数constructorと`CreatePhaseZeroUnityLeftEye(int, in CaptureImageRect)`へTrace容量propertyを追加しない。Trace／Draft容量は別のimmutable `CaptureTraceProfile`へ分離し、`CaptureProfileId`、`PostRollCapacity`、`MaxInFlightDraftCount`、`MaxDraftCountPerRun`を必須propertyとする。対応する`CaptureFrameProfile.ProfileId`と`CaptureTraceProfile.CaptureProfileId`はRun構築時に一致を要求する。

`CaptureTraceProfile`は`1 <= MaxInFlightDraftCount <= MaxDraftCountPerRun <= 100000`、`MaxInFlightDraftCount + 1 <= PostRollCapacity`、各値のchecked演算をconstructorで検証する。`MaxInFlightDraftCount`は、受付済みでまだ`Staged`／`Dropped`へ終端していない全`Pending` Draftの厳密なHard上限であり、受付、Scheduler待機、readback待機、encode待機、staging登録待機を含む全queue／worker間の合計へ適用する。Draftはどのqueueへ入るより先にDraft Registryへ原子的に登録し、Registry外にPending Draftを保持してはならないため、freeze時の強制Drop数は必ず`MaxInFlightDraftCount`以下となる。Terminal Intent Queue容量はprofileへ自由設定値を追加せず`checked(2 * MaxInFlightDraftCount)`へ固定し、Run構築時に2～200,000の範囲とoverflowを検証して事前確保する。各DraftはStage／Dropを合計して未処理最大2件、Run中に受理される総数も最大2件とし、3件目以降をQueueへ格納しない。

既存public constructor `TraceFlightRecorder(TraceLogger logger, int postRollCapacity)`は互換性のため維持し、`freezeTerminalTraceReserve=0`のLegacy Recorderを作る。Capture Runでは直接constructorを呼ばず、internal `CaptureTraceFlightRecorderFactory.Create(TraceLogger logger, CaptureFrameProfile frameProfile, CaptureTraceProfile traceProfile)`だけを使用する。FactoryはProfile ID一致を検証し、checked演算で`reserve = traceProfile.MaxInFlightDraftCount + 1`を求め、`0 < reserve <= traceProfile.PostRollCapacity`とTrace Profile全不変条件を再検証して、新しいinternal constructor `TraceFlightRecorder(TraceLogger logger, int postRollCapacity, int freezeTerminalTraceReserve)`へ渡す。Recorderはimmutableな`FreezeTerminalTraceReserve`と`NormalPostRollCapacity`を公開read-only propertyとして保持し、Reset後も構成値を変えない。internal constructorは負値、post-roll超過、加算overflowを引数例外としてRun開始前にRejectする。

Phase 0の標準構築は新しい`PhaseZeroCaptureProfileSet.CreateUnityLeftEye(int profileId, in CaptureImageRect imageRect)`だけを使う。このFactoryは既存`CaptureFrameProfile.CreatePhaseZeroUnityLeftEye(profileId, imageRect)`の戻り値をそのままFrame Profileとし、同じProfile IDで`CaptureTraceProfile(PostRollCapacity=4096, MaxInFlightDraftCount=32, MaxDraftCountPerRun=10000)`を生成してimmutableなpairを返す。既存`CreatePhaseZeroUnityLeftEye`単体は従来どおりFrame Profileだけを返し、Trace容量の暗黙defaultを持たない。したがって既存呼出元と7引数constructorは変更不要で、Capture Runの新規bootstrapだけをProfile Setへ移行する。

Draft RegistryはRun中に発行済みの全Entryを`CaptureFrameId`昇順で保持するappend-onlyな固定容量Entry Storeと、再利用可能なPending Slot Poolを分離する。Entry Store容量は`MaxDraftCountPerRun`、Pending Slot Pool容量は`MaxInFlightDraftCount`とする。`Pending -> Staged`または`Pending -> Dropped`の終端遷移時にPending Slotだけを解放して次の受付へ再利用し、EntryとDropped tombstoneはfreeze／Finalizer完了まで削除・再利用しない。したがってStaged／Droppedの累積件数はPending枠を消費しない。Publication Planへ入るStaged件数は`EntryCount <= MaxDraftCountPerRun <= 100000`となり、Plan Schema上限と一致する。

受付はEntry Store枠とPending Slotの両方を単一transactionで予約してからだけ正の`CaptureFrameId`を発行する。いずれかが満杯なら両方を変更せず、IDもDraftも発行せず、要求元へ同期的なbackpressure／受付拒否を返す。この拒否はDraftの終端ではないため`CaptureFrameDropped`を発行せず、後述の`CaptureFrameAdmissionRejected`だけを`CaptureFrameId=0`で記録する。Entry Store総上限到達後はRun終了まで新規受付を拒否し、Pending Slotだけが空いた場合は総上限未到達時に限り受付を再開できる。

`TraceFlightRecorderState`は既存`Armed=0`、`CapturingPostRoll=1`、`Frozen=2`を変えず、通常領域が満杯でもFrozenへせずreserveを保護して通常Queueのdrainだけを続けるappend-only状態`AwaitingFreezeTerminal=3`を追加する。通常の`TraceFlightRecorder.Drain`／`TraceLogger.Drain`は通常領域へ最大NormalPostRollCapacity件だけ複製でき、reserveへ書くこともFrozenへ遷移することもできない。通常領域の空き／満杯によって自動遷移せず、BarrierはFIFOを最後までdrainし、drained件数と通常領域へcapturedした件数を照合する。通常領域超過があればterminal処理は続けて保存可能にするが、そのTraceは容量不足としてIncompleteでありGolden根拠にしない。

Barrier専用の内部API `TraceFlightRecorder.BeginFreezeTerminalAppend`を追加し、Main Thread、状態`CapturingPostRoll`、terminal reserve有効、同Runの新規受付停止、`TraceLogger`が同じTestRunIdでSealed、通常Queue空、最終Drain完了照合済みを引数とRecorder内部状態の双方からall-or-noneで検証する。合格時だけ`CapturingPostRoll -> AwaitingFreezeTerminal`へ遷移し、capture列とCountは変更しない。`AwaitingFreezeTerminal`を含む`CapturingPostRoll`以外からの再呼出しは状態違反としてRejectし、状態とcapture列を変更しない。Coordinatorは状態照会でBegin成功済みかを判断し、その後の再試行は`AppendFreezeTerminalEvents`だけに対して行う。`FreezeTerminalTraceReserve > 0`のRecorderに対する既存public bool `Freeze()`は例外を投げず`false`を返し、状態、capture列、Countを一切変更しない。Coordinatorだけが`BeginFreezeTerminalAppend -> AppendFreezeTerminalEvents`を実行する。reserve 0のLegacy Recorderに限り、`CapturingPostRoll`から直接Frozenへ進み成功時true、それ以外falseという既存public `Freeze()`契約を維持する。

freeze deadline後、CoordinatorはDraft Registry上で`Pending -> Dropped`へ実際に遷移した全DraftのうちReasonが`FreezeDrainTimeout`である`CaptureFrameId`だけを、正数・重複なし・昇順のimmutableな`ForcedDropFrameIdSet`として一度確定する。この集合の確定後はDraftの追加、終端状態の変更、集合の差し替えを禁止する。各Draftは受付時の既存`CaptureFrameTraceContext`を欠落なく保持するimmutableな`CaptureFrameDraftTraceContext`を持ち、`Timestamp`、`UnityFrameId`、`FixedStepId`、`ThreadId`、`CaptureFrameId`、`OpenXRFrameId`、`TestRunId`、`SlashId`、`FrontEdgeId`、`ObjectId`、`ObjectGeneration`、`TaskId`の12 fieldを元Capture requestから完全転記する。強制終端した`CaptureFrameDropped`は通常の`RecordDropped`／Logger Queueを通さず、Observerの副作用なしBuilderで事前確保した`FreezeTerminalTraceBuffer`へ`ForcedDropFrameIdSet`と同じ順で1件ずつ構築する。

強制Drop Eventの全フィールドを次に固定する。`Timestamp`、`FrameId=UnityFrameId`、`FixedStepId`、`ThreadId`、`CaptureFrameId`、`OpenXRFrameId`、`TestRunId`、`SlashId`、`FrontEdgeId`、`ObjectId`、`ObjectGeneration`、`TaskId`は対応Draft Trace Contextとbit単位で一致させる。`SlashGeneration=0`、`MobId=0`、`PlanGeneration=0`、`EventType=CaptureFrameDropped`、`TaskType=None`、`FromState=Pending(0)`、`ToState=Dropped(2)`、`Reason=None`、`Value0=0.0`、`Value1=FreezeDrainTimeout(9)`とする。これは既存`CaptureFrameTraceObserver.BuildEvent`が通常Dropへ転記する相関情報と一致し、元Contextに存在しないGeneration／Mob／Planだけを0へ固定する。NaN、負の0、未使用fieldの非ゼロを許可しない。

Logger sealと最終Drainの完了直後、CoordinatorはMain Thread上で`FreezeTerminalCheckpoint`を1回だけ採取し、有限かつ非負の`Timestamp`、現`FrameId`、`FixedStepId`、Main Thread ID、現`TestRunId`をimmutableに保持する。`CaptureRingFrozen`はforced dropが0件でも必ず1件生成し、`Timestamp`、`FrameId`、`FixedStepId`、`ThreadId`、`TestRunId`をCheckpointからbit単位で転記する。`SlashId=0`、`SlashGeneration=0`、`FrontEdgeId=0`、`ObjectId=0`、`ObjectGeneration=0`、`MobId=0`、`PlanGeneration=0`、`TaskId=0`、`CaptureFrameId=0`、`OpenXRFrameId=0`、`EventType=CaptureRingFrozen`、`TaskType=None`、`FromState=AwaitingFreezeTerminal(3)`、`ToState=Frozen(2)`、`Reason=None`、`Value0=ForcedDropFrameIdSet.Count`、`Value1=0.0`へ固定する。Countは非負intでbinary64へ正確に格納し、負の0を許可しない。

`TraceFlightRecorder.AppendFreezeTerminalEvents`はMain Thread／`AwaitingFreezeTerminal`でのみ呼べる内部APIとし、Loggerが同RunでSealed、通常Queue空、件数が`ForcedDropFrameIdSet.Count + 1`かつ`<= FreezeTerminalTraceReserve`、先頭からのDrop Event列が集合の全IDと順序・対応Draft Trace Context・上記全固定fieldへbit単位で完全一致し、欠落・余分・重複がなく、末尾だけが同じCheckpointとTestRunIdから作った上記`CaptureRingFrozen`であることを全22 fieldについて事前検証する。enum、integer、doubleの未使用値、NaN、Infinity、負の0もRejectする。検証とreserve書込みはall-or-noneとし、現在のFIFO Drainや自動Freezeを代用しない。追記成功後だけRecorderを`Frozen`へ遷移させる。

terminal Buffer構築、検証、reserve書込みのいずれかが失敗した場合、Recorderと`ForcedDropFrameIdSet`を`AwaitingFreezeTerminal`のまま保持し、capture列を一切変更せず、Frozen Snapshot生成、Summary生成、Manifest生成、Plan確定、bundle exportを禁止する。Coordinatorは同じimmutable集合からBufferを再構築して同じ内部APIを再試行できる。成功まで不完全なbundleへ進むFallbackは設けない。永続的に再試行不能なら明示的にRunをAbortし、stagingを隔離してbundleを公開しない。失敗回数と最後の失敗理由はRun外の診断ログへ記録できるが、未Frozen captureの完全性を表すEventやSummaryとしては扱わない。checked計算でreserveがPostRollCapacityを超えるProfileはRun開始前にRejectする。これによりbounded drain中の`CaptureFrameEncoded`、通常Drop、Barrier開始前の残存Eventは通常領域だけを使い、terminal reserveを消費できない。

freeze後、ExporterはRecorderのFrozen通常Event列をコピーし、Logger seal時に固定済みの`SealedTraceEnqueueFailureCount`、`TraceCaptureOverflowCount`、`PriorBundlePublishFailureCount`のスナップショットを持つ`TraceIntegritySummary`をQueue経由ではなく末尾へ直接1件追加した「Summary付きExport Snapshot」を新たに構築する。Recorder自身のCountや保持列は変更しない。Export SnapshotとManifestに限り`CapturedPostRollCount=N+1`を「trigger後にExport Snapshotへ収録したrecord数」として使用し、内訳をSummary以外のduplicated post-roll N件＋synthetic Summary 1件とする。したがってRecorderでは`CapturedCount == TriggerHistoryCount + N`かつ`N <= PostRollCapacity`、Export Snapshot／Manifestでは`EventCount == TriggerHistoryCount + (N + 1)`かつ`CapturedPostRollCount <= PostRollCapacity + 1`がそれぞれ成立する。後者にRecorderの`CapturedPostRollCount <= PostRollCapacity`を適用してはならない。`TraceCaptureSnapshot.CapturedPostRollCount`のAPI説明はExport SnapshotでSummaryを含む意味へ更新し、従来値が必要な呼出元は`CapturedPostRollCount - 1`を暗黙使用せず、RecorderのCountまたは末尾EventTypeを検証してSummary以外の件数を導出する。Recorderの既存Count／Capacityテストは維持し、Snapshot／Manifest／ExporterテストだけへSummary有無の両形式と`+1`上限を追加する。導入前Snapshot／bundleでは全Countが従来のduplicated eventである。Summary付きSnapshotからManifestを一度生成した後はManifestを変更しない。

`TraceIntegritySummary`の共通時系列フィールドは決定論的に固定する。通常の捕捉Eventが1件以上ある場合は、chronological Snapshotで直前に位置する最終通常Eventの`Timestamp`、`FrameId`、`FixedStepId`をそのまま継承し、`ThreadId`はfreezeを実行するMain Thread IDとする。同値時のTimeline sortは既存の入力順tie-breakerにより末尾Summaryを最後に保つ。通常Eventが0件の空Captureでは`Timestamp=0`、`FrameId=0`、`FixedStepId=0`、`ThreadId=freeze Main Thread ID`とする。どちらの場合も`TestRunId`は対応`TraceRunContext.TestRunId`、`EventType=TraceIntegritySummary`、`TaskType=None`とし、`SlashId`、`SlashGeneration`、`FrontEdgeId`、`ObjectId`、`ObjectGeneration`、`MobId`、`PlanGeneration`、`TaskId`、`CaptureFrameId`、`OpenXRFrameId`はすべて0へ固定する。`FromState`、`ToState`、`Reason`、`Value0`、`Value1`だけを完全性表の割当に従って設定する。

mutableな`TraceEnqueueFailureCount`、immutableな`SealedTraceEnqueueFailureCount`、process診断用`PostSealTraceEnqueueAttemptCount`、および`PriorBundlePublishFailureCount`はTraceLogger Queueとは独立したsaturating `uint`とする。mutable Run Countは当該Trace Runのcutoff前enqueue失敗を表し、新しい`TestRunId`でRunを開始するときだけ0へ初期化する。`SealAndDrainRunForFreeze`は全active writer退出後、拒否計上との共通atomic gate上でcutoffを線形化し、その値をSealed Countへ一度だけコピーして以後変更しない。cutoff後／Sealed後の試行はPost-Seal Countだけへ入り、Run Summary、Complete判定、次RunのRun Countへ混入しない。Post-Seal Countはprocess終了までの診断Counterであり、UI／Profilerへ表示できるがTrace bundleへ保存しない。

`PriorBundlePublishFailureCount`は同一プロセスで直前までに公開できなかったbundle公開試行回数を表し、Run境界ではリセットしない監査情報であって、現在bundleのTrace欠落を意味しない。`TraceCaptureOverflowCount`は通常FIFO DrainでdrainしたがNormalPostRollCapacity不足によりcaptureへ複製できなかったEvent数を表すsaturating non-negative `int`とし、新しいTestRunIdで0へ初期化してSummaryのToStateへ格納する。`TraceIntegrityState`は固定値`Complete=0`、`Incomplete=1`とし、Sealed Enqueue FailureまたはCapture Overflowのどちらかが非ゼロならIncompleteとする。ReasonはSealed Enqueue Failureを優先して`TraceWriteFailureObserved`、それが0でOverflowが非ゼロなら`TraceCaptureOverflowObserved`、両方0なら`None`とする。`PriorBundlePublishFailureCount`またはPost-Seal Countだけが非ゼロでもStateをIncompleteにしない。

1回のbundle公開試行は`Summary付きExport Snapshot構築 -> Manifest生成 -> Capture Draft全件Finalization -> CapturePublicationPlan生成 -> SaveAtomic -> 最終Rename`全体と定義する。Summary予約／追加、Snapshot不変条件検証、Manifest生成、Draft／PNG staging検証、Finalization、Plan生成、または`SaveAtomic`のどこで失敗しても、外側の`FrozenRunPublicationCoordinator`が`PriorBundlePublishFailureCount`を厳密に1回だけ増やし、内部段階で重複加算しない。失敗したbundle自身へ記録できるとは保証せず、次に成功したbundleのSummaryへ累積値を監査用に保存し、Trace bundleの原子的な最終公開が成功した後だけ同Countを0へ戻す。Trace公開後のCapture Artifact個別公開失敗はこの試行の失敗へ戻さず、同Countを増やさない。成功前のプロセス終了やクラッシュではこのCountを永続化できないことを明記し、bundle外journalはPoCスコープ外とする。

保存Traceを`Complete`と判定する必要十分条件は、bundle／Manifest／traceの既存hash・件数検証に成功し、Manifest v1の`WasHistoryOverwrittenAtTrigger == false`であり、最終Eventが唯一の`TraceIntegritySummary`で`SealedTraceEnqueueFailureCount == 0`かつ`TraceCaptureOverflowCount == 0`かつState／Reasonが整合し、全LogicalCutOperation作成Trace束が`LogicalCutOperationTraceCompleted`まで完全であることとする。`PriorBundlePublishFailureCount`と`PostSealTraceEnqueueAttemptCount`は表示・監査するが、この判定条件へ含めない。既存hash／schema検証失敗は従来どおりbundle自体をRejectする。検証済みbundleで履歴上書きがtrue、Summaryが存在するが重複／非終端／Sealed Failure Count／Overflow Count／State／Reason不整合、またはOperation作成束が不完全なら`Incomplete`とする。Summaryが存在せず、ほかに既知の不完全条件もない導入前bundle v1は既存Loaderで引き続き閲覧可能な`UnknownLegacy`とし、破損扱いにはしないが、完全Traceを要求するGolden比較やT-019／T-074の合格根拠には使用しない。これにより、ring履歴上書きでOperation束全体が消えて残存Eventだけでは欠落を検出できない場合も、既存`WasHistoryOverwrittenAtTrigger`からIncompleteと判定できる。

自動保存トリガーは、二重Commit、存在しないTaskの完了、Slash／Object／Plan Generation不一致Commitの試行、Hit未確認Commit、Pending状態のタイムアウト、表示破片とColliderの不一致、成果物の未解放を基本とする。

### 21.5 Editor Timeline

最初は独立したEditorWindowとして実装し、Unity ProfilerのカスタムModule化は必要性が確認されてから行う。

- 横軸は時刻またはフレームとする。
- レーンはSlash、Object、MobPlan、Task、Threadを切り替える。
- `SlashId`、`ObjectId`、`ObjectGeneration`、`MobId`、`PlanGeneration`、`TaskId`、`CutOperationId`、失敗理由で絞り込む。Operation系Eventでは作成Trace束の完了性と境界Endpoint Graphを復元し、不完全束を明示する。Render／Convex対応イベントではイベント別フィールド表を解釈し、`ObjectId + ObjectGeneration + RenderFragmentLocalId`または`LogicalConvexFragmentLocalId`でも絞り込めるようにする。
- Running、Completed、Rejected、Stale、Fallbackを色分けする。
- イベント選択時に前提世代、予測値、拒否理由、依存Taskを表示する。
- 対応するGameObjectをHierarchyで選択できるようにする。
- 保存Traceを再読込し、Play Mode外でも閲覧できるようにする。

### 21.6 性能上の規則

- Taskごとの`Debug.Log`や文字列補間をホットパスで使用しない。
- 既知EventTypeとReasonはenumで管理し、表示時だけ文字列へ変換する。
- バッファ、Queue、書き出し領域を事前確保またはプールする。
- Trace記録自体のCPU時間、GC、ドロップ数をProfilerで測定する。
- Development Buildでは通常有効、Release Buildでは無効または重大異常のみとする。
- ロガーが競合条件を隠さないことをT-020で比較検証する。

### 21.7 映像キャプチャとTrace同期

#### 21.7.1 目的と証拠の優先順位

映像はTraceLoggerを置換せず、処理経路デバッグ色、切断面、VFX、左右眼差、表示の巻戻りを人間が確認する補助証拠とする。ゲーム状態と因果関係の正本はTrace、CPU／GPU時間の正本はProfiler／XRDisplaySubsystem、画像の正本はCapture Recordが指すフレームとする。

```text
Trace Event／Profiler
       └-> CaptureFrameId
              ├-> Unity選択キャプチャ画像／動画
              └-> 後期OpenXR Projection画像
```

#### 21.7.2 Phase A：Unity側の選択的キャプチャ

PoC初期はUnityのXRDisplaySubsystem、XR Render PassまたはURP側の明示的なRenderTexture Blitを利用し、Window、OBS、Desktop Duplication、HMD Mirror Windowを必須にしない。通常は左眼を45fpsで取得し、必要に応じて解像度を縮小してGPU対応の動画Encoderへ渡す。フルフレームの同期GPU-to-CPU Readbackは常用せず、静止画が必要な場合も非同期かつ枚数制限付きとする。

動画はTraceの30秒リングとは別に、初期候補5～15秒の圧縮リングバッファを持つ。不変条件違反、Commit Reject、Pending Timeout、手動トリガーで直前区間を固定し、可能なら数秒の事後映像を追加する。原解像度・両眼は常時動画ではなく、異常フレーム前後の限定静止画または短区間だけを候補とする。

#### 21.7.3 Capture Record

各保存画像／動画フレームは、少なくとも次のメタデータへ一意に対応させる。

```text
CaptureFrameId / UnityFrameId / OpenXRFrameId
TestRunId / TestCaseId / BuildId / SceneId / RandomSeed
predictedDisplayTime / predictedDisplayPeriod / shouldRender
HeadPose / LeftControllerPose / RightControllerPose
SlashId / FrontEdgeId / ObjectId / ObjectGeneration / TaskId
CommitPath / CaptureSource / Eye / ImageRect / ArrayIndex
AppGPUTime / CompositorGPUTime / DroppedFrameCount
CaptureProfileId / RunManifestHash（freeze後に確定）
```

Captureは二段階ライフサイクルへ固定する。ライブ取得時は内部型`CaptureDraftRunContext`と`CaptureFrameDraft`を使う。前者は`TestRunId`、`TestCaseId`、`BuildId`、`SceneId`、`RandomSeed`、`CaptureProfileId`等のRun開始時不変値を持つが、`TraceRunManifest`、`CaptureRunReference`、`RunManifestContentSha256`を持たない。後者は上表のうち`RunManifestHash`以外のFrame timing、Pose、ID、ProfileとContext参照を持ち、freeze後の`CaptureFrameRecord`へ必要な値を欠落なく保持するimmutableな内部Recordとする。`CaptureFrameId`は既存と同じ正のID範囲・非再利用規則で`CaptureFrameDraftFactory`が発行し、`CaptureFrameDraftRegistry`が`TestRunId + CaptureFrameId`をKeyとして昇格または明示的な回収まで所有する。Draftを既存の公開型`CaptureFrameRecord`へcast／仮変換せず、未確定Manifest hashを捏造しない。

現行のライブ`CaptureFrameRecordFactory`／`CaptureFrameRecordRegistry`／`CaptureFrameRenderTargetRecordSubmissionCoordinator`／`CaptureFrameRenderTargetRecordScheduler`の相関責務は、Phase 0で`CaptureFrameDraftFactory`／`CaptureFrameDraftRegistry`／`CaptureFrameRenderTargetDraftSubmissionCoordinator`／`CaptureFrameRenderTargetDraftScheduler`へ置換する。Capture request、RenderTexture lease、Queue予約、rollback、drop、backpressureの所有権契約は既存経路を維持し、相関Keyだけを最終RecordからDraftへ変える。Readback Pump／Completion Router／PNG Encode Queueは`CaptureFrameRequest`と`CaptureFrameId`でDraft Registryを参照し、ライブ中に最終Recordを要求しない。readback完了後は、encoded PNG bytesまたは一時file、byte length、content hash、Draft IDを持つ`CaptureFramePngStagingEntry`をboundedな`CaptureFramePngStagingStore`へ原子的に登録する。登録成功後はRenderTarget／readback Bufferを解放できるが、canonical sidecar生成、`CaptureFramePngArtifactCodec`による最終Artifact準備、Artifact Registry登録、最終pathへのPersistence、Completion通知はfreeze後まで実行しない。

Draft Registry Entryは固定値`CaptureFrameDraftStatus.Pending=0`、`Staged=1`、`Dropped=2`の直交状態を持つ。Registryは`MaxDraftCountPerRun`件のappend-only Entry Storeと`MaxInFlightDraftCount`件の再利用可能Pending Slot Poolを持ち、正の`CaptureFrameId`を発行したEntryは状態にかかわらずfreeze／Finalizer完了までStoreへ保持する。Draft共有資源、Registry終端状態、Pending Slotを変更できるのはMain Threadの単一`CaptureFrameDraftTerminalCoordinator`だけとする。Readback Pump、Completion Router、Encoder worker、取消callbackはStage／DropのimmutableなTerminal Intentを固定容量`CaptureFrameDraftTerminalIntentQueue`へ通知するだけで、Draft共有lease、一時file、登録済みPNG Staging Entryをrollback、採用、解放しない。Intentがencoded bytes等のproducer私有Bufferを参照する場合、`EnqueueTerminalIntent`が`Accepted`を返す線形化点でだけその所有権をQueue／Coordinatorへ移す。それ以外のstatusではQueue、受理Count、所有権を変更せず、producerが私有Bufferを保持する。

readback完了後の処理境界は`Readback Completion Collect -> Encode Submission -> Encode Service -> Encode Completion Collect -> Main Thread Completion Apply`へ分離する。`CaptureFrameReadbackPayloadLease`はDispatcherのraw Buffer自体を所有せず、その成功Resultを一度だけ`Release`する義務を所有する。`CaptureFrameEncodeSubmission`がcaller所有のLeaseを保持し、固定容量Serviceが`Accepted`を返す線形化点でだけServiceへ移す。`Backpressured`、`NotAccepting`、受付前例外ではSubmissionとLeaseを変更しない。Serviceは`CaptureFrameWorkToken`のService identity、Slot index、Generation、TestRunId、CaptureFrameIdで受理作業を識別し、Slotを`Completion`の回収・Main Thread反映・acknowledge完了まで再利用しない。Completionの重複、別Service、stale Generationは副作用前にRejectし、Frame IDをrollback／再発行しない。

Phase 1の`SynchronousCaptureFrameEncodeService`は構築Thread上で`CaptureFramePngEncoder`を同期実行し、Thread、`Task.Run`、Job、raw Bufferの追加copy、PNG実装変更を導入しない。ServiceはDraft、Registry、TraceLogger、Trace Observerを参照せず、成功／失敗／取消のimmutableな`CaptureFrameEncodeCompletion`だけを返す。`CaptureFrameEncodeCompletionCoordinator`だけがMain ThreadでCompletionを一度反映し、既存順序の`CaptureFrameEncoded`記録、Dispatcher Releaseを行う。その後のRenderTexture Lease返却、Queue投入、Record rollback、Draft Terminal Intent生成と正式なDraft／Registry遷移もMain Threadに限定する。Phase 1の既存Router APIは互換Adapterとしてこの境界を同じTick内で連続実行し、Event内容／順序、Drop理由、PNG bytes、例外伝播、Release／Return順、各Tick最大件数を変更しない。

非同期処理の進行はDraft状態へ追加せず、独立したappend-only `CaptureFrameWorkStage`の`ReadbackCompleted -> EncodeQueued -> Encoding -> Encoded -> SaveQueued -> Saving -> DurableStaged -> Published`または`Dropped`として扱う。Draftの`Staged`／`Dropped`は従来どおり終端であり、`Staged`後のdurable保存失敗をDraftの`Dropped`へ巻き戻さない。Service lifecycleは将来のFreeze／shutdown順`新規受付停止 -> BeginDrain -> queued取消 -> running完了 -> TryCollect -> Main Thread反映 -> TryJoin -> Terminal Intent Queue close -> Pending強制Drop -> Trace seal/freeze -> Record Finalization -> Dispose`を表現できるようにする。Phase 1はWorkerを持たないため`CancelQueued`は0、`TryJoin`は即時成功だが、受付停止とCompletion回収の境界は先に固定する。

PNG圧縮と永続I/Oは同じ実行方式として扱わない。将来のPNG Serviceは、メインスレッド外から利用でき、単一の専用Workerへ閉じ込めて直列利用でき、Unityのメインスレッド専用APIへ依存せず、初期化・使用・破棄を同じWorkerで行えるEncoderへ交換してから非同期化する。現行`ImageConversion.EncodeNativeArrayToPNG`はPhase 1ではMain Thread専用同期Encoderのままとする。ファイルI/O、SHA-256、file／directory flush、Renameは別の長寿命専用I/O Workerへ移し、ブロッキングI/OをUnity Jobへ投入しない。いずれも固定Slotと受付時に予約済みのCompletion領域を持ち、WorkerはRegistry／Draft／Traceを変更しない。具体的なSlot数、raw Buffer copy方式、zero-copy、Pixel前処理Job、Encoderライブラリは負荷Spike後まで固定しない。

Queue容量は`checked(2 * MaxInFlightDraftCount)`、各Draftの未処理Intent数とRun中の受理総数はそれぞれ2件以下とする。`EnqueueTerminalIntent`はboolではなく固定`TerminalIntentEnqueueStatus`を返し、判定優先順を`InvalidIntent -> RunNotAccepting -> DraftAlreadyTerminal -> IntentLimitExceeded -> Backpressured -> Accepted`へ固定する。未知Draft ID、型／理由／Context不正、null／破棄済み私有Bufferは`InvalidIntent`、Runが通常受付中でもFreeze Barrierのproducer drain中でもない場合は`RunNotAccepting`、対象DraftがStaged／Droppedなら`DraftAlreadyTerminal`、当該DraftのRun中受理総数または未処理数が2以上なら`IntentLimitExceeded`、そこまで合格してQueueだけが満杯なら`Backpressured`、すべて合格した場合だけ`Accepted`とする。

`Accepted`はIntent、私有Buffer所有数、Draft別受理／未処理数、Run受理数を同じ操作で更新し、producerは再試行も解放も行わない。`Backpressured`だけは一時的な容量不足であり、producerが私有Bufferを保持したままCoordinatorのdrain後に同じIntentを再試行する。`DraftAlreadyTerminal`と`IntentLimitExceeded`は永久的な非受理であり、producerが自身の私有Bufferだけを解放して再試行しない。`RunNotAccepting`も再試行せず私有Bufferを解放して停止acknowledgementへ進む。`InvalidIntent`は私有Bufferをproducer所有のまま明示的に解放し、RunをFail Fast／Incomplete対象として同じIntentを再試行しない。特にDraft別受理総数はdequeue後も減らないため、2件到達後を`Backpressured`として扱ってはならない。freeze取消後は、既に得た`Backpressured` Intentをproducer drain期間中に再試行して`Accepted`へ進めるか、Barrierが受付を閉じた後の`RunNotAccepting`で私有Bufferを解放し所有数0をacknowledgeするまでjoin完了とみなさない。

CoordinatorはQueueから取り出したIntentを一列に処理し、対象EntryがPendingである最初の有効Intentだけを勝者とする。以後のDrop対Drop、Drop対Stage、Stage対Dropの敗者IntentはEntry状態を変えず、勝者が採用した共有資源へ触れず、Intent自身だけが所有する私有BufferだけをCoordinatorが解放する。各dequeueはDraft別未処理数とRun処理数を同じ操作で更新するが、Draft別受理総数は減らさない。producer join後、CoordinatorはQueueが空になるまで最終drainし、`QueueCount=0`、`RunAcceptedIntentCount=RunProcessedIntentCount`、`QueueOwnedPrivateBufferCount=0`、全producerの`RetainedPrivateBufferCount=0`を持つ`TerminalIntentOwnershipSnapshot`を確定する。この照合に失敗した場合は残存Pendingを理由9へ変えずRunをabort／Incomplete対象とする。照合成功後だけ、まだPendingであるDraftをfreeze timeoutとして強制終端できる。

Stage Intentが勝った場合は、PNG Staging Entryの登録と`Pending -> Staged`、Pending Slotの一度限り解放をCoordinatorの単一終端操作として確定する。Drop Intentが勝った場合は、Coordinatorが所有するlease／一時fileを一度だけrollbackし、その完了後にRegistryの`Pending -> Dropped`、Pending Slotの一度限り解放、CaptureFrameId、理由6～8、immutableなDraft Trace ContextからなるDrop Trace payloadの保存、直交する`DraftDropTraceEmissionState=None -> Pending`を同じ終端操作で確定する。rollbackに失敗した場合は状態をPendingのまま別Intentへ渡さずRunをabort対象とし、部分的にStaged／Droppedとして公開しない。これにより、終端所有権を持たない処理がrollbackした後に別処理がStagedへ勝つ状態を禁止する。

新設internal `CaptureFrameTraceObserver.RecordDraftDropped(Registry, CaptureFrameId)`は、同じTerminal CoordinatorだけがDropped確定直後に一度呼ぶ。Registryは対象がDropped、理由6～8、`DraftDropTraceEmissionState=Pending`であることを検証し、payloadを取得すると同時に`DraftDropTraceEmissionState.Pending -> Attempted`へ不可逆遷移してからObserverへ返す。消費と状態変更は原子的であり、2回目以降、並行呼出し、Staged／Pending／理由9のEntryではpayloadを返さない。copy可能なResult／Receiptをpublicに公開せず、生のID／理由／ContextからEventを合成するoverloadも設けない。Observerは消費済みpayloadからEventへ12相関fieldを転記し、元Contextに存在しない`SlashGeneration`／`MobId`／`PlanGeneration`だけを0、`EventType=CaptureFrameDropped`、`TaskType=None`、`FromState=Pending(0)`、`ToState=Dropped(2)`、`Reason=None`、`Value0=0.0`、`Value1=確定理由`へ固定して通常Queueへbest-effort enqueueする。

Logger破棄、seal競合、Queue／Nativeエラー等でTrace enqueueが失敗しても、`DraftDropTraceEmissionState`はAttemptedのままとし、確定済みDropped状態をPendingへ戻さず、Slotを再取得せず、rollbackやTrace発行を再試行しない。cutoff前の失敗は同Runの`TraceEnqueueFailureCount`へ記録してRunをIncompleteにする。したがってTrace欠落は観測完全性の失敗であり、ゲーム側Draft終端処理の失敗ではない。Dropped Entryは監査用の軽量tombstoneとしてfreezeまで保持するが、PNGを要求せず、理由9の`ForcedDropFrameIdSet`、最終`CaptureFrameRecord`、Publication Planの期待集合、CaptureComplete件数へ含めない。

freezeは21.4のFreeze Barrierに従い、新規受付を停止し、Terminal CoordinatorがQueueを継続drainしながらin-flight producerをboundedにdrainする。deadlineでは取消を要求し、producerが未受理Intentをenqueueするか私有Bufferを解放してからjoinする。全producer静止後にTerminal Intent Queueを空まで最終drainし、`TerminalIntentOwnershipSnapshot`でIntentと私有Bufferの全回収を照合する。この時点までに到着した成功Stageと通常失敗Dropを通常終端処理へ反映した後、それでも残るPendingだけを理由`FreezeDrainTimeout`でDroppedへ終端化し、対応TraceをQueueへenqueueせずterminal Bufferへ構築する。所有権照合前のPending列挙、強制Drop、ForcedDropFrameIdSet確定を禁止する。通常Queueを通常領域へ完全Drainした後、`BeginFreezeTerminalAppend`で`AwaitingFreezeTerminal`へ遷移し、強制Drop列と`CaptureRingFrozen`を専用reserveへ直接追記してRecorderをFrozen化し、その後だけFinalizerへ渡す。PNG Staging Store上限へ達した既存Draftは理由7でDroppedへ終端化する。Entry StoreまたはPending Slotが受付前に満杯の場合はDraftを作らず`CaptureFrameAdmissionRejected`を記録するため、`Pending`のまま残す対象も`CaptureFrameDropped`対象も存在しない。

`CaptureFrameDropReason`は既存値を変更せずappend-onlyとし、`None=0`、`RequestQueueFull=1`、`ReadbackFailed=2`、`EncodedPngQueueFull=3`、`FrameRecordRegistryFull=4`を維持したうえで、`FrameDraftRegistryFull=5`、`PngEncodeFailed=6`、`PngStagingStoreFull=7`、`CaptureCancelled=8`、`FreezeDrainTimeout=9`を追加する。受付前Request Queue拒否は1、GPU readback errorは2、readback後のEncode Queue拒否は3、旧Record経路のRegistry拒否は4、新Draft経路でEntry StoreまたはPending Slotを予約できない受付拒否は5、Encoder実行失敗／不正出力は6、encoded PNGのStaging Store容量拒否は7、明示取消／shutdown取消は8、freeze drain期限超過だけは9へ一意に対応させる。既存1～4の意味は変更しない。

理由5は正のIDを持つDraftのDropではなく、append-only EventType `CaptureFrameAdmissionRejected`でだけ記録する。共通`CaptureFrameId=0`、`TestRunId`は現Run、`Value0`は固定`CaptureFrameAdmissionRejectKind`（`PendingLimit=1`、`RunEntryLimit=2`）、`Value1=FrameDraftRegistryFull(5)`、`Reason=None`とし、両枠が同時に不足する場合はRun総量を優先して`RunEntryLimit`とする。`CaptureFrameTraceObserver.RecordAdmissionRejected`はこの組合せだけを受理し、Timelineでは「Capture未受付」と表示してDropped件数やDraft Registry件数へ加算しない。既存`CaptureFrameTraceObserver.RecordDropped`はLegacy理由1～4と既存の`FromState=0`／`ToState=0`契約だけを維持し、Draft経路から使用しない。新設internal `RecordDraftDropped`はRegistryとCaptureFrameIdだけを受け取り、Registry内の未消費な理由6～8のDrop Trace payloadを`DraftDropTraceEmissionState.Pending -> Attempted`へ一度だけ変更して消費し、Trace Event上の`Pending(0) -> Dropped(2)`を記録する。copy可能なDrop Result／Receiptや、生の理由／ContextからEventを合成するpublic overloadを設けない。理由9は通常Queue用の`RecordDropped`と`RecordDraftDropped`の双方で明示Rejectし、freeze後の`ForcedDropFrameIdSet`を入力とするterminal Builderだけが生成できる。理由5、ID 0、負値、10以上、未定義値も両通常Drop APIの該当しない入口でRejectする。これにより受付拒否を架空のDropped Draftとして復元せず、`FreezeDrainTimeout`を通常領域へ混入させない。

freeze後は`FrozenRunPublicationCoordinator`だけがFrozen通常Event列、Draft Registry、PNG Staging Store、派生するSummary付きExport Snapshot、Manifest、最終Record／sidecarを所有し、状態を`Collecting -> Frozen -> Preparing -> ReadyToPublishTrace -> TracePublishedCapturePending -> CaptureComplete`の順に進める。`CaptureFrameDraftFinalizer`は全Draftの`TestRunId`と不変Contextが最終Manifestへ一致すること、全Entryが終端状態であること、各Staged Draftだけに対応PNG Staging Entryが一意に存在し、Dropped Draftには存在しないこと、ID集合と件数が一致することを先に検証する。その後、Staged Draftだけを`CaptureFrameId`昇順で`CaptureRunReference`、既存`CaptureFrameRecord`、canonical sidecar、最終PNG／sidecarのstaging集合へ変換する。全件成功するまでDraft／元PNG stagingを消費せず、公開Registryへ部分的なRecordを登録しない。これにより`CaptureRunReference`、`CaptureFrameRecord`、`CaptureFramePngArtifactCodec`の「構築時に最終Manifestとhashが一致する」公開契約は変更せず、生成時期だけをfreeze後へ移す。

確定順は`Summary付きExport Snapshot構築 -> 最終TraceRunManifest生成 -> RunManifestContentSha256計算 -> CaptureFrameDraft全件Finalization -> Capture staging durable化 -> CapturePublicationPlan原子的確定 -> Trace bundle原子的公開 -> Capture Artifact個別原子的公開 -> 全件検証 -> 永続Capture Index原子的確定 -> CaptureComplete`とする。`CapturePublicationPlan`は公開bundleへ追加しないcanonical Schema v1の永続staging専用fileであり、`TestRunId`、最終Manifest hash、`CaptureFrameId`昇順の期待集合、各PNG／sidecarのstaging相対pathと最終相対path、byte length、content hashを固定する。

Planのcanonical JSONはUTF-8、BOMなし、末尾改行なし、余分な空白なしとする。top-level propertyは順に`SchemaVersion`（JSON integer、必須、値1）、`TestRunId`（JSON integer、必須、1～`long.MaxValue`）、`RunInitializationId`（string、必須、小文字ASCII 32桁hex、両run.init／readyと一致）、`RunManifestContentSha256`（string、必須、小文字ASCII 64桁hex）、`EntryCount`（JSON integer、必須、0～100,000）、`Entries`（array、必須、長さはEntryCountと一致）だけを持つ。各Entryは順に`CaptureFrameId`（JSON integer、必須、1～`long.MaxValue`、昇順かつ重複なし）、`PngStagingRelativePath`、`SidecarStagingRelativePath`、`PngFinalRelativePath`、`SidecarFinalRelativePath`（各string、必須）、`PngByteLength`、`SidecarByteLength`（各JSON integer、必須、1～`long.MaxValue`かつProfile上限内）、`PngContentSha256`、`SidecarContentSha256`（各string、必須、小文字ASCII 64桁hex）だけを持つ。integerは符号なし先頭0なしの最短10進表記とする。全string値はhash、Initialization ID、またはSchemaから導出する固定pathなので印字可能ASCIIだけを許し、`\uXXXX`、`\/`等のescape表現を含めずliteral ASCIIでserializeする。Decoderはparse後にcanonical bytesへ再serializeして入力bytesとの完全一致を要求し、意味が同じでもescape、空白、property順、integer表記が異なる入力をRejectする。null、浮動小数、指数表記、未知／欠落／重複property、Entry順違反、宣言件数不一致をRejectする。Schema最大16 MiB、最大100,000 Entry、相対path最大512 UTF-8 byteとし、Loaderはこれ以下の呼出側`maxPlanBytes`／`maxEntryCount`／`maxPathBytes`を必須で受け取る。file長と宣言件数を配列確保前に検査し、非seek streamは`limit + 1` byteまで試読してbufferへ保持するのはlimitまでとする。各PNG／sidecarもCapture Profileの1 file／Run総byte上限と呼出側上限を、全量確保前に長さとstreaming hashで検証する。

`CaptureStagingBaseRoot`と`CaptureFinalBaseRoot`はPlan外の信頼済み設定とし、互いに同一または祖先／子孫となる構成をRun開始前にRejectする。先頭0なし10進の`TestRunId`を`{runId}`として、Run専用rootをそれぞれ`CaptureStagingBaseRoot/runs/run-{runId}`と`CaptureFinalBaseRoot/runs/run-{runId}`へ決定論的に固定する。

両baseは完全修飾されたlocal absolute pathであることを要求し、relative path、drive-relative path、UNC、device path、extended pathを拒否する。baseのcanonicalizationは`Path.GetFullPath`で`.`／`..`を解決し、`AltDirectorySeparatorChar`を`DirectorySeparatorChar`へ統一し、filesystem root自身のseparatorを除く末尾separatorを除去する。stored pathとroot hash入力にはcase foldingとUnicode normalizationを行わない。baseの同一・祖先判定だけはsegment境界を尊重する`OrdinalIgnoreCase`で行い、caseだけ異なるbaseや祖先関係を保守的に拒否する。filesystem alias、reparse point、実体の存在確認は後続のlock／filesystem層の責務とし、この値契約では行わない。

新規開始とRecoveryは、rootを作成／列挙する前に`CaptureStagingBaseRoot/.locks/run-{runId}.lock`と`CaptureFinalBaseRoot/.locks/run-{runId}.lock`の2本をno-followでopenし、各OS handleを`FileShare.None`相当の排他共有Modeで取得する。両lockのabsolute pathをOSの正規化済みfull pathへ変換し、まず`OrdinalIgnoreCase`、同値時はordinalで比較した昇順へsortして、すべてのCoordinatorが同じ順で取得する。正規化後に同一となるlock pathは構成不正としてRun開始前にRejectし、暗黙に1本へ縮約しない。lock directory／fileは各信頼base root直下の固定名だけを許し、reparse pointを拒否する。

取得は非待機とし、2本目を含む途中の取得に失敗した場合は取得済みhandleを逆順に直ちに解放し、staging／finalのどちらのRun rootも作成、列挙、変更しないで`RunAlreadyOwned`としてbackpressureする。両handleの取得成功だけがRun root一組の排他的所有権を与える。Coordinatorは初期化からCaptureComplete後のstaging cleanupまたは明示abortまで両handleを保持するため、異なるstaging baseから同じfinal base／TestRunIdを狙うCoordinatorもfinal側lockで排除される。lock fileの存在や内容は所有権の証拠にせず、取得中handle集合だけを正本とする。プロセス終了／crashではOSが両handleを解放し、残った固定lock fileは次回同じ順序で再openできる。

両lock取得後に暗号学的乱数128 bitの小文字hex 32桁`RunInitializationId`を発行し、両Run rootを次の順で二相初期化する。`staging root作成 -> staging/run.init.tmp書込・flush・run.initへRename・directory flush -> final root作成 -> final/run.init.tmp書込・flush・run.initへRename・directory flush -> 両init照合 -> stagingとfinalへrun.ready.tmpを書いてflush・run.readyへRename・各directory flush`とし、両`run.ready`確定後だけ新規Capture受付を許可する。`run.init`はcanonical Schema v1で`SchemaVersion`、`TestRunId`、`RunInitializationId`、`RootRole`（`Staging`または`Final`）、`StagingRunRootSha256`、`FinalRunRootSha256`をこの順に持つ。Root hashは信頼baseから導出・正規化した各absolute Run rootのUTF-8 bytesに対する小文字SHA-256とする。`run.ready`はSchemaVersion、TestRunId、RunInitializationId、StagingInitSha256、FinalInitSha256をこの順に持ち、両rootで同一canonical bytesとする。両SchemaはPlanと同じUTF-8／BOMなし／空白なし／最短integer／literal ASCII／再serialize完全一致規則、最大4 KiBと必須の呼出側byte上限を使う。tmpは権威を持たず、init／readyだけが相互bindingの正本となる。

両lock取得後のRecoveryでは両rootを同時に調査する。一方だけが存在して有効な`run.init`を持つ場合、marker内の両Root hashと導出rootが一致し、既存rootに初期化許可file以外がなければ、同じRunInitializationIdで欠けたpeer root／init／readyを作って初期化を完了する。ただしstaging rootがなく、final rootに有効なinit／ready／`capture.index`とIndex記載の全Artifactが揃う場合は完了後cleanup済みの正常状態であり、staging rootを再作成しない。両rootに一致するinitがありreadyが片側／両側で欠ける場合も同じbytesのreadyを補完する。root作成後marker書込前にcrashした空directory、または非権威な`run.init.tmp`／`run.ready.tmp`だけを持つdirectoryは、排他lock集合、no-follow、導出path、空／tmp-onlyを確認して削除し同じRecoveryを再開できる。markerなしで他fileを持つroot、init／readyのTestRunId、InitializationId、Role、Root hash、相互hashが不一致なrootは削除／上書きせず`RunRootCollision`として両rootを隔離する。既存の完全初期化rootは、その後Plan／IndexとTrace ManifestのTestRunId、RunInitializationId、Manifest hashが一致する明示Recoveryにだけ開く。

許可file集合は、staging Run root直下では`run.init`、`run.ready`、各初期化`.tmp`、`publication.plan`、Phase 0／0.1用`publication.plan.tmp`、Phase 0.11用`publication.plan.nvenc-precommit.tmp`、固定`frames`／`chunks` subtree、final Run root直下では`run.init`、`run.ready`、各初期化`.tmp`、`capture.index`／`capture.index.tmp`、固定`frames`／`chunks` subtreeだけとする。`publication.plan.nvenc-precommit.tmp`はPhase 0.11の明示的な未確定fileであり、共通tmp昇格候補またはcommit markerとして扱わない。lock fileはRun root外の`.locks`だけに置く。初期化完了後の未知file、別Run marker、別Coordinatorによる同時所有はFail Fastし、既存fileを変更しない。これによりRunごとに1から再開する`CaptureFrameId`、固定`frames/{id}`、`publication.plan`、`capture.index`が他Runと衝突しない。

相対pathは`/`だけをseparatorとするNFC文字列とし、rooted／drive／UNC path、空文字、先頭／末尾`/`、空segment、`.`、`..`、`:`、NUL／制御文字、`\`をRejectする。同一種類内およびstaging／final各Run root内でordinal重複とWindows ordinal-ignore-case衝突をRejectする。さらにSchema v1では自由なpathを許さず、IDの先頭0なし10進表記を`{id}`として、`PngStagingRelativePath=frames/{id}.png.stage`、`SidecarStagingRelativePath=frames/{id}.json.stage`、`PngFinalRelativePath=frames/{id}.png`、`SidecarFinalRelativePath=frames/{id}.json`との完全一致を要求する。各Run rootと結合後にOS absolute pathへ正規化し、末尾separator付きの許可Run root配下であることをordinal-ignore-caseで再検証する。base rootから対象までの全既存componentについてsymlink、junction、mount point、その他reparse pointを拒否し、no-follow相当でhandleを開いた後にも最終解決先とfile identityを再検証する。検証とopen／renameの間にroot外へ差し替えられた場合はRecoveryを中止し、破損Plan／hard errorとして隔離する。

staging root内の各PNG／sidecarは一時名へ書き、dataとfile metadataをdurable flushしてから同root内の確定staging名へ原子的Renameする。全Entry確定後に`publication.plan.tmp`を同様にflushし、最後に`publication.plan`へ原子的Renameしてdirectory metadataをflushする。Windowsでは`FlushFileBuffers`相当の完了証拠を必要とし、fileまたはdirectoryのdurabilityを確認できなければTrace公開へ進まない。`publication.plan`をstaging集合の唯一のcommit markerとし、Plan確定前のfile群はRecovery対象Artifactとして解釈しない。Trace bundleの最終Rename成功前はCapture Artifactを1件も最終pathへ公開してはならない。

Trace bundle公開前にSummary追加、Snapshot／Manifest生成、Draft Finalization、Plan確定、`SaveAtomic`、最終Renameのいずれかが失敗した場合は、同じFrozen Runを`RetryableBeforeTrace`として保持する。immutableなFrozen通常Event列、Draft Registry、元PNG Staging Storeと、すでにdurableかつ再検証済みのCapture staging／Planは保持し、未確定の一時fileだけを除外する。21.4の規則で`PriorBundlePublishFailureCount`を1回増やし、次回は同じ`TestRunId`とFrozen入力からSummary付きTrace以降を再構築する。累積CountはSummary Eventのpayloadを変えるため`trace.bin`とそのcontent hash、`bundle.index`は変わるが、Summary件数、Run Context、Manifest propertyが同じなら`TraceRunManifest` bytesと`RunManifestContentSha256`は変わらない。保持PlanのManifest hashが再生成値と一致する場合は再利用し、不一致ならTraceを公開せずPlan／派生sidecarを再構築する。通常経路では自動破棄や別Runへの流用をせず、staging容量が不足すれば新規Capture Runをbackpressureする。

Trace bundleの最終Rename成功後は、そのManifest bytesとhashを永久に固定し、Summary／bundleを再生成しない。Capture Artifactの一部公開に失敗した場合は`TracePublishedCapturePending`に留まり、未公開の最終Record／PNG／sidecar stagingとPublication Planを保持して、同じ最終Manifestのまま欠落fileだけを再試行する。PNGとsidecarの双方が存在してPlanと一致すればidempotentな成功としてskipする。片側だけ存在する場合は、存在側のbyte length／hashと、sidecarなら最終Manifest参照も検証し、一致すれば保持して欠落側だけをstagingから原子的に公開する。存在するfileの内容不一致、Schemaで明示した未確定`capture.index.tmp`を除くPlan外file、またはsidecarのManifest参照不一致だけをhard errorとし、一致する既存fileを上書きしない。この段階の失敗はbundle公開失敗ではないため`PriorBundlePublishFailureCount`を増やさない。

`CaptureComplete`は、Publication Planの全期待`CaptureFrameId`についてPNGとsidecarの双方が存在し、各hash、sidecar内Record、最終Manifest hashの再照合に成功し、同じcanonical Plan bytesを永続Capture rootの`capture.index.tmp`へ書いてflushし、`capture.index`へ原子的Renameしてdirectory metadataをflushした後だけ成立する。`capture.index`はPlanと同じSchema v1 bytesを持つ永久的なCapture Artifact Indexであり、公開済みArtifactの期待集合・hashと完了状態を再起動後や後日のVerifierへ提供する。Trace bundle v1の一部にはせず、Capture Artifact集合の必須fileとする。既存`capture.index`が同じbytesならidempotent成功、不一致なら上書きしないhard errorとする。

`capture.index.tmp`はfinal Run root直下で唯一許可する未確定fileであり、CaptureCompleteの証拠、期待集合の正本、Plan外Artifactとは扱わない。Recoveryはno-follow／reparse検証後にtmpをbounded Loaderで読む。`capture.index`がなく、tmp bytesが現行`publication.plan`のcanonical bytesと完全一致し、TestRunId／Manifest hashもRun root／Trace bundleへ一致し、Plan記載の全最終Artifactが再検証済みの場合だけ、再flushして`capture.index`へ原子的Renameして再利用する。Indexが存在しtmpも同Indexと完全一致する場合はtmpを削除してcleanupを続行する。tmpが非canonical／途中書込みなら、Run rootの排他的所有とno-follow検証を完了した非権威fileに限り理由を記録して削除し、Planから再生成する。canonicalだがPlan／Index、TestRunId、Manifest hashのいずれかと不一致なら別所有者または内容衝突とみなし、削除／上書きせずRun rootごとhard errorへ隔離する。root列挙、許可file集合、T-082はこのtmp例外を明示的に扱う。

`capture.index`確定後にだけDraft Registryを解放し、EntryごとのPNG／sidecar stagingと一時fileを削除してdirectoryをflushし、`publication.plan`を最後に削除して再度directoryをflushする。最終Capture rootの`run.init`／`run.ready`は永久に保持してIndexのRunInitializationIdを検証できるようにする。staging rootはPlan削除後に`run.ready`、`run.init`の順で削除・flushし、空のRun rootを除く。この順序の途中でクラッシュした場合、`capture.index`があればその期待集合とfinal markerからCaptureCompleteを再検証して、残るPlan／staging／markerのcleanupを排他lock集合の保持下で再開する。IndexがなくPlanだけが残る場合は従来どおり公開またはcleanupを再開する。Plan削除後に残ったstaging fileは公開へ使用せず安全なorphan cleanup対象とする。一部成功をRun完了として通知せず、`TracePublishedCapturePending`中はPlanや必要stagingを自動清掃しない。完了通知は`capture.index`のdurable確定後に限り、通知前クラッシュ時は次回Loaderが同Indexと全Artifactを検証して再通知または既完了として復元する。cleanup／通知状態の確定後に両OS lock handleを逆順で最後に解放する。

再起動Recoveryは、信頼済みbase rootからTestRunIdでRun rootを導出して排他的所有権を得た後、rootを列挙し、`capture.index.tmp`を上記規則で最初に解決する。その後`capture.index`があれば同じbounded／path-safe Loaderで優先して読み、公開済みTrace bundleのManifest hashと全最終Artifactを照合して完了を復元する。Indexがなければ`publication.plan`を読み、Trace bundle、Plan、staging／最終fileを照合してから欠落fileの公開を再開する。Trace bundleが存在しないが有効なPlanがあるpre-publication stagingは自動公開せず`OrphanedPreTrace`として隔離し、明示的な同一Run回復または管理操作まで保持する。管理操作で放棄したRunは`TraceOnlyCaptureIncomplete`として永久にCaptureCompleteにならない。

ライブUIやCapture SchedulerがRun相関に使う正本は`TestRunId`、`CaptureFrameId`、`CaptureDraftRunContext`であり、未確定なManifest content hashではない。最終Artifactだけが`RunManifestHash`を必須とし、sidecar Loaderは従来どおり渡された最終Manifestを再hashして一致を要求する。これにより、最終EventCount、`WasHistoryOverwrittenAtTrigger`、Summaryがfreezeまで未確定でも、Capture ArtifactとTrace bundleは同じ最終Manifest hashへ結合される。

ゲーム固有ID、TestRunId、意味付きController PoseはUnity側が記録する。OpenXR API Layerが`xrLocateSpace`を観測しただけではSpaceのゲーム上の意味を確実に識別できないため、Unity TraceとAPI Layerは固定長共有メモリまたは低頻度IPCで相関情報を交換する。文字列はRun開始時の辞書へ保存し、各フレームでは整数IDを使う。

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

#### 21.7.6 Run Manifestと環境差

Unityプロジェクトで固定できるGraphics API、MSAA、Dynamic Resolution、Stereo Mode、App Layer構成は設定ファイルと起動引数で固定する。一方、Meta Runtime、Quest OS、GPU Driverは更新され得るため、固定を仮定せず各Runで次を記録する。

```text
Unity Version / OpenXR・URP Package Version
Meta Runtime Version / Quest OS Version
GPU Name / Driver Version / Encoder Name
Graphics API / Color Format
Swapchain Width / Height / Image Count / Array Size / Sample Count
Image Rect / Render Scale / Refresh Rate / Link設定
Capture Profile Version / API Layer BuildId
```

Run Manifestを正規化してHash化し、Hashが異なるRunは同一環境の回帰比較へ自動投入しない。固定Profileに一致していてもRuntimeやDriverが変わった場合は別環境の測定として保存する。

### 21.15 Capture Evidence Backend 境界（Phase 0 正本）

Phase 0 の映像証拠は引き続き「1 Capture Frame につき PNG 1 件と canonical JSON metadata 1 件」を生成する。ただし、これは共通 Capture API の成果物モデルではなく、最初の実装である `PngJsonCaptureEvidenceBackend` の契約である。共通境界は GPU readback より前に置き、Producer は codec 非依存の `CaptureFrameEnvelope` と caller-owned の `CaptureSurfaceLease` を `ICaptureEvidenceSession.TrySubmit` へ渡す。共通 Coordinator は `AsyncGPUReadback`、raw `NativeArray<byte>`、PNG encode、JSON schema、拡張子、backend 固有 queue を参照しない。

`CaptureFrameEnvelope` は TestRunId、CaptureFrameId、Unity/OpenXR frame ID、display timing、head/controller pose、Slash/Object/Task 相関、CaptureSource、Eye、ImageRect、PixelLayout、ColorSpace、TestCase/Build/Scene/RandomSeed、CommitPathId、CaptureProfileId を保持する。encoded bytes、PNG/JSON path、byte count、content hash、encoder 設定、将来形式の packet/segment 情報は保持しない。

Submission の所有権は線形とする。`Accepted` のときだけ Surface 所有権を backend へ移し、有効な Work Token を返す。`Backpressured` または `NotAccepting`、および例外では caller が同じ Surface を所有したままとする。Backendは固定容量のWork領域とCompletion領域を使い、stale／foreign／duplicate tokenを拒否する。`TrySubmit`のAccepted線形化点より前にFrame Completion 1件と`MaximumArtifactCountPerSubmission`件を欠落なく公開できる容量および必要なBackend資源を一括確認・予約する。全資源を同じtokenへ束縛できた場合だけ`Accepted`を返し、いずれかが不足する場合は内部予約を一切残さずSurface所有権も受け取らず`Backpressured`または`NotAccepting`を返す。Accepted後のCompletion公開は容量不足で失敗せず、受理したtokenごとに`CaptureFrameCompletion`をexactly onceで先に通知し、その`ProducedArtifactCount`に対応するsubmission-scoped `CaptureArtifactCompletion`を各Artifactにつきexactly onceで通知する。Phase 0.11の複数Frame共有chunkは後述のCoordinator所有Contextで別に終端し、任意のFrame Work Tokenへ偽装してはならない。Source SurfaceのBackend内部解放時点はFrame処理の終端結果と期待submission Artifact数を公開する`CaptureFrameCompletion`とは別概念である。Frame completion、submission artifact completion、Run chunk terminal result、Run publication completion は別概念であり、単一completionへ混在させない。

受付前に共通Coordinatorはbackendが宣言する1 submission当たり最大Artifact数をRegistryへだけ予約し、その成功後に現行`ICaptureEvidenceSession.TrySubmit`を呼ぶ。Backendが`Backpressured`／`NotAccepting`を返すか例外を投げた場合、Coordinatorは同じRun／FrameのRegistry予約をexactly onceで取り消し、Surface所有権がcallerに残ることを確認する。Backendは自身のWork／Completion／Surface予約を`TrySubmit`内部で管理し、拒否または例外時には全内部予約を返却してから制御を戻す。Accepted時だけRegistry予約を返却されたWork Tokenへ一意に結び付ける。受理後はFrame Completionの実submission Artifact数へRegistry予約を縮小し、`ProducedArtifactCount`件のsubmission Artifact Completionを`Staged`または`Failed`として必ず1回収集可能にする。Backend内部領域はSurface／bitstream等の所有権が全て解放され、必要なCompletionが全て収集済みになった後だけ再利用する。Main Thread Coordinatorは1 Tickに最大`CompletionDrainBudgetPerMainTick=8`件までbounded pollし、Frameをsubmission Artifactより先に反映する。Registry満杯はBackend呼出前、Backend内部容量不足はAccepted前に検出し、Accepted後に初めてCompletion容量不足を検出する経路を設けない。

Phase 0.11の責務は次の4系統へ固定する。ここで「単一論理Consumer」は固定2本のOrdered Submit WorkerとOrdered Output Workerから成り、Plan I/Oを行う非所有Publication Service Workerとは別である。Main／Render Thread Producerは固定容量資源をAccepted前に予約し、Accepted線形化点でWork Tokenを固定Submission Queue末尾へ厳密に1回入れて待たずに戻る。このFIFO挿入順がAccepted順の正本であり、Work TokenのSlotIndex、Generation、CaptureFrameIdまたは受付時刻をsort keyとして再構築しない。Submit WorkerはSubmission Queueを単独消費し、先頭Workについて`NvEncEncodePicture`成功なら`Submitted`、submit前失敗／取消なら`FailedBeforeSubmit`を固定Submit-to-Output Queueへ同順で厳密に1件渡す。Output Workerは同Queueを単独消費し、`Submitted`では先頭Eventだけを待ってBitstream ownershipを取得してInput Surface返却、chunk append、streaming hash／ByteLength更新、Frame Relation追加を行い、`FailedBeforeSubmit`ではappendせず所有資源を安全に解放またはQuarantineする。両variantのFrame CompletionはOutput Workerだけが生成する。Main Threadは固定容量Completion Queueをbounded pollしてDraft／Traceの正式状態だけを反映する。単一`NvencCaptureRunCoordinator`は全Queue、Backend／両Worker、Context、Session Ownership Lease、Trace Freeze状態、局所Registry slotおよびDispositionをRun開始からPlan commitまたはIncompleteまで所有し、両Worker静止後のPlan I/Oは所有権を渡さずPublication Serviceへ非同期要求する。

```text
Main／Render Thread Producer --TrySubmit--> 固定Submission Queue
                                              |
                                              v
                                    Ordered Submit Worker
                                              |
                                              v
                               固定Submit-to-Output Queue
                             Submitted | FailedBeforeSubmit
                                              |
                                              v
                                    Ordered Output Worker
                                              |
                                              v
                                   固定Completion Queue
                                              |
                                              v
                                  Main Thread bounded poll

NvencCaptureRunCoordinator --非所有・非同期要求--> Publication Service Worker
```

Phase 0.11の停止順は`StopAccepting -> BeginDrain -> Submit Workerが全Accepted WorkのSubmitted／FailedBeforeSubmit recordを生成 -> Submit Worker Join -> Output Workerが全record、Frame Completionおよび所有資源を回収 -> Output Worker上でchunk FinalizeまたはAbandon -> Context terminal result回収 -> Output Worker Join -> 全資源／予約ゼロ確認 -> Trace Freeze -> Plan commitまたはIncomplete`とする。Submit Worker Join前にSubmit-to-Output Queueを破棄せず、Output WorkerのFinalize／Abandonまたはterminal result回収をOutput Worker Join後へ送らず、各Join成功後に同Workerへ新しい処理を要求しない。Backendの`TryJoin`はこの固定内部順を実行し、両Workerが静止した場合だけ成功する。

Phase 0.11はPhase 0完了後の破壊的な内部API変更として、1 Run＝1 chunk専用の`NvencRunChunkContext`をCoordinator内部へ追加する。汎用`ICaptureRunArtifactProducer`、`MaximumRunArtifactCount`、Run Artifact Token／Reservation Receipt、atomic factory、Producer identityまたは複数chunk能力交渉を設けない。CoordinatorはFrame受付開始前にRegistry容量1件、単一streaming writer、固定長accepted Frame ID領域を含むContextを厳密に1個だけ作成し、どれかを確保できなければNVENC Backendを開始しない。ContextはTestRunId、`CaptureRunRootLayout`、Registry予約1件、writer 1個、accepted順Frame ID列、checked ByteLength／streaming content hash、`Open / Finalized / Abandoned`状態を単一所有する。Contextとwriterは外部Pluginへ公開せず、別Backend／別Runから注入・差替えできないtrusted internal実装とする。Phase 0.11 NVENCは`MaximumArtifactCountPerSubmission=0`とし、Frame Work TokenはFrame encode provenanceだけを表す。

Contextの終端結果はauthority Receiptではなく、Contextだけが内部状態から構築できるimmutableな`NvencChunkFinalizationResult`とする。Resultは有効な`CaptureArtifactDescriptor`、accepted順の`CaptureArtifactFrameRelation`、確定staging relative pathを保持し、呼出側からDescriptor、Relation、path、ByteLengthまたはcontent hashを注入できない。状態遷移は`Open -> Finalized`または`Open -> Abandoned`だけとし、両終端は相互排他的かつexactly once、終端後append、再Finalize、Finalized後AbandonまたはAbandoned後Finalizeを禁止する。terminal Resultの全field構築後に短いlockまたはrelease semantics相当で終端状態を公開し、Coordinatorは同じlockまたはacquire semantics相当のbounded poll後だけResultを読む。`NvencCaptureRunCoordinator`は自身が所有するContextの`Finalized` ResultをCoordinator内部の単一`NvencRunLocalRegistrySlot`へexactly once登録する。slotはappend-only固定状態`Empty=0 / Registered=1 / Committed=2`を持ち、`Registered`時にTestRunId、Context identity、Descriptor、Relationを保持する。同一Runで2件目を登録せず、別Context／Result／Runからの差替えを副作用前に拒否する。Plan commit前の既知失敗では同じCoordinatorだけが`Registered -> Empty`として局所Entryと予約を破棄でき、Plan rename成功後は`Registered -> Committed`へexactly once進めて以後破棄しない。外部へ渡すRegistry handle、owner generation、`PlanConsumed`状態または汎用Token／Receipt相互認証を設けない。`Abandoned`では未登録予約をexactly once解放し、Descriptor、Plan EntryまたはRun Artifact Completionを生成しない。Frame Tokenをchunk登録へ偽装・流用しない。

Phase 0.11専用Coordinatorはprocess-localな`NvencRunEvidenceDisposition`をappend-only固定値`Running=0 / Finalized=1 / Committed=2 / Incomplete=3 / CommitOutcomeUnknown=4`で保持する。初期値は必ず`Running`とし、未知値をfail-closedに扱う。Context terminal回収、局所Registry登録、`TryJoin`、全所有資源／予約ゼロ確認およびTrace sealが成功したPlan commit候補だけを`Finalized`とする。Plan renameが既知成功した同一点で局所Registry slotを`Committed`へ進め、Dispositionも`Committed`へexactly once確定する。rename呼出し前までの既知失敗、明示Abort、Contextの`Abandoned`、登録／Join／資源解放／Trace seal失敗、Result／Relation不整合またはpre-commit書込み失敗は`Incomplete`へ確定する。renameが例外、process interruptionまたはAPI結果不明により成功／失敗を同processで証明できない場合は`CommitOutcomeUnknown`へ確定し、その場でPlanやfileを再読込、rename、削除またはRegistry取消せず、次回Recoveryへ判断を委ねる。`CommitOutcomeUnknown`は旧process内だけの診断状態であり、新processはこれを復元せずfile集合から独立にRecovery dispositionを決める。`Incomplete`と`CommitOutcomeUnknown`ではCaptureCompleteを発行しない。理由は生存processのRun結果またはRun外診断に限定し、Frozen Traceへの追記、新journal／receiptによるcrash-durable化を要求しない。Finalization期限超過はscenario／Capture失敗を確定するが、Publication Serviceの未帰還I/OまたはWorkerを無視してSession Leaseを解放する権限にはならない。

Phase 0 backend は backend 内部で `AsyncGPUReadback -> PNG encode -> JSON metadata -> generic artifact staging` を行う。PNG と JSON の内容、FrameId/Trace 相関、pose/timing/run 意味情報は維持する。旧 `ICaptureFrameEncodeService`、`CaptureFrameEncodeSubmission`、`CaptureFrameEncodeCompletion`、`CaptureFrameEncodeCompletionCoordinator` という共通名は廃止し、互換経路を残す場合も `PngJson*` 実装詳細として隔離する。既存 `CaptureFramePng*` 型は Phase 0 backend の内部互換部品としてのみ再利用でき、共通 Coordinator、共通 Publication、共通 Recovery の型へ露出させない。

永続成果物の正本は `CaptureArtifactDescriptor` とする。Descriptor は ArtifactId、append-only の ArtifactKind、FormatId/Version、staging/final relative path、ByteLength、ContentHash を持つ。Artifact completion の Work Token は生成処理の provenance であり、Frame と Artifact の意味上の関係とは分離する。関係は独立した `CaptureArtifactFrameRelation` で表し、空集合を Run scoped、複数 ID を複数 Frame 共有 Artifact とする。`CapturePublicationPlan` は Run 相関、Descriptor 集合、この多対多関係だけを canonical に保持し、`.png`/`.json` 固定 field や「1 frame = 1 encoded file」を要求しない。Phase 0 では FrameImage と FrameMetadata の 2 Artifact を各 Frame に関連付けるが、別 backend は 0/1/複数 Artifact、複数 Frame segment、Run scoped Artifact を生成できる。例外として`NvencBringUpProfileV1`のPlan Builder入口は、同じ`NvencCaptureRunCoordinator`がDisposition=`Finalized`、局所Registry slot=`Registered`、`ArtifactKind=FrameSequence`厳密1件、そのRelationが全Staged Frame ID集合と厳密一致することを確認した場合だけ許可する。Artifact 0件、部分Relation、余分なFrame、別Kind、別Contextまたは他のDispositionではPlanを構築しない。Plan commit後のPublication／Recovery入口は完成`publication.plan`と通常のDescriptor／Relation相関をauthorityとし、process-localなbundle、handleまたはDispositionの復元を要求しない。

共通 Publication の Run lifecycle 正本は `CaptureEvidenceRunPublicationCoordinator` とする。Phase 0／0.1の既存 `CaptureFrameFreezeTerminalCoordinator.TryCompleteEvidenceRun` は従来どおり受付停止、queued cancellation、全Frame／submission Artifact Completion反映、backend join、未処理slotなし、Artifact予約ゼロ、Trace `Frozen`、同一Runの `CaptureRunInitializationSession` に対応する有効なOwnership LeaseによるOS lock保持を確認した後だけ `CaptureEvidenceRunFreezeReceipt` を発行する。Phase 0.11では単一`NvencCaptureRunCoordinator`が受付停止、queued cancellation、全Frame Completion反映、Context Finalize／Abandon、terminal回収、局所Registry登録または予約解放、backend join、未処理slot／所有権／予約ゼロ確認、Trace Freeze、Plan commit、通常PublicationまたはAbortまでを所有する。Phase 0完了後の破壊的な内部変更として、`CaptureRunInitializationSession`は`IDisposable`を実装せず、通常のSession参照、Freeze Receipt、Publication ResultまたはOpen Outcomeから直接OS lockを解放するAPIを持たない。実際の`CaptureRunLockLease`は非公開`CaptureRunInitializationSessionOwnershipLease`だけが内包し、その`Dispose()`だけがlockを解放できる。Receipt／Resultが公開するLock情報は参照同一性確認用の非所有`CaptureRunLockIdentityEvidence`へ置換し、実Lease参照やDispose能力を返さない。Session factory／Bootstrap／RecoveryはSessionと初期Ownership Leaseを同時に返す。Phase 0／0.1の既存Coordinatorと関連Result／ReceiptもSessionとLock Identityを非所有の相関証拠として保持し、別fieldのOwnership Leaseだけを解放するよう移行する。foreign／stale Leaseと二重解放を副作用前に拒否し、全既存Session生成・解放テストをこのLease契約へ更新する。Phase 0.11ではCoordinator間のLease移譲、owner generation更新またはFreeze Receipt消費によるOwnership Bundle生成を行わない。

Phase 0.11専用の非所有`NvencRunPublicationService`は、`NvencCaptureRunCoordinator`が保持するSession Ownership Leaseを取得、Disposeまたは移譲せず、Plan bytesの書込みとcommit後の既存Publication処理を専用Workerへ非同期要求し、Coordinatorへ固定容量のterminal resultを返す。Main／Render ThreadはI/O完了を待たずbounded pollだけを行う。terminal resultはrename呼出しを含む要求処理が帰還し、Service-owned file handle、request slot、bufferおよびI/O commandがすべて回収され、Workerが当該Runへ追加I/Oを発行しない静止状態になったことをall-or-noneで証明する。Coordinatorはこのterminal resultをacquire確認する前にSession Ownership Leaseを解放しない。Trace Freeze成功とDisposition=`Finalized`を確認後、完全なcanonical Planを専用`publication.plan.nvenc-precommit.tmp`へ書き、closeしてから同じRun rootの`publication.plan`へ非上書きrenameするようServiceへ要求する。fileまたはdirectory metadata flushを要求しない。rename呼出し前までの既知失敗では`Incomplete`へ進み、局所Registry slot破棄とabort cleanupを行う。renameが既知成功なら局所Registry slotとDispositionを`Committed`へ進める。renameの成否が不明なら`CommitOutcomeUnknown`とし、同processでfinal／tmpを再読込して推定せず、Registry slot、Plan、tmpまたはchunkを削除・rename・上書きしない。terminal result回収後にだけCoordinatorはbackend／Context等のprocess資源とSession Ownership Leaseを解放する。この時点で次回の具体的Recovery dispositionを決めず、再起動後のRun初期化／Recovery Plannerによるfile集合分類へ委ねる。timeoutまたは明示取消後もWorker／OS rename callが帰還せずterminal resultを得られない場合はLeaseとOS lockを保持し、同processからRecoveryを開始しない。ゲームは継続できるが、lock解放はWorker静止の確認またはprocess終了に委ねる。

`Committed`後も同じCoordinatorがSession Ownership Leaseを保持したまま、同じ非所有Serviceを介して既存のverify／publish／CaptureComplete処理を1回だけ試行する。成功時はService terminal resultと通常cleanup後にLeaseを解放する。commit後のverification、publishまたはCaptureComplete失敗ではPlan、Registry相当情報またはchunkを取消・削除せず、Service terminal result回収後にprocess資源とLeaseを解放して`PublicationRecoveryRequired`へ終端し、次回Recoveryへ委ねる。同process内の所有権移譲や無期限再試行を行わない。OS crash／電源断でrename済み`publication.plan`またはchunkを失うことはPhase 0.11の許容されたCapture喪失であり、durable commitを主張しない。Phase 0／0.1の共通`BuildAndPersist`は従来どおり`publication.plan.tmp`のdata／file metadata flush、非上書きrename、directory metadata flush完了までをdurable commit条件とする。

`IsFullyDrained`もqueued cancellation、Context terminal回収、Run chunk予約と未確定chunk所有権を含めて再判定する。Phase 0.11のPlan永続化は同じ`NvencCaptureRunCoordinator`が保持するContext、Disposition=`Finalized`、局所Registry slot=`Registered`およびSession Ownership Leaseを必須とし、同じ `CaptureRunRootLayout` を保持する単一 `CaptureArtifactFileStore` をPlan Store兼Artifact Storeとして選択するため、Run AのPlanとRun BのArtifact Storeを組み合わせられない。再起動時は既存 `CaptureRunInitializationOpenOutcome` が `PublicationRecoveryRequired` とOS lock保持を証明し、同一RootLayoutである場合だけ同じStoreの `ReadOrRecoverPlan` から汎用 `CapturePublicationPlan` を復元する。CoordinatorはSnapshotのPlan TestRunIdがOutcomeとStoreのTestRunIdの両方に一致することを確認して `CaptureEvidenceRunRecoveryInspectionReceipt` を発行し、Recovery継続は同じCoordinator発行の有効なReceiptだけを受理するため、別Run Snapshotを差し替えられない。Phase 0／0.1の`publication.plan.tmp`だけが残る場合は従来どおり、固定上限内のcanonical文書でRun相関も一致するときだけ非上書きで`publication.plan`へ昇格できる。Phase 0.11の`publication.plan.nvenc-precommit.tmp`はcanonical、Run相関一致またはchunk存在の有無にかかわらず絶対に昇格せず、OS lock下で隔離または全体破棄する。両tmpの同時存在、tmpとfinalの不正な組合せ、別Run、上限超過はcollisionとして自動昇格せず停止する。共通tmp不正は変更せず報告し、lock下の明示的な`DiscardInvalidTemporaryPlan`だけが再検証後に正確なtmpを破棄できる。NVENC専用tmpはPhase 0.11のorphan／incomplete cleanupだけが正確なpathとno-follow検証後に破棄できる。読み取りは固定byte上限+1で停止し、JSON object化前の無割当preflightでArtifact数、Frame Evidence数、1 Frame当たりArtifact参照数、各文字列長を検査し、canonical再serializeと完全一致しない文書を拒否する。旧`PngJsonCapturePublicationPlan`系は既存Phase 0ファイルとの互換専用であり、新規共通Publication／Recoveryの正本にはしない。

`ICaptureArtifactStore`は形式を解釈せず、既存のreceipt付きstaging write、長さ／hash検証、非上書きpublish、staging／final verificationを担当する。全Artifact検証はRun rootから導出したcanonical pathをno-follow相当で安全にopenし、同じhandleから事前上限付き固定bufferまたはbounded pooled bufferへ反復readして、checked累積ByteLengthとincremental SHA-256をEOFまで更新するstreaming verificationとする。`FileInfo.Length`だけを正本にせず、実read長、EOF、DescriptorのByteLength／ContentHashが全て一致した場合だけ`MatchesExpected`とする。file不存在、宣言長より短い／長い、途中I/O失敗、checked長overflow、読取り中変化、hash不一致、reparse point／不正file種別、path／Run相関不一致を区別してfail closedに返す。可能な限り同じopen handleを検証終了まで保持し、検証目的のseek／再open／複数passを行わない。対応filesystemではopen時にwrite／delete sharingを拒否し、open直後とEOF後のhandle由来file identity／lengthを照合する。既存writerその他の変更権限が残る等、この区間のfile不変性を安全に証明できない構成は`FileChangedDuringRead`またはUnsupportedとしてfail closedにし、path再解決、全長配列または追加passで推測補完しない。

検証memoryはArtifact ByteLengthに対してO(1)とし、buffer上限をStore構築前に固定してArtifact長で拡張しない。terminal成功、Absent／Mismatch、EOF、例外、取消の全経路でrented bufferを返却する。`File.ReadAllBytes`、Descriptor長を配列長にする確保、Artifact全長の`byte[]`／`MemoryStream`／managed string、最大256 MiBのSlot別事前確保、payload複製またはformat decodeによるhash代替を禁止する。bufferを取得できない場合は無制限確保やMain Thread同期へFallbackせず、後述するcommit境界別のDispositionへfail closedにしてゲームを継続する。streaming read、hash、EOF待機、buffer取得はPublication Service／Recovery Workerだけが行い、Main／Render Threadはterminal resultをbounded pollするだけとする。

既存`CaptureArtifactVerificationStatus`の`Absent / MatchesExpected / Mismatch / Invalid`の意味と値は維持し、内部`CaptureArtifactVerificationResult`へappend-only固定値の`CaptureArtifactVerificationFailureReason`を追加して診断を一意にする。値は`None=0 / FileAbsent=1 / ShorterThanDeclared=2 / LongerThanDeclared=3 / HashMismatch=4 / ReadIoFailure=5 / CheckedLengthOverflow=6 / FileChangedDuringRead=7 / ReparsePointOrInvalidFileKind=8 / PathOrRunCorrelationMismatch=9 / BufferUnavailable=10 / Cancelled=11`とし、成功時だけ`None`、Absent時だけ`FileAbsent`を許可する。既知の入力／I/O不一致を例外だけで失わずResultへ固定し、programmer contract違反は従来どおり例外としてよい。このReasonはprocess内診断でありDescriptor、Plan、IndexまたはCapture Run Manifest schemaへ追加しない。

内容検証結果と検証実行可否を混同しないため、内部`CaptureArtifactVerificationExecutionDisposition`を`None=0 / Completed=1 / Deferred=2`のappend-only固定値で追加する。`None`は未初期化であり分類、cleanup、PublicationまたはCaptureCompleteの根拠にできない。`Completed`だけが`Absent / MatchesExpected / Mismatch / Invalid`の内容Statusを持ち、`BufferUnavailable`をReasonにできない。`Deferred`は有効Descriptor、Status=`None`、Reason=`BufferUnavailable`、observed length 0だけを許可し、file不存在、不一致、collisionまたはcleanupを意味しない。既存classifierが`Invalid`をcollisionへ直結する経路へ`Deferred`を渡さず、公開Schema、Plan、Index、Manifestまたはdurable journalへExecution Dispositionを追加しない。

Phase 0.11のbuffer不足は線形化点ごとに次へ固定する。(1) Run開始前のfilesystem capability確認または固定buffer構成に失敗した場合は受付前にUnsupportedとし、Run root／Plan／chunkを作らない。(2) Plan commit前の既知の取得失敗はWorker上で追加待機せず`Incomplete`として明示Abortできるが、Publication Service terminal resultを回収する前にcleanupまたはLease解放を行わない。(3) Plan commit後のFresh publish／final verificationで取得できない場合は局所slotとDisposition=`Committed`、Planおよびchunkを無変更で維持し、Service terminal回収後に`PublicationRecoveryRequired`へ送ってpre-commit cleanupを禁止する。(4) Recovery中に取得できない場合は`Deferred`としてその試行を非変更で終了し、Artifact内容またはfile集合を未分類のまま維持してretryableなRecoveryを許可し、Mismatch、collision、Incomplete／orphan cleanupまたはCaptureCompleteへ変換しない。全経路でMain／Render Threadを待たせず、Service terminal確認後にだけSession Ownership Leaseを規定経路で解放する。

Phase 0／0.1の`WriteStaging`、staging write receipt、`Flush(true)`、file／directory durability、staging verification、final verification、Publication、Recovery、CaptureCompleteおよびPNG＋JSON Descriptor／Plan／Indexの意味は変更しない。実装は同じstreaming verificationへ移行できるが、memory削減をdurabilityまたは検証回数の省略理由にしない。形式固有妥当性はbackendまたは形式別verifierの責務とする。RecoveryはPlanのDescriptor集合だけを走査してfinal一致、確定済みstaging一致、欠落、不一致を分類し、不一致をcollisionとして停止し、正しい確定済みstagingがある欠落finalだけをpublishする。Backend固有の未確定`.partial`はDescriptorまたはPlanの一部ではなく、各Backendの明示契約に従って無視または破棄できる。CaptureCompleteは規定の全Descriptorのfinal verification合格または後述する同一処理内Publish Receiptの有効な再利用後に限る。OS固有no-follow、safe handle、非上書き移動およびdirectory metadata durabilityはStore backendのcapabilityであり、path文字列だけで保証済みと扱わない。

Phase 0.11だけは既存`ICaptureArtifactStore.WriteStaging`を呼ばず、Context内部の単一streaming writerを使用する。writerは同じ`CaptureRunInitializationSession`、OS lock、`CaptureRunRootLayout`およびpath安全規則へconstructor時に結合され、許可された単一`.partial` pathだけを非共有handleでopenする。append中はchecked ByteLength、streaming content hash、accepted CaptureFrameId列をwriter自身が更新する。Output Worker上の`TryFinalize`はappend停止、最小確定条件、Descriptor／Relation予定値を検証し、hash／length確定、close、同じRun root内の確定staging pathへの非上書きrenameがすべて成功した後だけContext内部の`NvencChunkFinalizationResult`を厳密に1件構築して`Finalized`を公開する。別writer、別Run、別Descriptor／Relation／pathを注入するAPI、Finalization Receipt、Run Artifact Completionを設けない。Resultはprocess-levelのclose／rename成功だけを表し、`Flush(true)`またはOS crash／電源断durabilityを証明しない。

Phase 0.11 Freshは一般`Publish(CaptureArtifactDescriptor)`のskip flagを公開せず、trusted internalな専用`PublishFreshNvencChunk`相当のStore／Publication Service操作を追加できる。この操作は同一process、TestRunId、OS lock、`NvencRunChunkContext`、writer identity、`NvencChunkFinalizationResult`、Context内部生成Descriptor／Relation、close済み確定staging path、未移譲ownershipおよびfinalへの非上書き移動を参照同一性で検証する。stagingとfinalが同一filesystem上で安全なrenameとして成立し、必要なno-follow／handle安全性をStoreが証明できる場合だけ、stagingのcontent全read／hashを省略してfile種別、reparse point、path相関、存在およびByteLengthを検査し、finalへ非上書きrenameする。別process、Recovery、一般Artifact、外部注入Descriptor、別Context／Run／Store、ownership移譲済み、cross-volume copy相当またはcapability不明では専用経路を拒否し、全量配列、Main Thread検証、hash省略または同期Fallbackを行わない。

rename後のfinalはPublication Service Workerが共通streaming verificationで全内容を厳密に1回read／hashする。成功時だけ既存`CaptureArtifactPublishReceipt`を発行し、このReceiptはStore identity、TestRunId、同一Descriptor、final path、verified ByteLength／ContentHashおよびverification完了を意味する。同じOS lock、同じStore／Descriptor／final path、同じPublication Service要求内でReceiptが有効な間はCaptureComplete判定へ再利用し、同じchunkを再度全hashしない。外部processによる同時改変をこの区間の即時検出対象とせず、信頼済みRun rootとOS lock下でfile不変とみなす。これを許容できない構成ではReceipt再利用を行わずBackendをUnsupported／Incompleteにし、暗黙の再hashへ切り替えない。final verificationのlength／hash不一致またはI/O失敗ではDescriptor／hashを書き換えず、再encode、canonicalize、H.264解析／修復またはCaptureCompleteを行わず、既存のcommit前／後境界に従うPublication／Recovery collisionまたはIncompleteへ送る。

Phase 0.11 Fresh成功処理のchunk hash回数は、Consumer append中のincremental hash 1系列をdisk再読込みに数えず、Publicationのfinal streaming verificationによる全file read／hashを厳密に1回、同じ処理のCaptureCompleteではPublish Receipt再利用により0回追加とする。P50／P95／P99またはwall-clock SLAは追加しない。新processのRecoveryではprocess-local Context、Finalization Resultまたは旧Receiptを信頼せず、PlanのDescriptorに対してfinal／staging Artifactを共通streaming verificationで少なくとも1回再検証する。同一Recovery分類内の重複hash削減に新しいauthority体系が必要ならPhase 0.11では最適化せず、まず固定memory化だけを必須とする。

`Incomplete`のabort cleanupは、Run開始から同じ`CaptureRunInitializationSessionOwnershipLease`を保持する`NvencCaptureRunCoordinator`だけが行う。局所Registry slotが`Registered`なら同じContextとの相関を確認して`Empty`へ戻し予約を解決し、未登録予約だけが残る場合もexactly once解放する。Publication Serviceへ要求済みなら、要求のterminal resultとWorker静止を先に確認し、未帰還ならfile cleanupとLease解放を開始しない。その後にContextの`.partial`、Plan未登録の確定staging file、`publication.plan.nvenc-precommit.tmp`、未消費buffer／handleをbest-effortで回収し、Context／writer／BackendをDisposeした後、`finally`相当でSession Ownership Leaseを厳密に1回Disposeして2本のOS lockを既存の逆順契約で解放する。通常のSession参照から解放を試みるAPIを設けない。cleanup失敗でもlock解放を抑止せず、失敗理由はRun外診断へ記録し、Plan、Capture IndexまたはCaptureCompleteを生成しない。削除できなかった`.partial`、NVENC専用tmp、orphan、`run.init`／`run.ready`またはRun rootは残存を許容し、次回RecoveryではPlanのないrootを自動公開せず既存のorphan／incomplete相当として隔離または全体破棄する。Trace sealが再試行可能な間はDisposition=`Running`または`Finalized`でSession Leaseとlockを保持し、成功または明示Abortまで解放しない。期限超過はCapture失敗を確定するが、Service terminal result未回収ならLease解放を許可しない。`Committed`、`CommitOutcomeUnknown`またはcommit後の`PublicationRecoveryRequired`では局所Registry slotを`Empty`へ戻さず、Plan／tmp／chunkをabort cleanupで削除しない。

Draft、Registry、Trace の状態遷移は main thread の共通 Coordinator だけが反映する。Backend はこれらを変更しない。Draft の外部状態は `Pending / Staged / Dropped` を維持し、backend 内部状態を追加しない。Phase 0.11の`Staged`は当該Frameのencode／chunk append成功だけを表し、Run Artifactの公開可能性またはCaptureCompleteを単独では証明しない。形式非依存 Drop Reason は既存 enum へ append-only で追加し、既存 0～9 を再番号しない。Phase 0／0.1のFreeze順と意味は維持するが、Sessionの生成・解放はOwnership Lease APIへ移行する。Phase 0.11は新規受付停止、Backend `BeginDrain`、queued cancellation、Submit Workerによる全Accepted Workの`Submitted`／`FailedBeforeSubmit` record生成、Submit Worker Join、Output Workerによる全record／Frame Completion／所有資源回収、Output WorkerへのFinalize／Abandon要求、Context terminal resultのbounded poll、`Finalized`の局所Registry登録または`Abandoned`予約解放、Output Worker Join、全所有資源／予約ゼロ確認、main-thread terminal intent反映、残存Pendingのtimeout Drop、Trace seal、Disposition=`Finalized`、専用tmp書込み、非上書きPlan rename、既知成功時の`Committed`確定と通常Publication、既知pre-commit失敗時の`Incomplete`とabort cleanup、rename結果不明時の`CommitOutcomeUnknown`とRecovery移行の順とする。全Accepted recordとCompletionのdrainおよび所有資源の解放／Quarantine完了前にFinalizeせず、各WorkerのJoin成功後に同Workerへ処理を要求せず、未知DispositionでTrace seal／Plan構築へ進まず、Plan rename成功前に`Committed`へ確定しない。Join前にbackend-owned Surface／buffer／未確定chunk payloadを外部からDisposeしない。

Phase 0の境界確定では将来backendを差し替えられる責務分離だけを確定する。ハードウェアencoder、特定動画codec、profile／bitrate／rate control、packet／GOP／keyframe、PTS／DTS／reorder、container／segment、GPU native handle／zero-copy、独自binary schema、MessagePack／CBOR／Protobuf、worker thread／Job／Burst、queue実測調整はPhase 0の設計・実装・比較・Spikeに含めない。Phase 0.1は既存境界を維持する。Phase 0.11はPhase 0完了後の破壊的変更として、trusted internalな`NvencRunChunkContext`、Registryの単一Run chunk直接登録経路、`FrameSequence` Kindを追加できるが、Phase 0／0.1のPNG Backend、Envelope、Frame／submission Artifact Completion、Publication Planの意味を変更しない。汎用Run Artifact Plugin APIは追加せず、特定codecの生成／verificationとStore実装はBackend内部へ隔離する。

Phase 0.1およびPhase 0.11はPhase 0完了後の独立した後続Phaseとし、Phase 0の完了条件、受入条件、実装範囲を遡及変更しない。Phase 0で使用するPNG＋JSON Backendは両Phaseの追加を理由にPhase 0内で作り直さない。

Phase 0.1はPNG＋JSON Backendを維持し、固定Unity版でthread-safeと規定された現行`ImageConversion.EncodeNativeArrayToPNG`を共有Unity Objectなしでcaller-owned bytesからWorker実行する。`PNG encode -> canonical JSON生成 -> content hash -> staging write -> file flush -> staging renameと必要なdirectory durability -> ArtifactCompletion生成`を固定容量Workerへ移し、Main Thread PNG Fallback、実行時Capability Gate、別PNG library、新しいPNG Format Versionを実装しない。Worker encodeが例外、空出力または不正出力となった場合は同じ入力をMain Threadで再試行せず、既存のCapture失敗終端とTraceへ進んでゲームを継続する。

固定FixtureではWorker出力をdecodeしたRGBA pixelが入力とlossless一致し、寸法、orientation、canonical JSONのproperty順、FrameId／Trace相関が従来経路と一致することを検査する。同じUnity版・同じ現行EncoderでもMain Thread時代のPNG圧縮bytesまたはcontent hashとの一致は要求せず、各出力自身のhashが正しく記録されることだけを要求する。既存Loader／Verifier／Publication／Recoveryが変換なしで同じArtifact形式を受理することを確認し、`durable staging completion`をWorker責務の終端とする。final Publication、`publication.plan`、`capture.index`、Recovery判断、CaptureComplete、cleanupは既存Coordinatorから移さない。Main Thread上の`TryCollectFrameCompletion`／`TryCollectArtifactCompletion`は既存PNG Backendの固定容量Completion Queueを軽量bounded pollし、正式状態遷移へ反映する。Phase 0.1のためにinline Cellへ再実装しない。Phase 0.1は単一の固定容量Worker実行列で成立させ、Encode列とI/O列の分離、payload copy、二重hash、PNG圧縮率の最適化は実測後の後段へ送る。flush待ちによるQueue枯渇ではMain Threadを待たせずCaptureだけをBackpressureまたはDropする。

Phase 0.1／0.11の試験は次の実行階層へ固定する。各試験は正本Tierを1つ持ち、上位Tierは下位Tierのfault位置、deadline、Queue、Registryおよびfile状態の直積を再実行しない。ただし実環境結合の成立を証明するため、Accepted／Completion件数、所有権解放、chunk確定、Plan commit等の最小sentinelを上位Tierでも観測してよい。

| Tier | 実行契機 | 使用可能な環境／入力 | 正本の検査範囲 |
| --- | --- | --- | --- |
| A 通常CI／毎コミット | 全変更 | fake monotonic clock／GPU Fence／NVENC completion／Publication Service、in-memoryまたは小容量temporary Store、数Frame、小synthetic chunk、内部completion gate | bounded予約、Backpressure、Completion／Context終端exactly-once、Source／Input所有権、`Finalize／Abandon -> terminal -> Join`、commit前／後／unknown分岐、Lease、`.partial`非公開、Recovery file-tree分類、Main／Render Thread非待機、Quarantine |
| B 対応環境NVENC統合 | NVENC native plugin、GPU変換、共通Coordinator、Surface所有権、NVENC Profileまたはその依存範囲の変更時、および手動要求時 | Windows／NVIDIA／D3D11 WDDM、最小の複数Frame列、実NVENC、確定small chunk、Decoder 1 process | Texture受渡し、RGBA→NV12、Accepted FIFO＝submit＝Output回収＝append＝Relation順、固定2 Worker、thread非待機、Surface返却、small chunk decodeだけ |
| C hardware qualification | Phase 0.11承認、Unity／NVENC SDK／native plugin／GPU Driver／GPU／OS／Capture Profile変更、リリース相当判定、明示的な定期qualification | 指定WDDM hardware、120 cadence tick、実時間4秒、確定Run chunk、FFmpeg 1 process、代表Recovery | hardware-qualified nominal、120件のsubmit／Output／Relation順一致、Plan／Publication／CaptureComplete、chunk全体decode、代表的な実process再起動／OS lock Recovery |
| D 手動診断 | 不具合調査または明示要求時だけ | 実device loss／driver hang／native永久停止／process kill／強制終了／disk full／antivirus干渉／最大256 MiB chunk／低速SSD／長時間Capture／外部改変 | 実障害の観測と診断。未実行または成功証拠の不在をPhase 0.11未完了としない |

Tier Aは実時間sleep、busy wait、実時間poll、実NVENC、FFmpeg、実process再起動、120 Frame、最大chunk、大容量fileの反復hash、実device loss／hangまたはOS scheduler／SSD性能依存の合否を禁止する。deadline直前と1 step超過をfake clockで即時に与え、native永久停止は内部fake completion gateを閉じて模擬する。Recoveryは完成Planのみ、NVENC専用tmpのみ、Planなし、完成Plan＋専用tmp、完成Plan＋不一致chunkの5つの小さい固定file-tree fixtureで分類し、sentinelの存在、長さ、hashにより無変更性を検査する。Phase 0.1もTier Aの数FrameFixtureだけでWorker受付、Completion Queue、Drain／Join、例外、Loader／Verifier互換、FrameId／Trace相関、Backpressure／Dropを検査し、大解像度、多数Frame、長時間I/Oまたは実時間cadenceを追加しない。

Tier Bの条件付き起動はRepositoryでversion管理する`CaptureQualificationTriggerManifestV1`を正本とする。Manifestは対象path patternと`ManagedCapture / NativePlugin / GpuConversion / SurfaceOwnership / CaptureProfile / BuildTooling / TestHarness`の変更カテゴリ、各カテゴリのTier B要否、手動override入口をcanonical順で保持する。Manifest自身の変更、旧pathまたは新pathのどちらかが対象となるrename、新規path、diff取得不能、複数カテゴリ競合または分類不能はTier B実行へfail closedとし、黙ってTier Aだけへ落とさない。個々のpath patternはCI実装時に初期値を確定できるが、Manifest外の暗黙一覧を正本にしない。手動skipは対象commit、理由、実行者、時刻をCI Resultへ記録し、通常開発上の例外としては許容するがTier B成功へ変換せず、Phase承認／リリース相当候補の証拠には使用しない。手動force-runはManifest判定にかかわらずTier Bを起動できる。

通常PRでは実NVENCを検証せず、native／GPU固有退行がTier B／Cまで遅延検出され得ることを許容する。Tier Cを通常PR／通常EditMode／全開発者環境のmerge Gateにせず、対応hardwareがなければTier Aで`Unsupported`開始拒否だけを確認する。CPU readback、同期Fallback、別Codecまたは別GPU vendorをQualification通過目的で追加しない。Tier Cは`CaptureTierCQualificationResultV1`へRepository commit ID、clean working tree証拠または完全source snapshot hash、managed build ID／content hash、native plugin binary hash、GPU変換shader／関連Asset content hash、Capture Profile ID／Version／hash、Test Profile／Harness version、Unity／NVENC SDK／GPU／Driver／OS identityと試験結果を固定する。dirty sourceをcommit IDだけで同一candidateとみなさず、欠落identity、hash不一致または別buildを成功Resultへ結合しない。

Phase承認、対象環境更新およびリリース相当判定は、対象candidate自身から生成してTier Cを通過した同一build artifactをそのまま昇格する。同じcommitからの再buildでもmanaged build、native pluginまたはGPU変換Asset hashが変われば別candidateとして再Qualificationし、古いHEAD、別binaryまたは手動skip済みTier BのResultを流用しない。通常merge後に以前のTier C Resultが新HEADを承認しないことは許容し、Qualification対象candidate以外の通常開発を停止させない。これらのidentityとtrigger判定はCI／Qualification Resultのschemaであり、Capture Run Manifest、Artifact DescriptorまたはRuntime Capture schemaへ追加しない。

Tier AはTrigger Manifestの対象／非対象path、renameの旧／新path、Manifest自身、新規path、分類不能、diff取得不能、手動force／skipを小さいsynthetic diffで検査する。Qualification Resultのcommit／snapshot／managed build／native plugin／shader／Profile／環境identityについて一致、1 field不一致、欠落、dirty source、再build差分を検査し、不一致candidateの昇格を副作用前に拒否する。実binaryの大容量再hashやTier C本体はこのunit testへ重複させない。

Tier AのArtifact Store試験は小さいsynthetic payloadまたは最大read要求サイズを記録するfake streamで、streaming hash一致／不一致、宣言長より短い／長い、EOF境界、途中I/O失敗、checked累積長overflow相当、読取り中変化、reparse point／不正path／file種別拒否、全terminal経路のbuffer返却、Artifact全長配列非確保を検査する。`BufferUnavailable`はRun開始前、Plan commit前、commit後Fresh verification、Recovery中の4境界で注入し、それぞれUnsupported／Incomplete Abort／`PublicationRecoveryRequired`／`Deferred`へ一意に進むこと、後2者がPlan／chunk／file集合を変更せずpre-commit cleanup、Mismatch、collisionまたはCaptureCompleteへ進まないこと、全経路でService terminal回収前にLeaseを解放しないことを確認する。`Deferred`と`Completed + Invalid`、未初期化`None`をclassifierへ渡し、前者2つを同じcollisionとして扱わず、`None`をfail-openしないことも検査する。Phase 0.11 Freshはstaging全hashを省略できる全条件と各1条件欠落を検査し、final全hashが1回、CaptureCompleteで追加0回、Recoveryでは新process相当として再検証することをcounterで確認する。Main／Render ThreadからStore検証を呼べないことも構造検査する。Tier Bは短い実NVENC chunkでwriter hash、staging確定、final rename／streaming verification、Publish Receipt、CaptureCompleteと全長配列非確保を確認する。Tier Cは120 Frame chunkでFresh全file hash 1回、最大検証bufferがchunk長へ比例しないこととPublication Service非待機をQualification Resultへ記録する。最大256 MiB file、disk full、実read障害、外部改変、antivirus、rename後破損、filesystem固有no-followおよびPublication中process killはTier Dだけで扱う。

Tier Bのordered NVENC Spikeは固定4 Frameだけを使い、正常系ではAccepted FIFO、`NvEncEncodePicture`呼出し、`Submitted` record、Completion／Output Buffer回収、chunk Access Unit、Frame RelationのToken／Frame ID列が完全一致することを確認する。各Eventの物理的signal時刻順や未待機の後続Event状態を観測せず、長時間録画、性能分位、Drop率またはDriver／GPU組合せ網羅を要求しない。このSpikeまたはTier C nominalで順序不変条件、固定2 Workerまたは30fpsが成立しない対応環境はPhase 0.11をUnsupportedとし、reorder実装を追加する理由にしない。

Tier C Decoderは確定chunk 1件につきclean processを1回だけ使い、先頭から末尾までstreaming decodeして全120 Frameの件数と1280×720寸法を確認する。全120 FrameをRGBA fileまたは同時保持メモリへ展開せず、画素、orientation、色およびFrame marker比較は先頭、中央`floor((N-1)/2)`、末尾だけとする。Frame Relationは120件全ての正値、重複なし、accepted順を検査し、sample 3件だけdecode ordinalとのmarker対応を照合する。未sample Frameの局所画質異常をPhase 0.11で検出できないことを許容する。実process終了／OS lock解放／新process起動を伴うRecoveryはTier Cの代表1系統だけとし、残る実crash、rename結果不明の実OS再現および外乱はTier Dへ送る。

Phase 0.11はPhase 0.1で確定した非同期Capture境界を入口条件として使用し、Phase 0のPNG＋JSON Backendの成果物意味を変更しない。Session生成・解放APIだけはOwnership Leaseへ破壊的に移行する。複数Frame共有Artifactを既に表せる`CaptureArtifactFrameRelation`と、21.15で追加した最小Run Artifact拡張だけを使い、Capture FrameとFrameId／Traceの対応、Freeze／Drain／Join／Publication／Recovery／CaptureCompleteをBackend固有経路で迂回しない。指定GPU／Driver／SDKを持つhardware-qualified runnerのnominalだけは`BringUpCadenceTickCount=120`、`BringUpCadenceHz=30`、`SubmissionWindowDeadlineMs=4000`、`FinalizationDeadlineMs=30000`を固定する。Tick indexは`0..119`、予定時刻はmonotonicな開始時刻を`T0`として`T0 + index / 30 second`とし、各tickで提出を厳密に1回だけ試みてBackpressure時も同tickを再試行しない。tick 119の試行完了または`T0 + 4000 ms`到達の早い方で新規提出を停止し、その後同じCoordinatorがDrain、全Frame回収、chunk確定／局所登録、`TryJoin`、資源ゼロ確認、Trace Freeze、Disposition=`Finalized`でPlan構築、rename既知成功時の`Committed`確定、通常Publicationを行う。rename結果不明では`CommitOutcomeUnknown`として自動cleanupせず次回起動時のfile集合分類へ送る。Finalization期限は提出停止時点から測り、期限内に期待終端へ到達しなければscenarioとCaptureを失敗扱いにして新規処理を停止するが、Service terminal result未回収時の強制unlockまたは同process Recoveryを許可しない。この4秒は提出窓のhard boundであり、Finalizationを含む全Run時間を4秒と主張しない。一般CIではmock／fake completion sourceを使う短い決定論的lifecycle試験だけを必須とし、wall-clock 30fps、NVENC capabilityまたは120 Frame成功を要求しない。同一Runで提出を再開しない。

hardware-qualified `NvencNominalBringUpV1`は全120 tickについて提出試行、Accepted Work Token、encode Frame Completionを厳密に1対1で要求し、Frame Completionは全件`Succeeded, ProducedArtifactCount=0`とする。正常DrainではSubmit Workerが全Accepted WorkをFIFO順にsubmitして`Submitted` recordを生成してjoinし、Output Workerが全Output／Frame Completionを同順で回収した後、停止前に全120 FrameのAccess Unitを同じAccepted FIFO順に含むRun chunkを確定する。Context terminal回収と局所Registry登録後にOutput Workerをjoinする。Accepted FIFO、`NvEncEncodePicture`呼出し、`Submitted` record、Bitstream回収、chunk append、Frame Relation、Frame Completionの各Token列が厳密一致すること、chunkのpath、length、hash、TestRunIdと正のCaptureFrameId 120件を重複なく持つことを検査する。Contextの単一`Finalized` Result、局所Registry slot=`Committed`、Disposition=`Committed`、Publication、CaptureCompleteが30秒Finalization期限内に成功しなければPhase 0.11のnominal完了を認めない。受付拒否、Backpressure、Drop、encode／chunk確定失敗、順序違反、件数不足をnominalの許容劣化にしない。この試験は対応hardwareのPhase承認／定期qualificationで実行し、GPUを持たない一般CIのmerge Gateにしない。

fault、Backpressure、Freeze／Drainの各scenarioはTier Aの決定論的試験を正本とし、`FaultCadenceTickCount=16`、`FaultCadenceHz=30`、`FaultSubmissionWindowDeadlineMs=1000`、`FaultFinalizationDeadlineMs=10000`を仮想時刻上限とする。必要tick数をscenario開始前に`1..16`で宣言し、fake monotonic clockのindex `0..DeclaredTickCount-1`を`T0 + index / 30 second`へ同期的に進めて各1回だけ試みる。宣言tick末尾または仮想`T0 + 1000 ms`の早い方で提出を止め、deadline直前では未失敗、1 step超過でscenario失敗となることをsleep／busy waitなしで検査する。事前宣言したFault Injectionと期待Dispositionに一致するAccepted／Frame Completion件数不足またはRun chunk `Abandoned`だけを許し、期待値にない不足、期限後の追加提出、同tick再試行、fault解除後の窓延長は禁止する。仮想10秒超過はscenario失敗と新規Capture処理停止を確定するだけであり、未静止Publication ServiceのSession Leaseを解放する期限ではない。実時間fault、device loss、native hangまたはOS scheduler依存の再実行はTier Dだけとする。

Tier A Recoveryはprocessを再起動せず固定file-tree fixtureを新しいRecovery Coordinatorへ渡し、fake clockで`RecoveryFinalizationDeadlineMs=10000`の直前／1 step超過と期待Dispositionを検査する。確定済みchunkがPlanへ入るfixtureだけPublication／CaptureCompleteを要求し、書込み中chunkだけのfixtureでは`.partial`の無視または破棄とCapture Incompleteを正しい終端とする。Tier Cの代表的な実再起動Recoveryだけは、旧processの提出停止時刻またはmonotonic clockを永続化・継承せず、新processがOS lockを取得して対象RunのRecovery entrypoint `BeginRecovery`へ入った瞬間を`RecoveryT0`として同processのmonotonic clockで固定する。実`RecoveryFinalizationDeadlineMs=10000`以内に期待Dispositionへ到達できなければQualification失敗とし、process停止から`BeginRecovery`到達までの時間は外部Harnessの別診断値とする。実process kill、強制終了、複数外乱、rename結果不明の実OS再現はTier Dへ送り、fault系とRecoveryで30fps持続性能を再証明しない。

`NvencBringUpProfileV1`はWindows 10以降／NVIDIA／D3D11／WDDM、`enableEncodeAsync=1`、SDR／sRGB、左眼、30fps Capture cadence、`width=1280`、`height=720`を固定し、Capture EnvelopeとRun ManifestへProfile IDを保存する。Run開始時にWDDM非同期NVENC capabilityと`CaptureFrameProfile.ImageRect`が厳密に1280×720であることを検査し、TCC、同期modeまたは非同期Completion Eventを使用できない構成をUnsupportedとする。NVENCへ提出する専用RenderTextureはImageRectと同じ全extent、`x=0`、`y=0`、`PixelLayout=RGBA8`、`GraphicsFormat.R8G8B8A8_SRGB`、MSAAなし、mipmapなし、row 0が表示画像上端のtop-left orientationとする。元Textureが別SubRect、Texture Arrayまたはbottom-left orientationなら、提出前のGPU Passでcrop／上下反転とRGBA8 sRGBからBT.709 limited-range NV12への変換を完了し、D3D11 GPU TextureとしてNVENCへ渡す。NVENC Backendは`UnityRenderTextureReadbackDispatcher`その他のPNG用RGBA32 CPU readbackを使用せず、圧縮後のbitstream bytesだけをCPUへ回収する。Backend内ではresize、追加crop、orientation推測を行わずalphaを無視する。1280×720以外の寸法、異なるFormat／Layout／orientation、Dynamic Resolution、ImageRectまたは寸法のRun中変更では新Encoder Sessionへ再構成せず、新規受付を止めてCaptureだけをFail Fastし、ゲームと既存の確定済みArtifactを維持する。

`NvencBringUpProfileV1`は`NvencWorkSlotCount=8`、`NvencInputSurfaceSlotCount=4`、`NvencSubmissionQueueCapacity=8`、`NvencSubmitToOutputQueueCapacity=8`、`NvencFrameCompletionQueueCapacity=8`を固定し、Run開始前にWork Slot領域、NVENC Input Surface Pool、3本の固定Queueを生成する。Submission Queueへの複数呼出元は既存`TrySubmit` admissionの単一線形化境界で直列化し、Submit-to-Output QueueとFrame Completion Queueはそれぞれ単一producer／単一consumerとする。Submit-to-Output Queueのrecordは排他的variantとし、`Submitted`はToken、Input Surface、Output Buffer、Completion Eventを、`FailedBeforeSubmit`はToken、固定失敗／取消Reason、Submit側に残るSource／Input等の所有資源とその解放待ち／Quarantine情報を持つ。同Queueは各Accepted Workを同順で厳密に1件受け渡す所有権Queueであり、任意順Completionのreorder bufferではない。同時in-flight Accepted Work数はWork Slot Count以下なので、Accepted時に予約したWork SlotがSubmit-to-Output Queueの対応capacityも保証し、Accepted後のどちらのvariantも容量不足でenqueue失敗させない。各Input slotのD3D11 Texture／NVENC登録handle／所有状態と、各Work Slotのtoken generation／Completion credit／所有権状態を固定長領域へ保持する。`ICaptureEvidenceSession.TrySubmit`が受理した時点で、呼出側の`CaptureSurfaceLease`所有権はBackendへ移る。BackendはSource Textureを読むcrop／flip／RGBA→NV12 GPU変換を登録しただけではLeaseを解放しない。GPU queue上で当該変換がSource Textureを参照し終えたことを示す`SourceReadCompleted` Fence／Query等の非同期完了証拠を取得した後だけ、Submit WorkerがLeaseを元Surface Poolへ返却またはDisposeする。これはSource所有権だけの解放であり、この時点では`CaptureFrameCompletion`を発行しない。Main／Render Threadは証拠を待たず、未完了Leaseを元Surface Poolへ返さない。

`NvencCaptureEvidenceBackend`は`MaximumArtifactCountPerSubmission=0`とし、Coordinatorから単一`NvencRunChunkContext`を受け取る。Run開始時にContext、Registry容量1件、writer、固定Work Slot 8件、NVENC Input Slot 4件、Submission／Submit-to-Output／Frame Completion Queue各8件を一括確保できなければ新規Frameを受け付けず、取得済み資源とRegistry予約を逆順で全返却する。`TrySubmit`は空Work Slot、NVENC Input Slot、Submission Queue record、Submit-to-Output Queue対応capacity、Frame Completion creditその他必要資源をAccepted前に一括予約できた場合だけ、Work TokenとSurface所有権を固定Submission Queue末尾へ同じ線形化点で移して`Accepted`を返す。容量不足、`NotAccepting`または例外では内部予約を残さずSurface所有権を受け取らず無効tokenを返す。Source Surfaceの解放とNVENC Input Slotの再利用は別lifecycleである。Source Read完了後もNV12を保持するInput Slotは占有を継続し、Output WorkerがNVENC encode完了証拠を取得して圧縮bitstreamの所有権をContextのwriterへ移した後だけ再利用する。Frame WorkはArtifact Descriptor、content hash、Artifact Completionを生成しない。取消、encode失敗、Drain時もSource Read完了前のLeaseとNVENC完了前のInput Slotを早期返却せず、device loss等で通常証拠を得られない場合は再利用可能Poolへ戻さず失敗終端してBackend破棄時に隔離解放する。

NVENC submit、Completion／Output Buffer回収、bitstream ownership取得、chunk append、streaming hash／ByteLength更新、Frame Relation追加、Frame Completion生成は固定2 Worker間のFIFOで直列順序を維持する。Submit WorkerはSubmission Queue先頭のGPU変換が完了するまで後続Workを先にsubmitせず、Accepted FIFO順にだけ`NvEncEncodePicture`を呼ぶ。GPU変換と`NvEncEncodePicture`が成功したWorkは`Submitted`、submit前の検出可能なGPU変換失敗、取消、shutdownまたは`NvEncEncodePicture`失敗は`FailedBeforeSubmit`として、各Accepted Workにつきどちらか一方だけをSubmit-to-Output Queue末尾へ移す。Output Workerは同Queue先頭だけを消費し、`Submitted`では対応Completion Eventだけを待ってOutput Bufferをlock／copy／unlockし、`FailedBeforeSubmit`ではNVENC出力を待たずReasonと所有権情報から安全な解放またはQuarantineを行う。後続Eventのsignal状態をprobeせず、Completion callbackの到着時刻、OS scheduler順またはToken fieldから回収順を変更しない。成功Runでは`Accepted FIFO順 = NvEncEncodePicture呼出順 = Completion／Bitstream回収順 = chunk append順 = Frame Relation順 = Frame Completion生成順`、失敗Runを含む全Runでは`Accepted FIFO順 = Submit-to-Output record順 = Frame Completion生成順`を不変条件とする。最初の失敗または順序違反を検出した側は新規受付を停止し、Output Workerが当該recordを消費した時点でContextを`Abandoned`へexactly onceで固定する。すでにAccepted済みでまだsubmitしていない残りはSubmit WorkerがNVENCへ新規submitせず`FailedBeforeSubmit(CancelledAfterRunAbandoned)`として同順に渡し、すでに`Submitted`となった残りはOutput WorkerがEvent／Output Bufferを安全に回収するが追加appendせず`CancelledAfterRunAbandoned`として終端する。全Accepted Workのrecord、Completionおよび所有資源を回収してから両Workerをjoinする。GPU変換または先行NVENC処理の停止で両Queue／Work Slotが埋まり後続受付が`Backpressured`になる通常のHead-of-line blockingを許容するが、Work Slotへ逆順Bitstreamを保持するreorder状態、専用reorder満杯Reason、別Session分散、同期FallbackまたはBitstream解析による事後整列を設けない。順序不一致を検出した場合は該当Access Unitを追加appendせずCaptureだけをFail Fastする。

encodeとappend ownership移転が成功したFrameは`CaptureFrameCompletion(Status=Succeeded, ProducedArtifactCount=0)`、`FailedBeforeSubmit`、encode後の失敗、取消、null／空出力、Access Unit上限超過、chunk byte上限超過、順序不一致、append失敗または先行失敗後の残存Workでは`Failed`または`Cancelled, ProducedArtifactCount=0`を予約済みFrame Completion creditへexactly once公開する。Frame Completionの唯一のproducerはOutput Worker、consumerはMain Threadとし、Submit Worker、GPU callbackまたはCoordinatorはCompletionを直接発行しない。Frame Completionには固定配列SPSC Queueを使う。各Queue操作は短いlockまたは同等のrelease／acquire同期を許可し、consumerはlock取得を待ち続けず取得できなければ次回pollへ送る。Accepted前に全capacityとcreditを予約するため、Accepted後のSubmit-to-Output recordまたはCompletion enqueueは容量不足で失敗しない。Work Slotは対応Completionが回収され、全Source／Input／Access Unit所有権が解放、Quarantineまたはwriterへ移転済みになった後だけ再利用する。Phase 0.1 PNG Queue実装そのものはcross-thread転用せず変更しない。一般thread pool、work stealing、lock-free Cell、fieldごとのbarrier、読取り前後のgeneration二重照合または全timingの競合網羅をPhase 0.11の成果物にしない。

Accepted後のFrame Completion exactly-onceは、Backendが成功、検出可能な失敗、取消または制御可能なshutdownのterminal処理へ到達した場合の安全契約であり、native NVENC／Driver／OS I/Oが永久に帰還しない場合の有限時間livenessを保証しない。永久停止中は該当WorkとRunを非終端のまま保持し、成功／失敗を推測した偽Completion、Completion creditの再利用、GPU資源の再利用または強制unlockを行わない。native処理が帰還すれば予約済みcreditへterminal Completionを1回だけ公開し、帰還しなければprocess終了時のBackend破棄とOS lock解放へ委ねる。device loss等で安全な完了証拠を得られないSource／Input／bitstream資源は再利用可能Poolへ戻さず`Quarantined`として隔離し、通常の`InFlight`または意図しない永久占有／lease leakと資源集計上区別する。固定容量を回復できなければ当該Runの新規Capture受付を停止する。ゲーム実行は継続し、資源回復はBackend破棄またはprocess再起動に限定する。

`FinalizationDeadlineMs=30000`はPhase試験とCaptureを期限超過として失敗判定し、新規受付を停止する期限であり、Frame Completion、資源解放、成功JoinまたはOS lock解放を捏造・強制する期限ではない。native／OS処理が帰還しない限りDrainは未完了であり、Trace Freeze、Plan commit、`Incomplete`を含むRun終端または成功Joinへ進めない。Production公開APIを増やさず、内部テスト構成だけがNVENC呼出し直前または完了回収境界へfake native completion gateを注入できるようにする。

Main ThreadとRender ThreadではNVENC Completion、GPU Fence、Texture空き、bitstream lock／取得、hash、file write、close／renameを待たない。Render Thread／Native Plugin callbackはcrop／flip／色変換を行うGPU workと固定Submission recordに必要なbounded command／handle登録だけを行って戻り、`NvEncEncodePicture`自体はAccepted FIFOを所有するSubmit Workerだけが呼ぶ。callback内でblocking encode、poll loop、sleep、device-context待機またはI/Oを行わない。GPU完了証拠のpollとNVENC submitはSubmit Worker、Completion Event待機、bitstream取得、content hash、chunk staging append／finalizationはOutput Workerが行う。4 Input Slotまたはcaller側Source Surface Poolがすべて占有中ならMain／Render Threadを待たせず`Backpressured`を返す。nominalでBackpressureが1件でも発生すれば失敗とし、専用fault scenarioでは期待Dispositionとして検査する。

crop、orientation補正、RGBA→NV12は可能な範囲で同一のversion付きGPU conversion passへまとめ、CPU roundtripを禁止する。API／format制約で追加GPU copyが必要な場合はPass数、copy数、Texture遷移を固定ProfileとProfilerへ記録するが、zero-copy方式比較や複数実装の最適化をPhase 0.11へ持ち込まない。構造テストでは`NvencCaptureEvidenceBackend`が`UnityRenderTextureReadbackDispatcher`を参照しないこと、Main／Render Thread経路から待機可能APIとArtifact Storeを呼ばないことを検査し、ProfilerでNative callback時間、Source Surface／Input Slot占有、Backpressure数を取得する。Tier Aの必須回帰は小payloadと数Frameを使う次の10代表caseに限定する。(1)正常な数Frameが1 chunkとして確定、Trace Freeze、Plan commit、Publicationされる、(2)pre-finalize失敗でPlanを作らず`Incomplete`となる、(3)chunk Finalize後かつrename呼出し前の失敗で局所Registry slotと予約を解放し`Incomplete`となる、(4)NVENC専用tmpだけのfixtureを自動昇格しない、(5)完成Planだけのfixtureを通常Recoveryできる、(6)完成PlanとNVENC専用tmpの同時存在をcollisionとして無変更停止する、(7)commit後のPublication失敗でPlan／chunkを削除せず`PublicationRecoveryRequired`となる、(8)abort cleanupのfile削除失敗でもゲームを止めずLeaseを解放する、(9)同一Runで局所Registry登録とPlan commitを重複実行しない、(10)Main／Render ThreadからPlan I/O、cleanup、待機可能API、hashまたはfile I/Oを呼ばない。`CommitOutcomeUnknown`はfake Publication Serviceのrename結果不明で作り、その場で再読込・cleanupしないことを1件だけ検査する。容量不足、Source／Input遅延、Completion、Context終端、Session Lease、commit前cleanup、commit後非cleanup、unknown非cleanup、`.partial`非公開、Quarantineは各不変条件につき共通unit test 1件へ集約し、fault位置と状態の直積を作らない。成功時は各Frameが`Succeeded/count=0`を厳密に1件返し、全Frame回収後かつOutput Worker Join前だけContextが`Finalized` Resultを1件生成する。失敗／取消Frameまたは0 Accepted Frameでは`Abandoned`として予約を1回だけ解放し、Resultを生成しない。Frameが全てStagedでもContextが`Abandoned`または局所Registry未登録ならArtifact 0件のPlanを構築せずCaptureCompleteを発行しない。資源ゼロと意図しない永久占有なしは全fake native／OS処理が帰還したcaseだけで検査する。Tier Aのordered pipeline試験はGPU変換ready順をAccepted FIFOと意図的に異ならせてもSubmit Workerが先頭Workだけを先にsubmitし、Submit-to-Output QueueとOutput Workerが同順を維持することを検査する。後続Eventの先行signalを観測・回収するfake、逆順Bitstream保持またはreorder結果検査は作らない。fakeで順序recordを破損した場合は追加appendせず`Abandoned`／Capture Fail Fastとなることを1件だけ検査する。Tier Aのfake native completion gate停止caseでは、仮想期限超過後も偽Frame Completion、credit／GPU資源／OS lockの再利用・解放、Trace Freeze、Plan commit、`Incomplete`終端または成功Joinが発生せず、枯渇後はゲームを待たせずBackpressure／受付停止となり、gate解放後だけterminal Completionをexactly onceで発行してDrain／Joinへ収束することを確認する。実native永久停止からの復帰不能性はTier Dだけで扱う。複数Run Artifactまたは2番目のchunkはPhase 0.11の構造上生成できず、Phase 4.8の別versionへ送る。

Tier Aではsubmit前GPU変換失敗と`NvEncEncodePicture`失敗を各1件注入し、失敗したAccepted Workが`FailedBeforeSubmit`として同じ予約済みFIFO slotへ渡され、Output Workerだけが対応するFailed Completionを厳密に1件発行することを検査する。先行する`Submitted`、失敗record、すでにAccepted済みの後続Workを混在させ、後続が`FailedBeforeSubmit(CancelledAfterRunAbandoned)`または回収済み`Submitted`からのCancelled Completionへ順番に収束し、追加chunk append、二重Completion、Work／Completion credit漏れ、Source／Input早期再利用がないことを確認する。安全な完了証拠を取得できないfake native停止では`FailedBeforeSubmit`を推測生成せず、従来どおり非終端保持とQuarantine／受付停止を検査する。

Tier AのPlan境界回帰は小さい固定file-tree fixtureを使い、Phase 0.11のpre-commit失敗後に`publication.plan.nvenc-precommit.tmp`削除も失敗した状態を入力して、canonical bytes、正しいRun相関および対応chunkが存在しても`publication.plan`へ昇格せず隔離／破棄されることを確認する。同じsuiteでPhase 0／0.1のcanonical `publication.plan.tmp`だけが残るfixtureは従来どおり昇格できることを確認する。fake Publication Serviceのrename例外／結果不明は`CommitOutcomeUnknown`へ固定し、final／tmpの再読込、Registry slot破棄、staging削除または再renameを行わない。Service request実行中、rename call未帰還またはterminal result未回収ではSession Leaseを解放できず、terminal result後はService-owned handle、request slot、bufferおよびI/O commandがすべてゼロであることを検査する。完成Planのみ／専用tmpのみ／Planなし／final＋tmp／finalとchunk不一致をそれぞれ`PublicationRecoveryRequired`／Incomplete-orphan cleanup／Incomplete-orphan cleanup／`PublicationRecoveryCollision`／Recovery collisionへ分類する。完成Plan＋専用tmp fixtureはsentinelの存在、長さ、content hashが検査前後で不変で、Plan／Artifact公開とCaptureCompleteが発生しないことを確認する。実process再起動とOS lock解放の代表経路はTier C 1系統、timeout／取消中の実Worker、実rename結果不明または外部改変はTier Dへ分離する。

Run開始時はWDDM modeとNVENC capability queryでH.264 High Profile、NV12入力、固定寸法、必要なD3D11／非同期Encode機能を問い合わせ、`enableEncodeAsync=1`と要求設定による単一Encoder Session初期化の成功を必須とする。TCC、同期mode、Completion Event登録不能または固定2 Workerのordered async経路を構築できない場合はCaptureだけをUnsupportedとする。アプリ内でAnnex AのMaxFS／MaxMBPS／MaxBR／MaxCPBを再計算せず、対応可否はcapability queryと初期化結果を正本とする。RGBA8 sRGBからBT.709 limited-range NV12への変換はversion付き固定GPU Passで行うが、係数、量子化丸め、chroma sample location、H.264 VUIの個別bit値をPhase 0.11のProduction受入契約にしない。変換Profile IDとVersionだけをRun Manifestへ記録し、色とorientationの妥当性は後述のDecoder結合テストで確認する。

Encoderへの要求設定はH.264 High Profile、progressive Annex B、各入力をIDRとしてencode、SPS／PPS反復、P／B Frameなし、Frame間参照なし、Constant QP 28とし、SDK／Driver識別子と全要求値をRun Manifestへ記録する。AUDの有無はPhase 0.11の受入条件にしない。NVENC APIが要求設定またはSession初期化を拒否した場合はCaptureだけをFail Fastする。ProductionはNVENC出力を再解析して設定適合を証明せず、NVENCが返したraw Annex B bytesを正本とする。

Phase 0.11の永続映像Artifactはappend-onlyの`ArtifactKind=FrameSequence(6)`、`FormatId=NvencH264IdrChunk`、`FormatVersion=1`とする。`FrameSequence`はcodec非依存の複数Frame共有Artifactを表し、既存Kind 0～5を再番号しない。nominal Runはchunk sequence 0だけを使い、staging rootとfinal rootのrelative pathを`chunks/chunk-0.nvenc-idr-chunk-v1.h264`、未確定pathを`chunks/chunk-0.nvenc-idr-chunk-v1.h264.partial`へ固定する。`.partial`はDescriptor、Artifact Completion、Plan、Capture Indexへ登録しない。確定chunkの`CaptureArtifactFrameRelation`は、そのchunkへappendを完了した正のCaptureFrameIdをaccepted順に重複なく保持する。Work TokenはFrame encodeのprovenance、Context ownershipはchunk生成の内部provenance、Frame Relationは意味上の関連であり相互に代用しない。Phase 0.11は各Frameのbyte offset、random access、seek indexをProduction契約にしない。

各Accepted Work Tokenについてencode API成功、返却bufferが非nullかつ1～`MaxAccessUnitByteLength=16 MiB`であることだけをAccess Unitのbyte受入条件とする。16 MiBはReject用上限であって各Work Slotの予約容量ではなく、実出力長に応じたbounded pooled bufferまたは同等の所有権付き領域を使う。NVENCが返した3／4-byte start code、NAL順、leading／trailing zeroを変更せず、accepted CaptureFrameId順に`.partial`へstreaming appendする。Run chunkのchecked累積上限を`MaxChunkByteLength=256 MiB`とし、上限を越えるAccess Unitは部分appendせずchunkを`Abandoned`へ送る。Frameごとのcontent hash、flush、close、rename、Descriptor、Artifact Completionを生成しない。各append完了後はAccess Unit bufferを解放できるが、`.partial`の所有権はRun chunk writerが保持する。

正常Freezeでは新規受付停止後にAccepted WorkをDrainし、Submit Workerが全Accepted Workの`Submitted`／`FailedBeforeSubmit` recordを生成してjoinした後、Output Workerが全recordと所有資源を処理して全Frame Completionを回収する。この時点ではOutput WorkerをJoinせず、Run chunkを`Finalized`にできる最小条件として、(1) AcceptedかつSucceededのFrameが1件以上、(2) Access Unit append件数が1件以上、(3) Accepted FIFO、`Submitted`、Output回収、Succeeded Frame、append済みAccess Unit、Frame Relation、Frame Completionの件数と順序が一致すること、(4) 全Accepted FrameがSucceededで`FailedBeforeSubmit`が0件であること、を検査する。0 Accepted Frame、0 Access Unit、件数／順序不一致、`FailedBeforeSubmit` 1件以上またはいずれかのFrame失敗では空chunkを確定せずOutput Worker上で`Abandoned`へ進む。

最小条件を満たした場合だけ、Coordinatorは停止前のOutput WorkerへContextのFinalizeを要求する。Output Workerは同じappend直列化境界でappendを停止し、appendごとに更新済みのchecked累積ByteLengthと単一streaming content hashを確定してhandleをcloseし、確定staging pathへ非上書きrenameした後だけ、Context内部状態からDescriptor／Relation／pathを持つ`NvencChunkFinalizationResult`を構築して`Finalized`を公開する。hashのためにfileを再読込せず、`Flush(true)`を呼ばず、close後にhandleを再openしない。Main Threadの`NvencCaptureRunCoordinator`はterminal resultをbounded pollし、自身が所有するContextとResultを局所Registry slotへexactly once登録する。Context terminal回収と登録または予約解放が終わった後にだけOutput Workerをjoinし、成功後に同Workerへ処理を要求しない。全Frame Completion回収前のFinalize要求またはResult登録を禁止する。両Worker Join、資源ゼロ確認およびTrace sealまで成功した候補はDisposition=`Finalized`へ進み、Session Ownership Leaseを保持したまま非所有Publication ServiceへPlan書込み／renameを要求する。rename既知成功時に局所slotとDispositionを`Committed`へ進め、通常Publicationへ進む。別CoordinatorへのContext、Lease、Freeze ReceiptまたはRegistry authorityの移譲を行わない。

encode、append、hash、close、Result生成、局所Registry登録、Join、資源ゼロ確認、Trace sealまたはrename呼出し前のPlan書込みの失敗・取消では明示Abortとし、局所slotが`Registered`なら同じCoordinatorが`Empty`へ戻して予約を解決した後に`Incomplete`へ確定する。Contextが`Abandoned`でもJoinと資源解放を完了してTrace sealできる場合はTraceをFrozen化した後に`Incomplete`へ確定する。成功候補のTrace sealが失敗した場合は再試行するか、明示Abortして同じIncomplete経路へ進む。rename既知成功後の失敗では局所slot=`Committed`とDisposition=`Committed`を維持し、Plan／chunkを取消・削除せず次回Recoveryへ送る。rename成否不明では`CommitOutcomeUnknown`へ進み、その場でfile集合を検査または変更しない。Finalize前の失敗ではContextを`Abandoned`へexactly onceで固定し、Finalize後の失敗ではContextを`Finalized`のまま維持する。pre-commit失敗経路ではPlan、Capture IndexまたはCaptureCompleteを生成せず、ゲーム、既に確定した別Artifact、既に公開したFrame Completionをrollbackしない。したがってFrame Completion成功は当該Frameのencode／append成功、Disposition=`Committed`だけが同processでchunkの通常Publicationへ進めることを表す。Trace enqueue／sealまたはpre-commit失敗はRun Incompleteになり得るがFrame CompletionまたはContext outcomeを再発行せず、Frozen Traceへ事後理由を追記しない。

RecoveryはProduction H.264 parserを持たず、Planへ登録済みの確定chunkについてDescriptorのcanonical path、ByteLength、content hash、TestRunId、ArtifactId、Frame RelationをPublication／Recovery Worker上の固定memory streaming verificationで再検査する。process-localなwriter hash、`NvencChunkFinalizationResult`または旧processのPublish Receiptを再利用せず、Artifact全長配列を確保しない。同じOS lock下の単一Recovery分類で得た検証結果を後続実行へ渡す既存Snapshotで安全に再利用できる場合は同じfileを理由なく再hashしないが、そのための新しいToken／owner generation／汎用authority体系を追加しない。bufferを取得できない試行は`Deferred`としてService terminal回収後にLeaseを解放し、Plan、chunk、staging、finalまたは専用tmpを検査済みとも不一致とも分類せず、そのfile集合を無変更のまま後続Recoveryへ残す。保存済み確定bytesのlength／hash不一致、path／Run／Frame相関不一致では対象Artifactを変更せずRecovery collisionとして停止する。`.partial`、Planに未登録のstaging chunk、確定rename前に停止したchunkは未確定であり、OS lock下で安全に無視または全体を破棄する。NAL走査、安全位置へのtruncate、末尾救済、部分hash、部分Relation復元、部分公開を一切行わない。未確定chunk喪失は当該CaptureをIncompleteにするが、ゲームRun全体または既に確定した別Artifactの破損とは扱わない。Recovery時にstart code、SPS／PPS、Slice、Level、VUIを解析せず、raw bytesをcanonicalize／再serializeしない。OS crash／電源断に対する完全durabilityをPhase 0.11で保証しない。

Phase 0.11 RecoveryのPlan authorityは完成名`publication.plan`だけとする。新processは旧processの`CommitOutcomeUnknown`その他process-localな`NvencRunEvidenceDisposition`を復元・推定せず、OS lock取得後のfile集合から次の順で分類する。(1)完成Planだけが存在しcanonical検証、Run相関および対応chunkのlength／hash一致に合格すれば`PublicationRecoveryRequired`、(2)NVENC専用tmpだけ、または完成Planも専用tmpもないPlanなしrootはIncomplete／orphan cleanup、(3)完成PlanとNVENC専用tmpが同時に存在すれば`PublicationRecoveryCollision`、(4)完成Planがあっても対応chunkが欠落または不一致ならRecovery collision、とする。`publication.plan.nvenc-precommit.tmp`だけが残るrootは、明示Abort、pre-commit失敗またはcrashのいずれであるかを推定せず、同tmpをPlanとしてparse／昇格／部分利用しない。OS lock、no-follow、固定path、Run root相関を検証して隔離または全体破棄し、削除失敗時も自動公開せずIncomplete rootとして残す。完成PlanとNVENC専用tmpが同時に存在する場合は、どちらの内容がcanonicalまたは相互一致するかにかかわらずfail closedに停止し、final、tmp、chunkまたは他のRun root内fileを削除、rename、上書き、公開せず、CaptureCompleteを発行しない。Phase 0／0.1の`publication.plan.tmp`昇格規則へこの制限を波及させない。

Tier B／CのDecoder結合テストはProduction Captureとは独立した`NvencChunkDecodeSmokeProfileV1`を使い、Tier AではFFmpegその他の外部processを起動しない。DecoderはFFmpeg 9.0.1の固定Portable Bundleとし、実行file／packageのSHA-256、起動option、OS、NVENC SDK、GPU、Driverをテスト結果へ記録する。Bundle treeのcanonical index、環境変数schema、DLL探索の再実装、append-only外部Tool Profile基盤をPhase 0.11の成果物にしない。Test Runnerは検証済み絶対pathのDecoderを使い、確定Run chunk 1件につき新しいprocess／sessionと空の作業directoryを1つだけ作る。raw Annex B chunkだけを入力し、外部extradata、別Run Artifact、Decoder session／cacheを再利用しない。Tier Bは実native経路と複数Framechunkを証明できる最小Frame列だけ、Tier Cはnominal 120 Frame chunkだけを入力する。timeout、stdout／stderrのbounded回収、失敗時のprocess tree停止と作業directory回収はTest Runnerの安全要件として維持するが、これらをProduction schemaへ追加しない。

Tier C nominalの確定chunkをclean Decoder process 1回で先頭から末尾まで逐次decodeし、出力Frame数がFrame Relation件数と厳密に一致して120であること、全Frameの寸法が1280×720であることをstreaming count／metadataで確認する。全120 FrameのRGBAを同時保持または個別file保存せず、比較対象以外は検査後ただちに破棄する。Frame Relation列のordinal `i`とdecode出力ordinal `i`を対応させ、先頭、中央、末尾のFrame markerだけをテスト専用expected sequenceと照合する。Productionは各Frameのbyte offset、seekまたはrandom decodeを保証せず、Smoke Testもそれらを要求しない。確定chunkのpath、length、hash、Run相関とFrame Relationは全件検査するが、未確定chunkをDecoderへ入力しない。

色とorientationのSmoke Testは1280×720固定の`NvencDecodeFixtureV1`を使う。Fixtureは異なる安全色の4領域と左上だけの非対称markerを持ち、上下／左右反転または90度回転を識別できることを必須とする。decode列の先頭、中央`floor((N-1)/2)`、末尾Frameだけを画像比較対象とし、各領域の境界から8 pixelを除外した平均RGB絶対誤差が各channel 24 code value以下で、markerが左上ROIに存在すれば成功とする。P99、gradient精度、詳細な色変換品質、PSNR／SSIMはPhase 0.11の完了条件にせず、必要ならPhase 4.8で測定する。Decoder identity、chunk Artifact、Frame数、寸法、比較ordinal、平均誤差、orientation、Artifact hashをテスト結果へ保存し、Capture Run ManifestまたはProduction Recovery schemaへ追加しない。

Phase 0.11はFrameごとのfile create／open／close、`.tmp`／rename、content hash、Artifact Registry Entry、Publication Plan Entryを禁止する。nominalではRun chunk 1件だけがこれらの対象となり、Phase 0.11ではchunk確定時を含め`Flush(true)`を要求・実行しない。この削減のために未確定chunkの細粒度Recovery状態機械を追加しない。120 Frame上限を外す、分単位の録画へ進む、またはbounded chunkを正式形式へ昇格する前に、Phase 4.8で正式chunk長、GOP／Container／segment、durability頻度、index／seek、保持期間、payload所有権、hash回数、停止時Publication時間を実測して正本化する。

Phase 0.11ではencoded bytesの決定性、canonical start code／NAL列、独自parserによるH.264完全適合証明、Annex A Level制約のアプリ内再計算、Recovery時のSPS／PPS／Slice意味解析、未確定chunkの部分修復、OS crash／電源断への完全durability、画質、bitrate、長時間安定性のSLAを保証しない。各入力FrameをIDRとしSPS／PPSを反復する要求設定は維持するが、P／B Frame、Frame間参照、PTS／DTS、正式GOP／Container／segment、index／seek、OpenXR Projection Swapchain直接Capture、AMF／QSVその他GPU vendor、Codec比較、zero-copy方式比較、Queue容量調整、詳細性能最適化、長時間または分単位の測定・テストは設計・実装・完了条件に含めず、Phase 4.8以降で実測して判断する。

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

- [Unity 6.3 Mesh.AllocateWritableMeshData](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Mesh.AllocateWritableMeshData.html)

- [Unity 6.3 Mesh.ApplyAndDisposeWritableMeshData](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Mesh.ApplyAndDisposeWritableMeshData.html)

- [Unity 6.3 PhysicsScene.Simulate](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/PhysicsScene.Simulate.html)

- [Unity 6.3 LocalPhysicsMode](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/SceneManagement.LocalPhysicsMode.html)

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
